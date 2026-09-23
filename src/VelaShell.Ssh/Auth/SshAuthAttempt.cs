// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §3.4;velashell-docs/zh/ssh/spec/08-failures.md §5.3

namespace VelaShell.Ssh.Auth;

/// <summary>一次认证尝试的结果。</summary>
public enum SshAuthOutcome
{
    /// <summary>认证完成。</summary>
    Success,

    /// <summary>
    /// **这一步通过了**，但服务端还要求继续下一种方法。
    /// </summary>
    /// <remarks>
    /// 多因素认证（公钥 + OTP、密码 + OTP）在 SSH 里就是这么表达的 ——
    /// <b>没有单独的「2FA 报文」</b>。把它当成失败处理，是 2FA 支持最常见、
    /// 也最隐蔽的实现错误（velashell-docs/zh/ssh/spec/04 §3.3）。
    /// </remarks>
    PartialSuccess,

    /// <summary>这一步失败。</summary>
    Failure,

    /// <summary>没试 —— 服务端不接受这种方法。</summary>
    SkippedNotOffered,

    /// <summary>没试 —— 凭据本身取不到材料（私钥文件读不出来之类）。</summary>
    SkippedNoMaterial,
}

/// <summary>认证过程中的一条尝试记录。</summary>
/// <param name="Method">认证方法名。</param>
/// <param name="CredentialLabel">凭据的标签。<b>不含任何密钥材料。</b></param>
/// <param name="Outcome">结果。</param>
/// <param name="ServerOfferedAfter">这一步之后服务端给出的可继续方法列表。</param>
/// <param name="Detail">补充说明，例如跳过或失败的具体原因。</param>
/// <remarks>
/// 这张表会装进 <c>SshAuthenticationException</c>。它存在的理由很具体：
/// <b>要让「这台机器需要动态码」和「密码打错了」在 UI 上能区分开</b>，
/// 也要让「私钥文件读不出来」不再显示成「用户名或密码不正确」。
/// </remarks>
public readonly record struct SshAuthAttempt(
    string Method,
    string CredentialLabel,
    SshAuthOutcome Outcome,
    IReadOnlyList<string> ServerOfferedAfter,
    string? Detail = null)
{
    /// <summary>一行人话，用于日志与诊断面板。</summary>
    public override string ToString()
    {
        string outcome = Outcome switch
        {
            SshAuthOutcome.Success => "成功",
            SshAuthOutcome.PartialSuccess => "部分成功（服务端要求继续）",
            SshAuthOutcome.Failure => "失败",
            SshAuthOutcome.SkippedNotOffered => "跳过（服务端不接受这种方法）",
            SshAuthOutcome.SkippedNoMaterial => "跳过（凭据取不到材料）",
            _ => Outcome.ToString(),
        };
        return string.IsNullOrEmpty(Detail)
            ? $"{CredentialLabel}：{outcome}"
            : $"{CredentialLabel}：{outcome} —— {Detail}";
    }
}
