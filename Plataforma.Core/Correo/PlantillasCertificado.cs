using System.Net;

namespace Plataforma.Core.Correo;

/// <summary>
/// El correo de que vence el certificado digital de una empresa (paso 6.3).
///
/// EN SU PROPIO ARCHIVO y no dentro de <see cref="Plantillas"/>: así esta
/// entrega no toca un archivo que crece con cada fase. El marco es el mismo,
/// copiado tal cual (allí es privado).
///
/// ES UN AVISO, ASÍ QUE NO LLEVA ENLACES (ver la regla en Plantillas): dice qué
/// pasa y qué hacer, y dónde, con palabras. Un correo de «tu certificado vence,
/// pulsa aquí» es exactamente el que copiaría un estafador.
/// </summary>
public static class PlantillasCertificado
{
    /// <param name="empresa">El nombre de la empresa (el que se ve en la plataforma).</param>
    /// <param name="ruc">Su RUC, para que no haya duda de cuál es.</param>
    /// <param name="validoHasta">El último día que vale, en hora de Lima.</param>
    /// <param name="dias">Los que le quedan: 30, 15, 7, 1, 0 (hoy) o negativo (ya venció).</param>
    public static CorreoCompuesto VencimientoCertificado(
        string empresa, string? ruc, DateOnly validoHasta, int dias)
    {
        var e = WebUtility.HtmlEncode(empresa);
        var r = string.IsNullOrWhiteSpace(ruc) ? "" : $" (RUC {WebUtility.HtmlEncode(ruc)})";
        var rTexto = string.IsNullOrWhiteSpace(ruc) ? "" : $" (RUC {ruc})";
        var f = Fecha(validoHasta);

        var (asunto, titulo, cuando, color) = dias switch
        {
            < 0 => ($"Venció el certificado digital de {empresa}",
                    "El certificado digital venció",
                    $"venció el <strong>{f}</strong>. Desde ese día <strong>no se puede emitir</strong> ningún comprobante a nombre de la empresa",
                    "#a6301f"),
            0   => ($"Hoy vence el certificado digital de {empresa}",
                    "El certificado digital vence hoy",
                    $"vence <strong>hoy, {f}</strong>. Desde mañana <strong>no se podrá emitir</strong> ningún comprobante a nombre de la empresa",
                    "#a6301f"),
            1   => ($"Mañana vence el certificado digital de {empresa}",
                    "El certificado digital vence mañana",
                    $"vence <strong>mañana, {f}</strong>. Después <strong>no se podrá emitir</strong> ningún comprobante a nombre de la empresa",
                    "#a6301f"),
            _   => ($"El certificado digital de {empresa} vence en {dias} días",
                    $"El certificado digital vence en {dias} días",
                    $"vence el <strong>{f}</strong>, dentro de {dias} días. Cuando venza, ya no se podrá emitir ningún comprobante a nombre de la empresa",
                    dias <= 7 ? "#a6301f" : "#8a5a09")
        };

        var html = Marco($"""
            <p style="margin:0 0 6px;font-size:13px;color:{color};font-weight:600;letter-spacing:.02em;">
              AVISO DE CERTIFICADO DIGITAL
            </p>
            <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">{titulo}</h1>

            <p style="margin:0 0 16px;">
              El certificado digital de <strong>{e}</strong>{r} {cuando}.
            </p>

            <p style="margin:0 0 8px;"><strong>Qué hacer:</strong></p>
            <ol style="margin:0 0 16px;padding-left:20px;">
              <li style="margin-bottom:6px;">Pide la renovación a tu entidad certificadora (la que te vendió el certificado). Suele tardar unos días.</li>
              <li style="margin-bottom:6px;">Cuando te entreguen el archivo nuevo (.pfx) y su contraseña, entra a la plataforma y súbelo en <strong>Configuración</strong>, paso <strong>2 · Certificado digital</strong>. Reemplaza al que vence.</li>
              <li>Comprueba en <strong>Inicio</strong> que el aviso del certificado desapareció.</li>
            </ol>

            <p style="margin:0 0 16px;color:#4a5a67;font-size:14px;">
              Los comprobantes ya emitidos siguen siendo válidos: lo que vence es la
              firma para emitir los nuevos.
            </p>

            <p style="margin:0;color:#7d8d99;font-size:13px;">
              Te llega por administrar {e} en Reyban Facturación. Este aviso sale a los
              30, 15, 7 y 1 días y el día que vence, una sola vez cada uno. Por
              seguridad, nuestros avisos nunca traen enlaces: entra a la plataforma
              como siempre.
            </p>
            """);

        var texto = $"""
            {titulo}

            El certificado digital de {empresa}{rTexto} {Plano(cuando)}.

            Qué hacer:
              1. Pide la renovación a tu entidad certificadora. Suele tardar unos días.
              2. Cuando te entreguen el archivo nuevo (.pfx) y su contraseña, entra a la
                 plataforma y súbelo en Configuración, paso 2 · Certificado digital.
                 Reemplaza al que vence.
              3. Comprueba en Inicio que el aviso del certificado desapareció.

            Los comprobantes ya emitidos siguen siendo válidos: lo que vence es la
            firma para emitir los nuevos.

            Te llega por administrar {empresa} en Reyban Facturación. Este aviso sale
            a los 30, 15, 7 y 1 días y el día que vence, una sola vez cada uno. Por
            seguridad, nuestros avisos nunca traen enlaces.
            """;

        return new CorreoCompuesto(asunto, html, texto);
    }

    private static readonly string[] Meses =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio",
         "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>«13 de septiembre de 2027», sin depender de la cultura del servidor.</summary>
    private static string Fecha(DateOnly d) => $"{d.Day} de {Meses[d.Month - 1]} de {d.Year}";

    private static string Plano(string html) =>
        html.Replace("<strong>", "").Replace("</strong>", "");

    // Igual que el de Plantillas (allí es privado).
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
}