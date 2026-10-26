-- =============================================================================
-- 061 — PRESENTACIONES: UNIDAD DE COMPRA DISTINTA A LA DE VENTA (e184, 25 oct 2026)
--
-- Inventario ampliado D1. Un producto se cuenta SIEMPRE en su unidad (la del
-- producto: NIU, KGM…). Una presentación es otra forma de comprarlo o
-- venderlo: «CAJA X 12» (BX) = 12 unidades, «SACO 50 KG» = 50 KGM.
--
--   · producto_presentaciones: nombre, unidad SUNAT, cuántas unidades del
--     producto trae (factor), precio propio opcional y código de barras.
--   · documento_lineas y guia_bienes: presentacion_id y factor. La línea va a
--     SUNAT tal cual (1 BX a S/ 300); el stock se mueve en unidades (12).
--     El factor lo pone la BASE (disparador), nunca la pantalla: así una nota
--     de crédito o una guía que copian la línea no pueden equivocarse.
--   · inv_operacion_lineas: el ingreso por compra guarda lo que se escribió
--     (10 cajas a S/ 120) y la cantidad y el costo en unidades (120 a S/ 10).
--   · inv_sync_documento e inv_sync_guia: multiplican por el factor.
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS producto_presentaciones (
    id              uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid          NOT NULL REFERENCES empresas (id),
    producto_id     uuid          NOT NULL REFERENCES productos (id),
    nombre          text          NOT NULL,
    unidad_medida   text          NOT NULL DEFAULT 'BX',
    -- Cuántas unidades del producto trae una presentación.
    factor          numeric(18,4) NOT NULL,
    -- Precio de venta propio (opcional). Sin él: precio del producto × factor.
    precio_unitario numeric(18,10),
    incluye_igv     boolean       NOT NULL DEFAULT true,
    codigo_barras   text,
    para_compra     boolean       NOT NULL DEFAULT true,
    para_venta      boolean       NOT NULL DEFAULT true,

    creado_en       timestamptz   NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean       NOT NULL DEFAULT true,
    version         integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_presentaciones_nombre CHECK (length(btrim(nombre)) BETWEEN 1 AND 60),
    CONSTRAINT ck_presentaciones_factor CHECK (factor > 0),
    CONSTRAINT ck_presentaciones_precio CHECK (precio_unitario IS NULL OR precio_unitario > 0),
    CONSTRAINT ck_presentaciones_uso    CHECK (para_compra OR para_venta)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_presentaciones_nombre
    ON producto_presentaciones (producto_id, lower(btrim(nombre))) WHERE activo;
CREATE UNIQUE INDEX IF NOT EXISTS ux_presentaciones_barras
    ON producto_presentaciones (empresa_id, codigo_barras) WHERE activo AND codigo_barras IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_presentaciones_producto ON producto_presentaciones (producto_id);

COMMENT ON TABLE producto_presentaciones IS '061: otra forma de comprar o vender un producto (CAJA X 12 = 12 unidades). El stock se cuenta en la unidad del producto.';

ALTER TABLE producto_presentaciones ENABLE ROW LEVEL SECURITY;
ALTER TABLE producto_presentaciones FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_producto_presentaciones ON producto_presentaciones;
CREATE POLICY p_producto_presentaciones ON producto_presentaciones
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_producto_presentaciones') THEN
        PERFORM vigilar_tabla('producto_presentaciones');
    END IF;
END $$;

-- 056: el nombre, en mayúsculas.
CREATE OR REPLACE TRIGGER a_mayusculas_producto_presentaciones BEFORE INSERT OR UPDATE ON producto_presentaciones
    FOR EACH ROW EXECUTE FUNCTION fn_mayusculas('nombre');

GRANT SELECT, INSERT, UPDATE ON producto_presentaciones TO plataforma_app;

-- --- Las líneas ------------------------------------------------------------------

ALTER TABLE documento_lineas     ADD COLUMN IF NOT EXISTS presentacion_id uuid REFERENCES producto_presentaciones (id);
ALTER TABLE documento_lineas     ADD COLUMN IF NOT EXISTS factor numeric(18,4);
ALTER TABLE guia_bienes          ADD COLUMN IF NOT EXISTS presentacion_id uuid REFERENCES producto_presentaciones (id);
ALTER TABLE guia_bienes          ADD COLUMN IF NOT EXISTS factor numeric(18,4);
ALTER TABLE inv_operacion_lineas ADD COLUMN IF NOT EXISTS presentacion_id uuid REFERENCES producto_presentaciones (id);
ALTER TABLE inv_operacion_lineas ADD COLUMN IF NOT EXISTS factor numeric(18,4);
ALTER TABLE inv_operacion_lineas ADD COLUMN IF NOT EXISTS cantidad_presentacion numeric(18,4);
ALTER TABLE inv_operacion_lineas ADD COLUMN IF NOT EXISTS costo_presentacion numeric(18,6);

COMMENT ON COLUMN documento_lineas.factor IS '061: unidades del producto por cada unidad de la línea (lo pone la base desde presentacion_id). Null = 1.';
COMMENT ON COLUMN guia_bienes.factor IS '061: unidades del producto por cada unidad del bien (de su presentación o de la línea de la factura). Null = 1.';

-- El factor lo pone la base. Una presentación de OTRO producto no vale: se
-- quita (la línea queda en la unidad del producto).
CREATE OR REPLACE FUNCTION fn_factor_documento_linea() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.presentacion_id IS NULL THEN
        NEW.factor := NULL;
    ELSE
        SELECT p.factor INTO NEW.factor
          FROM producto_presentaciones p
         WHERE p.id = NEW.presentacion_id AND p.producto_id IS NOT DISTINCT FROM NEW.producto_id;
        IF NEW.factor IS NULL THEN NEW.presentacion_id := NULL; END IF;
    END IF;
    RETURN NEW;
END $$;

CREATE OR REPLACE TRIGGER b_factor_documento_lineas BEFORE INSERT OR UPDATE OF presentacion_id, producto_id, factor ON documento_lineas
    FOR EACH ROW EXECUTE FUNCTION fn_factor_documento_linea();

-- El bien de una guía: si sale de una línea de factura, lleva su presentación.
CREATE OR REPLACE FUNCTION fn_factor_guia_bien() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.documento_linea_id IS NOT NULL THEN
        SELECT l.presentacion_id INTO NEW.presentacion_id FROM documento_lineas l WHERE l.id = NEW.documento_linea_id;
    END IF;
    IF NEW.presentacion_id IS NULL THEN
        NEW.factor := NULL;
    ELSE
        SELECT p.factor INTO NEW.factor
          FROM producto_presentaciones p
         WHERE p.id = NEW.presentacion_id AND p.producto_id IS NOT DISTINCT FROM NEW.producto_id;
        IF NEW.factor IS NULL THEN NEW.presentacion_id := NULL; END IF;
    END IF;
    RETURN NEW;
END $$;

CREATE OR REPLACE TRIGGER b_factor_guia_bienes BEFORE INSERT OR UPDATE OF presentacion_id, producto_id, documento_linea_id, factor ON guia_bienes
    FOR EACH ROW EXECUTE FUNCTION fn_factor_guia_bien();

-- --- El stock, en unidades del producto ---------------------------------------------
-- (Las mismas funciones de la 052; solo cambia la suma: cantidad × factor.)

CREATE OR REPLACE FUNCTION inv_sync_documento(p_doc uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    d          documentos%ROWTYPE;
    a          inv_aplicado%ROWTYPE;
    v_cfg      inventario_config%ROWTYPE;
    v_quiere   boolean;
    v_modo     text;
    v_suc      uuid;
    v_estricto boolean;
    r          record;
    v_pend     numeric;
    v_toma     numeric;
    v_resto    numeric;
BEGIN
    SELECT * INTO d FROM documentos WHERE id = p_doc;
    IF NOT FOUND OR d.tipo NOT IN ('01', '03', '07') THEN RETURN; END IF;

    IF d.tipo = '07' THEN
        v_quiere := d.activo AND d.estado IN ('aceptado', 'observado') AND d.tipo_nota IN ('01', '02', '06', '07');
    ELSE
        v_quiere := d.activo AND d.estado IN ('encolado', 'aceptado', 'observado');
    END IF;

    SELECT * INTO a FROM inv_aplicado WHERE fuente_id = p_doc FOR UPDATE;

    IF NOT FOUND THEN
        IF NOT v_quiere THEN RETURN; END IF;
        SELECT * INTO v_cfg FROM inventario_config WHERE empresa_id = d.empresa_id;
        IF NOT FOUND OR NOT v_cfg.activo_inventario THEN RETURN; END IF;

        v_suc := COALESCE(d.sucursal_id, inv_principal(d.empresa_id));
        IF v_suc IS NULL THEN RETURN; END IF;
        IF d.tipo = '07' THEN
            v_modo := 'nota';
            v_suc := COALESCE(d.inv_sucursal_id, v_suc);
        ELSIF COALESCE(d.entrega_despues, v_cfg.modo = 'despacho') THEN
            v_modo := 'reserva';
        ELSE
            v_modo := 'salida';
        END IF;

        INSERT INTO inv_aplicado (fuente_id, fuente, empresa_id, modo, sucursal_id, aplicado)
        VALUES (p_doc, 'documento', d.empresa_id, v_modo, v_suc, false)
        RETURNING * INTO a;
    END IF;

    IF v_quiere = a.aplicado THEN RETURN; END IF;

    IF v_quiere THEN
        -- Si el inventario se apagó entretanto, no se mueve nada nuevo.
        IF NOT inv_activo(d.empresa_id) THEN RETURN; END IF;
        v_estricto := d.estado = 'encolado' AND inv_bloquea(d.empresa_id, a.sucursal_id);

        FOR r IN
            SELECT l.producto_id, sum(l.cantidad * COALESCE(l.factor, 1)) AS q   -- 061: en unidades del producto
              FROM documento_lineas l
              JOIN productos p ON p.id = l.producto_id
             WHERE l.documento_id = d.id AND l.activo AND p.controla_stock AND l.cantidad > 0
             GROUP BY l.producto_id
             ORDER BY l.producto_id     -- siempre el mismo orden: sin cruces de candados
        LOOP
            IF a.modo = 'salida' THEN
                PERFORM inv_mover(d.empresa_id, r.producto_id, a.sucursal_id, 'venta', -r.q, 0,
                                  'documento', d.id, v_estricto, d.id);

            ELSIF a.modo = 'reserva' THEN
                -- Si se vuelve a aplicar (raro: llegó «aceptado» después del
                -- rechazo), no se reserva lo que ya despacharon sus guías.
                SELECT COALESCE(sum(despachada), 0) INTO v_pend
                  FROM inv_reservas WHERE documento_id = d.id AND producto_id = r.producto_id;
                v_toma := GREATEST(r.q - v_pend, 0);
                INSERT INTO inv_reservas AS x (empresa_id, documento_id, producto_id, sucursal_id, reservada)
                VALUES (d.empresa_id, d.id, r.producto_id, a.sucursal_id, v_toma)
                ON CONFLICT (documento_id, producto_id, sucursal_id) DO UPDATE
                    SET reservada = x.reservada + v_toma, actualizado_en = now();
                PERFORM inv_mover(d.empresa_id, r.producto_id, a.sucursal_id, 'reserva', 0, v_toma,
                                  'documento', d.id, v_estricto, d.id);

            ELSE   -- nota de crédito
                v_toma := 0;
                FOR v_pend, v_suc IN
                    SELECT x.reservada - x.despachada - x.liberada, x.sucursal_id
                      FROM inv_reservas x
                     WHERE x.documento_id = d.documento_ref_id AND x.producto_id = r.producto_id
                       AND x.reservada - x.despachada - x.liberada > 0
                LOOP
                    v_resto := LEAST(r.q - v_toma, v_pend);
                    EXIT WHEN v_resto <= 0;
                    UPDATE inv_reservas SET liberada = liberada + v_resto, actualizado_en = now()
                     WHERE documento_id = d.documento_ref_id AND producto_id = r.producto_id AND sucursal_id = v_suc;
                    PERFORM inv_mover(d.empresa_id, r.producto_id, v_suc, 'reserva_liberada', 0, -v_resto,
                                      'documento', d.id, false, d.documento_ref_id, NULL, NULL, NULL, 'nota de crédito');
                    v_toma := v_toma + v_resto;
                END LOOP;

                v_resto := r.q - v_toma;
                IF v_resto > 0 AND COALESCE(d.inv_retorno, 'volvio') IN ('volvio', 'danada') THEN
                    PERFORM inv_mover(d.empresa_id, r.producto_id, a.sucursal_id, 'devolucion', v_resto, 0,
                                      'documento', d.id, false, d.id);
                    IF d.inv_retorno = 'danada' THEN
                        PERFORM inv_mover(d.empresa_id, r.producto_id, a.sucursal_id, 'ajuste', -v_resto, 0,
                                          'documento', d.id, false, d.id, NULL, NULL, 'merma', 'volvió dañada');
                    END IF;
                END IF;
            END IF;
        END LOOP;

        UPDATE inv_aplicado SET aplicado = true, actualizado_en = now() WHERE fuente_id = p_doc;

    ELSE
        -- REVERTIR.
        IF a.modo = 'reserva' THEN
            -- Se libera lo que faltaba despachar.
            FOR r IN SELECT * FROM inv_reservas WHERE documento_id = d.id ORDER BY producto_id LOOP
                v_pend := r.reservada - r.despachada - r.liberada;
                IF v_pend > 0 THEN
                    UPDATE inv_reservas SET liberada = liberada + v_pend, actualizado_en = now() WHERE id = r.id;
                    PERFORM inv_mover(d.empresa_id, r.producto_id, r.sucursal_id, 'reserva_revertida', 0, -v_pend,
                                      'documento', d.id, false, d.id, NULL, NULL, NULL, d.estado);
                END IF;
            END LOOP;
        ELSE
            -- Venta directa o nota: lo contrario de lo que movió.
            FOR r IN
                SELECT m.producto_id, m.sucursal_id, m.documento_id,
                       sum(m.cantidad) AS c, sum(m.reservado) AS rv
                  FROM inv_movimientos m
                 WHERE m.fuente = 'documento' AND m.fuente_id = d.id
                 GROUP BY m.producto_id, m.sucursal_id, m.documento_id
                HAVING sum(m.cantidad) <> 0 OR sum(m.reservado) <> 0
                 ORDER BY m.producto_id
            LOOP
                -- Lo liberado del original vuelve a estar reservado.
                IF r.rv <> 0 AND r.documento_id IS DISTINCT FROM d.id THEN
                    UPDATE inv_reservas SET liberada = liberada + r.rv, actualizado_en = now()
                     WHERE documento_id = r.documento_id AND producto_id = r.producto_id AND sucursal_id = r.sucursal_id;
                END IF;
                PERFORM inv_mover(d.empresa_id, r.producto_id, r.sucursal_id,
                                  CASE WHEN a.modo = 'nota' THEN 'devolucion_revertida' ELSE 'venta_revertida' END,
                                  -r.c, -r.rv, 'documento', d.id, false, r.documento_id, NULL, NULL, NULL, d.estado);
            END LOOP;
        END IF;

        UPDATE inv_aplicado SET aplicado = false, actualizado_en = now() WHERE fuente_id = p_doc;
    END IF;
END $$;


CREATE OR REPLACE FUNCTION inv_sync_guia(p_guia uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    g          guias%ROWTYPE;
    a          inv_aplicado%ROWTYPE;
    ad         inv_aplicado%ROWTYPE;
    v_quiere   boolean;
    v_modo     text;
    v_suc      uuid;
    v_estricto boolean;
    v_tras     uuid;
    v_estado_t text;
    r          record;
    v_pend     numeric;
    v_toma     numeric;
BEGIN
    SELECT * INTO g FROM guias WHERE id = p_guia;
    IF NOT FOUND OR g.tipo <> '09' THEN RETURN; END IF;

    v_quiere := g.activo AND g.estado IN ('encolado', 'aceptado', 'observado');

    SELECT * INTO a FROM inv_aplicado WHERE fuente_id = p_guia FOR UPDATE;
    IF NOT FOUND THEN
        IF NOT v_quiere OR NOT inv_activo(g.empresa_id) THEN RETURN; END IF;

        v_suc := COALESCE(g.inv_origen_id, g.sucursal_id, inv_principal(g.empresa_id));
        IF g.motivo IN ('02', '08') THEN
            v_modo := 'ninguno';
        ELSIF g.documento_id IS NOT NULL THEN
            SELECT * INTO ad FROM inv_aplicado WHERE fuente_id = g.documento_id;
            IF FOUND AND ad.modo = 'reserva' THEN
                v_modo := 'despacho';
                v_suc := ad.sucursal_id;
            ELSIF FOUND AND ad.modo = 'salida' THEN
                v_modo := 'ninguno';
            ELSE
                v_modo := 'salida';     -- facturada antes de llevar inventario: sale ahora
            END IF;
        ELSIF g.motivo = '04' AND g.inv_destino_id IS NOT NULL AND g.inv_destino_id <> v_suc THEN
            v_modo := 'traslado';
        ELSE
            v_modo := 'salida';
        END IF;
        IF v_suc IS NULL THEN RETURN; END IF;

        INSERT INTO inv_aplicado (fuente_id, fuente, empresa_id, modo, sucursal_id, aplicado)
        VALUES (p_guia, 'guia', g.empresa_id, v_modo, v_suc, false)
        RETURNING * INTO a;
    END IF;

    IF v_quiere = a.aplicado OR a.modo = 'ninguno' THEN RETURN; END IF;

    IF v_quiere THEN
        IF NOT inv_activo(g.empresa_id) THEN RETURN; END IF;
        v_estricto := g.estado = 'encolado' AND inv_bloquea(g.empresa_id, a.sucursal_id);

        IF a.modo = 'traslado' THEN
            SELECT id, estado INTO v_tras, v_estado_t FROM inv_traslados WHERE guia_id = g.id;
            IF v_tras IS NULL THEN
                INSERT INTO inv_traslados (empresa_id, guia_id, origen_id, destino_id)
                VALUES (g.empresa_id, g.id, a.sucursal_id, g.inv_destino_id)
                RETURNING id INTO v_tras;
            ELSE
                UPDATE inv_traslados SET estado = 'en_transito', salio_en = now() WHERE id = v_tras;
            END IF;
        END IF;

        FOR r IN
            SELECT b.producto_id, sum(b.cantidad * COALESCE(b.factor, 1)) AS q   -- 061: en unidades del producto
              FROM guia_bienes b
              JOIN productos p ON p.id = b.producto_id
             WHERE b.guia_id = g.id AND b.activo AND p.controla_stock
             GROUP BY b.producto_id
             ORDER BY b.producto_id
        LOOP
            IF a.modo = 'despacho' THEN
                SELECT COALESCE(sum(reservada - despachada - liberada), 0) INTO v_pend
                  FROM inv_reservas WHERE documento_id = g.documento_id AND producto_id = r.producto_id
                   AND sucursal_id = a.sucursal_id;
                v_toma := LEAST(r.q, GREATEST(v_pend, 0));
                IF v_toma > 0 THEN
                    UPDATE inv_reservas SET despachada = despachada + v_toma, actualizado_en = now()
                     WHERE documento_id = g.documento_id AND producto_id = r.producto_id AND sucursal_id = a.sucursal_id;
                END IF;
                PERFORM inv_mover(g.empresa_id, r.producto_id, a.sucursal_id, 'despacho', -r.q, -v_toma,
                                  'guia', g.id, v_estricto, g.documento_id, g.id);

            ELSIF a.modo = 'traslado' THEN
                INSERT INTO inv_traslado_lineas AS x (empresa_id, traslado_id, producto_id, enviado)
                VALUES (g.empresa_id, v_tras, r.producto_id, r.q)
                ON CONFLICT (traslado_id, producto_id) DO UPDATE SET enviado = r.q;
                PERFORM inv_mover(g.empresa_id, r.producto_id, a.sucursal_id, 'traslado_salida', -r.q, 0,
                                  'guia', g.id, v_estricto, NULL, g.id);

            ELSE
                PERFORM inv_mover(g.empresa_id, r.producto_id, a.sucursal_id, 'guia_salida', -r.q, 0,
                                  'guia', g.id, v_estricto, NULL, g.id);
            END IF;
        END LOOP;

        UPDATE inv_aplicado SET aplicado = true, actualizado_en = now() WHERE fuente_id = p_guia;

    ELSE
        IF a.modo = 'traslado' THEN
            SELECT id, estado INTO v_tras, v_estado_t FROM inv_traslados WHERE guia_id = g.id;
            -- Ya recibida en destino: la mercadería está allá.
            IF v_estado_t IN ('recibido', 'con_diferencia') THEN RETURN; END IF;
            UPDATE inv_traslados SET estado = 'anulado' WHERE id = v_tras;
        END IF;

        FOR r IN
            SELECT m.producto_id, m.sucursal_id, m.documento_id, sum(m.cantidad) AS c, sum(m.reservado) AS rv
              FROM inv_movimientos m
             WHERE m.fuente = 'guia' AND m.fuente_id = g.id
             GROUP BY m.producto_id, m.sucursal_id, m.documento_id
            HAVING sum(m.cantidad) <> 0 OR sum(m.reservado) <> 0
             ORDER BY m.producto_id
        LOOP
            IF r.rv <> 0 AND r.documento_id IS NOT NULL THEN
                UPDATE inv_reservas SET despachada = despachada + r.rv, actualizado_en = now()
                 WHERE documento_id = r.documento_id AND producto_id = r.producto_id AND sucursal_id = r.sucursal_id;
            END IF;
            PERFORM inv_mover(g.empresa_id, r.producto_id, r.sucursal_id,
                              CASE a.modo WHEN 'despacho' THEN 'despacho_revertido'
                                          WHEN 'traslado' THEN 'traslado_revertido'
                                          ELSE 'guia_revertida' END,
                              -r.c, -r.rv, 'guia', g.id, false, r.documento_id, g.id, NULL, NULL, g.estado);
        END LOOP;

        UPDATE inv_aplicado SET aplicado = false, actualizado_en = now() WHERE fuente_id = p_guia;
    END IF;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('061_presentaciones')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '061: presentaciones (unidad de compra distinta a la de venta). Nada se borró.';
END $$;