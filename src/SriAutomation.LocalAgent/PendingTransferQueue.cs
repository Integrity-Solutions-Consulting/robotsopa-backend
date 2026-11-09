using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SriAutomation.LocalAgent;

// Each complete directory is published by an atomic rename. Incomplete staging
// directories are retained for inspection and count against the quota.
internal sealed class PendingTransferQueue(string root, long maxBytes = 512L * 1024 * 1024)
{
    internal sealed record Entry(string Account, string Type, string Key, DateOnly Issued, string Extension, string JobId, long Generation);
    private sealed record Manifest(int Version, string Account, string Type, string Key, DateOnly Issued,
        string Extension, string Sha256, long Length, string Status, string JobId, long Generation);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Identity(string account, string type, string key, DateOnly issued, string extension) =>
        Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { account, type, key, issued, extension })));
    private FileStream Lock()
    {
        Directory.CreateDirectory(root);
        for (var attempt = 0; ; attempt++) {
            try { return new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
        }
    }
    private static void WriteDurable(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true);
    }
    private static (Manifest manifest, byte[] bytes) Read(string directory)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(Path.Combine(directory, "manifest.json"))) ?? throw new IOException("Manifiesto de transferencia inválido.");
        var bytes = File.ReadAllBytes(Path.Combine(directory, "content"));
        if (manifest.Version != 1 || manifest.Status != "pending" || manifest.Length != bytes.LongLength || manifest.Sha256 != Hash(bytes))
            throw new IOException("La transferencia pendiente no supera la verificación de integridad; se conserva para revisión.");
        return (manifest, bytes);
    }
    private static bool Matches(Manifest m, string account, string type, string key, DateOnly issued, string extension) =>
        m.Account == account && m.Type == type && m.Key == key && m.Issued == issued && m.Extension == extension;
    public IReadOnlyList<Entry> Entries()
    {
        using var gate = Lock();
        var entries = new List<Entry>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            try {
                var (m, _) = Read(dir);
                if (Path.GetFileName(dir) != Identity(m.Account, m.Type, m.Key, m.Issued, m.Extension)) continue;
                entries.Add(new(m.Account, m.Type, m.Key, m.Issued, m.Extension, m.JobId, m.Generation));
            } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* Preserve corrupt entries for review. */ }
        }
        return entries;
    }
    public byte[]? Load(string account, string type, string key, DateOnly issued, string extension)
    {
        using var gate = Lock();
        var id = Identity(account, type, key, issued, extension);
        var directory = Path.Combine(root, id);
        if (!Directory.Exists(directory)) foreach (var staged in Directory.EnumerateDirectories(root, ".staging-" + id + "-*")) {
            try {
                var candidate = Read(staged);
                if (!Matches(candidate.manifest, account, type, key, issued, extension)) continue;
                Directory.Move(staged, directory);
                break;
            } catch (IOException) { /* Keep incomplete staging for inspection. */ }
              catch (JsonException) { /* Keep malformed staging for inspection. */ }
        }
        if (!Directory.Exists(directory)) return null;
        var (manifest, bytes) = Read(directory);
        if (!Matches(manifest, account, type, key, issued, extension)) throw new IOException("Identidad de transferencia pendiente inválida.");
        return bytes;
    }
    public void Save(string account, string type, string key, DateOnly issued, string extension, byte[] bytes, string jobId, long generation)
    {
        if (extension is not ("xml" or "pdf") || bytes.Length is 0 or > 20_000_000) throw new IOException("Archivo pendiente inválido o demasiado grande.");
        using var gate = Lock();
        var id = Identity(account, type, key, issued, extension);
        var destination = Path.Combine(root, id);
        if (Directory.Exists(destination)) {
            var (existing, content) = Read(destination);
            if (!Matches(existing, account, type, key, issued, extension) || !content.AsSpan().SequenceEqual(bytes))
                throw new IOException("Existe una transferencia pendiente diferente; se conserva para revisión.");
            return;
        }
        var manifest = new Manifest(1, account, type, key, issued, extension, Hash(bytes), bytes.LongLength, "pending", jobId, generation);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var used = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        if (used + bytes.LongLength + metadata.LongLength > maxBytes) throw new IOException("Cola de transferencias llena; no se ha confirmado el archivo.");
        var staging = Path.Combine(root, ".staging-" + id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        WriteDurable(Path.Combine(staging, "content"), bytes);
        WriteDurable(Path.Combine(staging, "manifest.json"), metadata);
        Directory.Move(staging, destination);
    }
    public void Confirm(string account, string type, string key, DateOnly issued, string extension, string sha256)
    {
        using var gate = Lock();
        var directory = Path.Combine(root, Identity(account, type, key, issued, extension));
        if (!Directory.Exists(directory)) return;
        var (manifest, _) = Read(directory);
        if (!Matches(manifest, account, type, key, issued, extension) || manifest.Sha256 != sha256)
            throw new IOException("La confirmación no coincide con la transferencia pendiente.");
        Directory.Delete(directory, true);
    }
}
