using System.Text;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>差异标签保存时换行照原文件:LF 的远端配置不能因为在 Windows 上编辑而混进 CRLF。</summary>
[TestClass]
public sealed class FileTextStoreTests
{
    [TestMethod]
    [DataRow("a\nb\n", "x\r\ny\nz", "x\ny\nz")]
    [DataRow("a\r\nb\r\n", "x\r\ny\nz", "x\r\ny\r\nz")]
    [DataRow("a\r\nb\nc\r\n", "x\ny", "x\r\ny")]
    [DataRow("one line", "x\r\ny", "x\r\ny")]
    [DataRow(null, "x\r\ny", "x\r\ny")]
    public void MatchLineEndings_FollowsDominantStyleOfOriginal(string? reference, string text, string expected)
        => Assert.AreEqual(expected, FileTextStore.MatchLineEndings(text, reference));

    [TestMethod]
    public async Task LocalRestore_RequiresMatchingContentsAndPreservesBomAcrossRename()
    {
        using var context = new TestPluginContext();
        string root = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var store = new FileTextStore(context, () => [new LocalWorkspace { Id = "work", RootPath = root }], 1024);
            var rename = new FileChange("rename", "local:work", "new.txt", FileChangeKind.Renamed, "old", "longer AI text", "old.txt");
            string path = Path.Combine(root, rename.Path);
            string old = Path.Combine(root, "old.txt");
            File.WriteAllText(path, "my edit", Encoding.UTF8);
            Assert.AreEqual(FileTextStore.ErrorReviewConflict, await store.RestoreAsync(rename));
            Assert.AreEqual("my edit", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(old));

            File.WriteAllText(path, rename.After, Encoding.UTF8);
            File.WriteAllText(old, "occupied");
            Assert.AreEqual(FileTextStore.ErrorReviewConflict, await store.RestoreAsync(rename));
            Assert.AreEqual("occupied", File.ReadAllText(old));
            File.Delete(old);
            Assert.AreEqual(FileTextStore.ErrorPathRejected, await store.RestoreAsync(rename with { OldPath = "../outside" }));
            Assert.IsNull(await store.RestoreAsync(rename));
            Assert.IsFalse(File.Exists(path));
            CollectionAssert.AreEqual((byte[])[.. Encoding.UTF8.Preamble, .. "old"u8], File.ReadAllBytes(old));

            var modified = rename with { Kind = FileChangeKind.Modified, OldPath = null, Path = "old.txt", Before = "base", After = "old", BeforeHasBom = false };
            Assert.IsNull(await store.RestoreAsync(modified));
            CollectionAssert.AreEqual("base"u8.ToArray(), File.ReadAllBytes(old));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LocalRestore_CreatedAndDeletedRequireTheExpectedPathState()
    {
        using var context = new TestPluginContext();
        string root = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var store = new FileTextStore(context, () => [new LocalWorkspace { Id = "work", RootPath = root }], 1024);
            var change = new FileChange("create", "local:work", "file", FileChangeKind.Created, null, "");
            string path = Path.Combine(root, change.Path);
            Directory.CreateDirectory(path);
            Assert.AreEqual(FileTextStore.ErrorReviewConflict, await store.RestoreAsync(change));
            Directory.Delete(path);
            File.WriteAllText(path, "");
            Assert.AreEqual(FileTextStore.ErrorReviewUnavailable, await store.RestoreAsync(change with { After = null }));
            Assert.IsNull(await store.RestoreAsync(change));
            Assert.IsFalse(File.Exists(path));

            var deleted = change with { Kind = FileChangeKind.Deleted, Before = "original", After = null, OldPath = "old", BeforeHasBom = true };
            Assert.IsNull(await store.RestoreAsync(deleted));
            Assert.IsFalse(File.Exists(path));
            CollectionAssert.AreEqual((byte[])[.. Encoding.UTF8.Preamble, .. "original"u8], File.ReadAllBytes(Path.Combine(root, "old")));
            Assert.AreEqual(FileTextStore.ErrorReviewConflict, await store.RestoreAsync(deleted));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteRestore_ChecksCurrentPathTypeAndSnapshotBeforeMutating()
    {
        using var context = new TestPluginContext();
        string session = context.FakeSessions.AddConnected().SessionId;
        var fs = context.FakeRemoteFs;
        var store = new FileTextStore(context, () => [], 1024);
        var create = new FileChange("create", $"remote:{session}", "/new", FileChangeKind.Created, null, "ai");
        fs.AddFile(session, "/new/child", "child"u8.ToArray());
        Assert.AreEqual(FileTextStore.ErrorReviewConflict, await store.RestoreAsync(create));
        CollectionAssert.AreEqual("child"u8.ToArray(), fs.GetFile(session, "/new/child"));

        create = create with { Path = "/source" };
        fs.AddFile(session, create.Path, "changed"u8.ToArray());
        Assert.AreEqual(FileTextStore.ErrorReviewConflict, await store.RestoreAsync(create));
        fs.AddFile(session, create.Path, "ai"u8.ToArray());
        var rename = create with { Kind = FileChangeKind.Renamed, OldPath = "/old", Before = "original", BeforeHasBom = true };
        Assert.IsNull(await store.RestoreAsync(rename));
        Assert.IsNull(fs.GetFile(session, create.Path));
        CollectionAssert.AreEqual((byte[])[.. Encoding.UTF8.Preamble, .. "original"u8], fs.GetFile(session, "/old"));
        Assert.IsNull(await store.RestoreAsync(create with { Path = "/old", After = "original" }));
        Assert.IsNull(fs.GetFile(session, "/old"));
        var deleted = create with { Kind = FileChangeKind.Deleted, Before = "before", After = null };
        Assert.IsNull(await store.RestoreAsync(deleted));
        CollectionAssert.AreEqual("before"u8.ToArray(), fs.GetFile(session, create.Path));
        Assert.AreEqual(FileTextStore.ErrorSessionGone, await store.RestoreAsync(deleted with { Location = "remote:disconnected" }));
    }

    [TestMethod]
    public void MatchLineEndings_LeavesOtherSeparatorsAlone()
    {
        string ls = ((char)0x2028).ToString();
        string text = "a\fb" + ls + "c\rd\r\ne";
        Assert.AreEqual("a\fb" + ls + "c\rd\ne", FileTextStore.MatchLineEndings(text, "x\ny"));
    }
}
