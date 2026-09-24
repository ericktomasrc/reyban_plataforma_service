using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Sesion;

public record PeticionCambioClave(string ClaveActual, string ClaveNueva);

public static class EndpointsClave
{
    /// <summary>
    /// Ocho caracteres es poco, y lo es a propósito.
    ///
    /// Un mínimo alto empuja a la gente a patrones predecibles —"Empresa2026!"—
    /// que son peores que una frase larga y sin símbolos. Lo que de verdad
    /// protege aquí son las 210.000 iteraciones del hash y el bloqueo tras
    /// cinco intentos, no obligar a poner una mayúscula.
    /// </summary>
    private const int LargoMinimo = 8;

    public static void MapearClave(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api").WithTags("Sesión");

        grupo.MapPost("/clave", async (
            PeticionCambioClave peticion,
            ContextoPlataforma db,
            ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(peticion.ClaveNueva) ||
                peticion.ClaveNueva.Length < LargoMinimo)
            {
                return Results.BadRequest(new
                {
                    error = $"La contraseña nueva tiene que tener al menos {LargoMinimo} caracteres."
                });
            }

            if (peticion.ClaveActual == peticion.ClaveNueva)
                return Results.BadRequest(new { error = "La contraseña nueva es igual a la actual." });

            var usuario = await db.Usuarios.FirstAsync(u => u.Id == ctx.UsuarioId, ct);

            // SE PIDE LA ACTUAL AUNQUE YA HAYA SESIÓN, y no es burocracia:
            // es lo que impide que alguien que encuentre el portátil
            // desbloqueado se quede con la cuenta para siempre.
            if (!HashDeClaves.Verificar(peticion.ClaveActual, usuario.ClaveHash, out _))
            {
                await Registrar(db, TipoEvento.ClaveCambiada, false, ctx, ct);
                return Results.BadRequest(new { error = "La contraseña actual no es correcta." });
            }

            usuario.ClaveHash          = HashDeClaves.Cifrar(peticion.ClaveNueva);
            usuario.ClaveCambiadaEn    = DateTime.UtcNow;
            usuario.ClaveCambioForzado = false;

            // SE CIERRAN LAS DEMÁS SESIONES.
            //
            // Casi siempre se cambia una contraseña porque se sospecha que
            // alguien la tiene. Dejar abiertas las sesiones que ya existían
            // haría que el cambio no sirviera de nada.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE sesiones
                   SET revocada_en = now(), motivo_revocacion = 'cambio de contrasena'
                 WHERE usuario_id = {ctx.UsuarioId}
                   AND id <> {ctx.SesionId}
                   AND revocada_en IS NULL
                """, ct);

            await db.SaveChangesAsync(ct);
            await Registrar(db, TipoEvento.ClaveCambiada, true, ctx, ct);

            return Results.Ok(new { mensaje = "Contraseña cambiada. Las otras sesiones se han cerrado." });
        })
        // A MEDIAS a propósito: éste es justamente el endpoint que tiene que
        // funcionar cuando al usuario se le exige cambiar la contraseña.
        .RequiereSesionAMedias()
        .WithSummary("Cambiar mi contraseña");
    }


    private static Task Registrar(
        ContextoPlataforma db, string tipo, bool exito, ContextoPeticion ctx, CancellationToken ct)
    {
        var usuario = ctx.UsuarioId?.ToString();
        var empresa = ctx.EmpresaId?.ToString();

        return db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT registrar_evento({tipo}::text, {exito}::boolean,
                                    {usuario}::uuid, {empresa}::uuid, NULL, NULL)
            """, ct);
    }
}
