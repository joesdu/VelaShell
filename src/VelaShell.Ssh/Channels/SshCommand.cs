// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.5   exec
//   RFC 4254 §6.10  exit-status / exit-signal
//   行为规格:       velashell-docs/zh/ssh/spec/05-connection.md §5.2、§5.4、§7.1

using System.IO.Pipelines;
using System.Text;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Ssh.Channels;

/// <summary>执行一条命令的参数。</summary>
public sealed record SshCommandOptions : SshSessionRequestOptions
{
    /// <summary>默认选项。</summary>
    public static SshCommandOptions Default { get; } = new();
}

/// <summary>一条正在运行的远端命令。</summary>
/// <remarks>
/// 〔决策 velashell-docs/zh/ssh/spec/05 §7.2〕<b><see cref="SshCommand"/> 与 <see cref="SshShell"/>
/// 是两个类型，不是一个带 <c>HasTerminal</c> 的类。</b>
/// 它们的生命周期、读写形状、退出语义都不一样；挤在一起的结果是一堆
/// 「有 pty 时这个属性无意义」的条件分支，而那种分支永远会有人踩。
/// </remarks>
public sealed class SshCommand : IAsyncDisposable
{
    internal SshCommand(
        SshChannel channel,
        X11Forwarder? x11 = null,
        AgentForwarder? agent = null,
        SshForwardException? x11SetupFailure = null)
    {
        Channel = channel;
        X11 = x11;
        Agent = agent;
        X11SetupFailure = x11SetupFailure;
    }

    /// <summary>这条命令的 agent 转发；没请求过就是 <see langword="null"/>。</summary>
    public AgentForwarder? Agent { get; }

    /// <summary>这条命令的 X11 转发；没请求过、或尽力而为的请求没成时是 <see langword="null"/>。</summary>
    /// <remarks>
    /// 交出来是为了能看计数（接受了几条、拒绝了几条）——
    /// <c>RejectedChannels</c> 非零意味着有人拿着错的 cookie 在敲门。
    /// </remarks>
    public X11Forwarder? X11 { get; }

    /// <summary>
    /// 尽力而为的 X11 请求（<see cref="X11ForwardOptions.BestEffort"/>）没成时的原因；
    /// 其余情况都是 <see langword="null"/>。
    /// </summary>
    /// <remarks>语义见 <see cref="SshShell.X11SetupFailure"/>。</remarks>
    public SshForwardException? X11SetupFailure { get; }

    /// <summary>底层通道。</summary>
    public SshChannel Channel { get; }

    /// <summary>远端的标准输出。</summary>
    public PipeReader StandardOutput => Channel.StandardOutput;

    /// <summary>远端的标准错误。</summary>
    public PipeReader StandardError => Channel.StandardError;

    /// <summary>写进去的内容变成远端的标准输入。</summary>
    public PipeWriter StandardInput => Channel.StandardInput;

    /// <summary>告诉远端「标准输入到此为止」。</summary>
    /// <remarks>
    /// <b>这不是关闭通道</b> —— 发完之后<b>仍然会继续收到输出</b>。
    /// 把它当成结束是最常见的错误，症状是
    /// <c>ssh host 'cat &gt; f' &lt; big</c> 这类场景下丢掉远端的最后输出。
    /// </remarks>
    public ValueTask CompleteStandardInputAsync(CancellationToken cancellationToken = default) =>
        Channel.SendEofAsync(cancellationToken);

    /// <summary>给远端进程发信号。</summary>
    /// <param name="signalName">信号名，<b>不带 <c>SIG</c> 前缀</b>（<c>"TERM"</c> 而非 <c>"SIGTERM"</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask SendSignalAsync(string signalName, CancellationToken cancellationToken = default) =>
        Channel.SendSignalAsync(signalName, cancellationToken);

    /// <summary>等命令结束。</summary>
    /// <remarks>
    /// <b>只调用这个而不读 <see cref="StandardOutput"/> 会在输出较多时卡住。</b>
    /// 那不是 bug：接收窗口挂在消费上，没人读就不回补，远端自然停下来 ——
    /// 这正是背压在起作用。要么读输出，要么用
    /// <see cref="ReadToEndAsync"/>，要么把 stderr 设成
    /// <see cref="SshStderrMode.Discard"/>。
    /// </remarks>
    public ValueTask<SshExitStatus> WaitAsync(CancellationToken cancellationToken = default) =>
        Channel.WaitForExitAsync(cancellationToken);

    /// <summary>把 stdout 与 stderr 都读完，再等命令结束。</summary>
    /// <remarks>
    /// <b>两条流是并发读的。</b>先读完一条再读另一条会死锁：
    /// 先读的那条可能一直没数据，而远端正因为另一条的窗口被吃空而停住。
    /// </remarks>
    public async ValueTask<SshCommandResult> ReadToEndAsync(CancellationToken cancellationToken = default)
    {
        Task<string> stdout = ReadAllTextAsync(StandardOutput, cancellationToken);
        Task<string> stderr = ReadAllTextAsync(StandardError, cancellationToken);

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        SshExitStatus status = await WaitAsync(cancellationToken).ConfigureAwait(false);

        return new SshCommandResult(status, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<string> ReadAllTextAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        Decoder decoder = Encoding.UTF8.GetDecoder();

        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            foreach (ReadOnlyMemory<byte> segment in read.Buffer)
            {
                // 逐段解码并保留状态 —— 一个多字节字符可能横跨两个段，
                // 每段各自 GetString 会把它切成两个乱码字符。
                char[] chars = new char[segment.Length];
                int count = decoder.GetChars(segment.Span, chars, flush: false);
                text.Append(chars, 0, count);
            }

            reader.AdvanceTo(read.Buffer.End);

            if (read.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync().ConfigureAwait(false);
        return text.ToString();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 先摘 X11 处理器再关通道：反过来的话，关通道那一刻服务端
    /// 可能还在往回开 x11 通道，而处理器已经没了。
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
