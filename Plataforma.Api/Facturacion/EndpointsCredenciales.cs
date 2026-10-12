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

/// <summary>«Comprobar» y «Conectar»: la dirección del servicio y la clave fac_, sin guardar todavía.</summary>
public record PeticionConectar(string BaseUrl, string Clave);


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
///
/// ══════════════════════════════════════════════════════════════════════════
/// DESDE LA PARTE B DEL ALTA: «COMPROBAR» Y «CONECTAR»
///
/// El camino normal ya no es pegar la clave y el `whsec_` a mano. Es:
///
///   comprobar   GET /v1/emisor con la clave SIN GUARDAR. Enseña de quién es
///               (RUC, razón social, ambiente, certificado) y si se puede
///               conectar. No escribe nada.
///
///   conectar    vuelve a preguntar, aplica la regla del RUC, registra el
///               webhook de la empresa en el servicio (POST /v1/webhook) y
///               solo entonces escribe todo de una vez: credencial, secreto,
///               RUC, razón social, ambiente y certificado.
///
///   reintentar-avisos   en otra petición, ya confirmado lo anterior: el
///               servicio reenvía los avisos que se rechazaron mientras tenía
///               un secreto que la plataforma no conocía.
///
/// Los dos PUT de abajo siguen, como «Configuración manual» de emergencia
/// mientras exista la pestaña Webhooks del panel del servicio.
/// ══════════════════════════════════════════════════════════════════════════
///
/// ══════════════════════════════════════════════════════════════════════════
/// DESDE LA PARTE C (diseño §13): EL ALTA CREA EL EMISOR
///
/// Una empresa nueva ya llega conectada: el alta crea el emisor en el servicio
/// y guarda su fac_ y su whsec_ (EndpointsEmpresas). «Comprobar → Conectar»
/// sigue para rotar la clave, para las empresas de antes y para pasar a
/// producción. Y se añaden:
///
///   confirmar-alta          le dice al servicio que el alta ya está guardada
///                           aquí. La pantalla la llama sola.
///   clave-anterior/revocada «Ya la revoqué»: comprueba contra el servicio que
///                           la clave vieja ya no vale (401), lo apunta y vacía
///                           su copia cifrada.
///
/// AL ROTAR, la fila anterior se queda (nada se borra) pero pierde su whsec_
/// en el acto —ya no vale: el servicio dio uno nuevo— y su fac_ en cuanto se
/// comprueba revocada. Pulsar «Conectar» otra vez con LA MISMA clave no es
/// una rotación: la copia vieja se vacía en el acto, sin aviso.
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
            OpcionesServicioFacturacion servicio, CancellationToken ct) =>
        {
            var c = await db.CredencialesFacturacion
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.EmpresaId == id, ct);

            var e = await db.Empresas
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Nombre, x.Ruc, x.RazonSocial, x.Ambiente, x.DatosOficialesEn })
                .FirstOrDefaultAsync(ct);

            if (e is null) return Results.NotFound();

            var ultimo = await UltimoAvisoAsync(db, id, ct);
            var urlWebhook = UrlWebhook(urlApi, id);

            // LO ÚLTIMO QUE DIJO EL SERVICIO DEL EMISOR (migración 015): alta,
            // portal, dirección y qué le falta para emitir.
            var (estado, estadoEn) = c is null
                ? ((System.Text.Json.JsonElement?)null, (DateTime?)null)
                : await EstadoDelEmisor.LeerAsync(db, id, ct);

            // LAS CLAVES ANTERIORES. Una fila retirada que conserva su clave
            // cifrada es el aviso rojo: puede seguir valiendo en el servicio.
            // Se apaga con «Ya la revoqué», y vuelve sola en la próxima rotación.
            var anteriores = await db.CredencialesFacturacion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.EmpresaId == id && !x.Activo)
                .OrderByDescending(x => x.RotadaEn)
                .Select(x => new
                {
                    x.Id, x.Pista, x.RotadaEn,
                    pendiente = x.ClaveCifrada != null,
                    x.RevocadaComprobadaEn, x.RevocadaComprobadaPor, x.RevocadaComprobacion
                })
                .ToListAsync(ct);

            var comprobada = anteriores
                .Where(a => a.RevocadaComprobadaEn != null)
                .OrderByDescending(a => a.RevocadaComprobadaEn)
                .FirstOrDefault();

            var comprobadaPor = comprobada?.RevocadaComprobadaPor is { } quien
                ? await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
                      .Where(u => u.Id == quien).Select(u => u.Nombre).FirstOrDefaultAsync(ct)
                : null;

            return Results.Ok(new
            {
                configurada = c is not null,

                // Dónde está el servicio según el .env, para proponerla en
                // «Comprobar», y si el alta puede crear emisores.
                servicio = new
                {
                    baseUrl = servicio.BaseUrl.Length > 0 ? servicio.BaseUrl : null,
                    altaDisponible = servicio.ProblemaAlta() is null
                },

                alta = c is null ? null : new
                {
                    creadaPorAlta = c.CreadaPorAlta,
                    confirmadaEn  = c.AltaConfirmadaEn,
                    sinConfirmar  = c.CreadaPorAlta && c.AltaConfirmadaEn is null
                },

                emisor   = estado,
                emisorEn = estadoEn,
                avisos   = EstadoDelEmisor.Avisos(estado),

                claveAnterior = new
                {
                    pendientes = anteriores
                        .Where(a => a.pendiente)
                        .Select(a => new { a.Id, a.Pista, a.RotadaEn }),

                    ultimaComprobada = comprobada is null ? null : new
                    {
                        comprobada.Pista,
                        comprobadaEn = comprobada.RevocadaComprobadaEn,
                        por = comprobadaPor,
                        comprobacion = comprobada.RevocadaComprobacion
                    }
                },

                // LOS DATOS OFICIALES, que llegan del servicio. `conectada`
                // falso = todavía sin RUC: no puede tener el módulo.
                empresa = new
                {
                    e.Nombre, e.Ruc, e.RazonSocial, e.Ambiente,
                    e.DatosOficialesEn,
                    conectada = e.Ruc is not null
                },

                credencial = c is null ? null : new
                {
                    c.Id, c.BaseUrl, c.Pista,
                    c.RotadaEn, c.UltimaLlamadaEn, c.UltimoFalloEn, c.UltimoFallo,
                    c.CreadoEn
                },

                // LO ÚLTIMO QUE DIJO EL SERVICIO DEL CERTIFICADO. Lo refrescan
                // «Probar conexión» y la revisión diaria. `consultadoEn` nulo es
                // «nunca se preguntó»; consultado con `validoHasta` nulo es «el
                // servicio no tiene certificado activo»: no puede emitir.
                certificado = c is null ? null : new
                {
                    validoHasta    = c.CertificadoValidoHasta,
                    consultadoEn   = c.CertificadoConsultadoEn,
                    diasParaVencer = CertificadoDelEmisor.DiasParaVencer(c.CertificadoValidoHasta)
                },

                webhook = new
                {
                    url        = urlWebhook,
                    configurado = c?.WebhookSecretoCifrado is { Length: > 0 },
                    pista      = PistaSecreto(c, cifrador),
                    ultimoAviso = ultimo,

                    // LO QUE DICE EL SERVICIO (GET /v1/webhook), la última vez
                    // que se le preguntó. `apuntaAqui` falso es el rojo de la
                    // pantalla: los avisos de SUNAT se van a otra dirección.
                    urlServicio = c?.WebhookUrlServicio,
                    revisadoEn  = c?.WebhookRevisadoEn,
                    apuntaAqui  = c?.WebhookRevisadoEn is null
                                  ? (bool?)null
                                  : c.WebhookUrlServicio == urlWebhook
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
            ContextoPlataforma db, Cifrador cifrador, ContextoPeticion ctx,
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
                .Select(e => new { e.Nombre })
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

            // EL SECRETO SE LEE ANTES DE RETIRAR LA ANTERIOR: retirarla vacía
            // su copia (la fila nueva se queda con ella).
            var secretoCifrado = secretoNuevo.Length > 0
                ? cifrador.Cifrar(secretoNuevo)
                : anterior?.WebhookSecretoCifrado;

            var misma = anterior is not null &&
                        RetirarAnterior(anterior, clave, cifrador, ctx.AutorId, DateTime.UtcNow);

            db.CredencialesFacturacion.Add(new CredencialFacturacion
            {
                EmpresaId    = id,
                BaseUrl      = url,
                ClaveCifrada = cifrador.Cifrar(clave),

                // Los cuatro últimos, en claro. Con menos no se distinguen dos
                // claves; con más se empieza a regalar la clave.
                Pista = clave.Length >= 4 ? clave[^4..] : null,

                WebhookSecretoCifrado = secretoCifrado,

                // LA FECHA DEL CERTIFICADO SE COPIA, como el secreto. Sin esto,
                // rotar la clave dejaría la pantalla en «sin consultar» hasta la
                // revisión del día siguiente. La pantalla prueba la conexión
                // justo después de guardar, y eso la refresca con la clave nueva.
                CertificadoValidoHasta  = anterior?.CertificadoValidoHasta,
                CertificadoConsultadoEn = anterior?.CertificadoConsultadoEn,

                CreadaPorAlta    = anterior?.CreadaPorAlta ?? false,
                AltaConfirmadaEn = anterior?.AltaConfirmadaEn
            });

            await db.SaveChangesAsync(ct);

            var conSecreto = secretoCifrado is { Length: > 0 };

            return Results.Ok(new
            {
                mensaje = (anterior is null
                              ? $"Configurado. {empresa.Nombre} ya puede facturar."
                              : misma
                                  ? "Guardada otra vez la misma clave."
                                  : "Clave rotada. La anterior SIGUE VALIENDO en el servicio hasta que la revoques allí; " +
                                    "después pulsa «Ya la revoqué».")
                          + (conSecreto
                              ? ""
                              : " Falta el secreto del webhook: hasta que se guarde, las " +
                                "respuestas de SUNAT no llegarán y los comprobantes se quedarán «en camino»."),
                rotada = anterior is not null && !misma,
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
            UrlApi urlApi,
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
                .Where(e => e.Id == id).Select(e => new { e.Ruc, e.Nombre })
                .FirstAsync(ct);

            var rucCoincide = empresa.Ruc is not null && string.Equals(
                resultado.Datos?.Ruc?.Trim(), empresa.Ruc.Trim(), StringComparison.Ordinal);

            // DE PASO, SE REFRESCA LO QUE DICE EL SERVICIO: el certificado, la
            // razón social, el ambiente y a dónde manda los avisos. Solo si el
            // RUC coincide: con la clave de otro cliente, nada de eso es de
            // esta empresa. Es el botón «Volver a preguntar al servicio».
            object? webhook = null;
            var emisorFinal = resultado.Datos;

            if (rucCoincide && resultado.Datos is { } emisor)
            {
                // UN ALTA QUE SE QUEDÓ SIN CONFIRMAR (la pantalla se cerró antes
                // de tiempo): la empresa está aquí, así que se confirma ahora y
                // se vuelve a preguntar para guardar el estado bueno.
                if (emisor.Alta?.Estado == "sin_confirmar" &&
                    await EstadoDelEmisor.ConfirmarAsync(db, facturacion, id, ct) == "confirmada")
                {
                    var otra = await facturacion.LeerComoEmpresaAsync<EmisorDelServicio>(id, "/v1/emisor", ct);
                    if (otra.Exito && otra.Datos is { } nuevo) emisor = emisorFinal = nuevo;
                }

                await CertificadoDelEmisor.GuardarAsync(db, id, emisor.CertificadoValidoHasta, ct);
                await DatosOficiales.RefrescarAsync(db, id, emisor, ct);
                await EstadoDelEmisor.GuardarAsync(db, id, emisor, ct);

                var w = await facturacion.LeerComoEmpresaAsync<WebhookDelServicio>(id, "/v1/webhook", ct);

                if (w.Exito && w.Datos is { } dw)
                {
                    var urlServicio = dw.Registrado ? dw.Url : null;
                    await WebhookDelEmisor.GuardarAsync(db, id, urlServicio, ct);

                    var esperada = UrlWebhook(urlApi, id);
                    webhook = new
                    {
                        urlServicio,
                        apuntaAqui = urlServicio == esperada,
                        dw.Pendientes,
                        dw.Agotados,
                        dw.UltimaEntrega,
                        dw.OtrosWebhooksActivos
                    };
                }
            }

            return Results.Ok(new
            {
                ok = true,
                emisor = emisorFinal,
                rucCoincide,
                webhook,
                avisos = rucCoincide && emisorFinal is not null
                    ? EstadoDelEmisor.Avisos(System.Text.Json.JsonSerializer.SerializeToElement(
                          emisorFinal, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))
                    : new List<object>(),
                mensaje = empresa.Ruc is null
                    ? $"La clave llega, y es de {resultado.Datos?.Ruc} · {resultado.Datos?.RazonSocial}. " +
                      "Pero esta empresa todavía no está conectada: usa «Comprobar» y «Conectar»."
                    : rucCoincide
                        ? $"Conectado. La clave corresponde a {resultado.Datos?.RazonSocial}."
                        : $"Conectado, PERO el RUC no coincide: el servicio responde {resultado.Datos?.Ruc} " +
                          $"y esta empresa es {empresa.Ruc}. Es la clave de otro cliente."
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Volver a preguntar al servicio: la clave, los datos oficiales y el webhook");


        // =====================================================================
        // COMPROBAR UNA CLAVE, SIN GUARDARLA
        //
        // La pantalla intermedia del diseño (§3.1): «Esta clave es de
        // 20601234565 · MI EMPRESA SAC · beta · certificado hasta… ¿Es esta
        // empresa?». La primera vez no hay ningún RUC con el que comparar, y
        // el ojo de quien da el alta es la única comprobación posible.
        //
        // NO ESCRIBE NADA, ni aquí ni en el servicio. Ni siquiera la
        // telemetría: no hay credencial todavía.
        // =====================================================================
        grupo.MapPost("/comprobar", async (
            Guid id, PeticionConectar p,
            ContextoPlataforma db, ClienteFacturacion facturacion,
            CancellationToken ct) =>
        {
            if (RevisarEntrada(p, out var url, out var clave) is { } error)
                return Results.BadRequest(new { error });

            var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id, ct);

            if (empresa is null) return Results.NotFound();

            var r = await facturacion.ConsultarEmisorConClaveAsync<EmisorDelServicio>(url, clave, ct);

            if (!r.Exito || r.Datos is null || string.IsNullOrWhiteSpace(r.Datos.Ruc))
                return FalloDelServicio(r);

            var emisor = r.Datos;
            var ruc = r.Datos.Ruc.Trim();
            var problema = await RucNoValeAsync(db, empresa, ruc, ct);

            return Results.Ok(new
            {
                ok = true,
                puedeConectar = problema is null,
                problema,
                emisor = new
                {
                    ruc,
                    razonSocial = emisor.RazonSocial,
                    ambiente    = emisor.Ambiente,
                    certificadoValidoHasta = emisor.CertificadoValidoHasta,
                    certificadoDiasParaVencer =
                        CertificadoDelEmisor.DiasParaVencer(emisor.CertificadoValidoHasta?.UtcDateTime),

                    // Lo nuevo del servicio (parte C1). Nulos con un servicio
                    // anterior: la pantalla simplemente no los enseña.
                    alta   = emisor.Alta,
                    portal = emisor.Portal,
                    listo  = emisor.Listo
                },
                empresa = new { empresa.Nombre, empresa.Ruc, conectada = empresa.Ruc is not null },
                rotacion = empresa.Ruc is not null
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Comprobar de quién es una clave, sin guardarla");


        // =====================================================================
        // CONECTAR
        //
        // EL ORDEN NO ES INTERCAMBIABLE (diseño §3.1):
        //
        //   1. Volver a preguntar a GET /v1/emisor. No se fía de lo que había
        //      en pantalla: entre «Comprobar» y «Conectar» pudo cambiar todo.
        //   2. La regla del RUC. Si falla, para: no guarda nada y NO toca el
        //      servicio.
        //   3. POST /v1/webhook con la dirección de la plataforma: el servicio
        //      crea o actualiza el webhook de la empresa y da un secreto nuevo.
        //   4. Escribir todo de una vez.
        //
        // LAS LLAMADAS AL SERVICIO VAN ANTES QUE LA ESCRITURA. Si el paso 4
        // falla, el servicio tiene un secreto que aquí no está, y los avisos se
        // rechazan con 401; se dice en pantalla y volver a pulsar «Conectar» lo
        // arregla (el paso 3 actualiza el mismo webhook y da otro secreto). Al
        // revés, la pantalla diría «conectado» antes de estarlo.
        //
        // EL PASO 4 VA EN UN PUNTO DE GUARDADO, como el receptor del webhook:
        // si falla, se vuelve a él y la respuesta llega igual, con su mensaje,
        // en vez de un 500 sin explicación.
        //
        // NADA DE ESTO ESCRIBE EN `llamadas_facturacion`: la respuesta del
        // paso 3 lleva el `whsec_` en claro (diseño §10.3).
        // =====================================================================
        grupo.MapPost("/conectar", async (
            Guid id, PeticionConectar p,
            ContextoPlataforma db, Cifrador cifrador,
            ClienteFacturacion facturacion, UrlApi urlApi, ContextoPeticion ctx,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Facturacion.Conectar");

            if (RevisarEntrada(p, out var url, out var clave) is { } error)
                return Results.BadRequest(new { error });

            var empresa = await db.Empresas.IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.Id == id, ct);

            if (empresa is null) return Results.NotFound();

            if (!empresa.Activo)
                return Results.BadRequest(new
                {
                    error = "Esta empresa está desactivada. Reactívala antes de conectarla."
                });

            // --- 1. ¿De quién es la clave? ------------------------------------
            var r = await facturacion.ConsultarEmisorConClaveAsync<EmisorDelServicio>(url, clave, ct);

            if (!r.Exito || r.Datos is null || string.IsNullOrWhiteSpace(r.Datos.Ruc))
                return FalloDelServicio(r);

            var emisor = r.Datos;
            var ruc = r.Datos.Ruc.Trim();

            // --- 2. La regla del RUC ------------------------------------------
            if (await RucNoValeAsync(db, empresa, ruc, ct) is { } problema)
                return Results.Conflict(new { error = problema, nadaGuardado = true });

            // --- 3. El webhook, en el servicio --------------------------------
            var urlWebhook = UrlWebhook(urlApi, id);

            var w = await facturacion.RegistrarWebhookConClaveAsync<RegistroWebhookDelServicio>(
                url, clave, urlWebhook, ct);

            if (!w.Exito || w.Datos?.Secreto is not { Length: > 0 } secreto ||
                RevisarSecreto(secreto) is not null)
            {
                return Results.Json(new
                {
                    error = "El servicio no registró el webhook, así que no se guardó nada.",
                    detalle = w.Error is null ? w.Detalle : $"{w.Error} {w.Detalle}".Trim(),
                    nadaGuardado = true
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            // --- 4. Escribir todo de una vez ----------------------------------
            var transaccion = db.Database.CurrentTransaction
                ?? throw new InvalidOperationException(
                    "«Conectar» necesita la transacción de MiddlewareContexto.");

            await transaccion.CreateSavepointAsync(PuntoConectar, ct);

            var ahora = DateTime.UtcNow;
            CredencialFacturacion? anterior;
            var misma = false;

            try
            {
                anterior = await db.CredencialesFacturacion
                    .FirstOrDefaultAsync(c => c.EmpresaId == id, ct);

                // La anterior se retira: pierde su whsec_ (el servicio acaba de
                // dar otro) y, si era la misma fac_, también la clave.
                if (anterior is not null)
                    misma = RetirarAnterior(anterior, clave, cifrador, ctx.AutorId, ahora);

                db.CredencialesFacturacion.Add(new CredencialFacturacion
                {
                    EmpresaId    = id,
                    BaseUrl      = url,
                    ClaveCifrada = cifrador.Cifrar(clave),
                    Pista        = clave[^4..],

                    WebhookSecretoCifrado = cifrador.Cifrar(secreto),
                    WebhookUrlServicio    = w.Datos.Url ?? urlWebhook,
                    WebhookRevisadoEn     = ahora,

                    CertificadoValidoHasta  = emisor.CertificadoValidoHasta?.UtcDateTime,
                    CertificadoConsultadoEn = ahora,
                    UltimaLlamadaEn         = ahora,

                    CreadaPorAlta    = anterior?.CreadaPorAlta ?? false,
                    AltaConfirmadaEn = anterior?.AltaConfirmadaEn
                });

                // EL RUC SOLO SE PONE SI NO HABÍA (la regla de arriba ya
                // comprobó que, si había, es el mismo). La razón social y el
                // ambiente se refrescan siempre: son los del servicio.
                empresa.Ruc ??= ruc;
                empresa.RazonSocial = string.IsNullOrWhiteSpace(emisor.RazonSocial)
                    ? empresa.RazonSocial ?? empresa.Nombre
                    : emisor.RazonSocial.Trim();
                empresa.Ambiente = DatosOficiales.Ambiente(emisor.Ambiente) ?? empresa.Ambiente;
                empresa.DatosOficialesEn = ahora;

                await db.SaveChangesAsync(ct);

                // Lo que dijo el servicio del emisor, en la fila nueva.
                await EstadoDelEmisor.GuardarAsync(db, id, emisor, ct);
            }
            catch (Exception e) when (e is DbUpdateException or PostgresException)
            {
                await transaccion.RollbackToSavepointAsync(PuntoConectar, ct);
                db.ChangeTracker.Clear();

                var otraConElRuc = (e as PostgresException ?? e.InnerException as PostgresException)
                    is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_empresas_ruc" };

                log.LogError(e, "«Conectar» registró el webhook de la empresa {Empresa} en el servicio " +
                                "pero no pudo guardar. Los avisos se rechazarán hasta que se vuelva a conectar.", id);

                return Results.Json(new
                {
                    error = "El servicio ya tiene un secreto nuevo y la plataforma no pudo guardarlo. " +
                            "Los avisos de SUNAT se están rechazando. Pulsa «Conectar» otra vez.",
                    detalle = otraConElRuc
                        ? $"Otra empresa activa acaba de quedarse con el RUC {ruc}."
                        : null,
                    secretoDesincronizado = true
                }, statusCode: StatusCodes.Status500InternalServerError);
            }

            var rotada = anterior is not null && !misma;

            log.LogInformation("Empresa {Empresa} conectada al servicio: RUC {Ruc}, clave …{Pista}{Rotada}.",
                               id, ruc, clave[^4..],
                               rotada ? " (rotada)" : anterior is null ? "" : " (misma clave, reconectada)");

            return Results.Ok(new
            {
                ok = true,
                rotada,
                empresa = new
                {
                    empresa.Nombre, empresa.Ruc, empresa.RazonSocial, empresa.Ambiente
                },
                certificado = new
                {
                    validoHasta    = emisor.CertificadoValidoHasta,
                    diasParaVencer = CertificadoDelEmisor.DiasParaVencer(emisor.CertificadoValidoHasta?.UtcDateTime)
                },
                webhook = new { url = w.Datos.Url ?? urlWebhook, creado = w.Datos.Creado },

                // DICHO SIN SUAVIZAR (decisión del 10 de octubre, §10.2): si se
                // rota, casi siempre es porque la clave se filtró, y dejarla
                // viva arruina el motivo. Revocarla desde aquí es el punto 21.
                //
                // CON LA MISMA CLAVE NO HAY AVISO: no es una rotación, y la copia
                // vieja ya se vació. Con otra, el aviso se apaga con «Ya la revoqué».
                avisoClaveAnterior = rotada
                    ? "La clave anterior SIGUE VALIENDO. Si rotas porque se filtró, revócala ahora " +
                      "en el panel del servicio (pestaña Claves de acceso de esta empresa) y después " +
                      "pulsa «Ya la revoqué»."
                    : null,

                avisos = EstadoDelEmisor.Avisos(System.Text.Json.JsonSerializer.SerializeToElement(
                    emisor, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))),

                // La pantalla llama a las dos, en este orden, en peticiones
                // aparte: cuando llegan, lo de aquí ya está confirmado.
                siguiente = new[]
                {
                    $"POST /api/admin/empresas/{id}/facturacion/confirmar-alta",
                    $"POST /api/admin/empresas/{id}/facturacion/reintentar-avisos"
                },
                mensaje = anterior is null
                    ? $"{empresa.Nombre} conectada: RUC {empresa.Ruc} · {empresa.RazonSocial}."
                    : rotada
                        ? "Clave rotada y webhook actualizado."
                        : "Reconectada con la misma clave; webhook actualizado."
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Conectar la empresa con su clave: datos oficiales, webhook y secreto, solos");


        // =====================================================================
        // REINTENTAR LOS AVISOS — en otra petición, después de «Conectar»
        //
        // Va aparte a propósito: cuando esta llega, el secreto nuevo ya está
        // confirmado en la base. Dentro de «Conectar», el servicio podría
        // reenviar antes de que la transacción terminara, y los avisos se
        // volverían a rechazar. La pantalla la llama sola al terminar de
        // conectar (parte C).
        //
        // Es seguro llamarla de más: los avisos llevan su `evento_id` y el
        // receptor descarta los que ya tenía, y uno viejo no pisa a uno nuevo.
        // =====================================================================
        grupo.MapPost("/reintentar-avisos", async (
            Guid id, int? dias, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var r = await facturacion.ReintentarAvisosAsync<ReintentoDelServicio>(id, dias ?? 7, ct);

            if (!r.Exito)
                return FalloDelServicio(r);

            var n = r.Datos?.Reencoladas ?? 0;

            return Results.Ok(new
            {
                ok = true,
                reencoladas = n,
                mensaje = n == 0
                    ? "No había avisos rechazados que reenviar."
                    : $"El servicio reenviará {n} avisos en los próximos segundos."
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Pedir al servicio que reenvíe los avisos rechazados");


        // =====================================================================
        // CONFIRMAR EL ALTA — en otra petición, después del alta o de «Conectar»
        //
        // EL INVARIANTE (diseño §13): nunca una empresa aquí sin su emisor
        // allí. Por eso el servicio crea el emisor «sin confirmar», la
        // plataforma guarda lo suyo, y SOLO DESPUÉS —cuando eso ya está
        // confirmado en la base— se le dice al servicio que siga. Dentro de la
        // misma petición del alta no se puede: la transacción de
        // MiddlewareContexto se confirma al terminar el endpoint.
        //
        // Antes de confirmar se comprueba que la clave guardada sea de esta
        // empresa (mismo RUC): confirmar el alta de otro sería peor que no
        // confirmar nada.
        //
        // Se puede llamar de más. Si la pantalla se cierra antes, lo hacen
        // «Volver a preguntar al servicio» o la revisión diaria.
        // =====================================================================
        grupo.MapPost("/confirmar-alta", async (
            Guid id, ContextoPlataforma db, ClienteFacturacion facturacion,
            CancellationToken ct) =>
        {
            var ruc = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.Id == id).Select(e => e.Ruc).FirstOrDefaultAsync(ct);

            if (!await db.CredencialesFacturacion.AnyAsync(c => c.EmpresaId == id, ct))
                return Results.BadRequest(new { error = "Esta empresa todavía no tiene clave del servicio." });

            var r = await facturacion.LeerComoEmpresaAsync<EmisorDelServicio>(id, "/v1/emisor", ct);

            if (!r.Exito || r.Datos is null)
                return Results.Json(new
                {
                    ok = false,
                    error = "No se pudo preguntar al servicio. La empresa está guardada; el alta se " +
                            "confirmará sola al volver a preguntar o en la revisión diaria.",
                    detalle = r.Error is null ? r.Detalle : $"{r.Error} {r.Detalle}".Trim()
                }, statusCode: StatusCodes.Status502BadGateway);

            if (ruc is null || !string.Equals(r.Datos.Ruc?.Trim(), ruc, StringComparison.Ordinal))
                return Results.Conflict(new
                {
                    ok = false,
                    error = $"La clave guardada responde con el RUC {r.Datos.Ruc} y esta empresa es " +
                            $"{ruc ?? "(sin RUC)"}. No se confirmó nada."
                });

            var emisor = r.Datos;
            string? estadoAlta = emisor.Alta?.Estado;

            if (estadoAlta == "sin_confirmar")
            {
                estadoAlta = await EstadoDelEmisor.ConfirmarAsync(db, facturacion, id, ct);

                if (estadoAlta is null)
                    return Results.Json(new
                    {
                        ok = false,
                        error = "El servicio no confirmó el alta. La empresa está guardada; se reintenta " +
                                "sola al volver a preguntar o en la revisión diaria."
                    }, statusCode: StatusCodes.Status502BadGateway);

                var otra = await facturacion.LeerComoEmpresaAsync<EmisorDelServicio>(id, "/v1/emisor", ct);
                if (otra.Exito && otra.Datos is { } nuevo) emisor = nuevo;
            }

            await CertificadoDelEmisor.GuardarAsync(db, id, emisor.CertificadoValidoHasta, ct);
            await EstadoDelEmisor.GuardarAsync(db, id, emisor, ct);

            var (estado, estadoEn) = await EstadoDelEmisor.LeerAsync(db, id, ct);

            return Results.Ok(new
            {
                ok = true,
                alta = estadoAlta ?? "(el servicio no lo dice)",
                emisor = estado,
                emisorEn = estadoEn,
                avisos = EstadoDelEmisor.Avisos(estado),
                mensaje = estadoAlta == "abandonada"
                    ? "El servicio dio este alta por abandonada. Hay que conectar la empresa con otra clave."
                    : "Alta confirmada en el servicio."
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Confirmar al servicio el alta de la empresa, y guardar lo que dice del emisor");


        // =====================================================================
        // «YA LA REVOQUÉ»
        //
        // El aviso rojo de la clave anterior no se apaga porque alguien lo
        // diga: se COMPRUEBA. Cada fila retirada que conserva su clave se
        // prueba contra el servicio (GET /v1/emisor, que no escribe nada):
        //
        //   401         revocada de verdad: se apunta quién, cuándo y «el
        //               servicio la rechaza», y se vacía la copia cifrada.
        //   la acepta   sigue viva: no se apunta nada de esa, y el aviso
        //               sigue en rojo (409, con su pista).
        //   no se llega no se apunta NADA de ninguna (502): sin respuesta no
        //               hay comprobación.
        //
        // La misma clave que la activa no se prueba: no fue una rotación, y
        // se vacía en el acto.
        //
        // El aviso vuelve solo en la próxima rotación: es otra fila con clave.
        // =====================================================================
        grupo.MapPost("/clave-anterior/revocada", async (
            Guid id, ContextoPlataforma db, Cifrador cifrador,
            ClienteFacturacion facturacion, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var activa = await db.CredencialesFacturacion.AsNoTracking()
                .FirstOrDefaultAsync(c => c.EmpresaId == id, ct);

            var pendientes = await db.CredencialesFacturacion
                .IgnoreQueryFilters()
                .Where(c => c.EmpresaId == id && !c.Activo && c.ClaveCifrada != null)
                .OrderBy(c => c.RotadaEn)
                .ToListAsync(ct);

            if (pendientes.Count == 0)
                return Results.Ok(new
                {
                    ok = true,
                    comprobadas = 0,
                    mensaje = "No hay ninguna clave anterior guardada: no hay nada que comprobar."
                });

            var claveActiva = Descifrable(activa?.ClaveCifrada, cifrador);

            var revisadas = new List<(CredencialFacturacion Fila, string Resultado, string? Detalle)>();

            foreach (var fila in pendientes)
            {
                var vieja = Descifrable(fila.ClaveCifrada, cifrador);

                if (vieja is null)
                {
                    revisadas.Add((fila, "ilegible", null));
                    continue;
                }

                if (vieja == claveActiva)
                {
                    revisadas.Add((fila, "misma", null));
                    continue;
                }

                var r = await facturacion.ConsultarEmisorConClaveAsync<EmisorDelServicio>(fila.BaseUrl, vieja, ct);

                revisadas.Add(r.Estado switch
                {
                    401                              => (fila, "revocada", null),
                    _ when r.Exito || r.Estado == 403 => (fila, "viva", null),
                    _                                => (fila, "sin_llegar", r.Error is null ? r.Detalle : $"{r.Error} {r.Detalle}".Trim())
                });
            }

            if (revisadas.FirstOrDefault(x => x.Resultado == "sin_llegar") is { Fila: not null } caida)
                return Results.Json(new
                {
                    ok = false,
                    error = "No se pudo preguntar al servicio, así que no se apuntó nada. " +
                            "Revisa que esté encendido y vuelve a pulsar «Ya la revoqué».",
                    detalle = caida.Detalle
                }, statusCode: StatusCodes.Status502BadGateway);

            var ahora = DateTime.UtcNow;

            foreach (var (fila, resultado, _) in revisadas)
            {
                if (resultado is not ("revocada" or "misma")) continue;

                fila.ClaveCifrada          = null;
                fila.ClaveVaciadaEn        = ahora;
                fila.RevocadaComprobadaEn  = ahora;
                fila.RevocadaComprobadaPor = ctx.AutorId;
                fila.RevocadaComprobacion  = resultado == "revocada"
                    ? "comprobado: el servicio la rechaza"
                    : "era la misma clave que la activa: no fue una rotación";
            }

            await db.SaveChangesAsync(ct);

            var apuntadas = revisadas
                .Where(x => x.Resultado is "revocada" or "misma")
                .Select(x => x.Fila.Pista).ToList();

            var vivas = revisadas.Where(x => x.Resultado == "viva").Select(x => x.Fila.Pista).ToList();
            var ilegibles = revisadas.Where(x => x.Resultado == "ilegible").Select(x => x.Fila.Pista).ToList();

            if (vivas.Count > 0)
                return Results.Conflict(new
                {
                    ok = false,
                    error = $"El servicio todavía acepta la clave …{string.Join(", …", vivas)}. Revócala en el " +
                            "panel del servicio (pestaña Claves de acceso de esta empresa) y vuelve a pulsar " +
                            "«Ya la revoqué».",
                    vivas,
                    apuntadas
                });

            if (ilegibles.Count > 0)
                return Results.Conflict(new
                {
                    ok = false,
                    error = $"La copia de la clave …{string.Join(", …", ilegibles)} no se puede descifrar, así " +
                            "que no se puede comprobar. Revócala a mano en el panel del servicio.",
                    ilegibles,
                    apuntadas
                });

            return Results.Ok(new
            {
                ok = true,
                comprobadas = apuntadas.Count,
                apuntadas,
                mensaje = $"Comprobado: el servicio ya rechaza la clave anterior (…{string.Join(", …", apuntadas)}). " +
                          "Se vació su copia guardada."
            });
        })
        .SoloSuperAdmin()
        .WithSummary("«Ya la revoqué»: comprobar contra el servicio que la clave anterior ya no vale");
    }


    /// <summary>
    /// Retira la credencial anterior al rotar o reconectar. Devuelve si era
    /// LA MISMA clave (entonces no es una rotación).
    ///
    /// NADA SE BORRA: la fila se queda, con su pista y sus fechas. Lo que se
    /// va es lo que ya no sirve y sí podría filtrarse:
    ///
    ///   · el whsec_, siempre: el servicio acaba de dar otro (o la fila nueva
    ///     se quedó con él, en la configuración manual).
    ///   · la fac_, solo si es la misma que la nueva: la nueva fila ya la
    ///     tiene, y una segunda copia no aporta nada. Si es otra, se queda
    ///     hasta que «Ya la revoqué» compruebe que el servicio la rechaza.
    /// </summary>
    private static bool RetirarAnterior(
        CredencialFacturacion anterior, string claveNueva, Cifrador cifrador, Guid? autor, DateTime ahora)
    {
        anterior.Activo   = false;
        anterior.RotadaEn = ahora;
        anterior.WebhookSecretoCifrado = null;

        var misma = Descifrable(anterior.ClaveCifrada, cifrador) == claveNueva;

        if (misma)
        {
            anterior.ClaveCifrada          = null;
            anterior.ClaveVaciadaEn        = ahora;
            anterior.RevocadaComprobadaEn  = ahora;
            anterior.RevocadaComprobadaPor = autor;
            anterior.RevocadaComprobacion  = "era la misma clave que la nueva: no fue una rotación";
        }

        return misma;
    }

    /// <summary>La clave en claro, o null si no hay o no se puede descifrar.</summary>
    private static string? Descifrable(byte[]? cifrada, Cifrador cifrador)
    {
        if (cifrada is not { Length: > 0 }) return null;

        try { return cifrador.Descifrar(cifrada); }
        catch { return null; }
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
    internal static string UrlWebhook(UrlApi urlApi, Guid empresaId) =>
        $"{urlApi.Base}/api/webhooks/facturacion/{empresaId}";


    private const string PuntoConectar = "conectar_empresa";


    /// <summary>
    /// La dirección y la clave de «Comprobar» y «Conectar». Las mismas reglas
    /// que el PUT de siempre, en un solo sitio para los dos botones.
    /// </summary>
    private static string? RevisarEntrada(PeticionConectar p, out string url, out string clave)
    {
        url   = (p.BaseUrl ?? "").Trim().TrimEnd('/');
        clave = (p.Clave ?? "").Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "La dirección tiene que ser una URL completa, con https:// delante.";

        if (clave.StartsWith("whsec_", StringComparison.Ordinal))
            return "Eso es un secreto de webhook, no la clave del servicio. La clave empieza por fac_. " +
                   "El secreto ya no hace falta: «Conectar» lo pide solo.";

        if (!clave.StartsWith("fac_", StringComparison.Ordinal) || clave.Length < 16 ||
            clave.Any(char.IsWhiteSpace))
            return "La clave del servicio empieza por fac_ y va entera, sin espacios.";

        return null;
    }


    /// <summary>
    /// LA REGLA DEL RUC (diseño §3.2). Null si se puede conectar; si no, el
    /// motivo, escrito para quien está dando el alta.
    ///
    ///   · La empresa YA tiene RUC: tiene que ser el mismo (es rotar la clave).
    ///   · NO tiene: se acepta el del servicio si ninguna otra empresa ACTIVA
    ///     lo tiene. El filtro global de `activo` hace justo eso: una
    ///     desactivada deja su RUC libre (§10.1).
    ///
    /// El índice único parcial `ux_empresas_ruc` es la red de debajo, para dos
    /// «Conectar» simultáneos con la misma clave en dos empresas.
    /// </summary>
    private static async Task<string?> RucNoValeAsync(
        ContextoPlataforma db, Empresa empresa, string rucServicio, CancellationToken ct)
    {
        if (empresa.Ruc is { } propio)
        {
            return propio == rucServicio
                ? null
                : $"Esta clave es del RUC {rucServicio}; esta empresa es la del RUC {propio}. " +
                  "Es la clave de otro cliente.";
        }

        var otra = await db.Empresas
            .AsNoTracking()
            .Where(e => e.Id != empresa.Id && e.Ruc == rucServicio)
            .Select(e => e.Nombre)
            .FirstOrDefaultAsync(ct);

        return otra is null
            ? null
            : $"Esta clave es de {otra}, que ya está dada de alta aquí con el RUC {rucServicio}.";
    }


    /// <summary>
    /// Un fallo al hablar con el servicio, para la pantalla. 502: quien falló
    /// es el de enfrente, no quien pulsó el botón. Con la clave mala, 400.
    /// </summary>
    private static IResult FalloDelServicio<T>(ResultadoFacturacion<T> r) =>
        Results.Json(new
        {
            ok = false,
            error = r.Error ?? "El servicio no devolvió el RUC de la clave.",
            r.Detalle,
            pista = r.NoSePudoLlegar
                ? "Revisa que el servicio esté encendido y que la dirección sea la correcta."
                : r.Estado == 401
                    ? "La dirección llega, pero la clave no vale. Revisa que esté entera, o pide otra en el panel del servicio."
                    : null,
            nadaGuardado = true
        },
        statusCode: r.Estado == 401 ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);


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
    ///
    /// `CertificadoValidoHasta` es nulo cuando el servicio no tiene certificado
    /// activo para ese emisor. Lo usan «Probar conexión» y la revisión diaria
    /// (`RevisionCertificados`). Los días que faltan NO se leen de aquí: se
    /// calculan al enseñarlos, porque un número guardado ayer ya miente hoy.
    /// </summary>
    public sealed record EmisorDelServicio(
        string? Ruc,
        string? RazonSocial,
        string? NombreComercial,
        string? Ambiente,
        DateTimeOffset? CertificadoValidoHasta,

        // Desde la parte C1 del servicio (diseño §13.4). Nulos con un
        // servicio anterior, y todo sigue funcionando.
        AltaDelEmisor? Alta = null,
        PortalDelEmisor? Portal = null,
        DireccionDelEmisor? Direccion = null,
        ListoDelEmisor? Listo = null);

    /// <summary>`sin_confirmar`, `confirmada` o `abandonada`.</summary>
    public sealed record AltaDelEmisor(
        string? Estado, DateTimeOffset? CreadoEn, DateTimeOffset? ConfirmadaEn);

    /// <summary>Sin identificador y correo de respuesta, ningún receptor recibe su correo.</summary>
    public sealed record PortalDelEmisor(
        string? Identificador, string? CorreoRespuesta, bool? TieneLogo, bool? CorreosAlReceptor);

    /// <summary>La dirección fiscal. Vive en el servicio: aquí solo se enseña.</summary>
    public sealed record DireccionDelEmisor(
        string? Ubigeo, string? Direccion, string? Distrito, string? Provincia,
        string? Departamento, DateTimeOffset? RevisadaEn);

    /// <summary>Qué le falta para emitir. `Faltan` va en palabras, en el orden en que se hace.</summary>
    public sealed record ListoDelEmisor(
        bool? ParaEmitir, bool? Certificado, bool? Sol, string[]? Series, bool? Direccion,
        string[]? Faltan);


    /// <summary>
    /// Lo que devuelve <c>GET /v1/webhook</c>: el webhook de la API de la
    /// empresa. Nunca el secreto. `OtrosWebhooksActivos` cuenta los
    /// registrados a mano en el panel del servicio.
    /// </summary>
    public sealed record WebhookDelServicio(
        bool Registrado,
        Guid? Id,
        string? Url,
        int? Pendientes,
        int? Agotados,
        int? OtrosWebhooksActivos,
        UltimaEntregaDelServicio? UltimaEntrega);

    public sealed record UltimaEntregaDelServicio(
        string? Tipo,
        string? Estado,
        int? Codigo,
        string? Error,
        DateTimeOffset? CreadoEn,
        DateTimeOffset? EntregadoEn);

    /// <summary>
    /// Lo que devuelve <c>POST /v1/webhook</c>. `Secreto` es el `whsec_`
    /// nuevo, EN CLARO: se cifra en cuanto llega y no se registra en ningún
    /// sitio.
    /// </summary>
    public sealed record RegistroWebhookDelServicio(
        Guid Id,
        string? Url,
        string? Secreto,
        bool Creado)
    {
        // UN RECORD SE IMPRIME ENTERO, secreto incluido, en cuanto alguien lo
        // pone en un log o en un mensaje de error. Este no.
        public override string ToString() =>
            $"RegistroWebhookDelServicio {{ Id = {Id}, Url = {Url}, Creado = {Creado} }}";
    }

    /// <summary>Lo que devuelve <c>POST /v1/webhook/reintentar</c>.</summary>
    public sealed record ReintentoDelServicio(int Reencoladas, int Dias);
}