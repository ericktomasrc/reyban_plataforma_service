using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Guias;

/// <summary>Cuerpo de POST /api/guias/{id}/whatsapp.</summary>
/// <param name="Para">«cliente» (su número guardado, o el que se escriba) o «conductor» (hay que escribirlo).</param>
public sealed record PeticionWhatsappGuia(string? Para, string? Numero);

/// <summary>
/// e174 (057) — LA GUÍA POR WHATSAPP, igual que las facturas (043): la
/// plataforma prepara el mensaje y el enlace, la persona lo manda desde su
/// WhatsApp.
///
/// EL ENLACE LO DA LA PLATAFORMA, no el portal del servicio (que solo enseña
/// facturas, boletas y notas): /api/publico/guias/&lt;enlace&gt; abre el PDF
/// de ESA guía sin entrar. Se guarda el hash (guia_enlaces), caduca a los 30
/// días y cuenta los usos.
///
/// A QUIÉN: la guía la necesita sobre todo el CONDUCTOR (la lleva en el
/// camino), además del cliente. Por eso se elige.
/// </summary>
public static class EnvioWhatsappGuia
{
    public const int Dias = 30;

    /// <summary>Lo que devuelve resolver_enlace_guia.</summary>
    public sealed class FilaEnlaceGuia
    {
        public Guid EmpresaId { get; set; }
        public Guid GuiaId { get; set; }
    }

    public static void MapearWhatsappGuia(this IEndpointRouteBuilder rutas)
    {
        // =====================================================================
        // EL MENSAJE Y EL ENLACE de una guía ya emitida.
        // =====================================================================
        rutas.MapPost("/api/guias/{id:guid}/whatsapp", async (
            Guid id, PeticionWhatsappGuia? p, ContextoPlataforma db, ContextoPeticion ctx,
            OpcionesCorreo opciones, CancellationToken ct) =>
        {
            var g = await db.Guias.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound(new { error = "No se encontró la guía." });

            if (g.GuiaServicioId is null || g.Correlativo is null ||
                g.Estado is EstadoGuia.Error or EstadoGuia.Rechazado or EstadoGuia.Anulado)
                return Results.Conflict(new
                {
                    error = "Esta guía no se puede mandar.",
                    detalle = g.Estado switch
                    {
                        EstadoGuia.Rechazado => "SUNAT la rechazó: no sirve para el traslado.",
                        EstadoGuia.Anulado   => "Está anulada.",
                        _                    => "Primero tiene que emitirse."
                    }
                });

            var para = (p?.Para ?? "cliente").Trim().ToLowerInvariant();
            if (para is not ("cliente" or "conductor"))
                return Results.BadRequest(new { error = "Elige a quién: «cliente» o «conductor»." });

            Cliente? cliente = g.ClienteId is { } cid
                ? await db.Clientes.FirstOrDefaultAsync(c => c.Id == cid, ct)
                : null;

            string? numero;
            if (!string.IsNullOrWhiteSpace(p?.Numero))
            {
                numero = Whatsapp.Normalizar(p.Numero);
                if (numero is null)
                    return Results.BadRequest(new { error = "Ese número no tiene forma de celular. Escribe 987 654 321 (o con +51…)." });
            }
            else if (para == "cliente")
            {
                numero = cliente?.Whatsapp;
                if (numero is null)
                    return Results.BadRequest(new { error = "El cliente no tiene WhatsApp guardado. Escribe su número." });
            }
            else
                return Results.BadRequest(new { error = "Escribe el celular del conductor." });

            // El enlace: se guarda el hash, nunca el enlace.
            var (token, hash) = GeneradorTokens.Nuevo();
            var expira = DateTime.UtcNow.AddDays(Dias);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO guia_enlaces (empresa_id, guia_id, hash, para, numero, expira_en, creado_por)
                VALUES ({g.EmpresaId}, {g.Id}, {hash}, {para}, {numero}, {expira}, {ctx.UsuarioId}::uuid)
                """, ct);

            // Como en las facturas: el número escrito a mano queda en el cliente.
            if (para == "cliente" && cliente is not null && cliente.Whatsapp != numero && !string.IsNullOrWhiteSpace(p?.Numero))
            {
                cliente.Whatsapp = numero;
                await db.SaveChangesAsync(ct);
            }

            var empresa = await db.Empresas.AsNoTracking()
                .Where(e => e.Id == g.EmpresaId)
                .Select(e => new { e.NombreComercial, e.RazonSocial, e.Nombre })
                .FirstOrDefaultAsync(ct);
            var emisor = empresa?.NombreComercial ?? empresa?.RazonSocial ?? empresa?.Nombre ?? "";

            var enlace = $"{opciones.UrlBase.TrimEnd('/')}/api/publico/guias/{token}";
            var mensaje = Mensaje(g, para, enlace, emisor);
            return Results.Ok(new
            {
                numero,
                numeroLegible = Whatsapp.Legible(numero),
                mensaje,
                enlace,
                expiraEn = expira,
                abrir = $"https://wa.me/{numero}?text={Uri.EscapeDataString(mensaje)}"
            });
        })
        .RequierePermiso(Permisos.Guias.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithTags("Guías de remisión")
        .WithSummary("El mensaje y el enlace para mandar la guía por WhatsApp (al cliente o al conductor)");


        // =====================================================================
        // EL ENLACE: abre el PDF de la guía, sin entrar.
        // =====================================================================
        rutas.MapGet("/api/publico/guias/{token}", async (
            string token, HttpContext http, ContextoPlataforma db, ContextoPeticion ctx,
            ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (token.Length is < 20 or > 100) return NoDisponible("Este enlace no es válido.");

            var hash = GeneradorTokens.Hashear(token);
            var fila = (await db.Database.SqlQuery<FilaEnlaceGuia>(
                    $"SELECT empresa_id, guia_id FROM resolver_enlace_guia({hash})").ToListAsync(ct))
                .FirstOrDefault();
            if (fila is null)
                return NoDisponible("Este enlace caducó o no existe. Pide a quien te lo mandó uno nuevo.");

            await db.FijarContextoAsync(null, fila.EmpresaId, ctx.Ip?.ToString(), "enlace de guía (WhatsApp)", false, ct);

            var g = await db.Guias.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fila.GuiaId, ct);
            if (g?.GuiaServicioId is not { } sid)
                return NoDisponible("La guía no está disponible.");
            if (g.Estado == EstadoGuia.Anulado)
                return NoDisponible($"La guía {g.Numero} fue anulada: ya no sirve para el traslado.");

            var r = await facturacion.DescargarComprobanteAsync(g.EmpresaId, sid, ArchivoComprobante.GuiaPdf, ct);
            if (!r.Exito || r.Contenido is null)
                return NoDisponible("No se pudo traer el PDF de la guía. Inténtalo en unos minutos.");

            var nombre = $"{g.Numero}.pdf";
            http.Response.Headers.ContentDisposition = $"inline; filename=\"{nombre}\"";
            return Results.File(r.Contenido, "application/pdf");
        })
        .WithTags("Guías de remisión")
        .WithSummary("Público: el PDF de una guía desde su enlace de WhatsApp (caduca a los 30 días)");
    }

    /// <summary>Una página mínima: quien abre el enlace no tiene sesión ni la app.</summary>
    private static IResult NoDisponible(string texto) =>
        Results.Content($"""
            <!doctype html><html lang="es"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1"><title>Guía de remisión</title></head>
            <body style="font-family:system-ui,sans-serif;max-width:32rem;margin:15vh auto;padding:0 1rem;color:#222">
            <h1 style="font-size:1.2rem">Guía de remisión</h1><p>{WebUtility.HtmlEncode(texto)}</p></body></html>
            """, "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);

    public static string Mensaje(Guia g, string para, string enlace, string emisor)
    {
        var fecha = g.FechaTraslado.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var firma = string.IsNullOrWhiteSpace(emisor) ? "Gracias." : $"Gracias, {emisor}.";

        if (para == "conductor")
        {
            var nombre = g.ConductorNombre is { Length: > 0 } c ? $", {c}" : "";
            return $"Hola{nombre}:\n" +
                   $"Esta es la guía de remisión {g.Numero} para el traslado del {fecha}.\n" +
                   $"Partida: {g.PartidaDireccion}\n" +
                   $"Llegada: {g.LlegadaDireccion}\n" +
                   $"Llévala en el camino (PDF):\n{enlace}\n" + firma;
        }

        var destinatario = g.DestinatarioNombre is { Length: > 0 } d ? $", {d}" : "";
        return $"Hola{destinatario}:\n" +
               $"Te enviamos la guía de remisión {g.Numero} (traslado del {fecha}).\n" +
               $"Descarga el PDF aquí:\n{enlace}\n" + firma;
    }
}