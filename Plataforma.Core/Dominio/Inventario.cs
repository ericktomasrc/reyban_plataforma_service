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