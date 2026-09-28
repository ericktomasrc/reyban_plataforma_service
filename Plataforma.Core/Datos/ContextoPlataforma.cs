using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Plataforma.Core.Dominio;

namespace Plataforma.Core.Datos;

/// <summary>
/// El contexto de EF de la plataforma.
///
/// UNA COSA QUE NO HACE, Y ES IMPORTANTE: no crea ni migra el esquema.
///
/// No hay migraciones de EF. El esquema lo definen los archivos .sql de
/// <c>migraciones/</c>, que se corren a mano, en orden y una sola vez. Eso es
/// deliberado: todo lo que sostiene este sistema —los disparadores de
/// auditoría, las políticas de Row Level Security, los índices parciales, los
/// candados de solo-inserción— no se puede expresar con migraciones de EF.
/// Si EF generara el esquema, produciría unas tablas parecidas y sin ninguna
/// de esas defensas.
///
/// Aquí EF solo LEE y ESCRIBE filas.
/// </summary>
public class ContextoPlataforma(DbContextOptions<ContextoPlataforma> opciones)
    : DbContext(opciones)
{
    public DbSet<Empresa>        Empresas       => Set<Empresa>();
    public DbSet<Usuario>        Usuarios       => Set<Usuario>();
    public DbSet<Permiso>        Permisos       => Set<Permiso>();
    public DbSet<Rol>            Roles          => Set<Rol>();
    public DbSet<RolPermiso>     RolPermisos    => Set<RolPermiso>();
    public DbSet<UsuarioRol>     UsuarioRoles   => Set<UsuarioRol>();
    public DbSet<Modulo>         Modulos        => Set<Modulo>();
    public DbSet<EmpresaModulo>  EmpresaModulos => Set<EmpresaModulo>();
    public DbSet<Sesion>         Sesiones       => Set<Sesion>();
    public DbSet<EventoSeguridad> Eventos       => Set<EventoSeguridad>();

    // SOLO LECTURA desde la aplicación: las filas las escribe un disparador de
    // la base, en la misma transacción que el cambio que las provocó.
    public DbSet<Bitacora>       Bitacora      => Set<Bitacora>();

    // Vistas. Solo lectura.
    public DbSet<PermisoEfectivo> PermisosEfectivos => Set<PermisoEfectivo>();
    public DbSet<EntradaMenu>     Menu              => Set<EntradaMenu>();

    // Resultados de las dos funciones de ingreso. Solo por FromSql.
    public DbSet<UsuarioParaIngreso> UsuariosParaIngreso => Set<UsuarioParaIngreso>();
    public DbSet<SesionResuelta>     SesionesResueltas   => Set<SesionResuelta>();
    public DbSet<InvitacionResuelta> InvitacionesResueltas => Set<InvitacionResuelta>();


    /// <summary>
    /// Fija quién es el usuario, de qué empresa y desde dónde, para esta
    /// transacción.
    ///
    /// LO LEEN DOS COSAS EN LA BASE: las políticas de aislamiento, para saber
    /// qué filas existen; y el disparador de auditoría, para anotar quién
    /// hizo cada cambio.
    ///
    /// TIENE QUE LLAMARSE DENTRO DE UNA TRANSACCIÓN. El valor es local a ella
    /// —con un pool de conexiones, uno que sobreviviera se filtraría a la
    /// petición del siguiente usuario— así que fuera de una transacción se
    /// pierde entre sentencia y sentencia, y el aislamiento devuelve cero
    /// filas SIN DAR NINGÚN ERROR.
    ///
    /// Los `::tipo` después de cada parámetro no son adorno: PostgreSQL no
    /// puede deducir el tipo de un parámetro nulo, y usuario y empresa lo son
    /// mientras nadie ha iniciado sesión.
    /// </summary>
    public Task FijarContextoAsync(
        Guid? usuarioId,
        Guid? empresaId,
        string? ip,
        string? agente,
        bool esSuperAdmin,
        CancellationToken ct = default)
    {
        var usuario = usuarioId?.ToString();
        var empresa = empresaId?.ToString();

        return Database.ExecuteSqlInterpolatedAsync(
            $"SELECT fijar_contexto({usuario}::uuid, {empresa}::uuid, {ip}::text, {agente}::text, {esSuperAdmin}::boolean)",
            ct);
    }


    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.ApplyConfigurationsFromAssembly(typeof(ContextoPlataforma).Assembly);
        AplicarAuditoria(mb);
        base.OnModelCreating(mb);
    }


    /// <summary>
    /// Aplica de golpe, a toda entidad que herede de <see cref="EntidadAuditada"/>,
    /// las reglas que de otro modo habría que repetir en cada configuración —
    /// y olvidar en la número catorce.
    /// </summary>
    private static void AplicarAuditoria(ModelBuilder mb)
    {
        // ToList() y no el enumerable directo: dentro del bucle se llama a
        // mb.Entity(), que modifica el modelo que se está recorriendo.
        var tipos = mb.Model.GetEntityTypes()
                            .Where(t => typeof(EntidadAuditada).IsAssignableFrom(t.ClrType))
                            .Select(t => t.ClrType)
                            .Distinct()
                            .ToList();

        foreach (var tipo in tipos)
        {
            var entidad = mb.Entity(tipo);

            // --- LOS CUATRO CAMPOS QUE LA APLICACIÓN NO ESCRIBE -------------
            //
            // Los rellena un disparador de PostgreSQL y pisa lo que venga. Si
            // EF los mandara igualmente, el valor se perdería en la base y el
            // log de SQL mostraría algo que no ocurrió — que es peor que no
            // mostrarlo.
            //
            // `Ignore` en las dos direcciones significa: no los mandes al
            // INSERT, no los mandes al UPDATE. Leerlos de vuelta sí, y por eso
            // sigue haciendo falta ValueGenerated.

            SoloDeLaBase(entidad, nameof(EntidadAuditada.CreadoEn),      ValueGenerated.OnAdd);
            SoloDeLaBase(entidad, nameof(EntidadAuditada.CreadoPor),     ValueGenerated.OnAdd);
            SoloDeLaBase(entidad, nameof(EntidadAuditada.ModificadoEn),  ValueGenerated.OnAddOrUpdate);
            SoloDeLaBase(entidad, nameof(EntidadAuditada.ModificadoPor), ValueGenerated.OnAddOrUpdate);

            // --- LA VERSIÓN, QUE SÍ VIAJA — PERO EN EL WHERE ----------------
            //
            // Como token de concurrencia, EF la pone en la condición del
            // UPDATE: «cambia esta fila solo si sigue en la versión que leí».
            // Si otro la tocó mientras tanto, no se actualiza ninguna fila y
            // EF lanza DbUpdateConcurrencyException en vez de pisar el cambio
            // ajeno en silencio.
            entidad.Property(nameof(EntidadAuditada.Version))
                   .IsConcurrencyToken()
                   .ValueGeneratedOnAddOrUpdate();

            // --- NADA BORRADO APARECE EN UNA CONSULTA NORMAL ----------------
            //
            // El filtro se salta con IgnoreQueryFilters(), que es justo lo que
            // hace falta en una pantalla de «ver desactivados».
            entidad.HasQueryFilter(FiltroActivo(tipo));
        }
    }


    private static void SoloDeLaBase(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder entidad,
        string propiedad,
        ValueGenerated cuando)
    {
        var p = entidad.Property(propiedad);
        p.Metadata.ValueGenerated = cuando;
        p.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        p.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }


    /// <summary>
    /// Construye <c>e =&gt; e.Activo</c> para un tipo que solo se conoce en
    /// tiempo de ejecución. Es la forma de tener un único filtro para todas
    /// las entidades sin escribirlo una vez por clase.
    /// </summary>
    private static LambdaExpression FiltroActivo(Type tipo)
    {
        var parametro = Expression.Parameter(tipo, "e");
        var cuerpo = Expression.Property(parametro, nameof(EntidadAuditada.Activo));
        return Expression.Lambda(cuerpo, parametro);
    }
}
