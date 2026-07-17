using System.Reactive.Concurrency;
using NSubstitute;
using ReactiveUI.Builder;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
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
        runner.SendCommand.Execute(library.AllCommands[0]).Subscribe();
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

        vm.Sidebar.QuickCommands!.SendCommand.Execute(library.AllCommands[0]).Subscribe();

        Assert.IsTrue(focusRequested);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_JoinPauseLeave_UpdatesTabState()
    {
        var tab = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());

        tab.JoinSyncChannel(SyncInputChannel.A);
        Assert.IsTrue(tab.IsInSyncChannel);
        Assert.AreEqual("A", tab.SyncChannelLetter);

        tab.ToggleSyncPauseCommand.Execute().Subscribe();
        Assert.IsTrue(tab.IsSyncPaused);

        // 改挂新频道时清除暂停态。
        tab.JoinSyncChannel(SyncInputChannel.B);
        Assert.AreEqual("B", tab.SyncChannelLetter);
        Assert.IsFalse(tab.IsSyncPaused);

        tab.LeaveSyncChannelCommand.Execute().Subscribe();
        Assert.IsFalse(tab.IsInSyncChannel);
        Assert.AreEqual(string.Empty, tab.SyncChannelLetter);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_CloseChannel_RemovesAllChannelMembersOnly()
    {
        var vm = new MainWindowViewModel();
        var first = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());
        var second = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());
        var other = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());
        vm.TabBar.AddTab(first);
        vm.TabBar.AddTab(second);
        vm.TabBar.AddTab(other);
        first.JoinSyncChannel(SyncInputChannel.A);
        second.JoinSyncChannel(SyncInputChannel.A);
        other.JoinSyncChannel(SyncInputChannel.B);

        first.CloseSyncChannelCommand.Execute().Subscribe();

        Assert.IsFalse(first.IsInSyncChannel);
        Assert.IsFalse(second.IsInSyncChannel);
        Assert.IsTrue(other.IsInSyncChannel);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_RemovedTab_LeavesChannel()
    {
        var vm = new MainWindowViewModel();
        var tab = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());
        vm.TabBar.AddTab(tab);
        tab.JoinSyncChannel(SyncInputChannel.C);

        vm.TabBar.Tabs.Remove(tab);

        Assert.IsFalse(tab.IsInSyncChannel);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_ForwardedInput_BypassesPeerEmulatorInputEvents()
    {
        var vm = new MainWindowViewModel();
        ITerminalEmulator firstEmulator = Substitute.For<ITerminalEmulator>();
        ITerminalEmulator secondEmulator = Substitute.For<ITerminalEmulator>();
        var first = new TerminalTabViewModel(firstEmulator)
        {
            ConnectionStatus = SessionStatus.Connected,
        };
        var second = new TerminalTabViewModel(secondEmulator)
        {
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.TabBar.AddTab(first);
        vm.TabBar.AddTab(second);
        first.JoinSyncChannel(SyncInputChannel.A);
        second.JoinSyncChannel(SyncInputChannel.A);

        firstEmulator.TypedInput += Raise.Event<Action<byte[]>>("ls"u8.ToArray());

        // 转发必须直写桥(SendRaw),不得经接收端终端控件的输入 API——否则会二次
        // 触发 TypedInput(转发回环)并驱动非焦点标签的命令补全弹层。
        secondEmulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
        secondEmulator.DidNotReceive().WriteTextInput(Arg.Any<string>());
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

    /// <summary>构造带设置/状态桩的 VM 并完成初始化,用于验证 SFTP 面板的启动意图。</summary>
    private static async Task<MainWindowViewModel> CreateInitializedVmAsync(
        bool autoOpenFileBrowser,
        bool lastVisible,
        ISettingsService? settingsServiceOut = null)
    {
        ISettingsService settingsService = settingsServiceOut ?? Substitute.For<ISettingsService>();
        settingsService
            .GetSettingsAsync()
            .Returns(new AppSettings
            {
                TerminalBehavior = new() { AutoOpenFileBrowser = autoOpenFileBrowser }
            });
        settingsService.GetStateAsync().Returns(new AppState { FileBrowserVisible = lastVisible });
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sftpService: Substitute.For<ISftpService>()
        );
        await vm.InitializeAsync();
        return vm;
    }

    private static TerminalTabViewModel CreateConnectedSshTab() =>
        new(Substitute.For<ITerminalEmulator>())
        {
            Profile = new() { Name = "srv", Host = "srv.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_AutoOpenSettingOn_OpensOnConnectDespiteLastClosedState()
    {
        MainWindowViewModel vm = await CreateInitializedVmAsync(
            autoOpenFileBrowser: true, lastVisible: false);

        vm.TabBar.AddTab(CreateConnectedSshTab());

        Assert.IsTrue(vm.FileBrowser.IsVisible, "开关开启时,无论上次退出状态如何都自动打开。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_AutoOpenSettingOff_FollowsLastClosedState()
    {
        MainWindowViewModel vm = await CreateInitializedVmAsync(
            autoOpenFileBrowser: false, lastVisible: false);

        vm.TabBar.AddTab(CreateConnectedSshTab());

        Assert.IsFalse(vm.FileBrowser.IsVisible, "开关关闭且上次退出时面板关闭:不自动打开。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_AutoOpenSettingOff_FollowsLastVisibleState()
    {
        MainWindowViewModel vm = await CreateInitializedVmAsync(
            autoOpenFileBrowser: false, lastVisible: true);

        vm.TabBar.AddTab(CreateConnectedSshTab());

        Assert.IsTrue(vm.FileBrowser.IsVisible, "开关关闭但上次退出时面板可见:仍自动打开。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_VisibilityIntent_IsPersistedOnExit()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        MainWindowViewModel vm = await CreateInitializedVmAsync(
            autoOpenFileBrowser: true, lastVisible: true, settingsService);

        vm.TabBar.AddTab(CreateConnectedSshTab());
        Assert.IsTrue(vm.FileBrowser.IsVisible);
        vm.ToggleFileBrowser();
        Assert.IsFalse(vm.FileBrowser.IsVisible);

        await vm.PersistSidebarStateAsync();

        await settingsService
            .Received()
            .SaveStateAsync(Arg.Is<AppState>(state => !state.FileBrowserVisible));
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
