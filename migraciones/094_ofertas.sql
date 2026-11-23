-- =============================================================================
-- 094 — OFERTAS (e279, 22 nov 2026)
--
-- Vender más barato por un tiempo SIN cambiar el precio del producto ni el
-- stock. Cuatro tipos:
--
--   cantidad   «Lleva varios, paga menos»: N unidades del mismo producto por
--              un precio (2 gaseosas por S/ 16; 3x2 = 3 por el precio de 2).
--   pack       Varios productos distintos por un precio (pan + leche +
--              mantequilla por S/ 20).
--   minimo     «Por cantidad»: desde N unidades, cada una a un precio.
--   lote       «Liquidar lote»: un lote que está por vencer, a un precio por
--              unidad.
--
-- El precio de la oferta es CON IGV (lo que paga el cliente). Se aplica sola
-- en el punto de venta: la API reparte el descuento entre los productos según
-- su precio y lo pone como descuento de cada línea del comprobante. Del stock
-- sale cada producto, como siempre (y en «lote», ese lote).
--
--   ofertas            la oferta (nombre, tipo, precio, fechas, sucursales…)
--   oferta_productos   los productos de la oferta (y cuántos de cada uno)
--   oferta_ventas      cada vez que se vendió (para «vendidas» y el límite);
--                      solo inserción.
--
-- Permiso nuevo pos.ofertas (crear y cambiar ofertas), para los roles que ya
-- administran las cajas. Vender con oferta no pide permiso: la pone la API.
-- Solo crea. Nada se borra: una oferta se pausa o se termina.
-- Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS ofertas (
    id                uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id        uuid          NOT NULL REFERENCES empresas (id),
    nombre            text          NOT NULL,
    tipo              text          NOT NULL,
    -- Con IGV. cantidad y pack: el precio de todo el grupo; minimo y lote: por unidad.
    precio            numeric(14,2) NOT NULL,
    -- Solo «lote»: el lote que se liquida (de su único producto).
    lote              text,
    desde             date          NOT NULL,
    hasta             date,
    todas_sucursales  boolean       NOT NULL DEFAULT true,
    sucursales        uuid[]        NOT NULL DEFAULT '{}',
    -- cantidad y pack: cuántos grupos; minimo y lote: cuántas unidades. NULL = sin límite.
    limite            numeric(18,4),
    estado            text          NOT NULL DEFAULT 'activa',
    creado_en         timestamptz   NOT NULL DEFAULT now(),
    creado_por        uuid,
    modificado_en     timestamptz,
    modificado_por    uuid,
    version           integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_ofertas_nombre  CHECK (length(btrim(nombre)) BETWEEN 2 AND 60),
    CONSTRAINT ck_ofertas_tipo    CHECK (tipo IN ('cantidad', 'pack', 'minimo', 'lote')),
    CONSTRAINT ck_ofertas_precio  CHECK (precio > 0),
    CONSTRAINT ck_ofertas_lote    CHECK ((tipo = 'lote') = (lote IS NOT NULL AND length(lote) BETWEEN 1 AND 40)),
    CONSTRAINT ck_ofertas_fechas  CHECK (hasta IS NULL OR hasta >= desde),
    CONSTRAINT ck_ofertas_limite  CHECK (limite IS NULL OR limite > 0),
    CONSTRAINT ck_ofertas_estado  CHECK (estado IN ('activa', 'pausada', 'terminada')),
    CONSTRAINT ck_ofertas_suc     CHECK (todas_sucursales OR cardinality(sucursales) > 0)
);
CREATE INDEX IF NOT EXISTS ix_ofertas_empresa ON ofertas (empresa_id, estado);

COMMENT ON TABLE ofertas IS '094: ofertas del punto de venta (precio con IGV). No se borran: se pausan o terminan.';

CREATE TABLE IF NOT EXISTS oferta_productos (
    oferta_id    uuid          NOT NULL REFERENCES ofertas (id),
    empresa_id   uuid          NOT NULL REFERENCES empresas (id),
    producto_id  uuid          NOT NULL REFERENCES productos (id),
    -- pack: cuántos de este producto lleva el pack; cantidad: N; minimo: desde N; lote: 1.
    cantidad     numeric(18,4) NOT NULL DEFAULT 1,
    orden        smallint      NOT NULL DEFAULT 1,
    PRIMARY KEY (oferta_id, producto_id),
    CONSTRAINT ck_oferta_productos_cantidad CHECK (cantidad > 0)
);

COMMENT ON TABLE oferta_productos IS '094: los productos de una oferta y cuántos de cada uno.';

CREATE TABLE IF NOT EXISTS oferta_ventas (
    id            uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id    uuid          NOT NULL REFERENCES empresas (id),
    oferta_id     uuid          NOT NULL REFERENCES ofertas (id),
    documento_id  uuid          NOT NULL REFERENCES documentos (id),
    -- Grupos (cantidad, pack) o unidades (minimo, lote).
    veces         numeric(18,4) NOT NULL,
    -- Lo que se cobró por esos productos y lo que se dejó de cobrar (con IGV).
    cobrado       numeric(14,2) NOT NULL,
    descuento     numeric(14,2) NOT NULL,
    creado_en     timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_oferta_ventas CHECK (veces > 0 AND cobrado >= 0 AND descuento >= 0)
);
CREATE INDEX IF NOT EXISTS ix_oferta_ventas_oferta    ON oferta_ventas (oferta_id);
CREATE INDEX IF NOT EXISTS ix_oferta_ventas_documento ON oferta_ventas (documento_id);

COMMENT ON TABLE oferta_ventas IS '094: cada venta con oferta (cuántas veces, cobrado y descuento con IGV). Solo inserción.';

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['ofertas', 'oferta_productos', 'oferta_ventas'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS %I ON %I', 'p_' || t, t);
        EXECUTE format(
            'CREATE POLICY %I ON %I USING (ctx_es_super() OR empresa_id = ctx_empresa()) ' ||
            'WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa())', 'p_' || t, t);
    END LOOP;
    -- La bitácora: la oferta con su autor y versión; sus productos, cada cambio;
    -- lo vendido, solo inserción.
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_ofertas') THEN
        PERFORM vigilar_tabla('ofertas');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_oferta_productos') THEN
        CREATE TRIGGER auditar_oferta_productos AFTER INSERT OR UPDATE OR DELETE ON oferta_productos
            FOR EACH ROW EXECUTE FUNCTION fn_auditar();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_oferta_ventas') THEN
        PERFORM vigilar_tabla_inmutable('oferta_ventas');
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON ofertas TO plataforma_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON oferta_productos TO plataforma_app;
GRANT SELECT, INSERT ON oferta_ventas TO plataforma_app;

-- --- Cuántas veces se vendió (solo comprobantes que cuentan) -------------------
CREATE OR REPLACE FUNCTION oferta_vendidas(p_oferta uuid)
RETURNS numeric LANGUAGE sql STABLE AS $$
    SELECT coalesce(sum(v.veces), 0)
      FROM oferta_ventas v JOIN documentos d ON d.id = v.documento_id
     WHERE v.oferta_id = p_oferta AND d.estado IN ('encolado', 'aceptado', 'observado')
$$;
GRANT EXECUTE ON FUNCTION oferta_vendidas(uuid) TO plataforma_app;

-- --- El precio con IGV de un producto (lo que se ve en el punto de venta) ------
CREATE OR REPLACE FUNCTION producto_precio_venta(p productos)
RETURNS numeric LANGUAGE sql IMMUTABLE AS $$
    SELECT round(CASE WHEN p.tipo_afectacion = '10' AND NOT p.incluye_igv THEN p.precio_unitario * 1.18
                      ELSE p.precio_unitario END, 2)
$$;
GRANT EXECUTE ON FUNCTION producto_precio_venta(productos) TO plataforma_app;

-- --- Las ofertas que valen hoy en una sucursal (para vender) -------------------
-- El ORDEN importa: el punto de venta y la API las aplican en este orden
-- (primero el lote, luego los packs, luego «lleva varios», luego «por cantidad»).
CREATE OR REPLACE FUNCTION ofertas_activas(p_sucursal uuid)
RETURNS jsonb LANGUAGE sql STABLE AS $$
    SELECT coalesce(jsonb_agg(x.j ORDER BY x.prioridad, x.creado_en, x.id), '[]'::jsonb)
      FROM (
        SELECT o.id, o.creado_en,
               CASE o.tipo WHEN 'lote' THEN 1 WHEN 'pack' THEN 2 WHEN 'cantidad' THEN 3 ELSE 4 END AS prioridad,
               jsonb_build_object(
                   'id', o.id, 'nombre', o.nombre, 'tipo', o.tipo, 'precio', o.precio, 'lote', o.lote,
                   'restante', CASE WHEN o.limite IS NULL THEN NULL ELSE greatest(0, o.limite - oferta_vendidas(o.id)) END,
                   'productos', (SELECT jsonb_agg(jsonb_build_object('productoId', op.producto_id, 'cantidad', op.cantidad)
                                                  ORDER BY op.orden, op.producto_id)
                                   FROM oferta_productos op WHERE op.oferta_id = o.id)) AS j
          FROM ofertas o
         WHERE o.empresa_id = ctx_empresa() AND o.estado = 'activa'
           AND o.desde <= hoy_peru() AND (o.hasta IS NULL OR o.hasta >= hoy_peru())
           AND (o.todas_sucursales OR p_sucursal = ANY (o.sucursales))
           AND (o.limite IS NULL OR oferta_vendidas(o.id) < o.limite)
      ) x
$$;
COMMENT ON FUNCTION ofertas_activas(uuid) IS '094: las ofertas que valen hoy en esa sucursal, en el orden en que se aplican.';
GRANT EXECUTE ON FUNCTION ofertas_activas(uuid) TO plataforma_app;

-- --- La lista de Punto de venta › Ofertas ---------------------------------------
-- estado de la lista: activa | programada | pausada | terminada (por fecha o
-- límite también cuenta como terminada).
CREATE OR REPLACE FUNCTION ofertas_lista()
RETURNS jsonb LANGUAGE sql STABLE AS $$
    WITH vt AS (
        SELECT v.oferta_id, sum(v.veces) AS veces, sum(v.cobrado) AS cobrado, sum(v.descuento) AS descuento,
               count(DISTINCT v.documento_id) AS ventas,
               sum(v.cobrado) FILTER (WHERE date_trunc('month', d.fecha_emision) = date_trunc('month', hoy_peru())) AS cobrado_mes,
               sum(v.descuento) FILTER (WHERE date_trunc('month', d.fecha_emision) = date_trunc('month', hoy_peru())) AS descuento_mes,
               count(DISTINCT v.documento_id) FILTER (WHERE date_trunc('month', d.fecha_emision) = date_trunc('month', hoy_peru())) AS ventas_mes
          FROM oferta_ventas v JOIN documentos d ON d.id = v.documento_id
         WHERE v.empresa_id = ctx_empresa() AND d.estado IN ('encolado', 'aceptado', 'observado')
         GROUP BY v.oferta_id),
    f AS (
        SELECT o.*, coalesce(vt.veces, 0) AS veces, coalesce(vt.cobrado, 0) AS cobrado, coalesce(vt.descuento, 0) AS dsc,
               coalesce(vt.cobrado_mes, 0) AS cobrado_mes, coalesce(vt.descuento_mes, 0) AS descuento_mes,
               coalesce(vt.ventas_mes, 0) AS ventas_mes,
               CASE WHEN o.estado <> 'activa' THEN o.estado
                    WHEN o.hasta < hoy_peru() OR (o.limite IS NOT NULL AND coalesce(vt.veces, 0) >= o.limite) THEN 'terminada'
                    WHEN o.desde > hoy_peru() THEN 'programada'
                    ELSE 'activa' END AS estado_lista,
               (SELECT jsonb_agg(jsonb_build_object('productoId', p.id, 'codigo', p.codigo, 'descripcion', p.descripcion,
                                                    'cantidad', op.cantidad, 'precio', producto_precio_venta(p))
                                 ORDER BY op.orden, p.descripcion)
                  FROM oferta_productos op JOIN productos p ON p.id = op.producto_id WHERE op.oferta_id = o.id) AS productos,
               (SELECT jsonb_agg(s.codigo || ' ' || s.nombre ORDER BY s.codigo) FROM sucursales s WHERE s.id = ANY (o.sucursales)) AS nombres_suc
          FROM ofertas o LEFT JOIN vt ON vt.oferta_id = o.id
         WHERE o.empresa_id = ctx_empresa())
    SELECT jsonb_build_object(
        'hoy', hoy_peru(),
        'activas', (SELECT count(*) FROM f WHERE estado_lista = 'activa'),
        'terminanSemana', (SELECT count(*) FROM f WHERE estado_lista = 'activa' AND hasta <= hoy_peru() + 7),
        'cobradoMes', (SELECT coalesce(sum(cobrado_mes), 0) FROM f),
        'descuentoMes', (SELECT coalesce(sum(descuento_mes), 0) FROM f),
        'ventasMes', (SELECT coalesce(sum(ventas_mes), 0) FROM f),
        'lotesPorVencer', (
            SELECT count(*) FROM (
                SELECT l.producto_id, l.lote
                  FROM inv_lotes l JOIN productos p ON p.id = l.producto_id AND p.activo
                 WHERE l.empresa_id = ctx_empresa() AND l.fisico > 0 AND l.lote <> ''
                   AND l.vence BETWEEN hoy_peru() AND hoy_peru() + 15
                   AND NOT EXISTS (SELECT 1 FROM f JOIN oferta_productos op ON op.oferta_id = f.id
                                    WHERE f.tipo = 'lote' AND f.estado_lista IN ('activa', 'programada')
                                      AND op.producto_id = l.producto_id AND f.lote = l.lote)
                 GROUP BY 1, 2) q),
        'filas', coalesce((SELECT jsonb_agg(jsonb_build_object(
                    'id', f.id, 'nombre', f.nombre, 'tipo', f.tipo, 'precio', f.precio, 'lote', f.lote,
                    'loteVence', CASE WHEN f.tipo = 'lote' THEN (SELECT min(l.vence) FROM inv_lotes l JOIN oferta_productos op
                                       ON op.oferta_id = f.id AND op.producto_id = l.producto_id WHERE l.lote = f.lote) END,
                    'loteQuedan', CASE WHEN f.tipo = 'lote' THEN (SELECT sum(l.fisico) FROM inv_lotes l JOIN oferta_productos op
                                       ON op.oferta_id = f.id AND op.producto_id = l.producto_id WHERE l.lote = f.lote) END,
                    'desde', f.desde, 'hasta', f.hasta, 'todasSucursales', f.todas_sucursales,
                    'sucursales', to_jsonb(f.sucursales), 'nombresSucursales', coalesce(f.nombres_suc, '[]'::jsonb),
                    'limite', f.limite, 'estado', f.estado, 'estadoLista', f.estado_lista,
                    'vendidas', f.veces, 'cobrado', f.cobrado, 'descuento', f.dsc,
                    'productos', coalesce(f.productos, '[]'::jsonb), 'creadoEn', f.creado_en)
                 ORDER BY CASE f.estado_lista WHEN 'activa' THEN 1 WHEN 'programada' THEN 2 WHEN 'pausada' THEN 3 ELSE 4 END,
                          f.desde DESC, f.creado_en DESC) FROM f), '[]'::jsonb))
$$;
COMMENT ON FUNCTION ofertas_lista() IS '094: Punto de venta › Ofertas: la franja de arriba y todas las ofertas con lo vendido.';
GRANT EXECUTE ON FUNCTION ofertas_lista() TO plataforma_app;

-- --- Permiso ---------------------------------------------------------------
INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('pos.ofertas', 'pos', 'Crear, cambiar, pausar y terminar las ofertas del punto de venta')
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT DISTINCT o.rol_id, 'pos.ofertas'
  FROM rol_permiso o
 WHERE o.permiso_codigo = 'pos.cajas_gestionar'
   AND NOT EXISTS (SELECT 1 FROM rol_permiso x WHERE x.rol_id = o.rol_id AND x.permiso_codigo = 'pos.ofertas');

-- --- El menú en el armador de planes (va con el punto de venta) ---------------
INSERT INTO funciones (codigo, menu, menu_orden, nombre, orden, plan_minimo, fija, oculta, es_nueva, modulo, rutas_web, rutas_api)
SELECT 'pos_ofertas', 'Punto de venta', 60, 'Ofertas', 3, f.plan_minimo, false, false, true, 'pos',
       ARRAY['/pos/ofertas'], ARRAY['/api/pos/ofertas']
  FROM funciones f WHERE f.codigo = 'pos_vender'
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('094_ofertas')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '094: ofertas del punto de venta (lleva varios, pack, por cantidad, liquidar lote). Nada se borró.';
END $$;