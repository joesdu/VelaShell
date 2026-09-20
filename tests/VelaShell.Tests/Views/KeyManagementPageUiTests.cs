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
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

/// <summary>
/// 密钥管理页的「生成算法」下拉:真渲染出来,条目数与视图模型的档位表一致,选择写得回去。
/// </summary>
/// <remarks>
/// <see cref="ViewModels.SshKeyChoiceCatalogTests" /> 比的是 axaml **文本**与档位表对不对得上;
/// 这条补的是另一半 —— 控件真装配起来之后,<c>SelectedIndex</c> 是不是**双向**的。
/// 绑成单向的话编译照过、下拉照样能选,但选完视图模型那边一直是 0,
/// 用户选什么都拿到 Ed25519。
/// </remarks>
[TestClass]
[TestCategory("SettingsUi")]
public sealed class KeyManagementPageUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(KeyManagementPageUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void AlgorithmSelector_HasOneItemPerChoice_AndDefaultsToTheFirst() =>
        OnKeyManagementPage((selector, viewModel) =>
        {
            Assert.AreEqual(SshKeyManagerViewModel.AlgorithmChoices.Count, selector.ItemCount,
                            "下拉条目数与 SshKeyManagerViewModel.AlgorithmChoices 对不上");
            Assert.AreEqual(0, selector.SelectedIndex, "默认选中第一档(Ed25519)");
            Assert.AreEqual(0, viewModel.SshKeys.SelectedAlgorithmIndex);
        });

    [TestMethod]
    public void AlgorithmSelector_WritesTheSelectionBackToTheViewModel() =>
        OnKeyManagementPage((selector, viewModel) =>
        {
            int last = SshKeyManagerViewModel.AlgorithmChoices.Count - 1;
            selector.SelectedIndex = last;
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(last, viewModel.SshKeys.SelectedAlgorithmIndex,
                            "SelectedIndex 没有写回视图模型 —— 绑定成单向了,用户选什么都会拿到第一档");
        });

    private static void OnKeyManagementPage(Action<ComboBox, SettingsViewModel> body) =>
        _session.Dispatch(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, theme);
            await viewModel.LoadCommand.Execute().FirstAsync();

            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            viewModel.SelectSection(SettingsSectionKey.Keys);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            KeyManagementPage page = window.GetVisualDescendants().OfType<KeyManagementPage>().Single();
            ComboBox selector = page.GetVisualDescendants()
                                    .OfType<ComboBox>()
                                    .Single(box => box.Name == "AlgorithmSelector");

            body(selector, viewModel);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
