using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Localization;
using VelaShell.Core.Resources;
using VelaShell.Localization;
using VelaShell.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
public sealed class UploadPickerCheckboxUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(UploadPickerCheckboxUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public Task Checkboxes_AreIndependentOfHighlight_AndHeaderSelectsUnrealizedRows() => RunAsync(async (dialog, browser, _) =>
    {
        LocalFileEntry file = browser.Entries.First(entry => !entry.IsDirectory);
        browser.SelectedEntries.Add(file);
        Pump(dialog);
        Assert.IsFalse(dialog.ConfirmButton.IsEnabled);
        Click(dialog, EntryCheck(dialog, file));
        Assert.IsTrue(dialog.ConfirmButton.IsEnabled);
        Assert.IsTrue(dialog.Picker.IsChecked(file));
        Assert.IsNull(dialog.SelectAllCheck.IsChecked);
        Assert.IsEmpty(browser.SelectedEntries.Where(entry => entry.IsParentEntry));

        Click(dialog, dialog.SelectAllCheck);
        Assert.IsTrue(dialog.SelectAllCheck.IsChecked, "部分勾选时点击应全选。");
        Assert.AreEqual(browser.Entries.Count - 1, dialog.Picker.CheckedEntries.Count);
        Assert.IsNull(dialog.FileList.ContainerFromIndex(browser.Entries.Count - 1), "末行不在视口,全选仍需包含它。");
        dialog.View = LocalPickerView.Icons;
        Pump(dialog);
        Assert.IsTrue(dialog.IconSelectAllCheck.IsChecked);
        CheckBox iconCheck = EntryCheck(dialog, file);
        Assert.IsTrue(iconCheck.IsChecked, "新建的虚拟化格子从独立勾选集恢复。");
        Click(dialog, iconCheck);
        Assert.IsFalse(dialog.Picker.IsChecked(file));
        Assert.IsNull(dialog.IconSelectAllCheck.IsChecked);
        Click(dialog, dialog.IconSelectAllCheck);
        Click(dialog, dialog.IconSelectAllCheck);
        Assert.IsEmpty(dialog.Picker.CheckedEntries);
        Assert.IsFalse(dialog.ConfirmButton.IsEnabled);
        await Task.CompletedTask;
    });

    [TestMethod]
    public Task DirectoryButton_CancelAndSameDirectoryPreserveChecks_NewDirectoryClearsThem() => RunAsync(async (dialog, browser, root) =>
    {
        dialog.Picker.SetAll(true);
        int count = dialog.Picker.CheckedEntries.Count;
        dialog.PickDirectory = () => Task.FromResult<string?>(null);
        Click(dialog, dialog.ChooseDirectoryButton);
        Assert.AreEqual(root, browser.CurrentPath);
        Assert.AreEqual(count, dialog.Picker.CheckedEntries.Count);

        dialog.PickDirectory = () => Task.FromResult<string?>(root);
        Click(dialog, dialog.ChooseDirectoryButton);
        await WaitForAsync(dialog, () => browser.IsDirectoryLoading);
        Assert.AreEqual(count, dialog.Picker.CheckedEntries.Count);
        dialog.PickDirectory = () => Task.FromResult<string?>(Path.Combine(root, "sub"));
        Click(dialog, dialog.ChooseDirectoryButton);
        await WaitForAsync(dialog, () => browser.IsDirectoryLoading);
        Assert.AreEqual(Path.Combine(root, "sub"), browser.CurrentPath);
        Assert.IsEmpty(dialog.Picker.CheckedEntries);
        Assert.IsFalse(dialog.SelectAllCheck.IsEnabled, "空目录只有上一级,没有可勾选项。");
    });

    [TestMethod]
    public Task DoubleClickFile_TogglesChecks_AndCheckboxDoubleClickDoesNotUploadOrNavigate() => RunAsync(async (dialog, browser, _) =>
    {
        LocalFileEntry file = browser.Entries.First(entry => !entry.IsDirectory);
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        var row = (Control)dialog.FileList.ContainerFromItem(file)!;
        Point at = row.TranslatePoint(new(100, row.Bounds.Height / 2), dialog)!.Value;
        ClickAt(dialog, at);
        ClickAt(dialog, at);
        Assert.IsTrue(dialog.Picker.IsChecked(file));
        Assert.IsFalse(closed);

        LocalFileEntry folder = browser.Entries.Single(entry => entry.Name == "sub");
        CheckBox check = EntryCheck(dialog, folder);
        Click(dialog, check);
        Click(dialog, check);
        Assert.IsFalse(dialog.Picker.IsChecked(folder));
        Assert.AreNotEqual(folder.FullPath, browser.CurrentPath);
        Assert.IsFalse(closed);
        await Task.CompletedTask;
    });

    [TestMethod]
    public Task SidebarButtons_RestoreChecksAndChangeTarget_AndRenderBothThemes() => RunAsync(async (_, _, root) =>
    {
        string local = Path.Combine(root, "sub");
        string file = Path.Combine(local, "index.html");
        await File.WriteAllTextAsync(file, "page");
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/app", "deployment");
        await store.SaveAsync(context with { RemotePath = "/web" }, local, [file], true);
        await store.SaveAsync(context, root, [Path.Combine(root, "file-000.txt")], true);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        var dialog = new LocalPathPickerDialog(browser, false, store, context);
        try
        {
            await dialog.Picker.InitializeAsync();
            dialog.Show();
            Pump(dialog);
            SaveFrame(dialog, "upload-picker-list-dark.png");
            dialog.RequestedThemeVariant = ThemeVariant.Light;
            Pump(dialog);
            SaveFrame(dialog, "upload-picker-list-light.png");
            Button mapping = dialog.MappingList.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is UploadMappingItem { RemotePath: "/web" });
            Click(dialog, mapping);
            await WaitForAsync(dialog, () => dialog.Picker.IsRestoring);
            Assert.AreEqual(local, browser.CurrentPath);
            Assert.AreSequenceEqual([file], [.. dialog.Picker.CheckedEntries.Select(entry => entry.FullPath)]);
            StringAssert.Contains(dialog.UploadTargetText.Text!, "/web");
            await dialog.Picker.FlushAsync();
            Assert.AreEqual("/web", dialog.Picker.Mappings[0].RemotePath);
            dialog.View = LocalPickerView.Icons;
            Pump(dialog);
            SaveFrame(dialog, "upload-picker-icons-light.png");
        }
        finally
        {
            await dialog.Picker.FlushAsync();
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [TestMethod]
    public Task ClosingWaitsForPendingPersistence_AndReopeningRestoresChecks() => RunAsync(async (dialog, browser, root) =>
    {
        var disk = new UploadMemoryStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using LocalFilePaneViewModel otherBrowser = UnwatchedLocalFileSystem.NewPane();
        await new UploadPickerStateStore(disk).SaveAsync(null, root, [], false);
        var other = new LocalPathPickerDialog(otherBrowser, false, new(disk), new(Guid.NewGuid(), "/app", "server"));
        await other.Picker.InitializeAsync();
        await other.Picker.FlushAsync();
        disk.BeforeWrite = async () => { started.TrySetResult(); await release.Task; };
        try
        {
            other.Show();
            Pump(other);
            other.Picker.SetChecked(otherBrowser.Entries.First(entry => !entry.IsDirectory), true);
            await started.Task;
            bool closed = false;
            other.Closed += (_, _) => closed = true;
            other.Close();
            Pump(other);
            Assert.IsFalse(closed);
            release.SetResult();
            await other.Picker.FlushAsync();
            await WaitForAsync(other, () => !closed);
            Assert.IsTrue(closed);
            UploadPickerState state = await new UploadPickerStateStore(disk).LoadAsync();
            Assert.HasCount(1, state.CheckedPaths);
        }
        finally
        {
            release.TrySetResult();
            other.Close();
        }
    });

    [TestMethod]
    public Task SidebarRightClick_AddCancelAndDelete_DoNotActivateTheClickedMapping() => RunAsync(async (_, _, root) =>
    {
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/app", "server");
        await store.SaveAsync(context, root, [Path.Combine(root, "file-000.txt")], true);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        var dialog = new LocalPathPickerDialog(browser, false, store, context);
        try
        {
            await dialog.Picker.InitializeAsync();
            dialog.Show();
            Pump(dialog);
            Guid? original = dialog.Picker.ActiveMappingId;
            int bindCalls = 0;
            dialog.BindMapping = (remote, local) =>
            {
                bindCalls++;
                Assert.AreEqual("/app", remote);
                Assert.AreEqual(root, local);
                return Task.FromResult<UploadMappingBinding?>(null);
            };
            MenuItem add = RightClickMenu(dialog, dialog.MappingArea, new(100, dialog.MappingArea.Bounds.Height - 20));
            Assert.AreEqual(Strings.Get("Sftp_UploadMappingAdd"), add.Header);
            add.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            dialog.MappingContextMenu!.Close();
            Pump(dialog);
            Assert.AreEqual(1, bindCalls);
            Assert.HasCount(1, dialog.Picker.Mappings, "取消绑定不保存草稿。");
            Assert.AreEqual(original, dialog.Picker.ActiveMappingId);

            dialog.BindMapping = (_, _) => Task.FromResult<UploadMappingBinding?>(new("/web", Path.Combine(root, "sub")));
            add = RightClickMenu(dialog, dialog.MappingArea, new(100, dialog.MappingArea.Bounds.Height - 20));
            add.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            dialog.MappingContextMenu!.Close();
            await dialog.Picker.FlushAsync();
            await WaitForAsync(dialog, () => dialog.Picker.IsRestoring);
            Pump(dialog);
            Assert.HasCount(2, dialog.Picker.Mappings);
            Assert.AreEqual("/web", dialog.Picker.TargetRemotePath);

            Button old = dialog.MappingList.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is UploadMappingItem { RemotePath: "/app" });
            MenuItem delete = RightClickMenu(dialog, old, new(50, old.Bounds.Height / 2));
            Assert.AreEqual(Strings.Get("Sftp_UploadMappingDelete"), delete.Header);
            Assert.AreEqual("/web", dialog.Picker.TargetRemotePath, "右键不能激活所指记录。");
            delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            dialog.MappingContextMenu!.Close();
            await dialog.Picker.FlushAsync();
            Pump(dialog);
            Assert.HasCount(1, dialog.Picker.Mappings);

            Button current = dialog.MappingList.GetVisualDescendants().OfType<Button>().Single();
            delete = RightClickMenu(dialog, current, new(50, current.Bounds.Height / 2));
            delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            dialog.MappingContextMenu!.Close();
            await dialog.Picker.FlushAsync();
            Pump(dialog);
            Assert.IsFalse(dialog.ConfirmButton.IsEnabled, "删除后勾选清空,没选东西时不给按。");
            Assert.IsNull(dialog.Picker.ActiveMappingId);
            Assert.AreEqual(Path.Combine(root, "sub"), browser.CurrentPath);
            File.WriteAllText(Path.Combine(root, "sub", "x.txt"), "x");
            await browser.RefreshAsync();
            await dialog.Picker.FlushAsync();
            Pump(dialog);
            Assert.IsTrue(dialog.SelectAllCheck.IsEnabled, "删除当前映射后仍可勾选上传。");
            dialog.Picker.SetAll(true);
            Pump(dialog);
            Assert.IsTrue(dialog.ConfirmButton.IsEnabled);
            StringAssert.Contains(dialog.UploadTargetText.Text, "/web");
        }
        finally
        {
            await dialog.Picker.FlushAsync();
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [TestMethod]
    [DataRow(0)]
    [DataRow(9)]
    [DataRow(19)]
    public Task OpeningScrollsToRestoredMapping_WithoutReorderingOrLaterScrollJumps(int selectedIndex) => RunAsync(async (_, _, root) =>
    {
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        // 最近上传只在入口远端目录的绑定之间恢复,所以 20 条都挂在同一远端目录下、各自绑定不同的本地目录。
        var context = new UploadPickerContext(Guid.NewGuid(), "/record", "server");
        var ids = new List<Guid>();
        for (int i = 0; i < 20; i++)
        {
            string local = Path.Combine(root, $"m-{i:00}");
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "f.txt"), "x");
            ids.Add((await store.AddMappingAsync(context, local)).Id);
        }
        await store.MarkUploadedAsync(context.ProfileId!.Value, ids[selectedIndex]);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        var dialog = new LocalPathPickerDialog(browser, false, store, context);
        try
        {
            await dialog.Picker.InitializeAsync();
            dialog.Show();
            Pump(dialog);
            UploadMappingItem restored = dialog.Picker.Mappings.Single(row => row.Mapping.Id == ids[selectedIndex]);
            Control? container = dialog.MappingList.ContainerFromItem(restored);
            Assert.IsNotNull(container);
            double y = container.TranslatePoint(new(0, 0), dialog.MappingScroll)!.Value.Y;
            Assert.IsGreaterThanOrEqualTo(-1d, y, "恢复项应在视口内。");
            Assert.IsLessThanOrEqualTo(dialog.MappingScroll.Viewport.Height + 1,
                y + container.Bounds.Height, "恢复项的底边应在视口内。");
            Assert.IsGreaterThan(dialog.MappingScroll.Viewport.Height, dialog.MappingScroll.Extent.Height);
            if (selectedIndex == 0) { Assert.AreEqual(0d, dialog.MappingScroll.Offset.Y, 1d); }
            Assert.AreSequenceEqual([.. ids], [.. dialog.Picker.Mappings.Select(row => row.Mapping.Id)]);
            dialog.MappingScroll.Offset = new(0, selectedIndex > 0 ? 0
                : dialog.MappingScroll.Extent.Height - dialog.MappingScroll.Viewport.Height);
            Pump(dialog);
            double manual = dialog.MappingScroll.Offset.Y;
            dialog.Picker.SetChecked(browser.Entries.First(entry => !entry.IsDirectory), true);
            await dialog.Picker.FlushAsync();
            Pump(dialog);
            Assert.AreEqual(manual, dialog.MappingScroll.Offset.Y, 1d, "保存不应把用户滚动位置拉回。");
            Assert.AreSame(container, dialog.MappingList.ContainerFromItem(restored), "仅更新标记不能重建条目控件。");
            await dialog.Picker.AddMappingAsync("/appended", root);
            await dialog.Picker.FlushAsync();
            Pump(dialog);
            Assert.AreEqual(manual, dialog.MappingScroll.Offset.Y, 1d, "新建后选中不再次执行打开定位。");
            Assert.AreEqual("/appended", dialog.Picker.Mappings[^1].RemotePath);
        }
        finally { await dialog.Picker.PrepareCloseAsync(); dialog.Close(); Dispatcher.UIThread.RunJobs(); }
    });

    [TestMethod]
    public Task OpeningWithoutAnActiveMapping_DoesNotScrollOrFocusTheSidebar() => RunAsync(async (_, _, root) =>
    {
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/entry", "server");
        await store.AddMappingAsync(context, root);
        for (int i = 0; i < 16; i++) { await store.AddMappingAsync(context with { RemotePath = "/other-" + i }, root); }
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        var dialog = new LocalPathPickerDialog(browser, false, store, context);
        try
        {
            await dialog.Picker.InitializeAsync();
            await dialog.Picker.DeleteMappingAsync(dialog.Picker.Mappings[0]);
            dialog.Show();
            Pump(dialog);
            Assert.IsNull(dialog.Picker.ActiveMappingId);
            Assert.AreEqual(0d, dialog.MappingScroll.Offset.Y, 1d);
            Assert.IsFalse(dialog.MappingList.IsKeyboardFocusWithin);
        }
        finally { await dialog.Picker.PrepareCloseAsync(); dialog.Close(); Dispatcher.UIThread.RunJobs(); }
    });

    [TestMethod]
    public Task LeftClickSynchronizesRealRemotePane_WhileRestoreAddAndRightClickDoNot() => RunAsync(async (_, _, root) =>
    {
        string local = Path.Combine(root, "sub");
        string file = Path.Combine(local, "local.txt");
        await File.WriteAllTextAsync(file, "x");
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/app", "server");
        await store.AddMappingAsync(context, root, [Path.Combine(root, "file-000.txt")]);
        await store.AddMappingAsync(context with { RemotePath = "/web" }, local, [file]);
        ISftpService service = Substitute.For<ISftpService>();
        var session = Guid.NewGuid();
        service.ListDirectoryAsync(session, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult(new List<RemoteFileInfo> { new()
            {
                Name = "remote.txt", FullPath = (string)call[1] + "/remote.txt", IsDirectory = false,
                Size = 1, Permissions = "", Owner = "", Group = "", LastModified = DateTime.UtcNow,
            } }));
        var remote = new FileBrowserViewModel(service, session);
        await remote.NavigateToAsync("/app");
        service.ClearReceivedCalls();
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        var dialog = new LocalPathPickerDialog(browser, false, store, context)
        {
            NavigateRemoteForMapping = remote.NavigateForUploadMappingAsync,
        };
        try
        {
            await dialog.Picker.InitializeAsync();
            dialog.Show();
            Pump(dialog);
            await service.DidNotReceiveWithAnyArgs().ListDirectoryAsync(default, default!, default);
            Button web = dialog.MappingList.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is UploadMappingItem { RemotePath: "/web" });
            Click(dialog, web);
            await WaitForAsync(dialog, () => dialog.Picker.IsRestoring);
            Assert.AreEqual("/web", remote.CurrentPath);
            Assert.AreEqual("/web/remote.txt", remote.Files.Single(entry => !entry.IsParentEntry).FullPath);
            Assert.AreEqual(local, browser.CurrentPath);
            Assert.AreSame(web, dialog.MappingList.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is UploadMappingItem { RemotePath: "/web" }));
            Assert.AreSequenceEqual(["/app", "/web"], [.. dialog.Picker.Mappings.Select(row => row.RemotePath)]);
            Click(dialog, web);
            await WaitForAsync(dialog, () => dialog.Picker.IsRestoring);
            await service.Received(2).ListDirectoryAsync(session, "/web", Arg.Any<CancellationToken>());

            service.ListDirectoryAsync(session, "/app", Arg.Any<CancellationToken>())
                .Returns(Task.FromException<List<RemoteFileInfo>>(new UnauthorizedAccessException("denied")));
            Button app = dialog.MappingList.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is UploadMappingItem { RemotePath: "/app" });
            Click(dialog, app);
            await WaitForAsync(dialog, () => dialog.Picker.IsRestoring);
            Assert.AreEqual("/web", remote.CurrentPath);
            Assert.AreEqual(local, browser.CurrentPath);
            Assert.AreEqual("/web", dialog.Picker.TargetRemotePath);
            Assert.AreSequenceEqual([file], [.. dialog.Picker.CheckedEntries.Select(entry => entry.FullPath)]);
            Assert.IsTrue(dialog.Picker.Mappings[1].IsCurrent);
            Assert.IsTrue(dialog.PickerErrorBanner.IsVisible);

            _ = RightClickMenu(dialog, app, new(40, app.Bounds.Height / 2));
            dialog.MappingContextMenu!.Close();
            await service.Received(1).ListDirectoryAsync(session, "/app", Arg.Any<CancellationToken>());
            await dialog.Picker.AddMappingAsync("/added", root);
            Assert.AreEqual("/web", remote.CurrentPath, "新建后选中不联动主远端面板。");
            Assert.AreEqual("/added", dialog.Picker.TargetRemotePath);
            await service.DidNotReceive().ListDirectoryAsync(session, "/added", Arg.Any<CancellationToken>());
        }
        finally { await dialog.Picker.PrepareCloseAsync(); dialog.Close(); Dispatcher.UIThread.RunJobs(); }
        Assert.AreEqual("/web", remote.CurrentPath, "取消上传不撤销已经完成的远端浏览。");
    });

    [TestMethod]
    public Task ClosingDuringRemoteNavigation_DiscardsLateUiResults() => RunAsync(async (_, _, root) =>
    {
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/app", "server");
        await store.AddMappingAsync(context, root, [Path.Combine(root, "file-000.txt")]);
        await store.AddMappingAsync(context with { RemotePath = "/web" }, Path.Combine(root, "sub"));
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new LocalPathPickerDialog(browser, false, store, context)
        {
            NavigateRemoteForMapping = (_, ct) => { started.TrySetResult(ct); return release.Task; },
        };
        try
        {
            await dialog.Picker.InitializeAsync();
            dialog.Show();
            Pump(dialog);
            Click(dialog, dialog.MappingList.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is UploadMappingItem { RemotePath: "/web" }));
            CancellationToken token = await started.Task;
            dialog.Close();
            Pump(dialog);
            Assert.IsFalse(dialog.IsVisible, "关闭不能等待一个忽略取消的目录请求。");
            Assert.IsTrue(token.IsCancellationRequested);
            release.TrySetResult(true);
            await dialog.Picker.FlushAsync();
            Assert.AreEqual(root, browser.CurrentPath);
            Assert.AreEqual("/app", dialog.Picker.TargetRemotePath);
        }
        finally
        {
            release.TrySetResult(true);
            await dialog.Picker.FlushAsync();
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [TestMethod]
    public Task RapidLeftClicks_KeepMappingsClickableAndOnlyApplyTheNewestSelection() => RunAsync(async (_, _, root) =>
    {
        var store = new UploadPickerStateStore(new UploadMemoryStore());
        var context = new UploadPickerContext(Guid.NewGuid(), "/app", "server");
        string checkedFile = Path.Combine(root, "file-000.txt");
        await store.AddMappingAsync(context, root, [checkedFile]);
        await store.AddMappingAsync(context with { RemotePath = "/slow" }, Path.Combine(root, "sub"));
        await store.AddMappingAsync(context with { RemotePath = "/new" }, root, [checkedFile]);
        using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new LocalPathPickerDialog(browser, false, store, context)
        {
            NavigateRemoteForMapping = async (path, ct) =>
            {
                if (path != "/slow") { return true; }
                started.SetResult(ct);
                bool result = await release.Task;
                finished.SetResult();
                return result;
            },
        };
        try
        {
            await dialog.Picker.InitializeAsync();
            dialog.Show();
            Pump(dialog);
            Button Row(string remote) => dialog.MappingList.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is UploadMappingItem row && row.RemotePath == remote);
            Click(dialog, Row("/slow"));
            CancellationToken oldToken = await started.Task;
            Assert.IsTrue(dialog.MappingList.IsEnabled);
            Click(dialog, Row("/new"));
            await WaitForAsync(dialog, () => dialog.Picker.IsRestoring);
            Assert.IsTrue(oldToken.IsCancellationRequested);
            release.SetResult(true);
            await finished.Task;
            Pump(dialog);
            Assert.AreEqual("/new", dialog.Picker.TargetRemotePath);
            Assert.AreEqual(root, browser.CurrentPath);
            Assert.AreSequenceEqual([checkedFile], [.. dialog.Picker.CheckedEntries.Select(entry => entry.FullPath)]);
            Assert.AreSequenceEqual(["/app", "/slow", "/new"], [.. dialog.Picker.Mappings.Select(row => row.RemotePath)]);
            Assert.IsTrue(dialog.Picker.Mappings[2].IsCurrent);
        }
        finally
        {
            release.TrySetResult(true);
            await dialog.Picker.PrepareCloseAsync();
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    private static MenuItem RightClickMenu(LocalPathPickerDialog dialog, Control control, Point local)
    {
        Point point = control.TranslatePoint(local, dialog)!.Value;
        dialog.MouseDown(point, MouseButton.Right);
        dialog.MouseUp(point, MouseButton.Right);
        Pump(dialog);
        Assert.IsNotNull(dialog.MappingContextMenu);
        Assert.IsTrue(dialog.MappingContextMenu.IsOpen);
        return dialog.MappingContextMenu.Items.OfType<MenuItem>().Single();
    }

    private static Task RunAsync(Func<LocalPathPickerDialog, LocalFilePaneViewModel, string, Task> body) =>
        _session.RunOnUiAsync(async () =>
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vela-upload-ui-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            for (int i = 0; i < 80; i++)
            {
                File.WriteAllText(Path.Combine(root, $"file-{i:000}.txt"), "x");
            }
            using LocalFilePaneViewModel browser = UnwatchedLocalFileSystem.NewPane();
            await browser.NavigateToAsync(root);
            var dialog = new LocalPathPickerDialog(browser, false);
            try
            {
                dialog.Show();
                Pump(dialog);
                await body(dialog, browser, root);
            }
            finally
            {
                await dialog.Picker.FlushAsync();
                dialog.Close();
                Dispatcher.UIThread.RunJobs();
                Directory.Delete(root, true);
            }
        });

    private static CheckBox EntryCheck(LocalPathPickerDialog dialog, LocalFileEntry entry) =>
        dialog.FileList.GetVisualDescendants().OfType<CheckBox>().Single(check =>
            check.DataContext is LocalFileEntry item && item.FullPath == entry.FullPath);

    private static void Click(Window window, Control control) => ClickAt(window,
        control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value);

    private static void ClickAt(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump(window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveFrame(Window window, string fileName)
    {
        if (Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR") is not { Length: > 0 } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.IsNotNull(frame);
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static async Task WaitForAsync(Window window, Func<bool> waiting)
    {
        for (int i = 0; i < 500 && waiting(); i++)
        {
            await Task.Delay(10);
            Pump(window);
        }
        Assert.IsFalse(waiting(), "异步窗口操作没有及时结束。");
    }
}
