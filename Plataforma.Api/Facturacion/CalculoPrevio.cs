using Plataforma.Core.Dominio;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// El cálculo que se enseña en pantalla mientras se arma un comprobante.
///
/// ══════════════════════════════════════════════════════════════════════════
/// ESTO NO ES EL CÁLCULO DEL COMPROBANTE. LÉASE ESTO ANTES DE TOCAR NADA.
///
/// La regla que no se cruza en todo este proyecto es que **la plataforma no
/// sabe de SUNAT**: no calcula el IGV que se declara, no decide redondeos, no
/// conoce las reglas de afectación. Eso vive en el servicio de facturación y
/// está probado contra el ambiente beta documento por documento.
///
/// Lo de aquí sirve para dos cosas y ninguna más:
///
///   1. **Que quien está escribiendo vea un total** antes de pulsar Emitir. Un
///      formulario de factura sin total a la vista es inusable.
///
///   2. **Que el listado no haga una llamada HTTP por fila.** Se guarda en
///      `documentos` como copia, tal como pide la migración 005.
///
/// LA CIFRA BUENA ES LA QUE DEVUELVE EL SERVICIO. En cuanto contesta, su
/// `importeTotal` pisa el de aquí. Si algún día los dos difieren, gana el
/// servicio y esta fila está mal — no al revés.
///
/// POR QUÉ ENTONCES SE CALCULA ALGO AQUÍ, si duplica lógica: porque la
/// alternativa era pedirle al servicio un cálculo de prueba en cada tecla, o
/// enseñar la pantalla sin totales hasta emitir. La primera es una llamada HTTP
/// por pulsación; la segunda deja a alguien emitiendo a ciegas.
///
/// LO QUE ESTO **NO** HACE, y por eso no puede sustituir al servicio:
/// detracción, ISC, ICBPER, IVAP, anticipos, percepción, cuotas de crédito,
/// operaciones gratuitas, cargos globales del catálogo 53 y exportación de
/// servicios. Cada uno tiene reglas propias que el servicio ya resolvió.
/// ══════════════════════════════════════════════════════════════════════════
/// </summary>
public static class CalculoPrevio
{
    /// <summary>
    /// La tasa general del IGV.
    ///
    /// ESTÁ AQUÍ COMO NÚMERO Y NO SALE DE NINGUNA TABLA, a propósito: es un
    /// número de pantalla. El que se declara lo pone el servicio, que además
    /// sabe que el arroz pilado tributa al 4% y no al 18%.
    /// </summary>
    private const decimal TasaIgv = 0.18m;

    /// <summary>
    /// Las afectaciones que esta pantalla sabe estimar.
    ///
    /// Cualquier otra —las de operación gratuita, sobre todo— se deja en cero y
    /// el total de pantalla sale corto. Es preferible a enseñar un número
    /// inventado: un total que no cuadra se nota; uno que cuadra por
    /// casualidad, no.
    /// </summary>
    public const string Gravado     = "10";
    public const string Exonerado   = "20";
    public const string Inafecto    = "30";
    public const string Exportacion = "40";
    public const string Ivap        = "17";

    public static bool SeSabeEstimar(string afectacion) =>
        afectacion is Gravado or Exonerado or Inafecto or Exportacion or Ivap;

    /// <summary>
    /// e169 — LAS OPERACIONES GRATUITAS («🎁 Es regalo» en la pantalla).
    ///
    /// 11 a 16 son gratuitas sobre algo gravado (el IGV lo asume quien
    /// regala), 21 sobre algo exonerado y 31 a 37 sobre algo inafecto. La
    /// pantalla elige el código por el motivo; aquí solo se reconoce.
    ///
    /// Lo que se guarda en la línea: el valor unitario es el VALOR
    /// REFERENCIAL (cuánto vale, sin IGV; SUNAT lo pide mayor que cero). El
    /// valor de venta es ese valor por la cantidad y va al total gratuito;
    /// el importe que paga el cliente por la línea es cero. El XML, la
    /// leyenda 1002 y el tributo 9996 los pone el servicio de facturación.
    /// </summary>
    public static readonly IReadOnlySet<string> Gratuitas = new HashSet<string>
    {
        "11", "12", "13", "14", "15", "16", "21", "31", "32", "33", "34", "35", "36", "37"
    };

    public static bool EsGratuita(string afectacion) => Gratuitas.Contains(afectacion);

    /// <summary>Las afectaciones que acepta un comprobante de la plataforma.</summary>
    public static bool EsConocida(string afectacion) => SeSabeEstimar(afectacion) || EsGratuita(afectacion);


    /// <summary>
    /// Rellena los importes de una línea y devuelve la misma línea.
    ///
    /// EL VALOR UNITARIO QUE ENTRA ES SIEMPRE SIN IGV. La conversión desde un
    /// precio con IGV se hace antes, al elegir el producto, porque es ahí donde
    /// se sabe si el precio guardado lo llevaba.
    /// </summary>
    public static DocumentoLinea Calcular(DocumentoLinea l)
    {
        // El descuento se aplica sobre el valor de venta, no sobre el unitario:
        // así un 10% sobre tres unidades descuenta lo mismo que tres descuentos
        // del 10%, sin arrastrar el redondeo de cada unidad.
        var bruto = l.Cantidad * l.ValorUnitario;
        var neto  = bruto - l.Descuento;

        if (neto < 0) neto = 0;

        l.ValorVenta = Redondear(neto);

        // e182: EL PRECIO SE DIO CON IGV (28.50): DESDE EL TOTAL, como el
        // servicio (CalculadoraTotales.DesdeElTotal). 2 × 28.50 = 57.00, no 57.01.
        if (l.TipoAfectacion == Gravado && PrecioConIgv(l.ValorUnitario) is { } precio)
        {
            var pct = PorcentajeDescuento(l.Cantidad, l.ValorUnitario, l.Descuento);
            var factor = Redondear(pct / 100m, 5);
            var brutoConIgv = Redondear(l.Cantidad * precio);
            var total = factor > 0 ? Redondear(brutoConIgv * (1m - factor)) : brutoConIgv;
            l.ValorVenta = Redondear(total / (1m + TasaIgv));
            l.Igv = total - l.ValorVenta;
            l.Isc = 0;
            l.OtrosTributos = 0;
            l.ImporteTotal = total;
            l.PrecioUnitario = l.Cantidad > 0 ? Redondear(total / l.Cantidad, 10) : 0;
            return l;
        }

        var tasa = l.TipoAfectacion switch
        {
            Gravado => TasaIgv,

            // El IVAP sustituye al IGV y va al 4%. Se estima aparte porque, si
            // se dejara en 18, el total de pantalla saldría un 14% por encima
            // del real y nadie entendería por qué.
            Ivap    => 0.04m,

            _       => 0m
        };

        l.Igv = Redondear(l.ValorVenta * tasa);

        // ISC y otros tributos no se estiman: los calcula el servicio. Quedan
        // en cero a propósito, no por olvido.
        l.Isc = 0;
        l.OtrosTributos = 0;

        l.ImporteTotal = Redondear(l.ValorVenta + l.Igv);

        // e169: UN REGALO NO SE COBRA. Su valor de venta (el referencial)
        // queda para el total gratuito; lo que paga el cliente es cero.
        if (EsGratuita(l.TipoAfectacion))
        {
            l.Igv = 0;
            l.ImporteTotal = 0;
            l.PrecioUnitario = 0;
            return l;
        }

        // El precio unitario con IGV, solo para que se vea en la pantalla y en
        // el PDF. No viaja al servicio.
        l.PrecioUnitario = l.Cantidad > 0
            ? Redondear(l.ImporteTotal / l.Cantidad, 10)
            : 0;

        return l;
    }


    /// <summary>
    /// Suma las líneas en los totales del documento.
    ///
    /// SE SUMAN LAS LÍNEAS YA REDONDEADAS, no los valores exactos. Es lo que
    /// hace SUNAT —la base imponible del documento tiene que ser exactamente la
    /// suma de las líneas, regla 3277— y redondear al final daría un céntimo de
    /// diferencia con lo que acabe diciendo el comprobante.
    /// </summary>
    public static void Totalizar(Documento d)
    {
        d.TotalGravado     = 0;
        d.TotalExonerado   = 0;
        d.TotalInafecto    = 0;
        d.TotalGratuito    = 0;
        d.TotalExportacion = 0;
        d.TotalDescuentos  = 0;
        d.TotalIgv         = 0;
        d.TotalIsc         = 0;
        d.TotalOtros       = 0;

        foreach (var l in d.Lineas.Where(x => x.Activo))
        {
            d.TotalDescuentos += l.Descuento;
            d.TotalIgv        += l.Igv;

            switch (l.TipoAfectacion)
            {
                case Gravado:
                case Ivap:
                    d.TotalGravado += l.ValorVenta;
                    break;

                case Exonerado:   d.TotalExonerado   += l.ValorVenta; break;
                case Inafecto:    d.TotalInafecto    += l.ValorVenta; break;
                case Exportacion: d.TotalExportacion += l.ValorVenta; break;

                // Gratuitas y cualquier afectación que esta pantalla no sabe
                // estimar: no suman al importe total, que es justamente lo que
                // hacen de verdad —el cliente paga lo mismo—. Su IGV lo asume
                // el emisor y lo declara el servicio.
                default:
                    d.TotalGratuito += l.ValorVenta;
                    break;
            }
        }

        d.TotalGravado     = Redondear(d.TotalGravado);
        d.TotalExonerado   = Redondear(d.TotalExonerado);
        d.TotalInafecto    = Redondear(d.TotalInafecto);
        d.TotalGratuito    = Redondear(d.TotalGratuito);
        d.TotalExportacion = Redondear(d.TotalExportacion);
        d.TotalDescuentos  = Redondear(d.TotalDescuentos);
        d.TotalIgv         = Redondear(d.TotalIgv);

        d.ImporteTotal = Redondear(
            d.TotalGravado + d.TotalExonerado + d.TotalInafecto +
            d.TotalExportacion + d.TotalIgv);
    }


    /// <summary>
    /// Quita el IGV de un precio que ya lo lleva.
    ///
    /// SE USA AL ELEGIR UN PRODUCTO cuyo precio está guardado con IGV, porque
    /// el servicio espera el valor unitario sin él. Que esto viva en un solo
    /// sitio es lo que evita que alguien divida entre 1.18 a mano en la
    /// pantalla y se olvide en el back, o al revés.
    ///
    /// DIEZ DECIMALES, los mismos que la columna. Con dos, un precio de 100 con
    /// IGV daría 84.75 y el total de una factura de cien unidades saldría
    /// medio sol por debajo.
    /// </summary>
    public static decimal QuitarIgv(decimal precioConIgv) =>
        Redondear(precioConIgv / (1 + TasaIgv), 10);

    /// <summary>
    /// e182 — ¿ESTE VALOR SIN IGV SALIÓ DE UN PRECIO CON IGV REDONDO? Si
    /// 24.1525423729 × 1.18 da 28.50 (a una millonésima), el precio fue 28.50 y
    /// se devuelve; si no (24.15 × 1.18 = 28.497), null. Así no hace falta
    /// guardar aparte cómo se escribió el precio de cada línea.
    /// </summary>
    public static decimal? PrecioConIgv(decimal valorUnitario)
    {
        if (valorUnitario <= 0) return null;
        var conIgv = valorUnitario * (1 + TasaIgv);
        var precio = Redondear(conIgv);
        return Math.Abs(conIgv - precio) < 0.000001m ? precio : null;
    }

    /// <summary>El descuento en % del bruto, con seis decimales (igual que se manda al servicio).</summary>
    public static decimal PorcentajeDescuento(decimal cantidad, decimal valorUnitario, decimal descuento)
    {
        var bruto = cantidad * valorUnitario;
        if (bruto <= 0 || descuento <= 0) return 0;
        return Math.Round(descuento / bruto * 100m, 6, MidpointRounding.AwayFromZero);
    }


    /// <summary>
    /// A la mitad, hacia arriba.
    ///
    /// `MidpointRounding.AwayFromZero` y NO el de .NET por defecto, que es «al
    /// par más cercano»: con el de fábrica, 0.125 redondea a 0.12 y 0.135 a
    /// 0.14, y una columna de importes empieza a no cuadrar con lo que
    /// cualquiera calcularía a mano.
    /// </summary>
    private static decimal Redondear(decimal valor, int decimales = 2) =>
        Math.Round(valor, decimales, MidpointRounding.AwayFromZero);
}
