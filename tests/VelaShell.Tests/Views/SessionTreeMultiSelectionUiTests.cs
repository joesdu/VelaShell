using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 资源管理器多选的界面一半:真的按住 Ctrl / Shift 点下去,列表不会按它自己的单选规则把选择改回去;
/// 右键多选里的行弹的是多选专用菜单,右键别的行弹回原菜单。
/// </summary>
/// <remarks>
/// 视图模型侧的规则由 <c>SessionTreeMultiSelectionTests</c> 覆盖。这里守的是接线:
/// 列表控件本身是单选的,Ctrl / Shift 单击若漏到它手里,会被当成"切换这一项的选中",多选当场就散了 ——
/// 这种事只有走一遍真实的指针输入才看得出来。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeMultiSelectionUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionTreeMultiSelectionUiTests).Assembly);

    [TestMethod]
    public void CtrlClick_PairsTwoRows_AndTheirRightClickOpensTheMultiMenu()
    {
        _session.Dispatch(async () =>
        {
            (SessionTreeViewModel viewModel, SessionTreeView view, Window window) = await ShowTreeAsync();
            Border alpha = Row(view, "alpha");
            Border beta = Row(view, "beta");
            Border gamma = Row(view, "gamma");
            ContextMenu? ownMenu = beta.ContextMenu;

            Click(window, alpha, MouseButton.Left, RawInputModifiers.None);
            Click(window, beta, MouseButton.Left, RawInputModifiers.Control);

            Assert.HasCount(2, viewModel.MultiSelection, "Ctrl 单击第二行必须组成多选,而不是被列表改成单选。");
            Assert.AreEqual("alpha", viewModel.MultiSelection[0].Name);
            Assert.AreEqual("beta", viewModel.MultiSelection[1].Name);
            Assert.IsTrue(alpha.Classes.Contains("multimarked"));
            Assert.IsTrue(beta.Classes.Contains("multimarked"));

            Click(window, beta, MouseButton.Right, RawInputModifiers.None);
            Assert.HasCount(2, viewModel.MultiSelection, "右键多选里的行不能把多选打散。");
            Assert.IsNotNull(beta.ContextMenu);
            Assert.AreNotSame(ownMenu, beta.ContextMenu, "右键多选里的行要弹多选专用菜单。");
            beta.ContextMenu.Open(beta);
            Dispatcher.UIThread.RunJobs();
            SaveOptionalFrame(window, "session-tree-multiselect.png");
            if (TopLevel.GetTopLevel(beta.ContextMenu) is { } menuRoot && !ReferenceEquals(menuRoot, window))
            {
                SaveOptionalFrame(menuRoot, "session-tree-multiselect-menu.png");
            }
            List<MenuItem> items = [.. beta.ContextMenu.Items.OfType<MenuItem>()];
            Assert.AreSame(viewModel.OpenSelectedCommand, items[0].Command, "多选菜单的第一项是「打开 N 个连接」。");
            MenuItem dual = items.Single(item => ReferenceEquals(item.Command, viewModel.OpenDualSftpCommand));
            Assert.IsTrue(dual.IsVisible, "恰好两条时有「在双栏 SFTP 中打开」。");
            Assert.Contains(item => ReferenceEquals(item.Command, viewModel.DeleteSelectedCommand), items);
            beta.ContextMenu.Close();

            // 右键多选之外的行:多选结束,那一行弹它自己的原菜单;多选里那行也换回原菜单。
            Click(window, gamma, MouseButton.Right, RawInputModifiers.None);
            Assert.IsEmpty(viewModel.MultiSelection);
            Assert.DoesNotContain(item => ReferenceEquals(item.Command, viewModel.OpenSelectedCommand), gamma.ContextMenu!.Items.OfType<MenuItem>());
            Click(window, beta, MouseButton.Right, RawInputModifiers.None);
            Assert.AreSame(ownMenu, beta.ContextMenu);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ShiftClick_SelectsTheRange_AndThreeRowsHideTheDualSftpItem()
    {
        _session.Dispatch(async () =>
        {
            (SessionTreeViewModel viewModel, SessionTreeView view, Window window) = await ShowTreeAsync();
            Border alpha = Row(view, "alpha");
            Border gamma = Row(view, "gamma");

            Click(window, alpha, MouseButton.Left, RawInputModifiers.None);
            Click(window, gamma, MouseButton.Left, RawInputModifiers.Shift);

            Assert.AreSequenceEqual(new[] { "alpha", "beta", "gamma" }, viewModel.MultiSelection.Select(static n => n.Name).ToArray(),
                "Shift 单击选中起点到这一行之间的全部行,不能被列表改回单选。");
            Assert.IsTrue(Row(view, "beta").Classes.Contains("multimarked"));

            Click(window, gamma, MouseButton.Right, RawInputModifiers.None);
            gamma.ContextMenu!.Open(gamma);
            Dispatcher.UIThread.RunJobs();
            MenuItem dual = gamma.ContextMenu.Items.OfType<MenuItem>().Single(item => ReferenceEquals(item.Command, viewModel.OpenDualSftpCommand));
            Assert.IsFalse(dual.IsVisible, "三条时双栏那一项不出现。");
            gamma.ContextMenu.Close();

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void PlainClick_EndsTheMultiSelection()
    {
        _session.Dispatch(async () =>
        {
            (SessionTreeViewModel viewModel, SessionTreeView view, Window window) = await ShowTreeAsync();
            Click(window, Row(view, "alpha"), MouseButton.Left, RawInputModifiers.None);
            Click(window, Row(view, "beta"), MouseButton.Left, RawInputModifiers.Control);
            Assert.HasCount(2, viewModel.MultiSelection);

            Click(window, Row(view, "beta"), MouseButton.Left, RawInputModifiers.None);

            Assert.IsEmpty(viewModel.MultiSelection);
            Assert.AreEqual("beta", viewModel.SelectedNode?.Name);
            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task<(SessionTreeViewModel, SessionTreeView, Window)> ShowTreeAsync()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>
        {
            new() { Id = Guid.NewGuid(), Name = "alpha", Host = "a.example.com", Username = "u", ConnectionType = ConnectionType.SSH },
            new() { Id = Guid.NewGuid(), Name = "beta", Host = "b.example.com", Username = "u", ConnectionType = ConnectionType.SFTP },
            new() { Id = Guid.NewGuid(), Name = "gamma", Host = "c.example.com", Username = "u", ConnectionType = ConnectionType.FTP },
        }));
        var viewModel = new SessionTreeViewModel(repository);
        await viewModel.LoadCommand.Execute().FirstAsync();
        var view = new SessionTreeView { DataContext = viewModel };
        var window = new Window { Width = 260, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (viewModel, view, window);
    }

    private static Border Row(SessionTreeView view, string name) =>
        view.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Classes.Contains("session")
                              && border.DataContext is SessionTreeNodeViewModel { IsGroup: false } node
                              && node.Name == name);

    /// <summary>设了 <c>VELASHELL_VISUAL_QA_DIR</c> 时存一帧画面供人眼核对(同 DockPaneUiTests)。</summary>
    private static void SaveOptionalFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using Avalonia.Media.Imaging.WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static void Click(Window window, Border row, MouseButton button, RawInputModifiers modifiers)
    {
        Point at = row.TranslatePoint(new Point(40, row.Bounds.Height / 2), window)
                   ?? throw new InvalidOperationException("row is not in the window");
        window.MouseDown(at, button, modifiers);
        window.MouseUp(at, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }
}
