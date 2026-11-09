using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Arranque;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Planes;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Cobros;

public sealed record PeticionCambioPlan(Guid EmpresaId, string? Plan, bool Pagar);

/// <summary>
/// e230 (083) — SUBIR Y BAJAR DE PLAN A MITAD DE PERIODO, como las
/// suscripciones de software:
///
///   GET  /api/planes/cambio?empresa=&amp;plan=   qué pasaría (no cambia nada): sube
///                                         ya pagando la diferencia, baja en el
///                                         próximo cobro, o cambia ya sin cobrar.
///   POST /api/planes/cambio                hacerlo. Con «pagar» y una diferencia
///                                         que cobrar, se cobra con la tarjeta
///                                         principal y el plan sube al quedar
///                                         pagado (lo hace la base). Sin «pagar»
///                                         (sin tarjeta), sube ya y la diferencia
///                                         va al próximo cobro.
///
/// El monto lo calcula SIEMPRE la base (planes_cotizar_cambio), nunca el navegador.
/// </summary>
public static class EndpointsCambioPlan
{
    private static readonly string[] Planes = ["basico", "pro", "max"];

    public static void MapearCambioPlan(this IEndpointRouteBuilder rutas)
    {
        var g = rutas.MapGroup("/api/planes").WithTags("Planes");

        g.MapGet("/cambio", async (Guid empresa, string? plan, ContextoPlataforma db, OpcionesCulqi llaves,
            OpcionesConexionSunat sunat, CancellationToken ct) =>
        {
            var p = (plan ?? "").Trim().ToLowerInvariant();
            if (!Planes.Contains(p)) return Results.BadRequest(new { error = "Elige Básico, Pro o Max." });

            var q = await db.Database.SqlQuery<string>($"SELECT planes_cotizar_cambio({empresa}, {p})::text AS \"Value\"").FirstAsync(ct);
            var modo = await EndpointsCobros.ModoAsync(db, llaves, sunat, ct);
            var extra = await db.Database.SqlQuery<string>($"""
                SELECT jsonb_build_object(
                    'tarjeta', (SELECT jsonb_build_object('id', t.id, 'marca', t.marca, 'ultimos4', t.ultimos4)
                                  FROM cobros_tarjetas t
                                 WHERE t.cuenta_id = cobros_mi_cuenta() AND t.modo = {modo.Modo}
                                   AND t.principal AND t.quitada_en IS NULL),
                    'comprobante', (SELECT CASE d.tipo_doc WHEN 'ruc' THEN 'factura' ELSE 'boleta' END
                                      FROM cobros_datos d WHERE d.cuenta_id = cobros_mi_cuenta()))::text AS "Value"
                """).FirstAsync(ct);

            using var a = JsonDocument.Parse(q);
            using var b = JsonDocument.Parse(extra);
            var r = new Dictionary<string, object?>();
            foreach (var x in a.RootElement.EnumerateObject()) r[x.Name] = x.Value.Clone();
            foreach (var x in b.RootElement.EnumerateObject()) r[x.Name] = x.Value.Clone();
            r["conTarjeta"] = modo.ConTarjeta;
            r["sinDineroReal"] = modo.SinDineroReal;
            return Results.Ok(r);
        })
        .RequiereSesion()
        .WithSummary("Qué pasa si una empresa cambia de plan (diferencia, fecha, cómo se paga)");

        g.MapPost("/cambio", async (PeticionCambioPlan pe, ContextoPeticion ctx, ContextoPlataforma db, OpcionesCulqi llaves,
            OpcionesConexionSunat sunat, ClienteCulqi culqi, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Cobros");
            var plan = (pe.Plan ?? "").Trim().ToLowerInvariant();
            if (!Planes.Contains(plan)) return Results.BadRequest(new { error = "Elige Básico, Pro o Max." });
            var nombre = ReglasPlan.Nombre(plan);

            using var cot = JsonDocument.Parse(await db.Database
                .SqlQuery<string>($"SELECT planes_cotizar_cambio({pe.EmpresaId}, {plan})::text AS \"Value\"").FirstAsync(ct));
            var q = cot.RootElement;
            var accion = q.GetProperty("accion").GetString();
            var alProximo = q.TryGetProperty("alProximo", out var ap) && ap.ValueKind == JsonValueKind.True;

            // SUBIR PAGANDO AHORA: como «Mis pagos › Pagar», con la tarjeta principal.
            if (accion == "cobrar" && pe.Pagar && !alProximo)
            {
                var modo = await EndpointsCobros.ModoAsync(db, llaves, sunat, ct);
                if (!modo.ConTarjeta)
                    return Results.BadRequest(new { error = "Los pagos con tarjeta están apagados por ahora." });
                var tarjetaId = await db.Database.SqlQuery<Guid?>($"""
                    SELECT t.id AS "Value" FROM cobros_tarjetas t
                     WHERE t.cuenta_id = cobros_mi_cuenta() AND t.modo = {modo.Modo} AND t.principal AND t.quitada_en IS NULL
                    """).FirstOrDefaultAsync(ct);
                if (tarjetaId is null)
                    return Results.BadRequest(new { error = "Agrega una tarjeta en «Mis pagos» para pagar la diferencia." });

                JsonElement prep;
                try
                {
                    prep = JsonDocument.Parse(await db.Database.SqlQuery<string>($"""
                        SELECT planes_cambio_preparar({pe.EmpresaId}, {plan}, {modo.Modo}, {tarjetaId})::text AS "Value"
                        """).FirstAsync(ct)).RootElement;
                }
                catch (Exception e) when (EndpointsCobros.EnCurso(e))
                {
                    return Results.Conflict(new { error = "Ya hay un pago en curso. Espera unos segundos." });
                }

                var cobro = prep.GetProperty("id").GetGuid();
                var monto = prep.GetProperty("monto").GetDecimal();
                var tarjeta = prep.GetProperty("tarjeta").GetString()!;
                var r = modo.Modo == "simulado"
                    ? PasarelaSimulada.Cobrar(tarjeta)
                    : await culqi.CobrarAsync(tarjeta, prep.GetProperty("correo").GetString() ?? "", monto,
                                              prep.GetProperty("concepto").GetString()!, cobro, prep.GetProperty("cuenta").GetGuid(), CancellationToken.None);   // 086: ya se pidió el cargo: no se corta

                if (r.SinRespuesta)
                    return Results.Json(new { estado = "procesando", error = "Culqi no respondió todavía. Si el pago pasa, tu plan sube solo en unos minutos." },
                                        statusCode: StatusCodes.Status202Accepted);
                if (r.Requiere3ds)
                    r = r with { Ok = false, Codigo = "3ds", Mensaje = "Tu banco pidió confirmar este pago. Vuelve a agregar la tarjeta en «Mis pagos» (te pedirá confirmar) y prueba de nuevo." };

                await db.Database.SqlQuery<string>($"""
                    SELECT cobros_resultado({cobro}, {r.Ok}, {r.Id}::text, {r.Codigo}::text, {r.Mensaje}::text)::text AS "Value"
                    """).FirstAsync(CancellationToken.None);
                log.LogInformation("Diferencia de plan {Cobro} ({Modo}), usuario {Usuario}: {Estado}.",
                    cobro, modo.Modo, ctx.UsuarioId, r.Ok ? "pagado" : r.Codigo);

                if (!r.Ok)
                    return Results.BadRequest(new { estado = "rechazado", error = r.Mensaje ?? "La tarjeta no pasó. Tu plan no cambió.", codigo = r.Codigo });

                return Results.Ok(new
                {
                    estado = "pagado", accion, monto,
                    mensaje = (modo.SinDineroReal ? "Pago de PRUEBA aprobado (no se movió dinero real). " : "")
                              + $"¡Listo! Ya tienes {nombre}. Pagaste S/ {monto:0.00} por los días que quedan; tu comprobante llegará a tu correo."
                });
            }

            // Sin pagar ahora (la diferencia al próximo cobro) solo si no hay cómo
            // cobrarla hoy: sin tarjeta, pagos con tarjeta apagados o menos de S/ 3.
            if (accion == "cobrar" && !alProximo)
            {
                var modo = await EndpointsCobros.ModoAsync(db, llaves, sunat, ct);
                var hayTarjeta = modo.ConTarjeta && await db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS (SELECT 1 FROM cobros_tarjetas t WHERE t.cuenta_id = cobros_mi_cuenta() AND t.modo = {modo.Modo}
                                     AND t.principal AND t.quitada_en IS NULL) AS "Value"
                    """).FirstAsync(ct);
                if (hayTarjeta)
                    return Results.BadRequest(new { error = "Tienes una tarjeta guardada: la diferencia se paga ahora con ella." });
            }

            using var hecho = JsonDocument.Parse(await db.Database
                .SqlQuery<string>($"SELECT planes_cambiar({pe.EmpresaId}, {plan}, {accion == "cobrar"})::text AS \"Value\"").FirstAsync(ct));
            var h = hecho.RootElement;
            string Fecha(string campo) => h.TryGetProperty(campo, out var f) && f.ValueKind == JsonValueKind.String
                ? DateOnly.Parse(f.GetString()!).ToString("dd/MM/yyyy") : "";

            var prueba = await db.Database.SqlQuery<DateTime?>(
                $"SELECT GREATEST(e.prueba_hasta, c.prueba_hasta) AS \"Value\" FROM empresas e JOIN cuentas c ON c.id = e.cuenta_id WHERE e.id = {pe.EmpresaId}")
                .FirstOrDefaultAsync(ct);
            var enPrueba = prueba is { } fin && fin > DateTime.UtcNow;

            var mensaje = accion switch
            {
                "igual"      => $"Ya estás en {nombre}.",
                "deshacer"   => $"Listo: sigues en {nombre}. Se quitó el cambio que tenías programado.",
                "programado" => $"Listo: sigues con {ReglasPlan.Nombre(h.GetProperty("actual").GetString())} hasta el {Fecha("pagadoHasta")}, que ya pagaste. Desde el {Fecha("desde")} pasas a {nombre}. Puedes deshacerlo hasta ese día.",
                "cobrar"     => $"Listo: ya tienes {nombre}. La diferencia (S/ {h.GetProperty("monto").GetDecimal():0.00}) se suma a tu próximo cobro.",
                _ => enPrueba
                    ? $"Listo: elegiste {nombre} para cuando termine tu mes gratis (el {prueba!.Value.ToLocalTime():dd/MM/yyyy}). Hasta ese día sigues usando todo."
                    : $"Listo: la empresa queda en el plan {nombre}."
            };
            return Results.Ok(new { estado = "hecho", accion, mensaje });
        })
        .RequiereSesion()
        .WithSummary("Cambiar el plan de una empresa (sube pagando la diferencia, baja en el próximo cobro)");
    }
}