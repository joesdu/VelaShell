// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.10  exit-status / exit-signal
//   RFC 4250 §4.2   信号名
//   行为规格:       velashell-docs/zh/ssh/spec/05-connection.md §1、§5.4

namespace VelaShell.Ssh.Channels;

/// <summary><c>CHANNEL_OPEN_FAILURE</c> 的原因码（RFC 4254 §5.1）。</summary>
public enum SshChannelOpenFailureReason : uint
{
    /// <summary>对端明确拒绝 —— 常见于服务端禁了转发。</summary>
    AdministrativelyProhibited = 1,

    /// <summary>目标连不上（转发场景）。</summary>
    ConnectFailed = 2,

    /// <summary>不认识这种通道类型。</summary>
    UnknownChannelType = 3,

    /// <summary>资源不足 —— 常见于服务端 <c>MaxSessions</c> 撞满。</summary>
    ResourceShortage = 4,
}
