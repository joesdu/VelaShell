using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using VelaShell.Core.Resources;
using VelaShell.Tests.TestSupport;
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
/// 资源管理器树上分组行的右键菜单接线。
/// </summary>
/// <remarks>实际打开弹出菜单，验证删除命令的绑定，并通过输入框重命名被点击的分组。</remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeGroupMenuUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(SessionTreeGroupMenuUiTests).Assembly
        );

    [TestMethod]
    public Task GroupRow_ContextMenu_RenamesClickedGroup_AndBindsDeleteCommand() =>
        _session.RunOnUiAsync(async () =>
        {
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            var group = new ServerGroup { Id = Guid.NewGuid(), Name = "Production" };
            var session = new SessionProfile
            {
                Id = Guid.NewGuid(),
                Name = "WebServer",
                Host = "web.example.com",
                Username = "admin",
                GroupId = group.Id,
            };
            repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
            repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { session }));

            var viewModel = new SessionTreeViewModel(repository);
            await viewModel.LoadCommand.Execute().FirstAsync();

            var view = new SessionTreeView { DataContext = viewModel };
            var window = new Window { Width = 260, Height = 400, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Border groupRow = view.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Classes.Contains("group")
                                  && border.DataContext is SessionTreeNodeViewModel { IsGroup: true });

            Assert.IsNotNull(groupRow.ContextMenu, "分组行必须有右键菜单,否则根本没有删除分组的入口。");
            IEnumerable<MenuItem> items = groupRow.ContextMenu.Items.OfType<MenuItem>();

            // 打开菜单才会求值绑定:关掉的 ContextMenu 里 Command 恒为 null,直接断言会假通过。
            groupRow.ContextMenu.Open(groupRow);
            Dispatcher.UIThread.RunJobs();

            MenuItem delete = items.Single(i => Equals(i.Header, Strings.Get("Tree_DeleteGroup")));
            Assert.AreSame(viewModel.DeleteGroupCommand, delete.Command,
                           "分组菜单的删除项必须绑到 DeleteGroupCommand。");

            items.Single(i => Equals(i.Header, Strings.Get("Rename"))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            MessageDialog dialog = window.OwnedWindows.OfType<MessageDialog>().Single();
            TextBox input = dialog.FindControl<TextBox>("InputBox")!;
            Assert.AreEqual("Production", input.Text);
            input.Text = "Staging";
            dialog.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("Staging", viewModel.Nodes.Single().Name);
            groupRow.ContextMenu.Close();
            window.Close();
        });
}
