using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Plataforma.Api.Arranque;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Middleware;

/// <summary>
/// 038 — UNA LÍNEA POR PETICIÓN en el log del día:
///
///     2026-10-18 19:40:02.311 [INF] RegistroPeticiones: POST /api/facturacion/documentos → 200 en 184 ms · usuario 7f3c… empresa 2a91… · ip 190.x.x.x
///
/// 4xx como aviso y 5xx como error. Sin cuerpo, sin cabeceras, sin la cadena
/// de consulta; los tokens de la ruta, tapados. No registra los archivos del
/// panel (.js, .css, imágenes) ni /health.
/// </summary>
public sealed class RegistroPeticiones(RequestDelegate siguiente, ILogger<RegistroPeticiones> log)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var ruta = http.Request.Path.Value ?? "/";
        if (HttpMethods.IsOptions(http.Request.Method) || ruta.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            (HttpMethods.IsGet(http.Request.Method) && Path.HasExtension(ruta)))
        {
            await siguiente(http);
            return;
        }

        var reloj = Stopwatch.StartNew();
        var fallo = false;
        try
        {
            await siguiente(http);
        }
        catch
        {
            fallo = true;
            throw;
        }
        finally
        {
            reloj.Stop();
            var estado = fallo && !http.Response.HasStarted ? 500 : http.Response.StatusCode;
            var nivel = estado >= 500 ? LogLevel.Error : estado >= 400 ? LogLevel.Warning : LogLevel.Information;
            if (log.IsEnabled(nivel))
            {
                // Quién: el contexto lo llena MiddlewareContexto (va después de este).
                var ctx = http.RequestServices.GetService<ContextoPeticion>();
                var quien = ctx?.UsuarioId is { } u
                    ? $"usuario {u.ToString()[..8]} empresa {(ctx.EmpresaId?.ToString()[..8] ?? "-")}"
                    : "sin sesión";
                log.Log(nivel, "{Metodo} {Ruta} → {Estado} en {Ms} ms · {Quien} · ip {Ip}",
                    http.Request.Method, RutaSegura.Tapar(ruta), estado, reloj.ElapsedMilliseconds,
                    quien, http.Connection.RemoteIpAddress?.ToString() ?? "?");
            }
        }
    }
}