using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Npgsql;
using NpgsqlTypes;
using Plataforma.Api.Arranque;

namespace Plataforma.Api.Consultas;

/// <summary>
/// 042 — LA CONFIGURACIÓN DEL PADRÓN, del .env (todo opcional).
///
///   PADRON_SUNAT_URL          de dónde se baja (por defecto, el de SUNAT)
///   PADRON_SUNAT_AUTOMATICO   false para no bajarlo solo (por defecto, sí)
///   PADRON_SUNAT_HORA         a partir de qué hora de Lima (por defecto, 2)
///   PADRON_SUNAT_MINIMO       menos RUC que esto = archivo incompleto (1 000 000)
///
/// 045 — EL DE LOCALES ANEXOS (para Sucursales), con la misma hora y el mismo
/// interruptor automático:
///
///   PADRON_ANEXOS_URL         de dónde se baja (por defecto, el de SUNAT)
///   PADRON_ANEXOS_MINIMO      menos locales que esto = archivo incompleto (100 000)
/// </summary>
public sealed class OpcionesPadron
{
    public string Url { get; init; } = "https://www2.sunat.gob.pe/padron_reducido_ruc.zip";
    public bool Automatico { get; init; } = true;
    public int Hora { get; init; } = 2;
    public long Minimo { get; init; } = 1_000_000;
    public string UrlAnexos { get; init; } = "https://www.sunat.gob.pe/descargaPRR/padron_reducido_local_anexo.zip";
    public long MinimoAnexos { get; init; } = 100_000;
    public string Carpeta { get; init; } = Path.Combine(Path.GetTempPath(), "padron-sunat");
    public string CadenaConexion { get; init; } = "";

    /// <summary>Hasta tres intentos automáticos por día, separados por dos horas.</summary>
    public const int IntentosPorDia = 3;
    public static readonly TimeSpan EntreIntentos = TimeSpan.FromHours(2);

    /// <summary>045: la dirección de descarga y el mínimo de cada padrón.</summary>
    public string UrlDe(string padron) => padron == Padrones.Anexos ? UrlAnexos : Url;
    public long MinimoDe(string padron) => padron == Padrones.Anexos ? MinimoAnexos : Minimo;

    public static OpcionesPadron Leer() => new()
    {
        Url        = Entorno.Opcional("PADRON_SUNAT_URL", "https://www2.sunat.gob.pe/padron_reducido_ruc.zip").Trim(),
        Automatico = !string.Equals(Entorno.Opcional("PADRON_SUNAT_AUTOMATICO", "true").Trim(), "false",
                                    StringComparison.OrdinalIgnoreCase),
        Hora       = Math.Clamp(Entorno.Numero("PADRON_SUNAT_HORA", 2), 0, 23),
        Minimo     = Math.Max(1, Entorno.Numero("PADRON_SUNAT_MINIMO", 1_000_000)),
        UrlAnexos  = Entorno.Opcional("PADRON_ANEXOS_URL", "https://www.sunat.gob.pe/descargaPRR/padron_reducido_local_anexo.zip").Trim(),
        MinimoAnexos = Math.Max(1, Entorno.Numero("PADRON_ANEXOS_MINIMO", 100_000)),
        CadenaConexion = Plataforma.Api.Arranque.CadenaConexion.Ajustada(Entorno.Obligatoria("PLATAFORMA_CADENA_CONEXION")),   // e180
    };
}

/// <summary>045: los dos padrones de SUNAT que se cargan.</summary>
public static class Padrones
{
    public const string Ruc = "ruc";
    public const string Anexos = "anexos";

    /// <summary>Lo que venga en la petición → «ruc» o «anexos».</summary>
    public static string Normalizar(string? padron) =>
        string.Equals(padron?.Trim(), Anexos, StringComparison.OrdinalIgnoreCase) ? Anexos : Ruc;

    public static string Nombre(string padron) => padron == Anexos ? "padrón de locales anexos" : "padrón de RUC";
}

/// <summary>Un trabajo para el cargador: bajar de SUNAT (Ruta nula) o un archivo ya subido. 045: de qué padrón.</summary>
public sealed record PedidoPadron(string Origen, Guid? UsuarioId, string? Ruta, string Archivo, long? Bytes, string Padron = Padrones.Ruc);

/// <summary>
/// La cola del cargador: la llenan «Descargar ahora» y «Subir archivo»; la
/// vacía <see cref="CargadorPadron"/>. De a uno: dos cargas a la vez no pueden.
/// </summary>
public sealed class ColaPadron
{
    private readonly Channel<PedidoPadron> _canal = Channel.CreateBounded<PedidoPadron>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropWrite });

    public bool Encolar(PedidoPadron p)
    {
        if (!_canal.Writer.TryWrite(p)) return false;
        // 22 oct: que la pantalla lo vea al instante, antes de que el cargador lo tome.
        if (Avance is null) Fijar("en_cola", p.Origen, p.Archivo, 0, null, 0, subido: p.Ruta is not null, padron: p.Padron);
        return true;
    }

    public ChannelReader<PedidoPadron> Lector => _canal.Reader;

    // =========================================================================
    // 22 OCT — EL AVANCE, EN MEMORIA, PARA LA BARRA DE LA PANTALLA.
    //
    //   en_cola    pedido, esperando al cargador (unos segundos)
    //   bajando    bajando el .zip de SUNAT: Hecho / Total en bytes
    //   cargando   leyendo el archivo y copiándolo a la base: Hecho / Total en
    //              bytes del archivo leídos; Filas = RUC copiados
    //   activando  comprobando, indexando y pasándolo a usar (sin porcentaje)
    //
    // Null = no hay nada en marcha. No va a la base: si la plataforma se
    // reinicia a mitad, la carga se corta y la lista lo dice como siempre.
    // =========================================================================

    public sealed record FotoAvance(
        string Etapa, string Origen, string Archivo, DateTimeOffset Desde, DateTimeOffset EtapaDesde,
        long Hecho, long? Total, long Filas, bool Subido, string Padron = Padrones.Ruc);

    private volatile FotoAvance? _avance;
    public FotoAvance? Avance => _avance;

    // e163: ¿HAY DE VERDAD UNA CARGA TRABAJANDO? El avance es una foto en
    // memoria; si se queda puesta sin nadie detrás, la pantalla bloqueaba
    // «Descargar ahora» y «Subir archivo» para siempre. Con esto, «Destrabar»
    // sabe si puede soltarla.
    private volatile bool _trabajando;
    public bool Trabajando { get => _trabajando; set => _trabajando = value; }

    /// <summary>e163: suelta un avance que se quedó sin carga detrás.</summary>
    public void Soltar() => _avance = null;

    /// <param name="subido">true = archivo subido a mano; false = bajado de SUNAT; null = como estaba.</param>
    /// <param name="padron">045: «ruc» o «anexos»; null = como estaba.</param>
    public void Fijar(string etapa, string origen, string archivo, long hecho, long? total, long filas, bool? subido = null,
                      string? padron = null)
    {
        var antes = _avance;
        var ahora = DateTimeOffset.UtcNow;
        _avance = new FotoAvance(
            etapa, origen, archivo,
            antes?.Desde ?? ahora,
            antes is not null && antes.Etapa == etapa ? antes.EtapaDesde : ahora,
            hecho, total, filas, subido ?? antes?.Subido ?? false, padron ?? antes?.Padron ?? Padrones.Ruc);
    }

    /// <summary>Terminó (bien o mal). Si quedó otro pedido esperando, se ve como «en cola».</summary>
    public void Terminar()
    {
        _avance = null;
        if (_canal.Reader.CanCount && _canal.Reader.Count > 0)
            Fijar("en_cola", "manual", "", 0, null, 0);
    }
}


/// <summary>
/// 042 — BAJA Y CARGA EL PADRÓN REDUCIDO DE SUNAT.
///
///   · Solo, cada madrugada (desde PADRON_SUNAT_HORA, hora de Lima). Si falla,
///     reintenta hasta tres veces en el día, cada dos horas.
///   · Cuando alguien pulsa «Descargar ahora» o sube el archivo a mano.
///
/// LA CARGA NO TOCA EL PADRÓN EN USO: se llena la otra tabla y solo si salió
/// entera pasa a ser la que se usa. Si algo falla, queda anotado CON EL
/// MOTIVO EN PALABRAS en padron_cargas, que es lo que enseña la vigilancia.
///
/// SUNAT REDIRIGE DE https A http para entregar el archivo (por eso el
/// navegador lo bloquea desde su página). HttpClient no sigue solo una
/// redirección a http, así que aquí se siguen a mano, como mucho cinco.
/// </summary>
public sealed class CargadorPadron(
    ColaPadron cola, OpcionesPadron opciones, IHttpClientFactory fabrica, ILogger<CargadorPadron> log)
    : BackgroundService
{
    public const string NombreCliente = "padron-sunat";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Que la plataforma termine de arrancar antes de mirar.
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
        catch (OperationCanceledException) { return; }

        // e163: lo que quedó «en curso» de antes de arrancar no lo está haciendo
        // nadie (la plataforma se reinició a mitad). Se anota como interrumpido
        // para que no bloquee las cargas nuevas durante cuatro horas.
        if (!cola.Trabajando)
        {
            var n = await CerrarHuerfanasAsync(opciones.CadenaConexion, ct);
            if (n > 0) log.LogWarning("Padrón SUNAT: {N} carga(s) quedaron a medias al reiniciar; anotadas como interrumpidas.", n);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (cola.Lector.TryRead(out var pedido))
                    await ProcesarAsync(pedido, ct);

                if (await TocaAutomaticaAsync(Padrones.Ruc, ct))
                    await ProcesarAsync(new PedidoPadron("automatica", null, null, "Descarga automática de SUNAT", null), ct);

                // 045: y el de locales anexos, después.
                if (await TocaAutomaticaAsync(Padrones.Anexos, ct))
                    await ProcesarAsync(new PedidoPadron("automatica", null, null, "Descarga automática de SUNAT (locales anexos)",
                                                         null, Padrones.Anexos), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                // Nada de aquí puede tumbar la plataforma.
                log.LogError(e, "Padrón SUNAT: error inesperado en el cargador.");
            }

            try
            {
                using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
                espera.CancelAfter(TimeSpan.FromMinutes(5));
                await cola.Lector.WaitToReadAsync(espera.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* pasaron los 5 minutos */ }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// e163: anota como interrumpidas las cargas «en curso» que nadie está
    /// haciendo. Solo se llama cuando no hay ninguna trabajando.
    /// </summary>
    public static async Task<int> CerrarHuerfanasAsync(string cadena, CancellationToken ct)
    {
        try
        {
            await using var con = new NpgsqlConnection(cadena);
            await con.OpenAsync(ct);
            string? texto;
            await using (var est = new NpgsqlCommand("SELECT padron_estado()", con))
                texto = (string?)await est.ExecuteScalarAsync(ct);
            if (texto is null) return 0;

            using var doc = JsonDocument.Parse(texto);
            var ids = new List<Guid>();
            void Juntar(JsonElement padron)
            {
                if (padron.ValueKind != JsonValueKind.Object || !padron.TryGetProperty("cargas", out var cargas)) return;
                foreach (var c in cargas.EnumerateArray())
                    if (c.GetProperty("estado").GetString() == "en_curso" && c.TryGetProperty("id", out var id) && id.TryGetGuid(out var g))
                        ids.Add(g);
            }
            Juntar(doc.RootElement);
            if (doc.RootElement.TryGetProperty("anexos", out var anexos)) Juntar(anexos);

            foreach (var id in ids)
            {
                await using var cmd = new NpgsqlCommand("SELECT padron_fallo_carga(@id, @motivo)", con);
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("motivo", "Interrumpida: la plataforma se reinició (o se destrabó) a mitad de la carga. Vuelve a descargarla o súbela.");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return ids.Count;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return 0; }
    }

    /// <summary>¿Toca la descarga automática? Desde la hora fijada, si hoy no salió bien todavía.</summary>
    private async Task<bool> TocaAutomaticaAsync(string padron, CancellationToken ct)
    {
        if (!opciones.Automatico) return false;
        if (AhoraEnLima().Hour < opciones.Hora) return false;

        await using var con = new NpgsqlConnection(opciones.CadenaConexion);
        await con.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT padron_automaticas_hoy(@padron)", con);
        cmd.Parameters.AddWithValue("padron", padron);
        string? texto;
        try { texto = (string?)await cmd.ExecuteScalarAsync(ct); }
        catch (PostgresException e) when (e.SqlState == "42883")
        {
            // Sin la migración 045 todavía: solo el de RUC, como antes.
            if (padron != Padrones.Ruc) return false;
            await using var antes = new NpgsqlCommand("SELECT padron_automaticas_hoy()", con);
            texto = (string?)await antes.ExecuteScalarAsync(ct);
        }
        if (texto is null) return false;

        using var doc = JsonDocument.Parse(texto);
        var r = doc.RootElement;
        if (r.GetProperty("ok").GetBoolean() || r.GetProperty("enCurso").GetBoolean()) return false;

        var intentos = r.GetProperty("intentos").GetInt32();
        if (intentos >= OpcionesPadron.IntentosPorDia) return false;
        if (intentos == 0) return true;

        var ultima = r.GetProperty("ultimaEn").GetDateTimeOffset();
        return DateTimeOffset.UtcNow - ultima >= OpcionesPadron.EntreIntentos;
    }

    // =========================================================================

    private async Task ProcesarAsync(PedidoPadron p, CancellationToken ct)
    {
        var ruta = p.Ruta;
        var bytes = p.Bytes;
        cola.Fijar("en_cola", p.Origen, p.Archivo, 0, null, 0, subido: p.Ruta is not null, padron: p.Padron);
        cola.Trabajando = true;
        try
        {
            if (ruta is null)
            {
                var url = opciones.UrlDe(p.Padron);
                cola.Fijar("bajando", p.Origen, p.Archivo, 0, null, 0, subido: false, padron: p.Padron);
                log.LogInformation("Padrón SUNAT ({Padron}): bajando {Url}…", p.Padron, url);
                var (descargado, error) = await DescargarAsync(url, p.Padron, ct);
                if (descargado is null)
                {
                    log.LogWarning("Padrón SUNAT ({Padron}): no se pudo bajar. {Motivo}", p.Padron, error);
                    await AnotarFalloAsync(p.Origen, p.UsuarioId, url, error!, p.Padron, ct);
                    return;
                }
                ruta = descargado;
                bytes = new FileInfo(ruta).Length;
            }

            await CargarAsync(p with { Ruta = ruta, Bytes = bytes }, ct);
        }
        finally
        {
            if (ruta is not null)
                try { File.Delete(ruta); } catch { /* temporal: se limpia al reiniciar */ }
            cola.Trabajando = false;
            cola.Terminar();
        }
    }

    /// <summary>Baja el zip a una carpeta temporal. (ruta, null) o (null, motivo en palabras).</summary>
    private async Task<(string? Ruta, string? Error)> DescargarAsync(string direccion, string padron, CancellationToken ct)
    {
        Directory.CreateDirectory(opciones.Carpeta);
        var ruta = Path.Combine(opciones.Carpeta, $"sunat-{padron}-{DateTime.UtcNow:yyyyMMddHHmmss}.zip");
        var http = fabrica.CreateClient(NombreCliente);
        var variable = padron == Padrones.Anexos ? "PADRON_ANEXOS_URL" : "PADRON_SUNAT_URL";

        try
        {
            var url = new Uri(direccion);
            for (var salto = 0; salto < 6; salto++)
            {
                using var pet = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await http.SendAsync(pet, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } destino)
                {
                    url = destino.IsAbsoluteUri ? destino : new Uri(url, destino);
                    continue;
                }

                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    return (null, $"SUNAT rechazó la descarga ({(int)resp.StatusCode}): no deja que un programa baje el archivo. " +
                                  "Bájalo con el navegador y súbelo aquí.");
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return (null, "SUNAT respondió que el archivo no existe en esa dirección (404). Puede que lo haya movido: " +
                                  $"revisa la página del padrón reducido y, si cambió, la variable {variable}.");
                if (!resp.IsSuccessStatusCode)
                    return (null, $"SUNAT respondió {(int)resp.StatusCode} {resp.ReasonPhrase}. Suele ser pasajero: se reintenta más tarde.");

                var tipo = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (tipo.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return (null, "SUNAT devolvió una página web en vez del archivo (posible bloqueo o mantenimiento). " +
                                  "Bájalo con el navegador y súbelo aquí.");

                // Copia a mano (no CopyToAsync) para ir contando los bytes: la barra.
                var total = resp.Content.Headers.ContentLength;
                cola.Fijar("bajando", cola.Avance?.Origen ?? "manual", cola.Avance?.Archivo ?? "", 0, total, 0);
                await using (var origen = await resp.Content.ReadAsStreamAsync(ct))
                await using (var archivo = File.Create(ruta))
                {
                    var buffer = new byte[1 << 20];
                    long llevados = 0;
                    int n;
                    while ((n = await origen.ReadAsync(buffer, ct)) > 0)
                    {
                        await archivo.WriteAsync(buffer.AsMemory(0, n), ct);
                        llevados += n;
                        cola.Fijar("bajando", cola.Avance?.Origen ?? "manual", cola.Avance?.Archivo ?? "", llevados, total, 0);
                    }
                }

                var tam = new FileInfo(ruta).Length;
                if (tam < 1_000_000)
                {
                    File.Delete(ruta);
                    return (null, $"El archivo de SUNAT llegó con solo {tam:N0} bytes: incompleto. Se reintenta más tarde.");
                }

                log.LogInformation("Padrón SUNAT: bajado ({Mb:N0} MB).", tam / 1_048_576);
                return (ruta, null);
            }

            return (null, "SUNAT redirigió demasiadas veces. No se pudo llegar al archivo.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Borrar(ruta);
            return (null, "SUNAT tardó demasiado en entregar el archivo (más de 40 minutos). Se reintenta más tarde.");
        }
        catch (HttpRequestException e)
        {
            Borrar(ruta);
            return (null, $"No se pudo conectar con SUNAT ({new Uri(direccion).Host}): {e.Message}");
        }
        catch (IOException e)
        {
            Borrar(ruta);
            return (null, EsDiscoLleno(e)
                ? "No hay espacio en el disco del servidor de aplicación para guardar el archivo."
                : $"No se pudo guardar el archivo: {e.Message}");
        }
    }

    /// <summary>Lee el archivo y lo copia a la tabla libre; si salió entero, pasa a ser el padrón en uso.</summary>
    private async Task CargarAsync(PedidoPadron p, CancellationToken ct)
    {
        await using var con = new NpgsqlConnection(opciones.CadenaConexion);
        await con.OpenAsync(ct);

        // 045: el de locales anexos tiene sus propias tablas y funciones.
        var anexos = p.Padron == Padrones.Anexos;

        string texto;
        await using (var cmd = new NpgsqlCommand(
            anexos ? "SELECT anexos_preparar_carga(@origen, @usuario, @archivo, @bytes)"
                   : "SELECT padron_preparar_carga(@origen, @usuario, @archivo, @bytes)", con))
        {
            cmd.CommandTimeout = 300;
            cmd.Parameters.AddWithValue("origen", p.Origen);
            cmd.Parameters.Add(new NpgsqlParameter("usuario", NpgsqlDbType.Uuid) { Value = (object?)p.UsuarioId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("archivo", p.Archivo);
            cmd.Parameters.Add(new NpgsqlParameter("bytes", NpgsqlDbType.Bigint) { Value = (object?)p.Bytes ?? DBNull.Value });
            texto = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }

        Guid id;
        string tabla;
        using (var doc = JsonDocument.Parse(texto))
        {
            if (doc.RootElement.TryGetProperty("error", out _))
            {
                await AnotarFalloAsync(p.Origen, p.UsuarioId, p.Archivo,
                    $"Ya había otra carga del {Padrones.Nombre(p.Padron)} en curso. Espera a que termine.", p.Padron, ct);
                return;
            }
            id = doc.RootElement.GetProperty("id").GetGuid();
            tabla = doc.RootElement.GetProperty("tabla").GetString()!;
        }

        if (anexos ? tabla is not ("anexos_a" or "anexos_b") : tabla is not ("padron_ruc_a" or "padron_ruc_b"))
        {
            await FalloAsync(id, "Tabla de destino inesperada.", ct);
            return;
        }

        long filas = 0, descartadas = 0;
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var (archivo, lector, nombre) = LectorPadron.Abrir(p.Ruta!);
            await using (archivo)
            using (lector)
            {
                // El avance se mide por lo leído del archivo (del .zip, si es zip).
                var largo = archivo.Length;
                cola.Fijar("cargando", p.Origen, p.Archivo, 0, largo, 0, padron: p.Padron);

                var columnas = LectorPadron.LeerTitulos(await lector.ReadLineAsync(ct), anexos);

                await using var copia = await con.BeginBinaryImportAsync(anexos
                    ? $"COPY referencia.{tabla} (ruc, ubigeo, direccion) FROM STDIN (FORMAT BINARY)"
                    : $"COPY referencia.{tabla} (ruc, razon_social, estado, condicion, ubigeo, direccion) FROM STDIN (FORMAT BINARY)", ct);

                string? linea;
                while ((linea = await lector.ReadLineAsync(ct)) is not null)
                {
                    // 045: una fila del de anexos (RUC, ubigeo, dirección).
                    if (anexos)
                    {
                        if (LectorPadron.LeerLineaAnexo(linea, columnas) is not { } a) { descartadas++; continue; }
                        await copia.StartRowAsync(ct);
                        await copia.WriteAsync(a.Ruc, NpgsqlDbType.Text, ct);
                        await Escribir(copia, a.Ubigeo, ct);
                        await copia.WriteAsync(a.Direccion, NpgsqlDbType.Text, ct);
                        if (++filas % 50_000 == 0)
                            cola.Fijar("cargando", p.Origen, p.Archivo, Math.Min(archivo.Position, largo), largo, filas);
                        continue;
                    }

                    if (LectorPadron.LeerLinea(linea, columnas) is not { } f) { descartadas++; continue; }

                    await copia.StartRowAsync(ct);
                    await copia.WriteAsync(f.Ruc, NpgsqlDbType.Text, ct);
                    await copia.WriteAsync(f.RazonSocial, NpgsqlDbType.Text, ct);
                    await Escribir(copia, f.Estado, ct);
                    await Escribir(copia, f.Condicion, ct);
                    await Escribir(copia, f.Ubigeo, ct);
                    await Escribir(copia, f.Direccion, ct);

                    if (++filas % 50_000 == 0)
                        cola.Fijar("cargando", p.Origen, p.Archivo, Math.Min(archivo.Position, largo), largo, filas);
                    if (filas % 2_000_000 == 0)
                        log.LogInformation("Padrón SUNAT: {Filas:N0} RUC copiados…", filas);
                }

                cola.Fijar("activando", p.Origen, p.Archivo, 0, null, filas);
                await copia.CompleteAsync(ct);
                log.LogInformation("Padrón SUNAT: {Filas:N0} RUC copiados de {Nombre} en {Min:N1} min ({Malas:N0} líneas descartadas).",
                    filas, nombre, reloj.Elapsed.TotalMinutes, descartadas);
            }
        }
        catch (LectorPadron.Problema e)
        {
            await FalloAsync(id, e.Message, ct);
            return;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogError(e, "Padrón SUNAT: falló la carga.");
            await FalloAsync(id, Traducir(e, filas), ct);
            return;
        }

        // Comprobar, indexar y pasar a usarlo. Con una conexión nueva: la de la
        // copia puede haber quedado ocupada si algo se cortó.
        try
        {
            await using var con2 = new NpgsqlConnection(opciones.CadenaConexion);
            await con2.OpenAsync(ct);
            await using var fin = new NpgsqlCommand(anexos
                ? "SELECT anexos_terminar_carga(@id, @descartadas, @minimo)"
                : "SELECT padron_terminar_carga(@id, @descartadas, @minimo)", con2);
            fin.CommandTimeout = 0;
            fin.Parameters.AddWithValue("id", id);
            fin.Parameters.AddWithValue("descartadas", descartadas);
            fin.Parameters.AddWithValue("minimo", opciones.MinimoDe(p.Padron));
            var r = (string?)await fin.ExecuteScalarAsync(ct);

            if (r == "ok")
                log.LogInformation("Padrón SUNAT: listo, {Filas:N0} RUC en uso ({Min:N1} min en total).", filas, reloj.Elapsed.TotalMinutes);
            else
                log.LogWarning("Padrón SUNAT: no se usó la carga. {Motivo}", r);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogError(e, "Padrón SUNAT: falló al terminar la carga.");
            await FalloAsync(id, Traducir(e, filas), ct);
        }
    }

    private static Task Escribir(NpgsqlBinaryImporter copia, string? valor, CancellationToken ct) =>
        valor is null ? copia.WriteNullAsync(ct) : copia.WriteAsync(valor, NpgsqlDbType.Text, ct);

    private async Task FalloAsync(Guid id, string motivo, CancellationToken ct)
    {
        try
        {
            await using var con = new NpgsqlConnection(opciones.CadenaConexion);
            await con.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT padron_fallo_carga(@id, @motivo)", con);
            cmd.CommandTimeout = 300;
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("motivo", motivo);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception e) { log.LogError(e, "Padrón SUNAT: no se pudo anotar el fallo ({Motivo}).", motivo); }
    }

    private async Task AnotarFalloAsync(string origen, Guid? usuario, string archivo, string motivo, string padron, CancellationToken ct)
    {
        try
        {
            await using var con = new NpgsqlConnection(opciones.CadenaConexion);
            await con.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT padron_anotar_fallo(@origen, @usuario, @archivo, @motivo, @padron)", con);
            cmd.Parameters.AddWithValue("padron", padron);
            cmd.Parameters.AddWithValue("origen", origen);
            cmd.Parameters.Add(new NpgsqlParameter("usuario", NpgsqlDbType.Uuid) { Value = (object?)usuario ?? DBNull.Value });
            cmd.Parameters.AddWithValue("archivo", archivo);
            cmd.Parameters.AddWithValue("motivo", motivo);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception e) { log.LogError(e, "Padrón SUNAT: no se pudo anotar el fallo ({Motivo}).", motivo); }
    }

    /// <summary>El error, en palabras de quien lo va a leer.</summary>
    private static string Traducir(Exception e, long filas)
    {
        var donde = filas > 0 ? $" (iban {filas:N0} RUC copiados)" : "";
        return e switch
        {
            PostgresException { SqlState: "53100" } =>
                "La base de datos se quedó sin espacio en disco" + donde + ". No se cambió el padrón en uso.",
            PostgresException pg =>
                $"La base de datos rechazó la carga{donde}: {pg.MessageText}. No se cambió el padrón en uso.",
            IOException io when EsDiscoLleno(io) =>
                "No hay espacio en el disco del servidor de aplicación" + donde + ".",
            InvalidDataException =>
                "El .zip está dañado o incompleto" + donde + ". Vuelve a bajarlo.",
            _ => $"Falló la carga{donde}: {e.Message}. No se cambió el padrón en uso."
        };
    }

    private static bool EsDiscoLleno(IOException e) =>
        e.HResult is unchecked((int)0x80070070) or 28 ||
        e.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase);

    private static void Borrar(string ruta)
    {
        try { File.Delete(ruta); } catch { /* temporal */ }
    }

    public static DateTime AhoraEnLima()
    {
        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima"));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTime.UtcNow.AddHours(-5);
        }
    }
}