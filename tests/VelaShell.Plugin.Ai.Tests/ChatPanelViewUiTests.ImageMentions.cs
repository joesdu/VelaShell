using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    private static async Task<ChatPanelView> ImageMentionPanel(TestPluginContext context, bool configured = true, bool automatic = false)
    {
        var store = new AiSettingsStore(context);
        AiProvider chat = StubProvider("chat", "http://127.0.0.1:1/v1", "text");
        AiProvider image = StubProvider("image", "http://127.0.0.1:1/v1", "gpt-image-2");
        if (automatic) chat.Models.Add(image.Models[0]);
        await store.SaveAsync(new AiSettings
        {
            Providers = automatic ? [chat] : [chat, image],
            ActiveModelId = chat.Models[0].Id,
            ImageModelId = configured && !automatic ? image.Models[0].Id : null,
            ImageGenerationEnabled = automatic ? true : null,
            Mode = ChatMode.Chat,
            SuggestFollowUps = false
        });
        return new ChatPanelView(context, store);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ImageMention_NoSshSessionShowsToolsAndInsertsAtomicToken(bool configured)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            ChatPanelView panel = await ImageMentionPanel(context, configured);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.TextArea.Focus();
                input.Text = "@";
                input.CaretOffset = 1;
                await PumpAsync();
                Assert.IsTrue(Find<Popup>(panel, "FilePopup").IsOpen);
                Assert.HasCount(2, Find<StackPanel>(panel, "FileList").Children);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Assert.AreEqual("@imagegen ", input.Text);
                Assert.IsFalse(Find<Popup>(panel, "FilePopup").IsOpen);
                if (!configured)
                {
                    Assert.AreEqual(new Loc("en")["ImageMentionUnavailable"], Find<TextBlock>(panel, "StatusText").Text);
                    PressEnter(input);
                    await PumpAsync();
                    Assert.AreEqual("@imagegen ", input.Text);
                    Assert.IsFalse(Find<Viewbox>(panel, "StopIcon").IsVisible);
                }
                window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
                Assert.AreEqual("", input.Text);
                input.Text = "@imagee";
                input.CaretOffset = input.Text.Length;
                await PumpAsync();
                Assert.HasCount(1, Find<StackPanel>(panel, "FileList").Children);
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Assert.AreEqual("@imageedit ", input.Text);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ComposerTriggers_SeparateImageToolsFromRemoteFiles()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            SessionInfo session = context.FakeSessions.AddConnected();
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/hosts", "127.0.0.1"u8.ToArray());
            ChatPanelView panel = await ImageMentionPanel(context);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.TextArea.Focus();
                Type(input, "@i");
                await PumpAsync();
                Assert.HasCount(2, Find<StackPanel>(panel, "FileList").Children);
                Assert.AreEqual(0, context.FakeRemoteFs.ListDirectoryCalls);
                Type(input, "#/etc/ho");
                await PumpAsync();
                Assert.HasCount(1, Find<StackPanel>(panel, "FileList").Children);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Assert.AreEqual("#/etc/hosts ", input.Text);
                int listings = context.FakeRemoteFs.ListDirectoryCalls;
                Type(input, "@/etc/hosts");
                await PumpAsync();
                Assert.IsFalse(Find<Popup>(panel, "FilePopup").IsOpen);
                Assert.AreEqual(listings, context.FakeRemoteFs.ListDirectoryCalls);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ImageMention_ExpandsAsToolIntentNotRemoteFileAndRespectsPlanMode(bool automatic, bool missingDefault)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            ChatPanelView panel = await ImageMentionPanel(context, automatic: automatic);
            var window = new Window { Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                if (missingDefault) PanelSettings(panel).ImageModelId = "removed-default";
                MethodInfo resolve = typeof(ChatPanelView).GetMethod("ResolveAttachmentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var task = (Task<(string Text, IReadOnlyList<string> Attached, IReadOnlyList<string> Failed)>)resolve.Invoke(panel,
                    ["@imagegen draw a cat", CancellationToken.None])!;
                (string Text, IReadOnlyList<string> Attached, IReadOnlyList<string> Failed) = await task;
                Assert.Contains("Use generate_image", Text);
                Assert.Contains("draw a cat", Text);
                Assert.IsEmpty(Attached);
                Assert.IsEmpty(Failed);
                PanelSettings(panel).Mode = ChatMode.Plan;
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await (Task)resolve.Invoke(panel,
                    ["@imageedit change background", CancellationToken.None])!);
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "@";
                input.CaretOffset = 1;
                await PumpAsync();
                Assert.IsFalse(Find<Popup>(panel, "FilePopup").IsOpen);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
