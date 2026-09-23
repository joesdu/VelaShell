using VelaShell.Core.DirectorySync;
using static VelaShell.Core.Tests.DirectorySync.DirectoryComparerTests;

namespace VelaShell.Core.Tests.DirectorySync;

[TestClass]
public class SyncPlannerTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>本地、远端各一份,覆盖所有比较结论。</summary>
    private static readonly SyncItem[] Local =
    [
        File("same.txt", 1, T0),
        File("local-newer.txt", 1, T0.AddHours(1)),
        File("remote-newer.txt", 1, T0),
        File("size.txt", 1, T0),
        File("only-local.txt", 1, T0),
        Dir("new-dir"),
        File("new-dir/inner.txt", 1, T0),
    ];

    private static readonly SyncItem[] Remote =
    [
        File("same.txt", 1, T0),
        File("local-newer.txt", 1, T0),
        File("remote-newer.txt", 1, T0.AddHours(1)),
        File("size.txt", 2, T0),
        File("only-remote.txt", 1, T0),
        Dir("old-dir"),
        File("old-dir/a.txt", 1, T0),
        Dir("old-dir/sub"),
        File("old-dir/sub/b.txt", 1, T0),
    ];

    [TestMethod]
    public void ToRemote_Synchronize_UploadsNewerAndMissing_LeavesNewerTargetsAlone()
    {
        Dictionary<string, SyncActionKind> plan = Plan(new() { Direction = SyncDirection.ToRemote, IgnoreCase = false });

        Assert.AreEqual(SyncActionKind.Upload, plan["local-newer.txt"]);
        Assert.AreEqual(SyncActionKind.Upload, plan["size.txt"]);
        Assert.AreEqual(SyncActionKind.Upload, plan["only-local.txt"]);
        Assert.AreEqual(SyncActionKind.CreateRemoteDirectory, plan["new-dir"]);
        Assert.AreEqual(SyncActionKind.Upload, plan["new-dir/inner.txt"]);
        Assert.IsFalse(plan.ContainsKey("remote-newer.txt"), "同步模式不拿旧文件覆盖较新的目标");
        Assert.IsFalse(plan.ContainsKey("only-remote.txt"), "没勾删除就不删");
        Assert.IsFalse(plan.ContainsKey("same.txt"));
    }

    [TestMethod]
    public void ToRemote_Mirror_OverwritesNewerTargets_AndDeletesOnlyTopMostExtraneousDirectory()
    {
        Dictionary<string, SyncActionKind> plan = Plan(new()
        {
            Direction = SyncDirection.ToRemote,
            Mode = SyncMode.Mirror,
            DeleteExtraneous = true,
            IgnoreCase = false,
        });

        Assert.AreEqual(SyncActionKind.Upload, plan["remote-newer.txt"]);
        Assert.AreEqual(SyncActionKind.DeleteRemote, plan["only-remote.txt"]);
        Assert.AreEqual(SyncActionKind.DeleteRemote, plan["old-dir"]);
        Assert.IsFalse(plan.ContainsKey("old-dir/a.txt"), "目录整个删,里面的条目不再单列");
        Assert.IsFalse(plan.ContainsKey("old-dir/sub"));
        Assert.IsFalse(plan.ContainsKey("old-dir/sub/b.txt"));
    }

    [TestMethod]
    public void ToLocal_IsTheMirrorImage()
    {
        Dictionary<string, SyncActionKind> plan = Plan(new()
        {
            Direction = SyncDirection.ToLocal,
            DeleteExtraneous = true,
            IgnoreCase = false,
        });

        Assert.AreEqual(SyncActionKind.Download, plan["remote-newer.txt"]);
        Assert.AreEqual(SyncActionKind.Download, plan["only-remote.txt"]);
        Assert.AreEqual(SyncActionKind.CreateLocalDirectory, plan["old-dir"]);
        Assert.AreEqual(SyncActionKind.Download, plan["old-dir/sub/b.txt"]);
        Assert.AreEqual(SyncActionKind.DeleteLocal, plan["only-local.txt"]);
        Assert.AreEqual(SyncActionKind.DeleteLocal, plan["new-dir"]);
        Assert.IsFalse(plan.ContainsKey("new-dir/inner.txt"));
        Assert.IsFalse(plan.ContainsKey("local-newer.txt"));
    }

    [TestMethod]
    public void Both_FillsGapsBothWays_NeverDeletes_AndLeavesSizeOnlyDifferencesAlone()
    {
        Dictionary<string, SyncActionKind> plan = Plan(new()
        {
            Direction = SyncDirection.Both,
            Mode = SyncMode.Mirror,          // 双向下被忽略
            DeleteExtraneous = true,         // 双向下被忽略
            IgnoreCase = false,
        });

        Assert.AreEqual(SyncActionKind.Upload, plan["local-newer.txt"]);
        Assert.AreEqual(SyncActionKind.Download, plan["remote-newer.txt"]);
        Assert.AreEqual(SyncActionKind.Upload, plan["only-local.txt"]);
        Assert.AreEqual(SyncActionKind.Download, plan["only-remote.txt"]);
        Assert.IsFalse(plan.ContainsKey("size.txt"), "时间相同大小不同:没有源,谁覆盖谁都可能毁掉改动");
        Assert.DoesNotContain(k => k is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote, plan.Values);
    }

    [TestMethod]
    public void ExistingOnly_SkipsEverythingThatWouldBeCreated()
    {
        Dictionary<string, SyncActionKind> plan = Plan(new()
        {
            Direction = SyncDirection.ToRemote,
            ExistingOnly = true,
            IgnoreCase = false,
        });

        Assert.IsFalse(plan.ContainsKey("only-local.txt"));
        Assert.IsFalse(plan.ContainsKey("new-dir"));
        Assert.IsFalse(plan.ContainsKey("new-dir/inner.txt"));
        Assert.AreEqual(SyncActionKind.Upload, plan["local-newer.txt"]);
    }

    [TestMethod]
    public void Timestamps_OnlyTouchesSameSizeFilesWhoseTimesDiffer()
    {
        Dictionary<string, SyncActionKind> plan = Plan(new()
        {
            Direction = SyncDirection.ToRemote,
            Mode = SyncMode.Timestamps,
            Criteria = SyncCriteria.Size,     // 时间戳模式与依据无关
            DeleteExtraneous = true,          // 时间戳模式不删
            IgnoreCase = false,
        });

        Assert.AreSequenceEqual(["local-newer.txt", "remote-newer.txt"], [.. plan.Keys], SequenceOrder.InAnyOrder);
        Assert.IsTrue(plan.Values.All(k => k == SyncActionKind.SetRemoteTime));
    }

    [TestMethod]
    public void Conflicts_ProduceNoActions()
    {
        IReadOnlyList<SyncComparison> comparisons = DirectoryComparer.Compare(
            [File("x", 1, T0)],
            [Dir("x"), File("x/a", 1, T0)],
            new() { IgnoreCase = false });

        IReadOnlyList<SyncAction> plan = SyncPlanner.Plan(comparisons, new()
        {
            Direction = SyncDirection.ToRemote,
            Mode = SyncMode.Mirror,
            DeleteExtraneous = true,
            IgnoreCase = false,
        });

        Assert.IsEmpty(plan);
    }

    private static Dictionary<string, SyncActionKind> Plan(SyncOptions options) =>
        SyncPlanner.Plan(DirectoryComparer.Compare(Local, Remote, options), options)
            .ToDictionary(a => a.RelativePath, a => a.Kind);
}
