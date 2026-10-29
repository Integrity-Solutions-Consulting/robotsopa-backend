using System.Security.Cryptography;
using SriAutomation.Infrastructure;
using Xunit;

namespace SriAutomation.Tests;

public class EncryptionKeyTests
{
    [Fact]
    public void Base64AndFileReturnTheSameOriginalBytesAndRejectInvalidConfiguration()
    {
        const string variable = "SRI_TEST_KEY_BASE64_UNIQUE";
        var path = Path.GetTempFileName();
        var original = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllBytes(path, original);
            Environment.SetEnvironmentVariable(variable, null);
            Assert.Equal(original, EncryptionKey.Read(variable, path));
            Environment.SetEnvironmentVariable(variable, Convert.ToBase64String(original));
            Assert.Equal(original, EncryptionKey.Read(variable, path));
            Environment.SetEnvironmentVariable(variable, "invalid!");
            Assert.Throws<InvalidOperationException>(() => EncryptionKey.Read(variable, path));
            Environment.SetEnvironmentVariable(variable, Convert.ToBase64String(new byte[31]));
            Assert.Throws<InvalidOperationException>(() => EncryptionKey.Read(variable, path));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            File.Delete(path);
            CryptographicOperations.ZeroMemory(original);
        }
    }
}
