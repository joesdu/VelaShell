using VelaShell.Core.DirectorySync;

namespace VelaShell.Core.Tests.DirectorySync;

/// <summary>摘要参与比较时的口径:内容说了算,算不出来就回退到大小与修改时间。</summary>
[TestClass]
public sealed class ChecksumComparisonTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private const string H1 = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string H2 = "2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly SyncOptions WithChecksum = new()
    {
        Criteria = SyncCriteria.Checksum | SyncCriteria.Time | SyncCriteria.Size,
        IgnoreCase = false,
    };

    [TestMethod]
    public void SameHash_IsSameEvenWhenTimesDiffer()
    {
        // git checkout / 解压之后时间全变了,内容没变:不该传。
        Assert.AreEqual(SyncComparisonState.Same,
            DirectoryComparer.CompareFiles(File(10, T0.AddHours(3), H1), File(10, T0, H1.ToUpperInvariant()), WithChecksum));
    }

    [TestMethod]
    public void DifferentHash_UsesTimeForDirection_AndIsDiffersWhenTimesMatch()
    {
        Assert.AreEqual(SyncComparisonState.LocalNewer,
            DirectoryComparer.CompareFiles(File(10, T0.AddMinutes(5), H1), File(10, T0, H2), WithChecksum));
        Assert.AreEqual(SyncComparisonState.RemoteNewer,
            DirectoryComparer.CompareFiles(File(10, T0, H1), File(10, T0.AddMinutes(5), H2), WithChecksum));
        // 大小与时间都一样、内容却不同:只有摘要看得出来。
        Assert.AreEqual(SyncComparisonState.Differs,
            DirectoryComparer.CompareFiles(File(10, T0, H1), File(10, T0, H2), WithChecksum));
    }

    [TestMethod]
    public void MissingHashOnEitherSide_FallsBackToTimeAndSize()
    {
        Assert.AreEqual(SyncComparisonState.LocalNewer,
            DirectoryComparer.CompareFiles(File(10, T0.AddMinutes(5), H1), File(10, T0, null), WithChecksum));
        Assert.AreEqual(SyncComparisonState.Same,
            DirectoryComparer.CompareFiles(File(10, T0, null), File(10, T0, null), WithChecksum));
    }

    [TestMethod]
    public void ChecksumOnlyCriterion_StillFallsBackToBothTimeAndSize()
    {
        SyncOptions checksumOnly = WithChecksum with { Criteria = SyncCriteria.Checksum };

        Assert.AreEqual(SyncComparisonState.Differs,
            DirectoryComparer.CompareFiles(File(10, T0, null), File(11, T0, null), checksumOnly), "回退不能退成什么都不比");
        Assert.AreEqual(SyncComparisonState.RemoteNewer,
            DirectoryComparer.CompareFiles(File(10, T0, null), File(10, T0.AddHours(1), null), checksumOnly));
    }

    [TestMethod]
    public void OneWaySync_DoesNotTransferIdenticalContent_ButTransfersSameSizeSameTimeChanges()
    {
        SyncOptions options = WithChecksum with { Direction = SyncDirection.ToRemote };
        SyncComparison[] comparisons =
        [
            new("touched.txt", File(10, T0.AddHours(1), H1, "touched.txt"), File(10, T0, H1, "touched.txt"), SyncComparisonState.Same),
            new("edited.txt", File(10, T0, H1, "edited.txt"), File(10, T0, H2, "edited.txt"), SyncComparisonState.Differs),
        ];

        Assert.AreSequenceEqual(
            ["edited.txt"],
            [.. SyncPlanner.Plan(comparisons, options).Select(a => a.RelativePath)]);
    }

    [TestMethod]
    public void TimestampsMode_NeverStampsFilesWhoseContentDiffers()
    {
        SyncOptions options = WithChecksum with { Direction = SyncDirection.ToRemote, Mode = SyncMode.Timestamps };
        SyncComparison[] comparisons =
        [
            new("same-content.txt", File(10, T0.AddHours(1), H1, "same-content.txt"), File(10, T0, H1, "same-content.txt"), SyncComparisonState.Same),
            new("changed.txt", File(10, T0.AddHours(1), H1, "changed.txt"), File(10, T0, H2, "changed.txt"), SyncComparisonState.LocalNewer),
        ];

        IReadOnlyList<SyncAction> plan = SyncPlanner.Plan(comparisons, options);

        Assert.HasCount(1, plan);
        Assert.AreEqual("same-content.txt", plan[0].RelativePath);
        Assert.AreEqual(SyncActionKind.SetRemoteTime, plan[0].Kind);
    }

    private static SyncItem File(long size, DateTime utc, string? sha256, string path = "a.txt") =>
        new(path, "/" + path, false, size, utc) { Sha256 = sha256 };
}
