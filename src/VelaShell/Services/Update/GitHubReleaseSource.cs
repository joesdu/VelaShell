using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VelaShell.Services.Update;

/// <summary>
/// 以 GitHub Releases 为更新源:每个 Release 附带 CI 生成的 latest.json 清单。
/// stable 通道优先走固定重定向地址 releases/latest/download/latest.json(不占 API 配额、
/// 不受限流);该地址只认非预发布 Release,404(例如 beta 阶段全是预发布)时回退到
/// REST API 列表取最新可用发布。preview 通道直接走 API 列表(预发布也算)。
/// </summary>
public sealed class GitHubReleaseSource : IUpdateSource
{
    private readonly string _owner;
    private readonly string _repo;
    private readonly HttpClient _http;

    /// <summary>以仓库地址(https://github.com/&lt;owner&gt;/&lt;repo&gt;)构造;<paramref name="handler" /> 供测试注入。</summary>
    public GitHubReleaseSource(string repositoryUrl, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        Uri uri = new(repositoryUrl.TrimEnd('/'));
        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2 || segments[0].Length == 0 || segments[1].Length == 0)
        {
            throw new ArgumentException($"Not a GitHub repository URL: {repositoryUrl}", nameof(repositoryUrl));
        }
        _owner = segments[0];
        _repo = segments[1];
        _http = handler != null ? new(handler) : new();
        // 下载走同一个 client,大包需要宽裕的整体超时;单步(清单/API)另用 CTS 收紧。
        _http.Timeout = TimeSpan.FromMinutes(15);
        // GitHub REST API 要求 User-Agent,缺失直接 403。
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VelaShell-Updater", "1"));
    }

    /// <inheritdoc />
    public async Task<UpdateManifest?> GetLatestManifestAsync(bool includePreRelease, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        if (!includePreRelease)
        {
            string url = $"https://github.com/{_owner}/{_repo}/releases/latest/download/latest.json";
            using HttpResponseMessage response = await _http.GetAsync(url, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                return UpdateManifest.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            }
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
            // 404:尚无正式版(或老版本 Release 没有清单),回退 API 列表。
        }
        string? tag = await FindLatestTagViaApiAsync(includePreRelease, cts.Token);
        if (tag == null)
        {
            return null;
        }
        string manifestUrl = $"https://github.com/{_owner}/{_repo}/releases/download/{Uri.EscapeDataString(tag)}/latest.json";
        using HttpResponseMessage manifestResponse = await _http.GetAsync(manifestUrl, cts.Token);
        if (manifestResponse.StatusCode == HttpStatusCode.NotFound)
        {
            // 最新 Release 没带清单(升级更新机制前发布的老版本),视为无可用更新。
            return null;
        }
        manifestResponse.EnsureSuccessStatusCode();
        return UpdateManifest.Parse(await manifestResponse.Content.ReadAsStringAsync(cts.Token));
    }

    /// <summary>
    /// 经 REST API 取最新可用 Release 的标签:preview 通道取第一个非草稿;
    /// stable 通道取第一个非草稿非预发布,一个都没有时(beta 阶段)放宽为第一个非草稿。
    /// </summary>
    private async Task<string?> FindLatestTagViaApiAsync(bool includePreRelease, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        string? firstNonDraft = null;
        foreach (JsonElement release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean())
            {
                continue;
            }
            string? tag = release.GetProperty("tag_name").GetString();
            firstNonDraft ??= tag;
            if (includePreRelease || !release.GetProperty("prerelease").GetBoolean())
            {
                return tag;
            }
        }
        return firstNonDraft;
    }

    /// <inheritdoc />
    public async Task DownloadAssetAsync(
        UpdateManifest manifest,
        UpdateAsset asset,
        string destinationPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string url = $"https://github.com/{_owner}/{_repo}/releases/download/"
            + $"{Uri.EscapeDataString(manifest.Tag)}/{Uri.EscapeDataString(asset.Name)}";
        using HttpResponseMessage response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? asset.Size;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream target = new(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        byte[] buffer = new byte[81920];
        long copied = 0;
        int lastPercent = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (total > 0 && progress != null)
            {
                int percent = (int)(copied * 100 / total);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress.Report(Math.Min(percent, 100));
                }
            }
        }
        progress?.Report(100);
    }
}
