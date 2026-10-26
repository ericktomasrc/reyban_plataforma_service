using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Inventario;

/// <summary>
/// 062 — Kits y combos (inventario ampliado D2). Un kit no tiene stock propio:
/// venderlo descuenta sus componentes. El stock lo mueve la base
/// (inv_sync_documento / inv_sync_guia); esto es para lo que se revisa ANTES.
/// </summary>
public static class Kits
{
    /// <summary>Kit → sus componentes que controlan stock (con cuántos lleva).</summary>
    public static async Task<Dictionary<Guid, List<(Guid Componente, decimal Cantidad)>>> RecetasAsync(
        ContextoPlataforma db, IEnumerable<Guid> productos, CancellationToken ct)
    {
        var ids = productos.Distinct().ToList();
        if (ids.Count == 0) return [];
        var filas = await (
            from k in db.Productos.IgnoreQueryFilters().AsNoTracking()
            where ids.Contains(k.Id) && k.EsKit
            join c in db.ProductoComponentes.AsNoTracking() on k.Id equals c.KitId
            join p in db.Productos.IgnoreQueryFilters().AsNoTracking() on c.ComponenteId equals p.Id
            where p.ControlaStock
            select new { c.KitId, c.ComponenteId, c.Cantidad }).ToListAsync(ct);
        return filas.GroupBy(f => f.KitId).ToDictionary(g => g.Key, g => g.Select(f => (f.ComponenteId, f.Cantidad)).ToList());
    }

    /// <summary>Lo pedido con cada kit cambiado por sus componentes (sumados a lo suelto).</summary>
    public static async Task<Dictionary<Guid, decimal>> ExpandirAsync(ContextoPlataforma db, Dictionary<Guid, decimal> pedido, CancellationToken ct)
    {
        var recetas = await RecetasAsync(db, pedido.Keys, ct);
        if (recetas.Count == 0) return pedido;
        var r = new Dictionary<Guid, decimal>();
        void Sumar(Guid id, decimal q) => r[id] = r.GetValueOrDefault(id) + q;
        foreach (var (id, q) in pedido)
        {
            if (recetas.TryGetValue(id, out var comps)) foreach (var (c, n) in comps) Sumar(c, q * n);
            else Sumar(id, q);
        }
        return r;
    }

    /// <summary>Cuántos kits completos se pueden armar con lo disponible (0 si falta un componente).</summary>
    public static decimal Armables(List<(Guid Componente, decimal Cantidad)> receta, Func<Guid, decimal> disponible) =>
        receta.Count == 0 ? 0 : receta.Min(x => Math.Max(0, Math.Floor(disponible(x.Componente) / x.Cantidad)));
}