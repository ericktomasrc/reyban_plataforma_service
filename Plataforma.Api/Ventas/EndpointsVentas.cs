using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Reportes;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Ventas;

/// <summary>
/// e274 (091) — VENTAS Y PAGOS Y COMPROBANTES. Solo lectura.
///
///   La plataforma (super admin, permiso «ver planes»):
///     GET /api/admin/ventas/resumen?pruebas=          la franja de arriba
///     GET /api/admin/ventas?estado&amp;q&amp;desde&amp;hasta&amp;plan&amp;medio&amp;pruebas&amp;pagina&amp;porPagina&amp;formato=xlsx
///     GET /api/admin/ventas/reportes?meses&amp;pruebas&amp;formato=xlsx
///
///   La cuenta (dueño o administrador):
///     GET /api/cobros/mis-pagos?anio=&amp;formato=xlsx   lo que le pagó a Reyban y sus gastos por mes
///     GET /api/cobros/{id}/comprobante/{pdf|xml}       la boleta o factura de un pago
///
/// Por defecto las ventas son solo dinero de verdad (Culqi real y pagos a mano);
/// pruebas=true suma los de prueba (para QA).
/// </summary>
public static class EndpointsVentas
{
    private static readonly CultureInfo Pe = CultureInfo.InvariantCulture;

    public static void MapearVentas(this IEndpointRouteBuilder rutas)
    {
        var admin = rutas.MapGroup("/api/admin/ventas").WithTags("Ventas · plataforma");

        admin.MapGet("/resumen", async (bool? pruebas, ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>(
                $"SELECT ventas_resumen({pruebas ?? false})::text AS \"Value\"").FirstAsync(ct), "application/json"))
            .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
            .WithSummary("Cobrado hoy y en el mes, ingreso fijo, por cobrar esta semana, atrasados");

        admin.MapGet("", async (string? estado, string? q, DateOnly? desde, DateOnly? hasta, string? plan, string? medio,
                bool? pruebas, int? pagina, int? porPagina, string? formato, ContextoPlataforma db, CancellationToken ct) =>
        {
            var excel = string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase);
            int pg = excel ? 1 : Math.Max(pagina ?? 1, 1), pp = excel ? 5000 : Math.Clamp(porPagina ?? 20, 1, 100);
            var json = await db.Database.SqlQuery<string>($"""
                SELECT ventas_lista({Limpio(estado)}::text, {q}::text, {desde}::date, {hasta}::date, {Limpio(plan)}::text,
                                    {Limpio(medio)}::text, {pruebas ?? false}, {pg}, {pp})::text AS "Value"
                """).FirstAsync(ct);
            if (!excel) return Results.Content(json, "application/json");

            var hoja = new HojaExcel("Ventas") { Horizontal = true, FilaTotal = true };
            hoja.Titulos.AddRange(["Ventas de planes", Periodo(desde, hasta)]);
            hoja.Columnas.AddRange([new("Fecha", TipoColumna.Texto, 18), new("N.°", TipoColumna.Numero, 8),
                new("Cliente", TipoColumna.Texto, 30), new("Empresa", TipoColumna.Texto, 30), new("Plan", TipoColumna.Texto, 12),
                new("Periodo", TipoColumna.Texto, 10), new("Medio", TipoColumna.Texto, 22), new("Monto", TipoColumna.Monto, 12),
                new("Estado", TipoColumna.Texto, 12), new("Comprobante", TipoColumna.Texto, 16), new("ID Culqi / operación", TipoColumna.Texto, 28)]);
            foreach (var f in Filas(json))
                hoja.Filas.Add([FechaHora(f["fecha"]), new CeldaExcel(f["numero"]?.GetValue<long>(), TipoColumna.Numero), T(f["cuenta"]),
                    T(f["empresa"]), T(f["plan"]), Periodo(T(f["periodo"])), Medio(f), new CeldaExcel(Dec(f["monto"]), TipoColumna.Monto),
                    Estado(T(f["estado"])), T(f["comprobanteNumero"]), T(f["referencia"])]);
            return Results.File(ExcelSimple.Crear([hoja]), ExcelSimple.TipoMime, $"ventas-{Hoy():yyyy-MM-dd}.xlsx");
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
        .WithSummary("Las ventas de planes, como Culqi: filtros, páginas y Excel");

        admin.MapGet("/reportes", async (int? meses, bool? pruebas, string? formato, ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database.SqlQuery<string>(
                $"SELECT ventas_reportes({meses ?? 6}, {pruebas ?? false})::text AS \"Value\"").FirstAsync(ct);
            if (!string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase)) return Results.Content(json, "application/json");

            var d = JsonNode.Parse(json)!.AsObject();
            var porMes = new HojaExcel("Por mes") { FilaTotal = true };
            porMes.Titulos.AddRange(["Cobrado por mes", "Con IGV."]);
            porMes.Columnas.AddRange([new("Mes", TipoColumna.Texto, 14), new("Pagos", TipoColumna.Entero, 10), new("Cobrado", TipoColumna.Monto, 14)]);
            foreach (var m in (d["meses"] as JsonArray ?? []).OfType<JsonObject>())
                porMes.Filas.Add([T(m["mes"]), new CeldaExcel(m["pagos"]?.GetValue<int>() ?? 0, TipoColumna.Entero), new CeldaExcel(Dec(m["cobrado"]), TipoColumna.Monto)]);
            var porPlan = new HojaExcel("Por plan") { FilaTotal = true };
            porPlan.Titulos.AddRange(["Por plan", "Este mes."]);
            porPlan.Columnas.AddRange([new("Plan", TipoColumna.Texto, 16), new("Clientes", TipoColumna.Entero, 10), new("Cobrado", TipoColumna.Monto, 14)]);
            foreach (var p in (d["planes"] as JsonArray ?? []).OfType<JsonObject>())
                porPlan.Filas.Add([T(p["plan"]), new CeldaExcel(p["clientes"]?.GetValue<int>() ?? 0, TipoColumna.Entero), new CeldaExcel(Dec(p["cobrado"]), TipoColumna.Monto)]);
            var atr = new HojaExcel("Atrasados");
            atr.Titulos.AddRange(["Atrasados y en reintento", $"Al {Hoy():dd/MM/yyyy}."]);
            atr.Columnas.AddRange([new("Cliente", TipoColumna.Texto, 30), new("Plan", TipoColumna.Texto, 12), new("Estado", TipoColumna.Texto, 14),
                new("Tarjeta", TipoColumna.Texto, 22), new("Debe", TipoColumna.Monto, 12), new("Detalle", TipoColumna.Texto, 40)]);
            foreach (var a in (d["atrasados"] as JsonArray ?? []).OfType<JsonObject>())
                atr.Filas.Add([T(a["cuenta"]), T(a["plan"]), EstadoCuenta(T(a["estado"])), T(a["tarjeta"]), new CeldaExcel(Dec(a["debe"]), TipoColumna.Monto), T(a["error"])]);
            return Results.File(ExcelSimple.Crear([porMes, porPlan, atr]), ExcelSimple.TipoMime, $"ventas-reportes-{Hoy():yyyy-MM-dd}.xlsx");
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
        .WithSummary("Ventas por mes, por plan y los atrasados");

        // ---------------------------------------------------------------------
        // LA CUENTA
        // ---------------------------------------------------------------------
        rutas.MapGet("/api/cobros/mis-pagos", async (int? anio, string? formato, ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database.SqlQuery<string>($"SELECT cobros_mis_pagos({anio})::text AS \"Value\"").FirstAsync(ct);
            if (!string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase)) return Results.Content(json, "application/json");

            var d = JsonNode.Parse(json)!.AsObject();
            var a = d["anio"]?.GetValue<int>() ?? Hoy().Year;
            var pagos = new HojaExcel("Pagos") { Horizontal = true };
            pagos.Titulos.AddRange([$"Pagos a Reyban {a}", "Con IGV."]);
            pagos.Columnas.AddRange([new("Fecha", TipoColumna.Texto, 18), new("Concepto", TipoColumna.Texto, 44), new("Medio", TipoColumna.Texto, 22),
                new("Monto", TipoColumna.Monto, 12), new("Estado", TipoColumna.Texto, 12), new("Comprobante", TipoColumna.Texto, 16)]);
            foreach (var f in (d["filas"] as JsonArray ?? []).OfType<JsonObject>())
                pagos.Filas.Add([FechaHora(f["fecha"]), T(f["concepto"]), Medio(f), new CeldaExcel(Dec(f["monto"]), TipoColumna.Monto),
                    Estado(T(f["estado"])), T(f["comprobanteNumero"])]);
            var gastos = new HojaExcel("Gastos por mes") { FilaTotal = true };
            gastos.Titulos.AddRange([$"Gastos con Reyban {a}", "Base, IGV y total de lo pagado."]);
            gastos.Columnas.AddRange([new("Mes", TipoColumna.Texto, 16), new("Pagos", TipoColumna.Entero, 8),
                new("Base", TipoColumna.Monto, 12), new("IGV", TipoColumna.Monto, 12), new("Total", TipoColumna.Monto, 12)]);
            foreach (var m in (d["porMes"] as JsonArray ?? []).OfType<JsonObject>().Reverse())
            {
                var total = Dec(m["total"]);
                var baseImp = Math.Round(total / 1.18m, 2);
                gastos.Filas.Add([NombreMes(m["mes"]?.GetValue<int>() ?? 1, a), new CeldaExcel(m["pagos"]?.GetValue<int>() ?? 0, TipoColumna.Entero),
                    new CeldaExcel(baseImp, TipoColumna.Monto), new CeldaExcel(total - baseImp, TipoColumna.Monto), new CeldaExcel(total, TipoColumna.Monto)]);
            }
            return Results.File(ExcelSimple.Crear([gastos, pagos]), ExcelSimple.TipoMime, $"pagos-reyban-{a}.xlsx");
        })
        .RequiereSesion()
        .WithTags("Cobros")
        .WithSummary("Pagos y comprobantes: lo que la cuenta le pagó a Reyban y sus gastos por mes");

        // El comprobante lo emitió «Mi empresa» (la de Reyban). Se comprueba que
        // el pago es de tu cuenta y solo entonces se pide el archivo con el
        // contexto de esa empresa (su credencial solo se lee así), como hace el
        // emisor automático.
        rutas.MapGet("/api/cobros/{id:guid}/comprobante/{archivo}", async (Guid id, string archivo, HttpContext http,
                ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var tipoArchivo = archivo.ToLowerInvariant() switch
            {
                "pdf" => ArchivoComprobante.Pdf,
                "xml" => ArchivoComprobante.Xml,
                _     => (ArchivoComprobante?)null
            };
            if (tipoArchivo is null) return Results.NotFound();

            var c = await db.Database.SqlQuery<ComprobanteCobro>($"""
                SELECT empresa AS "Empresa", documento AS "Documento", tipo AS "Tipo", numero AS "Numero"
                  FROM cobros_comprobante_mio({id})
                """).FirstOrDefaultAsync(ct);
            if (c is null) return Results.NotFound(new { error = "Ese comprobante no está disponible todavía." });
            if (tipoArchivo == ArchivoComprobante.Xml && c.Tipo == "03")
                return Results.NotFound(new { error = "Una boleta no tiene XML propio (viaja en el resumen diario). Baja su PDF." });

            await db.FijarContextoAsync(null, c.Empresa, http.Connection.RemoteIpAddress?.ToString(), "pagos y comprobantes", false, ct);
            var r = await facturacion.DescargarComprobanteAsync(c.Empresa, c.Documento, tipoArchivo.Value, ct);
            if (!r.Exito || r.Contenido is null)
                return Results.Json(new { error = r.Error ?? "No se pudo bajar el comprobante.", detalle = r.Detalle },
                    statusCode: r.Estado == StatusCodes.Status404NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status502BadGateway);

            http.Response.Headers.CacheControl = "no-store";
            var nombre = r.NombreArchivo ?? $"{c.Numero ?? "comprobante"}.{(tipoArchivo == ArchivoComprobante.Xml ? "xml" : "pdf")}";
            return Results.File(r.Contenido, r.TipoContenido ?? (tipoArchivo == ArchivoComprobante.Xml ? "application/xml" : "application/pdf"), nombre);
        })
        .RequiereSesion()
        .WithTags("Cobros")
        .WithSummary("La boleta o factura de un pago (PDF o XML)");
    }

    public sealed class ComprobanteCobro
    {
        public Guid Empresa { get; set; }
        public Guid Documento { get; set; }
        public string? Tipo { get; set; }
        public string? Numero { get; set; }
    }

    // ---- ayudas --------------------------------------------------------------
    private static string? Limpio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateTime Hoy() => DateTime.UtcNow.AddHours(-5);
    private static string T(JsonNode? n) => n?.ToString() ?? "";
    private static decimal Dec(JsonNode? n) => n is null ? 0m : n.GetValue<decimal>();
    private static IEnumerable<JsonObject> Filas(string json) =>
        (JsonNode.Parse(json)?["filas"] as JsonArray ?? []).OfType<JsonObject>();

    private static string FechaHora(JsonNode? n) =>
        n is null ? "" : DateTimeOffset.TryParse(n.ToString(), Pe, DateTimeStyles.None, out var f)
            ? f.ToOffset(TimeSpan.FromHours(-5)).ToString("dd/MM/yyyy HH:mm", Pe) : n.ToString();

    private static string Periodo(DateOnly? desde, DateOnly? hasta) =>
        desde is null && hasta is null ? "Todas las fechas." : $"Del {desde?.ToString("dd/MM/yyyy", Pe) ?? "inicio"} al {hasta?.ToString("dd/MM/yyyy", Pe) ?? "hoy"}.";

    private static string Periodo(string p) => p switch { "mensual" => "Mensual", "anual" => "Anual", _ => "Otro" };

    private static string Medio(JsonObject f) =>
        T(f["medio"]) == "tarjeta" ? (T(f["tarjeta"]) is { Length: > 0 } t ? t : "Tarjeta")
        : T(f["medio"]) switch { "transferencia" => "Transferencia", "deposito" => "Depósito", "yape" => "Yape", "efectivo" => "Efectivo", _ => "Otro" };

    private static string Estado(string e) => e switch
    {
        "pagado" => "Pagado", "rechazado" => "Rechazado", "devuelto" => "Devuelto",
        "anulado" => "Anulado", "procesando" => "Procesando", _ => e
    };

    private static string EstadoCuenta(string e) => e switch
    {
        "reintento" => "En reintento", "gracia" => "En gracia", "no_emite" => "No emite", _ => e
    };

    private static string NombreMes(int mes, int anio) =>
        new DateTime(anio, Math.Clamp(mes, 1, 12), 1).ToString("MMMM yyyy", new CultureInfo("es-PE")) is var s && s.Length > 0
            ? char.ToUpper(s[0], Pe) + s[1..] : $"{mes}/{anio}";
}