namespace Plataforma.Core.Dominio;

/// <summary>
/// Una sesión abierta. La cookie lleva un token aleatorio; esta fila dice si
/// sigue valiendo.
///
/// POR QUÉ UNA TABLA Y NO UN JWT AUTOCONTENIDO:
///
/// Un JWT vale hasta que expira, y no hay forma de anularlo. Si despides a
/// alguien a las diez de la mañana, su token sigue funcionando hasta que
/// caduque. Con una fila en una tabla, cerrarle la puerta es un UPDATE.
///
/// NO HEREDA DE EntidadAuditada: una sesión no se «desactiva», se revoca, y
/// no tiene versión ni concurrencia optimista. Lleva su propia bitácora por
/// disparador igual que las demás.
/// </summary>
public class Sesion
{
    public Guid Id { get; set; }

    // Sin navegación a Usuario, y es deliberado.
    //
    // `Usuario` lleva el filtro de `activo`; una sesión no. Al declarar la
    // relación como obligatoria, EF avisa de que el filtro de un lado puede
    // dejar fuera lo que el otro exige — y tiene razón.
    //
    // Pero es que además no hace falta: quien necesita los datos del usuario
    // a partir de una sesión es el middleware, y para eso usa
    // `resolver_sesion`, que trae exactamente lo que necesita en una sola
    // consulta.
    public Guid UsuarioId { get; set; }

    public Guid? EmpresaId { get; set; }

    /// <summary>
    /// SHA-256 del token que viaja en la cookie. 32 bytes.
    ///
    /// SE GUARDA EL HASH, NUNCA EL TOKEN. De un hash no se vuelve atrás, así
    /// que quien robe un volcado de la base no puede suplantar a nadie. Si se
    /// guardara el token tal cual, un respaldo filtrado sería una sesión
    /// abierta por cada usuario conectado.
    /// </summary>
    public byte[] TokenHash { get; set; } = [];

    public DateTime CreadaEn { get; set; }
    public DateTime ExpiraEn { get; set; }
    public DateTime UltimaActividad { get; set; }

    public System.Net.IPAddress? Ip { get; set; }
    public string? Agente { get; set; }

    /// <summary>
    /// El segundo factor es un estado de la SESIÓN, no del usuario: una
    /// sesión recién creada existe pero todavía no lo ha superado, y hasta
    /// que lo haga no puede hacer nada más que validarlo.
    /// </summary>
    public bool DosfaSuperado { get; set; }

    public DateTime? RevocadaEn { get; set; }
    public string? MotivoRevocacion { get; set; }

    public bool Vive(DateTime ahora) =>
        RevocadaEn is null && ExpiraEn > ahora;
}
