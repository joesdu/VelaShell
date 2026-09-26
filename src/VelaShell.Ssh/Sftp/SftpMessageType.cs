// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02  §3(报文类型)、§7(状态码)、§6.3(pflags)、§5(ATTRS)
//   OpenSSH PROTOCOL              扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md §二、§三、§四
//
// 为什么只做 v3：v3 是 draft-02,OpenSSH 只实现它,而 OpenSSH 是绝大多数
// SFTP 服务端的实现或行为基准。v4–v6 在真实世界里几乎见不到。
//
// 这个文件里只放协议规定的码表 —— 名字与数值都由规范定死，任何正确的实现都必然相同。
// 本库自己选的常量（块大小、上限、默认权限）在 SftpProtocol.cs。

namespace VelaShell.Ssh.Sftp;

/// <summary>SFTP 报文类型。</summary>
internal enum SftpMessageType : byte
{
    /// <summary>握手请求。<b>没有 request-id。</b></summary>
    Init = 1,

    /// <summary>握手应答。<b>没有 request-id。</b></summary>
    Version = 2,

    /// <summary>打开文件。</summary>
    Open = 3,

    /// <summary>关闭句柄。</summary>
    Close = 4,

    /// <summary>按偏移读。</summary>
    Read = 5,

    /// <summary>按偏移写。</summary>
    Write = 6,

    /// <summary>取属性，<b>不跟随</b>符号链接。</summary>
    LStat = 7,

    /// <summary>按句柄取属性。</summary>
    FStat = 8,

    /// <summary>设属性。</summary>
    SetStat = 9,

    /// <summary>按句柄设属性。</summary>
    FSetStat = 10,

    /// <summary>打开目录。</summary>
    OpenDir = 11,

    /// <summary>读一批目录项。</summary>
    ReadDir = 12,

    /// <summary>删文件。</summary>
    Remove = 13,

    /// <summary>建目录。</summary>
    MkDir = 14,

    /// <summary>删空目录。</summary>
    RmDir = 15,

    /// <summary>规范化路径。</summary>
    RealPath = 16,

    /// <summary>取属性，<b>跟随</b>符号链接。</summary>
    Stat = 17,

    /// <summary>重命名。</summary>
    Rename = 18,

    /// <summary>读链接目标。</summary>
    ReadLink = 19,

    /// <summary>建符号链接。<b>参数顺序有坑</b>，见 <c>SftpWire.WriteSymLink</c>。</summary>
    SymLink = 20,

    /// <summary>状态应答。</summary>
    Status = 101,

    /// <summary>句柄应答。</summary>
    Handle = 102,

    /// <summary>数据应答。</summary>
    Data = 103,

    /// <summary>名字列表应答。</summary>
    Name = 104,

    /// <summary>属性应答。</summary>
    Attrs = 105,

    /// <summary>厂商扩展请求。</summary>
    Extended = 200,

    /// <summary>厂商扩展应答。</summary>
    ExtendedReply = 201,
}

