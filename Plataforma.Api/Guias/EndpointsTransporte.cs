using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Autorizacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Guias;

public sealed record PeticionVehiculo(string? Placa, string? Descripcion, string? Tuc, string? Autorizacion, string? Entidad);
public sealed record PeticionConductor(string? TipoDoc, string? NumeroDoc, string? Nombres, string? Apellidos, string? Licencia, Guid? UsuarioId);
public sealed record PeticionTransportista(string? Ruc, string? RazonSocial, string? Mtc);


/// <summary>
/// 050 — EL TRANSPORTE GUARDADO DE CADA EMPRESA (vehículos, conductores y
/// empresas de transporte) y la configuración de guías.
///
///   GET  /api/guias/transporte                         las tres listas (con las desactivadas)
///   GET  /api/guias/transporte/personal                el personal con DNI (para elegir conductor)
///   POST/PUT /api/guias/transporte/vehiculos[/{id}]     crear / corregir
///   POST/PUT /api/guias/transporte/conductores[/{id}]
///   POST/PUT /api/guias/transporte/transportistas[/{id}]
///   POST /api/guias/transporte/{clase}/{id}/desactivar | reactivar
///   GET  /api/guias/configuracion                      transporta carga, MTC, credenciales de guías
///   PUT  /api/guias/configuracion/transporte           transporta carga y registro MTC
///   PUT  /api/guias/configuracion/gre                  credenciales de guías (al servicio, cifradas allá)
///
/// SE GUARDAN SOLOS AL EMITIR (RecordarAsync): quien emite no tiene que
/// pasar por aquí antes. Esta pantalla es para corregir o desactivar.
/// NADA SE BORRA.
/// </summary>
public static class EndpointsTransporte
{
    private static readonly Regex Placa = new("^[A-Z0-9]{6,8}$", RegexOptions.Compiled);   // SUNAT 2567: 6 a 8 letras y números
    private static readonly Regex Ruc = new("^(10|15|17|20)[0-9]{9}$", RegexOptions.Compiled);
    private static readonly Regex Licencia = new("^[A-Z0-9-]{9,10}$", RegexOptions.Compiled);
    private static readonly Regex Mtc = new("^[A-Z0-9-]{3,20}$", RegexOptions.Compiled);
    private const string PuntoGuardado = "recordar_transporte";

    public static void MapearTransporte(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/guias").WithTags("Guías de remisión · transporte");

        // =====================================================================
        // LAS TRES LISTAS
        // =====================================================================
        grupo.MapGet("/transporte", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });

            var vehiculos = await db.Vehiculos.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.EmpresaId == empresaId)
                .OrderByDescending(x => x.Activo).ThenByDescending(x => x.UltimoUso).ThenBy(x => x.Placa)
                .Select(x => new { x.Id, x.Placa, x.Descripcion, x.Tuc, x.Autorizacion, entidad = x.EntidadAutorizadora, x.Usos, x.UltimoUso, x.Activo })
                .ToListAsync(ct);
            var conductores = await db.Conductores.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.EmpresaId == empresaId)
                .OrderByDescending(x => x.Activo).ThenByDescending(x => x.UltimoUso).ThenBy(x => x.Apellidos)
                .Select(x => new { x.Id, x.TipoDoc, x.NumeroDoc, x.Nombres, x.Apellidos, x.Licencia, x.UsuarioId, x.Usos, x.UltimoUso, x.Activo })
                .ToListAsync(ct);
            var transportistas = await db.Transportistas.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.EmpresaId == empresaId)
                .OrderByDescending(x => x.Activo).ThenByDescending(x => x.UltimoUso).ThenBy(x => x.RazonSocial)
                .Select(x => new { x.Id, x.Ruc, x.RazonSocial, x.Mtc, x.Usos, x.UltimoUso, x.Activo })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                vehiculos, conductores, transportistas,
                puedeGestionar = ctx.EsSuperAdmin || ctx.Permisos.Contains(Permisos.Guias.Transporte)
            });
        })
        .RequierePermiso(Permisos.Guias.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Vehículos, conductores y empresas de transporte guardados");


        // El personal con DNI: para elegir conductor sin volver a escribirlo.
        grupo.MapGet("/transporte/personal", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.Ok(Array.Empty<object>());
            var gente = await db.Usuarios.AsNoTracking()
                .Where(u => u.EmpresaId == empresaId && u.NumeroDocumento != null && u.TipoDocumento != null)
                .OrderBy(u => u.ApellidoPaterno).ThenBy(u => u.Nombres)
                .Select(u => new
                {
                    usuarioId = u.Id, tipoDoc = u.TipoDocumento, numeroDoc = u.NumeroDocumento,
                    nombres = u.Nombres ?? u.Nombre,
                    apellidos = ((u.ApellidoPaterno ?? "") + " " + (u.ApellidoMaterno ?? "")).Trim(),
                    u.Cargo
                })
                .ToListAsync(ct);
            return Results.Ok(gente);
        })
        .RequierePermiso(Permisos.Guias.Emitir)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("El personal con documento, para elegir conductor");


        // =====================================================================
        // VEHÍCULOS
        // =====================================================================
        grupo.MapPost("/transporte/vehiculos", async (PeticionVehiculo p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            await GuardarVehiculoAsync(null, p, db, ctx, ct))
            .RequierePermiso(Permisos.Guias.Transporte).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Agregar un vehículo");

        grupo.MapPut("/transporte/vehiculos/{id:guid}", async (Guid id, PeticionVehiculo p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            await GuardarVehiculoAsync(id, p, db, ctx, ct))
            .RequierePermiso(Permisos.Guias.Transporte).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Corregir un vehículo");

        // =====================================================================
        // CONDUCTORES
        // =====================================================================
        grupo.MapPost("/transporte/conductores", async (PeticionConductor p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            await GuardarConductorAsync(null, p, db, ctx, ct))
            .RequierePermiso(Permisos.Guias.Transporte).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Agregar un conductor");

        grupo.MapPut("/transporte/conductores/{id:guid}", async (Guid id, PeticionConductor p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            await GuardarConductorAsync(id, p, db, ctx, ct))
            .RequierePermiso(Permisos.Guias.Transporte).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Corregir un conductor");

        // =====================================================================
        // EMPRESAS DE TRANSPORTE
        // =====================================================================
        grupo.MapPost("/transporte/transportistas", async (PeticionTransportista p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            await GuardarTransportistaAsync(null, p, db, ctx, ct))
            .RequierePermiso(Permisos.Guias.Transporte).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Agregar una empresa de transporte");

        grupo.MapPut("/transporte/transportistas/{id:guid}", async (Guid id, PeticionTransportista p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
            await GuardarTransportistaAsync(id, p, db, ctx, ct))
            .RequierePermiso(Permisos.Guias.Transporte).RequiereModulo(Modulos.Facturacion)
            .WithSummary("Corregir una empresa de transporte");

        // =====================================================================
        // DESACTIVAR / REACTIVAR (nada se borra)
        // =====================================================================
        grupo.MapPost("/transporte/{clase}/{id:guid}/{accion}", async (
            string clase, Guid id, string accion, ContextoPlataforma db, CancellationToken ct) =>
        {
            if (accion is not ("desactivar" or "reactivar")) return Results.NotFound();
            var activo = accion == "reactivar";

            EntidadAuditada? fila = clase switch
            {
                "vehiculos"      => await db.Vehiculos.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct),
                "conductores"    => await db.Conductores.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct),
                "transportistas" => await db.Transportistas.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct),
                _ => null
            };
            if (fila is null) return Results.NotFound(new { error = "No se encontró." });

            fila.Activo = activo;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                mensaje = activo ? "Reactivado: vuelve a salir al emitir." : "Desactivado: ya no sale al emitir. Las guías que lo usaron no cambian."
            });
        })
        .RequierePermiso(Permisos.Guias.Transporte)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Desactivar o reactivar un vehículo, conductor o empresa de transporte");


        // =====================================================================
        // CONFIGURACIÓN DE GUÍAS
        // =====================================================================
        grupo.MapGet("/configuracion", async (
            ContextoPlataforma db, ContextoPeticion ctx, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var e = await db.Empresas.AsNoTracking().FirstAsync(x => x.Id == empresaId, ct);

            var gre = await facturacion.ConfigurarAsync<GreDelServicio>(HttpMethod.Get, "v1/configuracion/gre", ct: ct);

            return Results.Ok(new
            {
                transportaCarga = e.TransportaCarga,
                registroMtc = e.RegistroMtc,
                ambiente = e.Ambiente == "produccion" ? "produccion" : "beta",
                gre = gre.Exito && gre.Datos is { } g
                    ? new { leido = true, habilitado = g.Habilitado, clientId = g.ClientId, tieneSecreto = g.TieneSecreto }
                    : new { leido = false, habilitado = false, clientId = (string?)null, tieneSecreto = false },
                puedeGestionar = ctx.EsSuperAdmin || ctx.Permisos.Contains(Permisos.Facturacion.CredencialesGestionar)
            });
        })
        .RequierePermiso(Permisos.Guias.Ver)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Configuración de guías de mi empresa");

        grupo.MapPut("/configuracion/transporte", async (
            PeticionTransporteEmpresa p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
            var mtc = string.IsNullOrWhiteSpace(p.RegistroMtc) ? null : p.RegistroMtc.Trim().ToUpperInvariant();
            if (mtc is not null && !Mtc.IsMatch(mtc))
                return Results.BadRequest(new { error = "El registro MTC son letras y números (3 a 20). Ej.: 1543210CNG." });
            if (p.TransportaCarga && mtc is null)
                return Results.BadRequest(new { error = "Para la guía del transportista hace falta el registro MTC de tu empresa." });

            var e = await db.Empresas.FirstAsync(x => x.Id == empresaId, ct);
            e.TransportaCarga = p.TransportaCarga;
            e.RegistroMtc = mtc;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mensaje = p.TransportaCarga ? "Listo: ya puedes emitir la guía del transportista." : "Guardado." });
        })
        .RequierePermiso(Permisos.Facturacion.CredencialesGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Si mi empresa transporta carga ajena, y su registro MTC");

        grupo.MapPut("/configuracion/gre", async (
            PeticionGre p, ClienteFacturacion facturacion, CancellationToken ct) =>
        {
            var id = p.ClientId?.Trim() ?? "";
            var secreto = p.ClientSecret?.Trim() ?? "";
            if (id.Length < 8) return Results.BadRequest(new { error = "Pega el ID que te dio SOL (Credenciales de API SUNAT)." });
            if (secreto.Length < 8) return Results.BadRequest(new { error = "Pega la clave que te dio SOL (solo se ve una vez al registrarla)." });

            // El secreto no se guarda aquí: va al servicio, que lo cifra. Tampoco se registra.
            var r = await facturacion.ConfigurarAsync<JsonElement>(HttpMethod.Put, "v1/configuracion/gre",
                new { clientId = id, clientSecret = secreto }, ct: ct);
            if (!r.Exito)
                return Results.Json(new { error = $"{r.Error} {r.Detalle}".Trim() },
                                    statusCode: r.Estado == 400 ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);

            return Results.Ok(new { mensaje = "Credenciales de guías guardadas (cifradas). Ya puedes emitir guías en SUNAT real." });
        })
        .RequierePermiso(Permisos.Facturacion.CredencialesGestionar)
        .RequiereModulo(Modulos.Facturacion)
        .WithSummary("Guardar las credenciales de guías (ID y clave de SOL)");
    }


    // =========================================================================
    // GUARDAR A MANO
    // =========================================================================

    private static async Task<IResult> GuardarVehiculoAsync(
        Guid? id, PeticionVehiculo p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
        var placa = EndpointsGuias.PlacaLimpia(p.Placa) ?? "";
        if (!Placa.IsMatch(placa)) return Results.BadRequest(new { error = "La placa son 6 a 8 letras y números, sin guion. Ej.: ABC123." });
        if (!string.IsNullOrWhiteSpace(p.Autorizacion) && string.IsNullOrWhiteSpace(p.Entidad))
            return Results.BadRequest(new { error = "Con autorización especial, elige qué entidad la dio." });
        if (EndpointsGuias.ProblemaTuc(p.Tuc) is { } malTuc) return Results.BadRequest(new { error = malTuc });   // 24 oct

        var otra = await db.Vehiculos.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.EmpresaId == empresaId && x.Placa == placa, ct);
        Vehiculo v;
        if (id is { } i)
        {
            var hallado = await db.Vehiculos.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == i && x.EmpresaId == empresaId, ct);
            if (hallado is null) return Results.NotFound(new { error = "No se encontró." });
            v = hallado;
            if (otra is not null && otra.Id != i) return Results.Conflict(new { error = $"La placa {placa} ya está guardada." });
        }
        else
        {
            if (otra is not null) return Results.Conflict(new { error = $"La placa {placa} ya está guardada{(otra.Activo ? "" : " (desactivada: reactívala)")}." });
            v = new Vehiculo { EmpresaId = empresaId };
            db.Vehiculos.Add(v);
        }

        v.Placa = placa;
        v.Descripcion = EndpointsGuias.Limpio(p.Descripcion, 80);
        v.Tuc = EndpointsGuias.TucLimpio(p.Tuc);
        v.Autorizacion = EndpointsGuias.Limpio(p.Autorizacion, 50);
        v.EntidadAutorizadora = v.Autorizacion is null ? null : EndpointsGuias.Limpio(p.Entidad, 2);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { v.Id, mensaje = $"Vehículo {placa} guardado." });
    }

    private static async Task<IResult> GuardarConductorAsync(
        Guid? id, PeticionConductor p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
        var tipo = p.TipoDoc is "4" or "7" ? p.TipoDoc : "1";
        var doc = (p.NumeroDoc ?? "").Trim().ToUpperInvariant();
        var licencia = (p.Licencia ?? "").Trim().ToUpperInvariant();
        if (tipo == "1" ? !Regex.IsMatch(doc, "^[0-9]{8}$") : !Regex.IsMatch(doc, "^[A-Z0-9]{8,15}$"))
            return Results.BadRequest(new { error = tipo == "1" ? "El DNI son 8 dígitos." : "El documento no es válido." });
        if (string.IsNullOrWhiteSpace(p.Nombres) || string.IsNullOrWhiteSpace(p.Apellidos))
            return Results.BadRequest(new { error = "Escribe los nombres y apellidos del conductor." });
        if (!Licencia.IsMatch(licencia))
            return Results.BadRequest(new { error = "La licencia son 9 o 10 caracteres, como figura en el brevete. Ej.: Q41025874." });

        var otro = await db.Conductores.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.EmpresaId == empresaId && x.TipoDoc == tipo && x.NumeroDoc == doc, ct);
        Conductor c;
        if (id is { } i)
        {
            var hallado = await db.Conductores.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == i && x.EmpresaId == empresaId, ct);
            if (hallado is null) return Results.NotFound(new { error = "No se encontró." });
            c = hallado;
            if (otro is not null && otro.Id != i) return Results.Conflict(new { error = $"El conductor con documento {doc} ya está guardado." });
        }
        else
        {
            if (otro is not null) return Results.Conflict(new { error = $"El conductor con documento {doc} ya está guardado{(otro.Activo ? "" : " (desactivado: reactívalo)")}." });
            c = new Conductor { EmpresaId = empresaId };
            db.Conductores.Add(c);
        }

        c.TipoDoc = tipo;
        c.NumeroDoc = doc;
        c.Nombres = p.Nombres!.Trim();
        c.Apellidos = p.Apellidos!.Trim();
        c.Licencia = licencia;
        c.UsuarioId = p.UsuarioId;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { c.Id, mensaje = $"Conductor {c.Nombres} {c.Apellidos} guardado." });
    }

    private static async Task<IResult> GuardarTransportistaAsync(
        Guid? id, PeticionTransportista p, ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct)
    {
        if (ctx.EmpresaId is not { } empresaId) return Results.BadRequest(new { error = "Elige una empresa." });
        var ruc = (p.Ruc ?? "").Trim();
        if (!Ruc.IsMatch(ruc)) return Results.BadRequest(new { error = "El RUC no es válido (11 dígitos)." });
        if (string.IsNullOrWhiteSpace(p.RazonSocial)) return Results.BadRequest(new { error = "Escribe la razón social." });
        var mtc = string.IsNullOrWhiteSpace(p.Mtc) ? null : p.Mtc.Trim().ToUpperInvariant();
        if (mtc is not null && !Mtc.IsMatch(mtc)) return Results.BadRequest(new { error = "El registro MTC no es válido." });

        var otro = await db.Transportistas.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.EmpresaId == empresaId && x.Ruc == ruc, ct);
        Transportista t;
        if (id is { } i)
        {
            var hallado = await db.Transportistas.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == i && x.EmpresaId == empresaId, ct);
            if (hallado is null) return Results.NotFound(new { error = "No se encontró." });
            t = hallado;
            if (otro is not null && otro.Id != i) return Results.Conflict(new { error = $"El RUC {ruc} ya está guardado." });
        }
        else
        {
            if (otro is not null) return Results.Conflict(new { error = $"El RUC {ruc} ya está guardado{(otro.Activo ? "" : " (desactivado: reactívalo)")}." });
            t = new Transportista { EmpresaId = empresaId };
            db.Transportistas.Add(t);
        }

        t.Ruc = ruc;
        t.RazonSocial = p.RazonSocial.Trim().ToUpperInvariant();
        t.Mtc = mtc;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { t.Id, mensaje = $"{t.RazonSocial} guardada." });
    }


    // =========================================================================
    // SE GUARDAN SOLOS AL EMITIR
    //
    // Lo nuevo se agrega; lo que ya estaba se actualiza (la licencia que
    // cambió, el TUC que faltaba) y suma un uso. Lo DESACTIVADO no se toca:
    // alguien decidió que no salga, y emitir con él a mano no lo revive.
    // =========================================================================

    internal static async Task RecordarAsync(
        ContextoPlataforma db, Guid empresaId, PeticionGuiaPlataforma p, string tipo, CancellationToken ct)
    {
        var ahora = DateTime.UtcNow;
        var publico = tipo == "09" && p.Modalidad == "01";

        if (!publico && EndpointsGuias.PlacaLimpia(p.Vehiculo?.Placa) is { } placa && Placa.IsMatch(placa))
        {
            var v = await db.Vehiculos.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.EmpresaId == empresaId && x.Placa == placa, ct);
            if (v is null)
            {
                db.Vehiculos.Add(new Vehiculo
                {
                    EmpresaId = empresaId, Placa = placa,
                    Descripcion = EndpointsGuias.Limpio(p.Vehiculo!.Descripcion, 80),
                    Tuc = EndpointsGuias.TucLimpio(p.Vehiculo.Tuc),
                    Autorizacion = EndpointsGuias.Limpio(p.Vehiculo.Autorizacion, 50),
                    EntidadAutorizadora = string.IsNullOrWhiteSpace(p.Vehiculo.Autorizacion) ? null : EndpointsGuias.Limpio(p.Vehiculo.Entidad, 2),
                    Usos = 1, UltimoUso = ahora
                });
            }
            else if (v.Activo)
            {
                v.Usos++; v.UltimoUso = ahora;
                v.Tuc = EndpointsGuias.TucLimpio(p.Vehiculo!.Tuc) ?? v.Tuc;
                v.Descripcion = EndpointsGuias.Limpio(p.Vehiculo.Descripcion, 80) ?? v.Descripcion;
                if (!string.IsNullOrWhiteSpace(p.Vehiculo.Autorizacion))
                {
                    v.Autorizacion = EndpointsGuias.Limpio(p.Vehiculo.Autorizacion, 50);
                    v.EntidadAutorizadora = EndpointsGuias.Limpio(p.Vehiculo.Entidad, 2);
                }
            }
        }

        if (!publico)
        {
            foreach (var c in new[] { p.Conductor }.Concat(p.Relevos ?? []))
            {
                if (c?.NumeroDocumento is null || c.Licencia is null) continue;
                var tipoDoc = c.TipoDocumento is "4" or "7" ? c.TipoDocumento : "1";
                var doc = c.NumeroDocumento.Trim().ToUpperInvariant();
                var lic = c.Licencia.Trim().ToUpperInvariant();
                if (!Licencia.IsMatch(lic) || !Regex.IsMatch(doc, "^[A-Z0-9]{8,15}$")) continue;

                var k = await db.Conductores.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.EmpresaId == empresaId && x.TipoDoc == tipoDoc && x.NumeroDoc == doc, ct);
                if (k is null)
                {
                    db.Conductores.Add(new Conductor
                    {
                        EmpresaId = empresaId, TipoDoc = tipoDoc, NumeroDoc = doc,
                        Nombres = c.Nombres?.Trim() ?? "", Apellidos = c.Apellidos?.Trim() ?? "",
                        Licencia = lic, UsuarioId = c.UsuarioId, Usos = 1, UltimoUso = ahora
                    });
                }
                else if (k.Activo)
                {
                    k.Usos++; k.UltimoUso = ahora; k.Licencia = lic;
                    k.UsuarioId ??= c.UsuarioId;
                }
            }
        }

        var empresas = new List<TransportistaGuia>();
        if (publico && p.Transportista is not null) empresas.Add(p.Transportista);
        if (tipo == "31" && p.Subcontratista is not null) empresas.Add(p.Subcontratista);
        foreach (var t in empresas)
        {
            var ruc = t.Ruc?.Trim() ?? "";
            if (!Ruc.IsMatch(ruc) || string.IsNullOrWhiteSpace(t.RazonSocial)) continue;
            var x = await db.Transportistas.IgnoreQueryFilters().FirstOrDefaultAsync(y => y.EmpresaId == empresaId && y.Ruc == ruc, ct);
            if (x is null)
            {
                db.Transportistas.Add(new Transportista
                {
                    EmpresaId = empresaId, Ruc = ruc, RazonSocial = t.RazonSocial.Trim().ToUpperInvariant(),
                    Mtc = EndpointsGuias.Limpio(t.Mtc, 20)?.ToUpperInvariant(), Usos = 1, UltimoUso = ahora
                });
            }
            else if (x.Activo)
            {
                x.Usos++; x.UltimoUso = ahora;
                x.Mtc = EndpointsGuias.Limpio(t.Mtc, 20)?.ToUpperInvariant() ?? x.Mtc;
            }
        }

        // EN UN PUNTO DE GUARDADO: si falla (dos guías al mismo tiempo con la
        // misma placa nueva), se deshace solo esto y la guía, ya emitida, queda.
        var transaccion = db.Database.CurrentTransaction;
        if (transaccion is not null) await transaccion.CreateSavepointAsync(PuntoGuardado, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            if (transaccion is not null) await transaccion.RollbackToSavepointAsync(PuntoGuardado, ct);
            foreach (var e in db.ChangeTracker.Entries().Where(x => x.Entity is Vehiculo or Conductor or Transportista).ToList())
                e.State = EntityState.Detached;
        }
    }
}