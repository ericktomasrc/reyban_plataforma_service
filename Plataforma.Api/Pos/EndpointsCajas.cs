using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Pos;

/// <summary>
/// e278 (093) — LAS CAJAS DEL PUNTO DE VENTA: crear, nombrar y desactivar.
///
///   GET  /api/pos/config-cajas                  las cajas de cada sucursal que la persona puede ver
///   POST /api/pos/config-cajas                  { sucursalId, nombre }   crear
///   PUT  /api/pos/config-cajas/{id}             { nombre }               renombrar
///   POST /api/pos/config-cajas/{id}/activo      { activo }               desactivar / reactivar
///
/// Ver: pos.cajas_ver o pos.cajas_gestionar. Cambiar: pos.cajas_gestionar.
/// Una caja con una apertura en curso no se desactiva. Nada se borra.
/// </summary>
public static class EndpointsCajas
{
    private const string Ver = "pos.cajas_ver";
    private const string Gestionar = "pos.cajas_gestionar";

    public sealed record PeticionCaja(Guid? SucursalId, string? Nombre);
    public sealed record PeticionActivo(bool Activo);

    public static void MapearCajas(this IEndpointRouteBuilder rutas)
    {
        var g = rutas.MapGroup("/api/pos/config-cajas").WithTags("Punto de venta");

        g.MapGet("", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (!ctx.Tiene(Ver) && !ctx.Tiene(Gestionar)) return Results.Json(new { error = "No tienes permiso para ver las cajas." }, statusCode: StatusCodes.Status403Forbidden);
            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
            var todas = permitidas is null;
            var ids = permitidas?.ToArray() ?? [];
            var json = await db.Database.SqlQuery<string>($"""
                SELECT coalesce(json_agg(x ORDER BY x.codigo), '[]'::json)::text AS "Value" FROM (
                    SELECT su.id, su.codigo, su.nombre,
                           (SELECT coalesce(json_agg(json_build_object(
                                       'id', k.id, 'nombre', k.nombre, 'activo', k.activo, 'creadoEn', k.creado_en,
                                       'abiertaPor', (SELECT u.nombre FROM pos_sesiones s JOIN usuarios u ON u.id = s.usuario_id
                                                       WHERE s.caja_id = k.id AND s.cerrada_en IS NULL LIMIT 1),
                                       'aperturas', (SELECT count(*) FROM pos_sesiones s WHERE s.caja_id = k.id),
                                       'ultimoUso', (SELECT max(s.abierta_en) FROM pos_sesiones s WHERE s.caja_id = k.id))
                                     ORDER BY k.activo DESC, k.nombre), '[]'::json)
                              FROM pos_cajas k WHERE k.sucursal_id = su.id) AS cajas
                      FROM sucursales su
                     WHERE su.activo AND ({todas} OR su.id = ANY({ids}))
                ) x
                """).FirstAsync(ct);
            return Results.Content($$"""{"puedeCambiar":{{(ctx.Tiene(Gestionar) ? "true" : "false")}},"sucursales":{{json}}}""", "application/json");
        })
        .RequiereSesion()
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo("pos")
        .WithSummary("Las cajas de cada sucursal, con quién la tiene abierta y su último uso");

        g.MapPost("", async (PeticionCaja p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var nombre = Limpiar(p.Nombre);
            if (nombre is null) return Mal("Escribe el nombre de la caja (de 1 a 60 letras).");
            if (p.SucursalId is not { } suc) return Mal("Elige la sucursal.");
            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
            if (permitidas is not null && !permitidas.Contains(suc)) return Mal("No trabajas en esa sucursal.");
            var existe = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM sucursales WHERE id = {suc} AND activo) AS "Value"
                """).FirstAsync(ct);
            if (!existe) return Mal("Esa sucursal no existe o está desactivada.");
            if (await Repetida(db, suc, nombre, null, ct)) return Mal($"Ya hay una caja «{nombre}» en esa sucursal.");
            var id = (await db.Database.SqlQuery<Guid>($"""
                INSERT INTO pos_cajas (empresa_id, sucursal_id, nombre)
                VALUES ({ctx.EmpresaId!.Value}, {suc}, {nombre}) RETURNING id AS "Value"
                """).ToListAsync(ct)).Single();
            return Results.Ok(new { id, mensaje = $"Caja «{nombre.ToUpperInvariant()}» creada." });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo("pos")
        .WithSummary("Crear una caja en una sucursal");

        g.MapPut("/{id:guid}", async (Guid id, PeticionCaja p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var nombre = Limpiar(p.Nombre);
            if (nombre is null) return Mal("Escribe el nombre de la caja (de 1 a 60 letras).");
            var suc = await SucursalDe(db, ctx, id, ct);
            if (suc is null) return Results.NotFound(new { error = "Esa caja no existe o no es de tus sucursales." });
            if (await Repetida(db, suc.Value, nombre, id, ct)) return Mal($"Ya hay una caja «{nombre}» en esa sucursal.");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pos_cajas SET nombre = {nombre} WHERE id = {id}", ct);
            return Results.Ok(new { mensaje = $"La caja ahora se llama «{nombre.ToUpperInvariant()}». Sus aperturas y arqueos siguen igual." });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo("pos")
        .WithSummary("Cambiar el nombre de una caja");

        g.MapPost("/{id:guid}/activo", async (Guid id, PeticionActivo p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var suc = await SucursalDe(db, ctx, id, ct);
            if (suc is null) return Results.NotFound(new { error = "Esa caja no existe o no es de tus sucursales." });
            if (!p.Activo)
            {
                var abierta = await db.Database.SqlQuery<string?>($"""
                    SELECT u.nombre AS "Value" FROM pos_sesiones s JOIN usuarios u ON u.id = s.usuario_id
                     WHERE s.caja_id = {id} AND s.cerrada_en IS NULL LIMIT 1
                    """).FirstOrDefaultAsync(ct);
                if (abierta is not null)
                    return Results.Conflict(new { error = $"La tiene abierta {abierta}. Que la cierre primero; después la desactivas." });
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pos_cajas SET activo = {p.Activo} WHERE id = {id}", ct);
            return Results.Ok(new
            {
                mensaje = p.Activo ? "Caja reactivada: ya se puede abrir en el punto de venta."
                                   : "Caja desactivada: ya no sale para abrir. Sus arqueos se guardan."
            });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo("pos")
        .WithSummary("Desactivar o reactivar una caja");
    }

    private static IResult Mal(string error) => Results.BadRequest(new { error });

    private static string? Limpiar(string? s)
    {
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
        return t.Length > 60 ? null : t;
    }

    private static Task<bool> Repetida(ContextoPlataforma db, Guid sucursal, string nombre, Guid? excepto, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM pos_cajas WHERE sucursal_id = {sucursal} AND lower(btrim(nombre)) = lower(btrim({nombre}))
                                                     AND ({excepto}::uuid IS NULL OR id <> {excepto})) AS "Value"
            """).FirstAsync(ct);

    /// <summary>La sucursal de la caja, si es de esta empresa (RLS) y de las sucursales de la persona.</summary>
    private static async Task<Guid?> SucursalDe(ContextoPlataforma db, ContextoPeticion ctx, Guid caja, CancellationToken ct)
    {
        var suc = await db.Database.SqlQuery<Guid?>($"SELECT sucursal_id AS \"Value\" FROM pos_cajas WHERE id = {caja}").FirstOrDefaultAsync(ct);
        if (suc is null) return null;
        var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
        return permitidas is null || permitidas.Contains(suc.Value) ? suc : null;
    }
}