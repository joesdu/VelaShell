// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md

namespace VelaShell.Ssh.Sftp;

/// <summary>ATTRS 里带了哪些字段（draft-02 §5）。</summary>
[Flags]
public enum SftpAttributeFields : uint
{
    /// <summary>什么都没带。</summary>
    None = 0,

    /// <summary>带了 <c>size</c>。</summary>
    Size = 0x00000001,

    /// <summary>
    /// 带了 <c>uid</c> <b>与</b> <c>gid</c>。
    /// </summary>
    /// <remarks>⚠️ 两者共用这**一个**标志位，要设一个就得同时给另一个。</remarks>
    UidGid = 0x00000002,

    /// <summary>带了 <c>permissions</c>。</summary>
    Permissions = 0x00000004,

    /// <summary>
    /// 带了 <c>atime</c> <b>与</b> <c>mtime</c>。
    /// </summary>
    /// <remarks>⚠️ 同样共用一个标志位。只想改 mtime 也必须一并给 atime。</remarks>
    Times = 0x00000008,

    /// <summary>带了厂商扩展属性。</summary>
    Extended = 0x80000000,
}
