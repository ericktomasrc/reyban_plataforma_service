using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plataforma.Core.Dominio;

namespace Plataforma.Core.Datos.Configuraciones;

// =============================================================================
// EL MAPEO ENTRE LAS CLASES Y LAS TABLAS
//
// POR QUÉ ESTÁN TODAS EN UN ARCHIVO Y NO UNA POR ENTIDAD:
//
// Con la convención de snake_case activada, EF traduce `CreadoEn` a
// `creado_en` y `EmpresaId` a `empresa_id` sin que haya que decirle nada. Lo
// único que queda por configurar es el nombre de la tabla —que en la base va
// en plural— y media docena de excepciones.
//
// Eso deja cada configuración en dos o tres líneas. Doce archivos de tres
// líneas se leen peor que uno de sesenta.
//
// Cuando una entidad necesite configuración de verdad, se saca a su propio
// archivo y ya está.
// =============================================================================


public class ConfiguracionEmpresa : IEntityTypeConfiguration<Empresa>
{
    public void Configure(EntityTypeBuilder<Empresa> e)
    {
        e.ToTable("empresas");

        e.HasMany(x => x.Usuarios)
         .WithOne(x => x.Empresa!)
         .HasForeignKey(x => x.EmpresaId);

        e.HasMany(x => x.Modulos)
         .WithOne(x => x.Empresa)
         .HasForeignKey(x => x.EmpresaId);
    }
}


public class ConfiguracionUsuario : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> e)
    {
        e.ToTable("usuarios");

        // La única columna cuyo nombre no sale de la convención: `2fa` no se
        // puede escribir en un nombre de propiedad de C#.
        e.Property(x => x.SecretoDosFactor).HasColumnName("secreto_2fa");

        // Calculadas en C#, no existen en la tabla.
        e.Ignore(x => x.EstaBloqueado);

        e.HasMany(x => x.Roles)
         .WithOne(x => x.Usuario)
         .HasForeignKey(x => x.UsuarioId);
    }
}


public class ConfiguracionPermiso : IEntityTypeConfiguration<Permiso>
{
    public void Configure(EntityTypeBuilder<Permiso> e)
    {
        e.ToTable("permisos");
        e.HasKey(x => x.Codigo);   // el código ES la clave, no hay id
    }
}


public class ConfiguracionRol : IEntityTypeConfiguration<Rol>
{
    public void Configure(EntityTypeBuilder<Rol> e)
    {
        e.ToTable("roles");
        e.Ignore(x => x.EsDeSistema);

        e.HasMany(x => x.Permisos)
         .WithOne(x => x.Rol)
         .HasForeignKey(x => x.RolId);
    }
}


public class ConfiguracionRolPermiso : IEntityTypeConfiguration<RolPermiso>
{
    public void Configure(EntityTypeBuilder<RolPermiso> e)
    {
        e.ToTable("rol_permiso");

        e.HasOne(x => x.Permiso)
         .WithMany()
         .HasForeignKey(x => x.PermisoCodigo);

        // La pareja es única aunque la clave primaria sea el id.
        e.HasIndex(x => new { x.RolId, x.PermisoCodigo }).IsUnique();
    }
}


public class ConfiguracionUsuarioRol : IEntityTypeConfiguration<UsuarioRol>
{
    public void Configure(EntityTypeBuilder<UsuarioRol> e)
    {
        e.ToTable("usuario_rol");

        e.HasOne(x => x.Rol)
         .WithMany()
         .HasForeignKey(x => x.RolId);

        e.HasIndex(x => new { x.UsuarioId, x.RolId }).IsUnique();
    }
}


public class ConfiguracionModulo : IEntityTypeConfiguration<Modulo>
{
    public void Configure(EntityTypeBuilder<Modulo> e)
    {
        e.ToTable("modulos");
        e.HasKey(x => x.Codigo);
    }
}


public class ConfiguracionEmpresaModulo : IEntityTypeConfiguration<EmpresaModulo>
{
    public void Configure(EntityTypeBuilder<EmpresaModulo> e)
    {
        e.ToTable("empresa_modulo");
        e.Ignore(x => x.Vigente);

        e.Property(x => x.Config).HasColumnType("jsonb");

        e.HasOne(x => x.Modulo)
         .WithMany()
         .HasForeignKey(x => x.ModuloCodigo);

        e.HasIndex(x => new { x.EmpresaId, x.ModuloCodigo }).IsUnique();
    }
}


public class ConfiguracionSesion : IEntityTypeConfiguration<Sesion>
{
    public void Configure(EntityTypeBuilder<Sesion> e)
    {
        e.ToTable("sesiones");

        // No hereda de EntidadAuditada, así que su id sí lo genera la base
        // con gen_random_uuid().
        e.Property(x => x.Id).ValueGeneratedOnAdd();

        e.HasIndex(x => x.TokenHash).IsUnique();
    }
}


public class ConfiguracionEventoSeguridad : IEntityTypeConfiguration<EventoSeguridad>
{
    public void Configure(EntityTypeBuilder<EventoSeguridad> e)
    {
        e.ToTable("eventos_seguridad");
        e.Property(x => x.Id).ValueGeneratedOnAdd();
        e.Property(x => x.OcurridoEn).ValueGeneratedOnAdd();
        e.Property(x => x.Detalle).HasColumnType("jsonb");
    }
}


public class ConfiguracionBitacora : IEntityTypeConfiguration<Bitacora>
{
    public void Configure(EntityTypeBuilder<Bitacora> e)
    {
        e.ToTable("bitacora");
        e.Property(x => x.Id).ValueGeneratedOnAdd();
        e.Property(x => x.OcurridoEn).ValueGeneratedOnAdd();
        e.Property(x => x.Antes).HasColumnType("jsonb");
        e.Property(x => x.Despues).HasColumnType("jsonb");

        // char(1) en la base: 'I', 'U' o 'D'.
        e.Property(x => x.Operacion).HasColumnType("char(1)");
    }
}


// --- LAS DOS VISTAS ---------------------------------------------------------
//
// `HasNoKey` + `ToView` le dice a EF dos cosas: que no puede escribir aquí, y
// que no intente seguir la pista de los cambios. Una vista es el resultado de
// un cálculo, no una fila que se pueda guardar.

public class ConfiguracionPermisoEfectivo : IEntityTypeConfiguration<PermisoEfectivo>
{
    public void Configure(EntityTypeBuilder<PermisoEfectivo> e)
    {
        e.HasNoKey().ToView("permisos_efectivos");
    }
}


public class ConfiguracionEntradaMenu : IEntityTypeConfiguration<EntradaMenu>
{
    public void Configure(EntityTypeBuilder<EntradaMenu> e)
    {
        e.HasNoKey().ToView("menu_usuario");
    }
}
