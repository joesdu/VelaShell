using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 差异文档的二次编辑与保存:编辑后重算差异、本地写盘保留 BOM、冲突提示(覆盖 / 重新载入)、远端写与断开的会话、
/// 「载入完整文件」,以及 diff 的语法高亮与对比标签落在同一组。
/// </summary>
public sealed partial class ChatPanelViewUiTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>不碰磁盘的读写替身。</summary>
    private sealed class MemoryDiffHost : IFileDiffHost
    {
        public Dictionary<string, string> Files { get; } = [];
        public List<string> Saved { get; } = [];

        public Task<FileTextReadResult> ReadAsync(string location, string path)
            => Task.FromResult(Files.TryGetValue(path, out string? text)
                ? new FileTextReadResult(text, true, false, null)
                : new FileTextReadResult(null, false, false, null));

        public Task<string?> WriteAsync(string location, string path, string text, bool withBom)
        {
            Files[path] = text;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> ReviewAsync(ChangedFileEntry entry, int revision, bool keep) => throw new NotSupportedException();

        void IFileDiffHost.Saved(FileDiffDocument document, string text) => Saved.Add(text);
    }

    private static FileDiffDocument FullDocument(string location, string path, string before, string after)
        => new(path, null, null, ToolDiff.FromTexts(path, before, after, FileChangeKind.Modified))
        {
            RawLocation = location,
            Before = before,
            After = after,
            HasFullText = true
        };

    /// <summary>临时工作区 + 写好设置,再把面板挂起来。</summary>
    private static async Task<(Window Window, ChatPanelView Panel, string Root)> ShowWithWorkspaceAsync(TestPluginContext context)
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        {
            LocalWorkspaces = [new LocalWorkspace { Id = "w", Name = "Proj", RootPath = root }],
            LocalDefaultWorkspaceId = "w"
        });
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        return (window, panel, root);
    }

    private static async Task<FileDiffDocumentView> OpenChangedAsync(TestPluginContext context, ChatPanelView panel, FileChange change)
    {
        panel.OnToolFileChanged(change);
        await PumpAsync(3);
        ChangedFileEntry entry = panel.ActiveChangedFiles.Find(change.Location, change.Path)!;
        await panel.OpenDiffDocumentAsync(ChangedFileList.KeyOf(change.Location, change.Path),
            panel.DocumentOf(entry));
        return (FileDiffDocumentView)context.FakeUi.LastPanel.CreateContent();
    }

    [TestMethod]
    public void DiffDocument_EditRecomputesAfterDebounce_AndRevertClearsDirty()
    {
        OnUi(async () =>
        {
            var host = new MemoryDiffHost();
            var view = new FileDiffDocumentView(FullDocument("local:w", "a.txt", "a\nb\n", "a\nB\n"), new Loc("en"), host);
            var window = new Window { Width = 1000, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                Assert.AreEqual(1, view.CurrentDiff!.Added);
                Assert.IsFalse(view.IsDirty);
                TextBlock dot = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DiffDocDirty");
                Assert.IsFalse(dot.IsVisible);

                view.RightEditor!.Text = "a\nB\nC\n";
                Assert.IsTrue(view.IsDirty);
                Assert.IsTrue(dot.IsVisible, "有未保存修改时显示圆点");
                Assert.AreEqual(1, view.CurrentDiff.Added, "防抖期间还没重算");
                await Task.Delay(FileDiffDocumentView.RecomputeDelay + TimeSpan.FromMilliseconds(200));
                await PumpAsync(5);
                Assert.AreEqual(2, view.CurrentDiff!.Added, "停手约 300ms 后重算");
                Assert.AreEqual("+2", view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DiffDocAdded").Text);
                Assert.HasCount(1, view.ChangeStarts);

                view.Revert();
                Assert.IsFalse(view.IsDirty);
                Assert.AreEqual("a\nB\n", view.RightEditor.Text);

                // 内联模式只读,右侧的修改带过去、切回来还在
                view.RightEditor.Text = "a\nX\n";
                RaiseClick(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.Name == "DiffInlineToggle"));
                Assert.IsNotNull(view.InlineView);
                StringAssert.Contains(view.StatusText, "read-only");
                RaiseClick(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.Name == "DiffSideBySideToggle"));
                Assert.AreEqual("a\nX\n", view.RightEditor!.Text);
                Assert.IsTrue(view.IsDirty);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void DiffDocument_CSharpIndentationFollowsBlockDepth()
    {
        OnUi(() =>
        {
            const string code = "class Example\n{\nint value;\n}";
            var view = new FileDiffDocumentView(FullDocument("local:w", "sample.CS", code, code), new Loc("en"), new MemoryDiffHost());
            var editor = view.RightEditor!;
            editor.TextArea.IndentationStrategy.IndentLine(editor.Document, editor.Document.GetLineByNumber(3));
            Assert.AreEqual("class Example\n{\n" + editor.Options.IndentationString + "int value;\n}", editor.Text);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void DiffDocument_WhitespaceFilterPreservesEditsWhenSwitchingModesAndSaving()
    {
        OnUi(async () =>
        {
            var host = new MemoryDiffHost();
            host.Files["a.txt"] = "x\n";
            var view = new FileDiffDocumentView(FullDocument("local:w", "a.txt", "x\n", "x\n"), new Loc("en"), host);
            var window = new Window { Width = 1000, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(3);
                view.RightEditor!.Text = "  x \n";
                var toggle = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => b.Name == "DiffIgnoreWhitespace");
                toggle.IsChecked = true;
                RaiseClick(toggle);
                Assert.IsFalse(view.CurrentDiff!.HasChanges);
                Assert.IsTrue(view.IsDirty);
                RaiseClick(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiffInlineToggle"));
                Assert.IsFalse(view.CurrentDiff.HasChanges);
                Assert.IsTrue(view.IsDirty);
                view.Update(FullDocument("local:w", "a.txt", "x\n", "agent\n"));
                toggle.IsChecked = false;
                RaiseClick(toggle);
                Assert.IsTrue(view.CurrentDiff.HasChanges);
                RaiseClick(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiffSideBySideToggle"));
                await view.SaveAsync();
                Assert.AreEqual("  x \n", host.Files["a.txt"]);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void DiffDocument_SplitterResizesPanes_AndWidthSurvivesModeSwitch()
    {
        OnUi(async () =>
        {
            var view = new FileDiffDocumentView(FullDocument("local:w", "a.txt", "a\nb\n", "a\nB\n"), new Loc("en"), new MemoryDiffHost());
            var window = new Window { Width = 1000, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                GridSplitter splitter = view.GetVisualDescendants()
                    .OfType<GridSplitter>().Single(s => s.Name == "DiffSplitter");
                double before = view.LeftEditor!.Bounds.Width;
                Assert.AreEqual(view.RightEditor!.Bounds.Width, before, 1.0, "默认对半");

                splitter.RaiseEvent(new Avalonia.Input.VectorEventArgs
                {
                    RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragStartedEvent,
                    Vector = default
                });
                splitter.RaiseEvent(new Avalonia.Input.VectorEventArgs
                {
                    RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragDeltaEvent,
                    Vector = new Avalonia.Vector(200, 0)
                });
                splitter.RaiseEvent(new Avalonia.Input.VectorEventArgs
                {
                    RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragCompletedEvent,
                    Vector = new Avalonia.Vector(200, 0)
                });
                await PumpAsync(5);
                Assert.IsGreaterThan(before + 150, view.LeftEditor.Bounds.Width, "拖动分割条后左栏变宽");
                Assert.IsGreaterThan(0.6, view.LeftShare);
                double share = view.LeftShare;

                RaiseClick(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.Name == "DiffInlineToggle"));
                RaiseClick(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.Name == "DiffSideBySideToggle"));
                await PumpAsync(5);
                Assert.AreEqual(share, view.LeftShare, 0.001);
                Assert.IsGreaterThan(view.RightEditor!.Bounds.Width + 100, view.LeftEditor!.Bounds.Width, "切内联再切回来宽度不丢");
            }
            finally { window.Close(); }
        });
    }

    /// <summary>像用户拖滚动条 / 滚轮那样设编辑器的滚动位置(TextEditor.ScrollToVerticalOffset 在 AvaloniaEdit 12.0.0 里是空方法)。</summary>
    private static void ScrollEditor(AvaloniaEdit.TextEditor editor, double x, double y)
        => editor.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = new Avalonia.Vector(x, y);

    private static double LineTop(AvaloniaEdit.TextEditor editor, int line)
        => editor.TextArea.TextView.GetVisualTopByDocumentLine(line);

    [TestMethod]
    public void DiffDocument_ScrollingEitherSide_KeepsTheOtherAligned()
    {
        OnUi(async () =>
        {
            // 300 行:第 10 行很长(横向滚动用),第 75 行改了,第 150 行后插入 6 行,第 200 行删掉
            var old = Enumerable.Range(1, 300).Select(i => i == 10 ? new string('x', 600) : "line " + i).ToList();
            var @new = old.Select(l => l == "line 75" ? "line 75 changed" : l).ToList();
            @new.InsertRange(150, Enumerable.Range(1, 6).Select(i => "inserted " + i));
            @new.Remove("line 200");
            string before = string.Join('\n', old) + "\n", after = string.Join('\n', @new) + "\n";
            var view = new FileDiffDocumentView(FullDocument("local:w", "a.txt", before, after), new Loc("en"), new MemoryDiffHost());
            var window = new Window { Width = 1000, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                AvaloniaEdit.TextEditor left = view.LeftEditor!, right = view.RightEditor!;

                ScrollEditor(right, 0, LineTop(right, 57));
                await PumpAsync(3);
                Assert.AreEqual(LineTop(left, 57), left.VerticalOffset, 1.0, "滚右侧,左侧跟到同一行");

                // 右侧滚进插入的那 6 行中间:左侧停在第 150 行附近慢慢走,不跳到后面
                ScrollEditor(right, 0, LineTop(right, 153));
                await PumpAsync(3);
                Assert.IsGreaterThan(LineTop(left, 150) - 1, left.VerticalOffset);
                Assert.IsLessThan(LineTop(left, 151), left.VerticalOffset, "插入块里左侧不跳");
                Assert.AreEqual(LineTop(right, 153), right.VerticalOffset, 1.0, "右侧没被反向同步拉回去");

                ScrollEditor(right, 0, LineTop(right, 180));
                await PumpAsync(3);
                Assert.AreEqual(LineTop(left, 174), left.VerticalOffset, 1.0, "过了插入块,两侧按行映射对齐");

                // 反过来滚左侧:第 200 行删了,左 250 对右 255
                ScrollEditor(left, 0, LineTop(left, 250));
                await PumpAsync(3);
                Assert.AreEqual(LineTop(right, 255), right.VerticalOffset, 1.0, "滚左侧,右侧跟到对应的行");
                Assert.AreEqual(LineTop(left, 250), left.VerticalOffset, 1.0);

                // 横向照搬
                ScrollEditor(left, 0, 0);
                await PumpAsync(3);
                Assert.AreEqual(0, right.VerticalOffset, 1.0);
                ScrollEditor(right, 120, 0);
                await PumpAsync(3);
                Assert.AreEqual(120, left.HorizontalOffset, 1.0, "横向滚动也同步");
                ScrollEditor(right, 0, 0);
                await PumpAsync(3);

                // 下一处改动真的滚过去(第 75 行,上面留两行),左侧跟着
                view.Navigate(1);
                await PumpAsync(3);
                Assert.AreEqual(LineTop(right, 73), right.VerticalOffset, 1.0, "下一处改动滚到位");
                Assert.AreEqual(LineTop(left, 73), left.VerticalOffset, 1.0);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void DiffScrollMapping_InterpolatesBetweenAnchors_AndRunsOnPastTheLast()
    {
        double[] from = [0, 10, 11, 20], to = [0, 10, 17, 26];
        Assert.AreEqual(5, FileDiffDocumentView.MapAcross(5, from, to), 1e-9);
        Assert.AreEqual(13.5, FileDiffDocumentView.MapAcross(10.5, from, to), 1e-9, "一行对七行的那段按比例摊开");
        Assert.AreEqual(17, FileDiffDocumentView.MapAcross(11, from, to), 1e-9);
        Assert.AreEqual(30, FileDiffDocumentView.MapAcross(24, from, to), 1e-9, "最后一个锚点之后一比一顺延");
        Assert.AreEqual(10.5, FileDiffDocumentView.MapAcross(13.5, to, from), 1e-9, "反向映射是逆");
    }

    [TestMethod]
    public void DiffDocument_SaveWritesLocalFile_KeepsBom_AndUpdatesChangedFiles()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, string root) = await ShowWithWorkspaceAsync(context);
            try
            {
                string file = Path.Combine(root, "src", "app.cs");
                await File.WriteAllBytesAsync(file, [.. Bom, .. Encoding.UTF8.GetBytes("int a = 1;\n")]);
                FileDiffDocumentView view = await OpenChangedAsync(context, panel,
                    new FileChange("patch_local_file", "local:w", "src/app.cs", FileChangeKind.Modified, "int a = 0;\n", "int a = 1;\n"));

                view.RightEditor!.Text = "int a = 2;\n";
                await view.SaveAsync();

                byte[] bytes = await File.ReadAllBytesAsync(file);
                Assert.AreSequenceEqual(Bom, bytes[..3], "原文件带 BOM,保存后照样带");
                Assert.AreEqual("int a = 2;\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
                Assert.IsFalse(view.IsDirty);
                Assert.AreEqual("Saved.", view.StatusText);
                Assert.AreEqual("int a = 2;\n", view.Baseline);
                ChangedFileEntry entry = panel.ActiveChangedFiles.Entries.Single();
                Assert.AreEqual("int a = 2;\n", entry.After, "文件条跟着更新");
                Assert.AreEqual("int a = 0;\n", entry.Before);

                // 没有 BOM 的文件写完也没有
                string plain = Path.Combine(root, "plain.txt");
                await File.WriteAllTextAsync(plain, "x\n", new UTF8Encoding(false));
                FileDiffDocumentView plainView = await OpenChangedAsync(context, panel,
                    new FileChange("write_local_file", "local:w", "plain.txt", FileChangeKind.Modified, "", "x\n"));
                plainView.RightEditor!.Text = "y\n";
                await plainView.SaveAsync();
                Assert.AreSequenceEqual(Encoding.UTF8.GetBytes("y\n"), await File.ReadAllBytesAsync(plain));
            }
            finally { panel.Detach(); window.Close(); Directory.Delete(root, recursive: true); }
        });
    }

    [TestMethod]
    public void DiffDocument_SaveDetectsConflict_OverwriteOrReload()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, string root) = await ShowWithWorkspaceAsync(context);
            try
            {
                string file = Path.Combine(root, "conf.ini");
                await File.WriteAllTextAsync(file, "k=1\n");
                FileDiffDocumentView view = await OpenChangedAsync(context, panel,
                    new FileChange("write_local_file", "local:w", "conf.ini", FileChangeKind.Modified, "k=0\n", "k=1\n"));

                // 别的程序改了文件
                await File.WriteAllTextAsync(file, "k=9\n");
                view.RightEditor!.Text = "k=2\n";
                await view.SaveAsync();
                Assert.IsTrue(view.IsConflictShown, "磁盘内容与基线不一致:提示冲突");
                Assert.AreEqual("k=9\n", await File.ReadAllTextAsync(file), "冲突时先不写");

                await view.OverwriteAsync();
                Assert.AreEqual("k=2\n", await File.ReadAllTextAsync(file));
                Assert.IsFalse(view.IsConflictShown);

                await File.WriteAllTextAsync(file, "k=7\n");
                view.RightEditor.Text = "k=3\n";
                await view.SaveAsync();
                Assert.IsTrue(view.IsConflictShown);
                view.ReloadFromDisk();
                Assert.AreEqual("k=7\n", view.RightEditor.Text, "重新载入:换成磁盘上的当前内容");
                Assert.IsFalse(view.IsDirty);
                Assert.AreEqual("k=7\n", await File.ReadAllTextAsync(file));
            }
            finally { panel.Detach(); window.Close(); Directory.Delete(root, recursive: true); }
        });
    }

    [TestMethod]
    public void DiffDocument_RemoteSave_AndDisconnectedSession()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            SessionInfo session = context.FakeSessions.AddConnected(host: "10.0.0.5", username: "root");
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/app.conf", Encoding.UTF8.GetBytes("port=80\n"));
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                FileDiffDocumentView view = await OpenChangedAsync(context, panel,
                    new FileChange("patch_remote_file", "remote:" + session.SessionId, "/etc/app.conf", FileChangeKind.Modified, "port=22\n", "port=80\n"));
                view.RightEditor!.Text = "port=8080\n";
                await view.SaveAsync();
                Assert.AreEqual("port=8080\n", Encoding.UTF8.GetString(context.FakeRemoteFs.GetFile(session.SessionId, "/etc/app.conf")!));
                Assert.AreEqual("Saved.", view.StatusText);

                FileDiffDocumentView gone = await OpenChangedAsync(context, panel,
                    new FileChange("patch_remote_file", "remote:closed-session", "/etc/x.conf", FileChangeKind.Modified, "a\n", "b\n"));
                gone.RightEditor!.Text = "c\n";
                await gone.SaveAsync();
                StringAssert.Contains(gone.StatusText, "disconnected", "会话断开时给出明确提示");
                Assert.IsTrue(gone.IsDirty);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>只有补丁片段:「载入完整文件」读当前全文,左侧用片段反推改动前的内容。</summary>
    [TestMethod]
    public void DiffDocument_LoadFullFile_FromFragment_ReverseAppliesThePatch()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, string root) = await ShowWithWorkspaceAsync(context);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(root, "src", "app.cs"), "int a = 1;\nint b = 3;\nint c = 4;\n");
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                // 历史回放的样子:只有参数,没有改动事件
                bubble.AddToolCall("c1", "patch_local_file", PatchArgs);
                bubble.CompleteToolCall("c1", "Patched.");
                await PumpAsync(3);
                bubble.ToolCardsForTest().Single().OpenDiffDocument();
                await PumpAsync(5);
                var view = (FileDiffDocumentView)context.FakeUi.LastPanel.CreateContent();
                Assert.IsFalse(view.Document.HasFullText);
                Assert.IsTrue(view.Document.CanLoadFull);
                Assert.IsTrue(view.RightEditor!.IsReadOnly, "片段不能直接编辑");

                await view.LoadFullFileAsync();
                Assert.IsTrue(view.Document.CanEdit);
                Assert.AreEqual("int a = 1;\nint b = 3;\nint c = 4;\n", view.RightEditor!.Text);
                Assert.AreEqual("int a = 1;\nint b = 2;\n", view.LeftEditor!.Text, "左侧由补丁反推出改动前的内容");
                Assert.IsFalse(view.RightEditor.IsReadOnly);
            }
            finally { panel.Detach(); window.Close(); Directory.Delete(root, recursive: true); }
        });
    }

    [TestMethod]
    public void DiffTabs_OpenIntoTheSameGroup()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                await OpenChangedAsync(context, panel, new FileChange("write_local_file", "local:w", "a.txt", FileChangeKind.Created, null, "a"));
                FakePanel first = context.FakeUi.LastPanel;
                Assert.AreEqual(0, first.ActivateCount);
                await OpenChangedAsync(context, panel, new FileChange("write_local_file", "local:w", "b.txt", FileChangeKind.Created, null, "b"));
                Assert.AreNotSame(first, context.FakeUi.LastPanel);
                Assert.AreEqual(1, first.ActivateCount, "开新的对比标签前先把已有的那个切到前面,新标签进同一组");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>工具卡片里的 diff 与对比标签的内联视图都带 TextMate 高亮。</summary>
    [TestMethod]
    public void DiffViews_AreSyntaxHighlighted()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                bubble.AddToolCall("c1", "patch_local_file", PatchArgs);
                await PumpAsync(3);
                Border card = ToolItems(bubble.Root).Single();
                Press(card.GetVisualDescendants().OfType<Grid>().First());
                await PumpAsync(3);
                DiffView diff = card.GetVisualDescendants().OfType<DiffView>().Single();
                Assert.IsTrue(diff.IsHighlighted);
                TextBlock removed = diff.Rows.Children.First(r => r.Classes.Contains("diffRemoved"))
                    .GetVisualDescendants().OfType<TextBlock>().Last();
                Assert.IsTrue(removed.Inlines!.OfType<Run>().Any(r => r.Foreground is not null), "int 关键字着色");

                FileDiffDocumentView view = await OpenChangedAsync(context, panel,
                    new FileChange("patch_local_file", "local:w", "src/x.cs", FileChangeKind.Modified, "int a;\n", "int b;\n"));
                Assert.IsNotNull(view.RightColorizer, "并排编辑器挂上了 TextMate 着色器");
                Assert.IsNotEmpty(view.RightColorizer.SpansOf(1));
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
