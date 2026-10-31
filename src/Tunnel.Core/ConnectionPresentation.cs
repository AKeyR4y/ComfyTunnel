namespace ComfyTunnel;

// Pure state mapping shared by the view and regression tests.
public sealed record ConnectionPresentation(string Action, string Label, string Tone, bool Busy)
{
    public static ConnectionPresentation From(TunnelState state, bool configured) => state switch
    {
        TunnelState.Connecting => new("取消", "连接中…", "Accent", true),
        TunnelState.Retrying => new("取消重连", "正在重连…", "Warning", true),
        TunnelState.Connected => new("断开", "已连接", "Success", false),
        TunnelState.Failed => new("重试", "连接失败", "Error", false),
        _ => new(configured ? "连接" : "添加服务器", configured ? "未连接" : "尚未配置", "Muted", false)
    };
}
