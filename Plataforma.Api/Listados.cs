namespace Plataforma.Api;

/// <summary>
/// 24 OCT — LO COMÚN DE LAS LISTAS: la página y cuántas filas por página.
///
/// Todas las listas largas van paginadas y con lo más nuevo arriba. Por
/// página: 20 (por defecto), 50 o 100. Cualquier otro número vuelve a 20: un
/// «porPagina=100000» no sirve para bajarse la base entera.
/// </summary>
public static class Listados
{
    public static int Pagina(int? pagina) => Math.Max(1, pagina ?? 1);

    public static int PorPagina(int? porPagina) => porPagina is 20 or 50 or 100 ? porPagina.Value : 20;
}