// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §5.2  none
//   RFC 4252 §7    publickey
//   RFC 4252 §8    password
//   RFC 4256       keyboard-interactive
//   行为规格:      velashell-docs/zh/ssh/spec/04-authentication.md §2、§4、§5、§6

namespace VelaShell.Ssh.Auth;

/// <summary>一条认证凭据。</summary>
/// <remarks>
/// <para>
/// 〔决策，velashell-docs/zh/ssh/spec/04 §2.2〕<b>使用者配置的凭据列表就是全部，不做任何隐式回退。</b>
/// 不自动读 <c>~/.ssh/id_*</c>，不自动连 ssh-agent —— 除非使用者显式加了对应凭据。
/// </para>
/// <para>
/// 理由很具体：隐式回退在桌面客户端里是实打实的问题。用户在界面上选了「密码」，
/// 库却先拿某把默认私钥去试，于是服务器日志里出现莫名其妙的失败记录；
/// Windows 上 <c>SSH_AUTH_SOCK</c> 常指向 msys/WSL 的 Unix 套接字，
/// 自动连 agent 每次都撞一发异常。
/// </para>
/// </remarks>
public abstract class SshCredential
{
    /// <summary>这条凭据用哪种认证方法。</summary>
    public abstract string MethodName { get; }

    /// <summary>
    /// 给使用者看的标签，进认证尝试记录。
    /// </summary>
    /// <remarks><b>禁止包含任何密钥材料</b> —— 它会进异常、日志与诊断面板。</remarks>
    public virtual string Label => MethodName;
}

