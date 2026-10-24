using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// 23 OCT — LAS REGLAS DE SUNAT ENTRE LA BAJA Y LAS NOTAS, en un solo sitio.
///
///   · Una factura o boleta CON NOTAS (de crédito o de débito) que valen —
///     emitidas o en camino— ya no se da de baja: se sigue con notas.
///   · Con la BAJA PEDIDA y sin respuesta de SUNAT, no se hacen notas (si
///     SUNAT la acepta, el comprobante deja de existir) ni se pide otra baja.
///   · Pasados 7 días ya no hay baja: nota de crédito (EndpointsDocumentos).
///
/// LA BAJA PEDIDA se anota en los datos extra del documento con la clave
/// interna «_bajaPedidaEn» (las que empiezan por «_» no se muestran ni viajan
/// al servicio). Vale 48 horas: la baja de una factura se contesta en minutos
/// y la anulación de una boleta va en el resumen del día siguiente. Si SUNAT
/// la rechazó, pasado ese plazo el comprobante vuelve a admitir notas.
/// </summary>
public static class ReglasBaja
{
    public const string Clave = "_bajaPedidaEn";
    private static readonly TimeSpan Vigencia = TimeSpan.FromHours(48);

    /// <summary>Cuándo se pidió la baja, si está pendiente de SUNAT; null si no.</summary>
    public static DateTime? Pendiente(Documento d)
    {
        if (d.Estado is not (EstadoDocumento.Aceptado or EstadoDocumento.Observado)) return null;
        if (d.DatosExtra is null || !d.DatosExtra.TryGetValue(Clave, out var texto)) return null;
        if (!DateTime.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var en))
            return null;
        return DateTime.UtcNow - en < Vigencia ? en : null;
    }

    /// <summary>Anota que se pidió la baja (el documento tiene que estar siendo seguido por EF).</summary>
    public static void Anotar(Documento d) =>
        d.DatosExtra = new Dictionary<string, string>(d.DatosExtra ?? [])
        {
            [Clave] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

    /// <summary>El motivo, en palabras, para quien quiere hacer una nota con la baja en trámite.</summary>
    public static string TextoPendiente(Documento d, DateTime pedida, string que)
    {
        var lima = pedida.AddHours(-5);
        var como = d.Tipo == "03" || d.Serie?.StartsWith('B') == true ? "la anulación" : "la baja";
        return $"Se pidió {como} de este comprobante el {lima:dd/MM/yyyy} a las {lima:HH:mm}. " +
               $"Mientras SUNAT no conteste no se puede hacer una {que}: si la acepta, el comprobante deja de existir.";
    }

    /// <summary>Cuántas notas (de crédito o de débito) que valen tiene: emitidas, aceptadas o en camino.</summary>
    /// 046: todas, las haga quien las haga (una nota del jefe también impide anular).
    public static Task<int> NotasVigentesAsync(ContextoPlataforma db, Guid id, CancellationToken ct) =>
        SinAlcance.LeerAsync(db, () => db.Documentos.AsNoTracking().CountAsync(x =>
            x.DocumentoRefId == id && (x.Tipo == "07" || x.Tipo == "08") &&
            x.Estado != EstadoDocumento.Borrador && x.Estado != EstadoDocumento.Rechazado &&
            x.Estado != EstadoDocumento.Error && x.Estado != EstadoDocumento.Anulado, ct), ct);
}