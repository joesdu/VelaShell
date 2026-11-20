using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VelaShell.Core.Layouts;
using VelaShell.Core.Resources;
using VelaShell.Core.Sync;

namespace VelaShell.Infrastructure.Sync;

/// <summary>
/// 布局模板的云同步:Gist 里与主载荷并排的 <c>velashell-layouts.json</c>(格式见 <see cref="LayoutTemplatesSyncEnvelope" />),
/// 按模板 Id 合并、删除靠墓碑(<see cref="LayoutTemplateMerge" />)。
/// </summary>
/// <remarks>
/// <para>
/// <b>与主载荷的「较新者胜」各管各的</b>:主载荷照旧整份推拉、比时间定方向;模板这一份每次读到 Gist 都与本机合并一次 ——
/// 推送前先合并云端那份再写(两台设备各存的模板都留下),拉取后合并进本机,核对出主载荷两边一致时也合并
/// (升级前就有的模板、刚打开模板同步的设备,靠这一步补上去)。合并与先后、次数无关,所以多合几次不会打架。
/// </para>
/// <para>
/// 整块放在这个分部文件里,主文件只在读 Gist、推送、拉取之后、恢复历史这几处各挂一行,
/// 免得与同时在改主文件的其它改动(审计来源、无口令剔除机密)缠在一起。
/// </para>
/// </remarks>
public sealed partial class GistSyncService
{
    /// <summary>布局模板在 Gist 里的文件名。</summary>
    internal const string LayoutTemplatesFileName = "velashell-layouts.json";

    private static readonly string[] SyncFileNames = [GistFileName, LayoutTemplatesFileName];

    /// <summary>云端那份布局模板读出来的样子。</summary>
    /// <param name="State">模板与墓碑;云端没有(或文件坏了、按没有处理)时为 null。</param>
    /// <param name="SkipNote">这一次跳过模板同步的原因(附在同步结果后面);能读时为 null。</param>
    internal readonly record struct RemoteLayoutTemplates(LayoutTemplateSyncState? State, string? SkipNote);

    /// <summary>这台设备同不同步布局模板。</summary>
    private bool LayoutTemplatesInScope(SyncSettings config) => layoutTemplates is not null && config.SyncLayoutTemplates;

    /// <summary>读 Gist:主载荷与布局模板文件一次取回,保证两者来自同一个 revision。</summary>
    /// <param name="token">GitHub 令牌。</param>
    /// <param name="gistId">Gist Id。</param>
    /// <param name="revision">指定的修订版本;null = 当前。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主载荷内容、revision、布局模板文件内容(不存在的为 null)。</returns>
    private static async Task<(string? Content, string Version, string? Layouts)> ReadGistAsync(
        string token,
        string gistId,
        string? revision,
        CancellationToken cancellationToken)
    {
        (IReadOnlyDictionary<string, string?> files, string version) = await GistApiClient
            .GetFilesAsync(token, gistId, revision, SyncFileNames, cancellationToken)
            .ConfigureAwait(false);
        return (files[GistFileName], version, files[LayoutTemplatesFileName]);
    }

    /// <summary>
    /// 一次推送要写的文件:主载荷,外加布局模板文件 —— 后者只在同步范围内、且与云端那份合并之后确有不同时才写;
    /// 没列出的文件 GitHub 原样留着,所以不写不等于删。
    /// </summary>
    private async Task<Dictionary<string, string>> BuildUploadFilesAsync(
        SyncSettings config,
        string content,
        string? remote,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { [GistFileName] = content };
        if (!LayoutTemplatesInScope(config))
        {
            return files;
        }
        if (await BuildLayoutTemplatesUploadAsync(config, remote, cancellationToken).ConfigureAwait(false) is { } layouts)
        {
            files[LayoutTemplatesFileName] = layouts;
        }
        return files;
    }

    /// <summary>
    /// 推送前把云端那份合并进本机(云端有、本机没有的模板与删除顺手收下),合并结果与云端不同时返回要写上去的文件内容。
    /// 不在范围内、云端那份读不了(更新的版本写的、口令解不开)、本机库读不出来时返回 null —— 只推主载荷,模板这次不动。
    /// </summary>
    /// <param name="config">同步配置。</param>
    /// <param name="remoteContent">云端那份文件的内容;null = 还没有。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>要写的文件内容,或 null。</returns>
    internal async Task<string?> BuildLayoutTemplatesUploadAsync(
        SyncSettings config,
        string? remoteContent,
        CancellationToken cancellationToken)
    {
        if (!LayoutTemplatesInScope(config))
        {
            return null;
        }
        (LayoutTemplateMergeResult? merge, string? note) = await MergeRemoteLayoutTemplatesAsync(config, remoteContent, cancellationToken)
            .ConfigureAwait(false);
        if (note is not null)
        {
            // 推送这条路上没有结果消息可附,原因只记日志;拉取与核对那条路会把它报给用户。
            Trace.WriteLine($"[Sync] 布局模板这次没有推送:{note}");
        }
        return merge is { RemoteChanged: true } ? BuildLayoutTemplatesContent(config, merge.Merged) : null;
    }

    /// <summary>
    /// 拉取(或核对出主载荷两边一致)之后合并云端的布局模板。云端缺本机的改动时 —— 升级前就有的模板、
    /// 较新者胜那一轮输掉的本机模板改动、刚打开模板同步的设备 —— <paramref name="allowUpload" /> 为真就接着推一次补上;
    /// 「仅拉取」不推,留给下一次同步。
    /// </summary>
    private async Task<SyncResult> ReconcileLayoutTemplatesAsync(
        SyncSettings config,
        string token,
        string? remoteContent,
        SyncResult result,
        bool allowUpload,
        CancellationToken cancellationToken)
    {
        if (!result.Success || !LayoutTemplatesInScope(config))
        {
            return result;
        }
        (LayoutTemplateMergeResult? merge, string? note) = await MergeRemoteLayoutTemplatesAsync(config, remoteContent, cancellationToken)
            .ConfigureAwait(false);
        if (note is not null)
        {
            return result with { Message = result.Message + note };
        }
        if (merge is not { RemoteChanged: true } || !allowUpload)
        {
            return result;
        }
        // 拉取已经把同步状态写回去了(revision、改动标记),推送要接着那一份往下写。
        SyncSettings latest = await GetSyncSettingsAsync(cancellationToken).ConfigureAwait(false);
        SyncResult pushed = await PushCoreAsync(latest, token, cancellationToken).ConfigureAwait(false);
        return result.Action == SyncAction.UpToDate
            ? pushed
            : result with { Message = result.Message + Strings.Get("SyncSvc_LayoutsPushed") };
    }

    /// <summary>
    /// 读云端那份并合并进本机。返回合并结果;跳过时(读不了、本机库读不出来)合并结果为 null、附上原因。
    /// </summary>
    /// <param name="config">同步配置。</param>
    /// <param name="remoteContent">云端那份文件的内容;null = 还没有。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>合并结果与跳过的原因。</returns>
    internal async Task<(LayoutTemplateMergeResult? Merge, string? Note)> MergeRemoteLayoutTemplatesAsync(
        SyncSettings config,
        string? remoteContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoteLayoutTemplates remote = ReadLayoutTemplatesFile(config, remoteContent);
        if (remote.SkipNote is { } skip)
        {
            return (null, skip);
        }
        try
        {
            return (await layoutTemplates!.MergeAsync(remote.State).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // 本机库读写失败只跳过模板这一份:设置与连接已经同步了,不该因为它整次报失败。
            Trace.WriteLine($"[Sync] 合并布局模板失败:{ex.Message}");
            return (null, Strings.Format("SyncSvc_LayoutsFailed", ex.Message));
        }
    }

    /// <summary>
    /// 恢复历史版本时,把那个版本里的模板全部找回(见 <see cref="LayoutTemplateStore.RestoreAsync" />);随后的推送把它们写回云端。
    /// </summary>
    /// <returns>要附在结果后面的说明;没有可说的为 null。</returns>
    internal async Task<string?> RestoreLayoutTemplatesAsync(SyncSettings config, string? revisionContent)
    {
        if (!LayoutTemplatesInScope(config))
        {
            return null;
        }
        RemoteLayoutTemplates revision = ReadLayoutTemplatesFile(config, revisionContent);
        if (revision.SkipNote is not null || revision.State is not { } state)
        {
            // 那个版本里没有模板文件(功能上线之前的版本)就什么都不动。
            return revision.SkipNote;
        }
        try
        {
            await layoutTemplates!.RestoreAsync(state.Templates).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Trace.WriteLine($"[Sync] 恢复布局模板失败:{ex.Message}");
            return Strings.Format("SyncSvc_LayoutsFailed", ex.Message);
        }
    }

    /// <summary>写出布局模板文件:设了端到端口令时整段加密(与主载荷同一套 AES-GCM),否则明文。</summary>
    /// <param name="config">同步配置。</param>
    /// <param name="state">要写的模板与墓碑。</param>
    /// <returns>文件内容。</returns>
    internal string BuildLayoutTemplatesContent(SyncSettings config, LayoutTemplateSyncState state)
    {
        var data = new LayoutTemplatesSyncData { Templates = [.. state.Templates], Tombstones = [.. state.Tombstones] };
        bool encrypted = HasPassphrase(config);
        var envelope = new LayoutTemplatesSyncEnvelope
        {
            UpdatedAtUtc = DateTime.UtcNow,
            DeviceName = config.DeviceName,
            Encrypted = encrypted,
        };
        if (encrypted)
        {
            envelope.CipherText = SyncCrypto.Encrypt(JsonSerializer.Serialize(data, JsonOptions), Passphrase(config)!);
        }
        else
        {
            envelope.Data = data;
        }
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    /// <summary>
    /// 读云端那份布局模板文件。没有、或坏了(不是 JSON、格式标识不对、密文残缺)按「没有」处理 —— 下次推送用合并结果重写它,
    /// 旧内容仍在 Gist 的历史版本里;版本比本机新、加了密而本机没口令或口令不对时跳过(读不了也不写),附上原因。
    /// </summary>
    /// <param name="config">同步配置(取口令)。</param>
    /// <param name="content">文件内容;null 或空白 = 没有。</param>
    /// <returns>读出来的样子。</returns>
    internal RemoteLayoutTemplates ReadLayoutTemplatesFile(SyncSettings config, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new(null, null);
        }
        try
        {
            LayoutTemplatesSyncEnvelope? envelope = JsonSerializer.Deserialize<LayoutTemplatesSyncEnvelope>(content, JsonOptions);
            if (envelope is null
                || !string.Equals(envelope.Format, LayoutTemplatesSyncEnvelope.FormatId, StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine("[Sync] 云端的布局模板文件格式不对,按没有处理。");
                return new(null, null);
            }
            if (envelope.SchemaVersion > LayoutTemplatesSyncEnvelope.CurrentSchemaVersion)
            {
                return new(null, Strings.Get("SyncSvc_LayoutsNewer"));
            }
            LayoutTemplatesSyncData? data = envelope.Data;
            if (envelope.Encrypted)
            {
                string? passphrase = Passphrase(config);
                if (string.IsNullOrEmpty(passphrase))
                {
                    return new(null, Strings.Get("SyncSvc_LayoutsLocked"));
                }
                data = JsonSerializer.Deserialize<LayoutTemplatesSyncData>(
                    SyncCrypto.Decrypt(envelope.CipherText ?? string.Empty, passphrase), JsonOptions);
            }
            return new(new LayoutTemplateSyncState(data?.Templates ?? [], data?.Tombstones ?? []), null);
        }
        catch (CryptographicException)
        {
            return new(null, Strings.Get("SyncSvc_LayoutsLocked"));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or NotSupportedException)
        {
            Trace.WriteLine($"[Sync] 云端的布局模板文件读不出来,按没有处理:{ex.Message}");
            return new(null, null);
        }
    }
}
