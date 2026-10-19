using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Plataforma.Api.Reportes;

/// <summary>Cómo se escribe una columna en el Excel.</summary>
public enum TipoColumna
{
    Texto,
    /// <summary>Soles: 1,234.50 (negativos en rojo). Se suma en la fila de totales.</summary>
    Monto,
    /// <summary>Un conteo: 1,234. Se suma en la fila de totales.</summary>
    Entero,
    /// <summary>Cantidad con decimales (unidades, tipo de cambio). No se suma.</summary>
    Cantidad,
    /// <summary>Fecha de verdad para Excel (se ordena y filtra como fecha): dd/mm/aaaa.</summary>
    Fecha,
    /// <summary>Un número que es un código (correlativo): sin separador de miles, no se suma.</summary>
    Numero,
    /// <summary>0.125 se ve como 12.5 %.</summary>
    Porcentaje
}

public sealed record ColumnaExcel(string Titulo, TipoColumna Tipo = TipoColumna.Texto, int Ancho = 16);

/// <summary>Una celda con su propio formato (en hojas de «concepto / valor»).</summary>
public sealed record CeldaExcel(object? Valor, TipoColumna Tipo);

/// <summary>Una fila que es un subtítulo dentro de la hoja («IGV del periodo»).</summary>
public sealed record FilaSeccion(string Texto);

/// <summary>Una hoja: título, datos de la empresa y del periodo, cabecera, filas y totales.</summary>
public sealed class HojaExcel(string nombre)
{
    public string Nombre { get; } = nombre;
    /// <summary>El primero es el título; los demás, debajo (empresa, periodo, aviso de prueba).</summary>
    public List<string> Titulos { get; } = [];
    public List<ColumnaExcel> Columnas { get; } = [];
    /// <summary>Cada fila: un object?[] (una celda por columna) o un FilaSeccion.</summary>
    public List<object?[]> Filas { get; } = [];
    /// <summary>Fila de TOTAL al final, con fórmulas que suman los montos y conteos (respetan el filtro de Excel).</summary>
    public bool FilaTotal { get; set; }
    /// <summary>Filtro y paneles fijos en la cabecera (en tablas; no en hojas de «concepto / valor»).</summary>
    public bool ConFiltro { get; set; } = true;
    public bool Horizontal { get; set; }
}

/// <summary>
/// 032/037 — UN .XLSX DE REPORTE, SIN LIBRERÍAS (un .xlsx es un .zip con XML).
///
/// Lo que lleva para que se vea profesional y le sirva al contador:
///   · título grande, empresa y RUC, periodo, fecha de generación;
///   · cabecera de color con filtro, y la cabecera fija al bajar;
///   · filas cebradas, montos como números (negativos en rojo), fechas como
///     fechas de verdad, correlativos sin separador de miles;
///   · fila de TOTAL con fórmulas SUBTOTAL (si filtras en Excel, suma lo filtrado);
///   · impresión lista: A4, todo el ancho en una hoja, cabecera repetida y
///     «Página N de M».
/// </summary>
public static class ExcelSimple
{
    public const string TipoMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Estilos (índices de cellXfs, ver Estilos()).
    private const int SNormal = 0, SCabecera = 1, STitulo = 2, SSubtitulo = 3, SAviso = 4, SVacio = 5, SSeccion = 6,
        STotalTexto = 7, STotalMonto = 8, STotalEntero = 9;
    // Por tipo: [normal, cebra]
    private static readonly Dictionary<TipoColumna, (int Normal, int Cebra)> PorTipo = new()
    {
        [TipoColumna.Texto]      = (10, 11),
        [TipoColumna.Monto]      = (12, 13),
        [TipoColumna.Entero]     = (14, 15),
        [TipoColumna.Cantidad]   = (16, 17),
        [TipoColumna.Fecha]      = (18, 19),
        [TipoColumna.Numero]     = (20, 21),
        [TipoColumna.Porcentaje] = (22, 23),
    };

    public static byte[] Crear(IReadOnlyList<HojaExcel> hojas)
    {
        var generado = AhoraEnLima();
        var nombres = NombresUnicos(hojas);

        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            Escribir(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
                """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""" +
                """<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""" +
                """<Default Extension="xml" ContentType="application/xml"/>""" +
                """<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""" +
                """<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""" +
                """<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>""" +
                string.Concat(hojas.Select((_, i) =>
                    $"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""")) +
                "</Types>");

            Escribir(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
                """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
                """<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>""" +
                """<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>""" +
                "</Relationships>");

            var titulo = hojas.Count > 0 && hojas[0].Titulos.Count > 0 ? hojas[0].Titulos[0] : "Reporte";
            Escribir(zip, "docProps/core.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
                """<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">""" +
                $"<dc:title>{Xml(titulo)}</dc:title><dc:creator>Reyban · Facturación</dc:creator>" +
                $"""<dcterms:created xsi:type="dcterms:W3CDTF">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created>""" +
                "</cp:coreProperties>");

            // Nombres definidos: el filtro de cada hoja y la cabecera que se repite al imprimir.
            var definidos = new StringBuilder();
            for (var i = 0; i < hojas.Count; i++)
            {
                var h = hojas[i];
                if (h.Columnas.Count == 0) continue;
                var filaCab = FilaCabecera(h);
                var hoja = $"'{nombres[i].Replace("'", "''")}'";
                definidos.Append($"""<definedName name="_xlnm.Print_Titles" localSheetId="{i}">{Xml(hoja)}!${filaCab}:${filaCab}</definedName>""");
                if (h.ConFiltro && h.Filas.Count > 0)
                    definidos.Append($"""<definedName name="_xlnm._FilterDatabase" localSheetId="{i}" hidden="1">{Xml(hoja)}!$A${filaCab}:${Columna(h.Columnas.Count)}${filaCab + h.Filas.Count}</definedName>""");
            }

            Escribir(zip, "xl/workbook.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
                """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""" +
                string.Concat(hojas.Select((h, i) =>
                    $"""<sheet name="{Xml(nombres[i])}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""")) +
                "</sheets>" +
                (definidos.Length > 0 ? $"<definedNames>{definidos}</definedNames>" : "") +
                """<calcPr calcId="191029" fullCalcOnLoad="1"/></workbook>""");

            Escribir(zip, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
                """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
                string.Concat(hojas.Select((_, i) =>
                    $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>""")) +
                $"""<Relationship Id="rId{hojas.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""" +
                "</Relationships>");

            Escribir(zip, "xl/styles.xml", Estilos());

            for (var i = 0; i < hojas.Count; i++)
                Escribir(zip, $"xl/worksheets/sheet{i + 1}.xml", Hoja(hojas[i], generado));
        }
        return memoria.ToArray();
    }

    /// <summary>La fila de la cabecera: títulos + «Generado el…» + una en blanco.</summary>
    private static int FilaCabecera(HojaExcel h) => h.Titulos.Count + 3;

    private static string Hoja(HojaExcel h, DateTime generado)
    {
        var columnas = Math.Max(h.Columnas.Count, 1);
        var ultimaCol = Columna(columnas);
        var filaCab = FilaCabecera(h);
        var s = new StringBuilder();

        s.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        s.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        s.Append("""<sheetPr><pageSetUpPr fitToPage="1"/></sheetPr>""");

        // La cabecera queda fija al bajar.
        s.Append("""<sheetViews><sheetView workbookViewId="0" showGridLines="0">""");
        if (h.Columnas.Count > 0 && h.ConFiltro)
            s.Append($"""<pane ySplit="{filaCab}" topLeftCell="A{filaCab + 1}" activePane="bottomLeft" state="frozen"/>""");
        s.Append("</sheetView></sheetViews>");
        s.Append("""<sheetFormatPr defaultRowHeight="15"/>""");

        if (h.Columnas.Count > 0)
        {
            s.Append("<cols>");
            for (var c = 0; c < h.Columnas.Count; c++)
                s.Append(CultureInfo.InvariantCulture, $"""<col min="{c + 1}" max="{c + 1}" width="{h.Columnas[c].Ancho + 2}" customWidth="1"/>""");
            s.Append("</cols>");
        }

        s.Append("<sheetData>");
        var fila = 1;
        var combinadas = new List<string>();

        // Título, empresa, periodo, aviso.
        foreach (var (titulo, i) in h.Titulos.Select((t, i) => (t, i)))
        {
            var estilo = i == 0 ? STitulo : titulo.StartsWith("MODO PRUEBA", StringComparison.Ordinal) ? SAviso : SSubtitulo;
            var alto = i == 0 ? """ ht="24" customHeight="1" """ : " ";
            s.Append($"""<row r="{fila}"{alto.TrimEnd()}>{Celda(1, fila, titulo, estilo)}</row>""");
            if (columnas > 1) combinadas.Add($"A{fila}:{ultimaCol}{fila}");
            fila++;
        }
        s.Append($"""<row r="{fila}">{Celda(1, fila, $"Generado el {generado:dd/MM/yyyy} a las {generado:HH:mm} (hora de Lima) · Reyban · Facturación", SSubtitulo)}</row>""");
        if (columnas > 1) combinadas.Add($"A{fila}:{ultimaCol}{fila}");
        fila += 2;

        // Cabecera.
        var primeraDatos = fila + 1;
        if (h.Columnas.Count > 0)
        {
            s.Append($"""<row r="{fila}" ht="20" customHeight="1">""");
            for (var c = 0; c < h.Columnas.Count; c++) s.Append(Celda(c + 1, fila, h.Columnas[c].Titulo, SCabecera));
            s.Append("</row>");
            fila++;
        }

        // Filas.
        var cebra = false;
        foreach (var datos in h.Filas)
        {
            if (datos is [FilaSeccion seccion])
            {
                s.Append($"""<row r="{fila}" ht="20" customHeight="1">{Celda(1, fila, seccion.Texto, SSeccion)}</row>""");
                if (columnas > 1) combinadas.Add($"A{fila}:{ultimaCol}{fila}");
                fila++; cebra = false;
                continue;
            }

            s.Append($"""<row r="{fila}">""");
            for (var c = 0; c < datos.Length; c++)
            {
                var valor = datos[c];
                var tipo = c < h.Columnas.Count ? h.Columnas[c].Tipo : TipoColumna.Texto;
                if (valor is CeldaExcel celda) { valor = celda.Valor; tipo = celda.Tipo; }
                var (normal, conCebra) = PorTipo[tipo];
                s.Append(Celda(c + 1, fila, Convertir(valor, tipo), cebra ? conCebra : normal));
            }
            s.Append("</row>");
            fila++; cebra = !cebra;
        }

        if (h.Filas.Count == 0 && h.Columnas.Count > 0)
        {
            s.Append($"""<row r="{fila}">{Celda(1, fila, "Sin datos en el periodo elegido.", SVacio)}</row>""");
            if (columnas > 1) combinadas.Add($"A{fila}:{ultimaCol}{fila}");
            fila++;
        }

        // Totales con SUBTOTAL(9, …): si se filtra en Excel, suma solo lo visible.
        if (h.FilaTotal && h.Filas.Count > 0 && h.Columnas.Count > 0)
        {
            var ultimaDatos = fila - 1;
            s.Append($"""<row r="{fila}" ht="20" customHeight="1">""");
            for (var c = 0; c < h.Columnas.Count; c++)
            {
                var ref_ = Columna(c + 1) + fila.ToString(CultureInfo.InvariantCulture);
                var tipo = h.Columnas[c].Tipo;
                if (c == 0)
                    s.Append(Celda(1, fila, "TOTAL", STotalTexto));
                else if (tipo is TipoColumna.Monto or TipoColumna.Entero)
                    s.Append($"""<c r="{ref_}" s="{(tipo == TipoColumna.Monto ? STotalMonto : STotalEntero)}"><f>SUBTOTAL(9,{Columna(c + 1)}{primeraDatos}:{Columna(c + 1)}{ultimaDatos})</f></c>""");
                else
                    s.Append($"""<c r="{ref_}" s="{STotalTexto}"/>""");
            }
            s.Append("</row>");
            fila++;
        }

        s.Append("</sheetData>");

        if (h.ConFiltro && h.Columnas.Count > 0 && h.Filas.Count > 0)
            s.Append($"""<autoFilter ref="A{filaCab}:{ultimaCol}{filaCab + h.Filas.Count}"/>""");

        if (combinadas.Count > 0)
        {
            s.Append($"""<mergeCells count="{combinadas.Count}">""");
            foreach (var m in combinadas) s.Append($"""<mergeCell ref="{m}"/>""");
            s.Append("</mergeCells>");
        }

        s.Append("""<pageMargins left="0.4" right="0.4" top="0.5" bottom="0.6" header="0.25" footer="0.25"/>""");
        s.Append($"""<pageSetup paperSize="9" orientation="{(h.Horizontal || h.Columnas.Count > 7 ? "landscape" : "portrait")}" fitToWidth="1" fitToHeight="0"/>""");
        s.Append("""<headerFooter><oddFooter>&amp;L&amp;8Reyban · Facturación&amp;R&amp;8Página &amp;P de &amp;N</oddFooter></headerFooter>""");
        s.Append("</worksheet>");
        return s.ToString();
    }

    /// <summary>Lo que llega como texto y la columna dice que es fecha o número, se convierte.</summary>
    private static object? Convertir(object? valor, TipoColumna tipo)
    {
        if (valor is not string texto || texto.Length == 0) return valor;

        if (tipo == TipoColumna.Fecha &&
            (DateOnly.TryParseExact(texto.Length >= 10 ? texto[..10] : texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ||
             DateOnly.TryParseExact(texto, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out f)))
            return f;

        return valor;
    }

    private static string Celda(int columna, int fila, object? valor, int estilo)
    {
        var r = Columna(columna) + fila.ToString(CultureInfo.InvariantCulture);
        return valor switch
        {
            null => $"""<c r="{r}" s="{estilo}"/>""",
            DateOnly d => $"""<c r="{r}" s="{estilo}"><v>{(d.DayNumber - new DateOnly(1899, 12, 30).DayNumber).ToString(CultureInfo.InvariantCulture)}</v></c>""",
            decimal n => $"""<c r="{r}" s="{estilo}"><v>{n.ToString(CultureInfo.InvariantCulture)}</v></c>""",
            int n => $"""<c r="{r}" s="{estilo}"><v>{n.ToString(CultureInfo.InvariantCulture)}</v></c>""",
            long n => $"""<c r="{r}" s="{estilo}"><v>{n.ToString(CultureInfo.InvariantCulture)}</v></c>""",
            double n => $"""<c r="{r}" s="{estilo}"><v>{n.ToString("R", CultureInfo.InvariantCulture)}</v></c>""",
            _ => $"""<c r="{r}" s="{estilo}" t="inlineStr"><is><t xml:space="preserve">{Xml(valor.ToString() ?? "")}</t></is></c>"""
        };
    }

    private static string Estilos() =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
        """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
        """<numFmts count="4">""" +
        """<numFmt numFmtId="164" formatCode="#,##0.00;[Red]\-#,##0.00"/>""" +
        """<numFmt numFmtId="165" formatCode="#,##0.###"/>""" +
        """<numFmt numFmtId="166" formatCode="dd/mm/yyyy"/>""" +
        """<numFmt numFmtId="167" formatCode="0.0%"/>""" +
        "</numFmts>" +
        """<fonts count="7">""" +
        """<font><sz val="10"/><color rgb="FF1D2329"/><name val="Calibri"/><family val="2"/></font>""" +                // 0 normal
        """<font><b/><sz val="10"/><color rgb="FFFFFFFF"/><name val="Calibri"/><family val="2"/></font>""" +            // 1 cabecera
        """<font><b/><sz val="16"/><color rgb="FF1F6F6A"/><name val="Calibri"/><family val="2"/></font>""" +            // 2 título
        """<font><sz val="10"/><color rgb="FF5C6570"/><name val="Calibri"/><family val="2"/></font>""" +                // 3 subtítulo
        """<font><b/><sz val="10"/><color rgb="FF9A5B00"/><name val="Calibri"/><family val="2"/></font>""" +            // 4 aviso
        """<font><i/><sz val="10"/><color rgb="FF8A939C"/><name val="Calibri"/><family val="2"/></font>""" +            // 5 vacío
        """<font><b/><sz val="11"/><color rgb="FF1F6F6A"/><name val="Calibri"/><family val="2"/></font>""" +            // 6 sección / total
        "</fonts>" +
        """<fills count="5">""" +
        """<fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>""" +
        """<fill><patternFill patternType="solid"><fgColor rgb="FF1F6F6A"/><bgColor indexed="64"/></patternFill></fill>""" +    // 2 cabecera
        """<fill><patternFill patternType="solid"><fgColor rgb="FFF3F6F5"/><bgColor indexed="64"/></patternFill></fill>""" +    // 3 cebra
        """<fill><patternFill patternType="solid"><fgColor rgb="FFE6F1EF"/><bgColor indexed="64"/></patternFill></fill>""" +    // 4 total
        "</fills>" +
        """<borders count="4">""" +
        """<border><left/><right/><top/><bottom/><diagonal/></border>""" +
        """<border><left/><right/><top/><bottom style="thin"><color rgb="FFE1E5E8"/></bottom><diagonal/></border>""" +                 // 1 fila
        """<border><left/><right/><top style="medium"><color rgb="FF1F6F6A"/></top><bottom style="medium"><color rgb="FF1F6F6A"/></bottom><diagonal/></border>""" + // 2 total
        """<border><left/><right/><top/><bottom style="medium"><color rgb="FF1F6F6A"/></bottom><diagonal/></border>""" +               // 3 sección
        "</borders>" +
        """<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>""" +
        """<cellXfs count="24">""" +
        """<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>""" +                                                                        // 0
        """<xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>""" + // 1 cabecera
        """<xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="center"/></xf>""" +  // 2 título
        """<xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1"/>""" +                                                         // 3 subtítulo
        """<xf numFmtId="0" fontId="4" fillId="0" borderId="0" xfId="0" applyFont="1"/>""" +                                                         // 4 aviso
        """<xf numFmtId="0" fontId="5" fillId="0" borderId="0" xfId="0" applyFont="1"/>""" +                                                         // 5 vacío
        """<xf numFmtId="0" fontId="6" fillId="0" borderId="3" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment vertical="bottom"/></xf>""" + // 6 sección
        """<xf numFmtId="0" fontId="6" fillId="4" borderId="2" xfId="0" applyFont="1" applyFill="1" applyBorder="1"/>""" +                          // 7 total texto
        """<xf numFmtId="164" fontId="6" fillId="4" borderId="2" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1"/>""" + // 8 total monto
        """<xf numFmtId="3" fontId="6" fillId="4" borderId="2" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1"/>""" +   // 9 total entero
        Par(0) +    // 10/11 texto
        Par(164) +  // 12/13 monto
        Par(3) +    // 14/15 entero
        Par(165) +  // 16/17 cantidad
        Par(166) +  // 18/19 fecha
        Par(1) +    // 20/21 número (sin miles)
        Par(167) +  // 22/23 porcentaje
        "</cellXfs>" +
        """<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>""" +
        "</styleSheet>";

    /// <summary>El estilo normal y el cebrado de un formato de número.</summary>
    private static string Par(int formato) =>
        $"""<xf numFmtId="{formato}" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1"/>""" +
        $"""<xf numFmtId="{formato}" fontId="0" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyFill="1" applyBorder="1"/>""";

    private static string Columna(int n)
    {
        var s = "";
        while (n > 0) { var r = (n - 1) % 26; s = (char)('A' + r) + s; n = (n - 1) / 26; }
        return s;
    }

    /// <summary>Excel no admite en el nombre de una hoja : \ / ? * [ ], ni más de 31 letras, ni dos iguales.</summary>
    private static List<string> NombresUnicos(IReadOnlyList<HojaExcel> hojas)
    {
        var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lista = new List<string>();
        foreach (var h in hojas)
        {
            var limpio = new string(h.Nombre.Where(c => !":\\/?*[]".Contains(c)).ToArray()).Trim();
            if (limpio.Length == 0) limpio = "Hoja";
            if (limpio.Length > 31) limpio = limpio[..31];
            var nombre = limpio; var n = 2;
            while (!usados.Add(nombre)) nombre = (limpio.Length > 27 ? limpio[..27] : limpio) + $" ({n++})";
            lista.Add(nombre);
        }
        return lista;
    }

    private static string Xml(string texto)
    {
        var sinControl = new string(texto.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray());
        return SecurityElement.Escape(sinControl) ?? "";
    }

    private static DateTime AhoraEnLima()
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")); }
        catch (TimeZoneNotFoundException) { return DateTime.UtcNow.AddHours(-5); }
    }

    private static void Escribir(ZipArchive zip, string ruta, string contenido)
    {
        var entrada = zip.CreateEntry(ruta, CompressionLevel.Optimal);
        using var flujo = entrada.Open();
        flujo.Write(new UTF8Encoding(false).GetBytes(contenido));
    }
}