using System.Text.Json;
using Microsoft.EntityFrameworkCore;
// `ValueComparer` vive aquí, en ChangeTracking, y NO en Storage.ValueConversion
// —que es donde está `ValueConverter`—. Los dos suenan a lo mismo y están en
// sitios distintos.
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
        e.Ignore(x => x.FichaIncompleta);   // 046

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


// =============================================================================
// EL MÓDULO DE FACTURACIÓN
//
// Las tres existen en la base desde las migraciones 004 y 005. Hasta hoy no
// tenían clase en C#, que es justo la lección 18 del documento maestro del
// service: código escrito y sin cablear es código que no existe — aquí era al
// revés, tablas sin código, y el efecto es el mismo.
// =============================================================================

public class ConfiguracionCredencialFacturacion : IEntityTypeConfiguration<CredencialFacturacion>
{
    public void Configure(EntityTypeBuilder<CredencialFacturacion> e)
    {
        e.ToTable("credenciales_facturacion");

        e.HasOne(x => x.Empresa)
         .WithMany()
         .HasForeignKey(x => x.EmpresaId);
    }
}


public class ConfiguracionCliente : IEntityTypeConfiguration<Cliente>
{
    public void Configure(EntityTypeBuilder<Cliente> e)
    {
        e.ToTable("clientes");

        // `char(1)` en la base, string en C#. Sin esto EF lo mapearía a
        // `text` y el CHECK de la tabla rechazaría la inserción con un
        // mensaje que habla del tipo, no del valor.
        e.Property(x => x.TipoDocumento).HasColumnType("char(1)");
        e.Property(x => x.Ubigeo).HasColumnType("char(6)");
        e.Property(x => x.AfectacionHabitual).HasColumnType("char(2)");
    }
}


public class ConfiguracionProducto : IEntityTypeConfiguration<Producto>
{
    public void Configure(EntityTypeBuilder<Producto> e)
    {
        e.ToTable("productos");

        e.Property(x => x.TipoAfectacion).HasColumnType("char(2)");

        // DIEZ DECIMALES, los mismos que la columna. Con el `decimal(18,2)`
        // que EF pone por defecto, un precio de 12.3456 se guardaría como
        // 12.35 y el total del comprobante no cuadraría con el del servicio.
        e.Property(x => x.PrecioUnitario).HasColumnType("numeric(18,10)");
    }
}


public class ConfiguracionDocumento : IEntityTypeConfiguration<Documento>
{
    /// <summary>
    /// Las opciones con las que se escribe y se lee `datos_extra`.
    ///
    /// LAS MISMAS EN LOS DOS SENTIDOS, y por eso están en un campo y no
    /// escritas en cada llamada: dos juegos distintos —uno que pasa las claves
    /// a camelCase y otro que no— guardarían `OrdenCompra` y leerían
    /// `ordenCompra`, y el dato desaparecería sin que nadie viera un error.
    ///
    /// Sin política de nombres a propósito: las claves de este diccionario son
    /// datos del cliente, no nombres de propiedades, y tienen que volver tal
    /// como entraron.
    /// </summary>
    private static readonly JsonSerializerOptions JsonDatosExtra = new();

    /// <summary>
    /// 040: las cuotas y la detracción, en camelCase (los CHECK de la base
    /// miran `codigo` y `porcentaje`). Las mismas opciones al leer y escribir.
    /// </summary>
    private static readonly JsonSerializerOptions JsonPago = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Documento> e)
    {
        e.ToTable("documentos");

        e.Property(x => x.Tipo).HasColumnType("char(2)");
        e.Property(x => x.TipoNota).HasColumnType("char(2)");
        e.Property(x => x.Moneda).HasColumnType("char(3)");

        // `text[]` de PostgreSQL. Sin decirlo, EF intentaría una tabla aparte.
        e.Property(x => x.Observaciones).HasColumnType("text[]");

        // ═════════════════════════════════════════════════════════════════════
        // EL JSONB SE MAPEA A UN DICCIONARIO, no a una cadena. Así el código
        // que lo lee no tiene que acordarse de deserializar —y el que se
        // olvide no compila, en vez de fallar al ejecutarse.
        //
        // PERO LA CONVERSIÓN HAY QUE ESCRIBIRLA, y esto costó un fallo en
        // producción de desarrollo: sin ella, Npgsql intenta pasar el
        // `Dictionary` tal cual como parámetro `jsonb` y revienta al guardar —
        //
        //     Writing values of 'Dictionary`2' is not supported for parameters
        //     having NpgsqlDbType 'Jsonb' ... required dynamic JSON
        //     serialization, which requires an explicit opt-in
        //
        // Desde Npgsql 8 la serialización por reflexión está apagada de fábrica.
        // Hay dos formas de encenderla:
        //
        //   1. `EnableDynamicJson()` al construir la fuente de datos. Una línea,
        //      pero la enciende para TODO el proyecto: cualquier propiedad que
        //      alguien marque como jsonb pasaría a serializarse sola, con las
        //      opciones que Npgsql decida y sin que se vea en ningún sitio.
        //
        //   2. Esta: decir aquí mismo cómo se convierte. Es explícito, se lee
        //      donde está el mapeo, y deja la fuente de datos sin ajustes
        //      globales que sorprendan tres archivos más allá.
        //
        // Se eligió la 2, que además deja este jsonb igual que los otros de la
        // base —`bitacora.antes`, `eventos_seguridad.detalle`—: por el cable
        // viaja una cadena.
        // ═════════════════════════════════════════════════════════════════════
        e.Property(x => x.DatosExtra)
         .HasColumnType("jsonb")
         .HasConversion(
             v => JsonSerializer.Serialize(v, JsonDatosExtra),
             v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonDatosExtra) ?? new())
         // EL COMPARADOR NO ES OPCIONAL con un diccionario. EF detecta cambios
         // comparando con la copia que guardó al leer, y sin comparador compara
         // REFERENCIAS: mutar el diccionario en vez de asignar uno nuevo
         //     d.DatosExtra["ordenCompra"] = "OC-1";
         // no se guardaría nunca, en silencio y sin error.
         .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>>(
             (a, b) => JsonSerializer.Serialize(a, JsonDatosExtra) == JsonSerializer.Serialize(b, JsonDatosExtra),
             v => JsonSerializer.Serialize(v, JsonDatosExtra).GetHashCode(),
             v => JsonSerializer.Deserialize<Dictionary<string, string>>(
                      JsonSerializer.Serialize(v, JsonDatosExtra), JsonDatosExtra) ?? new()));

        // Los diez totales, con los dos decimales de la columna. Con el
        // `decimal(18,2)` que EF pondría por defecto daría igual aquí, pero
        // escrito se lee y no hay que ir a mirar la migración.
        foreach (var total in new[]
        {
            nameof(Documento.TotalGravado),    nameof(Documento.TotalExonerado),
            nameof(Documento.TotalInafecto),   nameof(Documento.TotalGratuito),
            nameof(Documento.TotalExportacion), nameof(Documento.TotalDescuentos),
            nameof(Documento.TotalIgv),        nameof(Documento.TotalIsc),
            nameof(Documento.TotalOtros),      nameof(Documento.ImporteTotal),
        })
        {
            e.Property(total).HasColumnType("numeric(18,2)");
        }

        // 040: forma de pago, cuotas y detracción. El jsonb, como `datos_extra`:
        // conversión escrita aquí y comparador por contenido.
        e.Property(x => x.Cuotas)
         .HasColumnType("jsonb")
         .HasConversion(
             v => JsonSerializer.Serialize(v, JsonPago),
             v => JsonSerializer.Deserialize<List<CuotaDocumento>>(v, JsonPago) ?? new())
         .Metadata.SetValueComparer(new ValueComparer<List<CuotaDocumento>>(
             (a, b) => JsonSerializer.Serialize(a, JsonPago) == JsonSerializer.Serialize(b, JsonPago),
             v => JsonSerializer.Serialize(v, JsonPago).GetHashCode(),
             v => v.ToList()));

        e.Property(x => x.Detraccion)
         .HasColumnType("jsonb")
         .HasConversion(
             v => JsonSerializer.Serialize(v, JsonPago),
             v => JsonSerializer.Deserialize<DetraccionDocumento>(v, JsonPago));

        // Calculadas en C#, no existen en la tabla.
        e.Ignore(x => x.Numero);
        e.Ignore(x => x.SePuedeEditar);

        e.HasOne(x => x.Cliente)
         .WithMany()
         .HasForeignKey(x => x.ClienteId);

        e.HasMany(x => x.Lineas)
         .WithOne()
         .HasForeignKey(x => x.DocumentoId);
    }
}


public class ConfiguracionDocumentoLinea : IEntityTypeConfiguration<DocumentoLinea>
{
    public void Configure(EntityTypeBuilder<DocumentoLinea> e)
    {
        e.ToTable("documento_lineas");

        e.Property(x => x.TipoAfectacion).HasColumnType("char(2)");

        // DIEZ DECIMALES en cantidad y en los dos unitarios, los mismos que la
        // columna. Redondear aquí a dos produce diferencias de céntimos que
        // después no cuadran con el total que devolvió el servicio.
        e.Property(x => x.Cantidad).HasColumnType("numeric(18,10)");
        e.Property(x => x.ValorUnitario).HasColumnType("numeric(18,10)");
        e.Property(x => x.PrecioUnitario).HasColumnType("numeric(18,10)");

        foreach (var importe in new[]
        {
            nameof(DocumentoLinea.Descuento), nameof(DocumentoLinea.Igv),
            nameof(DocumentoLinea.Isc),       nameof(DocumentoLinea.OtrosTributos),
            nameof(DocumentoLinea.ValorVenta), nameof(DocumentoLinea.ImporteTotal),
        })
        {
            e.Property(importe).HasColumnType("numeric(18,2)");
        }
    }
}


/// <summary>
/// El historial. SOLO LECTURA PARA LA APLICACIÓN: lo escribe un disparador.
///
/// No es `EntidadAuditada` —no tiene `activo` ni `version`— y tampoco le hace
/// falta: la tabla ya rechaza UPDATE y DELETE con su propio disparador.
/// </summary>
public class ConfiguracionDocumentoHistorial : IEntityTypeConfiguration<DocumentoHistorial>
{
    public void Configure(EntityTypeBuilder<DocumentoHistorial> e)
    {
        e.ToTable("documento_historial");

        // La columna `detalle jsonb` de la tabla NO se mapea: hoy no la lee
        // nadie, y una propiedad que existe «por si acaso» es una que alguien
        // acabará rellenando sin saber quién la consume.
    }
}