-- =============================================================================
-- 032 — REPORTES, entrega 1: «Mi negocio» (18 oct 2026)
--
-- Un permiso por reporte, para decidir por rol quién ve qué. Lo que trae cada
-- rol de sistema al empezar (maqueta aprobada):
--
--                         Administrador  Facturador  Consulta
--   Mi día                      sí           sí          sí
--   Tablero de ventas           sí           —           sí
--   Ventas por cliente          sí           —           sí
--   Ventas por producto         sí           —           sí
--   Ventas por vendedor         sí           —           sí
--
-- Solo lectura: ningún reporte escribe nada. Nada se borra. Se puede correr
-- dos veces.
-- =============================================================================

BEGIN;

INSERT INTO permisos (codigo, area, descripcion) VALUES
('reportes.mi_dia',          'reportes', 'Reporte: Mi día'),
('reportes.tablero_ventas',  'reportes', 'Reporte: Tablero de ventas'),
('reportes.ventas_cliente',  'reportes', 'Reporte: Ventas por cliente'),
('reportes.ventas_producto', 'reportes', 'Reporte: Ventas por producto'),
('reportes.ventas_vendedor', 'reportes', 'Reporte: Ventas por vendedor')
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT x.rol::uuid, x.permiso
  FROM (VALUES
        ('11111111-1111-1111-1111-111111111111', 'reportes.mi_dia'),
        ('11111111-1111-1111-1111-111111111111', 'reportes.tablero_ventas'),
        ('11111111-1111-1111-1111-111111111111', 'reportes.ventas_cliente'),
        ('11111111-1111-1111-1111-111111111111', 'reportes.ventas_producto'),
        ('11111111-1111-1111-1111-111111111111', 'reportes.ventas_vendedor'),
        ('22222222-2222-2222-2222-222222222222', 'reportes.mi_dia'),
        ('33333333-3333-3333-3333-333333333333', 'reportes.mi_dia'),
        ('33333333-3333-3333-3333-333333333333', 'reportes.tablero_ventas'),
        ('33333333-3333-3333-3333-333333333333', 'reportes.ventas_cliente'),
        ('33333333-3333-3333-3333-333333333333', 'reportes.ventas_producto'),
        ('33333333-3333-3333-3333-333333333333', 'reportes.ventas_vendedor')
       ) AS x(rol, permiso)
 WHERE EXISTS (SELECT 1 FROM roles r WHERE r.id = x.rol::uuid)
   AND NOT EXISTS (SELECT 1 FROM rol_permiso rp
                    WHERE rp.rol_id = x.rol::uuid AND rp.permiso_codigo = x.permiso);

INSERT INTO migraciones_aplicadas (nombre) VALUES ('032_reportes')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$
DECLARE v_n int;
BEGIN
    SELECT count(*) INTO v_n FROM rol_permiso WHERE permiso_codigo LIKE 'reportes.%';
    RAISE NOTICE '032: 5 permisos de reportes listos (% asignaciones a roles). Nada se borró.', v_n;
END $$;