using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Facturacion;

/// <summary>043: un número de WhatsApp, solo dígitos con el código de país.</summary>
public static class Whatsapp
{
    /// <summary>
    /// «987 654 321», «+51 987-654-321», «0051987654321» → «51987654321».
    /// Nueve dígitos que empiezan por 9 son un celular de Perú: se les pone
    /// el 51. Null si no tiene forma de número (de 8 a 15 dígitos, E.164).
    /// </summary>
    public static string? Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        if (texto.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '-' or '+' or '(' or ')' or '.'))) return null;

        var d = new string(texto.Where(char.IsAsciiDigit).ToArray());
        if (d.StartsWith("00")) d = d[2..];
        if (d.Length == 9 && d[0] == '9') d = "51" + d;

        return d.Length is >= 8 and <= 15 && d[0] != '0' ? d : null;
    }

    /// <summary>«51987654321» → «+51 987 654 321» (para leerlo, no para guardarlo).</summary>
    public static string Legible(string numero) =>
        numero.Length == 11 && numero.StartsWith("51")
            ? $"+51 {numero[2..5]} {numero[5..8]} {numero[8..]}"
            : "+" + numero;
}

public sealed record PeticionWhatsapp(string? Numero);
public sealed record PeticionResultadoWhatsapp(string? Resultado);

/// <summary>
/// 043 — MANDAR EL COMPROBANTE POR WHATSAPP (semiautomático, maqueta del 22 oct).
///
///   · El cliente tiene su WhatsApp y la casilla «enviarle sus comprobantes
///     por WhatsApp» (la regla general).
///   · Cada comprobante lo propone al hacerlo y se puede cambiar solo para ese.
///   · Al emitir, si va por WhatsApp, queda PENDIENTE: la pantalla pide aquí
///     el mensaje (con un enlace al portal del receptor que abre solo ese
///     comprobante) y abre WhatsApp con el número y el texto listos. La
///     persona pulsa Enviar en WhatsApp y vuelve a decir cómo salió.
///
/// NO SE PUEDE SABER DESDE AQUÍ si el número tiene WhatsApp ni si se envió:
/// lo dice la persona («¿Se envió?»). Si dice que ese número no tiene
/// WhatsApp, se desactiva el envío en el cliente. Con la API de WhatsApp
/// (Meta) esto será automático; las pantallas y estas columnas no cambian.
///
/// WhatsApp no deja adjuntar archivos desde un enlace: va el enlace al
/// portal, donde el cliente baja el PDF (y el XML y el CDR si es factura).
/// </summary>
public static class EnvioWhatsapp
{
    public const string Pendiente = "pendiente";
    public const string Enviado = "enviado";
    public const string SinWhatsapp = "sin_whatsapp";
    public const string NoEnviado = "no_enviado";

    /// <summary>¿Este comprobante va por WhatsApp? Lo pedido, o lo que diga el cliente.</summary>
    public static async Task<bool> QuiereAsync(bool? pedido, Guid clienteId, ContextoPlataforma db, CancellationToken ct)
    {
        if (pedido is { } p) return p;
        return await db.Clientes.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == clienteId).Select(c => c.WhatsappActivo).FirstOrDefaultAsync(ct);
    }

    /// <summary>Al emitir: si va por WhatsApp y todavía no salió, queda pendiente. Devuelve si quedó pendiente.</summary>
    public static async Task<bool> MarcarPendienteAsync(Documento d, ContextoPlataforma db, CancellationToken ct)
    {
        if (!d.WhatsappEnviar || d.WhatsappEstado is Enviado) return false;
        if (d.WhatsappEstado != Pendiente)
        {
            d.WhatsappEstado = Pendiente;
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public static void MapearWhatsapp(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/documentos").WithTags("Facturación · WhatsApp");

        // =====================================================================
        // EL MENSAJE Y EL ENLACE DE WHATSAPP de un comprobante ya emitido.
        // Con `numero`: a ese número, y se guarda en el cliente para la próxima.
        // =====================================================================
        grupo.MapPost("/{id:guid}/whatsapp", async (
            Guid id, PeticionWhatsapp? p, ContextoPlataforma db, ClienteFacturacion facturacion,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var d = await db.Documentos.Include(x => x.Cliente).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();

            if (d.ComprobanteId is null || d.Serie is null || d.Correlativo is null ||
                d.Estado is EstadoDocumento.Borrador or EstadoDocumento.Error or EstadoDocumento.Rechazado)
                return Results.Conflict(new
                {
                    error = "Este comprobante no se puede mandar todavía.",
                    detalle = d.Estado == EstadoDocumento.Rechazado
                        ? "SUNAT lo rechazó: no es un comprobante."
                        : "Primero tiene que emitirse."
                });

            string? numero;
            if (!string.IsNullOrWhiteSpace(p?.Numero))
            {
                numero = Whatsapp.Normalizar(p.Numero);
                if (numero is null)
                    return Results.BadRequest(new { error = "Ese número no tiene forma de celular. Escribe 987 654 321 (o con +51…)." });
            }
            else
            {
                numero = d.WhatsappNumero ?? d.Cliente?.Whatsapp;
                if (numero is null)
                    return Results.BadRequest(new { error = "El cliente no tiene WhatsApp. Escribe su número." });
            }

            // EL ENLACE: lo da el servicio (abre solo este comprobante y caduca).
            var r = await facturacion.EnviarAsync<JsonElement>(
                $"v1/comprobantes/{d.ComprobanteId}/enlace-portal", new { dias = 30 }, null, ct);

            if (!r.Exito || r.Datos.ValueKind != JsonValueKind.Object ||
                !r.Datos.TryGetProperty("url", out var u) || u.GetString() is not { Length: > 0 } enlace)
            {
                logs.CreateLogger("Plataforma.Whatsapp")
                    .LogWarning("No se pudo pedir el enlace del portal para {Id}: {Error} {Detalle}", d.Id, r.Error, r.Detalle);
                return Results.Json(new
                {
                    error = r.Error ?? "No se pudo preparar el enlace del comprobante.",
                    detalle = r.Detalle
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            // EL NÚMERO QUE SE ESCRIBIÓ A MANO QUEDA EN EL CLIENTE (la casilla
            // general no se toca: eso lo decide quien edita el cliente).
            if (d.Cliente is { } cliente && cliente.Whatsapp != numero && !string.IsNullOrWhiteSpace(p?.Numero))
                cliente.Whatsapp = numero;

            d.WhatsappNumero = numero;
            d.WhatsappEnviar = true;
            if (d.WhatsappEstado != Enviado) d.WhatsappEstado = Pendiente;
            await db.SaveChangesAsync(ct);

            var empresa = await db.Empresas.AsNoTracking()
                .Where(e => e.Id == d.EmpresaId)
                .Select(e => new { e.NombreComercial, e.RazonSocial, e.Nombre })
                .FirstOrDefaultAsync(ct);
            var emisor = empresa?.NombreComercial ?? empresa?.RazonSocial ?? empresa?.Nombre ?? "";

            var mensaje = Mensaje(d, enlace, emisor);
            return Results.Ok(new
            {
                numero,
                numeroLegible = Whatsapp.Legible(numero),
                mensaje,
                enlace,
                abrir = $"https://wa.me/{numero}?text={Uri.EscapeDataString(mensaje)}"
            });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("El mensaje y el enlace para mandar este comprobante por WhatsApp");


        // =====================================================================
        // CÓMO SALIÓ: lo dice la persona al volver de WhatsApp.
        // =====================================================================
        grupo.MapPost("/{id:guid}/whatsapp/resultado", async (
            Guid id, PeticionResultadoWhatsapp p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var resultado = (p.Resultado ?? "").Trim();
            if (resultado is not (Enviado or SinWhatsapp or NoEnviado))
                return Results.BadRequest(new { error = "El resultado es «enviado», «sin_whatsapp» o «no_enviado»." });

            var d = await db.Documentos.Include(x => x.Cliente).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();

            d.WhatsappEstado = resultado;
            if (resultado == Enviado) d.WhatsappEn = DateTime.UtcNow;

            var mensaje = resultado switch
            {
                Enviado => "Anotado: se envió por WhatsApp.",
                NoEnviado => "Anotado: no se envió por WhatsApp. Puedes mandarlo cuando quieras desde aquí.",
                _ => "Anotado."
            };

            // ESE NÚMERO NO TIENE WHATSAPP: se apaga el envío en el cliente
            // (el número se queda, por si fue un dedazo y hay que corregirlo).
            if (resultado == SinWhatsapp && d.Cliente is { } c && c.Whatsapp == d.WhatsappNumero && c.WhatsappActivo)
            {
                c.WhatsappActivo = false;
                mensaje = $"Anotado. Se desactivó el envío por WhatsApp para {c.RazonSocial}: " +
                          "si el número estaba mal, corrígelo en Clientes y vuelve a activarlo.";
            }
            else if (resultado == SinWhatsapp)
                mensaje = "Anotado: ese número no tiene WhatsApp.";

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { estado = d.WhatsappEstado, mensaje });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Anotar si el comprobante salió por WhatsApp");
    }

    /// <summary>El texto del mensaje. Corto: es lo primero que el cliente ve en su chat.</summary>
    public static string Mensaje(Documento d, string enlace, string emisor)
    {
        var tipo = d.Tipo switch
        {
            "01" => "Factura", "03" => "Boleta", "07" => "Nota de crédito", "08" => "Nota de débito", _ => "Comprobante"
        };
        var moneda = d.Moneda == "PEN" ? "S/" : d.Moneda == "USD" ? "US$" : d.Moneda;
        var archivos = d.Tipo == "03" || d.Serie?.StartsWith('B') == true ? "el PDF" : "el PDF, el XML y el CDR";
        var nombre = d.Cliente?.RazonSocial is { Length: > 0 } r ? $", {r}" : "";
        var total = d.ImporteTotal.ToString("#,##0.00", CultureInfo.InvariantCulture);

        return $"Hola{nombre}:\n" +
               $"Te enviamos tu {tipo} {d.Numero} por {moneda} {total}.\n" +
               $"Descarga {archivos} aquí:\n{enlace}\n" +
               (string.IsNullOrWhiteSpace(emisor) ? "Gracias." : $"Gracias, {emisor}.");
    }
}