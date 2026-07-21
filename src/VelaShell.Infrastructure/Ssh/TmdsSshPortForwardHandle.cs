using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

internal sealed class TmdsSshPortForwardHandle : IPortForwardHandle
{
    private readonly IDisposable _forwardHandle;
    private readonly CancellationTokenRegistration _stoppedRegistration;
    private volatile bool _stopped;
    private volatile bool _userStopped;

    public TmdsSshPortForwardHandle(Tmds.Ssh.SshClient client, PortForwardRequest request)
    {
        switch (request.Kind)
        {
            case PortForwardKind.Local:
            {
                Tmds.Ssh.LocalForward forward = client.StartForwardAsync(
                    new System.Net.IPEndPoint(IPAddressParse(request.BoundHost), (int)request.BoundPort),
                    new Tmds.Ssh.RemoteHostEndPoint(request.TargetHost!, (int)request.TargetPort!))
                    .GetAwaiter().GetResult();
                _forwardHandle = forward;
                _stoppedRegistration = forward.Stopped.Register(() => OnStopped(forward.ThrowIfStopped));
                break;
            }
            case PortForwardKind.Remote:
            {
                Tmds.Ssh.RemoteForward forward = client.StartRemoteForwardAsync(
                    new Tmds.Ssh.RemoteIPListenEndPoint(request.BoundHost, (int)request.BoundPort),
                    new System.Net.IPEndPoint(IPAddressParse(request.TargetHost!), (int)request.TargetPort!))
                    .GetAwaiter().GetResult();
                _forwardHandle = forward;
                _stoppedRegistration = forward.Stopped.Register(() => OnStopped(forward.ThrowIfStopped));
                break;
            }
            case PortForwardKind.Dynamic:
            {
                Tmds.Ssh.SocksForward forward = client.StartSocksForwardAsync(
                    new System.Net.IPEndPoint(IPAddressParse(request.BoundHost), (int)request.BoundPort))
                    .GetAwaiter().GetResult();
                _forwardHandle = forward;
                _stoppedRegistration = forward.Stopped.Register(() => OnStopped(forward.ThrowIfStopped));
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Kind, @"Unknown port forward kind.");
        }
    }

    public bool IsStarted => !_stopped;

    public event Action<Exception>? ChannelError;

    /// <summary>
    /// 转发意外停止(连接断开/远端拒绝)时经 <see cref="ChannelError" /> 上报;
    /// 用户主动 Stop/Dispose 不算通道错误。
    /// </summary>
    private void OnStopped(Action throwIfStopped)
    {
        if (_userStopped) return;
        _stopped = true;
        try { throwIfStopped(); }
        catch (Exception ex) { ChannelError?.Invoke(TmdsSshInterop.Translate(ex) ?? ex); }
    }

    private static System.Net.IPAddress IPAddressParse(string host)
    {
        return host is "0.0.0.0" or "*"
            ? System.Net.IPAddress.Any
            : host == "::"
            ? System.Net.IPAddress.IPv6Any
            : host is "localhost" or "127.0.0.1"
            ? System.Net.IPAddress.Loopback
            : System.Net.IPAddress.Parse(host);
    }

    public void Stop()
    {
        if (_userStopped) return;
        _userStopped = true;
        _stopped = true;
        _stoppedRegistration.Dispose();
        try { _forwardHandle.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
