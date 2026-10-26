-- =============================================================================
-- 064 — LOTES Y VENCIMIENTOS (e187, 25 oct 2026)
--
-- Inventario ampliado D4 (farmacias, DIGEMID, alimentos). En un producto con
-- «Maneja lotes», el stock de cada sucursal se reparte en lotes con su fecha
-- de vencimiento. El saldo total sigue en inv_stock (no cambia nada de lo de
-- antes); los lotes son el detalle:
--
--   · Entra por compra o stock inicial: al lote que se escribió.
--   · Sale (venta, guía, ajuste, conteo, traslado): primero lo que vence
--     antes (FEFO), solo.
--   · Se deshace (rechazo, baja, anulación): vuelve a los lotes de donde salió.
--   · Traslado: llega al destino con los mismos lotes que salieron.
--   · Nota de crédito: vuelve a los lotes del comprobante original.
--   · Lo que no tiene lote (stock de antes, sin lote) va al lote «» (sin lote).
--
-- Lo hace un disparador sobre inv_movimientos (el kardex): todo lo que mueve
-- stock ya pasa por ahí. inv_lote_mov guarda cada reparto (solo inserción).
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE productos ADD COLUMN IF NOT EXISTS maneja_lotes boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN productos.maneja_lotes IS '064: su stock se reparte en lotes con vencimiento (sale primero lo que vence antes).';

ALTER TABLE inv_operacion_lineas ADD COLUMN IF NOT EXISTS lote  text;
ALTER TABLE inv_operacion_lineas ADD COLUMN IF NOT EXISTS vence date;

CREATE TABLE IF NOT EXISTS inv_lotes (
    id           uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id   uuid          NOT NULL REFERENCES empresas (id),
    producto_id  uuid          NOT NULL REFERENCES productos (id),
    sucursal_id  uuid          NOT NULL REFERENCES sucursales (id),
    -- '' = sin lote (lo de antes, o lo que entró sin decir de qué lote).
    lote         text          NOT NULL DEFAULT '',
    vence        date,
    fisico       numeric(18,4) NOT NULL DEFAULT 0,
    creado_en    timestamptz   NOT NULL DEFAULT now(),
    actualizado_en timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT ck_inv_lotes_lote CHECK (length(lote) <= 40)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_lotes ON inv_lotes (producto_id, sucursal_id, lote);
CREATE INDEX IF NOT EXISTS ix_inv_lotes_vence ON inv_lotes (empresa_id, vence) WHERE fisico > 0;

COMMENT ON TABLE inv_lotes IS '064: el stock de un producto con lotes, por lote y sucursal. Lo escribe solo fn_inv_lotes.';

CREATE TABLE IF NOT EXISTS inv_lote_mov (
    id             uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id     uuid          NOT NULL REFERENCES empresas (id),
    lote_id        uuid          NOT NULL REFERENCES inv_lotes (id),
    movimiento_id  uuid          NOT NULL REFERENCES inv_movimientos (id),
    fuente_id      uuid          NOT NULL,
    cantidad       numeric(18,4) NOT NULL,
    creado_en      timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_inv_lote_mov_fuente ON inv_lote_mov (fuente_id);
CREATE INDEX IF NOT EXISTS ix_inv_lote_mov_lote   ON inv_lote_mov (lote_id);

COMMENT ON TABLE inv_lote_mov IS '064: cuánto de cada movimiento del kardex fue a (o salió de) cada lote. Solo inserción.';

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['inv_lotes', 'inv_lote_mov'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'p_' || t) THEN
            EXECUTE format(
                'CREATE POLICY %I ON %I USING (ctx_es_super() OR empresa_id = ctx_empresa()) ' ||
                'WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa())', 'p_' || t, t);
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_lote_mov'::regclass AND tgname = 'inmutable_inv_lote_mov') THEN
        CREATE TRIGGER inmutable_inv_lote_mov BEFORE UPDATE OR DELETE ON inv_lote_mov
            FOR EACH ROW EXECUTE FUNCTION fn_solo_insercion();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_lote_mov'::regclass AND tgname = 'auditar_inv_lote_mov') THEN
        CREATE TRIGGER auditar_inv_lote_mov AFTER UPDATE OR DELETE ON inv_lote_mov
            FOR EACH ROW EXECUTE FUNCTION fn_auditar();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_lotes'::regclass AND tgname = 'auditar_inv_lotes') THEN
        CREATE TRIGGER auditar_inv_lotes AFTER DELETE ON inv_lotes FOR EACH ROW EXECUTE FUNCTION fn_auditar();
    END IF;
END $$;

-- La aplicación solo los lee (los escribe la base).
GRANT SELECT ON inv_lotes, inv_lote_mov TO plataforma_app;

-- --- Repartir un movimiento del kardex en lotes -------------------------------------

CREATE OR REPLACE FUNCTION inv_lote_sumar(p_m inv_movimientos, p_lote text, p_vence date, p_q numeric) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_id uuid;
BEGIN
    IF COALESCE(p_q, 0) = 0 THEN RETURN; END IF;
    INSERT INTO inv_lotes AS x (empresa_id, producto_id, sucursal_id, lote, vence, fisico)
    VALUES (p_m.empresa_id, p_m.producto_id, p_m.sucursal_id, COALESCE(p_lote, ''), p_vence, p_q)
    ON CONFLICT (producto_id, sucursal_id, lote) DO UPDATE
        SET fisico = x.fisico + p_q, vence = COALESCE(x.vence, EXCLUDED.vence), actualizado_en = now()
    RETURNING id INTO v_id;
    INSERT INTO inv_lote_mov (empresa_id, lote_id, movimiento_id, fuente_id, cantidad)
    VALUES (p_m.empresa_id, v_id, p_m.id, p_m.fuente_id, p_q);
END $$;

CREATE OR REPLACE FUNCTION fn_inv_lotes() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_resto numeric;
    v_toma  numeric;
    v_ya    numeric;
    v_lote  text;
    v_src   uuid;
    r       record;
BEGIN
    IF NEW.cantidad = 0 OR NOT EXISTS (SELECT 1 FROM productos p WHERE p.id = NEW.producto_id AND p.maneja_lotes) THEN
        RETURN NULL;
    END IF;
    v_resto := abs(NEW.cantidad);

    -- 1) Deshacer lo que esta misma fuente hizo al revés aquí (rechazo, baja,
    --    anulación, la merma de un traslado recibido): a los mismos lotes.
    FOR r IN
        SELECT lo.lote, lo.vence, sum(a.cantidad) AS neto
          FROM inv_lote_mov a JOIN inv_lotes lo ON lo.id = a.lote_id
         WHERE a.fuente_id = NEW.fuente_id AND lo.producto_id = NEW.producto_id AND lo.sucursal_id = NEW.sucursal_id
           AND a.movimiento_id <> NEW.id
         GROUP BY lo.lote, lo.vence
        HAVING sign(sum(a.cantidad)) = -sign(NEW.cantidad)
         ORDER BY lo.vence DESC NULLS FIRST, lo.lote
    LOOP
        EXIT WHEN v_resto <= 0;
        v_toma := LEAST(v_resto, abs(r.neto));
        PERFORM inv_lote_sumar(NEW, r.lote, r.vence, sign(NEW.cantidad) * v_toma);
        v_resto := v_resto - v_toma;
    END LOOP;
    IF v_resto <= 0 THEN RETURN NULL; END IF;

    IF NEW.cantidad > 0 THEN
        -- 2) Compra o stock inicial: el lote que se escribió.
        v_lote := NULLIF(btrim(COALESCE(current_setting('inv.lote', true), '')), '');
        IF v_lote IS NOT NULL AND NEW.fuente = 'operacion' THEN
            PERFORM inv_lote_sumar(NEW, upper(v_lote), NULLIF(current_setting('inv.vence', true), '')::date, v_resto);
            RETURN NULL;
        END IF;

        -- 3) Un traslado llega con los lotes que salieron con su guía; una nota
        --    de crédito vuelve a los lotes del comprobante original.
        v_src := CASE WHEN NEW.fuente = 'traslado' THEN NEW.guia_id
                      WHEN NEW.fuente = 'documento' THEN (SELECT d.documento_ref_id FROM documentos d WHERE d.id = NEW.fuente_id) END;
        IF v_src IS NOT NULL THEN
            FOR r IN
                SELECT lo.lote, lo.vence, -sum(a.cantidad) AS salio
                  FROM inv_lote_mov a JOIN inv_lotes lo ON lo.id = a.lote_id
                 WHERE a.fuente_id = v_src AND lo.producto_id = NEW.producto_id
                 GROUP BY lo.lote, lo.vence HAVING sum(a.cantidad) < 0
                 ORDER BY lo.vence NULLS LAST, lo.lote
            LOOP
                SELECT COALESCE(sum(a.cantidad), 0) INTO v_ya
                  FROM inv_lote_mov a JOIN inv_lotes lo ON lo.id = a.lote_id
                 WHERE a.fuente_id = NEW.fuente_id AND lo.producto_id = NEW.producto_id AND lo.lote = r.lote
                   AND a.cantidad > 0 AND a.movimiento_id <> NEW.id;
                v_toma := LEAST(v_resto, r.salio - v_ya);
                CONTINUE WHEN v_toma <= 0;
                PERFORM inv_lote_sumar(NEW, r.lote, r.vence, v_toma);
                v_resto := v_resto - v_toma;
                EXIT WHEN v_resto <= 0;
            END LOOP;
        END IF;
        IF v_resto > 0 THEN PERFORM inv_lote_sumar(NEW, '', NULL, v_resto); END IF;
    ELSE
        -- 4) Sale: primero lo que vence antes (FEFO); lo sin lote, al final.
        FOR r IN
            SELECT lo.lote, lo.vence, lo.fisico
              FROM inv_lotes lo
             WHERE lo.producto_id = NEW.producto_id AND lo.sucursal_id = NEW.sucursal_id AND lo.fisico > 0
             ORDER BY lo.vence NULLS LAST, (lo.lote = '') , lo.creado_en
               FOR UPDATE
        LOOP
            v_toma := LEAST(v_resto, r.fisico);
            PERFORM inv_lote_sumar(NEW, r.lote, r.vence, -v_toma);
            v_resto := v_resto - v_toma;
            EXIT WHEN v_resto <= 0;
        END LOOP;
        IF v_resto > 0 THEN PERFORM inv_lote_sumar(NEW, '', NULL, -v_resto); END IF;
    END IF;
    RETURN NULL;
END $$;

CREATE OR REPLACE TRIGGER z_inv_lotes AFTER INSERT ON inv_movimientos
    FOR EACH ROW WHEN (NEW.cantidad <> 0) EXECUTE FUNCTION fn_inv_lotes();

-- Al encender «Maneja lotes», lo que ya había queda en «sin lote».
CREATE OR REPLACE FUNCTION fn_producto_lotes_encendido() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.maneja_lotes AND NOT OLD.maneja_lotes THEN
        INSERT INTO inv_lotes AS x (empresa_id, producto_id, sucursal_id, lote, fisico)
        SELECT s.empresa_id, s.producto_id, s.sucursal_id, '', s.fisico - COALESCE((
                   SELECT sum(l.fisico) FROM inv_lotes l WHERE l.producto_id = s.producto_id AND l.sucursal_id = s.sucursal_id AND l.lote <> ''), 0)
          FROM inv_stock s
         WHERE s.producto_id = NEW.id
        ON CONFLICT (producto_id, sucursal_id, lote) DO UPDATE SET fisico = EXCLUDED.fisico, actualizado_en = now();
    END IF;
    RETURN NULL;
END $$;

CREATE OR REPLACE TRIGGER z_producto_lotes AFTER UPDATE OF maneja_lotes ON productos
    FOR EACH ROW EXECUTE FUNCTION fn_producto_lotes_encendido();

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['inv_lote_sumar(inv_movimientos, text, date, numeric)', 'fn_inv_lotes()', 'fn_producto_lotes_encendido()'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
    END LOOP;
    EXECUTE 'REVOKE ALL ON FUNCTION inv_lote_sumar(inv_movimientos, text, date, numeric) FROM PUBLIC';
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'plataforma_app') THEN
        EXECUTE 'REVOKE ALL ON FUNCTION inv_lote_sumar(inv_movimientos, text, date, numeric) FROM plataforma_app';
    END IF;
END $$;

-- --- La compra y el stock inicial dicen el lote ----------------------------------------
-- (La misma inv_aplicar_operacion de la 052; solo pasa el lote de cada línea.)

CREATE OR REPLACE FUNCTION inv_aplicar_operacion(p_op uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    o          inv_operaciones%ROWTYPE;
    l          record;
    v_actual   numeric;
    v_estricto boolean;
    v_delta    numeric;
BEGIN
    SELECT * INTO o FROM inv_operaciones WHERE id = p_op FOR UPDATE;
    IF NOT FOUND OR (o.empresa_id IS DISTINCT FROM ctx_empresa() AND NOT ctx_es_super()) THEN
        RAISE EXCEPTION 'INV_NO_EXISTE';
    END IF;
    IF o.aplicada_en IS NOT NULL THEN RETURN; END IF;
    IF NOT inv_activo(o.empresa_id) THEN RAISE EXCEPTION 'INV_APAGADO'; END IF;

    v_estricto := inv_bloquea(o.empresa_id, o.sucursal_id);

    FOR l IN SELECT * FROM inv_operacion_lineas WHERE operacion_id = o.id AND activo ORDER BY producto_id LOOP
        -- 064: el lote y el vencimiento de la línea, para fn_inv_lotes (solo esta transacción).
        PERFORM set_config('inv.lote', COALESCE(l.lote, ''), true);
        PERFORM set_config('inv.vence', COALESCE(l.vence::text, ''), true);

        IF o.tipo = 'ingreso' THEN
            PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'compra', l.cantidad, 0,
                              'operacion', o.id, false, NULL, NULL, o.id, NULL, o.nota, l.costo_unitario, o.moneda);

        ELSIF o.tipo = 'ajuste' THEN
            PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'ajuste', l.cantidad, 0,
                              'operacion', o.id, v_estricto, NULL, NULL, o.id, o.motivo, o.nota);

        ELSIF o.tipo = 'stock_inicial' THEN
            IF EXISTS (SELECT 1 FROM inv_movimientos m
                        WHERE m.producto_id = l.producto_id AND m.sucursal_id = o.sucursal_id AND m.tipo = 'stock_inicial') THEN
                RAISE EXCEPTION 'INV_YA_INICIAL|%', l.producto_id;
            END IF;
            SELECT COALESCE(fisico, 0) INTO v_actual FROM inv_stock
             WHERE producto_id = l.producto_id AND sucursal_id = o.sucursal_id;
            v_delta := l.cantidad - COALESCE(v_actual, 0);
            UPDATE inv_operacion_lineas SET sistema = COALESCE(v_actual, 0) WHERE id = l.id;
            IF v_delta <> 0 THEN
                PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'stock_inicial', v_delta, 0,
                                  'operacion', o.id, false, NULL, NULL, o.id, NULL, o.nota, l.costo_unitario, o.moneda);
            ELSE
                -- En cero y ya en cero: igual queda constancia de que se cargó.
                INSERT INTO inv_movimientos (empresa_id, producto_id, sucursal_id, tipo, cantidad, reservado,
                                             saldo_fisico, saldo_reservado, fuente, fuente_id, operacion_id, usuario_id)
                VALUES (o.empresa_id, l.producto_id, o.sucursal_id, 'stock_inicial', 0, 0,
                        COALESCE(v_actual, 0),
                        COALESCE((SELECT reservado FROM inv_stock WHERE producto_id = l.producto_id AND sucursal_id = o.sucursal_id), 0),
                        'operacion', o.id, o.id, ctx_usuario());
            END IF;

        ELSIF o.tipo = 'conteo' THEN
            IF l.contado IS NULL THEN CONTINUE; END IF;
            SELECT COALESCE(fisico, 0) INTO v_actual FROM inv_stock
             WHERE producto_id = l.producto_id AND sucursal_id = o.sucursal_id;
            v_delta := l.contado - COALESCE(v_actual, 0);
            UPDATE inv_operacion_lineas SET sistema = COALESCE(v_actual, 0), cantidad = v_delta WHERE id = l.id;
            PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'conteo', v_delta, 0,
                              'operacion', o.id, false, NULL, NULL, o.id, NULL, 'diferencia de conteo');
        END IF;
    END LOOP;
    PERFORM set_config('inv.lote', '', true);
    PERFORM set_config('inv.vence', '', true);

    IF o.tipo = 'stock_inicial' THEN
        INSERT INTO inv_sucursales AS s (empresa_id, sucursal_id, stock_inicial_en)
        VALUES (o.empresa_id, o.sucursal_id, now())
        ON CONFLICT (sucursal_id) DO UPDATE SET stock_inicial_en = COALESCE(s.stock_inicial_en, now());
    END IF;

    UPDATE inv_operaciones
       SET aplicada_en = now(),
           estado = CASE WHEN tipo = 'conteo' THEN 'cerrado' ELSE estado END
     WHERE id = o.id;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('064_lotes')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '064: lotes y vencimientos (sale primero lo que vence antes). Nada se borró.';
END $$;