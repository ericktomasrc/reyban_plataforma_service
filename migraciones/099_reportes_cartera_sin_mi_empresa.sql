-- =============================================================================
-- 099 — LOS REPORTES DE LA CARTERA, SIN «MI EMPRESA» (e285, 28 nov 2026)
--
-- «Reportes de la cartera › Mi cartera del mes» y «Certificados por vencer»
-- son de los clientes del estudio. La empresa del propio estudio («Mi
-- empresa») ya no entra en ellos ni en sus totales: sus reportes se ven
-- entrando a Mi empresa, como en cualquier empresa.
--
-- Se puede correr dos veces. Nada se borra.
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
                          AND NOT e.es_del_estudio   -- 099: «Mi empresa» no es de la cartera
          LEFT JOIN credenciales_facturacion cf ON cf.empresa_id = e.id AND cf.activo
         WHERE (yo.nivel_cuenta = 'dueno' OR yo.ve_todas
                OR EXISTS (SELECT 1 FROM usuario_empresa ue
                            WHERE ue.usuario_id = yo.id AND ue.empresa_id = e.id AND ue.activo))
           -- 046: donde solo ve su sucursal o lo suyo, los totales de la
           -- empresa no son para él: esa empresa no sale aquí.
           AND NOT (yo.nivel_cuenta = 'colaborador' AND EXISTS (
                    SELECT 1 FROM usuario_alcance a
                     WHERE a.usuario_id = yo.id AND a.empresa_id = e.id AND a.activo AND a.alcance <> 'empresa'))
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

DO $$ BEGIN
    ALTER FUNCTION reporte_cartera(date, date) OWNER TO plataforma_puertas;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('099_reportes_cartera_sin_mi_empresa')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '099: los reportes de la cartera ya no incluyen «Mi empresa». Nada se borró.';
END $$;