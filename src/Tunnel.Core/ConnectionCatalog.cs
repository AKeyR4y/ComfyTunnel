using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace ComfyTunnel;

public sealed record SavedConnection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "新的连接";
    public Profile Profile { get; init; } = new();
    public override string ToString() => Name;
}
public sealed record ConnectionCatalog
{
    public int Version { get; init; } = 1;
    public string? SelectedId { get; init; }
    public List<SavedConnection> Connections { get; init; } = [];
}

// Encrypted bytes are the only payload ever written, including temporary files.
[SupportedOSPlatform("windows")]
internal static class EncryptedFile
{
    public static T Read<T>(string path)
    {
        byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(clear) ?? throw new InvalidDataException("加密文件为空。"); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public static void Write<T>(string path, T data)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(data);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(encrypted); stream.Flush(true); }
        File.Move(temp, path, true);
    }
}
