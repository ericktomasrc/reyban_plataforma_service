using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plataforma.Core.Dominio;

namespace Plataforma.Core.Datos.Configuraciones;

// 044 — Sucursales. Las columnas salen solas (snake_case); solo los nombres
// de tabla y los char(n), que sin decirlo serían text y el CHECK de la base
// rechazaría con un mensaje que habla del tipo y no del valor.

public class ConfiguracionSucursal : IEntityTypeConfiguration<Sucursal>
{
    public void Configure(EntityTypeBuilder<Sucursal> e)
    {
        e.ToTable("sucursales");
        e.Property(x => x.Codigo).HasColumnType("char(4)");
        e.Property(x => x.Ubigeo).HasColumnType("char(6)");
        e.Ignore(x => x.EsPrincipal);
    }
}

public class ConfiguracionSucursalSerie : IEntityTypeConfiguration<SucursalSerie>
{
    public void Configure(EntityTypeBuilder<SucursalSerie> e)
    {
        e.ToTable("sucursal_series");
        e.Property(x => x.TipoComprobante).HasColumnType("char(2)");
    }
}

public class ConfiguracionUsuarioSucursal : IEntityTypeConfiguration<UsuarioSucursal>
{
    public void Configure(EntityTypeBuilder<UsuarioSucursal> e)
    {
        e.ToTable("usuario_sucursal");
    }
}