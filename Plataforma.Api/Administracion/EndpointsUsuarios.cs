using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

public record PeticionAltaUsuario(string Correo, string Nombre, Guid[] Roles);
public record PeticionRoles(Guid[] Roles);


/// <summary>
/// Usuarios y roles DENTRO de una empresa. Lo usa el administrador del
/// cliente, no el super administrador.
///
/// No hace falta filtrar por empresa en ninguna consulta de este archivo: el
/// Row Level Security ya lo hace. Un `SELECT` aquí solo puede devolver filas
/// de la empresa de quien pregunta, aunque el código se olvide del WHERE.
///
/// Eso no es una excusa para olvidarlo. Es la red que hay debajo.
/// </summary>
public static class EndpointsUsuarios
{
    public static void MapearUsuarios(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/usuarios").WithTags("Usuarios");


        // =====================================================================
        grupo.MapGet("/", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var usuarios = await db.Usuarios
                .IgnoreQueryFilters()          // también los desactivados
                .AsNoTracking()
                .OrderBy(u => u.Nombre)
                .Select(u => new
                {
                    u.Id, u.Correo, u.Nombre, u.Activo,
                    u.DosfaActivo, u.UltimoIngresoEn, u.BloqueadoHasta,
                    roles = db.UsuarioRoles
                              .Where(r => r.UsuarioId == u.Id && r.Activo)
                              .Select(r => new { r.RolId, r.Rol.Nombre })
                              .ToList()
                })
                .ToListAsync(ct);

            return Results.Ok(usuarios);
        })
        .RequierePermiso(Permisos.Usuarios.Ver)
        .WithSummary("Listar los usuarios de mi empresa");


        // =====================================================================
        grupo.MapPost("/", async (
            PeticionAltaUsuario p,
            ContextoPlataforma db,
            ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(p.Correo))
                return Results.BadRequest(new { error = "Falta el correo." });

            // EL CORREO ES ÚNICO EN TODA LA PLATAFORMA, pero el aislamiento
            // solo deja ver los usuarios de la propia empresa.
            //
            // Consultarlo con EF —o con SQL a mano, da igual— diría que está
            // libre aunque lo tenga alguien de otra empresa, y el INSERT se
            // estrellaría contra el índice único con un error ilegible.
            //
            // Escribir la consulta en SQL crudo NO ayuda: el Row Level
            // Security actúa en el motor, por debajo. Y `IgnoreQueryFilters()`
            // tampoco: esos son los filtros de EF, que son otra capa. Hace
            // falta una función marcada, y devuelve solo un booleano.
            var disponible = await db.Database
                .SqlQuery<bool>($"SELECT correo_disponible({p.Correo}) AS \"Value\"")
                .FirstAsync(ct);

            if (!disponible)
                return Results.Conflict(new { error = "Ese correo ya está en uso." });

            var rolesValidos = await db.Roles
                .Where(r => p.Roles.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(ct);

            // Un rol que no aparece aquí es de otra empresa: el aislamiento lo
            // hizo invisible. Decir «no existe» es además la respuesta
            // correcta, porque para esta empresa no existe.
            var invalidos = p.Roles.Except(rolesValidos).ToArray();
            if (invalidos.Length > 0)
                return Results.BadRequest(new { error = "Hay roles que no existen.", roles = invalidos });

            var clave = GeneradorTokens.ClaveTemporal();

            var usuario = new Usuario
            {
                EmpresaId          = ctx.EmpresaId,
                Correo             = p.Correo.Trim(),
                Nombre             = p.Nombre?.Trim() is { Length: > 0 } n ? n : p.Correo.Trim(),
                ClaveHash          = HashDeClaves.Cifrar(clave),
                ClaveCambioForzado = true
            };
            db.Usuarios.Add(usuario);

            foreach (var rolId in rolesValidos)
                db.UsuarioRoles.Add(new UsuarioRol { Usuario = usuario, RolId = rolId });

            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/usuarios/{usuario.Id}", new
            {
                usuario.Id, usuario.Correo, usuario.Nombre,
                claveTemporal = clave,
                aviso = "Esta contraseña no se puede volver a consultar."
            });
        })
        .RequierePermiso(Permisos.Usuarios.Crear)
        .WithSummary("Crear un usuario en mi empresa");


        // =====================================================================
        grupo.MapPut("/{id:guid}/roles", async (
            Guid id, PeticionRoles p,
            ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (usuario is null) return Results.NotFound();

            // NADIE SE QUITA SUS PROPIOS PERMISOS SIN QUERER.
            //
            // Sin esto, el único administrador de una empresa puede dejarse
            // fuera de su propia administración con un clic, y ya no hay forma
            // de volver desde dentro.
            if (id == ctx.UsuarioId)
                return Results.BadRequest(new
                {
                    error = "No puedes cambiar tus propios roles. Que lo haga otro administrador."
                });

            var actuales = await db.UsuarioRoles.Where(r => r.UsuarioId == id).ToListAsync(ct);

            var validos = await db.Roles
                .Where(r => p.Roles.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(ct);

            // Se desactivan los que sobran y se reactivan los que vuelven, en
            // vez de borrar y crear. La bitácora cuenta entonces la historia
            // completa: quién le quitó qué y cuándo se lo devolvieron.
            foreach (var rel in actuales)
                rel.Activo = validos.Contains(rel.RolId);

            foreach (var rolId in validos.Where(r => actuales.All(a => a.RolId != r)))
                db.UsuarioRoles.Add(new UsuarioRol { UsuarioId = id, RolId = rolId });

            await db.SaveChangesAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento({TipoEvento.RolAsignado}::text, true,
                    {id}::uuid, NULL, NULL,
                    jsonb_build_object('roles', {validos.Count}::int))
                """, ct);

            return Results.Ok(new { mensaje = "Roles actualizados." });
        })
        .RequierePermiso(Permisos.Usuarios.RolesGestionar)
        .WithSummary("Cambiar los roles de un usuario");


        // =====================================================================
        grupo.MapPost("/{id:guid}/desactivar", async (
            Guid id, ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            if (id == ctx.UsuarioId)
                return Results.BadRequest(new { error = "No puedes desactivarte a ti mismo." });

            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (usuario is null) return Results.NotFound();

            usuario.Activo = false;

            // Igual que al suspender una empresa: sus sesiones se cortan ahora.
            // Desactivar a alguien y que siga dentro doce horas no es
            // desactivarlo.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE sesiones
                   SET revocada_en = now(), motivo_revocacion = 'usuario desactivado'
                 WHERE usuario_id = {id} AND revocada_en IS NULL
                """, ct);

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Usuario desactivado y sus sesiones cerradas." });
        })
        .RequierePermiso(Permisos.Usuarios.Desactivar)
        .WithSummary("Desactivar un usuario");


        // =====================================================================
        rutas.MapGet("/api/roles", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            // Salen los de sistema (empresa nula) y los propios. Los de otra
            // empresa no aparecen porque el aislamiento no los deja.
            var roles = await db.Roles
                .AsNoTracking()
                .OrderBy(r => r.Nombre)
                .Select(r => new
                {
                    r.Id, r.Nombre, r.Descripcion, r.Editable,
                    deSistema = r.EmpresaId == null,
                    permisos = db.RolPermisos
                                 .Where(rp => rp.RolId == r.Id && rp.Activo)
                                 .Select(rp => rp.PermisoCodigo)
                                 .ToList()
                })
                .ToListAsync(ct);

            return Results.Ok(roles);
        })
        .RequierePermiso(Permisos.Usuarios.Ver)
        .WithTags("Usuarios")
        .WithSummary("Listar los roles disponibles");
    }
}
