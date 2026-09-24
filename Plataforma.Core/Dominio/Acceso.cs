namespace Plataforma.Core.Dominio;

// =============================================================================
// PERMISOS, ROLES Y SUS UNIONES
//
// LA REGLA QUE ORDENA TODO ESTO: el código pregunta por PERMISOS, nunca por
// roles.
//
// Un endpoint dice «hace falta facturacion.emitir» y le da igual qué rol lo
// tenga. Así, cuando mañana se cree un rol nuevo que también deba emitir,
// ningún código se entera.
//
// Si los endpoints preguntaran por roles, cada rol nuevo sería un despliegue.
// =============================================================================


/// <summary>
/// Catálogo global de permisos. No lleva empresa: los permisos que existen
/// son los mismos para todos; lo que cambia es quién los tiene.
///
/// SE LLENA EN UNA MIGRACIÓN, NO DESDE UNA PANTALLA. Un permiso existe porque
/// hay una línea de código que lo comprueba; crearlos desde la interfaz
/// produciría permisos que no protegen nada, y —peor— la sensación de que sí.
/// </summary>
public class Permiso
{
    public string Codigo { get; set; } = string.Empty;

    /// <summary>
    /// Tiene que coincidir con el código de un módulo para que la entrada
    /// aparezca en el menú. Las áreas `plataforma` y `usuarios` no son
    /// módulos: son transversales y no salen ahí.
    /// </summary>
    public string Area { get; set; } = string.Empty;

    public string Descripcion { get; set; } = string.Empty;
}


/// <summary>
/// Un conjunto de permisos con nombre.
/// </summary>
public class Rol : EntidadAuditada
{
    /// <summary>
    /// Nulo = rol de sistema, disponible para todas las empresas.
    /// Con valor = rol propio de esa empresa.
    /// </summary>
    public Guid? EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    public string Nombre { get; set; } = string.Empty;
    public string? Descripcion { get; set; }

    /// <summary>
    /// El rol de administrador no se puede editar ni borrar. Si se le
    /// pudieran quitar permisos, alguien podría dejar a su empresa sin nadie
    /// capaz de administrarla, sin forma de recuperarlo desde dentro.
    /// </summary>
    public bool Editable { get; set; } = true;

    public ICollection<RolPermiso> Permisos { get; set; } = [];

    public bool EsDeSistema => EmpresaId is null;
}


public class RolPermiso : EntidadAuditada
{
    public Guid RolId { get; set; }
    public Rol Rol { get; set; } = null!;

    public string PermisoCodigo { get; set; } = string.Empty;
    public Permiso Permiso { get; set; } = null!;
}


public class UsuarioRol : EntidadAuditada
{
    public Guid UsuarioId { get; set; }
    public Usuario Usuario { get; set; } = null!;

    public Guid RolId { get; set; }
    public Rol Rol { get; set; } = null!;
}


// =============================================================================
// LAS DOS VISTAS
//
// El cálculo de permisos y de menú vive en la base, en una vista, para que
// exista en un solo sitio. Si cada endpoint reconstruyera la consulta, tarde
// o temprano una versión olvidaría mirar si el rol está activo.
// =============================================================================

/// <summary>Vista <c>permisos_efectivos</c>. Solo lectura.</summary>
public class PermisoEfectivo
{
    public Guid UsuarioId { get; set; }
    public Guid? EmpresaId { get; set; }
    public string PermisoCodigo { get; set; } = string.Empty;
}


/// <summary>
/// Vista <c>menu_usuario</c>. Solo lectura.
///
/// OCULTAR EL MENÚ NO ES SEGURIDAD. Esto es comodidad: evita que la gente
/// pulse cosas que le darían error. La validación real va en cada endpoint,
/// que comprueba el permiso otra vez.
/// </summary>
public class EntradaMenu
{
    public Guid UsuarioId { get; set; }
    public string Codigo { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Ruta { get; set; } = string.Empty;
    public string? Icono { get; set; }
    public int Orden { get; set; }
}
