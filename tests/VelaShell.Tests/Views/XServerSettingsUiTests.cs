using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Core.XServer;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

/// <summary>
/// 设置 → X Server 页与 VcXsrv 参数帮助对话框:真渲染出来,下拉是双向的,帮助的过滤能用。
/// </summary>
[TestClass]
[TestCategory("SettingsUi")]
public sealed class XServerSettingsUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(XServerSettingsUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void Page_Renders_AndDropdownsWriteBack() =>
        OnXServerPage((page, viewModel) =>
        {
            ComboBox display = Find<ComboBox>(page, "DisplayNumberCombo");
            ComboBox mode = Find<ComboBox>(page, "WindowModeCombo");
            ComboBox layout = Find<ComboBox>(page, "KeyboardLayoutCombo");

            Assert.AreEqual(XServerOptions.MaxDisplayNumber + 2, display.ItemCount);
            Assert.AreEqual(XServerWindowModes.All.Count, mode.ItemCount,
                            "窗口模式下拉的条目数与 XServerWindowModes.All 对不上 —— 选中项会映射到错误的模式");
            Assert.AreEqual(viewModel.XServerKeyboardLayouts.Length, layout.ItemCount);

            mode.SelectedIndex = 3;
            display.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(XServerWindowModes.Fullscreen, viewModel.XServer.WindowMode);
            Assert.AreEqual(1, viewModel.XServer.DisplayNumber);
            StringAssert.Contains(Find<SelectableTextBlock>(page, "CommandPreviewText").Text, "-fullscreen");
        });

    [TestMethod]
    public void HelpDialog_ListsEverything_AndFilters() =>
        _session.Dispatch(() =>
        {
            var dialog = new XServerHelpDialog();
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            int total = XServerHelpCatalog.Groups.Sum(g => g.Entries.Count);
            Assert.AreEqual(total, dialog.VisibleSections.Sum(s => s.Rows.Count));

            dialog.ApplyFilter("multiwindow");
            Assert.IsTrue(dialog.VisibleSections.SelectMany(s => s.Rows).Any(r => r.Syntax == "-multiwindow"));
            Assert.IsLessThan(total, dialog.VisibleSections.Sum(s => s.Rows.Count));

            // 扩展名单也参与匹配:搜 RANDR 落到「扩展」一组。
            dialog.ApplyFilter("randr");
            Assert.IsTrue(dialog.VisibleSections.Any(s => s.HasExtensions));

            dialog.ApplyFilter("zzz-no-such-thing");
            Assert.IsEmpty(dialog.VisibleSections);

            dialog.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void OnXServerPage(Action<XServerSettingsPage, SettingsViewModel> body) =>
        _session.Dispatch(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            ILocalXServer server = Substitute.For<ILocalXServer>();
            server.IsSupported.Returns(true);
            var viewModel = new SettingsViewModel(settings, theme, localXServer: server);
            await viewModel.LoadCommand.Execute().FirstAsync();

            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            viewModel.SelectSection(SettingsSectionKey.XServer);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            XServerSettingsPage page = window.GetVisualDescendants().OfType<XServerSettingsPage>().Single();
            body(page, viewModel);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
