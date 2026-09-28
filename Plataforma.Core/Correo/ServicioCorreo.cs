using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Plataforma.Core.Correo;

public interface IServicioCorreo
{
    Task<bool> EnviarAsync(
        string destino,
        string asunto,
        string cuerpoHtml,
        string cuerpoTexto,
        CancellationToken ct = default);
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

            return true;
        }

        if (!opciones.EstaCompleta)
        {
            log.LogError(
                "No se pudo enviar a {Destino}: falta configuracion de correo en el entorno.",
                destino);
            return false;
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

            // 465 habla cifrado desde el primer byte; 587 empieza en claro y
            // sube a TLS con STARTTLS. Elegir mal deja la conexión colgada
            // hasta que expira, sin un error que lo explique.
            var seguridad = opciones.Puerto == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await cliente.ConnectAsync(opciones.Servidor, opciones.Puerto, seguridad, ct);
            await cliente.AuthenticateAsync(opciones.Usuario, opciones.Clave, ct);
            await cliente.SendAsync(mensaje, ct);
            await cliente.DisconnectAsync(true, ct);

            log.LogInformation("Correo enviado a {Destino}: {Asunto}", destino, asunto);
            return true;
        }
        catch (Exception e)
        {
            // El mensaje del servidor entero, que es lo único que distingue
            // «contraseña de aplicación caducada» de «no hay red».
            log.LogError(e, "Fallo al enviar a {Destino}: {Mensaje}", destino, e.Message);
            return false;
        }
    }
}
