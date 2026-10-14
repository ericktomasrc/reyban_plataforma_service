-- =============================================================================
-- 020 — QUIEN SE REGISTRA ES EL DUEÑO DE SU CUENTA
--
-- Fallo encontrado el 12 de octubre: la 018 marcó como «dueño» a los que ya
-- se habían registrado, pero registrar_cuenta() (016) seguía creando el
-- usuario SIN nivel de cuenta. Para la base era entonces un usuario de una
-- sola empresa, y el asistente respondía «Tu usuario es de una empresa, no
-- tiene cuenta propia».
--
--   1. registrar_cuenta() crea al usuario con nivel_cuenta = 'dueno'.
--   2. Se arreglan los que se registraron entre la 018 y esta: el primer
--      usuario de cada cuenta de registro que no tenga dueño.
--
-- No se borra nada.
-- =============================================================================

BEGIN;

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

    -- 020: quien se registra es el DUEÑO de su cuenta. Sin esto quedaba como
    -- «usuario de una sola empresa» y el asistente no le dejaba agregar la suya.
    INSERT INTO usuarios (cuenta_id, correo, nombre, alias, clave_hash, nivel_cuenta)
    VALUES (v_cuenta, btrim(p_correo), btrim(p_nombre), v_alias,
            'sin-clave$' || encode(gen_random_bytes(16), 'hex'), 'dueno')
    RETURNING id INTO v_usuario;

    INSERT INTO tokens_un_uso (usuario_id, proposito, token_hash, expira_en)
    VALUES (v_usuario, 'invitacion', p_token_hash, p_expira_en);

    INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, p_correo, 'ok');

    RETURN QUERY SELECT 'ok'::text, v_usuario;
END $$;


-- 2. Los que quedaron sin nivel: el primer usuario de cada cuenta de
--    registro que todavía no tiene dueño activo.
UPDATE usuarios u
   SET nivel_cuenta = 'dueno'
  FROM (SELECT DISTINCT ON (u2.cuenta_id) u2.id
          FROM usuarios u2
          JOIN cuentas c ON c.id = u2.cuenta_id
         WHERE c.origen = 'registro' AND NOT u2.es_super_admin AND u2.activo
           AND NOT EXISTS (SELECT 1 FROM usuarios d
                            WHERE d.cuenta_id = u2.cuenta_id AND d.nivel_cuenta = 'dueno' AND d.activo)
         ORDER BY u2.cuenta_id, u2.creado_en, u2.id) primero
 WHERE u.id = primero.id AND u.nivel_cuenta IS NULL;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('020_registro_crea_dueno');

COMMIT;