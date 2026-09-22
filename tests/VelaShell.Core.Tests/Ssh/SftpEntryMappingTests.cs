using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// SFTP 属性 → <see cref="SftpEntry" /> 映射回归:修改时间必须换算为本地时区。
/// 2026-07-23 事故:曾用 <c>DateTimeOffset.DateTime</c> 剥掉偏移,文件浏览器显示 +0 时区
/// 时间(与系统 date -R 的 +8 不符),下载保留时间戳还会再错一次时差。
/// </summary>
[TestClass]
[TestCategory("SftpMapping")]
public class SftpEntryMappingTests
{
    /// <summary>按 SFTP 的口径造一份属性:mtime 是 Unix 纪元秒。</summary>
    private static SftpFileAttributes Attributes(
        DateTimeOffset modified, uint permissions, SftpFileTypeBits typeBits, ulong size = 0) =>
        new()
        {
            Flags = SftpAttributeFields.Size | SftpAttributeFields.UidGid
                    | SftpAttributeFields.Permissions | SftpAttributeFields.Times,
            Size = size,
            UserId = 1000,
            GroupId = 1000,
            Permissions = permissions | (uint)typeBits,
            AccessTime = (int)modified.ToUnixTimeSeconds(),
            ModifyTime = (int)modified.ToUnixTimeSeconds(),
            Extended = [],
        };

    /// <summary>POSIX 的文件类型位,写在权限字的高位。</summary>
    private enum SftpFileTypeBits : uint
    {
        RegularFile = 0x8000,
        Directory = 0x4000,
        SymbolicLink = 0xA000,
    }

    [TestMethod]
    public void MapEntry_ConvertsLastWriteTimeToLocalKindAndInstant()
    {
        // SFTP mtime 本质是 Unix 纪元秒(UTC 瞬间);以一个确定的 UTC 时刻构造。
        var utcInstant = new DateTimeOffset(2026, 7, 23, 1, 14, 55, TimeSpan.Zero);

        SftpEntry entry = VelaSftpClientWrapper.MapEntry(
            "/home/rocktech/a.txt",
            Attributes(utcInstant, 0b110_000_000, SftpFileTypeBits.RegularFile, size: 42),
            isSymbolicLink: false, linkTarget: null);

        Assert.AreEqual(DateTimeKind.Local, entry.LastWriteTime.Kind, "映射结果必须是本地时区时间(Kind=Local)。");
        Assert.AreEqual(utcInstant.LocalDateTime, entry.LastWriteTime, "墙钟数应为该 UTC 瞬间换算到本机时区的值。");
        Assert.AreEqual(utcInstant.UtcDateTime, entry.LastWriteTime.ToUniversalTime(), "换算不得改变时间瞬间本身。");
        Assert.AreEqual(42L, entry.Length);
        Assert.IsTrue(entry.OwnerCanRead);
        Assert.IsTrue(entry.OwnerCanWrite);
        Assert.IsFalse(entry.OwnerCanExecute);
    }

    [TestMethod]
    public void MapEntry_SymbolicLink_SetsLinkFlagAndIsNotADirectory()
    {
        SftpEntry entry = VelaSftpClientWrapper.MapEntry(
            "/srv/app/current",
            Attributes(DateTimeOffset.UnixEpoch, 0b111_101_101, SftpFileTypeBits.SymbolicLink),
            isSymbolicLink: true, linkTarget: "/srv/app/releases/42");

        Assert.IsTrue(entry.IsSymbolicLink);
        Assert.IsFalse(entry.IsDirectory, "lstat 得来的链接条目在补上目标信息之前不能冒充目录。");
        Assert.AreEqual("/srv/app/releases/42", entry.LinkTarget);
    }

    /// <summary>
    /// 链接**跟随之后**指向目录:<c>IsDirectory</c> 要为真,<c>IsSymbolicLink</c> 同时为真。
    /// </summary>
    /// <remarks>
    /// 两个标志必须能同时成立 —— 文件浏览器靠前者决定「能不能双击进去」,
    /// 而递归删除靠后者决定「不要沿着它往下删」。把它们混成一个,
    /// 删一个指向目录的链接就会把目标目录里的东西逐个删光。
    /// </remarks>
    [TestMethod]
    public void MapEntry_ResolvedLinkToDirectory_KeepsBothFlags()
    {
        SftpEntry entry = VelaSftpClientWrapper.MapEntry(
            "/srv/app/current",
            Attributes(DateTimeOffset.UnixEpoch, 0b111_101_101, SftpFileTypeBits.Directory),
            isSymbolicLink: true, linkTarget: "/srv/app/releases/42");

        Assert.IsTrue(entry.IsSymbolicLink);
        Assert.IsTrue(entry.IsDirectory);
    }

    /// <summary>九个权限位逐个对位,错一位就是显示成别人的权限。</summary>
    [TestMethod]
    public void MapEntry_MapsAllNinePermissionBits()
    {
        SftpEntry entry = VelaSftpClientWrapper.MapEntry(
            "/tmp/x",
            Attributes(DateTimeOffset.UnixEpoch, 0b101_011_110, SftpFileTypeBits.RegularFile),
            isSymbolicLink: false, linkTarget: null);

        Assert.IsTrue(entry.OwnerCanRead);
        Assert.IsFalse(entry.OwnerCanWrite);
        Assert.IsTrue(entry.OwnerCanExecute);

        Assert.IsFalse(entry.GroupCanRead);
        Assert.IsTrue(entry.GroupCanWrite);
        Assert.IsTrue(entry.GroupCanExecute);

        Assert.IsTrue(entry.OthersCanRead);
        Assert.IsTrue(entry.OthersCanWrite);
        Assert.IsFalse(entry.OthersCanExecute);
    }
}
