using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

public record PeticionSuplantar(Guid UsuarioId);


/// <summary>
/// VER LA PLATAFORMA CON LOS OJOS DE OTRO, para dar soporte.
///
/// El caso que resuelve: un cliente llama diciendo «no me sale el botón para
/// emitir facturas». Sin esto hay tres salidas y las tres son malas — pedirle
/// capturas y adivinar, pedirle la contraseña (que no se hace nunca), o entrar
/// a la base de datos a mirar sus permisos a mano.
///
/// LAS CUATRO COSAS QUE LA HACEN SEGURA, y ninguna sobra:
///
///   1. NO SE NECESITA SU CONTRASEÑA. Nunca se llega a saber.
///   2. ES DE SOLO LECTURA. Lo cierra el middleware, en una sola puerta.
///   3. LA AUTORÍA NO CAMBIA. Si algo se escribiera, la bitácora diría el
///      nombre del super administrador. Nadie puede firmar como otro.
///   4. QUEDA REGISTRADO al empezar y al terminar, con nombres y horas.
///
/// LO QUE NO HACE, Y POR QUÉ: no pide la contraseña otra vez para empezar. Es
/// una decisión discutible y conviene tenerla a la vista. A favor: es una
/// herramienta de soporte que se usa con el cliente al teléfono, y una
/// contraseña por cada intento hace que la gente deje de usar la herramienta y
/// vuelva a pedir contraseñas ajenas, que es infinitamente peor. En contra:
/// quien encuentre la sesión del super administrador abierta puede mirar los
/// datos de cualquier cliente. Lo que compensa eso es que solo puede MIRAR, y
/// que mirar deja rastro.
/// </summary>
public static class EndpointsSuplantacion
{
    public static void MapearSuplantacion(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/suplantacion").WithTags("Suplantación");


        // =====================================================================
        // EMPEZAR
        //
        // Las comprobaciones NO están aquí: están en `iniciar_suplantacion`, en
        // la base, dentro de la misma transacción que escribe la columna y con
        // la sesión bloqueada. Hacerlas aquí dejaría un hueco entre comprobar y
        // escribir en el que la cuenta se desactiva y la suplantación empieza
        // igual. Este endpoint solo traduce el motivo del rechazo a un mensaje.
        // =====================================================================
        grupo.MapPost("/", async (
            PeticionSuplantar p,
            ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            if (ctx.Suplantando)
                return Results.BadRequest(new
                {
                    error = $"Ya estás viendo como {ctx.SuplantadoNombre}. " +
                            "Vuelve a ser tú antes de entrar a otra cuenta."
                });

            if (ctx.SesionId is not { } sesionId)
                return Results.Unauthorized();

            var resultado = await db.Database
                .SqlQuery<string>($"""
                    SELECT iniciar_suplantacion(
                        {sesionId}::uuid, {ctx.UsuarioId}::uuid, {p.UsuarioId}::uuid) AS "Value"
                    """)
                .FirstAsync(ct);

            if (resultado != "ok")
            {
                var mensaje = resultado switch
                {
                    "sesion_invalida"    => "Tu sesión ya no vale. Vuelve a entrar.",
                    "sesion_ajena"       => "Esa sesión no es tuya.",
                    "no_eres_super"      => "Solo la administración de la plataforma puede ver como otro usuario.",
                    "debes_cambiar_clave" => "Primero cambia tu contraseña: mientras la debas, no puedes entrar a otra cuenta.",
                    "eres_tu"            => "Ese eres tú.",
                    "no_existe"          => "Ese usuario no existe.",
                    "desactivado"        => "Está desactivado. Reactívalo primero si necesitas ver su pantalla.",
                    "es_super"           => "No se puede ver como otro super administrador: dos personas con la " +
                                            "misma potestad y una capaz de actuar como la otra hacen que la " +
                                            "bitácora deje de poder distinguirlas.",
                    "sin_empresa"        => "Ese usuario no pertenece a ninguna empresa.",
                    "empresa_suspendida" => "Su empresa está suspendida. Reactívala primero.",
                    _                    => "No se pudo entrar a esa cuenta."
                };

                return Results.BadRequest(new { error = mensaje, codigo = resultado });
            }

            // El nombre y la empresa se leen DESPUÉS de que la función haya
            // dicho que sí: si hubiera dicho que no, leerlos habría sido
            // confirmar que ese usuario existe.
            var objetivo = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == p.UsuarioId)
                .Select(u => new { u.Nombre, u.EmpresaId })
                .FirstAsync(ct);

            // EL EVENTO SE REGISTRA CON EL USUARIO OBJETIVO en el campo de
            // usuario, no con el super administrador, y a propósito: así aparece
            // en la pantalla de seguridad de ESA empresa. Quien audite a su
            // cliente tiene que poder ver que alguien de la plataforma entró a
            // mirar, sin depender de que nadie se lo cuente.
            //
            // LA EMPRESA VA EXPLÍCITA, y hay que ponerla a mano: sin ella,
            // `registrar_evento` usa `ctx_empresa()`, que en esta petición es el
            // contexto del super administrador — o sea, ninguna. El evento
            // quedaría sin empresa y no aparecería en la pantalla de seguridad
            // del cliente, que es justo donde tiene que aparecer.
            //
            // Quién entró va en el detalle. `ctx.AutorId` no hace falta aquí:
            // esta petición todavía no era una suplantación cuando empezó.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento(
                    {TipoEvento.SuplantacionIniciada}::text, true,
                    {p.UsuarioId}::uuid, {objetivo.EmpresaId}::uuid, NULL,
                    jsonb_build_object(
                        'suplantador_id', {ctx.UsuarioId}::uuid,
                        'suplantado',     {objetivo.Nombre}::text))
                """, ct);

            return Results.Ok(new
            {
                suplantado = objetivo.Nombre,
                mensaje = $"Estás viendo la plataforma como {objetivo.Nombre}, en solo lectura."
            });
        })
        .RequierePermiso(Permisos.Plataforma.Suplantar)
        .WithSummary("Empezar a ver la plataforma como otro usuario");


        // =====================================================================
        // VOLVER A SER YO
        //
        // SIN PERMISO NINGUNO, y es lo importante de este endpoint.
        //
        // Suplantando, `ctx.EsSuperAdmin` es falso y los permisos son los del
        // cliente — que no incluyen `plataforma.suplantar`. Si este endpoint
        // pidiera ese permiso, entrar a ver una cuenta sería un viaje de ida:
        // el botón de volver respondería 403.
        //
        // No hace falta protegerlo: lo único que consigue quien lo llame es
        // devolverse a su propia cuenta, que es donde ya podía estar.
        // =====================================================================
        grupo.MapDelete("/", async (
            ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            if (!ctx.Suplantando)
                return Results.Ok(new { mensaje = "No estabas viendo como nadie." });

            var suplantado = ctx.UsuarioId;
            var nombre     = ctx.SuplantadoNombre;

            // ANTES DE TOCAR NADA, VUELVE A SER ÉL MISMO EN LA BASE.
            //
            // Esto lo encontró una revisión y arregla dos cosas de golpe. El
            // contexto de esta petición se fijó con la empresa del cliente y
            // `super` apagado, y con eso:
            //
            //   1. El disparador de auditoría archiva la fila de `sesiones` en
            //      la BITÁCORA DEL CLIENTE —porque la fila no tiene empresa y
            //      `fn_auditar` recurre a la del contexto—, con el `antes` y el
            //      `despues` completos dentro: el identificador, la IP y el
            //      navegador del super administrador. Su administrador los lee
            //      desde la pantalla de auditoría, que ya tiene.
            //
            //   2. La política de `sesiones` no dejaría ver la fila propia si no
            //      se hubiera corregido en la 010, y el UPDATE sería de cero
            //      filas en silencio.
            //
            // Y sobre todo es lo honesto: en el momento en que se decide dejar
            // de ser otro, ya no se es. El inicio y el fin quedan así
            // archivados igual, los dos fuera de la empresa del cliente.
            await db.FijarContextoAsync(
                ctx.SuplantadorId, null, ctx.Ip?.ToString(), ctx.Agente, true, ct);

            var termino = await db.Database
                .SqlQuery<bool>($"""
                    SELECT terminar_suplantacion({ctx.SesionId}::uuid) AS "Value"
                    """)
                .FirstAsync(ct);

            if (!termino)
            {
                // NO SE DICE QUE TERMINÓ SI NO TERMINÓ. Antes esta respuesta
                // caía fuera del `if` y devolvía «Vuelves a ser tú» siempre: el
                // front se lo creía, refrescaba, y la barra naranja seguía ahí
                // sin error y sin explicación.
                return Results.Json(new
                {
                    error = "No se pudo terminar. Cierra sesión y vuelve a entrar.",
                    codigo = "no_termino"
                }, statusCode: StatusCodes.Status409Conflict);
            }

            var minutos = ctx.SuplantacionInicio is { } desde
                ? (int)Math.Round((DateTime.UtcNow - desde).TotalMinutes)
                : 0;

            // La empresa va explícita porque el contexto ya se corrigió
            // arriba: `ctx_empresa()` es nula otra vez, y sin este parámetro
            // el evento del final no aparecería en la pantalla de seguridad
            // del cliente aunque el del principio sí. Los dos extremos
            // tienen que contar la misma historia en el mismo sitio.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento(
                    {TipoEvento.SuplantacionTerminada}::text, true,
                    {suplantado}::uuid, {ctx.EmpresaId}::uuid, NULL,
                    jsonb_build_object(
                        'suplantador_id', {ctx.SuplantadorId}::uuid,
                        'suplantado',     {nombre}::text,
                        'minutos',        {minutos}::int))
                """, ct);

            return Results.Ok(new { mensaje = "Vuelves a ser tú." });
        })
        .RequiereSesion()
        .WithSummary("Dejar de ver como otro usuario");
    }
}
