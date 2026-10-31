namespace ComfyTunnel;

public static class ProfileFields
{
    public static Dictionary<string, string> Validate(string host, string sshPort, string user,
        string targetHost, string targetPort, string localPort, bool useKey, string keyPath, bool requireComplete, bool enableComfy = true)
    {
        var errors = new Dictionary<string, string>();
        if ((!string.IsNullOrWhiteSpace(host) || requireComplete) && !Profile.ValidHost(host)) errors["Host"] = "请输入服务器域名或 IP，不要包含协议和端口。";
        if ((!string.IsNullOrWhiteSpace(user) || requireComplete) && (string.IsNullOrWhiteSpace(user) || user.Any(char.IsWhiteSpace) || user.Any(char.IsControl))) errors["User"] = "请输入有效的 SSH 用户名。";
        if (enableComfy && !Profile.ValidHost(targetHost)) errors["TargetHost"] = "请输入目标服务的域名或 IP。";
        foreach (var (key, value) in enableComfy ? new[] { ("SshPort", sshPort), ("TargetPort", targetPort), ("LocalPort", localPort) } : new[] { ("SshPort", sshPort) })
            if (!int.TryParse(value, out int port) || port is < 1 or > 65535) errors[key] = "请输入 1–65535 之间的端口。";
        if (useKey && requireComplete && !File.Exists(keyPath)) errors["KeyPath"] = "私钥文件不存在，请重新选择。";
        return errors;
    }
}
