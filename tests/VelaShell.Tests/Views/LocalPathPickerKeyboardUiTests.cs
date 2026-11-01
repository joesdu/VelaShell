using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Localization;
using VelaShell.Localization;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 上传选择器的键盘操作:派发真实的键入 / 按键 / 点击,验证
/// 「直接敲文件名开头定位」与「路径栏直接输入路径」这两件事在真窗口里接得上。
/// <para>
/// 匹配规则由 <see cref="TypeAheadSearchTests" />、路径解析由 <c>LocalFilePaneViewModelTests</c> 单独盯住;
/// 这一层盯的是接线 —— 键入有没有到手、定位到的行有没有滚进视口、焦点有没有跟过去、
/// 路径框里的回车 / Esc 会不会冒到窗口上把选择器整个提交或关掉。
/// </para>
/// </summary>
[TestClass]
[TestCategory("PickerKeyboardUI")]
public sealed class LocalPathPickerKeyboardUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LocalPathPickerKeyboardUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    // ---- 键入定位 ----

    [TestMethod]
    public void TypingTheStartOfAName_SelectsIt_ScrollsItIntoView_AndMovesFocusThere()
    {
        RunWithPicker((dialog, list, vm, _) =>
        {
            List<string> path = [];
            foreach (char key in "fxyc")
            {
                dialog.KeyTextInput(key.ToString());
                Pump(dialog);
                path.Add(string.Join(",", SelectedNames(vm)));
            }

            // f → fxa;fx 时 fxa 还对得上,原地不动;fxy → fxyb;fxyc → fxyc。
            Assert.AreSequenceEqual(["fxa.txt", "fxa.txt", "fxyb.txt", "fxyc.txt"], path);
            LocalFileEntry target = vm.SelectedEntries.Single();
            Assert.IsTrue(IsInViewport(list, target), "定位到的行在列表很靠下,必须滚进视口,否则选中了也看不见。");
            Assert.AreEqual("fxyc.txt", FocusedEntry(dialog)?.Name, "焦点要跟到定位到的行上,接着按方向键才从这里走。");
        });
    }

    [TestMethod]
    public void RepeatingOneLetter_CyclesThroughTheNamesStartingWithIt()
    {
        RunWithPicker((dialog, _, vm, _) =>
        {
            List<string> path = [];
            for (int i = 0; i < 4; i++)
            {
                dialog.KeyTextInput("f");
                Pump(dialog);
                path.Add(string.Join(",", SelectedNames(vm)));
            }

            Assert.AreSequenceEqual(["fxa.txt", "fxyb.txt", "fxyc.txt", "fz.txt"], path);
        });
    }

    [TestMethod]
    public void TypingReplacesAnEarlierMultiSelection()
    {
        RunWithPicker((dialog, _, vm, _) =>
        {
            vm.SelectedEntries.Add(vm.Entries.Single(entry => entry.Name == "a000.txt"));
            vm.SelectedEntries.Add(vm.Entries.Single(entry => entry.Name == "a001.txt"));
            Pump(dialog);

            dialog.KeyTextInput("f");
            dialog.KeyTextInput("z");
            Pump(dialog);

            Assert.AreSequenceEqual(["fz.txt"], SelectedNames(vm));
        });
    }

    [TestMethod]
    public void TypingAFolderName_LocatesTheFolderToo()
    {
        RunWithPicker((dialog, _, vm, _) =>
        {
            dialog.KeyTextInput("s");
            Pump(dialog);

            Assert.AreSequenceEqual(["sub"], SelectedNames(vm));
        });
    }

    [TestMethod]
    public void EnterOnALocatedFolder_OpensIt_InsteadOfUploadingIt()
    {
        RunWithPicker((dialog, _, vm, tempRoot) =>
        {
            bool closed = false;
            dialog.Closed += (_, _) => closed = true;

            dialog.KeyTextInput("s");
            Pump(dialog);
            Press(dialog, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
            PumpWhile(dialog, () => vm.CurrentPath != Path.Combine(tempRoot, "sub") || FocusedEntry(dialog) is null);

            Assert.AreEqual(Path.Combine(tempRoot, "sub"), vm.CurrentPath, "只选着一个文件夹时回车与双击一样:进去。");
            Assert.IsFalse(closed);
            Assert.IsNotNull(FocusedEntry(dialog), "进去之后焦点在新列表上,接着能按方向键。");
            Assert.IsEmpty(vm.SelectedEntries);
        });
    }

    // ---- 手动输入路径 ----

    [TestMethod]
    public void CtrlL_OpensThePathBox_Focused_AndTypingThereDoesNotMoveTheListSelection()
    {
        RunWithPicker((dialog, _, vm, _) =>
        {
            Press(dialog, Key.L, PhysicalKey.L, RawInputModifiers.Control);
            Pump(dialog);

            TextBox box = PathBox(dialog);
            Assert.IsTrue(vm.IsPathEditing);
            Assert.IsTrue(box.IsVisible);
            Assert.IsTrue(box.IsFocused, "切出来的输入框要直接能打字。");

            dialog.KeyTextInput("fx");
            Pump(dialog);

            Assert.AreEqual("fx", vm.PathEditText, "预填的路径是全选的,敲字即整条替换。");
            Assert.IsEmpty(vm.SelectedEntries, "在路径框里打的字不是键入定位。");
        });
    }

    [TestMethod]
    public void EnteringAFolderPath_NavigatesThere_WithoutSubmittingTheDialog()
    {
        RunWithPicker((dialog, _, vm, tempRoot) =>
        {
            bool closed = false;
            dialog.Closed += (_, _) => closed = true;

            // 先选着一项,让「上传」按钮可按:回车要是漏到默认按钮上,窗口就会带着它关掉。
            vm.SelectedEntries.Add(vm.Entries.Single(entry => entry.Name == "a000.txt"));
            Press(dialog, Key.L, PhysicalKey.L, RawInputModifiers.Control);
            Pump(dialog);
            dialog.KeyTextInput(Path.Combine(tempRoot, "sub"));
            Press(dialog, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
            PumpWhile(dialog, () => vm.IsPathEditing);

            Assert.AreEqual(Path.Combine(tempRoot, "sub"), vm.CurrentPath);
            Assert.IsFalse(vm.IsPathEditing);
            Assert.IsFalse(closed, "路径框里的回车只管跳转,不能顺带把选择器提交掉。");
            Assert.IsTrue(dialog.FileList.IsKeyboardFocusWithin, "跳转后焦点回到列表,接着就能按方向键。");
            Assert.IsEmpty(vm.SelectedEntries, "把焦点给行不等于选中它。");
        });
    }

    [TestMethod]
    public void EnteringAFilePath_OpensItsFolder_AndSelectsAndRevealsTheFile()
    {
        RunWithPicker((dialog, list, vm, tempRoot) =>
        {
            PumpUntil(vm.NavigateToAsync(Path.Combine(tempRoot, "sub")));
            Pump(dialog);

            Press(dialog, Key.L, PhysicalKey.L, RawInputModifiers.Control);
            Pump(dialog);
            // 资源管理器「复制为路径」拷出来的带引号。
            dialog.KeyTextInput($"\"{Path.Combine(tempRoot, "fxyc.txt")}\"");
            Press(dialog, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
            PumpWhile(dialog, () => vm.IsPathEditing || FocusedEntry(dialog) is null);

            Assert.AreEqual(tempRoot, vm.CurrentPath);
            Assert.AreSequenceEqual(["fxyc.txt"], SelectedNames(vm));
            Assert.IsTrue(IsInViewport(list, vm.SelectedEntries.Single()), "选中的文件在列表很靠下,要滚进视口。");
            Assert.AreEqual("fxyc.txt", FocusedEntry(dialog)?.Name);

            // 输入框已收起,这时的回车才是「上传」:贴路径、回车、再回车,一气呵成。
            bool closed = false;
            dialog.Closed += (_, _) => closed = true;
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(closed, "选中文件后再按回车应直接确认上传。");
        });
    }

    [TestMethod]
    public void EscInThePathBox_OnlyClosesTheBox_NotTheDialog()
    {
        RunWithPicker((dialog, _, vm, tempRoot) =>
        {
            bool closed = false;
            dialog.Closed += (_, _) => closed = true;

            Press(dialog, Key.L, PhysicalKey.L, RawInputModifiers.Control);
            Pump(dialog);
            dialog.KeyTextInput("somewhere-else");
            Press(dialog, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
            Pump(dialog);

            Assert.IsFalse(vm.IsPathEditing);
            Assert.AreEqual(tempRoot, vm.CurrentPath, "Esc 是放弃,不跳转。");
            Assert.IsFalse(closed, "路径框里的 Esc 只收起输入框;再按一次才关选择器。");

            // 只按下不抬起:这一下就把窗口关了,再往已关闭的窗口送抬起会抛 ObjectDisposedException。
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(closed);
        });
    }

    [TestMethod]
    public void ClickingTheBlankAfterTheBreadcrumbs_OpensThePathBox_ButClickingACrumbDoesNot()
    {
        RunWithPicker((dialog, _, vm, tempRoot) =>
        {
            // 退到盘根:临时目录的面包屑很长,会把整条路径栏占满,没有空白可点。
            PumpUntil(vm.NavigateToAsync(Path.GetPathRoot(tempRoot)!));
            Pump(dialog);
            Border area = dialog.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PathCrumbArea");
            Button rootCrumb = area.GetVisualDescendants().OfType<Button>()
                .First(button => Equals(button.Content, vm.RootBreadcrumbLabel));

            Click(dialog, rootCrumb.TranslatePoint(new(rootCrumb.Bounds.Width / 2, rootCrumb.Bounds.Height / 2), dialog));
            Assert.IsFalse(vm.IsPathEditing, "面包屑按钮是导航,不该顺带切成输入框。");

            Click(dialog, area.TranslatePoint(new(area.Bounds.Width - 4, area.Bounds.Height / 2), dialog));
            Assert.IsTrue(vm.IsPathEditing, "点面包屑后面的空白应切成输入框。");
            Assert.IsTrue(PathBox(dialog).IsFocused);
        });
    }

    // ---- 工具 ----

    private static string[] SelectedNames(LocalFilePaneViewModel vm) =>
        [.. vm.SelectedEntries.Select(entry => entry.Name)];

    private static TextBox PathBox(Window dialog) =>
        dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PathEditBox");

    private static LocalFileEntry? FocusedEntry(Window dialog) =>
        (dialog.FocusManager?.GetFocusedElement() as Visual)
            ?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as LocalFileEntry;

    /// <summary>该行的容器存在,且整行落在列表的可见范围里。</summary>
    private static bool IsInViewport(ListBox list, LocalFileEntry entry)
    {
        if (list.ContainerFromItem(entry) is not { } container
            || container.TranslatePoint(default, list) is not { } top)
        {
            return false;
        }
        return top.Y >= 0 && top.Y + container.Bounds.Height <= list.Bounds.Height + 0.5;
    }

    private static void Press(Window dialog, Key key, PhysicalKey physical, RawInputModifiers modifiers)
    {
        dialog.KeyPress(key, modifiers, physical, null);
        dialog.KeyRelease(key, modifiers, physical, null);
        Pump(dialog);
    }

    private static void Click(Window dialog, Point? point)
    {
        Point at = point ?? throw new InvalidOperationException("坐标换算失败。");
        dialog.MouseDown(at, MouseButton.Left);
        dialog.MouseUp(at, MouseButton.Left);
        Pump(dialog);
    }

    private static void Pump(Window dialog)
    {
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>目录列举在线程池上完成、续体回 UI 线程:边泵边等,最多两秒。</summary>
    private static void PumpWhile(Window dialog, Func<bool> condition)
    {
        for (int i = 0; i < 2000 && condition(); i++)
        {
            Pump(dialog);
            Thread.Sleep(1);
        }
        Pump(dialog);
    }

    /// <summary>
    /// 建一个临时目录:子目录 sub,90 个 aNNN.txt 垫在前面,再是 fxa / fxyb / fxyc / fz ——
    /// f 开头的几个排在列表很靠下,不滚动是看不见的。在其中开选择器,跑完 body 后务必关窗。
    /// </summary>
    private static void RunWithPicker(Action<LocalPathPickerDialog, ListBox, LocalFilePaneViewModel, string> body) =>
        OnUi(() =>
        {
            string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"vela-typeahead-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "sub"));
            File.WriteAllText(Path.Combine(tempRoot, "sub", "inner.txt"), "x");
            for (int i = 0; i < 90; i++)
            {
                File.WriteAllText(Path.Combine(tempRoot, $"a{i:000}.txt"), "x");
            }
            foreach (string name in new[] { "fxa.txt", "fxyb.txt", "fxyc.txt", "fz.txt" })
            {
                File.WriteAllText(Path.Combine(tempRoot, name), "x");
            }

            var vm = UnwatchedLocalFileSystem.NewPane();
            PumpUntil(vm.NavigateToAsync(tempRoot));

            var dialog = new LocalPathPickerDialog(vm, loadInitial: false);
            try
            {
                dialog.Show();
                Pump(dialog);

                ListBox list = dialog.FileList;
                Assert.HasCount(1 + 1 + 90 + 4, vm.Entries, "应为 \"..\"、sub、90 个 a 文件和 4 个 f 文件。");
                Assert.IsFalse(IsInViewport(list, vm.Entries.Single(entry => entry.Name == "fxyc.txt")),
                    "前提:fxyc.txt 起初不在视口里,否则测不出滚动。");

                body(dialog, list, vm, tempRoot);
            }
            finally
            {
                // 不关窗整个 headless 套件会永久卡死;先停掉目录监视再删目录(见 LocalPathPickerMarqueeUiTests)。
                dialog.Close();
                Dispatcher.UIThread.RunJobs();
                vm.Dispose();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        });

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static void PumpUntil(Task task)
    {
        for (int i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
}
