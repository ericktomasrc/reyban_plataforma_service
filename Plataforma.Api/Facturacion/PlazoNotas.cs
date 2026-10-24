namespace Plataforma.Api.Facturacion;

/// <summary>
/// 24 OCT — EL PLAZO PARA CORREGIR CON NOTA DE CRÉDITO (aviso, no bloqueo).
///
/// Las hojas del contador hablan de «15 días hábiles del mes siguiente».
/// Como hacen los sistemas comerciales (Odoo, SAP), la plataforma NO bloquea
/// la nota por plazo: quien emite responde ante SUNAT. Solo lo dice, en
/// pequeño, en la pantalla de la nota.
///
/// EL CÁLCULO: el día hábil número 15 del mes siguiente al de la emisión,
/// sin sábados, domingos ni feriados nacionales del Perú. Si el contador
/// confirma otra regla, se cambia aquí y nada más.
/// </summary>
public static class PlazoNotas
{
    public const int DiasHabiles = 15;

    public static DateOnly Limite(DateOnly emision)
    {
        var dia = new DateOnly(emision.Year, emision.Month, 1).AddMonths(1);
        var cuenta = 0;
        while (true)
        {
            if (EsHabil(dia) && ++cuenta == DiasHabiles) return dia;
            dia = dia.AddDays(1);
        }
    }

    public static bool EsHabil(DateOnly d) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !EsFeriado(d);

    /// <summary>Feriados nacionales (Ley 31788 y anteriores), con la Semana Santa de cada año.</summary>
    public static bool EsFeriado(DateOnly d)
    {
        var fijos = new (int Mes, int Dia)[]
        {
            (1, 1), (5, 1), (6, 7), (6, 29), (7, 23), (7, 28), (7, 29),
            (8, 6), (8, 30), (10, 8), (11, 1), (12, 8), (12, 9), (12, 25)
        };
        if (fijos.Contains((d.Month, d.Day))) return true;

        var pascua = Pascua(d.Year);
        return d == pascua.AddDays(-3) || d == pascua.AddDays(-2);   // jueves y viernes santo
    }

    /// <summary>Domingo de Pascua (algoritmo de Butcher, calendario gregoriano).</summary>
    private static DateOnly Pascua(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int mes = (h + l - 7 * m + 114) / 31, dia = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(y, mes, dia);
    }
}