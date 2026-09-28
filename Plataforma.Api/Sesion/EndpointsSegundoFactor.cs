using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Avisos;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Sesion;

public record PeticionCodigo(string Codigo);

/// <summary>
/// El segundo factor, dentro de una sesión a medias.
///
/// TODO LO DE AQUÍ LLEVA <c>RequiereSesionAMedias</c>: la sesión existe pero
/// no vale para nada más hasta que se supere el factor. Es el mismo estado en
/// el que vive el cambio de contraseña obligatorio.
///
/// TRES SITUACIONES DISTINTAS, y conviene no mezclarlas:
///
///   1. Entra y todavía NO TIENE factor configurado → preparar + confirmar.
///      Le pasa al super administrador recién creado y a quien acaba de
///      recibir un reinicio.
///   2. Entra y SÍ lo tiene → validar un código de seis dígitos.
///   3. Perdió el teléfono → uno de sus ocho códigos de recuperación.
/// </summary>
public static class EndpointsSegundoFactor
{
    private const string Emisor = "Reyban Plataforma";

    /// <summary>Cinco fallos y media hora fuera, igual que con la contraseña.</summary>
    private const int FallosParaBloquear = 5;
    private static readonly TimeSpan Bloqueo = TimeSpan.FromMinutes(30);

    public static void MapearSegundoFactor(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/2fa").WithTags("Segundo factor");


        // =====================================================================
        // 1a. PREPARAR: generar el secreto y devolver el QR
        //
        // EL SECRETO SE GUARDA YA, con `dosfa_activo` en false. Esa pareja
        // significa «a medio configurar»: no sirve para entrar, porque el
        // ingreso exige `dosfa_activo`.
        //
        // Guardarlo antes de confirmarlo es lo que permite que la persona
        // escanee el QR, cierre la ventana sin querer, vuelva a entrar y siga
        // teniendo el mismo secreto que ya tiene en su teléfono.
        //
        // Llamarlo otra vez genera uno nuevo y pisa el anterior. También es lo
        // correcto: si volvió aquí, es que el QR de antes no le sirvió.
        // =====================================================================
        grupo.MapPost("/preparar", async (
            ContextoPlataforma db, ContextoPeticion ctx, Cifrador cifrador,
            CancellationToken ct) =>
        {
            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == ctx.UsuarioId, ct);
            if (usuario is null) return Results.Unauthorized();

            if (usuario.DosfaActivo)
                return Results.BadRequest(new
                {
                    error = "Ya tienes segundo factor. Para cambiar de teléfono, reinícialo desde tu perfil."
                });

            var secreto = SegundoFactor.GenerarSecreto();

            // Cifrado con la llave maestra, no en claro. Un volcado de la base
            // sin la llave no deja generar los códigos de nadie.
            usuario.SecretoDosFactor = cifrador.Cifrar(Convert.ToBase64String(secreto));
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                direccionQr    = SegundoFactor.DireccionParaQr(usuario.Correo, secreto, Emisor),
                secretoLegible = SegundoFactor.SecretoLegible(secreto),
                correo         = usuario.Correo,

                // El mismo secreto que va dentro del QR, en Base64.
                //
                // No es una filtración: se lo estamos dando a la única persona
                // a quien pertenece, que además acaba de verlo en el código de
                // barras. Lo usa el guion de pruebas para calcular códigos sin
                // un teléfono de por medio.
                secreto = Convert.ToBase64String(secreto)
            });
        })
        .RequiereSesionAMedias()
        .WithSummary("Generar el QR del segundo factor");


        // =====================================================================
        // 1b. CONFIRMAR: activar, y entregar los códigos de recuperación
        // =====================================================================
        grupo.MapPost("/confirmar", async (
            PeticionCodigo p,
            ContextoPlataforma db, ContextoPeticion ctx, Cifrador cifrador,
            AvisosDeSeguridad avisos,
            CancellationToken ct) =>
        {
            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == ctx.UsuarioId, ct);
            if (usuario is null) return Results.Unauthorized();

            if (usuario.DosfaActivo)
                return Results.BadRequest(new { error = "Ya estaba configurado." });

            if (usuario.SecretoDosFactor is null)
                return Results.BadRequest(new
                {
                    error = "No hay ningún QR pendiente. Vuelve a empezar.",
                    codigo = "sin_secreto"
                });

            var secreto = Convert.FromBase64String(cifrador.Descifrar(usuario.SecretoDosFactor));

            // `null` como último paso: al configurar no hay ninguno anterior.
            if (!SegundoFactor.Verificar(secreto, p.Codigo, null, out var paso))
                return MalElCodigo();

            usuario.DosfaActivo      = true;
            usuario.DosfaActivadoEn  = DateTime.UtcNow;
            usuario.DosfaUltimoPaso  = paso;

            // Los códigos viejos, si los había, dejan de valer.
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

            await MarcarSesionAsync(db, ctx, ct);
            await db.SaveChangesAsync(ct);
            await RegistrarAsync(db, TipoEvento.DosfaActivado, true, ctx, ct);

            // EL AVISO, aunque lo esté configurando su propio dueño ahora mismo.
            //
            // Parece redundante —acaba de escanear el QR, ya lo sabe— y no lo
            // es: si quien lo configuró fue un intruso con la contraseña
            // robada, este correo es lo ÚNICO que se lo cuenta al dueño. Y si
            // solo llegara en ese caso, la gente aprendería a ignorarlo.
            await avisos.SegundoFactorConfiguradoAsync(ctx.UsuarioId, ct);

            // SE VEN UNA VEZ. De la base solo sale su hash.
            return Results.Ok(new
            {
                codigosRecuperacion = codigos,
                aviso = "Guárdalos donde no se pierdan. No se pueden volver a consultar."
            });
        })
        .RequiereSesionAMedias()
        .WithSummary("Confirmar el segundo factor y recibir los códigos de recuperación");


        // =====================================================================
        // 2. VALIDAR EL CÓDIGO AL ENTRAR
        // =====================================================================
        grupo.MapPost("/", async (
            PeticionCodigo p,
            ContextoPlataforma db, ContextoPeticion ctx, Cifrador cifrador,
            CancellationToken ct) =>
        {
            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == ctx.UsuarioId, ct);
            if (usuario is null) return Results.Unauthorized();

            if (!usuario.DosfaActivo || usuario.SecretoDosFactor is null)
                return Results.BadRequest(new
                {
                    error = "Todavía no has configurado el segundo factor.",
                    codigo = "sin_configurar"
                });

            if (usuario.BloqueadoHasta is { } hasta && hasta > DateTime.UtcNow)
            {
                var faltan = (int)Math.Ceiling((hasta - DateTime.UtcNow).TotalMinutes);
                return Results.Json(
                    new { error = $"Cuenta bloqueada. Vuelve a intentarlo en {faltan} minutos." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var secreto = Convert.FromBase64String(cifrador.Descifrar(usuario.SecretoDosFactor));

            // EL ÚLTIMO PASO ES LO QUE IMPIDE REUTILIZAR UN CÓDIGO dentro de
            // sus treinta segundos.
            if (!SegundoFactor.Verificar(secreto, p.Codigo, usuario.DosfaUltimoPaso, out var paso))
            {
                usuario.IntentosFallidos++;

                var bloqueado = usuario.IntentosFallidos >= FallosParaBloquear;
                if (bloqueado)
                {
                    usuario.BloqueadoHasta   = DateTime.UtcNow.Add(Bloqueo);
                    usuario.IntentosFallidos = 0;
                }

                await db.SaveChangesAsync(ct);
                await RegistrarAsync(db, TipoEvento.DosfaFallido, false, ctx, ct);

                return bloqueado
                    ? Results.Json(new { error = "Demasiados intentos. Cuenta bloqueada 30 minutos." },
                                   statusCode: StatusCodes.Status401Unauthorized)
                    : MalElCodigo();
            }

            usuario.DosfaUltimoPaso  = paso;
            usuario.IntentosFallidos = 0;

            await MarcarSesionAsync(db, ctx, ct);
            await db.SaveChangesAsync(ct);
            await RegistrarAsync(db, TipoEvento.DosfaSuperado, true, ctx, ct);

            return Results.Ok(new { mensaje = "Listo." });
        })
        .RequiereSesionAMedias()
        .WithSummary("Validar el código de seis dígitos");


        // =====================================================================
        // 3. ENTRAR CON UN CÓDIGO DE RECUPERACIÓN
        //
        // No activa nada ni desactiva el factor: solo deja pasar esta vez. El
        // teléfono nuevo se configura después, desde el perfil, con la sesión
        // ya abierta.
        // =====================================================================
        grupo.MapPost("/recuperacion", async (
            PeticionCodigo p,
            ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            var ip = ctx.Ip?.ToString() ?? "0.0.0.0";

            var valido = await db.Database
                .SqlQuery<bool>($"""
                    SELECT usar_codigo_recuperacion(
                        {ctx.UsuarioId}::uuid,
                        {CodigosRecuperacion.Hashear(p.Codigo ?? "")},
                        {ip}::inet) AS "Value"
                    """)
                .FirstAsync(ct);

            if (!valido)
            {
                await RegistrarAsync(db, TipoEvento.DosfaFallido, false, ctx, ct);
                return Results.Json(
                    new { error = "Ese código de recuperación no vale o ya se usó." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            await MarcarSesionAsync(db, ctx, ct);
            await RegistrarAsync(db, TipoEvento.CodigoRecuperacionUsado, true, ctx, ct);

            var quedan = await db.Database
                .SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM codigos_recuperacion
                     WHERE usuario_id = {ctx.UsuarioId} AND usado_en IS NULL
                    """)
                .FirstAsync(ct);

            return Results.Ok(new
            {
                mensaje = "Entraste con un código de recuperación.",
                quedan,
                aviso = quedan <= 2
                    ? "Te quedan muy pocos. Reconfigura el segundo factor desde tu perfil."
                    : null
            });
        })
        .RequiereSesionAMedias()
        .WithSummary("Entrar con un código de recuperación");


        // =====================================================================
        // 4. CAMBIAR DE TELÉFONO, con la sesión ya abierta
        //
        // PIDE LA CONTRASEÑA. Sin eso, quien encuentre una sesión abierta en
        // un ordenador sin bloquear se pone el segundo factor en su propio
        // teléfono y se queda con la cuenta para siempre.
        // =====================================================================
        rutas.MapPost("/api/2fa/reiniciar-el-mio", async (
            PeticionReinicioPropio p,
            ContextoPlataforma db, ContextoPeticion ctx,
            AvisosDeSeguridad avisos,
            CancellationToken ct) =>
        {
            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == ctx.UsuarioId, ct);
            if (usuario is null) return Results.Unauthorized();

            if (!HashDeClaves.Verificar(p.Clave ?? "", usuario.ClaveHash, out _))
                return Results.Json(new { error = "La contraseña no es correcta." },
                                    statusCode: StatusCodes.Status401Unauthorized);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT reiniciar_segundo_factor({ctx.UsuarioId}::uuid)", ct);

            await RegistrarAsync(db, TipoEvento.DosfaDesactivado, true, ctx, ct);

            // ESTE ES EL AVISO MÁS IMPORTANTE DE LOS CUATRO. Borrar el segundo
            // factor es el último paso de quedarse con una cuenta ajena: quien
            // lo haga con la contraseña robada se pone el suyo al volver a
            // entrar, y el dueño ya no puede pasar del código de seis dígitos.
            await avisos.SegundoFactorBorradoAsync(ctx.UsuarioId, ct);

            // `reiniciar_segundo_factor` corta también las sesiones abiertas,
            // así que quien hizo esto tiene que volver a entrar y configurar.
            return Results.Ok(new
            {
                mensaje = "Segundo factor borrado. Vuelve a entrar para configurar el nuevo."
            });
        })
        .RequiereSesion()
        .WithTags("Segundo factor")
        .WithSummary("Reiniciar mi propio segundo factor");
    }


    // =========================================================================

    private static IResult MalElCodigo() =>
        Results.Json(new
        {
            error = "El código no coincide. Si tu teléfono tiene la hora desajustada, " +
                    "ponla en automática y prueba otra vez.",
            codigo = "codigo_invalido"
        },
        statusCode: StatusCodes.Status401Unauthorized);


    /// <summary>
    /// Marca la sesión como superada. A partir de aquí vale para todo.
    /// </summary>
    private static Task MarcarSesionAsync(
        ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE sesiones SET dosfa_superado = true
             WHERE id = {ctx.SesionId}
            """, ct);


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

public record PeticionReinicioPropio(string Clave);
