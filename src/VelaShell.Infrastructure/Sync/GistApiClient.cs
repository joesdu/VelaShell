using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VelaShell.Core.Resources;
using VelaShell.Core.Sync;

namespace VelaShell.Infrastructure.Sync;

/// <summary>
/// GitHub Gist REST 客户端(创建/读取/更新/修订历史/指定修订)。
/// 只依赖 gist 权限的 PAT;所有请求走同一个 HttpClient。
/// </summary>
public sealed class GistApiClient
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>测试替身:只在设下它的那条异步流程里替掉 <see cref="SharedHttp" />,别的流程(并行的别的用例)看不到。</summary>
    private static readonly AsyncLocal<HttpClient?> TestHttp = new();

    private static HttpClient Http => TestHttp.Value ?? SharedHttp;

    /// <summary>
    /// 测试用:让当前异步流程里的 Gist 请求改走 <paramref name="client" />(一个假的 GitHub),
    /// 好把 <see cref="GistSyncService.SyncNowAsync" /> 的推拉方向整条跑一遍;传 null 退回真的那个。
    /// </summary>
    /// <param name="client">替身客户端。</param>
    internal static void UseHttpClientForTests(HttpClient? client) => TestHttp.Value = client;

    /// <summary>创建 secret Gist,返回 (gistId, 初始 revision)。</summary>
    public static Task<(string GistId, string Version)> CreateGistAsync(string token,
        string description,
        string fileName,
        string content,
        CancellationToken cancellationToken) =>
        CreateGistAsync(token, description, new Dictionary<string, string> { [fileName] = content }, cancellationToken);

    /// <summary>创建 secret Gist 并一次写入多个文件,返回 (gistId, 初始 revision)。</summary>
    public static async Task<(string GistId, string Version)> CreateGistAsync(string token,
        string description,
        IReadOnlyDictionary<string, string> files,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["description"] = description,
            ["public"] = false,
            ["files"] = FilesNode(files)
        };
        JsonDocument doc = await SendAsync(token, HttpMethod.Post, "https://api.github.com/gists", body, cancellationToken).ConfigureAwait(false);
        using (doc)
        {
            string id = doc.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException(Strings.Get("SyncSvc_NoGistId"));
            return (id, ReadLatestVersion(doc.RootElement));
        }
    }

    /// <summary>更新 Gist 文件内容(每次更新即产生一个新 revision),返回新 revision。</summary>
    public static Task<string> UpdateGistAsync(string token,
        string gistId,
        string fileName,
        string content,
        CancellationToken cancellationToken) =>
        UpdateGistAsync(token, gistId, new Dictionary<string, string> { [fileName] = content }, cancellationToken);

    /// <summary>
    /// 在同一个 revision 里更新多个文件,返回新 revision。没列出的文件 GitHub 原样保留(「更新 Gist」接口的约定)——
    /// 旧客户端只写它认识的那个文件,旁边的布局模板文件因此不会被它抹掉。
    /// </summary>
    public static async Task<string> UpdateGistAsync(string token,
        string gistId,
        IReadOnlyDictionary<string, string> files,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["files"] = FilesNode(files)
        };
        JsonDocument doc = await SendAsync(token, HttpMethod.Patch, $"https://api.github.com/gists/{gistId}", body, cancellationToken).ConfigureAwait(false);
        using (doc)
        {
            return ReadLatestVersion(doc.RootElement);
        }
    }

    /// <summary>读取 Gist 当前内容;文件不存在时返回 null 内容。</summary>
    public static Task<(string? Content, string Version)> GetFileAsync(string token,
        string gistId,
        string fileName,
        CancellationToken cancellationToken) =>
        GetFileCoreAsync(token, $"https://api.github.com/gists/{gistId}", fileName, cancellationToken);

    /// <summary>读取指定修订版本的文件内容。</summary>
    public static Task<(string? Content, string Version)> GetFileAtRevisionAsync(string token,
        string gistId,
        string revision,
        string fileName,
        CancellationToken cancellationToken) =>
        GetFileCoreAsync(token, $"https://api.github.com/gists/{gistId}/{revision}", fileName, cancellationToken);

    /// <summary>
    /// 一次请求读出当前(<paramref name="revision" /> 为 null)或指定修订版本里的几个文件,保证它们来自同一个 revision;
    /// 不存在的文件内容为 null。
    /// </summary>
    public static Task<(IReadOnlyDictionary<string, string?> Contents, string Version)> GetFilesAsync(string token,
        string gistId,
        string? revision,
        IReadOnlyCollection<string> fileNames,
        CancellationToken cancellationToken) =>
        GetFilesCoreAsync(token,
            revision is null ? $"https://api.github.com/gists/{gistId}" : $"https://api.github.com/gists/{gistId}/{revision}",
            fileNames,
            cancellationToken);

    /// <summary>修订历史(新→旧,最多 30 条)。</summary>
    public static async Task<List<GistRevision>> GetCommitsAsync(string token, string gistId, CancellationToken cancellationToken)
    {
        JsonDocument doc = await SendAsync(token, HttpMethod.Get, $"https://api.github.com/gists/{gistId}/commits?per_page=30", null, cancellationToken).ConfigureAwait(false);
        using (doc)
        {
            var revisions = new List<GistRevision>();
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string? version = item.GetProperty("version").GetString();
                if (string.IsNullOrEmpty(version))
                {
                    continue;
                }
                DateTime committedAt = item.TryGetProperty("committed_at", out JsonElement at) && at.TryGetDateTime(out DateTime t)
                                           ? t.ToUniversalTime()
                                           : DateTime.MinValue;
                int additions = 0, deletions = 0;
                if (item.TryGetProperty("change_status", out JsonElement status))
                {
                    additions = status.TryGetProperty("additions", out JsonElement a) ? a.GetInt32() : 0;
                    deletions = status.TryGetProperty("deletions", out JsonElement d) ? d.GetInt32() : 0;
                }
                revisions.Add(new(version, committedAt, additions, deletions));
            }
            return revisions;
        }
    }

    private static async Task<(string? Content, string Version)> GetFileCoreAsync(string token,
        string url,
        string fileName,
        CancellationToken cancellationToken)
    {
        (IReadOnlyDictionary<string, string?> contents, string version) =
            await GetFilesCoreAsync(token, url, [fileName], cancellationToken).ConfigureAwait(false);
        return (contents[fileName], version);
    }

    private static async Task<(IReadOnlyDictionary<string, string?> Contents, string Version)> GetFilesCoreAsync(string token,
        string url,
        IReadOnlyCollection<string> fileNames,
        CancellationToken cancellationToken)
    {
        JsonDocument doc = await SendAsync(token, HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        using (doc)
        {
            string version = ReadLatestVersion(doc.RootElement);
            var contents = new Dictionary<string, string?>(StringComparer.Ordinal);
            bool hasFiles = doc.RootElement.TryGetProperty("files", out JsonElement files);
            foreach (string fileName in fileNames)
            {
                contents[fileName] = hasFiles && files.TryGetProperty(fileName, out JsonElement file)
                    ? await ReadContentAsync(token, file, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            return (contents, version);
        }
    }

    private static async Task<string?> ReadContentAsync(string token, JsonElement file, CancellationToken cancellationToken)
    {
        // 大文件(>1MB)content 会被截断,此时按 raw_url 取全文。
        bool truncated = file.TryGetProperty("truncated", out JsonElement tr) && tr.GetBoolean();
        if (!truncated)
        {
            return file.GetProperty("content").GetString();
        }
        string rawUrl = file.GetProperty("raw_url").GetString() ?? throw new InvalidOperationException(Strings.Get("SyncSvc_MissingRawUrl"));
        using HttpRequestMessage rawRequest = CreateRequest(token, HttpMethod.Get, rawUrl, null);
        using HttpResponseMessage rawResponse = await Http.SendAsync(rawRequest, cancellationToken).ConfigureAwait(false);
        rawResponse.EnsureSuccessStatusCode();
        return await rawResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject FilesNode(IReadOnlyDictionary<string, string> files)
    {
        var node = new JsonObject();
        foreach ((string name, string content) in files)
        {
            node[name] = new JsonObject { ["content"] = content };
        }
        return node;
    }

    private static string ReadLatestVersion(JsonElement gist) =>
        gist.TryGetProperty("history", out JsonElement history) && history.ValueKind == JsonValueKind.Array && history.GetArrayLength() > 0
            ? history[0].GetProperty("version").GetString() ?? ""
            : "";

    private static HttpRequestMessage CreateRequest(string token, HttpMethod method, string url, JsonNode? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("VelaShell");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static async Task<JsonDocument> SendAsync(string token,
        HttpMethod method,
        string url,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(token, method, url, body);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => Strings.Get("SyncSvc_TokenInvalid"),
                HttpStatusCode.NotFound => Strings.Get("SyncSvc_GistNotFound"),
                HttpStatusCode.Forbidden => Strings.Get("SyncSvc_Forbidden"),
                _ => Strings.Format("SyncSvc_ApiFailed", (int)response.StatusCode, Truncate(text))
            });
        }
        return JsonDocument.Parse(text);
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
