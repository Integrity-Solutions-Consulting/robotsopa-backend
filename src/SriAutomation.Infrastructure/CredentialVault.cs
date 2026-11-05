using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SriAutomation.Application;
namespace SriAutomation.Infrastructure;

public sealed class CredentialVault(string directory,string keyFile)
{
    private sealed record Envelope(int Version,DateTimeOffset Updated,string Nonce,string Tag,string Cipher);
    public static CredentialVault Configured()=>new(Environment.GetEnvironmentVariable("SRI_CREDENTIAL_PATH")??Path.GetFullPath("storage/credentials"),Environment.GetEnvironmentVariable("SRI_CREDENTIAL_KEY_FILE")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SRI-Automation","secrets","credential.key"));
    private string PathFor(string account)=>Path.Combine(directory,Validation.Identifier(account)+".credential");
    private static byte[] Aad(string account,string taxId)=>Encoding.UTF8.GetBytes("SRI-CREDENTIAL-V1|"+Validation.Identifier(account)+"|"+taxId);
    public bool Present(string account)=>File.Exists(PathFor(account));
    public DateTimeOffset? Updated(string account)=>Present(account)?Read(account).Updated:null;
    private Envelope Read(string account)=>JsonSerializer.Deserialize<Envelope>(File.ReadAllText(PathFor(account)))??throw new InvalidDataException("Credencial cifrada inválida.");
    public void Save(string account,string taxId,string password)
    {
        if(taxId.Length!=13||!taxId.All(char.IsAsciiDigit)||string.IsNullOrWhiteSpace(password)||password.Length>1024)throw new ArgumentException("RUC o contraseña inválidos.");
        Directory.CreateDirectory(directory);var key=EncryptionKey.Read("SRI_CREDENTIAL_KEY_BASE64",keyFile);var plain=Encoding.UTF8.GetBytes(password);var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var cipher=new byte[plain.Length];var temp=PathFor(account)+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            if(key.Length!=32)throw new InvalidDataException("Clave de cifrado inválida.");
            using var aes=new AesGcm(key,16);aes.Encrypt(nonce,plain,cipher,tag,Aad(account,taxId));
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,new Envelope(1,DateTimeOffset.UtcNow,Convert.ToBase64String(nonce),Convert.ToBase64String(tag),Convert.ToBase64String(cipher)));file.Flush(true);}File.Move(temp,PathFor(account),true);
        }finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);if(File.Exists(temp))File.Delete(temp);}
    }
    public async Task Use(string account,string taxId,Func<string,Task> action)
    {
        var data=Read(account);var key=EncryptionKey.Read("SRI_CREDENTIAL_KEY_BASE64",keyFile);var cipher=Convert.FromBase64String(data.Cipher);var plain=new byte[cipher.Length];
        try{if(data.Version!=1||key.Length!=32)throw new InvalidDataException("Credencial cifrada incompatible.");using var aes=new AesGcm(key,16);aes.Decrypt(Convert.FromBase64String(data.Nonce),cipher,Convert.FromBase64String(data.Tag),plain,Aad(account,taxId));await action(Encoding.UTF8.GetString(plain));}
        catch(CryptographicException){throw new InvalidDataException("No se pudo verificar la credencial cifrada. Actualícela desde Configuración.");}
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
    public void Delete(string account){var path=PathFor(account);if(File.Exists(path))File.Delete(path);}
}
