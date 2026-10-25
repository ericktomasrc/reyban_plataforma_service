namespace Plataforma.Core.Dominio;

/// <summary>
/// Los estados por los que pasa un comprobante.
///
/// SE ESCRIBEN COMO CADENAS PORQUE ASÍ ESTÁN EN LA BASE, con su CHECK. Un enum
/// de C# tendría que traducirse en los dos sentidos, y el día que alguien
/// añadiera un valor en un sitio y no en el otro, el fallo aparecería al
/// guardar, con un mensaje sobre una restricción.
/// </summary>
public static class EstadoDocumento
{
    /// <summary>Solo existe aquí. Se puede editar y borrar.</summary>
    public const string Borrador = "borrador";

    /// <summary>
    /// El servicio lo aceptó (202) y está esperando a SUNAT.
    ///
    /// ES EL ESTADO NORMAL DURANTE UNOS SEGUNDOS, no un problema. El servicio
    /// responde en milisegundos porque no espera a SUNAT: quien mira la
    /// pantalla justo después ve esto, y está bien.
    /// </summary>
    public const string Encolado = "encolado";

    public const string Aceptado  = "aceptado";
    public const string Observado = "observado";

    /// <summary>SUNAT lo rechazó. No se reintenta: el XML está mal.</summary>
    public const string Rechazado = "rechazado";

    public const string Anulado = "anulado";

    /// <summary>
    /// No se pudo ni entregar al servicio.
    ///
    /// DISTINTO DE `Rechazado`, y la diferencia importa: esto se arregla
    /// encendiendo el servicio y reintentando; aquello, corrigiendo el
    /// documento. Un comprobante en `error` NO tiene número: nunca llegó a
    /// existir para SUNAT.
    /// </summary>
    public const string Error = "error";
}


/// <summary>
/// Un comprobante, visto desde la plataforma.
///
/// LO QUE ESTA FILA ES: qué escribió el usuario, qué se le mandó al servicio y
/// qué contestó. Nada más.
///
/// LO QUE NO ES: el original. El XML firmado, el CDR y el PDF viven en el
/// servicio y no se copian aquí — guardarlos sería tener dos originales del
/// mismo documento legal. `ComprobanteId` es el puente para pedírselos.
/// </summary>
public class Documento : EntidadAuditada
{
    public Guid EmpresaId { get; set; }

    /// <summary>Catálogo 01: «01» factura, «03» boleta.</summary>
    public string Tipo { get; set; } = "01";

    /// <summary>
    /// LOS PONE EL SERVICIO, no la plataforma, y por eso son nulos mientras el
    /// documento es un borrador.
    ///
    /// Duplicar la numeración aquí sería crear una segunda fuente de verdad
    /// para el dato que más caro cuesta equivocar: un correlativo duplicado es
    /// un problema tributario, no un error de programa.
    /// </summary>
    public string? Serie { get; set; }
    public int? Correlativo { get; set; }

    public Guid ClienteId { get; set; }
    public Cliente? Cliente { get; set; }

    public DateOnly FechaEmision { get; set; }
    public DateOnly? FechaVencimiento { get; set; }
    public string Moneda { get; set; } = "PEN";

    /// <summary>
    /// Soles por unidad de la moneda, con 3 decimales (migración 026). Solo en
    /// moneda extranjera: el de venta que publica la SBS para la fecha de
    /// emisión. SUNAT lo exige en el XML y el servicio rechaza sin él.
    /// </summary>
    public decimal? TipoCambio { get; set; }

    /// <summary>
    /// 030: el SUNAT al que fue: «beta» (pruebas, sin valor legal) o
    /// «produccion». Lo pone ServicioEmision al emitir. Null en un borrador.
    /// </summary>
    public string? Ambiente { get; set; }

    // --- Notas de crédito y débito (todavía no se emiten) --------------------
    public Guid? DocumentoRefId { get; set; }
    public string? MotivoNota { get; set; }
    public string? TipoNota { get; set; }

    // --- El puente con el servicio ------------------------------------------

    /// <summary>El identificador que devolvió el servicio. Con él se piden el XML, el CDR y el PDF.</summary>
    public Guid? ComprobanteId { get; set; }

    /// <summary>
    /// Lo que impide que un doble clic emita dos facturas.
    ///
    /// SE GENERA AL CREAR EL BORRADOR, no al emitir, y esa es la parte que
    /// importa: si se generara al pulsar «Emitir», dos clics serían dos claves
    /// distintas y el servicio no tendría forma de saber que son el mismo
    /// documento.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// El `ocurridoEn` del último aviso del servicio que se aplicó.
    ///
    /// LOS AVISOS PUEDEN LLEGAR DESORDENADOS: el servicio reintenta con espera
    /// creciente, y un «aceptado» que se quedó reintentando puede llegar
    /// después del «anulado». Un aviso que no sea posterior a este no se
    /// aplica — se registra y se ignora.
    ///
    /// Lo escribe solo el receptor del webhook. Columna de la migración 012.
    /// </summary>
    public DateTime? EstadoServicioEn { get; set; }

    public string Estado { get; set; } = EstadoDocumento.Borrador;

    public string? CodigoSunat { get; set; }
    public string? MensajeSunat { get; set; }
    public string[]? Observaciones { get; set; }

    // --- Totales -------------------------------------------------------------
    //
    // COPIA INFORMATIVA. La cifra que se firmó y viajó a SUNAT es la que
    // devolvió el servicio; si algún día las dos difieren, gana el servicio y
    // esta fila está mal.
    //
    // Se guardan igualmente porque un listado de comprobantes no puede hacer
    // una llamada HTTP por cada fila de la pantalla.

    public decimal TotalGravado { get; set; }
    public decimal TotalExonerado { get; set; }
    public decimal TotalInafecto { get; set; }
    public decimal TotalGratuito { get; set; }
    public decimal TotalExportacion { get; set; }
    public decimal TotalDescuentos { get; set; }
    public decimal TotalIgv { get; set; }
    public decimal TotalIsc { get; set; }
    public decimal TotalOtros { get; set; }
    public decimal ImporteTotal { get; set; }

    /// <summary>
    /// Lo que el usuario escribió y no es de SUNAT: orden de compra, vendedor,
    /// centro de costo.
    ///
    /// NUNCA VIAJA AL XML. Es la regla de oro número uno. Si hace falta que
    /// aparezca impreso, va en la plantilla del PDF.
    /// </summary>
    public Dictionary<string, string> DatosExtra { get; set; } = [];

    /// <summary>
    /// 040: «contado» o «credito», como pregunta SUNAT SOL. Una factura a
    /// crédito con receptor RUC necesita al menos una cuota (errores 3249/3251).
    /// </summary>
    public string FormaPago { get; set; } = FormaDePago.Contado;

    /// <summary>040: a crédito, las cuotas. Suman el neto pendiente (total − detracción).</summary>
    public List<CuotaDocumento> Cuotas { get; set; } = [];

    /// <summary>040: la detracción (SPOT). Solo facturas. Null = no lleva.</summary>
    public DetraccionDocumento? Detraccion { get; set; }

    // --- 043: WhatsApp -------------------------------------------------------
    /// <summary>Se manda por WhatsApp al emitirlo. Se decide al hacerlo (propuesto por el cliente).</summary>
    public bool WhatsappEnviar { get; set; }

    /// <summary>pendiente | enviado | sin_whatsapp | no_enviado. Null: no se pidió.</summary>
    public string? WhatsappEstado { get; set; }

    public string? WhatsappNumero { get; set; }
    public DateTime? WhatsappEn { get; set; }

    // --- 044: sucursales -------------------------------------------------------
    /// <summary>De qué sucursal salió (la de su serie). Null en los de antes = la 0000.</summary>
    public Guid? SucursalId { get; set; }

    // --- 052: inventario --------------------------------------------------------
    /// <summary>
    /// «Se entrega después (con guía)»: la factura reserva y la guía saca. Null =
    /// lo que diga el modo de la empresa (venta directa o con despacho).
    /// </summary>
    public bool? EntregaDespues { get; set; }

    /// <summary>Nota de crédito que devuelve productos: volvio | danada | no_volvio (null = volvió).</summary>
    public string? InvRetorno { get; set; }

    /// <summary>Nota de crédito: a qué sucursal volvió (null = la del comprobante).</summary>
    public Guid? InvSucursalId { get; set; }

    public List<DocumentoLinea> Lineas { get; set; } = [];

    /// <summary>El número tal como lo lee una persona: «F001-00000087».</summary>
    public string Numero =>
        Serie is { Length: > 0 } s && Correlativo is { } n
            ? $"{s}-{n:D8}"
            : "(sin número)";

    /// <summary>
    /// Un borrador se puede cambiar; lo demás, no.
    ///
    /// EN CUANTO SE ENTREGA AL SERVICIO DEJA DE SER NUESTRO. Aunque SUNAT lo
    /// rechace después, esa fila ya gastó un correlativo y forma parte de una
    /// secuencia. Se corrige con una nota de crédito, no editándola.
    /// </summary>
    public bool SePuedeEditar => Estado is EstadoDocumento.Borrador or EstadoDocumento.Error;
}


/// <summary>040: los dos valores de la forma de pago, como están en la base.</summary>
public static class FormaDePago
{
    public const string Contado = "contado";
    public const string Credito = "credito";
}

/// <summary>
/// 040: una cuota de una factura a crédito. `Vence` tiene que ser posterior a
/// la fecha de emisión (error 3267 de SUNAT).
/// </summary>
public sealed record CuotaDocumento(int Numero, decimal Monto, DateOnly Vence);

/// <summary>
/// 040: la detracción de una factura.
///
/// `Codigo` del catálogo 54 (por ejemplo 022, otros servicios empresariales);
/// `Porcentaje` la tasa (12 = 12 %); `Cuenta` la del Banco de la Nación del
/// VENDEDOR; `MedioPago` del catálogo 59 (001 = depósito en cuenta); `Monto`
/// siempre en soles y redondeado al entero, como el depósito del banco.
/// </summary>
public sealed record DetraccionDocumento(
    string Codigo, decimal Porcentaje, string Cuenta, string MedioPago, decimal? Monto = null);


/// <summary>
/// Una línea del comprobante.
///
/// LOS DATOS VAN COPIADOS, NO REFERENCIADOS. `ProductoId` queda como recuerdo
/// de dónde salió, pero la descripción, la unidad y el precio se guardan aquí.
///
/// Es lo correcto tributariamente: una factura de marzo dice lo que decía en
/// marzo. Si la línea apuntara al producto, subir la lista de precios cambiaría
/// facturas ya emitidas — y eso, además de estar mal, es indefendible en una
/// fiscalización.
/// </summary>
public class DocumentoLinea : EntidadAuditada
{
    public Guid DocumentoId { get; set; }
    public Guid EmpresaId { get; set; }

    /// <summary>Empieza en 1. Es el orden en que sale impreso.</summary>
    public int Numero { get; set; }

    public Guid? ProductoId { get; set; }

    public string? Codigo { get; set; }
    public string Descripcion { get; set; } = string.Empty;
    public string UnidadMedida { get; set; } = "NIU";

    public decimal Cantidad { get; set; }

    /// <summary>Sin IGV y sin descuento. Es lo que espera el servicio.</summary>
    public decimal ValorUnitario { get; set; }

    /// <summary>Con IGV. Solo para la pantalla y el PDF.</summary>
    public decimal PrecioUnitario { get; set; }

    public decimal Descuento { get; set; }

    public string TipoAfectacion { get; set; } = "10";

    public decimal Igv { get; set; }
    public decimal Isc { get; set; }
    public decimal OtrosTributos { get; set; }

    public decimal ValorVenta { get; set; }
    public decimal ImporteTotal { get; set; }
}


/// <summary>
/// Cada cambio de estado de un documento.
///
/// NO SE ESCRIBE DESDE C#: lo hace un disparador de la base. El estado cambia
/// desde tres sitios —la pantalla, el webhook, la consulta manual— y mañana
/// habrá un cuarto; si cada uno tuviera que acordarse de escribir aquí, alguno
/// se olvidaría.
///
/// Y es de solo inserción: hay un disparador que rechaza UPDATE y DELETE.
/// </summary>
public class DocumentoHistorial
{
    public long Id { get; set; }
    public Guid DocumentoId { get; set; }
    public Guid EmpresaId { get; set; }

    public DateTime OcurridoEn { get; set; }

    public string? EstadoAnterior { get; set; }
    public string EstadoNuevo { get; set; } = string.Empty;

    /// <summary>`usuario`, `webhook`, `consulta` o `sistema`.</summary>
    public string Origen { get; set; } = string.Empty;
    public Guid? UsuarioId { get; set; }

    public string? CodigoSunat { get; set; }
    public string? Mensaje { get; set; }
}