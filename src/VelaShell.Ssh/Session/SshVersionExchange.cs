// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §4.2  版本标识串交换、前导行、255 字节上限
//   RFC 4253 §5.1  1.99 表示「同时支持 1.x 与 2.0」
//   行为规格:      velashell-docs/zh/ssh/spec/02-version-exchange.md

using System.Text;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>版本交换的结果。</summary>
/// <param name="ClientVersion">我们发出的标识串（去行尾）。</param>
/// <param name="ServerVersion">对端的标识串（去行尾）。</param>
/// <param name="PreAuthBanner">标识串之前的前导行（法律声明之类），按出现顺序。</param>
public sealed record SshVersionExchangeResult(
    string ClientVersion,
    string ServerVersion,
    IReadOnlyList<string> PreAuthBanner)
{
    /// <summary>客户端标识串的原始字节 —— **交换哈希的第一个输入**。</summary>
    public byte[] ClientVersionBytes { get; } = Encoding.ASCII.GetBytes(ClientVersion);

    /// <summary>服务端标识串的原始字节 —— 交换哈希的第二个输入。</summary>
    public byte[] ServerVersionBytes { get; } = Encoding.UTF8.GetBytes(ServerVersion);
}

/// <summary>版本标识串交换（RFC 4253 §4.2）。</summary>
public static class SshVersionExchange
{
    /// <summary>我们的软件版本串。</summary>
    /// <remarks>
    /// 〔决策，velashell-docs/zh/ssh/spec/02 §2.1〕<b>不发注释，也不发操作系统 / 运行时信息</b>，
    /// 版本号只到次版本。
    /// <para>
    /// 注释部分唯一的用途是给对端的兼容性判断提供线索，而我们不需要对端为我们做特殊处理。
    /// 反过来它是一个纯粹的指纹面 —— 把「.NET 11 / Windows 11」广播给每一台连过的机器
    /// （包括蜜罐）没有任何收益。
    /// </para>
    /// </remarks>
    public const string ClientIdentification = "SSH-2.0-VelaShell.Ssh_0.1";

    /// <summary>前导行的最大行数。</summary>
    public const int MaxBannerLines = 1024;

    /// <summary>前导行的累计字节上限。</summary>
    public const int MaxBannerBytes = 64 * 1024;

    /// <summary>
    /// 执行一次版本交换。
    /// </summary>
    /// <param name="transport">传输。</param>
    /// <param name="clientIdentification">我们的标识串；<see langword="null"/> 时用默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>我们先发，不等对端。</b>RFC 允许双方同时发；等对端先发会平白增加一个 RTT，
    /// 而且某些服务端（以及所有把 SSH 当端口探测目标的中间设备）确实在等客户端先说话。
    /// </remarks>
    public static async ValueTask<SshVersionExchangeResult> ExchangeAsync(
        SshPacketTransport transport,
        string? clientIdentification = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        string ours = clientIdentification ?? ClientIdentification;

        try
        {
            await transport.WriteLineAsync(ours, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 对端在我们开口之前就不见了（进程挂了、连接被中间设备重置）。
            // 这是一个**连接失败**，不是一个 IO 细节 —— 漏一个裸 IOException 出去，
            // 调用方就得去认它的内部消息才知道发生了什么。
            throw new SshConnectException(
                SshFailureReason.ClosedByPeer, SshPhase.VersionExchange,
                "发送版本标识串时连接已断开 —— 对端在握手开始之前就关闭了连接。", ex);
        }

        List<string> banner = [];
        int bannerBytes = 0;

        while (true)
        {
            string? line;
            try
            {
                line = await transport.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshFrameFormatException ex)
            {
                throw new SshConnectException(
                    SshFailureReason.NotAnSshServer, SshPhase.VersionExchange,
                    $"读取版本标识串失败：{ex.Message}", ex);
            }

            if (line is null)
            {
                throw new SshConnectException(
                    SshFailureReason.ClosedByPeer, SshPhase.VersionExchange,
                    banner.Count > 0
                        ? "对端在发出版本标识串之前关闭了连接。"
                        : "对端未发出任何数据就关闭了连接 —— 这个端口上可能没有 SSH 服务。");
            }

            if (line.StartsWith("SSH-", StringComparison.Ordinal))
            {
                ValidateProtocolVersion(line);
                return new SshVersionExchangeResult(ours, line, banner);
            }

            // 前导行：服务端可以在标识串之前发任意行文本（RFC 4253 §4.2）。
            // 它们不参与交换哈希，但**必须**设上限 —— 否则是一个无成本的内存耗尽面。
            bannerBytes += line.Length;
            if (banner.Count >= MaxBannerLines || bannerBytes > MaxBannerBytes)
            {
                throw new SshConnectException(
                    SshFailureReason.NotAnSshServer, SshPhase.VersionExchange,
                    $"对端在版本标识串之前发出了过多前导行（已 {banner.Count} 行 / {bannerBytes} 字节）。" +
                    "这个端口上可能没有 SSH 服务。");
            }
            banner.Add(line);
        }
    }

    private static void ValidateProtocolVersion(string identification)
    {
        // 形如 SSH-protoversion-softwareversion[ comments]
        int second = identification.IndexOf('-', 4);
        if (second < 0)
        {
            throw new SshConnectException(
                SshFailureReason.NotAnSshServer, SshPhase.VersionExchange,
                $"对端的版本标识串格式非法：{Describe(identification)}");
        }

        string protocolVersion = identification[4..second];

        // 1.99 表示「同时支持 1.x 与 2.0」（RFC 4253 §5.1），按 2.0 处理。
        if (protocolVersion is "2.0" or "1.99")
        {
            return;
        }

        throw new SshConnectException(
            SshFailureReason.VersionMismatch, SshPhase.VersionExchange,
            $"对端只支持 SSH 协议 {protocolVersion}，本库只实现 SSH-2.0。" +
            (protocolVersion.StartsWith('1')
                ? " SSH-1 已被废弃且不安全，不会被支持。"
                : ""));
    }

    /// <summary>把不可信的标识串裁短并去掉控制字符之后再放进错误消息。</summary>
    /// <remarks>
    /// 它来自一个**还没被认证**的对端，可能含控制字符 —— 原样拼进消息再打到终端上，
    /// 就是一个转义序列注入面。
    /// </remarks>
    private static string Describe(string identification)
    {
        const int limit = 64;
        string trimmed = identification.Length > limit ? identification[..limit] + "…" : identification;
        return new string([.. trimmed.Select(static c => c is >= ' ' and <= '~' ? c : '?')]);
    }
}
