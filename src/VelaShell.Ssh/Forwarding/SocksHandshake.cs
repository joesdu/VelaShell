// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 1928  SOCKS5
//   行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §三

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;

namespace VelaShell.Ssh.Forwarding;

/// <summary>SOCKS5 的应答码（RFC 1928 §6）。</summary>
public enum SocksReply : byte
{
    /// <summary>成功。</summary>
    Succeeded = 0x00,

    /// <summary>一般性失败。</summary>
    GeneralFailure = 0x01,

    /// <summary>规则不允许。</summary>
    NotAllowed = 0x02,

    /// <summary>网络不可达。</summary>
    NetworkUnreachable = 0x03,

    /// <summary>主机不可达。</summary>
    HostUnreachable = 0x04,

    /// <summary>连接被拒。</summary>
    ConnectionRefused = 0x05,

    /// <summary>TTL 到期。</summary>
    TtlExpired = 0x06,

    /// <summary>命令不支持。</summary>
    CommandNotSupported = 0x07,

    /// <summary>地址类型不支持。</summary>
    AddressTypeNotSupported = 0x08,
}

/// <summary>客户端在 SOCKS 握手里给出的目标。</summary>
/// <param name="Host">
/// 目标主机。<b>域名原样保留，不在本地解析。</b>
/// </param>
/// <param name="Port">目标端口。</param>
/// <param name="AddressType">原始地址类型字节（回应答时要原样带回）。</param>
public readonly record struct SocksTarget(string Host, int Port, byte AddressType);

/// <summary>SOCKS5 握手。</summary>
/// <remarks>
/// <para>
/// 我们只实现 RFC 1928 的一个子集：<b>只支持 SOCKS5</b>（SOCKS4/4a 不实现）、
/// <b>只接受「无认证」</b>、<b>只支持 <c>CONNECT</c></b>。
/// </para>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/07 §3.1〕<b>不实现 SOCKS 认证。</b>
/// 这个监听默认只在环回上，同机进程本来就能连；加一层用户名密码
/// 只会给人一种「它是安全边界」的错觉，而它不是。真要限制访问，
/// 用操作系统的手段（防火墙、命名空间）。
/// </para>
/// </remarks>
public static class SocksHandshake
{
    private const byte Version5 = 0x05;
    private const byte NoAuthentication = 0x00;
    private const byte NoAcceptableMethods = 0xFF;
    private const byte CommandConnect = 0x01;

    private const byte AddressIPv4 = 0x01;
    private const byte AddressDomain = 0x03;
    private const byte AddressIPv6 = 0x04;

    /// <summary>跑完握手的前半段，拿到客户端想连哪里。</summary>
    /// <returns>目标；握手非法或命令不支持时为 <see langword="null"/>（已经回过应答了）。</returns>
    public static async ValueTask<SocksTarget?> ReadRequestAsync(
        PipeReader input, PipeWriter output, CancellationToken cancellationToken = default)
    {
        // ① 方法协商：VER ‖ NMETHODS ‖ METHODS...
        byte[]? greeting = await ReadExactlyAsync(input, 2, cancellationToken).ConfigureAwait(false);
        if (greeting is null)
        {
            return null;
        }

        if (greeting[0] != Version5)
        {
            // SOCKS4 的第一个字节是 0x04。我们不实现它，而且**不能**按 SOCKS5 回应答
            // —— 那会让对面按错误的格式解析。直接断开。
            return null;
        }

        byte[]? methods = await ReadExactlyAsync(input, greeting[1], cancellationToken).ConfigureAwait(false);
        if (methods is null)
        {
            return null;
        }

        if (!methods.Contains(NoAuthentication))
        {
            await WriteAsync(output, [Version5, NoAcceptableMethods], cancellationToken).ConfigureAwait(false);
            return null;
        }

        await WriteAsync(output, [Version5, NoAuthentication], cancellationToken).ConfigureAwait(false);

        // ② 请求：VER ‖ CMD ‖ RSV ‖ ATYP ‖ DST.ADDR ‖ DST.PORT
        byte[]? header = await ReadExactlyAsync(input, 4, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        if (header[0] != Version5)
        {
            return null;
        }

        byte addressType = header[3];

        if (header[1] != CommandConnect)
        {
            // BIND 与 UDP ASSOCIATE 不实现 —— 回 0x07 而不是沉默，
            // 让对面知道是「不支持」而不是「连不上」。
            await WriteReplyAsync(output, SocksReply.CommandNotSupported, addressType, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        string? host = await ReadAddressAsync(input, addressType, cancellationToken).ConfigureAwait(false);
        if (host is null)
        {
            await WriteReplyAsync(output, SocksReply.AddressTypeNotSupported, addressType, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        byte[]? portBytes = await ReadExactlyAsync(input, 2, cancellationToken).ConfigureAwait(false);
        if (portBytes is null)
        {
            return null;
        }

        int port = (portBytes[0] << 8) | portBytes[1];
        return new SocksTarget(host, port, addressType);
    }

    /// <summary>回一个应答。</summary>
    /// <remarks>
    /// 应答码对不对是有实际后果的：<c>curl</c> 与浏览器会根据它决定要不要重试、
    /// 以及报给用户哪句话。一律回 <see cref="SocksReply.GeneralFailure"/> 等于把信息丢了。
    /// </remarks>
    public static async ValueTask WriteReplyAsync(
        PipeWriter output, SocksReply reply, byte addressType, CancellationToken cancellationToken = default)
    {
        // BND.ADDR / BND.PORT 我们填 0 —— CONNECT 的应答里它们没有实际用途，
        // 而编一个假地址只会误导排查。
        byte[] response = addressType switch
        {
            AddressIPv6 => [Version5, (byte)reply, 0x00, AddressIPv6, .. new byte[16], 0, 0],
            _ => [Version5, (byte)reply, 0x00, AddressIPv4, 0, 0, 0, 0, 0, 0],
        };

        await WriteAsync(output, response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把 SSH 的通道打开失败原因翻成 SOCKS 应答码。</summary>
    public static SocksReply MapFailure(Channels.SshChannelOpenFailureReason? reason) => reason switch
    {
        Channels.SshChannelOpenFailureReason.AdministrativelyProhibited => SocksReply.NotAllowed,
        Channels.SshChannelOpenFailureReason.ConnectFailed => SocksReply.ConnectionRefused,
        Channels.SshChannelOpenFailureReason.UnknownChannelType => SocksReply.GeneralFailure,
        Channels.SshChannelOpenFailureReason.ResourceShortage => SocksReply.GeneralFailure,
        _ => SocksReply.GeneralFailure,
    };

    private static async ValueTask<string?> ReadAddressAsync(
        PipeReader input, byte addressType, CancellationToken cancellationToken)
    {
        if (addressType is AddressIPv4 or AddressIPv6)
        {
            int length = addressType == AddressIPv4 ? 4 : 16;
            byte[]? bytes = await ReadExactlyAsync(input, length, cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : new IPAddress(bytes).ToString();
        }

        if (addressType != AddressDomain)
        {
            return null;
        }

        byte[]? lengthByte = await ReadExactlyAsync(input, 1, cancellationToken).ConfigureAwait(false);
        if (lengthByte is null)
        {
            return null;
        }

        byte[]? name = await ReadExactlyAsync(input, lengthByte[0], cancellationToken).ConfigureAwait(false);

        // 空名字不是一个目标：放过去的话，开隧道那一步因为参数不合法而抛，客户端一句应答也收不到，
        // 只能干等到超时。当成不支持的地址回掉。
        if (name is null || name.Length == 0)
        {
            return null;
        }

        // 〔决策 velashell-docs/zh/ssh/spec/07 §3.1〕**域名不在本地解析，原样交给服务端。**
        // 这是动态转发最重要的一条语义 —— curl --socks5-hostname 依赖它。
        // 本地解析会导致「DNS 走本地、连接走隧道」的分裂，
        // 在内网域名场景下直接失效，而且泄漏了访问目标。
        return Encoding.ASCII.GetString(name);
    }

    private static async ValueTask<byte[]?> ReadExactlyAsync(
        PipeReader input, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return [];
        }

        while (true)
        {
            ReadResult read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;

            if (buffer.Length >= count)
            {
                byte[] result = buffer.Slice(0, count).ToArray();
                input.AdvanceTo(buffer.GetPosition(count));
                return result;
            }

            if (read.IsCompleted)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                return null;   // 对面在握手中途走了
            }

            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static async ValueTask WriteAsync(
        PipeWriter output, byte[] bytes, CancellationToken cancellationToken)
    {
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
