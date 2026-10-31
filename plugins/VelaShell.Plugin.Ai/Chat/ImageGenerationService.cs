using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Images;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>One image request, with no URL fetching or automatic retries.</summary>
internal sealed class ImageGenerationService(ImageClient client, string model, bool requestBytes)
{
    private const int MaxInputBytes = 20 * 1024 * 1024;

    /// <summary>
    /// 改图接口(<c>/images/edits</c>)收得下的输入格式。只有这几种才登记成会话里可改的图、发给模型图片 ID;
    /// GIF 仍可作为视觉附件发给聊天模型,但不给 ID —— 否则模型拿它调 <c>edit_image</c> 必然被下面拒掉,
    /// 还会把这一轮的图片工具锁死。
    /// </summary>
    internal static bool IsEditableInput(string? mediaType) => mediaType is "image/png" or "image/jpeg" or "image/webp";

    public async Task<DataContent> GenerateAsync(string prompt, IReadOnlyList<DataContent>? inputs, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        token.ThrowIfCancellationRequested();
        if (inputs is null || inputs.Count == 0)
        {
            OpenAI.Images.ImageGenerationOptions? options = requestBytes ? new() { ResponseFormat = GeneratedImageFormat.Bytes } : null;
            GeneratedImage image = (await client.GenerateImageAsync(prompt, options, token).ConfigureAwait(false)).Value;
            if (image.ImageBytes is null || image.ImageBytes.ToMemory().IsEmpty)
                throw new InvalidOperationException("The image endpoint did not return image bytes.");
            return new DataContent(image.ImageBytes.ToMemory(), "image/png");
        }
        if (inputs.Count > 3) throw new ArgumentException("At most three input images are supported.", nameof(inputs));
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(model), "model");
        multipart.Add(new StringContent(prompt), "prompt");
        if (requestBytes) multipart.Add(new StringContent("b64_json"), "response_format");
        foreach (DataContent input in inputs)
        {
            if (input is null || input.Data.IsEmpty || input.Data.Length > MaxInputBytes)
                throw new ArgumentException("Each input image must be nonempty and at most 20 MiB.", nameof(inputs));
            string extension = input.MediaType?.ToLowerInvariant() switch
            {
                "image/png" => "png",
                "image/jpeg" => "jpg",
                "image/webp" => "webp",
                _ => throw new ArgumentException("Input images must be PNG, JPEG, or WebP.", nameof(inputs))
            };
            var part = new ByteArrayContent(input.Data.ToArray());
            part.Headers.ContentType = new MediaTypeHeaderValue(input.MediaType);
            multipart.Add(part, "image[]", $"image-{multipart.Count()}.{extension}");
        }
        using var stream = new MemoryStream();
        await multipart.CopyToAsync(stream, token).ConfigureAwait(false);
        stream.Position = 0;
        using var body = BinaryContent.Create(stream);
        ClientResult result = await client.GenerateImageEditsAsync(body, multipart.Headers.ContentType!.ToString(),
            new RequestOptions { CancellationToken = token }).ConfigureAwait(false);
        using var json = JsonDocument.Parse(result.GetRawResponse().Content.ToMemory());
        if (!json.RootElement.TryGetProperty("data", out JsonElement data) || data.GetArrayLength() == 0
            || !data[0].TryGetProperty("b64_json", out JsonElement encoded) || encoded.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(encoded.GetString()))
        {
            throw new InvalidOperationException("The image endpoint did not return image bytes.");
        }

        return new DataContent(Convert.FromBase64String(encoded.GetString()!), "image/png");
    }
}
