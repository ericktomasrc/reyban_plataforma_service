using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Planes;

public sealed record PeticionPaginaPublica(
    string? RazonSocial, string? Ruc, string? Direccion, string? Correo, string? Whatsapp, DateOnly? Actualizado);

/// <summary>
/// 074 — LA PÁGINA PÚBLICA (facturacion.reybanconsulting.com).
///
/// /api/publico/vitrina es SIN SESIÓN, a propósito: es la vitrina que ven
/// Culqi, Google y cualquiera que llegue. Solo trae los precios (los mismos de
/// «Precios y reglas») y los datos de contacto de Reyban. Nada de ninguna cuenta.
///
/// La plataforma cambia esos datos en Planes y cobros › Precios y reglas ›
/// Página pública (permiso planes_armar).
/// </summary>
public static class EndpointsPaginaPublica
{
    public static void MapearPaginaPublica(this IEndpointRouteBuilder rutas)
    {
        rutas.MapGet("/api/publico/vitrina", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"SELECT publico_vitrina()::text AS \"Value\"").FirstAsync(ct),
                            "application/json"))
            .WithTags("Página pública")
            .WithSummary("Precios y contacto para la página pública (sin sesión)");

        rutas.MapPut("/api/admin/planes/pagina-publica", async (PeticionPaginaPublica p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var razon = (p.RazonSocial ?? "").Trim();
            var ruc = string.IsNullOrWhiteSpace(p.Ruc) ? null : Regex.Replace(p.Ruc, @"\s", "");
            var dir = (p.Direccion ?? "").Trim();
            var correo = (p.Correo ?? "").Trim().ToLowerInvariant();
            var wsp = string.IsNullOrWhiteSpace(p.Whatsapp) ? null : p.Whatsapp.Trim();
            if (razon.Length is < 2 or > 200) return Results.BadRequest(new { error = "Escribe la razón social." });
            if (dir.Length is < 2 or > 300) return Results.BadRequest(new { error = "Escribe la dirección (por ejemplo: Lima, Perú)." });
            if (!Regex.IsMatch(correo, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return Results.BadRequest(new { error = "El correo de contacto no es válido." });
            if (wsp is not null && !Regex.IsMatch(wsp, @"^\+?[0-9 ]{6,20}$")) return Results.BadRequest(new { error = "El WhatsApp solo lleva números (puede empezar con +51)." });
            if (ruc is not null && !await db.Database.SqlQuery<bool>($"SELECT ruc_valido({ruc}) AS \"Value\"").FirstAsync(ct))
                return Results.BadRequest(new { error = "Ese RUC no es válido. Revisa los 11 números." });
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE plataforma_ajustes
                   SET publica_razon_social = {razon}, publica_ruc = {ruc}::text, publica_direccion = {dir},
                       publica_correo = {correo}, publica_whatsapp = {wsp}::text,
                       publica_actualizado = COALESCE({p.Actualizado}::date, publica_actualizado)
                 WHERE id = 1
                """, ct);
            return Results.Ok(new { mensaje = "Listo: la página pública ya muestra estos datos." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesArmar)
        .WithTags("Administración · planes")
        .WithSummary("Datos de contacto de la página pública");
    }
}