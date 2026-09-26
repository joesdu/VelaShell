// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.2   pty-req
//   RFC 4254 §6.4   env
//   RFC 4254 §6.5   shell / exec / subsystem
//   RFC 4254 §7.2   direct-tcpip
//   OpenSSH PROTOCOL  direct-streamlocal@openssh.com
//   行为规格:       velashell-docs/zh/ssh/spec/05-connection.md §5.2、§7;velashell-docs/zh/ssh/spec/07-forwarding.md §一、§2.1

using System.Buffers;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

/// <summary>在一条连接上跑命令、开 shell、开子系统、开隧道。</summary>
/// <remarks>
/// 这些操作都只用到 <see cref="SshConnection"/> 开通道的能力，所以写成扩展方法，
/// 不往那个已经够大的类型里塞 —— 一个类型只有这一个扩展方法类（AGENTS.md §4.4）。
/// </remarks>
public static class SshConnectionExtensions
{
    /// <summary>跑一条命令，把 stdout / stderr / 退出状态一次拿全。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="commandLine">命令行 —— <b>由远端的登录 shell 解释</b>（见 <see cref="ExecuteAsync"/>）。</param>
    /// <param name="options">参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 远端的 stdin 会先被关掉 —— 这里不会再有输入，免得等着读的程序（<c>cat</c>、<c>sort</c>）一直挂着。
    /// 要边跑边读、或者要往 stdin 里写东西，用 <see cref="ExecuteAsync"/>。
    /// </remarks>
    public static async ValueTask<SshCommandResult> RunAsync(
        this SshConnection connection,
        string commandLine,
        SshCommandOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using SshCommand command = await connection
            .ExecuteAsync(commandLine, options, cancellationToken).ConfigureAwait(false);

        await command.CompleteStandardInputAsync(cancellationToken).ConfigureAwait(false);
        return await command.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>启动一条命令，交出它的句柄。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="commandLine">命令行 —— <b>由远端的登录 shell 解释</b>。</param>
    /// <param name="options">参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>这里没有参数数组，只有一整条字符串</b>，因为协议本身就只传一条字符串：
    /// 远端拿它交给 <c>$SHELL -c</c>。所以命令里的空格、引号、
    /// <c>$</c> 都由远端 shell 解释 —— 拼接不可信内容进去就是注入。
    /// 库不能替使用者转义，因为不知道远端是哪种 shell。
    /// </remarks>
    public static async ValueTask<SshCommand> ExecuteAsync(
        this SshConnection connection,
        string commandLine,
        SshCommandOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(commandLine);

        SshCommandOptions effective = options ?? SshCommandOptions.Default;
        SshChannel channel = await connection
            .OpenSessionChannelAsync(effective.Channel, cancellationToken).ConfigureAwait(false);

        SessionForwarding forwarding = default;
        try
        {
            // 〔velashell-docs/zh/ssh/spec/07 §7.5.3〕时序：x11-req → auth-agent-req → env → exec。
            // 顺序不对的话某些服务端会忽略 x11-req，而那时 DISPLAY 不会被设上，
            // 症状是「远端程序说连不上 X server」—— 指不到这里。
            forwarding = await RequestForwardingAsync(connection, channel, effective, cancellationToken)
                .ConfigureAwait(false);

            await SendEnvironmentAsync(channel, effective.Environment, cancellationToken).ConfigureAwait(false);

            if (effective.BeforeStart is { } beforeStart)
            {
                await beforeStart(channel, cancellationToken).ConfigureAwait(false);
            }

            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteUtf8String(commandLine);

            // 〔决策 velashell-docs/zh/ssh/spec/05 §5.2〕必须要求回复并等它。
            // 不等就发数据，在服务端拒绝执行时会表现为「命令没输出也没报错」。
            bool accepted = await channel.SendRequestAsync(
                SshProtocolNames.RequestExec, buffer.WrittenMemory, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!accepted)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelRequestRejected,
                    "服务端拒绝执行这条命令（常见原因：账号被限制成只能跑固定命令，" +
                    "或者 sshd_config 里配了 ForceCommand）。");
            }

            return new SshCommand(channel, forwarding.X11, forwarding.Agent, forwarding.X11SetupFailure);
        }
        catch (Exception)
        {
            await forwarding.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>开一个交互式 shell。</summary>
    /// <remarks>
    /// 请求的时序是 <c>pty-req</c> → <c>x11-req</c> → <c>auth-agent-req</c> → <c>env</c> →
    /// （<see cref="SshSessionRequestOptions.BeforeStart"/>）→ <c>shell</c>（<c>velashell-docs/zh/ssh/spec/07</c> §7.5.3）。
    /// X11 与 agent 转发只在选项里显式要求时才请求；要求了而服务端拒绝时抛出，不静默降级。
    /// 唯一的例外是标了 <see cref="X11ForwardOptions.BestEffort"/> 的 X11 选项（连接级开关打开的，
    /// <c>velashell-docs/zh/ssh/spec/07</c> §7.5.8）：它的设置失败时 shell 照常启动、
    /// <see cref="SshShell.X11"/> 为空，原因见 <see cref="SshShell.X11SetupFailure"/>。
    /// </remarks>
    public static async ValueTask<SshShell> OpenShellAsync(
        this SshConnection connection,
        SshShellOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        SshShellOptions effective = options ?? SshShellOptions.Default;
        SshChannel channel = await connection
            .OpenSessionChannelAsync(effective.Channel, cancellationToken).ConfigureAwait(false);

        SessionForwarding forwarding = default;
        try
        {
            ArrayBufferWriter<byte> pty = new();
            SshDataWriter ptyWriter = new(pty);
            ptyWriter.WriteUtf8String(effective.TerminalType);
            ptyWriter.WriteUInt32((uint)effective.Size.Columns);
            ptyWriter.WriteUInt32((uint)effective.Size.Rows);
            ptyWriter.WriteUInt32((uint)effective.Size.PixelWidth);
            ptyWriter.WriteUInt32((uint)effective.Size.PixelHeight);
            ptyWriter.WriteString(effective.Modes.Encode());

            bool ptyAccepted = await channel.SendRequestAsync(
                SshProtocolNames.RequestPty, pty.WrittenMemory, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!ptyAccepted)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelRequestRejected,
                    "服务端拒绝分配伪终端（常见原因：sshd_config 里 PermitTTY no，" +
                    "或者这个账号被配成了无终端登录）。");
            }

            forwarding = await RequestForwardingAsync(connection, channel, effective, cancellationToken)
                .ConfigureAwait(false);

            await SendEnvironmentAsync(channel, effective.Environment, cancellationToken).ConfigureAwait(false);

            if (effective.BeforeStart is { } beforeStart)
            {
                await beforeStart(channel, cancellationToken).ConfigureAwait(false);
            }

            bool shellAccepted = await channel.SendRequestAsync(
                SshProtocolNames.RequestShell, default, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!shellAccepted)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelRequestRejected, "服务端拒绝启动 shell。");
            }

            return new SshShell(
                channel, effective.Size, forwarding.X11, forwarding.Agent, forwarding.X11SetupFailure);
        }
        catch (Exception)
        {
            await forwarding.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>开一个子系统通道（如 <c>sftp</c>）。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="subsystemName">子系统名。</param>
    /// <param name="options">通道参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async ValueTask<SshChannel> OpenSubsystemAsync(
        this SshConnection connection,
        string subsystemName,
        SshChannelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(subsystemName);

        SshChannel channel = await connection
            .OpenSessionChannelAsync(options, cancellationToken).ConfigureAwait(false);

        try
        {
            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteUtf8String(subsystemName);

            bool accepted = await channel.SendRequestAsync(
                SshProtocolNames.RequestSubsystem, buffer.WrittenMemory, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!accepted)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelRequestRejected,
                    $"服务端没有提供子系统 {subsystemName}" +
                    (subsystemName == SshProtocolNames.SubsystemSftp
                        ? "（检查 sshd_config 里的 Subsystem sftp 那一行）。"
                        : "。"));
            }

            return channel;
        }
        catch (Exception)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>开一条到远端 TCP 端点的隧道。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="host">
    /// 目标主机。<b>从服务端视角解析</b> —— <c>localhost</c> 指的是服务端的环回。
    /// </param>
    /// <param name="port">目标端口（1–65535）。</param>
    /// <param name="originatorHost">本机发起方地址。</param>
    /// <param name="originatorPort">本机发起方端口（0–65535）。</param>
    /// <param name="options">通道参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// <b>这条路不在本机开监听端口。</b>流直接交给调用方，同机的其它进程连不上去。
    /// </para>
    /// <para>
    /// 接内网 HTTP API 这类端点时，这才是该用的形态 ——
    /// 开一个 <c>-L</c> 监听意味着同机任何进程都能连上去。
    /// </para>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §2.1〕<b>originator 如实填写。</b>
    /// 服务端会把它写进日志；填假的会让管理员无法追溯，
    /// 而且没有任何隐私收益 —— 服务端本来就知道我们的连接来源。
    /// </para>
    /// </remarks>
    public static ValueTask<SshChannel> OpenTcpTunnelAsync(
        this SshConnection connection,
        string host,
        int port,
        string originatorHost = "127.0.0.1",
        int originatorPort = 0,
        SshChannelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentNullException.ThrowIfNull(originatorHost);
        ArgumentOutOfRangeException.ThrowIfNegative(originatorPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(originatorPort, 65535);

        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUtf8String(host);
        writer.WriteUInt32((uint)port);
        writer.WriteUtf8String(originatorHost);
        writer.WriteUInt32((uint)originatorPort);

        return connection.OpenChannelAsync(
            SshProtocolNames.ChannelDirectTcpIp, payload.WrittenMemory, options, cancellationToken);
    }

    /// <summary>开一条到远端 <b>Unix 套接字</b>的隧道。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="socketPath">远端套接字路径。</param>
    /// <param name="options">通道参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 接 <c>/var/run/docker.sock</c> 这类端点的唯一正路：
    /// 它<b>不在本机开监听端口</b>，流直接交给调用方。
    /// 对 root 等价的端点，这个区别不是优化而是前提 ——
    /// 开一个本地端口等于把 docker 的控制权交给同机的每一个进程。
    /// </remarks>
    public static ValueTask<SshChannel> OpenUnixSocketTunnelAsync(
        this SshConnection connection,
        string socketPath,
        SshChannelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(socketPath);

        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUtf8String(socketPath);
        writer.WriteUtf8String("");   // reserved
        writer.WriteUInt32(0);        // reserved

        return connection.OpenChannelAsync(
            SshProtocolNames.ChannelDirectStreamLocal, payload.WrittenMemory, options, cancellationToken);
    }

    /// <summary>一个会话请求到的转发，失败时一并收拾。</summary>
    /// <param name="X11">X11 转发；没请求、或尽力而为的请求没成时为空。</param>
    /// <param name="Agent">agent 转发。</param>
    /// <param name="X11SetupFailure">尽力而为的 X11 请求没成的原因。</param>
    private readonly record struct SessionForwarding(
        X11Forwarder? X11,
        AgentForwarder? Agent,
        SshForwardException? X11SetupFailure) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (X11 is not null)
            {
                await X11.DisposeAsync().ConfigureAwait(false);
            }

            if (Agent is not null)
            {
                await Agent.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>按选项依次请求 X11 与 agent 转发（<c>x11-req</c> 在前）。</summary>
    private static async ValueTask<SessionForwarding> RequestForwardingAsync(
        SshConnection connection,
        SshChannel channel,
        SshSessionRequestOptions options,
        CancellationToken cancellationToken)
    {
        X11Forwarder? x11 = null;
        SshForwardException? x11Failure = null;
        try
        {
            if (options.X11Forwarding is { } x11Options)
            {
                try
                {
                    x11 = await X11Forwarder
                        .RequestAsync(connection, channel, x11Options, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SshForwardException ex) when (x11Options.BestEffort)
                {
                    // 〔velashell-docs/zh/ssh/spec/07 §7.5.8〕连接级开关打开的 X11：记下原因，照常启动 ——
                    // 否则一份存量的 ForwardX11 yes 会让这台主机上所有会话都起不来。
                    // 只接 SshForwardException（拿不到显示 / cookie、xauth 失败、服务端拒绝）：
                    // 取消与通道本身的故障照常抛。RequestAsync 失败时已经把自己从 X11 路由上摘掉了，
                    // 这条连接不会因此留下一个接 x11 通道的半挂转发。
                    x11Failure = ex;
                    ForwardEvents.RecordError(ForwardKind.X11, ForwardErrorReason.X11SetupSkipped);
                }
            }

            AgentForwarder? agent = null;
            if (options.AgentForwarding is { } agentOptions)
            {
                agent = await AgentForwarder
                    .RequestAsync(connection, channel, agentOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new SessionForwarding(x11, agent, x11Failure);
        }
        catch (Exception)
        {
            if (x11 is not null)
            {
                await x11.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private static async ValueTask SendEnvironmentAsync(
        SshChannel channel,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        foreach ((string name, string value) in environment)
        {
            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteUtf8String(name);
            writer.WriteUtf8String(value);

            // 〔决策 velashell-docs/zh/ssh/spec/05 §5.2〕不要求回复。
            // 服务端的 AcceptEnv 只放行少数变量，被拒是常态而不是错误；
            // 要求回复会让每设一个变量多一个 RTT，还会把一个正常情况报成失败。
            await channel.SendRequestAsync(
                SshProtocolNames.RequestEnvironment, buffer.WrittenMemory, wantReply: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
