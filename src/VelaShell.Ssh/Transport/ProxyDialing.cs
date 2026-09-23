// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §1、§2

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Transport;

/// <summary>代理的用户名与口令。</summary>
/// <param name="UserName">用户名。</param>
/// <param name="Password">口令。</param>
/// <remarks>
/// 〔安全〕<see cref="ToString"/> 不输出口令 —— 这个对象很容易被顺手写进日志。
/// </remarks>
public sealed record SshProxyCredentials(string UserName, string Password)
{
    /// <summary>只输出用户名。</summary>
    public override string ToString() => $"{UserName}:***";
}

/// <summary>「先到达代理、再在它上面握手」这一类拨号器共用的骨架。</summary>
/// <remarks>
/// 失败时的跳信息全在这里拼：到不了代理，保留内层给的跳；
/// 到了代理但握手失败，记「到代理成功 + 这一跳失败」（velashell-docs/zh/ssh/spec/09 §2.2）。
/// </remarks>
internal static class ProxyDialing
{
    /// <summary>经 <paramref name="inner"/> 连到 <paramref name="proxy"/>，然后跑 <paramref name="handshake"/>。</summary>
    /// <param name="kind">这一跳的种类（进跳信息）。</param>
    /// <param name="kindName">给人看的代理名称（「SOCKS5 代理」）。</param>
    /// <param name="inner">怎么到达代理。</param>
    /// <param name="proxy">代理的地址。</param>
    /// <param name="target">最终要请代理连的目标。</param>
    /// <param name="handshake">在到代理的流上完成握手，返回之后的透明流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async ValueTask<Stream> DialAsync(
        SshDialKind kind,
        string kindName,
        ISshTransportDialer inner,
        SshEndPoint proxy,
        SshDialTarget target,
        Func<Stream, CancellationToken, ValueTask<Stream>> handshake,
        CancellationToken cancellationToken)
    {
        long startedAt = Environment.TickCount64;
        Stream stream;
        try
        {
            stream = await inner.DialAsync(target with { EndPoint = proxy }, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException ex)
        {
            throw DialHops.Rewrap(
                ex,
                $"连不上{kindName} {proxy}：{ex.Message}",
                DialHops.FromInnerFailure(ex, inner.Kind, proxy, startedAt));
        }

        SshHopInfo reachedProxy = DialHops.Hop(inner.Kind, proxy, succeeded: true, startedAt);
        long handshakeStartedAt = Environment.TickCount64;

        try
        {
            return await handshake(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            await DisposeQuietlyAsync(stream).ConfigureAwait(false);

            // 握手中途断开：代理说话说到一半就走了 —— 那是代理拒绝了我们，只是没说原因。
            string detail = ex is IOException
                ? $"{kindName}在握手中途断开了连接（{ex.Message}）。"
                : ex.Message;

            throw DialHops.Rewrap(
                ex,
                ex is IOException ? $"经{kindName} {proxy} 连 {target.EndPoint} 失败：{detail}" : detail,
                [reachedProxy, DialHops.Hop(kind, target.EndPoint, succeeded: false, handshakeStartedAt, detail)]);
        }
        catch (Exception)
        {
            await DisposeQuietlyAsync(stream).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>一条代理拒绝的失败（<see cref="SshFailureReason.ProxyRefused"/>）。</summary>
    public static SshConnectException Refused(string message) =>
        new(SshFailureReason.ProxyRefused, SshPhase.Dialing, message);

    /// <summary>一条代理要认证的失败（<see cref="SshFailureReason.ProxyAuthRequired"/>）。</summary>
    public static SshConnectException AuthRequired(string message) =>
        new(SshFailureReason.ProxyAuthRequired, SshPhase.Dialing, message);

    private static async ValueTask DisposeQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 失败路径上的清理不抛 —— 否则真正的原因会被盖住。
        }
    }
}
