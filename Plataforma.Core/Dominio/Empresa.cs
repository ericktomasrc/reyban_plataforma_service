namespace Plataforma.Core.Dominio;

/// <summary>
/// Una empresa cliente. En el resto del sistema se la llama también
/// «el tenant»: un RUC, una empresa, un historial separado de los demás.
/// </summary>
public class Empresa : EntidadAuditada
{
    /// <summary>
    /// El nombre para mostrar: cabecera, lista de empresas, correos. Se teclea
    /// en el alta y se puede editar. Migración 014.
    /// </summary>
    public string Nombre { get; set; } = string.Empty;

    /// <summary>
    /// La cuenta a la que pertenece (migración 016). NULA AL INSERTAR = la
    /// base le crea su Cuenta Negocio sola (disparador `tg_empresas_cuenta`).
    /// </summary>
    public Guid? CuentaId { get; set; }

    /// <summary>
    /// EL RUC LLEGA DEL SERVICIO, al pulsar «Conectar» con la clave fac_ de la
    /// empresa. Nulo = todavía no conectada, y entonces no puede tener el
    /// módulo de facturación.
    ///
    /// No cambia una vez puesto: lo impide un disparador de la base
    /// (`tg_empresas_ruc_inmutable`). Es único entre las empresas ACTIVAS, así
    /// que una desactivada puede compartirlo: toda búsqueda por RUC tiene que
    /// filtrar por activo.
    /// </summary>
    public string? Ruc { get; set; }

    /// <summary>
    /// La oficial, la que dice SUNAT. Llega del servicio con el RUC y se
    /// refresca cada vez que se le pregunta: SUNAT deja cambiarla conservando
    /// el RUC. Nunca se teclea aquí.
    /// </summary>
    public string? RazonSocial { get; set; }

    /// <summary>«beta» o «produccion», según el servicio. Nulo hasta conectar.</summary>
    public string? Ambiente { get; set; }

    /// <summary>Cuándo se preguntó al servicio por el RUC, la razón social y el ambiente.</summary>
    public DateTime? DatosOficialesEn { get; set; }

    public string? NombreComercial { get; set; }

    /// <summary>
    /// El correo del dueño de la empresa (017). Lo pide el asistente; en la
    /// fase 4 es la llave para recuperarla. Nulo en las del super administrador.
    /// </summary>
    public string? CorreoTitular { get; set; }
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

    /// <summary>
    /// 040: lo habitual de la detracción. Se guarda al emitir una factura con
    /// detracción y la pantalla lo propone en las siguientes.
    /// </summary>
    public string? DetraccionCuenta { get; set; }
    public string? DetraccionCodigo { get; set; }
    public decimal? DetraccionPorcentaje { get; set; }

    /// <summary>050: también lleva carga ajena (emite la guía del transportista, 31).</summary>
    public bool TransportaCarga { get; set; }
    /// <summary>050: su registro en el MTC (obligatorio en la 31).</summary>
    public string? RegistroMtc { get; set; }

    public ICollection<Usuario> Usuarios { get; set; } = [];
    public ICollection<EmpresaModulo> Modulos { get; set; } = [];

    public bool EstaSuspendida => SuspendidaEn is not null;

    /// <summary>Tiene RUC oficial: ya se conectó con su clave del servicio.</summary>
    public bool EstaConectada => Ruc is not null;
    public bool PuedeOperar => Activo && !EstaSuspendida;
}