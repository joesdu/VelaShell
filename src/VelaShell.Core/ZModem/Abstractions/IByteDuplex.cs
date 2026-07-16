namespace VelaShell.Core.ZModem.Abstractions;

/// <summary>
/// ZMODEM 引擎面向的最小双工字节通道:与具体传输(SSH Shell、本地 ConPTY、
/// 未来的串口 / Telnet)解耦。终端侧把截获的输出字节送入通道,
/// 引擎从 <see cref="ReadAsync" /> 拉取,并经 <see cref="WriteAsync" /> 回写协议帧。
/// </summary>
/// <remarks>
/// 该接口只搬运原始字节,绝不做任何字符编码 / 换行归一化,以保证 ZMODEM 的二进制安全。
/// </remarks>
public interface IByteDuplex : IAsyncDisposable
{
    /// <summary>
    /// 拉取下一段已到达的入站字节。无数据时异步等待;通道结束(EOF)时返回长度为 0 的内存。
    /// </summary>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>到达的字节段;EOF 时为空。</returns>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken);

    /// <summary>把一段字节写入底层传输(发送给对端)。</summary>
    /// <param name="data">要发送的字节。</param>
    /// <param name="cancellationToken">取消写入的令牌。</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>刷新底层传输的写缓冲。</summary>
    /// <param name="cancellationToken">取消刷新的令牌。</param>
    ValueTask FlushAsync(CancellationToken cancellationToken);
}
