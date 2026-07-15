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
        IAppDataStore store = Substitute.For<IAppDataStore>();
        var library = new QuickCommandsViewModel(store);
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
        IAppDataStore store = Substitute.For<IAppDataStore>();
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            quickCommands: new QuickCommandsViewModel(store)
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
}
