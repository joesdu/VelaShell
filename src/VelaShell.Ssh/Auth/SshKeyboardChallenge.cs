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

/// <summary>服务端发来的一轮键盘交互询问。</summary>
public sealed class SshKeyboardChallenge
{
    /// <summary>对话框标题；服务端没给时是空串（不是 <see langword="null"/>）。</summary>
    public required string Name { get; init; }

    /// <summary>说明文字，例如「请按下硬件令牌上的按钮」；服务端没给时是空串。</summary>
    public required string Instruction { get; init; }

    /// <summary>提示列表。可能为空 —— 那表示这一轮只是展示信息。</summary>
    public required IReadOnlyList<SshKeyboardPrompt> Prompts { get; init; }

    /// <summary>
    /// 这一轮是不是<b>纯展示</b>（没有提示要回答）。
    /// </summary>
    /// <remarks>
    /// 〔决策，velashell-docs/zh/ssh/spec/04 §6.4〕此时<b>不该</b>弹窗要用户输入，
    /// 但仍然要把 <see cref="Instruction"/> 交给回调 ——
    /// 否则用户会对着一个没反应的界面干等，而服务端正等他去按硬件令牌。
    /// </remarks>
    public bool IsInformationalOnly => Prompts.Count == 0;
}
