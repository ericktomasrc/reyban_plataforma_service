-- =============================================================================
-- 036 — REPORTES, entrega 3: «Mi cartera» y «Plataforma» (18 oct 2026)
--
--   · reporte_cartera(desde, hasta): las empresas de la cartera de quien
--     pregunta (las mismas que ve en «Mis empresas»), con lo emitido en el
--     periodo, rechazos, pendientes y el vencimiento del certificado. Cruza
--     empresas, por eso es una función especial (de plataforma_puertas, como
--     mis_empresas()); quien no tiene cartera recibe una lista vacía.
--   · Los reportes del super administrador no necesitan función: el
--     aislamiento ya le deja ver todo.
--
-- Solo lectura. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION reporte_cartera(p_desde date, p_hasta date)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    WITH yo AS (
        SELECT u.id, u.cuenta_id, u.nivel_cuenta, u.ve_todas
          FROM usuarios u
         WHERE u.id = ctx_usuario() AND u.activo AND u.nivel_cuenta IS NOT NULL
    ), emp AS (
        SELECT e.id, e.nombre, e.ruc, e.razon_social, e.pausada_en,
               CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS amb,
               cf.certificado_valido_hasta
          FROM yo
          JOIN empresas e ON e.cuenta_id = yo.cuenta_id AND e.activo
          LEFT JOIN credenciales_facturacion cf ON cf.empresa_id = e.id AND cf.activo
         WHERE yo.nivel_cuenta = 'dueno' OR yo.ve_todas
            OR EXISTS (SELECT 1 FROM usuario_empresa ue
                        WHERE ue.usuario_id = yo.id AND ue.empresa_id = e.id AND ue.activo)
    ), doc AS (
        SELECT d.empresa_id, d.tipo, d.estado, d.importe_total, d.moneda, d.tipo_cambio, d.creado_en,
               CASE WHEN d.tipo = '07' THEN -1 ELSE 1 END AS signo
          FROM documentos d JOIN emp ON emp.id = d.empresa_id
         WHERE d.activo AND coalesce(d.ambiente, 'beta') = emp.amb
           AND d.fecha_emision BETWEEN p_desde AND p_hasta
    )
    SELECT json_build_object('filas', coalesce(json_agg(json_build_object(
        'id', emp.id, 'nombre', coalesce(emp.razon_social, emp.nombre), 'ruc', emp.ruc,
        'ambiente', emp.amb, 'pausada', emp.pausada_en IS NOT NULL,
        'certificadoHasta', emp.certificado_valido_hasta,
        'comprobantes', (SELECT count(*) FROM doc WHERE doc.empresa_id = emp.id AND doc.tipo IN ('01', '03')
                          AND doc.estado IN ('encolado', 'aceptado', 'observado')),
        'notas', (SELECT count(*) FROM doc WHERE doc.empresa_id = emp.id AND doc.tipo = '07'
                   AND doc.estado IN ('encolado', 'aceptado', 'observado')),
        'total', coalesce((SELECT sum(round(doc.signo * doc.importe_total *
                                CASE WHEN doc.moneda = 'PEN' THEN 1 ELSE coalesce(doc.tipo_cambio, 0) END, 2))
                             FROM doc WHERE doc.empresa_id = emp.id
                              AND doc.estado IN ('encolado', 'aceptado', 'observado')), 0),
        'rechazados', (SELECT count(*) FROM doc WHERE doc.empresa_id = emp.id AND doc.estado = 'rechazado'),
        'pendientes', (SELECT count(*) FROM doc WHERE doc.empresa_id = emp.id AND doc.estado IN ('encolado', 'error')),
        'ultimo', (SELECT max(doc.creado_en) FROM doc WHERE doc.empresa_id = emp.id))
      ORDER BY coalesce(emp.razon_social, emp.nombre)), '[]'::json))::text
      FROM emp;
$$;

ALTER FUNCTION reporte_cartera(date, date) OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION reporte_cartera(date, date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION reporte_cartera(date, date) TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('036_reportes_cartera_plataforma')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '036: reportes de la cartera y de la plataforma listos. Nada se borró.';
END $$;