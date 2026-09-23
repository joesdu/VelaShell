using NSubstitute;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.DirectorySync;

[TestClass]
public class DirectoryTreeScannerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vela-sync-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言。
        }
    }

    [TestMethod]
    public async Task ScanLocal_ReturnsRelativeSlashPaths_AndDoesNotEnterExcludedDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "deep", "a.txt"), "abc");
        await File.WriteAllTextAsync(Path.Combine(_root, "node_modules", "pkg", "index.js"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "top.log"), "12345");
        Assert.IsTrue(SyncFileMask.TryParse("| node_modules/", out SyncFileMask mask, out _));

        SyncTree tree = await DirectoryTreeScanner.ScanLocalAsync(_root, mask);

        Assert.AreSequenceEqual(
            ["src", "src/deep", "src/deep/a.txt", "top.log"], [.. tree.Items.Select(i => i.RelativePath)], SequenceOrder.InAnyOrder);
        SyncItem file = tree.Items.Single(i => i.RelativePath == "src/deep/a.txt");
        Assert.AreEqual(3, file.Size);
        Assert.AreEqual(DateTimeKind.Utc, file.LastWriteTimeUtc.Kind);
    }

    [TestMethod]
    public async Task ScanLocal_NonRecursiveWithPrefix_KeepsPathsRelativeToTheSyncRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child", "grandchild"));
        await File.WriteAllTextAsync(Path.Combine(_root, "child", "f.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(_root, "child", "grandchild", "g.txt"), "1");

        SyncTree tree = await DirectoryTreeScanner.ScanLocalAsync(
            Path.Combine(_root, "child"), SyncFileMask.Empty, recursive: false, relativePrefix: "child");

        Assert.AreSequenceEqual(
            ["child/f.txt", "child/grandchild"], [.. tree.Items.Select(i => i.RelativePath)], SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task ScanLocal_MissingRoot_Throws()
    {
        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() =>
            DirectoryTreeScanner.ScanLocalAsync(Path.Combine(_root, "nope"), SyncFileMask.Empty));
    }

    [TestMethod]
    public async Task ScanRemote_SkipsDirectoryLinks_FollowsFileLinks_AndInfersFtpPrecision()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var session = Guid.NewGuid();
        sftp.ListDirectoryAsync(session, "/srv", Arg.Any<CancellationToken>()).Returns(
        [
            Remote("/srv/app", isDirectory: true, new(2026, 9, 1, 10, 30, 0)),
            Remote("/srv/current", isDirectory: true, new(2026, 9, 1, 10, 30, 0), linkTarget: "app"),
            Remote("/srv/latest.log", isDirectory: false, new(2026, 9, 1, 10, 31, 0), linkTarget: "app/x.log"),
            Remote("/srv/old.tar", isDirectory: false, new(2025, 1, 2)),
        ]);
        sftp.ListDirectoryAsync(session, "/srv/app", Arg.Any<CancellationToken>()).Returns(
        [
            Remote("/srv/app/x.log", isDirectory: false, new(2026, 9, 1, 10, 31, 7)),
        ]);

        SyncTree tree = await DirectoryTreeScanner.ScanRemoteAsync(sftp, session, "/srv", SyncFileMask.Empty, inferPrecision: true);

        Assert.AreEqual(1, tree.SkippedLinks, "指向目录的链接不进入");
        Assert.AreSequenceEqual(
            ["app", "app/x.log", "latest.log", "old.tar"], [.. tree.Items.Select(i => i.RelativePath)], SequenceOrder.InAnyOrder);
        Assert.AreEqual(SyncTimePrecision.Minute, tree.Items.Single(i => i.RelativePath == "latest.log").Precision);
        Assert.AreEqual(SyncTimePrecision.Day, tree.Items.Single(i => i.RelativePath == "old.tar").Precision);
        Assert.AreEqual(SyncTimePrecision.Second, tree.Items.Single(i => i.RelativePath == "app/x.log").Precision);
        await sftp.DidNotReceive().ListDirectoryAsync(session, "/srv/current", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ScanRemote_WithoutInference_TreatsWholeMinuteSftpTimesAsSeconds()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var session = Guid.NewGuid();
        sftp.ListDirectoryAsync(session, "/", Arg.Any<CancellationToken>()).Returns(
        [
            Remote("/a.txt", isDirectory: false, new(2026, 9, 1, 10, 30, 0)),
        ]);

        SyncTree tree = await DirectoryTreeScanner.ScanRemoteAsync(sftp, session, "/", SyncFileMask.Empty, inferPrecision: false);

        Assert.AreEqual(SyncTimePrecision.Second, tree.Items.Single().Precision);
        Assert.AreEqual("/a.txt", tree.Items.Single().FullPath);
    }

    [TestMethod]
    public void CombineRemote_HandlesRootAndTrailingSlashes()
    {
        Assert.AreEqual("/a/b", DirectoryTreeScanner.CombineRemote("/", "a/b"));
        Assert.AreEqual("/srv/a", DirectoryTreeScanner.CombineRemote("/srv/", "a"));
        Assert.AreEqual("/srv", DirectoryTreeScanner.CombineRemote("/srv", ""));
    }

    private static RemoteFileInfo Remote(string path, bool isDirectory, DateTime modified, string? linkTarget = null) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        FullPath = path,
        Size = isDirectory ? 4096 : 10,
        Permissions = "rw-r--r--",
        IsDirectory = isDirectory,
        IsSymbolicLink = linkTarget is not null,
        LinkTarget = linkTarget,
        LastModified = modified,
        Owner = "u",
        Group = "g",
    };
}
