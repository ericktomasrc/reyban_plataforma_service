using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Arranque;

/// <summary>
/// Lo que se comprueba antes de atender la primera petición.
///
/// LA IDEA: fallar al arrancar, no a mitad del día.
///
/// Un sistema que arranca con la llave maestra equivocada funciona
/// perfectamente hasta que alguien intenta leer un certificado, y entonces
/// falla con un cliente delante. Un sistema que se niega a arrancar falla
/// cuando estás mirando el log, que es el único momento bueno para fallar.
/// </summary>
public static class ComprobacionesArranque
{
    private const int MigracionesEsperadas = 6;

    public static async Task ComprobarAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>()
                              .CreateLogger("Arranque");

        using var ambito = app.Services.CreateScope();
        var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();

        // --- 1. ¿Se llega a la base? ----------------------------------------
        try
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.CloseConnectionAsync();
        }
        catch (Exception ex)
        {
            // EL MENSAJE TIENE QUE DECIR LO QUE PASÓ, no lo que suele pasar.
            //
            // La primera versión de esto decía siempre «¿está Docker
            // corriendo?», y la primera vez que falló de verdad fue por un
            // puerto equivocado: la conexión llegaba a OTRA base de datos y
            // rechazaba la contraseña. El mensaje mandó a mirar donde no era.
            var donde = db.Database.GetConnectionString() is { } c
                ? Ocultar(c) : "(sin cadena de conexión)";

            var detalle = ex.InnerException?.Message ?? ex.Message;

            throw new InvalidOperationException(
                $"No se pudo conectar a PostgreSQL.\n" +
                $"  Conexión : {donde}\n" +
                $"  Dice     : {detalle}\n" +
                $"  Revisa PLATAFORMA_CADENA_CONEXION en el .env, y que el " +
                $"contenedor esté arriba (docker compose ps).", ex);
        }

        // --- 2. ¿Están las migraciones? --------------------------------------
        //
        // No las aplica: las migraciones se corren a mano, en orden y una sola
        // vez, con migrar.ps1. Un contenedor que se reinicia en bucle no puede
        // estar intentando migrar la base en cada vuelta.
        int aplicadas;
        try
        {
            // El alias "Value" no es opcional: SqlQuery<T> para un tipo
            // simple busca una columna con ese nombre exacto.
            aplicadas = await db.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM migraciones_aplicadas")
                .FirstAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "La base existe pero está vacía: no encuentro migraciones_aplicadas. " +
                "Corre .\\migrar.ps1 desde la raíz del proyecto.", ex);
        }

        if (aplicadas < MigracionesEsperadas)
        {
            throw new InvalidOperationException(
                $"La base tiene {aplicadas} migraciones y el código espera " +
                $"{MigracionesEsperadas}. Corre .\\migrar.ps1.");
        }

        // --- 3. ¿El rol de la aplicación es el correcto? ---------------------
        //
        // LA COMPROBACIÓN QUE MÁS VALE DE LAS TRES.
        //
        // Si la aplicación se conectara como `postgres`, todo funcionaría
        // igual de bien — y el aislamiento entre empresas no existiría, porque
        // un superusuario se salta las políticas siempre. Es un fallo
        // invisible: no da error, no da aviso, y solo se nota el día que un
        // cliente ve las facturas de otro.
        var usuario = await db.Database
            .SqlQuery<string>($"SELECT current_user::text AS \"Value\"")
            .FirstAsync();

        var esSuper = await db.Database
            .SqlQuery<bool>($"SELECT (rolsuper OR rolbypassrls) AS \"Value\" FROM pg_roles WHERE rolname = current_user")
            .FirstAsync();

        if (esSuper)
        {
            throw new InvalidOperationException(
                $"La aplicación se está conectando como '{usuario}', que puede " +
                "saltarse el aislamiento entre empresas. La cadena de conexión " +
                "tiene que usar el rol plataforma_app. Revisa " +
                "PLATAFORMA_CADENA_CONEXION en el .env.");
        }

        log.LogInformation("Base de datos lista · rol {Rol} · {N} migraciones", usuario, aplicadas);

        // --- 4. ¿Está la llave maestra? --------------------------------------
        _ = LlaveMaestra.Leer();
        log.LogInformation("Llave maestra presente");
    }


    /// <summary>
    /// Tapa la contraseña de una cadena de conexión antes de enseñarla en un
    /// mensaje de error. Un mensaje de arranque acaba pegado en un chat, en
    /// un ticket o en un log compartido.
    /// </summary>
    private static string Ocultar(string cadena) =>
        System.Text.RegularExpressions.Regex.Replace(
            cadena,
            @"(?i)(password|pwd)\s*=\s*[^;]*",
            "$1=***");
}
