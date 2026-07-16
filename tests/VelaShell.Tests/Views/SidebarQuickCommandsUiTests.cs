using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>左栏快捷片段区域在最小窗口高度下的布局与折叠回归测试。</summary>
[TestClass]
[TestCategory("SidebarUi")]
public class SidebarQuickCommandsUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    [TestMethod]
    public void MinimumHeight_QuickCommandsCanCollapseAndRestore()
    {
        OnUi(() =>
        {
            IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
            var library = new QuickCommandsViewModel(repository);
            var runner = new QuickCommandRunnerViewModel(library);
            var viewModel = new SidebarViewModel(quickCommands: runner)
            {
                IsQuickCommandsVisible = true,
            };
            var view = new SidebarView { DataContext = viewModel };
            var window = new Window
            {
                Width = 260,
                Height = 464,
                Content = view,
            };
            window.Show();
            Relayout(window);
            Grid grid = view.FindControl<Grid>("SessionAndQuickGrid")!;
            Button toggle = view.FindControl<Button>("QuickCommandsToggle")!;
            QuickCommandsView content = view.FindControl<QuickCommandsView>(
                "QuickCommandsContent"
            )!;

            Assert.IsTrue(content.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.IsFalse(content.IsVisible);
            Assert.AreEqual(36, grid.RowDefinitions[2].ActualHeight);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.IsTrue(content.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);
            window.Close();
        });
    }

    [TestMethod]
    public void HiddenQuickCommands_ReclaimsPanelAndSplitterSpace()
    {
        OnUi(() =>
        {
            IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
            var runner = new QuickCommandRunnerViewModel(new QuickCommandsViewModel(repository));
            var viewModel = new SidebarViewModel(quickCommands: runner);
            var view = new SidebarView { DataContext = viewModel };
            var window = new Window
            {
                Width = 260,
                Height = 464,
                Content = view,
            };
            window.Show();
            Relayout(window);
            Grid grid = view.FindControl<Grid>("SessionAndQuickGrid")!;
            Border section = view.FindControl<Border>("QuickCommandsSection")!;
            GridSplitter splitter = view.FindControl<GridSplitter>("QuickCommandsSplitter")!;

            Assert.IsFalse(section.IsVisible);
            Assert.IsFalse(splitter.IsVisible);
            Assert.AreEqual(0, grid.RowDefinitions[1].ActualHeight);
            Assert.AreEqual(0, grid.RowDefinitions[2].ActualHeight);

            viewModel.IsQuickCommandsVisible = true;
            Relayout(window);
            Assert.IsTrue(section.IsVisible);
            Assert.IsTrue(splitter.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);
            window.Close();
        });
    }

    [TestMethod]
    public void QuickCommands_RendersCollapsibleGroups()
    {
        OnUi(() =>
        {
            IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
            var runner = new QuickCommandRunnerViewModel(new QuickCommandsViewModel(repository));
            var view = new QuickCommandsView { DataContext = runner };
            var window = new Window
            {
                Width = 300,
                Height = 500,
                Content = view,
            };
            window.Show();
            Relayout(window);

            Assert.IsGreaterThan(
                1,
                view.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Primitives.ToggleButton>()
                    .Count()
            );
            window.Close();
        });
    }

    [TestMethod]
    public void BroadcastBar_ToggleFocusAndClose_UsesTextlessCaptureArea()
    {
        OnUi(() =>
        {
            var viewModel = new MainWindowViewModel();
            var view = new BroadcastInputView { DataContext = viewModel };
            var window = new Window
            {
                Width = 900,
                Height = 80,
                Content = view,
            };
            window.Show();

            viewModel.BroadcastInput.ToggleCommand.Execute().Subscribe();
            Relayout(window);
            view.FocusCapture();
            Relayout(window);

            Border capture = view.FindControl<Border>("CaptureBorder")!;
            Assert.IsTrue(view.IsVisible);
            Assert.IsTrue(capture.IsFocused);
            Assert.IsEmpty(view.GetVisualDescendants().OfType<TextBox>());

            viewModel.BroadcastInput.CloseCommand.Execute().Subscribe();
            Relayout(window);
            Assert.IsFalse(view.IsVisible);
            window.Close();
        });
    }

    [TestMethod]
    public void MinimumHeight_RecentConnectionsCanCollapseAndRestore()
    {
        OnUi(() =>
        {
            var view = new SidebarView { DataContext = new SidebarViewModel() };
            var window = new Window
            {
                Width = 260,
                Height = 464,
                Content = view,
            };
            window.Show();
            Relayout(window);
            Grid grid = view.FindControl<Grid>("SidebarSectionsGrid")!;
            Button toggle = view.FindControl<Button>("RecentConnectionsToggle")!;
            ScrollViewer content = view.FindControl<ScrollViewer>("RecentConnectionsContent")!;
            GridSplitter splitter = view.FindControl<GridSplitter>("RecentConnectionsSplitter")!;

            Assert.IsTrue(content.IsVisible);
            Assert.IsTrue(splitter.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.IsFalse(content.IsVisible);
            Assert.IsFalse(splitter.IsVisible);
            Assert.AreEqual(36, grid.RowDefinitions[2].ActualHeight);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.IsTrue(content.IsVisible);
            Assert.IsTrue(splitter.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);
            window.Close();
        });
    }

    [TestMethod]
    public void StatusBar_RemoteFilesButtonTracksAvailabilityAndOpenState()
    {
        OnUi(() =>
        {
            ICommand command = Substitute.For<ICommand>();
            command.CanExecute(null).Returns(true);
            var view = new StatusBarView
            {
                DataContext = new StatusBarViewModel(),
                FileBrowserCommand = command,
                ShowFileBrowserButton = true,
            };
            var window = new Window
            {
                Width = 900,
                Height = 80,
                Content = view,
            };
            window.Show();
            Relayout(window);
            Button button = view.FindControl<Button>("FileBrowserButton")!;
            Control closedIcon = view.FindControl<Control>("FileBrowserClosedIcon")!;
            Control openIcon = view.FindControl<Control>("FileBrowserOpenIcon")!;

            Assert.IsTrue(button.IsVisible);
            Assert.IsTrue(closedIcon.IsVisible);
            Assert.IsFalse(openIcon.IsVisible);

            view.IsFileBrowserVisible = true;
            Relayout(window);
            Assert.IsFalse(closedIcon.IsVisible);
            Assert.IsTrue(openIcon.IsVisible);

            Assert.AreSame(command, button.Command);
            button.Command!.Execute(button.CommandParameter);
            command.Received(1).Execute(null);

            view.ShowFileBrowserButton = false;
            Relayout(window);
            Assert.IsFalse(button.IsVisible);
            window.Close();
        });
    }

    private static void Relayout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void OnUi(Action body) =>
        _session
            .Dispatch(
                () =>
                {
                    body();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();

    private sealed class HeadlessTestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Resources.MergedDictionaries.Add(
                LoadDictionary("avares://VelaShell.Controls/Themes/Generic.axaml")
            );
            Resources.MergedDictionaries.Add(
                LoadDictionary("avares://VelaShell.Controls/Themes/VelaTokens.axaml")
            );
            Resources.MergedDictionaries.Add(
                LoadDictionary("avares://VelaShell.Controls/Themes/VelaShellTokens.axaml")
            );
            Resources.MergedDictionaries.Add(
                LoadDictionary("avares://VelaShell.Controls/Themes/Icons.axaml")
            );
        }

        private static ResourceInclude LoadDictionary(string uri) =>
            new(new Uri(uri)) { Source = new(uri) };
    }
}
