using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Administracion;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Recuperacion;
using Plataforma.Api.Registro;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Traspasos;

public record PeticionRechazo(string? Motivo);


/// <summary>
/// RECLAMAR LA EMPRESA CON DOCUMENTOS (fase 4b, camino C; migración 022).
///
///   POST /api/traspasos                                  el reclamo, con sus archivos (sin sesión)
///   GET  /api/admin/traspasos                            la lista (super administrador)
///   GET  /api/admin/traspasos/documentos/{id}            un documento, descifrado
///   POST /api/admin/traspasos/{id}/aprobar
///   POST /api/admin/traspasos/{id}/rechazar              con motivo
///
/// FASE 4d (024): la cuenta que tiene la empresa ve el reclamo y responde.
///   GET  /api/traspasos/mios                             reclamos sobre mis empresas
///   POST /api/traspasos/{id}/responder                   de acuerdo / se opone (+ un documento)
///
/// LOS ARCHIVOS SE GUARDAN CIFRADOS con la llave maestra, igual que las
/// credenciales: un respaldo de la base no deja ver el DNI de nadie.
///
/// APROBAR reutiliza la fase 4a: la empresa queda en pausa, su correo del
/// titular pasa a ser el de quien reclamó, y a ese correo le llega el enlace
/// de «Recupera tu empresa».
/// </summary>
public static class EndpointsTraspasos
{
    private const int TopeArchivo = 5 * 1024 * 1024;

    private static readonly string[] Tipos = ["ficha_ruc", "dni", "vigencia_poder", "certificado"];

    private static readonly Dictionary<string, string> Relaciones = new()
    {
        ["titular"] = "Titular (persona natural con negocio)",
        ["representante"] = "Gerente general / representante legal",
        ["apoderado"] = "Apoderado",
    };

    public static void MapearTraspasos(this IEndpointRouteBuilder rutas)
    {
        // =====================================================================
        // EL RECLAMO (sin sesión)
        // =====================================================================
        rutas.MapPost("/api/traspasos", async (
            HttpRequest peticion, HttpContext http, ContextoPlataforma db, Captcha captcha, Cifrador cifrador,
            IServicioCorreo correo, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Traspasos");

            if (!peticion.HasFormContentType)
                return Results.BadRequest(new { error = "Falta el formulario." });

            var f = await peticion.ReadFormAsync(ct);
            string Campo(string n) => string.Join(' ', f[n].ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            var ruc = new string(Campo("ruc").Where(char.IsDigit).ToArray());
            var nombre = Campo("nombre");
            var dni = new string(Campo("dni").Where(char.IsDigit).ToArray());
            var relacion = Campo("relacion").ToLowerInvariant();
            var correoNuevo = Campo("correo");
            var telefono = Campo("telefono");
            var explicacion = f["explicacion"].ToString().Trim();

            IResult Mal(string error, string campo) => Results.BadRequest(new { error, codigo = $"campo_{campo}" });

            if (ReglasEmisor.ProblemaRuc(ruc) is { } problemaRuc) return Mal(problemaRuc, "ruc");
            if (nombre.Length is < 5 or > 120) return Mal("Escribe tu nombre completo.", "nombre");
            if (dni.Length != 8) return Mal("El DNI tiene 8 dígitos.", "dni");
            if (!Relaciones.ContainsKey(relacion)) return Mal("Elige tu relación con la empresa.", "relacion");
            if (!CorreoValido(correoNuevo)) return Mal("Ese correo no parece válido.", "correo");
            if (telefono.Length > 30) return Mal("El teléfono es demasiado largo.", "telefono");
            if (explicacion.Length is < 10 or > 1000) return Mal("Cuéntanos qué pasó (10 a 1000 caracteres).", "explicacion");
            if (f["aceptaTerminos"].ToString() != "true")
                return Mal("Para seguir tienes que aceptar los términos de uso y la política de privacidad.", "aceptaTerminos");

            // Los archivos: la ficha RUC y el DNI siempre; la vigencia de poder,
            // si es empresa (RUC 20); el certificado, si lo tiene.
            var archivos = new List<(string Tipo, IFormFile Archivo)>();
            foreach (var tipo in Tipos)
            {
                var a = f.Files.GetFile(tipo);
                if (a is null || a.Length == 0) continue;
                if (a.Length > TopeArchivo) return Mal($"«{a.FileName}» pesa más de 5 MB.", tipo);
                if (!TipoPermitido(tipo, a)) return Mal($"«{a.FileName}»: sube un PDF o una foto (JPG o PNG).", tipo);
                archivos.Add((tipo, a));
            }
            if (!archivos.Any(x => x.Tipo == "ficha_ruc")) return Mal("Sube tu ficha RUC.", "ficha_ruc");
            if (!archivos.Any(x => x.Tipo == "dni")) return Mal("Sube tu DNI.", "dni_archivo");
            if (ruc.StartsWith("20") && !archivos.Any(x => x.Tipo == "vigencia_poder"))
                return Mal("Una empresa (RUC 20) necesita la vigencia de poder.", "vigencia_poder");

            var ip = http.Connection.RemoteIpAddress?.ToString();
            if (captcha.Configurado && !await captcha.VerificarAsync(f["captcha"].ToString(), ip, ct))
                return Mal("No pudimos comprobar que no eres un robot. Vuelve a intentarlo.", "captcha");

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('r', x.resultado, 'id', x.solicitud_id, 'numero', x.numero,
                         'empresa', x.empresa, 'plazo', x.plazo_hasta,
                         'avisoCorreo', x.aviso_correo, 'avisoNombre', x.aviso_nombre)::text AS "Value"
                  FROM crear_solicitud_traspaso({ruc}::text, {nombre}::text, {dni}::text, {relacion}::text,
                                                {correoNuevo}::text, {telefono}::text, {explicacion}::text, {ip}::inet) x
                """).FirstOrDefaultAsync(ct);

            using var doc = JsonDocument.Parse(fila ?? "{}");
            var r = doc.RootElement;

            switch (Texto(r, "r"))
            {
                case "ok": break;
                case "sin_empresa":
                    return Mal("Ese RUC no está en la plataforma. Si es tu empresa, regístrate y agrégala tú mismo.", "ruc");
                case "ya_hay":
                    return Results.Conflict(new { error = "Ya hay una solicitud abierta para esa empresa. Te escribiremos al revisarla." });
                case "demasiados":
                    return Results.Json(new { error = "Demasiadas solicitudes desde tu conexión. Prueba en una hora." },
                                        statusCode: StatusCodes.Status429TooManyRequests);
                default:
                    return Results.Problem("No se pudo guardar la solicitud.");
            }

            var solicitudId = r.GetProperty("id").GetGuid();
            var numero = r.GetProperty("numero").GetInt32();
            var empresa = Texto(r, "empresa") ?? "";
            var plazo = r.GetProperty("plazo").GetDateTime();

            foreach (var (tipo, a) in archivos)
            {
                using var ms = new MemoryStream();
                await a.CopyToAsync(ms, ct);
                var cifrado = cifrador.Cifrar(Convert.ToBase64String(ms.ToArray()));

                await db.Database.SqlQuery<string>($"""
                    SELECT agregar_documento_solicitud({solicitudId}, {tipo}::text, {Path.GetFileName(a.FileName)}::text,
                           {a.ContentType ?? "application/octet-stream"}::text, {(int)a.Length}, {cifrado}) AS "Value"
                    """).FirstOrDefaultAsync(ct);
            }

            // --- Los avisos ---------------------------------------------------
            var plazoTexto = plazo.ToLocalTime().ToString("d 'de' MMMM", new System.Globalization.CultureInfo("es-PE"));
            await EnviarAsync(correo, log, correoNuevo, Plantillas.ReclamoRecibido(nombre, empresa, numero));

            if (Texto(r, "avisoCorreo") is { Length: > 0 } aviso)
                await EnviarAsync(correo, log, aviso,
                    Plantillas.ReclamoAvisoCuenta(Texto(r, "avisoNombre") ?? "", empresa, nombre, Relaciones[relacion], plazoTexto));

            var supers = await db.Database
                .SqlQuery<string>($"SELECT correo AS \"Value\" FROM correos_super_admin()").ToListAsync(ct);
            foreach (var s in supers)
                await EnviarAsync(correo, log, s, Plantillas.ReclamoAvisoSuper(empresa, ruc, numero));

            return Results.Ok(new
            {
                numero,
                mensaje = $"Recibimos tu solicitud N.º {numero:0000}. Te respondemos en 3 días hábiles a {correoNuevo}."
            });
        })
        .DisableAntiforgery()
        .WithTags("Traspasos")
        .WithSummary("Reclamar una empresa con documentos (camino C)");


        // =====================================================================
        // LA CUENTA QUE TIENE LA EMPRESA: VE EL RECLAMO Y RESPONDE (fase 4d)
        // =====================================================================
        rutas.MapGet("/api/traspasos/mios", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"""
                SELECT coalesce(json_agg(t), '[]'::json)::text AS "Value"
                  FROM (SELECT id, numero, empresa_id AS "empresaId", empresa, ruc, pide, relacion,
                               plazo_hasta AS "plazoHasta", respuesta, puedo_responder AS "puedoResponder"
                          FROM reclamos_sobre_mis_empresas()) t
                """).FirstOrDefaultAsync(ct) ?? "[]", "application/json"))
        .RequiereSesion()
        .WithTags("Traspasos")
        .WithSummary("Los reclamos abiertos sobre las empresas que veo («traspaso en revisión»)");


        rutas.MapPost("/api/traspasos/{id:guid}/responder", async (
            Guid id, HttpRequest peticion, ContextoPlataforma db, Cifrador cifrador,
            IServicioCorreo correo, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Traspasos");

            if (!peticion.HasFormContentType)
                return Results.BadRequest(new { error = "Falta el formulario." });

            var f = await peticion.ReadFormAsync(ct);
            var respuesta = f["respuesta"].ToString().Trim();
            var texto = f["texto"].ToString().Trim();

            IResult Mal(string error, string campo) => Results.BadRequest(new { error, codigo = $"campo_{campo}" });

            if (respuesta is not ("de_acuerdo" or "se_opone")) return Mal("Elige tu respuesta.", "respuesta");
            if (respuesta == "se_opone" && texto.Length is < 10 or > 1000)
                return Mal("Cuéntanos por qué (10 a 1000 caracteres).", "texto");
            if (texto.Length > 1000) return Mal("El texto va hasta 1000 caracteres.", "texto");

            var archivo = f.Files.GetFile("archivo");
            if (archivo is { Length: > 0 })
            {
                if (archivo.Length > TopeArchivo) return Mal($"«{archivo.FileName}» pesa más de 5 MB.", "archivo");
                if (!TipoPermitido("respuesta", archivo)) return Mal($"«{archivo.FileName}»: sube un PDF o una foto (JPG o PNG).", "archivo");
            }
            else archivo = null;

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('r', x.resultado, 'numero', x.numero, 'empresa', x.empresa)::text AS "Value"
                  FROM responder_reclamo({id}, {respuesta}::text, {texto}::text) x
                """).FirstOrDefaultAsync(ct);

            using var doc = JsonDocument.Parse(fila ?? "{}");
            var r = doc.RootElement;

            switch (Texto(r, "r"))
            {
                case "ok": break;
                case "ya_respondida": return Results.Conflict(new { error = "Ese reclamo ya tiene tu respuesta. Solo se responde una vez." });
                case "sin_permiso":
                    return Results.Json(new { error = "Solo el dueño o un colaborador administrador responden." }, statusCode: 403);
                case "texto": return Mal("Cuéntanos por qué (10 a 1000 caracteres).", "texto");
                case "respuesta": return Mal("Elige tu respuesta.", "respuesta");
                default: return Results.NotFound(new { error = "No hay un reclamo abierto con ese número sobre tus empresas." });
            }

            if (archivo is not null)
            {
                using var ms = new MemoryStream();
                await archivo.CopyToAsync(ms, ct);
                var cifrado = cifrador.Cifrar(Convert.ToBase64String(ms.ToArray()));

                await db.Database.SqlQuery<string>($"""
                    SELECT agregar_documento_respuesta({id}, {Path.GetFileName(archivo.FileName)}::text,
                           {archivo.ContentType ?? "application/octet-stream"}::text, {(int)archivo.Length}, {cifrado}) AS "Value"
                    """).FirstOrDefaultAsync(ct);
            }

            var numero = r.TryGetProperty("numero", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
            var empresa = Texto(r, "empresa") ?? "";

            var supers = await db.Database
                .SqlQuery<string>($"SELECT correo AS \"Value\" FROM correos_super_admin()").ToListAsync(ct);
            foreach (var s in supers)
                await EnviarAsync(correo, log, s, Plantillas.ReclamoRespondido(empresa, numero, respuesta == "de_acuerdo"));

            return Results.Ok(new { mensaje = "Enviamos tu respuesta. La lee el equipo de Reyban al revisar la solicitud." });
        })
        .RequiereSesion()
        .DisableAntiforgery()
        .WithTags("Traspasos")
        .WithSummary("Responder un reclamo sobre una empresa mía (una vez)");


        // =====================================================================
        // EL SUPER ADMINISTRADOR
        // =====================================================================
        var admin = rutas.MapGroup("/api/admin/traspasos").WithTags("Traspasos");

        admin.MapGet("/", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"""
                SELECT coalesce(json_agg(t), '[]'::json)::text AS "Value"
                  FROM (SELECT id, numero, estado, empresa_id AS "empresaId", empresa, ruc, nombre, dni, relacion,
                               correo_nuevo AS "correoNuevo", telefono, explicacion,
                               cuenta_anterior AS "cuentaAnterior", pausada, plazo_hasta AS "plazoHasta",
                               creado_en AS "creadoEn", motivo_rechazo AS "motivoRechazo",
                               revisada_en AS "revisadaEn", documentos,
                               respuesta, respuesta_texto AS "respuestaTexto", respuesta_en AS "respuestaEn",
                               respuesta_quien AS "respuestaQuien"
                          FROM solicitudes_traspaso_lista()) t
                """).FirstOrDefaultAsync(ct) ?? "[]", "application/json"))
        .SoloSuperAdmin()
        .WithSummary("Las solicitudes de traspaso");


        admin.MapGet("/documentos/{id:guid}", async (Guid id, ContextoPlataforma db, Cifrador cifrador, CancellationToken ct) =>
        {
            // Las columnas en minúsculas y con guion bajo: EF las busca así
            // (nombre, tipo_mime, contenido), por la convención snake_case.
            var d = await db.Database.SqlQuery<DocumentoGuardado>($"""
                SELECT nombre, tipo_mime, contenido_cifrado AS contenido
                  FROM documento_solicitud({id})
                """).FirstOrDefaultAsync(ct);

            if (d is null) return Results.NotFound(new { error = "No existe ese documento." });

            var bytes = Convert.FromBase64String(cifrador.Descifrar(d.Contenido));

            // Siempre se BAJA, con el nombre con que se subió. El front lo pide
            // con fetch (api.descargar): si algo falla, el error sale en una
            // ventana de la app y no en una pestaña rota del navegador.
            return Results.File(bytes, d.TipoMime, d.Nombre);
        })
        .SoloSuperAdmin()
        .WithSummary("Ver un documento de una solicitud (descifrado)");


        admin.MapPost("/{id:guid}/aprobar", async (
            Guid id, ContextoPlataforma db, IServicioCorreo correo, OpcionesCorreo opciones,
            IHostEnvironment entorno, IServiceScopeFactory ambitos, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Traspasos");
            var empresaId = await db.Database.SqlQuery<Guid?>(
                $"SELECT x.empresa_id AS \"Value\" FROM solicitudes_traspaso_lista() x WHERE x.id = {id}").FirstOrDefaultAsync(ct);
            var (token, hash) = GeneradorTokens.Nuevo();
            var expira = DateTime.UtcNow.AddHours(EndpointsRecuperacion.HorasEnlace);

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('r', x.resultado, 'correo', x.correo, 'nombre', x.nombre,
                         'empresa', x.empresa, 'ruc', x.ruc,
                         'avisoCorreo', x.aviso_correo, 'avisoNombre', x.aviso_nombre)::text AS "Value"
                  FROM aprobar_traspaso({id}, {hash}, {expira}::timestamptz) x
                """).FirstOrDefaultAsync(ct);

            using var doc = JsonDocument.Parse(fila ?? "{}");
            var r = doc.RootElement;

            switch (Texto(r, "r"))
            {
                case "ok": break;
                case "ya_revisada": return Results.Conflict(new { error = "Esa solicitud ya se revisó." });
                case "no_existe": return Results.NotFound(new { error = "No existe esa solicitud." });
                default: return Results.Json(new { error = "Solo el super administrador aprueba traspasos." }, statusCode: 403);
            }

            var empresa = Texto(r, "empresa") ?? "";
            var enlace = EndpointsRecuperacion.Enlace(opciones, token);

            // FASE 4c: mientras el titular no la recupere, nadie emite a su nombre.
            if (empresaId is { } e) await PausaEnServicio.AplicarAsync(ambitos, e, true, "Traspaso aprobado a su titular.", log, ct);
            var destino = Texto(r, "correo") ?? "";

            await EnviarAsync(correo, log, destino,
                Plantillas.ReclamoAprobado(Texto(r, "nombre") ?? "", empresa, Texto(r, "ruc") ?? "", enlace,
                                           EndpointsRecuperacion.HorasEnlace));

            if (Texto(r, "avisoCorreo") is { Length: > 0 } aviso)
                await EnviarAsync(correo, log, aviso, Plantillas.EmpresaTraspasada(Texto(r, "avisoNombre") ?? "", empresa));

            return Results.Ok(new
            {
                mensaje = $"Traspaso aprobado. Le enviamos el enlace a {destino}.",
                enlaceDePrueba = entorno.IsDevelopment() && !opciones.Habilitado ? enlace : null
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Aprobar un traspaso: pausa, correo nuevo del titular y enlace de recuperación");


        admin.MapPost("/{id:guid}/rechazar", async (
            Guid id, PeticionRechazo p, ContextoPlataforma db, IServicioCorreo correo,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var motivo = (p.Motivo ?? "").Trim();
            if (motivo.Length is < 10 or > 500)
                return Results.BadRequest(new { error = "El motivo va de 10 a 500 caracteres: le llega a quien lo pidió.", codigo = "campo_motivo" });

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('r', x.resultado, 'correo', x.correo, 'nombre', x.nombre, 'empresa', x.empresa)::text AS "Value"
                  FROM rechazar_traspaso({id}, {motivo}::text) x
                """).FirstOrDefaultAsync(ct);

            using var doc = JsonDocument.Parse(fila ?? "{}");
            var r = doc.RootElement;

            switch (Texto(r, "r"))
            {
                case "ok": break;
                case "ya_revisada": return Results.Conflict(new { error = "Esa solicitud ya se revisó." });
                case "no_existe": return Results.NotFound(new { error = "No existe esa solicitud." });
                default: return Results.Json(new { error = "Solo el super administrador rechaza traspasos." }, statusCode: 403);
            }

            await EnviarAsync(correo, logs.CreateLogger("Traspasos"), Texto(r, "correo") ?? "",
                Plantillas.ReclamoRechazado(Texto(r, "nombre") ?? "", Texto(r, "empresa") ?? "", motivo));

            return Results.Ok(new { mensaje = "Solicitud rechazada. Le escribimos con el motivo." });
        })
        .SoloSuperAdmin()
        .WithSummary("Rechazar un traspaso, con motivo");
    }


    // =========================================================================

    private sealed record DocumentoGuardado(string Nombre, string TipoMime, byte[] Contenido);

    private static bool TipoPermitido(string tipo, IFormFile a)
    {
        var ext = Path.GetExtension(a.FileName).ToLowerInvariant();
        if (tipo == "certificado") return ext is ".pfx" or ".p12" or ".pdf";
        // «respuesta» (fase 4d): lo mismo que los demás documentos.
        return ext is ".pdf" or ".jpg" or ".jpeg" or ".png";
    }

    private static bool CorreoValido(string correo)
    {
        if (correo.Length is < 6 or > 200 || correo.Contains(' ')) return false;
        try
        {
            var m = new MailAddress(correo);
            return m.Address == correo && correo.Split('@')[1].Contains('.');
        }
        catch (FormatException) { return false; }
    }

    private static async Task EnviarAsync(IServicioCorreo correo, ILogger log, string destino, CorreoCompuesto m)
    {
        if (string.IsNullOrWhiteSpace(destino)) return;
        try { await correo.EnviarAsync(destino, m.Asunto, m.Html, m.Texto); }
        catch (Exception e) { log.LogWarning(e, "No salió un correo de traspaso a {Destino}.", destino); }
    }

    private static string? Texto(JsonElement e, string nombre) =>
        e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}