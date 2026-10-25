using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Reportes;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

/// <summary>
/// 22 OCT — IMPORTAR PRODUCTOS DESDE EXCEL (maqueta aprobada el 22 oct 2026).
///
///   1. Se baja la PLANTILLA: una hoja «Productos» con los de la empresa (para
///      cambiar precios de golpe y volver a subirla) o dos de ejemplo, y una
///      hoja «Ayuda» con las unidades y las afectaciones que valen.
///   2. Se sube llena y se REVISA (simular): fila por fila, qué es nuevo, qué
///      se actualiza, qué queda igual y qué tiene un error y por qué.
///   3. Se IMPORTA: entran los nuevos y los que cambian; las filas con error
///      no entran. NADA SE BORRA: un producto que no está en el Excel queda
///      como estaba.
///
/// Las reglas son las mismas que al crear un producto a mano (Revisar de
/// EndpointsProductos), más un poco de manga ancha con cómo escribe la gente:
/// «KILO» es KGM, «Gravado» es 10, «SÍ» / «x» es que incluye IGV.
/// </summary>
public static class EndpointsImportarProductos
{
    private const int MaxFilas = 5000;
    private const int MaxBytes = 10 * 1024 * 1024;

    private static readonly string[] Titulos =
        ["Código", "Descripción", "Unidad", "Precio", "Incluye IGV", "Afectación", "Código SUNAT"];

    // Cómo escribe la gente la unidad → el código del catálogo 03.
    private static readonly Dictionary<string, string> Sinonimos = new()
    {
        ["UNIDAD"] = "NIU", ["UND"] = "NIU", ["UNID"] = "NIU", ["U"] = "NIU", ["UNIDADES"] = "NIU", ["PZA"] = "NIU", ["PIEZA"] = "NIU",
        ["SERVICIO"] = "ZZ", ["SERV"] = "ZZ",
        ["KILO"] = "KGM", ["KILOS"] = "KGM", ["KG"] = "KGM", ["KILOGRAMO"] = "KGM", ["KILOGRAMOS"] = "KGM",
        ["TONELADA"] = "TNE", ["TN"] = "TNE", ["GRAMO"] = "GRM", ["GR"] = "GRM", ["G"] = "GRM",
        ["LITRO"] = "LTR", ["LT"] = "LTR", ["L"] = "LTR", ["METRO"] = "MTR", ["M"] = "MTR", ["MT"] = "MTR",
        ["M2"] = "MTK", ["METRO CUADRADO"] = "MTK", ["M3"] = "MTQ", ["METRO CUBICO"] = "MTQ",
        ["BOLSA"] = "BG", ["CAJA"] = "BX", ["PAQUETE"] = "PK", ["PAQ"] = "PK", ["JUEGO"] = "SET",
        ["GALON"] = "GLL", ["GAL"] = "GLL", ["DOCENA"] = "DZN", ["CIENTO"] = "CEN", ["MILLAR"] = "MLL",
        ["HORA"] = "HUR", ["HORAS"] = "HUR", ["HR"] = "HUR", ["DIA"] = "DAY", ["DIAS"] = "DAY", ["MES"] = "MON", ["MESES"] = "MON",
    };

    public sealed record FilaRevisada(
        int Fila, string Codigo, string Descripcion, string Unidad, decimal? Precio, bool IncluyeIgv,
        string Afectacion, string? CodigoSunat, string Resultado, string? Mensaje, decimal? PrecioAntes);

    public static void MapearImportarProductos(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/productos").WithTags("Facturación · productos");

        // =====================================================================
        // LA PLANTILLA (con los productos de la empresa, para editarlos)
        // =====================================================================
        grupo.MapGet("/plantilla", async (ContextoPlataforma db, CancellationToken ct) =>
        {
            var productos = await db.Productos.AsNoTracking()
                .OrderBy(p => p.Codigo)
                .Take(MaxFilas)
                .Select(p => new { p.Codigo, p.Descripcion, p.UnidadMedida, p.PrecioUnitario, p.IncluyeIgv, p.TipoAfectacion, p.CodigoSunat })
                .ToListAsync(ct);

            var hoja = new HojaExcel("Productos") { Horizontal = true };
            hoja.Titulos.Add("Productos para importar");
            hoja.Titulos.Add("Llena una fila por producto. No cambies los títulos de las columnas. Lo que no esté aquí no se toca.");
            hoja.Columnas.Add(new("Código", TipoColumna.Texto, 16));
            hoja.Columnas.Add(new("Descripción", TipoColumna.Texto, 44));
            hoja.Columnas.Add(new("Unidad", TipoColumna.Texto, 10));
            hoja.Columnas.Add(new("Precio", TipoColumna.Cantidad, 12));
            hoja.Columnas.Add(new("Incluye IGV", TipoColumna.Texto, 12));
            hoja.Columnas.Add(new("Afectación", TipoColumna.Texto, 12));
            hoja.Columnas.Add(new("Código SUNAT", TipoColumna.Texto, 14));

            if (productos.Count == 0)
            {
                hoja.Filas.Add(["P001", "PRODUCTO DE EJEMPLO (bórralo)", "NIU", 10.50m, "SI", "10", null]);
                hoja.Filas.Add(["S001", "SERVICIO DE EJEMPLO (bórralo)", "ZZ", 100m, "SI", "10", null]);
            }
            else
                foreach (var p in productos)
                    hoja.Filas.Add([p.Codigo, p.Descripcion, p.UnidadMedida, p.PrecioUnitario, p.IncluyeIgv ? "SI" : "NO",
                                    p.TipoAfectacion, p.CodigoSunat]);

            var ayuda = new HojaExcel("Ayuda") { ConFiltro = false };
            ayuda.Titulos.Add("Qué va en cada columna");
            ayuda.Columnas.Add(new("Columna", TipoColumna.Texto, 16));
            ayuda.Columnas.Add(new("Qué poner", TipoColumna.Texto, 90));
            ayuda.Filas.Add(["Código", "Tu código del producto (obligatorio, hasta 30). Si ya existe, se actualiza ese producto."]);
            ayuda.Filas.Add(["Descripción", "Lo que sale impreso en el comprobante (obligatorio)."]);
            ayuda.Filas.Add(["Unidad", "El código de la lista de abajo (NIU unidad, ZZ servicio, KGM kilo…). Vacío = NIU."]);
            ayuda.Filas.Add(["Precio", "El precio de venta. Con punto o coma decimal."]);
            ayuda.Filas.Add(["Incluye IGV", "SI si el precio ya tiene IGV; NO si es sin IGV. Vacío = SI."]);
            ayuda.Filas.Add(["Afectación", "10 gravado, 20 exonerado, 30 inafecto, 40 exportación, 17 IVAP. Vacío = 10."]);
            ayuda.Filas.Add(["Código SUNAT", "Opcional: 8 dígitos del catálogo 25 (UNSPSC). Si no lo sabes, déjalo vacío."]);
            ayuda.Filas.Add([new FilaSeccion("Unidades")]);
            foreach (var u in EndpointsProductos.Unidades)
                ayuda.Filas.Add([u.Key, u.Value]);
            ayuda.Filas.Add([new FilaSeccion("Afectaciones del IGV")]);
            foreach (var a in EndpointsProductos.Afectaciones)
                ayuda.Filas.Add([a.Key, a.Value]);

            return Results.File(ExcelSimple.Crear([hoja, ayuda]), ExcelSimple.TipoMime, "plantilla-productos.xlsx");
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("La plantilla de Excel para importar productos (con los de mi empresa)");


        // =====================================================================
        // REVISAR (simular=true) O IMPORTAR. El archivo va tal cual en el cuerpo.
        // =====================================================================
        grupo.MapPost("/importar", async (
            HttpContext http, bool? simular, bool? actualizar,
            ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            byte[] contenido;
            using (var memoria = new MemoryStream())
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    memoria.Write(buffer, 0, n);
                    if (memoria.Length > MaxBytes)
                        return Results.BadRequest(new { error = "El archivo pasa de 10 MB. Pártelo en varios." });
                }
                contenido = memoria.ToArray();
            }

            List<LectorExcel.Fila> filas;
            try { filas = LectorExcel.Leer(contenido, MaxFilas + 20); }
            catch (LectorExcel.Problema e) { return Results.BadRequest(new { error = e.Message }); }

            var cabecera = filas.FindIndex(f => Normalizar(f.Celda(0)) == "CODIGO");
            if (cabecera < 0)
                return Results.BadRequest(new
                {
                    error = "No encuentro la fila de títulos (la que empieza con «Código»).",
                    detalle = "Usa la plantilla: baja «Bajar plantilla .xlsx» y llénala sin cambiar los títulos."
                });

            var columnas = Columnas(filas[cabecera]);
            if (columnas.Descripcion < 0)
                return Results.BadRequest(new { error = "Falta la columna «Descripción». Usa la plantilla sin cambiar los títulos." });

            var existentes = await db.Productos.ToListAsync(ct);
            var porCodigo = existentes.GroupBy(p => p.Codigo.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            var actualizarExistentes = actualizar != false;

            var revisadas = new List<FilaRevisada>();
            var vistos = new HashSet<string>();
            foreach (var f in filas.Skip(cabecera + 1).Where(f => !f.Vacia))
            {
                var r = Revisar(f, columnas, vistos, porCodigo, actualizarExistentes);
                revisadas.Add(r);
                if (r.Codigo.Length > 0) vistos.Add(r.Codigo.ToLowerInvariant());
            }

            if (revisadas.Count == 0)
                return Results.BadRequest(new { error = "El archivo no tiene productos debajo de los títulos." });
            if (revisadas.Count > MaxFilas)
                return Results.BadRequest(new { error = $"Son más de {MaxFilas:N0} productos. Pártelo en varios archivos." });

            var nuevos = revisadas.Count(r => r.Resultado == "nuevo");
            var cambian = revisadas.Count(r => r.Resultado == "actualiza");
            var iguales = revisadas.Count(r => r.Resultado is "igual" or "existe");
            var errores = revisadas.Count(r => r.Resultado == "error");

            if (simular != false)
                return Results.Ok(new { nuevos, actualizan = cambian, iguales, errores, total = revisadas.Count, filas = revisadas });

            // --- IMPORTAR: en una sola transacción, todo o nada -----------------
            foreach (var r in revisadas.Where(r => r.Resultado is "nuevo" or "actualiza"))
            {
                if (r.Resultado == "nuevo")
                {
                    db.Productos.Add(new Producto
                    {
                        EmpresaId      = ctx.EmpresaId!.Value,
                        Codigo         = r.Codigo,
                        Descripcion    = r.Descripcion,
                        UnidadMedida   = r.Unidad,
                        TipoAfectacion = r.Afectacion,
                        PrecioUnitario = r.Precio ?? 0,
                        IncluyeIgv     = r.IncluyeIgv,
                        CodigoSunat    = r.CodigoSunat
                    });
                }
                else
                {
                    var p = porCodigo[r.Codigo.ToLowerInvariant()];
                    p.Descripcion    = r.Descripcion;
                    p.UnidadMedida   = r.Unidad;
                    p.TipoAfectacion = r.Afectacion;
                    p.PrecioUnitario = r.Precio ?? p.PrecioUnitario;
                    p.IncluyeIgv     = r.IncluyeIgv;
                    p.CodigoSunat    = r.CodigoSunat;
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new
                {
                    error = "No se importó nada: otro usuario cambió los productos a la vez. Vuelve a subir el archivo.",
                });
            }

            return Results.Ok(new
            {
                nuevos, actualizan = cambian, iguales, errores, total = revisadas.Count,
                mensaje = $"Listo: {nuevos} producto(s) nuevo(s) y {cambian} actualizado(s)." +
                          (errores > 0 ? $" {errores} fila(s) con error no entraron." : "")
            });
        })
        .RequierePermiso(Permisos.Facturacion.ProductosGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Revisar (simular) o importar productos desde un Excel (.xlsx) o .csv");
    }


    // =========================================================================

    private sealed record Cols(int Codigo, int Descripcion, int Unidad, int Precio, int Igv, int Afectacion, int Sunat);

    private static Cols Columnas(LectorExcel.Fila cab)
    {
        int Buscar(Func<string, bool> es) =>
            cab.Celdas.Where(c => es(Normalizar(c.Value))).Select(c => c.Key).DefaultIfEmpty(-1).First();

        return new Cols(
            Codigo:      Buscar(t => t == "CODIGO"),
            Descripcion: Buscar(t => t.StartsWith("DESCRIPCION") || t == "NOMBRE" || t == "PRODUCTO"),
            Unidad:      Buscar(t => t.StartsWith("UNIDAD")),
            Precio:      Buscar(t => t.StartsWith("PRECIO")),
            Igv:         Buscar(t => t.Contains("IGV") && !t.StartsWith("AFECT")),
            Afectacion:  Buscar(t => t.StartsWith("AFECTACION") || t == "TIPO DE AFECTACION"),
            Sunat:       Buscar(t => t.Contains("SUNAT")));
    }

    private static FilaRevisada Revisar(
        LectorExcel.Fila f, Cols c, HashSet<string> vistos, Dictionary<string, Producto> existentes, bool actualizar)
    {
        string Celda(int i) => i < 0 ? "" : f.Celda(i);

        var codigo = Celda(c.Codigo);
        var descripcion = Celda(c.Descripcion);
        var unidadTexto = Celda(c.Unidad);
        var precioTexto = Celda(c.Precio);
        var igvTexto = Normalizar(Celda(c.Igv));
        var afectacionTexto = Normalizar(Celda(c.Afectacion));
        var sunat = Celda(c.Sunat);

        var unidad = Unidad(unidadTexto);
        var precio = precioTexto.Length == 0 ? 0m : LectorExcel.Numero(precioTexto);
        var incluyeIgv = igvTexto is "" or "SI" or "S" or "X" or "1" or "TRUE" or "VERDADERO";
        var afectacion = Afectacion(afectacionTexto);
        string? codigoSunat = string.IsNullOrWhiteSpace(sunat) ? null : sunat.Trim();

        FilaRevisada Con(string resultado, string? mensaje, decimal? antes = null) =>
            new(f.Numero, codigo, descripcion, unidad ?? unidadTexto, precio, incluyeIgv,
                afectacion ?? afectacionTexto, codigoSunat, resultado, mensaje, antes);

        if (codigo.Length == 0) return Con("error", "Falta el código.");
        if (vistos.Contains(codigo.ToLowerInvariant())) return Con("error", $"El código {codigo} ya está en otra fila del archivo.");
        if (unidad is null) return Con("error", $"La unidad «{unidadTexto}» no existe. Usa un código de la hoja Ayuda (NIU, ZZ, KGM…).");
        if (precio is null) return Con("error", $"El precio «{precioTexto}» no es un número.");
        if (igvTexto is not ("" or "SI" or "S" or "X" or "1" or "TRUE" or "VERDADERO" or "NO" or "N" or "0" or "FALSE" or "FALSO"))
            return Con("error", "En «Incluye IGV» va SI o NO.");
        if (afectacion is null) return Con("error", $"La afectación «{Celda(c.Afectacion)}» no existe. Usa 10, 20, 30, 40 o 17.");

        var peticion = new PeticionProducto(codigo, descripcion, unidad, afectacion, Math.Round(precio.Value, 10), incluyeIgv, codigoSunat);
        if (EndpointsProductos.Revisar(peticion) is { } error) return Con("error", error);

        var mensaje = unidadTexto.Length > 0 && !string.Equals(unidadTexto.Trim(), unidad, StringComparison.OrdinalIgnoreCase)
            ? $"Unidad «{unidadTexto}» → {unidad}." : null;

        if (!existentes.TryGetValue(codigo.ToLowerInvariant(), out var p))
            return Con("nuevo", mensaje);

        var igual = string.Equals(p.Descripcion, descripcion.Trim(), StringComparison.OrdinalIgnoreCase) &&   // e174: se guarda en mayúsculas
                    p.UnidadMedida == unidad && p.TipoAfectacion == afectacion &&
                    p.PrecioUnitario == precio && p.IncluyeIgv == incluyeIgv && p.CodigoSunat == codigoSunat;
        if (igual) return Con("igual", "Ya está así.");
        if (!actualizar) return Con("existe", "Ya existe: no se toca (no marcaste «actualizar»).", p.PrecioUnitario);

        var cambios = new List<string>();
        if (p.PrecioUnitario != precio) cambios.Add($"precio {p.PrecioUnitario.ToString("0.00##", CultureInfo.InvariantCulture)} → {precio.Value.ToString("0.00##", CultureInfo.InvariantCulture)}");
        if (!string.Equals(p.Descripcion, descripcion.Trim(), StringComparison.OrdinalIgnoreCase)) cambios.Add("descripción");
        if (p.UnidadMedida != unidad) cambios.Add($"unidad {p.UnidadMedida} → {unidad}");
        if (p.TipoAfectacion != afectacion) cambios.Add($"afectación {p.TipoAfectacion} → {afectacion}");
        if (p.IncluyeIgv != incluyeIgv) cambios.Add(incluyeIgv ? "ahora incluye IGV" : "ahora sin IGV");
        if (p.CodigoSunat != codigoSunat) cambios.Add("código SUNAT");

        return Con("actualiza", "Cambia: " + string.Join(", ", cambios) + "." + (mensaje is null ? "" : " " + mensaje), p.PrecioUnitario);
    }

    private static string? Unidad(string texto)
    {
        var t = Normalizar(texto);
        if (t.Length == 0) return "NIU";
        if (EndpointsProductos.Unidades.ContainsKey(t)) return t;
        if (Sinonimos.TryGetValue(t, out var s)) return s;
        var porNombre = EndpointsProductos.Unidades.FirstOrDefault(u => Normalizar(u.Value) == t).Key;
        if (porNombre is not null) return porNombre;
        // Otro código del catálogo 03 (2 o 3 letras y números): se admite, como a mano.
        return t.Length is 2 or 3 && t.All(char.IsAsciiLetterOrDigit) ? t : null;
    }

    private static string? Afectacion(string t)
    {
        if (t.Length == 0) return "10";
        if (EndpointsProductos.Afectaciones.ContainsKey(t)) return t;
        if (t.StartsWith("GRAV")) return "10";
        if (t.StartsWith("EXON")) return "20";
        if (t.StartsWith("INAF")) return "30";
        if (t.StartsWith("EXPORT")) return "40";
        if (t.StartsWith("IVAP") || t.Contains("ARROZ")) return "17";
        return null;
    }

    /// <summary>Mayúsculas, sin tildes ni espacios de más.</summary>
    private static string Normalizar(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) b.Append(ch);
        return string.Join(' ', b.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}