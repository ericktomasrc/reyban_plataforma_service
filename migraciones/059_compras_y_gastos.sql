-- =============================================================================
-- 059 — COMPRAS Y GASTOS (e178, 25 oct 2026)
--
-- Una pantalla propia para anotar lo que la empresa compra y gasta, con o sin
-- inventario: la factura del proveedor (RUC, tipo, serie, número, fecha, base,
-- IGV, total) y los gastos (alquiler, luz, sueldos, movilidad…). Es lo que
-- después usan el Registro de Compras, el margen y el estado de resultados.
--
-- Con inventario, la mercadería además se ingresa al stock (Inventario ›
-- Ingreso por compra); la compra puede quedar enlazada a ese ingreso.
--
-- Una compra no se borra: se anula, con motivo.
--
-- Permisos: compras.ver y compras.registrar. Administrador y Jefe de
-- sucursal, los dos; Consulta, ver.
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS compras (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id         uuid          NOT NULL REFERENCES empresas (id),
    sucursal_id        uuid          REFERENCES sucursales (id),

    -- «compra» (con comprobante de un proveedor) o «gasto» (puede no tenerlo).
    clase              text          NOT NULL DEFAULT 'compra',
    fecha_emision      date          NOT NULL,

    proveedor_id       uuid          REFERENCES proveedores (id),
    proveedor_tipo_doc char(1),
    proveedor_doc      text,
    proveedor_nombre   text,

    -- 01 factura · 03 boleta · 02 recibo por honorarios · 12 ticket ·
    -- 14 recibo de servicios públicos · 00 otro (sin comprobante).
    comprobante_tipo   text          NOT NULL DEFAULT '01',
    serie              text,
    numero             text,

    moneda             char(3)       NOT NULL DEFAULT 'PEN',
    tipo_cambio        numeric(10,4),
    base               numeric(14,2) NOT NULL DEFAULT 0,
    igv                numeric(14,2) NOT NULL DEFAULT 0,
    no_gravado         numeric(14,2) NOT NULL DEFAULT 0,
    total              numeric(14,2) NOT NULL,

    categoria          text          NOT NULL DEFAULT 'mercaderia',
    descripcion        text,

    -- Con inventario: el ingreso por compra que la llevó al stock.
    inv_operacion_id   uuid          REFERENCES inv_operaciones (id),

    anulada_en         timestamptz,
    anulada_motivo     text,

    creado_en          timestamptz   NOT NULL DEFAULT now(),
    creado_por         uuid,
    modificado_en      timestamptz,
    modificado_por     uuid,
    activo             boolean       NOT NULL DEFAULT true,
    version            integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_compras_clase CHECK (clase IN ('compra', 'gasto')),
    CONSTRAINT ck_compras_tipo CHECK (comprobante_tipo IN ('01', '03', '02', '12', '14', '00')),
    CONSTRAINT ck_compras_moneda CHECK (moneda IN ('PEN', 'USD')),
    CONSTRAINT ck_compras_tc CHECK (moneda = 'PEN' OR tipo_cambio > 0),
    CONSTRAINT ck_compras_montos CHECK (base >= 0 AND igv >= 0 AND no_gravado >= 0 AND total > 0
                                        AND abs(base + igv + no_gravado - total) <= 0.05),
    CONSTRAINT ck_compras_categoria CHECK (categoria IN ('mercaderia', 'insumos', 'activos', 'alquiler', 'servicios',
                                                         'sueldos', 'transporte', 'impuestos', 'publicidad', 'otros')),
    CONSTRAINT ck_compras_comprobante CHECK (comprobante_tipo = '00' OR (serie IS NOT NULL AND numero IS NOT NULL
                                             AND proveedor_doc IS NOT NULL AND proveedor_nombre IS NOT NULL)),
    CONSTRAINT ck_compras_anulada CHECK (anulada_en IS NULL OR length(btrim(coalesce(anulada_motivo, ''))) >= 3)
);

-- El mismo comprobante del mismo proveedor no se anota dos veces.
CREATE UNIQUE INDEX IF NOT EXISTS ux_compras_comprobante
    ON compras (empresa_id, proveedor_doc, comprobante_tipo, upper(serie), ltrim(numero, '0'))
 WHERE anulada_en IS NULL AND comprobante_tipo <> '00';
CREATE INDEX IF NOT EXISTS ix_compras_fecha ON compras (empresa_id, fecha_emision);

COMMENT ON TABLE compras IS '059: compras y gastos de la empresa (con o sin inventario). No se borran: se anulan.';

ALTER TABLE compras ENABLE ROW LEVEL SECURITY;
ALTER TABLE compras FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_compras ON compras;
CREATE POLICY p_compras ON compras
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_compras') THEN
        PERFORM vigilar_tabla('compras');
    END IF;
END $$;

-- 056: el nombre del proveedor y la descripción, en mayúsculas.
CREATE OR REPLACE TRIGGER a_mayusculas_compras BEFORE INSERT OR UPDATE ON compras
    FOR EACH ROW EXECUTE FUNCTION fn_mayusculas('proveedor_nombre', 'descripcion', 'serie');

GRANT SELECT, INSERT, UPDATE ON compras TO plataforma_app;

-- --- Permisos ------------------------------------------------------------------

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('compras.ver',       'facturacion', 'Ver las compras y los gastos'),
    ('compras.registrar', 'facturacion', 'Registrar, corregir y anular compras y gastos')
ON CONFLICT (codigo) DO NOTHING;

-- Administrador y Jefe de sucursal (y los roles basados en él): los dos.
INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, p.codigo
  FROM roles r
 CROSS JOIN (VALUES ('compras.ver'), ('compras.registrar')) AS p (codigo)
 WHERE (r.id IN ('11111111-1111-1111-1111-111111111111', '55555555-5555-5555-5555-555555555555')
        OR r.basado_en = '55555555-5555-5555-5555-555555555555')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = p.codigo);

-- Consulta: ver.
INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, 'compras.ver'
  FROM roles r
 WHERE (r.id = '33333333-3333-3333-3333-333333333333' OR r.basado_en = '33333333-3333-3333-3333-333333333333')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = 'compras.ver');

INSERT INTO migraciones_aplicadas (nombre) VALUES ('059_compras_y_gastos')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '059: compras y gastos (pantalla propia, con o sin inventario). Nada se borró.';
END $$;