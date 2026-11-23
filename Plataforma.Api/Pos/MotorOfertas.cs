using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Pos;

/// <summary>
/// e279 (094) — EL MOTOR DE LAS OFERTAS: qué descuento lleva cada línea de una
/// venta del punto de venta.
///
/// EL MISMO CÁLCULO VIVE EN LA WEB (src/modulos/pos/ofertas.ts): el punto de
/// venta lo usa para mostrar el total antes de cobrar y la API lo repite al
/// cobrar (es la que manda). Si se cambia uno, hay que cambiar el otro.
///
/// Todo en ENTEROS para que los dos den lo mismo: precios en céntimos con IGV
/// y cantidades en diezmilésimas (las cuatro decimales de la cantidad).
///
///   1. Solo entran las líneas de un producto, por unidad (sin presentación),
///      sin descuento a mano y gravadas, exoneradas o inafectas (no regalos).
///   2. Las ofertas se aplican en el orden que trae ofertas_activas (lote,
///      pack, lleva varios, por cantidad). Una unidad entra en una sola oferta.
///   3. El descuento de un grupo (pack o «lleva varios») se reparte según lo
///      que vale cada línea; el sobrante de céntimos va a la primera.
///   4. El descuento con IGV de cada línea se pasa al descuento sin IGV del
///      comprobante buscando, céntimo a céntimo, el que da justo el total.
/// </summary>
public static class MotorOfertas
{
    public sealed record ItemOferta(Guid ProductoId, decimal Cantidad);
    public sealed record OfertaActiva(Guid Id, string Nombre, string Tipo, decimal Precio, string? Lote, decimal? Restante, List<ItemOferta> Productos);

    /// <summary>Lo que una oferta hizo en la venta (para oferta_ventas).</summary>
    public sealed record Uso(Guid OfertaId, decimal Veces, decimal Cobrado, decimal Descuento);

    public sealed record Resultado(List<PeticionLinea> Lineas, List<Uso> Usos);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<List<OfertaActiva>> ActivasAsync(ContextoPlataforma db, Guid sucursal, CancellationToken ct)
    {
        var json = await db.Database.SqlQuery<string>($"SELECT ofertas_activas({sucursal})::text AS \"Value\"").FirstAsync(ct);
        return JsonSerializer.Deserialize<List<OfertaActiva>>(json, Json) ?? [];
    }

    private const long U = 10_000;   // una unidad, en diezmilésimas

    private sealed class Linea
    {
        public int Indice;
        public Guid ProductoId;
        public string? Lote;
        public long Precio;      // céntimos con IGV, por unidad
        public long Resto;       // diezmilésimas que todavía no entraron en una oferta
        public long Descuento;   // céntimos con IGV
        public List<string> Nombres = [];
    }

    private sealed record Toma(Linea L, long Cant, long Normal);

    /// <summary>El precio con IGV, en céntimos, de una línea (lo que se ve en el punto de venta).</summary>
    /// El valor unitario llega con 6 decimales: se pasa a millonésimas (entero) y de ahí a céntimos.
    public static long PrecioCentimos(decimal valorUnitario, string afectacion)
    {
        var k = (long)Math.Round(valorUnitario * 1_000_000m, 0, MidpointRounding.AwayFromZero);
        return afectacion == "10" ? (k * 118 + 500_000) / 1_000_000 : (k + 5_000) / 10_000;
    }

    private static long Cant4(decimal c) => (long)Math.Round(c * U, 0, MidpointRounding.AwayFromZero);
    private static long Valor(long cant4, long precio) => (cant4 * precio + U / 2) / U;

    public static Resultado Aplicar(IReadOnlyList<PeticionLinea> lineas, IReadOnlyList<OfertaActiva> ofertas)
    {
        var elegibles = new List<Linea>();
        for (var i = 0; i < lineas.Count; i++)
        {
            var l = lineas[i];
            var afect = (l.TipoAfectacion ?? "10").Trim();
            if (l.ProductoId is not { } pid || l.PresentacionId is not null || l.Descuento != 0
                || afect is not ("10" or "20" or "30") || l.Cantidad <= 0 || l.ValorUnitario <= 0) continue;
            elegibles.Add(new Linea
            {
                Indice = i, ProductoId = pid, Precio = PrecioCentimos(l.ValorUnitario, afect), Resto = Cant4(l.Cantidad),
                Lote = string.IsNullOrWhiteSpace(l.Lote) ? null : l.Lote.Trim().ToUpperInvariant(),
            });
        }

        var usos = new List<Uso>();
        foreach (var o in ofertas)
        {
            if (elegibles.Count == 0) break;
            if (o.Productos is not { Count: > 0 }) continue;
            var precio = Cant4(o.Precio) / 100;   // céntimos
            var resto4 = o.Restante is { } r ? Cant4(r) : long.MaxValue;
            if (resto4 <= 0) continue;
            switch (o.Tipo)
            {
                case "lote":
                {
                    var p = o.Productos[0].ProductoId;
                    var lote = o.Lote?.Trim().ToUpperInvariant();
                    long veces = 0, cobrado = 0, desc = 0;
                    foreach (var l in elegibles.Where(x => x.ProductoId == p && x.Lote == lote && x.Resto > 0))
                    {
                        var t = Math.Min(l.Resto, resto4 - veces);
                        if (t <= 0) break;
                        var normal = Valor(t, l.Precio);
                        var d = normal - Valor(t, precio);
                        if (d <= 0) continue;
                        l.Resto -= t; l.Descuento += d; Nombrar(l, o.Nombre);
                        veces += t; cobrado += normal - d; desc += d;
                    }
                    if (veces > 0) usos.Add(new Uso(o.Id, veces / (decimal)U, cobrado / 100m, desc / 100m));
                    break;
                }
                case "minimo":
                {
                    var p = o.Productos[0].ProductoId;
                    var min4 = Cant4(o.Productos[0].Cantidad);
                    var suyas = elegibles.Where(x => x.ProductoId == p && x.Resto > 0).ToList();
                    var hay = Math.Min(suyas.Sum(x => x.Resto), resto4);
                    if (hay < min4) break;
                    long veces = 0, cobrado = 0, desc = 0;
                    foreach (var l in suyas)
                    {
                        var t = Math.Min(l.Resto, hay - veces);
                        if (t <= 0) break;
                        var normal = Valor(t, l.Precio);
                        var d = normal - Valor(t, precio);
                        if (d <= 0) continue;
                        l.Resto -= t; l.Descuento += d; Nombrar(l, o.Nombre);
                        veces += t; cobrado += normal - d; desc += d;
                    }
                    if (veces > 0) usos.Add(new Uso(o.Id, veces / (decimal)U, cobrado / 100m, desc / 100m));
                    break;
                }
                case "cantidad":
                case "pack":
                {
                    // Cuántos grupos completos alcanzan (unidades enteras).
                    long k = resto4 == long.MaxValue ? long.MaxValue : resto4 / U;
                    foreach (var it in o.Productos)
                    {
                        var n = Cant4(it.Cantidad);
                        if (n <= 0) { k = 0; break; }
                        var hay = elegibles.Where(x => x.ProductoId == it.ProductoId).Sum(x => x.Resto);
                        k = Math.Min(k, hay / n);
                    }
                    if (k <= 0 || k == long.MaxValue) break;
                    var tomas = new List<Toma>();
                    foreach (var it in o.Productos)
                    {
                        var falta = k * Cant4(it.Cantidad);
                        foreach (var l in elegibles.Where(x => x.ProductoId == it.ProductoId && x.Resto > 0))
                        {
                            if (falta <= 0) break;
                            var t = Math.Min(l.Resto, falta - tomas.Where(x => x.L == l).Sum(x => x.Cant));
                            if (t <= 0) continue;
                            tomas.Add(new Toma(l, t, Valor(t, l.Precio)));
                            falta -= t;
                        }
                    }
                    var total = tomas.Sum(x => x.Normal);
                    var dsc = total - k * precio;
                    if (dsc <= 0 || total <= 0) break;
                    long repartido = 0;
                    var partes = tomas.Select(x => { var d = dsc * x.Normal / total; repartido += d; return d; }).ToList();
                    partes[0] += dsc - repartido;
                    for (var i = 0; i < tomas.Count; i++)
                    {
                        tomas[i].L.Resto -= tomas[i].Cant;
                        tomas[i].L.Descuento += partes[i];
                        Nombrar(tomas[i].L, o.Nombre);
                    }
                    usos.Add(new Uso(o.Id, k, (total - dsc) / 100m, dsc / 100m));
                    break;
                }
            }
        }

        // El descuento sin IGV va con dos decimales: a veces una línea no puede dar
        // justo su total (S/ 10 al 20 % da 8.01 o 7.99). Se elige lo más cercano y,
        // si la venta queda un céntimo arriba o abajo, se compensa en otra línea con
        // oferta, para que el total de la venta sea justo (el pack, S/ 20.00).
        var salida = lineas.ToList();
        var con = elegibles.Where(x => x.Descuento > 0).ToList();
        var opciones = con.Select(l => Opciones(salida[l.Indice], l.Descuento)).ToList();
        var elegida = opciones.Select(o => Enumerable.Range(0, o.Count).OrderBy(k => Math.Abs(o[k].Dif)).ThenBy(k => k).First()).ToList();
        var resto = Enumerable.Range(0, con.Count).Sum(i => opciones[i][elegida[i]].Dif);
        for (var vuelta = 0; vuelta < 50 && resto != 0; vuelta++)
        {
            (long Queda, long Dif, int I, int K)? mejor = null;
            for (var i = 0; i < con.Count; i++)
                for (var k = 0; k < opciones[i].Count; k++)
                {
                    if (k == elegida[i]) continue;
                    var queda = resto - opciones[i][elegida[i]].Dif + opciones[i][k].Dif;
                    if (Math.Abs(queda) >= Math.Abs(resto)) continue;
                    var c = (Math.Abs(queda), Math.Abs(opciones[i][k].Dif), i, k);
                    if (mejor is null || c.CompareTo(mejor.Value) < 0) mejor = c;
                }
            if (mejor is not { } m) break;
            resto = resto - opciones[m.I][elegida[m.I]].Dif + opciones[m.I][m.K].Dif;
            elegida[m.I] = m.K;
        }
        for (var i = 0; i < con.Count; i++)
        {
            var l = con[i];
            var p = salida[l.Indice];
            salida[l.Indice] = p with
            {
                Descuento = opciones[i][elegida[i]].Descuento,
                Descripcion = Recortar($"{p.Descripcion.Trim()} (OFERTA: {string.Join(", ", l.Nombres).ToUpperInvariant()})", 500),
            };
        }
        return new Resultado(salida, usos);
    }

    private static void Nombrar(Linea l, string nombre) { if (!l.Nombres.Contains(nombre)) l.Nombres.Add(nombre); }

    private static string Recortar(string s, int n) => s.Length > n ? s[..n] : s;

    private static decimal TotalCon(PeticionLinea p, decimal descuento) =>
        CalculoPrevio.Calcular(new DocumentoLinea
        {
            Cantidad = p.Cantidad, ValorUnitario = p.ValorUnitario, Descuento = descuento,
            TipoAfectacion = (p.TipoAfectacion ?? "10").Trim(),
        }).ImporteTotal;

    /// <summary>Un descuento sin IGV posible y cuántos céntimos se aleja del total buscado.</summary>
    public sealed record Opcion(decimal Descuento, long Dif);

    /// <summary>
    /// Los descuentos sin IGV (dos decimales) cerca del que da justo el total con
    /// oferta, en este orden: el calculado, +1, −1, +2, −2… céntimos.
    /// </summary>
    public static List<Opcion> Opciones(PeticionLinea p, long descuentoCentimos)
    {
        var afect = (p.TipoAfectacion ?? "10").Trim();
        // Exonerado o inafecto: sin IGV, el descuento es justo lo que se rebaja.
        if (afect != "10") return [new Opcion(descuentoCentimos / 100m, 0)];
        var meta = TotalCon(p, 0) - descuentoCentimos / 100m;
        var c = Math.Round(descuentoCentimos / 100m / 1.18m, 2, MidpointRounding.AwayFromZero);
        var bruto = p.Cantidad * p.ValorUnitario;
        var salida = new List<Opcion>();
        foreach (var paso in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5 })
        {
            var d = c + paso * 0.01m;
            if (d <= 0 || d >= bruto) continue;
            salida.Add(new Opcion(d, (long)Math.Round((TotalCon(p, d) - meta) * 100m)));
        }
        if (salida.Count == 0)
        {
            var d = c > 0 ? c : 0.01m;
            salida.Add(new Opcion(d, (long)Math.Round((TotalCon(p, d) - meta) * 100m)));
        }
        return salida;
    }
}