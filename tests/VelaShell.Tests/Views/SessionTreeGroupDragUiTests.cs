using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
/// 拖动分组行重排(#571)的视觉提示:插入线画在分组之间的那条缝上,幽灵标签写明落点;落回原处不画线。
/// </summary>
/// <remarks>
/// 拖放事件在无头测试里造不出来,与 <c>SessionTreeDragFeedbackUiTests</c> 同一个做法:直接调视图的反馈方法,
/// 量真实排布出来的位置。落点规则本身在 <c>SessionTreeGroupOperationsTests</c> 里。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeGroupDragUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionTreeGroupDragUiTests).Assembly);

    [TestMethod]
    public void InsertLine_SitsOnTheGapBetweenGroups_AndHidesForANoOpDrop()
    {
        _session.Dispatch(async () =>
        {
            var first = new ServerGroup { Id = Guid.NewGuid(), Name = "first", SortOrder = 0 };
            var second = new ServerGroup { Id = Guid.NewGuid(), Name = "second", SortOrder = 1 };
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { first, second }));
            repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>
            {
                new() { Name = "a", Host = "a", GroupId = first.Id },
                new() { Name = "b", Host = "b", GroupId = second.Id }
            }));
            var viewModel = new SessionTreeViewModel(repository);
            await viewModel.LoadCommand.Execute().FirstAsync();
            var view = new SessionTreeView { DataContext = viewModel };
            var window = new Window { Width = 260, Height = 400, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Border line = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GroupInsertLine");
            Control overlay = view.GetVisualDescendants().OfType<Canvas>().Single(c => c.Name == "DragOverlay");
            Border secondRow = view.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("group") && b.DataContext is SessionTreeNodeViewModel { Name: "second" });
            double secondTop = secondRow.TranslatePoint(new Point(0, 0), overlay)!.Value.Y;

            view.ShowGroupDragFeedback("first", slot: 1, moves: false, new Point(20, 20));
            Assert.IsFalse(line.IsVisible, "落回原处不画线");

            view.ShowGroupDragFeedback("first", slot: 2, moves: true, new Point(20, 20));
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(line.IsVisible);
            Assert.IsGreaterThan(secondTop, Canvas.GetTop(line), "排到最后:线在最后一组(连同展开的会话)的下面");

            view.ShowGroupDragFeedback("second", slot: 0, moves: true, new Point(20, 20));
            Dispatcher.UIThread.RunJobs();
            Border firstRow = view.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("group") && b.DataContext is SessionTreeNodeViewModel { Name: "first" });
            double firstTop = firstRow.TranslatePoint(new Point(0, 0), overlay)!.Value.Y;
            Assert.AreEqual(Math.Max(0, firstTop - 1), Canvas.GetTop(line), 1.5, "插到第一组前面:线在第一组标题行的上沿");
            TextBlock ghost = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DragGhostText");
            Assert.Contains("first", ghost.Text!);

            view.ClearDragFeedback();
            Assert.IsFalse(line.IsVisible);
            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
