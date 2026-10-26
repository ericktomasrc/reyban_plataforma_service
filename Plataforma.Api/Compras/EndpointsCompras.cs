using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Reportes;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Compras;

public sealed record PeticionCompra(
    string? Clase, string? Fecha, string? ProveedorTipoDoc, string? ProveedorDoc, string? ProveedorNombre,
    string? ComprobanteTipo, string? Serie, string? Numero, string? Moneda, decimal? TipoCambio,
    decimal? Base, decimal? Igv, decimal? NoGravado, decimal? Total, string? Categoria, string? Descripcion,
    Guid? SucursalId, Guid? InvOperacionId);

public sealed record PeticionAnularCompra(string? Motivo);

public sealed record PeticionIngresoCompra(Guid OperacionId);

/// <summary>
/// e178 (059) — COMPRAS Y GASTOS, con o sin inventario.
///
///   GET  /api/compras                 la lista (fechas, clase, búsqueda, páginas) con totales
///   GET  /api/compras/{id}
///   POST /api/compras                 anotar
///   PUT  /api/compras/{id}            corregir (si no está anulada)
///   POST /api/compras/{id}/anular     con motivo (no se borra)
///   GET  /api/reportes/compras        por proveedor, categoría y mes, y el Registro de Compras (Excel)
///
/// SQL directo (sin entidad de EF): lo que la base pone en mayúsculas (056) es
/// lo que se devuelve. Un repetido se mira ANTES de insertar: un error de la
/// base tumbaría la transacción de toda la petición.
/// </summary>
public static class EndpointsCompras
{
    public const string Ver = "compras.ver";
    public const string Registrar = "compras.registrar";

    public static readonly string[] Categorias = ["mercaderia", "insumos", "activos", "alquiler", "servicios", "sueldos", "transporte", "impuestos", "publicidad", "otros"];
    public static readonly string[] Tipos = ["01", "03", "02", "12", "14", "00"];

    public sealed class FilaId { public Guid Id { get; set; } }
    public sealed class FilaCuenta { public int Cuantos { get; set; } }

    public static void MapearCompras(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/compras").WithTags("Compras y gastos");

        // --- La lista ------------------------------------------------------------
        grupo.MapGet("/", async (string? desde, string? hasta, string? clase, string? estado, string? q, int? pagina, int? porPagina,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            if (Periodo(desde, hasta, out var d, out var h) is { } mal) return Results.BadRequest(new { error = mal });
            var cl = clase is "compra" or "gasto" ? clase : null;
            var anuladas = estado == "anuladas";
            var texto = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().ToUpperInvariant() + "%";
            var pp = Math.Clamp(porPagina ?? 20, 10, 100);
            var pg = Math.Max(pagina ?? 1, 1);

            var json = await TextoAsync(db, $"""
                WITH f AS (
                    SELECT c.*, CASE WHEN c.moneda = 'PEN' THEN 1 ELSE coalesce(c.tipo_cambio, 0) END AS tc
                      FROM compras c
                     WHERE c.empresa_id = ctx_empresa() AND c.activo
                       AND c.fecha_emision BETWEEN {d} AND {h}
                       AND ({cl}::text IS NULL OR c.clase = {cl}::text)
                       AND ((c.anulada_en IS NOT NULL) = {anuladas})
                       AND ({texto}::text IS NULL OR upper(coalesce(c.proveedor_nombre, '') || ' ' || coalesce(c.proveedor_doc, '') || ' '
                            || coalesce(c.serie, '') || '-' || coalesce(c.numero, '') || ' ' || coalesce(c.descripcion, '')) LIKE {texto}::text)
                )
                SELECT json_build_object(
                    'total', (SELECT count(*) FROM f),
                    'pagina', {pg}, 'porPagina', {pp},
                    'totales', (SELECT json_build_object('base', coalesce(sum(round(base * tc, 2)), 0), 'igv', coalesce(sum(round(igv * tc, 2)), 0),
                                                         'noGravado', coalesce(sum(round(no_gravado * tc, 2)), 0), 'total', coalesce(sum(round(total * tc, 2)), 0)) FROM f),
                    'conteo', (SELECT json_build_object(
                                  'compras', count(*) FILTER (WHERE x.clase = 'compra' AND x.anulada_en IS NULL),
                                  'gastos', count(*) FILTER (WHERE x.clase = 'gasto' AND x.anulada_en IS NULL),
                                  'anuladas', count(*) FILTER (WHERE x.anulada_en IS NOT NULL))
                                 FROM compras x WHERE x.empresa_id = ctx_empresa() AND x.activo AND x.fecha_emision BETWEEN {d} AND {h}),
                    'filas', (SELECT coalesce(json_agg(t), '[]'::json) FROM (
                        SELECT
                        c.id, c.clase, c.fecha_emision AS "fecha", c.sucursal_id AS "sucursalId",
                        c.proveedor_tipo_doc AS "proveedorTipoDoc", c.proveedor_doc AS "proveedorDoc", c.proveedor_nombre AS "proveedorNombre",
                        c.comprobante_tipo AS "comprobanteTipo", c.serie, c.numero, c.moneda, c.tipo_cambio AS "tipoCambio",
                        c.base, c.igv, c.no_gravado AS "noGravado", c.total, c.categoria, c.descripcion,
                        c.inv_operacion_id AS "invOperacionId", c.anulada_en AS "anuladaEn", c.anulada_motivo AS "anuladaMotivo",
                        c.proveedor_id AS "proveedorId", (SELECT 'IN-' || lpad(o.numero::text, 4, '0') FROM inv_operaciones o WHERE o.id = c.inv_operacion_id) AS "ingreso",
                        c.creado_en AS "creadoEn", coalesce(u.nombre, '') AS "registro"
                        
                          FROM f c LEFT JOIN usuarios u ON u.id = c.creado_por
                         ORDER BY c.fecha_emision DESC, c.creado_en DESC
                         LIMIT {pp} OFFSET {(pg - 1) * pp}) t)
                )::text AS "Value"
                """, ct);
            return Results.Content(json ?? "{}", "application/json");
        })
        .RequierePermiso(Ver).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Compras y gastos del periodo");

        // --- Una ---------------------------------------------------------------
        grupo.MapGet("/{id:guid}", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await TextoAsync(db, $"""
                SELECT row_to_json(t)::text AS "Value" FROM (
                    SELECT
                        c.id, c.clase, c.fecha_emision AS "fecha", c.sucursal_id AS "sucursalId",
                        c.proveedor_tipo_doc AS "proveedorTipoDoc", c.proveedor_doc AS "proveedorDoc", c.proveedor_nombre AS "proveedorNombre",
                        c.comprobante_tipo AS "comprobanteTipo", c.serie, c.numero, c.moneda, c.tipo_cambio AS "tipoCambio",
                        c.base, c.igv, c.no_gravado AS "noGravado", c.total, c.categoria, c.descripcion,
                        c.inv_operacion_id AS "invOperacionId", c.anulada_en AS "anuladaEn", c.anulada_motivo AS "anuladaMotivo",
                        c.proveedor_id AS "proveedorId", (SELECT 'IN-' || lpad(o.numero::text, 4, '0') FROM inv_operaciones o WHERE o.id = c.inv_operacion_id) AS "ingreso",
                        c.creado_en AS "creadoEn", coalesce(u.nombre, '') AS "registro"
                        
                      FROM compras c LEFT JOIN usuarios u ON u.id = c.creado_por WHERE c.id = {id}) t
                """, ct);
            return json is null ? Results.NotFound(new { error = "No se encontró." }) : Results.Content(json, "application/json");
        })
        .RequierePermiso(Ver).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Una compra o gasto");

        // --- Anotar ------------------------------------------------------------
        grupo.MapPost("/", async (PeticionCompra p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (Revisar(p, out var v) is { } mal) return Results.BadRequest(new { error = mal });
            if (await RepetidaAsync(db, v, null, ct)) return Results.Conflict(new { error = $"Ya anotaste el comprobante {v.Serie}-{v.Numero} de {v.ProveedorNombre}." });
            var proveedorId = await ProveedorAsync(db, ctx, v, ct);

            var id = (await db.Database.SqlQuery<FilaId>($"""
                INSERT INTO compras (empresa_id, sucursal_id, clase, fecha_emision, proveedor_id, proveedor_tipo_doc, proveedor_doc, proveedor_nombre,
                                     comprobante_tipo, serie, numero, moneda, tipo_cambio, base, igv, no_gravado, total, categoria, descripcion,
                                     inv_operacion_id, creado_por)
                VALUES (ctx_empresa(), {v.SucursalId}::uuid, {v.Clase}, {v.Fecha}, {proveedorId}::uuid, {v.ProveedorTipoDoc}::char(1), {v.ProveedorDoc}::text, {v.ProveedorNombre}::text,
                        {v.ComprobanteTipo}, {v.Serie}::text, {v.Numero}::text, {v.Moneda}, {v.TipoCambio}::numeric, {v.Base}, {v.Igv}, {v.NoGravado}, {v.Total}, {v.Categoria}, {v.Descripcion}::text,
                        {v.InvOperacionId}::uuid, ctx_usuario())
                RETURNING id
                """).ToListAsync(ct)).Single().Id;
            return Results.Ok(new { id, mensaje = v.Clase == "gasto" ? "Gasto anotado." : "Compra anotada." });
        })
        .RequierePermiso(Registrar).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Anotar una compra o un gasto");

        // --- Corregir ----------------------------------------------------------
        grupo.MapPut("/{id:guid}", async (Guid id, PeticionCompra p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (Revisar(p, out var v) is { } mal) return Results.BadRequest(new { error = mal });
            var estado = await TextoAsync(db, $"SELECT CASE WHEN anulada_en IS NULL THEN 'ok' ELSE 'anulada' END AS \"Value\" FROM compras WHERE id = {id}", ct);
            if (estado is null) return Results.NotFound(new { error = "No se encontró." });
            if (estado == "anulada") return Results.Conflict(new { error = "Está anulada: no se corrige. Anota una nueva." });
            if (await RepetidaAsync(db, v, id, ct)) return Results.Conflict(new { error = $"Ya anotaste el comprobante {v.Serie}-{v.Numero} de {v.ProveedorNombre}." });
            var proveedorId = await ProveedorAsync(db, ctx, v, ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE compras SET sucursal_id = {v.SucursalId}::uuid, clase = {v.Clase}, fecha_emision = {v.Fecha}, proveedor_id = {proveedorId}::uuid,
                       proveedor_tipo_doc = {v.ProveedorTipoDoc}::char(1), proveedor_doc = {v.ProveedorDoc}::text, proveedor_nombre = {v.ProveedorNombre}::text,
                       comprobante_tipo = {v.ComprobanteTipo}, serie = {v.Serie}::text, numero = {v.Numero}::text, moneda = {v.Moneda},
                       tipo_cambio = {v.TipoCambio}::numeric, base = {v.Base}, igv = {v.Igv}, no_gravado = {v.NoGravado}, total = {v.Total},
                       categoria = {v.Categoria}, descripcion = {v.Descripcion}::text, inv_operacion_id = {v.InvOperacionId}::uuid
                 WHERE id = {id} AND anulada_en IS NULL
                """, ct);
            return Results.Ok(new { id, mensaje = "Corregido." });
        })
        .RequierePermiso(Registrar).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Corregir una compra o gasto");

        // --- Anular ------------------------------------------------------------
        grupo.MapPost("/{id:guid}/anular", async (Guid id, PeticionAnularCompra p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var motivo = (p.Motivo ?? "").Trim();
            if (motivo.Length < 3) return Results.BadRequest(new { error = "Escribe por qué se anula (3 letras como mínimo)." });
            var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE compras SET anulada_en = now(), anulada_motivo = {motivo[..Math.Min(motivo.Length, 200)]}
                 WHERE id = {id} AND anulada_en IS NULL
                """, ct);
            return n == 0 ? Results.NotFound(new { error = "No se encontró, o ya estaba anulada." }) : Results.Ok(new { mensaje = "Anulada. Queda en la pestaña «Anuladas»." });
        })
        .RequierePermiso(Registrar).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Anular una compra o gasto (no se borra)");

        // --- e182: la compra ya entró al stock (Inventario › Ingreso por compra) ----
        grupo.MapPost("/{id:guid}/ingreso", async (Guid id, PeticionIngresoCompra p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var valido = (await db.Database.SqlQuery<FilaCuenta>($"""
                SELECT count(*)::int AS cuantos FROM inv_operaciones
                 WHERE id = {p.OperacionId} AND empresa_id = ctx_empresa() AND tipo = 'ingreso' AND estado = 'registrada'
                """).ToListAsync(ct)).Single().Cuantos > 0;
            if (!valido) return Results.BadRequest(new { error = "Ese ingreso no existe o está anulado." });
            var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE compras SET inv_operacion_id = {p.OperacionId} WHERE id = {id} AND anulada_en IS NULL
                """, ct);
            return n == 0 ? Results.NotFound(new { error = "No se encontró la compra, o está anulada." })
                          : Results.Ok(new { mensaje = "La compra quedó enlazada a su ingreso al stock." });
        })
        .RequierePermiso(Registrar).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Enlazar la compra con su ingreso al stock");

        // --- El reporte --------------------------------------------------------
        rutas.MapGet("/api/reportes/compras", async (string? desde, string? hasta, string? formato, string? q,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (Periodo(desde, hasta, out var d, out var h) is { } mal) return Results.BadRequest(new { error = mal });
            var texto = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().ToUpperInvariant() + "%";
            var json = await TextoAsync(db, $"""
                WITH f AS (
                    SELECT c.*, CASE WHEN c.moneda = 'PEN' THEN 1 ELSE coalesce(c.tipo_cambio, 0) END AS tc
                      FROM compras c
                     WHERE c.empresa_id = ctx_empresa() AND c.activo AND c.anulada_en IS NULL
                       AND c.fecha_emision BETWEEN {d} AND {h}
                       AND ({texto}::text IS NULL OR upper(coalesce(c.proveedor_nombre, '') || ' ' || coalesce(c.proveedor_doc, '') || ' ' || coalesce(c.descripcion, '')) LIKE {texto}::text)
                )
                SELECT json_build_object(
                    'compras', (SELECT coalesce(sum(round(total * tc, 2)), 0) FROM f WHERE clase = 'compra'),
                    'gastos',  (SELECT coalesce(sum(round(total * tc, 2)), 0) FROM f WHERE clase = 'gasto'),
                    'igv',     (SELECT coalesce(sum(round(igv * tc, 2)), 0) FROM f WHERE comprobante_tipo = '01'),
                    'porProveedor', (SELECT coalesce(json_agg(x ORDER BY x.total DESC), '[]'::json) FROM (
                        SELECT coalesce(proveedor_nombre, '(sin proveedor)') AS proveedor, coalesce(proveedor_doc, '') AS documento,
                               count(*)::int AS comprobantes, sum(round(base * tc, 2)) AS base, sum(round(igv * tc, 2)) AS igv, sum(round(total * tc, 2)) AS total
                          FROM f GROUP BY 1, 2) x),
                    'porCategoria', (SELECT coalesce(json_agg(x ORDER BY x.total DESC), '[]'::json) FROM (
                        SELECT categoria, clase, count(*)::int AS cuantos, sum(round(total * tc, 2)) AS total FROM f GROUP BY 1, 2) x),
                    'porMes', (SELECT coalesce(json_agg(x ORDER BY x.mes), '[]'::json) FROM (
                        SELECT to_char(fecha_emision, 'YYYY-MM') AS mes,
                               sum(round(total * tc, 2)) FILTER (WHERE clase = 'compra') AS compras,
                               sum(round(total * tc, 2)) FILTER (WHERE clase = 'gasto') AS gastos,
                               sum(round(igv * tc, 2)) FILTER (WHERE comprobante_tipo = '01') AS igv
                          FROM f GROUP BY 1) x),
                    'registro', (SELECT coalesce(json_agg(x ORDER BY x.fecha, x.serie, x.numero), '[]'::json) FROM (
                        SELECT id, fecha_emision AS fecha, comprobante_tipo AS tipo, serie, numero, proveedor_tipo_doc AS "tipoDoc",
                               proveedor_doc AS documento, proveedor_nombre AS proveedor, moneda, tipo_cambio AS "tipoCambio",
                               round(base * tc, 2) AS base, round(igv * tc, 2) AS igv, round(no_gravado * tc, 2) AS "noGravado",
                               round(total * tc, 2) AS total, clase, categoria, descripcion
                          FROM f) x)
                )::text AS "Value"
                """, ct);
            var datos = JsonNode.Parse(json ?? "{}")!.AsObject();
            datos["desde"] = d.ToString("yyyy-MM-dd"); datos["hasta"] = h.ToString("yyyy-MM-dd"); datos["moneda"] = "PEN";
            datos["ambiente"] = "produccion";   // las compras no tienen modo prueba

            if (!string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase))
                return Results.Content(datos.ToJsonString(), "application/json");

            var empresa = await db.Empresas.AsNoTracking().Where(e => e.Id == ctx.EmpresaId)
                .Select(e => new { e.Nombre, e.RazonSocial, e.Ruc }).FirstOrDefaultAsync(ct);
            var titulos = new List<string> { "Compras y gastos",
                $"{empresa?.RazonSocial ?? empresa?.Nombre}{(empresa?.Ruc is { } ruc ? " · RUC " + ruc : "")}", $"Del {d:dd/MM/yyyy} al {h:dd/MM/yyyy}. Montos en soles." };
            return Results.File(ExcelSimple.Crear(Hojas(datos, titulos)), ExcelSimple.TipoMime, $"compras-y-gastos-{d:yyyy-MM-dd}-a-{h:yyyy-MM-dd}.xlsx");
        })
        .RequierePermiso(Ver).RequiereModulo(Modulos.Facturacion)
        .WithTags("Reportes")
        .WithSummary("Compras y gastos: por proveedor, categoría y mes; Registro de Compras");
    }

    // =========================================================================

    private sealed record Valida(string Clase, DateOnly Fecha, string? ProveedorTipoDoc, string? ProveedorDoc, string? ProveedorNombre,
        string ComprobanteTipo, string? Serie, string? Numero, string Moneda, decimal? TipoCambio,
        decimal Base, decimal Igv, decimal NoGravado, decimal Total, string Categoria, string? Descripcion, Guid? SucursalId, Guid? InvOperacionId);

    private static string? Revisar(PeticionCompra p, out Valida v)
    {
        v = null!;
        static string? Limpio(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, max)];
        var clase = p.Clase is "gasto" ? "gasto" : "compra";
        if (!DateOnly.TryParseExact(p.Fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)) return "Falta la fecha (o no es válida).";
        if (fecha > DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5)).AddDays(1)) return "La fecha no puede ser futura.";
        var tipo = Tipos.Contains(p.ComprobanteTipo) ? p.ComprobanteTipo! : "01";
        var categoria = Categorias.Contains(p.Categoria) ? p.Categoria! : (clase == "compra" ? "mercaderia" : "otros");
        var moneda = p.Moneda == "USD" ? "USD" : "PEN";
        if (moneda == "USD" && p.TipoCambio is not > 0) return "En dólares, escribe el tipo de cambio.";
        var total = Math.Round(p.Total ?? 0, 2);
        if (total <= 0) return "El total tiene que ser mayor que cero.";

        var doc = Limpio(p.ProveedorDoc, 15);
        var nombre = Limpio(p.ProveedorNombre, 200);
        var serie = Limpio(p.Serie, 10);
        var numero = Limpio(p.Numero, 20);
        if (tipo != "00")
        {
            if (doc is null || nombre is null) return "Escribe el RUC o DNI del proveedor y su nombre.";
            if (serie is null || numero is null) return "Escribe la serie y el número del comprobante.";
            if (tipo == "01" && (doc.Length != 11 || !doc.All(char.IsAsciiDigit))) return "Una factura es de un RUC (11 dígitos).";
        }
        var tipoDoc = doc is null ? null : doc.Length == 11 ? "6" : doc.Length == 8 ? "1" : (p.ProveedorTipoDoc is { Length: 1 } t ? t : "0");

        // Montos: si solo viene el total, una factura se parte en base + IGV (18 %); lo demás va como no gravado.
        decimal b = Math.Round(p.Base ?? 0, 2), i = Math.Round(p.Igv ?? 0, 2), n = Math.Round(p.NoGravado ?? 0, 2);
        if (p.Base is null && p.Igv is null && p.NoGravado is null)
        {
            if (tipo == "01") { b = Math.Round(total / 1.18m, 2); i = total - b; } else n = total;
        }
        if (b < 0 || i < 0 || n < 0) return "Los montos no pueden ser negativos.";
        if (Math.Abs(b + i + n - total) > 0.05m) return $"Base + IGV + no gravado ({b + i + n:0.00}) no cuadra con el total ({total:0.00}).";

        v = new Valida(clase, fecha, tipoDoc, doc, nombre, tipo, serie, numero, moneda, moneda == "USD" ? p.TipoCambio : null,
                       b, i, n, total, categoria, Limpio(p.Descripcion, 300), p.SucursalId, p.InvOperacionId);
        return null;
    }

    private static async Task<bool> RepetidaAsync(ContextoPlataforma db, Valida v, Guid? menos, CancellationToken ct)
    {
        if (v.ComprobanteTipo == "00") return false;
        var n = (await db.Database.SqlQuery<FilaCuenta>($"""
            SELECT count(*)::int AS cuantos FROM compras
             WHERE empresa_id = ctx_empresa() AND anulada_en IS NULL AND comprobante_tipo = {v.ComprobanteTipo}
               AND proveedor_doc = {v.ProveedorDoc} AND upper(serie) = upper({v.Serie}) AND ltrim(numero, '0') = ltrim({v.Numero}, '0')
               AND ({menos}::uuid IS NULL OR id <> {menos}::uuid)
            """).ToListAsync(ct)).Single().Cuantos;
        return n > 0;
    }

    /// <summary>El proveedor queda en la lista (Inventario › Proveedores) para la próxima.</summary>
    private static async Task<Guid?> ProveedorAsync(ContextoPlataforma db, ContextoPeticion ctx, Valida v, CancellationToken ct)
    {
        if (v.ProveedorDoc is null || v.ProveedorNombre is null || v.ProveedorTipoDoc is null || ctx.EmpresaId is not { } empresaId) return null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO proveedores (empresa_id, tipo_documento, numero_documento, razon_social, creado_por)
            VALUES ({empresaId}, {v.ProveedorTipoDoc}::char(1), {v.ProveedorDoc}, {v.ProveedorNombre}, ctx_usuario())
            ON CONFLICT (empresa_id, tipo_documento, numero_documento) DO NOTHING
            """, ct);
        return (await db.Database.SqlQuery<FilaId>($"""
            SELECT id FROM proveedores WHERE empresa_id = {empresaId} AND tipo_documento = {v.ProveedorTipoDoc}::char(1) AND numero_documento = {v.ProveedorDoc}
            """).ToListAsync(ct)).FirstOrDefault()?.Id;
    }

    private static string? Periodo(string? desde, string? hasta, out DateOnly d, out DateOnly h)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        d = new DateOnly(hoy.Year, hoy.Month, 1); h = hoy;
        static bool F(string? t, out DateOnly f) => DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out f);
        if (!string.IsNullOrWhiteSpace(desde) && !F(desde, out d)) return "La fecha «desde» no es válida.";
        if (!string.IsNullOrWhiteSpace(hasta) && !F(hasta, out h)) return "La fecha «hasta» no es válida.";
        if (d > h) return "La fecha «desde» es posterior a «hasta».";
        if (h.DayNumber - d.DayNumber > 366) return "Elige un periodo de un año como máximo.";
        return null;
    }

    private static async Task<string?> TextoAsync(ContextoPlataforma db, FormattableString sql, CancellationToken ct) =>
        (await db.Database.SqlQuery<string>(sql).ToListAsync(ct)).FirstOrDefault();

    private static readonly Dictionary<string, string> NombreTipo = new()
        { ["01"] = "Factura", ["03"] = "Boleta", ["02"] = "Recibo por honorarios", ["12"] = "Ticket", ["14"] = "Recibo de servicios", ["00"] = "Sin comprobante" };

    private static List<HojaExcel> Hojas(JsonObject d, List<string> titulos)
    {
        HojaExcel Nueva(string nombre) { var h = new HojaExcel(nombre) { Horizontal = true }; h.Titulos.AddRange(titulos); return h; }
        static CeldaExcel M(JsonNode? n) => new(n is null ? 0m : n.GetValue<decimal>(), TipoColumna.Monto);
        static string T(JsonNode? n) => n?.ToString() ?? "";
        IEnumerable<JsonObject> F(string k) => (d[k] as JsonArray ?? []).OfType<JsonObject>();

        var reg = Nueva("Registro de compras");
        reg.Columnas.AddRange([new("Fecha", TipoColumna.Texto, 12), new("Tipo", TipoColumna.Texto, 18), new("Serie", TipoColumna.Texto, 8),
            new("Número", TipoColumna.Texto, 12), new("Tipo doc.", TipoColumna.Texto, 8), new("RUC / DNI", TipoColumna.Texto, 14),
            new("Proveedor", TipoColumna.Texto, 36), new("Moneda", TipoColumna.Texto, 8), new("T. cambio", TipoColumna.Texto, 10),
            new("Base", TipoColumna.Monto, 13), new("IGV", TipoColumna.Monto, 13), new("No gravado", TipoColumna.Monto, 13), new("Total", TipoColumna.Monto, 13),
            new("Clase", TipoColumna.Texto, 9), new("Categoría", TipoColumna.Texto, 14), new("Descripción", TipoColumna.Texto, 30)]);
        foreach (var f in F("registro"))
            reg.Filas.Add([DateOnly.Parse(T(f["fecha"]), CultureInfo.InvariantCulture).ToString("dd/MM/yyyy"), NombreTipo.GetValueOrDefault(T(f["tipo"]), T(f["tipo"])),
                T(f["serie"]), T(f["numero"]), T(f["tipoDoc"]), T(f["documento"]), T(f["proveedor"]), T(f["moneda"]), T(f["tipoCambio"]),
                M(f["base"]), M(f["igv"]), M(f["noGravado"]), M(f["total"]), T(f["clase"]), T(f["categoria"]), T(f["descripcion"])]);

        var prov = Nueva("Por proveedor");
        prov.Columnas.AddRange([new("Proveedor", TipoColumna.Texto, 36), new("RUC / DNI", TipoColumna.Texto, 14), new("Comprobantes", TipoColumna.Entero, 12),
            new("Base", TipoColumna.Monto, 13), new("IGV", TipoColumna.Monto, 13), new("Total", TipoColumna.Monto, 13)]);
        foreach (var f in F("porProveedor"))
            prov.Filas.Add([T(f["proveedor"]), T(f["documento"]), new CeldaExcel(f["comprobantes"]!.GetValue<int>(), TipoColumna.Entero), M(f["base"]), M(f["igv"]), M(f["total"])]);

        var cat = Nueva("Por categoría");
        cat.Columnas.AddRange([new("Categoría", TipoColumna.Texto, 18), new("Clase", TipoColumna.Texto, 10), new("Cuántos", TipoColumna.Entero, 10), new("Total", TipoColumna.Monto, 13)]);
        foreach (var f in F("porCategoria"))
            cat.Filas.Add([T(f["categoria"]), T(f["clase"]), new CeldaExcel(f["cuantos"]!.GetValue<int>(), TipoColumna.Entero), M(f["total"])]);

        var mes = Nueva("Por mes");
        mes.Columnas.AddRange([new("Mes", TipoColumna.Texto, 10), new("Compras", TipoColumna.Monto, 13), new("Gastos", TipoColumna.Monto, 13), new("IGV de facturas", TipoColumna.Monto, 14)]);
        foreach (var f in F("porMes")) mes.Filas.Add([T(f["mes"]), M(f["compras"]), M(f["gastos"]), M(f["igv"])]);

        return [reg, prov, cat, mes];
    }
}