using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

/// <summary>Cuerpo de crear o cambiar un rol propio.</summary>
public sealed record PeticionRolPropio(string? Nombre, string? Descripcion, List<string>? Permisos);

/// <summary>037: los permisos de un rol de la plataforma, ajustado para la empresa.</summary>
public sealed record PeticionAjusteRol(List<string>? Permisos);

/// <summary>
/// 035 — ROLES PROPIOS DE CADA EMPRESA (maqueta aprobada el 18 oct 2026).
///
///   GET  /api/roles/permisos           qué se puede marcar en un rol propio
///   POST /api/roles                    crear un rol propio (o una copia)
///   PUT  /api/roles/{id}               cambiar nombre, descripción y permisos
///   POST /api/roles/{id}/desactivar    deja de dar permisos (no se borra)
///   POST /api/roles/{id}/activar
///
/// 037 — AJUSTAR UN ROL DE LA PLATAFORMA, SOLO PARA ESTA EMPRESA:
///   PUT  /api/roles/{id}/ajuste        los permisos del Facturador, Consulta o
///                                      Contabilidad para esta empresa
///   POST /api/roles/{id}/original      vuelve al de la plataforma
///
/// El ajuste es un rol de la empresa con `basado_en` = el de la plataforma.
/// Las personas siguen asignadas al de la plataforma; la vista
/// permisos_efectivos usa el ajuste de su empresa. Las demás empresas no lo
/// ven (aislamiento) y siguen con el original.
///
/// Los roles de la plataforma (empresa nula) no se tocan desde aquí: el
/// aislamiento de la base ya lo impide, y aquí se dice en palabras. Un rol
/// propio nunca lleva la llave del Administrador (certificado y series,
/// repartir roles, plataforma): lo comprueba esta API y, debajo, un candado
/// en la base (migración 035).
///
/// NADA SE BORRA: los permisos que se quitan se desactivan, y la bitácora
/// cuenta quién cambió qué.
/// </summary>
public static class EndpointsRoles
{
    /// <summary>Lo que solo puede tener el Administrador.</summary>
    private static bool SoloAdministrador(string codigo) =>
        codigo is Permisos.Facturacion.CredencialesGestionar or Permisos.Usuarios.RolesGestionar ||
        codigo.StartsWith("plataforma.", StringComparison.Ordinal);

    public static void MapearRoles(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/roles").WithTags("Usuarios");

        grupo.MapGet("/permisos", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var lista = await db.Permisos.AsNoTracking()
                .Where(p => p.Area != "plataforma")
                .OrderBy(p => p.Area).ThenBy(p => p.Codigo)
                .Select(p => new { p.Codigo, p.Area, p.Descripcion })
                .ToListAsync(ct);

            return Results.Ok(lista.Select(p => new
            {
                p.Codigo, p.Area, p.Descripcion,
                soloAdministrador = SoloAdministrador(p.Codigo)
            }));
        })
        .RequierePermiso(Permisos.Usuarios.RolesGestionar)
        .WithSummary("Los permisos que se pueden poner en un rol propio");


        // Para la pantalla de roles: los de la plataforma y los propios, también
        // los desactivados (para poder volver a activarlos).
        grupo.MapGet("/todos", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var roles = await db.Roles.IgnoreQueryFilters().AsNoTracking()
                .OrderBy(r => r.EmpresaId == null ? 0 : 1).ThenBy(r => r.Nombre)
                .Select(r => new
                {
                    r.Id, r.Nombre, r.Descripcion, r.Activo, r.EmpresaId, r.Editable, r.BasadoEn,
                    permisos = db.RolPermisos.Where(rp => rp.RolId == r.Id && rp.Activo)
                                 .Select(rp => rp.PermisoCodigo).ToList(),
                    usuarios = db.UsuarioRoles.Count(ur => ur.RolId == r.Id && ur.Activo)
                })
                .ToListAsync(ct);

            // 037: el ajuste de esta empresa no sale como un rol más: va dentro
            // del rol de la plataforma al que ajusta.
            var ajustes = roles.Where(r => r.BasadoEn is not null && r.Activo)
                               .GroupBy(r => r.BasadoEn!.Value)
                               .ToDictionary(g => g.Key, g => g.First());

            return Results.Ok(roles
                .Where(r => r.BasadoEn is null && (r.Activo || r.EmpresaId is not null))
                .Select(r =>
                {
                    ajustes.TryGetValue(r.Id, out var aj);
                    return new
                    {
                        r.Id, r.Nombre, r.Descripcion, r.Activo,
                        deSistema = r.EmpresaId == null,
                        esAdministrador = !r.Editable && r.EmpresaId == null,
                        ajustado = aj is not null,
                        permisos = aj?.permisos ?? r.permisos,
                        original = aj is null ? null : r.permisos,
                        r.usuarios
                    };
                }));
        })
        .RequierePermiso(Permisos.Usuarios.RolesGestionar)
        .WithSummary("Roles de la plataforma (con el ajuste de la empresa) y propios");


        // 037: ajustar un rol de la plataforma solo para esta empresa.
        grupo.MapPut("/{id:guid}/ajuste", async (Guid id, PeticionAjusteRol p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId)
                return Results.BadRequest(new { error = "Abre una empresa para ajustar sus roles." });

            var original = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (original is null) return Results.NotFound();
            if (original.EmpresaId is not null)
                return Results.BadRequest(new { error = "Ese rol es de tu empresa: cámbialo con «Editar»." });
            if (!original.Editable)
                return Results.BadRequest(new { error = $"El rol «{original.Nombre}» es fijo: no se ajusta." });

            var permisos = (p.Permisos ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).ToHashSet();
            if (permisos.Count == 0) return Results.BadRequest(new { error = "Marca al menos un permiso." });
            if (permisos.FirstOrDefault(SoloAdministrador) is { } prohibido)
                return Results.BadRequest(new { error = $"«{prohibido}» es solo del Administrador: no puede ir en otro rol." });
            var existentes = await db.Permisos.AsNoTracking()
                .Where(x => permisos.Contains(x.Codigo)).Select(x => x.Codigo).ToListAsync(ct);
            if (permisos.Except(existentes).FirstOrDefault() is { } raro)
                return Results.BadRequest(new { error = $"El permiso «{raro}» no existe." });

            var permisosOriginal = await db.RolPermisos.AsNoTracking()
                .Where(rp => rp.RolId == id).Select(rp => rp.PermisoCodigo).ToListAsync(ct);
            var ajuste = await db.Roles.FirstOrDefaultAsync(r => r.BasadoEn == id && r.EmpresaId == empresaId, ct);

            // Igual que el original: no hace falta ajuste.
            if (permisos.SetEquals(permisosOriginal))
            {
                if (ajuste is not null) { ajuste.Activo = false; await db.SaveChangesAsync(ct); }
                return Results.Ok(new { mensaje = $"«{original.Nombre}» quedó igual que el de la plataforma." });
            }

            if (ajuste is null)
            {
                if (await db.Roles.AnyAsync(r => r.EmpresaId == empresaId && r.BasadoEn == null &&
                                                 r.Nombre.ToLower() == original.Nombre.ToLower(), ct))
                    return Results.Conflict(new { error = $"Tu empresa ya tiene un rol propio llamado «{original.Nombre}». Cámbiale el nombre y vuelve a intentarlo." });

                ajuste = new Rol
                {
                    EmpresaId = empresaId, Nombre = original.Nombre, Descripcion = original.Descripcion,
                    Editable = true, BasadoEn = id
                };
                db.Roles.Add(ajuste);
                foreach (var codigo in permisos)
                    db.RolPermisos.Add(new RolPermiso { Rol = ajuste, PermisoCodigo = codigo });
            }
            else
            {
                var actuales = await db.RolPermisos.IgnoreQueryFilters()
                    .Where(rp => rp.RolId == ajuste.Id).ToListAsync(ct);
                foreach (var rp in actuales)
                    rp.Activo = permisos.Contains(rp.PermisoCodigo);
                foreach (var codigo in permisos.Where(c => actuales.All(a => a.PermisoCodigo != c)))
                    db.RolPermisos.Add(new RolPermiso { RolId = ajuste.Id, PermisoCodigo = codigo });
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                mensaje = $"«{original.Nombre}» ajustado solo para tu empresa. Vale desde la próxima vez que esas personas entren."
            });
        })
        .RequierePermiso(Permisos.Usuarios.RolesGestionar)
        .WithSummary("Ajustar los permisos de un rol de la plataforma, solo para esta empresa");


        grupo.MapPost("/{id:guid}/original", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId)
                return Results.BadRequest(new { error = "Abre una empresa." });
            var ajuste = await db.Roles.FirstOrDefaultAsync(r => r.BasadoEn == id && r.EmpresaId == empresaId, ct);
            if (ajuste is null) return Results.Ok(new { mensaje = "Ya está como el de la plataforma." });

            ajuste.Activo = false;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                mensaje = $"«{ajuste.Nombre}» volvió a ser el de la plataforma. Vale desde la próxima vez que esas personas entren."
            });
        })
        .RequierePermiso(Permisos.Usuarios.RolesGestionar)
        .WithSummary("Quitar el ajuste de un rol de la plataforma (vuelve al original)");


        grupo.MapPost("/", async (PeticionRolPropio p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId)
                return Results.BadRequest(new { error = "Abre una empresa para crear sus roles." });

            var (nombre, descripcion, permisos, problema) = await RevisarAsync(p, db, null, ct);
            if (problema is not null) return Results.BadRequest(new { error = problema });

            var rol = new Rol { EmpresaId = empresaId, Nombre = nombre, Descripcion = descripcion, Editable = true };
            db.Roles.Add(rol);
            foreach (var codigo in permisos)
                db.RolPermisos.Add(new RolPermiso { Rol = rol, PermisoCodigo = codigo });

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { rol.Id, mensaje = $"Rol «{nombre}» creado. Ya lo puedes asignar en Usuarios." });
        })
        .RequierePermiso(Permisos.Usuarios.RolesGestionar)
        .WithSummary("Crear un rol propio de la empresa");


        grupo.MapPut("/{id:guid}", async (Guid id, PeticionRolPropio p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var rol = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (rol is null) return Results.NotFound();
            if (rol.EmpresaId is null || rol.EmpresaId != ctx.EmpresaId)
                return Results.BadRequest(new { error = "Los roles de la plataforma se cambian con «Editar permisos»." });
            if (rol.BasadoEn is not null)
                return Results.BadRequest(new { error = "Es un ajuste de un rol de la plataforma: cámbialo con «Editar permisos»." });

            var (nombre, descripcion, permisos, problema) = await RevisarAsync(p, db, id, ct);
            if (problema is not null) return Results.BadRequest(new { error = problema });

            rol.Nombre = nombre;
            rol.Descripcion = descripcion;

            // Se reactivan los que vuelven y se desactivan los que sobran:
            // nada se borra (y el índice único no admite dos filas iguales).
            var actuales = await db.RolPermisos.IgnoreQueryFilters()
                .Where(rp => rp.RolId == id).ToListAsync(ct);

            foreach (var rp in actuales)
                rp.Activo = permisos.Contains(rp.PermisoCodigo);
            foreach (var codigo in permisos.Where(c => actuales.All(a => a.PermisoCodigo != c)))
                db.RolPermisos.Add(new RolPermiso { RolId = id, PermisoCodigo = codigo });

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = $"Rol «{nombre}» guardado. Vale desde la próxima vez que esas personas entren." });
        })
        .RequierePermiso(Permisos.Usuarios.RolesGestionar)
        .WithSummary("Cambiar un rol propio");


        grupo.MapPost("/{id:guid}/desactivar", (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            CambiarActivoAsync(id, false, db, ctx, ct))
            .RequierePermiso(Permisos.Usuarios.RolesGestionar)
            .WithSummary("Desactivar un rol propio (deja de dar permisos; no se borra)");

        grupo.MapPost("/{id:guid}/activar", (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            CambiarActivoAsync(id, true, db, ctx, ct))
            .RequierePermiso(Permisos.Usuarios.RolesGestionar)
            .WithSummary("Volver a activar un rol propio");
    }


    private static async Task<IResult> CambiarActivoAsync(
        Guid id, bool activo, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        var rol = await db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rol is null) return Results.NotFound();
        if (rol.EmpresaId is null || rol.EmpresaId != ctx.EmpresaId)
            return Results.BadRequest(new { error = "Los roles de la plataforma no se desactivan." });
        if (rol.BasadoEn is not null)
            return Results.BadRequest(new { error = "Es un ajuste de un rol de la plataforma: usa «Volver al original» o «Editar permisos»." });

        if (activo && await db.Roles.AnyAsync(r => r.Id != id && r.EmpresaId == rol.EmpresaId &&
                                                   r.Nombre.ToLower() == rol.Nombre.ToLower(), ct))
            return Results.Conflict(new { error = $"Ya hay otro rol activo llamado «{rol.Nombre}». Cámbiale el nombre a uno de los dos." });

        rol.Activo = activo;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            mensaje = activo
                ? $"Rol «{rol.Nombre}» activo otra vez."
                : $"Rol «{rol.Nombre}» desactivado: quienes lo tenían ya no reciben sus permisos."
        });
    }

    private static async Task<(string Nombre, string? Descripcion, HashSet<string> Permisos, string? Problema)> RevisarAsync(
        PeticionRolPropio p, ContextoPlataforma db, Guid? id, CancellationToken ct)
    {
        var nombre = (p.Nombre ?? "").Trim();
        var descripcion = string.IsNullOrWhiteSpace(p.Descripcion) ? null : p.Descripcion.Trim();
        var permisos = (p.Permisos ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).ToHashSet();

        if (nombre.Length is < 3 or > 60) return (nombre, descripcion, permisos, "El nombre del rol va de 3 a 60 letras.");
        if (descripcion is { Length: > 200 }) descripcion = descripcion[..200];
        if (permisos.Count == 0) return (nombre, descripcion, permisos, "Marca al menos un permiso.");

        if (permisos.FirstOrDefault(SoloAdministrador) is { } prohibido)
            return (nombre, descripcion, permisos, $"«{prohibido}» es solo del Administrador: no puede ir en un rol propio.");

        var existentes = await db.Permisos.AsNoTracking()
            .Where(x => permisos.Contains(x.Codigo)).Select(x => x.Codigo).ToListAsync(ct);
        if (permisos.Except(existentes).FirstOrDefault() is { } raro)
            return (nombre, descripcion, permisos, $"El permiso «{raro}» no existe.");

        // Los de la plataforma también cuentan: no puede haber un «Consulta» propio.
        var lower = nombre.ToLower();
        if (await db.Roles.AnyAsync(r => r.Id != id && r.Nombre.ToLower() == lower, ct))
            return (nombre, descripcion, permisos, $"Ya hay un rol llamado «{nombre}». Elige otro nombre.");

        return (nombre, descripcion, permisos, null);
    }
}