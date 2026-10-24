using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Consultas;

/// <summary>
/// 042 — DE DÓNDE SALEN EL RUC, EL DNI Y EL TIPO DE CAMBIO.
///
/// El modo lo elige el super administrador (o quien tenga
/// «plataforma.consultas_gestionar»), y vive en la base:
///
///   · sunat (sin costo): RUC del padrón de SUNAT; tipo de cambio del de la
///     SBS (el que publica SUNAT); DNI a mano.
///   · decolecta (membresía): RUC primero del padrón y, si no está, de
///     Decolecta; DNI y tipo de cambio de Decolecta, como antes.
///
/// NUNCA BLOQUEA: si algo no responde, la pantalla deja escribirlo a mano.
/// </summary>
public sealed class ConsultasDocumento(ContextoPlataforma db, ClienteDecolecta decolecta, ILogger<ConsultasDocumento> log)
{
    public async Task<string> ModoAsync(CancellationToken ct)
    {
        try
        {
            return await db.Database.SqlQuery<string>($"SELECT consultas_modo() AS \"Value\"").FirstAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Sin la 042, como antes.
            log.LogWarning(e, "No se pudo leer el modo de consultas. ¿Falta la migración 042?");
            return "decolecta";
        }
    }

    public async Task<ResultadoConsulta> RucAsync(string numero, CancellationToken ct)
    {
        string? fila = null;
        try
        {
            fila = await db.Database.SqlQuery<string>($"SELECT padron_consultar({numero}) AS \"Value\"")
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "No se pudo consultar el padrón de SUNAT.");
        }

        var vacio = false;
        if (fila is not null)
        {
            using var doc = JsonDocument.Parse(fila);
            if (doc.RootElement.TryGetProperty("padronVacio", out _)) vacio = true;
            else return new ResultadoConsulta(200, doc.RootElement.Clone());
        }

        if (await ModoAsync(ct) == "decolecta")
            return await decolecta.RucAsync(numero, ct);

        if (vacio || !await HayPadronAsync(ct))
            return new ResultadoConsulta(503, new
            {
                error = "El padrón de SUNAT todavía no está cargado. Escribe los datos a mano y revísalos con tu cliente.",
                manual = true
            });

        return new ResultadoConsulta(404, new
        {
            error = "Ese RUC no está en el padrón de SUNAT. Revisa el número; si es un RUC recién creado, escribe los datos a mano.",
            manual = true
        });
    }

    public async Task<ResultadoConsulta> DniAsync(string numero, CancellationToken ct)
    {
        if (await ModoAsync(ct) == "decolecta")
            return await decolecta.DniAsync(numero, ct);

        return new ResultadoConsulta(503, new
        {
            error = "Escribe el nombre a mano, tal como figura en su DNI (la consulta automática de DNI está apagada).",
            manual = true
        });
    }

    private async Task<bool> HayPadronAsync(CancellationToken ct)
    {
        try
        {
            var e = await db.Database.SqlQuery<string>($"SELECT padron_estado() AS \"Value\"").FirstAsync(ct);
            using var doc = JsonDocument.Parse(e);
            return doc.RootElement.GetProperty("enUso").ValueKind == JsonValueKind.Object;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }
}


/// <summary>
/// 042 — EL TIPO DE CAMBIO PARA UNA FECHA, según el modo.
///
/// En modo SUNAT sale de la estadística pública del BCRP: el «TC Sistema
/// bancario SBS» de compra y venta (series PD04639PD y PD04640PD). SUNAT
/// publica para cada día el CIERRE DEL DÍA HÁBIL ANTERIOR de la SBS, así que
/// para la fecha D se toma el último valor de la SBS antes de D.
///
/// Devuelve lo mismo que Decolecta ({fecha, compra, venta}), para que
/// TipoCambioDiario lo guarde igual. 404 = todavía no hay (hoy, temprano).
/// </summary>
public sealed class FuenteTipoCambio(
    ConsultasDocumento consultas, ClienteDecolecta decolecta, IHttpClientFactory fabrica, ILogger<FuenteTipoCambio> log)
{
    public const string NombreCliente = "bcrp";
    private const string Url = "https://estadisticas.bcrp.gob.pe/estadisticas/series/api/PD04639PD-PD04640PD/json/";

    private static readonly Dictionary<string, int> Meses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ene"] = 1, ["Feb"] = 2, ["Mar"] = 3, ["Abr"] = 4, ["May"] = 5, ["Jun"] = 6,
        ["Jul"] = 7, ["Ago"] = 8, ["Set"] = 9, ["Sep"] = 9, ["Oct"] = 10, ["Nov"] = 11, ["Dic"] = 12
    };

    public async Task<ResultadoConsulta> TipoCambioAsync(DateOnly fecha, CancellationToken ct)
    {
        if (await consultas.ModoAsync(ct) == "decolecta")
            return await decolecta.TipoCambioAsync(fecha, ct);

        // 24 OCT: PRIMERO EL DE SUNAT. Publica el del día en un archivo de una
        // línea («24/10/2026|3.443|3.451|»: fecha, compra, venta). Solo trae el
        // de HOY; para otra fecha se sigue con el BCRP y Decolecta. Como cada
        // día queda guardado (TipoCambioDiario), con el uso se van juntando.
        if (await SunatDelDiaAsync(fecha, ct) is { } deSunat)
            return deSunat;

        var desde = fecha.AddDays(-12);
        var hasta = fecha.AddDays(-1);

        string cuerpo;
        try
        {
            var http = fabrica.CreateClient(NombreCliente);
            // 24 OCT: CON CABECERAS DE NAVEGADOR. Sin ellas el BCRP a veces
            // contesta 200 con una página HTML (su filtro de robots) en vez del
            // JSON, y la consulta fallaba en silencio.
            using var pedido = new HttpRequestMessage(HttpMethod.Get, $"{Url}{desde:yyyy-MM-dd}/{hasta:yyyy-MM-dd}/esp");
            pedido.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            pedido.Headers.TryAddWithoutValidation("Accept-Language", "es-PE,es;q=0.9");
            pedido.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            using var resp = await http.SendAsync(pedido, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("BCRP respondió {Codigo} al pedir el tipo de cambio.", (int)resp.StatusCode);
                return await RespaldoAsync(fecha, ct);
            }
            cuerpo = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "No se pudo consultar el tipo de cambio al BCRP.");
            return await RespaldoAsync(fecha, ct);
        }

        if (!cuerpo.TrimStart().StartsWith('{'))
        {
            var inicio = cuerpo.Length > 200 ? cuerpo[..200] : cuerpo;
            log.LogWarning("El BCRP no devolvió JSON (¿filtro de robots?). Empieza así: {Inicio}",
                           inicio.Replace('\n', ' ').Replace('\r', ' '));
            return await RespaldoAsync(fecha, ct);
        }

        // {"periods":[{"name":"28.Set.26","values":["3.432","3.440"]}, …]}
        (DateOnly Dia, decimal? Compra, decimal Venta)? ultimo = null;
        try
        {
            using var doc = JsonDocument.Parse(cuerpo);
            foreach (var p in doc.RootElement.GetProperty("periods").EnumerateArray())
            {
                if (!Dia(p.GetProperty("name").GetString(), out var dia)) continue;
                var v = p.GetProperty("values").EnumerateArray().Select(x => x.GetString()).ToArray();
                if (v.Length < 2 || !Numero(v[1], out var venta)) continue;
                decimal? compra = Numero(v[0], out var c) ? c : null;
                if (dia < fecha && (ultimo is null || dia > ultimo.Value.Dia)) ultimo = (dia, compra, venta);
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            log.LogWarning(e, "El BCRP devolvió algo que no se entiende.");
            return await RespaldoAsync(fecha, ct);
        }

        if (ultimo is null)
            return new ResultadoConsulta(404, new { error = "Todavía no hay tipo de cambio publicado para esa fecha.", manual = true });

        // HOY, TEMPRANO: si ayer fue día hábil y la SBS aún no aparece, el de
        // hoy todavía no existe. Se dice «todavía no» (y se vuelve a preguntar
        // en una hora) en vez de guardar el de anteayer como si fuera el de hoy.
        var ayer = fecha.AddDays(-1);
        if (fecha == HoyEnPeru() && ultimo.Value.Dia < ayer &&
            ayer.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            return new ResultadoConsulta(404, new { error = "El tipo de cambio de hoy todavía no está publicado.", manual = true });

        return new ResultadoConsulta(200, new
        {
            fecha = fecha.ToString("yyyy-MM-dd"),
            compra = ultimo.Value.Compra,
            venta = ultimo.Value.Venta
        });
    }

    private const string UrlSunat = "https://www.sunat.gob.pe/a/txt/tipoCambio.txt";

    /// <summary>El de SUNAT si el archivo es de esa fecha; null si no (o no responde).</summary>
    private async Task<ResultadoConsulta?> SunatDelDiaAsync(DateOnly fecha, CancellationToken ct)
    {
        try
        {
            var http = fabrica.CreateClient(NombreCliente);
            using var pedido = new HttpRequestMessage(HttpMethod.Get, UrlSunat);
            pedido.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            using var resp = await http.SendAsync(pedido, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("SUNAT respondió {Codigo} al pedir tipoCambio.txt.", (int)resp.StatusCode);
                return null;
            }

            var texto = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            var p = texto.Split('|');
            if (p.Length < 3 ||
                !DateOnly.TryParseExact(p[0].Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia) ||
                !Numero(p[2].Trim(), out var venta))
            {
                log.LogWarning("tipoCambio.txt de SUNAT no tiene la forma esperada. Empieza así: {Inicio}",
                               texto.Length > 80 ? texto[..80] : texto);
                return null;
            }

            if (dia != fecha) return null;   // es de otro día: que siga con el BCRP / Decolecta

            decimal? compra = Numero(p[1].Trim(), out var c) ? c : null;
            return new ResultadoConsulta(200, new { fecha = fecha.ToString("yyyy-MM-dd"), compra, venta });
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "No se pudo leer tipoCambio.txt de SUNAT.");
            return null;
        }
    }

    /// <summary>
    /// 24 OCT: SI EL BCRP NO RESPONDE, DECOLECTA (si hay token). Es el mismo
    /// número (el de SUNAT); solo cambia de dónde se lee. Sin token, «escríbelo
    /// a mano», como antes.
    /// </summary>
    private async Task<ResultadoConsulta> RespaldoAsync(DateOnly fecha, CancellationToken ct)
    {
        try
        {
            var r = await decolecta.TipoCambioAsync(fecha, ct);
            if (r.Estado == 200)
            {
                log.LogInformation("Tipo de cambio de {Fecha} tomado de Decolecta (el BCRP no respondió).", fecha);
                return r;
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Tampoco se pudo consultar el tipo de cambio a Decolecta.");
        }
        return Caido();
    }

    private static ResultadoConsulta Caido() => new(503, new
    {
        error = "La consulta del tipo de cambio no está disponible. Escríbelo a mano (el de venta SUNAT de la fecha).",
        manual = true
    });

    private static bool Dia(string? nombre, out DateOnly dia)
    {
        dia = default;
        var p = (nombre ?? "").Split('.');
        return p.Length == 3
            && int.TryParse(p[0], out var d)
            && Meses.TryGetValue(p[1], out var m)
            && int.TryParse(p[2], out var a)
            && DateOnly.TryParseExact($"{2000 + a:D4}-{m:D2}-{d:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out dia);
    }

    private static bool Numero(string? s, out decimal n) =>
        decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out n) && n > 0;

    private static DateOnly HoyEnPeru() => DateOnly.FromDateTime(CargadorPadron.AhoraEnLima());
}


/// <summary>
/// 042 — LA PANTALLA «CONSULTAS SUNAT» (super administrador o
/// «plataforma.consultas_gestionar») y el estado para el panel del servicio.
/// </summary>
public static class EndpointsConsultasSunat
{
    public static void MapearConsultasSunat(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/admin/consultas-sunat").WithTags("Administración · consultas SUNAT");

        grupo.MapGet("/", async (ContextoPlataforma db, OpcionesDecolecta decolecta, OpcionesPadron padron, CancellationToken ct) =>
        {
            var estado = await db.Database.SqlQuery<string>($"SELECT padron_estado() AS \"Value\"").FirstAsync(ct);

            string? usadas = null;
            try
            {
                usadas = await db.Database.SqlQuery<string>(
                    $"SELECT estado_consultas_externas({"decolecta"}) AS \"Value\"").FirstOrDefaultAsync(ct);
            }
            catch (Npgsql.PostgresException) { /* sin la 029 */ }

            using var e = JsonDocument.Parse(estado);
            using var u = usadas is null ? null : JsonDocument.Parse(usadas);

            return Results.Ok(new
            {
                padron = e.RootElement.Clone(),
                automatico = new { activo = padron.Automatico, hora = padron.Hora, url = padron.Url, minimo = padron.Minimo },
                decolecta = new
                {
                    configurado = decolecta.Configurado,
                    usadas = u?.RootElement.GetProperty("usadas").GetInt32() ?? 0,
                    limite = decolecta.LimiteMes
                }
            });
        })
        .RequierePermiso(Permisos.Plataforma.ConsultasGestionar)
        .WithSummary("Modo de consultas, estado del padrón de SUNAT y sus últimas cargas");

        grupo.MapPut("/modo", async (PeticionModo p, ContextoPlataforma db, OpcionesDecolecta decolecta, CancellationToken ct) =>
        {
            var modo = (p.Modo ?? "").Trim().ToLowerInvariant();
            if (modo == "decolecta" && !decolecta.Configurado)
                return Results.BadRequest(new
                {
                    error = "Para el modo Decolecta falta su token: DECOLECTA_TOKEN en el .env de la plataforma (y reiniciarla)."
                });

            var r = await db.Database.SqlQuery<string>($"SELECT fijar_consultas_modo({modo}) AS \"Value\"").FirstAsync(ct);
            return r switch
            {
                "ok" => Results.Ok(new
                {
                    modo,
                    mensaje = modo == "sunat"
                        ? "Modo SUNAT: RUC del padrón y tipo de cambio de la SBS, sin costo. El DNI se escribe a mano."
                        : "Modo Decolecta: RUC, DNI y tipo de cambio automáticos (cada consulta cuenta en tu plan)."
                }),
                "modo_invalido" => Results.BadRequest(new { error = "El modo es «sunat» o «decolecta»." }),
                _ => Results.Json(new { error = "No tienes permiso para cambiar el modo de consultas." },
                                  statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequierePermiso(Permisos.Plataforma.ConsultasGestionar)
        .WithSummary("Cambiar el modo de consultas: SUNAT (sin costo) o Decolecta");

        grupo.MapPost("/padron/descargar", (ColaPadron cola, OpcionesPadron op, ContextoPeticion ctx) =>
            cola.Encolar(new PedidoPadron("manual", ctx.AutorId, null, "Descarga desde SUNAT (pedida a mano)", null))
                ? Results.Accepted(value: new
                  {
                      mensaje = "Se empezó a bajar el padrón de SUNAT. Tarda varios minutos: el resultado sale en la lista."
                  })
                : Results.Conflict(new { error = "Ya hay una carga del padrón esperando. Espera a que termine." }))
        .RequierePermiso(Permisos.Plataforma.ConsultasGestionar)
        .WithSummary("Bajar ahora el padrón de SUNAT");

        // SUBIR EL ARCHIVO A MANO: el .zip de SUNAT (o el .txt de dentro), tal
        // cual, como cuerpo de la petición. Se guarda, se mira que sea el padrón
        // (si no, se dice por qué al instante) y se carga en segundo plano.
        grupo.MapPost("/padron/subir", async (
            HttpContext http, string? nombre, ColaPadron cola, OpcionesPadron op,
            ContextoPeticion ctx, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limite)
                limite.MaxRequestBodySize = 4L * 1024 * 1024 * 1024;

            var archivo = string.IsNullOrWhiteSpace(nombre) ? "archivo subido" : Path.GetFileName(nombre.Trim());
            if (archivo.Length > 200) archivo = archivo[..200];

            Directory.CreateDirectory(op.Carpeta);
            var ruta = Path.Combine(op.Carpeta, $"subido-{Guid.NewGuid():N}");
            long bytes;
            try
            {
                await using (var f = File.Create(ruta))
                    await http.Request.Body.CopyToAsync(f, 1 << 20, ct);
                bytes = new FileInfo(ruta).Length;
            }
            catch (IOException e)
            {
                Borrar(ruta);
                return Results.Json(new { error = $"No se pudo guardar el archivo en el servidor: {e.Message}" },
                                    statusCode: StatusCodes.Status507InsufficientStorage);
            }

            // ¿Es el padrón? Se mira ya, para decirlo en la pantalla y anotarlo.
            string? problema = bytes < 1000 ? "El archivo está vacío o incompleto." : null;
            if (problema is null)
            {
                try
                {
                    var (a, lector, _) = LectorPadron.Abrir(ruta);
                    await using (a) using (lector)
                        LectorPadron.LeerTitulos(await lector.ReadLineAsync(ct));
                }
                catch (LectorPadron.Problema e) { problema = e.Message; }
                catch (InvalidDataException) { problema = "El .zip está dañado o incompleto. Vuelve a bajarlo."; }
            }

            if (problema is not null)
            {
                Borrar(ruta);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT padron_anotar_fallo('manual', {ctx.AutorId}::uuid, {archivo}, {problema})", ct);
                return Results.BadRequest(new { error = problema });
            }

            if (!cola.Encolar(new PedidoPadron("manual", ctx.AutorId, ruta, archivo, bytes)))
            {
                Borrar(ruta);
                return Results.Conflict(new { error = "Ya hay una carga del padrón esperando. Espera a que termine." });
            }

            return Results.Accepted(value: new
            {
                mensaje = $"Recibido ({bytes / 1_048_576.0:N0} MB). Se está cargando: tarda varios minutos. El resultado sale en la lista."
            });
        })
        .RequierePermiso(Permisos.Plataforma.ConsultasGestionar)
        .WithSummary("Subir a mano el padrón de SUNAT (.zip o .txt)");

        // EL ESTADO PARA EL PANEL DEL SERVICIO (Vigilancia SUNAT). Sin datos de
        // nadie: el modo, cuándo se cargó, cuántos RUC y los motivos de error.
        rutas.MapGet("/api/publico/padron-estado", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var estado = await db.Database.SqlQuery<string>($"SELECT padron_estado() AS \"Value\"").FirstAsync(ct);
            using var doc = JsonDocument.Parse(estado);
            var r = doc.RootElement;

            return Results.Ok(new
            {
                modo = r.GetProperty("modo").GetString(),
                enUso = r.GetProperty("enUso").Clone(),
                enCurso = r.GetProperty("enCurso").GetBoolean(),
                cargas = r.GetProperty("cargas").EnumerateArray().Select(c => new
                {
                    origen = c.GetProperty("origen").GetString(),
                    estado = c.GetProperty("estado").GetString(),
                    iniciadaEn = c.GetProperty("iniciadaEn").GetString(),
                    terminadaEn = c.GetProperty("terminadaEn").ValueKind == JsonValueKind.String ? c.GetProperty("terminadaEn").GetString() : null,
                    filas = c.GetProperty("filas").ValueKind == JsonValueKind.Number ? c.GetProperty("filas").GetInt64() : (long?)null,
                    motivo = c.GetProperty("motivo").ValueKind == JsonValueKind.String ? c.GetProperty("motivo").GetString() : null
                }).ToList()
            });
        })
        .WithTags("Administración · consultas SUNAT")
        .WithSummary("Estado del padrón de SUNAT (sin datos de nadie), para la vigilancia del servicio");
    }

    public sealed record PeticionModo(string? Modo);

    private static void Borrar(string ruta)
    {
        try { File.Delete(ruta); } catch { /* temporal */ }
    }
}