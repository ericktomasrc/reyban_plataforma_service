using System.Globalization;
using System.Net;
using Plataforma.Core.Correo;

namespace Plataforma.Api.Cobros;

/// <summary>
/// 076 — LOS CORREOS DE COBROS: el cobro que viene, el pago recibido, la
/// tarjeta que no pasó, la gracia y el «no puedes emitir».
///
/// COMO LOS DEMÁS AVISOS, SIN ENLACES: dicen qué pasa y dónde hacerlo, con
/// palabras. Un correo de «tu pago falló, pulsa aquí» es exactamente el que
/// copiaría un estafador.
/// </summary>
public static class PlantillasCobros
{
    private const string Pie = "Te llega por ser el titular de una cuenta en Reyban Facturación. Por seguridad, nuestros avisos nunca traen enlaces: entra a la plataforma como siempre, en «Mi plan › Mis pagos».";

    public static CorreoCompuesto Previo(DateOnly fecha, decimal monto, bool conTarjeta) =>
        Armar(conTarjeta ? $"El {Fecha(fecha)} se renueva tu plan ({Soles(monto)})" : $"Tu plan vence el {Fecha(fecha)}",
              "AVISO DE COBRO", conTarjeta ? "Se renueva tu plan" : "Tu plan vence pronto",
              conTarjeta
                ? $"El <strong>{Fecha(fecha)}</strong> cobraremos <strong>{Soles(monto)}</strong> a tu tarjeta guardada. No tienes que hacer nada."
                : $"El <strong>{Fecha(fecha)}</strong> vence tu plan (<strong>{Soles(monto)}</strong>). Para que se cobre solo, deja tu tarjeta en <strong>Mi plan › Mis pagos</strong>, o paga por transferencia.",
              "#0f6f6c");

    public static CorreoCompuesto Pagado(decimal monto, string concepto, DateOnly? hasta, bool prueba) =>
        Armar(prueba ? $"Pago de prueba recibido ({Soles(monto)})" : $"Recibimos tu pago de {Soles(monto)}",
              "PAGO RECIBIDO", "¡Gracias por tu pago!",
              $"Recibimos <strong>{Soles(monto)}</strong> por <strong>{WebUtility.HtmlEncode(concepto)}</strong>."
              + (hasta is { } h ? $" Tu plan queda pagado hasta el <strong>{Fecha(h)}</strong>." : "")
              + (prueba ? " <em>(Es un pago de prueba: no se movió dinero.)</em>" : " Tu factura o boleta te llega en un correo aparte."),
              "#1b6b4a");

    public static CorreoCompuesto Rechazado(decimal monto, string? motivo, int[] reintentos) =>
        Armar($"Tu tarjeta no pasó: no pudimos cobrar {Soles(monto)}",
              "PAGO NO REALIZADO", "Tu tarjeta no pasó",
              $"Intentamos cobrar <strong>{Soles(monto)}</strong> y tu banco no lo aprobó"
              + (string.IsNullOrWhiteSpace(motivo) ? "." : $": {WebUtility.HtmlEncode(motivo)}.")
              + (reintentos.Length > 0 ? $" Lo intentaremos de nuevo los días {string.Join(" y ", reintentos)} después de la fecha de cobro." : "")
              + " Si quieres, cambia tu tarjeta o paga ahora en <strong>Mi plan › Mis pagos</strong>.",
              "#a6301f");

    public static CorreoCompuesto Gracia(DateOnly hasta) =>
        Armar($"Tu cuenta está en días de gracia hasta el {Fecha(hasta)}",
              "DÍAS DE GRACIA", "Ponte al día antes del " + Fecha(hasta),
              $"No pudimos cobrar tu plan. Tienes hasta el <strong>{Fecha(hasta)}</strong> para ponerte al día en <strong>Mi plan › Mis pagos</strong>. "
              + "Después podrás entrar y ver todo, pero no emitir hasta que pagues. No se borra nada.",
              "#8a5a09");

    public static CorreoCompuesto NoEmite() =>
        Armar("Tu cuenta no puede emitir hasta que pagues",
              "EMISIÓN EN PAUSA", "No puedes emitir hasta pagar",
              "Pasaron los días de gracia sin pago. Puedes entrar y ver todos tus comprobantes, pero <strong>no emitir</strong> nuevos. "
              + "Apenas pagues en <strong>Mi plan › Mis pagos</strong>, se desbloquea solo. No se borró nada.",
              "#a6301f");

    /// <summary>081: confirmación de que canceló su plan.</summary>
    public static CorreoCompuesto Cancelado(DateOnly? hasta) =>
        Armar("Cancelaste tu plan de Reyban Facturación",
              "PLAN CANCELADO", "Cancelaste tu plan",
              "No se te cobrará más. "
              + (hasta is { } h && h >= DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5))
                    ? $"Sigues usando todo hasta el <strong>{Fecha(h)}</strong>. Después p"
                    : "P")
              + "odrás entrar, ver y descargar todos tus comprobantes, pero no emitir nuevos. <strong>No se borra nada.</strong> "
              + "Si cambias de idea, reactívalo en <strong>Mi plan</strong> cuando quieras.",
              "#5b6b77");

    // -------------------------------------------------------------------------

    private static CorreoCompuesto Armar(string asunto, string etiqueta, string titulo, string cuerpo, string color)
    {
        var html = $"""
            <!doctype html>
            <html lang="es"><body style="margin:0;padding:24px;background:#f3f5f7;
                  font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;color:#16202a;line-height:1.55;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                     style="max-width:520px;margin:0 auto;background:#ffffff;border:1px solid #d3dbe1;border-radius:6px;">
                <tr><td style="padding:32px 28px;">
                  <p style="margin:0 0 6px;font-size:13px;color:{color};font-weight:600;letter-spacing:.02em;">{etiqueta}</p>
                  <h1 style="margin:0 0 16px;font-size:20px;color:#16202a;">{WebUtility.HtmlEncode(titulo)}</h1>
                  <p style="margin:0 0 16px;">{cuerpo}</p>
                  <p style="margin:0;color:#7d8d99;font-size:13px;">{Pie}</p>
                </td></tr>
              </table>
            </body></html>
            """;
        var texto = $"{titulo}\n\n{Plano(cuerpo)}\n\n{Pie}";
        return new CorreoCompuesto(asunto, html, texto);
    }

    private static readonly string[] Meses =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    private static string Fecha(DateOnly d) => $"{d.Day} de {Meses[d.Month - 1]} de {d.Year}";

    private static string Soles(decimal m) => "S/ " + m.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Plano(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
}