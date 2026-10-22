using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// 22 OCT — LEE LA PRIMERA HOJA DE UN .XLSX (o un .csv), SIN LIBRERÍAS.
///
/// Un .xlsx es un .zip con XML: los textos van aparte (sharedStrings.xml) y
/// cada celda dice si es texto, número o booleano. Aquí solo hace falta lo
/// que escribe una persona en una plantilla: textos y números. Las fórmulas
/// se leen por su último valor calculado (el que guardó Excel).
///
/// Devuelve las filas con su NÚMERO DE FILA DE EXCEL (para decir «fila 7») y
/// cada celda por su columna (0 = A). Las celdas vacías no están.
/// </summary>
public static class LectorExcel
{
    public sealed class Problema(string mensaje) : Exception(mensaje);

    public sealed record Fila(int Numero, Dictionary<int, string> Celdas)
    {
        public string Celda(int columna) => Celdas.TryGetValue(columna, out var v) ? v.Trim() : "";
        public bool Vacia => Celdas.Values.All(string.IsNullOrWhiteSpace);
    }

    private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Las filas de la primera hoja. `maxFilas` para no leer un archivo sin fin.</summary>
    public static List<Fila> Leer(byte[] contenido, int maxFilas)
    {
        if (contenido.Length < 4) throw new Problema("El archivo está vacío.");

        // PK = zip = .xlsx. Si no, se prueba como texto separado (.csv).
        if (contenido[0] != (byte)'P' || contenido[1] != (byte)'K')
            return LeerCsv(contenido, maxFilas);

        try
        {
            using var zip = new ZipArchive(new MemoryStream(contenido), ZipArchiveMode.Read);
            var compartidos = LeerCompartidos(zip);
            var hoja = RutaPrimeraHoja(zip);
            var entrada = zip.GetEntry(hoja) ?? throw new Problema("El Excel no tiene hojas.");

            XDocument doc;
            using (var s = entrada.Open()) doc = XDocument.Load(s);

            var filas = new List<Fila>();
            foreach (var fila in doc.Descendants(M + "row"))
            {
                if (filas.Count >= maxFilas)
                    throw new Problema($"El archivo tiene más de {maxFilas:N0} filas. Pártelo en varios.");

                var numero = int.TryParse((string?)fila.Attribute("r"), out var n) ? n : filas.Count + 1;
                var celdas = new Dictionary<int, string>();
                var siguiente = 0;
                foreach (var c in fila.Elements(M + "c"))
                {
                    var col = Columna((string?)c.Attribute("r")) ?? siguiente;
                    siguiente = col + 1;
                    var valor = Valor(c, compartidos);
                    if (!string.IsNullOrWhiteSpace(valor)) celdas[col] = valor;
                }
                filas.Add(new Fila(numero, celdas));
            }
            return filas;
        }
        catch (Problema) { throw; }
        catch (InvalidDataException)
        {
            throw new Problema("El archivo no es un Excel (.xlsx) válido. Si es un .xls antiguo, ábrelo y guárdalo como .xlsx.");
        }
        catch (System.Xml.XmlException)
        {
            throw new Problema("El Excel está dañado: no se pudo leer. Ábrelo y vuelve a guardarlo.");
        }
    }

    private static List<string> LeerCompartidos(ZipArchive zip)
    {
        var lista = new List<string>();
        var e = zip.GetEntry("xl/sharedStrings.xml");
        if (e is null) return lista;
        using var s = e.Open();
        var doc = XDocument.Load(s);
        foreach (var si in doc.Root!.Elements(M + "si"))
            lista.Add(string.Concat(si.Descendants(M + "t").Select(t => t.Value)));
        return lista;
    }

    private static string RutaPrimeraHoja(ZipArchive zip)
    {
        var wb = zip.GetEntry("xl/workbook.xml");
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (wb is null || rels is null) return "xl/worksheets/sheet1.xml";

        XDocument dWb, dRels;
        using (var s = wb.Open()) dWb = XDocument.Load(s);
        using (var s = rels.Open()) dRels = XDocument.Load(s);

        var id = (string?)dWb.Descendants(M + "sheet").FirstOrDefault()?.Attribute(R + "id");
        var destino = (string?)dRels.Descendants(Pr + "Relationship")
            .FirstOrDefault(x => (string?)x.Attribute("Id") == id)?.Attribute("Target");
        if (string.IsNullOrWhiteSpace(destino)) return "xl/worksheets/sheet1.xml";

        return destino.StartsWith('/') ? destino.TrimStart('/') : "xl/" + destino;
    }

    private static string Valor(XElement c, List<string> compartidos)
    {
        var tipo = (string?)c.Attribute("t");
        var v = c.Element(M + "v")?.Value;
        return tipo switch
        {
            "s" => int.TryParse(v, out var i) && i >= 0 && i < compartidos.Count ? compartidos[i] : "",
            "inlineStr" => string.Concat(c.Descendants(M + "t").Select(t => t.Value)),
            "b" => v == "1" ? "SI" : "NO",
            _ => v ?? ""
        };
    }

    /// <summary>«C12» → 2. Null si no tiene referencia.</summary>
    private static int? Columna(string? referencia)
    {
        if (string.IsNullOrEmpty(referencia)) return null;
        var col = 0;
        var letras = 0;
        foreach (var ch in referencia)
        {
            if (ch is < 'A' or > 'Z') break;
            col = col * 26 + (ch - 'A' + 1);
            letras++;
        }
        return letras == 0 ? null : col - 1;
    }

    /// <summary>Un .csv: separado por «;», «,» o tabulador (el que más aparezca en la primera línea).</summary>
    private static List<Fila> LeerCsv(byte[] contenido, int maxFilas)
    {
        string texto;
        try { texto = new UTF8Encoding(false, true).GetString(contenido); }
        catch (DecoderFallbackException) { texto = Encoding.Latin1.GetString(contenido); }
        texto = texto.TrimStart('﻿');

        if (texto.Contains('\0'))
            throw new Problema("El archivo no es un Excel (.xlsx) ni un .csv.");

        var lineas = texto.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var primera = lineas.FirstOrDefault() ?? "";
        var sep = new[] { ';', ',', '\t' }.OrderByDescending(s => primera.Count(ch => ch == s)).First();

        var filas = new List<Fila>();
        for (var i = 0; i < lineas.Length; i++)
        {
            if (filas.Count >= maxFilas)
                throw new Problema($"El archivo tiene más de {maxFilas:N0} filas. Pártelo en varios.");
            var celdas = new Dictionary<int, string>();
            var partes = PartirCsv(lineas[i], sep);
            for (var j = 0; j < partes.Count; j++)
                if (!string.IsNullOrWhiteSpace(partes[j])) celdas[j] = partes[j];
            filas.Add(new Fila(i + 1, celdas));
        }
        return filas;
    }

    private static List<string> PartirCsv(string linea, char sep)
    {
        var partes = new List<string>();
        var actual = new StringBuilder();
        var comillas = false;
        for (var i = 0; i < linea.Length; i++)
        {
            var ch = linea[i];
            if (comillas)
            {
                if (ch == '"' && i + 1 < linea.Length && linea[i + 1] == '"') { actual.Append('"'); i++; }
                else if (ch == '"') comillas = false;
                else actual.Append(ch);
            }
            else if (ch == '"') comillas = true;
            else if (ch == sep) { partes.Add(actual.ToString()); actual.Clear(); }
            else actual.Append(ch);
        }
        partes.Add(actual.ToString());
        return partes;
    }

    /// <summary>Un número escrito por una persona: «1,234.50», «1234,5», «S/ 28.00».</summary>
    public static decimal? Numero(string texto)
    {
        // Lo que guarda Excel (28.5, 1.5E-3) se lee tal cual.
        if (decimal.TryParse(texto.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var exacto)) return exacto;

        var t = new string(texto.Where(ch => char.IsDigit(ch) || ch is '.' or ',' or '-').ToArray());
        if (t.Length == 0) return null;

        // Con punto y coma a la vez, el último es el decimal.
        if (t.Contains('.') && t.Contains(','))
            t = t.LastIndexOf(',') > t.LastIndexOf('.') ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        else if (t.Contains(','))
            t = t.Count(ch => ch == ',') == 1 && t.Length - t.IndexOf(',') - 1 != 3 ? t.Replace(',', '.') : t.Replace(",", "");

        return decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}