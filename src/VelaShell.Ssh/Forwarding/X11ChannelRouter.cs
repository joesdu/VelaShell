// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.3.2  x11 通道(服务端发起,载荷里只有来源地址与端口)
//   X11 核心协议     连接建立报文里的授权字段
//   行为规格:        velashell-docs/zh/ssh/spec/07-forwarding.md §7.5.4

using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Forwarding;

/// <summary>
/// 一条连接上所有 X11 转发共用的入口：按假 cookie 把 <c>x11</c> 通道分给对应的转发。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：<c>x11</c> 通道本身不带任何能指回「哪个会话请求的」的字段
/// （RFC 4254 §6.3.2 只给来源地址与端口）。同一条连接上两个会话各自请求了 X11 转发，
/// 各有各的假 cookie、各有各的显示与有效期 —— 如果只有一个处理器位置，
/// 后请求的会把先请求的挤掉，先那个会话的 X 程序就会被当成「cookie 不对」而拒绝；
/// 任何一个释放时又会把处理器整个摘掉。
/// </para>
/// <para>
/// 唯一能区分它们的是<b>建立报文里的 cookie</b>。所以路由在读到建立报文之后才做：
/// 逐个转发做常数时间比较，对上哪个就交给哪个。一个都对不上，就是有人拿着错的
/// cookie 在敲门 —— 所有活着的转发都记一笔拒绝，让使用者看得见。
/// </para>
/// <para>
/// 并发槽位也在认出归属之后才占（按那个转发自己的上限），所以通道被本端拒绝
/// 时没有「占了槽却没走到处理」的泄漏路径。
/// </para>
/// </remarks>
internal sealed class X11ChannelRouter : IIncomingChannelHandler
{
    /// <summary>等 X11 建立报文的上限 —— 远端开了通道却不说话时不能一直挂着。</summary>
    internal static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);

    private readonly Session.SshConnection _connection;
    private readonly Lock _lock = new();
    private readonly List<X11Forwarder> _forwarders = [];

    internal X11ChannelRouter(Session.SshConnection connection) => _connection = connection;

    /// <summary>登记一个转发。第一个登记时把路由挂到连接上。</summary>
    public void Add(X11Forwarder forwarder)
    {
        bool first;
        lock (_lock)
        {
            first = _forwarders.Count == 0;
            _forwarders.Add(forwarder);
        }

        if (first)
        {
            _connection.AddIncomingChannelHandler(SshAlgorithmNames.ChannelX11, this);
        }
    }

    /// <summary>摘掉一个转发。最后一个摘掉时路由也从连接上摘下来 —— 之后的 x11 通道一律拒绝。</summary>
    public void Remove(X11Forwarder forwarder)
    {
        bool last;
        lock (_lock)
        {
            last = _forwarders.Remove(forwarder) && _forwarders.Count == 0;
        }

        if (last)
        {
            _connection.RemoveIncomingChannelHandler(SshAlgorithmNames.ChannelX11, this);
        }
    }

    private X11Forwarder[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _forwarders];
        }
    }

    /// <inheritdoc />
    public ValueTask<SshChannelOptions> GetOptionsAsync(
        string channelType, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken)
    {
        X11Forwarder[] forwarders = Snapshot();

        // 〔velashell-docs/zh/ssh/spec/07 §7.5.7〕全都过期了就在开通道这一步拒 —— 不值得为它开一条通道。
        // 还有没过期的就先接下来：归谁要等建立报文到了才知道。
        if (forwarders.Length == 0 || forwarders.All(static f => f.IsExpired))
        {
            foreach (X11Forwarder forwarder in forwarders)
            {
                forwarder.NoteRejected();
            }

            throw new SshForwardException("这条连接上的 X11 转发都已过期 —— 拒绝新的 x11 通道。");
        }

        return ValueTask.FromResult(SshChannelOptions.Default);
    }

    /// <inheritdoc />
    public async Task HandleAsync(
        SshChannel channel, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        (X11SetupMessage.Parsed Setup, byte[] Original)? setup;
        try
        {
            setup = await ReadSetupAsync(channel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            setup = null;   // 建立报文迟迟不来
        }

        X11Forwarder[] forwarders = Snapshot();

        if (setup is { } received)
        {
            // ⚠️ 逐个做**常数时间**比较；只认 MIT-MAGIC-COOKIE-1（都在 TryRewriteCookie 里）。
            foreach (X11Forwarder forwarder in forwarders)
            {
                if (forwarder.TryRewrite(received.Original, received.Setup, out byte[] rewritten))
                {
                    await forwarder.RelayAsync(channel, rewritten, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }

        // 报文不合法、迟迟不来、或者 cookie 谁都对不上：**绝不往本机 X server 上连**。
        foreach (X11Forwarder forwarder in forwarders)
        {
            forwarder.NoteRejected();
        }
    }

    /// <summary>读完整的建立报文；不合法或对端提前关闭时返回 <see langword="null"/>。</summary>
    private static async ValueTask<(X11SetupMessage.Parsed Setup, byte[] Original)?> ReadSetupAsync(
        SshChannel channel, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SetupTimeout);

        PipeReader reader = channel.StandardOutput;

        while (true)
        {
            ReadResult read = await reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;

            bool parsed;
            X11SetupMessage.Parsed setup;
            try
            {
                parsed = X11SetupMessage.TryParse(buffer, out setup);
            }
            catch (FormatException)
            {
                // 报文本身就不合法 —— 不是 X11 客户端，或者是在乱打。
                reader.AdvanceTo(buffer.Start, buffer.End);
                return null;
            }

            if (parsed)
            {
                byte[] original = buffer.Slice(0, setup.TotalLength).ToArray();

                // 建立报文**消费掉**，后面的字节留给对搬。
                reader.AdvanceTo(buffer.GetPosition(setup.TotalLength));
                return (setup, original);
            }

            if (read.IsCompleted)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                return null;
            }

            // 还没收全：**examined 要到 buffer.End**，否则不会等新数据。
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }
}
