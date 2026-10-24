using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Facturacion;
using Plataforma.Core.Datos;
using Plataforma.Core.Dominio;
using Plataforma.Core.Seguridad;

namespace Plataforma.Api.Administracion;

/// <summary>
/// 046 — LOS DATOS DE LA PERSONA (Personal de la empresa y Mi equipo).
///
/// Obligatorios: tipo y número de documento, nombres, apellido paterno y el
/// materno con DNI (con carné o pasaporte puede no tenerlo). Lo demás es
/// opcional. Ley 29733: nada de foto del documento.
/// </summary>
public sealed record PeticionFicha(
    string? TipoDocumento,
    string? NumeroDocumento,
    string? Nombres,
    string? ApellidoPaterno,
    string? ApellidoMaterno,
    string? Celular = null,
    DateOnly? FechaNacimiento = null,
    string? Direccion = null,
    string? Ubigeo = null,
    string? Cargo = null,
    DateOnly? FechaIngreso = null);

/// <summary>
/// 046 — Qué puede hacer y qué ve en la empresa: un rol, el alcance, sus
/// sucursales (y la de por defecto) y las dos casillas que suman permisos.
/// </summary>
public sealed record PeticionAcceso(
    Guid? Rol,
    string? Alcance,
    Guid[]? Sucursales,
    Guid? SucursalDefecto,
    bool Notas = false,
    bool Personal = false);

/// <summary>La ficha ya revisada y normalizada (mayúsculas, sin espacios de más).</summary>
public sealed record Ficha(
    string TipoDocumento, string NumeroDocumento, string Nombres, string ApellidoPaterno, string? ApellidoMaterno,
    string? Celular, DateOnly? FechaNacimiento, string? Direccion, string? Ubigeo, string? Cargo, DateOnly? FechaIngreso)
{
    public string NombreCompleto =>
        string.Join(' ', new[] { Nombres, ApellidoPaterno, ApellidoMaterno }.Where(x => !string.IsNullOrWhiteSpace(x)));

    public void Aplicar(Usuario u)
    {
        u.TipoDocumento   = TipoDocumento;
        u.NumeroDocumento = NumeroDocumento;
        u.Nombres         = Nombres;
        u.ApellidoPaterno = ApellidoPaterno;
        u.ApellidoMaterno = ApellidoMaterno;
        u.Celular         = Celular;
        u.FechaNacimiento = FechaNacimiento;
        u.Direccion       = Direccion;
        u.Ubigeo          = Ubigeo;
        u.Cargo           = Cargo;
        u.FechaIngreso    = FechaIngreso;
        u.Nombre          = NombreCompleto;
    }

    /// <summary>Para las funciones de Mi equipo (jsonb).</summary>
    public string ComoJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        tipoDocumento = TipoDocumento, numeroDocumento = NumeroDocumento, nombres = Nombres,
        apellidoPaterno = ApellidoPaterno, apellidoMaterno = ApellidoMaterno, celular = Celular,
        fechaNacimiento = FechaNacimiento?.ToString("yyyy-MM-dd"), direccion = Direccion, ubigeo = Ubigeo,
        cargo = Cargo, fechaIngreso = FechaIngreso?.ToString("yyyy-MM-dd")
    });
}

public static class FichaPersonal
{
    private static readonly Regex Dni = new("^[0-9]{8}$", RegexOptions.Compiled);
    private static readonly Regex Ce = new("^[A-Z0-9]{8,12}$", RegexOptions.Compiled);
    private static readonly Regex Pasaporte = new("^[A-Z0-9]{6,12}$", RegexOptions.Compiled);
    private static readonly Regex Espacios = new(@"\s+", RegexOptions.Compiled);

    public static string NombreDocumento(string? tipo) => tipo switch
    {
        "1" => "DNI", "4" => "carné de extranjería", "7" => "pasaporte", _ => "documento"
    };

    private static string? Limpio(string? s, int max)
    {
        var t = Espacios.Replace((s ?? "").Trim(), " ");
        return t.Length == 0 ? null : t.Length > max ? t[..max] : t;
    }

    /// <summary>Revisa la ficha. Devuelve la ficha limpia, o el problema y el campo.</summary>
    public static async Task<(Ficha? Ficha, string? Error, string? Campo)> RevisarAsync(
        PeticionFicha? f, ContextoPlataforma db, CancellationToken ct)
    {
        if (f is null) return (null, "Faltan los datos de la persona.", "nombres");

        var tipo = (f.TipoDocumento ?? "").Trim();
        if (tipo is not ("1" or "4" or "7"))
            return (null, "Elige el tipo de documento: DNI, carné de extranjería o pasaporte.", "tipoDocumento");

        var numero = (f.NumeroDocumento ?? "").Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();
        var bien = tipo switch { "1" => Dni.IsMatch(numero), "4" => Ce.IsMatch(numero), _ => Pasaporte.IsMatch(numero) };
        if (!bien)
            return (null, tipo switch
            {
                "1" => "El DNI son 8 dígitos.",
                "4" => "El carné de extranjería son de 8 a 12 letras o números.",
                _   => "El pasaporte son de 6 a 12 letras o números."
            }, "numeroDocumento");

        var nombres = Limpio(f.Nombres, 80);
        if (nombres is null || nombres.Length < 2) return (null, "Escribe sus nombres.", "nombres");
        var paterno = Limpio(f.ApellidoPaterno, 60);
        if (paterno is null) return (null, "Escribe su apellido paterno.", "apellidoPaterno");
        var materno = Limpio(f.ApellidoMaterno, 60);
        if (materno is null && tipo == "1")
            return (null, "Con DNI, el apellido materno es obligatorio (con carné o pasaporte puede no tenerlo).", "apellidoMaterno");

        string? celular = null;
        if (!string.IsNullOrWhiteSpace(f.Celular))
        {
            var c = new string(f.Celular.Where(ch => char.IsDigit(ch) || ch == '+').ToArray());
            if (!Regex.IsMatch(c, @"^\+?[0-9]{6,15}$"))
                return (null, "El celular son solo números (9 dígitos en Perú). Ej.: 987654321.", "celular");
            celular = c;
        }

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        if (f.FechaNacimiento is { } nac && (nac > hoy.AddYears(-14) || nac < new DateOnly(1900, 1, 1)))
            return (null, "Revisa la fecha de nacimiento (tiene que tener al menos 14 años).", "fechaNacimiento");
        if (f.FechaIngreso is { } ing && (ing > hoy.AddDays(90) || ing < new DateOnly(1950, 1, 1)))
            return (null, "Revisa la fecha de ingreso.", "fechaIngreso");

        var direccion = Limpio(f.Direccion, 200);
        string? ubigeo = null;
        if (!string.IsNullOrWhiteSpace(f.Ubigeo))
        {
            if (await Sucursales.ProblemaUbigeoAsync(db, f.Ubigeo, ct) is { } malUbigeo)
                return (null, malUbigeo, "ubigeo");
            ubigeo = f.Ubigeo.Trim();
        }

        return (new Ficha(tipo, numero, nombres, paterno, materno, celular, f.FechaNacimiento,
                          direccion, ubigeo, Limpio(f.Cargo, 80), f.FechaIngreso), null, null);
    }
}


/// <summary>Los roles de la plataforma que el código necesita reconocer (046).</summary>
public static class RolesSistema
{
    public static readonly Guid Administrador  = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Facturador     = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid JefeSucursal   = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>Se suman a otro rol: las casillas de la ficha.</summary>
    public static readonly Guid Notas          = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid AgregaPersonal = Guid.Parse("77777777-7777-7777-7777-777777777777");

    public static bool EsComplemento(Guid id) => id == Notas || id == AgregaPersonal;

    /// <summary>Los que un jefe de sucursal nunca da, ni edita a quien los tiene.</summary>
    public static bool DeMando(Guid id) => id == Administrador || id == JefeSucursal || id == AgregaPersonal;

    public static int Rango(string? alcance) => alcance switch { "empresa" => 2, "sucursal" => 1, _ => 0 };
}


/// <summary>
/// 046 — QUIÉN GESTIONA A QUIÉN.
///
/// El Administrador (quien tiene «usuarios.roles_gestionar», que solo lleva
/// ese rol) gestiona a todos. Cualquier otro con permiso de agregar personal
/// —el jefe de sucursal con su casilla— es «restringido»:
///
///   · solo ve y edita personal de SUS sucursales, nunca al Administrador ni
///     a otro jefe;
///   · solo marca sus sucursales;
///   · solo da roles con iguales o menos permisos que los suyos, y nunca
///     Administrador, Jefe ni «Agrega personal»;
///   · no da un alcance mayor que el suyo.
/// </summary>
public sealed class Gestor
{
    public bool Restringido { get; }
    public Guid? Id { get; }
    public IReadOnlySet<string> Permisos { get; }

    /// <summary>Nulo = todas (alcance de toda la empresa).</summary>
    public IReadOnlySet<Guid>? Sucursales { get; }
    public int Rango { get; }

    public Gestor(ContextoPeticion ctx)
    {
        Id          = ctx.UsuarioId;
        Restringido = !ctx.Tiene(Plataforma.Core.Dominio.Permisos.Usuarios.RolesGestionar);
        Permisos    = ctx.Permisos;
        Sucursales  = ctx.VeTodaLaEmpresa ? null : ctx.SucursalesAlcance.Select(s => s.Id).ToHashSet();
        Rango       = ctx.VeTodaLaEmpresa ? 2 : RolesSistema.Rango(ctx.Alcance);
    }

    /// <summary>¿Puede ver y editar a esta persona? (A sí misma, solo su ficha: lo decide quien llama.)</summary>
    public bool Puede(PersonaGestionada p, IReadOnlyDictionary<Guid, List<string>> permisosDeRol)
    {
        if (!Restringido) return true;
        if (p.Id == Id) return false;
        if (p.Roles.Any(RolesSistema.DeMando)) return false;
        if (RolesSistema.Rango(p.Alcance) > Rango) return false;
        if (Sucursales is not null && (p.Sucursales.Count == 0 || !p.Sucursales.All(Sucursales.Contains))) return false;
        return p.Roles.All(r => permisosDeRol.TryGetValue(r, out var ps) && ps.All(Permisos.Contains));
    }
}

/// <summary>Lo que hace falta de una persona para decidir si se la gestiona.</summary>
public sealed record PersonaGestionada(Guid Id, List<Guid> Roles, string Alcance, List<Guid> Sucursales);


public static class DatosPersonal
{
    /// <summary>
    /// Los permisos de cada rol que se puede asignar en esta empresa: los de la
    /// plataforma (con su ajuste de la empresa, si lo hay) y los propios.
    /// </summary>
    public static async Task<List<RolAsignable>> RolesAsync(ContextoPlataforma db, CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking()
            .Select(r => new
            {
                r.Id, r.Nombre, r.Descripcion, r.BasadoEn, deSistema = r.EmpresaId == null,
                permisos = db.RolPermisos.Where(rp => rp.RolId == r.Id && rp.Activo).Select(rp => rp.PermisoCodigo).ToList()
            })
            .ToListAsync(ct);

        var ajustes = roles.Where(r => r.BasadoEn is not null)
                           .GroupBy(r => r.BasadoEn!.Value)
                           .ToDictionary(g => g.Key, g => g.First().permisos);

        return roles.Where(r => r.BasadoEn is null)
                    .Select(r => new RolAsignable(r.Id, r.Nombre, r.Descripcion, r.deSistema,
                                                  ajustes.TryGetValue(r.Id, out var aj) ? aj : r.permisos))
                    .ToList();
    }

    /// <summary>Roles, alcance y sucursales de varias personas en la empresa abierta.</summary>
    public static async Task<Dictionary<Guid, PersonaGestionada>> PersonasAsync(
        ContextoPlataforma db, Guid empresaId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var roles = (await db.UsuarioRoles.AsNoTracking()
                .Where(r => ids.Contains(r.UsuarioId))
                .Select(r => new { r.UsuarioId, r.RolId }).ToListAsync(ct))
            .GroupBy(x => x.UsuarioId).ToDictionary(g => g.Key, g => g.Select(x => x.RolId).ToList());

        var alcances = (await db.UsuarioAlcances.AsNoTracking()
                .Where(a => a.EmpresaId == empresaId && ids.Contains(a.UsuarioId))
                .Select(a => new { a.UsuarioId, a.Alcance }).ToListAsync(ct))
            .GroupBy(x => x.UsuarioId).ToDictionary(g => g.Key, g => g.First().Alcance);

        var sucursales = (await db.UsuarioSucursales.AsNoTracking()
                .Where(x => x.EmpresaId == empresaId && ids.Contains(x.UsuarioId))
                .Select(x => new { x.UsuarioId, x.SucursalId }).ToListAsync(ct))
            .GroupBy(x => x.UsuarioId).ToDictionary(g => g.Key, g => g.Select(x => x.SucursalId).ToList());

        return ids.Distinct().ToDictionary(id => id, id => new PersonaGestionada(
            id,
            roles.GetValueOrDefault(id) ?? [],
            roles.GetValueOrDefault(id)?.Contains(RolesSistema.Administrador) == true ? "empresa"
                : alcances.GetValueOrDefault(id) ?? "empresa",
            sucursales.GetValueOrDefault(id) ?? []));
    }
}

public sealed record RolAsignable(Guid Id, string Nombre, string? Descripcion, bool DeSistema, List<string> Permisos);