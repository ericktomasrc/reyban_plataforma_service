using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Traspasos;

public record PeticionPedirEmpresa(string? Ruc, string? Mensaje);

public record PeticionResponderPedido(bool Aprobar, bool Seguir, string? Nombre);


/// <summary>
/// CAMINO B (fase 4d, migración 024). Lienzo «Fase 4 — Traspasos», pantallas 9 a 12.
///
///   GET  /api/pedidos/revisar?ruc=          ¿se puede pedir esta empresa? (el asistente)
///   POST /api/pedidos                       pedirla: le escribimos al titular
///   GET  /api/pedidos                       los pedidos de mi cuenta (Mis empresas)
///   POST /api/pedidos/{id}/cancelar
///   POST /api/pedidos/{id}/ocultar          «Quitar de la lista»
///   GET  /api/pedido/{token}                la página del titular (sin sesión)
///   POST /api/pedido/{token}/responder      aprobar o «No, gracias» (sin sesión)
///
/// EL CORREO VA SOLO AL TITULAR, el que se guardó al dar de alta la empresa.
/// La pantalla de quien pide solo ve el correo tapado (j•••@dominio). Aprobar
/// y rechazar están en la página, no en el correo: un filtro que abre enlaces
/// no puede mover una empresa.
///
/// Todo lo decide la base (024); aquí se generan los tokens, se escriben los
/// correos y se traducen los códigos.
/// </summary>
public static class EndpointsCaminoB
{
    /// <summary>Lo que vale el enlace al titular: 7 días.</summary>
    public const int HorasEnlace = 168;

    /// <summary>Lo que vale el enlace para elegir contraseña, si se le crea usuario.</summary>
    private const int HorasInvitacion = 24;

    public static void MapearCaminoB(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/pedidos").WithTags("Camino B");


        // =====================================================================
        // ¿SE PUEDE PEDIR? (el asistente, al escribir un RUC que ya existe)
        // =====================================================================
        grupo.MapGet("/revisar", async (string? ruc, ContextoPlataforma db, CancellationToken ct) =>
        {
            var limpio = new string((ruc ?? "").Where(char.IsDigit).ToArray());
            if (limpio.Length != 11)
                return Results.BadRequest(new { error = "El RUC tiene 11 dígitos.", codigo = "campo_ruc" });

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('resultado', x.resultado, 'empresa', x.empresa, 'correo', x.correo)::text AS "Value"
                  FROM revisar_pedido_empresa({limpio}::text) x
                """).FirstOrDefaultAsync(ct);

            using var doc = JsonDocument.Parse(fila ?? """{"resultado":"libre"}""");
            var r = Texto(doc.RootElement, "resultado") ?? "libre";

            return Results.Ok(new
            {
                resultado = r,
                empresa = Texto(doc.RootElement, "empresa"),
                correo = Texto(doc.RootElement, "correo"),
                mensaje = MotivoRevision(r)
            });
        })
        .RequiereSesion()
        .WithSummary("¿Se puede pedir al titular una empresa que ya está en Reyban?");


        // =====================================================================
        // PEDIRLA
        // =====================================================================
        grupo.MapPost("/", async (
            PeticionPedirEmpresa p, ContextoPlataforma db, IServicioCorreo correo, OpcionesCorreo opciones,
            IHostEnvironment entorno, ILoggerFactory logs, CancellationToken ct) =>
        {
            var ruc = new string((p.Ruc ?? "").Where(char.IsDigit).ToArray());
            if (ruc.Length != 11)
                return Results.BadRequest(new { error = "El RUC tiene 11 dígitos.", codigo = "campo_ruc" });

            var mensaje = (p.Mensaje ?? "").Trim();
            if (mensaje.Length > 500)
                return Results.BadRequest(new { error = "El mensaje va hasta 500 caracteres.", codigo = "campo_mensaje" });

            var (token, hash) = GeneradorTokens.Nuevo();
            var expira = DateTime.UtcNow.AddHours(HorasEnlace);

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('r', x.resultado, 'correo', x.correo, 'empresa', x.empresa, 'ruc', x.ruc,
                         'quien', x.quien, 'cuenta', x.cuenta, 'tapado', x.correo_tapado)::text AS "Value"
                  FROM pedir_empresa({ruc}::text, {mensaje}::text, {hash}, {expira}::timestamptz) x
                """).FirstOrDefaultAsync(ct);

            using var doc = JsonDocument.Parse(fila ?? "{}");
            var raiz = doc.RootElement;
            var r = Texto(raiz, "r") ?? "";

            if (r != "ok")
                return Results.Json(new { error = MotivoRevision(r), codigo = r },
                                    statusCode: r == "sin_permiso" ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict);

            var empresa = Texto(raiz, "empresa") ?? "";
            var enlace = Enlace(opciones, token);

            try
            {
                var m = Plantillas.PedidoAlTitular(Texto(raiz, "quien") ?? "", Texto(raiz, "cuenta") ?? "",
                                                   empresa, Texto(raiz, "ruc") ?? "", mensaje, enlace, HorasEnlace);
                await correo.EnviarAsync(Texto(raiz, "correo") ?? "", m.Asunto, m.Html, m.Texto, ct);
            }
            catch (Exception e)
            {
                logs.CreateLogger("CaminoB").LogError(e, "No salió el pedido al titular de {Ruc}.", ruc);
            }

            return Results.Ok(new
            {
                mensaje = $"Le pedimos a su titular {empresa} ({Texto(raiz, "tapado")}). Te avisamos por correo cuando responda.",
                enlaceDePrueba = entorno.IsDevelopment() && !opciones.Habilitado ? enlace : null
            });
        })
        .RequiereSesion()
        .WithSummary("Pedir al titular llevar su empresa (camino B)");


        // =====================================================================
        // LOS PEDIDOS DE MI CUENTA · CANCELAR · QUITAR DE LA LISTA
        // =====================================================================
        grupo.MapGet("/", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"""
                SELECT coalesce(json_agg(t), '[]'::json)::text AS "Value"
                  FROM (SELECT id, empresa, ruc, estado, creado_en AS "creadoEn", expira_en AS "expiraEn",
                               respondido_en AS "respondidoEn"
                          FROM mis_pedidos()) t
                """).FirstOrDefaultAsync(ct) ?? "[]", "application/json"))
        .RequiereSesion()
        .WithSummary("Los pedidos de mi cuenta (camino B)");


        grupo.MapPost("/{id:guid}/cancelar", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var r = await db.Database.SqlQuery<string>($"SELECT cancelar_pedido({id}) AS \"Value\"").FirstOrDefaultAsync(ct);
            return r switch
            {
                "ok"          => Results.Ok(new { mensaje = "Pedido cancelado. El enlace del titular ya no sirve." }),
                "no_abierto"  => Results.Conflict(new { error = "Ese pedido ya no está esperando." }),
                "sin_permiso" => Results.Json(new { error = "Solo el dueño o un colaborador administrador cancela pedidos." }, statusCode: 403),
                _             => Results.NotFound(new { error = "No existe ese pedido." })
            };
        })
        .RequiereSesion()
        .WithSummary("Cancelar un pedido abierto");


        grupo.MapPost("/{id:guid}/ocultar", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var r = await db.Database.SqlQuery<string>($"SELECT ocultar_pedido({id}) AS \"Value\"").FirstOrDefaultAsync(ct);
            return r switch
            {
                "ok"      => Results.Ok(new { mensaje = "Quitado de la lista." }),
                "abierto" => Results.Conflict(new { error = "Un pedido que espera se cancela, no se quita." }),
                _         => Results.NotFound(new { error = "No existe ese pedido." })
            };
        })
        .RequiereSesion()
        .WithSummary("Quitar un pedido respondido de la lista");


        // =====================================================================
        // LA PÁGINA DEL TITULAR (sin sesión)
        // =====================================================================
        var titular = rutas.MapGroup("/api/pedido").WithTags("Camino B");

        titular.MapGet("/{token}", async (string token, ContextoPlataforma db, CancellationToken ct) =>
        {
            var hash = GeneradorTokens.Hashear(token);
            var json = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object(
                         'estado', x.estado, 'empresa', x.empresa, 'ruc', x.ruc, 'quien', x.quien,
                         'cuenta', x.cuenta, 'mensaje', x.mensaje, 'hoyLaTiene', x.hoy_la_tiene,
                         'pausada', x.pausada, 'comprobantes', x.comprobantes, 'clientes', x.clientes,
                         'series', x.series, 'expiraEn', x.expira_en, 'titular', x.titular)::text AS "Value"
                  FROM resolver_pedido({hash}) x
                """).FirstOrDefaultAsync(ct);

            return Results.Content(json ?? """{"estado":"no_existe"}""", "application/json");
        })
        .WithSummary("Lo que ve el titular al abrir el enlace del pedido");


        titular.MapPost("/{token}/responder", async (
            string token, PeticionResponderPedido p, ContextoPlataforma db, IServicioCorreo correo,
            IServiceScopeFactory ambitos, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("CaminoB");
            var nombre = string.Join(' ', (p.Nombre ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (nombre.Length > 120)
                return Results.BadRequest(new { error = "El nombre es demasiado largo.", codigo = "campo_nombre" });

            var hash = GeneradorTokens.Hashear(token);
            var (tokenInv, hashInv) = GeneradorTokens.Nuevo();
            var expiraInv = DateTime.UtcNow.AddHours(HorasInvitacion);

            var fila = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('r', x.resultado, 'empresaId', x.empresa_id, 'empresa', x.empresa,
                         'pausada', x.estaba_pausada, 'titular', x.titular, 'invitacion', x.invitacion,
                         'pideCorreo', x.pide_correo, 'pideNombre', x.pide_nombre,
                         'avisoCorreo', x.aviso_correo, 'avisoNombre', x.aviso_nombre)::text AS "Value"
                  FROM responder_pedido({hash}, {p.Aprobar}, {p.Seguir}, {nombre}::text,
                                        {hashInv}, {expiraInv}::timestamptz) x
                """).FirstOrDefaultAsync(ct);

            using var doc = JsonDocument.Parse(fila ?? "{}");
            var raiz = doc.RootElement;
            var r = Texto(raiz, "r");
            var empresa = Texto(raiz, "empresa") ?? "la empresa";

            switch (r)
            {
                case "aprobado":
                case "rechazado":
                    break;
                case "nombre":
                    return Results.BadRequest(new { error = "Escribe tu nombre y apellido para crear tu usuario.", codigo = "campo_nombre" });
                case "reclamo_abierto":
                    return Results.Conflict(new
                    {
                        error = $"Hay un reclamo con documentos abierto sobre {empresa}. Cuando se resuelva, pueden volver a pedírtela.",
                        codigo = r
                    });
                case "vencido":
                    return Results.Json(new { error = "Este enlace venció. Quien lo pidió puede pedírtela otra vez.", codigo = r }, statusCode: 410);
                case "usado":
                    return Results.Json(new { error = "Ya respondiste este pedido.", codigo = r }, statusCode: 410);
                case "cancelado":
                    return Results.Json(new { error = "Este pedido se canceló. No tienes que hacer nada.", codigo = r }, statusCode: 410);
                default:
                    return Results.NotFound(new { error = "Ese enlace no existe. Revisa que lo copiaste entero.", codigo = "no_existe" });
            }

            var pideCorreo = Texto(raiz, "pideCorreo") ?? "";
            var pideNombre = Texto(raiz, "pideNombre") ?? "";

            if (r == "rechazado")
            {
                await EnviarAsync(correo, log, pideCorreo, Plantillas.PedidoRechazado(pideNombre, empresa));
                return Results.Ok(new { mensaje = $"Listo. {empresa} sigue donde está; le avisamos a quien lo pidió." });
            }

            // FASE 4c: si estaba en pausa, la clave vuelve a poder emitir.
            if (raiz.TryGetProperty("pausada", out var pz) && pz.ValueKind == JsonValueKind.True
                && raiz.TryGetProperty("empresaId", out var eid) && eid.ValueKind == JsonValueKind.String)
                await PausaEnServicio.AplicarAsync(ambitos, eid.GetGuid(), false, null, log, ct);

            await EnviarAsync(correo, log, pideCorreo, Plantillas.PedidoAprobado(pideNombre, empresa));

            if (Texto(raiz, "avisoCorreo") is { Length: > 0 } aviso)
                await EnviarAsync(correo, log, aviso, Plantillas.EmpresaPasoAOtraCartera(Texto(raiz, "avisoNombre") ?? "", empresa));

            var conInvitacion = raiz.TryGetProperty("invitacion", out var inv) && inv.ValueKind == JsonValueKind.True;

            return Results.Ok(new
            {
                mensaje = Texto(raiz, "titular") switch
                {
                    "crear"       => $"Listo. {empresa} ya está en la cartera de quien la pidió. Ahora elige tu contraseña.",
                    "mover"       => $"Listo. {empresa} ya está en la cartera de quien la pidió. Sigues entrando con tu correo de siempre.",
                    "ya_entra"    => $"Listo. {empresa} ya está en la cartera de quien la pidió. Sigues entrando como hasta hoy.",
                    _             => $"Listo. {empresa} ya está en la cartera de quien la pidió."
                },
                invitacion = conInvitacion ? $"/invitacion/{tokenInv}" : null
            });
        })
        .WithSummary("El titular aprueba o rechaza el pedido");
    }


    // =========================================================================

    public static string Enlace(OpcionesCorreo opciones, string token) =>
        $"{opciones.UrlBase.TrimEnd('/')}/pedido/{token}";

    private static string? MotivoRevision(string r) => r switch
    {
        "ok"               => null,
        "libre"            => null,
        "ya_es_tuya"       => "Esa empresa ya está en tu cuenta.",
        "ya_pediste"       => "Ya se la pediste a su titular. Está en Mis empresas, esperando su respuesta.",
        "ya_pedida"        => "Otra cuenta ya pidió esa empresa y su titular todavía no responde. Vuelve a intentarlo en unos días.",
        "reclamo_abierto"  => "Hay un reclamo con documentos abierto sobre esa empresa. Cuando se resuelva, podrás pedirla.",
        "sin_correo"       => "Esa empresa no tiene correo del titular. Su titular puede reclamarla con sus documentos desde «Recupérala».",
        "sin_permiso"      => "Solo el dueño o un colaborador administrador pueden pedir empresas.",
        "convertir"        => "Tu Cuenta Negocio ya tiene su empresa. Conviértela en Cuenta Profesional (en «Mi cuenta») para agregar otra.",
        "cuenta_no_activa" => "Tu cuenta está sin confirmar o suspendida.",
        "tope"             => "Llegaste al tope de 10 pedidos por día. Vuelve mañana.",
        _                  => "No se puede pedir esa empresa ahora."
    };

    private static async Task EnviarAsync(IServicioCorreo correo, ILogger log, string destino, CorreoCompuesto m)
    {
        if (string.IsNullOrWhiteSpace(destino)) return;
        try { await correo.EnviarAsync(destino, m.Asunto, m.Html, m.Texto); }
        catch (Exception e) { log.LogWarning(e, "No salió un correo del camino B a {Destino}.", destino); }
    }

    private static string? Texto(JsonElement e, string nombre) =>
        e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}