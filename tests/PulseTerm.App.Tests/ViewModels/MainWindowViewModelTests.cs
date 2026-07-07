using System.Reactive.Concurrency;
using PulseTerm.App.ViewModels;
using PulseTerm.Presentation.ViewModels;
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
    public void SidebarViewModel_Initializes_WithCommands()
    {
        var vm = new SidebarViewModel();

        Assert.IsNotNull(vm.QuickConnectCommand);
        Assert.IsNotNull(vm.SettingsCommand);
        Assert.IsNotNull(vm.NotificationsCommand);
        Assert.AreEqual(string.Empty, vm.QuickConnectText);
    }
}
