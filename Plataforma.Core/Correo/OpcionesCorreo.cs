namespace Plataforma.Core.Correo;

/// <summary>
/// Lo que hace falta para enviar un correo, todo desde el entorno.
///
/// NADA DE ESTO SE ESCRIBE EN EL CÓDIGO. La contraseña de aplicación de Gmail
/// abre la cuenta entera; en el repositorio queda para siempre, también en el
/// historial de git de quien clone el proyecto.
/// </summary>
public sealed class OpcionesCorreo
{
    public string Servidor { get; init; } = "smtp.gmail.com";
    public int    Puerto   { get; init; } = 587;

    public string Usuario { get; init; } = string.Empty;
    public string Clave   { get; init; } = string.Empty;

    /// <summary>
    /// EL REMITENTE TIENE QUE SER LA CUENTA AUTENTICADA, o un alias verificado
    /// en ella. Gmail no deja enviar como cualquier dirección: o lo rechaza, o
    /// lo reescribe.
    ///
    /// Y aunque lo dejara, el dominio ajeno tiene SPF y DMARC publicados, así
    /// que el correo llegaría marcado como falsificado — a la carpeta de spam
    /// o directamente rebotado.
    /// </summary>
    public string De       { get; init; } = string.Empty;
    public string DeNombre { get; init; } = "Plataforma";

    /// <summary>
    /// Con esto en false no se envía nada: el correo se escribe en el log.
    ///
    /// Es lo que se quiere en desarrollo — se prueba el flujo entero, se ve el
    /// enlace en la consola y se pega en el navegador, sin gastar envíos ni
    /// molestar a nadie con correos de prueba.
    /// </summary>
    public bool Habilitado { get; init; }

    /// <summary>
    /// La dirección pública de la aplicación, para construir los enlaces.
    ///
    /// NO SE DEDUCE DE LA PETICIÓN, a propósito. Si se tomara de la cabecera
    /// `Host`, cualquiera podría pedir un enlace de invitación con una cabecera
    /// falsificada y recibir un correo legítimo tuyo apuntando a su propio
    /// servidor. Tiene que decidirlo la configuración, no quien llama.
    /// </summary>
    public string UrlBase { get; init; } = "http://localhost:5173";

    /// <summary>Lo mínimo para poder enviar de verdad.</summary>
    public bool EstaCompleta =>
        !string.IsNullOrWhiteSpace(Servidor) &&
        !string.IsNullOrWhiteSpace(Usuario)  &&
        !string.IsNullOrWhiteSpace(Clave)    &&
        !string.IsNullOrWhiteSpace(De);
}
