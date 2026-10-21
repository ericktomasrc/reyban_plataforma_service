using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Avisos;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Sesion;

/// <summary>
/// 039 — RECUPERAR EL SEGUNDO FACTOR SIN NADIE, CON ESPERA.
///
/// Para quien perdió el teléfono Y los ocho códigos (le robaron el teléfono y
/// la laptop). Como Google, AWS o Apple:
///
///   1. En la pantalla del código —ya puso su contraseña— pide recuperar.
///   2. Le llega un enlace a su correo. Al confirmarlo se cierran TODAS sus
///      sesiones y empieza la espera: 24 h un usuario, 72 h un administrador,
///      7 días un super administrador (solo si hay otro).
///   3. Pasada la espera, entra con su contraseña y configura un teléfono nuevo.
///
/// Se avisa a él y a quienes están por encima, SIN ENLACES. Se cancela sola si
/// el dueño entra con su teléfono o con un código, y la cancela cualquier
/// administrador de su empresa o un super administrador.
///
/// Por qué la espera: quien robó la laptop puede tener la contraseña y el
/// correo abiertos. La espera le da al dueño (y a su jefe) días para verlo y
/// cortarlo. Con teléfono y correo robados a la vez, nada automático es seguro;
/// la espera es lo que lo hace razonable.
/// </summary>
public sealed class ServicioRecuperacion2fa(
    ContextoPlataforma db,
    IServicioCorreo correo,
    OpcionesCorreo opciones,
    IHostEnvironment entorno,
    ILogger<ServicioRecuperacion2fa> log)
{
    /// <summary>El enlace del correo de confirmación: una hora.</summary>
    public const int HorasEnlace = 1;

    /// <summary>Perú no cambia de hora: UTC−5 todo el año.</summary>
    private static readonly TimeSpan HoraPeru = TimeSpan.FromHours(-5);

    public sealed record Recuperacion(
        Guid id, Guid usuarioId, string nombre, string correo, string empresa,
        string estado, string nivel, int horasEspera,
        DateTimeOffset pedidaEn, DateTimeOffset? disponibleEn, bool lista, string[]? avisados);

    public static Recuperacion? Leer(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Recuperacion>(json);

    /// <summary>Lo que ve la persona: sin la lista de correos de sus jefes.</summary>
    public static object? ParaPantalla(Recuperacion? r) => r is null ? null : new
    {
        r.estado, r.nivel, r.horasEspera, r.pedidaEn, r.disponibleEn, r.lista,
        disponibleTexto = r.disponibleEn is { } d ? Fecha(d) : null,
        esperaTexto     = TextoEspera(r.horasEspera)
    };

    public static string Fecha(DateTimeOffset cuando)
    {
        var p = cuando.ToOffset(HoraPeru);
        return $"{p:dd/MM/yyyy} a las {p:HH:mm} (hora de Perú)";
    }

    public static string TextoEspera(int horas) => horas switch
    {
        24  => "24 horas",
        72  => "3 días",
        168 => "7 días",
        _ when horas % 24 == 0 => $"{horas / 24} días",
        _   => $"{horas} horas"
    };


    // =========================================================================
    // 1. PEDIR: manda el enlace de confirmación a su correo.
    // =========================================================================
    public async Task<(string Resultado, Recuperacion? R, string? EnlaceDePrueba, bool Enviado)> PedirAsync(CancellationToken ct)
    {
        var (token, hash) = GeneradorTokens.Nuevo();
        var expira = DateTime.UtcNow.AddHours(HorasEnlace);

        var json = await db.Database
            .SqlQuery<string>($"SELECT pedir_recuperacion_2fa({hash}, {expira}::timestamptz) AS \"Value\"")
            .FirstAsync(ct);

        using var doc  = JsonDocument.Parse(json);
        var resultado  = doc.RootElement.GetProperty("resultado").GetString() ?? "";
        var r          = doc.RootElement.TryGetProperty("recuperacion", out var rec) ? Leer(rec.GetRawText()) : null;

        if (resultado != "pedida" || r is null)
            return (resultado, r, null, false);

        var enlace  = $"{opciones.UrlBase.TrimEnd('/')}/recuperar-segundo-factor/{token}";
        var mensaje = Plantillas.ConfirmarRecuperacion2fa(r.nombre, enlace, HorasEnlace, TextoEspera(r.horasEspera));
        var envio   = await correo.EnviarConMotivoAsync(r.correo, mensaje.Asunto, mensaje.Html, mensaje.Texto, ct);

        await Registrar("otro", envio.Enviado, r.usuarioId, "recuperacion_2fa_pedida", ct);

        var deprueba = entorno.IsDevelopment() && !opciones.Habilitado ? enlace : null;
        return (resultado, r, deprueba, envio.Enviado);
    }


    // =========================================================================
    // 2. CONFIRMADA (con el enlace): empieza la espera. Aviso a todos.
    // =========================================================================
    public async Task ConfirmadaAsync(Recuperacion r, CancellationToken ct)
    {
        await Registrar("sesion_revocada", true, r.usuarioId, "recuperacion_2fa_confirmada", ct);

        var cuando = r.disponibleEn is { } d ? Fecha(d) : "";
        await AvisarAsync(r,
            "Se pidió recuperar el segundo factor de tu cuenta.",
            $"Tus sesiones se cerraron. Si nadie lo cancela, el {cuando} se podrá entrar con tu " +
            "contraseña y configurar un teléfono nuevo. Si NO fuiste tú: entra con tu teléfono o " +
            "con uno de tus códigos y se cancela solo; o pide a tu administrador que lo cancele, " +
            "y cambia tu contraseña.",
            $"{r.nombre} ({r.correo}) pidió recuperar su segundo factor porque dice haber perdido " +
            $"su teléfono y sus códigos. Si nadie lo cancela, el {cuando} podrá configurar un " +
            "teléfono nuevo solo con su contraseña.",
            ct);
    }


    // =========================================================================
    // 3. COMPLETADA: ya configuró (o va a configurar) el teléfono nuevo.
    // =========================================================================
    public async Task CompletadaAsync(Recuperacion r, CancellationToken ct)
    {
        await Registrar("dosfa_desactivado", true, r.usuarioId, "recuperacion_2fa_completada", ct);

        await AvisarAsync(r,
            "Se recuperó el segundo factor de tu cuenta tras la espera.",
            "El teléfono anterior y los ocho códigos anteriores dejaron de valer. Ahora se " +
            "configura un teléfono nuevo con ocho códigos nuevos.",
            $"{r.nombre} ({r.correo}) terminó la espera y recuperó su segundo factor: su " +
            "teléfono anterior ya no vale y está configurando uno nuevo.",
            ct);
    }


    // =========================================================================
    // 4. CANCELADA: por él (entró con su factor) o por un administrador.
    // =========================================================================
    public async Task CanceladaAsync(Recuperacion r, string porQuien, CancellationToken ct)
    {
        await Registrar("otro", true, r.usuarioId, "recuperacion_2fa_cancelada", ct);

        await AvisarAsync(r,
            "Se canceló la recuperación del segundo factor de tu cuenta.",
            $"La canceló {porQuien}. Tu teléfono y tus códigos siguen valiendo como antes. " +
            "Si no la pediste tú, alguien conoce tu contraseña: cámbiala desde tu perfil.",
            $"Se canceló la recuperación del segundo factor de {r.nombre} ({r.correo}). " +
            $"La canceló {porQuien}. Nada cambió en su cuenta.",
            ct);
    }

    /// <summary>
    /// Al pasar el segundo factor (teléfono o código), una recuperación en
    /// curso se cancela sola: quien la pidió no era él, o ya no la necesita.
    /// </summary>
    public async Task CancelarSiHabiaAsync(CancellationToken ct)
    {
        try
        {
            var json = await db.Database
                .SqlQuery<string>($"SELECT cancelar_mi_recuperacion_2fa() AS \"Value\"")
                .FirstOrDefaultAsync(ct);
            if (Leer(json) is { } r)
                await CanceladaAsync(r, "la misma persona, al entrar con su teléfono o con un código", ct);
        }
        catch (Exception e)
        {
            // Nunca impide entrar.
            log.LogError(e, "No se pudo cancelar la recuperación del segundo factor al entrar.");
        }
    }


    // =========================================================================
    // LOS AVISOS: SIN ENLACES. Al dueño con su texto, a los demás con el suyo.
    // =========================================================================
    private async Task AvisarAsync(Recuperacion r, string quePaso, string consecuencia,
                                   string paraTerceros, CancellationToken ct)
    {
        try
        {
            var propio = Plantillas.AvisoDeSeguridad(r.nombre, quePaso, consecuencia);
            await correo.EnviarAsync(r.correo, propio.Asunto, propio.Html, propio.Texto, ct);

            var tercero = Plantillas.AvisoRecuperacionTercero(r.empresa, paraTerceros);
            foreach (var destino in r.avisados ?? [])
            {
                if (string.Equals(destino, r.correo, StringComparison.OrdinalIgnoreCase)) continue;
                await correo.EnviarAsync(destino, tercero.Asunto, tercero.Html, tercero.Texto, ct);
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "Falló un aviso de la recuperación del segundo factor de {Usuario}", r.usuarioId);
        }
    }

    private Task Registrar(string tipo, bool exito, Guid usuarioId, string accion, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT registrar_evento({tipo}::text, {exito}::boolean, {usuarioId}::uuid, NULL, NULL,
                                    jsonb_build_object('accion', {accion}::text))
            """, ct);
}


public static class EndpointsRecuperacion2fa
{
    private const string EnlaceNoSirve =
        "Este enlace ya no sirve: caducó (dura una hora), ya se usó o se canceló. " +
        "Vuelve a entrar con tu contraseña y pídelo otra vez.";

    public static void MapearRecuperacion2fa(this IEndpointRouteBuilder rutas)
    {
        // ---------------------------------------------------------------------
        // LA MÍA, desde la pantalla del código (sesión a medias)
        // ---------------------------------------------------------------------
        rutas.MapGet("/api/2fa/espera", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database
                .SqlQuery<string>($"SELECT mi_recuperacion_2fa() AS \"Value\"")
                .FirstOrDefaultAsync(ct);
            return Results.Ok(new { recuperacion = ServicioRecuperacion2fa.ParaPantalla(ServicioRecuperacion2fa.Leer(json)) });
        })
        .RequiereSesionAMedias()
        .WithTags("Segundo factor")
        .WithSummary("Mi recuperación del segundo factor en curso, si hay");


        // ---------------------------------------------------------------------
        // PEDIRLA: «Tampoco tengo mis códigos»
        // ---------------------------------------------------------------------
        rutas.MapPost("/api/2fa/espera", async (ServicioRecuperacion2fa servicio, CancellationToken ct) =>
        {
            var (resultado, r, enlaceDePrueba, enviado) = await servicio.PedirAsync(ct);

            return resultado switch
            {
                "pedida" => Results.Ok(new
                {
                    resultado,
                    enviado,
                    enlaceDePrueba,
                    recuperacion = ServicioRecuperacion2fa.ParaPantalla(r),
                    mensaje = enviado
                        ? $"Te mandamos un enlace a {r!.correo}. Ábrelo en la próxima hora para empezar la espera."
                        : "No pudimos mandarte el correo. Inténtalo en unos minutos o avisa a tu administrador."
                }),
                "en_curso" => Results.Ok(new
                {
                    resultado,
                    recuperacion = ServicioRecuperacion2fa.ParaPantalla(r)
                }),
                "espera" => Results.Json(new
                {
                    resultado,
                    error = "Ya te mandamos el enlace hace menos de un minuto. Revisa tu correo (y la carpeta de spam)."
                }, statusCode: StatusCodes.Status429TooManyRequests),
                "sin_factor" => Results.BadRequest(new
                {
                    resultado,
                    error = "Tu cuenta no tiene segundo factor todavía: configúralo con el código QR."
                }),
                "no_permitida" => Results.Json(new
                {
                    resultado,
                    error = "Eres el único super administrador con segundo factor, así que nadie podría " +
                            "cancelar una recuperación falsa. Se hace desde el servidor " +
                            "(PLATAFORMA_REINICIAR_2FA en el .env, reiniciar, y quitar la línea)."
                }, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Unauthorized()
            };
        })
        .RequiereSesionAMedias()
        .WithTags("Segundo factor")
        .WithSummary("Pedir recuperar el segundo factor (perdí el teléfono y los códigos)");


        // ---------------------------------------------------------------------
        // COMPLETARLA: pasada la espera, borra el factor viejo. Después la
        // pantalla sigue con /api/2fa/preparar y /api/2fa/confirmar, igual que
        // la primera vez.
        // ---------------------------------------------------------------------
        rutas.MapPost("/api/2fa/espera/completar", async (
            ContextoPlataforma db, ContextoPeticion ctx,
            ServicioRecuperacion2fa servicio, CancellationToken ct) =>
        {
            var json = await db.Database
                .SqlQuery<string>($"SELECT completar_recuperacion_2fa({ctx.SesionId}::uuid) AS \"Value\"")
                .FirstOrDefaultAsync(ct);

            if (ServicioRecuperacion2fa.Leer(json) is not { } r)
                return Results.Conflict(new
                {
                    error = "Todavía no se puede: la espera no terminó o la recuperación se canceló."
                });

            await servicio.CompletadaAsync(r, ct);
            return Results.Ok(new { mensaje = "Listo. Ahora configura tu teléfono nuevo." });
        })
        .RequiereSesionAMedias()
        .WithTags("Segundo factor")
        .WithSummary("Terminar la recuperación del segundo factor (pasada la espera)");


        // ---------------------------------------------------------------------
        // CONFIRMAR CON EL ENLACE DEL CORREO (sin sesión)
        //
        // POST y no GET: algunos correos abren los enlaces solos para revisarlos,
        // y eso no puede empezar una espera ni cerrar sesiones. La página pide
        // un clic.
        // ---------------------------------------------------------------------
        rutas.MapPost("/api/recuperar-segundo-factor/{token}", async (
            string token, HttpContext http, ContextoPlataforma db,
            ServicioRecuperacion2fa servicio, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
                return Results.NotFound(new { error = EnlaceNoSirve, codigo = "enlace_invalido" });

            var hash = GeneradorTokens.Hashear(token);
            var ip   = http.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

            var json = await db.Database
                .SqlQuery<string>($"SELECT confirmar_recuperacion_2fa({hash}, {ip}::inet) AS \"Value\"")
                .FirstOrDefaultAsync(ct);

            if (ServicioRecuperacion2fa.Leer(json) is not { } r)
                return Results.NotFound(new { error = EnlaceNoSirve, codigo = "enlace_invalido" });

            await servicio.ConfirmadaAsync(r, ct);

            return Results.Ok(new { recuperacion = ServicioRecuperacion2fa.ParaPantalla(r) });
        })
        .WithTags("Segundo factor")
        .WithSummary("Confirmar con el enlace del correo: empieza la espera (sin sesión)");


        // ---------------------------------------------------------------------
        // LAS QUE ESTÁN EN CURSO: el super administrador ve todas; un
        // administrador, las de su empresa. La base decide.
        // ---------------------------------------------------------------------
        rutas.MapGet("/api/recuperaciones-2fa", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT vencer_recuperaciones_2fa()", ct);
            var json = await db.Database
                .SqlQuery<string>($"SELECT recuperaciones_2fa_en_curso() AS \"Value\"")
                .FirstOrDefaultAsync(ct) ?? "[]";

            var lista = JsonSerializer.Deserialize<ServicioRecuperacion2fa.Recuperacion[]>(json) ?? [];
            return Results.Ok(lista.Select(r => new
            {
                r.id, r.usuarioId, r.nombre, r.correo, r.empresa, r.estado, r.nivel,
                r.pedidaEn, r.disponibleEn, r.lista,
                disponibleTexto = r.disponibleEn is { } d ? ServicioRecuperacion2fa.Fecha(d) : null
            }));
        })
        .RequiereSesion()
        .WithTags("Segundo factor")
        .WithSummary("Recuperaciones del segundo factor en curso (administradores)");


        rutas.MapPost("/api/recuperaciones-2fa/{id:guid}/cancelar", async (
            Guid id, ContextoPlataforma db, ContextoPeticion ctx,
            ServicioRecuperacion2fa servicio, CancellationToken ct) =>
        {
            var json = await db.Database
                .SqlQuery<string>($"SELECT cancelar_recuperacion_2fa({id}) AS \"Value\"")
                .FirstOrDefaultAsync(ct);

            if (ServicioRecuperacion2fa.Leer(json) is not { } r)
                return Results.NotFound(new { error = "Esa recuperación ya no está en curso, o no te toca cancelarla." });

            await servicio.CanceladaAsync(r,
                ctx.EsSuperAdmin ? "un super administrador de la plataforma" : "un administrador de tu empresa", ct);

            return Results.Ok(new { mensaje = $"Cancelada. Avisamos a {r.nombre}." });
        })
        .RequiereSesion()
        .WithTags("Segundo factor")
        .WithSummary("Cancelar una recuperación del segundo factor");


        // ---------------------------------------------------------------------
        // CERRAR LAS SESIONES DE ALGUIEN (permiso «usuarios.sesiones_revocar»)
        //
        // Lo primero que hace un administrador cuando a alguien le roban la
        // laptop: que la sesión abierta ahí deje de valer ya.
        // ---------------------------------------------------------------------
        rutas.MapPost("/api/usuarios/{id:guid}/cerrar-sesiones", async (
            Guid id, ContextoPlataforma db, ContextoPeticion ctx,
            AvisosDeSeguridad avisos, CancellationToken ct) =>
        {
            if (id == ctx.UsuarioId)
                return Results.BadRequest(new { error = "Para cerrar las tuyas, usa «Cerrar sesión»." });

            // Con la seguridad de filas, solo aparecen los de mi empresa.
            var existe = await db.Usuarios.IgnoreQueryFilters().AnyAsync(u => u.Id == id, ct);
            if (!existe) return Results.NotFound();

            var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE sesiones
                   SET revocada_en = now(), motivo_revocacion = 'cerradas por un administrador'
                 WHERE usuario_id = {id} AND revocada_en IS NULL
                """, ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento('sesion_revocada'::text, true, {id}::uuid, NULL, NULL,
                                        jsonb_build_object('accion', 'cerradas_por_administrador', 'cuantas', {n}))
                """, ct);

            await avisos.AvisarAsync(id,
                "Un administrador cerró todas tus sesiones.",
                "Tienes que volver a entrar con tu contraseña y tu segundo factor. Nada más cambió.",
                ct);

            return Results.Ok(new
            {
                mensaje = n == 0 ? "No tenía sesiones abiertas." : $"Listo: {n} sesión(es) cerrada(s)."
            });
        })
        .RequierePermiso(Permisos.Usuarios.SesionesRevocar)
        .WithTags("Usuarios")
        .WithSummary("Cerrar todas las sesiones de un usuario");
    }
}