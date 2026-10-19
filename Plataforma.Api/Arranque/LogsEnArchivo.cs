using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Plataforma.Api.Arranque;

/// <summary>
/// 038 — LOGS EN ARCHIVO, UNA CARPETA POR DÍA (hora de Lima).
///
///     logs/
///       .gitignore              (lo crea solo: los logs nunca entran en git)
///       2026-10-18/
///         plataforma-api.log
///
/// Sin paquetes NuGet: un proveedor de ILogger propio. Todo lo que el sistema
/// ya escribe con ILogger (errores, avisos, el worker, la prueba de madrugada,
/// SUNAT) cae aquí, más una línea por cada petición HTTP (RegistroPeticiones).
///
/// QUÉ NO SE ESCRIBE NUNCA: cuerpos de petición o respuesta, cabeceras,
/// cookies, la cadena de consulta (?…) ni el SQL con sus parámetros de EF
/// (en desarrollo lleva datos sensibles). Los tokens de enlaces que van en la
/// ruta (/invitacion/{token}, /e/{enlace}) se tapan.
///
/// Variables (opcionales):
///   LOGS_CARPETA   dónde (por defecto, la carpeta «logs» junto a los proyectos)
///   LOGS_NIVEL     Information (defecto), Debug, Warning, Error
///   LOGS_DIAS      cuántos días guardar (defecto 60; 0 = no borrar nunca)
///
/// Si la carpeta no se puede escribir, el sistema sigue funcionando: se avisa
/// una vez por la consola y no se escribe archivo.
/// </summary>
public sealed class ProveedorLogsArchivo : ILoggerProvider
{
    private static readonly TimeSpan Lima = TimeSpan.FromHours(-5);   // Perú no cambia de hora

    private readonly string _carpeta;
    private readonly string _nombre;
    private readonly LogLevel _minimo;
    private readonly int _dias;
    private readonly BlockingCollection<(DateTimeOffset Cuando, string Linea)> _cola = new(10_000);
    private readonly Thread _escritor;
    private volatile bool _roto;

    /// <param name="nombre">El archivo de cada día: «plataforma-api», «servicio-worker»…</param>
    /// <param name="raizContenido">La carpeta del proyecto; los logs van a ../logs.</param>
    public ProveedorLogsArchivo(string nombre, string raizContenido)
    {
        _nombre = nombre;
        var carpeta = Environment.GetEnvironmentVariable("LOGS_CARPETA");
        _carpeta = Path.GetFullPath(string.IsNullOrWhiteSpace(carpeta)
            ? Path.Combine(raizContenido, "..", "logs")
            : carpeta.Trim());
        _minimo = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("LOGS_NIVEL"), true, out var n)
            ? n : LogLevel.Information;
        _dias = int.TryParse(Environment.GetEnvironmentVariable("LOGS_DIAS"), out var d) && d >= 0 ? d : 60;

        _escritor = new Thread(Escribir) { IsBackground = true, Name = "logs-archivo" };
        _escritor.Start();
    }

    public string Carpeta => _carpeta;

    public ILogger CreateLogger(string categoria) => new Registrador(this, categoria);

    /// <summary>Qué categorías van al archivo y desde qué nivel.</summary>
    internal bool Acepta(string categoria, LogLevel nivel)
    {
        if (_roto || nivel == LogLevel.None || nivel < _minimo) return false;

        // El SQL de EF con sus parámetros: nunca (en desarrollo trae datos sensibles).
        if (categoria.StartsWith("Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal))
            return false;
        // EF Core: sus avisos de desarrollo (#10400, #10103) son ruido; solo errores.
        if (categoria.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            return nivel >= LogLevel.Error;
        // Arranque y parada sí; el resto de Microsoft/System, solo avisos y errores.
        if (categoria == "Microsoft.Hosting.Lifetime") return true;
        if (categoria.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            categoria.StartsWith("System.", StringComparison.Ordinal))
            return nivel >= LogLevel.Warning;
        return true;
    }

    internal void Anotar(LogLevel nivel, string categoria, EventId evento, string mensaje, Exception? ex)
    {
        var cuando = DateTimeOffset.UtcNow.ToOffset(Lima);
        var sb = new StringBuilder(160);
        sb.Append(cuando.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(" [").Append(Nivel(nivel)).Append("] ")
          .Append(Corta(categoria));
        if (evento.Id != 0) sb.Append(" #").Append(evento.Id);
        sb.Append(": ").Append(mensaje.Replace("\r", "").Replace("\n", "\n    "));
        if (ex is not null)
            sb.Append("\n    ").Append(ex.ToString().Replace("\r", "").Replace("\n", "\n    "));

        // Si la cola se llena (disco lento), se pierde la línea antes que frenar al sistema.
        _cola.TryAdd((cuando, sb.ToString()));
    }

    private static string Nivel(LogLevel n) => n switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "AVI", LogLevel.Error => "ERR", LogLevel.Critical => "CRI", _ => "???"
    };

    /// <summary>«Plataforma.Api.Middleware.MiddlewareErrores» → «MiddlewareErrores».</summary>
    private static string Corta(string categoria)
    {
        var i = categoria.LastIndexOf('.');
        return i >= 0 && i < categoria.Length - 1 ? categoria[(i + 1)..] : categoria;
    }

    private void Escribir()
    {
        string? diaAbierto = null;
        StreamWriter? archivo = null;
        try
        {
            Directory.CreateDirectory(_carpeta);
            var ignorar = Path.Combine(_carpeta, ".gitignore");
            if (!File.Exists(ignorar))
                File.WriteAllText(ignorar, "# Los logs nunca entran en git (pueden llevar correos y RUC).\n*\n");

            foreach (var (cuando, linea) in _cola.GetConsumingEnumerable())
            {
                var dia = cuando.ToString("yyyy-MM-dd");
                if (dia != diaAbierto)
                {
                    archivo?.Dispose();
                    var carpetaDia = Path.Combine(_carpeta, dia);
                    Directory.CreateDirectory(carpetaDia);
                    var fs = new FileStream(Path.Combine(carpetaDia, _nombre + ".log"),
                        FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    archivo = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
                    diaAbierto = dia;
                    Limpiar(cuando);
                }

                archivo!.Write(linea);
                archivo.Write('\n');
                if (_cola.Count == 0) archivo.Flush();
            }
        }
        catch (Exception ex)
        {
            _roto = true;
            Console.Error.WriteLine($"[logs] No se pueden escribir los logs en {_carpeta}: {ex.Message}. El sistema sigue sin logs en archivo.");
        }
        finally
        {
            archivo?.Dispose();
        }
    }

    /// <summary>Borra las carpetas de días más viejos que LOGS_DIAS (solo carpetas con fecha).</summary>
    private void Limpiar(DateTimeOffset hoy)
    {
        if (_dias == 0) return;
        try
        {
            var limite = DateOnly.FromDateTime(hoy.Date).AddDays(-_dias);
            foreach (var dir in Directory.EnumerateDirectories(_carpeta))
                if (DateOnly.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", out var fecha) && fecha < limite)
                    Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }                 // otro proceso lo tiene abierto: será mañana
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _cola.CompleteAdding();
        _escritor.Join(TimeSpan.FromSeconds(3));
    }


    private sealed class Registrador(ProveedorLogsArchivo proveedor, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel nivel) => proveedor.Acepta(categoria, nivel);

        public void Log<TState>(LogLevel nivel, EventId evento, TState estado, Exception? ex,
            Func<TState, Exception?, string> formato)
        {
            if (!IsEnabled(nivel)) return;
            proveedor.Anotar(nivel, categoria, evento, formato(estado, ex), ex);
        }
    }
}


/// <summary>
/// Tapa en una ruta los segmentos que parecen un token (enlaces de invitación,
/// recuperación, portal del receptor). Los GUID y los números se quedan.
/// </summary>
public static class RutaSegura
{
    public static string Tapar(string? ruta)
    {
        if (string.IsNullOrEmpty(ruta)) return "/";
        var partes = ruta.Split('/');
        for (var i = 0; i < partes.Length; i++)
        {
            var p = partes[i];
            // Un token: largo y con mayúsculas o dígitos. Las rutas son palabras
            // en minúsculas con guiones («certificado-autofirmado»): esas no.
            if (p.Length >= 20 && !Guid.TryParse(p, out _) && p.Any(c => char.IsUpper(c) || char.IsDigit(c)))
                partes[i] = p[..4] + "…";
        }
        return string.Join('/', partes);
    }
}