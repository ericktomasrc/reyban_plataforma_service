using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Arranque;

/// <summary>
/// 069 — EL INTERRUPTOR GENERAL DE PLANES, LA PRIMERA VEZ.
///
/// La migración lo deja sin decidir (NULL). Al arrancar, si sigue así:
///   · en producción (PLATAFORMA_SUNAT=real) queda ENCENDIDO: se cobra y cada
///     cuenta ve lo de su plan;
///   · en QA y en la PC de desarrollo queda APAGADO: todos ven todo.
/// Después manda el super administrador (Planes y cobros › Interruptor). Un
/// reinicio de bases lo vuelve a dejar sin decidir, y se decide otra vez aquí.
/// </summary>
public static class PlanesDefecto
{
    public static async Task FijarSiHaceFaltaAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Arranque");
        var produccion = app.Services.GetRequiredService<OpcionesConexionSunat>().Fija;

        using var ambito = app.Services.CreateScope();
        var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.FijarContextoAsync(null, null, "127.0.0.1", "arranque", esSuperAdmin: true);
            var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE plataforma_ajustes SET planes_activos = {produccion}, planes_cambiado_en = now()
                 WHERE id = 1 AND planes_activos IS NULL
                """);
            await tx.CommitAsync();
            if (n > 0)
                log.LogWarning(produccion
                    ? "Planes y cobros ENCENDIDOS (producción): cada cuenta ve lo de su plan."
                    : "Planes y cobros APAGADOS (QA o desarrollo): todas las cuentas ven todo y no se cobra.");
        }
        catch (Exception e)
        {
            // Sin la 069 la plataforma arranca igual (los planes quedan apagados).
            log.LogError(e, "No se pudo fijar el interruptor de planes al arrancar (¿falta la migración 069?).");
        }
    }
}