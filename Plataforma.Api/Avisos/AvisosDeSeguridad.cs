using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Avisos;

/// <summary>
/// «Pasó esto en tu cuenta.»
///
/// LOS CUATRO MOMENTOS EN QUE ALGUIEN SE QUEDA CON UNA CUENTA son: le cambian
/// la contraseña, le configuran un segundo factor, le generan códigos de
/// recuperación nuevos, o le borran el segundo factor. Los cuatro dejan rastro
/// en la bitácora — y la bitácora la mira el administrador, no el dueño.
///
/// ESTE CORREO ES LO ÚNICO QUE SE LO CUENTA AL DUEÑO. Sin él, quien entra con
/// una contraseña robada y se pone el segundo factor en su propio teléfono se
/// queda con la cuenta para siempre y en silencio: la víctima solo se entera
/// cuando intenta entrar, y para entonces ya no puede.
///
/// SE MANDA AUNQUE EL CAMBIO LO HAYA HECHO SU DUEÑO, y es deliberado. Un aviso
/// que solo llega cuando hay problema enseña a la gente a ignorarlo el resto
/// del tiempo, y el día que importa ya nadie lo lee. Llegando siempre, el
/// silencio es la señal rara.
///
/// NUNCA IMPIDE LA OPERACIÓN. `IServicioCorreo.EnviarAsync` devuelve un
/// booleano y no lanza, así que un servidor de correo caído no puede hacer que
/// un cambio de contraseña falle — y esta clase tampoco lanza por su cuenta.
/// Si el correo no sale, se anota en el log y ya está: el cambio está hecho, y
/// deshacerlo porque no se pudo avisar sería peor.
/// </summary>
public sealed class AvisosDeSeguridad(
    ContextoPlataforma db,
    IServicioCorreo correo,
    ILogger<AvisosDeSeguridad> log)
{
    /// <summary>
    /// Avisa al dueño de una cuenta de que algo cambió en ella.
    /// </summary>
    /// <param name="usuarioId">A quién. Se le manda a la dirección de su cuenta.</param>
    /// <param name="quePaso">En una frase y en pasado: «Se cambió tu contraseña.»</param>
    /// <param name="consecuencia">Qué significa para él, en una o dos frases.</param>
    public async Task AvisarAsync(
        Guid? usuarioId,
        string quePaso,
        string consecuencia,
        CancellationToken ct = default)
    {
        if (usuarioId is not { } id) return;

        try
        {
            // `IgnoreQueryFilters` porque a un usuario desactivado también hay
            // que avisarle: que le hayan desactivado la cuenta no quita que
            // merezca saber que alguien le tocó la contraseña.
            var quien = await db.Usuarios
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.Id == id)
                .Select(u => new { u.Correo, u.Nombre })
                .FirstOrDefaultAsync(ct);

            if (quien is null) return;

            var mensaje = Plantillas.AvisoDeSeguridad(quien.Nombre, quePaso, consecuencia);

            var enviado = await correo.EnviarAsync(
                quien.Correo, mensaje.Asunto, mensaje.Html, mensaje.Texto, ct);

            if (!enviado)
            {
                log.LogWarning(
                    "No salió el aviso de seguridad a {Correo}: «{Que}». " +
                    "El cambio SÍ se hizo; lo que falló es el aviso.",
                    quien.Correo, quePaso);
            }
        }
        catch (Exception e)
        {
            // NI UNA EXCEPCIÓN DE AQUÍ PUEDE TUMBAR LO DE FUERA.
            //
            // Esto corre dentro de la transacción de la petición, así que una
            // excepción que subiera la desharía entera: alguien cambiaría su
            // contraseña, el aviso fallaría, y se quedaría con la contraseña
            // vieja sin entender por qué. Exactamente al revés de lo que este
            // archivo viene a conseguir.
            log.LogError(e, "Falló el aviso de seguridad a {Usuario}: «{Que}»", id, quePaso);
        }
    }


    // =========================================================================
    // LOS CUATRO AVISOS, con su texto.
    //
    // Escritos aquí y no en cada endpoint para que digan lo mismo siempre: son
    // el correo que alguien va a leer el peor día, y no es el momento de que
    // cada uno esté redactado de una forma.
    // =========================================================================

    public Task ClaveCambiadaAsync(Guid? usuarioId, CancellationToken ct = default) =>
        AvisarAsync(usuarioId,
            "Se cambió la contraseña de tu cuenta.",
            "Tus demás sesiones se cerraron. Tu segundo factor no cambió: " +
            "sigue siendo el mismo teléfono.",
            ct);

    public Task SegundoFactorConfiguradoAsync(Guid? usuarioId, CancellationToken ct = default) =>
        AvisarAsync(usuarioId,
            "Se configuró un segundo factor en tu cuenta.",
            "A partir de ahora, entrar pide un código de seis dígitos de ese " +
            "teléfono además de tu contraseña.",
            ct);

    public Task CodigosNuevosAsync(Guid? usuarioId, CancellationToken ct = default) =>
        AvisarAsync(usuarioId,
            "Se generaron códigos de recuperación nuevos en tu cuenta.",
            "Los ocho anteriores dejaron de valer, incluidos los que no habías " +
            "usado. Tu contraseña y tu segundo factor no cambiaron.",
            ct);

    public Task SegundoFactorBorradoAsync(Guid? usuarioId, CancellationToken ct = default) =>
        AvisarAsync(usuarioId,
            "Se borró el segundo factor de tu cuenta.",
            "Tus sesiones se cerraron. Al volver a entrar habrá que configurarlo " +
            "otra vez escaneando un código QR nuevo, y se darán ocho códigos de " +
            "recuperación nuevos.",
            ct);
}
