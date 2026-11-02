using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class QuickCommandsViewModelTests : IDisposable
{
    /// <summary>
    /// 内置命令条数。一律由目录推导,不写死数字:内置目录是产品内容、会随需求增删
    /// (如 5c22090 换掉了整批命令),这里要盯的是“视图模型如何对待内置命令”,
    /// 而不是目录当下装了哪几条 —— 写死就会在每次改目录时集体失效。
    /// </summary>
    private static int BuiltInCount => QuickCommandCatalog.BuiltIns.Count;

    /// <summary>拿来当操作对象的样本内置命令(同样不写死具体是哪条)。</summary>
    private static QuickCommand SampleBuiltIn => QuickCommandCatalog.BuiltIns[0];

    private readonly IAppDataStore _dataStore;
    private readonly SonnetDbEngine _engine;
    private readonly string _legacyDataPath;
    private readonly string _testDirectory;
    private readonly SonnetDbQuickCommandRepository _repository;
    private readonly QuickCommandsViewModel _vm;

    public QuickCommandsViewModelTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_qctest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _legacyDataPath = Path.Combine(_testDirectory, "quick-commands.json");
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
        _dataStore = new SonnetDbAppDataStore(_engine);
        _repository = new SonnetDbQuickCommandRepository(_dataStore, _legacyDataPath);
        _vm = new(_repository);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
        GC.SuppressFinalize(this);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void BuiltInDefaults_LoadTheEntireBuiltInCatalog()
    {
        var names = _vm.AllCommands.Select(c => c.Name).ToList();
        Assert.AreSequenceEqual([.. QuickCommandCatalog.BuiltIns.Select(c => c.Name)], names, SequenceOrder.InAnyOrder);
        Assert.HasCount(BuiltInCount, _vm.AllCommands);
        Assert.IsTrue(_vm.AllCommands.All(c => c.IsBuiltIn));
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_FiltersByName_CaseInsensitive()
    {
        // 大写查询命中小写名称 —— 这里验的是忽略大小写,期望条数按目录实际情况推导。
        int expected = QuickCommandCatalog.BuiltIns.Count(c =>
            c.Name.Contains("docker", StringComparison.OrdinalIgnoreCase)
        );
        Assert.IsGreaterThan(0, expected, "样本前提:内置目录里应有 docker 相关命令");

        _vm.SearchQuery = "DOCKER";

        Assert.HasCount(expected, _vm.FilteredCommands);
        Assert.IsTrue(
            _vm.FilteredCommands.All(c =>
                c.Name.Contains("docker", StringComparison.OrdinalIgnoreCase)
            )
        );
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_FiltersByDescriptionAndCommandText()
    {
        // 只出现在命令正文、不出现在名称里的词:命中它就说明筛选确实看了 CommandText。
        QuickCommand byText = QuickCommandCatalog.BuiltIns.First(c =>
            c.CommandText.Contains("dpkg")
        );
        _vm.SearchQuery = "dpkg";
        Assert.ContainsSingle(c => c.Name == byText.Name, _vm.FilteredCommands);
        Assert.DoesNotContain(
            "dpkg",
            byText.Name,
            "样本前提:该词不该出现在名称里,否则测不到 CommandText 匹配"
        );

        // 描述是本地化的,拿目录里的原值当查询,才不会绑死在某种语言上。
        QuickCommand byDescription = QuickCommandCatalog.BuiltIns.First(c =>
            !string.IsNullOrWhiteSpace(c.Description)
        );
        _vm.SearchQuery = byDescription.Description;
        Assert.Contains(c => c.Name == byDescription.Name, _vm.FilteredCommands);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Runner_NoExplicitSelection_UsesCurrentTerminal()
    {
        var runner = new QuickCommandRunnerViewModel(_vm);
        var currentId = Guid.NewGuid();
        runner.UpdateTargets([(currentId, "current")]);
        runner.SetCurrentTarget(currentId);
        QuickCommandExecutionRequest? request = null;
        runner.ExecutionRequested += (_, e) => request = e;
        QuickCommandViewModel command = _vm.AllCommands.First(c => c.Name == SampleBuiltIn.Name);
        runner.SendCommand.Execute(command).Subscribe();

        Assert.IsNotNull(request);
        Assert.AreEqual(SampleBuiltIn.CommandText, request.CommandText);
        Assert.AreSequenceEqual([currentId], [.. request.TargetIds]);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Runner_ExplicitSelection_BroadcastsOnlySelectedTerminals()
    {
        var runner = new QuickCommandRunnerViewModel(_vm);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        runner.UpdateTargets([(firstId, "first"), (secondId, "second"), (currentId, "current")]);
        runner.SetCurrentTarget(currentId);
        runner.Targets[0].IsSelected = true;
        runner.Targets[1].IsSelected = true;
        QuickCommandExecutionRequest? request = null;
        runner.ExecutionRequested += (_, e) => request = e;

        runner.SendCommand.Execute(_vm.AllCommands[0]).Subscribe();

        Assert.IsNotNull(request);
        Assert.AreSequenceEqual([firstId, secondId], [.. request.TargetIds], SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Runner_RemovedSelectedTarget_FallsBackToCurrentTerminal()
    {
        var runner = new QuickCommandRunnerViewModel(_vm);
        var removedId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        runner.UpdateTargets([(removedId, "removed"), (currentId, "current")]);
        runner.Targets[0].IsSelected = true;
        runner.SetCurrentTarget(currentId);

        runner.UpdateTargets([(currentId, "current")]);

        Assert.AreEqual(0, runner.SelectedTargetCount);
        Assert.IsTrue(runner.CanRun);
        QuickCommandExecutionRequest? request = null;
        runner.ExecutionRequested += (_, e) => request = e;
        runner.SendCommand.Execute(_vm.AllCommands[0]).Subscribe();
        Assert.IsNotNull(request);
        Assert.AreSequenceEqual([currentId], [.. request.TargetIds]);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task AddCommand_AddsCustomCommandToListAndPersists()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        Assert.IsTrue(_vm.IsAddingCommand);
        string ungrouped = Core.Resources.Strings.Get("QuickCmd_Ungrouped");
        Assert.AreEqual(ungrouped, _vm.NewCategory);
        _vm.NewName = "my-cmd";
        _vm.NewCommandText = "echo hello";
        _vm.NewDescription = "Says hello";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        Assert.HasCount(BuiltInCount + 1, _vm.AllCommands);
        Assert.IsFalse(_vm.IsAddingCommand);
        QuickCommandViewModel added = _vm.AllCommands.Last();
        Assert.AreEqual("my-cmd", added.Name);
        Assert.AreEqual("echo hello", added.CommandText);
        Assert.AreEqual("Says hello", added.Description);
        Assert.AreEqual(ungrouped, added.Category);
        Assert.IsFalse(added.IsBuiltIn);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task ExecuteAfterSend_PersistsThroughEditingReloadAndSync()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "execute";
        _vm.NewCommandText = "echo hello";
        _vm.NewExecuteAfterSend = true;
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        QuickCommandViewModel added = (await ReloadAsync()).AllCommands.Single(c => c.Name == "execute");
        Assert.IsTrue(added.ExecuteAfterSend);

        QuickCommandSyncData exported = await _repository.ExportSyncAsync();
        Assert.IsTrue(exported.Commands.Single(c => c.Id == added.Id).ExecuteAfterSend);
        await _repository.SaveAsync(new QuickCommandData());
        await _repository.ApplySyncAsync(exported);
        QuickCommandsViewModel reloaded = await ReloadAsync();
        QuickCommandViewModel synced = reloaded.AllCommands.Single(c => c.Id == added.Id);
        Assert.IsTrue(synced.ExecuteAfterSend);

        reloaded.BeginEditCommand.Execute(synced).Subscribe();
        Assert.IsTrue(reloaded.NewExecuteAfterSend);
        reloaded.NewExecuteAfterSend = false;
        await reloaded.SaveEditCommand.Execute().FirstAsync();
        Assert.IsFalse((await ReloadAsync()).AllCommands.Single(c => c.Id == added.Id).ExecuteAfterSend);

        _vm.AddCommandCommand.Execute().Subscribe();
        Assert.IsFalse(_vm.NewExecuteAfterSend, "新片段不能沿用上一条的自动执行选项");
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task ExecuteAfterSend_OnlyChangeToBuiltIn_IsSavedAndRestored()
    {
        QuickCommandViewModel builtIn = _vm.AllCommands.Single(c => c.Id == SampleBuiltIn.Id);
        _vm.BeginEditCommand.Execute(builtIn).Subscribe();
        _vm.NewExecuteAfterSend = true;
        await _vm.SaveEditCommand.Execute().FirstAsync();

        QuickCommandsViewModel reloaded = await ReloadAsync();
        QuickCommandViewModel edited = reloaded.AllCommands.Single(c => c.Id == builtIn.Id);
        Assert.IsTrue(edited.IsBuiltInOverride);
        Assert.IsTrue(edited.ExecuteAfterSend);
        await reloaded.RestoreBuiltInCommand.Execute(edited).FirstAsync();
        Assert.IsFalse((await ReloadAsync()).AllCommands.Single(c => c.Id == builtIn.Id).ExecuteAfterSend);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task OldSnippetData_WithoutExecuteAfterSend_DoesNotSubmit()
    {
        QuickCommandData old = System.Text.Json.JsonSerializer.Deserialize<QuickCommandData>(
            """{"SchemaVersion":2,"Commands":[{"Name":"old","CommandText":"echo old"}]}""")!;
        await _repository.SaveAsync(old);
        QuickCommandsViewModel reloaded = await ReloadAsync();
        var runner = new QuickCommandRunnerViewModel(reloaded);
        Guid target = Guid.NewGuid();
        runner.UpdateTargets([(target, "terminal")]);
        runner.SetCurrentTarget(target);
        QuickCommandExecutionRequest? request = null;
        runner.ExecutionRequested += (_, e) => request = e;

        runner.SendCommand.Execute(reloaded.AllCommands.Single(c => c.Name == "old")).Subscribe();

        Assert.IsNotNull(request);
        Assert.IsFalse(request.ExecuteAfterSend);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task DeleteCommand_RemovesCustomCommand()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "temp-cmd";
        _vm.NewCommandText = "ls -la";
        _vm.NewDescription = "List files";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        QuickCommandViewModel customCmd = _vm.AllCommands.First(c => c.Name == "temp-cmd");
        await _vm.DeleteCommandCommand.Execute(customCmd).FirstAsync();
        Assert.DoesNotContain(c => c.Name == "temp-cmd", _vm.AllCommands);
        Assert.HasCount(BuiltInCount, _vm.AllCommands);
        Assert.IsFalse(_vm.HasBuiltInChanges, "删的是自定义命令,不该冒出「恢复内置命令」。");
    }

    /// <summary>#555:内置命令删得掉,删掉的重启后仍不出现,「恢复内置命令」能把它找回来。</summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task DeleteBuiltIn_StaysHiddenAfterReload_UntilRestored()
    {
        QuickCommandViewModel builtIn = _vm.AllCommands.Single(c => c.Id == SampleBuiltIn.Id);

        await _vm.DeleteCommandCommand.Execute(builtIn).FirstAsync();

        Assert.DoesNotContain(c => c.Id == SampleBuiltIn.Id, _vm.AllCommands);
        Assert.IsTrue(_vm.HasBuiltInChanges);
        QuickCommandsViewModel reloaded = await ReloadAsync();
        Assert.DoesNotContain(c => c.Id == SampleBuiltIn.Id, reloaded.AllCommands);
        Assert.HasCount(BuiltInCount - 1, reloaded.AllCommands);

        await reloaded.RestoreAllBuiltInsCommand.Execute().FirstAsync();

        Assert.IsTrue(reloaded.AllCommands.Single(c => c.Id == SampleBuiltIn.Id).IsBuiltIn);
        Assert.IsFalse(reloaded.HasBuiltInChanges);
        Assert.HasCount(BuiltInCount, (await ReloadAsync()).AllCommands);
    }

    /// <summary>
    /// 改内置命令 = 原地换上一条同标识的自定义命令:位置不变、重启后还在原处;
    /// 「恢复默认」把目录里的原样放回同一个位置。
    /// </summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task EditBuiltIn_ReplacesItInPlace_AndRestoreBringsBackTheOriginal()
    {
        QuickCommandGroupViewModel group = _vm.Groups.First(g => g.Commands.Count >= 3);
        QuickCommandViewModel original = group.Commands[1];
        Assert.IsTrue(original.IsBuiltIn, "样本前提:内置分组里的命令应是内置命令");
        string originalText = original.CommandText;

        _vm.BeginEditCommand.Execute(original).Subscribe();
        _vm.NewCommandText = originalText + " --edited";
        await _vm.SaveEditCommand.Execute().FirstAsync();

        QuickCommandViewModel edited = group.Commands[1];
        Assert.AreEqual(original.Id, edited.Id);
        Assert.IsFalse(edited.IsBuiltIn);
        Assert.IsTrue(edited.IsBuiltInOverride);
        Assert.AreEqual(originalText + " --edited", edited.CommandText);
        Assert.ContainsSingle(c => c.Id == original.Id, _vm.AllCommands);
        Assert.IsTrue(_vm.HasBuiltInChanges);

        QuickCommandsViewModel reloaded = await ReloadAsync();
        QuickCommandGroupViewModel reloadedGroup = reloaded.Groups.Single(g => g.Id == group.Id);
        Assert.AreEqual(original.Id, reloadedGroup.Commands[1].Id, "改过的内置命令重启后应留在原来的位置");
        Assert.AreEqual(originalText + " --edited", reloadedGroup.Commands[1].CommandText);

        await reloaded.RestoreBuiltInCommand.Execute(reloadedGroup.Commands[1]).FirstAsync();

        QuickCommandViewModel restored = reloaded.Groups.Single(g => g.Id == group.Id).Commands[1];
        Assert.AreEqual(original.Id, restored.Id);
        Assert.IsTrue(restored.IsBuiltIn);
        Assert.AreEqual(originalText, restored.CommandText);
        Assert.IsFalse(reloaded.HasBuiltInChanges);
        Assert.AreEqual(
            originalText,
            (await ReloadAsync()).Groups.Single(g => g.Id == group.Id).Commands[1].CommandText
        );
    }

    /// <summary>打开内置命令的编辑框、什么都不改就保存:它仍是原样的内置命令,不留一条一模一样的副本。</summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task EditBuiltIn_WithoutChanges_KeepsItBuiltIn()
    {
        QuickCommandViewModel builtIn = _vm.AllCommands.Single(c => c.Id == SampleBuiltIn.Id);

        _vm.BeginEditCommand.Execute(builtIn).Subscribe();
        await _vm.SaveEditCommand.Execute().FirstAsync();

        Assert.IsNull(_vm.EditingCommand);
        Assert.IsTrue(_vm.AllCommands.Single(c => c.Id == SampleBuiltIn.Id).IsBuiltIn);
        Assert.IsFalse(_vm.HasBuiltInChanges);
    }

    /// <summary>删掉一条改过的内置命令:是删掉,不是恢复默认 —— 目录里的原样不能冒出来。</summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task DeleteEditedBuiltIn_DoesNotBringBackTheOriginal()
    {
        QuickCommandViewModel builtIn = _vm.AllCommands.Single(c => c.Id == SampleBuiltIn.Id);
        _vm.BeginEditCommand.Execute(builtIn).Subscribe();
        _vm.NewName = "renamed";
        await _vm.SaveEditCommand.Execute().FirstAsync();

        await _vm.DeleteCommandCommand.Execute(_vm.AllCommands.Single(c => c.Id == SampleBuiltIn.Id)).FirstAsync();

        Assert.DoesNotContain(c => c.Id == SampleBuiltIn.Id, _vm.AllCommands);
        Assert.DoesNotContain(c => c.Id == SampleBuiltIn.Id, (await ReloadAsync()).AllCommands);
    }

    /// <summary>「恢复内置命令」只动内置命令:删掉的回来、改过的还原,自己建的命令原样保留。</summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task RestoreAllBuiltIns_KeepsCustomCommands()
    {
        QuickCommand[] samples = [.. QuickCommandCatalog.BuiltIns.Take(2)];
        await _vm.DeleteCommandCommand.Execute(_vm.AllCommands.Single(c => c.Id == samples[0].Id)).FirstAsync();
        _vm.BeginEditCommand.Execute(_vm.AllCommands.Single(c => c.Id == samples[1].Id)).Subscribe();
        _vm.NewName = "renamed";
        await _vm.SaveEditCommand.Execute().FirstAsync();
        _vm.NewName = "mine";
        _vm.NewCommandText = "echo mine";
        _vm.NewCategory = "Ops";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();

        await _vm.RestoreAllBuiltInsCommand.Execute().FirstAsync();

        Assert.HasCount(BuiltInCount + 1, _vm.AllCommands);
        Assert.AreEqual(BuiltInCount, _vm.AllCommands.Count(c => c.IsBuiltIn));
        Assert.AreEqual(samples[1].Name, _vm.AllCommands.Single(c => c.Id == samples[1].Id).Name);
        Assert.ContainsSingle(c => c.Name == "mine", (await ReloadAsync()).AllCommands);
    }

    /// <summary>组内拖动:内置命令也能排,排出来的顺序重启后还在,内置命令仍是内置命令。</summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task MoveCommand_WithinGroup_PersistsOrderOfBuiltIns()
    {
        QuickCommandGroupViewModel group = _vm.Groups.First(g => g.Commands.Count >= 3);
        QuickCommandViewModel last = group.Commands[^1];
        Guid[] expected = [last.Id, .. group.Commands.Take(group.Commands.Count - 1).Select(c => c.Id)];

        Assert.IsTrue(await _vm.MoveCommandAsync(last, group, group.Commands[0]));

        Assert.AreSequenceEqual(expected, [.. group.Commands.Select(c => c.Id)]);
        Assert.IsTrue(group.Commands[0].IsBuiltIn, "组内挪动不该把内置命令变成自定义命令");
        Assert.IsFalse(_vm.HasBuiltInChanges);
        Assert.AreSequenceEqual(
            expected,
            [.. (await ReloadAsync()).Groups.Single(g => g.Id == group.Id).Commands.Select(c => c.Id)]
        );
    }

    /// <summary>落回原处(自己前面或后面一格)什么都不做,分组也不被记成「排过序」。</summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task MoveCommand_BackToItsOwnPlace_IsANoOp()
    {
        QuickCommandGroupViewModel group = _vm.Groups.First(g => g.Commands.Count >= 3);
        QuickCommandViewModel middle = group.Commands[1];

        Assert.IsFalse(await _vm.MoveCommandAsync(middle, group, middle));
        Assert.IsFalse(await _vm.MoveCommandAsync(middle, group, group.Commands[2]));

        Assert.AreSame(middle, group.Commands[1]);
        Assert.IsFalse(group.HasCustomOrder);
    }

    /// <summary>
    /// 内置命令的分组由目录定死,拖进别的分组就换成一条同标识的自定义命令;
    /// 「恢复默认」把它送回目录定的分组。
    /// </summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task MoveBuiltInToAnotherGroup_BecomesAnOverride_AndRestoreSendsItHome()
    {
        QuickCommandGroupViewModel home = _vm.Groups.Single(g => g.Id == SampleBuiltIn.GroupId);
        QuickCommandGroupViewModel other = _vm.Groups.First(g => g.Kind == QuickCommandGroupKind.BuiltIn && g.Id != home.Id);
        QuickCommandViewModel builtIn = home.Commands.Single(c => c.Id == SampleBuiltIn.Id);

        Assert.IsTrue(await _vm.MoveCommandAsync(builtIn, other, null));

        QuickCommandViewModel moved = other.Commands[^1];
        Assert.AreEqual(SampleBuiltIn.Id, moved.Id);
        Assert.IsTrue(moved.IsBuiltInOverride);
        Assert.AreEqual(other.Name, moved.Category);
        Assert.DoesNotContain(c => c.Id == SampleBuiltIn.Id, home.Commands);

        QuickCommandsViewModel reloaded = await ReloadAsync();
        Assert.Contains(c => c.Id == SampleBuiltIn.Id, reloaded.Groups.Single(g => g.Id == other.Id).Commands);

        await reloaded.RestoreBuiltInCommand.Execute(reloaded.AllCommands.Single(c => c.Id == SampleBuiltIn.Id)).FirstAsync();

        Assert.IsTrue(reloaded.Groups.Single(g => g.Id == home.Id).Commands.Single(c => c.Id == SampleBuiltIn.Id).IsBuiltIn);
        Assert.DoesNotContain(c => c.Id == SampleBuiltIn.Id, reloaded.Groups.Single(g => g.Id == other.Id).Commands);
    }

    /// <summary>自定义命令拖进别的分组,换的是它自己的分组,重启后仍在新分组的那个位置。</summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task MoveCustomCommand_AcrossGroups_PersistsGroupAndPosition()
    {
        _vm.NewName = "deploy";
        _vm.NewCommandText = "./deploy.sh";
        _vm.NewCategory = "Ops";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        QuickCommandViewModel deploy = _vm.AllCommands.Single(c => c.Name == "deploy");
        QuickCommandGroupViewModel target = _vm.Groups.First(g => g.Commands.Count >= 2 && g.Kind == QuickCommandGroupKind.BuiltIn);

        Assert.IsTrue(await _vm.MoveCommandAsync(deploy, target, target.Commands[1]));

        Assert.AreSame(deploy, target.Commands[1]);
        Assert.AreEqual(target.Id, deploy.GroupId);
        QuickCommandGroupViewModel reloaded = (await ReloadAsync()).Groups.Single(g => g.Id == target.Id);
        Assert.AreEqual("deploy", reloaded.Commands[1].Name);
        Assert.AreEqual(target.Name, reloaded.Commands[1].Category);
    }

    /// <summary>分组拖动:顺序重启后还在;「未分组」固定垫底,拖不动,也没有分组能排到它后面。</summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task MoveGroup_PersistsOrder_AndUngroupedStaysLast()
    {
        _vm.NewName = "loose";
        _vm.NewCommandText = "pwd";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        QuickCommandGroupViewModel ungrouped = _vm.Groups.Single(g => g.IsDefault);
        QuickCommandGroupViewModel first = _vm.Groups[0];
        QuickCommandGroupViewModel lastBuiltIn = _vm.Groups.Last(g => g.Kind == QuickCommandGroupKind.BuiltIn);

        Assert.IsTrue(await _vm.MoveGroupAsync(lastBuiltIn, first));
        Assert.IsTrue(await _vm.MoveGroupAsync(first, null));
        Assert.IsFalse(await _vm.MoveGroupAsync(ungrouped, _vm.Groups[0]), "「未分组」不该能拖");

        Guid[] expected = [.. _vm.Groups.Select(g => g.Id)];
        Assert.AreEqual(lastBuiltIn.Id, expected[0]);
        Assert.AreEqual(first.Id, expected[^2], "排到最后的分组应在「未分组」之前");
        Assert.AreEqual(ungrouped.Id, expected[^1]);
        Assert.AreSequenceEqual(expected, [.. (await ReloadAsync()).Groups.Select(g => g.Id)]);
    }

    /// <summary>多行命令:换行统一成 \n、首尾空白去掉;侧栏用的首行与「+N 行」跟着正文走。</summary>
    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task NewMultilineCommand_NormalizesLineEndings_AndExposesPreview()
    {
        _vm.NewName = "multi";
        _vm.NewCommandText = "  cd /srv\r\ngit pull\r\nsystemctl restart app\r\n";

        await _vm.SaveNewCommandCommand.Execute().FirstAsync();

        QuickCommandViewModel multi = _vm.AllCommands.Single(c => c.Name == "multi");
        Assert.AreEqual("cd /srv\ngit pull\nsystemctl restart app", multi.CommandText);
        Assert.IsTrue(multi.IsMultiline);
        Assert.AreEqual("cd /srv", multi.CommandPreview);
        Assert.AreEqual(Core.Resources.Strings.Format("QuickCmd_MoreLines", 2), multi.MoreLinesText);
        Assert.AreEqual(
            "cd /srv\ngit pull\nsystemctl restart app",
            (await ReloadAsync()).AllCommands.Single(c => c.Name == "multi").CommandText
        );
    }

    /// <summary>重新开一个仓储与视图模型,从落盘的数据装载 —— 等同于重启。</summary>
    private async Task<QuickCommandsViewModel> ReloadAsync()
    {
        var reloaded = new QuickCommandsViewModel(
            new SonnetDbQuickCommandRepository(_dataStore, _legacyDataPath)
        );
        await reloaded.LoadAsync();
        return reloaded;
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_EmptyString_ShowsAllCommands()
    {
        _vm.SearchQuery = "docker";
        Assert.IsLessThan(
            BuiltInCount,
            _vm.FilteredCommands.Count,
            "样本前提:该查询应筛掉一部分命令"
        );

        _vm.SearchQuery = "";

        Assert.HasCount(BuiltInCount, _vm.FilteredCommands);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Categories_ContainAllDistinctCategories()
    {
        var expected = QuickCommandGroupCatalog
            .BuiltIns.Select(group => group.Name)
            .Append(Core.Resources.Strings.Get("QuickCmd_Ungrouped"))
            .ToList();
        Assert.AreSequenceEqual(
            expected, [.. _vm.Categories], "分类应为内置目录去重排序后的结果"
        );
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void BuiltInCommand_CannotBeModified()
    {
        QuickCommandViewModel builtIn = _vm.AllCommands.First(c => c.Name == SampleBuiltIn.Name);

        builtIn.Name = "modified";
        builtIn.CommandText = "modified";
        builtIn.Description = "modified";

        // 三个字段都该纹丝不动(描述是本地化文案,拿目录里的原值比,不绑死语言)。
        Assert.AreEqual(SampleBuiltIn.Name, builtIn.Name);
        Assert.AreEqual(SampleBuiltIn.CommandText, builtIn.CommandText);
        Assert.AreEqual(SampleBuiltIn.Description, builtIn.Description);
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
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        var vm2 = new QuickCommandsViewModel(
            new SonnetDbQuickCommandRepository(_dataStore, _legacyDataPath)
        );
        await vm2.LoadAsync();
        Assert.HasCount(BuiltInCount + 1, vm2.AllCommands);
        QuickCommandViewModel restored = vm2.AllCommands.First(c => c.Name == "persisted-cmd");
        Assert.AreEqual("uptime", restored.CommandText);
        Assert.IsFalse(restored.IsBuiltIn);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task LoadAsync_RepeatedCall_DoesNotDuplicateCustomCommands()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "single-load";
        _vm.NewCommandText = "whoami";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        var vm2 = new QuickCommandsViewModel(
            new SonnetDbQuickCommandRepository(_dataStore, _legacyDataPath)
        );

        await vm2.LoadAsync();
        await vm2.LoadAsync();

        Assert.ContainsSingle(command => command.Name == "single-load", vm2.AllCommands);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Search_ExpandsMatchingGroup_ThenRestoresCollapsedState()
    {
        QuickCommandGroupViewModel group = _vm.Groups.First(item => item.Commands.Count > 0);
        QuickCommandViewModel command = group.Commands[0];
        group.IsExpanded = false;

        _vm.SearchQuery = command.Name;

        Assert.IsTrue(group.IsExpanded);
        Assert.Contains(group, _vm.FilteredGroups);

        _vm.SearchQuery = string.Empty;

        Assert.IsFalse(group.IsExpanded);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task EditCommand_MovesItToNewPersistedGroup()
    {
        _vm.NewName = "deploy";
        _vm.NewCommandText = "./deploy.sh";
        _vm.NewCategory = "Ops";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        QuickCommandViewModel command = _vm.AllCommands.Single(item => item.Name == "deploy");

        _vm.BeginEditCommand.Execute(command).Subscribe();
        _vm.NewCategory = "Release";
        await _vm.SaveEditCommand.Execute().FirstAsync();

        Assert.AreEqual("Release", command.Category);
        var vm2 = new QuickCommandsViewModel(
            new SonnetDbQuickCommandRepository(_dataStore, _legacyDataPath)
        );
        await vm2.LoadAsync();
        Assert.AreEqual("Release", vm2.AllCommands.Single(item => item.Name == "deploy").Category);
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
