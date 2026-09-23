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
// ⚠️ 这个文件在相似度门禁的白名单里(scripts/ssh/similarity-gate/allowlist.txt)。
//    里面**只放协议规定的码表** —— 名字与数值都由规范定死,任何正确的实现
//    都必然相同。我们自己选的常量(块大小、上限、默认权限)在 SftpConstants.cs,
//    那个文件仍然受门禁监视。**往这里加东西前先确认它是协议事实,不是我们的选择。**

namespace VelaShell.Ssh.Sftp;

/// <summary>SFTP 报文类型。</summary>
public enum SftpMessageType : byte
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

/// <summary>打开文件的方式（draft-02 §6.3 的 pflags）。</summary>
[Flags]
public enum SftpOpenMode : uint
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

