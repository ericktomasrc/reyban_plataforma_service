using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Arranque;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Cobros;

public sealed record PeticionPaquete(Guid? EmpresaId);

/// <summary>
/// e234 (085) — EL TOPE DE COMPROBANTES DEL MES Y LOS PAQUETES EXTRA:
///
///   GET  /api/planes/comprobantes?empresa=    cuántos lleva, su tope, su saldo y
///                                             el paquete (para Inicio y el aviso).
///   POST /api/planes/comprobantes/paquete     comprar un paquete con la tarjeta
///                                             principal: al quedar pagado, la base
///                                             suma el saldo (no vence).
///   GET  /api/admin/planes/comprobantes       el uso de todas (columna de Empresas).
///
/// El precio y la cantidad los pone SIEMPRE la base, nunca el navegador.
/// </summary>
public static class EndpointsComprobantesTope
{
    public static void MapearComprobantesTope(this IEndpointRouteBuilder rutas)
    {
        var g = rutas.MapGroup("/api/planes/comprobantes").WithTags("Planes");

        g.MapGet("/", async (Guid? empresa, ContextoPeticion ctx, ContextoPlataforma db, OpcionesCulqi llaves,
            OpcionesConexionSunat sunat, CancellationToken ct) =>
        {
            if ((empresa ?? ctx.EmpresaId) is not { } e) return Results.Ok(new { aplica = false });
            var q = await db.Database.SqlQuery<string?>($"SELECT comprobantes_tope({e})::text AS \"Value\"").FirstAsync(ct);
            if (q is null) return Results.Ok(new { aplica = false });

            var modo = await EndpointsCobros.ModoAsync(db, llaves, sunat, ct);
            var extra = await db.Database.SqlQuery<string>($"""
                SELECT jsonb_build_object(
                    'puedeComprar', EXISTS (SELECT 1 FROM empresas x WHERE x.id = {e} AND x.cuenta_id = cobros_mi_cuenta()),
                    'tarjeta', (SELECT jsonb_build_object('marca', t.marca, 'ultimos4', t.ultimos4)
                                  FROM cobros_tarjetas t
                                 WHERE t.cuenta_id = cobros_mi_cuenta() AND t.modo = {modo.Modo}
                                   AND t.principal AND t.quitada_en IS NULL))::text AS "Value"
                """).FirstAsync(ct);

            using var a = JsonDocument.Parse(q);
            using var b = JsonDocument.Parse(extra);
            var r = new Dictionary<string, object?>();
            foreach (var x in a.RootElement.EnumerateObject()) r[x.Name] = x.Value.Clone();
            foreach (var x in b.RootElement.EnumerateObject()) r[x.Name] = x.Value.Clone();
            r["empresa"] = e;
            r["conTarjeta"] = modo.ConTarjeta;
            r["sinDineroReal"] = modo.SinDineroReal;
            return Results.Ok(r);
        })
        .RequiereSesion()
        .WithSummary("Comprobantes del mes: cuántos lleva, su tope, su saldo y el paquete extra");

        g.MapPost("/paquete", async (PeticionPaquete pe, ContextoPeticion ctx, ContextoPlataforma db, OpcionesCulqi llaves,
            OpcionesConexionSunat sunat, ClienteCulqi culqi, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Cobros");
            if ((pe.EmpresaId ?? ctx.EmpresaId) is not { } empresa)
                return Results.BadRequest(new { error = "Elige la empresa." });

            var modo = await EndpointsCobros.ModoAsync(db, llaves, sunat, ct);
            if (!modo.ConTarjeta)
                return Results.BadRequest(new { error = "Los pagos con tarjeta están apagados por ahora." });
            var tarjetaId = await db.Database.SqlQuery<Guid?>($"""
                SELECT t.id AS "Value" FROM cobros_tarjetas t
                 WHERE t.cuenta_id = cobros_mi_cuenta() AND t.modo = {modo.Modo} AND t.principal AND t.quitada_en IS NULL
                """).FirstOrDefaultAsync(ct);
            if (tarjetaId is null)
                return Results.BadRequest(new { error = "Agrega una tarjeta en «Mi plan › Mis pagos» para comprar el paquete." });

            JsonElement prep;
            try
            {
                prep = JsonDocument.Parse(await db.Database.SqlQuery<string>($"""
                    SELECT comprobantes_paquete_preparar({empresa}, {modo.Modo}, {tarjetaId})::text AS "Value"
                    """).FirstAsync(ct)).RootElement;
            }
            catch (Exception e) when (EndpointsCobros.EnCurso(e))
            {
                return Results.Conflict(new { error = "Ya hay un pago en curso. Espera unos segundos." });
            }

            var cobro = prep.GetProperty("id").GetGuid();
            var monto = prep.GetProperty("monto").GetDecimal();
            var cantidad = prep.GetProperty("cantidad").GetInt32();
            var tarjeta = prep.GetProperty("tarjeta").GetString()!;
            var r = modo.Modo == "simulado"
                ? PasarelaSimulada.Cobrar(tarjeta)
                : await culqi.CobrarAsync(tarjeta, prep.GetProperty("correo").GetString() ?? "", monto,
                                          prep.GetProperty("concepto").GetString()!, cobro, prep.GetProperty("cuenta").GetGuid(), CancellationToken.None);   // 086: ya se pidió el cargo: no se corta

            if (r.SinRespuesta)
                return Results.Json(new { estado = "procesando", error = "Culqi no respondió todavía. Si el pago pasa, el paquete se suma solo en unos minutos." },
                                    statusCode: StatusCodes.Status202Accepted);
            if (r.Requiere3ds)
                r = r with { Ok = false, Codigo = "3ds", Mensaje = "Tu banco pidió confirmar este pago. Vuelve a agregar la tarjeta en «Mis pagos» (te pedirá confirmar) y prueba de nuevo." };

            await db.Database.SqlQuery<string>($"""
                SELECT cobros_resultado({cobro}, {r.Ok}, {r.Id}::text, {r.Codigo}::text, {r.Mensaje}::text)::text AS "Value"
                """).FirstAsync(CancellationToken.None);
            log.LogInformation("Paquete de comprobantes {Cobro} ({Modo}), empresa {Empresa}, usuario {Usuario}: {Estado}.",
                cobro, modo.Modo, empresa, ctx.UsuarioId, r.Ok ? "pagado" : r.Codigo);

            if (!r.Ok)
                return Results.BadRequest(new { estado = "rechazado", error = r.Mensaje ?? "La tarjeta no pasó. No se sumó el paquete.", codigo = r.Codigo });

            return Results.Ok(new
            {
                estado = "pagado", monto, cantidad,
                mensaje = (modo.SinDineroReal ? "Pago de PRUEBA aprobado (no se movió dinero real). " : "")
                          + $"¡Listo! Se sumaron {cantidad.ToString("N0", CultureInfo.InvariantCulture)} comprobantes. Ya puedes emitir; lo que no uses pasa al mes siguiente. Tu comprobante llegará a tu correo."
            });
        })
        .RequiereSesion()
        .WithSummary("Comprar un paquete extra de comprobantes con la tarjeta principal");

        rutas.MapGet("/api/admin/planes/comprobantes", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"SELECT comprobantes_uso_empresas()::text AS \"Value\"").FirstAsync(ct),
                            "application/json"))
            .WithTags("Planes")
            .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
            .WithSummary("Comprobantes del mes de cada empresa (con tope)");
    }

    /// <summary>El mensaje del bloqueo (402, codigo «tope»).</summary>
    public static string MensajeBloqueo(JsonElement q)
    {
        var tope = q.TryGetProperty("tope", out var t) ? t.GetInt32() : 0;
        var reinicia = q.TryGetProperty("reinicia", out var r) && r.ValueKind == JsonValueKind.String
            ? DateOnly.Parse(r.GetString()!).ToString("dd/MM/yyyy") : "el 1 del mes que viene";
        return $"Llegaste a tu tope de {tope.ToString("N0", CultureInfo.InvariantCulture)} comprobantes de este mes. Las facturas y boletas nuevas están en pausa hasta el {reinicia}, " +
               "salvo que compres un paquete extra. Las notas de crédito y las anulaciones siguen funcionando.";
    }
}