// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §7   publickey 的两段式与签名输入
//   行为规格:     velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/design/architecture.md §8 第 5 项

using VelaShell.Ssh.HostKeys;

namespace VelaShell.Ssh.Auth;

/// <summary>
/// 一把能签名的私钥。<b>私钥从哪来由实现决定，可以从不进程内。</b>
/// </summary>
/// <remarks>
/// 这是架构 §8 第 5 项的扩展点：文件私钥、ssh-agent、PKCS#11、HSM、
/// 云密钥服务都是它的实现。
/// </remarks>
public interface ISshSigner
{
    /// <summary>对应的公钥。</summary>
    SshPublicKey PublicKey { get; }

    /// <summary>这把密钥能用的签名算法名，按偏好排序。</summary>
    IReadOnlyList<string> SignatureAlgorithms { get; }

    /// <summary>
    /// 签名是否**在本地且代价低廉**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这个属性决定公钥认证走一段式还是两段式（velashell-docs/zh/ssh/spec/04 §4.1）：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>本地私钥</b>（<see langword="true"/>）：直接签，省一个 RTT。
    ///   服务端不认这把钥时白签一次，而本地签名很便宜。</item>
    ///   <item><b>外部签名</b>（<see langword="false"/>）：先问「你认这把公钥吗」，
    ///   认了再签。为一把服务端根本不认的密钥去让用户按硬件键、输 PIN，
    ///   或者发一次网络请求，是不可接受的。</item>
    /// </list>
    /// <para>
    /// <b>没有默认实现，刻意的。</b>往「便宜」那边默认的话，一个忘了覆写的硬件签名器会在服务端
    /// 还没认这把公钥时就让用户去按键 —— 每个实现都得自己说清楚。
    /// </para>
    /// </remarks>
    bool IsLocalAndCheap { get; }

    /// <summary>对数据签名，产出 SSH 格式的签名 blob。</summary>
    /// <param name="data">被签名的数据。</param>
    /// <param name="algorithm">签名算法名，取自 <see cref="SignatureAlgorithms"/>。</param>
    /// <param name="cancellationToken">取消令牌（外部签名可能要等用户操作）。</param>
    ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default);
}
