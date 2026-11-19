using SonnetDB.Engine;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 开库参数:生产路径保留 SonnetDB 的逐次落盘同步;测试用的 <see cref="SonnetDbEngine.NoFsyncEnvironmentVariable" /> 只关这两项。
/// </summary>
/// <remarks>
/// 整套测试经 tests/velashell.runsettings 设了 <c>VELASHELL_NO_DB_FSYNC=1</c>,于是别的用例开出来的库都不 fsync ——
/// 这里直接验 <see cref="SonnetDbEngine.CreateOptions" />,不经环境变量,守住「生产默认仍落盘」这一条。
/// </remarks>
[TestClass]
public sealed class SonnetDbEngineOptionsTests
{
    [TestMethod]
    public void Production_KeepsPerWriteFsyncOfTheWalAndSegments()
    {
        TsdbOptions options = SonnetDbEngine.CreateOptions("root", fsync: true);

        Assert.IsTrue(options.Kv.SyncWalOnEveryWrite, "生产路径每次 KV / 文档写入都要把 WAL 同步到磁盘");
        Assert.IsTrue(options.SegmentWriterOptions.FsyncOnCommit, "生产路径段文件提交要 fsync");
        Assert.AreEqual("root", options.RootDirectory);
        Assert.IsTrue(options.SegmentReaderOptions.UseMemoryMappedFileForLargeSegments);
    }

    [TestMethod]
    public void NoFsync_DropsOnlyTheDiskSyncs_AndStillHandsWritesToTheOs()
    {
        TsdbOptions durable = SonnetDbEngine.CreateOptions("root", fsync: true);
        TsdbOptions fast = SonnetDbEngine.CreateOptions("root", fsync: false);

        Assert.IsFalse(fast.Kv.SyncWalOnEveryWrite);
        Assert.IsFalse(fast.SegmentWriterOptions.FsyncOnCommit);
        Assert.IsTrue(fast.FlushWalToOsOnWrite, "写入仍要交给操作系统:同机关库重开读到的是同一份数据");
        // 其余参数与生产一致,只差那两项
        Assert.AreEqual(durable.Kv with { SyncWalOnEveryWrite = false }, fast.Kv);
        Assert.AreEqual(durable.SegmentWriterOptions with { FsyncOnCommit = false }, fast.SegmentWriterOptions);
        Assert.AreEqual(durable.SegmentReaderOptions.MemoryMappedFileThresholdBytes, fast.SegmentReaderOptions.MemoryMappedFileThresholdBytes);
    }
}
