using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Plataforma.Core.Correo;

/// <summary>Lo que pasó con un envío: si salió y, si no, por qué (en palabras simples).</summary>
public sealed record ResultadoEnvio(bool Enviado, string? Motivo);

public interface IServicioCorreo
{
    Task<bool> EnviarAsync(
        string destino,
        string asunto,
        string cuerpoHtml,
        string cuerpoTexto,
        CancellationToken ct = default);

    /// <summary>
    /// Igual que <see cref="EnviarAsync"/>, pero devuelve también el motivo si
    /// no salió (038). Lo usan los correos de acceso, que se anotan en la lista
    /// del super administrador.
    /// </summary>
    async Task<ResultadoEnvio> EnviarConMotivoAsync(
        string destino,
        string asunto,
        string cuerpoHtml,
        string cuerpoTexto,
        CancellationToken ct = default)
    {
        var enviado = await EnviarAsync(destino, asunto, cuerpoHtml, cuerpoTexto, ct);
        return new ResultadoEnvio(enviado, enviado ? null : "No se pudo enviar");
    }

    /// <summary>Conecta y se identifica con el servidor de correo, sin mandar nada (038).</summary>
    Task<ResultadoEnvio> ProbarAsync(CancellationToken ct = default) =>
        Task.FromResult(new ResultadoEnvio(true, null));
}


/// <summary>
/// 038 — EL ÚLTIMO ESTADO DEL CORREO, para el aviso del super administrador.
///
/// Se actualiza con cada envío y cada prueba. Vive en memoria (se pierde al
/// reiniciar, y al arrancar se prueba otra vez): lo que interesa es saber AHORA
/// si Gmail acepta el usuario y la contraseña, no la historia.
/// </summary>
public static class EstadoCorreo
{
    private static readonly object Candado = new();
    private static bool _ok = true;
    private static string? _motivo;
    private static DateTime? _fallaDesde;
    private static DateTime? _ultimaVez;

    public static void Anotar(bool ok, string? motivo)
    {
        lock (Candado)
        {
            if (ok) { _fallaDesde = null; _motivo = null; }
            else
            {
                if (_ok || _fallaDesde is null) _fallaDesde = DateTime.UtcNow;
                _motivo = motivo;
            }
            _ok = ok;
            _ultimaVez = DateTime.UtcNow;
        }
    }

    public static (bool Ok, string? Motivo, DateTime? FallaDesde, DateTime? UltimaVez) Leer()
    {
        lock (Candado) return (_ok, _motivo, _fallaDesde, _ultimaVez);
    }

    /// <summary>
    /// El error del servidor de correo, en palabras que entiende quien lo lee.
    /// El mensaje entero va al log; aquí solo lo que hay que hacer.
    /// </summary>
    public static string MotivoSimple(Exception e)
    {
        var m = e.Message ?? "";
        if (m.Contains("5.7.8") || m.Contains("535") || m.Contains("Username and Password not accepted", StringComparison.OrdinalIgnoreCase)
            || e is AuthenticationException)
            return "Gmail rechazó el usuario o la contraseña de envío";
        if (m.Contains("5.1.1") || m.Contains("550") || m.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            return "El buzón de destino no existe";
        if (m.Contains("5.4.5") || m.Contains("limit", StringComparison.OrdinalIgnoreCase))
            return "Se alcanzó el límite de envíos del día de la cuenta de correo";
        if (e is System.Net.Sockets.SocketException || e is TimeoutException || e is OperationCanceledException
            || m.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "No hay conexión con el servidor de correo";
        if (e is ParseException || e is FormatException)
            return "La dirección de correo no es válida";
        return "No se pudo enviar (" + (m.Length > 120 ? m[..120] + "…" : m) + ")";
    }
}


/// <summary>
/// Envía correo por SMTP. Con <see cref="OpcionesCorreo.Habilitado"/> en false,
/// lo escribe en el log y no sale nada.
///
/// DEVUELVE UN BOOLEANO Y NO LANZA. Es deliberado, y es la decisión importante
/// de este archivo: que un servidor de correo esté caído no puede impedir que
/// se cree un usuario.
///
/// Si esto lanzara, la excepción tumbaría la transacción entera y el usuario
/// no llegaría a existir — por un problema que no tiene nada que ver con él.
/// En cambio, devolviendo false, la cuenta queda creada y quien la creó ve un
/// aviso de que el correo no salió, con el botón de reenviar a mano.
/// </summary>
public sealed class ServicioCorreo(OpcionesCorreo opciones, ILogger<ServicioCorreo> log)
    : IServicioCorreo
{
    public async Task<bool> EnviarAsync(
        string destino,
        string asunto,
        string cuerpoHtml,
        string cuerpoTexto,
        CancellationToken ct = default) =>
        (await EnviarConMotivoAsync(destino, asunto, cuerpoHtml, cuerpoTexto, ct)).Enviado;

    public async Task<ResultadoEnvio> EnviarConMotivoAsync(
        string destino,
        string asunto,
        string cuerpoHtml,
        string cuerpoTexto,
        CancellationToken ct = default)
    {
        if (!opciones.Habilitado)
        {
            // En desarrollo esto es la funcionalidad, no un apaño: el enlace
            // sale en la consola y se pega en el navegador.
            log.LogInformation(
                "CORREO NO ENVIADO (MAIL_ENABLED=false)\n" +
                "  Para:   {Destino}\n" +
                "  Asunto: {Asunto}\n" +
                "{Cuerpo}",
                destino, asunto, cuerpoTexto);

            return new ResultadoEnvio(true, null);
        }

        if (!opciones.EstaCompleta)
        {
            log.LogError(
                "No se pudo enviar a {Destino}: falta configuracion de correo en el entorno.",
                destino);
            const string falta = "Falta configurar el correo (MAIL_USERNAME, MAIL_PASSWORD, MAIL_FROM)";
            EstadoCorreo.Anotar(false, falta);
            return new ResultadoEnvio(false, falta);
        }

        try
        {
            var mensaje = new MimeMessage();
            mensaje.From.Add(new MailboxAddress(opciones.DeNombre, opciones.De));
            mensaje.To.Add(MailboxAddress.Parse(destino));
            mensaje.Subject = asunto;

            // Las dos versiones, siempre. Hay clientes que no pintan HTML, y
            // los filtros de spam desconfían de un correo que solo trae HTML.
            mensaje.Body = new BodyBuilder
            {
                HtmlBody = cuerpoHtml,
                TextBody = cuerpoTexto
            }.ToMessageBody();

            using var cliente = new SmtpClient();
            await ConectarAsync(cliente, ct);
            await cliente.SendAsync(mensaje, ct);
            await cliente.DisconnectAsync(true, ct);

            log.LogInformation("Correo enviado a {Destino}: {Asunto}", destino, asunto);
            EstadoCorreo.Anotar(true, null);
            return new ResultadoEnvio(true, null);
        }
        catch (Exception e)
        {
            // El mensaje del servidor entero, que es lo único que distingue
            // «contraseña de aplicación caducada» de «no hay red».
            log.LogError(e, "Fallo al enviar a {Destino}: {Mensaje}", destino, e.Message);
            var motivo = EstadoCorreo.MotivoSimple(e);

            // Un buzón que no existe es culpa del destino, no del correo: el
            // aviso rojo del super administrador no se enciende por eso.
            if (motivo != "El buzón de destino no existe" && motivo != "La dirección de correo no es válida")
                EstadoCorreo.Anotar(false, motivo);

            return new ResultadoEnvio(false, motivo);
        }
    }

    /// <summary>
    /// 038 — Conectar e identificarse, sin mandar nada. Al arrancar y con el
    /// botón «Probar el correo ahora»: así una contraseña de Gmail equivocada se
    /// ve en el primer minuto, no cuando alguien se queja de que no le llegó nada.
    /// </summary>
    public async Task<ResultadoEnvio> ProbarAsync(CancellationToken ct = default)
    {
        if (!opciones.Habilitado)
            return new ResultadoEnvio(true, "El correo está apagado (MAIL_ENABLED=false): se escribe en el log.");

        if (!opciones.EstaCompleta)
        {
            const string falta = "Falta configurar el correo (MAIL_USERNAME, MAIL_PASSWORD, MAIL_FROM)";
            EstadoCorreo.Anotar(false, falta);
            return new ResultadoEnvio(false, falta);
        }

        try
        {
            using var cliente = new SmtpClient();
            await ConectarAsync(cliente, ct);
            await cliente.DisconnectAsync(true, ct);
            EstadoCorreo.Anotar(true, null);
            return new ResultadoEnvio(true, null);
        }
        catch (Exception e)
        {
            log.LogError(e, "PRUEBA DE CORREO FALLIDA ({Usuario} en {Servidor}:{Puerto}): {Mensaje}",
                opciones.Usuario, opciones.Servidor, opciones.Puerto, e.Message);
            var motivo = EstadoCorreo.MotivoSimple(e);
            EstadoCorreo.Anotar(false, motivo);
            return new ResultadoEnvio(false, motivo);
        }
    }

    private async Task ConectarAsync(SmtpClient cliente, CancellationToken ct)
    {
        // 465 habla cifrado desde el primer byte; 587 empieza en claro y
        // sube a TLS con STARTTLS. Elegir mal deja la conexión colgada
        // hasta que expira, sin un error que lo explique.
        var seguridad = opciones.Puerto == 465
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        cliente.Timeout = 20_000;
        await cliente.ConnectAsync(opciones.Servidor, opciones.Puerto, seguridad, ct);
        await cliente.AuthenticateAsync(opciones.Usuario, opciones.Clave, ct);
    }
}
