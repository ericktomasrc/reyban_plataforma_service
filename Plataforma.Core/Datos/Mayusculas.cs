using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Plataforma.Core.Datos;

/// <summary>
/// e174 (056) — LO QUE SE ESCRIBE SE GUARDA EN MAYÚSCULAS.
///
/// Se hace AQUÍ, antes de guardar, y no solo con un disparador de la base: si
/// la base lo cambiara por su cuenta, la entidad que EF tiene en memoria
/// seguiría en minúsculas, y un comprobante que se crea y se emite en la misma
/// petición (el POS) viajaría a SUNAT distinto de como quedó guardado.
/// La migración 056 pone además disparadores en las tablas que se escriben con
/// SQL directo (cajas, movimientos de caja, notas del inventario).
///
/// SOLO estas columnas. Nunca correos, teléfonos, enlaces, códigos de lista
/// (contado, efectivo, merma…), números de operación ni cuentas.
///
/// Solo lo NUEVO o lo que se acaba de cambiar: tocar una fila por otra razón
/// (un estado, una fecha) no le cambia el texto.
/// </summary>
public static class Mayusculas
{
    private static readonly Dictionary<string, string[]> Columnas = new()
    {
        ["Cliente"]           = ["RazonSocial", "NombreComercial", "Direccion"],
        ["Producto"]          = ["Codigo", "Descripcion"],
        ["DocumentoLinea"]    = ["Descripcion"],
        ["Sucursal"]          = ["Nombre", "Direccion"],
        ["Proveedor"]         = ["RazonSocial", "Direccion"],
        ["ProductoCategoria"] = ["Nombre"],
        ["Guia"]              = ["MotivoDescripcion", "DestinatarioNombre", "PartidaDireccion", "LlegadaDireccion",
                                 "VehiculoPlaca", "ConductorNombre", "TransportistaNombre"],
        ["GuiaBien"]          = ["Descripcion"],
        ["GuiaEvento"]        = ["ConductorNombre", "Placa", "LlegadaDireccion", "ReinicioDireccion", "Observacion"],
        ["Vehiculo"]          = ["Placa", "Descripcion"],
        ["Conductor"]         = ["Nombres", "Apellidos", "Licencia"],
        ["Transportista"]     = ["RazonSocial"],
        ["InvOperacion"]      = ["DocumentoProveedor", "Nota"],
        ["InvTraslado"]       = ["Nota"],
    };

    public static void Aplicar(ChangeTracker tracker)
    {
        foreach (var e in tracker.Entries())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified)) continue;
            if (!Columnas.TryGetValue(e.Metadata.ClrType.Name, out var props)) continue;

            foreach (var nombre in props)
            {
                if (e.Metadata.FindProperty(nombre) is null) continue;
                var p = e.Property(nombre);
                if (e.State == EntityState.Modified && !p.IsModified) continue;
                if (p.CurrentValue is string s && s.Length > 0)
                {
                    var m = s.ToUpperInvariant();
                    if (!string.Equals(m, s, StringComparison.Ordinal)) p.CurrentValue = m;
                }
            }
        }
    }
}