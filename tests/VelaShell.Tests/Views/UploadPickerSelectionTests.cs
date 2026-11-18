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
            Assert.IsEmpty(picker.CheckedEntries, "首次自动建立的映射没有勾选。");
        }
    });

    [TestMethod]
    public Task ManualNavigation_ClearsChecks_RefreshPreservesExistingAndNewFilesStayUnchecked() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        await browser.NavigateToAsync(tree.A);
        using var picker = new UploadPickerSelection(browser, new(null), new(null, "/app", "one"));
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
    public Task OpeningAnotherMapping_RestoresChecksAndChangesTarget_WithoutChangingSource() => _session.RunOnUiAsync(async () =>
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
        Assert.AreEqual(tree.A, state.Mappings.Single(item => item.RemotePath == "/app").LocalPath);
        Assert.AreSequenceEqual([tree.FileA], state.Mappings.Single(item => item.RemotePath == "/app").CheckedPaths);
        Assert.AreEqual("/web", picker.TargetRemotePath);
        Assert.AreSequenceEqual(new[] { "/app", "/web" }, picker.Mappings.Select(item => item.RemotePath).ToArray());
        Assert.IsTrue(picker.Mappings[1].IsCurrent);
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
        await store.SaveAsync(new(profile, "/old", "server"), tree.A, [tree.FileA], true);
        await store.SaveAsync(new(profile, "/new", "server"), tree.B, [tree.FileB], true);
        using var picker = new UploadPickerSelection(browser, store, new(profile, "/new", "server"));
        await picker.InitializeAsync();
        Task old = picker.ApplyMappingAsync(picker.Mappings.Single(item => item.RemotePath == "/old"));
        // 等旧的列表请求开始,再发起新导航。
        await Task.Yield();
        await picker.ApplyMappingAsync(picker.Mappings.Single(item => item.RemotePath == "/new"));
        delayed.SetResult([new("a.txt", tree.FileA, false, 1, DateTime.UtcNow)]);
        await old;
        await picker.FlushAsync();
        Assert.AreEqual(tree.B, browser.CurrentPath);
        Assert.AreSequenceEqual([tree.FileB], picker.CheckedEntries.Select(entry => entry.FullPath).ToArray());
        Assert.AreEqual(tree.B, (await store.LoadAsync()).Mappings.Single(item => item.RemotePath == "/new").LocalPath);
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
        Task first = store.AddMappingAsync(new(profile, "/one", "server"), "first");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = store.AddMappingAsync(new(profile, "/two", "server"), "second", ["checked"]);
        Task<UploadPickerState> reading = store.LoadAsync();
        Assert.IsFalse(second.IsCompleted);
        Assert.IsFalse(reading.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);
        UploadPickerState state = await reading;
        Assert.AreSequenceEqual(new[] { "checked" }, state.Mappings.Single(item => item.RemotePath == "/two").CheckedPaths);
        Assert.HasCount(2, state.Mappings);
        Assert.HasCount(2, (await new UploadPickerStateStore(disk).LoadAsync()).Mappings);
    }

    [TestMethod]
    public Task MultipleMappingsForOneRemote_DuplicateBindingPreservesExistingChecks() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/app", "one");
        var first = await store.AddMappingAsync(context, tree.A, [tree.FileA]);
        var second = await store.AddMappingAsync(context, tree.B, [tree.FileB]);
        Assert.AreNotEqual(first.Id, second.Id);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, context);
        await picker.InitializeAsync();
        Assert.AreEqual(second.Id, picker.ActiveMappingId, "无提交历史时选当前目录最近保存的记录。");
        await picker.AddMappingAsync("/app/./", tree.A);
        await picker.FlushAsync();
        Assert.AreEqual(first.Id, picker.ActiveMappingId);
        Assert.AreSequenceEqual([tree.FileA], picker.CheckedEntries.Select(item => item.FullPath).ToArray());
        Assert.HasCount(2, (await store.LoadAsync()).Mappings);
    });

    [TestMethod]
    public Task LastSubmittedMapping_IsPerConnection_AndBrowsingDoesNotReplaceIt() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var disk = new UploadMemoryStore();
        var store = new UploadPickerStateStore(disk);
        var context = new UploadPickerContext(Guid.NewGuid(), "/app", "one");
        await store.AddMappingAsync(context, tree.A, [tree.FileA]);
        Guid web = (await store.AddMappingAsync(context with { RemotePath = "/web" }, tree.B, [tree.FileB])).Id;
        using (LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane())
        using (var picker = new UploadPickerSelection(browser, store, context))
        {
            await picker.InitializeAsync();
            await picker.ApplyMappingAsync(picker.Mappings.Single(item => item.Mapping.Id == web));
            UploadSelectionResult result = picker.Submit()!;
            Assert.AreEqual("/web", result.RemotePath);
            Assert.AreEqual(web, result.MappingId);
            Assert.AreSequenceEqual([tree.FileB], result.LocalPaths);
            await picker.FlushAsync();
            await picker.ApplyMappingAsync(picker.Mappings.Single(item => item.RemotePath == "/app"));
            picker.SetAll(false); // 相当于浏览后取消,不重新提交。
            await picker.FlushAsync();
        }
        using LocalFilePaneViewModel reopened = UnwatchedLocalFileSystem.NewPane();
        using var restored = new UploadPickerSelection(reopened, new(disk), context);
        await restored.InitializeAsync();
        Assert.AreEqual(web, restored.ActiveMappingId);
        Assert.AreEqual("/web", restored.TargetRemotePath);
        Assert.AreSequenceEqual([tree.FileB], restored.CheckedEntries.Select(item => item.FullPath).ToArray());
        await restored.FlushAsync();
    });

    [TestMethod]
    public Task UnmappedEntry_CreatesAndSelectsItEvenWhenAnotherRemoteWasLastSubmitted() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/web", "one");
        Guid web = (await store.AddMappingAsync(context, tree.A, [tree.FileA])).Id;
        await store.MarkUploadedAsync(context.ProfileId!.Value, web);
        await store.SaveSelectionAsync(null, null, tree.B, [tree.FileB], false);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, context with { RemotePath = "/new" });
        await picker.InitializeAsync();
        await picker.FlushAsync();
        Assert.AreEqual("/new", picker.TargetRemotePath);
        Assert.AreEqual(tree.B, browser.CurrentPath);
        Assert.IsEmpty(picker.CheckedEntries);
        UploadPickerState state = await store.LoadAsync();
        Assert.HasCount(2, state.Mappings);
        Assert.AreEqual(web, state.LastUploadedMappings[context.ProfileId.Value]);
    });

    [TestMethod]
    public Task DeleteActiveMapping_WaitsForSelection_AndStaleWritesCannotRecreateIt() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/app", "one");
        Guid id = (await store.AddMappingAsync(context, tree.A, [tree.FileA])).Id;
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, context);
        await picker.InitializeAsync();
        Assert.IsNotNull(picker.Submit());
        await picker.DeleteMappingAsync(picker.Mappings.Single());
        Assert.AreEqual(tree.A, browser.CurrentPath);
        Assert.IsFalse(picker.HasActiveMapping);
        Assert.IsEmpty(picker.CheckedEntries);
        picker.SetAll(true);
        Assert.IsNull(picker.Submit());
        await browser.RefreshAsync();
        await store.SaveSelectionAsync(context, id, tree.B, [tree.FileB], true);
        await picker.FlushAsync();
        UploadPickerState state = await store.LoadAsync();
        Assert.IsEmpty(state.Mappings);
        Assert.IsEmpty(state.LastUploadedMappings);
        await picker.AddMappingAsync("/web", tree.B);
        Assert.IsTrue(picker.HasActiveMapping);
        Assert.AreEqual("/web", picker.TargetRemotePath);
        Assert.IsEmpty(picker.CheckedEntries);
        await picker.FlushAsync();
    });

    [TestMethod]
    public async Task LegacyMappings_AreMigratedToStableIdsWithoutLosingChecks()
    {
        using var tree = new Tree();
        var disk = new UploadMemoryStore();
        Guid profile = Guid.NewGuid();
        await disk.UpsertAsync("uploadPickerState", "state", new UploadPickerState
        {
            LocalPath = tree.B, CheckedPaths = [tree.FileB],
            Mappings = [new() { ProfileId = profile, RemotePath = "/app", LocalPath = tree.A, CheckedPaths = [tree.FileA] }],
        });
        UploadPickerState first = await new UploadPickerStateStore(disk).LoadAsync();
        UploadPickerState second = await new UploadPickerStateStore(disk).LoadAsync();
        Assert.AreNotEqual(Guid.Empty, first.Mappings.Single().Id);
        Assert.AreEqual(first.Mappings.Single().Id, second.Mappings.Single().Id);
        Assert.AreSequenceEqual([tree.FileA], second.Mappings.Single().CheckedPaths);
        Assert.AreEqual(tree.B, second.LocalPath);
        Assert.IsEmpty(second.LastUploadedMappings);
    }

    [TestMethod]
    public Task DeletingAMappingDuringItsLoad_InvalidatesTheLocalDirectoryRequest() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new TaskCompletionSource<IReadOnlyList<LocalFileSystemEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new ControlledFileSystem(path =>
        {
            if (path == tree.A) { started.TrySetResult(); return slow.Task; }
            return Task.FromResult<IReadOnlyList<LocalFileSystemEntry>>([new("b.txt", tree.FileB, false, 1, DateTime.UtcNow)]);
        });
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/current", "one");
        await store.AddMappingAsync(context, tree.B, [tree.FileB]);
        await store.AddMappingAsync(context with { RemotePath = "/slow" }, tree.A, [tree.FileA]);
        using var browser = new LocalFilePaneViewModel(new(), fs);
        using var picker = new UploadPickerSelection(browser, store, context);
        await picker.InitializeAsync();
        UploadMappingItem item = picker.Mappings.Single(row => row.RemotePath == "/slow");
        Task opening = picker.ApplyMappingAsync(item);
        await started.Task;
        await picker.DeleteMappingAsync(item);
        slow.SetResult([new("a.txt", tree.FileA, false, 1, DateTime.UtcNow)]);
        await opening;
        await picker.FlushAsync();
        Assert.AreEqual(tree.B, browser.CurrentPath);
        Assert.AreEqual("/current", picker.TargetRemotePath);
        Assert.AreSequenceEqual([tree.FileB], picker.CheckedEntries.Select(row => row.FullPath).ToArray());
        Assert.HasCount(1, (await store.LoadAsync()).Mappings);
    });

    [TestMethod]
    public async Task InvalidAdd_DoesNotPoisonLaterPersistenceOperations()
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), " ", "one");
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => { await store.AddMappingAsync(context, tree.A); });
        await store.AddMappingAsync(context with { RemotePath = "/valid" }, tree.A);
        Assert.HasCount(1, (await store.LoadAsync()).Mappings);
    }

    [TestMethod]
    public Task MappingOrderAndItemIdentity_SurviveSelectionSavingAndReopening() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var disk = new UploadMemoryStore();
        var store = new UploadPickerStateStore(disk);
        var context = new UploadPickerContext(Guid.NewGuid(), "/z", "one");
        await store.AddMappingAsync(context, tree.A, [tree.FileA]);
        await store.AddMappingAsync(context with { RemotePath = "/a" }, tree.B, [tree.FileB]);
        await store.AddMappingAsync(context, tree.B, [tree.FileB]);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, context);
        await picker.InitializeAsync();
        await picker.FlushAsync();
        UploadMappingItem[] rows = [.. picker.Mappings];
        Guid[] ids = [.. rows.Select(item => item.Mapping.Id)];
        int collectionChanges = 0;
        picker.Mappings.CollectionChanged += (_, _) => collectionChanges++;
        await picker.ApplyMappingAsync(rows[1]);
        picker.SetAll(false);
        await browser.RefreshAsync();
        await picker.FlushAsync();
        Assert.AreEqual(0, collectionChanges, "切换、勾选和刷新不能重建或移动映射项。");
        for (int i = 0; i < rows.Length; i++) { Assert.AreSame(rows[i], picker.Mappings[i]); }
        Assert.AreSequenceEqual(ids, picker.Mappings.Select(item => item.Mapping.Id).ToArray());
        Assert.IsTrue(rows[1].IsCurrent);
        Assert.IsFalse(rows[0].IsCurrent);
        Assert.IsFalse(rows[2].IsCurrent);

        await picker.AddMappingAsync("/0", tree.A);
        Assert.AreEqual("/0", picker.Mappings[^1].RemotePath, "新增项追加末尾,不能按路径排序。");
        await picker.DeleteMappingAsync(rows[1]);
        await picker.FlushAsync();
        Guid[] remaining = [.. picker.Mappings.Select(item => item.Mapping.Id)];
        Assert.AreSequenceEqual(new[] { ids[0], ids[2] }, remaining[..2]);
        using LocalFilePaneViewModel otherBrowser = UnwatchedLocalFileSystem.NewPane();
        using var reopened = new UploadPickerSelection(otherBrowser, new(disk), context);
        await reopened.InitializeAsync();
        await reopened.FlushAsync();
        Assert.AreSequenceEqual(remaining, reopened.Mappings.Select(item => item.Mapping.Id).ToArray());
    });

    [TestMethod]
    public Task RemoteFailure_DiscardsPreparedLocalContentsAndKeepsPreviousMapping() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/old", "one");
        await store.AddMappingAsync(context, tree.A, [tree.FileA]);
        await store.AddMappingAsync(context with { RemotePath = "/denied" }, tree.B, [tree.FileB]);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, context);
        await picker.InitializeAsync();
        await picker.FlushAsync();
        Guid? old = picker.ActiveMappingId;
        LocalFileEntry[] entries = [.. browser.Entries];
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task switching = picker.ApplyMappingAsync(picker.Mappings[1], (path, _) =>
        {
            Assert.AreEqual("/denied", path);
            started.SetResult();
            return release.Task;
        });
        await started.Task;
        Assert.AreEqual(tree.A, browser.CurrentPath, "远端成功以前不显示暂存的本地目录。");
        Assert.AreEqual(old, picker.ActiveMappingId);
        release.SetResult(false);
        await switching;
        await picker.FlushAsync();
        Assert.AreEqual(tree.A, browser.CurrentPath);
        Assert.AreEqual(old, picker.ActiveMappingId);
        Assert.AreEqual("/old", picker.TargetRemotePath);
        Assert.AreSequenceEqual([tree.FileA], picker.CheckedEntries.Select(item => item.FullPath).ToArray());
        Assert.IsTrue(entries.SequenceEqual(browser.Entries), "失败时保留原列表项。");
        Assert.IsNotNull(picker.ErrorMessage);
        Assert.IsTrue(picker.Mappings[0].IsCurrent);
        Assert.IsFalse(picker.Mappings[1].IsCurrent);
        Assert.IsEmpty((await store.LoadAsync()).LastUploadedMappings);
    });

    [TestMethod]
    public Task MissingLocalDirectory_DoesNotInvokeRemoteNavigation() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/old", "one");
        await store.AddMappingAsync(context, tree.A, [tree.FileA]);
        await store.AddMappingAsync(context with { RemotePath = "/missing" }, Path.Combine(tree.B, "missing"));
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, context);
        await picker.InitializeAsync();
        int calls = 0;
        await picker.ApplyMappingAsync(picker.Mappings[1], (_, _) => { calls++; return Task.FromResult(true); });
        Assert.AreEqual(0, calls);
        Assert.AreEqual(tree.A, browser.CurrentPath);
        Assert.AreEqual("/old", picker.TargetRemotePath);
        Assert.IsNotNull(picker.ErrorMessage);
        await picker.FlushAsync();
    });

    [TestMethod]
    public Task NewerMapping_CancelsDelayedRemoteAndKeepsItsOwnChecks() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/old", "one");
        await store.AddMappingAsync(context, tree.A, [tree.FileA]);
        await store.AddMappingAsync(context with { RemotePath = "/slow" }, tree.B, [tree.FileB]);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, context);
        await picker.InitializeAsync();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task slow = picker.ApplyMappingAsync(picker.Mappings[1], (_, ct) => { started.SetResult(ct); return release.Task; });
        CancellationToken oldToken = await started.Task;
        await picker.ApplyMappingAsync(picker.Mappings[0], (_, _) => Task.FromResult(true));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        release.SetResult(true); // 模拟忽略取消的服务晚到成功。
        await slow;
        await picker.FlushAsync();
        Assert.AreEqual("/old", picker.TargetRemotePath);
        Assert.AreEqual(tree.A, browser.CurrentPath);
        Assert.AreSequenceEqual([tree.FileA], picker.CheckedEntries.Select(item => item.FullPath).ToArray());
        Assert.IsNull(picker.ErrorMessage);
    });

    [TestMethod]
    public Task Closing_CancelsNavigationWithoutWaitingForAnIgnoringBackend() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var disk = new UploadMemoryStore();
        var store = new UploadPickerStateStore(disk);
        var context = new UploadPickerContext(Guid.NewGuid(), "/old", "one");
        await store.AddMappingAsync(context, tree.A, [tree.FileA]);
        await store.AddMappingAsync(context with { RemotePath = "/slow" }, tree.B);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        using var picker = new UploadPickerSelection(browser, store, context);
        await picker.InitializeAsync();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task slow = picker.ApplyMappingAsync(picker.Mappings[1], (_, ct) => { started.SetResult(ct); return release.Task; });
        CancellationToken token = await started.Task;
        await picker.PrepareCloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsFalse(slow.IsCompleted);
        picker.Dispose();
        release.SetResult(true);
        await slow;
        Assert.AreEqual(tree.A, browser.CurrentPath);
        Assert.AreEqual("/old", picker.TargetRemotePath);
        UploadPickerState persisted = await new UploadPickerStateStore(disk).LoadAsync();
        Assert.AreSequenceEqual([tree.FileA], persisted.CheckedPaths);
    });

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
