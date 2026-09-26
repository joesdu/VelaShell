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
