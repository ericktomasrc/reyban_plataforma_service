-- =============================================================================
-- 048 — LA PERSONA IDENTIFICADA (24 oct 2026)
--
--   1. FICHA OBLIGATORIA antes de lo importante (emitir, anular, notas,
--      agregar una empresa). `ficha_plazo`: hasta cuándo se tolera la ficha
--      incompleta. Los que ya existen tienen 15 días desde hoy; los nuevos,
--      ninguno (pero solo se les pide al hacer algo importante).
--   2. «TUS DATOS» AL ACEPTAR UNA INVITACIÓN: `ficha_confirmada_en`. Quien fue
--      creado por otra persona (invitado) la revisa o la completa al entrar,
--      antes de ver nada. Si cierra, se le pide al volver. Los que ya
--      entraron alguna vez quedan como confirmados.
--   3. CAMBIAR EL CORREO uno mismo: `cambios_correo`. Con contraseña y
--      código; vale cuando se abre el enlace que llega al correo NUEVO; se
--      avisa al anterior. confirmar_cambio_correo() lo aplica (sin sesión).
--   4. Mi equipo dice si la invitación venció.
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- --- 1 y 2: plazo y confirmación de la ficha --------------------------------------

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'usuarios' AND column_name = 'ficha_plazo') THEN
        ALTER TABLE usuarios ADD COLUMN ficha_plazo timestamptz;
        ALTER TABLE usuarios ADD COLUMN ficha_confirmada_en timestamptz;
        -- Solo la primera vez: los de hoy, 15 días de aviso; los que ya
        -- entraron alguna vez no pasan por «Tus datos».
        UPDATE usuarios SET ficha_plazo = now() + interval '15 days';
        UPDATE usuarios SET ficha_confirmada_en = ultimo_ingreso_en WHERE ultimo_ingreso_en IS NOT NULL;
    END IF;
END $$;

COMMENT ON COLUMN usuarios.ficha_plazo IS
    '048: hasta cuándo puede emitir, anular o hacer notas con la ficha incompleta. NULL = ya no (se le pide).';
COMMENT ON COLUMN usuarios.ficha_confirmada_en IS
    '048: cuándo revisó o completó su ficha. Un invitado sin esto ve «Tus datos» al entrar.';


-- --- 3: cambio de correo -----------------------------------------------------------

CREATE TABLE IF NOT EXISTS cambios_correo (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id      uuid        NOT NULL REFERENCES usuarios (id),
    correo_nuevo    text        NOT NULL,
    token_hash      bytea       NOT NULL,
    expira_en       timestamptz NOT NULL,
    usado_en        timestamptz,
    anulado_en      timestamptz,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cambios_correo CHECK (correo_nuevo ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$')
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_cambios_correo_token ON cambios_correo (token_hash);
CREATE INDEX IF NOT EXISTS ix_cambios_correo_usuario ON cambios_correo (usuario_id, creado_en DESC);

ALTER TABLE cambios_correo ENABLE ROW LEVEL SECURITY;
ALTER TABLE cambios_correo FORCE  ROW LEVEL SECURITY;

-- Cada quien pide los suyos. El enlace lo resuelve la función (sin sesión).
DROP POLICY IF EXISTS p_cambios_correo ON cambios_correo;
CREATE POLICY p_cambios_correo ON cambios_correo
    USING (ctx_es_super() OR usuario_id = ctx_usuario())
    WITH CHECK (usuario_id = ctx_usuario());

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'cambios_correo'::regclass AND NOT tgisinternal) THEN
        PERFORM vigilar_tabla('cambios_correo', ARRAY['token_hash']);
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON cambios_correo TO plataforma_app;

-- Aplica el cambio del enlace. json: {resultado: ok|no_existe|vencido|ocupado,
-- anterior, nuevo, nombre}. Un enlace sirve una vez; los demás pedidos de
-- esa persona quedan anulados.
CREATE OR REPLACE FUNCTION confirmar_cambio_correo(p_token_hash bytea)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_c   cambios_correo%ROWTYPE;
    v_ant text;
    v_nom text;
BEGIN
    SELECT * INTO v_c FROM cambios_correo WHERE token_hash = p_token_hash FOR UPDATE;
    IF v_c.id IS NULL OR v_c.usado_en IS NOT NULL OR v_c.anulado_en IS NOT NULL THEN
        RETURN json_build_object('resultado', 'no_existe')::text;
    END IF;
    IF v_c.expira_en <= now() THEN
        RETURN json_build_object('resultado', 'vencido')::text;
    END IF;
    IF EXISTS (SELECT 1 FROM usuarios WHERE lower(correo) = lower(v_c.correo_nuevo) AND id <> v_c.usuario_id) THEN
        RETURN json_build_object('resultado', 'ocupado')::text;
    END IF;

    SELECT correo, nombre INTO v_ant, v_nom FROM usuarios WHERE id = v_c.usuario_id AND activo;
    IF v_ant IS NULL THEN RETURN json_build_object('resultado', 'no_existe')::text; END IF;

    UPDATE usuarios SET correo = v_c.correo_nuevo WHERE id = v_c.usuario_id;
    UPDATE cambios_correo SET usado_en = now() WHERE id = v_c.id;
    UPDATE cambios_correo SET anulado_en = now()
     WHERE usuario_id = v_c.usuario_id AND id <> v_c.id AND usado_en IS NULL AND anulado_en IS NULL;

    PERFORM registrar_evento('otro'::text, true, v_c.usuario_id, NULL, v_c.correo_nuevo,
        jsonb_build_object('accion', 'correo_cambiado_por_el_mismo', 'anterior', v_ant));

    RETURN json_build_object('resultado', 'ok', 'anterior', v_ant, 'nuevo', v_c.correo_nuevo, 'nombre', v_nom)::text;
END $$;

ALTER FUNCTION confirmar_cambio_correo(bytea) OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION confirmar_cambio_correo(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION confirmar_cambio_correo(bytea) TO plataforma_app;


-- --- 4: Mi equipo con «invitación vencida» (misma firma que en la 046) -------------

CREATE OR REPLACE FUNCTION equipo()
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE(json_agg(p ORDER BY p.orden, p.activo DESC, p.nombre), '[]'::json)::text
      FROM (
        SELECT o.id, o.nombre, o.correo, o.alias, o.nivel_cuenta AS nivel, o.ve_todas AS "veTodas",
               o.activo, o.ultimo_ingreso_en IS NULL AS pendiente,
               -- 048: nunca entró y su último enlace ya venció (o no hay ninguno vigente).
               (o.ultimo_ingreso_en IS NULL AND NOT EXISTS (
                    SELECT 1 FROM tokens_un_uso t
                     WHERE t.usuario_id = o.id AND t.proposito = 'invitacion'
                       AND t.usado_en IS NULL AND t.anulado_en IS NULL AND t.expira_en > now())) AS "invitacionVencida",
               CASE o.nivel_cuenta WHEN 'dueno' THEN 0 WHEN 'colaborador_admin' THEN 1 ELSE 2 END AS orden,
               o.tipo_documento AS "tipoDocumento", o.numero_documento AS "numeroDocumento",
               o.nombres, o.apellido_paterno AS "apellidoPaterno", o.apellido_materno AS "apellidoMaterno",
               o.celular, o.fecha_nacimiento AS "fechaNacimiento", o.direccion, o.ubigeo, o.cargo,
               o.fecha_ingreso AS "fechaIngreso",
               (o.numero_documento IS NULL OR o.nombres IS NULL OR o.apellido_paterno IS NULL
                OR (o.tipo_documento = '1' AND o.apellido_materno IS NULL)) AS "fichaIncompleta",
               COALESCE((
                 SELECT json_agg(json_build_object(
                          'empresaId', e.id,
                          'empresa', e.nombre,
                          'rolId', COALESCE(rx.rol_id, CASE WHEN o.nivel_cuenta = 'colaborador'
                                                            THEN '22222222-2222-2222-2222-222222222222'::uuid
                                                            ELSE '11111111-1111-1111-1111-111111111111'::uuid END),
                          'rol', COALESCE(rx.nombre, CASE WHEN o.nivel_cuenta = 'colaborador' THEN 'Facturador' ELSE 'Administrador' END),
                          'notas', EXISTS (SELECT 1 FROM usuario_rol_empresa n
                                            WHERE n.usuario_id = o.id AND n.empresa_id = e.id AND n.activo
                                              AND n.rol_id = '66666666-6666-6666-6666-666666666666'),
                          'alcance', CASE WHEN o.nivel_cuenta = 'colaborador' THEN COALESCE(a.alcance, 'empresa') ELSE 'empresa' END,
                          'defecto', a.sucursal_defecto_id,
                          'sucursales', COALESCE((SELECT json_agg(us.sucursal_id) FROM usuario_sucursal us
                                                   WHERE us.usuario_id = o.id AND us.empresa_id = e.id AND us.activo), '[]'::json))
                        ORDER BY e.nombre)
                   FROM empresas e
                   LEFT JOIN LATERAL (
                        SELECT x.rol_id, r.nombre FROM usuario_rol_empresa x JOIN roles r ON r.id = x.rol_id
                         WHERE x.usuario_id = o.id AND x.empresa_id = e.id AND x.activo
                           AND x.rol_id <> '66666666-6666-6666-6666-666666666666'
                         LIMIT 1) rx ON true
                   LEFT JOIN usuario_alcance a ON a.usuario_id = o.id AND a.empresa_id = e.id AND a.activo
                  WHERE e.cuenta_id = o.cuenta_id AND e.activo
                    AND (o.nivel_cuenta = 'dueno' OR o.ve_todas
                         OR EXISTS (SELECT 1 FROM usuario_empresa ue
                                     WHERE ue.usuario_id = o.id AND ue.empresa_id = e.id AND ue.activo))
               ), '[]'::json) AS accesos
          FROM usuarios yo
          JOIN cuentas c  ON c.id = yo.cuenta_id AND c.tipo = 'profesional'
          JOIN usuarios o ON o.cuenta_id = yo.cuenta_id AND o.nivel_cuenta IS NOT NULL
         WHERE yo.id = ctx_usuario() AND yo.activo
           AND yo.nivel_cuenta IN ('dueno', 'colaborador_admin')
      ) p;
$$;

ALTER FUNCTION equipo() OWNER TO plataforma_puertas;



-- --- 5: reenviar la invitación de alguien de Mi equipo --------------------------
-- Solo a quien nunca entró, y solo quien puede gestionarlo (equipo_puede).
-- Anula los enlaces anteriores y deja uno nuevo. Devuelve json:
-- {resultado: ok|no|no_existe|ya_entro, correo, nombre}
CREATE OR REPLACE FUNCTION equipo_reinvitar(p_usuario uuid, p_token_hash bytea, p_expira_en timestamptz)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_puede text := equipo_puede(p_usuario);
    v_o     usuarios%ROWTYPE;
BEGIN
    IF v_puede <> 'si' THEN
        RETURN json_build_object('resultado', v_puede)::text;
    END IF;
    SELECT * INTO v_o FROM usuarios WHERE id = p_usuario;
    IF NOT v_o.activo THEN RETURN json_build_object('resultado', 'no_existe')::text; END IF;
    IF v_o.ultimo_ingreso_en IS NOT NULL THEN RETURN json_build_object('resultado', 'ya_entro')::text; END IF;

    UPDATE tokens_un_uso SET anulado_en = now()
     WHERE usuario_id = p_usuario AND proposito = 'invitacion' AND usado_en IS NULL AND anulado_en IS NULL;
    INSERT INTO tokens_un_uso (usuario_id, proposito, token_hash, expira_en, creado_por)
    VALUES (p_usuario, 'invitacion', p_token_hash, p_expira_en, ctx_usuario());

    PERFORM registrar_evento('otro'::text, true, p_usuario, NULL, v_o.correo,
        jsonb_build_object('accion', 'invitacion_reenviada'));

    RETURN json_build_object('resultado', 'ok', 'correo', v_o.correo, 'nombre', v_o.nombre)::text;
END $$;

ALTER FUNCTION equipo_reinvitar(uuid, bytea, timestamptz) OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION equipo_reinvitar(uuid, bytea, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION equipo_reinvitar(uuid, bytea, timestamptz) TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('048_persona_identificada')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '048: ficha obligatoria (15 días de aviso), «Tus datos» al aceptar una invitación y cambio de correo listos. Nada se borró.';
END $$;