using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Plataforma.Api.Reportes;

/// <summary>
/// 037 — LA BÚSQUEDA Y LOS FILTROS DE LOS REPORTES, en un solo sitio.
///
/// Se aplican AQUÍ (en el servidor) y no en la pantalla, para que la pantalla
/// y el Excel muestren siempre lo mismo: si se busca «andina», el Excel trae
/// solo esas filas. El «total» del reporte se vuelve a sumar con lo que queda.
///
///   q       texto en cualquier columna (sin importar mayúsculas ni tildes;
///           «F001-101» o «F001-00000101» encuentran el comprobante)
///   tipo    01 factura, 03 boleta, 07 nota (o el tipo del reporte: negocio…)
///   estado  el estado de la fila
///   sunat   «real» o «prueba»
/// </summary>
public static class FiltroReportes
{
    public static void Aplicar(JsonObject datos, string? q, string? tipo, string? estado, string? sunat)
    {
        if (datos["filas"] is not JsonArray filas) return;

        var texto = Normalizar(q);
        tipo = string.IsNullOrWhiteSpace(tipo) ? null : tipo.Trim();
        estado = string.IsNullOrWhiteSpace(estado) ? null : estado.Trim();
        sunat = string.IsNullOrWhiteSpace(sunat) ? null : sunat.Trim().ToLowerInvariant();
        if (texto.Length == 0 && tipo is null && estado is null && sunat is null) return;

        var quedan = filas.OfType<JsonObject>()
            .Where(f => (texto.Length == 0 || Contiene(f, texto)) &&
                        (tipo is null || Igual(f["tipo"], tipo)) &&
                        (estado is null || Igual(f["estado"], estado)) &&
                        (sunat is null || EsSunat(f["ambiente"], sunat)))
            .Select(f => f.DeepClone())
            .ToArray();

        datos["filas"] = new JsonArray(quedan);
        datos["filtrado"] = true;

        if (datos.ContainsKey("total"))
            datos["total"] = quedan.Sum(f => f?["total"] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : 0m);
    }

    private static bool Contiene(JsonObject fila, string texto)
    {
        var junto = new StringBuilder();
        foreach (var (_, valor) in fila)
            if (valor is JsonValue v)
                junto.Append(' ').Append(v.TryGetValue<string>(out var s) ? s : v.ToJsonString());

        // El número del comprobante, como lo escribe la gente: F001-101 o F001-00000101.
        if (fila["serie"] is JsonValue se && se.TryGetValue<string>(out var serie) &&
            fila["numero"] is JsonValue nu && nu.TryGetValue<int>(out var numero))
            junto.Append($" {serie}-{numero} {serie}-{numero:D8}");

        var todo = Normalizar(junto.ToString());
        return texto.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(todo.Contains);
    }

    private static bool Igual(JsonNode? n, string valor) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(s, valor, StringComparison.OrdinalIgnoreCase);

    private static bool EsSunat(JsonNode? n, string sunat)
    {
        var a = n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var real = a is "real" or "produccion";
        return sunat == "real" ? real : !real;
    }

    /// <summary>Minúsculas y sin tildes: «PÉREZ» y «perez» son lo mismo.</summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "";
        var d = texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var s = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) s.Append(c);
        return s.ToString().Normalize(NormalizationForm.FormC);
    }
}