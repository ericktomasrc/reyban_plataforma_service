using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Reportes;

/// <summary>
/// 034 — REPORTES, entrega 2: «SUNAT y contabilidad».
///
///   GET /api/reportes/igv            Resumen de IGV por mes (en soles)
///   GET /api/reportes/registro       Registro de Ventas (para el contador)
///   GET /api/reportes/problemas      Comprobantes con problema
///   GET /api/reportes/anulados       Anulados y notas de crédito
///   GET /api/reportes/descarga       Descarga masiva .zip (XML, CDR, PDF)
///
/// ?desde=AAAA-MM-DD&amp;hasta=AAAA-MM-DD; &amp;formato=xlsx para Excel.
/// Solo lectura. Montos EN SOLES: lo emitido en dólares se pasa con el tipo de
/// cambio de cada comprobante. Las notas de crédito restan; los anulados salen
/// en 0 en el Registro; los rechazados no existen para SUNAT y no van.
/// </summary>
public static class EndpointsReportesSunat
{
    private const int MaximoDescarga = 200;

    private const string SqlIgv = """
WITH amb AS (
    SELECT CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS a
      FROM empresas e WHERE e.id = ctx_empresa()
), s AS (
    SELECT d.*,
           CASE WHEN d.tipo = '07' THEN -1 ELSE 1 END AS signo,
           CASE WHEN d.moneda = 'PEN' THEN 1 ELSE coalesce(d.tipo_cambio, 0) END AS tc
      FROM documentos d CROSS JOIN amb
     WHERE d.empresa_id = ctx_empresa() AND d.activo
       AND coalesce(d.ambiente, 'beta') = amb.a
       AND d.tipo IN ('01', '03', '07', '08')
       AND d.serie IS NOT NULL AND d.correlativo IS NOT NULL
       AND d.fecha_emision BETWEEN {0} AND {1}
),
v AS (SELECT * FROM s WHERE estado IN ('encolado', 'aceptado', 'observado'))
SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'sinTipoCambio', (SELECT count(*) FROM v WHERE moneda <> 'PEN' AND tipo_cambio IS NULL),
    'filas', (SELECT coalesce(json_agg(json_build_object(
                  'mes', to_char(x.mes, 'YYYY-MM'), 'comprobantes', x.n, 'notas', x.notas,
                  'gravado', x.gravado, 'exonerado', x.exonerado, 'inafecto', x.inafecto,
                  'exportacion', x.exportacion, 'igv', x.igv, 'total', x.total) ORDER BY x.mes), '[]'::json)
                FROM (SELECT date_trunc('month', fecha_emision)::date AS mes,
                             count(*) FILTER (WHERE tipo NOT IN ('07', '08')) AS n,
                             count(*) FILTER (WHERE tipo IN ('07', '08'))  AS notas,
                             sum(round(signo * total_gravado     * tc, 2)) AS gravado,
                             sum(round(signo * total_exonerado   * tc, 2)) AS exonerado,
                             sum(round(signo * total_inafecto    * tc, 2)) AS inafecto,
                             sum(round(signo * total_exportacion * tc, 2)) AS exportacion,
                             sum(round(signo * total_igv         * tc, 2)) AS igv,
                             sum(round(signo * importe_total     * tc, 2)) AS total
                        FROM v GROUP BY 1) x),
    'porTipo', (SELECT coalesce(json_agg(json_build_object('tipo', tipo, 'cantidad', n, 'base', b, 'igv', i, 'total', t) ORDER BY tipo), '[]'::json)
                  FROM (SELECT tipo, count(*) AS n,
                               sum(round(signo * (total_gravado + total_exonerado + total_inafecto + total_exportacion) * tc, 2)) AS b,
                               sum(round(signo * total_igv * tc, 2)) AS i,
                               sum(round(signo * importe_total * tc, 2)) AS t
                          FROM v GROUP BY tipo) y)
)::text AS "Value"
""";
    private const string SqlRegistro = """
WITH amb AS (
    SELECT CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS a
      FROM empresas e WHERE e.id = ctx_empresa()
), s AS (
    SELECT d.*,
           CASE WHEN d.tipo = '07' THEN -1 ELSE 1 END AS signo,
           CASE WHEN d.moneda = 'PEN' THEN 1 ELSE coalesce(d.tipo_cambio, 0) END AS tc
      FROM documentos d CROSS JOIN amb
     WHERE d.empresa_id = ctx_empresa() AND d.activo
       AND coalesce(d.ambiente, 'beta') = amb.a
       AND d.tipo IN ('01', '03', '07', '08')
       AND d.serie IS NOT NULL AND d.correlativo IS NOT NULL
       AND d.fecha_emision BETWEEN {0} AND {1}
)
SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'filas', coalesce(json_agg(json_build_object(
        'id', s.id,
        'fecha', s.fecha_emision, 'vence', s.fecha_vencimiento, 'tipo', s.tipo,
        'serie', s.serie, 'numero', s.correlativo,
        'tipoDoc', c.tipo_documento, 'documento', c.numero_documento, 'cliente', c.razon_social,
        'exportacion', CASE WHEN s.estado = 'anulado' THEN 0 ELSE round(s.signo * s.total_exportacion * s.tc, 2) END,
        'gravado',     CASE WHEN s.estado = 'anulado' THEN 0 ELSE round(s.signo * s.total_gravado * s.tc, 2) END,
        'exonerado',   CASE WHEN s.estado = 'anulado' THEN 0 ELSE round(s.signo * s.total_exonerado * s.tc, 2) END,
        'inafecto',    CASE WHEN s.estado = 'anulado' THEN 0 ELSE round(s.signo * s.total_inafecto * s.tc, 2) END,
        'igv',         CASE WHEN s.estado = 'anulado' THEN 0 ELSE round(s.signo * s.total_igv * s.tc, 2) END,
        'total',       CASE WHEN s.estado = 'anulado' THEN 0 ELSE round(s.signo * s.importe_total * s.tc, 2) END,
        'moneda', s.moneda, 'tipoCambio', s.tipo_cambio,
        'refFecha', r.fecha_emision, 'refTipo', r.tipo, 'refSerie', r.serie, 'refNumero', r.correlativo,
        'estado', s.estado)
      ORDER BY s.fecha_emision, s.tipo, s.serie, s.correlativo), '[]'::json)
)::text AS "Value"
  FROM s
  JOIN clientes c ON c.id = s.cliente_id
  LEFT JOIN documentos r ON r.id = s.documento_ref_id
 WHERE s.estado IN ('encolado', 'aceptado', 'observado', 'anulado')
""";
    private const string SqlProblemas = """
WITH amb AS (
    SELECT CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS a
      FROM empresas e WHERE e.id = ctx_empresa()
)
SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'filas', coalesce(json_agg(json_build_object(
        'id', d.id, 'fecha', d.fecha_emision, 'tipo', d.tipo,
        'numero', CASE WHEN d.serie IS NULL THEN NULL ELSE d.serie || '-' || lpad(d.correlativo::text, 8, '0') END,
        'cliente', c.razon_social, 'total', d.importe_total, 'moneda', d.moneda,
        'estado', d.estado, 'codigo', d.codigo_sunat, 'mensaje', d.mensaje_sunat,
        'observaciones', d.observaciones)
      ORDER BY d.fecha_emision DESC, d.creado_en DESC), '[]'::json)
)::text AS "Value"
  FROM documentos d CROSS JOIN amb
  JOIN clientes c ON c.id = d.cliente_id
 WHERE d.empresa_id = ctx_empresa() AND d.activo
   AND coalesce(d.ambiente, 'beta') = amb.a
   AND d.fecha_emision BETWEEN {0} AND {1}
   AND (d.estado IN ('rechazado', 'error', 'observado')
        OR (d.estado = 'encolado' AND d.fecha_emision <=
              (now() AT TIME ZONE 'America/Lima')::date
              - CASE WHEN d.tipo = '03' OR (d.tipo IN ('07', '08') AND d.serie LIKE 'B%') THEN 5 ELSE 2 END))
""";
    private const string SqlAnulados = """
WITH amb AS (
    SELECT CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS a
      FROM empresas e WHERE e.id = ctx_empresa()
), s AS (
    SELECT d.*,
           CASE WHEN d.tipo = '07' THEN -1 ELSE 1 END AS signo,
           CASE WHEN d.moneda = 'PEN' THEN 1 ELSE coalesce(d.tipo_cambio, 0) END AS tc
      FROM documentos d CROSS JOIN amb
     WHERE d.empresa_id = ctx_empresa() AND d.activo
       AND coalesce(d.ambiente, 'beta') = amb.a
       AND d.tipo IN ('01', '03', '07', '08')
       AND d.serie IS NOT NULL AND d.correlativo IS NOT NULL
       AND d.fecha_emision BETWEEN {0} AND {1}
)
SELECT json_build_object(
    'ambiente', (SELECT a FROM amb),
    'filas', coalesce(json_agg(json_build_object(
        'id', s.id, 'fecha', s.fecha_emision, 'tipo', s.tipo,
        'numero', s.serie || '-' || lpad(s.correlativo::text, 8, '0'),
        'cliente', c.razon_social, 'total', s.importe_total, 'moneda', s.moneda,
        'estado', s.estado, 'tipoNota', s.tipo_nota, 'motivo', s.motivo_nota,
        'afecta', CASE WHEN r.id IS NULL THEN NULL ELSE r.serie || '-' || lpad(r.correlativo::text, 8, '0') END,
        'afectaId', r.id)
      ORDER BY s.fecha_emision DESC, s.creado_en DESC), '[]'::json)
)::text AS "Value"
  FROM s
  JOIN clientes c ON c.id = s.cliente_id
  LEFT JOIN documentos r ON r.id = s.documento_ref_id
 WHERE s.estado = 'anulado' OR (s.tipo IN ('07', '08') AND s.estado <> 'borrador')
""";
    private const string SqlDescarga = """
WITH amb AS (
    SELECT CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS a
      FROM empresas e WHERE e.id = ctx_empresa()
)
SELECT json_build_object(
    'total', count(*),
    'filas', coalesce(json_agg(json_build_object(
        'comprobanteId', d.comprobante_id, 'tipo', d.tipo, 'estado', d.estado,
        'numero', d.serie || '-' || lpad(d.correlativo::text, 8, '0'))
      ORDER BY d.fecha_emision, d.serie, d.correlativo), '[]'::json)
)::text AS "Value"
  FROM documentos d CROSS JOIN amb
 WHERE d.empresa_id = ctx_empresa() AND d.activo
   AND coalesce(d.ambiente, 'beta') = amb.a
   AND d.tipo IN ('01', '03', '07', '08')
   AND d.comprobante_id IS NOT NULL AND d.serie IS NOT NULL AND d.correlativo IS NOT NULL
   AND d.estado IN ('encolado', 'aceptado', 'observado', 'anulado', 'rechazado')
   AND d.fecha_emision BETWEEN {0} AND {1}
""";

    private sealed record Periodo(DateOnly Desde, DateOnly Hasta)
    {
        public string Texto => $"Del {Desde:dd/MM/yyyy} al {Hasta:dd/MM/yyyy} · montos en soles";
    }

    public static void MapearReportesSunat(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/reportes").WithTags("Reportes");

        grupo.MapGet("/igv", (string? desde, string? hasta, string? formato, string? q, string? tipo, string? estado,
                ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            Responder("igv", SqlIgv, desde, hasta, formato, db, ctx, ct, q, tipo, estado))
            .RequierePermiso(Permisos.Reportes.ResumenIgv).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Resumen de IGV por mes");

        grupo.MapGet("/registro", (string? desde, string? hasta, string? formato, string? q, string? tipo, string? estado,
                ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            Responder("registro", SqlRegistro, desde, hasta, formato, db, ctx, ct, q, tipo, estado))
            .RequierePermiso(Permisos.Reportes.RegistroVentas).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Registro de Ventas");

        grupo.MapGet("/problemas", (string? desde, string? hasta, string? formato, string? q, string? tipo, string? estado,
                ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            Responder("problemas", SqlProblemas, desde, hasta, formato, db, ctx, ct, q, tipo, estado))
            .RequierePermiso(Permisos.Reportes.ConProblema).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Comprobantes con problema");

        grupo.MapGet("/anulados", (string? desde, string? hasta, string? formato, string? q, string? tipo, string? estado,
                ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            Responder("anulados", SqlAnulados, desde, hasta, formato, db, ctx, ct, q, tipo, estado))
            .RequierePermiso(Permisos.Reportes.AnuladosNotas).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Anulados y notas de crédito");

        grupo.MapGet("/descarga", Descargar)
            .RequierePermiso(Permisos.Reportes.DescargaMasiva).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Descarga masiva: XML, CDR y PDF del periodo en un .zip");
    }


    private static async Task<IResult> Responder(
        string reporte, string sql, string? desde, string? hasta, string? formato,
        ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct,
        string? q = null, string? tipo = null, string? estado = null)
    {
        if (ctx.EmpresaId is null)
            return Results.BadRequest(new { error = "Abre una empresa para ver sus reportes." });
        if (LeerPeriodo(desde, hasta, out var p) is { } problema)
            return Results.BadRequest(new { error = problema });

        var json = (await db.Database.SqlQuery<string>(Formato(sql, p)).ToListAsync(ct)).FirstOrDefault();
        var datos = JsonNode.Parse(json ?? "{}")!.AsObject();
        datos["desde"] = p.Desde.ToString("yyyy-MM-dd");
        datos["hasta"] = p.Hasta.ToString("yyyy-MM-dd");

        // 037: búsqueda y filtros, aquí: la pantalla y el Excel muestran lo mismo.
        FiltroReportes.Aplicar(datos, q, tipo, estado, null);

        if (!string.Equals(formato, "xlsx", StringComparison.OrdinalIgnoreCase))
            return Results.Content(datos.ToJsonString(), "application/json");

        var titulos = await TitulosAsync(Nombre(reporte), p, (string?)datos["ambiente"], db, ctx, ct);
        var filtros = new[]
        {
            string.IsNullOrWhiteSpace(q) ? null : $"búsqueda «{q.Trim()}»",
            tipo switch { "01" => "solo facturas", "03" => "solo boletas", "07" => "solo notas de crédito", "08" => "solo notas de débito", _ => null },
            string.IsNullOrWhiteSpace(estado) ? null : "estado: " + Estado(estado).ToLowerInvariant()
        }.OfType<string>().ToList();
        if (filtros.Count > 0) titulos.Insert(3, "Filtro: " + string.Join(" · ", filtros));
        var hoja = Hoja(reporte, datos, titulos, p);
        return Results.File(ExcelSimple.Crear([hoja]), ExcelSimple.TipoMime,
                            $"{Archivo(reporte)}-{p.Desde:yyyy-MM-dd}-a-{p.Hasta:yyyy-MM-dd}.xlsx");
    }


    // =========================================================================
    // DESCARGA MASIVA: los archivos que guarda el servicio, en un .zip.
    // =========================================================================
    private static async Task<IResult> Descargar(
        string? desde, string? hasta, bool? xml, bool? cdr, bool? pdf,
        ContextoPlataforma db, ContextoPeticion ctx, ClienteFacturacion facturacion, CancellationToken ct)
    {
        if (ctx.EmpresaId is not { } empresaId)
            return Results.BadRequest(new { error = "Abre una empresa para descargar sus comprobantes." });
        if (LeerPeriodo(desde, hasta, out var p) is { } problema)
            return Results.BadRequest(new { error = problema });

        var conXml = xml ?? true; var conCdr = cdr ?? true; var conPdf = pdf ?? false;
        if (!conXml && !conCdr && !conPdf)
            return Results.BadRequest(new { error = "Elige al menos un tipo de archivo (XML, CDR o PDF)." });

        var json = (await db.Database.SqlQuery<string>(Formato(SqlDescarga, p)).ToListAsync(ct)).FirstOrDefault();
        var datos = JsonNode.Parse(json ?? "{}")!.AsObject();
        var total = (int?)datos["total"] ?? 0;

        if (total == 0)
            return Results.BadRequest(new { error = "No hay comprobantes emitidos en ese periodo." });
        if (total > MaximoDescarga)
            return Results.BadRequest(new
            {
                error = $"Son {total} comprobantes. Se bajan hasta {MaximoDescarga} por vez: elige un periodo más corto."
            });

        var faltan = new List<string>();
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var fila in (datos["filas"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (!Guid.TryParse((string?)fila["comprobanteId"], out var comprobanteId)) continue;
                var numero = (string?)fila["numero"] ?? comprobanteId.ToString();
                var estado = (string?)fila["estado"];

                var pedidos = new List<(ArchivoComprobante Tipo, string Carpeta, string Extension)>();
                if (conXml) pedidos.Add((ArchivoComprobante.Xml, "XML", ".xml"));
                // El CDR existe cuando SUNAT ya contestó (las boletas, en su resumen).
                if (conCdr && estado != EstadoDocumento.Encolado) pedidos.Add((ArchivoComprobante.Cdr, "CDR", ".zip"));
                if (conPdf) pedidos.Add((ArchivoComprobante.Pdf, "PDF", ".pdf"));

                foreach (var (tipo, carpeta, extension) in pedidos)
                {
                    var r = await facturacion.DescargarComprobanteAsync(empresaId, comprobanteId, tipo, ct);
                    if (!r.Exito || r.Contenido is null)
                    {
                        faltan.Add($"{numero} · {carpeta}: {r.Error ?? "no disponible"}");
                        continue;
                    }
                    var nombre = string.IsNullOrWhiteSpace(r.NombreArchivo) ? numero + extension : Path.GetFileName(r.NombreArchivo);
                    var entrada = zip.CreateEntry($"{carpeta}/{nombre}", CompressionLevel.Optimal);
                    await using var flujo = entrada.Open();
                    await flujo.WriteAsync(r.Contenido, ct);
                }
            }

            if (faltan.Count > 0)
            {
                var texto = "Archivos que no se pudieron bajar (vuelve a intentarlo más tarde, o bájalos desde cada comprobante):\r\n\r\n" +
                            string.Join("\r\n", faltan);
                var entrada = zip.CreateEntry("LEEME-faltantes.txt");
                await using var flujo = entrada.Open();
                await flujo.WriteAsync(new UTF8Encoding(true).GetBytes(texto), ct);
            }
        }

        return Results.File(memoria.ToArray(), "application/zip",
                            $"comprobantes-{p.Desde:yyyy-MM-dd}-a-{p.Hasta:yyyy-MM-dd}.zip");
    }


    // =========================================================================
    // EL EXCEL DE CADA REPORTE
    // =========================================================================
    private static HojaExcel Hoja(string reporte, JsonObject d, List<string> titulos, Periodo p)
    {
        var h = new HojaExcel(Nombre(reporte));
        h.Titulos.AddRange(titulos);
        var filas = (d["filas"] as JsonArray ?? []).OfType<JsonObject>().ToList();

        switch (reporte)
        {
            case "igv":
                h.FilaTotal = true;
                h.Columnas.Add(new("Mes", TipoColumna.Texto, 10));
                h.Columnas.Add(new("Comprobantes", TipoColumna.Entero, 13));
                h.Columnas.Add(new("Notas de crédito", TipoColumna.Entero, 15));
                h.Columnas.Add(new("Base gravada", TipoColumna.Monto, 15));
                h.Columnas.Add(new("Exonerado", TipoColumna.Monto, 13));
                h.Columnas.Add(new("Inafecto", TipoColumna.Monto, 13));
                h.Columnas.Add(new("Exportación", TipoColumna.Monto, 13));
                h.Columnas.Add(new("IGV", TipoColumna.Monto, 14));
                h.Columnas.Add(new("Total", TipoColumna.Monto, 15));
                foreach (var f in filas)
                    h.Filas.Add([Mes(Txt(f["mes"])), Ent(f["comprobantes"]), Ent(f["notas"]), Dec(f["gravado"]), Dec(f["exonerado"]),
                                 Dec(f["inafecto"]), Dec(f["exportacion"]), Dec(f["igv"]), Dec(f["total"])]);
                break;

            case "registro":
                h.FilaTotal = true;
                h.Horizontal = true;
                h.Columnas.Add(new("Periodo", TipoColumna.Texto, 9));
                h.Columnas.Add(new("Fecha de emisión", TipoColumna.Fecha, 11));
                h.Columnas.Add(new("Fecha de vencimiento", TipoColumna.Fecha, 11));
                h.Columnas.Add(new("Tipo", TipoColumna.Texto, 5));
                h.Columnas.Add(new("Serie", TipoColumna.Texto, 6));
                h.Columnas.Add(new("Número", TipoColumna.Numero, 9));
                h.Columnas.Add(new("Tipo doc. cliente", TipoColumna.Texto, 8));
                h.Columnas.Add(new("Nº doc. cliente", TipoColumna.Texto, 13));
                h.Columnas.Add(new("Cliente", TipoColumna.Texto, 34));
                h.Columnas.Add(new("Exportación", TipoColumna.Monto, 12));
                h.Columnas.Add(new("Base gravada", TipoColumna.Monto, 13));
                h.Columnas.Add(new("Exonerada", TipoColumna.Monto, 11));
                h.Columnas.Add(new("Inafecta", TipoColumna.Monto, 11));
                h.Columnas.Add(new("IGV", TipoColumna.Monto, 12));
                h.Columnas.Add(new("Total", TipoColumna.Monto, 13));
                h.Columnas.Add(new("Moneda", TipoColumna.Texto, 7));
                h.Columnas.Add(new("Tipo de cambio", TipoColumna.Cantidad, 9));
                h.Columnas.Add(new("Ref. fecha", TipoColumna.Fecha, 11));
                h.Columnas.Add(new("Ref. tipo", TipoColumna.Texto, 6));
                h.Columnas.Add(new("Ref. serie", TipoColumna.Texto, 7));
                h.Columnas.Add(new("Ref. número", TipoColumna.Numero, 9));
                h.Columnas.Add(new("Estado", TipoColumna.Texto, 13));
                foreach (var f in filas)
                {
                    var fecha = Txt(f["fecha"]) ?? "";
                    h.Filas.Add([
                        fecha.Length >= 7 ? fecha[..4] + fecha.Substring(5, 2) + "00" : "",
                        Txt(f["fecha"]), Txt(f["vence"]), Txt(f["tipo"]), Txt(f["serie"]), Ent(f["numero"]),
                        Txt(f["tipoDoc"]), Txt(f["documento"]), Txt(f["cliente"]),
                        Dec(f["exportacion"]), Dec(f["gravado"]), Dec(f["exonerado"]), Dec(f["inafecto"]),
                        Dec(f["igv"]), Dec(f["total"]), Txt(f["moneda"]),
                        f["tipoCambio"] is null ? null : Dec(f["tipoCambio"]),
                        Txt(f["refFecha"]), Txt(f["refTipo"]), Txt(f["refSerie"]),
                        f["refNumero"] is null ? null : Ent(f["refNumero"]),
                        Estado(Txt(f["estado"]))]);
                }
                break;

            case "problemas":
                h.Horizontal = true;
                h.Columnas.Add(new("Fecha", TipoColumna.Fecha, 11));
                h.Columnas.Add(new("Comprobante", TipoColumna.Texto, 16));
                h.Columnas.Add(new("Tipo", TipoColumna.Texto, 14));
                h.Columnas.Add(new("Cliente", TipoColumna.Texto, 32));
                h.Columnas.Add(new("Estado", TipoColumna.Texto, 16));
                h.Columnas.Add(new("Código SUNAT", TipoColumna.Texto, 10));
                h.Columnas.Add(new("Mensaje", TipoColumna.Texto, 56));
                h.Columnas.Add(new("Total", TipoColumna.Monto, 13));
                foreach (var f in filas)
                    h.Filas.Add([Txt(f["fecha"]), Txt(f["numero"]) ?? "(sin número)", TipoTexto(Txt(f["tipo"])), Txt(f["cliente"]),
                                 Estado(Txt(f["estado"])), Txt(f["codigo"]), Txt(f["mensaje"]), Dec(f["total"])]);
                break;

            default: // anulados
                h.Horizontal = true;
                h.Columnas.Add(new("Fecha", TipoColumna.Fecha, 11));
                h.Columnas.Add(new("Comprobante", TipoColumna.Texto, 16));
                h.Columnas.Add(new("Tipo", TipoColumna.Texto, 16));
                h.Columnas.Add(new("Cliente", TipoColumna.Texto, 32));
                h.Columnas.Add(new("Modifica a", TipoColumna.Texto, 16));
                h.Columnas.Add(new("Motivo", TipoColumna.Texto, 40));
                h.Columnas.Add(new("Estado", TipoColumna.Texto, 13));
                h.Columnas.Add(new("Moneda", TipoColumna.Texto, 7));
                h.Columnas.Add(new("Total", TipoColumna.Monto, 13));
                foreach (var f in filas)
                    h.Filas.Add([Txt(f["fecha"]), Txt(f["numero"]), TipoTexto(Txt(f["tipo"])),
                                 Txt(f["cliente"]), Txt(f["afecta"]), Motivo(Txt(f["tipo"]) == "08" ? "D" + Txt(f["tipoNota"]) : Txt(f["tipoNota"]), Txt(f["motivo"])),
                                 Estado(Txt(f["estado"])), Txt(f["moneda"]), Dec(f["total"])]);
                break;
        }
        return h;
    }

    private static string TipoTexto(string? t) => t switch
    {
        "01" => "Factura", "03" => "Boleta", "07" => "Nota de crédito", "08" => "Nota de débito", _ => t ?? ""
    };

    private static string Motivo(string? codigo, string? texto)
    {
        var m = codigo switch
        {
            "01" => "Anulación de la operación", "02" => "Error en el RUC", "04" => "Descuento global",
            "05" => "Descuento por ítem", "06" => "Devolución total", "07" => "Devolución por ítem",
            "09" => "Disminución en el valor",
            // 22 oct: notas de débito (catálogo 10), con «D» delante para no chocar con las de crédito.
            "D01" => "Intereses por mora", "D02" => "Aumento en el valor", "D03" => "Penalidades u otros conceptos",
            _ => null
        };
        return string.Join(" · ", new[] { m, texto }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string Mes(string? aaaaMm)
    {
        if (aaaaMm is not { Length: 7 } || !int.TryParse(aaaaMm[5..], out var m) || m is < 1 or > 12) return aaaaMm ?? "";
        string[] nombres = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto",
                            "setiembre", "octubre", "noviembre", "diciembre"];
        return $"{nombres[m - 1]} {aaaaMm[..4]}";
    }

    private static async Task<List<string>> TitulosAsync(
        string nombre, Periodo p, string? ambiente, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        var empresa = await db.Empresas.AsNoTracking()
            .Where(e => e.Id == ctx.EmpresaId)
            .Select(e => new { e.Nombre, e.RazonSocial, e.Ruc })
            .FirstOrDefaultAsync(ct);

        var t = new List<string>
        {
            nombre,
            $"{empresa?.RazonSocial ?? empresa?.Nombre}{(empresa?.Ruc is { } ruc ? " · RUC " + ruc : "")}",
            p.Texto
        };
        if (ambiente == "beta") t.Add("MODO PRUEBA: comprobantes de prueba (SUNAT beta), sin valor ante SUNAT.");
        return t;
    }

    private static string Estado(string? e) => e switch
    {
        EstadoDocumento.Aceptado  => "Aceptado",
        EstadoDocumento.Observado => "Con observaciones",
        EstadoDocumento.Encolado  => "En camino",
        EstadoDocumento.Anulado   => "Anulado",
        EstadoDocumento.Rechazado => "Rechazado",
        EstadoDocumento.Error     => "No se pudo enviar",
        _ => e ?? ""
    };

    private static string Nombre(string reporte) => reporte switch
    {
        "igv"       => "Resumen de IGV",
        "registro"  => "Registro de Ventas",
        "problemas" => "Comprobantes con problema",
        _           => "Anulados y notas de crédito"
    };

    private static string Archivo(string reporte) => reporte switch
    {
        "igv"       => "resumen-igv",
        "registro"  => "registro-de-ventas",
        "problemas" => "comprobantes-con-problema",
        _           => "anulados-y-notas"
    };

    private static FormattableString Formato(string sql, Periodo p) =>
        FormattableStringFactory.Create(sql, p.Desde, p.Hasta);

    private static string? LeerPeriodo(string? desde, string? hasta, out Periodo p)
    {
        var hoy = HoyEnPeru();
        p = new Periodo(new DateOnly(hoy.Year, hoy.Month, 1), hoy);

        static bool Leer(string? texto, out DateOnly f) =>
            DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out f);

        var d = p.Desde; var h = p.Hasta;
        if (!string.IsNullOrWhiteSpace(desde) && !Leer(desde, out d)) return "La fecha «desde» no es válida.";
        if (!string.IsNullOrWhiteSpace(hasta) && !Leer(hasta, out h)) return "La fecha «hasta» no es válida.";
        if (d > h) return "La fecha «desde» es posterior a «hasta».";
        if (h.DayNumber - d.DayNumber > 366) return "Elige un periodo de un año como máximo.";

        p = new Periodo(d, h);
        return null;
    }

    private static string? Fecha(JsonNode? n) =>
        Txt(n) is { Length: >= 10 } s ? $"{s.Substring(8, 2)}/{s.Substring(5, 2)}/{s[..4]}" : null;
    private static decimal Dec(JsonNode? n) => n is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : 0m;
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