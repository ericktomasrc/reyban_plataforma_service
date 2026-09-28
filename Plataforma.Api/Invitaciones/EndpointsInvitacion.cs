using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Invitaciones;

public record PeticionEstablecerClave(
    string Clave,

    // El secreto del segundo factor, en Base32, tal como se lo dio el GET y
    // tal como lo tiene ya el teléfono de quien responde.
    string Secreto,

    // Los seis dígitos que demuestran que el QR llegó a su destino.
    string Codigo);

/// <summary>
/// Las dos llamadas que hace la pantalla del enlace.
///
/// SON LOS ÚNICOS ENDPOINTS SIN SESIÓN DE TODA LA APLICACIÓN, junto al login.
/// Y no pueden tenerla: quien abre una invitación todavía no tiene contraseña.
///
/// Por eso las dos van contra funciones `SECURITY DEFINER` de la migración 007
/// y no contra EF: con el contexto vacío, el aislamiento de la base no deja
/// leer ni una fila de `usuarios`.
///
/// LO QUE PROTEGE ESTO son los 32 bytes al azar del token. No hay nada más, y
/// no hace falta nada más — pero sí hace falta que no se filtren: de ahí que
/// nunca aparezcan en una respuesta, ni en un mensaje de error, ni en el log.
/// </summary>
public static class EndpointsInvitacion
{
    /// <summary>El nombre que verá la persona en su aplicación de códigos.</summary>
    private const string Emisor = "Reyban Plataforma";

    public static void MapearInvitacion(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/invitacion").WithTags("Invitación");


        // =====================================================================
        // ¿ESTE ENLACE SIRVE, Y DE QUIÉN ES?
        //
        // Lo llama la pantalla al abrirse, para saludar por el nombre y para
        // no dejar que alguien escriba una contraseña en un formulario que iba
        // a fallar de todas formas.
        //
        // NO DICE POR QUÉ NO SIRVE cuando no sirve. Caducado, usado o
        // inexistente dan la misma respuesta: quien prueba enlaces al azar no
        // necesita saber cuál de las tres acertó.
        // =====================================================================
        grupo.MapGet("/{token}", async (
            string token, ContextoPlataforma db, CancellationToken ct) =>
        {
            var hash = GeneradorTokens.Hashear(token);

            var fila = await db.InvitacionesResueltas
                .FromSqlInterpolated($"SELECT * FROM resolver_invitacion({hash})")
                .FirstOrDefaultAsync(ct);

            if (fila is null
                || fila.UsadoEn   is not null
                || fila.AnuladoEn is not null
                || fila.ExpiraEn  <= DateTime.UtcNow
                || !fila.UsuarioActivo
                || !fila.EmpresaOperativa)
            {
                return Results.NotFound(new
                {
                    error = "Este enlace ya no sirve. Pídele a tu administrador que te envíe uno nuevo.",
                    codigo = "enlace_invalido"
                });
            }

            // EL SECRETO DEL SEGUNDO FACTOR SE GENERA AQUÍ Y VIAJA AL
            // NAVEGADOR, que lo devolverá con el formulario.
            //
            // No se guarda a medias en la base a propósito: hacerlo obligaría
            // a poder leerlo después sin sesión —una función capaz de devolver
            // el secreto de alguien a quien solo se conoce por su token— y esa
            // función es mejor que no exista.
            //
            // No se pierde nada de seguridad: quien tiene el token va a ser el
            // dueño del factor, así que podría elegir el secreto que quisiera
            // de todas formas. Lo que importa es que al final del proceso
            // demuestre, con un código, que ese secreto está en su teléfono.
            var secreto = SegundoFactor.GenerarSecreto();

            return Results.Ok(new
            {
                fila.Correo,
                fila.Nombre,
                fila.Empresa,
                fila.ExpiraEn,

                // Para que la pantalla titule «Elige tu contraseña» o
                // «Restablece tu contraseña», que no son lo mismo para quien
                // lo lee.
                primeraVez = fila.Proposito == "invitacion",

                // El segundo factor, para el paso dos de la misma pantalla.
                secreto        = Convert.ToBase64String(secreto),
                direccionQr    = SegundoFactor.DireccionParaQr(fila.Correo, secreto, Emisor),
                secretoLegible = SegundoFactor.SecretoLegible(secreto)
            });
        })
        .WithSummary("Comprobar un enlace de invitación");


        // =====================================================================
        // ESTABLECER LA CONTRASEÑA
        //
        // Todo el trabajo lo hace `consumir_invitacion`, en una sola
        // transacción: comprobar, guardar, marcar el token, anular los demás y
        // cortar las sesiones abiertas.
        //
        // No se parte en pasos desde aquí porque entre un paso y otro cabe una
        // segunda pestaña con el mismo enlace.
        // =====================================================================
        grupo.MapPost("/{token}", async (
            string token,
            PeticionEstablecerClave p,
            HttpContext http,
            ContextoPlataforma db,
            Cifrador cifrador,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(p.Clave) || p.Clave.Length < 12)
                return Results.BadRequest(new
                {
                    error = "La contraseña tiene que tener al menos 12 caracteres."
                });

            if (string.IsNullOrWhiteSpace(p.Secreto))
                return Results.BadRequest(new
                {
                    error = "Falta el segundo factor. Recarga la página y vuelve a escanear el QR.",
                    codigo = "sin_secreto"
                });

            byte[] secreto;
            try
            {
                secreto = Convert.FromBase64String(p.Secreto);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "El segundo factor no llegó bien. Recarga la página." });
            }

            // EL CÓDIGO ES LA PRUEBA DE QUE EL QR LLEGÓ A UN TELÉFONO.
            //
            // Sin esta comprobación, alguien podría terminar el proceso sin
            // haber escaneado nada y quedarse con una cuenta cuyo segundo
            // factor no existe en ningún sitio: no podría entrar nunca más.
            if (!SegundoFactor.Verificar(secreto, p.Codigo, null, out _))
                return Results.BadRequest(new
                {
                    error = "El código no coincide. Si tu teléfono tiene la hora desajustada, " +
                            "ponla en automática y prueba otra vez.",
                    codigo = "codigo_invalido"
                });

            var hash      = GeneradorTokens.Hashear(token);
            var claveHash = HashDeClaves.Cifrar(p.Clave);
            var cifrado   = cifrador.Cifrar(Convert.ToBase64String(secreto));
            var ip        = http.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

            var usuarioId = await db.Database
                .SqlQuery<Guid?>(
                    $"SELECT consumir_invitacion({hash}, {claveHash}, {cifrado}, {ip}::inet) AS \"Value\"")
                .FirstAsync(ct);

            if (usuarioId is null)
                return Results.NotFound(new
                {
                    error = "Este enlace ya no sirve. Pídele a tu administrador que te envíe uno nuevo.",
                    codigo = "enlace_invalido"
                });

            // Los códigos de recuperación, después de que la transacción de
            // arriba haya salido bien. Antes sería prometer una salida a una
            // cuenta que quizá no llegó a quedar configurada.
            var codigos = CodigosRecuperacion.Generar();

            foreach (var c in codigos)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO codigos_recuperacion (usuario_id, codigo_hash)
                    VALUES ({usuarioId}::uuid, {CodigosRecuperacion.Hashear(c)})
                    """, ct);
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento('clave_restablecer_usado'::text, true,
                    {usuarioId}::uuid, NULL, NULL,
                    jsonb_build_object('ip', {ip}::text, 'dosfa', true))
                """, ct);

            // NO SE INICIA SESIÓN AQUÍ, y es a propósito: que la persona entre
            // una vez con su contraseña recién puesta confirma que la recuerda
            // y que el gestor de contraseñas de su navegador la guardó. Entrar
            // solo por haber abierto un enlace del correo se salta esa prueba.
            return Results.Ok(new
            {
                mensaje = "Cuenta lista. Ya puedes entrar.",

                // SE VEN UNA VEZ. De la base solo sale su hash.
                codigosRecuperacion = codigos,
                aviso = "Guarda estos códigos. Son tu salida si pierdes el teléfono."
            });
        })
        .WithSummary("Establecer la contraseña y el segundo factor desde un enlace");
    }
}

