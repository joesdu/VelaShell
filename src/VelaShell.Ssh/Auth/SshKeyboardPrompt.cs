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

/// <summary>键盘交互认证的一条提示。</summary>
/// <param name="Text">提示文本（<b>不可信</b>，展示时按不可信内容处理）。</param>
/// <param name="Echo">输入是否应当回显。<see langword="false"/> 时**必须**以密码方式采集。</param>
public readonly record struct SshKeyboardPrompt(string Text, bool Echo);
