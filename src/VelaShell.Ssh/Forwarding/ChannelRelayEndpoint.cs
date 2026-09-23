// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §2.2、§六

using System.IO.Pipelines;
using VelaShell.Ssh.Channels;

namespace VelaShell.Ssh.Forwarding;

/// <summary>把一条 SSH 通道接进搬运循环。</summary>
/// <remarks>
/// 半关闭映射成 <c>CHANNEL_EOF</c> —— <b>不是</b> <c>CHANNEL_CLOSE</c>。
/// 两者的区别就是「我不再发了」与「这条通道结束了」的区别，
/// 混淆它们会截断对面还没发完的数据。
/// </remarks>
public sealed class ChannelRelayEndpoint(SshChannel channel, bool ownsChannel = false) : IRelayEndpoint
{
    /// <inheritdoc />
    public PipeReader Input => channel.StandardOutput;

    /// <inheritdoc />
    public PipeWriter Output => channel.StandardInput;

    /// <inheritdoc />
    public async ValueTask CompleteSendAsync(CancellationToken cancellationToken) =>
        await channel.SendEofAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (ownsChannel)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
