// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.1  CHANNEL_OPEN / CONFIRMATION / FAILURE
//   RFC 4254 §5.2  CHANNEL_WINDOW_ADJUST / DATA / EXTENDED_DATA
//   RFC 4254 §5.3  CHANNEL_EOF / CHANNEL_CLOSE
//   RFC 4254 §5.4  CHANNEL_REQUEST / SUCCESS / FAILURE
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§3、§4、§5

namespace VelaShell.Ssh.Channels;

/// <summary>通道的状态。</summary>
public enum SshChannelState
{
    /// <summary>已发 <c>CHANNEL_OPEN</c>，还没收到应答。</summary>
    Opening,

    /// <summary>双向都能收发。</summary>
    Open,

    /// <summary>我们发过 <c>CHANNEL_EOF</c>，不再发数据；<b>仍然可以收</b>。</summary>
    LocalEof,

    /// <summary>对端发过 <c>CHANNEL_EOF</c>；<b>我们仍然可以发</b>。</summary>
    RemoteEof,

    /// <summary>双向都发过 EOF，但通道还没关。</summary>
    BothEof,

    /// <summary><c>CHANNEL_CLOSE</c> 已收或已发，等另一半。</summary>
    Closing,

    /// <summary>双向 <c>CHANNEL_CLOSE</c> 都走完了。</summary>
    Closed,
}
