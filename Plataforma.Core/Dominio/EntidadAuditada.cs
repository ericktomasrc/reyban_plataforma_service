namespace Plataforma.Core.Dominio;

/// <summary>
/// Base de toda entidad que se guarda en la base de datos.
///
/// LAS SEIS COLUMNAS ESTÁN EN CADA TABLA, no en una tabla aparte: esta clase
/// existe para no repetirlas en veinte archivos, y EF la aplana al mapear.
/// No se crea ninguna tabla "EntidadAuditada".
///
/// NINGUNA DE ESTAS PROPIEDADES LA ESCRIBE LA APLICACIÓN. Las rellena un
/// disparador de PostgreSQL, y pisa lo que venga. Por eso van configuradas en
/// el contexto como generadas por la base: si EF las mandara en el INSERT,
/// el valor se perdería igual y solo serviría para confundir al leer el log
/// de SQL.
/// </summary>
public abstract class EntidadAuditada
{
    public Guid Id { get; set; }

    public DateTime CreadoEn { get; set; }

    /// <summary>
    /// Nulo significa que no había sesión: una migración, un trabajo de fondo
    /// o un script de mantenimiento. Es un caso normal, no un dato que falte.
    /// </summary>
    public Guid? CreadoPor { get; set; }

    public DateTime? ModificadoEn { get; set; }
    public Guid? ModificadoPor { get; set; }

    /// <summary>
    /// Nada se borra. Desactivar deja la fila con su historia intacta.
    ///
    /// El rol de la aplicación no tiene permiso DELETE en ninguna tabla, así
    /// que esto no es una convención que se pueda saltar por descuido: un
    /// DELETE falla en la base.
    /// </summary>
    public bool Activo { get; set; } = true;

    /// <summary>
    /// Concurrencia optimista. La sube el disparador en cada cambio real.
    ///
    /// Dos usuarios abren el mismo cliente y los dos guardan: sin esta
    /// columna, el segundo pisa al primero en silencio. Con ella, EF lanza
    /// DbUpdateConcurrencyException y se puede avisar.
    /// </summary>
    public int Version { get; set; }
}


/// <summary>
/// Entidad que pertenece a una empresa concreta.
///
/// La columna la comprueba además el Row Level Security de PostgreSQL, que es
/// la barrera de verdad: para una sesión de la empresa A, las filas de la B
/// no existen. Esta propiedad es lo que el código necesita para escribirlas;
/// el aislamiento al leer no depende de ella.
/// </summary>
public abstract class EntidadDeEmpresa : EntidadAuditada
{
    public Guid EmpresaId { get; set; }
}
