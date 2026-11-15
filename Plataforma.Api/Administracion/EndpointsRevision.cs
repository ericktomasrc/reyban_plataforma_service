using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;
using PermisosPlataforma = global::Plataforma.Core.Dominio.Permisos.Plataforma;

namespace Plataforma.Api.Administracion;

public sealed record PeticionRevisionNueva(string? Usuario, string? Para, string? Tipo, int? Dias, bool? EnPrueba, string? Clave = null);
public sealed record PeticionRevisionClave(string? Clave);
public sealed record PeticionRevisionDias(int? Dias);
public sealed record PeticionRevisionPrueba(bool? Activa);

/// <summary>
/// 089 — ACCESOS DE REVISIÓN.
///
/// Para que alguien de fuera (Culqi, un banco, un socio) pruebe la plataforma
/// como un cliente: entra con usuario y contraseña, sin correo y sin segundo
/// factor. El super administrador lo crea aquí, entra él mismo con ese usuario
/// para crear la empresa (con el asistente de siempre) y se lo pasa.
///
///   GET  /api/admin/revision                    la lista
///   POST /api/admin/revision                    crear (con la contraseña que mostró la pantalla)
///   POST /api/admin/revision/{id}/apagar        deja de entrar y se cierran sus sesiones
///   POST /api/admin/revision/{id}/alargar       N días más desde hoy (y lo vuelve a encender)
///   POST /api/admin/revision/{id}/clave         contraseña nueva (devuelta una vez)
///   POST /api/admin/revision/{id}/prueba        terminar la prueba gratis o darla de nuevo
///
/// La contraseña la genera el servidor y solo se guarda su hash.
/// </summary>
public static class EndpointsRevision
{
    // Sin letras que se confunden (0/O, 1/l/I): se dicta o se copia a un ticket.
    private const string Letras = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// e241: la pantalla genera la contraseña (al azar, en el navegador) y la
    /// muestra para copiarla ANTES de guardar. Aquí solo se revisa que sea
    /// fuerte: 12 a 64 caracteres, sin espacios. Si no llega, se genera aquí.
    /// </summary>
    private static string? ProblemaClave(string clave) =>
        clave.Length is < 12 or > 64 ? "La contraseña va de 12 a 64 caracteres."
        : clave.Any(char.IsWhiteSpace) ? "La contraseña no lleva espacios."
        : null;

    private static string ClaveNueva() =>
        string.Join('-', Enumerable.Range(0, 3).Select(_ => RandomNumberGenerator.GetString(Letras, 5)));

    public static void MapearRevision(this IEndpointRouteBuilder rutas)
    {
        var g = rutas.MapGroup("/api/admin/revision").WithTags("Administración · accesos de revisión");

        g.MapGet("/", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"SELECT revision_lista()::text AS \"Value\"").FirstAsync(ct),
                            "application/json"))
            .SoloSuperAdmin(PermisosPlataforma.CuentasGestionar)
            .WithSummary("Los accesos de revisión");

        g.MapPost("/", async (PeticionRevisionNueva p, ContextoPlataforma db, ILoggerFactory logs, CancellationToken ct) =>
        {
            var usuario = (p.Usuario ?? "").Trim().ToLowerInvariant();
            var pedida = p.Clave?.Trim();
            var clave = string.IsNullOrEmpty(pedida) ? ClaveNueva() : pedida;
            if (ProblemaClave(clave) is { } mal) return Results.BadRequest(new { error = mal });
            try
            {
                var id = await db.Database.SqlQuery<Guid>($"""
                    SELECT revision_crear({usuario}, {p.Para ?? ""}, {p.Tipo ?? ""}, {p.Dias ?? 0},
                                          {p.EnPrueba ?? false}, {HashDeClaves.Cifrar(clave)}) AS "Value"
                    """).FirstAsync(ct);
                logs.CreateLogger("Revision").LogInformation("Acceso de revisión creado: {Usuario} ({Cuenta}).", usuario, id);
                return Results.Ok(new { id, usuario, clave });
            }
            catch (Exception e) when (Motivo(e) is { } m)
            {
                return Results.BadRequest(new { error = m });
            }
        })
        .SoloSuperAdmin(PermisosPlataforma.CuentasGestionar)
        .WithSummary("Crear un acceso de revisión (la contraseña se muestra una sola vez)");

        g.MapPost("/{id:guid}/apagar", (Guid id, ContextoPlataforma db, CancellationToken ct) =>
            Cambiar(db, id, "apagar", null, null, "Acceso apagado: ya no puede entrar y se cerraron sus sesiones.", ct))
            .SoloSuperAdmin(PermisosPlataforma.CuentasGestionar)
            .WithSummary("Apagar un acceso de revisión");

        g.MapPost("/{id:guid}/alargar", (Guid id, PeticionRevisionDias p, ContextoPlataforma db, CancellationToken ct) =>
            Cambiar(db, id, "alargar", p.Dias ?? 0, null, $"Listo: puede entrar {p.Dias} día(s) más desde hoy.", ct))
            .SoloSuperAdmin(PermisosPlataforma.CuentasGestionar)
            .WithSummary("Alargar (o volver a encender) un acceso de revisión");

        g.MapPost("/{id:guid}/clave", async (Guid id, PeticionRevisionClave? p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var pedida = p?.Clave?.Trim();
            var clave = string.IsNullOrEmpty(pedida) ? ClaveNueva() : pedida;
            if (ProblemaClave(clave) is { } mal) return Results.BadRequest(new { error = mal });
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT revision_cambiar({id}, 'clave', NULL::int, {HashDeClaves.Cifrar(clave)}::text)", ct);
                return Results.Ok(new { clave });
            }
            catch (Exception e) when (Motivo(e) is { } m)
            {
                return Results.BadRequest(new { error = m });
            }
        })
        .SoloSuperAdmin(PermisosPlataforma.CuentasGestionar)
        .WithSummary("Contraseña nueva para un acceso de revisión (se muestra una sola vez)");

        g.MapPost("/{id:guid}/prueba", (Guid id, PeticionRevisionPrueba p, ContextoPlataforma db, CancellationToken ct) =>
            p.Activa == true
                ? Cambiar(db, id, "dar_prueba", null, null, "Listo: vuelve a estar en prueba gratis (todo abierto).", ct)
                : Cambiar(db, id, "terminar_prueba", null, null, "Listo: la prueba gratis terminó. Verá los candados y tendrá que elegir un plan.", ct))
            .SoloSuperAdmin(PermisosPlataforma.CuentasGestionar)
            .WithSummary("Terminar la prueba gratis de un acceso de revisión, o darla de nuevo");
    }

    private static async Task<IResult> Cambiar(ContextoPlataforma db, Guid id, string accion, int? dias, string? hash,
                                               string mensaje, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT revision_cambiar({id}, {accion}, {dias}::int, {hash}::text)", ct);
            return Results.Ok(new { mensaje });
        }
        catch (Exception e) when (Motivo(e) is { } m)
        {
            return Results.BadRequest(new { error = m });
        }
    }

    /// <summary>Los avisos de la base («REVISION: …»), en limpio.</summary>
    private static string? Motivo(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is PostgresException pg && pg.MessageText.StartsWith("REVISION:", StringComparison.Ordinal))
                return pg.MessageText["REVISION:".Length..].Trim();
        return null;
    }
}