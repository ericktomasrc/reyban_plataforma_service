using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Reportes;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Inventario;

public sealed record PeticionConfigInventario(bool Encendido, string? Modo, bool PermitirSinStock, bool AvisarMinimo);
public sealed record PeticionControlar(List<Guid>? SucursalIds, bool Controlar = true);
public sealed record MinimoSucursal(Guid SucursalId, decimal? Minimo);

/// <summary>
/// 052 — INVENTARIO POR SUCURSAL (diseño aprobado el 24 oct 2026).
///
///   GET  /api/inventario/estado              encendido, modo, sucursales (stock inicial, controla) y conteos
///   PUT  /api/inventario/configuracion       encender / modo / permitir vender sin stock / avisos
///   POST /api/inventario/controlar           «Empezar a controlar» (por sucursal) o dejar de controlar
///   POST /api/inventario/marcar-bienes       «Controla stock» en todos los bienes
///   GET  /api/inventario/stock               el stock por sucursal (paginado, con filtros); /excel
///   GET  /api/inventario/disponible          para «Nueva factura»: lo disponible de unos productos
///   GET  /api/inventario/kardex/{producto}   el kardex (paginado, con filtros); /excel
///   GET|PUT /api/inventario/minimos/{producto}
///   GET  /api/inventario/alertas             bajo el mínimo / en negativo, por sucursal
///
/// Las operaciones (ingresos, ajustes, stock inicial, conteos), los traslados
/// y las maestras están en sus propios archivos.
/// </summary>
public static class EndpointsInventario
{
    public sealed class FilaSaldo
    {
        public Guid ProductoId { get; set; }
        public Guid SucursalId { get; set; }
        public decimal Fisico { get; set; }
        public decimal Reservado { get; set; }
        public decimal? Minimo { get; set; }
    }

    public sealed class FilaTransito
    {
        public Guid ProductoId { get; set; }
        public Guid DestinoId { get; set; }
        public decimal Cantidad { get; set; }
    }

    public sealed class FilaKardex
    {
        public Guid Id { get; set; }
        public long Orden { get; set; }
        public DateTime CreadoEn { get; set; }
        public Guid SucursalId { get; set; }
        public string SucursalCodigo { get; set; } = "";
        public string Tipo { get; set; } = "";
        public decimal Cantidad { get; set; }
        public decimal Reservado { get; set; }
        public decimal SaldoFisico { get; set; }
        public decimal SaldoReservado { get; set; }
        public string Fuente { get; set; } = "";
        public Guid FuenteId { get; set; }
        public Guid? DocumentoId { get; set; }
        public string? DocumentoNumero { get; set; }
        public string? ReferenciaNumero { get; set; }
        public Guid? GuiaId { get; set; }
        public string? GuiaNumero { get; set; }
        public Guid? OperacionId { get; set; }
        public string? OperacionCodigo { get; set; }
        public string? Proveedor { get; set; }
        public string? DocumentoProveedor { get; set; }
        public string? Motivo { get; set; }
        public string? Nota { get; set; }
        public string? Usuario { get; set; }
        public decimal? CostoUnitario { get; set; }
    }

    public sealed class FilaConteoSucursal
    {
        public Guid SucursalId { get; set; }
        public int Cargados { get; set; }
    }

    public sealed class FilaAlerta
    {
        public Guid SucursalId { get; set; }
        public int BajoMinimo { get; set; }
        public int Negativos { get; set; }
    }

    public sealed record SucursalVisible(Guid Id, string Codigo, string Nombre);

    /// <summary>Los grupos del filtro «Movimiento» del kardex.</summary>
    private static readonly Dictionary<string, string[]> Grupos = new()
    {
        ["ventas"] = ["venta", "venta_revertida", "reserva", "reserva_liberada", "reserva_revertida",
                      "despacho", "despacho_revertido", "guia_salida", "guia_revertida"],
        ["compras"] = ["compra", "compra_anulada"],
        ["traslados"] = ["traslado_salida", "traslado_revertido", "traslado_entrada", "traslado_faltante", "traslado_retorno"],
        ["ajustes"] = ["ajuste", "ajuste_anulado", "conteo", "stock_inicial"],
        ["devoluciones"] = ["devolucion", "devolucion_revertida"],
    };

    public static void MapearInventario(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/inventario").WithTags("Inventario");

        // =====================================================================
        // ESTADO Y CONFIGURACIÓN
        // =====================================================================
        grupo.MapGet("/estado", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            await Sucursales.PrincipalAsync(db, empresaId, ct);

            var cfg = await ReglasInventario.ConfigAsync(db, empresaId, ct);
            var sucursales = await db.Sucursales.AsNoTracking()
                .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null)
                .OrderBy(s => s.Codigo)
                .Select(s => new { s.Id, s.Codigo, s.Nombre })
                .ToListAsync(ct);
            var control = await db.InvSucursales.AsNoTracking().Where(x => x.EmpresaId == empresaId).ToListAsync(ct);
            var cargados = await db.Database.SqlQuery<FilaConteoSucursal>($"""
                SELECT sucursal_id, count(DISTINCT producto_id)::int AS cargados
                  FROM inv_movimientos
                 WHERE empresa_id = {empresaId} AND tipo = 'stock_inicial'
                 GROUP BY sucursal_id
                """).ToListAsync(ct);

            var bienes = await db.Productos.AsNoTracking().Where(p => p.UnidadMedida != "ZZ").CountAsync(ct);
            var controlados = await db.Productos.AsNoTracking().Where(p => p.UnidadMedida != "ZZ" && p.ControlaStock).CountAsync(ct);
            var servicios = await db.Productos.AsNoTracking().Where(p => p.UnidadMedida == "ZZ").CountAsync(ct);

            return Results.Ok(new
            {
                encendido = cfg?.Encendido ?? false,
                modo = cfg?.Modo ?? ModoInventario.Directa,
                permitirSinStock = cfg?.PermitirSinStock ?? false,
                avisarMinimo = cfg?.AvisarMinimo ?? true,
                activadoEn = cfg?.ActivadoEn,
                productos = new { bienes, controlados, sinControl = bienes - controlados, servicios },
                sucursales = sucursales.Select(s =>
                {
                    var c = control.FirstOrDefault(x => x.SucursalId == s.Id);
                    return new
                    {
                        s.Id, s.Codigo, s.Nombre,
                        stockInicialEn = c?.StockInicialEn,
                        controlaDesde = c?.ControlaDesde,
                        conStockInicial = cargados.FirstOrDefault(x => x.SucursalId == s.Id)?.Cargados ?? 0
                    };
                }),
                motivosAjuste = MotivoAjuste.Todos.Select(m => new { codigo = m.Codigo, nombre = m.Nombre, signo = m.Signo })
            });
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Cómo está el inventario de mi empresa");


        grupo.MapPut("/configuracion", async (
            PeticionConfigInventario p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var modo = p.Modo is ModoInventario.Despacho ? ModoInventario.Despacho : ModoInventario.Directa;

            var cfg = await db.InventarioConfig.FirstOrDefaultAsync(x => x.EmpresaId == empresaId, ct);
            if (cfg is null)
            {
                cfg = new InventarioConfig { EmpresaId = empresaId };
                db.InventarioConfig.Add(cfg);
            }
            if (p.Encendido && !cfg.Encendido) cfg.ActivadoEn = DateTime.UtcNow;
            cfg.Encendido = p.Encendido;
            cfg.Modo = modo;
            cfg.PermitirSinStock = p.PermitirSinStock;
            cfg.AvisarMinimo = p.AvisarMinimo;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                mensaje = p.Encendido
                    ? "Listo. Carga el stock inicial de cada sucursal y después elige «Empezar a controlar». Hasta entonces nada se bloquea."
                    : "Inventario apagado: la facturación sigue como siempre. Lo anotado hasta hoy se guarda."
            });
        })
        .RequierePermiso(Permisos.Inventario.Configurar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Encender el inventario y elegir su modo");


        grupo.MapPost("/controlar", async (
            PeticionControlar p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var cfg = await ReglasInventario.ConfigAsync(db, empresaId, ct);
            if (cfg is not { Encendido: true })
                return Results.Conflict(new { error = "Primero enciende el inventario." });

            var ids = (p.SucursalIds ?? []).Distinct().ToList();
            var validas = await db.Sucursales.AsNoTracking()
                .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null && ids.Contains(s.Id))
                .Select(s => s.Id).ToListAsync(ct);
            if (validas.Count == 0) return Results.BadRequest(new { error = "Elige al menos una sucursal." });

            var filas = await db.InvSucursales.Where(x => x.EmpresaId == empresaId && validas.Contains(x.SucursalId)).ToListAsync(ct);
            foreach (var id in validas)
            {
                var f = filas.FirstOrDefault(x => x.SucursalId == id);
                if (f is null) { f = new InvSucursal { EmpresaId = empresaId, SucursalId = id }; db.InvSucursales.Add(f); }
                f.ControlaDesde = p.Controlar ? (f.ControlaDesde ?? DateTime.UtcNow) : null;
            }
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                mensaje = p.Controlar
                    ? (cfg.PermitirSinStock
                        ? "Listo: se controla desde ahora. Como «Permitir vender sin stock» está encendido, lo que no hay queda en negativo para revisarlo."
                        : "Listo: desde ahora no se emite lo que no hay en esa sucursal.")
                    : "Listo: esa sucursal vuelve a solo avisar."
            });
        })
        .RequierePermiso(Permisos.Inventario.Configurar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Empezar (o dejar) de controlar el stock de una o varias sucursales");


        grupo.MapPost("/marcar-bienes", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var productos = await db.Productos.Where(p => p.UnidadMedida != "ZZ" && !p.ControlaStock).ToListAsync(ct);
            foreach (var x in productos) x.ControlaStock = true;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                marcados = productos.Count,
                mensaje = productos.Count == 0 ? "Todos tus bienes ya controlan stock." : $"Listo: {productos.Count} producto(s) ahora controlan stock."
            });
        })
        .RequierePermiso(Permisos.Inventario.Configurar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Marcar «Controla stock» en todos los bienes (no en servicios)");


        // =====================================================================
        // EL STOCK
        // =====================================================================
        grupo.MapGet("/stock", async (
            string? q, Guid? sucursalId, Guid? categoriaId, string? mostrar, int? pagina, int? porPagina,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var (sucursales, filas) = await StockAsync(db, ctx, empresaId, q, sucursalId, categoriaId, mostrar, ct);

            var pp = Listados.PorPagina(porPagina);
            var pg = Listados.Pagina(pagina);
            return Results.Ok(new
            {
                total = filas.Count, pagina = pg, porPagina = pp,
                sucursales,
                filas = filas.Skip((pg - 1) * pp).Take(pp)
            });
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("El stock por sucursal (físico, reservado, disponible, en tránsito)");


        grupo.MapGet("/stock/excel", async (
            string? q, Guid? sucursalId, Guid? categoriaId, string? mostrar,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var (sucursales, filas) = await StockAsync(db, ctx, empresaId, q, sucursalId, categoriaId, mostrar, ct);

            var hoja = new HojaExcel("Stock") { Horizontal = true };
            hoja.Titulos.Add("Stock por sucursal");
            hoja.Titulos.Add($"Al {DateTime.UtcNow.AddHours(-5):dd/MM/yyyy HH:mm}. Disponible = físico − reservado.");
            hoja.Columnas.Add(new("Código", TipoColumna.Texto, 14));
            hoja.Columnas.Add(new("Producto", TipoColumna.Texto, 40));
            hoja.Columnas.Add(new("Unidad", TipoColumna.Texto, 8));
            hoja.Columnas.Add(new("Categoría", TipoColumna.Texto, 16));
            foreach (var s in sucursales)
            {
                hoja.Columnas.Add(new($"{s.Codigo} físico", TipoColumna.Cantidad, 12));
                hoja.Columnas.Add(new($"{s.Codigo} reservado", TipoColumna.Cantidad, 12));
                hoja.Columnas.Add(new($"{s.Codigo} disponible", TipoColumna.Cantidad, 12));
                hoja.Columnas.Add(new($"{s.Codigo} mínimo", TipoColumna.Cantidad, 12));
            }
            hoja.Columnas.Add(new("En tránsito", TipoColumna.Cantidad, 12));
            hoja.Columnas.Add(new("Total disponible", TipoColumna.Cantidad, 14));

            foreach (var f in filas)
            {
                var celdas = new List<object?> { f.Codigo, f.Descripcion, f.Unidad, f.Categoria };
                foreach (var s in sucursales)
                {
                    var x = f.Sucursales.First(y => y.SucursalId == s.Id);
                    celdas.AddRange([x.Fisico, x.Reservado, x.Disponible, x.Minimo]);
                }
                celdas.Add(f.Transito);
                celdas.Add(f.TotalDisponible);
                hoja.Filas.Add([.. celdas]);
            }
            return Results.File(ExcelSimple.Crear([hoja]), ExcelSimple.TipoMime, $"stock-{DateTime.UtcNow.AddHours(-5):yyyy-MM-dd}.xlsx");
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("El stock por sucursal, en Excel");


        // =====================================================================
        // PARA «NUEVA FACTURA»: lo disponible de unos productos en la sucursal
        // de la serie (o la elegida), y en las demás.
        // =====================================================================
        grupo.MapGet("/disponible", async (
            string? productos, Guid? sucursalId, string? tipo, string? serie,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var cfg = await ReglasInventario.ConfigAsync(db, empresaId, ct);
            if (cfg is not { Encendido: true }) return Results.Ok(new { encendido = false });

            var principal = await Sucursales.PrincipalAsync(db, empresaId, ct);
            var suc = sucursalId;
            if (suc is null && !string.IsNullOrWhiteSpace(tipo) && !string.IsNullOrWhiteSpace(serie))
            {
                var mapa = await Sucursales.MapaAsync(db, empresaId, ct);
                suc = mapa.TryGetValue(Sucursales.Clave(tipo, serie), out var m) ? m : principal.Id;
            }
            suc ??= ctx.SucursalVista ?? principal.Id;

            var ids = (productos ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().Take(200).ToList();
            var controlados = await db.Productos.IgnoreQueryFilters().AsNoTracking()
                .Where(p => ids.Contains(p.Id) && p.ControlaStock).Select(p => p.Id).ToListAsync(ct);
            var saldos = await ReglasInventario.SaldosAsync(db, empresaId, controlados, ct);
            var sucursales = await db.Sucursales.AsNoTracking()
                .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null)
                .Select(s => new { s.Id, s.Codigo, s.Nombre }).ToListAsync(ct);

            return Results.Ok(new
            {
                encendido = true,
                cfg.Modo,
                cfg.PermitirSinStock,
                sucursalId = suc,
                controla = await db.InvSucursales.AsNoTracking().AnyAsync(x => x.SucursalId == suc && x.ControlaDesde != null, ct),
                bloquea = await ReglasInventario.BloqueaAsync(db, empresaId, suc.Value, ct),
                productos = ids.Select(id =>
                {
                    var aqui = saldos.FirstOrDefault(s => s.ProductoId == id && s.SucursalId == suc);
                    return new
                    {
                        productoId = id,
                        controla = controlados.Contains(id),
                        disponible = aqui is null ? 0 : aqui.Fisico - aqui.Reservado,
                        fisico = aqui?.Fisico ?? 0,
                        reservado = aqui?.Reservado ?? 0,
                        otras = saldos.Where(s => s.ProductoId == id && s.SucursalId != suc && s.Fisico - s.Reservado > 0)
                            .Select(s => sucursales.FirstOrDefault(x => x.Id == s.SucursalId) is { } x
                                ? new { sucursalId = x.Id, x.Codigo, x.Nombre, disponible = s.Fisico - s.Reservado } : null)
                            .Where(x => x is not null)
                    };
                })
            });
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Lo disponible de unos productos (para emitir)");


        // =====================================================================
        // EL KARDEX
        // =====================================================================
        grupo.MapGet("/kardex/{productoId:guid}", async (
            Guid productoId, Guid? sucursalId, DateOnly? desde, DateOnly? hasta, string? movimiento,
            int? pagina, int? porPagina, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var producto = await db.Productos.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Id == productoId)
                .Select(p => new { p.Id, p.Codigo, p.Descripcion, p.UnidadMedida, p.ControlaStock })
                .FirstOrDefaultAsync(ct);
            if (producto is null) return Results.NotFound(new { error = "No se encontró el producto." });

            var visibles = await VisiblesAsync(db, ctx, empresaId, ct);
            var sucs = visibles.Select(s => s.Id).Where(id => sucursalId is null || id == sucursalId).ToArray();
            var pp = Listados.PorPagina(porPagina);
            var pg = Listados.Pagina(pagina);
            var tipos = movimiento is not null && Grupos.TryGetValue(movimiento, out var t) ? t : null;

            var (total, filas) = await KardexAsync(db, empresaId, productoId, sucs, desde, hasta, tipos, pp, (pg - 1) * pp, ct);
            var saldos = await ReglasInventario.SaldosAsync(db, empresaId, [productoId], ct);
            var mios = saldos.Where(s => sucs.Contains(s.SucursalId)).ToList();

            return Results.Ok(new
            {
                producto,
                sucursales = visibles,
                fisico = mios.Sum(s => s.Fisico),
                reservado = mios.Sum(s => s.Reservado),
                disponible = mios.Sum(s => s.Fisico - s.Reservado),
                total, pagina = pg, porPagina = pp, filas
            });
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("El kardex de un producto");


        grupo.MapGet("/kardex/{productoId:guid}/excel", async (
            Guid productoId, Guid? sucursalId, DateOnly? desde, DateOnly? hasta, string? movimiento,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var producto = await db.Productos.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.Id == productoId, ct);
            if (producto is null) return Results.NotFound(new { error = "No se encontró el producto." });

            var visibles = await VisiblesAsync(db, ctx, empresaId, ct);
            var sucs = visibles.Select(s => s.Id).Where(id => sucursalId is null || id == sucursalId).ToArray();
            var tipos = movimiento is not null && Grupos.TryGetValue(movimiento, out var t) ? t : null;
            var (_, filas) = await KardexAsync(db, empresaId, productoId, sucs, desde, hasta, tipos, 20000, 0, ct);

            var hoja = new HojaExcel("Kardex") { Horizontal = true };
            hoja.Titulos.Add($"Kardex · {producto.Codigo} {producto.Descripcion}");
            hoja.Titulos.Add($"Unidad: {producto.UnidadMedida}. Cantidades; lo más nuevo arriba.");
            hoja.Columnas.Add(new("Fecha", TipoColumna.Texto, 18));
            hoja.Columnas.Add(new("Sucursal", TipoColumna.Texto, 10));
            hoja.Columnas.Add(new("Movimiento", TipoColumna.Texto, 22));
            hoja.Columnas.Add(new("Documento", TipoColumna.Texto, 20));
            hoja.Columnas.Add(new("Detalle", TipoColumna.Texto, 30));
            hoja.Columnas.Add(new("Quién", TipoColumna.Texto, 18));
            hoja.Columnas.Add(new("Entra", TipoColumna.Cantidad, 10));
            hoja.Columnas.Add(new("Sale", TipoColumna.Cantidad, 10));
            hoja.Columnas.Add(new("Reservado (±)", TipoColumna.Cantidad, 12));
            hoja.Columnas.Add(new("Saldo físico", TipoColumna.Cantidad, 12));
            foreach (var f in filas)
                hoja.Filas.Add([
                    f.CreadoEn.AddHours(-5).ToString("dd/MM/yyyy HH:mm"), f.SucursalCodigo, NombreTipo(f.Tipo),
                    f.OperacionCodigo ?? f.DocumentoNumero ?? f.GuiaNumero,
                    string.Join(" · ", new[] { f.Proveedor, f.DocumentoProveedor, f.Motivo, f.Nota, f.ReferenciaNumero }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    f.Usuario ?? "sistema",
                    f.Cantidad > 0 ? f.Cantidad : null, f.Cantidad < 0 ? -f.Cantidad : null,
                    f.Reservado != 0 ? f.Reservado : null, f.SaldoFisico]);
            return Results.File(ExcelSimple.Crear([hoja]), ExcelSimple.TipoMime, $"kardex-{producto.Codigo}.xlsx");
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("El kardex de un producto, en Excel");


        // =====================================================================
        // MÍNIMOS Y ALERTAS
        // =====================================================================
        grupo.MapGet("/minimos/{productoId:guid}", async (
            Guid productoId, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var sucursales = await db.Sucursales.AsNoTracking()
                .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null).OrderBy(s => s.Codigo)
                .Select(s => new { s.Id, s.Codigo, s.Nombre }).ToListAsync(ct);
            var saldos = await db.Database.SqlQuery<FilaSaldo>($"""
                SELECT producto_id, sucursal_id, fisico, reservado, minimo FROM inv_stock
                 WHERE empresa_id = {empresaId} AND producto_id = {productoId}
                """).ToListAsync(ct);
            return Results.Ok(sucursales.Select(s =>
            {
                var x = saldos.FirstOrDefault(y => y.SucursalId == s.Id);
                return new { sucursalId = s.Id, s.Codigo, s.Nombre, minimo = x?.Minimo, fisico = x?.Fisico ?? 0, disponible = (x?.Fisico ?? 0) - (x?.Reservado ?? 0) };
            }));
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("El stock mínimo de un producto en cada sucursal");


        grupo.MapPut("/minimos/{productoId:guid}", async (
            Guid productoId, List<MinimoSucursal> p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            if (!await db.Productos.AnyAsync(x => x.Id == productoId, ct)) return Results.NotFound(new { error = "No se encontró el producto." });
            var validas = await db.Sucursales.AsNoTracking().Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null)
                .Select(s => s.Id).ToListAsync(ct);

            foreach (var m in p ?? [])
            {
                if (!validas.Contains(m.SucursalId)) continue;
                if (m.Minimo is < 0) return Results.BadRequest(new { error = "El mínimo no puede ser negativo." });
                if (m.Minimo is > 999_999_999) return Results.BadRequest(new { error = "El mínimo es demasiado grande." });
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO inv_stock (empresa_id, producto_id, sucursal_id, minimo)
                    VALUES ({empresaId}, {productoId}, {m.SucursalId}, {m.Minimo})
                    ON CONFLICT (producto_id, sucursal_id) DO UPDATE SET minimo = EXCLUDED.minimo
                    """, ct);
            }
            return Results.Ok(new { mensaje = "Mínimos guardados." });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Guardar el stock mínimo de un producto en cada sucursal");


        grupo.MapGet("/alertas", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var cfg = await ReglasInventario.ConfigAsync(db, empresaId, ct);
            if (cfg is not { Encendido: true }) return Results.Ok(Array.Empty<object>());

            var visibles = await VisiblesAsync(db, ctx, empresaId, ct);
            var ids = visibles.Select(v => v.Id).ToArray();
            var filas = await db.Database.SqlQuery<FilaAlerta>($"""
                SELECT s.sucursal_id,
                       count(*) FILTER (WHERE s.minimo IS NOT NULL AND s.fisico - s.reservado < s.minimo)::int AS bajo_minimo,
                       count(*) FILTER (WHERE s.fisico < 0)::int AS negativos
                  FROM inv_stock s
                  JOIN productos p ON p.id = s.producto_id AND p.activo AND p.controla_stock
                 WHERE s.empresa_id = {empresaId} AND s.sucursal_id = ANY ({ids})
                 GROUP BY s.sucursal_id
                """).ToListAsync(ct);
            return Results.Ok(filas
                .Where(f => (cfg.AvisarMinimo && f.BajoMinimo > 0) || f.Negativos > 0)
                .Select(f =>
                {
                    var s = visibles.First(v => v.Id == f.SucursalId);
                    return new { f.SucursalId, s.Codigo, s.Nombre, bajoMinimo = cfg.AvisarMinimo ? f.BajoMinimo : 0, f.Negativos };
                }));
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Productos bajo su mínimo o en negativo, por sucursal");
    }


    // =========================================================================

    /// <summary>Las sucursales que ve quien pregunta: la elegida arriba, o en las que trabaja, o todas.</summary>
    public static async Task<List<SucursalVisible>> VisiblesAsync(ContextoPlataforma db, ContextoPeticion ctx, Guid empresaId, CancellationToken ct)
    {
        await Sucursales.PrincipalAsync(db, empresaId, ct);
        var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
        var lista = await db.Sucursales.AsNoTracking()
            .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null)
            .OrderBy(s => s.Codigo)
            .Select(s => new SucursalVisible(s.Id, s.Codigo, s.Nombre))
            .ToListAsync(ct);
        if (permitidas is not null) lista = lista.Where(s => permitidas.Contains(s.Id)).ToList();
        if (ctx.SucursalVista is { } v) lista = lista.Where(s => s.Id == v).ToList();
        return lista;
    }

    public sealed record SaldoEnSucursal(Guid SucursalId, decimal Fisico, decimal Reservado, decimal Disponible, decimal? Minimo, bool BajoMinimo);
    public sealed record FilaStock(
        Guid ProductoId, string Codigo, string Descripcion, string Unidad, string? Categoria,
        List<SaldoEnSucursal> Sucursales, decimal Transito, decimal TotalDisponible,
        bool BajoMinimo, bool SinStock, bool Negativo);

    private static async Task<(List<SucursalVisible>, List<FilaStock>)> StockAsync(
        ContextoPlataforma db, ContextoPeticion ctx, Guid empresaId,
        string? q, Guid? sucursalId, Guid? categoriaId, string? mostrar, CancellationToken ct)
    {
        var visibles = await VisiblesAsync(db, ctx, empresaId, ct);
        if (sucursalId is { } solo) visibles = visibles.Where(s => s.Id == solo).ToList();
        var ids = visibles.Select(s => s.Id).ToArray();

        var consulta = db.Productos.AsNoTracking().Where(p => p.ControlaStock);
        if (categoriaId is { } cat) consulta = consulta.Where(p => p.CategoriaId == cat);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var texto = q.Trim().ToLowerInvariant();
            consulta = consulta.Where(p => p.Codigo.ToLower().Contains(texto) || p.Descripcion.ToLower().Contains(texto));
        }
        var productos = await consulta.OrderBy(p => p.Descripcion)
            .Select(p => new { p.Id, p.Codigo, p.Descripcion, p.UnidadMedida, p.CategoriaId })
            .Take(10000).ToListAsync(ct);
        var categorias = await db.ProductoCategorias.IgnoreQueryFilters().AsNoTracking()
            .Select(c => new { c.Id, c.Nombre }).ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);

        var saldos = await db.Database.SqlQuery<FilaSaldo>($"""
            SELECT producto_id, sucursal_id, fisico, reservado, minimo FROM inv_stock
             WHERE empresa_id = {empresaId} AND sucursal_id = ANY ({ids})
            """).ToListAsync(ct);
        var transito = await db.Database.SqlQuery<FilaTransito>($"""
            SELECT l.producto_id, t.destino_id, sum(l.enviado) AS cantidad
              FROM inv_traslados t JOIN inv_traslado_lineas l ON l.traslado_id = t.id
             WHERE t.empresa_id = {empresaId} AND t.estado = 'en_transito' AND t.destino_id = ANY ({ids})
             GROUP BY l.producto_id, t.destino_id
            """).ToListAsync(ct);

        var porProducto = saldos.ToLookup(s => s.ProductoId);
        var transitoPorProducto = transito.ToLookup(t => t.ProductoId);

        var filas = new List<FilaStock>();
        foreach (var p in productos)
        {
            var suyas = visibles.Select(s =>
            {
                var x = porProducto[p.Id].FirstOrDefault(y => y.SucursalId == s.Id);
                var disp = (x?.Fisico ?? 0) - (x?.Reservado ?? 0);
                return new SaldoEnSucursal(s.Id, x?.Fisico ?? 0, x?.Reservado ?? 0, disp, x?.Minimo,
                                           x?.Minimo is { } mn && disp < mn);
            }).ToList();
            var tr = transitoPorProducto[p.Id].Sum(t => t.Cantidad);
            var total = suyas.Sum(s => s.Disponible);
            var fila = new FilaStock(p.Id, p.Codigo, p.Descripcion, p.UnidadMedida,
                p.CategoriaId is { } c && categorias.TryGetValue(c, out var n) ? n : null,
                suyas, tr, total,
                suyas.Any(s => s.BajoMinimo), suyas.Any(s => s.Disponible <= 0), suyas.Any(s => s.Fisico < 0));

            var entra = mostrar switch
            {
                "bajo_minimo" => fila.BajoMinimo,
                "sin_stock"   => fila.SinStock,
                "negativo"    => fila.Negativo,
                _             => true
            };
            if (entra) filas.Add(fila);
        }
        return (visibles, filas);
    }

    private static async Task<(int Total, List<FilaKardex> Filas)> KardexAsync(
        ContextoPlataforma db, Guid empresaId, Guid productoId, Guid[] sucs, DateOnly? desde, DateOnly? hasta,
        string[]? tipos, int limite, int salto, CancellationToken ct)
    {
        var total = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM inv_movimientos m
             WHERE m.empresa_id = {empresaId} AND m.producto_id = {productoId} AND m.sucursal_id = ANY ({sucs})
               AND ({desde}::date IS NULL OR m.creado_en >= {desde}::date)
               AND ({hasta}::date IS NULL OR m.creado_en < {hasta}::date + 1)
               AND ({tipos}::text[] IS NULL OR m.tipo = ANY ({tipos}::text[]))
            """).FirstAsync(ct);

        // Los números de los documentos se leen sin el recorte del alcance: el
        // kardex es de la sucursal, aunque la factura la haya hecho otra persona.
        var filas = await SinAlcance.LeerAsync(db, () => db.Database.SqlQuery<FilaKardex>($"""
            SELECT m.id, m.orden, m.creado_en, m.sucursal_id, s.codigo AS sucursal_codigo, m.tipo,
                   m.cantidad, m.reservado, m.saldo_fisico, m.saldo_reservado, m.fuente, m.fuente_id,
                   m.documento_id,
                   CASE WHEN fd.correlativo IS NOT NULL THEN fd.serie || '-' || lpad(fd.correlativo::text, 8, '0')
                        WHEN d.correlativo IS NOT NULL THEN d.serie || '-' || lpad(d.correlativo::text, 8, '0') END AS documento_numero,
                   CASE WHEN fd.id IS NOT NULL AND d.id IS NOT NULL AND d.id <> fd.id AND d.correlativo IS NOT NULL
                        THEN d.serie || '-' || lpad(d.correlativo::text, 8, '0')
                        WHEN m.fuente = 'guia' AND d.correlativo IS NOT NULL
                        THEN d.serie || '-' || lpad(d.correlativo::text, 8, '0') END AS referencia_numero,
                   m.guia_id,
                   CASE WHEN g.correlativo IS NOT NULL THEN g.serie || '-' || lpad(g.correlativo::text, 8, '0') ELSE g.serie END AS guia_numero,
                   m.operacion_id,
                   CASE WHEN o.id IS NOT NULL THEN
                        (CASE o.tipo WHEN 'ingreso' THEN 'IN' WHEN 'ajuste' THEN 'AJ' WHEN 'stock_inicial' THEN 'SI' ELSE 'CF' END)
                        || '-' || lpad(o.numero::text, 4, '0') END AS operacion_codigo,
                   pr.razon_social AS proveedor, o.documento_proveedor,
                   m.motivo, m.nota, u.nombre AS usuario, m.costo_unitario
              FROM inv_movimientos m
              JOIN sucursales s ON s.id = m.sucursal_id
              LEFT JOIN documentos fd ON m.fuente = 'documento' AND fd.id = m.fuente_id
              LEFT JOIN documentos d ON d.id = m.documento_id
              LEFT JOIN guias g ON g.id = m.guia_id
              LEFT JOIN inv_operaciones o ON o.id = m.operacion_id
              LEFT JOIN proveedores pr ON pr.id = o.proveedor_id
              LEFT JOIN usuarios u ON u.id = m.usuario_id
             WHERE m.empresa_id = {empresaId} AND m.producto_id = {productoId} AND m.sucursal_id = ANY ({sucs})
               AND ({desde}::date IS NULL OR m.creado_en >= {desde}::date)
               AND ({hasta}::date IS NULL OR m.creado_en < {hasta}::date + 1)
               AND ({tipos}::text[] IS NULL OR m.tipo = ANY ({tipos}::text[]))
             ORDER BY m.orden DESC
             LIMIT {limite} OFFSET {salto}
            """).ToListAsync(ct), ct);

        return (total, filas);
    }

    /// <summary>El tipo de movimiento en palabras (como en la pantalla).</summary>
    public static string NombreTipo(string tipo) => tipo switch
    {
        "stock_inicial"        => "Stock inicial",
        "compra"               => "Ingreso por compra",
        "compra_anulada"       => "Ingreso anulado",
        "ajuste"               => "Ajuste",
        "ajuste_anulado"       => "Ajuste anulado",
        "conteo"               => "Conteo físico",
        "venta"                => "Venta",
        "venta_revertida"      => "Venta revertida",
        "reserva"              => "Reserva",
        "reserva_liberada"     => "Reserva liberada",
        "reserva_revertida"    => "Reserva revertida",
        "despacho"             => "Despacho (guía)",
        "despacho_revertido"   => "Despacho revertido",
        "guia_salida"          => "Salida con guía",
        "guia_revertida"       => "Salida con guía revertida",
        "devolucion"           => "Devolución",
        "devolucion_revertida" => "Devolución revertida",
        "traslado_salida"      => "Traslado salida",
        "traslado_revertido"   => "Traslado revertido",
        "traslado_entrada"     => "Traslado entrada",
        "traslado_faltante"    => "Faltante en traslado",
        "traslado_retorno"     => "Vuelve al origen",
        _ => tipo
    };
}