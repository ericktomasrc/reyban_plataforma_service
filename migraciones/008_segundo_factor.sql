-- =============================================================================
-- 008 — SEGUNDO FACTOR OBLIGATORIO
--
-- Nadie entra sin él. Se configura al abrir el enlace de invitación, en el
-- mismo paso en que se elige la contraseña.
--
-- LAS COLUMNAS YA EXISTEN desde la 002 (`secreto_2fa`, `dosfa_activo`,
-- `dosfa_activado_en`) y la tabla `codigos_recuperacion` desde la 003. Aquí se
-- añade lo que faltaba para que el flujo funcione de punta a punta.
-- =============================================================================


-- =============================================================================
-- QUE UN CÓDIGO NO SIRVA DOS VECES
--
-- Un código TOTP vale 30 segundos, y durante esos 30 segundos es válido tantas
-- veces como se presente. Quien mire por encima del hombro mientras alguien lo
-- teclea tiene media ventana para usarlo él.
--
-- Guardando el último paso aceptado, el segundo intento con el mismo código
-- se rechaza aunque siga dentro de su ventana.
-- =============================================================================

ALTER TABLE usuarios
    ADD COLUMN dosfa_ultimo_paso bigint;

COMMENT ON COLUMN usuarios.dosfa_ultimo_paso IS
    'Número de ventana TOTP del último código aceptado. Impide reutilizar el mismo código dentro de sus 30 segundos.';

-- NO SE VUELVE A LLAMAR A `vigilar_tabla`: los disparadores de `usuarios` ya
-- existen desde la 002 y la lista de columnas tapadas no cambia. Llamarla otra
-- vez falla, porque no borra los disparadores anteriores antes de crearlos.
--
-- La columna nueva queda vigilada igual, sin hacer nada: el disparador compara
-- la fila entera. Y no añade ruido a la bitácora porque se actualiza dentro
-- del mismo UPDATE que ya registra cada ingreso.


-- =============================================================================
-- CONSUMIR LA INVITACIÓN, AHORA CON SEGUNDO FACTOR
--
-- Reemplaza a la de la 007. La diferencia: activa también el segundo factor,
-- en la misma transacción.
--
-- POR QUÉ JUNTO Y NO EN DOS PASOS: si la contraseña se guardara primero y el
-- segundo factor después, una ventana cerrada en medio dejaría una cuenta con
-- contraseña y sin factor — es decir, una cuenta que no puede entrar y cuyo
-- enlace ya se gastó. Así, o queda todo hecho, o el enlace sigue sirviendo.
--
-- EL SECRETO LLEGA COMO PARÁMETRO, ya cifrado con la llave maestra, y la
-- aplicación ya comprobó un código contra él antes de llamar aquí.
--
-- POR QUÉ VIAJA EN LA PETICIÓN Y NO SE GUARDA A MEDIAS EN LA BASE: quien abre
-- el enlace no tiene sesión, así que un secreto guardado antes de confirmarlo
-- haría falta poder leerlo después sin sesión — es decir, una función capaz de
-- devolver el secreto de alguien a quien solo se conoce por su token. Mejor no
-- tener esa función. Y no se pierde nada: quien tiene el token puede elegir el
-- secreto que quiera de todas formas, porque el factor va a ser suyo.
-- =============================================================================

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

    -- SIN SECRETO NO SE TERMINA. El segundo factor es obligatorio, así que una
    -- cuenta a la que se le fijara contraseña sin tenerlo quedaría sin poder
    -- entrar nunca. Que falle aquí es preferible.
    IF p_secreto IS NULL THEN
        RETURN NULL;
    END IF;

    UPDATE usuarios
       SET clave_hash           = p_clave_hash,
           clave_cambiada_en    = now(),
           clave_cambio_forzado = false,
           intentos_fallidos    = 0,
           bloqueado_hasta      = NULL,

           -- El secreto y su activación, juntos: no existe el estado
           -- intermedio de «tiene secreto pero no vale».
           secreto_2fa          = p_secreto,
           dosfa_activo         = true,
           dosfa_activado_en    = now(),
           dosfa_ultimo_paso    = NULL
     WHERE id = v_usuario.id;

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

    -- Los códigos de recuperación viejos dejan de valer: los nuevos se generan
    -- ahora. Si no se borraran, alguien que hubiera guardado los de hace un
    -- año entraría saltándose el factor recién configurado.
    UPDATE codigos_recuperacion
       SET usado_en = now()
     WHERE usuario_id = v_usuario.id
       AND usado_en IS NULL;

    RETURN v_usuario.id;
END;
$$;


-- =============================================================================
-- QUEMAR UN CÓDIGO DE RECUPERACIÓN
--
-- Se usa DURANTE el ingreso, cuando la sesión existe pero todavía no ha
-- superado el segundo factor. En ese estado el contexto sí está fijado, así
-- que esta no necesita ser SECURITY DEFINER — pero sí necesita ser atómica.
--
-- `FOR UPDATE` otra vez: dos pestañas con el mismo código lo usarían las dos.
-- =============================================================================

CREATE OR REPLACE FUNCTION usar_codigo_recuperacion(
    p_usuario_id  uuid,
    p_codigo_hash bytea,
    p_ip          inet
) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_id uuid;
BEGIN
    SELECT id INTO v_id
      FROM codigos_recuperacion
     WHERE usuario_id  = p_usuario_id
       AND codigo_hash = p_codigo_hash
       AND usado_en IS NULL
       FOR UPDATE;

    IF NOT FOUND THEN
        RETURN false;
    END IF;

    UPDATE codigos_recuperacion
       SET usado_en = now(), usado_desde = p_ip
     WHERE id = v_id;

    RETURN true;
END;
$$;


-- =============================================================================
-- BORRAR EL SEGUNDO FACTOR
--
-- Para cuando alguien perdió el teléfono Y los códigos. Su administrador lo
-- reinicia, y el enlace de invitación que recibe le deja configurar otro.
--
-- ES UNA OPERACIÓN DELICADA: quien la ejecuta está bajando la barrera de otra
-- persona. Por eso queda en la bitácora por el disparador de `usuarios`, que
-- anota `secreto_2fa` entre los campos cambiados sin guardar su valor.
-- =============================================================================

CREATE OR REPLACE FUNCTION reiniciar_segundo_factor(p_usuario_id uuid)
RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
BEGIN
    UPDATE usuarios
       SET secreto_2fa       = NULL,
           dosfa_activo      = false,
           dosfa_activado_en = NULL,
           dosfa_ultimo_paso = NULL
     WHERE id = p_usuario_id;

    IF NOT FOUND THEN
        RETURN false;
    END IF;

    UPDATE codigos_recuperacion
       SET usado_en = now()
     WHERE usuario_id = p_usuario_id
       AND usado_en IS NULL;

    -- Sus sesiones se cortan. Una sesión abierta que ya superó el factor
    -- seguiría valiendo, y el reinicio se hace justo cuando se sospecha que
    -- alguien más la tiene.
    UPDATE sesiones
       SET revocada_en = now(), motivo_revocacion = 'segundo factor reiniciado'
     WHERE usuario_id = p_usuario_id
       AND revocada_en IS NULL;

    RETURN true;
END;
$$;


-- La versión de tres argumentos que creó la 007 se queda sin usar y sin
-- sentido: el segundo factor ya no es opcional. Borrarla evita que alguien la
-- llame por error y cree una cuenta sin factor.
DROP FUNCTION IF EXISTS consumir_invitacion(bytea, text, inet);

GRANT EXECUTE ON FUNCTION consumir_invitacion(bytea, text, bytea, inet) TO plataforma_app;
GRANT EXECUTE ON FUNCTION usar_codigo_recuperacion(uuid, bytea, inet)  TO plataforma_app;
GRANT EXECUTE ON FUNCTION reiniciar_segundo_factor(uuid)               TO plataforma_app;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('008_segundo_factor');
