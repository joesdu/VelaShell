using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>Conversation-owned image references and per-turn image tools; never accepts model-provided file paths.</summary>
internal sealed class ConversationImages(GeneratedImageStore images)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ChatGeneratedImage> _known = [with(StringComparer.Ordinal)];

    internal void Clear() { lock (_gate) _known.Clear(); }
    internal void Register(ChatGeneratedImage image) { lock (_gate) _known[image.FileName] = image; }
    /// <summary>这段对话登记过的图片文件名 —— 回收历史图片时,还活在界面上的对话要用的这些得留着。</summary>
    internal IReadOnlyList<string> Files
    {
        get { lock (_gate) return [.. _known.Keys]; }
    }
    internal string Describe()
    {
        lock (_gate) return string.Join("\n", _known.Keys.Select(id => $"Image reference: {id}"));
    }
    internal DataContent Load(string id)
    {
        ChatGeneratedImage reference;
        lock (_gate)
        {
            reference = _known.GetValueOrDefault(id)
            ?? throw new ArgumentException("This image was not provided in the current conversation.");
        }

        return images.Load(reference);
    }

    internal ImageToolTurn BeginTurn(AiSettingsStore store, string? defaultModelId,
        Func<string, CancellationToken, Task<bool>> approve, CancellationToken lifetime)
        => new(this, images, store, defaultModelId, approve, lifetime);
}

/// <summary>A single turn owns completed image receipts, so only trusted tool results can display files.</summary>
internal sealed class ImageToolTurn(ConversationImages conversation, GeneratedImageStore images,
    AiSettingsStore store, string? defaultModelId,
    Func<string, CancellationToken, Task<bool>> approve, CancellationToken lifetime)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ChatGeneratedImage> _results = [with(StringComparer.Ordinal)];
    private int _count;
    private bool _failed;

    private sealed record Target(string ProviderId, string Model, ChatProtocol Protocol, string BaseUrl, string KeyOwnerId)
    {
        internal static Target From(ResolvedModel model)
            => new(model.Provider.Id, model.Model, model.Protocol, model.BaseUrl, model.ApiKeyOwnerId);
    }

    internal IList<AITool> CreateTools(AiSettings settings, string providerId)
    {
        ResolvedModel[] models = [.. settings.ResolveModels().Where(model =>
            (model.Provider.Id == providerId || model.Id == defaultModelId) && AiSettingsStore.IsImageModel(model))];
        var targets = models.ToDictionary(model => model.Id, Target.From, StringComparer.Ordinal);
        string selection = " Optional modelId is a configured ID from this list: " +
            JsonSerializer.Serialize(models.Select(model => new { modelId = model.Id, name = model.Name, provider = model.ProviderName, model = model.Model })) +
            ". Honor the user's explicit model request. Otherwise use the default when configured; " +
            "without a default, select a listed modelId. Do not invent capabilities, quality rankings or prices from names. " +
            "For a requested comparison, call once per selected model with exactly the same prompt. " +
            "The receipt identifies the requested model, not an independently verified upstream model. Default modelId: " +
            JsonSerializer.Serialize(defaultModelId) + ".";

        Task<string> GenerateAsync([Description("Complete standalone drawing specification.")] string prompt,
            [Description("Configured image model ID from the allowed list; omit to use the optional default.")] string? modelId = null,
            CancellationToken cancellationToken = default)
            => ExecuteAsync(prompt, [], modelId, targets, cancellationToken);

        Task<string> EditAsync([Description("Complete edit instructions, including preservation constraints.")] string prompt,
            [Description("One to three exact image IDs previously attached or generated in this conversation.")] string[] imageIds,
            [Description("Configured image model ID from the allowed list; omit to use the optional default.")] string? modelId = null,
            CancellationToken cancellationToken = default)
        {
            if (imageIds is null || imageIds.Length is < 1 or > 3)
                throw new ArgumentException("Provide one to three conversation image IDs.");
            return ExecuteAsync(prompt, imageIds, modelId, targets, cancellationToken);
        }

        return
        [
            AIFunctionFactory.Create(GenerateAsync, "generate_image",
            "Create one new raster image. Write a complete visual specification using the relevant conversation requirements, not references like 'as above'. " +
            "The configured image provider receives this prompt and bills separately. Do not use this for explaining or inspecting images. " +
            "Use edit_image when changing an existing image. Never retry a failed generation automatically." + selection),
            AIFunctionFactory.Create(EditAsync, "edit_image",
            "Edit or combine one to three images supplied in this conversation. Use exact imageIds from the conversation, never file paths. " +
            "Specify the numbered source roles, requested changes and everything that must stay unchanged. " +
            "This sends source images to the configured image provider and bills separately. Never retry a failed edit automatically." + selection)
        ];
    }

    private async Task<string> ExecuteAsync(string prompt, string[] imageIds, string? requestedModelId,
        IReadOnlyDictionary<string, Target> targets, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        token = linked.Token;
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32000)
            throw new ArgumentException("A non-empty image prompt of at most 32000 characters is required.");
        lock (_gate)
        {
            if (_failed) throw new InvalidOperationException("An image operation already failed. Wait for a new user request before generating again.");
            if (_count >= GeneratedImageStore.MaxImagesPerTurn) throw new InvalidOperationException("Image limit reached for this turn.");
            _count++;
        }
        try
        {
            DataContent[] inputs = [.. imageIds.Select(conversation.Load)];
            string modelId = string.IsNullOrWhiteSpace(requestedModelId) ? defaultModelId
                ?? throw new InvalidOperationException("No default image model is configured. Select a modelId from the allowed list.")
                : requestedModelId.Trim();
            if (!targets.TryGetValue(modelId, out Target? target))
                throw new InvalidOperationException("Select an image model from the current provider or the explicitly configured default.");
            AiSettings settings = await store.LoadAsync(token).ConfigureAwait(false);
            ResolvedModel model = settings.ImageToolsEnabled && settings.ImageModelId == defaultModelId ? settings.FindModel(modelId)
                ?? throw new InvalidOperationException("The configured image model was removed.")
                : throw new InvalidOperationException("Image generation settings changed; send a new request.");
            if (!AiSettingsStore.IsImageModel(model)) throw new InvalidOperationException("Select an image model in global settings.");
            if (Target.From(model) != target)
                throw new InvalidOperationException("The selected image model changed; send a new request.");
            string destination = $"{model.Provider.Name} / {model.Model}\n{model.BaseUrl}\n" +
                $"Reference images: {imageIds.Length}\n{prompt}";
            if (!await approve(destination, token).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Image generation was denied. Do not retry.");
            token.ThrowIfCancellationRequested();
            AiSettings verified = await store.LoadAsync(token).ConfigureAwait(false);
            ResolvedModel? latest = verified.FindModel(modelId);
            if (!verified.ImageToolsEnabled || verified.ImageModelId != defaultModelId || latest is null
                || Target.From(latest) != target)
            {
                throw new InvalidOperationException("Image generation settings changed during approval. Send a new request.");
            }

            DataContent output = await store.GenerateImageAsync(latest, prompt, inputs, token).ConfigureAwait(false);
            ChatGeneratedImage reference = images.Save(output);
            conversation.Register(reference);
            lock (_gate) _results[reference.FileName] = reference;
            return JsonSerializer.Serialize(new
            {
                type = "image_generation_result",
                operation = inputs.Length == 0 ? "generate" : "edit",
                imageId = reference.FileName,
                mediaType = reference.MediaType,
                model = target.Model,
                modelId,
                providerId = target.ProviderId
            });
        }
        catch
        {
            lock (_gate) _failed = true;
            throw;
        }
    }

    internal ChatGeneratedImage? ResolveResult(string result)
    {
        try
        {
            using var json = JsonDocument.Parse(result);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "image_generation_result"
                && json.RootElement.TryGetProperty("imageId", out JsonElement id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is { } key)
            {
                lock (_gate) return _results.GetValueOrDefault(key);
            }
        }
        catch (JsonException) { }
        return null;
    }
}
