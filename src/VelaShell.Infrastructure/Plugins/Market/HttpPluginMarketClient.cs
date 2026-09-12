using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace VelaShell.Infrastructure.Plugins.Market;

/// <summary>
/// 走 HTTP 的插件商店客户端。
/// </summary>
/// <remarks>
/// <para>
/// 规矩与 <c>HttpAnnouncementFeed</c> 一套:**只走 https**(明文取回的版本号与下载地址
/// 可被中间人换掉,而那头连着的是要在本机跑起来的代码)、响应体有上限、元数据请求短超时、
/// 失败不抛只留一行 <see cref="Trace" />。出站自动继承进程级
/// <c>HttpClient.DefaultProxy</c>(由 <c>VelaWebProxy</c> 接管),因此与更新检查、
/// 资讯源同样遵守「设置 → 网络代理」。
/// </para>
/// <para>
/// **不发任何身份信息**:没有账号、没有令牌、没有安装 id。请求里唯一与本机有关的东西,
/// 是用户自己装了哪几个插件的 id —— 那是"问哪几个插件有没有新版"绕不开的输入。
/// </para>
/// </remarks>
/// <param name="baseUrl">商店根地址;缺省走 <see cref="DefaultBaseUrl" />。</param>
/// <param name="httpClient">注入以便测试;省略时用内置的短超时客户端。</param>
public sealed class HttpPluginMarketClient(string? baseUrl = null, HttpClient? httpClient = null) : IPluginMarketClient
{
    /// <summary>官方插件商店地址。</summary>
    public const string DefaultBaseUrl = "https://market.easilynet.top";

    /// <summary>一次请求最多问多少个 id(与商店侧的上限一致)。超出的分批问。</summary>
    private const int MaxIdsPerRequest = 200;

    /// <summary>版本表的响应体上限:两百条记录的 JSON 远不到这个量级,超了只可能是地址配错了。</summary>
    private const int MaxMetadataBytes = 512 * 1024;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly HttpClient _http = httpClient ?? Shared;

    /// <inheritdoc />
    public string BaseUrl { get; } = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, PluginMarketVersion>> GetLatestAsync(
        IReadOnlyCollection<string> pluginIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pluginIds);
        string[] ids = [.. pluginIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)];
        var found = new Dictionary<string, PluginMarketVersion>(StringComparer.Ordinal);
        if (ids.Length == 0 || !Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? root) || root.Scheme != Uri.UriSchemeHttps)
        {
            return found;
        }
        for (int offset = 0; offset < ids.Length; offset += MaxIdsPerRequest)
        {
            string batch = string.Join(',', ids.Skip(offset).Take(MaxIdsPerRequest));
            string? json = await GetMetadataAsync(
                $"{BaseUrl}/api/plugins/latest?ids={Uri.EscapeDataString(batch)}", cancellationToken)
                .ConfigureAwait(false);
            if (json is null)
            {
                // 这一批没问到就算了,已经问到的仍然作数 —— 半张表好过一张空表。
                continue;
            }
            try
            {
                Parse(json, found);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                Trace.WriteLine($"[VelaShell] Plugin marketplace returned an unreadable version list: {ex.Message}");
            }
        }
        return found;
    }

    /// <inheritdoc />
    public async Task DownloadAsync(
        string pluginId,
        string version,
        string destinationPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string ticketUrl = $"{BaseUrl}/api/plugins/{Uri.EscapeDataString(pluginId)}"
                           + $"/versions/{Uri.EscapeDataString(version)}/download";
        string json = await GetMetadataAsync(ticketUrl, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException(
                          $"The marketplace has no downloadable package for {pluginId} v{version}.");

        using var ticket = JsonDocument.Parse(json);
        JsonElement root = ticket.RootElement;
        string url = root.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() ?? "" : "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? download)
            || (download.Scheme != Uri.UriSchemeHttps && download.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"The marketplace returned a download URL that is not http(s): '{url}'.");
        }
        string? expected = root.TryGetProperty("fileSha256", out JsonElement shaElement) ? shaElement.GetString() : null;
        long declaredSize = root.TryGetProperty("packageSize", out JsonElement sizeElement)
                            && sizeElement.TryGetInt64(out long parsed)
            ? parsed
            : 0;

        string actual = await DownloadToFileAsync(download, destinationPath, declaredSize, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrEmpty(expected) && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            // 摘要对不上就把文件删掉再报错:一个内容存疑的包留在磁盘上,
            // 迟早会有人(或某段重试逻辑)把它当成"已经下好了"直接装上去。
            TryDelete(destinationPath);
            throw new InvalidOperationException(
                $"The downloaded package for {pluginId} v{version} does not match the digest the marketplace declared.");
        }
    }

    /// <summary>取一段 JSON 元数据;地址不可达、非 2xx 或体积超限时返回 <see langword="null" />。</summary>
    private async Task<string?> GetMetadataAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.Add(new("application/json"));
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxMetadataBytes)
            {
                return null;
            }
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[] buffer = new byte[MaxMetadataBytes];
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
                if (chunk == 0)
                {
                    break;
                }
                read += chunk;
            }
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or InvalidOperationException or IOException)
        {
            // 商店不可达是常态(离线、内网、代理没配好)。当作"这次没查到",
            // 但留一行 —— 否则看到的人无从判断该查网络还是查代码。
            cancellationToken.ThrowIfCancellationRequested();
            Trace.WriteLine($"[VelaShell] Plugin marketplace is unreachable ({url}): {ex.Message}");
            return null;
        }
    }

    /// <summary>流式下载到 <c>*.partial</c>、边下边算 SHA-256,完整后才改名到位。返回整包摘要。</summary>
    private async Task<string> DownloadToFileAsync(
        Uri url, string destinationPath, long declaredSize, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        string partial = destinationPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string hash;
        using (HttpResponseMessage response = await _http
                   .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? declaredSize;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream target = new(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            byte[] buffer = new byte[81920];
            long copied = 0;
            int lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                copied += read;
                if (total > 0 && progress is not null)
                {
                    int percent = (int)Math.Min(copied * 100 / total, 100);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            }
            hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        File.Move(partial, destinationPath, true);
        progress?.Report(100);
        return hash;
    }

    /// <summary>把 <c>/api/plugins/latest</c> 的响应读进结果表。</summary>
    private static void Parse(string json, Dictionary<string, PluginMarketVersion> into)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("plugins", out JsonElement plugins)
            || plugins.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (JsonProperty entry in plugins.EnumerateObject())
        {
            JsonElement v = entry.Value;
            if (!v.TryGetProperty("version", out JsonElement versionElement)
                || versionElement.GetString() is not { Length: > 0 } version)
            {
                continue;
            }
            into[entry.Name] = new()
            {
                Id = entry.Name,
                Version = version,
                ApiLevel = v.TryGetProperty("apiLevel", out JsonElement api) && api.TryGetInt32(out int level) ? level : 0,
                MinHostVersion = Text(v, "minHostVersion"),
                MinSdkVersion = Text(v, "minSdkVersion"),
                Signature = Text(v, "signature"),
                PublisherFingerprint = Text(v, "publisherFingerprint"),
                FileSha256 = Text(v, "fileSha256"),
                PackageSize = v.TryGetProperty("packageSize", out JsonElement size) && size.TryGetInt64(out long bytes)
                    ? bytes
                    : 0
            };
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉不阻断:调用方已经拿到"摘要对不上"的异常,不会去装它。
        }
    }
}
