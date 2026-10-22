namespace Plataforma.Core.Dominio;

/// <summary>
/// Cómo llega esta empresa al servicio de facturación.
///
/// UNA FILA ACTIVA POR EMPRESA. El índice único de la base lo garantiza, y por
/// eso rotar la clave es desactivar la anterior e insertar otra, no un UPDATE:
/// así queda en la bitácora cuándo se cambió y quién lo hizo.
///
/// ES LA TABLA MÁS SENSIBLE DE LA PLATAFORMA. Esa clave emite comprobantes
/// tributarios en nombre del cliente. Quien la tenga puede facturar por él.
/// </summary>
public class CredencialFacturacion : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    /// <summary>
    /// Dónde vive el servicio. Cambia entre desarrollo y producción, y podría
    /// cambiar por empresa el día que un cliente grande tenga su propia
    /// instancia.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// La clave de API, cifrada con la llave maestra.
    ///
    /// UN SOLO CAMPO, no clave + nonce por separado: dentro van pegados el
    /// nonce, la etiqueta y el texto cifrado. Se descifra entero o no se
    /// descifra.
    ///
    /// NULA SOLO EN UNA FILA INACTIVA (migración 015, CHECK
    /// `ck_credenciales_clave_activa`): al rotar, la anterior se vacía en
    /// cuanto se comprueba que el servicio ya la rechaza —«Ya la revoqué»— o en
    /// el acto si era la misma clave. La fila se queda; la clave, no.
    /// </summary>
    public byte[]? ClaveCifrada { get; set; }

    /// <summary>
    /// El secreto `whsec_...` con el que el servicio firma sus avisos, cifrado
    /// igual que la clave.
    ///
    /// NO ES LA CLAVE `fac_...`: son dos secretos con dos usos distintos, y uno
    /// no se deduce del otro. El servicio lo enseña UNA sola vez, al registrar
    /// el webhook en su panel.
    ///
    /// Nulo = no configurado, y entonces el receptor rechaza todos los avisos
    /// de esta empresa. Va tapado en la bitácora (migración 012).
    /// </summary>
    public byte[]? WebhookSecretoCifrado { get; set; }

    /// <summary>
    /// Los últimos cuatro caracteres, en claro.
    ///
    /// Es lo que hacen los bancos con las tarjetas y sirve para lo mismo: que
    /// el administrador reconozca cuál es sin poder usarla.
    /// </summary>
    public string? Pista { get; set; }

    public DateTime? RotadaEn { get; set; }

    /// <summary>
    /// Cuándo se usó por última vez y cómo fue.
    ///
    /// SON LOS TRES CAMPOS QUE CONTESTAN «¿por qué no se emite?» sin abrir un
    /// log: si `UltimoFalloEn` es más reciente que `UltimaLlamadaEn`, algo está
    /// roto y `UltimoFallo` dice qué.
    /// </summary>
    public DateTime? UltimaLlamadaEn { get; set; }
    public DateTime? UltimoFalloEn { get; set; }
    public string? UltimoFallo { get; set; }

    /// <summary>
    /// Hasta cuándo vale el certificado digital del emisor, según el servicio
    /// (`GET /v1/emisor`). Columnas de la migración 013.
    ///
    /// EL CERTIFICADO NO VIVE AQUÍ: vive en el servicio y es el que firma cada
    /// XML. Esto es la última respuesta, guardada para poder avisar antes de
    /// que venza sin llamar al servicio en cada pantalla. La escriben «Probar
    /// conexión» y la revisión diaria (`RevisionCertificados`), siempre por
    /// `CertificadoDelEmisor.GuardarAsync`.
    ///
    /// LOS DOS NULOS SIGNIFICAN COSAS DISTINTAS:
    ///
    ///   · <see cref="CertificadoConsultadoEn"/> nulo: todavía no se preguntó.
    ///   · consultado, y <see cref="CertificadoValidoHasta"/> nulo: el servicio
    ///     no tiene certificado activo para esta empresa. No puede emitir.
    /// </summary>
    public DateTime? CertificadoValidoHasta { get; set; }
    public DateTime? CertificadoConsultadoEn { get; set; }

    /// <summary>
    /// A dónde manda el servicio los avisos de esta empresa, según
    /// `GET /v1/webhook`, y cuándo se preguntó. Migración 014.
    ///
    /// Si no es la URL de la plataforma, alguien con la clave la cambió y los
    /// avisos de SUNAT ya no llegan aquí: la pantalla lo dice en rojo.
    /// </summary>
    public string? WebhookUrlServicio { get; set; }
    public DateTime? WebhookRevisadoEn { get; set; }

    /// <summary>
    /// Cuándo se vació la clave de esta fila (ya retirada), y qué se comprobó
    /// antes: «el servicio la rechaza» o «era la misma clave que la activa».
    /// Quién lo comprobó va en <see cref="RevocadaComprobadaPor"/>. Migración 015.
    ///
    /// Mientras una fila inactiva conserve su clave, la pantalla lo dice en
    /// rojo: puede seguir valiendo en el servicio.
    /// </summary>
    public DateTime? ClaveVaciadaEn { get; set; }
    public DateTime? RevocadaComprobadaEn { get; set; }
    public Guid? RevocadaComprobadaPor { get; set; }
    public string? RevocadaComprobacion { get; set; }

    /// <summary>
    /// La creó el alta de la plataforma, junto con el emisor del servicio, en
    /// la misma operación (migración 015). Falso si se conectó con «Conectar».
    /// </summary>
    public bool CreadaPorAlta { get; set; }

    /// <summary>
    /// Cuándo confirmó la plataforma el alta al servicio. Nulo con
    /// <see cref="CreadaPorAlta"/> = el emisor sigue «sin confirmar» allí.
    /// </summary>
    public DateTime? AltaConfirmadaEn { get; set; }

    // `emisor_estado` (jsonb) y `emisor_estado_en` NO están aquí a propósito:
    // se escriben y se leen por SQL (`EstadoDelEmisor`), como la telemetría.
    // Así EF no tiene que saber nada de jsonb.
}


/// <summary>
/// A quién se le factura. Es del cliente, no de la plataforma.
///
/// EL MISMO RUC PUEDE ESTAR EN DOS EMPRESAS y son dos fichas distintas: dos
/// clientes tuyos pueden venderle al mismo comprador, y cada uno lleva su
/// propio correo de contacto y su propio historial.
/// </summary>
public class Cliente : EntidadAuditada
{
    public Guid EmpresaId { get; set; }

    /// <summary>
    /// Catálogo 06 de SUNAT: «1» DNI, «6» RUC, «4» carné de extranjería,
    /// «7» pasaporte, «0» sin documento.
    ///
    /// SE GUARDA EL CÓDIGO TAL CUAL, no un nombre. Así viaja al servicio de
    /// facturación sin traducción, y una traducción es justo donde se cuela un
    /// error que SUNAT devuelve tres pasos más adelante.
    /// </summary>
    public string TipoDocumento { get; set; } = "6";

    public string NumeroDocumento { get; set; } = string.Empty;
    public string RazonSocial { get; set; } = string.Empty;

    public string? NombreComercial { get; set; }
    public string? Direccion { get; set; }
    public string? Ubigeo { get; set; }
    public string? Correo { get; set; }
    public string? Telefono { get; set; }

    /// <summary>
    /// La afectación del IGV que se propone al facturarle, del catálogo 07.
    ///
    /// NULL ES EL CASO NORMAL y significa «nada especial, usa la del producto».
    /// Se rellena para el cliente que siempre va igual: el de exportación, el
    /// de la Amazonía.
    ///
    /// ES UNA PREFERENCIA, NO UNA RESTRICCIÓN. La pantalla la propone al elegir
    /// un producto y avisa si la línea no coincide, pero no impide nada: un
    /// cliente de exportación puede comprar algo en el mercado local, y una
    /// plataforma que lo prohíba obliga a mentir en la ficha para poder
    /// facturar.
    /// </summary>
    public string? AfectacionHabitual { get; set; }

    /// <summary>
    /// 043: su WhatsApp, solo dígitos con el código de país (51987654321).
    /// Opcional y aparte del teléfono: el fijo de la oficina no recibe WhatsApp.
    /// </summary>
    public string? Whatsapp { get; set; }

    /// <summary>043: «enviarle sus comprobantes por WhatsApp». La regla general; cada comprobante puede cambiarla.</summary>
    public bool WhatsappActivo { get; set; }
}


/// <summary>
/// Lo que se vende. También es del cliente.
///
/// NO ES UN INVENTARIO: no hay stock, ni almacenes, ni movimientos. Es el
/// catálogo de lo que aparece en una factura, y nada más. El día que haga falta
/// inventario, es otro módulo.
/// </summary>
public class Producto : EntidadAuditada
{
    public Guid EmpresaId { get; set; }

    public string Codigo { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;

    /// <summary>Catálogo 03: «NIU» unidad, «ZZ» servicio, «KGM» kilo…</summary>
    public string UnidadMedida { get; set; } = "NIU";

    /// <summary>
    /// Catálogo 07: «10» gravado, «20» exonerado, «30» inafecto,
    /// «40» exportación, «17» arroz pilado.
    /// </summary>
    public string TipoAfectacion { get; set; } = "10";

    /// <summary>
    /// DIEZ DECIMALES, y no es exageración: SUNAT los admite en el valor
    /// unitario, y redondear aquí produce diferencias de céntimos que después
    /// no cuadran con el total del comprobante.
    /// </summary>
    public decimal PrecioUnitario { get; set; }

    /// <summary>
    /// Si el precio guardado ya lleva IGV.
    ///
    /// IMPORTA MÁS DE LO QUE PARECE: el servicio de facturación espera el valor
    /// unitario SIN IGV. Guardar cómo se escribió el precio evita que alguien
    /// tenga que acordarse de dividir entre 1.18 cada vez, que es exactamente
    /// el sitio donde se equivoca.
    /// </summary>
    public bool IncluyeIgv { get; set; } = true;

    /// <summary>Código en el catálogo de SUNAT (catálogo 25). Opcional.</summary>
    public string? CodigoSunat { get; set; }
}