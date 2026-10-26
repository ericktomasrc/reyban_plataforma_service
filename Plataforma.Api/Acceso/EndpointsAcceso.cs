using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Invitaciones;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Acceso;

public record PeticionRecuperarAcceso(string? Correo);
public record PeticionNuevaClave(string? Clave);

/// <summary>Lo que pasó al pedir un enlace: si había que mandar uno, y si salió.</summary>
public sealed record ResultadoEnlaceAcceso(bool HabiaQueMandar, bool Enviado, string? Motivo, string? EnlaceDePrueba);

/// <summary>
/// 038 — UN ENLACE PARA RECUPERAR EL ACCESO, POR CORREO.
///
/// Lo usan dos: la persona que lo pide desde el login (sin sesión) y el super
/// administrador que reenvía un correo que no salió. Los dos pasan por
/// `pedir_enlace_acceso`, que decide qué enlace toca:
///
///   · quien nunca entró → el de siempre (contraseña y segundo factor),
///     con el texto del registro o de la invitación;
///   · quien ya entraba → SOLO la contraseña. Su segundo factor no se toca:
///     con el correo solo no se le puede quitar a nadie.
/// </summary>
public sealed class ServicioEnlaceAcceso(
    ContextoPlataforma db,
    IServicioCorreo correo,
    OpcionesCorreo opciones,
    IHostEnvironment entorno)
{
    /// <summary>El de siempre (registro e invitación).</summary>
    public const int HorasPrimeraVez = 24;

    /// <summary>Cambiar la contraseña: una hora, como en todas partes.</summary>
    public const int HorasClave = 1;

    private sealed record Fila(Guid usuarioId, string nombre, string correo, string proposito,
                               bool pendiente, string? cuentaTipo, string empresa, int horas);

    public async Task<ResultadoEnlaceAcceso> EnviarAsync(string correoPedido, CancellationToken ct)
    {
        var (token, hash) = GeneradorTokens.Nuevo();
        var expiraPrimera = DateTime.UtcNow.AddHours(HorasPrimeraVez);
        var expiraClave   = DateTime.UtcNow.AddHours(HorasClave);

        var json = await db.Database.SqlQuery<string>($"""
            SELECT json_build_object(
                       'usuarioId',  r.usuario_id,
                       'nombre',     r.nombre,
                       'correo',     r.correo,
                       'proposito',  r.proposito,
                       'pendiente',  r.pendiente,
                       'cuentaTipo', r.cuenta_tipo,
                       'empresa',    r.empresa,
                       'horas',      CASE WHEN r.proposito = 'restablecer_clave' THEN {HorasClave} ELSE {HorasPrimeraVez} END
                   )::text AS "Value"
              FROM pedir_enlace_acceso({correoPedido}::text, {hash}, {expiraPrimera}::timestamptz, {expiraClave}::timestamptz) r
            """).FirstOrDefaultAsync(ct);

        if (json is null)
            return new ResultadoEnlaceAcceso(false, false, null, null);

        var f = JsonSerializer.Deserialize<Fila>(json)!;

        // EL TOKEN VA EN LA RUTA, NO EN LA CONSULTA: las direcciones con
        // parámetros acaban en los registros de los servidores intermedios.
        var baseUrl = opciones.UrlBase.TrimEnd('/');
        string enlace, tipo;
        CorreoCompuesto mensaje;

        if (f.proposito == "restablecer_clave")
        {
            enlace  = $"{baseUrl}/restablecer/{token}";
            tipo    = "restablecer_clave";
            mensaje = Plantillas.RecuperarClave(f.nombre, enlace, f.horas);
        }
        else if (f.pendiente)
        {
            enlace  = $"{baseUrl}/invitacion/{token}";
            tipo    = "confirmar_registro";
            var cuenta = f.cuentaTipo == "profesional" ? "Cuenta Profesional" : "Cuenta Negocio";
            mensaje = Plantillas.ConfirmarRegistro(f.nombre, cuenta, enlace, f.horas);
        }
        else
        {
            enlace  = $"{baseUrl}/invitacion/{token}";
            tipo    = "invitacion";
            mensaje = Plantillas.Invitacion(f.nombre, f.empresa, enlace, f.horas, "El administrador");
        }

        var r = await correo.EnviarConMotivoAsync(f.correo, mensaje.Asunto, mensaje.Html, mensaje.Texto, ct);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT anotar_correo({f.correo}::text, {tipo}::text, {f.usuarioId}::uuid, {r.Enviado}::boolean, {r.Motivo}::text)", ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT registrar_evento('clave_restablecer_solicitado'::text, {r.Enviado},
                {f.usuarioId}::uuid, NULL, {f.correo}::text,
                jsonb_build_object('origen', 'recuperar_acceso', 'proposito', {f.proposito}::text,
                                   'enviado', {r.Enviado}::boolean))
            """, ct);

        // Solo en desarrollo con el correo apagado: para probar sin buzón.
        var deprueba = entorno.IsDevelopment() && !opciones.Habilitado ? enlace : null;
        return new ResultadoEnlaceAcceso(true, r.Enviado, r.Motivo, deprueba);
    }

    public static bool CorreoValido(string correo)
    {
        if (string.IsNullOrWhiteSpace(correo) || correo.Length > 254) return false;
        try { return new MailAddress(correo).Address.Equals(correo, StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }
}


/// <summary>
/// 038 — Las pantallas «¿Olvidaste tu contraseña o no te llegó el correo?» y
/// «Elige tu contraseña nueva». SIN SESIÓN, como la invitación: van contra
/// funciones SECURITY DEFINER.
/// </summary>
public static class EndpointsAcceso
{
    private const string EnlaceNoSirve =
        "Este enlace ya no sirve: caducó, ya se usó o se pidió otro después. " +
        "Pide uno nuevo en «¿Olvidaste tu contraseña?».";

    public static void MapearRecuperarAcceso(this IEndpointRouteBuilder rutas)
    {
        // ---------------------------------------------------------------------
        // PEDIR EL ENLACE
        //
        // LA RESPUESTA ES SIEMPRE LA MISMA, exista o no el correo, haya pedido
        // hace un minuto o no: si dijera «ese correo no está registrado»,
        // serviría para averiguar quién tiene cuenta.
        // ---------------------------------------------------------------------
        rutas.MapPost("/api/acceso/recuperar", async (
            PeticionRecuperarAcceso p,
            ServicioEnlaceAcceso enlaces,
            CancellationToken ct) =>
        {
            var correo = p.Correo?.Trim() ?? "";
            string? enlaceDePrueba = null;

            if (ServicioEnlaceAcceso.CorreoValido(correo))
            {
                var r = await enlaces.EnviarAsync(correo, ct);
                enlaceDePrueba = r.EnlaceDePrueba;
            }

            return Results.Ok(new
            {
                mensaje = "Si el correo está registrado, ya te llegó el enlace.",
                enlaceDePrueba
            });
        })
        .WithTags("Acceso")
        .WithSummary("Pedir un enlace para recuperar el acceso (sin sesión)");


        // ---------------------------------------------------------------------
        // ¿ESTE ENLACE DE CONTRASEÑA SIRVE, Y DE QUIÉN ES?
        //
        // Solo los enlaces de «cambiar la contraseña». Uno de invitación no
        // sirve aquí, ni uno de aquí en la invitación (ver EndpointsInvitacion).
        // ---------------------------------------------------------------------
        rutas.MapGet("/api/restablecer/{token}", async (
            string token, ContextoPlataforma db, CancellationToken ct) =>
        {
            var fila = await Resolver(db, token, ct);
            if (fila is null)
                return Results.NotFound(new { error = EnlaceNoSirve, codigo = "enlace_invalido" });

            return Results.Ok(new { fila.Correo, fila.Nombre, fila.ExpiraEn });
        })
        .WithTags("Acceso")
        .WithSummary("Comprobar un enlace para cambiar la contraseña");


        // ---------------------------------------------------------------------
        // CAMBIAR LA CONTRASEÑA
        //
        // Solo la contraseña: el segundo factor sigue igual y se pide al entrar.
        // Se cierran las sesiones abiertas y se avisa por correo.
        // ---------------------------------------------------------------------
        rutas.MapPost("/api/restablecer/{token}", async (
            string token,
            PeticionNuevaClave p,
            HttpContext http,
            ContextoPlataforma db,
            IServicioCorreo correo,
            CancellationToken ct) =>
        {
            var clave = p.Clave ?? "";
            if (clave.Length < EndpointsInvitacion.LongitudMinimaClave)
                return Results.BadRequest(new
                {
                    error = $"La contraseña tiene que tener al menos {EndpointsInvitacion.LongitudMinimaClave} caracteres."
                });
            if (clave.Length > 200)
                return Results.BadRequest(new { error = "La contraseña es demasiado larga (máximo 200)." });

            var fila = await Resolver(db, token, ct);
            if (fila is null)
                return Results.NotFound(new { error = EnlaceNoSirve, codigo = "enlace_invalido" });

            var hash      = GeneradorTokens.Hashear(token);
            var claveHash = HashDeClaves.Cifrar(clave);
            var ip        = http.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

            var usuarioId = await db.Database
                .SqlQuery<Guid?>($"SELECT consumir_restablecimiento({hash}, {claveHash}, {ip}::inet) AS \"Value\"")
                .FirstAsync(ct);

            if (usuarioId is null)
                return Results.NotFound(new { error = EnlaceNoSirve, codigo = "enlace_invalido" });

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento('clave_restablecer_usado'::text, true,
                    {usuarioId}::uuid, NULL, NULL,
                    jsonb_build_object('ip', {ip}::text, 'origen', 'recuperar_acceso', 'dosfa', 'sin_cambios'))
                """, ct);

            // El aviso, a su propio correo. Sin sesión no se puede leer al
            // usuario, así que va con lo que ya dio el enlace. No puede impedir
            // nada: la contraseña ya está cambiada.
            try
            {
                var aviso = Plantillas.AvisoDeSeguridad(fila.Nombre,
                    "Se cambió la contraseña de tu cuenta con un enlace enviado a tu correo.",
                    "Tus sesiones abiertas se cerraron. Tu segundo factor no cambió: sigue siendo el mismo teléfono.");
                await correo.EnviarAsync(fila.Correo, aviso.Asunto, aviso.Html, aviso.Texto, ct);
            }
            catch { /* el aviso nunca tumba el cambio */ }

            return Results.Ok(new { mensaje = "Listo: tu contraseña cambió. Entra con ella y con el código de tu teléfono." });
        })
        .WithTags("Acceso")
        .WithSummary("Cambiar la contraseña con el enlace del correo (solo la contraseña)");
    }

    private static async Task<InvitacionResuelta?> Resolver(ContextoPlataforma db, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;

        var hash = GeneradorTokens.Hashear(token);
        var fila = await db.InvitacionesResueltas
            .FromSqlInterpolated($"SELECT * FROM resolver_invitacion({hash})")
            .FirstOrDefaultAsync(ct);

        return fila is null
            || fila.Proposito != "restablecer_clave"
            || fila.UsadoEn   is not null
            || fila.AnuladoEn is not null
            || fila.ExpiraEn  <= DateTime.UtcNow
            || !fila.UsuarioActivo
            || !fila.EmpresaOperativa
            ? null : fila;
    }
}


/// <summary>
/// 038 — Administración › Correos: el estado del correo y los correos de acceso
/// que no salieron. SOLO EL SUPER ADMINISTRADOR.
/// </summary>
public static class EndpointsCorreosAdmin
{
    public static void MapearCorreosAdmin(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/admin/correos").WithTags("Administración");

        grupo.MapGet("/", async (ContextoPlataforma db, OpcionesCorreo opciones, CancellationToken ct) =>
        {
            var lista = await db.Database
                .SqlQuery<string>($"SELECT correos_fallidos_abiertos() AS \"Value\"")
                .FirstOrDefaultAsync(ct) ?? "[]";

            return Results.Ok(new
            {
                estado   = Estado(opciones),
                fallidos = JsonDocument.Parse(lista).RootElement
            });
        })
        .SoloSuperAdmin(global::Plataforma.Core.Dominio.Permisos.Plataforma.AccesosGestionar)
        .WithSummary("Estado del correo y correos de acceso que no salieron");

        grupo.MapPost("/probar", async (IServicioCorreo correo, OpcionesCorreo opciones, CancellationToken ct) =>
        {
            var r = await correo.ProbarAsync(ct);
            return Results.Ok(new { r.Enviado, r.Motivo, estado = Estado(opciones) });
        })
        .SoloSuperAdmin(global::Plataforma.Core.Dominio.Permisos.Plataforma.AccesosGestionar)
        .WithSummary("Probar el correo ahora (conecta y se identifica, no envía nada)");

        grupo.MapPost("/{id:guid}/reenviar", async (
            Guid id, ContextoPlataforma db, ServicioEnlaceAcceso enlaces, CancellationToken ct) =>
        {
            var destino = await db.Database
                .SqlQuery<string>($"SELECT correo_fallido_destino({id}) AS \"Value\"")
                .FirstOrDefaultAsync(ct);

            if (destino is null)
                return Results.NotFound(new { error = "Ese correo ya no está en la lista." });

            var r = await enlaces.EnviarAsync(destino, ct);
            if (!r.HabiaQueMandar)
                return Results.Conflict(new
                {
                    error = "No se mandó: se envió un enlace a esa persona hace menos de un minuto, " +
                            "ya pidió cinco en la última hora, o la persona o su empresa ya no están activas."
                });

            return r.Enviado
                ? Results.Ok(new { mensaje = $"Enlace nuevo enviado a {destino}." })
                : Results.UnprocessableEntity(new { error = $"Tampoco salió esta vez: {r.Motivo}." });
        })
        .SoloSuperAdmin(global::Plataforma.Core.Dominio.Permisos.Plataforma.AccesosGestionar)
        .WithSummary("Reenviar un correo de acceso que no salió (con un enlace nuevo)");
    }

    private static object Estado(OpcionesCorreo opciones)
    {
        var (ok, motivo, desde, ultima) = EstadoCorreo.Leer();
        return new
        {
            habilitado = opciones.Habilitado,
            cuenta     = opciones.Usuario,
            ok,
            motivo,
            fallaDesde = desde,
            ultimaVez  = ultima
        };
    }
}