-- =============================================================================
-- 038 — RECUPERAR EL ACCESO POR CORREO, Y LOS CORREOS QUE NO SALIERON
--        (21 oct 2026, maquetas 4, 5 y 6 aprobadas)
--
-- 1. «¿Olvidaste tu contraseña o no te llegó el correo?» en el login. Quien
--    lo pide escribe su correo y el sistema decide qué mandarle:
--      · nunca entró (registro sin confirmar, o invitado que no usó su
--        enlace)  → el enlace de siempre: contraseña Y segundo factor;
--      · ya entraba → un enlace para cambiar SOLO la contraseña. El segundo
--        factor y los códigos de recuperación NO se tocan: con el correo solo
--        no se puede quitar el segundo factor a nadie (quien perdió también
--        el teléfono lo pide a su administrador, como hasta ahora).
--    Un pedido por minuto y cinco por hora por persona. La respuesta al
--    navegador es siempre la misma, exista o no el correo.
--
-- 2. Los correos de acceso que no salieron (registro, invitación,
--    recuperación) quedan anotados, con el motivo, para el super
--    administrador. Cuando uno sale bien, su fila se da por resuelta.
--
-- Nadie lee ni escribe estas cosas directamente: solo por las funciones.
-- No se borra nada. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- =============================================================================
-- 1. LOS CORREOS QUE NO SALIERON
-- =============================================================================

CREATE TABLE IF NOT EXISTS correos_fallidos (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    destino        text        NOT NULL,
    tipo           text        NOT NULL,
    usuario_id     uuid        REFERENCES usuarios (id),
    -- En palabras simples («Gmail rechazó la contraseña»), no el error crudo.
    motivo         text        NOT NULL,
    intentos       integer     NOT NULL DEFAULT 1,
    ultimo_en      timestamptz NOT NULL DEFAULT now(),
    resuelto_en    timestamptz,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_correos_fallidos_tipo CHECK (tipo IN
        ('confirmar_registro', 'invitacion', 'restablecer_clave'))
);

-- Uno abierto por destino y tipo: el segundo fallo suma un intento.
CREATE UNIQUE INDEX IF NOT EXISTS ux_correos_fallidos_abierto
    ON correos_fallidos (lower(destino), tipo) WHERE resuelto_en IS NULL;

COMMENT ON TABLE correos_fallidos IS
    'Correos de acceso que no salieron, para el super administrador. Solo por funciones.';

ALTER TABLE correos_fallidos ENABLE ROW LEVEL SECURITY;
ALTER TABLE correos_fallidos FORCE  ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_correos_fallidos') THEN
        PERFORM vigilar_tabla('correos_fallidos');
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON correos_fallidos TO plataforma_puertas;


-- Anota el resultado de un envío. Salió → se resuelven las filas abiertas de
-- ese destino. No salió → se abre una, o se le suma un intento.
-- Sin sesión también: el registro y la recuperación no la tienen.
CREATE OR REPLACE FUNCTION anotar_correo(
    p_destino text,
    p_tipo    text,
    p_usuario uuid,
    p_enviado boolean,
    p_motivo  text
) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF p_destino IS NULL OR btrim(p_destino) = ''
       OR p_tipo NOT IN ('confirmar_registro', 'invitacion', 'restablecer_clave') THEN
        RETURN;
    END IF;

    -- Salió: ese buzón ya recibió un enlace vigente (el último anula los
    -- anteriores), así que se resuelve TODO lo abierto de ese destino.
    IF p_enviado THEN
        UPDATE correos_fallidos
           SET resuelto_en = now()
         WHERE lower(destino) = lower(btrim(p_destino))
           AND resuelto_en IS NULL;
        RETURN;
    END IF;

    UPDATE correos_fallidos
       SET intentos  = intentos + 1,
           ultimo_en = now(),
           motivo    = left(coalesce(nullif(btrim(p_motivo), ''), 'No se pudo enviar'), 300),
           usuario_id = coalesce(p_usuario, usuario_id)
     WHERE lower(destino) = lower(btrim(p_destino))
       AND tipo = p_tipo
       AND resuelto_en IS NULL;

    IF NOT FOUND THEN
        INSERT INTO correos_fallidos (destino, tipo, usuario_id, motivo)
        VALUES (lower(btrim(p_destino)), p_tipo, p_usuario,
                left(coalesce(nullif(btrim(p_motivo), ''), 'No se pudo enviar'), 300));
    END IF;
END $$;

ALTER FUNCTION anotar_correo(text, text, uuid, boolean, text) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION anotar_correo(text, text, uuid, boolean, text) TO plataforma_app;


-- La lista del super administrador: los abiertos, el más reciente primero.
CREATE OR REPLACE FUNCTION correos_fallidos_abiertos()
RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RETURN NULL; END IF;

    RETURN coalesce((
        SELECT json_agg(json_build_object(
                   'id',        f.id,
                   'destino',   f.destino,
                   'tipo',      f.tipo,
                   'motivo',    f.motivo,
                   'intentos',  f.intentos,
                   'ultimoEn',  f.ultimo_en,
                   'usuarioId', f.usuario_id,
                   'nombre',    u.nombre)
                 ORDER BY f.ultimo_en DESC)
          FROM (SELECT * FROM correos_fallidos
                 WHERE resuelto_en IS NULL
                 ORDER BY ultimo_en DESC LIMIT 200) f
          LEFT JOIN usuarios u ON u.id = f.usuario_id
    ), '[]'::json)::text;
END $$;

ALTER FUNCTION correos_fallidos_abiertos() OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION correos_fallidos_abiertos() TO plataforma_app;


-- El destino de una fila (para reenviar desde la lista). Solo el super admin.
CREATE OR REPLACE FUNCTION correo_fallido_destino(p_id uuid)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT f.destino FROM correos_fallidos f
     WHERE f.id = p_id AND f.resuelto_en IS NULL AND ctx_es_super();
$$;

ALTER FUNCTION correo_fallido_destino(uuid) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION correo_fallido_destino(uuid) TO plataforma_app;


-- =============================================================================
-- 2. PEDIR UN ENLACE PARA RECUPERAR EL ACCESO
--
-- Devuelve una fila SOLO si hay que mandar un correo. Sin fila: el correo no
-- existe, la persona o su empresa no están operativas, o pidió hace muy poco.
-- El navegador nunca se entera de cuál de esas fue.
--
--   proposito = 'invitacion'        nunca entró: contraseña y segundo factor
--   proposito = 'restablecer_clave' ya entraba: solo la contraseña
-- =============================================================================
CREATE OR REPLACE FUNCTION pedir_enlace_acceso(
    p_correo          text,
    p_token_hash      bytea,
    p_expira_primera  timestamptz,
    p_expira_clave    timestamptz
) RETURNS TABLE (
    usuario_id uuid,
    nombre     text,
    correo     text,
    proposito  text,
    pendiente  boolean,
    cuenta_tipo text,
    empresa    text,
    expira_en  timestamptz
)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_usr   usuarios%ROWTYPE;
    v_cta   cuentas%ROWTYPE;
    v_emp   text;
    v_op    boolean;
    v_prop  text;
    v_exp   timestamptz;
BEGIN
    IF p_correo IS NULL OR btrim(p_correo) = '' THEN RETURN; END IF;

    SELECT * INTO v_usr FROM usuarios u
     WHERE lower(u.correo) = lower(btrim(p_correo)) AND u.activo
     LIMIT 1;
    IF NOT FOUND THEN RETURN; END IF;

    SELECT * INTO v_cta FROM cuentas c WHERE c.id = v_usr.cuenta_id;

    SELECT COALESCE(e.razon_social, e.nombre),
           COALESCE(e.activo AND e.suspendida_en IS NULL, true)
      INTO v_emp, v_op
      FROM empresas e WHERE e.id = v_usr.empresa_id;
    v_op := COALESCE(v_op, true) AND COALESCE(v_cta.estado <> 'suspendida', true);
    IF NOT v_op THEN RETURN; END IF;

    -- Un pedido por minuto y cinco por hora, contando todos los enlaces de la
    -- persona (también los que mandó un administrador).
    IF EXISTS (SELECT 1 FROM tokens_un_uso t
                WHERE t.usuario_id = v_usr.id
                  AND t.creado_en > now() - interval '60 seconds') THEN
        RETURN;
    END IF;
    IF (SELECT count(*) FROM tokens_un_uso t
         WHERE t.usuario_id = v_usr.id
           AND t.creado_en > now() - interval '1 hour') >= 5 THEN
        RETURN;
    END IF;

    IF v_usr.ultimo_ingreso_en IS NULL OR v_usr.clave_hash IS NULL OR v_cta.estado = 'pendiente' THEN
        v_prop := 'invitacion';
        v_exp  := p_expira_primera;
    ELSE
        v_prop := 'restablecer_clave';
        v_exp  := p_expira_clave;
    END IF;

    -- Los enlaces anteriores dejan de valer ahora.
    UPDATE tokens_un_uso t
       SET anulado_en = now()
     WHERE t.usuario_id = v_usr.id AND t.usado_en IS NULL AND t.anulado_en IS NULL;

    INSERT INTO tokens_un_uso (usuario_id, proposito, token_hash, expira_en)
    VALUES (v_usr.id, v_prop, p_token_hash, v_exp);

    RETURN QUERY SELECT v_usr.id, v_usr.nombre, v_usr.correo, v_prop,
                        COALESCE(v_cta.estado = 'pendiente', false),
                        v_cta.tipo,
                        COALESCE(v_emp, v_cta.titular_nombre, 'la plataforma'),
                        v_exp;
END $$;

ALTER FUNCTION pedir_enlace_acceso(text, bytea, timestamptz, timestamptz) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION pedir_enlace_acceso(text, bytea, timestamptz, timestamptz) TO plataforma_app;


-- =============================================================================
-- 3. CAMBIAR LA CONTRASEÑA CON EL ENLACE (SOLO LA CONTRASEÑA)
--
-- Solo acepta enlaces 'restablecer_clave'. El segundo factor y los códigos de
-- recuperación quedan como estaban: al entrar, se piden igual. Se cierran las
-- sesiones abiertas (si alguien usaba la contraseña vieja, queda fuera).
-- =============================================================================
CREATE OR REPLACE FUNCTION consumir_restablecimiento(
    p_token_hash bytea,
    p_clave_hash text,
    p_ip         inet
) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_token tokens_un_uso%ROWTYPE;
    v_usr   usuarios%ROWTYPE;
BEGIN
    IF p_clave_hash IS NULL OR btrim(p_clave_hash) = '' THEN RETURN NULL; END IF;

    SELECT * INTO v_token FROM tokens_un_uso t
     WHERE t.token_hash = p_token_hash AND t.proposito = 'restablecer_clave'
       FOR UPDATE;

    IF NOT FOUND
       OR v_token.usado_en   IS NOT NULL
       OR v_token.anulado_en IS NOT NULL
       OR v_token.expira_en  <= now() THEN
        RETURN NULL;
    END IF;

    SELECT * INTO v_usr FROM usuarios WHERE id = v_token.usuario_id;
    IF NOT FOUND OR NOT v_usr.activo THEN RETURN NULL; END IF;

    IF v_usr.empresa_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM empresas WHERE id = v_usr.empresa_id AND activo AND suspendida_en IS NULL
    ) THEN
        RETURN NULL;
    END IF;

    IF v_usr.cuenta_id IS NOT NULL AND EXISTS (
        SELECT 1 FROM cuentas WHERE id = v_usr.cuenta_id AND estado = 'suspendida'
    ) THEN
        RETURN NULL;
    END IF;

    UPDATE usuarios
       SET clave_hash           = p_clave_hash,
           clave_cambiada_en    = now(),
           clave_cambio_forzado = false,
           intentos_fallidos    = 0,
           bloqueado_hasta      = NULL
     WHERE id = v_usr.id;

    UPDATE tokens_un_uso SET usado_en = now(), usado_desde = p_ip WHERE id = v_token.id;

    UPDATE tokens_un_uso
       SET anulado_en = now()
     WHERE usuario_id = v_usr.id AND id <> v_token.id
       AND usado_en IS NULL AND anulado_en IS NULL;

    UPDATE sesiones
       SET revocada_en = now(), motivo_revocacion = 'contraseña cambiada con enlace del correo'
     WHERE usuario_id = v_usr.id AND revocada_en IS NULL;

    RETURN v_usr.id;
END $$;

ALTER FUNCTION consumir_restablecimiento(bytea, text, inet) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION consumir_restablecimiento(bytea, text, inet) TO plataforma_app;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('038_recuperar_acceso')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN RAISE NOTICE '038: recuperar el acceso por correo y lista de correos que no salieron. Nada se borró.'; END $$;