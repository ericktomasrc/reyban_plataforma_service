using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plataforma.Core.Dominio;

namespace Plataforma.Core.Datos.Configuraciones;

// 050 — Guías de remisión y transporte guardado. Columnas en snake_case solas;
// aquí van las tablas, los char(n) y el jsonb (como texto: se arma y se lee
// con System.Text.Json en la API).

public class ConfiguracionGuia : IEntityTypeConfiguration<Guia>
{
    public void Configure(EntityTypeBuilder<Guia> e)
    {
        e.ToTable("guias");
        e.Property(x => x.Tipo).HasColumnType("char(2)");
        e.Property(x => x.Motivo).HasColumnType("char(2)");
        e.Property(x => x.Modalidad).HasColumnType("char(2)");
        e.Property(x => x.PartidaUbigeo).HasColumnType("char(6)");
        e.Property(x => x.LlegadaUbigeo).HasColumnType("char(6)");
        e.Property(x => x.PesoBruto).HasColumnType("numeric(14,3)");
        e.Property(x => x.Detalle).HasColumnType("jsonb");
        e.Ignore(x => x.Numero);
        e.HasMany(x => x.Bienes).WithOne().HasForeignKey(b => b.GuiaId);
    }
}

public class ConfiguracionGuiaBien : IEntityTypeConfiguration<GuiaBien>
{
    public void Configure(EntityTypeBuilder<GuiaBien> e)
    {
        e.ToTable("guia_bienes");
        e.Property(x => x.Cantidad).HasColumnType("numeric(14,3)");
    }
}

public class ConfiguracionVehiculo : IEntityTypeConfiguration<Vehiculo>
{
    public void Configure(EntityTypeBuilder<Vehiculo> e) => e.ToTable("vehiculos");
}

public class ConfiguracionConductor : IEntityTypeConfiguration<Conductor>
{
    public void Configure(EntityTypeBuilder<Conductor> e)
    {
        e.ToTable("conductores");
        e.Property(x => x.TipoDoc).HasColumnType("char(1)");
    }
}

public class ConfiguracionTransportista : IEntityTypeConfiguration<Transportista>
{
    public void Configure(EntityTypeBuilder<Transportista> e)
    {
        e.ToTable("transportistas");
        e.Property(x => x.Ruc).HasColumnType("char(11)");
    }
}

// 051: los eventos hechos en SOL.
public class ConfiguracionGuiaEvento : IEntityTypeConfiguration<GuiaEvento>
{
    public void Configure(EntityTypeBuilder<GuiaEvento> e) => e.ToTable("guia_eventos");
}