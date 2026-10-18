using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// LOS CORREOS DEL CERTIFICADO POR VENCER (paso 6.3, migración 025).
///
/// Los llama la revisión diaria (<see cref="RevisionCertificados"/>) en cada
/// vuelta de una hora, justo después de actualizar las fechas: así no hace
/// falta otro reloj.
///
/// LA BASE DECIDE QUÉ TOCA (`certificados_por_avisar`): a los 30, 15, 7 y 1
/// días, el día que vence y una vez vencido; una sola vez cada uno por
/// certificado; si la API estuvo apagada, solo el que toca hoy. También decide
/// a quién (`destinatarios_aviso_certificado`): el titular, los
/// administradores de la empresa, el dueño y los colaboradores administradores
/// de la cartera, y los super administradores.
///
/// CADA AVISO, EN SU TRANSACCIÓN: se anota, se escriben los correos y se
/// confirma solo si salió alguno. Si no salió ninguno (el correo está caído),
/// se deshace y se reintenta en la vuelta siguiente. Dos copias de la API a la
/// vez no mandan dos: la segunda espera en el índice único y no recibe nada.
///
/// El correo no lleva enlaces (ver <see cref="PlantillasCertificado"/>).
/// </summary>
public static class AvisosCertificado
{
    private const string Agente = "avisos del certificado por vencer";

    /// <summary>Para no repetir en el log, cada hora, que falta la migración.</summary>
    private static bool _avisadoSinMigracion;

    public static async Task EnviarPendientesAsync(
        IServiceScopeFactory ambitos, ILogger log, CancellationToken ct)
    {
        List<Pendiente> pendientes;

        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();

            var filas = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object('e', p.empresa_id, 'v', p.valido_hasta, 'u', p.umbral)::text AS "Value"
                  FROM certificados_por_avisar() p
                """).ToListAsync(ct);

            pendientes = filas.Select(Leer).ToList();
        }
        catch (PostgresException e) when (e.SqlState == "42883")
        {
            // La función no existe: falta la migración 025.
            if (!_avisadoSinMigracion)
                log.LogWarning("Falta aplicar la migración 025: no salen los correos del certificado por vencer.");
            _avisadoSinMigracion = true;
            return;
        }

        var enviados = 0;

        foreach (var p in pendientes)
        {
            try
            {
                if (await EnviarAsync(ambitos, log, p, ct)) enviados++;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "No se pudo enviar el aviso del certificado de la empresa {Empresa}.", p.EmpresaId);
            }
        }

        if (enviados > 0)
            log.LogInformation("Avisos del certificado por vencer: {Enviados} enviados.", enviados);
    }


    private static async Task<bool> EnviarAsync(
        IServiceScopeFactory ambitos, ILogger log, Pendiente p, CancellationToken ct)
    {
        await using var ambito = ambitos.CreateAsyncScope();
        var db     = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();
        var correo = ambito.ServiceProvider.GetRequiredService<IServicioCorreo>();

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        await db.FijarContextoAsync(null, null, null, Agente, false, ct);

        var fila = await db.Database.SqlQuery<string>($"""
            SELECT json_build_object('id', x.aviso_id, 'nombre', x.nombre, 'ruc', x.ruc,
                                     'fecha', x.valido_hasta_lima, 'dias', x.dias,
                                     'correos', x.correos)::text AS "Value"
              FROM tomar_aviso_certificado({p.EmpresaId}, {p.ValidoHasta}, {p.Umbral}) x
            """).FirstOrDefaultAsync(ct);

        // Ya no toca (otra copia lo mandó, o se renovó entre medias).
        if (fila is null) { await transaccion.RollbackAsync(ct); return false; }

        using var doc = JsonDocument.Parse(fila);
        var r = doc.RootElement;

        var avisoId = r.GetProperty("id").GetGuid();
        var nombre  = r.GetProperty("nombre").GetString() ?? "";
        var ruc     = r.GetProperty("ruc").ValueKind == JsonValueKind.String ? r.GetProperty("ruc").GetString() : null;
        var fecha   = DateOnly.Parse(r.GetProperty("fecha").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var dias    = r.GetProperty("dias").GetInt32();
        var correos = r.GetProperty("correos").EnumerateArray().Select(c => c.GetString()!).ToList();

        var m = PlantillasCertificado.VencimientoCertificado(nombre, ruc, fecha, dias);

        var fallidos = new List<string>();

        foreach (var destino in correos)
        {
            bool ok;
            try { ok = await correo.EnviarAsync(destino, m.Asunto, m.Html, m.Texto, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Falló el correo del certificado a un destinatario de la empresa {Empresa}.", p.EmpresaId);
                ok = false;
            }

            if (!ok) fallidos.Add(destino);
        }

        if (fallidos.Count == correos.Count)
        {
            // No salió ninguno: se deshace y la vuelta siguiente lo intenta otra vez.
            await transaccion.RollbackAsync(ct);
            log.LogWarning(
                "No salió ningún correo del certificado de la empresa {Empresa} ({Dias} días). Se reintenta en una hora.",
                p.EmpresaId, dias);
            return false;
        }

        if (fallidos.Count > 0)
        {
            var arreglo = fallidos.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT anotar_fallidos_aviso_certificado({avisoId}, {arreglo})", ct);

            log.LogWarning(
                "El aviso del certificado de la empresa {Empresa} no llegó a {Fallidos} de {Total} destinatarios.",
                p.EmpresaId, fallidos.Count, correos.Count);
        }

        await transaccion.CommitAsync(ct);

        log.LogInformation(
            "Aviso del certificado enviado: empresa {Empresa} (RUC {Ruc}), {Dias} días, {Total} destinatarios.",
            p.EmpresaId, ruc, dias, correos.Count - fallidos.Count);

        return true;
    }


    private sealed record Pendiente(Guid EmpresaId, DateTime ValidoHasta, short Umbral);

    private static Pendiente Leer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        // json_build_object escribe el timestamptz con su desfase; se pasa a UTC
        // para devolverlo exacto a tomar_aviso_certificado.
        var v = r.GetProperty("v").GetDateTimeOffset().UtcDateTime;

        return new Pendiente(r.GetProperty("e").GetGuid(), DateTime.SpecifyKind(v, DateTimeKind.Utc),
                             r.GetProperty("u").GetInt16());
    }
}