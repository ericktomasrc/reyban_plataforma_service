-- =============================================================================
-- 097 — NOTAS DE VENTA: CONTADO, FIADO Y PEDIDO (e282, 22 nov 2026)
--
-- Una venta SIN comprobante electrónico (no va a SUNAT): para vender al fiado,
-- separar un pedido o vender rápido. Cuando el cliente pide su comprobante, la
-- nota se convierte en boleta o factura.
--
--   · notas_venta / nota_venta_lineas: la nota (serie NV01, NV02… una por
--     sucursal; número correlativo) y sus productos. Nada se borra: una nota
--     se anula con motivo.
--   · forma: contado (con su medio) | fiado (paga hasta) | pedido (paga al
--     entregar). entregado_en: NULL = todavía no se lo lleva (el stock queda
--     RESERVADO); con fecha = salió del stock.
--   · nota_venta_stock(): el stock de una nota, siempre como debe estar
--     (reservado, salió o nada si se anuló). Mueve solo la diferencia: se
--     puede llamar las veces que sea. En el kardex sale como «Nota de venta».
--   · documentos.nota_venta_id: la boleta o factura en la que se convirtió.
--     ESA BOLETA O FACTURA NO MUEVE STOCK (ya lo movió la nota): así no se
--     descuenta dos veces.
--   · Fiado: queda en Cobros y pagos › Por cobrar. Los pagos van en
--     deuda_pagos (columna nueva nota_venta_id), con el mismo freno de «no se
--     paga más de lo que falta». La deuda sigue en la nota aunque se
--     convierta (la boleta o factura de una nota fiada no se cuenta otra vez).
--   · El menú «Notas de venta» en Comprobantes (armador de planes: Básico,
--     marcada «nueva»).
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================

BEGIN;

-- --- Las series: una por sucursal (NV01, NV02…) ---------------------------------
CREATE TABLE IF NOT EXISTS nv_series (
    sucursal_id  uuid        PRIMARY KEY REFERENCES sucursales (id),
    empresa_id   uuid        NOT NULL REFERENCES empresas (id),
    serie        text        NOT NULL,
    ultimo       integer     NOT NULL DEFAULT 0,
    creado_en    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_nv_series UNIQUE (empresa_id, serie),
    CONSTRAINT ck_nv_series CHECK (serie ~ '^NV[0-9]{2,3}$' AND ultimo >= 0)
);
COMMENT ON TABLE nv_series IS '097: la serie de notas de venta de cada sucursal y su último número.';

ALTER TABLE nv_series ENABLE ROW LEVEL SECURITY;
ALTER TABLE nv_series FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_nv_series ON nv_series;
CREATE POLICY p_nv_series ON nv_series
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());
DROP TRIGGER IF EXISTS auditar_nv_series ON nv_series;
CREATE TRIGGER auditar_nv_series AFTER INSERT OR UPDATE OR DELETE ON nv_series
    FOR EACH ROW EXECUTE FUNCTION fn_auditar();
GRANT SELECT, INSERT, UPDATE ON nv_series TO plataforma_app;

-- --- La nota ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS notas_venta (
    id              uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid          NOT NULL REFERENCES empresas (id),
    sucursal_id     uuid          NOT NULL REFERENCES sucursales (id),
    serie           text          NOT NULL,
    correlativo     integer       NOT NULL,
    fecha           timestamptz   NOT NULL DEFAULT now(),
    -- NULL = «cliente varios» (solo al contado).
    cliente_id      uuid          REFERENCES clientes (id),
    forma           text          NOT NULL,
    medio           text,
    vence           date,
    entrega_fecha   date,
    entregado_en    timestamptz,
    total           numeric(14,2) NOT NULL,
    nota            text,
    anulado_en      timestamptz,
    anulado_motivo  text,
    anulado_por     uuid,
    creado_en       timestamptz   NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean       NOT NULL DEFAULT true,
    version         integer       NOT NULL DEFAULT 1,

    CONSTRAINT ux_notas_venta_numero UNIQUE (empresa_id, serie, correlativo),
    CONSTRAINT ck_notas_venta_forma CHECK (forma IN ('contado', 'fiado', 'pedido')),
    CONSTRAINT ck_notas_venta_medio CHECK (medio IS NULL OR medio IN ('efectivo', 'yape', 'tarjeta', 'transferencia', 'otro')),
    CONSTRAINT ck_notas_venta_reglas CHECK (
        (forma <> 'fiado' OR (vence IS NOT NULL AND cliente_id IS NOT NULL))
        AND (forma <> 'pedido' OR (entregado_en IS NULL AND cliente_id IS NOT NULL))),
    CONSTRAINT ck_notas_venta_total  CHECK (total > 0),
    CONSTRAINT ck_notas_venta_anulada CHECK (anulado_en IS NULL OR length(btrim(coalesce(anulado_motivo, ''))) >= 3)
);
CREATE INDEX IF NOT EXISTS ix_notas_venta_fecha   ON notas_venta (empresa_id, fecha DESC);
CREATE INDEX IF NOT EXISTS ix_notas_venta_cliente ON notas_venta (cliente_id) WHERE cliente_id IS NOT NULL;

COMMENT ON TABLE notas_venta IS '097: notas de venta (sin SUNAT): contado, fiado o pedido. No se borran: se anulan con motivo.';
COMMENT ON COLUMN notas_venta.entregado_en IS '097: NULL = todavía no se lo lleva (stock reservado); con fecha = salió del stock.';

ALTER TABLE notas_venta ENABLE ROW LEVEL SECURITY;
ALTER TABLE notas_venta FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_notas_venta ON notas_venta;
CREATE POLICY p_notas_venta ON notas_venta
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_notas_venta') THEN
        PERFORM vigilar_tabla('notas_venta');
    END IF;
END $$;
GRANT SELECT, INSERT, UPDATE ON notas_venta TO plataforma_app;

-- --- Sus productos (no cambian: para corregir, se anula y se hace otra) ----------
CREATE TABLE IF NOT EXISTS nota_venta_lineas (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id       uuid          NOT NULL REFERENCES empresas (id),
    nota_id          uuid          NOT NULL REFERENCES notas_venta (id),
    numero           integer       NOT NULL,
    producto_id      uuid          NOT NULL REFERENCES productos (id),
    codigo           text,
    descripcion      text          NOT NULL,
    unidad_medida    text          NOT NULL DEFAULT 'NIU',
    tipo_afectacion  text          NOT NULL DEFAULT '10',
    cantidad         numeric(18,4) NOT NULL,
    -- Lo que se cobra por unidad (con IGV si es gravado) y lo mismo sin IGV.
    precio           numeric(14,2) NOT NULL,
    valor_unitario   numeric(18,6) NOT NULL,
    importe          numeric(14,2) NOT NULL,
    presentacion_id  uuid,
    factor           numeric(18,4),
    creado_en        timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ux_nota_venta_lineas UNIQUE (nota_id, numero),
    CONSTRAINT ck_nota_venta_lineas CHECK (cantidad > 0 AND precio >= 0 AND valor_unitario >= 0 AND importe >= 0
                                          AND (factor IS NULL OR factor > 0))
);
CREATE INDEX IF NOT EXISTS ix_nota_venta_lineas_nota ON nota_venta_lineas (nota_id);
COMMENT ON TABLE nota_venta_lineas IS '097: los productos de una nota de venta. Solo inserción.';

ALTER TABLE nota_venta_lineas ENABLE ROW LEVEL SECURITY;
ALTER TABLE nota_venta_lineas FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_nota_venta_lineas ON nota_venta_lineas;
CREATE POLICY p_nota_venta_lineas ON nota_venta_lineas
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_nota_venta_lineas') THEN
        PERFORM vigilar_tabla_inmutable('nota_venta_lineas');
    END IF;
END $$;
GRANT SELECT, INSERT ON nota_venta_lineas TO plataforma_app;

-- --- La boleta o factura en la que se convirtió ------------------------------------
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS nota_venta_id uuid REFERENCES notas_venta (id);
CREATE INDEX IF NOT EXISTS ix_documentos_nota_venta ON documentos (nota_venta_id) WHERE nota_venta_id IS NOT NULL;
COMMENT ON COLUMN documentos.nota_venta_id IS '097: la nota de venta de la que sale. No mueve stock (ya lo movió la nota).';

-- --- Los pagos de una nota fiada ----------------------------------------------------
ALTER TABLE deuda_pagos ADD COLUMN IF NOT EXISTS nota_venta_id uuid REFERENCES notas_venta (id);
CREATE INDEX IF NOT EXISTS ix_deuda_pagos_nota ON deuda_pagos (nota_venta_id) WHERE nota_venta_id IS NOT NULL;
ALTER TABLE deuda_pagos DROP CONSTRAINT IF EXISTS ck_deuda_pagos_lado;
ALTER TABLE deuda_pagos ADD CONSTRAINT ck_deuda_pagos_lado CHECK (
    (lado = 'cobrar' AND compra_id IS NULL AND ((documento_id IS NOT NULL) <> (nota_venta_id IS NOT NULL)))
    OR (lado = 'pagar' AND compra_id IS NOT NULL AND documento_id IS NULL AND nota_venta_id IS NULL));

-- --- El kardex: un movimiento puede venir de una nota de venta -------------------------
ALTER TABLE inv_movimientos DROP CONSTRAINT IF EXISTS ck_inv_movimientos_fuente;
ALTER TABLE inv_movimientos ADD CONSTRAINT ck_inv_movimientos_fuente
    CHECK (fuente IN ('documento', 'guia', 'operacion', 'traslado', 'nota_venta'));

-- -----------------------------------------------------------------------------
-- EL NÚMERO: la serie de la sucursal (se crea la primera vez) y el siguiente.
-- Con los permisos de quien llama (solo su empresa).
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION nota_venta_numero(p_sucursal uuid, OUT o_serie text, OUT o_numero integer)
LANGUAGE plpgsql VOLATILE AS $$
DECLARE
    v_e uuid;
BEGIN
    SELECT s.empresa_id INTO v_e FROM sucursales s WHERE s.id = p_sucursal AND s.activo AND s.empresa_id = ctx_empresa();
    IF v_e IS NULL THEN
        RAISE EXCEPTION 'NV_SUCURSAL: Esa sucursal no existe o está desactivada.' USING ERRCODE = 'check_violation';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtext('nv_series:' || v_e::text));
    INSERT INTO nv_series AS x (sucursal_id, empresa_id, serie, ultimo)
    VALUES (p_sucursal, v_e, 'NV' || lpad(((SELECT count(*) FROM nv_series y WHERE y.empresa_id = v_e) + 1)::text, 2, '0'), 1)
    ON CONFLICT (sucursal_id) DO UPDATE SET ultimo = x.ultimo + 1
    RETURNING x.serie, x.ultimo INTO o_serie, o_numero;
END $$;
GRANT EXECUTE ON FUNCTION nota_venta_numero(uuid) TO plataforma_app;

-- -----------------------------------------------------------------------------
-- EL STOCK DE UNA NOTA, SIEMPRE COMO DEBE ESTAR:
--   anulada            → nada (vuelve lo que salió, se libera lo reservado)
--   sin entregar       → reservado (no se puede vender a otro)
--   entregada          → salió del físico
-- Mueve solo la diferencia con lo que ya movió: se puede llamar varias veces.
-- Bloquea por falta de stock solo si la sucursal controla (como al emitir).
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION nota_venta_stock(p_nota uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    n          notas_venta%ROWTYPE;
    v_modo     text;
    v_estricto boolean;
    v_num      text;
    v_tipo     text;
    r          record;
BEGIN
    SELECT * INTO n FROM notas_venta WHERE id = p_nota FOR UPDATE;
    IF NOT FOUND OR (NOT ctx_es_super() AND n.empresa_id IS DISTINCT FROM ctx_empresa()) THEN
        RAISE EXCEPTION 'NV_NO_EXISTE: No se encontró esa nota de venta.' USING ERRCODE = 'check_violation';
    END IF;

    v_modo := CASE WHEN n.anulado_en IS NOT NULL OR NOT n.activo THEN 'nada'
                   WHEN n.entregado_en IS NULL THEN 'reserva'
                   ELSE 'salida' END;

    -- Inventario apagado y nada movido: no se mueve nada nuevo.
    IF NOT inv_activo(n.empresa_id)
       AND NOT EXISTS (SELECT 1 FROM inv_movimientos m WHERE m.fuente = 'nota_venta' AND m.fuente_id = p_nota) THEN
        RETURN;
    END IF;

    v_estricto := v_modo <> 'nada' AND inv_activo(n.empresa_id) AND inv_bloquea(n.empresa_id, n.sucursal_id);
    v_num := n.serie || '-' || lpad(n.correlativo::text, 8, '0');

    FOR r IN
        WITH quiere AS (
            SELECT x.producto_id, sum(x.q) AS q
              FROM (SELECT l.producto_id, l.cantidad * COALESCE(l.factor, 1) AS q
                      FROM nota_venta_lineas l
                      JOIN productos p ON p.id = l.producto_id
                     WHERE l.nota_id = n.id AND p.controla_stock AND NOT p.es_kit
                    UNION ALL
                    SELECT c.componente_id, l.cantidad * COALESCE(l.factor, 1) * c.cantidad
                      FROM nota_venta_lineas l
                      JOIN productos k ON k.id = l.producto_id AND k.es_kit
                      JOIN producto_componentes c ON c.kit_id = k.id AND c.activo
                      JOIN productos pc ON pc.id = c.componente_id AND pc.controla_stock
                     WHERE l.nota_id = n.id) x
             GROUP BY x.producto_id),
        hay AS (
            SELECT m.producto_id, sum(m.cantidad) AS c, sum(m.reservado) AS rv
              FROM inv_movimientos m
             WHERE m.fuente = 'nota_venta' AND m.fuente_id = n.id
             GROUP BY m.producto_id)
        SELECT COALESCE(q.producto_id, h.producto_id) AS producto_id,
               (CASE WHEN v_modo = 'salida'  THEN -COALESCE(q.q, 0) ELSE 0 END) - COALESCE(h.c, 0)  AS dc,
               (CASE WHEN v_modo = 'reserva' THEN  COALESCE(q.q, 0) ELSE 0 END) - COALESCE(h.rv, 0) AS dr
          FROM quiere q
          FULL JOIN hay h ON h.producto_id = q.producto_id
         ORDER BY 1     -- siempre el mismo orden: sin cruces de candados
    LOOP
        CONTINUE WHEN r.dc = 0 AND r.dr = 0;
        v_tipo := CASE WHEN r.dc < 0 AND r.dr < 0 THEN 'despacho'
                       WHEN r.dc < 0 THEN 'venta'
                       WHEN r.dc > 0 THEN 'venta_revertida'
                       WHEN r.dr > 0 THEN 'reserva'
                       ELSE 'reserva_revertida' END;
        PERFORM inv_mover(n.empresa_id, r.producto_id, n.sucursal_id, v_tipo, r.dc, r.dr,
                          'nota_venta', n.id, v_estricto, NULL, NULL, NULL, NULL, 'Nota de venta ' || v_num);
    END LOOP;
END $$;
REVOKE ALL ON FUNCTION nota_venta_stock(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nota_venta_stock(uuid) TO plataforma_app;

-- -----------------------------------------------------------------------------
-- UNA BOLETA O FACTURA QUE SALE DE UNA NOTA NO MUEVE STOCK.
-- (La misma inv_sync_documento de la 062; solo se añade esa salida.)
-- -----------------------------------------------------------------------------
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
    -- 097: la boleta o factura de una nota de venta no mueve stock (ya lo movió la nota).
    IF d.nota_venta_id IS NOT NULL THEN RETURN; END IF;

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
            -- 061: en unidades del producto. 062: un kit mueve sus componentes.
            SELECT x.producto_id, sum(x.q) AS q
              FROM (SELECT l.producto_id, l.cantidad * COALESCE(l.factor, 1) AS q
                      FROM documento_lineas l
                      JOIN productos p ON p.id = l.producto_id
                     WHERE l.documento_id = d.id AND l.activo AND p.controla_stock AND NOT p.es_kit AND l.cantidad > 0
                    UNION ALL
                    SELECT c.componente_id, l.cantidad * COALESCE(l.factor, 1) * c.cantidad
                      FROM documento_lineas l
                      JOIN productos k ON k.id = l.producto_id AND k.es_kit
                      JOIN producto_componentes c ON c.kit_id = k.id AND c.activo
                      JOIN productos pc ON pc.id = c.componente_id AND pc.controla_stock
                     WHERE l.documento_id = d.id AND l.activo AND l.cantidad > 0) x
             GROUP BY x.producto_id
             ORDER BY x.producto_id     -- siempre el mismo orden: sin cruces de candados
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


-- -----------------------------------------------------------------------------
-- LO QUE FALTA PAGAR DE UNA NOTA FIADA (NULL si no es deuda: contado, pedido
-- sin entregar o anulada). Con los permisos de quien llama.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION deuda_saldo_nota(p_nota uuid, p_menos uuid DEFAULT NULL)
RETURNS numeric LANGUAGE sql STABLE AS $$
    SELECT greatest(0, n.total
             - coalesce((SELECT sum(p.monto) FROM deuda_pagos p
                          WHERE p.nota_venta_id = n.id AND p.anulado_en IS NULL AND p.activo
                            AND (p_menos IS NULL OR p.id <> p_menos)), 0))
      FROM notas_venta n
     WHERE n.id = p_nota AND n.forma = 'fiado' AND n.anulado_en IS NULL AND n.activo
$$;
COMMENT ON FUNCTION deuda_saldo_nota(uuid, uuid) IS '097: lo que falta pagar de una nota de venta fiada (NULL si no es deuda).';
GRANT EXECUTE ON FUNCTION deuda_saldo_nota(uuid, uuid) TO plataforma_app;

-- El freno de la 090, ahora también para las notas fiadas.
CREATE OR REPLACE FUNCTION fn_deuda_pagos_tope() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_saldo numeric;
BEGIN
    IF NEW.anulado_en IS NOT NULL OR NOT NEW.activo THEN
        RETURN NEW;     -- anular un pago siempre se puede
    END IF;
    IF TG_OP = 'UPDATE' AND (NEW.monto IS DISTINCT FROM OLD.monto
                             OR NEW.documento_id IS DISTINCT FROM OLD.documento_id
                             OR NEW.compra_id IS DISTINCT FROM OLD.compra_id
                             OR NEW.nota_venta_id IS DISTINCT FROM OLD.nota_venta_id
                             OR OLD.anulado_en IS NOT NULL) THEN
        RAISE EXCEPTION 'DEUDA_PAGO_FIJO: Un pago no se cambia: anúlalo y anota otro.'
            USING ERRCODE = 'check_violation';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        RETURN NEW;
    END IF;
    v_saldo := CASE WHEN NEW.nota_venta_id IS NOT NULL THEN deuda_saldo_nota(NEW.nota_venta_id, NEW.id)
                    ELSE deuda_saldo(NEW.documento_id, NEW.compra_id, NEW.id) END;
    IF v_saldo IS NULL THEN
        RAISE EXCEPTION 'DEUDA_NO_HAY: Eso no es una deuda (no es al crédito o fiado, o está anulado).'
            USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.monto > v_saldo + 0.005 THEN
        RAISE EXCEPTION 'DEUDA_TOPE: El pago (%) es mayor que lo que falta (%).', NEW.monto, v_saldo
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

-- -----------------------------------------------------------------------------
-- LA LISTA DE COBROS Y PAGOS (090), con las notas fiadas en «Por cobrar».
-- Una boleta o factura que salió de una nota fiada no se cuenta otra vez: la
-- deuda sigue en la nota.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION deudas_filas(p_lado text)
RETURNS TABLE (origen text, id uuid, nombre text, tipo_doc text, numero_doc text, comprobante text, tipo text, clase text,
               emitido date, vence date, cuota int, cuotas int, moneda text, total numeric, nc numeric, pagado numeric,
               saldo numeric, estado text, dias int, ultimo date)
LANGUAGE sql STABLE AS $$
    WITH pgd AS (
        SELECT p.documento_id AS id, sum(p.monto) AS pagado, max(p.fecha) AS ultimo
          FROM deuda_pagos p WHERE p.empresa_id = ctx_empresa() AND p.lado = 'cobrar' AND p.documento_id IS NOT NULL
           AND p.anulado_en IS NULL AND p.activo GROUP BY 1),
    pgn AS (
        SELECT p.nota_venta_id AS id, sum(p.monto) AS pagado, max(p.fecha) AS ultimo
          FROM deuda_pagos p WHERE p.empresa_id = ctx_empresa() AND p.lado = 'cobrar' AND p.nota_venta_id IS NOT NULL
           AND p.anulado_en IS NULL AND p.activo GROUP BY 1),
    pgc AS (
        SELECT p.compra_id AS id, sum(p.monto) AS pagado, max(p.fecha) AS ultimo
          FROM deuda_pagos p WHERE p.empresa_id = ctx_empresa() AND p.lado = 'pagar' AND p.anulado_en IS NULL AND p.activo GROUP BY 1),
    nc AS (
        SELECT n.documento_ref_id AS id, sum(n.importe_total) AS nc
          FROM documentos n WHERE n.empresa_id = ctx_empresa() AND n.tipo = '07' AND n.documento_ref_id IS NOT NULL
           AND n.estado IN ('encolado', 'aceptado', 'observado') GROUP BY 1),
    doc AS (
        SELECT d.id, d.tipo, d.serie, d.correlativo, d.fecha_emision, d.moneda, d.cuotas,
               c.razon_social AS nombre, c.tipo_documento AS tipo_doc, c.numero_documento AS numero_doc,
               coalesce((SELECT sum((x ->> 'monto')::numeric) FROM jsonb_array_elements(d.cuotas) x), 0) AS total,
               coalesce(nc.nc, 0) AS nc, coalesce(pgd.pagado, 0) AS pagado, pgd.ultimo
          FROM documentos d
          JOIN clientes c ON c.id = d.cliente_id
          LEFT JOIN nc ON nc.id = d.id
          LEFT JOIN pgd ON pgd.id = d.id
         WHERE p_lado = 'cobrar' AND d.empresa_id = ctx_empresa() AND d.forma_pago = 'credito' AND d.tipo IN ('01', '03')
           AND d.estado IN ('encolado', 'aceptado', 'observado')
           AND NOT EXISTS (SELECT 1 FROM notas_venta nv WHERE nv.id = d.nota_venta_id AND nv.forma = 'fiado')),
    fila AS (
        SELECT 'documento'::text AS origen, doc.id, doc.nombre, doc.tipo_doc::text AS tipo_doc, doc.numero_doc,
               coalesce(doc.serie, '') || '-' || lpad(coalesce(doc.correlativo, 0)::text, 8, '0') AS comprobante,
               doc.tipo::text AS tipo, NULL::text AS clase, doc.fecha_emision AS emitido,
               coalesce(prox.vence, (SELECT max((x ->> 'vence')::date) FROM jsonb_array_elements(doc.cuotas) x), doc.fecha_emision) AS vence,
               prox.n AS cuota, jsonb_array_length(doc.cuotas) AS cuotas, doc.moneda::text AS moneda,
               doc.total, doc.nc, doc.pagado, greatest(0, doc.total - doc.nc - doc.pagado) AS saldo, doc.ultimo
          FROM doc
          LEFT JOIN LATERAL (
              SELECT (q.x ->> 'vence')::date AS vence, q.o::int AS n
                FROM (SELECT t.x, t.o, sum((t.x ->> 'monto')::numeric) OVER (ORDER BY t.o) AS acum
                        FROM jsonb_array_elements(doc.cuotas) WITH ORDINALITY AS t(x, o)) q
               WHERE q.acum > doc.nc + doc.pagado + 0.005
               ORDER BY q.o LIMIT 1) prox ON true
        UNION ALL
        -- 097: las notas de venta fiadas.
        SELECT 'nota'::text, n.id, coalesce(c.razon_social, 'CLIENTE VARIOS'), c.tipo_documento::text, c.numero_documento,
               n.serie || '-' || lpad(n.correlativo::text, 8, '0'), 'NV'::text, NULL::text,
               (n.fecha AT TIME ZONE 'America/Lima')::date, n.vence, NULL::int, NULL::int, 'PEN'::text,
               n.total, 0::numeric, coalesce(pgn.pagado, 0), greatest(0, n.total - coalesce(pgn.pagado, 0)), pgn.ultimo
          FROM notas_venta n
          LEFT JOIN clientes c ON c.id = n.cliente_id
          LEFT JOIN pgn ON pgn.id = n.id
         WHERE p_lado = 'cobrar' AND n.empresa_id = ctx_empresa() AND n.activo AND n.anulado_en IS NULL AND n.forma = 'fiado'
        UNION ALL
        SELECT 'compra'::text, c.id,
               coalesce(c.proveedor_nombre, upper(left(c.categoria, 1)) || substr(c.categoria, 2)),
               c.proveedor_tipo_doc::text, c.proveedor_doc,
               CASE WHEN c.comprobante_tipo = '00' THEN coalesce(c.descripcion, 'Sin comprobante')
                    ELSE coalesce(c.serie, '') || '-' || coalesce(c.numero, '') END,
               c.comprobante_tipo, c.clase, c.fecha_emision, coalesce(c.vence, c.fecha_emision), NULL::int, NULL::int, c.moneda::text,
               c.total, 0::numeric, coalesce(pgc.pagado, 0), greatest(0, c.total - coalesce(pgc.pagado, 0)), pgc.ultimo
          FROM compras c
          LEFT JOIN pgc ON pgc.id = c.id
         WHERE p_lado = 'pagar' AND c.empresa_id = ctx_empresa() AND c.activo AND c.anulada_en IS NULL AND c.forma_pago = 'credito')
    SELECT f.origen, f.id, f.nombre, f.tipo_doc, f.numero_doc, f.comprobante, f.tipo, f.clase, f.emitido, f.vence,
           f.cuota, f.cuotas, f.moneda, f.total, f.nc, f.pagado, f.saldo,
           CASE WHEN f.saldo <= 0.005 THEN 'pagada'
                WHEN f.vence < hoy_peru() THEN 'vencida'
                WHEN f.vence <= hoy_peru() + 7 THEN 'porvencer'
                ELSE 'aldia' END,
           (f.vence - hoy_peru())::int, f.ultimo
      FROM fila f
$$;
COMMENT ON FUNCTION deudas_filas(text) IS '090/097: la lista de Cobros y pagos (cobrar | pagar), con las notas de venta fiadas.';
GRANT EXECUTE ON FUNCTION deudas_filas(text) TO plataforma_app;

-- -----------------------------------------------------------------------------
-- LAS NOTAS DE LA EMPRESA ABIERTA, con su estado, lo pagado y su comprobante.
-- estado: anulada | convertida | pedido | por_cobrar | pagada.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION notas_venta_filas()
RETURNS TABLE (id uuid, numero text, fecha timestamptz, sucursal_id uuid, cliente_id uuid, cliente text,
               cliente_tipo_doc text, cliente_doc text, forma text, medio text, vence date, entrega_fecha date,
               entregado_en timestamptz, total numeric, pagado numeric, saldo numeric,
               documento_id uuid, documento text, documento_tipo text,
               anulado_en timestamptz, anulado_motivo text, estado text, vendedor text, nota text)
LANGUAGE sql STABLE AS $$
    SELECT n.id, n.serie || '-' || lpad(n.correlativo::text, 8, '0'), n.fecha, n.sucursal_id, n.cliente_id,
           coalesce(c.razon_social, 'CLIENTE VARIOS'), c.tipo_documento::text, c.numero_documento,
           n.forma, n.medio, n.vence, n.entrega_fecha, n.entregado_en, n.total,
           coalesce(p.pagado, 0),
           CASE WHEN n.forma = 'fiado' AND n.anulado_en IS NULL THEN greatest(0, n.total - coalesce(p.pagado, 0)) ELSE 0 END,
           d.id, CASE WHEN d.id IS NOT NULL THEN d.serie || '-' || lpad(coalesce(d.correlativo, 0)::text, 8, '0') END, d.tipo::text,
           n.anulado_en, n.anulado_motivo,
           CASE WHEN n.anulado_en IS NOT NULL THEN 'anulada'
                WHEN d.id IS NOT NULL THEN 'convertida'
                WHEN n.forma = 'pedido' THEN 'pedido'
                WHEN n.forma = 'fiado' AND n.total - coalesce(p.pagado, 0) > 0.005 THEN 'por_cobrar'
                ELSE 'pagada' END,
           u.nombre, n.nota
      FROM notas_venta n
      LEFT JOIN clientes c ON c.id = n.cliente_id
      LEFT JOIN LATERAL (SELECT sum(x.monto) AS pagado FROM deuda_pagos x
                          WHERE x.nota_venta_id = n.id AND x.anulado_en IS NULL AND x.activo) p ON true
      LEFT JOIN LATERAL (SELECT y.id, y.serie, y.correlativo, y.tipo FROM documentos y
                          WHERE y.nota_venta_id = n.id AND y.activo AND y.estado IN ('encolado', 'aceptado', 'observado')
                          ORDER BY y.creado_en DESC LIMIT 1) d ON true
      LEFT JOIN usuarios u ON u.id = n.creado_por
     WHERE n.empresa_id = ctx_empresa() AND n.activo
$$;
COMMENT ON FUNCTION notas_venta_filas() IS '097: las notas de venta de la empresa abierta con su estado, lo pagado y su boleta o factura.';
GRANT EXECUTE ON FUNCTION notas_venta_filas() TO plataforma_app;

-- --- El menú en el armador de planes -----------------------------------------
INSERT INTO funciones (codigo, menu, menu_orden, grupo, nombre, orden, plan_minimo, fija, oculta, es_nueva, modulo, rutas_web, rutas_api)
VALUES ('notas_venta', 'Comprobantes', 10, NULL, 'Notas de venta', 4, 'basico', false, false, true, NULL,
        ARRAY['/facturacion/notas-venta'], ARRAY['/api/notas-venta'])
ON CONFLICT (codigo) DO NOTHING;

-- Las funciones con SECURITY DEFINER son del dueño de las puertas (027).
DO $$ BEGIN
    ALTER FUNCTION nota_venta_stock(uuid) OWNER TO plataforma_puertas;
    ALTER FUNCTION inv_sync_documento(uuid) OWNER TO plataforma_puertas;
    REVOKE ALL ON FUNCTION nota_venta_stock(uuid) FROM PUBLIC;
    GRANT EXECUTE ON FUNCTION nota_venta_stock(uuid) TO plataforma_app;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('097_notas_de_venta')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '097: notas de venta (contado, fiado y pedido). Nada se borró.';
END $$;