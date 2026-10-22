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
/// 032 — REPORTES, entrega 1: «Mi negocio» (maqueta aprobada el 18 oct 2026).
///
///   GET /api/reportes/mi-dia       Mi día
///   GET /api/reportes/ventas       Tablero de ventas
///   GET /api/reportes/clientes     Ventas por cliente
///   GET /api/reportes/productos    Ventas por producto
///   GET /api/reportes/vendedores   Ventas por vendedor (quién emitió)
///
/// Todos: ?desde=AAAA-MM-DD&amp;hasta=AAAA-MM-DD&amp;moneda=PEN|USD, y
/// &amp;formato=xlsx para bajarlo en Excel. Un permiso por reporte.
///
/// SOLO LECTURA. Las cuentas se hacen en la base, en una sola consulta por
/// reporte, y el aislamiento (RLS) deja ver solo la empresa abierta.
///
/// QUÉ CUENTA: facturas, boletas y notas de crédito EN CAMINO, ACEPTADAS u
/// OBSERVADAS, del SUNAT en el que está ahora la empresa: en real, solo lo
/// real; en modo prueba, solo lo de prueba (y la pantalla lo dice). Nunca se
/// mezclan. Las notas de crédito RESTAN. Rechazados, anulados, borradores y
/// los que no se pudieron enviar no cuentan.
/// </summary>
public static class EndpointsReportes
{
    private const string Base = """
WITH amb AS (
    SELECT CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS a
      FROM empresas e WHERE e.id = ctx_empresa()
), v AS (
    SELECT d.*, CASE WHEN d.tipo = '07' THEN -1 ELSE 1 END AS signo
      FROM documentos d CROSS JOIN amb
     WHERE d.empresa_id = ctx_empresa() AND d.activo
       AND coalesce(d.ambiente, 'beta') = amb.a
       AND d.estado IN ('encolado', 'aceptado', 'observado')
       AND d.tipo IN ('01', '03', '07', '08')
       AND d.moneda = {2}
       AND d.fecha_emision BETWEEN {0} AND {1}
)
""";

    private const string SqlTotal = """

SELECT coalesce(sum(signo * importe_total), 0) AS "Value" FROM v
""";
    private const string SqlTablero = """
,
k AS (
    SELECT coalesce(sum(signo * importe_total), 0)                    AS total,
           count(*) FILTER (WHERE tipo = '01')                        AS facturas,
           count(*) FILTER (WHERE tipo = '03')                        AS boletas,
           count(*) FILTER (WHERE tipo = '07')                        AS notas,
           coalesce(sum(importe_total) FILTER (WHERE tipo = '07'), 0) AS monto_notas,
           coalesce(sum(signo * total_gravado), 0)                    AS gravado,
           coalesce(sum(signo * (total_exonerado + total_inafecto)), 0) AS exonerado,
           coalesce(sum(signo * total_exportacion), 0)                AS exportacion,
           coalesce(sum(signo * total_igv), 0)                        AS igv
      FROM v
)
SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'kpis', (SELECT json_build_object(
                'total', total, 'facturas', facturas, 'boletas', boletas,
                'notas', notas, 'montoNotas', monto_notas,
                'promedio', CASE WHEN facturas + boletas > 0
                                 THEN round(total / (facturas + boletas), 2) ELSE 0 END) FROM k),
    'igv', (SELECT json_build_object(
                'gravado', gravado, 'exonerado', exonerado, 'exportacion', exportacion,
                'igv', igv, 'total', total) FROM k),
    'porDia', (SELECT coalesce(json_agg(json_build_object('fecha', g.dia::date, 'total', coalesce(t.total, 0))
                                        ORDER BY g.dia), '[]'::json)
                 FROM generate_series({0}::timestamp, {1}::timestamp, interval '1 day') AS g(dia)
                 LEFT JOIN (SELECT fecha_emision, sum(signo * importe_total) AS total FROM v GROUP BY 1) t
                        ON t.fecha_emision = g.dia::date),
    'porTipo', (SELECT coalesce(json_agg(json_build_object('tipo', tipo, 'cantidad', n, 'total', t) ORDER BY tipo), '[]'::json)
                  FROM (SELECT tipo, count(*) AS n, sum(importe_total) AS t FROM v GROUP BY tipo) x),
    'topClientes', (SELECT coalesce(json_agg(json_build_object(
                               'cliente', c.razon_social, 'documento', c.numero_documento, 'total', x.t)
                           ORDER BY x.t DESC), '[]'::json)
                      FROM (SELECT cliente_id, sum(signo * importe_total) AS t FROM v
                             GROUP BY cliente_id ORDER BY 2 DESC LIMIT 5) x
                      JOIN clientes c ON c.id = x.cliente_id)
)::text AS "Value"
""";
    private const string SqlClientes = """

SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'total', coalesce(sum(x.t), 0),
    'filas', coalesce(json_agg(json_build_object(
                 'cliente', c.razon_social, 'documento', c.numero_documento,
                 'comprobantes', x.n, 'base', x.base, 'igv', x.igv, 'total', x.t)
             ORDER BY x.t DESC), '[]'::json)
)::text AS "Value"
  FROM (SELECT cliente_id,
               count(*) FILTER (WHERE tipo <> '07') AS n,
               sum(signo * (total_gravado + total_exonerado + total_inafecto + total_exportacion)) AS base,
               sum(signo * total_igv) AS igv,
               sum(signo * importe_total) AS t
          FROM v GROUP BY cliente_id) x
  JOIN clientes c ON c.id = x.cliente_id
""";
    private const string SqlProductos = """
,
l AS (
    SELECT CASE WHEN v.tipo = '07' AND dl.unidad_medida = 'ZZ' AND dl.producto_id IS NULL
                THEN 'ajuste'
                ELSE coalesce(dl.producto_id::text, coalesce(dl.codigo, '') || '|' || upper(dl.descripcion)) END AS clave,
           CASE WHEN v.tipo = '07' AND dl.unidad_medida = 'ZZ' AND dl.producto_id IS NULL
                THEN 'Descuentos y ajustes (notas de crédito)' ELSE dl.descripcion END AS descripcion,
           dl.codigo, dl.unidad_medida,
           v.signo * dl.cantidad      AS cantidad,
           v.signo * dl.valor_venta   AS base,
           v.signo * dl.importe_total AS total
      FROM v JOIN documento_lineas dl ON dl.documento_id = v.id AND dl.activo
)
SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'total', coalesce(sum(x.total), 0),
    'filas', coalesce(json_agg(json_build_object(
                 'producto', x.descripcion, 'codigo', x.codigo, 'unidad', x.unidad,
                 'cantidad', CASE WHEN x.clave = 'ajuste' THEN NULL ELSE x.cantidad END,
                 'base', x.base, 'total', x.total)
             ORDER BY x.total DESC), '[]'::json)
)::text AS "Value"
  FROM (SELECT clave, max(descripcion) AS descripcion, max(codigo) AS codigo,
               max(unidad_medida) AS unidad, sum(cantidad) AS cantidad,
               sum(base) AS base, sum(total) AS total
          FROM l GROUP BY clave) x
""";
    private const string SqlVendedores = """

SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'total', coalesce(sum(x.t), 0),
    'filas', coalesce(json_agg(json_build_object(
                 'vendedor', coalesce(u.nombre, CASE WHEN x.creado_por IS NULL THEN 'Sistema'
                                                     ELSE 'Usuario de la cartera' END),
                 'comprobantes', x.n, 'notas', x.notas, 'total', x.t)
             ORDER BY x.t DESC), '[]'::json)
)::text AS "Value"
  FROM (SELECT creado_por,
               count(*) FILTER (WHERE tipo <> '07') AS n,
               count(*) FILTER (WHERE tipo = '07')  AS notas,
               sum(signo * importe_total) AS t
          FROM v GROUP BY creado_por) x
  LEFT JOIN usuarios u ON u.id = x.creado_por
""";
    private const string SqlMiDia = """

SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'total',    coalesce((SELECT sum(signo * importe_total) FROM v), 0),
    'facturas', (SELECT count(*) FROM v WHERE tipo = '01'),
    'boletas',  (SELECT count(*) FROM v WHERE tipo = '03'),
    'notas',    (SELECT count(*) FROM v WHERE tipo = '07'),
    'enCamino', (SELECT count(*) FROM v WHERE estado = 'encolado'),
    'problemas', (SELECT count(*) FROM documentos d
                   WHERE d.empresa_id = ctx_empresa() AND d.activo
                     AND d.estado IN ('rechazado', 'error')
                     AND d.fecha_emision >= {0}::date - 7),
    'porHora', (SELECT coalesce(json_agg(json_build_object('hora', h, 'total', t) ORDER BY h), '[]'::json)
                  FROM (SELECT extract(hour FROM creado_en AT TIME ZONE 'America/Lima')::int AS h,
                               sum(signo * importe_total) AS t
                          FROM v GROUP BY 1) x),
    'ultimos', (SELECT coalesce(json_agg(json_build_object(
                         'id', x.id, 'tipo', x.tipo, 'numero', x.serie || '-' || lpad(x.correlativo::text, 8, '0'),
                         'estado', x.estado, 'total', x.importe_total, 'cliente', x.razon_social,
                         'hora', to_char(x.creado_en AT TIME ZONE 'America/Lima', 'HH24:MI'))
                       ORDER BY x.creado_en DESC), '[]'::json)
                  FROM (SELECT v.id, v.tipo, v.serie, v.correlativo, v.estado, v.importe_total, v.creado_en, c.razon_social
                          FROM v JOIN clientes c ON c.id = v.cliente_id
                         ORDER BY v.creado_en DESC LIMIT 10) x)
)::text AS "Value"
""";

    private sealed record Periodo(DateOnly Desde, DateOnly Hasta, string Moneda)
    {
        public string Texto =>
            $"Del {Desde:dd/MM/yyyy} al {Hasta:dd/MM/yyyy} · {(Moneda == "USD" ? "Dólares" : "Soles")}";
    }

    public static void MapearReportes(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/reportes").WithTags("Reportes");

        grupo.MapGet("/mi-dia", (string? moneda, string? formato, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            Responder("mi-dia", SqlMiDia, HoyEnPeru().ToString("yyyy-MM-dd"), HoyEnPeru().ToString("yyyy-MM-dd"), moneda, formato, db, ctx, ct))
            .RequierePermiso(Permisos.Reportes.MiDia).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Mi día: lo vendido hoy");

        grupo.MapGet("/ventas", (string? desde, string? hasta, string? moneda, string? formato, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            Responder("ventas", SqlTablero, desde, hasta, moneda, formato, db, ctx, ct))
            .RequierePermiso(Permisos.Reportes.TableroVentas).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Tablero de ventas");

        grupo.MapGet("/clientes", (string? desde, string? hasta, string? moneda, string? formato, string? q, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            Responder("clientes", SqlClientes, desde, hasta, moneda, formato, db, ctx, ct, q))
            .RequierePermiso(Permisos.Reportes.VentasCliente).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Ventas por cliente");

        grupo.MapGet("/productos", (string? desde, string? hasta, string? moneda, string? formato, string? q, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            Responder("productos", SqlProductos, desde, hasta, moneda, formato, db, ctx, ct, q))
            .RequierePermiso(Permisos.Reportes.VentasProducto).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Ventas por producto");

        grupo.MapGet("/vendedores", (string? desde, string? hasta, string? moneda, string? formato, string? q, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            Responder("vendedores", SqlVendedores, desde, hasta, moneda, formato, db, ctx, ct, q))
            .RequierePermiso(Permisos.Reportes.VentasVendedor).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Ventas por vendedor (quién emitió)");
    }


    private static async Task<IResult> Responder(
        string reporte, string sql, string? desde, string? hasta, string? moneda, string? formato,
        ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct, string? q = null)
    {
        if (ctx.EmpresaId is null)
            return Results.BadRequest(new { error = "Abre una empresa para ver sus reportes." });

        if (LeerPeriodo(desde, hasta, moneda, out var p) is { } problema)
            return Results.BadRequest(new { error = problema });

        var json = await Consultar(db, Base + sql, p, ct);
        var datos = JsonNode.Parse(json ?? "{}")!.AsObject();

        datos["desde"] = p.Desde.ToString("yyyy-MM-dd");
        datos["hasta"] = p.Hasta.ToString("yyyy-MM-dd");
        datos["moneda"] = p.Moneda;

        // 037: la búsqueda, aquí: la pantalla y el Excel muestran lo mismo.
        FiltroReportes.Aplicar(datos, q, null, null, null);

        // El tablero se compara con el periodo anterior del mismo largo.
        if (reporte == "ventas")
        {
            var dias = p.Hasta.DayNumber - p.Desde.DayNumber;
            var antHasta = p.Desde.AddDays(-1);
            var anterior = new Periodo(antHasta.AddDays(-dias), antHasta, p.Moneda);
            var totales = await db.Database
                .SqlQuery<decimal>(Formato(Base + SqlTotal, anterior))
                .ToListAsync(ct);
            datos["anterior"] = new JsonObject
            {
                ["desde"] = anterior.Desde.ToString("yyyy-MM-dd"),
                ["hasta"] = anterior.Hasta.ToString("yyyy-MM-dd"),
                ["total"] = totales.FirstOrDefault()
            };
        }

        if (!string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase))
            return Results.Content(datos.ToJsonString(), "application/json");

        var empresa = await db.Empresas.AsNoTracking()
            .Where(e => e.Id == ctx.EmpresaId)
            .Select(e => new { e.Nombre, e.RazonSocial, e.Ruc })
            .FirstOrDefaultAsync(ct);

        var titulos = new List<string>
        {
            Nombre(reporte),
            $"{empresa?.RazonSocial ?? empresa?.Nombre}{(empresa?.Ruc is { } ruc ? " · RUC " + ruc : "")}",
            p.Texto
        };
        if (!string.IsNullOrWhiteSpace(q))
            titulos.Add($"Búsqueda: «{q.Trim()}»");
        if ((string?)datos["ambiente"] == "beta")
            titulos.Add("MODO PRUEBA: comprobantes de prueba (SUNAT beta), sin valor ante SUNAT.");

        var hojas = Hojas(reporte, datos, titulos);
        var archivo = ExcelSimple.Crear(hojas);
        var nombre = reporte == "mi-dia"
            ? $"reporte-mi-dia-{p.Desde:yyyy-MM-dd}.xlsx"
            : $"reporte-{reporte}-{p.Desde:yyyy-MM-dd}-a-{p.Hasta:yyyy-MM-dd}.xlsx";

        return Results.File(archivo, ExcelSimple.TipoMime, nombre);
    }


    private static async Task<string?> Consultar(ContextoPlataforma db, string sql, Periodo p, CancellationToken ct) =>
        (await db.Database.SqlQuery<string>(Formato(sql, p)).ToListAsync(ct)).FirstOrDefault();

    /// <summary>{0} desde, {1} hasta, {2} moneda: van como PARÁMETROS, nunca pegados al texto.</summary>
    private static FormattableString Formato(string sql, Periodo p) =>
        FormattableStringFactory.Create(sql, p.Desde, p.Hasta, p.Moneda);

    private static string? LeerPeriodo(string? desde, string? hasta, string? moneda, out Periodo p)
    {
        var hoy = HoyEnPeru();
        p = new Periodo(new DateOnly(hoy.Year, hoy.Month, 1), hoy, "PEN");

        static bool Fecha(string? texto, out DateOnly f) =>
            DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out f);

        var d = p.Desde; var h = p.Hasta;
        if (!string.IsNullOrWhiteSpace(desde) && !Fecha(desde, out d)) return "La fecha «desde» no es válida.";
        if (!string.IsNullOrWhiteSpace(hasta) && !Fecha(hasta, out h)) return "La fecha «hasta» no es válida.";
        if (d > h) return "La fecha «desde» es posterior a «hasta».";
        if (h.DayNumber - d.DayNumber > 366) return "Elige un periodo de un año como máximo.";

        var m = (moneda ?? "PEN").Trim().ToUpperInvariant();
        if (m is not ("PEN" or "USD")) return "La moneda es PEN o USD.";

        p = new Periodo(d, h, m);
        return null;
    }

    private static string Nombre(string reporte) => reporte switch
    {
        "mi-dia"     => "Mi día",
        "ventas"     => "Tablero de ventas",
        "clientes"   => "Ventas por cliente",
        "productos"  => "Ventas por producto",
        "vendedores" => "Ventas por vendedor",
        _            => "Reporte"
    };


    // =========================================================================
    // EL EXCEL DE CADA REPORTE
    // =========================================================================

    private static List<HojaExcel> Hojas(string reporte, JsonObject d, List<string> titulos)
    {
        HojaExcel Nueva(string nombre, bool tabla = true)
        {
            var h = new HojaExcel(nombre) { ConFiltro = tabla };
            h.Titulos.AddRange(titulos);
            return h;
        }

        static object Entero(JsonNode? n) => new CeldaExcel(Ent(n), TipoColumna.Entero);
        static object Monto(decimal m) => new CeldaExcel(m, TipoColumna.Monto);

        var hojas = new List<HojaExcel>();

        switch (reporte)
        {
            case "mi-dia":
            {
                var r = Nueva("Resumen del día", tabla: false);
                r.Columnas.Add(new("Concepto", TipoColumna.Texto, 36));
                r.Columnas.Add(new("Valor", TipoColumna.Texto, 18));
                r.Filas.Add([new FilaSeccion("Lo vendido hoy")]);
                r.Filas.Add(["Vendido hoy (con IGV)", Monto(Dec(d["total"]))]);
                r.Filas.Add(["Facturas", Entero(d["facturas"])]);
                r.Filas.Add(["Boletas", Entero(d["boletas"])]);
                r.Filas.Add(["Notas de crédito", Entero(d["notas"])]);
                r.Filas.Add([new FilaSeccion("Para revisar")]);
                r.Filas.Add(["En camino a SUNAT", Entero(d["enCamino"])]);
                r.Filas.Add(["Rechazados o sin enviar (últimos 7 días)", Entero(d["problemas"])]);
                hojas.Add(r);

                var u = Nueva("Últimos de hoy");
                u.Columnas.Add(new("Hora", TipoColumna.Texto, 8));
                u.Columnas.Add(new("Comprobante", TipoColumna.Texto, 18));
                u.Columnas.Add(new("Tipo", TipoColumna.Texto, 16));
                u.Columnas.Add(new("Cliente", TipoColumna.Texto, 40));
                u.Columnas.Add(new("Estado", TipoColumna.Texto, 16));
                u.Columnas.Add(new("Total", TipoColumna.Monto, 14));
                foreach (var f in Filas(d["ultimos"]))
                    u.Filas.Add([Txt(f["hora"]), Txt(f["numero"]), TipoTexto(Txt(f["tipo"])), Txt(f["cliente"]),
                                 EstadoTexto(Txt(f["estado"])), (Txt(f["tipo"]) == "07" ? -1 : 1) * Dec(f["total"])]);
                hojas.Add(u);
                break;
            }

            case "ventas":
            {
                var k = d["kpis"]; var igv = d["igv"];
                var total = Dec(k?["total"]); var anterior = Dec(d["anterior"]?["total"]);
                var r = Nueva("Resumen", tabla: false);
                r.Columnas.Add(new("Concepto", TipoColumna.Texto, 36));
                r.Columnas.Add(new("Valor", TipoColumna.Texto, 18));
                r.Filas.Add([new FilaSeccion("Ventas")]);
                r.Filas.Add(["Ventas del periodo (con IGV)", Monto(total)]);
                r.Filas.Add([$"Periodo anterior ({Fecha(d["anterior"]?["desde"])} – {Fecha(d["anterior"]?["hasta"])})", Monto(anterior)]);
                if (anterior != 0)
                    r.Filas.Add(["Variación", new CeldaExcel(Math.Round((total - anterior) / Math.Abs(anterior), 4), TipoColumna.Porcentaje)]);
                r.Filas.Add(["Facturas", Entero(k?["facturas"])]);
                r.Filas.Add(["Boletas", Entero(k?["boletas"])]);
                r.Filas.Add(["Venta promedio por comprobante", Monto(Dec(k?["promedio"]))]);
                r.Filas.Add(["Notas de crédito", Entero(k?["notas"])]);
                r.Filas.Add(["Monto de las notas de crédito", Monto(-Dec(k?["montoNotas"]))]);
                r.Filas.Add([new FilaSeccion("IGV del periodo (referencial)")]);
                r.Filas.Add(["Base gravada", Monto(Dec(igv?["gravado"]))]);
                r.Filas.Add(["Exonerado / inafecto", Monto(Dec(igv?["exonerado"]))]);
                r.Filas.Add(["Exportación", Monto(Dec(igv?["exportacion"]))]);
                r.Filas.Add(["IGV", Monto(Dec(igv?["igv"]))]);
                r.Filas.Add(["Total", Monto(Dec(igv?["total"]))]);
                hojas.Add(r);

                var dia = Nueva("Ventas por día");
                dia.FilaTotal = true;
                dia.Columnas.Add(new("Fecha", TipoColumna.Fecha, 12));
                dia.Columnas.Add(new("Vendido", TipoColumna.Monto, 16));
                foreach (var f in Filas(d["porDia"])) dia.Filas.Add([Txt(f["fecha"]), Dec(f["total"])]);
                hojas.Add(dia);

                var t = Nueva("Por tipo");
                t.FilaTotal = true;
                t.Columnas.Add(new("Tipo de comprobante", TipoColumna.Texto, 22));
                t.Columnas.Add(new("Cantidad", TipoColumna.Entero, 12));
                t.Columnas.Add(new("Total", TipoColumna.Monto, 16));
                foreach (var f in Filas(d["porTipo"]))
                    t.Filas.Add([TipoTexto(Txt(f["tipo"])), Ent(f["cantidad"]), (Txt(f["tipo"]) == "07" ? -1 : 1) * Dec(f["total"])]);
                hojas.Add(t);

                var c = Nueva("Mejores clientes");
                c.Columnas.Add(new("Cliente", TipoColumna.Texto, 42));
                c.Columnas.Add(new("RUC / DNI", TipoColumna.Texto, 14));
                c.Columnas.Add(new("Total", TipoColumna.Monto, 16));
                foreach (var f in Filas(d["topClientes"])) c.Filas.Add([Txt(f["cliente"]), Txt(f["documento"]), Dec(f["total"])]);
                hojas.Add(c);
                break;
            }

            case "clientes":
            {
                var h = Nueva("Ventas por cliente");
                h.FilaTotal = true;
                var suma = Dec(d["total"]);
                h.Columnas.Add(new("Cliente", TipoColumna.Texto, 42));
                h.Columnas.Add(new("RUC / DNI", TipoColumna.Texto, 14));
                h.Columnas.Add(new("Comprobantes", TipoColumna.Entero, 13));
                h.Columnas.Add(new("Base", TipoColumna.Monto, 15));
                h.Columnas.Add(new("IGV", TipoColumna.Monto, 13));
                h.Columnas.Add(new("Total", TipoColumna.Monto, 15));
                h.Columnas.Add(new("% del total", TipoColumna.Porcentaje, 11));
                foreach (var f in Filas(d["filas"]))
                    h.Filas.Add([Txt(f["cliente"]), Txt(f["documento"]), Ent(f["comprobantes"]),
                                 Dec(f["base"]), Dec(f["igv"]), Dec(f["total"]),
                                 suma == 0 ? 0m : Math.Round(Dec(f["total"]) / suma, 4)]);
                hojas.Add(h);
                break;
            }

            case "productos":
            {
                var h = Nueva("Ventas por producto");
                h.FilaTotal = true;
                h.Columnas.Add(new("Producto", TipoColumna.Texto, 42));
                h.Columnas.Add(new("Código", TipoColumna.Texto, 12));
                h.Columnas.Add(new("Unidad", TipoColumna.Texto, 8));
                h.Columnas.Add(new("Cantidad", TipoColumna.Cantidad, 12));
                h.Columnas.Add(new("Base", TipoColumna.Monto, 15));
                h.Columnas.Add(new("Total", TipoColumna.Monto, 15));
                foreach (var f in Filas(d["filas"]))
                    h.Filas.Add([Txt(f["producto"]), Txt(f["codigo"]), Txt(f["unidad"]),
                                 f["cantidad"] is null ? null : Dec(f["cantidad"]), Dec(f["base"]), Dec(f["total"])]);
                hojas.Add(h);
                break;
            }

            default:
            {
                var h = Nueva("Ventas por vendedor");
                h.FilaTotal = true;
                h.Columnas.Add(new("Quién emitió", TipoColumna.Texto, 34));
                h.Columnas.Add(new("Comprobantes", TipoColumna.Entero, 13));
                h.Columnas.Add(new("Notas de crédito", TipoColumna.Entero, 15));
                h.Columnas.Add(new("Total", TipoColumna.Monto, 15));
                foreach (var f in Filas(d["filas"]))
                    h.Filas.Add([Txt(f["vendedor"]), Ent(f["comprobantes"]), Ent(f["notas"]), Dec(f["total"])]);
                hojas.Add(h);
                break;
            }
        }

        return hojas;
    }

    private static string TipoTexto(string? t) => t switch
    {
        "01" => "Factura", "03" => "Boleta", "07" => "Nota de crédito", "08" => "Nota de débito", _ => t ?? ""
    };

    private static string EstadoTexto(string? e) => e switch
    {
        "aceptado" => "Aceptado", "observado" => "Con observaciones", "encolado" => "En camino",
        "anulado" => "Anulado", "rechazado" => "Rechazado", "error" => "No se pudo enviar", _ => e ?? ""
    };

    private static string? Fecha(JsonNode? n) =>
        Txt(n) is { Length: >= 10 } s ? $"{s.Substring(8, 2)}/{s.Substring(5, 2)}/{s[..4]}" : null;

    private static IEnumerable<JsonNode> Filas(JsonNode? n) =>
        n is JsonArray a ? a.Where(x => x is not null).Select(x => x!) : [];

    private static decimal Dec(JsonNode? n) => n is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : 0m;
    private static int Ent(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    private static string? Txt(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    private static DateOnly HoyEnPeru()
    {
        try
        {
            var lima = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, lima));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        }
    }
}