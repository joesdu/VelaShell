using System.Reactive.Concurrency;
using NSubstitute;
using ReactiveUI.Builder;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.ViewModels;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class MainWindowViewModelTests
{
    static MainWindowViewModelTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadScheduler.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }

    [TestMethod]
    [TestCategory("UI")]
    public void MainWindowViewModel_Initializes_WithAllSubViewModels()
    {
        var vm = new MainWindowViewModel();

        Assert.IsNotNull(vm.Sidebar);
        Assert.IsNotNull(vm.TabBar);
        Assert.IsNotNull(vm.StatusBar);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void ToolsFilesCommand_IsRegistered_WithShortcut()
    {
        var vm = new MainWindowViewModel();

        CommandDescriptor? command = vm.Commands.Find("tools.files");

        Assert.IsNotNull(
            command,
            "SFTP file manager command must be wired so the panel can be opened."
        );
        Assert.AreEqual("Ctrl+Shift+F", command.Shortcut);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void ToggleFileBrowser_RequiresConnectedSsh_AndPreservesHiddenIntentAcrossTabs()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var vm = new MainWindowViewModel(sftpService: sftp);

        Assert.IsFalse(vm.FileBrowser.IsVisible);
        Assert.IsFalse(vm.CanToggleFileBrowser);
        vm.ToggleFileBrowser();
        Assert.IsFalse(
            vm.FileBrowser.IsVisible,
            "No active SSH terminal must keep the panel closed."
        );

        var first = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>())
        {
            Profile = new() { Name = "one", Host = "one.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.TabBar.AddTab(first);
        Assert.IsTrue(vm.CanToggleFileBrowser);
        Assert.IsTrue(
            vm.FileBrowser.IsVisible,
            "The first connected SSH terminal is visible by default."
        );

        vm.ToggleFileBrowser();
        Assert.IsFalse(vm.FileBrowser.IsVisible);

        var second = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>())
        {
            Profile = new() { Name = "two", Host = "two.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.TabBar.AddTab(second);
        Assert.IsFalse(vm.FileBrowser.IsVisible);

        second.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsFalse(vm.CanToggleFileBrowser);

        var local = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>())
        {
            LocalShell = new("pwsh", "PowerShell", "pwsh.exe"),
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.TabBar.AddTab(local);
        Assert.IsFalse(vm.CanToggleFileBrowser);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void ConnectedStateChange_AddsTargetAndEnablesCurrentTerminalExecution()
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var library = new QuickCommandsViewModel(repository);
        var vm = new MainWindowViewModel(quickCommands: library);
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        var tab = new TerminalTabViewModel(emulator)
        {
            Profile = new() { Name = "server", Host = "server.example" },
        };
        vm.TabBar.AddTab(tab);
        QuickCommandRunnerViewModel runner = vm.Sidebar.QuickCommands!;
        Assert.IsEmpty(runner.Targets);

        tab.ConnectionStatus = SessionStatus.Connected;

        Assert.HasCount(1, runner.Targets);
        Assert.AreEqual(tab.Id, runner.Targets[0].Id);
        Assert.IsTrue(runner.CanRun);
        runner.RunCommand.Execute(library.AllCommands[0]).Subscribe();
        emulator.Received(1).WriteInput(Arg.Any<byte[]>());

        tab.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsEmpty(runner.Targets);
        Assert.IsFalse(runner.CanRun);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void QuickCommandExecution_RequestsFocusForActiveTerminal()
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var library = new QuickCommandsViewModel(repository);
        var vm = new MainWindowViewModel(quickCommands: library);
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        var tab = new TerminalTabViewModel(emulator) { ConnectionStatus = SessionStatus.Connected };
        vm.TabBar.AddTab(tab);
        bool focusRequested = false;
        vm.TerminalFocusRequested += (_, _) => focusRequested = true;

        vm.Sidebar.QuickCommands!.RunCommand.Execute(library.AllCommands[0]).Subscribe();

        Assert.IsTrue(focusRequested);
    }

    [TestMethod]
    [TestCategory("Broadcast")]
    public void BroadcastInput_UsesSharedSelectionAndPerTerminalInputMethods()
    {
        var vm = new MainWindowViewModel();
        ITerminalEmulator firstEmulator = Substitute.For<ITerminalEmulator>();
        ITerminalEmulator secondEmulator = Substitute.For<ITerminalEmulator>();
        firstEmulator
            .WriteKeyInput(Arg.Any<Avalonia.Input.Key>(), Arg.Any<Avalonia.Input.KeyModifiers>())
            .Returns(true);
        secondEmulator
            .WriteKeyInput(Arg.Any<Avalonia.Input.Key>(), Arg.Any<Avalonia.Input.KeyModifiers>())
            .Returns(true);
        var first = new TerminalTabViewModel(firstEmulator)
        {
            Title = "first",
            ConnectionStatus = SessionStatus.Connected,
        };
        var second = new TerminalTabViewModel(secondEmulator)
        {
            Title = "second",
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.TabBar.AddTab(first);
        vm.TabBar.AddTab(second);
        vm.BroadcastInput.TargetSelector.Targets[0].IsSelected = true;
        vm.BroadcastInput.TargetSelector.Targets[1].IsSelected = true;

        Assert.IsTrue(vm.BroadcastTextInput("cd src"));
        Assert.IsTrue(
            vm.BroadcastKeyInput(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None)
        );

        firstEmulator.Received(1).WriteTextInput("cd src");
        secondEmulator.Received(1).WriteTextInput("cd src");
        firstEmulator
            .Received(1)
            .WriteKeyInput(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None);
        secondEmulator
            .Received(1)
            .WriteKeyInput(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None);
    }

    [TestMethod]
    [TestCategory("Broadcast")]
    public void BroadcastInput_WithoutSelectionFallsBackToCurrentConnectedTerminal()
    {
        var vm = new MainWindowViewModel();
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        var tab = new TerminalTabViewModel(emulator) { ConnectionStatus = SessionStatus.Connected };
        vm.TabBar.AddTab(tab);

        Assert.IsTrue(vm.BroadcastTextInput("pwd"));

        emulator.Received(1).WriteTextInput("pwd");
    }

    [TestMethod]
    [TestCategory("Broadcast")]
    public void BroadcastInput_DisconnectedTerminalIsRemovedFromTargets()
    {
        var vm = new MainWindowViewModel();
        var tab = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>())
        {
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.TabBar.AddTab(tab);
        Assert.HasCount(1, vm.BroadcastInput.TargetSelector.Targets);

        tab.ConnectionStatus = SessionStatus.Disconnected;

        Assert.IsEmpty(vm.BroadcastInput.TargetSelector.Targets);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void SettingsSaved_ReappliesScrollbackToOpenTabs()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        var vm = new MainWindowViewModel(settingsService: settingsService);

        var tab = new TerminalTabViewModel(emulator);
        vm.TabBar.Tabs.Add(tab);

        // Saving settings must re-apply live values to already-open terminals (#3/#15/#21).
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { ScrollbackLines = 88_000 }
        );

        // The re-apply is marshalled through RxApp.MainThreadScheduler, which other test
        // classes may have initialized to an asynchronous scheduler — allow it to land.
        SpinWait.SpinUntil(() => emulator.ScrollbackLines == 88_000, TimeSpan.FromSeconds(5));
        Assert.AreEqual(88_000, emulator.ScrollbackLines);

        tab.Dispose();
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SettingsSaved_UpdatesQuickCommandsPanelVisibility()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            quickCommands: new QuickCommandsViewModel(repository)
        );

        Assert.IsFalse(vm.Sidebar.IsQuickCommandsVisible);
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { Appearance = new() { ShowQuickCommandsPanel = true } }
        );

        Assert.IsTrue(vm.Sidebar.IsQuickCommandsVisible);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void SidebarViewModel_Initializes_WithCommands()
    {
        var vm = new SidebarViewModel();

        Assert.IsNotNull(vm.SettingsCommand);
        Assert.IsNotNull(vm.NotificationsCommand);
        Assert.IsNotNull(vm.RecentConnections);
    }

    [TestMethod]
    [TestCategory("Sidebar")]
    public async Task InitializeAsync_RestoresAndPersistsSidebarLayoutState()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());
        settingsService
            .GetStateAsync()
            .Returns(
                new AppState
                {
                    SidebarQuickCommandsExpanded = false,
                    SidebarQuickCommandsHeight = 245,
                    SidebarRecentConnectionsExpanded = true,
                    SidebarRecentConnectionsHeight = 275,
                }
            );
        var vm = new MainWindowViewModel(settingsService: settingsService);

        await vm.InitializeAsync();

        Assert.IsFalse(vm.Sidebar.QuickCommandsExpanded);
        Assert.AreEqual(245, vm.Sidebar.QuickCommandsHeight);
        Assert.IsTrue(vm.Sidebar.RecentConnectionsExpanded);
        Assert.AreEqual(275, vm.Sidebar.RecentConnectionsHeight);

        vm.Sidebar.QuickCommandsExpanded = true;
        vm.Sidebar.QuickCommandsHeight = 310;
        vm.Sidebar.RecentConnectionsExpanded = false;
        await vm.PersistSidebarStateAsync();

        await settingsService
            .Received()
            .SaveStateAsync(
                Arg.Is<AppState>(state =>
                    state.SidebarQuickCommandsExpanded
                    && state.SidebarQuickCommandsHeight == 310
                    && !state.SidebarRecentConnectionsExpanded
                    && state.SidebarRecentConnectionsHeight == 275
                )
            );
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task ActiveTerminalTab_FollowsSavedProfileWhenSettingEnabled()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        ISessionRepository sessionRepository = Substitute.For<ISessionRepository>();
        SessionProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "server",
            Host = "server.example",
            Username = "root",
        };
        settingsService
            .GetSettingsAsync()
            .Returns(new AppSettings { General = new() { FollowActiveTerminalInExplorer = true } });
        settingsService.GetStateAsync().Returns(new AppState());
        sessionRepository.GetAllGroupsAsync().Returns([]);
        sessionRepository.GetAllSessionsAsync().Returns([profile]);
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sessionRepository: sessionRepository
        );
        await vm.InitializeAsync();

        vm.TabBar.AddTab(
            new TerminalTabViewModel(Substitute.For<ITerminalEmulator>()) { Profile = profile }
        );

        Assert.AreEqual(profile.Id, vm.Sidebar.SessionTree?.SelectedNode?.Id);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task ActiveTerminalTab_DoesNotChangeTreeSelectionWhenSettingDisabled()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        ISessionRepository sessionRepository = Substitute.For<ISessionRepository>();
        SessionProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "server",
            Host = "server.example",
            Username = "root",
        };
        settingsService
            .GetSettingsAsync()
            .Returns(
                new AppSettings { General = new() { FollowActiveTerminalInExplorer = false } }
            );
        settingsService.GetStateAsync().Returns(new AppState());
        sessionRepository.GetAllGroupsAsync().Returns([]);
        sessionRepository.GetAllSessionsAsync().Returns([profile]);
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sessionRepository: sessionRepository
        );
        await vm.InitializeAsync();

        vm.TabBar.AddTab(
            new TerminalTabViewModel(Substitute.For<ITerminalEmulator>()) { Profile = profile }
        );

        Assert.IsNull(vm.Sidebar.SessionTree?.SelectedNode);
    }
}
