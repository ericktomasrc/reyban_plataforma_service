using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plataforma.Api.Arranque;

namespace Plataforma.Api.Cobros;

/// <summary>
/// 073 — LAS LLAVES DE CULQI, leídas del .env y de ningún otro sitio.
///
///   CULQI_LLAVE_PUBLICA     pk_test_… o pk_live_…  (la ve el navegador: es pública)
///   CULQI_LLAVE_SECRETA     sk_test_… o sk_live_…  (NUNCA sale del servidor)
///   CULQI_WEBHOOK_USUARIO   opcional: usuario y clave que pones en el aviso
///   CULQI_WEBHOOK_CLAVE     (webhook) de CulqiPanel; si están, se exigen
///
/// POR QUÉ NO EN LA BASE NI EN EL PANEL: la llave secreta cobra y devuelve
/// dinero a tu nombre. En el .env del servidor no la ve nadie que entre al
/// panel, no sale en una captura de pantalla, no viaja en una copia de la base
/// y no queda en la bitácora. Así lo hacen las tiendas serias.
///
/// PRUEBA O REAL NO SE ELIGE: lo dice la llave. pk_test + sk_test = prueba;
/// pk_live + sk_live = real. Si no coinciden, los pagos con tarjeta se paran.
/// </summary>
public sealed class OpcionesCulqi
{
    public string? Publica { get; init; }
    public string? Secreta { get; init; }
    public string? WebhookUsuario { get; init; }
    public string? WebhookClave { get; init; }

    public static OpcionesCulqi Leer() => new()
    {
        Publica        = Limpia("CULQI_LLAVE_PUBLICA"),
        Secreta        = Limpia("CULQI_LLAVE_SECRETA"),
        WebhookUsuario = Limpia("CULQI_WEBHOOK_USUARIO"),
        WebhookClave   = Limpia("CULQI_WEBHOOK_CLAVE"),
    };

    private static string? Limpia(string nombre) =>
        Environment.GetEnvironmentVariable(nombre)?.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>sin_llaves · prueba · real · no_coinciden · mal_escritas</summary>
    public string EstadoLlaves
    {
        get
        {
            if (Publica is null && Secreta is null) return "sin_llaves";
            if (Publica is null || Secreta is null) return "mal_escritas";
            var p = Publica.StartsWith("pk_test_", StringComparison.Ordinal) ? "prueba"
                  : Publica.StartsWith("pk_live_", StringComparison.Ordinal) ? "real" : null;
            var s = Secreta.StartsWith("sk_test_", StringComparison.Ordinal) ? "prueba"
                  : Secreta.StartsWith("sk_live_", StringComparison.Ordinal) ? "real" : null;
            if (p is null || s is null) return "mal_escritas";
            return p == s ? p : "no_coinciden";
        }
    }

    public bool WebhookProtegido => WebhookUsuario is not null && WebhookClave is not null;

    /// <summary>Para enseñar en el panel sin enseñarla entera: pk_test_…a1b2.</summary>
    public string? PublicaEnmascarada =>
        Publica is null ? null : Publica.Length <= 12 ? "configurada" : Publica[..8] + "…" + Publica[^4..];
}

/// <summary>El modo en que de verdad está la plataforma ahora, y por qué.</summary>
public sealed record EstadoModo(string Modo, string Guardado, string? Problema, string? Aviso)
{
    public bool ConTarjeta => Modo is "simulado" or "prueba" or "real";
    public bool SinDineroReal => Modo is "simulado" or "prueba";

    /// <summary>
    /// guardado: lo que eligió la plataforma en el panel (apagado / simulado /
    /// culqi). produccion: PLATAFORMA_SUNAT=real.
    /// </summary>
    public static EstadoModo Calcular(string guardado, OpcionesCulqi llaves, bool produccion)
    {
        switch (guardado)
        {
            case "simulado":
                return produccion
                    ? new("apagado", guardado, "En producción el modo simulado está bloqueado: nadie puede «pagar de mentira». Elige Culqi.", null)
                    : new("simulado", guardado, null, null);

            case "culqi":
                return llaves.EstadoLlaves switch
                {
                    "sin_llaves"   => new("apagado", guardado, "Faltan las llaves de Culqi en el .env del servidor (CULQI_LLAVE_PUBLICA y CULQI_LLAVE_SECRETA).", null),
                    "mal_escritas" => new("apagado", guardado, "Las llaves de Culqi del .env están incompletas o mal copiadas: la pública empieza en pk_ y la secreta en sk_.", null),
                    "no_coinciden" => new("apagado", guardado, "Las llaves de Culqi no son del mismo tipo: una es de prueba y la otra es real.", null),
                    "prueba"       => new("prueba", guardado, null, produccion
                                        ? "Estás en PRODUCCIÓN con llaves de PRUEBA: los clientes «pagan», pero no entra dinero. Cambia a las llaves reales cuando Culqi apruebe tu cuenta."
                                        : null),
                    _              => new("real", guardado, null, produccion
                                        ? null
                                        : "Llaves REALES en un entorno que no es producción: los cobros serán de verdad."),
                };

            default:
                return new("apagado", "apagado", null, null);
        }
    }
}

/// <summary>Lo que respondió la pasarela, ya entendido.</summary>
public sealed record ResultadoPasarela(
    bool Ok, bool Requiere3ds, string? Id, string? Codigo, string? Mensaje, JsonNode? Cuerpo = null, bool SinRespuesta = false)
{
    public static ResultadoPasarela Falla(string codigo, string mensaje, bool sinRespuesta = false) =>
        new(false, false, null, codigo, mensaje, null, sinRespuesta);
}

/// <summary>Los datos del 3DS que devuelve Culqi3DS en el navegador.</summary>
public sealed record Autenticacion3ds(string? Eci, string? Xid, string? Cavv, string? ProtocolVersion, string? DirectoryServerTransactionId);

/// <summary>
/// 073 — HABLAR CON CULQI (API v2).
///
/// Solo lo que hace falta: crear el cliente, guardar su tarjeta (con 3DS si
/// el banco lo pide), cobrar a la tarjeta guardada, leer un cargo o una
/// devolución (para creer un aviso) y probar la conexión.
///
/// LA TARJETA NUNCA PASA POR AQUÍ: el cliente la escribe en el formulario de
/// Culqi, que nos da un token de un solo uso (dura 5 minutos). Con ese token
/// Culqi guarda la tarjeta y nos da su código (crd_…). Nosotros solo
/// guardamos ese código, la marca y los cuatro últimos.
/// </summary>
public sealed class ClienteCulqi(IHttpClientFactory fabrica, OpcionesCulqi llaves, ILogger<ClienteCulqi> log)
{
    public const string NombreCliente = "culqi";
    public const string Base = "https://api.culqi.com/v2/";

    private HttpClient Http()
    {
        var c = fabrica.CreateClient(NombreCliente);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", llaves.Secreta);
        return c;
    }

    public Task<ResultadoPasarela> CrearClienteAsync(JsonElement datos, CancellationToken ct)
    {
        var nombre = Texto(datos, "nombre") ?? "Cliente";
        var partes = nombre.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cuerpo = new JsonObject
        {
            ["first_name"]    = Recortar(partes.ElementAtOrDefault(0) ?? "Cliente", 50),
            ["last_name"]     = Recortar(partes.ElementAtOrDefault(1) ?? (Texto(datos, "tipoDoc") == "ruc" ? "Empresa" : "Cliente"), 50),
            ["email"]         = Texto(datos, "correo"),
            ["address"]       = Recortar(Texto(datos, "direccion") ?? "Lima", 100),
            ["address_city"]  = "Lima",
            ["country_code"]  = "PE",
            ["phone_number"]  = (Texto(datos, "telefono") ?? "").Replace(" ", "").Replace("+51", ""),
            ["metadata"]      = new JsonObject { ["cuenta"] = Texto(datos, "cuenta"), ["documento"] = Texto(datos, "numero") },
        };
        return EnviarAsync(HttpMethod.Post, "customers", cuerpo, ct);
    }

    /// <summary>Si el correo ya tenía cliente en Culqi (otra prueba, un reinicio de base), se reutiliza.</summary>
    public async Task<string?> BuscarClienteAsync(string correo, CancellationToken ct)
    {
        var r = await EnviarAsync(HttpMethod.Get, "customers?email=" + Uri.EscapeDataString(correo) + "&limit=1", null, ct);
        return r.Cuerpo?["data"] is JsonArray a && a.Count > 0 ? a[0]?["id"]?.GetValue<string>() : null;
    }

    public Task<ResultadoPasarela> CrearTarjetaAsync(string cliente, string token, Autenticacion3ds? auth, CancellationToken ct)
    {
        var cuerpo = new JsonObject { ["customer_id"] = cliente, ["token_id"] = token };
        if (auth is not null) cuerpo["authentication_3DS"] = Auth3ds(auth);
        return EnviarAsync(HttpMethod.Post, "cards", cuerpo, ct);
    }

    public Task<ResultadoPasarela> CobrarAsync(string tarjeta, string correo, decimal monto, string descripcion,
                                               Guid cobro, Guid cuenta, CancellationToken ct)
    {
        var cuerpo = new JsonObject
        {
            ["amount"]        = (long)Math.Round(monto * 100m, MidpointRounding.AwayFromZero),
            ["currency_code"] = "PEN",
            ["email"]         = correo,
            ["source_id"]     = tarjeta,
            ["description"]   = Recortar(descripcion, 80),
            ["capture"]       = true,
            // El cobro va en la metadata: si la respuesta se pierde, el aviso
            // de Culqi dice a qué pago pertenece.
            ["metadata"]      = new JsonObject { ["cobro_id"] = cobro.ToString(), ["cuenta_id"] = cuenta.ToString() },
        };
        return EnviarAsync(HttpMethod.Post, "charges", cuerpo, ct);
    }

    public Task<ResultadoPasarela> LeerCargoAsync(string id, CancellationToken ct) =>
        EnviarAsync(HttpMethod.Get, "charges/" + Uri.EscapeDataString(id), null, ct);

    public Task<ResultadoPasarela> LeerDevolucionAsync(string id, CancellationToken ct) =>
        EnviarAsync(HttpMethod.Get, "refunds/" + Uri.EscapeDataString(id), null, ct);

    /// <summary>Pide la lista de cargos (uno): no cobra nada y dice si la llave secreta sirve.</summary>
    public Task<ResultadoPasarela> ProbarAsync(CancellationToken ct) =>
        EnviarAsync(HttpMethod.Get, "charges?limit=1", null, ct);

    // -------------------------------------------------------------------------

    private async Task<ResultadoPasarela> EnviarAsync(HttpMethod metodo, string ruta, JsonObject? cuerpo, CancellationToken ct)
    {
        if (llaves.Secreta is null)
            return ResultadoPasarela.Falla("sin_llaves", "Faltan las llaves de Culqi en el servidor.");

        using var peticion = new HttpRequestMessage(metodo, ruta);
        if (cuerpo is not null)
            peticion.Content = new StringContent(cuerpo.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage respuesta;
        string texto;
        try
        {
            respuesta = await Http().SendAsync(peticion, ct);
            texto = await respuesta.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Culqi no respondió ({Ruta}): {Tipo}", RutaSinDatos(ruta), e.GetType().Name);
            return ResultadoPasarela.Falla("sin_respuesta",
                "No pudimos hablar con Culqi. No se cobró nada; inténtalo en unos minutos.", sinRespuesta: true);
        }

        JsonNode? json = null;
        try { json = JsonNode.Parse(texto); } catch (JsonException) { }

        var status = (int)respuesta.StatusCode;
        if (status == 401)
        {
            log.LogWarning("Culqi rechazó la llave secreta ({Ruta}).", RutaSinDatos(ruta));
            return ResultadoPasarela.Falla("llave_invalida", "Culqi no aceptó la llave secreta del servidor.");
        }

        // 3DS: el banco pide que el cliente confirme. Viene con action_code
        // REVIEW (el código HTTP cambia según la versión; se mira el cuerpo).
        if (json?["action_code"]?.GetValue<string>() is "REVIEW")
            return new(false, true, null, "3ds", json["user_message"]?.GetValue<string>() ?? "Tu banco pide confirmar.", json);

        if (status >= 400 || json?["object"]?.GetValue<string>() == "error" || json is null)
        {
            var codigo = Texto(json, "decline_code") ?? Texto(json, "code") ?? Texto(json, "type") ?? $"http_{status}";
            var mensaje = Texto(json, "user_message") ?? Texto(json, "merchant_message") ?? "Culqi no aceptó la operación.";
            log.LogInformation("Culqi respondió {Status} {Codigo} en {Ruta}.", status, codigo, RutaSinDatos(ruta));
            return new(false, false, Texto(json, "charge_id"), codigo, mensaje, json);
        }

        return new(true, false, Texto(json, "id"), null, null, json);
    }

    private static JsonObject Auth3ds(Autenticacion3ds a) => new()
    {
        ["eci"] = a.Eci, ["xid"] = a.Xid, ["cavv"] = a.Cavv,
        ["protocolVersion"] = a.ProtocolVersion, ["directoryServerTransactionId"] = a.DirectoryServerTransactionId,
    };

    private static string RutaSinDatos(string ruta) => ruta.Split('?', '/')[0];

    private static string Recortar(string s, int n) => s.Length <= n ? s : s[..n];

    public static string? Texto(JsonNode? n, string clave) =>
        n is JsonObject o && o[clave] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string? Texto(JsonElement e, string clave) =>
        e.TryGetProperty(clave, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>¿Este cargo de Culqi está cobrado de verdad?</summary>
    public static bool CargoExitoso(JsonNode? cargo)
    {
        var outcome = cargo?["outcome"];
        var tipo = Texto(outcome, "type");
        var codigo = Texto(outcome, "code");
        return tipo == "venta_exitosa" || codigo == "AUT0000";
    }

    /// <summary>La marca y los cuatro últimos de una tarjeta guardada.</summary>
    public static (string Marca, string Ultimos4) Tarjeta(JsonNode? tarjeta)
    {
        var token = tarjeta?["source"] ?? tarjeta?["token"];
        var marca = Texto(token?["iin"], "card_brand") ?? "Tarjeta";
        var ult = Texto(token, "last_four") ?? Texto(token, "card_number")?[^4..] ?? "0000";
        return (marca, ult);
    }
}

/// <summary>
/// 073 — EL MODO SIMULADO. Sin Culqi y sin dinero.
///
/// El formulario simulado de la pantalla solo acepta las tarjetas de prueba
/// de la lista (cualquier otra se rechaza ahí mismo, así nadie escribe una
/// real) y NO manda el número: manda un token «sim_tok_{caso}_{últimos4}_{marca}».
///
///   4111 1111 1111 1111  aprobada         se guarda y cobra bien
///   4000 0000 0000 0002  sin fondos       se guarda, pero el cobro se rechaza
///   4000 0000 0000 0119  rechazada        el banco no deja guardarla
///   4000 0000 0000 3220  pide 3DS         hay que confirmar (simulado) y luego cobra bien
///
/// En producción este modo no existe (EstadoModo.Calcular).
/// </summary>
public static class PasarelaSimulada
{
    private static readonly string[] Casos = ["aprobada", "fondos", "rechazada", "3ds"];

    public static ResultadoPasarela GuardarTarjeta(string token, Autenticacion3ds? auth, out string marca, out string ultimos4)
    {
        marca = "Tarjeta"; ultimos4 = "0000";
        var p = token.Split('_');
        if (p.Length != 5 || p[0] != "sim" || p[1] != "tok" || !Casos.Contains(p[2]) ||
            p[3].Length != 4 || !p[3].All(char.IsAsciiDigit) || p[4] is not ("Visa" or "Mastercard" or "Amex" or "Diners"))
            return ResultadoPasarela.Falla("token_invalido", "Esa tarjeta no es una de las tarjetas de prueba.");

        marca = p[4]; ultimos4 = p[3];
        switch (p[2])
        {
            case "rechazada":
                return ResultadoPasarela.Falla("card_declined", "Tu banco no autorizó esta tarjeta. Usa otra.");
            case "3ds" when auth?.Cavv != "sim_cavv_ok":
                return new(false, true, null, "3ds", "Tu banco pide confirmar que eres tú.");
        }
        // El caso queda en el código de la tarjeta para saber cómo cobrarle.
        return new(true, false, $"sim_crd_{p[2]}_{Guid.NewGuid():N}"[..32], null, null);
    }

    public static ResultadoPasarela Cobrar(string tarjeta) =>
        tarjeta.StartsWith("sim_crd_fondos_", StringComparison.Ordinal)
            ? new(false, false, $"sim_chr_{Guid.NewGuid():N}", "insufficient_funds",
                  "Fondos insuficientes. Usa otra tarjeta o recarga esta (tarjeta de prueba).")
            : new(true, false, $"sim_chr_{Guid.NewGuid():N}", null, null);
}