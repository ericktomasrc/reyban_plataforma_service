-- =============================================================================
-- 039 — RECUPERAR EL SEGUNDO FACTOR SIN NADIE, CON ESPERA
--        (21 oct 2026, aprobado por Erick: «ya hazlo de una vez todo»)
--
-- Para quien perdió el teléfono Y sus ocho códigos (por ejemplo, le robaron
-- el teléfono y la laptop). Es lo que hacen Google, AWS o Apple:
--
--   1. En la pantalla del código (ya puso su CONTRASEÑA), pide recuperar.
--   2. Le llega un enlace a SU CORREO. Al abrirlo:
--        · se cierran TODAS sus sesiones (la laptop robada queda fuera);
--        · empieza la espera: 24 h un usuario, 72 h un administrador,
--          7 días un super administrador (y solo si hay OTRO super admin);
--        · se avisa a él y a quienes están por encima (administradores de
--          su empresa, dueño de su cuenta; a los super admins si es
--          administrador o si no hay nadie más).
--   3. Pasada la espera, al entrar con su contraseña configura un teléfono
--      nuevo (QR y ocho códigos nuevos). Sin que nadie intervenga.
--
-- SE CANCELA SOLA si el dueño entra con su teléfono o con un código (prueba
-- de que no lo perdió: quien la pidió no era él). Y la cancela cualquier
-- administrador de su empresa, o un super administrador.
--
-- Los avisos NO llevan enlaces (regla de la plataforma): se cancela desde
-- dentro, en Usuarios o en Plataforma › Recuperaciones.
--
-- Nadie lee ni escribe la tabla directamente: solo por las funciones.
-- No se borra nada. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS recuperaciones_2fa (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id       uuid        NOT NULL REFERENCES usuarios (id),
    empresa_id       uuid        REFERENCES empresas (id),

    -- por_confirmar → en_espera → completada; o cancelada / vencida.
    estado           text        NOT NULL DEFAULT 'por_confirmar',
    nivel            text        NOT NULL,
    horas_espera     integer     NOT NULL,

    -- El enlace del correo (SHA-256), y hasta cuándo sirve.
    token_hash       bytea       NOT NULL,
    token_expira_en  timestamptz NOT NULL,

    pedida_en        timestamptz NOT NULL DEFAULT now(),
    pedida_desde     inet,
    confirmada_en    timestamptz,
    confirmada_desde inet,
    disponible_en    timestamptz,
    completada_en    timestamptz,
    cancelada_en     timestamptz,
    cancelada_por    uuid,
    motivo_cancelacion text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_rec2fa_estado CHECK (estado IN
        ('por_confirmar', 'en_espera', 'completada', 'cancelada', 'vencida')),
    CONSTRAINT ck_rec2fa_nivel  CHECK (nivel IN ('usuario', 'administrador', 'super')),
    CONSTRAINT ck_rec2fa_espera CHECK (horas_espera BETWEEN 1 AND 720)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_rec2fa_token ON recuperaciones_2fa (token_hash);

-- Una sola en curso por persona.
CREATE UNIQUE INDEX IF NOT EXISTS ux_rec2fa_en_curso
    ON recuperaciones_2fa (usuario_id) WHERE estado IN ('por_confirmar', 'en_espera');

COMMENT ON TABLE recuperaciones_2fa IS
    'Recuperar el segundo factor sin nadie, con espera (039). Solo por funciones; token_hash nunca a la bitácora.';

ALTER TABLE recuperaciones_2fa ENABLE ROW LEVEL SECURITY;
ALTER TABLE recuperaciones_2fa FORCE  ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_recuperaciones_2fa') THEN
        PERFORM vigilar_tabla('recuperaciones_2fa', ARRAY['token_hash']);
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON recuperaciones_2fa TO plataforma_puertas;


-- =============================================================================
-- QUIÉN ES (para la espera) Y A QUIÉN SE AVISA
-- =============================================================================

-- usuario (24 h) · administrador (72 h) · super (7 días)
CREATE OR REPLACE FUNCTION nivel_recuperacion_2fa(p_usuario uuid)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT CASE
             WHEN u.es_super_admin THEN 'super'
             WHEN u.nivel_cuenta IN ('dueno', 'colaborador_admin') THEN 'administrador'
             WHEN EXISTS (SELECT 1 FROM usuario_rol ur
                           WHERE ur.usuario_id = u.id AND ur.activo
                             AND ur.rol_id = '11111111-1111-1111-1111-111111111111') THEN 'administrador'
             ELSE 'usuario'
           END
      FROM usuarios u WHERE u.id = p_usuario;
$$;
ALTER FUNCTION nivel_recuperacion_2fa(uuid) OWNER TO plataforma_puertas;


-- Los correos que reciben el aviso, SIN el de la propia persona (a ella se le
-- escribe aparte). Si no queda nadie por encima, los super administradores.
CREATE OR REPLACE FUNCTION avisados_recuperacion_2fa(p_usuario uuid)
RETURNS text[]
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_u      usuarios%ROWTYPE;
    v_nivel  text := nivel_recuperacion_2fa(p_usuario);
    v_lista  text[];
BEGIN
    SELECT * INTO v_u FROM usuarios WHERE id = p_usuario;
    IF NOT FOUND THEN RETURN ARRAY[]::text[]; END IF;

    SELECT coalesce(array_agg(DISTINCT c), ARRAY[]::text[]) INTO v_lista
      FROM (
            -- Los administradores de su empresa.
            SELECT lower(btrim(a.correo)) AS c
              FROM usuarios a
              JOIN usuario_rol ur ON ur.usuario_id = a.id AND ur.activo
             WHERE v_u.empresa_id IS NOT NULL
               AND a.empresa_id = v_u.empresa_id
               AND a.activo AND a.id <> v_u.id
               AND ur.rol_id = '11111111-1111-1111-1111-111111111111'
            UNION
            -- El dueño y los colaboradores administradores de su cuenta.
            SELECT lower(btrim(a.correo))
              FROM usuarios a
             WHERE v_u.cuenta_id IS NOT NULL
               AND a.cuenta_id = v_u.cuenta_id
               AND a.nivel_cuenta IN ('dueno', 'colaborador_admin')
               AND a.activo AND a.id <> v_u.id
           ) x
     WHERE c IS NOT NULL AND c <> '';

    IF v_nivel IN ('administrador', 'super') OR cardinality(v_lista) = 0 THEN
        SELECT coalesce(array_agg(DISTINCT c), ARRAY[]::text[]) INTO v_lista
          FROM (SELECT unnest(v_lista) AS c
                UNION
                SELECT lower(btrim(s.correo)) FROM usuarios s
                 WHERE s.es_super_admin AND s.activo AND s.id <> v_u.id) y
         WHERE c IS NOT NULL AND c <> '';
    END IF;

    RETURN v_lista;
END $$;
ALTER FUNCTION avisados_recuperacion_2fa(uuid) OWNER TO plataforma_puertas;


-- Lo que se cuenta de una recuperación (para el correo y las pantallas).
CREATE OR REPLACE FUNCTION json_recuperacion_2fa(p_id uuid)
RETURNS json
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT json_build_object(
               'id',           r.id,
               'usuarioId',    r.usuario_id,
               'nombre',       u.nombre,
               'correo',       u.correo,
               'empresa',      coalesce(e.nombre, 'la plataforma'),
               'estado',       r.estado,
               'nivel',        r.nivel,
               'horasEspera',  r.horas_espera,
               'pedidaEn',     r.pedida_en,
               'disponibleEn', r.disponible_en,
               'lista',        r.estado = 'en_espera' AND r.disponible_en <= now(),
               'avisados',     to_json(avisados_recuperacion_2fa(r.usuario_id)))
      FROM recuperaciones_2fa r
      JOIN usuarios u ON u.id = r.usuario_id
      LEFT JOIN empresas e ON e.id = r.empresa_id
     WHERE r.id = p_id;
$$;
ALTER FUNCTION json_recuperacion_2fa(uuid) OWNER TO plataforma_puertas;


-- =============================================================================
-- 1. PEDIR (con la sesión a medias: la contraseña ya se comprobó)
--
-- Devuelve json con `resultado`:
--   pedida        → mandar el correo de confirmación (trae los datos)
--   en_curso      → ya hay una en espera (trae los datos; no se manda nada)
--   espera        → se pidió hace menos de un minuto
--   sin_factor    → no tiene segundo factor: lo configura normalmente
--   no_permitida  → super administrador sin otro super administrador
-- =============================================================================
CREATE OR REPLACE FUNCTION pedir_recuperacion_2fa(p_token_hash bytea, p_expira timestamptz)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_u     usuarios%ROWTYPE;
    v_r     recuperaciones_2fa%ROWTYPE;
    v_nivel text;
    v_horas integer;
BEGIN
    SELECT * INTO v_u FROM usuarios WHERE id = ctx_usuario() AND activo;
    IF NOT FOUND THEN RETURN json_build_object('resultado', 'sin_sesion')::text; END IF;

    IF NOT v_u.dosfa_activo THEN
        RETURN json_build_object('resultado', 'sin_factor')::text;
    END IF;

    v_nivel := nivel_recuperacion_2fa(v_u.id);
    v_horas := CASE v_nivel WHEN 'super' THEN 168 WHEN 'administrador' THEN 72 ELSE 24 END;

    -- Un super administrador solo, si otro puede cancelarla.
    IF v_nivel = 'super' AND NOT EXISTS (
        SELECT 1 FROM usuarios s
         WHERE s.es_super_admin AND s.activo AND s.dosfa_activo AND s.id <> v_u.id
    ) THEN
        RETURN json_build_object('resultado', 'no_permitida')::text;
    END IF;

    SELECT * INTO v_r FROM recuperaciones_2fa
     WHERE usuario_id = v_u.id AND estado IN ('por_confirmar', 'en_espera')
       FOR UPDATE;

    IF FOUND AND v_r.estado = 'en_espera' THEN
        RETURN json_build_object('resultado', 'en_curso',
                                 'recuperacion', json_recuperacion_2fa(v_r.id))::text;
    END IF;

    IF FOUND THEN
        -- Por confirmar: un correo nuevo, como mucho uno por minuto.
        IF coalesce(v_r.modificado_en, v_r.pedida_en) > now() - interval '60 seconds' THEN
            RETURN json_build_object('resultado', 'espera')::text;
        END IF;
        UPDATE recuperaciones_2fa
           SET token_hash = p_token_hash, token_expira_en = p_expira,
               pedida_desde = ctx_ip(), nivel = v_nivel, horas_espera = v_horas
         WHERE id = v_r.id;
    ELSE
        INSERT INTO recuperaciones_2fa
            (usuario_id, empresa_id, nivel, horas_espera, token_hash, token_expira_en, pedida_desde)
        VALUES (v_u.id, v_u.empresa_id, v_nivel, v_horas, p_token_hash, p_expira, ctx_ip())
        RETURNING * INTO v_r;
    END IF;

    RETURN json_build_object('resultado', 'pedida',
                             'recuperacion', json_recuperacion_2fa(v_r.id))::text;
END $$;
ALTER FUNCTION pedir_recuperacion_2fa(bytea, timestamptz) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION pedir_recuperacion_2fa(bytea, timestamptz) TO plataforma_app;


-- =============================================================================
-- 2. CONFIRMAR CON EL ENLACE DEL CORREO (sin sesión)
--
-- Empieza la espera y CIERRA TODAS SUS SESIONES. Nulo si el enlace no sirve.
-- =============================================================================
CREATE OR REPLACE FUNCTION confirmar_recuperacion_2fa(p_token_hash bytea, p_ip inet)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_r recuperaciones_2fa%ROWTYPE;
BEGIN
    SELECT * INTO v_r FROM recuperaciones_2fa
     WHERE token_hash = p_token_hash
       FOR UPDATE;

    IF NOT FOUND OR v_r.estado <> 'por_confirmar' OR v_r.token_expira_en <= now() THEN
        RETURN NULL;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM usuarios WHERE id = v_r.usuario_id AND activo) THEN
        RETURN NULL;
    END IF;

    UPDATE recuperaciones_2fa
       SET estado = 'en_espera',
           confirmada_en = now(),
           confirmada_desde = p_ip,
           disponible_en = now() + make_interval(hours => v_r.horas_espera)
     WHERE id = v_r.id;

    UPDATE sesiones
       SET revocada_en = now(), motivo_revocacion = 'recuperacion del segundo factor pedida'
     WHERE usuario_id = v_r.usuario_id AND revocada_en IS NULL;

    RETURN json_recuperacion_2fa(v_r.id)::text;
END $$;
ALTER FUNCTION confirmar_recuperacion_2fa(bytea, inet) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION confirmar_recuperacion_2fa(bytea, inet) TO plataforma_app;


-- =============================================================================
-- 3. LA MÍA (para la pantalla del código, con la sesión a medias)
-- =============================================================================
CREATE OR REPLACE FUNCTION mi_recuperacion_2fa()
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT json_recuperacion_2fa(r.id)::text
      FROM recuperaciones_2fa r
     WHERE r.usuario_id = ctx_usuario()
       AND r.estado IN ('por_confirmar', 'en_espera')
     LIMIT 1;
$$;
ALTER FUNCTION mi_recuperacion_2fa() OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION mi_recuperacion_2fa() TO plataforma_app;


-- =============================================================================
-- 4. COMPLETAR: pasada la espera, borrar el factor viejo para configurar el
--    nuevo en esta misma sesión. Las demás sesiones ya se cerraron al
--    confirmar; se cierran otra vez por si acaso, menos esta.
-- =============================================================================
CREATE OR REPLACE FUNCTION completar_recuperacion_2fa(p_sesion uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_r recuperaciones_2fa%ROWTYPE;
BEGIN
    SELECT * INTO v_r FROM recuperaciones_2fa
     WHERE usuario_id = ctx_usuario() AND estado = 'en_espera'
       FOR UPDATE;

    IF NOT FOUND OR v_r.disponible_en > now() THEN
        RETURN NULL;
    END IF;

    UPDATE usuarios
       SET secreto_2fa = NULL, dosfa_activo = false,
           dosfa_activado_en = NULL, dosfa_ultimo_paso = NULL,
           intentos_fallidos = 0, bloqueado_hasta = NULL
     WHERE id = v_r.usuario_id;

    UPDATE codigos_recuperacion SET usado_en = now()
     WHERE usuario_id = v_r.usuario_id AND usado_en IS NULL;

    UPDATE sesiones
       SET revocada_en = now(), motivo_revocacion = 'segundo factor recuperado'
     WHERE usuario_id = v_r.usuario_id AND revocada_en IS NULL
       AND id IS DISTINCT FROM p_sesion;

    UPDATE recuperaciones_2fa SET estado = 'completada', completada_en = now() WHERE id = v_r.id;

    RETURN json_recuperacion_2fa(v_r.id)::text;
END $$;
ALTER FUNCTION completar_recuperacion_2fa(uuid) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION completar_recuperacion_2fa(uuid) TO plataforma_app;


-- =============================================================================
-- 5. CANCELAR
--
--   · cancelar_mi_recuperacion_2fa(): la cancela su dueño al entrar con su
--     teléfono o con un código (prueba de que no lo perdió).
--   · cancelar_recuperacion_2fa(id): un administrador de SU empresa (permiso
--     «usuarios.desactivar») o un super administrador.
-- =============================================================================
CREATE OR REPLACE FUNCTION cancelar_mi_recuperacion_2fa()
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_id uuid;
BEGIN
    UPDATE recuperaciones_2fa
       SET estado = 'cancelada', cancelada_en = now(), cancelada_por = ctx_usuario(),
           motivo_cancelacion = 'entró con su segundo factor'
     WHERE usuario_id = ctx_usuario() AND estado IN ('por_confirmar', 'en_espera')
    RETURNING id INTO v_id;

    RETURN CASE WHEN v_id IS NULL THEN NULL ELSE json_recuperacion_2fa(v_id)::text END;
END $$;
ALTER FUNCTION cancelar_mi_recuperacion_2fa() OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION cancelar_mi_recuperacion_2fa() TO plataforma_app;


CREATE OR REPLACE FUNCTION cancelar_recuperacion_2fa(p_id uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_r recuperaciones_2fa%ROWTYPE;
BEGIN
    SELECT * INTO v_r FROM recuperaciones_2fa
     WHERE id = p_id AND estado IN ('por_confirmar', 'en_espera')
       FOR UPDATE;
    IF NOT FOUND THEN RETURN NULL; END IF;

    IF NOT ctx_es_super() THEN
        IF ctx_usuario() IS NULL
           OR v_r.usuario_id = ctx_usuario()
           OR v_r.empresa_id IS DISTINCT FROM ctx_empresa()
           OR NOT EXISTS (SELECT 1 FROM permisos_efectivos pe
                           WHERE pe.usuario_id = ctx_usuario()
                             AND pe.permiso_codigo = 'usuarios.desactivar') THEN
            RETURN NULL;
        END IF;
    END IF;

    UPDATE recuperaciones_2fa
       SET estado = 'cancelada', cancelada_en = now(), cancelada_por = ctx_usuario(),
           motivo_cancelacion = CASE WHEN ctx_es_super() THEN 'cancelada por un super administrador'
                                     ELSE 'cancelada por un administrador de la empresa' END
     WHERE id = v_r.id;

    RETURN json_recuperacion_2fa(v_r.id)::text;
END $$;
ALTER FUNCTION cancelar_recuperacion_2fa(uuid) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION cancelar_recuperacion_2fa(uuid) TO plataforma_app;


-- =============================================================================
-- 6. LAS QUE ESTÁN EN CURSO (para Usuarios y para Plataforma › Recuperaciones)
--
-- El super administrador ve todas; un administrador, las de su empresa (con
-- «usuarios.ver»); nadie más ve ninguna. Sin la lista de avisados.
-- =============================================================================
CREATE OR REPLACE FUNCTION recuperaciones_2fa_en_curso()
RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() AND NOT EXISTS (
        SELECT 1 FROM permisos_efectivos pe
         WHERE pe.usuario_id = ctx_usuario() AND pe.permiso_codigo = 'usuarios.ver'
    ) THEN
        RETURN '[]';
    END IF;

    RETURN coalesce((
        SELECT json_agg(json_build_object(
                   'id',           r.id,
                   'usuarioId',    r.usuario_id,
                   'nombre',       u.nombre,
                   'correo',       u.correo,
                   'empresa',      coalesce(e.nombre, 'la plataforma'),
                   'estado',       r.estado,
                   'nivel',        r.nivel,
                   'pedidaEn',     r.pedida_en,
                   'disponibleEn', r.disponible_en,
                   'lista',        r.estado = 'en_espera' AND r.disponible_en <= now())
                 ORDER BY r.pedida_en DESC)
          FROM recuperaciones_2fa r
          JOIN usuarios u ON u.id = r.usuario_id
          LEFT JOIN empresas e ON e.id = r.empresa_id
         WHERE r.estado IN ('por_confirmar', 'en_espera')
           AND (ctx_es_super() OR (r.empresa_id = ctx_empresa() AND r.usuario_id <> ctx_usuario()))
    ), '[]'::json)::text;
END $$;
ALTER FUNCTION recuperaciones_2fa_en_curso() OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION recuperaciones_2fa_en_curso() TO plataforma_app;


-- Las que nadie confirmó en dos días quedan vencidas (se llama al pedir otra
-- y desde el listado; no hace falta un proceso aparte).
CREATE OR REPLACE FUNCTION vencer_recuperaciones_2fa()
RETURNS integer
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
    WITH v AS (
        UPDATE recuperaciones_2fa SET estado = 'vencida'
         WHERE estado = 'por_confirmar' AND token_expira_en < now() - interval '2 days'
        RETURNING 1)
    SELECT count(*)::int FROM v;
$$;
ALTER FUNCTION vencer_recuperaciones_2fa() OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION vencer_recuperaciones_2fa() TO plataforma_app;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('039_recuperar_segundo_factor')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN RAISE NOTICE '039: recuperar el segundo factor con espera (24 h / 72 h / 7 días). Nada se borró.'; END $$;