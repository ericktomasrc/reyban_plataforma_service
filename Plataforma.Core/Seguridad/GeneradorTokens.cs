using System.Security.Cryptography;
using System.Text;

namespace Plataforma.Core.Seguridad;

/// <summary>
/// Genera tokens de sesión y de un solo uso.
///
/// LA REGLA: el usuario recibe el token una vez —en su cookie o en su
/// correo— y la base guarda solo su SHA-256. Para validarlo se hashea lo que
/// llega y se compara.
///
/// Consecuencia: quien robe un volcado de la base NO PUEDE suplantar a nadie,
/// porque de un hash no se saca el token. Si se guardara el token tal cual,
/// un respaldo filtrado sería una sesión abierta por cada usuario conectado.
///
/// Aquí sí basta SHA-256 sin iteraciones, al revés que con las contraseñas:
/// son 32 bytes aleatorios y no hay diccionario que los adivine.
/// </summary>
public static class GeneradorTokens
{
    private const int Bytes = 32;

    /// <summary>
    /// Devuelve el token para enviar al usuario y su hash para guardar.
    ///
    /// Los dos juntos y en el mismo sitio a propósito: que sea imposible
    /// guardar uno y olvidarse del otro.
    /// </summary>
    public static (string Token, byte[] Hash) Nuevo()
    {
        var crudo = RandomNumberGenerator.GetBytes(Bytes);

        // Base64 "de URL": sin +, / ni =, para que viaje en una cookie o en
        // un enlace de correo sin que nadie lo escape mal por el camino.
        var token = Convert.ToBase64String(crudo)
                           .Replace('+', '-')
                           .Replace('/', '_')
                           .TrimEnd('=');

        return (token, Hashear(token));
    }

    public static byte[] Hashear(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));


    /// <summary>
    /// Una contraseña inicial para un usuario recién creado.
    ///
    /// LA GENERA EL SISTEMA, NO QUIEN DA DE ALTA. Si la eligiera el
    /// administrador, la mitad de los usuarios de la plataforma acabarían con
    /// la misma, porque las personas repiten.
    ///
    /// Se enseña UNA vez, al crearla, y el usuario está obligado a cambiarla
    /// en su primer ingreso.
    ///
    /// Sin caracteres que se confundan al leerlos en voz alta o copiarlos de
    /// un correo: ni 0 ni O, ni 1 ni l ni I.
    /// </summary>
    public static string ClaveTemporal(int largo = 14)
    {
        const string alfabeto = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        var salida = new char[largo];
        for (var i = 0; i < largo; i++)
            salida[i] = alfabeto[RandomNumberGenerator.GetInt32(alfabeto.Length)];

        return new string(salida);
    }
}
