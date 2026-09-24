using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Plataforma.Api.Middleware;

/// <summary>
/// Convierte cualquier excepción que se escape en una respuesta con forma.
///
/// POR QUÉ HACE FALTA, más allá de la elegancia:
///
/// Una excepción sin atrapar dentro del middleware de contexto deja la
/// transacción a medias y la petición sin respuesta. El cliente se queda
/// esperando hasta que agota el tiempo, y el mensaje que ve —«se excedió el
/// tiempo de espera»— no se parece en nada a la causa.
///
/// VA EL PRIMERO DE LA TUBERÍA, antes del contexto, porque tiene que poder
/// atrapar también lo que falle al abrir la transacción.
/// </summary>
public class MiddlewareErrores(RequestDelegate siguiente, ILogger<MiddlewareErrores> log)
{
    public async Task InvokeAsync(HttpContext http)
    {
        try
        {
            await siguiente(http);
        }
        catch (Exception ex)
        {
            // Si ya empezó a escribirse la respuesta no se puede cambiar la
            // cabecera. Lo único honesto es cortar y dejar constancia.
            if (http.Response.HasStarted)
            {
                log.LogError(ex, "Fallo con la respuesta ya empezada en {Ruta}", http.Request.Path);
                throw;
            }

            var (codigo, mensaje) = Traducir(ex);

            log.LogError(ex, "Fallo en {Metodo} {Ruta}", http.Request.Method, http.Request.Path);

            http.Response.StatusCode  = codigo;
            http.Response.ContentType = "application/json";

            await http.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = mensaje,
                // El detalle técnico solo en desarrollo. En producción, un
                // mensaje de excepción puede revelar nombres de tablas,
                // consultas y rutas del servidor.
                detalle = EsDesarrollo(http) ? ex.ToString() : null
            }));
        }
    }


    /// <summary>
    /// Traduce los fallos que el usuario puede provocar. El resto es un 500,
    /// y un 500 no explica nada por fuera: quien tiene que entenderlo es quien
    /// mira el log.
    /// </summary>
    private static (int, string) Traducir(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException =>
            (409, "Otra persona modificó este registro mientras lo editabas. Vuelve a cargarlo."),

        DbUpdateException { InnerException: PostgresException pg } => pg.SqlState switch
        {
            // 23505 — índice único. Casi siempre es un correo, un RUC o una
            // numeración repetida.
            "23505" => (409, "Ya existe un registro con esos datos."),

            // 23503 — clave foránea. Se apunta a algo que no existe, o que el
            // aislamiento hace invisible.
            "23503" => (400, "Hay una referencia a algo que no existe."),

            // 23514 — un CHECK. La base rechazó un valor que el código dejó
            // pasar: es un fallo nuestro, pero el mensaje sirve igual.
            "23514" => (400, "Los datos no cumplen una regla de la base."),

            // 42501 — permiso denegado. Aquí significa que una política de
            // aislamiento cortó una escritura. NO es un error a corregir
            // relajando la política.
            "42501" => (403, "No tienes acceso a ese registro."),

            _ => (500, "Error al guardar.")
        },

        ArgumentException or InvalidOperationException when ex.Message.Length < 200 =>
            (400, ex.Message),

        _ => (500, "Algo salió mal. Queda registrado en el log del servidor.")
    };


    private static bool EsDesarrollo(HttpContext http) =>
        http.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
}
