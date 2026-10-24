using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

public record PeticionCargo(string? Nombre, string? Motivo = null);
public record PeticionPasarCargo(Guid Hacia, Guid[]? Usuarios = null);

/// <summary>
/// 047 — CARGOS DE LA EMPRESA (Personal › Cargos), como los «Puestos de
/// trabajo» de Odoo.
///
///   GET  /api/cargos                 la maestra, con cuántas personas usa cada uno
///   POST /api/cargos                 crear
///   PUT  /api/cargos/{id}            renombrar (en uso: con motivo)
///   POST /api/cargos/{id}/activar · /desactivar
///   POST /api/cargos/{id}/pasar      mover a esas personas a otro cargo
///
/// En la ficha no hace falta crearlos aquí: al escribir un cargo nuevo, la base
/// lo agrega sola. Esta pantalla es para corregir y ordenar.
///
/// REGLAS: no se repiten nombres; renombrar uno en uso pide motivo (queda en
/// la bitácora) y cambia todas sus fichas; para un cambio de puesto de verdad,
/// «pasar» y desactivar el viejo; no se desactiva uno que usa alguien activo.
/// Ordenar la maestra es del administrador; el jefe de sucursal solo la lee.
/// </summary>
public static class EndpointsCargos
{
    public static void MapearCargos(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/cargos").WithTags("Usuarios");

        grupo.MapGet("/", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige primero una empresa." });

            var cargos = await db.Cargos.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.EmpresaId == empresaId)
                .Select(c => new { c.Id, c.Nombre, c.Activo, c.Motivo })
                .ToListAsync(ct);

            var personas = await db.Database.SqlQuery<PersonaDeCargo>($"""
                SELECT u.cargo_id AS "CargoId", u.id AS "Id", u.nombre AS "Nombre", u.activo AS "Activo"
                  FROM usuarios u
                 WHERE u.empresa_id = {empresaId} AND u.cargo_id IS NOT NULL AND u.nivel_cuenta IS NULL
                """).ToListAsync(ct);

            return Results.Ok(cargos
                .OrderByDescending(c => c.Activo).ThenBy(c => c.Nombre.ToLowerInvariant())
                .Select(c =>
                {
                    var suyas = personas.Where(p => p.CargoId == c.Id).OrderBy(p => p.Nombre).ToList();
                    return new
                    {
                        c.Id, c.Nombre, c.Activo, c.Motivo,
                        usos = suyas.Count,
                        usosActivos = suyas.Count(p => p.Activo),
                        personas = suyas.Select(p => new { p.Id, p.Nombre, p.Activo })
                    };
                }));
        })
        .RequierePermiso(Permisos.Usuarios.Ver)
        .WithSummary("Los cargos de mi empresa");


        grupo.MapPost("/", async (PeticionCargo p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige primero una empresa." });
            if (new Gestor(ctx).Restringido) return SoloAdministrador();

            var nombre = Limpio(p.Nombre);
            if (nombre is null) return Results.BadRequest(new { error = "Escribe el nombre del cargo (hasta 80 caracteres).", campo = "nombre" });
            if (await RepetidoAsync(db, empresaId, nombre, null, ct) is { } ya)
                return Results.Conflict(new { error = $"Ya existe el cargo «{ya}».", campo = "nombre" });

            db.Cargos.Add(new Cargo { EmpresaId = empresaId, Nombre = nombre });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = $"Cargo «{nombre}» creado." });
        })
        .RequierePermiso(Permisos.Usuarios.Editar)
        .WithSummary("Crear un cargo");


        grupo.MapPut("/{id:guid}", async (Guid id, PeticionCargo p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige primero una empresa." });
            if (new Gestor(ctx).Restringido) return SoloAdministrador();

            var cargo = await db.Cargos.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id && c.EmpresaId == empresaId, ct);
            if (cargo is null) return Results.NotFound(new { error = "Ese cargo no existe." });

            var nombre = Limpio(p.Nombre);
            if (nombre is null) return Results.BadRequest(new { error = "Escribe el nombre del cargo (hasta 80 caracteres).", campo = "nombre" });
            if (nombre == cargo.Nombre) return Results.Ok(new { mensaje = "Sin cambios." });
            if (await RepetidoAsync(db, empresaId, nombre, id, ct) is { } ya)
                return Results.Conflict(new { error = $"Ya existe el cargo «{ya}». Si es el mismo puesto, usa «Pasar a otro cargo».", campo = "nombre" });

            var usos = await UsosAsync(db, id, ct);
            var motivo = (p.Motivo ?? "").Trim();
            if (usos > 0 && motivo.Length < 5)
                return Results.BadRequest(new
                {
                    error = $"Lo usan {usos} persona{(usos == 1 ? "" : "s")}: el cambio se verá en sus fichas. Escribe el motivo " +
                            "(por ejemplo, «estaba mal escrito»). Si el puesto cambió de verdad, crea el nuevo y usa «Pasar a otro cargo».",
                    campo = "motivo"
                });

            cargo.Nombre = nombre;
            cargo.Motivo = motivo.Length == 0 ? null : motivo.Length > 300 ? motivo[..300] : motivo;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = usos > 0 ? $"Cargo renombrado: se cambió en {usos} ficha{(usos == 1 ? "" : "s")}." : "Cargo renombrado." });
        })
        .RequierePermiso(Permisos.Usuarios.Editar)
        .WithSummary("Renombrar un cargo (en uso, con motivo)");


        grupo.MapPost("/{id:guid}/desactivar", (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            CambiarActivoAsync(id, false, db, ctx, ct))
        .RequierePermiso(Permisos.Usuarios.Editar)
        .WithSummary("Desactivar un cargo (si nadie activo lo usa)");

        grupo.MapPost("/{id:guid}/activar", (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            CambiarActivoAsync(id, true, db, ctx, ct))
        .RequierePermiso(Permisos.Usuarios.Editar)
        .WithSummary("Reactivar un cargo");


        grupo.MapPost("/{id:guid}/pasar", async (Guid id, PeticionPasarCargo p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige primero una empresa." });
            if (new Gestor(ctx).Restringido) return SoloAdministrador();
            if (id == p.Hacia) return Results.BadRequest(new { error = "Elige otro cargo." });

            var hacia = await db.Cargos.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == p.Hacia && c.EmpresaId == empresaId, ct);
            var desde = await db.Cargos.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(c => c.Id == id && c.EmpresaId == empresaId, ct);
            if (hacia is null || !desde) return Results.NotFound(new { error = "Ese cargo no existe." });

            var usuarios = p.Usuarios is { Length: > 0 } u ? u : null;
            var n = usuarios is null
                ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                      UPDATE usuarios SET cargo = {hacia.Nombre}, cargo_id = {hacia.Id}
                       WHERE cargo_id = {id} AND empresa_id = {empresaId} AND nivel_cuenta IS NULL
                      """, ct)
                : await db.Database.ExecuteSqlInterpolatedAsync($"""
                      UPDATE usuarios SET cargo = {hacia.Nombre}, cargo_id = {hacia.Id}
                       WHERE cargo_id = {id} AND empresa_id = {empresaId} AND nivel_cuenta IS NULL
                         AND id = ANY ({usuarios})
                      """, ct);

            return Results.Ok(new { mensaje = $"{n} persona{(n == 1 ? "" : "s")} pasaron a «{hacia.Nombre}»." });
        })
        .RequierePermiso(Permisos.Usuarios.Editar)
        .WithSummary("Pasar personas de un cargo a otro");
    }

    /// <summary>Para SqlQuery: propiedades con set (EF las llena por nombre).</summary>
    public sealed class PersonaDeCargo
    {
        public Guid CargoId { get; set; }
        public Guid Id { get; set; }
        public string Nombre { get; set; } = "";
        public bool Activo { get; set; }
    }

    private static async Task<IResult> CambiarActivoAsync(Guid id, bool activo, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige primero una empresa." });
        if (new Gestor(ctx).Restringido) return SoloAdministrador();

        var cargo = await db.Cargos.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id && c.EmpresaId == empresaId, ct);
        if (cargo is null) return Results.NotFound(new { error = "Ese cargo no existe." });

        if (!activo)
        {
            var activos = await db.Database.SqlQuery<int>(
                $"SELECT count(*)::int AS \"Value\" FROM usuarios WHERE cargo_id = {id} AND activo").FirstAsync(ct);
            if (activos > 0)
                return Results.Conflict(new
                {
                    error = $"Lo usa{(activos == 1 ? "" : "n")} {activos} persona{(activos == 1 ? "" : "s")} activa{(activos == 1 ? "" : "s")}. " +
                            "Pásala" + (activos == 1 ? "" : "s") + " primero a otro cargo."
                });
        }

        cargo.Activo = activo;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { mensaje = activo ? $"«{cargo.Nombre}» reactivado." : $"«{cargo.Nombre}» desactivado. No se borró nada." });
    }

    private static Task<int> UsosAsync(ContextoPlataforma db, Guid id, CancellationToken ct) =>
        db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM usuarios WHERE cargo_id = {id}").FirstAsync(ct);

    private static async Task<string?> RepetidoAsync(ContextoPlataforma db, Guid empresaId, string nombre, Guid? excepto, CancellationToken ct)
    {
        var clave = nombre.ToLowerInvariant();
        return (await db.Cargos.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.EmpresaId == empresaId && c.Id != excepto)
                .Select(c => c.Nombre).ToListAsync(ct))
            .FirstOrDefault(n => n.Trim().ToLowerInvariant() == clave);
    }

    private static string? Limpio(string? s)
    {
        var t = string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length is >= 1 and <= 80 ? t : null;
    }

    private static IResult SoloAdministrador() => Results.Json(
        new { error = "Ordenar los cargos es del administrador. Al escribir un cargo nuevo en la ficha, se agrega solo." },
        statusCode: StatusCodes.Status403Forbidden);
}