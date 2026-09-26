// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md §九

namespace VelaShell.Ssh.Sftp;

/// <summary>出错的是哪一种 SFTP 操作（<see cref="SftpException.Operation"/>）。</summary>
/// <remarks>
/// 界面要说「删除 /a 失败：权限不足」，就按它与 <see cref="SftpException.StatusCode"/> 去取本地化文案 ——
/// 不必去切异常消息里的句子。
/// </remarks>
public enum SftpOperation
{
    /// <summary>没说是哪一种。</summary>
    Unknown = 0,

    /// <summary>打开文件（<c>SSH_FXP_OPEN</c>）。</summary>
    Open,

    /// <summary>读文件（<c>SSH_FXP_READ</c>）。</summary>
    Read,

    /// <summary>写文件（<c>SSH_FXP_WRITE</c>）。</summary>
    Write,

    /// <summary>关闭句柄（<c>SSH_FXP_CLOSE</c>）。</summary>
    Close,

    /// <summary>刷到盘上（<c>fsync@openssh.com</c>）。</summary>
    Fsync,

    /// <summary>设置文件长度。</summary>
    SetLength,

    /// <summary>取属性（<c>STAT</c> / <c>LSTAT</c> / <c>FSTAT</c>）。</summary>
    GetAttributes,

    /// <summary>设置属性（<c>SETSTAT</c> / <c>FSETSTAT</c>）。</summary>
    SetAttributes,

    /// <summary>打开目录（<c>SSH_FXP_OPENDIR</c>）。</summary>
    OpenDirectory,

    /// <summary>读目录（<c>SSH_FXP_READDIR</c>）。</summary>
    ReadDirectory,

    /// <summary>建目录（<c>SSH_FXP_MKDIR</c>）。</summary>
    CreateDirectory,

    /// <summary>删目录（<c>SSH_FXP_RMDIR</c>）。</summary>
    RemoveDirectory,

    /// <summary>删文件（<c>SSH_FXP_REMOVE</c>）。</summary>
    Remove,

    /// <summary>重命名（<c>SSH_FXP_RENAME</c>）。</summary>
    Rename,

    /// <summary>原子覆盖式重命名（<c>posix-rename@openssh.com</c>）。</summary>
    PosixRename,

    /// <summary>规范化路径（<c>SSH_FXP_REALPATH</c>）。</summary>
    RealPath,

    /// <summary>读符号链接（<c>SSH_FXP_READLINK</c>）。</summary>
    ReadLink,

    /// <summary>建符号链接（<c>SSH_FXP_SYMLINK</c>）。</summary>
    CreateSymbolicLink,

    /// <summary>建硬链接（<c>hardlink@openssh.com</c>）。</summary>
    CreateHardLink,

    /// <summary>查询服务端的限额（<c>limits@openssh.com</c>）。</summary>
    QueryLimits,
}
