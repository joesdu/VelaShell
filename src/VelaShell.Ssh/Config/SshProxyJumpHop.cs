// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  ProxyJump 的写法(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

namespace VelaShell.Ssh.Config;

/// <summary><c>ProxyJump</c> 里的一跳：<c>[user@]host[:port]</c>（<see cref="SshConfigFile.ParseProxyJump"/>）。</summary>
/// <param name="UserName">登录跳板的用户名；没写就是 <see langword="null"/>（由跳板自己的配置决定）。</param>
/// <param name="Host">跳板主机 —— 可能是 <c>ssh_config</c> 里的一个别名。IPv6 地址已去掉方括号。</param>
/// <param name="Port">端口；没写就是 <see langword="null"/>。</param>
public sealed record SshProxyJumpHop(string? UserName, string Host, int? Port);
