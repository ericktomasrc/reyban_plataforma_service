using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Plataforma.Api.Errores;

/// <summary>
/// e225 — LEE LOS LOGS DE CADA DÍA Y SACA LOS ERRORES (8 nov 2026).
///
/// Los logs ya existían (038): una carpeta por día, un archivo por proceso.
///
///     logs/2026-11-08/plataforma-api.log      (carpeta de la plataforma)
///     logs/2026-11-08/servicio-api.log        (carpeta del servicio)
///     logs/2026-11-08/servicio-worker.log     (carpeta del servicio)
///
/// Cada entrada empieza con «2026-11-08 05:41:02.118 [ERR] Categoria: mensaje»
/// y sus líneas siguientes (el detalle, la excepción) van con 4 espacios.
///
/// DÓNDE BUSCA:
///   · Plataforma: LOGS_CARPETA, o «..\logs» junto al proyecto (como 038).
///   · Servicio:   LOGS_SERVICIO_CARPETA, o la carpeta logs del servicio de
///     facturación al lado (en local: ..\..\..\facturacion electronica\
///     facturacion_electronica_service\logs). En QA, docker la monta en
///     /app/logs-servicio, SOLO LECTURA.
///
/// Solo LEE: nunca escribe, mueve ni borra un log (los borra el propio
/// proceso a los 60 días). Los logs ya no llevan claves, cookies ni cuerpos
/// de petición (038).
///
/// LOS ERRORES SE AGRUPAN: el mismo error repetido 50 veces sale una vez con
/// «50 veces». Dos errores son «el mismo» si tienen la misma categoría y el
/// mismo mensaje cambiando números, GUID y fechas.
///
/// QUÉ ES UN ERROR: los [ERR] y [CRI], y los [AVI] que son un rechazo de
/// SUNAT (el worker los anota como aviso). El resto de [AVI] son avisos.
/// </summary>
public sealed class LectorErrores
{
    public sealed record Proceso(string Clave, string Nombre, string Archivo, bool DelServicio);

    public static readonly Proceso[] Procesos =
    [
        new("plataforma", "Plataforma", "plataforma-api.log", false),
        new("servicio", "Servicio (API)", "servicio-api.log", true),
        new("worker", "Worker (envíos a SUNAT)", "servicio-worker.log", true),
    ];

    public sealed record Entrada(DateTime Cuando, string Nivel, string Categoria, string Primera, string Texto);

    public sealed record Grupo(
        string Firma, string Titulo, string? Significado, int Veces,
        DateTime Primera, DateTime Ultima, string Categoria, string Ejemplo);

    public sealed record Conteo(int Errores, int Avisos, bool Existe);

    private static readonly Regex Cabeza = new(
        @"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \[([A-Z?]{3})\] ([^:]*?): ?(.*)$",
        RegexOptions.Compiled);

    private readonly string? _carpetaPlataforma;
    private readonly string? _carpetaServicio;

    // Conteos de un archivo, mientras no cambie (los días pasados no cambian).
    private readonly ConcurrentDictionary<string, (long Largo, DateTime Escrito, Conteo Conteo)> _cache = new();

    public LectorErrores(string? carpetaPlataforma, string? carpetaServicio)
    {
        _carpetaPlataforma = carpetaPlataforma;
        _carpetaServicio = carpetaServicio;
    }

    /// <summary>Las carpetas como las resuelve el sistema (ver arriba).</summary>
    public static LectorErrores DesdeEntorno(string raizContenido)
    {
        var propia = Environment.GetEnvironmentVariable("LOGS_CARPETA");
        var plataforma = Path.GetFullPath(string.IsNullOrWhiteSpace(propia)
            ? Path.Combine(raizContenido, "..", "logs")
            : propia.Trim());

        var delServicio = Environment.GetEnvironmentVariable("LOGS_SERVICIO_CARPETA");
        var servicio = Path.GetFullPath(string.IsNullOrWhiteSpace(delServicio)
            ? Path.Combine(raizContenido, "..", "..", "..", "facturacion electronica",
                           "facturacion_electronica_service", "logs")
            : delServicio.Trim());

        return new LectorErrores(plataforma, servicio);
    }

    public static Proceso? BuscarProceso(string? clave) =>
        Procesos.FirstOrDefault(p => p.Clave == clave);

    /// <summary>¿Se encuentra la carpeta de logs de este proceso?</summary>
    public bool CarpetaExiste(Proceso p)
    {
        var c = p.DelServicio ? _carpetaServicio : _carpetaPlataforma;
        return c is not null && Directory.Exists(c);
    }

    public string? RutaDe(DateOnly dia, Proceso p)
    {
        var c = p.DelServicio ? _carpetaServicio : _carpetaPlataforma;
        return c is null ? null : Path.Combine(c, dia.ToString("yyyy-MM-dd"), p.Archivo);
    }

    /// <summary>Cuántos errores y avisos tiene ese día. Rápido: no arma las entradas.</summary>
    public Conteo Contar(DateOnly dia, Proceso p)
    {
        var ruta = RutaDe(dia, p);
        if (ruta is null || !File.Exists(ruta)) return new Conteo(0, 0, false);

        var info = new FileInfo(ruta);
        if (_cache.TryGetValue(ruta, out var c) && c.Largo == info.Length && c.Escrito == info.LastWriteTimeUtc)
            return c.Conteo;

        int errores = 0, avisos = 0;
        foreach (var linea in Lineas(ruta))
        {
            // «2026-11-08 05:41:02.118 [ERR] …»: el nivel está en la columna 24.
            if (linea.Length < 29 || linea[23] != ' ' || linea[24] != '[') continue;
            var nivel = linea.Substring(25, 3);
            if (EsError(nivel, linea)) errores++;
            else if (nivel is "AVI") avisos++;
        }

        var conteo = new Conteo(errores, avisos, true);
        _cache[ruta] = (info.Length, info.LastWriteTimeUtc, conteo);
        return conteo;
    }

    /// <summary>
    /// ¿Cuenta como error? ERR y CRI, y también los avisos que son un RECHAZO
    /// DE SUNAT: el worker los anota como aviso (el sistema sigue bien), pero
    /// para quien mira son lo más importante del día.
    /// </summary>
    public static bool EsError(string nivel, string texto) =>
        nivel is "ERR" or "CRI" || (nivel == "AVI" && RechazoSunat.IsMatch(texto));

    private static readonly Regex RechazoSunat = new(
        @"RECHAZADO|no se pudo enviar|\[\d{4}\]", RegexOptions.Compiled);

    /// <summary>Las entradas de errores (ERR, CRI y rechazos de SUNAT) o de avisos (el resto de AVI) de ese día.</summary>
    public List<Entrada> Entradas(DateOnly dia, Proceso p, bool avisos)
    {
        var ruta = RutaDe(dia, p);
        var lista = new List<Entrada>();
        if (ruta is null || !File.Exists(ruta)) return lista;

        Entrada? actual = null;
        StringBuilder? texto = null;

        void Cerrar()
        {
            if (actual is null) return;
            var error = EsError(actual.Nivel, actual.Primera);
            var quiere = avisos ? actual.Nivel == "AVI" && !error : error;
            if (quiere) lista.Add(actual with { Texto = texto!.ToString() });
            actual = null;
        }

        foreach (var linea in Lineas(ruta))
        {
            var m = Cabeza.Match(linea);
            if (m.Success)
            {
                Cerrar();
                DateTime.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var cuando);
                actual = new Entrada(cuando, m.Groups[2].Value, m.Groups[3].Value.Trim(), m.Groups[4].Value, "");
                texto = new StringBuilder(linea);
            }
            else if (actual is not null && texto!.Length < 20_000)
            {
                texto.Append('\n').Append(linea);
            }
        }
        Cerrar();
        return lista;
    }

    /// <summary>Las entradas agrupadas: el mismo error, una sola vez con sus «veces».</summary>
    public static List<Grupo> Agrupar(IEnumerable<Entrada> entradas) =>
        entradas
            .GroupBy(Firma)
            .Select(g =>
            {
                var orden = g.OrderBy(e => e.Cuando).ToList();
                var ultima = orden[^1];
                return new Grupo(
                    g.Key,
                    Titulo(ultima),
                    Significado(ultima.Texto),
                    orden.Count,
                    orden[0].Cuando,
                    ultima.Cuando,
                    SinEvento(ultima.Categoria),
                    ultima.Texto.Length > 8_000 ? ultima.Texto[..8_000] + "\n    …" : ultima.Texto);
            })
            .OrderByDescending(g => g.Ultima)
            .ToList();

    // ------------------------------------------------------------------ apoyo

    private static readonly Regex Guid_ = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
    private static readonly Regex Numeros = new(@"\d+", RegexOptions.Compiled);

    /// <summary>Lo que hace «el mismo error»: categoría + mensaje sin números ni GUID.</summary>
    public static string Firma(Entrada e)
    {
        var normal = Numeros.Replace(Guid_.Replace(e.Primera, "*"), "#");
        if (normal.Length > 300) normal = normal[..300];
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(SinEvento(e.Categoria) + "|" + normal));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    private static string SinEvento(string categoria)
    {
        var i = categoria.IndexOf(" #", StringComparison.Ordinal);
        return i > 0 ? categoria[..i] : categoria;
    }

    private static string Titulo(Entrada e)
    {
        var t = string.IsNullOrWhiteSpace(e.Primera) ? SinEvento(e.Categoria) : e.Primera.Trim();
        return t.Length > 180 ? t[..180] + "…" : t;
    }

    private static readonly (Regex Patron, string Texto)[] Significados =
    [
        (new(@"\b2256\b|no se encuentra en el catalogo", RegexOptions.IgnoreCase),
            "SUNAT no aceptó un dato del XML: ese valor no está en su catálogo. Reintentar no lo arregla; hay que corregir el código."),
        (new(@"\b0127\b|ticket no existe", RegexOptions.IgnoreCase),
            "SUNAT dice que no conoce el ticket del resumen. El sistema vuelve a preguntar y, si sigue igual, lo reenvía solo."),
        (new(@"HTTP 401|Credenciales rechazadas|401 Authorization", RegexOptions.IgnoreCase),
            "SUNAT no aceptó el usuario SOL o la clave, o SUNAT beta falló un momento. Si se repite muchas horas, revisa la clave SOL de la empresa."),
        // La base antes que «timeout»: un Npgsql «timed out» es la base, no SUNAT (e226).
        (new(@"Npgsql|could not connect|Connection refused|Failed to connect|error occurred using a transaction", RegexOptions.IgnoreCase),
            "No se pudo hablar con la base de datos: estaba apagada, ocupada o la PC recién despertaba. Se reintenta solo; si se repite, revisa que Docker y la base estén levantados."),
        (new(@"Tiempo de espera agotado|timed out|timeout|TaskCanceledException|no respondió", RegexOptions.IgnoreCase),
            "SUNAT u otro servicio no contestó a tiempo. Se reintenta solo; preocúpate solo si se repite muchas horas."),
        (new(@"certificado", RegexOptions.IgnoreCase),
            "Problema con el certificado digital de la empresa: vencido, sin cargar o con la clave errada."),
        (new(@"\[\d{4}\]|RECHAZADO", RegexOptions.None),
            "SUNAT rechazó el documento por una de sus reglas. El número entre corchetes es el código de SUNAT y el texto dice qué dato falló."),
        (new(@"webhook", RegexOptions.IgnoreCase),
            "No se pudo avisar a la plataforma de un cambio (webhook). Se reintenta solo."),
        (new(@"Culqi", RegexOptions.IgnoreCase),
            "Error al hablar con Culqi (pagos con tarjeta)."),
        (new(@"SMTP|correo", RegexOptions.IgnoreCase),
            "Un correo no salió. Revisa la configuración de correo si se repite."),
    ];

    /// <summary>Qué significa, en palabras simples, para los errores que ya conocemos.</summary>
    public static string? Significado(string texto) =>
        Significados.FirstOrDefault(s => s.Patron.IsMatch(texto)).Texto;

    /// <summary>Las líneas del archivo, sin bloquearlo: el proceso sigue escribiendo.</summary>
    private static IEnumerable<string> Lineas(string ruta)
    {
        using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var lector = new StreamReader(fs, Encoding.UTF8);
        while (lector.ReadLine() is { } linea)
            yield return linea;
    }
}