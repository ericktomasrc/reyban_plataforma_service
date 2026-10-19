using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// 033 — EL TIPO DE CAMBIO, UNA CONSULTA POR DÍA.
///
/// El de SUNAT (que sale del cierre de la SBS) es el mismo para todas las
/// empresas y todo el día. Se pide a Decolecta la PRIMERA vez que alguien
/// factura en dólares con esa fecha, se guarda en `tipos_cambio`, y desde ahí
/// todas las facturas de esa fecha, de todas las empresas, usan el guardado.
///
/// SIN PUBLICACIÓN (domingo, feriado, o hoy todavía no): se propone el ÚLTIMO
/// publicado antes de esa fecha, diciendo de qué día es. Siempre se puede
/// corregir a mano en el comprobante.
///
/// Para no gastar consultas en vano: un día pasado sin publicación queda
/// anotado y no se vuelve a preguntar; HOY sin publicación se vuelve a
/// preguntar como mucho una vez por hora.
///
/// NUNCA BLOQUEA: si algo falla, la pantalla deja escribirlo a mano.
/// </summary>
public sealed class TipoCambioDiario(
    ClienteDecolecta decolecta, ContextoPlataforma db, ILogger<TipoCambioDiario> log)
{
    private sealed record Guardado(DateOnly Fecha, decimal? Compra, decimal? Venta, bool Publicado, DateTime ConsultadoEn);

    public async Task<ResultadoConsulta> ObtenerAsync(DateOnly fecha, CancellationToken ct)
    {
        var hoy = HoyEnPeru();
        if (fecha > hoy)
            return new ResultadoConsulta(400, new { error = "La fecha es posterior a hoy.", manual = true });

        Guardado? g;
        try { g = await LeerAsync(fecha, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Sin la 033 (o la base no responde): como antes, directo al proveedor.
            log.LogWarning(e, "No se pudo leer el tipo de cambio guardado. ¿Falta la migración 033?");
            return await decolecta.TipoCambioAsync(fecha, ct);
        }

        if (g is { Publicado: true }) return Respuesta(g, null);

        // ¿Vale la pena preguntar? Un día pasado ya anotado sin publicación, no.
        var preguntar = g is null || (fecha == hoy && g.ConsultadoEn < DateTime.UtcNow.AddHours(-1));
        var proveedorCaido = false;

        if (preguntar)
        {
            var (estado, encontrado) = await PreguntarAsync(fecha, ct);
            if (encontrado is not null)
                return Respuesta(encontrado, encontrado.Fecha == fecha ? null : fecha);
            proveedorCaido = estado != 404;
        }

        // SIN PUBLICACIÓN: el último publicado antes de esa fecha. Al proveedor
        // se le pregunta como mucho por 3 días más (los primeros días de uso).
        var preguntas = 0;
        for (var atras = 1; atras <= 7; atras++)
        {
            var dia = fecha.AddDays(-atras);
            var previo = await LeerAsync(dia, ct);

            if (previo is { Publicado: true }) return Respuesta(previo, fecha, proveedorCaido);
            if (previo is not null || proveedorCaido || preguntas >= 3) continue;   // ya se sabe que ese día no hubo
            preguntas++;

            // Los primeros días de uso todavía no hay nada guardado: se pregunta
            // por los días anteriores (cada respuesta queda guardada).
            var (estado, encontrado) = await PreguntarAsync(dia, ct);
            if (encontrado is not null) return Respuesta(encontrado, fecha);
            if (estado != 404) proveedorCaido = true;
        }

        return new ResultadoConsulta(proveedorCaido ? 503 : 404, new
        {
            error = proveedorCaido
                ? "La consulta del tipo de cambio no está disponible. Escríbelo a mano (el de venta SUNAT de la fecha)."
                : "SUNAT no tiene tipo de cambio para esa fecha. Escríbelo a mano.",
            manual = true
        });
    }


    /// <summary>Pregunta al proveedor por una fecha y guarda lo que conteste. 404 = no hubo publicación.</summary>
    private async Task<(int Estado, Guardado? Encontrado)> PreguntarAsync(DateOnly fecha, CancellationToken ct)
    {
        var r = await decolecta.TipoCambioAsync(fecha, ct);

        if (r.Estado == 404)
        {
            await GuardarAsync(fecha, null, null, false, ct);
            return (404, null);
        }
        if (r.Estado != 200) return (r.Estado, null);

        var j = JsonSerializer.SerializeToElement(r.Cuerpo);
        decimal? Dec(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
        var venta = Dec("venta");
        if (venta is not > 0) return (404, null);

        // Si el proveedor devuelve otro día (el último publicado), se guarda en
        // ese día, y la fecha pedida queda como «sin publicación».
        var publicadaEl = j.TryGetProperty("fecha", out var f) && f.ValueKind == JsonValueKind.String &&
                          DateOnly.TryParseExact(f.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                                 DateTimeStyles.None, out var d) && d <= fecha
            ? d : fecha;

        var compra = Dec("compra");
        await GuardarAsync(publicadaEl, compra, venta, true, ct);
        if (publicadaEl != fecha) await GuardarAsync(fecha, null, null, false, ct);

        return (200, new Guardado(publicadaEl, compra, venta, true, DateTime.UtcNow));
    }

    private async Task<Guardado?> LeerAsync(DateOnly fecha, CancellationToken ct)
    {
        var filas = await db.Database.SqlQuery<string>($"""
            SELECT json_build_object('fecha', fecha, 'compra', compra, 'venta', venta,
                                     'publicado', publicado, 'consultadoEn', consultado_en)::text AS "Value"
              FROM tipos_cambio
             WHERE moneda = 'USD' AND fecha = {fecha}
            """).ToListAsync(ct);

        if (filas.Count == 0) return null;

        using var doc = JsonDocument.Parse(filas[0]);
        var r = doc.RootElement;
        decimal? Dec(string n) => r.GetProperty(n).ValueKind == JsonValueKind.Number ? r.GetProperty(n).GetDecimal() : null;

        return new Guardado(
            fecha, Dec("compra"), Dec("venta"),
            r.GetProperty("publicado").GetBoolean(),
            r.GetProperty("consultadoEn").GetDateTime().ToUniversalTime());
    }

    /// <summary>Uno por fecha. Un día ya publicado no se pisa nunca.</summary>
    private async Task GuardarAsync(DateOnly fecha, decimal? compra, decimal? venta, bool publicado, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO tipos_cambio (moneda, fecha, compra, venta, publicado, consultado_en)
                VALUES ('USD', {fecha}, {compra}::numeric, {venta}::numeric, {publicado}, now())
                ON CONFLICT (moneda, fecha) DO UPDATE
                   SET compra = EXCLUDED.compra, venta = EXCLUDED.venta,
                       publicado = EXCLUDED.publicado, consultado_en = now()
                 WHERE NOT tipos_cambio.publicado
                """, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Guardar es un ahorro, no una condición: si falla, la respuesta sigue.
            log.LogWarning(e, "No se pudo guardar el tipo de cambio del {Fecha}.", fecha);
        }
    }

    private static ResultadoConsulta Respuesta(Guardado g, DateOnly? pedida, bool proveedorCaido = false) => new(200, new
    {
        fecha  = g.Fecha.ToString("yyyy-MM-dd"),
        compra = g.Compra,
        venta  = g.Venta,
        nota   = pedida is not { } p ? null
            : proveedorCaido
                ? $"No se pudo consultar el del {p:dd/MM/yyyy}: se propone el último guardado, del {g.Fecha:dd/MM/yyyy}. Revísalo."
                : $"SUNAT no publicó tipo de cambio para el {p:dd/MM/yyyy}" +
                  (p == HoyEnPeru() ? " (todavía)" : "") +
                  $": se propone el último publicado, del {g.Fecha:dd/MM/yyyy}."
    });

    private static DateOnly HoyEnPeru()
    {
        try
        {
            var lima = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, lima));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        }
    }
}