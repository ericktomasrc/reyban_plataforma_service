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
/// e178 — LOS REPORTES DEL PUNTO DE VENTA (bloque 4), en una sola consulta:
///
///   cajeros       ventas, total y ticket promedio por persona y caja
///   medios        efectivo, Yape/Plin, tarjeta, transferencia (y por día)
///   horas         las horas pico (para saber cuándo poner más gente)
///   diferencias   faltantes y sobrantes de cada cierre, y por cajero
///   movimientos   retiros, gastos e ingresos por motivo
///   productos     lo más vendido en el mostrador
///
/// Cuentan las ventas cuyo comprobante está en camino o aceptado (igual que el
/// arqueo): una que no llegó a SUNAT o fue rechazada no se vendió. Los días son
/// de Lima. Pide «Ver las cajas y arqueos de todos» y el módulo de POS.
/// </summary>
public static class EndpointsReportesPos
{
    public const string PermisoCajas = "pos.cajas_ver";

    private const string Sql = """
WITH amb AS (
    SELECT CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS a
      FROM empresas e WHERE e.id = ctx_empresa()
), v AS (
    SELECT pv.id, pv.sesion_id, pv.documento_id, pv.total, pv.creado_en,
           (pv.creado_en AT TIME ZONE 'America/Lima') AS lima,
           s.usuario_id, s.caja_id, s.sucursal_id
      FROM pos_ventas pv
      JOIN pos_sesiones s ON s.id = pv.sesion_id
      JOIN documentos d   ON d.id = pv.documento_id
      CROSS JOIN amb
     WHERE pv.empresa_id = ctx_empresa()
       AND d.estado IN ('encolado', 'aceptado', 'observado')
       AND coalesce(d.ambiente, 'beta') = amb.a
       AND (pv.creado_en AT TIME ZONE 'America/Lima')::date BETWEEN {0} AND {1}
), nombres AS (
    SELECT u.id, coalesce(nullif(u.nombre, ''), u.correo) AS nombre FROM usuarios u
), cajas AS (
    SELECT c.id, c.nombre || ' · ' || coalesce(su.codigo || ' ' || su.nombre, '') AS nombre
      FROM pos_cajas c LEFT JOIN sucursales su ON su.id = c.sucursal_id
)
SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'ventas', (SELECT count(*) FROM v),
    'total',  (SELECT coalesce(sum(total), 0) FROM v),
    'cajeros', (SELECT coalesce(json_agg(x ORDER BY x.total DESC), '[]'::json) FROM (
        SELECT n.nombre AS cajero, c.nombre AS caja, count(*)::int AS ventas, sum(v.total) AS total,
               round(sum(v.total) / count(*), 2) AS promedio, count(DISTINCT v.sesion_id)::int AS turnos
          FROM v JOIN nombres n ON n.id = v.usuario_id JOIN cajas c ON c.id = v.caja_id
         GROUP BY n.nombre, c.nombre) x),
    'medios', (SELECT coalesce(json_agg(x ORDER BY x.monto DESC), '[]'::json) FROM (
        SELECT p.medio, count(DISTINCT p.venta_id)::int AS ventas, sum(p.monto) AS monto
          FROM pos_pagos p JOIN v ON v.id = p.venta_id GROUP BY p.medio) x),
    'mediosPorDia', (SELECT coalesce(json_agg(x ORDER BY x.fecha, x.medio), '[]'::json) FROM (
        SELECT v.lima::date AS fecha, p.medio, sum(p.monto) AS monto
          FROM pos_pagos p JOIN v ON v.id = p.venta_id GROUP BY 1, 2) x),
    'horas', (SELECT coalesce(json_agg(x ORDER BY x.hora), '[]'::json) FROM (
        SELECT extract(hour FROM v.lima)::int AS hora, count(*)::int AS ventas, sum(v.total) AS total
          FROM v GROUP BY 1) x),
    'diferencias', (SELECT coalesce(json_agg(x ORDER BY x.cerrada DESC), '[]'::json) FROM (
        SELECT s.id, s.cerrada_en AS cerrada, n.nombre AS cajero, c.nombre AS caja,
               s.efectivo_esperado AS esperado, s.efectivo_contado AS contado, s.diferencia, s.observacion_cierre AS observacion
          FROM pos_sesiones s JOIN nombres n ON n.id = s.usuario_id JOIN cajas c ON c.id = s.caja_id
         WHERE s.empresa_id = ctx_empresa() AND s.cerrada_en IS NOT NULL
           AND (s.cerrada_en AT TIME ZONE 'America/Lima')::date BETWEEN {0} AND {1}) x),
    'movimientos', (SELECT coalesce(json_agg(x ORDER BY x.tipo DESC, x.monto DESC), '[]'::json) FROM (
        SELECT m.tipo, m.motivo, count(*)::int AS veces, sum(m.monto) AS monto
          FROM pos_movimientos m JOIN pos_sesiones s ON s.id = m.sesion_id
         WHERE m.empresa_id = ctx_empresa()
           AND (m.creado_en AT TIME ZONE 'America/Lima')::date BETWEEN {0} AND {1}
         GROUP BY m.tipo, m.motivo) x),
    'productos', (SELECT coalesce(json_agg(x ORDER BY x.total DESC), '[]'::json) FROM (
        SELECT coalesce(l.codigo, '') AS codigo, l.descripcion AS producto, l.unidad_medida AS unidad,
               sum(l.cantidad) AS cantidad, sum(l.importe_total) AS total, count(DISTINCT v.id)::int AS ventas
          FROM v JOIN documento_lineas l ON l.documento_id = v.documento_id
         GROUP BY 1, 2, 3
         ORDER BY sum(l.importe_total) DESC LIMIT 200) x)
)::text AS "Value"
""";

    public static void MapearReportesPos(this IEndpointRouteBuilder rutas)
    {
        rutas.MapGet("/api/reportes/pos", async (string? desde, string? hasta, string? formato,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is null) return Results.BadRequest(new { error = "Abre una empresa para ver sus reportes." });

            var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
            var d = new DateOnly(hoy.Year, hoy.Month, 1); var h = hoy;
            static bool Fecha(string? t, out DateOnly f) =>
                DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out f);
            if (!string.IsNullOrWhiteSpace(desde) && !Fecha(desde, out d)) return Results.BadRequest(new { error = "La fecha «desde» no es válida." });
            if (!string.IsNullOrWhiteSpace(hasta) && !Fecha(hasta, out h)) return Results.BadRequest(new { error = "La fecha «hasta» no es válida." });
            if (d > h) return Results.BadRequest(new { error = "La fecha «desde» es posterior a «hasta»." });
            if (h.DayNumber - d.DayNumber > 366) return Results.BadRequest(new { error = "Elige un periodo de un año como máximo." });

            var json = (await db.Database.SqlQuery<string>(FormattableStringFactory.Create(Sql, d, h)).ToListAsync(ct)).FirstOrDefault();
            var datos = JsonNode.Parse(json ?? "{}")!.AsObject();
            datos["desde"] = d.ToString("yyyy-MM-dd");
            datos["hasta"] = h.ToString("yyyy-MM-dd");
            datos["moneda"] = "PEN";

            if (!string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase))
                return Results.Content(datos.ToJsonString(), "application/json");

            var empresa = await db.Empresas.AsNoTracking().Where(e => e.Id == ctx.EmpresaId)
                .Select(e => new { e.Nombre, e.RazonSocial, e.Ruc }).FirstOrDefaultAsync(ct);
            var titulos = new List<string>
            {
                "Punto de venta",
                $"{empresa?.RazonSocial ?? empresa?.Nombre}{(empresa?.Ruc is { } ruc ? " · RUC " + ruc : "")}",
                $"Del {d:dd/MM/yyyy} al {h:dd/MM/yyyy}"
            };
            if ((string?)datos["ambiente"] == "beta") titulos.Add("MODO PRUEBA: comprobantes de prueba (SUNAT beta), sin valor ante SUNAT.");

            return Results.File(ExcelSimple.Crear(Hojas(datos, titulos)), ExcelSimple.TipoMime,
                                $"reporte-punto-de-venta-{d:yyyy-MM-dd}-a-{h:yyyy-MM-dd}.xlsx");
        })
        .RequierePermiso(PermisoCajas)
        .RequiereModulo(Modulos.Pos)
        .WithTags("Reportes")
        .WithSummary("Reportes del punto de venta: cajeros, medios de pago, horas, diferencias, retiros y lo más vendido");
    }

    private static List<HojaExcel> Hojas(JsonObject d, List<string> titulos)
    {
        HojaExcel Nueva(string nombre) { var h = new HojaExcel(nombre); h.Titulos.AddRange(titulos); return h; }
        static CeldaExcel M(JsonNode? n) => new(n is null ? 0m : n.GetValue<decimal>(), TipoColumna.Monto);
        static CeldaExcel E(JsonNode? n) => new(n is null ? 0 : n.GetValue<int>(), TipoColumna.Entero);
        static string T(JsonNode? n) => n?.ToString() ?? "";
        IEnumerable<JsonObject> F(string k) => (d[k] as JsonArray ?? []).OfType<JsonObject>();

        var cajeros = Nueva("Cajeros");
        cajeros.Columnas.AddRange([new("Cajero", TipoColumna.Texto, 28), new("Caja", TipoColumna.Texto, 28), new("Ventas", TipoColumna.Entero, 10),
                                   new("Total", TipoColumna.Monto, 14), new("Ticket promedio", TipoColumna.Monto, 14), new("Turnos", TipoColumna.Entero, 10)]);
        foreach (var f in F("cajeros")) cajeros.Filas.Add([T(f["cajero"]), T(f["caja"]), E(f["ventas"]), M(f["total"]), M(f["promedio"]), E(f["turnos"])]);

        var medios = Nueva("Medios de pago");
        medios.Columnas.AddRange([new("Medio", TipoColumna.Texto, 20), new("Ventas", TipoColumna.Entero, 10), new("Monto", TipoColumna.Monto, 14)]);
        foreach (var f in F("medios")) medios.Filas.Add([Medio(T(f["medio"])), E(f["ventas"]), M(f["monto"])]);

        var horas = Nueva("Por hora");
        horas.Columnas.AddRange([new("Hora", TipoColumna.Texto, 10), new("Ventas", TipoColumna.Entero, 10), new("Total", TipoColumna.Monto, 14)]);
        foreach (var f in F("horas")) horas.Filas.Add([$"{f["hora"]!.GetValue<int>():00}:00", E(f["ventas"]), M(f["total"])]);

        var dif = Nueva("Diferencias de caja");
        dif.Columnas.AddRange([new("Cierre", TipoColumna.Texto, 18), new("Cajero", TipoColumna.Texto, 24), new("Caja", TipoColumna.Texto, 24),
                               new("Esperado", TipoColumna.Monto, 14), new("Contado", TipoColumna.Monto, 14), new("Diferencia", TipoColumna.Monto, 14),
                               new("Observación", TipoColumna.Texto, 36)]);
        foreach (var f in F("diferencias"))
            dif.Filas.Add([DateTime.Parse(T(f["cerrada"]), CultureInfo.InvariantCulture).ToUniversalTime().AddHours(-5).ToString("dd/MM/yyyy HH:mm"),
                           T(f["cajero"]), T(f["caja"]), M(f["esperado"]), M(f["contado"]), M(f["diferencia"]), T(f["observacion"])]);

        var mov = Nueva("Retiros y gastos");
        mov.Columnas.AddRange([new("Tipo", TipoColumna.Texto, 12), new("Motivo", TipoColumna.Texto, 36), new("Veces", TipoColumna.Entero, 10), new("Monto", TipoColumna.Monto, 14)]);
        foreach (var f in F("movimientos")) mov.Filas.Add([T(f["tipo"]) == "retiro" ? "Retiro" : "Ingreso", T(f["motivo"]), E(f["veces"]), M(f["monto"])]);

        var prod = Nueva("Lo más vendido");
        prod.Columnas.AddRange([new("Código", TipoColumna.Texto, 14), new("Producto", TipoColumna.Texto, 40), new("Unidad", TipoColumna.Texto, 8),
                                new("Cantidad", TipoColumna.Monto, 12), new("Total", TipoColumna.Monto, 14), new("Ventas", TipoColumna.Entero, 10)]);
        foreach (var f in F("productos")) prod.Filas.Add([T(f["codigo"]), T(f["producto"]), T(f["unidad"]), M(f["cantidad"]), M(f["total"]), E(f["ventas"])]);

        return [cajeros, medios, horas, dif, mov, prod];
    }

    private static string Medio(string m) => m switch
    {
        "efectivo" => "Efectivo", "yape" => "Yape / Plin", "tarjeta" => "Tarjeta", "transferencia" => "Transferencia", _ => m
    };
}