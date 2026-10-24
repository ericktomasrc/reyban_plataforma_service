using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>Una línea a devolver: el número de línea del original y cuántos.</summary>
public sealed record LineaNotaCredito(int Numero, decimal Cantidad);

/// <summary>
/// Cuerpo de POST .../{id}/nota-credito.
///
/// `Clave` la genera la pantalla al abrirse: un doble clic manda la misma y
/// no hace dos notas. `Simular` = solo calcular (la pantalla enseña el total
/// que de verdad saldrá, calculado aquí y no en el navegador).
/// </summary>
public sealed record PeticionNotaCredito(
    string? Clave,
    string? Motivo,
    string? Descripcion,
    List<LineaNotaCredito>? Lineas,
    decimal? Monto,
    int? Linea,
    bool Simular = false);


/// <summary>
/// NOTAS DE CRÉDITO (18 oct 2026, maqueta aprobada).
///
/// Una nota de crédito corrige o anula una factura o boleta YA ACEPTADA por
/// SUNAT, sin plazo. Nace siempre del comprobante original: el cliente, la
/// moneda, el tipo de cambio y los productos salen de él y no se pueden
/// cambiar. El precio tampoco: aquí solo se elige QUÉ pasó y CUÁNTO.
///
/// LO QUE SE PERMITE (catálogo 09 de SUNAT):
///   01 Anular toda la venta · 02 Error en el RUC (solo facturas) ·
///   06 Devolución total     → copian el comprobante entero.
///   07 Devolución por ítem  → qué líneas y cuántas unidades.
///   04 Descuento global · 05 Descuento por ítem · 09 Disminución en el valor
///                           → un monto con IGV.
/// Los demás (03 corrección de descripción, 08 bonificación, 10 otros) quedan
/// para después: tienen reglas de SUNAT propias que todavía no probamos.
///
/// EL SALDO: lo que queda del original = su total − las notas que valen (en
/// camino, aceptadas, observadas o sin enviar). Una nota nunca puede pasar
/// del saldo: SUNAT rechaza la que supera al comprobante (3286), y aquí se
/// cuenta además lo que ya devolvieron las notas anteriores.
///
/// La serie de las notas: FC01 para facturas, BC01 para boletas (la letra la
/// exige SUNAT). Se crea sola, en 0, la primera vez.
/// </summary>
public static class EndpointsNotasCredito
{
    /// <summary>Clave interna de datos_extra: qué cantidades devolvió esta nota («1=2;3=1»). NO viaja al servicio.</summary>
    public const string ClaveDevuelto = "_devuelto";

    private static readonly Dictionary<string, string> Motivos = new()
    {
        ["01"] = "Anulación de la operación",
        ["02"] = "Anulación por error en el RUC",
        ["04"] = "Descuento global",
        ["05"] = "Descuento por ítem",
        ["06"] = "Devolución total",
        ["07"] = "Devolución por ítem",
        ["09"] = "Disminución en el valor",
    };

    private static readonly string[] EstadosQueCuentan =
    [
        EstadoDocumento.Borrador, EstadoDocumento.Encolado, EstadoDocumento.Aceptado,
        EstadoDocumento.Observado, EstadoDocumento.Error
    ];

    public static string SerieDeNota(string tipoOriginal) => tipoOriginal == "03" ? "BC01" : "FC01";

    public sealed record NotaResumen(
        Guid Id, string Numero, string? TipoNota, string? MotivoNota,
        decimal ImporteTotal, string Estado, DateOnly FechaEmision, bool Cuenta);

    /// <summary>Las notas de crédito de un comprobante, de la más vieja a la más nueva.</summary>
    public static async Task<List<NotaResumen>> NotasDeAsync(ContextoPlataforma db, Guid id, CancellationToken ct)
    {
        // 046: TODAS, las haga quien las haga: de aquí sale el saldo.
        var filas = await SinAlcance.LeerAsync(db, () => db.Documentos.AsNoTracking()
            .Where(x => x.DocumentoRefId == id && x.Tipo == "07")
            .OrderBy(x => x.CreadoEn)
            .Select(x => new { x.Id, x.Serie, x.Correlativo, x.TipoNota, x.MotivoNota, x.ImporteTotal, x.Estado, x.FechaEmision })
            .ToListAsync(ct), ct);

        return filas.Select(x => new NotaResumen(
            x.Id,
            x.Serie is { Length: > 0 } s && x.Correlativo is { } n ? $"{s}-{n:D8}" : "(sin número)",
            x.TipoNota, x.MotivoNota, x.ImporteTotal, x.Estado, x.FechaEmision,
            EstadosQueCuentan.Contains(x.Estado))).ToList();
    }

    /// <summary>Lo que queda del comprobante después de sus notas.</summary>
    public static decimal Saldo(decimal total, IEnumerable<NotaResumen> notas) =>
        total - notas.Where(n => n.Cuenta).Sum(n => n.ImporteTotal);


    public static void MapearNotasCredito(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/documentos")
                         .WithTags("Facturación · notas de crédito");

        // =====================================================================
        // LO QUE NECESITA LA PANTALLA: el original, su saldo y sus líneas
        // =====================================================================
        grupo.MapGet("/{id:guid}/nota-credito", async (
            Guid id, ContextoPlataforma db, OpcionesConexionSunat conexionSunat,
            ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var o = await db.Documentos.AsNoTracking()
                .Include(x => x.Lineas).Include(x => x.Cliente)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (o is null) return Results.NotFound();

            var notas = await NotasDeAsync(db, id, ct);
            var devuelto = Devuelto(await ExtrasDeNotasAsync(db, id, ct));
            var problema = await ProblemaAsync(o, db, conexionSunat, ct);
            // 22 oct: la serie configurada para notas; si no hay, la de siempre (se crea sola).
            var elegida = await SeriesDeNotas.ElegirAsync(facturacion, "07", o.Tipo, ct, db, o);
            var saldo = Saldo(o.ImporteTotal, notas);
            if (problema is null && saldo <= 0)
                problema = "Este comprobante ya está anulado del todo con notas de crédito: no queda nada por devolver.";

            var dias = HoyEnPeru().DayNumber - o.FechaEmision.DayNumber;

            var lineas = o.Lineas.OrderBy(l => l.Numero).Select(l => new
            {
                l.Numero, l.Descripcion, l.UnidadMedida, l.Cantidad, l.PrecioUnitario,
                l.ImporteTotal, l.TipoAfectacion,
                devuelta = devuelto.GetValueOrDefault(l.Numero),
                disponible = Math.Max(0, l.Cantidad - devuelto.GetValueOrDefault(l.Numero))
            }).ToList();

            return Results.Ok(new
            {
                o.Id, o.Tipo, numero = o.Numero, o.Moneda, o.FechaEmision, o.ImporteTotal, o.Ambiente,
                cliente = o.Cliente is null ? null : new { o.Cliente.RazonSocial, o.Cliente.NumeroDocumento },
                saldo,
                serieNota = elegida.Serie,
                serieNueva = !elegida.Existe,
                puede = problema is null,
                problema,
                diasDesdeEmision = dias,
                // Dentro de los 7 días, «Comunicar baja» / «Anular» también sirven (y son más simples).
                aTiempoDeBaja = dias <= 7,
                hayNotas = notas.Any(n => n.Cuenta),
                afectacionUnica = AfectacionUnica(o) is not null,
                notas,
                lineas
            });
        })
        .RequierePermiso(Permisos.Facturacion.NotasCredito)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Lo que hace falta para hacer una nota de crédito de este comprobante");


        // =====================================================================
        // CALCULAR (Simular) O EMITIR
        // =====================================================================
        grupo.MapPost("/{id:guid}/nota-credito", async (
            Guid id, PeticionNotaCredito p, ContextoPlataforma db, ContextoPeticion ctx,
            ServicioEmision emision, ClienteFacturacion facturacion,
            OpcionesConexionSunat conexionSunat, ILoggerFactory logs, CancellationToken ct) =>
        {
            var o = await db.Documentos.AsNoTracking()
                .Include(x => x.Lineas)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (o is null) return Results.NotFound();

            // UN DOBLE CLIC: la misma clave → la misma nota, no otra.
            var clave = string.IsNullOrWhiteSpace(p.Clave) ? null : "nc-" + p.Clave.Trim();
            if (clave is { Length: > 80 }) clave = clave[..80];

            if (!p.Simular && clave is not null &&
                await db.Documentos.Include(x => x.Lineas)
                        .FirstOrDefaultAsync(x => x.IdempotencyKey == clave, ct) is { } yaHecha)
                return await EmitirOResponder(yaHecha, emision, ct);

            if (await ProblemaAsync(o, db, conexionSunat, ct) is { } problema)
                return Problema(p.Simular, problema);

            var notas = await NotasDeAsync(db, id, ct);
            var saldo = Saldo(o.ImporteTotal, notas);
            var devuelto = Devuelto(await ExtrasDeNotasAsync(db, id, ct));

            var armado = Armar(o, p, notas.Any(n => n.Cuenta), devuelto);
            if (armado.Problema is not null) return Problema(p.Simular, armado.Problema);

            var nota = new Documento
            {
                EmpresaId      = o.EmpresaId,
                Tipo           = "07",
                Serie          = (await SeriesDeNotas.ElegirAsync(facturacion, "07", o.Tipo, ct, db, o)).Serie,   // 22 oct
                SucursalId     = await Sucursales.DeNotaAsync(db, o, ct),   // 044: la del original
                ClienteId      = o.ClienteId,
                FechaEmision   = HoyEnPeru(),
                Moneda         = o.Moneda,
                TipoCambio     = o.TipoCambio,
                DocumentoRefId = o.Id,
                TipoNota       = p.Motivo,
                MotivoNota     = Descripcion(p),
                Estado         = EstadoDocumento.Borrador,
                IdempotencyKey = clave ?? Guid.NewGuid().ToString("N"),
                DatosExtra     = armado.Devuelto is null
                                     ? new Dictionary<string, string>()
                                     : new Dictionary<string, string> { [ClaveDevuelto] = armado.Devuelto }
            };

            var numero = 1;
            foreach (var l in armado.Lineas!)
            {
                l.EmpresaId = o.EmpresaId;
                l.Numero = numero++;
                CalculoPrevio.Calcular(l);
                nota.Lineas.Add(l);
            }
            CalculoPrevio.Totalizar(nota);

            if (nota.ImporteTotal <= 0)
                return Problema(p.Simular, "La nota tiene que ser por un monto mayor que cero.");

            // Unos céntimos de tolerancia: devolver por partes redondea cada vez.
            if (nota.ImporteTotal > saldo + 0.05m)
                return Problema(p.Simular,
                    $"La nota suma {nota.ImporteTotal:0.00} y de {o.Numero} solo quedan {Math.Max(0, saldo):0.00} " +
                    "(el total menos las notas anteriores). SUNAT la rechazaría.");

            if (p.Simular)
                return Results.Ok(new
                {
                    problema = (string?)null,
                    total = nota.ImporteTotal,
                    gravado = nota.TotalGravado, exonerado = nota.TotalExonerado,
                    inafecto = nota.TotalInafecto, exportacion = nota.TotalExportacion,
                    igv = nota.TotalIgv,
                    saldoDespues = Math.Max(0, saldo - nota.ImporteTotal),
                    serie = nota.Serie,
                    lineas = nota.Lineas.Select(l => new { l.Descripcion, l.Cantidad, l.UnidadMedida, l.ImporteTotal })
                });

            // LA SERIE DE NOTAS: se crea sola la primera vez, en 0. Si ya existe
            // el servicio contesta 409 y no la toca: eso también vale.
            var actor = await db.Usuarios.AsNoTracking()
                .Where(u => u.Id == ctx.UsuarioId).Select(u => u.Correo).FirstOrDefaultAsync(ct);
            var serie = await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Post, "v1/configuracion/series", new
                {
                    tipoComprobante = "07", serie = nota.Serie, ultimoCorrelativo = 0, confirmado = false, actor
                }, ct: ct);

            if (!serie.Exito && serie.Estado != StatusCodes.Status409Conflict)
                return Results.Json(new
                {
                    error = $"No se pudo preparar la serie {nota.Serie} para las notas de crédito.",
                    detalle = serie.Error
                }, statusCode: StatusCodes.Status502BadGateway);

            db.Documentos.Add(nota);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // Dos clics a la vez con la misma clave: la otra ya la guardó.
                logs.CreateLogger("Plataforma.NotasCredito")
                    .LogInformation(ex, "Nota de crédito con la misma clave guardada a la vez.");
                db.ChangeTracker.Clear();
                var otra = await db.Documentos.Include(x => x.Lineas)
                    .FirstOrDefaultAsync(x => x.IdempotencyKey == nota.IdempotencyKey, ct);
                if (otra is null) throw;
                return await EmitirOResponder(otra, emision, ct);
            }

            return await EmitirOResponder(nota, emision, ct);
        })
        .RequierePermiso(Permisos.Facturacion.NotasCredito)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Calcular o emitir una nota de crédito de este comprobante");
    }


    // =========================================================================

    private static IResult Problema(bool simular, string problema) =>
        simular ? Results.Ok(new { problema }) : Results.BadRequest(new { error = problema });

    private static async Task<IResult> EmitirOResponder(Documento nota, ServicioEmision emision, CancellationToken ct)
    {
        if (nota.SePuedeEditar)
        {
            if (nota.FechaEmision < HoyEnPeru()) nota.FechaEmision = HoyEnPeru();

            var r = await emision.EmitirAsync(nota, ct);
            if (!r.Exito)
                return Results.Json(new
                {
                    error = r.Error, detalle = r.Detalle, id = nota.Id, estado = r.Documento.Estado
                }, statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new
        {
            nota.Id,
            numero = nota.Numero,
            nota.Estado,
            nota.ImporteTotal,
            mensaje = nota.Serie?.StartsWith('B') == true
                ? $"Nota de crédito {nota.Numero} emitida. Las notas de boletas viajan a SUNAT en el resumen diario: su estado tardará en cambiar."
                : $"Nota de crédito {nota.Numero} emitida. Está en camino a SUNAT; su estado se actualizará en unos segundos."
        });
    }


    /// <summary>Por qué este comprobante no admite nota de crédito ahora, o null.</summary>
    private static async Task<string?> ProblemaAsync(
        Documento o, ContextoPlataforma db, OpcionesConexionSunat conexionSunat, CancellationToken ct)
    {
        if (o.Tipo is not ("01" or "03"))
            return "Las notas de crédito se hacen sobre facturas y boletas.";

        if (o.Estado is EstadoDocumento.Rechazado or EstadoDocumento.Anulado or EstadoDocumento.Error or EstadoDocumento.Borrador)
            return o.Estado == EstadoDocumento.Anulado
                ? "Este comprobante ya está anulado ante SUNAT: no necesita nota de crédito."
                : "Este comprobante no vale ante SUNAT (no fue aceptado): no necesita nota de crédito.";

        if (o.Estado == EstadoDocumento.Encolado || o.Serie is null || o.Correlativo is null || o.ComprobanteId is null)
            return "Todavía no tiene respuesta de SUNAT. Espera a que SUNAT lo acepte.";

        // 23 oct: con la baja pedida y sin respuesta de SUNAT, no.
        if (ReglasBaja.Pendiente(o) is { } pedida)
            return ReglasBaja.TextoPendiente(o, pedida, "nota de crédito");

        var ambienteEmpresa = await db.Empresas.AsNoTracking()
            .Where(e => e.Id == o.EmpresaId).Select(e => e.Ambiente).FirstOrDefaultAsync(ct);
        var ambiente = ambienteEmpresa == "produccion" ? "produccion" : "beta";

        // LA NOTA VA AL MISMO SUNAT QUE SU ORIGINAL (el servicio solo busca el
        // original en el ambiente actual de la empresa).
        if ((o.Ambiente ?? "beta") != ambiente)
            return o.Ambiente == "produccion"
                ? "Este comprobante es real, pero la empresa está ahora en modo prueba. Haz la nota cuando vuelva a SUNAT real."
                : "Este comprobante es de prueba (SUNAT beta) y la empresa ya está en SUNAT real: no tiene valor y no necesita nota.";

        var conexion = await ConexionSunat.LeerAsync(db, conexionSunat, ct);
        return ConexionSunat.Bloqueo(conexion, ambienteEmpresa);
    }


    private sealed record Armado(List<DocumentoLinea>? Lineas, string? Devuelto, string? Problema);

    private static Armado Mal(string problema) => new(null, null, problema);

    /// <summary>Las líneas de la nota según el motivo. Los precios salen SIEMPRE del original.</summary>
    private static Armado Armar(
        Documento o, PeticionNotaCredito p, bool hayNotas, Dictionary<int, decimal> devuelto)
    {
        var motivo = p.Motivo?.Trim() ?? "";
        if (!Motivos.ContainsKey(motivo))
            return Mal("Elige qué pasó (el motivo de la nota).");

        if (motivo == "02" && o.Tipo != "01")
            return Mal("«Me equivoqué de RUC» es solo para facturas.");

        // Al calcular todavía puede estar vacía; al emitir, no.
        if (!p.Simular && (p.Descripcion?.Trim().Length ?? 0) < 5)
            return Mal("Explica en una línea qué pasó (sale en el PDF y va a SUNAT).");

        var originales = o.Lineas.Where(l => l.Activo).OrderBy(l => l.Numero).ToList();

        switch (motivo)
        {
            // --- TODO EL COMPROBANTE -----------------------------------------
            case "01" or "02" or "06":
                if (hayNotas)
                    return Mal("Este comprobante ya tiene notas de crédito. Para lo que queda, usa " +
                               "«Me devolvieron algunos productos» o un descuento.");

                return new Armado(
                    originales.Select(Copia).ToList(),
                    string.Join(";", originales.Select(l => $"{l.Numero}={Num(l.Cantidad)}")),
                    null);

            // --- ALGUNOS PRODUCTOS -------------------------------------------
            case "07":
            {
                var pedidas = (p.Lineas ?? []).Where(x => x.Cantidad > 0).ToList();
                if (pedidas.Count == 0)
                    return Mal("Escribe cuántas unidades te devolvieron de al menos un producto.");

                var lineas = new List<DocumentoLinea>();
                foreach (var x in pedidas.GroupBy(x => x.Numero).Select(g => new LineaNotaCredito(g.Key, g.Sum(y => y.Cantidad))))
                {
                    var l = originales.FirstOrDefault(y => y.Numero == x.Numero);
                    if (l is null) return Mal($"La línea {x.Numero} no existe en el comprobante.");

                    var disponible = l.Cantidad - devuelto.GetValueOrDefault(l.Numero);
                    if (x.Cantidad > disponible)
                        return Mal(disponible <= 0
                            ? $"«{l.Descripcion}»: ya se devolvió todo lo vendido."
                            : $"«{l.Descripcion}»: solo quedan {Num(disponible)} por devolver.");

                    var c = Copia(l);
                    c.Cantidad = x.Cantidad;
                    c.Descuento = l.Cantidad > 0
                        ? Math.Round(l.Descuento * x.Cantidad / l.Cantidad, 2, MidpointRounding.AwayFromZero)
                        : 0;
                    lineas.Add(c);
                }

                return new Armado(lineas,
                    string.Join(";", pedidas.GroupBy(x => x.Numero).Select(g => $"{g.Key}={Num(g.Sum(y => y.Cantidad))}")),
                    null);
            }

            // --- UN MONTO ----------------------------------------------------
            default:
            {
                if (p.Monto is not { } monto || monto <= 0)
                    return Mal("Escribe el monto, con IGV.");
                monto = Math.Round(monto, 2, MidpointRounding.AwayFromZero);

                string afectacion, descripcion;

                if (motivo == "04")
                {
                    if (AfectacionUnica(o) is not { } unica)
                        return Mal("Este comprobante tiene productos con distinto IGV: haz el descuento por producto.");
                    afectacion = unica;
                    descripcion = "DESCUENTO GLOBAL";
                }
                else
                {
                    var l = originales.FirstOrDefault(y => y.Numero == p.Linea);
                    if (l is null) return Mal("Elige a qué producto se aplica.");
                    if (!CalculoPrevio.SeSabeEstimar(l.TipoAfectacion))
                        return Mal("A ese producto no se le puede hacer un descuento desde aquí.");
                    if (monto > l.ImporteTotal)
                        return Mal($"El monto es mayor que lo que costó «{l.Descripcion}» ({l.ImporteTotal:0.00}).");
                    afectacion = l.TipoAfectacion;
                    descripcion = (motivo == "05" ? "DESCUENTO: " : "DISMINUCIÓN EN EL VALOR: ") + l.Descripcion;
                }

                return new Armado([new DocumentoLinea
                {
                    Descripcion    = descripcion.Length > 250 ? descripcion[..250] : descripcion,
                    UnidadMedida   = "ZZ",
                    Cantidad       = 1,
                    ValorUnitario  = ValorSinIgv(monto, afectacion),
                    TipoAfectacion = afectacion
                }], null, null);
            }
        }
    }

    private static DocumentoLinea Copia(DocumentoLinea l) => new()
    {
        ProductoId     = l.ProductoId,
        Codigo         = l.Codigo,
        Descripcion    = l.Descripcion,
        UnidadMedida   = l.UnidadMedida,
        Cantidad       = l.Cantidad,
        ValorUnitario  = l.ValorUnitario,
        Descuento      = l.Descuento,
        TipoAfectacion = l.TipoAfectacion
    };

    /// <summary>La afectación de IGV si todas las líneas con valor la comparten, o null.</summary>
    private static string? AfectacionUnica(Documento o)
    {
        var distintas = o.Lineas.Where(l => l.Activo && l.ValorVenta > 0)
            .Select(l => l.TipoAfectacion).Distinct().ToList();
        return distintas.Count == 1 && CalculoPrevio.SeSabeEstimar(distintas[0]) ? distintas[0] : null;
    }

    /// <summary>
    /// El valor sin IGV que da el monto con IGV que se escribió, con los mismos
    /// redondeos que CalculoPrevio (y SUNAT). A veces no hay uno exacto: se usa
    /// el que más se acerca sin pasarse, y la pantalla enseña el total real.
    /// </summary>
    internal static decimal ValorSinIgv(decimal monto, string afectacion)
    {
        var tasa = afectacion switch { CalculoPrevio.Gravado => 0.18m, CalculoPrevio.Ivap => 0.04m, _ => 0m };
        var base_ = Math.Round(monto / (1 + tasa), 2, MidpointRounding.AwayFromZero);

        decimal Total(decimal v) =>
            Math.Round(v, 2, MidpointRounding.AwayFromZero) +
            Math.Round(Math.Round(v, 2, MidpointRounding.AwayFromZero) * tasa, 2, MidpointRounding.AwayFromZero);

        var candidatos = Enumerable.Range(-3, 7).Select(k => base_ + k * 0.01m).Where(v => v > 0).ToList();
        return candidatos.FirstOrDefault(v => Total(v) == monto) is var exacto && exacto > 0
            ? exacto
            : candidatos.Where(v => Total(v) <= monto).DefaultIfEmpty(base_).Max();
    }

    private static string Descripcion(PeticionNotaCredito p)
    {
        var d = (p.Descripcion ?? "").Trim();
        return d.Length > 250 ? d[..250] : d;
    }

    private static async Task<List<string>> ExtrasDeNotasAsync(ContextoPlataforma db, Guid id, CancellationToken ct)
    {
        var filas = await SinAlcance.LeerAsync(db, () => db.Documentos.AsNoTracking()
            .Where(x => x.DocumentoRefId == id && x.Tipo == "07" && EstadosQueCuentan.Contains(x.Estado))
            .Select(x => x.DatosExtra)
            .ToListAsync(ct), ct);

        return filas.Select(e => e.GetValueOrDefault(ClaveDevuelto)).OfType<string>().ToList();
    }

    /// <summary>«1=2;3=1» de cada nota → cuánto se devolvió de cada línea.</summary>
    private static Dictionary<int, decimal> Devuelto(IEnumerable<string> textos)
    {
        var suma = new Dictionary<int, decimal>();
        foreach (var texto in textos)
            foreach (var par in texto.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var partes = par.Split('=');
                if (partes.Length == 2 &&
                    int.TryParse(partes[0], out var n) &&
                    decimal.TryParse(partes[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var c))
                    suma[n] = suma.GetValueOrDefault(n) + c;
            }
        return suma;
    }

    private static string Num(decimal d) => d.ToString("0.##########", CultureInfo.InvariantCulture);

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