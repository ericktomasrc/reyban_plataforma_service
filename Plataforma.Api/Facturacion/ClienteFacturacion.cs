using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// Lo que devolvió el servicio de facturación, bien o mal.
///
/// NO LANZA EXCEPCIONES POR UN ERROR DEL SERVICIO, y es la decisión central de
/// este archivo. Un 400 de SUNAT —«la serie no existe», «el RUC está mal»— no
/// es un fallo del programa: es información para quien está emitiendo, y tiene
/// que llegarle tal cual. Envuelto en una excepción acabaría convertido en un
/// 500 genérico, que es exactamente el mensaje que no ayuda a nadie.
/// </summary>
public sealed record ResultadoFacturacion<T>(
    bool Exito,
    int Estado,
    T? Datos,
    string? Error,
    string? Detalle)
{
    /// <summary>
    /// Cierto cuando ni siquiera se pudo hablar con el servicio: está apagado,
    /// la URL está mal o el certificado no vale. Distinto de que el servicio
    /// respondiera que no.
    /// </summary>
    public bool NoSePudoLlegar => Estado == 0;
}


/// <summary>Los tres archivos que el servicio guarda —o genera— de un comprobante.</summary>
public enum ArchivoComprobante { Xml, Cdr, Pdf }


/// <summary>
/// Lo que devolvió el servicio al pedirle un archivo, bien o mal.
///
/// Igual que <see cref="ResultadoFacturacion{T}"/>, NO LANZA por un error del
/// servicio: su «Archivo no disponible» con su detalle está escrito para quien
/// lo pidió, y tiene que llegarle tal cual.
///
/// `NombreArchivo` es el que manda el servicio en `Content-Disposition`
/// —«F001-00000086.xml», no el GUID— y es el que tiene que ver el usuario.
/// </summary>
public sealed record ResultadoArchivo(
    bool Exito,
    int Estado,
    byte[]? Contenido,
    string? TipoContenido,
    string? NombreArchivo,
    string? Error,
    string? Detalle)
{
    public bool NoSePudoLlegar => Estado == 0;
}


/// <summary>
/// El único sitio de la plataforma que habla con el servicio de facturación.
///
/// LA PLATAFORMA CONSUME LA API COMO SI FUERA DE UN TERCERO, con su clave y su
/// empresa, aunque los dos proyectos sean del mismo dueño. Esa disciplina es lo
/// que mantiene el servicio vendible: el día que un cliente externo lo consuma,
/// usa exactamente el mismo camino que usamos nosotros.
///
/// POR ESO AQUÍ NO HAY NI UNA LÍNEA DE FACTURACIÓN. No sabe qué es el IGV, ni
/// cómo se arma un XML, ni qué exige SUNAT. Manda JSON y devuelve lo que le
/// contesten.
///
/// ══════════════════════════════════════════════════════════════════════════
/// LA CLAVE SE RESUELVE POR EMPRESA, EN CADA LLAMADA
///
/// No hay una clave de la plataforma: hay una por cliente, guardada cifrada, y
/// la que se usa sale de `ctx.EmpresaId`. Nadie pasa la empresa por parámetro,
/// así que no hay forma de emitir con la clave de otro ni por error. Las
/// excepciones —probar la clave, bajar un archivo, y las rutas fijas del
/// webhook que usa «Conectar»— no emiten nunca.
/// ══════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class ClienteFacturacion(
    IHttpClientFactory fabrica,
    ContextoPlataforma db,
    ContextoPeticion ctx,
    Cifrador cifrador,
    ILogger<ClienteFacturacion> log)
{
    public const string NombreCliente = "facturacion";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// ¿Esta empresa está configurada para facturar?
    ///
    /// Lo usan las pantallas para explicar por qué no se puede emitir, en vez
    /// de dejar que falle al primer intento.
    /// </summary>
    public Task<bool> EstaConfiguradaAsync(CancellationToken ct = default) =>
        db.CredencialesFacturacion.AnyAsync(c => c.EmpresaId == ctx.EmpresaId, ct);


    public Task<ResultadoFacturacion<T>> ObtenerAsync<T>(string ruta, CancellationToken ct = default) =>
        LlamarAsync<T>(HttpMethod.Get, ruta, null, null, null, ct);

    /// <param name="llaveIdempotencia">
    /// La cabecera que impide un duplicado por doble clic o por reintento.
    ///
    /// NO ES OPCIONAL EN LA PRÁCTICA cuando se emite: sin ella, pulsar dos
    /// veces «Emitir» son dos comprobantes con dos correlativos, y un
    /// correlativo gastado no se recupera. Con ella, el servicio devuelve el
    /// mismo de la primera vez.
    /// </param>
    public Task<ResultadoFacturacion<T>> EnviarAsync<T>(
        string ruta, object cuerpo, string? llaveIdempotencia = null,
        CancellationToken ct = default) =>
        LlamarAsync<T>(HttpMethod.Post, ruta, cuerpo, llaveIdempotencia, null, ct);


    /// <summary>
    /// Una lectura contra el servicio con la clave de OTRA empresa.
    ///
    /// LA USAN DOS SITIOS, y ninguno más: la pantalla de credenciales del
    /// super administrador, que comprueba que la clave recién guardada
    /// funciona, y la revisión diaria del certificado (`RevisionCertificados`),
    /// que no tiene sesión y fija el contexto de esa misma empresa antes de
    /// llamar. Tiene dos candados que conviene no quitar:
    ///
    ///   1. **Solo GET.** No hay versión de esto que escriba, y no debe
    ///      haberla. Emitir con la clave de otra empresa es el peor fallo que
    ///      este archivo podría tener: un comprobante tributario a nombre de
    ///      quien no lo pidió, y un comprobante emitido no se deshace.
    ///
    ///   2. **Quien la llama ya decidió que puede.** El endpoint lleva
    ///      `SoloSuperAdmin`; la revisión diaria no tiene persona detrás y
    ///      trabaja con el contexto de la empresa que revisa, así que el
    ///      aislamiento no le deja leer la credencial de ninguna otra. Aquí no
    ///      se comprueba, y es correcto que no: esta clase no decide quién
    ///      puede hacer qué. Un tercer sitio que la llame tiene que poner su
    ///      propia comprobación delante.
    ///
    /// El resto del archivo NO acepta una empresa por parámetro, a propósito:
    /// la saca de la cookie, y así no hay forma de equivocarse. Las otras
    /// excepciones son <see cref="DescargarComprobanteAsync"/> (solo GET),
    /// <see cref="ReintentarAvisosAsync{T}"/> y <see cref="ConfirmarAltaAsync{T}"/>
    /// (rutas fijas, no emiten).
    /// </summary>
    public Task<ResultadoFacturacion<T>> LeerComoEmpresaAsync<T>(
        Guid empresaId, string ruta, CancellationToken ct = default) =>
        LlamarAsync<T>(HttpMethod.Get, ruta, null, null, empresaId, ct);


    /// <summary>
    /// Fase 4c: pausa o reactiva la emisión de una empresa en el servicio
    /// (POST /v1/configuracion/pausa). Ruta fija, no emite nada. La empresa va
    /// por parámetro como en <see cref="LeerComoEmpresaAsync{T}"/>: la usa
    /// <c>PausaEnServicio</c>, que pone antes el contexto de esa empresa.
    /// </summary>
    public Task<ResultadoFacturacion<System.Text.Json.JsonElement>> PausarEmisorAsync(
        Guid empresaId, bool pausar, string? motivo, CancellationToken ct = default) =>
        LlamarAsync<System.Text.Json.JsonElement>(HttpMethod.Post, "v1/configuracion/pausa",
            new { pausar, motivo }, null, empresaId, ct);


    /// <summary>
    /// 030: el ambiente de la empresa en el servicio (GET) o cambiarlo (POST
    /// { ambiente: "produccion" | "beta" }). Ruta fija. Va con DOS llaves: la
    /// fac_ de la empresa y la clave de alta de la plataforma; el servicio no
    /// lo cambia con una sola. Lo usan solo la pantalla «Conexión con SUNAT»
    /// (SoloSuperAdmin, con el contexto de esa empresa) y el paso 6 del
    /// asistente en producción (empresa de la sesión: <paramref name="empresaId"/> null).
    /// </summary>
    public Task<ResultadoFacturacion<T>> AmbienteAsync<T>(
        HttpMethod metodo, object? cuerpo, string claveAlta, Guid? empresaId,
        CancellationToken ct = default) =>
        LlamarAsync<T>(metodo, "v1/configuracion/ambiente", cuerpo, null, empresaId, ct, null, claveAlta);


    /// <summary>
    /// Baja el XML, el CDR o el PDF de un comprobante.
    ///
    /// ══════════════════════════════════════════════════════════════════════
    /// LA RUTA LA ARMA ESTE MÉTODO, Y SIEMPRE ES `/v1/`
    ///
    /// El servicio tiene las mismas tres rutas bajo `/admin/comprobantes/...`,
    /// con cookie del panel y SIN aislamiento por empresa: con ellas un cliente
    /// podría bajar el comprobante de otro. Por eso aquí no se acepta una ruta
    /// de fuera, solo el tipo de archivo, y la ruta sale de una tabla fija.
    /// Con la clave `fac_` de la empresa, es el servicio quien garantiza que el
    /// comprobante es suyo.
    /// ══════════════════════════════════════════════════════════════════════
    ///
    /// LA EMPRESA SALE DEL DOCUMENTO, no de la cookie, y es la otra excepción a
    /// la regla del archivo junto a <see cref="LeerComoEmpresaAsync{T}"/>. Hace
    /// falta para el super administrador —que no tiene empresa— cuando da
    /// soporte. No abre nada nuevo: quien llama ya pudo LEER ese documento, y
    /// eso lo decidió el aislamiento de la base, no este código. Y es solo GET.
    ///
    /// SE LEE ENTERO EN MEMORIA, con un tope. Un PDF son ~50 KB y un XML menos;
    /// si algún día pasa del tope, algo está mal y es mejor decirlo que
    /// mandar 200 MB a un navegador.
    /// </summary>
    public async Task<ResultadoArchivo> DescargarComprobanteAsync(
        Guid empresaId, Guid comprobanteId, ArchivoComprobante archivo, CancellationToken ct = default,
        string? formato = null)   // 22 oct: el PDF en ticket (ticket80 | ticket58)
    {
        const long Tope = 20 * 1024 * 1024;

        var ruta = archivo switch
        {
            ArchivoComprobante.Xml => $"v1/comprobantes/{comprobanteId}/xml",
            ArchivoComprobante.Cdr => $"v1/comprobantes/{comprobanteId}/cdr",
            ArchivoComprobante.Pdf => $"v1/comprobantes/{comprobanteId}/pdf" +
                                      (formato is "ticket80" or "ticket58" ? $"?formato={formato}" : ""),
            _ => throw new ArgumentOutOfRangeException(nameof(archivo))
        };

        var credencial = await db.CredencialesFacturacion
            .FirstOrDefaultAsync(c => c.EmpresaId == empresaId, ct);

        if (credencial is null)
        {
            return new ResultadoArchivo(false, 0, null, null, null,
                "Esta empresa todavía no tiene configurado el servicio de facturación.",
                "Lo configura la administración de la plataforma.");
        }

        var clave = cifrador.Descifrar(credencial.ClaveCifrada!);
        var http  = fabrica.CreateClient(NombreCliente);

        using var peticion = new HttpRequestMessage(
            HttpMethod.Get, new Uri(new Uri(credencial.BaseUrl.TrimEnd('/') + "/"), ruta));

        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clave);

        HttpResponseMessage respuesta;

        try
        {
            // ResponseHeadersRead: se mira el tamaño ANTES de leer el cuerpo.
            respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            await AnotarFalloAsync(credencial.Id, Recortar(e.Message), ct);

            log.LogWarning(e, "No se pudo llegar al servicio de facturación en {Url}",
                           credencial.BaseUrl);

            return new ResultadoArchivo(false, 0, null, null, null,
                "No se pudo conectar con el servicio de facturación.", e.Message);
        }

        using (respuesta)
        {
            if (respuesta.IsSuccessStatusCode)
            {
                if (respuesta.Content.Headers.ContentLength > Tope)
                {
                    return new ResultadoArchivo(false, (int)respuesta.StatusCode, null, null, null,
                        "El archivo es demasiado grande.",
                        $"El servicio quiso mandar {respuesta.Content.Headers.ContentLength} bytes.");
                }

                var contenido = await respuesta.Content.ReadAsByteArrayAsync(ct);
                await AnotarLlamadaAsync(credencial.Id, ct);

                // EL NOMBRE, TAL CUAL LO MANDA EL SERVICIO. `FileNameStar` es
                // el `filename*=UTF-8''...` y gana cuando viene; las comillas
                // del `filename` normal se quitan.
                var disposicion = respuesta.Content.Headers.ContentDisposition;
                var nombre = (disposicion?.FileNameStar ?? disposicion?.FileName)?.Trim('"');

                return new ResultadoArchivo(true, (int)respuesta.StatusCode, contenido,
                    respuesta.Content.Headers.ContentType?.MediaType,
                    string.IsNullOrWhiteSpace(nombre) ? null : nombre,
                    null, null);
            }

            // ---- El servicio dijo que no -------------------------------------
            //
            // Su 404 trae `{ error, detalle }` y el detalle explica POR QUÉ
            // —«el CDR solo existe cuando SUNAT ya respondió»—. Se pasa tal cual.
            var texto = await respuesta.Content.ReadAsStringAsync(ct);
            string? error = null, detalle = null;

            try
            {
                var e = JsonSerializer.Deserialize<ErrorDelServicio>(texto, Json);
                error   = e?.Error;
                detalle = e?.Detalle;
            }
            catch (JsonException) { /* no venía en esa forma */ }

            error ??= respuesta.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "La clave del servicio de facturación no vale.",
                HttpStatusCode.Forbidden    => "La clave no tiene permiso para esto.",
                HttpStatusCode.NotFound     => "El servicio no tiene ese archivo.",
                _                           => $"El servicio respondió {(int)respuesta.StatusCode}."
            };

            if (respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                await AnotarFalloAsync(credencial.Id, error, ct);
            else
                await AnotarLlamadaAsync(credencial.Id, ct);

            return new ResultadoArchivo(false, (int)respuesta.StatusCode, null, null, null,
                error, detalle ?? Recortar(texto, 500));
        }
    }


    // =========================================================================
    // LA TERCERA PUERTA: UNA CLAVE QUE TODAVÍA NO ESTÁ GUARDADA
    //
    // La usa SOLO el «Comprobar» y el «Conectar» de la ventana de Facturación
    // (EndpointsCredenciales), que llevan `SoloSuperAdmin`. Existe porque esos
    // dos botones tienen que preguntar al servicio ANTES de guardar nada: es
    // lo que deja ver que la clave es de otra empresa antes de que se quede
    // pegada a esta (diseño §3.1).
    //
    // TRES CANDADOS, igual de importantes que los de las otras dos puertas:
    //
    //   1. **Las rutas son fijas**, una por método, y ninguna emite:
    //      `GET /v1/emisor`, `POST /v1/webhook` y `POST /v1/webhook/reintentar`.
    //      No hay versión de esto que reciba una ruta de fuera.
    //
    //   2. **No escribe nada aquí**: ni `ultima_llamada_en` (no hay credencial
    //      todavía) ni `llamadas_facturacion`. La respuesta de
    //      `POST /v1/webhook` lleva el `whsec_` en claro, y esa tabla guarda
    //      respuestas enteras (diseño §10.3).
    //
    //   3. **No registra el cuerpo de la respuesta** en ningún log, por lo
    //      mismo. Un fallo devuelve el mensaje del servicio, que nunca lleva
    //      el secreto.
    // =========================================================================

    /// <summary>`GET /v1/emisor` con una clave sin guardar: de quién es.</summary>
    public Task<ResultadoFacturacion<T>> ConsultarEmisorConClaveAsync<T>(
        string baseUrl, string clave, CancellationToken ct = default) =>
        ConClaveAsync<T>(baseUrl, clave, HttpMethod.Get, "v1/emisor", null, ct);

    /// <summary>
    /// `POST /v1/webhook`: registra (o actualiza) el webhook de la empresa de
    /// la clave apuntando a <paramref name="urlWebhook"/>, y devuelve el
    /// secreto nuevo. El anterior deja de valer en ese instante.
    /// </summary>
    public Task<ResultadoFacturacion<T>> RegistrarWebhookConClaveAsync<T>(
        string baseUrl, string clave, string urlWebhook, CancellationToken ct = default) =>
        ConClaveAsync<T>(baseUrl, clave, HttpMethod.Post, "v1/webhook", new { url = urlWebhook }, ct);

    /// <summary>
    /// `POST /v1/clave/revocar` CON LA CLAVE QUE SE QUIERE REVOCAR (punto 21).
    /// El servicio la da de baja si la empresa tiene otra activa (409 si es la
    /// única; 401 si ya no valía). La clave no se registra en ningún sitio.
    /// </summary>
    public Task<ResultadoFacturacion<T>> RevocarClaveAsync<T>(
        string baseUrl, string clave, CancellationToken ct = default) =>
        ConClaveAsync<T>(baseUrl, clave, HttpMethod.Post, "v1/clave/revocar", null, ct);

    /// <summary>
    /// `POST /v1/webhook/reintentar`: el servicio devuelve a la cola las
    /// entregas agotadas del webhook de la empresa de la clave (diseño §10.5).
    /// </summary>
    public Task<ResultadoFacturacion<T>> ReintentarAvisosConClaveAsync<T>(
        string baseUrl, string clave, int dias, CancellationToken ct = default) =>
        ConClaveAsync<T>(baseUrl, clave, HttpMethod.Post,
                         $"v1/webhook/reintentar?dias={Math.Clamp(dias, 1, 30)}", null, ct);

    /// <summary>
    /// `POST /v1/webhook/reintentar` con la clave GUARDADA de una empresa.
    ///
    /// Para después de «Conectar»: tiene que ir en OTRA petición, cuando el
    /// secreto nuevo ya está confirmado en la base. Si se pidiera dentro de la
    /// misma, el servicio podría reenviar antes de que la transacción termine y
    /// los avisos volverían a rechazarse con 401.
    ///
    /// La empresa va por parámetro —el super administrador no tiene— igual que
    /// en <see cref="LeerComoEmpresaAsync{T}"/>; la ruta es fija y solo reenvía
    /// avisos de esa misma empresa. Quien llama lleva `SoloSuperAdmin`.
    /// </summary>
    public async Task<ResultadoFacturacion<T>> ReintentarAvisosAsync<T>(
        Guid empresaId, int dias, CancellationToken ct = default)
    {
        var credencial = await db.CredencialesFacturacion
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.EmpresaId == empresaId, ct);

        if (credencial is null)
        {
            return new ResultadoFacturacion<T>(false, 0, default,
                "Esta empresa todavía no tiene configurado el servicio de facturación.", null);
        }

        return await ConClaveAsync<T>(
            credencial.BaseUrl, cifrador.Descifrar(credencial.ClaveCifrada!), HttpMethod.Post,
            $"v1/webhook/reintentar?dias={Math.Clamp(dias, 1, 30)}", null, ct);
    }


    /// <summary>
    /// `POST /v1/alta/confirmar` con la clave GUARDADA de una empresa: le dice
    /// al servicio que la plataforma ya guardó el alta (diseño §13). Ruta fija,
    /// no emite, y se puede llamar de más. Como <see cref="ReintentarAvisosAsync{T}"/>,
    /// la empresa va por parámetro y quien llama decide si puede.
    /// </summary>
    public async Task<ResultadoFacturacion<T>> ConfirmarAltaAsync<T>(
        Guid empresaId, CancellationToken ct = default)
    {
        var credencial = await db.CredencialesFacturacion
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.EmpresaId == empresaId, ct);

        if (credencial is null)
        {
            return new ResultadoFacturacion<T>(false, 0, default,
                "Esta empresa todavía no tiene configurado el servicio de facturación.", null);
        }

        return await ConClaveAsync<T>(
            credencial.BaseUrl, cifrador.Descifrar(credencial.ClaveCifrada!), HttpMethod.Post,
            "v1/alta/confirmar", null, ct);
    }


    // =========================================================================
    // LA CONFIGURACIÓN DEL EMISOR (fase 2, el asistente)
    //
    // `/v1/configuracion/...` con la clave GUARDADA de la empresa de la sesión:
    // certificado, series, marca, logo y SOL. Igual que emitir, la empresa
    // sale de `ctx.EmpresaId` y nunca de un parámetro.
    //
    // DOS CANDADOS: solo rutas de `v1/configuracion/`, y el contenido puede ir
    // como JSON (`cuerpo`) o tal cual (`contenido`, para subir archivos). El
    // certificado y su contraseña viajan dentro: aquí no se registra nada del
    // cuerpo, ni de ida ni de vuelta.
    // =========================================================================

    public Task<ResultadoFacturacion<T>> ConfigurarAsync<T>(
        HttpMethod metodo, string ruta, object? cuerpo = null, HttpContent? contenido = null,
        CancellationToken ct = default)
    {
        if (!ruta.StartsWith("v1/configuracion/", StringComparison.Ordinal) || ruta.Contains(".."))
            throw new ArgumentException("Solo rutas de v1/configuracion/.", nameof(ruta));

        return LlamarAsync<T>(metodo, ruta, cuerpo, null, null, ct, contenido);
    }


    // =========================================================================
    // LA CUARTA PUERTA: LA CLAVE DE ALTA (diseño §13)
    //
    // `POST /alta/emisores` con la clave `alt_` de la plataforma: crea en el
    // servicio el emisor, su clave fac_ y su webhook, en una transacción, y
    // los deja «sin confirmar». La usa SOLO el alta de empresas
    // (EndpointsEmpresas), que lleva `SoloSuperAdmin`.
    //
    // LOS MISMOS CANDADOS QUE LA TERCERA: ruta fija, no escribe nada aquí y no
    // registra el cuerpo de la respuesta — lleva la fac_ y el whsec_ en claro.
    // La clave de alta no puede leer ni tocar ningún emisor que ya exista:
    // eso lo garantiza el servicio, no esta clase.
    // =========================================================================

    /// <summary>`POST /alta/emisores` con la clave de alta.</summary>
    public Task<ResultadoFacturacion<T>> CrearEmisorConClaveAltaAsync<T>(
        string baseUrl, string claveAlta, object cuerpo, CancellationToken ct = default) =>
        ConClaveAsync<T>(baseUrl, claveAlta, HttpMethod.Post, "alta/emisores", cuerpo, ct);


    private async Task<ResultadoFacturacion<T>> ConClaveAsync<T>(
        string baseUrl, string clave, HttpMethod metodo, string ruta, object? cuerpo,
        CancellationToken ct)
    {
        var http = fabrica.CreateClient(NombreCliente);

        using var peticion = new HttpRequestMessage(
            metodo, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), ruta));

        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clave);

        if (cuerpo is not null)
            peticion.Content = JsonContent.Create(cuerpo, options: Json);

        HttpResponseMessage respuesta;

        try
        {
            respuesta = await http.SendAsync(peticion, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("No se pudo llegar al servicio de facturación en {Url}: {Error}",
                           baseUrl, e.Message);

            return new ResultadoFacturacion<T>(false, 0, default,
                "No se pudo conectar con el servicio de facturación.", e.Message);
        }

        using (respuesta)
        {
            var texto = await respuesta.Content.ReadAsStringAsync(ct);

            if (respuesta.IsSuccessStatusCode)
            {
                try
                {
                    var datos = texto.Length == 0 ? default : JsonSerializer.Deserialize<T>(texto, Json);
                    return new ResultadoFacturacion<T>(true, (int)respuesta.StatusCode, datos, null, null);
                }
                catch (JsonException e)
                {
                    // El cuerpo NO va al detalle: si fuera el de POST
                    // /v1/webhook, llevaría el secreto. Solo el error de lectura.
                    return new ResultadoFacturacion<T>(false, (int)respuesta.StatusCode, default,
                        "El servicio respondió algo que no se pudo leer.", e.Message);
                }
            }

            string? error = null, detalle = null;

            try
            {
                var e = JsonSerializer.Deserialize<ErrorDelServicio>(texto, Json);
                error   = e?.Error;
                detalle = e?.Detalle;
            }
            catch (JsonException) { /* no venía en esa forma */ }

            error ??= respuesta.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "La clave no vale: el servicio no la reconoce, o está revocada.",
                HttpStatusCode.Forbidden    => "La clave no tiene permiso para esto.",
                HttpStatusCode.NotFound     => "Esa ruta no existe en el servicio. ¿Está actualizado?",
                _                           => $"El servicio respondió {(int)respuesta.StatusCode}."
            };

            return new ResultadoFacturacion<T>(false, (int)respuesta.StatusCode, default,
                error, detalle ?? Recortar(texto, 300));
        }
    }


    // =========================================================================

    private async Task<ResultadoFacturacion<T>> LlamarAsync<T>(
        HttpMethod metodo,
        string ruta,
        object? cuerpo,
        string? llaveIdempotencia,
        Guid? empresa,
        CancellationToken ct,
        HttpContent? contenido = null,
        string? claveAlta = null)
    {
        var deQuien = empresa ?? ctx.EmpresaId;

        var credencial = await db.CredencialesFacturacion
            .FirstOrDefaultAsync(c => c.EmpresaId == deQuien, ct);

        if (credencial is null)
        {
            return new ResultadoFacturacion<T>(false, 0, default,
                "Esta empresa todavía no tiene configurado el servicio de facturación.",
                "Lo configura la administración de la plataforma.");
        }

        var clave = cifrador.Descifrar(credencial.ClaveCifrada!);
        var http  = fabrica.CreateClient(NombreCliente);

        using var peticion = new HttpRequestMessage(
            metodo, new Uri(new Uri(credencial.BaseUrl.TrimEnd('/') + "/"), ruta.TrimStart('/')));

        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clave);

        if (llaveIdempotencia is { Length: > 0 })
            peticion.Headers.TryAddWithoutValidation("Idempotency-Key", llaveIdempotencia);

        // 030: la segunda llave para cambiar el ambiente (solo esa ruta la pide).
        if (claveAlta is { Length: > 0 })
            peticion.Headers.TryAddWithoutValidation("X-Clave-Alta", claveAlta);

        if (cuerpo is not null)
            peticion.Content = JsonContent.Create(cuerpo, options: Json);
        else if (contenido is not null)
            peticion.Content = contenido;

        HttpResponseMessage respuesta;

        try
        {
            respuesta = await http.SendAsync(peticion, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // NO SE PUDO NI LLEGAR. Es distinto de que el servicio dijera que
            // no, y quien lo lea tiene que poder distinguirlo: uno se arregla
            // encendiendo el servicio, el otro corrigiendo el documento.
            //
            // El mensaje de la excepción va dentro, no se tapa. Un «no se pudo
            // conectar» sin el motivo manda a buscar en la dirección
            // equivocada — es la primera lección del documento maestro del
            // servicio, y costó cuatro veces aprenderla.
            await AnotarFalloAsync(credencial.Id, Recortar(e.Message), ct);

            log.LogWarning(e, "No se pudo llegar al servicio de facturación en {Url}",
                           credencial.BaseUrl);

            return new ResultadoFacturacion<T>(false, 0, default,
                "No se pudo conectar con el servicio de facturación.",
                e.Message);
        }

        using (respuesta)
        {
            var texto = await respuesta.Content.ReadAsStringAsync(ct);

            if (respuesta.IsSuccessStatusCode)
            {
                await AnotarLlamadaAsync(credencial.Id, ct);

                // 204, o un cuerpo vacío: éxito sin datos.
                if (texto.Length == 0)
                    return new ResultadoFacturacion<T>(true, (int)respuesta.StatusCode, default, null, null);

                try
                {
                    var datos = JsonSerializer.Deserialize<T>(texto, Json);
                    return new ResultadoFacturacion<T>(true, (int)respuesta.StatusCode, datos, null, null);
                }
                catch (JsonException e)
                {
                    // Respondió bien pero con algo que no esperábamos. Casi
                    // siempre significa que la URL apunta a otro sitio —un
                    // proxy, una página de error en HTML— y el cuerpo crudo es
                    // lo que lo delata.
                    await AnotarFalloAsync(credencial.Id, "respuesta ilegible", ct);

                    return new ResultadoFacturacion<T>(false, (int)respuesta.StatusCode, default,
                        "El servicio respondió algo que no se pudo leer.",
                        Recortar($"{e.Message} — empezaba por: {Recortar(texto, 200)}"));
                }
            }

            // ---- El servicio dijo que no -------------------------------------
            //
            // Su forma de error es `{ error, detalle }` (RespuestaError). Se
            // pasa tal cual: sus mensajes están escritos para que los lea quien
            // emite, y reescribirlos aquí solo los empeoraría.
            string? error = null, detalle = null;

            try
            {
                var e = JsonSerializer.Deserialize<ErrorDelServicio>(texto, Json);
                error   = e?.Error;
                detalle = e?.Detalle;
            }
            catch (JsonException) { /* no venía en esa forma */ }

            error ??= respuesta.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "La clave del servicio de facturación no vale.",
                HttpStatusCode.Forbidden    => "La clave no tiene permiso para esto.",
                HttpStatusCode.NotFound     => "Esa ruta no existe en el servicio de facturación.",
                _                           => $"El servicio respondió {(int)respuesta.StatusCode}."
            };

            detalle ??= Recortar(texto, 500);

            // El 401 se anota como fallo porque casi siempre significa que la
            // clave caducó o se rotó del otro lado, y eso hay que verlo en la
            // pantalla de credenciales sin bucear en un log.
            if (respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                await AnotarFalloAsync(credencial.Id, error, ct);
            else
                await AnotarLlamadaAsync(credencial.Id, ct);

            return new ResultadoFacturacion<T>(false, (int)respuesta.StatusCode, default, error, detalle);
        }
    }


    private sealed record ErrorDelServicio(string? Error, string? Detalle);


    /// <summary>
    /// Se escribe con SQL y no con EF a propósito: es una columna de
    /// telemetría, y no tiene que arrastrar el resto de los cambios que haya
    /// pendientes en el contexto ni disparar la comprobación de concurrencia.
    /// </summary>
    private Task AnotarLlamadaAsync(Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE credenciales_facturacion
               SET ultima_llamada_en = now()
             WHERE id = {id}
            """, ct);

    private Task AnotarFalloAsync(Guid id, string? motivo, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE credenciales_facturacion
               SET ultima_llamada_en = now(),
                   ultimo_fallo_en   = now(),
                   ultimo_fallo      = {motivo}
             WHERE id = {id}
            """, ct);

    private static string Recortar(string texto, int largo = 300) =>
        texto.Length <= largo ? texto : texto[..largo] + "…";
}