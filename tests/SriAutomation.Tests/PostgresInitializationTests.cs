using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public class PostgresInitializationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(1025)]
    public void RejectsInvalidPasswordLengthBeforeConnecting(int length)
    {
        var password = new string('x', length);
        var error = Assert.Throws<ArgumentException>(() => new PostgresPanelDatabase("invalid").Initialize("Cristopher", password));
        Assert.Contains($"({length})", error.Message);
        if (length > 0) Assert.DoesNotContain(password, error.Message);
    }

    [Fact]
    public void ValidPasswordPassesValidationAndAttemptsConnection()
    {
        var error = Record.Exception(() => new PostgresPanelDatabase("invalid").Initialize("Cristopher", "valid-test-password-123"));
        Assert.NotNull(error);
        Assert.DoesNotContain("contraseña", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
