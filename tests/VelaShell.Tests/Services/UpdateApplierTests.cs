using System.Formats.Tar;
using System.IO.Compression;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

[TestClass]
public class UpdateApplierTests : IDisposable
{
    private readonly string _appDir;
    private readonly UpdateApplier _applier;

    public UpdateApplierTests()
    {
        _appDir = Path.Combine(Path.GetTempPath(), $"velashell_applier_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_appDir);
        _applier = new(_appDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_appDir))
        {
            Directory.Delete(_appDir, true);
        }
    }

    private string CreateZip(params (string Path, string Content)[] entries)
    {
        string zipPath = Path.Combine(_applier.PrepareStagingDirectory(), "package.zip");
        using FileStream stream = File.Create(zipPath);
        using ZipArchive zip = new(stream, ZipArchiveMode.Create);
        foreach ((string path, string content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(path);
            using StreamWriter writer = new(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }

    private void WriteAppFile(string relativePath, string content)
    {
        string path = Path.Combine(_appDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadAppFile(string relativePath) =>
        File.ReadAllText(Path.Combine(_appDir, relativePath));

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_ReplacesPackagedFiles_AndLeavesUserFilesAlone()
    {
        WriteAppFile("app.exe", "old-exe");
        WriteAppFile("user-notes.txt", "user data");
        string zip = CreateZip(("app.exe", "new-exe"), ("lib/helper.dll", "new-dll"));

        _applier.Apply(zip);

        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
        Assert.AreEqual("new-dll", ReadAppFile(Path.Combine("lib", "helper.dll")));
        // 包外文件绝不动:既不重命名也不删除。
        Assert.AreEqual("user data", ReadAppFile("user-notes.txt"));
        Assert.IsFalse(File.Exists(Path.Combine(_appDir, "user-notes.txt.old")));
        // 被替换的旧文件以 .old 留存,待下次启动清理;新增文件没有 .old。
        Assert.AreEqual("old-exe", ReadAppFile("app.exe.old"));
        Assert.IsFalse(File.Exists(Path.Combine(_appDir, "lib", "helper.dll.old")));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_TarGzPackage_Works()
    {
        WriteAppFile("app", "old");
        string tarPath = Path.Combine(_applier.PrepareStagingDirectory(), "package.tar.gz");
        using (FileStream stream = File.Create(tarPath))
        using (GZipStream gzip = new(stream, CompressionMode.Compress))
        using (TarWriter tar = new(gzip))
        {
            string payload = Path.Combine(_appDir, "payload.tmp");
            File.WriteAllText(payload, "new");
            tar.WriteEntry(payload, "./app");
            File.Delete(payload);
        }

        _applier.Apply(tarPath);

        Assert.AreEqual("new", ReadAppFile("app"));
        Assert.AreEqual("old", ReadAppFile("app.old"));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_ThenFinalize_CleansOldFilesAndStaging()
    {
        WriteAppFile("app.exe", "old-exe");
        string zip = CreateZip(("app.exe", "new-exe"));
        _applier.Apply(zip);

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
        Assert.IsFalse(File.Exists(Path.Combine(_appDir, "app.exe.old")));
        Assert.IsFalse(Directory.Exists(_applier.StagingDirectory));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_ZipSlipEntry_ThrowsWithoutTouchingFiles()
    {
        WriteAppFile("app.exe", "old-exe");
        string zip = CreateZip(("../evil.txt", "evil"), ("app.exe", "new-exe"));

        Assert.ThrowsExactly<InvalidDataException>(() => _applier.Apply(zip));

        Assert.AreEqual("old-exe", ReadAppFile("app.exe"));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(_appDir)!, "evil.txt")));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_AbsoluteEntryPath_Throws()
    {
        string zip = CreateZip((OperatingSystem.IsWindows() ? "C:/evil.txt" : "/evil.txt", "evil"));
        Assert.ThrowsExactly<InvalidDataException>(() => _applier.Apply(zip));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_EmptyPackage_Throws()
    {
        string zip = CreateZip();
        Assert.ThrowsExactly<InvalidDataException>(() => _applier.Apply(zip));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_StaleOldAndNewLeftovers_ArePrecleaned()
    {
        // 历史残留的 .old/.new(上次清理失败)不能污染本次换版与回滚判定。
        WriteAppFile("app.exe", "current");
        WriteAppFile("app.exe.old", "ancient");
        WriteAppFile("app.exe.new", "stale");
        string zip = CreateZip(("app.exe", "new-exe"));

        _applier.Apply(zip);

        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
        Assert.AreEqual("current", ReadAppFile("app.exe.old"));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_CrashDuringSwap_RollsBack()
    {
        // 模拟换版中途崩溃后的现场:a 已换入新版(a.old 在),b(新增文件)已换入,
        // c 尚未换(仅 .new)。日志停留在 applying。
        WriteAppFile("a.txt", "a-new");
        WriteAppFile("a.txt.old", "a-old");
        WriteAppFile("b.txt", "b-new");
        WriteAppFile("c.txt", "c-old");
        WriteAppFile("c.txt.new", "c-new");
        Directory.CreateDirectory(_applier.StagingDirectory);
        File.WriteAllText(Path.Combine(_applier.StagingDirectory, "apply.json"), """
            {
              "Phase": "applying",
              "Files": [
                { "Path": "a.txt", "Existed": true },
                { "Path": "b.txt", "Existed": false },
                { "Path": "c.txt", "Existed": true }
              ]
            }
            """);

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.AreEqual("a-old", ReadAppFile("a.txt"));
        Assert.IsFalse(File.Exists(Path.Combine(_appDir, "b.txt")), "新增文件应被回滚删除");
        Assert.AreEqual("c-old", ReadAppFile("c.txt"));
        Assert.IsFalse(File.Exists(Path.Combine(_appDir, "a.txt.old")));
        Assert.IsFalse(File.Exists(Path.Combine(_appDir, "c.txt.new")));
        Assert.IsFalse(Directory.Exists(_applier.StagingDirectory));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_CrashBeforeSwap_RemovesStagedFiles()
    {
        WriteAppFile("a.txt", "a-old");
        WriteAppFile("a.txt.new", "a-new");
        Directory.CreateDirectory(_applier.StagingDirectory);
        File.WriteAllText(Path.Combine(_applier.StagingDirectory, "apply.json"), """
            { "Phase": "staged", "Files": [ { "Path": "a.txt", "Existed": true } ] }
            """);

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.AreEqual("a-old", ReadAppFile("a.txt"));
        Assert.IsFalse(File.Exists(Path.Combine(_appDir, "a.txt.new")));
        Assert.IsFalse(Directory.Exists(_applier.StagingDirectory));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_NoJournal_KeepsDownloadsForResume()
    {
        // 无换版日志时暂存目录里只有下载产物,保留给断点续传/免下载复用,
        // 过期残留由下次下载按文件名清理。
        _applier.PrepareStagingDirectory();
        string archive = Path.Combine(_applier.StagingDirectory, "package.zip");
        string partial = Path.Combine(_applier.StagingDirectory, "package.zip.partial");
        File.WriteAllText(archive, "leftover download");
        File.WriteAllText(partial, "half download");

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.AreEqual("leftover download", File.ReadAllText(archive));
        Assert.AreEqual("half download", File.ReadAllText(partial));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_NothingPending_ReturnsTrue()
    {
        Assert.IsTrue(_applier.TryFinalizeStartup());
    }

    [TestMethod]
    [TestCategory("Update")]
    public void IsApplicationDirectoryWritable_TempDir_IsTrue()
    {
        Assert.IsTrue(_applier.IsApplicationDirectoryWritable());
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_PackageEntryInsideStagingDirectory_IsIgnored()
    {
        WriteAppFile("app.exe", "old-exe");
        string zip = CreateZip(
            ("app.exe", "new-exe"),
            ($"{UpdateApplier.StagingDirectoryName}/apply.json", "malicious journal"));

        _applier.Apply(zip);

        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
    }
}
