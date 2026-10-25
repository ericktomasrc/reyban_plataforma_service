-- =============================================================================
-- 053 — EL INVENTARIO PASA A SER UN MÓDULO QUE SE CONTRATA (e162, 24 oct 2026)
--
-- La idea del producto: se empieza con lo simple —facturar a SUNAT— y se crece.
-- Una empresa que solo factura no ve nada de inventario: ni el menú, ni la
-- casilla «Controla stock», ni el disponible en la factura. El inventario se
-- activa como un plan más, igual que la facturación: lo activa el super
-- administrador en Empresas › Módulos.
--
-- Lo que hace:
--
--   · Agrega el módulo «inventario» al catálogo. El menú (menu_usuario) sale
--     solo: cruza lo contratado con los permisos de área «inventario».
--   · inv_activo() e inv_bloquea() exigen ahora el módulo contratado y vigente.
--     Si se quita el módulo, el stock deja de contarse y de bloquear en el
--     acto; el kardex y los saldos quedan guardados (nada se borra).
--   · Las empresas que YA encendieron el inventario (o ya tienen movimientos)
--     reciben el módulo, para que esta migración no les quite nada.
--
-- Sin el módulo, la empresa factura exactamente como antes de la 052.
-- =============================================================================

BEGIN;

INSERT INTO modulos (codigo, nombre, descripcion, ruta, icono, orden) VALUES
('inventario', 'Inventario',
 'Stock por sucursal, kardex, ingresos por compra, traslados, ajustes y conteos',
 '/inventario', 'caja', 20)
ON CONFLICT (codigo) DO NOTHING;


-- Las que ya lo usaban, lo conservan.
INSERT INTO empresa_modulo (empresa_id, modulo_codigo, contratado_en)
SELECT c.empresa_id, 'inventario', COALESCE(c.activado_en, now())
  FROM inventario_config c
 WHERE (c.activo_inventario OR EXISTS (SELECT 1 FROM inv_movimientos m WHERE m.empresa_id = c.empresa_id))
   AND NOT EXISTS (SELECT 1 FROM empresa_modulo em WHERE em.empresa_id = c.empresa_id AND em.modulo_codigo = 'inventario');


-- ¿Tiene la empresa el módulo contratado y vigente?
CREATE OR REPLACE FUNCTION inv_contratado(p_empresa uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT EXISTS (
        SELECT 1 FROM empresa_modulo em
          JOIN modulos m ON m.codigo = em.modulo_codigo AND m.disponible
         WHERE em.empresa_id = p_empresa AND em.modulo_codigo = 'inventario' AND em.activo
           AND (em.vence_en IS NULL OR em.vence_en > now()));
$$;

-- Encendido = contratado Y encendido por la empresa.
CREATE OR REPLACE FUNCTION inv_activo(p_empresa uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT inv_contratado(p_empresa)
       AND COALESCE((SELECT activo_inventario FROM inventario_config WHERE empresa_id = p_empresa), false);
$$;

-- Se bloquea: contratado, encendido, controlando esa sucursal y sin «permitir
-- vender sin stock».
CREATE OR REPLACE FUNCTION inv_bloquea(p_empresa uuid, p_sucursal uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT inv_contratado(p_empresa) AND COALESCE((
        SELECT c.activo_inventario AND NOT c.permitir_sin_stock
           AND EXISTS (SELECT 1 FROM inv_sucursales s WHERE s.sucursal_id = p_sucursal AND s.controla_desde IS NOT NULL)
          FROM inventario_config c WHERE c.empresa_id = p_empresa), false);
$$;

ALTER FUNCTION inv_contratado(uuid) OWNER TO plataforma_puertas;
ALTER FUNCTION inv_activo(uuid) OWNER TO plataforma_puertas;
ALTER FUNCTION inv_bloquea(uuid, uuid) OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION inv_contratado(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION inv_contratado(uuid) TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('053_inventario_modulo')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$
DECLARE n integer;
BEGIN
    SELECT count(*) INTO n FROM empresa_modulo WHERE modulo_codigo = 'inventario' AND activo;
    RAISE NOTICE '053: inventario ahora es un módulo. % empresa(s) lo tienen (las que ya lo usaban). Las demás facturan como siempre. Nada se borró.', n;
END $$;