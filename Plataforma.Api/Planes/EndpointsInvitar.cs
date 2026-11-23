using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Planes;

/// <summary>
/// e281 (096) — PREMIO POR INVITAR CONTADORES.
///
///   GET  /api/planes/invitar                 mi enlace, a quién invité y mis premios
///   POST /api/planes/invitar/anotar          { codigo }  «me invitó…» (primeros 45 días)
///   GET  /api/admin/planes/invitar           reglas y quién trajo a quién
///   PUT  /api/admin/planes/invitar/reglas    las reglas del premio
///
/// El premio lo da el ciclo de cobros (invitar_dar_premios) cuando el
/// invitado ya pagó los meses pedidos. Nada se borra.
/// </summary>
public static class EndpointsInvitar
{
    public sealed record PeticionAnotar(string? Codigo);
    public sealed record PeticionReglasInvitar(bool Activo, int PremioMeses, int MesesPagados, int TopeAnual, int EmpresasMax);

    public static void MapearInvitar(this IEndpointRouteBuilder rutas)
    {
        rutas.MapGet("/api/planes/invitar", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            // El código se crea la primera vez que la Cuenta Profesional lo pide.
            var tipo = await db.Database.SqlQuery<string?>($"""
                SELECT c.tipo AS "Value" FROM usuarios u JOIN cuentas c ON c.id = u.cuenta_id WHERE u.id = ctx_usuario()
                """).FirstOrDefaultAsync(ct);
            if (tipo == "profesional")
                await db.Database.SqlQuery<string>($"SELECT invitar_mi_codigo() AS \"Value\"").FirstAsync(ct);
            var json = await db.Database.SqlQuery<string?>($"SELECT invitar_mio()::text AS \"Value\"").FirstAsync(ct);
            return json is null ? Results.NotFound(new { error = "No tienes una cuenta." }) : Results.Content(json, "application/json");
        })
        .RequiereSesion()
        .WithTags("Planes")
        .WithSummary("Mi enlace para invitar contadores, a quién invité y mis premios");

        rutas.MapPost("/api/planes/invitar/anotar", async (PeticionAnotar p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var nombre = await db.Database.SqlQuery<string?>($"SELECT invitar_anotar({p.Codigo}) AS \"Value\"").FirstAsync(ct);
            return Results.Ok(new { mensaje = $"Listo: quedó anotado que te invitó {nombre ?? "otro contador"}. ¡Bienvenido!" });
        })
        .RequiereSesion()
        .WithTags("Planes")
        .WithSummary("Anotar quién me invitó (código de invitación)");

        rutas.MapGet("/api/admin/planes/invitar", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database.SqlQuery<string>($"SELECT invitar_lista_admin()::text AS \"Value\"").FirstAsync(ct);
            return Results.Content(json, "application/json");
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
        .WithTags("Administración · planes")
        .WithSummary("Premios por invitar: reglas y quién trajo a quién");

        rutas.MapPut("/api/admin/planes/invitar/reglas", async (PeticionReglasInvitar p, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (p.PremioMeses is < 1 or > 12) return Results.BadRequest(new { error = "El premio va de 1 a 12 meses." });
            if (p.MesesPagados is < 1 or > 24) return Results.BadRequest(new { error = "Los meses que debe pagar el nuevo van de 1 a 24." });
            if (p.TopeAnual is < 0 or > 24) return Results.BadRequest(new { error = "El tope al año va de 0 a 24 meses." });
            if (p.EmpresasMax is < 1 or > 10000) return Results.BadRequest(new { error = "Las empresas que cubre el premio van de 1 a 10000." });
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE plataforma_ajustes
                   SET invitar_activo = {p.Activo}, invitar_premio_meses = {p.PremioMeses}, invitar_meses_pagados = {p.MesesPagados},
                       invitar_tope_anual = {p.TopeAnual}, invitar_empresas_max = {p.EmpresasMax}
                 WHERE id = 1
                """, ct);
            return Results.Ok(new { mensaje = p.Activo
                ? $"Listo: premio encendido. Cada contador que traen y paga {p.MesesPagados} meses da {p.PremioMeses} mes(es) gratis a quien lo invitó."
                : "Listo: premio apagado. Los enlaces siguen anotando quién invitó, pero no se dan premios." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesArmar)
        .WithTags("Administración · planes")
        .WithSummary("Las reglas del premio por invitar");
    }
}