-- =============================================================================
-- 088 — REFUERZOS DE SEGURIDAD DE LA REVISIÓN QA (8 nov 2026, e239)
--
-- B4: la regla de `usuarios` deja a cada uno editar SU fila (perfil, clave,
--     ficha). Eso incluía, sin querer, las columnas que dicen a qué empresa
--     pertenece, su nivel en la cuenta y si es de la plataforma. Hoy ninguna
--     ruta de la API las cambia así, pero la base no debe depender de eso.
--     Ahora un disparador las protege: solo las cambian las funciones de la
--     plataforma (crear empresa, traspasar, editar colaborador…) o el super
--     administrador. Y nadie que no sea super puede crear un usuario de la
--     plataforma ni darle permisos de plataforma.
--
-- B5: tokens de un uso, códigos de recuperación y sesiones tenían reglas
--     abiertas o con un hueco:
--       · sesiones: «sin empresa en el contexto» dejaba ver y cerrar sesiones
--         de otros a quien entraba sin empresa elegida (dueño de Cartera,
--         equipo del estudio). Ahora el hueco es solo para quien todavía NO
--         ha entrado (en el ingreso, sin usuario en el contexto).
--       · códigos de recuperación: solo los propios.
--       · tokens de un uso: los propios, o los de alguien que ya puedes ver
--         (tu empresa) o de tu misma cuenta.
--     Las pantallas sin sesión (aceptar invitación, entrar) siguen igual.
--
-- El resto de esta entrega está en la API (no cambia la base):
--   · Ingreso: 10 intentos fallidos bloquean 15 min (antes 5), un límite por
--     IP, y el mismo mensaje y la misma demora exista o no el correo.
--   · Registro con un correo que ya existe: la misma respuesta de siempre; al
--     dueño del correo le llega un aviso «ya tienes cuenta».
--   · Traspasos: respuesta neutra (no dice si el RUC existe) y el archivo se
--     revisa por su contenido, no solo por la extensión.
--   · En producción (SUNAT real) la API no arranca sin captcha.
--   · Ajustar una serie: no deja saltos de más de 99,999.
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================
BEGIN;

-- --- B4: columnas protegidas de usuarios ------------------------------------
-- Solo mira cuando la sentencia la corre directamente la aplicación
-- (current_user = plataforma_app). Las funciones de la plataforma corren como
-- su dueño (plataforma_puertas) y no se ven afectadas.
CREATE OR REPLACE FUNCTION fn_usuarios_campos_protegidos() RETURNS trigger
LANGUAGE plpgsql SET search_path TO 'public' AS $$
BEGIN
    IF current_user <> 'plataforma_app' OR ctx_es_super() THEN
        RETURN NEW;
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.es_super_admin OR NEW.plataforma_permisos IS NOT NULL THEN
            RAISE EXCEPTION 'Solo la plataforma crea usuarios de la plataforma.'
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF NEW.empresa_id IS NOT NULL AND NEW.cuenta_id IS DISTINCT FROM
               (SELECT e.cuenta_id FROM empresas e WHERE e.id = NEW.empresa_id) THEN
            RAISE EXCEPTION 'El usuario no puede ser de otra cuenta que la de su empresa.'
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.empresa_id          IS DISTINCT FROM OLD.empresa_id
    OR (NEW.cuenta_id IS DISTINCT FROM OLD.cuenta_id
        -- (completar una cuenta vacía con la de su empresa sí vale)
        AND NOT (OLD.cuenta_id IS NULL AND NEW.empresa_id IS NOT NULL
                 AND NEW.cuenta_id = (SELECT e.cuenta_id FROM empresas e WHERE e.id = NEW.empresa_id)))
    OR NEW.nivel_cuenta        IS DISTINCT FROM OLD.nivel_cuenta
    OR NEW.es_super_admin      IS DISTINCT FROM OLD.es_super_admin
    OR NEW.plataforma_permisos IS DISTINCT FROM OLD.plataforma_permisos THEN
        RAISE EXCEPTION 'La empresa, la cuenta y el nivel de un usuario solo se cambian desde la plataforma.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;

-- «zz_»: corre después de los otros disparadores BEFORE (que completan cuenta_id).
DROP TRIGGER IF EXISTS zz_usuarios_campos_protegidos ON usuarios;
CREATE TRIGGER zz_usuarios_campos_protegidos
    BEFORE INSERT OR UPDATE ON usuarios
    FOR EACH ROW EXECUTE FUNCTION fn_usuarios_campos_protegidos();


-- --- B5: ¿el usuario es de mi misma cuenta? ---------------------------------
-- Para el equipo de una cuenta (nivel_cuenta no nulo): sus compañeros y la
-- gente de sus empresas. Corre como dueño para poder mirar usuarios que la
-- regla de usuarios no deja ver.
CREATE OR REPLACE FUNCTION ctx_misma_cuenta(p_usuario uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT EXISTS (
        SELECT 1
          FROM usuarios yo
          JOIN usuarios u ON u.id = p_usuario
          LEFT JOIN empresas e ON e.id = u.empresa_id
         WHERE yo.id = ctx_usuario()
           AND yo.nivel_cuenta IS NOT NULL
           AND yo.cuenta_id IS NOT NULL
           AND (u.cuenta_id = yo.cuenta_id OR e.cuenta_id = yo.cuenta_id));
$$;
ALTER FUNCTION ctx_misma_cuenta(uuid) OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION ctx_misma_cuenta(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ctx_misma_cuenta(uuid) TO plataforma_app;


-- --- B5: sesiones ------------------------------------------------------------
-- «ctx_usuario() IS NULL»: solo mientras alguien entra (todavía no se sabe
-- quién es). Ya no basta con no tener empresa elegida.
DROP POLICY IF EXISTS p_sesiones ON sesiones;
CREATE POLICY p_sesiones ON sesiones
    USING (ctx_es_super()
           OR ctx_usuario() IS NULL
           OR usuario_id = ctx_usuario()
           OR (ctx_empresa() IS NOT NULL AND empresa_id = ctx_empresa())
           OR ctx_misma_cuenta(usuario_id))
    WITH CHECK (ctx_es_super()
           OR ctx_usuario() IS NULL
           OR usuario_id = ctx_usuario()
           OR (ctx_empresa() IS NOT NULL AND empresa_id = ctx_empresa())
           OR ctx_misma_cuenta(usuario_id));


-- --- B5: códigos de recuperación: solo los propios ---------------------------
-- (Al aceptar una invitación todavía no hay sesión: ctx_usuario() es nulo.)
DROP POLICY IF EXISTS p_codigos_recuperacion ON codigos_recuperacion;
CREATE POLICY p_codigos_recuperacion ON codigos_recuperacion
    USING (ctx_es_super() OR ctx_usuario() IS NULL OR usuario_id = ctx_usuario())
    WITH CHECK (ctx_es_super() OR ctx_usuario() IS NULL OR usuario_id = ctx_usuario());


-- --- B5: tokens de un uso ------------------------------------------------------
-- Los propios, los de alguien que la regla de usuarios ya te deja ver (tu
-- empresa) o los de tu misma cuenta. Sin sesión (enlaces de los correos), igual
-- que antes: ahí la seguridad la da el hash del token.
DROP POLICY IF EXISTS p_tokens_un_uso ON tokens_un_uso;
CREATE POLICY p_tokens_un_uso ON tokens_un_uso
    USING (ctx_es_super()
           OR ctx_usuario() IS NULL
           OR usuario_id = ctx_usuario()
           OR EXISTS (SELECT 1 FROM usuarios u
                       WHERE u.id = tokens_un_uso.usuario_id
                         AND u.empresa_id = ctx_empresa() AND u.nivel_cuenta IS NULL)
           OR ctx_misma_cuenta(usuario_id))
    WITH CHECK (ctx_es_super()
           OR ctx_usuario() IS NULL
           OR usuario_id = ctx_usuario()
           OR EXISTS (SELECT 1 FROM usuarios u
                       WHERE u.id = tokens_un_uso.usuario_id
                         AND u.empresa_id = ctx_empresa() AND u.nivel_cuenta IS NULL)
           OR ctx_misma_cuenta(usuario_id));

COMMENT ON TABLE tokens_un_uso IS
    '088: con sesión, solo los tokens propios, los de tu empresa o los de tu misma cuenta. Sin sesión (enlaces de restablecer o invitación) la seguridad la da el hash del token.';

INSERT INTO migraciones_aplicadas (nombre) VALUES ('088_refuerzos_seguridad')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;