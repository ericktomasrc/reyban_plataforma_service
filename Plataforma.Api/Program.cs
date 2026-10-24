using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Acceso;
using Plataforma.Api.Administracion;
using Plataforma.Api.Arranque;
using Plataforma.Api.Avisos;
using Plataforma.Api.Cartera;
using Plataforma.Api.Consultas;
using Plataforma.Api.Configuracion;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Guias;
using Plataforma.Api.Inicio;
using Plataforma.Api.Recuperacion;
using Plataforma.Api.Reportes;
using Plataforma.Api.Traspasos;
using Plataforma.Api.Auditoria;
using Plataforma.Api.Invitaciones;
using Plataforma.Api.Middleware;
using Plataforma.Api.Registro;
using Plataforma.Api.Sesion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

// El .env se lee ANTES de construir nada: la cadena de conexión y la llave
// maestra salen de ahí. En producción no hay .env y las variables las pone el
// contenedor; el cargador no pisa lo que ya esté definido.
Entorno.CargarDotEnv();

var constructor = WebApplication.CreateBuilder(args);

// 038: LOGS EN ARCHIVO, una carpeta por día: ..\logs\AAAA-MM-DD\plataforma-api.log
constructor.Logging.AddProvider(new ProveedorLogsArchivo("plataforma-api", constructor.Environment.ContentRootPath));


// --- Base de datos -----------------------------------------------------------

constructor.Services.AddDbContext<ContextoPlataforma>(opciones =>
{
    opciones.UseNpgsql(Entorno.Obligatoria("PLATAFORMA_CADENA_CONEXION"))
            // Traduce CreadoEn a creado_en y EmpresaId a empresa_id sin que
            // haya que decírselo entidad por entidad.
            .UseSnakeCaseNamingConvention();

    if (constructor.Environment.IsDevelopment())
    {
        // En desarrollo el SQL generado se ve en el log. Es la forma más
        // rápida de descubrir que una consulta no trae nada porque el
        // contexto no estaba fijado.
        opciones.EnableSensitiveDataLogging()
                .EnableDetailedErrors();
    }
});


// --- Servicios ---------------------------------------------------------------

// Una llave maestra para todo el proceso: se lee una vez y falla al arrancar
// si no está.
constructor.Services.AddSingleton(_ => LlaveMaestra.Leer());
constructor.Services.AddSingleton<Cifrador>();

// El contexto de la petición es por petición, no compartido. Cada una tiene
// el suyo: si fuera singleton, dos usuarios simultáneos se mezclarían.
constructor.Services.AddScoped<ContextoPeticion>();
constructor.Services.AddScoped<ServicioIngreso>();


// --- Correo ------------------------------------------------------------------

// Las opciones se leen UNA VEZ al arrancar, no en cada envío. Así, un .env mal
// escrito se nota al levantar la aplicación y no la primera vez que alguien da
// de alta un usuario un viernes por la tarde.
constructor.Services.AddSingleton(_ => new OpcionesCorreo
{
    Servidor   = Entorno.Opcional("MAIL_HOST", "smtp.gmail.com"),
    Puerto     = Entorno.Numero("MAIL_PORT", 587),
    Usuario    = Entorno.Opcional("MAIL_USERNAME", ""),
    Clave      = Entorno.Opcional("MAIL_PASSWORD", ""),
    De         = Entorno.Opcional("MAIL_FROM", ""),
    DeNombre   = Entorno.Opcional("MAIL_FROM_NAME", "Plataforma"),
    Habilitado = Entorno.Bandera("MAIL_ENABLED"),

    // Con esto se construyen los enlaces de los correos. NO se deduce de la
    // petición: una cabecera Host falsificada haría que la plataforma mandase
    // invitaciones legítimas apuntando al servidor de otro.
    UrlBase    = Entorno.Opcional("PLATAFORMA_URL_PUBLICA", "http://localhost:5173"),
});

constructor.Services.AddScoped<IServicioCorreo, ServicioCorreo>();
constructor.Services.AddScoped<AvisosDeSeguridad>();
constructor.Services.AddScoped<ServicioInvitaciones>();
constructor.Services.AddScoped<ServicioEnlaceAcceso>();   // 038: recuperar el acceso por correo
constructor.Services.AddScoped<ServicioRecuperacion2fa>(); // 039: recuperar el segundo factor con espera

// 016 — EL CAPTCHA DEL REGISTRO (Cloudflare Turnstile). Singleton: lee sus
// claves del entorno una vez. Sin claves en desarrollo usa las de prueba; sin
// claves en producción el registro queda cerrado.
constructor.Services.AddHttpClient(Captcha.NombreCliente, c => c.Timeout = TimeSpan.FromSeconds(10));

// 049 — ENTRAR CON GOOGLE. Sin GOOGLE_CLIENT_ID y GOOGLE_CLIENT_SECRET no hay botón.
constructor.Services.AddSingleton(Plataforma.Api.Sesion.OpcionesGoogle.Leer());
constructor.Services.AddHttpClient(Plataforma.Api.Sesion.EndpointsGoogle.NombreCliente, c => c.Timeout = TimeSpan.FromSeconds(10));
constructor.Services.AddSingleton<Captcha>();

// 029 — CONSULTAS DE RUC, DNI Y TIPO DE CAMBIO (Decolecta). Sin DECOLECTA_TOKEN
// no se consulta nada y la pantalla deja escribir los datos a mano.
constructor.Services.AddSingleton(OpcionesDecolecta.Leer());
constructor.Services.AddHttpClient(ClienteDecolecta.NombreCliente, c => c.Timeout = TimeSpan.FromSeconds(8));
constructor.Services.AddScoped<ClienteDecolecta>();
constructor.Services.AddScoped<TipoCambioDiario>();   // 033: una consulta por día

// 042 — CONSULTAS DESDE SUNAT. El modo (SUNAT sin costo / Decolecta) vive en la
// base. El padrón de RUC se baja cada madrugada y se puede subir a mano; ver
// CargadorPadron. El tipo de cambio en modo SUNAT sale del BCRP (SBS).
constructor.Services.AddSingleton(OpcionesPadron.Leer());
constructor.Services.AddSingleton<ColaPadron>();
constructor.Services.AddScoped<ConsultasDocumento>();
constructor.Services.AddScoped<FuenteTipoCambio>();
constructor.Services.AddHostedService<CargadorPadron>();
constructor.Services.AddHttpClient(CargadorPadron.NombreCliente, c =>
    {
        c.Timeout = TimeSpan.FromMinutes(40);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ReybanPlataforma/1.0");
    })
    // SUNAT entrega el archivo redirigiendo de https a http: se sigue a mano.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
constructor.Services.AddHttpClient(FuenteTipoCambio.NombreCliente, c => c.Timeout = TimeSpan.FromSeconds(10));


// --- El cliente del servicio de facturación ---------------------------------
//
// POR QUÉ `AddHttpClient` Y NO UN `new HttpClient()`: el de fábrica recicla las
// conexiones TCP. Un `HttpClient` nuevo por llamada agota los puertos del
// sistema bajo carga —y el fallo aparece semanas después, en producción, con un
// error de red que no menciona en ningún sitio que el problema sea este.
//
// LA DIRECCIÓN BASE NO SE PONE AQUÍ, a propósito: es distinta por empresa y
// sale de `credenciales_facturacion`. Ponerla aquí sería volver a tener una
// sola para todos.
constructor.Services.AddScoped<ClienteFacturacion>();
constructor.Services.AddScoped<ServicioEmision>();
constructor.Services.AddSingleton(UrlApi.Leer(constructor.Environment));

// Dónde está el servicio y la clave de alta (alt_), del .env. Sin ellas la
// plataforma arranca igual: solo el alta de empresas lo dice y no crea nada.
// Ver OpcionesServicioFacturacion.
constructor.Services.AddSingleton(OpcionesServicioFacturacion.Leer());

// 030: modo prueba / real. En producción PLATAFORMA_SUNAT=real lo deja fijo.
constructor.Services.AddSingleton(OpcionesConexionSunat.Leer());

// La revisión diaria del certificado digital de cada empresa: pregunta al
// servicio hasta cuándo vale y lo guarda, para avisar antes de que venza. Se
// despierta cada hora y pregunta una vez al día por empresa. Ver
// RevisionCertificados.
constructor.Services.AddHostedService<RevisionCertificados>();

// 021: a los 30, 60 y 90 días de una pausa, un enlace nuevo al titular para
// recuperar su empresa. Ver RecordatoriosRecuperacion.
constructor.Services.AddHostedService<RecordatoriosRecuperacion>();

constructor.Services.AddHttpClient(ClienteFacturacion.NombreCliente, c =>
{
    // TREINTA SEGUNDOS, no los cien de por defecto.
    //
    // El servicio responde 202 en milisegundos porque no espera a SUNAT. Si
    // tarda treinta segundos es que no está, y esperar cien deja colgada la
    // pantalla de quien está facturando sin decirle nada.
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("User-Agent", "reyban-plataforma/1.0");
})
.ConfigurePrimaryHttpMessageHandler(sp =>
{
    var manejador = new HttpClientHandler();

    // ════════════════════════════════════════════════════════════════════════
    // SALTARSE LA COMPROBACIÓN DEL CERTIFICADO — SOLO EN DESARROLLO Y SOLO SI
    // SE PIDE A MANO.
    //
    // El servicio corre en `https://localhost:7295` con el certificado de
    // desarrollo de .NET. Si nunca se ejecutó `dotnet dev-certs https --trust`,
    // esta plataforma no se fía de él y toda llamada falla con un error de TLS
    // que no dice que el problema sea ese.
    //
    // LAS DOS CONDICIONES SON OBLIGATORIAS Y VAN JUNTAS. Con solo la variable,
    // alguien la copiaría al `.env` de producción sin darse cuenta y ahí
    // cualquiera podría ponerse en medio de la conexión y leer —o cambiar— los
    // comprobantes. Con solo el entorno, se desactivaría la comprobación para
    // todo el mundo en desarrollo, incluido quien sí tiene el certificado bien.
    //
    // LA SALIDA BUENA ES `dotnet dev-certs https --trust`, una vez y ya. Esto
    // es el apaño para cuando eso no se puede.
    // ════════════════════════════════════════════════════════════════════════
    var entorno = sp.GetRequiredService<IHostEnvironment>();

    if (entorno.IsDevelopment() && Entorno.Bandera("FACTURACION_TLS_INSEGURO"))
    {
        sp.GetRequiredService<ILoggerFactory>()
          .CreateLogger("Facturacion")
          .LogWarning("FACTURACION_TLS_INSEGURO está activo: NO se comprueba el " +
                      "certificado del servicio de facturación. Solo para desarrollo.");

        manejador.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }

    return manejador;
});

constructor.Services.AddEndpointsApiExplorer();
constructor.Services.AddOpenApi();


var app = constructor.Build();

// DETRÁS DE CADDY (producción): la IP real del cliente llega en la cabecera
// X-Forwarded-For, y SOLO se cree si la petición viene del proxy: la IP fija de
// Caddy en la red de Docker (PROXY_IP_CONFIABLE). Sin esa variable no se lee
// nada —en desarrollo no hay proxy—, porque fiarse de la cabecera dejaría a
// cualquiera inventarse su IP y saltarse los límites de intentos por IP.
var ipProxyConfiable = Environment.GetEnvironmentVariable("PROXY_IP_CONFIABLE");
if (!string.IsNullOrWhiteSpace(ipProxyConfiable))
{
    var reenvio = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                           Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1
    };
    reenvio.KnownProxies.Clear();
    foreach (var ip in ipProxyConfiable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        reenvio.KnownProxies.Add(System.Net.IPAddress.Parse(ip));
    app.UseForwardedHeaders(reenvio);
}


// --- Comprobaciones antes de la primera petición -----------------------------

await ComprobacionesArranque.ComprobarAsync(app);
await PrimerAdministrador.CrearSiHaceFaltaAsync(app);

// La llave de emergencia del super administrador que perdió su segundo factor
// y sus códigos. No hace nada salvo que PLATAFORMA_REINICIAR_2FA esté puesta.
await ReinicioSegundoFactor.EjecutarSiHaceFaltaAsync(app);


// --- La tubería, y su orden importa ------------------------------------------

if (app.Environment.IsDevelopment())
{
    // DOS PIEZAS DISTINTAS, y conviene no confundirlas:
    //
    //   OpenAPI     el estándar que describe la API. Antes se llamaba
    //               Swagger, y lo genera .NET solo, en /openapi/v1.json
    //
    //   Swagger UI  el visor que lee ese JSON y lo pinta. En .NET 9 dejó de
    //               venir en la plantilla, así que se añade con el paquete
    //               Swashbuckle.AspNetCore.SwaggerUI
    //
    // Se usa Swagger UI y no otro visor por una sola razón: el servicio de
    // facturación ya usa este. Dos proyectos con dos visores distintos son
    // dos cosas que recordar a cambio de nada.
    app.MapOpenApi();

    app.UseSwaggerUI(opciones =>
    {
        opciones.SwaggerEndpoint("/openapi/v1.json", "Plataforma API v1");
        opciones.RoutePrefix = "swagger";          // http://localhost:5255/swagger
        opciones.DocumentTitle = "Plataforma — API";
    });

    // SOLO EN DESARROLLO, y no por pudor: una página que lista todos los
    // endpoints con sus formas y un botón para llamarlos le ahorra a un
    // atacante la mitad del trabajo de reconocimiento.
}

// Los archivos del front se sirven ANTES del middleware de contexto, a
// propósito: una petición de un .js o un .css no necesita saber quién la hace
// ni abrir una transacción contra la base. Puestos al revés, cada imagen de
// la página abriría y cerraría una transacción.
// El manejador de errores va EL PRIMERO, antes que nada: tiene que poder
// atrapar también lo que falle dentro del middleware de contexto. Una
// excepción que se escape de ahí deja la peticion sin respuesta, y el cliente
// ve «se excedió el tiempo de espera» en vez del error real.
// 038: una línea por petición en el log del día (antes que el de errores,
// para anotar también el 500 que este responde).
app.UseMiddleware<RegistroPeticiones>();
app.UseMiddleware<MiddlewareErrores>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMiddleware<MiddlewareContexto>();

app.MapearSesion();
app.MapearSegundoFactor();
app.MapearClave();
app.MapearPerfil();
app.MapearGoogle();       // 049: entrar con Google
app.MapearEmpresas();
app.MapearModulos();
app.MapearInvitacion();
app.MapearRecuperarAcceso();  // 038: «¿Olvidaste tu contraseña o no te llegó el correo?» (sin sesión)
app.MapearCorreosAdmin();     // 038: correos de acceso que no salieron (super administrador)
app.MapearRecuperacion2fa();  // 039: recuperar el segundo factor con espera
app.MapearRegistro();       // 016: registro libre (sin sesión)
app.MapearCuentas();        // 016: «Cuentas», solo super admin
app.MapearConfiguracion();  // 017: el asistente «Agregar mi empresa»
app.MapearCartera();        // 018: Mis empresas, selector y colaboradores
app.MapearInicio();         // el inicio de la empresa: el mes y los avisos
app.MapearRecuperacion();   // 021: recuperar la empresa en pausa (sin sesión)
app.MapearTraspasos();      // 022: reclamar con documentos y su revisión
app.MapearCaminoB();        // 024: una cuenta pide la empresa y el titular aprueba
app.MapearUsuarios();
app.MapearSuplantacion();
app.MapearAuditoria();
app.MapearCredenciales();
app.MapearClientes();
app.MapearConsultas();      // 029: RUC, DNI y tipo de cambio (Decolecta)
app.MapearConsultasSunat(); // 042: modo SUNAT / Decolecta y padrón de SUNAT
app.MapearConexionSunat();  // 030: modo prueba / real (super administrador)
app.MapearProductos();
app.MapearImportarProductos();   // 22 oct: importar productos desde Excel
app.MapearDocumentos();
app.MapearNotasCredito();   // 031: notas de crédito desde el comprobante original
app.MapearNotasDebito();    // 22 oct: notas de débito (intereses, aumento de valor, penalidades)
app.MapearSucursales();     // 044: sucursales (locales del mismo RUC), sus series y usuarios
app.MapearGuias();          // 050: guías de remisión (09 remitente, 31 transportista)
app.MapearTransporte();     // 050: vehículos, conductores y transportistas guardados; configuración de guías
app.MapearWhatsapp();       // 043: mandar el comprobante por WhatsApp (semiautomático)
app.MapearReportes();       // 032: Reportes › Mi negocio
app.MapearReportesSunat();  // 034: Reportes › SUNAT y contabilidad
app.MapearRoles();          // 035: roles propios de cada empresa
app.MapearReportesPlataforma(); // 036: reportes de la cartera y del super administrador

// El receptor del webhook del servicio de facturación. Sin sesión, sin permiso
// y sin módulo: lo protege la firma HMAC. Una sola vez — registrado dos veces
// no falla al arrancar, falla en la primera petición con AmbiguousMatchException.
app.MapearWebhookFacturacion();
app.MapearDescargas();

// EL FRONT ES UNA SOLA PÁGINA CON RUTAS PROPIAS. Sin esto, abrir
// /invitacion/xxx directamente —que es justo lo que hace quien pulsa el enlace
// de su correo— daría 404 en producción: no existe ese archivo en wwwroot.
//
// Va al final a propósito: cualquier ruta de /api que exista gana, y solo lo
// que no coincida con ninguna cae en el index.html.
//
// En desarrollo no hace nada, porque el front lo sirve Vite en otro puerto.
app.MapFallbackToFile("index.html");


// El único endpoint sin sesión: lo usa el healthcheck de Docker, que no tiene
// forma de iniciar sesión.
app.MapGet("/salud", () => Results.Ok(new { estado = "vivo" }))
   .WithTags("Diagnóstico");

// 038 — EL CORREO SE PRUEBA AL ARRANCAR (conecta y se identifica, sin mandar
// nada). Una contraseña de Gmail equivocada se ve en el log en el primer
// minuto, no cuando alguien dice que no le llegó la invitación.
if (app.Services.GetRequiredService<OpcionesCorreo>().Habilitado)
{
    _ = Task.Run(async () =>
    {
        try
        {
            await using var alcance = app.Services.CreateAsyncScope();
            var prueba = await alcance.ServiceProvider.GetRequiredService<IServicioCorreo>().ProbarAsync();
            if (prueba.Enviado)
                app.Logger.LogInformation("Correo listo: el servidor de correo aceptó el usuario y la contraseña.");
            else
                app.Logger.LogError(
                    "EL CORREO NO FUNCIONA: {Motivo}. No saldrán invitaciones, enlaces ni avisos " +
                    "hasta arreglarlo (MAIL_USERNAME / MAIL_PASSWORD en el .env).", prueba.Motivo);
        }
        catch (Exception e)
        {
            app.Logger.LogError(e, "No se pudo probar el correo al arrancar.");
        }
    });
}

app.Run();