using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Administracion;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Recuperacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Cartera;

public record PeticionPausa(string? Motivo, string? Ruc);

// 046: con la ficha (Mi equipo). Sin ficha, como antes.
public record PeticionColaborador(string? Correo, string? Nombre, string? Nivel, bool VeTodas, Guid[]? Empresas,
                                  PeticionFicha? Ficha = null);

public record CambioColaborador(string? Nivel, bool VeTodas, Guid[]? Empresas);


/// <summary>
/// LA CARTERA (fase 3, migración 018): «Mis empresas», el selector, los
/// colaboradores, «Quitar de mi cartera» y convertir la Cuenta Negocio.
///
///   GET    /api/cartera/empresas                  la bandeja
///   POST   /api/cartera/empresas/{id}/abrir       el selector
///   POST   /api/cartera/empresas/{id}/pausar      «Quitar de mi cartera» (dueño)
///   POST   /api/cartera/empresas/{id}/reanudar    (dueño)
///   GET    /api/cartera/colaboradores
///   POST   /api/cartera/colaboradores             invitar
///   PUT    /api/cartera/colaboradores/{id}        nivel y empresas
///   DELETE /api/cartera/colaboradores/{id}        quitar (dueño; se desactiva)
///   POST   /api/cartera/convertir                 Negocio → Profesional (dueño)
///
/// QUIÉN PUEDE QUÉ LO DECIDE LA BASE: cada ruta llama a UNA función SECURITY
/// DEFINER de la 018 que mira el nivel de quien llama (ctx_usuario) y devuelve
/// un código. Aquí solo se traduce el código a un mensaje. Por eso no llevan
/// RequierePermiso: los permisos son de la empresa abierta, y la cartera está
/// por encima de las empresas.
/// </summary>
public static class EndpointsCartera
{
    private const int HorasInvitacion = 72;

    public static void MapearCartera(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/cartera").WithTags("Cartera");

        // =====================================================================
        grupo.MapGet("/empresas", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await JsonAsync(db, $"""
                SELECT coalesce(json_agg(t), '[]'::json)::text AS "Value"
                  FROM (SELECT id, nombre, ruc, razon_social AS "razonSocial", ambiente, abierta,
                               pausada_en AS "pausadaEn", motivo_pausa AS "motivoPausa",
                               facturacion_activa AS "facturacionActiva",
                               lista_para_emitir AS "listaParaEmitir",
                               certificado_hasta AS "certificadoHasta",
                               rechazos_7_dias AS "rechazos",
                               ultimo_documento AS "ultimoDocumento",
                               la_ven AS "laVen",
                               correo_titular AS "correoTitular"
                          FROM mis_empresas()) t
                """, ct), "application/json"))
        .RequiereSesion()
        .WithSummary("Mis empresas: las de la cuenta que puedo ver, con su estado");


        // =====================================================================
        grupo.MapPost("/empresas/{id:guid}/abrir", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var r = await TextoAsync(db, $"SELECT abrir_empresa({id}::uuid) AS \"Value\"", ct);

            return r switch
            {
                "ok"      => Results.Ok(new { mensaje = "Empresa abierta." }),
                "pausada" => Results.Conflict(new { error = "Esa empresa está en pausa: no se puede abrir. Reanúdala primero." }),
                _         => Results.Json(new { error = "No tienes acceso a esa empresa." },
                                          statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequiereSesion()
        .WithSummary("Cambiar de empresa (el selector)");


        // =====================================================================
        grupo.MapPost("/empresas/{id:guid}/pausar", async (
            Guid id, PeticionPausa p, ContextoPeticion ctx, ContextoPlataforma db,
            IServicioCorreo correo, OpcionesCorreo opciones, IServiceScopeFactory ambitos,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var motivo = (p.Motivo ?? "").Trim();
            if (motivo.Length is < 10 or > 500)
                return Results.BadRequest(new { error = "El motivo va de 10 a 500 caracteres. Lo leerá el titular.", codigo = "campo_motivo" });

            var fila = await TextoAsync(db, $"""
                SELECT json_build_object('r', r.resultado, 'correo', r.correo_titular, 'nombre', r.nombre)::text AS "Value"
                  FROM pausar_empresa({id}::uuid, {motivo}::text, {(p.Ruc ?? "").Trim()}::text) r
                """, ct);

            using var doc = JsonDocument.Parse(fila ?? "{}");
            var raiz = doc.RootElement;
            var resultado = raiz.TryGetProperty("r", out var rr) ? rr.GetString() : null;

            switch (resultado)
            {
                case "ok": break;
                case "ruc":
                    return Results.BadRequest(new { error = "El RUC no coincide con el de la empresa.", codigo = "campo_ruc" });
                case "motivo":
                    return Results.BadRequest(new { error = "El motivo va de 10 a 500 caracteres.", codigo = "campo_motivo" });
                case "ya_pausada":
                    return Results.Conflict(new { error = "Esa empresa ya estaba en pausa." });
                case "no_existe":
                    return Results.NotFound(new { error = "No se encontró esa empresa en tu cartera." });
                default:
                    return Results.Json(new { error = "Solo el dueño de la cuenta quita empresas de la cartera." },
                                        statusCode: StatusCodes.Status403Forbidden);
            }

            // FASE 4c: la clave de la empresa deja de poder emitir en el servicio.
            await PausaEnServicio.AplicarAsync(ambitos, id, true, motivo, logs.CreateLogger("Cartera"), ct);

            // EL AVISO AL TITULAR, después de guardar. Si falla, la pausa vale
            // igual: se dice en la respuesta.
            var aQuien = raiz.GetProperty("correo").ValueKind == JsonValueKind.String ? raiz.GetProperty("correo").GetString() : null;
            var nombre = raiz.GetProperty("nombre").GetString() ?? "tu empresa";
            var avisado = false;

            if (!string.IsNullOrWhiteSpace(aQuien))
            {
                try
                {
                    var quien = await db.Usuarios.AsNoTracking().Where(u => u.Id == ctx.UsuarioId)
                        .Select(u => u.Nombre).FirstOrDefaultAsync(ct) ?? "Quien gestionaba tu empresa";
                    // FASE 4: el correo trae «Seguir yo mismo», un enlace de un
                    // uso (7 días) que solo sirve desde el correo del titular.
                    var (token, hash) = GeneradorTokens.Nuevo();
                    var expira = DateTime.UtcNow.AddHours(EndpointsRecuperacion.HorasEnlace);
                    var conEnlace = await db.Database.SqlQuery<string>($"""
                        SELECT x.correo AS "Value"
                          FROM enlace_tras_pausa({id}::uuid, {hash}, {expira}::timestamptz) x
                        """).FirstOrDefaultAsync(ct);

                    var enlace = conEnlace is null ? null : EndpointsRecuperacion.Enlace(opciones, token);
                    var m = Plantillas.EmpresaEnPausa(nombre, quien, motivo, enlace, EndpointsRecuperacion.HorasEnlace);
                    avisado = await correo.EnviarAsync(aQuien, m.Asunto, m.Html, m.Texto, ct);
                }
                catch (Exception e)
                {
                    logs.CreateLogger("Cartera").LogError(e, "No se pudo avisar al titular de la empresa {Empresa}.", id);
                }
            }

            return Results.Ok(new
            {
                mensaje = avisado
                    ? $"{nombre} quedó en pausa. Le escribimos al titular."
                    : $"{nombre} quedó en pausa. No se pudo escribir al titular (revisa su correo en la configuración).",
                avisado
            });
        })
        .RequiereSesion()
        .WithSummary("«Quitar de mi cartera»: la empresa queda en pausa (no se borra nada)");


        grupo.MapPost("/empresas/{id:guid}/reanudar", async (
            Guid id, ContextoPlataforma db, IServiceScopeFactory ambitos, ILoggerFactory logs, CancellationToken ct) =>
        {
            var r = await TextoAsync(db, $"SELECT reanudar_empresa({id}::uuid) AS \"Value\"", ct);

            // FASE 4c: la clave vuelve a poder emitir en el servicio.
            if (r == "ok") await PausaEnServicio.AplicarAsync(ambitos, id, false, null, logs.CreateLogger("Cartera"), ct);

            return r switch
            {
                "ok"        => Results.Ok(new { mensaje = "La empresa vuelve a tu cartera y puede emitir." }),
                "no_existe" => Results.NotFound(new { error = "No hay una empresa en pausa con ese id en tu cartera." }),
                // 024: con un reclamo con documentos abierto, se espera a que se resuelva.
                "en_revision" => Results.Conflict(new
                {
                    error = "Hay un pedido de traspaso en revisión sobre esta empresa. Podrás reanudarla cuando se resuelva.",
                    codigo = "en_revision"
                }),
                _           => Results.Json(new { error = "Solo el dueño de la cuenta reanuda empresas." },
                                            statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequiereSesion()
        .WithSummary("Reanudar una empresa en pausa");


        // =====================================================================
        // COLABORADORES
        // =====================================================================
        grupo.MapGet("/colaboradores", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await JsonAsync(db, $"""
                SELECT coalesce(json_agg(t), '[]'::json)::text AS "Value"
                  FROM (SELECT id, nombre, correo, alias, nivel, ve_todas AS "veTodas",
                               empresas, activo, pendiente
                          FROM colaboradores()) t
                """, ct), "application/json"))
        .RequiereSesion()
        .WithSummary("Las personas de la cuenta (dueño y colaboradores administradores)");


        grupo.MapPost("/colaboradores", async (
            PeticionColaborador p, ContextoPeticion ctx, ContextoPlataforma db,
            IServicioCorreo correo, OpcionesCorreo opciones, IHostEnvironment entorno,
            CancellationToken ct) =>
        {
            var email = (p.Correo ?? "").Trim();
            if (email.Length is < 3 or > 254 || !email.Contains('@') || email.Any(char.IsWhiteSpace))
                return Results.BadRequest(new { error = "Ese correo no es válido.", codigo = "campo_correo" });

            var nombre = string.Join(' ', (p.Nombre ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            // 046: con la ficha, el nombre sale de ella, y el documento no se
            // repite en el equipo (se mira ANTES de invitar: si no, la
            // invitación quedaría hecha y la ficha no).
            Ficha? ficha = null;
            if (p.Ficha is not null)
            {
                var (f, mal, campo) = await FichaPersonal.RevisarAsync(p.Ficha, db, ct);
                if (f is null) return Results.BadRequest(new { error = mal, codigo = $"campo_{campo}" });
                if (await TextoAsync(db, $"SELECT equipo_documento_de({f.TipoDocumento}, {f.NumeroDocumento}, NULL::uuid) AS \"Value\"", ct) is { } de)
                    return Results.Conflict(new { error = $"Ese {FichaPersonal.NombreDocumento(f.TipoDocumento)} ya es de {de}, de tu equipo.", codigo = "campo_numeroDocumento" });
                ficha = f;
                nombre = f.NombreCompleto;
            }

            if (nombre.Length is < 2 or > 120)
                return Results.BadRequest(new { error = "Escribe su nombre (2 a 120 caracteres).", codigo = "campo_nombre" });

            var empresas = p.Empresas ?? [];
            if (!p.VeTodas && empresas.Length == 0)
                return Results.BadRequest(new { error = "Elige al menos una empresa, o «Todas».", codigo = "campo_empresas" });

            var (token, hash) = GeneradorTokens.Nuevo();
            var expira = DateTime.UtcNow.AddHours(HorasInvitacion);

            var fila = await TextoAsync(db, $"""
                SELECT r.resultado || ':' || coalesce(r.usuario_id::text, '') AS "Value"
                  FROM invitar_colaborador({email}::text, {nombre}::text, {p.Nivel}::text, {p.VeTodas},
                                           {empresas}::uuid[], {hash}, {expira}::timestamptz) r
                """, ct) ?? "error:";

            var partes = fila.Split(':', 2);

            switch (partes[0])
            {
                case "ok": break;
                case "correo_ocupado":
                    return Results.Conflict(new { error = $"{email} ya tiene una cuenta en la plataforma.", codigo = "campo_correo" });
                case "nivel":
                    return Results.BadRequest(new { error = "Solo el dueño invita colaboradores administradores." });
                case "empresa_ajena":
                    return Results.BadRequest(new { error = "Alguna de esas empresas no es de tu cuenta." });
                case "cuenta_no_activa":
                    return Results.Conflict(new { error = "Tu cuenta no está activa." });
                default:
                    return Results.Json(new { error = "Invitan el dueño y los colaboradores administradores de una Cuenta Profesional." },
                                        statusCode: StatusCodes.Status403Forbidden);
            }

            if (ficha is not null)
                await TextoAsync(db, $"SELECT equipo_guardar_ficha({Guid.Parse(partes[1])}::uuid, {ficha.ComoJson()}::jsonb) AS \"Value\"", ct);

            // El correo, con el nombre del titular de la cuenta como «empresa».
            var datos = await JsonAsync(db, $"""
                SELECT json_build_object('quien', yo.nombre, 'cuenta', coalesce(c.titular_nombre, 'la cuenta'))::text AS "Value"
                  FROM usuarios yo JOIN cuentas c ON c.id = yo.cuenta_id
                 WHERE yo.id = {ctx.UsuarioId}
                """, ct);
            using var d = JsonDocument.Parse(datos);
            var quien = d.RootElement.GetProperty("quien").GetString() ?? "Tu equipo";
            var cuenta = d.RootElement.GetProperty("cuenta").GetString() ?? "la cuenta";

            var enlace = $"{opciones.UrlBase.TrimEnd('/')}/invitacion/{token}";
            var m = Plantillas.Invitacion(nombre, $"el equipo de {cuenta}", enlace, HorasInvitacion, quien);
            var enviado = await correo.EnviarAsync(email, m.Asunto, m.Html, m.Texto, ct);

            return Results.Ok(new
            {
                id = Guid.Parse(partes[1]),
                enviado,
                mensaje = enviado
                    ? $"Invitación enviada a {email}. El enlace vale {HorasInvitacion / 24} días."
                    : $"{nombre} quedó creado, pero el correo no salió.",
                enlaceDePrueba = entorno.IsDevelopment() && !opciones.Habilitado ? enlace : null
            });
        })
        .RequiereSesion()
        .WithSummary("Invitar un colaborador");


        grupo.MapPut("/colaboradores/{id:guid}", async (
            Guid id, CambioColaborador p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var empresas = p.Empresas ?? [];
            if (!p.VeTodas && empresas.Length == 0)
                return Results.BadRequest(new { error = "Elige al menos una empresa, o «Todas»." });

            var r = await TextoAsync(db, $"""
                SELECT editar_colaborador({id}::uuid, {p.Nivel}::text, {p.VeTodas}, {empresas}::uuid[]) AS "Value"
                """, ct);

            return r switch
            {
                "ok"            => Results.Ok(new { mensaje = "Cambios guardados." }),
                "no_existe"     => Results.NotFound(new { error = "Esa persona no es de tu cuenta." }),
                "nivel"         => Results.BadRequest(new { error = "Ese cambio de nivel solo lo hace el dueño." }),
                "empresa_ajena" => Results.BadRequest(new { error = "Alguna de esas empresas no es de tu cuenta." }),
                _               => Results.Json(new { error = "No puedes cambiar a esta persona." },
                                                statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequiereSesion()
        .WithSummary("Cambiar el nivel y las empresas de un colaborador");


        grupo.MapDelete("/colaboradores/{id:guid}", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var r = await TextoAsync(db, $"SELECT quitar_colaborador({id}::uuid) AS \"Value\"", ct);

            return r switch
            {
                "ok"        => Results.Ok(new { mensaje = "Colaborador quitado. Sus sesiones se cerraron; nada se borró." }),
                "no_existe" => Results.NotFound(new { error = "Esa persona no es colaboradora de tu cuenta." }),
                _           => Results.Json(new { error = "Solo el dueño quita colaboradores." },
                                            statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequiereSesion()
        .WithSummary("Quitar un colaborador (se desactiva)");


        // =====================================================================
        // 046 — MI EQUIPO: la ficha y el acceso por empresa (rol, alcance y
        // sucursales). Quién puede lo decide la base (equipo_puede), como el
        // resto de la cartera.
        // =====================================================================
        grupo.MapGet("/equipo", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await JsonAsync(db, $"SELECT equipo() AS \"Value\"", ct), "application/json"))
        .RequiereSesion()
        .WithSummary("Mi equipo: las personas del estudio con su ficha y sus accesos");

        grupo.MapGet("/equipo/empresas", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await JsonAsync(db, $"SELECT equipo_empresas() AS \"Value\"", ct), "application/json"))
        .RequiereSesion()
        .WithSummary("Las empresas de la cuenta con sus sucursales y los roles que se pueden dar");

        grupo.MapPut("/equipo/{id:guid}/ficha", async (
            Guid id, PeticionFicha p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var (f, mal, campo) = await FichaPersonal.RevisarAsync(p, db, ct);
            if (f is null) return Results.BadRequest(new { error = mal, campo });

            var r = await TextoAsync(db, $"SELECT equipo_guardar_ficha({id}::uuid, {f.ComoJson()}::jsonb) AS \"Value\"", ct);
            return r switch
            {
                "ok"                 => Results.Ok(new { mensaje = "Ficha guardada." }),
                "documento_repetido" => Results.Conflict(new { error = $"Ese {FichaPersonal.NombreDocumento(f.TipoDocumento)} ya es de otra persona de tu equipo.", campo = "numeroDocumento" }),
                "no_existe"          => Results.NotFound(new { error = "Esa persona no es de tu equipo." }),
                _                    => Results.Json(new { error = "No puedes cambiar la ficha de esta persona." },
                                                     statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequiereSesion()
        .WithSummary("Guardar la ficha de una persona del equipo");

        grupo.MapPut("/equipo/{id:guid}/accesos/{empresaId:guid}", async (
            Guid id, Guid empresaId, PeticionAcceso p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var sucursales = (p.Sucursales ?? []).Distinct().ToArray();
            var r = await TextoAsync(db, $"""
                SELECT equipo_guardar_acceso({id}::uuid, {empresaId}::uuid, {p.Rol}::uuid, {p.Alcance}::text,
                                             {sucursales}::uuid[], {p.SucursalDefecto}::uuid, {p.Notas}) AS "Value"
                """, ct);
            return r switch
            {
                "ok"            => Results.Ok(new { mensaje = "Acceso guardado." }),
                "no_existe"     => Results.NotFound(new { error = "Esa persona no es de tu equipo." }),
                "nivel"         => Results.BadRequest(new { error = "El dueño y los colaboradores administradores son administradores en todas las empresas: no se les da un acceso aparte." }),
                "empresa_ajena" => Results.BadRequest(new { error = "Esa empresa no es de tu cuenta." }),
                "rol"           => Results.BadRequest(new { error = "Elige un rol para esa empresa (el Administrador es el nivel «Colaborador administrador»).", campo = "rol" }),
                "alcance"       => Results.BadRequest(new { error = "Elige qué puede ver.", campo = "alcance" }),
                "sucursal"      => Results.BadRequest(new { error = "Revisa sus sucursales: marca al menos una de esa empresa, y la de por defecto entre ellas.", campo = "sucursales" }),
                _               => Results.Json(new { error = "No puedes cambiar el acceso de esta persona." },
                                                statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequiereSesion()
        .WithSummary("Dar o cambiar el acceso de un colaborador a una empresa");

        grupo.MapDelete("/equipo/{id:guid}/accesos/{empresaId:guid}", async (
            Guid id, Guid empresaId, ContextoPlataforma db, CancellationToken ct) =>
        {
            var r = await TextoAsync(db, $"SELECT equipo_quitar_acceso({id}::uuid, {empresaId}::uuid) AS \"Value\"", ct);
            return r switch
            {
                "ok"        => Results.Ok(new { mensaje = "Ya no entra a esa empresa. No se borró nada." }),
                "ve_todas"  => Results.Conflict(new { error = "Ve «Todas» las empresas: quita primero «Todas» y elige las que sí." }),
                "nivel"     => Results.BadRequest(new { error = "El dueño y los colaboradores administradores entran a todas." }),
                "no_existe" => Results.NotFound(new { error = "Esa persona no es de tu equipo." }),
                _           => Results.Json(new { error = "No puedes cambiar el acceso de esta persona." },
                                            statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequiereSesion()
        .WithSummary("Quitarle a un colaborador una empresa");


        // =====================================================================
        grupo.MapPost("/convertir", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var r = await TextoAsync(db, $"SELECT convertir_a_profesional() AS \"Value\"", ct);

            return r switch
            {
                "ok"    => Results.Ok(new { mensaje = "Ya tienes Cuenta Profesional: Mis empresas y Colaboradores están en el menú." }),
                "ya_es" => Results.Ok(new { mensaje = "Tu cuenta ya era Profesional." }),
                _       => Results.Json(new { error = "Solo el dueño de la cuenta puede convertirla." },
                                        statusCode: StatusCodes.Status403Forbidden)
            };
        })
        .RequiereSesion()
        .WithSummary("Convertir la Cuenta Negocio en Profesional");
    }


    private static Task<string?> TextoAsync(ContextoPlataforma db, FormattableString sql, CancellationToken ct) =>
        db.Database.SqlQuery<string>(sql).FirstOrDefaultAsync(ct);

    private static async Task<string> JsonAsync(ContextoPlataforma db, FormattableString sql, CancellationToken ct) =>
        await db.Database.SqlQuery<string>(sql).FirstOrDefaultAsync(ct) ?? "[]";
}