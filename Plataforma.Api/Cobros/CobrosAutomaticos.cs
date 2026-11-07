using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Cobros;

/// <summary>
/// 076 — LO QUE LOS COBROS HACEN SOLOS, en una tarea de fondo de la API.
///
///   Cada 5 minutos   el comprobante de cada pago: «Mi empresa» le emite al
///                    cliente factura (RUC) o boleta (DNI). Si el servicio no
///                    responde, reintenta con espera creciente (hasta 10 veces).
///   Cada hora        el cobro del mes a la tarjeta principal (un intento por
///                    día: el día del cobro y los de reintento), el estado de
///                    cada cuenta (al día, reintento, gracia, no emite) y los
///                    avisos por correo (cada uno una sola vez).
///
/// NUNCA TUMBA LA API: cada parte va en su try. Trabaja como la plataforma
/// (contexto de super administrador), porque no hay ninguna persona detrás.
///
/// EL MONTO LO CALCULA LA BASE (cobros_monto), igual que cuando paga una
/// persona. Y LA EMPRESA QUE EMITE ES SIEMPRE LA DE «MI EMPRESA».
/// </summary>
public sealed class CobrosAutomaticos(IServiceScopeFactory ambitos, ILogger<CobrosAutomaticos> log) : BackgroundService
{
    private const string Agente = "cobros automáticos";
    private static readonly TimeSpan Ciclo = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CicloLargo = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            var ultimoLargo = DateTime.MinValue;
            using var reloj = new PeriodicTimer(Ciclo);
            do
            {
                await Paso("comprobantes", EmitirComprobantesAsync, ct);
                if (DateTime.UtcNow - ultimoLargo >= CicloLargo)
                {
                    ultimoLargo = DateTime.UtcNow;
                    await Paso("cobros del mes", CobrarDelMesAsync, ct);
                    await Paso("estados de pago", EstadosAsync, ct);
                    await Paso("avisos por correo", AvisosAsync, ct);
                }
            }
            while (await reloj.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task Paso(string nombre, Func<CancellationToken, Task> f, CancellationToken ct)
    {
        try { await f(ct); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogError(e, "Cobros automáticos: falló «{Paso}». Se reintenta en la siguiente vuelta.", nombre);
        }
    }

    /// <summary>Un ámbito con su transacción y el contexto de la plataforma.</summary>
    private async Task<(AsyncServiceScope Ambito, ContextoPlataforma Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx)>
        AbrirAsync(CancellationToken ct)
    {
        var ambito = ambitos.CreateAsyncScope();
        var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();
        var tx = await db.Database.BeginTransactionAsync(ct);
        await db.FijarContextoAsync(null, null, null, Agente, true, ct);
        return (ambito, db, tx);
    }

    // =========================================================================
    // 1. EL COMPROBANTE DE CADA PAGO
    // =========================================================================
    private async Task EmitirComprobantesAsync(CancellationToken ct)
    {
        List<Guid> pendientes;
        {
            var (ambito, db, tx) = await AbrirAsync(ct);
            await using (ambito) await using (tx)
                pendientes = await db.Database.SqlQuery<Guid>($"SELECT p AS \"Value\" FROM cobros_comprobantes_pendientes() p").ToListAsync(ct);
        }
        foreach (var id in pendientes)
        {
            try { await EmitirUnoAsync(id, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "No se pudo emitir el comprobante del pago {Cobro}.", id);
            }
        }
    }

    private async Task EmitirUnoAsync(Guid id, CancellationToken ct)
    {
        var (ambito, db, tx) = await AbrirAsync(ct);
        await using var _a = ambito;
        await using var _t = tx;
        var facturacion = ambito.ServiceProvider.GetRequiredService<ClienteFacturacion>();

        var texto = await db.Database.SqlQuery<string>($"SELECT cobros_para_emitir({id})::text AS \"Value\"").FirstOrDefaultAsync(ct);
        if (texto is null) return;   // todavía no se puede (falta la serie o los datos): sigue pendiente
        var p = JsonDocument.Parse(texto).RootElement;
        var empresa = p.GetProperty("empresa").GetGuid();
        var monto = p.GetProperty("monto").GetDecimal();
        var serie = p.GetProperty("serie").GetString()!;

        var cuerpo = new
        {
            tipo = p.GetProperty("tipo").GetString(),
            establecimiento = (object?)null,
            serie,
            moneda = "PEN",
            fechaEmision = p.GetProperty("fecha").GetString(),
            formaPago = "Contado",
            cuotas = Array.Empty<object>(),
            detraccion = (object?)null,
            tipoCambio = (object?)null,
            receptor = JsonSerializer.Deserialize<JsonElement>(p.GetProperty("receptor").GetRawText()),
            lineas = new[]
            {
                new
                {
                    codigoProducto = p.GetProperty("codigo").GetString(),
                    codigoProductoSunat = (string?)null,
                    descripcion = p.GetProperty("concepto").GetString(),
                    unidadMedida = "ZZ",        // servicio
                    cantidad = 1m,
                    valorUnitario = CalculoPrevio.QuitarIgv(monto),
                    precioUnitario = (decimal?)monto,
                    descuentoPorcentaje = 0m,
                    tipoAfectacionIgv = "10"   // gravado (precios con IGV)
                }
            },
            extra = (object?)null
        };

        // La misma clave en cada intento: si el servicio ya lo emitió, devuelve
        // el mismo comprobante (duplicado) en vez de gastar otro número.
        // La credencial del servicio solo se lee con el contexto de su empresa
        // (aislamiento): se pone para la llamada y se vuelve al de la plataforma.
        await db.FijarContextoAsync(null, empresa, null, Agente, false, ct);
        var r = await facturacion.EmitirComprobanteCobroAsync<RespuestaComprobante>(empresa, cuerpo, "cobro-" + id, ct);
        await db.FijarContextoAsync(null, null, null, Agente, true, ct);

        if (r.Exito && r.Datos is { } c)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT cobros_comprobante_guardar({id}, true, {c.Id}, {c.Numero}, {c.Estado}, NULL::text, {empresa}, {serie})
                """, ct);
            log.LogInformation("Comprobante {Numero} emitido para el pago {Cobro}.", c.Numero, id);
        }
        else
        {
            var error = string.Join(" ", new[] { r.Error, r.Detalle }.Where(x => !string.IsNullOrWhiteSpace(x)));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT cobros_comprobante_guardar({id}, false, NULL::uuid, NULL::text, NULL::text, {error}, NULL::uuid, NULL::text)
                """, ct);
            log.LogWarning("El servicio no emitió el comprobante del pago {Cobro}: {Error}", id, error);
        }
        await tx.CommitAsync(ct);
    }

    // =========================================================================
    // 2. EL COBRO DEL MES
    // =========================================================================
    private sealed record PorCobrar(Guid Cuenta, Guid Tarjeta, string Periodo, int Dias);

    private async Task CobrarDelMesAsync(CancellationToken ct)
    {
        List<PorCobrar> lista;
        EstadoModo modo;
        {
            var (ambito, db, tx) = await AbrirAsync(ct);
            await using (ambito) await using (tx)
            {
                var llaves = ambito.ServiceProvider.GetRequiredService<OpcionesCulqi>();
                var sunat = ambito.ServiceProvider.GetRequiredService<OpcionesConexionSunat>();
                var guardado = await db.Database.SqlQuery<string>($"SELECT cobros_modo_guardado() AS \"Value\"").FirstAsync(ct);
                modo = EstadoModo.Calcular(guardado, llaves, sunat.Fija);
                if (!modo.ConTarjeta) return;
                var texto = await db.Database.SqlQuery<string>($"SELECT cobros_por_cobrar({modo.Modo})::text AS \"Value\"").FirstAsync(ct);
                lista = JsonSerializer.Deserialize<List<PorCobrar>>(texto, Json) ?? [];
            }
        }
        foreach (var x in lista)
        {
            try { await CobrarUnaAsync(x, modo, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "No se pudo cobrar a la cuenta {Cuenta}.", x.Cuenta);
            }
        }
    }

    private async Task CobrarUnaAsync(PorCobrar x, EstadoModo modo, CancellationToken ct)
    {
        var (ambito, db, tx) = await AbrirAsync(ct);
        await using var _a = ambito;
        await using var _t = tx;
        var culqi = ambito.ServiceProvider.GetRequiredService<ClienteCulqi>();

        var prep = JsonDocument.Parse(await db.Database.SqlQuery<string>($"""
            SELECT cobros_preparar_auto({x.Cuenta}, {modo.Modo}, {x.Periodo}, {x.Tarjeta})::text AS "Value"
            """).FirstAsync(ct)).RootElement;
        if (prep.GetProperty("nada").GetBoolean()) return;

        var cobro = prep.GetProperty("id").GetGuid();
        var monto = prep.GetProperty("monto").GetDecimal();
        var tarjeta = prep.GetProperty("tarjeta").GetString()!;
        // El «procesando» se guarda ANTES de cobrar: si la API se cae a la mitad,
        // el aviso de Culqi lo encuentra por el cobro_id de la metadata.
        await tx.CommitAsync(ct);

        var r = modo.Modo == "simulado"
            ? PasarelaSimulada.Cobrar(tarjeta)
            : await culqi.CobrarAsync(tarjeta, prep.GetProperty("correo").GetString() ?? "", monto,
                                      prep.GetProperty("concepto").GetString()!, cobro, x.Cuenta, ct);
        if (r.SinRespuesta) return;   // queda «procesando»; el aviso de Culqi o los 10 minutos lo resuelven
        if (r.Requiere3ds)
            r = r with { Ok = false, Codigo = "3ds", Mensaje = "Tu banco pidió confirmar el pago. Vuelve a agregar tu tarjeta en «Mis pagos»." };

        await using var tx2 = await db.Database.BeginTransactionAsync(ct);
        await db.FijarContextoAsync(null, null, null, Agente, true, ct);
        await db.Database.SqlQuery<string>($"""
            SELECT cobros_resultado({cobro}, {r.Ok}, {r.Id}::text, {r.Codigo}::text, {r.Mensaje}::text)::text AS "Value"
            """).FirstAsync(ct);
        await tx2.CommitAsync(ct);
        log.LogInformation("Cobro del mes de la cuenta {Cuenta} ({Modo}): {Resultado}.", x.Cuenta, modo.Modo, r.Ok ? "pagado" : r.Codigo);
    }

    // =========================================================================
    // 3. EL ESTADO DE PAGO DE CADA CUENTA (y sus correos de gracia y bloqueo)
    // =========================================================================
    private sealed record Cambio(Guid Cuenta, string? De, string A, DateOnly? Fecha, DateOnly? Gracia);

    private async Task EstadosAsync(CancellationToken ct)
    {
        List<Cambio> cambios;
        {
            var (ambito, db, tx) = await AbrirAsync(ct);
            await using (ambito) await using (tx)
            {
                var texto = await db.Database.SqlQuery<string>($"SELECT cobros_estados_actualizar()::text AS \"Value\"").FirstAsync(ct);
                cambios = JsonSerializer.Deserialize<List<Cambio>>(texto, Json) ?? [];
                await tx.CommitAsync(ct);
            }
        }
        foreach (var c in cambios.Where(c => c.A is "gracia" or "no_emite"))
        {
            var m = c.A == "gracia" ? PlantillasCobros.Gracia(c.Gracia ?? DateOnly.FromDateTime(DateTime.Today)) : PlantillasCobros.NoEmite();
            await AvisarAsync(c.Cuenta, c.A, (c.Fecha?.ToString("yyyy-MM-dd") ?? "") + ":" + c.A, m, ct);
        }
    }

    // =========================================================================
    // 4. LOS AVISOS POR CORREO (el cobro que viene, el pago, la tarjeta que no pasó)
    // =========================================================================
    private async Task AvisosAsync(CancellationToken ct)
    {
        List<JsonElement> avisos;
        {
            var (ambito, db, tx) = await AbrirAsync(ct);
            await using (ambito) await using (tx)
                avisos = JsonSerializer.Deserialize<List<JsonElement>>(
                    await db.Database.SqlQuery<string>($"SELECT cobros_avisos_pendientes()::text AS \"Value\"").FirstAsync(ct), Json) ?? [];
        }
        foreach (var a in avisos)
        {
            var tipo = a.GetProperty("tipo").GetString()!;
            var cuenta = a.GetProperty("cuenta").GetGuid();
            var clave = a.GetProperty("clave").GetString()!;
            var monto = a.TryGetProperty("monto", out var mm) && mm.ValueKind == JsonValueKind.Number ? mm.GetDecimal() : 0m;
            CorreoCompuesto m = tipo switch
            {
                "previo" => PlantillasCobros.Previo(DateOnly.Parse(a.GetProperty("fecha").GetString()!), monto, a.GetProperty("conTarjeta").GetBoolean()),
                "pagado" => PlantillasCobros.Pagado(monto, a.GetProperty("concepto").GetString() ?? "tu plan",
                                                    a.TryGetProperty("hasta", out var h) && h.ValueKind == JsonValueKind.String ? DateOnly.Parse(h.GetString()!) : null,
                                                    a.GetProperty("modo").GetString() is "simulado" or "prueba"),
                _ => PlantillasCobros.Rechazado(monto, a.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null,
                                                a.TryGetProperty("reintentos", out var re) ? re.EnumerateArray().Select(z => z.GetInt32()).ToArray() : []),
            };
            await AvisarAsync(cuenta, tipo, clave, m, ct);
        }
    }

    /// <summary>Manda un aviso y lo anota (una sola vez). Si el correo no sale, no se anota y se reintenta.</summary>
    private async Task AvisarAsync(Guid cuenta, string tipo, string clave, CorreoCompuesto m, CancellationToken ct)
    {
        var (ambito, db, tx) = await AbrirAsync(ct);
        await using var _a = ambito;
        await using var _t = tx;
        var correo = ambito.ServiceProvider.GetRequiredService<IServicioCorreo>();
        var destino = await db.Database.SqlQuery<string?>($"SELECT cobros_correo_cuenta({cuenta}) AS \"Value\"").FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(destino)) return;

        if (!await db.Database.SqlQuery<bool>($"SELECT cobros_aviso_anotar({cuenta}, {tipo}, {clave}, {destino}) AS \"Value\"").FirstAsync(ct))
            return;   // ya salió antes

        bool ok;
        try { ok = await correo.EnviarAsync(destino, m.Asunto, m.Html, m.Texto, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Falló el aviso de cobro «{Tipo}» de la cuenta {Cuenta}.", tipo, cuenta);
            ok = false;
        }
        if (ok) await tx.CommitAsync(ct);   // si no salió, se deshace la anotación y se reintenta en una hora
    }
}