using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 开库时清掉上一次运行留下的段临时文件。
/// </summary>
/// <remarks>
/// 这一条不是洁癖,是修一个会丢数据、而且不会自愈的故障:刷盘落段是「先写 <c>*.SDBSEG.tmp</c>、
/// 写成了再改名」,进程死在这两步之间(调试器停止、崩溃、退出刷盘被掐断)就会把 tmp 留在盘上,
/// 此后每一次落到同一段号的刷盘都撞「文件已存在」而失败 —— 连同内存表里那批数据一起。
/// 真实现场:一个 <c>0000000000000270.SDBSEG.tmp</c> 留了一个多小时,退出时的最后一次刷盘
/// 就死在它身上。
/// </remarks>
[TestClass]
public sealed class SonnetDbSegmentTempSweepTests
{
    private string _root = null!;

    /// <summary>段文件实际所在的层级,照着真实库的目录结构摆。</summary>
    private string SegmentDirectory =>
        Path.Combine(_root, "segments", "v2", "00", "0000000000000000");

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vela-segsweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(SegmentDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 被映射的段文件在 Windows 上删不掉,留给系统清临时目录。
        }
    }

    [TestMethod]
    public void OpeningTheDatabaseRemovesAnOrphanedSegmentTempFile()
    {
        string orphan = Path.Combine(SegmentDirectory, "0000000000000270.SDBSEG.tmp");
        File.WriteAllBytes(orphan, new byte[64]);

        using SonnetDbEngine engine = new(_root);

        Assert.IsFalse(File.Exists(orphan),
            "上次运行留下的段临时文件必须清掉,否则之后每次刷到这个段号都会撞「文件已存在」。");
    }

    [TestMethod]
    public async Task TheSweptDatabaseStillReadsAndWrites()
    {
        File.WriteAllBytes(Path.Combine(SegmentDirectory, "0000000000000271.SDBSEG.tmp"), new byte[64]);

        using SonnetDbEngine engine = new(_root);
        var repo = new SonnetDbSessionRepository(engine, new AesSecretProtector(Path.Combine(_root, "secret.key")));
        await repo.SaveSessionAsync(new SessionProfile { Name = "web-01", Host = "10.0.0.1", Username = "root" });

        List<SessionProfile> sessions = await repo.GetAllSessionsAsync();

        Assert.HasCount(1, sessions);
        Assert.AreEqual("web-01", sessions[0].Name);
    }

    [TestMethod]
    public void OpeningTheDatabaseLeavesOtherTempFilesAlone()
    {
        // 清的是段的中间产物,不是"库目录下所有 .tmp"。别人的临时文件不归我们处置。
        string unrelated = Path.Combine(SegmentDirectory, "something-else.tmp");
        File.WriteAllBytes(unrelated, new byte[8]);

        using SonnetDbEngine engine = new(_root);

        Assert.IsTrue(File.Exists(unrelated), "只有 *.SDBSEG.tmp 才是段刷盘的残留。");
    }
}
