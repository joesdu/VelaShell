using VelaShell.Core.Data;
using VelaShell.Infrastructure.Persistence;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class QuickCommandsViewModelTests : IDisposable
{
    private readonly IAppDataStore _dataStore;
    private readonly SonnetDbEngine _engine;
    private readonly string _legacyDataPath;
    private readonly string _testDirectory;
    private readonly QuickCommandsViewModel _vm;
    private string? _executedCommand;

    public QuickCommandsViewModelTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_qctest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _legacyDataPath = Path.Combine(_testDirectory, "quick-commands.json");
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
        _dataStore = new SonnetDbAppDataStore(_engine);
        _executedCommand = null;
        _vm = new(_dataStore,
            cmd => _executedCommand = cmd,
            _legacyDataPath);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void BuiltInDefaults_ContainExpectedCommands()
    {
        var names = _vm.AllCommands.Select(c => c.Name).ToList();
        Assert.Contains("htop", names);
        Assert.Contains("top", names);
        Assert.Contains("df -h", names);
        Assert.Contains("free -m", names);
        Assert.Contains("docker ps", names);
        Assert.Contains("docker stats", names);
        Assert.Contains("netstat -tlnp", names);
        Assert.Contains("ss -tlnp", names);
        Assert.Contains("systemctl status", names);
        Assert.Contains("journalctl -f", names);
        Assert.HasCount(10, _vm.AllCommands);
        Assert.IsTrue(_vm.AllCommands.All(c => c.IsBuiltIn));
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_FiltersByName_CaseInsensitive()
    {
        _vm.SearchQuery = "DOCKER";
        Assert.HasCount(2, _vm.FilteredCommands);
        Assert.IsTrue(_vm.FilteredCommands.All(c => c.Name.Contains("docker")));
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_FiltersByDescriptionAndCommandText()
    {
        _vm.SearchQuery = "process";
        Assert.Contains(c => c.Name == "htop", _vm.FilteredCommands);
        Assert.Contains(c => c.Name == "top", _vm.FilteredCommands);
        _vm.SearchQuery = "systemctl";
        Assert.HasCount(1, _vm.FilteredCommands);
        Assert.AreEqual("systemctl status", _vm.FilteredCommands[0].Name);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void ExecuteCommand_InvokesCallbackWithCommandText()
    {
        QuickCommandViewModel htopVm = _vm.AllCommands.First(c => c.Name == "htop");
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
        Assert.HasCount(11, _vm.AllCommands);
        Assert.IsFalse(_vm.IsAddingCommand);
        QuickCommandViewModel added = _vm.AllCommands.Last();
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
        QuickCommandViewModel customCmd = _vm.AllCommands.First(c => c.Name == "temp-cmd");
        _vm.DeleteCommandCommand.Execute(customCmd).Subscribe();
        Assert.DoesNotContain(c => c.Name == "temp-cmd", _vm.AllCommands);
        Assert.HasCount(10, _vm.AllCommands);
        QuickCommandViewModel builtIn = _vm.AllCommands.First(c => c.Name == "htop");
        _vm.DeleteCommandCommand.Execute(builtIn).Subscribe();
        Assert.Contains(c => c.Name == "htop", _vm.AllCommands);
        Assert.HasCount(10, _vm.AllCommands);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_EmptyString_ShowsAllCommands()
    {
        _vm.SearchQuery = "docker";
        Assert.HasCount(2, _vm.FilteredCommands);
        _vm.SearchQuery = "";
        Assert.HasCount(10, _vm.FilteredCommands);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Categories_ContainAllDistinctCategories()
    {
        Assert.Contains("System Monitor", _vm.Categories);
        Assert.Contains("Network", _vm.Categories);
        Assert.Contains("Docker", _vm.Categories);
        Assert.Contains("System", _vm.Categories);
        Assert.HasCount(4, _vm.Categories);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void BuiltInCommand_CannotBeModified()
    {
        QuickCommandViewModel htop = _vm.AllCommands.First(c => c.Name == "htop");
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
        var vm2 = new QuickCommandsViewModel(_dataStore,
            null,
            _legacyDataPath);
        await vm2.LoadCustomCommandsAsync();
        Assert.HasCount(11, vm2.AllCommands);
        QuickCommandViewModel restored = vm2.AllCommands.First(c => c.Name == "persisted-cmd");
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
