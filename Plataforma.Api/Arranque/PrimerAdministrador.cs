using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Arranque;

/// <summary>
/// Crea el primer super administrador si no existe ninguno.
///
/// POR QUÉ AQUÍ Y NO EN UNA MIGRACIÓN:
///
/// Una contraseña escrita en un archivo .sql acaba en el repositorio, en el
/// historial de git y en la máquina de cualquiera que clone el proyecto. Y el
/// historial de git no se limpia: queda para siempre, aunque después se
/// cambie la línea.
///
/// Aquí sale de una variable de entorno, y se obliga a cambiarla en el primer
/// ingreso.
/// </summary>
public static class PrimerAdministrador
{
    public static async Task CrearSiHaceFaltaAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>()
                              .CreateLogger("Arranque");

        using var ambito = app.Services.CreateScope();
        var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();

        // TODO ESTO VA EN UNA TRANSACCIÓN, igual que cualquier petición: sin
        // ella el contexto se pierde entre sentencias y las políticas de
        // aislamiento devuelven cero filas.
        await using var tx = await db.Database.BeginTransactionAsync();

        // Con la bandera de super administrador, para poder escribir un
        // usuario que no pertenece a ninguna empresa.
        await db.FijarContextoAsync(null, null, "127.0.0.1", "arranque", esSuperAdmin: true);

        var yaHay = await db.Usuarios.AnyAsync(u => u.EsSuperAdmin);
        if (yaHay)
        {
            await tx.CommitAsync();
            return;
        }

        var correo = Environment.GetEnvironmentVariable("PLATAFORMA_ADMIN_CORREO");
        var clave  = Environment.GetEnvironmentVariable("PLATAFORMA_ADMIN_CLAVE");

        if (string.IsNullOrWhiteSpace(correo) || string.IsNullOrWhiteSpace(clave))
        {
            throw new InvalidOperationException(
                "No hay ningún super administrador y faltan PLATAFORMA_ADMIN_CORREO " +
                "o PLATAFORMA_ADMIN_CLAVE para crearlo. Mira el .env.");
        }

        db.Usuarios.Add(new Usuario
        {
            EmpresaId          = null,          // vive fuera de las empresas
            EsSuperAdmin       = true,
            Correo             = correo,
            Nombre             = "Administrador",
            ClaveHash          = HashDeClaves.Cifrar(clave),
            // La del .env es una contraseña generada y escrita en un archivo.
            // Sirve para entrar una vez.
            ClaveCambioForzado = true
        });

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        log.LogWarning(
            "Creado el primer super administrador: {Correo}. " +
            "Tiene que cambiar la contraseña en el primer ingreso.", correo);
    }
}
