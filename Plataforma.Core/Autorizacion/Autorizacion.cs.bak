using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
// Hace falta a mano: Plataforma.Core es una biblioteca, y los `using`
// implícitos de un proyecto web —donde este vendría solo— no llegan aquí.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Core.Autorizacion;

/// <summary>
/// Las comprobaciones que cruza una petición antes de llegar al código del
/// endpoint.
///
/// SE ENCADENAN ASÍ:
///
///     grupo.MapPost("/documentos", Emitir)
///          .RequierePermiso(Permisos.Facturacion.Emitir)
///          .RequiereModulo(Modulos.Facturacion);
///
/// Y ESTO ES LA SEGURIDAD DE VERDAD, no el menú. El menú se filtra por
/// permisos para que la gente no pulse cosas que le darían error; si alguien
/// escribe la URL a mano, se estrella aquí.
///
/// Viven en Core y no en la Api porque los módulos —Facturación, y los que
/// vengan— tienen que poder usarlas, y un módulo no conoce a la Api.
/// </summary>
public static class Autorizacion
{
    /// <summary>
    /// Exige una sesión válida y con el segundo factor superado.
    ///
    /// Se aplica sola dentro de RequierePermiso y RequiereModulo, así que
    /// rara vez hace falta escribirla.
    /// </summary>
    public static RouteHandlerBuilder RequiereSesion(this RouteHandlerBuilder ruta) =>
        ruta.AddEndpointFilter(async (contexto, siguiente) =>
        {
            var ctx = contexto.HttpContext.RequestServices
                              .GetRequiredService<ContextoPeticion>();

            if (!ctx.Autenticado)
                return Results.Unauthorized();

            // Una sesión con el 2FA pendiente existe pero no sirve para nada
            // más que para validar el código. Sin esta línea, el segundo
            // factor sería una pantalla decorativa.
            if (!ctx.SesionCompleta)
                return Results.Json(
                    new { error = "Falta validar el segundo factor.", codigo = "2fa_pendiente" },
                    statusCode: StatusCodes.Status401Unauthorized);

            // Y lo mismo con el cambio de contraseña obligatorio: si se
            // pudiera ignorar, la contraseña que escribió otra persona y viajó
            // por correo seguiría valiendo para siempre.
            //
            // El front ve el código y lleva al usuario a la pantalla de
            // cambio; los endpoints que sí puede usar en ese estado —cambiar
            // la clave y salir— no llevan este filtro.
            if (ctx.DebeCambiarClave)
                return Results.Json(
                    new { error = "Tienes que cambiar tu contraseña antes de seguir.",
                          codigo = "cambio_de_clave_pendiente" },
                    statusCode: StatusCodes.Status403Forbidden);

            return await siguiente(contexto);
        });


    /// <summary>
    /// Exige sesión, pero admite las dos situaciones en que el usuario está a
    /// medias: segundo factor pendiente y cambio de contraseña obligatorio.
    ///
    /// Es solo para los endpoints que resuelven justamente eso. Si se pone en
    /// cualquier otro sitio, deja de haber obligación.
    /// </summary>
    public static RouteHandlerBuilder RequiereSesionAMedias(this RouteHandlerBuilder ruta) =>
        ruta.AddEndpointFilter(async (contexto, siguiente) =>
        {
            var ctx = contexto.HttpContext.RequestServices
                              .GetRequiredService<ContextoPeticion>();

            return ctx.Autenticado
                ? await siguiente(contexto)
                : Results.Unauthorized();
        });


    /// <summary>
    /// Exige un permiso concreto. El super administrador los tiene todos.
    /// </summary>
    public static RouteHandlerBuilder RequierePermiso(
        this RouteHandlerBuilder ruta, string permiso) =>
        ruta.RequiereSesion()
            .AddEndpointFilter(async (contexto, siguiente) =>
            {
                var http = contexto.HttpContext;
                var ctx  = http.RequestServices.GetRequiredService<ContextoPeticion>();

                if (!ctx.Tiene(permiso))
                {
                    var db = http.RequestServices.GetRequiredService<ContextoPlataforma>();

                    // Un 403 no cambia ninguna fila, así que no deja rastro en
                    // la bitácora. Por eso se registra a mano: es justo el
                    // movimiento que interesa mirar cuando algo huele mal.
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        SELECT registrar_evento(
                            {TipoEvento.AccesoDenegado}::text, false, NULL, NULL, NULL,
                            jsonb_build_object('permiso', {permiso}::text,
                                               'ruta', {http.Request.Path.Value}::text))
                        """, http.RequestAborted);

                    return Results.Json(
                        new { error = $"Hace falta el permiso {permiso}." },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                return await siguiente(contexto);
            });


    /// <summary>
    /// Exige que la empresa tenga contratado el módulo.
    ///
    /// Es distinto del permiso: el permiso dice qué puede hacer esta persona;
    /// el módulo dice qué compró su empresa. Un administrador con todos los
    /// permisos del mundo no puede emitir si su empresa no contrató
    /// facturación.
    /// </summary>
    public static RouteHandlerBuilder RequiereModulo(
        this RouteHandlerBuilder ruta, string modulo) =>
        ruta.RequiereSesion()
            .AddEndpointFilter(async (contexto, siguiente) =>
            {
                var http = contexto.HttpContext;
                var ctx  = http.RequestServices.GetRequiredService<ContextoPeticion>();

                // El super administrador entra a todo: su trabajo es dar
                // soporte a empresas que sí lo contrataron.
                if (!ctx.EsSuperAdmin)
                {
                    var db = http.RequestServices.GetRequiredService<ContextoPlataforma>();

                    var contratado = await db.EmpresaModulos
                        .AnyAsync(m => m.EmpresaId == ctx.EmpresaId
                                    && m.ModuloCodigo == modulo
                                    && (m.VenceEn == null || m.VenceEn > DateTime.UtcNow),
                                  http.RequestAborted);

                    if (!contratado)
                        return Results.Json(
                            new { error = $"Tu empresa no tiene contratado el módulo de {modulo}." },
                            statusCode: StatusCodes.Status403Forbidden);
                }

                return await siguiente(contexto);
            });


    /// <summary>Solo el super administrador.</summary>
    public static RouteHandlerBuilder SoloSuperAdmin(this RouteHandlerBuilder ruta) =>
        ruta.RequiereSesion()
            .AddEndpointFilter(async (contexto, siguiente) =>
            {
                var ctx = contexto.HttpContext.RequestServices
                                  .GetRequiredService<ContextoPeticion>();

                return ctx.EsSuperAdmin
                    ? await siguiente(contexto)
                    : Results.Json(new { error = "No autorizado." },
                                   statusCode: StatusCodes.Status403Forbidden);
            });
}
