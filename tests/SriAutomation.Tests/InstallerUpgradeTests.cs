namespace SriAutomation.Tests;

public sealed class InstallerUpgradeTests
{
    [Fact]
    public void InstallerKeepsExistingIdentityAndDoesNotInstallUserSettings()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var setup = File.ReadAllText(Path.Combine(root, "installer", "Bot.iss"));
        var window = File.ReadAllText(Path.Combine(root, "src", "Bot", "MainWindow.xaml.cs"));
        Assert.Contains("AppId={{B2411B51-2C2D-47B1-887A-495C8974E3C5}", setup);
        Assert.Contains("UsePreviousAppDir=yes", setup);
        Assert.Contains("CloseApplications=yes", setup);
        Assert.DoesNotContain("CloseApplications=force", setup);
        Assert.DoesNotContain("[InstallDelete]", setup);
        Assert.DoesNotContain("config.dat", setup);
        Assert.Contains("Environment.SpecialFolder.LocalApplicationData), \"Bot\", \"config.dat\"", window);
        Assert.Contains("DataProtectionScope.CurrentUser", window);
    }
}
