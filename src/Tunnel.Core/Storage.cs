using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace ComfyTunnel;

[SupportedOSPlatform("windows")]
public sealed class Storage(string directory)
{
    private readonly object gate = new();
    public string DirectoryPath { get; } = directory;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComfyTunnel");

    [SupportedOSPlatform("windows")]
    private Profile LoadLegacyProfile()
    {
        string path = Path.Combine(DirectoryPath, "profile.json");
        if (!File.Exists(path)) return new();
        var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path)) ?? throw new InvalidDataException("配置为空。");
        if (profile.Version != 1) throw new InvalidDataException("配置版本不受支持。");
        if (profile.RememberSecret && profile.ProtectedSecret != null)
        {
            byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(profile.ProtectedSecret), null, DataProtectionScope.CurrentUser);
            try { profile = profile with { Secret = Encoding.UTF8.GetString(clear) }; }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        return profile;
    }

    [SupportedOSPlatform("windows")]
    private static Profile PrepareForStorage(Profile profile)
    {
        string? encrypted = null;
        if (profile.RememberSecret)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(profile.Secret);
            try { encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return profile with { ProtectedSecret = encrypted, Secret = "" };
    }

    public Profile LoadProfile()
    {
        var catalog = LoadConnections();
        return (catalog.Connections.Find(c => c.Id == catalog.SelectedId) ?? catalog.Connections.FirstOrDefault())?.Profile ?? new();
    }
    public void SaveProfile(Profile profile)
    {
        var entry = new SavedConnection { Name = "默认连接", Profile = profile };
        SaveConnections(new() { SelectedId = entry.Id, Connections = [entry] });
    }
    public ConnectionCatalog LoadConnections()
    {
        lock (gate)
        {
            _ = ReadHosts(); // migrate old host metadata on startup, not only on the next connection
            string path = Path.Combine(DirectoryPath, "connections.dat");
            string legacy = Path.Combine(DirectoryPath, "profile.json");
            if (!File.Exists(path))
            {
                if (!File.Exists(legacy)) return new();
                var entry = new SavedConnection { Name = "已迁移的连接", Profile = LoadLegacyProfile() };
                SaveConnections(new() { SelectedId = entry.Id, Connections = [entry] });
            }
            var result = EncryptedFile.Read<ConnectionCatalog>(path);
            if (result.Version != 1 || result.Connections == null || result.Connections.Count > 500 ||
                result.Connections.Any(c => c == null || c.Profile == null || string.IsNullOrEmpty(c.Id)) ||
                result.Connections.Select(c => c.Id).Distinct().Count() != result.Connections.Count)
                throw new InvalidDataException("连接资料格式不受支持。");
            var entries = result.Connections.Select(c =>
            {
                var p = c.Profile;
                if (p.RememberSecret && p.ProtectedSecret != null)
                {
                    byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(p.ProtectedSecret), null, DataProtectionScope.CurrentUser);
                    try { p = p with { Secret = Encoding.UTF8.GetString(bytes) }; }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                return c with { Profile = p };
            }).ToList();
            if (File.Exists(legacy)) File.Delete(legacy); // encrypted replacement has been read successfully
            return result with { Connections = entries };
        }
    }
    public void SaveConnections(ConnectionCatalog catalog)
    {
        if (catalog.Version != 1) throw new InvalidDataException("连接资料版本不受支持。");
        lock (gate)
        {
            EncryptedFile.Write(Path.Combine(DirectoryPath, "connections.dat"), catalog with
            { Connections = catalog.Connections.Select(c => c with { Profile = PrepareForStorage(c.Profile) }).ToList() });
            string legacy = Path.Combine(DirectoryPath, "profile.json");
            if (File.Exists(legacy)) File.Delete(legacy);
        }
    }

    public bool VerifyHost(string host, int port, string fingerprint, Func<bool> confirm)
    {
        lock (gate)
        {
            string path = Path.Combine(DirectoryPath, "known-hosts.dat");
            var hosts = ReadHosts();
            string id = $"[{host.ToLowerInvariant()}]:{port}";
            if (hosts.TryGetValue(id, out var saved))
            {
                if (saved != fingerprint) throw new HostTrustException("服务器指纹已变化，连接已阻止。请核实服务器身份；不要直接忽略此错误。");
                return true;
            }
            if (!confirm()) return false;
            hosts[id] = fingerprint;
            EncryptedFile.Write(path, hosts);
            return true;
        }
    }

    private Dictionary<string, string> ReadHosts()
    {
        string path = Path.Combine(DirectoryPath, "known-hosts.dat");
        string legacy = Path.Combine(DirectoryPath, "known-hosts.json");
        var hosts = File.Exists(path) ? EncryptedFile.Read<Dictionary<string, string>>(path)
            : File.Exists(legacy) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(legacy))
                ?? throw new InvalidDataException("主机指纹文件为空。") : new();
        if (File.Exists(legacy)) { EncryptedFile.Write(path, hosts); File.Delete(legacy); }
        return hosts;
    }

}

public sealed class HostTrustException(string message) : Exception(message);

public sealed class SafeLog(string directory)
{
    private readonly object gate = new();
    // Only application-owned status text is accepted. Never pass raw SSH errors or credentials.
    public void Write(string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "tunnel.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    for (int i = 3; i >= 1; i--)
                        if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                    File.Move(path, path + ".1", true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
