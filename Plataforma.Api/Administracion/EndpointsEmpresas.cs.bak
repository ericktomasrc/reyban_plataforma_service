using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Api.Invitaciones;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

public record PeticionAltaEmpresa(
    string Ruc,
    string RazonSocial,
    string? NombreComercial,
    string? Direccion,
    string CorreoAdministrador,
    string NombreAdministrador,
    string[]? Modulos,

    // Horas que durarán los enlaces de esta empresa. Si no va, la base pone 24.
    int? HorasInvitacion);

public record PeticionHorasInvitacion(int Horas);

public record PeticionSuspension(string Motivo);


/// <summary>
/// El panel del super administrador: dar de alta clientes y decidir qué
/// módulos tienen.
///
/// TODO LO DE AQUÍ LLEVA SoloSuperAdmin. Un administrador de empresa no entra
/// ni a mirar: si pudiera, tarde o temprano se activaría un módulo que no
/// paga, o vería la lista de tus otros clientes.
/// </summary>
public static class EndpointsEmpresas
{
    public static void MapearEmpresas(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/admin/empresas").WithTags("Administración");


        // =====================================================================
        // EL ALTA DE UN CLIENTE, ENTERA, EN UNA SOLA LLAMADA
        //
        // Crea la empresa, le activa los módulos contratados y le deja un
        // administrador con contraseña temporal.
        //
        // Va junto porque una empresa sin administrador no sirve para nada, y
        // hacerlo en tres llamadas deja tres formas de quedarse a medias: una
        // empresa sin nadie dentro, un usuario sin módulos, un módulo activo
        // en una empresa vacía.
        //
        // Todo pasa dentro de la transacción que abrió el middleware: o sale
        // entero, o no sale.
        // =====================================================================
        grupo.MapPost("/", async (
            PeticionAltaEmpresa p,
            ContextoPlataforma db,
            ContextoPeticion ctx,
            ServicioInvitaciones invitaciones,
            CancellationToken ct) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(p.Ruc ?? "", @"^\d{11}$"))
                return Results.BadRequest(new { error = "El RUC tiene que ser de 11 dígitos." });

            if (string.IsNullOrWhiteSpace(p.RazonSocial))
                return Results.BadRequest(new { error = "Falta la razón social." });

            if (string.IsNullOrWhiteSpace(p.CorreoAdministrador))
                return Results.BadRequest(new { error = "Falta el correo del administrador." });

            if (await db.Empresas.IgnoreQueryFilters().AnyAsync(e => e.Ruc == p.Ruc, ct))
                return Results.Conflict(new { error = $"Ya existe una empresa con el RUC {p.Ruc}." });

            // Por la función y no con EF: el correo es único globalmente y el
            // aislamiento solo deja ver la propia empresa. Aquí quien llama es
            // el super administrador y lo vería igual, pero se usa la misma
            // puerta que el resto del sistema para que haya una sola forma de
            // hacerlo — y no dos que se comporten distinto.
            var disponible = await db.Database
                .SqlQuery<bool>($"SELECT correo_disponible({p.CorreoAdministrador}) AS \"Value\"")
                .FirstAsync(ct);

            if (!disponible)
                return Results.Conflict(new { error = $"Ya hay un usuario con el correo {p.CorreoAdministrador}." });

            var empresa = new Empresa
            {
                Ruc             = p.Ruc!,
                RazonSocial     = p.RazonSocial.Trim(),
                NombreComercial = p.NombreComercial?.Trim(),
                Direccion       = p.Direccion?.Trim(),

                // Si no lo mandan, la base pone 24. El rango lo comprueba
                // también un CHECK: esto es comodidad, no la validación.
                HorasInvitacion = p.HorasInvitacion is >= 1 and <= 168
                                  ? p.HorasInvitacion.Value
                                  : 24
            };
            db.Empresas.Add(empresa);

            foreach (var codigo in p.Modulos ?? [])
            {
                if (!await db.Modulos.AnyAsync(m => m.Codigo == codigo && m.Disponible, ct))
                    return Results.BadRequest(new { error = $"El módulo '{codigo}' no existe." });

                db.EmpresaModulos.Add(new EmpresaModulo
                {
                    Empresa       = empresa,
                    ModuloCodigo  = codigo,
                    ContratadoEn  = DateTime.UtcNow
                });
            }

            var admin = new Usuario
            {
                Empresa = empresa,
                Correo  = p.CorreoAdministrador.Trim(),
                Nombre  = p.NombreAdministrador?.Trim() is { Length: > 0 } n ? n : "Administrador",

                // Una contraseña que no conoce nadie. La suya la elige él al
                // abrir el enlace que le llega por correo.
                ClaveHash          = HashDeClaves.Cifrar(GeneradorTokens.ClaveTemporal(32)),
                ClaveCambioForzado = true
            };
            db.Usuarios.Add(admin);

            db.UsuarioRoles.Add(new UsuarioRol
            {
                Usuario = admin,
                RolId   = RolesDeSistema.Administrador
            });

            await db.SaveChangesAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento('otro'::text, true, NULL, {empresa.Id}::uuid, NULL,
                    jsonb_build_object('accion','alta_empresa','ruc',{p.Ruc}::text))
                """, ct);

            // EL ENLACE SE MANDA DESPUÉS DE GUARDAR, no antes. Si fallara el
            // correo, la empresa ya existe y el enlace se reenvía con un clic;
            // al revés, se habría mandado una invitación a una cuenta que la
            // transacción podría no haber llegado a crear.
            var invitacion = await invitaciones.EnviarAsync(admin.Id, ct);

            return Results.Created($"/api/admin/empresas/{empresa.Id}", new
            {
                empresa = new { empresa.Id, empresa.Ruc, empresa.RazonSocial },
                administrador = new
                {
                    admin.Id,
                    admin.Correo,
                    admin.Nombre,
                    invitacionEnviada = invitacion.Enviado,
                    invitacion.ExpiraEn,
                    invitacion.EnlaceDePrueba
                }
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Dar de alta una empresa con su administrador");


        // =====================================================================
        grupo.MapGet("/", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            // IgnoreQueryFilters: el super administrador tiene que ver también
            // las empresas desactivadas. Son las que va a querer mirar cuando
            // alguien pregunte por qué no puede entrar.
            var empresas = await db.Empresas
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderBy(e => e.RazonSocial)
                .Select(e => new
                {
                    e.Id, e.Ruc, e.RazonSocial, e.Activo, e.SuspendidaEn, e.MotivoSuspension,
                    e.HorasInvitacion,
                    usuarios = db.Usuarios.Count(u => u.EmpresaId == e.Id && u.Activo),
                    modulos  = db.EmpresaModulos
                                 .Where(m => m.EmpresaId == e.Id && m.Activo)
                                 .Select(m => m.ModuloCodigo)
                                 .ToList()
                })
                .ToListAsync(ct);

            return Results.Ok(empresas);
        })
        .SoloSuperAdmin()
        .WithSummary("Listar empresas");


        // =====================================================================
        // LOS USUARIOS DE UNA EMPRESA
        //
        // EL ÚNICO SITIO DE LA PLATAFORMA DONDE ALGUIEN LEE LOS USUARIOS DE OTRA
        // EMPRESA, y existe para una sola cosa: elegir a quién suplantar cuando
        // un cliente llama con un problema.
        //
        // No es la pantalla de gestión de usuarios —esa es `/api/usuarios`, y es
        // del administrador del cliente, no de aquí. Esta devuelve lo justo para
        // reconocer a una persona en una lista: nombre, correo, roles y estado.
        // Nada de fechas de contraseña ni de sesiones.
        //
        // El `Where` por empresa es de verdad necesario aquí, al contrario que
        // en el resto del sistema: el aislamiento no recorta nada para un super
        // administrador, así que sin él saldrían los usuarios de todos.
        // =====================================================================
        grupo.MapGet("/{id:guid}/usuarios", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.Id == id)
                .Select(e => new { e.RazonSocial, e.Activo, e.SuspendidaEn })
                .FirstOrDefaultAsync(ct);

            if (empresa is null) return Results.NotFound();

            var usuarios = await db.Usuarios
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.EmpresaId == id)
                .OrderByDescending(u => u.Activo)
                .ThenBy(u => u.Nombre)
                .Select(u => new
                {
                    u.Id, u.Nombre, u.Correo, u.Activo,
                    u.DosfaActivo, u.UltimoIngresoEn,
                    roles = db.UsuarioRoles
                              .Where(r => r.UsuarioId == u.Id && r.Activo)
                              .Select(r => r.Rol.Nombre)
                              .ToList()
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                empresa = empresa.RazonSocial,

                // Suplantando a alguien de una empresa suspendida no se vería
                // nada útil: el aislamiento la esconde entera. La pantalla lo
                // usa para decirlo antes de que nadie lo intente.
                operativa = empresa.Activo && empresa.SuspendidaEn is null,
                usuarios
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Listar los usuarios de una empresa");


        // =====================================================================
        grupo.MapPost("/{id:guid}/suspender", async (
            Guid id, PeticionSuspension p,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var empresa = await db.Empresas.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (empresa is null) return Results.NotFound();

            empresa.SuspendidaEn     = DateTime.UtcNow;
            empresa.MotivoSuspension = p.Motivo;

            // Las sesiones abiertas se cortan ahora, no cuando caduquen. Una
            // suspensión que tarda doce horas en notarse no es una suspensión.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE sesiones
                   SET revocada_en = now(), motivo_revocacion = 'empresa suspendida'
                 WHERE empresa_id = {id} AND revocada_en IS NULL
                """, ct);

            await db.SaveChangesAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento({TipoEvento.EmpresaSuspendida}::text, true,
                    NULL, {id}::uuid, NULL,
                    jsonb_build_object('motivo', {p.Motivo}::text))
                """, ct);

            return Results.Ok(new { mensaje = "Empresa suspendida y sesiones cerradas." });
        })
        .SoloSuperAdmin()
        .WithSummary("Suspender una empresa");


        // =====================================================================
        grupo.MapPost("/{id:guid}/reactivar", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var empresa = await db.Empresas.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (empresa is null) return Results.NotFound();

            empresa.SuspendidaEn     = null;
            empresa.MotivoSuspension = null;
            empresa.Activo           = true;

            await db.SaveChangesAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento({TipoEvento.EmpresaReactivada}::text, true,
                                        NULL, {id}::uuid, NULL, NULL)
                """, ct);

            return Results.Ok(new { mensaje = "Empresa reactivada." });
        })
        .SoloSuperAdmin()
        .WithSummary("Reactivar una empresa");


        // =====================================================================
        // EL PLAZO DE LOS ENLACES, POR EMPRESA
        //
        // Una oficina que revisa el correo cada mañana necesita más margen que
        // otra donde el administrador llama por teléfono antes de crear la
        // cuenta. Por eso no es un número global.
        //
        // El rango lo comprueban los dos lados: aquí para dar un mensaje que
        // se entienda, y un CHECK en la base para que siga siendo cierto aunque
        // alguien llame al endpoint desde fuera de la aplicación.
        // =====================================================================
        grupo.MapPut("/{id:guid}/invitacion", async (
            Guid id, PeticionHorasInvitacion p,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            if (p.Horas is < 1 or > 168)
                return Results.BadRequest(new
                {
                    error = "El plazo tiene que estar entre 1 hora y 168 (siete días)."
                });

            var empresa = await db.Empresas.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (empresa is null) return Results.NotFound();

            empresa.HorasInvitacion = p.Horas;
            await db.SaveChangesAsync(ct);

            // Los enlaces ya emitidos conservan su fecha: se calculó al
            // crearlos. Cambiar el plazo no alarga ni acorta lo que ya salió.
            return Results.Ok(new { mensaje = $"Los enlaces durarán {p.Horas} horas." });
        })
        .SoloSuperAdmin()
        .WithSummary("Cambiar el plazo de los enlaces de una empresa");


        // =====================================================================
        // ACTIVAR Y DESACTIVAR MÓDULOS
        //
        // Desactivar no borra la fila: la deja con `activo = false`. Así, el
        // día que vuelvan a contratarlo, su configuración sigue ahí — y queda
        // escrito en la bitácora quién lo quitó y cuándo.
        // =====================================================================
        grupo.MapPut("/{id:guid}/modulos/{codigo}", async (
            Guid id, string codigo,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            if (!await db.Empresas.IgnoreQueryFilters().AnyAsync(e => e.Id == id, ct))
                return Results.NotFound(new { error = "No existe esa empresa." });

            if (!await db.Modulos.AnyAsync(m => m.Codigo == codigo && m.Disponible, ct))
                return Results.NotFound(new { error = $"No existe el módulo '{codigo}'." });

            var existente = await db.EmpresaModulos
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.EmpresaId == id && m.ModuloCodigo == codigo, ct);

            if (existente is null)
            {
                db.EmpresaModulos.Add(new EmpresaModulo
                {
                    EmpresaId    = id,
                    ModuloCodigo = codigo,
                    ContratadoEn = DateTime.UtcNow
                });
            }
            else
            {
                existente.Activo  = true;
                existente.VenceEn = null;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = $"Módulo '{codigo}' activado." });
        })
        .SoloSuperAdmin()
        .WithSummary("Activar un módulo para una empresa");


        // =====================================================================
        grupo.MapDelete("/{id:guid}/modulos/{codigo}", async (
            Guid id, string codigo,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var modulo = await db.EmpresaModulos
                .FirstOrDefaultAsync(m => m.EmpresaId == id && m.ModuloCodigo == codigo, ct);

            if (modulo is null) return Results.NotFound();

            modulo.Activo = false;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { mensaje = $"Módulo '{codigo}' desactivado." });
        })
        .SoloSuperAdmin()
        .WithSummary("Quitar un módulo a una empresa");
    }
}


/// <summary>
/// Los identificadores de los tres roles que crea la migración 006.
///
/// Están fijos en la migración y por eso pueden estar fijos aquí: son los
/// mismos en cualquier base, la de desarrollo y la de producción.
/// </summary>
public static class RolesDeSistema
{
    public static readonly Guid Administrador = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Facturador    = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Consulta      = Guid.Parse("33333333-3333-3333-3333-333333333333");
}
