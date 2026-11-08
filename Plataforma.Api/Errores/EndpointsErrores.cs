using System.Text;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Errores;

/// <summary>
/// e225 — ERRORES DEL SISTEMA (8 nov 2026): dónde hubo errores, sin adivinar,
/// y descargar directo el error.
///
///   GET /api/admin/errores/resumen?dias=14     la tabla: días × procesos, con
///                                              errores y avisos de cada uno.
///   GET /api/admin/errores/hoy                 errores de hoy (el número del menú).
///   GET /api/admin/errores/{dia}/{proceso}?avisos=false
///                                              los errores (o avisos) de ese
///                                              día, agrupados.
///   GET /api/admin/errores/{dia}/{proceso}/descargar?avisos=false&amp;firma=…
///                                              un .txt: con firma, solo ese
///                                              error (todas sus veces); sin
///                                              firma, todos los del día.
///
/// Solo el equipo de la plataforma con el permiso plataforma.errores (el
/// dueño lo tiene). El día se valida como fecha y el proceso contra una lista
/// fija: nunca se arma una ruta con lo que manda el navegador.
/// </summary>
public static class EndpointsErrores
{
    private static readonly TimeSpan Lima = TimeSpan.FromHours(-5);

    private static DateOnly Hoy() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Lima).DateTime);

    public static void MapearErrores(this IEndpointRouteBuilder rutas)
    {
        var g = rutas.MapGroup("/api/admin/errores").WithTags("Errores del sistema");

        g.MapGet("/resumen", (int? dias, LectorErrores lector) =>
        {
            var n = Math.Clamp(dias ?? 14, 1, 60);
            var hoy = Hoy();

            var lista = Enumerable.Range(0, n).Select(i => hoy.AddDays(i - n + 1)).Select(d => new
            {
                dia = d.ToString("yyyy-MM-dd"),
                conteos = LectorErrores.Procesos.ToDictionary(p => p.Clave, p =>
                {
                    var c = lector.Contar(d, p);
                    return new { errores = c.Errores, avisos = c.Avisos, existe = c.Existe };
                })
            }).ToList();

            // El último error de hoy, para el aviso de arriba.
            object? ultimo = null;
            var deHoy = LectorErrores.Procesos
                .Where(p => lector.Contar(hoy, p).Errores > 0)
                .SelectMany(p => lector.Entradas(hoy, p, avisos: false).Select(e => (p, e)))
                .OrderByDescending(x => x.e.Cuando)
                .ToList();
            if (deHoy.Count > 0)
            {
                var (p, e) = deHoy[0];
                ultimo = new
                {
                    proceso = p.Clave,
                    hora = e.Cuando.ToString("HH:mm"),
                    titulo = LectorErrores.Agrupar([e])[0].Titulo,
                    significado = LectorErrores.Significado(e.Texto),
                    errores = deHoy.Count(x => x.p == p),
                };
            }

            return Results.Ok(new
            {
                hoy = hoy.ToString("yyyy-MM-dd"),
                procesos = LectorErrores.Procesos.Select(p => new
                {
                    clave = p.Clave, nombre = p.Nombre, carpeta = lector.CarpetaExiste(p)
                }),
                dias = lista,
                ultimo,
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.Errores)
        .WithSummary("Días × procesos: cuántos errores y avisos hubo");

        g.MapGet("/hoy", (LectorErrores lector) =>
        {
            var hoy = Hoy();
            return Results.Ok(new { errores = LectorErrores.Procesos.Sum(p => lector.Contar(hoy, p).Errores) });
        })
        .SoloSuperAdmin(Permisos.Plataforma.Errores)
        .WithSummary("Errores de hoy (el número del menú)");

        g.MapGet("/{dia}/{proceso}", (string dia, string proceso, bool? avisos, LectorErrores lector) =>
        {
            if (!Validar(dia, proceso, out var d, out var p, out var error)) return error!;

            var grupos = LectorErrores.Agrupar(lector.Entradas(d, p!, avisos == true));
            var c = lector.Contar(d, p!);
            return Results.Ok(new
            {
                dia, proceso = p!.Clave, nombre = p.Nombre,
                errores = c.Errores, avisos = c.Avisos, existe = c.Existe,
                grupos = grupos.Take(300).Select(x => new
                {
                    firma = x.Firma, titulo = x.Titulo, significado = x.Significado, veces = x.Veces,
                    primera = x.Primera.ToString("HH:mm"), ultima = x.Ultima.ToString("HH:mm"),
                    categoria = x.Categoria, ejemplo = x.Ejemplo,
                }),
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.Errores)
        .WithSummary("Los errores (o avisos) de un día y un proceso, agrupados");

        g.MapGet("/{dia}/{proceso}/descargar", (string dia, string proceso, bool? avisos, string? firma, LectorErrores lector) =>
        {
            if (!Validar(dia, proceso, out var d, out var p, out var error)) return error!;

            var soloAvisos = avisos == true;
            var entradas = lector.Entradas(d, p!, soloAvisos);
            if (!string.IsNullOrWhiteSpace(firma))
                entradas = entradas.Where(e => LectorErrores.Firma(e) == firma).ToList();

            if (entradas.Count == 0)
                return Results.NotFound(new { error = "Ese día no hay nada que descargar en ese proceso." });

            var que = soloAvisos ? "avisos" : "errores";
            var sb = new StringBuilder();
            sb.Append($"# {p!.Nombre} · {dia} · {(firma is null ? $"todos los {que}" : $"un {(soloAvisos ? "aviso" : "error")} ({entradas.Count} {(entradas.Count == 1 ? "vez" : "veces")})")}\n");
            var significado = firma is null ? null : LectorErrores.Significado(entradas[^1].Texto);
            if (significado is not null) sb.Append("# Qué significa: ").Append(significado).Append('\n');
            sb.Append("# Hora de Lima. Descargado desde «Errores del sistema».\n\n");
            foreach (var e in entradas) sb.Append(e.Texto).Append("\n\n");

            var nombre = firma is null
                ? $"{que}-{p.Clave}-{dia}.txt"
                : $"{(soloAvisos ? "aviso" : "error")}-{p.Clave}-{dia}-{firma}.txt";
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString().Replace("\n", "\r\n")),
                "text/plain; charset=utf-8", nombre);
        })
        .SoloSuperAdmin(Permisos.Plataforma.Errores)
        .WithSummary("Descarga un error (todas sus veces) o todos los del día, en .txt");
    }

    private static bool Validar(string dia, string proceso, out DateOnly d,
        out LectorErrores.Proceso? p, out IResult? error)
    {
        p = LectorErrores.BuscarProceso(proceso);
        error = null;
        if (!DateOnly.TryParseExact(dia, "yyyy-MM-dd", out d))
            error = Results.BadRequest(new { error = "La fecha no es válida." });
        else if (p is null)
            error = Results.BadRequest(new { error = "Ese proceso no existe." });
        return error is null;
    }
}