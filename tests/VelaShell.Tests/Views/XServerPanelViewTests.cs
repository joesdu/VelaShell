using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Localization;
using VelaShell.Core.XServer;
using VelaShell.Localization;
using VelaShell.Presentation.ViewModels;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>标题栏 X Server 按钮的浮层(F3):真渲染出来,程序行的名字与来历看得到,按钮接的是视图模型的命令,断开前的确认由视图接上。</summary>
[TestClass]
[TestCategory("XServer")]
public sealed class XServerPanelViewTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(XServerPanelViewTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [TestMethod]
    public Task Panel_ShowsEachProgram_AndItsButtonsDriveTheViewModel() =>
        _session.RunOnUiAsync(async () =>
        {
            ILocalXServer server = Substitute.For<ILocalXServer>();
            server.IsSupported.Returns(true);
            server.State.Returns(XServerState.Running);
            server.CanManageClients.Returns(true);
            server.Display.Returns("localhost:10.0");
            server.GetClientsAsync().Returns(
            [
                new XServerClient("1:1", 1, "XTerm", "user@box: ~", "user@box:22", 1, 2 << 20, Retained: false, HoldsServerGrab: true),
                new XServerClient("1:2", 2, "", "xclock", null, 1, 4096, Retained: true, HoldsServerGrab: false),
            ]);
            using ToastHostViewModel toasts = new((_, _) => new NoTimer());
            XServerToggleViewModel vm = new(server, toasts) { RefreshInterval = TimeSpan.FromHours(1) };
            XServerPanelView view = new() { DataContext = vm };
            Window window = new() { Content = view, Width = 400, Height = 500 };
            window.Show();
            await vm.RefreshClientsAsync();
            Dispatcher.UIThread.RunJobs();

            string[] texts = [.. view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];
            Assert.Contains("XTerm", texts);
            Assert.Contains("user@box:22", texts);
            Assert.Contains("xclock", texts, "没写 WM_CLASS:按窗口标题称呼");
            Assert.Contains("localhost:10.0", texts);
            Assert.Contains(Core.Resources.Strings.Get("XServer_PanelGrabbing"), texts, "抓着整个 X Server 的那一行标出来");
            Assert.Contains(Core.Resources.Strings.Get("XServer_PanelRetained"), texts);
            Assert.IsNotNull(vm.ConfirmDisconnectAsync, "断开前的确认框由视图接上");
            // 行模板里直接写的 Foreground 压不过 desc 样式:那一行原先渲染成普通说明的灰色。
            TextBlock retained = view.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.IsEffectivelyVisible && t.Text == Core.Resources.Strings.Get("XServer_PanelRetained"));
            Assert.IsTrue(window.TryFindResource("VelaWarning", window.ActualThemeVariant, out object? warning));
            Assert.AreEqual(((Avalonia.Media.ISolidColorBrush)warning!).Color,
                (retained.Foreground as Avalonia.Media.ISolidColorBrush)?.Color, "Retain 状态用警告色");

            Button breakGrabs = view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.BreakGrabsCommand));
            breakGrabs.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            server.Received(1).BreakGrabs();

            Assert.HasCount(2, view.GetVisualDescendants().OfType<Button>().Where(b => ReferenceEquals(b.Command, vm.DisconnectCommand)),
                "每一行一个断开按钮");
            window.Close();
        });
}
