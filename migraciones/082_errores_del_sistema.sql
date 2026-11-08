-- =============================================================================
-- 082 — ERRORES DEL SISTEMA (8 nov 2026, e225)
--
-- La pantalla «Errores del sistema» (super admin) lee los logs de cada día y
-- muestra dónde hubo errores, agrupados, con su descarga. Los logs viven en
-- archivos (038), no en la base: aquí solo nace el permiso para darlo a
-- alguien del equipo de la plataforma. El dueño ya lo tiene.
--
--   · permiso plataforma.errores
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================
BEGIN;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('plataforma.errores', 'plataforma', 'Errores del sistema: ver los errores de cada día y descargarlos')
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('082_errores_del_sistema')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '082: Errores del sistema (permiso plataforma.errores). Nada se borró.';
END $$;