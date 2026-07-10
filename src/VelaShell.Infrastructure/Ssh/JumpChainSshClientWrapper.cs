using Renci.SshNet;
using Renci.SshNet.Common;
using VelaShell.Core.Ssh;
using VelaConnectionInfo = VelaShell.Core.Models.ConnectionInfo;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// SSH 跳板链(ProxyJump,§12 P1-2):按 外层跳板 → … → 目标 逐跳建立连接,每一跳在前
/// 一跳上开一个本地转发端口(127.0.0.1:随机),下一跳经该端口进入。除 Connect/Dispose 外
/// 的所有操作(shell/命令/转发端口)都落在目标跳的客户端上。
/// 主机指纹按各跳的<b>逻辑主机</b>(host:port)校验——绝不能按 127.0.0.1 记录。
/// </summary>
public sealed class JumpChainSshClientWrapper : ISshClientWrapper
{
    /// <summary>
    /// 为一跳构建 SshClient:logical = 该跳的逻辑连接信息(凭据与指纹校验键),
    /// connectHost/connectPort = 实际 socket 端点(直连时同 logical,经跳板时是本地转发口)。
    /// </summary>
    public delegate SshClient HopClientBuilder(VelaConnectionInfo logical, string connectHost, int connectPort);

    private readonly HopClientBuilder _buildHopClient;

    // 建链顺序(外层跳板在前,目标在最后);teardown 逆序。
    private readonly List<SshClient> _hopClients = [];
    private readonly List<ForwardedPortLocal> _hopForwards = [];

    private readonly VelaConnectionInfo _target;
    private bool _disposed;
    private TimeSpan? _pendingTimeout;
    private SshClient? _targetClient;

    public JumpChainSshClientWrapper(VelaConnectionInfo target, HopClientBuilder buildHopClient)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _buildHopClient = buildHopClient ?? throw new ArgumentNullException(nameof(buildHopClient));
        if (target.JumpHost is null)
        {
            throw new ArgumentException(@"Target has no jump host; use SshClientWrapper instead.", nameof(target));
        }
    }

    private SshClient Target => _targetClient ?? throw new InvalidOperationException("Jump chain is not connected yet.");

    public bool IsConnected
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _targetClient?.IsConnected == true;
        }
    }

    public TimeSpan ConnectionTimeout
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _targetClient?.ConnectionInfo.Timeout ?? _pendingTimeout ?? TimeSpan.FromSeconds(10);
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingTimeout = value;
            _targetClient?.ConnectionInfo.Timeout = value;
        }
    }

    public void Connect() => ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_targetClient?.IsConnected == true)
        {
            return;
        }

        // 链上的跳,从最外层(没有再上级跳板的那台)到目标排序。
        var hops = new List<VelaConnectionInfo>();
        for (VelaConnectionInfo? hop = _target; hop is not null; hop = hop.JumpHost)
        {
            hops.Insert(0, hop);
        }
        try
        {
            SshClient? previous = null;
            foreach (VelaConnectionInfo hop in hops)
            {
                string connectHost = hop.Host;
                int connectPort = hop.Port;
                if (previous is not null)
                {
                    // 上一跳开本地转发口,本跳经它进入(等价 OpenSSH 的 ProxyJump/-W)。
                    var forward = new ForwardedPortLocal("127.0.0.1", 0, hop.Host, (uint)hop.Port);
                    previous.AddForwardedPort(forward);
                    forward.Start();
                    _hopForwards.Add(forward);
                    connectHost = "127.0.0.1";
                    connectPort = (int)forward.BoundPort;
                }
                SshClient client = _buildHopClient(hop, connectHost, connectPort);
                if (_pendingTimeout is { } timeout)
                {
                    client.ConnectionInfo.Timeout = timeout;
                }
                _hopClients.Add(client);
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                previous = client;
            }
            _targetClient = _hopClients[^1];
        }
        catch
        {
            // 任一跳失败:整条链回收,不留下半开的跳板连接。
            TearDownChain();
            throw;
        }
    }

    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TearDownChain();
    }

    public IShellStreamWrapper CreateShellStream(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IDictionary<TerminalModes, uint>? terminalModeValues = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ShellStream shellStream = Target.CreateShellStream(terminalName, columns, rows, width, height, bufferSize, terminalModeValues);
        return new ShellStreamWrapper(shellStream);
    }

    public Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SshClient target = Target;
        return Task.Run(() =>
        {
            using SshCommand command = target.CreateCommand(commandText);
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            return command.Execute();
        }, cancellationToken);
    }

    public void AddForwardedPort(ForwardedPort port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Target.AddForwardedPort(port);
    }

    public void RemoveForwardedPort(ForwardedPort port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Target.RemoveForwardedPort(port);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        TearDownChain();
    }

    /// <summary>逆序拆链:目标 → 转发口 → 跳板;每步独立吞错,保证整条链都被回收。</summary>
    private void TearDownChain()
    {
        for (int i = _hopClients.Count - 1; i >= 0; i--)
        {
            try
            {
                if (_hopClients[i].IsConnected)
                {
                    _hopClients[i].Disconnect();
                }
            }
            catch
            {
                // 单跳断开失败不阻塞其余回收。
            }
        }
        foreach (ForwardedPortLocal forward in _hopForwards)
        {
            try
            {
                forward.Dispose();
            }
            catch
            {
                // ignore
            }
        }
        _hopForwards.Clear();
        foreach (SshClient client in _hopClients)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // ignore
            }
        }
        _hopClients.Clear();
        _targetClient = null;
    }
}
