using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Api.Invitaciones;
using Plataforma.Core.Correo;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

public record PeticionAltaUsuario(string Correo, string Nombre, Guid[] Roles);
public record PeticionEditarUsuario(string Correo, string Nombre);
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
            ServicioInvitaciones invitaciones,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(p.Correo))
                return Results.BadRequest(new { error = "Falta el correo." });

            if (!CorreoConForma(p.Correo.Trim()))
                return Results.BadRequest(new { error = "Ese correo no tiene forma de correo." });

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

            var usuario = new Usuario
            {
                EmpresaId = ctx.EmpresaId,
                Correo    = p.Correo.Trim(),
                Nombre    = p.Nombre?.Trim() is { Length: > 0 } n ? n : p.Correo.Trim(),

                // UNA CONTRASEÑA QUE NADIE CONOCE, NI SIQUIERA QUIEN CREA LA
                // CUENTA. La de verdad la elige su dueño al abrir el enlace.
                //
                // La columna no admite nulos, y tampoco convendría: una cuenta
                // sin contraseña es una cuenta a la que se entra con cualquier
                // cosa si algún día alguien se equivoca comparando.
                ClaveHash          = HashDeClaves.Cifrar(GeneradorTokens.ClaveTemporal(32)),
                ClaveCambioForzado = true
            };
            db.Usuarios.Add(usuario);

            foreach (var rolId in rolesValidos)
                db.UsuarioRoles.Add(new UsuarioRol { Usuario = usuario, RolId = rolId });

            await db.SaveChangesAsync(ct);

            var invitacion = await invitaciones.EnviarAsync(usuario.Id, ct);

            return Results.Created($"/api/usuarios/{usuario.Id}", new
            {
                usuario.Id, usuario.Correo, usuario.Nombre,
                invitacionEnviada = invitacion.Enviado,
                invitacion.ExpiraEn,

                // Nulo salvo en desarrollo con el correo apagado. Lo usa el
                // guion de pruebas; en producción no aparece.
                invitacion.EnlaceDePrueba
            });
        })
        .RequierePermiso(Permisos.Usuarios.Crear)
        .WithSummary("Crear un usuario en mi empresa");


        // =====================================================================
        // EDITAR: NOMBRE Y CORREO
        //
        // Existe por un motivo pequeño y muy real: hasta ahora, un correo mal
        // escrito al dar de alta obligaba a entrar a la base de datos a
        // corregirlo a mano.
        //
        // NO TOCA LA CONTRASEÑA, NI LOS ROLES, NI EL SEGUNDO FACTOR. Cada una
        // de esas cosas tiene su propia puerta, con su propio permiso y su
        // propio rastro. Un endpoint que lo cambiara todo junto haría
        // imposible leer la bitácora después: «editó al usuario» no dice nada.
        //
        // Y NO CIERRA SUS SESIONES. El correo no es una credencial —lo que
        // demuestra quién eres son la contraseña y el segundo factor, y
        // ninguno de los dos cambia aquí—, así que echar a alguien de la
        // aplicación por corregirle una letra sería un castigo por un arreglo.
        // =====================================================================
        grupo.MapPut("/{id:guid}", async (
            Guid id, PeticionEditarUsuario p,
            ContextoPlataforma db, ContextoPeticion ctx,
            IServicioCorreo correo,
            CancellationToken ct) =>
        {
            var nombre = p.Nombre?.Trim() ?? "";
            var nuevo  = p.Correo?.Trim() ?? "";

            if (nombre.Length == 0)
                return Results.BadRequest(new { error = "Falta el nombre." });

            if (!CorreoConForma(nuevo))
                return Results.BadRequest(new { error = "Ese correo no tiene forma de correo." });

            // `IgnoreQueryFilters` porque a un usuario desactivado también hay
            // que poder corregirle el correo: puede ser justamente el motivo
            // por el que nunca llegó a entrar.
            var usuario = await db.Usuarios.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(u => u.Id == id, ct);
            if (usuario is null) return Results.NotFound();

            var anterior = usuario.Correo;

            // SIN DISTINGUIR MAYÚSCULAS, igual que el índice único de la base
            // —que es sobre `lower(correo)`— y que el ingreso. Pasar de
            // «Ana@x.pe» a «ana@x.pe» no es un cambio de correo, y tratarlo
            // como tal mandaría un aviso de seguridad por nada.
            var cambiaCorreo = !string.Equals(anterior, nuevo, StringComparison.OrdinalIgnoreCase);

            if (cambiaCorreo)
            {
                // La misma función marcada que en el alta, y por lo mismo: el
                // correo es único en toda la plataforma, pero el aislamiento
                // solo deja ver los de la propia empresa. Preguntarlo con EF
                // diría que está libre aunque lo tenga alguien de otra empresa,
                // y el UPDATE se estrellaría contra el índice único.
                var disponible = await db.Database
                    .SqlQuery<bool>($"SELECT correo_disponible({nuevo}) AS \"Value\"")
                    .FirstAsync(ct);

                if (!disponible)
                    return Results.Conflict(new { error = "Ese correo ya está en uso." });
            }

            usuario.Nombre = nombre;
            usuario.Correo = nuevo;

            await db.SaveChangesAsync(ct);

            // EL AVISO VA A LA DIRECCIÓN ANTIGUA.
            //
            // Cambiar el correo de alguien y pedir después un enlace de acceso
            // es quedarse con su cuenta sin que él se entere nunca. Es el poder
            // que tiene un administrador y no se le puede quitar; lo que sí se
            // puede es avisar al dueño. Va después de guardar: si el servidor
            // de correo está caído, el arreglo ya está hecho.
            //
            // No se manda cuando alguien se corrige a sí mismo: sería mandarle
            // un aviso de seguridad por algo que acaba de hacer él.
            var avisado = false;
            if (cambiaCorreo && id != ctx.UsuarioId)
            {
                var quien = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.Id == ctx.UsuarioId)
                    .Select(u => u.Nombre)
                    .FirstOrDefaultAsync(ct) ?? "El administrador";

                var mensaje = Plantillas.CorreoCambiado(nombre, anterior, nuevo, quien);

                avisado = await correo.EnviarAsync(
                    anterior, mensaje.Asunto, mensaje.Html, mensaje.Texto, ct);
            }

            // No hace falta registrar ningún evento de seguridad: el disparador
            // de la base ya dejó la fila en la bitácora, con el antes, el
            // después y `correo` entre los campos cambiados. Anotarlo dos veces
            // solo consigue que los dos sitios se contradigan algún día.
            return Results.Ok(new
            {
                usuario.Id, usuario.Correo, usuario.Nombre,
                correoCambiado  = cambiaCorreo,
                avisoAlAnterior = avisado,
                mensaje = !cambiaCorreo
                    ? "Datos actualizados."
                    : avisado
                        ? $"Correo actualizado. Se avisó a {anterior}."
                        : $"Correo actualizado, pero no se pudo avisar a {anterior}."
            });
        })
        .RequierePermiso(Permisos.Usuarios.Editar)
        .WithSummary("Editar el nombre y el correo de un usuario");


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
        // REACTIVAR
        //
        // Lleva el mismo permiso que desactivar, a propósito: quien puede
        // cerrar una puerta puede volver a abrirla. Partirlo en dos permisos
        // solo consigue que alguien desactive a un compañero por error y haya
        // que buscar a otra persona para deshacerlo.
        //
        // `IgnoreQueryFilters` es obligatorio aquí: el filtro de EF esconde
        // justamente lo que venimos a buscar.
        // =====================================================================
        grupo.MapPost("/{id:guid}/reactivar", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var usuario = await db.Usuarios.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(u => u.Id == id, ct);
            if (usuario is null) return Results.NotFound();

            usuario.Activo = true;

            // Si estaba bloqueado por intentos fallidos, el bloqueo se levanta
            // también. Reactivar a alguien y que siga sin poder entrar durante
            // media hora no es reactivarlo.
            usuario.IntentosFallidos = 0;
            usuario.BloqueadoHasta   = null;

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Usuario reactivado." });
        })
        .RequierePermiso(Permisos.Usuarios.Desactivar)
        .WithSummary("Reactivar un usuario");


        // =====================================================================
        // REENVIAR EL ENLACE
        //
        // UN SOLO ENDPOINT PARA DOS COSAS QUE PARECEN DISTINTAS: reenviar la
        // invitación a quien nunca llegó a entrar, y restablecer la contraseña
        // de quien la perdió. Por debajo son lo mismo —un enlace de un uso que
        // caduca— y el correo elige su texto según si la persona entró alguna
        // vez o no.
        //
        // Partirlo en dos endpoints obligaría a quien llama a saber esa
        // diferencia, y a equivocarse la mitad de las veces.
        //
        // NO DEVUELVE NINGUNA CONTRASEÑA, porque no genera ninguna: el enlace
        // va al correo de su dueño y la contraseña la elige él. Quien pulsa
        // este botón nunca llega a conocerla, y eso es exactamente lo que se
        // busca.
        //
        // EL SUPER ADMINISTRADOR TAMBIÉN LLEGA AQUÍ, sin endpoint aparte: tiene
        // todos los permisos y el aislamiento le deja ver a cualquiera. Es lo
        // que le permite rescatar a un cliente que perdió su única cuenta.
        // =====================================================================
        grupo.MapPost("/{id:guid}/invitacion", async (
            Guid id,
            ContextoPlataforma db,
            ContextoPeticion ctx,
            ServicioInvitaciones invitaciones,
            CancellationToken ct) =>
        {
            if (id == ctx.UsuarioId)
                return Results.BadRequest(new
                {
                    error = "Para cambiar tu propia contraseña usa el cambio de contraseña, que pide la anterior."
                });

            var usuario = await db.Usuarios.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(u => u.Id == id, ct);
            if (usuario is null) return Results.NotFound();

            if (!usuario.Activo)
                return Results.BadRequest(new
                {
                    error = "Está desactivado. Reactívalo primero, o el enlace no le servirá."
                });

            // EL ENLACE NUEVO BORRA TAMBIÉN SU SEGUNDO FACTOR.
            //
            // Es lo que resuelve el caso real: perdió el teléfono Y los ocho
            // códigos de recuperación. Si el enlace solo cambiara la
            // contraseña, seguiría sin poder pasar del código de seis dígitos.
            //
            // Y por eso el botón lleva confirmación en la pantalla: quien lo
            // pulsa está bajando la barrera de otra persona. Queda en la
            // bitácora, con `secreto_2fa` entre los campos cambiados.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT reiniciar_segundo_factor({id}::uuid)", ct);

            var invitacion = await invitaciones.EnviarAsync(id, ct);

            return Results.Ok(new
            {
                enviado = invitacion.Enviado,
                invitacion.Correo,
                invitacion.ExpiraEn,
                invitacion.EnlaceDePrueba,
                mensaje = invitacion.Enviado
                    ? $"Enlace enviado a {invitacion.Correo}."
                    : "No se pudo enviar el correo. Revisa la configuración e inténtalo otra vez."
            });
        })
        .RequierePermiso(Permisos.Usuarios.Editar)
        .WithSummary("Reenviar la invitación o el enlace de restablecimiento");


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


    /// <summary>
    /// ¿Tiene forma de correo? Nada más.
    ///
    /// DELIBERADAMENTE FLOJO. La expresión regular «que valida correos de
    /// verdad» no existe: el estándar admite cosas que ningún validador acepta
    /// y hay direcciones perfectamente válidas que casi todos rechazan. Lo
    /// único que esto evita es el error de dedo —un espacio, una coma por un
    /// punto, el arroba olvidado—, que es el 99% de lo que pasa.
    ///
    /// Quien comprueba de verdad que la dirección existe es el correo que sale
    /// después: si no llega, no llega, y eso no lo sabe ninguna expresión.
    /// </summary>
    private static bool CorreoConForma(string correo)
    {
        if (correo.Length is < 5 or > 254) return false;
        if (correo.Any(char.IsWhiteSpace)) return false;

        var partes = correo.Split('@');
        if (partes.Length != 2) return false;
        if (partes[0].Length == 0) return false;

        var dominio = partes[1];
        return dominio.Contains('.')
            && !dominio.StartsWith('.') && !dominio.EndsWith('.')
            && !dominio.Contains("..");
    }
}
