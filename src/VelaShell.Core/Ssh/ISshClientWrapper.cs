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

    /// <summary>
    /// 当底层 SSH 连接丢失时被取消的令牌(远端关闭/网络中断)。
    /// 提供快速断线检测,无需轮询或定时器。
    /// </summary>
    CancellationToken Disconnected { get; }

    /// <summary>建立连接时的超时时长。</summary>
    TimeSpan ConnectionTimeout { get; set; }

    /// <summary>异步连接到远程主机。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>断开与远程主机的连接。</summary>
    void Disconnect();

    /// <summary>
    /// 在当前连接上异步创建一条交互式 shell 流(打开通道 + pty-req + shell,2~3 个网络往返),
    /// 使用给定的终端类型、行列尺寸、像素尺寸、缓冲区大小及可选的终端模式参数。
    /// </summary>
    Task<IShellStreamWrapper> CreateShellStreamAsync(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IReadOnlyDictionary<TerminalMode, uint>? terminalModeValues = null,
        CancellationToken cancellationToken = default);

    /// <summary>在远端主机上执行一次性命令并返回其标准输出。</summary>
    Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步建立并启动一条端口转发;返回的句柄负责其停止与清理。
    /// 启动失败时抛出且不留下半挂的监听。
    /// </summary>
    Task<IPortForwardHandle> StartPortForwardAsync(PortForwardRequest request, CancellationToken cancellationToken = default);
}
