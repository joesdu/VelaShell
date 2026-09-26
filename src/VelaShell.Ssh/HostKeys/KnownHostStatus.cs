// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH sshd(8) 的 SSH_KNOWN_HOSTS 章节（含 @cert-authority / @revoked）
//   行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.4、§5.5

namespace VelaShell.Ssh.HostKeys;

/// <summary>查 <c>known_hosts</c> 的结果。</summary>
/// <remarks>零值是 <see cref="Unknown"/>：没填的查询结果不能被当成「对得上」。</remarks>
public enum KnownHostStatus
{
    /// <summary>没见过这台主机。</summary>
    Unknown = 0,

    /// <summary>这台主机 + 这把密钥都对得上。</summary>
    Known,

    /// <summary>
    /// 见过这台主机，但<b>密钥变了</b>。
    /// </summary>
    /// <remarks>
    /// 这是最要紧的一种：它可能是中间人，也可能只是服务器重装了。
    /// <b>两者在协议层无法区分</b>，所以库不替使用者决定 —— 如实报出来。
    /// </remarks>
    Changed,

    /// <summary>这把密钥被 <c>@revoked</c> 标记过。</summary>
    Revoked,

    /// <summary>
    /// 见过这台主机，但记着的是<b>别的类型</b>的密钥；这一种类型的一把都没有。
    /// </summary>
    /// <remarks>
    /// <b>不能当成「没见过」</b>：中间人只要出示一种 known_hosts 里没记过的类型，
    /// 「密钥变了」的检查就被绕过去，接受新主机的策略还会把它悄悄记下来。
    /// 连接时会把已记录的类型排在主机密钥算法的前面（见 <see cref="IHostKeyTypePreference"/>），
    /// 正常的服务端因此谈成已知的那一种；还落到这里，就与 <see cref="Changed"/> 同样处理。
    /// <para>
    /// 对上这台主机的 <c>@cert-authority</c> 行也算「记着别的」：这台主机由 CA 管，
    /// 出示一把没有这个 CA 担保的钥（普通钥，或者别的 CA 签的证书）而那把钥又没有单独记着，同样落到这里。
    /// </para>
    /// </remarks>
    OtherKeyTypesKnown,

    /// <summary>
    /// 出示的是主机证书，签发它的 CA 在 <c>@cert-authority</c> 里对上了这台主机，但证书本身不合格
    /// （过期、主体不含这台主机、类型不对、签名验不过……）。原因见 <see cref="KnownHostLookup.CertificateProblem"/>。
    /// </summary>
    /// <remarks>
    /// <b>不退回到「没见过」</b>：这台主机配了 CA，证书不合格说明配置出了错或者路上有人
    /// （velashell-docs/zh/ssh/spec/03 §5.5）。
    /// </remarks>
    CertificateInvalid,
}
