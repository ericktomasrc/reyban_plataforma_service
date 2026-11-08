using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Planes;

public sealed record PeticionElegirPlan(Guid EmpresaId, string? Plan);
public sealed record PeticionCupos(int Cupos);
public sealed record PeticionEstudio(Guid? EmpresaId);

public sealed record CambioArmador(string? Codigo, string? Plan);
public sealed record PeticionArmador(List<CambioArmador>? Cambios);
public sealed record PeticionInterruptor(bool Encendidos);
public sealed record PeticionReglas(
    int DiasPrueba, int PruebaCarteraEmpresas, bool RucGratisActivo, int RucGratisTope,
    int CarteraMinimo, DateOnly? OfertaHasta, int AnualMesesGratis,
    // 071: qué ve una empresa en su mes gratis (basico, pro o max).
    string? PlanPrueba = null,
    // 079: el interruptor de la oferta y cuántos meses guarda el precio de lanzamiento quien entra en ella.
    bool? OfertaActiva = null, int? LanzamientoMeses = null);
public sealed record PeticionLanzamiento(DateOnly? Hasta);
public sealed record PeticionCancelarPlan(string? Motivo);
public sealed record PrecioPlan(
    string? Codigo, decimal PrecioRegular, decimal? PrecioOferta, int? SucursalesMax,
    decimal CarteraExtraRegular, decimal? CarteraExtraOferta);
public sealed record TramoCartera(int Desde, int? Hasta, decimal PrecioOferta, decimal PrecioRegular);
public sealed record PeticionPrecios(List<PrecioPlan>? Planes, List<TramoCartera>? Tramos);
public sealed record PeticionAjusteCuenta(string? Tipo, string? Plan, int? Meses, decimal? Precio, DateOnly Hasta, string? Motivo);
public sealed record PeticionAnularAjuste(string? Motivo);
public sealed record PeticionAccesoTotal(bool Activo, string? Motivo);
public sealed record PeticionPlanEmpresa(string? Plan);

/// <summary>
/// 069 — PLANES.
///
/// LA CUENTA (/api/planes): ve su plan, lo que paga y sus empresas; el dueño
/// (o un colaborador administrador) elige el plan de cada empresa, los cupos
/// de la Cartera y cuál es la empresa de su estudio. El cobro con tarjeta
/// llega en la entrega de Cobros: por ahora elegir un plan lo deja elegido.
///
/// LA PLATAFORMA (/api/admin/planes), con tres permisos:
///   · planes_ver         la lista de cuentas y el detalle.
///   · planes_descuentos  ajustes (cortesía, meses gratis, precio especial,
///                        plan mejor, más prueba), acceso total, cambiar el
///                        plan o los cupos de una cuenta.
///   · planes_armar       el armador, precios, tramos, reglas del mes gratis
///                        y el interruptor general.
/// Todo queda en la bitácora (las tablas están vigiladas).
/// </summary>
public static class EndpointsPlanes
{
    private static readonly string[] PlanesValidos = ["basico", "pro", "max"];
    private static readonly string[] TiposAjuste = ["cortesia", "meses_gratis", "precio_especial", "plan_mejor", "ampliar_prueba"];

    public static void MapearPlanes(this IEndpointRouteBuilder rutas)
    {
        // =====================================================================
        // LA CUENTA
        // =====================================================================
        // 074: la página pública (sin sesión) y sus datos de contacto.
        rutas.MapearPaginaPublica();

        var mio = rutas.MapGroup("/api/planes").WithTags("Planes");

        mio.MapGet("/mio", async (ContextoPeticion ctx, ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database.SqlQuery<string>($"""
                SELECT jsonb_build_object(
                    'catalogo', planes_catalogo(),
                    'cuenta', (SELECT planes_resumen_cuenta(u.cuenta_id) FROM usuarios u WHERE u.id = {ctx.UsuarioId} AND u.cuenta_id IS NOT NULL),
                    'puedeCambiar', planes_mi_cuenta_admin() IS NOT NULL,
                    'empresa', CASE WHEN {ctx.EmpresaId}::uuid IS NULL THEN NULL ELSE
                        (SELECT jsonb_build_object('id', e.id, 'nombre', e.nombre, 'plan', e.plan,
                                                   'efectivo', plan_efectivo(e.id), 'pruebaHasta', e.prueba_hasta)
                           FROM empresas e WHERE e.id = {ctx.EmpresaId}::uuid) END
                )::text AS "Value"
                """).FirstAsync(ct);
            return Results.Content(json, "application/json");
        })
        .RequiereSesion()
        .WithSummary("Mi plan: el catálogo, lo que pago y mis empresas");

        mio.MapPost("/elegir", async (PeticionElegirPlan p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var plan = (p.Plan ?? "").Trim().ToLowerInvariant();
            if (!PlanesValidos.Contains(plan)) return Results.BadRequest(new { error = "Elige Básico, Pro o Max." });
            var efectivo = await db.Database
                .SqlQuery<string>($"SELECT planes_elegir({p.EmpresaId}, {plan}) AS \"Value\"").FirstAsync(ct);
            // 1 nov: en el mes gratis, decir que el plan elegido vale para DESPUÉS.
            var prueba = await db.Database.SqlQuery<DateTime?>(
                $"SELECT e.prueba_hasta AS \"Value\" FROM empresas e WHERE e.id = {p.EmpresaId}").FirstOrDefaultAsync(ct);
            return Results.Ok(new
            {
                efectivo,
                mensaje = prueba is { } fin && fin > DateTime.UtcNow
                    ? $"Listo: elegiste {ReglasPlan.Nombre(plan)} para cuando termine tu mes gratis (el {fin.ToLocalTime():dd/MM/yyyy}). Hasta ese día sigues usando todo. Para que se cobre solo, deja tu tarjeta en «Mis pagos»."
                    : $"Listo: la empresa queda en el plan {ReglasPlan.Nombre(plan)}. Lo pagas en «Mis pagos»."
            });
        })
        .RequiereSesion()
        .WithSummary("Elegir el plan de una empresa de mi cuenta");

        // 081: CANCELAR MI PLAN. No se cobra más; sigue usando todo hasta lo que
        // pagó; después ve y descarga todo, pero no emite. Nada se borra.
        mio.MapPost("/cancelar", async (PeticionCancelarPlan p, ContextoPlataforma db,
            Plataforma.Core.Correo.IServicioCorreo correo, ILoggerFactory logs, CancellationToken ct) =>
        {
            var r = JsonDocument.Parse(await db.Database.SqlQuery<string>(
                $"SELECT planes_cancelar({p.Motivo}::text)::text AS \"Value\"").FirstAsync(ct)).RootElement;
            var hasta = r.TryGetProperty("hasta", out var h) && h.ValueKind == JsonValueKind.String ? DateOnly.Parse(h.GetString()!) : (DateOnly?)null;
            if (r.TryGetProperty("correo", out var c) && c.ValueKind == JsonValueKind.String)
            {
                try
                {
                    var m = Plataforma.Api.Cobros.PlantillasCobros.Cancelado(hasta);
                    await correo.EnviarAsync(c.GetString()!, m.Asunto, m.Html, m.Texto, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logs.CreateLogger("Planes").LogWarning(e, "La cancelación se guardó, pero el correo de confirmación no salió.");
                }
            }
            return Results.Ok(new
            {
                hasta = hasta?.ToString("yyyy-MM-dd"),
                mensaje = hasta is { } d && d >= DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5))
                    ? $"Listo: cancelaste tu plan. No se te cobrará más. Sigues usando todo hasta el {d:dd/MM/yyyy}; después podrás entrar y ver todo, pero no emitir. Puedes reactivarlo cuando quieras."
                    : "Listo: cancelaste tu plan. No se te cobrará más. Puedes entrar y ver todo, pero no emitir. Puedes reactivarlo cuando quieras."
            });
        })
        .RequiereSesion()
        .WithSummary("Cancelar mi plan (sin cobros nuevos; nada se borra)");

        mio.MapPost("/reactivar", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT planes_reactivar()", ct);
            return Results.Ok(new { mensaje = "¡Bienvenido de vuelta! Tu plan está activo otra vez. Si tu fecha de pago ya pasó, paga en «Mis pagos» para volver a emitir." });
        })
        .RequiereSesion()
        .WithSummary("Reactivar mi plan cancelado");

        mio.MapPost("/cupos", async (PeticionCupos p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var n = await db.Database.SqlQuery<int>($"SELECT planes_fijar_cupos({p.Cupos}) AS \"Value\"").FirstAsync(ct);
            return Results.Ok(new { cupos = n, mensaje = $"Listo: tu Cartera queda con {n} empresas." });
        })
        .RequiereSesion()
        .WithSummary("Cuántas empresas paga mi Cartera");

        mio.MapPost("/estudio", async (PeticionEstudio p, ContextoPlataforma db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT planes_marcar_estudio({p.EmpresaId}::uuid)", ct);
            return Results.Ok(new
            {
                mensaje = p.EmpresaId is null
                    ? "Listo: ninguna empresa queda como la de tu estudio."
                    : "Listo: esa es la empresa de tu estudio. En Básico es gratis y no ocupa cupo."
            });
        })
        .RequiereSesion()
        .WithSummary("Cuál es la empresa de mi propio estudio");


        // =====================================================================
        // LA PLATAFORMA
        // =====================================================================
        var admin = rutas.MapGroup("/api/admin/planes").WithTags("Administración · planes");

        // --- Configuración: interruptor, reglas, precios y tramos -------------
        admin.MapGet("/config", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database.SqlQuery<string>($"""
                SELECT jsonb_build_object(
                    'catalogo', planes_catalogo(),
                    'interruptor', (SELECT jsonb_build_object(
                        'encendidos', planes_encendidos(), 'decidido', a.planes_activos IS NOT NULL,
                        'cambiadoEn', a.planes_cambiado_en,
                        'cambiadoPor', (SELECT u.nombre FROM usuarios u WHERE u.id = a.planes_cambiado_por))
                      FROM plataforma_ajustes a WHERE a.id = 1),
                    -- 074: los datos de la página pública.
                    'publica', (SELECT jsonb_build_object(
                        'razonSocial', a.publica_razon_social, 'ruc', a.publica_ruc, 'direccion', a.publica_direccion,
                        'correo', a.publica_correo, 'whatsapp', a.publica_whatsapp, 'actualizado', a.publica_actualizado)
                      FROM plataforma_ajustes a WHERE a.id = 1)
                )::text AS "Value"
                """).FirstAsync(ct);
            return Results.Content(json, "application/json");
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
        .WithSummary("Planes, precios, tramos, reglas del mes gratis y el interruptor general");

        admin.MapPut("/interruptor", async (PeticionInterruptor p, ContextoPeticion ctx, ContextoPlataforma db,
            Plataforma.Api.Facturacion.OpcionesConexionSunat entorno, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE plataforma_ajustes
                   SET planes_activos = {p.Encendidos}, planes_cambiado_en = now(), planes_cambiado_por = {ctx.AutorId}::uuid
                 WHERE id = 1
                """, ct);
            return Results.Ok(new
            {
                encendidos = p.Encendidos,
                mensaje = p.Encendidos
                    ? "Planes encendidos: cada cuenta ve los menús de su plan, y las que no tenían mes gratis lo empiezan hoy."
                    : entorno.Fija
                        ? "Planes apagados EN PRODUCCIÓN: todas las cuentas ven todo y no se cobra. Vuelve a encenderlos cuando termines."
                        : "Planes apagados: todas las cuentas ven todos los menús y no se cobra nada."
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesArmar)
        .WithSummary("El interruptor general de planes y cobros");

        admin.MapPut("/reglas", async (PeticionReglas p, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (p.DiasPrueba is < 0 or > 365) return Results.BadRequest(new { error = "Los días del mes gratis van de 0 a 365." });
            if (p.PruebaCarteraEmpresas is < 0 or > 1000) return Results.BadRequest(new { error = "Las empresas con mes gratis en la Cartera van de 0 a 1000." });
            if (p.RucGratisTope is < 0 or > 100) return Results.BadRequest(new { error = "El tope de meses gratis por RUC nuevo va de 0 a 100 al mes." });
            if (p.CarteraMinimo is < 1 or > 100) return Results.BadRequest(new { error = "El mínimo de la Cartera va de 1 a 100 empresas." });
            if (p.AnualMesesGratis is < 0 or > 11) return Results.BadRequest(new { error = "Los meses gratis del pago anual van de 0 a 11." });
            var planPrueba = string.IsNullOrWhiteSpace(p.PlanPrueba) ? "max" : p.PlanPrueba.Trim().ToLowerInvariant();
            if (!PlanesValidos.Contains(planPrueba)) return Results.BadRequest(new { error = "Elige qué ve en su mes gratis: Básico, Pro o Max." });
            if (p.LanzamientoMeses is < 0 or > 60) return Results.BadRequest(new { error = "Los meses con precio de lanzamiento van de 0 a 60." });
            if (p.OfertaActiva == true && p.OfertaHasta is { } fin && fin < DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5)))
                return Results.BadRequest(new { error = "La fecha de fin de la oferta ya pasó: pon una fecha futura o apaga la oferta." });
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE plataforma_ajustes
                   SET dias_prueba = {p.DiasPrueba}, prueba_cartera_empresas = {p.PruebaCarteraEmpresas},
                       ruc_gratis_activo = {p.RucGratisActivo}, ruc_gratis_tope = {p.RucGratisTope},
                       cartera_minimo = {p.CarteraMinimo}, oferta_hasta = {p.OfertaHasta}::date,
                       anual_meses_gratis = {p.AnualMesesGratis}, plan_prueba = {planPrueba},
                       oferta_activa = COALESCE({p.OfertaActiva}::boolean, oferta_activa),
                       lanzamiento_meses = COALESCE({p.LanzamientoMeses}::integer, lanzamiento_meses)
                 WHERE id = 1
                """, ct);
            return Results.Ok(new { mensaje = "Listo: reglas guardadas. Valen desde ahora para lo nuevo." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesArmar)
        .WithSummary("Reglas del mes gratis, mínimo de la Cartera, fin de la oferta y pago anual");

        admin.MapPut("/precios", async (PeticionPrecios p, ContextoPlataforma db, CancellationToken ct) =>
        {
            foreach (var x in p.Planes ?? [])
            {
                var codigo = (x.Codigo ?? "").Trim().ToLowerInvariant();
                if (!PlanesValidos.Contains(codigo)) return Results.BadRequest(new { error = "Hay un plan que no existe." });
                if (x.PrecioRegular < 0 || x.PrecioOferta < 0 || x.CarteraExtraRegular < 0 || x.CarteraExtraOferta < 0)
                    return Results.BadRequest(new { error = "Los precios no pueden ser negativos." });
                if (x.SucursalesMax is < 1) return Results.BadRequest(new { error = "Las sucursales van de 1 en adelante (vacío = ilimitadas)." });
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE planes SET precio_regular = {x.PrecioRegular}, precio_oferta = {x.PrecioOferta}::numeric,
                           sucursales_max = {x.SucursalesMax}::int, cartera_extra_regular = {x.CarteraExtraRegular},
                           cartera_extra_oferta = {x.CarteraExtraOferta}::numeric
                     WHERE codigo = {codigo}
                    """, ct);
            }

            if (p.Tramos is { Count: > 0 } tramos)
            {
                var orden = tramos.OrderBy(t => t.Desde).ToList();
                if (orden[0].Desde != 1) return Results.BadRequest(new { error = "El primer tramo empieza en 1." });
                for (var i = 0; i < orden.Count; i++)
                {
                    var t = orden[i];
                    if (t.PrecioOferta < 0 || t.PrecioRegular < 0) return Results.BadRequest(new { error = "Los precios no pueden ser negativos." });
                    var ultimo = i == orden.Count - 1;
                    if (ultimo ? t.Hasta is not null : t.Hasta is null || t.Hasta < t.Desde || orden[i + 1].Desde != t.Hasta + 1)
                        return Results.BadRequest(new { error = "Los tramos van seguidos (1 a 5, 6 a 10…) y el último no tiene fin." });
                }
                // Los tramos que ya no están quedan inactivos: nada se borra.
                var desdes = orden.Select(t => t.Desde).ToArray();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE cartera_tramos SET activo = false WHERE activo AND NOT (desde = ANY({desdes}))", ct);
                foreach (var t in orden)
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO cartera_tramos (desde, hasta, precio_oferta, precio_regular)
                        VALUES ({t.Desde}, {t.Hasta}::int, {t.PrecioOferta}, {t.PrecioRegular})
                        ON CONFLICT (desde) DO UPDATE SET hasta = EXCLUDED.hasta, precio_oferta = EXCLUDED.precio_oferta,
                               precio_regular = EXCLUDED.precio_regular, activo = true
                        """, ct);
            }
            return Results.Ok(new { mensaje = "Listo: precios guardados. Los clientes ven los nuevos desde ahora." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesArmar)
        .WithSummary("Precios de Básico, Pro y Max, y los tramos de la Cartera");

        // --- El armador -----------------------------------------------------
        admin.MapGet("/armador", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"SELECT planes_armador()::text AS \"Value\"").FirstAsync(ct),
                            "application/json"))
        .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
        .WithSummary("Los menús y submenús, con el plan donde aparecen");

        admin.MapPost("/armador/afectados", async (PeticionArmador p, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (Revisar(p) is { } mal) return Results.BadRequest(new { error = mal });
            var cambios = JsonSerializer.Serialize(p.Cambios!.Select(c => new { codigo = c.Codigo, plan = c.Plan }));
            var json = await db.Database
                .SqlQuery<string>($"SELECT planes_afectados({cambios}::jsonb)::text AS \"Value\"").FirstAsync(ct);
            return Results.Content(json, "application/json");
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesArmar)
        .WithSummary("A cuántas empresas les cambia lo que ven (antes de guardar)");

        admin.MapPut("/armador", async (PeticionArmador p, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (Revisar(p) is { } mal) return Results.BadRequest(new { error = mal });
            var fijas = await db.Database.SqlQuery<string>(
                $"SELECT codigo AS \"Value\" FROM funciones WHERE fija OR oculta").ToListAsync(ct);
            if (p.Cambios!.Any(c => fijas.Contains(c.Codigo!)))
                return Results.BadRequest(new { error = "Comprobantes, clientes, productos, sucursales, personal y configuración van siempre en Básico." });

            var n = 0;
            foreach (var c in p.Cambios!)
                n += await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE funciones SET plan_minimo = {c.Plan}::text, es_nueva = false
                     WHERE codigo = {c.Codigo} AND plan_minimo IS DISTINCT FROM {c.Plan}::text
                    """, ct);
            return Results.Ok(new { cambiadas = n, mensaje = n == 0 ? "No había nada que cambiar." : $"Listo: {n} cambio(s) guardado(s)." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesArmar)
        .WithSummary("Guardar el armador");

        // --- Las cuentas ------------------------------------------------------
        admin.MapGet("/cuentas", async (ContextoPlataforma db, CancellationToken ct) =>
            Results.Content(await db.Database.SqlQuery<string>($"SELECT planes_cuentas_admin()::text AS \"Value\"").FirstAsync(ct),
                            "application/json"))
        .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
        .WithSummary("Todas las cuentas: su plan, lo que pagan y sus ajustes");

        admin.MapGet("/cuentas/{id:guid}", async (Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var json = await db.Database
                .SqlQuery<string>($"SELECT COALESCE(planes_resumen_cuenta({id})::text, 'null') AS \"Value\"").FirstAsync(ct);
            return json == "null" ? Results.NotFound(new { error = "No existe esa cuenta." }) : Results.Content(json, "application/json");
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesVer)
        .WithSummary("Una cuenta: plan, empresas, lo que paga y sus ajustes");

        admin.MapPost("/cuentas/{id:guid}/ajustes", async (Guid id, PeticionAjusteCuenta p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var tipo = (p.Tipo ?? "").Trim();
            var motivo = (p.Motivo ?? "").Trim();
            var plan = string.IsNullOrWhiteSpace(p.Plan) ? null : p.Plan.Trim().ToLowerInvariant();
            if (!TiposAjuste.Contains(tipo)) return Results.BadRequest(new { error = "Elige qué le das: cortesía, meses gratis, precio especial, un plan mejor o más prueba." });
            if (motivo.Length is < 5 or > 500) return Results.BadRequest(new { error = "Escribe el motivo (queda en la bitácora)." });
            if (p.Hasta < DateOnly.FromDateTime(DateTime.Today)) return Results.BadRequest(new { error = "La fecha de fin ya pasó." });
            if (tipo is "cortesia" or "plan_mejor" && (plan is null || !PlanesValidos.Contains(plan)))
                return Results.BadRequest(new { error = "Elige el plan (Básico, Pro o Max)." });
            if (tipo == "meses_gratis" && p.Meses is not (>= 1 and <= 24)) return Results.BadRequest(new { error = "Los meses gratis van de 1 a 24." });
            if (tipo == "precio_especial" && p.Precio is not >= 0) return Results.BadRequest(new { error = "Escribe el precio especial al mes." });

            var existe = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM cuentas WHERE id = {id}").FirstAsync(ct);
            if (existe == 0) return Results.NotFound(new { error = "No existe esa cuenta." });

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cuenta_ajustes (cuenta_id, tipo, plan_codigo, meses, precio, hasta, motivo)
                VALUES ({id}, {tipo},
                        {(tipo is "cortesia" or "plan_mejor" ? plan : null)}::text,
                        {(tipo == "meses_gratis" ? p.Meses : null)}::int,
                        {(tipo == "precio_especial" ? p.Precio : null)}::numeric,
                        {p.Hasta}, {motivo})
                """, ct);
            return Results.Ok(new { mensaje = $"Listo: ajuste guardado hasta el {p.Hasta:dd/MM/yyyy}. Queda en la bitácora." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesDescuentos)
        .WithSummary("Darle un trato especial a una cuenta");

        admin.MapPost("/ajustes/{id:guid}/anular", async (Guid id, PeticionAnularAjuste p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var motivo = (p.Motivo ?? "").Trim();
            if (motivo.Length is < 3 or > 500) return Results.BadRequest(new { error = "Escribe por qué lo quitas." });
            var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cuenta_ajustes SET anulado_en = now(), anulado_motivo = {motivo}
                 WHERE id = {id} AND anulado_en IS NULL
                """, ct);
            return n == 0 ? Results.NotFound(new { error = "Ese ajuste no existe o ya estaba quitado." })
                          : Results.Ok(new { mensaje = "Listo: ajuste quitado. Queda en la bitácora." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesDescuentos)
        .WithSummary("Quitar un ajuste antes de su fecha");

        // 079: hasta cuándo una cuenta conserva el precio de lanzamiento (vacío = lo pierde).
        admin.MapPut("/cuentas/{id:guid}/lanzamiento", async (Guid id, PeticionLanzamiento p, ContextoPlataforma db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT planes_fijar_lanzamiento({id}, {p.Hasta}::date)", ct);
            return Results.Ok(new
            {
                mensaje = p.Hasta is { } h
                    ? $"Listo: esta cuenta paga el precio de lanzamiento hasta el {h:dd/MM/yyyy}."
                    : "Listo: esta cuenta ya paga el precio real."
            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesDescuentos)
        .WithSummary("Hasta cuándo una cuenta conserva el precio de lanzamiento");

        admin.MapPut("/cuentas/{id:guid}/acceso-total", async (Guid id, PeticionAccesoTotal p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var motivo = (p.Motivo ?? "").Trim();
            if (p.Activo && motivo.Length is < 3 or > 300) return Results.BadRequest(new { error = "Escribe para qué es (por ejemplo «cuenta de prueba»)." });
            var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cuentas SET acceso_total = {p.Activo}, acceso_total_motivo = {(p.Activo ? motivo : null)}::text
                 WHERE id = {id}
                """, ct);
            return n == 0 ? Results.NotFound(new { error = "No existe esa cuenta." })
                          : Results.Ok(new
                            {
                                mensaje = p.Activo
                                    ? "Listo: esta cuenta ve todos los menús y no paga (cuenta de prueba)."
                                    : "Listo: la cuenta vuelve a su plan."
                            });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesDescuentos)
        .WithSummary("Acceso total (cuenta de prueba)");

        admin.MapPut("/empresas/{id:guid}/plan", async (Guid id, PeticionPlanEmpresa p, ContextoPlataforma db, CancellationToken ct) =>
        {
            var plan = (p.Plan ?? "").Trim().ToLowerInvariant();
            if (!PlanesValidos.Contains(plan)) return Results.BadRequest(new { error = "Elige Básico, Pro o Max." });
            // 083: lo que pone la plataforma manda: quita una bajada programada.
            var n = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE empresas SET plan = {plan}, plan_programado = NULL, plan_programado_desde = NULL WHERE id = {id}", ct);
            return n == 0 ? Results.NotFound(new { error = "No existe esa empresa." })
                          : Results.Ok(new { mensaje = $"Listo: la empresa queda en {ReglasPlan.Nombre(plan)}." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesDescuentos)
        .WithSummary("Cambiar el plan de una empresa");

        admin.MapPut("/cuentas/{id:guid}/cupos", async (Guid id, PeticionCupos p, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (p.Cupos is < 1 or > 10000) return Results.BadRequest(new { error = "Los cupos van de 1 a 10 000." });
            var n = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cuentas SET cartera_cupos = {p.Cupos} WHERE id = {id} AND tipo = 'profesional'", ct);
            return n == 0 ? Results.NotFound(new { error = "No existe esa Cuenta Profesional." })
                          : Results.Ok(new { mensaje = $"Listo: la Cartera queda con {p.Cupos} empresas." });
        })
        .SoloSuperAdmin(Permisos.Plataforma.PlanesDescuentos)
        .WithSummary("Cambiar los cupos de una Cartera");
    }

    private static string? Revisar(PeticionArmador p)
    {
        if (p.Cambios is not { Count: > 0 }) return "No hay cambios.";
        if (p.Cambios.Count > 200) return "Demasiados cambios de una vez.";
        foreach (var c in p.Cambios)
        {
            if (string.IsNullOrWhiteSpace(c.Codigo)) return "Hay un cambio sin función.";
            if (c.Plan is not null && !PlanesValidos.Contains(c.Plan)) return "Hay un cambio a un plan que no existe.";
        }
        return null;
    }
}