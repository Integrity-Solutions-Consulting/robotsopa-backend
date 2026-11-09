using System.Windows;
namespace Bot;
public partial class App : Application
{
    public App() => Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", "0");
}
