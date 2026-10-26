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

/// <summary>24 oct (motivo 03): la línea del original y cómo debió decir.</summary>
public sealed record CorreccionNota(int Numero, string? Descripcion);

/// <summary>24 oct (motivo 13): una cuota nueva.</summary>
public sealed record CuotaNota(decimal Monto, DateOnly? Vence);

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
    bool Simular = false,
    List<CorreccionNota>? Correcciones = null,   // 24 oct: motivo 03
    string? Afectacion = null,                   // 24 oct: motivo 10 (10, 20 o 30)
    List<CuotaNota>? Cuotas = null,              // 24 oct: motivo 13
    string? Retorno = null,                      // 052: volvio | danada | no_volvio (01, 02, 06, 07)
    Guid? RetornoSucursalId = null);             // 052: a qué sucursal volvió (null = la del comprobante)


/// <summary>
/// NOTAS DE CRÉDITO (18 oct 2026, maqueta aprobada).
///
/// Una nota de crédito corrige o anula una factura o boleta YA ACEPTADA por
/// SUNAT, sin plazo. Nace siempre del comprobante original: el cliente, la
/// moneda, el tipo de cambio y los productos salen de él y no se pueden
/// cambiar. El precio tampoco: aquí solo se elige QUÉ pasó y CUÁNTO.
///
/// LO QUE SE PERMITE (catálogo 09 de SUNAT, los 13 desde el 24 oct):
///   01 Anular toda la venta · 02 Error en el RUC (solo facturas) ·
///   06 Devolución total     → copian el comprobante entero.
///   07 Devolución por ítem  → qué líneas y cuántas unidades.
///   04 Descuento global · 05 Descuento por ítem · 09 Disminución en el valor ·
///   08 Bonificación         → un monto con IGV (04, 05 y 08 solo facturas).
///   10 Otros conceptos      → un monto y su IGV (gravado, exonerado, inafecto).
///   11 Ajuste de exportación (afectación 40) · 12 Ajuste IVAP (17)
///                           → un monto, si el original tiene esas líneas.
///   03 Corrección de la descripción → las líneas con el texto bien escrito,
///                           en cero (no devuelve nada).
///   13 Ajuste de cuotas     → la nota va en cero y lleva las cuotas nuevas
///                           (solo facturas al crédito).
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
        ["03"] = "Corrección por error en la descripción",
        ["08"] = "Bonificación",
        ["10"] = "Otros conceptos",
        ["11"] = "Ajustes de operaciones de exportación",
        ["12"] = "Ajustes afectos al IVAP",
        ["13"] = "Ajuste de montos o fechas de pago (cuotas)",
    };

    /// <summary>24 oct: los que SUNAT no acepta sobre boletas.</summary>
    private static readonly string[] SoloFacturas = ["02", "04", "05", "08", "13"];

    /// <summary>24 oct: los que van en cero (no devuelven nada).</summary>
    private static readonly string[] EnCero = ["03", "13"];

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
            .OrderByDescending(x => x.CreadoEn)   // 24 oct: lo más nuevo arriba
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
        // 24 oct: «NUEVA NOTA DE CRÉDITO / DÉBITO» desde el menú, como en SOL:
        // se busca la factura o boleta por su número (o por el cliente) y se
        // abre la pantalla de siempre. GET /api/facturacion/documentos/para-nota?q=F001-11
        // =====================================================================
        grupo.MapGet("/para-nota", async (string? q, ContextoPlataforma db, CancellationToken ct) =>
            Results.Ok(await BuscarComprobantesAsync(db, q, ct)))
        .RequierePermiso(Permisos.Facturacion.NotasCredito)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Buscar la factura o boleta de una nota por su número o su cliente");

        // 24 oct: las series de facturas y boletas emitidas, para elegirlas (como en SOL).
        grupo.MapGet("/para-nota/series", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Ok(await SeriesEmitidasAsync(db, ct)))
        .RequierePermiso(Permisos.Facturacion.NotasCredito)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las series de facturas y boletas emitidas");


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
                // 24 oct: el plazo para corregir (aviso, no bloquea).
                plazoCorreccion = PlazoNotas.Limite(o.FechaEmision),
                plazoVencido = HoyEnPeru() > PlazoNotas.Limite(o.FechaEmision),
                hayNotas = notas.Any(n => n.Cuenta && n.ImporteTotal > 0),
                afectacionUnica = AfectacionUnica(o) is not null,
                // 24 oct: para los motivos 11, 12 y 13.
                tieneExportacion = o.Lineas.Any(l => l.Activo && l.TipoAfectacion == CalculoPrevio.Exportacion),
                tieneIvap = o.Lineas.Any(l => l.Activo && l.TipoAfectacion == CalculoPrevio.Ivap),
                alCredito = o.Tipo == "01" && o.FormaPago == FormaDePago.Credito,
                cuotas = o.Cuotas.OrderBy(c => c.Numero).Select(c => new { c.Numero, c.Monto, c.Vence }),
                motivos = Motivos.Where(m => o.Tipo == "01" || !SoloFacturas.Contains(m.Key))
                                 .Select(m => new { codigo = m.Key, nombre = m.Value }),
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

            // 24 oct: las notas en cero (03, 13) no impiden anular el resto.
            var armado = Armar(o, p, notas.Any(n => n.Cuenta && n.ImporteTotal > 0), devuelto);
            if (armado.Problema is not null) return Problema(p.Simular, armado.Problema);

            // 052: ¿la mercadería volvió al almacén? Solo en las que devuelven productos.
            var devuelve = p.Motivo is "01" or "02" or "06" or "07";
            if (devuelve && p.Retorno is not (null or "volvio" or "danada" or "no_volvio"))
                return Problema(p.Simular, "Elige si la mercadería volvió al almacén.");
            if (devuelve && p.RetornoSucursalId is { } sucRet &&
                !await db.Sucursales.AnyAsync(s => s.Id == sucRet && s.EmpresaId == o.EmpresaId && s.EliminadaEn == null, ct))
                return Problema(p.Simular, "La sucursal a la que volvió la mercadería no existe.");

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
                                     : new Dictionary<string, string> { [ClaveDevuelto] = armado.Devuelto },
                Cuotas         = armado.Cuotas ?? [],  // 24 oct: motivo 13
                InvRetorno     = devuelve ? p.Retorno : null,              // 052
                InvSucursalId  = devuelve ? p.RetornoSucursalId : null
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

            var enCero = EnCero.Contains(p.Motivo);
            if (enCero && nota.ImporteTotal != 0)
                return Problema(p.Simular, "Esta nota va en cero: no devuelve nada.");
            if (!enCero && nota.ImporteTotal <= 0)
                return Problema(p.Simular, "La nota tiene que ser por un monto mayor que cero.");

            // 24 oct (13): las cuotas nuevas no pueden pasar de lo que queda.
            if (p.Motivo == "13" && nota.Cuotas.Sum(c => c.Monto) > saldo + 0.05m)
                return Problema(p.Simular,
                    $"Las cuotas suman {nota.Cuotas.Sum(c => c.Monto):0.00} y de {o.Numero} quedan {Math.Max(0, saldo):0.00}. " +
                    "El monto pendiente no puede ser mayor.");

            // Unos céntimos de tolerancia: devolver por partes redondea cada vez.
            // El motivo 10 (otros conceptos) no tiene ese tope en SUNAT.
            if (p.Motivo != "10" && nota.ImporteTotal > saldo + 0.05m)
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
                    cuotas = nota.Cuotas.Sum(c => c.Monto),
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
        .RequiereFicha()
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Calcular o emitir una nota de crédito de este comprobante");
    }


    /// <summary>
    /// 24 oct: una factura o boleta emitida, por su número (F001-11), su
    /// correlativo o su cliente. La usan «Nueva nota» y «Nueva guía». Con el
    /// alcance de quien busca (046).
    /// </summary>
    internal static async Task<List<object>> SeriesEmitidasAsync(ContextoPlataforma db, CancellationToken ct)
    {
        var filas = await db.Documentos.AsNoTracking()
            .Where(d => (d.Tipo == "01" || d.Tipo == "03") && d.Serie != null && d.Correlativo != null &&
                        d.Estado != EstadoDocumento.Borrador)
            .GroupBy(d => new { d.Tipo, d.Serie })
            .Select(g => new { g.Key.Tipo, g.Key.Serie, ultimo = g.Max(x => x.Correlativo) })
            .ToListAsync(ct);
        return filas.OrderBy(x => x.Tipo).ThenBy(x => x.Serie)
            .Select(x => (object)new { x.Tipo, x.Serie, x.ultimo }).ToList();
    }

    internal static async Task<List<object>> BuscarComprobantesAsync(ContextoPlataforma db, string? q, CancellationToken ct)
    {
        var texto = (q ?? "").Trim().ToUpperInvariant().Replace(" ", "");
        if (texto.Length < 1) return [];

        var consulta = db.Documentos.AsNoTracking()
            .Where(d => (d.Tipo == "01" || d.Tipo == "03") && d.Serie != null && d.Correlativo != null &&
                        d.Estado != EstadoDocumento.Borrador);

        var numero = System.Text.RegularExpressions.Regex.Match(texto, "^([FB][A-Z0-9]{3})-?0*([0-9]{1,8})$");
        if (numero.Success)
        {
            var serie = numero.Groups[1].Value;
            var correlativo = int.Parse(numero.Groups[2].Value, CultureInfo.InvariantCulture);
            consulta = consulta.Where(d => d.Serie == serie && d.Correlativo == correlativo);
        }
        else if (System.Text.RegularExpressions.Regex.IsMatch(texto, "^[0-9]{1,7}$"))
        {
            var correlativo = int.Parse(texto, CultureInfo.InvariantCulture);
            consulta = consulta.Where(d => d.Correlativo == correlativo);
        }
        else
        {
            var t = (q ?? "").Trim().ToLower();
            consulta = consulta.Where(d => d.Cliente!.NumeroDocumento.Contains(t) || d.Cliente!.RazonSocial.ToLower().Contains(t));
        }

        var filas = await consulta.OrderByDescending(d => d.FechaEmision).ThenByDescending(d => d.Correlativo)
            .Take(10)
            .Select(d => new
            {
                d.Id, d.Tipo, d.Serie, d.Correlativo, d.Estado, d.FechaEmision, d.Moneda, d.ImporteTotal,
                cliente = d.Cliente!.RazonSocial, documentoCliente = d.Cliente!.NumeroDocumento
            })
            .ToListAsync(ct);

        return filas.Select(d => (object)new
        {
            d.Id, d.Tipo, numero = $"{d.Serie}-{d.Correlativo:D8}", d.Estado, d.FechaEmision, d.Moneda,
            d.ImporteTotal, d.cliente, d.documentoCliente
        }).ToList();
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


    private sealed record Armado(List<DocumentoLinea>? Lineas, string? Devuelto, string? Problema,
                                 List<CuotaDocumento>? Cuotas = null);

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
        if (SoloFacturas.Contains(motivo) && o.Tipo != "01")
            return Mal($"«{Motivos[motivo]}» es solo para facturas: SUNAT no lo acepta en boletas.");

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
                    if (CalculoPrevio.EsGratuita(l.TipoAfectacion) && pedidas.All(y => originales.FirstOrDefault(z => z.Numero == y.Numero) is { } o2 && CalculoPrevio.EsGratuita(o2.TipoAfectacion)))
                        return Mal("Solo elegiste regalos: no hay nada que devolver en dinero (una nota solo con regalos sale en cero y SUNAT la rechaza). " +
                                   "Si el regalo volvió al almacén, regístralo en Inventario.");
                    if (x.Cantidad > disponible)
                        return Mal(disponible <= 0
                            ? $"«{l.Descripcion}»: ya se devolvió todo lo vendido."
                            : $"«{l.Descripcion}»: solo quedan {Num(disponible)} por devolver.");

                    var c = Copia(l);
                    c.Cantidad = x.Cantidad;
                    if (x.Cantidad != l.Cantidad) c.Series = null;   // 065: devolución parcial: no se sabe cuáles
                    c.Descuento = l.Cantidad > 0
                        ? Math.Round(l.Descuento * x.Cantidad / l.Cantidad, 2, MidpointRounding.AwayFromZero)
                        : 0;
                    lineas.Add(c);
                }

                return new Armado(lineas,
                    string.Join(";", pedidas.GroupBy(x => x.Numero).Select(g => $"{g.Key}={Num(g.Sum(y => y.Cantidad))}")),
                    null);
            }

            // --- 03: CORREGIR LA DESCRIPCIÓN (en cero) ------------------------
            case "03":
            {
                var pedidas = (p.Correcciones ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Descripcion)).ToList();
                if (pedidas.Count == 0)
                    return Mal("Escribe cómo debió decir al menos un producto.");

                var lineas = new List<DocumentoLinea>();
                foreach (var x in pedidas.GroupBy(x => x.Numero).Select(g => g.Last()))
                {
                    var l = originales.FirstOrDefault(y => y.Numero == x.Numero);
                    if (l is null) return Mal($"La línea {x.Numero} no existe en el comprobante.");
                    if (CalculoPrevio.EsGratuita(l.TipoAfectacion))
                        return Mal($"«{l.Descripcion}» fue un regalo: su descripción no se corrige con una nota. Corrige las líneas vendidas.");
                    var texto = x.Descripcion!.Trim();
                    if (texto.Length < 3) return Mal($"La descripción corregida de la línea {x.Numero} es muy corta.");
                    if (string.Equals(texto, l.Descripcion.Trim(), StringComparison.OrdinalIgnoreCase))
                        return Mal($"«{l.Descripcion}»: la descripción corregida es igual a la original.");

                    var c = Copia(l);
                    c.Descripcion   = texto.Length > 250 ? texto[..250] : texto;
                    c.ValorUnitario = 0;
                    c.Descuento     = 0;
                    lineas.Add(c);
                }
                return new Armado(lineas, null, null);
            }

            // --- 13: CUOTAS NUEVAS (en cero) ----------------------------------
            case "13":
            {
                if (o.FormaPago != FormaDePago.Credito)
                    return Mal("Este comprobante fue al contado: no tiene cuotas que corregir.");
                var cuotas = (p.Cuotas ?? []).ToList();
                if (cuotas.Count == 0) return Mal("Escribe las cuotas nuevas (monto y fecha de vencimiento).");
                if (cuotas.Count > 60) return Mal("Son demasiadas cuotas.");
                var hoy = HoyEnPeru();
                foreach (var (q, i) in cuotas.Select((q, i) => (q, i + 1)))
                {
                    if (q.Monto <= 0) return Mal($"La cuota {i} tiene que ser por un monto mayor que cero.");
                    if (q.Vence is not { } v) return Mal($"A la cuota {i} le falta la fecha de vencimiento.");
                    if (v <= hoy) return Mal($"La cuota {i} tiene que vencer después de hoy (SUNAT, error 3267).");
                }

                return new Armado([new DocumentoLinea
                {
                    Descripcion    = "AJUSTE DE MONTOS Y/O FECHAS DE PAGO",
                    UnidadMedida   = "ZZ",
                    Cantidad       = 1,
                    ValorUnitario  = 0,
                    TipoAfectacion = AfectacionUnica(o) ?? CalculoPrevio.Gravado
                }], null, null,
                cuotas.OrderBy(q => q.Vence).Select((q, i) =>
                    new CuotaDocumento(i + 1, Math.Round(q.Monto, 2, MidpointRounding.AwayFromZero), q.Vence!.Value)).ToList());
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
                else if (motivo == "10")
                {
                    // 24 oct: otros conceptos, con el IGV que se elija.
                    afectacion = p.Afectacion?.Trim() switch
                    {
                        "20" => CalculoPrevio.Exonerado, "30" => CalculoPrevio.Inafecto, _ => CalculoPrevio.Gravado
                    };
                    var texto = (p.Descripcion ?? "").Trim().ToUpperInvariant();
                    descripcion = texto.Length > 0 ? $"OTROS CONCEPTOS: {texto}" : "OTROS CONCEPTOS";
                }
                else if (motivo is "11" or "12")
                {
                    // 24 oct: ajustes de exportación (40) o del IVAP (17), solo si el
                    // original tiene líneas así.
                    afectacion = motivo == "11" ? CalculoPrevio.Exportacion : CalculoPrevio.Ivap;
                    var base_ = originales.Where(l => l.TipoAfectacion == afectacion).Sum(l => l.ImporteTotal);
                    if (base_ <= 0)
                        return Mal(motivo == "11"
                            ? "Este comprobante no tiene ventas de exportación que ajustar."
                            : "Este comprobante no tiene ventas afectas al IVAP (arroz pilado) que ajustar.");
                    if (monto > base_)
                        return Mal($"El ajuste es mayor que lo vendido con esa afectación ({base_:0.00}).");
                    descripcion = motivo == "11" ? "AJUSTE DE OPERACIONES DE EXPORTACIÓN" : "AJUSTE DE OPERACIONES AFECTAS AL IVAP";
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
                    descripcion = (motivo switch
                    {
                        "05" => "DESCUENTO: ", "08" => "BONIFICACIÓN: ", _ => "DISMINUCIÓN EN EL VALOR: "
                    }) + l.Descripcion;
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
        TipoAfectacion = l.TipoAfectacion,
        PresentacionId = l.PresentacionId,  // 061: la caja devuelta vuelve como 12 unidades
        Series         = l.Series           // 065: lo devuelto (si es todo); en parcial se quitan
    };

    /// <summary>La afectación de IGV si todas las líneas con valor la comparten, o null.</summary>
    private static string? AfectacionUnica(Documento o)
    {
        // e169: los regalos no cuentan: no se cobraron, no hay nada que descontarles.
        var distintas = o.Lineas.Where(l => l.Activo && l.ValorVenta > 0 && !CalculoPrevio.EsGratuita(l.TipoAfectacion))
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