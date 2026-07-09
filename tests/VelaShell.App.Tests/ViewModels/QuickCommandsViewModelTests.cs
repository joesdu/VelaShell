using System.Reactive.Linq;
using NSubstitute;
using VelaShell.App.ViewModels;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.App.Tests.ViewModels;

[TestClass]
public class QuickCommandsViewModelTests : IDisposable
{
    private readonly SonnetDbEngine _engine;
    private readonly IAppDataStore _dataStore;
    private readonly string _testDirectory;
    private readonly string _legacyDataPath;
    private readonly QuickCommandsViewModel _vm;
    private string? _executedCommand;

    public QuickCommandsViewModelTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_qctest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _legacyDataPath = Path.Combine(_testDirectory, "quick-commands.json");

        _engine = new SonnetDbEngine(Path.Combine(_testDirectory, "sonnetdb"));
        _dataStore = new SonnetDbAppDataStore(_engine);
        _executedCommand = null;
        _vm = new QuickCommandsViewModel(
            _dataStore,
            cmd => _executedCommand = cmd,
            _legacyDataPath);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, true);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void BuiltInDefaults_ContainExpectedCommands()
    {
        var names = _vm.AllCommands.Select(c => c.Name).ToList();

        Assert.IsTrue(names.Contains("htop"));
        Assert.IsTrue(names.Contains("top"));
        Assert.IsTrue(names.Contains("df -h"));
        Assert.IsTrue(names.Contains("free -m"));
        Assert.IsTrue(names.Contains("docker ps"));
        Assert.IsTrue(names.Contains("docker stats"));
        Assert.IsTrue(names.Contains("netstat -tlnp"));
        Assert.IsTrue(names.Contains("ss -tlnp"));
        Assert.IsTrue(names.Contains("systemctl status"));
        Assert.IsTrue(names.Contains("journalctl -f"));

        Assert.AreEqual(10, _vm.AllCommands.Count());
        Assert.IsTrue(_vm.AllCommands.All(c => c.IsBuiltIn));
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_FiltersByName_CaseInsensitive()
    {
        _vm.SearchQuery = "DOCKER";

        Assert.AreEqual(2, _vm.FilteredCommands.Count());
        Assert.IsTrue(_vm.FilteredCommands.All(c => c.Name.Contains("docker")));
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_FiltersByDescriptionAndCommandText()
    {
        _vm.SearchQuery = "process";

        Assert.IsTrue(_vm.FilteredCommands.Any(c => c.Name == "htop"));
        Assert.IsTrue(_vm.FilteredCommands.Any(c => c.Name == "top"));

        _vm.SearchQuery = "systemctl";

        Assert.AreEqual(1, _vm.FilteredCommands.Count());
        Assert.AreEqual("systemctl status", _vm.FilteredCommands[0].Name);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void ExecuteCommand_InvokesCallbackWithCommandText()
    {
        var htopVm = _vm.AllCommands.First(c => c.Name == "htop");

        _vm.ExecuteCommandCommand.Execute(htopVm).Subscribe();

        Assert.AreEqual("htop", _executedCommand);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void AddCommand_AddsCustomCommandToListAndPersists()
    {
        _vm.AddCommandCommand.Execute().Subscribe();

        Assert.IsTrue(_vm.IsAddingCommand);
        Assert.AreEqual("Custom", _vm.NewCategory);

        _vm.NewName = "my-cmd";
        _vm.NewCommandText = "echo hello";
        _vm.NewDescription = "Says hello";

        _vm.SaveNewCommandCommand.Execute().Subscribe();

        Assert.AreEqual(11, _vm.AllCommands.Count());
        Assert.IsFalse(_vm.IsAddingCommand);

        var added = _vm.AllCommands.Last();
        Assert.AreEqual("my-cmd", added.Name);
        Assert.AreEqual("echo hello", added.CommandText);
        Assert.AreEqual("Says hello", added.Description);
        Assert.AreEqual("Custom", added.Category);
        Assert.IsFalse(added.IsBuiltIn);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void DeleteCommand_RemovesCustomCommand_ButNotBuiltIn()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "temp-cmd";
        _vm.NewCommandText = "ls -la";
        _vm.NewDescription = "List files";
        _vm.SaveNewCommandCommand.Execute().Subscribe();

        var customCmd = _vm.AllCommands.First(c => c.Name == "temp-cmd");
        _vm.DeleteCommandCommand.Execute(customCmd).Subscribe();

        Assert.IsFalse(_vm.AllCommands.Any(c => c.Name == "temp-cmd"));
        Assert.AreEqual(10, _vm.AllCommands.Count());

        var builtIn = _vm.AllCommands.First(c => c.Name == "htop");
        _vm.DeleteCommandCommand.Execute(builtIn).Subscribe();

        Assert.IsTrue(_vm.AllCommands.Any(c => c.Name == "htop"));
        Assert.AreEqual(10, _vm.AllCommands.Count());
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_EmptyString_ShowsAllCommands()
    {
        _vm.SearchQuery = "docker";
        Assert.AreEqual(2, _vm.FilteredCommands.Count());

        _vm.SearchQuery = "";
        Assert.AreEqual(10, _vm.FilteredCommands.Count());
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Categories_ContainAllDistinctCategories()
    {
        Assert.IsTrue(_vm.Categories.Contains("System Monitor"));
        Assert.IsTrue(_vm.Categories.Contains("Network"));
        Assert.IsTrue(_vm.Categories.Contains("Docker"));
        Assert.IsTrue(_vm.Categories.Contains("System"));
        Assert.AreEqual(4, _vm.Categories.Count());
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void BuiltInCommand_CannotBeModified()
    {
        var htop = _vm.AllCommands.First(c => c.Name == "htop");

        htop.Name = "modified";
        Assert.AreEqual("htop", htop.Name);

        htop.CommandText = "modified";
        Assert.AreEqual("htop", htop.CommandText);

        htop.Description = "modified";
        Assert.AreEqual("Interactive process viewer", htop.Description);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task LoadCustomCommands_RestoresPersistedCommands()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "persisted-cmd";
        _vm.NewCommandText = "uptime";
        _vm.NewDescription = "Show uptime";
        _vm.NewCategory = "Custom";
        _vm.SaveNewCommandCommand.Execute().Subscribe();

        await Task.Delay(200);

        var vm2 = new QuickCommandsViewModel(
            _dataStore,
            null,
            _legacyDataPath);
        await vm2.LoadCustomCommandsAsync();

        Assert.AreEqual(11, vm2.AllCommands.Count());
        var restored = vm2.AllCommands.First(c => c.Name == "persisted-cmd");
        Assert.AreEqual("uptime", restored.CommandText);
        Assert.IsFalse(restored.IsBuiltIn);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void CancelAdd_HidesAddForm()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        Assert.IsTrue(_vm.IsAddingCommand);

        _vm.CancelAddCommand.Execute().Subscribe();
        Assert.IsFalse(_vm.IsAddingCommand);
    }
}
