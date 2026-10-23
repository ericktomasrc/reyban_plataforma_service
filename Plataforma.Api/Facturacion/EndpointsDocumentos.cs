using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

public record PeticionLinea(
    Guid? ProductoId,
    string? Codigo,
    string Descripcion,
    string UnidadMedida,
    decimal Cantidad,
    decimal ValorUnitario,
    decimal Descuento,
    string TipoAfectacion);

public record PeticionDocumento(
    string Tipo,
    string Serie,
    Guid ClienteId,
    DateOnly? FechaEmision,
    string Moneda,
    List<PeticionLinea> Lineas,
    Dictionary<string, string>? DatosExtra,
    decimal? TipoCambio = null,
    // 040: contado o crédito, y la detracción. Solo facturas.
    string? FormaPago = null,
    List<PeticionCuota>? Cuotas = null,
    PeticionDetraccion? Detraccion = null,
    // 043: mandarlo por WhatsApp al emitirlo. null = lo que diga el cliente.
    bool? Whatsapp = null);

/// <summary>21 oct: una serie como la devuelve el servicio (GET /v1/configuracion/series).</summary>
public sealed record SerieDelServicio(string TipoComprobante, string Serie, int UltimoCorrelativo, bool Activo);

/// <summary>040: una cuota, en la moneda de la factura.</summary>
public record PeticionCuota(decimal Monto, DateOnly Vence);

/// <summary>040: la detracción tal como la escribe quien emite.</summary>
public record PeticionDetraccion(string Codigo, decimal Porcentaje, string Cuenta, string? MedioPago);


/// <summary>
/// Emitir comprobantes.
///
/// EL CICLO, EN TRES TIEMPOS:
///
///   1. **Borrador.** Se arma aquí, se edita y se borra. No existe para nadie
///      más.
///   2. **Emitir.** Se manda al servicio, que asigna el número y lo encola.
///      A partir de aquí ya no se puede cambiar.
///   3. **El estado lo trae SUNAT**, por webhook: EndpointsWebhookFacturacion.
///
/// POR QUÉ UN BORRADOR Y NO EMITIR DIRECTO: porque armar una factura de quince
/// líneas lleva un rato, y perderla por un clic en el sitio equivocado o una
/// sesión caducada es inaceptable. Además deja revisarla antes del paso que no
/// tiene vuelta atrás.
/// </summary>
public static class EndpointsDocumentos
{
    /// <summary>
    /// Lo que se puede emitir hoy.
    ///
    /// FACTURA Y BOLETA, Y NADA MÁS, a propósito. El servicio emite el catálogo
    /// entero —notas, liquidaciones, retenciones, percepciones, guías— pero
    /// cada uno tiene su pantalla, sus reglas y sus campos. Abrirlos todos a la
    /// vez sería tener siete pantallas a medias en vez de dos terminadas.
    /// </summary>
    private static readonly Dictionary<string, string> Tipos = new()
    {
        ["01"] = "Factura",
        ["03"] = "Boleta de venta"
    };

    public static void MapearDocumentos(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/documentos")
                         .WithTags("Facturación · comprobantes");


        // =====================================================================
        // LISTAR
        // =====================================================================
        grupo.MapGet("/", async (
            string? estado, string? q, int? pagina, string? tipo,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            const int PorPagina = 50;
            var p = Math.Max(1, pagina ?? 1);

            var consulta = db.Documentos.AsNoTracking();

            // 031: «Notas de crédito» (07) o un tipo concreto.
            if (!string.IsNullOrWhiteSpace(tipo))
                consulta = consulta.Where(d => d.Tipo == tipo);

            if (!string.IsNullOrWhiteSpace(estado))
                consulta = consulta.Where(d => d.Estado == estado);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var texto = q.Trim().ToLowerInvariant();

                consulta = consulta.Where(d =>
                    (d.Serie != null && d.Serie.ToLower().Contains(texto)) ||
                    d.Cliente!.RazonSocial.ToLower().Contains(texto) ||
                    d.Cliente!.NumeroDocumento.Contains(texto));
            }

            var total = await consulta.CountAsync(ct);

            var filas = await consulta
                // Por fecha de emisión y después por cuándo se creó: dos
                // comprobantes del mismo día se ordenan por el orden real en
                // que se hicieron, no al azar.
                .OrderByDescending(d => d.FechaEmision)
                .ThenByDescending(d => d.CreadoEn)
                .Skip((p - 1) * PorPagina)
                .Take(PorPagina)
                .Select(d => new
                {
                    d.Id, d.Tipo, d.Serie, d.Correlativo, d.Estado,
                    d.FechaEmision, d.Moneda, d.ImporteTotal,
                    d.CodigoSunat, d.MensajeSunat, d.ComprobanteId,
                    d.Ambiente,   // 030: «beta» = prueba, sin valor ante SUNAT
                    d.DocumentoRefId,   // 031: la nota de crédito, a qué afecta
                    cliente = new { d.Cliente!.RazonSocial, d.Cliente.NumeroDocumento }
                })
                .ToListAsync(ct);

            // 031: el número del comprobante al que afecta cada nota, en una consulta.
            var refs = filas.Where(f => f.DocumentoRefId is not null)
                            .Select(f => f.DocumentoRefId!.Value).Distinct().ToList();
            var numerosRef = refs.Count == 0
                ? new Dictionary<Guid, string>()
                : (await db.Documentos.AsNoTracking()
                        .Where(x => refs.Contains(x.Id))
                        .Select(x => new { x.Id, x.Serie, x.Correlativo })
                        .ToListAsync(ct))
                  .ToDictionary(x => x.Id, x => $"{x.Serie}-{x.Correlativo ?? 0:D8}");

            return Results.Ok(new
            {
                total, pagina = p, porPagina = PorPagina,
                filas = filas.Select(f => new
                {
                    f.Id, f.Tipo, f.Serie, f.Correlativo, f.Estado,
                    f.FechaEmision, f.Moneda, f.ImporteTotal,
                    f.CodigoSunat, f.MensajeSunat, f.ComprobanteId,
                    f.Ambiente,
                    afecta = f.DocumentoRefId is { } r && numerosRef.TryGetValue(r, out var n) ? n : null,
                    f.cliente
                })
            });
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Listar los comprobantes de mi empresa");


        // =====================================================================
        // 21 OCT: LAS SERIES DE MI EMPRESA, del servicio, con su último número.
        // La pantalla de emisión las ofrece en una lista (antes era un campo
        // de texto que empezaba en F001 aunque la empresa usara F002) y enseña
        // el número que tocará. El número final lo pone el servicio al emitir.
        // =====================================================================
        grupo.MapGet("/series", async (
            ClienteFacturacion facturacion, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var r = await facturacion.ConfigurarAsync<List<SerieDelServicio>>(
                HttpMethod.Get, "v1/configuracion/series", ct: ct);

            if (!r.Exito || r.Datos is null)
                return Results.Json(new { error = r.Error ?? "No se pudieron leer las series." },
                                    statusCode: StatusCodes.Status502BadGateway);

            // 044: CADA SERIE CON SU SUCURSAL (sin asignar = la 0000), y solo las
            // de sucursales activas en las que trabaja quien emite.
            Guid? principalId = null;
            Dictionary<string, Guid> mapa = [];
            HashSet<Guid>? permitidas = null;
            HashSet<Guid> activas = [];
            if (ctx.EmpresaId is { } empresaId)
            {
                principalId = (await Sucursales.PrincipalAsync(db, empresaId, ct)).Id;
                mapa = await Sucursales.MapaAsync(db, empresaId, ct);
                permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
                activas = (await db.Sucursales.AsNoTracking()
                    .Where(x => x.EmpresaId == empresaId && x.EliminadaEn == null)
                    .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
            }
            Guid? SucursalDe(string tipo, string serie) =>
                mapa.TryGetValue(Sucursales.Clave(tipo, serie), out var m) && activas.Contains(m) ? m : principalId;

            return Results.Ok(r.Datos
                .Where(s => s.Activo)
                .Select(s => new { s, sucursalId = SucursalDe(s.TipoComprobante, s.Serie) })
                .Where(x => x.sucursalId is null ||
                            (activas.Contains(x.sucursalId.Value) && (permitidas is null || permitidas.Contains(x.sucursalId.Value))))
                .OrderBy(x => x.s.TipoComprobante).ThenBy(x => x.s.Serie)
                .Select(x => new
                {
                    tipo = x.s.TipoComprobante,
                    serie = x.s.Serie,
                    ultimo = x.s.UltimoCorrelativo,
                    siguiente = $"{x.s.Serie}-{x.s.UltimoCorrelativo + 1:D8}",
                    sucursalId = x.sucursalId
                }));
        })
        .RequierePermiso(Permisos.Facturacion.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Las series de mi empresa y el número que sigue");


        // =====================================================================
        // 040: LO HABITUAL DE LA DETRACCIÓN de mi empresa (cuenta del Banco de
        // la Nación, código y porcentaje). Lo propone la pantalla de emisión.
        // =====================================================================
        grupo.MapGet("/preferencias-detraccion", async (
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var e = await db.Empresas.AsNoTracking()
                .Where(x => x.Id == ctx.EmpresaId)
                .Select(x => new { cuenta = x.DetraccionCuenta, codigo = x.DetraccionCodigo, porcentaje = x.DetraccionPorcentaje })
                .FirstOrDefaultAsync(ct);

            return Results.Ok(e ?? new { cuenta = (string?)null, codigo = (string?)null, porcentaje = (decimal?)null });
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("La cuenta de detracciones y el código habitual de mi empresa");


        // =====================================================================
        grupo.MapGet("/tipos", () => Results.Ok(
            Tipos.Select(t => new { codigo = t.Key, nombre = t.Value })))
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Qué se puede emitir hoy");


        // =====================================================================
        // ATRASADOS — lo que se está quedando sin plazo ante SUNAT
        //
        // Un comprobante que no llega a SUNAT dentro de plazo es una multa, y
        // hoy nada lo avisaba: se quedaba «en camino» o «no se pudo enviar» en
        // la lista, entre los demás, hasta que alguien lo viera.
        //
        // ══════════════════════════════════════════════════════════════════
        // TRES REGLAS QUE PARECEN DETALLES Y NO LO SON
        //
        //   · FACTURA Y BOLETA NO TIENEN EL MISMO PLAZO: 3 días calendario la
        //     factura, 7 la boleta. Y una boleta se queda «encolada» hasta el
        //     día siguiente POR DISEÑO —viaja en el resumen diario—. Medirlas
        //     igual haría saltar la alarma con cada boleta al día siguiente de
        //     emitirla, y una alarma que salta todos los días se ignora; y
        //     entonces se ignora también la de verdad.
        //
        //   · SE CUENTA DESDE `fecha_emision`, que es desde donde cuenta SUNAT,
        //     no desde que se encoló. Uno emitido el día 1 y encolado el día 4
        //     ya está fuera de plazo el mismo día que entra en la cola.
        //
        //   · `error` QUEMA PLAZO IGUAL QUE `encolado`. Tampoco llegó a SUNAT,
        //     y el reloj corre lo mismo.
        // ══════════════════════════════════════════════════════════════════
        //
        // SE AVISA ANTES DE QUE VENZA, no cuando ya venció: la factura cuando
        // le queda un día, la boleta cuando le quedan dos. Ver `Plazos`.
        //
        // SE ORDENA POR URGENCIA Y SE DEVUELVEN SOLO LOS CINCO PRIMEROS, con el
        // total aparte. Por urgencia y no por fecha de emisión porque los
        // plazos difieren: una boleta del día 4 (le quedan dos) no puede salir
        // antes que una factura del día 6 que ya venció. Y cinco porque con
        // diez la banda ya es una pared de texto que empuja la lista fuera de
        // la pantalla; el resto se cuenta, no se enumera.
        // =====================================================================
        grupo.MapGet("/atrasados", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var hoy = HoyEnPeru();

            // Los límites se calculan aquí y la base solo compara fechas: así
            // la consulta usa el índice y no hace aritmética fila por fila.
            var limiteFactura = hoy.AddDays(-Plazos.AvisoFactura);
            var limiteBoleta  = hoy.AddDays(-Plazos.AvisoBoleta);

            var consulta = db.Documentos
                .AsNoTracking()
                .Where(d => d.Estado == EstadoDocumento.Encolado || d.Estado == EstadoDocumento.Error)
                .Where(d => ((d.Tipo == "01" || ((d.Tipo == "07" || d.Tipo == "08") && d.Serie!.StartsWith("F"))) && d.FechaEmision <= limiteFactura) ||
                            ((d.Tipo == "03" || ((d.Tipo == "07" || d.Tipo == "08") && d.Serie!.StartsWith("B"))) && d.FechaEmision <= limiteBoleta));

            var total = await consulta.CountAsync(ct);

            // El vencimiento depende del tipo y se calcula en memoria, así que
            // el orden por urgencia va DESPUÉS de traerlos. Se traen como mucho
            // 500, los de fecha más antigua: con más atrasados que eso, el
            // orden exacto de los cinco primeros es el menor de los problemas.
            var filas = await consulta
                .OrderBy(d => d.FechaEmision)
                .Take(500)
                .Select(d => new
                {
                    d.Id, d.Tipo, d.Serie, d.Correlativo, d.Estado, d.FechaEmision,
                    Cliente = d.Cliente!.RazonSocial
                })
                .ToListAsync(ct);

            var atrasados = filas.Select(d =>
            {
                var vence  = d.FechaEmision.AddDays(Plazos.De(d.Tipo is "07" or "08" && d.Serie?.StartsWith('B') == true ? "03"
                                                              : d.Tipo is "07" or "08" ? "01" : d.Tipo));
                var quedan = vence.DayNumber - hoy.DayNumber;

                return new
                {
                    d.Id,
                    d.Tipo,
                    numero = d.Serie is { Length: > 0 } s && d.Correlativo is { } n ? $"{s}-{n:D8}" : null,
                    d.Estado,
                    d.FechaEmision,
                    venceEl       = vence,
                    diasRestantes = quedan,
                    vencido       = quedan < 0,
                    cliente       = d.Cliente
                };
            })
            .OrderBy(a => a.venceEl)
            .ThenBy(a => a.FechaEmision)
            .ToList();

            return Results.Ok(new
            {
                hoy,
                total,
                vencidos  = atrasados.Count(a => a.vencido),
                atrasados = atrasados.Take(5)
            });
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Comprobantes que no llegaron a SUNAT y se acercan al plazo");


        // =====================================================================
        grupo.MapGet("/{id:guid}", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var d = await db.Documentos
                .AsNoTracking()
                .Include(x => x.Lineas)
                .Include(x => x.Cliente)
                .FirstOrDefaultAsync(x => x.Id == id, ct);

            if (d is null) return Results.NotFound();

            var historial = await db.DocumentoHistorial
                .AsNoTracking()
                .Where(h => h.DocumentoId == id)
                .OrderBy(h => h.OcurridoEn)
                .Select(h => new
                {
                    h.OcurridoEn, h.EstadoAnterior, h.EstadoNuevo,
                    h.Origen, h.CodigoSunat, h.Mensaje
                })
                .ToListAsync(ct);

            // 031: sus notas de crédito y, si es una nota, el comprobante al que afecta.
            var notas = await EndpointsNotasCredito.NotasDeAsync(db, id, ct);
            var afecta = d.DocumentoRefId is { } refId
                ? await db.Documentos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == refId, ct)
                : null;

            return Results.Ok(new
            {
                d.Id, d.Tipo, d.Serie, d.Correlativo, d.Estado,
                numero = d.Numero,
                d.FechaEmision, d.Moneda, d.TipoCambio, d.ComprobanteId,
                d.Ambiente,   // 030
                d.CodigoSunat, d.MensajeSunat, d.Observaciones,
                sePuedeEditar = d.SePuedeEditar,

                cliente = d.Cliente is null ? null : new
                {
                    d.Cliente.Id, d.Cliente.TipoDocumento, d.Cliente.NumeroDocumento,
                    d.Cliente.RazonSocial, d.Cliente.Direccion,
                    d.Cliente.Whatsapp, d.Cliente.WhatsappActivo   // 043
                },

                // 043: WhatsApp de este comprobante.
                whatsapp = new
                {
                    enviar = d.WhatsappEnviar,
                    estado = d.WhatsappEstado,
                    numero = d.WhatsappNumero,
                    en = d.WhatsappEn
                },

                totales = new
                {
                    d.TotalGravado, d.TotalExonerado, d.TotalInafecto,
                    d.TotalGratuito, d.TotalExportacion, d.TotalDescuentos,
                    d.TotalIgv, d.TotalIsc, d.TotalOtros, d.ImporteTotal
                },

                lineas = d.Lineas.OrderBy(l => l.Numero).Select(l => new
                {
                    l.Id, l.Numero, l.ProductoId, l.Codigo, l.Descripcion,
                    l.UnidadMedida, l.Cantidad, l.ValorUnitario, l.PrecioUnitario,
                    l.Descuento, l.TipoAfectacion, l.Igv, l.ValorVenta, l.ImporteTotal
                }),

                d.DatosExtra,
                historial,

                // 040
                d.FormaPago, d.Cuotas, d.Detraccion,

                // 031: notas de crédito
                d.TipoNota, d.MotivoNota,
                afecta = afecta is null ? null : new { afecta.Id, numero = afecta.Numero, afecta.Tipo },
                notasCredito = notas,
                saldo = EndpointsNotasCredito.Saldo(d.ImporteTotal, notas),
                // 22 oct: sus notas de débito.
                notasDebito = await EndpointsNotasDebito.NotasDeAsync(db, id, ct),
                // 23 oct: reglas de la baja (ReglasBaja).
                notasVigentes = await ReglasBaja.NotasVigentesAsync(db, id, ct),
                bajaPedidaEn = ReglasBaja.Pendiente(d)
            });
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Ver un comprobante con sus líneas y su historial");


        // =====================================================================
        // GUARDAR EL BORRADOR
        // =====================================================================
        grupo.MapPost("/", async (
            PeticionDocumento p, ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            if (await Revisar(p, db, ct) is { } error)
                return Results.BadRequest(new { error });

            // 044: la serie dice de qué sucursal sale, y quien emite tiene que trabajar en ella.
            var suc = await Sucursales.RevisarSerieAsync(db, ctx, ctx.EmpresaId!.Value, p.Tipo, p.Serie.Trim().ToUpperInvariant(), ct);
            if (suc.Error is { } errorSucursal)
                return Results.BadRequest(new { error = errorSucursal });

            var d = new Documento
            {
                SucursalId   = suc.Id,
                EmpresaId    = ctx.EmpresaId!.Value,
                Tipo         = p.Tipo,
                Serie        = p.Serie.Trim().ToUpperInvariant(),
                ClienteId    = p.ClienteId,
                FechaEmision = p.FechaEmision ?? HoyEnPeru(),
                Moneda       = (p.Moneda ?? "PEN").ToUpperInvariant(),
                TipoCambio   = TipoCambioDe(p),
                Estado       = EstadoDocumento.Borrador,
                DatosExtra   = p.DatosExtra ?? [],

                // LA CLAVE NACE CON EL BORRADOR, no al emitir. Si se generara
                // al pulsar «Emitir», dos clics serían dos claves distintas y
                // el servicio no tendría forma de saber que son el mismo
                // documento — que es exactamente lo que la idempotencia viene
                // a resolver.
                IdempotencyKey = Guid.NewGuid().ToString("N")
            };

            RellenarLineas(d, p.Lineas, ctx.EmpresaId!.Value);

            // 040: contado o crédito, y la detracción (ya con el total).
            CopiarPago(d, p);
            if (RevisarPagoConTotal(d) is { } errorPago)
                return Results.BadRequest(new { error = errorPago });

            d.WhatsappEnviar = await EnvioWhatsapp.QuiereAsync(p.Whatsapp, p.ClienteId, db, ct);   // 043

            db.Documentos.Add(d);
            await RecordarDetraccionAsync(d, db, ct);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/facturacion/documentos/{d.Id}",
                new { d.Id, d.Estado, d.ImporteTotal });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Guardar un borrador");


        // =====================================================================
        grupo.MapPut("/{id:guid}", async (
            Guid id, PeticionDocumento p,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var d = await db.Documentos.Include(x => x.Lineas)
                            .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();

            if (!d.SePuedeEditar)
                return Results.BadRequest(new
                {
                    error = $"Este comprobante ya está {d.Estado} y no se puede cambiar.",
                    detalle = "Un comprobante entregado al servicio gastó un correlativo. " +
                              "Se corrige con una nota de crédito."
                });

            if (await Revisar(p, db, ct) is { } error)
                return Results.BadRequest(new { error });

            // 044: la sucursal sale de la serie (puede haber cambiado de serie).
            var suc = await Sucursales.RevisarSerieAsync(db, ctx, d.EmpresaId, p.Tipo, p.Serie.Trim().ToUpperInvariant(), ct);
            if (suc.Error is { } errorSucursal)
                return Results.BadRequest(new { error = errorSucursal });
            d.SucursalId   = suc.Id;

            d.Tipo         = p.Tipo;
            d.Serie        = p.Serie.Trim().ToUpperInvariant();
            d.ClienteId    = p.ClienteId;
            d.FechaEmision = p.FechaEmision ?? d.FechaEmision;
            d.Moneda       = (p.Moneda ?? "PEN").ToUpperInvariant();
            d.TipoCambio   = TipoCambioDe(p);
            d.DatosExtra   = p.DatosExtra ?? [];

            // Las líneas se reescriben REUSANDO LAS FILAS POR NÚMERO: nada se
            // borra. El porqué, en ReescribirLineasAsync.
            await ReescribirLineasAsync(d, p.Lineas, ctx.EmpresaId!.Value, db, ct);

            // 040: contado o crédito, y la detracción (ya con el total).
            CopiarPago(d, p);
            if (RevisarPagoConTotal(d) is { } errorPago)
                return Results.BadRequest(new { error = errorPago });

            d.WhatsappEnviar = await EnvioWhatsapp.QuiereAsync(p.Whatsapp, p.ClienteId, db, ct);   // 043

            await RecordarDetraccionAsync(d, db, ct);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { d.Id, d.Estado, d.ImporteTotal });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Cambiar un borrador");


        // =====================================================================
        // DESCARTAR — solo borradores
        //
        // NO BORRA: DESACTIVA. Es la regla de oro nº 4 —nada se borra, el rol
        // no tiene DELETE— y aquí se cumple igual que en todas partes.
        //
        // Se intentó que esto fuera un borrado real y la base lo paró dos
        // veces, con razón:
        //
        //   · no hay GRANT DELETE sobre `documentos` en ninguna migración
        //     («permission denied for table documentos», 42501), y
        //   · todo borrador tiene desde que NACE una fila de
        //     `documento_historial` —el disparador escribe también en el
        //     INSERT— que es inmutable y lo referencia sin cascada.
        //
        // Abrir el borrado habría costado cuatro agujeros en el aislamiento
        // para poder tirar un papel a medio escribir.
        //
        // Con `Activo = false` el filtro global de EF lo esconde de la lista y
        // del detalle, el historial se conserva, y la bitácora registra quién
        // lo descartó y cuándo.
        //
        // LA RUTA SIGUE SIENDO DELETE: para quien llama, el borrador
        // desaparece. Lo que pasa por debajo es asunto de la base.
        // =====================================================================
        grupo.MapDelete("/{id:guid}", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var d = await db.Documentos.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();

            if (d.Estado != EstadoDocumento.Borrador)
                return Results.BadRequest(new
                {
                    error = $"Solo se descartan los borradores. Este está {d.Estado}."
                });

            // Las líneas se quedan como están: sin su documento visible no
            // aparecen en ningún sitio, y desactivarlas una a una no protege
            // nada que el documento no proteja ya.
            d.Activo = false;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { mensaje = "Borrador descartado." });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Descartar un borrador")
        .WithDescription("No lo borra: lo desactiva. Nada se borra en la plataforma.");


        // =====================================================================
        // EMITIR — el paso que no tiene vuelta atrás
        // =====================================================================
        grupo.MapPost("/{id:guid}/emitir", async (
            Guid id, ContextoPlataforma db, ServicioEmision emision,
            OpcionesConexionSunat conexionSunat, CancellationToken ct) =>
        {
            var d = await db.Documentos.Include(x => x.Lineas)
                            .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();

            // 031: UNA NOTA DE CRÉDITO (o, desde el 22 oct, DE DÉBITO) QUE NO LLEGÓ
            // se reintenta desde su detalle y sale con la fecha de hoy.
            if (d.Tipo is "07" or "08" && d.SePuedeEditar && d.FechaEmision < HoyEnPeru())
                d.FechaEmision = HoyEnPeru();

            // ══════════════════════════════════════════════════════════════
            // LA FECHA SE COMPRUEBA AQUÍ Y NO SE CAMBIA EN SILENCIO.
            //
            // Un borrador puede llevar días guardado, y su fecha con él. Si ya
            // está fuera del plazo de SUNAT, se dice y se para: quien emite
            // decide la fecha nueva. Cambiarla por su cuenta haría que la
            // factura saliera con un día distinto del que esa persona eligió y
            // vio en la revisión, y se enteraría días después.
            //
            // 400 Y NO 502: aquí no falló el servicio de enfrente, sino un dato
            // del documento que se arregla en la pantalla.
            //
            // El servicio rechazaría lo mismo por su cuenta (error 2108); esto
            // solo da el mensaje antes y en palabras de quien emite.
            // ══════════════════════════════════════════════════════════════
            if (d.SePuedeEditar &&
                FechaFueraDePlazo(d.Tipo, d.FechaEmision, HoyEnPeru()) is { } fechaMal)
            {
                return Results.BadRequest(new
                {
                    error = fechaMal,
                    detalle = "El borrador sigue guardado. Cambia la fecha de emisión y vuelve a emitirlo.",
                    estado = d.Estado
                });
            }

            // EN DÓLARES, CON TIPO DE CAMBIO (migración 026). El borrador se deja
            // guardar sin él; emitir, no: el servicio lo rechazaría igual, pero
            // aquí se dice en palabras de quien emite y sin gastar la llamada.
            if (d.SePuedeEditar && d.Moneda != "PEN" && d.TipoCambio is null)
            {
                return Results.BadRequest(new
                {
                    error = "Falta el tipo de cambio.",
                    detalle = "En dólares hay que poner el tipo de cambio de venta que publica la SBS " +
                              "para la fecha de emisión. El borrador sigue guardado.",
                    estado = d.Estado
                });
            }

            // 040: A CRÉDITO HACE FALTA AL MENOS UNA CUOTA, y ninguna puede vencer
            // el día de la emisión o antes (SUNAT 3249 y 3267). El borrador se
            // deja guardar a medias; emitir, no.
            if (d.SePuedeEditar && RevisarPagoAlEmitir(d) is { } pagoMal)
            {
                return Results.BadRequest(new
                {
                    error = pagoMal,
                    detalle = "El borrador sigue guardado. Corrige la forma de pago y vuelve a emitirlo.",
                    estado = d.Estado
                });
            }

            // 030: MODO PRUEBA / REAL. En QA con SUNAT real solo emiten las
            // empresas que el super pasó a real; en producción, las que ya
            // terminaron su asistente. Antes de gastar un número.
            var conexion = await ConexionSunat.LeerAsync(db, conexionSunat, ct);
            var ambienteEmpresa = await db.Empresas.AsNoTracking()
                .Where(e => e.Id == d.EmpresaId).Select(e => e.Ambiente).FirstOrDefaultAsync(ct);

            if (ConexionSunat.Bloqueo(conexion, ambienteEmpresa) is { } bloqueo)
                return Results.Json(new { error = bloqueo, codigo = "conexion_sunat", estado = d.Estado },
                                    statusCode: StatusCodes.Status409Conflict);

            var r = await emision.EmitirAsync(d, ct);

            if (!r.Exito)
            {
                // 502 Y NO 400: el documento está bien, quien falló fue el
                // servicio de enfrente. Un 400 haría que quien lo lea busque el
                // error en lo que escribió.
                // 21 OCT: EL MOTIVO VA EN EL MENSAJE. El servicio dice «No se
                // pudo emitir» y el porqué (p. ej. «No existe la serie F001»)
                // en `detalle`, que la pantalla no mostraba.
                return Results.Json(new
                {
                    error = string.IsNullOrWhiteSpace(r.Detalle) ? r.Error : $"{r.Error} {r.Detalle}",
                    detalle = r.Detalle,
                    estado = r.Documento.Estado
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            // 043: SI VA POR WHATSAPP, queda pendiente de que alguien pulse Enviar.
            var whatsapp = await EnvioWhatsapp.MarcarPendienteAsync(r.Documento, db, ct);

            return Results.Ok(new
            {
                whatsapp,
                r.Documento.Id,
                numero = r.Documento.Numero,
                r.Documento.Estado,
                r.Documento.ImporteTotal,
                r.Documento.ComprobanteId,

                // LO QUE HAY QUE DECIRLE A QUIEN EMITE, y no es obvio: el
                // servicio responde antes de hablar con SUNAT. «Encolado» es
                // normal, no un problema.
                mensaje = r.Documento.Tipo == "03"
                    ? $"Boleta {r.Documento.Numero} emitida. Las boletas viajan a SUNAT " +
                      "en el resumen diario del día siguiente, así que su estado tardará en cambiar."
                    : $"Comprobante {r.Documento.Numero} emitido. Está en camino a SUNAT; " +
                      "su estado se actualizará en unos segundos."
            });
        })
        .RequierePermiso(Permisos.Facturacion.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Mandar un borrador al servicio de facturación");


        // =====================================================================
        // 18 OCT: ANULAR DESDE LA PLATAFORMA, con los nombres de SUNAT.
        //
        //   Factura (01)  → «Comunicar baja»: comunicación de baja (RA).
        //   Boleta  (03)  → «Anular»: va en el resumen diario con estado 3.
        //
        // Un comprobante bien emitido no se toca nunca: esto es para un error,
        // una venta que no se concretó, uno emitido dos veces, o una prueba en
        // SUNAT real. Plazo de SUNAT: 7 días desde la emisión; después
        // corresponde una nota de crédito (la plataforma todavía no la emite).
        // El estado pasa a «anulado» cuando SUNAT lo acepta (llega por el
        // webhook); si lo rechaza, el comprobante sigue valiendo.
        // =====================================================================
        grupo.MapPost("/{id:guid}/anular", async (
            Guid id, PeticionAnular p, ContextoPlataforma db, ClienteFacturacion facturacion,
            CancellationToken ct) =>
        {
            // 23 oct: seguido por EF, para anotar que se pidió la baja.
            var d = await db.Documentos.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();

            var motivo = p.Motivo?.Trim() ?? "";
            if (motivo.Length < 5)
                return Results.BadRequest(new { error = "Escribe el motivo (lo lee SUNAT y queda guardado)." });
            if (motivo.Length > 100) motivo = motivo[..100];

            if (d.Estado == EstadoDocumento.Anulado)
                return Results.Conflict(new { error = "Este comprobante ya está anulado." });

            if (d.Estado is not (EstadoDocumento.Aceptado or EstadoDocumento.Observado) ||
                d.ComprobanteId is null || d.Serie is null || d.Correlativo is null)
                return Results.Conflict(new
                {
                    error = "Solo se anula lo que SUNAT aceptó.",
                    detalle = "Si todavía está en camino, espera a que SUNAT conteste. Si lo rechazó, no existe para SUNAT y no hay nada que anular."
                });

            if (d.Tipo is not ("01" or "03" or "07" or "08"))
                return Results.BadRequest(new { error = "Desde aquí se anulan facturas, boletas y notas." });

            // 031: una nota de crédito de factura (FC..) va por comunicación de
            // baja; una de boleta (BC..), por el resumen diario. Manda la serie.
            // 22 oct: la de débito (FD.. / BD..), igual.
            var porBaja = d.Tipo == "01" || (d.Tipo is "07" or "08" && d.Serie?.StartsWith('F') == true);

            var ambienteEmpresa = await db.Empresas.AsNoTracking()
                .Where(e => e.Id == d.EmpresaId).Select(e => e.Ambiente).FirstOrDefaultAsync(ct) == "produccion"
                ? "produccion" : "beta";

            if ((d.Ambiente ?? "beta") != ambienteEmpresa)
                return Results.Conflict(new
                {
                    error = d.Ambiente == "produccion"
                        ? "Este comprobante es real, pero la empresa está ahora en modo prueba. Anúlalo cuando vuelva a SUNAT real."
                        : "Este comprobante es de prueba (SUNAT beta): no tiene valor ante SUNAT y no hace falta anularlo."
                });

            var dias = HoyEnPeru().DayNumber - d.FechaEmision.DayNumber;
            if (dias > 7)
                return Results.Conflict(new
                {
                    error = $"Pasaron {dias} días desde su emisión: SUNAT ya no deja anularlo (el plazo es de 7 días).",
                    detalle = d.Tipo is "07" or "08"
                        ? "Una nota equivocada ya no se puede dar de baja pasados 7 días."
                        : "Lo que corresponde es una nota de crédito por anulación: botón «Hacer nota de crédito».",
                    codigo = "fuera_de_plazo"
                });

            // 23 OCT: REGLAS DE SUNAT. Con notas que valen, ya no hay baja (se
            // sigue con notas). Y una baja ya pedida no se pide otra vez.
            if (d.Tipo is "01" or "03" && await ReglasBaja.NotasVigentesAsync(db, d.Id, ct) is > 0 and var cuantas)
                return Results.Conflict(new
                {
                    error = $"Este comprobante tiene {cuantas} nota{(cuantas > 1 ? "s" : "")} de crédito o débito: ya no se puede dar de baja.",
                    detalle = "Una vez que tiene notas, se sigue corrigiendo con notas. Para anular lo que queda, haz una nota de crédito.",
                    codigo = "tiene_notas"
                });
            if (ReglasBaja.Pendiente(d) is { } yaPedida)
                return Results.Conflict(new
                {
                    error = $"Ya se pidió el {yaPedida.AddHours(-5):dd/MM/yyyy HH:mm}. Espera la respuesta de SUNAT.",
                    codigo = "baja_pendiente"
                });

            var r = porBaja
                ? await facturacion.EnviarAsync<System.Text.Json.JsonElement>("/v1/bajas", new
                  {
                      motivo,
                      comprobantes = new[] { new { tipoDocumento = d.Tipo, serie = d.Serie, correlativo = d.Correlativo, motivo } }
                  }, null, ct)
                : await facturacion.EnviarAsync<System.Text.Json.JsonElement>(
                      $"/v1/comprobantes/{d.ComprobanteId}/anular", new { motivo }, null, ct);

            if (!r.Exito)
                return Results.Json(new { error = r.Error, detalle = r.Detalle },
                                    statusCode: r.Estado is >= 400 and < 500 ? r.Estado : StatusCodes.Status502BadGateway);

            // 23 oct: anotada, para que no se hagan notas mientras SUNAT contesta.
            ReglasBaja.Anotar(d);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                mensaje = porBaja
                    ? $"Baja de {d.Numero} comunicada. SUNAT la confirma en unos minutos; entonces pasa a «anulado»."
                    : $"Anulación de {d.Numero} pedida. Va a SUNAT en el resumen diario; cuando lo acepte, pasa a «anulado»."
            });
        })
        .RequierePermiso(Permisos.Facturacion.Anular)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Comunicar la baja de una factura o anular una boleta (7 días)");


        // 18 OCT: AJUSTAR LA SERIE (solo hacia arriba), para el error 1033
        // «ese número ya existe en SUNAT» o una serie que ya se usaba en otro
        // sistema. Queda en la bitácora de correlativos del servicio.
        rutas.MapPost("/api/facturacion/series/ajustar", async (
            PeticionAjusteSerie p, ContextoPlataforma db, ContextoPeticion ctx,
            ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var actor = await db.Usuarios.AsNoTracking()
                .Where(u => u.Id == ctx.UsuarioId).Select(u => u.Correo).FirstOrDefaultAsync(ct);

            var r = await facturacion.ConfigurarAsync<System.Text.Json.JsonElement>(
                HttpMethod.Post, "v1/configuracion/series/ajustar",
                new { p.TipoComprobante, p.Serie, p.UltimoCorrelativo, p.Motivo, Actor = actor }, ct: ct);

            return r.Exito
                ? Results.Ok(r.Datos)
                : Results.Json(new { error = r.Error, detalle = r.Detalle },
                               statusCode: r.Estado is >= 400 and < 500 ? r.Estado : StatusCodes.Status502BadGateway);
        })
        .RequierePermiso(Permisos.Facturacion.CredencialesGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithTags("Facturación")
        .WithSummary("Subir el último número de una serie (solo hacia arriba)");
    }


    // =========================================================================

    /// <summary>
    /// Los plazos de SUNAT para que un comprobante llegue, en días calendario
    /// desde su fecha de emisión, y desde cuándo se avisa.
    ///
    /// LA FACTURA, 3 DÍAS: el mismo día o hasta tres después. Se avisa cuando
    /// le queda uno — el último día ya no da tiempo a pedir ayuda.
    ///
    /// LA BOLETA, 7 DÍAS: viaja en el resumen diario, hasta el 7.º día. Se avisa
    /// cuando le quedan dos. El día siguiente a emitirla es normal verla
    /// «encolada», y por eso el aviso no puede empezar antes.
    /// </summary>
    private static class Plazos
    {
        public const int Factura      = 3;
        public const int Boleta       = 7;
        public const int AvisoFactura = Factura - 1;
        public const int AvisoBoleta  = Boleta - 2;

        public static int De(string tipo) => tipo == "03" ? Boleta : Factura;
    }


    /// <summary>
    /// Por qué esta fecha de emisión ya no se puede mandar a SUNAT, o null si
    /// se puede.
    ///
    /// SUNAT ADMITE UNA FECHA ANTERIOR A HOY, dentro del plazo del tipo —«ayer
    /// vendí, hoy facturo con la fecha de ayer» es lo normal—, y no admite
    /// nunca una futura. La pantalla ya acota el campo a ese margen; esto es
    /// para el borrador que se quedó guardado y envejeció.
    ///
    /// Los mismos plazos que la alarma de atrasados (`Plazos`): un documento
    /// que esto deja emitir no puede salir en la banda como vencido.
    /// </summary>
    private static string? FechaFueraDePlazo(string tipo, DateOnly fecha, DateOnly hoy)
    {
        static string F(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        if (fecha > hoy)
            return $"La fecha de emisión ({F(fecha)}) es posterior a hoy. SUNAT no acepta comprobantes con fecha futura.";

        var plazo  = Plazos.De(tipo);
        var minima = hoy.AddDays(-plazo);

        if (fecha < minima)
        {
            var que = tipo == "03" ? "una boleta" : "una factura";
            return $"Este borrador tiene fecha del {F(fecha)} y el plazo para enviarlo a SUNAT ya venció: " +
                   $"{que} tiene {plazo} días. Cambia la fecha de emisión a una desde el {F(minima)} " +
                   "antes de emitir.";
        }

        return null;
    }


    private static void RellenarLineas(Documento d, List<PeticionLinea> lineas, Guid empresaId)
    {
        var numero = 1;

        foreach (var p in lineas)
        {
            var l = new DocumentoLinea { EmpresaId = empresaId, Numero = numero++ };
            CopiarLinea(l, p);
            d.Lineas.Add(l);
        }

        CalculoPrevio.Totalizar(d);
    }


    /// <summary>
    /// Reescribe las líneas de un borrador que ya existe REUSANDO SUS FILAS
    /// POR NÚMERO. No borra nada.
    ///
    /// ══════════════════════════════════════════════════════════════════════
    /// POR QUÉ NO SE BORRAN Y SE VUELVEN A ESCRIBIR
    ///
    /// Antes esto hacía `RemoveRange` + insertar de nuevo. `documento_lineas`
    /// tiene GRANT SELECT, INSERT, UPDATE —sin DELETE, a propósito: regla de
    /// oro nº 4— y el segundo guardado de un borrador daba 42501. No se
    /// arregla concediendo DELETE.
    ///
    ///   · La línea nº i va a la fila con `numero = i` si ya existe, ACTIVA O
    ///     NO: se sobrescriben sus campos y se pone `activo = true`.
    ///   · Si no existe, se inserta.
    ///   · Las filas con `numero > N` se desactivan.
    ///
    /// POR QUÉ REUSAR Y NO «DESACTIVAR E INSERTAR»: el índice
    /// `ux_documento_lineas_numero` es UNIQUE (documento_id, numero) SIN filtro
    /// por activo. Una fila desactivada sigue ocupando su número, y una nueva
    /// con ese número chocaría (23505) en cuanto el borrador vuelva a crecer.
    ///
    /// POR QUÉ `IgnoreQueryFilters`: el filtro global de EF esconde las filas
    /// con `activo = false`. Sin él, un borrador que pasó de 3 líneas a 2 y
    /// vuelve a 3 no vería la fila nº 3 desactivada, intentaría insertarla
    /// otra vez y daría el mismo 23505.
    ///
    /// Al cargarlas así, EF engancha también las desactivadas en `d.Lineas`.
    /// NO se sacan de la colección —sacar una línea de `d.Lineas` EF lo
    /// convierte en un DELETE—; por eso `CalculoPrevio.Totalizar` suma solo
    /// las activas. Fuera de esta petición `d.Lineas` llega ya filtrada.
    /// ══════════════════════════════════════════════════════════════════════
    /// </summary>
    private static async Task ReescribirLineasAsync(
        Documento d, List<PeticionLinea> lineas, Guid empresaId,
        ContextoPlataforma db, CancellationToken ct)
    {
        var existentes = await db.DocumentoLineas
            .IgnoreQueryFilters()
            .Where(l => l.DocumentoId == d.Id)
            .ToListAsync(ct);

        // El índice único garantiza un solo número por documento.
        var porNumero = existentes.ToDictionary(l => l.Numero);

        var numero = 1;

        foreach (var p in lineas)
        {
            if (!porNumero.TryGetValue(numero, out var l))
            {
                l = new DocumentoLinea { EmpresaId = empresaId, Numero = numero };
                d.Lineas.Add(l);
            }

            l.Activo = true;
            CopiarLinea(l, p);

            numero++;
        }

        foreach (var sobrante in existentes.Where(l => l.Numero > lineas.Count && l.Activo))
            sobrante.Activo = false;

        CalculoPrevio.Totalizar(d);
    }


    /// <summary>
    /// Copia lo que llega de la pantalla a una línea y calcula sus importes.
    /// Un solo sitio para la línea nueva y para la reusada: si mañana se añade
    /// un campo, no puede quedarse sin copiar en uno de los dos caminos.
    /// </summary>
    private static void CopiarLinea(DocumentoLinea l, PeticionLinea p)
    {
        l.ProductoId     = p.ProductoId;
        l.Codigo         = string.IsNullOrWhiteSpace(p.Codigo) ? null : p.Codigo.Trim();
        l.Descripcion    = p.Descripcion.Trim();
        l.UnidadMedida   = p.UnidadMedida.Trim().ToUpperInvariant();
        l.Cantidad       = p.Cantidad;
        l.ValorUnitario  = p.ValorUnitario;
        l.Descuento      = p.Descuento;
        l.TipoAfectacion = p.TipoAfectacion;

        CalculoPrevio.Calcular(l);
    }


    /// <summary>
    /// El tipo de cambio que se guarda: nada en soles; en otra moneda, el que
    /// vino, con los 3 decimales que declara SUNAT.
    /// </summary>
    private static decimal? TipoCambioDe(PeticionDocumento p) =>
        (p.Moneda ?? "PEN").ToUpperInvariant() == "PEN" || p.TipoCambio is null
            ? null
            : Math.Round(p.TipoCambio.Value, 3, MidpointRounding.AwayFromZero);


    private static async Task<string?> Revisar(
        PeticionDocumento p, ContextoPlataforma db, CancellationToken ct)
    {
        if (!Tipos.ContainsKey(p.Tipo ?? ""))
            return "Por ahora solo se pueden emitir facturas y boletas.";

        // SOLES O DÓLARES (migración 026). El tipo de cambio puede faltar en
        // un borrador —se para al emitir—, pero si viene tiene que ser un
        // número razonable: soles por dólar, con 3 decimales.
        var moneda = (p.Moneda ?? "PEN").ToUpperInvariant();
        if (moneda is not ("PEN" or "USD"))
            return "Por ahora se emite en soles (PEN) o en dólares (USD).";

        if (moneda != "PEN" && p.TipoCambio is { } tc &&
            (tc <= 0m || Math.Round(tc, 3, MidpointRounding.AwayFromZero) is <= 0m or >= 100m))
            return "El tipo de cambio son los soles que vale un dólar, por ejemplo 3.745.";

        if (string.IsNullOrWhiteSpace(p.Serie))
            return "Falta la serie. Es una de las que tiene dada de alta tu empresa en el servicio.";

        // LA SERIE TIENE QUE EMPEZAR POR LA LETRA DEL TIPO. Lo valida el
        // servicio también, pero su rechazo llega después de armar todo el
        // comprobante y el mensaje no menciona la serie.
        var letra = p.Tipo == "01" ? 'F' : 'B';
        if (char.ToUpperInvariant(p.Serie.Trim()[0]) != letra)
        {
            return p.Tipo == "01"
                ? "La serie de una factura empieza por F."
                : "La serie de una boleta empieza por B.";
        }

        // UNA FECHA FUTURA NO SE GUARDA NI EN BORRADOR: SUNAT no la acepta
        // nunca, así que no hay borrador que valga la pena con ella. La
        // anterior al plazo sí se deja GUARDAR —un borrador puede esperar— y
        // se para al EMITIR, con el mensaje de FechaFueraDePlazo.
        if (p.FechaEmision is { } fecha && fecha > HoyEnPeru())
            return "La fecha de emisión no puede ser posterior a hoy.";

        if (p.Lineas is null || p.Lineas.Count == 0)
            return "Un comprobante necesita al menos una línea.";

        if (p.Lineas.Count > 500)
            return "Un comprobante no puede llevar más de 500 líneas.";

        foreach (var (l, i) in p.Lineas.Select((l, i) => (l, i + 1)))
        {
            if (string.IsNullOrWhiteSpace(l.Descripcion))
                return $"A la línea {i} le falta la descripción.";

            if (l.Cantidad <= 0)
                return $"La cantidad de la línea {i} tiene que ser mayor que cero.";

            if (l.ValorUnitario < 0)
                return $"El valor unitario de la línea {i} no puede ser negativo.";

            if (l.Descuento < 0)
                return $"El descuento de la línea {i} no puede ser negativo.";

            if (l.Descuento > l.Cantidad * l.ValorUnitario)
                return $"El descuento de la línea {i} es mayor que la propia línea.";
        }

        // ══════════════════════════════════════════════════════════════════
        // EL IVAP ES EXCLUYENTE: si una línea lo lleva, todas.
        //
        // El arroz pilado no paga IGV sino un impuesto propio del 4% que lo
        // sustituye, y va en su propio comprobante (regla 2644 de SUNAT).
        //
        // Se comprueba aquí y no en el catálogo de productos porque es una
        // regla DEL COMPROBANTE: nada impide que una empresa venda arroz y
        // cemento, cada uno en su factura.
        // ══════════════════════════════════════════════════════════════════
        var conIvap = p.Lineas.Count(l => l.TipoAfectacion == CalculoPrevio.Ivap);
        if (conIvap > 0 && conIvap != p.Lineas.Count)
            return "El arroz pilado va en su propio comprobante: no se puede mezclar con otras líneas.";

        // Lo mismo con la exportación: todas las líneas o ninguna (error 2642).
        var conExportacion = p.Lineas.Count(l => l.TipoAfectacion == CalculoPrevio.Exportacion);
        if (conExportacion > 0 && conExportacion != p.Lineas.Count)
            return "En una factura de exportación todas las líneas tienen que ser de exportación.";

        if (!await db.Clientes.AnyAsync(c => c.Id == p.ClienteId, ct))
            return "Ese cliente no existe o está desactivado.";

        if (RevisarPago(p) is { } pagoMal)
            return pagoMal;

        return null;
    }


    // =========================================================================
    // 040 — CONTADO O CRÉDITO, Y DETRACCIÓN
    //
    // Lo mismo que pregunta «Emitir Factura» en SUNAT SOL. El servicio de
    // facturación vuelve a comprobarlo todo; esto da el mensaje antes y en
    // palabras de quien emite.
    // =========================================================================

    private static string FormaDe(PeticionDocumento p) =>
        (p.FormaPago ?? FormaDePago.Contado).Trim().ToLowerInvariant() switch
        {
            "credito" or "crédito" => FormaDePago.Credito,
            "contado" or ""        => FormaDePago.Contado,
            var otra               => otra
        };

    private static readonly System.Text.RegularExpressions.Regex CuentaBn =
        new(@"^[0-9][0-9-]{4,23}[0-9]$");

    private static readonly System.Text.RegularExpressions.Regex TresDigitos = new(@"^[0-9]{3}$");

    /// <summary>Lo que se comprueba sin el total: forma, cuotas y datos de la detracción.</summary>
    private static string? RevisarPago(PeticionDocumento p)
    {
        var forma = FormaDe(p);
        if (forma is not (FormaDePago.Contado or FormaDePago.Credito))
            return "La forma de pago es «Al contado» o «Al crédito».";

        var cuotas = p.Cuotas ?? [];
        var esFactura = p.Tipo == "01";

        if (!esFactura && forma == FormaDePago.Credito)
            return "Al crédito, con cuotas, solo se emiten facturas.";
        if (!esFactura && p.Detraccion is not null)
            return "La detracción solo va en facturas.";

        if (forma == FormaDePago.Contado && cuotas.Count > 0)
            return "Al contado no hay cuotas. Si se cobra en cuotas, es al crédito.";

        if (cuotas.Count > 60)
            return "Como mucho 60 cuotas.";

        var emision = p.FechaEmision ?? HoyEnPeru();
        foreach (var (c, i) in cuotas.Select((c, i) => (c, i + 1)))
        {
            if (c.Monto <= 0)
                return $"La cuota {i} tiene que ser mayor que cero.";
            if (c.Vence <= emision)
                return $"La cuota {i} vence el {c.Vence:dd/MM/yyyy}: tiene que vencer después de la fecha de emisión (SUNAT no acepta el mismo día).";
        }

        if (p.Detraccion is { } x)
        {
            if (!TresDigitos.IsMatch((x.Codigo ?? "").Trim()))
                return "Elige el bien o servicio sujeto a detracción (código de 3 dígitos del catálogo 54).";
            if (x.Porcentaje <= 0 || x.Porcentaje > 100)
                return "El porcentaje de detracción va entre 0 y 100 (por ejemplo 12).";
            if (!CuentaBn.IsMatch((x.Cuenta ?? "").Trim()))
                return "Falta la cuenta de detracciones del Banco de la Nación (solo números y guiones). La abre tu empresa, no el cliente.";
            if (!string.IsNullOrWhiteSpace(x.MedioPago) && !TresDigitos.IsMatch(x.MedioPago.Trim()))
                return "El medio de pago de la detracción es un código de 3 dígitos (001 = depósito en cuenta).";
        }

        return null;
    }

    /// <summary>Copia la forma de pago y la detracción al documento (ya revisadas).</summary>
    private static void CopiarPago(Documento d, PeticionDocumento p)
    {
        d.FormaPago = FormaDe(p);

        // Las cuotas, por fecha de vencimiento y numeradas desde 1 (Cuota001…).
        d.Cuotas = d.FormaPago == FormaDePago.Credito
            ? (p.Cuotas ?? [])
                .OrderBy(c => c.Vence)
                .Select((c, i) => new CuotaDocumento(i + 1, Math.Round(c.Monto, 2, MidpointRounding.AwayFromZero), c.Vence))
                .ToList()
            : [];

        d.Detraccion = d.Tipo == "01" && p.Detraccion is { } x
            ? new DetraccionDocumento(
                x.Codigo.Trim(),
                Math.Round(x.Porcentaje, 2, MidpointRounding.AwayFromZero),
                x.Cuenta.Trim(),
                string.IsNullOrWhiteSpace(x.MedioPago) ? "001" : x.MedioPago.Trim())
            : null;

        if (d.Detraccion is not null)
            d.Detraccion = d.Detraccion with { Monto = MontoDetraccion(d) };
    }

    /// <summary>
    /// El monto de la detracción: SIEMPRE EN SOLES y redondeado al entero, que
    /// es como lo deposita el Banco de la Nación. En dólares, con el tipo de
    /// cambio; sin él todavía no se sabe (se para al emitir).
    /// </summary>
    public static decimal? MontoDetraccion(Documento d)
    {
        if (d.Detraccion is not { } x) return null;

        decimal? totalSoles = d.Moneda == "PEN" ? d.ImporteTotal
                            : d.TipoCambio is { } tc ? d.ImporteTotal * tc
                            : null;

        return totalSoles is { } t
            ? Math.Round(t * x.Porcentaje / 100m, 0, MidpointRounding.AwayFromZero)
            : null;
    }

    /// <summary>Lo que necesita el total: que las cuotas no pasen del importe.</summary>
    private static string? RevisarPagoConTotal(Documento d)
    {
        if (d.Cuotas.Count == 0) return null;

        var suma = d.Cuotas.Sum(c => c.Monto);
        if (suma > d.ImporteTotal)
            return $"Las cuotas suman {suma:N2} y el total de la factura es {d.ImporteTotal:N2}. " +
                   "No pueden pasar del total (lo normal: total menos la detracción).";

        return null;
    }

    /// <summary>Al emitir: a crédito, al menos una cuota, y todas después de la fecha.</summary>
    private static string? RevisarPagoAlEmitir(Documento d)
    {
        if (d.Tipo != "01" || d.FormaPago != FormaDePago.Credito) return null;

        if (d.Cuotas.Count == 0)
            return "Una factura al crédito necesita al menos una cuota con su monto y su fecha de vencimiento (SUNAT la rechaza sin ella).";

        if (d.Cuotas.FirstOrDefault(c => c.Vence <= d.FechaEmision) is { } mal)
            return $"La cuota {mal.Numero} vence el {mal.Vence:dd/MM/yyyy}, y la factura es del {d.FechaEmision:dd/MM/yyyy}. " +
                   "Tiene que vencer después.";

        return RevisarPagoConTotal(d);
    }

    /// <summary>
    /// La cuenta, el código y el porcentaje que se acaban de usar quedan como
    /// los habituales de la empresa: la próxima factura los trae puestos.
    /// </summary>
    private static async Task RecordarDetraccionAsync(Documento d, ContextoPlataforma db, CancellationToken ct)
    {
        if (d.Detraccion is not { } x) return;

        var e = await db.Empresas.FirstOrDefaultAsync(e => e.Id == d.EmpresaId, ct);
        if (e is null) return;

        if (e.DetraccionCuenta != x.Cuenta)          e.DetraccionCuenta = x.Cuenta;
        if (e.DetraccionCodigo != x.Codigo)          e.DetraccionCodigo = x.Codigo;
        if (e.DetraccionPorcentaje != x.Porcentaje)  e.DetraccionPorcentaje = x.Porcentaje;
    }


    /// <summary>
    /// Hoy, en hora de Perú.
    ///
    /// NO `DateTime.Today`, que es la del servidor. Con el servidor en Alemania
    /// —que es la recomendación del documento de despliegue— a partir de las
    /// siete de la tarde hora de Lima ya es el día siguiente allí, y una
    /// factura fechada mañana la rechaza SUNAT.
    /// </summary>
    /// <summary>18 oct: cuerpo de POST .../{id}/anular.</summary>
    public sealed record PeticionAnular(string? Motivo);

    /// <summary>18 oct: cuerpo de POST /api/facturacion/series/ajustar.</summary>
    public sealed record PeticionAjusteSerie(string? TipoComprobante, string? Serie, int UltimoCorrelativo, string? Motivo);

    private static DateOnly HoyEnPeru()
    {
        try
        {
            var lima = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, lima));
        }
        catch (TimeZoneNotFoundException)
        {
            // SIN `tzdata` EN EL CONTENEDOR, lo de arriba lanza. No se deja
            // caer la creación del comprobante por eso: Perú está en UTC-5 todo
            // el año y no tiene horario de verano, así que restar cinco horas
            // da exactamente lo mismo.
            //
            // La resta es correcta HOY y lo seguirá siendo mientras Perú no
            // cambie de huso. El día que lo haga, esta línea miente — por eso
            // los Dockerfiles instalan `tzdata` y este camino no debería
            // usarse nunca.
            return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        }
    }
}