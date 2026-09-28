using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Administracion;

/// <summary>
/// El catálogo de módulos del producto.
///
/// Es la lista de lo que se puede vender, no de lo que tiene contratado nadie.
/// Eso último sale de `/api/yo`, que ya cruza empresa, rol y permiso.
///
/// POR QUÉ HACE FALTA: la pantalla de alta de una empresa tiene que ofrecer
/// los módulos con casillas. Sin este endpoint, el front tendría que llevar la
/// lista escrita a mano — y el día que se añada un módulo en la migración,
/// nadie se acordaría de añadirlo también en el front.
/// </summary>
public static class EndpointsModulos
{
    public static void MapearModulos(this IEndpointRouteBuilder rutas)
    {
        rutas.MapGet("/api/admin/modulos", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var modulos = await db.Modulos
                .AsNoTracking()
                .Where(m => m.Disponible)
                .OrderBy(m => m.Orden)
                .Select(m => new { m.Codigo, m.Nombre, m.Descripcion, m.Ruta, m.Icono })
                .ToListAsync(ct);

            return Results.Ok(modulos);
        })
        .SoloSuperAdmin()
        .WithTags("Administración")
        .WithSummary("Listar los módulos del catálogo");
    }
}
