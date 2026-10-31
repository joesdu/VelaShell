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

    internal ImageToolTurn BeginTurn(AiSettingsStore store, string modelId,
        Func<string, CancellationToken, Task<bool>> approve, CancellationToken lifetime)
        => new(this, images, store, modelId, approve, lifetime);
}

/// <summary>A single turn owns completed image receipts, so only trusted tool results can display files.</summary>
internal sealed class ImageToolTurn(ConversationImages conversation, GeneratedImageStore images,
    AiSettingsStore store, string modelId,
    Func<string, CancellationToken, Task<bool>> approve, CancellationToken lifetime)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ChatGeneratedImage> _results = [with(StringComparer.Ordinal)];
    private int _count;
    private bool _failed;

    internal IList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(GenerateAsync, "generate_image",
            "Create one new raster image. Write a complete visual specification using the relevant conversation requirements, not references like 'as above'. " +
            "The configured image provider receives this prompt and bills separately. Do not use this for explaining or inspecting images. " +
            "Use edit_image when changing an existing image. Never retry a failed generation automatically."),
        AIFunctionFactory.Create(EditAsync, "edit_image",
            "Edit or combine one to three images supplied in this conversation. Use exact imageIds from the conversation, never file paths. " +
            "Specify the numbered source roles, requested changes and everything that must stay unchanged. " +
            "This sends source images to the configured image provider and bills separately. Never retry a failed edit automatically.")
    ];

    private Task<string> GenerateAsync([Description("Complete standalone drawing specification.")] string prompt,
        CancellationToken cancellationToken) => ExecuteAsync(prompt, [], cancellationToken);

    private Task<string> EditAsync([Description("Complete edit instructions, including preservation constraints.")] string prompt,
        [Description("One to three exact image IDs previously attached or generated in this conversation.")] string[] imageIds,
        CancellationToken cancellationToken)
    {
        if (imageIds is null || imageIds.Length is < 1 or > 3)
            throw new ArgumentException("Provide one to three conversation image IDs.");
        return ExecuteAsync(prompt, imageIds, cancellationToken);
    }

    private async Task<string> ExecuteAsync(string prompt, string[] imageIds, CancellationToken token)
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
            AiSettings settings = await store.LoadAsync(token).ConfigureAwait(false);
            ResolvedModel model = settings.ImageModelId == modelId ? settings.FindModel(modelId)
                ?? throw new InvalidOperationException("The configured image model was removed.")
                : throw new InvalidOperationException("Image generation settings changed; send a new request.");
            if (!AiSettingsStore.IsImageModel(model)) throw new InvalidOperationException("Select an image model in global settings.");
            string destination = $"{model.Provider.Name} / {model.Model}\n{model.BaseUrl}\n" +
                $"Reference images: {imageIds.Length}\n{prompt}";
            if (!await approve(destination, token).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Image generation was denied. Do not retry.");
            token.ThrowIfCancellationRequested();
            AiSettings verified = await store.LoadAsync(token).ConfigureAwait(false);
            ResolvedModel? latest = verified.FindModel(modelId);
            if (verified.ImageModelId != modelId || latest is null
                || latest.Model != model.Model || latest.Protocol != model.Protocol
                || latest.BaseUrl != model.BaseUrl || latest.ApiKeyOwnerId != model.ApiKeyOwnerId)
            {
                throw new InvalidOperationException("Image generation settings changed during approval. Send a new request.");
            }

            DataContent output = await store.GenerateImageAsync(model, prompt, inputs, token).ConfigureAwait(false);
            ChatGeneratedImage reference = images.Save(output);
            conversation.Register(reference);
            lock (_gate) _results[reference.FileName] = reference;
            return JsonSerializer.Serialize(new
            {
                type = "image_generation_result",
                operation = inputs.Length == 0 ? "generate" : "edit",
                imageId = reference.FileName,
                mediaType = reference.MediaType,
                model = model.Model
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
