using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Reportes;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Inventario;

public sealed record LineaOperacion(Guid ProductoId, decimal Cantidad, decimal? CostoUnitario = null,
                                   Guid? PresentacionId = null,    // 061: solo en el ingreso por compra
                                   string? Lote = null, DateOnly? Vence = null,    // 064: compra y stock inicial
                                   List<string>? Series = null);                   // 065: compra
public sealed record ProveedorNuevo(string? TipoDocumento, string? NumeroDocumento, string? RazonSocial, string? Direccion);

public sealed record PeticionIngreso(
    Guid SucursalId, Guid? ProveedorId, ProveedorNuevo? Proveedor, string? DocumentoProveedor,
    DateOnly? Fecha, string? Moneda, string? Nota, List<LineaOperacion>? Lineas,
    Guid? AlmacenId = null);   // 066: a qué almacén de la sucursal entra

public sealed record PeticionAjuste(Guid SucursalId, string? Motivo, string? Nota, List<LineaOperacion>? Lineas);
public sealed record PeticionStockInicial(Guid SucursalId, string? Nota, List<LineaOperacion>? Lineas);
public sealed record PeticionAnularOperacion(string? Motivo);
public sealed record PeticionConteo(Guid SucursalId, Guid? CategoriaId, List<Guid>? ProductoIds, string? Nota);
public sealed record ConteoLinea(Guid ProductoId, decimal? Contado);
public sealed record PeticionGuardarConteo(List<ConteoLinea>? Lineas, string? Nota);

/// <summary>
/// 052 — LAS OPERACIONES DEL INVENTARIO: ingreso por compra, ajuste, stock
/// inicial (a mano o con Excel) y conteo físico.
///
/// Cada una se guarda con sus líneas y, EN LA MISMA TRANSACCIÓN, la base
/// mueve el stock (inv_aplicar_operacion), dejando su movimiento en el kardex.
/// Nada se edita ni se borra: un ingreso o un ajuste equivocado se ANULA, que
/// es el movimiento contrario con su motivo.
/// </summary>
public static class EndpointsInventarioOperaciones
{
    private const int MaxLineas = 500;
    private const int MaxFilasExcel = 5000;
    private static readonly Regex Ruc = new("^(10|15|17|20)[0-9]{9}$", RegexOptions.Compiled);

    public sealed class FilaOperacion
    {
        public Guid Id { get; set; }
        public string Tipo { get; set; } = "";
        public int Numero { get; set; }
        public string Estado { get; set; } = "";
        public DateOnly Fecha { get; set; }
        public DateTime CreadoEn { get; set; }
        public Guid SucursalId { get; set; }
        public string SucursalCodigo { get; set; } = "";
        public string SucursalNombre { get; set; } = "";
        public string? Proveedor { get; set; }
        public string? DocumentoProveedor { get; set; }
        public string? Moneda { get; set; }
        public string? Motivo { get; set; }
        public string? Nota { get; set; }
        public string? Usuario { get; set; }
        public int Lineas { get; set; }
        public decimal Unidades { get; set; }
        public decimal? Costo { get; set; }
    }

    public static void MapearInventarioOperaciones(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/inventario").WithTags("Inventario · operaciones");

        // =====================================================================
        // LISTA Y DETALLE
        // =====================================================================
        grupo.MapGet("/operaciones", async (
            string? tipo, string? estado, DateOnly? desde, DateOnly? hasta, Guid? sucursalId, string? q,
            int? pagina, int? porPagina, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var visibles = (await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct)).Select(s => s.Id)
                .Where(id => sucursalId is null || id == sucursalId).ToArray();
            var tipos = tipo switch
            {
                "ingreso" => new[] { "ingreso" },
                "ajuste" => ["ajuste", "stock_inicial", "conteo"],
                "conteo" => ["conteo"],
                "stock_inicial" => ["stock_inicial"],
                _ => ["ingreso", "ajuste", "stock_inicial", "conteo"]
            };
            var texto = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().ToLowerInvariant() + "%";
            var pp = Listados.PorPagina(porPagina);
            var pg = Listados.Pagina(pagina);

            var total = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                  FROM inv_operaciones o LEFT JOIN proveedores pr ON pr.id = o.proveedor_id
                 WHERE o.empresa_id = {empresaId} AND o.activo AND o.tipo = ANY ({tipos}) AND o.sucursal_id = ANY ({visibles})
                   AND ({estado}::text IS NULL OR o.estado = {estado}::text)
                   AND ({desde}::date IS NULL OR o.fecha >= {desde}::date) AND ({hasta}::date IS NULL OR o.fecha <= {hasta}::date)
                   AND ({texto}::text IS NULL OR lower(coalesce(pr.razon_social, '') || ' ' || coalesce(pr.numero_documento, '') || ' ' ||
                        coalesce(o.documento_proveedor, '') || ' ' || coalesce(o.nota, '')) LIKE {texto}::text)
                """).FirstAsync(ct);

            var filas = await db.Database.SqlQuery<FilaOperacion>($"""
                SELECT o.id, o.tipo, o.numero, o.estado, o.fecha, o.creado_en, o.sucursal_id,
                       s.codigo AS sucursal_codigo, s.nombre AS sucursal_nombre,
                       pr.razon_social AS proveedor, o.documento_proveedor, o.moneda, o.motivo, o.nota,
                       u.nombre AS usuario,
                       (SELECT count(*)::int FROM inv_operacion_lineas l WHERE l.operacion_id = o.id AND l.activo) AS lineas,
                       (SELECT coalesce(sum(abs(coalesce(CASE WHEN o.tipo = 'conteo' THEN l.contado ELSE l.cantidad END, 0))), 0)
                          FROM inv_operacion_lineas l WHERE l.operacion_id = o.id AND l.activo) AS unidades,
                       (SELECT sum(l.cantidad * l.costo_unitario) FROM inv_operacion_lineas l
                         WHERE l.operacion_id = o.id AND l.activo AND l.costo_unitario IS NOT NULL) AS costo
                  FROM inv_operaciones o
                  JOIN sucursales s ON s.id = o.sucursal_id
                  LEFT JOIN proveedores pr ON pr.id = o.proveedor_id
                  LEFT JOIN usuarios u ON u.id = o.creado_por
                 WHERE o.empresa_id = {empresaId} AND o.activo AND o.tipo = ANY ({tipos}) AND o.sucursal_id = ANY ({visibles})
                   AND ({estado}::text IS NULL OR o.estado = {estado}::text)
                   AND ({desde}::date IS NULL OR o.fecha >= {desde}::date) AND ({hasta}::date IS NULL OR o.fecha <= {hasta}::date)
                   AND ({texto}::text IS NULL OR lower(coalesce(pr.razon_social, '') || ' ' || coalesce(pr.numero_documento, '') || ' ' ||
                        coalesce(o.documento_proveedor, '') || ' ' || coalesce(o.nota, '')) LIKE {texto}::text)
                 ORDER BY o.creado_en DESC
                 LIMIT {pp} OFFSET {(pg - 1) * pp}
                """).ToListAsync(ct);

            return Results.Ok(new
            {
                total, pagina = pg, porPagina = pp,
                filas = filas.Select(f => new
                {
                    f.Id, f.Tipo, codigo = $"{TipoOperacion.Prefijo(f.Tipo)}-{f.Numero:D4}", f.Estado, f.Fecha, f.CreadoEn,
                    f.SucursalId, sucursal = $"{f.SucursalCodigo} {f.SucursalNombre}", f.Proveedor, f.DocumentoProveedor,
                    f.Moneda, f.Motivo, motivoNombre = NombreMotivo(f.Motivo), f.Nota, f.Usuario, f.Lineas, f.Unidades, f.Costo
                })
            });
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Ingresos, ajustes, stocks iniciales y conteos (paginado, con filtros)");


        grupo.MapGet("/operaciones/{id:guid}", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var o = await db.InvOperaciones.AsNoTracking().Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (o is null) return Results.NotFound(new { error = "No se encontró." });

            var ids = o.Lineas.Select(l => l.ProductoId).ToList();
            var productos = await db.Productos.IgnoreQueryFilters().AsNoTracking().Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.Codigo, p.Descripcion, p.UnidadMedida }).ToDictionaryAsync(p => p.Id, ct);
            var saldos = await ReglasInventario.SaldosAsync(db, o.EmpresaId, ids, ct);
            var suc = await db.Sucursales.IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == o.SucursalId)
                .Select(s => new { s.Codigo, s.Nombre }).FirstAsync(ct);
            var proveedor = o.ProveedorId is { } pid
                ? await db.Proveedores.IgnoreQueryFilters().AsNoTracking().Where(p => p.Id == pid)
                    .Select(p => new { p.Id, p.TipoDocumento, p.NumeroDocumento, p.RazonSocial }).FirstOrDefaultAsync(ct)
                : null;
            var usuario = await db.Usuarios.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == o.CreadoPor)
                .Select(u => u.Nombre).FirstOrDefaultAsync(ct);
            // 061: el nombre de la presentación en que se compró.
            var presIds = o.Lineas.Where(l => l.PresentacionId is not null).Select(l => l.PresentacionId!.Value).ToList();
            var presNombres = presIds.Count == 0 ? new Dictionary<Guid, string>() : await db.ProductoPresentaciones.IgnoreQueryFilters().AsNoTracking()
                .Where(x => presIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Nombre, ct);

            return Results.Ok(new
            {
                o.Id, o.Tipo, codigo = o.Codigo, o.Estado, o.Fecha, o.CreadoEn, usuario,
                o.SucursalId, sucursal = $"{suc.Codigo} {suc.Nombre}",
                proveedor, o.DocumentoProveedor, o.Moneda, o.Motivo, motivoNombre = NombreMotivo(o.Motivo), o.Nota,
                o.AplicadaEn, o.AnuladaEn, o.AnuladaMotivo,
                lineas = o.Lineas.Where(l => l.Activo).OrderBy(l => productos.GetValueOrDefault(l.ProductoId)?.Descripcion).Select(l =>
                {
                    var p = productos.GetValueOrDefault(l.ProductoId);
                    var s = saldos.FirstOrDefault(x => x.ProductoId == l.ProductoId && x.SucursalId == o.SucursalId);
                    return new
                    {
                        l.ProductoId, codigo = p?.Codigo, descripcion = p?.Descripcion, unidad = p?.UnidadMedida,
                        l.Cantidad, l.CostoUnitario, l.Sistema, l.Contado,
                        presentacion = l.PresentacionId is { } pr ? presNombres.GetValueOrDefault(pr) : null,   // 061
                        l.Lote, l.Vence,                                                                       // 064
                        l.Series,                                                                              // 065
                        l.Factor, l.CantidadPresentacion, l.CostoPresentacion,
                        fisicoAhora = s?.Fisico ?? 0,
                        // Si se anula: lo que quedaría (para avisar antes).
                        quedariaSiAnula = (s?.Fisico ?? 0) - l.Cantidad
                    };
                })
            });
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Una operación de inventario con sus líneas");


        // =====================================================================
        // INGRESO POR COMPRA
        // =====================================================================
        grupo.MapPost("/ingresos", async (PeticionIngreso p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            if (await RevisarSucursalAsync(db, ctx, empresaId, p.SucursalId, ct) is { } malSuc) return malSuc;
            if (await RevisarLineasAsync(db, p.Lineas, positivas: true, ct) is { } malLineas) return Results.BadRequest(new { error = malLineas });
            if (p.Lineas!.Any(l => l.CostoUnitario is < 0)) return Results.BadRequest(new { error = "El costo no puede ser negativo." });

            var moneda = p.Moneda is "USD" or "EUR" ? p.Moneda : "PEN";
            var documento = Limpio(p.DocumentoProveedor, 40)?.ToUpperInvariant();

            // El proveedor: uno guardado, o uno nuevo que se guarda solo.
            Guid? proveedorId = null;
            if (p.ProveedorId is { } elegido)
            {
                if (!await db.Proveedores.AnyAsync(x => x.Id == elegido, ct))
                    return Results.BadRequest(new { error = "Ese proveedor no está en tu lista." });
                proveedorId = elegido;
            }
            else if (p.Proveedor is { NumeroDocumento: { Length: > 0 } } nuevo)
            {
                var (id, error) = await EndpointsProveedores.GuardarOUsarAsync(db, empresaId, nuevo, ct);
                if (error is not null) return Results.BadRequest(new { error });
                proveedorId = id;
            }

            // 066: el almacén, de esta sucursal.
            if (p.AlmacenId is { } alm && !await db.InvAlmacenes.AnyAsync(a => a.Id == alm && a.SucursalId == p.SucursalId, ct))
                return Results.BadRequest(new { error = "Ese almacén no es de la sucursal elegida." });

            var o = new InvOperacion
            {
                EmpresaId = empresaId, Tipo = TipoOperacion.Ingreso, SucursalId = p.SucursalId,
                Fecha = p.Fecha ?? EndpointsNotasCredito.HoyEnPeru(), ProveedorId = proveedorId,
                DocumentoProveedor = documento, Moneda = moneda, Nota = Limpio(p.Nota, 250),
                AlmacenId = p.AlmacenId
            };
            // 061: en una presentación (10 cajas x 12 a S/ 120): al stock entran
            // 120 unidades a S/ 10; se guarda también lo que se escribió.
            var conPres = p.Lineas!.Where(l => l.PresentacionId is not null).Select(l => l.PresentacionId!.Value).Distinct().ToList();
            var pres = conPres.Count == 0 ? [] : await db.ProductoPresentaciones.AsNoTracking()
                .Where(x => conPres.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            foreach (var l in p.Lineas!.Where(l => l.PresentacionId is not null))
                if (!pres.TryGetValue(l.PresentacionId!.Value, out var x) || x.ProductoId != l.ProductoId)
                    return Results.BadRequest(new { error = "Una presentación no es de su producto (o ya no existe). Vuelve a elegirla." });
            if (await LotesMalAsync(db, p.Lineas!, ct) is { } malLote) return Results.BadRequest(new { error = malLote });   // 064
            if (await SeriesMalAsync(db, p.Lineas!, pres, ct) is { } malSerie) return Results.BadRequest(new { error = malSerie });   // 065

            foreach (var g in p.Lineas!.GroupBy(l => (l.ProductoId, l.PresentacionId, Lote: LoteLimpio(l.Lote), l.Vence)))
            {
                var cant = Math.Round(g.Sum(x => x.Cantidad), 4);
                var costo = g.Select(x => x.CostoUnitario).FirstOrDefault(c => c is not null);
                if (g.Key.PresentacionId is { } pid)
                {
                    var f = pres[pid].Factor;
                    o.Lineas.Add(new InvOperacionLinea
                    {
                        EmpresaId = empresaId, ProductoId = g.Key.ProductoId,
                        Cantidad = Math.Round(cant * f, 4),
                        CostoUnitario = costo is { } c ? Math.Round(c / f, 6) : null,
                        PresentacionId = pid, Factor = f, CantidadPresentacion = cant, CostoPresentacion = costo,
                        Lote = g.Key.Lote, Vence = g.Key.Vence, Series = SeriesDe(g)
                    });
                }
                else
                    o.Lineas.Add(new InvOperacionLinea { EmpresaId = empresaId, ProductoId = g.Key.ProductoId, Cantidad = cant, CostoUnitario = costo,
                                                         Lote = g.Key.Lote, Vence = g.Key.Vence, Series = SeriesDe(g) });
            }

            return await GuardarYAplicarAsync(db, o, $"Ingreso registrado: {{codigo}}. Entraron {o.Lineas.Select(x => x.ProductoId).Distinct().Count()} producto(s).", ct);
        })
        .RequierePermiso(Permisos.Inventario.Ingresar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Registrar un ingreso por compra (suma al físico de la sucursal)");


        // =====================================================================
        // AJUSTE
        // =====================================================================
        grupo.MapPost("/ajustes", async (PeticionAjuste p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            if (await RevisarSucursalAsync(db, ctx, empresaId, p.SucursalId, ct) is { } malSuc) return malSuc;
            var motivo = MotivoAjuste.Todos.FirstOrDefault(m => m.Codigo == p.Motivo);
            if (motivo.Codigo is null) return Results.BadRequest(new { error = "Elige el motivo del ajuste." });
            var nota = Limpio(p.Nota, 250);
            if (nota is null || nota.Length < 3) return Results.BadRequest(new { error = "Explica qué pasó (es obligatorio en un ajuste)." });
            if (await RevisarLineasAsync(db, p.Lineas, positivas: false, ct) is { } malLineas) return Results.BadRequest(new { error = malLineas });

            var lineas = Agrupar(p.Lineas!).Where(l => l.Cantidad != 0).ToList();
            if (lineas.Count == 0) return Results.BadRequest(new { error = "Escribe cuánto entra o sale." });
            if (motivo.Signo < 0 && lineas.Any(l => l.Cantidad > 0))
                return Results.BadRequest(new { error = $"Con «{motivo.Nombre}» la mercadería sale: no puede entrar." });
            if (motivo.Signo > 0 && lineas.Any(l => l.Cantidad < 0))
                return Results.BadRequest(new { error = $"Con «{motivo.Nombre}» la mercadería entra: no puede salir." });

            var o = new InvOperacion
            {
                EmpresaId = empresaId, Tipo = TipoOperacion.Ajuste, SucursalId = p.SucursalId,
                Fecha = EndpointsNotasCredito.HoyEnPeru(), Motivo = motivo.Codigo, Nota = nota
            };
            foreach (var l in lineas)
                o.Lineas.Add(new InvOperacionLinea { EmpresaId = empresaId, ProductoId = l.ProductoId, Cantidad = l.Cantidad });

            return await GuardarYAplicarAsync(db, o, "Ajuste registrado: {codigo}.", ct);
        })
        .RequierePermiso(Permisos.Inventario.Ajustar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Registrar un ajuste de stock (con motivo)");


        // =====================================================================
        // STOCK INICIAL: a mano o con Excel
        // =====================================================================
        grupo.MapPost("/stock-inicial", async (PeticionStockInicial p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            if (await RevisarSucursalAsync(db, ctx, empresaId, p.SucursalId, ct) is { } malSuc) return malSuc;
            if (await RevisarLineasAsync(db, p.Lineas, positivas: false, ct) is { } malLineas) return Results.BadRequest(new { error = malLineas });
            if (p.Lineas!.Any(l => l.Cantidad < 0)) return Results.BadRequest(new { error = "El stock inicial no puede ser negativo." });

            var o = new InvOperacion
            {
                EmpresaId = empresaId, Tipo = TipoOperacion.StockInicial, SucursalId = p.SucursalId,
                Fecha = EndpointsNotasCredito.HoyEnPeru(), Nota = Limpio(p.Nota, 250) ?? "a mano", Moneda = "PEN"
            };
            // 068: con lotes, una línea por lote (el lote es opcional: sin él queda
            // «sin lote»); con series, los números son opcionales pero, si se
            // escriben, uno por unidad.
            if (p.Lineas!.Any(l => l.Vence is { } v && v < new DateOnly(2000, 1, 1))) return Results.BadRequest(new { error = "Una fecha de vencimiento no es válida." });
            if (await SeriesInicialesMalAsync(db, p.Lineas!, ct) is { } malSerie) return Results.BadRequest(new { error = malSerie });
            var conLotes = await LotesDeAsync(db, p.Lineas!, ct);
            foreach (var g in p.Lineas!.GroupBy(l => (l.ProductoId,
                         Lote: conLotes.Contains(l.ProductoId) ? LoteLimpio(l.Lote) : null,
                         Vence: conLotes.Contains(l.ProductoId) && LoteLimpio(l.Lote) is not null ? l.Vence : null)))
                o.Lineas.Add(new InvOperacionLinea
                {
                    EmpresaId = empresaId, ProductoId = g.Key.ProductoId, Cantidad = Math.Round(g.Sum(x => x.Cantidad), 4),
                    CostoUnitario = g.Select(x => x.CostoUnitario).FirstOrDefault(c => c is not null),
                    Lote = g.Key.Lote, Vence = g.Key.Vence, Series = SeriesDe(g)
                });

            return await GuardarYAplicarAsync(db, o, "Stock inicial cargado: {codigo}.", ct);
        })
        .RequierePermiso(Permisos.Inventario.Ajustar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Cargar el stock inicial a mano");


        grupo.MapGet("/stock-inicial/plantilla", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var sucursales = await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct);
            var productos = await db.Productos.AsNoTracking().Where(p => p.ControlaStock).OrderBy(p => p.Codigo)
                .Select(p => new { p.Codigo, p.Descripcion, p.UnidadMedida, p.ManejaLotes, p.ManejaSeries }).Take(MaxFilasExcel).ToListAsync(ct);
            // e194: lote, vencimiento y números de serie (solo si algún producto los usa).
            var conLotes = productos.Any(p => p.ManejaLotes);
            var conSeries = productos.Any(p => p.ManejaSeries);

            var hoja = new HojaExcel("Stock inicial") { Horizontal = true };
            hoja.Titulos.Add("Stock inicial por sucursal");
            hoja.Titulos.Add("Escribe lo que hay HOY en cada sucursal. Vacío = no se carga. No cambies los títulos de las columnas.");
            hoja.Columnas.Add(new("Código", TipoColumna.Texto, 16));
            hoja.Columnas.Add(new("Descripción", TipoColumna.Texto, 44));
            hoja.Columnas.Add(new("Unidad", TipoColumna.Texto, 10));
            foreach (var s in sucursales) hoja.Columnas.Add(new($"{s.Codigo} {s.Nombre}", TipoColumna.Cantidad, 18));
            hoja.Columnas.Add(new("Costo unitario (opcional)", TipoColumna.Cantidad, 20));
            if (conLotes)
            {
                hoja.Columnas.Add(new("Lote", TipoColumna.Texto, 16));
                hoja.Columnas.Add(new("Vence (dd/mm/aaaa)", TipoColumna.Texto, 18));
            }
            if (conSeries) hoja.Columnas.Add(new("Números de serie (separados por coma)", TipoColumna.Texto, 40));
            foreach (var p in productos)
            {
                var fila = new List<object?> { p.Codigo, p.Descripcion, p.UnidadMedida };
                fila.AddRange(sucursales.Select(_ => (object?)null));
                fila.Add(null);
                if (conLotes) { fila.Add(p.ManejaLotes ? null : "—"); fila.Add(p.ManejaLotes ? null : "—"); }
                if (conSeries) fila.Add(p.ManejaSeries ? null : "—");
                hoja.Filas.Add([.. fila]);
            }

            var ayuda = new HojaExcel("Ayuda") { ConFiltro = false };
            ayuda.Titulos.Add("Cómo llenarla");
            ayuda.Columnas.Add(new("Columna", TipoColumna.Texto, 24));
            ayuda.Columnas.Add(new("Qué poner", TipoColumna.Texto, 90));
            ayuda.Filas.Add(["Código", "El de tu producto. Solo los que tienen «Controla stock». No lo cambies."]);
            ayuda.Filas.Add(["Una columna por sucursal", "Cuánto hay hoy (con punto o coma decimal). Vacío = esa sucursal no se carga para ese producto."]);
            ayuda.Filas.Add(["Costo unitario", "Opcional, sin IGV, en soles. Servirá para el inventario valorizado."]);
            if (conLotes)
            {
                ayuda.Filas.Add(["Lote", "Solo productos con «Maneja lotes». Un lote por fila: si tiene varios, copia la fila (mismo código) y cambia el lote y las cantidades. Vacío = queda «sin lote»."]);
                ayuda.Filas.Add(["Vence", "La fecha de vencimiento de ese lote (dd/mm/aaaa). Opcional."]);
            }
            if (conSeries)
                ayuda.Filas.Add(["Números de serie", "Solo productos con «Maneja series». Opcional; si los escribes, uno por unidad, separados por coma, y la fila lleva cantidad en UNA sola sucursal."]);
            ayuda.Filas.Add(["«—»", "Ese producto no usa esa columna: déjala así."]);
            ayuda.Filas.Add(["Una vez", "El stock inicial se carga una sola vez por producto y sucursal. Después: ingresos, traslados, ajustes o conteos."]);

            return Results.File(ExcelSimple.Crear([hoja, ayuda]), ExcelSimple.TipoMime, "plantilla-stock-inicial.xlsx");
        })
        .RequierePermiso(Permisos.Inventario.Ajustar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("La plantilla de Excel del stock inicial (una columna por sucursal)");


        grupo.MapPost("/stock-inicial/importar", async (
            HttpContext http, bool? simular, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });

            byte[] contenido;
            using (var memoria = new MemoryStream())
            {
                await http.Request.Body.CopyToAsync(memoria, ct);
                if (memoria.Length > 10 * 1024 * 1024) return Results.BadRequest(new { error = "El archivo pasa de 10 MB." });
                contenido = memoria.ToArray();
            }

            List<LectorExcel.Fila> filas;
            try { filas = LectorExcel.Leer(contenido, MaxFilasExcel + 20); }
            catch (LectorExcel.Problema e) { return Results.BadRequest(new { error = e.Message }); }

            var cab = filas.FindIndex(f => Normalizar(f.Celda(0)) == "CODIGO");
            if (cab < 0)
                return Results.BadRequest(new { error = "No encuentro la fila de títulos (la que empieza con «Código»). Usa la plantilla." });

            var sucursales = await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct);
            var columnas = new List<(int Col, EndpointsInventario.SucursalVisible Suc)>();
            var colCosto = -1;
            int colLote = -1, colVence = -1, colSeries = -1;   // e194
            foreach (var (col, titulo) in filas[cab].Celdas)
            {
                var t = Normalizar(titulo);
                if (t.StartsWith("COSTO")) { colCosto = col; continue; }
                if (t.StartsWith("LOTE")) { colLote = col; continue; }
                if (t.StartsWith("VENCE")) { colVence = col; continue; }
                if (t.StartsWith("NUMEROS DE SERIE") || t.StartsWith("SERIES")) { colSeries = col; continue; }
                var s = sucursales.FirstOrDefault(x => t.StartsWith(x.Codigo));
                if (s is not null) columnas.Add((col, s));
            }
            if (columnas.Count == 0)
                return Results.BadRequest(new { error = "No encuentro columnas de sucursal (por ejemplo «0000 Centro»). Usa la plantilla." });

            var productos = await db.Productos.AsNoTracking().Where(p => p.ControlaStock)
                .Select(p => new { p.Id, p.Codigo, p.Descripcion, p.ManejaLotes, p.ManejaSeries }).ToListAsync(ct);
            var porCodigo = productos.GroupBy(p => p.Codigo.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            var sucIds = columnas.Select(c => c.Suc.Id).ToArray();
            var yaCargados = (await db.Database.SqlQuery<EndpointsInventario.FilaTransito>($"""
                SELECT DISTINCT producto_id, sucursal_id AS destino_id, 0::numeric AS cantidad
                  FROM inv_movimientos WHERE empresa_id = {empresaId} AND tipo = 'stock_inicial' AND sucursal_id = ANY ({sucIds})
                """).ToListAsync(ct)).Select(x => (x.ProductoId, x.DestinoId)).ToHashSet();

            var revisadas = new List<object>();
            var porSucursal = columnas.ToDictionary(c => c.Suc.Id, _ => new List<InvOperacionLinea>());
            int buenas = 0, errores = 0;
            var vistos = new HashSet<string>();
            foreach (var f in filas.Skip(cab + 1).Where(f => !f.Vacia))
            {
                var codigo = f.Celda(0);
                if (!porCodigo.TryGetValue(codigo.ToLowerInvariant(), out var p))
                {
                    errores++;
                    revisadas.Add(new { fila = f.Numero, codigo, resultado = "error", mensaje = "No hay un producto con ese código que controle stock." });
                    continue;
                }
                // e194: lote, vencimiento y números de serie («—» = no aplica).
                string Opcional(int col) => col < 0 || f.Celda(col) is "—" or "-" ? "" : f.Celda(col);
                var lote = LoteLimpio(Opcional(colLote));
                var venceTexto = Opcional(colVence);
                var series = Opcional(colSeries).Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => x.ToUpperInvariant()).ToArray();
                DateOnly? vence = null;
                string? malExtra =
                    lote is not null && !p.ManejaLotes ? "Este producto no maneja lotes: deja el lote vacío (o actívalo en el producto)." :
                    venceTexto.Length > 0 && lote is null ? "El vencimiento va con un lote." :
                    venceTexto.Length > 0 && (vence = FechaExcel(venceTexto)) is null ? $"La fecha «{venceTexto}» no es válida (dd/mm/aaaa)." :
                    series.Length > 0 && !p.ManejaSeries ? "Este producto no maneja números de serie." :
                    series.Distinct().Count() != series.Length ? "Hay un número de serie repetido." :
                    series.Any(x => x.Length > 60) ? "Un número de serie pasa de 60 caracteres." : null;
                if (malExtra is not null)
                {
                    errores++;
                    revisadas.Add(new { fila = f.Numero, codigo, resultado = "error", mensaje = malExtra });
                    continue;
                }
                if (!vistos.Add(codigo.ToLowerInvariant() + "|" + (p.ManejaLotes ? lote ?? "" : "")))
                {
                    errores++;
                    revisadas.Add(new { fila = f.Numero, codigo, resultado = "error", mensaje = p.ManejaLotes
                        ? "Ese código con ese lote ya está en otra fila." : "Ese código ya está en otra fila." });
                    continue;
                }
                if (series.Length > 0)
                {
                    var conCantidad = columnas.Where(c => f.Celda(c.Col).Length > 0).ToList();
                    var unidades = conCantidad.Count == 1 ? LectorExcel.Numero(f.Celda(conCantidad[0].Col)) : null;
                    if (conCantidad.Count != 1 || unidades != series.Length)
                    {
                        errores++;
                        revisadas.Add(new { fila = f.Numero, codigo, resultado = "error", mensaje = conCantidad.Count != 1
                            ? "Con números de serie, la fila lleva cantidad en una sola sucursal."
                            : $"Un número de serie por unidad: {unidades:0.####} unidades y {series.Length} números." });
                        continue;
                    }
                }
                decimal? costo = null;
                if (colCosto >= 0 && f.Celda(colCosto).Length > 0)
                {
                    costo = LectorExcel.Numero(f.Celda(colCosto));
                    if (costo is null or < 0)
                    {
                        errores++;
                        revisadas.Add(new { fila = f.Numero, codigo, resultado = "error", mensaje = $"El costo «{f.Celda(colCosto)}» no es un número válido." });
                        continue;
                    }
                }
                foreach (var (col, s) in columnas)
                {
                    var texto = f.Celda(col);
                    if (texto.Length == 0) continue;
                    var cantidad = LectorExcel.Numero(texto);
                    string? mal = cantidad is null ? $"«{texto}» no es un número." :
                                  cantidad < 0 ? "No puede ser negativo." :
                                  yaCargados.Contains((p.Id, s.Id)) ? "Ya tiene stock inicial en esta sucursal (usa un ajuste o un conteo)." : null;
                    if (mal is not null)
                    {
                        errores++;
                        revisadas.Add(new { fila = f.Numero, codigo, p.Descripcion, sucursal = s.Codigo, resultado = "error", mensaje = mal });
                        continue;
                    }
                    buenas++;
                    revisadas.Add(new { fila = f.Numero, codigo, p.Descripcion, sucursal = s.Codigo, cantidad, costo, lote, resultado = "carga", mensaje = (string?)null });
                    porSucursal[s.Id].Add(new InvOperacionLinea
                    {
                        EmpresaId = empresaId, ProductoId = p.Id, Cantidad = Math.Round(cantidad!.Value, 4), CostoUnitario = costo,
                        Lote = p.ManejaLotes ? lote : null, Vence = p.ManejaLotes && lote is not null ? vence : null,   // e194
                        Series = series.Length > 0 ? series : null
                    });
                }
            }

            if (simular != false)
                return Results.Ok(new { cargan = buenas, errores, filas = revisadas });

            if (buenas == 0) return Results.BadRequest(new { error = "No hay nada que cargar." });

            var codigos = new List<string>();
            foreach (var (sucId, lineas) in porSucursal.Where(x => x.Value.Count > 0))
            {
                var o = new InvOperacion
                {
                    EmpresaId = empresaId, Tipo = TipoOperacion.StockInicial, SucursalId = sucId,
                    Fecha = EndpointsNotasCredito.HoyEnPeru(), Nota = "Excel", Moneda = "PEN", Lineas = lineas
                };
                var r = await GuardarAsync(db, o, ct);
                if (r is not null) return r;
                codigos.Add(o.Codigo);
            }
            return Results.Ok(new
            {
                cargan = buenas, errores,
                mensaje = $"Listo: {buenas} cantidad(es) cargadas ({string.Join(", ", codigos)})." +
                          (errores > 0 ? $" {errores} con error no entraron." : "")
            });
        })
        .RequierePermiso(Permisos.Inventario.Ajustar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Revisar (simular) o cargar el stock inicial desde la plantilla de Excel");


        // =====================================================================
        // ANULAR UN INGRESO O UN AJUSTE
        // =====================================================================
        grupo.MapPost("/operaciones/{id:guid}/anular", async (
            Guid id, PeticionAnularOperacion p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var o = await db.InvOperaciones.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (o is null) return Results.NotFound(new { error = "No se encontró." });
            var permiso = o.Tipo == TipoOperacion.Ingreso ? Permisos.Inventario.Ingresar : Permisos.Inventario.Ajustar;
            if (!ctx.Tiene(permiso)) return Results.Json(new { error = $"Hace falta el permiso {permiso}." }, statusCode: 403);
            var motivo = Limpio(p.Motivo, 250);
            if (motivo is null || motivo.Length < 3) return Results.BadRequest(new { error = "Escribe por qué se anula." });

            var choque = await ReglasInventario.ConPuntoAsync(db, () =>
                db.Database.ExecuteSqlInterpolatedAsync($"SELECT inv_anular_operacion({id}, {motivo})", ct), ct);
            if (choque is not null) return Results.BadRequest(new { error = ReglasInventario.Mensaje(choque) });

            return Results.Ok(new { mensaje = $"{o.Codigo} anulado: se hizo el movimiento contrario y queda en el kardex." });
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Anular un ingreso o un ajuste (el movimiento contrario)");


        // =====================================================================
        // CONTEO FÍSICO (toma de inventario): abrir, guardar lo contado, cerrar
        // =====================================================================
        grupo.MapPost("/conteos", async (PeticionConteo p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            if (await RevisarSucursalAsync(db, ctx, empresaId, p.SucursalId, ct) is { } malSuc) return malSuc;

            var consulta = db.Productos.AsNoTracking().Where(x => x.ControlaStock);
            if (p.ProductoIds is { Count: > 0 } ids) consulta = consulta.Where(x => ids.Contains(x.Id));
            else if (p.CategoriaId is { } cat) consulta = consulta.Where(x => x.CategoriaId == cat);
            var productos = await consulta.OrderBy(x => x.Descripcion).Select(x => x.Id).Take(MaxFilasExcel).ToListAsync(ct);
            if (productos.Count == 0) return Results.BadRequest(new { error = "No hay productos que contar con ese filtro." });

            var o = new InvOperacion
            {
                EmpresaId = empresaId, Tipo = TipoOperacion.Conteo, SucursalId = p.SucursalId, Estado = "abierto",
                Fecha = EndpointsNotasCredito.HoyEnPeru(), Nota = Limpio(p.Nota, 250)
            };
            foreach (var id in productos)
                o.Lineas.Add(new InvOperacionLinea { EmpresaId = empresaId, ProductoId = id });
            db.InvOperaciones.Add(o);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { o.Id, codigo = o.Codigo, mensaje = $"Conteo {o.Codigo} abierto con {productos.Count} producto(s)." });
        })
        .RequierePermiso(Permisos.Inventario.Contar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Abrir un conteo físico");


        grupo.MapPut("/conteos/{id:guid}", async (
            Guid id, PeticionGuardarConteo p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var o = await db.InvOperaciones.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id && x.Tipo == TipoOperacion.Conteo, ct);
            if (o is null) return Results.NotFound(new { error = "No se encontró el conteo." });
            if (o.Estado != "abierto") return Results.Conflict(new { error = "Ese conteo ya está cerrado." });

            foreach (var l in p.Lineas ?? [])
            {
                if (l.Contado is < 0) return Results.BadRequest(new { error = "Lo contado no puede ser negativo." });
                var fila = o.Lineas.FirstOrDefault(x => x.ProductoId == l.ProductoId);
                if (fila is not null) fila.Contado = l.Contado is { } c ? Math.Round(c, 4) : null;
            }
            if (p.Nota is not null) o.Nota = Limpio(p.Nota, 250);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Conteo guardado." });
        })
        .RequierePermiso(Permisos.Inventario.Contar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Guardar lo contado (el conteo sigue abierto)");


        grupo.MapPost("/conteos/{id:guid}/cerrar", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var o = await db.InvOperaciones.AsNoTracking().Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id && x.Tipo == TipoOperacion.Conteo, ct);
            if (o is null) return Results.NotFound(new { error = "No se encontró el conteo." });
            if (o.Estado != "abierto") return Results.Conflict(new { error = "Ese conteo ya está cerrado." });
            if (!o.Lineas.Any(l => l.Activo && l.Contado is not null))
                return Results.BadRequest(new { error = "No hay nada contado todavía." });

            var choque = await ReglasInventario.ConPuntoAsync(db, () =>
                db.Database.ExecuteSqlInterpolatedAsync($"SELECT inv_aplicar_operacion({id})", ct), ct);
            if (choque is not null) return Results.BadRequest(new { error = ReglasInventario.Mensaje(choque) });

            return Results.Ok(new
            {
                mensaje = $"Conteo {o.Codigo} cerrado: se ajustaron las diferencias. Lo que no se contó quedó como estaba."
            });
        })
        .RequierePermiso(Permisos.Inventario.Contar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Cerrar el conteo: ajusta cada diferencia con el stock de ese momento");


        grupo.MapPost("/conteos/{id:guid}/descartar", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var o = await db.InvOperaciones.FirstOrDefaultAsync(x => x.Id == id && x.Tipo == TipoOperacion.Conteo, ct);
            if (o is null) return Results.NotFound(new { error = "No se encontró el conteo." });
            if (o.Estado != "abierto") return Results.Conflict(new { error = "Ese conteo ya está cerrado." });
            o.Estado = "anulada";
            o.AnuladaEn = DateTime.UtcNow;
            o.AnuladaMotivo = "descartado sin cerrar";
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = $"Conteo {o.Codigo} descartado. No se movió nada." });
        })
        .RequierePermiso(Permisos.Inventario.Contar)
        .RequiereModulo(Modulos.Inventario)
        .WithSummary("Descartar un conteo abierto (no mueve nada)");
    }


    // =========================================================================

    private static async Task<IResult> GuardarYAplicarAsync(ContextoPlataforma db, InvOperacion o, string mensaje, CancellationToken ct) =>
        await GuardarAsync(db, o, ct)
        ?? Results.Ok(new { o.Id, codigo = o.Codigo, mensaje = mensaje.Replace("{codigo}", o.Codigo) });

    /// <summary>Guarda la operación y aplica su movimiento, todo o nada.</summary>
    private static async Task<IResult?> GuardarAsync(ContextoPlataforma db, InvOperacion o, CancellationToken ct)
    {
        var choque = await ReglasInventario.ConPuntoAsync(db, async () =>
        {
            db.InvOperaciones.Add(o);
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT inv_aplicar_operacion({o.Id})", ct);
        }, ct);
        if (choque is null) return null;

        db.Entry(o).State = EntityState.Detached;
        foreach (var l in o.Lineas) db.Entry(l).State = EntityState.Detached;

        if (ReglasInventario.SinStockDe(choque) is { } s)
            return await ReglasInventario.SinStockAsync(db, o.EmpresaId, s, ct);
        return Results.BadRequest(new { error = ReglasInventario.Mensaje(choque) });
    }

    private static async Task<IResult?> RevisarSucursalAsync(
        ContextoPlataforma db, ContextoPeticion ctx, Guid empresaId, Guid sucursalId, CancellationToken ct)
    {
        var cfg = await ReglasInventario.ConfigAsync(db, empresaId, ct);
        if (cfg is not { Encendido: true })
            return Results.Conflict(new { error = "El inventario de tu empresa está apagado. Enciéndelo en Inventario › Configuración." });
        var visibles = await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct);
        return visibles.Any(s => s.Id == sucursalId)
            ? null
            : Results.BadRequest(new { error = "Elige una sucursal en la que trabajes (o cambia la de arriba)." });
    }

    private static async Task<string?> RevisarLineasAsync(ContextoPlataforma db, List<LineaOperacion>? lineas, bool positivas, CancellationToken ct)
    {
        if (lineas is null || lineas.Count == 0) return "Agrega al menos un producto.";
        if (lineas.Count > MaxLineas) return $"Son demasiados productos (máximo {MaxLineas}).";
        if (positivas && lineas.Any(l => l.Cantidad <= 0)) return "Cada cantidad tiene que ser mayor que cero.";
        if (lineas.Any(l => Math.Abs(l.Cantidad) > 999_999_999)) return "Una cantidad es demasiado grande.";
        var ids = lineas.Select(l => l.ProductoId).Distinct().ToList();
        var validos = await db.Productos.AsNoTracking().Where(p => ids.Contains(p.Id) && p.ControlaStock).CountAsync(ct);
        return validos == ids.Count ? null : "Hay un producto que no existe o no controla stock (márcale «Controla stock» primero).";
    }

    /// <summary>065: los números de un grupo de líneas, limpios (null si no hay).</summary>
    private static string[]? SeriesDe(IEnumerable<LineaOperacion> g) =>
        g.SelectMany(l => l.Series ?? []).Select(x => (x ?? "").Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToArray() is { Length: > 0 } s ? s : null;

    /// <summary>065: un producto con series entra con un número por unidad (sin repetir).</summary>
    private static async Task<string?> SeriesMalAsync(ContextoPlataforma db, List<LineaOperacion> lineas,
        Dictionary<Guid, ProductoPresentacion> pres, CancellationToken ct)
    {
        var ids = lineas.Select(l => l.ProductoId).Distinct().ToList();
        var conSeries = await db.Productos.AsNoTracking().Where(p => ids.Contains(p.Id) && p.ManejaSeries)
            .Select(p => new { p.Id, p.Descripcion }).ToListAsync(ct);
        foreach (var p in conSeries)
        {
            var mias = lineas.Where(l => l.ProductoId == p.Id).ToList();
            var unidades = mias.Sum(l => l.Cantidad * (l.PresentacionId is { } x && pres.TryGetValue(x, out var pr) ? pr.Factor : 1m));
            var numeros = SeriesDe(mias) ?? [];
            if (unidades != Math.Floor(unidades)) return $"«{p.Descripcion}» maneja series: la cantidad va en unidades enteras.";
            if (numeros.Length != (int)unidades)
                return $"«{p.Descripcion}» maneja series: escribe un número de serie por unidad ({unidades:0.####} unidades, {numeros.Length} números).";
            if (numeros.Distinct().Count() != numeros.Length) return $"«{p.Descripcion}»: hay un número de serie repetido.";
            if (numeros.Any(n => n.Length > 60)) return "Un número de serie pasa de 60 caracteres.";
        }
        return null;
    }

    /// <summary>e194: una fecha del Excel: dd/mm/aaaa, aaaa-mm-dd o el número de serie de Excel.</summary>
    private static DateOnly? FechaExcel(string texto)
    {
        var t = texto.Trim();
        string[] formatos = ["d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d-M-yyyy", "dd-MM-yyyy", "yyyy-MM-dd HH:mm:ss", "d/M/yyyy H:mm:ss"];
        if (DateOnly.TryParseExact(t, formatos, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var f))
            return f.Year >= 2000 ? f : null;
        if (DateTime.TryParseExact(t, formatos, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
            return dt.Year >= 2000 ? DateOnly.FromDateTime(dt) : null;
        if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is > 36500 and < 80000)
            return DateOnly.FromDateTime(DateTime.FromOADate(n));
        return null;
    }

    /// <summary>068: los productos (de estas líneas) que manejan lotes.</summary>
    private static async Task<HashSet<Guid>> LotesDeAsync(ContextoPlataforma db, List<LineaOperacion> lineas, CancellationToken ct)
    {
        var ids = lineas.Select(l => l.ProductoId).Distinct().ToList();
        return (await db.Productos.AsNoTracking().Where(p => ids.Contains(p.Id) && p.ManejaLotes).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>068: en el stock inicial las series son opcionales; si se escriben, una por unidad.</summary>
    private static async Task<string?> SeriesInicialesMalAsync(ContextoPlataforma db, List<LineaOperacion> lineas, CancellationToken ct)
    {
        var ids = lineas.Where(l => l.Series is { Count: > 0 }).Select(l => l.ProductoId).Distinct().ToList();
        if (ids.Count == 0) return null;
        var conSeries = await db.Productos.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Descripcion, p.ManejaSeries }).ToListAsync(ct);
        foreach (var p in conSeries)
        {
            if (!p.ManejaSeries) return $"«{p.Descripcion}» no maneja números de serie (actívalo en el producto).";
            var mias = lineas.Where(l => l.ProductoId == p.Id).ToList();
            var unidades = mias.Sum(l => l.Cantidad);
            var numeros = SeriesDe(mias) ?? [];
            if (numeros.Length != unidades)
                return $"«{p.Descripcion}»: escribe un número de serie por unidad ({unidades:0.####} unidades, {numeros.Length} números), o ninguno.";
            if (numeros.Distinct().Count() != numeros.Length) return $"«{p.Descripcion}»: hay un número de serie repetido.";
            if (numeros.Any(n => n.Length > 60)) return "Un número de serie pasa de 60 caracteres.";
        }
        return null;
    }

    /// <summary>064: «l-001 » → «L-001» (null si está vacío).</summary>
    internal static string? LoteLimpio(string? lote) =>
        string.IsNullOrWhiteSpace(lote) ? null : (lote.Trim().ToUpperInvariant() is var t && t.Length > 40 ? t[..40] : lote.Trim().ToUpperInvariant());

    /// <summary>064: un producto con lotes entra con su lote (el vencimiento, si lo tiene).</summary>
    private static async Task<string?> LotesMalAsync(ContextoPlataforma db, List<LineaOperacion> lineas, CancellationToken ct)
    {
        var ids = lineas.Select(l => l.ProductoId).Distinct().ToList();
        var conLotes = await db.Productos.AsNoTracking().Where(p => ids.Contains(p.Id) && p.ManejaLotes)
            .Select(p => new { p.Id, p.Descripcion }).ToListAsync(ct);
        foreach (var p in conLotes)
            if (lineas.Any(l => l.ProductoId == p.Id && LoteLimpio(l.Lote) is null))
                return $"«{p.Descripcion}» maneja lotes: escribe el lote (y su vencimiento) en cada línea.";
        if (lineas.Any(l => l.Vence is { } v && v < new DateOnly(2000, 1, 1))) return "Una fecha de vencimiento no es válida.";
        return null;
    }

    private static List<LineaOperacion> Agrupar(List<LineaOperacion> lineas) =>
        lineas.GroupBy(l => l.ProductoId)
              .Select(g => new LineaOperacion(g.Key, Math.Round(g.Sum(x => x.Cantidad), 4),
                                              g.Select(x => x.CostoUnitario).FirstOrDefault(c => c is not null)))
              .ToList();

    public static string? NombreMotivo(string? codigo) =>
        codigo is null ? null : MotivoAjuste.Todos.FirstOrDefault(m => m.Codigo == codigo).Nombre ?? codigo;

    internal static string? Limpio(string? s, int largo)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = string.Join(' ', s.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > largo ? t[..largo] : t;
    }

    private static string Normalizar(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) b.Append(ch);
        return b.ToString();
    }

    internal static bool EsRuc(string s) => Ruc.IsMatch(s);
}