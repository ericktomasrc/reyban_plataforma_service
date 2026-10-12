using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Avisos;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Sesion;

public record PeticionMiNombre(string Nombre);
public record PeticionCodigosNuevos(string Clave, string Codigo);


/// <summary>
/// MI PROPIA CUENTA. Todo lo de aquí es sobre quien pregunta, y solo sobre él.
///
/// NINGÚN ENDPOINT DE ESTE ARCHIVO LLEVA PERMISO, y es lo correcto: un permiso
/// sirve para decidir qué puedes hacerle a OTRO. Mirar tus propios datos o
/// cambiar tu propio nombre no se le niega a nadie — un usuario al que no se
/// le deja ver su propia cuenta es una cuenta que no puede cuidar de sí misma.
///
/// Y NINGUNO ACEPTA UN IDENTIFICADOR. Todos usan <c>ctx.UsuarioId</c>, que sale
/// de la cookie. Si alguno recibiera un id por parámetro, sería cuestión de
/// tiempo que alguien probara a poner el de otra persona.
///
/// LO QUE SÍ ESTÁ AQUÍ Y ES DELICADO —cambiar la contraseña, reiniciar el
/// segundo factor, generar códigos nuevos— PIDE LA CONTRASEÑA OTRA VEZ. La
/// cookie demuestra que la sesión se abrió en algún momento; no demuestra que
/// quien está delante del teclado ahora mismo sea su dueño.
/// </summary>
public static class EndpointsPerfil
{
    public static void MapearPerfil(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/perfil").WithTags("Mi perfil");


        // =====================================================================
        // MIS DATOS
        //
        // Se parece a /api/yo pero no es lo mismo, y conviene que sigan
        // separados: /api/yo lo pide el front en CADA arranque y en cada
        // recarga, así que tiene que ser barato. Esto se pide una vez, cuando
        // alguien abre su perfil, y puede permitirse contar sesiones y códigos.
        //
        // Mezclarlos habría hecho que toda la aplicación pagara cuatro
        // consultas más en cada recarga para enseñar un dato que casi nadie
        // mira.
        // =====================================================================
        grupo.MapGet("/", async (
            ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            var u = await db.Usuarios
                .AsNoTracking()
                .Where(x => x.Id == ctx.UsuarioId)
                .Select(x => new
                {
                    x.Id, x.Nombre, x.Correo, x.EsSuperAdmin,
                    x.DosfaActivo, x.DosfaActivadoEn,
                    x.ClaveCambiadaEn, x.UltimoIngresoEn, x.CreadoEn
                })
                .FirstOrDefaultAsync(ct);

            if (u is null) return Results.Unauthorized();

            var empresa = ctx.EmpresaId is null ? null : await db.Empresas
                .AsNoTracking()
                .Where(e => e.Id == ctx.EmpresaId)
                .Select(e => new { e.Ruc, e.RazonSocial })
                .FirstOrDefaultAsync(ct);

            var roles = await db.UsuarioRoles
                .AsNoTracking()
                .Where(r => r.UsuarioId == ctx.UsuarioId && r.Activo)
                .OrderBy(r => r.Rol.Nombre)
                .Select(r => r.Rol.Nombre)
                .ToListAsync(ct);

            // CUÁNTOS CÓDIGOS DE RECUPERACIÓN LE QUEDAN.
            //
            // Es el número más útil de esta pantalla y hasta hoy no había forma
            // de verlo: quien usó seis de sus ocho no se enteraba hasta
            // quedarse sin ninguno, que es el peor momento posible.
            var codigosRestantes = await db.Database
                .SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM codigos_recuperacion
                     WHERE usuario_id = {ctx.UsuarioId} AND usado_en IS NULL
                    """)
                .FirstAsync(ct);

            // Las sesiones vivas, contando la de ahora. Si dice 3 y solo estás
            // en un sitio, hay dos que conviene cerrar.
            var sesionesAbiertas = await db.Database
                .SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM sesiones
                     WHERE usuario_id = {ctx.UsuarioId}
                       AND revocada_en IS NULL
                       AND expira_en > now()
                    """)
                .FirstAsync(ct);

            return Results.Ok(new
            {
                u.Id, u.Nombre, u.Correo, u.EsSuperAdmin,
                u.DosfaActivo, u.DosfaActivadoEn,
                u.ClaveCambiadaEn, u.UltimoIngresoEn, u.CreadoEn,
                empresa,
                roles,
                codigosRestantes,
                sesionesAbiertas
            });
        })
        .RequiereSesion()
        .WithSummary("Mis datos, mi segundo factor y mis sesiones");


        // =====================================================================
        // CAMBIAR MI NOMBRE
        //
        // SOLO EL NOMBRE, NO EL CORREO, y esta es la decisión del archivo.
        //
        // Dejar que alguien cambie su propio correo de acceso sin más sería
        // regalar la cuenta a quien encuentre una sesión abierta: cambia el
        // correo al suyo, pide un enlace de acceso y la cuenta es suya, sin
        // haber sabido nunca la contraseña.
        //
        // Hacerlo bien exige mandar un código a la dirección NUEVA y no creerse
        // el cambio hasta que vuelva — que es una pantalla entera, no un campo.
        // Mientras no exista, el correo lo cambia el administrador de la
        // empresa, que avisa a la dirección anterior.
        // =====================================================================
        grupo.MapPut("/", async (
            PeticionMiNombre p,
            ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            var nombre = p.Nombre?.Trim() ?? "";

            if (nombre.Length == 0)
                return Results.BadRequest(new { error = "El nombre no puede quedar vacío." });

            if (nombre.Length > 120)
                return Results.BadRequest(new { error = "El nombre es demasiado largo." });

            var usuario = await db.Usuarios.FirstAsync(u => u.Id == ctx.UsuarioId, ct);
            usuario.Nombre = nombre;

            await db.SaveChangesAsync(ct);

            return Results.Ok(new { nombre, mensaje = "Nombre actualizado." });
        })
        .RequiereSesion()
        .WithSummary("Cambiar mi nombre");


        // =====================================================================
        // CÓDIGOS DE RECUPERACIÓN NUEVOS
        //
        // Llena el hueco que quedaba: hasta hoy, quien se quedaba sin códigos
        // tenía que reiniciar el segundo factor entero y volver a escanear el
        // QR en el teléfono. Mucho trabajo para un problema pequeño, y suficiente
        // para que nadie lo hiciera.
        //
        // PIDE LAS DOS COSAS: LA CONTRASEÑA Y UN CÓDIGO DEL TELÉFONO.
        //
        // Y el código del teléfono es el que importa. Estos ocho códigos son
        // justamente la puerta de atrás del segundo factor: si se pudieran
        // generar con la contraseña sola, el segundo factor no estaría
        // protegiendo nada — bastaría con la contraseña para fabricarse una
        // llave que lo salta.
        //
        // Exigir el código del teléfono significa que solo puede pedir códigos
        // nuevos quien TIENE el teléfono ahora mismo. Que es exactamente el
        // momento correcto para hacerlo: antes de perderlo.
        //
        // LOS ANTERIORES DEJAN DE VALER AL INSTANTE, incluidos los que no se
        // habían usado. Si alguien pide códigos nuevos porque cree que los
        // viejos se filtraron, dejarlos vivos haría que la operación no
        // sirviera para nada.
        // =====================================================================
        grupo.MapPost("/codigos-recuperacion", async (
            PeticionCodigosNuevos p,
            ContextoPlataforma db, ContextoPeticion ctx, Cifrador cifrador,
            AvisosDeSeguridad avisos,
            CancellationToken ct) =>
        {
            var usuario = await db.Usuarios.FirstAsync(u => u.Id == ctx.UsuarioId, ct);

            if (!HashDeClaves.Verificar(p.Clave ?? "", usuario.ClaveHash, out _))
                return Results.Json(new { error = "La contraseña no es correcta." },
                                    statusCode: StatusCodes.Status401Unauthorized);

            if (!usuario.DosfaActivo || usuario.SecretoDosFactor is null)
                return Results.BadRequest(new
                {
                    error = "Todavía no tienes segundo factor configurado."
                });

            var secreto = Convert.FromBase64String(cifrador.Descifrar(usuario.SecretoDosFactor));

            // El mismo control de reutilización que al entrar: un código de seis
            // dígitos no sirve dos veces, ni aquí ni allí.
            if (!SegundoFactor.Verificar(secreto, p.Codigo ?? "", usuario.DosfaUltimoPaso, out var paso))
            {
                await RegistrarAsync(db, TipoEvento.DosfaFallido, false, ctx, ct);

                return Results.Json(new
                {
                    error = "El código de tu teléfono no coincide. Si tiene la hora " +
                            "desajustada, ponla en automática y prueba otra vez."
                },
                statusCode: StatusCodes.Status401Unauthorized);
            }

            usuario.DosfaUltimoPaso = paso;

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE codigos_recuperacion SET usado_en = now()
                 WHERE usuario_id = {ctx.UsuarioId} AND usado_en IS NULL
                """, ct);

            var codigos = CodigosRecuperacion.Generar();

            foreach (var c in codigos)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO codigos_recuperacion (usuario_id, codigo_hash)
                    VALUES ({ctx.UsuarioId}, {CodigosRecuperacion.Hashear(c)})
                    """, ct);
            }

            await db.SaveChangesAsync(ct);

            await avisos.CodigosNuevosAsync(ctx.UsuarioId, ct);

            // No hay tipo de evento para esto y no hace falta inventarlo: el
            // disparador de la base ya dejó en la bitácora las ocho filas
            // nuevas de `codigos_recuperacion` y las anuladas, con quién y
            // cuándo. Añadir un tipo obligaría a una migración para extender
            // el CHECK de `eventos_seguridad`, y a mantener dos versiones de la
            // misma verdad.
            return Results.Ok(new
            {
                codigos,
                aviso = "Los anteriores ya no valen. Guarda estos donde no se pierdan: " +
                        "no se pueden volver a consultar."
            });
        })
        .RequiereSesion()
        .WithSummary("Generar códigos de recuperación nuevos");


        // =====================================================================
        // CERRAR MIS DEMÁS SESIONES
        //
        // Para lo más común de todo: entraste en un ordenador que no era tuyo y
        // no te acuerdas de si cerraste.
        //
        // NO PIDE LA CONTRASEÑA, al revés que lo de arriba, y es deliberado.
        // Esto no abre ninguna puerta: la cierra. Lo peor que puede conseguir
        // alguien que lo pulse sin permiso es molestarte, y ponerle una
        // contraseña delante haría que la gente no lo usara el día que hace
        // falta usarlo rápido.
        //
        // LA SESIÓN DE AHORA NO SE TOCA. Cerrarla también dejaría a quien pulsa
        // el botón en la pantalla de ingreso, con la sensación de haber roto
        // algo.
        // =====================================================================
        grupo.MapDelete("/sesiones", async (
            ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            var cerradas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE sesiones
                   SET revocada_en = now(), motivo_revocacion = 'cerradas por su dueno'
                 WHERE usuario_id = {ctx.UsuarioId}
                   AND id <> {ctx.SesionId}
                   AND revocada_en IS NULL
                """, ct);

            return Results.Ok(new
            {
                cerradas,
                mensaje = cerradas == 0
                    ? "No había ninguna otra sesión abierta."
                    : cerradas == 1
                        ? "Se cerró la otra sesión."
                        : $"Se cerraron {cerradas} sesiones."
            });
        })
        .RequiereSesion()
        .WithSummary("Cerrar todas mis sesiones menos esta");
    }


    private static Task RegistrarAsync(
        ContextoPlataforma db, string tipo, bool exito,
        ContextoPeticion ctx, CancellationToken ct)
    {
        var usuario = ctx.UsuarioId?.ToString();
        var empresa = ctx.EmpresaId?.ToString();

        return db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT registrar_evento(
                {tipo}::text, {exito}::boolean,
                {usuario}::uuid, {empresa}::uuid, NULL, NULL)
            """, ct);
    }
}
