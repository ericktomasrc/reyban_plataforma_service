using System.Security.Cryptography;
using System.Text;

namespace Plataforma.Core.Seguridad;

/// <summary>
/// El segundo factor: TOTP, el de Google Authenticator y Microsoft
/// Authenticator (RFC 6238).
///
/// POR QUÉ TOTP Y NO UN CÓDIGO POR SMS O POR CORREO:
///
///   · El SMS se intercepta —cambiando la tarjeta SIM de la víctima, que es
///     más fácil de lo que suena en Perú— y además cuesta dinero por mensaje.
///   · El código por correo no protege de nada cuando lo que se teme es que
///     alguien tenga acceso al correo, que es el caso habitual.
///   · TOTP no necesita red en el teléfono, ni proveedor, ni cuesta nada.
///
/// ESCRITO A MANO Y NO CON UN PAQUETE, a propósito. TOTP son treinta líneas
/// sobre HMAC-SHA1, que trae .NET. No es criptografía inventada aquí: es una
/// construcción publicada y fija desde 2011, y el algoritmo está entero en
/// este archivo, a la vista. Una dependencia más sería más superficie que
/// mantener a cambio de nada.
/// </summary>
public static class SegundoFactor
{
    /// <summary>
    /// 20 bytes, que es el tamaño del bloque de SHA-1 y lo que esperan todas
    /// las aplicaciones de autenticación.
    /// </summary>
    private const int BytesDelSecreto = 20;

    /// <summary>Seis dígitos, treinta segundos. Lo que asume cualquier app.</summary>
    private const int Digitos  = 6;
    private const int Segundos = 30;

    /// <summary>
    /// Cuántas ventanas antes y después se aceptan.
    ///
    /// UNA, que son 90 segundos en total contando la actual. El reloj de un
    /// teléfono se desajusta unos segundos y eso no puede dejar a nadie fuera;
    /// pero cada ventana de más es medio minuto más que dura un código robado.
    /// </summary>
    private const int Tolerancia = 1;


    public static byte[] GenerarSecreto() =>
        RandomNumberGenerator.GetBytes(BytesDelSecreto);


    /// <summary>
    /// La dirección que se mete en el QR.
    ///
    /// El `issuer` sale dos veces —en la ruta y como parámetro— y no es un
    /// error: las aplicaciones antiguas leen uno y las nuevas el otro. Sin los
    /// dos, hay teléfonos donde la cuenta aparece sin nombre y el usuario
    /// termina con tres entradas iguales y sin saber cuál es cuál.
    /// </summary>
    public static string DireccionParaQr(string correo, byte[] secreto, string emisor)
    {
        var e = Uri.EscapeDataString(emisor);
        var c = Uri.EscapeDataString(correo);

        return $"otpauth://totp/{e}:{c}" +
               $"?secret={Base32(secreto)}" +
               $"&issuer={e}" +
               $"&algorithm=SHA1&digits={Digitos}&period={Segundos}";
    }


    /// <summary>
    /// El secreto en texto, por si el teléfono no puede leer el QR.
    /// En grupos de cuatro, que es como lo enseña todo el mundo.
    /// </summary>
    public static string SecretoLegible(byte[] secreto)
    {
        var b32 = Base32(secreto);
        var sb  = new StringBuilder();

        for (var i = 0; i < b32.Length; i += 4)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b32.AsSpan(i, Math.Min(4, b32.Length - i)));
        }

        return sb.ToString();
    }


    /// <summary>
    /// Comprueba un código.
    ///
    /// <paramref name="ultimoPaso"/> es la ventana del último código aceptado
    /// para este usuario. UN CÓDIGO NO SE ACEPTA DOS VECES: durante sus 30
    /// segundos sigue siendo matemáticamente válido, así que quien lo vea por
    /// encima del hombro tendría media ventana para usarlo. Rechazando todo
    /// paso que no sea posterior al último, esa puerta se cierra.
    /// </summary>
    public static bool Verificar(byte[] secreto, string codigo, long? ultimoPaso, out long paso)
    {
        paso = 0;

        if (string.IsNullOrWhiteSpace(codigo)) return false;

        // La gente copia el código con el espacio de en medio que enseña la
        // aplicación. Quitarlo aquí evita un «código incorrecto» absurdo.
        var limpio = new string(codigo.Where(char.IsDigit).ToArray());
        if (limpio.Length != Digitos) return false;

        var ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Segundos;

        for (var d = -Tolerancia; d <= Tolerancia; d++)
        {
            var candidato = ahora + d;

            if (ultimoPaso is not null && candidato <= ultimoPaso) continue;

            if (Comparar(Calcular(secreto, candidato), limpio))
            {
                paso = candidato;
                return true;
            }
        }

        return false;
    }


    // =========================================================================
    // EL ALGORITMO, QUE SON DIEZ LÍNEAS
    // =========================================================================

    private static string Calcular(byte[] secreto, long paso)
    {
        var contador = BitConverter.GetBytes(paso);
        if (BitConverter.IsLittleEndian) Array.Reverse(contador);

        var hash = HMACSHA1.HashData(secreto, contador);

        // «Truncamiento dinámico»: los cuatro últimos bits del hash dicen por
        // dónde empezar a leer. Así dos códigos seguidos no comparten ni la
        // posición de la que salen.
        var desplazamiento = hash[^1] & 0x0F;

        var numero = ((hash[desplazamiento]     & 0x7F) << 24)
                   | ((hash[desplazamiento + 1] & 0xFF) << 16)
                   | ((hash[desplazamiento + 2] & 0xFF) << 8)
                   |  (hash[desplazamiento + 3] & 0xFF);

        return (numero % (int)Math.Pow(10, Digitos)).ToString().PadLeft(Digitos, '0');
    }


    /// <summary>
    /// Comparación en tiempo constante.
    ///
    /// Un `==` normal se detiene en el primer carácter distinto, y esa
    /// diferencia de microsegundos, medida muchas veces, deja adivinar el
    /// código dígito a dígito. Con seis dígitos y treinta segundos el ataque
    /// es poco práctico, pero cuesta una línea evitarlo.
    /// </summary>
    private static bool Comparar(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));


    /// <summary>
    /// Base32 del RFC 4648, que es lo que leen las aplicaciones de
    /// autenticación. No vale Base64: su alfabeto tiene minúsculas y símbolos
    /// que ninguna de ellas acepta.
    /// </summary>
    private static string Base32(byte[] datos)
    {
        const string alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var sb     = new StringBuilder();
        var buffer = 0;
        var bits   = 0;

        foreach (var b in datos)
        {
            buffer = (buffer << 8) | b;
            bits  += 8;

            while (bits >= 5)
            {
                sb.Append(alfabeto[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            sb.Append(alfabeto[(buffer << (5 - bits)) & 31]);

        return sb.ToString();
    }
}


/// <summary>
/// Los códigos de recuperación: la salida cuando se pierde el teléfono.
///
/// SE ENSEÑAN UNA VEZ Y SE GUARDA SOLO SU HASH, igual que las contraseñas. Si
/// se guardaran en claro, quien leyera la tabla entraría en cualquier cuenta
/// saltándose el segundo factor — que es exactamente lo que el segundo factor
/// existe para impedir.
/// </summary>
public static class CodigosRecuperacion
{
    public const int Cuantos = 8;

    /// <summary>
    /// Sin caracteres que se confundan al copiarlos a mano de un papel: ni 0
    /// ni O, ni 1 ni I ni L. Quien usa uno de estos códigos suele estar
    /// nervioso y leyendo de una libreta.
    /// </summary>
    private const string Alfabeto = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static List<string> Generar()
    {
        var codigos = new List<string>(Cuantos);

        for (var i = 0; i < Cuantos; i++)
        {
            var grupo1 = Bloque();
            var grupo2 = Bloque();
            codigos.Add($"{grupo1}-{grupo2}");
        }

        return codigos;
    }

    /// <summary>
    /// El hash que se guarda. SHA-256 sin iteraciones basta, al revés que con
    /// las contraseñas: son diez caracteres al azar de un alfabeto de treinta
    /// y uno, no una palabra que alguien pueda estar en un diccionario.
    ///
    /// Se normaliza antes de hashear —mayúsculas, sin guiones ni espacios—
    /// para que dé igual cómo lo escriba quien lo teclea.
    /// </summary>
    public static byte[] Hashear(string codigo)
    {
        var limpio = new string(codigo
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

        return SHA256.HashData(Encoding.UTF8.GetBytes(limpio));
    }

    private static string Bloque()
    {
        var salida = new char[5];
        for (var i = 0; i < salida.Length; i++)
            salida[i] = Alfabeto[RandomNumberGenerator.GetInt32(Alfabeto.Length)];

        return new string(salida);
    }
}
