-- =============================================================================
-- 067 — EQUIPO DE LA PLATAFORMA CON PERMISOS PARCIALES (e190, 25 oct 2026)
--
-- Hasta hoy, quien administraba la plataforma podía todo. Ahora se puede
-- sumar gente al equipo (soporte, ventas, contabilidad) con solo una parte:
--
--   · usuarios.plataforma_permisos   NULL = todo (el dueño de la plataforma);
--     con lista = solo esos permisos «plataforma.*».
--   · Permisos nuevos: cuentas, credenciales (servicio de facturación y
--     conexión con SUNAT), reportes, accesos (correos) y equipo.
--   · Siempre queda al menos un dueño (con todo) activo.
--
-- Lo recorta la API (cada pantalla pide su permiso). En la base siguen siendo
-- super administradores: el aislamiento entre empresas no se toca.
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS plataforma_permisos text[];
COMMENT ON COLUMN usuarios.plataforma_permisos IS '067: del equipo de la plataforma: NULL = todo (dueño); con lista = solo esos permisos plataforma.*.';

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_usuarios_plataforma_permisos') THEN
        ALTER TABLE usuarios ADD CONSTRAINT ck_usuarios_plataforma_permisos
            CHECK (plataforma_permisos IS NULL OR es_super_admin);
    END IF;
END $$;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('plataforma.empresas_gestionar',    'plataforma', 'Dar de alta, suspender y reactivar empresas; traspasos'),
    ('plataforma.modulos_gestionar',     'plataforma', 'Activar y quitar módulos de una empresa'),
    ('plataforma.suplantar',             'plataforma', 'Ver la plataforma como un usuario (soporte, solo lectura)'),
    ('plataforma.cuentas_gestionar',     'plataforma', 'Suspender y reactivar cuentas; su tope de empresas'),
    ('plataforma.credenciales_gestionar','plataforma', 'Conexión de cada empresa con el servicio de facturación y con SUNAT'),
    ('plataforma.reportes_ver',          'plataforma', 'Reportes de la plataforma'),
    ('plataforma.accesos_gestionar',     'plataforma', 'Correos de acceso que no salieron (reenviar)'),
    ('plataforma.consultas_gestionar',   'plataforma', 'Consultas SUNAT: modo y padrón'),
    ('plataforma.equipo_gestionar',      'plataforma', 'Sumar gente al equipo de la plataforma y darle permisos')
ON CONFLICT (codigo) DO NOTHING;

-- Siempre queda al menos un dueño (super administrador con todo) activo.
CREATE OR REPLACE FUNCTION fn_queda_un_dueno() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF OLD.es_super_admin AND OLD.plataforma_permisos IS NULL AND OLD.activo
       AND (NOT NEW.activo OR NEW.plataforma_permisos IS NOT NULL OR NOT NEW.es_super_admin)
       AND NOT EXISTS (SELECT 1 FROM usuarios u
                        WHERE u.id <> OLD.id AND u.es_super_admin AND u.plataforma_permisos IS NULL AND u.activo) THEN
        RAISE EXCEPTION 'Tiene que quedar al menos un dueño de la plataforma con todos los permisos.'
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;
ALTER FUNCTION fn_queda_un_dueno() OWNER TO plataforma_puertas;

CREATE OR REPLACE TRIGGER b_queda_un_dueno BEFORE UPDATE OF activo, plataforma_permisos, es_super_admin ON usuarios
    FOR EACH ROW EXECUTE FUNCTION fn_queda_un_dueno();

INSERT INTO migraciones_aplicadas (nombre) VALUES ('067_equipo_plataforma')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '067: equipo de la plataforma con permisos parciales. Nada se borró.';
END $$;