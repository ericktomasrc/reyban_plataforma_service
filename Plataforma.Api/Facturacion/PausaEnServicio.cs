using Plataforma.Core.Datos;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// LA PAUSA, TAMBIÉN EN EL SERVICIO (fase 4c).
///
/// Cuando una empresa queda en pausa (o sale de ella), se le dice al servicio
/// de facturación con la clave de ESA empresa: en pausa, la clave solo puede
/// consultar y descargar. Así nadie emite a su nombre desde otro sistema.
///
/// EN SU PROPIO ÁMBITO: quien pausa (el dueño de la cartera) o recupera (sin
/// sesión) no tiene abierta esa empresa, y la credencial solo se lee con el
/// contexto de su empresa. Se abre un ámbito aparte con ese contexto, como la
/// revisión diaria de certificados.
///
/// NO BLOQUEA: si el servicio no responde, la pausa de la plataforma vale igual
/// y queda un aviso en el registro. Reintentarlo es pausar o reanudar otra vez.
/// </summary>
public static class PausaEnServicio
{
    public static async Task AplicarAsync(
        IServiceScopeFactory ambitos, Guid empresaId, bool pausar, string? motivo,
        ILogger log, CancellationToken ct)
    {
        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();
            var cliente = ambito.ServiceProvider.GetRequiredService<ClienteFacturacion>();

            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            await db.FijarContextoAsync(null, empresaId, null, "pausa en el servicio", false, ct);

            var r = await cliente.PausarEmisorAsync(empresaId, pausar, motivo, ct);
            await transaccion.CommitAsync(ct);

            if (!r.Exito)
                log.LogWarning("El servicio no aplicó la {Accion} de la empresa {Empresa}: {Error}",
                               pausar ? "pausa" : "reactivación", empresaId, r.Error);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "No se pudo avisar al servicio la {Accion} de la empresa {Empresa}.",
                           pausar ? "pausa" : "reactivación", empresaId);
        }
    }
}