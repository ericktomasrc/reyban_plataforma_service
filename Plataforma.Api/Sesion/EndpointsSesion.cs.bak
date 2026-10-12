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
                segundoFactorPendiente      = r.SegundoFactorPendiente,
                debeConfigurarSegundoFactor = r.DebeConfigurarSegundoFactor,
                cambioDeClaveForzado        = r.CambioDeClaveForzado
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

            // LOS NOMBRES DE SUS ROLES, para enseñarlos en la cabecera.
            //
            // Son solo para mostrar: lo que decide qué puede hacer cada uno son
            // los permisos, que van aparte. Si algún día el front empezara a
            // preguntar por el nombre del rol, sería el mismo error que el
            // back tiene prohibido — y por el mismo motivo: un rol nuevo
            // obligaría a tocar código.
            var roles = await db.UsuarioRoles
                .AsNoTracking()
                .Where(r => r.UsuarioId == ctx.UsuarioId && r.Activo)
                .OrderBy(r => r.Rol.Nombre)
                .Select(r => r.Rol.Nombre)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                usuario,
                empresa,
                permisos = ctx.Permisos.OrderBy(p => p).ToArray(),
                roles,
                menu,

                // LAS DOS BANDERAS QUE EL FRONT NECESITA PARA SABER A DÓNDE
                // LLEVAR AL USUARIO.
                //
                // Sin ellas tendría que deducirlo del código de error de otra
                // llamada, y al recargar la página no tendría forma de saberlo.
                debeCambiarClave       = ctx.DebeCambiarClave,
                segundoFactorPendiente = !ctx.SesionCompleta,

                // Distingue «enséñame el QR» de «pídeme el código». Sin esta
                // bandera el front no sabría cuál de las dos pantallas pintar
                // al recargar la página a medio camino.
                // FALSO MIENTRAS SE SUPLANTA, aunque el cliente no tenga
                // segundo factor. Sin esto viajaría la bandera del SUPLANTADO, y
                // aunque hoy sea inerte —`segundoFactorPendiente` es falso—
                // dejaría media ruta construida hacia el peor fallo del front:
                // la pantalla del segundo factor vive fuera del marco, o sea sin
                // barra de aviso y sin botón de volver.
                debeConfigurarSegundoFactor = !ctx.Suplantando && !usuario.DosfaActivo,

                // LA SUPLANTACIÓN, SI LA HAY.
                //
                // Todo lo de arriba —el usuario, la empresa, los permisos, el
                // menú— es ya del usuario SUPLANTADO, porque `ctx` trae la
                // identidad efectiva. Eso es exactamente lo que se busca: el
                // front pinta la plataforma del cliente sin saber que está
                // suplantando.
                //
                // Y por eso mismo esto hace falta. Sin este dato, el super
                // administrador vería la pantalla de su cliente sin ninguna
                // señal de que no es la suya, sin forma de volver, y bastaría
                // recargar la página para perderse. La barra de aviso del front
                // sale de aquí.
                suplantacion = ctx.Suplantando
                    ? new
                    {
                        suplantador = ctx.SuplantadorNombre,
                        suplantado  = ctx.SuplantadoNombre,
                        desde       = ctx.SuplantacionInicio
                    }
                    : null
            });
        })
        // A MEDIAS a propósito.
        //
        // Es el endpoint que responde «¿quién soy y qué me falta?», así que
        // tiene que funcionar precisamente cuando al usuario le falta algo. No
        // devuelve nada que no sea suyo, y quien no ha superado el segundo
        // factor sigue sin poder usar ningún otro endpoint.
        .RequiereSesionAMedias()
        .WithSummary("Quién soy, qué puedo y qué menú veo");
    }
}
