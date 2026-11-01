using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Administracion;
using Plataforma.Api.Arranque;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.MiEmpresa;

public sealed record PeticionMiEmpresa(
    string? Tipo, string? Ruc, string? RazonSocial, string? NombreComercial,
    string? Direccion, string? Ubigeo, string? Distrito, string? Provincia, string? Departamento);
public sealed record PeticionSerieMia(string? TipoComprobante, string? Serie, int UltimoCorrelativo, bool Confirmado);
public sealed record PeticionSolMio(string? Usuario, string? Clave);
public sealed record PeticionMarcaMia(string? Identificador, string? ColorMarca, string? CorreoRespuesta, bool? CopiaAlEmisor);
public sealed record PeticionComprobanteCobros(string? SerieFactura, string? SerieBoleta, bool? EnviarCorreo, string? Descripcion);

/// <summary>
/// 075 — MI EMPRESA: la empresa de Reyban (RUC 20, o RUC 10 de persona natural
/// con negocio) que cobra los planes y emite la factura o boleta de cada pago.
///
/// Es un menú APARTE de «Empresas» (los clientes), solo del super
/// administrador con el permiso plataforma.mi_empresa:
///
///   GET    /api/admin/mi-empresa                  todo: la empresa, lo que dice el servicio, sus series
///   POST   /api/admin/mi-empresa                  registrarla (aquí y en el servicio, en beta)
///   POST   /api/admin/mi-empresa/confirmar        confirmar el alta en el servicio (otra petición)
///   POST   /api/admin/mi-empresa/certificado      el .pfx y su clave
///   POST   /api/admin/mi-empresa/certificado/prueba  uno de prueba (beta)
///   PUT    /api/admin/mi-empresa/sol              usuario SOL secundario
///   POST   /api/admin/mi-empresa/series           agregar una serie
///   DELETE /api/admin/mi-empresa/series/{id}      quitar una serie que nunca se usó
///   PUT    /api/admin/mi-empresa/marca            correo de respuesta, color, copia
///   POST   /api/admin/mi-empresa/logo             el logo del PDF
///   PUT    /api/admin/mi-empresa/comprobante      series y descripción de los comprobantes de cobros
///
/// EL ALTA ES LA MISMA DE «EMPRESAS › DAR DE ALTA» (el mismo orden: revisar
/// todo aquí, el emisor en el servicio, guardar aquí), SIN administrador ni
/// invitación: la configura el super administrador desde este menú.
///
/// SEGURIDAD: SOLO CONFIGURA. La empresa va por parámetro al servicio
/// (ConfigurarComoEmpresaAsync / LeerComoEmpresaAsync) y siempre es la marcada
/// como «Mi empresa»: no hay forma de tocar la de un cliente desde aquí. Y no
/// hay ruta para emitir: los comprobantes de cobros los emite el sistema solo.
/// </summary>
public static class EndpointsMiEmpresa
{
    private const string PuntoAlta = "mi_empresa";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapearMiEmpresa(this IEndpointRouteBuilder rutas)
    {
        var g = rutas.MapGroup("/api/admin/mi-empresa").WithTags("Mi empresa");

        // --- Todo ------------------------------------------------------------
        g.MapGet("/", async (ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(await db.Database
                .SqlQuery<string>($"SELECT mi_empresa()::text AS \"Value\"").FirstAsync(ct))!.AsObject();
            var id = await PropiaAsync(db, ct);
            if (id is null) return Results.Content(json.ToJsonString(), "application/json");

            var r = await facturacion.LeerComoEmpresaAsync<EndpointsCredenciales.EmisorDelServicio>(id.Value, "/v1/emisor", ct);
            var emisor = r.Exito ? r.Datos : null;
            JsonElement? series = null;
            if (emisor is not null)
            {
                // Un alta que se quedó sin confirmar (se cerró la pantalla): se confirma ahora.
                if (emisor.Alta?.Estado == "sin_confirmar")
                    await EstadoDelEmisor.ConfirmarAsync(db, facturacion, id.Value, ct);
                await CertificadoDelEmisor.GuardarAsync(db, id.Value, emisor.CertificadoValidoHasta, ct);
                await EstadoDelEmisor.GuardarAsync(db, id.Value, emisor, ct);
                var s = await facturacion.ConfigurarComoEmpresaAsync<JsonElement>(id.Value, HttpMethod.Get, "v1/configuracion/series", ct: ct);
                if (s.Exito) series = s.Datos;
            }
            json["servicio"] = new System.Text.Json.Nodes.JsonObject
            {
                ["alcanzado"] = emisor is not null,
                ["error"] = emisor is null ? (r.Error ?? "No pudimos hablar con el servicio de facturación.") : null,
            };
            json["emisor"] = emisor is null ? null : System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(emisor, Json));
            json["series"] = series is null ? null : System.Text.Json.Nodes.JsonNode.Parse(series.Value.GetRawText());
            json["pasos"] = new System.Text.Json.Nodes.JsonObject
            {
                ["empresa"] = true,
                ["certificado"] = emisor?.Listo?.Certificado == true,
                ["sol"] = emisor?.Listo?.Sol == true,
                ["series"] = emisor?.Listo?.Series is { Length: > 0 },
                ["direccion"] = emisor?.Listo?.Direccion == true,
                ["listo"] = emisor?.Listo?.ParaEmitir == true,
            };
            return Results.Content(json.ToJsonString(), "application/json");
        })
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Mi empresa: los datos, lo que dice el servicio y sus series");

        // --- Registrarla -----------------------------------------------------
        g.MapPost("/", async (PeticionMiEmpresa p, ContextoPlataforma db, ClienteFacturacion facturacion,
            OpcionesServicioFacturacion servicio, Cifrador cifrador, UrlApi urlApi, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("MiEmpresa.Alta");
            if (await PropiaAsync(db, ct) is not null)
                return Results.Conflict(new { error = "Tu empresa ya está registrada. Sus datos oficiales se corrigen en el servicio (Marca)." });

            var tipo = (p.Tipo ?? "").Trim().ToLowerInvariant();
            if (tipo is not ("ruc20" or "ruc10"))
                return Results.BadRequest(new { error = "Elige si es una empresa (RUC 20) o una persona natural con negocio (RUC 10).", campo = "tipo" });

            var ruc = (p.Ruc ?? "").Trim();
            if (ReglasEmisor.ProblemaRuc(ruc) is { } errorRuc)
                return Results.BadRequest(new { error = errorRuc, campo = "ruc" });
            if (tipo == "ruc20" && !ruc.StartsWith("20", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Una empresa tiene RUC que empieza en 20. Si es tu RUC personal, elige «Persona natural (RUC 10)».", campo = "ruc" });
            if (tipo == "ruc10" && !(ruc.StartsWith("10", StringComparison.Ordinal) || ruc.StartsWith("15", StringComparison.Ordinal) || ruc.StartsWith("17", StringComparison.Ordinal)))
                return Results.BadRequest(new { error = "El RUC de una persona natural empieza en 10. Si es de una empresa, elige «Empresa (RUC 20)».", campo = "ruc" });

            var razon = Limpio(p.RazonSocial);
            if (razon.Length is < 3 or > 300)
                return Results.BadRequest(new { error = tipo == "ruc10" ? "Escribe tu nombre completo, como sale en tu ficha RUC." : "Falta la razón social, tal como sale en la ficha RUC.", campo = "razonSocial" });
            var comercial = Limpio(p.NombreComercial) is { Length: > 0 } nc ? nc : null;
            if (comercial is { Length: > 300 }) return Results.BadRequest(new { error = "El nombre comercial es demasiado largo.", campo = "nombreComercial" });

            var (direccion, errorDireccion) = ReglasEmisor.Revisar(p.Ubigeo, p.Direccion, p.Distrito, p.Provincia, p.Departamento);
            if (direccion is null) return Results.BadRequest(new { error = errorDireccion, campo = "direccion" });

            var yaEsta = await db.Empresas.AsNoTracking().Where(e => e.Ruc == ruc).Select(e => e.Nombre).FirstOrDefaultAsync(ct);
            if (yaEsta is not null)
                return Results.Conflict(new { error = $"El RUC {ruc} ya es de {yaEsta}, aquí (en Empresas). No se creó nada.", campo = "ruc" });

            if (servicio.ProblemaAlta() is { } faltaConfig)
                return Results.Json(new { error = faltaConfig }, statusCode: StatusCodes.Status503ServiceUnavailable);

            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({"alta-ruc:" + ruc}::text))", ct);

            var empresaId = Guid.NewGuid();
            var urlWebhook = EndpointsCredenciales.UrlWebhook(urlApi, empresaId);
            var r = await facturacion.CrearEmisorConClaveAltaAsync<AltaDelServicio>(servicio.BaseUrl, servicio.ClaveAlta, new
            {
                ruc, razonSocial = razon, nombreComercial = comercial,
                direccion = direccion.Direccion, ubigeo = direccion.Ubigeo, distrito = direccion.Distrito,
                provincia = direccion.Provincia, departamento = direccion.Departamento, webhookUrl = urlWebhook
            }, ct);

            if (!r.Exito)
                return Results.Json(new { error = r.Estado == 409 ? (r.Error ?? "El servicio dice que ese RUC ya existe.") : (r.Error ?? "El servicio no pudo crear el emisor."), detalle = r.Detalle },
                                    statusCode: r.Estado is 400 or 409 ? r.Estado : StatusCodes.Status502BadGateway);
            if (r.Datos is not { } alta || alta.Clave is not { Length: >= 16 } clave || !clave.StartsWith("fac_", StringComparison.Ordinal)
                || alta.Webhook?.Secreto is not { Length: >= 30 } secreto || !secreto.StartsWith("whsec_", StringComparison.Ordinal))
                return Results.Json(new { error = "El servicio respondió sin la clave o sin el secreto: aquí no se guardó nada. Repite en dos minutos." },
                                    statusCode: StatusCodes.Status502BadGateway);

            var transaccion = db.Database.CurrentTransaction
                ?? throw new InvalidOperationException("Mi empresa necesita la transacción de MiddlewareContexto.");
            await transaccion.CreateSavepointAsync(PuntoAlta, ct);
            try
            {
                var ahora = DateTime.UtcNow;
                var empresa = new Empresa
                {
                    Id = empresaId, Nombre = comercial ?? razon, Ruc = ruc, RazonSocial = razon,
                    NombreComercial = comercial, Ambiente = DatosOficiales.Ambiente(alta.Ambiente) ?? "beta",
                    DatosOficialesEn = ahora,
                };
                db.Empresas.Add(empresa);
                db.CredencialesFacturacion.Add(new CredencialFacturacion
                {
                    Empresa = empresa, BaseUrl = servicio.BaseUrl,
                    ClaveCifrada = cifrador.Cifrar(clave), Pista = clave[^4..],
                    WebhookSecretoCifrado = cifrador.Cifrar(secreto),
                    WebhookUrlServicio = alta.Webhook.Url ?? urlWebhook, WebhookRevisadoEn = ahora,
                    UltimaLlamadaEn = ahora, CreadaPorAlta = true
                });
                await db.SaveChangesAsync(ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT mi_empresa_fijar({empresaId})", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    SELECT registrar_evento('otro'::text, true, NULL, {empresaId}::uuid, NULL,
                        jsonb_build_object('accion','alta_mi_empresa','ruc',{ruc}::text,'emisor',{alta.EmisorId.ToString()}::text))
                    """, ct);
            }
            catch (Exception e) when (e is DbUpdateException or Npgsql.PostgresException)
            {
                await transaccion.RollbackToSavepointAsync(PuntoAlta, ct);
                db.ChangeTracker.Clear();
                log.LogError(e, "Mi empresa: el servicio creó el emisor {Emisor} (RUC {Ruc}) pero aquí no se pudo guardar.", alta.EmisorId, ruc);
                return Results.Json(new { error = "El emisor se creó en el servicio, pero aquí no se pudo guardar. Repite en dos minutos." },
                                    statusCode: StatusCodes.Status500InternalServerError);
            }

            log.LogInformation("Mi empresa registrada: RUC {Ruc} (emisor {Emisor}, beta).", ruc, alta.EmisorId);
            return Results.Ok(new
            {
                id = empresaId,
                siguiente = "POST /api/admin/mi-empresa/confirmar",
                mensaje = $"Listo: {razon} quedó registrada (en beta). Ahora sube tu certificado y crea tus series."
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Registrar mi empresa (RUC 20 o RUC 10), aquí y en el servicio");

        g.MapPost("/confirmar", async (ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (await PropiaAsync(db, ct) is not { } id) return SinEmpresa();
            var estado = await EstadoDelEmisor.ConfirmarAsync(db, facturacion, id, ct);
            return Results.Ok(new { alta = estado });
        })
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Confirmar el alta de mi empresa en el servicio");

        // --- Facturación electrónica -----------------------------------------
        g.MapPost("/certificado", async (IFormFile archivo, [FromForm] string? clave, [FromForm] bool? confirmado,
            ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (await PropiaAsync(db, ct) is not { } id) return SinEmpresa();
            if (archivo.Length is 0 or > 512 * 1024)
                return Results.BadRequest(new { error = archivo.Length == 0 ? "El archivo está vacío." : "El archivo es demasiado grande: un certificado .pfx pesa unos pocos kilobytes." });
            var contenido = await FormularioAsync(archivo, ct);
            contenido.Add(new StringContent(clave ?? ""), "clave");
            if (confirmado == true) contenido.Add(new StringContent("true"), "confirmado");
            var r = await facturacion.ConfigurarComoEmpresaAsync<JsonElement>(id, HttpMethod.Post, "v1/configuracion/certificado", contenido: contenido, ct: ct);
            if (r.Exito) await RefrescarAsync(db, facturacion, id, ct);
            return Responder(r, codigo422: "ruc_no_coincide");
        })
        .DisableAntiforgery()
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Subir el certificado de mi empresa (.pfx y su contraseña)");

        g.MapPost("/certificado/prueba", async (ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (await PropiaAsync(db, ct) is not { } id) return SinEmpresa();
            var r = await facturacion.ConfigurarComoEmpresaAsync<JsonElement>(id, HttpMethod.Post, "v1/configuracion/certificado/prueba", ct: ct);
            if (r.Exito) await RefrescarAsync(db, facturacion, id, ct);
            return Responder(r);
        })
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Usar un certificado de prueba (solo beta)");

        g.MapPut("/sol", async (PeticionSolMio p, ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (await PropiaAsync(db, ct) is not { } id) return SinEmpresa();
            var r = await facturacion.ConfigurarComoEmpresaAsync<JsonElement>(id, HttpMethod.Put, "v1/configuracion/sol",
                new { usuario = p.Usuario, clave = p.Clave }, ct: ct);
            if (r.Exito) await RefrescarAsync(db, facturacion, id, ct);
            return Responder(r);
        })
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Usuario SOL secundario de mi empresa");

        g.MapPut("/marca", async (PeticionMarcaMia p, ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (await PropiaAsync(db, ct) is not { } id) return SinEmpresa();
            var r = await facturacion.ConfigurarComoEmpresaAsync<JsonElement>(id, HttpMethod.Put, "v1/configuracion/marca", new
            {
                identificador = p.Identificador, colorMarca = p.ColorMarca, correoRespuesta = p.CorreoRespuesta, copiaAlEmisor = p.CopiaAlEmisor
            }, ct: ct);
            return Responder(r);
        })
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Correo de respuesta, color y página del portal de mi empresa");

        g.MapPost("/logo", async (IFormFile archivo, ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (await PropiaAsync(db, ct) is not { } id) return SinEmpresa();
            if (archivo.Length is 0 or > 1024 * 1024) return Results.BadRequest(new { error = "El logo está vacío o pesa demasiado (máximo 1 MB)." });
            var r = await facturacion.ConfigurarComoEmpresaAsync<JsonElement>(id, HttpMethod.Post, "v1/configuracion/logo",
                contenido: await FormularioAsync(archivo, ct), ct: ct);
            return Responder(r);
        })
        .DisableAntiforgery()
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("El logo del PDF de mi empresa");

        // --- Series -----------------------------------------------------------
        g.MapPost("/series", async (PeticionSerieMia p, ContextoPeticion ctx, ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (await PropiaAsync(db, ct) is not { } id) return SinEmpresa();
            var correo = await db.Usuarios.AsNoTracking().Where(u => u.Id == ctx.UsuarioId).Select(u => u.Correo).FirstOrDefaultAsync(ct);
            var r = await facturacion.ConfigurarComoEmpresaAsync<JsonElement>(id, HttpMethod.Post, "v1/configuracion/series", new
            {
                tipoComprobante = p.TipoComprobante, serie = p.Serie, ultimoCorrelativo = p.UltimoCorrelativo,
                confirmado = p.Confirmado, actor = correo
            }, ct: ct);
            return Responder(r, codigo422: "confirmar_correlativo");
        })
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Agregar una serie a mi empresa");

        g.MapDelete("/series/{serieId:guid}", async (Guid serieId, ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (await PropiaAsync(db, ct) is not { } id) return SinEmpresa();
            return Responder(await facturacion.ConfigurarComoEmpresaAsync<JsonElement>(id, HttpMethod.Delete, $"v1/configuracion/series/{serieId}", ct: ct));
        })
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Quitar una serie que nunca se usó");

        // --- Comprobantes de cobros -------------------------------------------
        g.MapPut("/comprobante", async (PeticionComprobanteCobros p, ContextoPlataforma db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT cobros_fijar_comprobante({p.SerieFactura}::text, {p.SerieBoleta}::text, {p.EnviarCorreo ?? true}, {p.Descripcion ?? ""})
                """, ct);
            return Results.Ok(new { mensaje = "Listo: los comprobantes de los cobros saldrán con estas series." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.MiEmpresa)
        .WithSummary("Series y descripción de los comprobantes de cobros");
    }

    // =========================================================================

    private static Task<Guid?> PropiaAsync(ContextoPlataforma db, CancellationToken ct) =>
        db.Database.SqlQuery<Guid?>($"SELECT empresa_propia AS \"Value\" FROM plataforma_ajustes WHERE id = 1").FirstOrDefaultAsync(ct);

    private static IResult SinEmpresa() =>
        Results.BadRequest(new { error = "Primero registra tu empresa en «Mi empresa › Datos de la empresa»." });

    private static async Task RefrescarAsync(ContextoPlataforma db, ClienteFacturacion facturacion, Guid id, CancellationToken ct)
    {
        try
        {
            var e = await facturacion.LeerComoEmpresaAsync<EndpointsCredenciales.EmisorDelServicio>(id, "/v1/emisor", ct);
            if (!e.Exito || e.Datos is not { } emisor) return;
            await CertificadoDelEmisor.GuardarAsync(db, id, emisor.CertificadoValidoHasta, ct);
            await EstadoDelEmisor.GuardarAsync(db, id, emisor, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* lo corrige la revisión diaria */ }
    }

    private static IResult Responder(ResultadoFacturacion<JsonElement> r, string? codigo422 = null)
    {
        if (r.Exito) return Results.Json(r.Datos, Json);
        var texto = string.Join(" ", new[] { r.Error, r.Detalle }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return r.Estado switch
        {
            400 => Results.BadRequest(new { error = texto }),
            409 => Results.Conflict(new { error = texto }),
            404 => Results.NotFound(new { error = texto }),
            422 => Results.UnprocessableEntity(new { error = texto, codigo = codigo422 }),
            401 or 403 => Results.Json(new { error = "El servicio de facturación no reconoce la clave de tu empresa. No se guardó nada." },
                                       statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(new { error = "No pudimos hablar con el servicio de facturación. No se guardó nada: inténtalo en unos minutos." },
                              statusCode: StatusCodes.Status502BadGateway)
        };
    }

    private static async Task<MultipartFormDataContent> FormularioAsync(IFormFile archivo, CancellationToken ct)
    {
        using var memoria = new MemoryStream();
        await archivo.CopyToAsync(memoria, ct);
        var parte = new ByteArrayContent(memoria.ToArray());
        parte.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var nombre = Path.GetFileName(archivo.FileName);
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Length > 100) nombre = "archivo";
        return new MultipartFormDataContent { { parte, "archivo", nombre } };
    }

    private static string Limpio(string? s) =>
        string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}