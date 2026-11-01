using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plataforma.Api.Arranque;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Cobros;

public sealed record PeticionDatosPago(string? TipoDoc, string? Numero, string? Nombre, string? Direccion, string? Correo, string? Telefono);
public sealed record PeticionTarjeta(string? Token, Autenticacion3ds? Autenticacion);
public sealed record PeticionPagar(Guid TarjetaId, string? Periodo);
public sealed record PeticionPagoManual(Guid CuentaId, decimal Monto, string? Medio, string? Referencia, int Meses, string? Notas);
public sealed record PeticionAnularPago(string? Motivo);

/// <summary>
/// 073 — COBROS, PRIMERA PARTE.
///
/// LA CUENTA (/api/cobros): «Mis pagos». Sus datos del comprobante (RUC →
/// factura, DNI → boleta), sus tarjetas, pagar el mes o el año, y su historial.
/// Paga el dueño o un colaborador administrador; los demás solo miran.
///
/// LA PLATAFORMA (/api/admin/cobros):
///   · planes_ver          ver el panel y los pagos de todas las cuentas.
///   · cobros_configurar   el panel: modo, reintentos, gracia, empresa que
///                         emite y sus series, medios de pago.
///   · cobros_registrar    registrar pagos por transferencia y anularlos.
///
/// EL AVISO DE CULQI (/api/webhooks/culqi): sin sesión. No se cree lo que
/// dice: con el código del cargo se le pregunta a Culqi, con la llave
/// secreta, cómo está de verdad, y eso es lo que se aplica.
///
/// EL MONTO NUNCA VIENE DE LA PANTALLA: lo calcula la base (cobros_monto).
/// </summary>
public static class EndpointsCobros
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Medios = ["transferencia", "deposito", "yape", "efectivo", "otro"];

    public static void MapearCobros(this IEndpointRouteBuilder rutas)
    {
        // =====================================================================
        // LA CUENTA
        // =====================================================================
        var mio = rutas.MapGroup("/api/cobros").WithTags("Cobros");

        mio.MapGet("/mio", async (ContextoPlataforma db, OpcionesCulqi llaves, OpcionesConexionSunat sunat, CancellationToken ct) =>
        {
            var modo = await ModoAsync(db, llaves, sunat, ct);
            var json = JsonNode.Parse(await db.Database
                .SqlQuery<string>($"SELECT cobros_mio({modo.Modo})::text AS \"Value\"").FirstAsync(ct))!.AsObject();
            // Para abrir el formulario de Culqi: la llave PÚBLICA (es pública
            // por diseño: va en el navegador). La secreta nunca sale de aquí.
            json["culqi"] = modo.Modo is "prueba" or "real"
                ? new JsonObject { ["llavePublica"] = llaves.Publica }
                : null;
            return Results.Content(json.ToJsonString(), "application/json");
        })
        .RequiereSesion()
        .WithSummary("Mis pagos: datos del comprobante, tarjetas, lo que toca pagar y el historial");

        mio.MapPut("/datos", async (PeticionDatosPago p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var tipo = (p.TipoDoc ?? "").Trim().ToLowerInvariant();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT cobros_guardar_datos({tipo}, {p.Numero ?? ""}, {p.Nombre ?? ""}, {p.Direccion}::text,
                                            {p.Correo ?? ""}, {p.Telefono}::text)
                """, ct);
            return Results.Ok(new
            {
                mensaje = tipo == "ruc"
                    ? "Listo: tus pagos llevarán FACTURA a ese RUC."
                    : "Listo: tus pagos llevarán BOLETA a ese DNI."
            });
        })
        .RequiereSesion()
        .WithSummary("Guardar los datos del comprobante (RUC → factura, DNI → boleta)");

        mio.MapPost("/tarjetas", async (PeticionTarjeta p, ContextoPlataforma db, OpcionesCulqi llaves,
            OpcionesConexionSunat sunat, ClienteCulqi culqi, CancellationToken ct) =>
        {
            var modo = await ModoAsync(db, llaves, sunat, ct);
            if (!modo.ConTarjeta)
                return Results.BadRequest(new { error = "Los pagos con tarjeta están apagados por ahora." });
            var token = (p.Token ?? "").Trim();
            if (token.Length is < 6 or > 120)
                return Results.BadRequest(new { error = "No llegó la tarjeta. Vuelve a escribirla." });

            // Los datos del comprobante tienen que estar (y dicen quién es el cliente).
            var datos = JsonDocument.Parse(await db.Database
                .SqlQuery<string>($"SELECT cobros_para_pasarela({modo.Modo})::text AS \"Value\"").FirstAsync(ct)).RootElement;

            string marca, ultimos4, tarjetaRef;
            string? clienteRef = null;

            if (modo.Modo == "simulado")
            {
                if (!token.StartsWith("sim_tok_", StringComparison.Ordinal))
                    return Results.BadRequest(new { error = "En modo simulado solo sirven las tarjetas de prueba." });
                var r = PasarelaSimulada.GuardarTarjeta(token, p.Autenticacion, out marca, out ultimos4);
                if (r.Requiere3ds) return Results.Ok(new { requiere3ds = true, mensaje = r.Mensaje });
                if (!r.Ok) return Results.BadRequest(new { error = r.Mensaje, codigo = r.Codigo });
                tarjetaRef = r.Id!;
            }
            else
            {
                if (!token.StartsWith("tkn_", StringComparison.Ordinal))
                    return Results.BadRequest(new { error = "No llegó la tarjeta desde Culqi. Vuelve a escribirla." });
                if (string.IsNullOrWhiteSpace(datos.GetProperty("telefono").GetString()))
                    return Results.BadRequest(new { error = "Agrega tu celular en los datos del comprobante: Culqi lo pide para guardar la tarjeta." });

                clienteRef = datos.GetProperty("cliente").ValueKind == JsonValueKind.String ? datos.GetProperty("cliente").GetString() : null;
                if (clienteRef is null)
                {
                    var c = await culqi.CrearClienteAsync(datos, ct);
                    clienteRef = c.Ok ? c.Id : await culqi.BuscarClienteAsync(datos.GetProperty("correo").GetString()!, ct);
                    if (clienteRef is null)
                        return Results.BadRequest(new { error = c.Mensaje ?? "Culqi no aceptó tus datos. Revisa el correo y el celular." });
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT cobros_fijar_cliente({modo.Modo}, {clienteRef})", ct);
                }

                var r = await culqi.CrearTarjetaAsync(clienteRef, token, p.Autenticacion, ct);
                if (r.Requiere3ds) return Results.Ok(new { requiere3ds = true, mensaje = r.Mensaje });
                if (!r.Ok) return Results.BadRequest(new { error = r.Mensaje, codigo = r.Codigo });
                tarjetaRef = r.Id!;
                (marca, ultimos4) = ClienteCulqi.Tarjeta(r.Cuerpo);
            }

            var id = await db.Database.SqlQuery<Guid>($"""
                SELECT cobros_guardar_tarjeta({modo.Modo}, {clienteRef}::text, {tarjetaRef}, {marca}, {ultimos4}) AS "Value"
                """).FirstAsync(ct);
            return Results.Ok(new { id, mensaje = $"Listo: guardamos tu {marca} terminada en {ultimos4}." });
        })
        .RequiereSesion()
        .WithSummary("Guardar una tarjeta (llega como token de Culqi; el número nunca pasa por aquí)");

        mio.MapDelete("/tarjetas/{id:guid}", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT cobros_quitar_tarjeta({id})", ct);
            return Results.Ok(new { mensaje = "Listo: esa tarjeta ya no se usará." });
        })
        .RequiereSesion()
        .WithSummary("Quitar una tarjeta guardada");

        mio.MapPost("/tarjetas/{id:guid}/principal", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT cobros_tarjeta_principal({id})", ct);
            return Results.Ok(new { mensaje = "Listo: los cobros irán a esa tarjeta." });
        })
        .RequiereSesion()
        .WithSummary("Elegir la tarjeta a la que se cobra");

        mio.MapPost("/pagar", async (PeticionPagar p, ContextoPeticion ctx, ContextoPlataforma db, OpcionesCulqi llaves,
            OpcionesConexionSunat sunat, ClienteCulqi culqi, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Cobros");
            var modo = await ModoAsync(db, llaves, sunat, ct);
            if (!modo.ConTarjeta)
                return Results.BadRequest(new { error = "Los pagos con tarjeta están apagados por ahora." });
            var periodo = (p.Periodo ?? "mensual").Trim().ToLowerInvariant();

            JsonElement prep;
            try
            {
                prep = JsonDocument.Parse(await db.Database
                    .SqlQuery<string>($"SELECT cobros_preparar({modo.Modo}, {periodo}, {p.TarjetaId})::text AS \"Value\"")
                    .FirstAsync(ct)).RootElement;
            }
            catch (Exception e) when (EnCurso(e))
            {
                return Results.Conflict(new { error = "Ya hay un pago en curso. Espera unos segundos." });
            }

            var cobro   = prep.GetProperty("id").GetGuid();
            var monto   = prep.GetProperty("monto").GetDecimal();
            var tarjeta = prep.GetProperty("tarjeta").GetString()!;

            var r = modo.Modo == "simulado"
                ? PasarelaSimulada.Cobrar(tarjeta)
                : await culqi.CobrarAsync(tarjeta, prep.GetProperty("correo").GetString()!, monto,
                                          prep.GetProperty("concepto").GetString()!, cobro, prep.GetProperty("cuenta").GetGuid(), ct);

            // Si Culqi no respondió, el pago se queda «procesando»: si sí cobró,
            // su aviso lo confirma; si no, a los 10 minutos se puede reintentar.
            if (r.SinRespuesta)
                return Results.Json(new { estado = "procesando", error = r.Mensaje }, statusCode: StatusCodes.Status202Accepted);

            if (r.Requiere3ds)
                r = r with { Ok = false, Codigo = "3ds", Mensaje = "Tu banco pidió confirmar este pago. Vuelve a agregar la tarjeta (te pedirá confirmar) y paga de nuevo." };

            var res = JsonDocument.Parse(await db.Database.SqlQuery<string>($"""
                SELECT cobros_resultado({cobro}, {r.Ok}, {r.Id}::text, {r.Codigo}::text, {r.Mensaje}::text)::text AS "Value"
                """).FirstAsync(ct)).RootElement;

            log.LogInformation("Pago {Cobro} ({Modo}) de la cuenta del usuario {Usuario}: {Estado}.",
                cobro, modo.Modo, ctx.UsuarioId, r.Ok ? "pagado" : r.Codigo);

            if (!r.Ok)
                return Results.BadRequest(new { estado = "rechazado", error = r.Mensaje ?? "La tarjeta no pasó.", codigo = r.Codigo });

            var proximo = res.TryGetProperty("proximo", out var px) && px.ValueKind == JsonValueKind.String ? px.GetString() : null;
            return Results.Ok(new
            {
                estado = "pagado",
                monto,
                proximo,
                mensaje = modo.SinDineroReal
                    ? $"Pago de PRUEBA aprobado (S/ {monto:0.00}). No se movió dinero real."
                    : $"¡Gracias! Pagaste S/ {monto:0.00}. Tu comprobante llegará a tu correo."
            });
        })
        .RequiereSesion()
        .WithSummary("Pagar el mes o el año con una tarjeta guardada (el monto lo calcula la plataforma)");


        // =====================================================================
        // LA PLATAFORMA
        // =====================================================================
        var admin = rutas.MapGroup("/api/admin/cobros").WithTags("Administración · cobros");

        admin.MapGet("/config", async (ContextoPlataforma db, OpcionesCulqi llaves, OpcionesConexionSunat sunat,
            UrlApi url, CancellationToken ct) =>
        {
            var json = JsonNode.Parse(await db.Database
                .SqlQuery<string>($"SELECT cobros_config()::text AS \"Value\"").FirstAsync(ct))!.AsObject();
            var modo = EstadoModo.Calcular(json["modo"]!.GetValue<string>(), llaves, sunat.Fija);
            // Lo que se ve de las llaves: si están y de qué tipo. Nunca la secreta.
            json["conexion"] = new JsonObject
            {
                ["modoEfectivo"] = modo.Modo,
                ["problema"] = modo.Problema,
                ["aviso"] = modo.Aviso,
                ["produccion"] = sunat.Fija,
                ["llaves"] = llaves.EstadoLlaves,
                ["llavePublica"] = llaves.PublicaEnmascarada,
                ["llaveSecreta"] = llaves.Secreta is not null,
                ["webhookProtegido"] = llaves.WebhookProtegido,
                ["webhookUrl"] = url.Base + "/api/webhooks/culqi",
            };
            return Results.Content(json.ToJsonString(), "application/json");
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
        .WithSummary("El panel de pagos: conexión con Culqi (sin las llaves), reglas, comprobante y medios");

        admin.MapPut("/config", async (JsonElement p, ContextoPlataforma db, OpcionesCulqi llaves,
            OpcionesConexionSunat sunat, CancellationToken ct) =>
        {
            var texto = p.GetRawText();
            if (texto.Length > 8_000) return Results.BadRequest(new { error = "Demasiados datos." });
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT cobros_guardar_config({texto}::jsonb, {sunat.Fija})", ct);
            var modo = EstadoModo.Calcular(p.TryGetProperty("modo", out var m) ? m.GetString() ?? "" : "", llaves, sunat.Fija);
            return Results.Ok(new
            {
                mensaje = modo.Modo switch
                {
                    "simulado" => "Listo: pagos en modo SIMULADO. Se prueba todo sin dinero real.",
                    "prueba"   => "Listo: pagos con Culqi en modo PRUEBA (llaves de prueba). Sin dinero real.",
                    "real"     => "Listo: pagos con Culqi REAL. Los cobros son de verdad.",
                    _          => modo.Problema is { } x ? "Guardado, pero los pagos con tarjeta están parados: " + x
                                                         : "Listo: pagos con tarjeta apagados."
                }
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.CobrosConfigurar)
        .WithSummary("Guardar el panel de pagos");

        admin.MapPost("/probar", async (ContextoPlataforma db, OpcionesCulqi llaves, OpcionesConexionSunat sunat,
            ClienteCulqi culqi, CancellationToken ct) =>
        {
            var modo = await ModoAsync(db, llaves, sunat, ct);
            bool ok;
            string detalle;
            if (modo.Modo == "simulado")
            {
                (ok, detalle) = (true, "Modo simulado: no hay nada que conectar. Funciona.");
            }
            else if (llaves.EstadoLlaves is "prueba" or "real")
            {
                var r = await culqi.ProbarAsync(ct);
                ok = r.Ok;
                detalle = r.Ok
                    ? $"Culqi respondió y aceptó la llave secreta ({(llaves.EstadoLlaves == "real" ? "REAL" : "de prueba")}). No se cobró nada."
                    : r.Codigo == "llave_invalida" ? "Culqi NO aceptó la llave secreta. Revisa que la copiaste completa en el .env."
                    : r.Mensaje ?? "Culqi no respondió.";
            }
            else
            {
                (ok, detalle) = (false, modo.Problema ?? "Faltan las llaves de Culqi en el .env del servidor.");
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT cobros_guardar_prueba({ok}, {detalle})", ct);
            return Results.Ok(new { ok, mensaje = detalle });
        })
        .SoloSuperAdmin(Permisos.Plataforma.CobrosConfigurar)
        .WithSummary("Probar la conexión con Culqi (no cobra nada)");

        admin.MapGet("/pagos", async (string? estado, string? texto, DateOnly? desde, DateOnly? hasta,
            ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"""
                SELECT cobros_lista({estado}::text, {texto}::text, {desde}::date, {hasta}::date)::text AS "Value"
                """).FirstAsync(ct), "application/json"))
        .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
        .WithSummary("Los pagos de todas las cuentas");

        admin.MapGet("/cuentas", async (string? texto, ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>(
                $"SELECT cobros_buscar_cuentas({texto ?? ""})::text AS \"Value\"").FirstAsync(ct), "application/json"))
        .SoloSuperAdmin(Permisos.Plataforma.CobrosRegistrar)
        .WithSummary("Buscar la cuenta para registrar un pago");

        admin.MapPost("/manual", async (PeticionPagoManual p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var medio = (p.Medio ?? "").Trim().ToLowerInvariant();
            if (!Medios.Contains(medio)) return Results.BadRequest(new { error = "Elige cómo pagó." });
            var id = await db.Database.SqlQuery<Guid>($"""
                SELECT cobros_manual({p.CuentaId}, {p.Monto}, {medio}, {p.Referencia ?? ""}, {p.Meses}, {p.Notas}::text) AS "Value"
                """).FirstAsync(ct);
            return Results.Ok(new { id, mensaje = $"Listo: pago de S/ {p.Monto:0.00} registrado. La cuenta queda al día." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.CobrosRegistrar)
        .WithSummary("Registrar un pago por transferencia, depósito, Yape o efectivo");

        admin.MapPost("/pagos/{id:guid}/anular", async (Guid id, PeticionAnularPago p, ContextoPlataforma db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT cobros_anular({id}, {p.Motivo ?? ""})", ct);
            return Results.Ok(new { mensaje = "Listo: pago anulado. Queda en el historial con su motivo." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.CobrosRegistrar)
        .WithSummary("Anular un pago registrado a mano o simulado (nada se borra)");


        // =====================================================================
        // EL AVISO DE CULQI
        // =====================================================================
        rutas.MapPost("/api/webhooks/culqi", RecibirAvisoAsync)
             .WithTags("Cobros · webhook")
             .WithSummary("Recibe los avisos de Culqi (se verifican preguntándole a Culqi)");
    }

    // =========================================================================

    private static async Task<EstadoModo> ModoAsync(ContextoPlataforma db, OpcionesCulqi llaves, OpcionesConexionSunat sunat, CancellationToken ct)
    {
        // cobros_ajustes solo lo lee la plataforma (aislamiento): el modo se
        // pregunta con su función, que cualquiera con sesión puede usar.
        var guardado = await db.Database.SqlQuery<string>($"SELECT cobros_modo_guardado() AS \"Value\"").FirstAsync(ct);
        return EstadoModo.Calcular(guardado, llaves, sunat.Fija);
    }

    private static bool EnCurso(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is PostgresException pg && pg.SqlState == "23505" && pg.MessageText.Contains("ux_cobros_en_curso"))
                return true;
        return false;
    }

    /// <summary>
    /// EL AVISO DE CULQI.
    ///   1. Si pusiste usuario y clave en el .env, se exigen (401 si no cuadran).
    ///   2. Se guarda el aviso, valga o no (recortado si no vale).
    ///   3. Del aviso solo se toma el tipo y el código del cargo o devolución.
    ///   4. Se le pregunta a Culqi con la llave secreta cómo está de verdad, y
    ///      eso se aplica. Un aviso falso no puede marcar nada como pagado.
    /// Responde 200 aunque el cargo no sea nuestro (reintentar no lo cambia) y
    /// 503 si no se pudo preguntar a Culqi (que lo reintente).
    /// </summary>
    private static async Task<IResult> RecibirAvisoAsync(HttpRequest peticion, ContextoPeticion ctx, ContextoPlataforma db,
        OpcionesCulqi llaves, ClienteCulqi culqi, ILoggerFactory logs, CancellationToken ct)
    {
        var log = logs.CreateLogger("Cobros.Webhook");
        var ip = ctx.Ip?.ToString();

        string cuerpo;
        using (var lector = new StreamReader(peticion.Body, Encoding.UTF8))
        {
            var buffer = new char[64 * 1024 + 1];
            var n = await lector.ReadBlockAsync(buffer, ct);
            if (n > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            cuerpo = new string(buffer, 0, n);
        }

        if (llaves.WebhookProtegido && !BasicCuadra(peticion.Headers.Authorization.ToString(), llaves))
        {
            await db.Database.SqlQuery<long>($"SELECT cobros_evento(NULL, NULL, false, {"usuario o clave del aviso no cuadran · " + cuerpo}, {ip}) AS \"Value\"").FirstAsync(ct);
            log.LogWarning("Aviso de Culqi rechazado desde {Ip}: usuario o clave no cuadran.", ip);
            return Results.Json(new { error = "Aviso no válido." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        string? tipo = null, refId = null;
        JsonNode? data = null;
        try
        {
            var evento = JsonNode.Parse(cuerpo);
            tipo = ClienteCulqi.Texto(evento, "type");
            // Culqi manda «data» como texto JSON (a veces como objeto).
            data = evento?["data"] is JsonValue v && v.TryGetValue<string>(out var s) ? JsonNode.Parse(s) : evento?["data"];
            refId = ClienteCulqi.Texto(data, "id");
        }
        catch (JsonException) { }

        var evId = await db.Database.SqlQuery<long>(
            $"SELECT cobros_evento({tipo}::text, {refId}::text, {tipo is not null && refId is not null}, {cuerpo}, {ip}) AS \"Value\"").FirstAsync(ct);

        if (tipo is null || refId is null)
            return Results.Ok(new { recibido = true });

        if (llaves.EstadoLlaves is not ("prueba" or "real"))
        {
            await Resultado(db, evId, false, "sin llaves para verificarlo", ct);
            return Results.Ok(new { recibido = true });
        }
        var modo = llaves.EstadoLlaves;

        string resultado;
        if (tipo.StartsWith("charge.", StringComparison.Ordinal))
        {
            var r = await culqi.LeerCargoAsync(refId, ct);
            if (r.SinRespuesta) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!r.Ok) { await Resultado(db, evId, false, "Culqi no reconoce ese cargo", ct); return Results.Ok(new { recibido = true }); }
            if (!ClienteCulqi.CargoExitoso(r.Cuerpo)) { await Resultado(db, evId, false, "el cargo no está cobrado", ct); return Results.Ok(new { recibido = true }); }

            Guid? cobro = Guid.TryParse(ClienteCulqi.Texto(r.Cuerpo?["metadata"], "cobro_id"), out var g) ? g : null;
            var monto = (r.Cuerpo?["amount"]?.GetValue<long>() ?? 0) / 100m;
            resultado = await db.Database.SqlQuery<string>($"""
                SELECT cobros_confirmar({cobro}::uuid, {modo}, {refId}, 'pagado', {monto}) AS "Value"
                """).FirstAsync(ct);
        }
        else if (tipo.StartsWith("refund.", StringComparison.Ordinal))
        {
            var r = await culqi.LeerDevolucionAsync(refId, ct);
            if (r.SinRespuesta) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var cargo = ClienteCulqi.Texto(r.Cuerpo, "charge_id");
            if (!r.Ok || cargo is null) { await Resultado(db, evId, false, "Culqi no reconoce esa devolución", ct); return Results.Ok(new { recibido = true }); }
            resultado = await db.Database.SqlQuery<string>($"""
                SELECT cobros_confirmar(NULL::uuid, {modo}, {cargo}, 'devuelto', NULL::numeric) AS "Value"
                """).FirstAsync(ct);
        }
        else
        {
            resultado = "tipo que no usamos";
        }

        await Resultado(db, evId, resultado is "pagado" or "devuelto", resultado, ct);
        if (resultado == "no_es_nuestro")
            log.LogWarning("Culqi avisó de un cargo cobrado que no está en la plataforma ({Ref}). Revísalo en CulqiPanel.", refId);
        return Results.Ok(new { recibido = true });
    }

    private static Task Resultado(ContextoPlataforma db, long id, bool aplicado, string texto, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT cobros_evento_resultado({id}, {aplicado}, {texto})", ct);

    private static bool BasicCuadra(string cabecera, OpcionesCulqi llaves)
    {
        if (!cabecera.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        string par;
        try { par = Encoding.UTF8.GetString(Convert.FromBase64String(cabecera[6..].Trim())); }
        catch (FormatException) { return false; }
        var esperado = llaves.WebhookUsuario + ":" + llaves.WebhookClave;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(par)), SHA256.HashData(Encoding.UTF8.GetBytes(esperado)));
    }
}

/// <summary>Los errores de cobros (COBRO_…) en palabras.</summary>
public static class ReglasCobro
{
    public static string? Mensaje(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is not PostgresException pg || !pg.MessageText.StartsWith("COBRO_", StringComparison.Ordinal)) continue;
            var i = pg.MessageText.IndexOf(':');
            return i > 0 ? pg.MessageText[(i + 1)..].Trim() : "No se pudo hacer el pago.";
        }
        return null;
    }

    public static int Estado(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is PostgresException pg)
                return pg.MessageText.StartsWith("COBRO_NO_PUEDE", StringComparison.Ordinal) ? 403
                     : pg.MessageText.StartsWith("COBRO_EN_CURSO", StringComparison.Ordinal) ? 409 : 400;
        return 400;
    }
}