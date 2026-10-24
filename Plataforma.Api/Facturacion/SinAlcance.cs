using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// 046 — LAS CUENTAS QUE NO PUEDEN DEPENDER DE QUIÉN PREGUNTA.
///
/// El alcance recorta lo que cada persona VE de los comprobantes (su sucursal,
/// solo los suyos, la sucursal elegida arriba). Pero algunas reglas tienen que
/// contar TODO lo de la empresa aunque la persona no lo vea:
///
///   · ¿este comprobante ya tiene notas? (no se anula si las tiene, y el
///     saldo para otra nota se calcula con todas, las haga quien las haga)
///   · ¿esta sucursal tuvo movimiento? (para Eliminar y cambiar el código)
///
/// Con el recorte, una cajera «solo lo suyo» no vería la nota que hizo su
/// jefe y el sistema la dejaría anular. Por eso estas lecturas quitan el
/// recorte UN MOMENTO —solo el del alcance: la empresa sigue aislada— y lo
/// devuelven al terminar, pase lo que pase.
///
/// Solo para contar o comprobar. Nada que se enseñe en pantalla pasa por aquí.
/// </summary>
public static class SinAlcance
{
    public static async Task<T> LeerAsync<T>(ContextoPlataforma db, Func<Task<T>> leer, CancellationToken ct)
    {
        var antes = await db.Database
            .SqlQuery<string>($"SELECT coalesce(current_setting('app.alc_tipo', true), '') AS \"Value\"")
            .FirstAsync(ct);

        if (antes.Length == 0) return await leer();

        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.alc_tipo', '', true)", ct);
        try
        {
            return await leer();
        }
        finally
        {
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('app.alc_tipo', {antes}, true)", CancellationToken.None);
            }
            catch
            {
                // Si la lectura rompió la transacción, esta tampoco corre: la
                // petición termina en error y la transacción se deshace entera,
                // con el recorte y todo. No queda nada sin recortar.
            }
        }
    }
}