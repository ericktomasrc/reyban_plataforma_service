using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Plataforma.Core.Autorizacion;
using Plataforma.Api.Arranque;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <param name="WebhookSecreto">
/// Opcional. Si no viene, se CONSERVA el de la credencial anterior.
/// </param>
public record PeticionCredencial(string BaseUrl, string Clave, string? WebhookSecreto);

public record PeticionSecretoWebhook(string Secreto);


/// <summary>
/// La clave con la que cada empresa llega al servicio de facturación, y el
/// secreto con el que el servicio le firma los avisos de vuelta.
///
/// ES LA PANTALLA MÁS SENSIBLE DE LA PLATAFORMA. Esa clave emite comprobantes
/// tributarios en nombre del cliente: quien la tenga puede facturar por él, y
/// un comprobante emitido no se deshace con un botón.
///
/// TODO ESTO ES DEL SUPER ADMINISTRADOR, no del administrador del cliente, y
/// no es un detalle de permisos. La clave se la da la plataforma al cliente
/// —sale de dar de alta su empresa en el servicio de facturación—, así que
/// dejársela cambiar a él sería dejarle apuntar a otro servicio.
///
/// NINGUNO DE LOS DOS SECRETOS SALE DE VUELTA. Se guardan cifrados y solo se
/// devuelven sus cuatro últimos caracteres, que es lo que hacen los bancos con
/// las tarjetas y sirve para lo mismo: reconocerlos sin poder usarlos.
///
/// ══════════════════════════════════════════════════════════════════════════
/// DOS SECRETOS, DOS DIRECCIONES, NO SE CONFUNDEN
///
///   clave `fac_...`     la plataforma → el servicio   (para emitir)
///   secreto `whsec_...` el servicio → la plataforma   (para firmar avisos)
///
/// El servicio enseña CADA UNO UNA SOLA VEZ. Por eso el secreto del webhook
/// tiene su propio endpoint: se registra el webhook DESPUÉS de configurar la
/// clave, y obligar a volver a pegar la `fac_` para guardarlo sería obligar a
/// pedirle una nueva al servicio, porque esa ya no se puede volver a ver.
/// ══════════════════════════════════════════════════════════════════════════
/// </summary>
public static class EndpointsCredenciales
{
    public static void MapearCredenciales(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/admin/empresas/{id:guid}/facturacion")
                         .WithTags("Facturación · credenciales");


        // =====================================================================
        // VER CÓMO ESTÁ
        //
        // Devuelve la URL, la pista y las tres fechas de telemetría. Esas tres
        // son las que contestan «¿por qué no se emite?» sin abrir un log: si el
        // último fallo es más reciente que la última llamada, algo está roto y
        // `ultimoFallo` dice qué.
        //
        // Y LO MISMO DEL WEBHOOK, en la otra dirección: la URL que hay que
        // registrar en el servicio, si el secreto está guardado, y el último
        // aviso que llegó —bueno o malo—. Eso contesta «¿por qué se queda en
        // camino?»: si el último llegó con la firma mal, el secreto no es el
        // del webhook registrado.
        // =====================================================================
        grupo.MapGet("/", async (
            Guid id, ContextoPlataforma db, Cifrador cifrador, UrlApi urlApi,
            CancellationToken ct) =>
        {
            var c = await db.CredencialesFacturacion
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.EmpresaId == id, ct);

            var ultimo = await UltimoAvisoAsync(db, id, ct);

            return Results.Ok(new
            {
                configurada = c is not null,

                credencial = c is null ? null : new
                {
                    c.Id, c.BaseUrl, c.Pista,
                    c.RotadaEn, c.UltimaLlamadaEn, c.UltimoFalloEn, c.UltimoFallo,
                    c.CreadoEn
                },

                webhook = new
                {
                    url        = UrlWebhook(urlApi, id),
                    configurado = c?.WebhookSecretoCifrado is { Length: > 0 },
                    pista      = PistaSecreto(c, cifrador),
                    ultimoAviso = ultimo
                }
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Ver la configuración de facturación de una empresa");


        // =====================================================================
        // GUARDAR O ROTAR LA CLAVE
        //
        // ES SIEMPRE UN ALTA, NUNCA UN UPDATE de la clave.
        //
        // La anterior se desactiva y se inserta otra. Cuesta una fila más y
        // paga dos cosas: la bitácora conserva cuándo se cambió y quién lo
        // hizo, y el índice único parcial —que solo mira las activas— garantiza
        // que no haya dos vivas a la vez.
        //
        // Sobrescribir la fila habría borrado la respuesta a «¿desde cuándo
        // está fallando esto?», que es justo la pregunta del día malo.
        //
        // EL SECRETO DEL WEBHOOK VIAJA CON ELLA. Si viene, se usa; si no, se
        // copia el de la fila anterior TAL CUAL, cifrado —misma llave maestra,
        // no hace falta descifrarlo—. Sin esa copia, rotar la clave `fac_`
        // dejaría la fila nueva sin secreto y todos los avisos de SUNAT
        // empezarían a rechazarse con 401, sin que nadie hubiera tocado el
        // webhook.
        // =====================================================================
        grupo.MapPut("/", async (
            Guid id, PeticionCredencial p,
            ContextoPlataforma db, Cifrador cifrador,
            CancellationToken ct) =>
        {
            var url   = (p.BaseUrl ?? "").Trim().TrimEnd('/');
            var clave = (p.Clave ?? "").Trim();

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return Results.BadRequest(new
                {
                    error = "La dirección tiene que ser una URL completa, con https:// delante."
                });
            }

            if (clave.Length < 16)
                return Results.BadRequest(new { error = "Esa clave es demasiado corta para ser la del servicio." });

            // El orden de las dos comprobaciones importa: la confusión que pasa
            // de verdad es pegar el `whsec_` en la casilla de la clave.
            if (clave.StartsWith("whsec_", StringComparison.Ordinal))
                return Results.BadRequest(new
                {
                    error = "Eso es el secreto del webhook, no la clave del servicio. " +
                            "La clave empieza por fac_."
                });

            var secretoNuevo = (p.WebhookSecreto ?? "").Trim();
            if (secretoNuevo.Length > 0 && RevisarSecreto(secretoNuevo) is { } errorSecreto)
                return Results.BadRequest(new { error = errorSecreto });

            var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.Id == id)
                .Select(e => new { e.RazonSocial })
                .FirstOrDefaultAsync(ct);

            if (empresa is null) return Results.NotFound();

            // NO SE COMPRUEBA QUE LA EMPRESA TENGA EL MÓDULO CONTRATADO.
            //
            // Es deliberado y el orden importa: primero se configura, después
            // se activa el módulo y recién entonces la gente ve la pantalla. Al
            // revés, habría un rato en que el módulo está activo y cualquiera
            // que entre a emitir se encuentra un error.

            var anterior = await db.CredencialesFacturacion
                .FirstOrDefaultAsync(c => c.EmpresaId == id, ct);

            if (anterior is not null)
            {
                anterior.Activo   = false;
                anterior.RotadaEn = DateTime.UtcNow;
            }

            var secretoCifrado = secretoNuevo.Length > 0
                ? cifrador.Cifrar(secretoNuevo)
                : anterior?.WebhookSecretoCifrado;

            db.CredencialesFacturacion.Add(new CredencialFacturacion
            {
                EmpresaId    = id,
                BaseUrl      = url,
                ClaveCifrada = cifrador.Cifrar(clave),

                // Los cuatro últimos, en claro. Con menos no se distinguen dos
                // claves; con más se empieza a regalar la clave.
                Pista = clave.Length >= 4 ? clave[^4..] : null,

                WebhookSecretoCifrado = secretoCifrado
            });

            await db.SaveChangesAsync(ct);

            var conSecreto = secretoCifrado is { Length: > 0 };

            return Results.Ok(new
            {
                mensaje = (anterior is null
                              ? $"Configurado. {empresa.RazonSocial} ya puede facturar."
                              : "Clave rotada. La anterior dejó de valer ahora mismo.")
                          + (conSecreto
                              ? ""
                              : " Falta el secreto del webhook: hasta que se guarde, las " +
                                "respuestas de SUNAT no llegarán y los comprobantes se quedarán «en camino»."),
                rotada = anterior is not null,
                webhookConfigurado = conSecreto
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Guardar o rotar la clave del servicio de facturación");


        // =====================================================================
        // GUARDAR EL SECRETO DEL WEBHOOK, SOLO
        //
        // AQUÍ SÍ ES UN UPDATE, y no contradice lo de arriba: lo que se
        // conserva insertando es la historia de la CLAVE, que es lo que emite.
        // El cambio de secreto queda igualmente en la bitácora —que registra
        // el UPDATE con la columna en `campos_cambiados` y el valor tapado
        // (migración 012)—, y no obliga a volver a pegar una `fac_` que el
        // servicio ya no enseña.
        // =====================================================================
        grupo.MapPut("/webhook", async (
            Guid id, PeticionSecretoWebhook p,
            ContextoPlataforma db, Cifrador cifrador, CancellationToken ct) =>
        {
            var secreto = (p.Secreto ?? "").Trim();

            if (RevisarSecreto(secreto) is { } error)
                return Results.BadRequest(new { error });

            var c = await db.CredencialesFacturacion
                .FirstOrDefaultAsync(x => x.EmpresaId == id, ct);

            if (c is null)
                return Results.BadRequest(new
                {
                    error = "Primero hay que guardar la dirección y la clave del servicio."
                });

            var habia = c.WebhookSecretoCifrado is { Length: > 0 };

            c.WebhookSecretoCifrado = cifrador.Cifrar(secreto);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                mensaje = (habia ? "Secreto del webhook cambiado." : "Secreto del webhook guardado.") +
                          " Para comprobarlo, manda un aviso de prueba desde el panel del servicio.",
                pista = secreto[^4..]
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Guardar el secreto con el que el servicio firma sus avisos");


        // =====================================================================
        // PROBAR LA CONEXIÓN
        //
        // SE PRUEBA DE VERDAD, llamando a `GET /v1/emisor` del servicio, que
        // devuelve el emisor que esa clave resuelve.
        //
        // Y ESE ENDPOINT ES EL CORRECTO PARA PROBAR, no `/health`: `/health`
        // responde sin clave, así que diría que todo está bien con una clave
        // equivocada. Lo que hay que comprobar no es que el servicio esté
        // encendido, sino que ESTA clave sirve y a qué RUC corresponde.
        //
        // Ver el RUC que devuelve es media prueba: si no es el de la empresa
        // que se está configurando, la clave es de otro cliente — que es el
        // error que de verdad pasa al copiar y pegar.
        //
        // EL WEBHOOK NO SE PUEDE PROBAR DESDE AQUÍ: el aviso de prueba lo
        // dispara el panel del servicio, que es quien firma. Lo que sí se ve
        // aquí, en el GET, es si llegó y si la firma cuadró.
        // =====================================================================
        grupo.MapPost("/probar", async (
            Guid id,
            ContextoPlataforma db,
            ClienteFacturacion facturacion,
            CancellationToken ct) =>
        {
            var existe = await db.CredencialesFacturacion.AnyAsync(c => c.EmpresaId == id, ct);
            if (!existe)
                return Results.BadRequest(new { error = "Esta empresa todavía no tiene clave configurada." });

            // EL CLIENTE RESUELVE LA CLAVE POR LA COOKIE, y un super
            // administrador no pertenece a ninguna empresa. Por eso se usa
            // `LeerComoEmpresaAsync`, que es la ÚNICA puerta del cliente que
            // acepta una empresa por parámetro — y es de solo lectura.
            //
            // La alternativa que se descartó era cambiarle la empresa al
            // contexto de la petición durante la llamada. Funciona, y es
            // exactamente la clase de truco que alguien copia seis meses
            // después para el camino de emitir, donde sí haría daño.
            var resultado = await facturacion.LeerComoEmpresaAsync<EmisorDelServicio>(
                id, "/v1/emisor", ct);

            if (!resultado.Exito)
            {
                return Results.Ok(new
                {
                    ok = false,
                    resultado.Error,
                    resultado.Detalle,
                    pista = resultado.NoSePudoLlegar
                        ? "Revisa que el servicio esté encendido y que la dirección sea la correcta."
                        : resultado.Estado == 401
                            ? "La dirección llega, pero la clave no vale. Pide otra en el panel del servicio."
                            : null
                });
            }

            var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.Id == id).Select(e => new { e.Ruc, e.RazonSocial })
                .FirstAsync(ct);

            var rucCoincide = string.Equals(
                resultado.Datos?.Ruc?.Trim(), empresa.Ruc?.Trim(), StringComparison.Ordinal);

            return Results.Ok(new
            {
                ok = true,
                emisor = resultado.Datos,
                rucCoincide,
                mensaje = rucCoincide
                    ? $"Conectado. La clave corresponde a {resultado.Datos?.RazonSocial}."
                    : $"Conectado, PERO el RUC no coincide: el servicio responde {resultado.Datos?.Ruc} " +
                      $"y esta empresa es {empresa.Ruc}. Es la clave de otro cliente."
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Comprobar que la clave llega y a qué emisor corresponde");
    }


    // =========================================================================

    /// <summary>
    /// La dirección que hay que registrar en el panel del servicio.
    ///
    /// SALE DE `PLATAFORMA_URL_API`, NO DE `PLATAFORMA_URL_PUBLICA`. Son dos
    /// direcciones distintas aunque en producción coincidan: la pública es la
    /// del FRONT, a donde llevan los enlaces de los correos; esta es la de la
    /// API, a donde tiene que llegar el servicio. En desarrollo la primera es
    /// el 5173 (Vite) y la segunda el 5255, y usar la del correo daba una URL
    /// de webhook que no servía. Ver `UrlApi`.
    ///
    /// Y no se deduce de la cabecera `Host` de la petición, por lo mismo que
    /// los enlaces de los correos: dejaría que cualquiera la apuntara a su
    /// propio servidor.
    /// </summary>
    private static string UrlWebhook(UrlApi urlApi, Guid empresaId) =>
        $"{urlApi.Base}/api/webhooks/facturacion/{empresaId}";


    /// <summary>
    /// ¿Tiene pinta de ser el secreto del webhook? No comprueba que sea EL
    /// secreto — eso solo lo dice el primer aviso.
    ///
    /// La comprobación que importa es la de `fac_`: pegar la clave del servicio
    /// en esta casilla es el error que pasa, y no da ningún fallo al guardar —
    /// solo un 401 en cada aviso, horas después.
    /// </summary>
    private static string? RevisarSecreto(string secreto)
    {
        if (secreto.Length == 0)
            return "Falta el secreto del webhook.";

        if (secreto.StartsWith("fac_", StringComparison.Ordinal))
            return "Esa es la clave del servicio, no el secreto del webhook. " +
                   "El secreto empieza por whsec_ y lo enseña el servicio al registrar el webhook.";

        if (!secreto.StartsWith("whsec_", StringComparison.Ordinal))
            return "El secreto del webhook empieza por whsec_. Cópialo entero.";

        if (secreto.Length < 30 || secreto.Any(char.IsWhiteSpace))
            return "Ese secreto está incompleto o lleva espacios. Cópialo otra vez, entero.";

        return null;
    }


    /// <summary>
    /// Los cuatro últimos caracteres del secreto, o null.
    ///
    /// SE DESCIFRA PARA SACARLOS, en vez de guardar una pista aparte: es un
    /// endpoint de super administrador, se hace una vez por pantalla, y una
    /// columna más es una columna más que tapar en la bitácora.
    ///
    /// Si no se puede descifrar —la llave maestra cambió, que es justo lo que
    /// el documento dice que no puede pasar— se dice, en vez de tumbar la
    /// pantalla entera.
    /// </summary>
    private static string? PistaSecreto(CredencialFacturacion? c, Cifrador cifrador)
    {
        if (c?.WebhookSecretoCifrado is not { Length: > 0 } cifrado) return null;

        try
        {
            var s = cifrador.Descifrar(cifrado);
            return s.Length >= 4 ? s[^4..] : null;
        }
        catch
        {
            return "(no se puede descifrar)";
        }
    }


    public sealed record UltimoAviso(
        DateTime RecibidoEn, bool FirmaValida, DateTime? ProcesadoEn, string? Nota);

    /// <summary>
    /// El último aviso que llegó para esta empresa, bueno o malo.
    ///
    /// LOS MALOS SE GUARDAN SIN EMPRESA —no se cree a quien no firmó bien— pero
    /// con la de la URL dentro de `error_proceso`. Por eso la búsqueda es
    /// doble. Si algún día hace falta buscarlo a menudo, eso pide una columna
    /// propia, no un LIKE.
    ///
    /// Por SQL y no por EF, igual que el receptor: la tabla no tiene entidad.
    /// </summary>
    private static async Task<UltimoAviso?> UltimoAvisoAsync(
        ContextoPlataforma db, Guid empresaId, CancellationToken ct)
    {
        var conexion = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaccion = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();

        await using var cmd = new NpgsqlCommand("""
            SELECT recibido_en, firma_valida, procesado_en, error_proceso
              FROM webhooks_recibidos
             WHERE empresa_id = @empresa
                OR (NOT firma_valida AND error_proceso LIKE @patron)
             ORDER BY id DESC
             LIMIT 1
            """, conexion, transaccion);

        cmd.Parameters.AddWithValue("empresa", empresaId);
        cmd.Parameters.AddWithValue("patron", $"%{empresaId}%");

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new UltimoAviso(
            r.GetDateTime(0),
            r.GetBoolean(1),
            r.IsDBNull(2) ? null : r.GetDateTime(2),
            r.IsDBNull(3) ? null : r.GetString(3));
    }


    /// <summary>
    /// Lo que devuelve <c>GET /v1/emisor</c> del servicio de facturación.
    ///
    /// SE DECLARAN SOLO LOS CAMPOS QUE SE USAN. Devuelve más, y da igual: si un
    /// día añade otro, esto sigue funcionando. Copiar su contrato entero solo
    /// conseguiría que un cambio suyo rompiera esto sin motivo.
    /// </summary>
    public sealed record EmisorDelServicio(
        string? Ruc,
        string? RazonSocial,
        string? NombreComercial,
        string? Ambiente);
}
