-- =============================================================================
-- 072 — EL ARMADOR IGUAL QUE EL MENÚ (e206, 1 nov 2026)
--
-- Cada submenú del menú de la izquierda tiene su propia fila en el armador,
-- con el mismo nombre, en el mismo menú y en el mismo orden. Antes algunos
-- iban juntos («Guías de remisión» eran 3 pantallas en una fila; Clientes,
-- Productos y Sucursales en «Clientes y productos»; Personal, Auditoría y
-- Configuración juntos; algunos reportes de dos en dos).
--
--   · funciones.grupo: el grupo dentro del menú (Reportes › Mi negocio…).
--   · Las filas que ya existían cambian de nombre, menú u orden, y CONSERVAN
--     su plan. Las nuevas (las que salen de partir una) nacen con el plan de
--     la que venían: nadie gana ni pierde nada con esta migración.
--   · «Transporte guardado» y «Configuración de guías» se cierran en su
--     pantalla; su API sigue libre porque la usa la pantalla de emitir guías.
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE funciones ADD COLUMN IF NOT EXISTS grupo text;
COMMENT ON COLUMN funciones.grupo IS '072: el grupo dentro del menú (por ejemplo Reportes › Ganancias). NULL = sin grupo.';

-- Las nuevas, con el plan de la fila de la que salen (solo la primera vez).
INSERT INTO funciones (codigo, menu, menu_orden, nombre, orden, plan_minimo, fija, oculta, modulo, rutas_web, rutas_api)
SELECT n.codigo, n.menu, n.menu_orden, n.nombre, n.orden,
       (SELECT f.plan_minimo FROM funciones f WHERE f.codigo = n.de), n.fija, false, n.modulo, n.web, n.api
  FROM (VALUES
    ('guias_transporte', 'Guías de remisión', 60, 'Transporte guardado',     2, 'guias',     false, NULL::text,
        ARRAY['/guias/transporte'], ARRAY[]::text[]),
    ('guias_config',     'Guías de remisión', 60, 'Configuración de guías',  3, 'guias',     false, NULL,
        ARRAY['/guias/configuracion'], ARRAY[]::text[]),
    ('inv_config',       'Inventario',        80, 'Configuración',           9, 'inv_stock', false, 'inventario',
        ARRAY['/inventario/configuracion'], ARRAY['/api/inventario/configuracion']),
    ('rep_personas_notas','Reportes',         90, 'Notas y anulaciones por persona', 12, 'rep_anulados', false, NULL,
        ARRAY['/reportes/personas-notas'], ARRAY['/api/reportes/personas-notas']),
    ('rep_resultados',   'Reportes',          90, 'Estado de resultados',   21, 'rep_ganancias', false, NULL,
        ARRAY['/reportes/resultados'], ARRAY['/api/reportes/resultados']),
    ('rep_compras',      'Reportes',          90, 'Compras y gastos',       30, 'compras',   false, NULL,
        ARRAY['/reportes/compras'], ARRAY['/api/reportes/compras']),
    ('per_roles',        'Personal',         100, 'Roles y permisos',        2, 'personal',  true,  NULL,
        ARRAY['/usuarios/roles'], ARRAY['/api/roles']),
    ('per_cargos',       'Personal',         100, 'Cargos',                  3, 'personal',  true,  NULL,
        ARRAY['/usuarios/cargos'], ARRAY['/api/cargos'])
  ) AS n(codigo, menu, menu_orden, nombre, orden, de, fija, modulo, web, api)
ON CONFLICT (codigo) DO NOTHING;

-- Las que ya estaban: nombre, menú, orden y rutas como el menú real. El plan no se toca.
UPDATE funciones f
   SET menu = x.menu, menu_orden = x.menu_orden, grupo = x.grupo, nombre = x.nombre, orden = x.orden,
       rutas_web = x.web, rutas_api = x.api
  FROM (VALUES
    -- Comprobantes
    ('comprobantes',    'Comprobantes', 10, NULL::text, 'Comprobantes emitidos', 1,
        ARRAY['/facturacion/comprobantes'], ARRAY['/api/facturacion/documentos']),
    ('notas_credito',   'Comprobantes', 10, NULL, 'Nueva nota de crédito', 2,
        ARRAY['/facturacion/notas/nueva-credito'], ARRAY[]::text[]),
    ('notas_debito',    'Comprobantes', 10, NULL, 'Nueva nota de débito', 3,
        ARRAY['/facturacion/notas/nueva-debito'], ARRAY[]::text[]),
    -- Clientes · Productos · Compras y gastos · Sucursales (sueltos, como en el menú)
    ('clientes',        'Clientes', 20, NULL, 'Clientes', 1,
        ARRAY['/facturacion/clientes'], ARRAY['/api/facturacion/clientes']),
    ('productos',       'Productos', 30, NULL, 'Productos', 1,
        ARRAY['/facturacion/productos'], ARRAY['/api/facturacion/productos']),
    ('compras',         'Compras y gastos', 40, NULL, 'Compras y gastos', 1,
        ARRAY['/compras'], ARRAY['/api/compras']),
    ('sucursales',      'Sucursales', 50, NULL, 'Sucursales', 1,
        ARRAY['/facturacion/sucursales'], ARRAY['/api/facturacion/sucursales']),
    -- Guías de remisión
    ('guias',           'Guías de remisión', 60, NULL, 'Guías emitidas (y nueva guía)', 1,
        ARRAY['/guias'], ARRAY['/api/guias']),
    -- Punto de venta
    ('pos_vender',      'Punto de venta', 70, NULL, 'Vender', 1, ARRAY['/pos'], ARRAY['/api/pos']),
    ('pos_cajas',       'Punto de venta', 70, NULL, 'Cajas y arqueos', 2, ARRAY['/pos/cajas'], ARRAY['/api/pos/cajas']),
    -- Inventario (en el orden del menú)
    ('inv_stock',       'Inventario', 80, NULL, 'Stock (y kardex)', 1, ARRAY['/inventario'], ARRAY['/api/inventario']),
    ('inv_ingresos',    'Inventario', 80, NULL, 'Ingresos por compra', 2,
        ARRAY['/inventario/ingresos'], ARRAY['/api/inventario/ingresos']),
    ('inv_traslados',   'Inventario', 80, NULL, 'Traslados', 3,
        ARRAY['/inventario/traslados'], ARRAY['/api/inventario/traslados']),
    ('inv_ajustes',     'Inventario', 80, NULL, 'Ajustes y conteos', 4,
        ARRAY['/inventario/ajustes'], ARRAY['/api/inventario/ajustes', '/api/inventario/conteos']),
    ('inv_lotes',       'Inventario', 80, NULL, 'Lotes y vencimientos', 5,
        ARRAY['/inventario/lotes'], ARRAY['/api/inventario/lotes']),
    ('inv_series',      'Inventario', 80, NULL, 'Números de serie', 6,
        ARRAY['/inventario/series'], ARRAY['/api/inventario/series']),
    ('inv_almacenes',   'Inventario', 80, NULL, 'Almacenes', 7,
        ARRAY['/inventario/almacenes'], ARRAY['/api/inventario/almacenes']),
    ('inv_proveedores', 'Inventario', 80, NULL, 'Proveedores y categorías', 8,
        ARRAY['/inventario/proveedores'], ARRAY['/api/inventario/proveedores', '/api/inventario/categorias']),
    ('inv_al_vender',   'Inventario', 80, NULL, 'Stock al vender (interno)', 99,
        ARRAY[]::text[], ARRAY['/api/inventario/disponible', '/api/inventario/lotes/para-vender', '/api/inventario/estado']),
    -- Reportes, con los grupos del menú
    ('rep_mi_dia',      'Reportes', 90, 'Mi negocio', 'Mi día', 1, ARRAY['/reportes/mi-dia'], ARRAY['/api/reportes/mi-dia']),
    ('rep_ventas',      'Reportes', 90, 'Mi negocio', 'Tablero de ventas', 2, ARRAY['/reportes/ventas'], ARRAY['/api/reportes/ventas']),
    ('rep_comparar',    'Reportes', 90, 'Mi negocio', 'Comparar periodos', 3, ARRAY['/reportes/comparar'], ARRAY[]::text[]),
    ('rep_clientes',    'Reportes', 90, 'Mi negocio', 'Ventas por cliente', 4, ARRAY['/reportes/clientes'], ARRAY['/api/reportes/clientes']),
    ('rep_productos',   'Reportes', 90, 'Mi negocio', 'Ventas por producto', 5, ARRAY['/reportes/productos'], ARRAY['/api/reportes/productos']),
    ('rep_vendedores',  'Reportes', 90, 'Mi negocio', 'Ventas por vendedor', 6, ARRAY['/reportes/vendedores'], ARRAY['/api/reportes/vendedores']),
    ('rep_sucursales',  'Reportes', 90, 'Mi negocio', 'Ventas por sucursal', 7, ARRAY['/reportes/sucursales'], ARRAY['/api/reportes/sucursales']),
    ('rep_igv',         'Reportes', 90, 'SUNAT y contabilidad', 'Resumen de IGV', 8, ARRAY['/reportes/igv'], ARRAY['/api/reportes/igv']),
    ('rep_registro',    'Reportes', 90, 'SUNAT y contabilidad', 'Registro de Ventas', 9, ARRAY['/reportes/registro'], ARRAY['/api/reportes/registro']),
    ('rep_problemas',   'Reportes', 90, 'SUNAT y contabilidad', 'Comprobantes con problema', 10, ARRAY['/reportes/problemas'], ARRAY['/api/reportes/problemas']),
    ('rep_anulados',    'Reportes', 90, 'SUNAT y contabilidad', 'Anulados y notas de crédito', 11, ARRAY['/reportes/anulados'], ARRAY['/api/reportes/anulados']),
    ('rep_descarga',    'Reportes', 90, 'SUNAT y contabilidad', 'Descarga masiva', 13, ARRAY['/reportes/descarga'], ARRAY['/api/reportes/descarga']),
    ('rep_ganancias',   'Reportes', 90, 'Ganancias', 'Margen bruto', 20, ARRAY['/reportes/margen'], ARRAY['/api/reportes/margen']),
    ('rep_valorizado',  'Reportes', 90, 'Ganancias', 'Inventario valorizado', 22, ARRAY['/reportes/valorizado'], ARRAY['/api/reportes/valorizado']),
    ('rep_pos',         'Reportes', 90, 'Punto de venta', 'Punto de venta', 40, ARRAY['/reportes/pos'], ARRAY['/api/reportes/pos']),
    -- Personal · Auditoría · Configuración (como en el menú)
    ('personal',        'Personal', 100, NULL, 'Personal de la empresa', 1, ARRAY['/usuarios'], ARRAY['/api/usuarios']),
    ('auditoria',       'Auditoría', 110, NULL, 'Auditoría', 1, ARRAY['/auditoria'], ARRAY['/api/auditoria']),
    ('configuracion',   'Configuración', 120, NULL, 'Configuración de la empresa', 1, ARRAY['/configurar'], ARRAY['/api/configuracion'])
  ) AS x(codigo, menu, menu_orden, grupo, nombre, orden, web, api)
 WHERE f.codigo = x.codigo;

-- Los grupos de las nuevas.
UPDATE funciones SET grupo = 'SUNAT y contabilidad' WHERE codigo = 'rep_personas_notas';
UPDATE funciones SET grupo = 'Ganancias'            WHERE codigo = 'rep_resultados';
UPDATE funciones SET grupo = 'Compras'              WHERE codigo = 'rep_compras';

-- El armador y el catálogo, con el grupo.
CREATE OR REPLACE FUNCTION planes_armador() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN jsonb_build_object(
        'encendidos', planes_encendidos(),
        'planes', (SELECT jsonb_agg(jsonb_build_object('codigo', codigo, 'nombre', nombre, 'orden', orden) ORDER BY orden) FROM planes),
        'funciones', (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                          'codigo', f.codigo, 'menu', f.menu, 'menuOrden', f.menu_orden, 'grupo', f.grupo, 'nombre', f.nombre,
                          'orden', f.orden, 'plan', f.plan_minimo, 'fija', f.fija, 'nueva', f.es_nueva,
                          'rutas', f.rutas_web) ORDER BY f.menu_orden, f.orden), '[]'::jsonb)
                        FROM funciones f WHERE f.activo AND NOT f.oculta),
        'empresasPorPlan', (SELECT COALESCE(jsonb_object_agg(plan, n), '{}'::jsonb)
                              FROM (SELECT plan_efectivo(e.id) AS plan, count(*) n FROM empresas e WHERE e.activo GROUP BY 1) q));
END $$;
ALTER FUNCTION planes_armador() OWNER TO plataforma_puertas;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('072_armador_como_el_menu')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '072: el armador tiene una fila por cada submenú del menú (% filas). Nada cambió de plan.',
        (SELECT count(*) FROM funciones WHERE activo AND NOT oculta);
END $$;