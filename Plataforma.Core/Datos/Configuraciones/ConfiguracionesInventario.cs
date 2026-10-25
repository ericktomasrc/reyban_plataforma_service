using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plataforma.Core.Dominio;

namespace Plataforma.Core.Datos.Configuraciones;

// 052 — Inventario. El saldo (inv_stock) y el kardex (inv_movimientos) no se
// mapean: los escribe la base y la API los lee con SQL.

public class ConfiguracionInventarioConfig : IEntityTypeConfiguration<InventarioConfig>
{
    public void Configure(EntityTypeBuilder<InventarioConfig> e)
    {
        e.ToTable("inventario_config");
        e.Property(x => x.Encendido).HasColumnName("activo_inventario");
    }
}

public class ConfiguracionInvSucursal : IEntityTypeConfiguration<InvSucursal>
{
    public void Configure(EntityTypeBuilder<InvSucursal> e) => e.ToTable("inv_sucursales");
}

public class ConfiguracionProveedor : IEntityTypeConfiguration<Proveedor>
{
    public void Configure(EntityTypeBuilder<Proveedor> e)
    {
        e.ToTable("proveedores");
        e.Property(x => x.TipoDocumento).HasColumnType("char(1)");
    }
}

public class ConfiguracionProductoCategoria : IEntityTypeConfiguration<ProductoCategoria>
{
    public void Configure(EntityTypeBuilder<ProductoCategoria> e) => e.ToTable("producto_categorias");
}

public class ConfiguracionInvOperacion : IEntityTypeConfiguration<InvOperacion>
{
    public void Configure(EntityTypeBuilder<InvOperacion> e)
    {
        e.ToTable("inv_operaciones");
        e.Property(x => x.Moneda).HasColumnType("char(3)");
        // El número lo pone la base (fn_inv_numerar): se lee de vuelta.
        e.Property(x => x.Numero).ValueGeneratedOnAdd();
        e.Ignore(x => x.Codigo);
        e.HasMany(x => x.Lineas).WithOne().HasForeignKey(l => l.OperacionId);
    }
}

public class ConfiguracionInvOperacionLinea : IEntityTypeConfiguration<InvOperacionLinea>
{
    public void Configure(EntityTypeBuilder<InvOperacionLinea> e)
    {
        e.ToTable("inv_operacion_lineas");
        e.Property(x => x.Cantidad).HasColumnType("numeric(18,4)");
        e.Property(x => x.CostoUnitario).HasColumnType("numeric(18,6)");
        e.Property(x => x.Sistema).HasColumnType("numeric(18,4)");
        e.Property(x => x.Contado).HasColumnType("numeric(18,4)");
    }
}

public class ConfiguracionInvTraslado : IEntityTypeConfiguration<InvTraslado>
{
    public void Configure(EntityTypeBuilder<InvTraslado> e)
    {
        e.ToTable("inv_traslados");
        e.HasMany(x => x.Lineas).WithOne().HasForeignKey(l => l.TrasladoId);
    }
}

public class ConfiguracionInvTrasladoLinea : IEntityTypeConfiguration<InvTrasladoLinea>
{
    public void Configure(EntityTypeBuilder<InvTrasladoLinea> e)
    {
        e.ToTable("inv_traslado_lineas");
        e.Property(x => x.Enviado).HasColumnType("numeric(18,4)");
        e.Property(x => x.Recibido).HasColumnType("numeric(18,4)");
    }
}