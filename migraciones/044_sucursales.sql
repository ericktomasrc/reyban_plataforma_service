-- =============================================================================
-- 044 — SUCURSALES (23 oct 2026, maqueta «Sucursales» y análisis del 22 oct)
--
-- Una sucursal es un local del MISMO RUC (establecimiento anexo de la ficha
-- RUC). Lo que cambia con ella:
--
--   · sucursales          código de establecimiento SUNAT (4 dígitos; 0000 =
--                         domicilio fiscal), nombre, dirección, ubigeo.
--   · sucursal_series     a qué sucursal pertenece cada serie. Una serie SIN
--                         fila aquí es de la 0000: así las series de antes
--                         no hace falta tocarlas.
--   · usuario_sucursal    en qué sucursales trabaja cada usuario. SIN filas
--                         activas = en todas (dueño, administrador).
--   · documentos.sucursal_id  de qué local salió. NULL = la 0000 (lo de antes).
--
-- La 0000 de cada empresa existente se crea aquí con la dirección de la
-- empresa. Las empresas nuevas la reciben al primer uso (la API la crea).
--
-- NADA SE BORRA: «eliminar» una sucursal que nunca se usó la marca
-- (eliminada_en) y la esconde; desactivar la deja de lado sin esconderla.
-- Permiso nuevo «facturacion.sucursales_gestionar» (Administrador).
-- Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- --- sucursales ---------------------------------------------------------------

CREATE TABLE IF NOT EXISTS sucursales (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    codigo          char(4)     NOT NULL,
    nombre          text        NOT NULL,
    direccion       text,
    ubigeo          char(6),
    eliminada_en    timestamptz,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_sucursales_codigo    CHECK (codigo ~ '^[0-9]{4}$'),
    CONSTRAINT ck_sucursales_nombre    CHECK (length(btrim(nombre)) BETWEEN 1 AND 80),
    CONSTRAINT ck_sucursales_ubigeo    CHECK (ubigeo IS NULL OR ubigeo ~ '^[0-9]{6}$'),
    -- Una eliminada está siempre inactiva; la 0000 no se elimina.
    CONSTRAINT ck_sucursales_eliminada CHECK (eliminada_en IS NULL OR (NOT activo AND codigo <> '0000'))
);

-- El código no se repite en la empresa mientras la sucursal exista (activa o
-- desactivada): es lo que va en el XML.
CREATE UNIQUE INDEX IF NOT EXISTS ux_sucursales_codigo
    ON sucursales (empresa_id, codigo) WHERE eliminada_en IS NULL;

-- El nombre no se repite entre las activas.
CREATE UNIQUE INDEX IF NOT EXISTS ux_sucursales_nombre
    ON sucursales (empresa_id, lower(btrim(nombre))) WHERE activo;

-- --- series por sucursal -----------------------------------------------------

CREATE TABLE IF NOT EXISTS sucursal_series (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id        uuid        NOT NULL REFERENCES empresas (id),
    sucursal_id       uuid        NOT NULL REFERENCES sucursales (id),
    tipo_comprobante  char(2)     NOT NULL,
    serie             text        NOT NULL,

    creado_en         timestamptz NOT NULL DEFAULT now(),
    creado_por        uuid,
    modificado_en     timestamptz,
    modificado_por    uuid,
    activo            boolean     NOT NULL DEFAULT true,
    version           integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_sucursal_series_serie CHECK (serie ~ '^[A-Z0-9]{4}$')
);

-- Cada serie, de una sola sucursal.
CREATE UNIQUE INDEX IF NOT EXISTS ux_sucursal_series
    ON sucursal_series (empresa_id, tipo_comprobante, serie) WHERE activo;

-- --- usuarios por sucursal ----------------------------------------------------

CREATE TABLE IF NOT EXISTS usuario_sucursal (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    usuario_id      uuid        NOT NULL REFERENCES usuarios (id),
    sucursal_id     uuid        NOT NULL REFERENCES sucursales (id),

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_usuario_sucursal
    ON usuario_sucursal (usuario_id, sucursal_id) WHERE activo;

-- --- documentos: de qué local salió ---------------------------------------------

ALTER TABLE documentos ADD COLUMN IF NOT EXISTS sucursal_id uuid REFERENCES sucursales (id);
CREATE INDEX IF NOT EXISTS ix_documentos_sucursal ON documentos (sucursal_id) WHERE sucursal_id IS NOT NULL;

COMMENT ON COLUMN documentos.sucursal_id IS '044: de qué sucursal salió. NULL = la 0000 (domicilio fiscal).';

-- --- aislamiento, auditoría y permisos -------------------------------------------

ALTER TABLE sucursales       ENABLE ROW LEVEL SECURITY;
ALTER TABLE sucursal_series  ENABLE ROW LEVEL SECURITY;
ALTER TABLE usuario_sucursal ENABLE ROW LEVEL SECURITY;
ALTER TABLE sucursales       FORCE ROW LEVEL SECURITY;
ALTER TABLE sucursal_series  FORCE ROW LEVEL SECURITY;
ALTER TABLE usuario_sucursal FORCE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'p_sucursales') THEN
        CREATE POLICY p_sucursales ON sucursales
            USING (ctx_es_super() OR empresa_id = ctx_empresa())
            WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'p_sucursal_series') THEN
        CREATE POLICY p_sucursal_series ON sucursal_series
            USING (ctx_es_super() OR empresa_id = ctx_empresa())
            WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'p_usuario_sucursal') THEN
        CREATE POLICY p_usuario_sucursal ON usuario_sucursal
            USING (ctx_es_super() OR empresa_id = ctx_empresa())
            WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'sucursales'::regclass AND NOT tgisinternal) THEN
        PERFORM vigilar_tabla('sucursales');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'sucursal_series'::regclass AND NOT tgisinternal) THEN
        PERFORM vigilar_tabla('sucursal_series');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'usuario_sucursal'::regclass AND NOT tgisinternal) THEN
        PERFORM vigilar_tabla('usuario_sucursal');
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON sucursales, sucursal_series, usuario_sucursal TO plataforma_app;

-- El nombre del distrito para la dirección del local (datos públicos del INEI).
GRANT SELECT ON referencia.ubigeos TO plataforma_app;

INSERT INTO permisos (codigo, area, descripcion)
VALUES ('facturacion.sucursales_gestionar', 'facturacion', 'Gestionar sucursales y en cuáles trabaja cada usuario')
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT '11111111-1111-1111-1111-111111111111', 'facturacion.sucursales_gestionar'
 WHERE NOT EXISTS (
       SELECT 1 FROM rol_permiso
        WHERE rol_id = '11111111-1111-1111-1111-111111111111'
          AND permiso_codigo = 'facturacion.sucursales_gestionar');

-- --- la 0000 de cada empresa que ya existe ----------------------------------------

INSERT INTO sucursales (empresa_id, codigo, nombre, direccion)
SELECT e.id, '0000', 'Domicilio fiscal', e.direccion
  FROM empresas e
 WHERE NOT EXISTS (SELECT 1 FROM sucursales s WHERE s.empresa_id = e.id AND s.codigo = '0000');

INSERT INTO migraciones_aplicadas (nombre) VALUES ('044_sucursales')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '044: sucursales listas (% empresas con su 0000). Nada se borró.',
        (SELECT count(*) FROM sucursales WHERE codigo = '0000');
END $$;