using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Arranque;

/// <summary>
/// La llave de emergencia para quien se quedó fuera y no tiene a nadie por
/// encima.
///
/// EL CASO QUE RESUELVE: el super administrador pierde el teléfono Y sus ocho
/// códigos de recuperación. A un usuario de empresa lo rescata su
/// administrador; al administrador de una empresa lo rescatas tú. A ti no te
/// rescata nadie.
///
/// CÓMO SE USA:
///
///   1. Añadir al .env del servidor:  PLATAFORMA_REINICIAR_2FA=tu@correo
///   2. Reiniciar la aplicación
///   3. Entrar con la contraseña y configurar el QR de nuevo
///   4. BORRAR ESA LÍNEA DEL .env
///
/// El paso 4 no es una formalidad: esto se lee en cada arranque, así que una
/// línea olvidada borra el segundo factor en cada despliegue y nadie entiende
/// por qué le vuelve a pedir el QR.
///
/// POR QUÉ ES ACEPTABLE QUE ESTA PUERTA EXISTA: para usarla hay que tener
/// acceso al servidor, y quien lo tiene ya tiene la base de datos entera —
/// podría hacer lo mismo con un UPDATE a mano. No abre una puerta nueva:
/// deja la que ya existía a mano, registrada en la bitácora y en el log.
/// </summary>
public static class ReinicioSegundoFactor
{
    public static async Task EjecutarSiHaceFaltaAsync(WebApplication app)
    {
        var correo = Environment.GetEnvironmentVariable("PLATAFORMA_REINICIAR_2FA")?.Trim();
        if (string.IsNullOrWhiteSpace(correo)) return;

        using var ambito = app.Services.CreateScope();
        var db  = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();
        var log = app.Logger;

        // Contexto de super administrador: hay que poder ver a cualquiera, y
        // la bitácora tiene que poder anotar el cambio.
        await db.FijarContextoAsync(null, null, "127.0.0.1", "arranque", true);

        // Por la función marcada, no por EF: es la misma puerta que usa el
        // resto del sistema, y así el aislamiento no estorba aunque la cuenta
        // sea de una empresa.
        var usuario = await db.Usuarios
            .IgnoreQueryFilters()
            .Where(u => u.Correo.ToLower() == correo.ToLower())
            .Select(u => new { u.Id, u.Correo, u.DosfaActivo })
            .FirstOrDefaultAsync();

        if (usuario is null)
        {
            log.LogWarning(
                "PLATAFORMA_REINICIAR_2FA apunta a {Correo} y no existe ningun usuario con ese correo. " +
                "No se ha tocado nada.", correo);
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT reiniciar_segundo_factor({usuario.Id}::uuid)");

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT registrar_evento('dosfa_desactivado'::text, true,
                {usuario.Id}::uuid, NULL, {usuario.Correo}::text,
                jsonb_build_object('origen', 'variable de entorno al arrancar'))
            """);

        log.LogWarning(
            "SEGUNDO FACTOR REINICIADO para {Correo} por PLATAFORMA_REINICIAR_2FA. " +
            "Sus sesiones se cerraron y sus codigos de recuperacion dejaron de valer. " +
            "BORRA ESA LINEA DEL .env: si se queda, esto se repite en cada arranque.",
            usuario.Correo);
    }
}
