-- =============================================================================
-- 068 — NÚMEROS DE SERIE QUE VIAJAN Y LOTES QUE SE ELIGEN
--
--   · SERIES EN LOS TRASLADOS. La guía de un traslado entre sucursales lleva
--     los números que salen (guia_bienes.series). Al salir quedan «en camino»;
--     al recibir, pasan a la sucursal de destino. Lo que no llegó: si «se
--     envió de menos», vuelve a la de origen; si fue merma o pérdida, queda
--     «de baja». Si la guía se anula antes de recibir, vuelven a la de origen.
--   · LOTE AL VENDER. Una línea del comprobante puede llevar el lote
--     (documento_lineas.lote): sale primero de ese lote; lo que falte, como
--     siempre (lo que vence antes).
--   · STOCK INICIAL POR LOTES (y con series). El mismo producto puede ir en
--     varias líneas, una por lote; los números de serie que se escriban
--     entran como «en stock».
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================
BEGIN;

-- --- Columnas nuevas ---------------------------------------------------------------------------

ALTER TABLE guia_bienes      ADD COLUMN IF NOT EXISTS series text[];
ALTER TABLE documento_lineas ADD COLUMN IF NOT EXISTS lote   text;
COMMENT ON COLUMN guia_bienes.series IS '068: en un traslado entre sucursales, los números de serie que viajan.';
COMMENT ON COLUMN documento_lineas.lote IS '068: el lote del que se vende (null = el que vence primero).';

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_documento_lineas_lote') THEN
        ALTER TABLE documento_lineas ADD CONSTRAINT ck_documento_lineas_lote
            CHECK (lote IS NULL OR length(btrim(lote)) BETWEEN 1 AND 40);
    END IF;
END $$;

ALTER TABLE inv_series ADD COLUMN IF NOT EXISTS traslado_id uuid REFERENCES inv_traslados (id);
COMMENT ON COLUMN inv_series.traslado_id IS '068: el último traslado en el que viajó.';

-- Dos estados más: en camino (traslado sin recibir) y de baja (no llegó: merma o pérdida).
ALTER TABLE inv_series DROP CONSTRAINT IF EXISTS ck_inv_series_estado;
ALTER TABLE inv_series ADD CONSTRAINT ck_inv_series_estado
    CHECK (estado IN ('en_stock', 'vendida', 'anulada', 'en_transito', 'baja'));


-- --- Series: salen con la guía del traslado (o vuelven si se anula) ------------------------------

CREATE OR REPLACE FUNCTION fn_inv_series_traslado() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_tras uuid;
BEGIN
    IF NEW.fuente <> 'guia' THEN RETURN NULL; END IF;
    SELECT id INTO v_tras FROM inv_traslados WHERE guia_id = NEW.fuente_id;
    IF v_tras IS NULL THEN RETURN NULL; END IF;

    IF NEW.tipo = 'traslado_salida' THEN
        UPDATE inv_series s
           SET estado = 'en_transito', traslado_id = v_tras, actualizado_en = now()
         WHERE s.producto_id = NEW.producto_id AND s.sucursal_id = NEW.sucursal_id AND s.estado = 'en_stock'
           AND upper(s.serie) IN (SELECT upper(btrim(x))
                                    FROM guia_bienes b, unnest(b.series) AS x
                                   WHERE b.guia_id = NEW.fuente_id AND b.producto_id = NEW.producto_id AND b.activo);
    ELSIF NEW.tipo = 'traslado_revertido' THEN
        UPDATE inv_series s
           SET estado = 'en_stock', actualizado_en = now()
         WHERE s.traslado_id = v_tras AND s.producto_id = NEW.producto_id AND s.estado = 'en_transito';
    END IF;
    RETURN NULL;
END $$;

CREATE OR REPLACE TRIGGER z_inv_series_traslado AFTER INSERT ON inv_movimientos
    FOR EACH ROW WHEN (NEW.tipo IN ('traslado_salida', 'traslado_revertido'))
    EXECUTE FUNCTION fn_inv_series_traslado();


-- --- Series: al recibir el traslado ---------------------------------------------------------------
-- p_faltan: [{"productoId": "…", "serie": "ABC1", "motivo": "merma|perdida|enviado_menos"}]
-- Se llama después de inv_recibir_traslado, en la misma transacción.

CREATE OR REPLACE FUNCTION inv_series_recibir(p_traslado uuid, p_faltan jsonb) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    t   inv_traslados%ROWTYPE;
    f   record;
    n   integer;
BEGIN
    SELECT * INTO t FROM inv_traslados WHERE id = p_traslado;
    IF NOT FOUND OR (t.empresa_id IS DISTINCT FROM ctx_empresa() AND NOT ctx_es_super()) THEN
        RAISE EXCEPTION 'INV_NO_EXISTE';
    END IF;
    IF t.estado NOT IN ('recibido', 'con_diferencia') THEN RETURN 0; END IF;

    -- Lo que no llegó.
    FOR f IN SELECT (x ->> 'productoId')::uuid AS producto_id, upper(btrim(x ->> 'serie')) AS serie, x ->> 'motivo' AS motivo
               FROM jsonb_array_elements(COALESCE(p_faltan, '[]'::jsonb)) AS x
    LOOP
        UPDATE inv_series
           SET estado = CASE WHEN f.motivo = 'enviado_menos' THEN 'en_stock' ELSE 'baja' END,
               sucursal_id = t.origen_id, actualizado_en = now()
         WHERE traslado_id = t.id AND producto_id = f.producto_id AND upper(serie) = f.serie AND estado = 'en_transito';
    END LOOP;

    -- Lo demás llegó: queda en el destino.
    UPDATE inv_series
       SET estado = 'en_stock', sucursal_id = t.destino_id, actualizado_en = now()
     WHERE traslado_id = t.id AND estado = 'en_transito';
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n;
END $$;


-- --- Lotes: el que se eligió al vender, primero ---------------------------------------------------
-- (La misma fn_inv_lotes de la 064; solo cambia el paso 4.)

CREATE OR REPLACE FUNCTION fn_inv_lotes() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_resto numeric;
    v_toma  numeric;
    v_ya    numeric;
    v_hay   numeric;
    v_lote  text;
    v_src   uuid;
    v_doc   uuid;
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
        -- 4a) 068: la venta (o el despacho de una venta) con el lote elegido en
        --     la línea: primero de ese lote, hasta lo que pidió y lo que hay.
        v_doc := CASE WHEN NEW.fuente = 'documento' THEN NEW.fuente_id
                      WHEN NEW.fuente = 'guia' THEN NEW.documento_id END;
        IF v_doc IS NOT NULL THEN
            FOR r IN
                SELECT upper(btrim(l.lote)) AS lote, sum(l.cantidad * COALESCE(l.factor, 1)) AS pide
                  FROM documento_lineas l
                 WHERE l.documento_id = v_doc AND l.producto_id = NEW.producto_id AND l.activo AND l.lote IS NOT NULL
                 GROUP BY 1
                 ORDER BY 1
            LOOP
                EXIT WHEN v_resto <= 0;
                -- Lo que ya salió de ese lote por esta venta (otra guía, por ejemplo).
                SELECT COALESCE(-sum(a.cantidad), 0) INTO v_ya
                  FROM inv_lote_mov a
                  JOIN inv_lotes lo ON lo.id = a.lote_id
                  JOIN inv_movimientos m ON m.id = a.movimiento_id
                 WHERE lo.producto_id = NEW.producto_id AND lo.lote = r.lote AND m.id <> NEW.id
                   AND (m.fuente_id = v_doc OR m.documento_id = v_doc);
                SELECT GREATEST(fisico, 0) INTO v_hay FROM inv_lotes
                 WHERE producto_id = NEW.producto_id AND sucursal_id = NEW.sucursal_id AND lote = r.lote
                   FOR UPDATE;
                v_toma := LEAST(v_resto, r.pide - v_ya, COALESCE(v_hay, 0));
                CONTINUE WHEN v_toma <= 0;
                PERFORM inv_lote_sumar(NEW, r.lote,
                    (SELECT vence FROM inv_lotes WHERE producto_id = NEW.producto_id AND sucursal_id = NEW.sucursal_id AND lote = r.lote),
                    -v_toma);
                v_resto := v_resto - v_toma;
            END LOOP;
            IF v_resto <= 0 THEN RETURN NULL; END IF;
        END IF;

        -- 4b) Sale: primero lo que vence antes (FEFO); lo sin lote, al final.
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


-- --- Stock inicial: varias líneas del mismo producto (una por lote) y con series ------------------
-- (La misma inv_aplicar_operacion de la 066; cambia solo el stock inicial.)

CREATE OR REPLACE FUNCTION inv_aplicar_operacion(p_op uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    o          inv_operaciones%ROWTYPE;
    l          record;
    v_actual   numeric;
    v_estricto boolean;
    v_delta    numeric;
    v_segunda  boolean;
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

    FOR l IN SELECT * FROM inv_operacion_lineas WHERE operacion_id = o.id AND activo ORDER BY producto_id, creado_en, id LOOP
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
            -- 068: otra línea del mismo producto en ESTA carga (otro lote) suma.
            v_segunda := EXISTS (SELECT 1 FROM inv_movimientos m
                                  WHERE m.producto_id = l.producto_id AND m.sucursal_id = o.sucursal_id
                                    AND m.tipo = 'stock_inicial' AND m.operacion_id = o.id);
            IF NOT v_segunda AND EXISTS (SELECT 1 FROM inv_movimientos m
                        WHERE m.producto_id = l.producto_id AND m.sucursal_id = o.sucursal_id AND m.tipo = 'stock_inicial') THEN
                RAISE EXCEPTION 'INV_YA_INICIAL|%', l.producto_id;
            END IF;
            SELECT COALESCE(fisico, 0) INTO v_actual FROM inv_stock
             WHERE producto_id = l.producto_id AND sucursal_id = o.sucursal_id;
            v_delta := CASE WHEN v_segunda THEN l.cantidad ELSE l.cantidad - COALESCE(v_actual, 0) END;
            UPDATE inv_operacion_lineas SET sistema = COALESCE(v_actual, 0) WHERE id = l.id;
            IF v_delta <> 0 THEN
                PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'stock_inicial', v_delta, 0,
                                  'operacion', o.id, false, NULL, NULL, o.id, NULL, o.nota, l.costo_unitario, o.moneda);
            ELSIF NOT v_segunda THEN
                -- En cero y ya en cero: igual queda constancia de que se cargó.
                INSERT INTO inv_movimientos (empresa_id, producto_id, sucursal_id, tipo, cantidad, reservado,
                                             saldo_fisico, saldo_reservado, fuente, fuente_id, operacion_id, usuario_id)
                VALUES (o.empresa_id, l.producto_id, o.sucursal_id, 'stock_inicial', 0, 0,
                        COALESCE(v_actual, 0),
                        COALESCE((SELECT reservado FROM inv_stock WHERE producto_id = l.producto_id AND sucursal_id = o.sucursal_id), 0),
                        'operacion', o.id, o.id, ctx_usuario());
            END IF;
            -- 068: los números de serie de lo que ya hay.
            IF l.series IS NOT NULL AND cardinality(l.series) > 0 THEN
                PERFORM inv_series_entrar(o.empresa_id, l.producto_id, o.sucursal_id, l.series, o.id);
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


-- --- Dueños y permisos -------------------------------------------------------------------------

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['fn_inv_series_traslado()', 'inv_series_recibir(uuid, jsonb)', 'fn_inv_lotes()',
                             'inv_aplicar_operacion(uuid)'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('068_series_lotes_venta')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '068: series en los traslados, lote al vender, stock inicial por lotes. Nada se borró.';
END $$;