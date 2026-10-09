using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 导出 / 导入连接文件与批量修改三个对话框(#571)的界面:动作按钮走共享主题(DESIGN.md §5.1)、
/// 预览行真的渲染出来、几个只在某些条件下出现的区域按条件出现。
/// </summary>
/// <remarks>设了 <c>VELASHELL_VISUAL_QA_DIR</c> 时把每个对话框的画面存成 PNG,供人眼核对。</remarks>
[TestClass]
[TestCategory("SessionArchive")]
public class SessionTransferDialogsUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionTransferDialogsUiTests).Assembly);

    [TestMethod]
    public void ExportDialog_UsesSharedButtonThemes_AndShowsPassphraseFieldsOnlyWhenNeeded()
    {
        _session.Dispatch(async () =>
        {
            ISessionArchiveService service = Substitute.For<ISessionArchiveService>();
            service.PreviewExportAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(new SessionExportPreview(12, 10, 2, 3, 4));
            var viewModel = new SessionExportViewModel(service, new SessionExportRequest([Guid.NewGuid()], "生产环境"));
            var window = new SessionExportView { DataContext = viewModel };
            window.Show();
            await viewModel.InitializeAsync();
            Dispatcher.UIThread.RunJobs();

            Button export = Named<Button>(window, "ExportButton");
            Button cancel = Named<Button>(window, "CancelButton");
            Assert.AreEqual(Theme("VelaAccentPillButtonTheme"), export.Theme);
            Assert.AreEqual(Theme("VelaOutlineButtonTheme"), cancel.Theme);
            Assert.AreEqual(export.Bounds.Height, cancel.Bounds.Height, "同一条动作条上的按钮要等高");
            Assert.IsTrue(export.IsEnabled);

            Assert.IsFalse(PassphraseBoxes(window).Any(static box => box.IsEffectivelyVisible), "没勾敏感信息时不出现口令框");
            viewModel.IncludeSecrets = true;
            Dispatcher.UIThread.RunJobs();
            Assert.HasCount(2, PassphraseBoxes(window).Where(static box => box.IsEffectivelyVisible).ToList());
            Assert.IsFalse(export.IsEnabled, "勾了敏感信息、还没设口令:导出按钮是灰的");
            SaveOptionalFrame(window, "session-export-secrets.png");

            viewModel.Format = SessionFileFormat.Csv;
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(Named<CheckBox>(window, "IncludeSecretsCheckBox").IsEffectivelyVisible, "CSV 没有敏感信息这一项");
            Assert.IsTrue(export.IsEnabled);
            SaveOptionalFrame(window, "session-export-csv.png");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ImportDialog_RendersEveryKindOfRow_AndTheImportButtonCarriesTheCount()
    {
        _session.Dispatch(async () =>
        {
            const string csv = "name,host,username,group,port,password\n"
                               + "web,10.0.0.1,root,Prod,,\n"
                               + "new-a,10.0.0.2,root,Prod,,secret\n"
                               + "new-b,10.0.0.3,root,Lab,,\n"
                               + "broken,10.0.0.4,root,,99999,\n";
            SessionImportDocument document = SessionCsv.Read("hosts.csv", csv);
            var prod = new ServerGroup { Name = "Prod" };
            SessionImportPlan plan = SessionImportPlanner.Plan(document,
                [new SessionProfile { Name = "web", Host = "10.0.0.1", Username = "root" }], [prod], []);
            ISessionArchiveService service = Substitute.For<ISessionArchiveService>();
            service.PlanAsync(document, Arg.Any<CancellationToken>()).Returns(plan);
            var viewModel = new SessionFileImportViewModel(service, document);
            var window = new SessionFileImportView { DataContext = viewModel };
            window.Show();
            await viewModel.InitializeAsync();
            Dispatcher.UIThread.RunJobs();

            Button import = Named<Button>(window, "ImportButton");
            Button cancel = Named<Button>(window, "CancelButton");
            Assert.AreEqual(Theme("VelaAccentPillButtonTheme"), import.Theme);
            Assert.AreEqual(Theme("VelaOutlineButtonTheme"), cancel.Theme);
            Assert.AreEqual(import.Bounds.Height, cancel.Bounds.Height);
            Assert.IsTrue(import.IsEffectivelyEnabled);
            Assert.Contains(static text => text.Text?.Contains('2') == true, import.GetVisualDescendants().OfType<TextBlock>(), "按钮上写着要导入几条");

            List<string> names = [.. window.GetVisualDescendants().OfType<TextBlock>()
                .Where(static t => t.DataContext is SessionFileImportItemViewModel && t.FontWeight == Avalonia.Media.FontWeight.Medium)
                .Select(static t => t.Text ?? string.Empty)];
            CollectionAssert.IsSubsetOf(new[] { "web", "new-a", "new-b", "broken" }, names, "四行都要渲染出来");
            List<CheckBox> boxes = [.. window.GetVisualDescendants().OfType<CheckBox>()
                .Where(static box => box.DataContext is SessionFileImportItemViewModel)];
            Assert.HasCount(4, boxes);
            Assert.IsFalse(boxes.Single(b => ((SessionFileImportItemViewModel)b.DataContext!).Name == "web").IsEnabled, "重复的行(默认跳过)勾不上");
            SaveOptionalFrame(window, "session-import.png");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ImportDialog_CannotBeClosedWhileItIsWriting()
    {
        _session.Dispatch(async () =>
        {
            SessionImportDocument document = SessionCsv.Read("hosts.csv", "host\n10.0.0.2\n");
            SessionImportPlan plan = SessionImportPlanner.Plan(document, [], [], []);
            var writing = new TaskCompletionSource<SessionFileImportOutcome>();
            ISessionArchiveService service = Substitute.For<ISessionArchiveService>();
            service.PlanAsync(document, Arg.Any<CancellationToken>()).Returns(plan);
            service.ImportAsync(Arg.Any<SessionImportPlan>(), Arg.Any<SessionImportOptions>(), Arg.Any<CancellationToken>())
                .Returns(writing.Task);
            var viewModel = new SessionFileImportViewModel(service, document);
            var window = new SessionFileImportView { DataContext = viewModel };
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            await viewModel.InitializeAsync();
            Dispatcher.UIThread.RunJobs();

            viewModel.ImportCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(viewModel.IsBusy);
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(closed, "写库途中关掉,写入照样做完,宿主却不知道要刷新资源管理器");

            writing.SetResult(new SessionFileImportOutcome(1, 0, 0, 0, 0, []));
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(closed, "写完拿到结果就照常关掉");
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void BatchEditDialog_UsesSharedButtonThemes_AndApplyStaysOffUntilSomethingIsTicked()
    {
        _session.Dispatch(() =>
        {
            SessionProfile[] targets =
            [
                new() { Name = "a", Host = "10.0.0.1", Username = "root" },
                new() { Name = "b", Host = "10.0.0.2", Username = "root" },
                new() { Name = "ftp", Host = "10.0.0.3", ConnectionType = ConnectionType.FTP }
            ];
            var bastion = new SessionProfile { Name = "bastion", Host = "203.0.113.1" };
            var viewModel = new SessionBatchEditViewModel(targets, [.. targets, bastion],
                [new SharedCredential { Name = "ops", Username = "ops" }], Substitute.For<ISessionRepository>());
            var window = new SessionBatchEditView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Button apply = Named<Button>(window, "ApplyButton");
            Button cancel = Named<Button>(window, "CancelButton");
            Assert.AreEqual(Theme("VelaAccentPillButtonTheme"), apply.Theme);
            Assert.AreEqual(Theme("VelaOutlineButtonTheme"), cancel.Theme);
            Assert.AreEqual(apply.Bounds.Height, cancel.Bounds.Height);
            Assert.IsFalse(apply.IsEffectivelyEnabled, "一项都没勾:不能应用");

            Named<CheckBox>(window, "ChangeUsernameBox").IsChecked = true;
            viewModel.ChangeAuth = true;
            viewModel.AuthKind = SessionBatchAuthKind.PrivateKey;
            viewModel.PrivateKeyPath = "~/.ssh/id_ed25519";
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(apply.IsEffectivelyEnabled);
            SaveOptionalFrame(window, "session-batch-edit.png");

            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static IEnumerable<TextBox> PassphraseBoxes(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>()
            .Where(static box => VelaShell.Behaviors.SecurePasswordBox.GetEnabled(box));

    private static ControlTheme Theme(string key) =>
        Application.Current!.TryGetResource(key, null, out object? value) && value is ControlTheme theme
            ? theme
            : throw new AssertFailedException($"按钮主题 {key} 不存在");

    private static void SaveOptionalFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }
}
