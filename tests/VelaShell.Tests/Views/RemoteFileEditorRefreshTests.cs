using System.Diagnostics;
using System.Globalization;
using System.Resources;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using VelaShell.Core.Resources;
using VelaShell.Tests.TestSupport;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("EditorUI")]
public sealed class RemoteFileEditorRefreshTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteFileEditorRefreshTests).Assembly);

    [TestMethod]
    public Task DefaultsAndImmediateRefresh_OverwriteUnsavedEditsAndClearUndo() => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("initial");
        Assert.AreNotEqual(true, f.Auto.IsChecked);
        Assert.AreNotEqual(true, f.Keep.IsChecked);
        Assert.AreEqual(1000m, f.Interval.Value);
        Assert.IsTrue(f.Auto.IsEnabled);
        f.Editor.Text = "unsaved";
        f.Auto.IsChecked = true;
        Assert.AreEqual(1, f.Remote.Count, "启用必须立即读取,不等第一个间隔。");
        Assert.IsFalse(f.Editor.IsReadOnly);
        f.Editor.Text = "typed during download";
        Task refresh = f.View.RefreshForTestAsync();
        f.Remote.Complete("remote");
        await refresh;
        Assert.AreEqual("remote", f.Editor.Text);
        Assert.AreEqual("remote", await File.ReadAllTextAsync(f.LocalPath));
        Assert.IsFalse(f.View.IsDirtyForTest);
        Assert.IsFalse(f.Editor.CanUndo);
        Assert.AreEqual(f.Editor.Document.TextLength, f.Editor.CaretOffset);
    });

    [TestMethod]
    public Task SmallEditsUseOneDocumentChange_AndIdenticalContentSkipsChanges() => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("prefix:old:suffix");
        var changes = new List<(int Removed, int Added)>();
        f.Editor.Document.Changed += (_, e) => changes.Add((e.RemovalLength, e.InsertionLength));
        object document = f.Editor.Document;
        await f.RefreshAsync("prefix:new:suffix");
        Assert.AreSame(document, f.Editor.Document);
        CollectionAssert.AreEqual(new[] { (3, 3) }, changes);
        await f.RefreshAsync("prefix:new:suffix");
        Assert.HasCount(1, changes);
        await f.RefreshAsync("prefix:new:suffix+tail");
        Assert.AreEqual((0, 5), changes.Last());
        await f.RefreshAsync("");
        Assert.AreEqual("", f.Editor.Text);
        Assert.AreEqual(0, f.Editor.CaretOffset);
    });

    [TestMethod]
    public Task KeepPosition_PreservesBothScrollAxesCaretAndSelection_AndClampsTruncation() => _session.RunOnUiAsync(async () =>
    {
        string content = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"{i}:" + new string('x', 400)));
        await using var f = await Fixture.OpenAsync(content);
        f.Keep.IsChecked = true;
        f.Editor.Select(500, 23);
        f.Editor.CaretOffset = 500; // 反向选区的光标在起点。
        f.Editor.UpdateLayout();
        f.Scroll.Offset = new Vector(120, 800);
        Vector offset = f.Scroll.Offset;
        Assert.IsTrue(offset.X > 0 && offset.Y > 0, "前置:必须实际滚动,避免零偏移假通过。");
        await f.RefreshAsync(content + "\nappended");
        await SettleAsync();
        Assert.AreEqual(offset.X, f.Scroll.Offset.X, 1);
        Assert.AreEqual(offset.Y, f.Scroll.Offset.Y, 1);
        Assert.AreEqual(500, f.Editor.CaretOffset);
        Assert.AreEqual(500, f.Editor.SelectionStart);
        Assert.AreEqual(23, f.Editor.SelectionLength);
        await f.RefreshAsync("short");
        await SettleAsync();
        Assert.AreEqual(5, f.Editor.CaretOffset);
        Assert.AreEqual(0, f.Editor.SelectionLength);
        Assert.AreEqual(0, f.Scroll.Offset.Y, 1);
        Assert.AreEqual(0, f.Scroll.Offset.X, 1);
    });

    [TestMethod]
    public Task FollowEnd_ScrollsToCaret_EvenWhenRemoteContentIsUnchanged() => _session.RunOnUiAsync(async () =>
    {
        string content = string.Join('\n', Enumerable.Range(0, 300).Select(i => "line " + i));
        await using var f = await Fixture.OpenAsync(content);
        await f.RefreshAsync(content + "\nlast line");
        await SettleAsync();
        Assert.AreEqual(f.Editor.Document.TextLength, f.Editor.CaretOffset);
        Assert.IsTrue(f.Scroll.Offset.Y > 0);
        f.Editor.CaretOffset = 0;
        f.Scroll.Offset = default;
        await f.RefreshAsync(content + "\nlast line");
        await SettleAsync();
        Assert.AreEqual(f.Editor.Document.TextLength, f.Editor.CaretOffset);
        Assert.IsTrue(f.Scroll.Offset.Y > 0);
    });

    [TestMethod]
    public Task ToggleOffAndOn_DiscardsLateResponseAndNeverOverlapsDownloads() => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("initial");
        f.Auto.IsChecked = true;
        Task old = f.View.RefreshForTestAsync();
        CancellationToken token = f.Remote.CurrentToken;
        f.Auto.IsChecked = false;
        Assert.IsTrue(token.IsCancellationRequested);
        f.Auto.IsChecked = true;
        Assert.AreEqual(1, f.Remote.Count);
        f.Remote.Complete("obsolete"); // 后端刻意不响应取消。
        await old;
        await UntilAsync(() => f.Remote.Count == 2);
        Assert.AreEqual("initial", f.Editor.Text);
        Assert.AreEqual("initial", await File.ReadAllTextAsync(f.LocalPath));
        Task current = f.View.RefreshForTestAsync();
        f.Remote.Complete("latest");
        await current;
        Assert.AreEqual("latest", f.Editor.Text);
        Assert.AreEqual(1, f.Remote.MaxConcurrent);
        Assert.HasCount(1, Directory.GetFiles(f.DirectoryPath));
    });

    [TestMethod]
    public Task IntervalChangesRestartTimer_AndSlowReadsDoNotQueueTicks() => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("initial");
        await f.RefreshAsync("first");
        f.Interval.Value = 100;
        await UntilAsync(() => f.Remote.Count == 2);
        await Task.Delay(230);
        Assert.AreEqual(2, f.Remote.Count);
        Assert.AreEqual(1, f.Remote.MaxConcurrent);
        Task current = f.View.RefreshForTestAsync();
        f.Remote.Complete("second");
        await current;
        f.Auto.IsChecked = false;
        await Task.Delay(150);
        Assert.AreEqual(2, f.Remote.Count);
        f.Interval.Value = 101.9m;
        Assert.AreEqual(101m, f.Interval.Value);
        f.Interval.Value = 1;
        Assert.AreEqual(100m, f.Interval.Value);
    });

    [TestMethod]
    public Task SaveCancelsPendingRefresh_AndRefreshWaitsForUpload() => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("initial");
        f.Auto.IsChecked = true;
        Task obsolete = f.View.RefreshForTestAsync();
        f.Editor.Text = "save me";
        f.UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task save = f.View.SaveForTestAsync();
        await UntilAsync(() => f.UploadCount == 1);
        Assert.IsTrue(f.Remote.CurrentToken.IsCancellationRequested);
        f.Remote.Complete("old remote");
        await obsolete;
        await f.View.RefreshForTestAsync();
        Assert.AreEqual(1, f.Remote.Count);
        Assert.AreEqual("save me", f.Editor.Text);
        Assert.AreEqual("save me", await File.ReadAllTextAsync(f.LocalPath));
        f.UploadGate.SetResult();
        await save;
        Assert.IsFalse(f.View.IsDirtyForTest);
        await f.RefreshAsync("saved remotely");
        Assert.AreEqual("saved remotely", f.Editor.Text);
    });

    [TestMethod]
    public Task ClosingDuringRepeatedSave_StillWaitsForTheActualUpload() => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("initial");
        f.Editor.Text = "saved";
        f.UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task save = f.View.SaveForTestAsync();
        await UntilAsync(() => f.UploadCount == 1);
        Assert.AreSame(save, f.View.SaveForTestAsync());
        f.View.Close();
        Assert.IsTrue(f.View.IsVisible);
        f.UploadGate.SetResult();
        await save;
        await UntilAsync(() => !f.View.IsVisible);
        await UntilAsync(() => !Directory.Exists(f.DirectoryPath));
    });

    [TestMethod]
    public Task CloseKeepsTempDirectoryUntilIgnoringCancellationBackendFinishes() => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("initial");
        f.Auto.IsChecked = true;
        Task read = f.View.RefreshForTestAsync();
        f.View.Close();
        Assert.IsFalse(f.View.IsVisible);
        Assert.IsTrue(Directory.Exists(f.DirectoryPath));
        Assert.IsTrue(f.Remote.CurrentToken.IsCancellationRequested);
        f.Remote.Complete("late");
        await read;
        await UntilAsync(() => !Directory.Exists(f.DirectoryPath));
        Assert.AreEqual("initial", f.Editor.Text);
    });

    [TestMethod]
    public Task CancellingCloseResumesRefresh_AndTheConfirmationDoesNotLoseEdits() => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("initial");
        await f.RefreshAsync("remote");
        f.Interval.Value = 100;
        f.Editor.Text = "unsaved";
        f.View.Close();
        await UntilAsync(() => f.View.OwnedWindows.OfType<MessageDialog>().Any());
        await Task.Delay(160);
        Assert.AreEqual(1, f.Remote.Count);
        Assert.AreEqual("unsaved", f.Editor.Text);
        f.View.OwnedWindows.OfType<MessageDialog>().Single().Close(false);
        await UntilAsync(() => f.Remote.Count == 2);
        Assert.IsTrue(f.View.IsVisible);
        Task refresh = f.View.RefreshForTestAsync();
        f.Remote.Complete("latest");
        await refresh;
        Assert.AreEqual("latest", f.Editor.Text);
    });

    [TestMethod]
    public Task SourceLifetimeStopsActiveOrIdleRefresh_AndMultipleWindowsStayIndependent() => _session.RunOnUiAsync(async () =>
    {
        await using var first = await Fixture.OpenAsync("one");
        await using var second = await Fixture.OpenAsync("two");
        first.Auto.IsChecked = true;
        Task read = first.View.RefreshForTestAsync();
        await second.RefreshAsync("updated two");
        first.Source.Cancel();
        await UntilAsync(() => !first.Auto.IsEnabled);
        Assert.AreNotEqual(true, first.Auto.IsChecked);
        Assert.IsTrue(first.Remote.CurrentToken.IsCancellationRequested);
        first.Remote.Complete("late one");
        await read;
        Assert.AreEqual("one", first.Editor.Text);
        Assert.IsTrue(second.Auto.IsChecked);
        second.Source.Cancel();
        await UntilAsync(() => !second.Auto.IsEnabled);
        Assert.AreNotEqual(true, second.Auto.IsChecked);
    });

    [TestMethod]
    [DataRow("permission")]
    [DataRow("missing")]
    [DataRow("disconnect")]
    [DataRow("partial")]
    [DataRow("too-large")]
    public Task FailedReadsKeepContentAndLocalCopy_StopAndAllowExplicitRetry(string failure) => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("initial");
        f.Editor.Text = "unsaved";
        f.Auto.IsChecked = true;
        Task refresh = f.View.RefreshForTestAsync();
        if (failure == "too-large")
        {
            f.Remote.Complete(new byte[5 * 1024 * 1024 + 1]);
        }
        else
        {
            Exception error = failure switch
            {
                "permission" => new UnauthorizedAccessException("denied"),
                "missing" => new FileNotFoundException("missing"),
                _ => new IOException("connection reset")
            };
            f.Remote.Fail(error, failure == "partial");
        }
        await refresh;
        Assert.AreNotEqual(true, f.Auto.IsChecked);
        Assert.AreEqual("unsaved", f.Editor.Text);
        Assert.IsTrue(f.View.IsDirtyForTest);
        Assert.AreEqual("initial", await File.ReadAllTextAsync(f.LocalPath));
        Assert.HasCount(1, Directory.GetFiles(f.DirectoryPath));
        Assert.IsFalse(string.IsNullOrEmpty(f.View.FindControl<TextBlock>("StatusText")!.Text));
        await f.RefreshAsync("retry succeeded");
        Assert.AreEqual("retry succeeded", f.Editor.Text);
        Assert.IsFalse(f.View.IsDirtyForTest);
    });

    [TestMethod]
    [DataRow("utf8-bom")]
    [DataRow("utf16-le")]
    [DataRow("utf16-be")]
    [DataRow("gbk")]
    public Task RefreshPreservesRemoteEncodingAndBomOnSubsequentSave(string name) => _session.RunOnUiAsync(async () =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = name switch
        {
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => Encoding.Unicode,
            "utf16-be" => Encoding.BigEndianUnicode,
            _ => Encoding.GetEncoding(936)
        };
        await using var f = await Fixture.OpenAsync("initial", Encoding.GetEncoding(936));
        byte[] remote = [.. encoding.GetPreamble(), .. encoding.GetBytes("远端文本")];
        await f.RefreshAsync(remote);
        Assert.AreEqual("远端文本", f.Editor.Text);
        CollectionAssert.AreEqual(remote, await File.ReadAllBytesAsync(f.LocalPath));
        f.Auto.IsChecked = false;
        f.Editor.Text += "修改";
        await f.View.SaveForTestAsync();
        byte[] saved = await File.ReadAllBytesAsync(f.LocalPath);
        CollectionAssert.AreEqual(new byte[] { }.Concat(encoding.GetPreamble()).Concat(encoding.GetBytes("远端文本修改")).ToArray(), saved);
    });

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public Task MinimumWindowKeepsControlsUsable_AndProducesPreview(string cultureName) => _session.RunOnUiAsync(async () =>
    {
        await using var f = await Fixture.OpenAsync("# configuration\nkey=value\n");
        var resources = new ResourceManager("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);
        var culture = CultureInfo.GetCultureInfo(cultureName);
        f.Auto.Content = resources.GetString("Editor_AutoRefresh", culture);
        f.Keep.Content = resources.GetString("Editor_KeepViewPosition", culture);
        f.View.Width = f.View.MinWidth;
        f.View.Height = f.View.MinHeight;
        await SettleAsync();
        foreach (Control control in new Control[] { f.Auto, f.Interval, f.Keep })
        {
            Assert.IsTrue(control.Bounds.Width > 0 && control.Bounds.Height > 0);
            Point? point = control.TranslatePoint(default, f.View);
            Assert.IsNotNull(point);
            Assert.IsTrue(point.Value.X >= 0 && point.Value.X + control.Bounds.Width <= f.View.Bounds.Width + 1);
            Assert.IsTrue(point.Value.Y + control.Bounds.Height <= f.View.Bounds.Height + 1);
        }
        string? output = Environment.GetEnvironmentVariable("VELASHELL_EDITOR_PREVIEW");
        if (!string.IsNullOrEmpty(output) && cultureName == "zh-Hans")
        {
            using (WriteableBitmap? small = f.View.CaptureRenderedFrame())
            {
                Assert.IsNotNull(small);
                using FileStream smallFile = File.Create(Path.ChangeExtension(output, "min.png"));
                small.Save(smallFile, PngBitmapEncoderOptions.Default);
            }
            f.View.Width = 1160;
            f.View.Height = 820;
            await SettleAsync();
            using WriteableBitmap? frame = f.View.CaptureRenderedFrame();
            Assert.IsNotNull(frame);
            using FileStream file = File.Create(output);
            frame.Save(file, PngBitmapEncoderOptions.Default);
        }
    });

    private static async Task SettleAsync()
    {
        await Task.Delay(25);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(5); }
        Assert.IsTrue(condition(), "等待受控任务超时。");
    }

    private sealed class Downloads
    {
        private readonly Queue<(string Path, TaskCompletionSource<byte[]> Result)> _pending = new();
        private int _concurrent;
        public int Count { get; private set; }
        public int MaxConcurrent { get; private set; }
        public CancellationToken CurrentToken { get; private set; }

        public async Task RunAsync(string path, CancellationToken token)
        {
            Count++;
            CurrentToken = token;
            MaxConcurrent = Math.Max(MaxConcurrent, ++_concurrent);
            var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((path, result));
            try
            {
                byte[] bytes = await result.Task; // 模拟不响应取消的真实后端。
                await File.WriteAllBytesAsync(path, bytes);
            }
            finally { _concurrent--; }
        }

        public void Complete(string text) => Complete(Encoding.UTF8.GetBytes(text));
        public void Complete(byte[] bytes) => _pending.Dequeue().Result.SetResult(bytes);
        public void Fail(Exception error, bool partial)
        {
            var item = _pending.Dequeue();
            if (partial) { File.WriteAllText(item.Path, "partial download"); }
            item.Result.SetException(error);
        }
        public void FinishAll()
        {
            while (_pending.Count != 0) { Complete("cleanup"); }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "vela-refresh-test-" + Guid.NewGuid().ToString("N"));
        public string LocalPath => Path.Combine(DirectoryPath, "config.conf");
        public RemoteFileEditorView View { get; private set; } = null!;
        public Downloads Remote { get; } = new();
        public CancellationTokenSource Source { get; } = new();
        public TaskCompletionSource? UploadGate { get; set; }
        public int UploadCount { get; private set; }
        public TextEditor Editor => View.FindControl<TextEditor>("Editor")!;
        public CheckBox Auto => View.FindControl<CheckBox>("AutoRefreshCheckBox")!;
        public CheckBox Keep => View.FindControl<CheckBox>("KeepViewPositionCheckBox")!;
        public NumericUpDown Interval => View.FindControl<NumericUpDown>("RefreshIntervalInput")!;
        public ScrollViewer Scroll => Editor.GetVisualDescendants().OfType<ScrollViewer>().First();

        public static async Task<Fixture> OpenAsync(string content, Encoding? encoding = null)
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.DirectoryPath);
            await File.WriteAllTextAsync(f.LocalPath, content);
            f.View = new RemoteFileEditorView("config.conf", "/etc/config.conf", f.LocalPath,
                                            () => { f.UploadCount++; return f.UploadGate?.Task ?? Task.CompletedTask; },
                                            encoding, f.Remote.RunAsync, f.Source.Token);
            f.View.Show();
            await UntilAsync(() => !f.Editor.IsReadOnly);
            f.View.UpdateLayout();
            return f;
        }

        public Task RefreshAsync(string text) => RefreshAsync(Encoding.UTF8.GetBytes(text));
        public async Task RefreshAsync(byte[] bytes)
        {
            Auto.IsChecked = true;
            Task read = View.RefreshForTestAsync();
            Remote.Complete(bytes);
            await read;
        }

        public async ValueTask DisposeAsync()
        {
            Auto.IsChecked = false;
            Remote.FinishAll();
            UploadGate?.TrySetResult();
            if (View.IsVisible)
            {
                await View.SaveForTestAsync();
                View.Close();
            }
            await UntilAsync(() => !Directory.Exists(DirectoryPath));
            Source.Dispose();
        }
    }
}
