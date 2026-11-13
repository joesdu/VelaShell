using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    private static string ImageToolCall(string name, string arguments) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id = "image-call",
            @object = "chat.completion.chunk",
            created = 1,
            model = "chat-model",
            choices = new[] { new { index = 0, delta = new { tool_calls = new[] {
                new { index = 0, id = "image-call-1", type = "function", function = new { name, arguments } }
            } }, finish_reason = "tool_calls" } }
        }) + "\n\ndata: [DONE]\n\n";

    [TestMethod]
    [DataRow(ChatMode.Chat, true)]
    [DataRow(ChatMode.Agent, true)]
    [DataRow(ChatMode.Plan, true)]
    [DataRow(ChatMode.Chat, false)]
    public void ContextImageTool_AutomaticSelectionNeedsNoDefault(ChatMode mode, bool enabled)
    {
        OnUi(async () =>
        {
            bool available = enabled && mode != ChatMode.Plan;
            using var images = new KeyAuthStub((_, _) => (200,
                "{\"created\":1,\"data\":[{\"b64_json\":\"" + GeneratedPng + "\"}]}"));
            var image = new AiModelConfig { Model = "gpt-image-selected", BaseUrlOverride = images.BaseUrl + "/v1" };
            using var chat = new KeyAuthStub((_, body) => (200,
                !available || body.Contains("\"role\":\"tool\"", StringComparison.Ordinal) ? LifecycleAnswer
                    : ImageToolCall("generate_image", JsonSerializer.Serialize(new { prompt = "雨后咖啡", modelId = image.Id }))));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("current", chat.BaseUrl, "chat-model");
            provider.Models.Add(image);
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                ImageGenerationEnabled = enabled, Mode = mode,
                Approval = ApprovalMode.Bypass, SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("生成海报");
                Assert.IsTrue(await WaitForAsync(() => chat.Requests.Count > 0 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.AreEqual(available ? 1 : 0, images.Requests.Count);
                Assert.AreEqual(available, chat.Requests[0].Body.Contains("generate_image", StringComparison.Ordinal));
                Assert.IsNull(PanelSettings(panel).ImageModelId);
                Assert.AreEqual(provider.Models[0].Id, PanelSettings(panel).ActiveModelId);
                if (available)
                {
                    using var request = JsonDocument.Parse(images.Requests[0].Body);
                    Assert.AreEqual(image.Model, request.RootElement.GetProperty("model").GetString());
                    Assert.Contains(image.Id, chat.Requests[0].Body);
                }
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(ChatMode.Chat)]
    [DataRow(ChatMode.Agent)]
    public void ContextImageTool_KeepsChatModelAndDisplaysSavedImage(ChatMode mode)
    {
        OnUi(async () =>
        {
            using var images = new KeyAuthStub((_, _) => (200,
                "{\"created\":1,\"data\":[{\"b64_json\":\"" + GeneratedPng + "\"}]}"));
            using var chat = new KeyAuthStub((_, body) => (200, body.Contains("\"role\":\"tool\"", StringComparison.Ordinal)
                ? LifecycleAnswer : ImageToolCall("generate_image", "{\"prompt\":\"A blue cat with the discussed striped scarf\"}")));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider text = StubProvider("chat", chat.BaseUrl + "/v1", "chat-model");
            AiProvider image = StubProvider("images", images.BaseUrl + "/v1", "gpt-image-2");
            await store.SaveAsync(new AiSettings
            {
                Providers = [text, image],
                ActiveModelId = text.Models[0].Id,
                ImageModelId = image.Models[0].Id,
                Mode = mode,
                Approval = ApprovalMode.Bypass,
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("按我们刚才讨论的画一只猫");
                Assert.IsTrue(await WaitForAsync(() => chat.Requests.Count > 0 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.HasCount(1, images.Requests);
                Assert.HasCount(2, chat.Requests);
                Assert.AreEqual(text.Models[0].Id, PanelSettings(panel).ActiveModelId);
                Assert.HasCount(1, Find<StackPanel>(panel, "MessagesPanel").GetVisualDescendants().OfType<Image>().Where(i => i.Source is not null).ToList());
                Assert.Contains("generate_image", chat.Requests[0].Body);
                Assert.Contains("image_generation_result", chat.Requests[1].Body);
                if (mode == ChatMode.Chat) Assert.DoesNotContain("run_command", chat.Requests[0].Body);
                using var request = JsonDocument.Parse(images.Requests[0].Body);
                Assert.AreEqual("A blue cat with the discussed striped scarf", request.RootElement.GetProperty("prompt").GetString());
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ContextImageTool_EditsPreviousImageAfterHistoryReload()
    {
        OnUi(async () =>
        {
            string? imageId = null;
            using var images = new KeyAuthStub((_, _) => (200,
                "{\"created\":1,\"data\":[{\"b64_json\":\"" + GeneratedPng + "\"}]}"));
            using var chat = new KeyAuthStub((_, body) => (200,
                body.Contains("\"role\":\"tool\"", StringComparison.Ordinal) ? LifecycleAnswer
                    : imageId is null ? ImageToolCall("generate_image", "{\"prompt\":\"blue cat\"}")
                    : ImageToolCall("edit_image", JsonSerializer.Serialize(new { prompt = "Keep the cat unchanged; make the background white", imageIds = new[] { imageId } }))));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider text = StubProvider("chat", chat.BaseUrl, "chat-model");
            AiProvider image = StubProvider("images", images.BaseUrl + "/v1", "gpt-image-2");
            await store.SaveAsync(new AiSettings
            {
                Providers = [text, image],
                ActiveModelId = text.Models[0].Id,
                ImageModelId = image.Models[0].Id,
                Mode = ChatMode.Chat,
                Approval = ApprovalMode.Bypass,
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("画一只猫");
                Assert.IsTrue(await WaitForAsync(() => images.Requests.Count == 1 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                var history = new ChatHistoryStore(context);
                await history.InitAsync();
                ChatSessionSummary saved = (await history.ListSessionsAsync()).Single();
                imageId = (await history.LoadAsync(saved.Id)).Single(e => e.Role == "assistant").Meta!.Images!.Single().FileName;
                await (Task)typeof(ChatPanelView).GetMethod("LoadConversationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [saved])!;
                panel.SendExternal("把刚才那张背景改白，猫保持不变");
                Assert.IsTrue(await WaitForAsync(() => images.Requests.Count == 2 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.Contains("name=\"image[]\"", images.Requests[1].Body);
                Assert.Contains("Keep the cat unchanged", images.Requests[1].Body);
                Assert.HasCount(2, Find<StackPanel>(panel, "MessagesPanel").GetVisualDescendants().OfType<Image>().Where(i => i.Source is not null).ToList());
                Assert.AreEqual(text.Models[0].Id, PanelSettings(panel).ActiveModelId);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ContextImageTool_FailureDoesNotRegenerateOrSwitchModel()
    {
        OnUi(async () =>
        {
            using var images = new KeyAuthStub((_, _) => (503, "{\"error\":{\"message\":\"unavailable\"}}"));
            using var chat = new KeyAuthStub((_, _) => (200, ImageToolCall("generate_image", "{\"prompt\":\"blue cat\"}")));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider text = StubProvider("chat", chat.BaseUrl, "chat-model");
            AiProvider image = StubProvider("images", images.BaseUrl + "/v1", "gpt-image-2");
            await store.SaveAsync(new AiSettings
            {
                Providers = [text, image],
                ActiveModelId = text.Models[0].Id,
                ImageModelId = image.Models[0].Id,
                Mode = ChatMode.Chat,
                Approval = ApprovalMode.Bypass,
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("画一只猫");
                Assert.IsTrue(await WaitForAsync(() => images.Requests.Count > 0 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.HasCount(1, images.Requests);
                Assert.HasCount(1, chat.Requests);
                Assert.AreEqual(text.Models[0].Id, PanelSettings(panel).ActiveModelId);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ContextImageTool_DenialStopsBeforeSendingImageRequest()
    {
        OnUi(async () =>
        {
            using var images = new KeyAuthStub((_, _) => (200, "{}"));
            using var chat = new KeyAuthStub((_, _) => (200, ImageToolCall("generate_image", "{\"prompt\":\"blue cat\"}")));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider text = StubProvider("chat", chat.BaseUrl, "chat-model");
            AiProvider image = StubProvider("images", images.BaseUrl + "/v1", "gpt-image-2");
            await store.SaveAsync(new AiSettings
            {
                Providers = [text, image],
                ActiveModelId = text.Models[0].Id,
                ImageModelId = image.Models[0].Id,
                Mode = ChatMode.Chat,
                Approval = ApprovalMode.ReadOnlyAuto,
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("画一只猫");
                var loc = new Loc(context.Host.Locale);
                Assert.IsTrue(await WaitForAsync(() => Find<StackPanel>(panel, "MessagesPanel").GetVisualDescendants()
                    .OfType<Button>().Any(button => Equals(button.Content, loc["Deny"]))));
                Button deny = Find<StackPanel>(panel, "MessagesPanel").GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, loc["Deny"]));
                RaiseClick(deny);
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.IsEmpty(images.Requests);
                Assert.HasCount(1, chat.Requests);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ContextImageTool_PlanModeDoesNotOfferImageTools()
    {
        OnUi(async () =>
        {
            using var chat = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider text = StubProvider("chat", chat.BaseUrl, "chat-model");
            AiProvider image = StubProvider("images", "http://127.0.0.1:1/v1", "gpt-image-2");
            await store.SaveAsync(new AiSettings
            {
                Providers = [text, image],
                ActiveModelId = text.Models[0].Id,
                ImageModelId = image.Models[0].Id,
                Mode = ChatMode.Plan,
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("计划一下绘图步骤");
                Assert.IsTrue(await WaitForAsync(() => chat.Requests.Count > 0 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.DoesNotContain("generate_image", chat.Requests[0].Body);
                Assert.DoesNotContain("edit_image", chat.Requests[0].Body);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
