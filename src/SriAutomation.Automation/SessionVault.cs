using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SriAutomation.Application;

namespace SriAutomation.Automation;

public sealed record SessionData(string Account, string TaxId, DateTimeOffset Expires, string StorageState,string? SessionStorage=null);
public sealed class SessionVault(string directory, string keyFile)
{
    private string PathFor(string account) => Path.Combine(directory, Validation.Identifier(account) + ".session");
    public void Save(SessionData data)
    {
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(data); var key = File.ReadAllBytes(keyFile);
        if (key.Length != 32) throw new InvalidDataException("La clave de sesiones debe tener 32 bytes.");
        var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(data.Account));
            var temporary = PathFor(data.Account) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(nonce); file.Write(tag); file.Write(cipher); file.Flush(true); }
                File.Move(temporary, PathFor(data.Account), true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public SessionData Load(string account, string taxId)
    {
        if (!File.Exists(PathFor(account)) || !File.Exists(keyFile)) throw new HumanActionRequiredException("No hay sesión SRI protegida para esta cuenta. Complete el inicio de sesión local.");
        var encrypted = File.ReadAllBytes(PathFor(account)); var key = File.ReadAllBytes(keyFile);
        if (encrypted.Length < 28 || key.Length != 32) throw new HumanActionRequiredException("Sesión protegida no válida; autentique nuevamente.");
        var plain = new byte[encrypted.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16); aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(account));
            var data = JsonSerializer.Deserialize<SessionData>(plain) ?? throw new CryptographicException();
            if (data.Account != account || data.TaxId != taxId || data.Expires <= DateTimeOffset.UtcNow) throw new HumanActionRequiredException("Sesión SRI vencida o perteneciente a otra cuenta. Autentique nuevamente.");
            return data;
        }
        catch (CryptographicException) { throw new HumanActionRequiredException("No se pudo verificar la sesión protegida. Autentique nuevamente."); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public void Revoke(string account) { var path = PathFor(account); if (File.Exists(path)) File.Delete(path); }
}
