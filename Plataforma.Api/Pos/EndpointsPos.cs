using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Pos;

public record PeticionAbrirCaja(Guid SucursalId, decimal Fondo, string? Nota, Guid? CajaId = null);
public record PeticionMovimientoCaja(string Tipo, decimal Monto, string Motivo, string? Detalle);
public record PagoPos(string Medio, decimal Monto, string? Referencia);
public record PeticionCobrar(
    string Tipo, string Serie, Guid ClienteId, List<PeticionLinea> Lineas, List<PagoPos> Pagos,
    decimal? Recibido = null, bool? Whatsapp = null, Guid? DocumentoId = null);
public record PeticionCerrarCaja(decimal Contado, string? Observacion, decimal? DejaEnCaja, decimal? Entrega);

/// <summary>
/// e173 (migración 055) — EL PUNTO DE VENTA Y LA CAJA.
///
///   GET  /api/pos/estado          mi caja abierta (o las cajas para abrir una)
///   POST /api/pos/abrir           abrir mi caja con su fondo
///   POST /api/pos/movimientos     ingreso o retiro de efectivo
///   POST /api/pos/cobrar          vender: crea la boleta o factura, la emite y
///                                 guarda cómo se pagó (o reintenta una que no salió)
///   GET  /api/pos/resumen         el arqueo de mi caja abierta (lo que dice el sistema)
///   POST /api/pos/cerrar          cerrar mi caja: lo contado y la diferencia
///   GET  /api/pos/cajas           aperturas y cierres (las mías, o todas con pos.cajas_ver)
///   GET  /api/pos/cajas/{id}      el arqueo de una caja
///
/// CADA VENTA ES UN COMPROBANTE NORMAL. Se crea y se emite con lo mismo que usa
/// «Nuevo comprobante» (CrearBorradorAsync y EmitirAsync): las mismas reglas,
/// el mismo stock, la misma serie de la sucursal. Lo único propio del POS es
/// de qué caja salió y cómo se pagó.
///
/// LO QUE CUENTA LA CAJA SALE DE LOS COMPROBANTES: una venta cuyo comprobante
/// no llegó (error) o fue rechazado no suma al efectivo esperado.
/// </summary>
public static class EndpointsPos
{
    public const string ModuloPos = "pos";

    private static class P
    {
        public const string Vender      = "pos.vender";
        public const string Movimientos = "pos.movimientos";
        public const string Descuentos  = "pos.descuentos";
        public const string CajasVer    = "pos.cajas_ver";
    }

    private static readonly string[] Medios = ["efectivo", "yape", "tarjeta", "transferencia"];
    private const string EstadosQueCuentan = "('encolado','aceptado','observado')";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Sesion(Guid Id, Guid CajaId, string Caja, Guid SucursalId, string Sucursal,
                                 DateTime AbiertaEn, decimal Fondo);

    public static void MapearPos(this IEndpointRouteBuilder rutas)
    {
        var g = rutas.MapGroup("/api/pos").WithTags("Punto de venta");

        // =====================================================================
        // ESTADO: mi caja abierta, o con qué cajas puedo abrir.
        // =====================================================================
        g.MapGet("/estado", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var sesion = await SesionAbiertaAsync(db, ctx, ct);
            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
            var todas = permitidas is null;
            var ids = permitidas?.ToArray() ?? [];

            // Las sucursales en las que puede vender, con sus cajas y el último cierre.
            var json = await db.Database.SqlQuery<string>($"""
                SELECT coalesce(json_agg(x ORDER BY x.codigo), '[]'::json)::text AS "Value" FROM (
                    SELECT su.id, su.codigo, su.nombre,
                           (SELECT coalesce(json_agg(json_build_object(
                                       'id', k.id, 'nombre', k.nombre,
                                       'abiertaPor', (SELECT u.nombre FROM pos_sesiones s JOIN usuarios u ON u.id = s.usuario_id
                                                       WHERE s.caja_id = k.id AND s.cerrada_en IS NULL),
                                       'ultimoCierre', (SELECT json_build_object('en', s.cerrada_en, 'por', u.nombre, 'deja', s.deja_en_caja)
                                                          FROM pos_sesiones s JOIN usuarios u ON u.id = s.usuario_id
                                                         WHERE s.caja_id = k.id AND s.cerrada_en IS NOT NULL
                                                         ORDER BY s.cerrada_en DESC LIMIT 1)) ORDER BY k.nombre), '[]'::json)
                              FROM pos_cajas k WHERE k.sucursal_id = su.id AND k.activo) AS cajas
                      FROM sucursales su
                     WHERE su.activo AND ({todas} OR su.id = ANY({ids}))
                ) x
                """).FirstAsync(ct);

            return Results.Ok(new
            {
                sesion,
                sucursales = JsonDocument.Parse(json).RootElement,
                puede = new
                {
                    movimientos = ctx.Tiene(P.Movimientos),
                    descuentos  = ctx.Tiene(P.Descuentos),
                    cajasVer    = ctx.Tiene(P.CajasVer),
                }
            });
        })
        .RequierePermiso(P.Vender)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(ModuloPos)
        .WithSummary("Mi caja abierta, o las cajas con las que puedo abrir");


        // =====================================================================
        // ABRIR
        // =====================================================================
        g.MapPost("/abrir", async (PeticionAbrirCaja p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (await SesionAbiertaAsync(db, ctx, ct) is { } ya)
                return Mal($"Ya tienes abierta la {ya.Caja} ({ya.Sucursal}). Ciérrala antes de abrir otra.");

            if (p.Fondo < 0 || p.Fondo > 1_000_000)
                return Mal("El fondo inicial va de 0 a 1,000,000.");

            var permitidas = await Sucursales.PermitidasAsync(db, ctx, ct);
            if (permitidas is not null && !permitidas.Contains(p.SucursalId))
                return Mal("No trabajas en esa sucursal.");
            if (!await db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM sucursales WHERE id = {p.SucursalId} AND activo) AS \"Value\"").FirstAsync(ct))
                return Mal("Esa sucursal no existe o está desactivada.");

            // La caja: la elegida, o «Caja 01» de la sucursal (se crea la primera vez).
            Guid caja;
            if (p.CajaId is { } elegida)
            {
                var ok = await db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS (SELECT 1 FROM pos_cajas WHERE id = {elegida} AND sucursal_id = {p.SucursalId} AND activo) AS "Value"
                    """).FirstAsync(ct);
                if (!ok) return Mal("Esa caja no es de esta sucursal.");
                caja = elegida;
            }
            else
            {
                var libre = await db.Database.SqlQuery<Guid?>($"""
                    SELECT k.id AS "Value" FROM pos_cajas k
                     WHERE k.sucursal_id = {p.SucursalId} AND k.activo
                       AND NOT EXISTS (SELECT 1 FROM pos_sesiones s WHERE s.caja_id = k.id AND s.cerrada_en IS NULL)
                     ORDER BY k.nombre LIMIT 1
                    """).FirstOrDefaultAsync(ct);
                if (libre is { } l) caja = l;
                else
                {
                    var n = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pos_cajas WHERE sucursal_id = {p.SucursalId}").FirstAsync(ct);
                    var nombre = $"Caja {n + 1:00}";
                    // ToListAsync y no FirstAsync: un INSERT … RETURNING no se puede
                    // envolver en el SELECT … LIMIT 1 que arma EF con First.
                    caja = (await db.Database.SqlQuery<Guid>($"""
                        INSERT INTO pos_cajas (empresa_id, sucursal_id, nombre)
                        VALUES ({ctx.EmpresaId!.Value}, {p.SucursalId}, {nombre}) RETURNING id AS "Value"
                        """).ToListAsync(ct)).Single();
                }
            }

            var abierta = await db.Database.SqlQuery<string?>($"""
                SELECT u.nombre AS "Value" FROM pos_sesiones s JOIN usuarios u ON u.id = s.usuario_id
                 WHERE s.caja_id = {caja} AND s.cerrada_en IS NULL
                """).FirstOrDefaultAsync(ct);
            if (abierta is not null) return Mal($"Esa caja la tiene abierta {abierta}. Elige otra.");

            var nota = string.IsNullOrWhiteSpace(p.Nota) ? null : Recortar(p.Nota.Trim(), 250);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO pos_sesiones (empresa_id, sucursal_id, caja_id, usuario_id, fondo_inicial, nota_apertura)
                VALUES ({ctx.EmpresaId!.Value}, {p.SucursalId}, {caja}, {ctx.UsuarioId!.Value}, {Math.Round(p.Fondo, 2)}, {nota})
                """, ct);

            return Results.Ok(new { sesion = await SesionAbiertaAsync(db, ctx, ct), mensaje = "Caja abierta. A vender." });
        })
        .RequierePermiso(P.Vender)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(ModuloPos)
        .WithSummary("Abrir mi caja con su fondo inicial");


        // =====================================================================
        // INGRESO O RETIRO DE EFECTIVO
        // =====================================================================
        g.MapPost("/movimientos", async (PeticionMovimientoCaja p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (await SesionAbiertaAsync(db, ctx, ct) is not { } s)
                return Mal("No tienes una caja abierta.");

            var tipo = (p.Tipo ?? "").Trim().ToLowerInvariant();
            if (tipo is not ("ingreso" or "retiro")) return Mal("El movimiento es un ingreso o un retiro.");
            if (p.Monto <= 0 || p.Monto > 1_000_000) return Mal("Escribe un monto mayor que cero.");
            var motivo = (p.Motivo ?? "").Trim();
            if (motivo.Length < 3) return Mal("Escribe el motivo (al menos 3 letras).");

            var monto = Math.Round(p.Monto, 2, MidpointRounding.AwayFromZero);
            if (tipo == "retiro")
            {
                var arqueo = await ArqueoAsync(db, s.Id, ct);
                if (monto > arqueo.Esperado)
                    return Mal($"En caja debería haber S/ {arqueo.Esperado:0.00} en efectivo: no se puede retirar más.");
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO pos_movimientos (empresa_id, sesion_id, tipo, monto, motivo, detalle)
                VALUES ({ctx.EmpresaId!.Value}, {s.Id}, {tipo}, {monto}, {Recortar(motivo, 80)},
                        {(string.IsNullOrWhiteSpace(p.Detalle) ? null : Recortar(p.Detalle.Trim(), 250))})
                """, ct);

            return Results.Ok(new { mensaje = tipo == "ingreso" ? $"Ingreso de S/ {monto:0.00} registrado." : $"Retiro de S/ {monto:0.00} registrado." });
        })
        .RequierePermiso(P.Movimientos)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(ModuloPos)
        .WithSummary("Ingreso o retiro de efectivo en mi caja");


        // =====================================================================
        // COBRAR: crea la boleta o factura, la emite y guarda cómo se pagó.
        // =====================================================================
        g.MapPost("/cobrar", async (
            PeticionCobrar p, ContextoPlataforma db, ContextoPeticion ctx, ServicioEmision emision,
            OpcionesConexionSunat conexionSunat, CancellationToken ct) =>
        {
            if (await SesionAbiertaAsync(db, ctx, ct) is not { } s)
                return Mal("No tienes una caja abierta. Ábrela para vender.");

            // --- Reintentar una venta que no salió (el servicio no respondió) ---
            if (p.DocumentoId is { } reintento)
            {
                var deEsta = await db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS (SELECT 1 FROM pos_ventas WHERE documento_id = {reintento} AND sesion_id = {s.Id}) AS "Value"
                    """).FirstAsync(ct);
                if (!deEsta) return Mal("Esa venta no es de tu caja abierta.");
                var r0 = await EndpointsDocumentos.EmitirAsync(reintento, db, emision, conexionSunat, ct);
                return await ResponderAsync(r0, reintento, db, ct);
            }

            if (p.Tipo is not ("01" or "03")) return Mal("En el punto de venta se emiten boletas y facturas.");
            if (p.Lineas is null || p.Lineas.Count == 0) return Mal("No hay nada que cobrar.");
            if (p.Lineas.Count > 200) return Mal("Son demasiadas líneas para una venta de mostrador.");
            if (p.Lineas.Any(l => l.Descuento > 0) && !ctx.Tiene(P.Descuentos))
                return Mal("No tienes permiso para dar descuentos en el punto de venta.");

            var pagos = (p.Pagos ?? []).Select(x => x with { Medio = (x.Medio ?? "").Trim().ToLowerInvariant() }).ToList();
            if (pagos.Count == 0) return Mal("Falta cómo paga el cliente.");
            if (pagos.Any(x => !Medios.Contains(x.Medio))) return Mal("Medio de pago desconocido.");
            if (pagos.Any(x => x.Monto <= 0)) return Mal("Cada pago tiene que ser mayor que cero.");
            if (pagos.GroupBy(x => x.Medio).Any(gr => gr.Count() > 1)) return Mal("Un medio de pago va una sola vez.");

            // --- El comprobante: lo mismo que «Nuevo comprobante» ---------------
            var (d, error) = await EndpointsDocumentos.CrearBorradorAsync(new PeticionDocumento(
                p.Tipo, p.Serie, p.ClienteId, null, "PEN", p.Lineas, null,
                FormaPago: FormaDePago.Contado, Whatsapp: p.Whatsapp, EntregaDespues: false), db, ctx, ct);
            if (d is null) return Mal(error ?? "No se pudo armar el comprobante.");

            if (d.SucursalId != s.SucursalId)
            {
                await DescartarAsync(d, db, ct);
                return Mal($"La serie {d.Serie} no es de la sucursal de tu caja ({s.Sucursal}).");
            }

            // --- Los pagos tienen que dar el total ----------------------------
            var total = d.ImporteTotal;
            var suma = Math.Round(pagos.Sum(x => x.Monto), 2);
            if (Math.Abs(suma - total) > 0.009m)
            {
                await DescartarAsync(d, db, ct);
                return Mal($"Los pagos suman S/ {suma:0.00} y el total es S/ {total:0.00}. Revísalos y vuelve a cobrar.");
            }
            var efectivo = pagos.FirstOrDefault(x => x.Medio == "efectivo")?.Monto ?? 0;
            var recibido = p.Recibido is { } rc && efectivo > 0 ? Math.Round(rc, 2) : efectivo;
            if (efectivo > 0 && recibido < efectivo)
            {
                await DescartarAsync(d, db, ct);
                return Mal($"Recibiste S/ {recibido:0.00} y en efectivo son S/ {efectivo:0.00}.");
            }
            var vuelto = efectivo > 0 ? recibido - efectivo : 0;

            // --- Emitir --------------------------------------------------------
            var r = await EndpointsDocumentos.EmitirAsync(d.Id, db, emision, conexionSunat, ct);

            // No llegó a emitirse (fecha, stock, conexión con SUNAT…): se descarta
            // el borrador y se dice por qué. La venta no existe.
            if (d.Estado == EstadoDocumento.Borrador)
            {
                await DescartarAsync(d, db, ct);
                return r;
            }

            // Se emitió, o se intentó y el servicio no respondió (error): la venta
            // queda en la caja; si es error, se puede reintentar.
            var venta = (await db.Database.SqlQuery<Guid>($"""
                INSERT INTO pos_ventas (empresa_id, sesion_id, documento_id, total, recibido, vuelto)
                VALUES ({ctx.EmpresaId!.Value}, {s.Id}, {d.Id}, {total}, {(efectivo > 0 ? recibido : (decimal?)null)}, {vuelto})
                RETURNING id AS "Value"
                """).ToListAsync(ct)).Single();
            foreach (var x in pagos)
            {
                var referencia = string.IsNullOrWhiteSpace(x.Referencia) ? null : Recortar(x.Referencia.Trim(), 60);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO pos_pagos (empresa_id, venta_id, medio, monto, referencia)
                    VALUES ({ctx.EmpresaId!.Value}, {venta}, {x.Medio}, {Math.Round(x.Monto, 2)}, {referencia})
                    """, ct);
            }

            return await ResponderAsync(r, d.Id, db, ct, vuelto);
        })
        .RequierePermiso(P.Vender)
        .RequiereFicha()
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(ModuloPos)
        .WithSummary("Vender: emitir la boleta o factura y guardar cómo se pagó");


        // =====================================================================
        // EL ARQUEO DE MI CAJA ABIERTA
        // =====================================================================
        g.MapGet("/resumen", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (await SesionAbiertaAsync(db, ctx, ct) is not { } s)
                return Mal("No tienes una caja abierta.");
            return Results.Content(await DetalleAsync(db, s.Id, ct), "application/json");
        })
        .RequierePermiso(P.Vender)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(ModuloPos)
        .WithSummary("Lo que dice el sistema de mi caja abierta");


        // =====================================================================
        // CERRAR
        // =====================================================================
        g.MapPost("/cerrar", async (PeticionCerrarCaja p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (await SesionAbiertaAsync(db, ctx, ct) is not { } s)
                return Mal("No tienes una caja abierta.");
            if (p.Contado < 0 || p.Contado > 100_000_000) return Mal("Escribe el efectivo que contaste.");

            var arqueo = await ArqueoAsync(db, s.Id, ct);
            var contado = Math.Round(p.Contado, 2, MidpointRounding.AwayFromZero);
            var diferencia = contado - arqueo.Esperado;
            var observacion = (p.Observacion ?? "").Trim();
            if (diferencia != 0 && observacion.Length < 3)
                return Mal($"Hay una diferencia de S/ {diferencia:0.00}: escribe qué pasó.");

            var deja = Math.Round(p.DejaEnCaja ?? 0, 2);
            var entrega = Math.Round(p.Entrega ?? contado - deja, 2);
            if (deja < 0 || entrega < 0 || deja + entrega != contado)
                return Mal($"Lo que dejas ({deja:0.00}) y lo que entregas ({entrega:0.00}) tienen que sumar lo contado ({contado:0.00}).");

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE pos_sesiones
                   SET cerrada_en = now(), cerrada_por = {ctx.UsuarioId!.Value},
                       efectivo_esperado = {arqueo.Esperado}, efectivo_contado = {contado}, diferencia = {diferencia},
                       observacion_cierre = {(observacion.Length > 0 ? Recortar(observacion, 250) : null)},
                       deja_en_caja = {deja}, entrega = {entrega},
                       resumen = {arqueo.Json}::jsonb
                 WHERE id = {s.Id} AND cerrada_en IS NULL
                """, ct);

            return Results.Ok(new
            {
                id = s.Id,
                mensaje = diferencia == 0
                    ? "Caja cerrada. Cuadró exacto."
                    : $"Caja cerrada con una diferencia de S/ {diferencia:0.00}."
            });
        })
        .RequierePermiso(P.Vender)
        .RequiereModulo(Modulos.Facturacion)
        .RequiereModulo(ModuloPos)
        .WithSummary("Cerrar mi caja: lo contado y la diferencia");


        // =====================================================================
        // HISTORIAL Y ARQUEO DE UNA CAJA
        // =====================================================================
        g.MapGet("/cajas", async (int? pagina, Guid? sucursalId, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var p = Math.Max(1, pagina ?? 1);
            Guid? soloDe = ctx.Tiene(P.CajasVer) ? null : ctx.UsuarioId;
            var json = await db.Database.SqlQuery<string>($"""
                WITH f AS (
                    SELECT s.* FROM pos_sesiones s
                     WHERE ({soloDe}::uuid IS NULL OR s.usuario_id = {soloDe}::uuid)
                       AND ({sucursalId}::uuid IS NULL OR s.sucursal_id = {sucursalId}::uuid)
                ), pag AS (
                    SELECT f.*, k.nombre AS caja, su.codigo || ' ' || su.nombre AS sucursal, u.nombre AS usuario,
                           (SELECT count(*) FROM pos_ventas v JOIN documentos d ON d.id = v.documento_id
                             WHERE v.sesion_id = f.id AND d.estado IN ('encolado','aceptado','observado')) AS ventas,
                           (SELECT coalesce(sum(v.total), 0) FROM pos_ventas v JOIN documentos d ON d.id = v.documento_id
                             WHERE v.sesion_id = f.id AND d.estado IN ('encolado','aceptado','observado')) AS vendido
                      FROM f JOIN pos_cajas k ON k.id = f.caja_id JOIN sucursales su ON su.id = f.sucursal_id
                      JOIN usuarios u ON u.id = f.usuario_id
                     ORDER BY f.cerrada_en IS NULL DESC, f.abierta_en DESC
                     OFFSET {(p - 1) * 20} LIMIT 20
                )
                SELECT json_build_object(
                    'total', (SELECT count(*) FROM f), 'pagina', {p}, 'porPagina', 20,
                    'filas', coalesce((SELECT json_agg(json_build_object(
                        'id', id, 'caja', caja, 'sucursal', sucursal, 'usuario', usuario,
                        'abiertaEn', abierta_en, 'cerradaEn', cerrada_en, 'fondo', fondo_inicial,
                        'ventas', ventas, 'vendido', vendido,
                        'esperado', efectivo_esperado, 'contado', efectivo_contado, 'diferencia', diferencia)) FROM pag), '[]'::json)
                )::text AS "Value"
                """).FirstAsync(ct);
            return Results.Content(json, "application/json");
        })
        .RequierePermiso(P.Vender)
        .RequiereModulo(ModuloPos)
        .WithSummary("Aperturas y cierres de caja (las mías, o todas con pos.cajas_ver)");

        g.MapGet("/cajas/{id:guid}", async (Guid id, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var duenio = await db.Database.SqlQuery<Guid?>($"SELECT usuario_id AS \"Value\" FROM pos_sesiones WHERE id = {id}").FirstOrDefaultAsync(ct);
            if (duenio is null) return Results.NotFound();
            if (duenio != ctx.UsuarioId && !ctx.Tiene(P.CajasVer))
                return Results.Json(new { error = "Solo ves tus cajas." }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Content(await DetalleAsync(db, id, ct), "application/json");
        })
        .RequierePermiso(P.Vender)
        .RequiereModulo(ModuloPos)
        .WithSummary("El arqueo de una caja, con sus ventas y movimientos");
    }


    // =========================================================================

    private static IResult Mal(string error) => Results.BadRequest(new { error });

    private static string Recortar(string s, int n) => s.Length > n ? s[..n] : s;

    private static async Task<Sesion?> SesionAbiertaAsync(ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        if (ctx.UsuarioId is not { } u) return null;
        var json = await db.Database.SqlQuery<string?>($"""
            SELECT json_build_object('id', s.id, 'cajaId', s.caja_id, 'caja', k.nombre, 'sucursalId', s.sucursal_id,
                                     'sucursal', su.codigo || ' ' || su.nombre, 'abiertaEn', s.abierta_en,
                                     'fondo', s.fondo_inicial)::text AS "Value"
              FROM pos_sesiones s JOIN pos_cajas k ON k.id = s.caja_id JOIN sucursales su ON su.id = s.sucursal_id
             WHERE s.usuario_id = {u} AND s.cerrada_en IS NULL
            """).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<Sesion>(json, Json);
    }

    /// <summary>El borrador que no llegó a ser venta: se desactiva (nada se borra).</summary>
    private static async Task DescartarAsync(Documento d, ContextoPlataforma db, CancellationToken ct)
    {
        if (d.Estado != EstadoDocumento.Borrador) return;
        d.Activo = false;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<IResult> ResponderAsync(IResult r, Guid documentoId, ContextoPlataforma db, CancellationToken ct, decimal vuelto = 0)
    {
        var codigo = (r as IStatusCodeHttpResult)?.StatusCode ?? 200;
        if (codigo is >= 200 and < 300)
        {
            var d = await db.Documentos.AsNoTracking().Where(x => x.Id == documentoId)
                .Select(x => new { x.Id, x.Serie, x.Correlativo, x.Estado, x.ImporteTotal, x.Tipo, x.ComprobanteId }).FirstAsync(ct);
            return Results.Ok(new
            {
                d.Id, numero = d.Correlativo is { } n ? $"{d.Serie}-{n:D8}" : d.Serie, d.Estado, total = d.ImporteTotal,
                d.Tipo, d.ComprobanteId, vuelto,
                mensaje = $"{(d.Tipo == "01" ? "Factura" : "Boleta")} {d.Serie}-{d.Correlativo:D8} emitida."
            });
        }
        // El servicio no respondió: la venta quedó en la caja con su comprobante en «error».
        if (codigo == StatusCodes.Status502BadGateway)
            return Results.Json(new
            {
                error = "No se pudo enviar el comprobante al servicio de facturación. La venta quedó guardada: reintenta.",
                documentoId,
                // La pantalla lee el «codigo» de los errores: así sabe qué reintentar.
                codigo = $"reintentar:{documentoId}"
            }, statusCode: StatusCodes.Status502BadGateway);
        return r;
    }

    private sealed record Arqueo(decimal Esperado, string Json);

    /// <summary>
    /// Lo que dice el sistema: fondo + ventas en efectivo + ingresos − retiros,
    /// contando solo los comprobantes emitidos (no los rechazados ni los que no llegaron).
    /// </summary>
    private static async Task<Arqueo> ArqueoAsync(ContextoPlataforma db, Guid sesion, CancellationToken ct)
    {
        var json = await db.Database.SqlQuery<string>($"""
            WITH vv AS (
                SELECT v.* FROM pos_ventas v JOIN documentos d ON d.id = v.documento_id
                 WHERE v.sesion_id = {sesion} AND d.estado IN ('encolado','aceptado','observado')
            ), pg AS (
                SELECT p.medio, count(DISTINCT p.venta_id) AS ventas, sum(p.monto) AS monto
                  FROM pos_pagos p JOIN vv ON vv.id = p.venta_id GROUP BY p.medio
            )
            SELECT json_build_object(
                'fondo', (SELECT fondo_inicial FROM pos_sesiones WHERE id = {sesion}),
                'medios', coalesce((SELECT json_agg(json_build_object('medio', medio, 'ventas', ventas, 'monto', monto) ORDER BY medio) FROM pg), '[]'::json),
                'ventas', (SELECT count(*) FROM vv),
                'vendido', coalesce((SELECT sum(total) FROM vv), 0),
                'efectivo', coalesce((SELECT monto FROM pg WHERE medio = 'efectivo'), 0),
                'ingresos', coalesce((SELECT sum(monto) FROM pos_movimientos WHERE sesion_id = {sesion} AND tipo = 'ingreso'), 0),
                'retiros', coalesce((SELECT sum(monto) FROM pos_movimientos WHERE sesion_id = {sesion} AND tipo = 'retiro'), 0),
                'retirosCuantos', (SELECT count(*) FROM pos_movimientos WHERE sesion_id = {sesion} AND tipo = 'retiro'),
                'ingresosCuantos', (SELECT count(*) FROM pos_movimientos WHERE sesion_id = {sesion} AND tipo = 'ingreso'),
                'noCuentan', (SELECT count(*) FROM pos_ventas v JOIN documentos d ON d.id = v.documento_id
                               WHERE v.sesion_id = {sesion} AND d.estado NOT IN ('encolado','aceptado','observado'))
            )::text AS "Value"
            """).FirstAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        decimal D(string k) => r.GetProperty(k).GetDecimal();
        var esperado = D("fondo") + D("efectivo") + D("ingresos") - D("retiros");
        return new Arqueo(Math.Round(esperado, 2), json);
    }

    /// <summary>El arqueo completo: la caja, lo que dice el sistema, las ventas y los movimientos.</summary>
    private static async Task<string> DetalleAsync(ContextoPlataforma db, Guid sesion, CancellationToken ct)
    {
        var arqueo = await ArqueoAsync(db, sesion, ct);
        return await db.Database.SqlQuery<string>($"""
            SELECT json_build_object(
                'sesion', (SELECT json_build_object(
                    'id', s.id, 'caja', k.nombre, 'sucursal', su.codigo || ' ' || su.nombre, 'usuario', u.nombre,
                    'abiertaEn', s.abierta_en, 'fondo', s.fondo_inicial, 'notaApertura', s.nota_apertura,
                    'cerradaEn', s.cerrada_en, 'cerradaPor', (SELECT nombre FROM usuarios WHERE id = s.cerrada_por),
                    'esperado', s.efectivo_esperado, 'contado', s.efectivo_contado, 'diferencia', s.diferencia,
                    'observacion', s.observacion_cierre, 'dejaEnCaja', s.deja_en_caja, 'entrega', s.entrega,
                    'resumen', s.resumen)
                    FROM pos_sesiones s JOIN pos_cajas k ON k.id = s.caja_id JOIN sucursales su ON su.id = s.sucursal_id
                    JOIN usuarios u ON u.id = s.usuario_id WHERE s.id = {sesion}),
                'arqueo', {arqueo.Json}::json,
                'esperado', {arqueo.Esperado},
                'movimientos', coalesce((SELECT json_agg(json_build_object(
                    'tipo', m.tipo, 'monto', m.monto, 'motivo', m.motivo, 'detalle', m.detalle, 'en', m.creado_en,
                    'por', (SELECT nombre FROM usuarios WHERE id = m.creado_por)) ORDER BY m.creado_en DESC)
                    FROM pos_movimientos m WHERE m.sesion_id = {sesion}), '[]'::json),
                'ventas', coalesce((SELECT json_agg(x ORDER BY x.en DESC) FROM (
                    SELECT v.documento_id AS id, d.tipo, d.estado, v.total, v.vuelto, v.creado_en AS en,
                           CASE WHEN d.correlativo IS NOT NULL THEN d.serie || '-' || lpad(d.correlativo::text, 8, '0') ELSE d.serie END AS numero,
                           c.razon_social AS cliente,
                           (SELECT json_agg(json_build_object('medio', p.medio, 'monto', p.monto, 'referencia', p.referencia))
                              FROM pos_pagos p WHERE p.venta_id = v.id) AS pagos
                      FROM pos_ventas v JOIN documentos d ON d.id = v.documento_id JOIN clientes c ON c.id = d.cliente_id
                     WHERE v.sesion_id = {sesion}
                     ORDER BY v.creado_en DESC LIMIT 300) x), '[]'::json)
            )::text AS "Value"
            """).FirstAsync(ct);
    }
}