using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

public static class FiltroFicha
{
    /// <summary>
    /// 048 — EMITIR, ANULAR, HACER NOTAS Y AGREGAR UNA EMPRESA PIDEN LA FICHA.
    ///
    /// Cada comprobante queda firmado por una persona identificada con su
    /// documento. Mientras dure su plazo de aviso (los que ya existían: 15
    /// días), pasa; después, 409 con código «ficha_incompleta» y la pantalla
    /// abre «Completa tus datos». El super administrador y «ver como», no.
    /// </summary>
    public static RouteHandlerBuilder RequiereFicha(this RouteHandlerBuilder ruta) =>
        ruta.AddEndpointFilter(async (contexto, siguiente) =>
        {
            var http = contexto.HttpContext;
            var ctx = http.RequestServices.GetRequiredService<ContextoPeticion>();
            if (ctx.EsSuperAdmin || ctx.Suplantando || ctx.UsuarioId is not { } id) return await siguiente(contexto);

            var db = http.RequestServices.GetRequiredService<ContextoPlataforma>();
            var u = await db.Usuarios.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.TipoDocumento, x.NumeroDocumento, x.Nombres, x.ApellidoPaterno, x.ApellidoMaterno, x.FichaPlazo })
                .FirstOrDefaultAsync(http.RequestAborted);
            if (u is null || !Incompleta(u.TipoDocumento, u.NumeroDocumento, u.Nombres, u.ApellidoPaterno, u.ApellidoMaterno))
                return await siguiente(contexto);
            if (u.FichaPlazo is { } plazo && plazo > DateTime.UtcNow)
                return await siguiente(contexto);

            return Results.Json(new
            {
                error = "Antes de seguir, completa tus datos (documento, nombres y apellidos): " +
                        "cada comprobante queda firmado por una persona identificada.",
                codigo = "ficha_incompleta"
            }, statusCode: StatusCodes.Status409Conflict);
        });

    public static bool Incompleta(string? tipo, string? numero, string? nombres, string? paterno, string? materno) =>
        numero is null || nombres is null || paterno is null || (tipo == "1" && materno is null);
}