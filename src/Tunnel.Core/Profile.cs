using System.Net;
using System.Text.Json.Serialization;

namespace ComfyTunnel;

public sealed record Profile
{
    public int Version { get; init; } = 1;
    public string Host { get; init; } = "";
    public int SshPort { get; init; } = 22;
    public string User { get; init; } = "";
    public bool UseKey { get; init; }
    public string KeyPath { get; init; } = "";
    public bool EnableComfy { get; init; }
    public string TargetHost { get; init; } = "127.0.0.1";
    public int TargetPort { get; init; } = 8188;
    public int LocalPort { get; init; } = 18188;
    public bool AutomaticPort { get; init; } = true;
    public bool RememberSecret { get; init; }
    public string? ProtectedSecret { get; init; }
    [JsonIgnore] public string Secret { get; init; } = "";
    public override string ToString() => "SSH connection profile"; // never expose credentials via diagnostics or UI automation

    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("配置版本不受支持。");
        if (!ValidHost(Host) || (EnableComfy && !ValidHost(TargetHost))) throw new ArgumentException("请填写有效的服务器和目标地址（不含 URL 或端口）。");
        if (string.IsNullOrWhiteSpace(User) || User.Any(char.IsWhiteSpace) || User.Any(char.IsControl))
            throw new ArgumentException("请填写有效的 SSH 用户名。");
        foreach (var port in EnableComfy ? new[] { SshPort, TargetPort, LocalPort } : new[] { SshPort })
            if (port is < 1 or > 65535) throw new ArgumentException("端口必须在 1–65535 之间。");
        if (UseKey && !File.Exists(KeyPath)) throw new ArgumentException("私钥文件不存在，请重新选择。");
    }

    public static bool ValidHost(string s) => !string.IsNullOrWhiteSpace(s) &&
        !s.Any(char.IsWhiteSpace) && !s.Any(char.IsControl) &&
        (IPAddress.TryParse(s, out _) || Uri.CheckHostName(s) == UriHostNameType.Dns);
}
