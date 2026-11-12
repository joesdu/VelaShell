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
/// 上传选择框的图标视图(资源管理器「中等图标」):派发真实的点击、拖动、按键与键入,验证
/// 右上角的「列表 | 图标」切换器真能切过去、格子真按二维排开,框选、方向键与键入定位在格子里照常可用。
/// <para>
/// 格子几何由 <see cref="TileGridMathTests" /> 单独盯住;这一层盯的是接线 —— 面板与 ListBox 的选择、
/// 键盘导航、ScrollIntoView 接不接得上,框选的坐标换算对不对。列表视图用的也是同一个面板,
/// 那一侧由 <see cref="LocalPathPickerMarqueeUiTests" /> 与 <see cref="LocalPathPickerKeyboardUiTests" /> 盯着。
/// </para>
/// </summary>
[TestClass]
[TestCategory("PickerIconViewUI")]
public sealed class LocalPathPickerIconViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LocalPathPickerIconViewUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void ClickingTheSwitcher_LaysEntriesOutInAGrid_AndBack_KeepingTheSelection()
    {
        RunWithPicker((dialog, list, vm, _) =>
        {
            vm.SelectedEntries.Add(vm.Entries[3]);
            vm.SelectedEntries.Add(vm.Entries[5]);
            Pump(dialog);

            ClickCenter(dialog, dialog.IconViewButton);

            Assert.AreEqual(LocalPickerView.Icons, dialog.View);
            Assert.IsTrue(dialog.IconViewButton.IsChecked);
            Assert.AreNotEqual(true, dialog.ListViewButton.IsChecked, "切换器两段互斥。");
            Assert.IsFalse(dialog.ColumnHeader.IsVisible, "格子里没有列,列头要收起来。");

            VirtualizingTilePanel panel = Panel(list);
            Assert.IsGreaterThan(1, panel.Columns, "920 宽的窗口里一行应能排下好几格。");
            Rect first = Bounds(list, 1), second = Bounds(list, 2), below = Bounds(list, 1 + panel.Columns);
            Assert.AreEqual(first.Y, second.Y, 0.5, "相邻两项在同一行。");
            Assert.IsGreaterThan(first.X, second.X);
            Assert.AreEqual(first.X, below.X, 0.5, "隔一行的同一列对齐。");
            Assert.IsGreaterThan(first.Y, below.Y);
            Assert.IsGreaterThan(60, first.Height, "格子比列表的行高得多。");

            Assert.AreSequenceEqual([vm.Entries[3], vm.Entries[5]], [.. vm.SelectedEntries], SequenceOrder.InAnyOrder, "切视图不该丢掉选中。");
            Assert.IsTrue(((ListBoxItem)list.ContainerFromIndex(3)!).IsSelected, "重建出来的格子要带上选中态。");

            ClickCenter(dialog, dialog.ListViewButton);

            Assert.AreEqual(LocalPickerView.List, dialog.View);
            Assert.IsTrue(dialog.ColumnHeader.IsVisible);
            Assert.AreEqual(1, panel.Columns, "列表视图一项占满一行。");
            Assert.AreEqual(28, Bounds(list, 1).Height, 0.5);
            Assert.AreSequenceEqual([vm.Entries[3], vm.Entries[5]], [.. vm.SelectedEntries], SequenceOrder.InAnyOrder);
        });
    }

    [TestMethod]
    public void AfterSwitching_FocusIsBackInTheList()
    {
        RunWithPicker((dialog, _, vm, _) =>
        {
            ClickCenter(dialog, dialog.IconViewButton);

            Assert.IsNotNull(FocusedEntry(dialog), "点完切换器接着按方向键、敲字,走的应是文件而不是切换器。");
            Assert.IsEmpty(vm.SelectedEntries, "把焦点给格子不等于选中它。");
        });
    }

    [TestMethod]
    public void MarqueeInIcons_SelectsTheRectangle_NotWholeRows()
    {
        RunWithPicker((dialog, list, vm, _) =>
        {
            dialog.View = LocalPickerView.Icons;
            Pump(dialog);
            int columns = Panel(list).Columns;

            // 从第 2 格拖到下一行的第 3 格:框住 2×2 四格,而不是两整行。
            Drag(dialog, TileCenter(dialog, list, 2), TileCenter(dialog, list, columns + 3));

            string[] expected = [.. new[] { 2, 3, columns + 2, columns + 3 }.Select(i => vm.Entries[i].Name)];
            Assert.AreSequenceEqual(expected, SelectedNames(vm), SequenceOrder.InAnyOrder);
        });
    }

    [TestMethod]
    public void ArrowKeysInIcons_StepAcrossTheRow_AndJumpAWholeRowUpAndDown()
    {
        RunWithPicker((dialog, list, vm, _) =>
        {
            dialog.View = LocalPickerView.Icons;
            Pump(dialog);
            int columns = Panel(list).Columns;
            Click(dialog, TileCenter(dialog, list, 2));
            Assert.AreSequenceEqual([vm.Entries[2].Name], SelectedNames(vm));

            Press(dialog, Key.Right);
            Assert.AreSequenceEqual([vm.Entries[3].Name], SelectedNames(vm), "右键走到同一行的下一格。");

            Press(dialog, Key.Down);
            Assert.AreSequenceEqual([vm.Entries[3 + columns].Name], SelectedNames(vm), "下键跳一整行、列不变。");

            Press(dialog, Key.Up);
            Assert.AreSequenceEqual([vm.Entries[3].Name], SelectedNames(vm));
        });
    }

    [TestMethod]
    public void TypingInIcons_ScrollsTheMatchIntoView_AndMovesFocusThere()
    {
        RunWithPicker((dialog, list, vm, _) =>
        {
            dialog.View = LocalPickerView.Icons;
            Pump(dialog);
            Assert.IsFalse(IsInViewport(list, vm.Entries.Single(entry => entry.Name == "fxyc.txt")),
                "前提:fxyc.txt 起初不在视口里,否则测不出滚动。");

            foreach (char key in "fxyc")
            {
                dialog.KeyTextInput(key.ToString());
                Pump(dialog);
            }

            Assert.AreSequenceEqual(["fxyc.txt"], SelectedNames(vm));
            Assert.IsTrue(IsInViewport(list, vm.SelectedEntries.Single()), "视口外那一格要实现出来并滚进视口。");
            Assert.AreEqual("fxyc.txt", FocusedEntry(dialog)?.Name);
        });
    }

    [TestMethod]
    public void EnterOnAFolderTile_OpensIt()
    {
        RunWithPicker((dialog, list, vm, tempRoot) =>
        {
            dialog.View = LocalPickerView.Icons;
            Pump(dialog);
            Click(dialog, TileCenter(dialog, list, 1));
            Assert.AreSequenceEqual(["sub"], SelectedNames(vm));

            Press(dialog, Key.Enter);
            PumpWhile(dialog, () => vm.CurrentPath != Path.Combine(tempRoot, "sub"));

            Assert.AreEqual(Path.Combine(tempRoot, "sub"), vm.CurrentPath);
            Assert.AreEqual(LocalPickerView.Icons, dialog.View, "进了文件夹还是图标视图。");
        });
    }

    // ---- 工具 ----

    private static string[] SelectedNames(LocalFilePaneViewModel vm) =>
        [.. vm.SelectedEntries.Select(entry => entry.Name)];

    private static VirtualizingTilePanel Panel(ListBox list) =>
        list.ItemsPanelRoot as VirtualizingTilePanel
        ?? throw new InvalidOperationException("文件列表的面板应是 VirtualizingTilePanel。");

    /// <summary>第 <paramref name="index" /> 项的容器在列表坐标里的位置与尺寸。</summary>
    private static Rect Bounds(ListBox list, int index)
    {
        Control container = list.ContainerFromIndex(index)
                            ?? throw new InvalidOperationException($"第 {index} 项没有实现出容器。");
        Point topLeft = container.TranslatePoint(default, list) ?? throw new InvalidOperationException("坐标换算失败。");
        return new(topLeft, container.Bounds.Size);
    }

    private static Point TileCenter(Window dialog, ListBox list, int index)
    {
        Control container = list.ContainerFromIndex(index)
                            ?? throw new InvalidOperationException($"第 {index} 项没有实现出容器。");
        return container.TranslatePoint(new(container.Bounds.Width / 2, container.Bounds.Height / 2), dialog)
               ?? throw new InvalidOperationException("坐标换算失败。");
    }

    private static LocalFileEntry? FocusedEntry(Window dialog) =>
        (dialog.FocusManager?.GetFocusedElement() as Visual)
            ?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as LocalFileEntry;

    /// <summary>该项的容器存在,且整格落在列表的可见范围里。</summary>
    private static bool IsInViewport(ListBox list, LocalFileEntry entry)
    {
        if (list.ContainerFromItem(entry) is not { } container
            || container.TranslatePoint(default, list) is not { } top)
        {
            return false;
        }
        return top.Y >= 0 && top.Y + container.Bounds.Height <= list.Bounds.Height + 0.5;
    }

    private static void ClickCenter(Window dialog, Control control) =>
        Click(dialog, control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), dialog)
                      ?? throw new InvalidOperationException("坐标换算失败。"));

    private static void Click(Window dialog, Point at)
    {
        dialog.MouseDown(at, MouseButton.Left);
        dialog.MouseUp(at, MouseButton.Left);
        Pump(dialog);
    }

    /// <summary>按住左键从 <paramref name="start" /> 分几步拖到 <paramref name="end" /> 再松手。</summary>
    private static void Drag(Window dialog, Point start, Point end)
    {
        dialog.MouseDown(start, MouseButton.Left);
        Pump(dialog);
        for (int step = 1; step <= 4; step++)
        {
            dialog.MouseMove(new(
                start.X + ((end.X - start.X) * step / 4),
                start.Y + ((end.Y - start.Y) * step / 4)
            ));
            Pump(dialog);
        }
        dialog.MouseUp(end, MouseButton.Left);
        Pump(dialog);
    }

    private static void Press(Window dialog, Key key)
    {
        PhysicalKey physical = key switch
        {
            Key.Right => PhysicalKey.ArrowRight,
            Key.Left => PhysicalKey.ArrowLeft,
            Key.Up => PhysicalKey.ArrowUp,
            Key.Down => PhysicalKey.ArrowDown,
            Key.Enter => PhysicalKey.Enter,
            _ => PhysicalKey.None
        };
        dialog.KeyPress(key, RawInputModifiers.None, physical, null);
        dialog.KeyRelease(key, RawInputModifiers.None, physical, null);
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
    /// 建一个临时目录:子目录 sub,90 个 aNNN.txt,再是 fxa / fxyb / fxyc / fz —— 图标视图里也排出好几屏,
    /// f 开头的几个不滚动看不见。在其中开选择器,跑完 body 后务必关窗。
    /// </summary>
    private static void RunWithPicker(Action<LocalPathPickerDialog, ListBox, LocalFilePaneViewModel, string> body) =>
        OnUi(() =>
        {
            string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"vela-iconview-{Guid.NewGuid():N}"));
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

            LocalFilePaneViewModel vm = UnwatchedLocalFileSystem.NewPane();
            PumpUntil(vm.NavigateToAsync(tempRoot));

            var dialog = new LocalPathPickerDialog(vm, loadInitial: false);
            try
            {
                dialog.Show();
                Pump(dialog);

                Assert.HasCount(1 + 1 + 90 + 4, vm.Entries, "应为 \"..\"、sub、90 个 a 文件和 4 个 f 文件。");
                Assert.AreEqual("sub", vm.Entries[1].Name, "目录排在文件前面。");
                Assert.AreEqual(LocalPickerView.List, dialog.View, "默认是列表视图。");

                body(dialog, dialog.FileList, vm, tempRoot);
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
