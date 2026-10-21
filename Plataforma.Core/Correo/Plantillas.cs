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
    // CONFIRMAR EL REGISTRO (migración 016)
    //
    // ABRE UNA PUERTA, como la invitación: lleva el enlace de un uso a la misma
    // pantalla (/invitacion/...), donde elige contraseña y segundo factor. Es
    // la prueba de que el correo es suyo; hasta entonces la cuenta no existe
    // para entrar.
    // =========================================================================
    public static CorreoCompuesto ConfirmarRegistro(
        string nombre, string tipoCuenta, string enlace, int horas)
    {
        var n = WebUtility.HtmlEncode(nombre);
        var t = WebUtility.HtmlEncode(tipoCuenta);
        var plazo = TextoPlazo(horas);

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Hola, {n}</h1>

            <p style="margin:0 0 16px;">
              Creaste una <strong>{t}</strong>. Confirma que este correo es tuyo
              y elige tu contraseña:
            </p>

            {Boton(enlace, "Confirmar mi correo")}

            <p style="margin:24px 0 8px;color:#4a5a67;font-size:14px;">
              El enlace caduca en <strong>{plazo}</strong> y solo sirve una vez.
              Si se te pasa, pide otro en la pantalla de registro.
            </p>

            <p style="margin:0;color:#7d8d99;font-size:13px;">
              Si no fuiste tú, ignora este correo: sin confirmarlo, la cuenta no
              se puede usar.
            </p>
            """);

        var texto = $"""
            Hola, {nombre}

            Creaste una {tipoCuenta}. Confirma que este correo es tuyo y elige
            tu contraseña aquí:

            {enlace}

            El enlace caduca en {plazo} y solo sirve una vez. Si se te pasa,
            pide otro en la pantalla de registro.

            Si no fuiste tu, ignora este correo: sin confirmarlo, la cuenta no
            se puede usar.
            """;

        return new CorreoCompuesto("Confirma tu correo para empezar a facturar", html, texto);
    }


    // =========================================================================
    // AVISO AL SUPER ADMINISTRADOR: ALGUIEN SE REGISTRÓ
    //
    // SIN ENLACE, como todos los avisos. Lo que hay que hacer —revisar o
    // suspender— se hace entrando a la plataforma, en «Cuentas».
    // =========================================================================
    public static CorreoCompuesto AvisoRegistroNuevo(
        string tipoCuenta, string titular, string documento, string correo, string alias)
    {
        var t = WebUtility.HtmlEncode(tipoCuenta);
        var ti = WebUtility.HtmlEncode(titular);
        var d = WebUtility.HtmlEncode(documento);
        var c = WebUtility.HtmlEncode(correo);
        var a = WebUtility.HtmlEncode(alias);

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Registro nuevo</h1>

            <table role="presentation" cellpadding="0" cellspacing="0"
                   style="margin:0 0 20px;font-size:14px;">
              <tr><td style="padding:4px 12px 4px 0;color:#7d8d99;">Cuenta</td><td><strong>{t}</strong></td></tr>
              <tr><td style="padding:4px 12px 4px 0;color:#7d8d99;">Titular</td><td>{ti}</td></tr>
              <tr><td style="padding:4px 12px 4px 0;color:#7d8d99;">Documento</td><td>{d}</td></tr>
              <tr><td style="padding:4px 12px 4px 0;color:#7d8d99;">Correo</td><td>{c}</td></tr>
              <tr><td style="padding:4px 12px 4px 0;color:#7d8d99;">Alias</td><td>@{a}</td></tr>
            </table>

            <p style="margin:0;color:#4a5a67;font-size:14px;">
              Todavía no confirmó su correo. Si algo no cuadra, suspéndela desde
              la plataforma, en <strong>Cuentas</strong>.
            </p>
            """);

        var texto = $"""
            Registro nuevo

              Cuenta:    {tipoCuenta}
              Titular:   {titular}
              Documento: {documento}
              Correo:    {correo}
              Alias:     @{alias}

            Todavia no confirmo su correo. Si algo no cuadra, suspendela desde
            la plataforma, en Cuentas.
            """;

        return new CorreoCompuesto($"Registro nuevo: {tipoCuenta} de {titular}", html, texto);
    }


    // =========================================================================
    // «QUITAR DE MI CARTERA» (fase 3): al correo del titular de la empresa.
    //
    // SIN ENLACE, como todos los avisos. Recuperarla («Seguir yo mismo») es la
    // fase 4; hasta entonces, el aviso dice que nada se perdió y qué hacer.
    // =========================================================================
    public static CorreoCompuesto EmpresaEnPausa(
        string empresa, string quien, string motivo, string? enlace = null, int horas = 168)
    {
        var e = WebUtility.HtmlEncode(empresa);
        var q = WebUtility.HtmlEncode(quien);
        var m = WebUtility.HtmlEncode(motivo);
        var plazo = TextoPlazo(horas);

        // Con enlace (fase 4): «Seguir yo mismo». Sin él (por si no se pudo
        // crear), lo de antes: responder al correo.
        var seguir = enlace is null
            ? """
              <p style="margin:0;color:#4a5a67;font-size:14px;">
                Para seguir facturando tú mismo, responde a este correo y te ayudamos.
              </p>
              """
            : $"""
              <p style="margin:0 0 12px;"><strong>¿Quieres seguir facturando tú mismo?</strong>
                Crea tu cuenta (o entra con la que tienes) y la empresa pasa a ti, con todo su historial.</p>

              {Boton(enlace, "Seguir yo mismo")}

              <p style="margin:16px 0 0;color:#4a5a67;font-size:14px;">
                El botón vale <strong>{plazo}</strong>. Si vence, pide otro en la pantalla de ingreso:
                «¿Tu empresa está en pausa? Recupérala».
              </p>
              """;

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">{e} quedó en pausa</h1>

            <p style="margin:0 0 16px;">
              <strong>{q}</strong> dejó de gestionar la facturación electrónica de
              <strong>{e}</strong> en nuestra plataforma.
            </p>

            <p style="margin:0 0 16px;color:#4a5a67;">Motivo: «{m}»</p>

            <p style="margin:0 0 16px;">
              <strong>Tus comprobantes están a salvo:</strong> no se borró nada. Mientras
              esté en pausa, nadie puede emitir a nombre de tu empresa.
            </p>

            <p style="margin:0 0 16px;">
              Te recomendamos <strong>cambiar la clave de tu usuario SOL secundario</strong>:
              quien gestionaba tu empresa la conocía.
            </p>

            {seguir}
            """);

        var texto = $"""
            {empresa} quedó en pausa

            {quien} dejó de gestionar la facturación electrónica de {empresa}.
            Motivo: "{motivo}"

            Tus comprobantes están a salvo: no se borró nada. Mientras esté en
            pausa, nadie puede emitir a nombre de tu empresa.

            Te recomendamos cambiar la clave de tu usuario SOL secundario: quien
            gestionaba tu empresa la conocía.

            {(enlace is null
                ? "Para seguir facturando tú mismo, responde a este correo y te ayudamos."
                : $"Para seguir facturando tú mismo (vale {plazo}):\n{enlace}")}
            """;

        return new CorreoCompuesto($"{empresa} quedó en pausa: tus comprobantes están a salvo", html, texto);
    }


    // =========================================================================
    // RECUPERA TU EMPRESA (fase 4): el enlace pedido con «Recupérala» o el
    // recordatorio de cada 30 días. Va SOLO al correo del titular.
    // =========================================================================
    public static CorreoCompuesto RecuperarEmpresa(
        string empresa, string ruc, string enlace, int horas, int? recordatorio = null)
    {
        var e = WebUtility.HtmlEncode(empresa);
        var plazo = TextoPlazo(horas);
        var intro = recordatorio is null
            ? "Pediste recuperar tu empresa. Con este botón la pasas a tu cuenta, con todo su historial:"
            : "Tu empresa sigue en pausa y a salvo. Cuando quieras seguir facturando, recupérala con este botón:";

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Recupera {e}</h1>

            <p style="margin:0 0 8px;color:#4a5a67;font-size:14px;">RUC {WebUtility.HtmlEncode(ruc)}</p>

            <p style="margin:0 0 16px;">{intro}</p>

            {Boton(enlace, "Recuperar mi empresa")}

            <p style="margin:24px 0 8px;color:#4a5a67;font-size:14px;">
              El enlace vale <strong>{plazo}</strong> y sirve una sola vez.
            </p>

            <p style="margin:0;color:#7d8d99;font-size:13px;">
              Si no lo pediste tú, ignora este correo: sin usar el enlace no cambia nada.
            </p>
            """);

        var texto = $"""
            Recupera {empresa} (RUC {ruc})

            {intro}

            {enlace}

            El enlace vale {plazo} y sirve una sola vez. Si no lo pediste tú,
            ignora este correo: sin usar el enlace no cambia nada.
            """;

        var asunto = recordatorio is null
            ? $"Recupera {empresa}"
            : $"{empresa} sigue en pausa: recupérala cuando quieras";

        return new CorreoCompuesto(asunto, html, texto);
    }


    // =========================================================================
    // AVISO A LA CUENTA QUE LA TENÍA: la empresa volvió a su titular. Sin
    // enlace: no hay nada que hacer.
    // =========================================================================
    public static CorreoCompuesto EmpresaRecuperada(string nombre, string empresa)
    {
        var n = WebUtility.HtmlEncode(nombre);
        var e = WebUtility.HtmlEncode(empresa);

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">{e} volvió a su titular</h1>

            <p style="margin:0 0 16px;">Hola, {n}. El titular de <strong>{e}</strong> recuperó la
              empresa con el enlace de su correo. Ya no está en tu cartera.</p>

            <p style="margin:0;color:#4a5a67;font-size:14px;">
              Si te pide que la sigas llevando, te invitará desde su cuenta.
            </p>
            """);

        var texto = $"""
            {empresa} volvió a su titular

            Hola, {nombre}. El titular de {empresa} recuperó la empresa con el
            enlace de su correo. Ya no está en tu cartera.

            Si te pide que la sigas llevando, te invitará desde su cuenta.
            """;

        return new CorreoCompuesto($"{empresa} volvió a su titular", html, texto);
    }


    // =========================================================================
    // RECLAMO CON DOCUMENTOS (fase 4b, camino C)
    // =========================================================================

    /// <summary>A quien reclamó: lo recibimos.</summary>
    public static CorreoCompuesto ReclamoRecibido(string nombre, string empresa, int numero)
    {
        var n = WebUtility.HtmlEncode(nombre);
        var e = WebUtility.HtmlEncode(empresa);
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Recibimos tu solicitud N.º {numero:0000}</h1>
            <p style="margin:0 0 16px;">Hola, {n}. Pediste recuperar <strong>{e}</strong> con tus documentos.
              Una persona los revisa y te respondemos a este correo en 3 días hábiles.</p>
            <p style="margin:0;color:#4a5a67;font-size:14px;">Mientras tanto no tienes que hacer nada.</p>
            """);
        var texto = $"""
            Recibimos tu solicitud N.º {numero:0000}

            Hola, {nombre}. Pediste recuperar {empresa} con tus documentos. Una
            persona los revisa y te respondemos a este correo en 3 días hábiles.
            """;
        return new CorreoCompuesto($"Solicitud N.º {numero:0000}: recibimos tus documentos", html, texto);
    }

    /// <summary>A la cuenta que hoy tiene la empresa: piden el traspaso; plazo para responder.</summary>
    public static CorreoCompuesto ReclamoAvisoCuenta(string nombre, string empresa, string reclamante, string relacion, string plazo)
    {
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Piden el traspaso de {WebUtility.HtmlEncode(empresa)}</h1>
            <p style="margin:0 0 16px;">Hola, {WebUtility.HtmlEncode(nombre)}.
              <strong>{WebUtility.HtmlEncode(reclamante)}</strong> ({WebUtility.HtmlEncode(relacion.ToLowerInvariant())})
              presentó documentos para recuperar la empresa, que hoy está en tu cuenta.</p>
            <p style="margin:0 0 16px;">Si crees que es un error, <strong>respóndenos desde la plataforma antes del {WebUtility.HtmlEncode(plazo)}</strong>:
              en Mis empresas (o en tu Inicio) verás el aviso con el botón «Responder». Si no, lo revisaremos con lo que hay.</p>
            <p style="margin:0;color:#4a5a67;font-size:14px;">No se borra nada: si se aprueba, la empresa pasa entera a su titular.</p>
            """);
        var texto = $"""
            Piden el traspaso de {empresa}

            Hola, {nombre}. {reclamante} ({relacion.ToLowerInvariant()}) presentó documentos
            para recuperar la empresa, que hoy está en tu cuenta.

            Si crees que es un error, respóndenos desde la plataforma antes del
            {plazo}: en Mis empresas (o en tu Inicio) verás el aviso con el botón
            «Responder». Si no, lo revisaremos con lo que hay.
            """;
        return new CorreoCompuesto($"Piden el traspaso de {empresa}", html, texto);
    }

    /// <summary>Al super administrador: hay una solicitud por revisar. Sin enlace.</summary>
    public static CorreoCompuesto ReclamoAvisoSuper(string empresa, string ruc, int numero)
    {
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Solicitud de traspaso N.º {numero:0000}</h1>
            <p style="margin:0 0 16px;">Piden recuperar <strong>{WebUtility.HtmlEncode(empresa)}</strong> (RUC {WebUtility.HtmlEncode(ruc)}) con documentos.</p>
            <p style="margin:0;color:#4a5a67;font-size:14px;">Revísala en la plataforma: Plataforma → Solicitudes de traspaso.</p>
            """);
        var texto = $"""
            Solicitud de traspaso N.º {numero:0000}

            Piden recuperar {empresa} (RUC {ruc}) con documentos. Revísala en la
            plataforma: Plataforma → Solicitudes de traspaso.
            """;
        return new CorreoCompuesto($"Traspaso por revisar: {empresa}", html, texto);
    }

    /// <summary>A quien reclamó: aprobado, con el enlace para recuperarla.</summary>
    public static CorreoCompuesto ReclamoAprobado(string nombre, string empresa, string ruc, string enlace, int horas)
    {
        var plazo = TextoPlazo(horas);
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Aprobamos tu solicitud</h1>
            <p style="margin:0 0 16px;">Hola, {WebUtility.HtmlEncode(nombre)}. Revisamos tus documentos:
              <strong>{WebUtility.HtmlEncode(empresa)}</strong> (RUC {WebUtility.HtmlEncode(ruc)}) es tuya.
              Este correo es ahora el del titular.</p>
            <p style="margin:0 0 16px;">Crea tu cuenta (o entra con la que tienes) y la empresa pasa a ti, con todo su historial:</p>
            {Boton(enlace, "Recuperar mi empresa")}
            <p style="margin:24px 0 0;color:#4a5a67;font-size:14px;">El enlace vale <strong>{plazo}</strong> y sirve una sola vez.
              Te recomendamos cambiar la clave de tu usuario SOL secundario.</p>
            """);
        var texto = $"""
            Aprobamos tu solicitud

            Hola, {nombre}. Revisamos tus documentos: {empresa} (RUC {ruc}) es tuya.
            Este correo es ahora el del titular.

            Crea tu cuenta (o entra con la que tienes) y la empresa pasa a ti:
            {enlace}

            El enlace vale {plazo} y sirve una sola vez.
            """;
        return new CorreoCompuesto($"{empresa} es tuya: recupérala", html, texto);
    }

    /// <summary>A quien reclamó: rechazado, con el motivo.</summary>
    public static CorreoCompuesto ReclamoRechazado(string nombre, string empresa, string motivo)
    {
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">No pudimos aprobar tu solicitud</h1>
            <p style="margin:0 0 16px;">Hola, {WebUtility.HtmlEncode(nombre)}. Revisamos tus documentos para recuperar
              <strong>{WebUtility.HtmlEncode(empresa)}</strong> y no pudimos aprobarla:</p>
            <blockquote style="margin:0 0 16px;padding:10px 14px;border-left:3px solid #d3dbe1;color:#4a5a67;">{WebUtility.HtmlEncode(motivo)}</blockquote>
            <p style="margin:0;color:#4a5a67;font-size:14px;">Si lo corriges, puedes enviar una solicitud nueva.</p>
            """);
        var texto = $"""
            No pudimos aprobar tu solicitud

            Hola, {nombre}. Revisamos tus documentos para recuperar {empresa} y no
            pudimos aprobarla:

            "{motivo}"

            Si lo corriges, puedes enviar una solicitud nueva.
            """;
        return new CorreoCompuesto($"Tu solicitud para recuperar {empresa}", html, texto);
    }

    /// <summary>A la cuenta que la tenía: se aprobó el traspaso.</summary>
    public static CorreoCompuesto EmpresaTraspasada(string nombre, string empresa)
    {
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">{WebUtility.HtmlEncode(empresa)} pasa a su titular</h1>
            <p style="margin:0 0 16px;">Hola, {WebUtility.HtmlEncode(nombre)}. Revisamos los documentos del titular y aprobamos
              el traspaso. La empresa quedó en pausa y ya no está en tu cartera.</p>
            <p style="margin:0;color:#4a5a67;font-size:14px;">No se borró nada. Si el titular quiere que la sigas llevando, te invitará.</p>
            """);
        var texto = $"""
            {empresa} pasa a su titular

            Hola, {nombre}. Revisamos los documentos del titular y aprobamos el
            traspaso. La empresa quedó en pausa y ya no está en tu cartera.
            """;
        return new CorreoCompuesto($"{empresa} pasa a su titular", html, texto);
    }


    // =========================================================================
    // FASE 4d: LA RESPUESTA AL RECLAMO Y EL CAMINO B
    // =========================================================================

    /// <summary>Al super administrador: la cuenta respondió un reclamo. Sin enlace.</summary>
    public static CorreoCompuesto ReclamoRespondido(string empresa, int numero, bool deAcuerdo)
    {
        var que = deAcuerdo ? "está de acuerdo" : "se opone";
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Respondieron la solicitud N.º {numero:0000}</h1>
            <p style="margin:0 0 16px;">La cuenta que tiene <strong>{WebUtility.HtmlEncode(empresa)}</strong> <strong>{que}</strong> con el traspaso.</p>
            <p style="margin:0;color:#4a5a67;font-size:14px;">Lee su respuesta en la plataforma: Plataforma → Solicitudes de traspaso.</p>
            """);
        var texto = $"""
            Respondieron la solicitud N.º {numero:0000}

            La cuenta que tiene {empresa} {que} con el traspaso. Lee su respuesta en
            la plataforma: Plataforma → Solicitudes de traspaso.
            """;
        return new CorreoCompuesto($"Solicitud N.º {numero:0000}: la cuenta {que}", html, texto);
    }

    /// <summary>Al titular: una cuenta pide llevar su empresa. Un solo botón: la decisión está en la página.</summary>
    public static CorreoCompuesto PedidoAlTitular(
        string quien, string cuenta, string empresa, string ruc, string mensaje, string enlace, int horas)
    {
        var q = WebUtility.HtmlEncode(quien);
        var c = WebUtility.HtmlEncode(cuenta);
        var e = WebUtility.HtmlEncode(empresa);
        var plazo = TextoPlazo(horas);
        var deQuien = string.IsNullOrWhiteSpace(cuenta) || cuenta == quien ? q : $"{q}, de <strong>{c}</strong>,";
        var deQuienTexto = string.IsNullOrWhiteSpace(cuenta) || cuenta == quien ? quien : $"{quien}, de {cuenta},";
        var cita = string.IsNullOrWhiteSpace(mensaje) ? "" :
            $"""<blockquote style="margin:0 0 16px;padding:10px 14px;background:#f3f5f7;border-radius:6px;color:#4a5a67;font-style:italic;">«{WebUtility.HtmlEncode(mensaje)}»</blockquote>""";

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">¿Quieres que {c} lleve tu empresa?</h1>
            <p style="margin:0 0 16px;"><strong>{deQuien}</strong> pidió llevar la facturación de <strong>{e}</strong>
              (RUC {WebUtility.HtmlEncode(ruc)}) en Reyban.</p>
            {cita}
            <p style="margin:0 0 16px;">Te escribimos porque eres el titular. <strong>Nada cambia hasta que tú lo apruebes.</strong></p>
            {Boton(enlace, "Revisar y responder")}
            <p style="margin:24px 0 0;color:#4a5a67;font-size:14px;">El enlace vale <strong>{plazo}</strong> y sirve una vez.
              Si no lo esperabas o no conoces a quien lo pide, ignora este correo: la empresa sigue donde está.</p>
            """);
        var texto = $"""
            ¿Quieres que {cuenta} lleve tu empresa?

            {deQuienTexto} pidió llevar la facturación de {empresa} (RUC {ruc}) en Reyban.
            {(string.IsNullOrWhiteSpace(mensaje) ? "" : $"\n«{mensaje}»\n")}
            Te escribimos porque eres el titular. Nada cambia hasta que tú lo apruebes.

            Revisar y responder: {enlace}

            El enlace vale {plazo} y sirve una vez. Si no lo esperabas, ignora este
            correo: la empresa sigue donde está.
            """;
        return new CorreoCompuesto($"{cuenta} pide llevar {empresa}", html, texto);
    }

    /// <summary>A quien pidió: el titular aprobó.</summary>
    public static CorreoCompuesto PedidoAprobado(string nombre, string empresa)
    {
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">{WebUtility.HtmlEncode(empresa)} ya está en tu cartera</h1>
            <p style="margin:0 0 16px;">Hola, {WebUtility.HtmlEncode(nombre)}. Su titular aprobó tu pedido.
              La encuentras en Mis empresas, con todo su historial.</p>
            <p style="margin:0;color:#4a5a67;font-size:14px;">Revisa sus series y su clave SOL antes de emitir.</p>
            """);
        var texto = $"""
            {empresa} ya está en tu cartera

            Hola, {nombre}. Su titular aprobó tu pedido. La encuentras en Mis
            empresas, con todo su historial. Revisa sus series y su clave SOL antes
            de emitir.
            """;
        return new CorreoCompuesto($"{empresa} ya está en tu cartera", html, texto);
    }

    /// <summary>A quien pidió: el titular dijo que no. Sin más detalle.</summary>
    public static CorreoCompuesto PedidoRechazado(string nombre, string empresa)
    {
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">El titular no aceptó</h1>
            <p style="margin:0 0 16px;">Hola, {WebUtility.HtmlEncode(nombre)}. El titular de <strong>{WebUtility.HtmlEncode(empresa)}</strong>
              no aceptó tu pedido. La empresa sigue donde estaba.</p>
            <p style="margin:0;color:#4a5a67;font-size:14px;">Si fue un error, habla con él y vuelve a pedirla.</p>
            """);
        var texto = $"""
            El titular no aceptó

            Hola, {nombre}. El titular de {empresa} no aceptó tu pedido. La empresa
            sigue donde estaba. Si fue un error, habla con él y vuelve a pedirla.
            """;
        return new CorreoCompuesto($"{empresa}: el titular no aceptó", html, texto);
    }

    /// <summary>A la cuenta que la tenía: el titular la confió a otra cuenta.</summary>
    public static CorreoCompuesto EmpresaPasoAOtraCartera(string nombre, string empresa)
    {
        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">{WebUtility.HtmlEncode(empresa)} pasó a otra cartera</h1>
            <p style="margin:0 0 16px;">Hola, {WebUtility.HtmlEncode(nombre)}. El titular de <strong>{WebUtility.HtmlEncode(empresa)}</strong>
              decidió que la lleve otra cuenta. Ya no está en tu cartera.</p>
            <p style="margin:0;color:#4a5a67;font-size:14px;">No se borró nada: lo que emitiste sigue en la empresa, con su historial.</p>
            """);
        var texto = $"""
            {empresa} pasó a otra cartera

            Hola, {nombre}. El titular de {empresa} decidió que la lleve otra cuenta.
            Ya no está en tu cartera. No se borró nada.
            """;
        return new CorreoCompuesto($"{empresa} pasó a otra cartera por decisión de su titular", html, texto);
    }


    // =========================================================================
    // 038 — CAMBIAR LA CONTRASEÑA, PEDIDO DESDE EL LOGIN
    //
    // Solo la contraseña: el segundo factor NO se toca (al contrario que
    // `Restablecer`, que es el enlace que manda un administrador y rehace todo).
    // =========================================================================
    public static CorreoCompuesto RecuperarClave(string nombre, string enlace, int horas)
    {
        var n     = WebUtility.HtmlEncode(nombre);
        var plazo = TextoPlazo(horas);

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Hola, {n}</h1>

            <p style="margin:0 0 24px;">
              Pediste cambiar tu contraseña. Elige una nueva aquí:
            </p>

            {Boton(enlace, "Elegir contraseña nueva")}

            <p style="margin:24px 0 16px;color:#4a5a67;font-size:14px;">
              El enlace caduca en <strong>{plazo}</strong> y solo sirve una vez.
              Tu segundo factor no cambia: al entrar te pedirá el código de tu
              teléfono, como siempre.
            </p>

            <p style="margin:16px 0 0;color:#7d8d99;font-size:13px;">
              <strong>Si no lo pediste tú</strong>, ignora este correo: tu
              contraseña actual sigue funcionando y no ha cambiado nada.
            </p>
            """);

        var texto = $"""
            Hola, {nombre}

            Pediste cambiar tu contraseña. Elige una nueva aquí:

            {enlace}

            El enlace caduca en {plazo} y solo sirve una vez. Tu segundo factor
            no cambia: al entrar te pedira el codigo de tu telefono, como siempre.

            Si no lo pediste tu, ignora este correo: tu contrasena actual sigue
            funcionando y no ha cambiado nada.
            """;

        return new CorreoCompuesto("Cambia tu contraseña", html, texto);
    }


    // =========================================================================
    // 039 — CONFIRMAR LA RECUPERACIÓN DEL SEGUNDO FACTOR
    //
    // ABRE UNA PUERTA, por eso lleva enlace (a la propia dirección de la
    // cuenta, igual que cambiar la contraseña). Al confirmarlo empieza la
    // espera y se cierran todas sus sesiones.
    // =========================================================================
    public static CorreoCompuesto ConfirmarRecuperacion2fa(
        string nombre, string enlace, int horasEnlace, string espera)
    {
        var n     = WebUtility.HtmlEncode(nombre);
        var e     = WebUtility.HtmlEncode(espera);
        var plazo = TextoPlazo(horasEnlace);

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Hola, {n}</h1>

            <p style="margin:0 0 16px;">
              Pediste recuperar tu segundo factor porque perdiste tu teléfono y
              tus códigos de recuperación.
            </p>

            <p style="margin:0 0 24px;color:#4a5a67;">
              Al confirmar: se cierran <strong>todas</strong> tus sesiones
              (también la de un equipo perdido o robado) y empieza una espera de
              <strong>{e}</strong>. Pasada la espera, entras con tu contraseña y
              configuras un teléfono nuevo. Avisaremos también a tu administrador.
            </p>

            {Boton(enlace, "Confirmar y empezar la espera")}

            <p style="margin:24px 0 16px;color:#4a5a67;font-size:14px;">
              El enlace caduca en <strong>{plazo}</strong> y solo sirve una vez.
            </p>

            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                   style="background:#fdf3e2;border:1px solid #eddcbb;border-radius:4px;">
              <tr><td style="padding:14px 16px;font-size:14px;color:#8a5a09;">
                <strong>Si no lo pediste tú</strong>, no lo abras: alguien conoce
                tu contraseña. Entra y cámbiala desde tu perfil.
              </td></tr>
            </table>
            """);

        var texto = $"""
            Hola, {nombre}

            Pediste recuperar tu segundo factor porque perdiste tu telefono y tus
            codigos de recuperacion.

            Al confirmar se cierran todas tus sesiones (tambien la de un equipo
            perdido o robado) y empieza una espera de {espera}. Pasada la espera,
            entras con tu contrasena y configuras un telefono nuevo. Avisaremos
            tambien a tu administrador.

            Confirmar y empezar la espera:
            {enlace}

            El enlace caduca en {plazo} y solo sirve una vez.

            Si no lo pediste tu, no lo abras: alguien conoce tu contrasena.
            Entra y cambiala desde tu perfil.
            """;

        return new CorreoCompuesto("Confirma la recuperación de tu segundo factor", html, texto);
    }


    // =========================================================================
    // 039 — AVISO A LOS ADMINISTRADORES (o a los super administradores)
    //
    // SIN ENLACE, como todos los avisos. Se cancela desde dentro: Usuarios, o
    // Plataforma › Recuperaciones.
    // =========================================================================
    public static CorreoCompuesto AvisoRecuperacionTercero(string empresa, string quePaso)
    {
        var em = WebUtility.HtmlEncode(empresa);
        var q  = WebUtility.HtmlEncode(quePaso);

        var html = Marco($"""
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">Aviso de seguridad · {em}</h1>

            <p style="margin:0 0 24px;font-size:16px;">{q}</p>

            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                   style="background:#fdf3e2;border:1px solid #eddcbb;border-radius:4px;">
              <tr><td style="padding:14px 16px;font-size:14px;color:#8a5a09;">
                <strong>Si te parece raro</strong>, confírmalo con esa persona por
                teléfono o en persona. Para cancelarlo, entra a la plataforma:
                <strong>Usuarios</strong> (o, si eres super administrador,
                <strong>Plataforma › Recuperaciones</strong>).
              </td></tr>
            </table>

            <p style="margin:24px 0 0;color:#7d8d99;font-size:12px;">
              Este aviso no lleva ningún enlace ni ningún código, y nunca los
              llevará. Si recibes uno que sí los trae, no es nuestro.
            </p>
            """);

        var texto = $"""
            Aviso de seguridad - {empresa}

            {quePaso}

            Si te parece raro, confirmalo con esa persona por telefono o en
            persona. Para cancelarlo, entra a la plataforma: Usuarios (o, si eres
            super administrador, Plataforma > Recuperaciones).

            Este aviso no lleva ningun enlace ni ningun codigo, y nunca los
            llevara. Si recibes uno que si los trae, no es nuestro.
            """;

        return new CorreoCompuesto($"Aviso de seguridad: recuperación de segundo factor ({empresa})", html, texto);
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