-- =============================================================================
-- 069 — PLANES (e197, 31 oct 2026)
--
-- Básico, Pro y Max para la Cuenta Negocio y para cada empresa de la Cartera
-- (Cuenta Profesional). Lo acordado está en «planes-y-cobros.md».
--
--   · plataforma_ajustes   una sola fila: el INTERRUPTOR GENERAL (planes
--                          encendidos o apagados), los días del mes gratis y
--                          las reglas del mes gratis por RUC nuevo.
--                          NULL = todavía no decidido: al arrancar, la API lo
--                          enciende en producción (PLATAFORMA_SUNAT=real) y lo
--                          deja apagado en QA y en la PC de desarrollo.
--   · planes               Básico / Pro / Max: precios (el real y el de
--                          lanzamiento), sucursales y la subida en la Cartera.
--   · funciones            el ARMADOR: cada submenú con su plan mínimo (NULL =
--                          «Sin plan»: nadie lo ve), sus pantallas y su API.
--   · cartera_tramos       el precio escalonado de la Cartera.
--   · cuenta_ajustes       el trato especial de una cuenta (cortesía, meses
--                          gratis, precio especial, plan mejor, más prueba),
--                          siempre con fecha de fin y motivo.
--   · ruc_mes_gratis       un RUC tiene un solo mes gratis en la vida.
--   · cuentas              + cupos de la Cartera, fin de la prueba y «acceso
--                          total» (cuentas de prueba).
--   · empresas.plan        ya existía (002); ahora vale basico/pro/max.
--                          + fin de su mes gratis y «es mi estudio».
--
-- plan_efectivo(empresa):  'todo' (planes apagados o acceso total: lo ve todo,
-- hasta lo que está «Sin plan»), 'max' (en su mes gratis o con prueba
-- ampliada) o el mayor entre su plan y una cortesía o plan mejor vigente.
--
-- Nunca se quita un módulo contratado ni se borra nada: bajar de plan solo
-- esconde menús y la API responde 402 en lo que el plan no trae.
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- -----------------------------------------------------------------------------
-- 1. AJUSTES DE LA PLATAFORMA (una fila)
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS plataforma_ajustes (
    id                       smallint     PRIMARY KEY DEFAULT 1,
    planes_activos           boolean,
    dias_prueba              integer      NOT NULL DEFAULT 30,
    prueba_cartera_empresas  integer      NOT NULL DEFAULT 10,
    ruc_gratis_activo        boolean      NOT NULL DEFAULT true,
    ruc_gratis_tope          integer      NOT NULL DEFAULT 5,
    cartera_minimo           integer      NOT NULL DEFAULT 2,
    cartera_precio_regular   numeric(10,2) NOT NULL DEFAULT 15,
    oferta_hasta             date         DEFAULT DATE '2027-03-31',
    anual_meses_gratis       integer      NOT NULL DEFAULT 2,
    planes_cambiado_en       timestamptz,
    planes_cambiado_por      uuid,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_plataforma_ajustes_uno   CHECK (id = 1),
    CONSTRAINT ck_plataforma_ajustes_dias  CHECK (dias_prueba BETWEEN 0 AND 365),
    CONSTRAINT ck_plataforma_ajustes_cart  CHECK (prueba_cartera_empresas BETWEEN 0 AND 1000),
    CONSTRAINT ck_plataforma_ajustes_tope  CHECK (ruc_gratis_tope BETWEEN 0 AND 100),
    CONSTRAINT ck_plataforma_ajustes_min   CHECK (cartera_minimo BETWEEN 1 AND 100),
    CONSTRAINT ck_plataforma_ajustes_reg   CHECK (cartera_precio_regular >= 0),
    CONSTRAINT ck_plataforma_ajustes_anual CHECK (anual_meses_gratis BETWEEN 0 AND 11)
);
COMMENT ON COLUMN plataforma_ajustes.planes_activos IS
    '069: el interruptor general. NULL = aún no decidido (la API lo fija al arrancar: encendido solo en producción).';

-- -----------------------------------------------------------------------------
-- 2. PLANES
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS planes (
    codigo                 text         PRIMARY KEY,
    nombre                 text         NOT NULL,
    orden                  integer      NOT NULL,
    para_quien             text,
    precio_regular         numeric(10,2) NOT NULL,
    precio_oferta          numeric(10,2),
    sucursales_max         integer,
    cartera_extra_regular  numeric(10,2) NOT NULL DEFAULT 0,
    cartera_extra_oferta   numeric(10,2),

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_planes_codigo  CHECK (codigo IN ('basico', 'pro', 'max')),
    CONSTRAINT ck_planes_precios CHECK (precio_regular >= 0 AND (precio_oferta IS NULL OR precio_oferta >= 0)
                                        AND cartera_extra_regular >= 0
                                        AND (cartera_extra_oferta IS NULL OR cartera_extra_oferta >= 0)),
    CONSTRAINT ck_planes_suc     CHECK (sucursales_max IS NULL OR sucursales_max >= 1)
);

INSERT INTO planes (codigo, nombre, orden, para_quien, precio_regular, precio_oferta, sucursales_max,
                    cartera_extra_regular, cartera_extra_oferta) VALUES
    ('basico', 'Básico', 1, 'Servicios e independientes',                45, 25, 1,     0,  0),
    ('pro',    'Pro',    2, 'Bodegas, ferreterías, distribuidoras',      69, 39, 3,    10,  8),
    ('max',    'Max',    3, 'Tiendas con caja, farmacias, varios locales', 89, 55, NULL, 20, 15)
ON CONFLICT (codigo) DO NOTHING;

-- -----------------------------------------------------------------------------
-- 3. FUNCIONES (el armador)
--
-- Una fila por submenú. «Prefijo» de ruta: /inventario/lotes vale también para
-- /inventario/lotes/123. Gana el prefijo MÁS LARGO entre los permitidos y los
-- bloqueados: así «Stock» (/inventario) puede estar en Pro y «Lotes»
-- (/inventario/lotes) en Max, o al revés.
--
-- fija:   siempre en Básico, el armador no la mueve (sin ella no se trabaja).
-- oculta: no sale en el armador (lo interno que otras pantallas necesitan,
--         como el stock disponible al vender).
-- modulo: el módulo que necesita (inventario, pos): al quedar visible, la
--         empresa lo recibe contratado. Nunca se quita.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS funciones (
    codigo       text     PRIMARY KEY,
    menu         text     NOT NULL,
    menu_orden   integer  NOT NULL DEFAULT 0,
    nombre       text     NOT NULL,
    orden        integer  NOT NULL DEFAULT 0,
    plan_minimo  text     REFERENCES planes (codigo),
    fija         boolean  NOT NULL DEFAULT false,
    oculta       boolean  NOT NULL DEFAULT false,
    modulo       text     REFERENCES modulos (codigo),
    rutas_web    text[]   NOT NULL DEFAULT ARRAY[]::text[],
    rutas_api    text[]   NOT NULL DEFAULT ARRAY[]::text[],
    es_nueva     boolean  NOT NULL DEFAULT false,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_funciones_codigo CHECK (codigo ~ '^[a-z_]+$'),
    CONSTRAINT ck_funciones_fija   CHECK (NOT fija OR plan_minimo = 'basico'),
    CONSTRAINT ck_funciones_rutas  CHECK (
        array_to_string(rutas_web, ',') ~ '^(/[a-z0-9/_-]*(,|$))*$'
        AND array_to_string(rutas_api, ',') ~ '^(/api/[a-z0-9/_-]+(,|$))*$')
);

INSERT INTO modulos (codigo, nombre, ruta, orden)
SELECT x.codigo, x.nombre, x.ruta, x.orden
  FROM (VALUES ('inventario', 'Inventario', '/inventario', 20), ('pos', 'Punto de venta', '/pos', 30)) x(codigo, nombre, ruta, orden)
 WHERE NOT EXISTS (SELECT 1 FROM modulos m WHERE m.codigo = x.codigo);

INSERT INTO funciones (codigo, menu, menu_orden, nombre, orden, plan_minimo, fija, oculta, modulo, rutas_web, rutas_api) VALUES
    -- Comprobantes
    ('comprobantes',     'Comprobantes', 10, 'Comprobantes emitidos', 1, 'basico', true,  false, NULL,
        ARRAY['/facturacion/comprobantes'], ARRAY['/api/facturacion/documentos']),
    ('notas_credito',    'Comprobantes', 10, 'Notas de crédito',      2, 'basico', false, false, NULL,
        ARRAY['/facturacion/notas/nueva-credito'], ARRAY[]::text[]),
    ('notas_debito',     'Comprobantes', 10, 'Notas de débito',       3, 'basico', false, false, NULL,
        ARRAY['/facturacion/notas/nueva-debito'], ARRAY[]::text[]),
    -- Guías
    ('guias',            'Guías de remisión', 20, 'Guías de remisión', 1, 'basico', false, false, NULL,
        ARRAY['/guias'], ARRAY['/api/guias']),
    -- Clientes y productos
    ('clientes',         'Clientes y productos', 30, 'Clientes',   1, 'basico', true, false, NULL,
        ARRAY['/facturacion/clientes'], ARRAY['/api/facturacion/clientes']),
    ('productos',        'Clientes y productos', 30, 'Productos',  2, 'basico', true, false, NULL,
        ARRAY['/facturacion/productos'], ARRAY['/api/facturacion/productos']),
    ('sucursales',       'Clientes y productos', 30, 'Sucursales', 3, 'basico', true, false, NULL,
        ARRAY['/facturacion/sucursales'], ARRAY['/api/facturacion/sucursales']),
    -- Compras y gastos
    ('compras',          'Compras y gastos', 40, 'Compras y gastos (y su reporte)', 1, 'pro', false, false, NULL,
        ARRAY['/compras', '/reportes/compras'], ARRAY['/api/compras', '/api/reportes/compras']),
    -- Inventario
    ('inv_stock',        'Inventario', 50, 'Stock, kardex y configuración', 1, 'pro', false, false, 'inventario',
        ARRAY['/inventario'], ARRAY['/api/inventario']),
    ('inv_ingresos',     'Inventario', 50, 'Ingresos por compra', 2, 'pro', false, false, 'inventario',
        ARRAY['/inventario/ingresos'], ARRAY['/api/inventario/ingresos']),
    ('inv_traslados',    'Inventario', 50, 'Traslados',           3, 'pro', false, false, 'inventario',
        ARRAY['/inventario/traslados'], ARRAY['/api/inventario/traslados']),
    ('inv_ajustes',      'Inventario', 50, 'Ajustes y conteos',   4, 'pro', false, false, 'inventario',
        ARRAY['/inventario/ajustes'], ARRAY['/api/inventario/ajustes', '/api/inventario/conteos']),
    ('inv_proveedores',  'Inventario', 50, 'Proveedores y categorías', 5, 'pro', false, false, 'inventario',
        ARRAY['/inventario/proveedores'], ARRAY['/api/inventario/proveedores', '/api/inventario/categorias']),
    ('inv_lotes',        'Inventario', 50, 'Lotes y vencimientos', 6, 'max', false, false, 'inventario',
        ARRAY['/inventario/lotes'], ARRAY['/api/inventario/lotes']),
    ('inv_series',       'Inventario', 50, 'Números de serie',    7, 'max', false, false, 'inventario',
        ARRAY['/inventario/series'], ARRAY['/api/inventario/series']),
    ('inv_almacenes',    'Inventario', 50, 'Almacenes',           8, 'max', false, false, 'inventario',
        ARRAY['/inventario/almacenes'], ARRAY['/api/inventario/almacenes']),
    -- Lo que la venta necesita aunque el plan no traiga inventario.
    ('inv_al_vender',    'Inventario', 50, 'Stock al vender (interno)', 99, 'basico', true, true, NULL,
        ARRAY[]::text[], ARRAY['/api/inventario/disponible', '/api/inventario/lotes/para-vender', '/api/inventario/estado']),
    -- Punto de venta
    ('pos_vender',       'Punto de venta', 60, 'Vender (caja)',  1, 'max', false, false, 'pos',
        ARRAY['/pos'], ARRAY['/api/pos']),
    ('pos_cajas',        'Punto de venta', 60, 'Cajas y arqueos', 2, 'max', false, false, 'pos',
        ARRAY['/pos/cajas'], ARRAY['/api/pos/cajas']),
    -- Reportes
    ('rep_mi_dia',       'Reportes', 70, 'Mi día',                     1, 'basico', false, false, NULL,
        ARRAY['/reportes/mi-dia'], ARRAY['/api/reportes/mi-dia']),
    ('rep_ventas',       'Reportes', 70, 'Tablero de ventas',          2, 'basico', false, false, NULL,
        ARRAY['/reportes/ventas'], ARRAY['/api/reportes/ventas']),
    ('rep_clientes',     'Reportes', 70, 'Ventas por cliente',         3, 'basico', false, false, NULL,
        ARRAY['/reportes/clientes'], ARRAY['/api/reportes/clientes']),
    ('rep_vendedores',   'Reportes', 70, 'Ventas por vendedor',        4, 'basico', false, false, NULL,
        ARRAY['/reportes/vendedores'], ARRAY['/api/reportes/vendedores']),
    ('rep_sucursales',   'Reportes', 70, 'Ventas por sucursal',        5, 'basico', false, false, NULL,
        ARRAY['/reportes/sucursales'], ARRAY['/api/reportes/sucursales']),
    ('rep_igv',          'Reportes', 70, 'Resumen de IGV',             6, 'basico', false, false, NULL,
        ARRAY['/reportes/igv'], ARRAY['/api/reportes/igv']),
    ('rep_registro',     'Reportes', 70, 'Registro de Ventas',         7, 'basico', false, false, NULL,
        ARRAY['/reportes/registro'], ARRAY['/api/reportes/registro']),
    ('rep_problemas',    'Reportes', 70, 'Comprobantes con problema',  8, 'basico', false, false, NULL,
        ARRAY['/reportes/problemas'], ARRAY['/api/reportes/problemas']),
    ('rep_anulados',     'Reportes', 70, 'Anulados, notas y por persona', 9, 'basico', false, false, NULL,
        ARRAY['/reportes/anulados', '/reportes/personas-notas'], ARRAY['/api/reportes/anulados', '/api/reportes/personas-notas']),
    ('rep_descarga',     'Reportes', 70, 'Descarga masiva',           10, 'basico', false, false, NULL,
        ARRAY['/reportes/descarga'], ARRAY['/api/reportes/descarga']),
    ('rep_productos',    'Reportes', 70, 'Más vendidos (ventas por producto)', 11, 'pro', false, false, NULL,
        ARRAY['/reportes/productos'], ARRAY['/api/reportes/productos']),
    ('rep_comparar',     'Reportes', 70, 'Comparar periodos',         12, 'pro', false, false, NULL,
        ARRAY['/reportes/comparar'], ARRAY[]::text[]),
    ('rep_ganancias',    'Reportes', 70, 'Ganancias: margen y resultados', 13, 'pro', false, false, NULL,
        ARRAY['/reportes/margen', '/reportes/resultados'], ARRAY['/api/reportes/margen', '/api/reportes/resultados']),
    ('rep_valorizado',   'Reportes', 70, 'Inventario valorizado',     14, 'pro', false, false, 'inventario',
        ARRAY['/reportes/valorizado'], ARRAY['/api/reportes/valorizado']),
    ('rep_pos',          'Reportes', 70, 'Reportes de caja',          15, 'max', false, false, 'pos',
        ARRAY['/reportes/pos'], ARRAY['/api/reportes/pos']),
    -- Personal y configuración
    ('personal',         'Personal', 80, 'Personal, roles y cargos', 1, 'basico', true, false, NULL,
        ARRAY['/usuarios'], ARRAY['/api/usuarios', '/api/roles', '/api/cargos']),
    ('auditoria',        'Personal', 80, 'Auditoría',               2, 'basico', true, false, NULL,
        ARRAY['/auditoria'], ARRAY['/api/auditoria']),
    ('configuracion',    'Personal', 80, 'Configuración de la empresa', 3, 'basico', true, false, NULL,
        ARRAY['/configurar'], ARRAY['/api/configuracion'])
ON CONFLICT (codigo) DO NOTHING;

-- -----------------------------------------------------------------------------
-- 4. TRAMOS DE LA CARTERA (precio graduado: cada empresa paga el de su tramo)
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS cartera_tramos (
    desde          integer       PRIMARY KEY,
    hasta          integer,
    precio_oferta  numeric(10,2) NOT NULL,
    precio_regular numeric(10,2) NOT NULL,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cartera_tramos CHECK (desde >= 1 AND (hasta IS NULL OR hasta >= desde)
                                        AND precio_oferta >= 0 AND precio_regular >= 0)
);
INSERT INTO cartera_tramos (desde, hasta, precio_oferta, precio_regular) VALUES
    (1, 5, 10, 15), (6, 10, 9, 15), (11, 20, 8, 15), (21, 50, 7, 15), (51, NULL, 6, 15)
ON CONFLICT (desde) DO NOTHING;

-- -----------------------------------------------------------------------------
-- 5. CUENTAS Y EMPRESAS
-- -----------------------------------------------------------------------------
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS cartera_cupos        integer;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS prueba_hasta         timestamptz;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS acceso_total         boolean NOT NULL DEFAULT false;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS acceso_total_motivo  text;
COMMENT ON COLUMN cuentas.cartera_cupos IS '069: empresas que paga la Cartera (se pagan aunque no se usen). NULL = aún no eligió.';
COMMENT ON COLUMN cuentas.prueba_hasta  IS '069: fin del mes gratis de la cuenta. Empieza al encender los planes (o al registrarse con los planes encendidos).';
COMMENT ON COLUMN cuentas.acceso_total  IS '069: cuenta de prueba: lo ve todo y no paga.';

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cuentas_cupos') THEN
        ALTER TABLE cuentas ADD CONSTRAINT ck_cuentas_cupos CHECK (cartera_cupos IS NULL OR cartera_cupos BETWEEN 1 AND 10000);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cuentas_acceso_total') THEN
        ALTER TABLE cuentas ADD CONSTRAINT ck_cuentas_acceso_total
            CHECK (NOT acceso_total OR length(btrim(COALESCE(acceso_total_motivo, ''))) BETWEEN 3 AND 300);
    END IF;
END $$;

-- empresas.plan existe desde la 002 con 'basico'. Lo que no sea un plan, a Básico.
UPDATE empresas SET plan = 'basico' WHERE plan IS DISTINCT FROM 'basico' AND plan NOT IN ('pro', 'max');
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS prueba_hasta   timestamptz;
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS prueba_motivo  text;
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS es_del_estudio boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN empresas.plan           IS '069: basico · pro · max (lo que paga). Lo que ve: plan_efectivo().';
COMMENT ON COLUMN empresas.prueba_hasta   IS '069: fin de su mes gratis (el de la cuenta o el de RUC nuevo).';
COMMENT ON COLUMN empresas.es_del_estudio IS '069: la empresa del propio estudio: gratis en Básico y fuera del cupo.';

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_empresas_plan') THEN
        ALTER TABLE empresas ADD CONSTRAINT ck_empresas_plan CHECK (plan IN ('basico', 'pro', 'max'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_empresas_prueba_motivo') THEN
        ALTER TABLE empresas ADD CONSTRAINT ck_empresas_prueba_motivo
            CHECK (prueba_motivo IS NULL OR prueba_motivo IN ('cuenta', 'ruc_nuevo', 'ajuste'));
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_empresas_del_estudio ON empresas (cuenta_id) WHERE es_del_estudio;

-- -----------------------------------------------------------------------------
-- 6. AJUSTES DE UNA CUENTA (trato especial) y MES GRATIS POR RUC
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS cuenta_ajustes (
    id             uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    cuenta_id      uuid          NOT NULL REFERENCES cuentas (id),
    tipo           text          NOT NULL,
    plan_codigo    text          REFERENCES planes (codigo),
    meses          integer,
    precio         numeric(10,2),
    hasta          date          NOT NULL,
    motivo         text          NOT NULL,
    anulado_en     timestamptz,
    anulado_motivo text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cuenta_ajustes_tipo   CHECK (tipo IN ('cortesia', 'meses_gratis', 'precio_especial', 'plan_mejor', 'ampliar_prueba')),
    CONSTRAINT ck_cuenta_ajustes_motivo CHECK (length(btrim(motivo)) BETWEEN 5 AND 500),
    CONSTRAINT ck_cuenta_ajustes_datos  CHECK (
        (tipo IN ('cortesia', 'plan_mejor') AND plan_codigo IS NOT NULL)
     OR (tipo = 'meses_gratis'    AND meses BETWEEN 1 AND 24)
     OR (tipo = 'precio_especial' AND precio >= 0)
     OR (tipo = 'ampliar_prueba')),
    CONSTRAINT ck_cuenta_ajustes_anulado CHECK (
        anulado_en IS NULL OR length(btrim(COALESCE(anulado_motivo, ''))) BETWEEN 3 AND 500)
);
CREATE INDEX IF NOT EXISTS ix_cuenta_ajustes_cuenta ON cuenta_ajustes (cuenta_id, hasta);

CREATE TABLE IF NOT EXISTS ruc_mes_gratis (
    ruc            text        PRIMARY KEY,
    cuenta_id      uuid        REFERENCES cuentas (id),
    empresa_id     uuid        REFERENCES empresas (id),
    motivo         text        NOT NULL,
    hasta          timestamptz,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_ruc_mes_gratis_ruc    CHECK (ruc ~ '^[0-9]{11}$'),
    CONSTRAINT ck_ruc_mes_gratis_motivo CHECK (motivo IN ('cuenta', 'ruc_nuevo', 'ajuste'))
);
CREATE INDEX IF NOT EXISTS ix_ruc_mes_gratis_cuenta ON ruc_mes_gratis (cuenta_id, creado_en);

-- -----------------------------------------------------------------------------
-- 7. AISLAMIENTO Y BITÁCORA
--
-- Los catálogos (ajustes, planes, funciones, tramos) los lee cualquiera con
-- sesión —la pantalla «Elige tu plan» los necesita— y los escribe solo el
-- super administrador. Los ajustes de una cuenta y sus meses gratis: cada
-- cuenta ve los suyos; escribir, solo el super administrador o las funciones.
-- -----------------------------------------------------------------------------
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['plataforma_ajustes', 'planes', 'funciones', 'cartera_tramos'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = t AND policyname = 'p_' || t) THEN
            EXECUTE format('CREATE POLICY %I ON %I USING (true) WITH CHECK (ctx_es_super())', 'p_' || t, t);
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_' || t) THEN
            PERFORM vigilar_tabla(t);
        END IF;
    END LOOP;

    FOREACH t IN ARRAY ARRAY['cuenta_ajustes', 'ruc_mes_gratis'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = t AND policyname = 'p_' || t) THEN
            EXECUTE format($p$CREATE POLICY %I ON %I
                USING (ctx_es_super() OR cuenta_id = (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario()))
                WITH CHECK (ctx_es_super())$p$, 'p_' || t, t);
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_' || t) THEN
            PERFORM vigilar_tabla(t);
        END IF;
    END LOOP;
END $$;

-- Las que el delete no necesita: nada se borra.
GRANT SELECT, INSERT, UPDATE ON plataforma_ajustes, planes, funciones, cartera_tramos, cuenta_ajustes TO plataforma_app;
GRANT SELECT ON ruc_mes_gratis TO plataforma_app;

INSERT INTO plataforma_ajustes (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('plataforma.planes_ver',        'plataforma', 'Planes y cobros: ver las cuentas, su plan y registrar pagos'),
    ('plataforma.planes_descuentos', 'plataforma', 'Planes y cobros: dar descuentos, cortesías, acceso total y cambiar el plan de una cuenta'),
    ('plataforma.planes_armar',      'plataforma', 'Planes y cobros: armar los planes, precios, tramos y el interruptor general')
ON CONFLICT (codigo) DO NOTHING;

-- -----------------------------------------------------------------------------
-- 8. LAS FUNCIONES
-- -----------------------------------------------------------------------------

-- ¿Planes encendidos? NULL (aún no decidido) = apagados.
CREATE OR REPLACE FUNCTION planes_encendidos() RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE((SELECT planes_activos FROM plataforma_ajustes WHERE id = 1), false);
$$;

-- El orden de un plan; 'todo' por encima de todos.
CREATE OR REPLACE FUNCTION plan_orden(p_plan text) RETURNS integer
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT CASE WHEN p_plan = 'todo' THEN 1000
                ELSE COALESCE((SELECT orden FROM planes WHERE codigo = p_plan), 0) END;
$$;

-- LO QUE VE UNA EMPRESA HOY.
CREATE OR REPLACE FUNCTION plan_efectivo(p_empresa uuid) RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_plan   text;
    v_prueba timestamptz;
    v_cuenta uuid;
    v_total  boolean;
    v_mejor  text;
BEGIN
    IF NOT planes_encendidos() THEN RETURN 'todo'; END IF;

    SELECT e.plan, e.prueba_hasta, e.cuenta_id, COALESCE(c.acceso_total, false)
      INTO v_plan, v_prueba, v_cuenta, v_total
      FROM empresas e LEFT JOIN cuentas c ON c.id = e.cuenta_id
     WHERE e.id = p_empresa;
    IF NOT FOUND THEN RETURN 'basico'; END IF;

    IF v_total THEN RETURN 'todo'; END IF;
    IF v_prueba > now() THEN RETURN 'max'; END IF;
    IF EXISTS (SELECT 1 FROM cuenta_ajustes a
                WHERE a.cuenta_id = v_cuenta AND a.activo AND a.anulado_en IS NULL
                  AND a.tipo = 'ampliar_prueba' AND a.hasta >= current_date) THEN
        RETURN 'max';
    END IF;

    SELECT a.plan_codigo INTO v_mejor
      FROM cuenta_ajustes a JOIN planes p ON p.codigo = a.plan_codigo
     WHERE a.cuenta_id = v_cuenta AND a.activo AND a.anulado_en IS NULL
       AND a.tipo IN ('cortesia', 'plan_mejor') AND a.hasta >= current_date
     ORDER BY p.orden DESC LIMIT 1;

    IF v_mejor IS NOT NULL AND plan_orden(v_mejor) > plan_orden(COALESCE(v_plan, 'basico')) THEN
        RETURN v_mejor;
    END IF;
    RETURN COALESCE(v_plan, 'basico');
END $$;

-- ¿Una función se ve con este plan?
CREATE OR REPLACE FUNCTION plan_ve_funcion(p_plan text, p_minimo text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT p_plan = 'todo' OR (p_minimo IS NOT NULL AND plan_orden(p_minimo) <= plan_orden(p_plan));
$$;

-- LO QUE EL PLAN DE UNA EMPRESA NO TRAE, y lo que sí (para el prefijo más
-- largo). Vacío con 'todo'.
CREATE OR REPLACE FUNCTION planes_rutas(p_empresa uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_plan text := plan_efectivo(p_empresa);
BEGIN
    IF v_plan = 'todo' THEN
        RETURN jsonb_build_object('plan', v_plan, 'web', '[]'::jsonb, 'api', '[]'::jsonb,
                                  'libresWeb', '[]'::jsonb, 'libresApi', '[]'::jsonb);
    END IF;
    RETURN (
        SELECT jsonb_build_object(
            'plan', v_plan,
            'web',       COALESCE(jsonb_agg(DISTINCT r.ruta) FILTER (WHERE r.web AND NOT r.ve), '[]'::jsonb),
            'api',       COALESCE(jsonb_agg(DISTINCT r.ruta) FILTER (WHERE NOT r.web AND NOT r.ve), '[]'::jsonb),
            'libresWeb', COALESCE(jsonb_agg(DISTINCT r.ruta) FILTER (WHERE r.web AND r.ve), '[]'::jsonb),
            'libresApi', COALESCE(jsonb_agg(DISTINCT r.ruta) FILTER (WHERE NOT r.web AND r.ve), '[]'::jsonb),
            -- El plan mínimo de cada pantalla bloqueada, para decir «esto es del Pro».
            'minimos', COALESCE(jsonb_object_agg(r.ruta, COALESCE(r.plan_minimo, 'sin')) FILTER (WHERE NOT r.ve), '{}'::jsonb))
          FROM (SELECT x.ruta, true AS web, plan_ve_funcion(v_plan, f.plan_minimo) AS ve, f.plan_minimo
                  FROM funciones f, unnest(f.rutas_web) x(ruta) WHERE f.activo
                UNION ALL
                SELECT x.ruta, false, plan_ve_funcion(v_plan, f.plan_minimo), f.plan_minimo
                  FROM funciones f, unnest(f.rutas_api) x(ruta) WHERE f.activo) r);
END $$;

-- LOS MÓDULOS QUE EL PLAN NECESITA, contratados. Solo agrega: nunca quita
-- (bajar de plan esconde menús; los datos y el módulo quedan). Solo con los
-- planes encendidos o en una cuenta con acceso total.
CREATE OR REPLACE FUNCTION planes_sincronizar_modulos(p_empresa uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_plan text := plan_efectivo(p_empresa);
    v_mod  text;
BEGIN
    -- Con los planes apagados, los módulos los maneja el super administrador
    -- a mano, como siempre; solo una cuenta con acceso total los recibe.
    IF NOT planes_encendidos() AND NOT EXISTS (
        SELECT 1 FROM empresas e JOIN cuentas c ON c.id = e.cuenta_id
         WHERE e.id = p_empresa AND c.acceso_total) THEN
        RETURN;
    END IF;
    FOR v_mod IN
        SELECT DISTINCT f.modulo FROM funciones f
          JOIN modulos m ON m.codigo = f.modulo AND m.disponible
         WHERE f.activo AND f.modulo IS NOT NULL AND f.modulo <> 'facturacion'
           AND plan_ve_funcion(v_plan, f.plan_minimo)
    LOOP
        INSERT INTO empresa_modulo (empresa_id, modulo_codigo, contratado_en, activo)
        VALUES (p_empresa, v_mod, now(), true)
        ON CONFLICT (empresa_id, modulo_codigo)
        DO UPDATE SET activo = true, vence_en = NULL
         WHERE empresa_modulo.activo IS DISTINCT FROM true
            OR (empresa_modulo.vence_en IS NOT NULL AND empresa_modulo.vence_en <= now());
    END LOOP;
END $$;

CREATE OR REPLACE FUNCTION planes_sincronizar_cuenta(p_cuenta uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_e uuid;
BEGIN
    FOR v_e IN SELECT id FROM empresas WHERE cuenta_id = p_cuenta AND activo LOOP
        PERFORM planes_sincronizar_modulos(v_e);
    END LOOP;
END $$;

CREATE OR REPLACE FUNCTION planes_sincronizar_todas() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_e uuid;
BEGIN
    FOR v_e IN SELECT id FROM empresas WHERE activo LOOP
        PERFORM planes_sincronizar_modulos(v_e);
    END LOOP;
END $$;

-- EL MES GRATIS DE UNA EMPRESA NUEVA (se llama antes de guardarla).
--   · Con la cuenta en su mes gratis: Negocio siempre; la Cartera, hasta
--     «prueba_cartera_empresas» empresas.
--   · Pasado ese mes, en la Cartera: un mes gratis por RUC nuevo (un RUC lo
--     tiene una sola vez en la vida), hasta «ruc_gratis_tope» al mes.
CREATE OR REPLACE FUNCTION planes_prueba_empresa_nueva(p_cuenta uuid, p_ruc text, p_excluir uuid)
RETURNS TABLE (hasta timestamptz, motivo text)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    a plataforma_ajustes%ROWTYPE;
    c cuentas%ROWTYPE;
BEGIN
    IF p_cuenta IS NULL OR NOT planes_encendidos() THEN RETURN; END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    SELECT * INTO c FROM cuentas WHERE id = p_cuenta;
    IF NOT FOUND THEN RETURN; END IF;

    IF c.prueba_hasta > now() THEN
        IF c.tipo = 'negocio'
           OR (SELECT count(*) FROM empresas e
                WHERE e.cuenta_id = p_cuenta AND e.prueba_motivo = 'cuenta'
                  AND e.id IS DISTINCT FROM p_excluir) < a.prueba_cartera_empresas THEN
            hasta := c.prueba_hasta; motivo := 'cuenta'; RETURN NEXT; RETURN;
        END IF;
    ELSIF c.tipo = 'profesional' AND a.ruc_gratis_activo AND p_ruc ~ '^[0-9]{11}$'
          AND NOT EXISTS (SELECT 1 FROM ruc_mes_gratis r WHERE r.ruc = p_ruc)
          AND (SELECT count(*) FROM ruc_mes_gratis r
                WHERE r.cuenta_id = p_cuenta AND r.motivo = 'ruc_nuevo'
                  AND r.creado_en >= date_trunc('month', now())) < a.ruc_gratis_tope THEN
        hasta := now() + make_interval(days => a.dias_prueba); motivo := 'ruc_nuevo'; RETURN NEXT; RETURN;
    END IF;
END $$;

-- -----------------------------------------------------------------------------
-- 9. DISPARADORES
-- -----------------------------------------------------------------------------

-- Cuenta nueva con los planes encendidos: empieza su mes gratis.
CREATE OR REPLACE FUNCTION fn_planes_cuenta_nueva() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.prueba_hasta IS NULL AND planes_encendidos() THEN
        NEW.prueba_hasta := now() + make_interval(days => (SELECT dias_prueba FROM plataforma_ajustes WHERE id = 1));
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS b_planes_cuenta_nueva ON cuentas;
CREATE TRIGGER b_planes_cuenta_nueva BEFORE INSERT ON cuentas
    FOR EACH ROW EXECUTE FUNCTION fn_planes_cuenta_nueva();

-- Acceso total cambiado: los módulos que ahora ve.
CREATE OR REPLACE FUNCTION fn_planes_cuenta_cambio() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    PERFORM planes_sincronizar_cuenta(NEW.id);
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS a_planes_cuenta_cambio ON cuentas;
CREATE TRIGGER a_planes_cuenta_cambio AFTER UPDATE OF acceso_total, prueba_hasta ON cuentas
    FOR EACH ROW WHEN (OLD.acceso_total IS DISTINCT FROM NEW.acceso_total
                    OR OLD.prueba_hasta IS DISTINCT FROM NEW.prueba_hasta)
    EXECUTE FUNCTION fn_planes_cuenta_cambio();

-- Empresa nueva: su mes gratis (si le toca).
CREATE OR REPLACE FUNCTION fn_planes_empresa_nueva() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE r record;
BEGIN
    IF NEW.prueba_hasta IS NULL THEN
        SELECT * INTO r FROM planes_prueba_empresa_nueva(NEW.cuenta_id, NEW.ruc, NEW.id);
        IF r.hasta IS NOT NULL THEN
            NEW.prueba_hasta  := r.hasta;
            NEW.prueba_motivo := r.motivo;
        END IF;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS b_planes_empresa_nueva ON empresas;
CREATE TRIGGER b_planes_empresa_nueva BEFORE INSERT ON empresas
    FOR EACH ROW EXECUTE FUNCTION fn_planes_empresa_nueva();

-- Después de guardarla (o de cambiar su plan, su prueba o su RUC): el RUC
-- queda con su mes gratis usado y la empresa recibe los módulos de su plan.
CREATE OR REPLACE FUNCTION fn_planes_empresa_despues() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.prueba_hasta IS NOT NULL AND NEW.ruc ~ '^[0-9]{11}$' THEN
        INSERT INTO ruc_mes_gratis (ruc, cuenta_id, empresa_id, motivo, hasta)
        VALUES (NEW.ruc, NEW.cuenta_id, NEW.id, COALESCE(NEW.prueba_motivo, 'cuenta'), NEW.prueba_hasta)
        ON CONFLICT (ruc) DO NOTHING;
    END IF;
    IF NEW.activo THEN
        PERFORM planes_sincronizar_modulos(NEW.id);
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS a_planes_empresa ON empresas;
CREATE TRIGGER a_planes_empresa AFTER INSERT OR UPDATE OF plan, prueba_hasta, ruc, cuenta_id, activo ON empresas
    FOR EACH ROW EXECUTE FUNCTION fn_planes_empresa_despues();

-- Un ajuste de la cuenta (cortesía, plan mejor, más prueba): sus módulos.
CREATE OR REPLACE FUNCTION fn_planes_ajuste() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    PERFORM planes_sincronizar_cuenta(NEW.cuenta_id);
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS a_planes_ajuste ON cuenta_ajustes;
CREATE TRIGGER a_planes_ajuste AFTER INSERT OR UPDATE ON cuenta_ajustes
    FOR EACH ROW EXECUTE FUNCTION fn_planes_ajuste();

-- EL INTERRUPTOR GENERAL. Al encenderlo, las cuentas sin mes gratis empiezan
-- el suyo HOY (mientras estuvo apagado no corrió) y cada empresa recibe los
-- módulos de lo que ahora ve. Al apagarlo no se toca nada.
CREATE OR REPLACE FUNCTION fn_planes_interruptor() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_dias integer := NEW.dias_prueba;
    v_c    record;
    v_e    record;
BEGIN
    IF NEW.planes_activos IS NOT DISTINCT FROM OLD.planes_activos THEN RETURN NULL; END IF;

    IF NEW.planes_activos THEN
        FOR v_c IN SELECT id FROM cuentas WHERE prueba_hasta IS NULL LOOP
            UPDATE cuentas SET prueba_hasta = now() + make_interval(days => v_dias) WHERE id = v_c.id;
            FOR v_e IN SELECT id, ruc FROM empresas
                        WHERE cuenta_id = v_c.id AND activo AND prueba_hasta IS NULL
                        ORDER BY creado_en LOOP
                UPDATE empresas e SET prueba_hasta = p.hasta, prueba_motivo = p.motivo
                  FROM planes_prueba_empresa_nueva(v_c.id, v_e.ruc, v_e.id) p
                 WHERE e.id = v_e.id AND p.hasta IS NOT NULL;
            END LOOP;
        END LOOP;
        PERFORM planes_sincronizar_todas();
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS a_planes_interruptor ON plataforma_ajustes;
CREATE TRIGGER a_planes_interruptor AFTER UPDATE OF planes_activos ON plataforma_ajustes
    FOR EACH ROW EXECUTE FUNCTION fn_planes_interruptor();

-- El armador movió algo: los módulos de lo que ahora se ve.
CREATE OR REPLACE FUNCTION fn_planes_armador() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    PERFORM planes_sincronizar_todas();
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS a_planes_armador ON funciones;
CREATE TRIGGER a_planes_armador AFTER UPDATE OF plan_minimo ON funciones
    FOR EACH STATEMENT EXECUTE FUNCTION fn_planes_armador();

-- LAS SUCURSALES DEL PLAN (Básico 1, Pro 3, Max ilimitadas), con los planes
-- encendidos. Nunca se desactiva una que ya existe.
CREATE OR REPLACE FUNCTION fn_planes_sucursales() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_plan text;
    v_max  integer;
    v_hay  integer;
BEGIN
    IF NOT NEW.activo OR NOT planes_encendidos() THEN RETURN NEW; END IF;
    IF TG_OP = 'UPDATE' AND OLD.activo THEN RETURN NEW; END IF;

    v_plan := plan_efectivo(NEW.empresa_id);
    IF v_plan = 'todo' THEN RETURN NEW; END IF;
    SELECT sucursales_max INTO v_max FROM planes WHERE codigo = v_plan;
    IF v_max IS NULL THEN RETURN NEW; END IF;

    SELECT count(*) INTO v_hay FROM sucursales
     WHERE empresa_id = NEW.empresa_id AND activo AND id <> NEW.id;
    IF v_hay >= v_max THEN
        RAISE EXCEPTION 'PLAN_SUCURSALES: Tu plan % permite % sucursal(es). Para tener más, sube de plan.',
            (SELECT nombre FROM planes WHERE codigo = v_plan), v_max
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS b_planes_sucursales ON sucursales;
CREATE TRIGGER b_planes_sucursales BEFORE INSERT OR UPDATE OF activo ON sucursales
    FOR EACH ROW EXECUTE FUNCTION fn_planes_sucursales();

-- -----------------------------------------------------------------------------
-- 10. LO QUE HACE LA CUENTA (dueño o colaborador administrador)
-- -----------------------------------------------------------------------------

-- Quién puede cambiar planes y cupos de su cuenta.
CREATE OR REPLACE FUNCTION planes_mi_cuenta_admin() RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT u.cuenta_id FROM usuarios u
     WHERE u.id = ctx_usuario() AND u.activo AND u.cuenta_id IS NOT NULL
       AND u.nivel_cuenta IN ('dueno', 'colaborador_admin');
$$;

-- Elegir el plan de una empresa de la cuenta.
CREATE OR REPLACE FUNCTION planes_elegir(p_empresa uuid, p_plan text) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := planes_mi_cuenta_admin();
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) elige el plan.';
    END IF;
    IF p_plan NOT IN ('basico', 'pro', 'max') THEN
        RAISE EXCEPTION 'PLAN_NO_EXISTE: Ese plan no existe.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM empresas WHERE id = p_empresa AND cuenta_id = v_cuenta AND activo) THEN
        RAISE EXCEPTION 'PLAN_NO_EXISTE: Esa empresa no es de tu cuenta.';
    END IF;
    UPDATE empresas SET plan = p_plan WHERE id = p_empresa;
    RETURN plan_efectivo(p_empresa);
END $$;

-- Los cupos de la Cartera.
CREATE OR REPLACE FUNCTION planes_fijar_cupos(p_cupos integer) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := planes_mi_cuenta_admin();
    v_min    integer := (SELECT cartera_minimo FROM plataforma_ajustes WHERE id = 1);
BEGIN
    IF v_cuenta IS NULL OR (SELECT tipo FROM cuentas WHERE id = v_cuenta) <> 'profesional' THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Los cupos son de la Cuenta Profesional, y los cambia su dueño o un colaborador administrador.';
    END IF;
    IF p_cupos IS NULL OR p_cupos < v_min OR p_cupos > 10000 THEN
        RAISE EXCEPTION 'PLAN_CUPOS: El mínimo de la Cartera es % empresas.', v_min;
    END IF;
    UPDATE cuentas SET cartera_cupos = p_cupos WHERE id = v_cuenta;
    RETURN p_cupos;
END $$;

-- La empresa del propio estudio (gratis en Básico y fuera del cupo). Una por cuenta.
CREATE OR REPLACE FUNCTION planes_marcar_estudio(p_empresa uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := planes_mi_cuenta_admin();
BEGIN
    IF v_cuenta IS NULL OR (SELECT tipo FROM cuentas WHERE id = v_cuenta) <> 'profesional' THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo el dueño del estudio (o un colaborador administrador) lo marca.';
    END IF;
    IF p_empresa IS NOT NULL AND NOT EXISTS (SELECT 1 FROM empresas WHERE id = p_empresa AND cuenta_id = v_cuenta AND activo) THEN
        RAISE EXCEPTION 'PLAN_NO_EXISTE: Esa empresa no es de tu cuenta.';
    END IF;
    UPDATE empresas SET es_del_estudio = false WHERE cuenta_id = v_cuenta AND es_del_estudio AND id IS DISTINCT FROM p_empresa;
    IF p_empresa IS NOT NULL THEN
        UPDATE empresas SET es_del_estudio = true WHERE id = p_empresa;
    END IF;
END $$;

-- Las empresas que ocupan cupo: activas, sin pausa y que no son del estudio.
CREATE OR REPLACE FUNCTION planes_cupos_usados(p_cuenta uuid) RETURNS integer
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT count(*)::integer FROM empresas
     WHERE cuenta_id = p_cuenta AND activo AND pausada_en IS NULL AND NOT es_del_estudio;
$$;

-- Al agregar una empresa a la Cartera: ¿se pasa del cupo? Devuelve null si
-- no (o si no aplica), o los cupos que tendría (+1) si sí.
CREATE OR REPLACE FUNCTION planes_cupo_al_agregar() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid;
    c cuentas%ROWTYPE;
    v_usados integer;
BEGIN
    SELECT u.cuenta_id INTO v_cuenta FROM usuarios u WHERE u.id = ctx_usuario();
    IF v_cuenta IS NULL OR NOT planes_encendidos() THEN RETURN NULL; END IF;
    SELECT * INTO c FROM cuentas WHERE id = v_cuenta;
    IF c.tipo <> 'profesional' OR c.acceso_total OR c.prueba_hasta > now() OR c.cartera_cupos IS NULL THEN
        RETURN NULL;
    END IF;
    v_usados := planes_cupos_usados(v_cuenta);
    IF v_usados < c.cartera_cupos THEN RETURN NULL; END IF;
    RETURN jsonb_build_object('cupos', c.cartera_cupos, 'usados', v_usados, 'nuevos', v_usados + 1,
                              'diasQueFaltan', (date_trunc('month', now()) + interval '1 month')::date - current_date);
END $$;

-- Confirmado por quien la agrega: con la empresa ya creada, el cupo alcanza
-- a las que se usan (la nueva incluida).
CREATE OR REPLACE FUNCTION planes_subir_cupo() RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid;
    v_n integer;
BEGIN
    SELECT u.cuenta_id INTO v_cuenta FROM usuarios u
     WHERE u.id = ctx_usuario() AND u.nivel_cuenta IS NOT NULL;
    IF v_cuenta IS NULL THEN RAISE EXCEPTION 'PLAN_NO_PUEDE: No tienes una cartera.'; END IF;
    UPDATE cuentas SET cartera_cupos = GREATEST(COALESCE(cartera_cupos, 0), planes_cupos_usados(v_cuenta), 1)
     WHERE id = v_cuenta RETURNING cartera_cupos INTO v_n;
    RETURN v_n;
END $$;

-- -----------------------------------------------------------------------------
-- 11. LO QUE SE LEE: el resumen de una cuenta (lo que paga y por qué), la
--     lista del super administrador, el armador y a quién afecta un cambio.
-- -----------------------------------------------------------------------------

-- El precio de hoy de un plan (el de lanzamiento mientras dure la oferta).
CREATE OR REPLACE FUNCTION planes_en_oferta() RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE((SELECT oferta_hasta IS NULL OR current_date <= oferta_hasta FROM plataforma_ajustes WHERE id = 1), true);
$$;

-- Lo que paga la Cartera por n empresas (precio graduado: cada empresa paga
-- el de su tramo). p_regular: con el precio real (el que va tachado).
CREATE OR REPLACE FUNCTION planes_precio_cartera(p_n integer, p_regular boolean) RETURNS numeric
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE(sum(
               GREATEST(0, LEAST(p_n, COALESCE(t.hasta, p_n)) - t.desde + 1)
             * CASE WHEN p_regular THEN t.precio_regular ELSE t.precio_oferta END), 0)
      FROM cartera_tramos t
     WHERE t.activo AND t.desde <= p_n;
$$;

CREATE OR REPLACE FUNCTION planes_resumen_cuenta(p_cuenta uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    c        cuentas%ROWTYPE;
    a        plataforma_ajustes%ROWTYPE;
    v_oferta boolean := planes_en_oferta();
    v_usados integer;
    v_n      integer;
    v_base   numeric := 0;
    v_baser  numeric := 0;
    v_extra  numeric := 0;
    v_extrar numeric := 0;
    v_total  numeric;
    v_estado text;
    v_aj     cuenta_ajustes%ROWTYPE;
    v_emp    jsonb;
    v_ajs    jsonb;
BEGIN
    IF NOT ctx_es_super()
       AND p_cuenta IS DISTINCT FROM (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario()) THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Esa cuenta no es tuya.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT * INTO c FROM cuentas WHERE id = p_cuenta;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;

    SELECT COALESCE(jsonb_agg(jsonb_build_object(
               'id', e.id, 'nombre', e.nombre, 'ruc', e.ruc, 'plan', e.plan,
               'efectivo', plan_efectivo(e.id), 'pruebaHasta', e.prueba_hasta, 'pruebaMotivo', e.prueba_motivo,
               'esDelEstudio', e.es_del_estudio, 'pausada', e.pausada_en IS NOT NULL,
               'suspendida', e.suspendida_en IS NOT NULL)
             ORDER BY e.es_del_estudio DESC, e.nombre), '[]'::jsonb)
      INTO v_emp
      FROM empresas e WHERE e.cuenta_id = p_cuenta AND e.activo;

    SELECT COALESCE(jsonb_agg(jsonb_build_object(
               'id', x.id, 'tipo', x.tipo, 'plan', x.plan_codigo, 'meses', x.meses, 'precio', x.precio,
               'hasta', x.hasta, 'motivo', x.motivo, 'creadoEn', x.creado_en,
               'anuladoEn', x.anulado_en, 'anuladoMotivo', x.anulado_motivo,
               'vigente', x.anulado_en IS NULL AND x.hasta >= current_date)
             ORDER BY x.creado_en DESC), '[]'::jsonb)
      INTO v_ajs
      FROM cuenta_ajustes x WHERE x.cuenta_id = p_cuenta AND x.activo;

    -- Lo que costaría, sin tratos especiales.
    IF c.tipo = 'negocio' THEN
        SELECT COALESCE(sum(CASE WHEN v_oferta THEN COALESCE(p.precio_oferta, p.precio_regular) ELSE p.precio_regular END), 0),
               COALESCE(sum(p.precio_regular), 0)
          INTO v_base, v_baser
          FROM empresas e JOIN planes p ON p.codigo = e.plan
         WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL;
        v_usados := (SELECT count(*) FROM empresas e WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL);
        v_n := v_usados;
    ELSE
        v_usados := planes_cupos_usados(p_cuenta);
        v_n := GREATEST(COALESCE(c.cartera_cupos, v_usados), a.cartera_minimo);
        v_base  := planes_precio_cartera(v_n, NOT v_oferta);
        v_baser := planes_precio_cartera(v_n, true);
        SELECT COALESCE(sum(CASE WHEN v_oferta THEN COALESCE(p.cartera_extra_oferta, p.cartera_extra_regular) ELSE p.cartera_extra_regular END), 0),
               COALESCE(sum(p.cartera_extra_regular), 0)
          INTO v_extra, v_extrar
          FROM empresas e JOIN planes p ON p.codigo = e.plan
         WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL;
    END IF;
    v_total := v_base + v_extra;

    -- El trato especial vigente que manda (el más reciente de cada tipo).
    v_estado := 'al_dia';
    IF NOT planes_encendidos() THEN
        v_estado := 'apagado'; v_total := 0;
    ELSIF c.acceso_total THEN
        v_estado := 'acceso_total'; v_total := 0;
    ELSIF c.prueba_hasta > now() THEN
        v_estado := 'prueba'; v_total := 0;
    ELSE
        SELECT * INTO v_aj FROM cuenta_ajustes x
         WHERE x.cuenta_id = p_cuenta AND x.activo AND x.anulado_en IS NULL AND x.hasta >= current_date
         ORDER BY CASE x.tipo WHEN 'ampliar_prueba' THEN 1 WHEN 'cortesia' THEN 2 WHEN 'meses_gratis' THEN 3
                              WHEN 'precio_especial' THEN 4 ELSE 5 END, x.creado_en DESC
         LIMIT 1;
        IF FOUND THEN
            CASE v_aj.tipo
                WHEN 'ampliar_prueba'  THEN v_estado := 'prueba';       v_total := 0;
                WHEN 'cortesia'        THEN v_estado := 'cortesia';     v_total := 0;
                WHEN 'meses_gratis'    THEN v_estado := 'meses_gratis'; v_total := 0;
                WHEN 'precio_especial' THEN v_estado := 'precio_especial'; v_total := v_aj.precio;
                ELSE NULL;   -- plan_mejor: paga lo suyo y ve el plan mejor
            END CASE;
        END IF;
    END IF;

    RETURN jsonb_build_object(
        'id', c.id, 'tipo', c.tipo, 'titular', c.titular_nombre, 'estadoCuenta', c.estado,
        'creadaEn', c.creado_en, 'origen', c.origen,
        'encendidos', planes_encendidos(), 'enOferta', v_oferta, 'ofertaHasta', a.oferta_hasta,
        'pruebaHasta', c.prueba_hasta, 'accesoTotal', c.acceso_total, 'accesoTotalMotivo', c.acceso_total_motivo,
        'cupos', c.cartera_cupos, 'usados', v_usados, 'cobradas', v_n, 'minimo', a.cartera_minimo,
        'empresas', v_emp, 'ajustes', v_ajs,
        'estado', v_estado,
        'mensual', jsonb_build_object('base', v_base, 'extras', v_extra, 'total', v_total,
                                      'regular', v_baser + v_extrar,
                                      'anual', round(v_total * (12 - a.anual_meses_gratis), 2)));
END $$;

-- El catálogo que ve cualquiera con sesión: planes, tramos y reglas.
CREATE OR REPLACE FUNCTION planes_catalogo() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT jsonb_build_object(
            'encendidos', planes_encendidos(),
            'enOferta', planes_en_oferta(),
            'reglas', (SELECT jsonb_build_object(
                'diasPrueba', a.dias_prueba, 'pruebaCarteraEmpresas', a.prueba_cartera_empresas,
                'rucGratisActivo', a.ruc_gratis_activo, 'rucGratisTope', a.ruc_gratis_tope,
                'carteraMinimo', a.cartera_minimo, 'ofertaHasta', a.oferta_hasta,
                'anualMesesGratis', a.anual_meses_gratis) FROM plataforma_ajustes a WHERE a.id = 1),
            'planes', (SELECT jsonb_agg(jsonb_build_object(
                'codigo', p.codigo, 'nombre', p.nombre, 'orden', p.orden, 'paraQuien', p.para_quien,
                'precioRegular', p.precio_regular, 'precioOferta', p.precio_oferta,
                'sucursalesMax', p.sucursales_max,
                'carteraExtraRegular', p.cartera_extra_regular, 'carteraExtraOferta', p.cartera_extra_oferta,
                'funciones', (SELECT COALESCE(jsonb_agg(jsonb_build_object('menu', f.menu, 'nombre', f.nombre)
                                ORDER BY f.menu_orden, f.orden), '[]'::jsonb)
                                FROM funciones f
                               WHERE f.activo AND NOT f.oculta AND f.plan_minimo = p.codigo))
                ORDER BY p.orden) FROM planes p),
            'tramos', (SELECT jsonb_agg(jsonb_build_object('desde', t.desde, 'hasta', t.hasta,
                        'precioOferta', t.precio_oferta, 'precioRegular', t.precio_regular) ORDER BY t.desde)
                         FROM cartera_tramos t WHERE t.activo));
$$;

-- Todas las cuentas, para «Planes y cobros» (solo el super administrador).
CREATE OR REPLACE FUNCTION planes_cuentas_admin() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN (SELECT COALESCE(jsonb_agg(planes_resumen_cuenta(c.id) ORDER BY c.creado_en DESC), '[]'::jsonb)
              FROM cuentas c WHERE c.activo);
END $$;

-- Cuántas empresas cambian lo que ven con estos movimientos del armador:
-- [{"codigo": "inv_lotes", "plan": "pro"}, ...] (plan null = «Sin plan»).
CREATE OR REPLACE FUNCTION planes_afectados(p_cambios jsonb) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN (
        WITH cambios AS (
            SELECT f.codigo, f.plan_minimo AS antes, NULLIF(x->>'plan', '') AS despues
              FROM jsonb_array_elements(COALESCE(p_cambios, '[]'::jsonb)) x
              JOIN funciones f ON f.codigo = x->>'codigo'),
        empresas_plan AS (
            SELECT e.id, plan_efectivo(e.id) AS plan FROM empresas e WHERE e.activo),
        tocadas AS (
            SELECT DISTINCT ep.id, ep.plan,
                   bool_or(NOT plan_ve_funcion(ep.plan, c.antes) AND plan_ve_funcion(ep.plan, c.despues)) OVER (PARTITION BY ep.id) AS gana,
                   bool_or(plan_ve_funcion(ep.plan, c.antes) AND NOT plan_ve_funcion(ep.plan, c.despues)) OVER (PARTITION BY ep.id) AS pierde
              FROM empresas_plan ep JOIN cambios c
                ON plan_ve_funcion(ep.plan, c.antes) <> plan_ve_funcion(ep.plan, c.despues))
        SELECT jsonb_build_object(
                   'empresas', count(*),
                   'ganan',    count(*) FILTER (WHERE gana),
                   'pierden',  count(*) FILTER (WHERE pierde),
                   'porPlan',  COALESCE((SELECT jsonb_object_agg(plan, n) FROM (SELECT plan, count(*) n FROM tocadas GROUP BY plan) q), '{}'::jsonb))
          FROM tocadas);
END $$;

-- El armador: las funciones y cuántas empresas ve cada plan hoy.
CREATE OR REPLACE FUNCTION planes_armador() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN jsonb_build_object(
        'encendidos', planes_encendidos(),
        'planes', (SELECT jsonb_agg(jsonb_build_object('codigo', codigo, 'nombre', nombre, 'orden', orden) ORDER BY orden) FROM planes),
        'funciones', (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                          'codigo', f.codigo, 'menu', f.menu, 'menuOrden', f.menu_orden, 'nombre', f.nombre,
                          'orden', f.orden, 'plan', f.plan_minimo, 'fija', f.fija, 'nueva', f.es_nueva,
                          'rutas', f.rutas_web) ORDER BY f.menu_orden, f.orden), '[]'::jsonb)
                        FROM funciones f WHERE f.activo AND NOT f.oculta),
        'empresasPorPlan', (SELECT COALESCE(jsonb_object_agg(plan, n), '{}'::jsonb)
                              FROM (SELECT plan_efectivo(e.id) AS plan, count(*) n FROM empresas e WHERE e.activo GROUP BY 1) q));
END $$;

-- -----------------------------------------------------------------------------
-- 12. DUEÑO Y PERMISOS DE LAS FUNCIONES
-- -----------------------------------------------------------------------------
DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'planes_encendidos()', 'plan_orden(text)', 'plan_efectivo(uuid)', 'plan_ve_funcion(text, text)',
        'planes_rutas(uuid)', 'planes_sincronizar_modulos(uuid)', 'planes_sincronizar_cuenta(uuid)',
        'planes_sincronizar_todas()', 'planes_prueba_empresa_nueva(uuid, text, uuid)',
        'fn_planes_cuenta_nueva()', 'fn_planes_cuenta_cambio()', 'fn_planes_empresa_nueva()',
        'fn_planes_empresa_despues()', 'fn_planes_ajuste()', 'fn_planes_interruptor()', 'fn_planes_armador()',
        'fn_planes_sucursales()', 'planes_mi_cuenta_admin()', 'planes_elegir(uuid, text)',
        'planes_fijar_cupos(integer)', 'planes_marcar_estudio(uuid)', 'planes_cupos_usados(uuid)',
        'planes_cupo_al_agregar()', 'planes_subir_cupo()', 'planes_en_oferta()',
        'planes_precio_cartera(integer, boolean)', 'planes_resumen_cuenta(uuid)', 'planes_cuentas_admin()',
        'planes_afectados(jsonb)', 'planes_armador()', 'planes_catalogo()']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
    END LOOP;

    FOREACH f IN ARRAY ARRAY[
        'planes_encendidos()', 'plan_orden(text)', 'plan_efectivo(uuid)', 'plan_ve_funcion(text, text)',
        'planes_rutas(uuid)', 'planes_mi_cuenta_admin()', 'planes_elegir(uuid, text)', 'planes_fijar_cupos(integer)',
        'planes_marcar_estudio(uuid)', 'planes_cupos_usados(uuid)', 'planes_cupo_al_agregar()',
        'planes_subir_cupo()', 'planes_en_oferta()', 'planes_precio_cartera(integer, boolean)',
        'planes_resumen_cuenta(uuid)', 'planes_cuentas_admin()', 'planes_afectados(jsonb)', 'planes_armador()',
        'planes_catalogo()']
    LOOP
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('069_planes')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '069: planes (Básico, Pro, Max), armador, Cartera, mes gratis y acceso total. Interruptor general: %. Nada se borró.',
        COALESCE((SELECT CASE planes_activos WHEN true THEN 'encendido' WHEN false THEN 'apagado' END FROM plataforma_ajustes WHERE id = 1),
                 'lo decide la API al arrancar (encendido solo en producción)');
END $$;