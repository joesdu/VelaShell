using Renci.SshNet;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="IPortForwardHandle" /> 的 SSH.NET 实现:构造即把 ForwardedPort 挂到客户端并
/// 启动,失败时摘除不留半挂监听;Stop/Dispose 幂等,客户端已随会话释放时静默成功。
/// </summary>
internal sealed class SshNetPortForwardHandle : IPortForwardHandle
{
    private readonly SshClient _client;
    private readonly ForwardedPort _port;
    private bool _stopped;

    public SshNetPortForwardHandle(SshClient client, ForwardedPort port)
    {
        _client = client;
        _port = port;
        _port.Exception += OnPortException;
        _client.AddForwardedPort(port);
        try
        {
            _port.Start();
        }
        catch
        {
            _port.Exception -= OnPortException;
            try
            {
                _client.RemoveForwardedPort(port);
            }
            catch
            {
                // 启动失败后的摘除只是尽力而为;原始启动异常照常上抛。
            }
            throw;
        }
    }

    public bool IsStarted => !_stopped && _port.IsStarted;

    public event Action<Exception>? ChannelError;

    public void Stop()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        _port.Exception -= OnPortException;
        try
        {
            _port.Stop();
            _client.RemoveForwardedPort(_port);
        }
        catch
        {
            // 会话已断开、客户端已释放:监听端口随之失效,无需(也无法)再摘除。
        }
    }

    public void Dispose()
    {
        Stop();
        _port.Dispose();
    }

    private void OnPortException(object? sender, Renci.SshNet.Common.ExceptionEventArgs e) => ChannelError?.Invoke(e.Exception);
}
