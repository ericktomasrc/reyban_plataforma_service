using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Administracion;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Registro;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Recuperacion;

public record PeticionPedirRecuperacion(string? Ruc, string? Captcha);

public record PeticionRecuperarNueva(string? Nombre, bool AceptaTerminos);


/// <summary>
/// RECUPERAR LA EMPRESA (fase 4a, migración 021). Lienzo «Fase 4 — Traspasos».
///
///   POST /api/recuperar/pedir                 «Recupérala»: RUC + captcha (sin sesión)
///   GET  /api/recuperar/{token}               lo que enseña la página del enlace
///   POST /api/recuperar/{token}/cuenta-nueva  crea la Cuenta Negocio y pasa la empresa
///   POST /api/recuperar/{token}/a-mi-cuenta   la pasa a la cuenta con la que entré
///
/// EL ENLACE VA SOLO AL CORREO DEL TITULAR, el que se guardó al dar de alta la
/// empresa. Nadie puede pedir que vaya a otro: «Recupérala» solo recibe el RUC,
/// y responde lo mismo exista o no, esté o no en pausa.
///
/// Todo lo decide la base (funciones SECURITY DEFINER de la 021); aquí se
/// generan los tokens, se escriben los correos y se traducen los códigos.
/// </summary>
public static class EndpointsRecuperacion
{
    /// <summary>Lo que vale un enlace de recuperación: 7 días.</summary>
    public const int HorasEnlace = 168;

    /// <summary>Lo que vale el enlace para elegir contraseña, después.</summary>
    private const int HorasInvitacion = 24;

    private const string RespuestaPedido =
        "Si ese RUC está en pausa, le enviamos un enlace al correo del titular que tenemos registrado. " +
        "Vale 7 días. Revisa también el correo no deseado.";

    public static void MapearRecuperacion(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/recuperar").WithTags("Recuperar empresa");


        // =====================================================================
        // «¿TU EMPRESA ESTÁ EN PAUSA? RECUPÉRALA»
        // =====================================================================
        grupo.MapPost("/pedir", async (
            PeticionPedirRecuperacion p, HttpContext http, ContextoPlataforma db, Captcha captcha,
            IServicioCorreo correo, OpcionesCorreo opciones, IHostEnvironment entorno,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var ruc = new string((p.Ruc ?? "").Where(char.IsDigit).ToArray());
            if (ReglasEmisor.ProblemaRuc(ruc) is { } problema)
                return Results.BadRequest(new { error = problema, codigo = "campo_ruc" });

            var ip = http.Connection.RemoteIpAddress?.ToString();

            if (captcha.Configurado && !await captcha.VerificarAsync(p.Captcha, ip, ct))
                return Results.BadRequest(new
                {
                    error = "No pudimos comprobar que no eres un robot. Vuelve a intentarlo.",
                    codigo = "campo_captcha"
                });

            var (token, hash) = GeneradorTokens.Nuevo();
            var expira = DateTime.UtcNow.AddHours(HorasEnlace);

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('correo', x.correo, 'nombre', x.nombre, 'ruc', x.ruc)::text AS "Value"
                  FROM pedir_recuperacion({ruc}::text, {hash}, {expira}::timestamptz, {ip}::inet) x
                """).FirstOrDefaultAsync(ct);

            string? enlaceDePrueba = null;

            if (fila is not null)
            {
                var (destino, nombre, rucEmpresa) = Leer(fila);
                var enlace = Enlace(opciones, token);
                var m = Plantillas.RecuperarEmpresa(nombre, rucEmpresa, enlace, HorasEnlace);

                try { await correo.EnviarAsync(destino, m.Asunto, m.Html, m.Texto, ct); }
                catch (Exception e) { logs.CreateLogger("Recuperar").LogError(e, "No salió el enlace de recuperación del RUC {Ruc}.", ruc); }

                if (entorno.IsDevelopment() && !opciones.Habilitado) enlaceDePrueba = enlace;
            }

            // LA MISMA RESPUESTA SIEMPRE: si cambiara, serviría para averiguar
            // qué RUC usa la plataforma y cuál está en pausa.
            return Results.Ok(new { mensaje = RespuestaPedido, enlaceDePrueba });
        })
        .WithSummary("Pedir un enlace para recuperar una empresa en pausa (va al correo del titular)");


        // =====================================================================
        // LO QUE ENSEÑA LA PÁGINA DEL ENLACE
        // =====================================================================
        grupo.MapGet("/{token}", async (string token, ContextoPlataforma db, CancellationToken ct) =>
        {
            var hash = GeneradorTokens.Hashear(token);

            var json = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object(
                         'estado', x.estado, 'nombre', x.nombre, 'ruc', x.ruc, 'correo', x.correo,
                         'pausadaEn', x.pausada_en, 'motivo', x.motivo_pausa, 'quien', x.quien,
                         'comprobantes', x.comprobantes, 'clientes', x.clientes, 'series', x.series,
                         'expiraEn', x.expira_en, 'correoConCuenta', x.correo_con_cuenta)::text AS "Value"
                  FROM resolver_recuperacion({hash}) x
                """).FirstOrDefaultAsync(ct);

            return Results.Content(json ?? """{"estado":"no_existe"}""", "application/json");
        })
        .WithSummary("Ver la empresa que se va a recuperar con este enlace");


        // =====================================================================
        // RECUPERAR CREANDO LA CUENTA (sin sesión)
        //
        // Devuelve la dirección de la pantalla de la invitación: ahí elige su
        // contraseña y activa su segundo factor, como cualquier cuenta nueva.
        // =====================================================================
        grupo.MapPost("/{token}/cuenta-nueva", async (
            string token, PeticionRecuperarNueva p, HttpContext http, ContextoPlataforma db,
            IServicioCorreo correo, IServiceScopeFactory ambitos, ILoggerFactory logs, CancellationToken ct) =>
        {
            var nombre = string.Join(' ', (p.Nombre ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (nombre.Length is < 2 or > 120)
                return Results.BadRequest(new { error = "Escribe tu nombre y apellido.", codigo = "campo_nombre" });
            if (!p.AceptaTerminos)
                return Results.BadRequest(new
                {
                    error = "Para seguir tienes que aceptar los términos de uso y la política de privacidad.",
                    codigo = "campo_aceptaTerminos"
                });

            var hash = GeneradorTokens.Hashear(token);
            var empresaId = await EmpresaDelEnlaceAsync(db, hash, ct);
            var (tokenInv, hashInv) = GeneradorTokens.Nuevo();
            var expiraInv = DateTime.UtcNow.AddHours(HorasInvitacion);
            var ip = http.Connection.RemoteIpAddress?.ToString();

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('r', x.resultado, 'correo', x.correo,
                         'avisoCorreo', x.aviso_correo, 'avisoNombre', x.aviso_nombre, 'empresa', x.empresa)::text AS "Value"
                  FROM recuperar_con_cuenta_nueva({hash}, {nombre}::text, {hashInv},
                                                  {expiraInv}::timestamptz, {ip}::inet) x
                """).FirstOrDefaultAsync(ct);

            using var doc = JsonDocument.Parse(fila ?? "{}");
            var raiz = doc.RootElement;
            var r = Texto(raiz, "r");

            if (r != "ok") return Codigo(r);

            // FASE 4c: la clave de la empresa vuelve a poder emitir en el servicio.
            if (empresaId is { } e1) await PausaEnServicio.AplicarAsync(ambitos, e1, false, null, logs.CreateLogger("Recuperar"), ct);

            await AvisarCuentaAnteriorAsync(raiz, correo, logs, ct);

            return Results.Ok(new
            {
                mensaje = $"{Texto(raiz, "empresa")} ya es tuya. Ahora elige tu contraseña.",
                invitacion = $"/invitacion/{tokenInv}"
            });
        })
        .WithSummary("Recuperar la empresa creando una Cuenta Negocio con el correo del titular");


        // =====================================================================
        // RECUPERAR A LA CUENTA CON LA QUE ENTRÉ
        // =====================================================================
        grupo.MapPost("/{token}/a-mi-cuenta", async (
            string token, ContextoPlataforma db, IServicioCorreo correo, IServiceScopeFactory ambitos,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var hash = GeneradorTokens.Hashear(token);
            var empresaId = await EmpresaDelEnlaceAsync(db, hash, ct);

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('r', x.resultado, 'avisoCorreo', x.aviso_correo,
                         'avisoNombre', x.aviso_nombre, 'empresa', x.empresa)::text AS "Value"
                  FROM recuperar_a_mi_cuenta({hash}) x
                """).FirstOrDefaultAsync(ct);

            using var doc = JsonDocument.Parse(fila ?? "{}");
            var raiz = doc.RootElement;
            var r = Texto(raiz, "r");

            if (r != "ok") return Codigo(r);

            if (empresaId is { } e2) await PausaEnServicio.AplicarAsync(ambitos, e2, false, null, logs.CreateLogger("Recuperar"), ct);

            await AvisarCuentaAnteriorAsync(raiz, correo, logs, ct);

            return Results.Ok(new { mensaje = $"{Texto(raiz, "empresa")} ya está en tu cuenta. La dejamos abierta." });
        })
        .RequiereSesion()
        .WithSummary("Recuperar la empresa a la cuenta con la que entré (mismo correo del titular)");
    }


    // =========================================================================

    public static string Enlace(OpcionesCorreo opciones, string token) =>
        $"{opciones.UrlBase.TrimEnd('/')}/recuperar/empresa/{token}";

    private static IResult Codigo(string? r) => r switch
    {
        "vencido"           => Results.Json(new { error = "Este enlace venció. Pide otro con «Recupérala» en la pantalla de ingreso.", codigo = r }, statusCode: StatusCodes.Status410Gone),
        "usado"             => Results.Json(new { error = "Este enlace ya se usó. Si la empresa ya es tuya, entra con tu correo.", codigo = r }, statusCode: StatusCodes.Status410Gone),
        "no_pausada"        => Results.Conflict(new { error = "Esa empresa ya no está en pausa.", codigo = r }),
        "correo_con_cuenta" => Results.Conflict(new { error = "Ese correo ya tiene cuenta: entra con él y vuelve a abrir el enlace.", codigo = r }),
        "otro_correo"       => Results.Json(new { error = "Entraste con otro correo. Para recuperarla, entra con el correo del titular.", codigo = r }, statusCode: StatusCodes.Status403Forbidden),
        "sin_permiso"       => Results.Json(new { error = "Solo el dueño de una cuenta puede recibir la empresa.", codigo = r }, statusCode: StatusCodes.Status403Forbidden),
        "convertir"         => Results.Conflict(new { error = "Tu Cuenta Negocio ya tiene una empresa. Conviértela en Cuenta Profesional (en «Mi cuenta») y vuelve a abrir el enlace.", codigo = r }),
        _                   => Results.NotFound(new { error = "Ese enlace no existe.", codigo = "no_existe" })
    };

    private static async Task AvisarCuentaAnteriorAsync(
        JsonElement raiz, IServicioCorreo correo, ILoggerFactory logs, CancellationToken ct)
    {
        var destino = Texto(raiz, "avisoCorreo");
        if (string.IsNullOrWhiteSpace(destino)) return;

        try
        {
            var m = Plantillas.EmpresaRecuperada(Texto(raiz, "avisoNombre") ?? "", Texto(raiz, "empresa") ?? "la empresa");
            await correo.EnviarAsync(destino, m.Asunto, m.Html, m.Texto, ct);
        }
        catch (Exception e)
        {
            // La recuperación ya está hecha: el aviso es cortesía.
            logs.CreateLogger("Recuperar").LogWarning(e, "No salió el aviso a la cuenta anterior.");
        }
    }

    /// <summary>De qué empresa es el enlace (023), antes de usarlo.</summary>
    private static Task<Guid?> EmpresaDelEnlaceAsync(ContextoPlataforma db, byte[] hash, CancellationToken ct) =>
        db.Database.SqlQuery<Guid?>($"SELECT empresa_por_enlace({hash}) AS \"Value\"").FirstOrDefaultAsync(ct);

    private static (string Correo, string Nombre, string Ruc) Leer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return (Texto(r, "correo") ?? "", Texto(r, "nombre") ?? "", Texto(r, "ruc") ?? "");
    }

    private static string? Texto(JsonElement e, string nombre) =>
        e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}