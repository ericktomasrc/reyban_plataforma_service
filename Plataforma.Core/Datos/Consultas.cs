using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Plataforma.Core.Datos;

// =============================================================================
// LOS RESULTADOS DE LAS FUNCIONES MARCADAS
//
// No son tablas y no se pueden guardar: son la forma que devuelven las
// funciones SECURITY DEFINER que tienen que devolver más de un valor —
// `buscar_usuario_para_ingreso`, `resolver_sesion` y `resolver_invitacion`.
//
// YA NO SON DOS. Este comentario decía «las dos únicas funciones del sistema
// marcadas SECURITY DEFINER», y con las migraciones 003, 007 y 010 ya son
// muchas más. Aquí solo están las que devuelven varias columnas; las demás
// devuelven un escalar y no necesitan tipo.
//
// Y LA LECCIÓN, que vale más que la corrección: un comentario que afirma un
// número envejece mal y nadie lo revisa. Por eso este ya no dice cuántas son.
// Si hace falta el número, lo da la base:
//
//   SELECT proname FROM pg_proc WHERE prosecdef AND pronamespace = 'public'::regnamespace;
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

    // --- Suplantación: nulos casi siempre -----------------------------------
    //
    // Con valor, `UsuarioId` y `EmpresaId` de arriba NO son de quien abrió la
    // sesión: son del usuario al que está viendo. Quien está de verdad al
    // teclado es `SuplantadorId`, y solo sirve para dos cosas — la barra de
    // aviso y la autoría de la bitácora.

    public Guid? SuplantadorId { get; set; }
    public string? SuplantadorNombre { get; set; }
    public string? SuplantadoNombre { get; set; }
    public DateTime? SuplantacionInicio { get; set; }
}


/// <summary>
/// Lo que devuelve <c>resolver_invitacion</c>, la tercera puerta.
///
/// Misma historia que las dos de arriba: quien abre un enlace de invitación no
/// tiene sesión —todavía no tiene contraseña— y sin contexto el aislamiento no
/// deja leer su propia fila.
/// </summary>
public class InvitacionResuelta
{
    public Guid TokenId { get; set; }
    public Guid UsuarioId { get; set; }
    public string Correo { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Nulo para un super administrador, que no tiene empresa.</summary>
    public string? Empresa { get; set; }

    public string Proposito { get; set; } = string.Empty;
    public DateTime ExpiraEn { get; set; }
    public DateTime? UsadoEn { get; set; }
    public DateTime? AnuladoEn { get; set; }
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

public class ConfiguracionInvitacionResuelta : IEntityTypeConfiguration<InvitacionResuelta>
{
    public void Configure(EntityTypeBuilder<InvitacionResuelta> e) =>
        e.HasNoKey().ToView(null);
}
