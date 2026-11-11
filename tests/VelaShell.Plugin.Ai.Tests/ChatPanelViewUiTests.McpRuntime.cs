using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void McpRuntime_ShowsActualFailureAndAllowsStopping()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var manager = new McpManager(context);
            var config = new McpServerConfig { Transport = McpTransportType.Http, Url = "invalid-secret-url" };
            var settings = new AiSettings { McpServers = [config] };
            var view = new McpServersView(context, settings, new Loc("en"), () => Task.CompletedTask, manager);
            var window = new Window { Content = view };
            window.Show();
            try
            {
                await PumpAsync(2);
                Button start = view.FindControl<Button>("McpStartButton")!;
                Assert.IsTrue(start.IsEnabled);
                Assert.IsFalse(view.FindControl<Button>("McpRefreshButton")!.IsEnabled);
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(await WaitForAsync(() => view.FindControl<TextBlock>("McpRuntimeText")!.Text?.Contains("Failed") == true));
                Assert.Contains("InvalidOperationException", view.FindControl<TextBlock>("McpLastErrorText")!.Text!);
                Assert.DoesNotContain("invalid-secret-url", view.FindControl<TextBlock>("McpLastErrorText")!.Text!);
                Assert.IsTrue(start.IsEnabled);
                Assert.IsTrue(view.FindControl<Button>("McpRestartButton")!.IsEnabled);
                view.FindControl<CheckBox>("McpAutoStartCheck")!.IsChecked = false;
                view.FindControl<Button>("McpSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsFalse(config.AutoStart);
            }
            finally { window.Close(); }
        });
    }
}
