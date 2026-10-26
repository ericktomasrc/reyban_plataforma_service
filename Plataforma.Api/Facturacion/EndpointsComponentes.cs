using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>062: un componente de un kit como llega de la pantalla.</summary>
public record PeticionComponente(Guid ComponenteId, decimal Cantidad);

/// <summary>
/// 062 — DE QUÉ ESTÁ HECHO UN KIT (inventario ampliado D2).
///
/// «KIT BAÑO» = 1 inodoro + 1 lavatorio + 2 llaves. Venderlo descuenta eso.
/// Se guarda la lista entera; lo que ya no viene se desactiva (nada se borra).
/// Un kit no lleva otro kit, ni a sí mismo.
/// </summary>
public static class EndpointsComponentes
{
    public static void MapearComponentes(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/productos/{kitId:guid}/componentes")
                         .WithTags("Facturación · productos");

        grupo.MapGet("/", async (Guid kitId, ContextoPlataforma db, CancellationToken ct) =>
            Results.Ok(await (
                from c in db.ProductoComponentes.AsNoTracking()
                where c.KitId == kitId
                join p in db.Productos.IgnoreQueryFilters().AsNoTracking() on c.ComponenteId equals p.Id
                orderby p.Descripcion
                select new { c.ComponenteId, p.Codigo, p.Descripcion, p.UnidadMedida, c.Cantidad, p.ControlaStock, productoActivo = p.Activo })
                .ToListAsync(ct)))
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Los componentes de un kit");

        grupo.MapPut("/", async (Guid kitId, List<PeticionComponente> lista, ContextoPlataforma db, CancellationToken ct) =>
        {
            var kit = await db.Productos.FirstOrDefaultAsync(x => x.Id == kitId, ct);
            if (kit is null) return Results.NotFound();
            if (!kit.EsKit) return Results.BadRequest(new { error = "Marca el producto como kit antes de ponerle componentes." });

            lista ??= [];
            if (lista.Count > 50) return Results.BadRequest(new { error = "Hasta 50 componentes por kit." });
            if (lista.Any(x => x.Cantidad <= 0 || Math.Round(x.Cantidad, 4) != x.Cantidad))
                return Results.BadRequest(new { error = "Cada componente lleva una cantidad mayor que cero (hasta 4 decimales)." });
            if (lista.Any(x => x.ComponenteId == kitId)) return Results.BadRequest(new { error = "Un kit no puede llevarse a sí mismo." });
            if (lista.GroupBy(x => x.ComponenteId).Any(g => g.Count() > 1))
                return Results.BadRequest(new { error = "Un componente está dos veces: suma sus cantidades en una sola fila." });

            var ids = lista.Select(x => x.ComponenteId).ToList();
            var productos = await db.Productos.AsNoTracking().Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.Descripcion, p.EsKit }).ToListAsync(ct);
            if (productos.Count != ids.Count) return Results.BadRequest(new { error = "Un componente no existe o está desactivado." });
            if (productos.FirstOrDefault(p => p.EsKit) is { } otroKit)
                return Results.BadRequest(new { error = $"«{otroKit.Descripcion}» es otro kit: un kit no lleva kits. Pon sus componentes directamente." });

            var actuales = await db.ProductoComponentes.Where(c => c.KitId == kitId).ToListAsync(ct);
            foreach (var a in actuales)
            {
                var p = lista.FirstOrDefault(x => x.ComponenteId == a.ComponenteId);
                if (p is null) a.Activo = false;
                else a.Cantidad = p.Cantidad;
            }
            foreach (var p in lista.Where(x => actuales.All(a => a.ComponenteId != x.ComponenteId)))
                db.ProductoComponentes.Add(new ProductoComponente
                {
                    EmpresaId = kit.EmpresaId, KitId = kitId, ComponenteId = p.ComponenteId, Cantidad = p.Cantidad
                });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = lista.Count == 0 ? "El kit quedó sin componentes." : "Componentes guardados." });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Guardar los componentes de un kit (la lista entera)");
    }
}