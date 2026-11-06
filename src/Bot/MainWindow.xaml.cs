using System.Net.Http;
using System.IO;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using SriAutomation.LocalAgent;

namespace Bot;
public partial class MainWindow : Window
{
    private readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bot", "config.dat");
    private CancellationTokenSource? stop;
    private Task? running;
    private bool closing;
    private sealed record Settings(string Api, string AgentId, string Secret);
    private sealed record Status(bool Authenticated, bool ExecutionAvailable);
    public MainWindow()
    {
        InitializeComponent();
        Console.SetOut(new UiWriter(this, false)); Console.SetError(new UiWriter(this, true));
        try { if (File.Exists(settingsFile)) { var s = JsonSerializer.Deserialize<Settings>(ProtectedData.Unprotect(File.ReadAllBytes(settingsFile), null, DataProtectionScope.CurrentUser)); if (s is not null) { Api.Text = s.Api; AgentId.Text = s.AgentId; Secret.Password = s.Secret; } } }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException) { Append("No se pudo leer la configuración guardada.", true); }
    }
    private Settings ReadSettings()
    {
        var address = Api.Text.Trim(); var id = AgentId.Text.Trim(); var secret = Secret.Password.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException("Introduzca una URL HTTPS válida, sin credenciales ni parámetros.");
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("El ID del agente debe ser un GUID válido.");
        if (secret.Length != 64 || !secret.All(Uri.IsHexDigit)) throw new ArgumentException("El secreto debe contener 64 caracteres hexadecimales.");
        return new(address, id, secret);
    }
    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        try { var s = ReadSettings(); Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(s), null, DataProtectionScope.CurrentUser); File.WriteAllBytes(settingsFile, bytes); Append("Configuración guardada para este usuario de Windows.", false); }
        catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException or UnauthorizedAccessException) { MessageBox.Show(ex.Message, "Bot", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void TestConnection(object sender, RoutedEventArgs e)
    {
        try {
            var s = ReadSettings(); using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(s.Api), Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("X-Delivery-Agent-Id", s.AgentId); client.DefaultRequestHeaders.Add("X-Delivery-Agent-Secret", s.Secret);
            using var response = await client.GetAsync("/execution-agent/v1/status"); response.EnsureSuccessStatusCode(); var status = await response.Content.ReadFromJsonAsync<Status>();
            if (status?.Authenticated != true) throw new InvalidOperationException("La API no confirmó la identidad.");
            Connection.Text = status.ExecutionAvailable ? "Conexión: autenticada; ejecución disponible" : "Conexión: autenticada; ejecución no habilitada"; LastContact.Text = "Última comunicación: " + DateTime.Now.ToString("g");
        } catch (Exception ex) when (ex is ArgumentException or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { Connection.Text = "Conexión: no disponible"; Append("No se pudo verificar la conexión: " + ex.GetType().Name, true); }
    }
    private async void Start(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        Settings s; try { s = ReadSettings(); } catch (ArgumentException ex) { MessageBox.Show(ex.Message, "Bot"); return; }
        Environment.SetEnvironmentVariable("SRI_AGENT_API", s.Api); Environment.SetEnvironmentVariable("SRI_DELIVERY_AGENT_ID", s.AgentId); Environment.SetEnvironmentVariable("SRI_DELIVERY_AGENT_SECRET", s.Secret);
        stop = new CancellationTokenSource(); StartButton.IsEnabled = false; StopButton.IsEnabled = true; AgentStatus.Text = "Agente: iniciado";
        try { running = Task.Run(() => RemoteWorker.RunAsync(stop.Token)); await running; }
        catch (OperationCanceledException) { Append("Agente detenido.", false); }
        catch (Exception ex) { Append("El agente se detuvo: " + ex.GetType().Name, true); }
        finally { running = null; stop.Dispose(); stop = null; StartButton.IsEnabled = true; StopButton.IsEnabled = false; AgentStatus.Text = "Agente: detenido"; if (closing) Close(); }
    }
    private void Stop(object sender, RoutedEventArgs e) { StopButton.IsEnabled = false; AgentStatus.Text = "Agente: deteniendo"; stop?.Cancel(); }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (running is not null) { e.Cancel = true; closing = true; stop?.Cancel(); AgentStatus.Text = "Agente: deteniendo"; } base.OnClosing(e); }
    private void Append(string message, bool error) { Dispatcher.BeginInvoke(() => { Log.AppendText($"{DateTime.Now:g} {(error ? "Error: " : "")}{message}\n"); Log.ScrollToEnd(); if (message.Contains("Trabajo recibido")) CurrentWork.Text = "Trabajo y progreso: procesando consulta"; if (message.Contains("Trabajo finalizado")) { CurrentWork.Text = "Trabajo y progreso: finalizado"; LastRun.Text = "Última ejecución: " + DateTime.Now.ToString("g"); } }); }
    private sealed class UiWriter(MainWindow window, bool error) : TextWriter { public override Encoding Encoding => Encoding.UTF8; public override void WriteLine(string? value) { if (!string.IsNullOrWhiteSpace(value)) window.Append(value, error); } }
}


