namespace Plataforma.Api.Arranque;

/// <summary>
/// La dirección por la que se llega a ESTA API desde fuera.
///
/// HOY LA USA UN SOLO SITIO: la pantalla de credenciales, para enseñar la URL
/// del webhook que hay que registrar en el servicio de facturación.
///
/// ══════════════════════════════════════════════════════════════════════════
/// NO ES `PLATAFORMA_URL_PUBLICA`, aunque en producción valgan lo mismo
///
///   PLATAFORMA_URL_PUBLICA   la del FRONT. A donde llevan los enlaces de los
///                            correos: tiene que abrir una pantalla.
///   PLATAFORMA_URL_API       la de la API. A donde llama el servicio de
///                            facturación: tiene que llegar a un endpoint.
///
/// En desarrollo son dos puertos —5173 y 5255— y reutilizar la del correo
/// daba una URL de webhook que no servía. En producción la API sirve el front
/// desde `wwwroot`, así que suelen ser la misma; por eso, si falta esta, se
/// usa aquella FUERA de desarrollo.
/// ══════════════════════════════════════════════════════════════════════════
///
/// SE LEE UNA VEZ AL ARRANCAR, como las opciones del correo: un `.env` mal
/// escrito se nota al levantar, no el día que alguien registra un webhook.
/// </summary>
public sealed record UrlApi(string Base)
{
    public static UrlApi Leer(IHostEnvironment entorno)
    {
        var propia = Entorno.Opcional("PLATAFORMA_URL_API", "").Trim();

        if (propia.Length > 0)
            return new UrlApi(Normalizar(propia, "PLATAFORMA_URL_API"));

        if (entorno.IsDevelopment())
            return new UrlApi("http://localhost:5255");

        // Fuera de desarrollo, sin la variable: la pública. Es correcto cuando
        // la API sirve el front, que es como se despliega.
        var publica = Entorno.Opcional("PLATAFORMA_URL_PUBLICA", "").Trim();

        if (publica.Length == 0)
            throw new InvalidOperationException(
                "Falta PLATAFORMA_URL_API (o PLATAFORMA_URL_PUBLICA) fuera de desarrollo. " +
                "Es la dirección a la que el servicio de facturación manda sus avisos.");

        return new UrlApi(Normalizar(publica, "PLATAFORMA_URL_PUBLICA"));
    }

    private static string Normalizar(string url, string variable)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{variable} no es una URL completa: «{url}». Tiene que empezar por http:// o https://");
        }

        return url.TrimEnd('/');
    }
}
