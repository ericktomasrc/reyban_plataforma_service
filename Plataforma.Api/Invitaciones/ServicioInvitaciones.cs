using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Invitaciones;

public sealed record ResultadoInvitacion(
    bool Enviado,
    DateTime ExpiraEn,
    string Correo,

    // EL ENLACE EN CLARO, SOLO EN DESARROLLO Y SOLO CON EL CORREO APAGADO.
    // Nulo en cualquier otro caso.
    //
    // Existe para que el guion de pruebas complete el flujo entero sin un
    // buzón de por medio. Fuera de ese caso sería un agujero: quien crea la
    // cuenta vería el enlace de otra persona, y el punto de todo esto es
    // justamente que no lo vea.
    string? EnlaceDePrueba);

/// <summary>
/// Crear un enlace de un solo uso y mandarlo por correo.
///
/// ES EL ÚNICO SITIO QUE FABRICA ESTOS ENLACES. El alta de una empresa, el
/// alta de un usuario y el reenvío pasan todos por aquí, y por eso el plazo,
/// la anulación de los enlaces anteriores y el registro en la bitácora salen
/// iguales en los tres casos — sin que nadie tenga que acordarse.
/// </summary>
public sealed class ServicioInvitaciones(
    ContextoPlataforma db,
    IServicioCorreo correo,
    OpcionesCorreo opciones,
    ContextoPeticion ctx,
    IHostEnvironment entorno)
{
    /// <summary>
    /// Lo que se usa cuando no hay empresa todavía, o no se puede consultar.
    /// Coincide con el valor por defecto de la columna en la base.
    /// </summary>
    private const int HorasPorDefecto = 24;

    public async Task<ResultadoInvitacion> EnviarAsync(
        Guid usuarioId,
        CancellationToken ct = default)
    {
        var usuario = await db.Usuarios
            .IgnoreQueryFilters()
            .Include(u => u.Empresa)
            .FirstAsync(u => u.Id == usuarioId, ct);

        // Quién invita se resuelve aquí y no se pide por parámetro: así no hay
        // forma de que una llamada mande un nombre y otra se lo deje en blanco.
        var quienInvita = await db.Usuarios
            .IgnoreQueryFilters()
            .Where(u => u.Id == ctx.UsuarioId)
            .Select(u => u.Nombre)
            .FirstOrDefaultAsync(ct) ?? "El administrador";

        // El plazo lo decide la empresa de quien recibe el enlace, no quien lo
        // manda. Un super administrador no tiene empresa: le vale el defecto.
        var horas = usuario.Empresa?.HorasInvitacion ?? HorasPorDefecto;

        var (token, hash) = GeneradorTokens.Nuevo();
        var expira = DateTime.UtcNow.AddHours(horas);

        // LOS ENLACES ANTERIORES DEJAN DE VALER AHORA, no cuando se use el
        // nuevo. Si alguien pide tres reenvíos, los dos primeros son llaves
        // buenas circulando por correos que quizá se reenviaron a un tercero.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE tokens_un_uso
               SET anulado_en = now()
             WHERE usuario_id = {usuarioId}
               AND usado_en   IS NULL
               AND anulado_en IS NULL
            """, ct);

        // Sin EF: `tokens_un_uso` no es una entidad del modelo, y no tiene por
        // qué serlo. Nada del código de negocio la lee.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tokens_un_uso (usuario_id, proposito, token_hash, expira_en, creado_por)
            VALUES ({usuarioId}, 'invitacion', {hash}, {expira}, {ctx.UsuarioId})
            """, ct);

        // EL TOKEN VA EN LA RUTA, NO EN LA CONSULTA (?t=...). Las direcciones
        // con parámetros acaban en los registros de los servidores intermedios
        // y en la cabecera `Referer` de cualquier enlace externo de la página.
        var enlace = $"{opciones.UrlBase.TrimEnd('/')}/invitacion/{token}";

        // Primera vez o restablecimiento: lo distingue si ya entró alguna vez.
        // El correo tiene que decir cosas distintas — a quien nunca entró hay
        // que explicarle qué es esto; a quien ya tenía cuenta hay que avisarle
        // por si no lo pidió él.
        var mensaje = usuario.UltimoIngresoEn is null
            ? Plantillas.Invitacion(
                  usuario.Nombre,
                  usuario.Empresa?.RazonSocial ?? "la plataforma",
                  enlace, horas, quienInvita)
            : Plantillas.Restablecer(usuario.Nombre, enlace, horas);

        var enviado = await correo.EnviarAsync(
            usuario.Correo, mensaje.Asunto, mensaje.Html, mensaje.Texto, ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT registrar_evento('clave_restablecer_solicitado'::text, {enviado},
                {usuarioId}::uuid, {usuario.EmpresaId}::uuid, {usuario.Correo}::text,
                jsonb_build_object('horas', {horas}::int, 'enviado', {enviado}::boolean))
            """, ct);

        // LAS DOS CONDICIONES, Y LAS DOS HACEN FALTA. En producción el correo
        // está encendido, así que sale nulo aunque alguien se despiste con el
        // entorno; y en desarrollo con correo de verdad tampoco se enseña,
        // porque entonces el enlace ya llegó a su destinatario.
        var enlaceDePrueba = entorno.IsDevelopment() && !opciones.Habilitado
            ? enlace
            : null;

        return new ResultadoInvitacion(enviado, expira, usuario.Correo, enlaceDePrueba);
    }
}
