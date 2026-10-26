using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Reportes;

/// <summary>
/// e179 (060) — GANANCIAS (bloque 5), en tres reportes:
///
///   /api/reportes/valorizado   el stock de hoy por lo que costó (con inventario)
///   /api/reportes/margen       ventas − costo de lo vendido, por producto, cliente y mes
///   /api/reportes/resultados   ventas − costo − gastos, por mes (estado de resultados simple)
///
/// DE DÓNDE SALE EL COSTO de cada línea vendida:
///   · REAL: el costo promedio ponderado de las compras del inventario hasta el
///     día de la venta (solo ventas desde que se activó el inventario);
///   · «≈» MANUAL: el costo escrito en el producto (antes del inventario, o si
///     ese producto no tiene compras con costo);
///   · SIN COSTO: ni lo uno ni lo otro. Esas ventas salen aparte y no entran al
///     margen (para no inventar una ganancia del 100 %).
///
/// Las notas de crédito restan la venta y el costo (se asume devolución). Los
/// regalos tienen costo y venta cero: bajan el margen, que es lo que pasa.
/// Montos en soles y SIN IGV. Lo comprado en dólares pasa a soles con el
/// tipo de cambio SUNAT del día de la compra (tipos_cambio, 033).
/// </summary>
public static class EndpointsReportesGanancias
{
    public const string Permiso = "reportes.ganancias";

    // {0} desde · {1} hasta
    private const string Lineas = """
WITH amb AS (
    SELECT CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS a
      FROM empresas e WHERE e.id = ctx_empresa()
), cfg AS (
    SELECT (c.activado_en AT TIME ZONE 'America/Lima')::date AS desde
      FROM inventario_config c WHERE c.empresa_id = ctx_empresa() AND c.activado_en IS NOT NULL LIMIT 1
), v AS (
    SELECT d.id, d.fecha_emision, d.cliente_id,
           CASE WHEN d.tipo = '07' THEN -1 ELSE 1 END AS signo,
           CASE WHEN d.moneda = 'PEN' THEN 1 ELSE coalesce(d.tipo_cambio, 0) END AS tc
      FROM documentos d CROSS JOIN amb
     WHERE d.empresa_id = ctx_empresa() AND d.activo
       AND coalesce(d.ambiente, 'beta') = amb.a
       AND d.estado IN ('encolado', 'aceptado', 'observado')
       AND d.tipo IN ('01', '03', '07')
       AND d.fecha_emision BETWEEN {0} AND {1}
), l AS (
    SELECT v.*, dl.producto_id, coalesce(p.descripcion, dl.descripcion) AS producto, coalesce(p.codigo, dl.codigo) AS codigo,
           -- 061: en la unidad del producto (una caja de 12 son 12, al costo de la unidad).
           coalesce(p.unidad_medida, dl.unidad_medida) AS unidad, v.signo * dl.cantidad * coalesce(dl.factor, 1) AS cantidad,
           round(v.signo * dl.valor_venta * v.tc, 2) AS venta, p.costo AS costo_manual,
           (SELECT cfg.desde FROM cfg) AS inv_desde
      FROM v JOIN documento_lineas dl ON dl.documento_id = v.id AND dl.activo
      LEFT JOIN productos p ON p.id = dl.producto_id
), c AS (
    SELECT l.*, r.costo AS costo_real
      FROM l
      LEFT JOIN LATERAL (
          SELECT sum(x.cantidad * x.costo) / nullif(sum(x.cantidad), 0) AS costo
            FROM (SELECT m.cantidad,
                         -- e182: lo comprado en dólares, a soles con el tipo de cambio SUNAT del día.
                         m.costo_unitario * CASE WHEN coalesce(m.moneda, 'PEN') = 'PEN' THEN 1
                              ELSE (SELECT tc.venta FROM tipos_cambio tc
                                     WHERE tc.moneda = m.moneda AND tc.publicado
                                       AND tc.fecha <= (m.creado_en AT TIME ZONE 'America/Lima')::date
                                     ORDER BY tc.fecha DESC LIMIT 1) END AS costo
                    FROM inv_movimientos m
                   WHERE m.empresa_id = ctx_empresa() AND m.producto_id = l.producto_id
                     AND m.tipo IN ('compra', 'compra_anulada', 'stock_inicial')
                     AND m.costo_unitario IS NOT NULL
                     AND (m.creado_en AT TIME ZONE 'America/Lima')::date <= l.fecha_emision) x
           WHERE x.costo IS NOT NULL
      ) r ON l.producto_id IS NOT NULL AND l.inv_desde IS NOT NULL AND l.fecha_emision >= l.inv_desde
), k AS (
    SELECT c.*,
           CASE WHEN c.costo_real > 0 THEN 'real' WHEN c.costo_manual IS NOT NULL THEN 'manual' ELSE 'sin' END AS fuente,
           round(c.cantidad * coalesce(CASE WHEN c.costo_real > 0 THEN c.costo_real END, c.costo_manual), 2) AS costo
      FROM c
)
""";

    private const string SqlMargen = Lineas + """
SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'activadoEn', (SELECT desde FROM cfg),
    'ventas',        (SELECT coalesce(sum(venta), 0) FROM k WHERE fuente <> 'sin'),
    'costo',         (SELECT coalesce(sum(costo), 0) FROM k WHERE fuente <> 'sin'),
    'ventasReal',    (SELECT coalesce(sum(venta), 0) FROM k WHERE fuente = 'real'),
    'ventasManual',  (SELECT coalesce(sum(venta), 0) FROM k WHERE fuente = 'manual'),
    'ventasSinCosto',(SELECT coalesce(sum(venta), 0) FROM k WHERE fuente = 'sin'),
    'porProducto', (SELECT coalesce(json_agg(x ORDER BY x.venta DESC), '[]'::json) FROM (
        SELECT producto, codigo, unidad, sum(cantidad) AS cantidad, sum(venta) AS venta, sum(costo) AS costo,
               CASE WHEN bool_and(fuente = 'real') THEN 'real' WHEN bool_or(fuente = 'sin') THEN 'sin' ELSE 'manual' END AS fuente
          FROM k GROUP BY producto, codigo, unidad) x),
    'porCliente', (SELECT coalesce(json_agg(x ORDER BY x.venta DESC), '[]'::json) FROM (
        SELECT cl.razon_social AS cliente, cl.numero_documento AS documento,
               sum(k.venta) FILTER (WHERE k.fuente <> 'sin') AS venta, sum(k.costo) FILTER (WHERE k.fuente <> 'sin') AS costo,
               coalesce(sum(k.venta) FILTER (WHERE k.fuente = 'sin'), 0) AS "sinCosto"
          FROM k JOIN clientes cl ON cl.id = k.cliente_id GROUP BY cl.razon_social, cl.numero_documento) x),
    'porMes', (SELECT coalesce(json_agg(x ORDER BY x.mes), '[]'::json) FROM (
        SELECT to_char(fecha_emision, 'YYYY-MM') AS mes,
               coalesce(sum(venta) FILTER (WHERE fuente <> 'sin'), 0) AS venta, coalesce(sum(costo) FILTER (WHERE fuente <> 'sin'), 0) AS costo,
               coalesce(sum(venta) FILTER (WHERE fuente = 'sin'), 0) AS "sinCosto",
               coalesce(sum(venta) FILTER (WHERE fuente = 'manual'), 0) AS manual
          FROM k GROUP BY 1) x)
)::text AS "Value"
""";

    private const string SqlResultados = Lineas + """
, g AS (
    SELECT to_char(c.fecha_emision, 'YYYY-MM') AS mes, c.categoria,
           round(CASE WHEN c.comprobante_tipo = '01' THEN c.base + c.no_gravado ELSE c.total END
                 * CASE WHEN c.moneda = 'PEN' THEN 1 ELSE coalesce(c.tipo_cambio, 0) END, 2) AS monto
      FROM compras c
     WHERE c.empresa_id = ctx_empresa() AND c.activo AND c.anulada_en IS NULL AND c.clase = 'gasto'
       AND c.fecha_emision BETWEEN {0} AND {1}
), meses AS (
    SELECT to_char(fecha_emision, 'YYYY-MM') AS mes FROM k UNION SELECT mes FROM g
)
SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'activadoEn', (SELECT desde FROM cfg),
    'meses', (SELECT coalesce(json_agg(x ORDER BY x.mes), '[]'::json) FROM (
        SELECT m.mes,
               coalesce((SELECT sum(venta) FROM k WHERE to_char(k.fecha_emision, 'YYYY-MM') = m.mes), 0) AS ventas,
               coalesce((SELECT sum(costo) FROM k WHERE fuente <> 'sin' AND to_char(k.fecha_emision, 'YYYY-MM') = m.mes), 0) AS costo,
               coalesce((SELECT sum(venta) FROM k WHERE fuente = 'sin' AND to_char(k.fecha_emision, 'YYYY-MM') = m.mes), 0) AS "sinCosto",
               coalesce((SELECT sum(monto) FROM g WHERE g.mes = m.mes), 0) AS gastos
          FROM meses m) x),
    'gastos', (SELECT coalesce(json_agg(x ORDER BY x.monto DESC), '[]'::json) FROM (
        SELECT categoria, sum(monto) AS monto FROM g GROUP BY categoria) x)
)::text AS "Value"
""";

    private const string SqlValorizado = """
WITH soles AS (
    SELECT m.producto_id, m.cantidad,
           m.costo_unitario * CASE WHEN coalesce(m.moneda, 'PEN') = 'PEN' THEN 1
                ELSE (SELECT tc.venta FROM tipos_cambio tc
                       WHERE tc.moneda = m.moneda AND tc.publicado
                         AND tc.fecha <= (m.creado_en AT TIME ZONE 'America/Lima')::date
                       ORDER BY tc.fecha DESC LIMIT 1) END AS costo
      FROM inv_movimientos m
     WHERE m.empresa_id = ctx_empresa() AND m.tipo IN ('compra', 'compra_anulada', 'stock_inicial')
       AND m.costo_unitario IS NOT NULL
), costos AS (
    SELECT producto_id, sum(cantidad * costo) / nullif(sum(cantidad), 0) AS costo
      FROM soles WHERE costo IS NOT NULL GROUP BY producto_id
), s AS (
    SELECT p.id, p.codigo, p.descripcion AS producto, p.unidad_medida AS unidad, su.codigo || ' ' || su.nombre AS sucursal,
           st.fisico, co.costo AS costo_real, p.costo AS costo_manual
      FROM inv_stock st
      JOIN productos p   ON p.id = st.producto_id AND p.activo AND p.controla_stock
      JOIN sucursales su ON su.id = st.sucursal_id
      LEFT JOIN costos co ON co.producto_id = p.id
     WHERE st.empresa_id = ctx_empresa() AND st.fisico <> 0
)
SELECT json_build_object(
    'filas', (SELECT coalesce(json_agg(x ORDER BY x.valor DESC NULLS LAST), '[]'::json) FROM (
        SELECT id, codigo, producto, unidad, sucursal, fisico,
               round(coalesce(CASE WHEN costo_real > 0 THEN costo_real END, costo_manual), 4) AS costo,
               CASE WHEN costo_real > 0 THEN 'real' WHEN costo_manual IS NOT NULL THEN 'manual' ELSE 'sin' END AS fuente,
               round(fisico * coalesce(CASE WHEN costo_real > 0 THEN costo_real END, costo_manual), 2) AS valor
          FROM s) x)
)::text AS "Value"
""";

    public static void MapearReportesGanancias(this IEndpointRouteBuilder rutas)
    {
        rutas.MapGet("/api/reportes/margen", (string? desde, string? hasta, string? formato, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            ResponderAsync("margen", SqlMargen, desde, hasta, formato, db, ctx, ct))
            .RequierePermiso(Permiso).RequiereModulo(Modulos.Facturacion).WithTags("Reportes")
            .WithSummary("Margen bruto: ventas − costo de lo vendido, por producto, cliente y mes");

        rutas.MapGet("/api/reportes/resultados", (string? desde, string? hasta, string? formato, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            ResponderAsync("resultados", SqlResultados, desde, hasta, formato, db, ctx, ct))
            .RequierePermiso(Permiso).RequiereModulo(Modulos.Facturacion).WithTags("Reportes")
            .WithSummary("Estado de resultados simple: ventas − costo − gastos, por mes");

        rutas.MapGet("/api/reportes/valorizado", (string? formato, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            ResponderAsync("valorizado", SqlValorizado, null, null, formato, db, ctx, ct))
            .RequierePermiso(Permiso).RequiereModulo("inventario").WithTags("Reportes")
            .WithSummary("Inventario valorizado: el stock de hoy por lo que costó");
    }

    private static async Task<IResult> ResponderAsync(string reporte, string sql, string? desde, string? hasta, string? formato,
        ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        if (ctx.EmpresaId is null) return Results.BadRequest(new { error = "Abre una empresa para ver sus reportes." });
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        var d = new DateOnly(hoy.Year, hoy.Month, 1); var h = hoy;
        static bool F(string? t, out DateOnly f) => DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out f);
        if (!string.IsNullOrWhiteSpace(desde) && !F(desde, out d)) return Results.BadRequest(new { error = "La fecha «desde» no es válida." });
        if (!string.IsNullOrWhiteSpace(hasta) && !F(hasta, out h)) return Results.BadRequest(new { error = "La fecha «hasta» no es válida." });
        if (d > h) return Results.BadRequest(new { error = "La fecha «desde» es posterior a «hasta»." });
        if (h.DayNumber - d.DayNumber > 366) return Results.BadRequest(new { error = "Elige un periodo de un año como máximo." });

        var json = (await db.Database.SqlQuery<string>(FormattableStringFactory.Create(sql, d, h)).ToListAsync(ct)).FirstOrDefault();
        var datos = JsonNode.Parse(json ?? "{}")!.AsObject();
        datos["desde"] = d.ToString("yyyy-MM-dd"); datos["hasta"] = h.ToString("yyyy-MM-dd"); datos["moneda"] = "PEN";
        datos["ambiente"] ??= "produccion";

        if (!string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase))
            return Results.Content(datos.ToJsonString(), "application/json");

        var empresa = await db.Empresas.AsNoTracking().Where(e => e.Id == ctx.EmpresaId)
            .Select(e => new { e.Nombre, e.RazonSocial, e.Ruc }).FirstOrDefaultAsync(ct);
        var titulo = reporte switch { "margen" => "Margen bruto", "resultados" => "Estado de resultados (simple)", _ => "Inventario valorizado" };
        var titulos = new List<string> { titulo,
            $"{empresa?.RazonSocial ?? empresa?.Nombre}{(empresa?.Ruc is { } ruc ? " · RUC " + ruc : "")}",
            reporte == "valorizado" ? $"Al {hoy:dd/MM/yyyy}" : $"Del {d:dd/MM/yyyy} al {h:dd/MM/yyyy}. Montos en soles, sin IGV." };
        if ((string?)datos["ambiente"] == "beta") titulos.Add("MODO PRUEBA: ventas de prueba (SUNAT beta).");
        titulos.Add("Costo «≈»: el escrito a mano en el producto. Real: promedio de las compras del inventario.");

        return Results.File(ExcelSimple.Crear(Hojas(reporte, datos, titulos)), ExcelSimple.TipoMime,
            reporte == "valorizado" ? $"inventario-valorizado-{hoy:yyyy-MM-dd}.xlsx" : $"{reporte}-{d:yyyy-MM-dd}-a-{h:yyyy-MM-dd}.xlsx");
    }

    private static List<HojaExcel> Hojas(string reporte, JsonObject d, List<string> titulos)
    {
        HojaExcel Nueva(string nombre) { var h = new HojaExcel(nombre); h.Titulos.AddRange(titulos); return h; }
        static decimal D(JsonNode? n) => n is null ? 0m : n.GetValue<decimal>();
        static CeldaExcel M(decimal m) => new(m, TipoColumna.Monto);
        static string T(JsonNode? n) => n?.ToString() ?? "";
        static string Fuente(string f) => f == "real" ? "Real" : f == "manual" ? "≈ Manual" : "Sin costo";
        IEnumerable<JsonObject> F(string k) => (d[k] as JsonArray ?? []).OfType<JsonObject>();
        static string Pct(decimal venta, decimal costo) => venta == 0 ? "" : $"{(venta - costo) / venta * 100:0.0} %";

        if (reporte == "valorizado")
        {
            var v = Nueva("Inventario valorizado");
            v.Columnas.AddRange([new("Código", TipoColumna.Texto, 14), new("Producto", TipoColumna.Texto, 40), new("Sucursal", TipoColumna.Texto, 22),
                new("Stock", TipoColumna.Monto, 12), new("Unidad", TipoColumna.Texto, 8), new("Costo unit.", TipoColumna.Monto, 12),
                new("Costo", TipoColumna.Texto, 10), new("Valor", TipoColumna.Monto, 14)]);
            foreach (var f in F("filas"))
                v.Filas.Add([T(f["codigo"]), T(f["producto"]), T(f["sucursal"]), M(D(f["fisico"])), T(f["unidad"]), M(D(f["costo"])), Fuente(T(f["fuente"])), M(D(f["valor"]))]);
            return [v];
        }

        if (reporte == "resultados")
        {
            var r = Nueva("Estado de resultados");
            r.Columnas.AddRange([new("Mes", TipoColumna.Texto, 10), new("Ventas (sin IGV)", TipoColumna.Monto, 15), new("Costo de lo vendido", TipoColumna.Monto, 15),
                new("Utilidad bruta", TipoColumna.Monto, 15), new("Gastos", TipoColumna.Monto, 15), new("Resultado", TipoColumna.Monto, 15), new("Ventas sin costo", TipoColumna.Monto, 15)]);
            foreach (var f in F("meses"))
            {
                decimal ve = D(f["ventas"]), co = D(f["costo"]), ga = D(f["gastos"]);
                r.Filas.Add([T(f["mes"]), M(ve), M(co), M(ve - co), M(ga), M(ve - co - ga), M(D(f["sinCosto"]))]);
            }
            var g = Nueva("Gastos por categoría");
            g.Columnas.AddRange([new("Categoría", TipoColumna.Texto, 20), new("Monto", TipoColumna.Monto, 15)]);
            foreach (var f in F("gastos")) g.Filas.Add([T(f["categoria"]), M(D(f["monto"]))]);
            return [r, g];
        }

        var p = Nueva("Por producto");
        p.Columnas.AddRange([new("Código", TipoColumna.Texto, 14), new("Producto", TipoColumna.Texto, 40), new("Cantidad", TipoColumna.Monto, 12),
            new("Ventas", TipoColumna.Monto, 14), new("Costo", TipoColumna.Monto, 14), new("Margen", TipoColumna.Monto, 14), new("Margen %", TipoColumna.Texto, 10), new("Costo", TipoColumna.Texto, 10)]);
        foreach (var f in F("porProducto"))
        {
            decimal ve = D(f["venta"]), co = D(f["costo"]); var fu = T(f["fuente"]);
            p.Filas.Add([T(f["codigo"]), T(f["producto"]), M(D(f["cantidad"])), M(ve), fu == "sin" ? "" : (object)M(co), fu == "sin" ? "" : (object)M(ve - co), fu == "sin" ? "" : Pct(ve, co), Fuente(fu)]);
        }
        var c = Nueva("Por cliente");
        c.Columnas.AddRange([new("Cliente", TipoColumna.Texto, 36), new("RUC / DNI", TipoColumna.Texto, 14), new("Ventas con costo", TipoColumna.Monto, 15),
            new("Costo", TipoColumna.Monto, 14), new("Margen", TipoColumna.Monto, 14), new("Margen %", TipoColumna.Texto, 10), new("Ventas sin costo", TipoColumna.Monto, 15)]);
        foreach (var f in F("porCliente"))
        {
            decimal ve = D(f["venta"]), co = D(f["costo"]);
            c.Filas.Add([T(f["cliente"]), T(f["documento"]), M(ve), M(co), M(ve - co), Pct(ve, co), M(D(f["sinCosto"]))]);
        }
        var m = Nueva("Por mes");
        m.Columnas.AddRange([new("Mes", TipoColumna.Texto, 10), new("Ventas con costo", TipoColumna.Monto, 15), new("Costo", TipoColumna.Monto, 14),
            new("Margen", TipoColumna.Monto, 14), new("Margen %", TipoColumna.Texto, 10), new("Con costo ≈", TipoColumna.Monto, 14), new("Ventas sin costo", TipoColumna.Monto, 15)]);
        foreach (var f in F("porMes"))
        {
            decimal ve = D(f["venta"]), co = D(f["costo"]);
            m.Filas.Add([T(f["mes"]), M(ve), M(co), M(ve - co), Pct(ve, co), M(D(f["manual"])), M(D(f["sinCosto"]))]);
        }
        return [p, c, m];
    }
}