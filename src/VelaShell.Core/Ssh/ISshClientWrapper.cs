namespace VelaShell.Core.Ssh;

/// <summary>
/// SSH 客户端的库中立抽象:Core/App 只依赖此接口与本命名空间的中立类型
/// (<see cref="SftpEntry" />、<see cref="PortForwardRequest" />、SshClientException 层级),
/// 具体 SSH 库(当前为 SSH.NET)被隔离在 Infrastructure 的实现里,更换底层库时
/// 只需提供新的实现与异常翻译。
/// </summary>
public interface ISshClientWrapper : IDisposable
{
    bool IsConnected { get; }

    TimeSpan ConnectionTimeout { get; set; }

    void Connect();
    Task ConnectAsync(CancellationToken cancellationToken);
    void Disconnect();

    IShellStreamWrapper CreateShellStream(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IReadOnlyDictionary<TerminalMode, uint>? terminalModeValues = null);

    /// <summary>Runs a one-shot command on the remote host and returns its stdout.</summary>
    Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default);

    /// <summary>
    /// 建立并启动一条端口转发;返回的句柄负责其停止与清理。
    /// 启动失败时抛出且不留下半挂的监听。
    /// </summary>
    IPortForwardHandle StartPortForward(PortForwardRequest request);
}
