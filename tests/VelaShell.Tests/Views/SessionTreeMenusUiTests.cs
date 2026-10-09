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
/// #571 新增的两份菜单接线:分组行右键菜单的每一项都绑到会话树视图模型上对应的命令,
/// 侧栏「更多」菜单的每一项绑到会话树的命令(会话树由宿主后建,菜单靠 <c>SessionTree.</c> 前缀取)。
/// </summary>
/// <remarks>
/// 菜单是弹出层,编译期绑定写错路径不报错,只会在运行时静默绑成 null —— 点了没反应。
/// 视图模型侧的行为由 <c>SessionTreeGroupOperationsTests</c> 覆盖,这里只守「点得到」。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeMenusUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionTreeMenusUiTests).Assembly);

    private static async Task<SessionTreeViewModel> LoadTreeAsync()
    {
        var first = new ServerGroup { Name = "first", SortOrder = 0 };
        var second = new ServerGroup { Name = "second", SortOrder = 1 };
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { first, second }));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>
        {
            new() { Name = "a", Host = "a", GroupId = first.Id },
            new() { Name = "b", Host = "b", GroupId = second.Id }
        }));
        var viewModel = new SessionTreeViewModel(repository);
        await viewModel.LoadCommand.Execute().FirstAsync();
        return viewModel;
    }

    [TestMethod]
    public void GroupRowMenu_BindsEveryItemToTheTreeCommands()
    {
        _session.Dispatch(async () =>
        {
            SessionTreeViewModel viewModel = await LoadTreeAsync();
            var view = new SessionTreeView { DataContext = viewModel };
            var window = new Window { Width = 260, Height = 400, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Border secondRow = view.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("group") && b.DataContext is SessionTreeNodeViewModel { Name: "second" });

            // 右键先选中所指分组(菜单命令都作用于 SelectedNode)。
            Point at = secondRow.TranslatePoint(new Point(40, secondRow.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("second", viewModel.SelectedNode?.Name);

            ContextMenu menu = secondRow.ContextMenu!;
            menu.Open(secondRow);
            Dispatcher.UIThread.RunJobs();
            List<object?> commands = [.. menu.Items.OfType<MenuItem>().Select(static item => (object?)item.Command)];
            foreach (object command in (object[])
                     [
                         viewModel.OpenGroupCommand, viewModel.MoveGroupUpCommand, viewModel.MoveGroupDownCommand,
                         viewModel.ExportGroupCommand, viewModel.ImportIntoGroupCommand, viewModel.DeleteGroupCommand
                     ])
            {
                Assert.Contains(command, commands, "分组菜单少了一项,或者绑定路径写错成了 null");
            }
            MenuItem down = menu.Items.OfType<MenuItem>().Single(item => ReferenceEquals(item.Command, viewModel.MoveGroupDownCommand));
            Assert.IsFalse(down.IsEffectivelyEnabled, "最后一组「下移」是灰的");
            MenuItem up = menu.Items.OfType<MenuItem>().Single(item => ReferenceEquals(item.Command, viewModel.MoveGroupUpCommand));
            Assert.IsTrue(up.IsEffectivelyEnabled);
            menu.Close();

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SidebarMoreMenu_BindsToTheSessionTreeCommands()
    {
        _session.Dispatch(async () =>
        {
            SessionTreeViewModel tree = await LoadTreeAsync();
            var viewModel = new SidebarViewModel { SessionTree = tree };
            var view = new SidebarView { DataContext = viewModel };
            var window = new Window { Width = 260, Height = 600, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Button more = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ExplorerMoreButton");
            var flyout = (MenuFlyout)more.Flyout!;
            flyout.ShowAt(more);
            Dispatcher.UIThread.RunJobs();
            List<object?> commands = [.. flyout.Items.OfType<MenuItem>().Select(static item => (object?)item.Command)];
            foreach (object command in (object[])
                     [
                         tree.ImportFileCommand, tree.ExportAllCommand, tree.SaveCsvTemplateCommand,
                         tree.SortGroupsByNameCommand, tree.CollapseAllCommand, tree.ExpandAllCommand
                     ])
            {
                Assert.Contains(command, commands, "「更多」菜单少了一项,或者绑定路径写错成了 null");
            }
            Assert.HasCount(7, flyout.Items.OfType<MenuItem>().ToList(), "六项命令 + 原有的「从其他工具导入会话」");
            flyout.Hide();

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
