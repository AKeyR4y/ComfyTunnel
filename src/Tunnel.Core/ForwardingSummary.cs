namespace ComfyTunnel;

public static class ForwardingSummary
{
    public static string Format(bool enabled, string targetHost, int targetPort, int preferredPort, int? boundPort, TunnelState state)
    {
        if (!enabled) return "仅 SSH · 不开放本地端口";
        string host = targetHost.Contains(':') ? $"[{targetHost}]" : targetHost;
        string suffix = state == TunnelState.Connected && boundPort.HasValue ? "" : state == TunnelState.Retrying ? " · 待恢复" : " · 待连接";
        return $"127.0.0.1:{boundPort ?? preferredPort} → {host}:{targetPort}{suffix}";
    }
}
