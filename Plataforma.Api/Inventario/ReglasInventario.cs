using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Inventario;

/// <summary>
/// 052 — LO QUE EL INVENTARIO LE DICE A LA FACTURACIÓN.
///
/// El stock lo mueve la base (inv_sync_documento, inv_sync_guia) al cambiar
/// de estado un comprobante o una guía. Aquí está lo que va ANTES, para que
/// quien emite lea en palabras qué falta y dónde hay:
///
///   · <see cref="RevisarAsync"/>: con la sucursal controlando y sin «permitir
///     vender sin stock», ¿alcanza lo disponible? Si no, un 409 que dice cuánto
///     pidió, cuánto hay aquí y cuánto en las otras sucursales.
///   · <see cref="SinStockDe"/>: si aun así la base lo para (dos personas
///     vendieron lo mismo en el mismo segundo), el mismo mensaje.
///
/// Con el inventario apagado, todo esto no hace nada.
/// </summary>
public static class ReglasInventario
{
    public sealed class Saldo
    {
        public Guid ProductoId { get; set; }
        public Guid SucursalId { get; set; }
        public decimal Fisico { get; set; }
        public decimal Reservado { get; set; }
    }

    public sealed record OtraSucursal(Guid SucursalId, string Codigo, string Nombre, decimal Disponible);

    public sealed record Falta(Guid ProductoId, string Descripcion, decimal Pedido, decimal Disponible, List<OtraSucursal> Otras);

    public static Task<InventarioConfig?> ConfigAsync(ContextoPlataforma db, Guid empresaId, CancellationToken ct) =>
        db.InventarioConfig.AsNoTracking().FirstOrDefaultAsync(x => x.EmpresaId == empresaId, ct);

    public static async Task<bool> BloqueaAsync(ContextoPlataforma db, Guid empresaId, Guid sucursalId, CancellationToken ct) =>
        await db.Database.SqlQuery<bool>($"SELECT inv_bloquea({empresaId}, {sucursalId}) AS \"Value\"").FirstAsync(ct);

    /// <summary>El saldo de unos productos en todas las sucursales de la empresa.</summary>
    public static Task<List<Saldo>> SaldosAsync(ContextoPlataforma db, Guid empresaId, List<Guid> productos, CancellationToken ct) =>
        productos.Count == 0
            ? Task.FromResult(new List<Saldo>())
            : db.Database.SqlQuery<Saldo>($"""
                SELECT producto_id, sucursal_id, fisico, reservado
                  FROM inv_stock
                 WHERE empresa_id = {empresaId} AND producto_id = ANY ({productos.ToArray()})
                """).ToListAsync(ct);

    /// <summary>
    /// ¿Alcanza? pedido = producto → cantidad (solo los que controlan stock se
    /// miran). Devuelve las que no alcanzan y si la sucursal bloquea.
    /// </summary>
    public static async Task<(List<Falta> Faltan, bool Bloquea)> RevisarAsync(
        ContextoPlataforma db, Guid empresaId, Guid sucursalId, Dictionary<Guid, decimal> pedido, CancellationToken ct)
    {
        var cfg = await ConfigAsync(db, empresaId, ct);
        if (cfg is not { Encendido: true } || pedido.Count == 0) return ([], false);

        var ids = pedido.Keys.ToList();
        var productos = await db.Productos.IgnoreQueryFilters().AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.ControlaStock)
            .Select(p => new { p.Id, p.Descripcion })
            .ToListAsync(ct);
        if (productos.Count == 0) return ([], false);

        var saldos = await SaldosAsync(db, empresaId, productos.Select(p => p.Id).ToList(), ct);
        var sucursales = await db.Sucursales.AsNoTracking()
            .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null)
            .Select(s => new { s.Id, s.Codigo, s.Nombre })
            .ToListAsync(ct);

        var faltan = new List<Falta>();
        foreach (var p in productos.OrderBy(p => p.Descripcion))
        {
            var aqui = saldos.FirstOrDefault(s => s.ProductoId == p.Id && s.SucursalId == sucursalId);
            var disponible = aqui is null ? 0 : aqui.Fisico - aqui.Reservado;
            if (pedido[p.Id] <= disponible) continue;

            var otras = saldos
                .Where(s => s.ProductoId == p.Id && s.SucursalId != sucursalId && s.Fisico - s.Reservado > 0)
                .Select(s => sucursales.FirstOrDefault(x => x.Id == s.SucursalId) is { } x
                    ? new OtraSucursal(x.Id, x.Codigo, x.Nombre, s.Fisico - s.Reservado) : null)
                .Where(x => x is not null).Select(x => x!)
                .OrderByDescending(x => x.Disponible)
                .ToList();
            faltan.Add(new Falta(p.Id, p.Descripcion, pedido[p.Id], disponible, otras));
        }

        return (faltan, faltan.Count > 0 && await BloqueaAsync(db, empresaId, sucursalId, ct));
    }

    /// <summary>Lo que pide un comprobante, por producto (solo líneas con producto).</summary>
    public static Dictionary<Guid, decimal> Pedido(IEnumerable<DocumentoLinea> lineas) =>
        lineas.Where(l => l.Activo && l.ProductoId is not null && l.Cantidad > 0)
              .GroupBy(l => l.ProductoId!.Value)
              .ToDictionary(g => g.Key, g => g.Sum(l => l.Cantidad));

    /// <summary>
    /// Antes de emitir una factura o boleta: si no alcanza y la sucursal
    /// bloquea, el 409 con el detalle. Null = adelante.
    /// </summary>
    public static async Task<IResult?> RevisarDocumentoAsync(ContextoPlataforma db, Documento d, CancellationToken ct)
    {
        if (d.Tipo is not ("01" or "03") || !d.SePuedeEditar) return null;
        var sucursal = d.SucursalId ?? (await Sucursales.PrincipalAsync(db, d.EmpresaId, ct)).Id;
        var (faltan, bloquea) = await RevisarAsync(db, d.EmpresaId, sucursal, Pedido(d.Lineas), ct);
        return bloquea ? await SinStockAsync(db, sucursal, faltan, ct) : null;
    }

    /// <summary>El 409 «no alcanza», con lo que hay en las otras sucursales.</summary>
    public static async Task<IResult> SinStockAsync(ContextoPlataforma db, Guid sucursalId, List<Falta> faltan, CancellationToken ct)
    {
        var aqui = await db.Sucursales.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == sucursalId).Select(s => s.Codigo + " " + s.Nombre).FirstOrDefaultAsync(ct) ?? "esta sucursal";

        var primera = faltan[0];
        var error = faltan.Count == 1
            ? $"No alcanza «{primera.Descripcion}»: pides {Num(primera.Pedido)} y en {aqui} hay {Num(primera.Disponible)} disponibles."
            : $"No alcanzan {faltan.Count} productos en {aqui}: " +
              string.Join("; ", faltan.Select(f => $"«{f.Descripcion}» pides {Num(f.Pedido)}, hay {Num(f.Disponible)}")) + ".";
        var otras = faltan.SelectMany(f => f.Otras.Select(o => $"{o.Codigo} {o.Nombre}: {Num(o.Disponible)} de «{f.Descripcion}»")).ToList();

        return Results.Json(new
        {
            error,
            detalle = (otras.Count > 0 ? "En otras sucursales: " + string.Join("; ", otras) + ". " : "") +
                      "Baja la cantidad, pide un traslado o emite desde la sucursal que tiene.",
            codigo = "sin_stock",
            faltan
        }, statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Si la excepción es el «no alcanza» de la base (INV_SIN_STOCK), el
    /// producto, la sucursal, lo disponible y lo pedido. Si no, null.
    /// </summary>
    public static (Guid Producto, Guid Sucursal, decimal Disponible, decimal Pedido)? SinStockDe(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is PostgresException pg && pg.MessageText.StartsWith("INV_SIN_STOCK|", StringComparison.Ordinal))
            {
                var p = pg.MessageText.Split('|');
                if (p.Length >= 5 && Guid.TryParse(p[1], out var prod) && Guid.TryParse(p[2], out var suc))
                    return (prod, suc,
                            decimal.TryParse(p[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var disp) ? disp : 0,
                            decimal.TryParse(p[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var ped) ? ped : 0);
            }
        }
        return null;
    }

    /// <summary>El 409 a partir del «no alcanza» de la base.</summary>
    public static async Task<IResult> SinStockAsync(ContextoPlataforma db, Guid empresaId,
        (Guid Producto, Guid Sucursal, decimal Disponible, decimal Pedido) s, CancellationToken ct)
    {
        var (faltan, _) = await RevisarAsync(db, empresaId, s.Sucursal, new() { [s.Producto] = s.Pedido }, ct);
        if (faltan.Count == 0)
        {
            var desc = await db.Productos.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Id == s.Producto).Select(p => p.Descripcion).FirstOrDefaultAsync(ct) ?? "el producto";
            faltan = [new Falta(s.Producto, desc, s.Pedido, s.Disponible, [])];
        }
        return await SinStockAsync(db, s.Sucursal, faltan, ct);
    }

    /// <summary>
    /// El mensaje de una regla de la base (INV_…) en palabras, o null si la
    /// excepción no es de inventario.
    /// </summary>
    public static string? Mensaje(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is not PostgresException pg || !pg.MessageText.StartsWith("INV_", StringComparison.Ordinal)) continue;
            var codigo = pg.MessageText.Split('|')[0];
            return codigo switch
            {
                "INV_SIN_STOCK"     => "No alcanza el stock disponible.",
                "INV_YA_INICIAL"    => "Ese producto ya tiene su stock inicial en esta sucursal. Para corregirlo, haz un ajuste o un conteo físico.",
                "INV_APAGADO"       => "El inventario de tu empresa está apagado. Enciéndelo en Inventario › Configuración.",
                "INV_NO_EXISTE"     => "No se encontró esa operación.",
                "INV_NO_ANULABLE"   => "Esa operación ya no se puede anular.",
                "INV_YA_RECIBIDO"   => "Ese traslado ya se recibió (o se anuló).",
                "INV_RECIBIDO_MAL"  => "Lo recibido no puede ser negativo ni más que lo enviado.",
                "INV_FALTA_MOTIVO"  => "Si llegó menos de lo enviado, elige qué pasó con lo que falta.",
                _                   => "El inventario no permitió la operación."
            };
        }
        return null;
    }

    /// <summary>
    /// Mueve YA el stock de las guías guardadas en esta transacción. El
    /// disparador de la guía va al final de la transacción (sus bienes se
    /// insertan después que ella); la transacción es la de toda la petición,
    /// así que sin esto correría al confirmar, después de contestar.
    /// </summary>
    public static async Task AplicarGuiasAsync(ContextoPlataforma db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS inv_guia IMMEDIATE", ct);
        await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS inv_guia DEFERRED", ct);
    }

    /// <summary>
    /// Corre algo que puede chocar con una regla del inventario en la base,
    /// dentro de un punto de guardado: si choca, se deshace SOLO eso y la
    /// petición sigue viva para contestar en palabras.
    /// </summary>
    public static async Task<Exception?> ConPuntoAsync(ContextoPlataforma db, Func<Task> accion, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction;
        const string punto = "inventario";
        if (tx is not null) await tx.CreateSavepointAsync(punto, ct);
        try
        {
            await accion();
            if (tx is not null) await tx.ReleaseSavepointAsync(punto, ct);
            return null;
        }
        catch (Exception e) when (Mensaje(e) is not null)
        {
            if (tx is not null) await tx.RollbackToSavepointAsync(punto, ct);
            return e;
        }
    }

    public sealed class FilaAplicado
    {
        public string Modo { get; set; } = "";
        public bool Aplicado { get; set; }
        public Guid SucursalId { get; set; }
        public string Sucursal { get; set; } = "";
    }

    public sealed class FilaReserva
    {
        public Guid ProductoId { get; set; }
        public string Descripcion { get; set; } = "";
        public decimal Reservada { get; set; }
        public decimal Despachada { get; set; }
        public decimal Liberada { get; set; }
    }

    /// <summary>
    /// Lo que el inventario hizo con un comprobante (para «Ver comprobante»):
    /// si salió del stock o quedó reservado, y cuánto falta despachar. Null si
    /// no tocó el inventario.
    /// </summary>
    public static async Task<object?> DelDocumentoAsync(ContextoPlataforma db, Documento d, CancellationToken ct)
    {
        var a = await db.Database.SqlQuery<FilaAplicado>($"""
            SELECT a.modo, a.aplicado, a.sucursal_id, s.codigo || ' ' || s.nombre AS sucursal
              FROM inv_aplicado a JOIN sucursales s ON s.id = a.sucursal_id
             WHERE a.fuente_id = {d.Id}
            """).FirstOrDefaultAsync(ct);
        if (a is null) return null;

        var reservas = a.Modo == "reserva"
            ? await db.Database.SqlQuery<FilaReserva>($"""
                SELECT r.producto_id, p.descripcion, r.reservada, r.despachada, r.liberada
                  FROM inv_reservas r JOIN productos p ON p.id = r.producto_id
                 WHERE r.documento_id = {d.Id}
                 ORDER BY p.descripcion
                """).ToListAsync(ct)
            : [];

        return new
        {
            a.Modo, a.Aplicado, a.SucursalId, a.Sucursal,
            reservas = reservas.Select(r => new
            {
                r.ProductoId, r.Descripcion, r.Reservada, r.Despachada, r.Liberada,
                pendiente = Math.Max(0, r.Reservada - r.Despachada - r.Liberada)
            })
        };
    }

    public static string Num(decimal n) =>
        n.ToString(n == Math.Truncate(n) ? "#,##0" : "#,##0.####", CultureInfo.GetCultureInfo("es-PE"));
}