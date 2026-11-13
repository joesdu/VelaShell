using Avalonia.Controls;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void ImageModelSelection_UsesIdsAndPreservesMissingSelection()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var first = new AiModelConfig { Name = "First", Model = "gpt-image-1" };
            var second = new AiModelConfig { Name = "Second", Model = "gpt-image-2" };
            var text = new AiModelConfig { Name = "Text", Model = "gpt-4" };
            var provider = new AiProvider { Name = "Provider", Models = [first, second, text] };
            var settings = new AiSettings { Providers = [provider], ImageModelId = second.Id };
            AiSettings? submitted = null;
            var loc = new Loc("en");
            var view = new GlobalSettingsView(context, new AiSettingsStore(context), settings, loc,
                draft => { submitted = draft; return Task.CompletedTask; }, _ => { });
            var window = new Window { Width = 640, Height = 700, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                ComboBox combo = FindIn<ComboBox>(view, "ImageModelCombo");
                CheckBox enabled = FindIn<CheckBox>(view, "ImageEnabledCheck");
                Assert.IsTrue(enabled.IsChecked);
                Assert.AreEqual(3, combo.ItemCount);
                Assert.Contains("Second", combo.SelectedItem!.ToString());
                provider.Models.Remove(first);
                view.RefreshFromProviders();
                Assert.Contains("Second", combo.SelectedItem!.ToString());
                provider.Models.Remove(second);
                view.RefreshFromProviders();
                Assert.Contains(second.Id, combo.SelectedItem!.ToString());
                Assert.Contains("Missing", combo.SelectedItem!.ToString());
                loc.Switch("zh-Hans");
                view.ApplyLoc();
                Assert.Contains(second.Id, combo.SelectedItem!.ToString());
                RaiseClick(FindIn<Button>(view, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => submitted is not null));
                Assert.AreEqual(second.Id, submitted!.ImageModelId);
                combo.SelectedIndex = 0;
                RaiseClick(FindIn<Button>(view, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => submitted!.ImageModelId is null));
                Assert.IsTrue(submitted!.ImageToolsEnabled, "Automatic mode must remain enabled without a default.");
                enabled.IsChecked = false;
                RaiseClick(FindIn<Button>(view, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => !submitted!.ImageToolsEnabled));
            }
            finally { window.Close(); }
        });
    }
}
