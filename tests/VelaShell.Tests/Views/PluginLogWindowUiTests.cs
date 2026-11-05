using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("PluginLogUi")]
public sealed class PluginLogWindowUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PluginLogWindowUiTests).Assembly);

    [TestMethod]
    public Task Refresh_LoadsTextAndAppliesLogHighlighting() => _session.RunOnUiAsync(async () =>
    {
        const string text = "2026-10-24 [ERROR] Exception failed\\n";
        var viewModel = new PluginLogViewModel(
            "plugin", "Plugin", () => new PluginLogTail(text, null, false), null);
        var window = new PluginLogWindow { DataContext = viewModel };
        window.Show();
        try
        {
            await window.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            TextEditor editor = window.FindControl<TextEditor>("LogText")!;

            Assert.AreEqual(text, editor.Text);
            Assert.AreEqual("Log", editor.SyntaxHighlighting?.Name);
        }
        finally
        {
            window.Close();
        }
    });

    [TestMethod]
    public Task Escape_FromLogEditor_ClosesWindow() => _session.RunOnUiAsync(() =>
    {
        var viewModel = new PluginLogViewModel(
            "plugin", "Plugin", () => new PluginLogTail("[INFO] ready\n", null, false), null);
        var window = new PluginLogWindow { DataContext = viewModel };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            TextEditor editor = window.FindControl<TextEditor>("LogText")!;
            editor.Focus();
            editor.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape,
                Source = editor,
            });
            Dispatcher.UIThread.RunJobs();

            Assert.IsFalse(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });
}
