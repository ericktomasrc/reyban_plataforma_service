using Npgsql;

namespace Plataforma.Api.Arranque;

/// <summary>
/// e180 — LA CADENA DE CONEXIÓN, AJUSTADA PARA QUE NO HAYA ESPERAS.
///
/// LO QUE SE MIDIÓ (25 oct, en local): el 77 % de las llamadas respondía en
/// menos de 0,2 s, pero las demás tardaban 2 o 4 segundos EXACTOS, fueran
/// reportes pesados o consultas de una fila. Esa escalera es el costo de ABRIR
/// una conexión nueva con la base (en Windows con Docker, unos 2 s cada una),
/// no de las consultas. Pasaba al entrar, después de un rato sin uso (el pool
/// cierra las conexiones quietas) y cuando una pantalla pide varias cosas a la
/// vez (hacen falta varias conexiones juntas).
///
/// EL ARREGLO:
///   · Minimum Pool Size (10 por defecto): esas conexiones no se cierran nunca.
///   · Se abren todas al arrancar la API (`CalentarAsync`), no en medio de una
///     pantalla.
///   · «localhost» pasa a 127.0.0.1: así no se intenta primero por IPv6.
/// Lo que ya venga escrito en la cadena se respeta.
/// </summary>
public static class CadenaConexion
{
    public static string Ajustada(string cadena)
    {
        var b = new NpgsqlConnectionStringBuilder(cadena);
        var escrito = cadena.ToLowerInvariant();

        if (!escrito.Contains("minimum pool size") && !escrito.Contains("minpoolsize") && !escrito.Contains("min pool size"))
            b.MinPoolSize = int.TryParse(Environment.GetEnvironmentVariable("PLATAFORMA_BD_CONEXIONES_MINIMAS"), out var n) && n is >= 0 and <= 50 ? n : 10;

        if (string.Equals(b.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            b.Host = "127.0.0.1";

        return b.ConnectionString;
    }

    /// <summary>Abre las conexiones mínimas al arrancar (en paralelo) y las deja en el pool.</summary>
    public static async Task CalentarAsync(string cadena, ILogger log, CancellationToken ct = default)
    {
        var b = new NpgsqlConnectionStringBuilder(cadena);
        var cuantas = Math.Max(1, b.MinPoolSize);
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var abiertas = new List<NpgsqlConnection>();
            try
            {
                await Task.WhenAll(Enumerable.Range(0, cuantas).Select(async _ =>
                {
                    var c = new NpgsqlConnection(cadena);
                    await c.OpenAsync(ct);
                    lock (abiertas) abiertas.Add(c);
                }));
            }
            finally
            {
                foreach (var c in abiertas) await c.DisposeAsync();   // vuelven al pool, abiertas
            }
            log.LogInformation("Base de datos: {Cuantas} conexiones listas en {Ms} ms.", cuantas, reloj.ElapsedMilliseconds);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // No impide arrancar: las conexiones se abrirán cuando hagan falta.
            log.LogWarning(e, "No se pudieron abrir las conexiones de antemano.");
        }
    }
}