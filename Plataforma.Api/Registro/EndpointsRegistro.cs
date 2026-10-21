using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Administracion;
using Plataforma.Api.Invitaciones;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Registro;

/// <param name="Tipo">«negocio» o «profesional».</param>
/// <param name="TitularTipo">«natural» (DNI) o «juridica» (RUC).</param>
/// <param name="Nombre">
/// De la persona que se registra. Con persona natural puede venir vacío: se
/// usa el del titular. Con jurídica es obligatorio (la razón social no es una
/// persona).
/// </param>
/// <param name="Captcha">El token que dio Turnstile en la pantalla.</param>
/// <remarks>
/// SIN ALIAS: no se escribe. Sale solo de lo que va antes de la @ del correo
/// (decisión del 11 de octubre); si está tomado, con número (juan2).
/// </remarks>
public record PeticionRegistro(
    string? Tipo,
    string? TitularTipo,
    string? TitularDocumento,
    string? TitularNombre,
    string? Nombre,
    string? Correo,
    bool AceptaTerminos,
    string? Captcha);

public record PeticionReenviarRegistro(string? Correo);

/// <summary>
/// EL REGISTRO LIBRE (migración 016, fase 1 de cartera-diseno.md).
///
/// SON ENDPOINTS SIN SESIÓN, como el ingreso y la invitación. Por eso todo lo
/// que tocan va por funciones `SECURITY DEFINER` de la base (registrar_cuenta,
/// reenviar_registro, alias_disponible), que hacen lo justo y nada más.
///
/// TRES BARRERAS CONTRA LAS CUENTAS FALSAS EN MASA:
///   1. El captcha (Turnstile), comprobado aquí con la clave secreta.
///   2. El correo: hasta confirmar el enlace, la cuenta no sirve para entrar.
///   3. 10 registros por hora desde la misma red, en la base.
///
/// Y una cuarta, humana: al super administrador le llega un correo con cada
/// registro, y la puede suspender en «Cuentas».
///
/// LO QUE NO HACE: crear la empresa. La cuenta nace sin empresa y sin roles —
/// no ve nada de nadie— y la empresa se agrega en la fase 2.
/// </summary>
public static partial class EndpointsRegistro
{
    /// <summary>Lo que dura el enlace de confirmación.</summary>
    private const int HorasEnlace = 24;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,29}$")]
    private static partial Regex FormatoAlias();

    public static void MapearRegistro(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/registro").WithTags("Registro");


        // ---------------------------------------------------------------------
        // LO QUE LA PANTALLA NECESITA SABER ANTES DE PINTARSE
        // ---------------------------------------------------------------------
        grupo.MapGet("/config", (Captcha captcha) => Results.Ok(new
        {
            abierto            = captcha.Configurado,
            captchaSitio       = captcha.ClaveSitio,
            captchaDePrueba    = captcha.DePrueba,
            longitudMinimaClave = EndpointsInvitacion.LongitudMinimaClave,
            horasEnlace        = HorasEnlace
        }))
        .WithSummary("Configuración pública del registro (captcha, mínimos)");


        // ---------------------------------------------------------------------
        // ¿ESTÁ LIBRE ESTE ALIAS? Y SI NO, CUÁLES SÍ
        //
        // `correo` sirve para sugerir a partir de lo que va antes de la @ cuando
        // todavía no se escribió alias.
        // ---------------------------------------------------------------------
        grupo.MapGet("/alias", async (
            string? valor, string? correo, ContextoPlataforma db, CancellationToken ct) =>
        {
            var base_ = string.IsNullOrWhiteSpace(valor)
                ? (correo ?? "").Split('@')[0]
                : valor;

            var alias = await Escalar(db,
                $"SELECT normalizar_alias({base_}::text) AS \"Value\"", ct) ?? "";

            var formatoValido = FormatoAlias().IsMatch(alias);

            var disponible = formatoValido && await db.Database
                .SqlQuery<bool>($"SELECT alias_disponible({alias}::text) AS \"Value\"")
                .FirstAsync(ct);

            var sugerencias = new List<string>();
            if (!disponible)
            {
                // La primera libre a partir del texto, y dos variantes.
                var raiz = alias.Length >= 3 ? alias : (alias + "xxx")[..3];
                foreach (var candidato in new[] { raiz, raiz + ".pe", raiz + ".fact" })
                {
                    var libre = await Escalar(db,
                        $"SELECT alias_libre({candidato}::text) AS \"Value\"", ct);
                    if (libre is not null && !sugerencias.Contains(libre) && libre != alias)
                        sugerencias.Add(libre);
                }
            }

            return Results.Ok(new
            {
                alias,
                disponible,
                formatoValido,
                problema = formatoValido ? null :
                    "Entre 3 y 30 caracteres: letras, números, punto, guion o guion bajo, empezando por letra o número.",
                sugerencias
            });
        })
        .WithSummary("Comprueba un alias y propone otros si está tomado");


        // ---------------------------------------------------------------------
        // REGISTRARSE
        // ---------------------------------------------------------------------
        grupo.MapPost("/", async (
            PeticionRegistro p,
            HttpContext http,
            ContextoPlataforma db,
            Captcha captcha,
            IServicioCorreo correo,
            OpcionesCorreo opcionesCorreo,
            IHostEnvironment entorno,
            ILoggerFactory logs,
            CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Registro");

            if (!captcha.Configurado)
                return Results.Json(new
                {
                    error = "El registro no está disponible en este momento. Inténtalo más tarde.",
                    codigo = "registro_cerrado"
                }, statusCode: StatusCodes.Status503ServiceUnavailable);

            // --- Lo que llega, limpio y comprobado ----------------------------
            var r = Revisar(p);
            if (r.Problema is not null)
                // `codigo` = «campo_<nombre>»: la pantalla marca ese campo.
                return Results.BadRequest(new { error = r.Problema, codigo = $"campo_{r.Campo}" });

            var ip = http.Connection.RemoteIpAddress?.ToString();

            if (!await captcha.VerificarAsync(p.Captcha, ip, ct))
                return Results.BadRequest(new
                {
                    error = "No pudimos comprobar que no eres un robot. Vuelve a intentarlo.",
                    codigo = "campo_captcha"
                });

            // --- El alias: lo de antes de la @, el primero libre --------------
            var alias = await Escalar(db,
                $"SELECT alias_libre({r.Correo.Split('@')[0]}::text) AS \"Value\"", ct) ?? "";

            // --- La base lo hace todo de una vez ------------------------------
            var (token, hash) = GeneradorTokens.Nuevo();
            var expira = DateTime.UtcNow.AddHours(HorasEnlace);

            var respuesta = await Escalar(db, $"""
                SELECT r.resultado || ':' || coalesce(r.usuario_id::text, '') AS "Value"
                  FROM registrar_cuenta(
                       {r.Tipo}::text, {r.TitularTipo}::text, {r.Documento}::text,
                       {r.Titular}::text, {r.Correo}::text, {r.Nombre}::text,
                       {alias}::text, {hash}, {expira}::timestamptz, {ip}::inet) r
                """, ct) ?? "error:";

            var partes = respuesta.Split(':', 2);
            var resultado = partes[0];

            switch (resultado)
            {
                case "demasiados":
                    return Results.Json(new
                    {
                        error = "Demasiados registros desde tu conexión en la última hora. Prueba más tarde.",
                        codigo = "demasiados"
                    }, statusCode: StatusCodes.Status429TooManyRequests);

                case "correo_ocupado":
                    return Results.Conflict(new
                    {
                        error = "Ese correo ya tiene una cuenta. Si es tuyo, entra con él o recupera tu contraseña.",
                        codigo = "campo_correo"
                    });

                // Solo si otro se registró con el mismo alias en el mismo
                // instante: el siguiente intento ya saca uno libre.
                case "alias_ocupado":
                    return Results.Conflict(new
                    {
                        error = "Coincidió con otro registro en el mismo momento. Vuelve a pulsar «Crear cuenta».",
                        codigo = "alias_ocupado"
                    });

                case not "ok":
                    log.LogError("registrar_cuenta devolvió algo inesperado: {R}", resultado);
                    return Results.Problem("No se pudo completar el registro.");
            }

            var usuarioId = Guid.Parse(partes[1]);
            var nombreCuenta = r.Tipo == "profesional" ? "Cuenta Profesional" : "Cuenta Negocio";

            // --- El enlace, a la misma pantalla que la invitación -------------
            var enlace = $"{opcionesCorreo.UrlBase.TrimEnd('/')}/invitacion/{token}";
            var mensaje = Plantillas.ConfirmarRegistro(r.Nombre, nombreCuenta, enlace, HorasEnlace);
            var envio = await correo.EnviarConMotivoAsync(r.Correo, mensaje.Asunto, mensaje.Html, mensaje.Texto, ct);
            var enviado = envio.Enviado;

            // 038: a la lista del super administrador si no salió.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT anotar_correo({r.Correo}::text, 'confirmar_registro'::text, {usuarioId}::uuid, {enviado}::boolean, {envio.Motivo}::text)", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento('cuenta_registrada'::text, true,
                    {usuarioId}::uuid, NULL, {r.Correo}::text,
                    jsonb_build_object('tipo', {r.Tipo}::text, 'titular', {r.TitularTipo}::text,
                                       'enviado', {enviado}::boolean))
                """, ct);

            // --- Aviso a los super administradores (sin enlace) ---------------
            var supers = await db.Database
                .SqlQuery<string>($"SELECT correo AS \"Value\" FROM correos_super_admin()")
                .ToListAsync(ct);

            var aviso = Plantillas.AvisoRegistroNuevo(
                nombreCuenta, r.Titular, Ocultar(r.Documento), r.Correo, alias);

            foreach (var destino in supers)
                await correo.EnviarAsync(destino, aviso.Asunto, aviso.Html, aviso.Texto, ct);

            return Results.Ok(new
            {
                correo = r.Correo,
                alias,
                enviado,
                horas = HorasEnlace,

                // Solo en desarrollo con el correo apagado, igual que en la
                // invitación: para probar sin buzón. Nulo en cualquier otro caso.
                enlaceDePrueba = entorno.IsDevelopment() && !opcionesCorreo.Habilitado ? enlace : null
            });
        })
        .WithSummary("Crear una cuenta (Negocio o Profesional)");


        // ---------------------------------------------------------------------
        // REENVIAR EL ENLACE
        //
        // RESPONDE LO MISMO EXISTA O NO EL CORREO: si dijera «ese correo no
        // está registrado», serviría para averiguar quién tiene cuenta.
        // ---------------------------------------------------------------------
        grupo.MapPost("/reenviar", async (
            PeticionReenviarRegistro p,
            ContextoPlataforma db,
            IServicioCorreo correo,
            OpcionesCorreo opcionesCorreo,
            IHostEnvironment entorno,
            CancellationToken ct) =>
        {
            var destino = p.Correo?.Trim() ?? "";
            string? enlaceDePrueba = null;

            if (CorreoValido(destino))
            {
                var (token, hash) = GeneradorTokens.Nuevo();
                var expira = DateTime.UtcNow.AddHours(HorasEnlace);

                var fila = await Escalar(db, $"""
                    SELECT r.nombre || '|' || r.tipo AS "Value"
                      FROM reenviar_registro({destino}::text, {hash}, {expira}::timestamptz) r
                    """, ct);

                if (fila is not null)
                {
                    var partes = fila.Split('|', 2);
                    var nombreCuenta = partes[1] == "profesional" ? "Cuenta Profesional" : "Cuenta Negocio";
                    var enlace = $"{opcionesCorreo.UrlBase.TrimEnd('/')}/invitacion/{token}";
                    var mensaje = Plantillas.ConfirmarRegistro(partes[0], nombreCuenta, enlace, HorasEnlace);

                    var envio = await correo.EnviarConMotivoAsync(destino, mensaje.Asunto, mensaje.Html, mensaje.Texto, ct);

                    // 038: a la lista del super administrador si no salió.
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT anotar_correo({destino}::text, 'confirmar_registro'::text, NULL::uuid, {envio.Enviado}::boolean, {envio.Motivo}::text)", ct);

                    if (entorno.IsDevelopment() && !opcionesCorreo.Habilitado)
                        enlaceDePrueba = enlace;
                }
            }

            return Results.Ok(new
            {
                mensaje = "Si ese correo tiene una cuenta sin confirmar, te mandamos un enlace nuevo. " +
                          "Puedes pedir otro cada 60 segundos.",
                enlaceDePrueba
            });
        })
        .WithSummary("Reenviar el enlace de confirmación");
    }


    // =========================================================================
    // LA REVISIÓN DE LO QUE LLEGA
    // =========================================================================

    private sealed record Revisado(
        string Tipo, string TitularTipo, string Documento, string Titular,
        string Nombre, string Correo, string? Problema, string? Campo);

    private static Revisado Revisar(PeticionRegistro p)
    {
        var tipo     = p.Tipo?.Trim().ToLowerInvariant() ?? "";
        var titTipo  = p.TitularTipo?.Trim().ToLowerInvariant() ?? "";
        var doc      = new string((p.TitularDocumento ?? "").Where(char.IsDigit).ToArray());
        var titular  = Limpiar(p.TitularNombre);
        var nombre   = Limpiar(p.Nombre);
        var correo   = p.Correo?.Trim() ?? "";

        Revisado Mal(string problema, string campo) =>
            new(tipo, titTipo, doc, titular, nombre, correo, problema, campo);

        if (tipo is not ("negocio" or "profesional"))
            return Mal("Elige Cuenta Negocio o Cuenta Profesional.", "tipo");

        if (titTipo is not ("natural" or "juridica"))
            return Mal("Elige persona natural o persona jurídica.", "titularTipo");

        if (titTipo == "natural" && doc.Length != 8)
            return Mal("El DNI tiene 8 dígitos.", "titularDocumento");

        if (titTipo == "juridica")
        {
            if (ReglasEmisor.ProblemaRuc(doc) is { } problemaRuc)
                return Mal(problemaRuc, "titularDocumento");

            // Una persona jurídica tiene RUC que empieza por 20.
            if (!doc.StartsWith("20"))
                return Mal("El RUC de una persona jurídica empieza por 20.", "titularDocumento");
        }

        if (titular.Length is < 2 or > 200)
            return Mal(titTipo == "natural"
                ? "Escribe tus nombres y apellidos."
                : "Escribe la razón social.", "titularNombre");

        // La persona: con natural es el mismo titular si no se escribió otra.
        if (nombre.Length == 0 && titTipo == "natural") nombre = titular;

        if (nombre.Length is < 2 or > 120)
            return Mal("Escribe tu nombre (el de la persona que va a entrar).", "nombre");

        if (!CorreoValido(correo))
            return Mal("Ese correo no parece válido.", "correo");

        if (!p.AceptaTerminos)
            return Mal("Para seguir tienes que aceptar los términos de uso y la política de privacidad.",
                       "aceptaTerminos");

        return new(tipo, titTipo, doc, titular, nombre, correo, null, null);
    }

    /// <summary>Sin espacios de sobra; se guarda como se escribió.</summary>
    private static string Limpiar(string? texto) =>
        string.Join(' ', (texto ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool CorreoValido(string correo)
    {
        if (correo.Length is < 6 or > 200 || correo.Contains(' ')) return false;
        try
        {
            var m = new MailAddress(correo);
            return m.Address == correo && correo.Split('@')[1].Contains('.');
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Para el aviso: 4•••••12. Completo, en la plataforma.</summary>
    private static string Ocultar(string doc) =>
        doc.Length <= 4 ? doc : doc[..1] + new string('•', doc.Length - 3) + doc[^2..];

    private static Task<string?> Escalar(ContextoPlataforma db, FormattableString sql, CancellationToken ct) =>
        db.Database.SqlQuery<string>(sql).FirstOrDefaultAsync(ct);
}