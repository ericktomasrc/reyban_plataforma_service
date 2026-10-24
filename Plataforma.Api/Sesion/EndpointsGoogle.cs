using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Middleware;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Sesion;

/// <summary>049: las credenciales de Google Cloud (OAuth, cliente «Aplicación web»). Sin ellas, no hay botón.</summary>
public sealed class OpcionesGoogle
{
    public string? ClienteId { get; init; }
    public string? Secreto { get; init; }
    public bool Activo => !string.IsNullOrWhiteSpace(ClienteId) && !string.IsNullOrWhiteSpace(Secreto);

    public static OpcionesGoogle Leer() => new()
    {
        ClienteId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")?.Trim(),
        Secreto   = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET")?.Trim()
    };
}

public record PeticionRegistroGoogle(string? Token, string? Tipo, bool Acepto);

/// <summary>
/// 049 — ENTRAR CON GOOGLE (OpenID Connect, código + PKCE).
///
///   GET  /api/google/config     ¿hay botón?
///   GET  /api/google/entrar     lleva a Google (elige la cuenta)
///   GET  /api/google/vuelta     Google vuelve aquí con el código
///   GET  /api/google/pendiente  el registro a medias: correo y nombre
///   POST /api/google/registro   crear la cuenta (tipo y términos)
///
/// Todo sin sesión. El código se cambia por el token directamente con Google
/// (por TLS, con el secreto y el verificador PKCE): el id_token que vuelve es
/// de Google y para nosotros; se revisan audiencia, emisor, vencimiento y que
/// el correo esté verificado. Quién es y la sesión los decide la base.
///
/// POR QUÉ /vuelta RESPONDE UNA PÁGINA Y NO UN 302: la cookie de sesión es
/// SameSite=Strict, y el navegador no la manda en una navegación que empezó
/// en accounts.google.com, aunque rebote en nuestro dominio. La página da el
/// último salto desde aquí mismo, y entonces sí viaja.
/// </summary>
public static class EndpointsGoogle
{
    public const string NombreCliente = "google";
    private const string Cookie = "reyban_google";
    private static readonly TimeSpan DuraSesion = TimeSpan.FromHours(12);

    public static void MapearGoogle(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/google").WithTags("Entrar con Google");

        grupo.MapGet("/config", (OpcionesGoogle g) => Results.Ok(new { activo = g.Activo }))
            .WithSummary("¿Está configurado «Entrar con Google»?");


        grupo.MapGet("/entrar", (HttpContext http, OpcionesGoogle g, OpcionesCorreo opciones) =>
        {
            if (!g.Activo) return Results.Redirect("/?google=no_configurado");

            var estado = Aleatorio(24);
            var verificador = Aleatorio(48);
            var desafio = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verificador)));

            // Lax y no Strict: tiene que volver en la navegación que viene de Google.
            http.Response.Cookies.Append(Cookie, $"{estado}.{verificador}", new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/api/google",
                MaxAge = TimeSpan.FromMinutes(10)
            });

            var url = "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", new Dictionary<string, string>
            {
                ["client_id"] = g.ClienteId!,
                ["redirect_uri"] = Vuelta(opciones),
                ["response_type"] = "code",
                ["scope"] = "openid email profile",
                ["state"] = estado,
                ["code_challenge"] = desafio,
                ["code_challenge_method"] = "S256",
                ["prompt"] = "select_account"
            }.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

            return Results.Redirect(url);
        })
        .WithSummary("Ir a Google para elegir la cuenta");


        grupo.MapGet("/vuelta", async (
            string? code, string? state, string? error,
            HttpContext http, OpcionesGoogle g, OpcionesCorreo opciones, IHttpClientFactory fabrica,
            ContextoPlataforma db, ContextoPeticion ctx, ILogger<OpcionesGoogle> log, CancellationToken ct) =>
        {
            var guardado = http.Request.Cookies[Cookie];
            http.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/api/google" });

            if (!g.Activo) return Salto("/?google=no_configurado");
            if (!string.IsNullOrEmpty(error)) return Salto("/?google=cancelado");   // «Cancelar» en Google

            var partes = (guardado ?? "").Split('.', 2);
            if (partes.Length != 2 || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code)
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(partes[0]), Encoding.ASCII.GetBytes(state)))
                return Salto("/?google=vencido");

            // El código por los tokens, directo con Google.
            Identidad? yo;
            try
            {
                var cliente = fabrica.CreateClient(NombreCliente);
                using var r = await cliente.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = g.ClienteId!,
                    ["client_secret"] = g.Secreto!,
                    ["redirect_uri"] = Vuelta(opciones),
                    ["grant_type"] = "authorization_code",
                    ["code_verifier"] = partes[1]
                }), ct);
                var cuerpo = await r.Content.ReadAsStringAsync(ct);
                if (!r.IsSuccessStatusCode)
                {
                    log.LogWarning("Google no cambió el código: {Estado} {Cuerpo}", (int)r.StatusCode, cuerpo.Length > 300 ? cuerpo[..300] : cuerpo);
                    return Salto("/?google=fallo");
                }
                using var doc = JsonDocument.Parse(cuerpo);
                yo = LeerIdToken(doc.RootElement.TryGetProperty("id_token", out var t) ? t.GetString() : null, g.ClienteId!);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                log.LogWarning(ex, "No se pudo hablar con Google");
                return Salto("/?google=fallo");
            }

            if (yo is null) return Salto("/?google=fallo");
            if (!yo.Verificado) return Salto("/?google=sin_verificar");

            var (token, hash) = GeneradorTokens.Nuevo();
            var expira = DateTime.UtcNow.Add(DuraSesion);
            var ip = ctx.Ip?.ToString();

            var texto = await db.Database.SqlQuery<string>($"""
                SELECT ingresar_con_google({yo.Sub}, {yo.Correo}, {hash}, {expira}::timestamptz, {ip}::inet, {ctx.Agente}) AS "Value"
                """).FirstAsync(ct);
            var resultado = Leer(texto, "resultado");

            if (resultado == "ok")
            {
                PonerSesion(http, token, expira);
                return Salto("/");
            }

            if (resultado == "no_existe")
            {
                // A crear la cuenta: el enlace dura 15 minutos y sirve una vez.
                var (pendiente, hashPendiente) = GeneradorTokens.Nuevo();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    SELECT google_pendiente_guardar({hashPendiente}, {yo.Sub}, {yo.Correo}, {yo.Nombre}, {DateTime.UtcNow.AddMinutes(15)}::timestamptz)
                    """, ct);
                return Salto($"/registro/google/{pendiente}");
            }

            return Salto($"/?google={resultado}");
        })
        .WithSummary("Google vuelve aquí");


        grupo.MapGet("/pendiente", async (string? t, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(t)) return Results.NotFound(new { error = "Falta el enlace." });
            var texto = await db.Database.SqlQuery<string?>(
                $"SELECT google_pendiente_ver({GeneradorTokens.Hashear(t.Trim())}) AS \"Value\"").FirstAsync(ct);
            return texto is null
                ? Results.NotFound(new { error = "El enlace venció. Vuelve a pulsar «Entrar con Google»." })
                : Results.Content(texto, "application/json");
        })
        .WithSummary("El registro con Google a medias");


        grupo.MapPost("/registro", async (
            PeticionRegistroGoogle p, HttpContext http, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(p.Token)) return Results.BadRequest(new { error = "Falta el enlace." });
            if (p.Tipo is not ("negocio" or "profesional"))
                return Results.BadRequest(new { error = "Elige el tipo de cuenta.", campo = "tipo" });
            if (!p.Acepto)
                return Results.BadRequest(new { error = "Para crear la cuenta hay que aceptar los términos y la política de privacidad.", campo = "acepto" });

            var (token, hash) = GeneradorTokens.Nuevo();
            var expira = DateTime.UtcNow.Add(DuraSesion);
            var ip = ctx.Ip?.ToString();

            var texto = await db.Database.SqlQuery<string>($"""
                SELECT registrar_con_google({GeneradorTokens.Hashear(p.Token.Trim())}, {p.Tipo}, {hash},
                                            {expira}::timestamptz, {ip}::inet, {ctx.Agente}) AS "Value"
                """).FirstAsync(ct);

            switch (Leer(texto, "resultado"))
            {
                case "ok":
                    PonerSesion(http, token, expira);
                    return Results.Ok(new { mensaje = "Tu cuenta quedó creada." });
                case "correo_ocupado":
                    return Results.Conflict(new { error = "Ese correo ya tiene una cuenta: entra con Google o con tu contraseña." });
                case "demasiados":
                    return Results.Json(new { error = "Demasiados registros desde tu red. Prueba en una hora." },
                                        statusCode: StatusCodes.Status429TooManyRequests);
                default:
                    return Results.BadRequest(new { error = "El enlace venció. Vuelve a pulsar «Entrar con Google».", codigo = "vencido" });
            }
        })
        .WithSummary("Crear la cuenta con Google");
    }


    // =========================================================================

    private sealed record Identidad(string Sub, string Correo, string? Nombre, bool Verificado);

    /// <summary>
    /// El id_token vino de Google por TLS, en respuesta a nuestro código y
    /// nuestro secreto (OpenID Connect 3.1.3.7, punto 6): basta revisar
    /// audiencia, emisor y vencimiento.
    /// </summary>
    private static Identidad? LeerIdToken(string? idToken, string clienteId)
    {
        var partes = (idToken ?? "").Split('.');
        if (partes.Length != 3) return null;

        var json = Encoding.UTF8.GetString(DesdeBase64Url(partes[1]));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var aud = r.TryGetProperty("aud", out var a)
            ? a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString()).ToArray() : [a.GetString()]
            : [];
        if (!aud.Contains(clienteId)) return null;
        if (S("iss") is not ("accounts.google.com" or "https://accounts.google.com")) return null;
        if (!r.TryGetProperty("exp", out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) < DateTimeOffset.UtcNow.AddMinutes(-2))
            return null;

        var sub = S("sub"); var correo = S("email");
        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(correo)) return null;

        var verificado = r.TryGetProperty("email_verified", out var ev)
            && (ev.ValueKind == JsonValueKind.True || (ev.ValueKind == JsonValueKind.String && ev.GetString() == "true"));

        return new Identidad(sub, correo.Trim(), S("name"), verificado);
    }

    private static void PonerSesion(HttpContext http, string token, DateTime expira) =>
        http.Response.Cookies.Append(MiddlewareContexto.NombreCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expira
        });

    private static string Vuelta(OpcionesCorreo o) => $"{o.UrlBase.TrimEnd('/')}/api/google/vuelta";

    /// <summary>Una página que salta a la ruta del front (ver arriba por qué no un 302).</summary>
    private static IResult Salto(string ruta)
    {
        var url = WebUtility.HtmlEncode(ruta);
        var js = JsonSerializer.Serialize(ruta);
        return Results.Content($"""
            <!doctype html><html lang="es"><head><meta charset="utf-8">
            <meta http-equiv="refresh" content="0;url={url}"><title>Entrando…</title></head>
            <body style="font-family:sans-serif;padding:2rem;color:#555">Entrando… <a href="{url}">continuar</a>
            <script>location.replace({js});</script></body></html>
            """, "text/html; charset=utf-8");
    }

    private static string? Leer(string json, string clave)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(clave, out var v) ? v.GetString() : null;
    }

    private static string Aleatorio(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DesdeBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}