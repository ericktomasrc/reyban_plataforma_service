using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Guias;

// =============================================================================
// LO QUE MANDA LA PANTALLA
// =============================================================================

public sealed record PersonaGuia(string? TipoDocumento, string? NumeroDocumento, string? RazonSocial);
public sealed record PuntoGuia(string? Direccion, string? Ubigeo, string? CodigoEstablecimiento = null);
public sealed record VehiculoGuia(string? Placa, string? Descripcion = null, string? Tuc = null,
                                  string? Autorizacion = null, string? Entidad = null);
public sealed record ConductorGuia(string? TipoDocumento, string? NumeroDocumento, string? Nombres,
                                   string? Apellidos, string? Licencia, Guid? UsuarioId = null);
public sealed record TransportistaGuia(string? Ruc, string? RazonSocial, string? Mtc = null);
/// <summary>24 oct: otro documento relacionado (catálogo 61): constancia de detracción, DAM, otra factura…</summary>
public sealed record DocumentoGuia(string? Tipo, string? Numero, string? Ruc = null);

public sealed record BienGuia(string? Descripcion, decimal Cantidad, string? Unidad, string? Codigo = null,
                              Guid? ProductoId = null, Guid? DocumentoLineaId = null);

public sealed record PeticionGuiaPlataforma(
    string? Clave,
    string? Tipo,
    string? Serie,
    DateOnly? FechaEmision,
    DateOnly? FechaTraslado,
    string? Motivo,
    string? MotivoDescripcion,
    string? Modalidad,
    PersonaGuia? Destinatario,
    Guid? ClienteId,
    PuntoGuia? Partida,
    PuntoGuia? Llegada,
    decimal PesoBruto,
    int? Bultos,
    List<BienGuia>? Bienes,
    VehiculoGuia? Vehiculo = null,
    ConductorGuia? Conductor = null,
    List<ConductorGuia>? Relevos = null,
    TransportistaGuia? Transportista = null,
    DateOnly? EntregaAlTransportista = null,
    bool VehiculoMenor = false,
    Guid? DocumentoId = null,
    string? Observaciones = null,
    // --- solo la 31 ---
    PersonaGuia? Remitente = null,
    string? GuiaRemitente = null,
    PersonaGuia? PagadorFlete = null,
    TransportistaGuia? Subcontratista = null,
    // --- 24 oct (de los manuales de SOL) ---
    string? UnidadPeso = null,                 // KGM (kilos) o TNE (toneladas)
    bool TransbordoProgramado = false,
    bool RetornoVacio = false,
    bool RetornoConEnvases = false,
    List<DocumentoGuia>? Documentos = null);

public sealed record PeticionAnularGuia(string? Motivo, DateOnly? Fecha);

/// <summary>051: una GRE por evento hecha en SOL, para registrarla aquí.</summary>
public sealed record PeticionEventoGuia(
    string? Tipo, string? NumeroSol, DateOnly? Fecha,
    PuntoGuia? Reinicio, PuntoGuia? Llegada,
    string? Placa, ConductorGuia? Conductor, string? Observacion);
public sealed record PeticionTransporteEmpresa(bool TransportaCarga, string? RegistroMtc);
public sealed record PeticionGre(string? ClientId, string? ClientSecret);

/// <summary>Lo que contesta el servicio al recibir una guía (202).</summary>
public sealed record RespuestaGuiaServicio(Guid Id, string Numero, string Estado, bool Duplicado);

/// <summary>El estado de una guía en el servicio (GET /v1/guias/{id}).</summary>
public sealed record EstadoGuiaServicio(Guid Id, string Numero, string Estado, string? CodigoSunat, string? MensajeSunat);

/// <summary>Las credenciales de guías, como las devuelve el servicio (sin el secreto).</summary>
public sealed record GreDelServicio(bool Habilitado, string? ClientId, bool TieneSecreto, string? Ambiente);


/// <summary>
/// 050 — GUÍAS DE REMISIÓN (09 remitente y 31 transportista).
///
///   GET  /api/guias                          la lista (con el alcance de cada persona)
///   GET  /api/guias/contexto                 lo que necesita «Nueva guía»: sucursales, series, empresa
///   GET  /api/guias/desde/{documentoId}      una factura o boleta: qué falta despachar y sus guías
///   POST /api/guias                          emitir (directo: no hay borrador)
///   GET  /api/guias/{id}                     ver una guía
///   POST /api/guias/{id}/actualizar          preguntar su estado al servicio
///   POST /api/guias/{id}/reintentar          volver a mandar una que no llegó (misma clave)
///   POST /api/guias/{id}/anulacion           registrar que se anuló en SOL
///   GET  /api/guias/{id}/pdf | xml | cdr     los archivos
///
/// EL VEHÍCULO SALE CON LA GUÍA ACEPTADA. A diferencia de una factura, la guía
/// se emite antes del traslado y es lo que se muestra en un control.
///
/// EL TRANSPORTE SE GUARDA SOLO: al emitir, la placa, el conductor y la
/// empresa de transporte quedan en la maestra de la empresa (EndpointsTransporte).
/// </summary>
public static class EndpointsGuias
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Regex SerieT = new("^T[0-9]{3}$", RegexOptions.Compiled);
    private static readonly Regex SerieV = new("^V[0-9]{3}$", RegexOptions.Compiled);
    private static readonly Regex Ubigeo = new("^[0-9]{6}$", RegexOptions.Compiled);
    private static readonly Regex Placa = new("^[A-Z0-9]{6,8}$", RegexOptions.Compiled);   // SUNAT 2567: 6 a 8 letras y números
    private static readonly Regex Ruc = new("^(10|15|17|20)[0-9]{9}$", RegexOptions.Compiled);
    private static readonly Regex NumeroGuia = new("^[TV0-9][A-Z0-9]{3}-[0-9]{1,8}$", RegexOptions.Compiled);
    private static readonly Regex Licencia = new("^[A-Z0-9-]{9,10}$", RegexOptions.Compiled);

    /// <summary>
    /// 24 oct: catálogo 61, los documentos relacionados que se pueden agregar a
    /// mano. La factura o boleta de origen va sola.
    /// </summary>
    public static readonly Dictionary<string, string> TiposDocumento = new()
    {
        ["01"] = "Factura",
        ["03"] = "Boleta de venta",
        ["04"] = "Liquidación de compra",
        ["09"] = "Guía de remisión remitente",
        ["12"] = "Ticket o cinta de máquina registradora",
        ["31"] = "Guía de remisión transportista",
        ["48"] = "Comprobante de operaciones (Ley 29972)",
        ["49"] = "Constancia de depósito IVAP",
        ["50"] = "Declaración aduanera de mercancías (DAM)",
        ["52"] = "Declaración simplificada (DS)",
        ["80"] = "Constancia de depósito de detracción (SPOT)",
        ["81"] = "Código de autorización SCOP",
        ["82"] = "Declaración jurada de mudanza"
    };

    /// <summary>Los que llevan el RUC de quien los emitió (si se deja vacío, el tuyo).</summary>
    private static readonly string[] DocumentosConRuc = ["01", "03", "04", "09", "12", "31", "48"];

    /// <summary>Catálogo 20: por qué se traslada. Lo que ofrece la pantalla.</summary>
    public static readonly Dictionary<string, string> Motivos = new()
    {
        ["01"] = "Venta",
        ["14"] = "Venta sujeta a confirmación del comprador",
        ["03"] = "Venta con entrega a terceros",
        ["02"] = "Compra",
        ["04"] = "Traslado entre establecimientos de la misma empresa",
        ["05"] = "Consignación",
        ["06"] = "Devolución",
        ["07"] = "Recojo de bienes transformados",
        ["17"] = "Traslado de bienes para transformación",
        ["08"] = "Importación",
        ["09"] = "Exportación",
        ["18"] = "Traslado emisor itinerante",
        ["13"] = "Otros"
    };

    public static void MapearGuias(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/guias").WithTags("Guías de remisión");

        // =====================================================================
        // LISTAR
        // =====================================================================
        grupo.MapGet("/", async (
            string? tipo, string? estado, string? q, int? pagina,
            DateOnly? desde, DateOnly? hasta, Guid? sucursalId, int? porPagina,   // 24 oct
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var PorPagina = Listados.PorPagina(porPagina);
            var p = Listados.Pagina(pagina);

            var consulta = db.Guias.AsNoTracking();
            // 24 oct: por fechas de emisión y por sucursal (la placa entra por el buscador).
            if (desde is { } d0) consulta = consulta.Where(g => g.FechaEmision >= d0);
            if (hasta is { } d1) consulta = consulta.Where(g => g.FechaEmision <= d1);
            if (sucursalId is { } sid) consulta = consulta.Where(g => g.SucursalId == sid);
            if (tipo is "09" or "31") consulta = consulta.Where(g => g.Tipo == tipo);
            if (!string.IsNullOrWhiteSpace(estado)) consulta = consulta.Where(g => g.Estado == estado);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var t = q.Trim().ToLowerInvariant();
                consulta = consulta.Where(g =>
                    g.Serie.ToLower().Contains(t) ||
                    g.DestinatarioNombre.ToLower().Contains(t) ||
                    g.DestinatarioDoc.Contains(t) ||
                    (g.VehiculoPlaca != null && g.VehiculoPlaca.ToLower().Contains(t)));
            }

            var total = await consulta.CountAsync(ct);

            // Para los filtros de arriba: cuántas de cada.
            var conteo = await db.Guias.AsNoTracking()
                .GroupBy(g => new { g.Tipo, g.Estado })
                .Select(x => new { x.Key.Tipo, x.Key.Estado, n = x.Count() })
                .ToListAsync(ct);

            var filas = await consulta
                .OrderByDescending(g => g.FechaEmision).ThenByDescending(g => g.CreadoEn)
                .Skip((p - 1) * PorPagina).Take(PorPagina)
                .Select(g => new
                {
                    g.Id, g.Tipo, g.Serie, g.Correlativo, g.Estado, g.Ambiente,
                    g.FechaEmision, g.FechaTraslado, g.Motivo, g.Modalidad,
                    g.DestinatarioNombre, g.DestinatarioDoc, g.LlegadaDireccion,
                    g.VehiculoPlaca, g.ConductorNombre, g.TransportistaNombre,
                    g.DocumentoId, g.DocumentoRelacionado, g.SucursalId,
                    g.CodigoSunat, g.MensajeSunat, g.AnuladaEn
                })
                .ToListAsync(ct);

            var docIds = filas.Where(f => f.DocumentoId is not null).Select(f => f.DocumentoId!.Value).Distinct().ToList();
            var numerosDoc = docIds.Count == 0 ? [] : (await db.Documentos.AsNoTracking()
                    .Where(d => docIds.Contains(d.Id))
                    .Select(d => new { d.Id, d.Serie, d.Correlativo })
                    .ToListAsync(ct))
                .ToDictionary(d => d.Id, d => $"{d.Serie}-{d.Correlativo ?? 0:D8}");

            var sucIds = filas.Where(f => f.SucursalId is not null).Select(f => f.SucursalId!.Value).Distinct().ToList();
            var sucursales = sucIds.Count == 0 ? [] : (await db.Sucursales.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => sucIds.Contains(s.Id))
                    .Select(s => new { s.Id, s.Codigo, s.Nombre })
                    .ToListAsync(ct))
                .ToDictionary(s => s.Id, s => $"{s.Codigo} {s.Nombre}");

            return Results.Ok(new
            {
                total, pagina = p, porPagina = PorPagina,
                conteo = new
                {
                    todas = conteo.Sum(x => x.n),
                    remitente = conteo.Where(x => x.Tipo == "09").Sum(x => x.n),
                    transportista = conteo.Where(x => x.Tipo == "31").Sum(x => x.n),
                    rechazadas = conteo.Where(x => x.Estado is EstadoGuia.Rechazado or EstadoGuia.Error).Sum(x => x.n),
                    enCamino = conteo.Where(x => x.Estado == EstadoGuia.Encolado).Sum(x => x.n)
                },
                filas = filas.Select(f => new
                {
                    f.Id, f.Tipo, f.Estado, f.Ambiente,
                    numero = f.Correlativo is { } c ? $"{f.Serie}-{c:D8}" : null,
                    f.Serie, f.FechaEmision, f.FechaTraslado,
                    motivo = Motivos.GetValueOrDefault(f.Motivo) ?? f.Motivo,
                    f.Modalidad,
                    destinatario = f.DestinatarioNombre, f.DestinatarioDoc,
                    llegada = f.LlegadaDireccion,
                    placa = f.VehiculoPlaca, conductor = f.ConductorNombre, transportista = f.TransportistaNombre,
                    documento = f.DocumentoId is { } di && numerosDoc.TryGetValue(di, out var nd) ? nd : null,
                    f.DocumentoId, f.DocumentoRelacionado,
                    sucursal = f.SucursalId is { } si && sucursales.TryGetValue(si, out var ns) ? ns : null,
                    f.CodigoSunat, f.MensajeSunat, f.AnuladaEn
                })
            });
        })
        .RequierePermiso(Permisos.Guias.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las guías de mi empresa");


        // =====================================================================
        // LO QUE NECESITA «NUEVA GUÍA»
        // =====================================================================
        grupo.MapGet("/contexto", async (
            ContextoPlataforma db, ContextoPeticion ctx, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });

            var empresa = await db.Empresas.AsNoTracking().FirstAsync(e => e.Id == empresaId, ct);
            var locales = await LocalesAsync(db, ctx, facturacion, empresaId, ct);

            return Results.Ok(new
            {
                empresa = new
                {
                    ruc = empresa.Ruc, razonSocial = empresa.RazonSocial ?? empresa.Nombre,
                    ambiente = empresa.Ambiente == "produccion" ? "produccion" : "beta",
                    transportaCarga = empresa.TransportaCarga, registroMtc = empresa.RegistroMtc
                },
                sucursales = locales.Sucursales,
                seriesLeidas = locales.SeriesLeidas,
                motivos = Motivos.Select(m => new { codigo = m.Key, nombre = m.Value }),
                tiposDocumento = TiposDocumento.Select(m => new { codigo = m.Key, nombre = m.Value }),   // 24 oct
                hoy = HoyEnPeru()
            });
        })
        .RequierePermiso(Permisos.Guias.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Sucursales, series y datos de la empresa para emitir una guía");


        // =====================================================================
        // DESDE UNA FACTURA O BOLETA: qué falta despachar y sus guías
        // =====================================================================
        // 24 oct: «Nueva guía» suelta: buscar la factura o boleta de la que sale
        // (por número o cliente) y seguir como «Hacer guía» desde ella.
        grupo.MapGet("/buscar-comprobante", async (string? q, ContextoPlataforma db, CancellationToken ct) =>
            Results.Ok(await EndpointsNotasCredito.BuscarComprobantesAsync(db, q, ct)))
        .RequierePermiso(Permisos.Guias.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Buscar la factura o boleta de la que sale una guía");

        grupo.MapGet("/buscar-comprobante/series", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Ok(await EndpointsNotasCredito.SeriesEmitidasAsync(db, ct)))
        .RequierePermiso(Permisos.Guias.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las series de facturas y boletas emitidas");

        grupo.MapGet("/desde/{documentoId:guid}", async (
            Guid documentoId, ContextoPlataforma db, CancellationToken ct) =>
        {
            var d = await db.Documentos.AsNoTracking()
                .Include(x => x.Lineas).Include(x => x.Cliente)
                .FirstOrDefaultAsync(x => x.Id == documentoId, ct);
            if (d is null) return Results.NotFound(new { error = "No se encontró el comprobante." });

            return Results.Ok(await DespachoAsync(db, d, ct));
        })
        .RequierePermiso(Permisos.Guias.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Lo que falta despachar de un comprobante y sus guías");


        // =====================================================================
        // EMITIR
        // =====================================================================
        grupo.MapPost("/", async (
            PeticionGuiaPlataforma p, ContextoPlataforma db, ContextoPeticion ctx,
            ClienteFacturacion facturacion, OpcionesConexionSunat conexionSunat,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var log = logs.CreateLogger("Guias");

            var clave = (p.Clave ?? "").Trim();
            if (clave.Length is < 16 or > 64)
                return Results.BadRequest(new { error = "Falta la clave de la guía. Vuelve a abrir la pantalla." });

            // DOBLE CLIC: la misma clave es la misma guía.
            var previa = await SinAlcance.LeerAsync(db, () => db.Guias.AsNoTracking()
                .Where(g => g.EmpresaId == empresaId && g.IdempotencyKey == clave)
                .Select(g => new { g.Id, g.Serie, g.Correlativo, g.Estado })
                .FirstOrDefaultAsync(ct), ct);
            if (previa is not null)
                return Results.Ok(new
                {
                    previa.Id, previa.Estado, duplicado = true,
                    numero = previa.Correlativo is { } c0 ? $"{previa.Serie}-{c0:D8}" : previa.Serie,
                    mensaje = "Esta guía ya se había mandado."
                });

            var empresa = await db.Empresas.AsNoTracking().FirstAsync(e => e.Id == empresaId, ct);
            var tipo = p.Tipo == "31" ? "31" : "09";

            if (tipo == "31" && !empresa.TransportaCarga)
                return Results.BadRequest(new
                {
                    error = "Tu empresa no está marcada como transportista.",
                    detalle = "La guía del transportista es para quien lleva carga de otros. Actívalo en Configuración › Guías de remisión."
                });

            // --- La factura o boleta de la que sale -----------------------------
            Documento? origen = null;
            if (p.DocumentoId is { } docId)
            {
                origen = await db.Documentos.AsNoTracking().Include(x => x.Lineas).Include(x => x.Cliente)
                    .FirstOrDefaultAsync(x => x.Id == docId, ct);
                if (origen is null) return Results.BadRequest(new { error = "No se encontró el comprobante de esta guía." });
                if (await ProblemaOrigenAsync(db, origen, ct) is { } malOrigen) return Results.BadRequest(new { error = malOrigen });
            }

            if (await RevisarAsync(p, tipo, empresa, origen, db, ct) is { } error)
                return Results.BadRequest(new { error });

            // --- La serie y su sucursal ------------------------------------------
            var serie = (p.Serie ?? "").Trim().ToUpperInvariant();
            if (!(tipo == "09" ? SerieT : SerieV).IsMatch(serie))
                return Results.BadRequest(new
                {
                    error = tipo == "09"
                        ? $"«{serie}» no es una serie de guía del remitente: empieza con T y tres números. Ej.: T001."
                        : $"«{serie}» no es una serie de guía del transportista: empieza con V y tres números. Ej.: V001."
                });

            var locales = await LocalesAsync(db, ctx, facturacion, empresaId, ct);
            if (!locales.SeriesLeidas)
                return Results.Json(new { error = "No se pudieron leer tus series del servicio de facturación. Inténtalo en unos minutos." },
                                    statusCode: StatusCodes.Status502BadGateway);

            var local = locales.Sucursales.FirstOrDefault(s =>
                (tipo == "09" ? s.Series09 : s.Series31).Any(x => x.Serie == serie) ||
                (tipo == "09" ? s.Nueva09 : s.Nueva31) == serie);
            if (local is null)
                return Results.BadRequest(new { error = $"No puedes emitir con la serie {serie}: no es de una sucursal en la que trabajas." });

            var existe = (tipo == "09" ? local.Series09 : local.Series31).Any(x => x.Serie == serie);

            var suc = await Sucursales.RevisarSerieAsync(db, ctx, empresaId, tipo, serie, ct);
            // Una serie nueva todavía no está en el mapa: es de la sucursal que se eligió.
            Guid? sucursalId = existe ? suc.Id : local.Id;
            if (existe && suc.Error is { } errorSucursal) return Results.BadRequest(new { error = errorSucursal });
            if (!existe && ctx.SucursalVista is { } vista && vista != local.Id)
                return Results.BadRequest(new { error = $"Arriba tienes elegida otra sucursal. Cambia arriba a «{local.Nombre}»." });

            // --- Modo prueba / real y credenciales de guías -----------------------
            var conexion = await ConexionSunat.LeerAsync(db, conexionSunat, ct);
            if (ConexionSunat.Bloqueo(conexion, empresa.Ambiente) is { } bloqueo)
                return Results.Json(new { error = bloqueo, codigo = "conexion_sunat" }, statusCode: StatusCodes.Status409Conflict);

            var ambiente = empresa.Ambiente == "produccion" ? "produccion" : "beta";
            if (ambiente == "produccion")
            {
                var gre = await facturacion.ConfigurarAsync<GreDelServicio>(HttpMethod.Get, "v1/configuracion/gre", ct: ct);
                if (gre.Exito && gre.Datos is { Habilitado: false })
                    return Results.Json(new
                    {
                        error = "Faltan las credenciales de guías de tu empresa.",
                        detalle = "Las guías van por otro canal de SUNAT y piden su propio ID y clave (SOL › Credenciales de API SUNAT). " +
                                  "Ponlos en Configuración › Guías de remisión.",
                        codigo = "gre_sin_credenciales"
                    }, statusCode: StatusCodes.Status409Conflict);
            }

            // --- Serie nueva: se crea en el servicio y se asigna ------------------
            if (!existe)
            {
                var correo = await db.Usuarios.AsNoTracking().Where(u => u.Id == ctx.UsuarioId)
                    .Select(u => u.Correo).FirstOrDefaultAsync(ct);
                var rs = await facturacion.ConfigurarAsync<JsonElement>(HttpMethod.Post, "v1/configuracion/series", new
                {
                    tipoComprobante = tipo, serie, ultimoCorrelativo = 0, confirmado = false, actor = correo
                }, ct: ct);
                if (!rs.Exito && rs.Estado != 409)
                    return Results.Json(new { error = $"No se pudo crear la serie {serie}: {rs.Error} {rs.Detalle}".Trim() },
                                        statusCode: rs.Estado == 400 ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);

                if (!local.EsPrincipal)
                {
                    db.SucursalSeries.Add(new SucursalSerie { EmpresaId = empresaId, SucursalId = local.Id, TipoComprobante = tipo, Serie = serie });
                    await db.SaveChangesAsync(ct);
                }
            }

            // --- Armar y guardar ANTES de llamar ---------------------------------
            var codigosSunat = await CodigosSunatAsync(db, p.Bienes!, ct);
            var cuerpo = ArmarServicio(p, tipo, serie, empresa, origen, local, codigosSunat);

            var g = new Guia
            {
                EmpresaId = empresaId,
                SucursalId = local.EsPrincipal ? local.Id : sucursalId,
                Tipo = tipo,
                Serie = serie,
                Estado = EstadoGuia.Encolado,
                Ambiente = ambiente,
                FechaEmision = p.FechaEmision ?? HoyEnPeru(),
                FechaTraslado = p.FechaTraslado!.Value,
                Motivo = tipo == "31" ? "01" : p.Motivo!,
                MotivoDescripcion = Limpio(p.MotivoDescripcion, 100),
                Modalidad = tipo == "31" ? "02" : p.Modalidad!,
                DestinatarioTipo = p.Destinatario!.TipoDocumento!.Trim(),
                DestinatarioDoc = p.Destinatario.NumeroDocumento!.Trim(),
                DestinatarioNombre = p.Destinatario.RazonSocial!.Trim(),
                ClienteId = p.ClienteId ?? origen?.ClienteId,
                PartidaDireccion = p.Partida!.Direccion!.Trim(),
                PartidaUbigeo = p.Partida.Ubigeo!.Trim(),
                LlegadaDireccion = p.Llegada!.Direccion!.Trim(),
                LlegadaUbigeo = p.Llegada.Ubigeo!.Trim(),
                PesoBruto = p.PesoBruto,
                Bultos = p.Bultos,
                VehiculoPlaca = PlacaLimpia(p.Vehiculo?.Placa),
                ConductorDoc = p.Conductor?.NumeroDocumento?.Trim(),
                ConductorNombre = p.Conductor is null ? null : $"{p.Conductor.Nombres?.Trim()} {p.Conductor.Apellidos?.Trim()}".Trim(),
                TransportistaRuc = tipo == "09" && p.Modalidad == "01" ? p.Transportista?.Ruc?.Trim() : null,
                TransportistaNombre = tipo == "09" && p.Modalidad == "01" ? p.Transportista?.RazonSocial?.Trim() : null,
                DocumentoId = origen?.Id,
                DocumentoRelacionado = tipo == "31" && !string.IsNullOrWhiteSpace(p.GuiaRemitente) ? p.GuiaRemitente.Trim().ToUpperInvariant() : null,
                Detalle = JsonSerializer.Serialize(new { peticion = p, servicio = cuerpo }, Json),
                IdempotencyKey = clave
            };

            // 052: DE QUÉ SUCURSAL SALE LA MERCADERÍA (y a cuál llega, entre locales).
            g.InvOrigenId = g.SucursalId;
            if (tipo == "09" && p.Motivo == "04")
            {
                var codigos = new[] { p.Partida!.CodigoEstablecimiento?.Trim(), p.Llegada!.CodigoEstablecimiento?.Trim() };
                var locs = await db.Sucursales.AsNoTracking()
                    .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null && codigos.Contains(s.Codigo))
                    .Select(s => new { s.Id, s.Codigo }).ToListAsync(ct);
                g.InvOrigenId = locs.FirstOrDefault(s => s.Codigo == codigos[0])?.Id ?? g.SucursalId;
                g.InvDestinoId = locs.FirstOrDefault(s => s.Codigo == codigos[1])?.Id;
            }

            // 052: si la sucursal de salida controla stock y no alcanza, se dice
            // antes de gastar un número. (La que sale de una factura «se entrega
            // después» usa lo reservado: la revisa la base.)
            if (tipo == "09" && origen is null && p.Motivo is not ("02" or "08") && g.InvOrigenId is { } salida)
            {
                var pedido = p.Bienes!.Where(b => b.ProductoId is not null && b.Cantidad > 0)
                    .GroupBy(b => b.ProductoId!.Value).ToDictionary(x => x.Key, x => x.Sum(b => b.Cantidad));
                var (faltan, bloquea) = await Plataforma.Api.Inventario.ReglasInventario.RevisarAsync(db, empresaId, salida, pedido, ct);
                if (bloquea) return await Plataforma.Api.Inventario.ReglasInventario.SinStockAsync(db, salida, faltan, ct);
            }

            var n = 0;
            foreach (var b in p.Bienes!)
            {
                g.Bienes.Add(new GuiaBien
                {
                    EmpresaId = empresaId,
                    Numero = ++n,
                    ProductoId = b.ProductoId,
                    DocumentoLineaId = origen is null ? null : b.DocumentoLineaId,
                    Codigo = Limpio(b.Codigo, 30),
                    Descripcion = b.Descripcion!.Trim(),
                    Unidad = (b.Unidad ?? "NIU").Trim().ToUpperInvariant(),
                    Cantidad = b.Cantidad
                });
            }

            // 052: al guardarla, la base mueve el stock (sale, despacha o queda
            // en tránsito). Si no alcanza, se deshace solo esto y se dice.
            var choque = await Plataforma.Api.Inventario.ReglasInventario.ConPuntoAsync(db, async () =>
            {
                db.Guias.Add(g);
                await db.SaveChangesAsync(ct);
                await Plataforma.Api.Inventario.ReglasInventario.AplicarGuiasAsync(db, ct);
            }, ct);
            if (choque is not null)
            {
                db.Entry(g).State = EntityState.Detached;
                foreach (var b in g.Bienes) db.Entry(b).State = EntityState.Detached;
                return Plataforma.Api.Inventario.ReglasInventario.SinStockDe(choque) is { } falta
                    ? await Plataforma.Api.Inventario.ReglasInventario.SinStockAsync(db, empresaId, falta, ct)
                    : Results.BadRequest(new { error = Plataforma.Api.Inventario.ReglasInventario.Mensaje(choque) });
            }

            // --- Llamar ----------------------------------------------------------
            var r = await facturacion.EnviarAsync<RespuestaGuiaServicio>("/v1/guias", cuerpo, clave, ct);
            AplicarRespuesta(g, r);
            await db.SaveChangesAsync(ct);
            await Plataforma.Api.Inventario.ReglasInventario.AplicarGuiasAsync(db, ct);   // 052: si no llegó, vuelve el stock

            if (!r.Exito || r.Datos is null)
            {
                log.LogWarning("No se pudo emitir la guía {Id}: {Error}", g.Id, r.Error);
                return Results.Json(new
                {
                    g.Id, g.Estado,
                    error = string.IsNullOrWhiteSpace(r.Detalle) ? r.Error : $"{r.Error} {r.Detalle}",
                }, statusCode: r.Estado == 400 ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);
            }

            // EL TRANSPORTE SE GUARDA SOLO.
            await EndpointsTransporte.RecordarAsync(db, empresaId, p, tipo, ct);

            return Results.Ok(new
            {
                g.Id, g.Estado, numero = g.Numero, duplicado = r.Datos.Duplicado,
                mensaje = $"Guía {g.Numero} enviada a SUNAT. El vehículo sale cuando diga «Aceptada» (unos segundos)."
            });
        })
        .RequierePermiso(Permisos.Guias.Emitir)
        .RequiereFicha()
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Emitir una guía de remisión");


        // =====================================================================
        // VER
        // =====================================================================
        grupo.MapGet("/{id:guid}", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var g = await db.Guias.AsNoTracking().Include(x => x.Bienes).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound(new { error = "No se encontró la guía." });

            var doc = g.DocumentoId is { } di
                ? await db.Documentos.AsNoTracking().Where(d => d.Id == di)
                    .Select(d => new { d.Id, d.Tipo, d.Serie, d.Correlativo }).FirstOrDefaultAsync(ct)
                : null;
            var suc = g.SucursalId is { } si
                ? await db.Sucursales.IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == si)
                    .Select(s => new { s.Id, s.Codigo, s.Nombre }).FirstOrDefaultAsync(ct)
                : null;

            JsonElement? peticion = null;
            try
            {
                using var j = JsonDocument.Parse(g.Detalle);
                if (j.RootElement.TryGetProperty("peticion", out var pe)) peticion = pe.Clone();
            }
            catch (JsonException) { }

            return Results.Ok(new
            {
                g.Id, g.Tipo, g.Serie, g.Correlativo, numero = g.Correlativo is null ? null : g.Numero,
                g.Estado, g.Ambiente, g.FechaEmision, g.FechaTraslado,
                g.Motivo, motivoNombre = Motivos.GetValueOrDefault(g.Motivo) ?? g.Motivo, g.MotivoDescripcion, g.Modalidad,
                destinatario = new { tipoDocumento = g.DestinatarioTipo, numeroDocumento = g.DestinatarioDoc, razonSocial = g.DestinatarioNombre },
                partida = new { direccion = g.PartidaDireccion, ubigeo = g.PartidaUbigeo, lugar = await Sucursales.LugarAsync(db, g.PartidaUbigeo, ct) },
                llegada = new { direccion = g.LlegadaDireccion, ubigeo = g.LlegadaUbigeo, lugar = await Sucursales.LugarAsync(db, g.LlegadaUbigeo, ct) },
                g.PesoBruto, g.Bultos,
                placa = g.VehiculoPlaca, conductor = g.ConductorNombre, conductorDoc = g.ConductorDoc,
                transportista = g.TransportistaNombre is null ? null : new { ruc = g.TransportistaRuc, razonSocial = g.TransportistaNombre },
                documento = doc is null ? null : new { doc.Id, doc.Tipo, numero = $"{doc.Serie}-{doc.Correlativo ?? 0:D8}" },
                g.DocumentoRelacionado,
                sucursal = suc is null ? null : $"{suc.Codigo} {suc.Nombre}",
                g.CodigoSunat, g.MensajeSunat, g.AnuladaEn, g.AnuladaMotivo,
                puedeIniciarTraslado = g.Estado is EstadoGuia.Aceptado or EstadoGuia.Observado,
                bienes = g.Bienes.OrderBy(b => b.Numero).Select(b => new { b.Numero, b.Codigo, b.Descripcion, b.Unidad, b.Cantidad, b.ProductoId }),
                // 051: las GRE por evento hechas en SOL y registradas aquí.
                eventos = await db.GuiaEventos.AsNoTracking().Where(x => x.GuiaId == g.Id && x.Activo)
                    .OrderBy(x => x.Fecha).ThenBy(x => x.CreadoEn)
                    .Select(x => new
                    {
                        x.Id, x.Tipo, x.NumeroSol, x.Fecha, x.ReinicioDireccion, x.ReinicioUbigeo,
                        x.LlegadaDireccion, x.LlegadaUbigeo, x.Placa, x.ConductorNombre, x.ConductorDoc, x.Licencia, x.Observacion
                    }).ToListAsync(ct),
                peticion
            });
        })
        .RequierePermiso(Permisos.Guias.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Ver una guía");


        // =====================================================================
        // PREGUNTAR SU ESTADO AL SERVICIO (si el aviso no llegó)
        // =====================================================================
        grupo.MapPost("/{id:guid}/actualizar", async (
            Guid id, ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var g = await db.Guias.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound(new { error = "No se encontró la guía." });
            if (g.GuiaServicioId is not { } sid) return Results.Ok(new { g.Estado, cambio = false });

            var r = await facturacion.ObtenerAsync<EstadoGuiaServicio>($"v1/guias/{sid}", ct);
            if (!r.Exito || r.Datos is null)
                return Results.Json(new { error = r.Error ?? "No se pudo consultar la guía." }, statusCode: StatusCodes.Status502BadGateway);

            var nuevo = Traducir(r.Datos.Estado);
            var cambio = nuevo is not null && nuevo != g.Estado && g.Estado != EstadoGuia.Anulado;
            if (cambio)
            {
                g.Estado = nuevo!;
                g.CodigoSunat = r.Datos.CodigoSunat;
                g.MensajeSunat = Recortar(r.Datos.MensajeSunat, 500);
                if (nuevo == EstadoGuia.Anulado && g.AnuladaEn is null) g.AnuladaEn = HoyEnPeru();
                await db.SaveChangesAsync(ct);
                await Plataforma.Api.Inventario.ReglasInventario.AplicarGuiasAsync(db, ct);   // 052
            }
            return Results.Ok(new { g.Estado, cambio, g.CodigoSunat, g.MensajeSunat });
        })
        .RequierePermiso(Permisos.Guias.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Preguntar al servicio cómo va la guía");


        // =====================================================================
        // REINTENTAR (la que no llegó al servicio): misma clave, mismo cuerpo
        // =====================================================================
        grupo.MapPost("/{id:guid}/reintentar", async (
            Guid id, ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var g = await db.Guias.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound(new { error = "No se encontró la guía." });
            if (g.Estado != EstadoGuia.Error)
                return Results.Conflict(new { error = "Solo se reintenta una guía que no llegó al servicio." });

            JsonElement cuerpo;
            try
            {
                using var j = JsonDocument.Parse(g.Detalle);
                cuerpo = j.RootElement.GetProperty("servicio").Clone();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException)
            {
                return Results.Conflict(new { error = "No se pudo leer lo que se mandó la primera vez. Emítela de nuevo." });
            }

            g.Estado = EstadoGuia.Encolado;
            g.MensajeSunat = null;
            // 052: al volver a «en camino», la mercadería vuelve a salir; si
            // ya no alcanza, se dice y no se manda.
            var choque = await Plataforma.Api.Inventario.ReglasInventario.ConPuntoAsync(db, async () =>
            {
                await db.SaveChangesAsync(ct);
                await Plataforma.Api.Inventario.ReglasInventario.AplicarGuiasAsync(db, ct);
            }, ct);
            if (choque is not null)
            {
                var entrada = db.Entry(g);
                entrada.CurrentValues.SetValues(entrada.OriginalValues);
                entrada.State = EntityState.Unchanged;
                return Plataforma.Api.Inventario.ReglasInventario.SinStockDe(choque) is { } falta
                    ? await Plataforma.Api.Inventario.ReglasInventario.SinStockAsync(db, g.EmpresaId, falta, ct)
                    : Results.BadRequest(new { error = Plataforma.Api.Inventario.ReglasInventario.Mensaje(choque) });
            }

            var r = await facturacion.EnviarAsync<RespuestaGuiaServicio>("/v1/guias", cuerpo, g.IdempotencyKey, ct);
            AplicarRespuesta(g, r);
            await db.SaveChangesAsync(ct);
            await Plataforma.Api.Inventario.ReglasInventario.AplicarGuiasAsync(db, ct);   // 052

            return r.Exito
                ? Results.Ok(new { g.Id, g.Estado, numero = g.Numero, mensaje = $"Guía {g.Numero} enviada." })
                : Results.Json(new { g.Id, g.Estado, error = $"{r.Error} {r.Detalle}".Trim() }, statusCode: StatusCodes.Status502BadGateway);
        })
        .RequierePermiso(Permisos.Guias.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Volver a mandar una guía que no llegó");


        // =====================================================================
        // 051 — REGISTRAR UNA GRE POR EVENTO HECHA EN SOL
        //
        // SUNAT solo la recibe en SOL (no por API). Aquí se deja constancia:
        // qué pasó, dónde se reinició el traslado, la nueva llegada y el
        // vehículo y conductor nuevos (que quedan en el transporte guardado).
        // =====================================================================
        grupo.MapPost("/{id:guid}/evento", async (
            Guid id, PeticionEventoGuia p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var g = await db.Guias.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound(new { error = "No se encontró la guía." });
            if (g.Estado is not (EstadoGuia.Aceptado or EstadoGuia.Observado))
                return Results.Conflict(new { error = "Solo se registra un evento de una guía aceptada (y no anulada)." });

            if (await RevisarEventoAsync(p, g, db, ct) is { } error)
                return Results.BadRequest(new { error });

            var numero = NumeroEvento(p.NumeroSol)!;
            if (await db.GuiaEventos.AnyAsync(x => x.EmpresaId == g.EmpresaId && x.NumeroSol == numero, ct))
                return Results.Conflict(new { error = $"La GRE por evento {numero} ya está registrada." });

            var tipo = p.Tipo!;
            var conVehiculo = tipo is "transbordo" or "ambos";
            var conLlegada = tipo is "arribo" or "ambos";
            var c = conVehiculo ? p.Conductor : null;

            db.GuiaEventos.Add(new GuiaEvento
            {
                EmpresaId = g.EmpresaId, GuiaId = g.Id, Tipo = tipo, NumeroSol = numero,
                Fecha = p.Fecha ?? HoyEnPeru(),
                ReinicioDireccion = p.Reinicio!.Direccion!.Trim(), ReinicioUbigeo = p.Reinicio.Ubigeo!.Trim(),
                LlegadaDireccion = conLlegada ? p.Llegada!.Direccion!.Trim() : null,
                LlegadaUbigeo = conLlegada ? p.Llegada!.Ubigeo!.Trim() : null,
                Placa = conVehiculo ? PlacaLimpia(p.Placa) : null,
                ConductorDoc = c?.NumeroDocumento?.Trim(),
                ConductorNombre = c is null ? null : $"{c.Nombres?.Trim()} {c.Apellidos?.Trim()}".Trim(),
                Licencia = c?.Licencia?.Trim().ToUpperInvariant(),
                Observacion = Limpio(p.Observacion, 250)
            });
            await db.SaveChangesAsync(ct);

            // El vehículo y el conductor nuevos quedan en el transporte guardado.
            if (conVehiculo)
                await EndpointsTransporte.RecordarAsync(db, g.EmpresaId, new PeticionGuiaPlataforma(
                    null, "09", null, null, null, null, null, "02", null, null, null, null, 0, null, null,
                    Vehiculo: new VehiculoGuia(p.Placa), Conductor: c), "09", ct);

            return Results.Ok(new { mensaje = $"Evento {numero} registrado en la guía {g.Numero}." });
        })
        .RequierePermiso(Permisos.Guias.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Registrar una GRE por evento hecha en SOL");


        // =====================================================================
        // REGISTRAR LA ANULACIÓN HECHA EN SOL
        //
        // SUNAT no tiene servicio para dar de baja guías: se anula en SOL. Esto
        // deja constancia para que deje de figurar viva (y deje de contar como
        // despachado de su factura).
        // =====================================================================
        grupo.MapPost("/{id:guid}/anulacion", async (
            Guid id, PeticionAnularGuia p, ContextoPlataforma db, ContextoPeticion ctx,
            ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var g = await db.Guias.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound(new { error = "No se encontró la guía." });
            if (g.Estado == EstadoGuia.Anulado) return Results.Conflict(new { error = "Esta guía ya figura anulada." });
            if (g.Estado is not (EstadoGuia.Aceptado or EstadoGuia.Observado) || g.GuiaServicioId is not { } sid)
                return Results.Conflict(new { error = "Solo se registra la anulación de una guía que SUNAT aceptó." });

            var motivo = (p.Motivo ?? "").Trim();
            if (motivo.Length < 5) return Results.BadRequest(new { error = "Escribe el motivo (el mismo que pusiste en SOL)." });
            if (motivo.Length > 250) motivo = motivo[..250];

            var hoy = HoyEnPeru();
            var fecha = p.Fecha ?? hoy;
            if (fecha > hoy) return Results.BadRequest(new { error = "La fecha de anulación no puede ser futura." });
            if (fecha < g.FechaEmision) return Results.BadRequest(new { error = "La fecha de anulación no puede ser anterior a la emisión." });

            var quien = await db.Usuarios.AsNoTracking().Where(u => u.Id == ctx.AutorId)
                .Select(u => u.Nombre + " <" + u.Correo + ">").FirstOrDefaultAsync(ct) ?? "plataforma";

            var r = await facturacion.EnviarAsync<JsonElement>($"/v1/guias/{sid}/anulacion", new
            {
                motivo, registradaPor = quien, fechaAnulacion = fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            }, null, ct);

            if (!r.Exito)
                return Results.Json(new { error = $"{r.Error} {r.Detalle}".Trim() },
                                    statusCode: r.Estado is 400 or 409 ? StatusCodes.Status409Conflict : StatusCodes.Status502BadGateway);

            g.Estado = EstadoGuia.Anulado;
            g.AnuladaEn = fecha;
            g.AnuladaMotivo = motivo;
            await db.SaveChangesAsync(ct);
            await Plataforma.Api.Inventario.ReglasInventario.AplicarGuiasAsync(db, ct);   // 052: vuelve el stock (si no se recibió)

            return Results.Ok(new { g.Estado, mensaje = $"Listo: {g.Numero} figura anulada desde el {fecha:dd/MM/yyyy}." });
        })
        .RequierePermiso(Permisos.Guias.Anular)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Registrar que una guía se anuló en SOL");


        // =====================================================================
        // ARCHIVOS
        // =====================================================================
        grupo.MapGet("/{id:guid}/pdf", (Guid id, HttpContext http, ContextoPlataforma db, ClienteFacturacion f, CancellationToken ct) =>
            DescargarAsync(id, ArchivoComprobante.GuiaPdf, http, db, f, ct))
            .RequierePermiso(Permisos.Guias.Ver).RequiereModulo(Modulos.Facturacion)
            .WithSummary("El PDF de la guía (la hoja del conductor)");

        grupo.MapGet("/{id:guid}/xml", (Guid id, HttpContext http, ContextoPlataforma db, ClienteFacturacion f, CancellationToken ct) =>
            DescargarAsync(id, ArchivoComprobante.GuiaXml, http, db, f, ct))
            .RequierePermiso(Permisos.Guias.Ver).RequiereModulo(Modulos.Facturacion)
            .WithSummary("El XML firmado de la guía");

        grupo.MapGet("/{id:guid}/cdr", (Guid id, HttpContext http, ContextoPlataforma db, ClienteFacturacion f, CancellationToken ct) =>
            DescargarAsync(id, ArchivoComprobante.GuiaCdr, http, db, f, ct))
            .RequierePermiso(Permisos.Guias.Ver).RequiereModulo(Modulos.Facturacion)
            .WithSummary("La constancia de SUNAT de la guía (ZIP)");
    }


    // =========================================================================
    // APOYO
    // =========================================================================

    private static async Task<IResult> DescargarAsync(
        Guid id, ArchivoComprobante archivo, HttpContext http, ContextoPlataforma db,
        ClienteFacturacion facturacion, CancellationToken ct)
    {
        var g = await db.Guias.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return Results.NotFound(new { error = "No se encontró la guía." });
        if (g.GuiaServicioId is not { } sid)
            return Results.NotFound(new { error = "Archivo no disponible.", detalle = "Esta guía no llegó al servicio de facturación." });

        var r = await facturacion.DescargarComprobanteAsync(g.EmpresaId, sid, archivo, ct);
        if (!r.Exito || r.Contenido is null)
            return Results.Json(new { error = r.Error, detalle = r.Detalle },
                                statusCode: r.Estado == 404 ? StatusCodes.Status404NotFound : StatusCodes.Status502BadGateway);

        http.Response.Headers.CacheControl = "no-store";
        var (ext, mime) = archivo switch
        {
            ArchivoComprobante.GuiaXml => ("xml", "application/xml"),
            ArchivoComprobante.GuiaCdr => ("zip", "application/zip"),
            _ => ("pdf", "application/pdf")
        };
        return Results.File(r.Contenido, r.TipoContenido ?? mime, r.NombreArchivo ?? $"{g.Numero}.{ext}");
    }

    /// <summary>Lo que contestó el servicio, en la guía (sin guardar).</summary>
    private static void AplicarRespuesta(Guia g, ResultadoFacturacion<RespuestaGuiaServicio> r)
    {
        if (!r.Exito || r.Datos is null)
        {
            g.Estado = EstadoGuia.Error;
            g.MensajeSunat = Recortar(string.IsNullOrWhiteSpace(r.Detalle) ? r.Error : $"{r.Error} {r.Detalle}", 500);
            return;
        }

        g.GuiaServicioId = r.Datos.Id;
        g.Estado = Traducir(r.Datos.Estado) ?? EstadoGuia.Encolado;
        var partes = r.Datos.Numero.Split('-', 2);
        if (partes.Length == 2 && int.TryParse(partes[1], out var correlativo))
        {
            g.Serie = partes[0];
            g.Correlativo = correlativo;
        }
    }

    internal static string? Traducir(string? estado) => estado?.ToUpperInvariant() switch
    {
        "BORRADOR" or "ENCOLADO" or "ENVIADO" or "PROCESANDO" => EstadoGuia.Encolado,
        "ACEPTADO"                   => EstadoGuia.Aceptado,
        "ACEPTADO_CON_OBSERVACIONES" => EstadoGuia.Observado,
        "RECHAZADO"                  => EstadoGuia.Rechazado,
        "ANULADO"                    => EstadoGuia.Anulado,
        _                            => null
    };

    /// <summary>Una factura o boleta de la que se puede hacer guía, o por qué no.</summary>
    internal static string? ProblemaOrigen(Documento d)
    {
        if (d.Tipo is not ("01" or "03"))
            return "Solo se hace guía desde una factura o una boleta.";
        // 24 oct: con la baja pedida y sin respuesta de SUNAT, no (como en las notas).
        if (ReglasBaja.Pendiente(d) is { } pedida)
            return ReglasBaja.TextoPendiente(d, pedida, "guía de remisión");
        return d.Estado switch
        {
            EstadoDocumento.Encolado or EstadoDocumento.Aceptado or EstadoDocumento.Observado => null,
            EstadoDocumento.Rechazado => "SUNAT rechazó este comprobante: no sustenta ningún traslado. Corrígelo y emite uno nuevo.",
            EstadoDocumento.Anulado => "Este comprobante está anulado: no sustenta ningún traslado.",
            _ => "Este comprobante todavía no se emitió. Emítelo primero."
        };
    }

    /// <summary>
    /// 24 oct: lo de arriba, y además: una nota de crédito que anula el
    /// comprobante entero (01 anulación, 02 error en el RUC, 06 devolución
    /// total) también lo deja sin traslado que sustentar. Cuenta las notas de
    /// todos (SinAlcance), aceptadas o en camino.
    /// </summary>
    internal static async Task<string?> ProblemaOrigenAsync(ContextoPlataforma db, Documento d, CancellationToken ct)
    {
        if (ProblemaOrigen(d) is { } problema) return problema;

        var nota = await SinAlcance.LeerAsync(db, () => db.Documentos.AsNoTracking()
            .Where(x => x.DocumentoRefId == d.Id && x.Tipo == "07" &&
                        (x.TipoNota == "01" || x.TipoNota == "02" || x.TipoNota == "06") &&
                        (x.Estado == EstadoDocumento.Encolado || x.Estado == EstadoDocumento.Aceptado || x.Estado == EstadoDocumento.Observado))
            .Select(x => new { x.Serie, x.Correlativo })
            .FirstOrDefaultAsync(ct), ct);

        return nota is null ? null
            : $"Este comprobante tiene la nota de crédito {nota.Serie}-{nota.Correlativo ?? 0:D8}, que lo anula o lo devuelve entero: ya no sustenta ningún traslado.";
    }

    /// <summary>
    /// De una factura o boleta: cuánto se despachó de cada línea y qué falta,
    /// y sus guías. Lo despachado cuenta TODAS las guías de la empresa (no solo
    /// las que ve quien pregunta): si no, alguien con alcance «solo lo suyo»
    /// podría despachar dos veces lo mismo.
    /// </summary>
    internal static async Task<object> DespachoAsync(ContextoPlataforma db, Documento d, CancellationToken ct)
    {
        var lineaIds = d.Lineas.Select(l => l.Id).ToList();
        var despachado = await SinAlcance.LeerAsync(db, () =>
            (from b in db.GuiaBienes.AsNoTracking()
             join g in db.Guias.AsNoTracking() on b.GuiaId equals g.Id
             where b.DocumentoLineaId != null && lineaIds.Contains(b.DocumentoLineaId.Value)
                   && EstadoGuia.Cuentan.Contains(g.Estado)
             group b by b.DocumentoLineaId into x
             select new { Linea = x.Key!.Value, Cantidad = x.Sum(y => y.Cantidad) })
            .ToDictionaryAsync(x => x.Linea, x => x.Cantidad, ct), ct);

        var codigos = await CodigosDeProductosAsync(db, d.Lineas.Where(l => l.ProductoId is not null).Select(l => l.ProductoId!.Value), ct);

        var guias = await db.Guias.AsNoTracking()
            .Where(g => g.DocumentoId == d.Id)
            .OrderByDescending(g => g.CreadoEn)   // 24 oct: lo más nuevo arriba
            .Select(g => new
            {
                g.Id, g.Serie, g.Correlativo, g.Estado, g.FechaTraslado, g.VehiculoPlaca,
                bienes = g.Bienes.OrderBy(b => b.Numero).Select(b => new { b.Descripcion, b.Cantidad, b.Unidad })
            })
            .ToListAsync(ct);

        var lineas = d.Lineas.OrderBy(l => l.Numero).Select(l =>
        {
            var hecho = despachado.GetValueOrDefault(l.Id);
            return new
            {
                l.Id, l.Numero, l.ProductoId, l.Codigo, l.Descripcion,
                unidad = l.UnidadMedida, l.Cantidad,
                despachado = hecho,
                pendiente = Math.Max(0, l.Cantidad - hecho),
                esServicio = l.UnidadMedida == "ZZ",
                codigoSunat = l.ProductoId is { } pid ? codigos.GetValueOrDefault(pid) : null
            };
        }).ToList();

        var problema = await ProblemaOrigenAsync(db, d, ct);
        var falta = lineas.Where(l => !l.esServicio).Sum(l => l.pendiente);

        return new
        {
            d.Id, d.Tipo, numero = d.Numero, d.Estado, d.FechaEmision, d.SucursalId, d.Ambiente,
            cliente = d.Cliente is null ? null : new
            {
                d.Cliente.Id, d.Cliente.TipoDocumento, d.Cliente.NumeroDocumento, d.Cliente.RazonSocial,
                d.Cliente.Direccion, d.Cliente.Ubigeo,
                lugar = await Sucursales.LugarAsync(db, d.Cliente.Ubigeo, ct)
            },
            puede = problema is null && falta > 0,
            problema = problema ?? (falta <= 0 && lineas.Any(l => !l.esServicio) ? "Ya se despachó todo lo de este comprobante." : null),
            todoDespachado = falta <= 0,
            lineas,
            guias = guias.Select(g => new
            {
                g.Id, numero = g.Correlativo is { } c ? $"{g.Serie}-{c:D8}" : g.Serie,
                g.Estado, g.FechaTraslado, placa = g.VehiculoPlaca, g.bienes
            })
        };
    }

    private static async Task<Dictionary<Guid, string>> CodigosDeProductosAsync(
        ContextoPlataforma db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var lista = ids.Distinct().ToList();
        if (lista.Count == 0) return [];
        return await db.Productos.IgnoreQueryFilters().AsNoTracking()
            .Where(p => lista.Contains(p.Id) && p.CodigoSunat != null)
            .ToDictionaryAsync(p => p.Id, p => p.CodigoSunat!, ct);
    }

    private static Task<Dictionary<Guid, string>> CodigosSunatAsync(
        ContextoPlataforma db, List<BienGuia> bienes, CancellationToken ct) =>
        CodigosDeProductosAsync(db, bienes.Where(b => b.ProductoId is not null).Select(b => b.ProductoId!.Value), ct);


    // =========================================================================
    // LOCALES: en qué sucursales puede emitir guías quien pregunta, y con qué series
    // =========================================================================

    public sealed record SerieLocal(string Serie, int Ultimo, string Siguiente);

    public sealed record Local(
        Guid Id, string Codigo, string Nombre, string? Direccion, string? Ubigeo, string? Lugar, bool EsPrincipal,
        List<SerieLocal> Series09, List<SerieLocal> Series31, string? Nueva09, string? Nueva31);

    public sealed record Locales(List<Local> Sucursales, bool SeriesLeidas);

    internal static async Task<Locales> LocalesAsync(
        ContextoPlataforma db, ContextoPeticion ctx, ClienteFacturacion facturacion, Guid empresaId, CancellationToken ct)
    {
        var principal = await Sucursales.PrincipalAsync(db, empresaId, ct);
        var mapa = await Sucursales.MapaAsync(db, empresaId, ct);
        var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);

        var activas = await db.Sucursales.AsNoTracking()
            .Where(s => s.EmpresaId == empresaId && s.EliminadaEn == null)
            .OrderBy(s => s.Codigo)
            .ToListAsync(ct);

        var r = await facturacion.ConfigurarAsync<List<SerieDelServicio>>(HttpMethod.Get, "v1/configuracion/series", ct: ct);
        var series = r.Exito && r.Datos is not null ? r.Datos : [];

        Guid DeQuien(string tipo, string serie) =>
            mapa.TryGetValue(Sucursales.Clave(tipo, serie), out var m) && activas.Any(a => a.Id == m) ? m : principal.Id;

        // Todas las T y V que existen (activas o no): una nueva no puede repetirlas.
        var usadas = series.Select(s => s.Serie.ToUpperInvariant()).ToHashSet();
        string Siguiente(string letra, int desde)
        {
            for (var i = desde; i < 1000; i++)
            {
                var s = $"{letra}{i:D3}";
                if (!usadas.Contains(s)) { usadas.Add(s); return s; }
            }
            return $"{letra}999";
        }

        var lista = new List<Local>();
        foreach (var s in activas.OrderBy(x => x.Codigo))
        {
            if (ctx.SucursalVista is { } vista && vista != s.Id) continue;
            if (permitidas is not null && !permitidas.Contains(s.Id)) continue;

            List<SerieLocal> De(string tipo) => series
                .Where(x => x.Activo && x.TipoComprobante == tipo && DeQuien(tipo, x.Serie) == s.Id)
                .OrderBy(x => x.Serie)
                .Select(x => new SerieLocal(x.Serie, x.UltimoCorrelativo, $"{x.Serie}-{x.UltimoCorrelativo + 1:D8}"))
                .ToList();

            var s09 = De("09");
            var s31 = De("31");
            lista.Add(new Local(
                s.Id, s.Codigo, s.Nombre, s.Direccion, s.Ubigeo, await Sucursales.LugarAsync(db, s.Ubigeo, ct),
                s.Codigo == Sucursales.Principal,
                s09, s31,
                r.Exito && s09.Count == 0 ? Siguiente("T", s.Codigo == Sucursales.Principal ? 1 : 2) : null,
                r.Exito && s31.Count == 0 ? Siguiente("V", s.Codigo == Sucursales.Principal ? 1 : 2) : null));
        }

        // La 0000 sin dirección propia: la de la empresa.
        if (lista.FirstOrDefault(x => x.EsPrincipal && string.IsNullOrWhiteSpace(x.Direccion)) is { } p0)
        {
            var dir = await db.Empresas.AsNoTracking().Where(e => e.Id == empresaId).Select(e => e.Direccion).FirstOrDefaultAsync(ct);
            lista[lista.IndexOf(p0)] = p0 with { Direccion = dir };
        }

        return new Locales(lista, r.Exito);
    }


    // =========================================================================
    // REVISAR LO QUE MANDA LA PANTALLA (antes de gastar un número)
    // =========================================================================

    private static async Task<string?> RevisarAsync(
        PeticionGuiaPlataforma p, string tipo, Empresa empresa, Documento? origen,
        ContextoPlataforma db, CancellationToken ct)
    {
        var hoy = HoyEnPeru();
        var emision = p.FechaEmision ?? hoy;
        if (emision != hoy)
            return "La guía se emite con la fecha de hoy.";
        if (p.FechaTraslado is not { } traslado)
            return "Falta la fecha en que empieza el traslado.";
        if (traslado < emision)
            return "El traslado no puede empezar antes de la emisión de la guía.";
        if (traslado > emision.AddDays(30))
            return "La fecha de traslado está muy lejos (más de 30 días). Emite la guía cuando se acerque.";

        if (tipo == "09")
        {
            if (p.Motivo is null || !Motivos.ContainsKey(p.Motivo))
                return "Elige el motivo del traslado.";
            if (p.Motivo == "13" && Limpio(p.MotivoDescripcion, 100) is null)
                return "Con motivo «Otros», explica en una línea cuál es.";
            if (p.Modalidad is not ("01" or "02"))
                return "Elige quién lleva la carga: tu propio vehículo (privado) o una empresa de transporte (público).";
        }

        // --- destinatario ---
        var dest = p.Destinatario;
        if (dest is null || string.IsNullOrWhiteSpace(dest.NumeroDocumento) || string.IsNullOrWhiteSpace(dest.RazonSocial))
            return "Falta el destinatario (quién recibe).";
        if (dest.TipoDocumento is not ("6" or "1" or "4" or "7" or "0"))
            return "El tipo de documento del destinatario no es válido.";
        if (dest.TipoDocumento == "6" && !Ruc.IsMatch(dest.NumeroDocumento.Trim()))
            return "El RUC del destinatario no es válido (11 dígitos).";
        if (dest.TipoDocumento == "1" && !Regex.IsMatch(dest.NumeroDocumento.Trim(), "^[0-9]{8}$"))
            return "El DNI del destinatario son 8 dígitos.";
        if (p.Motivo == "04" && tipo == "09" && dest.NumeroDocumento.Trim() != empresa.Ruc)
            return "En un traslado entre tus locales, el destinatario eres tú mismo (tu RUC).";

        // --- recorrido ---
        foreach (var (punto, nombre) in new[] { (p.Partida, "partida"), (p.Llegada, "llegada") })
        {
            if (punto is null || (punto.Direccion ?? "").Trim().Length < 5)
                return $"Escribe la dirección de {nombre}.";
            if ((punto.Direccion ?? "").Trim().Length > 200)
                return $"La dirección de {nombre} es muy larga (máximo 200 caracteres).";
            if (!Ubigeo.IsMatch((punto.Ubigeo ?? "").Trim()))
                return $"Elige el distrito de {nombre} de la lista.";
            if (await Sucursales.LugarAsync(db, punto.Ubigeo, ct) is null)
                return $"El distrito de {nombre} no existe. Elígelo de la lista.";
        }
        if (p.Motivo == "04" && tipo == "09")
        {
            if (string.IsNullOrWhiteSpace(p.Partida!.CodigoEstablecimiento) || string.IsNullOrWhiteSpace(p.Llegada!.CodigoEstablecimiento))
                return "En un traslado entre tus locales, elige el local de partida y el de llegada.";
            if (p.Partida.CodigoEstablecimiento == p.Llegada.CodigoEstablecimiento)
                return "El local de llegada tiene que ser otro que el de partida.";
        }

        // --- carga ---
        var toneladas = p.UnidadPeso == "TNE";
        if (p.UnidadPeso is not (null or "KGM" or "TNE")) return "La unidad del peso es kilos (KGM) o toneladas (TNE).";
        if (p.PesoBruto <= 0) return $"Escribe el peso bruto total en {(toneladas ? "toneladas" : "kilos")}.";
        if (p.PesoBruto > (toneladas ? 1_000 : 1_000_000))
            return $"El peso bruto parece muy grande. Revísalo (va en {(toneladas ? "toneladas" : "kilos")}).";
        if (p.RetornoVacio && p.RetornoConEnvases)
            return "Elige solo uno: el vehículo vuelve vacío, o vuelve con envases o embalajes vacíos.";

        // --- 24 oct: otros documentos relacionados ---
        var docs = p.Documentos ?? [];
        if (docs.Count > 10) return "Son demasiados documentos relacionados (máximo 10).";
        foreach (var (d, i) in docs.Select((d, i) => (d, i + 1)))
        {
            if (d.Tipo is null || !TiposDocumento.ContainsKey(d.Tipo))
                return $"Elige el tipo del documento relacionado {i}.";
            var num = (d.Numero ?? "").Trim();
            if (num.Length is < 1 or > 30)
                return $"Escribe el número del documento relacionado {i} (hasta 30 caracteres).";
            if (!string.IsNullOrWhiteSpace(d.Ruc) && !Ruc.IsMatch(d.Ruc.Trim()))
                return $"El RUC del documento relacionado {i} no es válido (11 dígitos).";
            if (origen is not null && d.Tipo == origen.Tipo && num.ToUpperInvariant() == origen.Numero)
                return $"{origen.Numero} ya va como documento relacionado: no hace falta agregarlo.";
        }
        if (p.Bultos is < 0 or > 1_000_000) return "El número de bultos no es válido.";
        if (p.Motivo == "08" && p.Bultos is null) return "En una importación hay que declarar el número de bultos.";
        if (p.Bienes is not { Count: > 0 }) return "Agrega al menos un bien.";
        if (p.Bienes.Count > 500) return "Son demasiados bienes para una guía.";
        foreach (var (b, i) in p.Bienes.Select((b, i) => (b, i + 1)))
        {
            if (string.IsNullOrWhiteSpace(b.Descripcion)) return $"Al bien {i} le falta la descripción.";
            if (b.Descripcion.Trim().Length > 500) return $"La descripción del bien {i} es muy larga.";
            if (b.Cantidad <= 0) return $"La cantidad del bien {i} tiene que ser mayor que cero.";
            if (string.IsNullOrWhiteSpace(b.Unidad)) return $"Al bien {i} le falta la unidad.";
        }

        // --- despacho en partes: no más de lo que falta ---
        if (origen is not null)
        {
            var lineas = origen.Lineas.ToDictionary(l => l.Id);
            var despachado = await SinAlcance.LeerAsync(db, () =>
                (from b in db.GuiaBienes.AsNoTracking()
                 join g in db.Guias.AsNoTracking() on b.GuiaId equals g.Id
                 where g.DocumentoId == origen.Id && b.DocumentoLineaId != null && EstadoGuia.Cuentan.Contains(g.Estado)
                 group b by b.DocumentoLineaId into x
                 select new { Linea = x.Key!.Value, Cantidad = x.Sum(y => y.Cantidad) })
                .ToDictionaryAsync(x => x.Linea, x => x.Cantidad, ct), ct);

            foreach (var grupo in p.Bienes.Where(b => b.DocumentoLineaId is not null).GroupBy(b => b.DocumentoLineaId!.Value))
            {
                if (!lineas.TryGetValue(grupo.Key, out var l))
                    return "Un bien no es de este comprobante.";
                var pendiente = l.Cantidad - despachado.GetValueOrDefault(l.Id);
                if (grupo.Sum(b => b.Cantidad) > pendiente)
                    return $"De «{l.Descripcion}» quedan {pendiente:0.###} por despachar: no puedes mandar más.";
            }
        }

        // --- transporte ---
        if (tipo == "09" && p.Modalidad == "01")
        {
            var t = p.Transportista;
            if (t is null || !Ruc.IsMatch((t.Ruc ?? "").Trim()) || string.IsNullOrWhiteSpace(t.RazonSocial))
                return "Falta la empresa de transporte (su RUC y razón social).";
            if (t.Ruc!.Trim() == empresa.Ruc)
                return "En transporte público, quien lleva la carga es otra empresa. Si la llevas tú, elige «Mi vehículo» (privado).";
            if (p.EntregaAlTransportista is { } e && e > traslado)
                return "La entrega al transportista no puede ser después del inicio del traslado.";
        }
        else if (!(tipo == "09" && p.VehiculoMenor))
        {
            if (!Placa.IsMatch(PlacaLimpia(p.Vehiculo?.Placa) ?? ""))
                return "La placa son 6 a 8 letras y números, sin guion ni espacios, como en la tarjeta de propiedad. Ej.: ABC123 o A1B234.";
            if (ProblemaConductor(p.Conductor, "conductor") is { } mal) return mal;
            foreach (var (c, i) in (p.Relevos ?? []).Select((c, i) => (c, i + 1)))
                if (ProblemaConductor(c, $"conductor de relevo {i}") is { } malR) return malR;
            // 24 oct: el TUC con la forma que acepta SUNAT (si no, error 3355).
            if (ProblemaTuc(p.Vehiculo?.Tuc) is { } malTuc) return malTuc;
            if (!string.IsNullOrWhiteSpace(p.Vehiculo?.Autorizacion) && string.IsNullOrWhiteSpace(p.Vehiculo?.Entidad))
                return "Con autorización especial, elige qué entidad la dio (MTC, Digesa, Senasa…).";
        }

        // --- lo propio de la 31 ---
        if (tipo == "31")
        {
            if (string.IsNullOrWhiteSpace(empresa.RegistroMtc))
                return "Falta el registro MTC de tu empresa (Configuración › Guías de remisión).";
            var rem = p.Remitente;
            if (rem is null || rem.TipoDocumento != "6" || !Ruc.IsMatch((rem.NumeroDocumento ?? "").Trim()) || string.IsNullOrWhiteSpace(rem.RazonSocial))
                return "Falta quién te contrató: el RUC y la razón social del remitente.";
            // 24 oct: en un transbordo programado la guía del remitente puede no existir todavía.
            var sinGuia = string.IsNullOrWhiteSpace(p.GuiaRemitente);
            if (!(sinGuia && p.TransbordoProgramado) && !NumeroGuia.IsMatch((p.GuiaRemitente ?? "").Trim().ToUpperInvariant()))
                return "Escribe el número de la guía del remitente que amparas. Ej.: T001-00000453." +
                       " (Solo se omite en un transbordo programado.)";
            if (string.IsNullOrWhiteSpace(p.Vehiculo?.Tuc))
                return "Falta el certificado de habilitación vehicular (TUC) del vehículo.";
            if (p.PagadorFlete is { } pf && (!Ruc.IsMatch((pf.NumeroDocumento ?? "").Trim()) || string.IsNullOrWhiteSpace(pf.RazonSocial)))
                return "Falta el RUC y la razón social de quien paga el flete.";
            if (p.Subcontratista is { } sc && (!Ruc.IsMatch((sc.Ruc ?? "").Trim()) || string.IsNullOrWhiteSpace(sc.RazonSocial)))
                return "Falta el RUC y la razón social del transportista subcontratado.";
        }

        if (p.Observaciones is { Length: > 250 }) return "Las observaciones son muy largas (máximo 250 caracteres).";
        return null;
    }

    /// <summary>051: «eg01-12» → «EG01-12»; null si no tiene forma de número de SOL.</summary>
    internal static string? NumeroEvento(string? n)
    {
        var t = (n ?? "").Trim().ToUpperInvariant().Replace(" ", "");
        var m = Regex.Match(t, "^([A-Z0-9]{1,4})-0*([0-9]{1,8})$");
        return m.Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value}" : null;
    }

    private static async Task<string?> RevisarEventoAsync(PeticionEventoGuia p, Guia g, ContextoPlataforma db, CancellationToken ct)
    {
        if (p.Tipo is not ("arribo" or "transbordo" or "ambos"))
            return "Elige qué pasó: no se pudo llegar, transbordo, o las dos cosas.";
        if (NumeroEvento(p.NumeroSol) is null)
            return "Escribe el número de la GRE por evento que te dio SOL (serie y número, por ejemplo EG01-12).";
        var hoy = HoyEnPeru();
        var fecha = p.Fecha ?? hoy;
        if (fecha > hoy) return "La fecha del evento no puede ser futura.";
        if (fecha < g.FechaEmision) return "La fecha del evento no puede ser anterior a la guía.";

        var puntos = new List<(PuntoGuia? Punto, string Nombre)> { (p.Reinicio, "donde se reinicia el traslado") };
        if (p.Tipo is "arribo" or "ambos") puntos.Add((p.Llegada, "la nueva llegada"));
        foreach (var (punto, nombre) in puntos)
        {
            if (punto is null || (punto.Direccion ?? "").Trim().Length < 5)
                return $"Escribe la dirección de {nombre}.";
            if ((punto.Direccion ?? "").Trim().Length > 200)
                return $"La dirección de {nombre} es muy larga (máximo 200 caracteres).";
            if (!Ubigeo.IsMatch((punto.Ubigeo ?? "").Trim()) || await Sucursales.LugarAsync(db, punto.Ubigeo, ct) is null)
                return $"Elige el distrito de {nombre} de la lista.";
        }

        if (p.Tipo is "transbordo" or "ambos")
        {
            if (!Placa.IsMatch(PlacaLimpia(p.Placa) ?? ""))
                return "La placa del vehículo nuevo son 6 a 8 letras y números, sin guion. Ej.: ABC123.";
            if (ProblemaConductor(p.Conductor, "conductor") is { } mal) return mal;
        }

        if (p.Observacion is { Length: > 250 }) return "La observación es muy larga (máximo 250 caracteres).";
        return null;
    }

    private static string? ProblemaConductor(ConductorGuia? c, string quien)
    {
        if (c is null || string.IsNullOrWhiteSpace(c.NumeroDocumento))
            return $"Falta el documento del {quien}.";
        if (c.TipoDocumento is not ("1" or "4" or "7"))
            return $"El tipo de documento del {quien} no es válido.";
        if (c.TipoDocumento == "1" && !Regex.IsMatch(c.NumeroDocumento.Trim(), "^[0-9]{8}$"))
            return $"El DNI del {quien} son 8 dígitos.";
        if (string.IsNullOrWhiteSpace(c.Nombres) || string.IsNullOrWhiteSpace(c.Apellidos))
            return $"Faltan los nombres y apellidos del {quien}.";
        if (!Licencia.IsMatch((c.Licencia ?? "").Trim().ToUpperInvariant()))
            return $"La licencia del {quien} no es válida (9 o 10 caracteres, como figura en el brevete). Ej.: Q41025874.";
        return null;
    }


    // =========================================================================
    // EL CUERPO QUE ESPERA EL SERVICIO (POST /v1/guias)
    //
    // El remitente NO va: lo pone el servicio con la clave de la empresa.
    // =========================================================================

    private static object ArmarServicio(
        PeticionGuiaPlataforma p, string tipo, string serie, Empresa empresa, Documento? origen,
        Local local, Dictionary<Guid, string> codigosSunat)
    {
        static object ConductorServicio(ConductorGuia c) => new
        {
            tipoDocumento = c.TipoDocumento,
            numeroDocumento = c.NumeroDocumento!.Trim(),
            nombres = c.Nombres!.Trim(),
            apellidos = c.Apellidos!.Trim(),
            licencia = c.Licencia!.Trim().ToUpperInvariant()
        };

        var publico = tipo == "09" && p.Modalidad == "01";
        var conVehiculo = tipo == "31" || (!publico && !p.VehiculoMenor);
        var entreLocales = tipo == "09" && p.Motivo == "04";

        var documentos = new List<object>();
        if (origen is not null)
            documentos.Add(new { tipoDocumento = origen.Tipo, numeroDocumento = origen.Numero });
        if (tipo == "31" && !string.IsNullOrWhiteSpace(p.GuiaRemitente))
            documentos.Add(new
            {
                tipoDocumento = "09",
                numeroDocumento = p.GuiaRemitente.Trim().ToUpperInvariant(),
                rucEmisor = p.Remitente!.NumeroDocumento!.Trim()
            });
        // 24 oct: los que se agregaron a mano (constancia de detracción, DAM…).
        foreach (var d in p.Documentos ?? [])
            documentos.Add(new
            {
                tipoDocumento = d.Tipo,
                numeroDocumento = d.Numero!.Trim().ToUpperInvariant(),
                rucEmisor = DocumentosConRuc.Contains(d.Tipo) ? (string.IsNullOrWhiteSpace(d.Ruc) ? empresa.Ruc : d.Ruc.Trim()) : null
            });

        var indicadores = new List<string>();
        if (tipo == "31")
        {
            indicadores.Add(p.PagadorFlete is null
                ? "SUNAT_Envio_IndicadorPagadorFlete_Remitente"
                : "SUNAT_Envio_IndicadorPagadorFlete_Tercero");
            if (p.Subcontratista is not null) indicadores.Add("SUNAT_Envio_IndicadorTrasporteSubcontratado");
        }
        // 24 oct: las casillas de SOL.
        if (p.TransbordoProgramado) indicadores.Add("SUNAT_Envio_IndicadorTransbordoProgramado");
        if (p.RetornoVacio) indicadores.Add("SUNAT_Envio_IndicadorRetornoVehiculoVacio");
        if (p.RetornoConEnvases) indicadores.Add("SUNAT_Envio_IndicadorRetornoVehiculoEnvaseVacio");

        return new
        {
            tipoGuia = tipo,
            serie,
            fechaEmision = (p.FechaEmision ?? HoyEnPeru()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            fechaTraslado = p.FechaTraslado!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            destinatario = new
            {
                tipoDocumento = p.Destinatario!.TipoDocumento,
                numeroDocumento = p.Destinatario.NumeroDocumento!.Trim(),
                razonSocial = p.Destinatario.RazonSocial!.Trim()
            },
            motivoTraslado = tipo == "31" ? "01" : p.Motivo,
            descripcionMotivo = tipo == "09" ? Limpio(p.MotivoDescripcion, 100) ?? Motivos.GetValueOrDefault(p.Motivo ?? "") : null,
            modalidadTraslado = tipo == "31" ? "02" : p.Modalidad,
            pesoBruto = p.PesoBruto,
            unidadPeso = p.UnidadPeso == "TNE" ? "TNE" : "KGM",   // 24 oct
            numeroBultos = p.Bultos,
            puntoPartida = new
            {
                ubigeo = p.Partida!.Ubigeo!.Trim(),
                direccion = p.Partida.Direccion!.Trim(),
                codigoEstablecimiento = entreLocales ? (p.Partida.CodigoEstablecimiento ?? local.Codigo) : null
            },
            puntoLlegada = new
            {
                ubigeo = p.Llegada!.Ubigeo!.Trim(),
                direccion = p.Llegada.Direccion!.Trim(),
                codigoEstablecimiento = entreLocales ? p.Llegada.CodigoEstablecimiento : null
            },
            bienes = p.Bienes!.Select(b => new
            {
                descripcion = b.Descripcion!.Trim(),
                cantidad = b.Cantidad,
                unidadMedida = (b.Unidad ?? "NIU").Trim().ToUpperInvariant(),
                codigoProducto = Limpio(b.Codigo, 30),
                codigoSunat = b.ProductoId is { } pid ? codigosSunat.GetValueOrDefault(pid) : null
            }),
            transportista = publico ? new
            {
                tipoDocumento = "6",
                numeroDocumento = p.Transportista!.Ruc!.Trim(),
                razonSocial = p.Transportista.RazonSocial!.Trim(),
                numeroMtc = Limpio(p.Transportista.Mtc, 20)
            } : null,
            fechaEntregaAlTransportista = publico
                ? (p.EntregaAlTransportista ?? p.FechaTraslado!.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null,
            vehiculo = conVehiculo ? new
            {
                placa = PlacaLimpia(p.Vehiculo!.Placa),
                tuc = TucLimpio(p.Vehiculo.Tuc),
                numeroAutorizacion = Limpio(p.Vehiculo.Autorizacion, 50),
                entidadAutorizadora = string.IsNullOrWhiteSpace(p.Vehiculo.Autorizacion) ? null : Limpio(p.Vehiculo.Entidad, 2)
            } : null,
            conductor = conVehiculo ? ConductorServicio(p.Conductor!) : null,
            conductoresSecundarios = conVehiculo ? (p.Relevos ?? []).Select(ConductorServicio).ToList() : null,
            documentosRelacionados = documentos,
            observaciones = Limpio(p.Observaciones, 250),
            vehiculoMenor = tipo == "09" && !publico && p.VehiculoMenor,

            // --- 31 ---
            contratante = tipo == "31" ? new
            {
                tipoDocumento = "6",
                numeroDocumento = p.Remitente!.NumeroDocumento!.Trim(),
                razonSocial = p.Remitente.RazonSocial!.Trim()
            } : null,
            pagadorFlete = tipo == "31" && p.PagadorFlete is { } pf ? new
            {
                tipoDocumento = "6",
                numeroDocumento = pf.NumeroDocumento!.Trim(),
                razonSocial = pf.RazonSocial!.Trim()
            } : null,
            numeroMtc = tipo == "31" ? empresa.RegistroMtc : null,
            indicadores = indicadores.Count == 0 ? null : indicadores,
            operadorSubcontratado = tipo == "31" && p.Subcontratista is { } sc ? new
            {
                tipoDocumento = "6",
                numeroDocumento = sc.Ruc!.Trim(),
                razonSocial = sc.RazonSocial!.Trim(),
                numeroMtc = Limpio(sc.Mtc, 20)
            } : null
        };
    }

    /// <summary>24 oct: el certificado de habilitación (TUC/CHV) solo con letras y números.</summary>
    internal static string? TucLimpio(string? tuc)
    {
        if (string.IsNullOrWhiteSpace(tuc)) return null;
        var s = new string(tuc.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// 24 oct: SUNAT rechaza con 3355 un TUC que no tiene su forma (pasó con
    /// «123456»): de 10 a 15 letras y números. Vacío vale (en la 09 es opcional).
    /// </summary>
    internal static string? ProblemaTuc(string? tuc) =>
        TucLimpio(tuc) is { } t && t.Length is < 10 or > 15
            ? $"El certificado de habilitación (TUC) «{t}» no tiene la forma que pide SUNAT: de 10 a 15 letras y números, " +
              "como figura en el certificado (no es la tarjeta de propiedad). Ej.: 15M21028441E. Si es tu propio vehículo y no lo tienes, déjalo vacío."
            : null;

    internal static string? PlacaLimpia(string? placa)
    {
        if (string.IsNullOrWhiteSpace(placa)) return null;
        var s = new string(placa.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return s.Length == 0 ? null : s;
    }

    internal static string? Limpio(string? texto, int largo)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var t = texto.Trim();
        return t.Length <= largo ? t : t[..largo];
    }

    private static string? Recortar(string? texto, int largo) =>
        texto is null ? null : texto.Length <= largo ? texto : texto[..largo];

    internal static DateOnly HoyEnPeru()
    {
        try
        {
            var lima = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, lima));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        }
    }
}