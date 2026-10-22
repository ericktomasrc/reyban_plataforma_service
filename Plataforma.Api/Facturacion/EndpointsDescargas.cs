using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// Bajar el XML, el CDR y el PDF de un comprobante.
///
/// LOS ARCHIVOS NO ESTÁN AQUÍ. El original es del servicio de facturación:
/// la plataforma guarda el `comprobante_id` y con él los pide cada vez. Copiarlos
/// sería tener dos originales del mismo documento legal.
///
/// ══════════════════════════════════════════════════════════════════════════
/// QUÉ SE PUEDE BAJAR, Y POR QUÉ SE DECIDE AQUÍ Y NO PREGUNTANDO AL SERVICIO
///
///   sin comprobante_id         nada — nunca se entregó al servicio
///   encolado                   nada — todavía no hay respuesta de SUNAT;
///                              la pantalla se refresca sola y aparecen
///   boleta (03)                solo el PDF — una boleta no tiene XML ni CDR
///                              propios: viaja dentro del resumen diario
///   rechazado                  XML y PDF — el CDR solo existe si SUNAT aceptó
///   aceptado, observado,
///   anulado                    los tres
///
/// LA MISMA TABLA ESTÁ EN EL FRONT, en `ArchivosComprobante.tsx`, para no
/// enseñar un botón que falla. Si se cambia una, se cambia la otra. Aquí es la
/// red de seguridad: si alguien llama a la ruta a mano, recibe el mismo motivo
/// que vería en pantalla, y no se gasta una llamada al servicio para nada.
///
/// Y DETRÁS DE ESTA RED HAY OTRA: si el servicio aun así no tiene el archivo,
/// su 404 trae un `detalle` escrito para el usuario, y se pasa tal cual.
/// ══════════════════════════════════════════════════════════════════════════
///
/// SOLO LECTURA, con `facturacion.ver`. Funciona también suplantando —es un
/// GET— y para el super administrador, porque la empresa sale del documento,
/// que el aislamiento ya dejó ver. Ver `ClienteFacturacion.DescargarComprobanteAsync`.
/// </summary>
public static class EndpointsDescargas
{
    public static void MapearDescargas(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/documentos")
                         .WithTags("Facturación · archivos");

        grupo.MapGet("/{id:guid}/xml", (Guid id, HttpContext http, ContextoPlataforma db,
                ClienteFacturacion facturacion, CancellationToken ct) =>
            DescargarAsync(id, ArchivoComprobante.Xml, http, db, facturacion, ct))
            .RequierePermiso(Permisos.Facturacion.Ver)
            .RequiereModulo(Modulos.Facturacion)
            .WithSummary("Bajar el XML firmado");

        grupo.MapGet("/{id:guid}/cdr", (Guid id, HttpContext http, ContextoPlataforma db,
                ClienteFacturacion facturacion, CancellationToken ct) =>
            DescargarAsync(id, ArchivoComprobante.Cdr, http, db, facturacion, ct))
            .RequierePermiso(Permisos.Facturacion.Ver)
            .RequiereModulo(Modulos.Facturacion)
            .WithSummary("Bajar el CDR de SUNAT (ZIP)");

        // 22 OCT: ?formato=a4 (por defecto) | ticket80 | ticket58.
        grupo.MapGet("/{id:guid}/pdf", (Guid id, string? formato, HttpContext http, ContextoPlataforma db,
                ClienteFacturacion facturacion, CancellationToken ct) =>
            DescargarAsync(id, ArchivoComprobante.Pdf, http, db, facturacion, ct, formato))
            .RequierePermiso(Permisos.Facturacion.Ver)
            .RequiereModulo(Modulos.Facturacion)
            .WithSummary("Bajar la representación impresa (se genera al pedirla)");
    }


    private static async Task<IResult> DescargarAsync(
        Guid id, ArchivoComprobante archivo,
        HttpContext http, ContextoPlataforma db, ClienteFacturacion facturacion,
        CancellationToken ct, string? formato = null)
    {
        var d = await db.Documentos
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.EmpresaId, x.Tipo, x.Estado, x.ComprobanteId, x.Serie, x.Correlativo })
            .FirstOrDefaultAsync(ct);

        if (d is null) return Results.NotFound();

        if (Motivo(d.ComprobanteId, d.Tipo, d.Estado, archivo) is { } motivo)
            return Results.NotFound(new { error = "Archivo no disponible.", detalle = motivo });

        var r = await facturacion.DescargarComprobanteAsync(
            d.EmpresaId, d.ComprobanteId!.Value, archivo, ct,
            formato?.Trim().ToLowerInvariant() is "ticket80" or "ticket58" ? formato.Trim().ToLowerInvariant() : null);

        if (!r.Exito || r.Contenido is null)
        {
            // 404 del servicio → 404 aquí, con su detalle tal cual: es la
            // respuesta a lo que se pidió, no un fallo. Cualquier otra cosa
            // —no llega, la clave no vale— es 502: el que falló es el de
            // enfrente, no quien pulsó el botón.
            return Results.Json(
                new { error = r.Error, detalle = r.Detalle },
                statusCode: r.Estado == StatusCodes.Status404NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status502BadGateway);
        }

        // Documentos tributarios de un cliente: que no se quede una copia en
        // la caché de un proxy ni del navegador.
        http.Response.Headers.CacheControl = "no-store";

        var nombre = r.NombreArchivo ?? NombrePorDefecto(d.Serie, d.Correlativo, archivo);
        var tipo   = r.TipoContenido ?? TipoPorDefecto(archivo);

        // Results.File escribe `attachment; filename=...; filename*=UTF-8''...`
        // con el nombre del servicio: «F001-00000086.xml», no el GUID.
        return Results.File(r.Contenido, tipo, nombre);
    }


    /// <summary>
    /// Por qué NO se puede bajar ese archivo, o null si se puede.
    ///
    /// Escrito para quien lo va a leer en la ventana, igual que los motivos del
    /// servicio.
    /// </summary>
    private static string? Motivo(Guid? comprobanteId, string tipo, string estado, ArchivoComprobante archivo)
    {
        if (comprobanteId is null)
            return "Este comprobante nunca se entregó al servicio de facturación, así que no tiene archivos.";

        // 22 OCT: EL PDF SÍ, aunque vaya en camino: la boleta se imprime en la
        // caja al momento (viaja a SUNAT en el resumen del día siguiente). El
        // PDF dice «Pendiente de envío» hasta que SUNAT conteste.
        if (estado == EstadoDocumento.Encolado && archivo != ArchivoComprobante.Pdf)
            return "Todavía no hay respuesta de SUNAT. El XML y el CDR aparecen en cuanto llegue.";

        if (tipo == "03" && archivo != ArchivoComprobante.Pdf)
            return "Una boleta no tiene XML ni CDR propios: viaja a SUNAT dentro del resumen " +
                   "diario. Su PDF sí se puede bajar.";

        if (estado == EstadoDocumento.Rechazado && archivo == ArchivoComprobante.Cdr)
            return "SUNAT rechazó este comprobante. El CDR solo existe cuando SUNAT lo acepta.";

        if (estado is not (EstadoDocumento.Aceptado or EstadoDocumento.Observado
                        or EstadoDocumento.Anulado or EstadoDocumento.Rechazado or EstadoDocumento.Encolado))
            return $"Un comprobante en estado «{estado}» no tiene archivos.";

        return null;
    }

    private static string NombrePorDefecto(string? serie, int? correlativo, ArchivoComprobante archivo)
    {
        var numero = serie is { Length: > 0 } && correlativo is { } n ? $"{serie}-{n:D8}" : "comprobante";
        return archivo switch
        {
            ArchivoComprobante.Xml => $"{numero}.xml",
            ArchivoComprobante.Cdr => $"{numero}.zip",
            _                      => $"{numero}.pdf"
        };
    }

    private static string TipoPorDefecto(ArchivoComprobante archivo) => archivo switch
    {
        ArchivoComprobante.Xml => "application/xml",
        ArchivoComprobante.Cdr => "application/zip",
        _                      => "application/pdf"
    };
}
