// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §4  REQUEST_SUCCESS / REQUEST_FAILURE
//   行为规格:    velashell-docs/zh/ssh/spec/07-forwarding.md §4.2

namespace VelaShell.Ssh.Session;

/// <summary>一个全局请求的应答。</summary>
/// <param name="Success">服务端接受了没有。</param>
/// <param name="Payload">
/// <c>REQUEST_SUCCESS</c> 后面跟的类型相关数据。
/// </param>
/// <remarks>
/// 载荷不是可有可无的：<c>tcpip-forward</c> 请求端口 <c>0</c> 时，
/// <b>服务端分配的实际端口就在这里</b>（一个 <c>uint32</c>）。
/// 只回「成不成」的 API 会让那个端口号永远拿不到，
/// 而那正是 <c>-R 0:...</c> 这种用法的全部意义。
/// </remarks>
internal readonly record struct SshGlobalRequestReply(bool Success, ReadOnlyMemory<byte> Payload);
