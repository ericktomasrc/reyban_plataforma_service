using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// El receptor del webhook del servicio de facturación.
///
/// ES EL ÚNICO ENDPOINT DE LA PLATAFORMA QUE SE AUTENTICA POR FIRMA, no por
/// sesión. Quien llama es el servicio, no una persona: no hay cookie, no hay
/// usuario, no hay permiso que pedir. Lo único que hace creíble un aviso es
/// que su firma cuadre con el secreto de la empresa.
///
/// ══════════════════════════════════════════════════════════════════════════
/// EL ORDEN NO ES INTERCAMBIABLE
///
///   1. Leer el cuerpo CRUDO. La firma se calculó sobre esos bytes;
///      deserializar y volver a serializar cambia espacios y orden de claves.
///   2. Fijar el contexto con la empresa de la URL y SIN usuario. Todavía no
///      se ha creído nada: solo se necesita para leer su credencial, y el
///      aislamiento no deja leer la de ninguna otra.
///   3. Comprobar marca de tiempo y firma.
///   4. Guardar el aviso en `webhooks_recibidos`, VALGA O NO. Los inválidos,
///      sin empresa y recortados: dicen quién llama a la puerta, pero no
///      pueden llenar la base con 64 KB de basura cada uno.
///   5. Si no valía: 401 y parar. No se lee el contenido.
///   6. Si valía: aplicar el cambio de estado dentro de un punto de guardado,
///      para que un fallo al aplicar no se lleve por delante el registro del
///      aviso.
/// ══════════════════════════════════════════════════════════════════════════
///
/// POR QUÉ LA EMPRESA VA EN LA URL: el aviso no dice de qué empresa es, y el
/// secreto es por empresa. Sin la empresa no se sabe con qué secreto
/// comprobarlo. Que la URL la diga no le da nada a quien la falsifique: sin el
/// secreto de ESA empresa, la firma no cuadra.
///
/// POR QUÉ SIN USUARIO: `fn_historial_documento` decide el origen del cambio
/// mirando si hay usuario en el contexto. Sin usuario, el historial dice
/// «webhook», que es la verdad. Con uno, diría «usuario» y la ficha del
/// comprobante mentiría.
///
/// QUÉ SE RESPONDE, Y POR QUÉ IMPORTA: el servicio NO reintenta un 4xx (salvo
/// el 429) y SÍ reintenta un 5xx, hasta ocho veces con espera creciente.
///
///   200  aplicado, duplicado ya aplicado, aviso viejo, aviso de prueba, o
///        algo que reintentar no arreglaría (queda pendiente en nuestra tabla)
///   401  firma o marca de tiempo inválidas — hay que corregir la
///        configuración, no insistir
///   413  cuerpo demasiado grande
///   503  el comprobante todavía no está en la plataforma (ver abajo), o
///        falló al aplicar — que lo reintente
/// </summary>
public static class EndpointsWebhookFacturacion
{
    /// <summary>Un aviso real pesa menos de 1 KB. 64 KB es margen de sobra.</summary>
    public const int TamanoMaximo = 64 * 1024;

    /// <summary>
    /// Cuánto puede separarse la marca de tiempo del aviso de nuestro reloj.
    ///
    /// La marca entra en la firma para que un aviso capturado no se pueda
    /// reenviar más tarde. Cinco minutos cubren relojes algo desajustados; el
    /// servicio firma de nuevo en cada reintento, así que un reintento de
    /// dentro de tres horas llega con marca fresca.
    /// </summary>
    public static readonly TimeSpan Tolerancia = TimeSpan.FromMinutes(5);

    /// <summary>De un aviso con firma inválida se guarda solo el principio.</summary>
    private const int RecorteInvalidos = 2_000;

    private const string PuntoGuardado = "aplicar_webhook";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);


    public static void MapearWebhookFacturacion(this IEndpointRouteBuilder rutas)
    {
        // SIN RequiereSesion, SIN RequierePermiso, SIN RequiereModulo — a
        // propósito, y es el único endpoint así junto al login y la
        // invitación. Lo protege la firma, que se comprueba antes de mirar
        // nada del contenido.
        //
        // Sin RequiereModulo, además, porque una empresa que dejó de tener el
        // módulo sigue teniendo comprobantes emitidos, y SUNAT sigue
        // contestando sobre ellos.
        rutas.MapPost("/api/webhooks/facturacion/{empresaId:guid}", RecibirAsync)
             .WithTags("Facturación · webhook")
             .WithSummary("Recibe los avisos firmados del servicio de facturación")
             .WithDescription(
                 "Lo llama el servicio, no una persona. Exige X-Facturacion-Timestamp " +
                 "y X-Facturacion-Firma = HMAC-SHA256 de 'timestamp.cuerpo' con el " +
                 "secreto whsec_ de la empresa, en hexadecimal minúsculo.");
    }


    // =========================================================================

    private static async Task<IResult> RecibirAsync(
        Guid empresaId,
        HttpRequest peticion,
        ContextoPlataforma db,
        ContextoPeticion ctx,
        Cifrador cifrador,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        var log = logs.CreateLogger("Facturacion.Webhook");

        // --- 1. El cuerpo, crudo y con tope ---------------------------------
        var cuerpo = await LeerCuerpoAsync(peticion, ct);
        if (cuerpo is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var marca    = Cabecera(peticion, "X-Facturacion-Timestamp");
        var firma    = Cabecera(peticion, "X-Facturacion-Firma");
        var eventoId = Cabecera(peticion, "X-Facturacion-Evento");
        var tipo     = Cabecera(peticion, "X-Facturacion-Tipo");

        // --- 2. Contexto: la empresa de la URL, sin usuario ------------------
        //
        // MiddlewareContexto ya abrió la transacción y fijó un contexto
        // anónimo. Se vuelve a fijar dentro de la MISMA transacción, que es la
        // única forma en que el valor sobrevive a la siguiente sentencia.
        await db.FijarContextoAsync(
            null, empresaId, ctx.Ip?.ToString(), "webhook del servicio de facturación", false, ct);

        var credencial = await db.CredencialesFacturacion
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.EmpresaId == empresaId, ct);

        // --- 3. ¿Es auténtico? ---------------------------------------------
        var motivo = Comprobar(credencial, cifrador, marca, firma, cuerpo);

        if (motivo is not null)
        {
            // --- 4 y 5. Se guarda, sin empresa, y se para -----------------
            await InsertarAsync(db, ct,
                empresa: null,
                eventoId: eventoId,
                tipo: tipo,
                firmaValida: false,
                ip: ctx.Ip,
                cuerpoJson: CuerpoInvalido(cuerpo),
                error: $"{motivo} (empresa de la URL: {empresaId})");

            log.LogWarning(
                "Aviso de facturación rechazado para la empresa {Empresa} desde {Ip}: {Motivo}",
                empresaId, ctx.Ip, motivo);

            // No se dice CUÁL de las comprobaciones falló: a quien prueba
            // firmas a ciegas no hay que decirle si va mejorando. El motivo
            // exacto queda en la tabla y en el log.
            return Results.Json(new { error = "Aviso no válido." },
                                statusCode: StatusCodes.Status401Unauthorized);
        }

        // --- Idempotencia ----------------------------------------------------
        //
        // El servicio reintenta con el MISMO evento_id. Si ya lo tenemos:
        // aplicado → se contesta 200 sin tocar nada; pendiente → se vuelve a
        // intentar aplicar sobre la fila que ya existe.
        var previo = await BuscarPrevioAsync(db, eventoId, ct);

        if (previo is { ProcesadoEn: not null })
            return Results.Ok(new { recibido = true, duplicado = true });

        var texto = Encoding.UTF8.GetString(cuerpo);

        var recibidoId = previo?.Id ?? await InsertarAsync(db, ct,
            empresa: empresaId,
            eventoId: eventoId,
            tipo: tipo,
            firmaValida: true,
            ip: ctx.Ip,
            cuerpoJson: texto,
            error: null);

        // --- 6. Aplicar, en un punto de guardado ----------------------------
        //
        // Si aplicar falla, se vuelve al punto de guardado y el aviso sigue
        // registrado con su error. Sin esto, la excepción deshacía la
        // transacción entera del middleware y el aviso desaparecía como si
        // nunca hubiera llegado.
        var transaccion = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "El receptor del webhook necesita la transacción de MiddlewareContexto.");

        await transaccion.CreateSavepointAsync(PuntoGuardado, ct);

        Resultado r;
        try
        {
            r = await AplicarAsync(db, texto, log, ct);
        }
        catch (Exception e)
        {
            await transaccion.RollbackToSavepointAsync(PuntoGuardado, ct);
            db.ChangeTracker.Clear();

            log.LogError(e, "Falló al aplicar el aviso {Evento} de la empresa {Empresa}.",
                         eventoId, empresaId);

            await MarcarAsync(db, recibidoId, procesado: false,
                              $"Falló al aplicar: {Recortar(e.Message, 400)}", null, ct);

            return Results.Json(new { error = "No se pudo aplicar el aviso. Reintentar." },
                                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await MarcarAsync(db, recibidoId, r.Procesado, r.Nota, r.DocumentoId, ct);

        if (r.Reintentar)
        {
            return Results.Json(new { error = r.Nota },
                                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new { recibido = true, aplicado = r.Aplicado });
    }


    // =========================================================================
    // COMPROBAR
    // =========================================================================

    /// <summary>Null si el aviso es auténtico; si no, el motivo, para la tabla y el log.</summary>
    private static string? Comprobar(
        CredencialFacturacion? credencial, Cifrador cifrador,
        string? marca, string? firma, byte[] cuerpo)
    {
        if (credencial is null)
            return "La empresa no tiene configurado el servicio de facturación.";

        if (credencial.WebhookSecretoCifrado is not { Length: > 0 })
            return "La empresa no tiene guardado el secreto del webhook.";

        if (!long.TryParse(marca, out var segundos))
            return "Falta la marca de tiempo o no es un número.";

        var enviado = DateTimeOffset.FromUnixTimeSeconds(segundos);
        if ((DateTimeOffset.UtcNow - enviado).Duration() > Tolerancia)
            return $"La marca de tiempo está fuera de plazo ({enviado:u}).";

        if (string.IsNullOrWhiteSpace(firma))
            return "Falta la firma.";

        byte[] recibida;
        try { recibida = Convert.FromHexString(firma.Trim()); }
        catch (FormatException) { return "La firma no es hexadecimal."; }

        var secreto = cifrador.Descifrar(credencial.WebhookSecretoCifrado);

        // Exactamente lo que firma el servicio: HMAC-SHA256 de
        // "timestamp.cuerpo", con el secreto entero —prefijo whsec_ incluido—
        // como llave. Ver DespachadorWebhooks.Firmar.
        var prefijo = Encoding.UTF8.GetBytes(marca + ".");
        var contenido = new byte[prefijo.Length + cuerpo.Length];
        prefijo.CopyTo(contenido, 0);
        cuerpo.CopyTo(contenido, prefijo.Length);

        var esperada = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), contenido);

        // Tiempo constante: una comparación que se para en el primer byte
        // distinto dice, midiendo, cuántos bytes acertó quien prueba.
        return CryptographicOperations.FixedTimeEquals(esperada, recibida)
            ? null
            : "La firma no coincide.";
    }


    // =========================================================================
    // APLICAR
    // =========================================================================

    private sealed record Resultado(
        bool Procesado, bool Aplicado, bool Reintentar, string? Nota, Guid? DocumentoId);

    /// <summary>
    /// Lo que manda `encolar_evento_comprobante` en el servicio. Solo lo que
    /// usamos; el resto del cuerpo se queda guardado en `webhooks_recibidos`.
    /// </summary>
    private sealed record Aviso(
        string? Tipo,
        Guid? ComprobanteId,
        string? Estado,
        string? CodigoSunat,
        string? MensajeSunat,
        decimal? ImporteTotal,
        DateTimeOffset? OcurridoEn);

    private static async Task<Resultado> AplicarAsync(
        ContextoPlataforma db, string texto, ILogger log, CancellationToken ct)
    {
        Aviso? a;
        try { a = JsonSerializer.Deserialize<Aviso>(texto, Json); }
        catch (JsonException e)
        {
            // Firmado por el servicio y aun así ilegible: reintentar no lo
            // arregla. Se contesta 200 y queda pendiente para que alguien lo
            // mire.
            return new(false, false, false, $"Cuerpo firmado pero ilegible: {Recortar(e.Message, 200)}", null);
        }

        if (a is null)
            return new(false, false, false, "Cuerpo vacío.", null);

        // El aviso de prueba del panel del servicio. No toca nada: sirve para
        // comprobar la configuración sin esperar a una factura.
        if (a.Tipo == "prueba")
            return new(true, false, false, "Aviso de prueba recibido.", null);

        if (a.ComprobanteId is not { } comprobanteId)
            return new(false, false, false, $"Aviso «{a.Tipo}» sin comprobanteId.", null);

        var nuevo = Traducir(a.Estado);
        if (nuevo is null)
            return new(false, false, false, $"Estado del servicio desconocido: «{a.Estado}».", null);

        if (a.OcurridoEn is not { } ocurrido)
            return new(false, false, false, "Aviso sin ocurridoEn: no se puede ordenar.", null);

        // IgnoreQueryFilters por si el documento estuviera desactivado: lo que
        // diga SUNAT de un comprobante emitido es un hecho, lo miremos o no.
        // El aislamiento sigue mandando: solo existen los de esta empresa.
        var d = await db.Documentos
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.ComprobanteId == comprobanteId, ct);

        if (d is null)
        {
            // ══════════════════════════════════════════════════════════════
            // TODAVÍA NO EXISTE AQUÍ, Y ESO PUEDE SER UNA CARRERA.
            //
            // `comprobante_id` lo guarda ServicioEmision en su paso 3, DESPUÉS
            // de que el servicio conteste. Si SUNAT fuera muy rápido —o el
            // servicio devolviera un duplicado ya resuelto—, el aviso podría
            // llegar antes que ese guardado. Se pide reintento: al siguiente
            // intento el documento ya estará.
            //
            // Si de verdad no es nuestro —un comprobante emitido desde otro
            // sistema con la misma clave—, el servicio lo agotará tras ocho
            // intentos y quedará a la vista en los dos lados.
            // ══════════════════════════════════════════════════════════════
            return new(false, false, true,
                $"No hay ningún documento con el comprobante {comprobanteId} (todavía).", null);
        }

        var utc = ocurrido.UtcDateTime;

        // ══════════════════════════════════════════════════════════════════
        // UN AVISO VIEJO NO PISA A UNO NUEVO.
        //
        // Los reintentos del servicio pueden llegar desordenados. Sin esta
        // comprobación, un «aceptado» que se quedó reintentando llegaría
        // después del «anulado» y la factura anulada volvería a decir
        // aceptada.
        // ══════════════════════════════════════════════════════════════════
        if (d.EstadoServicioEn is { } ultimo && utc <= ultimo)
        {
            log.LogInformation(
                "Aviso del documento {Id} ignorado: ocurrió {Aviso:o}, el último aplicado es de {Ultimo:o}.",
                d.Id, utc, ultimo);

            return new(true, false, false,
                $"Más viejo que el último aplicado ({ultimo:o}); no se aplica.", d.Id);
        }

        if (d.Estado is EstadoDocumento.Borrador or EstadoDocumento.Error)
        {
            // Un borrador o un error no tienen comprobante en el servicio. Que
            // llegue un aviso para uno de ellos es incoherente, y mejor que lo
            // mire alguien que aplicarlo a ciegas.
            return new(false, false, false,
                $"El documento {d.Id} está en «{d.Estado}» y no debería recibir avisos.", d.Id);
        }

        d.Estado           = nuevo;
        d.CodigoSunat      = a.CodigoSunat;
        d.MensajeSunat     = a.MensajeSunat;
        d.EstadoServicioEn = utc;

        // El importe del servicio manda. Normalmente ya es el mismo desde la
        // emisión; si no lo fuera, este es el que se firmó.
        if (a.ImporteTotal is { } total)
            d.ImporteTotal = total;

        // El historial lo escribe fn_historial_documento, con origen
        // 'webhook' porque el contexto no tiene usuario.
        await db.SaveChangesAsync(ct);

        return new(true, true, false, null, d.Id);
    }


    /// <summary>
    /// El vocabulario del servicio, traducido al nuestro.
    ///
    /// Solo los cuatro que el servicio avisa (encolar_evento_comprobante). Un
    /// estado nuevo allí devuelve null aquí y el aviso queda pendiente con su
    /// motivo, en vez de escribir en `documentos` un valor que su CHECK no
    /// admite.
    /// </summary>
    private static string? Traducir(string? estado) => estado?.ToUpperInvariant() switch
    {
        "ACEPTADO"                   => EstadoDocumento.Aceptado,
        "ACEPTADO_CON_OBSERVACIONES" => EstadoDocumento.Observado,
        "RECHAZADO"                  => EstadoDocumento.Rechazado,
        "ANULADO"                    => EstadoDocumento.Anulado,
        _                            => null
    };


    // =========================================================================
    // LA TABLA webhooks_recibidos
    //
    // Por SQL y no por EF: no tiene entidad, y no la necesita. Se escribe en
    // tres sitios de este archivo y en ningún otro. Va por la conexión y la
    // transacción del contexto, que son las que llevan fijado el contexto.
    // =========================================================================

    private sealed record Previo(long Id, DateTime? ProcesadoEn);

    private static async Task<Previo?> BuscarPrevioAsync(
        ContextoPlataforma db, string? eventoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventoId)) return null;

        await using var cmd = Comando(db, """
            SELECT id, procesado_en
              FROM webhooks_recibidos
             WHERE evento_id = @evento AND firma_valida
            """, ("evento", eventoId));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new Previo(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetDateTime(1));
    }

    private static async Task<long> InsertarAsync(
        ContextoPlataforma db, CancellationToken ct,
        Guid? empresa, string? eventoId, string? tipo, bool firmaValida,
        System.Net.IPAddress? ip, string cuerpoJson, string? error)
    {
        await using var cmd = Comando(db, """
            INSERT INTO webhooks_recibidos
                (empresa_id, evento_id, tipo_evento, firma_valida, ip, cuerpo, error_proceso)
            VALUES
                (@empresa::uuid, @evento::text, @tipo::text, @valida, @ip::inet, @cuerpo::jsonb, @error::text)
            RETURNING id
            """,
            ("empresa", empresa),
            ("evento",  Recortar(eventoId, 100)),
            ("tipo",    Recortar(tipo, 60)),
            ("valida",  firmaValida),
            ("ip",      ip),
            ("cuerpo",  cuerpoJson),
            ("error",   Recortar(error, 500)));

        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Solo las tres columnas que el disparador de la 005 deja tocar. El
    /// cuerpo, la firma y la hora de llegada no se modifican nunca.
    /// </summary>
    private static async Task MarcarAsync(
        ContextoPlataforma db, long id, bool procesado, string? nota,
        Guid? documentoId, CancellationToken ct)
    {
        await using var cmd = Comando(db, """
            UPDATE webhooks_recibidos
               SET procesado_en  = CASE WHEN @procesado THEN now() ELSE NULL END,
                   error_proceso = @nota::text,
                   documento_id  = COALESCE(@documento::uuid, documento_id)
             WHERE id = @id
            """,
            ("procesado", procesado),
            ("nota",      Recortar(nota, 500)),
            ("documento", documentoId),
            ("id",        id));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Comando(
        ContextoPlataforma db, string sql, params (string Nombre, object? Valor)[] parametros)
    {
        var conexion = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaccion = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();

        var cmd = new NpgsqlCommand(sql, conexion, transaccion);
        foreach (var (nombre, valor) in parametros)
            cmd.Parameters.AddWithValue(nombre, valor ?? DBNull.Value);

        return cmd;
    }


    // =========================================================================

    /// <summary>El cuerpo entero, o null si pasa del tope. Sin leer más de la cuenta.</summary>
    private static async Task<byte[]?> LeerCuerpoAsync(HttpRequest peticion, CancellationToken ct)
    {
        if (peticion.ContentLength > TamanoMaximo) return null;

        using var destino = new MemoryStream();
        var bufer = ArrayPool<byte>.Shared.Rent(8192);

        try
        {
            int leidos;
            while ((leidos = await peticion.Body.ReadAsync(bufer.AsMemory(0, bufer.Length), ct)) > 0)
            {
                if (destino.Length + leidos > TamanoMaximo) return null;
                destino.Write(bufer, 0, leidos);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufer);
        }

        return destino.ToArray();
    }

    /// <summary>
    /// Lo que se guarda de un aviso que no pasó la firma: el principio, como
    /// texto, dentro de un JSON válido — la columna es jsonb y lo que llega
    /// puede no serlo.
    /// </summary>
    private static string CuerpoInvalido(byte[] cuerpo)
    {
        var texto = Encoding.UTF8.GetString(cuerpo, 0, Math.Min(cuerpo.Length, RecorteInvalidos));
        return JsonSerializer.Serialize(new { crudo = texto, bytes = cuerpo.Length });
    }

    private static string? Cabecera(HttpRequest peticion, string nombre) =>
        peticion.Headers.TryGetValue(nombre, out var v) ? v.ToString() : null;

    private static string? Recortar(string? texto, int largo) =>
        texto is null ? null : texto.Length <= largo ? texto : texto[..largo];
}