// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.10  exit-status / exit-signal
//   行为规格:       velashell-docs/zh/ssh/spec/05-connection.md §5.4

namespace VelaShell.Ssh.Channels;

/// <summary>远端进程是怎么结束的。</summary>
/// <param name="ExitCode">
/// 退出码。<b>可能为 <see langword="null"/></b> —— 进程被信号杀死时只有
/// <see cref="ExitSignalName"/>，连接中断时两者都没有。
/// </param>
/// <param name="ExitSignalName">
/// 杀死进程的信号名，<b>不带 <c>SIG</c> 前缀</b>。
/// </param>
/// <param name="CoreDumped">是否产生了核心转储。</param>
/// <param name="ErrorMessage">对端给的错误说明（<b>不可信文本</b>）。</param>
/// <remarks>
/// 〔决策 velashell-docs/zh/ssh/spec/05 §5.4〕<b>不把信号编成 128+n 这样的伪退出码。</b>
/// 那是 shell 的约定，不是 SSH 的；伪造它会让「进程返回 137」
/// 和「进程被 KILL」在调用方眼里无法区分。
/// </remarks>
public readonly record struct SshExitStatus(
    int? ExitCode,
    string? ExitSignalName = null,
    bool CoreDumped = false,
    string? ErrorMessage = null)
{
    /// <summary>是否以退出码 0 正常结束。</summary>
    public bool IsSuccess => ExitCode == 0 && ExitSignalName is null;

    /// <summary>一行人话，用于日志。</summary>
    public override string ToString() =>
        ExitSignalName is not null
            ? $"被信号 {ExitSignalName} 杀死{(CoreDumped ? "（已转储核心）" : "")}"
            : ExitCode is { } code
                ? $"退出码 {code}"
                : "没有退出状态（连接中断，或对端实现不规范）";
}
