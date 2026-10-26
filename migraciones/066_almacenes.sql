-- =============================================================================
-- 066 — VARIOS ALMACENES EN UNA SUCURSAL (e189, 25 oct 2026)
--
-- Inventario ampliado D6 (locales grandes: tienda y depósito). Una sucursal
-- puede tener almacenes; su stock total sigue en inv_stock (no cambia nada de
-- lo de antes) y esto es el detalle de DÓNDE está:
--
--   · Al crear el primer almacén extra, nace el «principal» (la tienda, de
--     donde se vende) con todo lo que hay.
--   · La compra entra al almacén que se elija (o al principal).
--   · Vender, guías, ajustes: sale primero del principal y, si no alcanza, de
--     los demás en su orden. Deshacer vuelve a donde salió.
--   · «Mover entre almacenes» no cambia el stock de la sucursal: solo dónde.
--
-- Lo hace un disparador sobre inv_movimientos (como los lotes de la 064).
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS inv_almacenes (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    sucursal_id     uuid        NOT NULL REFERENCES sucursales (id),
    nombre          text        NOT NULL,
    principal       boolean     NOT NULL DEFAULT false,
    orden           integer     NOT NULL DEFAULT 1,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_inv_almacenes_nombre CHECK (length(btrim(nombre)) BETWEEN 1 AND 60)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_almacenes_nombre ON inv_almacenes (sucursal_id, lower(btrim(nombre))) WHERE activo;
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_almacenes_principal ON inv_almacenes (sucursal_id) WHERE principal AND activo;

COMMENT ON TABLE inv_almacenes IS '066: almacenes dentro de una sucursal (tienda, depósito). El principal es de donde se vende.';

CREATE TABLE IF NOT EXISTS inv_almacen_stock (
    id           uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id   uuid          NOT NULL REFERENCES empresas (id),
    almacen_id   uuid          NOT NULL REFERENCES inv_almacenes (id),
    producto_id  uuid          NOT NULL REFERENCES productos (id),
    fisico       numeric(18,4) NOT NULL DEFAULT 0,
    actualizado_en timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_almacen_stock ON inv_almacen_stock (almacen_id, producto_id);

CREATE TABLE IF NOT EXISTS inv_almacen_mov (
    id             uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id     uuid          NOT NULL REFERENCES empresas (id),
    almacen_id     uuid          NOT NULL REFERENCES inv_almacenes (id),
    producto_id    uuid          NOT NULL REFERENCES productos (id),
    movimiento_id  uuid          REFERENCES inv_movimientos (id),
    fuente_id      uuid          NOT NULL,
    cantidad       numeric(18,4) NOT NULL,
    nota           text,
    usuario_id     uuid,
    creado_en      timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_inv_almacen_mov_fuente ON inv_almacen_mov (fuente_id);
CREATE INDEX IF NOT EXISTS ix_inv_almacen_mov_almacen ON inv_almacen_mov (almacen_id, creado_en DESC);

COMMENT ON TABLE inv_almacen_stock IS '066: cuánto hay de cada producto en cada almacén. Lo escriben solo las funciones inv_almacen_*.';
COMMENT ON TABLE inv_almacen_mov IS '066: cada reparto o movimiento entre almacenes. Solo inserción.';

ALTER TABLE inv_operaciones ADD COLUMN IF NOT EXISTS almacen_id uuid REFERENCES inv_almacenes (id);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['inv_almacenes', 'inv_almacen_stock', 'inv_almacen_mov'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'p_' || t) THEN
            EXECUTE format(
                'CREATE POLICY %I ON %I USING (ctx_es_super() OR empresa_id = ctx_empresa()) ' ||
                'WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa())', 'p_' || t, t);
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_inv_almacenes') THEN
        PERFORM vigilar_tabla('inv_almacenes');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_almacen_mov'::regclass AND tgname = 'inmutable_inv_almacen_mov') THEN
        CREATE TRIGGER inmutable_inv_almacen_mov BEFORE UPDATE OR DELETE ON inv_almacen_mov
            FOR EACH ROW EXECUTE FUNCTION fn_solo_insercion();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_almacen_mov'::regclass AND tgname = 'auditar_inv_almacen_mov') THEN
        CREATE TRIGGER auditar_inv_almacen_mov AFTER UPDATE OR DELETE ON inv_almacen_mov FOR EACH ROW EXECUTE FUNCTION fn_auditar();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_almacen_stock'::regclass AND tgname = 'auditar_inv_almacen_stock') THEN
        CREATE TRIGGER auditar_inv_almacen_stock AFTER DELETE ON inv_almacen_stock FOR EACH ROW EXECUTE FUNCTION fn_auditar();
    END IF;
END $$;

-- La aplicación crea y renombra almacenes; el stock por almacén lo escribe la base.
GRANT SELECT, INSERT, UPDATE ON inv_almacenes TO plataforma_app;
GRANT SELECT ON inv_almacen_stock, inv_almacen_mov TO plataforma_app;

-- --- Sumar o restar en un almacén ------------------------------------------------------

CREATE OR REPLACE FUNCTION inv_almacen_sumar(p_empresa uuid, p_almacen uuid, p_producto uuid, p_q numeric,
                                             p_mov uuid, p_fuente uuid, p_nota text DEFAULT NULL) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF COALESCE(p_q, 0) = 0 THEN RETURN; END IF;
    INSERT INTO inv_almacen_stock AS x (empresa_id, almacen_id, producto_id, fisico)
    VALUES (p_empresa, p_almacen, p_producto, p_q)
    ON CONFLICT (almacen_id, producto_id) DO UPDATE SET fisico = x.fisico + p_q, actualizado_en = now();
    INSERT INTO inv_almacen_mov (empresa_id, almacen_id, producto_id, movimiento_id, fuente_id, cantidad, nota, usuario_id)
    VALUES (p_empresa, p_almacen, p_producto, p_mov, p_fuente, p_q, p_nota, ctx_usuario());
END $$;

-- Un movimiento del kardex, repartido entre los almacenes de su sucursal.
CREATE OR REPLACE FUNCTION fn_inv_almacenes() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_principal uuid;
    v_elegido   uuid;
    v_resto     numeric;
    v_toma      numeric;
    r           record;
BEGIN
    SELECT id INTO v_principal FROM inv_almacenes
     WHERE sucursal_id = NEW.sucursal_id AND principal AND activo;
    IF v_principal IS NULL THEN RETURN NULL; END IF;   -- la sucursal no tiene almacenes
    v_resto := abs(NEW.cantidad);

    -- 1) Deshacer lo que esta misma fuente hizo al revés: a los mismos almacenes.
    FOR r IN
        SELECT m.almacen_id, sum(m.cantidad) AS neto
          FROM inv_almacen_mov m JOIN inv_almacenes a ON a.id = m.almacen_id
         WHERE m.fuente_id = NEW.fuente_id AND m.producto_id = NEW.producto_id AND a.sucursal_id = NEW.sucursal_id
           AND m.movimiento_id IS DISTINCT FROM NEW.id
         GROUP BY m.almacen_id
        HAVING sign(sum(m.cantidad)) = -sign(NEW.cantidad)
    LOOP
        EXIT WHEN v_resto <= 0;
        v_toma := LEAST(v_resto, abs(r.neto));
        PERFORM inv_almacen_sumar(NEW.empresa_id, r.almacen_id, NEW.producto_id, sign(NEW.cantidad) * v_toma, NEW.id, NEW.fuente_id);
        v_resto := v_resto - v_toma;
    END LOOP;
    IF v_resto <= 0 THEN RETURN NULL; END IF;

    IF NEW.cantidad > 0 THEN
        -- 2) Entra al elegido (la compra) o al principal.
        v_elegido := NULLIF(current_setting('inv.almacen', true), '')::uuid;
        IF v_elegido IS NULL OR NEW.fuente <> 'operacion'
           OR NOT EXISTS (SELECT 1 FROM inv_almacenes WHERE id = v_elegido AND sucursal_id = NEW.sucursal_id AND activo) THEN
            v_elegido := v_principal;
        END IF;
        PERFORM inv_almacen_sumar(NEW.empresa_id, v_elegido, NEW.producto_id, v_resto, NEW.id, NEW.fuente_id);
    ELSE
        -- 3) Sale: primero del principal, luego de los demás en su orden.
        FOR r IN
            SELECT a.id, COALESCE(s.fisico, 0) AS fisico
              FROM inv_almacenes a
              LEFT JOIN inv_almacen_stock s ON s.almacen_id = a.id AND s.producto_id = NEW.producto_id
             WHERE a.sucursal_id = NEW.sucursal_id AND a.activo AND COALESCE(s.fisico, 0) > 0
             ORDER BY a.principal DESC, a.orden, a.creado_en
        LOOP
            v_toma := LEAST(v_resto, r.fisico);
            PERFORM inv_almacen_sumar(NEW.empresa_id, r.id, NEW.producto_id, -v_toma, NEW.id, NEW.fuente_id);
            v_resto := v_resto - v_toma;
            EXIT WHEN v_resto <= 0;
        END LOOP;
        IF v_resto > 0 THEN
            PERFORM inv_almacen_sumar(NEW.empresa_id, v_principal, NEW.producto_id, -v_resto, NEW.id, NEW.fuente_id);
        END IF;
    END IF;
    RETURN NULL;
END $$;

CREATE OR REPLACE TRIGGER z_inv_almacenes AFTER INSERT ON inv_movimientos
    FOR EACH ROW WHEN (NEW.cantidad <> 0) EXECUTE FUNCTION fn_inv_almacenes();

-- Crear un almacén. El primero extra de una sucursal hace nacer el principal
-- (con el nombre que se diga) con todo lo que hay hoy en la sucursal.
CREATE OR REPLACE FUNCTION inv_almacen_crear(p_sucursal uuid, p_nombre text, p_principal_nombre text DEFAULT 'TIENDA') RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_emp  uuid;
    v_pri  uuid;
    v_id   uuid;
    v_fuente uuid := gen_random_uuid();
BEGIN
    SELECT empresa_id INTO v_emp FROM sucursales WHERE id = p_sucursal AND eliminada_en IS NULL;
    IF v_emp IS NULL OR (v_emp IS DISTINCT FROM ctx_empresa() AND NOT ctx_es_super()) THEN
        RAISE EXCEPTION 'INV_NO_EXISTE';
    END IF;
    SELECT id INTO v_pri FROM inv_almacenes WHERE sucursal_id = p_sucursal AND principal AND activo;
    IF v_pri IS NULL THEN
        INSERT INTO inv_almacenes (empresa_id, sucursal_id, nombre, principal, orden)
        VALUES (v_emp, p_sucursal, upper(btrim(COALESCE(NULLIF(btrim(p_principal_nombre), ''), 'TIENDA'))), true, 0)
        RETURNING id INTO v_pri;
        INSERT INTO inv_almacen_stock (empresa_id, almacen_id, producto_id, fisico)
        SELECT s.empresa_id, v_pri, s.producto_id, s.fisico FROM inv_stock s WHERE s.sucursal_id = p_sucursal AND s.fisico <> 0;
        INSERT INTO inv_almacen_mov (empresa_id, almacen_id, producto_id, fuente_id, cantidad, nota, usuario_id)
        SELECT s.empresa_id, v_pri, s.producto_id, v_fuente, s.fisico, 'lo que había al crear los almacenes', ctx_usuario()
          FROM inv_stock s WHERE s.sucursal_id = p_sucursal AND s.fisico <> 0;
    END IF;
    INSERT INTO inv_almacenes (empresa_id, sucursal_id, nombre, principal, orden)
    VALUES (v_emp, p_sucursal, upper(btrim(p_nombre)), false,
            COALESCE((SELECT max(orden) FROM inv_almacenes WHERE sucursal_id = p_sucursal), 0) + 1)
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- Mover entre almacenes de la misma sucursal: el stock de la sucursal no cambia.
CREATE OR REPLACE FUNCTION inv_almacen_mover(p_producto uuid, p_origen uuid, p_destino uuid, p_cantidad numeric, p_nota text)
RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    o   inv_almacenes%ROWTYPE;
    d   inv_almacenes%ROWTYPE;
    v_hay numeric;
    v_fuente uuid := gen_random_uuid();
BEGIN
    SELECT * INTO o FROM inv_almacenes WHERE id = p_origen AND activo;
    SELECT * INTO d FROM inv_almacenes WHERE id = p_destino AND activo;
    IF o.id IS NULL OR d.id IS NULL OR o.sucursal_id <> d.sucursal_id OR o.id = d.id
       OR (o.empresa_id IS DISTINCT FROM ctx_empresa() AND NOT ctx_es_super()) THEN
        RAISE EXCEPTION 'INV_ALMACEN_MAL';
    END IF;
    IF COALESCE(p_cantidad, 0) <= 0 THEN RAISE EXCEPTION 'INV_ALMACEN_CANTIDAD'; END IF;
    SELECT COALESCE(fisico, 0) INTO v_hay FROM inv_almacen_stock WHERE almacen_id = o.id AND producto_id = p_producto FOR UPDATE;
    IF COALESCE(v_hay, 0) < p_cantidad THEN
        RAISE EXCEPTION 'INV_ALMACEN_NO_ALCANZA|%|%', p_producto, COALESCE(v_hay, 0);
    END IF;
    PERFORM inv_almacen_sumar(o.empresa_id, o.id, p_producto, -p_cantidad, NULL, v_fuente, COALESCE(p_nota, 'a ' || d.nombre));
    PERFORM inv_almacen_sumar(o.empresa_id, d.id, p_producto, p_cantidad, NULL, v_fuente, COALESCE(p_nota, 'de ' || o.nombre));
END $$;

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['inv_almacen_sumar(uuid, uuid, uuid, numeric, uuid, uuid, text)', 'fn_inv_almacenes()',
                             'inv_almacen_crear(uuid, text, text)', 'inv_almacen_mover(uuid, uuid, uuid, numeric, text)'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
    END LOOP;
    EXECUTE 'REVOKE ALL ON FUNCTION inv_almacen_sumar(uuid, uuid, uuid, numeric, uuid, uuid, text) FROM PUBLIC';
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'plataforma_app') THEN
        EXECUTE 'REVOKE ALL ON FUNCTION inv_almacen_sumar(uuid, uuid, uuid, numeric, uuid, uuid, text) FROM plataforma_app';
        EXECUTE 'GRANT EXECUTE ON FUNCTION inv_almacen_crear(uuid, text, text), inv_almacen_mover(uuid, uuid, uuid, numeric, text) TO plataforma_app';
    END IF;
END $$;

-- --- La compra entra al almacén elegido ---------------------------------------------
-- (La inv_aplicar_operacion de la 065; además, el almacén de la operación.)

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
    -- 066: el almacén de la sucursal al que entra la compra (si tiene varios).
    PERFORM set_config('inv.almacen', COALESCE(o.almacen_id::text, ''), true);

    FOR l IN SELECT * FROM inv_operacion_lineas WHERE operacion_id = o.id AND activo ORDER BY producto_id LOOP
        -- 064: el lote y el vencimiento de la línea, para fn_inv_lotes (solo esta transacción).
        PERFORM set_config('inv.lote', COALESCE(l.lote, ''), true);
        PERFORM set_config('inv.vence', COALESCE(l.vence::text, ''), true);

        IF o.tipo = 'ingreso' THEN
            PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'compra', l.cantidad, 0,
                              'operacion', o.id, false, NULL, NULL, o.id, NULL, o.nota, l.costo_unitario, o.moneda);
            -- 065: los números de serie que llegaron.
            IF l.series IS NOT NULL AND cardinality(l.series) > 0 THEN
                PERFORM inv_series_entrar(o.empresa_id, l.producto_id, o.sucursal_id, l.series, o.id);
            END IF;

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
    PERFORM set_config('inv.almacen', '', true);

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

INSERT INTO migraciones_aplicadas (nombre) VALUES ('066_almacenes')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '066: varios almacenes en una sucursal (tienda y depósito). Nada se borró.';
END $$;