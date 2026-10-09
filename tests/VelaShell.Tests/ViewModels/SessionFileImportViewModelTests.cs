using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Credentials;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Security;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 导入连接文件对话框(#571):默认值不会改动任何已有连接,切换「重复时」立刻改变哪些行能勾,
/// 口令解锁、「放到哪里」与最终交给服务的选项。
/// </summary>
[TestClass]
[TestCategory("SessionArchive")]
public class SessionFileImportViewModelTests
{
    private readonly ISessionArchiveService _service = Substitute.For<ISessionArchiveService>();
    private readonly ServerGroup _prod = new() { Id = Guid.NewGuid(), Name = "Prod", SortOrder = 0 };
    private readonly SessionProfile _existing = new() { Name = "web", Host = "10.0.0.1", Username = "root" };

    private async Task<SessionFileImportViewModel> CreateAsync(SessionImportDocument document, Guid? target = null)
    {
        SessionImportPlan plan = SessionImportPlanner.Plan(document, [_existing], [_prod], []);
        _service.PlanAsync(document, Arg.Any<CancellationToken>()).Returns(plan);
        _service.ImportAsync(Arg.Any<SessionImportPlan>(), Arg.Any<SessionImportOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionFileImportOutcome(1, 0, 0, 0, 0, []));
        var vm = new SessionFileImportViewModel(_service, document, target);
        await vm.InitializeAsync();
        return vm;
    }

    private static SessionImportDocument Csv(string csv) => SessionCsv.Read("hosts.csv", csv);

    private const string MixedCsv =
        "name,host,username,group,port\n"
        + "web,10.0.0.1,root,Prod,\n"       // 与本机 web 重复
        + "new-a,10.0.0.2,root,Prod,\n"     // 新,进已有分组
        + "new-b,10.0.0.3,root,Lab,\n"      // 新,会新建分组
        + "broken,10.0.0.4,root,,99999\n";  // 有错误

    [TestMethod]
    public async Task Defaults_SelectOnlyNewValidRows()
    {
        SessionFileImportViewModel vm = await CreateAsync(Csv(MixedCsv));

        Assert.AreEqual(4, vm.TotalCount);
        Assert.AreEqual(2, vm.NewCount);
        Assert.AreEqual(1, vm.DuplicateCount);
        Assert.AreEqual(1, vm.ErrorCount);
        Assert.AreEqual(2, vm.SelectedCount, "重复的(默认跳过)与有错误的都不勾");
        Assert.IsFalse(vm.Items[0].CanSelect, "跳过模式下重复的行勾不上");
        Assert.IsFalse(vm.Items[3].CanSelect);
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    public async Task SwitchingToOverwrite_MakesDuplicatesSelectable_AndSelectsThem()
    {
        SessionFileImportViewModel vm = await CreateAsync(Csv(MixedCsv));
        string skipText = vm.Items[0].StatusText;

        vm.SelectedConflict = vm.ConflictOptions.Single(o => o.Conflict == SessionImportConflict.Overwrite);

        Assert.IsTrue(vm.Items[0].CanSelect);
        Assert.IsTrue(vm.Items[0].IsSelected);
        Assert.AreEqual(3, vm.SelectedCount);
        Assert.AreNotEqual(skipText, vm.Items[0].StatusText, "状态文案跟着变成「将覆盖」");
        Assert.IsFalse(vm.Items[3].CanSelect, "有错误的行无论如何都勾不上");
    }

    [TestMethod]
    public async Task GroupLabels_MarkGroupsThatWillBeCreated_AndFollowTheTarget()
    {
        SessionFileImportViewModel vm = await CreateAsync(Csv(MixedCsv));

        Assert.AreEqual("Prod", vm.Items[1].GroupLabel);
        Assert.AreNotEqual("Lab", vm.Items[2].GroupLabel, "本机没有 Lab:标出「新建」");
        Assert.Contains("Lab", vm.Items[2].GroupLabel);

        vm.SelectedGroupTarget = vm.GroupTargets.Single(t => t.GroupId == _prod.Id);

        Assert.IsTrue(vm.Items.All(i => i.GroupLabel == "Prod"), "选了目标分组就全部放进去");
    }

    [TestMethod]
    public async Task ImportIntoGroup_PresetsTheTarget()
    {
        SessionFileImportViewModel vm = await CreateAsync(Csv(MixedCsv), _prod.Id);

        Assert.IsFalse(vm.SelectedGroupTarget.FromFile);
        Assert.AreEqual(_prod.Id, vm.SelectedGroupTarget.GroupId);
    }

    [TestMethod]
    public async Task Import_PassesTheChosenRowsAndOptions()
    {
        SessionFileImportViewModel vm = await CreateAsync(Csv(MixedCsv));
        vm.Items[1].IsSelected = false;
        vm.SelectedGroupTarget = vm.GroupTargets.Single(t => t is { FromFile: false, GroupId: null });

        SessionFileImportOutcome? outcome = await vm.ImportCommand.Execute().FirstAsync();

        Assert.IsNotNull(outcome);
        await _service.Received(1).ImportAsync(
            Arg.Any<SessionImportPlan>(),
            Arg.Is<SessionImportOptions>(o =>
                o.Conflict == SessionImportConflict.Skip
                && !o.UseFileGroups
                && o.TargetGroupId == null
                && o.SelectedIndices!.SequenceEqual(new[] { 2 })),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SelectNone_DisablesImport_SelectAllSkipsWhatCannotBeChecked()
    {
        SessionFileImportViewModel vm = await CreateAsync(Csv(MixedCsv));

        await vm.SelectNoneCommand.Execute().FirstAsync();
        Assert.AreEqual(0, vm.SelectedCount);
        Assert.IsFalse(await vm.ImportCommand.CanExecute.FirstAsync());

        await vm.SelectAllCommand.Execute().FirstAsync();
        Assert.AreEqual(2, vm.SelectedCount, "全选也不会勾上跳过模式下的重复行与有错误的行");
    }

    [TestMethod]
    public async Task Unlock_WithTheWrongPassphrase_SaysSo_AndTheRightOneBringsThePasswords()
    {
        var session = new SessionProfile { Name = "db", Host = "10.9.9.9", Username = "dba", Password = "s3cret" };
        string json = SessionArchiveJson.Serialize(new SessionArchive { Sessions = [session] }, "export passphrase");
        SessionImportDocument document = SessionArchiveJson.Read("backup.json", json);
        SessionFileImportViewModel vm = await CreateAsync(document);
        Assert.IsTrue(vm.ShowUnlockFields);
        Assert.IsFalse(vm.Items[0].HasSecret);

        vm.Passphrase = SecureStringConvert.FromPlaintext("nope nope");
        await vm.UnlockCommand.Execute().FirstAsync();
        Assert.IsNotNull(vm.UnlockError);
        Assert.IsFalse(vm.SecretsUnlocked);

        vm.Passphrase = SecureStringConvert.FromPlaintext("export passphrase");
        Assert.IsNull(vm.UnlockError, "重新输入时清掉上一次的错误");
        await vm.UnlockCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.SecretsUnlocked);
        Assert.IsFalse(vm.ShowUnlockFields);
        Assert.IsTrue(vm.Items[0].HasSecret);
        Assert.Contains("1", vm.SecretsStatus);
    }

    [TestMethod]
    public async Task Initialize_CalledAgain_DoesNotDuplicateTheRows()
    {
        // 窗口的 Opened 会重发(从托盘恢复等);UI 测试里第一次真机截图就是这么拍到每条连接出现两遍的。
        SessionFileImportViewModel vm = await CreateAsync(Csv(MixedCsv));

        await vm.InitializeAsync();

        Assert.HasCount(4, vm.Items);
        Assert.HasCount(3, vm.GroupTargets, "按文件 / 未分组 / Prod");
        Assert.AreEqual(2, vm.SelectedCount);
        await _service.Received(1).PlanAsync(Arg.Any<SessionImportDocument>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Import_AStoreFailureOfAnyKind_StaysInTheDialog_AndMarksThatSomethingWasWritten()
    {
        // 代码评审指出:只接 IOException 时,别的异常会逃出 ReactiveCommand,被 ReactiveUI 的默认处理器带走整个应用。
        SessionFileImportViewModel vm = await CreateAsync(Csv(MixedCsv));
        _service.ImportAsync(Arg.Any<SessionImportPlan>(), Arg.Any<SessionImportOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<SessionFileImportOutcome>>(_ => throw new System.Data.DataException("segment locked"));
        Assert.IsFalse(vm.HasWritten);

        SessionFileImportOutcome? outcome = await vm.ImportCommand.Execute().FirstAsync();

        Assert.IsNull(outcome);
        Assert.Contains("segment locked", vm.ErrorMessage!);
        Assert.IsTrue(vm.HasWritten, "写到一半失败也算写过:宿主要据此重读资源管理器");
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    public async Task DocumentWarnings_AreShown()
    {
        SessionFileImportViewModel vm = await CreateAsync(Csv("host,rack\n10.0.0.9,A1\n"));

        Assert.IsTrue(vm.HasDocumentWarnings);
        Assert.Contains("rack", vm.DocumentWarnings);
    }
}
