// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.5   exec
//   RFC 4254 §6.10  exit-status / exit-signal
//   行为规格:       velashell-docs/zh/ssh/spec/05-connection.md §5.2、§5.4、§7.1

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>一次性命令的结果。</summary>
/// <param name="ExitCode">
/// 退出码。<b>可能为 <see langword="null"/></b> —— 进程被信号杀死时只有
/// <see cref="ExitSignalName"/>，连接中断时两者都没有。
/// </param>
/// <param name="ExitSignalName">
/// 杀死进程的信号名，<b>不带 <c>SIG</c> 前缀</b>。
/// </param>
/// <param name="CoreDumped">是否产生了核心转储。</param>
/// <param name="ErrorMessage">对端给的错误说明（<b>不可信文本</b>）。</param>
/// <remarks>
/// 〔决策 velashell-docs/zh/ssh/spec/05 §5.4〕<b>不把信号编成 128+n 这样的伪退出码。</b>
/// 那是 shell 的约定，不是 SSH 的；伪造它会让「进程返回 137」
/// 和「进程被 KILL」在调用方眼里无法区分。
/// </remarks>
public readonly record struct SshCommandResult(
    int? ExitCode,
    string? ExitSignalName = null,
    bool CoreDumped = false,
    string? ErrorMessage = null)
{
    /// <summary>命令是否以退出码 0 正常结束。</summary>
    public bool IsSuccess => ExitCode == 0 && ExitSignalName is null;

    /// <summary>一行人话，用于日志。</summary>
    public override string ToString() =>
        ExitSignalName is not null
            ? $"被信号 {ExitSignalName} 杀死{(CoreDumped ? "（已转储核心）" : "")}"
            : ExitCode is { } code
                ? $"退出码 {code}"
                : "没有退出状态（连接中断，或对端实现不规范）";
}

/// <summary>命令与 shell 共用的启动参数。</summary>
public sealed record SshExecutionOptions
{
    /// <summary>通道参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default;

    /// <summary>
    /// 要设的环境变量。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §5.2〕<b><c>env</c> 请求不要求回复。</b>
    /// 绝大多数服务端的 <c>AcceptEnv</c> 只放行少数变量，被拒是常态而不是错误；
    /// 要求回复只会让每设一个变量多一个 RTT，并且把一个正常情况报成失败。
    /// 设失败的后果由使用者在远端自行观察。
    /// </remarks>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>默认参数。</summary>
    /// <summary>为这条命令请求 X11 转发；<see langword="null"/> 表示不请求。</summary>
    /// <remarks>
    /// <para>
    /// <b>默认不请求。</b> X11 没有客户端隔离 —— 把本机显示交给远端，
    /// 等于把本机所有图形会话的输入输出交给远端（<c>velashell-docs/zh/ssh/spec/07</c> §7.5.1）。
    /// </para>
    /// <para>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8〕在这里显式要求的，<b>失败就抛</b> ——
    /// 调用方明确要 X11，静默降级等于骗他。
    /// </para>
    /// <para>
    /// 请求的时序是 <c>pty-req</c> → <c>x11-req</c> → <c>env</c> → <c>exec</c>，
    /// 由这个方法负责，调用方不用自己拼。
    /// </para>
    /// </remarks>
    public Forwarding.X11ForwardOptions? X11 { get; init; }

    /// <summary>为这条命令请求 agent 转发（<c>ssh -A</c>）；<see langword="null"/> 表示不请求。</summary>
    /// <remarks>
    /// <b>默认不请求。</b> agent 转发让远端能用本机 agent 里的钥签名 ——
    /// 远端的 root 同样能用。策略里的 <c>AllowedKeys</c> / <c>ConfirmEachSignature</c>
    /// 就是为这个存在的。显式要求而服务端拒绝时抛出。
    /// </remarks>
    public Forwarding.AgentForwardPolicy? AgentForwarding { get; init; }

    /// <summary>本机 agent 的位置；<see langword="null"/> 取 <c>SSH_AUTH_SOCK</c> / Windows 的 OpenSSH agent 管道。</summary>
    public string? AgentEndpoint { get; init; }

    /// <summary>
    /// 在 <c>exec</c> 请求发出之前、其余请求都发完之后调用 —— 给库没有内置的通道请求留的位置。
    /// </summary>
    /// <remarks>
    /// 时序是 <c>x11-req</c> → <c>auth-agent-req</c> → <c>env</c> → <b>这里</b> → <c>exec</c>。
    /// 抛异常等于放弃这条命令（通道会被关掉）。
    /// </remarks>
    public Func<SshChannel, CancellationToken, ValueTask>? BeforeStart { get; init; }

    /// <summary>默认选项。</summary>
    public static SshExecutionOptions Default { get; } = new();
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
        SshChannel channel, Forwarding.X11Forwarder? x11 = null, Forwarding.AgentForwarder? agent = null)
    {
        Channel = channel;
        X11 = x11;
        Agent = agent;
    }

    /// <summary>这条命令的 agent 转发；没请求过就是 <see langword="null"/>。</summary>
    public Forwarding.AgentForwarder? Agent { get; }

    /// <summary>这条命令的 X11 转发；没请求过就是 <see langword="null"/>。</summary>
    /// <remarks>
    /// 交出来是为了能看计数（接受了几条、拒绝了几条）——
    /// <c>RejectedChannels</c> 非零意味着有人拿着错的 cookie 在敲门。
    /// </remarks>
    public Forwarding.X11Forwarder? X11 { get; }

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
    /// <b>这不是关闭通道</b> —— 发完之后**仍然会继续收到输出**。
    /// 把它当成结束是最常见的错误，症状是
    /// <c>ssh host 'cat &gt; f' &lt; big</c> 这类场景下丢掉远端的最后输出。
    /// </remarks>
    public ValueTask CompleteStandardInputAsync(CancellationToken cancellationToken = default) =>
        Channel.SendEofAsync(cancellationToken);

    /// <summary>给远端进程发信号。</summary>
    /// <param name="signalName">信号名，<b>不带 <c>SIG</c> 前缀</b>（<c>"TERM"</c> 而非 <c>"SIGTERM"</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask SendSignalAsync(string signalName, CancellationToken cancellationToken = default) =>
        SendSignalCoreAsync(Channel, signalName, cancellationToken);

    internal static async ValueTask SendSignalCoreAsync(
        SshChannel channel, string signalName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(signalName);

        if (signalName.StartsWith("SIG", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"信号名不带 SIG 前缀：用 \"{signalName[3..]}\" 而不是 \"{signalName}\"（RFC 4254 §6.9）。",
                nameof(signalName));
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(signalName);

        // RFC 4254 §6.9 明确要求 want_reply 为假。
        await channel.SendRequestAsync(
            SshAlgorithmNames.RequestSignal, buffer.WrittenMemory, wantReply: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>等命令结束。</summary>
    /// <remarks>
    /// ⚠️ <b>只调用这个而不读 <see cref="StandardOutput"/> 会在输出较多时卡住。</b>
    /// 那不是 bug：接收窗口挂在消费上，没人读就不回补，远端自然停下来 ——
    /// 这正是背压在起作用。要么读输出，要么用
    /// <see cref="ReadToEndAsync"/>，要么把 stderr 设成
    /// <see cref="SshStderrPolicy.Discard"/>。
    /// </remarks>
    public async ValueTask<SshCommandResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        int? exitCode = null;
        string? signalName = null;
        bool coreDumped = false;
        string? errorMessage = null;

        while (true)
        {
            SshChannelEvent channelEvent;
            try
            {
                channelEvent = await Channel.ReadEventAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                break;
            }

            switch (channelEvent)
            {
                case SshChannelEvent.ExitStatus status:
                    exitCode = status.Code;
                    break;

                case SshChannelEvent.ExitSignal signal:
                    signalName = signal.SignalName;
                    coreDumped = signal.CoreDumped;
                    errorMessage = signal.ErrorMessage;
                    break;

                case SshChannelEvent.Closed:
                    // 收到 CLOSE 时可能**还没有**退出状态 —— 对端实现不规范，
                    // 或者连接断了。那时 ExitCode 是 null，那不是 bug 而是事实：
                    // 进程到底怎么结束的，我们不知道。
                    return new SshCommandResult(exitCode, signalName, coreDumped, errorMessage);

                default:
                    break;
            }
        }

        return new SshCommandResult(exitCode, signalName, coreDumped, errorMessage);
    }

    /// <summary>把 stdout 与 stderr 都读完，再等命令结束。</summary>
    /// <remarks>
    /// <b>两条流是并发读的。</b>先读完一条再读另一条会死锁：
    /// 先读的那条可能一直没数据，而远端正因为另一条的窗口被吃空而停住。
    /// </remarks>
    public async ValueTask<(SshCommandResult Result, string StandardOutput, string StandardError)>
        ReadToEndAsync(CancellationToken cancellationToken = default)
    {
        Task<string> stdout = ReadAllTextAsync(StandardOutput, cancellationToken);
        Task<string> stderr = ReadAllTextAsync(StandardError, cancellationToken);

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        SshCommandResult result = await WaitAsync(cancellationToken).ConfigureAwait(false);

        return (result, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
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
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // 先摘 X11 处理器再关通道：反过来的话，关通道那一刻服务端
        // 可能还在往回开 x11 通道，而处理器已经没了。
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
