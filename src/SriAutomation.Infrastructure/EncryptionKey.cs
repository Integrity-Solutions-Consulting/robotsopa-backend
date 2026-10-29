using System.Security.Cryptography;

namespace SriAutomation.Infrastructure;

public static class EncryptionKey
{
    public static byte[] Read(string variable, string file)
    {
        byte[] key;
        var encoded = Environment.GetEnvironmentVariable(variable);
        if (encoded is not null)
        {
            try { key = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new InvalidOperationException($"{variable} debe contener Base64 válido de una clave de 32 bytes."); }
        }
        else
        {
            try { key = File.ReadAllBytes(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { throw new InvalidOperationException($"Configure {variable} o un archivo de clave accesible de 32 bytes."); }
        }
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidOperationException($"La clave de {variable} debe contener exactamente 32 bytes.");
    }

    public static void Validate(string variable, string file) => CryptographicOperations.ZeroMemory(Read(variable, file));
}
