using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Inventario;

public sealed record LineaRecibida(Guid ProductoId, decimal? Recibido, string? Motivo);
public sealed record PeticionRecibir(List<LineaRecibida>? Lineas, string? Nota);

/// <summary>
/// 052 — TRASLADOS ENTRE SUCURSALES.
///
/// El traslado ES la guía de remisión con motivo 04 («Traslado entre
/// establecimientos de la misma empresa»): al emitirla, la mercadería sale del
/// origen y queda en tránsito. Aquí se listan y el destino marca «Recibí» con
/// lo que llegó de verdad; lo que falta queda anotado con su motivo.
/// </summary>
public static class EndpointsTraslados
{
    public sealed class FilaTraslado
    {
        public Guid Id { get; set; }
        public Guid GuiaId { get; set; }
        public string? GuiaNumero { get; set; }
        public string GuiaEstado { get; set; } = "";
        public Guid OrigenId { get; set; }
        public string Origen { get; set; } = "";
        public Guid DestinoId { get; set; }
        public string Destino { get; set; } = "";
        public string Estado { get; set; } = "";
        public DateTime SalioEn { get; set; }
        public DateTime? RecibidoEn { get; set; }
        public string? RecibidoPor { get; set; }
        public string? Nota { get; set; }
        public string? Productos { get; set; }
        public decimal Enviado { get; set; }
        public decimal? Recibido { get; set; }
    }

    public static void MapearTraslados(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/inventario/traslados").WithTags("Inventario · traslados");

        grupo.MapGet("/", async (
            string? filtro, DateOnly? desde, DateOnly? hasta, int? pagina, int? porPagina,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var visibles = (await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct)).Select(s => s.Id).ToArray();
            var pp = Listados.PorPagina(porPagina);
            var pg = Listados.Pagina(pagina);
            var f = filtro is "por_recibir" or "enviados" or "recibidos" or "diferencia" ? filtro : null;

            // La guía puede ser de otra sucursal (la de origen): su número se
            // lee sin el recorte del alcance.
            var (total, filas) = await SinAlcance.LeerAsync(db, async () =>
            {
                var t = await db.Database.SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM inv_traslados t
                     WHERE t.empresa_id = {empresaId} AND (t.origen_id = ANY ({visibles}) OR t.destino_id = ANY ({visibles}))
                       AND ({f}::text IS NULL
                            OR ({f}::text = 'por_recibir' AND t.estado = 'en_transito' AND t.destino_id = ANY ({visibles}))
                            OR ({f}::text = 'enviados' AND t.estado = 'en_transito' AND t.origen_id = ANY ({visibles}))
                            OR ({f}::text = 'recibidos' AND t.estado IN ('recibido', 'con_diferencia'))
                            OR ({f}::text = 'diferencia' AND t.estado = 'con_diferencia'))
                       AND ({desde}::date IS NULL OR t.salio_en >= {desde}::date)
                       AND ({hasta}::date IS NULL OR t.salio_en < {hasta}::date + 1)
                    """).FirstAsync(ct);
                var l = await db.Database.SqlQuery<FilaTraslado>($"""
                    SELECT t.id, t.guia_id,
                           CASE WHEN g.correlativo IS NOT NULL THEN g.serie || '-' || lpad(g.correlativo::text, 8, '0') ELSE g.serie END AS guia_numero,
                           g.estado AS guia_estado,
                           t.origen_id, so.codigo || ' ' || so.nombre AS origen,
                           t.destino_id, sd.codigo || ' ' || sd.nombre AS destino,
                           t.estado, t.salio_en, t.recibido_en, u.nombre AS recibido_por, t.nota,
                           (SELECT string_agg(p.descripcion || ' · ' || trim(to_char(x.enviado, 'FM999999990.####')), ', ' ORDER BY p.descripcion)
                              FROM inv_traslado_lineas x JOIN productos p ON p.id = x.producto_id WHERE x.traslado_id = t.id) AS productos,
                           (SELECT coalesce(sum(x.enviado), 0) FROM inv_traslado_lineas x WHERE x.traslado_id = t.id) AS enviado,
                           (SELECT sum(x.recibido) FROM inv_traslado_lineas x WHERE x.traslado_id = t.id) AS recibido
                      FROM inv_traslados t
                      JOIN guias g ON g.id = t.guia_id
                      JOIN sucursales so ON so.id = t.origen_id
                      JOIN sucursales sd ON sd.id = t.destino_id
                      LEFT JOIN usuarios u ON u.id = t.recibido_por
                     WHERE t.empresa_id = {empresaId} AND (t.origen_id = ANY ({visibles}) OR t.destino_id = ANY ({visibles}))
                       AND ({f}::text IS NULL
                            OR ({f}::text = 'por_recibir' AND t.estado = 'en_transito' AND t.destino_id = ANY ({visibles}))
                            OR ({f}::text = 'enviados' AND t.estado = 'en_transito' AND t.origen_id = ANY ({visibles}))
                            OR ({f}::text = 'recibidos' AND t.estado IN ('recibido', 'con_diferencia'))
                            OR ({f}::text = 'diferencia' AND t.estado = 'con_diferencia'))
                       AND ({desde}::date IS NULL OR t.salio_en >= {desde}::date)
                       AND ({hasta}::date IS NULL OR t.salio_en < {hasta}::date + 1)
                     ORDER BY t.salio_en DESC
                     LIMIT {pp} OFFSET {(pg - 1) * pp}
                    """).ToListAsync(ct);
                return (t, l);
            }, ct);

            var porRecibir = await db.InvTraslados.AsNoTracking()
                .CountAsync(t => t.EmpresaId == empresaId && t.Estado == "en_transito" && visibles.Contains(t.DestinoId), ct);
            var conDiferencia = await db.InvTraslados.AsNoTracking()
                .CountAsync(t => t.EmpresaId == empresaId && t.Estado == "con_diferencia" &&
                                 (visibles.Contains(t.DestinoId) || visibles.Contains(t.OrigenId)), ct);

            return Results.Ok(new
            {
                total, pagina = pg, porPagina = pp, porRecibir, conDiferencia,
                filas = filas.Select(x => new
                {
                    x.Id, x.GuiaId, x.GuiaNumero, x.GuiaEstado, x.OrigenId, x.Origen, x.DestinoId, x.Destino,
                    x.Estado, x.SalioEn, x.RecibidoEn, x.RecibidoPor, x.Nota, x.Productos, x.Enviado, x.Recibido,
                    puedeRecibir = x.Estado == "en_transito" && visibles.Contains(x.DestinoId) && ctx.Tiene(Permisos.Inventario.Trasladar)
                })
            });
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Los traslados entre sucursales (por recibir, enviados, recibidos, con diferencia)");


        grupo.MapGet("/{id:guid}", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var t = await db.InvTraslados.AsNoTracking().Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound(new { error = "No se encontró el traslado." });
            var ids = t.Lineas.Select(l => l.ProductoId).ToList();
            var productos = await db.Productos.IgnoreQueryFilters().AsNoTracking().Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.Codigo, p.Descripcion, p.UnidadMedida }).ToDictionaryAsync(p => p.Id, ct);
            var sucursales = await db.Sucursales.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Id == t.OrigenId || s.Id == t.DestinoId)
                .ToDictionaryAsync(s => s.Id, s => s.Codigo + " " + s.Nombre, ct);
            var guia = await SinAlcance.LeerAsync(db, () => db.Guias.AsNoTracking().Where(g => g.Id == t.GuiaId)
                .Select(g => new { g.Serie, g.Correlativo, g.Estado }).FirstAsync(ct), ct);
            var visibles = (await EndpointsInventario.VisiblesAsync(db, ctx, t.EmpresaId, ct)).Select(s => s.Id).ToList();

            return Results.Ok(new
            {
                t.Id, t.GuiaId, guiaNumero = guia.Correlativo is { } c ? $"{guia.Serie}-{c:D8}" : guia.Serie, guiaEstado = guia.Estado,
                t.OrigenId, origen = sucursales.GetValueOrDefault(t.OrigenId), t.DestinoId, destino = sucursales.GetValueOrDefault(t.DestinoId),
                t.Estado, t.SalioEn, t.RecibidoEn, t.Nota,
                puedeRecibir = t.Estado == "en_transito" && visibles.Contains(t.DestinoId) && ctx.Tiene(Permisos.Inventario.Trasladar),
                lineas = t.Lineas.Where(l => l.Activo).Select(l => new
                {
                    l.ProductoId, codigo = productos.GetValueOrDefault(l.ProductoId)?.Codigo,
                    descripcion = productos.GetValueOrDefault(l.ProductoId)?.Descripcion,
                    unidad = productos.GetValueOrDefault(l.ProductoId)?.UnidadMedida,
                    l.Enviado, l.Recibido, l.MotivoDiferencia
                }).OrderBy(x => x.descripcion)
            });
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Un traslado con lo que se envió y lo que llegó");


        grupo.MapPost("/{id:guid}/recibir", async (
            Guid id, PeticionRecibir p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var t = await db.InvTraslados.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound(new { error = "No se encontró el traslado." });
            var visibles = (await EndpointsInventario.VisiblesAsync(db, ctx, t.EmpresaId, ct)).Select(s => s.Id).ToList();
            if (!visibles.Contains(t.DestinoId))
                return Results.Json(new { error = "Solo recibe quien trabaja en la sucursal de destino (o cambia la de arriba)." }, statusCode: 403);
            if ((p.Lineas ?? []).Any(l => l.Motivo is not (null or "" or "merma" or "perdida" or "enviado_menos")))
                return Results.BadRequest(new { error = "El motivo de lo que falta no es válido." });

            var lineas = JsonSerializer.Serialize((p.Lineas ?? []).Select(l => new
            {
                productoId = l.ProductoId, recibido = l.Recibido, motivo = string.IsNullOrWhiteSpace(l.Motivo) ? null : l.Motivo
            }));
            var nota = EndpointsInventarioOperaciones.Limpio(p.Nota, 250);

            string? resultado = null;
            var choque = await ReglasInventario.ConPuntoAsync(db, async () =>
            {
                resultado = await db.Database.SqlQuery<string>(
                    $"SELECT inv_recibir_traslado({id}, {lineas}::jsonb, {nota}) AS \"Value\"").FirstAsync(ct);
            }, ct);
            if (choque is not null) return Results.BadRequest(new { error = ReglasInventario.Mensaje(choque) });

            return Results.Ok(new
            {
                estado = resultado,
                mensaje = resultado == "con_diferencia"
                    ? "Recibido con diferencia: lo que llegó entró al stock y lo que falta quedó anotado en el kardex."
                    : "Recibido completo: entró al stock de tu sucursal."
            });
        })
        .RequierePermiso(Permisos.Inventario.Trasladar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("«Recibí»: lo que llegó entra al destino; lo que falta, con su motivo");
    }
}