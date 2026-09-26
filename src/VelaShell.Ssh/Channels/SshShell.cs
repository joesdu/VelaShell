// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.2  pty-req
//   RFC 4254 §6.5  shell
//   RFC 4254 §6.7  window-change
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.3、§7.2

using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>打开交互式 shell 的参数。</summary>
public sealed record SshShellOptions : SshSessionRequestOptions
{
    /// <summary>建一组默认的 shell 参数。</summary>
    /// <remarks>
    /// 默认把 stderr 设成丢弃：<b>有 pty 时 stderr 会合并进 stdout</b>，
    /// 伪终端只有一条输出流，缓冲一条永远没数据的流毫无意义。
    /// </remarks>
    public SshShellOptions() =>
        Channel = SshChannelOptions.Default with { StderrMode = SshStderrMode.Discard };

    /// <summary>终端类型。</summary>
    public string TerminalType { get; init; } = "xterm-256color";

    /// <summary>初始尺寸。</summary>
    public SshTerminalSize Size { get; init; } = SshTerminalSize.Default;

    /// <summary>终端模式。</summary>
    public SshTerminalModes Modes { get; init; } = SshTerminalModes.Empty;

    /// <summary>默认参数。</summary>
    public static SshShellOptions Default { get; } = new();
}

/// <summary>一个正在运行的交互式 shell。</summary>
/// <remarks>
/// <para>
/// 与 <see cref="SshCommand"/> 的差别只有三处，但都关乎正确性：
/// </para>
/// <list type="number">
///   <item><c>exec</c> 换成 <c>pty-req</c> + <c>shell</c>。</item>
///   <item><b>有 pty 时 stderr 合并进 stdout</b> —— 伪终端只有一条输出流。
///   〔决策 velashell-docs/zh/ssh/spec/05 §7.2〕所以这个类型<b>干脆不暴露 <c>StandardError</c></b>，
///   免得使用者对着一条永远空的流等待。</item>
///   <item>尺寸变化发 <c>window-change</c>。</item>
/// </list>
/// 其余成员与 <see cref="SshCommand"/> 同名同义。
/// </remarks>
public sealed class SshShell : IAsyncDisposable
{
    internal SshShell(
        SshChannel channel,
        SshTerminalSize size,
        X11Forwarder? x11 = null,
        AgentForwarder? agent = null,
        SshForwardException? x11SetupFailure = null)
    {
        Channel = channel;
        Size = size;
        X11 = x11;
        Agent = agent;
        X11SetupFailure = x11SetupFailure;
    }

    /// <summary>底层通道。</summary>
    public SshChannel Channel { get; }

    /// <summary>这个 shell 的 X11 转发；没请求过、或尽力而为的请求没成时是 <see langword="null"/>。</summary>
    public X11Forwarder? X11 { get; }

    /// <summary>
    /// 尽力而为的 X11 请求（<see cref="X11ForwardOptions.BestEffort"/>）没成时的原因；
    /// 其余情况（成了、没请求、或者请求是严格的 —— 严格的失败直接抛）都是 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8〕连接级开关打开的 X11 失败时「记日志，照常启动」。
    /// 本库不带日志器，所以原因以结构化形式交给调用方（同时计入转发的错误计数），
    /// 由调用方决定记到哪里、要不要提示使用者。
    /// </remarks>
    public SshForwardException? X11SetupFailure { get; }

    /// <summary>这个 shell 的 agent 转发；没请求过就是 <see langword="null"/>。</summary>
    public AgentForwarder? Agent { get; }

    /// <summary>终端输出（<b>stdout 与 stderr 已经由伪终端合并</b>）。</summary>
    public PipeReader StandardOutput => Channel.StandardOutput;

    /// <summary>终端输入。</summary>
    public PipeWriter StandardInput => Channel.StandardInput;

    /// <summary>当前的终端尺寸。</summary>
    public SshTerminalSize Size { get; private set; }

    /// <summary>读下一件事（退出状态、关闭…）。</summary>
    public ValueTask<SshChannelEvent> ReadEventAsync(CancellationToken cancellationToken = default) =>
        Channel.ReadEventAsync(cancellationToken);

    /// <summary>终端尺寸变了。</summary>
    /// <remarks>
    /// 像素尺寸照样发过去 —— sixel、kitty 图形协议这类东西要靠它排版
    /// （velashell-docs/zh/ssh/spec/05 §5.3）。不知道就给 0，那也是一个有意义的回答。
    /// </remarks>
    public async ValueTask ResizeAsync(SshTerminalSize size, CancellationToken cancellationToken = default)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUInt32((uint)size.Columns);
        writer.WriteUInt32((uint)size.Rows);
        writer.WriteUInt32((uint)size.PixelWidth);
        writer.WriteUInt32((uint)size.PixelHeight);

        // RFC 4254 §6.7 明确要求 want_reply 为假。
        await Channel.SendRequestAsync(
            SshProtocolNames.RequestWindowChange, buffer.WrittenMemory, wantReply: false, cancellationToken)
            .ConfigureAwait(false);

        Size = size;
    }

    /// <summary>给远端进程发信号。</summary>
    /// <param name="signalName">信号名，<b>不带 <c>SIG</c> 前缀</b>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask SendSignalAsync(string signalName, CancellationToken cancellationToken = default) =>
        Channel.SendSignalAsync(signalName, cancellationToken);

    /// <summary>告诉远端输入到此为止。</summary>
    public ValueTask CompleteStandardInputAsync(CancellationToken cancellationToken = default) =>
        Channel.SendEofAsync(cancellationToken);

    /// <summary>等 shell 结束。</summary>
    public ValueTask<SshExitStatus> WaitAsync(CancellationToken cancellationToken = default) =>
        Channel.WaitForExitAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// 先摘转发的处理器再关通道：反过来的话，关通道那一刻服务端
    /// 可能还在往回开 x11 / agent 通道，而处理器已经没了。
    /// </remarks>
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

        await Channel.DisposeAsync().ConfigureAwait(false);
    }
}
