-- =============================================================================
-- 007 — INVITACIONES POR CORREO
--
-- Cuando se crea una cuenta, la persona recibe un enlace de un solo uso y
-- elige ella misma su contraseña. NINGUNA CONTRASEÑA VIAJA POR CORREO.
--
-- POR QUÉ IMPORTA: un correo con una contraseña dentro se queda en la bandeja
-- de entrada para siempre, se reenvía, se sincroniza con el móvil y aparece
-- en las copias de seguridad del proveedor. Un enlace que caduca en unas horas
-- y solo sirve una vez no deja nada detrás.
--
-- La tabla `tokens_un_uso` ya existe desde la 003, con el propósito
-- `invitacion` previsto. Aquí se añade lo que faltaba:
--
--   1. El plazo del enlace, configurable por empresa.
--   2. Las dos funciones que permiten usar el enlace SIN haber iniciado
--      sesión — que es el problema de fondo de esta migración.
-- =============================================================================


-- =============================================================================
-- EL PLAZO, POR EMPRESA
--
-- Con valor por defecto, para que una empresa recién creada funcione sin que
-- nadie configure nada. El tope de 168 horas son siete días: más allá, un
-- enlace olvidado en un buzón deja de ser una comodidad y pasa a ser una
-- llave abandonada.
-- =============================================================================

ALTER TABLE empresas
    ADD COLUMN horas_invitacion integer NOT NULL DEFAULT 24;

ALTER TABLE empresas
    ADD CONSTRAINT ck_empresas_horas_invitacion
    CHECK (horas_invitacion BETWEEN 1 AND 168);

COMMENT ON COLUMN empresas.horas_invitacion IS
    'Horas que dura un enlace de invitación o de restablecimiento para los usuarios de esta empresa.';


-- =============================================================================
-- LA TERCERA PUERTA
--
-- Las dos primeras están en la 003: `buscar_usuario_para_ingreso` y
-- `resolver_sesion`. Esta es la misma historia.
--
-- EL PROBLEMA: quien abre un enlace de invitación NO TIENE SESIÓN. No puede
-- tenerla: todavía no tiene contraseña. Así que el contexto está vacío, y con
-- el contexto vacío el aislamiento no deja leer ni una fila de `usuarios`.
--
-- Sin estas funciones, la pantalla de invitación no podría ni saber a qué
-- correo pertenece el enlace.
--
-- QUÉ LAS HACE SEGURAS, ya que saltan el aislamiento:
--
--   · Solo se puede llegar a una fila con el hash del token, que son 32 bytes
--     al azar. Adivinarlo no es una posibilidad práctica.
--   · No devuelven nada cifrado, ni el hash de la contraseña, ni el secreto
--     del segundo factor.
--   · Reciben el hash, no el token. Lo que se guarda en la base no sirve para
--     entrar: quien lea la tabla entera no puede construir un enlace válido.
-- =============================================================================

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
           e.razon_social,
           t.proposito,
           t.expira_en,
           t.usado_en,
           t.anulado_en,
           u.activo,
           -- Un super administrador no tiene empresa: para él, siempre true.
           COALESCE(e.activo AND e.suspendida_en IS NULL, true)
      FROM tokens_un_uso t
      JOIN usuarios u  ON u.id = t.usuario_id
      LEFT JOIN empresas e ON e.id = u.empresa_id
     WHERE t.token_hash = p_token_hash
       AND t.proposito IN ('invitacion', 'restablecer_clave');
$$;

COMMENT ON FUNCTION resolver_invitacion IS
    'SECURITY DEFINER a propósito: quien abre un enlace de invitación todavía no tiene contraseña, así que no puede tener sesión ni contexto. Devuelve solo lo que la pantalla necesita enseñar; nada cifrado.';


-- =============================================================================
-- CONSUMIR EL ENLACE
--
-- Todo lo que sigue ocurre o no ocurre entero. Es una sola función y no cuatro
-- llamadas desde la aplicación porque entre una y otra cabe una segunda
-- pestaña usando el mismo enlace.
--
-- EL `FOR UPDATE` ES LA PIEZA CLAVE. Dos peticiones simultáneas con el mismo
-- token llegan aquí a la vez; la primera bloquea la fila, la segunda espera, y
-- cuando entra ya ve `usado_en` puesto y se va con las manos vacías. Sin ese
-- bloqueo, las dos leerían «sin usar» y las dos establecerían una contraseña
-- distinta — ganaría la última, sin que nadie se entere.
--
-- Devuelve el identificador del usuario, o NULL si el enlace no servía. No se
-- distingue entre caducado, usado o inexistente: quien prueba enlaces al azar
-- no merece saber cuál de las tres cosas acertó.
-- =============================================================================

CREATE OR REPLACE FUNCTION consumir_invitacion(
    p_token_hash bytea,
    p_clave_hash text,
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

    -- Una cuenta desactivada no se resucita con un enlace viejo.
    IF NOT FOUND OR NOT v_usuario.activo THEN
        RETURN NULL;
    END IF;

    -- Ni una empresa suspendida deja entrar a los suyos por la puerta de atrás.
    IF v_usuario.empresa_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM empresas
         WHERE id = v_usuario.empresa_id
           AND activo AND suspendida_en IS NULL
    ) THEN
        RETURN NULL;
    END IF;

    UPDATE usuarios
       SET clave_hash           = p_clave_hash,
           clave_cambiada_en    = now(),
           -- Ya eligió la suya: no hay nada que forzarle a cambiar.
           clave_cambio_forzado = false,
           -- Si llegó aquí bloqueado por intentos fallidos, el bloqueo se
           -- levanta. Si no, acabaría de poner una contraseña que tampoco le
           -- deja entrar.
           intentos_fallidos    = 0,
           bloqueado_hasta      = NULL
     WHERE id = v_usuario.id;

    UPDATE tokens_un_uso
       SET usado_en = now(), usado_desde = p_ip
     WHERE id = v_token.id;

    -- Los demás enlaces pendientes de esta persona dejan de valer. Si pidió
    -- tres veces «reenviar», los otros dos siguen siendo llaves buenas.
    UPDATE tokens_un_uso
       SET anulado_en = now()
     WHERE usuario_id = v_usuario.id
       AND id <> v_token.id
       AND usado_en   IS NULL
       AND anulado_en IS NULL;

    -- Y sus sesiones abiertas se cortan. Si alguien entró con la contraseña
    -- anterior —que es justo lo que se teme cuando hay que restablecerla—,
    -- cambiarla sin cerrar sesiones no lo echa.
    UPDATE sesiones
       SET revocada_en = now(), motivo_revocacion = 'clave establecida por invitacion'
     WHERE usuario_id = v_usuario.id
       AND revocada_en IS NULL;

    RETURN v_usuario.id;
END;
$$;

COMMENT ON FUNCTION consumir_invitacion IS
    'SECURITY DEFINER y todo en una transacción: entre leer el token y marcarlo como usado no puede caber una segunda pestaña. Devuelve NULL sin distinguir entre caducado, usado o inexistente.';


GRANT EXECUTE ON FUNCTION resolver_invitacion(bytea)               TO plataforma_app;
GRANT EXECUTE ON FUNCTION consumir_invitacion(bytea, text, inet)   TO plataforma_app;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('007_invitaciones');
