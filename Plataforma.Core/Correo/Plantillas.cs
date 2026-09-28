using System.Net;

namespace Plataforma.Core.Correo;

public sealed record CorreoCompuesto(string Asunto, string Html, string Texto);

/// <summary>
/// Los correos que manda la plataforma.
///
/// EN UN SOLO SITIO, y sin motor de plantillas. Son cuatro correos: montar una
/// biblioteca para esto sería más código que los correos.
///
/// Y DOS FAMILIAS, que conviene no mezclar:
///
///   LOS QUE ABREN UNA PUERTA —`Invitacion` y `Restablecer`— llevan un enlace
///   de un uso que caduca. Son los únicos.
///
///   LOS QUE AVISAN DE QUE ALGO PASÓ —`CorreoCambiado` y `AvisoDeSeguridad`—
///   NO LLEVAN NINGÚN ENLACE, a propósito, y no es un olvido: un correo que
///   dice «pasó algo raro en tu cuenta» y además trae un botón que pulsar es
///   indistinguible de la estafa de la que está avisando. El día que alguien
///   copie el diseño para robar cuentas, la única defensa que le queda a quien
///   lo recibe es «los avisos de esta plataforma nunca traen enlaces».
///
/// TODO LO QUE VIENE DE FUERA PASA POR <see cref="WebUtility.HtmlEncode"/>.
/// Un nombre de empresa con un &lt;script&gt; dentro no ejecuta nada en un
/// cliente de correo moderno, pero sí rompe el maquetado — y más de un lector
/// web sigue siendo generoso con lo que interpreta.
/// </summary>
public static class Plantillas
{
    public static CorreoCompuesto Invitacion(
        string nombre, string empresa, string enlace, int horas, string quienInvita)
    {
        var n = WebUtility.HtmlEncode(nombre);
        var e = WebUtility.HtmlEncode(empresa);
        var q = WebUtility.HtmlEncode(quienInvita);

        var plazo = TextoPlazo(horas);

        var asunto = $"Tu acceso a {empresa}";

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Hola, {n}</h1>

            <p style="margin:0 0 16px;">
              {q} te creó una cuenta en la plataforma de <strong>{e}</strong>.
            </p>

            <p style="margin:0 0 24px;">
              Para entrar por primera vez, elige tu contraseña:
            </p>

            {Boton(enlace, "Elegir mi contraseña")}

            <p style="margin:24px 0 8px;color:#4a5a67;font-size:14px;">
              El enlace caduca en <strong>{plazo}</strong> y solo sirve una vez.
              Si se te pasa el plazo, pídele a {q} que te lo reenvíe.
            </p>

            <p style="margin:0;color:#7d8d99;font-size:13px;">
              Si no esperabas este correo, no hagas nada: sin abrir el enlace,
              la cuenta no se puede usar.
            </p>
            """);

        var texto = $"""
            Hola, {nombre}

            {quienInvita} te creó una cuenta en la plataforma de {empresa}.

            Para entrar por primera vez, elige tu contraseña aquí:

            {enlace}

            El enlace caduca en {plazo} y solo sirve una vez. Si se te pasa el
            plazo, pídele a {quienInvita} que te lo reenvíe.

            Si no esperabas este correo, no hagas nada: sin abrir el enlace, la
            cuenta no se puede usar.
            """;

        return new CorreoCompuesto(asunto, html, texto);
    }


    /// <summary>
    /// El enlace para elegir contraseña nueva, a quien ya había entrado antes.
    ///
    /// DICE QUE EL SEGUNDO FACTOR TAMBIÉN SE BORRÓ, y esa frase depende de algo
    /// que pasa en otro archivo: el endpoint que manda este correo
    /// —`POST /api/usuarios/{id}/invitacion`— llama SIEMPRE a
    /// `reiniciar_segundo_factor` antes. Si algún día esa llamada desaparece de
    /// allí, esta frase pasa a ser mentira y hay que quitarla de aquí.
    ///
    /// El alta de un usuario nuevo no llega a esta plantilla: usa `Invitacion`,
    /// porque quien nunca entró no tiene segundo factor que perder.
    /// </summary>
    public static CorreoCompuesto Restablecer(string nombre, string enlace, int horas)
    {
        var n     = WebUtility.HtmlEncode(nombre);
        var plazo = TextoPlazo(horas);

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Hola, {n}</h1>

            <p style="margin:0 0 24px;">
              Se pidió restablecer tu contraseña. Elige una nueva aquí:
            </p>

            {Boton(enlace, "Elegir contraseña nueva")}

            <p style="margin:24px 0 16px;color:#4a5a67;font-size:14px;">
              El enlace caduca en <strong>{plazo}</strong> y solo sirve una vez.
            </p>

            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                   style="background:#fdf3e2;border:1px solid #eddcbb;border-radius:4px;">
              <tr><td style="padding:14px 16px;font-size:14px;color:#8a5a09;">
                <strong>Tu segundo factor también se borró.</strong> Al abrir el
                enlace tendrás que escanear un código QR nuevo con tu teléfono, y
                recibirás ocho códigos de recuperación nuevos. Los anteriores ya
                no valen.
              </td></tr>
            </table>

            <p style="margin:16px 0 0;color:#7d8d99;font-size:13px;">
              <strong>Si no lo pediste tú</strong>, ignora este correo: tu
              contraseña actual sigue funcionando y no ha cambiado nada. Pero
              avisa al administrador de tu empresa, porque alguien lo intentó.
            </p>
            """);

        var texto = $"""
            Hola, {nombre}

            Se pidió restablecer tu contraseña. Elige una nueva aquí:

            {enlace}

            El enlace caduca en {plazo} y solo sirve una vez.

            TU SEGUNDO FACTOR TAMBIEN SE BORRO. Al abrir el enlace tendras que
            escanear un codigo QR nuevo con tu telefono, y recibiras ocho codigos
            de recuperacion nuevos. Los anteriores ya no valen.

            Si no lo pediste tu, ignora este correo: tu contrasena actual sigue
            funcionando y no ha cambiado nada. Pero avisa al administrador de tu
            empresa, porque alguien lo intento.
            """;

        return new CorreoCompuesto("Restablecer tu contraseña", html, texto);
    }


    // =========================================================================
    // AVISO DE QUE LE CAMBIARON EL CORREO
    //
    // VA A LA DIRECCIÓN ANTIGUA, NO A LA NUEVA, y ahí está todo el sentido de
    // este correo.
    //
    // Un administrador puede cambiar el correo de alguien y pedir después un
    // enlace de acceso: el enlace iría a la dirección nueva y la cuenta habría
    // cambiado de manos sin que su dueño lo supiera nunca. Es el poder que
    // tiene un administrador y no hay forma de quitárselo — lo que sí se puede
    // es hacer que el dueño se entere.
    //
    // NO LLEVA NINGÚN ENLACE, a propósito. Un correo que avisa de algo raro y
    // además trae un botón es indistinguible de la estafa de la que avisa.
    // =========================================================================
    public static CorreoCompuesto CorreoCambiado(
        string nombre, string anterior, string nuevo, string quienCambia)
    {
        var n = WebUtility.HtmlEncode(nombre);
        var a = WebUtility.HtmlEncode(anterior);
        var u = WebUtility.HtmlEncode(nuevo);
        var q = WebUtility.HtmlEncode(quienCambia);

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Hola, {n}</h1>

            <p style="margin:0 0 16px;">
              <strong>{q}</strong> cambió el correo de tu cuenta.
            </p>

            <table role="presentation" cellpadding="0" cellspacing="0"
                   style="margin:0 0 24px;font-size:14px;">
              <tr>
                <td style="padding:4px 12px 4px 0;color:#7d8d99;">Antes</td>
                <td style="padding:4px 0;">{a}</td>
              </tr>
              <tr>
                <td style="padding:4px 12px 4px 0;color:#7d8d99;">Ahora</td>
                <td style="padding:4px 0;"><strong>{u}</strong></td>
              </tr>
            </table>

            <p style="margin:0 0 16px;color:#4a5a67;font-size:14px;">
              A partir de ahora entrarás con la dirección nueva. Tu contraseña y
              tu segundo factor no han cambiado.
            </p>

            <p style="margin:0;color:#7d8d99;font-size:13px;">
              <strong>Si no sabías nada de esto</strong>, habla con {q} o con
              quien administre la plataforma, por teléfono o en persona — no
              respondiendo a este correo. Este mensaje llega a tu dirección
              anterior justamente para que te enteres.
            </p>
            """);

        var texto = $"""
            Hola, {nombre}

            {quienCambia} cambió el correo de tu cuenta.

              Antes: {anterior}
              Ahora: {nuevo}

            A partir de ahora entrarás con la direccion nueva. Tu contraseña y
            tu segundo factor no han cambiado.

            Si no sabias nada de esto, habla con {quienCambia} o con quien
            administre la plataforma, por telefono o en persona, no respondiendo
            a este correo. Este mensaje llega a tu direccion anterior justamente
            para que te enteres.
            """;

        return new CorreoCompuesto("Cambió el correo de tu cuenta", html, texto);
    }


    // =========================================================================
    // AVISO DE SEGURIDAD — «pasó esto en tu cuenta»
    //
    // UNO PARA LOS CUATRO MOMENTOS en que alguien puede quedarse con una
    // cuenta: cambió la contraseña, se configuró un segundo factor, se
    // generaron códigos de recuperación nuevos, o se borró el segundo factor.
    //
    // Una plantilla por cada uno habría sido cuatro veces el mismo correo con
    // una frase distinta — y cuatro sitios donde olvidarse de la advertencia
    // final, que es lo único que de verdad importa aquí.
    //
    // NO LLEVA ENLACE NI CÓDIGO NI CONTRASEÑA. Ver arriba.
    //
    // VA A LA DIRECCIÓN DE LA CUENTA, que es la del dueño. Si quien hizo el
    // cambio fue un intruso, este correo es lo único que se lo cuenta al dueño,
    // y por eso se manda aunque el cambio lo haya hecho él mismo: un aviso que
    // solo llega cuando hay problema enseña a ignorarlo cuando no lo hay.
    // =========================================================================
    public static CorreoCompuesto AvisoDeSeguridad(
        string nombre, string queePaso, string consecuencia)
    {
        var n = WebUtility.HtmlEncode(nombre);
        var q = WebUtility.HtmlEncode(queePaso);
        var c = WebUtility.HtmlEncode(consecuencia);

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Hola, {n}</h1>

            <p style="margin:0 0 16px;font-size:16px;">
              <strong>{q}</strong>
            </p>

            <p style="margin:0 0 24px;color:#4a5a67;">
              {c}
            </p>

            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                   style="background:#fdf3e2;border:1px solid #eddcbb;border-radius:4px;">
              <tr><td style="padding:14px 16px;font-size:14px;color:#8a5a09;">
                <strong>Si no fuiste tú</strong>, alguien tiene acceso a tu
                cuenta. Habla con el administrador de tu empresa por teléfono o
                en persona — no respondiendo a este correo.
              </td></tr>
            </table>

            <p style="margin:24px 0 0;color:#7d8d99;font-size:12px;">
              Este aviso no lleva ningún enlace ni ningún código, y nunca los
              llevará. Si recibes uno que sí los trae, no es nuestro.
            </p>
            """);

        var texto = $"""
            Hola, {nombre}

            {queePaso}

            {consecuencia}

            Si no fuiste tu, alguien tiene acceso a tu cuenta. Habla con el
            administrador de tu empresa por telefono o en persona, no
            respondiendo a este correo.

            Este aviso no lleva ningun enlace ni ningun codigo, y nunca los
            llevara. Si recibes uno que si los trae, no es nuestro.
            """;

        return new CorreoCompuesto(queePaso, html, texto);
    }


    // =========================================================================
    // EL MAQUETADO
    //
    // Tablas y estilos en línea, que en 2026 sigue siendo así: los clientes de
    // correo no entienden hojas de estilo externas, y varios recortan la
    // etiqueta <style> entera.
    // =========================================================================

    private static string Marco(string contenido) => $"""
        <!doctype html>
        <html lang="es"><body style="margin:0;padding:24px;background:#f3f5f7;
              font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;
              color:#16202a;line-height:1.55;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                 style="max-width:520px;margin:0 auto;background:#ffffff;
                        border:1px solid #d3dbe1;border-radius:6px;">
            <tr><td style="padding:32px 28px;">
              {contenido}
            </td></tr>
          </table>
        </body></html>
        """;

    private static string Boton(string enlace, string texto) => $"""
        <table role="presentation" cellpadding="0" cellspacing="0">
          <tr><td style="background:#0f6f6c;border-radius:4px;">
            <a href="{WebUtility.HtmlEncode(enlace)}"
               style="display:inline-block;padding:12px 22px;color:#ffffff;
                      text-decoration:none;font-weight:500;">{texto}</a>
          </td></tr>
        </table>

        <p style="margin:16px 0 0;color:#7d8d99;font-size:12px;word-break:break-all;">
          Si el botón no funciona, copia esta dirección en tu navegador:<br>
          {WebUtility.HtmlEncode(enlace)}
        </p>
        """;

    private static string TextoPlazo(int horas) => horas switch
    {
        1        => "1 hora",
        24       => "24 horas",
        48       => "2 días",
        72       => "3 días",
        168      => "7 días",
        < 24     => $"{horas} horas",
        _ when horas % 24 == 0 => $"{horas / 24} días",
        _        => $"{horas} horas",
    };
}
