using System.Text.Json;

namespace VelaShell.Plugin.Ai;

internal static class AiHttp
{
    internal static readonly string UserAgent = ReadUserAgent();

    private static string ReadUserAgent()
    {
        using Stream manifest = typeof(AiHttp).Assembly.GetManifestResourceStream("VelaShell.Plugin.Ai.plugin.json")!;
        using var json = JsonDocument.Parse(manifest, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return $"VelaShell-Copilot/{json.RootElement.GetProperty("version").GetString()} (VelaShell Copilot)";
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> ProviderHeaders(IReadOnlyList<KeyValuePair<string, string>>? headers)
    {
        if (headers?.Any(h => h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return headers;
        }
        return [new("User-Agent", UserAgent), .. headers ?? []];
    }

    internal static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
