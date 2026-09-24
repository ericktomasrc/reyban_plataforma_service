namespace Plataforma.Core.Dominio;

/// <summary>
/// Un intento, no un cambio.
///
/// La bitácora general registra cambios en FILAS. Esta tabla registra
/// INTENTOS, que no dejan rastro en ninguna fila:
///
///   · un ingreso con contraseña incorrecta no modifica nada
///   · un 403 por falta de permiso no modifica nada
///   · un enlace caducado que alguien intentó usar no modifica nada
///
/// Y son exactamente los que se quieren ver cuando algo huele mal.
///
/// SOLO-INSERCIÓN. El rol de la aplicación no tiene UPDATE ni DELETE aquí, y
/// además hay un disparador que salta aunque lo intente el propietario.
/// </summary>
public class EventoSeguridad
{
    public long Id { get; set; }
    public DateTime OcurridoEn { get; set; }

    public string Tipo { get; set; } = string.Empty;
    public bool Exito { get; set; }

    /// <summary>
    /// Nulo a propósito: un intento de ingreso con un correo que no existe no
    /// tiene usuario, y ese caso es justo el que interesa vigilar.
    /// </summary>
    public Guid? UsuarioId { get; set; }
    public Guid? EmpresaId { get; set; }
    public string? Correo { get; set; }

    public System.Net.IPAddress? Ip { get; set; }
    public string? Agente { get; set; }

    /// <summary>
    /// JSONB. Contexto libre.
    ///
    /// NUNCA contraseñas, tokens ni claves: lo que entra aquí se guarda para
    /// siempre y se lee desde una pantalla de soporte.
    /// </summary>
    public string? Detalle { get; set; }
}


/// <summary>
/// Los tipos admitidos. Tienen que coincidir con el CHECK de la migración
/// 003: un tipo que no esté en esa lista hace fallar el INSERT.
/// </summary>
public static class TipoEvento
{
    // Ingreso
    public const string Ingreso                   = "ingreso";
    public const string IngresoClaveIncorrecta    = "ingreso_clave_incorrecta";
    public const string IngresoUsuarioInexistente = "ingreso_usuario_inexistente";
    public const string IngresoUsuarioInactivo    = "ingreso_usuario_inactivo";
    public const string IngresoUsuarioBloqueado   = "ingreso_usuario_bloqueado";
    public const string IngresoEmpresaSuspendida  = "ingreso_empresa_suspendida";
    public const string CierreSesion              = "cierre_sesion";
    public const string SesionExpirada            = "sesion_expirada";
    public const string SesionRevocada            = "sesion_revocada";

    // Segundo factor
    public const string DosfaSuperado           = "dosfa_superado";
    public const string DosfaFallido            = "dosfa_fallido";
    public const string DosfaActivado           = "dosfa_activado";
    public const string DosfaDesactivado        = "dosfa_desactivado";
    public const string CodigoRecuperacionUsado = "codigo_recuperacion_usado";

    // Contraseñas
    public const string ClaveCambiada            = "clave_cambiada";
    public const string ClaveRestablecerPedido   = "clave_restablecer_solicitado";
    public const string ClaveRestablecerUsado    = "clave_restablecer_usado";
    public const string ClaveRestablecerInvalido = "clave_restablecer_invalido";

    // Autorización
    public const string AccesoDenegado        = "acceso_denegado";
    public const string PermisoOtorgado       = "permiso_otorgado";
    public const string PermisoRetirado       = "permiso_retirado";
    public const string RolAsignado           = "rol_asignado";
    public const string RolRetirado           = "rol_retirado";
    public const string SuplantacionIniciada  = "suplantacion_iniciada";
    public const string SuplantacionTerminada = "suplantacion_terminada";

    // Empresas y credenciales
    public const string EmpresaSuspendida  = "empresa_suspendida";
    public const string EmpresaReactivada  = "empresa_reactivada";
    public const string CredencialGuardada = "credencial_facturacion_guardada";
    public const string CredencialRotada   = "credencial_facturacion_rotada";
    public const string CredencialFallo    = "credencial_facturacion_usada_fallo";

    public const string Otro = "otro";
}
