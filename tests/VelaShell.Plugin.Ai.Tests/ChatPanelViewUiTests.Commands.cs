using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void SlashCommands_ClearAndResumeRunLocallyAndKeepSavedHistory()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var history = new ChatHistoryStore(context);
            await history.InitAsync();
            string id = ChatHistoryStore.NewConversationId();
            DateTimeOffset createdAt = DateTimeOffset.UtcNow;
            await history.AppendAsync(id, createdAt, 0, "user", "saved question");
            await history.AppendAsync(id, createdAt, 1, "assistant", "saved answer");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.TextArea.Focus();
                Type(input, "/cl");
                await PumpAsync(5);
                Assert.IsTrue(Find<Popup>(panel, "FilePopup").IsOpen);
                Assert.HasCount(1, Find<StackPanel>(panel, "FileList").Children);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Assert.AreEqual("/clear ", input.Text);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await PumpAsync(5);
                Assert.IsEmpty(Find<StackPanel>(panel, "MessagesPanel").Children);
                Assert.HasCount(1, await history.ListSessionsAsync());
                Type(input, "/resume");
                await PumpAsync(3);
                // A completed command still uses Enter once to accept and again to execute.
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await PumpAsync(10);
                Assert.IsTrue(Find<DockPanel>(panel, "HistoryHost").IsVisible);
                MethodInfo send = typeof(ChatPanelView).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)send.Invoke(panel, ["/resume " + id, true, null, null])!;
                Assert.HasCount(2, Find<StackPanel>(panel, "MessagesPanel").Children);
                Assert.IsTrue(Find<ScrollViewer>(panel, "ChatScroll").IsVisible);
                await (Task)send.Invoke(panel, ["/help", true, null, null])!;
                Assert.Contains("/clear", Find<TextBlock>(panel, "StatusText").Text!);
                await (Task)send.Invoke(panel, ["/not-a-command", true, null, null])!;
                Assert.Contains(new Loc(context.Host.Locale)["CommandUnknown"], Find<TextBlock>(panel, "StatusText").Text!);
                Assert.HasCount(2, Find<StackPanel>(panel, "MessagesPanel").Children, "commands must not add model turns");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
