namespace VelaShell.Core.Ssh;

/// <summary>
/// SSH 客户端的库中立抽象:Core/App 只依赖此接口与本命名空间的中立类型
/// (<see cref="SftpEntry" />、<see cref="PortForwardRequest" />、SshClientException 层级),
/// 具体 SSH 库(当前为 SSH.NET)被隔离在 Infrastructure 的实现里,更换底层库时
/// 只需提供新的实现与异常翻译。
/// </summary>
public interface ISshClientWrapper : IDisposable
{
    /// <summary>当前是否已与远程主机建立连接。</summary>
    bool IsConnected { get; }

    /// <summary>建立连接时的超时时长。</summary>
    TimeSpan ConnectionTimeout { get; set; }

    /// <summary>同步连接到远程主机。</summary>
    void Connect();

    /// <summary>异步连接到远程主机。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>断开与远程主机的连接。</summary>
    void Disconnect();

    /// <summary>
    /// 在当前连接上创建一条交互式 shell 流,使用给定的终端类型、行列尺寸、像素尺寸、缓冲区大小
    /// 及可选的终端模式参数。
    /// </summary>
    IShellStreamWrapper CreateShellStream(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IReadOnlyDictionary<TerminalMode, uint>? terminalModeValues = null);

    /// <summary>在远端主机上执行一次性命令并返回其标准输出。</summary>
    Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default);

    /// <summary>
    /// 建立并启动一条端口转发;返回的句柄负责其停止与清理。
    /// 启动失败时抛出且不留下半挂的监听。
    /// </summary>
    IPortForwardHandle StartPortForward(PortForwardRequest request);
}
