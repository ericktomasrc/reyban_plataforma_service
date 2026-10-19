-- =============================================================================
-- 034 — REPORTES, entrega 2: «SUNAT y contabilidad» (18 oct 2026)
--
-- Un permiso por reporte. Lo que trae cada rol de sistema al empezar:
--
--                                 Administrador  Facturador  Consulta
--   Resumen de IGV                      sí           —           —
--   Registro de Ventas                  sí           —           —
--   Comprobantes con problema           sí           sí          —
--   Anulados y notas de crédito         sí           —           —
--   Descarga masiva XML/CDR/PDF         sí           —           —
--
-- Solo lectura. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

INSERT INTO permisos (codigo, area, descripcion) VALUES
('reportes.resumen_igv',     'reportes', 'Reporte: Resumen de IGV'),
('reportes.registro_ventas', 'reportes', 'Reporte: Registro de Ventas (Excel)'),
('reportes.con_problema',    'reportes', 'Reporte: Comprobantes con problema'),
('reportes.anulados_notas',  'reportes', 'Reporte: Anulados y notas de crédito'),
('reportes.descarga_masiva', 'reportes', 'Reporte: Descarga masiva XML/CDR/PDF')
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT x.rol::uuid, x.permiso
  FROM (VALUES
        ('11111111-1111-1111-1111-111111111111', 'reportes.resumen_igv'),
        ('11111111-1111-1111-1111-111111111111', 'reportes.registro_ventas'),
        ('11111111-1111-1111-1111-111111111111', 'reportes.con_problema'),
        ('11111111-1111-1111-1111-111111111111', 'reportes.anulados_notas'),
        ('11111111-1111-1111-1111-111111111111', 'reportes.descarga_masiva'),
        ('22222222-2222-2222-2222-222222222222', 'reportes.con_problema')
       ) AS x(rol, permiso)
 WHERE EXISTS (SELECT 1 FROM roles r WHERE r.id = x.rol::uuid)
   AND NOT EXISTS (SELECT 1 FROM rol_permiso rp
                    WHERE rp.rol_id = x.rol::uuid AND rp.permiso_codigo = x.permiso);

INSERT INTO migraciones_aplicadas (nombre) VALUES ('034_reportes_sunat')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$
DECLARE v_n int;
BEGIN
    SELECT count(*) INTO v_n FROM permisos WHERE area = 'reportes';
    RAISE NOTICE '034: % permisos de reportes en total. Nada se borró.', v_n;
END $$;