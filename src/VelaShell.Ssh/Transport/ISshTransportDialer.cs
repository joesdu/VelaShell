// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/design/architecture.md §5.1、§8 第 1 项

namespace VelaShell.Ssh.Transport;

/// <summary>
/// 把一个 <see cref="SshDialTarget"/> 变成一条可读可写的字节流。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是本库的头号扩展点。</b>代理、跳板、TUN、内存传输 —— 全部是它的实现，
/// 而不是一堆互相叠加的布尔开关。
/// </para>
/// <para>
/// 它存在的直接理由：只认 <c>host:port</c> 的库，逼得使用者为了走一个
/// HTTP/SOCKS5 代理，在本机起一个环回中继去骗它。那大约是两百行纯粹多余的代码，
/// 而且把一个本该在本机之外的连接变成了本机上的一个监听端口。
/// </para>
/// <para>
/// 返回 <see cref="Stream"/> 而不是 <see cref="System.IO.Pipelines.IDuplexPipe"/>：
/// <see cref="Stream"/> 是这一层最通用的形态（<c>NetworkStream</c>、<c>SslStream</c>、
/// SSH 隧道流都是它），分帧层自己用 <c>PipeReader.Create</c> / <c>PipeWriter.Create</c>
/// 包一层即可。反过来会让每个实现者都先学一遍 Pipelines。
/// </para>
/// </remarks>
public interface ISshTransportDialer
{
    /// <summary>这个拨号器属于哪一类。只用于诊断与日志。</summary>
    SshDialKind Kind { get; }

    /// <summary>
    /// 连接到目标并返回双工字节流。
    /// </summary>
    /// <param name="target">这一跳要连的端点，以及它在链路上的位置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可读可写的流。调用方负责释放。</returns>
    /// <remarks>
    /// <para>
    /// 实现**应当**把底层失败翻译成带
    /// <see cref="Diagnostics.SshFailureReason"/> 的异常
    /// （<c>DnsFailure</c> / <c>TcpRefused</c> / <c>ProxyRefused</c> …）——
    /// 「连不上」三个字对用户没有任何帮助。
    /// </para>
    /// <para>
    /// 实现**禁止**在本地解析 <see cref="SshEndPoint.Host"/>，除非它自己就是那个
    /// 负责建立 TCP 连接的拨号器。理由见 <see cref="SshEndPoint"/>。
    /// </para>
    /// </remarks>
    ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default);
}
