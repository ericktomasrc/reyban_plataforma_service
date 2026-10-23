namespace Plataforma.Core.Dominio;

/// <summary>
/// Los códigos de permiso, como constantes.
///
/// POR QUÉ CONSTANTES Y NO CADENAS SUELTAS POR EL CÓDIGO:
///
/// Un permiso mal escrito en una cadena —"facturacion.emitr"— no falla al
/// compilar ni al ejecutar. Simplemente no coincide con ninguno, y según cómo
/// esté escrita la comprobación, DEJA PASAR. Con constantes, el error lo da
/// el compilador y nunca llega a producción.
///
/// Esta lista tiene que coincidir con la migración 006. Añadir un permiso son
/// dos cambios: una línea aquí y una línea en una migración nueva.
/// </summary>
public static class Permisos
{
    /// <summary>
    /// Solo para el super administrador. El rol «Administrador» de una
    /// empresa NO recibe ninguno de estos: si se mezclaran, tarde o temprano
    /// un cliente se activa un módulo que no paga.
    /// </summary>
    public static class Plataforma
    {
        public const string EmpresasGestionar = "plataforma.empresas_gestionar";
        public const string ModulosGestionar  = "plataforma.modulos_gestionar";
        public const string BitacoraVer       = "plataforma.bitacora_ver";
        public const string Suplantar         = "plataforma.suplantar";

        /// <summary>042: modo de consultas (SUNAT / Decolecta) y padrón de SUNAT.</summary>
        public const string ConsultasGestionar = "plataforma.consultas_gestionar";
    }

    /// <summary>Dentro de la propia empresa.</summary>
    public static class Usuarios
    {
        public const string Ver             = "usuarios.ver";
        public const string Crear           = "usuarios.crear";
        public const string Editar          = "usuarios.editar";
        public const string Desactivar      = "usuarios.desactivar";
        public const string RolesGestionar  = "usuarios.roles_gestionar";
        public const string SesionesRevocar = "usuarios.sesiones_revocar";
        public const string BitacoraVer     = "usuarios.bitacora_ver";
    }

    /// <summary>032: un permiso por reporte (Reportes › Mi negocio).</summary>
    public static class Reportes
    {
        public const string MiDia          = "reportes.mi_dia";
        public const string TableroVentas  = "reportes.tablero_ventas";
        public const string VentasCliente  = "reportes.ventas_cliente";
        public const string VentasProducto = "reportes.ventas_producto";
        public const string VentasVendedor = "reportes.ventas_vendedor";

        // 034: SUNAT y contabilidad
        public const string ResumenIgv     = "reportes.resumen_igv";
        public const string RegistroVentas = "reportes.registro_ventas";
        public const string ConProblema    = "reportes.con_problema";
        public const string AnuladosNotas  = "reportes.anulados_notas";
        public const string DescargaMasiva = "reportes.descarga_masiva";
    }

    public static class Facturacion
    {
        public const string Ver                   = "facturacion.ver";
        public const string Emitir                = "facturacion.emitir";
        public const string Anular                = "facturacion.anular";
        public const string NotasCredito          = "facturacion.notas_credito";   // 031
        public const string Descargar             = "facturacion.descargar";
        public const string ClientesGestionar     = "facturacion.clientes_gestionar";
        public const string ProductosGestionar    = "facturacion.productos_gestionar";
        public const string CredencialesGestionar = "facturacion.credenciales_gestionar";
        public const string SucursalesGestionar   = "facturacion.sucursales_gestionar";   // 044
    }
}


/// <summary>
/// Códigos de módulo. Hoy solo hay uno.
/// </summary>
public static class Modulos
{
    public const string Facturacion = "facturacion";
}
