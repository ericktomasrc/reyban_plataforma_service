using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Api.Inventario;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Avisos;

/// <summary>Un aviso de la campanita.</summary>
/// <param name="Clave">Cambia cuando cambia la situación: así vuelve a contar como «sin leer».</param>
/// <param name="Nivel">«rojo», «naranja», «verde» o «info».</param>
public sealed record Aviso(string Clave, string Tipo, string Nivel, string Titulo, string? Detalle, string? Enlace, DateTime? Fecha, bool Leido);

public sealed record PeticionLeidos(string[]? Claves);

/// <summary>
/// e177 (058) — LA CAMPANITA 🔔.
///
/// LOS AVISOS NO SE GUARDAN: se calculan al pedirlos, con lo que hay ahora
/// (y con el alcance de quien pregunta: la regla de `documentos` recorta sola).
/// Un aviso guardado se queda diciendo algo que ya se arregló; uno calculado,
/// no. Lo único que se guarda es qué vio cada persona (avisos_leidos).
///
/// LO QUE ENTRA (cada uno solo para quien tiene el permiso de verlo):
///   · certificado por vencer (todas las empresas de la cartera, o la abierta)
///   · comprobantes rechazados o sin enviar (7 días)
///   · bajas o anulaciones pedidas que SUNAT no confirmó en 48 horas
///   · guías rechazadas o sin enviar (7 días)
///   · stock bajo el mínimo o en negativo
///   · traslados por recibir en mis sucursales
///   · caja abierta desde un día anterior (sin cerrar)
///   · personas nuevas en la empresa (7 días)
///   · mi ficha incompleta
///
/// LOS COLORES del certificado: rojo vencido o 7 días o menos, naranja de 8 a
/// 30, verde más de 30 (ese no avisa). Los correos salen desde la 025.
/// </summary>
public static class EndpointsAvisos
{
    public sealed class FilaCert { public Guid Id { get; set; } public string Nombre { get; set; } = ""; public DateTime? CertificadoHasta { get; set; } }
    public sealed class FilaConteo { public int Cuantos { get; set; } public DateTime? Ultimo { get; set; } }
    public sealed class FilaPersona { public Guid Id { get; set; } public string Nombre { get; set; } = ""; public DateTime CreadoEn { get; set; } public DateTime? UltimoIngresoEn { get; set; } }

    public static void MapearAvisos(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/avisos").WithTags("Avisos");

        grupo.MapGet("/", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var (lista, diasCert) = await CalcularAsync(db, ctx, ct);
            var sinLeer = lista.Where(a => !a.Leido).ToList();
            return Results.Ok(new
            {
                avisos = lista,
                sinLeer = sinLeer.Count,
                // El color de la campanita: el peor de los sin leer.
                nivel = Peor(sinLeer.Select(a => a.Nivel)),
                // El certificado, aparte, para el menú: el peor y cuántas empresas.
                certificado = new
                {
                    empresas = lista.Count(a => a.Tipo == "certificado"),
                    // Rojo vencido o ≤ 7 días, naranja 8 a 30, verde más de 30 (sin certificado: nada).
                    nivel = diasCert is null ? null : diasCert <= 7 ? "rojo" : diasCert <= 30 ? "naranja" : "verde",
                    dias = diasCert,
                }
            });
        })
        .RequiereSesion()
        .WithSummary("La campanita: lo que pide atención ahora");

        grupo.MapPost("/leidos", async (PeticionLeidos p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.UsuarioId is not { } yo) return Results.Unauthorized();
            var claves = (p.Claves ?? []).Where(c => c.Length is >= 3 and <= 200).Distinct().Take(100).ToArray();
            foreach (var c in claves)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO avisos_leidos (usuario_id, clave, creado_por) VALUES ({yo}, {c}, {yo})
                    ON CONFLICT (usuario_id, clave) DO NOTHING
                    """, ct);
            return Results.Ok(new { leidos = claves.Length });
        })
        .RequiereSesion()
        .WithSummary("Marcar avisos como leídos");
    }

    private static readonly string[] Orden = ["rojo", "naranja", "info", "verde"];
    private static string? Peor(IEnumerable<string> niveles) =>
        niveles.OrderBy(n => Array.IndexOf(Orden, n)).FirstOrDefault();

    /// <returns>Los avisos y los días que le quedan al certificado que vence primero (null sin certificado).</returns>
    public static async Task<(List<Aviso> Avisos, int? DiasCertificado)> CalcularAsync(ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        var lista = new List<(string clave, string tipo, string nivel, string titulo, string? detalle, string? enlace, DateTime? fecha)>();
        if (ctx.UsuarioId is not { } yo) return ([], null);

        // --- 1. Certificado por vencer -------------------------------------------
        // mis_empresas(): las de la cartera que veo (o la mía). Si no da nada, la abierta.
        // e193: la plataforma no (sus empresas tienen su propio aviso en Empresas).
        var certs = ctx.EsSuperAdmin ? [] : await db.Database.SqlQuery<FilaCert>($"""
            SELECT m.id, m.nombre, m.certificado_hasta FROM mis_empresas() m
             WHERE m.certificado_hasta IS NOT NULL AND m.pausada_en IS NULL
            """).ToListAsync(ct);
        if (certs.Count == 0 && !ctx.EsSuperAdmin && ctx.EmpresaId is { } eid)
            certs = await db.Database.SqlQuery<FilaCert>($"""
                SELECT e.id, coalesce(e.nombre, e.razon_social, '') AS nombre, c.certificado_valido_hasta AS certificado_hasta
                  FROM empresas e JOIN credenciales_facturacion c ON c.empresa_id = e.id AND c.activo
                 WHERE e.id = {eid} AND c.certificado_valido_hasta IS NOT NULL
                """).ToListAsync(ct);
        int? diasCert = null;
        foreach (var c in certs)
        {
            var dias = DiasPara(c.CertificadoHasta);
            if (dias is { } dd && (diasCert is null || dd < diasCert)) diasCert = dd;
            if (dias is null || dias > 30 || dias < -30) continue;
            var tramo = dias < 0 ? -1 : dias == 0 ? 0 : dias <= 1 ? 1 : dias <= 7 ? 7 : 30;
            var titulo = dias < 0 ? $"El certificado de {c.Nombre} venció hace {-dias} día(s)"
                       : dias == 0 ? $"El certificado de {c.Nombre} vence hoy"
                       : $"El certificado de {c.Nombre} vence en {dias} día(s)";
            lista.Add(($"cert:{c.Id}:{c.CertificadoHasta:yyyyMMdd}:{tramo}", "certificado", dias <= 7 ? "rojo" : "naranja", titulo,
                       dias < 0 ? "SUNAT rechaza todo lo que se emita con él. Sube el certificado nuevo en la configuración de la empresa."
                                : "Cuando venza, SUNAT rechazará todo lo que se emita. Renuévalo y súbelo en la configuración de la empresa.",
                       "/configurar", c.CertificadoHasta));
        }

        if (ctx.EmpresaId is { } empresaId)
        {
            // --- 2. Comprobantes rechazados o sin enviar (7 días) -------------------
            if (ctx.Tiene(Permisos.Facturacion.Ver))
            {
                var r = await UnoAsync(db, $"""
                    SELECT count(*)::int AS cuantos, max(creado_en) AS ultimo FROM documentos
                     WHERE activo AND estado IN ('rechazado', 'error') AND creado_en > now() - interval '7 days'
                       AND NOT (estado = 'rechazado' AND (tipo = '03' OR (tipo IN ('07', '08') AND serie LIKE 'B%')))   -- e194: van aparte (resumen)
                    """, ct);
                if (r.Cuantos > 0)
                    lista.Add(($"docs:{r.Cuantos}:{r.Ultimo:yyyyMMddHHmmss}", "comprobantes", "rojo",
                               r.Cuantos == 1 ? "1 comprobante rechazado o sin enviar" : $"{r.Cuantos} comprobantes rechazados o sin enviar",
                               "En los últimos 7 días. Revisa qué pasó y corrígelo.", "/facturacion/comprobantes", r.Ultimo));

                // --- 3. Bajas pedidas que SUNAT no confirmó en 48 horas ----------------
                var b = await UnoAsync(db, $"""
                    SELECT count(*)::int AS cuantos, max(creado_en) AS ultimo FROM documentos
                     WHERE activo AND estado IN ('aceptado', 'observado')
                       AND datos_extra->>'_bajaPedidaEn' IS NOT NULL
                       AND (datos_extra->>'_bajaPedidaEn')::timestamptz < now() - interval '48 hours'
                       AND (datos_extra->>'_bajaPedidaEn')::timestamptz > now() - interval '30 days'
                    """, ct);
                if (b.Cuantos > 0)
                    lista.Add(($"bajas:{b.Cuantos}:{b.Ultimo:yyyyMMddHHmmss}", "bajas", "naranja",
                               b.Cuantos == 1 ? "1 baja o anulación sin confirmar" : $"{b.Cuantos} bajas o anulaciones sin confirmar",
                               "Se pidieron hace más de 48 horas y SUNAT no las aceptó: el comprobante sigue vivo.",
                               "/reportes/anulados", b.Ultimo));
            }

            // --- 3a. e194: EL RESUMEN DIARIO FUE RECHAZADO ------------------------------
            // Una boleta solo llega a SUNAT dentro del resumen diario: si sale
            // rechazada es que SUNAT rechazó su resumen (el servicio le pasa el
            // código y el motivo a cada boleta). No está informada.
            if (ctx.Tiene(Permisos.Facturacion.Ver))
            {
                var rr = await db.Database.SqlQuery<FilaResumen>($"""
                    SELECT fecha_emision AS dia, count(*)::int AS cuantos, max(codigo_sunat) AS codigo, max(mensaje_sunat) AS mensaje,
                           max(modificado_en) AS ultimo
                      FROM documentos
                     WHERE activo AND estado = 'rechazado'
                       AND (tipo = '03' OR (tipo IN ('07', '08') AND serie LIKE 'B%'))
                       AND fecha_emision > (now() AT TIME ZONE 'America/Lima')::date - 30
                     GROUP BY fecha_emision
                     ORDER BY fecha_emision
                    """).ToListAsync(ct);
                foreach (var x in rr)
                {
                    var quedan = 7 - (DateTime.UtcNow.AddHours(-5).Date - x.Dia.ToDateTime(TimeOnly.MinValue)).Days;
                    lista.Add(($"resumenrech:{x.Dia:yyyyMMdd}:{x.Cuantos}", "resumen", "rojo",
                               $"SUNAT rechazó el resumen de boletas del {x.Dia:dd/MM}: {x.Cuantos} {(x.Cuantos == 1 ? "boleta no está informada" : "boletas no están informadas")}",
                               (x.Codigo is null ? "" : $"Código {x.Codigo}: ") + (x.Mensaje ?? "sin detalle") + ". " +
                               (quedan > 0 ? $"Abre cada una para ver qué corregir y emítela de nuevo (sale con el número siguiente). Quedan {quedan} día(s) del plazo de SUNAT." : "Ya pasaron los 7 días del plazo de SUNAT: consulta con tu contador antes de emitirlas de nuevo."),
                               "/facturacion/comprobantes", x.Ultimo));
                }
            }

            // --- 3b. e182: boletas que siguen sin respuesta (el resumen diario) ------
            // Una boleta se informa en el resumen del día siguiente. Si a los
            // dos días sigue «en camino», el resumen falló o lo rechazaron, y
            // SUNAT solo da 7 días para informarla.
            if (ctx.Tiene(Permisos.Facturacion.Ver))
            {
                var rb = await UnoAsync(db, $"""
                    SELECT count(*)::int AS cuantos, min(fecha_emision)::timestamp AS ultimo FROM documentos
                     WHERE activo AND estado = 'encolado'
                       AND (tipo = '03' OR (tipo IN ('07', '08') AND serie LIKE 'B%'))
                       AND fecha_emision < (now() AT TIME ZONE 'America/Lima')::date - 2
                       AND fecha_emision > (now() AT TIME ZONE 'America/Lima')::date - 30
                    """, ct);
                if (rb.Cuantos > 0)
                {
                    var dias = rb.Ultimo is { } mas ? (DateTime.UtcNow.AddHours(-5).Date - mas.Date).Days : 0;
                    lista.Add(($"resumen:{rb.Cuantos}:{rb.Ultimo:yyyyMMdd}", "resumen", dias >= 5 ? "rojo" : "naranja",
                               rb.Cuantos == 1 ? "1 boleta sigue sin respuesta de SUNAT" : $"{rb.Cuantos} boletas siguen sin respuesta de SUNAT",
                               $"Van en el resumen diario y no llegó la respuesta (la más antigua es de hace {dias} días). SUNAT da 7 días para informarlas: revísalas en «En camino».",
                               "/facturacion/comprobantes", rb.Ultimo));
                }
            }

            // --- 4. Guías rechazadas o sin enviar (7 días) --------------------------
            if (ctx.Tiene(Permisos.Guias.Ver))
            {
                var g = await UnoAsync(db, $"""
                    SELECT count(*)::int AS cuantos, max(creado_en) AS ultimo FROM guias
                     WHERE estado IN ('rechazado', 'error') AND creado_en > now() - interval '7 days'
                    """, ct);
                if (g.Cuantos > 0)
                    lista.Add(($"guias:{g.Cuantos}:{g.Ultimo:yyyyMMddHHmmss}", "guias", "rojo",
                               g.Cuantos == 1 ? "1 guía rechazada o sin enviar" : $"{g.Cuantos} guías rechazadas o sin enviar",
                               "Sin guía aceptada la mercadería no debe salir.", "/guias", g.Ultimo));
            }

            // --- 5 y 6. Inventario: stock y traslados -------------------------------
            if (ctx.Tiene(Permisos.Inventario.Ver) && await ReglasInventario.ConfigAsync(db, empresaId, ct) is { Encendido: true } cfg)
            {
                var ids = (await EndpointsInventario.VisiblesAsync(db, ctx, empresaId, ct)).Select(v => v.Id).ToArray();
                var s = await UnoAsync(db, $"""
                    SELECT count(*) FILTER (WHERE s.fisico < 0 OR ({cfg.AvisarMinimo} AND s.minimo IS NOT NULL AND s.fisico - s.reservado < s.minimo))::int AS cuantos,
                           NULL::timestamptz AS ultimo
                      FROM inv_stock s
                      JOIN productos p ON p.id = s.producto_id AND p.activo AND p.controla_stock
                     WHERE s.empresa_id = {empresaId} AND s.sucursal_id = ANY ({ids})
                    """, ct);
                if (s.Cuantos > 0)
                    lista.Add(($"stock:{s.Cuantos}", "stock", "naranja",
                               s.Cuantos == 1 ? "1 producto bajo el mínimo o en negativo" : $"{s.Cuantos} productos bajo el mínimo o en negativo",
                               "Revisa el stock y repón lo que falta.", "/inventario", null));

                // 064: lotes vencidos con stock, o que vencen en 30 días.
                var lv = await UnoAsync(db, $"""
                    SELECT count(*)::int AS cuantos, NULL::timestamptz AS ultimo
                      FROM inv_lotes l JOIN productos p ON p.id = l.producto_id AND p.maneja_lotes AND p.activo
                     WHERE l.empresa_id = {empresaId} AND l.sucursal_id = ANY ({ids}) AND l.fisico > 0
                       AND l.vence <= (now() AT TIME ZONE 'America/Lima')::date + 30
                    """, ct);
                if (lv.Cuantos > 0)
                {
                    var vencidos = await UnoAsync(db, $"""
                        SELECT count(*)::int AS cuantos, NULL::timestamptz AS ultimo
                          FROM inv_lotes l JOIN productos p ON p.id = l.producto_id AND p.maneja_lotes AND p.activo
                         WHERE l.empresa_id = {empresaId} AND l.sucursal_id = ANY ({ids}) AND l.fisico > 0
                           AND l.vence < (now() AT TIME ZONE 'America/Lima')::date
                        """, ct);
                    lista.Add(($"lotes:{lv.Cuantos}:{vencidos.Cuantos}:{DateTime.UtcNow:yyyyMMdd}", "lotes", vencidos.Cuantos > 0 ? "rojo" : "naranja",
                               vencidos.Cuantos > 0
                                   ? (vencidos.Cuantos == 1 ? "1 lote vencido con stock" : $"{vencidos.Cuantos} lotes vencidos con stock")
                                   : (lv.Cuantos == 1 ? "1 lote vence en los próximos 30 días" : $"{lv.Cuantos} lotes vencen en los próximos 30 días"),
                               "Revisa Lotes y vencimientos: véndelos primero o sácalos con un ajuste «Vencido».", "/inventario/lotes", null));
                }

                var t = await UnoAsync(db, $"""
                    SELECT count(*)::int AS cuantos, max(salio_en) AS ultimo FROM inv_traslados
                     WHERE activo AND estado = 'en_transito' AND destino_id = ANY ({ids})
                    """, ct);
                if (t.Cuantos > 0)
                    lista.Add(($"traslados:{t.Cuantos}:{t.Ultimo:yyyyMMddHHmmss}", "traslados", "info",
                               t.Cuantos == 1 ? "1 traslado por recibir" : $"{t.Cuantos} traslados por recibir",
                               "Cuando llegue la mercadería, márcala como recibida.", "/inventario/traslados", t.Ultimo));
            }

            // --- 7. Caja sin cerrar desde un día anterior ---------------------------
            var todas = ctx.Tiene("pos.cajas_ver");
            var cj = await UnoAsync(db, $"""
                SELECT count(*)::int AS cuantos, min(abierta_en) AS ultimo FROM pos_sesiones
                 WHERE cerrada_en IS NULL
                   AND (abierta_en AT TIME ZONE 'America/Lima')::date < (now() AT TIME ZONE 'America/Lima')::date
                   AND ({todas} OR usuario_id = {yo})
                """, ct);
            if (cj.Cuantos > 0)
                lista.Add(($"caja:{cj.Cuantos}:{cj.Ultimo:yyyyMMddHHmmss}", "caja", "naranja",
                           cj.Cuantos == 1 ? "Una caja quedó abierta desde un día anterior" : $"{cj.Cuantos} cajas quedaron abiertas desde un día anterior",
                           "Ciérrala con su arqueo para que cuadre el efectivo.", "/pos/cajas", cj.Ultimo));

            // --- 8. Personas nuevas (7 días) ---------------------------------------
            if (ctx.Tiene(Permisos.Usuarios.Ver))
            {
                var nuevas = await db.Database.SqlQuery<FilaPersona>($"""
                    SELECT id, nombre, creado_en, ultimo_ingreso_en FROM usuarios
                     WHERE empresa_id = {empresaId} AND activo AND id <> {yo} AND creado_en > now() - interval '7 days'
                     ORDER BY creado_en DESC LIMIT 10
                    """).ToListAsync(ct);
                foreach (var n in nuevas)
                    lista.Add(($"persona:{n.Id}:{(n.UltimoIngresoEn is null ? 0 : 1)}", "persona", "info",
                               n.UltimoIngresoEn is null ? $"{n.Nombre} fue invitado al equipo" : $"{n.Nombre} ya entró: aceptó la invitación",
                               null, "/usuarios", n.UltimoIngresoEn ?? n.CreadoEn));
            }
        }

        // --- 8b. e193: TRASPASOS Y RECLAMOS -----------------------------------------
        // a) La plataforma: solicitudes de traspaso (reclamos con documentos) por revisar.
        if (ctx.EsSuperAdmin && ctx.Tiene(Permisos.Plataforma.EmpresasGestionar))
        {
            var st = await UnoAsync(db, $"""
                SELECT count(*)::int AS cuantos, max(creado_en) AS ultimo FROM solicitudes_traspaso_lista() WHERE estado = 'por_revisar'
                """, ct);
            if (st.Cuantos > 0)
                lista.Add(($"solicitudes:{st.Cuantos}:{st.Ultimo:yyyyMMddHHmmss}", "traspaso", "naranja",
                           st.Cuantos == 1 ? "1 solicitud de traspaso por revisar" : $"{st.Cuantos} solicitudes de traspaso por revisar",
                           "Alguien reclama una empresa con documentos: revísalos y aprueba o rechaza.", "/admin/traspasos", st.Ultimo));
        }
        if (!ctx.EsSuperAdmin)
        {
            // b) Reclamaron una empresa que tengo: respondo (de acuerdo o me opongo) dentro del plazo.
            var reclamos = await db.Database.SqlQuery<FilaReclamo>($"""
                SELECT id, empresa_id, empresa, plazo_hasta FROM reclamos_sobre_mis_empresas() WHERE puedo_responder
                """).ToListAsync(ct);
            foreach (var rc in reclamos)
                lista.Add(($"reclamo:{rc.Id}", "reclamo", "rojo", $"Reclamaron la empresa {rc.Empresa}",
                           $"Quien dice ser su titular pidió llevársela. Responde antes del {rc.PlazoHasta.AddHours(-5):dd/MM/yyyy HH:mm}: de acuerdo, o te opones con un motivo.",
                           rc.EmpresaId == ctx.EmpresaId ? "/" : "/empresas", rc.PlazoHasta));   // e195: la abierta (Cuenta Negocio) responde en su Inicio

            // c) Mis pedidos para llevar una empresa (camino B) que el titular ya respondió (7 días).
            var pedidos = await db.Database.SqlQuery<FilaPedido>($"""
                SELECT id, empresa, estado, respondido_en FROM mis_pedidos()
                 WHERE estado IN ('aprobado', 'rechazado', 'vencido') AND coalesce(respondido_en, expira_en) > now() - interval '7 days'
                """).ToListAsync(ct);
            foreach (var pd in pedidos)
                lista.Add(($"pedido:{pd.Id}:{pd.Estado}", "traspaso", pd.Estado == "aprobado" ? "verde" : "info",
                           pd.Estado switch
                           {
                               "aprobado" => $"{pd.Empresa} aceptó: ya está en tu cartera",
                               "rechazado" => $"{pd.Empresa} no aceptó el traspaso",
                               _ => $"Tu pedido para llevar {pd.Empresa} venció sin respuesta"
                           },
                           pd.Estado == "aprobado" ? "Entra a la empresa y revisa sus series y su conexión con SUNAT." : null,
                           "/empresas", pd.RespondidoEn));
        }

        // --- 9. Mi ficha incompleta ----------------------------------------------
        var u = await db.Usuarios.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == yo)
            .Select(x => new { x.TipoDocumento, x.NumeroDocumento, x.Nombres, x.ApellidoPaterno, x.ApellidoMaterno })
            .FirstOrDefaultAsync(ct);
        if (u is not null && !ctx.EsSuperAdmin && FiltroFicha.Incompleta(u.TipoDocumento, u.NumeroDocumento, u.Nombres, u.ApellidoPaterno, u.ApellidoMaterno))
            lista.Add(($"ficha:{yo}", "ficha", "naranja", "Completa tus datos",
                       "Tu documento y tu nombre completo: sin ellos no podrás emitir.", "/perfil", null));

        // Leídos
        var claves = lista.Select(a => a.clave).ToArray();
        var leidas = claves.Length == 0 ? [] : await db.Database.SqlQuery<string>($"""
            SELECT clave AS "Value" FROM avisos_leidos WHERE usuario_id = {yo} AND clave = ANY ({claves})
            """).ToListAsync(ct);
        var vistos = leidas.ToHashSet();

        return (lista
            .Select(a => new Aviso(a.clave, a.tipo, a.nivel, a.titulo, a.detalle, a.enlace, a.fecha, vistos.Contains(a.clave)))
            .OrderBy(a => a.Leido).ThenBy(a => Array.IndexOf(Orden, a.Nivel)).ThenByDescending(a => a.Fecha)
            .ToList(), diasCert);
    }

    /// <summary>Días que faltan (por fecha de Lima): 0 = vence hoy, negativo = ya venció.</summary>
    private static int? DiasPara(DateTime? hasta)
    {
        if (hasta is not { } h) return null;
        var lima = TimeSpan.FromHours(-5);
        var dia = DateOnly.FromDateTime(DateTime.SpecifyKind(h, DateTimeKind.Utc) + lima);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow + lima);
        return dia.DayNumber - hoy.DayNumber;
    }

    private static async Task<FilaConteo> UnoAsync(ContextoPlataforma db, FormattableString sql, CancellationToken ct) =>
        (await db.Database.SqlQuery<FilaConteo>(sql).ToListAsync(ct)).FirstOrDefault() ?? new FilaConteo();
    public sealed class FilaReclamo
    {
        public Guid Id { get; set; }
        public Guid EmpresaId { get; set; }
        public string Empresa { get; set; } = "";
        public DateTime PlazoHasta { get; set; }
    }

    public sealed class FilaPedido
    {
        public Guid Id { get; set; }
        public string Empresa { get; set; } = "";
        public string Estado { get; set; } = "";
        public DateTime? RespondidoEn { get; set; }
    }
    public sealed class FilaResumen
    {
        public DateOnly Dia { get; set; }
        public int Cuantos { get; set; }
        public string? Codigo { get; set; }
        public string? Mensaje { get; set; }
        public DateTime? Ultimo { get; set; }
    }
}