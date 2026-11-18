using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Localization;
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
    public Task SidebarButtons_RestoreChecksWithoutChangingTarget_AndRenderBothThemes() => RunAsync(async (_, _, root) =>
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
            Assert.AreSequenceEqual([file], dialog.Picker.CheckedEntries.Select(entry => entry.FullPath).ToArray());
            StringAssert.Contains(dialog.UploadTargetText.Text!, "/app");
            await dialog.Picker.FlushAsync();
            Assert.AreEqual("/app", dialog.Picker.Mappings[0].RemotePath);
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
        disk.BeforeWrite = async () => { started.TrySetResult(); await release.Task; };
        using LocalFilePaneViewModel otherBrowser = UnwatchedLocalFileSystem.NewPane();
        await otherBrowser.NavigateToAsync(root);
        var other = new LocalPathPickerDialog(otherBrowser, false, new(disk), new(Guid.NewGuid(), "/app", "server"));
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
