using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Middleware;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Sesion;

public record PeticionIngreso(string Correo, string Clave);

public static class EndpointsSesion
{
    public static void MapearSesion(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api").WithTags("Sesión");

        // ---------------------------------------------------------------
        grupo.MapPost("/sesion", async (
            PeticionIngreso peticion,
            ServicioIngreso servicio,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(peticion.Correo) ||
                string.IsNullOrWhiteSpace(peticion.Clave))
            {
                return Results.BadRequest(new { error = "Falta el correo o la contraseña." });
            }

            var r = await servicio.IngresarAsync(peticion.Correo, peticion.Clave, ct);

            if (!r.Exito)
                return Results.Json(new { error = r.Mensaje },
                                    statusCode: StatusCodes.Status401Unauthorized);

            // LA COOKIE, Y POR QUÉ CADA OPCIÓN:
            //
            //   HttpOnly   el JavaScript de la página no puede leerla, así que
            //              un XSS no se lleva la sesión. Es la razón principal
            //              de usar cookie y no un token en localStorage.
            //
            //   SameSite   el navegador no la manda en peticiones que nacen de
            //              otro sitio: eso es la defensa contra CSRF.
            //
            //   Secure     solo por HTTPS. En desarrollo iría en false y la
            //              cookie no llegaría; se decide según el esquema real
            //              de la petición.
            //
            //   Expires    sin fecha sería una cookie de sesión del navegador,
            //              que desaparece al cerrarlo. Con fecha, dura lo
            //              mismo que la fila de `sesiones`, que es quien manda.
            http.Response.Cookies.Append(MiddlewareContexto.NombreCookie, r.Token!,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure   = http.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    Path     = "/",
                    Expires  = r.Expira
                });

            return Results.Ok(new
            {
                segundoFactorPendiente = r.SegundoFactorPendiente,
                cambioDeClaveForzado   = r.CambioDeClaveForzado
            });
        })
        .WithSummary("Iniciar sesión");


        // ---------------------------------------------------------------
        grupo.MapDelete("/sesion", async (
            ServicioIngreso servicio,
            ContextoPeticion ctx,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (ctx.SesionId is { } id)
                await servicio.SalirAsync(id, ct);

            http.Response.Cookies.Delete(MiddlewareContexto.NombreCookie);
            return Results.NoContent();
        })
        .WithSummary("Cerrar sesión");


        // ---------------------------------------------------------------
        //
        // EL ENDPOINT QUE USA EL FRONT PARA SABERLO TODO DE UN GOLPE:
        // quién soy, de qué empresa, qué puedo hacer y qué menú pintar.
        //
        // Devolver el menú desde aquí, calculado en la base, evita que el
        // front tenga su propia idea de qué debería ver cada quien — que es
        // como acaban divergiendo lo que se muestra y lo que se permite.
        grupo.MapGet("/yo", async (
            ContextoPeticion ctx,
            ContextoPlataforma db,
            CancellationToken ct) =>
        {
            var usuario = await db.Usuarios
                .AsNoTracking()
                .Where(u => u.Id == ctx.UsuarioId)
                .Select(u => new { u.Id, u.Nombre, u.Correo, u.EsSuperAdmin, u.DosfaActivo, u.ClaveCambioForzado })
                .FirstOrDefaultAsync(ct);

            if (usuario is null) return Results.Unauthorized();

            var empresa = ctx.EmpresaId is null ? null : await db.Empresas
                .AsNoTracking()
                .Where(e => e.Id == ctx.EmpresaId)
                .Select(e => new { e.Id, e.Ruc, e.RazonSocial })
                .FirstOrDefaultAsync(ct);

            var menu = await db.Menu
                .Where(m => m.UsuarioId == ctx.UsuarioId)
                .OrderBy(m => m.Orden)
                .Select(m => new { m.Codigo, m.Nombre, m.Ruta, m.Icono })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                usuario,
                empresa,
                permisos = ctx.Permisos.OrderBy(p => p).ToArray(),
                menu
            });
        })
        .RequiereSesion()
        .WithSummary("Quién soy, qué puedo y qué menú veo");
    }
}
