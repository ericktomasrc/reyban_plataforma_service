-- =============================================================================
-- 065 — NÚMEROS DE SERIE (e188, 25 oct 2026)
--
-- Inventario ampliado D5 (celulares, motores, electrodomésticos). En un
-- producto con «Maneja series», cada unidad tiene su número:
--
--   · Entra por compra con sus números (tantos como la cantidad).
--   · Se vende eligiendo cuáles (factura, boleta): al emitir quedan
--     «vendida» con su comprobante; si SUNAT la rechaza o se da de baja,
--     vuelven a «en stock». Una nota de crédito con esos números los devuelve.
--   · Anular la compra los da de baja («anulada»).
--   · Se puede buscar un número: qué producto es, dónde está, a quién se
--     vendió (garantías).
--
-- El stock (inv_stock, kardex, lotes) no cambia: esto es el detalle por
-- unidad. Hoy no siguen a los traslados (quedan en la sucursal de compra).
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE productos ADD COLUMN IF NOT EXISTS maneja_series boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN productos.maneja_series IS '065: cada unidad tiene su número de serie (se escribe al comprar y se elige al vender).';

ALTER TABLE inv_operacion_lineas ADD COLUMN IF NOT EXISTS series text[];
ALTER TABLE documento_lineas     ADD COLUMN IF NOT EXISTS series text[];

CREATE TABLE IF NOT EXISTS inv_series (
    id            uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id    uuid        NOT NULL REFERENCES empresas (id),
    producto_id   uuid        NOT NULL REFERENCES productos (id),
    serie         text        NOT NULL,
    sucursal_id   uuid        NOT NULL REFERENCES sucursales (id),
    estado        text        NOT NULL DEFAULT 'en_stock',
    ingreso_id    uuid        REFERENCES inv_operaciones (id),
    documento_id  uuid        REFERENCES documentos (id),
    vendida_en    timestamptz,
    actualizado_en timestamptz NOT NULL DEFAULT now(),

    creado_en     timestamptz NOT NULL DEFAULT now(),
    creado_por    uuid,
    modificado_en timestamptz,
    modificado_por uuid,
    activo        boolean     NOT NULL DEFAULT true,
    version       integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_inv_series_estado CHECK (estado IN ('en_stock', 'vendida', 'anulada')),
    CONSTRAINT ck_inv_series_serie  CHECK (length(btrim(serie)) BETWEEN 1 AND 60)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_series ON inv_series (producto_id, upper(serie)) WHERE estado <> 'anulada';
CREATE INDEX IF NOT EXISTS ix_inv_series_buscar ON inv_series (empresa_id, upper(serie));
CREATE INDEX IF NOT EXISTS ix_inv_series_documento ON inv_series (documento_id) WHERE documento_id IS NOT NULL;

COMMENT ON TABLE inv_series IS '065: cada unidad de un producto con series: dónde está y a quién se vendió. Lo escriben las funciones inv_series_*.';

ALTER TABLE inv_series ENABLE ROW LEVEL SECURITY;
ALTER TABLE inv_series FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_inv_series ON inv_series;
CREATE POLICY p_inv_series ON inv_series
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_inv_series') THEN
        PERFORM vigilar_tabla('inv_series');
    END IF;
END $$;
GRANT SELECT ON inv_series TO plataforma_app;

-- Entran (compra). Un número que ya está (y no anulado) no entra dos veces.
CREATE OR REPLACE FUNCTION inv_series_entrar(p_empresa uuid, p_producto uuid, p_sucursal uuid, p_series text[], p_ingreso uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v text;
BEGIN
    FOREACH v IN ARRAY p_series LOOP
        CONTINUE WHEN btrim(COALESCE(v, '')) = '';
        IF EXISTS (SELECT 1 FROM inv_series s WHERE s.producto_id = p_producto AND upper(s.serie) = upper(btrim(v)) AND s.estado <> 'anulada') THEN
            RAISE EXCEPTION 'INV_SERIE_REPETIDA|%|%', p_producto, upper(btrim(v)) USING ERRCODE = 'P0001';
        END IF;
        INSERT INTO inv_series (empresa_id, producto_id, serie, sucursal_id, ingreso_id)
        VALUES (p_empresa, p_producto, upper(btrim(v)), p_sucursal, p_ingreso);
    END LOOP;
END $$;

-- Un comprobante y sus números: vendidos al emitir (o al aceptarse si
-- llegó directo), de vuelta si se rechaza o se da de baja. La nota de
-- crédito aceptada los devuelve a «en stock».
CREATE OR REPLACE FUNCTION inv_series_documento(p_doc uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    d       documentos%ROWTYPE;
    v_quiere boolean;
    v_suc   uuid;
    r       record;
    v       text;
    v_id    uuid;
BEGIN
    SELECT * INTO d FROM documentos WHERE id = p_doc;
    IF NOT FOUND OR d.tipo NOT IN ('01', '03', '07') THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM documento_lineas l WHERE l.documento_id = d.id AND l.activo AND cardinality(l.series) > 0) THEN
        RETURN;
    END IF;

    IF d.tipo = '07' THEN
        v_quiere := d.activo AND d.estado IN ('aceptado', 'observado');
        FOR r IN SELECT l.producto_id, l.series FROM documento_lineas l
                  WHERE l.documento_id = d.id AND l.activo AND cardinality(l.series) > 0 LOOP
            FOREACH v IN ARRAY r.series LOOP
                IF v_quiere THEN
                    UPDATE inv_series SET estado = 'en_stock', documento_id = NULL, vendida_en = NULL, actualizado_en = now()
                     WHERE producto_id = r.producto_id AND upper(serie) = upper(btrim(v)) AND estado = 'vendida'
                       AND documento_id = d.documento_ref_id;
                ELSE
                    UPDATE inv_series SET estado = 'vendida', documento_id = d.documento_ref_id, vendida_en = now(), actualizado_en = now()
                     WHERE producto_id = r.producto_id AND upper(serie) = upper(btrim(v)) AND estado = 'en_stock'
                       AND EXISTS (SELECT 1 FROM documento_lineas x WHERE x.documento_id = d.documento_ref_id AND upper(btrim(v)) = ANY (x.series));
                END IF;
            END LOOP;
        END LOOP;
        RETURN;
    END IF;

    v_quiere := d.activo AND d.estado IN ('encolado', 'aceptado', 'observado');
    v_suc := COALESCE(d.sucursal_id, inv_principal(d.empresa_id));

    IF v_quiere THEN
        FOR r IN SELECT l.producto_id, l.series FROM documento_lineas l
                  WHERE l.documento_id = d.id AND l.activo AND cardinality(l.series) > 0 LOOP
            FOREACH v IN ARRAY r.series LOOP
                SELECT id INTO v_id FROM inv_series
                 WHERE producto_id = r.producto_id AND upper(serie) = upper(btrim(v)) AND estado <> 'anulada'
                 FOR UPDATE;
                IF v_id IS NULL THEN
                    -- Solo bloquea al emitir: el aviso del servicio nunca falla.
                    IF d.estado = 'encolado' THEN
                        RAISE EXCEPTION 'INV_SERIE_NO_EXISTE|%|%', r.producto_id, upper(btrim(v)) USING ERRCODE = 'P0001';
                    END IF;
                    CONTINUE;
                END IF;
                UPDATE inv_series SET estado = 'vendida', documento_id = d.id, vendida_en = COALESCE(vendida_en, now()), actualizado_en = now()
                 WHERE id = v_id AND (estado = 'en_stock' OR documento_id = d.id)
                RETURNING id INTO v_id;
                IF v_id IS NULL AND d.estado = 'encolado' THEN
                    RAISE EXCEPTION 'INV_SERIE_VENDIDA|%|%', r.producto_id, upper(btrim(v)) USING ERRCODE = 'P0001';
                END IF;
            END LOOP;
        END LOOP;
    ELSE
        UPDATE inv_series SET estado = 'en_stock', documento_id = NULL, vendida_en = NULL, actualizado_en = now()
         WHERE documento_id = d.id AND estado = 'vendida';
    END IF;
END $$;

CREATE OR REPLACE FUNCTION fn_inv_series_documento() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    PERFORM inv_series_documento(NEW.id);
    RETURN NULL;
END $$;

CREATE OR REPLACE TRIGGER z_inv_series_documento AFTER UPDATE OF estado, activo ON documentos
    FOR EACH ROW WHEN (OLD.estado IS DISTINCT FROM NEW.estado OR OLD.activo IS DISTINCT FROM NEW.activo)
    EXECUTE FUNCTION fn_inv_series_documento();

-- Anular la compra: sus números se dan de baja (si siguen en stock).
CREATE OR REPLACE FUNCTION fn_inv_series_operacion() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.estado = 'anulada' AND OLD.estado <> 'anulada' THEN
        UPDATE inv_series SET estado = 'anulada', actualizado_en = now()
         WHERE ingreso_id = NEW.id AND estado = 'en_stock';
    END IF;
    RETURN NULL;
END $$;

CREATE OR REPLACE TRIGGER z_inv_series_operacion AFTER UPDATE OF estado ON inv_operaciones
    FOR EACH ROW EXECUTE FUNCTION fn_inv_series_operacion();

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['inv_series_entrar(uuid, uuid, uuid, text[], uuid)', 'inv_series_documento(uuid)',
                             'fn_inv_series_documento()', 'fn_inv_series_operacion()'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
    END LOOP;
    FOREACH f IN ARRAY ARRAY['inv_series_entrar(uuid, uuid, uuid, text[], uuid)', 'inv_series_documento(uuid)'] LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'plataforma_app') THEN
            EXECUTE format('REVOKE ALL ON FUNCTION %s FROM plataforma_app', f);
        END IF;
    END LOOP;
END $$;

-- --- La compra registra sus números ----------------------------------------------------
-- (La inv_aplicar_operacion de la 064; además, las series de cada línea.)

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

INSERT INTO migraciones_aplicadas (nombre) VALUES ('065_series')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '065: números de serie (compra, venta, devolución, búsqueda). Nada se borró.';
END $$;