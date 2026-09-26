// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §3.4;velashell-docs/zh/ssh/spec/08-failures.md §5.3

namespace VelaShell.Ssh.Auth;

/// <summary>一次认证尝试的结果。</summary>
/// <remarks>零值是 <see cref="Failure"/>：没填的结果不能被当成「认证完成」。</remarks>
public enum SshAuthOutcome
{
    /// <summary>这一步失败。</summary>
    Failure = 0,

    /// <summary>认证完成。</summary>
    Success,

    /// <summary>
    /// <b>这一步通过了</b>，但服务端还要求继续下一种方法。
    /// </summary>
    /// <remarks>
    /// 多因素认证（公钥 + OTP、密码 + OTP）在 SSH 里就是这么表达的 ——
    /// <b>没有单独的「2FA 报文」</b>。把它当成失败处理，是 2FA 支持最常见、
    /// 也最隐蔽的实现错误（velashell-docs/zh/ssh/spec/04 §3.3）。
    /// </remarks>
    PartialSuccess,

    /// <summary>没试 —— 服务端不接受这种方法。</summary>
    SkippedNotOffered,

    /// <summary>没试 —— 凭据本身取不到材料（私钥文件读不出来之类）。</summary>
    SkippedNoMaterial,
}
