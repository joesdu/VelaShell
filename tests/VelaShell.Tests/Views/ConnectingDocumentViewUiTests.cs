using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls.Controls;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Localization;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 「连接中」占位视图真的画得出来。
/// <para>
/// 这一条必须**渲染**而不是只读视图模型:编译期的 AXAML 校验拦不住动态资源缺失
/// (<c>{StaticResource Icon.*}</c> 写错一个键、令牌改名)—— 那些要到真正加载这张
/// 界面时才炸,而它恰好只在"连接慢"的时候才会被看到,平时点一下就过去了,
/// 最容易带着一个加载不出来的加载界面发版。
/// </para>
/// <para>
/// body 必须同步 + <c>return Task.CompletedTask</c> —— 传 async lambda 会绑到
/// <c>Dispatch(Func&lt;TResult&gt;)</c>,断言一条都不会跑却全绿。
/// </para>
/// </summary>
[TestClass]
[TestCategory("UI")]
public sealed class ConnectingDocumentViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ConnectingDocumentViewUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void ConnectingState_ShowsASpinningRingAndTheSessionName()
    {
        OnUi(() =>
        {
            var document = new ConnectingDocument(
                new() { Name = "本地 Redis", Host = "127.0.0.1", Port = 6379 }, "Redis");

            WithView(document, view =>
            {
                CircularProgressRing[] rings = [.. VisibleOfType<CircularProgressRing>(view)];
                Assert.HasCount(1, rings, "连接中要有且只有一圈在转的进度环。");
                Assert.IsTrue(rings[0].IsIndeterminate, "进度不可知 —— 别编一条假进度条。");
                Assert.Contains(
                    "本地 Redis",
                    string.Join('\n', VisibleOfType<TextBlock>(view).Select(t => t.Text)),
                    "卡片上要指名道姓说在连哪一台。");
            });
        });
    }

    [TestMethod]
    public void FailedState_SwapsTheRingForTheReasonAndTwoWaysOut()
    {
        OnUi(() =>
        {
            var document = new ConnectingDocument(
                new() { Name = "本地 Redis", Host = "127.0.0.1", Port = 6379 }, "Redis");
            document.MarkFailed("连不上 127.0.0.1:6379:Connection refused");

            WithView(document, view =>
            {
                Assert.IsEmpty(VisibleOfType<CircularProgressRing>(view), "已经失败了就别再转了。");
                Assert.Contains(
                    "Connection refused",
                    string.Join('\n', VisibleOfType<SelectableTextBlock>(view).Select(t => t.Text)),
                    "失败原因要能划选、Ctrl+C 拿走。");
                // 「重新连接」与「关闭标签页」—— 与终端标签页内的失败覆盖层同两个出口。
                Assert.HasCount(2, VisibleOfType<Button>(view));
            });
        });
    }

    [TestMethod]
    public void CancelButton_RaisesTheCancelRequest()
    {
        OnUi(() =>
        {
            var document = new ConnectingDocument(
                new() { Name = "本地 Redis", Host = "127.0.0.1", Port = 6379 }, "Redis");
            int cancels = 0;
            document.CancelRequested = () => cancels++;

            WithView(document, view =>
            {
                Button cancel = VisibleOfType<Button>(view).Single();
                cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                Assert.AreEqual(1, cancels, "覆盖层上的「取消」要把撤销意图发出来。");
            });
        });
    }

    private static IEnumerable<T> VisibleOfType<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible);

    private static void WithView(ConnectingDocument document, Action<ConnectingDocumentView> body)
    {
        var view = (ConnectingDocumentView)document.CreateView();
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            body(view);
        }
        finally
        {
            window.Close();
        }
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
