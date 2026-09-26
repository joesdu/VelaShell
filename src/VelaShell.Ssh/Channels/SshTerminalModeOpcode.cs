// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §8    终端模式编码
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.3

namespace VelaShell.Ssh.Channels;

/// <summary>终端模式的操作码（RFC 4254 §8）。</summary>
/// <remarks>
/// 只给常用的具名成员；其余的直接传裸数值即可 ——
/// 这张表不可能穷举，而未知的值本来就该原样传过去。
/// </remarks>
public enum SshTerminalModeOpcode : byte
{
    /// <summary>模式列表结束。<b>必须</b>以它结尾，漏掉 OpenSSH 会拒绝整个 <c>pty-req</c>。</summary>
    EndOfOptions = 0,

    /// <summary>中断字符，通常是 <c>^C</c>（VINTR）。</summary>
    InterruptCharacter = 1,

    /// <summary>退出字符（VQUIT）。</summary>
    QuitCharacter = 2,

    /// <summary>退格字符（VERASE）。</summary>
    EraseCharacter = 3,

    /// <summary>整行删除字符（VKILL）。</summary>
    KillCharacter = 4,

    /// <summary>文件结束字符，通常是 <c>^D</c>（VEOF）。</summary>
    EndOfFileCharacter = 5,

    /// <summary>输入时把 CR 转成 NL。</summary>
    MapCrToNlOnInput = 36,

    /// <summary>输入按 UTF-8 处理。</summary>
    Utf8Input = 42,

    /// <summary>规范模式（行编辑）。</summary>
    CanonicalInput = 51,

    /// <summary>回显输入。</summary>
    Echo = 53,

    /// <summary>输出时把 NL 转成 CR-NL。</summary>
    MapNlToCrNlOnOutput = 72,

    /// <summary>输入波特率。</summary>
    InputSpeed = 128,

    /// <summary>输出波特率。</summary>
    OutputSpeed = 129,
}
