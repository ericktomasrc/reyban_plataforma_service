-- =============================================================================
-- 101 — CÓMO ESTÁ CADA CUENTA HOY (e291, 28 nov 2026)
--
-- Para «Cuentas» del super administrador: de cada cuenta, lo que hace falta
-- para decir en una frase cómo está (todo normal, con candado, configuración a
-- medias, pago vencido…) y, de cada una de sus empresas, lo mismo que ve el
-- estudio en Mi cartera (lista para emitir, certificado, rechazos, último
-- comprobante, qué le falta).
--
--   · admin_cuentas_estado(): un JSON por cuenta. Solo el super administrador
--     (para cualquier otro devuelve vacío). La frase la arma la pantalla.
--
-- Solo lee. Se puede correr dos veces. Nada se borra.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION admin_cuentas_estado()
RETURNS TABLE (cuenta_id uuid, datos jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT c.id,
           jsonb_build_object(
               'pago', c.pago_estado,
               'accesoTotal', coalesce(c.acceso_total, false),
               'encendidos', planes_encendidos(),
               'pruebaHasta', greatest(c.prueba_hasta,
                                       (SELECT max(e.prueba_hasta) FROM empresas e
                                         WHERE e.cuenta_id = c.id AND e.activo)),
               'cartera', CASE WHEN c.tipo = 'profesional' THEN planes_cupos_usados(c.id) END,
               'estudio', CASE WHEN c.tipo = 'profesional' THEN estudio_estado_cuenta(c.id) END,
               'empresas', coalesce((
                   SELECT jsonb_agg(jsonb_build_object(
                              'id', e.id,
                              'esDelEstudio', e.es_del_estudio,
                              'pausadaEn', e.pausada_en,
                              'facturacionActiva', EXISTS (
                                  SELECT 1 FROM empresa_modulo m
                                   WHERE m.empresa_id = e.id AND m.modulo_codigo = 'facturacion' AND m.activo),
                              'listaParaEmitir', (cf.emisor_estado #>> '{listo,paraEmitir}')::boolean,
                              'faltan', cf.emisor_estado #> '{listo,faltan}',
                              'certificadoHasta', cf.certificado_valido_hasta,
                              'rechazos', (SELECT count(*)::int FROM documentos d
                                            WHERE d.empresa_id = e.id AND d.estado = 'rechazado'
                                              AND d.creado_en > now() - interval '7 days'),
                              'ultimoDocumento', (SELECT max(d.creado_en) FROM documentos d
                                                   WHERE d.empresa_id = e.id AND d.estado <> 'borrador'))
                          ORDER BY e.es_del_estudio DESC, e.nombre)
                     FROM empresas e
                     LEFT JOIN credenciales_facturacion cf ON cf.empresa_id = e.id AND cf.activo
                    WHERE e.cuenta_id = c.id AND e.activo), '[]'::jsonb))
      FROM cuentas c
     WHERE c.activo AND ctx_es_super()
$$;
COMMENT ON FUNCTION admin_cuentas_estado() IS '101: cómo está cada cuenta y sus empresas (Cuentas del super administrador). Solo lee.';

DO $$ BEGIN
    ALTER FUNCTION admin_cuentas_estado() OWNER TO plataforma_puertas;
    REVOKE ALL ON FUNCTION admin_cuentas_estado() FROM PUBLIC;
    GRANT EXECUTE ON FUNCTION admin_cuentas_estado() TO plataforma_app;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('101_cuentas_estado')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '101: Cuentas del super admin ya sabe cómo está cada cuenta. Solo lee; nada se borró.';
END $$;