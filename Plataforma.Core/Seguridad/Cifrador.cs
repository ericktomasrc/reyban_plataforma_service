using System.Security.Cryptography;
using System.Text;

namespace Plataforma.Core.Seguridad;

/// <summary>
/// Cifra y descifra con AES-GCM usando la llave maestra.
///
/// ES PARA LO QUE HAY QUE PODER RECUPERAR, no para contraseñas. El secreto
/// del segundo factor y la clave de API del servicio de facturación se tienen
/// que leer de vuelta: de un hash no se vuelve atrás.
///
/// GCM y no CBC: además de cifrar, autentica. Si alguien cambia un solo byte
/// del texto cifrado en la base, el descifrado FALLA en vez de devolver
/// basura silenciosa.
///
/// EL FORMATO GUARDADO ES UN SOLO BLOB:
///
///     [ nonce 12 bytes ][ etiqueta 16 bytes ][ texto cifrado ]
///
/// Todo junto en una columna, y no la clave por un lado y el nonce por otro.
/// Evita el fallo clásico de recuperar una fila con el nonce de otra, y deja
/// una sola regla en el sistema: lo cifrado se descifra entero o no se
/// descifra.
/// </summary>
public sealed class Cifrador(LlaveMaestra llave)
{
    private const int BytesNonce    = 12;   // lo que recomienda GCM
    private const int BytesEtiqueta = 16;

    public byte[] Cifrar(string texto)
    {
        var claro = Encoding.UTF8.GetBytes(texto);

        var nonce    = RandomNumberGenerator.GetBytes(BytesNonce);
        var cifrado  = new byte[claro.Length];
        var etiqueta = new byte[BytesEtiqueta];

        using var aes = new AesGcm(llave.Bytes, BytesEtiqueta);
        aes.Encrypt(nonce, claro, cifrado, etiqueta);

        var salida = new byte[BytesNonce + BytesEtiqueta + cifrado.Length];
        nonce.CopyTo(salida, 0);
        etiqueta.CopyTo(salida, BytesNonce);
        cifrado.CopyTo(salida, BytesNonce + BytesEtiqueta);
        return salida;
    }

    public string Descifrar(byte[] guardado)
    {
        if (guardado.Length < BytesNonce + BytesEtiqueta)
        {
            throw new CryptographicException(
                "El dato cifrado está truncado: no llega ni a la cabecera.");
        }

        var nonce    = guardado.AsSpan(0, BytesNonce);
        var etiqueta = guardado.AsSpan(BytesNonce, BytesEtiqueta);
        var cifrado  = guardado.AsSpan(BytesNonce + BytesEtiqueta);

        var claro = new byte[cifrado.Length];

        try
        {
            using var aes = new AesGcm(llave.Bytes, BytesEtiqueta);
            aes.Decrypt(nonce, cifrado, etiqueta, claro);
        }
        catch (CryptographicException)
        {
            // Casi siempre significa una de dos cosas, y conviene decirlas:
            // o la llave maestra no es la que cifró este dato, o alguien tocó
            // la fila. Las dos son graves y ninguna se arregla reintentando.
            throw new CryptographicException(
                "No se pudo descifrar. O la llave maestra no es la que cifró " +
                "este dato —¿se restauró un respaldo con otra llave?— o la " +
                "fila fue modificada.");
        }

        return Encoding.UTF8.GetString(claro);
    }
}
