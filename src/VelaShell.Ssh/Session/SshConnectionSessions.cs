// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.2  pty-req
//   RFC 4254 §6.4  env
//   RFC 4254 §6.5  shell / exec / subsystem
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.2、§7

using System.Buffers;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

/// <summary>在会话上起一次性命令、shell 或子系统。</summary>
public static class SshConnectionSessions
{
    /// <summary>执行一条命令。</summary>
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
        SshExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(commandLine);

        SshExecutionOptions effective = options ?? SshExecutionOptions.Default;
        SshChannel channel = await connection
            .OpenSessionChannelAsync(effective.Channel, cancellationToken).ConfigureAwait(false);

        SessionForwarding forwarding = default;
        try
        {
            // 〔velashell-docs/zh/ssh/spec/07 §7.5.3〕时序：x11-req → auth-agent-req → env → exec。
            // 顺序不对的话某些服务端会忽略 x11-req，而那时 DISPLAY 不会被设上，
            // 症状是「远端程序说连不上 X server」—— 指不到这里。
            forwarding = await RequestForwardingAsync(
                connection, channel, effective.X11, effective.AgentForwarding, effective.AgentEndpoint,
                cancellationToken).ConfigureAwait(false);

            await SendEnvironmentAsync(channel, effective.Environment, cancellationToken).ConfigureAwait(false);

            if (effective.BeforeStart is { } beforeStart)
            {
                await beforeStart(channel, cancellationToken).ConfigureAwait(false);
            }

            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteUtf8String(commandLine);

            // 〔决策 velashell-docs/zh/ssh/spec/05 §5.2〕**必须要求回复并等它。**
            // 不等就发数据，在服务端拒绝执行时会表现为「命令没输出也没报错」。
            bool accepted = await channel.SendRequestAsync(
                SshAlgorithmNames.RequestExec, buffer.WrittenMemory, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!accepted)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelOpenFailed,
                    "服务端拒绝执行这条命令（常见原因：账号被限制成只能跑固定命令，" +
                    "或者 sshd_config 里配了 ForceCommand）。");
            }

            return new SshCommand(channel, forwarding.X11, forwarding.Agent);
        }
        catch (Exception)
        {
            await forwarding.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>执行一条命令，把输出全读完。</summary>
    public static async ValueTask<(SshCommandResult Result, string StandardOutput, string StandardError)>
        ExecuteAndReadAsync(
            this SshConnection connection,
            string commandLine,
            SshExecutionOptions? options = null,
            CancellationToken cancellationToken = default)
    {
        await using SshCommand command = await connection
            .ExecuteAsync(commandLine, options, cancellationToken).ConfigureAwait(false);

        // 远端的 stdin 不会有东西了 —— 先告诉它，免得等着读的程序（cat、sort）一直挂着。
        await command.CompleteStandardInputAsync(cancellationToken).ConfigureAwait(false);

        return await command.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>开一个交互式 shell。</summary>
    /// <remarks>
    /// 请求的时序是 <c>pty-req</c> → <c>x11-req</c> → <c>auth-agent-req</c> → <c>env</c> →
    /// （<see cref="SshShellOptions.BeforeStart"/>）→ <c>shell</c>（<c>velashell-docs/zh/ssh/spec/07</c> §7.5.3）。
    /// X11 与 agent 转发只在 <see cref="SshShellOptions"/> 里显式要求时才请求；
    /// 要求了而服务端拒绝时抛出，不静默降级。
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
                SshAlgorithmNames.RequestPty, pty.WrittenMemory, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!ptyAccepted)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelOpenFailed,
                    "服务端拒绝分配伪终端（常见原因：sshd_config 里 PermitTTY no，" +
                    "或者这个账号被配成了无终端登录）。");
            }

            forwarding = await RequestForwardingAsync(
                connection, channel, effective.X11, effective.AgentForwarding, effective.AgentEndpoint,
                cancellationToken).ConfigureAwait(false);

            await SendEnvironmentAsync(channel, effective.Environment, cancellationToken).ConfigureAwait(false);

            if (effective.BeforeStart is { } beforeStart)
            {
                await beforeStart(channel, cancellationToken).ConfigureAwait(false);
            }

            bool shellAccepted = await channel.SendRequestAsync(
                SshAlgorithmNames.RequestShell, default, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!shellAccepted)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelOpenFailed, "服务端拒绝启动 shell。");
            }

            return new SshShell(channel, effective.Size, forwarding.X11, forwarding.Agent);
        }
        catch (Exception)
        {
            await forwarding.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>一个会话请求到的转发，失败时一并收拾。</summary>
    private readonly record struct SessionForwarding(
        Forwarding.X11Forwarder? X11, Forwarding.AgentForwarder? Agent) : IAsyncDisposable
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
        Forwarding.X11ForwardOptions? x11Options,
        Forwarding.AgentForwardPolicy? agentPolicy,
        string? agentEndpoint,
        CancellationToken cancellationToken)
    {
        Forwarding.X11Forwarder? x11 = null;
        try
        {
            if (x11Options is not null)
            {
                x11 = await Forwarding.X11Forwarder
                    .RequestAsync(connection, channel, x11Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            Forwarding.AgentForwarder? agent = null;
            if (agentPolicy is not null)
            {
                agent = await Forwarding.AgentForwarder
                    .RequestAsync(connection, channel, agentPolicy, agentEndpoint, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            return new SessionForwarding(x11, agent);
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

    /// <summary>开一个子系统通道（如 <c>sftp</c>）。</summary>
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
                SshAlgorithmNames.RequestSubsystem, buffer.WrittenMemory, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!accepted)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelOpenFailed,
                    $"服务端没有提供子系统 {subsystemName}" +
                    (subsystemName == "sftp"
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

            // 〔决策 velashell-docs/zh/ssh/spec/05 §5.2〕**不要求回复。**
            // 服务端的 AcceptEnv 只放行少数变量，被拒是常态而不是错误；
            // 要求回复会让每设一个变量多一个 RTT，还会把一个正常情况报成失败。
            await channel.SendRequestAsync(
                SshAlgorithmNames.RequestEnvironment, buffer.WrittenMemory, wantReply: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
