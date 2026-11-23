using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Reportes;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Pos;

/// <summary>
/// e279 (094) — LAS OFERTAS DEL PUNTO DE VENTA.
///
///   GET  /api/pos/ofertas                    la lista con lo vendido (?formato=xlsx: Excel)
///   GET  /api/pos/ofertas/activas            las que valen hoy en la sucursal de mi caja (para vender)
///   GET  /api/pos/ofertas/productos          los productos para armar una oferta (precio con IGV y costo)
///   GET  /api/pos/ofertas/lotes?productoId   los lotes con stock de un producto (para «liquidar lote»)
///   POST /api/pos/ofertas                    crear
///   PUT  /api/pos/ofertas/{id}               cambiar
///   POST /api/pos/ofertas/{id}/estado        { estado: activa | pausada | terminada }
///
/// Ver y vender: pos.vender. Crear y cambiar: pos.ofertas. Nada se borra.
/// El precio de la oferta va CON IGV. Al cobrar la aplica MotorOfertas.
/// </summary>
public static class EndpointsOfertas
{
    private const string Vender = "pos.vender";
    private const string Gestionar = "pos.ofertas";

    public sealed record ItemPeticion(Guid ProductoId, decimal Cantidad);
    public sealed record PeticionOferta(
        string? Nombre, string? Tipo, decimal Precio, string? Lote, DateOnly? Desde, DateOnly? Hasta,
        bool TodasSucursales, List<Guid>? Sucursales, decimal? Limite, List<ItemPeticion>? Productos);
    public sealed record PeticionEstado(string? Estado);

    private static readonly string[] Tipos = ["cantidad", "pack", "minimo", "lote"];

    public static void MapearOfertas(this IEndpointRouteBuilder rutas)
    {
        var g = rutas.MapGroup("/api/pos/ofertas").WithTags("Punto de venta");

        g.MapGet("", async (string? formato, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var json = await db.Database.SqlQuery<string>($"SELECT ofertas_lista()::text AS \"Value\"").FirstAsync(ct);
            if (!string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase))
                return Results.Content(json.Insert(1, $"\"puedeCambiar\":{(ctx.Tiene(Gestionar) ? "true" : "false")},"), "application/json");

            var hoja = new HojaExcel("Ofertas") { Horizontal = true, FilaTotal = true };
            hoja.Titulos.Add("Ofertas del punto de venta");
            hoja.Columnas.AddRange([new("Oferta", TipoColumna.Texto, 30), new("Tipo", TipoColumna.Texto, 20),
                new("Productos", TipoColumna.Texto, 44), new("Precio normal", TipoColumna.Monto, 13), new("Precio oferta", TipoColumna.Monto, 13),
                new("Desde", TipoColumna.Texto, 12), new("Hasta", TipoColumna.Texto, 12), new("Sucursales", TipoColumna.Texto, 24),
                new("Estado", TipoColumna.Texto, 12), new("Vendidas", TipoColumna.Cantidad, 10), new("Cobrado", TipoColumna.Monto, 12),
                new("Descuento dado", TipoColumna.Monto, 13)]);
            foreach (var f in JsonNode.Parse(json)!["filas"]!.AsArray().OfType<JsonNode>())
            {
                var prods = f["productos"]!.AsArray().OfType<JsonNode>().ToList();
                var tipo = T(f["tipo"]);
                var normal = prods.Sum(p => Dec(p["precio"]) * (tipo == "pack" || tipo == "cantidad" ? Dec(p["cantidad"]) : 1));
                hoja.Filas.Add([T(f["nombre"]), NombreTipo(tipo),
                    string.Join(" + ", prods.Select(p => (Dec(p["cantidad"]) != 1 && tipo != "minimo" ? $"{Dec(p["cantidad"]):0.##} " : "") + T(p["descripcion"])))
                        + (tipo == "lote" ? $" · lote {T(f["lote"])}" : tipo == "minimo" ? $" · desde {Dec(prods.FirstOrDefault()?["cantidad"]):0.##}" : ""),
                    new CeldaExcel(normal, TipoColumna.Monto), new CeldaExcel(Dec(f["precio"]), TipoColumna.Monto),
                    Fecha(f["desde"]), f["hasta"] is null ? "sin fin" : Fecha(f["hasta"]),
                    f["todasSucursales"]?.GetValue<bool>() == true ? "Todas" : string.Join(", ", f["nombresSucursales"]!.AsArray().Select(x => T(x))),
                    NombreEstado(T(f["estadoLista"])), new CeldaExcel(Dec(f["vendidas"]), TipoColumna.Cantidad),
                    new CeldaExcel(Dec(f["cobrado"]), TipoColumna.Monto), new CeldaExcel(Dec(f["descuento"]), TipoColumna.Monto)]);
            }
            return Results.File(ExcelSimple.Crear([hoja]), ExcelSimple.TipoMime, $"ofertas-{DateTime.UtcNow.AddHours(-5):yyyy-MM-dd}.xlsx");
        })
        .RequierePermiso(Vender)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(EndpointsPos.ModuloPos)
        .WithSummary("Las ofertas, con lo vendido este mes y los lotes por vencer sin oferta");

        g.MapGet("/activas", async (Guid? sucursalId, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (sucursalId is not { } suc) return Mal("Falta la sucursal.");
            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
            if (permitidas is not null && !permitidas.Contains(suc)) return Mal("No trabajas en esa sucursal.");
            var json = await db.Database.SqlQuery<string>($"SELECT ofertas_activas({suc})::text AS \"Value\"").FirstAsync(ct);
            return Results.Content(json, "application/json");
        })
        .RequierePermiso(Vender)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(EndpointsPos.ModuloPos)
        .WithSummary("Las ofertas que valen hoy en una sucursal, en el orden en que se aplican");

        g.MapGet("/productos", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            // El costo, sin IGV: el promedio de las compras (inventario) o el escrito en el producto.
            var json = await db.Database.SqlQuery<string>($"""
                SELECT coalesce(json_agg(json_build_object(
                           'id', p.id, 'codigo', p.codigo, 'descripcion', p.descripcion, 'unidadMedida', p.unidad_medida,
                           'precio', producto_precio_venta(p), 'tipoAfectacion', p.tipo_afectacion, 'manejaLotes', p.maneja_lotes,
                           'costo', coalesce((SELECT round(sum(m.cantidad * m.costo_unitario) / nullif(sum(m.cantidad), 0), 4)
                                                FROM inv_movimientos m
                                               WHERE m.producto_id = p.id AND m.tipo IN ('compra', 'stock_inicial')
                                                 AND m.costo_unitario IS NOT NULL AND coalesce(m.moneda, 'PEN') = 'PEN'), p.costo))
                         ORDER BY p.descripcion), '[]'::json)::text AS "Value"
                  FROM productos p
                 WHERE p.activo AND p.unidad_medida <> 'ZZ' AND p.tipo_afectacion IN ('10', '20', '30')
                """).FirstAsync(ct);
            return Results.Content(json, "application/json");
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(EndpointsPos.ModuloPos)
        .WithSummary("Los productos para armar una oferta: precio con IGV y costo sin IGV");

        g.MapGet("/lotes", async (Guid productoId, ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database.SqlQuery<string>($"""
                SELECT coalesce(json_agg(json_build_object('lote', x.lote, 'vence', x.vence, 'quedan', x.quedan)
                                         ORDER BY x.vence NULLS LAST, x.lote), '[]'::json)::text AS "Value"
                  FROM (SELECT l.lote, min(l.vence) AS vence, sum(l.fisico) AS quedan
                          FROM inv_lotes l WHERE l.producto_id = {productoId} AND l.lote <> '' AND l.fisico > 0
                         GROUP BY l.lote) x
                """).FirstAsync(ct);
            return Results.Content(json, "application/json");
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(EndpointsPos.ModuloPos)
        .WithSummary("Los lotes con stock de un producto, el que vence primero arriba");

        g.MapPost("", async (PeticionOferta p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var (ok, error) = await ValidarAsync(p, db, ctx, ct);
            if (error is not null) return Mal(error);
            var id = (await db.Database.SqlQuery<Guid>($"""
                INSERT INTO ofertas (empresa_id, nombre, tipo, precio, lote, desde, hasta, todas_sucursales, sucursales, limite, creado_por)
                VALUES ({ctx.EmpresaId!.Value}, {ok!.Nombre}, {ok.Tipo}, {ok.Precio}, {ok.Lote}, {ok.Desde}, {ok.Hasta},
                        {ok.Todas}, {ok.Sucursales}, {ok.Limite}, {ctx.UsuarioId})
                RETURNING id AS "Value"
                """).ToListAsync(ct)).Single();
            await GuardarProductosAsync(db, ctx, id, ok.Productos, ct);
            return Results.Ok(new { id, mensaje = $"Oferta «{ok.Nombre}» guardada. Se aplica sola en el punto de venta {Vigencia(ok.Desde, ok.Hasta)}." });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(EndpointsPos.ModuloPos)
        .WithSummary("Crear una oferta");

        g.MapPut("/{id:guid}", async (Guid id, PeticionOferta p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var estado = await db.Database.SqlQuery<string?>($"SELECT estado AS \"Value\" FROM ofertas WHERE id = {id}").FirstOrDefaultAsync(ct);
            if (estado is null) return Results.NotFound(new { error = "Esa oferta no existe." });
            if (estado == "terminada") return Mal("Una oferta terminada no se cambia. Crea una nueva.");
            var (ok, error) = await ValidarAsync(p, db, ctx, ct);
            if (error is not null) return Mal(error);
            // Ya se vendió con ella: el precio y los productos quedan como se vendieron.
            var antes = await db.Database.SqlQuery<string>($"""
                SELECT o.tipo || '|' || o.precio::text || '|' || coalesce(o.lote, '') || '|' ||
                       (SELECT string_agg(op.producto_id::text || 'x' || round(op.cantidad, 4)::text, ',' ORDER BY op.producto_id)
                          FROM oferta_productos op WHERE op.oferta_id = o.id) AS "Value"
                  FROM ofertas o WHERE o.id = {id} AND EXISTS (SELECT 1 FROM oferta_ventas v WHERE v.oferta_id = o.id)
                """).FirstOrDefaultAsync(ct);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var ahora = $"{ok!.Tipo}|{ok.Precio.ToString("0.00", inv)}|{ok.Lote ?? ""}|" + string.Join(",", ok.Productos
                .OrderBy(x => x.ProductoId.ToString(), StringComparer.Ordinal)
                .Select(x => $"{x.ProductoId}x{Math.Round(x.Cantidad, 4).ToString("0.0000", inv)}"));
            if (antes is not null && antes != ahora)
                return Mal("Ya se vendió con esta oferta: el precio y los productos no se cambian. Termínala y crea una nueva. (El nombre, las fechas, las sucursales y el límite sí se pueden cambiar.)");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ofertas SET nombre = {ok!.Nombre}, tipo = {ok.Tipo}, precio = {ok.Precio}, lote = {ok.Lote},
                       desde = {ok.Desde}, hasta = {ok.Hasta}, todas_sucursales = {ok.Todas}, sucursales = {ok.Sucursales},
                       limite = {ok.Limite}, modificado_en = now(), modificado_por = {ctx.UsuarioId}
                 WHERE id = {id}
                """, ct);
            if (antes is null) await GuardarProductosAsync(db, ctx, id, ok.Productos, ct);
            return Results.Ok(new { mensaje = $"Oferta «{ok.Nombre}» cambiada. Las ventas que ya se hicieron siguen igual." });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(EndpointsPos.ModuloPos)
        .WithSummary("Cambiar una oferta (las ventas hechas no cambian)");

        g.MapPost("/{id:guid}/estado", async (Guid id, PeticionEstado p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var nuevo = (p.Estado ?? "").Trim().ToLowerInvariant();
            if (nuevo is not ("activa" or "pausada" or "terminada")) return Mal("El estado es activa, pausada o terminada.");
            var o = await db.Database.SqlQuery<string?>($"SELECT estado || '|' || nombre AS \"Value\" FROM ofertas WHERE id = {id}").FirstOrDefaultAsync(ct);
            if (o is null) return Results.NotFound(new { error = "Esa oferta no existe." });
            var (antes, nombre) = (o.Split('|', 2)[0], o.Split('|', 2)[1]);
            if (antes == "terminada") return Mal("Una oferta terminada ya no se vuelve a usar. Crea una nueva.");
            if (antes == nuevo) return Results.Ok(new { mensaje = "Ya estaba así." });
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ofertas SET estado = {nuevo}, modificado_en = now(), modificado_por = {ctx.UsuarioId} WHERE id = {id}
                """, ct);
            return Results.Ok(new
            {
                mensaje = nuevo switch
                {
                    "pausada" => $"«{nombre}» en pausa: no se aplica hasta que la reanudes.",
                    "terminada" => $"«{nombre}» terminada. Lo vendido con ella se guarda.",
                    _ => $"«{nombre}» reanudada: se vuelve a aplicar en el punto de venta.",
                }
            });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(EndpointsPos.ModuloPos)
        .WithSummary("Pausar, reanudar o terminar una oferta");
    }

    private sealed record Validada(string Nombre, string Tipo, decimal Precio, string? Lote, DateOnly Desde, DateOnly? Hasta,
                                   bool Todas, Guid[] Sucursales, decimal? Limite, List<ItemPeticion> Productos);

    private static async Task<(Validada? Ok, string? Error)> ValidarAsync(PeticionOferta p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        var nombre = System.Text.RegularExpressions.Regex.Replace((p.Nombre ?? "").Trim(), @"\s+", " ");
        if (nombre.Length is < 2 or > 60) return (null, "Escribe el nombre de la oferta (de 2 a 60 letras). Sale en el ticket.");
        var tipo = (p.Tipo ?? "").Trim().ToLowerInvariant();
        if (!Tipos.Contains(tipo)) return (null, "Elige el tipo de oferta.");
        var precio = Math.Round(p.Precio, 2, MidpointRounding.AwayFromZero);
        if (precio <= 0 || precio > 1_000_000) return (null, "Escribe el precio de la oferta (mayor que cero).");
        if (p.Desde is not { } desde) return (null, "Elige desde qué día vale.");
        if (p.Hasta is { } h && h < desde) return (null, "La fecha «hasta» es antes de «desde».");
        if (p.Limite is { } lim && lim <= 0) return (null, "El límite tiene que ser mayor que cero (o déjalo vacío).");

        var items = (p.Productos ?? []).Where(x => x.Cantidad > 0).GroupBy(x => x.ProductoId)
            .Select(gr => new ItemPeticion(gr.Key, Math.Round(gr.Sum(x => x.Cantidad), 4))).ToList();
        if (items.Count == 0) return (null, "Elige el producto de la oferta.");
        if (tipo != "pack" && items.Count > 1) return (null, "Esta oferta es de un solo producto. Para varios, elige «Pack o combo».");
        if (tipo == "pack" && items.Count < 2) return (null, "Un pack lleva al menos dos productos distintos.");
        if (items.Count > 20) return (null, "Un pack lleva hasta 20 productos.");
        if ((tipo is "pack" or "cantidad") && items.Any(x => x.Cantidad != Math.Floor(x.Cantidad)))
            return (null, "En un pack o en «lleva varios» las cantidades son enteras (2, 3…).");
        if (tipo == "cantidad" && items[0].Cantidad < 2) return (null, "«Lleva varios» es desde 2 unidades.");
        if (tipo == "lote") items = [items[0] with { Cantidad = 1 }];
        if ((tipo is "pack" or "cantidad") && p.Limite is { } l2 && l2 != Math.Floor(l2)) return (null, "El límite de packs es un número entero.");

        var ids = items.Select(x => x.ProductoId).ToArray();
        var precios = await db.Database.SqlQuery<PrecioProducto>($"""
            SELECT p.id AS "Id", producto_precio_venta(p) AS "Precio", p.maneja_lotes AS "Lotes", p.descripcion AS "Descripcion"
              FROM productos p WHERE p.id = ANY({ids}) AND p.activo
            """).ToListAsync(ct);
        if (precios.Count != ids.Length) return (null, "Un producto de la oferta no existe o está desactivado.");

        string? lote = null;
        if (tipo == "lote")
        {
            lote = Plataforma.Api.Inventario.EndpointsInventarioOperaciones.LoteLimpio(p.Lote);
            if (lote is null) return (null, "Elige el lote que quieres liquidar.");
            if (!precios[0].Lotes) return (null, $"«{precios[0].Descripcion}» no maneja lotes.");
        }

        // El precio de la oferta tiene que ser menor que el normal.
        var normal = tipo switch
        {
            "pack" => items.Sum(x => x.Cantidad * precios.First(pp => pp.Id == x.ProductoId).Precio),
            "cantidad" => items[0].Cantidad * precios[0].Precio,
            _ => precios[0].Precio,
        };
        if (precio >= normal)
            return (null, $"El precio de la oferta (S/ {precio:0.00}) tiene que ser menor que el normal (S/ {normal:0.00}).");

        var sucursales = p.TodasSucursales ? [] : (p.Sucursales ?? []).Distinct().ToArray();
        if (!p.TodasSucursales)
        {
            if (sucursales.Length == 0) return (null, "Elige al menos una sucursal (o «Todas»).");
            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
            if (permitidas is not null && sucursales.Any(s => !permitidas.Contains(s))) return (null, "Elegiste una sucursal en la que no trabajas.");
            var n = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM sucursales WHERE id = ANY({sucursales}) AND activo").FirstAsync(ct);
            if (n != sucursales.Length) return (null, "Una sucursal elegida no existe o está desactivada.");
        }

        return (new Validada(nombre, tipo, precio, lote, desde, p.Hasta, p.TodasSucursales, sucursales,
                             p.Limite is { } li ? Math.Round(li, 4) : null, items), null);
    }

    private sealed record PrecioProducto(Guid Id, decimal Precio, bool Lotes, string Descripcion);

    private static async Task GuardarProductosAsync(ContextoPlataforma db, ContextoPeticion ctx, Guid oferta, List<ItemPeticion> items, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM oferta_productos WHERE oferta_id = {oferta}", ct);
        short orden = 1;
        foreach (var it in items)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO oferta_productos (oferta_id, empresa_id, producto_id, cantidad, orden)
                VALUES ({oferta}, {ctx.EmpresaId!.Value}, {it.ProductoId}, {it.Cantidad}, {orden})
                """, ct);
            orden++;
        }
    }

    /// <summary>Después de cobrar: lo que hizo cada oferta en esa venta.</summary>
    public static async Task AnotarAsync(ContextoPlataforma db, ContextoPeticion ctx, Guid documento, IEnumerable<MotorOfertas.Uso> usos, CancellationToken ct)
    {
        foreach (var u in usos)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO oferta_ventas (empresa_id, oferta_id, documento_id, veces, cobrado, descuento)
                VALUES ({ctx.EmpresaId!.Value}, {u.OfertaId}, {documento}, {u.Veces}, {u.Cobrado}, {u.Descuento})
                """, ct);
    }

    private static string Vigencia(DateOnly desde, DateOnly? hasta)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        var d = desde <= hoy ? "desde hoy" : $"desde el {desde:dd/MM/yyyy}";
        return hasta is { } h ? $"{d} hasta el {h:dd/MM/yyyy}" : d;
    }

    private static IResult Mal(string error) => Results.BadRequest(new { error });
    private static string T(JsonNode? n) => n?.ToString() ?? "";
    private static decimal Dec(JsonNode? n) => n is null ? 0m : n.GetValue<decimal>();
    private static string Fecha(JsonNode? n) => DateOnly.TryParse(T(n), out var d) ? d.ToString("dd/MM/yyyy") : T(n);
    private static string NombreTipo(string t) => t switch
    {
        "cantidad" => "Lleva varios, paga menos", "pack" => "Pack o combo", "minimo" => "Por cantidad", "lote" => "Liquidar lote", _ => t,
    };
    private static string NombreEstado(string e) => e switch
    {
        "activa" => "Activa", "programada" => "Programada", "pausada" => "En pausa", "terminada" => "Terminada", _ => e,
    };
}