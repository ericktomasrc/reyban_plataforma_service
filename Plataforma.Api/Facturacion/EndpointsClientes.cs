using Microsoft.EntityFrameworkCore;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Facturacion;

public record PeticionCliente(
    string TipoDocumento,
    string NumeroDocumento,
    string RazonSocial,
    string? NombreComercial,
    string? Direccion,
    string? Ubigeo,
    string? Correo,
    string? Telefono,
    string? AfectacionHabitual,
    // 043: null = no se tocan (una pantalla vieja que no los manda no los borra).
    string? Whatsapp = null,
    bool? WhatsappActivo = null);


/// <summary>
/// A quién le factura cada empresa.
///
/// ESTO ES DE LA PLATAFORMA, NO DEL SERVICIO DE FACTURACIÓN, y la regla de oro
/// número tres del documento maestro lo dice al revés: «si aparecen tablas de
/// productos o clientes en la base del servicio, se cruzó la línea». El
/// servicio recibe un comprobante ya armado y no sabe de dónde salió el precio
/// ni quién es el comprador. Esa ignorancia es lo que lo mantiene reutilizable.
///
/// Ninguna consulta de este archivo filtra por empresa: lo hace el Row Level
/// Security. Un `SELECT` aquí solo puede devolver filas de la empresa de quien
/// pregunta, aunque el código se olvide del WHERE.
/// </summary>
public static class EndpointsClientes
{
    /// <summary>
    /// Catálogo 06 de SUNAT, los que admite la tabla.
    ///
    /// SE VALIDA CONTRA ESTA LISTA Y NO SOLO CONTRA EL CHECK de la base porque
    /// el error de PostgreSQL —«viola la restricción ck_clientes_tipo_doc»— no
    /// le dice nada a quien está dando de alta un cliente.
    /// </summary>
    private static readonly string[] TiposDocumento =
        ["0", "1", "4", "6", "7", "A", "B", "C", "D", "E", "F", "G"];

    public static void MapearClientes(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/facturacion/clientes")
                         .WithTags("Facturación · clientes");


        // =====================================================================
        // BUSCAR
        //
        // UN SOLO CAMPO DE BÚSQUEDA, no un formulario con filtros. Quien da de
        // alta una factura escribe lo que recuerda —parte del RUC o parte del
        // nombre— y espera encontrarlo. Partirlo en dos campos obliga a decidir
        // en cuál escribir antes de buscar.
        // =====================================================================
        grupo.MapGet("/", async (
            string? q, bool? incluirInactivos,
            ContextoPlataforma db, CancellationToken ct) =>
        {
            var consulta = incluirInactivos == true
                ? db.Clientes.IgnoreQueryFilters()
                : db.Clientes;

            consulta = consulta.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(q))
            {
                var texto = q.Trim().ToLowerInvariant();

                consulta = consulta.Where(c =>
                    c.NumeroDocumento.Contains(texto) ||
                    c.RazonSocial.ToLower().Contains(texto) ||
                    (c.NombreComercial != null && c.NombreComercial.ToLower().Contains(texto)));
            }

            var clientes = await consulta
                .OrderBy(c => c.RazonSocial)
                .Take(200)
                .Select(c => new
                {
                    c.Id, c.TipoDocumento, c.NumeroDocumento, c.RazonSocial,
                    c.NombreComercial, c.Direccion, c.Ubigeo, c.Correo, c.Telefono,
                    c.AfectacionHabitual, c.Activo,
                    c.Whatsapp, c.WhatsappActivo   // 043
                })
                .ToListAsync(ct);

            return Results.Ok(clientes);
        })
        .RequierePermiso(Permisos.Facturacion.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Buscar clientes de mi empresa");


        // =====================================================================
        grupo.MapPost("/", async (
            PeticionCliente p, ContextoPlataforma db, ContextoPeticion ctx,
            CancellationToken ct) =>
        {
            if (Revisar(p) is { } error) return Results.BadRequest(new { error });

            var tipo   = p.TipoDocumento.Trim().ToUpperInvariant();
            var numero = p.NumeroDocumento.Trim();

            // El índice único de la base es sobre (empresa, tipo, número) y
            // solo mira los activos. Se comprueba aquí para dar un mensaje que
            // se entienda, no para sustituirlo: dos altas simultáneas seguirían
            // chocando contra el índice, y eso está bien.
            var repetido = await db.Clientes.AnyAsync(
                c => c.TipoDocumento == tipo && c.NumeroDocumento == numero, ct);

            if (repetido)
                return Results.Conflict(new { error = $"Ya tienes un cliente con el documento {numero}." });

            var cliente = new Cliente
            {
                EmpresaId       = ctx.EmpresaId!.Value,
                TipoDocumento   = tipo,
                NumeroDocumento = numero,
                RazonSocial     = p.RazonSocial.Trim(),
                NombreComercial = Vacio(p.NombreComercial),
                Direccion       = Vacio(p.Direccion),
                Ubigeo          = Vacio(p.Ubigeo),
                Correo          = Vacio(p.Correo),
                Telefono        = Vacio(p.Telefono),
                AfectacionHabitual = Vacio(p.AfectacionHabitual)
            };
            PonerWhatsapp(cliente, p);

            db.Clientes.Add(cliente);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/facturacion/clientes/{cliente.Id}",
                new { cliente.Id, cliente.RazonSocial });
        })
        .RequierePermiso(Permisos.Facturacion.ClientesGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Dar de alta un cliente");


        // =====================================================================
        grupo.MapPut("/{id:guid}", async (
            Guid id, PeticionCliente p, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (Revisar(p) is { } error) return Results.BadRequest(new { error });

            var cliente = await db.Clientes.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cliente is null) return Results.NotFound();

            var tipo   = p.TipoDocumento.Trim().ToUpperInvariant();
            var numero = p.NumeroDocumento.Trim();

            if (tipo != cliente.TipoDocumento || numero != cliente.NumeroDocumento)
            {
                var repetido = await db.Clientes.AnyAsync(
                    c => c.Id != id && c.TipoDocumento == tipo && c.NumeroDocumento == numero, ct);

                if (repetido)
                    return Results.Conflict(new { error = $"Ya tienes otro cliente con el documento {numero}." });
            }

            // SE DEJA CAMBIAR EL DOCUMENTO, y conviene saber por qué: un RUC
            // mal tecleado es el error más común del alta, y los comprobantes
            // ya emitidos no cambian — llevan copiados los datos del receptor
            // tal como estaban ese día, que es lo correcto tributariamente.
            cliente.TipoDocumento   = tipo;
            cliente.NumeroDocumento = numero;
            cliente.RazonSocial     = p.RazonSocial.Trim();
            cliente.NombreComercial = Vacio(p.NombreComercial);
            cliente.Direccion       = Vacio(p.Direccion);
            cliente.Ubigeo          = Vacio(p.Ubigeo);
            cliente.Correo          = Vacio(p.Correo);
            cliente.Telefono        = Vacio(p.Telefono);
            cliente.AfectacionHabitual = Vacio(p.AfectacionHabitual);
            PonerWhatsapp(cliente, p);

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = "Cliente actualizado." });
        })
        .RequierePermiso(Permisos.Facturacion.ClientesGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Editar un cliente");


        // =====================================================================
        // DESACTIVAR Y REACTIVAR
        //
        // NO SE BORRA NUNCA. Un cliente con comprobantes emitidos es parte del
        // historial tributario, y borrarlo dejaría facturas apuntando a un
        // hueco. Desactivado deja de salir en el buscador y de ocupar su
        // documento en el índice único, que es todo lo que hace falta.
        // =====================================================================
        grupo.MapPost("/{id:guid}/desactivar", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var cliente = await db.Clientes.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cliente is null) return Results.NotFound();

            cliente.Activo = false;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { mensaje = "Cliente desactivado. Sus comprobantes no se tocan." });
        })
        .RequierePermiso(Permisos.Facturacion.ClientesGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Desactivar un cliente");


        grupo.MapPost("/{id:guid}/reactivar", async (
            Guid id, ContextoPlataforma db, CancellationToken ct) =>
        {
            var cliente = await db.Clientes.IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cliente is null) return Results.NotFound();

            // Mientras estuvo desactivado, su documento quedó libre y alguien
            // pudo darlo de alta otra vez. Reactivarlo sin mirar chocaría
            // contra el índice único con un error ilegible.
            var ocupado = await db.Clientes.AnyAsync(
                c => c.Id != id &&
                     c.TipoDocumento == cliente.TipoDocumento &&
                     c.NumeroDocumento == cliente.NumeroDocumento, ct);

            if (ocupado)
                return Results.Conflict(new
                {
                    error = $"Mientras estaba desactivado se dio de alta otro cliente con el documento " +
                            $"{cliente.NumeroDocumento}. Desactiva ese primero, o cambia el documento de este."
                });

            cliente.Activo = true;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { mensaje = "Cliente reactivado." });
        })
        .RequierePermiso(Permisos.Facturacion.ClientesGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Reactivar un cliente");
    }


    // =========================================================================

    private static string? Revisar(PeticionCliente p)
    {
        var tipo   = (p.TipoDocumento ?? "").Trim().ToUpperInvariant();
        var numero = (p.NumeroDocumento ?? "").Trim();

        if (!TiposDocumento.Contains(tipo))
            return "Ese tipo de documento no existe en el catálogo 06 de SUNAT.";

        if (numero.Length == 0)
            return "Falta el número de documento.";

        if (numero.Any(c => !char.IsLetterOrDigit(c)))
            return "El número va sin guiones ni espacios.";

        if (string.IsNullOrWhiteSpace(p.RazonSocial))
            return "Falta la razón social.";

        // EL CORREO HACE FALTA, y no es un capricho del formulario: es a donde
        // se le manda el comprobante. Un cliente sin correo es un comprobante
        // que se emite, se firma, llega a SUNAT... y no llega a quien lo pagó.
        //
        // Se comprueba también aquí y no solo en la pantalla porque el endpoint
        // es público para cualquiera con permiso: quien llame desde fuera del
        // navegador se salta el `required` del formulario.
        //
        // 17 OCT: OBLIGATORIO SOLO CON RUC. En la caja de un restaurante o una
        // tienda, quien pide boleta con DNI casi nunca da su correo, y exigirlo
        // llevaría a inventar uno. Si se escribe, tiene que tener forma.
        if (tipo == "6" && string.IsNullOrWhiteSpace(p.Correo))
            return "Falta el correo: es a donde se le manda la factura.";

        if (!string.IsNullOrWhiteSpace(p.Correo) && !CorreoConForma(p.Correo.Trim()))
            return "Ese correo no tiene forma de correo. Revísalo.";

        // EL DÍGITO VERIFICADOR DEL RUC SE COMPRUEBA AQUÍ, no cuando se emita.
        //
        // Un RUC mal tecleado que se descubre al facturar ya costó el tiempo de
        // armar el comprobante, y el rechazo llega del servicio con un mensaje
        // que habla del receptor y no del dedazo. Aquí se ataja en el momento y
        // en la pantalla donde se escribió.
        if (tipo == "6")
        {
            if (numero.Length != 11 || !numero.All(char.IsDigit))
                return "Un RUC son 11 dígitos.";

            // EL PREFIJO SE MIRA ANTES QUE EL DÍGITO VERIFICADOR porque explica
            // mejor lo que pasa. Un número inventado —12345678910— falla las dos
            // comprobaciones, y decir «el dígito verificador no cuadra» manda a
            // buscar una cifra cambiada en un número que nunca fue un RUC.
            if (!PrefijosRuc.Contains(numero[..2]))
                return "Un RUC empieza por 10, 15, 16, 17 o 20. Revisa las dos primeras cifras.";

            if (!RucValido(numero))
                return "Ese RUC no es válido: el dígito verificador no cuadra. Revisa si hay una cifra cambiada.";
        }

        if (tipo == "1" && (numero.Length != 8 || !numero.All(char.IsDigit)))
            return "Un DNI son 8 dígitos.";

        if (p.Ubigeo is { Length: > 0 } u && (u.Trim().Length != 6 || !u.Trim().All(char.IsDigit)))
            return "El ubigeo son 6 dígitos.";

        // 043: el WhatsApp, si se escribe, tiene que tener forma de número.
        if (!string.IsNullOrWhiteSpace(p.Whatsapp) && Whatsapp.Normalizar(p.Whatsapp) is null)
            return "Ese WhatsApp no tiene forma de número. Escribe el celular: 987 654 321 (o con el código de país, +51…).";

        if (p.WhatsappActivo == true && string.IsNullOrWhiteSpace(p.Whatsapp))
            return "Para enviarle los comprobantes por WhatsApp, escribe su número.";

        // SE VALIDA CONTRA EL MISMO CATÁLOGO QUE USA UN PRODUCTO. Si aquí
        // entrara un código que la pantalla del comprobante no conoce, la línea
        // se rellenaría con una afectación que el desplegable no sabe enseñar.
        if (p.AfectacionHabitual is { Length: > 0 } a &&
            !EndpointsProductos.Afectaciones.ContainsKey(a.Trim()))
            return "Esa afectación del IGV no existe en el catálogo 07 de SUNAT.";

        return null;
    }


    /// <summary>
    /// Los dos primeros dígitos con los que SUNAT empieza un RUC.
    ///
    /// 10 y 15 son persona natural, 17 sucesión indivisa, 20 persona jurídica, y
    /// 16 no gubernamental. No hay más: cualquier otro comienzo es un número que
    /// no es un RUC, no un RUC mal escrito.
    /// </summary>
    private static readonly string[] PrefijosRuc = ["10", "15", "16", "17", "20"];


    /// <summary>
    /// El dígito verificador del RUC: módulo 11 con los pesos 5,4,3,2,7,6,5,4,3,2.
    ///
    /// Es aritmética fija de SUNAT y no cambia. No comprueba que el RUC EXISTA
    /// —eso necesita el padrón— pero sí ataja el error que de verdad pasa, que
    /// es la cifra cambiada de sitio.
    /// </summary>
    private static bool RucValido(string ruc)
    {
        int[] pesos = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];

        var suma = 0;
        for (var i = 0; i < 10; i++)
            suma += (ruc[i] - '0') * pesos[i];

        var resto = 11 - suma % 11;
        if (resto == 10) resto = 0;
        if (resto == 11) resto = 1;

        return resto == ruc[10] - '0';
    }

    /// <summary>
    /// ¿Tiene FORMA de correo? No comprueba que exista.
    ///
    /// DELIBERADAMENTE FLOJA. La regla completa de un correo válido es larga y
    /// rechaza direcciones raras pero legítimas; lo que de verdad pasa aquí es
    /// el espacio de más o la coma en vez del punto. Lo único que demuestra que
    /// un correo existe es mandarle algo.
    /// </summary>
    private static bool CorreoConForma(string correo)
    {
        var arroba = correo.IndexOf('@');
        if (arroba <= 0 || arroba != correo.LastIndexOf('@')) return false;

        var dominio = correo[(arroba + 1)..];
        return dominio.Contains('.')
            && !dominio.StartsWith('.')
            && !dominio.EndsWith('.')
            && !correo.Any(char.IsWhiteSpace);
    }

    /// <summary>043: el WhatsApp y su casilla. Lo que no se mandó (null) se deja como estaba.</summary>
    private static void PonerWhatsapp(Cliente c, PeticionCliente p)
    {
        if (p.Whatsapp is not null) c.Whatsapp = Whatsapp.Normalizar(p.Whatsapp);
        if (p.WhatsappActivo is { } activo) c.WhatsappActivo = activo;
        if (c.Whatsapp is null) c.WhatsappActivo = false;
    }

    private static string? Vacio(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}