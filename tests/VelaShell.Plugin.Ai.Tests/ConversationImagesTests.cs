using Microsoft.Extensions.AI;
using System.Text.Json;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;
using KeyAuthStub = VelaShell.Plugin.Ai.Tests.ChatPanelViewUiTests.KeyAuthStub;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class ConversationImagesTests
{
    [TestMethod]
    public async Task ToolCannotEditImageFromAnotherConversationOrReadArbitraryPath()
    {
        using var context = new TestPluginContext();
        var store = new GeneratedImageStore(context.DataDirectory);
        var first = new ConversationImages(store);
        var other = new ConversationImages(store);
        ChatGeneratedImage reference = store.Save(new DataContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="), "image/png"));
        first.Register(reference);
        Assert.IsFalse(first.Load(reference.FileName).Data.IsEmpty);
        Assert.ThrowsExactly<ArgumentException>(() => other.Load(reference.FileName));
        Assert.ThrowsExactly<ArgumentException>(() => first.Load("../../secret.png"));
        ImageToolTurn turn = other.BeginTurn(new AiSettingsStore(context), "missing", (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.IsNull(turn.ResolveResult("[]"));
        Assert.IsNull(turn.ResolveResult("{\"type\":7}"));
        Assert.IsNull(turn.ResolveResult("{\"type\":\"image_generation_result\",\"imageId\":42}"));
        AIFunction edit = turn.CreateTools(new AiSettings(), "provider").OfType<AIFunction>().Single(t => t.Name == "edit_image");
        await Assert.ThrowsAsync<ArgumentException>(async () => await edit.InvokeAsync(new AIFunctionArguments
        { ["prompt"] = "edit", ["imageIds"] = new[] { reference.FileName } }));
    }

    [TestMethod]
    public async Task DefaultImageModelSurvivesGlobalSaveAndReload()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiSettings settings = await store.LoadAsync();
        await store.SaveGlobalSettingsAsync(settings, new AiSettings { ImageModelId = "explicit-missing-id" });
        Assert.AreEqual("explicit-missing-id", settings.ImageModelId);
        Assert.AreEqual("explicit-missing-id", (await store.LoadAsync()).ImageModelId);
        settings.ImageModelId = null;
        await store.ReloadIntoAsync(settings);
        Assert.AreEqual("explicit-missing-id", settings.ImageModelId);
        Assert.IsTrue(settings.ImageToolsEnabled, "Old configurations with a default remain enabled.");
        await store.SaveGlobalSettingsAsync(settings, new AiSettings { ImageGenerationEnabled = true });
        Assert.IsNull(settings.ImageModelId);
        Assert.IsTrue(settings.ImageToolsEnabled);
        settings.ImageGenerationEnabled = false;
        await store.ReloadIntoAsync(settings);
        Assert.IsTrue(settings.ImageToolsEnabled);
        await store.SaveGlobalSettingsAsync(settings, new AiSettings { ImageGenerationEnabled = false, ImageModelId = "saved-default" });
        AiSettings disabled = await store.LoadAsync();
        Assert.IsFalse(disabled.ImageToolsEnabled);
        Assert.AreEqual("saved-default", disabled.ImageModelId);
    }

    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ToolsSelectDifferentModelsWithSamePromptAndKeepSettings(bool hasDefault)
    {
        using var endpoint = new KeyAuthStub((_, _) => (200, "{\"created\":1,\"data\":[{\"b64_json\":\"" + Png + "\"}]}"));
        using var context = new TestPluginContext();
        var first = new AiModelConfig { Model = "gpt-image-first" };
        var second = new AiModelConfig { Model = "gpt-image-second" };
        var text = new AiModelConfig { Model = "chat-model" };
        var provider = new AiProvider { BaseUrl = endpoint.BaseUrl + "/v1", Models = [text, first, second] };
        var settings = new AiSettings
        {
            Providers = [provider], ActiveModelId = text.Id,
            ImageGenerationEnabled = true, ImageModelId = hasDefault ? first.Id : null
        };
        var store = new AiSettingsStore(context);
        await store.SaveAsync(settings);
        var conversation = new ConversationImages(new GeneratedImageStore(context.DataDirectory));
        List<string> approvals = [];
        ImageToolTurn turn = conversation.BeginTurn(store, settings.ImageModelId,
            (detail, _) => { approvals.Add(detail); return Task.FromResult(true); }, CancellationToken.None);
        IList<AITool> tools = turn.CreateTools(settings, provider.Id);
        AIFunction generate = tools.OfType<AIFunction>().Single(t => t.Name == "generate_image");
        const string prompt = "雨后咖啡：让今天慢一点。3:2 横版海报。";
        string? reference = null;
        foreach (AiModelConfig model in new[] { first, second })
        {
            object? result = await generate.InvokeAsync(new AIFunctionArguments { ["prompt"] = prompt, ["modelId"] = model.Id });
            string receipt = result!.ToString()!;
            using var json = JsonDocument.Parse(receipt);
            Assert.AreEqual(model.Id, json.RootElement.GetProperty("modelId").GetString());
            Assert.AreEqual(provider.Id, json.RootElement.GetProperty("providerId").GetString());
            Assert.IsNotNull(turn.ResolveResult(receipt));
            reference = json.RootElement.GetProperty("imageId").GetString();
        }
        Assert.HasCount(2, endpoint.Requests);
        for (int index = 0; index < 2; index++)
        {
            using var request = JsonDocument.Parse(endpoint.Requests[index].Body);
            Assert.AreEqual(prompt, request.RootElement.GetProperty("prompt").GetString());
            Assert.AreEqual(index == 0 ? first.Model : second.Model, request.RootElement.GetProperty("model").GetString());
        }
        AIFunction edit = tools.OfType<AIFunction>().Single(t => t.Name == "edit_image");
        await edit.InvokeAsync(new AIFunctionArguments { ["prompt"] = "Keep the text; change the background.", ["imageIds"] = new[] { reference }, ["modelId"] = second.Id });
        Assert.Contains(second.Model, endpoint.Requests[2].Body);
        Assert.Contains("name=\"image[]\"", endpoint.Requests[2].Body);
        Assert.HasCount(3, approvals);
        Assert.Contains(first.Model, approvals[0]);
        Assert.Contains(second.Model, approvals[1]);
        AiSettings saved = await store.LoadAsync();
        Assert.AreEqual(text.Id, saved.ActiveModelId);
        Assert.AreEqual(hasDefault ? first.Id : null, saved.ImageModelId);
    }

    [TestMethod]
    public async Task RebuildingCandidatesKeepsTheTurnImageLimit()
    {
        using var endpoint = new KeyAuthStub((_, _) => (200, "{\"created\":1,\"data\":[{\"b64_json\":\"" + Png + "\"}]}"));
        using var context = new TestPluginContext();
        var first = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [new() { Model = "gpt-image-first" }] };
        var second = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [new() { Model = "gpt-image-second" }] };
        var settings = new AiSettings { Providers = [first, second], ImageGenerationEnabled = true };
        var store = new AiSettingsStore(context);
        await store.SaveAsync(settings);
        var conversation = new ConversationImages(new GeneratedImageStore(context.DataDirectory));
        ImageToolTurn turn = conversation.BeginTurn(store, null, (_, _) => Task.FromResult(true), CancellationToken.None);
        for (int index = 0; index < GeneratedImageStore.MaxImagesPerTurn; index++)
        {
            AiProvider provider = index % 2 == 0 ? first : second;
            AIFunction tool = turn.CreateTools(settings, provider.Id).OfType<AIFunction>().First();
            await tool.InvokeAsync(new AIFunctionArguments { ["prompt"] = "draw", ["modelId"] = provider.Models[0].Id });
        }
        AIFunction next = turn.CreateTools(settings, second.Id).OfType<AIFunction>().First();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await next.InvokeAsync(
            new AIFunctionArguments { ["prompt"] = "draw again", ["modelId"] = second.Models[0].Id }));
        Assert.HasCount(GeneratedImageStore.MaxImagesPerTurn, endpoint.Requests);
    }

    [TestMethod]
    [DataRow("foreign")]
    [DataRow("text")]
    [DataRow("missing")]
    [DataRow("unspecified")]
    [DataRow("disabled")]
    public async Task InvalidImageSelectionFailsBeforeApprovalOrRequest(string scenario)
    {
        using var endpoint = new KeyAuthStub((_, _) => (200, "{}"));
        using var context = new TestPluginContext();
        var image = new AiModelConfig { Model = "gpt-image-first" };
        var text = new AiModelConfig { Model = "text" };
        var foreign = new AiModelConfig { Model = "gpt-image-foreign" };
        var provider = new AiProvider { Name = "same name", BaseUrl = endpoint.BaseUrl, Models = [image, text] };
        var other = new AiProvider { Name = "same name", BaseUrl = endpoint.BaseUrl, Models = [foreign] };
        var settings = new AiSettings { Providers = [provider, other], ImageGenerationEnabled = scenario != "disabled" };
        var store = new AiSettingsStore(context);
        await store.SaveAsync(settings);
        int approvals = 0;
        var conversation = new ConversationImages(new GeneratedImageStore(context.DataDirectory));
        ImageToolTurn turn = conversation.BeginTurn(store, null,
            (_, _) => { approvals++; return Task.FromResult(true); }, CancellationToken.None);
        AIFunction tool = turn.CreateTools(settings, provider.Id).OfType<AIFunction>().First();
        Assert.DoesNotContain(foreign.Id, tool.Description!);
        string? selected = scenario switch { "foreign" => foreign.Id, "text" => text.Id, "missing" => "missing", "unspecified" => null, _ => image.Id };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tool.InvokeAsync(
            new AIFunctionArguments { ["prompt"] = "draw", ["modelId"] = selected }));
        Assert.AreEqual(0, approvals);
        Assert.IsEmpty(endpoint.Requests);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApprovalChangesInvalidateSelectedModel(bool disable)
    {
        using var endpoint = new KeyAuthStub((_, _) => (200, "{}"));
        using var context = new TestPluginContext();
        var image = new AiModelConfig { Model = "gpt-image-first" };
        var provider = new AiProvider { BaseUrl = endpoint.BaseUrl, Models = [image] };
        var settings = new AiSettings { Providers = [provider], ImageGenerationEnabled = true };
        var store = new AiSettingsStore(context);
        await store.SaveAsync(settings);
        var conversation = new ConversationImages(new GeneratedImageStore(context.DataDirectory));
        ImageToolTurn turn = conversation.BeginTurn(store, null, async (_, _) =>
        {
            if (disable) settings.ImageGenerationEnabled = false;
            else image.Model = "gpt-image-changed";
            await store.SaveAsync(settings);
            return true;
        }, CancellationToken.None);
        AIFunction tool = turn.CreateTools(settings, provider.Id).OfType<AIFunction>().First();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tool.InvokeAsync(
            new AIFunctionArguments { ["prompt"] = "draw", ["modelId"] = image.Id }));
        Assert.IsEmpty(endpoint.Requests);
    }
}
