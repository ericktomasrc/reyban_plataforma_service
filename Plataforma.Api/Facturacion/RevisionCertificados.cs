using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Arranque;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// Guarda lo que dijo el servicio sobre el certificado de una empresa.
///
/// UN SOLO SITIO PARA ESCRIBIRLO, y lo usan dos: «Probar conexión» y la
/// revisión diaria. Si cada uno armara su UPDATE, el día que cambie la regla
/// uno de los dos se quedaría atrás.
///
/// POR SQL Y NO POR EF, igual que `AnotarLlamadaAsync`: es telemetría, no tiene
/// que arrastrar otros cambios pendientes del contexto ni chocar con la
/// comprobación de versión de alguien que esté guardando la credencial a la
/// vez.
///
/// Escribe en la credencial ACTIVA de esa empresa. El aislamiento decide si se
/// puede: con el contexto de la empresa o de un super administrador, sí; con
/// cualquier otro, no se actualiza ninguna fila.
/// </summary>
public static class CertificadoDelEmisor
{
    public static Task GuardarAsync(
        ContextoPlataforma db, Guid empresaId, DateTimeOffset? validoHasta, CancellationToken ct)
    {
        DateTime? fecha = validoHasta?.UtcDateTime;

        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE credenciales_facturacion
               SET certificado_valido_hasta  = {fecha},
                   certificado_consultado_en = now()
             WHERE empresa_id = {empresaId}
               AND activo
            """, ct);
    }

    /// <summary>
    /// Días que le quedan, como los cuenta el servicio: hacia arriba. Con
    /// medio día por delante quedan 1, no 0 — el 0 es «vence hoy».
    /// </summary>
    public static int? DiasParaVencer(DateTime? validoHasta) =>
        validoHasta is { } h
            ? (int)Math.Ceiling((DateTime.SpecifyKind(h, DateTimeKind.Utc) - DateTime.UtcNow).TotalDays)
            : null;
}


/// <summary>
/// Los datos oficiales de la empresa —razón social y ambiente— tal como los
/// dice el servicio. Migración 014.
///
/// UN SOLO SITIO PARA ESCRIBIRLOS, por lo mismo que el certificado: los usan
/// «Probar conexión» (que la pantalla llama «Volver a preguntar al servicio»)
/// y la revisión diaria. «Conectar» los escribe con EF, junto al RUC, porque
/// es el único que puede poner el RUC por primera vez.
///
/// SOLO SI EL RUC COINCIDE, y lo comprueba la propia sentencia (`AND ruc =`):
/// con la clave de otro cliente no se toca nada. Una empresa sin RUC —no
/// conectada— tampoco: el RUC solo lo pone «Conectar».
///
/// Un ambiente que no sea `beta` o `produccion`, o una razón social vacía, no
/// pisan lo que había.
/// </summary>
public static class DatosOficiales
{
    public static Task<int> RefrescarAsync(
        ContextoPlataforma db, Guid empresaId,
        EndpointsCredenciales.EmisorDelServicio emisor, CancellationToken ct)
    {
        var ruc = emisor.Ruc?.Trim();
        if (string.IsNullOrEmpty(ruc)) return Task.FromResult(0);

        var razon = string.IsNullOrWhiteSpace(emisor.RazonSocial) ? null : emisor.RazonSocial.Trim();
        var ambiente = Ambiente(emisor.Ambiente);

        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE empresas
               SET razon_social       = COALESCE({razon}::text, razon_social),
                   ambiente           = COALESCE({ambiente}::text, ambiente),
                   datos_oficiales_en = now()
             WHERE id  = {empresaId}
               AND ruc = {ruc}
            """, ct);
    }

    /// <summary>`beta` o `produccion`, o null si el servicio dice otra cosa.</summary>
    public static string? Ambiente(string? delServicio) =>
        delServicio?.Trim().ToLowerInvariant() switch
        {
            "beta"       => "beta",
            "produccion" => "produccion",
            _            => null
        };
}


/// <summary>
/// Lo que dijo el servicio del webhook de la empresa (`GET /v1/webhook`): a
/// qué URL manda los avisos. Migración 014.
///
/// Si no es la de la plataforma, alguien con la clave la cambió, y los avisos
/// de SUNAT dejaron de llegar aquí (diseño §2.4). La pantalla lo dice en rojo
/// comparando con <see cref="EndpointsCredenciales.UrlWebhook"/>.
/// </summary>
public static class WebhookDelEmisor
{
    public static Task GuardarAsync(
        ContextoPlataforma db, Guid empresaId, string? url, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE credenciales_facturacion
               SET webhook_url_servicio = {url},
                   webhook_revisado_en  = now()
             WHERE empresa_id = {empresaId}
               AND activo
            """, ct);
}


/// <summary>
/// Pregunta al servicio, una vez al día por empresa, hasta cuándo vale su
/// certificado digital, y lo guarda.
///
/// DESDE LA 014 PREGUNTA TAMBIÉN LO DEMÁS, con las mismas llamadas: la razón
/// social y el ambiente (por si cambiaron en el servicio) y, con
/// `GET /v1/webhook`, si el servicio sigue mandando los avisos a la
/// plataforma. Se sigue llamando así para no cambiar de archivo; el diseño la
/// llama «revisión diaria».
///
/// ══════════════════════════════════════════════════════════════════════════
/// POR QUÉ HACE FALTA
///
/// El certificado firma cada XML. El día que vence, la empresa deja de poder
/// emitir de golpe, y lo descubre quien intenta facturar. Comprarlo y
/// renovarlo lleva días. Esto es lo que permite avisar con un mes de margen
/// (pasos 6.2 y 6.3: la franja en pantalla y el correo).
/// ══════════════════════════════════════════════════════════════════════════
///
/// CÓMO, Y POR QUÉ ASÍ:
///
///   · **Se despierta cada hora, pero pregunta una vez al día.** Para cada
///     empresa mira cuándo preguntó por última vez y, si fue hace menos de
///     20 horas, la salta. Así un reinicio no espera un día entero, dos
///     copias de la API no duplican el trabajo más que una hora, y una
///     empresa recién configurada se consulta en la siguiente vuelta.
///
///   · **Cada empresa, en su transacción y con SU contexto**, sin usuario:
///     igual que el receptor del webhook. Para saber qué empresas hay usa la
///     puerta `empresas_con_facturacion()` (migración 013), que solo devuelve
///     ids. Lo demás lo lee el aislamiento normal, que no deja ver la
///     credencial de ninguna otra.
///
///   · **Solo lee del servicio** (`GET /v1/emisor`). Y si el RUC que contesta
///     no es el de la empresa, no guarda nada: sería el certificado de otro.
///
///   · **Una empresa que falla no para a las demás.** Su fallo queda donde ya
///     quedan los de la clave —`ultimo_fallo` de la credencial, que enseña la
///     pantalla de facturación— y en el log.
///
/// DESDE LA 015, ADEMÁS: guarda lo que dice el servicio del emisor (alta,
/// portal, dirección, qué falta) y confirma un alta que se quedó «sin
/// confirmar» porque la pantalla se cerró antes de tiempo.
///
/// DESDE LA 025, ADEMÁS: en cada vuelta manda los correos del certificado por
/// vencer que toquen (<see cref="AvisosCertificado"/>).
///
/// DE PASO, ES UNA PRUEBA DIARIA DE CADA CLAVE: una que dejó de valer se ve
/// en «Fallando» al día siguiente, no el día que alguien intenta emitir.
/// </summary>
public sealed class RevisionCertificados(
    IServiceScopeFactory ambitos,
    ILogger<RevisionCertificados> log) : BackgroundService
{
    /// <summary>Cada cuánto se despierta.</summary>
    private static readonly TimeSpan Ciclo = TimeSpan.FromHours(1);

    /// <summary>Una consulta más reciente que esto no se repite.</summary>
    private static readonly TimeSpan Vigencia = TimeSpan.FromHours(20);

    /// <summary>
    /// La primera vuelta no sale al arrancar: se deja un minuto para que la
    /// API termine de levantarse y no compita con las primeras peticiones.
    /// </summary>
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromMinutes(1);

    /// <summary>Desde cuántos días se escribe en el log como aviso.</summary>
    private const int DiasDeAviso = 30;

    private const string Agente = "revisión diaria del certificado";


    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(EsperaInicial, ct);

            using var reloj = new PeriodicTimer(Ciclo);

            do
            {
                try
                {
                    await RevisarTodasAsync(ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    // NUNCA TUMBA LA API. Un BackgroundService que lanza para
                    // el proceso entero en .NET 8+; la revisión es un extra y
                    // no puede llevarse la facturación por delante.
                    log.LogError(e, "La revisión de certificados falló. Se reintenta en {Ciclo}.", Ciclo);
                }

                // PASO 6.3 (migración 025): los correos del certificado por
                // vencer, con las fechas recién actualizadas. Aparte, para que
                // un fallo de uno no se lleve al otro.
                try
                {
                    await AvisosCertificado.EnviarPendientesAsync(ambitos, log, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogError(e, "Los correos del certificado por vencer fallaron. Se reintenta en {Ciclo}.", Ciclo);
                }
            }
            while (await reloj.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // La API se está apagando. Nada que hacer.
        }
    }


    private async Task RevisarTodasAsync(CancellationToken ct)
    {
        List<Guid> empresas;

        await using (var ambito = ambitos.CreateAsyncScope())
        {
            var db = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();

            empresas = await db.Database
                .SqlQuery<Guid>($"SELECT f.empresa_id AS \"Value\" FROM empresas_con_facturacion() f")
                .ToListAsync(ct);
        }

        if (empresas.Count == 0) return;

        var consultadas = 0;

        foreach (var empresaId in empresas)
        {
            try
            {
                if (await RevisarAsync(empresaId, ct)) consultadas++;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "No se pudo revisar el certificado de la empresa {Empresa}.", empresaId);
            }
        }

        if (consultadas > 0)
            log.LogInformation(
                "Revisión de certificados: {Consultadas} de {Total} empresas consultadas al servicio.",
                consultadas, empresas.Count);
    }


    /// <summary>
    /// Revisa una empresa. Devuelve si llegó a preguntar al servicio (falso si
    /// la consulta anterior todavía vale).
    /// </summary>
    private async Task<bool> RevisarAsync(Guid empresaId, CancellationToken ct)
    {
        // UN ÁMBITO POR EMPRESA: su propio ContextoPlataforma, su propia
        // conexión y su propia transacción. Lo que falle en una no deja nada
        // a medias en la siguiente.
        await using var ambito = ambitos.CreateAsyncScope();
        var db          = ambito.ServiceProvider.GetRequiredService<ContextoPlataforma>();
        var facturacion = ambito.ServiceProvider.GetRequiredService<ClienteFacturacion>();
        var urlApi      = ambito.ServiceProvider.GetRequiredService<UrlApi>();

        // EL CONTEXTO SOLO VIVE DENTRO DE UNA TRANSACCIÓN (ver
        // ContextoPlataforma.FijarContextoAsync). Fuera de ella, el aislamiento
        // devolvería cero filas sin dar ningún error.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        await db.FijarContextoAsync(null, empresaId, null, Agente, false, ct);

        var credencial = await db.CredencialesFacturacion
            .AsNoTracking()
            .Where(c => c.EmpresaId == empresaId)
            .Select(c => new { c.CertificadoConsultadoEn })
            .FirstOrDefaultAsync(ct);

        if (credencial is null) return false;

        if (credencial.CertificadoConsultadoEn is { } antes &&
            DateTime.UtcNow - DateTime.SpecifyKind(antes, DateTimeKind.Utc) < Vigencia)
        {
            return false;
        }

        // `LeerComoEmpresaAsync` es la puerta de solo lectura del cliente. Ver
        // allí sus candados: aquí se cumplen porque el contexto es el de esta
        // empresa y solo se hace GET.
        var r = await facturacion.LeerComoEmpresaAsync<EndpointsCredenciales.EmisorDelServicio>(
            empresaId, "/v1/emisor", ct);

        if (!r.Exito || r.Datos is null)
        {
            // El cliente ya anotó el fallo en la credencial. Se confirma para
            // que esa anotación quede: es lo que enseña «Fallando» en la
            // pantalla de facturación.
            await transaccion.CommitAsync(ct);

            log.LogWarning("No se pudo consultar el certificado de la empresa {Empresa}: {Error}",
                           empresaId, r.Error);
            return true;
        }

        var ruc = await db.Empresas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.Id == empresaId)
            .Select(e => e.Ruc)
            .FirstOrDefaultAsync(ct);

        if (ruc is null)
        {
            // Tiene clave (puesta a mano) pero nunca se conectó: no hay RUC
            // con el que comparar, así que no se guarda nada de otro. La
            // pantalla dice «sin conectar».
            await transaccion.CommitAsync(ct);

            log.LogInformation(
                "La empresa {Empresa} tiene clave pero no está conectada (sin RUC). No se guarda nada.",
                empresaId);
            return true;
        }

        if (!string.Equals(r.Datos.Ruc?.Trim(), ruc.Trim(), StringComparison.Ordinal))
        {
            // La clave es de otro cliente. No se guarda una fecha que no es
            // suya; «Probar conexión» lo dice en rojo.
            await transaccion.CommitAsync(ct);

            log.LogWarning(
                "La clave de la empresa {Empresa} (RUC {Ruc}) responde con el RUC {Otro}. " +
                "No se guarda su certificado.", empresaId, ruc, r.Datos.Ruc);
            return true;
        }

        var emisor = r.Datos;

        // UN ALTA QUE SE QUEDÓ SIN CONFIRMAR (diseño §13): la empresa existe
        // aquí y la clave es suya —el RUC acaba de coincidir—, así que se
        // confirma ahora. Ruta fija, no emite. Si falla, mañana otra vez.
        if (emisor.Alta?.Estado == "sin_confirmar")
        {
            var estado = await EstadoDelEmisor.ConfirmarAsync(db, facturacion, empresaId, ct);

            if (estado == "confirmada")
            {
                log.LogInformation("Alta de la empresa {Empresa} (RUC {Ruc}) confirmada en la revisión diaria.",
                                   empresaId, ruc);

                var otra = await facturacion.LeerComoEmpresaAsync<EndpointsCredenciales.EmisorDelServicio>(
                    empresaId, "/v1/emisor", ct);
                if (otra.Exito && otra.Datos is { } nuevo) emisor = nuevo;
            }
            else
            {
                log.LogWarning("El alta de la empresa {Empresa} (RUC {Ruc}) sigue sin confirmar en el servicio ({Estado}).",
                               empresaId, ruc, estado ?? "no se pudo preguntar");
            }
        }

        await CertificadoDelEmisor.GuardarAsync(db, empresaId, emisor.CertificadoValidoHasta, ct);
        await DatosOficiales.RefrescarAsync(db, empresaId, emisor, ct);

        // Y LO DEMÁS QUE DICE DEL EMISOR (migración 015): portal, dirección y
        // qué falta. Es lo que enseñan la lista y la ventana sin preguntar.
        await EstadoDelEmisor.GuardarAsync(db, empresaId, emisor, ct);

        if (emisor.Portal?.CorreosAlReceptor == false)
            log.LogWarning("La empresa {Empresa} (RUC {Ruc}) no manda correos al receptor: falta el " +
                           "identificador o el correo de respuesta en el servicio.", empresaId, ruc);

        // Y EL WEBHOOK: ¿el servicio sigue mandando los avisos aquí? Si la
        // llamada falla, no se guarda nada (se queda lo de ayer) y el fallo ya
        // quedó anotado en la credencial.
        var w = await facturacion.LeerComoEmpresaAsync<EndpointsCredenciales.WebhookDelServicio>(
            empresaId, "/v1/webhook", ct);

        if (w.Exito && w.Datos is { } webhook)
        {
            var urlServicio = webhook.Registrado ? webhook.Url : null;
            await WebhookDelEmisor.GuardarAsync(db, empresaId, urlServicio, ct);

            var esperada = EndpointsCredenciales.UrlWebhook(urlApi, empresaId);

            if (!string.Equals(urlServicio, esperada, StringComparison.Ordinal))
                log.LogWarning(
                    "El servicio manda los avisos de la empresa {Empresa} (RUC {Ruc}) a {Url}, no a {Esperada}. " +
                    "Los avisos de SUNAT no llegan aquí: pulsa «Conectar» o revisa quién la cambió.",
                    empresaId, ruc, urlServicio ?? "(ningún webhook de la API)", esperada);
        }

        await transaccion.CommitAsync(ct);

        var dias = CertificadoDelEmisor.DiasParaVencer(emisor.CertificadoValidoHasta?.UtcDateTime);

        if (emisor.CertificadoValidoHasta is null)
            log.LogWarning("La empresa {Empresa} (RUC {Ruc}) no tiene certificado activo en el servicio: no puede emitir.",
                           empresaId, ruc);
        else if (dias <= DiasDeAviso)
            log.LogWarning("El certificado de la empresa {Empresa} (RUC {Ruc}) vence el {Fecha:yyyy-MM-dd}: quedan {Dias} días.",
                           empresaId, ruc, emisor.CertificadoValidoHasta, dias);

        return true;
    }
}