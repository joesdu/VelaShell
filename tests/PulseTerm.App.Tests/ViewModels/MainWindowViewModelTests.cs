using System.Reactive.Concurrency;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Presentation.ViewModels;
using PulseTerm.Terminal;
using ReactiveUI.Builder;

namespace PulseTerm.App.Tests.ViewModels;

[TestClass]
public class MainWindowViewModelTests
{
    static MainWindowViewModelTests()
    {
        try
        {
            RxAppBuilder.CreateReactiveUIBuilder()
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

        var command = vm.Commands.Find("tools.files");

        Assert.IsNotNull(command, "SFTP file manager command must be wired so the panel can be opened.");
        Assert.AreEqual("Ctrl+Shift+F", command.Shortcut);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void ToggleFileBrowser_FlipsPanelVisibility()
    {
        var vm = new MainWindowViewModel();

        Assert.IsFalse(vm.FileBrowser.IsVisible);

        vm.ToggleFileBrowser();
        Assert.IsTrue(vm.FileBrowser.IsVisible);

        vm.ToggleFileBrowser();
        Assert.IsFalse(vm.FileBrowser.IsVisible);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void SettingsSaved_ReappliesScrollbackToOpenTabs()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var emulator = Substitute.For<ITerminalEmulator>();
        var vm = new MainWindowViewModel(settingsService: settingsService);

        var tab = new TerminalTabViewModel(emulator);
        vm.TabBar.Tabs.Add(tab);

        // Saving settings must re-apply live values to already-open terminals (#3/#15/#21).
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { ScrollbackLines = 88_000 });

        // The re-apply is marshalled through RxApp.MainThreadScheduler, which other test
        // classes may have initialized to an asynchronous scheduler — allow it to land.
        SpinWait.SpinUntil(() => emulator.ScrollbackLines == 88_000, TimeSpan.FromSeconds(5));
        Assert.AreEqual(88_000, emulator.ScrollbackLines);

        tab.Dispose();
    }

    [TestMethod]
    [TestCategory("UI")]
    public void SidebarViewModel_Initializes_WithCommands()
    {
        var vm = new SidebarViewModel();

        Assert.IsNotNull(vm.QuickConnectCommand);
        Assert.IsNotNull(vm.SettingsCommand);
        Assert.IsNotNull(vm.NotificationsCommand);
        Assert.AreEqual(string.Empty, vm.QuickConnectText);
    }
}
