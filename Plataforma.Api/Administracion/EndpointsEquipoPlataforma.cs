using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Invitaciones;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

/// <summary>067: alguien nuevo en el equipo. Permisos null = todo (otro dueño).</summary>
public sealed record PeticionMiembroEquipo(string? Nombre, string? Correo, List<string>? Permisos);
public sealed record PeticionPermisosEquipo(List<string>? Permisos);

/// <summary>
/// 067 — EL EQUIPO DE LA PLATAFORMA CON PERMISOS PARCIALES.
///
/// Soporte, ventas o contabilidad entran al panel de la plataforma con solo una
/// parte de los permisos. Reglas:
///   · Nadie se cambia a sí mismo (ni se desactiva).
///   · Solo un dueño (con todo) toca a otro dueño o crea uno.
///   · Nadie da un permiso que no tiene.
///   · Siempre queda un dueño activo (lo cuida también la base).
/// </summary>
public static class EndpointsEquipoPlataforma
{
    public static void MapearEquipoPlataforma(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/admin/equipo").WithTags("Administración · equipo");

        grupo.MapGet("/", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var gente = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.EsSuperAdmin)
                .OrderBy(u => u.PlataformaPermisos != null).ThenBy(u => u.Nombre)
                .Select(u => new
                {
                    u.Id, u.Nombre, u.Correo, u.Activo, u.UltimoIngresoEn, u.DosfaActivo,
                    duenio = u.PlataformaPermisos == null,
                    permisos = u.PlataformaPermisos,
                    yo = u.Id == ctx.UsuarioId
                })
                .ToListAsync(ct);
            var catalogo = await db.Permisos.AsNoTracking().Where(p => p.Area == "plataforma").ToListAsync(ct);
            return Results.Ok(new
            {
                gente,
                permisos = Permisos.Plataforma.Todos
                    .Select(c => new { codigo = c, descripcion = catalogo.FirstOrDefault(x => x.Codigo == c)?.Descripcion ?? c }),
                soyDuenio = ctx.PlataformaPermisos is null
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.EquipoGestionar)
        .WithSummary("El equipo de la plataforma y sus permisos");

        grupo.MapPost("/", async (PeticionMiembroEquipo p, ContextoPlataforma db, ContextoPeticion ctx,
            ServicioInvitaciones invitaciones, CancellationToken ct) =>
        {
            var correo = (p.Correo ?? "").Trim().ToLowerInvariant();
            var nombre = (p.Nombre ?? "").Trim();
            if (nombre.Length is < 2 or > 120) return Results.BadRequest(new { error = "Escribe su nombre." });
            if (correo.Length is < 5 or > 200 || !correo.Contains('@')) return Results.BadRequest(new { error = "Escribe un correo válido." });
            if (Mal(p.Permisos, ctx) is { } mal) return Results.BadRequest(new { error = mal });
            if (await db.Usuarios.IgnoreQueryFilters().AnyAsync(u => u.Correo.ToLower() == correo, ct))
                return Results.Conflict(new { error = "Ese correo ya tiene una cuenta en la plataforma." });

            var u = new Usuario
            {
                EmpresaId = null, EsSuperAdmin = true, Correo = correo, Nombre = nombre,
                PlataformaPermisos = Limpios(p.Permisos),
                ClaveHash = HashDeClaves.Cifrar(GeneradorTokens.ClaveTemporal(32)),
                ClaveCambioForzado = true
            };
            db.Usuarios.Add(u);
            await db.SaveChangesAsync(ct);
            var inv = await invitaciones.EnviarAsync(u.Id, ct);
            return Results.Ok(new
            {
                u.Id,
                mensaje = inv.Enviado
                    ? $"Listo. Le enviamos a {correo} el enlace para crear su contraseña y su segundo factor."
                    : $"Se creó, pero el correo no salió. Reenvíale el enlace desde la lista.",
                inv.EnlaceDePrueba
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.EquipoGestionar)
        .WithSummary("Sumar a alguien al equipo de la plataforma (con sus permisos)");

        grupo.MapPut("/{id:guid}/permisos", async (Guid id, PeticionPermisosEquipo p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (await Tocable(db, ctx, id, ct) is { } no) return no;
            if (Mal(p.Permisos, ctx) is { } mal) return Results.BadRequest(new { error = mal });
            var u = await db.Usuarios.IgnoreQueryFilters().FirstAsync(x => x.Id == id, ct);
            u.PlataformaPermisos = Limpios(p.Permisos);
            await db.SaveChangesAsync(ct);
            await CortarSesionesAsync(db, id, ct);
            return Results.Ok(new { mensaje = "Permisos guardados. Valen desde su próximo ingreso." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.EquipoGestionar)
        .WithSummary("Cambiar los permisos de alguien del equipo");

        grupo.MapPost("/{id:guid}/desactivar", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (await Tocable(db, ctx, id, ct) is { } no) return no;
            var u = await db.Usuarios.IgnoreQueryFilters().FirstAsync(x => x.Id == id, ct);
            u.Activo = false;
            await db.SaveChangesAsync(ct);
            await CortarSesionesAsync(db, id, ct);
            return Results.Ok(new { mensaje = $"{u.Nombre} ya no entra a la plataforma. Su historial se conserva." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.EquipoGestionar)
        .WithSummary("Quitar a alguien del equipo (se desactiva; nada se borra)");

        grupo.MapPost("/{id:guid}/reactivar", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (await Tocable(db, ctx, id, ct) is { } no) return no;
            var u = await db.Usuarios.IgnoreQueryFilters().FirstAsync(x => x.Id == id, ct);
            u.Activo = true;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = $"{u.Nombre} vuelve a entrar." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.EquipoGestionar)
        .WithSummary("Volver a dejar entrar a alguien del equipo");

        grupo.MapPost("/{id:guid}/reenviar", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx,
            ServicioInvitaciones invitaciones, CancellationToken ct) =>
        {
            if (await Tocable(db, ctx, id, ct) is { } no) return no;
            var inv = await invitaciones.EnviarAsync(id, ct);
            return Results.Ok(new
            {
                mensaje = inv.Enviado ? $"Enlace enviado a {inv.Correo}." : "El correo no salió. Revisa Correos.",
                inv.EnlaceDePrueba
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.EquipoGestionar)
        .WithSummary("Reenviar el enlace de acceso a alguien del equipo");
    }

    /// <summary>null = todo (dueño). Si no, los conocidos, sin repetir.</summary>
    private static string[]? Limpios(List<string>? permisos) =>
        permisos is null ? null : permisos.Where(Permisos.Plataforma.Todos.Contains).Distinct().ToArray();

    /// <summary>Solo un dueño da «todo»; nadie da lo que no tiene.</summary>
    private static string? Mal(List<string>? permisos, ContextoPeticion ctx)
    {
        if (permisos is null)
            return ctx.PlataformaPermisos is null ? null : "Solo un dueño de la plataforma puede dar todos los permisos.";
        if (permisos.Any(x => !Permisos.Plataforma.Todos.Contains(x))) return "Hay un permiso que no existe.";
        if (permisos.FirstOrDefault(x => !ctx.Tiene(x)) is { } falta) return $"No puedes dar un permiso que no tienes ({falta}).";
        return null;
    }

    /// <summary>Nadie se toca a sí mismo; a un dueño solo lo toca otro dueño.</summary>
    private static async Task<IResult?> Tocable(ContextoPlataforma db, ContextoPeticion ctx, Guid id, CancellationToken ct)
    {
        if (id == ctx.UsuarioId) return Results.BadRequest(new { error = "No puedes cambiarte a ti mismo: pídeselo a otro dueño." });
        var u = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == id).Select(x => new { x.EsSuperAdmin, duenio = x.PlataformaPermisos == null }).FirstOrDefaultAsync(ct);
        if (u is not { EsSuperAdmin: true }) return Results.NotFound(new { error = "No es del equipo de la plataforma." });
        if (u.duenio && ctx.PlataformaPermisos is not null)
            return Results.Json(new { error = "A un dueño de la plataforma solo lo cambia otro dueño." }, statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    /// <summary>Lo nuevo vale ya: se cierran sus sesiones abiertas.</summary>
    private static Task CortarSesionesAsync(ContextoPlataforma db, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sesiones SET revocada_en = now(), motivo_revocacion = 'cerradas por un administrador' WHERE usuario_id = {id} AND revocada_en IS NULL", ct);
}