using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Inventario;

public sealed record PeticionProveedor(string? TipoDocumento, string? NumeroDocumento, string? RazonSocial,
                                       string? Direccion, string? Correo, string? Telefono);
public sealed record PeticionCategoria(string? Nombre);

/// <summary>
/// 052 — LAS MAESTRAS NUEVAS: PROVEEDORES (a quién se le compra) y
/// CATEGORÍAS DE PRODUCTO (para filtrar el stock, los productos y los
/// reportes). Nada se borra: se desactiva.
/// </summary>
public static class EndpointsProveedores
{
    private static readonly Regex Ruc = new("^(10|15|17|20)[0-9]{9}$", RegexOptions.Compiled);
    private static readonly Regex Dni = new("^[0-9]{8}$", RegexOptions.Compiled);
    private static readonly Regex Otro = new("^[A-Z0-9-]{1,15}$", RegexOptions.Compiled);
    private static readonly Regex Correo = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public static void MapearProveedores(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/inventario/proveedores").WithTags("Inventario · proveedores");

        // El buscador (ingreso por compra).
        grupo.MapGet("/", async (string? q, ContextoPlataforma db, CancellationToken ct) =>
        {
            var consulta = db.Proveedores.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var t = q.Trim().ToLowerInvariant();
                consulta = consulta.Where(p => p.NumeroDocumento.Contains(t) || p.RazonSocial.ToLower().Contains(t));
            }
            return Results.Ok(await consulta.OrderBy(p => p.RazonSocial).Take(20)
                .Select(p => new { p.Id, p.TipoDocumento, p.NumeroDocumento, p.RazonSocial }).ToListAsync(ct));
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Buscar proveedores");


        grupo.MapGet("/pagina", async (
            string? q, string? estado, int? pagina, int? porPagina, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var pp = Listados.PorPagina(porPagina);
            var pg = Listados.Pagina(pagina);
            var consulta = (estado is "inactivos" or "todos" ? db.Proveedores.IgnoreQueryFilters() : db.Proveedores)
                .AsNoTracking().Where(p => p.EmpresaId == empresaId);
            if (estado == "inactivos") consulta = consulta.Where(p => !p.Activo);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var t = q.Trim().ToLowerInvariant();
                consulta = consulta.Where(p => p.NumeroDocumento.Contains(t) || p.RazonSocial.ToLower().Contains(t));
            }
            var total = await consulta.CountAsync(ct);
            var filas = await consulta.OrderByDescending(p => p.CreadoEn).ThenBy(p => p.Id)
                .Skip((pg - 1) * pp).Take(pp)
                .Select(p => new { p.Id, p.TipoDocumento, p.NumeroDocumento, p.RazonSocial, p.Direccion, p.Correo, p.Telefono, p.Activo, p.CreadoEn })
                .ToListAsync(ct);

            // La última compra de cada uno.
            var ids = filas.Select(f => f.Id).ToList();
            var ultimas = await db.InvOperaciones.AsNoTracking()
                .Where(o => o.ProveedorId != null && ids.Contains(o.ProveedorId.Value) && o.Estado == "registrada")
                .GroupBy(o => o.ProveedorId!.Value)
                .Select(g => new { Id = g.Key, Fecha = g.Max(o => o.Fecha) })
                .ToDictionaryAsync(x => x.Id, x => x.Fecha, ct);

            return Results.Ok(new
            {
                total, pagina = pg, porPagina = pp,
                filas = filas.Select(f => new
                {
                    f.Id, f.TipoDocumento, f.NumeroDocumento, f.RazonSocial, f.Direccion, f.Correo, f.Telefono, f.Activo, f.CreadoEn,
                    ultimaCompra = ultimas.TryGetValue(f.Id, out var u) ? u : (DateOnly?)null
                })
            });
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("La lista de proveedores, paginada");


        grupo.MapPost("/", async (PeticionProveedor p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            if (Revisar(p) is { } error) return Results.BadRequest(new { error });
            var tipo = p.TipoDocumento!.Trim();
            var numero = p.NumeroDocumento!.Trim().ToUpperInvariant();
            if (await db.Proveedores.IgnoreQueryFilters().AnyAsync(x => x.EmpresaId == empresaId && x.TipoDocumento == tipo && x.NumeroDocumento == numero, ct))
                return Results.Conflict(new { error = $"Ya tienes el proveedor {numero} (si no lo ves, está desactivado)." });

            var prov = new Proveedor { EmpresaId = empresaId, TipoDocumento = tipo, NumeroDocumento = numero };
            Copiar(prov, p);
            db.Proveedores.Add(prov);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { prov.Id, mensaje = "Proveedor guardado." });
        })
        .RequierePermiso(Permisos.Inventario.Ingresar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Dar de alta un proveedor");


        grupo.MapPut("/{id:guid}", async (Guid id, PeticionProveedor p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var prov = await db.Proveedores.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (prov is null) return Results.NotFound(new { error = "No se encontró el proveedor." });
            // El documento no cambia: es quién es. Se corrigen nombre y contacto.
            if (Revisar(p with { TipoDocumento = prov.TipoDocumento, NumeroDocumento = prov.NumeroDocumento }) is { } error)
                return Results.BadRequest(new { error });
            Copiar(prov, p);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Proveedor actualizado." });
        })
        .RequierePermiso(Permisos.Inventario.Ingresar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Corregir un proveedor");


        grupo.MapPost("/{id:guid}/desactivar", (Guid id, ContextoPlataforma db, CancellationToken ct) => Activar(db, id, false, ct))
            .RequierePermiso(Permisos.Inventario.Ingresar).RequiereModulo(Modulos.Facturacion).WithSummary("Desactivar un proveedor");
        grupo.MapPost("/{id:guid}/reactivar", (Guid id, ContextoPlataforma db, CancellationToken ct) => Activar(db, id, true, ct))
            .RequierePermiso(Permisos.Inventario.Ingresar).RequiereModulo(Modulos.Facturacion).WithSummary("Reactivar un proveedor");


        // =====================================================================
        // CATEGORÍAS DE PRODUCTO
        // =====================================================================
        var cats = rutas.MapGroup("/api/inventario/categorias").WithTags("Inventario · categorías");

        cats.MapGet("/", async (bool? todas, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var lista = await (todas == true ? db.ProductoCategorias.IgnoreQueryFilters() : db.ProductoCategorias).AsNoTracking()
                .Where(c => c.EmpresaId == empresaId).OrderBy(c => c.Nombre)
                .Select(c => new { c.Id, c.Nombre, c.Activo, c.CreadoEn }).ToListAsync(ct);
            var usos = await db.Productos.AsNoTracking().Where(p => p.CategoriaId != null)
                .GroupBy(p => p.CategoriaId!.Value).Select(g => new { Id = g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.N, ct);
            return Results.Ok(lista.Select(c => new { c.Id, c.Nombre, c.Activo, c.CreadoEn, productos = usos.GetValueOrDefault(c.Id) }));
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las categorías de producto");


        cats.MapPost("/", async (PeticionCategoria p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var nombre = EndpointsInventarioOperaciones.Limpio(p.Nombre, 60);
            if (nombre is null) return Results.BadRequest(new { error = "Escribe el nombre de la categoría (hasta 60 caracteres)." });
            if (await db.ProductoCategorias.AnyAsync(c => c.EmpresaId == empresaId && c.Nombre.ToLower() == nombre.ToLower(), ct))
                return Results.Conflict(new { error = $"Ya existe la categoría «{nombre}»." });
            var c = new ProductoCategoria { EmpresaId = empresaId, Nombre = nombre };
            db.ProductoCategorias.Add(c);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { c.Id, c.Nombre, mensaje = "Categoría creada." });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Crear una categoría");


        cats.MapPut("/{id:guid}", async (Guid id, PeticionCategoria p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var c = await db.ProductoCategorias.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "No se encontró la categoría." });
            var nombre = EndpointsInventarioOperaciones.Limpio(p.Nombre, 60);
            if (nombre is null) return Results.BadRequest(new { error = "Escribe el nombre de la categoría (hasta 60 caracteres)." });
            if (await db.ProductoCategorias.AnyAsync(x => x.Id != id && x.EmpresaId == c.EmpresaId && x.Nombre.ToLower() == nombre.ToLower(), ct))
                return Results.Conflict(new { error = $"Ya existe la categoría «{nombre}»." });
            c.Nombre = nombre;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Categoría renombrada." });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Renombrar una categoría");


        cats.MapPost("/{id:guid}/desactivar", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var c = await db.ProductoCategorias.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "No se encontró la categoría." });
            c.Activo = false;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Categoría desactivada. Los productos que la tenían la conservan, pero ya no se ofrece." });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Desactivar una categoría");


        // e161: la que se desactivó por error vuelve a ofrecerse.
        cats.MapPost("/{id:guid}/reactivar", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var c = await db.ProductoCategorias.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "No se encontró la categoría." });
            c.Activo = true;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Categoría reactivada: vuelve a ofrecerse en los productos." });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Reactivar una categoría");
    }


    // =========================================================================

    /// <summary>
    /// Para el ingreso por compra con un proveedor escrito a mano: si ya existe
    /// (por su documento), ese; si no, se guarda.
    /// </summary>
    public static async Task<(Guid? Id, string? Error)> GuardarOUsarAsync(
        ContextoPlataforma db, Guid empresaId, ProveedorNuevo n, CancellationToken ct)
    {
        var p = new PeticionProveedor(n.TipoDocumento ?? "6", n.NumeroDocumento, n.RazonSocial, n.Direccion, null, null);
        if (Revisar(p) is { } error) return (null, error);
        var tipo = p.TipoDocumento!.Trim();
        var numero = p.NumeroDocumento!.Trim().ToUpperInvariant();
        var ya = await db.Proveedores.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.EmpresaId == empresaId && x.TipoDocumento == tipo && x.NumeroDocumento == numero, ct);
        if (ya is not null)
        {
            if (!ya.Activo) ya.Activo = true;
            return (ya.Id, null);
        }
        var prov = new Proveedor { EmpresaId = empresaId, TipoDocumento = tipo, NumeroDocumento = numero };
        Copiar(prov, p);
        db.Proveedores.Add(prov);
        await db.SaveChangesAsync(ct);
        return (prov.Id, null);
    }

    private static string? Revisar(PeticionProveedor p)
    {
        var tipo = (p.TipoDocumento ?? "").Trim();
        var numero = (p.NumeroDocumento ?? "").Trim().ToUpperInvariant();
        if (tipo is not ("6" or "1" or "4" or "7" or "0")) return "Elige el tipo de documento del proveedor.";
        if (tipo == "6" && !Ruc.IsMatch(numero)) return "El RUC del proveedor no es válido (11 dígitos, empieza con 10, 15, 17 o 20).";
        if (tipo == "1" && !Dni.IsMatch(numero)) return "El DNI son 8 dígitos.";
        if (tipo is not ("6" or "1") && !Otro.IsMatch(numero)) return "El número de documento no es válido.";
        if (string.IsNullOrWhiteSpace(p.RazonSocial) || p.RazonSocial.Trim().Length > 200) return "Escribe la razón social del proveedor.";
        if (!string.IsNullOrWhiteSpace(p.Correo) && !Correo.IsMatch(p.Correo.Trim())) return "El correo no tiene forma de correo.";
        if ((p.Telefono ?? "").Trim().Length > 30) return "El teléfono es demasiado largo.";
        return null;
    }

    private static void Copiar(Proveedor prov, PeticionProveedor p)
    {
        prov.RazonSocial = p.RazonSocial!.Trim();
        prov.Direccion = EndpointsInventarioOperaciones.Limpio(p.Direccion, 250);
        prov.Correo = EndpointsInventarioOperaciones.Limpio(p.Correo, 150);
        prov.Telefono = EndpointsInventarioOperaciones.Limpio(p.Telefono, 30);
    }

    private static async Task<IResult> Activar(ContextoPlataforma db, Guid id, bool activo, CancellationToken ct)
    {
        var prov = await db.Proveedores.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (prov is null) return Results.NotFound(new { error = "No se encontró el proveedor." });
        prov.Activo = activo;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { mensaje = activo ? "Proveedor reactivado." : "Proveedor desactivado. Sus compras se conservan." });
    }
}