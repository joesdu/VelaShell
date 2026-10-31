using VelaShell.Core.DirectorySync;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.DirectorySync;

[TestClass]
public class DirectoryComparerTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 10, 30, 15, DateTimeKind.Utc);

    [TestMethod]
    public void Compare_ClassifiesEveryPairing()
    {
        SyncItem[] local =
        [
            File("same.txt", 10, T0),
            File("local-newer.txt", 10, T0.AddMinutes(5)),
            File("remote-newer.txt", 10, T0),
            File("size.txt", 10, T0),
            File("only-local.txt", 1, T0),
            Dir("dir"),
        ];
        SyncItem[] remote =
        [
            File("same.txt", 10, T0),
            File("local-newer.txt", 10, T0),
            File("remote-newer.txt", 10, T0.AddHours(1)),
            File("size.txt", 11, T0),
            File("only-remote.txt", 1, T0),
            Dir("dir"),
        ];

        var states = DirectoryComparer
            .Compare(local, remote, Options())
            .ToDictionary(c => c.RelativePath, c => c.State);

        Assert.AreEqual(SyncComparisonState.Same, states["same.txt"]);
        Assert.AreEqual(SyncComparisonState.LocalNewer, states["local-newer.txt"]);
        Assert.AreEqual(SyncComparisonState.RemoteNewer, states["remote-newer.txt"]);
        Assert.AreEqual(SyncComparisonState.Differs, states["size.txt"]);
        Assert.AreEqual(SyncComparisonState.LocalOnly, states["only-local.txt"]);
        Assert.AreEqual(SyncComparisonState.RemoteOnly, states["only-remote.txt"]);
        Assert.AreEqual(SyncComparisonState.Same, states["dir"]);
    }

    [TestMethod]
    public void Criteria_ControlWhatCountsAsDifferent()
    {
        SyncItem l = File("a.txt", 10, T0.AddHours(1));
        SyncItem r = File("a.txt", 20, T0);

        Assert.AreEqual(SyncComparisonState.LocalNewer, DirectoryComparer.CompareFiles(l, r, Options()));
        Assert.AreEqual(SyncComparisonState.Differs,
            DirectoryComparer.CompareFiles(l, r, Options() with { Criteria = SyncCriteria.Size }));
        Assert.AreEqual(SyncComparisonState.Same,
            DirectoryComparer.CompareFiles(l, r, Options() with { Criteria = SyncCriteria.None }));
    }

    [TestMethod]
    public void MinutePrecisionRemote_DoesNotFlagEveryFileAsDifferent()
    {
        // FTP LIST 只给到分钟:本地 10:30:15 与远端 10:30(:00)是同一个时间。
        SyncItem l = File("a.txt", 10, T0);
        SyncItem r = File("a.txt", 10, T0.AddSeconds(-15)) with { Precision = SyncTimePrecision.Minute };
        Assert.AreEqual(SyncComparisonState.Same, DirectoryComparer.CompareFiles(l, r, Options()));

        // 同一精度下,整整晚一分钟就是较新。
        SyncItem later = r with { LastWriteTimeUtc = r.LastWriteTimeUtc.AddMinutes(1) };
        Assert.AreEqual(SyncComparisonState.RemoteNewer, DirectoryComparer.CompareFiles(l, later, Options()));
    }

    /// <summary>
    /// 服务器时区的换算:LIST(不带时区)按偏移换成 UTC,MLSD(已是 UTC)原样;偏移为 null 时按本机时间读(旧行为)。
    /// </summary>
    [TestMethod]
    public void ToUtc_WithServerClockOffset_ShiftsOnlyUnspecifiedTimes()
    {
        var wall = new DateTime(2026, 9, 1, 18, 30, 0, DateTimeKind.Unspecified);
        var utc = new DateTime(2026, 9, 1, 10, 30, 0, DateTimeKind.Utc);

        Assert.AreEqual(utc, SyncTime.ToUtc(wall, TimeSpan.FromHours(8)));
        Assert.AreEqual(DateTimeKind.Utc, SyncTime.ToUtc(wall, TimeSpan.FromHours(8)).Kind);
        Assert.AreEqual(utc.AddHours(13), SyncTime.ToUtc(wall, TimeSpan.FromHours(-5)));
        Assert.AreEqual(utc, SyncTime.ToUtc(utc, TimeSpan.FromHours(8)), "MLSD 的 UTC 不能再挪");
        Assert.AreEqual(SyncTime.ToUtc(wall), SyncTime.ToUtc(wall, null), "没配偏移就是旧行为");
        Assert.AreEqual(TimeSpan.FromHours(8), SyncTime.CalendarOffsetFor(wall, TimeSpan.FromHours(8)));
        Assert.IsNull(SyncTime.CalendarOffsetFor(utc, TimeSpan.FromHours(8)));
    }

    /// <summary>
    /// FTP 给不出时间的条目(MLSD 不给目录的 modify、认不出的 LIST 行)记成 MinValue:服务器在东八区时按偏移一减就越过下界,
    /// 「比较目录」整个抛 ArgumentOutOfRangeException(复查 file-panes F6)。不挪、夹住,比较时按「时间未知」看大小。
    /// </summary>
    [TestMethod]
    public void ToUtc_EntryWithoutATime_EastOfUtc_DoesNotThrow_AndComparesAsUnknownTime()
    {
        var east = TimeSpan.FromHours(8);

        Assert.AreEqual(DateTime.MinValue, SyncTime.ToUtc(DateTime.MinValue, east));
        Assert.AreEqual(DateTimeKind.Utc, SyncTime.ToUtc(DateTime.MinValue, east).Kind);
        Assert.AreEqual(DateTime.MinValue, SyncTime.ToUtc(DateTime.MinValue.AddHours(3), east), "挪到头的夹在下界");
        Assert.AreEqual(DateTime.MaxValue, SyncTime.ToUtc(DateTime.MaxValue, TimeSpan.FromHours(-5)), "西边同理夹在上界");

        // 与文件栏「比较目录」同样的拼法(SftpDocumentViewModel.CompareDirectoriesAsync)。
        SyncItem remote = File("conf", 10, SyncTime.ToUtc(DateTime.MinValue, east)) with
        {
            Precision = SyncTime.InferPrecision(DateTime.MinValue),
            CalendarOffset = SyncTime.CalendarOffsetFor(DateTime.MinValue, east),
        };
        Assert.AreEqual(SyncTimePrecision.Unknown, remote.Precision);
        Assert.AreEqual(SyncComparisonState.Same, DirectoryComparer.CompareFiles(File("conf", 10, T0), remote, Options()));
        Assert.AreEqual(SyncComparisonState.Differs, DirectoryComparer.CompareFiles(File("conf", 11, T0), remote, Options()));
    }

    /// <summary>只有 FTP 才有服务器时区;其余协议即便带着一块 FTP 设置也不认。</summary>
    [TestMethod]
    public void ServerClockOffset_OnlyAppliesToFtpProfiles()
    {
        var ftp = new SessionProfile
        {
            ConnectionType = ConnectionType.FTP,
            Ftp = new() { ServerUtcOffsetMinutes = -300 },
        };
        Assert.AreEqual(TimeSpan.FromHours(-5), SyncTime.ServerClockOffset(ftp));
        ftp.Ftp.ServerUtcOffsetMinutes = null;
        Assert.IsNull(SyncTime.ServerClockOffset(ftp));
        var sftp = new SessionProfile
        {
            ConnectionType = ConnectionType.SFTP,
            Ftp = new() { ServerUtcOffsetMinutes = 480 },
        };
        Assert.IsNull(SyncTime.ServerClockOffset(sftp));
        Assert.IsNull(SyncTime.ServerClockOffset(null));
    }

    /// <summary>
    /// 半年前的文件 LIST 只给服务器当地的日期。服务器在 UTC+14、文件在服务器的 12 月 2 日 23:00 改过、本机副本时间相同:
    /// 两边都要放进服务器那本日历里截断才是同一天;按本机日历截(几乎任何本机时区下)会落在 12 月 1 日与 2 日,误判本地较新。
    /// </summary>
    [TestMethod]
    public void DayPrecision_IsTruncatedInTheServersCalendar()
    {
        var offset = TimeSpan.FromHours(14);
        var listed = new DateTime(2025, 12, 2, 0, 0, 0, DateTimeKind.Unspecified);
        SyncItem remote = File("old.tar", 10, SyncTime.ToUtc(listed, offset)) with
        {
            Precision = SyncTimePrecision.Day,
            CalendarOffset = SyncTime.CalendarOffsetFor(listed, offset),
        };
        SyncItem local = File("old.tar", 10, new DateTime(2025, 12, 2, 9, 0, 0, DateTimeKind.Utc));

        Assert.AreEqual(SyncComparisonState.Same, DirectoryComparer.CompareFiles(local, remote, Options()));
        // 反证:服务器日历上晚了一天,就该判本地较新。
        SyncItem nextDay = local with { LastWriteTimeUtc = local.LastWriteTimeUtc.AddDays(1) };
        Assert.AreEqual(SyncComparisonState.LocalNewer, DirectoryComparer.CompareFiles(nextDay, remote, Options()));
    }

    [TestMethod]
    public void OneSecondTolerance_AbsorbsFatGranularity_ButTwoSecondsDoNot()
    {
        SyncItem l = File("a.txt", 10, T0);
        Assert.AreEqual(SyncComparisonState.Same,
            DirectoryComparer.CompareFiles(l, File("a.txt", 10, T0.AddSeconds(1)), Options()));
        Assert.AreEqual(SyncComparisonState.RemoteNewer,
            DirectoryComparer.CompareFiles(l, File("a.txt", 10, T0.AddSeconds(2)), Options()));
        Assert.AreEqual(SyncComparisonState.Same,
            DirectoryComparer.CompareFiles(l, File("a.txt", 10, T0.AddMilliseconds(900)), Options()), "亚秒级差异不算");
    }

    [TestMethod]
    public void UnknownRemoteTime_FallsBackToSizeOnly()
    {
        SyncItem l = File("a.txt", 10, T0);
        SyncItem r = File("a.txt", 10, DateTime.MinValue) with { Precision = SyncTimePrecision.Unknown };
        Assert.AreEqual(SyncComparisonState.Same, DirectoryComparer.CompareFiles(l, r, Options()));
    }

    [TestMethod]
    public void FileVersusDirectory_IsAConflict_AndNothingBelowItIsCompared()
    {
        SyncItem[] local = [File("x", 3, T0)];
        SyncItem[] remote = [Dir("x"), File("x/a.txt", 1, T0), File("x/b/c.txt", 1, T0)];

        IReadOnlyList<SyncComparison> result = DirectoryComparer.Compare(local, remote, Options());

        Assert.HasCount(1, result, "冲突目录下的子项若被列成「只有远端有」,勾上删除就会去删整棵树");
        Assert.AreEqual(SyncComparisonState.Conflict, result[0].State);
    }

    [TestMethod]
    public void CaseInsensitiveLocal_TwoRemoteNamesDifferingOnlyInCase_AreAConflict()
    {
        SyncItem[] local = [File("readme.md", 1, T0)];
        SyncItem[] remote = [File("README.md", 1, T0), File("readme.md", 2, T0)];

        IReadOnlyList<SyncComparison> ignoreCase = DirectoryComparer.Compare(local, remote, Options() with { IgnoreCase = true });
        Assert.HasCount(1, ignoreCase);
        Assert.AreEqual(SyncComparisonState.Conflict, ignoreCase[0].State);

        IReadOnlyList<SyncComparison> caseSensitive = DirectoryComparer.Compare(local, remote, Options() with { IgnoreCase = false });
        Assert.HasCount(2, caseSensitive);
        Assert.AreEqual(SyncComparisonState.RemoteOnly, caseSensitive.Single(c => c.RelativePath == "README.md").State);
    }

    [TestMethod]
    public void IgnoreCase_PairsNamesThatDifferOnlyInCase()
    {
        IReadOnlyList<SyncComparison> result = DirectoryComparer.Compare(
            [File("Photo.JPG", 5, T0)],
            [File("photo.jpg", 5, T0)],
            Options() with { IgnoreCase = true });

        Assert.HasCount(1, result);
        Assert.AreEqual(SyncComparisonState.Same, result[0].State);
    }

    [TestMethod]
    public void Results_ListParentsBeforeChildren()
    {
        IReadOnlyList<SyncComparison> result = DirectoryComparer.Compare(
            [File("a/b/c.txt", 1, T0), Dir("a/b"), Dir("a"), File("a-b.txt", 1, T0)],
            [],
            Options());

        List<string> order = [.. result.Select(c => c.RelativePath)];
        Assert.IsLessThan(order.IndexOf("a/b"), order.IndexOf("a"));
        Assert.IsLessThan(order.IndexOf("a/b/c.txt"), order.IndexOf("a/b"));
    }

    internal static SyncOptions Options() => new() { IgnoreCase = false };

    internal static SyncItem File(string path, long size, DateTime utc) => new(path, "/" + path, false, size, utc);

    internal static SyncItem Dir(string path) => new(path, "/" + path, true, 0, T0);
}
