using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>063: un atributo con sus valores: «TALLA» → S, M, L.</summary>
public record AtributoVariante(string Nombre, List<string> Valores);

/// <summary>063: crear las combinaciones que faltan de un modelo.</summary>
public record PeticionGenerarVariantes(List<AtributoVariante> Atributos, decimal? PrecioUnitario = null);

/// <summary>
/// 063 — VARIANTES (inventario ampliado D3): talla, color…
///
/// Un modelo («POLO BÁSICO») agrupa variantes («POLO BÁSICO · M / ROJO»). Cada
/// variante es un producto de verdad (código, precio, stock, kardex), así que
/// la factura, la guía, el POS y los reportes no cambian. Aquí: ver las
/// variantes de un modelo y crear de golpe las combinaciones que faltan.
/// </summary>
public static class Variantes
{
    public const int MaxCombinaciones = 200;

    /// <summary>{"TALLA":"M"} → diccionario (o null).</summary>
    public static Dictionary<string, string>? Leer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); } catch { return null; }
    }

    /// <summary>La combinación como la guarda la base: claves en orden, en mayúsculas.</summary>
    public static string Json(IEnumerable<KeyValuePair<string, string>> valores) =>
        JsonSerializer.Serialize(valores.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value));

    /// <summary>«ROJO OSCURO» → «ROJOOSC» (sin tildes ni espacios, hasta 6), para el código.</summary>
    public static string Corto(string v)
    {
        var d = v.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var b = new StringBuilder();
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch)) b.Append(ch);
        var t = b.ToString();
        return t.Length > 6 ? t[..6] : t;
    }

    public static void MapearVariantes(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/productos/{modeloId:guid}/variantes")
                         .WithTags("Facturación · productos");

        grupo.MapGet("/", async (Guid modeloId, ContextoPlataforma db, CancellationToken ct) =>
        {
            var filas = await db.Productos.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.PadreId == modeloId)
                .OrderBy(p => p.Descripcion)
                .Select(p => new { p.Id, p.Codigo, p.Descripcion, p.PrecioUnitario, p.IncluyeIgv, p.Activo, p.ControlaStock, p.Atributos })
                .ToListAsync(ct);
            var ids = filas.Select(f => f.Id).ToList();
            // El stock de cada una, sumado en todas las sucursales.
            var stock = await db.Database.SqlQuery<StockVariante>($"""
                SELECT producto_id, sum(fisico - reservado) AS disponible
                  FROM inv_stock WHERE producto_id = ANY ({ids.ToArray()}) GROUP BY producto_id
                """).ToDictionaryAsync(x => x.ProductoId, x => x.Disponible, ct);
            return Results.Ok(filas.Select(f => new
            {
                f.Id, f.Codigo, f.Descripcion, f.PrecioUnitario, f.IncluyeIgv, f.Activo, f.ControlaStock,
                atributos = Leer(f.Atributos),
                disponible = stock.TryGetValue(f.Id, out var d) ? d : (decimal?)null
            }));
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las variantes de un modelo (con su stock total)");

        grupo.MapPost("/generar", async (Guid modeloId, PeticionGenerarVariantes p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var modelo = await db.Productos.FirstOrDefaultAsync(x => x.Id == modeloId, ct);
            if (modelo is null) return Results.NotFound();
            if (!modelo.EsModelo) return Results.BadRequest(new { error = "Marca el producto como «tiene variantes» primero." });

            // Los atributos: nombre y valores en mayúsculas, sin repetir.
            var atributos = (p.Atributos ?? [])
                .Select(a => new
                {
                    Nombre = (a.Nombre ?? "").Trim().ToUpperInvariant(),
                    Valores = (a.Valores ?? []).Select(v => (v ?? "").Trim().ToUpperInvariant()).Where(v => v.Length > 0)
                                               .Distinct().ToList()
                })
                .Where(a => a.Nombre.Length > 0 && a.Valores.Count > 0).ToList();
            if (atributos.Count == 0) return Results.BadRequest(new { error = "Escribe al menos un atributo con sus valores (por ejemplo, Talla: S, M, L)." });
            if (atributos.Count > 3) return Results.BadRequest(new { error = "Hasta 3 atributos (por ejemplo talla, color y material)." });
            if (atributos.GroupBy(a => a.Nombre).Any(g => g.Count() > 1)) return Results.BadRequest(new { error = "Un atributo está dos veces." });
            if (atributos.Any(a => a.Nombre.Length > 20 || a.Valores.Any(v => v.Length > 30)))
                return Results.BadRequest(new { error = "El nombre del atributo, hasta 20 letras; cada valor, hasta 30." });
            var total = atributos.Aggregate(1, (n, a) => n * a.Valores.Count);
            if (total > MaxCombinaciones) return Results.BadRequest(new { error = $"Salen {total} combinaciones: el máximo es {MaxCombinaciones}." });
            if (p.PrecioUnitario is < 0) return Results.BadRequest(new { error = "El precio no puede ser negativo." });

            // Todas las combinaciones.
            IEnumerable<List<KeyValuePair<string, string>>> combos = [[]];
            foreach (var a in atributos)
                combos = combos.SelectMany(c => a.Valores.Select(v => c.Append(new KeyValuePair<string, string>(a.Nombre, v)).ToList())).ToList();

            var existentes = (await db.Productos.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.PadreId == modeloId && x.Activo).Select(x => x.Atributos).ToListAsync(ct))
                .Select(Leer).Where(x => x is not null)
                .Select(x => Json(x!)).ToHashSet();
            var codigos = (await db.Productos.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.EmpresaId == modelo.EmpresaId).Select(x => x.Codigo.ToUpper()).ToListAsync(ct)).ToHashSet();

            var creadas = 0;
            foreach (var c in combos)
            {
                var json = Json(c);
                if (existentes.Contains(json)) continue;
                var codigo = $"{modelo.Codigo}-{string.Join("-", c.Select(x => Corto(x.Value)))}";
                if (codigo.Length > 30) codigo = codigo[..30];
                var base_ = codigo; var n = 2;
                while (codigos.Contains(codigo.ToUpperInvariant())) codigo = $"{(base_.Length > 27 ? base_[..27] : base_)}-{n++}";
                codigos.Add(codigo.ToUpperInvariant());
                var desc = $"{modelo.Descripcion} · {string.Join(" / ", c.Select(x => x.Value))}";
                db.Productos.Add(new Producto
                {
                    EmpresaId = modelo.EmpresaId, Codigo = codigo,
                    Descripcion = desc.Length > 250 ? desc[..250] : desc,
                    UnidadMedida = modelo.UnidadMedida, TipoAfectacion = modelo.TipoAfectacion,
                    PrecioUnitario = p.PrecioUnitario ?? modelo.PrecioUnitario, IncluyeIgv = modelo.IncluyeIgv,
                    CodigoSunat = modelo.CodigoSunat, CategoriaId = modelo.CategoriaId, Costo = modelo.Costo,
                    ControlaStock = true, PadreId = modelo.Id, Atributos = json
                });
                creadas++;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                creadas,
                mensaje = creadas == 0 ? "Ya estaban todas las combinaciones." : $"Se crearon {creadas} variante(s). Cada una tiene su código, su precio y su stock."
            });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Crear las variantes que faltan de un modelo (todas las combinaciones)");
    }

    /// <summary>Para SqlQuery: EF busca las columnas en snake_case (producto_id, disponible).</summary>
    public sealed class StockVariante
    {
        public Guid ProductoId { get; set; }
        public decimal Disponible { get; set; }
    }
}