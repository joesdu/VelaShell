using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Core.XServer;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Session;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="ISshClientWrapper" /> 的 VelaShell.Ssh 实现。
/// </summary>
/// <remarks>
/// <para>
/// 与它替掉的 <c>TmdsSshClientWrapper</c> 相比,少掉的**不是功能,是绕路**:
/// </para>
/// <list type="bullet">
///   <item><b>代理不再走环回中继</b> —— <see cref="ProxyTransportDialer" /> 直接把代理
///     隧道交给库,本机不再开监听端口。</item>
///   <item><b>没有「补连一次」</b> —— 指纹弹窗的时间不计入连接超时,
///     所以不存在「用户点了信任、这一轮却已被判死」。</item>
///   <item><b>没有算法回探</b> —— 协商失败时异常自带双方名单。</item>
///   <item><b>没有字符串解析</b> —— 失败原因是强类型的。</item>
/// </list>
/// </remarks>
public sealed class VelaSshClientWrapper : ISshClientWrapper
{
    private readonly Func<CancellationToken, ValueTask<SshConnection>> _connect;
    private readonly SshServerBanners? _banners;
    private readonly SshSessionOptions? _features;
    private readonly ILocalXServer? _localXServer;
    private readonly IAgentSignPrompt? _agentPrompt;
    private readonly IHostKeyService? _hostKeys;
    private readonly string _target;
    private SshConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// 用一个「怎么建立连接」的工厂构造。
    /// </summary>
    /// <param name="connect">建立连接(含跳板链、代理、主机密钥裁决与认证)。</param>
    /// <param name="connectTimeout">建链超时,用于 <see cref="ConnectionTimeout" /> 的初值。</param>
    /// <param name="features">
    /// 交互式 shell 上要请求的转发(X11 / agent);<see langword="null" /> = 都不请求。
    /// 压缩不在这里 —— 它是建链时协商的,已经装进 <paramref name="connect" /> 了。
    /// </param>
    /// <param name="localXServer">
    /// VelaShell 管理的本机 X Server;开 X11 转发而配置里没写显示地址时,用它的显示(必要时先启动它)。
    /// <see langword="null" /> = 不接管,按 <c>DISPLAY</c> 与默认值走。
    /// </param>
    /// <param name="agentPrompt">agent 转发开了「逐次确认」时用来问用户;<see langword="null" /> 时一律拒签。</param>
    /// <param name="target">确认框里给用户看的「哪条会话」,<c>用户@主机:端口</c>。</param>
    /// <param name="hostKeys">已知主机;确认框拿它把远端要登录的目的主机认成主机名。</param>
    /// <param name="banners">认证时服务端发来的横幅;第一个 shell 打开时作为提示写进终端。</param>
    public VelaSshClientWrapper(
        Func<CancellationToken, ValueTask<SshConnection>> connect,
        TimeSpan connectTimeout,
        SshSessionOptions? features = null,
        ILocalXServer? localXServer = null,
        IAgentSignPrompt? agentPrompt = null,
        string target = "",
        IHostKeyService? hostKeys = null,
        SshServerBanners? banners = null)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _features = features;
        _localXServer = localXServer;
        _agentPrompt = agentPrompt;
        _hostKeys = hostKeys;
        _banners = banners;
        _target = target;
        ConnectionTimeout = connectTimeout;
    }

    /// <summary>底层连接;SFTP 要在同一条会话上开通道,所以要拿得到它。</summary>
    internal SshConnection? InnerConnection => _connection;

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connection is { IsAlive: true };
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 改它只影响**下一次**连接 —— 连接建立所用的超时在
    /// <see cref="SshConnectionOptions" /> 里,已经定死在那一次拨号上了。
    /// </remarks>
    public TimeSpan ConnectionTimeout
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return field;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            field = value;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 没连上时给一个**永不取消**的令牌,而不是一个已取消的 ——
    /// 已取消的会让终端的读循环在连接建立之前就退出。
    /// </remarks>
    public CancellationToken Disconnected => _connection?.Disconnected ?? CancellationToken.None;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is not null)
        {
            return;
        }

        try
        {
            _connection = await _connect(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>像素尺寸是一等公民</b>,不再被丢掉:上一版底层库的 pty-req 只接受字符行列数,
    /// width/height 与终端模式参数都无处可传。sixel 与 kitty 图形协议要靠像素尺寸排版。
    /// </remarks>
    public async Task<IShellStreamWrapper> CreateShellStreamAsync(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IReadOnlyDictionary<TerminalMode, uint>? terminalModeValues = null,
        CancellationToken cancellationToken = default)
    {
        SshConnection connection = EnsureConnected();

        try
        {
            SshShellOptions options = new()
            {
                TerminalType = terminalName,
                Size = new SshTerminalSize((int)columns, (int)rows, (int)width, (int)height),
                Modes = BuildModes(terminalModeValues),
            };

            // 服务端认证时发来的横幅排在最前面(法律声明、「密码将于 3 天后过期」);只在第一个 shell 上显示一次。
            List<ShellStreamNotice> notices = [.. _banners?.TakeNotices() ?? []];
            XServerDisplayResolution? localServer = await ResolveLocalXServerAsync(notices, cancellationToken).ConfigureAwait(false);
            // 连接器带上这个会话的来历(user@host:port)与信任级别:内置 X 服务端的日志与客户端清单据此说得出是哪个会话的程序,
            // 非受信的会话按 SECURITY 的语义受限(见 X11ChannelSource)。
            Func<CancellationToken, ValueTask<Stream>>? connector = localServer?.Connector is { } connect ? ct => connect(X11ChannelSource(), ct) : null;
            X11ForwardOptions? x11 = SshForwardingOptions.X11(_features, notices, localServer?.Display, connector);
            // agent 的端点交给库的默认值(Windows 上指向命名管道的 SSH_AUTH_SOCK 也认),与认证时连 agent 是同一个。
            AgentForwardOptions? agent = SshForwardingOptions.Agent(_features, notices, _agentPrompt, _target, hostKeys: _hostKeys);

            // 转发是附带功能:两项都按「没开成就不开」请求(见 SshForwardingOptions),
            // 失败时库不抛、shell 照常一次开成,原因在结果对象上,这里转成提示。
            SshShell shell = await connection
                .OpenShellAsync(options with { X11Forwarding = x11, AgentForwarding = agent }, cancellationToken)
                .ConfigureAwait(false);

            if (shell.X11SetupFailure is { } x11Failure)
            {
                notices.Add(ForwardFailed("Ssh_X11ForwardFailed", x11Failure));
            }
            if (shell.AgentSetupFailure is { } agentFailure)
            {
                notices.Add(ForwardFailed("Ssh_AgentForwardFailed", agentFailure));
            }
            if (shell.X11 is { } forwarder)
            {
                notices.Add(new(Strings.Format("Ssh_X11ForwardOn", SshForwardingOptions.Describe(forwarder.Display)), false));
            }
            if (shell.Agent is not null && agent is not null)
            {
                notices.Add(new(SshForwardingOptions.DescribeAgent(_features, agent), false));
            }
            return new ShellStreamWrapper(shell, notices);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// 要开 X11 转发、配置里又没写显示地址时,问一下 VelaShell 管理的本机 X Server:
    /// 在运行就用它的显示,没运行且设置允许就先把它拉起来。
    /// </summary>
    /// <remarks>
    /// 自动启动失败只记一行黄字,不拦 shell —— 与 X11 本身的尽力而为是同一个口径。
    /// 配置里写了显示地址时完全不碰它:那是用户明确指定的 X 服务端。
    /// </remarks>
    private async Task<XServerDisplayResolution?> ResolveLocalXServerAsync(List<ShellStreamNotice> notices, CancellationToken cancellationToken)
    {
        if (_localXServer is not { IsSupported: true }
            || _features is not { X11Forwarding: true }
            || !string.IsNullOrWhiteSpace(_features.X11Display))
        {
            return null;
        }

        XServerDisplayResolution resolution =
            await _localXServer.ResolveForwardingDisplayAsync(cancellationToken).ConfigureAwait(false);
        if (resolution.Error is { } error)
        {
            notices.Add(new(Strings.Format("XServer_AutoStartFailed", error), true));
        }
        return resolution;
    }

    /// <summary>
    /// 把宿主的终端模式表翻成库的 <see cref="SshTerminalModes" />。
    /// </summary>
    /// <remarks>
    /// 宿主的 <see cref="TerminalMode" /> 枚举值就是 RFC 4254 §8 的 opcode,
    /// 直接转字节即可 —— 两边用的是同一份编号表,那是协议规定的,不是巧合。
    /// </remarks>
    private static SshTerminalModes BuildModes(IReadOnlyDictionary<TerminalMode, uint>? values)
    {
        SshTerminalModes modes = SshTerminalModes.Empty;
        if (values is null)
        {
            return modes;
        }

        foreach ((TerminalMode mode, uint argument) in values)
        {
            modes = modes.With((SshTerminalModeOpcode)(byte)mode, argument);
        }
        return modes;
    }

    /// <summary>「某项转发没开成」的提示:本地化的标题 + 按原因码本地化的原因(见 <see cref="SshInterop.Localize" />)。</summary>
    private static ShellStreamNotice ForwardFailed(string key, SshForwardException ex) =>
        new(Strings.Format(key, SshInterop.Localize(ex)), true);

    /// <inheritdoc />
    public async Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default)
    {
        SshConnection connection = EnsureConnected();

        try
        {
            SshCommandOptions? options = await ResolveCommandOptionsAsync(cancellationToken).ConfigureAwait(false);
            SshCommandResult output = await connection
                .RunAsync(commandText, options, cancellationToken).ConfigureAwait(false);
            return output.StandardOutput;
        }
        catch (Exception ex) when (IsTornDown(ex))
        {
            throw new ObjectDisposedException(nameof(VelaSshClientWrapper), ex);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    public async Task<RemoteCommandResult> RunCommandDetailedAsync(
        string commandText, CancellationToken cancellationToken = default)
    {
        SshConnection connection = EnsureConnected();

        try
        {
            SshCommandOptions? options = await ResolveCommandOptionsAsync(cancellationToken).ConfigureAwait(false);
            SshCommandResult output = await connection
                .RunAsync(commandText, options, cancellationToken).ConfigureAwait(false);

            // ExitCode 是 int? —— 被信号杀死或连接中断时没有退出码。
            // 契约这一侧只有 int,所以把「没有退出码」折成 -1:
            // **不伪造 128+n**(那是 shell 的约定,不是 SSH 的),
            // 伪造它会让「进程返回 137」和「进程被 KILL」再也分不开。
            return new(output.StandardOutput, output.StandardError, output.ExitCode ?? -1);
        }
        catch (Exception ex) when (IsTornDown(ex))
        {
            throw new ObjectDisposedException(nameof(VelaSshClientWrapper), ex);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 两条流**各自**按行读,合到一个回调里。不能先读完一条再读另一条 ——
    /// 那是经典死锁:对端在 stderr 上写满了缓冲区等你读,而你在 stdout 上等它写完。
    /// </remarks>
    public async Task<RemoteCommandStreamResult> StreamCommandAsync(
        string commandText,
        bool includeStandardError,
        Action<bool, string> onLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        SshConnection connection = EnsureConnected();

        SshCommand? command = null;
        long lines = 0;

        try
        {
            SshCommandOptions? forwarding = await ResolveCommandOptionsAsync(cancellationToken).ConfigureAwait(false);

            // 不要 stderr 时让库直接丢弃,而不是缓冲着没人读:缓冲的那份只有被读走才回补窗口,
            // 窗口一满对端就停发 —— stdout 也跟着停住,`docker logs -f` 这类长驻命令会就此卡死。
            SshCommandOptions? options = includeStandardError && forwarding is null
                ? null
                : new SshCommandOptions
                {
                    Channel = includeStandardError
                        ? SshChannelOptions.Default
                        : SshChannelOptions.Default with { StderrMode = SshStderrMode.Discard },
                    X11Forwarding = forwarding?.X11Forwarding,
                };

            command = await connection.ExecuteAsync(commandText, options, cancellationToken)
                .ConfigureAwait(false);

            Task<long> stdout = PumpLinesAsync(command.StandardOutput, false, onLine, cancellationToken);
            Task<long> stderr = includeStandardError
                ? PumpLinesAsync(command.StandardError, true, onLine, cancellationToken)
                : Task.FromResult(0L);

            long[] counts = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            lines = counts[0] + counts[1];

            SshExitStatus result = await command.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(result.ExitCode ?? -1, lines);
        }
        catch (OperationCanceledException)
        {
            // 取消长驻命令时先给远端进程一个 TERM。只关通道的话,`docker logs -f`
            // 那一端要等到写管道被拒才知道该退出 —— 在没有新日志的空闲期,那可能是"永远"。
            await TrySendTermAsync(command).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (IsTornDown(ex))
        {
            throw new ObjectDisposedException(nameof(VelaSshClientWrapper), ex);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
        finally
        {
            if (command is not null)
            {
                try { await command.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    /// <summary>
    /// 这个会话的 x11 通道交给内置 X 服务端时带上的来历:哪个会话(user@host:port)、勾没勾「受信任」、
    /// 会话对象本身与它断开的信号(设置里开了「每个 SSH 会话一个显示」时,服务端按它们分显示、在会话断开时收掉)。
    /// </summary>
    private XServerChannelSource X11ChannelSource() => new(_target, _features?.X11Trusted ?? true, this, Disconnected);

    /// <summary>
    /// 为非交互式 exec 通道补上连接级 X11 转发。
    /// </summary>
    /// <remarks>
    /// 交互式终端在 <see cref="CreateShellStreamAsync" /> 里单独装配过；
    /// 命令 / 插件任务走的是另一条 session channel，若这里不发 x11-req，
    /// 远端 GUI 程序会拿到空的 DISPLAY 或直接连接失败。
    /// </remarks>
    private async Task<SshCommandOptions?> ResolveCommandOptionsAsync(CancellationToken cancellationToken)
    {
        if (_features is not { X11Forwarding: true })
        {
            return null;
        }

        XServerDisplayResolution? localServer = await ResolveLocalXServerAsync([], cancellationToken).ConfigureAwait(false);
        Func<CancellationToken, ValueTask<Stream>>? connector =
            localServer?.Connector is { } connect ? ct => connect(X11ChannelSource(), ct) : null;
        X11ForwardOptions? x11 = SshForwardingOptions.X11(
            _features,
            [],
            localServer?.Display,
            connector);

        return x11 is null ? null : new SshCommandOptions { X11Forwarding = x11 };
    }

    /// <summary>
    /// 从一条管道里按行读,逐行回调。
    /// </summary>
    /// <remarks>
    /// 在管道的缓冲区上直接切行,不先把整段转成 <see cref="string" /> 再 <c>Split</c> ——
    /// 长驻命令(<c>tail -F</c>)可能跑上几小时,按行切才不会把整份输出攒在内存里。
    /// </remarks>
    private static async Task<long> PumpLinesAsync(
        PipeReader reader, bool isError, Action<bool, string> onLine, CancellationToken cancellationToken)
    {
        long lines = 0;

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
            {
                onLine(isError, DecodeLine(line));
                lines++;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                // 最后一行可能没有换行符收尾 —— 丢掉它就等于丢掉命令的最后一句输出。
                if (!buffer.IsEmpty)
                {
                    onLine(isError, DecodeLine(buffer));
                    lines++;
                }
                return lines;
            }
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        SequencePosition? end = buffer.PositionOf((byte)'\n');
        if (end is null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, end.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, end.Value));
        return true;
    }

    /// <summary>解一行:UTF-8,并去掉行尾可能存在的 CR(远端是 CRLF 时)。</summary>
    private static string DecodeLine(ReadOnlySequence<byte> line)
    {
        string text = Encoding.UTF8.GetString(line);
        return text.EndsWith('\r') ? text[..^1] : text;
    }

    private static async ValueTask TrySendTermAsync(SshCommand? command)
    {
        if (command is null)
        {
            return;
        }
        try
        {
            await command.SendSignalAsync("TERM").ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 通道可能已经塌了 —— 这只是尽力而为的礼貌收尾,失败不该盖住原来的取消异常。
        }
    }

    /// <inheritdoc />
    public async Task<TimeSpan?> MeasureRoundTripAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not { } connection)
        {
            return null;
        }
        try
        {
            return await connection.MeasureRoundTripAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>计量在库里</b>,所以这里只是转交。宿主自己写的那条计量中继(376 行)
    /// 连同它在本机开的监听端口一起没了 —— 见 <see cref="LibraryPortForwardHandle" />。
    /// </remarks>
    public async Task<IPortForwardHandle> StartPortForwardAsync(
        PortForwardRequest request, CancellationToken cancellationToken = default)
    {
        SshConnection connection = EnsureConnected();

        try
        {
            return await LibraryPortForwardHandle
                .CreateAsync(connection, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenUnixConnectionAsync(
        string socketPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        SshConnection connection = EnsureConnected();

        try
        {
            SshChannel channel = await connection
                .OpenUnixSocketTunnelAsync(socketPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SyncCompatibleStream(channel.AsStream());
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenTcpConnectionAsync(
        string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        SshConnection connection = EnsureConnected();

        try
        {
            SshChannel channel = await connection
                .OpenTcpTunnelAsync(host, port, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SyncCompatibleStream(channel.AsStream());
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await DisposeConnectionAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    private SshConnection EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection ?? throw new InvalidOperationException("Not connected.");
    }

    /// <summary>
    /// 这次失败是不是「我们自己在拆」导致的。
    /// </summary>
    /// <remarks>
    /// 拆链途中的操作会撞上连接已关的异常,而调用方要的结论是
    /// <see cref="ObjectDisposedException" />(「这个包装器没了」),
    /// 不是「远端出了什么问题」。
    /// </remarks>
    private bool IsTornDown(Exception ex) =>
        ex is SshConnectionClosedException or ObjectDisposedException
        && (_disposed || _connection is null || !_connection.IsAlive);

    private async ValueTask DisposeConnectionAsync()
    {
        SshConnection? target = Interlocked.Exchange(ref _connection, null);
        if (target is not null)
        {
            await DisposeQuietlyAsync(target).ConfigureAwait(false);
        }
    }

    /// <summary>通道关闭时释放可能抛,视为正常清理噪声,吞掉即可。</summary>
    private static async ValueTask DisposeQuietlyAsync(SshConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }
    }
}
