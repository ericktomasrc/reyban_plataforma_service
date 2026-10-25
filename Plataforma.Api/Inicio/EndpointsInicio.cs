using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Inicio;

/// <summary>
/// EL INICIO DE UNA EMPRESA (lienzo «Cuenta Negocio», pantalla 1).
///
///   GET /api/inicio/resumen
///
/// Cómo va el mes, lo que necesita atención y los últimos comprobantes, en
/// UNA consulta sobre la tabla local `documentos`. No llama al servicio de
/// facturación: el estado de cada comprobante ya lo trae el webhook, y el
/// inicio tiene que abrir rápido aunque el servicio esté lento.
///
/// LA EMPRESA LA PONE LA SESIÓN (ctx_empresa) y el RLS de `documentos` hace
/// el resto: no hay ningún id en la ruta que alguien pueda cambiar.
///
///   vendido      lo del mes en soles con IGV: facturas, boletas y notas de
///                débito, menos notas de crédito. Cuenta lo aceptado y lo que
///                está en camino; no lo rechazado, anulado ni con error.
///   emitidos     los del mes que llegaron al servicio (sin borradores ni
///                errores de envío).
///   enCamino     esperando a SUNAT, de cualquier fecha.
///   ultimoRechazo el más reciente de los últimos 7 días, para el aviso.
///
/// El mes es el de Lima, no el del servidor: a las 11 de la noche del día 31
/// todavía es el mismo mes para quien mira la pantalla.
/// </summary>
public static class EndpointsInicio
{
    public static void MapearInicio(this IEndpointRouteBuilder rutas)
    {
        rutas.MapGet("/api/inicio/resumen", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database.SqlQuery<string>($"""
            WITH mes AS (
                SELECT date_trunc('month', now() AT TIME ZONE 'America/Lima')::date AS desde
            ),
            d AS (
                SELECT * FROM documentos
                 WHERE empresa_id = ctx_empresa() AND activo AND estado <> 'borrador'
            ),
            m AS (
                SELECT d.* FROM d, mes WHERE d.fecha_emision >= mes.desde
            )
            SELECT json_build_object(
                'desde',      (SELECT desde FROM mes),
                'ambiente',   (SELECT e.ambiente FROM empresas e WHERE e.id = ctx_empresa()),
                -- 021: recién recuperada, con lo que falta revisar.
                'recuperadaEn',      (SELECT e.recuperada_en FROM empresas e WHERE e.id = ctx_empresa()),
                'revisionPendiente', COALESCE((SELECT e.revision_pendiente FROM empresas e WHERE e.id = ctx_empresa()), false),
                'vendido',    coalesce((SELECT sum(CASE WHEN tipo = '07' THEN -importe_total ELSE importe_total END)
                                          FROM m WHERE moneda = 'PEN' AND estado IN ('aceptado','observado','encolado')), 0),
                'vendidoUsd', coalesce((SELECT sum(CASE WHEN tipo = '07' THEN -importe_total ELSE importe_total END)
                                          FROM m WHERE moneda = 'USD' AND estado IN ('aceptado','observado','encolado')), 0),
                'emitidos',   (SELECT count(*) FROM m WHERE estado <> 'error'),
                'aceptados',  (SELECT count(*) FROM m WHERE estado IN ('aceptado','observado')),
                'enCamino',   (SELECT count(*) FROM d WHERE estado = 'encolado'),
                'rechazados', (SELECT count(*) FROM m WHERE estado = 'rechazado'),
                'anulados',   (SELECT count(*) FROM m WHERE estado = 'anulado'),   -- e172
                'conError',   (SELECT count(*) FROM d WHERE estado = 'error'),
                'ultimoRechazo', (SELECT json_build_object(
                                     'id', r.id,
                                     'numero', CASE WHEN r.serie IS NOT NULL AND r.correlativo IS NOT NULL
                                                    THEN r.serie || '-' || lpad(r.correlativo::text, 8, '0') END,
                                     'mensaje', r.mensaje_sunat)
                                    FROM d r
                                   WHERE r.estado = 'rechazado'
                                     AND coalesce(r.modificado_en, r.creado_en) > now() - interval '7 days'
                                   ORDER BY coalesce(r.modificado_en, r.creado_en) DESC LIMIT 1),
                'certificadoHasta', (SELECT cf.certificado_valido_hasta FROM credenciales_facturacion cf
                                      WHERE cf.empresa_id = ctx_empresa() AND cf.activo),
                'series',     ARRAY(SELECT DISTINCT serie FROM d
                                     WHERE serie IS NOT NULL AND fecha_emision > current_date - 120 ORDER BY 1),
                'usuarios',   (SELECT count(*) FROM usuarios u WHERE u.empresa_id = ctx_empresa() AND u.activo),
                'ultimos',    (SELECT coalesce(json_agg(x), '[]'::json) FROM (
                                 SELECT d.id, d.tipo, d.serie, d.correlativo, c.razon_social AS cliente,
                                        d.creado_en AS "creadoEn", d.fecha_emision AS "fechaEmision",
                                        d.importe_total AS "importeTotal", d.moneda, d.estado
                                   FROM d JOIN clientes c ON c.id = d.cliente_id
                                  ORDER BY d.creado_en DESC LIMIT 5) x)
            )::text AS "Value"
            """).FirstOrDefaultAsync(ct);

            return Results.Content(json ?? "{}", "application/json");
        })
        .RequierePermiso("facturacion.ver")
        .WithTags("Inicio")
        .WithSummary("El resumen del mes de la empresa abierta, para su inicio");


        // «Ya lo revisé» (021): la empresa recuperada deja de pedir que se
        // revisen sus series y su clave SOL. Lo marca quien la configura.
        rutas.MapPost("/api/inicio/revision-hecha", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            await db.Database.SqlQuery<string>($"SELECT revision_hecha() AS \"Value\"").FirstOrDefaultAsync(ct);
            return Results.Ok(new { mensaje = "Listo. Ya no te lo recordamos." });
        })
        .RequierePermiso("facturacion.credenciales_gestionar")
        .WithTags("Inicio")
        .WithSummary("Marcar como revisada una empresa recién recuperada");
    }
}