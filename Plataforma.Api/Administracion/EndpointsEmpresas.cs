using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Plataforma.Api.Arranque;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Api.Invitaciones;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

/// <summary>
/// EL ALTA CREA TAMBIÉN EL EMISOR EN EL SERVICIO (parte C, diseño §13).
///
/// El RUC y la razón social se teclean UNA vez, aquí, y viajan al servicio;
/// nada se copia a mano. La dirección fiscal también viaja, y NO se queda
/// aquí: vive en el servicio, que es quien la pone en los comprobantes.
///
/// `Nombre` es el nombre para mostrar. Si no va, se usa el nombre comercial
/// o, si tampoco, la razón social.
/// </summary>
public record PeticionAltaEmpresa(
    string? Nombre,
    string? Ruc,
    string? RazonSocial,
    string? NombreComercial,
    string? Direccion,
    string? Ubigeo,
    string? Distrito,
    string? Provincia,
    string? Departamento,
    string? CorreoAdministrador,
    string? NombreAdministrador,
    string[]? Modulos,

    // Horas que durarán los enlaces de esta empresa. Si no va, la base pone 24.
    int? HorasInvitacion);

public record PeticionHorasInvitacion(int Horas);

public record PeticionSuspension(string Motivo);

public record PeticionNombreEmpresa(string Nombre);


/// <summary>
/// El panel del super administrador: dar de alta clientes y decidir qué
/// módulos tienen.
///
/// TODO LO DE AQUÍ LLEVA SoloSuperAdmin. Un administrador de empresa no entra
/// ni a mirar: si pudiera, tarde o temprano se activaría un módulo que no
/// paga, o vería la lista de tus otros clientes.
/// </summary>
public static class EndpointsEmpresas
{
    public static void MapearEmpresas(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/admin/empresas").WithTags("Administración");


        // =====================================================================
        // EL ALTA DE UN CLIENTE, ENTERA, EN UNA SOLA LLAMADA — Y SU EMISOR
        //
        // Crea la empresa, le activa los módulos contratados, le deja un
        // administrador con su invitación, y —desde la parte C— crea su emisor
        // en el servicio de facturación y guarda la fac_ y el whsec_ que este
        // devuelve. El RUC se teclea una vez y nada se copia.
        //
        // EL INVARIANTE: NUNCA UNA EMPRESA AQUÍ SIN SU EMISOR ALLÍ. Por eso el
        // orden no es intercambiable (diseño §13):
        //
        //   1. Validar TODO aquí: RUC (dígito verificador), razón social,
        //      dirección, nombre, correo libre, módulos, RUC sin empresa activa.
        //      Un dedazo se dice aquí y no deja nada creado en ningún lado.
        //   2. Candado por RUC, para dos altas a la vez del mismo.
        //   3. POST /alta/emisores con la clave de alta: el servicio crea, en
        //      una transacción, emisor + fac_ + webhook, «sin confirmar».
        //      Si dice que no (409 el RUC ya existe, 429 tope del día…), se
        //      para aquí y no queda nada en ninguno de los dos.
        //   4. Guardar todo aquí, en un punto de guardado.
        //   5. La invitación, después de guardar.
        //
        // LA CONFIRMACIÓN VA EN OTRA PETICIÓN (POST …/facturacion/confirmar-alta),
        // que la pantalla hace sola al terminar: la transacción de
        // MiddlewareContexto se confirma DESPUÉS de este endpoint, y confirmar
        // antes sería decirle al servicio «ya está guardado» cuando aún podría
        // deshacerse. Si la pantalla se cierra antes, confirman «Volver a
        // preguntar al servicio» o la revisión diaria; mientras tanto el
        // emisor está «sin confirmar», que no rompe nada.
        //
        // SI EL PASO 4 FALLA, el emisor queda «sin confirmar» en el servicio y
        // aquí no queda nada. Repetir el alta pasados dos minutos lo da por
        // abandonado y crea otro; antes, el servicio contesta «en curso».
        //
        // SIEMPRE EN BETA, con clave fac_test_. Producción es una decisión a
        // mano en el servicio, y se conecta después con «Comprobar → Conectar».
        //
        // La facturación sigue SIN activarse aquí: hace falta certificado,
        // usuario SOL, una serie y la dirección revisada (ver el PUT de módulos).
        // =====================================================================
        grupo.MapPost("/", async (
            PeticionAltaEmpresa p,
            ContextoPlataforma db,
            ContextoPeticion ctx,
            ServicioInvitaciones invitaciones,
            ClienteFacturacion facturacion,
            OpcionesServicioFacturacion servicio,
            Cifrador cifrador,
            UrlApi urlApi,
            ILoggerFactory logs,
            CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Administracion.Alta");

            // --- 1. Todo lo que se puede comprobar sin el servicio ------------

            var ruc = (p.Ruc ?? "").Trim();
            if (ReglasEmisor.ProblemaRuc(ruc) is { } errorRuc)
                return Results.BadRequest(new { error = errorRuc, campo = "ruc" });

            var razon = Limpio(p.RazonSocial);
            if (razon.Length is < 3 or > 300)
                return Results.BadRequest(new
                {
                    error = "Falta la razón social, tal como sale en la ficha RUC.",
                    campo = "razonSocial"
                });

            var comercial = Limpio(p.NombreComercial) is { Length: > 0 } nc ? nc : null;
            if (comercial is { Length: > 300 })
                return Results.BadRequest(new { error = "El nombre comercial es demasiado largo.", campo = "nombreComercial" });

            var (direccion, errorDireccion) = ReglasEmisor.Revisar(
                p.Ubigeo, p.Direccion, p.Distrito, p.Provincia, p.Departamento);

            if (direccion is null)
                return Results.BadRequest(new { error = errorDireccion, campo = "direccion" });

            var nombre = Limpio(p.Nombre) is { Length: > 0 } n ? n : comercial ?? razon;
            if (RevisarNombre(nombre) is { } errorNombre)
                return Results.BadRequest(new { error = errorNombre, campo = "nombre" });

            var correo = (p.CorreoAdministrador ?? "").Trim();
            if (correo.Length is < 3 or > 254 || !correo.Contains('@') || correo.Any(char.IsWhiteSpace))
                return Results.BadRequest(new { error = "Falta el correo del administrador, o no es un correo.", campo = "correoAdministrador" });

            var modulos = (p.Modulos ?? []).Distinct().ToArray();

            // EL MÓDULO DE FACTURACIÓN NO SE DA EN EL ALTA: el emisor recién
            // creado no tiene certificado, ni usuario SOL, ni series. Se activa
            // cuando los tenga (diseño §13). Así nunca hay un rato en que el
            // cliente ve Facturación y todo le falla.
            if (modulos.Contains(Core.Dominio.Modulos.Facturacion))
                return Results.BadRequest(new
                {
                    error = "La facturación se activa cuando el emisor tenga certificado, usuario SOL, " +
                            "una serie y la dirección revisada; no en el alta."
                });

            foreach (var codigo in modulos)
                if (!await db.Modulos.AnyAsync(m => m.Codigo == codigo && m.Disponible, ct))
                    return Results.BadRequest(new { error = $"El módulo '{codigo}' no existe." });

            // Por la función y no con EF: el correo es único globalmente y el
            // aislamiento solo deja ver la propia empresa. Aquí quien llama es
            // el super administrador y lo vería igual, pero se usa la misma
            // puerta que el resto del sistema para que haya una sola forma de
            // hacerlo — y no dos que se comporten distinto.
            var disponible = await db.Database
                .SqlQuery<bool>($"SELECT correo_disponible({correo}) AS \"Value\"")
                .FirstAsync(ct);

            if (!disponible)
                return Results.Conflict(new { error = $"Ya hay un usuario con el correo {correo}.", campo = "correoAdministrador" });

            if (await EmpresaConRucAsync(db, ruc, ct) is { } yaEsta)
                return Results.Conflict(new
                {
                    error = $"El RUC {ruc} ya es de {yaEsta}, aquí. No se creó nada.",
                    campo = "ruc",
                    nadaCreado = true
                });

            if (servicio.ProblemaAlta() is { } faltaConfig)
                return Results.Json(new { error = faltaConfig, nadaCreado = true },
                                    statusCode: StatusCodes.Status503ServiceUnavailable);

            // --- 2. Un alta por RUC a la vez ----------------------------------
            //
            // Candado de la transacción: se suelta solo al terminar. Y se vuelve
            // a mirar el RUC con él puesto, por si otra alta acaba de terminar.
            var llave = "alta-ruc:" + ruc;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({llave}::text))", ct);

            if (await EmpresaConRucAsync(db, ruc, ct) is { } recien)
                return Results.Conflict(new
                {
                    error = $"El RUC {ruc} acaba de darse de alta como {recien}. No se creó nada.",
                    campo = "ruc",
                    nadaCreado = true
                });

            // --- 3. El emisor, en el servicio ---------------------------------
            //
            // El id de la empresa se decide AQUÍ, antes de guardarla: la URL
            // del webhook lo lleva, y el servicio la registra en el mismo paso.
            var empresaId  = Guid.NewGuid();
            var urlWebhook = EndpointsCredenciales.UrlWebhook(urlApi, empresaId);

            var r = await facturacion.CrearEmisorConClaveAltaAsync<AltaDelServicio>(
                servicio.BaseUrl, servicio.ClaveAlta, new
                {
                    ruc,
                    razonSocial     = razon,
                    nombreComercial = comercial,
                    direccion       = direccion.Direccion,
                    ubigeo          = direccion.Ubigeo,
                    distrito        = direccion.Distrito,
                    provincia       = direccion.Provincia,
                    departamento    = direccion.Departamento,
                    webhookUrl      = urlWebhook
                }, ct);

            if (!r.Exito)
                return FalloDelAlta(r);

            if (r.Datos is not { } alta ||
                alta.Clave is not { Length: >= 16 } clave || !clave.StartsWith("fac_", StringComparison.Ordinal) ||
                alta.Webhook?.Secreto is not { Length: >= 30 } secreto || !secreto.StartsWith("whsec_", StringComparison.Ordinal))
            {
                log.LogError("El servicio creó el emisor del RUC {Ruc} pero la respuesta no trae clave o secreto válidos.", ruc);

                return Results.Json(new
                {
                    error = "El servicio respondió sin la clave o sin el secreto, así que aquí no se guardó nada.",
                    detalle = "Allí el emisor quedó «sin confirmar». Repite el alta pasados dos minutos: " +
                              "el servicio lo dará por abandonado y creará otro.",
                    nadaCreado = true
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            // --- 4. Todo aquí, de una vez -------------------------------------
            var transaccion = db.Database.CurrentTransaction
                ?? throw new InvalidOperationException("El alta necesita la transacción de MiddlewareContexto.");

            await transaccion.CreateSavepointAsync(PuntoAlta, ct);

            var ahora = DateTime.UtcNow;
            Empresa empresa;
            Usuario admin;

            try
            {
                empresa = new Empresa
                {
                    Id          = empresaId,
                    Nombre      = nombre,
                    Ruc         = ruc,
                    RazonSocial = razon,
                    Ambiente    = DatosOficiales.Ambiente(alta.Ambiente) ?? "beta",
                    DatosOficialesEn = ahora,

                    // Si no lo mandan, la base pone 24. El rango lo comprueba
                    // también un CHECK: esto es comodidad, no la validación.
                    HorasInvitacion = p.HorasInvitacion is >= 1 and <= 168
                                      ? p.HorasInvitacion.Value
                                      : 24
                };
                db.Empresas.Add(empresa);

                foreach (var codigo in modulos)
                {
                    db.EmpresaModulos.Add(new EmpresaModulo
                    {
                        Empresa       = empresa,
                        ModuloCodigo  = codigo,
                        ContratadoEn  = ahora
                    });
                }

                admin = new Usuario
                {
                    Empresa = empresa,
                    Correo  = correo,
                    Nombre  = p.NombreAdministrador?.Trim() is { Length: > 0 } na ? na : "Administrador",

                    // Una contraseña que no conoce nadie. La suya la elige él al
                    // abrir el enlace que le llega por correo.
                    ClaveHash          = HashDeClaves.Cifrar(GeneradorTokens.ClaveTemporal(32)),
                    ClaveCambioForzado = true
                };
                db.Usuarios.Add(admin);

                db.UsuarioRoles.Add(new UsuarioRol
                {
                    Usuario = admin,
                    RolId   = RolesDeSistema.Administrador
                });

                // LA CREDENCIAL, con lo que acaba de dar el servicio. Los dos
                // secretos se cifran aquí y no vuelven a salir: ni en la
                // respuesta, ni en un log.
                db.CredencialesFacturacion.Add(new CredencialFacturacion
                {
                    Empresa      = empresa,
                    BaseUrl      = servicio.BaseUrl,
                    ClaveCifrada = cifrador.Cifrar(clave),
                    Pista        = clave[^4..],

                    WebhookSecretoCifrado = cifrador.Cifrar(secreto),
                    WebhookUrlServicio    = alta.Webhook.Url ?? urlWebhook,
                    WebhookRevisadoEn     = ahora,

                    UltimaLlamadaEn = ahora,
                    CreadaPorAlta   = true
                });

                await db.SaveChangesAsync(ct);

                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    SELECT registrar_evento('otro'::text, true, NULL, {empresa.Id}::uuid, NULL,
                        jsonb_build_object('accion','alta_empresa','nombre',{empresa.Nombre}::text,
                                           'ruc',{ruc}::text,'emisor',{alta.EmisorId.ToString()}::text))
                    """, ct);
            }
            catch (Exception e) when (e is DbUpdateException or PostgresException)
            {
                await transaccion.RollbackToSavepointAsync(PuntoAlta, ct);
                db.ChangeTracker.Clear();

                var unico = (e as PostgresException ?? e.InnerException as PostgresException)
                    is { SqlState: PostgresErrorCodes.UniqueViolation } pe ? pe.ConstraintName : null;

                log.LogError(e, "El servicio creó el emisor {Emisor} del RUC {Ruc} pero la plataforma no pudo " +
                                "guardar la empresa. Allí queda «sin confirmar».", alta.EmisorId, ruc);

                return Results.Json(new
                {
                    error = "El emisor se creó en el servicio, pero la empresa no se pudo guardar aquí, " +
                            "así que aquí no queda nada.",
                    detalle = (unico is null ? "" : $"Choque con {unico}. ") +
                              "En el servicio el emisor queda «sin confirmar». Repite el alta pasados dos " +
                              "minutos: lo dará por abandonado y creará otro (antes, dirá «en curso»).",
                    nadaCreado = true
                }, statusCode: StatusCodes.Status500InternalServerError);
            }

            log.LogInformation("Alta: {Nombre} (RUC {Ruc}) creada aquí y en el servicio (emisor {Emisor}, beta){Abandonado}.",
                               nombre, ruc, alta.EmisorId,
                               alta.Abandonado is { } ab ? $"; el alta anterior sin confirmar {ab} quedó abandonada" : "");

            // --- 5. EL ENLACE SE MANDA DESPUÉS DE GUARDAR, no antes ------------
            //
            // Si fallara el correo, la empresa ya existe y el enlace se reenvía
            // con un clic; al revés, se habría mandado una invitación a una
            // cuenta que la transacción podría no haber llegado a crear.
            var invitacion = await invitaciones.EnviarAsync(admin.Id, ct);

            return Results.Created($"/api/admin/empresas/{empresa.Id}", new
            {
                empresa = new { empresa.Id, empresa.Nombre, empresa.Ruc, empresa.RazonSocial, empresa.Ambiente },
                administrador = new
                {
                    admin.Id,
                    admin.Correo,
                    admin.Nombre,
                    invitacionEnviada = invitacion.Enviado,
                    invitacion.ExpiraEn,
                    invitacion.EnlaceDePrueba
                },
                emisor = new
                {
                    id = alta.EmisorId,
                    estado = "sin_confirmar",
                    ambiente = alta.Ambiente ?? "beta",
                    pista = clave[^4..],
                    abandonado = alta.Abandonado
                },

                // La pantalla la llama sola, en cuanto recibe esto.
                siguiente = $"POST /api/admin/empresas/{empresa.Id}/facturacion/confirmar-alta",

                mensaje = $"{empresa.Nombre} creada aquí y en el servicio (beta). Para activar Facturación " +
                          "le faltan, en el panel del servicio: certificado digital, usuario SOL, al menos " +
                          "una serie y, si hace falta, el correo de respuesta del portal."
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Dar de alta una empresa con su administrador y su emisor en el servicio");


        // =====================================================================
        grupo.MapGet("/", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            // IgnoreQueryFilters: el super administrador tiene que ver también
            // las empresas desactivadas. Son las que va a querer mirar cuando
            // alguien pregunte por qué no puede entrar.
            var empresas = await db.Empresas
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderBy(e => e.Nombre)
                .Select(e => new
                {
                    // El NOMBRE es el que se ve. El RUC y la razón social son
                    // los oficiales, del servicio: nulos hasta conectar.
                    e.Id, e.Nombre, e.Ruc, e.RazonSocial, e.Ambiente,
                    conectada = e.Ruc != null,
                    e.Activo, e.SuspendidaEn, e.MotivoSuspension,
                    e.HorasInvitacion,
                    usuarios = db.Usuarios.Count(u => u.EmpresaId == e.Id && u.Activo),
                    modulos  = db.EmpresaModulos
                                 .Where(m => m.EmpresaId == e.Id && m.Activo)
                                 .Select(m => m.ModuloCodigo)
                                 .ToList()
                })
                .ToListAsync(ct);

            // LAS MARCAS DE FACTURACIÓN de cada empresa, de lo último que dijo
            // el servicio (migración 015). Sin llamar al servicio: la lista
            // tiene que abrir aunque esté apagado.
            var marcas = await MarcasFacturacionAsync(db, ct);

            return Results.Ok(empresas.Select(e => new
            {
                e.Id, e.Nombre, e.Ruc, e.RazonSocial, e.Ambiente, e.conectada,
                e.Activo, e.SuspendidaEn, e.MotivoSuspension, e.HorasInvitacion,
                e.usuarios, e.modulos,
                facturacion = marcas.GetValueOrDefault(e.Id)
            }));
        })
        .SoloSuperAdmin()
        .WithSummary("Listar empresas");


        // =====================================================================
        // LOS USUARIOS DE UNA EMPRESA
        //
        // EL ÚNICO SITIO DE LA PLATAFORMA DONDE ALGUIEN LEE LOS USUARIOS DE OTRA
        // EMPRESA, y existe para una sola cosa: elegir a quién suplantar cuando
        // un cliente llama con un problema.
        //
        // No es la pantalla de gestión de usuarios —esa es `/api/usuarios`, y es
        // del administrador del cliente, no de aquí. Esta devuelve lo justo para
        // reconocer a una persona en una lista: nombre, correo, roles y estado.
        // Nada de fechas de contraseña ni de sesiones.
        //
        // El `Where` por empresa es de verdad necesario aquí, al contrario que
        // en el resto del sistema: el aislamiento no recorta nada para un super
        // administrador, así que sin él saldrían los usuarios de todos.
        // =====================================================================
        grupo.MapGet("/{id:guid}/usuarios", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.Id == id)
                .Select(e => new { e.Nombre, e.Activo, e.SuspendidaEn })
                .FirstOrDefaultAsync(ct);

            if (empresa is null) return Results.NotFound();

            var usuarios = await db.Usuarios
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.EmpresaId == id)
                .OrderByDescending(u => u.Activo)
                .ThenBy(u => u.Nombre)
                .Select(u => new
                {
                    u.Id, u.Nombre, u.Correo, u.Activo,
                    u.DosfaActivo, u.UltimoIngresoEn,
                    roles = db.UsuarioRoles
                              .Where(r => r.UsuarioId == u.Id && r.Activo)
                              .Select(r => r.Rol.Nombre)
                              .ToList()
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                empresa = empresa.Nombre,

                // Suplantando a alguien de una empresa suspendida no se vería
                // nada útil: el aislamiento la esconde entera. La pantalla lo
                // usa para decirlo antes de que nadie lo intente.
                operativa = empresa.Activo && empresa.SuspendidaEn is null,
                usuarios
            });
        })
        .SoloSuperAdmin()
        .WithSummary("Listar los usuarios de una empresa");


        // =====================================================================
        grupo.MapPost("/{id:guid}/suspender", async (
            Guid id, PeticionSuspension p,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var empresa = await db.Empresas.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (empresa is null) return Results.NotFound();

            empresa.SuspendidaEn     = DateTime.UtcNow;
            empresa.MotivoSuspension = p.Motivo;

            // Las sesiones abiertas se cortan ahora, no cuando caduquen. Una
            // suspensión que tarda doce horas en notarse no es una suspensión.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE sesiones
                   SET revocada_en = now(), motivo_revocacion = 'empresa suspendida'
                 WHERE empresa_id = {id} AND revocada_en IS NULL
                """, ct);

            await db.SaveChangesAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento({TipoEvento.EmpresaSuspendida}::text, true,
                    NULL, {id}::uuid, NULL,
                    jsonb_build_object('motivo', {p.Motivo}::text))
                """, ct);

            return Results.Ok(new { mensaje = "Empresa suspendida y sesiones cerradas." });
        })
        .SoloSuperAdmin()
        .WithSummary("Suspender una empresa");


        // =====================================================================
        grupo.MapPost("/{id:guid}/reactivar", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var empresa = await db.Empresas.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (empresa is null) return Results.NotFound();

            // SU RUC PUDO QUEDAR EN OTRA EMPRESA mientras estaba desactivada:
            // desactivar libera el RUC (migración 014). Reactivarla sin mirar
            // chocaría contra `ux_empresas_ruc` con un error que no explica nada.
            if (!empresa.Activo && empresa.Ruc is { } ruc)
            {
                var otra = await db.Empresas.AsNoTracking()
                    .Where(e => e.Id != id && e.Ruc == ruc)
                    .Select(e => e.Nombre)
                    .FirstOrDefaultAsync(ct);

                if (otra is not null)
                    return Results.Conflict(new
                    {
                        error = $"Mientras estaba desactivada, el RUC {ruc} pasó a {otra}. " +
                                "Desactiva esa primero si es la que sobra."
                    });
            }

            empresa.SuspendidaEn     = null;
            empresa.MotivoSuspension = null;
            empresa.Activo           = true;

            await db.SaveChangesAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT registrar_evento({TipoEvento.EmpresaReactivada}::text, true,
                                        NULL, {id}::uuid, NULL, NULL)
                """, ct);

            return Results.Ok(new { mensaje = "Empresa reactivada." });
        })
        .SoloSuperAdmin()
        .WithSummary("Reactivar una empresa");


        // =====================================================================
        // CAMBIAR EL NOMBRE PARA MOSTRAR
        //
        // Solo el nombre. El RUC y la razón social son del servicio: el RUC no
        // cambia nunca, y la razón social se refresca sola al preguntarle.
        // =====================================================================
        grupo.MapPut("/{id:guid}/nombre", async (
            Guid id, PeticionNombreEmpresa p,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            if (RevisarNombre(p.Nombre) is { } error)
                return Results.BadRequest(new { error });

            var empresa = await db.Empresas.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (empresa is null) return Results.NotFound();

            empresa.Nombre = p.Nombre.Trim();
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { empresa.Nombre, mensaje = "Nombre actualizado." });
        })
        .SoloSuperAdmin()
        .WithSummary("Cambiar el nombre para mostrar de una empresa");


        // =====================================================================
        // EL PLAZO DE LOS ENLACES, POR EMPRESA
        //
        // Una oficina que revisa el correo cada mañana necesita más margen que
        // otra donde el administrador llama por teléfono antes de crear la
        // cuenta. Por eso no es un número global.
        //
        // El rango lo comprueban los dos lados: aquí para dar un mensaje que
        // se entienda, y un CHECK en la base para que siga siendo cierto aunque
        // alguien llame al endpoint desde fuera de la aplicación.
        // =====================================================================
        grupo.MapPut("/{id:guid}/invitacion", async (
            Guid id, PeticionHorasInvitacion p,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            if (p.Horas is < 1 or > 168)
                return Results.BadRequest(new
                {
                    error = "El plazo tiene que estar entre 1 hora y 168 (siete días)."
                });

            var empresa = await db.Empresas.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (empresa is null) return Results.NotFound();

            empresa.HorasInvitacion = p.Horas;
            await db.SaveChangesAsync(ct);

            // Los enlaces ya emitidos conservan su fecha: se calculó al
            // crearlos. Cambiar el plazo no alarga ni acorta lo que ya salió.
            return Results.Ok(new { mensaje = $"Los enlaces durarán {p.Horas} horas." });
        })
        .SoloSuperAdmin()
        .WithSummary("Cambiar el plazo de los enlaces de una empresa");


        // =====================================================================
        // ACTIVAR Y DESACTIVAR MÓDULOS
        //
        // Desactivar no borra la fila: la deja con `activo = false`. Así, el
        // día que vuelvan a contratarlo, su configuración sigue ahí — y queda
        // escrito en la bitácora quién lo quitó y cuándo.
        // =====================================================================
        grupo.MapPut("/{id:guid}/modulos/{codigo}", async (
            Guid id, string codigo,
            ContextoPlataforma db, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (!await db.Empresas.IgnoreQueryFilters().AnyAsync(e => e.Id == id, ct))
                return Results.NotFound(new { error = "No existe esa empresa." });

            if (!await db.Modulos.AnyAsync(m => m.Codigo == codigo && m.Disponible, ct))
                return Results.NotFound(new { error = $"No existe el módulo '{codigo}'." });

            // SIN CONECTAR NO HAY FACTURACIÓN (diseño §4): sin RUC oficial y
            // sin el secreto del webhook, el cliente vería la pantalla y todo
            // le fallaría —o emitiría y nunca sabría lo que dijo SUNAT—.
            var existente = await db.EmpresaModulos
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.EmpresaId == id && m.ModuloCodigo == codigo, ct);

            // YA ACTIVO: no se vuelve a exigir nada. Lo de abajo se exige AL
            // ACTIVAR, no con efecto retroactivo (decisión del 11 de octubre):
            // a una empresa que ya factura no se le quita el módulo por esto,
            // solo ve el aviso ámbar.
            if (existente is { Activo: true })
                return Results.Ok(new { mensaje = $"El módulo '{codigo}' ya estaba activo." });

            if (codigo == Core.Dominio.Modulos.Facturacion &&
                await FacturacionNoListaAsync(db, facturacion, id, ct) is { } motivo)
                return motivo;

            if (existente is null)
            {
                db.EmpresaModulos.Add(new EmpresaModulo
                {
                    EmpresaId    = id,
                    ModuloCodigo = codigo,
                    ContratadoEn = DateTime.UtcNow
                });
            }
            else
            {
                existente.Activo  = true;
                existente.VenceEn = null;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = $"Módulo '{codigo}' activado." });
        })
        .SoloSuperAdmin()
        .WithSummary("Activar un módulo para una empresa");


        // =====================================================================
        grupo.MapDelete("/{id:guid}/modulos/{codigo}", async (
            Guid id, string codigo,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var modulo = await db.EmpresaModulos
                .FirstOrDefaultAsync(m => m.EmpresaId == id && m.ModuloCodigo == codigo, ct);

            if (modulo is null) return Results.NotFound();

            modulo.Activo = false;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { mensaje = $"Módulo '{codigo}' desactivado." });
        })
        .SoloSuperAdmin()
        .WithSummary("Quitar un módulo a una empresa");
    }


    private const string PuntoAlta = "alta_empresa";

    private static string Limpio(string? s) =>
        string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>El nombre de la empresa ACTIVA que ya tiene ese RUC, o null.</summary>
    private static Task<string?> EmpresaConRucAsync(ContextoPlataforma db, string ruc, CancellationToken ct) =>
        db.Empresas.AsNoTracking()          // el filtro global deja solo las activas
          .Where(e => e.Ruc == ruc)
          .Select(e => e.Nombre)
          .FirstOrDefaultAsync(ct);


    /// <summary>
    /// Un «no» del servicio al crear el emisor, para la pantalla. En todos los
    /// casos, NO SE CREÓ NADA: ni allí ni aquí.
    /// </summary>
    private static IResult FalloDelAlta(ResultadoFacturacion<AltaDelServicio> r)
    {
        var (estado, error) = r.Estado switch
        {
            0   => (StatusCodes.Status502BadGateway,
                    "No se pudo conectar con el servicio de facturación. No se creó nada."),
            401 => (StatusCodes.Status502BadGateway,
                    "El servicio no acepta la clave de alta (FACTURACION_CLAVE_ALTA): está revocada o mal " +
                    "copiada. No se creó nada."),
            404 => (StatusCodes.Status502BadGateway,
                    "El servicio no tiene la ruta de alta. ¿Está actualizado (parte C1)? No se creó nada."),
            409 => (StatusCodes.Status409Conflict, r.Error ?? "El servicio dice que ese RUC ya existe."),
            429 => (StatusCodes.Status429TooManyRequests, r.Error ?? "La clave de alta llegó a su tope de hoy."),
            400 => (StatusCodes.Status400BadRequest, r.Error ?? "El servicio rechazó los datos."),
            _   => (StatusCodes.Status502BadGateway, r.Error ?? $"El servicio respondió {r.Estado}.")
        };

        return Results.Json(new { error, detalle = r.Detalle, nadaCreado = true }, statusCode: estado);
    }


    /// <summary>
    /// ¿Puede activarse Facturación? Null si sí; si no, la respuesta que lo
    /// explica (diseño §13).
    ///
    /// SE PREGUNTA AL SERVICIO EN EL MOMENTO, no a lo guardado: activar es
    /// justo cuando importa que sea verdad. Hace falta:
    ///
    ///   · estar conectada (RUC) y con el secreto del webhook;
    ///   · que la clave sea de esta empresa (mismo RUC);
    ///   · certificado vigente, usuario SOL de verdad (no MODDATOS), al menos
    ///     una serie y la dirección revisada — lo que el servicio llama
    ///     `listo.paraEmitir`.
    ///
    /// De paso guarda lo que dijo, para que la ventana lo enseñe, y confirma
    /// un alta que se hubiera quedado sin confirmar.
    /// </summary>
    private static async Task<IResult?> FacturacionNoListaAsync(
        ContextoPlataforma db, ClienteFacturacion facturacion, Guid id, CancellationToken ct)
    {
        var ruc = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == id).Select(e => e.Ruc).FirstOrDefaultAsync(ct);

        var conSecreto = await db.CredencialesFacturacion
            .AnyAsync(c => c.EmpresaId == id && c.WebhookSecretoCifrado != null, ct);

        if (ruc is null || !conSecreto)
            return Results.BadRequest(new
            {
                error = "Primero conecta la clave del servicio (Facturación → Comprobar → Conectar)."
            });

        var r = await facturacion.LeerComoEmpresaAsync<EndpointsCredenciales.EmisorDelServicio>(id, "/v1/emisor", ct);

        if (!r.Exito || r.Datos is null)
            return Results.Json(new
            {
                error = "No se pudo preguntar al servicio si la empresa puede emitir, así que no se activó.",
                detalle = r.Error is null ? r.Detalle : $"{r.Error} {r.Detalle}".Trim()
            }, statusCode: StatusCodes.Status502BadGateway);

        var emisor = r.Datos;

        if (!string.Equals(emisor.Ruc?.Trim(), ruc, StringComparison.Ordinal))
            return Results.Conflict(new
            {
                error = $"La clave guardada responde con el RUC {emisor.Ruc} y esta empresa es {ruc}. " +
                        "Es la clave de otro cliente: no se activó."
            });

        if (emisor.Alta?.Estado == "sin_confirmar" &&
            await EstadoDelEmisor.ConfirmarAsync(db, facturacion, id, ct) == "confirmada")
        {
            var otra = await facturacion.LeerComoEmpresaAsync<EndpointsCredenciales.EmisorDelServicio>(id, "/v1/emisor", ct);
            if (otra.Exito && otra.Datos is { } nuevo) emisor = nuevo;
        }

        await CertificadoDelEmisor.GuardarAsync(db, id, emisor.CertificadoValidoHasta, ct);
        await EstadoDelEmisor.GuardarAsync(db, id, emisor, ct);

        if (emisor.Listo is null)
            return Results.Json(new
            {
                error = "El servicio no dice qué le falta a la empresa para emitir. ¿Está actualizado (parte C1)?"
            }, statusCode: StatusCodes.Status502BadGateway);

        if (emisor.Listo.ParaEmitir != true)
            return Results.BadRequest(new
            {
                error = "Todavía no se puede activar Facturación: al emisor le falta algo en el servicio.",
                faltan = emisor.Listo.Faltan ?? []
            });

        return null;
    }


    /// <summary>
    /// Las marcas de facturación de cada empresa para la lista, de la
    /// credencial ACTIVA y de lo último que dijo el servicio. Por SQL: lee el
    /// jsonb de `emisor_estado`, que EF no conoce.
    /// </summary>
    private static async Task<Dictionary<Guid, MarcasFacturacion>> MarcasFacturacionAsync(
        ContextoPlataforma db, CancellationToken ct)
    {
        var conexion = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaccion = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();

        await using var cmd = new NpgsqlCommand("""
            SELECT c.empresa_id,
                   c.creada_por_alta AND c.alta_confirmada_en IS NULL,
                   c.emisor_estado #>> '{alta,estado}',
                   (c.emisor_estado #>> '{portal,correosAlReceptor}')::boolean,
                   (c.emisor_estado #>> '{listo,paraEmitir}')::boolean,
                   EXISTS (SELECT 1
                             FROM credenciales_facturacion v
                            WHERE v.empresa_id = c.empresa_id
                              AND NOT v.activo
                              AND v.clave_cifrada IS NOT NULL),
                   c.emisor_estado_en
              FROM credenciales_facturacion c
             WHERE c.activo
            """, conexion, transaccion);

        var marcas = new Dictionary<Guid, MarcasFacturacion>();

        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            var estadoAlta = r.IsDBNull(2) ? null : r.GetString(2);

            marcas[r.GetGuid(0)] = new MarcasFacturacion(
                AltaSinConfirmar:       r.GetBoolean(1) || estadoAlta == "sin_confirmar",
                CorreosAlReceptor:      r.IsDBNull(3) ? null : r.GetBoolean(3),
                ListoParaEmitir:        r.IsDBNull(4) ? null : r.GetBoolean(4),
                ClaveAnteriorPendiente: r.GetBoolean(5),
                ConsultadoEn:           r.IsDBNull(6) ? null : r.GetDateTime(6));
        }

        return marcas;
    }

    /// <summary>
    /// Para la lista. Los nulos son «todavía no se preguntó al servicio»: la
    /// pantalla no pinta nada, ni bueno ni malo.
    /// </summary>
    public sealed record MarcasFacturacion(
        bool AltaSinConfirmar,
        bool? CorreosAlReceptor,
        bool? ListoParaEmitir,
        bool ClaveAnteriorPendiente,
        DateTime? ConsultadoEn);


    /// <summary>El nombre para mostrar: entre 1 y 200 caracteres. La base lo comprueba también.</summary>
    private static string? RevisarNombre(string? nombre)
    {
        var n = nombre?.Trim() ?? "";

        if (n.Length == 0)
            return "Falta el nombre de la empresa. Es el que verá todo el mundo en la plataforma; " +
                   "el oficial es la razón social.";

        if (n.Length > 200)
            return "El nombre es demasiado largo (máximo 200 caracteres).";

        return null;
    }
}


/// <summary>
/// Los identificadores de los tres roles que crea la migración 006.
///
/// Están fijos en la migración y por eso pueden estar fijos aquí: son los
/// mismos en cualquier base, la de desarrollo y la de producción.
/// </summary>
public static class RolesDeSistema
{
    public static readonly Guid Administrador = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Facturador    = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Consulta      = Guid.Parse("33333333-3333-3333-3333-333333333333");
}