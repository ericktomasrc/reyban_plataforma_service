using System.Net;

namespace Plataforma.Core.Seguridad;

/// <summary>
/// Quién está haciendo esta petición.
///
/// Lo rellena el middleware al principio de cada petición y lo leen los
/// endpoints. Es un servicio con ámbito de petición: cada una tiene el suyo y
/// no se mezclan.
///
/// LOS MISMOS VALORES VIAJAN A POSTGRESQL con fijar_contexto(), y de ahí los
/// leen dos cosas: las políticas de aislamiento —para saber qué filas existen
/// para esta sesión— y el disparador de auditoría, para anotar quién hizo
/// cada cambio.
/// </summary>
public sealed class ContextoPeticion
{
    public Guid? UsuarioId { get; private set; }
    public Guid? EmpresaId { get; private set; }
    public Guid? SesionId  { get; private set; }
    public bool  EsSuperAdmin { get; private set; }

    public IPAddress? Ip { get; set; }
    public string? Agente { get; set; }

    /// <summary>
    /// Los permisos de este usuario, ya resueltos. Se cargan una vez por
    /// petición, no una vez por comprobación.
    /// </summary>
    public IReadOnlySet<string> Permisos { get; private set; } =
        new HashSet<string>();

    public bool Autenticado => UsuarioId is not null;

    /// <summary>
    /// Falso mientras la sesión exista pero no haya superado el segundo
    /// factor. En ese estado solo se puede validar el código: nada más.
    /// </summary>
    public bool SesionCompleta { get; private set; }

    /// <summary>
    /// Cierto mientras el usuario no haya cambiado la contraseña con la que
    /// se le dio de alta. En ese estado no puede hacer nada más que cambiarla.
    ///
    /// Un cambio obligatorio que el usuario puede ignorar no es obligatorio:
    /// la contraseña inicial la escribió otra persona y viajó por correo.
    /// </summary>
    public bool DebeCambiarClave { get; private set; }

    // =========================================================================
    // SUPLANTACIÓN
    //
    // `UsuarioId` y `EmpresaId` de arriba son la identidad EFECTIVA: a quién ve
    // la aplicación. Mientras hay suplantación son del usuario suplantado, y
    // `EsSuperAdmin` es falso — es lo que hace que el super administrador vea
    // exactamente lo que ve su cliente.
    //
    // `SuplantadorId` es quien está de verdad al teclado. Se usa para dos cosas
    // y ninguna más:
    //
    //   1. La autoría. El middleware fija el contexto de PostgreSQL con ESTE
    //      usuario, no con el efectivo, así que cualquier fila que se escriba
    //      durante una suplantación lleva el nombre del super administrador.
    //      Nadie puede hacer que la bitácora diga que algo lo hizo otro.
    //   2. La barra de aviso y el botón de volver.
    // =========================================================================

    public Guid? SuplantadorId { get; private set; }
    public string? SuplantadorNombre { get; private set; }
    public string? SuplantadoNombre { get; private set; }
    public DateTime? SuplantacionInicio { get; private set; }

    public bool Suplantando => SuplantadorId is not null;

    /// <summary>
    /// Quién responde de lo que pase en esta petición.
    ///
    /// Es el suplantador cuando hay suplantación, y el propio usuario cuando
    /// no. Va a `fijar_contexto`, de donde lo lee el disparador de auditoría.
    /// </summary>
    public Guid? AutorId => SuplantadorId ?? UsuarioId;

    /// <summary>067: del equipo de la plataforma con permisos parciales (null = todo).</summary>
    public IReadOnlySet<string>? PlataformaPermisos { get; private set; }

    public void FijarPlataformaPermisos(IEnumerable<string>? permisos) =>
        PlataformaPermisos = permisos?.ToHashSet();

    /// <summary>
    /// El super administrador tiene todo, salvo que sea del equipo con
    /// permisos parciales (067): entonces, de los «plataforma.*», solo los suyos.
    /// </summary>
    public bool Tiene(string permiso) =>
        EsSuperAdmin
            ? PlataformaPermisos is null || !permiso.StartsWith("plataforma.", StringComparison.Ordinal) || PlataformaPermisos.Contains(permiso)
            : Permisos.Contains(permiso);

    // =========================================================================
    // 046 — EL ALCANCE Y LA SUCURSAL ELEGIDA ARRIBA
    //
    // Los calcula `fijar_alcance` en la base (una vez por petición, en el
    // middleware) y quedan fijados también allí: la regla de `documentos`
    // recorta sola. Aquí están para la pantalla (/api/yo), para emitir desde
    // la sucursal correcta y para las reglas del jefe de sucursal.
    //
    //   empresa   ve todo (sin fila = esto; el dueño y el Administrador siempre)
    //   sucursal  ve lo de sus sucursales
    //   propio    ve solo lo que emitió
    // =========================================================================

    public string Alcance { get; private set; } = "empresa";

    /// <summary>La sucursal elegida arriba. Nula = «Todas».</summary>
    public Guid? SucursalVista { get; private set; }

    /// <summary>Con la que entra y la que viene elegida al emitir.</summary>
    public Guid? SucursalDefecto { get; private set; }

    /// <summary>Las que puede elegir arriba (activas).</summary>
    public IReadOnlyList<SucursalDeAlcance> SucursalesAlcance { get; private set; } = [];

    /// <summary>Cuántas sucursales activas tiene la empresa (con una sola no hay selector).</summary>
    public int SucursalesEmpresa { get; private set; }

    public bool VeTodaLaEmpresa => EsSuperAdmin || Alcance == "empresa";

    public void FijarAlcance(string alcance, Guid? vista, Guid? defecto,
                             IReadOnlyList<SucursalDeAlcance> sucursales, int sucursalesEmpresa)
    {
        Alcance           = alcance is "sucursal" or "propio" ? alcance : "empresa";
        SucursalVista     = vista;
        SucursalDefecto   = defecto;
        SucursalesAlcance = sucursales;
        SucursalesEmpresa = sucursalesEmpresa;
    }

    public void Identificar(
        Guid usuarioId,
        Guid? empresaId,
        Guid sesionId,
        bool esSuperAdmin,
        bool sesionCompleta,
        bool debeCambiarClave,
        IEnumerable<string> permisos,
        Guid? suplantadorId = null,
        string? suplantadorNombre = null,
        string? suplantadoNombre = null,
        DateTime? suplantacionInicio = null)
    {
        UsuarioId        = usuarioId;
        EmpresaId        = empresaId;
        SesionId         = sesionId;
        EsSuperAdmin     = esSuperAdmin;
        SesionCompleta   = sesionCompleta;
        DebeCambiarClave = debeCambiarClave;
        Permisos         = permisos.ToHashSet();

        SuplantadorId      = suplantadorId;
        SuplantadorNombre  = suplantadorNombre;
        SuplantadoNombre   = suplantadoNombre;
        SuplantacionInicio = suplantacionInicio;
    }
}

/// <summary>046 — Una sucursal que la persona puede elegir arriba.</summary>
public sealed record SucursalDeAlcance(Guid Id, string Codigo, string Nombre, bool Activo);
