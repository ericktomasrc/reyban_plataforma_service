using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Inventario;

public sealed record PeticionAlmacen(Guid SucursalId, string? Nombre, string? PrincipalNombre);
public sealed record PeticionRenombrarAlmacen(string? Nombre);
public sealed record PeticionMoverAlmacen(Guid ProductoId, Guid OrigenId, Guid DestinoId, decimal Cantidad, string? Nota);

/// <summary>
/// 066 — VARIOS ALMACENES EN UNA SUCURSAL (inventario ampliado D6): tienda y
/// depósito. El stock de la sucursal no cambia; esto dice dónde está. La base
/// reparte cada movimiento (fn_inv_almacenes); aquí: crear, renombrar, ver el
/// stock por almacén y mover entre ellos.
/// </summary>
public static class EndpointsAlmacenes
{
    /// <summary>Para SqlQuery: columnas en snake_case.</summary>
    public sealed class FilaAlmacenStock
    {
        public Guid ProductoId { get; set; }
        public string Codigo { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public string Unidad { get; set; } = "";
        public Guid AlmacenId { get; set; }
        public decimal Fisico { get; set; }
    }

    public static void MapearAlmacenes(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/inventario/almacenes").WithTags("Inventario");

        grupo.MapGet("/", async (Guid? sucursalId, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var visibles = (await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct)).Select(v => v.Id).ToList();
            var lista = await db.InvAlmacenes.AsNoTracking()
                .Where(a => visibles.Contains(a.SucursalId) && (sucursalId == null || a.SucursalId == sucursalId))
                .OrderBy(a => a.SucursalId).ThenByDescending(a => a.Principal).ThenBy(a => a.Orden)
                .Select(a => new { a.Id, a.SucursalId, a.Nombre, a.Principal, a.Orden })
                .ToListAsync(ct);
            return Results.Ok(lista);
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Los almacenes de mis sucursales");

        grupo.MapPost("/", async (PeticionAlmacen p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var nombre = (p.Nombre ?? "").Trim();
            if (nombre.Length is < 1 or > 60) return Results.BadRequest(new { error = "Ponle un nombre al almacén (hasta 60 letras), por ejemplo «Depósito»." });
            var visibles = await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct);
            if (visibles.All(v => v.Id != p.SucursalId)) return Results.BadRequest(new { error = "Elige una sucursal en la que trabajes." });
            if (await db.InvAlmacenes.AnyAsync(a => a.SucursalId == p.SucursalId && a.Nombre.ToLower() == nombre.ToLower(), ct))
                return Results.Conflict(new { error = $"Ya hay un almacén «{nombre}» en esa sucursal." });
            var r = await ReglasInventario.ConPuntoAsync(db, async () =>
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT inv_almacen_crear({p.SucursalId}, {nombre}, {(string.IsNullOrWhiteSpace(p.PrincipalNombre) ? "TIENDA" : p.PrincipalNombre.Trim())})", ct), ct);
            if (r is not null) return Results.BadRequest(new { error = ReglasInventario.Mensaje(r) ?? "No se pudo crear el almacén." });
            return Results.Ok(new { mensaje = $"Almacén «{nombre.ToUpperInvariant()}» creado. Lo que había quedó en el principal." });
        })
        .RequierePermiso(Permisos.Inventario.Configurar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Crear un almacén en una sucursal (el primero hace nacer el principal)");

        grupo.MapPut("/{id:guid}", async (Guid id, PeticionRenombrarAlmacen p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var a = await db.InvAlmacenes.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            var nombre = (p.Nombre ?? "").Trim();
            if (nombre.Length is < 1 or > 60) return Results.BadRequest(new { error = "Ponle un nombre (hasta 60 letras)." });
            if (await db.InvAlmacenes.AnyAsync(x => x.Id != id && x.SucursalId == a.SucursalId && x.Nombre.ToLower() == nombre.ToLower(), ct))
                return Results.Conflict(new { error = $"Ya hay un almacén «{nombre}» en esa sucursal." });
            a.Nombre = nombre;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Almacén renombrado." });
        })
        .RequierePermiso(Permisos.Inventario.Configurar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Renombrar un almacén");

        // El stock de una sucursal repartido en sus almacenes (los productos que tienen algo).
        grupo.MapGet("/stock", async (Guid sucursalId, string? q, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var visibles = await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct);
            if (visibles.All(v => v.Id != sucursalId)) return Results.BadRequest(new { error = "Elige una sucursal en la que trabajes." });
            var texto = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().ToLowerInvariant() + "%";
            var filas = await db.Database.SqlQuery<FilaAlmacenStock>($"""
                SELECT s.producto_id, p.codigo, p.descripcion, p.unidad_medida AS unidad, s.almacen_id, s.fisico
                  FROM inv_almacen_stock s
                  JOIN inv_almacenes a ON a.id = s.almacen_id AND a.activo
                  JOIN productos p ON p.id = s.producto_id
                 WHERE a.sucursal_id = {sucursalId} AND s.fisico <> 0
                   AND ({texto}::text IS NULL OR lower(p.descripcion) LIKE {texto} OR lower(p.codigo) LIKE {texto})
                 ORDER BY p.descripcion
                 LIMIT 3000
                """).ToListAsync(ct);
            return Results.Ok(filas.GroupBy(f => f.ProductoId).Take(500).Select(g => new
            {
                productoId = g.Key, g.First().Codigo, g.First().Descripcion, g.First().Unidad,
                almacenes = g.ToDictionary(x => x.AlmacenId, x => x.Fisico)
            }));
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("El stock de una sucursal por almacén");

        grupo.MapPost("/mover", async (PeticionMoverAlmacen p, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (p.Cantidad <= 0) return Results.BadRequest(new { error = "La cantidad a mover tiene que ser mayor que cero." });
            var nota = string.IsNullOrWhiteSpace(p.Nota) ? null : (p.Nota.Trim() is var n && n.Length > 200 ? n[..200] : p.Nota.Trim());
            var r = await ReglasInventario.ConPuntoAsync(db, async () =>
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT inv_almacen_mover({p.ProductoId}, {p.OrigenId}, {p.DestinoId}, {p.Cantidad}, {nota})", ct), ct);
            if (r is not null) return Results.BadRequest(new { error = ReglasInventario.Mensaje(r) ?? "No se pudo mover." });
            return Results.Ok(new { mensaje = "Movido. El stock de la sucursal no cambia: solo dónde está." });
        })
        .RequierePermiso(Permisos.Inventario.Ajustar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Mover mercadería entre almacenes de la misma sucursal");
    }
}