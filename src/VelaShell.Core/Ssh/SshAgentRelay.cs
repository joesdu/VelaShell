using System.Buffers.Binary;

namespace VelaShell.Core.Ssh;

/// <summary>
/// 一条 agent 转发连接的搬运循环:远端 ⇄ 策略闸门 ⇄ 本机 agent。
/// <para>
/// 刻意做成**只依赖两条 <see cref="Stream" />** 的纯逻辑放在 Core:这条路径上出错的代价是
/// 「本机 agent 被远端拿去做了不该做的事」,而它恰恰是最难在真机上复现的一段 ——
/// 用两个 <see cref="MemoryStream" /> 就能把「拒绝改钥匙」「谎报长度即断开」这些分支逐条钉住,
/// 不必架一台服务器。SSH 那一半(远端监听、通道)留在 Infrastructure。
/// </para>
/// <para>
/// 一条转发连接上可以有多个请求(对端的一次 <c>ssh</c> 通常先列身份再逐把试签名),
/// 因此本机 agent 的流**按连接复用**、首次放行时才惰性建立 —— 一次纯粹被拒的连接
/// (远端想 <c>ssh-add -D</c>)根本不会碰到本机 agent。
/// </para>
/// </summary>
public static class SshAgentRelay
{
    /// <summary>
    /// 在一条远端 agent 连接上循环搬运,直到对端关闭、发生 I/O 错误或被取消。
    /// </summary>
    /// <param name="remote">远端那一侧的双工流(SSH 通道)。</param>
    /// <param name="openAgent">按需打开一条到本机 agent 的流;同一次调用内至多用一次。</param>
    /// <param name="onRequest">每条请求处理完后的回调(可为 null)。在本方法的执行线程上同步调用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次连接上处理过的请求数。</returns>
    /// <remarks>
    /// 任何一侧的协议违例(长度越界、报文读不满)都以**断开这条连接**收场而不是抛给上层:
    /// 一条连接坏掉不该带倒整条转发 —— 对端下一次 <c>ssh</c> 会重开一条。
    /// </remarks>
    public static async Task<int> PumpAsync(
        Stream remote,
        Func<CancellationToken, Task<Stream>> openAgent,
        Action<AgentForwardRequestEvent>? onRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(openAgent);

        Stream? agent = null;
        int handled = 0;
        try
        {
            byte[] prefix = new byte[SshAgentProtocol.LengthPrefixSize];
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactlyOrEofAsync(remote, prefix, cancellationToken).ConfigureAwait(false))
                {
                    break; // 对端正常关闭
                }
                int length = SshAgentProtocol.ReadLength(prefix);
                if (length < 0)
                {
                    break; // 谎报的长度:不奉陪,断开这条连接
                }
                byte[] payload = new byte[length];
                if (!await ReadExactlyOrEofAsync(remote, payload, cancellationToken).ConfigureAwait(false))
                {
                    break; // 说了有 N 字节却没给够
                }
                handled++;

                byte messageType = payload[0];
                if (!SshAgentProtocol.IsAllowed(messageType))
                {
                    // 就地回绝。本机 agent 完全不知道刚才有人想动它 —— 这正是与 `ssh -A` 的差别。
                    await remote.WriteAsync(SshAgentProtocol.FrameSingle(SshAgentProtocol.Failure), cancellationToken)
                                .ConfigureAwait(false);
                    await remote.FlushAsync(cancellationToken).ConfigureAwait(false);
                    onRequest?.Invoke(new(messageType, false, null));
                    continue;
                }

                string? fingerprint = messageType == SshAgentProtocol.SignRequest
                                          ? SshAgentProtocol.TryGetSignRequestFingerprint(payload)
                                          : null;

                agent ??= await openAgent(cancellationToken).ConfigureAwait(false);
                await agent.WriteAsync(SshAgentProtocol.Frame(payload), cancellationToken).ConfigureAwait(false);
                await agent.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (await ReadMessageAsync(agent, cancellationToken).ConfigureAwait(false) is not { } answer)
                {
                    // 本机 agent 半路没了:告诉远端这次失败,并结束这条连接 ——
                    // 复用的流已经不可信,下一条请求只会撞同一堵墙。
                    await remote.WriteAsync(SshAgentProtocol.FrameSingle(SshAgentProtocol.Failure), cancellationToken)
                                .ConfigureAwait(false);
                    await remote.FlushAsync(cancellationToken).ConfigureAwait(false);
                    onRequest?.Invoke(new(messageType, false, fingerprint));
                    break;
                }
                await remote.WriteAsync(SshAgentProtocol.Frame(answer), cancellationToken).ConfigureAwait(false);
                await remote.FlushAsync(cancellationToken).ConfigureAwait(false);
                onRequest?.Invoke(new(messageType, true, fingerprint));
            }
        }
        finally
        {
            if (agent is not null)
            {
                await agent.DisposeAsync().ConfigureAwait(false);
            }
        }
        return handled;
    }

    /// <summary>读一条完整报文的负载(含消息号);对端关闭或协议违例时返回 <see langword="null" />。</summary>
    /// <param name="stream">要读取的流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>报文负载;读不出完整一条时为 <see langword="null" />。</returns>
    public static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] prefix = new byte[SshAgentProtocol.LengthPrefixSize];
        if (!await ReadExactlyOrEofAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        int length = SshAgentProtocol.ReadLength(prefix);
        if (length < 0)
        {
            return null;
        }
        byte[] payload = new byte[length];
        return await ReadExactlyOrEofAsync(stream, payload, cancellationToken).ConfigureAwait(false) ? payload : null;
    }

    /// <summary>把一条请求发给 agent 并取回应答;失败返回 <see langword="null" />。</summary>
    /// <param name="agent">与本机 agent 之间的流。</param>
    /// <param name="payload">请求负载(含消息号,不含长度前缀)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应答负载;失败时为 <see langword="null" />。</returns>
    public static async Task<byte[]?> ExchangeAsync(Stream agent, byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await agent.WriteAsync(SshAgentProtocol.Frame(payload), cancellationToken).ConfigureAwait(false);
        await agent.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadMessageAsync(agent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读满整个缓冲区。刚开始就 EOF 返回 <see langword="false" />(正常关闭);
    /// 读了一半才 EOF 也返回 <see langword="false" />(截断的报文,同样按断开处理)。
    /// </summary>
    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    /// <summary>构造一条 <see cref="SshAgentProtocol.RequestIdentities" /> 请求的负载。</summary>
    /// <returns>请求负载(不含长度前缀)。</returns>
    public static byte[] BuildRequestIdentities() => [SshAgentProtocol.RequestIdentities];

    /// <summary>把一个 32 位无符号整数按 SSH 线格式(大端)写进新数组,供构造报文用。</summary>
    /// <param name="value">要写入的值。</param>
    /// <returns>4 字节大端表示。</returns>
    public static byte[] Uint32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}
