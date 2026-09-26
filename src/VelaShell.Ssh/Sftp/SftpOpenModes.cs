// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md

namespace VelaShell.Ssh.Sftp;

/// <summary>打开文件的方式（draft-02 §6.3 的 pflags）。</summary>
[Flags]
public enum SftpOpenModes : uint
{
    /// <summary>不带任何标志。</summary>
    None = 0,

    /// <summary>读。</summary>
    Read = 0x00000001,

    /// <summary>写。</summary>
    Write = 0x00000002,

    /// <summary>追加 —— <b>每次写都落到末尾，忽略偏移</b>。</summary>
    Append = 0x00000004,

    /// <summary>不存在则创建。</summary>
    Create = 0x00000008,

    /// <summary>截断已有内容（须配 <see cref="Create"/>）。</summary>
    Truncate = 0x00000010,

    /// <summary>已存在则失败（须配 <see cref="Create"/>）。</summary>
    Exclusive = 0x00000020,
}
