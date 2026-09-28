using System.Net;

namespace Plataforma.Core.Dominio;

/// <summary>
/// Una fila de la bitácora: un cambio, en una tabla, hecho por alguien.
///
/// NO ES UNA <see cref="EntidadAuditada"/>, y no tendría sentido que lo fuera:
/// auditar la tabla de auditoría sería una recursión infinita. Su integridad
/// la garantiza otra cosa — el disparador `bitacora_inmutable`, que impide
/// actualizarla o borrarla, y la falta de permiso `DELETE` del rol.
///
/// **ESTA CLASE ES DE SOLO LECTURA PARA LA APLICACIÓN.** Nadie inserta aquí
/// desde C#: las filas las escribe `fn_auditar`, un disparador de la base, en
/// la misma transacción que el cambio que las provocó. Si el código pudiera
/// escribir aquí, podría también mentir.
/// </summary>
public class Bitacora
{
    public long Id { get; set; }
    public DateTime OcurridoEn { get; set; }

    public string Tabla { get; set; } = string.Empty;

    /// <summary>
    /// El identificador de la fila que cambió, como texto.
    ///
    /// Texto y no uuid porque no todas las claves primarias del sistema son
    /// uuid: `permisos` y `modulos` tienen clave de texto.
    /// </summary>
    public string FilaId { get; set; } = string.Empty;

    /// <summary>`I`, `U` o `D`.</summary>
    public char Operacion { get; set; }

    /// <summary>
    /// Nulo cuando el cambio no pertenece a ninguna empresa: una migración, el
    /// arranque, o algo que hizo el super administrador. Esas filas solo las
    /// ve él — lo decide la política de la base, no esta clase.
    /// </summary>
    public Guid? EmpresaId { get; set; }

    /// <summary>Nulo significa que no había sesión: un script o el arranque.</summary>
    public Guid? UsuarioId { get; set; }

    public IPAddress? Ip { get; set; }
    public string? Agente { get; set; }

    /// <summary>JSONB con la fila completa antes del cambio. Nulo en un INSERT.</summary>
    public string? Antes { get; set; }

    /// <summary>JSONB con la fila completa después. Nulo en un DELETE.</summary>
    public string? Despues { get; set; }

    /// <summary>
    /// Qué columnas cambiaron, en un UPDATE.
    ///
    /// Aquí aparecen también las sensibles —`clave_hash`, `secreto_2fa`— pero
    /// su **valor** va tapado en <see cref="Antes"/> y <see cref="Despues"/>.
    /// Queda el hecho, no el secreto: se puede saber que alguien cambió una
    /// contraseña sin poder saber cuál.
    /// </summary>
    public string[]? CamposCambiados { get; set; }
}
