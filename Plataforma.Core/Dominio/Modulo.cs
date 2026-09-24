namespace Plataforma.Core.Dominio;

/// <summary>
/// Catálogo de módulos del producto. Hoy solo existe «facturacion».
/// </summary>
public class Modulo
{
    public string Codigo { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public string Ruta { get; set; } = string.Empty;
    public string? Icono { get; set; }
    public int Orden { get; set; }
    public bool Disponible { get; set; } = true;
}


/// <summary>
/// Qué módulos tiene contratados cada empresa.
///
/// ESTA ES LA PIEZA QUE SOSTIENE LA PROMESA del documento maestro: «todo lo
/// que varía por empresa vive en configuración, nunca en condicionales de
/// código».
///
/// <see cref="Config"/> guarda los ajustes propios de esa empresa para ese
/// módulo. El día que un cliente pida algo distinto, la respuesta va ahí. El
/// día que la respuesta sea un <c>if (empresa == "X")</c> en el código,
/// empieza la cuenta regresiva hacia cincuenta sistemas disfrazados de uno.
///
/// SOLO EL SUPER ADMINISTRADOR ESCRIBE AQUÍ. La política de la base dice
/// <c>WITH CHECK (ctx_es_super())</c>: un administrador de empresa puede leer
/// qué contrató, no firmarse un contrato nuevo.
/// </summary>
public class EmpresaModulo : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Empresa Empresa { get; set; } = null!;

    public string ModuloCodigo { get; set; } = string.Empty;
    public Modulo Modulo { get; set; } = null!;

    /// <summary>JSONB. Ajustes de esa empresa para ese módulo.</summary>
    public string Config { get; set; } = "{}";

    public DateTime? ContratadoEn { get; set; }
    public DateTime? VenceEn { get; set; }

    public bool Vigente =>
        Activo && (VenceEn is null || VenceEn > DateTime.UtcNow);
}
