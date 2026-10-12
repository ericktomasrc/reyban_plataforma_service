using System.Globalization;
using System.Text;

namespace Plataforma.Api.Administracion;

/// <summary>La dirección del emisor, ya limpia y comprobada.</summary>
public sealed record DireccionDelAlta(
    string Ubigeo, string Direccion, string Distrito, string Provincia, string Departamento);

/// <summary>
/// Las reglas del RUC y de la dirección fiscal, para el alta de una empresa.
///
/// SON LAS MISMAS QUE APLICA EL SERVICIO (su Direccion.cs), copiadas a
/// propósito: la plataforma valida TODO antes de llamar al servicio, para que
/// un dedazo se diga aquí y no deje nada creado allí (diseño §13). El servicio
/// las vuelve a aplicar: si un día no coinciden, manda el servicio y su
/// mensaje llega tal cual a la pantalla.
///
/// LA PLATAFORMA NO SE QUEDA CON LA DIRECCIÓN: la manda y vive en el servicio.
///
/// POR QUÉ IMPORTA: la dirección sale en cada comprobante. SUNAT no rechaza
/// una dirección de Lima en una empresa de Arequipa, pero el comprobante sale
/// mal y nadie se da cuenta. El caso real: el formulario traía LIMA y 150101
/// por defecto.
///
/// LO QUE SE COMPRUEBA: que esté todo, y que los dos primeros dígitos del
/// ubigeo (el departamento, catálogo del INEI) correspondan al departamento
/// escrito. NO se comprueban provincia ni distrito contra el ubigeo: eso
/// exige el catálogo completo de distritos (unos 1.900), y la idea de
/// derivarlos del ubigeo en vez de comprobarlos está anotada para más
/// adelante (alta-empresa-diseno.md §14).
/// </summary>
public static class ReglasEmisor
{
    /// <summary>Los 25 departamentos del INEI por su código. Sin tildes y en mayúsculas.</summary>
    private static readonly Dictionary<string, string[]> Departamentos = new()
    {
        ["01"] = ["AMAZONAS"],
        ["02"] = ["ANCASH"],
        ["03"] = ["APURIMAC"],
        ["04"] = ["AREQUIPA"],
        ["05"] = ["AYACUCHO"],
        ["06"] = ["CAJAMARCA"],
        ["07"] = ["CALLAO", "PROVINCIA CONSTITUCIONAL DEL CALLAO"],
        ["08"] = ["CUSCO", "CUZCO"],
        ["09"] = ["HUANCAVELICA"],
        ["10"] = ["HUANUCO"],
        ["11"] = ["ICA"],
        ["12"] = ["JUNIN"],
        ["13"] = ["LA LIBERTAD"],
        ["14"] = ["LAMBAYEQUE"],
        ["15"] = ["LIMA"],
        ["16"] = ["LORETO"],
        ["17"] = ["MADRE DE DIOS"],
        ["18"] = ["MOQUEGUA"],
        ["19"] = ["PASCO"],
        ["20"] = ["PIURA"],
        ["21"] = ["PUNO"],
        ["22"] = ["SAN MARTIN"],
        ["23"] = ["TACNA"],
        ["24"] = ["TUMBES"],
        ["25"] = ["UCAYALI"]
    };

    /// <summary>
    /// Para comparar: sin tildes, en mayúsculas y con un solo espacio entre
    /// palabras. «Lima», «LIMA » y «lima» valen igual; «Junín» y «JUNIN» también.
    /// </summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "";

        var sinTildes = new StringBuilder();

        foreach (var c in texto.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sinTildes.Append(c);

        return string.Join(' ',
            sinTildes.ToString().Normalize(NormalizationForm.FormC)
                .ToUpperInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Limpia y comprueba. Devuelve la dirección lista para guardar, o el
    /// motivo por el que no vale, escrito para quien la tecleó.
    ///
    /// SE GUARDA COMO SE ESCRIBIÓ (solo sin espacios de sobra): la
    /// normalización es para comparar, no para cambiar lo que sale en el
    /// comprobante.
    /// </summary>
    public static (DireccionDelAlta? Direccion, string? Problema) Revisar(
        string? ubigeo, string? direccion, string? distrito, string? provincia, string? departamento)
    {
        static string Limpio(string? s) =>
            string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var u = Limpio(ubigeo);
        var d = Limpio(direccion);
        var di = Limpio(distrito);
        var p = Limpio(provincia);
        var de = Limpio(departamento);

        if (d.Length < 3) return (null, "Falta la dirección (calle y número).");
        if (d.Length > 200) return (null, "La dirección es demasiado larga (máximo 200 caracteres).");
        if (di.Length == 0) return (null, "Falta el distrito.");
        if (p.Length == 0) return (null, "Falta la provincia.");
        if (de.Length == 0) return (null, "Falta el departamento.");
        if (di.Length > 60 || p.Length > 60 || de.Length > 60)
            return (null, "Distrito, provincia y departamento: máximo 60 caracteres cada uno.");

        if (u.Length != 6 || !u.All(char.IsAsciiDigit))
            return (null, "El ubigeo son 6 dígitos (por ejemplo 040101 para Arequipa, Arequipa, Arequipa).");

        if (!Departamentos.TryGetValue(u[..2], out var nombres))
            return (null, $"El ubigeo {u} no existe: los dos primeros dígitos van de 01 a 25.");

        var escrito = Normalizar(de);

        if (!nombres.Contains(escrito))
            return (null,
                $"El ubigeo {u} es de {Titulo(nombres[0])}, y el departamento dice «{de}». " +
                "Uno de los dos está mal: revisa el ubigeo del distrito en la ficha RUC.");

        return (new DireccionDelAlta(u, d, di, p, de), null);
    }

    /// <summary>
    /// El RUC: 11 dígitos, empieza por 10, 15, 16, 17 o 20, y el último es el
    /// dígito verificador (módulo 11, el mismo cálculo que hace SUNAT). Así un
    /// dedazo no crea un emisor que no existe.
    /// </summary>
    public static string? ProblemaRuc(string? ruc)
    {
        if (ruc is null || ruc.Length != 11 || !ruc.All(char.IsAsciiDigit))
            return "El RUC son 11 dígitos.";

        if (ruc[..2] is not ("10" or "15" or "16" or "17" or "20"))
            return "Un RUC empieza por 10, 15, 16, 17 o 20.";

        int[] pesos = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];

        var suma = 0;
        for (var i = 0; i < 10; i++) suma += (ruc[i] - '0') * pesos[i];

        var digito = 11 - suma % 11;
        if (digito == 10) digito = 0;
        if (digito == 11) digito = 1;

        return digito == ruc[10] - '0'
            ? null
            : "Ese RUC no existe: el último dígito no cuadra. Revisa que esté bien copiado.";
    }

    private static string Titulo(string s) =>
        CultureInfo.GetCultureInfo("es-PE").TextInfo.ToTitleCase(s.ToLowerInvariant());
}