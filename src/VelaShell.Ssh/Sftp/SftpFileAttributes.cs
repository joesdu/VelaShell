// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §5  ATTRS
//   行为规格:                        velashell-docs/zh/ssh/spec/06-sftp.md §4.2

using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Sftp;

/// <summary>一个文件或目录的属性。</summary>
/// <remarks>
/// <para><b>三个坑，都在 draft-02 §5 里：</b></para>
/// <list type="number">
///   <item><c>uid</c> 与 <c>gid</c> 共用<b>一个</b>标志位，<c>atime</c> 与
///   <c>mtime</c> 也共用一个。只想改 mtime 时必须一并给 atime ——
///   <c>SftpFileSystem.SetLastWriteTimeAsync</c> 会先 stat 回来再写回。</item>
///   <item>时间是 <b>32 位 Unix 秒</b>，2038 年溢出。v3 没有解法。
///   按**有符号**读，与 OpenSSH 一致。</item>
///   <item><b>v3 没有单独的文件类型字段</b> —— 是文件还是目录，
///   只能从 <see cref="Permissions"/> 的高位（<c>S_IFMT</c>）取。</item>
/// </list>
/// </remarks>
public readonly record struct SftpFileAttributes
{
    /// <summary>哪些字段是有效的。</summary>
    public SftpAttributeFields Flags { get; init; }

    /// <summary>文件长度（字节）。<see cref="SftpAttributeFields.Size"/> 置位时有效。</summary>
    public ulong Size { get; init; }

    /// <summary>属主。<see cref="SftpAttributeFields.UidGid"/> 置位时有效。</summary>
    public uint UserId { get; init; }

    /// <summary>属组。<see cref="SftpAttributeFields.UidGid"/> 置位时有效。</summary>
    public uint GroupId { get; init; }

    /// <summary>权限位，<b>高位还带着文件类型</b>。</summary>
    public uint Permissions { get; init; }

    /// <summary>最后访问时间（Unix 秒，有符号）。</summary>
    public int AccessTime { get; init; }

    /// <summary>最后修改时间（Unix 秒，有符号）。</summary>
    public int ModifyTime { get; init; }

    /// <summary>厂商扩展属性。</summary>
    public IReadOnlyList<(string Type, string Data)> Extended { get; init; }

    /// <summary>什么都没带的空属性。</summary>
    public static SftpFileAttributes Empty => new() { Extended = [] };

    /// <summary>只带权限的属性（创建文件/目录时用）。</summary>
    public static SftpFileAttributes WithPermissions(uint permissions) => new()
    {
        Flags = SftpAttributeFields.Permissions,
        Permissions = permissions,
        Extended = [],
    };

    /// <summary>只带长度的属性（截断用）。</summary>
    public static SftpFileAttributes WithSize(ulong size) => new()
    {
        Flags = SftpAttributeFields.Size,
        Size = size,
        Extended = [],
    };

    /// <summary>带访问与修改时间的属性。</summary>
    /// <remarks>
    /// 两个时间**必须一起给** —— 它们共用一个标志位，
    /// 只给一个的话另一个会被服务端当成 0（1970 年）。
    /// </remarks>
    public static SftpFileAttributes WithTimes(DateTimeOffset accessTime, DateTimeOffset modifyTime) => new()
    {
        Flags = SftpAttributeFields.Times,
        AccessTime = (int)accessTime.ToUnixTimeSeconds(),
        ModifyTime = (int)modifyTime.ToUnixTimeSeconds(),
        Extended = [],
    };

    /// <summary>长度是否有效。</summary>
    public bool HasSize => (Flags & SftpAttributeFields.Size) != 0;

    /// <summary>属主属组是否有效。</summary>
    public bool HasUidGid => (Flags & SftpAttributeFields.UidGid) != 0;

    /// <summary>权限是否有效。</summary>
    public bool HasPermissions => (Flags & SftpAttributeFields.Permissions) != 0;

    /// <summary>时间是否有效。</summary>
    public bool HasTimes => (Flags & SftpAttributeFields.Times) != 0;

    /// <summary>这是不是一个目录。</summary>
    /// <remarks>权限字段无效时恒为 <see langword="false"/> —— 我们不知道，不能猜。</remarks>
    public bool IsDirectory =>
        HasPermissions && (Permissions & SftpProtocol.FileTypeMask) == SftpProtocol.FileTypeDirectory;

    /// <summary>这是不是一个符号链接。</summary>
    public bool IsSymbolicLink =>
        HasPermissions && (Permissions & SftpProtocol.FileTypeMask) == SftpProtocol.FileTypeSymbolicLink;

    /// <summary>这是不是一个普通文件。</summary>
    public bool IsRegularFile =>
        HasPermissions && (Permissions & SftpProtocol.FileTypeMask) == SftpProtocol.FileTypeRegular;

    /// <summary>去掉文件类型之后的权限位（<c>0644</c> 那部分）。</summary>
    public uint PermissionBits => Permissions & 0xFFF;

    /// <summary>最后修改时间。</summary>
    public DateTimeOffset LastWriteTime => DateTimeOffset.FromUnixTimeSeconds(ModifyTime);

    /// <summary>最后访问时间。</summary>
    public DateTimeOffset LastAccessTime => DateTimeOffset.FromUnixTimeSeconds(AccessTime);

    /// <summary>写进报文。</summary>
    internal void Write(ref SshDataWriter writer)
    {
        writer.WriteUInt32((uint)Flags);

        if (HasSize)
        {
            writer.WriteUInt64(Size);
        }

        if (HasUidGid)
        {
            writer.WriteUInt32(UserId);
            writer.WriteUInt32(GroupId);
        }

        if (HasPermissions)
        {
            writer.WriteUInt32(Permissions);
        }

        if (HasTimes)
        {
            writer.WriteUInt32((uint)AccessTime);
            writer.WriteUInt32((uint)ModifyTime);
        }

        if ((Flags & SftpAttributeFields.Extended) != 0)
        {
            IReadOnlyList<(string Type, string Data)> extended = Extended ?? [];
            writer.WriteUInt32((uint)extended.Count);
            foreach ((string type, string data) in extended)
            {
                writer.WriteUtf8String(type);
                writer.WriteUtf8String(data);
            }
        }
    }

    /// <summary>从报文里读出来。</summary>
    internal static SftpFileAttributes Read(ref SshDataReader reader)
    {
        SftpAttributeFields flags = (SftpAttributeFields)reader.ReadUInt32();

        ulong size = 0;
        uint uid = 0;
        uint gid = 0;
        uint permissions = 0;
        int accessTime = 0;
        int modifyTime = 0;
        List<(string, string)> extended = [];

        if ((flags & SftpAttributeFields.Size) != 0)
        {
            size = reader.ReadUInt64();
        }

        if ((flags & SftpAttributeFields.UidGid) != 0)
        {
            uid = reader.ReadUInt32();
            gid = reader.ReadUInt32();
        }

        if ((flags & SftpAttributeFields.Permissions) != 0)
        {
            permissions = reader.ReadUInt32();
        }

        if ((flags & SftpAttributeFields.Times) != 0)
        {
            // 按**有符号**读：服务端（OpenSSH）就是这么发的。
            // 按无符号读能撑到 2106 年，但那会与对端对不上账。
            accessTime = (int)reader.ReadUInt32();
            modifyTime = (int)reader.ReadUInt32();
        }

        if ((flags & SftpAttributeFields.Extended) != 0)
        {
            uint count = reader.ReadUInt32();

            // 上限防一个畸形报文让我们空转。
            for (uint i = 0; i < count && i < 1024; i++)
            {
                extended.Add((
                    reader.ReadUtf8String(SftpProtocol.MaxPathLength),
                    reader.ReadUtf8String(SftpProtocol.MaxPathLength)));
            }
        }

        return new SftpFileAttributes
        {
            Flags = flags,
            Size = size,
            UserId = uid,
            GroupId = gid,
            Permissions = permissions,
            AccessTime = accessTime,
            ModifyTime = modifyTime,
            Extended = extended,
        };
    }
}
