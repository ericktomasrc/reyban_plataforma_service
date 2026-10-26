using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Inventario;

/// <summary>
/// 064 — LOTES Y VENCIMIENTOS (inventario ampliado D4). La base reparte el
/// stock en lotes (fn_inv_lotes): entra al lote de la compra y sale primero lo
/// que vence antes. Aquí solo se mira: qué hay de cada lote y qué vence pronto.
/// </summary>
public static class EndpointsLotes
{
    /// <summary>Para SqlQuery: EF busca las columnas en snake_case.</summary>
    public sealed class FilaLote
    {
        public Guid ProductoId { get; set; }
        public string Codigo { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public string Unidad { get; set; } = "";
        public Guid SucursalId { get; set; }
        public string Sucursal { get; set; } = "";
        public string Lote { get; set; } = "";
        public DateOnly? Vence { get; set; }
        public decimal Fisico { get; set; }
        public int? Dias { get; set; }
    }

    /// <summary>065: un número de serie (para SqlQuery: columnas en snake_case).</summary>
    public sealed class FilaSerie
    {
        public Guid Id { get; set; }
        public Guid ProductoId { get; set; }
        public string Codigo { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public string Serie { get; set; } = "";
        public string Estado { get; set; } = "";
        public Guid SucursalId { get; set; }
        public string Sucursal { get; set; } = "";
        public DateTime CreadoEn { get; set; }
        public Guid? DocumentoId { get; set; }
        public string? Documento { get; set; }
        public string? Cliente { get; set; }
        public DateTime? VendidaEn { get; set; }
    }

    public static void MapearLotes(this IEndpointRouteBuilder rutas)
    {
        // 065: NÚMEROS DE SERIE. disponibles=true: los que se pueden vender (en
        // stock, en esa sucursal). q: buscar un número (garantías).
        rutas.MapGet("/api/inventario/series", async (
            Guid? productoId, Guid? sucursalId, bool? disponibles, string? q,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var texto = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().ToUpperInvariant() + "%";
            var solo = disponibles == true;
            var filas = await db.Database.SqlQuery<FilaSerie>($"""
                SELECT s.id, s.producto_id, p.codigo, p.descripcion, s.serie, s.estado,
                       s.sucursal_id, su.codigo || ' ' || su.nombre AS sucursal, s.creado_en,
                       s.documento_id, d.serie || '-' || lpad(d.correlativo::text, 8, '0') AS documento,
                       c.razon_social AS cliente, s.vendida_en
                  FROM inv_series s
                  JOIN productos p ON p.id = s.producto_id
                  JOIN sucursales su ON su.id = s.sucursal_id
                  LEFT JOIN documentos d ON d.id = s.documento_id
                  LEFT JOIN clientes c ON c.id = d.cliente_id
                 WHERE s.empresa_id = {empresaId}
                   AND ({productoId}::uuid IS NULL OR s.producto_id = {productoId})
                   AND ({sucursalId}::uuid IS NULL OR s.sucursal_id = {sucursalId})
                   AND (NOT {solo} OR s.estado = 'en_stock')
                   AND ({texto}::text IS NULL OR upper(s.serie) LIKE {texto} OR upper(p.descripcion) LIKE {texto})
                 ORDER BY s.creado_en DESC, s.serie
                 LIMIT 500
                """).ToListAsync(ct);
            return Results.Ok(filas);
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithTags("Inventario")
        .WithSummary("Números de serie: los disponibles para vender, o buscar uno (garantías)");

        // 068: LOS LOTES DE UN PRODUCTO CON STOCK EN UNA SUCURSAL, para elegir al
        // vender (comprobante o punto de venta). Lo que vence antes, primero.
        rutas.MapGet("/api/inventario/lotes/para-vender", async (
            Guid productoId, Guid? sucursalId, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var filas = await db.Database.SqlQuery<LoteParaVender>($"""
                SELECT l.lote, l.vence, l.fisico, (l.vence - (now() AT TIME ZONE 'America/Lima')::date) AS dias
                  FROM inv_lotes l
                 WHERE l.empresa_id = {empresaId} AND l.producto_id = {productoId} AND l.lote <> '' AND l.fisico > 0
                   AND l.sucursal_id = COALESCE({sucursalId}::uuid, inv_principal({empresaId}))
                 ORDER BY l.vence NULLS LAST, l.lote
                 LIMIT 200
                """).ToListAsync(ct);
            return Results.Ok(filas.Select(f => new { f.lote, f.vence, f.fisico, f.dias }));
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithTags("Inventario")
        .WithSummary("Los lotes con stock de un producto en una sucursal (para elegir al vender)");

        // dias: solo lo que vence en esos días (y lo vencido); null = todo lo que tiene stock.
        rutas.MapGet("/api/inventario/lotes", async (
            Guid? productoId, Guid? sucursalId, int? dias, string? q,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var visibles = (await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct)).Select(v => v.Id).ToArray();
            var texto = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().ToLowerInvariant() + "%";
            var filas = await db.Database.SqlQuery<FilaLote>($"""
                SELECT l.producto_id, p.codigo, p.descripcion, p.unidad_medida AS unidad,
                       l.sucursal_id, s.codigo || ' ' || s.nombre AS sucursal,
                       l.lote, l.vence, l.fisico,
                       (l.vence - (now() AT TIME ZONE 'America/Lima')::date) AS dias
                  FROM inv_lotes l
                  JOIN productos p ON p.id = l.producto_id AND p.maneja_lotes
                  JOIN sucursales s ON s.id = l.sucursal_id
                 WHERE l.empresa_id = {empresaId} AND l.sucursal_id = ANY ({visibles}) AND l.fisico <> 0
                   AND ({productoId}::uuid IS NULL OR l.producto_id = {productoId})
                   AND ({sucursalId}::uuid IS NULL OR l.sucursal_id = {sucursalId})
                   AND ({dias}::int IS NULL OR (l.vence IS NOT NULL AND l.vence <= (now() AT TIME ZONE 'America/Lima')::date + {dias}::int))
                   AND ({texto}::text IS NULL OR lower(p.descripcion) LIKE {texto} OR lower(p.codigo) LIKE {texto} OR lower(l.lote) LIKE {texto})
                 ORDER BY l.vence NULLS LAST, p.descripcion, l.lote
                 LIMIT 1000
                """).ToListAsync(ct);
            return Results.Ok(new
            {
                vencidos = filas.Count(f => f.Dias < 0 && f.Fisico > 0),
                porVencer30 = filas.Count(f => f.Dias is >= 0 and <= 30 && f.Fisico > 0),
                filas
            });
        })
        .RequierePermiso(Permisos.Inventario.Ver)
        .RequiereModulo(Modulos.Inventario)
        .WithTags("Inventario")
        .WithSummary("Lotes con stock y sus vencimientos (lo que vence antes, primero)");
    }
    private sealed class LoteParaVender
    {
        public string lote { get; set; } = "";
        public DateOnly? vence { get; set; }
        public decimal fisico { get; set; }
        public int? dias { get; set; }
    }
}