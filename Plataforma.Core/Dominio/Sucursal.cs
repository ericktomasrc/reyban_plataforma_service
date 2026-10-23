namespace Plataforma.Core.Dominio;

/// <summary>
/// 044 — UN LOCAL DEL MISMO RUC (establecimiento de la ficha RUC).
///
/// `Codigo` = el código de establecimiento de SUNAT, 4 dígitos; «0000» es el
/// domicilio fiscal, que toda empresa tiene y no se elimina. Es lo que va en
/// el XML (cbc:AddressTypeCode) y por eso no se repite en la empresa.
///
/// NADA SE BORRA: eliminar una sucursal que nunca se usó pone `EliminadaEn`
/// (y `Activo = false`) y la esconde de todas las listas; desactivarla la deja
/// en la lista, sin emitir.
/// </summary>
public class Sucursal : EntidadAuditada
{
    public const string CodigoPrincipal = "0000";

    public Guid EmpresaId { get; set; }
    public string Codigo { get; set; } = CodigoPrincipal;
    public string Nombre { get; set; } = "Domicilio fiscal";
    public string? Direccion { get; set; }
    public string? Ubigeo { get; set; }
    public DateTime? EliminadaEn { get; set; }

    public bool EsPrincipal => Codigo == CodigoPrincipal;
}

/// <summary>
/// 044 — A qué sucursal pertenece una serie. Una serie SIN fila es de la 0000:
/// así las series de antes no hubo que tocarlas.
/// </summary>
public class SucursalSerie : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid SucursalId { get; set; }
    public string TipoComprobante { get; set; } = "01";
    public string Serie { get; set; } = "";
}

/// <summary>
/// 044 — En qué sucursal trabaja un usuario. Sin filas activas: en todas.
/// </summary>
public class UsuarioSucursal : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid SucursalId { get; set; }
}