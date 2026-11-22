-- =============================================================================
-- 093 — PANTALLA DE CAJAS (e278, 22 nov 2026)
--
-- Hasta hoy las cajas del punto de venta se creaban solas («Caja 01», «Caja
-- 02») al abrir. Ahora se pueden crear, nombrar («Caja mostrador», «Caja
-- delivery») y desactivar desde Punto de venta › Cajas.
--
--   · Permiso nuevo pos.cajas_gestionar, para los mismos roles que ya ven las
--     cajas y los arqueos de todos (pos.cajas_ver).
--   · No cambia ninguna tabla: pos_cajas ya tiene nombre y activo. Una caja
--     con una apertura en curso no se desactiva (lo frena la API). Nada se borra.
-- Se puede correr dos veces.
-- =============================================================================

BEGIN;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('pos.cajas_gestionar', 'pos', 'Crear, nombrar y desactivar las cajas del punto de venta')
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT DISTINCT o.rol_id, 'pos.cajas_gestionar'
  FROM rol_permiso o
 WHERE o.permiso_codigo = 'pos.cajas_ver'
   AND NOT EXISTS (SELECT 1 FROM rol_permiso x WHERE x.rol_id = o.rol_id AND x.permiso_codigo = 'pos.cajas_gestionar');

INSERT INTO migraciones_aplicadas (nombre) VALUES ('093_cajas_gestionar')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '093: permiso pos.cajas_gestionar (crear, nombrar y desactivar cajas). Nada se borró.';
END $$;