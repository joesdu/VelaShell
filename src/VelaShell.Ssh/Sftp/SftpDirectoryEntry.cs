// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md

namespace VelaShell.Ssh.Sftp;

/// <summary>目录里的一项。</summary>
/// <param name="Name">文件名（<b>只是名字，不含路径</b>）。</param>
/// <param name="FullPath">拼好的完整路径。</param>
/// <param name="Attributes">
/// 属性。链接项描述的是<b>链接指向的对象</b>（断链时退回链接自身的属性）。
/// </param>
/// <param name="IsSymbolicLink"><b>这一项本身</b>是不是符号链接。</param>
/// <param name="LinkTarget"><c>READLINK</c> 的原文，可能是相对路径。</param>
/// <param name="IsBrokenLink">是链接，但跟随之后 stat 不到目标。</param>
/// <param name="LongName">
/// 服务端给的 <c>ls -l</c> 风格文本。<b>格式未标准化，不要解析它</b> —— 留着是给要显示原文的人用的。
/// </param>
public readonly record struct SftpDirectoryEntry(
    string Name,
    string FullPath,
    SftpFileAttributes Attributes,
    bool IsSymbolicLink,
    string? LinkTarget,
    bool IsBrokenLink,
    string LongName)
{
    /// <summary>这一项（跟随链接之后）是不是目录。</summary>
    public bool IsDirectory => Attributes.IsDirectory;

    /// <summary>长度（跟随链接之后）。</summary>
    public long Length => (long)Attributes.Size;
}
