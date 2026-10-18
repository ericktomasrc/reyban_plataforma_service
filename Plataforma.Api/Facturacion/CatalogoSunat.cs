using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Plataforma.Api.Facturacion;

/// <summary>Una fila del catálogo 25: un producto o una clase entera.</summary>
public sealed record CodigoSunat(
    string Codigo,
    string Descripcion,
    string? Clase,
    string? ClaseDescripcion);


/// <summary>
/// El catálogo 25 de SUNAT (UNSPSC v14): el «código de producto SUNAT».
///
/// ══════════════════════════════════════════════════════════════════════════
/// QUÉ ES: una lista oficial de ocho dígitos en cuatro niveles —segmento,
/// familia, clase, producto—. SUNAT admite desde el nivel CLASE (los que
/// acaban en 00) hacia abajo, y eso es lo que hay aquí: 3 818 clases y
/// 49 022 productos. Segmentos y familias no están porque no valen.
///
/// POR QUÉ ESTÁ EN LA PLATAFORMA: nadie se sabe estos códigos. La gente busca
/// «azúcar» y elige. Y al guardar un producto se comprueba que el código
/// exista: desde el 1 de enero de 2027 un código que no está en la lista hace
/// rechazar el comprobante (regla 3496).
///
/// EN MEMORIA, Y SE CARGA UNA SOLA VEZ: son unos 2.7 MB de texto, que se leen
/// la primera vez que alguien busca. No hay tabla en la base porque es una
/// lista de SUNAT, igual para todas las empresas, que cambia una vez cada
/// varios años.
///
/// SI NO SE PUDIERA CARGAR —no debería pasar: viaja dentro del código—, la
/// búsqueda dice que no está disponible y al guardar solo se mira que sean
/// ocho dígitos, como antes. No se bloquea a nadie por esto.
/// ══════════════════════════════════════════════════════════════════════════
/// </summary>
public static class CatalogoSunat
{
    private sealed record Fila(CodigoSunat Codigo, string[] Palabras, string[] Raices);

    private sealed class Datos
    {
        public required Fila[] Filas { get; init; }
        public required Dictionary<string, Fila> PorCodigo { get; init; }
    }

    private static readonly Lazy<Datos?> Cargado =
        new(Cargar, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Por qué no se pudo cargar, si no se pudo. Para el log.</summary>
    public static string? ErrorDeCarga { get; private set; }

    public static bool Disponible => Cargado.Value is not null;

    public static int Cantidad => Cargado.Value?.Filas.Length ?? 0;

    /// <summary>¿Está este código en la lista? (Sin catálogo cargado: no se sabe, y se dice que sí.)</summary>
    public static bool Existe(string codigo) =>
        Cargado.Value is not { } d || d.PorCodigo.ContainsKey(codigo);

    /// <summary>Cómo llama SUNAT a ese código, o null si no está.</summary>
    public static string? Nombre(string? codigo) =>
        codigo is not null && Cargado.Value is { } d && d.PorCodigo.TryGetValue(codigo, out var f)
            ? f.Codigo.Descripcion
            : null;


    /// <summary>
    /// Busca por lo que ES el producto: «azúcar», «cemento», «flete».
    ///
    /// CÓMO ORDENA. Cada palabra buscada tiene que estar en la descripción
    /// —si una falta, la fila no sale—. Puntúa más la palabra entera
    /// («pan» en «Pan fresco») que el comienzo de otra («pan» en «Pantalones»),
    /// y algo más si la descripción empieza por ella. A igual puntuación, la
    /// descripción más corta primero: «Cemento» antes que «Aceleradores de
    /// cemento».
    ///
    /// SIN TILDES NI PLURALES: «azucar» encuentra «Azúcares». Es lo que la
    /// gente escribe.
    ///
    /// Con 8 dígitos, devuelve ese código (si existe).
    /// </summary>
    public static IReadOnlyList<CodigoSunat> Buscar(string? q, int maximo = 20)
    {
        if (Cargado.Value is not { } d || string.IsNullOrWhiteSpace(q)) return [];

        var texto = q.Trim();

        if (texto.Length == 8 && texto.All(char.IsAsciiDigit))
            return d.PorCodigo.TryGetValue(texto, out var exacta) ? [exacta.Codigo] : [];

        var buscadas = Palabras(texto)
            .Where(p => !Vacias.Contains(p))
            .Select(Raiz)
            .Distinct()
            .ToArray();

        if (buscadas.Length == 0) return [];

        var halladas = new List<(int Puntos, int Largo, Fila Fila)>();

        foreach (var f in d.Filas)
        {
            var puntos = 0;
            var vale = true;

            foreach (var b in buscadas)
            {
                if (Array.IndexOf(f.Raices, b) >= 0) puntos += 3;
                else if (f.Palabras.Any(p => p.StartsWith(b, StringComparison.Ordinal))) puntos += 1;
                else { vale = false; break; }
            }

            if (!vale) continue;

            if (f.Raices.Length > 0 && f.Raices[0] == buscadas[0]) puntos += 2;

            halladas.Add((puntos, f.Palabras.Length, f));
        }

        return halladas
            .OrderByDescending(h => h.Puntos)
            .ThenBy(h => h.Largo)
            .ThenBy(h => h.Fila.Codigo.Codigo, StringComparer.Ordinal)
            .Take(maximo)
            .Select(h => h.Fila.Codigo)
            .ToList();
    }


    // ------------------------------------------------------------------ interno

    /// <summary>Palabras que no ayudan a buscar: «servicio DE transporte».</summary>
    private static readonly HashSet<string> Vacias =
    [
        "de", "la", "el", "y", "o", "para", "con", "en", "los", "las", "del",
        "a", "al", "un", "una", "por", "sin"
    ];

    /// <summary>Minúsculas, sin tildes, solo letras y números.</summary>
    internal static string[] Palabras(string texto)
    {
        var descompuesto = texto.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var limpio = new StringBuilder(descompuesto.Length);

        foreach (var c in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            limpio.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : ' ');
        }

        return limpio.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// El singular aproximado: «azucares» → «azucar», «ladrillos» → «ladrillo».
    /// No es gramática, es lo justo para que el plural no esconda resultados.
    /// </summary>
    internal static string Raiz(string p) =>
        p.Length > 4 && p.EndsWith("es", StringComparison.Ordinal) ? p[..^2]
        : p.Length > 3 && p.EndsWith('s') ? p[..^1]
        : p;

    private static Datos? Cargar()
    {
        try
        {
            var comprimido = Convert.FromBase64String(CatalogoSunatDatos.GzipBase64);

            using var entrada = new MemoryStream(comprimido);
            using var gzip = new GZipStream(entrada, CompressionMode.Decompress);
            using var lector = new StreamReader(gzip, Encoding.UTF8);

            var crudas = new List<string[]>();
            string? linea;

            while ((linea = lector.ReadLine()) is not null)
            {
                if (linea.Length == 0 || linea[0] == '#') continue;
                var c = linea.Split('\t');
                if (c.Length < 3 || c[0].Length != 8) continue;
                crudas.Add(c);
            }

            var nombres = crudas.ToDictionary(c => c[0], c => c[1]);

            var filas = crudas.Select(c =>
            {
                var clase = c[2].Length == 8 ? c[2] : null;
                var palabras = Palabras(c[1]);

                return new Fila(
                    new CodigoSunat(c[0], c[1], clase,
                        clase is not null && nombres.TryGetValue(clase, out var n) ? n : null),
                    palabras,
                    palabras.Select(Raiz).ToArray());
            }).ToArray();

            return new Datos
            {
                Filas = filas,
                PorCodigo = filas.ToDictionary(f => f.Codigo.Codigo)
            };
        }
        catch (Exception ex)
        {
            ErrorDeCarga = ex.Message;
            return null;
        }
    }
}