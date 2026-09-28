namespace Plataforma.Core.Dominio;

/// <summary>
/// Una empresa cliente. En el resto del sistema se la llama también
/// «el tenant»: un RUC, una empresa, un historial separado de los demás.
/// </summary>
public class Empresa : EntidadAuditada
{
    public string Ruc { get; set; } = string.Empty;
    public string RazonSocial { get; set; } = string.Empty;

    public string? NombreComercial { get; set; }
    public string? Direccion { get; set; }
    public string? CorreoContacto { get; set; }
    public string? Telefono { get; set; }

    /// <summary>
    /// Comercial, no técnico. No condiciona el código: los módulos se activan
    /// uno a uno en <see cref="EmpresaModulo"/>. Si algún día una línea de
    /// código pregunta por el plan, se cruzó la línea.
    /// </summary>
    public string Plan { get; set; } = "basico";

    /// <summary>
    /// Suspender no es desactivar.
    ///
    /// <c>Activo = false</c> significa «esta empresa ya no existe para
    /// nosotros». Suspendida significa «no paga, pero vuelve»: sus datos
    /// siguen ahí y sus usuarios no entran.
    /// </summary>
    public DateTime? SuspendidaEn { get; set; }
    public string? MotivoSuspension { get; set; }

    /// <summary>
    /// Cuánto dura un enlace de invitación o de restablecimiento para la gente
    /// de esta empresa. Entre 1 y 168 horas, y la base lo comprueba también.
    ///
    /// POR EMPRESA Y NO GLOBAL porque no todos los clientes trabajan igual:
    /// una oficina que revisa el correo cada mañana necesita más margen que
    /// una donde el administrador llama por teléfono antes de crear la cuenta.
    /// </summary>
    public int HorasInvitacion { get; set; } = 24;

    public ICollection<Usuario> Usuarios { get; set; } = [];
    public ICollection<EmpresaModulo> Modulos { get; set; } = [];

    public bool EstaSuspendida => SuspendidaEn is not null;
    public bool PuedeOperar => Activo && !EstaSuspendida;
}
