using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// Cuerpo de POST .../{id}/nota-debito. Como el de la nota de crédito: la
/// `Clave` nace al abrir la pantalla (un doble clic es la misma nota) y
/// `Simular` solo calcula.
/// </summary>
public sealed record PeticionNotaDebito(
    string? Clave,
    string? Motivo,
    string? Descripcion,
    decimal? Monto,
    string? Afectacion,
    int? Linea,
    bool Simular = false);


/// <summary>
/// 22 OCT — NOTAS DE DÉBITO (maqueta aprobada el 22 oct 2026).
///
/// Una nota de débito COBRA MÁS sobre una factura o boleta ya aceptada por
/// SUNAT. Nace del comprobante original: el cliente, la moneda y el tipo de
/// cambio salen de él. Aquí se elige POR QUÉ y CUÁNTO (catálogo 10):
///
///   01 Intereses por mora           → un monto; IGV a elegir (por defecto,
///                                     inafecto: el interés moratorio no es
///                                     una venta — que lo confirme el contador).
///   02 Aumento en el valor          → un monto sobre UN producto, con su IGV.
///   03 Penalidades / otros conceptos → un monto; IGV a elegir.
///
/// No hay tope (a diferencia de la de crédito, que no puede pasar del saldo).
/// Serie FD01 para facturas y BD01 para boletas: se crea sola la primera vez.
/// Usa el mismo permiso que la nota de crédito (facturacion.notas_credito):
/// quien corrige un comprobante hacia abajo también lo hace hacia arriba.
/// </summary>
public static class EndpointsNotasDebito
{
    private static readonly Dictionary<string, string> Motivos = new()
    {
        ["01"] = "Intereses por mora",
        ["02"] = "Aumento en el valor",
        ["03"] = "Penalidades u otros conceptos",
    };

    /// <summary>Las afectaciones que se pueden elegir para intereses y penalidades.</summary>
    private static readonly string[] AfectacionesLibres = ["10", "20", "30"];

    private static readonly string[] EstadosQueCuentan =
    [
        EstadoDocumento.Borrador, EstadoDocumento.Encolado, EstadoDocumento.Aceptado,
        EstadoDocumento.Observado, EstadoDocumento.Error
    ];

    public static string SerieDeNota(string tipoOriginal) => tipoOriginal == "03" ? "BD01" : "FD01";

    /// <summary>Las notas de débito de un comprobante, de la más vieja a la más nueva.</summary>
    public static async Task<List<EndpointsNotasCredito.NotaResumen>> NotasDeAsync(
        ContextoPlataforma db, Guid id, CancellationToken ct)
    {
        var filas = await db.Documentos.AsNoTracking()
            .Where(x => x.DocumentoRefId == id && x.Tipo == "08")
            .OrderBy(x => x.CreadoEn)
            .Select(x => new { x.Id, x.Serie, x.Correlativo, x.TipoNota, x.MotivoNota, x.ImporteTotal, x.Estado, x.FechaEmision })
            .ToListAsync(ct);

        return filas.Select(x => new EndpointsNotasCredito.NotaResumen(
            x.Id,
            x.Serie is { Length: > 0 } s && x.Correlativo is { } n ? $"{s}-{n:D8}" : "(sin número)",
            x.TipoNota, x.MotivoNota, x.ImporteTotal, x.Estado, x.FechaEmision,
            EstadosQueCuentan.Contains(x.Estado))).ToList();
    }

    public static void MapearNotasDebito(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/documentos")
                         .WithTags("Facturación · notas de débito");

        // =====================================================================
        grupo.MapGet("/{id:guid}/nota-debito", async (
            Guid id, ContextoPlataforma db, OpcionesConexionSunat conexionSunat, CancellationToken ct) =>
        {
            var o = await db.Documentos.AsNoTracking()
                .Include(x => x.Lineas).Include(x => x.Cliente)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (o is null) return Results.NotFound();

            var problema = await ProblemaAsync(o, db, conexionSunat, ct);
            var notasCredito = await EndpointsNotasCredito.NotasDeAsync(db, id, ct);
            var notas = await NotasDeAsync(db, id, ct);

            return Results.Ok(new
            {
                o.Id, o.Tipo, numero = o.Numero, o.Moneda, o.FechaEmision, o.ImporteTotal, o.Ambiente,
                o.FormaPago,
                vence = o.Cuotas.Count > 0 ? o.Cuotas.Max(c => c.Vence) : (DateOnly?)null,
                cliente = o.Cliente is null ? null : new { o.Cliente.RazonSocial, o.Cliente.NumeroDocumento },
                serieNota = SerieDeNota(o.Tipo),
                puede = problema is null,
                problema,
                // Lo que sumaron las notas de débito y restaron las de crédito: para el «total a cobrar».
                debitos = notas.Where(n => n.Cuenta).Sum(n => n.ImporteTotal),
                creditos = notasCredito.Where(n => n.Cuenta).Sum(n => n.ImporteTotal),
                notas,
                lineas = o.Lineas.Where(l => l.Activo).OrderBy(l => l.Numero).Select(l => new
                {
                    l.Numero, l.Descripcion, l.ImporteTotal, l.TipoAfectacion,
                    sePuede = CalculoPrevio.SeSabeEstimar(l.TipoAfectacion)
                }).ToList()
            });
        })
        .RequierePermiso(Permisos.Facturacion.NotasCredito)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Lo que hace falta para hacer una nota de débito de este comprobante");


        // =====================================================================
        grupo.MapPost("/{id:guid}/nota-debito", async (
            Guid id, PeticionNotaDebito p, ContextoPlataforma db, ContextoPeticion ctx,
            ServicioEmision emision, ClienteFacturacion facturacion,
            OpcionesConexionSunat conexionSunat, ILoggerFactory logs, CancellationToken ct) =>
        {
            var o = await db.Documentos.AsNoTracking()
                .Include(x => x.Lineas)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (o is null) return Results.NotFound();

            var clave = string.IsNullOrWhiteSpace(p.Clave) ? null : "nd-" + p.Clave.Trim();
            if (clave is { Length: > 80 }) clave = clave[..80];

            if (!p.Simular && clave is not null &&
                await db.Documentos.Include(x => x.Lineas)
                        .FirstOrDefaultAsync(x => x.IdempotencyKey == clave, ct) is { } yaHecha)
                return await EmitirOResponder(yaHecha, emision, db, ct);

            if (await ProblemaAsync(o, db, conexionSunat, ct) is { } problema)
                return Problema(p.Simular, problema);

            var armada = Armar(o, p);
            if (armada.Problema is { } malArmada) return Problema(p.Simular, malArmada);
            var linea = armada.Linea!;

            var nota = new Documento
            {
                EmpresaId      = o.EmpresaId,
                Tipo           = "08",
                Serie          = SerieDeNota(o.Tipo),
                ClienteId      = o.ClienteId,
                FechaEmision   = EndpointsNotasCredito.HoyEnPeru(),
                Moneda         = o.Moneda,
                TipoCambio     = o.TipoCambio,
                DocumentoRefId = o.Id,
                TipoNota       = p.Motivo!.Trim(),
                MotivoNota     = Descripcion(p),
                Estado         = EstadoDocumento.Borrador,
                IdempotencyKey = clave ?? Guid.NewGuid().ToString("N"),
                // 043: si el original iba por WhatsApp, la nota también se propone.
                WhatsappEnviar = o.WhatsappEnviar
            };

            linea.EmpresaId = o.EmpresaId;
            linea.Numero = 1;
            CalculoPrevio.Calcular(linea);
            nota.Lineas.Add(linea);
            CalculoPrevio.Totalizar(nota);

            if (nota.ImporteTotal <= 0)
                return Problema(p.Simular, "La nota tiene que ser por un monto mayor que cero.");

            if (p.Simular)
                return Results.Ok(new
                {
                    problema = (string?)null,
                    total = nota.ImporteTotal,
                    gravado = nota.TotalGravado, exonerado = nota.TotalExonerado,
                    inafecto = nota.TotalInafecto, exportacion = nota.TotalExportacion,
                    igv = nota.TotalIgv,
                    serie = nota.Serie,
                    descripcionLinea = linea.Descripcion
                });

            // LA SERIE: se crea sola la primera vez, en 0. 409 = ya existía, también vale.
            var actor = await db.Usuarios.AsNoTracking()
                .Where(u => u.Id == ctx.UsuarioId).Select(u => u.Correo).FirstOrDefaultAsync(ct);
            var serie = await facturacion.ConfigurarAsync<JsonElement>(
                HttpMethod.Post, "v1/configuracion/series", new
                {
                    tipoComprobante = "08", serie = nota.Serie, ultimoCorrelativo = 0, confirmado = false, actor
                }, ct: ct);

            if (!serie.Exito && serie.Estado != StatusCodes.Status409Conflict)
                return Results.Json(new
                {
                    error = $"No se pudo preparar la serie {nota.Serie} para las notas de débito.",
                    detalle = serie.Error
                }, statusCode: StatusCodes.Status502BadGateway);

            db.Documentos.Add(nota);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                logs.CreateLogger("Plataforma.NotasDebito")
                    .LogInformation(ex, "Nota de débito con la misma clave guardada a la vez.");
                db.ChangeTracker.Clear();
                var otra = await db.Documentos.Include(x => x.Lineas)
                    .FirstOrDefaultAsync(x => x.IdempotencyKey == nota.IdempotencyKey, ct);
                if (otra is null) throw;
                return await EmitirOResponder(otra, emision, db, ct);
            }

            return await EmitirOResponder(nota, emision, db, ct);
        })
        .RequierePermiso(Permisos.Facturacion.NotasCredito)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Calcular o emitir una nota de débito de este comprobante");
    }


    // =========================================================================

    private static IResult Problema(bool simular, string problema) =>
        simular ? Results.Ok(new { problema }) : Results.BadRequest(new { error = problema });

    private static async Task<IResult> EmitirOResponder(
        Documento nota, ServicioEmision emision, ContextoPlataforma db, CancellationToken ct)
    {
        if (nota.SePuedeEditar)
        {
            if (nota.FechaEmision < EndpointsNotasCredito.HoyEnPeru()) nota.FechaEmision = EndpointsNotasCredito.HoyEnPeru();

            var r = await emision.EmitirAsync(nota, ct);
            if (!r.Exito)
                return Results.Json(new
                {
                    error = string.IsNullOrWhiteSpace(r.Detalle) ? r.Error : $"{r.Error} {r.Detalle}",
                    detalle = r.Detalle, id = nota.Id, estado = r.Documento.Estado
                }, statusCode: StatusCodes.Status502BadGateway);
        }

        // 043: si va por WhatsApp, queda pendiente (el detalle lo abre).
        var whatsapp = nota.Estado is not (EstadoDocumento.Error or EstadoDocumento.Borrador)
                       && await EnvioWhatsapp.MarcarPendienteAsync(nota, db, ct);

        return Results.Ok(new
        {
            whatsapp,
            nota.Id,
            numero = nota.Numero,
            nota.Estado,
            nota.ImporteTotal,
            mensaje = nota.Serie?.StartsWith('B') == true
                ? $"Nota de débito {nota.Numero} emitida. Las notas de boletas viajan a SUNAT en el resumen diario: su estado tardará en cambiar."
                : $"Nota de débito {nota.Numero} emitida. Está en camino a SUNAT; su estado se actualizará en unos segundos."
        });
    }

    /// <summary>Por qué este comprobante no admite nota de débito ahora, o null.</summary>
    private static async Task<string?> ProblemaAsync(
        Documento o, ContextoPlataforma db, OpcionesConexionSunat conexionSunat, CancellationToken ct)
    {
        if (o.Tipo is not ("01" or "03"))
            return "Las notas de débito se hacen sobre facturas y boletas.";

        if (o.Estado == EstadoDocumento.Anulado)
            return "Este comprobante está anulado ante SUNAT: no se le puede cobrar más.";

        if (o.Estado is EstadoDocumento.Rechazado or EstadoDocumento.Error or EstadoDocumento.Borrador)
            return "Este comprobante no vale ante SUNAT (no fue aceptado): no admite nota de débito.";

        if (o.Estado == EstadoDocumento.Encolado || o.Serie is null || o.Correlativo is null || o.ComprobanteId is null)
            return "Todavía no tiene respuesta de SUNAT. Espera a que SUNAT lo acepte.";

        var ambienteEmpresa = await db.Empresas.AsNoTracking()
            .Where(e => e.Id == o.EmpresaId).Select(e => e.Ambiente).FirstOrDefaultAsync(ct);
        var ambiente = ambienteEmpresa == "produccion" ? "produccion" : "beta";

        if ((o.Ambiente ?? "beta") != ambiente)
            return o.Ambiente == "produccion"
                ? "Este comprobante es real, pero la empresa está ahora en modo prueba. Haz la nota cuando vuelva a SUNAT real."
                : "Este comprobante es de prueba (SUNAT beta) y la empresa ya está en SUNAT real: no tiene valor.";

        var conexion = await ConexionSunat.LeerAsync(db, conexionSunat, ct);
        return ConexionSunat.Bloqueo(conexion, ambienteEmpresa);
    }

    private sealed record Armada(DocumentoLinea? Linea, string? Problema);

    /// <summary>La única línea de la nota: el concepto, el monto con IGV y su afectación.</summary>
    private static Armada Armar(Documento o, PeticionNotaDebito p)
    {
        var motivo = p.Motivo?.Trim() ?? "";
        if (!Motivos.ContainsKey(motivo))
            return new(null, "Elige por qué cobras más (el motivo de la nota).");

        if (!p.Simular && (p.Descripcion?.Trim().Length ?? 0) < 5)
            return new(null, "Explica en una línea por qué (sale en el PDF y va a SUNAT).");

        if (p.Monto is not { } monto || monto <= 0)
            return new(null, "Escribe el monto, con IGV si lleva.");
        monto = Math.Round(monto, 2, MidpointRounding.AwayFromZero);

        string afectacion, descripcion;
        if (motivo == "02")
        {
            var l = o.Lineas.Where(x => x.Activo).FirstOrDefault(x => x.Numero == p.Linea);
            if (l is null) return new(null, "Elige a qué producto se aplica el aumento.");
            if (!CalculoPrevio.SeSabeEstimar(l.TipoAfectacion))
                return new(null, "A ese producto no se le puede hacer un aumento desde aquí.");
            afectacion = l.TipoAfectacion;
            descripcion = "AUMENTO EN EL VALOR: " + l.Descripcion;
        }
        else
        {
            afectacion = string.IsNullOrWhiteSpace(p.Afectacion) ? "30" : p.Afectacion.Trim();
            if (!AfectacionesLibres.Contains(afectacion))
                return new(null, "El IGV de la nota es gravado (10), exonerado (20) o inafecto (30).");
            descripcion = motivo == "01" ? "INTERESES POR MORA" : "PENALIDADES Y OTROS CONCEPTOS";
        }

        return new(new DocumentoLinea
        {
            Descripcion    = descripcion.Length > 250 ? descripcion[..250] : descripcion,
            UnidadMedida   = "ZZ",
            Cantidad       = 1,
            ValorUnitario  = EndpointsNotasCredito.ValorSinIgv(monto, afectacion),
            TipoAfectacion = afectacion
        }, null);
    }

    private static string Descripcion(PeticionNotaDebito p)
    {
        var d = (p.Descripcion ?? "").Trim();
        return d.Length > 250 ? d[..250] : d;
    }
}