-- =============================================================================
-- 027 — EL DUEÑO DE LAS PUERTAS: un rol propio con BYPASSRLS, sin login
--
-- POR QUÉ: las funciones SECURITY DEFINER (las «puertas»: registrarse,
-- ingresar, aceptar una invitación, la cartera, los traspasos, los avisos del
-- certificado…) corren con los permisos de su DUEÑO. Hasta hoy su dueño era el
-- superusuario que corre las migraciones (postgres), y se saltaban el RLS
-- porque un superusuario se salta todo.
--
-- Eso tenía dos problemas:
--
--   1. Depender del superusuario ata las puertas a él: si algún día las
--      migraciones las corre otro usuario, dejarían de ver las filas que
--      necesitan y fallarían sin aviso (lo advertían la 010 y la fase 3).
--      (Esta migración sí tiene que correrla un superusuario: crea un rol con
--      BYPASSRLS. En un PostgreSQL gestionado, revisar antes con el proveedor.)
--   2. Un superusuario lo puede TODO: leer archivos del servidor, ejecutar
--      programas (COPY … PROGRAM), cambiar roles. Si una puerta tuviera un
--      fallo, el fallo tendría ese alcance.
--
-- AHORA las posee `plataforma_puertas`:
--
--   - BYPASSRLS: ve las filas de todas las empresas, que es justo lo que una
--     puerta necesita (quien llama todavía no es nadie, o el cambio cruza
--     empresas, como un traspaso).
--   - NOLOGIN: nadie puede conectarse con él. No tiene contraseña que filtrar.
--   - Sin SUPERUSER, sin CREATEROLE, sin CREATEDB: solo leer y escribir tablas.
--   - plataforma_app NO es miembro: no puede «ponerse» este rol.
--
-- Puede borrar (DELETE) porque alguna puerta podría necesitarlo; el rol de la
-- aplicación sigue sin poder. Los candados de la bitácora y los historiales
-- (disparadores que saltan aunque sea el dueño) siguen igual.
--
-- AL AÑADIR UNA PUERTA NUEVA en una migración futura:
--     ALTER FUNCTION la_puerta(...) OWNER TO plataforma_puertas;
-- La comprobación 29 de verificar.sql falla si alguna quedó con otro dueño.
--
-- Se puede correr dos veces: no cambia nada la segunda.
-- =============================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'plataforma_puertas') THEN
        CREATE ROLE plataforma_puertas NOLOGIN BYPASSRLS;
    END IF;
END $$;

-- Por si existía de antes con otros atributos: se dejan los correctos.
ALTER ROLE plataforma_puertas NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS
    PASSWORD NULL;

-- Nadie es miembro de él, y él no es miembro de nadie.
DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT m.roleid::regrole AS grupo, m.member::regrole AS miembro
               FROM pg_auth_members m
              WHERE m.roleid = 'plataforma_puertas'::regrole
                 OR m.member = 'plataforma_puertas'::regrole
    LOOP
        EXECUTE format('REVOKE %s FROM %s', r.grupo, r.miembro);
    END LOOP;
END $$;

GRANT USAGE ON SCHEMA public TO plataforma_puertas;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO plataforma_puertas;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO plataforma_puertas;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO plataforma_puertas;

-- Lo que creen las migraciones futuras (con el mismo usuario que esta).
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO plataforma_puertas;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO plataforma_puertas;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT EXECUTE ON FUNCTIONS TO plataforma_puertas;

-- Las puertas pasan a ser suyas. Los permisos de ejecución que ya tenían
-- (plataforma_app y los demás) se conservan: los traspasa el propio ALTER.
DO $$
DECLARE f record;
BEGIN
    FOR f IN SELECT p.oid::regprocedure AS firma
               FROM pg_proc p
              WHERE p.pronamespace = 'public'::regnamespace
                AND p.prosecdef
                AND p.proowner <> 'plataforma_puertas'::regrole
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f.firma);
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('027_dueno_de_las_puertas')
ON CONFLICT (nombre) DO NOTHING;