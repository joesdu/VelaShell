using Tmds.Ssh;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// Tmds 目录条目 → <see cref="SftpEntry" /> 映射回归:修改时间必须换算为本地时区。
/// 2026-07-23 事故:曾用 <c>DateTimeOffset.DateTime</c> 剥掉偏移,文件浏览器显示 +0 时区
/// 时间(与系统 date -R 的 +8 不符),下载保留时间戳还会再错一次时差。
/// </summary>
[TestClass]
[TestCategory("SftpMapping")]
public class TmdsSftpEntryMappingTests
{
    [TestMethod]
    public void MapEntry_ConvertsLastWriteTimeToLocalKindAndInstant()
    {
        // SFTP mtime 本质是 Unix 纪元秒(UTC 瞬间);以一个确定的 UTC 时刻构造。
        var utcInstant = new DateTimeOffset(2026, 7, 23, 1, 14, 55, TimeSpan.Zero);
        var attrs = new FileEntryAttributes
        {
            Length = 42,
            Uid = 1000,
            Gid = 1000,
            FileType = UnixFileType.RegularFile,
            Permissions = UnixFilePermissions.UserRead | UnixFilePermissions.UserWrite,
            LastAccessTime = utcInstant,
            LastWriteTime = utcInstant
        };

        SftpEntry entry = TmdsSftpClientWrapper.MapEntry("/home/rocktech/a.txt", attrs);

        Assert.AreEqual(DateTimeKind.Local, entry.LastWriteTime.Kind, "映射结果必须是本地时区时间(Kind=Local)。");
        Assert.AreEqual(utcInstant.LocalDateTime, entry.LastWriteTime, "墙钟数应为该 UTC 瞬间换算到本机时区的值。");
        Assert.AreEqual(utcInstant.UtcDateTime, entry.LastWriteTime.ToUniversalTime(), "换算不得改变时间瞬间本身。");
    }
}
