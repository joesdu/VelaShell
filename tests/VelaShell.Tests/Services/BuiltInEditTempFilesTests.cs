using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 内置编辑器临时副本的启动兜底清理:只清 <c>builtin-edit</c> 里 7 天没碰过的子目录;
/// 旁边 <c>remote-edit</c> 里回传失败刻意保留的草稿,多旧都不动。
/// </summary>
[TestClass]
[TestCategory("ExternalEdit")]
public sealed class BuiltInEditTempFilesTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private string _base = string.Empty;

    [TestInitialize]
    public void CreateBase() =>
        _base = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vela-edit-sweep-{Guid.NewGuid():N}")).FullName;

    [TestCleanup]
    public void DeleteBase()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言。
        }
    }

    [TestMethod]
    public void SweepStale_RemovesOnlyOldBuiltInCopies_AndNeverTouchesRemoteEditDrafts()
    {
        string stale = Copy("builtin-edit", "0a1b2c3d", "nginx.conf", Now.AddDays(-8));
        string fresh = Copy("builtin-edit", "11112222", "hosts", Now.AddDays(-1));
        // 目录本身很旧,但里面的文件昨天才存过 —— 还算在用。
        string savedRecently = Copy("builtin-edit", "33334444", "app.yaml", Now.AddDays(-30));
        File.SetLastWriteTimeUtc(Path.Combine(savedRecently, "app.yaml"), Now.AddHours(-20));
        // 不是本程序起的名字:不认识的东西不删。
        string foreign = Copy("builtin-edit", "keep-me", "readme.txt", Now.AddDays(-90));
        // 外部编辑器那条路回传失败时刻意保留的草稿:一个月前的也不能删。
        string draft = Copy("remote-edit", "deadbeef", "crontab", Now.AddDays(-30));

        int removed = BuiltInEditTempFiles.SweepStale(_base, Now);

        Assert.AreEqual(1, removed);
        Assert.IsFalse(Directory.Exists(stale), "8 天没碰过的内置编辑器副本应当清掉");
        Assert.IsTrue(Directory.Exists(fresh));
        Assert.IsTrue(Directory.Exists(savedRecently), "按目录里最晚的修改时间算,不只看目录本身");
        Assert.IsTrue(Directory.Exists(foreign));
        Assert.IsTrue(File.Exists(Path.Combine(draft, "crontab")), "remote-edit 里的草稿是改动唯一的存身之处,不归这里清");
    }

    [TestMethod]
    public void SweepStale_LeasedCopyIsProtectedRegardlessOfAge()
    {
        string directory = Copy("builtin-edit", "1234abcd", "app.conf", Now.AddDays(-30));
        using (EditDirectoryLease.Acquire(directory))
        {
            Directory.SetLastWriteTimeUtc(directory, Now.AddDays(-30));
            Assert.AreEqual(0, BuiltInEditTempFiles.SweepStale(_base, Now));
            Assert.IsTrue(Directory.Exists(directory));
        }
        Assert.AreEqual(1, BuiltInEditTempFiles.SweepStale(_base, Now));
    }

    [TestMethod]
    public void SweepStale_ResumesRetiredCleanupOnlyAfterLeaseIsReleased()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Directory retirement is Unix-only; Windows deletes under the held lease.");
            return;
        }
        string directory = Copy("builtin-edit", "1234abcd", "app.conf", Now);
        string retired = directory + ".deleting-" + Guid.NewGuid().ToString("N");
        using (EditDirectoryLease.Acquire(directory))
        {
            Directory.Move(directory, retired);
            Assert.AreEqual(0, BuiltInEditTempFiles.SweepStale(_base, Now));
            Assert.IsTrue(File.Exists(Path.Combine(retired, "app.conf")));
        }
        Assert.AreEqual(1, BuiltInEditTempFiles.SweepStale(_base, Now));
        Assert.IsFalse(Directory.Exists(retired));
    }

    [TestMethod]
    public void SweepStale_DoesNotFollowNestedDirectoryLinks()
    {
        string directory = Copy("builtin-edit", "1234abcd", "app.conf", Now.AddDays(-30));
        string target = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        string protectedFile = Path.Combine(target, "keep.txt");
        File.WriteAllText(protectedFile, "keep");
        string link = Path.Combine(directory, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links unavailable: {ex.Message}");
            return;
        }
        try
        {
            Directory.SetLastWriteTimeUtc(directory, Now.AddDays(-30));
            Assert.AreEqual(0, BuiltInEditTempFiles.SweepStale(_base, Now));
            Assert.AreEqual("keep", File.ReadAllText(protectedFile));
            Assert.IsTrue(Directory.Exists(directory));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public async Task OpenOwnedAsync_FailedOpenRemovesDownloadedCopy()
    {
        string directory = Path.Combine(_base, "builtin-edit", "1234abcd");
        await Assert.ThrowsExactlyAsync<IOException>(() => BuiltInEditTempFiles.OpenOwnedAsync(directory, () =>
        {
            File.WriteAllText(Path.Combine(directory, "app.conf"), "partial download");
            throw new IOException("download failed");
        }));
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void SweepStale_MissingRoot_IsANoOp() =>
        Assert.AreEqual(0, BuiltInEditTempFiles.SweepStale(Path.Combine(_base, "absent"), Now));

    /// <summary>真实路径上两棵树是兄弟:清理的根就是内置编辑器那一棵,草稿所在的 remote-edit 不在它下面。</summary>
    [TestMethod]
    public void RealRoots_AreSiblingsUnderTheSameBase()
    {
        Assert.AreEqual(Path.Combine(BuiltInEditTempFiles.TempBase, "builtin-edit"), BuiltInEditTempFiles.Root);
        Assert.AreEqual(Path.Combine(BuiltInEditTempFiles.TempBase, "remote-edit"), RemoteEditSessionManager.TempRoot);
        StringAssert.StartsWith(BuiltInEditTempFiles.NewDirectory(), BuiltInEditTempFiles.Root + Path.DirectorySeparatorChar);
    }

    /// <summary>建一个带文件的子目录,文件与目录的修改时间都设成 <paramref name="touchedAt" />。</summary>
    private string Copy(string tree, string name, string fileName, DateTime touchedAt)
    {
        string directory = Directory.CreateDirectory(Path.Combine(_base, tree, name)).FullName;
        string file = Path.Combine(directory, fileName);
        File.WriteAllText(file, "content");
        File.SetLastWriteTimeUtc(file, touchedAt);
        Directory.SetLastWriteTimeUtc(directory, touchedAt);
        return directory;
    }
}
