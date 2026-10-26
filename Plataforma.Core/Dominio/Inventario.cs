namespace Plataforma.Core.Dominio;

// =============================================================================
// 052 — INVENTARIO POR SUCURSAL.
//
// Aquí van lo que la API escribe con EF: la configuración, las maestras
// (proveedores, categorías) y las operaciones (ingreso, ajuste, stock inicial,
// conteo). EL SALDO Y EL KARDEX NO: los escribe la base (inv_mover), y la API
// solo los lee con SQL. Así ningún camino puede mover stock sin dejar su
// movimiento.
// =============================================================================

/// <summary>El inventario de una empresa. Sin fila, o apagado: la facturación como siempre.</summary>
public class InventarioConfig : EntidadAuditada
{
    public Guid EmpresaId { get; set; }

    /// <summary>«Llevar inventario en esta empresa» (columna activo_inventario).</summary>
    public bool Encendido { get; set; }

    /// <summary>directa (sale al facturar) o despacho (la factura reserva, la guía saca).</summary>
    public string Modo { get; set; } = ModoInventario.Directa;

    /// <summary>Apagado: no se emite si no hay disponible. Encendido: queda en negativo.</summary>
    public bool PermitirSinStock { get; set; }

    public bool AvisarMinimo { get; set; } = true;
    public DateTime? ActivadoEn { get; set; }
}

public static class ModoInventario
{
    public const string Directa  = "directa";
    public const string Despacho = "despacho";
}

/// <summary>Por sucursal: cuándo cargó su stock inicial y desde cuándo bloquea.</summary>
public class InvSucursal : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid SucursalId { get; set; }
    public DateTime? StockInicialEn { get; set; }
    public DateTime? ControlaDesde { get; set; }
}

/// <summary>A quién se le compra (maestra).</summary>
public class Proveedor : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public string TipoDocumento { get; set; } = "6";
    public string NumeroDocumento { get; set; } = "";
    public string RazonSocial { get; set; } = "";
    public string? Direccion { get; set; }
    public string? Correo { get; set; }
    public string? Telefono { get; set; }
}

/// <summary>Categoría de producto (maestra): «Cementos», «Pinturas».</summary>
public class ProductoCategoria : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public string Nombre { get; set; } = "";
}

/// <summary>
/// Un ingreso por compra, un ajuste, una carga de stock inicial o un conteo
/// físico. Se guarda con sus líneas y, en la misma transacción, la base mueve
/// el stock (inv_aplicar_operacion). Nada se borra: un ingreso o ajuste
/// equivocado se anula con el movimiento contrario.
/// </summary>
public class InvOperacion : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public string Tipo { get; set; } = TipoOperacion.Ingreso;

    /// <summary>Lo pone la base: el siguiente de su tipo en la empresa.</summary>
    public int Numero { get; set; }

    public Guid SucursalId { get; set; }
    public string Estado { get; set; } = "registrada";
    public DateOnly Fecha { get; set; }

    public Guid? ProveedorId { get; set; }
    public string? DocumentoProveedor { get; set; }
    public string? Moneda { get; set; }

    public string? Motivo { get; set; }
    public string? Nota { get; set; }

    /// <summary>066: compra: a qué almacén de la sucursal entra (null = el principal).</summary>
    public Guid? AlmacenId { get; set; }

    public DateTime? AplicadaEn { get; set; }
    public DateTime? AnuladaEn { get; set; }
    public string? AnuladaMotivo { get; set; }

    public List<InvOperacionLinea> Lineas { get; set; } = [];

    public string Codigo => $"{TipoOperacion.Prefijo(Tipo)}-{Numero:D4}";
}

public class InvOperacionLinea : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid OperacionId { get; set; }
    public Guid ProductoId { get; set; }

    /// <summary>Ingreso: lo que llegó. Ajuste: + entra / − sale. Stock inicial: lo que hay.</summary>
    public decimal Cantidad { get; set; }

    public decimal? CostoUnitario { get; set; }

    /// <summary>Conteo y stock inicial: lo que decía el sistema al aplicarse.</summary>
    public decimal? Sistema { get; set; }

    /// <summary>Conteo: lo que se contó.</summary>
    public decimal? Contado { get; set; }

    /// <summary>061: ingreso en una presentación (10 cajas a S/ 120). Cantidad y
    /// CostoUnitario quedan en unidades del producto (120 a S/ 10).</summary>
    public Guid? PresentacionId { get; set; }
    public decimal? Factor { get; set; }
    public decimal? CantidadPresentacion { get; set; }
    public decimal? CostoPresentacion { get; set; }

    /// <summary>064: compra o stock inicial de un producto con lotes: el lote y su vencimiento.</summary>
    public string? Lote { get; set; }
    public DateOnly? Vence { get; set; }

    /// <summary>065: compra de un producto con series: sus números.</summary>
    public string[]? Series { get; set; }
}

/// <summary>
/// 061 — Otra forma de comprar o vender un producto: «CAJA X 12» (BX) = 12
/// unidades del producto. El stock se cuenta siempre en la unidad del producto.
/// </summary>
/// <summary>066 — Un almacén dentro de una sucursal (tienda, depósito). El principal es de donde se vende.</summary>
public class InvAlmacen : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid SucursalId { get; set; }
    public string Nombre { get; set; } = "";
    public bool Principal { get; set; }
    public int Orden { get; set; }
}

/// <summary>062 — De qué está hecho un kit: venderlo descuenta esto × la cantidad.</summary>
public class ProductoComponente : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid KitId { get; set; }
    public Guid ComponenteId { get; set; }
    public decimal Cantidad { get; set; }
}

public class ProductoPresentacion : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid ProductoId { get; set; }
    public string Nombre { get; set; } = "";
    public string UnidadMedida { get; set; } = "BX";
    public decimal Factor { get; set; }
    /// <summary>Precio de venta propio (opcional). Null = precio del producto × factor.</summary>
    public decimal? PrecioUnitario { get; set; }
    public bool IncluyeIgv { get; set; } = true;
    public string? CodigoBarras { get; set; }
    public bool ParaCompra { get; set; } = true;
    public bool ParaVenta { get; set; } = true;
}

public static class TipoOperacion
{
    public const string Ingreso      = "ingreso";
    public const string Ajuste       = "ajuste";
    public const string StockInicial = "stock_inicial";
    public const string Conteo       = "conteo";

    public static string Prefijo(string tipo) => tipo switch
    {
        Ingreso => "IN", Ajuste => "AJ", StockInicial => "SI", Conteo => "CF", _ => "OP"
    };
}

/// <summary>Los motivos de ajuste (lista fija, como en los reportes).</summary>
public static class MotivoAjuste
{
    public static readonly (string Codigo, string Nombre, int Signo)[] Todos =
    [
        ("merma",           "Merma o rotura",         -1),
        ("vencido",         "Vencido",                -1),
        ("robo",            "Robo o pérdida",         -1),
        ("consumo_interno", "Consumo interno",        -1),
        ("muestra",         "Muestra o regalo",       -1),
        ("sobrante",        "Sobrante encontrado",     1),
        ("correccion",      "Corrección de un error",  0),
        ("otro",            "Otro (explica cuál)",     0),
    ];
}

/// <summary>Un traslado entre sucursales (la guía 04). Lo escribe la base; la API lo lee.</summary>
public class InvTraslado : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid GuiaId { get; set; }
    public Guid OrigenId { get; set; }
    public Guid DestinoId { get; set; }
    public string Estado { get; set; } = "en_transito";
    public DateTime SalioEn { get; set; }
    public DateTime? RecibidoEn { get; set; }
    public Guid? RecibidoPor { get; set; }
    public string? Nota { get; set; }

    public List<InvTrasladoLinea> Lineas { get; set; } = [];
}

public class InvTrasladoLinea : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid TrasladoId { get; set; }
    public Guid ProductoId { get; set; }
    public decimal Enviado { get; set; }
    public decimal? Recibido { get; set; }
    public string? MotivoDiferencia { get; set; }
}