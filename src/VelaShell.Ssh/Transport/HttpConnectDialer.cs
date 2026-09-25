// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 9110 §9.3.6   CONNECT 方法
//   RFC 9110 §11.7    Proxy-Authenticate / Proxy-Authorization
//   RFC 9110 §15.5.8  407 Proxy Authentication Required
//   RFC 7617          Basic 认证方案
//   行为规格:         velashell-docs/zh/ssh/spec/09-dialing.md §4

using System.Buffers;
using System.Globalization;
using System.Text;

namespace VelaShell.Ssh.Transport;

/// <summary>经 HTTP 代理（<c>CONNECT</c> 方法）拨号。</summary>
/// <param name="Proxy">代理的地址。</param>
/// <remarks>
/// <para>
/// 只做 Basic 认证（RFC 7617），而且<b>第一个请求就带上</b> —— 等 407 再重试意味着
/// 代理可能关掉这条连接，而多一个往返在建连路径上是纯粹的延迟（velashell-docs/zh/ssh/spec/09 §4.1）。
/// NTLM / Negotiate / Digest 需要多轮往返，不做；要用就实现自己的拨号器。
/// </para>
/// <para>
/// 到达代理本身走 <see cref="Inner"/>，默认直连 TCP。连 HTTPS 代理时把 <see cref="Inner"/>
/// 换成一个在 TCP 上套 TLS 的拨号器即可。
/// </para>
/// </remarks>
public sealed record HttpConnectDialer(SshEndPoint Proxy) : ISshTransportDialer
{
    /// <summary>响应头的上限 —— 防一个坏代理（或根本不是代理的东西）把内存吃光。</summary>
    internal const int MaxResponseHeaderBytes = 16 * 1024;

    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>代理的凭据；<see langword="null"/> 表示不带 <c>Proxy-Authorization</c>。</summary>
    public SshProxyCredentials? Credentials { get; init; }

    /// <summary>怎么到达代理本身。默认直连 TCP。</summary>
    public ISshTransportDialer Inner { get; init; } = TcpTransportDialer.Shared;

    /// <inheritdoc />
    public SshDialKind Kind => SshDialKind.HttpConnect;

    /// <summary>换一个到达代理的方式（嵌套）。</summary>
    public HttpConnectDialer Via(ISshTransportDialer inner) => this with { Inner = inner };

    /// <inheritdoc />
    public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ProxyDialing.DialAsync(
            SshDialKind.HttpConnect, "HTTP 代理", Inner, Proxy, target,
            (stream, ct) => HandshakeAsync(stream, target.EndPoint, ct),
            cancellationToken);
    }

    /// <summary>拼 CONNECT 请求。</summary>
    internal static byte[] BuildRequest(SshEndPoint target, SshProxyCredentials? credentials)
    {
        // SshEndPoint.ToString 已经给 IPv6 字面量加了方括号 —— 请求目标要的正是这个形式。
        string authority = target.ToString();

        StringBuilder request = new();
        request.Append(CultureInfo.InvariantCulture, $"CONNECT {authority} HTTP/1.1\r\n");
        request.Append(CultureInfo.InvariantCulture, $"Host: {authority}\r\n");

        if (credentials is not null)
        {
            // RFC 7617 §2.1：user-id ":" password，UTF-8，再 base64。
            string token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"));
            request.Append(CultureInfo.InvariantCulture, $"Proxy-Authorization: Basic {token}\r\n");
        }

        request.Append("\r\n");
        return Encoding.ASCII.GetBytes(request.ToString());
    }

    private async ValueTask<Stream> HandshakeAsync(Stream stream, SshEndPoint target, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(BuildRequest(target, Credentials), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 响应头的长度事先不知道，只能读到空行为止 —— 那就难免多读。
        // 多读到的字节属于 SSH 服务端（它一连上就先说话），**必须原样交还**（velashell-docs/zh/ssh/spec/09 §2.3）。
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxResponseHeaderBytes);
        try
        {
            int filled = 0;
            int headerEnd;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(filled, MaxResponseHeaderBytes - filled), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("响应头还没读完连接就关了。");
                }
                filled += read;

                headerEnd = buffer.AsSpan(0, filled).IndexOf(HeaderTerminator);
                if (headerEnd >= 0)
                {
                    break;
                }

                if (filled == MaxResponseHeaderBytes)
                {
                    throw ProxyDialing.Refused(
                        $"HTTP 代理 {Proxy} 的响应头超过了 {MaxResponseHeaderBytes / 1024} KiB —— 它可能不是一个 HTTP 代理。");
                }
            }

            string head = Encoding.Latin1.GetString(buffer, 0, headerEnd);
            ThrowIfNotSuccess(head, target);

            int bodyStart = headerEnd + HeaderTerminator.Length;
            byte[] leftover = buffer.AsSpan(bodyStart, filled - bodyStart).ToArray();
            return leftover.Length == 0 ? stream : new PrefixedStream(leftover, stream);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ThrowIfNotSuccess(string head, SshEndPoint target)
    {
        string[] lines = head.Split("\r\n");
        string statusLine = lines[0];

        // 状态行：HTTP/1.x 空格 三位状态码 空格 原因短语。
        string[] parts = statusLine.Split(' ', 3);
        if (parts.Length < 2
            || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status))
        {
            throw ProxyDialing.Refused($"{Proxy} 回的不是 HTTP 响应（「{Truncate(statusLine)}」）—— 它可能是 SOCKS 代理，或者根本不是代理。");
        }

        if (status is >= 200 and < 300)
        {
            return;
        }

        if (status == 407)
        {
            string? challenge = lines
                .Skip(1)
                .FirstOrDefault(static l => l.StartsWith("Proxy-Authenticate:", StringComparison.OrdinalIgnoreCase));
            string scheme = challenge is null
                ? ""
                : $"（代理要求：{Truncate(challenge["Proxy-Authenticate:".Length..].Trim())}）";

            throw ProxyDialing.AuthRequired(Credentials is null
                ? $"HTTP 代理 {Proxy} 要求认证，但没有配置代理凭据{scheme}。"
                : $"HTTP 代理 {Proxy} 拒绝了用户名 {Credentials.UserName} 的凭据{scheme}。");
        }

        // 〔velashell-docs/zh/ssh/spec/09 §4.2〕高频而且用户完全猜不到的一种失败：代理只放行 80/443。
        string hint = target.Port == 22 && status is 403 or 405 or 501
            ? " 这个代理可能只放行 80/443 端口 —— 请改用 SOCKS5，或让 SSH 服务端在 443 上监听。"
            : "";

        throw ProxyDialing.Refused($"HTTP 代理 {Proxy} 拒绝连接 {target}：{Truncate(statusLine)}。{hint}");
    }

    /// <summary>代理的应答进消息之前：截短，并清掉控制字符（见 <see cref="Diagnostics.PeerText"/>）。</summary>
    private static string Truncate(string text) => Diagnostics.PeerText.Sanitize(text, 200);
}

/// <summary>先吐出一段已经读到的字节，再接着读底层流。</summary>
/// <remarks>
/// 代理握手时多读到的那几个字节属于 SSH 服务端 —— 丢了它们，标识串就残缺了。
/// </remarks>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private int _offset;

    public override bool CanRead => true;

    public override bool CanWrite => inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < prefix.Length)
        {
            int take = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.AsSpan(_offset, take).CopyTo(buffer.Span);
            _offset += take;
            return take;
        }

        return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_offset < prefix.Length)
        {
            int take = Math.Min(count, prefix.Length - _offset);
            prefix.AsSpan(_offset, take).CopyTo(buffer.AsSpan(offset));
            _offset += take;
            return take;
        }

        return inner.Read(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private int _disposed;

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
