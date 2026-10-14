using System.Text.Json;

namespace Plataforma.Api.Registro;

/// <summary>
/// La comprobación anti-robot del registro: Cloudflare Turnstile.
///
/// POR QUÉ TURNSTILE: es gratis, casi nunca le pide nada a una persona (no hay
/// «marca los semáforos») y no rastrea a quien lo resuelve.
///
/// LA PANTALLA SOLO CONSIGUE UN TOKEN; QUIEN DECIDE ES ESTE ARCHIVO, que se lo
/// pregunta a Cloudflare con la clave secreta. Un captcha que solo se mira en
/// el navegador se salta llamando a la API directamente.
///
/// LAS CLAVES, del .env:
///   TURNSTILE_SITE_KEY   pública, la usa la pantalla
///   TURNSTILE_SECRET     secreta, solo aquí
///
/// SIN CLAVES:
///   · en desarrollo se usan las CLAVES DE PRUEBA de Cloudflare, que aprueban
///     siempre. Se puede probar el registro entero sin crear nada.
///   · en producción el registro se CIERRA (503) en vez de abrirse sin
///     captcha. Un error de configuración no puede dejar la puerta abierta.
/// </summary>
public sealed class Captcha
{
    private readonly IHttpClientFactory fabrica;
    private readonly ILogger<Captcha> log;

    public const string NombreCliente = "turnstile";

    // Las de prueba que publica Cloudflare: «siempre aprueba».
    private const string SitioDePrueba  = "1x00000000000000000000AA";
    private const string SecretoDePrueba = "1x0000000000000000000000000000000AA";

    public string? ClaveSitio { get; }
    private string? Secreto { get; }

    /// <summary>Cierto si se están usando las claves de prueba.</summary>
    public bool DePrueba { get; }

    public bool Configurado => ClaveSitio is not null && Secreto is not null;

    public Captcha(IHttpClientFactory fabrica, ILogger<Captcha> log, IHostEnvironment entorno)
    {
        this.fabrica = fabrica;
        this.log = log;

        var sitio   = Environment.GetEnvironmentVariable("TURNSTILE_SITE_KEY");
        var secreto = Environment.GetEnvironmentVariable("TURNSTILE_SECRET");

        if (!string.IsNullOrWhiteSpace(sitio) && !string.IsNullOrWhiteSpace(secreto))
        {
            ClaveSitio = sitio.Trim();
            Secreto    = secreto.Trim();
        }
        else if (entorno.IsDevelopment())
        {
            ClaveSitio = SitioDePrueba;
            Secreto    = SecretoDePrueba;
            DePrueba   = true;
        }
    }

    /// <summary>
    /// Pregunta a Cloudflare si el token es bueno. Cualquier fallo —sin red,
    /// respuesta rara, token vacío— es un NO.
    /// </summary>
    public async Task<bool> VerificarAsync(string? token, string? ip, CancellationToken ct)
    {
        if (!Configurado || string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var http = fabrica.CreateClient(NombreCliente);

            var formulario = new Dictionary<string, string>
            {
                ["secret"]   = Secreto!,
                ["response"] = token,
            };
            if (!string.IsNullOrWhiteSpace(ip)) formulario["remoteip"] = ip;

            using var respuesta = await http.PostAsync(
                "https://challenges.cloudflare.com/turnstile/v0/siteverify",
                new FormUrlEncodedContent(formulario), ct);

            if (!respuesta.IsSuccessStatusCode)
            {
                log.LogWarning("Turnstile respondió {Codigo}", (int)respuesta.StatusCode);
                return false;
            }

            using var doc = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("success", out var exito)
                   && exito.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "No se pudo comprobar el captcha");
            return false;
        }
    }
}