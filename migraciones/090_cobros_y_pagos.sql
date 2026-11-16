-- =============================================================================
-- 090 — COBROS Y PAGOS: POR COBRAR Y POR PAGAR (e267, 16 nov 2026)
--
-- Saber quién te debe, cuánto y desde cuándo, y lo que tú debes a tus
-- proveedores. Con pagos parciales (abonos).
--
--   · Por cobrar: las facturas y boletas AL CRÉDITO (documentos.forma_pago =
--     'credito', con sus cuotas) que no están anuladas ni rechazadas. Lo que
--     se debe es la suma de las cuotas (ya sin detracción), menos las notas
--     de crédito que las corrigen, menos los pagos anotados.
--   · Por pagar: las compras y gastos AL CRÉDITO. Columnas nuevas en
--     `compras`: forma_pago ('contado' | 'credito') y vence (la fecha de pago).
--     Lo de antes queda al contado: no aparece nada que no se pidió.
--   · deuda_pagos: cada pago (abono) de una deuda. No se borra: se anula con
--     motivo. Un pago no puede pasar de lo que falta (lo frena la base).
--   · deuda_saldo(): lo que falta pagar de un comprobante o de una compra.
--     La usan la API y el freno de arriba: una sola cuenta.
--   · Permisos deudas.ver y deudas.registrar (como compras.*).
--   · El menú «Cobros y pagos» en el armador de planes (plan Pro, como
--     Compras y gastos; marcada «nueva» para que se vea en el armador).
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================

BEGIN;

-- --- compras: al contado o al crédito ----------------------------------------
ALTER TABLE compras ADD COLUMN IF NOT EXISTS forma_pago text NOT NULL DEFAULT 'contado';
ALTER TABLE compras ADD COLUMN IF NOT EXISTS vence      date;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_compras_forma_pago') THEN
        ALTER TABLE compras ADD CONSTRAINT ck_compras_forma_pago CHECK (
            forma_pago IN ('contado', 'credito')
            AND (forma_pago = 'contado' OR vence IS NOT NULL));
    END IF;
END $$;

COMMENT ON COLUMN compras.forma_pago IS '090: contado | credito. Al crédito entra en «Por pagar».';
COMMENT ON COLUMN compras.vence IS '090: al crédito, la fecha en que hay que pagarla.';

CREATE INDEX IF NOT EXISTS ix_compras_credito ON compras (empresa_id) WHERE forma_pago = 'credito' AND anulada_en IS NULL;
CREATE INDEX IF NOT EXISTS ix_documentos_credito ON documentos (empresa_id) WHERE forma_pago = 'credito';

-- --- Los pagos ----------------------------------------------------------------
CREATE TABLE IF NOT EXISTS deuda_pagos (
    id              uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid          NOT NULL REFERENCES empresas (id),
    -- 'cobrar' = te pagó un cliente · 'pagar' = le pagaste a un proveedor.
    lado            text          NOT NULL,
    documento_id    uuid          REFERENCES documentos (id),
    compra_id       uuid          REFERENCES compras (id),
    fecha           date          NOT NULL,
    monto           numeric(14,2) NOT NULL,
    moneda          char(3)       NOT NULL DEFAULT 'PEN',
    medio           text          NOT NULL DEFAULT 'efectivo',
    referencia      text,
    nota            text,
    anulado_en      timestamptz,
    anulado_motivo  text,
    creado_en       timestamptz   NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean       NOT NULL DEFAULT true,
    version         integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_deuda_pagos_lado CHECK (
        (lado = 'cobrar' AND documento_id IS NOT NULL AND compra_id IS NULL)
        OR (lado = 'pagar' AND compra_id IS NOT NULL AND documento_id IS NULL)),
    CONSTRAINT ck_deuda_pagos_monto  CHECK (monto > 0),
    CONSTRAINT ck_deuda_pagos_moneda CHECK (moneda IN ('PEN', 'USD')),
    CONSTRAINT ck_deuda_pagos_medio  CHECK (medio IN ('efectivo', 'yape', 'tarjeta', 'transferencia', 'otro')),
    CONSTRAINT ck_deuda_pagos_anulado CHECK (anulado_en IS NULL OR length(btrim(coalesce(anulado_motivo, ''))) >= 3)
);

CREATE INDEX IF NOT EXISTS ix_deuda_pagos_documento ON deuda_pagos (documento_id) WHERE documento_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_deuda_pagos_compra    ON deuda_pagos (compra_id)    WHERE compra_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_deuda_pagos_fecha     ON deuda_pagos (empresa_id, fecha);

COMMENT ON TABLE deuda_pagos IS '090: pagos (abonos) de lo que te deben y de lo que debes. No se borran: se anulan con motivo.';

ALTER TABLE deuda_pagos ENABLE ROW LEVEL SECURITY;
ALTER TABLE deuda_pagos FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_deuda_pagos ON deuda_pagos;
CREATE POLICY p_deuda_pagos ON deuda_pagos
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_deuda_pagos') THEN
        PERFORM vigilar_tabla('deuda_pagos');
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON deuda_pagos TO plataforma_app;

-- --- Lo que falta pagar -------------------------------------------------------
-- De un comprobante al crédito: cuotas − notas de crédito − pagos.
-- De una compra al crédito: total − pagos. Sin deuda (al contado, anulado…): NULL.
-- Corre con los permisos de quien llama (RLS incluida): solo ve su empresa.
CREATE OR REPLACE FUNCTION deuda_saldo(p_documento uuid, p_compra uuid, p_menos uuid DEFAULT NULL)
RETURNS numeric LANGUAGE sql STABLE AS $$
    SELECT CASE
        WHEN p_documento IS NOT NULL THEN (
            SELECT greatest(0,
                     coalesce((SELECT sum((c ->> 'monto')::numeric) FROM jsonb_array_elements(d.cuotas) c), 0)
                   - coalesce((SELECT sum(n.importe_total) FROM documentos n
                                WHERE n.documento_ref_id = d.id AND n.tipo = '07'
                                  AND n.estado IN ('encolado', 'aceptado', 'observado')), 0)
                   - coalesce((SELECT sum(p.monto) FROM deuda_pagos p
                                WHERE p.documento_id = d.id AND p.anulado_en IS NULL AND p.activo
                                  AND (p_menos IS NULL OR p.id <> p_menos)), 0))
              FROM documentos d
             WHERE d.id = p_documento AND d.forma_pago = 'credito' AND d.tipo IN ('01', '03')
               AND d.estado IN ('encolado', 'aceptado', 'observado'))
        WHEN p_compra IS NOT NULL THEN (
            SELECT greatest(0, c.total
                   - coalesce((SELECT sum(p.monto) FROM deuda_pagos p
                                WHERE p.compra_id = c.id AND p.anulado_en IS NULL AND p.activo
                                  AND (p_menos IS NULL OR p.id <> p_menos)), 0))
              FROM compras c
             WHERE c.id = p_compra AND c.forma_pago = 'credito' AND c.anulada_en IS NULL AND c.activo)
    END
$$;

COMMENT ON FUNCTION deuda_saldo(uuid, uuid, uuid) IS '090: lo que falta pagar de un comprobante o compra al crédito (NULL si no es deuda).';
GRANT EXECUTE ON FUNCTION deuda_saldo(uuid, uuid, uuid) TO plataforma_app;

-- --- El freno: no se paga lo que no se debe ----------------------------------
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
                             OR OLD.anulado_en IS NOT NULL) THEN
        RAISE EXCEPTION 'DEUDA_PAGO_FIJO: Un pago no se cambia: anúlalo y anota otro.'
            USING ERRCODE = 'check_violation';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        RETURN NEW;
    END IF;
    v_saldo := deuda_saldo(NEW.documento_id, NEW.compra_id, NEW.id);
    IF v_saldo IS NULL THEN
        RAISE EXCEPTION 'DEUDA_NO_HAY: Ese comprobante o compra no es una deuda (no es al crédito, o está anulado).'
            USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.monto > v_saldo + 0.005 THEN
        RAISE EXCEPTION 'DEUDA_TOPE: El pago (%) es mayor que lo que falta (%).', NEW.monto, v_saldo
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS b_deuda_pagos_tope ON deuda_pagos;
CREATE TRIGGER b_deuda_pagos_tope BEFORE INSERT OR UPDATE ON deuda_pagos
    FOR EACH ROW EXECUTE FUNCTION fn_deuda_pagos_tope();

-- --- La lista: una fila por deuda --------------------------------------------
CREATE OR REPLACE FUNCTION hoy_peru() RETURNS date LANGUAGE sql STABLE AS $$
    SELECT (now() AT TIME ZONE 'America/Lima')::date
$$;
GRANT EXECUTE ON FUNCTION hoy_peru() TO plataforma_app;

-- 'cobrar': una fila por factura o boleta al crédito; 'pagar': por compra al crédito.
-- vence = la primera cuota que todavía no se cubre (pagado + notas de crédito);
-- pagada del todo, la última. estado: vencida | porvencer (7 días) | aldia | pagada.
-- Solo la empresa abierta (ctx_empresa), y con los permisos de quien llama (RLS incluida).
CREATE OR REPLACE FUNCTION deudas_filas(p_lado text)
RETURNS TABLE (origen text, id uuid, nombre text, tipo_doc text, numero_doc text, comprobante text, tipo text, clase text,
               emitido date, vence date, cuota int, cuotas int, moneda text, total numeric, nc numeric, pagado numeric,
               saldo numeric, estado text, dias int, ultimo date)
LANGUAGE sql STABLE AS $$
    WITH pgd AS (
        SELECT p.documento_id AS id, sum(p.monto) AS pagado, max(p.fecha) AS ultimo
          FROM deuda_pagos p WHERE p.empresa_id = ctx_empresa() AND p.lado = 'cobrar' AND p.anulado_en IS NULL AND p.activo GROUP BY 1),
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
           AND d.estado IN ('encolado', 'aceptado', 'observado')),
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

COMMENT ON FUNCTION deudas_filas(text) IS '090: la lista de Cobros y pagos (cobrar | pagar), con lo que falta, la fecha que vence y el estado.';
GRANT EXECUTE ON FUNCTION deudas_filas(text) TO plataforma_app;

-- --- Permisos -----------------------------------------------------------------
INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('deudas.ver',       'facturacion', 'Ver lo que te deben y lo que debes (Cobros y pagos)'),
    ('deudas.registrar', 'facturacion', 'Anotar y anular pagos de lo que te deben y de lo que debes')
ON CONFLICT (codigo) DO NOTHING;

-- Los mismos roles que Compras y gastos (administrador y contabilidad), y
-- quien ve las compras (solo lectura) ve también esto.
INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, p.codigo
  FROM roles r
 CROSS JOIN (VALUES ('deudas.ver'), ('deudas.registrar')) AS p (codigo)
 WHERE (r.id IN ('11111111-1111-1111-1111-111111111111', '55555555-5555-5555-5555-555555555555')
        OR r.basado_en = '55555555-5555-5555-5555-555555555555')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = p.codigo);

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, 'deudas.ver'
  FROM roles r
 WHERE (r.id = '33333333-3333-3333-3333-333333333333' OR r.basado_en = '33333333-3333-3333-3333-333333333333')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = 'deudas.ver');

-- --- El menú en el armador de planes -----------------------------------------
INSERT INTO funciones (codigo, menu, menu_orden, grupo, nombre, orden, plan_minimo, fija, oculta, es_nueva, modulo, rutas_web, rutas_api)
VALUES
    ('deudas_cobrar', 'Cobros y pagos', 45, NULL, 'Por cobrar', 1, 'pro', false, false, true, NULL,
        ARRAY['/cobros/por-cobrar'], ARRAY['/api/deudas']),
    ('deudas_pagar',  'Cobros y pagos', 45, NULL, 'Por pagar',  2, 'pro', false, false, true, NULL,
        ARRAY['/cobros/por-pagar'], ARRAY[]::text[])
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('090_cobros_y_pagos')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '090: cobros y pagos (por cobrar y por pagar, con pagos parciales). Nada se borró.';
END $$;