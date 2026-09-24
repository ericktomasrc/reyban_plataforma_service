using System.Security.Cryptography;

namespace Plataforma.Core.Seguridad;

/// <summary>
/// Hash de contraseñas con PBKDF2-SHA256.
///
/// POR QUÉ 210.000 ITERACIONES Y NO SHA-256 A SECAS:
///
/// La diferencia está en quién elige el valor. Una clave de API es aleatoria
/// y nadie la adivina: un hash rápido basta. Una contraseña la eligió una
/// persona, y las personas eligen mal. Contra un hash rápido, una tarjeta
/// gráfica prueba miles de millones por segundo; con iteraciones, lo que al
/// usuario le cuesta milisegundos al atacante le cuesta años.
///
/// EL FORMATO GUARDA EL ALGORITMO Y LAS ITERACIONES junto al hash:
///
///     pbkdf2-sha256$210000$&lt;sal&gt;$&lt;hash&gt;
///
/// Eso permite subir las iteraciones con los años sin dejar fuera a nadie: al
/// ingresar, si el hash guardado usa menos de las actuales, se recifra la
/// contraseña con las nuevas y se guarda. Sin esta parte, subir el número
/// significaría invalidar todas las contraseñas existentes.
/// </summary>
public static class HashDeClaves
{
    public const int IteracionesActuales = 210_000;

    private const int BytesDeSal  = 16;
    private const int BytesDeHash = 32;
    private const string Algoritmo = "pbkdf2-sha256";

    public static string Cifrar(string clave, int iteraciones = IteracionesActuales)
    {
        var sal = RandomNumberGenerator.GetBytes(BytesDeSal);
        var hash = Derivar(clave, sal, iteraciones);

        return string.Join('$',
            Algoritmo,
            iteraciones,
            Convert.ToBase64String(sal),
            Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Comprueba una contraseña contra su hash.
    ///
    /// <paramref name="necesitaRecifrado"/> sale en true cuando el hash
    /// guardado usa menos iteraciones que las actuales. Quien llame debe
    /// volver a cifrar y guardar — es el único momento en que se tiene la
    /// contraseña en claro.
    /// </summary>
    public static bool Verificar(string clave, string guardado, out bool necesitaRecifrado)
    {
        necesitaRecifrado = false;

        var partes = guardado.Split('$');
        if (partes.Length != 4 || partes[0] != Algoritmo) return false;
        if (!int.TryParse(partes[1], out var iteraciones)) return false;

        byte[] sal, esperado;
        try
        {
            sal      = Convert.FromBase64String(partes[2]);
            esperado = Convert.FromBase64String(partes[3]);
        }
        catch (FormatException) { return false; }

        var calculado = Derivar(clave, sal, iteraciones);

        // COMPARACIÓN EN TIEMPO CONSTANTE, no ==.
        //
        // Una comparación normal se detiene en el primer byte distinto, y el
        // tiempo que tarda delata cuántos acertó. Con suficientes intentos
        // medidos, eso se convierte en el hash. FixedTimeEquals tarda lo
        // mismo siempre.
        var coincide = CryptographicOperations.FixedTimeEquals(calculado, esperado);

        if (coincide && iteraciones < IteracionesActuales)
            necesitaRecifrado = true;

        return coincide;
    }

    private static byte[] Derivar(string clave, byte[] sal, int iteraciones) =>
        Rfc2898DeriveBytes.Pbkdf2(
            password: clave,
            salt: sal,
            iterations: iteraciones,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: BytesDeHash);
}
