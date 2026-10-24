-- =============================================================================
-- 050 — GUÍAS DE REMISIÓN (e150, 24 oct 2026)
--
-- Lo que sustenta un traslado ante SUNAT. Dos guías, como en SUNAT:
--
--   09  remitente      la emite quien manda los bienes. Serie T###.
--   31  transportista  la emite quien los lleva por encargo (empresa de
--                      transporte). Serie V###. Ampara la 09 del remitente.
--
-- Lo que trae:
--
--   · guias           una fila por guía (cabecera con lo que se lista y se
--                     busca) y `detalle` con lo que se mandó al servicio.
--                     El estado lo trae el aviso del servicio (guia.aceptada,
--                     guia.rechazada, guia.anulada) — 040 del servicio.
--   · guia_bienes     lo que va dentro. Si sale de una factura o boleta,
--                     cada bien apunta a su línea: así se sabe cuánto falta
--                     despachar (despacho en partes).
--   · vehiculos, conductores, transportistas
--                     el TRANSPORTE GUARDADO de cada empresa. Se guardan solos
--                     al emitir; se corrigen o se desactivan (nada se borra).
--   · empresas.transporta_carga / registro_mtc
--                     si la empresa también lleva carga ajena (activa la 31).
--   · permisos guias.ver, guias.emitir, guias.anular, guias.transporte
--
-- Quién ve qué: igual que los comprobantes (046): la empresa, y dentro de ella
-- el alcance de cada persona (su sucursal, o solo lo suyo).
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- --- empresas: ¿también transporta carga ajena? ------------------------------------

ALTER TABLE empresas ADD COLUMN IF NOT EXISTS transporta_carga boolean NOT NULL DEFAULT false;
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS registro_mtc text;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_empresas_registro_mtc') THEN
        ALTER TABLE empresas ADD CONSTRAINT ck_empresas_registro_mtc
            CHECK (registro_mtc IS NULL OR registro_mtc ~ '^[A-Z0-9-]{3,20}$');
    END IF;
END $$;

COMMENT ON COLUMN empresas.transporta_carga IS '050: también lleva carga de otros por encargo (emite la guía del transportista, 31).';
COMMENT ON COLUMN empresas.registro_mtc IS '050: su número de registro en el MTC (obligatorio para la guía del transportista).';

-- --- guias ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS guias (
    id                   uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id           uuid          NOT NULL REFERENCES empresas (id),
    sucursal_id          uuid          REFERENCES sucursales (id),

    tipo                 char(2)       NOT NULL,
    serie                text          NOT NULL,
    correlativo          integer,
    estado               text          NOT NULL DEFAULT 'encolado',
    ambiente             text          NOT NULL DEFAULT 'beta',

    fecha_emision        date          NOT NULL,
    fecha_traslado       date          NOT NULL,
    motivo               char(2)       NOT NULL,
    motivo_descripcion   text,
    modalidad            char(2)       NOT NULL,

    destinatario_tipo    text          NOT NULL,
    destinatario_doc     text          NOT NULL,
    destinatario_nombre  text          NOT NULL,
    cliente_id           uuid          REFERENCES clientes (id),

    partida_direccion    text          NOT NULL,
    partida_ubigeo       char(6)       NOT NULL,
    llegada_direccion    text          NOT NULL,
    llegada_ubigeo       char(6)       NOT NULL,

    peso_bruto           numeric(14,3) NOT NULL,
    bultos               integer,

    vehiculo_placa       text,
    conductor_doc        text,
    conductor_nombre     text,
    transportista_ruc    text,
    transportista_nombre text,

    -- La factura o boleta de la que sale (despacho), o el número de un
    -- documento ajeno (en la 31, la guía del remitente que ampara).
    documento_id         uuid          REFERENCES documentos (id),
    documento_relacionado text,

    -- Lo que se mandó al servicio, tal cual (para verla y para reintentar).
    detalle              jsonb         NOT NULL DEFAULT '{}',

    guia_servicio_id     uuid,
    codigo_sunat         text,
    mensaje_sunat        text,
    estado_servicio_en   timestamptz,
    idempotency_key      text          NOT NULL,

    anulada_en           date,
    anulada_motivo       text,

    creado_en            timestamptz   NOT NULL DEFAULT now(),
    creado_por           uuid,
    modificado_en        timestamptz,
    modificado_por       uuid,
    activo               boolean       NOT NULL DEFAULT true,
    version              integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_guias_tipo      CHECK (tipo IN ('09', '31')),
    CONSTRAINT ck_guias_serie     CHECK ((tipo = '09' AND serie ~ '^T[0-9]{3}$') OR (tipo = '31' AND serie ~ '^V[0-9]{3}$')),
    CONSTRAINT ck_guias_estado    CHECK (estado IN ('encolado', 'aceptado', 'observado', 'rechazado', 'error', 'anulado')),
    CONSTRAINT ck_guias_ambiente  CHECK (ambiente IN ('beta', 'produccion')),
    CONSTRAINT ck_guias_modalidad CHECK (modalidad IN ('01', '02')),
    CONSTRAINT ck_guias_ubigeos   CHECK (partida_ubigeo ~ '^[0-9]{6}$' AND llegada_ubigeo ~ '^[0-9]{6}$'),
    CONSTRAINT ck_guias_peso      CHECK (peso_bruto > 0),
    CONSTRAINT ck_guias_anulada   CHECK ((estado = 'anulado') = (anulada_en IS NOT NULL))
);

-- Una clave por guía: dos clics en «Emitir» son la misma guía.
CREATE UNIQUE INDEX IF NOT EXISTS ux_guias_clave    ON guias (empresa_id, idempotency_key);
CREATE UNIQUE INDEX IF NOT EXISTS ux_guias_servicio ON guias (guia_servicio_id) WHERE guia_servicio_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_guias_empresa_fecha   ON guias (empresa_id, fecha_emision DESC, creado_en DESC);
CREATE INDEX IF NOT EXISTS ix_guias_documento       ON guias (documento_id) WHERE documento_id IS NOT NULL;

COMMENT ON TABLE guias IS '050: guías de remisión (09 remitente, 31 transportista). El estado lo trae el aviso del servicio.';

-- --- guia_bienes -----------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS guia_bienes (
    id                  uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id          uuid          NOT NULL REFERENCES empresas (id),
    guia_id             uuid          NOT NULL REFERENCES guias (id),
    numero              integer       NOT NULL,
    producto_id         uuid          REFERENCES productos (id),
    documento_linea_id  uuid          REFERENCES documento_lineas (id),
    codigo              text,
    descripcion         text          NOT NULL,
    unidad              text          NOT NULL,
    cantidad            numeric(14,3) NOT NULL,

    creado_en           timestamptz   NOT NULL DEFAULT now(),
    creado_por          uuid,
    modificado_en       timestamptz,
    modificado_por      uuid,
    activo              boolean       NOT NULL DEFAULT true,
    version             integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_guia_bienes_cantidad    CHECK (cantidad > 0),
    CONSTRAINT ck_guia_bienes_descripcion CHECK (length(btrim(descripcion)) BETWEEN 1 AND 500)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_guia_bienes_numero ON guia_bienes (guia_id, numero);
CREATE INDEX IF NOT EXISTS ix_guia_bienes_linea ON guia_bienes (documento_linea_id) WHERE documento_linea_id IS NOT NULL;

-- --- el transporte guardado ----------------------------------------------------------------

CREATE TABLE IF NOT EXISTS vehiculos (
    id                    uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id            uuid        NOT NULL REFERENCES empresas (id),
    placa                 text        NOT NULL,
    descripcion           text,
    tuc                   text,
    autorizacion          text,
    entidad_autorizadora  text,
    usos                  integer     NOT NULL DEFAULT 0,
    ultimo_uso            timestamptz,

    creado_en             timestamptz NOT NULL DEFAULT now(),
    creado_por            uuid,
    modificado_en         timestamptz,
    modificado_por        uuid,
    activo                boolean     NOT NULL DEFAULT true,
    version               integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_vehiculos_placa CHECK (placa ~ '^[A-Z0-9]{5,8}$')
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_vehiculos_placa ON vehiculos (empresa_id, placa);

CREATE TABLE IF NOT EXISTS conductores (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    tipo_doc        char(1)     NOT NULL DEFAULT '1',
    numero_doc      text        NOT NULL,
    nombres         text        NOT NULL,
    apellidos       text        NOT NULL,
    licencia        text        NOT NULL,
    usuario_id      uuid        REFERENCES usuarios (id),
    usos            integer     NOT NULL DEFAULT 0,
    ultimo_uso      timestamptz,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_conductores_doc      CHECK (numero_doc ~ '^[A-Z0-9]{8,15}$'),
    CONSTRAINT ck_conductores_licencia CHECK (licencia ~ '^[A-Z0-9-]{9,10}$')
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_conductores_doc ON conductores (empresa_id, tipo_doc, numero_doc);

CREATE TABLE IF NOT EXISTS transportistas (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    ruc             char(11)    NOT NULL,
    razon_social    text        NOT NULL,
    mtc             text,
    usos            integer     NOT NULL DEFAULT 0,
    ultimo_uso      timestamptz,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_transportistas_ruc CHECK (ruc ~ '^(10|15|17|20)[0-9]{9}$')
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_transportistas_ruc ON transportistas (empresa_id, ruc);

-- --- aislamiento, auditoría y permisos ------------------------------------------------------

ALTER TABLE guias          ENABLE ROW LEVEL SECURITY;
ALTER TABLE guia_bienes    ENABLE ROW LEVEL SECURITY;
ALTER TABLE vehiculos      ENABLE ROW LEVEL SECURITY;
ALTER TABLE conductores    ENABLE ROW LEVEL SECURITY;
ALTER TABLE transportistas ENABLE ROW LEVEL SECURITY;
ALTER TABLE guias          FORCE ROW LEVEL SECURITY;
ALTER TABLE guia_bienes    FORCE ROW LEVEL SECURITY;
ALTER TABLE vehiculos      FORCE ROW LEVEL SECURITY;
ALTER TABLE conductores    FORCE ROW LEVEL SECURITY;
ALTER TABLE transportistas FORCE ROW LEVEL SECURITY;

-- Las guías, con el alcance de cada persona (como los comprobantes, 046).
DROP POLICY IF EXISTS p_guias ON guias;
CREATE POLICY p_guias ON guias
    USING (ctx_es_super() OR (empresa_id = ctx_empresa() AND ctx_ve_documento(sucursal_id, creado_por)))
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['guia_bienes', 'vehiculos', 'conductores', 'transportistas'] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'p_' || t) THEN
            EXECUTE format(
                'CREATE POLICY %I ON %I USING (ctx_es_super() OR empresa_id = ctx_empresa()) ' ||
                'WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa())', 'p_' || t, t);
        END IF;
    END LOOP;

    FOREACH t IN ARRAY ARRAY['guias', 'guia_bienes', 'vehiculos', 'conductores', 'transportistas'] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = t::regclass AND NOT tgisinternal) THEN
            PERFORM vigilar_tabla(t);
        END IF;
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE ON guias, guia_bienes, vehiculos, conductores, transportistas TO plataforma_app;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('guias.ver',        'guias', 'Ver las guías de remisión'),
    ('guias.emitir',     'guias', 'Emitir guías de remisión'),
    ('guias.anular',     'guias', 'Registrar una guía anulada en SOL'),
    ('guias.transporte', 'guias', 'Corregir o desactivar vehículos, conductores y transportistas guardados')
ON CONFLICT (codigo) DO NOTHING;

-- Quien ya tiene la facturación, recibe las guías que le corresponden: emitir
-- comprobantes → emitir guías; verlos → verlas; anular → registrar anuladas.
-- El transporte guardado: el Administrador y el Jefe de sucursal.
INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT DISTINCT rp.rol_id, x.nuevo
  FROM rol_permiso rp
  JOIN (VALUES ('facturacion.ver',    'guias.ver'),
               ('facturacion.emitir', 'guias.ver'),
               ('facturacion.emitir', 'guias.emitir'),
               ('facturacion.anular', 'guias.anular')) AS x (tiene, nuevo) ON x.tiene = rp.permiso_codigo
 WHERE rp.activo
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = rp.rol_id AND o.permiso_codigo = x.nuevo);

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, p.codigo
  FROM roles r
 CROSS JOIN (VALUES ('guias.ver'), ('guias.emitir'), ('guias.anular'), ('guias.transporte')) AS p (codigo)
 WHERE r.id = '11111111-1111-1111-1111-111111111111'
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = p.codigo);

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, 'guias.transporte'
  FROM roles r
 WHERE (r.id = '55555555-5555-5555-5555-555555555555' OR r.basado_en = '55555555-5555-5555-5555-555555555555')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = 'guias.transporte');

INSERT INTO migraciones_aplicadas (nombre) VALUES ('050_guias_de_remision')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '050: guías de remisión (09 y 31) y transporte guardado listos. Nada se borró.';
END $$;