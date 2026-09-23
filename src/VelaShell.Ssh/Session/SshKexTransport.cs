// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.1  发出 KEXINIT 之后到 NEWKEYS 之前只许发传输层消息
//   RFC 4253 §7.3  NEWKEYS 的两个方向互不等待
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §八(重协商)

using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>密钥交换期间，报文从哪来、往哪去。</summary>
/// <remarks>
/// <para>
/// 有这个抽象是因为<b>首次交换与重协商的处境完全不同</b>：
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>首次交换</b>：连接还没开始跑，这条传输上只有我们一个读者、一个写者。
///     直接读写传输就行。
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>重协商</b>：会话正在跑。读那一侧归接收循环（它是唯一的读者），
///     写那一侧归会话的发送锁（N 条通道的泵都在写）。
///     密钥交换必须<b>借道这两者</b>，不能自己动传输 ——
///     否则轻则报文交错写坏，重则在换密钥的瞬间和别人抢发。
///     </description>
///   </item>
/// </list>
/// <para>
/// 把它做成接口而不是一堆 <c>if (isRekey)</c>，是因为那两处「必须原子」的地方
/// （发 NEWKEYS + 换发送密钥）在两种处境下的正确实现<b>根本不是同一段代码</b>。
/// </para>
/// </remarks>
internal interface ISshKexTransport
{
    /// <summary>发一个密钥交换报文。</summary>
    /// <remarks>发送侧的串行化由实现负责。</remarks>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);

    /// <summary>读下一个密钥交换报文。</summary>
    /// <remarks>
    /// 重协商期间<b>仍然会收到通道数据</b>（闸门只管发送方向，
    /// 见 <c>velashell-docs/zh/ssh/spec/03</c> §8.3）。那些报文由实现自己派发掉，
    /// 不会从这里冒出来。
    /// </remarks>
    ValueTask<SshInboundPacket> ReadPacketAsync(CancellationToken cancellationToken);

    /// <summary>发出 <c>NEWKEYS</c>，并在同一个临界区里换上新的发送侧状态。</summary>
    /// <param name="send">新的发送密码套件。</param>
    /// <param name="sendCompressor">
    /// 新的发送压缩器；<see langword="null"/> 表示<b>不动压缩</b>。
    /// </param>
    /// <param name="strictKeyExchange">是否启用了严格 KEX。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这几步必须原子。</b>
    /// 发出 <c>NEWKEYS</c> 之后，我们发的<b>下一个</b>报文就要用新密钥；
    /// 中间一旦被别的发送者插进来，那一帧会用旧密钥发出而对端已经在用新密钥解 ——
    /// 症状是「连接跑了一阵忽然开始解密失败」，而那时早已看不出是重协商的问题。
    /// </para>
    /// <para>
    /// <b>压缩上下文和密钥在同一个时刻换。</b>
    /// 压缩发生在加密之前，两者都以 <c>NEWKEYS</c> 为界；
    /// 少换一边的症状是对端解压失败（<c>velashell-docs/zh/ssh/spec/01</c> §六）。
    /// </para>
    /// <para>
    /// <paramref name="sendCompressor"/> 允许为 <see langword="null"/> 是因为
    /// <b>首次交换时不能在这里动压缩</b> —— <c>zlib@openssh.com</c> 要等认证成功
    /// 之后才启用（CRIME 那一类旁路）。只有重协商才在这里换。
    /// </para>
    /// </remarks>
    ValueTask SendNewKeysAndSwitchSendAsync(
        ISshCipherSuite send,
        ISshCompressor? sendCompressor,
        bool strictKeyExchange,
        CancellationToken cancellationToken);

    /// <summary>换上新的接收侧状态（收到对端的 <c>NEWKEYS</c> 之后）。</summary>
    /// <param name="receive">新的接收密码套件。</param>
    /// <param name="receiveCompressor">
    /// 新的接收压缩器；<see langword="null"/> 表示<b>不动压缩</b>（理由同上）。
    /// </param>
    /// <param name="strictKeyExchange">是否启用了严格 KEX。</param>
    void SwitchReceive(
        ISshCipherSuite receive, ISshCompressor? receiveCompressor, bool strictKeyExchange);
}

/// <summary>首次密钥交换：直接读写传输。</summary>
/// <remarks>
/// 这时候连接还没跑起来，没有别的读者与写者，所以不需要任何串行化 ——
/// 这也正是它能这么简单的原因。
/// </remarks>
internal sealed class DirectKexTransport(SshPacketTransport transport) : ISshKexTransport
{
    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        transport.WritePacket(packet.Span);
        await transport.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<SshInboundPacket> ReadPacketAsync(CancellationToken cancellationToken) =>
        transport.ReadPacketAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask SendNewKeysAndSwitchSendAsync(
        ISshCipherSuite send,
        ISshCompressor? sendCompressor,
        bool strictKeyExchange,
        CancellationToken cancellationToken)
    {
        await SendAsync(new[] { (byte)SshMessageNumber.NewKeys }, cancellationToken).ConfigureAwait(false);
        transport.SetSendCipherSuite(send, strictKeyExchange);

        if (sendCompressor is not null)
        {
            transport.SetSendCompressor(sendCompressor);
        }
    }

    /// <inheritdoc />
    public void SwitchReceive(
        ISshCipherSuite receive, ISshCompressor? receiveCompressor, bool strictKeyExchange)
    {
        transport.SetReceiveCipherSuite(receive, strictKeyExchange);

        if (receiveCompressor is not null)
        {
            transport.SetReceiveCompressor(receiveCompressor);
        }
    }
}
