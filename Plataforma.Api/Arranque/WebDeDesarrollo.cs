using System.Diagnostics;
using System.Net.Sockets;

namespace Plataforma.Api.Arranque;

/// <summary>
/// SOLO EN TU PC (Development): al hacer `dotnet run` de la API, levanta
/// también la web (`npm run dev`, puerto 5173), para no tener que abrir otra
/// ventana. Al parar la API (Ctrl+C) la web se apaga con ella.
///
///   · Si la web ya está corriendo (el 5173 responde), no hace nada.
///   · Busca la carpeta reyban_plataforma_web al lado de la del servicio.
///   · Si no hay node_modules, primero corre `npm install`.
///   · PLATAFORMA_LEVANTAR_WEB=no en el .env lo apaga.
///
/// En QA y en producción no se registra: allí la web va compilada en su
/// contenedor.
/// </summary>
public sealed class WebDeDesarrollo(IHostEnvironment entorno, ILogger<WebDeDesarrollo> log) : BackgroundService
{
    private const int Puerto = 5173;
    private Process? _proceso;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            if (Entorno.Opcional("PLATAFORMA_LEVANTAR_WEB", "si").Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
                return;
            if (await EscuchandoAsync(ct))
            {
                log.LogInformation("La web ya está corriendo en http://localhost:{Puerto}.", Puerto);
                return;
            }
            var carpeta = BuscarCarpetaWeb(entorno.ContentRootPath);
            if (carpeta is null)
            {
                log.LogWarning("No encontré la carpeta reyban_plataforma_web al lado del servicio: levanta la web a mano con «npm run dev».");
                return;
            }

            if (!Directory.Exists(Path.Combine(carpeta, "node_modules")))
            {
                log.LogInformation("Instalando las dependencias de la web (npm install), solo esta vez…");
                using var instalar = Iniciar(carpeta, "install");
                if (instalar is null) return;
                await instalar.WaitForExitAsync(ct);
            }

            _proceso = Iniciar(carpeta, "run dev");
            if (_proceso is not null)
                log.LogInformation("Web levantada: http://localhost:{Puerto} (se apaga junto con la API).", Puerto);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            log.LogWarning(e, "No se pudo levantar la web sola: levántala a mano con «npm run dev».");
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try
        {
            if (_proceso is { HasExited: false })
                _proceso.Kill(entireProcessTree: true);
        }
        catch { /* ya estaba cerrada */ }
        _proceso?.Dispose();
        await base.StopAsync(ct);
    }

    private Process? Iniciar(string carpeta, string argumentos)
    {
        // En Windows npm es npm.cmd: se llama a través de cmd.
        var windows = OperatingSystem.IsWindows();
        var info = new ProcessStartInfo
        {
            FileName = windows ? "cmd.exe" : "npm",
            Arguments = windows ? "/c npm " + argumentos : argumentos,
            WorkingDirectory = carpeta,
            UseShellExecute = false,   // comparte la consola: lo de Vite se ve aquí mismo
        };
        try { return Process.Start(info); }
        catch (Exception e)
        {
            log.LogWarning(e, "No encontré npm (¿Node.js instalado?). Levanta la web a mano.");
            return null;
        }
    }

    private static string? BuscarCarpetaWeb(string desde)
    {
        for (var d = new DirectoryInfo(desde); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "reyban_plataforma_web");
            if (File.Exists(Path.Combine(c, "package.json"))) return c;
        }
        return null;
    }

    private static async Task<bool> EscuchandoAsync(CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
            espera.CancelAfter(TimeSpan.FromMilliseconds(700));
            await tcp.ConnectAsync("localhost", Puerto, espera.Token);
            return true;
        }
        catch { return false; }
    }
}