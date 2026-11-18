using Avalonia.Headless;
using VelaShell.Core.Models;
using VelaShell.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Views;

[TestClass]
public sealed class UploadPickerSelectionTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(UploadPickerSelectionTests).Assembly);

    [TestMethod]
    public Task ExactMapping_RestoresItsDirectoryAndChecks_AcrossStoreInstances() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var disk = new UploadMemoryStore();
        var context = new UploadPickerContext(Guid.NewGuid(), "/app/./out/..", "server");
        var store = new UploadPickerStateStore(disk);
        await store.SaveAsync(context, tree.A, [tree.FileA, Path.Combine(tree.A, "missing")], true);
        await store.SaveAsync(null, tree.B, [tree.FileB], false);

        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, new(disk), context with { RemotePath = "/app/" });
        await picker.InitializeAsync();
        await picker.FlushAsync();

        Assert.AreEqual(tree.A, browser.CurrentPath);
        Assert.AreSequenceEqual([tree.FileA], picker.CheckedEntries.Select(entry => entry.FullPath).ToArray());
        Assert.IsTrue(picker.Mappings.Single().IsCurrent);
        Assert.IsEmpty(browser.SelectedEntries, "恢复勾选不改变列表高亮。");
        UploadPickerState restored = await new UploadPickerStateStore(disk).LoadAsync();
        Assert.AreSequenceEqual([tree.FileA], restored.Mappings.Single().CheckedPaths);
    });

    [TestMethod]
    public Task DifferentConnectionAndChildDirectory_DoNotInheritMappings() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        Guid profile = Guid.NewGuid();
        await store.SaveAsync(new(profile, "/app", "one"), tree.A, [tree.FileA], true);
        await store.SaveAsync(null, tree.B, [tree.FileB], false);
        foreach (UploadPickerContext context in new[]
        {
            new UploadPickerContext(profile, "/app/child", "one"),
            new UploadPickerContext(Guid.NewGuid(), "/app", "two"),
        })
        {
            using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
            using var picker = new UploadPickerSelection(browser, store, context);
            await picker.InitializeAsync();
            await picker.FlushAsync();
            Assert.AreEqual(tree.B, browser.CurrentPath);
            Assert.AreSequenceEqual([tree.FileB], picker.CheckedEntries.Select(entry => entry.FullPath).ToArray());
        }
    });

    [TestMethod]
    public Task ManualNavigation_ClearsChecks_RefreshPreservesExistingAndNewFilesStayUnchecked() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        await browser.NavigateToAsync(tree.A);
        using var picker = new UploadPickerSelection(browser, new(null), new(Guid.NewGuid(), "/app", "one"));
        picker.SetAll(true);
        Assert.IsTrue(picker.AllChecked);
        Assert.IsFalse(picker.IsChecked(browser.Entries.Single(entry => entry.IsParentEntry)));
        browser.SelectedEntries.Add(browser.Entries.Single(entry => entry.FullPath == tree.FileA));
        await File.WriteAllTextAsync(Path.Combine(tree.A, "new.txt"), "new");
        await browser.RefreshAsync();
        Assert.IsNull(picker.AllChecked, "刷新新出现的文件不自动勾选。");
        Assert.AreSequenceEqual([tree.FileA], picker.CheckedEntries.Where(entry => !entry.IsDirectory).Select(entry => entry.FullPath).ToArray());
        await browser.NavigateToAsync(tree.A + Path.DirectorySeparatorChar);
        Assert.IsNotEmpty(picker.CheckedEntries, "同一目录的尾分隔符不应清空勾选。");
        File.Delete(tree.FileA);
        await browser.RefreshAsync();
        Assert.IsFalse(picker.CheckedEntries.Any(entry => entry.FullPath == tree.FileA));
        await browser.NavigateToAsync(tree.B);
        Assert.IsEmpty(picker.CheckedEntries);
        await browser.NavigateToAsync(tree.A);
        Assert.IsEmpty(picker.CheckedEntries, "手动返回旧目录也从未勾选开始。");
        await picker.FlushAsync();
    });

    [TestMethod]
    public Task OpeningAnotherMapping_CopiesChecksIntoCurrentTarget_WithoutChangingSource() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var disk = new UploadMemoryStore();
        var store = new UploadPickerStateStore(disk);
        Guid profile = Guid.NewGuid();
        var target = new UploadPickerContext(profile, "/app", "one");
        await store.SaveAsync(target, tree.A, [tree.FileA], true);
        await store.SaveAsync(target with { RemotePath = "/web" }, tree.B, [tree.FileB], true);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, target);
        await picker.InitializeAsync();
        await picker.FlushAsync();
        await picker.ApplyMappingAsync(picker.Mappings.Single(item => item.RemotePath == "/web"));
        await picker.FlushAsync();
        Assert.AreEqual(tree.B, browser.CurrentPath);
        Assert.AreSequenceEqual([tree.FileB], picker.CheckedEntries.Select(entry => entry.FullPath).ToArray());
        UploadPickerState state = await new UploadPickerStateStore(disk).LoadAsync();
        foreach (UploadDirectoryMapping item in state.Mappings)
        {
            Assert.AreEqual(tree.B, item.LocalPath);
            Assert.AreSequenceEqual([tree.FileB], item.CheckedPaths);
        }
        Assert.AreEqual("/app", picker.Mappings[0].RemotePath);
        Assert.IsTrue(picker.Mappings[0].IsCurrent);
    });

    [TestMethod]
    public Task UnavailableMapping_FallsBackWithoutOverwriting_UntilUserChangesChecks() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/app", "one");
        string missing = Path.Combine(tree.A, "gone");
        await store.SaveAsync(context, missing, [], true);
        await store.SaveAsync(null, tree.B, [], false);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, context);
        await picker.InitializeAsync();
        await picker.FlushAsync();
        Assert.AreEqual(tree.B, browser.CurrentPath);
        Assert.IsNotNull(picker.ErrorMessage);
        Assert.AreEqual(missing, (await store.LoadAsync()).Mappings.Single().LocalPath);
        await browser.RefreshAsync();
        await picker.FlushAsync();
        Assert.AreEqual(missing, (await store.LoadAsync()).Mappings.Single().LocalPath);
        picker.SetChecked(browser.Entries.Single(entry => entry.FullPath == tree.FileB), true);
        await picker.FlushAsync();
        Assert.AreEqual(tree.B, (await store.LoadAsync()).Mappings.Single().LocalPath);
        Assert.IsNull(picker.ErrorMessage);
    });

    [TestMethod]
    public Task FailedNavigation_PreservesDirectoryAndChecks_AndTemporaryContextCreatesNoMapping() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        await browser.NavigateToAsync(tree.A);
        using var picker = new UploadPickerSelection(browser, store, new(null, "/app", "temporary"));
        picker.SetAll(true);
        string[] before = [.. picker.CheckedEntries.Select(entry => entry.FullPath)];
        await browser.NavigateToAsync(Path.Combine(tree.A, "missing"));
        Assert.AreEqual(tree.A, browser.CurrentPath);
        Assert.AreSequenceEqual(before, picker.CheckedEntries.Select(entry => entry.FullPath).ToArray());
        picker.SetChecked(browser.Entries.Single(entry => entry.FullPath == tree.FileA), false);
        await picker.FlushAsync();
        UploadPickerState state = await store.LoadAsync();
        Assert.IsEmpty(state.Mappings);
        Assert.IsFalse(state.CheckedPaths.Contains(tree.FileA), "导航失败后修改勾选仍须持久化。");
    });

    [TestMethod]
    public Task NewPicker_UsesConfiguredDirectory_WhenThereIsNoHistory() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        using var browser = new LocalFilePaneViewModel(new TransferOptions { LocalDownloadDirectory = tree.A }, new UnwatchedLocalFileSystem());
        using var picker = new UploadPickerSelection(browser, new(null), new(Guid.NewGuid(), "/new", "server"));
        await picker.InitializeAsync();
        await picker.FlushAsync();
        Assert.AreEqual(tree.A, browser.CurrentPath);
        Assert.IsEmpty(picker.CheckedEntries);
        Assert.IsTrue(picker.Mappings.Single().IsCurrent);
    });

    [TestMethod]
    public Task LateMappingNavigation_CannotReplaceTheNewerDirectoryOrItsChecks() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var delayed = new TaskCompletionSource<IReadOnlyList<LocalFileSystemEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new ControlledFileSystem(path => path == tree.A ? delayed.Task
            : Task.FromResult<IReadOnlyList<LocalFileSystemEntry>>([new("b.txt", tree.FileB, false, 1, DateTime.UtcNow)]));
        using var browser = new LocalFilePaneViewModel(new(), fs);
        await browser.NavigateToAsync(tree.B);
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        Guid profile = Guid.NewGuid();
        using var picker = new UploadPickerSelection(browser, store, new(profile, "/app", "server"));
        Task old = picker.ApplyMappingAsync(new(new()
        {
            ProfileId = profile, RemotePath = "/old", LocalPath = tree.A, CheckedPaths = [tree.FileA],
        }, false));
        await picker.ApplyMappingAsync(new(new()
        {
            ProfileId = profile, RemotePath = "/new", LocalPath = tree.B, CheckedPaths = [tree.FileB],
        }, false));
        delayed.SetResult([new("a.txt", tree.FileA, false, 1, DateTime.UtcNow)]);
        await old;
        await picker.FlushAsync();
        Assert.AreEqual(tree.B, browser.CurrentPath);
        Assert.AreSequenceEqual([tree.FileB], picker.CheckedEntries.Select(entry => entry.FullPath).ToArray());
        Assert.AreEqual(tree.B, (await store.LoadAsync()).Mappings.Single().LocalPath);
        Assert.IsNull(picker.ErrorMessage);
    });

    [TestMethod]
    public async Task SlowWrites_AreOrderedAndMergeOtherTargets_AndFailureDoesNotBreakTheQueue()
    {
        var disk = new UploadMemoryStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        disk.BeforeWrite = async () =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                started.SetResult();
                await release.Task;
                throw new IOException("first write failed");
            }
        };
        var store = new UploadPickerStateStore(disk);
        Guid profile = Guid.NewGuid();
        Task first = store.SaveAsync(new(profile, "/one", "server"), "first", [], true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = store.SaveAsync(new(profile, "/two", "server"), "second", ["checked"], true);
        Task<UploadPickerState> reading = store.LoadAsync();
        Assert.IsFalse(second.IsCompleted);
        Assert.IsFalse(reading.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);
        UploadPickerState state = await reading;
        Assert.AreEqual("second", state.LocalPath);
        Assert.HasCount(2, state.Mappings);
        Assert.HasCount(2, (await new UploadPickerStateStore(disk).LoadAsync()).Mappings);
    }

    private sealed class ControlledFileSystem(Func<string, Task<IReadOnlyList<LocalFileSystemEntry>>> enumerate) : ILocalFileSystem
    {
        public Task<IReadOnlyList<LocalFileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken) => enumerate(path);
        public IDisposable? Watch(string directory, Action onChanged) => null;
        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MoveAsync(string sourcePath, string destPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string path, FileMode mode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Tree : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vela-upload-state-" + Guid.NewGuid().ToString("N"));
        public string A => Path.Combine(_root, "a");
        public string B => Path.Combine(_root, "b");
        public string FileA => Path.Combine(A, "a.txt");
        public string FileB => Path.Combine(B, "b.txt");

        public Tree()
        {
            Directory.CreateDirectory(Path.Combine(A, "folder"));
            Directory.CreateDirectory(B);
            File.WriteAllText(FileA, "a");
            File.WriteAllText(FileB, "b");
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
