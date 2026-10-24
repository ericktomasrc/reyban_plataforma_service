using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// Lo que devuelve el servicio al aceptar un comprobante.
///
/// `Duplicado` es cierto cuando esta petición ya se había hecho antes con la
/// misma clave de idempotencia. NO ES UN ERROR: es el sistema funcionando, y
/// significa que un doble clic no emitió dos facturas.
///
/// Los comentarios van aquí arriba y no sobre cada parámetro: un `///` sobre
/// un parámetro posicional de un record es un CS1587, y este proyecto trata
/// los avisos como errores.
/// </summary>
public sealed record RespuestaComprobante(
    Guid Id,
    string Numero,
    string Estado,
    decimal ImporteTotal,
    string Moneda,
    bool Duplicado);


/// <summary>
/// Mandar un borrador al servicio de facturación.
///
/// ES LA OPERACIÓN MÁS DELICADA DE LA PLATAFORMA, por una razón que conviene
/// entender antes de tocar nada: **una vez que el servicio dice que sí, no hay
/// vuelta atrás**. Ese comprobante gastó un correlativo y forma parte de una
/// secuencia que SUNAT revisa. Corregirlo es emitir una nota de crédito, no
/// borrar una fila.
///
/// ══════════════════════════════════════════════════════════════════════════
/// EL ORDEN DE LOS PASOS NO ES INTERCAMBIABLE
///
///   1. Se marca el borrador como `encolado` Y SE GUARDA, antes de llamar.
///   2. Se llama al servicio.
///   3. Se guarda lo que contestó.
///
/// PARECE AL REVÉS Y NO LO ES. Si el paso 1 fuera después, este sería el
/// escenario: se llama, el servicio emite la factura, y justo entonces se cae
/// la luz. La factura existe en SUNAT y aquí sigue siendo un borrador; alguien
/// vuelve mañana, lo ve en borrador y lo emite otra vez.
///
/// Marcando antes, el peor caso es un documento que dice «encolado» sin
/// estarlo — feo, visible, y arreglable consultando al servicio. El otro peor
/// caso es una factura duplicada ante SUNAT.
///
/// Y LA CLAVE DE IDEMPOTENCIA CIERRA EL HUECO DEL TODO: nace con el borrador,
/// no al emitir. Si el mismo documento se manda dos veces, el servicio
/// reconoce la clave y devuelve el comprobante de la primera vez con
/// `duplicado: true`, en vez de emitir otro.
/// ══════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class ServicioEmision(
    ContextoPlataforma db,
    ContextoPeticion ctx,
    ClienteFacturacion facturacion,
    ILogger<ServicioEmision> log)
{
    public sealed record Resultado(bool Exito, string? Error, string? Detalle, Documento Documento);


    public async Task<Resultado> EmitirAsync(Documento d, CancellationToken ct)
    {
        if (d.Estado is not (EstadoDocumento.Borrador or EstadoDocumento.Error))
            return new Resultado(false, $"Este documento ya está {d.Estado}.", null, d);

        if (d.Lineas.Count == 0)
            return new Resultado(false, "Un comprobante necesita al menos una línea.", null, d);

        var cliente = await db.Clientes.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == d.ClienteId, ct);

        if (cliente is null)
            return new Resultado(false, "No se encuentra el cliente de este documento.", null, d);

        // UNA FACTURA EXIGE RUC. Se comprueba aquí y no solo en el servicio
        // porque el mensaje que se puede dar aquí es mejor: aquí se sabe que
        // existe la boleta como alternativa.
        if (d.Tipo == "01" && cliente.TipoDocumento != "6")
        {
            return new Resultado(false,
                "Una factura necesita un cliente con RUC.",
                "Para alguien sin RUC, lo que corresponde es una boleta.", d);
        }

        // LA FECHA DE EMISIÓN VIAJA TAL CUAL LA DEJÓ EL BORRADOR. La elige quien
        // emite —hoy por defecto, o anterior dentro del plazo de SUNAT—, y el
        // endpoint de emitir comprueba que siga en plazo ANTES de llegar aquí.
        // No se cambia en silencio: ver EndpointsDocumentos.FechaFueraDePlazo.

        // EL CÓDIGO DE PRODUCTO SUNAT DE CADA LÍNEA sale del producto, tal
        // como está AL EMITIR. Un borrador que falló y se reintenta después de
        // corregir el código del producto sale ya con el bueno. Una vez que el
        // servicio lo acepta, queda en su XML y no cambia más.
        var idsProducto = d.Lineas
            .Where(l => l.ProductoId is not null)
            .Select(l => l.ProductoId!.Value)
            .Distinct()
            .ToList();

        var codigosSunat = idsProducto.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Productos.IgnoreQueryFilters().AsNoTracking()
                .Where(p => idsProducto.Contains(p.Id) && p.CodigoSunat != null)
                .ToDictionaryAsync(p => p.Id, p => p.CodigoSunat!, ct);

        // 044: DESDE QUÉ SUCURSAL. Factura y boleta: la de su serie, revisada
        // otra vez al emitir (pudo desactivarse, o la serie cambiar de sucursal,
        // desde que se guardó el borrador). La nota: la que heredó del original.
        if (d.Tipo is "01" or "03")
        {
            var suc = await Sucursales.RevisarSerieAsync(db, ctx, d.EmpresaId, d.Tipo, d.Serie ?? "", ct);
            if (suc.Error is { } errorSucursal)
                return new Resultado(false, errorSucursal, null, d);
            d.SucursalId = suc.Id;
        }
        var establecimiento = await Sucursales.EstablecimientoAsync(db, d.SucursalId, ct);

        // 031: UNA NOTA DE CRÉDITO va a otra ruta y dice a qué comprobante afecta.
        var ruta = "/v1/comprobantes";
        object peticion;

        if (d.Tipo is "07" or "08")   // 22 oct: también la de débito
        {
            var original = d.DocumentoRefId is { } refId
                ? await db.Documentos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == refId, ct)
                : null;

            if (original?.Serie is null || original.Correlativo is null)
                return new Resultado(false, "No se encuentra el comprobante al que afecta esta nota.", null, d);

            ruta = d.Tipo == "08" ? "/v1/notas-debito" : "/v1/notas-credito";
            peticion = ArmarNota(d, original, cliente, codigosSunat, establecimiento);
        }
        else
        {
            peticion = Armar(d, cliente, codigosSunat, establecimiento);
        }

        // --- 1. Marcar ANTES de llamar -------------------------------------
        // 030: el SUNAT al que va: el de la empresa ahora (beta o producción).
        d.Ambiente = await db.Empresas.AsNoTracking()
            .Where(e => e.Id == d.EmpresaId)
            .Select(e => e.Ambiente)
            .FirstOrDefaultAsync(ct) == "produccion" ? "produccion" : "beta";
        d.Estado = EstadoDocumento.Encolado;
        d.MensajeSunat = null;
        d.CodigoSunat = null;
        await db.SaveChangesAsync(ct);

        // --- 2. Llamar -----------------------------------------------------
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var r = await facturacion.EnviarAsync<RespuestaComprobante>(
            ruta, peticion, d.IdempotencyKey, ct);

        reloj.Stop();

        await RegistrarLlamadaAsync(d, ruta, peticion, r, (int)reloj.ElapsedMilliseconds, ct);

        // --- 3. Guardar lo que contestó ------------------------------------
        if (!r.Exito || r.Datos is null)
        {
            // VUELVE A `error`, NO A `borrador`.
            //
            // Son dos cosas distintas y mezclarlas es peligroso: `borrador` es
            // «esto no se ha intentado»; `error` es «se intentó y no llegó».
            // El segundo merece que alguien lo mire antes de reintentar, por si
            // el servicio sí lo recibió y la respuesta se perdió por el camino.
            d.Estado = EstadoDocumento.Error;
            d.MensajeSunat = Recortar(r.Error);
            await db.SaveChangesAsync(ct);

            log.LogWarning("No se pudo emitir el documento {Id}: {Error}", d.Id, r.Error);

            return new Resultado(false, r.Error, r.Detalle, d);
        }

        var datos = r.Datos;

        d.ComprobanteId = datos.Id;
        d.Estado        = TraducirEstado(datos.Estado);

        // EL NÚMERO LO PONE EL SERVICIO. Llega como «F001-00000087» y se parte
        // para poder ordenar y buscar por correlativo. Si viniera con otra
        // forma se guarda entero en la serie y el correlativo queda nulo: es
        // mejor un dato raro que inventarse un número.
        var partes = datos.Numero.Split('-', 2);
        if (partes.Length == 2 && int.TryParse(partes[1], out var correlativo))
        {
            d.Serie = partes[0];
            d.Correlativo = correlativo;
        }
        else
        {
            d.Serie = datos.Numero;
            log.LogWarning("El servicio devolvió un número con forma inesperada: {Numero}", datos.Numero);
        }

        // ══════════════════════════════════════════════════════════════════
        // LA CIFRA DEL SERVICIO PISA LA DE AQUÍ.
        //
        // La de aquí era una estimación para la pantalla; la suya es la que se
        // firmó y viajó a SUNAT. Si difieren, la nuestra está mal — y conviene
        // saberlo, porque significa que la estimación no contempla algo de
        // este documento.
        // ══════════════════════════════════════════════════════════════════
        if (d.ImporteTotal != datos.ImporteTotal)
        {
            log.LogInformation(
                "El total del documento {Id} cambió al emitir: estimábamos {Nuestro}, " +
                "el servicio dice {Suyo}. Manda el suyo.",
                d.Id, d.ImporteTotal, datos.ImporteTotal);
        }

        d.ImporteTotal = datos.ImporteTotal;

        await db.SaveChangesAsync(ct);

        if (datos.Duplicado)
        {
            log.LogInformation(
                "El documento {Id} ya se había emitido con la misma clave de idempotencia. " +
                "El servicio devolvió {Numero} sin emitir otro.", d.Id, datos.Numero);
        }

        return new Resultado(true, null, null, d);
    }


    // =========================================================================

    /// <summary>
    /// Arma el cuerpo que espera el servicio.
    ///
    /// LO QUE NO SE MANDA, Y ES DELIBERADO:
    ///
    ///   · **El emisor.** Lo determina la clave de acceso. Que el cliente
    ///     declarara su propio RUC sería como dejar que el usuario diga quién
    ///     es en vez de autenticarse.
    ///
    ///   · **El correlativo.** Lo asigna el servicio, con candado. Mandarlo
    ///     abriría la puerta a duplicados y saltos de numeración.
    ///
    ///   · **`datosExtra`.** Van en el campo `extra`, que el servicio guarda y
    ///     devuelve pero NUNCA mete en el XML. Es la regla de oro número uno.
    /// </summary>
    private static object Armar(
        Documento d, Cliente cliente, IReadOnlyDictionary<Guid, string> codigosSunat, object? establecimiento) => new
    {
        tipo   = d.Tipo,

        // 044: el local desde el que se emite ({codigo, direccion}); null = la 0000.
        establecimiento,
        serie  = d.Serie,          // La serie SÍ: elige cuál de las suyas usar.
        moneda = d.Moneda,
        fechaEmision = d.FechaEmision.ToString("yyyy-MM-dd"),

        // 040: CONTADO O CRÉDITO, como lo escribe el servicio («Contado» /
        // «Credito»). A crédito, las cuotas: la suma es el monto pendiente.
        formaPago = d.FormaPago == FormaDePago.Credito ? "Credito" : "Contado",
        cuotas = d.Cuotas.Select(c => new
        {
            numero           = c.Numero,
            monto            = c.Monto,
            fechaVencimiento = c.Vence.ToString("yyyy-MM-dd")
        }),

        // 040: LA DETRACCIÓN. El tipo de operación sale del código: 027
        // transporte de carga (1004), 028 de pasajeros (1003), 004
        // hidrobiológicos (1002); lo demás, el general (1001). En soles el
        // monto lo calcula el servicio con su total exacto; en dólares va el
        // nuestro, ya convertido a soles (la cuenta del banco es en soles).
        detraccion = d.Tipo == "01" && d.Detraccion is { } x ? new
        {
            tipoOperacion      = x.Codigo switch { "027" => "1004", "028" => "1003", "004" => "1002", _ => "1001" },
            codigoBienServicio = x.Codigo,
            porcentaje         = x.Porcentaje,
            monto              = d.Moneda == "PEN" ? null : EndpointsDocumentos.MontoDetraccion(d),
            cuentaBancoNacion  = x.Cuenta,
            medioDePago        = x.MedioPago
        } : null,

        // EN MONEDA EXTRANJERA, EL TIPO DE CAMBIO (migración 026): a soles, con
        // la fecha de emisión. En soles no viaja: SUNAT no lo quiere.
        tipoCambio = d.Moneda == "PEN" || d.TipoCambio is null ? null : new
        {
            monedaOrigen  = d.Moneda,
            monedaDestino = "PEN",
            tasa          = d.TipoCambio.Value,
            fecha         = d.FechaEmision.ToString("yyyy-MM-dd")
        },

        receptor = new
        {
            tipoDocumento   = cliente.TipoDocumento,
            numeroDocumento = cliente.NumeroDocumento,
            razonSocial     = cliente.RazonSocial,
            direccion       = cliente.Direccion ?? "",

            // A DÓNDE MANDA EL SERVICIO EL COMPROBANTE cuando SUNAT lo
            // acepte: el PDF adjunto y el enlace al portal del receptor. El
            // correo del cliente es obligatorio en la plataforma justo por
            // esto. El servicio lo guarda aparte y nunca lo mete en el XML.
            correo          = cliente.Correo
        },

        lineas = d.Lineas.OrderBy(l => l.Numero).Select(l => new
        {
            codigoProducto   = l.Codigo ?? "",

            // El del catálogo 25 de SUNAT (ver arriba, EmitirAsync). Null si
            // el producto no lo tiene o la línea se escribió a mano.
            codigoProductoSunat = l.ProductoId is { } pid && codigosSunat.TryGetValue(pid, out var cs)
                ? cs
                : null,

            descripcion      = l.Descripcion,
            unidadMedida     = l.UnidadMedida,
            cantidad         = l.Cantidad,

            // SIN IGV Y SIN DESCUENTO, que es lo que el servicio espera.
            valorUnitario    = l.ValorUnitario,

            // EN PORCENTAJE, no en soles: así lo pide el contrato del servicio.
            // Guardamos el descuento en soles porque es como lo escribe la
            // gente, y se convierte aquí, en el único sitio que habla con él.
            descuentoPorcentaje = PorcentajeDescuento(l),

            tipoAfectacionIgv = l.TipoAfectacion
        }),

        extra = d.DatosExtra
    };


    /// <summary>
    /// 031: el cuerpo de una NOTA DE CRÉDITO para `POST /v1/notas-credito`.
    ///
    /// Lo mismo que un comprobante (receptor, líneas, moneda) más el motivo del
    /// catálogo 09, su explicación y el comprobante al que afecta. Los datos
    /// extra que empiezan por «_» son internos de la plataforma (qué cantidades
    /// devolvió) y NO viajan: el servicio imprime los extra en el PDF.
    /// </summary>
    private static object ArmarNota(
        Documento d, Documento original, Cliente cliente, IReadOnlyDictionary<Guid, string> codigosSunat,
        object? establecimiento) => new
    {
        serie  = d.Serie,
        establecimiento,   // 044
        moneda = d.Moneda,
        fechaEmision = d.FechaEmision.ToString("yyyy-MM-dd"),

        tipoCambio = d.Moneda == "PEN" || d.TipoCambio is null ? null : new
        {
            monedaOrigen  = d.Moneda,
            monedaDestino = "PEN",
            tasa          = d.TipoCambio.Value,
            fecha         = d.FechaEmision.ToString("yyyy-MM-dd")
        },

        codigoMotivo      = d.TipoNota,
        descripcionMotivo = d.MotivoNota ?? "",

        // 24 oct: la nota de crédito con motivo 13 lleva las cuotas nuevas.
        cuotas = d.Tipo == "07" && d.TipoNota == "13" && d.Cuotas.Count > 0
            ? d.Cuotas.OrderBy(c => c.Numero).Select(c => new
            {
                numero           = c.Numero,
                monto            = c.Monto,
                fechaVencimiento = c.Vence.ToString("yyyy-MM-dd")
            }).ToList()
            : null,

        afectado = new
        {
            tipoDocumento = original.Tipo,
            serie         = original.Serie,
            correlativo   = original.Correlativo
        },

        receptor = new
        {
            tipoDocumento   = cliente.TipoDocumento,
            numeroDocumento = cliente.NumeroDocumento,
            razonSocial     = cliente.RazonSocial,
            direccion       = cliente.Direccion ?? "",
            correo          = cliente.Correo
        },

        lineas = d.Lineas.Where(l => l.Activo).OrderBy(l => l.Numero).Select(l => new
        {
            codigoProducto      = l.Codigo ?? "",
            codigoProductoSunat = l.ProductoId is { } pid && codigosSunat.TryGetValue(pid, out var cs) ? cs : null,
            descripcion         = l.Descripcion,
            unidadMedida        = l.UnidadMedida,
            cantidad            = l.Cantidad,
            valorUnitario       = l.ValorUnitario,
            descuentoPorcentaje = PorcentajeDescuento(l),
            tipoAfectacionIgv   = l.TipoAfectacion
        }),

        extra = d.DatosExtra
            .Where(kv => !kv.Key.StartsWith('_'))
            .ToDictionary(kv => kv.Key, kv => kv.Value)
    };


    /// <summary>
    /// El descuento de una línea, en porcentaje del valor bruto.
    ///
    /// Se redondea a seis decimales: con dos, un descuento de 33.33 sobre 100
    /// se convertiría en 33.33% y de vuelta en 33.33 — pero uno de 1 sobre 3
    /// daría 33.33% y de vuelta 0.9999, un céntimo menos. Seis decimales dejan
    /// esa diferencia fuera de los dos decimales del importe.
    /// </summary>
    private static decimal PorcentajeDescuento(DocumentoLinea l)
    {
        var bruto = l.Cantidad * l.ValorUnitario;
        if (bruto <= 0 || l.Descuento <= 0) return 0;

        return Math.Round(l.Descuento / bruto * 100m, 6, MidpointRounding.AwayFromZero);
    }


    /// <summary>
    /// El estado del servicio, traducido al nuestro.
    ///
    /// SON DOS VOCABULARIOS DISTINTOS y conviene que sigan siéndolo: el día que
    /// el servicio añada un estado, esto falla de forma visible —se queda en
    /// `encolado` y se registra— en vez de escribir en nuestra columna un valor
    /// que su CHECK no admite.
    /// </summary>
    private string TraducirEstado(string? delServicio) => (delServicio ?? "").ToLowerInvariant() switch
    {
        "aceptado"   => EstadoDocumento.Aceptado,
        "observado"  => EstadoDocumento.Observado,

        // ASÍ LO ESCRIBE EL SERVICIO —«ACEPTADO_CON_OBSERVACIONES», en su CHECK
        // y en sus avisos—, no «observado». Sin esta línea, reemitir un
        // documento que el servicio ya tenía resuelto con observaciones lo
        // dejaba aquí como «encolado», y ningún aviso iba a venir a sacarlo:
        // el servicio ya lo había mandado y el receptor lo había descartado
        // por no encontrar el documento todavía.
        "aceptado_con_observaciones" => EstadoDocumento.Observado,
        "rechazado"  => EstadoDocumento.Rechazado,
        "anulado"    => EstadoDocumento.Anulado,

        // Lo normal: el servicio responde 202 y todavía no habló con SUNAT.
        "borrador" or "firmado" or "enviado" or "encolado" or "pendiente" or "" => EstadoDocumento.Encolado,

        _ => Desconocido(delServicio)
    };

    private string Desconocido(string? estado)
    {
        log.LogWarning("El servicio devolvió un estado que no conocemos: «{Estado}». " +
                       "Se deja como encolado.", estado);
        return EstadoDocumento.Encolado;
    }


    /// <summary>
    /// Deja constancia de la llamada, salga bien o mal.
    ///
    /// ES LO QUE CONTESTA «¿QUÉ LE MANDAMOS EXACTAMENTE?» seis meses después,
    /// cuando un cliente diga que su factura salió mal. Sin esto hay que
    /// reconstruirlo de memoria, que es como decir que no se puede.
    ///
    /// SE GUARDA LA PETICIÓN ENTERA. No lleva nada secreto —la clave va en una
    /// cabecera, no en el cuerpo— y recortarla a «lo que parecía importante» es
    /// justo lo que deja fuera el campo que resultó ser el problema.
    ///
    /// NUNCA TUMBA LA EMISIÓN: si falla el registro, se anota en el log y se
    /// sigue. Es la lección de la columna `accion` demasiado corta del
    /// documento maestro — la bitácora no puede romper lo que registra.
    /// </summary>
    private async Task RegistrarLlamadaAsync<T>(
        Documento d, string ruta, object peticion, ResultadoFacturacion<T> r, int ms, CancellationToken ct)
    {
        try
        {
            var cuerpo = JsonSerializer.Serialize(peticion);

            var respuesta = r.Exito
                ? JsonSerializer.Serialize(r.Datos)
                : JsonSerializer.Serialize(new { r.Error, r.Detalle });

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO llamadas_facturacion
                    (empresa_id, documento_id, metodo, ruta, idempotency_key,
                     codigo_http, duracion_ms, peticion, respuesta, error, usuario_id)
                VALUES
                    ({d.EmpresaId}, {d.Id}, 'POST', {ruta}, {d.IdempotencyKey},
                     {r.Estado}, {ms}, {cuerpo}::jsonb, {respuesta}::jsonb,
                     {r.Error}, {ctx.AutorId})
                """, ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "No se pudo registrar la llamada del documento {Id}. " +
                            "La emisión NO se ve afectada.", d.Id);
        }
    }

    private static string? Recortar(string? texto, int largo = 400) =>
        texto is null ? null : texto.Length <= largo ? texto : texto[..largo] + "…";
}