using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Inventario;
using Plataforma.Api.Reportes;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.NotasVenta;

public sealed record LineaNotaVenta(Guid ProductoId, decimal Cantidad, decimal Precio, Guid? PresentacionId = null);

public sealed record PeticionNotaVenta(
    Guid? SucursalId, Guid? ClienteId, string? Forma, string? Medio, string? Vence, string? EntregaFecha,
    bool? SeLoLleva, List<LineaNotaVenta>? Lineas, string? Nota);

public sealed record PeticionEntregarNota(string? Forma, string? Medio, string? Vence);

public sealed record PeticionAnularNota(string? Motivo);

public sealed record PeticionPagoNota(decimal? Monto, string? Fecha, string? Medio, string? Referencia);

/// <summary>
/// e282 (097) — NOTAS DE VENTA: vender sin comprobante electrónico (no va a SUNAT).
///
///   GET  /api/notas-venta                       la lista con su franja (formato=xlsx: Excel)
///   GET  /api/notas-venta/{id}                  una nota con sus productos, pagos y datos del ticket
///   POST /api/notas-venta                       nueva: contado, fiado o pedido (mueve el stock)
///   POST /api/notas-venta/{id}/entregar         el pedido (o lo que se llevaba después) sale del stock
///   POST /api/notas-venta/{id}/anular           con motivo (vuelve el stock)
///   POST /api/notas-venta/{id}/pagos            un pago de una nota fiada
///   POST /api/notas-venta/pagos/{id}/anular     anular ese pago (con motivo)
///
/// Convertir en boleta o factura es la pantalla de emitir con ?nota=… (el
/// borrador lleva nota_venta_id y esa boleta o factura NO mueve stock).
/// El stock lo pone siempre la base (nota_venta_stock): reservado mientras no
/// se lo lleva, fuera cuando se lo lleva, de vuelta si se anula.
/// </summary>
public static class EndpointsNotasVenta
{
    private static readonly string[] Medios = ["efectivo", "yape", "tarjeta", "transferencia", "otro"];
    private const string Prefijo = "/api/notas-venta";

    public sealed class FilaId { public Guid Id { get; set; } }

    private sealed class FilaProducto
    {
        public Guid Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public string UnidadMedida { get; set; } = "NIU";
        public string TipoAfectacion { get; set; } = "10";
        public bool ManejaSeries { get; set; }
        public bool EsModelo { get; set; }
        public Guid? PresentacionId { get; set; }
        public decimal? Factor { get; set; }
        public string? PresentacionNombre { get; set; }
        public string? PresentacionUnidad { get; set; }
    }

    private sealed class FilaEstado
    {
        public string Estado { get; set; } = "";
        public string Forma { get; set; } = "";
        public DateTime? EntregadoEn { get; set; }
        public Guid? DocumentoId { get; set; }
        public string? Documento { get; set; }
        public decimal Pagado { get; set; }
        public decimal Saldo { get; set; }
        public Guid? ClienteId { get; set; }
        public Guid SucursalId { get; set; }
        public string Numero { get; set; } = "";
    }

    public static void MapearNotasVenta(this IEndpointRouteBuilder rutas)
    {
        var g = rutas.MapGroup(Prefijo).WithTags("Notas de venta");

        // =====================================================================
        // LA LISTA Y SU FRANJA
        // =====================================================================
        g.MapGet("/", async (string? estado, string? q, string? desde, string? hasta, int? pagina, int? porPagina, string? formato,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var est = estado is "por_cobrar" or "pedidos" or "pagadas" or "convertidas" or "anuladas" ? estado : "todas";
            var texto = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().ToUpperInvariant() + "%";
            DateOnly? d1 = Fecha(desde), d2 = Fecha(hasta);
            var excel = string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase);
            var pp = excel ? 5000 : Math.Clamp(porPagina ?? 20, 10, 100);
            var pg = excel ? 1 : Math.Max(pagina ?? 1, 1);
            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
            var todas = permitidas is null;
            var sucs = permitidas?.ToArray() ?? [];

            var json = await TextoAsync(db, $"""
                WITH e AS (SELECT * FROM notas_venta_filas() f WHERE ({todas} OR f.sucursal_id = ANY ({sucs}))),
                v AS (
                    SELECT * FROM e
                     WHERE ({est} = 'todas'
                            OR ({est} = 'por_cobrar'  AND e.saldo > 0.005)
                            OR ({est} = 'pedidos'     AND e.entregado_en IS NULL AND e.anulado_en IS NULL)
                            OR ({est} = 'pagadas'     AND e.estado = 'pagada')
                            OR ({est} = 'convertidas' AND e.estado = 'convertida')
                            OR ({est} = 'anuladas'    AND e.estado = 'anulada'))
                       AND ({d1}::date IS NULL OR (e.fecha AT TIME ZONE 'America/Lima')::date >= {d1}::date)
                       AND ({d2}::date IS NULL OR (e.fecha AT TIME ZONE 'America/Lima')::date <= {d2}::date)
                       AND ({texto}::text IS NULL
                            OR upper(e.numero || ' ' || e.cliente || ' ' || coalesce(e.cliente_doc, '') || ' ' || coalesce(e.documento, '')) LIKE {texto}::text
                            OR EXISTS (SELECT 1 FROM nota_venta_lineas l WHERE l.nota_id = e.id AND upper(l.descripcion || ' ' || coalesce(l.codigo, '')) LIKE {texto}::text))
                ),
                mes AS (SELECT * FROM e WHERE e.fecha >= (date_trunc('month', hoy_peru())::timestamp AT TIME ZONE 'America/Lima'))
                SELECT json_build_object(
                    'total', (SELECT count(*) FROM v),
                    'pagina', {pg}, 'porPagina', {pp},
                    'cifras', json_build_object(
                        'vendidoMes',   (SELECT coalesce(sum(total), 0) FROM mes WHERE anulado_en IS NULL AND forma <> 'pedido'),
                        'notasMes',     (SELECT count(*) FROM mes WHERE anulado_en IS NULL),
                        'fiado',        (SELECT coalesce(sum(saldo), 0) FROM e),
                        'fiadoClientes',(SELECT count(DISTINCT cliente_id) FROM e WHERE saldo > 0.005),
                        'pedidos',      (SELECT count(*) FROM e WHERE entregado_en IS NULL AND anulado_en IS NULL),
                        'pedidosMonto', (SELECT coalesce(sum(total), 0) FROM e WHERE entregado_en IS NULL AND anulado_en IS NULL),
                        'convertidasMes', (SELECT count(*) FROM mes WHERE estado = 'convertida')),
                    'filas', (SELECT coalesce(json_agg(t), '[]'::json) FROM (
                        SELECT id, numero, fecha, cliente, cliente_doc AS "clienteDoc", forma, medio, vence,
                               entrega_fecha AS "entregaFecha", entregado_en AS "entregadoEn", total, pagado, saldo,
                               documento_id AS "documentoId", documento, documento_tipo AS "documentoTipo", estado, vendedor
                          FROM v ORDER BY fecha DESC
                         LIMIT {pp} OFFSET {(pg - 1) * pp}) t)
                )::text AS "Value"
                """, ct);
            if (!excel) return Results.Content(json ?? "{}", "application/json");

            var datos = JsonNode.Parse(json ?? "{}")!.AsObject();
            var hoja = new HojaExcel("Notas de venta") { Horizontal = true };
            hoja.Titulos.AddRange(["Notas de venta", $"Al {DateTime.UtcNow.AddHours(-5):dd/MM/yyyy}."]);
            hoja.Columnas.AddRange([new("Fecha", TipoColumna.Texto, 17), new("Nota", TipoColumna.Texto, 16), new("Cliente", TipoColumna.Texto, 34),
                new("DNI / RUC", TipoColumna.Texto, 14), new("Cómo paga", TipoColumna.Texto, 11), new("Total", TipoColumna.Monto, 13),
                new("Pagado", TipoColumna.Monto, 13), new("Debe", TipoColumna.Monto, 13), new("Estado", TipoColumna.Texto, 13),
                new("Boleta / factura", TipoColumna.Texto, 17)]);
            static string T(JsonNode? n) => n?.ToString() ?? "";
            static CeldaExcel M(JsonNode? n) => new(n is null ? 0m : n.GetValue<decimal>(), TipoColumna.Monto);
            var estados = new Dictionary<string, string> { ["por_cobrar"] = "Por cobrar", ["pagada"] = "Pagada", ["pedido"] = "Pedido",
                ["convertida"] = "Convertida", ["anulada"] = "Anulada" };
            foreach (var f in (datos["filas"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var fecha = DateTimeOffset.TryParse(T(f["fecha"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var fx)
                    ? fx.ToOffset(TimeSpan.FromHours(-5)).ToString("dd/MM/yyyy HH:mm") : "";
                hoja.Filas.Add([fecha, T(f["numero"]), T(f["cliente"]), T(f["clienteDoc"]), T(f["forma"]), M(f["total"]), M(f["pagado"]),
                    M(f["saldo"]), estados.GetValueOrDefault(T(f["estado"]), T(f["estado"])), T(f["documento"])]);
            }
            return Results.File(ExcelSimple.Crear([hoja]), ExcelSimple.TipoMime, $"notas-de-venta-{DateTime.UtcNow.AddHours(-5):yyyy-MM-dd}.xlsx");
        })
        .RequierePermiso(Permisos.Facturacion.Ver).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las notas de venta con su franja (vendido, fiado, pedidos, convertidas)");

        // =====================================================================
        // UNA NOTA: productos, pagos y lo que lleva el ticket
        // =====================================================================
        g.MapGet("/{id:guid}", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var json = await TextoAsync(db, $"""
                SELECT json_build_object(
                    'id', f.id, 'numero', f.numero, 'fecha', f.fecha, 'sucursalId', f.sucursal_id,
                    'cliente', CASE WHEN f.cliente_id IS NULL THEN NULL ELSE json_build_object('id', f.cliente_id, 'nombre', f.cliente,
                                     'tipoDocumento', f.cliente_tipo_doc, 'numeroDocumento', f.cliente_doc) END,
                    'clienteNombre', f.cliente,
                    'forma', f.forma, 'medio', f.medio, 'vence', f.vence, 'entregaFecha', f.entrega_fecha, 'entregadoEn', f.entregado_en,
                    'total', f.total, 'pagado', f.pagado, 'saldo', f.saldo, 'estado', f.estado, 'vendedor', f.vendedor, 'nota', f.nota,
                    'anuladoEn', f.anulado_en, 'anuladoMotivo', f.anulado_motivo,
                    'documento', CASE WHEN f.documento_id IS NULL THEN NULL ELSE json_build_object('id', f.documento_id, 'numero', f.documento, 'tipo', f.documento_tipo) END,
                    'borradores', (SELECT coalesce(json_agg(json_build_object('id', d.id, 'tipo', d.tipo) ORDER BY d.creado_en DESC), '[]'::json)
                                     FROM documentos d WHERE d.nota_venta_id = f.id AND d.activo AND d.estado IN ('borrador', 'error')),
                    'lineas', (SELECT coalesce(json_agg(json_build_object(
                                    'numero', l.numero, 'productoId', l.producto_id, 'codigo', l.codigo, 'descripcion', l.descripcion,
                                    'unidadMedida', l.unidad_medida, 'tipoAfectacion', l.tipo_afectacion, 'cantidad', l.cantidad,
                                    'precio', l.precio, 'valorUnitario', l.valor_unitario, 'importe', l.importe,
                                    'presentacionId', l.presentacion_id, 'factor', l.factor) ORDER BY l.numero), '[]'::json)
                                 FROM nota_venta_lineas l WHERE l.nota_id = f.id),
                    'pagos', (SELECT coalesce(json_agg(json_build_object('id', p.id, 'fecha', p.fecha, 'monto', p.monto, 'medio', p.medio,
                                    'referencia', p.referencia, 'anuladoEn', p.anulado_en, 'anuladoMotivo', p.anulado_motivo,
                                    'registro', coalesce(u.nombre, '')) ORDER BY p.fecha DESC, p.creado_en DESC), '[]'::json)
                                FROM deuda_pagos p LEFT JOIN usuarios u ON u.id = p.creado_por
                               WHERE p.nota_venta_id = f.id AND p.activo),
                    'emisor', (SELECT json_build_object('nombre', coalesce(nullif(e.nombre_comercial, ''), e.razon_social, e.nombre),
                                    'razonSocial', e.razon_social, 'ruc', e.ruc,
                                    'direccion', coalesce(nullif(s.direccion, ''), e.direccion), 'sucursal', s.nombre, 'telefono', e.telefono)
                                 FROM empresas e JOIN sucursales s ON s.id = f.sucursal_id WHERE e.id = ctx_empresa())
                )::text AS "Value"
                  FROM notas_venta_filas() f WHERE f.id = {id}
                """, ct);
            if (json is null) return Results.NotFound(new { error = "No se encontró esa nota de venta." });
            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
            var o = JsonNode.Parse(json)!.AsObject();
            if (permitidas is not null && Guid.TryParse(o["sucursalId"]?.ToString(), out var suc) && !permitidas.Contains(suc))
                return Results.NotFound(new { error = "No se encontró esa nota de venta." });
            o["puede"] = new JsonObject
            {
                ["emitir"] = ctx.Tiene(Permisos.Facturacion.Emitir),
                ["anular"] = ctx.Tiene(Permisos.Facturacion.Anular),
            };
            return Results.Content(o.ToJsonString(), "application/json");
        })
        .RequierePermiso(Permisos.Facturacion.Ver).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Una nota de venta con sus productos, sus pagos y los datos del ticket");

        // =====================================================================
        // NUEVA
        // =====================================================================
        g.MapPost("/", async (PeticionNotaVenta p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresa) return Mal("Abre una empresa para vender.");
            var forma = (p.Forma ?? "").Trim().ToLowerInvariant();
            if (forma is not ("contado" or "fiado" or "pedido")) return Mal("Elige cómo paga: contado, fiado o pedido.");
            var lineas = p.Lineas ?? [];
            if (lineas.Count == 0) return Mal("Agrega al menos un producto.");
            if (lineas.Count > 200) return Mal("Son demasiados productos para una nota (máximo 200).");
            if (lineas.Any(l => l.Cantidad <= 0 || l.Cantidad > 1_000_000)) return Mal("Cada cantidad tiene que ser mayor que cero.");
            if (lineas.Any(l => l.Precio < 0 || l.Precio > 10_000_000)) return Mal("Revisa los precios: no pueden ser negativos.");
            if (lineas.Any(l => decimal.Round(l.Cantidad, 4) != l.Cantidad)) return Mal("La cantidad lleva hasta 4 decimales.");

            var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
            string? medio = null;
            DateOnly? vence = null, entrega = null;
            var seLoLleva = forma != "pedido" && (p.SeLoLleva ?? true);
            if (forma == "contado") medio = Medios.Contains((p.Medio ?? "").Trim().ToLowerInvariant()) ? p.Medio!.Trim().ToLowerInvariant() : "efectivo";
            if (forma == "fiado")
            {
                vence = Fecha(p.Vence);
                if (vence is null) return Mal("Escribe hasta qué fecha paga.");
                if (vence < hoy) return Mal("La fecha en que paga no puede ser pasada.");
            }
            if (forma == "pedido" && !string.IsNullOrWhiteSpace(p.EntregaFecha))
            {
                entrega = Fecha(p.EntregaFecha);
                if (entrega is null) return Mal("La fecha de entrega no es válida.");
            }
            if (forma != "contado" && p.ClienteId is null)
                return Mal(forma == "fiado" ? "Para fiar, elige al cliente: así sabes quién te debe." : "Para un pedido, elige al cliente.");
            if (p.ClienteId is { } cli && !await db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM clientes WHERE id = {cli} AND activo) AS \"Value\"").FirstAsync(ct))
                return Mal("Ese cliente no existe.");

            // La sucursal: la elegida, la de por defecto o la primera en la que trabaja.
            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
            var sucursal = p.SucursalId ?? ctx.SucursalVista ?? ctx.SucursalDefecto;
            if (sucursal is null || (permitidas is not null && !permitidas.Contains(sucursal.Value)))
                sucursal = await db.Sucursales.AsNoTracking()
                    .Where(s => s.EmpresaId == empresa && s.EliminadaEn == null && (permitidas == null || permitidas.Contains(s.Id)))
                    .OrderBy(s => s.Codigo).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (sucursal is not { } suc) return Mal("No tienes una sucursal en la que vender.");
            if (permitidas is not null && !permitidas.Contains(suc)) return Mal("No trabajas en esa sucursal.");

            // Los productos, de la base (nunca lo que diga la pantalla, salvo cantidad y precio).
            var ids = lineas.Select(l => l.ProductoId).Distinct().ToArray();
            var productos = (await db.Database.SqlQuery<FilaProducto>($"""
                SELECT p.id, p.codigo, p.descripcion, p.unidad_medida, p.tipo_afectacion,
                       coalesce(p.maneja_series, false) AS maneja_series, coalesce(p.es_modelo, false) AS es_modelo,
                       NULL::uuid AS presentacion_id, NULL::numeric AS factor, NULL::text AS presentacion_nombre, NULL::text AS presentacion_unidad
                  FROM productos p WHERE p.id = ANY ({ids}) AND p.activo
                """).ToListAsync(ct)).ToDictionary(x => x.Id);
            var presIds = lineas.Where(l => l.PresentacionId is not null).Select(l => l.PresentacionId!.Value).Distinct().ToArray();
            var presentaciones = presIds.Length == 0 ? [] : (await db.Database.SqlQuery<FilaProducto>($"""
                SELECT x.producto_id AS id, '' AS codigo, '' AS descripcion, '' AS unidad_medida, '' AS tipo_afectacion,
                       false AS maneja_series, false AS es_modelo,
                       x.id AS presentacion_id, x.factor, x.nombre AS presentacion_nombre, x.unidad_medida AS presentacion_unidad
                  FROM producto_presentaciones x WHERE x.id = ANY ({presIds}) AND x.activo
                """).ToListAsync(ct)).ToDictionary(x => x.PresentacionId!.Value);

            var filas = new List<(int N, FilaProducto P, decimal Cant, decimal Precio, decimal Valor, decimal Importe, Guid? Pres, decimal? Factor, string Desc, string Unidad)>();
            var n = 0;
            foreach (var l in lineas)
            {
                if (!productos.TryGetValue(l.ProductoId, out var pr)) return Mal("Uno de los productos no existe o está desactivado.");
                if (pr.EsModelo) return Mal($"«{pr.Descripcion}» es un modelo: elige una de sus variantes (talla, color…).");
                if (pr.ManejaSeries) return Mal($"«{pr.Descripcion}» lleva número de serie: véndelo con boleta o factura.");
                string desc = pr.Descripcion, unidad = pr.UnidadMedida;
                decimal? factor = null;
                if (l.PresentacionId is { } pid)
                {
                    if (!presentaciones.TryGetValue(pid, out var pres) || pres.Id != pr.Id) return Mal($"La presentación de «{pr.Descripcion}» no existe.");
                    factor = pres.Factor;
                    desc = $"{pr.Descripcion} ({pres.PresentacionNombre})";
                    unidad = pres.PresentacionUnidad ?? unidad;
                }
                var precio = Math.Round(l.Precio, 2, MidpointRounding.AwayFromZero);
                var valor = pr.TipoAfectacion == "10" ? Math.Round(precio / 1.18m, 6, MidpointRounding.AwayFromZero) : precio;
                var importe = Math.Round(l.Cantidad * precio, 2, MidpointRounding.AwayFromZero);
                filas.Add((++n, pr, l.Cantidad, precio, valor, importe, l.PresentacionId, factor, desc, unidad));
            }
            var total = filas.Sum(x => x.Importe);
            if (total <= 0) return Mal("El total tiene que ser mayor que cero.");
            var nota = string.IsNullOrWhiteSpace(p.Nota) ? null : Recortar(p.Nota.Trim(), 300);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var num = (await db.Database.SqlQuery<FilaNumero>($"SELECT o_serie, o_numero FROM nota_venta_numero({suc})").ToListAsync(ct)).Single();
                var id = (await db.Database.SqlQuery<FilaId>($"""
                    INSERT INTO notas_venta (empresa_id, sucursal_id, serie, correlativo, cliente_id, forma, medio, vence, entrega_fecha,
                                             entregado_en, total, nota, creado_por)
                    VALUES ({empresa}, {suc}, {num.OSerie}, {num.ONumero}, {p.ClienteId}::uuid, {forma}, {medio}::text, {vence}::date, {entrega}::date,
                            {(seLoLleva ? DateTime.UtcNow : (DateTime?)null)}::timestamptz, {total}, {nota}::text, ctx_usuario())
                    RETURNING id
                    """).ToListAsync(ct)).Single().Id;
                foreach (var f in filas)
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO nota_venta_lineas (empresa_id, nota_id, numero, producto_id, codigo, descripcion, unidad_medida, tipo_afectacion,
                                                       cantidad, precio, valor_unitario, importe, presentacion_id, factor)
                        VALUES ({empresa}, {id}, {f.N}, {f.P.Id}, {f.P.Codigo}, {Recortar(f.Desc, 500)}, {f.Unidad}, {f.P.TipoAfectacion},
                                {f.Cant}, {f.Precio}, {f.Valor}, {f.Importe}, {f.Pres}::uuid, {f.Factor}::numeric)
                        """, ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nota_venta_stock({id})", ct);
                await tx.CommitAsync(ct);

                var numero = $"{num.OSerie}-{num.ONumero:D8}";
                return Results.Ok(new
                {
                    id, numero,
                    mensaje = forma switch
                    {
                        "fiado" => $"Nota {numero} guardada. Queda por cobrar en Cobros y pagos.",
                        "pedido" => $"Pedido {numero} guardado. El stock queda separado hasta que lo entregues.",
                        _ => seLoLleva ? $"Nota {numero} guardada." : $"Nota {numero} guardada. El stock queda separado hasta que se lo lleve.",
                    }
                });
            }
            catch (Exception e) when (ReglasInventario.SinStockDe(e) is { } falta)
            {
                await tx.RollbackAsync(ct);
                return await ReglasInventario.SinStockAsync(db, empresa, falta, ct);
            }
        })
        .RequierePermiso(Permisos.Facturacion.Emitir).RequiereFicha().RequiereModulo(Modulos.Facturacion)
        .WithSummary("Nueva nota de venta (contado, fiado o pedido)");

        // =====================================================================
        // ENTREGAR: el pedido (o lo que se llevaba después) sale del stock
        // =====================================================================
        g.MapPost("/{id:guid}/entregar", async (Guid id, PeticionEntregarNota p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (await EstadoAsync(db, ctx, id, ct) is not { } n) return Results.NotFound(new { error = "No se encontró esa nota de venta." });
            if (n.Estado == "anulada") return Mal("La nota está anulada.");
            if (n.EntregadoEn is not null) return Mal("Ya se entregó.");

            var forma = n.Forma;
            string? medio = null;
            DateOnly? vence = null;
            if (n.Forma == "pedido")
            {
                forma = (p.Forma ?? "").Trim().ToLowerInvariant();
                if (forma is not ("contado" or "fiado")) return Mal("Elige cómo paga al entregar: contado o fiado.");
                if (forma == "contado") medio = Medios.Contains((p.Medio ?? "").Trim().ToLowerInvariant()) ? p.Medio!.Trim().ToLowerInvariant() : "efectivo";
                else
                {
                    vence = Fecha(p.Vence);
                    var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
                    if (vence is null) return Mal("Escribe hasta qué fecha paga.");
                    if (vence < hoy) return Mal("La fecha en que paga no puede ser pasada.");
                }
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE notas_venta
                       SET entregado_en = now(), forma = {forma},
                           medio = CASE WHEN {forma} = 'contado' THEN coalesce({medio}::text, medio, 'efectivo') ELSE medio END,
                           vence = CASE WHEN {forma} = 'fiado' THEN coalesce({vence}::date, vence) ELSE vence END,
                           modificado_en = now(), modificado_por = ctx_usuario()
                     WHERE id = {id} AND entregado_en IS NULL AND anulado_en IS NULL
                    """, ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nota_venta_stock({id})", ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception e) when (ReglasInventario.SinStockDe(e) is { } falta)
            {
                await tx.RollbackAsync(ct);
                return await ReglasInventario.SinStockAsync(db, ctx.EmpresaId!.Value, falta, ct);
            }
            return Results.Ok(new { mensaje = forma == "fiado" ? $"Entregado. {n.Numero} queda por cobrar." : $"Entregado. {n.Numero} salió del stock." });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir).RequiereFicha().RequiereModulo(Modulos.Facturacion)
        .WithSummary("Entregar un pedido: sale del stock");

        // =====================================================================
        // ANULAR (vuelve el stock). Nada se borra.
        // =====================================================================
        g.MapPost("/{id:guid}/anular", async (Guid id, PeticionAnularNota p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var motivo = (p.Motivo ?? "").Trim();
            if (motivo.Length < 3) return Mal("Escribe por qué se anula (3 letras como mínimo).");
            if (await EstadoAsync(db, ctx, id, ct) is not { } n) return Results.NotFound(new { error = "No se encontró esa nota de venta." });
            if (n.Estado == "anulada") return Mal("Ya estaba anulada.");
            if (n.DocumentoId is not null)
                return Mal($"Ya se convirtió en {n.Documento}. Para deshacer la venta, haz una nota de crédito a ese comprobante.");
            if (n.Pagado > 0) return Mal("Tiene pagos anotados: anúlalos primero (abajo, en Pagos).");

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE notas_venta SET anulado_en = now(), anulado_motivo = {Recortar(motivo, 200)}, anulado_por = ctx_usuario(),
                                       modificado_en = now(), modificado_por = ctx_usuario()
                 WHERE id = {id} AND anulado_en IS NULL
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nota_venta_stock({id})", ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { mensaje = $"{n.Numero} anulada. Lo que había salido del stock volvió." });
        })
        .RequierePermiso(Permisos.Facturacion.Anular).RequiereFicha().RequiereModulo(Modulos.Facturacion)
        .WithSummary("Anular una nota de venta (con motivo; vuelve el stock)");

        // =====================================================================
        // PAGOS DE UNA NOTA FIADA (también en Cobros y pagos › Por cobrar)
        // =====================================================================
        g.MapPost("/{id:guid}/pagos", async (Guid id, PeticionPagoNota p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (await EstadoAsync(db, ctx, id, ct) is not { } n) return Results.NotFound(new { error = "No se encontró esa nota de venta." });
            if (n.Forma != "fiado" || n.Estado == "anulada") return Mal("Solo se anotan pagos de una nota fiada (no anulada).");
            var monto = Math.Round(p.Monto ?? 0, 2);
            if (monto <= 0) return Mal("Escribe cuánto pagó (mayor que cero).");
            var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
            var fecha = string.IsNullOrWhiteSpace(p.Fecha) ? hoy : Fecha(p.Fecha) ?? DateOnly.MinValue;
            if (fecha == DateOnly.MinValue) return Mal("La fecha no es válida.");
            if (fecha > hoy) return Mal("La fecha del pago no puede ser futura.");
            if (n.Saldo <= 0.005m) return Results.Conflict(new { error = "Ya está pagada: no falta nada." });
            if (monto > n.Saldo + 0.005m) return Mal($"El pago es mayor que lo que falta (S/ {n.Saldo:0.00}).");
            var medio = Medios.Contains(p.Medio) ? p.Medio! : "efectivo";
            var referencia = string.IsNullOrWhiteSpace(p.Referencia) ? null : Recortar(p.Referencia.Trim(), 80);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO deuda_pagos (empresa_id, lado, nota_venta_id, fecha, monto, moneda, medio, referencia, creado_por)
                VALUES (ctx_empresa(), 'cobrar', {id}, {fecha}, {monto}, 'PEN', {medio}, {referencia}::text, ctx_usuario())
                """, ct);
            var queda = n.Saldo - monto;
            return Results.Ok(new { saldo = queda, mensaje = queda <= 0.005m ? "Pago anotado. Ya no debe nada." : $"Pago anotado. Le quedan S/ {queda:0.00}." });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Anotar un pago de una nota fiada");

        g.MapPost("/pagos/{pago:guid}/anular", async (Guid pago, PeticionAnularNota p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var motivo = (p.Motivo ?? "").Trim();
            if (motivo.Length < 3) return Mal("Escribe por qué se anula (3 letras como mínimo).");
            var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE deuda_pagos SET anulado_en = now(), anulado_motivo = {Recortar(motivo, 200)}
                 WHERE id = {pago} AND nota_venta_id IS NOT NULL AND anulado_en IS NULL AND activo
                """, ct);
            return n == 0 ? Results.NotFound(new { error = "No se encontró, o ya estaba anulado." })
                          : Results.Ok(new { mensaje = "Pago anulado. Lo que debe volvió a subir." });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Anular un pago de una nota fiada (no se borra)");
    }

    // =========================================================================
    private sealed class FilaNumero { public string OSerie { get; set; } = ""; public int ONumero { get; set; } }

    private static IResult Mal(string error) => Results.BadRequest(new { error });

    private static string Recortar(string s, int n) => s.Length > n ? s[..n] : s;

    private static DateOnly? Fecha(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static async Task<string?> TextoAsync(ContextoPlataforma db, FormattableString sql, CancellationToken ct) =>
        (await db.Database.SqlQuery<string>(sql).ToListAsync(ct)).FirstOrDefault();

    /// <summary>El estado de una nota, si es de la empresa abierta y de una sucursal en la que trabaja.</summary>
    private static async Task<FilaEstado?> EstadoAsync(ContextoPlataforma db, ContextoPeticion ctx, Guid id, CancellationToken ct)
    {
        var n = (await db.Database.SqlQuery<FilaEstado>($"""
            SELECT estado, forma, entregado_en, documento_id, documento, pagado, saldo, cliente_id, sucursal_id, numero
              FROM notas_venta_filas() WHERE id = {id}
            """).ToListAsync(ct)).FirstOrDefault();
        if (n is null) return null;
        var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
        return permitidas is not null && !permitidas.Contains(n.SucursalId) ? null : n;
    }
}