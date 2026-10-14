-- =============================================================================
-- 019 — EL CORREO DEL TITULAR EN «MIS EMPRESAS»
--
-- Pedido el 12 de octubre: en la bandeja no se veía el correo del titular de
-- cada empresa. mis_empresas() lo devuelve al final. Cambia lo que devuelve la
-- función, y eso en PostgreSQL no se hace con CREATE OR REPLACE: se borra y se
-- vuelve a crear (es una función, no datos: no se pierde nada).
-- =============================================================================

BEGIN;

DROP FUNCTION mis_empresas();

CREATE OR REPLACE FUNCTION mis_empresas()
RETURNS TABLE (
    id                 uuid,
    nombre             text,
    ruc                text,
    razon_social       text,
    ambiente           text,
    abierta            boolean,
    pausada_en         timestamptz,
    motivo_pausa       text,
    facturacion_activa boolean,
    lista_para_emitir  boolean,
    certificado_hasta  timestamptz,
    rechazos_7_dias    integer,
    ultimo_documento   timestamptz,
    la_ven             text[],
    correo_titular     text
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    WITH yo AS (
        SELECT u.id, u.cuenta_id, u.nivel_cuenta, u.ve_todas, u.empresa_id
          FROM usuarios u
         WHERE u.id = ctx_usuario() AND u.activo AND u.nivel_cuenta IS NOT NULL
    )
    SELECT e.id, e.nombre, e.ruc, e.razon_social, e.ambiente,
           e.id = yo.empresa_id,
           e.pausada_en, e.motivo_pausa,
           EXISTS (SELECT 1 FROM empresa_modulo m
                    WHERE m.empresa_id = e.id AND m.modulo_codigo = 'facturacion' AND m.activo),
           (cf.emisor_estado #>> '{listo,paraEmitir}')::boolean,
           cf.certificado_valido_hasta,
           (SELECT count(*)::int FROM documentos d
             WHERE d.empresa_id = e.id AND d.estado = 'rechazado'
               AND d.creado_en > now() - interval '7 days'),
           (SELECT max(d.creado_en) FROM documentos d
             WHERE d.empresa_id = e.id AND d.estado <> 'borrador'),
           ARRAY(SELECT o.nombre FROM usuarios o
                  WHERE o.cuenta_id = e.cuenta_id AND o.activo
                    AND o.nivel_cuenta IN ('colaborador_admin', 'colaborador')
                    AND (o.ve_todas OR EXISTS (SELECT 1 FROM usuario_empresa ue
                                                WHERE ue.usuario_id = o.id AND ue.empresa_id = e.id
                                                  AND ue.activo))
                  ORDER BY o.nombre),
           e.correo_titular
      FROM yo
      JOIN empresas e ON e.cuenta_id = yo.cuenta_id AND e.activo
      LEFT JOIN credenciales_facturacion cf ON cf.empresa_id = e.id AND cf.activo
     WHERE yo.nivel_cuenta = 'dueno' OR yo.ve_todas
        OR EXISTS (SELECT 1 FROM usuario_empresa ue
                    WHERE ue.usuario_id = yo.id AND ue.empresa_id = e.id AND ue.activo)
     ORDER BY e.pausada_en IS NOT NULL, e.nombre;
$$;

GRANT EXECUTE ON FUNCTION mis_empresas() TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('019_correo_titular_en_cartera');

COMMIT;