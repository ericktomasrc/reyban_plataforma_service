using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Middleware;

/// <summary>
/// LA PIEZA DE RIESGO DE TODO EL BACK.
///
/// Abre una transacción, dice a PostgreSQL quién está preguntando, deja
/// correr la petición y confirma. De eso dependen dos cosas que no se ven:
/// el aislamiento entre empresas y la autoría de cada fila de la bitácora.
///
/// ────────────────────────────────────────────────────────────────────────
/// POR QUÉ LA TRANSACCIÓN NO ES NEGOCIABLE
///
/// `fijar_contexto` usa set_config(..., true), o sea local a la transacción.
/// Si una consulta corre fuera de una transacción explícita, cada sentencia
/// es su propia transacción: el contexto se fija en la primera y ha
/// desaparecido en la segunda.
///
/// Y el fallo no avisa. El aislamiento simplemente devuelve CERO FILAS, como
/// si la empresa no tuviera datos. Se busca el error en la consulta, en el
/// índice, en los datos — y está aquí.
///
/// Es local a la transacción a propósito: con un pool de conexiones, un valor
/// que sobreviviera se filtraría a la petición del siguiente usuario, que es
/// la misma clase de fallo que el aislamiento viene a evitar.
/// ────────────────────────────────────────────────────────────────────────
/// </summary>
public class MiddlewareContexto(RequestDelegate siguiente, ILogger<MiddlewareContexto> log)
{
    public const string NombreCookie = "plataforma_sesion";

    public async Task InvokeAsync(
        HttpContext http,
        ContextoPlataforma db,
        ContextoPeticion ctx)
    {
        var ct = http.RequestAborted;

        // Estas rutas no tocan la base. Abrirles una transacción sería gastar
        // una conexión del pool para no preguntar nada — y el healthcheck de
        // Docker llama cada treinta segundos, todo el día.
        if (SinBaseDeDatos(http.Request.Path))
        {
            await siguiente(http);
            return;
        }

        ctx.Ip = http.Connection.RemoteIpAddress;
        ctx.Agente = Recortar(http.Request.Headers.UserAgent.ToString(), 400);

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // --- 1. Contexto anónimo --------------------------------------------
        //
        // Se fija antes de saber quién es, para que la IP y el navegador estén
        // disponibles aunque la petición falle en el ingreso. Un intento
        // fallido sin IP no sirve para nada.
        await FijarAsync(db, ctx, ct);

        // --- 2. ¿Hay sesión? -------------------------------------------------
        var token = http.Request.Cookies[NombreCookie];
        if (!string.IsNullOrWhiteSpace(token))
            await IdentificarAsync(db, ctx, token, http, ct);

        // --- 3. Ahora sí, con los valores reales ----------------------------
        if (ctx.Autenticado)
            await FijarAsync(db, ctx, ct);

        await siguiente(http);

        // Sin commit no se guarda nada. Si la petición lanzó una excepción,
        // el `await using` deshace la transacción al salir — que es lo que se
        // quiere: una petición que falló a la mitad no deja medio cambio.
        await transaccion.CommitAsync(ct);
    }


    private static Task FijarAsync(ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        db.FijarContextoAsync(
            ctx.UsuarioId,
            ctx.EmpresaId,
            ctx.Ip?.ToString(),
            ctx.Agente,
            ctx.EsSuperAdmin,
            ct);


    private async Task IdentificarAsync(
        ContextoPlataforma db,
        ContextoPeticion ctx,
        string token,
        HttpContext http,
        CancellationToken ct)
    {
        var hash = GeneradorTokens.Hashear(token);

        // `resolver_sesion` es SECURITY DEFINER: para saber de quién es la
        // sesión hay que leerla ANTES de poder fijar el contexto. Es una de
        // las dos únicas funciones del sistema que se salta el aislamiento, y
        // se busca por el hash de 32 bytes aleatorios.
        var sesion = await db.SesionesResueltas
            .FromSqlInterpolated($"SELECT * FROM resolver_sesion({hash})")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (sesion is null)
        {
            BorrarCookie(http);
            return;
        }

        var ahora = DateTime.UtcNow;

        var motivo =
            sesion.RevocadaEn is not null ? "revocada"
            : sesion.ExpiraEn <= ahora ? "expirada"
            : !sesion.UsuarioActivo ? "usuario desactivado"
            : !sesion.EmpresaOperativa ? "empresa suspendida"
            : null;

        if (motivo is not null)
        {
            // No se registra evento aquí: una cookie caducada en una pestaña
            // olvidada dispararía uno en cada recarga, y la tabla de eventos
            // se llenaría de ruido que tapa lo que sí importa.
            log.LogDebug("Sesión no válida ({Motivo})", motivo);
            BorrarCookie(http);
            return;
        }

        // Los permisos se leen una vez por petición, no una vez por
        // comprobación. Aquí el contexto todavía es anónimo, así que la
        // consulta va por la puerta de la sesión: se filtra por usuario_id,
        // y la vista tiene security_invoker.
        await db.FijarContextoAsync(
            sesion.UsuarioId, sesion.EmpresaId,
            ctx.Ip?.ToString(), ctx.Agente,
            sesion.EsSuperAdmin, ct);

        var permisos = await db.PermisosEfectivos
            .Where(p => p.UsuarioId == sesion.UsuarioId)
            .Select(p => p.PermisoCodigo)
            .ToListAsync(ct);

        ctx.Identificar(
            sesion.UsuarioId,
            sesion.EmpresaId,
            sesion.SesionId,
            sesion.EsSuperAdmin,
            // Una sesión con 2FA pendiente existe, pero no vale para nada más
            // que para validar el código.
            sesion.DosfaSuperado,
            sesion.ClaveCambioForzado,
            permisos);

        // La última actividad se actualiza como mucho una vez por minuto.
        // Escribirla en cada petición convertiría cada GET en una escritura,
        // y llenaría la bitácora de filas que no dicen nada.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE sesiones SET ultima_actividad = now()
              WHERE id = {sesion.SesionId}
                AND ultima_actividad < now() - interval '1 minute'
             """, ct);
    }


    private static bool SinBaseDeDatos(PathString ruta) =>
        ruta.StartsWithSegments("/salud") ||   // healthcheck de Docker
        ruta.StartsWithSegments("/openapi") ||   // el documento OpenAPI
        ruta.StartsWithSegments("/swagger");      // su visor


    private static void BorrarCookie(HttpContext http) =>
        http.Response.Cookies.Delete(NombreCookie);

    private static string? Recortar(string? texto, int largo) =>
        string.IsNullOrEmpty(texto) ? null
        : texto.Length <= largo ? texto
        : texto[..largo];
}