using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Localization;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// SFTP / FTP 文档的本地栏与远端栏的键盘操作:键入定位(两栏都有)、路径栏直接输入(本地栏)。
/// 规则本身由 <see cref="TypeAheadSearchTests" /> 与 <c>LocalFilePaneViewModelTests</c> 盯,上传选择框由
/// <see cref="LocalPathPickerKeyboardUiTests" /> 盯;这一层盯的是两栏里的接线 —— 键入只在焦点所在的那一栏生效,
/// 进了别的目录之后接着敲还能定位,路径框里的回车 / Esc 不外溢。
/// </summary>
[TestClass]
[TestCategory("PaneKeyboardUI")]
public sealed class FilePaneKeyboardUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FilePaneKeyboardUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    // ---- 本地栏 ----

    [TestMethod]
    public void LocalPane_TypingTheStartOfAName_SelectsAndRevealsIt()
    {
        RunWithLocalPane((window, view, list, vm, _) =>
        {
            FocusRow(window, list, 0);
            Type(window, "fxyc");

            Assert.AreSequenceEqual(["fxyc.txt"], LocalNames(vm));
            Assert.IsTrue(IsInViewport(list, vm.SelectedEntries.Single()), "定位到的行很靠下,必须滚进视口。");
            Assert.AreEqual("fxyc.txt", (FocusedItem(window) as LocalFileEntry)?.Name);
        });
    }

    [TestMethod]
    public void LocalPane_AfterEnteringAFolder_TypingStillLocates()
    {
        RunWithLocalPane((window, view, list, vm, tempRoot) =>
        {
            // 双击进目录:旧的行全换掉,原来那一行的焦点跟着没了 —— 键入定位只在焦点在这一栏里时生效,焦点丢了就敲不动。
            FocusRow(window, list, 0);
            Type(window, "sub");
            Assert.AreSequenceEqual(["sub"], LocalNames(vm));
            DoubleTapFocusedRow(window, list);
            PumpWhile(window, () => vm.CurrentPath != Path.Combine(tempRoot, "sub"));
            Assert.AreEqual(Path.Combine(tempRoot, "sub"), vm.CurrentPath, "双击应进了 sub。");

            Type(window, "in");

            Assert.AreSequenceEqual(["inner.txt"], LocalNames(vm), "进了目录之后接着敲,还得能定位。");
        });
    }

    [TestMethod]
    public void LocalPane_RefreshKeepsFocusInTheList_WithoutScrolling()
    {
        RunWithLocalPane((window, view, list, vm, _) =>
        {
            // 往下翻到靠后的位置、不选任何东西,然后目录刷新(后台自动刷新也是这一条路):行整批换掉,焦点跟着没了。
            FocusRow(window, list, 0);
            Type(window, "fxyc");
            vm.SelectedEntries.Clear();
            Pump(window);
            ScrollViewer scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Vector before = scroller.Offset;
            Assert.IsGreaterThan(0, before.Y, "前提:列表已经往下翻过。");

            PumpUntil(vm.RefreshAsync());
            Pump(window);

            Assert.IsNotNull(FocusedItem(window), "刷新之后焦点还在列表里,接着能敲字定位、按方向键。");
            Assert.AreEqual(before, scroller.Offset, "把焦点还给列表时不能滚动 —— 后台刷新会把正在翻的人拽回去。");
        });
    }

    [TestMethod]
    public void LocalPane_CtrlL_ThenEnteringAFolderPath_Navigates_AndFocusReturnsToTheList()
    {
        RunWithLocalPane((window, view, list, vm, tempRoot) =>
        {
            FocusRow(window, list, 0);
            Press(window, Key.L, PhysicalKey.L, RawInputModifiers.Control);

            TextBox box = PathBox(view);
            Assert.IsTrue(vm.IsPathEditing);
            Assert.IsTrue(box.IsFocused, "切出来的输入框要直接能打字。");

            window.KeyTextInput(Path.Combine(tempRoot, "sub"));
            Pump(window);
            Assert.IsEmpty(vm.SelectedEntries, "在路径框里打的字不是键入定位。");
            Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
            PumpWhile(window, () => vm.IsPathEditing || FocusedItem(window) is null);

            Assert.AreEqual(Path.Combine(tempRoot, "sub"), vm.CurrentPath);
            Assert.IsNotNull(FocusedItem(window), "跳转后焦点回到列表,接着就能敲字定位。");
            Type(window, "in");
            Assert.AreSequenceEqual(["inner.txt"], LocalNames(vm));
        });
    }

    [TestMethod]
    public void LocalPane_EnteringAFilePath_SelectsThatFile()
    {
        RunWithLocalPane((window, view, list, vm, tempRoot) =>
        {
            PumpUntil(vm.NavigateToAsync(Path.Combine(tempRoot, "sub")));
            Pump(window);
            FocusRow(window, list, 0);
            Press(window, Key.L, PhysicalKey.L, RawInputModifiers.Control);
            window.KeyTextInput(Path.Combine(tempRoot, "fxyc.txt"));
            Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
            PumpWhile(window, () => vm.IsPathEditing || FocusedItem(window) is null);

            Assert.AreEqual(tempRoot, vm.CurrentPath);
            Assert.AreSequenceEqual(["fxyc.txt"], LocalNames(vm));
            Assert.IsTrue(IsInViewport(list, vm.SelectedEntries.Single()));
        });
    }

    [TestMethod]
    public void LocalPane_EscInThePathBox_CancelsWithoutLeakingToTheHost()
    {
        RunWithLocalPane((window, view, list, vm, tempRoot) =>
        {
            int escapesReachingWindow = 0;
            window.KeyDown += (_, e) => escapesReachingWindow += e.Key == Key.Escape ? 1 : 0;

            FocusRow(window, list, 0);
            Press(window, Key.L, PhysicalKey.L, RawInputModifiers.Control);
            window.KeyTextInput("somewhere-else");
            Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);

            Assert.IsFalse(vm.IsPathEditing);
            Assert.AreEqual(tempRoot, vm.CurrentPath);
            Assert.AreEqual(0, escapesReachingWindow, "路径框里的 Esc 只收起输入框,不该再冒到窗口上触发别的。");
        });
    }

    [TestMethod]
    public void LocalPane_ClickingTheBlankAfterTheBreadcrumbs_OpensThePathBox()
    {
        RunWithLocalPane((window, view, _, vm, tempRoot) =>
        {
            // 退到盘根:临时目录的面包屑很长,会把整条路径栏占满,没有空白可点。
            PumpUntil(vm.NavigateToAsync(Path.GetPathRoot(tempRoot)!));
            Pump(window);
            Border area = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PathCrumbArea");

            Point blank = area.TranslatePoint(new(area.Bounds.Width - 4, area.Bounds.Height / 2), window)!.Value;
            window.MouseDown(blank, MouseButton.Left);
            window.MouseUp(blank, MouseButton.Left);
            Pump(window);

            Assert.IsTrue(vm.IsPathEditing);
            Assert.IsTrue(PathBox(view).IsFocused);
        });
    }

    // ---- 远端栏 ----

    [TestMethod]
    public void RemotePane_TypingTheStartOfAName_SelectsAndRevealsIt()
    {
        RunWithRemotePane((window, list, vm) =>
        {
            FocusRow(window, list, 0);
            Type(window, "fxyc");

            Assert.AreSequenceEqual(["fxyc.txt"], RemoteNames(vm));
            Assert.IsTrue(IsInViewport(list, vm.SelectedFiles.Single()), "定位到的行很靠下,必须滚进视口。");
            Assert.AreEqual("fxyc.txt", (FocusedItem(window) as RemoteFileInfoViewModel)?.Name);
        });
    }

    [TestMethod]
    public void RemotePane_RepeatingOneLetter_CyclesThroughTheNamesStartingWithIt()
    {
        RunWithRemotePane((window, list, vm) =>
        {
            FocusRow(window, list, 0);
            List<string> path = [];
            for (int i = 0; i < 4; i++)
            {
                Type(window, "f");
                path.Add(string.Join(",", RemoteNames(vm)));
            }

            Assert.AreSequenceEqual(["fxa.txt", "fxyb.txt", "fxyc.txt", "fz.txt"], path);
        });
    }

    [TestMethod]
    public void RemotePane_AfterEnteringAFolder_TypingStillLocates()
    {
        RunWithRemotePane((window, list, vm) =>
        {
            FocusRow(window, list, 0);
            Type(window, "sub");
            Assert.AreSequenceEqual(["sub"], RemoteNames(vm));
            DoubleTapFocusedRow(window, list);
            PumpWhile(window, () => vm.CurrentPath != "/home/user/sub");
            Assert.AreEqual("/home/user/sub", vm.CurrentPath, "双击应进了 sub。");

            Type(window, "fz");

            Assert.AreSequenceEqual(["fz.txt"], RemoteNames(vm), "进了目录之后接着敲,还得能定位。");
        });
    }

    [TestMethod]
    public void RemotePane_EnterOnALocatedFolder_OpensIt_AndTypingStillLocates()
    {
        RunWithRemotePane((window, list, vm) =>
        {
            FocusRow(window, list, 0);
            Type(window, "sub");
            Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
            PumpWhile(window, () => vm.CurrentPath != "/home/user/sub" || FocusedItem(window) is null);

            Assert.AreEqual("/home/user/sub", vm.CurrentPath, "只选着一个文件夹时回车与双击一样:进去。");
            Type(window, "fz");
            Assert.AreSequenceEqual(["fz.txt"], RemoteNames(vm), "回车进去之后焦点在新列表上,接着敲还能定位。");
        });
    }

    [TestMethod]
    public void RemotePane_EnterOnAFile_DoesNothing()
    {
        RunWithRemotePane((window, list, vm) =>
        {
            FocusRow(window, list, 0);
            Type(window, "fxyc");
            Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
            PumpWhile(window, () => false);

            Assert.AreEqual("/home/user", vm.CurrentPath);
            Assert.AreSequenceEqual(["fxyc.txt"], RemoteNames(vm), "文件上的回车不做事(双击文件是下载并打开,不该被顺手的回车触发)。");
        });
    }

    [TestMethod]
    public void LocalPane_EnterOnALocatedFolder_OpensIt_ButNotOnAFile()
    {
        RunWithLocalPane((window, view, list, vm, tempRoot) =>
        {
            FocusRow(window, list, 0);
            Type(window, "fxyc");
            Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
            Assert.AreEqual(tempRoot, vm.CurrentPath, "文件上的回车不做事。");

            vm.SelectedEntries.Clear();
            PumpWhile(window, () => false);
            Thread.Sleep(TypeAheadSearch.Timeout + TimeSpan.FromMilliseconds(50));   // 让前缀过期,下面重新敲
            Type(window, "sub");
            Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
            PumpWhile(window, () => vm.CurrentPath != Path.Combine(tempRoot, "sub") || FocusedItem(window) is null);

            Assert.AreEqual(Path.Combine(tempRoot, "sub"), vm.CurrentPath, "只选着一个文件夹时回车与双击一样:进去。");
            Type(window, "in");
            Assert.AreSequenceEqual(["inner.txt"], LocalNames(vm), "回车进去之后焦点在新列表上,接着敲还能定位。");
        });
    }

    [TestMethod]
    public void TypingWithFocusOutsideThePane_DoesNotLocate()
    {
        RunWithRemotePane((window, list, vm) =>
        {
            // 焦点不在这一栏(比如在终端里):敲的字与这一栏无关。
            Type(window, "fx");

            Assert.IsEmpty(vm.SelectedFiles);
        });
    }

    // ---- 工具 ----

    private static string[] LocalNames(LocalFilePaneViewModel vm) => [.. vm.SelectedEntries.Select(entry => entry.Name)];

    private static string[] RemoteNames(FileBrowserViewModel vm) => [.. vm.SelectedFiles.Select(file => file.Name)];

    private static TextBox PathBox(Control view) =>
        view.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PathEditBox");

    private static object? FocusedItem(Window window) =>
        (window.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext;

    private static void FocusRow(Window window, ListBox list, int index)
    {
        list.ContainerFromIndex(index)!.Focus();
        Pump(window);
    }

    /// <summary>在焦点所在的行上双击(与用户的手势一样,经 DoubleTapped 进目录)。</summary>
    private static void DoubleTapFocusedRow(Window window, ListBox list)
    {
        Control row = (window.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)
                      ?? throw new InvalidOperationException("没有获焦的行。");
        Point at = row.TranslatePoint(new(20, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left);
        Pump(window);
        Assert.IsTrue(list.IsVisible);
    }

    private static void Type(Window window, string text)
    {
        foreach (char c in text)
        {
            window.KeyTextInput(c.ToString());
            Pump(window);
        }
    }

    private static bool IsInViewport(ListBox list, object item)
    {
        if (list.ContainerFromItem(item) is not { } container
            || container.TranslatePoint(default, list) is not { } top)
        {
            return false;
        }
        return top.Y >= 0 && top.Y + container.Bounds.Height <= list.Bounds.Height + 0.5;
    }

    private static void Press(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Pump(window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpWhile(Window window, Func<bool> condition)
    {
        for (int i = 0; i < 2000 && condition(); i++)
        {
            Pump(window);
            Thread.Sleep(1);
        }
        Pump(window);
    }

    private static void PumpUntil(Task task)
    {
        for (int i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }

    /// <summary>名字的排列与上传选择框的用例一样:sub、90 个 aNNN.txt 垫在前面,再是 fxa / fxyb / fxyc / fz。</summary>
    private static IEnumerable<string> FileNames() =>
        [.. Enumerable.Range(0, 90).Select(i => $"a{i:000}.txt"), "fxa.txt", "fxyb.txt", "fxyc.txt", "fz.txt"];

    /// <summary>在临时目录上开一个本地栏(放在窗口里,窗口另有一个可聚焦的按钮充当「别处」)。</summary>
    private static void RunWithLocalPane(Action<Window, LocalFilePaneView, ListBox, LocalFilePaneViewModel, string> body) =>
        OnUi(() =>
        {
            string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"vela-pane-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "sub"));
            File.WriteAllText(Path.Combine(tempRoot, "sub", "inner.txt"), "x");
            foreach (string name in FileNames())
            {
                File.WriteAllText(Path.Combine(tempRoot, name), "x");
            }

            var vm = UnwatchedLocalFileSystem.NewPane();
            PumpUntil(vm.NavigateToAsync(tempRoot));
            var view = new LocalFilePaneView { DataContext = vm };
            var window = new Window { Width = 600, Height = 400, Content = view };
            try
            {
                window.Show();
                Pump(window);
                ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList");
                Assert.HasCount(1 + 1 + 94, vm.Entries);
                body(window, view, list, vm, tempRoot);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                // 先停掉目录监视再删目录(见 LocalPathPickerMarqueeUiTests)。
                vm.Dispose();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        });

    /// <summary>开一个远端栏:/home/user 下与本地栏同样的名字,/home/user/sub 下是另一批(含 fz.txt)。</summary>
    private static void RunWithRemotePane(Action<Window, ListBox, FileBrowserViewModel> body) =>
        OnUi(() =>
        {
            ISftpService sftp = Substitute.For<ISftpService>();
            var sessionId = Guid.NewGuid();
            static RemoteFileInfo Remote(string directory, string name, bool isDirectory = false) => new()
            {
                Name = name,
                FullPath = $"{directory}/{name}",
                Size = 1,
                Permissions = isDirectory ? "drwxr-xr-x" : "-rw-r--r--",
                IsDirectory = isDirectory,
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            };
            List<RemoteFileInfo> home = [Remote("/home/user", "sub", isDirectory: true), .. FileNames().Select(name => Remote("/home/user", name))];
            List<RemoteFileInfo> sub = [.. new[] { "alpha.txt", "fz.txt", "zeta.txt" }.Select(name => Remote("/home/user/sub", name))];
            sftp.ListDirectoryAsync(sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(call.ArgAt<string>(1).EndsWith("/sub", StringComparison.Ordinal) ? sub : home));

            var vm = new FileBrowserViewModel(sftp, sessionId)
            {
                IsVisible = true,
                TransferOptions = new(),
                CurrentPath = "/home/user",
            };
            vm.RefreshCommand.Execute().Subscribe();
            for (int i = 0; i < 2000 && vm.Files.Count == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }

            var view = new FileBrowserView { DataContext = vm };
            var elsewhere = new Button { Content = "elsewhere" };
            var window = new Window { Width = 900, Height = 500, Content = new DockPanel { Children = { elsewhere, view } } };
            DockPanel.SetDock(elsewhere, Dock.Top);
            try
            {
                window.Show();
                Pump(window);
                elsewhere.Focus();
                Pump(window);
                ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList");
                Assert.AreEqual(1 + 1 + 94, list.ItemCount, "应为 \"..\"、sub 与 94 个文件。");
                body(window, list, vm);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
