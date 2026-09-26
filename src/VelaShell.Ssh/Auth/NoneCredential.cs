// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §5.2  none
//   RFC 4252 §7    publickey
//   RFC 4252 §8    password
//   RFC 4256       keyboard-interactive
//   行为规格:      velashell-docs/zh/ssh/spec/04-authentication.md §2、§4、§5、§6

using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Auth;

/// <summary>空认证：用来问出「这台机器接受哪些方法」。</summary>
/// <remarks>
/// 它几乎总会失败，但 <c>USERAUTH_FAILURE</c> 里带回服务端愿意接受的方法列表 ——
/// 那是唯一能问到这份清单的途径（velashell-docs/zh/ssh/spec/04 §2.1）。
/// <para>
/// 〔注意〕它也**可能成功**（服务端配了无认证）。不能假设它一定失败。
/// </para>
/// </remarks>
public sealed class NoneCredential : SshCredential
{
    /// <inheritdoc />
    public override string MethodName => SshProtocolNames.AuthNone;

    /// <inheritdoc />
    public override string Label => "none（探测可用方法）";
}
