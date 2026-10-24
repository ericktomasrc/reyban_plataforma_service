-- =============================================================================
-- 049 — ENTRAR CON GOOGLE (e149)
--
-- Además del correo y la contraseña, «Entrar con Google», como Odoo o SAP:
--
--   · Quien ya existe entra con la cuenta de Google de su mismo correo. La
--     primera vez queda enlazada (google_sub) y desde entonces es ESA cuenta
--     de Google, aunque el correo cambie. Google ya pidió su segundo factor:
--     aquí no se pide el nuestro.
--   · Quien no existe se registra en un paso (tipo de cuenta y términos). Su
--     DNI o RUC se pide al agregar su primera empresa: la ficha obligatoria
--     (048) lo exige, y la cuenta toma como titular el RUC de esa empresa.
--   · El super administrador NO entra con Google: sigue con contraseña y su
--     segundo factor.
--
-- La API habla con Google (el código por el token, con PKCE) y confirma que el
-- correo esté verificado. La base decide quién es, crea la sesión y lo anota.
--
-- Nada se borra. Idempotente.
-- =============================================================================

BEGIN;

-- --- 1: el enlace con Google -------------------------------------------------

ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS google_sub text;
CREATE UNIQUE INDEX IF NOT EXISTS ux_usuarios_google ON usuarios (google_sub) WHERE google_sub IS NOT NULL;

COMMENT ON COLUMN usuarios.google_sub IS
    '049: el identificador de su cuenta de Google (claim «sub»). Nulo = nunca entró con Google.';

-- --- 2: cuentas registradas con Google: el titular llega con su primera empresa

ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS con_google_en timestamptz;

ALTER TABLE cuentas DROP CONSTRAINT IF EXISTS ck_cuentas_titular;
ALTER TABLE cuentas ADD CONSTRAINT ck_cuentas_titular CHECK (
    (titular_tipo IS NULL AND titular_documento IS NULL AND (origen = 'alta' OR con_google_en IS NOT NULL))
    OR (titular_tipo = 'natural'  AND titular_documento ~ '^[0-9]{8}$'
        AND length(btrim(titular_nombre)) BETWEEN 2 AND 200)
    OR (titular_tipo = 'juridica' AND titular_documento ~ '^[0-9]{11}$'
        AND length(btrim(titular_nombre)) BETWEEN 2 AND 200));

-- El titular, de la primera empresa con RUC: RUC 10 → persona natural (su DNI);
-- los demás → jurídica.
CREATE OR REPLACE FUNCTION fn_empresas_titular_google() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_nombre text := btrim(coalesce(NEW.razon_social, NEW.nombre, ''));
BEGIN
    IF NEW.cuenta_id IS NULL OR NEW.ruc IS NULL OR NEW.ruc !~ '^[0-9]{11}$' OR length(v_nombre) < 2 THEN
        RETURN NULL;
    END IF;
    UPDATE cuentas
       SET titular_tipo      = CASE WHEN left(NEW.ruc, 2) = '10' THEN 'natural' ELSE 'juridica' END,
           titular_documento = CASE WHEN left(NEW.ruc, 2) = '10' THEN substr(NEW.ruc, 3, 8) ELSE NEW.ruc END,
           titular_nombre    = left(v_nombre, 200)
     WHERE id = NEW.cuenta_id AND con_google_en IS NOT NULL AND titular_documento IS NULL;
    RETURN NULL;
END $$;

ALTER FUNCTION fn_empresas_titular_google() OWNER TO plataforma_puertas;

DROP TRIGGER IF EXISTS tr_empresas_titular_google ON empresas;
CREATE TRIGGER tr_empresas_titular_google
    AFTER INSERT OR UPDATE OF ruc, razon_social ON empresas
    FOR EACH ROW EXECUTE FUNCTION fn_empresas_titular_google();


-- --- 3: el registro a medias (Google dijo quién es; falta el tipo de cuenta) ---

CREATE TABLE IF NOT EXISTS google_pendientes (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    token_hash      bytea       NOT NULL,
    google_sub      text        NOT NULL,
    correo          text        NOT NULL,
    nombre          text,
    expira_en       timestamptz NOT NULL,
    usado_en        timestamptz,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_google_pendientes_token ON google_pendientes (token_hash);

-- Solo las funciones de abajo la tocan: sin política para la aplicación.
ALTER TABLE google_pendientes ENABLE ROW LEVEL SECURITY;
ALTER TABLE google_pendientes FORCE  ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_google_pendientes ON google_pendientes;
CREATE POLICY p_google_pendientes ON google_pendientes USING (ctx_es_super()) WITH CHECK (ctx_es_super());

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'google_pendientes'::regclass AND NOT tgisinternal) THEN
        PERFORM vigilar_tabla('google_pendientes', ARRAY['token_hash']);
    END IF;
END $$;


-- --- 4: entrar ---------------------------------------------------------------
-- json: {resultado: ok|no_existe|otro_google|super|inactivo|suspendida|bloqueado,
--        usuarioId, empresaId, correo}
-- Con «ok» deja creada la sesión (con el segundo factor ya superado).
CREATE OR REPLACE FUNCTION ingresar_con_google(
    p_sub text, p_correo text, p_token_hash bytea, p_expira_en timestamptz, p_ip inet, p_agente text)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v usuarios%ROWTYPE;
BEGIN
    SELECT * INTO v FROM usuarios WHERE google_sub = p_sub;
    IF NOT FOUND THEN
        SELECT * INTO v FROM usuarios WHERE lower(correo) = lower(btrim(p_correo));
        IF NOT FOUND THEN
            RETURN json_build_object('resultado', 'no_existe')::text;
        END IF;
        -- Ese correo ya está enlazado a OTRA cuenta de Google.
        IF v.google_sub IS NOT NULL AND v.google_sub <> p_sub THEN
            RETURN json_build_object('resultado', 'otro_google')::text;
        END IF;
    END IF;

    IF v.es_super_admin THEN
        RETURN json_build_object('resultado', 'super')::text;
    END IF;
    IF NOT v.activo THEN
        RETURN json_build_object('resultado', 'inactivo')::text;
    END IF;
    IF v.bloqueado_hasta IS NOT NULL AND v.bloqueado_hasta > now() THEN
        RETURN json_build_object('resultado', 'bloqueado')::text;
    END IF;
    IF EXISTS (SELECT 1 FROM empresas e WHERE e.id = v.empresa_id AND NOT (e.activo AND e.suspendida_en IS NULL))
       OR EXISTS (SELECT 1 FROM cuentas c WHERE c.id = v.cuenta_id AND c.estado = 'suspendida') THEN
        RETURN json_build_object('resultado', 'suspendida')::text;
    END IF;

    UPDATE usuarios
       SET google_sub           = p_sub,
           clave_cambio_forzado = false,   -- no tiene contraseña que cambiar para entrar
           intentos_fallidos    = 0,
           bloqueado_hasta      = NULL,
           ultimo_ingreso_en    = now(),
           ultimo_ingreso_ip    = p_ip
     WHERE id = v.id;

    -- Google ya comprobó el correo: la cuenta que esperaba confirmarlo queda activa.
    UPDATE cuentas SET estado = 'activa', confirmada_en = coalesce(confirmada_en, now())
     WHERE id = v.cuenta_id AND estado = 'pendiente';

    -- Los enlaces de invitación que esperaban ya no hacen falta.
    UPDATE tokens_un_uso SET anulado_en = now()
     WHERE usuario_id = v.id AND proposito IN ('invitacion', 'verificar_correo')
       AND usado_en IS NULL AND anulado_en IS NULL;

    INSERT INTO sesiones (usuario_id, empresa_id, token_hash, expira_en, ip, agente, dosfa_superado)
    VALUES (v.id, v.empresa_id, p_token_hash, p_expira_en, p_ip, p_agente, true);

    PERFORM registrar_evento('ingreso'::text, true, v.id, v.empresa_id, v.correo,
        jsonb_build_object('via', 'google', 'enlazado', v.google_sub IS NULL));

    RETURN json_build_object('resultado', 'ok', 'usuarioId', v.id, 'empresaId', v.empresa_id, 'correo', v.correo)::text;
END $$;

ALTER FUNCTION ingresar_con_google(text, text, bytea, timestamptz, inet, text) OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION ingresar_con_google(text, text, bytea, timestamptz, inet, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ingresar_con_google(text, text, bytea, timestamptz, inet, text) TO plataforma_app;


-- --- 5: el registro a medias --------------------------------------------------

CREATE OR REPLACE FUNCTION google_pendiente_guardar(
    p_token_hash bytea, p_sub text, p_correo text, p_nombre text, p_expira_en timestamptz)
RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = public AS $$
    INSERT INTO google_pendientes (token_hash, google_sub, correo, nombre, expira_en)
    VALUES (p_token_hash, p_sub, btrim(p_correo), nullif(btrim(coalesce(p_nombre, '')), ''), p_expira_en);
$$;

-- json {correo, nombre} o NULL si no vale.
CREATE OR REPLACE FUNCTION google_pendiente_ver(p_token_hash bytea)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT json_build_object('correo', g.correo, 'nombre', g.nombre)::text
      FROM google_pendientes g
     WHERE g.token_hash = p_token_hash AND g.usado_en IS NULL AND g.expira_en > now();
$$;

-- json {resultado: ok|no_existe|correo_ocupado|demasiados, usuarioId, correo}.
-- Con «ok» deja la cuenta (activa: Google verificó el correo), su dueño y la sesión.
CREATE OR REPLACE FUNCTION registrar_con_google(
    p_token_hash bytea, p_tipo text, p_sesion_hash bytea, p_expira_en timestamptz, p_ip inet, p_agente text)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    g         google_pendientes%ROWTYPE;
    v_cuenta  uuid;
    v_usuario uuid;
BEGIN
    SELECT * INTO g FROM google_pendientes
     WHERE token_hash = p_token_hash AND usado_en IS NULL AND expira_en > now()
       FOR UPDATE;
    IF NOT FOUND THEN
        RETURN json_build_object('resultado', 'no_existe')::text;
    END IF;

    IF p_ip IS NOT NULL AND (SELECT count(*) FROM registro_intentos
                              WHERE ip = p_ip AND ocurrido_en > now() - interval '1 hour') >= 10 THEN
        INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, g.correo, 'demasiados');
        RETURN json_build_object('resultado', 'demasiados')::text;
    END IF;

    IF EXISTS (SELECT 1 FROM usuarios WHERE lower(correo) = lower(g.correo) OR google_sub = g.google_sub) THEN
        INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, g.correo, 'correo_ocupado');
        RETURN json_build_object('resultado', 'correo_ocupado')::text;
    END IF;

    UPDATE google_pendientes SET usado_en = now() WHERE id = g.id;

    INSERT INTO cuentas (tipo, estado, origen, confirmada_en, terminos_aceptados_en, con_google_en)
    VALUES (p_tipo, 'activa', 'registro', now(), now(), now())
    RETURNING id INTO v_cuenta;

    INSERT INTO usuarios (cuenta_id, correo, nombre, alias, clave_hash, nivel_cuenta, google_sub,
                          ultimo_ingreso_en, ultimo_ingreso_ip)
    VALUES (v_cuenta, g.correo, left(coalesce(g.nombre, split_part(g.correo, '@', 1)), 120),
            alias_libre(split_part(g.correo, '@', 1)),
            'sin-clave$' || encode(gen_random_bytes(16), 'hex'), 'dueno', g.google_sub, now(), p_ip)
    RETURNING id INTO v_usuario;

    INSERT INTO sesiones (usuario_id, empresa_id, token_hash, expira_en, ip, agente, dosfa_superado)
    VALUES (v_usuario, NULL, p_sesion_hash, p_expira_en, p_ip, p_agente, true);

    INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, g.correo, 'ok');
    PERFORM registrar_evento('cuenta_registrada'::text, true, v_usuario, NULL, g.correo,
        jsonb_build_object('via', 'google', 'tipo', p_tipo));

    RETURN json_build_object('resultado', 'ok', 'usuarioId', v_usuario, 'correo', g.correo)::text;
END $$;

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'google_pendiente_guardar(bytea, text, text, text, timestamptz)',
        'google_pendiente_ver(bytea)',
        'registrar_con_google(bytea, text, bytea, timestamptz, inet, text)']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('049_entrar_con_google')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '049: «Entrar con Google» listo (falta poner GOOGLE_CLIENT_ID y GOOGLE_CLIENT_SECRET en el .env). Nada se borró.';
END $$;