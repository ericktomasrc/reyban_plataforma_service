using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

public record PeticionSuspenderCuenta(string? Motivo);
public record PeticionTopeCuenta(int Tope);

/// <summary>
/// «Cuentas»: las que se registraron solas y las que nacieron con cada empresa
/// (migración 016). Solo el super administrador.
///
/// SUSPENDER NO BORRA NADA: la cuenta, sus usuarios y sus empresas siguen ahí.
/// Lo que pasa es que nadie de esa cuenta entra (lo miran las funciones de
/// ingreso y de sesión) y las sesiones abiertas se cortan EN ESE MOMENTO.
///
/// SIN EF, con SQL a la vista: `cuentas` no es una entidad del modelo y la
/// lista se arma en la base como JSON, en una sola consulta.
/// </summary>
public static class EndpointsCuentas
{
    public static void MapearCuentas(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/admin/cuentas").WithTags("Cuentas");

        // ---------------------------------------------------------------------
        grupo.MapGet("/", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database.SqlQuery<string>($"""
                SELECT coalesce(json_agg(x ORDER BY x."creadoEn" DESC), '[]'::json)::text AS "Value"
                  FROM (
                    SELECT c.id                     AS "id",
                           c.tipo                   AS "tipo",
                           c.estado                 AS "estado",
                           c.origen                 AS "origen",
                           c.titular_tipo           AS "titularTipo",
                           c.titular_documento      AS "titularDocumento",
                           coalesce(c.titular_nombre,
                                    (SELECT e.nombre FROM empresas e
                                      WHERE e.cuenta_id = c.id ORDER BY e.creado_en LIMIT 1))
                                                    AS "titularNombre",
                           c.tope_empresas_dia      AS "tope",
                           c.creado_en              AS "creadoEn",
                           c.confirmada_en          AS "confirmadaEn",
                           c.suspendida_en          AS "suspendidaEn",
                           c.motivo_suspension      AS "motivoSuspension",
                           (SELECT count(*) FROM empresas e
                             WHERE e.cuenta_id = c.id AND e.activo)::int AS "empresas",
                           (SELECT json_build_object('correo', u.correo, 'alias', u.alias, 'nombre', u.nombre)
                              FROM usuarios u
                             WHERE u.cuenta_id = c.id
                             ORDER BY u.creado_en LIMIT 1) AS "usuario"
                      FROM cuentas c
                     WHERE c.activo
                     ORDER BY c.creado_en DESC
                     LIMIT 500
                  ) x
                """).FirstAsync(ct);

            return Results.Content(json, "application/json");
        })
        .SoloSuperAdmin()
        .WithSummary("Lista las cuentas (las últimas 500)");


        // ---------------------------------------------------------------------
        grupo.MapPost("/{id:guid}/suspender", async (
            Guid id, PeticionSuspenderCuenta p,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var motivo = string.Join(' ', (p.Motivo ?? "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            // El motivo es lo único que explicará dentro de seis meses por qué
            // esta cuenta no entra. Igual que al suspender una empresa.
            if (motivo.Length is < 5 or > 500)
                return Results.BadRequest(new { error = "Escribe el motivo (de 5 a 500 caracteres)." });

            var filas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cuentas
                   SET estado = 'suspendida', suspendida_en = now(), motivo_suspension = {motivo}
                 WHERE id = {id} AND estado <> 'suspendida'
                """, ct);

            if (filas == 0)
                return Results.NotFound(new { error = "No existe esa cuenta, o ya estaba suspendida." });

            // LAS SESIONES SE CORTAN AHORA. Sin esto, quien ya estaba dentro
            // seguiría hasta que caduque su sesión (12 horas).
            var cortadas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE sesiones
                   SET revocada_en = now(), motivo_revocacion = 'cuenta suspendida'
                 WHERE revocada_en IS NULL
                   AND usuario_id IN (SELECT u.id FROM usuarios u WHERE u.cuenta_id = {id})
                """, ct);

            await Evento(db, "cuenta_suspendida", ctx.AutorId,
                $"{{\"cuenta\":\"{id}\",\"sesionesCortadas\":{cortadas}}}", ct);

            return Results.Ok(new { mensaje = "Cuenta suspendida. Sus sesiones se cerraron.", sesionesCortadas = cortadas });
        })
        .SoloSuperAdmin()
        .WithSummary("Suspender una cuenta (con motivo; corta sus sesiones)");


        // ---------------------------------------------------------------------
        grupo.MapPost("/{id:guid}/reactivar", async (
            Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            // Vuelve a como estaba: activa si ya había confirmado su correo,
            // pendiente si no.
            var filas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cuentas
                   SET estado = CASE WHEN confirmada_en IS NULL THEN 'pendiente' ELSE 'activa' END,
                       suspendida_en = NULL, motivo_suspension = NULL
                 WHERE id = {id} AND estado = 'suspendida'
                """, ct);

            if (filas == 0)
                return Results.NotFound(new { error = "No existe esa cuenta, o no estaba suspendida." });

            await Evento(db, "cuenta_reactivada", ctx.AutorId, $"{{\"cuenta\":\"{id}\"}}", ct);

            return Results.Ok(new { mensaje = "Cuenta reactivada." });
        })
        .SoloSuperAdmin()
        .WithSummary("Reactivar una cuenta suspendida");


        // ---------------------------------------------------------------------
        grupo.MapPut("/{id:guid}/tope", async (
            Guid id, PeticionTopeCuenta p,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (p.Tope is < 1 or > 10000)
                return Results.BadRequest(new { error = "El tope va de 1 a 10 000 empresas por día." });

            var filas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cuentas SET tope_empresas_dia = {p.Tope} WHERE id = {id}
                """, ct);

            if (filas == 0)
                return Results.NotFound(new { error = "No existe esa cuenta." });

            await Evento(db, "cuenta_tope_cambiado", ctx.AutorId,
                $"{{\"cuenta\":\"{id}\",\"tope\":{p.Tope}}}", ct);

            return Results.Ok(new { mensaje = $"Tope cambiado a {p.Tope} empresas por día." });
        })
        .SoloSuperAdmin()
        .WithSummary("Cambiar el tope de empresas nuevas por día de una cuenta");
    }

    private static Task Evento(
        ContextoPlataforma db, string tipo, Guid? autor, string detalle, CancellationToken ct)
    {
        var usuario = autor?.ToString();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT registrar_evento({tipo}::text, true, {usuario}::uuid, NULL, NULL, {detalle}::jsonb)
            """, ct);
    }
}