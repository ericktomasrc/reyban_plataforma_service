using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Plataforma.Api.Arranque;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// Dónde está el servicio de facturación y con qué clave se crean emisores en
/// él. Se lee UNA VEZ al arrancar (diseño §13).
///
///   FACTURACION_BASE_URL   la dirección del servicio. Es la que se guarda en
///                          la credencial de cada empresa dada de alta desde
///                          aquí, y la que la ventana de Facturación propone
///                          en «Comprobar».
///   FACTURACION_CLAVE_ALTA la clave `alt_` del servicio. SOLO CREA EMISORES:
///                          no lee, no lista y no modifica ninguno. Es un
///                          secreto: va en el .env, nunca en el código ni en
///                          el chat.
///
/// SIN LAS DOS, LA PLATAFORMA ARRANCA IGUAL: lo único que no funciona es el
/// alta, y lo dice con un mensaje que explica qué falta. Rotar claves, conectar
/// empresas antiguas y emitir no dependen de esto.
/// </summary>
public sealed class OpcionesServicioFacturacion
{
    public string BaseUrl { get; init; } = "";
    public string ClaveAlta { get; init; } = "";

    public static OpcionesServicioFacturacion Leer() => new()
    {
        BaseUrl   = Entorno.Opcional("FACTURACION_BASE_URL", "").Trim().TrimEnd('/'),
        ClaveAlta = Entorno.Opcional("FACTURACION_CLAVE_ALTA", "").Trim()
    };

    /// <summary>Null si se puede dar de alta; si no, qué falta en el .env.</summary>
    public string? ProblemaAlta()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Falta FACTURACION_BASE_URL en el .env de la plataforma (la dirección del " +
                   "servicio, por ejemplo https://localhost:7295). Ponla y reinicia la API.";

        if (ClaveAlta.Length == 0)
            return "Falta FACTURACION_CLAVE_ALTA en el .env de la plataforma. Emítela en el panel " +
                   "del servicio (Claves de alta), pégala en el .env y reinicia la API.";

        if (!ClaveAlta.StartsWith("alt_", StringComparison.Ordinal) || ClaveAlta.Any(char.IsWhiteSpace))
            return "FACTURACION_CLAVE_ALTA no tiene forma de clave de alta: empieza por alt_ y va " +
                   "entera, sin espacios. Ojo: no es una fac_ de ninguna empresa.";

        return null;
    }

    /// <summary>Que nunca se imprima la clave, ni en un log ni en un error.</summary>
    public override string ToString() =>
        $"OpcionesServicioFacturacion {{ BaseUrl = {BaseUrl}, ClaveAlta = {(ClaveAlta.Length > 0 ? "(puesta)" : "(falta)")} }}";
}


/// <summary>
/// Lo que devuelve <c>POST /alta/emisores</c>. `Clave` y `Webhook.Secreto` van
/// EN CLARO: se cifran en cuanto llegan y no se registran en ningún sitio.
/// </summary>
public sealed record AltaDelServicio(
    Guid EmisorId,
    string? Estado,
    DateTimeOffset? CreadoEn,
    string? Ambiente,
    string? Clave,
    WebhookDeAltaDelServicio? Webhook,
    Guid? Abandonado)
{
    public override string ToString() =>
        $"AltaDelServicio {{ EmisorId = {EmisorId}, Estado = {Estado}, Ambiente = {Ambiente}, Abandonado = {Abandonado} }}";
}

public sealed record WebhookDeAltaDelServicio(Guid Id, string? Url, string? Secreto)
{
    public override string ToString() => $"WebhookDeAltaDelServicio {{ Id = {Id}, Url = {Url} }}";
}

/// <summary>Lo que devuelve <c>POST /v1/alta/confirmar</c>.</summary>
public sealed record ConfirmacionDelServicio(string? Estado, DateTimeOffset? ConfirmadaEn, string? Mensaje);


/// <summary>
/// Lo que dijo el servicio del emisor (<c>GET /v1/emisor</c>), guardado en la
/// credencial ACTIVA para enseñarlo sin preguntar en cada pantalla
/// (`emisor_estado`, migración 015).
///
/// UN SOLO SITIO PARA ESCRIBIRLO, como el certificado: lo usan «Conectar»,
/// «Volver a preguntar al servicio», la confirmación del alta y la revisión
/// diaria. Por SQL y no por EF: es telemetría, y así EF no tiene que saber
/// nada de jsonb.
///
/// No lleva secretos: el estado del alta, el identificador y el correo de
/// respuesta del portal, la dirección y lo que falta para emitir.
/// </summary>
public static class EstadoDelEmisor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task GuardarAsync(
        ContextoPlataforma db, Guid empresaId,
        EndpointsCredenciales.EmisorDelServicio emisor, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(emisor, Json);

        // Si el servicio dice que el alta está confirmada, se apunta aquí
        // también (una sola vez: la primera fecha es la buena).
        DateTime? confirmada = emisor.Alta is { Estado: "confirmada" } a
            ? (a.ConfirmadaEn?.UtcDateTime ?? DateTime.UtcNow)
            : null;

        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE credenciales_facturacion
               SET emisor_estado      = {json}::jsonb,
                   emisor_estado_en   = now(),
                   alta_confirmada_en = COALESCE(alta_confirmada_en, {confirmada}::timestamptz)
             WHERE empresa_id = {empresaId}
               AND activo
            """, ct);
    }

    /// <summary>
    /// La última respuesta guardada, tal cual (JSON), y cuándo. Null si nunca
    /// se guardó ninguna.
    /// </summary>
    public static async Task<(JsonElement? Estado, DateTime? En)> LeerAsync(
        ContextoPlataforma db, Guid empresaId, CancellationToken ct)
    {
        // Por Npgsql y no por SqlQuery<T>: así no depende de cómo traduzca EF
        // los nombres de un tipo que no es entidad. Igual que UltimoAvisoAsync.
        var conexion = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaccion = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();

        if (conexion.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand("""
            SELECT emisor_estado::text, emisor_estado_en
              FROM credenciales_facturacion
             WHERE empresa_id = @empresa
               AND activo
            """, conexion, transaccion);

        cmd.Parameters.AddWithValue("empresa", empresaId);

        string? texto = null;
        DateTime? en = null;

        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                texto = r.IsDBNull(0) ? null : r.GetString(0);
                en    = r.IsDBNull(1) ? null : r.GetDateTime(1);
            }
        }

        if (texto is not { Length: > 0 }) return (null, en);

        using var doc = JsonDocument.Parse(texto);
        return (doc.RootElement.Clone(), en);
    }

    /// <summary>
    /// Le dice al servicio que la plataforma ya guardó el alta
    /// (`POST /v1/alta/confirmar` con la clave guardada). Seguro de llamar de
    /// más: si ya estaba confirmada, el servicio no cambia nada.
    ///
    /// La llaman la pantalla (justo después del alta y de «Conectar»), «Volver
    /// a preguntar al servicio» y la revisión diaria — estas dos, solo cuando
    /// el servicio dice «sin_confirmar» y el RUC ya se comprobó. Devuelve el
    /// estado que dijo el servicio, o null si no se pudo preguntar.
    /// </summary>
    public static async Task<string?> ConfirmarAsync(
        ContextoPlataforma db, ClienteFacturacion facturacion, Guid empresaId, CancellationToken ct)
    {
        var r = await facturacion.ConfirmarAltaAsync<ConfirmacionDelServicio>(empresaId, ct);

        if (!r.Exito || r.Datos is null) return null;

        if (r.Datos.Estado == "confirmada")
        {
            DateTime confirmada = r.Datos.ConfirmadaEn?.UtcDateTime ?? DateTime.UtcNow;

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE credenciales_facturacion
                   SET alta_confirmada_en = COALESCE(alta_confirmada_en, {confirmada}::timestamptz)
                 WHERE empresa_id = {empresaId}
                   AND activo
                """, ct);
        }

        return r.Datos.Estado;
    }

    /// <summary>
    /// Los avisos ámbar de la ventana de Facturación, a partir de lo último
    /// que dijo el servicio. El texto es el del diseño: lo que se acepte antes
    /// de arreglarlo NO recibe el correo nunca, tampoco después.
    /// </summary>
    public static List<object> Avisos(JsonElement? estado)
    {
        var avisos = new List<object>();
        if (estado is not { ValueKind: JsonValueKind.Object } e) return avisos;

        if (e.TryGetProperty("portal", out var portal) && portal.ValueKind == JsonValueKind.Object &&
            portal.TryGetProperty("correosAlReceptor", out var correos) && correos.ValueKind == JsonValueKind.False)
        {
            avisos.Add(new
            {
                nivel = "ambar",
                codigo = "sin_correos",
                texto = "Esta empresa está conectada, pero no va a mandar correos al receptor hasta " +
                        "que pongas el correo de respuesta en el servicio.",
                detalle = "Pestaña Marca del panel del servicio: identificador y correo de respuesta. " +
                          "Lo que se acepte antes no recibirá el correo nunca, tampoco después."
            });
        }

        if (e.TryGetProperty("alta", out var alta) && alta.ValueKind == JsonValueKind.Object &&
            alta.TryGetProperty("estado", out var est) && est.ValueKind == JsonValueKind.String &&
            est.GetString() == "sin_confirmar")
        {
            avisos.Add(new
            {
                nivel = "ambar",
                codigo = "alta_sin_confirmar",
                texto = "El alta sigue «sin confirmar» en el servicio.",
                detalle = "Se confirma sola al volver a preguntar al servicio, o en la revisión diaria."
            });
        }

        return avisos;
    }
}