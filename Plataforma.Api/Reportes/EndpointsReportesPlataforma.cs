using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;

namespace Plataforma.Api.Reportes;

/// <summary>
/// 036 — REPORTES, entrega 3: «Mi cartera» (contador) y «Plataforma» (super).
///
///   GET /api/reportes/cartera                  Mis empresas del mes y certificados (quien tiene cartera)
///   GET /api/admin/reportes/plataforma         Tablero de la plataforma
///   GET /api/admin/reportes/empresas           Comprobantes por empresa y mes
///   GET /api/admin/reportes/rechazos           Rechazos SUNAT por código
///   GET /api/admin/reportes/cuentas            Cuentas nuevas y consultas RUC/DNI
///   GET /api/admin/reportes/activacion         e148: Avance de activación (en qué paso se quedó cada cuenta)
///   GET /api/admin/reportes/riesgo             e148: Empresas en riesgo (hoy)
///   GET /api/admin/reportes/consumo            e148: Consumo por cuenta y mes
///
/// ?desde&amp;hasta (AAAA-MM-DD), &amp;formato=xlsx. Solo lectura. La cartera
/// sale de reporte_cartera() (las mismas empresas que «Mis empresas»); lo del
/// super administrador lo deja ver el aislamiento, y la ruta exige serlo.
/// La empresa de control (20999999990) no cuenta.
/// </summary>
public static class EndpointsReportesPlataforma
{
    private const string SqlPlataforma = """
WITH d AS (
    SELECT d.*, CASE WHEN d.ambiente = 'produccion' THEN 'real' ELSE 'prueba' END AS amb
      FROM documentos d JOIN empresas e ON e.id = d.empresa_id
     WHERE d.activo AND coalesce(e.ruc, '') <> '20999999990'
       AND d.tipo IN ('01', '03', '07', '08') AND d.serie IS NOT NULL
       AND d.fecha_emision BETWEEN {0} AND {1}
)
SELECT json_build_object(
    'empresas',      (SELECT count(*) FROM empresas e WHERE e.activo AND coalesce(e.ruc, '') <> '20999999990'),
    'empresasReal',  (SELECT count(*) FROM empresas e WHERE e.activo AND coalesce(e.ruc, '') <> '20999999990' AND e.ambiente = 'produccion'),
    'cuentas',       (SELECT count(*) FROM cuentas c WHERE c.activo),
    'cuentasNuevas', (SELECT count(*) FROM cuentas c WHERE (c.creado_en AT TIME ZONE 'America/Lima')::date BETWEEN {0} AND {1}),
    'reales',        (SELECT count(*) FROM d WHERE amb = 'real' AND estado IN ('encolado', 'aceptado', 'observado')),
    'prueba',        (SELECT count(*) FROM d WHERE amb = 'prueba' AND estado IN ('encolado', 'aceptado', 'observado')),
    'rechazados',    (SELECT count(*) FROM d WHERE estado = 'rechazado'),
    'porDia', (SELECT coalesce(json_agg(json_build_object('fecha', g.dia::date,
                         'reales', coalesce(x.r, 0), 'prueba', coalesce(x.p, 0)) ORDER BY g.dia), '[]'::json)
                 FROM generate_series({0}::timestamp, {1}::timestamp, interval '1 day') AS g(dia)
                 LEFT JOIN (SELECT fecha_emision,
                                   count(*) FILTER (WHERE amb = 'real') AS r,
                                   count(*) FILTER (WHERE amb = 'prueba') AS p
                              FROM d WHERE estado IN ('encolado', 'aceptado', 'observado') GROUP BY 1) x
                        ON x.fecha_emision = g.dia::date),
    'topEmpresas', (SELECT coalesce(json_agg(json_build_object('empresa', y.nombre, 'ruc', y.ruc, 'comprobantes', y.n) ORDER BY y.n DESC), '[]'::json)
                      FROM (SELECT coalesce(e.razon_social, e.nombre) AS nombre, e.ruc, count(*) AS n
                              FROM d JOIN empresas e ON e.id = d.empresa_id
                             WHERE d.estado IN ('encolado', 'aceptado', 'observado')
                             GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 10) y)
)::text AS "Value"
""";
    private const string SqlEmpresas = """
SELECT json_build_object('filas', coalesce(json_agg(json_build_object(
    'empresa', x.nombre, 'ruc', x.ruc, 'mes', to_char(x.mes, 'YYYY-MM'), 'ambiente', x.amb,
    'facturas', x.f, 'boletas', x.b, 'notas', x.n, 'rechazados', x.r, 'total', x.t)
  ORDER BY x.mes DESC, x.nombre), '[]'::json))::text AS "Value"
  FROM (SELECT coalesce(e.razon_social, e.nombre) AS nombre, e.ruc,
               date_trunc('month', d.fecha_emision)::date AS mes,
               CASE WHEN d.ambiente = 'produccion' THEN 'real' ELSE 'prueba' END AS amb,
               count(*) FILTER (WHERE d.tipo = '01' AND d.estado IN ('encolado', 'aceptado', 'observado')) AS f,
               count(*) FILTER (WHERE d.tipo = '03' AND d.estado IN ('encolado', 'aceptado', 'observado')) AS b,
               count(*) FILTER (WHERE d.tipo = '07' AND d.estado IN ('encolado', 'aceptado', 'observado')) AS n,
               count(*) FILTER (WHERE d.estado = 'rechazado') AS r,
               coalesce(sum(round(CASE WHEN d.tipo = '07' THEN -1 ELSE 1 END * d.importe_total *
                         CASE WHEN d.moneda = 'PEN' THEN 1 ELSE coalesce(d.tipo_cambio, 0) END, 2))
                        FILTER (WHERE d.estado IN ('encolado', 'aceptado', 'observado')), 0) AS t
          FROM documentos d JOIN empresas e ON e.id = d.empresa_id
         WHERE d.activo AND coalesce(e.ruc, '') <> '20999999990' AND d.tipo IN ('01', '03', '07', '08') AND d.serie IS NOT NULL
           AND d.fecha_emision BETWEEN {0} AND {1}
         GROUP BY 1, 2, 3, 4) x
""";
    private const string SqlRechazos = """
SELECT json_build_object('filas', coalesce(json_agg(json_build_object(
    'codigo', x.codigo, 'cantidad', x.n, 'empresas', x.e, 'mensaje', x.m, 'ultimo', x.u)
  ORDER BY x.n DESC), '[]'::json))::text AS "Value"
  FROM (SELECT coalesce(nullif(btrim(d.codigo_sunat), ''), 'sin código') AS codigo,
               count(*) AS n, count(DISTINCT d.empresa_id) AS e,
               max(d.mensaje_sunat) AS m, max(d.fecha_emision) AS u
          FROM documentos d JOIN empresas e ON e.id = d.empresa_id
         WHERE d.activo AND coalesce(e.ruc, '') <> '20999999990' AND d.estado = 'rechazado'
           AND d.fecha_emision BETWEEN {0} AND {1}
         GROUP BY 1) x
""";
    private const string SqlCuentas = """
SELECT json_build_object(
    'porDia', (SELECT coalesce(json_agg(json_build_object('fecha', g.dia::date,
                         'negocio', coalesce(x.ng, 0), 'profesional', coalesce(x.pr, 0)) ORDER BY g.dia), '[]'::json)
                 FROM generate_series({0}::timestamp, {1}::timestamp, interval '1 day') AS g(dia)
                 LEFT JOIN (SELECT (c.creado_en AT TIME ZONE 'America/Lima')::date AS f,
                                   count(*) FILTER (WHERE c.tipo = 'negocio') AS ng,
                                   count(*) FILTER (WHERE c.tipo = 'profesional') AS pr
                              FROM cuentas c GROUP BY 1) x ON x.f = g.dia::date),
    'filas', (SELECT coalesce(json_agg(json_build_object(
                  'fecha', (c.creado_en AT TIME ZONE 'America/Lima')::date, 'tipo', c.tipo,
                  'titular', u.nombre, 'correo', u.correo,
                  'empresas', (SELECT count(*) FROM empresas e WHERE e.cuenta_id = c.id AND e.activo))
                ORDER BY c.creado_en DESC), '[]'::json)
                FROM cuentas c
                LEFT JOIN usuarios u ON u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno'
               WHERE (c.creado_en AT TIME ZONE 'America/Lima')::date BETWEEN {0} AND {1})
)::text AS "Value"
""";

    // =========================================================================
    // e148 — AVANCE DE ACTIVACIÓN. Las cuentas que se registraron en el
    // periodo y hasta qué paso llegaron:
    //   1 Se registró · 2 Confirmó el correo · 3 Agregó su empresa ·
    //   4 Se conectó al servicio · 5 Emitió de prueba · 6 Pasó a SUNAT real ·
    //   7 Emite real
    // Con los días que lleva parada: a quién llamar.
    // =========================================================================
    private const string SqlActivacion = """
WITH c AS (
    SELECT c.id, c.tipo, c.creado_en,
           CASE WHEN c.confirmada_en IS NOT NULL OR c.estado = 'activa' THEN coalesce(c.confirmada_en, c.creado_en) END AS confirmo,
           coalesce(c.titular_nombre, u.nombre,
                    (SELECT coalesce(x.razon_social, x.nombre) FROM empresas x WHERE x.cuenta_id = c.id ORDER BY x.creado_en LIMIT 1)) AS titular,
           coalesce(u.correo, (SELECT x.correo_titular FROM empresas x WHERE x.cuenta_id = c.id ORDER BY x.creado_en LIMIT 1)) AS correo
      FROM cuentas c
      LEFT JOIN LATERAL (SELECT u.nombre, u.correo FROM usuarios u
                          WHERE u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno'
                          ORDER BY u.creado_en LIMIT 1) u ON true
     WHERE c.activo AND (c.creado_en AT TIME ZONE 'America/Lima')::date BETWEEN {0} AND {1}
), e AS (
    SELECT c.id AS cuenta,
           count(DISTINCT em.id) AS empresas,
           min(em.creado_en) AS empresa,
           min(cf.creado_en) AS conexion,
           min(dp.primero) AS prueba,
           min(em.modificado_en) FILTER (WHERE em.ambiente = 'produccion') AS real,
           bool_or(em.ambiente = 'produccion') AS en_real,
           min(dr.primero) AS emite_real
      FROM c
      LEFT JOIN empresas em ON em.cuenta_id = c.id AND em.activo AND coalesce(em.ruc, '') <> '20999999990'
      LEFT JOIN credenciales_facturacion cf ON cf.empresa_id = em.id AND cf.activo
      LEFT JOIN LATERAL (SELECT min(d.creado_en) AS primero FROM documentos d
                          WHERE d.empresa_id = em.id AND d.serie IS NOT NULL AND coalesce(d.ambiente, 'beta') <> 'produccion'
                            AND d.estado IN ('encolado', 'aceptado', 'observado', 'anulado')) dp ON true
      LEFT JOIN LATERAL (SELECT min(d.creado_en) AS primero FROM documentos d
                          WHERE d.empresa_id = em.id AND d.serie IS NOT NULL AND d.ambiente = 'produccion'
                            AND d.estado IN ('encolado', 'aceptado', 'observado', 'anulado')) dr ON true
     GROUP BY c.id
), p AS (
    SELECT c.*, e.empresas,
           CASE WHEN e.emite_real IS NOT NULL THEN 7
                WHEN e.en_real                THEN 6
                WHEN e.prueba IS NOT NULL     THEN 5
                WHEN e.conexion IS NOT NULL   THEN 4
                WHEN e.empresa IS NOT NULL    THEN 3
                WHEN c.confirmo IS NOT NULL   THEN 2
                ELSE 1 END AS paso,
           greatest(c.creado_en, c.confirmo, e.empresa, e.conexion, e.prueba, e.real, e.emite_real) AS ultimo
      FROM c JOIN e ON e.cuenta = c.id
)
SELECT json_build_object(
    'embudo', (SELECT json_agg(json_build_object('paso', n, 'cuentas', (SELECT count(*) FROM p WHERE p.paso >= n)) ORDER BY n)
                 FROM generate_series(1, 7) n),
    'filas', (SELECT coalesce(json_agg(json_build_object(
                  'fecha', (p.creado_en AT TIME ZONE 'America/Lima')::date, 'tipo', p.tipo,
                  'titular', p.titular, 'correo', p.correo, 'empresas', p.empresas, 'paso', p.paso,
                  'ultimo', (p.ultimo AT TIME ZONE 'America/Lima')::date,
                  'diasParada', CASE WHEN p.paso = 7 THEN NULL
                                     ELSE ((now() AT TIME ZONE 'America/Lima')::date - (p.ultimo AT TIME ZONE 'America/Lima')::date) END)
                ORDER BY p.paso, p.ultimo), '[]'::json) FROM p)
)::text AS "Value"
""";

    // =========================================================================
    // e148 — EMPRESAS EN RIESGO (hoy, sin periodo): las que pueden dejar de
    // facturar o ya dejaron. Solo salen las que tienen al menos un motivo.
    //   alto:  certificado vencido, en pausa, comprobantes sin respuesta o con
    //          error, 3+ rechazos en 7 días.
    //   medio: certificado vence en 30 días, algún rechazo, en SUNAT real y
    //          sin emitir hace 15 días.
    // =========================================================================
    private const string SqlRiesgo = """
WITH em AS (
    SELECT e.id, coalesce(e.razon_social, e.nombre) AS nombre, e.ruc, e.pausada_en, e.motivo_pausa,
           CASE WHEN e.ambiente = 'produccion' THEN 'real' ELSE 'prueba' END AS amb,
           coalesce(cu.titular_nombre, e.correo_titular) AS titular, e.correo_titular AS correo,
           (cf.certificado_valido_hasta AT TIME ZONE 'America/Lima')::date AS cert,
           ((cf.certificado_valido_hasta AT TIME ZONE 'America/Lima')::date - (now() AT TIME ZONE 'America/Lima')::date) AS dias_cert,
           (SELECT count(*) FROM documentos d WHERE d.empresa_id = e.id AND d.activo AND d.estado = 'rechazado'
               AND d.creado_en > now() - interval '7 days') AS rechazos,
           (SELECT count(*) FROM documentos d WHERE d.empresa_id = e.id AND d.activo
               AND (d.estado = 'error' AND d.creado_en > now() - interval '7 days'
                    OR (d.estado = 'encolado' AND d.fecha_emision <= (now() AT TIME ZONE 'America/Lima')::date
                        - CASE WHEN d.tipo = '03' OR (d.tipo IN ('07', '08') AND d.serie LIKE 'B%') THEN 5 ELSE 2 END))) AS sin_respuesta,
           (SELECT max(d.fecha_emision) FROM documentos d WHERE d.empresa_id = e.id AND d.activo AND d.serie IS NOT NULL
               AND coalesce(d.ambiente, 'beta') = CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END
               AND d.estado IN ('encolado', 'aceptado', 'observado')) AS ultimo
      FROM empresas e
      LEFT JOIN cuentas cu ON cu.id = e.cuenta_id
      LEFT JOIN credenciales_facturacion cf ON cf.empresa_id = e.id AND cf.activo
     WHERE e.activo AND coalesce(e.ruc, '') <> '20999999990'
), m AS (
    SELECT em.*,
           array_remove(ARRAY[
               CASE WHEN em.pausada_en IS NOT NULL THEN 'En pausa' || coalesce(': ' || em.motivo_pausa, '') END,
               CASE WHEN em.dias_cert < 0 THEN 'Certificado vencido hace ' || -em.dias_cert || ' día(s)' END,
               CASE WHEN em.sin_respuesta > 0 THEN em.sin_respuesta || ' comprobante(s) sin respuesta de SUNAT o con error' END,
               CASE WHEN em.rechazos >= 3 THEN em.rechazos || ' rechazos en 7 días' END
           ], NULL) AS altos,
           array_remove(ARRAY[
               CASE WHEN em.dias_cert BETWEEN 0 AND 30 THEN 'Certificado vence en ' || em.dias_cert || ' día(s)' END,
               CASE WHEN em.rechazos BETWEEN 1 AND 2 THEN em.rechazos || ' rechazo(s) en 7 días' END,
               CASE WHEN em.amb = 'real' AND em.pausada_en IS NULL
                         AND coalesce(em.ultimo, '1900-01-01') < (now() AT TIME ZONE 'America/Lima')::date - 15
                    THEN CASE WHEN em.ultimo IS NULL THEN 'En SUNAT real y todavía no emite'
                              ELSE 'Sin emitir desde el ' || to_char(em.ultimo, 'DD/MM/YYYY') END END
           ], NULL) AS medios
      FROM em
)
SELECT json_build_object('filas', coalesce(json_agg(json_build_object(
    'id', m.id, 'empresa', m.nombre, 'ruc', m.ruc, 'ambiente', m.amb, 'titular', m.titular, 'correo', m.correo,
    'nivel', CASE WHEN cardinality(m.altos) > 0 THEN 'alto' ELSE 'medio' END,
    'motivos', to_json(m.altos || m.medios), 'motivo', array_to_string(m.altos || m.medios, ' · '),
    'certificado', m.cert, 'ultimo', m.ultimo)
  ORDER BY cardinality(m.altos) DESC, cardinality(m.medios) DESC, m.nombre), '[]'::json))::text AS "Value"
  FROM m
 WHERE cardinality(m.altos) + cardinality(m.medios) > 0
""";

    // =========================================================================
    // e148 — CONSUMO POR CUENTA Y MES: lo que cada cuenta usa de la plataforma
    // (para el plan y el cobro). Comprobantes enviados a SUNAT (reales y de
    // prueba), notas, rechazados y WhatsApp enviados; empresas con movimiento.
    // =========================================================================
    private const string SqlConsumo = """
SELECT json_build_object('filas', coalesce(json_agg(json_build_object(
    'mes', to_char(x.mes, 'YYYY-MM'), 'tipo', x.tipo, 'titular', x.titular, 'correo', x.correo,
    'empresas', x.empresas, 'empresasCuenta', x.empresas_cuenta, 'reales', x.reales, 'prueba', x.prueba,
    'notas', x.notas, 'rechazados', x.rechazados, 'whatsapp', x.whatsapp, 'total', x.reales + x.prueba)
  ORDER BY x.mes DESC, x.reales DESC, x.titular), '[]'::json))::text AS "Value"
  FROM (SELECT date_trunc('month', d.fecha_emision)::date AS mes, cu.id AS cuenta, cu.tipo,
               coalesce(cu.titular_nombre,
                        (SELECT u.nombre FROM usuarios u WHERE u.cuenta_id = cu.id AND u.nivel_cuenta = 'dueno' ORDER BY u.creado_en LIMIT 1),
                        min(coalesce(e.razon_social, e.nombre))) AS titular,
               coalesce((SELECT u.correo FROM usuarios u WHERE u.cuenta_id = cu.id AND u.nivel_cuenta = 'dueno' ORDER BY u.creado_en LIMIT 1),
                        min(e.correo_titular)) AS correo,
               count(DISTINCT d.empresa_id) AS empresas,
               (SELECT count(*) FROM empresas e2 WHERE e2.cuenta_id = cu.id AND e2.activo) AS empresas_cuenta,
               count(*) FILTER (WHERE d.ambiente = 'produccion' AND d.estado IN ('encolado', 'aceptado', 'observado', 'anulado')) AS reales,
               count(*) FILTER (WHERE coalesce(d.ambiente, 'beta') <> 'produccion' AND d.estado IN ('encolado', 'aceptado', 'observado', 'anulado')) AS prueba,
               count(*) FILTER (WHERE d.tipo IN ('07', '08') AND d.estado IN ('encolado', 'aceptado', 'observado', 'anulado')) AS notas,
               count(*) FILTER (WHERE d.estado = 'rechazado') AS rechazados,
               count(*) FILTER (WHERE d.whatsapp_estado = 'enviado') AS whatsapp
          FROM documentos d
          JOIN empresas e ON e.id = d.empresa_id
          JOIN cuentas cu ON cu.id = e.cuenta_id
         WHERE d.activo AND coalesce(e.ruc, '') <> '20999999990'
           AND d.tipo IN ('01', '03', '07', '08') AND d.serie IS NOT NULL
           AND d.fecha_emision BETWEEN {0} AND {1}
         GROUP BY 1, 2, 3) x
""";

    public static void MapearReportesPlataforma(this IEndpointRouteBuilder rutas)
    {
        rutas.MapGet("/api/reportes/cartera", async (string? desde, string? hasta, string? formato,
                string? q, string? sunat, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (Periodo(desde, hasta, out var d, out var h) is { } problema)
                return Results.BadRequest(new { error = problema });

            var json = (await db.Database.SqlQuery<string>(
                $"SELECT reporte_cartera({d}, {h}) AS \"Value\"").ToListAsync(ct)).FirstOrDefault();
            return Responder("cartera", json, d, h, formato, q, null, sunat);
        })
        .RequiereSesion()
        .WithTags("Reportes")
        .WithSummary("Mis empresas: lo emitido en el periodo y el certificado de cada una");

        var admin = rutas.MapGroup("/api/admin/reportes").WithTags("Reportes · plataforma");
        foreach (var (nombre, sql) in new[] { ("plataforma", SqlPlataforma), ("empresas", SqlEmpresas),
                                               ("rechazos", SqlRechazos), ("cuentas", SqlCuentas),
                                               ("activacion", SqlActivacion), ("riesgo", SqlRiesgo), ("consumo", SqlConsumo) })
        {
            admin.MapGet("/" + nombre, async (string? desde, string? hasta, string? formato,
                    string? q, string? tipo, string? sunat, ContextoPlataforma db, CancellationToken ct) =>
            {
                if (Periodo(desde, hasta, out var d, out var h) is { } problema)
                    return Results.BadRequest(new { error = problema });

                var json = (await db.Database.SqlQuery<string>(
                    FormattableStringFactory.Create(sql, d, h)).ToListAsync(ct)).FirstOrDefault();
                return Responder(nombre, json, d, h, formato, q, tipo, sunat);
            })
            .SoloSuperAdmin(global::Plataforma.Core.Dominio.Permisos.Plataforma.ReportesVer)
            .WithSummary("Reporte de la plataforma: " + nombre);
        }
    }


    private static IResult Responder(string reporte, string? json, DateOnly d, DateOnly h, string? formato,
                                     string? q, string? tipo, string? sunat)
    {
        var datos = JsonNode.Parse(json ?? "{}")!.AsObject();
        datos["desde"] = d.ToString("yyyy-MM-dd");
        datos["hasta"] = h.ToString("yyyy-MM-dd");

        // 037: búsqueda y filtros, aquí: la pantalla y el Excel muestran lo mismo.
        FiltroReportes.Aplicar(datos, q, tipo, null, sunat);

        if (!string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase))
            return Results.Content(datos.ToJsonString(), "application/json");

        var hoja = new HojaExcel(Titulo(reporte));
        hoja.Titulos.Add(Titulo(reporte));
        hoja.Titulos.Add(reporte == "cartera" ? "Mi cartera" : "Reyban · Plataforma");
        hoja.Titulos.Add(reporte == "cartera" && d == h ? $"Al {d:dd/MM/yyyy}" : $"Del {d:dd/MM/yyyy} al {h:dd/MM/yyyy} · montos en soles");
        var filtros = new[]
        {
            string.IsNullOrWhiteSpace(q) ? null : $"búsqueda «{q.Trim()}»",
            sunat switch { "real" => "solo SUNAT real", "prueba" => "solo prueba", _ => null },
            tipo switch { "negocio" => "solo Cuenta Negocio", "profesional" => "solo Cuenta Profesional", _ => null }
        }.OfType<string>().ToList();
        if (filtros.Count > 0) hoja.Titulos.Add("Filtro: " + string.Join(" · ", filtros));

        var filas = (datos["filas"] as JsonArray ?? []).OfType<JsonObject>().ToList();

        switch (reporte)
        {
            case "cartera":
                hoja.FilaTotal = true;
                hoja.Horizontal = true;
                hoja.Columnas.AddRange([new("Empresa", TipoColumna.Texto, 36), new("RUC", TipoColumna.Texto, 13),
                    new("SUNAT", TipoColumna.Texto, 8), new("Comprobantes", TipoColumna.Entero, 13),
                    new("Notas de crédito", TipoColumna.Entero, 14), new("Vendido (S/)", TipoColumna.Monto, 15),
                    new("Rechazados", TipoColumna.Entero, 11), new("Sin respuesta", TipoColumna.Entero, 12),
                    new("Certificado vence", TipoColumna.Fecha, 13), new("En pausa", TipoColumna.Texto, 9)]);
                foreach (var f in filas)
                    hoja.Filas.Add([Txt(f["nombre"]), Txt(f["ruc"]), Txt(f["ambiente"]) == "produccion" ? "Real" : "Prueba",
                        Ent(f["comprobantes"]), Ent(f["notas"]), Dec(f["total"]), Ent(f["rechazados"]), Ent(f["pendientes"]),
                        Txt(f["certificadoHasta"]), (bool?)f["pausada"] == true ? "Sí" : "No"]);
                break;

            case "plataforma":
            {
                var r = new HojaExcel("Resumen") { ConFiltro = false };
                r.Titulos.AddRange(hoja.Titulos);
                r.Columnas.AddRange([new("Concepto", TipoColumna.Texto, 34), new("Valor", TipoColumna.Texto, 14)]);
                r.Filas.Add([new FilaSeccion("La plataforma")]);
                r.Filas.Add(["Empresas", new CeldaExcel(Ent(datos["empresas"]), TipoColumna.Entero)]);
                r.Filas.Add(["Empresas en SUNAT real", new CeldaExcel(Ent(datos["empresasReal"]), TipoColumna.Entero)]);
                r.Filas.Add(["Cuentas", new CeldaExcel(Ent(datos["cuentas"]), TipoColumna.Entero)]);
                r.Filas.Add(["Cuentas nuevas en el periodo", new CeldaExcel(Ent(datos["cuentasNuevas"]), TipoColumna.Entero)]);
                r.Filas.Add([new FilaSeccion("Comprobantes del periodo")]);
                r.Filas.Add(["Reales", new CeldaExcel(Ent(datos["reales"]), TipoColumna.Entero)]);
                r.Filas.Add(["De prueba", new CeldaExcel(Ent(datos["prueba"]), TipoColumna.Entero)]);
                r.Filas.Add(["Rechazados", new CeldaExcel(Ent(datos["rechazados"]), TipoColumna.Entero)]);

                hoja.FilaTotal = true;
                hoja.Columnas.AddRange([new("Fecha", TipoColumna.Fecha, 12), new("Comprobantes reales", TipoColumna.Entero, 18),
                    new("Comprobantes de prueba", TipoColumna.Entero, 20)]);
                foreach (var f in (datos["porDia"] as JsonArray ?? []).OfType<JsonObject>())
                    hoja.Filas.Add([Txt(f["fecha"]), Ent(f["reales"]), Ent(f["prueba"])]);

                var top = new HojaExcel("Empresas que más emiten");
                top.Titulos.AddRange(hoja.Titulos);
                top.Columnas.AddRange([new("Empresa", TipoColumna.Texto, 36), new("RUC", TipoColumna.Texto, 13),
                    new("Comprobantes", TipoColumna.Entero, 13)]);
                foreach (var f in (datos["topEmpresas"] as JsonArray ?? []).OfType<JsonObject>())
                    top.Filas.Add([Txt(f["empresa"]), Txt(f["ruc"]), Ent(f["comprobantes"])]);

                return Archivo([r, hoja, top], reporte, d, h);
            }

            case "empresas":
                hoja.FilaTotal = true;
                hoja.Horizontal = true;
                hoja.Columnas.AddRange([new("Mes", TipoColumna.Texto, 9), new("Empresa", TipoColumna.Texto, 36),
                    new("RUC", TipoColumna.Texto, 13), new("SUNAT", TipoColumna.Texto, 8),
                    new("Facturas", TipoColumna.Entero, 10), new("Boletas", TipoColumna.Entero, 10),
                    new("Notas", TipoColumna.Entero, 8), new("Rechazados", TipoColumna.Entero, 11),
                    new("Total (S/)", TipoColumna.Monto, 15)]);
                foreach (var f in filas)
                    hoja.Filas.Add([Txt(f["mes"]), Txt(f["empresa"]), Txt(f["ruc"]), Txt(f["ambiente"]) == "real" ? "Real" : "Prueba",
                        Ent(f["facturas"]), Ent(f["boletas"]), Ent(f["notas"]), Ent(f["rechazados"]), Dec(f["total"])]);
                break;

            case "rechazos":
                hoja.FilaTotal = true;
                hoja.Horizontal = true;
                hoja.Columnas.AddRange([new("Código SUNAT", TipoColumna.Texto, 13), new("Rechazos", TipoColumna.Entero, 10),
                    new("Empresas", TipoColumna.Entero, 10), new("Último", TipoColumna.Fecha, 12),
                    new("Mensaje", TipoColumna.Texto, 70)]);
                foreach (var f in filas)
                    hoja.Filas.Add([Txt(f["codigo"]), Ent(f["cantidad"]), Ent(f["empresas"]), Txt(f["ultimo"]), Txt(f["mensaje"])]);
                break;

            case "activacion":
            {
                var r = new HojaExcel("Embudo") { ConFiltro = false };
                r.Titulos.AddRange(hoja.Titulos);
                r.Columnas.AddRange([new("Paso", TipoColumna.Texto, 32), new("Cuentas que llegaron", TipoColumna.Entero, 20)]);
                foreach (var f in (datos["embudo"] as JsonArray ?? []).OfType<JsonObject>())
                    r.Filas.Add([$"{Ent(f["paso"])}. {Paso(Ent(f["paso"]))}", Ent(f["cuentas"])]);

                hoja.Horizontal = true;
                hoja.Columnas.AddRange([new("Registro", TipoColumna.Fecha, 12), new("Tipo", TipoColumna.Texto, 12),
                    new("Titular", TipoColumna.Texto, 30), new("Correo", TipoColumna.Texto, 30),
                    new("Empresas", TipoColumna.Entero, 10), new("Llegó hasta", TipoColumna.Texto, 26),
                    new("Último avance", TipoColumna.Fecha, 13), new("Días parada", TipoColumna.Entero, 11)]);
                foreach (var f in filas)
                    hoja.Filas.Add([Txt(f["fecha"]), Txt(f["tipo"]) == "profesional" ? "Profesional" : "Negocio",
                        Txt(f["titular"]), Txt(f["correo"]), Ent(f["empresas"]), $"{Ent(f["paso"])}. {Paso(Ent(f["paso"]))}",
                        Txt(f["ultimo"]), f["diasParada"] is null ? null : Ent(f["diasParada"])]);
                return Archivo([r, hoja], reporte, d, h);
            }

            case "riesgo":
                hoja.Titulos[2] = $"Al {HoyEnPeru():dd/MM/yyyy}";
                hoja.Horizontal = true;
                hoja.Columnas.AddRange([new("Nivel", TipoColumna.Texto, 8), new("Empresa", TipoColumna.Texto, 34),
                    new("RUC", TipoColumna.Texto, 13), new("SUNAT", TipoColumna.Texto, 8),
                    new("Motivos", TipoColumna.Texto, 60), new("Titular", TipoColumna.Texto, 26),
                    new("Correo", TipoColumna.Texto, 28), new("Certificado vence", TipoColumna.Fecha, 13),
                    new("Último comprobante", TipoColumna.Fecha, 13)]);
                foreach (var f in filas)
                    hoja.Filas.Add([Txt(f["nivel"]) == "alto" ? "Alto" : "Medio", Txt(f["empresa"]), Txt(f["ruc"]),
                        Txt(f["ambiente"]) == "real" ? "Real" : "Prueba", Txt(f["motivo"]), Txt(f["titular"]),
                        Txt(f["correo"]), Txt(f["certificado"]), Txt(f["ultimo"])]);
                break;

            case "consumo":
                hoja.FilaTotal = true;
                hoja.Horizontal = true;
                hoja.Columnas.AddRange([new("Mes", TipoColumna.Texto, 9), new("Cuenta", TipoColumna.Texto, 32),
                    new("Tipo", TipoColumna.Texto, 12), new("Empresas con movimiento", TipoColumna.Entero, 12),
                    new("Empresas de la cuenta", TipoColumna.Entero, 12), new("Reales", TipoColumna.Entero, 10),
                    new("De prueba", TipoColumna.Entero, 10), new("Notas", TipoColumna.Entero, 8),
                    new("Rechazados", TipoColumna.Entero, 11), new("WhatsApp", TipoColumna.Entero, 10)]);
                foreach (var f in filas)
                    hoja.Filas.Add([Txt(f["mes"]), Txt(f["titular"]), Txt(f["tipo"]) == "profesional" ? "Profesional" : "Negocio",
                        Ent(f["empresas"]), Ent(f["empresasCuenta"]), Ent(f["reales"]), Ent(f["prueba"]),
                        Ent(f["notas"]), Ent(f["rechazados"]), Ent(f["whatsapp"])]);
                break;

            default: // cuentas
                hoja.FilaTotal = true;
                hoja.Columnas.AddRange([new("Fecha", TipoColumna.Fecha, 12), new("Tipo", TipoColumna.Texto, 12),
                    new("Titular", TipoColumna.Texto, 30), new("Correo", TipoColumna.Texto, 32),
                    new("Empresas", TipoColumna.Entero, 10)]);
                foreach (var f in filas)
                    hoja.Filas.Add([Txt(f["fecha"]), Txt(f["tipo"]) == "profesional" ? "Profesional" : "Negocio",
                        Txt(f["titular"]), Txt(f["correo"]), Ent(f["empresas"])]);
                break;
        }

        return Archivo([hoja], reporte, d, h);
    }

    private static IResult Archivo(List<HojaExcel> hojas, string reporte, DateOnly d, DateOnly h) =>
        Results.File(ExcelSimple.Crear(hojas), ExcelSimple.TipoMime,
                     $"reporte-{reporte}-{d:yyyy-MM-dd}-a-{h:yyyy-MM-dd}.xlsx");

    private static string Titulo(string reporte) => reporte switch
    {
        "cartera"    => "Mis empresas",
        "plataforma" => "Tablero de la plataforma",
        "empresas"   => "Comprobantes por empresa y mes",
        "rechazos"   => "Rechazos SUNAT por código",
        "activacion" => "Avance de activación",
        "riesgo"     => "Empresas en riesgo",
        "consumo"    => "Consumo por cuenta y mes",
        _            => "Cuentas nuevas"
    };

    /// <summary>e148: los pasos de la activación (los mismos que la pantalla).</summary>
    private static string Paso(int n) => n switch
    {
        1 => "Se registró", 2 => "Confirmó el correo", 3 => "Agregó su empresa", 4 => "Se conectó al servicio",
        5 => "Emitió de prueba", 6 => "Pasó a SUNAT real", 7 => "Emite real", _ => "—"
    };

    private static string? Periodo(string? desde, string? hasta, out DateOnly d, out DateOnly h)
    {
        var hoy = HoyEnPeru();
        d = new DateOnly(hoy.Year, hoy.Month, 1); h = hoy;

        static bool Leer(string? t, out DateOnly f) =>
            DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out f);

        if (!string.IsNullOrWhiteSpace(desde) && !Leer(desde, out d)) return "La fecha «desde» no es válida.";
        if (!string.IsNullOrWhiteSpace(hasta) && !Leer(hasta, out h)) return "La fecha «hasta» no es válida.";
        if (d > h) return "La fecha «desde» es posterior a «hasta».";
        if (h.DayNumber - d.DayNumber > 366) return "Elige un periodo de un año como máximo.";
        return null;
    }

    private static string? Fecha(JsonNode? n) =>
        Txt(n) is { Length: >= 10 } s ? $"{s.Substring(8, 2)}/{s.Substring(5, 2)}/{s[..4]}" : null;
    private static decimal Dec(JsonNode? n) => n is JsonValue v && v.TryGetValue<decimal>(out var x) ? x : 0m;
    private static int Ent(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    private static string? Txt(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    private static DateOnly HoyEnPeru()
    {
        try
        {
            var lima = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, lima));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        }
    }
}