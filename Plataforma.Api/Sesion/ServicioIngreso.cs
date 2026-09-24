using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Sesion;

public record ResultadoIngreso(
    bool Exito,
    string? Token = null,
    DateTime? Expira = null,
    bool SegundoFactorPendiente = false,
    bool CambioDeClaveForzado = false,
    string? Mensaje = null);


/// <summary>
/// El ingreso. Es la pantalla más atacada de cualquier sistema, así que cada
/// decisión de aquí tiene un motivo.
/// </summary>
public class ServicioIngreso(ContextoPlataforma db, ContextoPeticion ctx, ILogger<ServicioIngreso> log)
{
    /// <summary>Tras cinco fallos, media hora de espera.</summary>
    private const int FallosParaBloquear = 5;
    private static readonly TimeSpan Bloqueo   = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DuraSesion = TimeSpan.FromHours(12);

    /// <summary>
    /// UN SOLO MENSAJE PARA TODOS LOS FALLOS.
    ///
    /// Si dijera «ese correo no existe» y «contraseña incorrecta» por
    /// separado, cualquiera podría averiguar qué correos están dados de alta
    /// probándolos uno a uno. Eso es media intrusión hecha.
    ///
    /// El motivo real sí queda, en eventos_seguridad, donde lo ve el
    /// administrador y no el que lo está intentando.
    /// </summary>
    private const string MensajeGenerico = "Correo o contraseña incorrectos.";


    public async Task<ResultadoIngreso> IngresarAsync(
        string correo, string clave, CancellationToken ct)
    {
        // `buscar_usuario_para_ingreso` es SECURITY DEFINER: en este momento
        // todavía no se sabe de qué empresa es quien llama, así que la tabla
        // `usuarios` está vacía para esta consulta.
        var u = await db.UsuariosParaIngreso
            .FromSqlInterpolated($"SELECT * FROM buscar_usuario_para_ingreso({correo})")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (u is null)
        {
            await RegistrarAsync(TipoEvento.IngresoUsuarioInexistente, false, correo: correo, ct: ct);
            return new(false, Mensaje: MensajeGenerico);
        }

        // A PARTIR DE AQUÍ SE SABE QUIÉN ES, así que se fija el contexto.
        //
        // Aunque la contraseña acabe siendo incorrecta: hace falta para poder
        // escribir el contador de intentos fallidos —la tabla está protegida
        // por el aislamiento— y para que la bitácora sepa a quién atribuir el
        // cambio.
        await db.FijarContextoAsync(
            u.Id, u.EmpresaId, ctx.Ip?.ToString(), ctx.Agente, u.EsSuperAdmin, ct);

        if (!u.Activo)
        {
            await RegistrarAsync(TipoEvento.IngresoUsuarioInactivo, false, u.Id, u.EmpresaId, correo, ct);
            return new(false, Mensaje: MensajeGenerico);
        }

        if (!u.EmpresaOperativa)
        {
            await RegistrarAsync(TipoEvento.IngresoEmpresaSuspendida, false, u.Id, u.EmpresaId, correo, ct);
            return new(false, Mensaje: "Tu empresa está suspendida. Habla con tu administrador.");
        }

        if (u.BloqueadoHasta is not null && u.BloqueadoHasta > DateTime.UtcNow)
        {
            await RegistrarAsync(TipoEvento.IngresoUsuarioBloqueado, false, u.Id, u.EmpresaId, correo, ct);

            var faltan = (int)Math.Ceiling((u.BloqueadoHasta.Value - DateTime.UtcNow).TotalMinutes);
            return new(false, Mensaje: $"Cuenta bloqueada. Vuelve a intentarlo en {faltan} minutos.");
        }

        if (!HashDeClaves.Verificar(clave, u.ClaveHash, out var necesitaRecifrado))
        {
            await ContarFalloAsync(u, correo, ct);
            return new(false, Mensaje: MensajeGenerico);
        }

        // --- Contraseña correcta --------------------------------------------

        var usuario = await db.Usuarios.FirstAsync(x => x.Id == u.Id, ct);

        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHasta   = null;
        usuario.UltimoIngresoEn  = DateTime.UtcNow;
        usuario.UltimoIngresoIp  = ctx.Ip;

        // ESTE ES EL ÚNICO MOMENTO EN QUE SE TIENE LA CONTRASEÑA EN CLARO.
        //
        // Si el hash guardado usa menos iteraciones que las actuales, es ahora
        // o nunca: recifrarla aquí es lo que permite subir el número con los
        // años sin invalidar las contraseñas de todo el mundo.
        if (necesitaRecifrado)
        {
            usuario.ClaveHash = HashDeClaves.Cifrar(clave);
            log.LogInformation("Contraseña recifrada con {N} iteraciones", HashDeClaves.IteracionesActuales);
        }

        var (token, hash) = GeneradorTokens.Nuevo();
        var expira = DateTime.UtcNow.Add(DuraSesion);

        db.Sesiones.Add(new Core.Dominio.Sesion
        {
            UsuarioId       = u.Id,
            EmpresaId       = u.EmpresaId,
            TokenHash       = hash,
            CreadaEn        = DateTime.UtcNow,
            ExpiraEn        = expira,
            UltimaActividad = DateTime.UtcNow,
            Ip              = ctx.Ip,
            Agente          = ctx.Agente,
            // La sesión nace incompleta si el usuario tiene segundo factor.
            DosfaSuperado   = !u.DosfaActivo
        });

        await db.SaveChangesAsync(ct);
        await RegistrarAsync(TipoEvento.Ingreso, true, u.Id, u.EmpresaId, correo, ct);

        return new ResultadoIngreso(
            Exito: true,
            Token: token,
            Expira: expira,
            SegundoFactorPendiente: u.DosfaActivo,
            CambioDeClaveForzado: u.ClaveCambioForzado);
    }


    public async Task SalirAsync(Guid sesionId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE sesiones
               SET revocada_en = now(), motivo_revocacion = 'cierre de sesion'
             WHERE id = {sesionId} AND revocada_en IS NULL
            """, ct);

        await RegistrarAsync(TipoEvento.CierreSesion, true, ctx.UsuarioId, ctx.EmpresaId, ct: ct);
    }


    private async Task ContarFalloAsync(UsuarioParaIngreso u, string correo, CancellationToken ct)
    {
        var usuario = await db.Usuarios.FirstAsync(x => x.Id == u.Id, ct);
        usuario.IntentosFallidos++;

        var bloqueado = usuario.IntentosFallidos >= FallosParaBloquear;
        if (bloqueado)
        {
            usuario.BloqueadoHasta = DateTime.UtcNow.Add(Bloqueo);
            usuario.IntentosFallidos = 0;
        }

        await db.SaveChangesAsync(ct);

        await RegistrarAsync(
            TipoEvento.IngresoClaveIncorrecta, false, u.Id, u.EmpresaId, correo, ct,
            detalle: bloqueado ? """{"bloqueado": true}""" : """{"bloqueado": false}""");
    }


    private Task RegistrarAsync(
        string tipo, bool exito,
        Guid? usuarioId = null, Guid? empresaId = null, string? correo = null,
        CancellationToken ct = default, string? detalle = null)
    {
        // Los identificadores viajan como texto y se convierten en SQL, no
        // como Guid?. Con un valor nulo, PostgreSQL no puede deducir el tipo
        // del parámetro y responde «could not determine data type» — un error
        // que aparece solo en el caso nulo y desconcierta bastante.
        var usuario = usuarioId?.ToString();
        var empresa = empresaId?.ToString();

        return db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT registrar_evento(
                {tipo}::text, {exito}::boolean,
                {usuario}::uuid, {empresa}::uuid, {correo}::text,
                {detalle}::jsonb)
            """, ct);
    }
}
