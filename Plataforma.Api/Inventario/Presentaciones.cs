using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Inventario;

/// <summary>
/// 061 — Presentaciones (inventario ampliado D1): «CAJA X 12» = 12 unidades
/// del producto. El stock se cuenta siempre en la unidad del producto; la
/// línea del comprobante va a SUNAT tal cual (1 BX). El factor de las líneas
/// lo pone la base; esto es para lo que se calcula ANTES de guardar.
/// </summary>
public static class Presentaciones
{
    /// <summary>Presentación → factor (solo las activas de la empresa en contexto).</summary>
    public static async Task<Dictionary<Guid, decimal>> FactoresAsync(ContextoPlataforma db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var lista = ids.Distinct().ToList();
        if (lista.Count == 0) return [];
        return await db.ProductoPresentaciones.AsNoTracking()
            .Where(x => lista.Contains(x.Id) && x.Activo)
            .ToDictionaryAsync(x => x.Id, x => x.Factor, ct);
    }
}