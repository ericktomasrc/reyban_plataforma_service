using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Administracion;
using Plataforma.Api.Arranque;
using Plataforma.Api.Avisos;
using Plataforma.Api.Auditoria;
using Plataforma.Api.Invitaciones;
using Plataforma.Api.Middleware;
using Plataforma.Api.Sesion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

// El .env se lee ANTES de construir nada: la cadena de conexión y la llave
// maestra salen de ahí. En producción no hay .env y las variables las pone el
// contenedor; el cargador no pisa lo que ya esté definido.
Entorno.CargarDotEnv();

var constructor = WebApplication.CreateBuilder(args);


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

constructor.Services.AddEndpointsApiExplorer();
constructor.Services.AddOpenApi();


var app = constructor.Build();


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
app.UseMiddleware<MiddlewareErrores>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMiddleware<MiddlewareContexto>();

app.MapearSesion();
app.MapearSegundoFactor();
app.MapearClave();
app.MapearPerfil();
app.MapearEmpresas();
app.MapearModulos();
app.MapearInvitacion();
app.MapearUsuarios();
app.MapearSuplantacion();
app.MapearAuditoria();

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

app.Run();
