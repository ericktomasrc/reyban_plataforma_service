using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Planes;

/// <summary>
/// 069 — LO QUE EL PLAN DE UNA EMPRESA NO TRAE.
///
/// La base calcula, con <c>planes_rutas(empresa)</c>, los prefijos bloqueados y
/// los permitidos (de las pantallas y de la API). Gana el prefijo MÁS LARGO:
/// así «Stock» (/api/inventario) puede estar en Pro y «Lotes»
/// (/api/inventario/lotes) en Max, y lo que la venta necesita del inventario
/// (/api/inventario/disponible) sigue libre en el Básico.
///
/// Con los planes apagados, con acceso total o sin empresa, no se bloquea nada.
/// </summary>
public static class ReglasPlan
{
    /// <summary>
    /// Las raíces de la API que el armador puede mover de plan. Lo demás
    /// (comprobantes, clientes, productos, sucursales, personal y
    /// configuración van siempre en Básico; sesión, cartera, administración)
    /// no se revisa: no cuesta una consulta.
    /// </summary>
    private static readonly string[] Raices =
    [
        "/api/guias", "/api/compras", "/api/inventario", "/api/pos", "/api/reportes"
    ];

    public static bool TocaRevisar(PathString ruta) =>
        Raices.Any(r => ruta.StartsWithSegments(r, StringComparison.OrdinalIgnoreCase));

    public sealed record Bloqueo(string Minimo, string Efectivo);

    /// <summary>Lo que dice la base para esta empresa (JSON de planes_rutas).</summary>
    public static async Task<JsonElement> RutasAsync(ContextoPlataforma db, Guid empresa, CancellationToken ct)
    {
        var texto = await db.Database
            .SqlQuery<string>($"SELECT planes_rutas({empresa})::text AS \"Value\"")
            .FirstAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(texto);
    }

    /// <summary>Null si la ruta de la API está en su plan; si no, el plan que la trae.</summary>
    public static async Task<Bloqueo?> BloqueoApiAsync(ContextoPlataforma db, Guid empresa, string ruta, CancellationToken ct)
    {
        var r = await RutasAsync(db, empresa, ct);
        var efectivo = r.TryGetProperty("plan", out var p) ? p.GetString() ?? "basico" : "basico";
        if (efectivo == "todo") return null;

        var bloqueada = Lista(r, "api");
        if (bloqueada.Count == 0) return null;

        var (prefijo, bloquea) = MasLargo(ruta, bloqueada, Lista(r, "libresApi"));
        if (!bloquea || prefijo is null) return null;

        var minimo = r.TryGetProperty("minimos", out var m) && m.TryGetProperty(prefijo, out var x)
            ? x.GetString() ?? "sin" : "sin";
        return new Bloqueo(minimo, efectivo);
    }

    /// <summary>El prefijo más largo que coincide, y si es de los bloqueados.</summary>
    public static (string? Prefijo, bool Bloquea) MasLargo(string ruta, IReadOnlyList<string> bloqueadas, IReadOnlyList<string> libres)
    {
        string? mejor = null;
        var bloquea = false;
        foreach (var (lista, valor) in new[] { (bloqueadas, true), (libres, false) })
            foreach (var prefijo in lista)
                if (Coincide(ruta, prefijo) && (mejor is null || prefijo.Length > mejor.Length))
                {
                    mejor = prefijo;
                    bloquea = valor;
                }
        return (mejor, bloquea);
    }

    /// <summary>/api/inventario coincide con /api/inventario y /api/inventario/123, no con /api/inventarios.</summary>
    public static bool Coincide(string ruta, string prefijo)
    {
        if (!ruta.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase)) return false;
        return ruta.Length == prefijo.Length || ruta[prefijo.Length] == '/' || prefijo.EndsWith('/');
    }

    private static List<string> Lista(JsonElement r, string nombre) =>
        r.TryGetProperty(nombre, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];

    /// <summary>El nombre que ve la gente.</summary>
    public static string Nombre(string? plan) => plan switch
    {
        "basico" => "Básico",
        "pro"    => "Pro",
        "max"    => "Max",
        "todo"   => "Todo",
        _        => "ningún plan todavía"
    };

    /// <summary>
    /// Lo que dicen las funciones de la 069 cuando algo no se puede
    /// («PLAN_…: mensaje»). Null si el error no es de planes.
    /// </summary>
    public static string? Mensaje(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is not PostgresException pg || !pg.MessageText.StartsWith("PLAN_", StringComparison.Ordinal)) continue;
            var i = pg.MessageText.IndexOf(':');
            return i > 0 ? pg.MessageText[(i + 1)..].Trim() : "Tu plan no lo permite.";
        }
        return null;
    }

    /// <summary>¿El error es «tu plan no lo trae» (402) y no un dato mal puesto (400)?</summary>
    public static bool EsDelPlan(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is PostgresException pg && pg.MessageText.StartsWith("PLAN_SUCURSALES", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Al agregar una empresa a la Cartera: null si cabe en su cupo (o no
    /// aplica); si no, lo que hay que decirle antes de confirmar.
    /// </summary>
    public static async Task<JsonElement?> CupoAlAgregarAsync(ContextoPlataforma db, CancellationToken ct)
    {
        var texto = await db.Database
            .SqlQuery<string>($"SELECT COALESCE(planes_cupo_al_agregar()::text, 'null') AS \"Value\"")
            .FirstAsync(ct);
        var r = JsonSerializer.Deserialize<JsonElement>(texto);
        return r.ValueKind == JsonValueKind.Object ? r : null;
    }

    /// <summary>
    /// 084: lo que costaría subir la Cartera a <paramref name="cupos"/> empresas
    /// a mitad de un periodo pagado (se suma al próximo cobro).
    /// </summary>
    public static async Task<JsonElement> DiferenciaCuposAsync(ContextoPlataforma db, int cupos, CancellationToken ct)
    {
        var texto = await db.Database
            .SqlQuery<string>($"SELECT planes_cotizar_cupos({cupos})::text AS \"Value\"")
            .FirstAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(texto);
    }

    /// <summary>084: la frase de la diferencia, o null si no se cobra nada.</summary>
    public static string? TextoDiferencia(JsonElement? d)
    {
        if (d is not { ValueKind: JsonValueKind.Object } x ||
            !x.TryGetProperty("cobrar", out var c) || c.ValueKind != JsonValueKind.True)
            return null;
        var monto = x.GetProperty("monto").GetDecimal();
        var dias = x.GetProperty("dias").GetInt32();
        return $"Por {(dias == 1 ? "el día que queda" : $"los {dias} días que quedan")} de tu periodo ya pagado se suman " +
               $"S/ {monto.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} a tu próximo cobro.";
    }
}