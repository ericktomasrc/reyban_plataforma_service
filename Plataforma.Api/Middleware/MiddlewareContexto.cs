using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Middleware;

/// <summary>
/// LA PIEZA DE RIESGO DE TODO EL BACK.
///
/// Abre una transacción, dice a PostgreSQL quién está preguntando, deja
/// correr la petición y confirma. De eso dependen dos cosas que no se ven:
/// el aislamiento entre empresas y la autoría de cada fila de la bitácora.
///
/// ────────────────────────────────────────────────────────────────────────
/// POR QUÉ LA TRANSACCIÓN NO ES NEGOCIABLE
///
/// `fijar_contexto` usa set_config(..., true), o sea local a la transacción.
/// Si una consulta corre fuera de una transacción explícita, cada sentencia
/// es su propia transacción: el contexto se fija en la primera y ha
/// desaparecido en la segunda.
///
/// Y el fallo no avisa. El aislamiento simplemente devuelve CERO FILAS, como
/// si la empresa no tuviera datos. Se busca el error en la consulta, en el
/// índice, en los datos — y está aquí.
///
/// Es local a la transacción a propósito: con un pool de conexiones, un valor
/// que sobreviviera se filtraría a la petición del siguiente usuario, que es
/// la misma clase de fallo que el aislamiento viene a evitar.
/// ────────────────────────────────────────────────────────────────────────
/// LA SUPLANTACIÓN SE RESUELVE AQUÍ, Y AQUÍ SE LIMITA
///
/// Cuando una sesión está suplantando, este archivo hace dos cosas que no hace
/// ningún otro sitio:
///
///   1. SEPARA EL ALCANCE DE LA AUTORÍA. El contexto de PostgreSQL se fija con
///      la empresa del suplantado y con `super` en falso —para que el
///      aislamiento recorte igual que a él— pero con el usuario de VERDAD como
///      autor. Cualquier fila escrita durante una suplantación lleva el nombre
///      del super administrador, nunca el del cliente.
///
///   2. CIERRA LA ESCRITURA. Suplantando solo se puede leer. Va aquí, en una
///      sola puerta por la que pasa todo, y no repartido por los endpoints: la
///      versión repartida se olvida en el endpoint número treinta.
/// ────────────────────────────────────────────────────────────────────────
/// </summary>
public class MiddlewareContexto(RequestDelegate siguiente, ILogger<MiddlewareContexto> log)
{
    public const string NombreCookie = "plataforma_sesion";

    public async Task InvokeAsync(
        HttpContext http,
        ContextoPlataforma db,
        ContextoPeticion ctx)
    {
        var ct = http.RequestAborted;

        // Estas rutas no tocan la base. Abrirles una transacción sería gastar
        // una conexión del pool para no preguntar nada — y el healthcheck de
        // Docker llama cada treinta segundos, todo el día.
        if (SinBaseDeDatos(http.Request.Path))
        {
            await siguiente(http);
            return;
        }

        ctx.Ip = http.Connection.RemoteIpAddress;
        ctx.Agente = Recortar(http.Request.Headers.UserAgent.ToString(), 400);

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // --- 1. Contexto anónimo --------------------------------------------
        //
        // Se fija antes de saber quién es, para que la IP y el navegador estén
        // disponibles aunque la petición falle en el ingreso. Un intento
        // fallido sin IP no sirve para nada.
        await FijarAsync(db, ctx, ct);

        // --- 2. ¿Hay sesión? -------------------------------------------------
        // 21 OCT: AL ENTRAR (POST /api/sesion) NO SE MIRA LA COOKIE VIEJA. Si
        // quedó una de «ver como» (la pantalla volvió al login), el candado de
        // solo lectura bloqueaba el propio ingreso y la persona solo podía
        // salir borrando cookies. Entrar es empezar de cero: la sesión nueva
        // reemplaza a la cookie, como si no hubiera ninguna.
        var token = http.Request.Cookies[NombreCookie];
        if (!string.IsNullOrWhiteSpace(token) && !EsIngreso(http.Request))
            await IdentificarAsync(db, ctx, token, http, ct);

        // --- 3. Ahora sí, con los valores reales ----------------------------
        if (ctx.Autenticado)
            await FijarAsync(db, ctx, ct);

        // --- 3b. 046: qué ve esta persona y la sucursal elegida arriba -------
        //
        // Lo calcula y lo fija la base (fijar_alcance): desde aquí, la regla
        // de `documentos` recorta sola la lista, el detalle, las descargas,
        // las notas, los reportes y el Inicio. Con «ver como», el de la persona
        // que se mira (ctx.UsuarioId ya es el suyo). El super administrador sin
        // suplantar no tiene empresa: no se recorta nada.
        if (ctx.Autenticado && ctx.SesionCompleta && !ctx.EsSuperAdmin && ctx.EmpresaId is not null)
            await FijarAlcanceAsync(db, ctx, http, ct);

        // --- 4. Suplantando, solo lectura -----------------------------------
        if (ctx.Suplantando && !PuedeEscribirSuplantando(http.Request))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;

            await http.Response.WriteAsJsonAsync(new
            {
                error = $"Estás viendo la plataforma como {ctx.SuplantadoNombre}, " +
                        "en solo lectura. Para hacer cambios, vuelve a ser tú.",
                codigo = "solo_lectura_suplantando"
            }, ct);

            // El commit sigue haciendo falta: la transacción está abierta y hay
            // que cerrarla, aunque no haya nada que guardar.
            await transaccion.CommitAsync(ct);
            return;
        }

        await siguiente(http);

        // Sin commit no se guarda nada. Si la petición lanzó una excepción,
        // el `await using` deshace la transacción al salir — que es lo que se
        // quiere: una petición que falló a la mitad no deja medio cambio.
        await transaccion.CommitAsync(ct);
    }


    /// <summary>
    /// EL USUARIO QUE VIAJA A POSTGRESQL ES EL AUTOR, NO EL EFECTIVO.
    ///
    /// Sin suplantación son el mismo y no hay nada que pensar. Con
    /// suplantación, `ctx.AutorId` es el super administrador y `ctx.EmpresaId`
    /// es la empresa del cliente: el aislamiento recorta como al cliente, y la
    /// bitácora firma con el nombre de quien está de verdad al teclado.
    ///
    /// Al revés —firmar con el suplantado— habría sido más fácil de escribir y
    /// habría destruido lo único que la auditoría tiene que garantizar.
    /// </summary>
    private static Task FijarAsync(ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        db.FijarContextoAsync(
            ctx.AutorId,
            ctx.EmpresaId,
            ctx.Ip?.ToString(),
            ctx.Agente,
            ctx.EsSuperAdmin,
            ct);


    /// <summary>
    /// Las dos únicas escrituras permitidas mientras se suplanta.
    ///
    /// Las dos son salidas, no entradas: terminar la suplantación y cerrar
    /// sesión. Si no estuvieran exentas, quien entra a ver una cuenta se queda
    /// atrapado dentro, porque el botón de volver es un DELETE — y eso sería
    /// mucho peor que cualquier cosa de la que este candado protege.
    ///
    /// Se comprueba por ruta y método y no por atributo en el endpoint porque
    /// el middleware corre antes del enrutado. Es menos elegante y es lo que
    /// hace que no se pueda olvidar en ningún endpoint nuevo.
    /// </summary>
    /// <summary>21 oct: POST /api/sesion, el ingreso con correo y contraseña.</summary>
    private static bool EsIngreso(HttpRequest peticion) =>
        HttpMethods.IsPost(peticion.Method) &&
        (peticion.Path.Value?.TrimEnd('/') ?? "").Equals("/api/sesion", StringComparison.OrdinalIgnoreCase);

    private static bool PuedeEscribirSuplantando(HttpRequest peticion)
    {
        if (HttpMethods.IsGet(peticion.Method) ||
            HttpMethods.IsHead(peticion.Method) ||
            HttpMethods.IsOptions(peticion.Method))
        {
            return true;
        }

        if (!HttpMethods.IsDelete(peticion.Method)) return false;

        // SE RECORTA LA BARRA FINAL, y no es quisquillosería: los dos endpoints
        // se declaran como grupo + `MapDelete("/")`, o sea con el patrón
        // `/api/suplantacion/`. El enrutado acepta las dos formas; una
        // comparación exacta, solo una.
        //
        // El día que un proxy, un `nginx` o Swagger UI mandaran la versión con
        // barra, este candado respondería 403 AL PROPIO BOTÓN DE VOLVER, y
        // entrar a ver una cuenta sería un viaje de ida.
        var ruta = peticion.Path.Value?.TrimEnd('/') ?? "";

        return ruta.Equals("/api/suplantacion", StringComparison.OrdinalIgnoreCase)
            || ruta.Equals("/api/sesion", StringComparison.OrdinalIgnoreCase);
    }


    private async Task IdentificarAsync(
        ContextoPlataforma db,
        ContextoPeticion ctx,
        string token,
        HttpContext http,
        CancellationToken ct)
    {
        var hash = GeneradorTokens.Hashear(token);

        // `resolver_sesion` es SECURITY DEFINER: para saber de quién es la
        // sesión hay que leerla ANTES de poder fijar el contexto. Es una de
        // las dos únicas funciones del sistema que se salta el aislamiento, y
        // se busca por el hash de 32 bytes aleatorios.
        var sesion = await db.SesionesResueltas
            .FromSqlInterpolated($"SELECT * FROM resolver_sesion({hash})")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (sesion is null)
        {
            BorrarCookie(http);
            return;
        }

        var ahora = DateTime.UtcNow;

        var motivo =
            sesion.RevocadaEn is not null ? "revocada"
            : sesion.ExpiraEn <= ahora    ? "expirada"
            : !sesion.UsuarioActivo       ? "usuario desactivado"
            : !sesion.EmpresaOperativa    ? "empresa suspendida"
            : null;

        if (motivo is not null)
        {
            // No se registra evento aquí: una cookie caducada en una pestaña
            // olvidada dispararía uno en cada recarga, y la tabla de eventos
            // se llenaría de ruido que tapa lo que sí importa.
            log.LogDebug("Sesión no válida ({Motivo})", motivo);
            BorrarCookie(http);
            return;
        }

        // Los permisos se leen una vez por petición, no una vez por
        // comprobación. Aquí el contexto todavía es anónimo, así que la
        // consulta va por la puerta de la sesión: se filtra por usuario_id,
        // y la vista tiene security_invoker.
        //
        // SUPLANTANDO, `sesion.UsuarioId` YA ES EL DEL SUPLANTADO: lo cambia
        // `resolver_sesion`, en la base. Que la decisión viva allí y no aquí es
        // lo que hace imposible el peor fallo posible de esta función —ver como
        // el cliente pero conservar los permisos de super administrador—,
        // porque no hay ninguna rama de este código que pueda quedarse a medias.
        await db.FijarContextoAsync(
            sesion.SuplantadorId ?? sesion.UsuarioId,
            sesion.EmpresaId,
            ctx.Ip?.ToString(), ctx.Agente,
            sesion.EsSuperAdmin, ct);

        // 041: CON «VER COMO», LA BASE TIENE QUE SABER A QUIÉN SE MIRA. Sin
        // esto, el dueño o un colaborador de una cuenta era invisible (la regla
        // de `usuarios` no deja ver a la gente de una cuenta desde la empresa)
        // y /api/yo respondía 401: la pantalla mandaba al login. Solo lectura:
        // las reglas de escritura no cambian.
        if (sesion.SuplantadorId is not null)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT fijar_suplantado({sesion.SesionId}::uuid)", ct);

        var permisos = await db.PermisosEfectivos
            .Where(p => p.UsuarioId == sesion.UsuarioId)
            .Select(p => p.PermisoCodigo)
            .ToListAsync(ct);

        ctx.Identificar(
            sesion.UsuarioId,
            sesion.EmpresaId,
            sesion.SesionId,
            sesion.EsSuperAdmin,
            // Una sesión con 2FA pendiente existe, pero no vale para nada más
            // que para validar el código.
            sesion.DosfaSuperado,
            sesion.ClaveCambioForzado,
            permisos,
            sesion.SuplantadorId,
            sesion.SuplantadorNombre,
            sesion.SuplantadoNombre,
            sesion.SuplantacionInicio);

        // 067: del equipo de la plataforma con permisos parciales (null = el dueño, todo).
        if (sesion.EsSuperAdmin && sesion.SuplantadorId is null)
            ctx.FijarPlataformaPermisos(await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == sesion.UsuarioId).Select(u => u.PlataformaPermisos).FirstOrDefaultAsync(ct));

        // La última actividad se actualiza como mucho una vez por minuto.
        // Escribirla en cada petición convertiría cada GET en una escritura,
        // y llenaría la bitácora de filas que no dicen nada.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE sesiones SET ultima_actividad = now()
              WHERE id = {sesion.SesionId}
                AND ultima_actividad < now() - interval '1 minute'
             """, ct);
    }


    /// <summary>
    /// 046 — La sucursal que pide el selector de arriba viaja en la cabecera
    /// X-Sucursal (un id; vacío o «todas» = la de siempre). La base decide si
    /// le toca: una ajena se ignora y queda la de por defecto.
    /// </summary>
    private static async Task FijarAlcanceAsync(
        ContextoPlataforma db, ContextoPeticion ctx, HttpContext http, CancellationToken ct)
    {
        var pedida = Guid.TryParse(http.Request.Headers["X-Sucursal"].ToString(), out var g) ? g.ToString() : null;
        var usuario = ctx.UsuarioId?.ToString();
        var empresa = ctx.EmpresaId?.ToString();

        var texto = await db.Database
            .SqlQuery<string>($"SELECT fijar_alcance({usuario}::uuid, {empresa}::uuid, {pedida}::uuid) AS \"Value\"")
            .FirstAsync(ct);

        using var doc = System.Text.Json.JsonDocument.Parse(texto);
        var r = doc.RootElement;
        if (!r.TryGetProperty("alcance", out var alcance)) return;

        static Guid? Id(System.Text.Json.JsonElement e, string p) =>
            e.TryGetProperty(p, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                && Guid.TryParse(v.GetString(), out var x) ? x : null;

        var sucursales = r.TryGetProperty("sucursales", out var lista) && lista.ValueKind == System.Text.Json.JsonValueKind.Array
            ? lista.EnumerateArray().Select(s => new SucursalDeAlcance(
                  Id(s, "id") ?? Guid.Empty,
                  s.GetProperty("codigo").GetString() ?? "",
                  s.GetProperty("nombre").GetString() ?? "",
                  s.TryGetProperty("activo", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True)).ToList()
            : [];

        ctx.FijarAlcance(
            alcance.GetString() ?? "empresa",
            Id(r, "vista"),
            Id(r, "defecto"),
            sucursales,
            r.TryGetProperty("total", out var total) && total.ValueKind == System.Text.Json.JsonValueKind.Number ? total.GetInt32() : 0);
    }


    private static bool SinBaseDeDatos(PathString ruta) =>
        ruta.StartsWithSegments("/salud")    ||   // healthcheck de Docker
        ruta.StartsWithSegments("/openapi")  ||   // el documento OpenAPI
        ruta.StartsWithSegments("/swagger");      // su visor


    private static void BorrarCookie(HttpContext http) =>
        http.Response.Cookies.Delete(NombreCookie);

    private static string? Recortar(string? texto, int largo) =>
        string.IsNullOrEmpty(texto) ? null
        : texto.Length <= largo ? texto
        : texto[..largo];
}
