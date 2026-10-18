using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Arranque;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// CONSULTAS DE RUC, DNI Y TIPO DE CAMBIO (17 oct, migración 029).
///
/// Las hace un servicio externo, Decolecta (antes apis.net.pe), que sirve los
/// datos públicos de SUNAT y RENIEC. SUNAT no tiene una consulta de RUC para
/// sistemas: su padrón es un archivo enorme y su web pide captcha.
///
/// SIEMPRE DESDE AQUÍ (el back), nunca desde el navegador: el token no puede
/// llegar a la pantalla de nadie, y el proveedor tampoco lo permite (CORS).
///
/// NUNCA BLOQUEA: si el servicio no responde, no hay token o se acabaron las
/// consultas del mes, la pantalla deja escribir los datos a mano.
/// </summary>
public sealed class OpcionesDecolecta
{
    public string Token { get; init; } = "";
    public string UrlBase { get; init; } = "https://api.decolecta.com";
    public int LimiteMes { get; init; } = 1000;
    public bool DniActivo { get; init; } = true;
    public string ContactoWhatsapp { get; init; } = "";
    public string ContactoCorreo { get; init; } = "";

    public bool Configurado => Token.Length > 0;

    public static OpcionesDecolecta Leer() => new()
    {
        Token            = Entorno.Opcional("DECOLECTA_TOKEN", "").Trim(),
        UrlBase          = Entorno.Opcional("DECOLECTA_URL", "https://api.decolecta.com").TrimEnd('/'),
        LimiteMes        = Entorno.Numero("DECOLECTA_LIMITE_MES", 1000),
        // Encendido salvo que se ponga DECOLECTA_DNI=false.
        DniActivo        = !string.Equals(Entorno.Opcional("DECOLECTA_DNI", "true").Trim(), "false",
                                          StringComparison.OrdinalIgnoreCase),
        ContactoWhatsapp = Entorno.Opcional("DECOLECTA_CONTACTO_WHATSAPP", "+51 984001136").Trim(),
        ContactoCorreo   = Entorno.Opcional("DECOLECTA_CONTACTO_CORREO", "").Trim(),
    };
}

/// <summary>Lo que salió de una consulta, ya traducido para la pantalla.</summary>
public sealed record ResultadoConsulta(int Estado, object Cuerpo);

public sealed class ClienteDecolecta(
    IHttpClientFactory fabrica, OpcionesDecolecta opciones, ContextoPlataforma db,
    IServicioCorreo correo, ILogger<ClienteDecolecta> log)
{
    public const string NombreCliente = "decolecta";
    private const string Proveedor = "decolecta";

    private static readonly object SinServicio = new
    {
        error = "La consulta automática no está disponible. Escribe los datos a mano y revísalos con tu cliente.",
        manual = true
    };

    public async Task<ResultadoConsulta> RucAsync(string numero, CancellationToken ct)
    {
        var r = await PedirAsync($"/v1/sunat/ruc?numero={numero}", ct);
        if (r.Error is not null) return r.Error;

        var j = r.Json!.Value;
        return new ResultadoConsulta(200, new
        {
            numero,
            razonSocial  = Texto(j, "razon_social"),
            estado       = Texto(j, "estado"),
            condicion    = Texto(j, "condicion"),
            direccion    = Texto(j, "direccion"),
            ubigeo       = Texto(j, "ubigeo"),
            distrito     = Texto(j, "distrito"),
            provincia    = Texto(j, "provincia"),
            departamento = Texto(j, "departamento"),
        });
    }

    public async Task<ResultadoConsulta> DniAsync(string numero, CancellationToken ct)
    {
        if (!opciones.DniActivo) return new ResultadoConsulta(503, SinServicio);

        var r = await PedirAsync($"/v1/reniec/dni?numero={numero}", ct);
        if (r.Error is not null) return r.Error;

        var j = r.Json!.Value;
        return new ResultadoConsulta(200, new
        {
            numero,
            nombreCompleto = Texto(j, "full_name"),
            nombres        = Texto(j, "first_name"),
            apellidoPaterno = Texto(j, "first_last_name"),
            apellidoMaterno = Texto(j, "second_last_name"),
        });
    }

    /// <summary>El tipo de cambio de SUNAT para una fecha (el de venta va en el comprobante).</summary>
    public async Task<ResultadoConsulta> TipoCambioAsync(DateOnly fecha, CancellationToken ct)
    {
        var r = await PedirAsync($"/v1/tipo-cambio/sunat?date={fecha:yyyy-MM-dd}", ct);
        if (r.Error is not null) return r.Error;

        var j = r.Json!.Value;
        decimal? Dec(string n) => decimal.TryParse(Texto(j, n),
            System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture,
            out var d) ? d : null;

        var venta = Dec("sell_price");
        if (venta is null or <= 0) return new ResultadoConsulta(404, new { error = "SUNAT no tiene tipo de cambio para esa fecha.", manual = true });

        return new ResultadoConsulta(200, new
        {
            fecha = Texto(j, "date") ?? fecha.ToString("yyyy-MM-dd"),
            compra = Dec("buy_price"),
            venta,
        });
    }

    // =========================================================================

    private sealed record Respuesta(JsonElement? Json, ResultadoConsulta? Error);

    private async Task<Respuesta> PedirAsync(string ruta, CancellationToken ct)
    {
        if (!opciones.Configurado)
            return new Respuesta(null, new ResultadoConsulta(503, SinServicio));

        HttpResponseMessage resp;
        string cuerpo;
        try
        {
            var http = fabrica.CreateClient(NombreCliente);
            using var pet = new HttpRequestMessage(HttpMethod.Get, opciones.UrlBase + ruta);
            pet.Headers.Authorization = new("Bearer", opciones.Token);
            pet.Headers.Accept.ParseAdd("application/json");
            resp = await http.SendAsync(pet, ct);
            cuerpo = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Decolecta no respondió ({Ruta}).", Ruta(ruta));
            return new Respuesta(null, new ResultadoConsulta(503, SinServicio));
        }

        // SE ACABARON LAS CONSULTAS DEL MES (o el plan no lo permite).
        if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden)
        {
            log.LogWarning("Decolecta: sin consultas disponibles ({Codigo}).", (int)resp.StatusCode);
            await AnotarAsync(agotada: true, ct);
            return new Respuesta(null, new ResultadoConsulta(503, new
            {
                error = "Se acabaron las consultas automáticas del mes. Escribe los datos a mano; ya se avisó al administrador.",
                manual = true
            }));
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            log.LogError("Decolecta rechazó el token (401). Revisa DECOLECTA_TOKEN en el .env de la plataforma.");
            return new Respuesta(null, new ResultadoConsulta(503, SinServicio));
        }

        // Cuenta como consulta aunque no encuentre nada: el proveedor la cobra.
        await AnotarAsync(agotada: false, ct);

        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest)
            return new Respuesta(null, new ResultadoConsulta(404, new { error = "No se encontró ese número.", manual = true }));

        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("Decolecta respondió {Codigo} ({Ruta}).", (int)resp.StatusCode, Ruta(ruta));
            return new Respuesta(null, new ResultadoConsulta(503, SinServicio));
        }

        try
        {
            using var doc = JsonDocument.Parse(cuerpo);
            return new Respuesta(doc.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            log.LogWarning("Decolecta devolvió algo que no es JSON ({Ruta}).", Ruta(ruta));
            return new Respuesta(null, new ResultadoConsulta(503, SinServicio));
        }
    }

    /// <summary>
    /// Cuenta la consulta. Al 80 % y al acabarse, UN correo a los super
    /// administradores (la base decide si toca, una vez por mes). Si algo de
    /// esto falla, la consulta sigue: contar no puede romper la pantalla.
    /// </summary>
    private async Task AnotarAsync(bool agotada, CancellationToken ct)
    {
        try
        {
            var fila = await db.Database.SqlQuery<string>(
                $"SELECT anotar_consulta_externa({Proveedor}, {opciones.LimiteMes}, {agotada}) AS \"Value\"")
                .FirstAsync(ct);

            using var doc = JsonDocument.Parse(fila);
            var r = doc.RootElement;
            var aviso = r.GetProperty("aviso").GetString();
            if (aviso is not ("aviso_80" or "aviso_100")) return;

            var usadas = r.GetProperty("usadas").GetInt32();
            var limite = r.GetProperty("limite").GetInt32();
            var correos = r.GetProperty("correos").EnumerateArray().Select(c => c.GetString()!).ToList();

            var (asunto, html, texto) = Correo(aviso == "aviso_100", usadas, limite);
            foreach (var destino in correos)
            {
                try { await correo.EnviarAsync(destino, asunto, html, texto, ct); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning(e, "No salió el aviso de consultas externas a un super administrador.");
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "No se pudo anotar la consulta externa. ¿Falta la migración 029?");
        }
    }

    private (string Asunto, string Html, string Texto) Correo(bool agotadas, int usadas, int limite)
    {
        var asunto = agotadas
            ? "Se acabaron las consultas de RUC y DNI del mes"
            : $"Van {usadas} de {limite} consultas de RUC y DNI este mes";

        var que = agotadas
            ? "Se acabaron las consultas automáticas de RUC, DNI y tipo de cambio de este mes. Mientras tanto, " +
              "quien cree un cliente tiene que escribir sus datos a mano."
            : $"Ya se usó el 80 % de las consultas automáticas de RUC, DNI y tipo de cambio de este mes " +
              $"({usadas} de {limite}). Cuando se acaben, los datos se escribirán a mano.";

        var contacto = new List<string>();
        if (opciones.ContactoWhatsapp.Length > 0) contacto.Add($"WhatsApp: {opciones.ContactoWhatsapp}");
        if (opciones.ContactoCorreo.Length > 0) contacto.Add($"Correo: {opciones.ContactoCorreo}");
        contacto.Add("Web: https://decolecta.com");

        static string e(string? s) => WebUtility.HtmlEncode(s ?? "");
        var html =
            "<div style=\"font-family:Segoe UI,system-ui,sans-serif;font-size:15px;line-height:1.55;color:#1b1f24;max-width:620px\">" +
            $"<p>{e(que)}</p>" +
            "<p><b>Para comprar más consultas</b> (proveedor: Decolecta):</p><ul>" +
            string.Concat(contacto.Select(c => $"<li>{e(c)}</li>")) + "</ul>" +
            "<p>Si cambias de plan, pon el nuevo límite en <code>DECOLECTA_LIMITE_MES</code> del .env de la plataforma y reiníciala.</p>" +
            "<p style=\"color:#5c6570;font-size:13px\">Te llega por ser super administrador. Sale una vez por mes en cada umbral.</p></div>";

        var texto = que + "\n\nPara comprar más consultas (Decolecta):\n" + string.Join("\n", contacto) +
                    "\n\nSi cambias de plan, pon el nuevo límite en DECOLECTA_LIMITE_MES del .env de la plataforma.";

        return (asunto, html, texto);
    }

    private static string? Texto(JsonElement j, string nombre) =>
        j.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() is { } s && s.Trim() is { Length: > 0 } t && t != "-" ? t : null)
            : null;

    // El número consultado no va al log: es un dato personal (el DNI).
    private static string Ruta(string ruta) => ruta.Split('?')[0];
}

public static class EndpointsConsultas
{
    public static void MapearConsultas(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/consultas")
                         .WithTags("Facturación · consultas RUC, DNI y tipo de cambio");

        grupo.MapGet("/ruc/{numero}", async (string numero, ClienteDecolecta d, CancellationToken ct) =>
        {
            numero = numero.Trim();
            if (numero.Length != 11 || !numero.All(char.IsDigit))
                return Results.BadRequest(new { error = "Un RUC son 11 dígitos." });
            var r = await d.RucAsync(numero, ct);
            return Results.Json(r.Cuerpo, statusCode: r.Estado);
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Razón social, dirección, estado y condición de un RUC (SUNAT)");

        // 18 OCT: LA MISMA CONSULTA PARA DAR DE ALTA UNA EMPRESA. Quien agrega su
        // primera empresa todavía no tiene Facturación (ni su permiso), por eso
        // basta con tener sesión. Gasta del mismo contador del mes.
        rutas.MapGet("/api/consultas/ruc/{numero}", async (string numero, ClienteDecolecta d, CancellationToken ct) =>
        {
            numero = numero.Trim();
            if (numero.Length != 11 || !numero.All(char.IsDigit))
                return Results.BadRequest(new { error = "Un RUC son 11 dígitos." });
            var r = await d.RucAsync(numero, ct);
            return Results.Json(r.Cuerpo, statusCode: r.Estado);
        })
        .RequiereSesion()
        .WithTags("Facturación · consultas RUC, DNI y tipo de cambio")
        .WithSummary("Ficha RUC para el alta de una empresa (asistente y super administrador)");

        grupo.MapGet("/dni/{numero}", async (string numero, ClienteDecolecta d, CancellationToken ct) =>
        {
            numero = numero.Trim();
            if (numero.Length != 8 || !numero.All(char.IsDigit))
                return Results.BadRequest(new { error = "Un DNI son 8 dígitos." });
            var r = await d.DniAsync(numero, ct);
            return Results.Json(r.Cuerpo, statusCode: r.Estado);
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Nombre de un DNI (RENIEC)");

        grupo.MapGet("/tipo-cambio", async (string? fecha, ClienteDecolecta d, CancellationToken ct) =>
        {
            if (!DateOnly.TryParseExact(fecha ?? "", "yyyy-MM-dd", out var dia))
                return Results.BadRequest(new { error = "La fecha va como AAAA-MM-DD." });
            var r = await d.TipoCambioAsync(dia, ct);
            return Results.Json(r.Cuerpo, statusCode: r.Estado);
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Tipo de cambio SUNAT (compra y venta) de una fecha");

        // «PÚBLICO EN GENERAL»: la boleta simple. Se crea la primera vez que
        // hace falta, una por empresa, y después siempre es la misma.
        rutas.MapPost("/api/facturacion/clientes/publico-general", async (
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            const string Numero = "00000000";

            var c = await db.Clientes.FirstOrDefaultAsync(
                x => x.TipoDocumento == "0" && x.NumeroDocumento == Numero, ct);

            if (c is null)
            {
                c = new Cliente
                {
                    EmpresaId       = ctx.EmpresaId!.Value,
                    TipoDocumento   = "0",
                    NumeroDocumento = Numero,
                    RazonSocial     = "PÚBLICO EN GENERAL",
                };
                db.Clientes.Add(c);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new
            {
                c.Id, c.TipoDocumento, c.NumeroDocumento, c.RazonSocial, c.NombreComercial,
                c.Direccion, c.Ubigeo, c.Correo, c.Telefono, c.AfectacionHabitual, c.Activo
            });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithTags("Facturación · clientes")
        .WithSummary("El cliente «Público en general» de la empresa (boleta sin documento)");

        // EL SUPER ADMINISTRADOR: cuántas consultas van este mes.
        rutas.MapGet("/api/admin/consultas-externas", async (
            ContextoPlataforma db, OpcionesDecolecta op, CancellationToken ct) =>
        {
            string? fila = null;
            try
            {
                fila = await db.Database.SqlQuery<string>(
                    $"SELECT estado_consultas_externas({"decolecta"}) AS \"Value\"").FirstOrDefaultAsync(ct);
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42883") { /* falta la 029 */ }

            using var doc = fila is null ? null : JsonDocument.Parse(fila);
            var r = doc?.RootElement;

            return Results.Ok(new
            {
                configurado = op.Configurado,
                dniActivo   = op.DniActivo,
                usadas      = r?.GetProperty("usadas").GetInt32() ?? 0,
                limite      = op.LimiteMes,
                agotadaEn   = r is { } x && x.GetProperty("agotadaEn").ValueKind == JsonValueKind.String
                                ? x.GetProperty("agotadaEn").GetString() : null,
                whatsapp    = op.ContactoWhatsapp,
                correo      = op.ContactoCorreo,
            });
        })
        .RequierePermiso(Permisos.Plataforma.EmpresasGestionar)
        .WithTags("Administración")
        .WithSummary("Consultas de RUC/DNI/tipo de cambio usadas este mes");
    }
}