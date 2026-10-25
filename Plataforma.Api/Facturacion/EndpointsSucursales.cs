using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>23 oct: una serie como la lista el servicio, con su id (para quitarla si nunca se usó).</summary>
public sealed record SerieConId(Guid Id, string TipoComprobante, string Serie, int UltimoCorrelativo, bool Activo, int Comprobantes);

/// <summary>
/// 23 OCT (044) — LAS REGLAS DE LAS SUCURSALES, para quien emite y para la
/// pantalla de Configuración › Sucursales.
///
/// UNA SUCURSAL ES UN LOCAL DEL MISMO RUC. Lo que la define ante SUNAT es su
/// código de establecimiento (ficha RUC); aquí además tiene sus series y sus
/// usuarios:
///
///   · Cada serie pertenece a UNA sucursal. Sin asignar = de la 0000.
///   · Un usuario sin sucursales asignadas trabaja en todas.
///   · El comprobante guarda de qué sucursal salió (documentos.sucursal_id);
///     null en los de antes = la 0000.
///
/// LA 0000 (domicilio fiscal) existe siempre: la 044 la creó para las
/// empresas de entonces y <see cref="PrincipalAsync"/> la crea al primer uso
/// para las nuevas. No se desactiva ni se elimina.
/// </summary>
public static class Sucursales
{
    public const string Principal = Sucursal.CodigoPrincipal;

    private static readonly Regex Codigo4Re = new("^[0-9]{4}$", RegexOptions.Compiled);
    private static readonly Regex Ubigeo6Re = new("^[0-9]{6}$", RegexOptions.Compiled);
    private static readonly Regex FormaSerieRe = new("^[A-Z][A-Z0-9]{3}$", RegexOptions.Compiled);
    private static readonly Regex EspaciosRe = new(@"\s+", RegexOptions.Compiled);
    private static Regex Codigo4() => Codigo4Re;
    private static Regex Ubigeo6() => Ubigeo6Re;
    private static Regex FormaSerie() => FormaSerieRe;
    private static Regex Espacios() => EspaciosRe;

    public static string Clave(string tipo, string serie) => $"{tipo}|{serie.Trim().ToUpperInvariant()}";

    /// <summary>Para comparar nombres y direcciones: minúsculas y sin espacios de más.</summary>
    public static string Comparable(string? s) => Espacios().Replace((s ?? "").Trim(), " ").ToLowerInvariant();

    /// <summary>La 0000 de la empresa. Si no existe (empresa nueva), se crea con la dirección de la empresa.</summary>
    public static async Task<Sucursal> PrincipalAsync(ContextoPlataforma db, Guid empresaId, CancellationToken ct)
    {
        var s = await db.Sucursales.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.EmpresaId == empresaId && x.Codigo == Principal && x.EliminadaEn == null, ct);
        if (s is not null) return s;

        var direccion = await db.Empresas.AsNoTracking()
            .Where(e => e.Id == empresaId).Select(e => e.Direccion).FirstOrDefaultAsync(ct);

        s = new Sucursal { EmpresaId = empresaId, Codigo = Principal, Nombre = "Domicilio fiscal", Direccion = direccion };
        db.Sucursales.Add(s);
        try
        {
            await db.SaveChangesAsync(ct);
            return s;
        }
        catch (DbUpdateException)
        {
            // Otra petición la creó al mismo tiempo: se usa esa.
            db.Entry(s).State = EntityState.Detached;
            return await db.Sucursales.IgnoreQueryFilters()
                .FirstAsync(x => x.EmpresaId == empresaId && x.Codigo == Principal && x.EliminadaEn == null, ct);
        }
    }

    /// <summary>Serie → sucursal, de las asignaciones vigentes. Lo que no está aquí es de la 0000.</summary>
    public static async Task<Dictionary<string, Guid>> MapaAsync(ContextoPlataforma db, Guid empresaId, CancellationToken ct) =>
        (await db.SucursalSeries.AsNoTracking()
            .Where(x => x.EmpresaId == empresaId)   // el super administrador ve todas las empresas
            .Select(x => new { x.TipoComprobante, x.Serie, x.SucursalId })
            .ToListAsync(ct))
        .GroupBy(x => Clave(x.TipoComprobante, x.Serie))
        .ToDictionary(g => g.Key, g => g.First().SucursalId);

    /// <summary>Las sucursales en las que trabaja el usuario; null = en todas.</summary>
    public static async Task<HashSet<Guid>?> PermitidasAsync(ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        if (ctx.EsSuperAdmin || ctx.UsuarioId is not { } u) return null;

        var ids = await db.UsuarioSucursales.AsNoTracking()
            .Where(x => x.UsuarioId == u)
            .Select(x => x.SucursalId)
            .ToListAsync(ct);

        return ids.Count == 0 ? null : ids.ToHashSet();
    }

    /// <summary>
    /// ¿Puede este usuario emitir con esta serie? Devuelve la sucursal (para
    /// guardarla en el comprobante) o el motivo por el que no.
    /// </summary>
    public static async Task<(Guid? Id, string? Error)> RevisarSerieAsync(
        ContextoPlataforma db, ContextoPeticion ctx, Guid empresaId, string tipo, string serie, CancellationToken ct)
    {
        var principal = await PrincipalAsync(db, empresaId, ct);
        var mapa = await MapaAsync(db, empresaId, ct);
        var id = mapa.TryGetValue(Clave(tipo, serie), out var m) ? m : principal.Id;

        var s = id == principal.Id ? principal
              : await db.Sucursales.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

        // Asignada a una sucursal que ya no existe: es de la 0000.
        if (s is null || s.EliminadaEn is not null) s = principal;

        if (!s.Activo)
            return (null, $"La serie {serie} es de la sucursal «{s.Nombre}» ({s.Codigo}), que está desactivada: no emite. " +
                          "Usa una serie de otra sucursal o reactívala en Configuración › Sucursales.");

        if (await PermitidasAsync(db, ctx, ct) is { } permitidas && !permitidas.Contains(s.Id))
            return (null, $"La serie {serie} es de la sucursal «{s.Nombre}» ({s.Codigo}) y tú no trabajas en ella. " +
                          "Usa una serie de tu sucursal, o pide al administrador que te la asigne.");

        // 046: con una sucursal elegida arriba, se emite desde esa. (Y con
        // alcance «su sucursal» o «solo lo suyo» siempre hay una elegida.)
        if (ctx.SucursalVista is { } vista && vista != s.Id)
            return (null, $"La serie {serie} es de la sucursal «{s.Nombre}» ({s.Codigo}), y arriba tienes elegida otra. " +
                          $"Cambia arriba a «{s.Nombre}»{(ctx.VeTodaLaEmpresa ? " o a «Todas»" : "")}, o usa una serie de la sucursal elegida.");

        return (s.Id, null);
    }

    /// <summary>
    /// La sucursal de una nota de crédito o débito: la del comprobante que
    /// corrige. Si esa ya no emite (desactivada o eliminada), la 0000.
    /// </summary>
    public static async Task<Guid> DeNotaAsync(ContextoPlataforma db, Documento original, CancellationToken ct)
    {
        var principal = await PrincipalAsync(db, original.EmpresaId, ct);
        if (original.SucursalId is not { } id || id == principal.Id) return principal.Id;

        var activa = await db.Sucursales.AsNoTracking().AnyAsync(x => x.Id == id && x.EliminadaEn == null, ct);
        return activa ? id : principal.Id;
    }

    /// <summary>
    /// Lo que va al servicio: {codigo, direccion} del local. Null para la 0000
    /// (el servicio pone 0000 y el domicilio fiscal, como siempre).
    /// </summary>
    public static async Task<object?> EstablecimientoAsync(ContextoPlataforma db, Guid? sucursalId, CancellationToken ct)
    {
        if (sucursalId is not { } id) return null;

        var s = await db.Sucursales.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || s.Codigo == Principal) return null;

        return new { codigo = s.Codigo, direccion = await DireccionCompletaAsync(db, s.Direccion, s.Ubigeo, ct) };
    }

    /// <summary>«Av. Túpac Amaru 1820 - Comas, Lima - Lima».</summary>
    public static async Task<string> DireccionCompletaAsync(ContextoPlataforma db, string? direccion, string? ubigeo, CancellationToken ct)
    {
        var lugar = await LugarAsync(db, ubigeo, ct);
        var d = (direccion ?? "").Trim();
        return lugar is null ? d : d.Length == 0 ? lugar : $"{d} - {lugar}";
    }

    /// <summary>«Comas, Lima - Lima» para un ubigeo; null si no hay o no existe.</summary>
    public static async Task<string?> LugarAsync(ContextoPlataforma db, string? ubigeo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ubigeo) || !Ubigeo6().IsMatch(ubigeo.Trim())) return null;
        var u = ubigeo.Trim();
        return await db.Database.SqlQuery<string>(
                $"SELECT initcap(lower(distrito)) || ', ' || initcap(lower(provincia)) || ' - ' || initcap(lower(departamento)) AS \"Value\" FROM referencia.ubigeos WHERE ubigeo = {u}")
            .FirstOrDefaultAsync(ct);
    }

    // =========================================================================
    // VALIDACIONES (las mismas al crear y al editar)
    // =========================================================================

    public static string? ProblemaCodigo(string? codigo) =>
        string.IsNullOrWhiteSpace(codigo) || !Codigo4().IsMatch(codigo.Trim())
            ? "El código de establecimiento son 4 dígitos, como figura en tu ficha RUC (SUNAT SOL › Consulta RUC › Establecimientos anexos). Ej.: 0001."
            : null;

    public static string? ProblemaNombre(string? nombre)
    {
        var n = (nombre ?? "").Trim();
        if (n.Length == 0) return "Ponle un nombre a la sucursal. Ej.: Tienda Norte.";
        if (n.Length > 80) return "El nombre de la sucursal es muy largo (máximo 80 caracteres).";
        return null;
    }

    public static string? ProblemaDireccion(string? direccion)
    {
        var d = (direccion ?? "").Trim();
        if (d.Length < 5) return "Escribe la dirección del local (sale en sus comprobantes).";
        if (d.Length > 200) return "La dirección es muy larga (máximo 200 caracteres).";
        return null;
    }

    public static async Task<string?> ProblemaUbigeoAsync(ContextoPlataforma db, string? ubigeo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ubigeo)) return null;
        if (!Ubigeo6().IsMatch(ubigeo.Trim())) return "El ubigeo son 6 dígitos. Elige el distrito de la lista.";
        return await LugarAsync(db, ubigeo, ct) is null ? "Ese ubigeo no existe. Elige el distrito de la lista." : null;
    }

    public static string? ProblemaSerie(string? tipo, string? serie)
    {
        var t = (tipo ?? "").Trim();
        var s = (serie ?? "").Trim().ToUpperInvariant();

        if (t is not ("01" or "03" or "07" or "08" or "09" or "31"))
            return "El tipo de la serie va 01 (factura), 03 (boleta), 07 (nota de crédito), 08 (nota de débito), 09 (guía del remitente) o 31 (guía del transportista).";
        if (!FormaSerie().IsMatch(s))
            return $"«{serie}» no es una serie: son cuatro caracteres, una letra y tres letras o números. Ej.: F002.";

        return (t, s[0]) switch
        {
            ("01", not 'F') => $"{s}: las facturas usan series que empiezan con F. Ej.: F002.",
            ("03", not 'B') => $"{s}: las boletas usan series que empiezan con B. Ej.: B002.",
            ("07" or "08", not ('F' or 'B')) => $"{s}: las notas empiezan con F (de facturas) o B (de boletas). Ej.: FC02.",
            // 050: guías. SUNAT pide T + 3 números (remitente) y V + 3 números (transportista).
            ("09", _) when !System.Text.RegularExpressions.Regex.IsMatch(s, "^T[0-9]{3}$") => $"{s}: las guías del remitente usan T y tres números. Ej.: T002.",
            ("31", _) when !System.Text.RegularExpressions.Regex.IsMatch(s, "^V[0-9]{3}$") => $"{s}: las guías del transportista usan V y tres números. Ej.: V001.",
            _ => null
        };
    }
}


public sealed record PeticionSerieSucursal(string Tipo, string Serie);

public sealed record PeticionSucursal(
    string? Codigo, string? Nombre, string? Direccion, string? Ubigeo,
    List<PeticionSerieSucursal>? Series = null,
    // La dirección se repite con otra sucursal y quien la crea dijo «guardar igual».
    bool Confirmado = false);

public sealed record PeticionSeriesSucursal(List<PeticionSerieSucursal>? Series);

public sealed record PeticionUsuarioSucursales(List<Guid>? Sucursales);


/// <summary>
/// 23 OCT (044) — CONFIGURACIÓN › SUCURSALES.
///
///   GET    /api/facturacion/sucursales                  la lista, con series, usuarios y si se puede eliminar
///   GET    /api/facturacion/sucursales/mias             en cuáles puedo emitir (pantalla de emisión)
///   GET    /api/facturacion/sucursales/locales-sunat    los locales de mi RUC según el padrón de SUNAT
///   GET    /api/facturacion/sucursales/ubigeos?q=       buscar un distrito
///   POST   /api/facturacion/sucursales                  crear (con sus series)
///   PUT    /api/facturacion/sucursales/{id}             cambiar nombre, dirección (y código, si nunca emitió)
///   POST   /api/facturacion/sucursales/{id}/series      agregarle series
///   POST   /api/facturacion/sucursales/{id}/desactivar  deja de emitir; todo lo suyo se queda
///   POST   /api/facturacion/sucursales/{id}/reactivar
///   DELETE /api/facturacion/sucursales/{id}             SOLO si nunca tuvo movimiento
///   GET    /api/facturacion/sucursales/usuarios         quién trabaja dónde
///   PUT    /api/facturacion/sucursales/usuarios/{id}    asignar (vacío = todas)
///
/// ELIMINAR vs DESACTIVAR: una sucursal sin ningún movimiento (ningún
/// comprobante, ni borrador, y sus series sin usar) se puede eliminar: se
/// marca y desaparece de las listas, y su código queda libre. Con el mínimo
/// movimiento ya no: se desactiva, y sus comprobantes y reportes se quedan.
/// Nada se borra de la base en ningún caso.
/// </summary>
public static class EndpointsSucursales
{
    private const string Gestionar = Permisos.Facturacion.SucursalesGestionar;

    public static void MapearSucursales(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/sucursales")
                         .WithTags("Facturación · sucursales");

        // =====================================================================
        // LA LISTA
        // =====================================================================
        grupo.MapGet("/", async (
            ContextoPlataforma db, ContextoPeticion ctx, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();

            var principal = await Sucursales.PrincipalAsync(db, empresaId, ct);
            var lista = await db.Sucursales.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null)
                .OrderByDescending(s => s.CreadoEn)   // 24 oct: lo más nuevo arriba
                .ToListAsync(ct);

            var mapa = await Sucursales.MapaAsync(db, empresaId, ct);
            var (series, seriesOk) = await SeriesDelServicioAsync(facturacion, ct);

            // Series de cada sucursal: las asignadas; a la 0000, todas las demás.
            var porSucursal = series
                .Where(s => s.Activo)
                .GroupBy(s => mapa.TryGetValue(Sucursales.Clave(s.TipoComprobante, s.Serie), out var id)
                                  && lista.Any(x => x.Id == id) ? id : principal.Id)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.TipoComprobante).ThenBy(x => x.Serie).ToList());

            var usuarios = (await db.UsuarioSucursales.AsNoTracking()
                    .Where(x => x.EmpresaId == empresaId)
                    .Select(x => x.SucursalId).ToListAsync(ct))
                .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

            // Comprobantes de cada sucursal, contando borradores (también los
            // descartados: fueron movimiento). Los de antes, sin sucursal, son de la 0000.
            // 046: todos los de la empresa, sin el recorte del alcance ni de la sucursal elegida arriba.
            var documentos = new Dictionary<Guid, int>();
            foreach (var x in await SinAlcance.LeerAsync(db, () => db.Documentos.IgnoreQueryFilters().AsNoTracking()
                         .Where(d => d.EmpresaId == empresaId)
                         .GroupBy(d => d.SucursalId)
                         .Select(g => new { g.Key, n = g.Count() })
                         .ToListAsync(ct), ct))
            {
                var k = x.Key ?? principal.Id;
                documentos[k] = documentos.GetValueOrDefault(k) + x.n;
            }

            var filas = new List<object>();
            foreach (var s in lista)
            {
                var suyas = porSucursal.GetValueOrDefault(s.Id) ?? [];
                var docs = documentos.GetValueOrDefault(s.Id);
                var (puede, motivo) = PuedeEliminar(s, docs, suyas, seriesOk);

                filas.Add(new
                {
                    s.Id, s.Codigo, s.Nombre, s.Direccion, s.Ubigeo,
                    lugar = await Sucursales.LugarAsync(db, s.Ubigeo, ct),
                    s.Activo, s.CreadoEn,   // 24 oct
                    esPrincipal = s.Codigo == Sucursales.Principal,
                    series = suyas.Select(x => new { tipo = x.TipoComprobante, serie = x.Serie, ultimo = x.UltimoCorrelativo }),
                    usuarios = usuarios.GetValueOrDefault(s.Id),
                    documentos = docs,
                    puedeEliminar = puede,
                    motivoNoEliminar = motivo,
                    // El código se puede corregir mientras no haya salido ningún comprobante con él.
                    puedeCambiarCodigo = s.Codigo != Sucursales.Principal && docs == 0
                });
            }

            return Results.Ok(new
            {
                sucursales = filas,
                seriesLeidas = seriesOk,
                puedeGestionar = ctx.EsSuperAdmin || ctx.Permisos.Contains(Gestionar)
            });
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las sucursales de mi empresa, con sus series y usuarios");


        // =====================================================================
        // EN CUÁLES PUEDO EMITIR (el selector de la pantalla de emisión)
        // =====================================================================
        grupo.MapGet("/mias", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();

            var principal = await Sucursales.PrincipalAsync(db, empresaId, ct);
            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);

            var lista = await db.Sucursales.AsNoTracking()   // solo activas (filtro global)
                .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null)
                .OrderBy(s => s.Codigo)
                .Select(s => new { s.Id, s.Codigo, s.Nombre, s.Direccion })
                .ToListAsync(ct);

            // 046: con una elegida arriba, solo esa. Y la de por defecto de la
            // persona, para que venga elegida en «Emitir desde».
            return Results.Ok(new
            {
                principalId = principal.Id,
                defectoId = ctx.SucursalDefecto,
                sucursales = lista.Where(s => (permitidas is null || permitidas.Contains(s.Id))
                                           && (ctx.SucursalVista is null || s.Id == ctx.SucursalVista))
            });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las sucursales desde las que puedo emitir");


        // =====================================================================
        // LOS LOCALES DE MI RUC SEGÚN SUNAT (padrón de locales anexos, 045)
        // =====================================================================
        grupo.MapGet("/locales-sunat", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();

            var ruc = await db.Empresas.AsNoTracking()
                .Where(e => e.Id == empresaId).Select(e => e.Ruc).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(ruc))
                return Results.Ok(new { cargado = false, ruc = (string?)null, locales = Array.Empty<object>() });

            string texto;
            try
            {
                texto = await db.Database.SqlQuery<string>($"SELECT anexos_consultar({ruc}) AS \"Value\"").FirstAsync(ct);
            }
            catch (Npgsql.PostgresException)
            {
                // Sin la migración 045 todavía: se escribe a mano.
                return Results.Ok(new { cargado = false, ruc, locales = Array.Empty<object>() });
            }

            // Cuáles ya son una sucursal (misma dirección y distrito).
            var usadas = (await db.Sucursales.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null)
                    .Select(s => new { s.Nombre, s.Codigo, s.Direccion, s.Ubigeo })
                    .ToListAsync(ct))
                .ToList();

            using var doc = JsonDocument.Parse(texto);
            var r = doc.RootElement;
            var locales = r.GetProperty("locales").EnumerateArray().Select(l =>
            {
                string? S(string p) => l.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var direccion = S("direccion");
                var ubigeo = S("ubigeo");
                var ya = usadas.FirstOrDefault(u =>
                    Sucursales.Comparable(u.Direccion) == Sucursales.Comparable(direccion) &&
                    (u.Ubigeo ?? "") == (ubigeo ?? ""));
                return new
                {
                    direccion, ubigeo,
                    lugar = S("distrito") is { } d
                        ? $"{Titulo(d)}, {Titulo(S("provincia"))} - {Titulo(S("departamento"))}"
                        : null,
                    yaEs = ya is null ? null : $"{ya.Nombre} ({ya.Codigo})"
                };
            }).ToList();

            return Results.Ok(new { cargado = r.GetProperty("cargado").GetBoolean(), ruc, locales });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Los locales anexos de mi RUC según el padrón de SUNAT");


        // =====================================================================
        // BUSCAR UN DISTRITO (para escribir la dirección a mano)
        // =====================================================================
        grupo.MapGet("/ubigeos", async (string? q, ContextoPlataforma db, CancellationToken ct) =>
        {
            var t = (q ?? "").Trim();
            if (t.Length < 2) return Results.Ok(Array.Empty<object>());
            var patron = "%" + t.Replace("\\", "").Replace("%", "").Replace("_", "") + "%";

            var texto = await db.Database.SqlQuery<string>($"""
                SELECT coalesce(json_agg(x ORDER BY x.distrito, x.provincia), '[]'::json)::text AS "Value"
                  FROM (SELECT ubigeo, initcap(lower(distrito)) AS distrito, initcap(lower(provincia)) AS provincia,
                               initcap(lower(departamento)) AS departamento
                          FROM referencia.ubigeos
                         WHERE distrito ILIKE {patron} OR ubigeo LIKE {patron}
                         ORDER BY distrito LIMIT 20) x
                """).FirstAsync(ct);

            return Results.Text(texto, "application/json");
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Buscar un distrito (ubigeo del INEI)");


        // =====================================================================
        // CREAR
        // =====================================================================
        grupo.MapPost("/", async (
            PeticionSucursal p, ContextoPlataforma db, ContextoPeticion ctx,
            ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();
            var principal = await Sucursales.PrincipalAsync(db, empresaId, ct);

            var codigo = (p.Codigo ?? "").Trim();
            if (Sucursales.ProblemaCodigo(codigo) is { } mc) return Mal(mc, "codigo");
            if (codigo == Sucursales.Principal)
                return Mal($"La 0000 es el domicilio fiscal y ya existe («{principal.Nombre}»). Los locales anexos van desde 0001.", "codigo");

            if (await ProblemaDatosAsync(p, db, empresaId, null, ct) is { } malo) return malo;

            // Las series: que tengan forma y que no sean de otra sucursal.
            var (pedidas, errorSeries) = await RevisarSeriesAsync(p.Series, db, facturacion, empresaId, principal.Id, null, ct);
            if (errorSeries is not null) return errorSeries;

            if (!p.Confirmado && await DireccionRepetidaAsync(db, empresaId, p.Direccion, p.Ubigeo, null, ct) is { } otra)
                return Results.UnprocessableEntity(new
                {
                    error = $"La sucursal «{otra}» ya tiene esa dirección. ¿Es otro local en la misma dirección (otro piso, otro puesto)?",
                    codigo = "direccion_repetida"
                });

            // Primero las series en el servicio: si falla, no queda una sucursal a medias.
            if (await CrearSeriesAsync(pedidas!, db, ctx, facturacion, ct) is { } falloServicio) return falloServicio;

            var s = new Sucursal
            {
                EmpresaId = empresaId,
                Codigo = codigo,
                Nombre = p.Nombre!.Trim(),
                Direccion = p.Direccion!.Trim(),
                Ubigeo = string.IsNullOrWhiteSpace(p.Ubigeo) ? null : p.Ubigeo.Trim()
            };
            // PRIMERO LA SUCURSAL, DESPUÉS SUS SERIES, en dos guardados: EF no
            // conoce la relación (no hay navegación) y podía insertar las series
            // antes que la sucursal → «referencia a algo que no existe» (23503).
            // Las dos van en la transacción de la petición.
            db.Sucursales.Add(s);
            if (await GuardarAsync(db, ct) is { } choque) return choque;

            foreach (var x in pedidas!)
                db.SucursalSeries.Add(new SucursalSerie { EmpresaId = empresaId, SucursalId = s.Id, TipoComprobante = x.Tipo, Serie = x.Serie });
            if (pedidas.Count > 0 && await GuardarAsync(db, ct) is { } choqueSeries) return choqueSeries;

            return Results.Created($"/api/facturacion/sucursales/{s.Id}", new
            {
                s.Id,
                mensaje = pedidas!.Count == 0
                    ? $"Sucursal «{s.Nombre}» ({s.Codigo}) creada. Agrégale series para poder emitir desde ella."
                    : $"Sucursal «{s.Nombre}» ({s.Codigo}) creada con {string.Join(", ", pedidas.Select(x => x.Serie))}."
            });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Crear una sucursal (con sus series)");


        // =====================================================================
        // CAMBIAR
        // =====================================================================
        grupo.MapPut("/{id:guid}", async (
            Guid id, PeticionSucursal p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();

            var s = await db.Sucursales.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == id && x.EmpresaId == empresaId && x.EliminadaEn == null, ct);
            if (s is null) return Results.NotFound(new { error = "Esa sucursal ya no existe." });

            var codigo = (p.Codigo ?? s.Codigo).Trim();
            if (codigo != s.Codigo)
            {
                if (s.Codigo == Sucursales.Principal)
                    return Mal("La 0000 es el domicilio fiscal: su código no cambia.", "codigo");
                if (Sucursales.ProblemaCodigo(codigo) is { } mc) return Mal(mc, "codigo");
                if (codigo == Sucursales.Principal)
                    return Mal("La 0000 es el domicilio fiscal y ya existe. Los locales anexos van desde 0001.", "codigo");
                if (await TieneDocumentosAsync(db, s, ct))
                    return Mal($"Ya salieron comprobantes con el código {s.Codigo}: no se puede cambiar (estaría en sus XML). " +
                               "Si te equivocaste de código, desactiva esta y crea otra.", "codigo");
            }

            if (await ProblemaDatosAsync(p, db, empresaId, s.Id, ct) is { } malo) return malo;

            if (!p.Confirmado && await DireccionRepetidaAsync(db, empresaId, p.Direccion, p.Ubigeo, s.Id, ct) is { } otra)
                return Results.UnprocessableEntity(new
                {
                    error = $"La sucursal «{otra}» ya tiene esa dirección. ¿Es otro local en la misma dirección (otro piso, otro puesto)?",
                    codigo = "direccion_repetida"
                });

            s.Codigo = codigo;
            s.Nombre = p.Nombre!.Trim();
            s.Direccion = p.Direccion!.Trim();
            s.Ubigeo = string.IsNullOrWhiteSpace(p.Ubigeo) ? null : p.Ubigeo.Trim();

            if (await GuardarAsync(db, ct) is { } choque) return choque;
            return Results.Ok(new { s.Id, mensaje = $"Sucursal «{s.Nombre}» guardada." });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Cambiar el nombre, la dirección o (si nunca emitió) el código");


        // =====================================================================
        // AGREGAR SERIES
        // =====================================================================
        grupo.MapPost("/{id:guid}/series", async (
            Guid id, PeticionSeriesSucursal p, ContextoPlataforma db, ContextoPeticion ctx,
            ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();
            var principal = await Sucursales.PrincipalAsync(db, empresaId, ct);

            var s = await db.Sucursales.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == id && x.EmpresaId == empresaId && x.EliminadaEn == null, ct);
            if (s is null) return Results.NotFound(new { error = "Esa sucursal ya no existe." });
            if (p.Series is not { Count: > 0 }) return Mal("Escribe al menos una serie.", "series");

            var (pedidas, errorSeries) = await RevisarSeriesAsync(p.Series, db, facturacion, empresaId, principal.Id, s.Id, ct);
            if (errorSeries is not null) return errorSeries;

            if (await CrearSeriesAsync(pedidas!, db, ctx, facturacion, ct) is { } falloServicio) return falloServicio;

            // Las de la 0000 no llevan fila: «sin asignar» es de la 0000.
            if (s.Id != principal.Id)
                foreach (var x in pedidas!)
                    db.SucursalSeries.Add(new SucursalSerie { EmpresaId = empresaId, SucursalId = s.Id, TipoComprobante = x.Tipo, Serie = x.Serie });

            if (await GuardarAsync(db, ct) is { } choque) return choque;
            return Results.Ok(new { mensaje = $"Listo: {string.Join(", ", pedidas!.Select(x => x.Serie))} de «{s.Nombre}»." });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Agregar series a una sucursal");


        // =====================================================================
        // DESACTIVAR / REACTIVAR
        // =====================================================================
        grupo.MapPost("/{id:guid}/desactivar", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();

            var s = await db.Sucursales.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == id && x.EmpresaId == empresaId && x.EliminadaEn == null, ct);
            if (s is null) return Results.NotFound(new { error = "Esa sucursal ya no existe." });
            if (s.Codigo == Sucursales.Principal)
                return Mal("La 0000 es el domicilio fiscal: no se desactiva.", null);
            if (!s.Activo) return Results.Ok(new { mensaje = "Ya estaba desactivada." });

            // Quién se queda sin dónde emitir.
            var asignados = await db.UsuarioSucursales.AsNoTracking()
                .Where(x => x.SucursalId == s.Id).Select(x => x.UsuarioId).ToListAsync(ct);
            var soloAqui = new List<string>();
            foreach (var u in asignados)
            {
                var otras = await db.UsuarioSucursales.AsNoTracking()
                    .Where(x => x.UsuarioId == u && x.SucursalId != s.Id)
                    .Join(db.Sucursales, x => x.SucursalId, y => y.Id, (x, y) => y.Id)
                    .AnyAsync(ct);
                if (!otras)
                    soloAqui.Add(await db.Usuarios.AsNoTracking().Where(x => x.Id == u).Select(x => x.Nombre).FirstOrDefaultAsync(ct) ?? "?");
            }

            s.Activo = false;
            if (await GuardarAsync(db, ct) is { } choque) return choque;

            return Results.Ok(new
            {
                mensaje = $"«{s.Nombre}» desactivada: ya no emite. Sus comprobantes y reportes se quedan.",
                aviso = soloAqui.Count == 0 ? null
                    : $"{string.Join(", ", soloAqui)} solo trabaja{(soloAqui.Count > 1 ? "n" : "")} en esta sucursal: " +
                      "asígnale otra en Personal de la empresa para que pueda seguir emitiendo."
            });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Desactivar una sucursal (deja de emitir; nada se borra)");

        grupo.MapPost("/{id:guid}/reactivar", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();

            var s = await db.Sucursales.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == id && x.EmpresaId == empresaId && x.EliminadaEn == null, ct);
            if (s is null) return Results.NotFound(new { error = "Esa sucursal ya no existe." });
            if (s.Activo) return Results.Ok(new { mensaje = "Ya estaba activa." });

            if (await NombreRepetidoAsync(db, empresaId, s.Nombre, s.Id, ct) is { } otra)
                return Mal($"Ya hay una sucursal activa que se llama «{otra}». Cámbiale el nombre a una de las dos antes de reactivarla.", "nombre");

            s.Activo = true;
            if (await GuardarAsync(db, ct) is { } choque) return choque;
            return Results.Ok(new { mensaje = $"«{s.Nombre}» reactivada: ya puede emitir." });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Reactivar una sucursal desactivada");


        // =====================================================================
        // ELIMINAR — solo si nunca tuvo ningún movimiento
        // =====================================================================
        grupo.MapDelete("/{id:guid}", async (
            Guid id, ContextoPlataforma db, ContextoPeticion ctx, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();

            var s = await db.Sucursales.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == id && x.EmpresaId == empresaId && x.EliminadaEn == null, ct);
            if (s is null) return Results.NotFound(new { error = "Esa sucursal ya no existe." });

            var mapa = await Sucursales.MapaAsync(db, empresaId, ct);
            var (series, seriesOk) = await SeriesDelServicioAsync(facturacion, ct);
            var suyas = series.Where(x => mapa.TryGetValue(Sucursales.Clave(x.TipoComprobante, x.Serie), out var m) && m == s.Id).ToList();
            var docs = await SinAlcance.LeerAsync(db, () => db.Documentos.IgnoreQueryFilters().AsNoTracking().CountAsync(d => d.SucursalId == s.Id, ct), ct);

            var (puede, motivo) = PuedeEliminar(s, docs, suyas, seriesOk);
            if (!puede) return Results.Conflict(new { error = motivo });

            var ahora = DateTime.UtcNow;
            s.Activo = false;
            s.EliminadaEn = ahora;

            foreach (var x in await db.SucursalSeries.Where(x => x.SucursalId == s.Id).ToListAsync(ct))
                x.Activo = false;
            foreach (var x in await db.UsuarioSucursales.Where(x => x.SucursalId == s.Id).ToListAsync(ct))
                x.Activo = false;

            if (await GuardarAsync(db, ct) is { } choque) return choque;

            // Sus series sin usar se quitan del servicio (si no se puede, se
            // quedan como de la 0000, sin usar: no estorban).
            foreach (var x in suyas)
                await facturacion.ConfigurarAsync<JsonElement>(HttpMethod.Delete, $"v1/configuracion/series/{x.Id}", ct: ct);

            return Results.Ok(new { mensaje = $"«{s.Nombre}» ({s.Codigo}) eliminada. Nunca emitió, así que no queda nada suyo; su código queda libre." });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Eliminar una sucursal que nunca tuvo movimiento")
        .WithDescription("Con cualquier comprobante (aunque sea un borrador) o una serie usada, no: se desactiva.");


        // =====================================================================
        // USUARIOS POR SUCURSAL
        // =====================================================================
        grupo.MapGet("/usuarios", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();

            var usuarios = await db.Usuarios.AsNoTracking()
                .Where(u => u.EmpresaId == empresaId)
                .OrderBy(u => u.Nombre)
                .Select(u => new { u.Id, u.Nombre, u.Correo })
                .ToListAsync(ct);

            // Todas sus asignaciones, también a sucursales desactivadas (la
            // pantalla las marca): así se ve a quien se quedó sin dónde emitir.
            var asignaciones = (await db.UsuarioSucursales.AsNoTracking()
                    .Where(x => x.EmpresaId == empresaId)
                    .Select(x => new { x.UsuarioId, x.SucursalId })
                    .ToListAsync(ct))
                .GroupBy(x => x.UsuarioId).ToDictionary(g => g.Key, g => g.Select(x => x.SucursalId).ToList());

            return Results.Ok(usuarios.Select(u => new
            {
                u.Id, u.Nombre, u.Correo,
                sucursales = asignaciones.GetValueOrDefault(u.Id) ?? [],   // vacío = todas
                todas = !asignaciones.ContainsKey(u.Id)
            }));
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("En qué sucursales trabaja cada usuario");

        grupo.MapPut("/usuarios/{usuarioId:guid}", async (
            Guid usuarioId, PeticionUsuarioSucursales p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return SinEmpresa();

            var usuario = await db.Usuarios.AsNoTracking()
                .Where(u => u.Id == usuarioId && u.EmpresaId == empresaId)
                .Select(u => new { u.Id, u.Nombre }).FirstOrDefaultAsync(ct);
            if (usuario is null) return Results.NotFound(new { error = "Ese usuario no es de esta empresa." });

            var pedidas = (p.Sucursales ?? []).Distinct().ToList();
            if (pedidas.Count > 0)
            {
                var validas = await db.Sucursales.AsNoTracking()
                    .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null && pedidas.Contains(s.Id))
                    .Select(s => s.Id).ToListAsync(ct);
                if (validas.Count != pedidas.Count)
                    return Mal("Alguna de esas sucursales no existe o está desactivada.", "sucursales");
            }

            var actuales = await db.UsuarioSucursales.Where(x => x.UsuarioId == usuarioId).ToListAsync(ct);
            foreach (var a in actuales.Where(a => !pedidas.Contains(a.SucursalId)))
                a.Activo = false;
            foreach (var n in pedidas.Where(n => actuales.All(a => a.SucursalId != n)))
                db.UsuarioSucursales.Add(new UsuarioSucursal { EmpresaId = empresaId, UsuarioId = usuarioId, SucursalId = n });

            if (await GuardarAsync(db, ct) is { } choque) return choque;
            return Results.Ok(new
            {
                mensaje = pedidas.Count == 0
                    ? $"{usuario.Nombre} trabaja en todas las sucursales."
                    : $"{usuario.Nombre} trabaja en {pedidas.Count} sucursal{(pedidas.Count > 1 ? "es" : "")}: al emitir solo verá sus series."
            });
        })
        .RequierePermiso(Gestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Asignar a un usuario sus sucursales (vacío = todas)");
    }

    // =========================================================================

    private static IResult SinEmpresa() =>
        Results.BadRequest(new { error = "Elige primero una empresa." });

    private static IResult Mal(string error, string? campo) =>
        Results.BadRequest(new { error, campo });

    /// <summary>¿Se puede eliminar? Sin comprobantes (ni borradores) y con sus series sin usar.</summary>
    private static (bool Puede, string? Motivo) PuedeEliminar(Sucursal s, int documentos, List<SerieConId> series, bool seriesLeidas)
    {
        if (s.Codigo == Sucursales.Principal)
            return (false, "La 0000 es el domicilio fiscal: existe siempre.");
        if (documentos > 0)
            return (false, $"Tiene {documentos} comprobante{(documentos > 1 ? "s" : "")} (contando borradores). Solo se puede desactivar.");
        if (series.FirstOrDefault(x => x.UltimoCorrelativo > 0 || x.Comprobantes > 0) is { } usada)
            return (false, $"Su serie {usada.Serie} ya se usó (va en la {usada.UltimoCorrelativo}). Solo se puede desactivar.");
        if (!seriesLeidas)
            return (false, "No se pudo consultar sus series al servicio de facturación. Inténtalo en unos minutos.");
        return (true, null);
    }

    private static async Task<bool> TieneDocumentosAsync(ContextoPlataforma db, Sucursal s, CancellationToken ct) =>
        await SinAlcance.LeerAsync(db, () => db.Documentos.IgnoreQueryFilters().AsNoTracking().AnyAsync(d => d.SucursalId == s.Id, ct), ct);

    private static async Task<(List<SerieConId> Series, bool Ok)> SeriesDelServicioAsync(ClienteFacturacion facturacion, CancellationToken ct)
    {
        var r = await facturacion.ConfigurarAsync<List<SerieConId>>(HttpMethod.Get, "v1/configuracion/series", ct: ct);
        return r.Exito && r.Datos is not null ? (r.Datos, true) : ([], false);
    }

    /// <summary>Nombre, dirección, ubigeo y que ni el código ni el nombre se repitan.</summary>
    private static async Task<IResult?> ProblemaDatosAsync(
        PeticionSucursal p, ContextoPlataforma db, Guid empresaId, Guid? excepto, CancellationToken ct)
    {
        if (Sucursales.ProblemaNombre(p.Nombre) is { } mn) return Mal(mn, "nombre");
        if (Sucursales.ProblemaDireccion(p.Direccion) is { } md) return Mal(md, "direccion");
        if (await Sucursales.ProblemaUbigeoAsync(db, p.Ubigeo, ct) is { } mu) return Mal(mu, "ubigeo");

        var codigo = (p.Codigo ?? "").Trim();
        if (codigo.Length > 0)
        {
            var conCodigo = await db.Sucursales.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null && s.Codigo == codigo && s.Id != excepto)
                .Select(s => new { s.Nombre, s.Activo }).FirstOrDefaultAsync(ct);
            if (conCodigo is not null)
                return Mal($"El código {codigo} ya es de la sucursal «{conCodigo.Nombre}»" +
                           (conCodigo.Activo ? "." : " (desactivada). Reactívala en vez de crear otra."), "codigo");
        }

        if (await NombreRepetidoAsync(db, empresaId, p.Nombre!, excepto, ct) is { } otra)
            return Mal($"Ya hay una sucursal que se llama «{otra}». Usa otro nombre.", "nombre");

        return null;
    }

    private static async Task<string?> NombreRepetidoAsync(ContextoPlataforma db, Guid empresaId, string nombre, Guid? excepto, CancellationToken ct)
    {
        var buscado = Sucursales.Comparable(nombre);
        var activas = await db.Sucursales.AsNoTracking()
            .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null && s.Id != excepto)
            .Select(s => s.Nombre).ToListAsync(ct);
        return activas.FirstOrDefault(n => Sucursales.Comparable(n) == buscado);
    }

    private static async Task<string?> DireccionRepetidaAsync(
        ContextoPlataforma db, Guid empresaId, string? direccion, string? ubigeo, Guid? excepto, CancellationToken ct)
    {
        var d = Sucursales.Comparable(direccion);
        var u = (ubigeo ?? "").Trim();
        var otras = await db.Sucursales.AsNoTracking()
            .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null && s.Id != excepto)
            .Select(s => new { s.Nombre, s.Codigo, s.Direccion, s.Ubigeo }).ToListAsync(ct);
        var otra = otras.FirstOrDefault(s => Sucursales.Comparable(s.Direccion) == d && (s.Ubigeo ?? "").Trim() == u);
        return otra is null ? null : $"{otra.Nombre} ({otra.Codigo})";
    }

    /// <summary>
    /// Revisa las series pedidas: forma, sin repetir, que no sean de OTRA
    /// sucursal y, si ya existen en el servicio, que no se hayan usado (una
    /// serie usada se queda donde emitió). Devuelve las que hay que asignar.
    /// </summary>
    private static async Task<(List<PeticionSerieSucursal>? Series, IResult? Error)> RevisarSeriesAsync(
        List<PeticionSerieSucursal>? pedidas, ContextoPlataforma db, ClienteFacturacion facturacion,
        Guid empresaId, Guid principalId, Guid? destino, CancellationToken ct)
    {
        var limpias = new List<PeticionSerieSucursal>();
        foreach (var x in pedidas ?? [])
        {
            if (string.IsNullOrWhiteSpace(x.Serie)) continue;
            if (Sucursales.ProblemaSerie(x.Tipo, x.Serie) is { } m) return (null, Mal(m, "series"));
            var limpia = new PeticionSerieSucursal(x.Tipo.Trim(), x.Serie.Trim().ToUpperInvariant());
            if (limpias.Any(y => y.Tipo == limpia.Tipo && y.Serie == limpia.Serie))
                return (null, Mal($"Pusiste {limpia.Serie} dos veces.", "series"));
            limpias.Add(limpia);
        }
        if (limpias.Count == 0) return (limpias, null);

        var mapa = await Sucursales.MapaAsync(db, empresaId, ct);
        var (servicio, ok) = await SeriesDelServicioAsync(facturacion, ct);
        if (!ok)
            return (null, Results.Json(new { error = "No pudimos hablar con el servicio de facturación para revisar las series. No se guardó nada: inténtalo en unos minutos." },
                                       statusCode: StatusCodes.Status502BadGateway));

        foreach (var x in limpias)
        {
            var clave = Sucursales.Clave(x.Tipo, x.Serie);
            var duena = mapa.TryGetValue(clave, out var m) ? m : principalId;
            var existe = servicio.FirstOrDefault(s => s.TipoComprobante == x.Tipo && s.Serie == x.Serie);

            if (existe is null) continue;                 // nueva: se crea
            if (duena == (destino ?? Guid.Empty))         // ya es de esta sucursal
                return (null, Mal($"{x.Serie} ya es de esta sucursal.", "series"));

            var nombre = await db.Sucursales.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Id == duena).Select(s => s.Nombre + " (" + s.Codigo + ")").FirstOrDefaultAsync(ct) ?? "otra sucursal";

            if (duena != principalId)
                return (null, Mal($"{x.Serie} ya es de la sucursal «{nombre}». Cada serie pertenece a una sola sucursal: usa otra (ej.: {Siguiente(x.Serie)}).", "series"));
            if (existe.UltimoCorrelativo > 0 || existe.Comprobantes > 0)
                return (null, Mal($"{x.Serie} ya se usa en «{nombre}» (va en la {existe.UltimoCorrelativo}). Una serie usada se queda donde emitió: crea otra para esta sucursal (ej.: {Siguiente(x.Serie)}).", "series"));
            // Existe sin usar y es de la 0000: se pasa a esta sucursal, sin crear nada.
        }

        return (limpias, null);
    }

    /// <summary>Crea en el servicio las series que todavía no existen (en 0). Un 409 = ya existía: sirve.</summary>
    private static async Task<IResult?> CrearSeriesAsync(
        List<PeticionSerieSucursal> series, ContextoPlataforma db, ContextoPeticion ctx,
        ClienteFacturacion facturacion, CancellationToken ct)
    {
        if (series.Count == 0) return null;

        var correo = await db.Usuarios.AsNoTracking()
            .Where(u => u.Id == ctx.UsuarioId).Select(u => u.Correo).FirstOrDefaultAsync(ct);

        foreach (var x in series)
        {
            var r = await facturacion.ConfigurarAsync<JsonElement>(HttpMethod.Post, "v1/configuracion/series", new
            {
                tipoComprobante = x.Tipo,
                serie = x.Serie,
                ultimoCorrelativo = 0,
                confirmado = false,
                actor = correo
            }, ct: ct);

            if (r.Exito || r.Estado == 409) continue;

            return r.Estado == 400
                ? Mal($"{x.Serie}: {r.Error} {r.Detalle}".Trim(), "series")
                : Results.Json(new { error = $"No se pudo crear la serie {x.Serie} en el servicio de facturación. No se guardó la sucursal: inténtalo en unos minutos." },
                               statusCode: StatusCodes.Status502BadGateway);
        }
        return null;
    }

    /// <summary>F002 → F003; FC01 → FC02. Solo para sugerir.</summary>
    private static string Siguiente(string serie)
    {
        var digitos = new string(serie.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
        if (digitos.Length == 0) return serie;
        var n = int.Parse(digitos) + 1;
        var nuevo = n.ToString().PadLeft(digitos.Length, '0');
        return nuevo.Length > digitos.Length ? serie : serie[..^digitos.Length] + nuevo;
    }

    /// <summary>Guarda; un choque con los índices únicos (otra persona al mismo tiempo) se dice en palabras.</summary>
    private static async Task<IResult?> GuardarAsync(ContextoPlataforma db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg)
        {
            return Results.Conflict(new
            {
                error = pg.ConstraintName switch
                {
                    "ux_sucursales_codigo" => "Ya existe una sucursal con ese código.",
                    "ux_sucursales_nombre" => "Ya hay una sucursal activa con ese nombre.",
                    "ux_sucursal_series"   => "Una de esas series ya es de otra sucursal.",
                    _ => "Otra persona cambió esto al mismo tiempo. Recarga y vuelve a intentarlo."
                }
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { error = "Otra persona cambió esta sucursal al mismo tiempo. Recarga y vuelve a intentarlo." });
        }
    }

    private static string Titulo(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" :
        System.Globalization.CultureInfo.GetCultureInfo("es-PE").TextInfo.ToTitleCase(s.Trim().ToLowerInvariant());
}