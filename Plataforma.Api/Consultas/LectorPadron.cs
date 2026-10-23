using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Plataforma.Api.Consultas;

/// <summary>Una fila del padrón, lista para la base.</summary>
public readonly record struct FilaPadron(
    string Ruc, string RazonSocial, string? Estado, string? Condicion, string? Ubigeo, string? Direccion);

/// <summary>045: una fila del padrón de locales anexos (un local de un RUC).</summary>
public readonly record struct FilaAnexo(string Ruc, string? Ubigeo, string Direccion);

/// <summary>
/// 042 — LEE EL PADRÓN REDUCIDO DE SUNAT (padron_reducido_ruc.zip o su .txt).
///
/// El archivo es texto ISO-8859-1, separado por «|», con una línea de títulos:
///
///   RUC|NOMBRE O RAZÓN SOCIAL|ESTADO DEL CONTRIBUYENTE|CONDICIÓN DE DOMICILIO|
///   UBIGEO|TIPO DE VÍA|NOMBRE DE VÍA|CÓDIGO DE ZONA|TIPO DE ZONA|NÚMERO|
///   INTERIOR|LOTE|DEPARTAMENTO|MANZANA|KILÓMETRO|
///
/// LAS COLUMNAS SE BUSCAN POR SU TÍTULO, no por su posición: si SUNAT agrega o
/// mueve una, sigue funcionando. Si faltan las que importan (RUC, razón social,
/// estado, condición), el archivo no es el padrón y se dice por qué — p. ej.
/// el de locales anexos, que se parece pero no trae la razón social.
///
/// Una línea mala (RUC que no son 11 dígitos, columnas de menos) se descarta y
/// se cuenta; no detiene la carga.
/// </summary>
public static class LectorPadron
{
    public sealed class Problema(string mensaje) : Exception(mensaje);

    /// <summary>
    /// Abre el archivo (zip con el .txt dentro, o el .txt directo) y devuelve
    /// el lector de texto en ISO-8859-1. Quien llama lo cierra.
    /// </summary>
    public static (Stream Archivo, StreamReader Texto, string Nombre) Abrir(string ruta)
    {
        var archivo = File.OpenRead(ruta);
        try
        {
            var firma = new byte[2];
            var leidos = archivo.Read(firma, 0, 2);
            archivo.Position = 0;

            if (leidos == 2 && firma[0] == (byte)'P' && firma[1] == (byte)'K')
            {
                var zip = new ZipArchive(archivo, ZipArchiveMode.Read, leaveOpen: false);
                var entrada = zip.Entries
                    .Where(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => e.Length)
                    .FirstOrDefault()
                    ?? throw new Problema("El .zip no trae ningún archivo .txt dentro. ¿Es el padrón de SUNAT?");

                return (archivo, new StreamReader(entrada.Open(), Encoding.Latin1, false, 1 << 16), entrada.Name);
            }

            return (archivo, new StreamReader(archivo, Encoding.Latin1, false, 1 << 16), Path.GetFileName(ruta));
        }
        catch
        {
            archivo.Dispose();
            throw;
        }
    }

    /// <summary>Las columnas que importan, por posición, sacadas de los títulos.</summary>
    public sealed record Columnas(
        int Ruc, int Razon, int Estado, int Condicion, int Ubigeo,
        int TipoVia, int NombreVia, int CodigoZona, int TipoZona, int Numero,
        int Interior, int Lote, int Departamento, int Manzana, int Kilometro, int Total);

    /// <param name="anexos">045: true = se espera el de LOCALES ANEXOS (padron_reducido_local_anexo).</param>
    public static Columnas LeerTitulos(string? linea, bool anexos = false)
    {
        if (string.IsNullOrWhiteSpace(linea))
            throw new Problema("El archivo está vacío.");

        var t = linea.Split('|').Select(Normalizar).ToArray();
        int Buscar(Func<string, bool> es) => Array.FindIndex(t, x => es(x));

        var c = new Columnas(
            Ruc:          Buscar(x => x == "RUC"),
            Razon:        Buscar(x => x.Contains("RAZON SOCIAL") || x.StartsWith("NOMBRE O")),
            Estado:       Buscar(x => x.StartsWith("ESTADO")),
            Condicion:    Buscar(x => x.StartsWith("CONDICION")),
            Ubigeo:       Buscar(x => x == "UBIGEO"),
            TipoVia:      Buscar(x => x == "TIPO DE VIA"),
            NombreVia:    Buscar(x => x == "NOMBRE DE VIA"),
            CodigoZona:   Buscar(x => x == "CODIGO DE ZONA"),
            TipoZona:     Buscar(x => x == "TIPO DE ZONA"),
            Numero:       Buscar(x => x == "NUMERO"),
            Interior:     Buscar(x => x == "INTERIOR"),
            Lote:         Buscar(x => x == "LOTE"),
            Departamento: Buscar(x => x == "DEPARTAMENTO"),
            Manzana:      Buscar(x => x == "MANZANA"),
            Kilometro:    Buscar(x => x == "KILOMETRO"),
            Total:        t.Length);

        if (c.Ruc < 0)
            throw new Problema(anexos
                ? "El archivo no tiene la columna RUC. No es el padrón de locales anexos de SUNAT."
                : "El archivo no tiene la columna RUC. No es el padrón reducido de SUNAT.");

        // 045: EL DE LOCALES ANEXOS: RUC, ubigeo y la dirección por partes; sin razón social.
        if (anexos)
        {
            if (c.Razon >= 0)
                throw new Problema("Este archivo es el padrón de RUC (trae la razón social). El que hace falta aquí es " +
                                   "el de locales anexos: padron_reducido_local_anexo.zip.");
            if (c.NombreVia < 0 || c.Ubigeo < 0)
                throw new Problema("Al archivo le falta la columna de ubigeo o de nombre de vía. " +
                                   "No parece el padrón de locales anexos de SUNAT.");
            return c;
        }

        if (c.Razon < 0)
            throw new Problema(c.TipoVia >= 0
                ? "Este archivo es el de LOCALES ANEXOS (solo trae RUC y dirección, sin razón social). " +
                  "El que hace falta es padron_reducido_ruc.zip."
                : "El archivo no tiene la columna de razón social. No es el padrón reducido de SUNAT.");

        if (c.Estado < 0 || c.Condicion < 0)
            throw new Problema("Al archivo le falta la columna de estado o de condición del contribuyente. " +
                               "No parece el padrón reducido de SUNAT.");

        return c;
    }

    /// <summary>Una línea → fila; null si no sirve (se cuenta como descartada).</summary>
    public static FilaPadron? LeerLinea(string linea, Columnas c)
    {
        if (linea.Length < 12) return null;

        // Un byte 0 suelto (pasa en archivos grandes) no entra en un texto de PostgreSQL.
        if (linea.Contains('\0')) linea = linea.Replace("\0", "");

        var p = linea.Split('|');
        if (p.Length <= Math.Max(c.Ruc, Math.Max(c.Razon, Math.Max(c.Estado, c.Condicion)))) return null;

        var ruc = p[c.Ruc].Trim();
        if (ruc.Length != 11 || !ruc.All(char.IsAsciiDigit)) return null;

        string? V(int i) => i >= 0 && i < p.Length && p[i].Trim() is { Length: > 0 } s && s != "-" && s != "----" ? s : null;

        var razon = V(c.Razon);
        if (razon is null) return null;

        return new FilaPadron(ruc, razon, V(c.Estado), V(c.Condicion), V(c.Ubigeo), Direccion(p, c, V));
    }

    /// <summary>045: una línea del de locales anexos → fila; null si no sirve (sin RUC válido o sin dirección).</summary>
    public static FilaAnexo? LeerLineaAnexo(string linea, Columnas c)
    {
        if (linea.Length < 12) return null;
        if (linea.Contains('\0')) linea = linea.Replace("\0", "");

        var p = linea.Split('|');
        if (p.Length <= c.Ruc) return null;

        var ruc = p[c.Ruc].Trim();
        if (ruc.Length != 11 || !ruc.All(char.IsAsciiDigit)) return null;

        string? V(int i) => i >= 0 && i < p.Length && p[i].Trim() is { Length: > 0 } s && s != "-" && s != "----" ? s : null;

        var direccion = Direccion(p, c, V);
        if (direccion is null) return null;

        var ubigeo = V(c.Ubigeo);
        if (ubigeo is not null && (ubigeo.Length != 6 || !ubigeo.All(char.IsAsciiDigit))) ubigeo = null;

        return new FilaAnexo(ruc, ubigeo, direccion);
    }

    /// <summary>
    /// La dirección como la escribe la ficha RUC: «AV. JAVIER PRADO NRO. 123
    /// INT. 4 URB. SAN BORJA MZA. B LOTE. 5 DPTO. 201 KM. 3». Sin los guiones
    /// que SUNAT pone en los campos vacíos.
    /// </summary>
    private static string? Direccion(string[] p, Columnas c, Func<int, string?> v)
    {
        var partes = new List<string>(10);
        void Poner(string? valor, string prefijo = "")
        {
            if (valor is not null) partes.Add(prefijo + valor);
        }

        Poner(v(c.TipoVia));
        Poner(v(c.NombreVia));
        Poner(v(c.Numero), "NRO. ");
        Poner(v(c.Interior), "INT. ");
        Poner(v(c.CodigoZona));
        Poner(v(c.TipoZona));
        Poner(v(c.Manzana), "MZA. ");
        Poner(v(c.Lote), "LOTE. ");
        Poner(v(c.Departamento), "DPTO. ");
        Poner(v(c.Kilometro), "KM. ");

        return partes.Count == 0 ? null : string.Join(' ', partes);
    }

    /// <summary>Mayúsculas y sin tildes: «CONDICIÓN DE DOMICILIO» → «CONDICION DE DOMICILIO».</summary>
    public static string Normalizar(string s)
    {
        var d = s.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) b.Append(ch);
        return b.ToString().Normalize(NormalizationForm.FormC);
    }
}