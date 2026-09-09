using System.Security.Cryptography;
using Tmds.Ssh;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// SSH agent 转发的数据面。
///
/// <para><b>为什么不是 <c>auth-agent-req@openssh.com</c>。</b></para>
/// 标准做法是在 session 通道上发一个 <c>auth-agent-req@openssh.com</c> 请求,之后由**服务端**
/// 反向开 <c>auth-agent@openssh.com</c> 通道回来。<b>Tmds.Ssh 0.24.0 不支持这条路</b> ——
/// 程序集里既没有这两个通道名,也没有任何"接受服务端发起的通道"的公开面
/// (2026-09-08 核过 0.24.0 的公开类型与字符串常量;上游也没有相关 issue)。
/// 按 <c>AGENTS.md</c> 的纪律,这种时候不在 <c>Infrastructure/Ssh/</c> 外面绕。
///
/// <para><b>于是走它支持的那条路:远端 unix 套接字转发。</b></para>
/// <c>SshClient.ListenUnixAsync</c>(SSH 的 <c>streamlocal-forward@openssh.com</c>)让 sshd 在
/// **远端**建一个 unix 域套接字,任何连上它的字节都被送回本机。把这个套接字的路径写进远端的
/// <c>SSH_AUTH_SOCK</c>,对端的 <c>ssh</c> 就会像用本地 agent 一样用它 —— 效果与 <c>ssh -A</c> 等价,
/// 用的却全是库已经支持的公开 API。代价有三,都记在这里:
/// <list type="bullet">
///   <item>远端必须是 POSIX(要有 unix 套接字与 <c>$HOME</c>);非 POSIX 时调用方不应启用。</item>
///   <item>
///     sshd 必须同时允许 <c>AllowTcpForwarding</c> 与 <c>AllowStreamLocalForwarding</c>
///     (两者默认都是 yes)。⚠️ 前者也管得着这条通道 —— 反直觉,但实测如此,
///     见 <see cref="ForwardingDeniedMessage" /> 的注释。**这是本方案相对协议原生
///     `auth-agent-req@openssh.com` 的实质差距**:后者归 `AllowAgentForwarding` 管。
///   </item>
///   <item><c>SSH_AUTH_SOCK</c> 要由我们注入 —— sshd 不知道这个套接字是个 agent。</item>
/// </list>
///
/// <para><b>比 <c>ssh -A</c> 严一点。</b></para>
/// 每条请求都过一遍 <see cref="SshAgentRelay" /> 的策略闸门:只放行「列举身份」与「签名」,
/// 远端想清空 / 上锁 / 加钥匙一律就地回绝(见 <see cref="SshAgentProtocol.IsAllowed" />),
/// 并且逐条计数 —— 「这条会话上远端动了几次你的钥匙」当场答得上来。
/// </summary>
internal sealed class SshAgentForwardHandle : IAgentForwardHandle
{
    /// <summary>
    /// 远端存放套接字的目录,相对 <c>$HOME</c>。
    /// <para>
    /// **不放 <c>/tmp</c>**:sshd 按登录会话的 umask 建套接字,通常是 <c>srwxr-xr-x</c> ——
    /// 摆在 <c>/tmp</c> 里等于同机任何用户都能连上来用你的钥匙签名。家目录下一个
    /// <c>700</c> 的子目录才是 OpenSSH 自己那套(<c>/tmp/ssh-XXXX/</c> 也是 700)的等价物:
    /// 套接字本身的权限管不住,靠父目录管。
    /// </para>
    /// </summary>
    private const string RemoteDirectory = ".velashell/agent";

    /// <summary>
    /// 准备远端目录并回显其绝对路径的一条命令。
    /// <para>
    /// <c>umask 077</c> 与显式 <c>chmod</c> 都写上:<c>mkdir -m</c> 在某些 shell 内建实现下
    /// 对已存在的目录不起作用,而这个目录**很可能已经存在**(同一台机器连第二次)。
    /// 权限没收紧就直接开转发,等于把上面那段注释白写了。
    /// </para>
    /// </summary>
    private const string PrepareCommand =
        $"""umask 077; mkdir -p "$HOME/{RemoteDirectory}" && chmod 700 "$HOME/{RemoteDirectory}" && printf %s "$HOME/{RemoteDirectory}" """;

    /// <summary>准备/清理这类小命令的超时:一次 exec 往返而已。</summary>
    private static readonly TimeSpan HousekeepingTimeout = TimeSpan.FromSeconds(10);

    private readonly ISshAgentClient _agent;
    private readonly SshClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly RemoteListener _listener;
    private int _deniedRequests;
    private volatile bool _disposed;
    private int _signRequests;
    private int _totalConnections;

    private SshAgentForwardHandle(SshClient client, ISshAgentClient agent, RemoteListener listener, string remoteSocketPath)
    {
        _client = client;
        _agent = agent;
        _listener = listener;
        RemoteSocketPath = remoteSocketPath;
        _ = AcceptLoopAsync();
    }

    /// <inheritdoc />
    public string RemoteSocketPath { get; }

    /// <inheritdoc />
    public bool IsRunning => !_disposed;

    /// <inheritdoc />
    public int TotalConnections => Volatile.Read(ref _totalConnections);

    /// <inheritdoc />
    public int SignRequests => Volatile.Read(ref _signRequests);

    /// <inheritdoc />
    public int DeniedRequests => Volatile.Read(ref _deniedRequests);

    /// <inheritdoc />
    public event Action<AgentForwardRequestEvent>? RequestHandled;

    /// <summary>
    /// 在远端建好套接字并开始转发。
    /// </summary>
    /// <param name="client">已连接的 Tmds.Ssh 客户端。</param>
    /// <param name="agent">本机 agent 客户端。</param>
    /// <param name="cancellationToken">取消令牌(只作用于建立阶段)。</param>
    /// <returns>已启动的转发句柄。</returns>
    /// <exception cref="InvalidOperationException">远端目录准备失败,或本机没有可用 agent。</exception>
    public static async Task<SshAgentForwardHandle> CreateAsync(
        SshClient client, ISshAgentClient agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(agent);

        // 先确认本机确实有 agent 可转发。没有就别去远端建目录建套接字 ——
        // 那会留下一个连上去只会失败的 SSH_AUTH_SOCK,比不开更糟:
        // 用户的 ssh 会以为有 agent,试完 agent 才回落到密码,平白多一轮失败。
        SshAgentProbe probe = await agent.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.IsAvailable)
        {
            throw new InvalidOperationException(probe.Error ?? "No local SSH agent is available.");
        }

        string directory = await PrepareRemoteDirectoryAsync(client, cancellationToken).ConfigureAwait(false);
        // 随机名,不带 pid:同一台机器上开多个会话各转发各的,重名会让后开的那条直接失败。
        string socketPath = $"{directory}/agent-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}.sock";
        RemoteListener listener;
        try
        {
            listener = await client.ListenUnixAsync(socketPath, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException ex) when (ex.Message.Contains("REQUEST_FAILURE", StringComparison.Ordinal))
        {
            // 对端拒绝开这条转发。Tmds 只能如实转述 "SSH_MSG_REQUEST_FAILURE",
            // 而那句话对用户毫无用处 —— 真正要看的是服务端的两条指令。
            throw new InvalidOperationException(ForwardingDeniedMessage, ex);
        }
        return new(client, agent, listener, socketPath);
    }

    /// <summary>
    /// 服务端拒绝开转发时给出的解释。
    /// </summary>
    /// <remarks>
    /// <b>两条指令都要提,而且 `AllowTcpForwarding` 排在前面 —— 这是实测出来的。</b>
    /// 直觉会认为 unix 套接字转发只归 `AllowStreamLocalForwarding` 管,但在
    /// linuxserver/openssh-server(Alpine 的 OpenSSH)上做过 A/B:
    /// `AllowStreamLocalForwarding yes` + `AllowTcpForwarding no` 时,
    /// `streamlocal-forward@openssh.com` 照样被拒(sshd 日志:"Received request … to remote
    /// forward to path …, but the request was denied."),把 `AllowTcpForwarding` 打开就通了。
    /// <para>
    /// 这也是本方案相对协议原生 `auth-agent-req@openssh.com` 的**实质差距**:
    /// 后者归 `AllowAgentForwarding` 管,在关掉 TCP 转发的机器上仍然可用,而我们不行。
    /// 上游哪天支持了原生通道,这一条就是换过去的理由。
    /// </para>
    /// </remarks>
    private const string ForwardingDeniedMessage =
        "The server refused to open the agent forwarding socket. Check sshd_config on the remote host: "
        + "agent forwarding needs both `AllowTcpForwarding yes` and `AllowStreamLocalForwarding yes` "
        + "(sshd denies stream-local forwarding when TCP forwarding is off, even if stream-local is allowed).";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // 停止路径上的失败一律吞掉:要停的东西本来就在停,而 SSH 连接此刻多半已经没了。
        try { await _cts.CancelAsync().ConfigureAwait(false); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Dispose(); } catch { }
        await TryRemoveRemoteSocketAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    /// <summary>
    /// 在远端建好 <c>700</c> 的存放目录,返回其绝对路径。
    /// </summary>
    private static async Task<string> PrepareRemoteDirectoryAsync(SshClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HousekeepingTimeout);
        using RemoteProcess process = await client.ExecuteAsync(PrepareCommand, timeout.Token).ConfigureAwait(false);
        (string? stdout, string? stderr) = await process
            .ReadToEndAsStringAsync(readStdout: true, readStderr: true, timeout.Token)
            .ConfigureAwait(false);
        int exitCode = await process.GetExitCodeAsync(timeout.Token).ConfigureAwait(false);
        string path = stdout?.Trim() ?? string.Empty;
        // 路径必须是绝对的:$HOME 展不开时 printf 打出来的是 "/.velashell/agent" 甚至空串,
        // 拿它去 bind 会在根目录下留东西或直接失败 —— 两种都不该悄悄发生。
        return exitCode == 0 && path.StartsWith('/') && path.Length > RemoteDirectory.Length + 1
                   ? path
                   : throw new InvalidOperationException(
                       $"Cannot prepare the remote agent socket directory (exit {exitCode}): {stderr?.Trim()}");
    }

    /// <summary>接受远端发起的每一条 agent 连接,各自起一条搬运任务。</summary>
    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using RemoteConnection connection = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
                if (!connection.HasStream)
                {
                    break; // 监听已停(会话断开或我们自己停的)
                }
                Interlocked.Increment(ref _totalConnections);
                Stream stream = connection.MoveStream();
                // 逐连接并发:对端一次 `ssh` 可能同时开好几条,串行会让它们互相等。
                _ = RelayAsync(stream);
            }
        }
        catch (Exception)
        {
            // 监听塌了就是转发结束。会话断开时这里必然抛,不是异常情况,更不该带倒调用方。
        }
    }

    /// <summary>把一条远端连接接到本机 agent 上,中间过策略闸门。</summary>
    private async Task RelayAsync(Stream remote)
    {
        try
        {
            await SshAgentRelay
                .PumpAsync(remote, _agent.ConnectAsync, OnRequestHandled, _cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 一条连接坏掉不影响其它连接,也不该冒到 UI 上 —— 对端下一次 ssh 会重开一条。
        }
        finally
        {
            try { await remote.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>计数并转发事件;订阅方抛出不得影响搬运循环。</summary>
    private void OnRequestHandled(AgentForwardRequestEvent e)
    {
        if (!e.Allowed)
        {
            Interlocked.Increment(ref _deniedRequests);
        }
        else if (e.MessageType == SshAgentProtocol.SignRequest)
        {
            Interlocked.Increment(ref _signRequests);
        }
        try
        {
            RequestHandled?.Invoke(e);
        }
        catch (Exception)
        {
            // 审计写库失败不该让远端的这次签名"看起来失败了"——签名已经发回去了。
        }
    }

    /// <summary>
    /// 尽力删掉远端那个套接字。sshd 在取消转发时通常会自己 unlink,但连接被硬断时不会 ——
    /// 留下的死套接字会在下一次登录时让用户看到一个自己没建过的文件。
    /// </summary>
    private async Task TryRemoveRemoteSocketAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(HousekeepingTimeout);
            using RemoteProcess process = await _client
                .ExecuteAsync($"rm -f '{RemoteSocketPath.Replace("'", "'\\''", StringComparison.Ordinal)}'", timeout.Token)
                .ConfigureAwait(false);
            await process.GetExitCodeAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 会话已经断了就没法清 —— 这本来就是尽力而为,失败不值得打扰任何人。
        }
    }
}
