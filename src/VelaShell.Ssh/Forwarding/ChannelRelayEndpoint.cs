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
    /// <remarks>对端的 <c>CHANNEL_CLOSE</c> 到了，或者会话没了。</remarks>
    public CancellationToken Closed => channel.Closed;

    /// <inheritdoc />
    /// <remarks>
    /// 不先发 EOF 就发 <c>CHANNEL_CLOSE</c>：对端看到的是通道被关掉，而不是一个干净的「发完了」。
    /// 不等它上线 —— 发送可能正排在背压后面，而中止要的是立刻停下；剩下的收尾交给通道的释放。
    /// </remarks>
    public ValueTask AbortAsync()
    {
        _ = CloseQuietlyAsync(channel);
        return ValueTask.CompletedTask;

        static async Task CloseQuietlyAsync(SshChannel channel)
        {
            try
            {
                await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 会话已经没了：通道自然也关了。
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (ownsChannel)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
