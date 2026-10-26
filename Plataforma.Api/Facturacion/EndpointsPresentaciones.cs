using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>061: una presentación como llega de la pantalla. Id null = nueva.</summary>
public record PeticionPresentacion(
    Guid? Id, string Nombre, string UnidadMedida, decimal Factor,
    decimal? PrecioUnitario, bool IncluyeIgv, string? CodigoBarras,
    bool ParaCompra = true, bool ParaVenta = true);

/// <summary>
/// 061 — LAS PRESENTACIONES DE UN PRODUCTO (inventario ampliado D1).
///
/// «CAJA X 12» (BX) = 12 unidades del producto. Se compra o se vende en la
/// presentación; el stock se cuenta siempre en la unidad del producto.
///
/// Se guardan todas juntas (la lista entera). Nada se borra: la que ya no
/// viene se desactiva. Si una presentación YA USADA cambia de factor, no se
/// pisa: se desactiva y nace otra, para que lo vendido siga diciendo lo que
/// era (una caja de 12 sigue siendo de 12 en la factura de ayer).
/// </summary>
public static class EndpointsPresentaciones
{
    public static void MapearPresentaciones(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/productos/{productoId:guid}/presentaciones")
                         .WithTags("Facturación · productos");

        grupo.MapGet("/", async (Guid productoId, ContextoPlataforma db, CancellationToken ct) =>
            Results.Ok(await db.ProductoPresentaciones.AsNoTracking()
                .Where(x => x.ProductoId == productoId)
                .OrderBy(x => x.Factor).ThenBy(x => x.Nombre)
                .Select(x => new
                {
                    x.Id, x.Nombre, x.UnidadMedida, x.Factor, x.PrecioUnitario, x.IncluyeIgv,
                    x.CodigoBarras, x.ParaCompra, x.ParaVenta
                })
                .ToListAsync(ct)))
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las presentaciones de un producto (caja, paquete, saco…)");

        grupo.MapPut("/", async (Guid productoId, List<PeticionPresentacion> lista,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var producto = await db.Productos.FirstOrDefaultAsync(x => x.Id == productoId, ct);
            if (producto is null) return Results.NotFound();
            if (producto.UnidadMedida == "ZZ" && lista.Count > 0)
                return Results.BadRequest(new { error = "Un servicio no tiene presentaciones: no se cuenta en cajas ni en paquetes." });

            lista ??= [];
            if (lista.Count > 20) return Results.BadRequest(new { error = "Hasta 20 presentaciones por producto." });

            var nombres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var barras = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in lista)
            {
                var nombre = (p.Nombre ?? "").Trim();
                if (nombre.Length is < 1 or > 60) return Results.BadRequest(new { error = "Cada presentación lleva un nombre (hasta 60 letras), por ejemplo «Caja x 12»." });
                if (!nombres.Add(nombre)) return Results.BadRequest(new { error = $"La presentación «{nombre}» está dos veces." });
                if (string.IsNullOrWhiteSpace(p.UnidadMedida)) return Results.BadRequest(new { error = $"Elige la unidad de SUNAT de «{nombre}» (caja, paquete…)." });
                if (p.Factor <= 0 || Math.Round(p.Factor, 4) != p.Factor)
                    return Results.BadRequest(new { error = $"«{nombre}»: escribe cuántas unidades trae (mayor que cero, hasta 4 decimales)." });
                if (p.PrecioUnitario is { } pr && pr <= 0) return Results.BadRequest(new { error = $"«{nombre}»: el precio debe ser mayor que cero, o déjalo vacío." });
                if (!p.ParaCompra && !p.ParaVenta) return Results.BadRequest(new { error = $"«{nombre}»: márcala para comprar, para vender o para las dos." });
                var cb = p.CodigoBarras?.Trim();
                if (!string.IsNullOrEmpty(cb) && !barras.Add(cb)) return Results.BadRequest(new { error = $"El código de barras {cb} está dos veces." });
            }

            // El código de barras no puede ser de otra presentación de la empresa.
            if (barras.Count > 0)
            {
                var propias = lista.Where(x => x.Id is not null).Select(x => x.Id!.Value).ToList();
                var barrasL = barras.ToList();
                var ocupado = await db.ProductoPresentaciones.AsNoTracking()
                    .Where(x => x.CodigoBarras != null && barrasL.Contains(x.CodigoBarras) && !propias.Contains(x.Id))
                    .Select(x => x.CodigoBarras).FirstOrDefaultAsync(ct);
                if (ocupado is not null) return Results.Conflict(new { error = $"El código de barras {ocupado} ya es de otra presentación." });
            }

            var actuales = await db.ProductoPresentaciones.Where(x => x.ProductoId == productoId).ToListAsync(ct);
            var usadas = await UsadasAsync(db, actuales.Select(x => x.Id).ToList(), ct);
            var nuevas = new List<ProductoPresentacion>();

            // 1) Lo que se desactiva (no vino, o cambió de factor ya usada). Primero,
            //    para que el nombre quede libre.
            foreach (var a in actuales)
            {
                var p = lista.FirstOrDefault(x => x.Id == a.Id);
                if (p is null || (p.Factor != a.Factor && usadas.Contains(a.Id))) a.Activo = false;
            }
            await db.SaveChangesAsync(ct);

            // 2) Lo que se actualiza o nace.
            foreach (var p in lista)
            {
                var a = p.Id is { } id ? actuales.FirstOrDefault(x => x.Id == id && x.Activo) : null;
                if (a is null)
                {
                    a = new ProductoPresentacion { EmpresaId = producto.EmpresaId, ProductoId = productoId };
                    nuevas.Add(a);
                    db.ProductoPresentaciones.Add(a);
                }
                a.Nombre = p.Nombre.Trim();
                a.UnidadMedida = p.UnidadMedida.Trim().ToUpperInvariant();
                a.Factor = p.Factor;
                a.PrecioUnitario = p.PrecioUnitario;
                a.IncluyeIgv = p.IncluyeIgv;
                a.CodigoBarras = string.IsNullOrWhiteSpace(p.CodigoBarras) ? null : p.CodigoBarras.Trim();
                a.ParaCompra = p.ParaCompra;
                a.ParaVenta = p.ParaVenta;
            }
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                mensaje = lista.Count == 0 ? "El producto queda sin presentaciones." : "Presentaciones guardadas."
            });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Guardar las presentaciones de un producto (la lista entera)");
    }

    /// <summary>Las presentaciones que ya salieron en una línea, una guía o un ingreso.</summary>
    private static async Task<HashSet<Guid>> UsadasAsync(ContextoPlataforma db, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var a = await db.DocumentoLineas.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.PresentacionId != null && ids.Contains(l.PresentacionId.Value)).Select(l => l.PresentacionId!.Value).Distinct().ToListAsync(ct);
        var b = await db.GuiaBienes.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.PresentacionId != null && ids.Contains(l.PresentacionId.Value)).Select(l => l.PresentacionId!.Value).Distinct().ToListAsync(ct);
        var c = await db.InvOperacionLineas.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.PresentacionId != null && ids.Contains(l.PresentacionId.Value)).Select(l => l.PresentacionId!.Value).Distinct().ToListAsync(ct);
        return [.. a, .. b, .. c];
    }
}