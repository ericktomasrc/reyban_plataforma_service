-- =============================================================================
-- 030 — CONEXIÓN CON SUNAT: MODO PRUEBA / MODO REAL (18 oct 2026)
--
-- Diseño: modo-prueba-y-real.md.
--
--   · conexion_sunat: UNA fila con el modo de esta instalación ('beta' o
--     'real'). Solo la cambia el super administrador, con la función
--     cambiar_conexion_sunat(). Cualquiera con sesión la lee con
--     conexion_sunat() (la franja de arriba). En producción la API no deja
--     cambiarla: queda fija en real por PLATAFORMA_SUNAT=real en el .env.
--   · documentos.ambiente: en qué SUNAT se emitió cada comprobante ('beta' o
--     'produccion'). La numeración no se repite DENTRO de un ambiente: F001-1
--     de prueba y F001-1 real son dos comprobantes distintos.
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS conexion_sunat (
    id                   smallint    PRIMARY KEY DEFAULT 1,
    modo                 text        NOT NULL DEFAULT 'beta',
    cambiado_en          timestamptz,
    cambiado_por_correo  text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_conexion_sunat_una CHECK (id = 1),
    CONSTRAINT ck_conexion_sunat_modo CHECK (modo IN ('beta', 'real'))
);

COMMENT ON TABLE conexion_sunat IS
    'El modo de esta instalación: beta (pruebas) o real. Una sola fila. Solo por funciones.';

ALTER TABLE conexion_sunat ENABLE ROW LEVEL SECURITY;
ALTER TABLE conexion_sunat FORCE  ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_conexion_sunat') THEN
        PERFORM vigilar_tabla('conexion_sunat');
    END IF;
END $$;


-- Lo lee cualquiera con sesión (la franja). No lleva nada secreto.
CREATE OR REPLACE FUNCTION conexion_sunat()
RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_fila conexion_sunat%ROWTYPE;
BEGIN
    SELECT * INTO v_fila FROM conexion_sunat WHERE id = 1;
    RETURN json_build_object(
        'modo',       coalesce(v_fila.modo, 'beta'),
        'cambiadoEn', v_fila.cambiado_en,
        'cambiadoPor', CASE WHEN ctx_es_super() THEN v_fila.cambiado_por_correo END
    )::text;
END $$;

ALTER FUNCTION conexion_sunat() OWNER TO plataforma_puertas;


-- Solo el super administrador. Devuelve el estado nuevo.
CREATE OR REPLACE FUNCTION cambiar_conexion_sunat(p_modo text)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_correo text;
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'Solo el super administrador cambia la conexión con SUNAT.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF p_modo NOT IN ('beta', 'real') THEN
        RAISE EXCEPTION 'El modo es beta o real.' USING ERRCODE = 'check_violation';
    END IF;

    SELECT u.correo INTO v_correo FROM usuarios u WHERE u.id = ctx_usuario();

    INSERT INTO conexion_sunat (id, modo, cambiado_en, cambiado_por_correo)
    VALUES (1, p_modo, now(), v_correo)
    ON CONFLICT (id) DO UPDATE
       SET modo = EXCLUDED.modo,
           cambiado_en = EXCLUDED.cambiado_en,
           cambiado_por_correo = EXCLUDED.cambiado_por_correo
     WHERE conexion_sunat.modo IS DISTINCT FROM EXCLUDED.modo;

    RETURN conexion_sunat();
END $$;

ALTER FUNCTION cambiar_conexion_sunat(text) OWNER TO plataforma_puertas;


-- --- El ambiente de UNA empresa, tal como lo dejó el servicio ------------------
--
-- Lo llama la API después de que el servicio aceptó el cambio (interruptor del
-- super, o paso 6 del asistente en producción). Solo esa empresa (su contexto)
-- o el super administrador.
CREATE OR REPLACE FUNCTION fijar_ambiente_empresa(p_empresa uuid, p_ambiente text)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_n integer;
BEGIN
    IF NOT (ctx_es_super() OR ctx_empresa() = p_empresa) THEN
        RAISE EXCEPTION 'Solo la propia empresa o el super administrador.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF p_ambiente NOT IN ('beta', 'produccion') THEN
        RAISE EXCEPTION 'El ambiente es beta o produccion.' USING ERRCODE = 'check_violation';
    END IF;

    UPDATE empresas SET ambiente = p_ambiente
     WHERE id = p_empresa AND ambiente IS DISTINCT FROM p_ambiente;
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RETURN v_n;
END $$;

ALTER FUNCTION fijar_ambiente_empresa(uuid, text) OWNER TO plataforma_puertas;


-- --- documentos.ambiente ------------------------------------------------------

ALTER TABLE documentos ADD COLUMN IF NOT EXISTS ambiente text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_documentos_ambiente') THEN
        ALTER TABLE documentos ADD CONSTRAINT ck_documentos_ambiente
            CHECK (ambiente IS NULL OR ambiente IN ('beta', 'produccion'));
    END IF;
END $$;

COMMENT ON COLUMN documentos.ambiente IS
    'beta o produccion: el SUNAT al que fue el comprobante. Lo pone la API al emitir. '
    'Null en un borrador que nunca se emitió.';

-- Lo ya emitido toma el ambiente que hoy tiene su empresa (hasta hoy todo
-- era beta salvo que se pasara a mano en el panel).
UPDATE documentos d
   SET ambiente = coalesce(e.ambiente, 'beta')
  FROM empresas e
 WHERE e.id = d.empresa_id
   AND d.ambiente IS NULL
   AND d.serie IS NOT NULL
   AND d.correlativo IS NOT NULL;

-- La numeración no se repite dentro de un ambiente.
DROP INDEX IF EXISTS ux_documentos_numeracion;
CREATE UNIQUE INDEX IF NOT EXISTS ux_documentos_numeracion_ambiente
    ON documentos (empresa_id, tipo, serie, correlativo, (coalesce(ambiente, 'beta')))
    WHERE serie IS NOT NULL AND correlativo IS NOT NULL;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('030_conexion_sunat')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;