namespace Plataforma.Core.Dominio;

/// <summary>
/// 050 — UNA GUÍA DE REMISIÓN (09 remitente, 31 transportista).
///
/// Se emite directo (no hay borrador): la pantalla junta todo, se manda al
/// servicio y desde ahí el estado lo trae el aviso del servicio. `Detalle` es
/// lo que se mandó, tal cual (jsonb): sirve para verla y para reintentar con
/// la misma clave si no llegó.
/// </summary>
public class Guia : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid? SucursalId { get; set; }

    public string Tipo { get; set; } = "09";
    public string Serie { get; set; } = "";
    public int? Correlativo { get; set; }
    public string Estado { get; set; } = EstadoGuia.Encolado;
    public string Ambiente { get; set; } = "beta";

    public DateOnly FechaEmision { get; set; }
    public DateOnly FechaTraslado { get; set; }
    public string Motivo { get; set; } = "01";
    public string? MotivoDescripcion { get; set; }
    public string Modalidad { get; set; } = "02";

    public string DestinatarioTipo { get; set; } = "6";
    public string DestinatarioDoc { get; set; } = "";
    public string DestinatarioNombre { get; set; } = "";
    public Guid? ClienteId { get; set; }

    public string PartidaDireccion { get; set; } = "";
    public string PartidaUbigeo { get; set; } = "";
    public string LlegadaDireccion { get; set; } = "";
    public string LlegadaUbigeo { get; set; } = "";

    public decimal PesoBruto { get; set; }
    public int? Bultos { get; set; }

    public string? VehiculoPlaca { get; set; }
    public string? ConductorDoc { get; set; }
    public string? ConductorNombre { get; set; }
    public string? TransportistaRuc { get; set; }
    public string? TransportistaNombre { get; set; }

    public Guid? DocumentoId { get; set; }
    public string? DocumentoRelacionado { get; set; }

    /// <summary>Lo que se mandó al servicio (json).</summary>
    public string Detalle { get; set; } = "{}";

    public Guid? GuiaServicioId { get; set; }
    public string? CodigoSunat { get; set; }
    public string? MensajeSunat { get; set; }
    public DateTime? EstadoServicioEn { get; set; }
    public string IdempotencyKey { get; set; } = "";

    public DateOnly? AnuladaEn { get; set; }
    public string? AnuladaMotivo { get; set; }

    /// <summary>052: de qué sucursal sale (null = la de la serie) y, en un traslado 04, a cuál llega.</summary>
    public Guid? InvOrigenId { get; set; }
    public Guid? InvDestinoId { get; set; }

    public List<GuiaBien> Bienes { get; set; } = [];

    public string Numero => Correlativo is { } c ? $"{Serie}-{c:D8}" : Serie;
}

/// <summary>Los estados de una guía en la plataforma.</summary>
public static class EstadoGuia
{
    public const string Encolado  = "encolado";
    public const string Aceptado  = "aceptado";
    public const string Observado = "observado";
    public const string Rechazado = "rechazado";
    public const string Error     = "error";
    public const string Anulado   = "anulado";

    /// <summary>Lo que cuenta como despachado de una factura (lo demás no salió).</summary>
    public static readonly string[] Cuentan = [Encolado, Aceptado, Observado];
}

/// <summary>050 — Un bien de la guía. Si sale de una factura, apunta a su línea.</summary>
public class GuiaBien : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid GuiaId { get; set; }
    public int Numero { get; set; }
    public Guid? ProductoId { get; set; }
    public Guid? DocumentoLineaId { get; set; }
    public string? Codigo { get; set; }
    public string Descripcion { get; set; } = "";
    public string Unidad { get; set; } = "NIU";
    public decimal Cantidad { get; set; }
}

/// <summary>050 — Un vehículo guardado de la empresa (se guarda solo al emitir).</summary>
public class Vehiculo : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public string Placa { get; set; } = "";
    public string? Descripcion { get; set; }
    public string? Tuc { get; set; }
    public string? Autorizacion { get; set; }
    public string? EntidadAutorizadora { get; set; }
    public int Usos { get; set; }
    public DateTime? UltimoUso { get; set; }
}

/// <summary>050 — Un conductor guardado. Puede ser alguien del personal (UsuarioId).</summary>
public class Conductor : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public string TipoDoc { get; set; } = "1";
    public string NumeroDoc { get; set; } = "";
    public string Nombres { get; set; } = "";
    public string Apellidos { get; set; } = "";
    public string Licencia { get; set; } = "";
    public Guid? UsuarioId { get; set; }
    public int Usos { get; set; }
    public DateTime? UltimoUso { get; set; }
}

/// <summary>050 — Una empresa de transporte guardada (transporte público).</summary>
public class Transportista : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public string Ruc { get; set; } = "";
    public string RazonSocial { get; set; } = "";
    public string? Mtc { get; set; }
    public int Usos { get; set; }
    public DateTime? UltimoUso { get; set; }
}

/// <summary>
/// 051 — Una GRE POR EVENTO hecha en SOL y registrada aquí (SUNAT no la
/// recibe por API). Tipo: «arribo» (no se pudo llegar o entregar), «transbordo»
/// (la carga pasó a otro vehículo) o «ambos».
/// </summary>
public class GuiaEvento : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
    public Guid GuiaId { get; set; }
    public string Tipo { get; set; } = "transbordo";
    public string NumeroSol { get; set; } = "";
    public DateOnly Fecha { get; set; }
    public string ReinicioDireccion { get; set; } = "";
    public string ReinicioUbigeo { get; set; } = "";
    public string? LlegadaDireccion { get; set; }
    public string? LlegadaUbigeo { get; set; }
    public string? Placa { get; set; }
    public string? ConductorDoc { get; set; }
    public string? ConductorNombre { get; set; }
    public string? Licencia { get; set; }
    public string? Observacion { get; set; }
}