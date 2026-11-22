using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Docking.Model;
using VelaShell.Localization;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Ui;
using VelaShell.Services.Plugins;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("UI")]
public sealed class AiAssistantRegionUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AiAssistantRegionUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    private sealed class NullLog : IPluginLogger
    {
        public void Write(PluginLogLevel level, string message, Exception? exception = null) { }
    }

    private sealed class TestDocument : DockDocument;

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Click(Control view, string name) =>
        view.GetControl<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [TestMethod]
    public Task HostRouting_HideCollapseAndActivatePreserveContent_DisposeClosesOnce() =>
        _session.RunOnUiAsync(async () =>
        {
            var vm = new MainWindowViewModel();
            var terminal = new TestDocument { Id = "terminal", Title = "terminal" };
            vm.Layout.AddDocument(terminal);
            DockNode originalRoot = vm.Layout.Root;
            DockDocument? originalActive = vm.Layout.ActiveDocument;
            using var api = new PluginUiApi("velashell.ai", new NullLog(), () => vm);
            var content = new TextBox { Text = "unfinished question" };
            IPluginPanel panel = await api.ShowPanelAsync(
                new() { Title = "AI", Placement = PanelPlacement.Right, PlacementRatio = 0.85 }, () => content);
            int closed = 0;
            panel.Closed += () => closed++;

            Assert.AreSame(originalRoot, vm.Layout.Root);
            Assert.AreSame(originalActive, vm.Layout.ActiveDocument);
            Assert.AreSame(terminal, vm.Layout.AllDocuments().Single());
            Assert.AreSame(content, vm.AiAssistant.Content);
            vm.AiAssistant.Collapse();
            Assert.AreEqual(AiAssistantRegionViewModel.RailWidth, vm.AiAssistant.ReservedWidth);
            vm.AiAssistant.Hide();
            Assert.AreEqual(0, vm.AiAssistant.ReservedWidth);
            Assert.IsTrue(panel.IsOpen);
            Assert.AreEqual(0, closed);
            await panel.ActivateAsync();
            Assert.IsTrue(vm.AiAssistant.ShowPane);
            Assert.AreSame(originalActive, vm.Layout.ActiveDocument);
            Assert.AreSame(content, vm.AiAssistant.Content);
            Assert.AreEqual("unfinished question", content.Text);

            api.Dispose();
            await panel.CloseAsync();
            Assert.AreEqual(1, closed);
            Assert.IsFalse(panel.IsOpen);
            Assert.IsNull(vm.AiAssistant.Content);
            Assert.IsFalse(vm.AiAssistant.IsVisible);
        });

    /// <summary>
    /// AI 插件另开的文档(改动对比,Placement=Tabs)进主标签区,不去抢聊天面板占着的专属区 ——
    /// 以前一律送进专属区,Attach 抛「已有内容」,插件只能退回一个独立小窗。
    /// </summary>
    [TestMethod]
    public Task HostRouting_AiSecondaryDocument_GoesToMainTabArea() =>
        _session.RunOnUiAsync(async () =>
        {
            var vm = new MainWindowViewModel();
            var terminal = new TestDocument { Id = "terminal", Title = "terminal" };
            vm.Layout.AddDocument(terminal);
            using var api = new PluginUiApi("velashell.ai", new NullLog(), () => vm);
            var chat = new TextBox { Text = "chat" };
            IPluginPanel chatPanel = await api.ShowPanelAsync(
                new() { Title = "AI", Placement = PanelPlacement.Right }, () => chat);
            Assert.AreSame(chat, vm.AiAssistant.Content);

            var diff = new TextBox { Text = "diff" };
            IPluginPanel diffPanel = await api.ShowPanelAsync(
                new() { Title = "a.cs (changes)", Placement = PanelPlacement.Tabs }, () => diff);

            Assert.AreSame(chat, vm.AiAssistant.Content, "the chat panel keeps the AI region");
            Assert.AreEqual(2, vm.Layout.AllDocuments().Count());
            Assert.AreSame(vm.Layout.FindGroup(terminal), vm.Layout.FindGroup(vm.Layout.ActiveDocument!),
                "the diff lands in the main tab group, next to the terminal");
            Assert.AreSame(diff, ((VelaShell.Docking.PluginDocument)vm.Layout.ActiveDocument!).CreateView());
            Assert.IsTrue(diffPanel.IsOpen);

            await diffPanel.CloseAsync();
            Assert.AreSame(terminal, vm.Layout.AllDocuments().Single());
            Assert.IsTrue(chatPanel.IsOpen);
            api.Dispose();
            await chatPanel.CloseAsync();
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task MainWindow_PinningReservesWidth_ResizeAndNarrowWindowKeepWorkspaceUsable(bool sidebarRight) =>
        _session.RunOnUiAsync(async () =>
        {
            var vm = new MainWindowViewModel();
            var settings = new AppSettings();
            settings.Appearance.AiPanelWidth = 420;
            settings.Appearance.SidebarPosition = sidebarRight ? "right" : "left";
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 800 };
            window.ApplyStartupAppearance(settings);
            using var api = new PluginUiApi("velashell.ai", new NullLog(), () => vm);
            IPluginPanel panel = await api.ShowPanelAsync(new() { Title = "AI", Placement = PanelPlacement.Right }, () => new TextBox());
            window.Show();
            try
            {
                Layout(window);
                Grid main = window.GetControl<Grid>("MainAreaGrid");
                AiAssistantRegionView region = window.GetVisualDescendants().OfType<AiAssistantRegionView>().Single();
                double dockedWidth = main.Bounds.Width;
                Assert.AreEqual(420, vm.AiAssistant.PaneWidth, 0.1);
                Assert.IsTrue(vm.AiAssistant.IsDocked);
                vm.AiAssistant.TogglePin();
                Layout(window);
                Assert.IsGreaterThan(dockedWidth + 300, main.Bounds.Width, "浮层只占窄边条,必须归还真实布局宽度");
                vm.AiAssistant.TogglePin();
                Layout(window);
                Border handle = region.GetControl<Border>("ResizeHandle");
                Point point = handle.TranslatePoint(new Point(2, 200), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseMove(point - new Vector(100, 0));
                window.MouseUp(point - new Vector(100, 0), MouseButton.Left);
                Layout(window);
                Assert.AreEqual(520, vm.AiAssistant.PaneWidth, 0.1);
                Assert.AreEqual(dockedWidth - 100, main.Bounds.Width, 1);
                point = handle.TranslatePoint(new Point(2, 200), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseMove(point + new Vector(1000, 0));
                window.MouseUp(point + new Vector(1000, 0), MouseButton.Left);
                Layout(window);
                Assert.AreEqual(AiAssistantRegionViewModel.MinimumWidth, vm.AiAssistant.PaneWidth, 0.1,
                    "拖过右边缘也不能将助手缩至最小宽度以下");
                vm.AiAssistant.Resize(520);
                vm.AiAssistant.TogglePin();
                vm.AiAssistant.SetAvailableWidth(0, 0);
                Assert.AreEqual(AiAssistantRegionViewModel.MinimumWidth, vm.AiAssistant.PaneWidth);
                vm.AiAssistant.TogglePin();
                Layout(window);

                window.Width = 800;
                Layout(window);
                Assert.IsFalse(vm.AiAssistant.IsDocked, "窄窗口应临时悬浮,不能挤坏终端");
                Assert.IsGreaterThanOrEqualTo(400, main.Bounds.Width);
                Assert.AreEqual(520, vm.AiAssistant.PreferredWidth);
                vm.AiAssistant.Collapse();
                Layout(window);
                window.MouseMove(new Point(10, 300));
                Button expand = region.GetControl<Button>("ExpandButton");
                window.MouseMove(expand.TranslatePoint(new Point(12, 10), window)!.Value);
                Layout(window);
                Assert.IsTrue(vm.AiAssistant.ShowPane, "窄窗口临时浮层也应允许移入 AI 图标展开");
                window.Width = 1440;
                Layout(window);
                Assert.IsTrue(vm.AiAssistant.IsDocked);
                Assert.AreEqual(520, vm.AiAssistant.PaneWidth, 0.1);
            }
            finally { await panel.CloseAsync(); window.Close(); }
        });

    [TestMethod]
    public Task MainWindowClose_RemembersAiPreferencesWithWindowAndSessionMemoryDisabled() =>
        _session.RunOnUiAsync(() =>
        {
            var settings = new AppSettings();
            settings.Appearance.StartupWindowState = "normal";
            settings.General.RestoreSessionsOnStartup = false;
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm };
            window.ApplyStartupAppearance(settings);
            vm.AiAssistant.SetAvailableWidth(900, 900);
            vm.AiAssistant.Resize(540);
            vm.AiAssistant.TogglePin();
            vm.AiAssistant.SetAvailableWidth(100, 450);
            vm.AiAssistant.Hide();
            ISettingsService service = Substitute.For<ISettingsService>();
            AppSettings? saved = null;
            service.SaveSettingsAsync(Arg.Do<AppSettings>(value => saved = value)).Returns(Task.CompletedTask);
            typeof(MainWindow).GetField("_settingsService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, service);
            typeof(MainWindow).GetMethod("PersistWindowBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [settings]);
            Assert.IsNotNull(saved);
            Assert.AreEqual(540, saved.Appearance.AiPanelWidth);
            Assert.IsFalse(saved.Appearance.AiPanelPinned);

            var nextVm = new MainWindowViewModel();
            var nextWindow = new MainWindow { DataContext = nextVm };
            nextWindow.ApplyStartupAppearance(saved);
            Assert.AreEqual(540, nextVm.AiAssistant.PreferredWidth);
            Assert.IsFalse(nextVm.AiAssistant.IsPinned);
            Assert.IsFalse(nextVm.AiAssistant.IsVisible, "恢复偏好不能自动启动 AI 助手");
            return Task.CompletedTask;
        });

    [TestMethod]
    public Task RegionPointer_WithRegionFocusStaysExpandedUntilFocusLeaves() =>
        _session.RunOnUiAsync(async () =>
        {
            var state = new AiAssistantRegionViewModel();
            var input = new TextBox { Text = "unfinished question" };
            var view = new AiAssistantRegionView { DataContext = state, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var outside = new Button { Width = 200, Height = 600, Content = "outside" };
            var host = new Grid();
            host.Children.Add(outside);
            host.Children.Add(view);
            var window = new Window { Width = 1000, Height = 600, Content = host };
            var panel = new PluginPanel("velashell.ai", new NullLog(), new() { Title = "AI" }, input, state);
            window.Show();
            try
            {
                Layout(window);
                Click(view, "PinButton");
                Layout(window);
                Assert.IsTrue(state.IsExpanded);
                Assert.IsTrue(input.Focus());
                window.MouseMove(new Point(10, 300));
                await Task.Delay(400);
                Assert.IsTrue(state.IsExpanded, "输入框仍有键盘焦点时,移出整个 AI 区域也不得收起");

                Assert.IsTrue(outside.Focus());
                await Task.Delay(400);
                Assert.IsFalse(state.IsExpanded, "焦点离开整个 AI 区域后,移出区域应恢复自动收起");
            }
            finally { await panel.CloseAsync(); window.Close(); }
        });

    [TestMethod]
    public Task RegionButtonsAndPointer_AutoHideWaitsForDropdown_AndReusesTheView() =>
        _session.RunOnUiAsync(async () =>
        {
            var state = new AiAssistantRegionViewModel();
            var combo = new ComboBox { ItemsSource = new[] { "server one", "server two" }, SelectedIndex = 0 };
            var view = new AiAssistantRegionView { DataContext = state, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var window = new Window { Width = 1000, Height = 600, Content = view };
            var panel = new PluginPanel("velashell.ai", new NullLog(), new() { Title = "AI" }, combo, state);
            window.Show();
            try
            {
                Layout(window);
                window.MouseMove(new Point(900, 10));
                Click(view, "PinButton");
                Assert.IsFalse(state.IsPinned);
                Layout(window);
                combo.IsDropDownOpen = true;
                window.MouseMove(new Point(10, 300));
                await Task.Delay(400);
                Assert.IsTrue(state.IsExpanded, "下拉菜单打开期间不得自动收起");
                combo.IsDropDownOpen = false;
                await Task.Delay(400);
                Layout(window);
                Assert.IsFalse(state.IsExpanded);
                window.MouseMove(new Point(990, 100));
                Layout(window);
                Assert.IsFalse(state.IsExpanded, "移入边条空白处不得展开浮层");
                Button expand = view.GetControl<Button>("ExpandButton");
                window.MouseMove(expand.TranslatePoint(new Point(12, 10), window)!.Value);
                Layout(window);
                Assert.IsTrue(state.IsExpanded, "移入 AI 图标才展开浮层");
                window.MouseMove(combo.TranslatePoint(new Point(10, 10), window)!.Value);
                await Task.Delay(400);
                Assert.IsTrue(state.IsExpanded, "从图标进入助手内容后应保持展开");
                Click(view, "CollapseButton");
                Layout(window);
                Assert.IsFalse(state.IsExpanded);
                Click(view, "ExpandButton");
                Layout(window);
                Assert.IsTrue(state.IsExpanded);
                Click(view, "HideButton");
                Layout(window);
                Assert.IsFalse(state.IsVisible);
                Assert.IsTrue(panel.IsOpen);
                await panel.ActivateAsync();
                Layout(window);
                Assert.AreSame(combo, view.GetControl<ContentControl>("PluginContent").Content);
            }
            finally { await panel.CloseAsync(); window.Close(); }
        });
}
