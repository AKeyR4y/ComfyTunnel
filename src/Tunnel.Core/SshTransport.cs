using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ComfyTunnel;

public sealed record HostIdentity(string Host, int Port, string Algorithm, string Fingerprint);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SshTransport(Storage storage, Func<HostIdentity, CancellationToken, Task<bool>> confirm) : ITunnelTransport
{
    private SshClient? client;
    private ForwardedPortLocal? forward;
    private PrivateKeyFile? privateKey;
    private Exception? transportError;
    private Exception? trustError;
    private bool trustDeclined;
    private bool enableComfy;
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<int?> ConnectAsync(Profile p, int? previousPort, CancellationToken token)
    {
        try
        {
            enableComfy = p.EnableComfy;
            AuthenticationMethod method;
            if (p.UseKey)
            {
                try
                {
                    privateKey = string.IsNullOrEmpty(p.Secret) ? new PrivateKeyFile(p.KeyPath) : new PrivateKeyFile(p.KeyPath, p.Secret);
                    method = new PrivateKeyAuthenticationMethod(p.User, privateKey);
                }
                catch (Exception) { throw new PermanentConnectionException("无法读取私钥，请检查文件格式、访问权限和私钥口令。"); }
            }
            else method = new PasswordAuthenticationMethod(p.User, p.Secret);
            var info = new ConnectionInfo(p.Host, p.SshPort, p.User, method) { Timeout = TimeSpan.FromSeconds(15) };
            client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
            client.ErrorOccurred += (_, e) => Interlocked.CompareExchange(ref transportError, e.Exception, null);
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                    e.CanTrust = storage.VerifyHost(p.Host, p.SshPort, fingerprint,
                        () => confirm(new(p.Host, p.SshPort, e.HostKeyName, fingerprint), token).GetAwaiter().GetResult() && !token.IsCancellationRequested);
                    trustDeclined = !e.CanTrust;
                }
                catch (Exception ex) { trustError = ex; }
            };
            await client.ConnectAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!enableComfy) return null;
            int wanted = previousPort ?? p.LocalPort;
            try { StartForward(p, (uint)wanted); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && p.AutomaticPort && previousPort == null)
            { RemoveForward(); StartForward(p, 0); }
            return checked((int)forward!.BoundPort);
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception) when (trustError != null)
        { throw new PermanentConnectionException(trustError is HostTrustException ? trustError.Message : "无法验证或保存服务器指纹。请检查本地指纹文件与权限。"); }
        catch (Exception) when (trustDeclined) { throw new PermanentConnectionException("未信任服务器，连接已取消。"); }
        catch (SshAuthenticationException) { throw new PermanentConnectionException("SSH 认证失败，请检查用户名、密码或私钥。"); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        { throw new PermanentConnectionException("本地端口被占用或被系统保留，请在高级设置中更换端口。"); }
    }

    private void StartForward(Profile p, uint port)
    {
        forward = new ForwardedPortLocal("127.0.0.1", port, p.TargetHost, (uint)p.TargetPort);
        // A channel-open timeout is lack of an SSH response. A refused target is not a dead SSH link.
        forward.Exception += (_, e) =>
        {
            if (e.Exception is SshOperationTimeoutException or SshConnectionException)
                Interlocked.CompareExchange(ref transportError, e.Exception, null);
        };
        client!.AddForwardedPort(forward);
        forward.Start();
    }

    public async Task<string> ProbeAsync(CancellationToken token)
    {
        CheckTransport();
        token.ThrowIfCancellationRequested();
        if (!enableComfy) return "SSH 已连接 · ComfyUI 转发未启用";
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{forward!.BoundPort}/", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            CheckTransport();
            return $"隧道已连接 · 目标 HTTP {(int)response.StatusCode}（不代表已确认 ComfyUI 身份）";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            CheckTransport();
            return "隧道已建立 · 目标暂不可达，请确认远端 ComfyUI 已启动。";
        }
    }

    private void CheckTransport()
    {
        if (Volatile.Read(ref transportError) != null || client?.IsConnected != true || (enableComfy && forward?.IsStarted != true))
            throw new IOException("SSH transport unavailable.");
    }

    private void RemoveForward()
    {
        if (forward == null) return;
        try { client?.RemoveForwardedPort(forward); } catch { }
        try { forward.Dispose(); } catch { }
        forward = null;
    }

    public void Dispose()
    {
        // Disconnect first to unblock pending channel-open operations before disposing the listener.
        try { client?.Dispose(); } catch { }
        RemoveForward(); privateKey?.Dispose(); http.Dispose();
    }
}
