// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 1928  SOCKS 第 5 版:问候、方法选择、CONNECT 请求与应答
//   RFC 1929  SOCKS5 用户名/口令认证
//   行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §3

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VelaShell.Ssh.Transport;

/// <summary>经 SOCKS5 代理拨号。</summary>
/// <param name="Proxy">代理的地址。</param>
/// <remarks>
/// <para>
/// <b>目标主机名交给代理去解析</b>，不在本机解析（velashell-docs/zh/ssh/spec/09 §2.1）——
/// 内网域名在本机解析不出来，本机解析还会把访问目标泄漏给本机 DNS。
/// </para>
/// <para>
/// 到达代理本身走 <see cref="Inner"/>，默认直连 TCP；换成另一个代理或跳板就是嵌套。
/// </para>
/// </remarks>
internal sealed record Socks5Dialer(SshEndPoint Proxy) : ISshTransportDialer
{
    private const byte Version = 5;
    private const byte MethodNoAuthentication = 0;
    private const byte MethodUserPassword = 2;
    private const byte MethodNoneAcceptable = 0xFF;
    private const byte UserPasswordVersion = 1;
    private const byte CommandConnect = 1;
    private const byte AddressIPv4 = 1;
    private const byte AddressDomain = 3;
    private const byte AddressIPv6 = 4;

    /// <summary>代理的凭据；<see langword="null"/> 表示只提供「无需认证」。</summary>
    public SshProxyCredentials? Credentials { get; init; }

    /// <summary>怎么到达代理本身。默认直连 TCP。</summary>
    public ISshTransportDialer Inner { get; init; } = TcpTransportDialer.Shared;

    /// <inheritdoc />
    public SshDialKind Kind => SshDialKind.Socks5;

    /// <inheritdoc />
    public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ProxyDialing.DialAsync(
            SshDialKind.Socks5, "SOCKS5 代理", Inner, Proxy, target,
            (stream, ct) => HandshakeAsync(stream, target.EndPoint, ct),
            cancellationToken);
    }

    private async ValueTask<Stream> HandshakeAsync(Stream stream, SshEndPoint target, CancellationToken cancellationToken)
    {
        // ① 问候：永远提供「无需认证」；配了凭据再加「用户名/口令」。
        byte[] greeting = Credentials is null
            ? [Version, 1, MethodNoAuthentication]
            : [Version, 2, MethodNoAuthentication, MethodUserPassword];
        await stream.WriteAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        byte[] choice = new byte[2];
        await stream.ReadExactlyAsync(choice, cancellationToken).ConfigureAwait(false);
        ExpectVersion(choice[0]);

        switch (choice[1])
        {
            case MethodNoAuthentication:
                break;

            case MethodUserPassword when Credentials is not null:
                await AuthenticateAsync(stream, Credentials, cancellationToken).ConfigureAwait(false);
                break;

            case MethodNoneAcceptable:
                throw Credentials is null
                    ? ProxyDialing.AuthRequired($"SOCKS5 代理 {Proxy} 要求认证，但没有配置代理凭据。")
                    : ProxyDialing.Refused($"SOCKS5 代理 {Proxy} 不接受我们提供的任何认证方法。");

            default:
                throw ProxyDialing.Refused(
                    $"SOCKS5 代理 {Proxy} 选了一个我们没提供的认证方法（0x{choice[1]:X2}）—— 这是协议错误。");
        }

        // ② CONNECT。
        await stream.WriteAsync(BuildConnectRequest(target), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // ③ 应答：定长的头 + 按地址类型定长的绑定地址 + 端口。
        //    ⚠️ **一个字节都不多读** —— 应答后面紧跟着的就是 SSH 服务端的标识串。
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        ExpectVersion(header[0]);

        int addressLength = header[3] switch
        {
            AddressIPv4 => 4,
            AddressIPv6 => 16,
            AddressDomain => await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw ProxyDialing.Refused(
                $"SOCKS5 代理 {Proxy} 的应答里地址类型非法（0x{header[3]:X2}）。"),
        };

        byte[] bound = new byte[addressLength + 2];
        await stream.ReadExactlyAsync(bound, cancellationToken).ConfigureAwait(false);

        if (header[1] != 0)
        {
            throw ProxyDialing.Refused(
                $"SOCKS5 代理 {Proxy} 拒绝连接 {target}：{DescribeReply(header[1])}（结果码 {header[1]}）。");
        }

        return stream;
    }

    private async ValueTask AuthenticateAsync(
        Stream stream, SshProxyCredentials credentials, CancellationToken cancellationToken)
    {
        byte[] user = Encoding.UTF8.GetBytes(credentials.UserName);
        byte[] password = Encoding.UTF8.GetBytes(credentials.Password);

        // RFC 1929：长度各占一个字节。超了就在本地拒绝，**不截断** ——
        // 截断之后发出去的是另一套凭据，失败原因会变得莫名其妙。
        if (user.Length is 0 or > 255 || password.Length is 0 or > 255)
        {
            throw ProxyDialing.AuthRequired(
                "SOCKS5 的用户名与口令编码后都必须在 1–255 字节之间（RFC 1929）。");
        }

        byte[] request = new byte[3 + user.Length + password.Length];
        request[0] = UserPasswordVersion;
        request[1] = (byte)user.Length;
        user.CopyTo(request, 2);
        request[2 + user.Length] = (byte)password.Length;
        password.CopyTo(request, 3 + user.Length);

        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        byte[] reply = new byte[2];
        await stream.ReadExactlyAsync(reply, cancellationToken).ConfigureAwait(false);
        if (reply[1] != 0)
        {
            throw ProxyDialing.AuthRequired($"SOCKS5 代理 {Proxy} 拒绝了用户名 {credentials.UserName} 的凭据。");
        }
    }

    /// <summary>拼 CONNECT 请求。主机名不是 IP 字面量时按域名发 —— 交给代理解析。</summary>
    internal static byte[] BuildConnectRequest(SshEndPoint target)
    {
        byte[] address;
        byte addressType;

        if (IPAddress.TryParse(target.Host, out IPAddress? ip))
        {
            address = ip.GetAddressBytes();
            addressType = ip.AddressFamily == AddressFamily.InterNetworkV6 ? AddressIPv6 : AddressIPv4;
        }
        else
        {
            // 国际化域名先转成 Punycode —— 直接按 ASCII 编码会把非 ASCII 字符变成「?」，
            // 代理拿到的就是另一个（不存在的）名字。
            string ascii;
            try
            {
                ascii = new System.Globalization.IdnMapping().GetAscii(target.Host);
            }
            catch (ArgumentException ex)
            {
                throw ProxyDialing.Refused($"主机名 {target.Host} 不是合法的域名：{ex.Message}");
            }

            byte[] name = Encoding.ASCII.GetBytes(ascii);
            if (name.Length is 0 or > 255)
            {
                throw ProxyDialing.Refused($"主机名 {target.Host} 超过了 SOCKS5 允许的 255 字节。");
            }

            address = [(byte)name.Length, .. name];
            addressType = AddressDomain;
        }

        byte[] request = new byte[4 + address.Length + 2];
        request[0] = Version;
        request[1] = CommandConnect;
        request[2] = 0;
        request[3] = addressType;
        address.CopyTo(request, 4);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4 + address.Length), (ushort)target.Port);
        return request;
    }

    private void ExpectVersion(byte version)
    {
        if (version != Version)
        {
            throw ProxyDialing.Refused(
                $"{Proxy} 回的不是 SOCKS5（版本字节 0x{version:X2}）—— 它可能是 HTTP 代理，或者根本不是代理。");
        }
    }

    private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
        return one[0];
    }

    private static string DescribeReply(byte code) => code switch
    {
        1 => "代理内部错误",
        2 => "代理的规则不允许这个连接",
        3 => "网络不可达",
        4 => "主机不可达",
        5 => "目标拒绝连接",
        6 => "TTL 过期",
        7 => "代理不支持 CONNECT 命令",
        8 => "代理不支持这种地址类型",
        _ => "未知错误",
    };
}
