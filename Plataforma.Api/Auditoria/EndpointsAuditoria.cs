using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;

namespace Plataforma.Api.Auditoria;

/// <summary>
/// Mirar lo que ya está registrado.
///
/// NINGUNA CONSULTA DE AQUÍ FILTRA POR EMPRESA, y no es un olvido: lo hace la
/// política de Row Level Security. Un administrador de empresa ve lo suyo; el
/// super administrador, todo. La misma consulta devuelve cosas distintas según
/// quién pregunta, que es justamente el punto del diseño.
///
/// Filtrarlo aquí además sería peor: daría a entender que el aislamiento
/// depende de que quien escriba la consulta se acuerde.
///
/// TODO ES DE SOLO LECTURA. No hay ningún endpoint que escriba, modifique o
/// borre: la bitácora la escribe un disparador de la base, y el disparador
/// `bitacora_inmutable` impide tocarla después.
/// </summary>
public static class EndpointsAuditoria
{
    /// <summary>
    /// Cuántas filas por página.
    ///
    /// La bitácora es la tabla que más crece de todo el sistema. Sin límite,
    /// la primera consulta de una empresa con un año de uso se traería cientos
    /// de miles de filas con su JSON completo, y el navegador se quedaría
    /// pensando hasta que alguien cierre la pestaña.
    /// </summary>
    private const int PorPaginaPorDefecto = 50;
    private const int PorPaginaMaximo     = 200;

    public static void MapearAuditoria(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/auditoria").WithTags("Auditoría");


        // =====================================================================
        // LA BITÁCORA: QUÉ CAMBIÓ
        //
        // Una fila por cada INSERT, UPDATE y DELETE de cada tabla.
        //
        // NO DEVUELVE `antes` NI `despues` EN EL LISTADO, a propósito: son dos
        // JSON con la fila entera, y cincuenta de ellos pesan más que todo lo
        // demás junto. Para verlos está el endpoint del detalle, que trae los
        // de una sola fila.
        // =====================================================================
        grupo.MapGet("/cambios", async (
            ContextoPlataforma db,
            CancellationToken ct,
            string? tabla    = null,
            Guid? usuarioId  = null,
            char? operacion  = null,
            DateTime? desde  = null,
            DateTime? hasta  = null,
            int pagina       = 1,
            int porPagina    = PorPaginaPorDefecto) =>
        {
            var consulta = db.Bitacora.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(tabla)) consulta = consulta.Where(b => b.Tabla == tabla);
            if (usuarioId is { } u)                consulta = consulta.Where(b => b.UsuarioId == u);
            if (operacion is { } op)               consulta = consulta.Where(b => b.Operacion == op);
            if (desde is { } d)                    consulta = consulta.Where(b => b.OcurridoEn >= d);

            // `hasta` se interpreta como el final de ese día. Quien escribe una
            // fecha en un filtro espera que ese día entre, no que se corte a
            // medianoche del anterior.
            if (hasta is { } h)                    consulta = consulta.Where(b => b.OcurridoEn < h.Date.AddDays(1));

            var (saltar, tomar) = Pagina(pagina, porPagina);

            var total = await consulta.CountAsync(ct);

            var filas = await consulta
                .OrderByDescending(b => b.OcurridoEn)
                .ThenByDescending(b => b.Id)
                .Skip(saltar).Take(tomar)
                .Select(b => new
                {
                    b.Id,
                    b.OcurridoEn,
                    b.Tabla,
                    b.FilaId,
                    b.Operacion,
                    b.UsuarioId,
                    b.CamposCambiados,
                    ip = b.Ip == null ? null : b.Ip.ToString(),

                    // El nombre de quien lo hizo, resuelto aquí para que la
                    // pantalla no tenga que pedir los usuarios por separado.
                    // Sale nulo si no había sesión —una migración, el arranque—
                    // o si esa persona ya no es visible para quien pregunta.
                    usuario = db.Usuarios
                        .IgnoreQueryFilters()
                        .Where(x => x.Id == b.UsuarioId)
                        .Select(x => x.Nombre)
                        .FirstOrDefault()
                })
                .ToListAsync(ct);

            return Results.Ok(new { total, pagina, porPagina = tomar, filas });
        })
        .RequierePermiso(Permisos.Usuarios.BitacoraVer)
        .WithSummary("Listar cambios registrados");


        // =====================================================================
        // EL DETALLE DE UNA FILA: qué había antes y qué quedó después
        // =====================================================================
        grupo.MapGet("/cambios/{id:long}", async (
            long id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var fila = await db.Bitacora
                .AsNoTracking()
                .Where(b => b.Id == id)
                .Select(b => new
                {
                    b.Id, b.OcurridoEn, b.Tabla, b.FilaId, b.Operacion,
                    b.UsuarioId, b.CamposCambiados, b.Antes, b.Despues,
                    b.Agente,
                    ip = b.Ip == null ? null : b.Ip.ToString(),
                    usuario = db.Usuarios
                        .IgnoreQueryFilters()
                        .Where(x => x.Id == b.UsuarioId)
                        .Select(x => x.Nombre)
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync(ct);

            // Un 404 aquí puede significar dos cosas —no existe, o es de otra
            // empresa— y **se responde igual en los dos casos**. Distinguirlas
            // dejaría averiguar cuántos cambios tiene otro cliente probando
            // identificadores.
            return fila is null ? Results.NotFound() : Results.Ok(fila);
        })
        .RequierePermiso(Permisos.Usuarios.BitacoraVer)
        .WithSummary("Ver el antes y el después de un cambio");


        // =====================================================================
        // EL HISTORIAL DE UNA FILA CONCRETA
        //
        // «Enséñame todo lo que le ha pasado a este usuario», que es la
        // pregunta que se hace de verdad cuando algo no cuadra.
        // =====================================================================
        grupo.MapGet("/cambios/{tabla}/{filaId}", async (
            string tabla, string filaId,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var filas = await db.Bitacora
                .AsNoTracking()
                .Where(b => b.Tabla == tabla && b.FilaId == filaId)
                .OrderByDescending(b => b.OcurridoEn)
                .Take(PorPaginaMaximo)
                .Select(b => new
                {
                    b.Id, b.OcurridoEn, b.Operacion, b.UsuarioId, b.CamposCambiados,
                    ip = b.Ip == null ? null : b.Ip.ToString(),
                    usuario = db.Usuarios
                        .IgnoreQueryFilters()
                        .Where(x => x.Id == b.UsuarioId)
                        .Select(x => x.Nombre)
                        .FirstOrDefault()
                })
                .ToListAsync(ct);

            return Results.Ok(filas);
        })
        .RequierePermiso(Permisos.Usuarios.BitacoraVer)
        .WithSummary("El historial completo de una fila");


        // =====================================================================
        // QUÉ TABLAS HAY, para llenar el desplegable del filtro
        //
        // Sale de lo que hay registrado y no de una lista escrita aquí: así, el
        // día que aparezca una tabla nueva, el filtro la ofrece solo.
        // =====================================================================
        grupo.MapGet("/tablas", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var tablas = await db.Bitacora
                .AsNoTracking()
                .Select(b => b.Tabla)
                .Distinct()
                .OrderBy(t => t)
                .ToListAsync(ct);

            return Results.Ok(tablas);
        })
        .RequierePermiso(Permisos.Usuarios.BitacoraVer)
        .WithSummary("Las tablas que aparecen en la bitácora");


        // =====================================================================
        // LOS EVENTOS DE SEGURIDAD: LO QUE NO CAMBIÓ NINGUNA FILA
        //
        // Un ingreso fallido, un 403, un código de segundo factor incorrecto,
        // un enlace caducado que alguien intentó usar. Nada de eso toca una
        // tabla, así que no deja rastro en la bitácora — y es justo lo que
        // interesa mirar cuando algo huele mal.
        // =====================================================================
        grupo.MapGet("/eventos", async (
            ContextoPlataforma db,
            CancellationToken ct,
            string? tipo    = null,
            bool? exito     = null,
            Guid? usuarioId = null,
            DateTime? desde = null,
            DateTime? hasta = null,
            int pagina      = 1,
            int porPagina   = PorPaginaPorDefecto) =>
        {
            var consulta = db.Eventos.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(tipo)) consulta = consulta.Where(e => e.Tipo == tipo);
            if (exito is { } ok)                  consulta = consulta.Where(e => e.Exito == ok);
            if (usuarioId is { } u)               consulta = consulta.Where(e => e.UsuarioId == u);
            if (desde is { } d)                   consulta = consulta.Where(e => e.OcurridoEn >= d);
            if (hasta is { } h)                   consulta = consulta.Where(e => e.OcurridoEn < h.Date.AddDays(1));

            var (saltar, tomar) = Pagina(pagina, porPagina);

            var total = await consulta.CountAsync(ct);

            var filas = await consulta
                .OrderByDescending(e => e.OcurridoEn)
                .ThenByDescending(e => e.Id)
                .Skip(saltar).Take(tomar)
                .Select(e => new
                {
                    e.Id, e.OcurridoEn, e.Tipo, e.Exito, e.UsuarioId, e.Correo, e.Detalle,
                    ip = e.Ip == null ? null : e.Ip.ToString(),
                    usuario = db.Usuarios
                        .IgnoreQueryFilters()
                        .Where(x => x.Id == e.UsuarioId)
                        .Select(x => x.Nombre)
                        .FirstOrDefault()
                })
                .ToListAsync(ct);

            return Results.Ok(new { total, pagina, porPagina = tomar, filas });
        })
        .RequierePermiso(Permisos.Usuarios.BitacoraVer)
        .WithSummary("Listar eventos de seguridad");
    }


    /// <summary>
    /// Traduce página y tamaño a saltar y tomar, con los topes puestos.
    ///
    /// El tope no es cortesía: sin él, `?porPagina=1000000` es una forma
    /// cómoda de tumbar el servidor desde la barra de direcciones.
    /// </summary>
    private static (int Saltar, int Tomar) Pagina(int pagina, int porPagina)
    {
        var p = pagina    < 1 ? 1 : pagina;
        var t = porPagina < 1 ? PorPaginaPorDefecto
              : porPagina > PorPaginaMaximo ? PorPaginaMaximo
              : porPagina;

        return ((p - 1) * t, t);
    }
}
