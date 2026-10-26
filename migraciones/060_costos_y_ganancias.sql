-- =============================================================================
-- 060 — COSTO DEL PRODUCTO Y REPORTES DE GANANCIAS (e179, 25 oct 2026)
--
-- productos.costo: el costo unitario SIN IGV escrito a mano (opcional). Sirve
-- para el margen mientras no haya costo real: el real sale de las compras del
-- inventario (costo promedio ponderado) desde que se activó el inventario.
--
-- Permiso nuevo: reportes.ganancias (Inventario valorizado, Margen bruto y
-- Estado de resultados). Administrador y Jefe de sucursal.
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE productos ADD COLUMN IF NOT EXISTS costo numeric(18,4);
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_productos_costo') THEN
        ALTER TABLE productos ADD CONSTRAINT ck_productos_costo CHECK (costo IS NULL OR costo > 0);
    END IF;
END $$;
COMMENT ON COLUMN productos.costo IS '060: costo unitario sin IGV escrito a mano (para el margen cuando no hay costo real del inventario).';

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('reportes.ganancias', 'facturacion', 'Ver los reportes de ganancias: inventario valorizado, margen bruto y estado de resultados')
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, 'reportes.ganancias'
  FROM roles r
 WHERE (r.id IN ('11111111-1111-1111-1111-111111111111', '55555555-5555-5555-5555-555555555555')
        OR r.basado_en = '55555555-5555-5555-5555-555555555555')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = 'reportes.ganancias');

INSERT INTO migraciones_aplicadas (nombre) VALUES ('060_costos_y_ganancias')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '060: costo del producto y reportes de ganancias. Nada se borró.';
END $$;