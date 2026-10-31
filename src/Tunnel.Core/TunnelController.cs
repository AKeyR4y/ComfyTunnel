namespace ComfyTunnel;

public enum TunnelState { Idle, Connecting, Connected, Retrying, Failed }
public sealed record TunnelStatus(TunnelState State, string Detail, int? LocalPort = null)
{
    public string? Url => LocalPort is int port ? $"http://127.0.0.1:{port}/" : null;
}
public sealed class PermanentConnectionException(string message) : Exception(message);
public interface ITunnelTransport : IDisposable
{
    Task<int?> ConnectAsync(Profile profile, int? previousPort, CancellationToken token);
    Task<string> ProbeAsync(CancellationToken token);
}

public sealed class TunnelController(Func<ITunnelTransport> factory, TimeSpan? probeInterval = null,
    Func<int, TimeSpan>? retryDelay = null)
{
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private readonly SemaphoreSlim wake = new(0, 1);
    public event Action<TunnelStatus>? Changed;
    public bool IsRunning { get { lock (gate) return worker is { IsCompleted: false }; } }

    public void Start(Profile profile)
    {
        profile.Validate();
        lock (gate)
        {
            if (worker is { IsCompleted: false }) throw new InvalidOperationException("已有连接流程正在运行。");
            cancellation?.Dispose();
            cancellation = new();
            var token = cancellation.Token;
            worker = Task.Run(() => RunAsync(profile, token));
        }
    }

    public async Task StopAsync()
    {
        Task? pending;
        lock (gate) { cancellation?.Cancel(); pending = worker; }
        if (pending != null) await pending.ConfigureAwait(false);
    }

    public void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }

    private async Task RunAsync(Profile profile, CancellationToken token)
    {
        int? bound = null; int attempts = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var transport = factory();
                    Emit(new(attempts == 0 ? TunnelState.Connecting : TunnelState.Retrying, "正在连接 SSH 服务器…"), token);
                    bound = await transport.ConnectAsync(profile, bound, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    Emit(new(TunnelState.Connected, profile.EnableComfy ? "隧道已建立，正在检测目标服务…" : "SSH 已连接 · ComfyUI 转发未启用", bound), token);
                    var stableSince = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        string health = await transport.ProbeAsync(token).ConfigureAwait(false);
                        Emit(new(TunnelState.Connected, health, bound), token);
                        if (stableSince.Elapsed > TimeSpan.FromSeconds(60)) attempts = 0;
                        await wake.WaitAsync(probeInterval ?? TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (PermanentConnectionException ex)
                { Emit(new(TunnelState.Failed, ex.Message), token); return; }
                catch (Exception)
                {
                    token.ThrowIfCancellationRequested();
                    attempts++;
                    var delay = retryDelay?.Invoke(attempts) ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempts, 5))) + Random.Shared.NextDouble());
                    Emit(new(TunnelState.Retrying, $"连接中断或服务器暂不可用，约 {Math.Ceiling(delay.TotalSeconds)} 秒后重试（第 {attempts} 次）。"), token);
                    await wake.WaitAsync(delay, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (token.IsCancellationRequested) Changed?.Invoke(new(TunnelState.Idle, "已断开。")); }
    }

    private void Emit(TunnelStatus status, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Changed?.Invoke(status); }
}
