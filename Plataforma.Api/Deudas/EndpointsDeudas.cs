using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Reportes;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Deudas;

public sealed record PeticionPagoDeuda(string? Origen, Guid? Id, decimal? Monto, string? Fecha, string? Medio, string? Referencia, string? Nota);

public sealed record PeticionAnularPagoDeuda(string? Motivo);

/// <summary>
/// e267 (090) — COBROS Y PAGOS: lo que te deben (por cobrar) y lo que debes (por pagar).
///
///   GET  /api/deudas?lado=cobrar|pagar&amp;estado=…&amp;q=…    la lista con sus cifras (formato=xlsx: Excel)
///   GET  /api/deudas/{origen}/{id}                    una deuda con sus cuotas y sus pagos (origen: documento | compra)
///   POST /api/deudas/pagos                            anotar un pago (parcial o total)
///   POST /api/deudas/pagos/{id}/anular                con motivo (no se borra)
///
/// Por cobrar = facturas y boletas al crédito (sus cuotas) − notas de crédito − pagos.
/// Por pagar  = compras y gastos al crédito − pagos.
/// Lo que falta lo calcula la base (deuda_saldo), la misma cuenta que frena un pago de más.
/// SQL directo, como Compras: se revisa ANTES de insertar (un error de la base tumbaría la petición).
/// </summary>
public static class EndpointsDeudas
{
    public const string Ver = "deudas.ver";
    public const string Registrar = "deudas.registrar";

    public static readonly string[] Medios = ["efectivo", "yape", "tarjeta", "transferencia", "otro"];

    public sealed class FilaSaldo { public decimal? Saldo { get; set; } public string? Moneda { get; set; } }
    public sealed class FilaId { public Guid Id { get; set; } }

    public static void MapearDeudas(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/deudas").WithTags("Cobros y pagos");

        // --- La lista ------------------------------------------------------------
        grupo.MapGet("/", async (string? lado, string? estado, string? q, int? pagina, int? porPagina, string? formato,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var cobrar = lado != "pagar";
            var ladoTexto = cobrar ? "cobrar" : "pagar";
            var est = estado is "vencidas" or "porvencer" or "pagadas" or "todas" ? estado : "pendientes";
            var texto = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().ToUpperInvariant() + "%";
            var excel = string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase);
            var pp = excel ? 5000 : Math.Clamp(porPagina ?? 20, 10, 100);
            var pg = excel ? 1 : Math.Max(pagina ?? 1, 1);

            var json = await TextoAsync(db, $"""
                WITH e AS (SELECT * FROM deudas_filas({ladoTexto})),
                v AS (
                    SELECT * FROM e
                     WHERE ({est} = 'todas'
                            OR ({est} = 'pendientes' AND e.estado <> 'pagada')
                            OR ({est} = 'vencidas'   AND e.estado = 'vencida')
                            OR ({est} = 'porvencer'  AND e.estado = 'porvencer')
                            OR ({est} = 'pagadas'    AND e.estado = 'pagada'))
                       AND ({texto}::text IS NULL OR upper(coalesce(e.nombre, '') || ' ' || coalesce(e.numero_doc, '') || ' ' || coalesce(e.comprobante, '')) LIKE {texto}::text)
                )
                SELECT json_build_object(
                    'lado', {ladoTexto}::text,
                    'total', (SELECT count(*) FROM v),
                    'pagina', {pg}, 'porPagina', {pp},
                    'cifras', (SELECT json_build_object(
                        'pendiente',  coalesce(sum(saldo) FILTER (WHERE estado <> 'pagada' AND moneda = 'PEN'), 0),
                        'pendientes', count(*) FILTER (WHERE estado <> 'pagada'),
                        'pendienteUsd', coalesce(sum(saldo) FILTER (WHERE estado <> 'pagada' AND moneda = 'USD'), 0),
                        'vencido',    coalesce(sum(saldo) FILTER (WHERE estado = 'vencida' AND moneda = 'PEN'), 0),
                        'vencidas',   count(*) FILTER (WHERE estado = 'vencida'),
                        'semana',     coalesce(sum(saldo) FILTER (WHERE estado = 'porvencer' AND moneda = 'PEN'), 0),
                        'porVencer',  count(*) FILTER (WHERE estado = 'porvencer'),
                        'personas',   count(DISTINCT coalesce(numero_doc, nombre)) FILTER (WHERE estado <> 'pagada'),
                        'pagadas',    count(*) FILTER (WHERE estado = 'pagada'),
                        'mes', (SELECT coalesce(sum(p.monto), 0) FROM deuda_pagos p
                                 WHERE p.empresa_id = ctx_empresa() AND p.lado = {ladoTexto}::text
                                   AND p.anulado_en IS NULL AND p.activo AND p.moneda = 'PEN'
                                   AND p.fecha >= date_trunc('month', hoy_peru())::date),
                        'pagosMes', (SELECT count(*) FROM deuda_pagos p
                                 WHERE p.empresa_id = ctx_empresa() AND p.lado = {ladoTexto}::text
                                   AND p.anulado_en IS NULL AND p.activo
                                   AND p.fecha >= date_trunc('month', hoy_peru())::date)) FROM e),
                    'filas', (SELECT coalesce(json_agg(t), '[]'::json) FROM (
                        SELECT origen, id, nombre, tipo_doc AS "tipoDoc", numero_doc AS "numeroDoc", comprobante, tipo, clase,
                               emitido, vence, cuota, cuotas, moneda, total, nc, pagado, saldo, estado, dias, ultimo AS "ultimoPago"
                          FROM v
                         ORDER BY CASE estado WHEN 'vencida' THEN 0 WHEN 'porvencer' THEN 1 WHEN 'aldia' THEN 2 ELSE 3 END,
                                  CASE WHEN estado = 'pagada' THEN NULL ELSE vence END, ultimo DESC NULLS LAST, emitido DESC
                         LIMIT {pp} OFFSET {(pg - 1) * pp}) t)
                )::text AS "Value"
                """, ct);
            if (!excel) return Results.Content(json ?? "{}", "application/json");

            var datos = JsonNode.Parse(json ?? "{}")!.AsObject();
            var hoja = new HojaExcel(cobrar ? "Por cobrar" : "Por pagar") { Horizontal = true };
            hoja.Titulos.AddRange([cobrar ? "Por cobrar" : "Por pagar", $"Al {DateTime.UtcNow.AddHours(-5):dd/MM/yyyy}."]);
            hoja.Columnas.AddRange([new(cobrar ? "Cliente" : "Proveedor", TipoColumna.Texto, 34), new("RUC / DNI", TipoColumna.Texto, 14),
                new("Comprobante", TipoColumna.Texto, 20), new("Emitido", TipoColumna.Texto, 12), new("Vence", TipoColumna.Texto, 12),
                new("Moneda", TipoColumna.Texto, 8), new("Total", TipoColumna.Monto, 13), new("Pagado", TipoColumna.Monto, 13),
                new("Saldo", TipoColumna.Monto, 13), new("Estado", TipoColumna.Texto, 14)]);
            static string T(JsonNode? n) => n?.ToString() ?? "";
            static CeldaExcel M(JsonNode? n) => new(n is null ? 0m : n.GetValue<decimal>(), TipoColumna.Monto);
            static string F(JsonNode? n) => n is null ? "" : DateOnly.Parse(n.ToString(), CultureInfo.InvariantCulture).ToString("dd/MM/yyyy");
            var nombres = new Dictionary<string, string> { ["vencida"] = "Vencida", ["porvencer"] = "Por vencer", ["aldia"] = "Al día", ["pagada"] = "Pagada" };
            foreach (var f in (datos["filas"] as JsonArray ?? []).OfType<JsonObject>())
                hoja.Filas.Add([T(f["nombre"]), T(f["numeroDoc"]), T(f["comprobante"]), F(f["emitido"]), F(f["vence"]), T(f["moneda"]),
                    M(f["total"]), M(f["pagado"]), M(f["saldo"]), nombres.GetValueOrDefault(T(f["estado"]), T(f["estado"]))]);
            return Results.File(ExcelSimple.Crear([hoja]), ExcelSimple.TipoMime,
                $"{(cobrar ? "por-cobrar" : "por-pagar")}-{DateTime.UtcNow.AddHours(-5):yyyy-MM-dd}.xlsx");
        })
        .RequierePermiso(Ver).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Lo que te deben o lo que debes, con sus cifras");

        // --- Una deuda, con sus cuotas y sus pagos ---------------------------------
        grupo.MapGet("/{origen}/{id:guid}", async (string origen, Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (origen is not ("documento" or "compra")) return Results.NotFound(new { error = "No se encontró." });
            var cobrar = origen == "documento";
            var json = await TextoAsync(db, $"""
                SELECT row_to_json(t)::text AS "Value" FROM (
                    SELECT f.origen, f.id, f.nombre, f.tipo_doc AS "tipoDoc", f.numero_doc AS "numeroDoc", f.comprobante, f.tipo, f.clase,
                           f.emitido, f.vence, f.cuota, f.cuotas, f.moneda, f.total, f.nc, f.pagado, f.saldo, f.estado, f.dias,
                           (SELECT coalesce(json_agg(json_build_object('numero', o, 'monto', (x ->> 'monto')::numeric, 'vence', x ->> 'vence') ORDER BY o), '[]'::json)
                              FROM documentos d, jsonb_array_elements(d.cuotas) WITH ORDINALITY AS c(x, o) WHERE d.id = f.id AND f.origen = 'documento') AS "detalleCuotas",
                           (SELECT coalesce(json_agg(json_build_object('id', p.id, 'fecha', p.fecha, 'monto', p.monto, 'medio', p.medio,
                                       'referencia', p.referencia, 'nota', p.nota, 'anuladoEn', p.anulado_en, 'anuladoMotivo', p.anulado_motivo,
                                       'registro', coalesce(u.nombre, '')) ORDER BY p.fecha DESC, p.creado_en DESC), '[]'::json)
                              FROM deuda_pagos p LEFT JOIN usuarios u ON u.id = p.creado_por
                             WHERE p.activo AND (p.documento_id = f.id OR p.compra_id = f.id)) AS pagos
                      FROM deudas_filas({(cobrar ? "cobrar" : "pagar")}) f WHERE f.id = {id}) t
                """, ct);
            return json is null ? Results.NotFound(new { error = "No se encontró, o no es una deuda (no es al crédito)." })
                                : Results.Content(json, "application/json");
        })
        .RequierePermiso(Ver).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Una deuda con sus cuotas y sus pagos");

        // --- Anotar un pago -----------------------------------------------------------
        grupo.MapPost("/pagos", async (PeticionPagoDeuda p, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (p.Origen is not ("documento" or "compra") || p.Id is not { } id) return Results.BadRequest(new { error = "Falta qué deuda se paga." });
            var monto = Math.Round(p.Monto ?? 0, 2);
            if (monto <= 0) return Results.BadRequest(new { error = "Escribe cuánto pagó (mayor que cero)." });
            var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
            var fecha = hoy;
            if (!string.IsNullOrWhiteSpace(p.Fecha)
                && !DateOnly.TryParseExact(p.Fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha))
                return Results.BadRequest(new { error = "La fecha no es válida." });
            if (fecha > hoy) return Results.BadRequest(new { error = "La fecha del pago no puede ser futura." });
            var medio = Medios.Contains(p.Medio) ? p.Medio! : "efectivo";
            static string? Limpio(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, max)];
            var esDoc = p.Origen == "documento";
            Guid? doc = esDoc ? id : null, compra = esDoc ? null : id;

            var s = (await db.Database.SqlQuery<FilaSaldo>($"""
                SELECT deuda_saldo({doc}::uuid, {compra}::uuid) AS saldo,
                       coalesce((SELECT moneda FROM documentos WHERE id = {doc}::uuid), (SELECT moneda FROM compras WHERE id = {compra}::uuid)) AS moneda
                """).ToListAsync(ct)).Single();
            if (s.Saldo is not { } saldo) return Results.BadRequest(new { error = "Eso no es una deuda: no es al crédito, o está anulado." });
            var simbolo = s.Moneda == "USD" ? "US$" : "S/";
            if (saldo <= 0.005m) return Results.Conflict(new { error = "Ya está pagada: no falta nada." });
            if (monto > saldo + 0.005m) return Results.BadRequest(new { error = $"El pago es mayor que lo que falta ({simbolo} {saldo:0.00})." });

            var nuevo = (await db.Database.SqlQuery<FilaId>($"""
                INSERT INTO deuda_pagos (empresa_id, lado, documento_id, compra_id, fecha, monto, moneda, medio, referencia, nota, creado_por)
                VALUES (ctx_empresa(), {(esDoc ? "cobrar" : "pagar")}, {doc}::uuid, {compra}::uuid, {fecha}, {monto}, {s.Moneda ?? "PEN"}, {medio},
                        {Limpio(p.Referencia, 80)}::text, {Limpio(p.Nota, 300)}::text, ctx_usuario())
                RETURNING id
                """).ToListAsync(ct)).Single().Id;
            var queda = saldo - monto;
            return Results.Ok(new
            {
                id = nuevo, saldo = queda,
                mensaje = queda <= 0.005m ? "Pago anotado. Ya no debe nada." : $"Pago anotado. Le quedan {simbolo} {queda:0.00}.",
            });
        })
        .RequierePermiso(Registrar).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Anotar un pago (parcial o total) de una deuda");

        // --- Anular un pago -------------------------------------------------------------
        grupo.MapPost("/pagos/{id:guid}/anular", async (Guid id, PeticionAnularPagoDeuda p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var motivo = (p.Motivo ?? "").Trim();
            if (motivo.Length < 3) return Results.BadRequest(new { error = "Escribe por qué se anula (3 letras como mínimo)." });
            var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE deuda_pagos SET anulado_en = now(), anulado_motivo = {motivo[..Math.Min(motivo.Length, 200)]}
                 WHERE id = {id} AND anulado_en IS NULL AND activo
                """, ct);
            return n == 0 ? Results.NotFound(new { error = "No se encontró, o ya estaba anulado." })
                          : Results.Ok(new { mensaje = "Pago anulado. Lo que falta volvió a subir." });
        })
        .RequierePermiso(Registrar).RequiereModulo(Modulos.Facturacion)
        .WithSummary("Anular un pago (no se borra)");
    }

    // =========================================================================
    private static async Task<string?> TextoAsync(ContextoPlataforma db, FormattableString sql, CancellationToken ct) =>
        (await db.Database.SqlQuery<string>(sql).ToListAsync(ct)).FirstOrDefault();
}