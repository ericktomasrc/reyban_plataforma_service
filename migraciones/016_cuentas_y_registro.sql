-- =============================================================================
-- 016 — CUENTAS Y REGISTRO LIBRE
--
-- Diseño: cartera-diseno.md (fase 1, §13).
--
-- Hasta hoy solo el super administrador creaba empresas, y con cada empresa
-- su primer usuario. Desde ahora cualquiera puede registrarse solo, en una de
-- dos cuentas:
--
--   Cuenta Negocio       «Facturo para mi negocio»: una empresa, la suya.
--   Cuenta Profesional   «Facturo para mis clientes»: varias empresas.
--
-- La cuenta es del TITULAR (persona natural con DNI o jurídica con RUC). Lo
-- que se emite sale siempre con el RUC de la EMPRESA que factura, que se
-- agrega después (fase 2).
--
-- LO QUE CAMBIA EN LO QUE YA EXISTÍA, y por qué:
--
--   1. `ck_usuarios_ambito`. Antes: super admin ⟺ sin empresa. Ahora un
--      usuario recién registrado existe ANTES de tener empresa: pertenece a su
--      cuenta. Super admin = sin empresa y sin cuenta; los demás, con empresa
--      o con cuenta.
--   2. `p_usuarios`. Tu propia fila es tuya: sin esto, quien todavía no tiene
--      empresa no se vería a sí mismo (`/api/yo` respondería 401) — la misma
--      corrección que la 010 hizo con `sesiones`.
--   3. Las empresas de hoy reciben su Cuenta Negocio, y sus usuarios esa
--      cuenta. Las que el super administrador dé de alta en adelante, igual,
--      sola, por un disparador: el alta de hoy no se toca.
--   4. Las funciones de ingreso, sesión e invitación miran también si la
--      cuenta está suspendida, y la invitación activa la cuenta al usarse.
--
-- LO QUE NO CAMBIA: el aislamiento entre empresas. Un usuario sin empresa no
-- tiene empresa en el contexto, así que el RLS no le enseña nada de negocio. Y
-- no tiene roles (los recibe con su empresa, en la fase 2): sin permisos, no
-- hay endpoint que le responda.
--
-- Todo en una transacción.
-- =============================================================================

BEGIN;


-- =============================================================================
-- 1. CUENTAS
-- =============================================================================

CREATE TABLE cuentas (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    tipo               text        NOT NULL,

    -- El titular. Nulos solo en las cuentas que se crearon solas para las
    -- empresas que ya existían o que da de alta el super administrador.
    titular_tipo       text,
    titular_documento  text,
    titular_nombre     text,

    -- pendiente: se registró y todavía no confirmó su correo.
    estado             text        NOT NULL DEFAULT 'pendiente',
    suspendida_en      timestamptz,
    motivo_suspension  text,
    confirmada_en      timestamptz,

    -- Cuándo aceptó los términos de uso y la política de privacidad. El
    -- registro no deja seguir sin aceptarlos.
    terminos_aceptados_en timestamptz,

    -- Empresas nuevas por día (fase 3). El super administrador lo sube.
    tope_empresas_dia  integer     NOT NULL DEFAULT 50,

    -- registro: se registró en la pantalla. alta: la creó el super admin.
    origen             text        NOT NULL DEFAULT 'registro',

    creado_en          timestamptz NOT NULL DEFAULT now(),
    creado_por         uuid,
    modificado_en      timestamptz,
    modificado_por     uuid,
    activo             boolean     NOT NULL DEFAULT true,
    version            integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cuentas_tipo   CHECK (tipo IN ('negocio', 'profesional')),
    CONSTRAINT ck_cuentas_estado CHECK (estado IN ('pendiente', 'activa', 'suspendida')),
    CONSTRAINT ck_cuentas_origen CHECK (origen IN ('registro', 'alta')),
    CONSTRAINT ck_cuentas_tope   CHECK (tope_empresas_dia BETWEEN 1 AND 10000),

    -- Suspendida ⟺ con fecha y motivo.
    CONSTRAINT ck_cuentas_suspension CHECK (
        (estado = 'suspendida') = (suspendida_en IS NOT NULL)
        AND (suspendida_en IS NULL OR length(btrim(motivo_suspension)) > 0)),

    -- El titular: DNI de 8 dígitos o RUC de 11, con su nombre.
    CONSTRAINT ck_cuentas_titular CHECK (
        (titular_tipo IS NULL AND titular_documento IS NULL AND origen = 'alta')
        OR (titular_tipo = 'natural'  AND titular_documento ~ '^[0-9]{8}$'
            AND length(btrim(titular_nombre)) BETWEEN 2 AND 200)
        OR (titular_tipo = 'juridica' AND titular_documento ~ '^[0-9]{11}$'
            AND length(btrim(titular_nombre)) BETWEEN 2 AND 200))
);

COMMENT ON TABLE cuentas IS
    'El titular que se registra (Negocio o Profesional). Las empresas y los usuarios cuelgan de su cuenta. Nada se borra: se suspende.';

CREATE INDEX ix_cuentas_creado ON cuentas (creado_en DESC);


-- =============================================================================
-- 2. EMPRESAS Y USUARIOS CUELGAN DE UNA CUENTA
-- =============================================================================

ALTER TABLE empresas ADD COLUMN cuenta_id uuid REFERENCES cuentas (id);
ALTER TABLE usuarios ADD COLUMN cuenta_id uuid REFERENCES cuentas (id);

-- El alias: lo que se enseña en la plataforma en vez del correo. Único, en
-- minúsculas. Sale de lo que va antes de la @ y se puede elegir otro.
ALTER TABLE usuarios ADD COLUMN alias text;

CREATE INDEX ix_empresas_cuenta ON empresas (cuenta_id);
CREATE INDEX ix_usuarios_cuenta ON usuarios (cuenta_id);


-- --- El alias: formato y unicidad ------------------------------------------

CREATE OR REPLACE FUNCTION normalizar_alias(p_texto text)
RETURNS text
LANGUAGE sql IMMUTABLE AS $$
    -- Minúsculas, sin tildes comunes, solo letras, números, punto, guion y
    -- guion bajo; sin símbolos al principio; 30 como mucho.
    SELECT left(
             regexp_replace(
               regexp_replace(
                 translate(lower(coalesce(p_texto, '')),
                           'áéíóúüñàèìòù', 'aeiouunaeiou'),
                 '[^a-z0-9._-]', '', 'g'),
               '^[._-]+', ''),
             30);
$$;

-- SECURITY DEFINER: el alias es único en toda la plataforma, y el aislamiento
-- solo deja ver la propia empresa. Devuelve un booleano y nada más, igual que
-- correo_disponible().
CREATE OR REPLACE FUNCTION alias_disponible(p_alias text)
RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT NOT EXISTS (SELECT 1 FROM usuarios WHERE lower(alias) = lower(p_alias));
$$;

-- El primer alias libre a partir de una base: «juan», «juan2», «juan3»…
CREATE OR REPLACE FUNCTION alias_libre(p_base text)
RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_base text := normalizar_alias(p_base);
    v_alias text;
    i int := 1;
BEGIN
    IF length(v_base) < 3 THEN
        v_base := rpad(v_base, 3, 'x');
    END IF;
    v_base := left(v_base, 26);
    v_alias := v_base;

    WHILE EXISTS (SELECT 1 FROM usuarios WHERE lower(alias) = v_alias) LOOP
        i := i + 1;
        v_alias := v_base || i;
    END LOOP;

    RETURN v_alias;
END $$;

-- Las que llevan ya los usuarios de hoy: lo de antes de la @, con número si
-- choca. En orden de alta, para que el más antiguo se quede el limpio.
DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT id, correo FROM usuarios ORDER BY creado_en, id LOOP
        UPDATE usuarios SET alias = alias_libre(split_part(r.correo, '@', 1))
         WHERE id = r.id;
    END LOOP;
END $$;

ALTER TABLE usuarios ALTER COLUMN alias SET NOT NULL;

ALTER TABLE usuarios ADD CONSTRAINT ck_usuarios_alias
    CHECK (alias ~ '^[a-z0-9][a-z0-9._-]{2,29}$');

CREATE UNIQUE INDEX ux_usuarios_alias ON usuarios (lower(alias));

-- Si quien inserta no trae alias (el alta de empresa y el alta de usuario de
-- hoy no lo mandan), se pone solo.
CREATE OR REPLACE FUNCTION fn_usuarios_alias_y_cuenta() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
    IF NEW.alias IS NULL OR btrim(NEW.alias) = '' THEN
        NEW.alias := alias_libre(split_part(NEW.correo, '@', 1));
    ELSE
        NEW.alias := lower(btrim(NEW.alias));
    END IF;

    -- El usuario de una empresa es de la cuenta de esa empresa.
    IF NEW.cuenta_id IS NULL AND NEW.empresa_id IS NOT NULL THEN
        SELECT cuenta_id INTO NEW.cuenta_id FROM empresas WHERE id = NEW.empresa_id;
    END IF;

    RETURN NEW;
END $$;

CREATE TRIGGER tg_usuarios_alias_y_cuenta
    BEFORE INSERT ON usuarios
    FOR EACH ROW EXECUTE FUNCTION fn_usuarios_alias_y_cuenta();


-- --- Las empresas de hoy: una Cuenta Negocio cada una ----------------------

DO $$
DECLARE r record; v_cuenta uuid;
BEGIN
    FOR r IN SELECT id FROM empresas ORDER BY creado_en, id LOOP
        INSERT INTO cuentas (tipo, estado, confirmada_en, origen)
        VALUES ('negocio', 'activa', now(), 'alta')
        RETURNING id INTO v_cuenta;

        UPDATE empresas SET cuenta_id = v_cuenta WHERE id = r.id;
        UPDATE usuarios SET cuenta_id = v_cuenta WHERE empresa_id = r.id;
    END LOOP;
END $$;

-- Y las que dé de alta el super administrador, también. Antes de insertar,
-- para que la empresa nazca ya con su cuenta.
CREATE OR REPLACE FUNCTION fn_empresas_cuenta() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.cuenta_id IS NULL THEN
        INSERT INTO cuentas (tipo, estado, confirmada_en, origen)
        VALUES ('negocio', 'activa', now(), 'alta')
        RETURNING id INTO NEW.cuenta_id;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER tg_empresas_cuenta
    BEFORE INSERT ON empresas
    FOR EACH ROW EXECUTE FUNCTION fn_empresas_cuenta();


-- --- El ámbito del usuario ------------------------------------------------

ALTER TABLE usuarios DROP CONSTRAINT ck_usuarios_ambito;

ALTER TABLE usuarios ADD CONSTRAINT ck_usuarios_ambito CHECK (
    (es_super_admin AND empresa_id IS NULL AND cuenta_id IS NULL) OR
    (NOT es_super_admin AND (empresa_id IS NOT NULL OR cuenta_id IS NOT NULL))
);


-- =============================================================================
-- 3. AISLAMIENTO
-- =============================================================================

-- TU PROPIA FILA ES TUYA. Sin esto, quien se acaba de registrar —sin empresa
-- todavía— no se vería a sí mismo y la plataforma lo echaría en cada petición.
-- No amplía nada indebido: nadie gana acceso a una fila ajena.
DROP POLICY p_usuarios ON usuarios;

CREATE POLICY p_usuarios ON usuarios
    USING (ctx_es_super() OR empresa_id = ctx_empresa() OR id = ctx_usuario())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa() OR id = ctx_usuario());

ALTER TABLE cuentas ENABLE ROW LEVEL SECURITY;
ALTER TABLE cuentas FORCE  ROW LEVEL SECURITY;

-- Cada uno ve su cuenta; el super administrador, todas. Escribir, solo el
-- super administrador: el registro y la confirmación van por funciones.
CREATE POLICY p_cuentas ON cuentas
    USING (ctx_es_super()
           OR id = (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario()))
    WITH CHECK (ctx_es_super());

SELECT vigilar_tabla('cuentas');

GRANT SELECT, INSERT, UPDATE ON cuentas TO plataforma_app;


-- =============================================================================
-- 4. LOS INTENTOS DE REGISTRO (tope por red)
--
-- Uno por intento, salga bien o mal. Sirven para el tope de 10 por hora desde
-- la misma IP y para ver quién llama a la puerta. Solo inserción.
-- =============================================================================

CREATE TABLE registro_intentos (
    id         bigserial   PRIMARY KEY,
    ocurrido_en timestamptz NOT NULL DEFAULT now(),
    ip         inet,
    correo     text,
    resultado  text        NOT NULL
);

CREATE INDEX ix_registro_intentos_ip ON registro_intentos (ip, ocurrido_en DESC);

ALTER TABLE registro_intentos ENABLE ROW LEVEL SECURITY;
ALTER TABLE registro_intentos FORCE  ROW LEVEL SECURITY;

CREATE POLICY p_registro_intentos ON registro_intentos
    USING (ctx_es_super()) WITH CHECK (true);

SELECT vigilar_tabla_inmutable('registro_intentos');

GRANT SELECT, INSERT ON registro_intentos TO plataforma_app;
GRANT USAGE ON SEQUENCE registro_intentos_id_seq TO plataforma_app;


-- =============================================================================
-- 5. LA PUERTA DEL REGISTRO
--
-- SECURITY DEFINER, como las del ingreso y la invitación: quien se registra no
-- tiene sesión ni contexto, y tiene que poder saber si su correo y su alias
-- están libres en TODA la plataforma.
--
-- Todo en una: comprobar, anotar el intento, crear la cuenta, el usuario y el
-- enlace. Entre comprobar y crear no cabe un segundo registro con el mismo
-- correo: el índice único lo pararía y la transacción entera se deshace.
--
-- La contraseña del usuario es un marcador que no es un hash válido: nadie
-- puede entrar con ella. La elige al abrir el enlace (consumir_invitacion).
--
-- Devuelve un código: ok · demasiados · correo_ocupado · alias_ocupado.
-- =============================================================================

CREATE OR REPLACE FUNCTION registrar_cuenta(
    p_tipo              text,
    p_titular_tipo      text,
    p_titular_documento text,
    p_titular_nombre    text,
    p_correo            text,
    p_nombre            text,
    p_alias             text,
    p_token_hash        bytea,
    p_expira_en         timestamptz,
    p_ip                inet
) RETURNS TABLE (resultado text, usuario_id uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_alias   text := lower(btrim(p_alias));
    v_cuenta  uuid;
    v_usuario uuid;
BEGIN
    -- Tope de respaldo: 10 por hora desde la misma red. El captcha es la
    -- primera barrera; esta es la que queda si alguien lo resuelve pagando.
    IF p_ip IS NOT NULL AND (
        SELECT count(*) FROM registro_intentos
         WHERE ip = p_ip AND ocurrido_en > now() - interval '1 hour') >= 10
    THEN
        INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, p_correo, 'demasiados');
        RETURN QUERY SELECT 'demasiados'::text, NULL::uuid;
        RETURN;
    END IF;

    IF EXISTS (SELECT 1 FROM usuarios WHERE lower(correo) = lower(btrim(p_correo))) THEN
        INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, p_correo, 'correo_ocupado');
        RETURN QUERY SELECT 'correo_ocupado'::text, NULL::uuid;
        RETURN;
    END IF;

    IF EXISTS (SELECT 1 FROM usuarios WHERE lower(alias) = v_alias) THEN
        INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, p_correo, 'alias_ocupado');
        RETURN QUERY SELECT 'alias_ocupado'::text, NULL::uuid;
        RETURN;
    END IF;

    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, origen,
                         terminos_aceptados_en)
    VALUES (p_tipo, p_titular_tipo, p_titular_documento, btrim(p_titular_nombre), 'pendiente', 'registro',
            now())
    RETURNING id INTO v_cuenta;

    INSERT INTO usuarios (cuenta_id, correo, nombre, alias, clave_hash)
    VALUES (v_cuenta, btrim(p_correo), btrim(p_nombre), v_alias,
            'sin-clave$' || encode(gen_random_bytes(16), 'hex'))
    RETURNING id INTO v_usuario;

    INSERT INTO tokens_un_uso (usuario_id, proposito, token_hash, expira_en)
    VALUES (v_usuario, 'invitacion', p_token_hash, p_expira_en);

    INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, p_correo, 'ok');

    RETURN QUERY SELECT 'ok'::text, v_usuario;
END $$;

COMMENT ON FUNCTION registrar_cuenta IS
    'SECURITY DEFINER: quien se registra no tiene sesion. Crea cuenta pendiente, usuario sin clave valida y enlace de un uso, todo junto. Tope de 10 por hora y red.';


-- --- Reenviar el enlace de una cuenta que no confirmó ---------------------
--
-- Solo si la cuenta sigue pendiente y el último enlace tiene más de 60
-- segundos. Anula los anteriores. Devuelve a quién escribir, o nada: la
-- pantalla responde lo mismo exista o no el correo.

CREATE OR REPLACE FUNCTION reenviar_registro(
    p_correo     text,
    p_token_hash bytea,
    p_expira_en  timestamptz
) RETURNS TABLE (usuario_id uuid, nombre text, tipo text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_usuario uuid; v_nombre text; v_tipo text;
BEGIN
    SELECT u.id, u.nombre, c.tipo INTO v_usuario, v_nombre, v_tipo
      FROM usuarios u JOIN cuentas c ON c.id = u.cuenta_id
     WHERE lower(u.correo) = lower(btrim(p_correo))
       AND c.estado = 'pendiente' AND u.activo;

    IF v_usuario IS NULL THEN RETURN; END IF;

    IF EXISTS (SELECT 1 FROM tokens_un_uso t
                WHERE t.usuario_id = v_usuario
                  AND t.creado_en > now() - interval '60 seconds') THEN
        RETURN;
    END IF;

    UPDATE tokens_un_uso SET anulado_en = now()
     WHERE tokens_un_uso.usuario_id = v_usuario AND usado_en IS NULL AND anulado_en IS NULL;

    INSERT INTO tokens_un_uso (usuario_id, proposito, token_hash, expira_en)
    VALUES (v_usuario, 'invitacion', p_token_hash, p_expira_en);

    RETURN QUERY SELECT v_usuario, v_nombre, v_tipo;
END $$;


-- --- A quién avisar de un registro nuevo ----------------------------------

CREATE OR REPLACE FUNCTION correos_super_admin()
RETURNS TABLE (correo text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT correo FROM usuarios WHERE es_super_admin AND activo ORDER BY creado_en;
$$;


GRANT EXECUTE ON FUNCTION normalizar_alias(text)  TO plataforma_app;
GRANT EXECUTE ON FUNCTION alias_disponible(text)  TO plataforma_app;
GRANT EXECUTE ON FUNCTION alias_libre(text)       TO plataforma_app;
GRANT EXECUTE ON FUNCTION registrar_cuenta(text, text, text, text, text, text, text, bytea, timestamptz, inet) TO plataforma_app;
GRANT EXECUTE ON FUNCTION reenviar_registro(text, bytea, timestamptz) TO plataforma_app;
GRANT EXECUTE ON FUNCTION correos_super_admin()   TO plataforma_app;


-- =============================================================================
-- 6. INGRESO, SESIÓN E INVITACIÓN MIRAN LA CUENTA
--
-- Mismas firmas que antes (CREATE OR REPLACE): el C# no cambia. Lo único
-- nuevo es que una cuenta suspendida cuenta como «empresa no operativa», y
-- que usar el enlace de invitación activa la cuenta pendiente.
-- =============================================================================

CREATE OR REPLACE FUNCTION buscar_usuario_para_ingreso(p_correo text)
RETURNS TABLE (
    id                   uuid,
    empresa_id           uuid,
    nombre               text,
    clave_hash           text,
    activo               boolean,
    es_super_admin       boolean,
    intentos_fallidos    integer,
    bloqueado_hasta      timestamptz,
    dosfa_activo         boolean,
    clave_cambio_forzado boolean,
    empresa_operativa    boolean
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT u.id,
           u.empresa_id,
           u.nombre,
           u.clave_hash,
           u.activo,
           u.es_super_admin,
           u.intentos_fallidos,
           u.bloqueado_hasta,
           u.dosfa_activo,
           u.clave_cambio_forzado,
           COALESCE(e.activo AND e.suspendida_en IS NULL, true)
           AND COALESCE(c.estado <> 'suspendida', true)
      FROM usuarios u
      LEFT JOIN empresas e ON e.id = u.empresa_id
      LEFT JOIN cuentas  c ON c.id = u.cuenta_id
     WHERE lower(u.correo) = lower(p_correo);
$$;


CREATE OR REPLACE FUNCTION resolver_sesion(p_token_hash bytea)
RETURNS TABLE (
    sesion_id            uuid,
    usuario_id           uuid,
    empresa_id           uuid,
    es_super_admin       boolean,
    dosfa_superado       boolean,
    clave_cambio_forzado boolean,
    expira_en            timestamptz,
    revocada_en          timestamptz,
    usuario_activo       boolean,
    empresa_operativa    boolean,
    suplantador_id       uuid,
    suplantador_nombre   text,
    suplantado_nombre    text,
    suplantacion_inicio  timestamptz
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT s.id,
           COALESCE(sup.id, dueno.id),
           COALESCE(sup.empresa_id, dueno.empresa_id),
           CASE WHEN sup.id IS NOT NULL THEN false ELSE dueno.es_super_admin END,
           s.dosfa_superado,
           CASE WHEN sup.id IS NOT NULL THEN false ELSE dueno.clave_cambio_forzado END,
           s.expira_en,
           s.revocada_en,
           dueno.activo AND COALESCE(sup.activo, true),

           -- La empresa Y la cuenta de la identidad efectiva.
           COALESCE(
               CASE WHEN sup.id IS NOT NULL
                    THEN (e_sup.activo AND e_sup.suspendida_en IS NULL)
                    ELSE (e_due.activo AND e_due.suspendida_en IS NULL)
               END,
               true)
           AND COALESCE(
               CASE WHEN sup.id IS NOT NULL
                    THEN c_sup.estado <> 'suspendida'
                    ELSE c_due.estado <> 'suspendida'
               END,
               true),

           CASE WHEN sup.id IS NOT NULL THEN dueno.id END,
           CASE WHEN sup.id IS NOT NULL THEN dueno.nombre END,
           sup.nombre,
           s.suplantacion_inicio

      FROM sesiones s
      JOIN usuarios dueno    ON dueno.id = s.usuario_id
      LEFT JOIN usuarios sup ON sup.id  = s.suplantando_usuario_id
      LEFT JOIN empresas e_due ON e_due.id = dueno.empresa_id
      LEFT JOIN empresas e_sup ON e_sup.id = sup.empresa_id
      LEFT JOIN cuentas  c_due ON c_due.id = dueno.cuenta_id
      LEFT JOIN cuentas  c_sup ON c_sup.id = sup.cuenta_id
     WHERE s.token_hash = p_token_hash;
$$;


-- La pantalla del enlace saluda con la empresa; quien se registró todavía no
-- tiene: se le saluda con el titular de su cuenta.
CREATE OR REPLACE FUNCTION resolver_invitacion(p_token_hash bytea)
RETURNS TABLE (
    token_id          uuid,
    usuario_id        uuid,
    correo            text,
    nombre            text,
    empresa           text,
    proposito         text,
    expira_en         timestamptz,
    usado_en          timestamptz,
    anulado_en        timestamptz,
    usuario_activo    boolean,
    empresa_operativa boolean
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT t.id,
           u.id,
           u.correo,
           u.nombre,
           COALESCE(e.razon_social, e.nombre, c.titular_nombre),
           t.proposito,
           t.expira_en,
           t.usado_en,
           t.anulado_en,
           u.activo,
           COALESCE(e.activo AND e.suspendida_en IS NULL, true)
           AND COALESCE(c.estado <> 'suspendida', true)
      FROM tokens_un_uso t
      JOIN usuarios u  ON u.id = t.usuario_id
      LEFT JOIN empresas e ON e.id = u.empresa_id
      LEFT JOIN cuentas  c ON c.id = u.cuenta_id
     WHERE t.token_hash = p_token_hash
       AND t.proposito IN ('invitacion', 'restablecer_clave');
$$;


-- La de la 008, con dos líneas nuevas: no entra quien tenga la cuenta
-- suspendida, y usar el enlace ACTIVA la cuenta pendiente — es la prueba de
-- que el correo es suyo.
CREATE OR REPLACE FUNCTION consumir_invitacion(
    p_token_hash bytea,
    p_clave_hash text,
    p_secreto    bytea,
    p_ip         inet
) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_token   tokens_un_uso%ROWTYPE;
    v_usuario usuarios%ROWTYPE;
BEGIN
    SELECT * INTO v_token
      FROM tokens_un_uso
     WHERE token_hash = p_token_hash
       AND proposito IN ('invitacion', 'restablecer_clave')
       FOR UPDATE;

    IF NOT FOUND
       OR v_token.usado_en   IS NOT NULL
       OR v_token.anulado_en IS NOT NULL
       OR v_token.expira_en  <= now()
    THEN
        RETURN NULL;
    END IF;

    SELECT * INTO v_usuario FROM usuarios WHERE id = v_token.usuario_id;

    IF NOT FOUND OR NOT v_usuario.activo THEN
        RETURN NULL;
    END IF;

    IF v_usuario.empresa_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM empresas
         WHERE id = v_usuario.empresa_id
           AND activo AND suspendida_en IS NULL
    ) THEN
        RETURN NULL;
    END IF;

    -- NUEVO: una cuenta suspendida no entra por la puerta de atrás.
    IF v_usuario.cuenta_id IS NOT NULL AND EXISTS (
        SELECT 1 FROM cuentas WHERE id = v_usuario.cuenta_id AND estado = 'suspendida'
    ) THEN
        RETURN NULL;
    END IF;

    IF p_secreto IS NULL THEN
        RETURN NULL;
    END IF;

    UPDATE usuarios
       SET clave_hash           = p_clave_hash,
           clave_cambiada_en    = now(),
           clave_cambio_forzado = false,
           intentos_fallidos    = 0,
           bloqueado_hasta      = NULL,
           secreto_2fa          = p_secreto,
           dosfa_activo         = true,
           dosfa_activado_en    = now(),
           dosfa_ultimo_paso    = NULL
     WHERE id = v_usuario.id;

    -- NUEVO: el correo quedó confirmado; la cuenta pasa a activa.
    UPDATE cuentas
       SET estado = 'activa', confirmada_en = now()
     WHERE id = v_usuario.cuenta_id AND estado = 'pendiente';

    UPDATE tokens_un_uso
       SET usado_en = now(), usado_desde = p_ip
     WHERE id = v_token.id;

    UPDATE tokens_un_uso
       SET anulado_en = now()
     WHERE usuario_id = v_usuario.id
       AND id <> v_token.id
       AND usado_en   IS NULL
       AND anulado_en IS NULL;

    UPDATE sesiones
       SET revocada_en = now(), motivo_revocacion = 'clave establecida por invitacion'
     WHERE usuario_id = v_usuario.id
       AND revocada_en IS NULL;

    UPDATE codigos_recuperacion
       SET usado_en = now()
     WHERE usuario_id = v_usuario.id
       AND usado_en IS NULL;

    RETURN v_usuario.id;
END;
$$;


-- --- Eventos nuevos ---------------------------------------------------------

ALTER TABLE eventos_seguridad DROP CONSTRAINT ck_eventos_tipo;

ALTER TABLE eventos_seguridad ADD CONSTRAINT ck_eventos_tipo CHECK (tipo IN (
    'ingreso', 'ingreso_clave_incorrecta', 'ingreso_usuario_inexistente',
    'ingreso_usuario_inactivo', 'ingreso_usuario_bloqueado',
    'ingreso_empresa_suspendida', 'cierre_sesion', 'sesion_expirada',
    'sesion_revocada',
    'dosfa_superado', 'dosfa_fallido', 'dosfa_activado', 'dosfa_desactivado',
    'codigo_recuperacion_usado',
    'clave_cambiada', 'clave_restablecer_solicitado', 'clave_restablecer_usado',
    'clave_restablecer_invalido',
    'acceso_denegado', 'permiso_otorgado', 'permiso_retirado', 'rol_asignado',
    'rol_retirado', 'suplantacion_iniciada', 'suplantacion_terminada',
    'empresa_suspendida', 'empresa_reactivada', 'credencial_facturacion_guardada',
    'credencial_facturacion_rotada', 'credencial_facturacion_usada_fallo',
    -- 016
    'cuenta_registrada', 'cuenta_suspendida', 'cuenta_reactivada',
    'cuenta_tope_cambiado',
    'otro'
));


-- =============================================================================
-- COMPROBACIONES
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM usuarios WHERE alias IS NULL) THEN
        RAISE EXCEPTION 'Hay usuarios sin alias.';
    END IF;

    IF EXISTS (SELECT 1 FROM empresas WHERE cuenta_id IS NULL) THEN
        RAISE EXCEPTION 'Hay empresas sin cuenta.';
    END IF;

    IF EXISTS (SELECT 1 FROM usuarios
                WHERE empresa_id IS NOT NULL AND cuenta_id IS NULL) THEN
        RAISE EXCEPTION 'Hay usuarios de empresa sin cuenta.';
    END IF;

    IF EXISTS (SELECT 1 FROM tablas_sin_vigilar) THEN
        RAISE EXCEPTION 'Quedó una tabla sin vigilar.';
    END IF;
END $$;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('016_cuentas_y_registro');

COMMIT;