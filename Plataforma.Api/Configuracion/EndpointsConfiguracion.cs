using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Administracion;
using Plataforma.Api.Arranque;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Configuracion;

/// <summary>Paso 1 del asistente: los datos de la empresa, tal cual la ficha RUC.</summary>
public record PeticionEmpresaPropia(
    string? Ruc,
    string? RazonSocial,
    string? NombreComercial,
    string? Direccion,
    string? Ubigeo,
    string? Distrito,
    string? Provincia,
    string? Departamento,
    string? CorreoTitular);

public record PeticionSerie(string? TipoComprobante, string? Serie, int UltimoCorrelativo, bool Confirmado);

public record PeticionMarca(string? Identificador, string? ColorMarca, string? CorreoRespuesta, bool? CopiaAlEmisor);

/// <summary>23 oct: el formato del PDF (moderno / clasico).</summary>
public record PeticionFormatoPdf(string? Formato);

public record PeticionSol(string? Usuario, string? Clave);


/// <summary>
/// EL ASISTENTE DE CONFIGURACIÓN (fase 2): de «tu cuenta está lista» a «lista
/// para emitir», sin el super administrador.
///
///   GET  /api/configuracion                     dónde va: qué tiene y qué le falta
///   POST /api/configuracion/empresa             paso 1: crea la empresa y su emisor
///   POST /api/configuracion/confirmar           confirma el alta en el servicio (otra petición)
///   POST /api/configuracion/certificado         paso 2: el .pfx
///   POST /api/configuracion/certificado/prueba  paso 2: uno de prueba (beta)
///   POST /api/configuracion/series              paso 3
///   PUT  /api/configuracion/marca               paso 4
///   POST /api/configuracion/logo                paso 4
///   PUT  /api/configuracion/sol                 paso 5 (para producción)
///   POST /api/configuracion/terminar            paso 6: activa Facturación
///
/// EL PASO 1 ES EL MISMO ALTA DEL SUPER ADMINISTRADOR (EndpointsEmpresas), con
/// la misma clave de alta y el mismo orden: revisar todo aquí, el emisor en el
/// servicio, guardar aquí. Lo único distinto es quién queda de Administrador:
/// quien lo hace, sin invitación, porque ya tiene su contraseña.
///
/// LOS DEMÁS PASOS VAN AL SERVICIO CON LA CLAVE fac_ DE LA EMPRESA, por
/// /v1/configuracion. La empresa sale de la sesión (ClienteFacturacion), nunca
/// de la petición: nadie configura la empresa de otro.
/// </summary>
public static class EndpointsConfiguracion
{
    private const string Permiso = "facturacion.credenciales_gestionar";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapearConfiguracion(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/configuracion").WithTags("Configuración");

        // =====================================================================
        // DÓNDE VA
        // =====================================================================
        grupo.MapGet("/", async (
            bool? nueva,
            ContextoPeticion ctx, ContextoPlataforma db,
            ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            // Sin empresa, o «Agregar empresa» desde la cartera (?nueva=true,
            // fase 3): ¿puede agregar una?
            if (ctx.EmpresaId is null || nueva == true)
            {
                var codigo = await RevisarAsync(db, null, ct);

                return Results.Ok(new
                {
                    empresa = (object?)null,
                    puedeAgregar = codigo == "ok",
                    motivo = codigo == "ok" ? null : Motivo(codigo)
                });
            }

            if (!ctx.Tiene(Permiso))
                return Results.Json(new { error = "La configuración de la empresa la hace su administrador." },
                                    statusCode: StatusCodes.Status403Forbidden);

            return Results.Ok(await EstadoAsync(ctx, db, facturacion, ct));
        })
        .RequiereSesion()
        .WithSummary("El estado del asistente: la empresa, lo que tiene y lo que le falta");


        // =====================================================================
        // PASO 1 — LA EMPRESA, AQUÍ Y EN EL SERVICIO
        // =====================================================================
        grupo.MapPost("/empresa", async (
            PeticionEmpresaPropia p,
            ContextoPeticion ctx,
            ContextoPlataforma db,
            ClienteFacturacion facturacion,
            OpcionesServicioFacturacion servicio,
            Cifrador cifrador,
            UrlApi urlApi,
            ILoggerFactory logs,
            CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Configuracion.Empresa");

            // --- 1. Todo lo que se puede revisar sin el servicio ---------------

            var ruc = (p.Ruc ?? "").Trim();
            if (ReglasEmisor.ProblemaRuc(ruc) is { } errorRuc)
                return Campo("ruc", errorRuc);

            var razon = Limpio(p.RazonSocial);
            if (razon.Length is < 3 or > 300)
                return Campo("razonSocial", "Falta la razón social, tal como sale en la ficha RUC.");

            var comercial = Limpio(p.NombreComercial) is { Length: > 0 } nc ? nc : null;
            if (comercial is { Length: > 200 })
                return Campo("nombreComercial", "El nombre comercial es demasiado largo (máximo 200).");

            var (direccion, errorDireccion) = ReglasEmisor.Revisar(
                p.Ubigeo, p.Direccion, p.Distrito, p.Provincia, p.Departamento);

            if (direccion is null)
                return Campo(CampoDeDireccion(errorDireccion), errorDireccion ?? "La dirección no es válida.");

            var correoTitular = (p.CorreoTitular ?? "").Trim();
            if (correoTitular.Length is < 3 or > 254 || !correoTitular.Contains('@') ||
                correoTitular.Any(char.IsWhiteSpace) || !correoTitular[(correoTitular.IndexOf('@') + 1)..].Contains('.'))
                return Campo("correoTitular", "Falta el correo del titular, o no es un correo.");

            var nombre = comercial ?? razon;
            if (nombre.Length > 200) nombre = nombre[..200];

            // ¿Puede, y el RUC está libre? Antes de llamar al servicio: un «no»
            // aquí no deja nada creado en ningún lado.
            var revision = await RevisarAsync(db, ruc, ct);
            if (revision != "ok")
                return Results.Json(new { error = Motivo(revision), codigo = revision == "ruc_ocupado" ? "campo_ruc" : revision },
                                    statusCode: StatusCodes.Status409Conflict);

            if (servicio.ProblemaAlta() is { } faltaConfig)
            {
                log.LogError("Asistente: no se puede crear el emisor. {Problema}", faltaConfig);
                return Results.Json(new
                {
                    error = "Agregar empresas no está disponible en este momento. No se creó nada. " +
                            "Escríbenos y lo resolvemos."
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // --- 2. Un alta por RUC a la vez (el mismo candado que el super admin)
            var llave = "alta-ruc:" + ruc;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({llave}::text))", ct);

            revision = await RevisarAsync(db, ruc, ct);
            if (revision != "ok")
                return Results.Json(new { error = Motivo(revision), codigo = revision == "ruc_ocupado" ? "campo_ruc" : revision },
                                    statusCode: StatusCodes.Status409Conflict);

            // --- 3. El emisor, en el servicio ---------------------------------
            var empresaId  = Guid.NewGuid();
            var urlWebhook = EndpointsCredenciales.UrlWebhook(urlApi, empresaId);

            var r = await facturacion.CrearEmisorConClaveAltaAsync<AltaDelServicio>(
                servicio.BaseUrl, servicio.ClaveAlta, new
                {
                    ruc,
                    razonSocial     = razon,
                    nombreComercial = comercial,
                    direccion       = direccion.Direccion,
                    ubigeo          = direccion.Ubigeo,
                    distrito        = direccion.Distrito,
                    provincia       = direccion.Provincia,
                    departamento    = direccion.Departamento,
                    webhookUrl      = urlWebhook
                }, ct);

            if (!r.Exito)
            {
                log.LogWarning("Asistente: el servicio no creó el emisor del RUC {Ruc} ({Estado}: {Error}).",
                               ruc, r.Estado, r.Error);

                return r.Estado switch
                {
                    409 => Results.Json(new
                    {
                        error = "Ese RUC ya está registrado. No se creó nada. Si es tuyo, escríbenos: " +
                                "pronto podrás recuperarlo tú mismo.",
                        codigo = "campo_ruc"
                    }, statusCode: StatusCodes.Status409Conflict),

                    429 => Results.Json(new
                    {
                        error = "Hoy se agregaron muchas empresas y llegamos al tope del día. No se creó nada. " +
                                "Vuelve a intentarlo mañana."
                    }, statusCode: StatusCodes.Status429TooManyRequests),

                    400 => Results.BadRequest(new { error = Unir(r.Error, r.Detalle) }),

                    // 401/403: la clave de alta (FACTURACION_CLAVE_ALTA) no vale.
                    // Esperar no lo arregla: lo arregla quien administra la plataforma.
                    401 or 403 => Results.Json(new
                    {
                        error = "La plataforma no tiene una clave válida para dar de alta empresas en el servicio " +
                                "de facturación. No se creó nada. Avisa al administrador de la plataforma.",
                        codigo = "clave_alta"
                    }, statusCode: StatusCodes.Status503ServiceUnavailable),

                    _ => Results.Json(new
                    {
                        error = "No pudimos hablar con el servicio de facturación. No se creó nada: " +
                                "inténtalo en unos minutos."
                    }, statusCode: StatusCodes.Status502BadGateway)
                };
            }

            if (r.Datos is not { } alta ||
                alta.Clave is not { Length: >= 16 } clave || !clave.StartsWith("fac_", StringComparison.Ordinal) ||
                alta.Webhook?.Secreto is not { Length: >= 30 } secreto || !secreto.StartsWith("whsec_", StringComparison.Ordinal))
            {
                log.LogError("Asistente: el servicio creó el emisor del RUC {Ruc} sin clave o secreto válidos.", ruc);

                return Results.Json(new
                {
                    error = "El servicio respondió algo incompleto, así que no se guardó nada. " +
                            "Vuelve a intentarlo en dos minutos."
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            // --- 4. Todo aquí, de una vez (la función revisa otra vez) --------
            var transaccion = db.Database.CurrentTransaction
                ?? throw new InvalidOperationException("El alta necesita la transacción de MiddlewareContexto.");

            await transaccion.CreateSavepointAsync(PuntoAlta, ct);

            string resultado;

            try
            {
                var claveCifrada  = cifrador.Cifrar(clave);
                var whsecCifrado  = cifrador.Cifrar(secreto);
                var ambiente      = DatosOficiales.Ambiente(alta.Ambiente) ?? "beta";
                var urlServicio   = alta.Webhook.Url ?? urlWebhook;
                var pista         = clave[^4..];

                resultado = await db.Database.SqlQuery<string>($"""
                    SELECT crear_empresa_propia(
                        {empresaId}::uuid, {nombre}::text, {ruc}::text, {razon}::text,
                        {comercial}::text, {ambiente}::text, {correoTitular}::text,
                        {servicio.BaseUrl}::text, {claveCifrada}::bytea, {pista}::text,
                        {whsecCifrado}::bytea, {urlServicio}::text) AS "Value"
                    """).FirstAsync(ct);
            }
            catch (Exception e) when (e is Npgsql.PostgresException or InvalidOperationException or DbUpdateException)
            {
                await transaccion.RollbackToSavepointAsync(PuntoAlta, ct);

                log.LogError(e, "Asistente: el emisor {Emisor} del RUC {Ruc} se creó en el servicio pero la empresa " +
                                "no se pudo guardar aquí. Allí queda «sin confirmar».", alta.EmisorId, ruc);

                return Results.Json(new
                {
                    error = "No se pudo guardar la empresa, así que no quedó nada. Vuelve a intentarlo en " +
                            "dos minutos."
                }, statusCode: StatusCodes.Status500InternalServerError);
            }

            if (resultado != "ok")
            {
                // Otra pestaña llegó antes, o la cuenta cambió entre medias. El
                // emisor queda «sin confirmar» en el servicio y se abandona solo.
                await transaccion.RollbackToSavepointAsync(PuntoAlta, ct);
                return Results.Json(new { error = Motivo(resultado), codigo = resultado },
                                    statusCode: StatusCodes.Status409Conflict);
            }

            log.LogInformation("Asistente: {Nombre} (RUC {Ruc}) creada por el usuario {Usuario} (emisor {Emisor}, beta).",
                               nombre, ruc, ctx.UsuarioId, alta.EmisorId);

            return Results.Ok(new
            {
                empresa = new { id = empresaId, nombre, ruc, razonSocial = razon },

                // La pantalla la llama sola, en otra petición: esta transacción
                // se confirma DESPUÉS de responder.
                siguiente = "POST /api/configuracion/confirmar",
                mensaje = $"{nombre} agregada. Ahora, el certificado digital."
            });
        })
        .RequiereSesion()
        .WithSummary("Paso 1: agrega la empresa (y su emisor en el servicio)");


        // =====================================================================
        grupo.MapPost("/confirmar", async (
            ContextoPeticion ctx, ContextoPlataforma db,
            ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var empresaId = ctx.EmpresaId!.Value;

            // Seguro de llamar de más: si ya estaba confirmada, no cambia nada.
            await EstadoDelEmisor.ConfirmarAsync(db, facturacion, empresaId, ct);

            return Results.Ok(await EstadoAsync(ctx, db, facturacion, ct));
        })
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Confirma el alta en el servicio y devuelve el estado");


        // =====================================================================
        // PASO 2 — EL CERTIFICADO
        // =====================================================================
        grupo.MapPost("/certificado", async (
            IFormFile archivo,
            [FromForm] string? clave,
            [FromForm] bool? confirmado,
            ClienteFacturacion facturacion,
            CancellationToken ct) =>
        {
            if (archivo.Length is 0 or > 512 * 1024)
                return Results.BadRequest(new
                {
                    error = archivo.Length == 0
                        ? "El archivo está vacío."
                        : "El archivo es demasiado grande: un certificado .pfx pesa unos pocos kilobytes.",
                    codigo = "campo_archivo"
                });

            var contenido = await FormularioAsync(archivo, ct);
            contenido.Add(new StringContent(clave ?? ""), "clave");
            if (confirmado == true) contenido.Add(new StringContent("true"), "confirmado");

            var r = await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Post, "v1/configuracion/certificado", contenido: contenido, ct: ct);

            return Responder(r, codigo422: "ruc_no_coincide", campo400: "campo_clave");
        })
        .DisableAntiforgery()
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 2: sube el certificado (.pfx y su contraseña)");

        grupo.MapPost("/certificado/prueba", async (ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var r = await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Post, "v1/configuracion/certificado/prueba", ct: ct);

            return Responder(r);
        })
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 2: usa un certificado de prueba (solo beta)");


        // =====================================================================
        // PASO 3 — LAS SERIES
        // =====================================================================
        grupo.MapPost("/series", async (
            PeticionSerie p, ContextoPeticion ctx, ContextoPlataforma db,
            ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            // Para la bitácora de correlativos del servicio: quién fue, aquí.
            var correo = await db.Usuarios.AsNoTracking()
                .Where(u => u.Id == ctx.UsuarioId).Select(u => u.Correo).FirstOrDefaultAsync(ct);

            var r = await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Post, "v1/configuracion/series", new
                {
                    tipoComprobante   = p.TipoComprobante,
                    serie             = p.Serie,
                    ultimoCorrelativo = p.UltimoCorrelativo,
                    confirmado        = p.Confirmado,
                    actor             = correo
                }, ct: ct);

            return Responder(r, codigo422: "confirmar_correlativo", campo400: "campo_serie");
        })
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 3: agrega una serie");

        // 22 OCT: quitar una serie que nunca se usó (la decide el servicio).
        grupo.MapDelete("/series/{id:guid}", async (Guid id, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var r = await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Delete, $"v1/configuracion/series/{id}", ct: ct);
            return Responder(r);
        })
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 3: quita una serie que nunca se usó");


        // =====================================================================
        // PASO 4 — CORREO Y MARCA
        // =====================================================================
        grupo.MapPut("/marca", async (PeticionMarca p, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var r = await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Put, "v1/configuracion/marca", new
                {
                    identificador   = p.Identificador,
                    colorMarca      = p.ColorMarca,
                    correoRespuesta = p.CorreoRespuesta,
                    copiaAlEmisor   = p.CopiaAlEmisor
                }, ct: ct);

            return Responder(r);
        })
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 4: correo para respuestas, copia, color y dirección de la página");

        grupo.MapPost("/logo", async (IFormFile archivo, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (archivo.Length is 0 or > 1024 * 1024)
                return Results.BadRequest(new { error = "El logo está vacío o pesa demasiado.", codigo = "campo_logo" });

            var contenido = await FormularioAsync(archivo, ct);

            var r = await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Post, "v1/configuracion/logo", contenido: contenido, ct: ct);

            return Responder(r, campo400: "campo_logo");
        })
        .DisableAntiforgery()
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 4: el logo (PNG o JPG)");

        // 23 OCT: EL FORMATO DEL PDF (moderno / clásico estilo SUNAT) y su ejemplo.
        grupo.MapGet("/formato-pdf", async (ClienteFacturacion facturacion, CancellationToken ct) =>
            Responder(await facturacion.ConfigurarAsync<JsonElement>(HttpMethod.Get, "v1/configuracion/formato-pdf", ct: ct)))
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 4: el formato del PDF de la empresa");

        grupo.MapPut("/formato-pdf", async (PeticionFormatoPdf p, ClienteFacturacion facturacion, CancellationToken ct) =>
            Responder(await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Put, "v1/configuracion/formato-pdf", new { formato = p.Formato }, ct: ct)))
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 4: cambiar el formato del PDF (moderno / clasico)");

        grupo.MapGet("/formato-pdf/muestra", async (
            string? formato, ContextoPeticion ctx, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var r = await facturacion.DescargarComprobanteAsync(
                ctx.EmpresaId!.Value, Guid.Empty, ArchivoComprobante.MuestraPdf, ct, formato);
            return r.Exito && r.Contenido is not null
                ? Results.File(r.Contenido, "application/pdf", $"ejemplo-{(formato == "clasico" ? "clasico" : "moderno")}.pdf")
                : Results.Json(new { error = r.Error ?? "No se pudo generar el ejemplo.", detalle = r.Detalle },
                               statusCode: StatusCodes.Status502BadGateway);
        })
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 4: un PDF de ejemplo del formato, con los datos de la empresa");


        // =====================================================================
        // PASO 5 — SUNAT (para producción)
        // =====================================================================
        grupo.MapPut("/sol", async (PeticionSol p, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var r = await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Put, "v1/configuracion/sol", new { usuario = p.Usuario, clave = p.Clave }, ct: ct);

            return Responder(r, campo400: "campo_usuarioSol");
        })
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 5: usuario SOL secundario y su clave");


        // =====================================================================
        // PASO 6 — LISTA PARA EMITIR: SE ACTIVA FACTURACIÓN
        //
        // Se pregunta al servicio EN EL MOMENTO (como al activar el módulo desde
        // el panel del super administrador): certificado vigente, una serie y la
        // dirección. El SOL solo se exige en producción.
        // =====================================================================
        grupo.MapPost("/terminar", async (
            ContextoPeticion ctx, ContextoPlataforma db,
            ClienteFacturacion facturacion, OpcionesConexionSunat conexionSunat,
            OpcionesServicioFacturacion servicio, CancellationToken ct) =>
        {
            var empresaId = ctx.EmpresaId!.Value;

            var ruc = await db.Empresas.AsNoTracking()
                .Where(e => e.Id == empresaId).Select(e => e.Ruc).FirstOrDefaultAsync(ct);

            var r = await facturacion.ObtenerAsync<EndpointsCredenciales.EmisorDelServicio>("v1/emisor", ct);

            if (!r.Exito || r.Datos is not { } emisor)
                return Results.Json(new
                {
                    error = "No pudimos preguntar al servicio si tu empresa ya puede emitir. Inténtalo en unos minutos."
                }, statusCode: StatusCodes.Status502BadGateway);

            if (!string.Equals(emisor.Ruc?.Trim(), ruc, StringComparison.Ordinal))
                return Results.Json(new
                {
                    error = "La clave guardada no es de esta empresa. No se activó nada: escríbenos."
                }, statusCode: StatusCodes.Status409Conflict);

            if (emisor.Alta?.Estado == "sin_confirmar")
                await EstadoDelEmisor.ConfirmarAsync(db, facturacion, empresaId, ct);

            await CertificadoDelEmisor.GuardarAsync(db, empresaId, emisor.CertificadoValidoHasta, ct);
            await EstadoDelEmisor.GuardarAsync(db, empresaId, emisor, ct);

            if (emisor.Listo?.ParaEmitir != true)
                return Results.BadRequest(new
                {
                    error = "Todavía falta algo para emitir: " + string.Join(" ", emisor.Listo?.Faltan ?? []),
                    codigo = "falta_algo"
                });

            // 030: EN PRODUCCIÓN (PLATAFORMA_SUNAT=real) la empresa pasa sola a
            // SUNAT real aquí, con la misma revisión del panel: certificado
            // real, usuario y clave SOL, series. Si falta algo, se dice y no se
            // activa. En QA sigue en beta (el super la pasa con el interruptor).
            if (conexionSunat.Fija && emisor.Ambiente != "produccion")
            {
                var cambio = await facturacion.AmbienteAsync<ConexionSunat.CambioDelServicio>(
                    HttpMethod.Post, new { ambiente = "produccion", quien = "asistente (paso 6)" },
                    servicio.ClaveAlta, null, ct);

                if (!cambio.Exito)
                    return Results.BadRequest(new
                    {
                        error = "Para emitir en SUNAT real todavía falta algo: " + (cambio.Detalle ?? cambio.Error),
                        codigo = "falta_algo"
                    });

                await db.Database
                    .SqlQuery<int>($"SELECT fijar_ambiente_empresa({empresaId}, {"produccion"}) AS \"Value\"")
                    .FirstAsync(ct);
            }

            var resultado = await db.Database
                .SqlQuery<string>($"SELECT activar_facturacion_propia() AS \"Value\"")
                .FirstAsync(ct);

            return resultado switch
            {
                "ok" or "ya_activo" => Results.Ok(new
                {
                    mensaje = "¡Lista para emitir! Ya tienes Facturación en el menú.",
                    estado = await EstadoAsync(ctx, db, facturacion, ct)
                }),

                "desactivado_por_admin" => Results.Json(new
                {
                    error = "La facturación de esta empresa está desactivada por la administración. Escríbenos."
                }, statusCode: StatusCodes.Status409Conflict),

                _ => Results.Json(new
                {
                    error = "Esta empresa no se configura desde aquí: la activa la administración de la plataforma.",
                    codigo = resultado
                }, statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequierePermiso(Permiso)
        .AddEndpointFilter(ConEmpresa)
        .WithSummary("Paso 6: comprueba que puede emitir y activa Facturación");
    }


    // =========================================================================

    private const string PuntoAlta = "empresa_propia";

    /// <summary>
    /// Los pasos 2 a 6 son de UNA empresa: la de la sesión. El super
    /// administrador tiene todos los permisos pero ninguna empresa, así que
    /// aquí no tiene nada que configurar.
    /// </summary>
    private static async ValueTask<object?> ConEmpresa(
        EndpointFilterInvocationContext contexto, EndpointFilterDelegate siguiente)
    {
        var ctx = contexto.HttpContext.RequestServices.GetRequiredService<ContextoPeticion>();

        return ctx.EmpresaId is null
            ? Results.Json(new { error = "Primero agrega tu empresa (paso 1)." },
                           statusCode: StatusCodes.Status409Conflict)
            : await siguiente(contexto);
    }

    /// <summary>
    /// La empresa de la sesión, lo que dice el servicio de ella y cómo va
    /// cada paso. Si el servicio no contesta, se dice, y el resto sale igual.
    /// </summary>
    private static async Task<object> EstadoAsync(
        ContextoPeticion ctx, ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct)
    {
        var empresaId = ctx.EmpresaId!.Value;

        var empresa = await db.Empresas.AsNoTracking()
            .Where(e => e.Id == empresaId)
            .Select(e => new { e.Id, e.Nombre, e.Ruc, e.RazonSocial, e.Ambiente, e.CorreoTitular })
            .FirstOrDefaultAsync(ct);

        var activa = await db.EmpresaModulos.AsNoTracking()
            .AnyAsync(m => m.EmpresaId == empresaId && m.ModuloCodigo == Core.Dominio.Modulos.Facturacion
                           && m.Activo, ct);

        var r = await facturacion.ObtenerAsync<EndpointsCredenciales.EmisorDelServicio>("v1/emisor", ct);
        var emisor = r.Exito ? r.Datos : null;

        JsonElement? series = null;

        if (emisor is not null)
        {
            var s = await facturacion.ConfigurarAsync<JsonElement>(HttpMethod.Get, "v1/configuracion/series", ct: ct);
            if (s.Exito) series = s.Datos;
        }

        return new
        {
            empresa,
            facturacionActiva = activa,
            servicio = new
            {
                alcanzado = emisor is not null,
                error = emisor is null
                    ? "No pudimos hablar con el servicio de facturación. Lo que guardaste sigue ahí: vuelve en unos minutos."
                    : null
            },
            emisor,
            series,
            pasos = new
            {
                empresa     = empresa is not null,
                certificado = emisor?.Listo?.Certificado == true,
                series      = emisor?.Listo?.Series is { Length: > 0 },
                correo      = emisor?.Portal?.CorreosAlReceptor == true,
                sol         = emisor?.Listo?.Sol == true,
                listo       = emisor?.Listo?.ParaEmitir == true
            }
        };
    }

    private static Task<string> RevisarAsync(ContextoPlataforma db, string? ruc, CancellationToken ct) =>
        db.Database.SqlQuery<string>($"SELECT revisar_empresa_propia({ruc}::text) AS \"Value\"").FirstAsync(ct);

    private static string Motivo(string codigo) => codigo switch
    {
        "super_admin"      => "El super administrador no agrega empresas aquí: usa Administración → Empresas.",
        "ya_tiene_empresa" => "Tu Cuenta Negocio ya tiene su empresa. Para facturar a varias, conviértela en Cuenta " +
                              "Profesional (en Inicio).",
        "sin_permiso"      => "Agregan empresas el dueño de la cuenta y los colaboradores administradores.",
        "sin_cuenta"       => "Tu usuario es de una empresa, no tiene cuenta propia.",
        "cuenta_no_activa" => "Tu cuenta está sin confirmar o suspendida.",
        "tope"             => "Llegaste al tope de empresas nuevas por hoy. Vuelve mañana o escríbenos.",
        "ruc_ocupado"      => "Ese RUC ya está registrado en la plataforma. Si es tuyo, escríbenos: pronto podrás " +
                              "recuperarlo tú mismo.",
        _                  => "No puedes agregar una empresa ahora."
    };

    /// <summary>
    /// Lo que dijo el servicio, para la pantalla. Un «no» suyo (400, 409, 422)
    /// se pasa con su mismo código y su texto, que está escrito para quien
    /// configura. Lo demás es que no se pudo hablar con él.
    /// </summary>
    private static IResult Responder(
        ResultadoFacturacion<JsonElement> r, string? codigo422 = null, string? campo400 = null)
    {
        if (r.Exito)
            return Results.Json(r.Datos, Json);

        return r.Estado switch
        {
            400 => Results.BadRequest(new { error = Unir(r.Error, r.Detalle), codigo = campo400 }),
            409 => Results.Conflict(new { error = Unir(r.Error, r.Detalle) }),
            404 => Results.NotFound(new { error = Unir(r.Error, r.Detalle) }),
            422 => Results.UnprocessableEntity(new { error = Unir(r.Error, r.Detalle), codigo = codigo422 }),

            // 401/403: la clave fac_ de esta empresa ya no vale en el servicio.
            401 or 403 => Results.Json(new
            {
                error = "El servicio de facturación no reconoce la clave de esta empresa. No se guardó nada. " +
                        "Avisa al administrador de la plataforma.",
                codigo = "clave_empresa"
            }, statusCode: StatusCodes.Status503ServiceUnavailable),

            _   => Results.Json(new
            {
                error = "No pudimos hablar con el servicio de facturación. No se guardó nada: inténtalo en unos minutos."
            }, statusCode: StatusCodes.Status502BadGateway)
        };
    }

    /// <summary>El archivo, para reenviarlo al servicio como lo mandó el navegador.</summary>
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

    private static IResult Campo(string campo, string error) =>
        Results.BadRequest(new { error, codigo = "campo_" + campo });

    private static string CampoDeDireccion(string? problema) =>
        problema is null ? "direccion"
        : problema.Contains("ubigeo", StringComparison.OrdinalIgnoreCase) ? "ubigeo"
        : problema.StartsWith("Falta el distrito", StringComparison.Ordinal) ? "distrito"
        : problema.StartsWith("Falta la provincia", StringComparison.Ordinal) ? "provincia"
        : problema.StartsWith("Falta el departamento", StringComparison.Ordinal) ? "departamento"
        : "direccion";

    private static string Limpio(string? s) =>
        string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Unir(string? error, string? detalle) =>
        string.Join(" ", new[] { error, detalle }.Where(t => !string.IsNullOrWhiteSpace(t)));
}