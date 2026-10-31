using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    private const string GeneratedPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";

    [TestMethod]
    [DataRow(ChatMode.Chat)]
    [DataRow(ChatMode.Agent)]
    public void ImageGeneration_DisplaysAndPersistsWithoutEmptyReplyRetry(ChatMode mode)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (200,
                "{\"created\":1,\"data\":[{\"b64_json\":\"" + GeneratedPng + "\"}]}"));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("images", endpoint.BaseUrl + "/v1", "gpt-image-2");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                Mode = mode,
                SuggestFollowUps = true
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("draw a cat");
                Assert.IsTrue(await WaitForAsync(() => endpoint.Requests.Count > 0
                    && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.HasCount(1, messages.GetVisualDescendants().OfType<Image>().Where(i => i.Source is not null).ToList());
                Assert.HasCount(1, endpoint.Requests, "纯图片不应判空、生成摘要或后续提问");
                var history = new ChatHistoryStore(context);
                await history.InitAsync();
                ChatSessionSummary saved = (await history.ListSessionsAsync()).Single();
                ChatEntry answer = (await history.LoadAsync(saved.Id)).Single(entry => entry.Role == "assistant");
                Assert.HasCount(1, answer.Meta!.Images!);
                Assert.IsFalse(string.IsNullOrWhiteSpace(answer.Text));
                MethodInfo load = typeof(ChatPanelView).GetMethod("LoadConversationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)load.Invoke(panel, [saved])!;
                Assert.HasCount(1, messages.GetVisualDescendants().OfType<Image>().Where(i => i.Source is not null).ToList(),
                    "历史重新加载后仍应显示图片");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void GifAttachment_StaysVisualInputButIsNeverOfferedForEdits()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                byte[] gif = "GIF89a"u8.ToArray();
                var message = new ChatMessage(ChatRole.User,
                [
                    new TextContent("edit both"),
                    new DataContent(Convert.FromBase64String(GeneratedPng), "image/png"),
                    new DataContent(gif, "image/gif")
                ]);
                var registered = (List<ChatGeneratedImage>)typeof(ChatPanelView)
                    .GetMethod("RegisterInputImages", flags)!.Invoke(panel, [message])!;
                Assert.HasCount(1, registered, "/images/edits 收不下 GIF,不该给它图片 ID");
                Assert.AreEqual("image/png", registered[0].MediaType);
                Assert.HasCount(2, message.Contents.OfType<DataContent>().ToList(), "GIF 仍作为视觉输入发给聊天模型");
                string note = message.Contents.OfType<TextContent>().Last().Text;
                Assert.Contains(registered[0].FileName, note);
                Assert.DoesNotContain(".gif", note);
                Assert.IsEmpty(Directory.GetFiles(Path.Combine(context.DataDirectory, "generated-images"), "*.gif"));

                // 旧版把 GIF 附件也登记成了可改的图:恢复这类会话时不再把它交给模型
                var images = new GeneratedImageStore(context.DataDirectory);
                ChatGeneratedImage legacyGif = images.Save(new DataContent(gif, "image/gif"));
                var history = new ChatHistoryStore(context);
                await history.InitAsync();
                string id = ChatHistoryStore.NewConversationId();
                await history.AppendAsync(id, DateTimeOffset.UtcNow, 0, "user", "old gif");
                await history.AppendMetaAsync(id, 0, new ChatTurnMeta(Images: [legacyGif, registered[0]]));
                ChatSessionSummary saved = (await history.ListSessionsAsync()).Single(s => s.Id == id);
                await (Task)typeof(ChatPanelView).GetMethod("LoadConversationAsync", flags)!.Invoke(panel, [saved])!;
                var restored = (List<ChatMessage>)typeof(ChatPanelView).GetProperty("History", flags)!.GetValue(panel)!;
                string userText = restored.Single(m => m.Role == ChatRole.User).Text;
                Assert.Contains(registered[0].FileName, userText);
                Assert.DoesNotContain(legacyGif.FileName, userText);
                object conversation = typeof(ChatPanelView).GetProperty("Cur", flags)!.GetValue(panel)!;
                var known = (ConversationImages)conversation.GetType().GetField("Images")!.GetValue(conversation)!;
                Assert.DoesNotContain(legacyGif.FileName, known.Describe());
                Assert.ThrowsExactly<ArgumentException>(() => known.Load(legacyGif.FileName));
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void DeletingAndClearingHistory_RemoveTheirImageFiles()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var images = new GeneratedImageStore(context.DataDirectory);
            ChatGeneratedImage first = images.Save(new DataContent(Convert.FromBase64String(GeneratedPng), "image/png"));
            ChatGeneratedImage second = images.Save(new DataContent(Convert.FromBase64String(GeneratedPng), "image/png"));
            ChatGeneratedImage orphan = images.Save(new DataContent(Convert.FromBase64String(GeneratedPng), "image/png"));
            var history = new ChatHistoryStore(context);
            await history.InitAsync();
            string doomed = ChatHistoryStore.NewConversationId(), kept = ChatHistoryStore.NewConversationId();
            DateTimeOffset created = DateTimeOffset.UtcNow;
            foreach ((string id, ChatGeneratedImage image) in new[] { (doomed, first), (kept, second) })
            {
                await history.AppendAsync(id, created, 0, "user", "draw");
                await history.AppendAsync(id, created, 1, "assistant", "done");
                await history.AppendMetaAsync(id, 1, new ChatTurnMeta(Images: [image]));
            }
            string FileOf(ChatGeneratedImage image) => Path.Combine(context.DataDirectory, "generated-images", image.FileName);
            // 旧版删历史留下的孤儿:面板起来时在后台回收(只动一天前写下的)
            File.SetLastWriteTimeUtc(FileOf(orphan), DateTime.UtcNow.AddDays(-2));
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Assert.IsTrue(await WaitForAsync(() => !File.Exists(FileOf(orphan))), "启动回收应删掉没有会话引用的旧图");
                Assert.IsTrue(File.Exists(FileOf(first)) && File.Exists(FileOf(second)));
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                ChatSessionSummary session = (await history.ListSessionsAsync()).Single(s => s.Id == doomed);
                var row = (Border)typeof(ChatPanelView).GetMethod("BuildHistoryRow", flags)!.Invoke(panel, [session])!;
                var tools = (StackPanel)((Grid)row.Child!).Children[1];
                ((Button)tools.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(await WaitForAsync(() => !File.Exists(FileOf(first))), "删掉会话应回收只有它引用的图");
                Assert.IsTrue(File.Exists(FileOf(second)), "别的会话的图不动");

                MethodInfo clear = typeof(ChatPanelView).GetMethod("OnClearHistoryClickedAsync", flags)!;
                await (Task)clear.Invoke(panel, [])!; // 第一次只是"确认吗"
                Assert.IsTrue(File.Exists(FileOf(second)));
                await (Task)clear.Invoke(panel, [])!;
                Assert.IsFalse(File.Exists(FileOf(second)), "清空历史应回收不再被任何会话引用的图");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ImageGeneration_ServerFailureDoesNotRetryOrFailOver()
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (503, "{\"error\":{\"message\":\"unavailable\"}}"));
            using var backup = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider image = StubProvider("images", endpoint.BaseUrl + "/v1", "gpt-image-2");
            AiProvider text = StubProvider("backup", backup.BaseUrl, "m");
            await store.SaveAsync(new AiSettings
            {
                Providers = [image, text],
                ActiveModelId = image.Models[0].Id,
                Mode = ChatMode.Agent,
                FailoverChain = [new FailoverEntry { ModelId = text.Models[0].Id }]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("draw a cat");
                Assert.IsTrue(await WaitForAsync(() => endpoint.Requests.Count > 0
                    && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.HasCount(1, endpoint.Requests);
                Assert.IsEmpty(backup.Requests);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ClipboardPaste_AddsImageAttachmentsAndStillPastesText()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                IClipboard clipboard = window.Clipboard!;
                TextEditor editor = Find<TextEditor>(panel, "InputBox");
                using var source = new MemoryStream(Convert.FromBase64String(GeneratedPng));
                using var bitmap = new Bitmap(source);
                await clipboard.SetBitmapAsync(bitmap);
                editor.Text = "prompt";
                ApplicationCommands.Paste.Execute(null, editor.TextArea);
                Assert.IsTrue(await WaitForAsync(() => Find<WrapPanel>(panel, "AttachmentBar").Children.Count == 1));
                Assert.AreEqual("prompt", editor.Text);
                var contents = (List<AIContent>)typeof(ChatPanelView).GetMethod("BuildUserContents",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, ["prompt"])!;
                Assert.HasCount(1, contents.OfType<DataContent>().ToList());
                for (int i = 0; i < 4; i++)
                {
                    ApplicationCommands.Paste.Execute(null, editor.TextArea);
                    int expected = Math.Min(i + 2, 4);
                    Assert.IsTrue(await WaitForAsync(() => Find<WrapPanel>(panel, "AttachmentBar").Children.Count == expected));
                }
                Assert.HasCount(4, Find<WrapPanel>(panel, "AttachmentBar").Children);
                await clipboard.SetTextAsync("pasted text");
                editor.Text = "";
                ApplicationCommands.Paste.Execute(null, editor.TextArea);
                Assert.IsTrue(await WaitForAsync(() => editor.Text == "pasted text"));
            }
            finally { await window.Clipboard!.ClearAsync(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void NonEmptyImageCountsAsMeaningfulOutput()
    {
        Assert.IsTrue(ChatPanelView.HasMeaningfulOutput([
            new ChatResponseUpdate(ChatRole.Assistant, [new DataContent(Convert.FromBase64String(GeneratedPng), "image/png")])]));
        Assert.IsFalse(ChatPanelView.HasMeaningfulOutput([
            new ChatResponseUpdate(ChatRole.Assistant, [new DataContent(ReadOnlyMemory<byte>.Empty, "image/png")])]));
        Assert.IsFalse(ChatPanelView.HasMeaningfulOutput([
            new ChatResponseUpdate(ChatRole.Assistant, [new DataContent(new byte[] { 1 }, "application/octet-stream")])]));
    }
}
