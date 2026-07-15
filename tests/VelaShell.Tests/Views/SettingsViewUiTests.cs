using System.Reactive.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>设置窗口键盘交互的 Headless UI 回归测试。</summary>
[TestClass]
[TestCategory("SettingsUi")]
public class SettingsViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    [TestMethod]
    public void Escape_FromTextBox_ClosesWindowAndRollsBackPreview()
    {
        OnUi(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings { Theme = "dark" });
            var viewModel = new SettingsViewModel(settings, theme);
            await viewModel.LoadCommand.Execute().FirstAsync();
            viewModel.Theme = "light";
            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox textBox = window.GetVisualDescendants().OfType<TextBox>().First();
            textBox.Focus();

            textBox.RaiseEvent(
                new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }
            );
            Dispatcher.UIThread.RunJobs();

            Assert.IsFalse(window.IsVisible);
            theme.Received().SetTheme("dark");
        });
    }

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();

    private sealed class HeadlessTestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Resources.MergedDictionaries.Add(
                LoadDictionary("avares://VelaShell.Controls/Themes/Generic.axaml")
            );
            Resources.MergedDictionaries.Add(
                LoadDictionary("avares://VelaShell.Controls/Themes/VelaTokens.axaml")
            );
            Resources.MergedDictionaries.Add(
                LoadDictionary("avares://VelaShell.Controls/Themes/VelaShellTokens.axaml")
            );
            Resources.MergedDictionaries.Add(
                LoadDictionary("avares://VelaShell.Controls/Themes/Icons.axaml")
            );
        }

        private static ResourceInclude LoadDictionary(string uri) =>
            new(new Uri(uri)) { Source = new(uri) };
    }
}
