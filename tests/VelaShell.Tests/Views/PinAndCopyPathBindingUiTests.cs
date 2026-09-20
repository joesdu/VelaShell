using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// #474 新加的两个入口确实接上了线:会话行右键的「置顶」、SFTP 路径栏的「复制当前路径」。
/// </summary>
/// <remarks>
/// 视图模型那一半由 <c>SessionTreePinAndCollapseTests</c> 覆盖,这里守的是 XAML 侧 ——
/// 这两处都是反射绑定(<c>$parent[ListBox]</c> 穿弹出层、DataContext 上的命令名),
/// 名字写错不会有任何编译错误,只会在用户点下去时"点了没反应"。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class PinAndCopyPathBindingUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(PinAndCopyPathBindingUiTests).Assembly
        );

    [TestMethod]
    public void SessionRow_ContextMenu_BindsTogglePinCommand_AndShowsTheCurrentLabel()
    {
        OnUi(async () =>
        {
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            var session = new SessionProfile
            {
                Id = Guid.NewGuid(),
                Name = "WebServer",
                Host = "web.example.com",
                Username = "admin",
            };
            repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
            repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { session }));

            var viewModel = new SessionTreeViewModel(repository);
            await viewModel.LoadCommand.Execute().FirstAsync();

            var view = new SessionTreeView { DataContext = viewModel };
            var window = new Window { Width = 260, Height = 400, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Border row = view.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Classes.Contains("session")
                                  && border.DataContext is SessionTreeNodeViewModel { IsGroup: false });
            Assert.IsNotNull(row.ContextMenu);

            // 打开菜单才会求值绑定:关着的 ContextMenu 里 Command 恒为 null,直接断言会假通过。
            row.ContextMenu.Open(row);
            Dispatcher.UIThread.RunJobs();

            MenuItem pin = row.ContextMenu.Items
                .OfType<MenuItem>()
                .Single(item => ReferenceEquals(item.Command, viewModel.TogglePinCommand));
            var node = (SessionTreeNodeViewModel)row.DataContext!;
            Assert.AreEqual(node.PinToggleText, pin.Header, "菜单文案要跟着当前置顶态走。");

            row.ContextMenu.Close();
            window.Close();
            return true;
        });
    }

    /// <summary>
    /// 路径栏右侧那枚复制按钮:用户此前必须先点铅笔进编辑态才能复制路径(#474 的第 5 条)。
    /// </summary>
    [TestMethod]
    public void SftpPathBar_HasACopyButton_BoundToCopyCurrentPathCommand()
    {
        OnUi(async () =>
        {
            ISftpService sftp = Substitute.For<ISftpService>();
            var viewModel = new FileBrowserViewModel(sftp, Guid.NewGuid())
            {
                IsVisible = true,
                CurrentPath = "/var/www/html",
            };
            var view = new FileBrowserView { DataContext = viewModel };
            var window = new Window { Width = 640, Height = 200, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Button copy = view.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, viewModel.CopyCurrentPathCommand));

            Assert.IsTrue(copy.IsVisible, "复制按钮要和面包屑一起显示,而不是只在编辑态下才出现。");
            Assert.IsTrue(copy.IsEffectivelyEnabled);

            window.Close();
            await Task.CompletedTask;
            return true;
        });
    }

    /// <summary>
    /// 带返回值的重载:<c>HeadlessUnitTestSession</c> 没有 <c>Func&lt;Task&gt;</c> 重载,
    /// 写成无返回值会拿到一个从未被等待的 <c>Task&lt;Task&gt;</c> —— 测试跑到第一个 await
    /// 就"通过",后面的断言全部丢失。
    /// </summary>
    private static void OnUi(Func<Task<bool>> body) =>
        _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();
}
