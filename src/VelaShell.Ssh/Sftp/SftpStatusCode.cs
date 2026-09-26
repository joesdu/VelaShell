// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md

namespace VelaShell.Ssh.Sftp;

/// <summary>SFTP 状态码（draft-02 §7）。</summary>
public enum SftpStatusCode : uint
{
    /// <summary>成功。</summary>
    Ok = 0,

    /// <summary>
    /// 文件或目录读完了。<b>这不是错误。</b>
    /// </summary>
    /// <remarks>
    /// <c>READ</c> 时表示读到末尾，<c>READDIR</c> 时表示目录读完。
    /// 把它当错误抛出去，正常的「读完一个文件」就会变成一次异常。
    /// </remarks>
    EndOfFile = 1,

    /// <summary>文件不存在。</summary>
    NoSuchFile = 2,

    /// <summary>权限不足。</summary>
    PermissionDenied = 3,

    /// <summary>
    /// 失败 —— <b>v3 的万能错误码</b>。
    /// </summary>
    /// <remarks>
    /// 「目录非空」「文件已存在」「磁盘满」「配额超限」在 v3 里**全是这一个码**。
    /// 所以服务端给的文本消息是唯一能区分它们的信息，必须原样保留。
    /// </remarks>
    Failure = 4,

    /// <summary>报文格式错。</summary>
    BadMessage = 5,

    /// <summary>没有连接。</summary>
    NoConnection = 6,

    /// <summary>连接丢失。</summary>
    ConnectionLost = 7,

    /// <summary>服务端不支持这个操作。</summary>
    OperationUnsupported = 8,
}
