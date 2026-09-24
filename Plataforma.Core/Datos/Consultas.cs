using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Plataforma.Core.Datos;

// =============================================================================
// LOS RESULTADOS DE LAS DOS FUNCIONES DE INGRESO
//
// No son tablas y no se pueden guardar: son la forma que devuelven
// `buscar_usuario_para_ingreso` y `resolver_sesion`, las dos únicas funciones
// del sistema marcadas SECURITY DEFINER.
//
// Existen porque el aislamiento está bien puesto y eso crea un problema de
// huevo y gallina: para saber de qué empresa es quien llama hay que leer su
// fila, y para leer su fila hay que saber de qué empresa es. Las funciones
// rompen el círculo, devolviendo solo lo justo.
// =============================================================================

public class UsuarioParaIngreso
{
    public Guid Id { get; set; }
    public Guid? EmpresaId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string ClaveHash { get; set; } = string.Empty;
    public bool Activo { get; set; }
    public bool EsSuperAdmin { get; set; }
    public int IntentosFallidos { get; set; }
    public DateTime? BloqueadoHasta { get; set; }
    public bool DosfaActivo { get; set; }
    public bool ClaveCambioForzado { get; set; }

    /// <summary>
    /// Falso si su empresa está desactivada o suspendida. Para un super
    /// administrador, que no tiene empresa, siempre es verdadero.
    /// </summary>
    public bool EmpresaOperativa { get; set; }
}


public class SesionResuelta
{
    public Guid SesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid? EmpresaId { get; set; }
    public bool EsSuperAdmin { get; set; }
    public bool DosfaSuperado { get; set; }
    public bool ClaveCambioForzado { get; set; }
    public DateTime ExpiraEn { get; set; }
    public DateTime? RevocadaEn { get; set; }
    public bool UsuarioActivo { get; set; }
    public bool EmpresaOperativa { get; set; }
}


// `HasNoKey().ToView(null)` significa: esto no vive en ninguna tabla ni vista,
// solo se obtiene con FromSql. EF no le sigue la pista ni intenta guardarlo.

public class ConfiguracionUsuarioParaIngreso : IEntityTypeConfiguration<UsuarioParaIngreso>
{
    public void Configure(EntityTypeBuilder<UsuarioParaIngreso> e) =>
        e.HasNoKey().ToView(null);
}

public class ConfiguracionSesionResuelta : IEntityTypeConfiguration<SesionResuelta>
{
    public void Configure(EntityTypeBuilder<SesionResuelta> e) =>
        e.HasNoKey().ToView(null);
}
