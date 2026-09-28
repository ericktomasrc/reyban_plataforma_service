namespace Plataforma.Core.Dominio;

/// <summary>
/// Una persona que entra al sistema.
///
/// SOBRE <see cref="EmpresaId"/> NULO: un usuario sin empresa es un super
/// administrador, alguien tuyo. Vive fuera de los tenants porque su trabajo
/// es justamente cruzarlos. La base lo obliga con un CHECK: o eres super
/// administrador sin empresa, o eres de una empresa y no eres super
/// administrador. No hay término medio, y es a propósito — mezclarlos es
/// como se acaba dando acceso global a alguien sin querer.
/// </summary>
public class Usuario : EntidadAuditada
{
    public Guid? EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    /// <summary>
    /// Único en toda la plataforma, no por empresa.
    ///
    /// La consecuencia: una persona que trabaje para dos empresas cliente
    /// necesita dos cuentas. Se eligió así porque la alternativa obliga a
    /// preguntar «¿a cuál entras?» en cada ingreso, y convierte la pantalla
    /// más usada del sistema en la más confusa.
    /// </summary>
    public string Correo { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;

    /// <summary>
    /// PBKDF2-SHA256. El formato guarda el algoritmo y las iteraciones junto
    /// al hash, para poder subir las iteraciones con los años sin dejar fuera
    /// a los usuarios existentes: al ingresar se les recifra la contraseña.
    ///
    /// NO SE USA SHA-256 A SECAS, que sí sirve para los tokens. La diferencia
    /// está en quién elige el valor: un token es aleatorio y nadie lo adivina;
    /// una contraseña la eligió una persona, y las personas eligen mal.
    /// </summary>
    public string ClaveHash { get; set; } = string.Empty;

    public DateTime? ClaveCambiadaEn { get; set; }
    public bool ClaveCambioForzado { get; set; }

    /// <summary>
    /// Cifrado con la llave maestra, nunca en claro. Con este secreto legible,
    /// cualquiera con acceso a la base genera códigos válidos y el segundo
    /// factor deja de ser un segundo factor.
    /// </summary>
    public byte[]? SecretoDosFactor { get; set; }

    public bool DosfaActivo { get; set; }
    public DateTime? DosfaActivadoEn { get; set; }

    /// <summary>
    /// La ventana TOTP del último código aceptado.
    ///
    /// Un código vale 30 segundos, y durante esos 30 segundos sigue siendo
    /// válido tantas veces como se presente. Quien lo vea por encima del
    /// hombro tendría media ventana para usarlo él. Guardando el último paso,
    /// el segundo intento con el mismo código se rechaza.
    /// </summary>
    public long? DosfaUltimoPaso { get; set; }

    public int IntentosFallidos { get; set; }
    public DateTime? BloqueadoHasta { get; set; }
    public DateTime? UltimoIngresoEn { get; set; }
    public System.Net.IPAddress? UltimoIngresoIp { get; set; }

    public bool EsSuperAdmin { get; set; }

    public ICollection<UsuarioRol> Roles { get; set; } = [];

    public bool EstaBloqueado => BloqueadoHasta is not null && BloqueadoHasta > DateTime.UtcNow;
}
