using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Recuperacion;

/// <summary>
/// LOS RECORDATORIOS DE UNA EMPRESA EN PAUSA (fase 4a).
///
/// Una vez por hora mira qué empresas llevan 30, 60 o 90 días en pausa sin
/// recordatorio, y a cada una le manda al correo del titular un enlace NUEVO
/// para recuperarla (el anterior queda anulado). Después del tercero, silencio:
/// la pausa no vence nunca, y siempre queda «Recupérala» en el ingreso.
///
/// La base decide si toca (recordatorios_pendientes / enlace_recordatorio, de
/// la 021), así que dos instancias a la vez no mandan dos correos: la segunda
/// recibe «nada».
/// </summary>
public sealed class RecordatoriosRecuperacion(
    IServiceScopeFactory ambitos,
    ILogger<RecordatoriosRecuperacion> log) : BackgroundService
{
    private static readonly TimeSpan Ciclo = TimeSpan.FromHours(1);
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromMinutes(2);
    private const string Agente = "recordatorios de recuperación";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(EsperaInicial, ct);
            using var reloj = new PeriodicTimer(Ciclo);

            do
            {
                try { await EnviarPendientesAsync(ct); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogError(e, "Los recordatorios de recuperación fallaron. Se reintenta en {Ciclo}.", Ciclo);
                }
            }
            while (await reloj.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task EnviarPendientesAsync(CancellationToken ct)
    {
        List<Guid> empresas;
        await using (var ambito = ambitos.CreateAsyncScope())
        {
            var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();
            empresas = await db.Database
                .SqlQuery<Guid>($"SELECT r AS \"Value\" FROM recordatorios_pendientes() r")
                .ToListAsync(ct);
        }

        var enviados = 0;
        foreach (var empresaId in empresas)
        {
            try { if (await EnviarAsync(empresaId, ct)) enviados++; }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "No se pudo enviar el recordatorio de la empresa {Empresa}.", empresaId);
            }
        }

        if (enviados > 0)
            log.LogInformation("Recordatorios de recuperación: {Enviados} enviados.", enviados);
    }

    private async Task<bool> EnviarAsync(Guid empresaId, CancellationToken ct)
    {
        await using var ambito = ambitos.CreateAsyncScope();
        var db       = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();
        var correo   = ambito.ServiceProvider.GetRequiredService<IServicioCorreo>();
        var opciones = ambito.ServiceProvider.GetRequiredService<OpcionesCorreo>();

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        await db.FijarContextoAsync(null, null, null, Agente, false, ct);

        var (token, hash) = GeneradorTokens.Nuevo();
        var expira = DateTime.UtcNow.AddHours(EndpointsRecuperacion.HorasEnlace);

        var fila = await db.Database.SqlQuery<string>($"""
            SELECT json_build_object('correo', x.correo, 'nombre', x.nombre, 'ruc', x.ruc, 'n', x.numero)::text AS "Value"
              FROM enlace_recordatorio({empresaId}, {hash}, {expira}::timestamptz) x
            """).FirstOrDefaultAsync(ct);

        if (fila is null) { await transaccion.CommitAsync(ct); return false; }

        using var doc = JsonDocument.Parse(fila);
        var r = doc.RootElement;
        var destino = r.GetProperty("correo").GetString() ?? "";
        var numero = r.GetProperty("n").GetInt32();

        var m = Plantillas.RecuperarEmpresa(
            r.GetProperty("nombre").GetString() ?? "", r.GetProperty("ruc").GetString() ?? "",
            EndpointsRecuperacion.Enlace(opciones, token), EndpointsRecuperacion.HorasEnlace, numero);

        // Primero se guarda (el enlace y el contador), después se escribe. Si
        // el correo falla, el próximo toca dentro de 30 días: no se reintenta
        // cada hora para no inundar a nadie.
        await transaccion.CommitAsync(ct);

        var enviado = await correo.EnviarAsync(destino, m.Asunto, m.Html, m.Texto, ct);
        if (!enviado) log.LogWarning("No salió el recordatorio {Numero} de la empresa {Empresa}.", numero, empresaId);
        return enviado;
    }
}