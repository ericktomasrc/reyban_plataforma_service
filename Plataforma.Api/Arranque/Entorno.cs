namespace Plataforma.Api.Arranque;

/// <summary>
/// Lee el archivo .env y mete sus valores en las variables de entorno del
/// proceso.
///
/// POR QUÉ ESCRITO A MANO Y NO UN PAQUETE: son treinta líneas, no tiene
/// dependencias y así se sabe exactamente qué hace con las comillas y los
/// comentarios — que es justo donde un .env mal leído deja de arrancar sin
/// decir por qué.
///
/// NO PISA LO QUE YA ESTÉ DEFINIDO. En producción las variables las pone el
/// contenedor, y ahí no hay .env. Si lo pisara, un archivo olvidado en el
/// servidor mandaría sobre la configuración real.
/// </summary>
public static class Entorno
{
    public static void CargarDotEnv(string? carpeta = null)
    {
        var dir = new DirectoryInfo(carpeta ?? Directory.GetCurrentDirectory());

        // El .env vive en la raíz del repositorio, y la aplicación arranca
        // desde la carpeta del proyecto. Se sube hasta encontrarlo.
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".env")))
            dir = dir.Parent;

        if (dir is null) return;

        foreach (var linea in File.ReadAllLines(Path.Combine(dir.FullName, ".env")))
        {
            var texto = linea.Trim();
            if (texto.Length == 0 || texto.StartsWith('#')) continue;

            var igual = texto.IndexOf('=');
            if (igual <= 0) continue;

            var clave = texto[..igual].Trim();
            var valor = texto[(igual + 1)..].Trim();

            // Quitar comillas si las hay, pero solo si envuelven todo el valor.
            if (valor.Length >= 2 &&
                ((valor[0] == '"' && valor[^1] == '"') ||
                 (valor[0] == '\'' && valor[^1] == '\'')))
            {
                valor = valor[1..^1];
            }

            if (Environment.GetEnvironmentVariable(clave) is null)
                Environment.SetEnvironmentVariable(clave, valor);
        }
    }

    public static string Obligatoria(string nombre) =>
        Environment.GetEnvironmentVariable(nombre)
        ?? throw new InvalidOperationException(
            $"Falta la variable de entorno {nombre}. Mírala en el .env de la raíz.");
}
