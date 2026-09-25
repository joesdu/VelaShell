// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02  §3、§6.2
//   OpenSSH PROTOCOL              扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md §二、§三、§四
//
// 这个文件放的是**我们自己选的**常量(块大小、上限、默认权限)与扩展名字符串。
// 协议规定的那几张码表(报文类型、状态码、pflags、ATTRS 位)在
// SftpProtocolCodes.cs —— 它们是协议事实,表达方式唯一。
// 拆开是为了让协议事实与我们的选择各在一处。

namespace VelaShell.Ssh.Sftp;

/// <summary>SFTP 协议层的常量。</summary>
public static class SftpProtocol
{
    /// <summary>我们说的版本，也是唯一实现的版本。</summary>
    public const uint Version = 3;

    /// <summary>
    /// 单个 SFTP 报文的长度上限。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §二〕数据块上限 256 KiB 加协议头余量。超限断开通道。
    /// </remarks>
    public const int MaxMessageLength = (256 * 1024) + 1024;

    /// <summary>句柄的长度上限（draft-02 规定）。</summary>
    public const int MaxHandleLength = 256;

    /// <summary>路径与文件名的长度上限。</summary>
    public const int MaxPathLength = 64 * 1024;

    /// <summary>没有 <c>limits@openssh.com</c> 时的保守块大小。</summary>
    public const int DefaultBlockSize = 32 * 1024;

    /// <summary>块大小的上限：一块 <c>DATA</c> 应答连同协议头要装得进 <see cref="MaxMessageLength"/>。</summary>
    public const int MaxBlockSize = 256 * 1024;

    /// <summary>
    /// 从服务端的 <c>max-packet-length</c> 里给请求头留的余量（长度、类型、编号、句柄、偏移…）。
    /// </summary>
    /// <remarks>与 <see cref="MaxMessageLength"/> 的余量取法一致；句柄最长 256 字节，1 KiB 绰绰有余。</remarks>
    public const int RequestHeaderAllowance = 1024;

    /// <summary>创建文件的默认权限。</summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §4.1〕**传明确的值**。不传 ATTRS 会让服务端用它自己的
    /// 默认值（通常受 umask 影响），结果不可预测。
    /// </remarks>
    public const uint DefaultFilePermissions = 0b110_100_100;   // 0644

    /// <summary>创建目录的默认权限。</summary>
    public const uint DefaultDirectoryPermissions = 0b111_101_101;   // 0755

    /// <summary>权限字段里的文件类型掩码（<c>S_IFMT</c>）。</summary>
    /// <remarks><b>v3 没有单独的类型字段，类型只能从权限的高位取。</b></remarks>
    public const uint FileTypeMask = 0xF000;

    /// <summary>普通文件（<c>S_IFREG</c>）。</summary>
    public const uint FileTypeRegular = 0x8000;

    /// <summary>目录（<c>S_IFDIR</c>）。</summary>
    public const uint FileTypeDirectory = 0x4000;

    /// <summary>符号链接（<c>S_IFLNK</c>）。</summary>
    public const uint FileTypeSymbolicLink = 0xA000;
}

/// <summary>我们会用到的 OpenSSH 扩展名。</summary>
public static class SftpExtensionNames
{
    /// <summary>原子重命名（覆盖目标）。</summary>
    public const string PosixRename = "posix-rename@openssh.com";

    /// <summary>建硬链接。</summary>
    public const string HardLink = "hardlink@openssh.com";

    /// <summary>强制落盘。</summary>
    public const string Fsync = "fsync@openssh.com";

    /// <summary>文件系统用量。</summary>
    public const string StatVfs = "statvfs@openssh.com";

    /// <summary>服务端宣告的报文与读写长度上限。</summary>
    public const string Limits = "limits@openssh.com";

    /// <summary>服务端内复制，不经过网络。</summary>
    public const string CopyData = "copy-data";

    /// <summary>取指定用户的家目录。</summary>
    public const string HomeDirectory = "home-directory";

    /// <summary>展开 <c>~</c>。</summary>
    public const string ExpandPath = "expand-path@openssh.com";
}
