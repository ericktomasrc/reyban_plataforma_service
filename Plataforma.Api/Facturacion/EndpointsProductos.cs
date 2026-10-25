using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

public record PeticionProducto(
    string Codigo,
    string Descripcion,
    string UnidadMedida,
    string TipoAfectacion,
    decimal PrecioUnitario,
    bool IncluyeIgv,
    string? CodigoSunat);


/// <summary>
/// El catálogo de lo que vende cada empresa.
///
/// NO ES UN INVENTARIO. No hay stock, ni almacenes, ni movimientos, ni costo.
/// Es la lista de lo que aparece en una línea de factura, y nada más. El día
/// que haga falta llevar existencias, eso es otro módulo con sus propias
/// tablas — meterlo aquí convertiría la facturación en medio ERP sin que nadie
/// lo decidiera.
/// </summary>
public static class EndpointsProductos
{
    /// <summary>
    /// Catálogo 07 de SUNAT, el subconjunto que tiene sentido en un catálogo
    /// de productos: los tipos de venta.
    ///
    /// NO ESTÁN LOS DE OPERACIÓN GRATUITA —11 a 16, 21, 31 a 37—, y es a
    /// propósito: una bonificación no es una propiedad del producto, es algo
    /// que se decide al vender. El mismo saco de cemento se cobra hoy y se
    /// regala mañana.
    /// </summary>
    /// <remarks>
    /// `internal` y no `private` porque la ficha del cliente valida contra esta
    /// misma lista su afectación habitual. Escrita dos veces, el día que se
    /// añada un código el otro sitio se queda atrás.
    /// </remarks>
    internal static readonly Dictionary<string, string> Afectaciones = new()
    {
        ["10"] = "Gravado con IGV",
        ["20"] = "Exonerado",
        ["30"] = "Inafecto",
        ["40"] = "Exportación",
        ["17"] = "IVAP (arroz pilado)"
    };

    /// <summary>
    /// Las unidades del catálogo 03 que se usan de verdad.
    ///
    /// EL CATÁLOGO ENTERO TIENE CIENTOS y ninguna pantalla debería enseñarlos
    /// todos. Estas cubren lo que factura una exportadora, una importadora o
    /// una ferretería, que son los tres perfiles de clientes a la vista. Se
    /// admite cualquier otra escrita a mano: la lista es para el desplegable,
    /// no para prohibir.
    /// </summary>
    internal static readonly Dictionary<string, string> Unidades = new()   // 22 oct: también la importación
    {
        ["NIU"] = "Unidad",
        ["ZZ"]  = "Servicio",
        ["KGM"] = "Kilogramo",
        ["TNE"] = "Tonelada",
        ["GRM"] = "Gramo",
        ["LTR"] = "Litro",
        ["MTR"] = "Metro",
        ["MTK"] = "Metro cuadrado",
        ["MTQ"] = "Metro cúbico",
        ["BG"]  = "Bolsa",
        ["BX"]  = "Caja",
        ["PK"]  = "Paquete",
        ["SET"] = "Juego",
        ["GLL"] = "Galón",
        ["DZN"] = "Docena",
        ["CEN"] = "Ciento",
        ["MLL"] = "Millar",
        ["HUR"] = "Hora",
        ["DAY"] = "Día",
        ["MON"] = "Mes"
    };

    public static void MapearProductos(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/productos")
                         .WithTags("Facturación · productos");


        // =====================================================================
        // LOS CATÁLOGOS, para los desplegables
        //
        // SALEN DEL BACK Y NO ESCRITOS EN EL FRONT. Los códigos de SUNAT son
        // los mismos para el catálogo de productos, para la línea de una
        // factura y para lo que viaja al servicio. Escritos dos veces, el día
        // que cambie uno el otro se queda atrás.
        // =====================================================================
        grupo.MapGet("/catalogos", () => Results.Ok(new
        {
            afectaciones = Afectaciones.Select(x => new { codigo = x.Key, nombre = x.Value }),
            unidades     = Unidades.Select(x => new { codigo = x.Key, nombre = x.Value })
        }))
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Los catálogos de SUNAT que usan las pantallas");


        // =====================================================================
        // EL BUSCADOR DEL CÓDIGO DE PRODUCTO SUNAT (catálogo 25)
        //
        // «azúcar» → las 20 filas que más se parecen. Con 8 dígitos, ese código.
        // El catálogo está en memoria (CatalogoSunat); no toca la base.
        // =====================================================================
        grupo.MapGet("/codigos-sunat", (string? q) =>
        {
            if (!CatalogoSunat.Disponible)
                return Results.Json(new
                {
                    error = "El catálogo de SUNAT no está disponible ahora. " +
                            "Puedes guardar el producto sin código y ponerlo después."
                }, statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Ok(CatalogoSunat.Buscar(q));
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Buscar en el catálogo 25 de SUNAT (código de producto)");


        // =====================================================================
        grupo.MapGet("/", async (
            string? q, bool? incluirInactivos,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var consulta = incluirInactivos == true
                ? db.Productos.IgnoreQueryFilters()
                : db.Productos;

            consulta = consulta.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(q))
            {
                var texto = q.Trim().ToLowerInvariant();

                consulta = consulta.Where(p =>
                    p.Codigo.ToLower().Contains(texto) ||
                    p.Descripcion.ToLower().Contains(texto));
            }

            var productos = await consulta
                .OrderBy(p => p.Descripcion)
                .Take(200)
                .Select(p => new
                {
                    p.Id, p.Codigo, p.Descripcion, p.UnidadMedida, p.TipoAfectacion,
                    p.PrecioUnitario, p.IncluyeIgv, p.CodigoSunat, p.Activo
                })
                .ToListAsync(ct);

            // El nombre del código SUNAT sale del catálogo en memoria, después
            // de leer: así la lista enseña «50161509 · Azúcares naturales» y
            // se ve de un vistazo si el código es el que toca.
            return Results.Ok(productos.Select(p => new
            {
                p.Id, p.Codigo, p.Descripcion, p.UnidadMedida, p.TipoAfectacion,
                p.PrecioUnitario, p.IncluyeIgv, p.CodigoSunat,
                CodigoSunatNombre = CatalogoSunat.Nombre(p.CodigoSunat),
                p.Activo
            }));
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Buscar productos de mi empresa");


        // =====================================================================
        // 24 OCT — LA LISTA DE LA PANTALLA «Productos»: paginada, lo más nuevo
        // arriba (o A–Z) y con filtros. El GET de arriba sigue igual: lo usan
        // los buscadores de producto al emitir.
        // =====================================================================
        grupo.MapGet("/pagina", async (
            string? q, string? unidad, string? afectacion, string? estado, string? orden, int? pagina, int? porPagina,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var pp = Listados.PorPagina(porPagina);
            var pg = Listados.Pagina(pagina);

            var consulta = (estado is "inactivos" or "todos" ? db.Productos.IgnoreQueryFilters() : db.Productos).AsNoTracking();
            if (estado == "inactivos") consulta = consulta.Where(x => !x.Activo);
            if (!string.IsNullOrWhiteSpace(unidad)) consulta = consulta.Where(x => x.UnidadMedida == unidad);
            if (!string.IsNullOrWhiteSpace(afectacion)) consulta = consulta.Where(x => x.TipoAfectacion == afectacion);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var texto = q.Trim().ToLowerInvariant();
                consulta = consulta.Where(x => x.Codigo.ToLower().Contains(texto) || x.Descripcion.ToLower().Contains(texto));
            }

            var total = await consulta.CountAsync(ct);
            consulta = orden == "az"
                ? consulta.OrderBy(x => x.Descripcion).ThenBy(x => x.Id)
                : consulta.OrderByDescending(x => x.CreadoEn).ThenBy(x => x.Id);

            var filas = await consulta.Skip((pg - 1) * pp).Take(pp)
                .Select(x => new
                {
                    x.Id, x.Codigo, x.Descripcion, x.UnidadMedida, x.TipoAfectacion,
                    x.PrecioUnitario, x.IncluyeIgv, x.CodigoSunat, x.Activo, x.CreadoEn
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                total, pagina = pg, porPagina = pp,
                filas = filas.Select(x => new
                {
                    x.Id, x.Codigo, x.Descripcion, x.UnidadMedida, x.TipoAfectacion,
                    x.PrecioUnitario, x.IncluyeIgv, x.CodigoSunat,
                    CodigoSunatNombre = CatalogoSunat.Nombre(x.CodigoSunat),
                    x.Activo, x.CreadoEn
                })
            });
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("La lista de productos, paginada y con filtros");


        // =====================================================================
        grupo.MapPost("/", async (
            PeticionProducto p, ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            if (Revisar(p) is { } error) return Results.BadRequest(new { error });

            var codigo = p.Codigo.Trim();

            // El índice único es sobre `lower(codigo)`, así que «P001» y «p001»
            // son el mismo producto. Se comprueba igual aquí para dar un
            // mensaje que se entienda.
            var repetido = await db.Productos
                .AnyAsync(x => x.Codigo.ToLower() == codigo.ToLower(), ct);

            if (repetido)
                return Results.Conflict(new { error = $"Ya tienes un producto con el código {codigo}." });

            var producto = new Producto
            {
                EmpresaId       = ctx.EmpresaId!.Value,
                Codigo          = codigo,
                Descripcion     = p.Descripcion.Trim(),
                UnidadMedida    = p.UnidadMedida.Trim().ToUpperInvariant(),
                TipoAfectacion  = p.TipoAfectacion.Trim(),
                PrecioUnitario  = p.PrecioUnitario,
                IncluyeIgv      = p.IncluyeIgv,
                CodigoSunat     = string.IsNullOrWhiteSpace(p.CodigoSunat) ? null : p.CodigoSunat.Trim()
            };

            db.Productos.Add(producto);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/facturacion/productos/{producto.Id}",
                new { producto.Id, producto.Codigo, producto.Descripcion });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Dar de alta un producto");


        // =====================================================================
        grupo.MapPut("/{id:guid}", async (
            Guid id, PeticionProducto p, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (Revisar(p) is { } error) return Results.BadRequest(new { error });

            var producto = await db.Productos.IgnoreQueryFilters()
                                  .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (producto is null) return Results.NotFound();

            var codigo = p.Codigo.Trim();

            if (!string.Equals(codigo, producto.Codigo, StringComparison.OrdinalIgnoreCase))
            {
                var repetido = await db.Productos
                    .AnyAsync(x => x.Id != id && x.Codigo.ToLower() == codigo.ToLower(), ct);

                if (repetido)
                    return Results.Conflict(new { error = $"Ya tienes otro producto con el código {codigo}." });
            }

            // CAMBIAR EL PRECIO NO TOCA LO YA FACTURADO. Cada comprobante lleva
            // copiado el precio del día que se emitió, que es lo correcto: una
            // factura no cambia porque la lista de precios suba.
            producto.Codigo         = codigo;
            producto.Descripcion    = p.Descripcion.Trim();
            producto.UnidadMedida   = p.UnidadMedida.Trim().ToUpperInvariant();
            producto.TipoAfectacion = p.TipoAfectacion.Trim();
            producto.PrecioUnitario = p.PrecioUnitario;
            producto.IncluyeIgv     = p.IncluyeIgv;
            producto.CodigoSunat    = string.IsNullOrWhiteSpace(p.CodigoSunat) ? null : p.CodigoSunat.Trim();

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Producto actualizado." });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Editar un producto");


        // =====================================================================
        grupo.MapPost("/{id:guid}/desactivar", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var producto = await db.Productos.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (producto is null) return Results.NotFound();

            producto.Activo = false;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { mensaje = "Producto desactivado. Los comprobantes ya emitidos no se tocan." });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Desactivar un producto");


        grupo.MapPost("/{id:guid}/reactivar", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var producto = await db.Productos.IgnoreQueryFilters()
                                  .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (producto is null) return Results.NotFound();

            var ocupado = await db.Productos
                .AnyAsync(x => x.Id != id && x.Codigo.ToLower() == producto.Codigo.ToLower(), ct);

            if (ocupado)
                return Results.Conflict(new
                {
                    error = $"Mientras estaba desactivado se dio de alta otro producto con el código " +
                            $"{producto.Codigo}. Desactiva ese primero, o cambia el código de este."
                });

            producto.Activo = true;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { mensaje = "Producto reactivado." });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Reactivar un producto");
    }


    // =========================================================================

    internal static string? Revisar(PeticionProducto p)   // 22 oct: también la importación
    {
        if (string.IsNullOrWhiteSpace(p.Codigo))
            return "Falta el código. Es con lo que lo vas a buscar al facturar.";

        if (p.Codigo.Trim().Length > 30)
            return "El código es demasiado largo.";

        if (string.IsNullOrWhiteSpace(p.Descripcion))
            return "Falta la descripción. Es lo que sale impreso en la factura.";

        if (string.IsNullOrWhiteSpace(p.UnidadMedida))
            return "Falta la unidad de medida.";

        if (!Afectaciones.ContainsKey((p.TipoAfectacion ?? "").Trim()))
            return "Ese tipo de afectación no está en la lista.";

        if (p.PrecioUnitario < 0)
            return "El precio no puede ser negativo.";

        // ═════════════════════════════════════════════════════════════════════
        // EL CÓDIGO DE SUNAT NO SE INVENTA.
        //
        // Es el catálogo 25, que es la lista UNSPSC de Naciones Unidas que SUNAT
        // adoptó: ocho dígitos de una tabla oficial. No es el código de la
        // empresa —ese es `Codigo`, el de arriba, y ahí cada uno pone lo que
        // quiera—. Este dice QUÉ ES la cosa según SUNAT, y por eso solo vale si
        // sale de su lista.
        //
        // SE COMPRUEBA QUE ESTÉ EN LA LISTA (14/10/2026). Antes solo se miraba
        // que fueran ocho dígitos; ahora la plataforma lleva el catálogo
        // entero (CatalogoSunat, UNSPSC v14) y un buscador, así que un código
        // que no existe se ataja aquí y no en SUNAT. Si el catálogo no se
        // pudiera cargar, se vuelve a lo de antes: solo la forma.
        //
        // OJO CON LA FECHA: desde el 1 de enero de 2027 un código que no está
        // en la lista hace RECHAZAR el comprobante (regla 3496; hasta ahora
        // era una observación). Y en la liquidación de compra pasa a ser
        // obligatorio. En factura y boleta sigue siendo opcional.
        // ═════════════════════════════════════════════════════════════════════
        if (p.CodigoSunat is { } cs && !string.IsNullOrWhiteSpace(cs))
        {
            var limpio = cs.Trim();

            if (limpio.Length != 8 || !limpio.All(char.IsDigit))
                return "El código de SUNAT son 8 dígitos del catálogo 25. No es tu código: " +
                       "ese va arriba. Si no lo tienes, déjalo vacío.";

            if (!CatalogoSunat.Existe(limpio))
                return $"El código {limpio} no está en la lista de SUNAT (catálogo 25). " +
                       "Búscalo por lo que es el producto, o déjalo vacío.";
        }

        // EL 17 ES EXCLUYENTE Y NO SE COMPRUEBA AQUÍ, y conviene dejarlo
        // escrito: el arroz pilado no puede mezclarse con líneas gravadas en un
        // mismo comprobante (regla 2644 de SUNAT). Pero eso es una regla DEL
        // COMPROBANTE, no del producto: nada impide que una empresa venda arroz
        // y cemento, cada uno en su factura. La comprobación va donde se arma
        // el documento.

        return null;
    }
}