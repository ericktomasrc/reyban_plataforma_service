using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plataforma.Api.Registro;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Reclamos;

public sealed record PeticionReclamo(
    string? Tipo, string? Bien, string? Nombre, string? DocTipo, string? DocNumero, string? Domicilio,
    string? Telefono, string? Correo, string? Apoderado, string? Monto, string? Descripcion, string? Detalle,
    string? Pedido, string? Captcha);

public sealed record PeticionRespuestaReclamo(string? Respuesta);

/// <summary>
/// 078 — EL LIBRO DE RECLAMACIONES VIRTUAL (lo pide Indecopi y lo pide Culqi).
///
///   POST /api/publico/reclamos                SIN SESIÓN, con captcha. Guarda la
///                                             hoja, le manda la copia al cliente
///                                             y el aviso a Reyban.
///   GET  /api/admin/reclamos                  la lista (permiso plataforma.reclamos).
///   POST /api/admin/reclamos/{id}/responder   guarda la respuesta y se la manda
///                                             por correo al cliente.
///
/// Los errores de la base vienen como «RECLAMO_CAMPO: campo|mensaje»: se
/// devuelven con el campo, para marcarlo en el formulario.
/// </summary>
public static class EndpointsReclamos
{
    private const string Punto = "reclamo";

    public static void MapearReclamos(this IEndpointRouteBuilder rutas)
    {
        rutas.MapPost("/api/publico/reclamos", async (
            PeticionReclamo p, HttpContext http, ContextoPlataforma db, Captcha captcha,
            IServicioCorreo correo, ILoggerFactory logs, CancellationToken ct) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString();
            if (captcha.Configurado && !await captcha.VerificarAsync(p.Captcha, ip, ct))
                return Results.BadRequest(new { error = "No pudimos comprobar que no eres un robot. Vuelve a intentarlo.", campo = "captcha" });

            var datos = JsonSerializer.Serialize(new
            {
                tipo = p.Tipo, bien = p.Bien, nombre = p.Nombre, docTipo = p.DocTipo, docNumero = p.DocNumero,
                domicilio = p.Domicilio, telefono = p.Telefono, correo = p.Correo, apoderado = p.Apoderado,
                monto = p.Monto?.Replace(",", "."), descripcion = p.Descripcion, detalle = p.Detalle, pedido = p.Pedido
            });

            var (r, error) = await IntentarAsync(db, () => db.Database.SqlQuery<string>(
                $"SELECT reclamo_registrar({datos}::jsonb, {ip}::inet)::text AS \"Value\"").FirstAsync(ct), ct);
            if (error is not null) return error;

            var hoja = JsonDocument.Parse(r!).RootElement;
            var numero = hoja.GetProperty("hoja").GetString()!;
            var vence = DateOnly.Parse(hoja.GetProperty("vence").GetString()!);
            var prov = hoja.GetProperty("proveedor");
            var log = logs.CreateLogger("Reclamos");

            // La copia al cliente y el aviso a Reyban. Si un correo no sale, la
            // hoja ya está guardada: se dice en el log y la pantalla muestra el número.
            try
            {
                var copia = Copia(numero, vence, p, prov);
                await correo.EnviarAsync(p.Correo!.Trim(), copia.Asunto, copia.Html, copia.Texto, ct);
                if (Texto(prov, "correo") is { Length: > 3 } nuestro)
                    await correo.EnviarAsync(nuestro, $"Nueva hoja de reclamación {numero}", copia.Html, copia.Texto, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "La hoja {Hoja} se guardó, pero no salió algún correo.", numero);
            }

            return Results.Ok(new
            {
                hoja = numero,
                vence = vence.ToString("yyyy-MM-dd"),
                mensaje = $"Registramos tu {(p.Tipo == "queja" ? "queja" : "reclamo")} con la hoja N.° {numero}. " +
                          $"Te enviamos una copia a {p.Correo!.Trim().ToLowerInvariant()}. Te responderemos a más tardar el {vence:dd/MM/yyyy}."
            });
        })
        .WithTags("Página pública")
        .WithSummary("Registrar una hoja del Libro de Reclamaciones (sin sesión)");

        var g = rutas.MapGroup("/api/admin/reclamos").WithTags("Libro de Reclamaciones");

        g.MapGet("/", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"SELECT reclamos_lista()::text AS \"Value\"").FirstAsync(ct),
                            "application/json"))
         .SoloSuperAdmin(Permisos.Plataforma.Reclamos)
         .WithSummary("Las hojas del Libro de Reclamaciones");

        // e233: cuántas faltan responder (el número rojo del menú) y cuántas vencen en 3 días o menos.
        g.MapGet("/pendientes", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"""
                SELECT jsonb_build_object(
                         'pendientes', count(*),
                         'porVencer', count(*) FILTER (WHERE (x->>'diasQuedan')::int <= 3))::text AS "Value"
                  FROM jsonb_array_elements(reclamos_lista()) x
                 WHERE x->>'estado' = 'pendiente'
                """).FirstAsync(ct), "application/json"))
         .SoloSuperAdmin(Permisos.Plataforma.Reclamos)
         .WithSummary("Hojas sin responder (para el menú)");

        g.MapPost("/{id:guid}/responder", async (
            Guid id, PeticionRespuestaReclamo p, ContextoPlataforma db, IServicioCorreo correo,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var (r, error) = await IntentarAsync(db, () => db.Database.SqlQuery<string>(
                $"SELECT reclamo_responder({id}, {p.Respuesta ?? ""})::text AS \"Value\"").FirstAsync(ct), ct);
            if (error is not null) return error;

            var d = JsonDocument.Parse(r!).RootElement;
            var hoja = d.GetProperty("hoja").GetString()!;
            var destino = d.GetProperty("correo").GetString()!;
            var enviado = false;
            try
            {
                var m = Respuesta(hoja, d.GetProperty("nombre").GetString() ?? "", p.Respuesta!.Trim(), d.GetProperty("proveedor"));
                enviado = await correo.EnviarAsync(destino, m.Asunto, m.Html, m.Texto, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logs.CreateLogger("Reclamos").LogWarning(e, "La respuesta de la hoja {Hoja} se guardó, pero el correo no salió.", hoja);
            }
            return Results.Ok(new
            {
                mensaje = enviado
                    ? $"Listo: la respuesta quedó guardada y se envió a {destino}."
                    : $"La respuesta quedó guardada, pero el correo a {destino} no salió. Revisa «Correos» y reenvíala desde tu correo."
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.Reclamos)
        .WithSummary("Responder una hoja (se envía por correo al cliente)");
    }

    // =========================================================================

    /// <summary>
    /// Corre la consulta en un punto de guardado: si la base dice que falta un
    /// dato, se vuelve atrás (la transacción de la petición sigue sana) y se
    /// contesta con el campo y el mensaje.
    /// </summary>
    private static async Task<(string? Valor, IResult? Error)> IntentarAsync(
        ContextoPlataforma db, Func<Task<string>> consulta, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction;
        if (tx is not null) await tx.CreateSavepointAsync(Punto, ct);
        try
        {
            return (await consulta(), null);
        }
        catch (PostgresException pg) when (pg.MessageText.StartsWith("RECLAMO_", StringComparison.Ordinal))
        {
            if (tx is not null) await tx.RollbackToSavepointAsync(Punto, ct);
            var texto = pg.MessageText[(pg.MessageText.IndexOf(':') + 1)..].Trim();
            if (pg.MessageText.StartsWith("RECLAMO_CAMPO", StringComparison.Ordinal))
            {
                var partes = texto.Split('|', 2);
                return (null, Results.BadRequest(new { error = partes.Length > 1 ? partes[1] : texto, campo = partes[0] }));
            }
            var estado = pg.MessageText.StartsWith("RECLAMO_TOPE", StringComparison.Ordinal) ? 429
                       : pg.MessageText.StartsWith("RECLAMO_NO_PUEDE", StringComparison.Ordinal) && pg.SqlState == "42501" ? 403 : 400;
            return (null, Results.Json(new { error = texto }, statusCode: estado));
        }
    }

    private static string? Texto(JsonElement e, string nombre) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Proveedor(JsonElement prov) =>
        $"{H(Texto(prov, "razonSocial") ?? "Reyban")}{(Texto(prov, "ruc") is { } ruc ? $" · RUC {H(ruc)}" : "")}" +
        $"{(Texto(prov, "direccion") is { } dir ? $" · {H(dir)}" : "")}";

    private static CorreoCompuesto Copia(string hoja, DateOnly vence, PeticionReclamo p, JsonElement prov)
    {
        var tipo = p.Tipo == "queja" ? "Queja" : "Reclamo";
        string Fila(string k, string? v) => string.IsNullOrWhiteSpace(v) ? "" :
            $"<tr><td style=\"padding:6px 10px;color:#5b6b77;white-space:nowrap;vertical-align:top\">{k}</td><td style=\"padding:6px 10px\">{H(v)}</td></tr>";
        var html = $"""
            <!doctype html><html lang="es"><body style="margin:0;padding:24px;background:#f3f5f7;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;color:#16202a;line-height:1.5">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;margin:0 auto;background:#fff;border:1px solid #d3dbe1;border-radius:6px">
            <tr><td style="padding:28px">
            <p style="margin:0 0 4px;font-size:13px;color:#0f6f6c;font-weight:600">LIBRO DE RECLAMACIONES</p>
            <h1 style="margin:0 0 6px;font-size:20px">Hoja de reclamación N.° {hoja}</h1>
            <p style="margin:0 0 16px;color:#5b6b77;font-size:14px">{Proveedor(prov)}</p>
            <table style="width:100%;border-collapse:collapse;font-size:14px;border:1px solid #e3e8ec">
            {Fila("Tipo", tipo)}{Fila("Nombre", p.Nombre?.ToUpperInvariant())}{Fila("Documento", $"{p.DocTipo} {p.DocNumero}")}
            {Fila("Domicilio", p.Domicilio)}{Fila("Teléfono", p.Telefono)}{Fila("Correo", p.Correo)}{Fila("Apoderado", p.Apoderado)}
            {Fila(p.Bien == "producto" ? "Producto" : "Servicio", p.Descripcion)}{Fila("Monto reclamado", string.IsNullOrWhiteSpace(p.Monto) ? null : "S/ " + p.Monto)}
            {Fila("Detalle", p.Detalle)}{Fila("Pedido", p.Pedido)}
            </table>
            <p style="margin:16px 0 0">Te responderemos a este correo a más tardar el <strong>{vence:dd/MM/yyyy}</strong> (15 días hábiles).</p>
            <p style="margin:12px 0 0;color:#7d8d99;font-size:12.5px">La formulación del reclamo no impide acudir a otras vías de solución de controversias ni es requisito previo para interponer una denuncia ante el INDECOPI.</p>
            </td></tr></table></body></html>
            """;
        var texto = $"Libro de Reclamaciones — Hoja N.° {hoja}\n{Texto(prov, "razonSocial")} {Texto(prov, "ruc")}\n\n" +
                    $"Tipo: {tipo}\nNombre: {p.Nombre}\nDocumento: {p.DocTipo} {p.DocNumero}\nDetalle: {p.Detalle}\nPedido: {p.Pedido}\n\n" +
                    $"Te responderemos a más tardar el {vence:dd/MM/yyyy}.";
        return new CorreoCompuesto($"Tu hoja de reclamación N.° {hoja}", html, texto);
    }

    private static CorreoCompuesto Respuesta(string hoja, string nombre, string respuesta, JsonElement prov)
    {
        var html = $"""
            <!doctype html><html lang="es"><body style="margin:0;padding:24px;background:#f3f5f7;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;color:#16202a;line-height:1.55">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;margin:0 auto;background:#fff;border:1px solid #d3dbe1;border-radius:6px">
            <tr><td style="padding:28px">
            <p style="margin:0 0 4px;font-size:13px;color:#0f6f6c;font-weight:600">LIBRO DE RECLAMACIONES</p>
            <h1 style="margin:0 0 6px;font-size:20px">Respuesta a tu hoja N.° {hoja}</h1>
            <p style="margin:0 0 16px;color:#5b6b77;font-size:14px">{Proveedor(prov)}</p>
            <p style="margin:0 0 12px">Hola {H(nombre)}:</p>
            <p style="margin:0 0 16px;white-space:pre-line">{H(respuesta)}</p>
            </td></tr></table></body></html>
            """;
        return new CorreoCompuesto($"Respuesta a tu hoja de reclamación N.° {hoja}", html,
                                   $"Respuesta a tu hoja N.° {hoja}\n\nHola {nombre}:\n\n{respuesta}");
    }
}