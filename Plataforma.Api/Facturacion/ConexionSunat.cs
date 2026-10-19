using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Arranque;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// CONEXIÓN CON SUNAT: MODO PRUEBA / MODO REAL (migración 030; diseño en
/// modo-prueba-y-real.md).
///
/// EN QA el super administrador tiene un interruptor:
///   · «SUNAT beta (pruebas)»: todas las empresas emiten contra beta. Nada
///     tiene valor legal. Es el modo de siempre.
///   · «SUNAT real»: SOLO las empresas que el super marca al cambiar pasan a
///     producción en el servicio y emiten de verdad. Las demás ven todo pero
///     no emiten mientras dure.
/// Volver a beta devuelve a beta a todas las que estaban en real.
///
/// EN PRODUCCIÓN (PLATAFORMA_SUNAT=real en el .env) no hay interruptor: queda
/// fijo en real, nadie lo cambia, no hay franja, y cada empresa pasa a real
/// sola al terminar el asistente (paso 6), con la misma revisión que el panel
/// (certificado real, usuario y clave SOL, series).
///
/// El servicio cambia el ambiente con DOS llaves: la fac_ de la empresa y la
/// clave de alta de la plataforma (FACTURACION_CLAVE_ALTA). Ninguna sola basta.
/// </summary>
public sealed class OpcionesConexionSunat
{
    /// <summary>Producción: siempre real y sin interruptor.</summary>
    public bool Fija { get; init; }

    public static OpcionesConexionSunat Leer() => new()
    {
        Fija = string.Equals(Entorno.Opcional("PLATAFORMA_SUNAT", "").Trim(), "real",
                             StringComparison.OrdinalIgnoreCase)
    };
}

public static class ConexionSunat
{
    public sealed record Estado(string Modo, bool Fija, DateTime? CambiadoEn, string? CambiadoPor);

    private sealed record Fila(string? Modo, DateTime? CambiadoEn, string? CambiadoPor);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<Estado> LeerAsync(
        ContextoPlataforma db, OpcionesConexionSunat opciones, CancellationToken ct)
    {
        var texto = await db.Database
            .SqlQuery<string>($"SELECT conexion_sunat() AS \"Value\"")
            .FirstAsync(ct);

        var f = JsonSerializer.Deserialize<Fila>(texto, Json);
        var modo = opciones.Fija ? "real" : (f?.Modo == "real" ? "real" : "beta");
        return new Estado(modo, opciones.Fija, f?.CambiadoEn, f?.CambiadoPor);
    }

    /// <summary>
    /// ¿Esta empresa puede emitir ahora? Null si sí; si no, el mensaje.
    ///
    /// En QA: en modo beta emiten las que están en beta; en modo real, solo
    /// las que el super pasó a real. En producción: solo las que ya están en
    /// real (las demás terminan antes el asistente).
    /// </summary>
    public static string? Bloqueo(Estado conexion, string? ambienteEmpresa)
    {
        var real = ambienteEmpresa == "produccion";

        if (conexion.Fija)
            return real ? null
                : "Tu empresa todavía no emite en SUNAT real. Termina la configuración (Configuración › paso 6) y vuelve a emitir.";

        if (conexion.Modo == "real")
            return real ? null
                : "En QA, el modo real es solo para las pruebas del desarrollador. Esta empresa no puede emitir ahora; " +
                  "puedes revisar todo lo demás. Cuando QA vuelva a modo prueba, podrás emitir otra vez.";

        return real
            ? "Esta empresa quedó en SUNAT real, pero la plataforma está en modo prueba. Pide a la administración que la devuelva a beta."
            : null;
    }

    /// <summary>Lo que dice el servicio de una empresa (GET /v1/configuracion/ambiente).</summary>
    public sealed record AmbienteDelServicio(
        string? Ambiente, bool PuedeProduccion, string[]? Faltantes, string[]? Advertencias);

    /// <summary>La respuesta del servicio a POST /v1/configuracion/ambiente.</summary>
    public sealed record CambioDelServicio(string? Ambiente, bool Cambiado, string? Mensaje);

    /// <summary>
    /// Pasa UNA empresa a «produccion» o «beta» en el servicio y guarda aquí
    /// su ambiente. En su propio ámbito, con el contexto de ESA empresa (la
    /// credencial solo se lee así), como la pausa.
    /// </summary>
    public static async Task<(bool Exito, string? Error, string? Detalle)> CambiarEmpresaAsync(
        IServiceScopeFactory ambitos, OpcionesServicioFacturacion servicio,
        Guid empresaId, string ambiente, string? quien, CancellationToken ct)
    {
        await using var ambito = ambitos.CreateAsyncScope();
        var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();
        var cliente = ambito.ServiceProvider.GetRequiredService<ClienteFacturacion>();

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        await db.FijarContextoAsync(null, empresaId, null, "conexión con SUNAT", false, ct);

        var r = await cliente.AmbienteAsync<CambioDelServicio>(
            HttpMethod.Post, new { ambiente, quien }, servicio.ClaveAlta, empresaId, ct);

        if (!r.Exito)
        {
            await transaccion.RollbackAsync(ct);
            return (false, r.Error, r.Detalle);
        }

        await db.Database
            .SqlQuery<int>($"SELECT fijar_ambiente_empresa({empresaId}, {ambiente}) AS \"Value\"")
            .FirstAsync(ct);
        await transaccion.CommitAsync(ct);
        return (true, null, null);
    }

    /// <summary>Lo que dice el servicio de una empresa, en su propio ámbito.</summary>
    public static async Task<(AmbienteDelServicio? Datos, string? Error)> RevisarEmpresaAsync(
        IServiceScopeFactory ambitos, OpcionesServicioFacturacion servicio,
        Guid empresaId, CancellationToken ct)
    {
        await using var ambito = ambitos.CreateAsyncScope();
        var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();
        var cliente = ambito.ServiceProvider.GetRequiredService<ClienteFacturacion>();

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        await db.FijarContextoAsync(null, empresaId, null, "conexión con SUNAT", false, ct);

        var r = await cliente.AmbienteAsync<AmbienteDelServicio>(
            HttpMethod.Get, null, servicio.ClaveAlta, empresaId, ct);

        await transaccion.CommitAsync(ct);
        return r.Exito ? (r.Datos, null) : (null, r.Error);
    }
}

public static class EndpointsConexionSunat
{
    public static void MapearConexionSunat(this IEndpointRouteBuilder rutas)
    {
        // La franja de arriba: la lee cualquiera con sesión.
        rutas.MapGet("/api/conexion-sunat", async (
            ContextoPlataforma db, OpcionesConexionSunat opciones, CancellationToken ct) =>
        {
            var e = await ConexionSunat.LeerAsync(db, opciones, ct);
            return Results.Ok(new { modo = e.Modo, fija = e.Fija, cambiadoEn = e.CambiadoEn, cambiadoPor = e.CambiadoPor });
        })
        .RequiereSesion()
        .WithTags("Conexión con SUNAT")
        .WithSummary("Modo prueba o real de esta instalación (030)");

        var admin = rutas.MapGroup("/api/admin/conexion-sunat").WithTags("Conexión con SUNAT");

        // Las empresas con facturación y lo que dice el servicio de cada una.
        admin.MapGet("/empresas", async (
            ContextoPlataforma db, IServiceScopeFactory ambitos,
            OpcionesServicioFacturacion servicio, CancellationToken ct) =>
        {
            var ids = await db.Database
                .SqlQuery<Guid>($"SELECT f.empresa_id AS \"Value\" FROM empresas_con_facturacion() f")
                .ToListAsync(ct);

            var empresas = await db.Empresas.AsNoTracking()
                .Where(e => ids.Contains(e.Id))
                .OrderBy(e => e.Nombre)
                .Select(e => new { e.Id, e.Nombre, e.Ruc, e.RazonSocial, e.Ambiente })
                .ToListAsync(ct);

            var filas = new List<object>();
            foreach (var e in empresas)
            {
                var (d, error) = await ConexionSunat.RevisarEmpresaAsync(ambitos, servicio, e.Id, ct);
                filas.Add(new
                {
                    e.Id, e.Nombre, e.Ruc, e.RazonSocial,
                    ambiente = d?.Ambiente ?? e.Ambiente,
                    puedeProduccion = d?.PuedeProduccion ?? false,
                    faltantes = d?.Faltantes ?? (error is null ? Array.Empty<string>() : new[] { error }),
                    advertencias = d?.Advertencias ?? Array.Empty<string>()
                });
            }

            return Results.Ok(filas);
        })
        .SoloSuperAdmin()
        .WithSummary("Empresas con facturación y si pueden pasar a SUNAT real");

        // El interruptor.
        admin.MapPost("", async (
            CambioConexionSunat p, ContextoPlataforma db, ContextoPeticion ctx,
            IServiceScopeFactory ambitos, OpcionesServicioFacturacion servicio,
            OpcionesConexionSunat opciones, CancellationToken ct) =>
        {
            if (opciones.Fija)
                return Results.Json(new { error = "En producción la conexión con SUNAT es fija: siempre real." },
                                    statusCode: StatusCodes.Status409Conflict);

            if (servicio.ProblemaAlta() is { } problema)
                return Results.Json(new { error = problema }, statusCode: StatusCodes.Status409Conflict);

            var modo = p.Modo?.Trim().ToLowerInvariant();
            if (modo is not ("beta" or "real"))
                return Results.BadRequest(new { error = "El modo es «beta» o «real»." });

            var quien = await db.Usuarios.AsNoTracking()
                .Where(u => u.Id == ctx.UsuarioId).Select(u => u.Correo).FirstOrDefaultAsync(ct);

            var resultados = new List<object>();
            var pasaron = 0;

            if (modo == "real")
            {
                var elegidas = (p.Empresas ?? []).Distinct().ToList();
                if (elegidas.Count == 0)
                    return Results.BadRequest(new { error = "Marca al menos una empresa para emitir en SUNAT real." });

                foreach (var id in elegidas)
                {
                    var (ok, error, detalle) = await ConexionSunat.CambiarEmpresaAsync(
                        ambitos, servicio, id, "produccion", quien, ct);
                    if (ok) pasaron++;
                    resultados.Add(new { empresaId = id, ok, error, detalle });
                }

                if (pasaron == 0)
                    return Results.Json(new
                    {
                        error = "Ninguna empresa pudo pasar a SUNAT real. La plataforma sigue en modo prueba.",
                        resultados
                    }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            else
            {
                // Volver a beta: TODAS las que estén en real.
                var ids = await db.Database
                    .SqlQuery<Guid>($"SELECT f.empresa_id AS \"Value\" FROM empresas_con_facturacion() f")
                    .ToListAsync(ct);
                var enReal = await db.Empresas.AsNoTracking()
                    .Where(e => ids.Contains(e.Id) && e.Ambiente == "produccion")
                    .Select(e => e.Id).ToListAsync(ct);

                foreach (var id in enReal)
                {
                    var (ok, error, detalle) = await ConexionSunat.CambiarEmpresaAsync(
                        ambitos, servicio, id, "beta", quien, ct);
                    resultados.Add(new { empresaId = id, ok, error, detalle });
                }
            }

            var estado = await db.Database
                .SqlQuery<string>($"SELECT cambiar_conexion_sunat({modo}) AS \"Value\"")
                .FirstAsync(ct);

            var e = await ConexionSunat.LeerAsync(db, opciones, ct);
            return Results.Ok(new
            {
                modo = e.Modo, fija = e.Fija, cambiadoEn = e.CambiadoEn, cambiadoPor = e.CambiadoPor,
                resultados,
                mensaje = e.Modo == "real"
                    ? "QA está en SUNAT real. Lo que emitan las empresas marcadas vale ante SUNAT."
                    : "QA volvió a modo prueba (SUNAT beta). Lo emitido en real sigue siendo real."
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Cambia la conexión con SUNAT (solo QA): beta o real con las empresas marcadas");
    }
}

/// <summary>Cuerpo de POST /api/admin/conexion-sunat.</summary>
public sealed record CambioConexionSunat(string? Modo, List<Guid>? Empresas);