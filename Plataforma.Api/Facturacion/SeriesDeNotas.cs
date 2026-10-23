namespace Plataforma.Api.Facturacion;

/// <summary>
/// 22 OCT — QUÉ SERIE USA UNA NOTA DE CRÉDITO O DE DÉBITO.
///
///   1. La que la empresa CONFIGURÓ para ese tipo (07 crédito, 08 débito) y
///      con la letra del original (F para facturas, B para boletas). Si hay
///      varias, la más usada (la de último número más alto).
///   2. Si no configuró ninguna, la de siempre —FC01 / BC01 (crédito),
///      FD01 / BD01 (débito)—, que se crea sola en 0 al emitir la primera.
///
/// Así se respeta lo que se dio de alta en Configuración › Series (si usa
/// FC02 porque FC01 la gastó en otro sistema, sale FC02) y a nadie se le
/// bloquea una nota por no haber creado la serie antes.
/// </summary>
public static class SeriesDeNotas
{
    public sealed record Eleccion(string Serie, bool Existe);

    public static string PorDefecto(string tipoNota, string tipoOriginal) =>
        (tipoOriginal == "03" ? "B" : "F") + (tipoNota == "08" ? "D01" : "C01");

    /// <param name="db">23 oct: con la base, se respetan las sucursales.</param>
    /// <param name="original">23 oct: el comprobante que corrige. Se prefiere una serie de
    /// notas de SU sucursal; las de otras sucursales no se usan. Sin asignar = de la 0000.</param>
    public static async Task<Eleccion> ElegirAsync(
        ClienteFacturacion facturacion, string tipoNota, string tipoOriginal, CancellationToken ct,
        Plataforma.Core.Datos.ContextoPlataforma? db = null, Plataforma.Core.Dominio.Documento? original = null)
    {
        var letra = tipoOriginal == "03" ? "B" : "F";
        var defecto = PorDefecto(tipoNota, tipoOriginal);

        var r = await facturacion.ConfigurarAsync<List<SerieDelServicio>>(
            HttpMethod.Get, "v1/configuracion/series", ct: ct);

        // Si no se pudieron leer, la de siempre: al emitir se crea (o ya existe).
        if (!r.Exito || r.Datos is null) return new(defecto, false);

        // 23 oct: de qué sucursal es cada serie, y cuál es la del original.
        Dictionary<string, Guid> mapa = [];
        Guid? suya = null;
        if (db is not null && original is not null)
        {
            mapa = await Sucursales.MapaAsync(db, original.EmpresaId, ct);
            suya = await Sucursales.DeNotaAsync(db, original, ct);
            var principal = await Sucursales.PrincipalAsync(db, original.EmpresaId, ct);
            if (suya == principal.Id) suya = null;   // la 0000 = las que no están asignadas
        }
        int Prioridad(string serie) =>
            mapa.TryGetValue(Sucursales.Clave(tipoNota, serie), out var de)
                ? (de == suya ? 0 : 9)      // de su sucursal, o de otra (no se usa)
                : (suya is null ? 0 : 1);   // sin asignar: de la 0000

        var configurada = r.Datos
            .Where(s => s.Activo && s.TipoComprobante == tipoNota &&
                        s.Serie.StartsWith(letra, StringComparison.OrdinalIgnoreCase) &&
                        Prioridad(s.Serie) < 9)
            .OrderBy(s => Prioridad(s.Serie))
            .ThenByDescending(s => s.UltimoCorrelativo)
            .ThenBy(s => s.Serie)
            .FirstOrDefault();

        return configurada is not null
            ? new(configurada.Serie, true)
            : new(defecto, r.Datos.Any(s => s.TipoComprobante == tipoNota && s.Serie == defecto));
    }
}