namespace Plataforma.Core.Seguridad;

/// <summary>
/// La llave con la que se cifra todo lo que hay que poder recuperar: el
/// secreto del segundo factor y las claves de API del servicio de facturación.
///
/// VIVE EN UNA VARIABLE DE ENTORNO Y NUNCA EN LA BASE. Guardar la llave junto
/// a lo que protege es cerrar la puerta y dejarla en la cerradura.
///
/// FALLA AL ARRANCAR SI NO ESTÁ, y es deliberado. La alternativa —arrancar y
/// descubrirlo el día que alguien guarde su primer certificado— convierte un
/// error de configuración en un incidente con un cliente delante.
/// </summary>
public sealed class LlaveMaestra
{
    public const string Variable = "PLATAFORMA_LLAVE_MAESTRA";

    /// <summary>32 bytes. AES-256.</summary>
    public byte[] Bytes { get; }

    private LlaveMaestra(byte[] bytes) => Bytes = bytes;

    /// <summary>
    /// Lee la llave. <paramref name="respaldo"/> permite pasar un lector
    /// alternativo (la configuración de ASP.NET, por ejemplo) sin que Core
    /// tenga que depender de ella.
    /// </summary>
    public static LlaveMaestra Leer(Func<string, string?>? respaldo = null)
    {
        var valor = Environment.GetEnvironmentVariable(Variable)
                    ?? respaldo?.Invoke(Variable);

        if (string.IsNullOrWhiteSpace(valor))
        {
            throw new InvalidOperationException(
                $"Falta la variable {Variable}. Sin ella no se puede descifrar " +
                "nada de lo ya guardado, así que el sistema no arranca. " +
                "Mírala en el .env, o genera una nueva con: openssl rand -base64 32");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(valor.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"{Variable} no está en base64. Tiene que ser el resultado de " +
                "32 bytes aleatorios codificados, no una frase escrita a mano.");
        }

        if (bytes.Length != 32)
        {
            throw new InvalidOperationException(
                $"{Variable} tiene {bytes.Length} bytes y hacen falta 32. " +
                "Genera una nueva con: openssl rand -base64 32");
        }

        return new LlaveMaestra(bytes);
    }
}
