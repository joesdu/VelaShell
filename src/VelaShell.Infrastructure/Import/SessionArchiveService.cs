using System.Reflection;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Import;

/// <summary>
/// <see cref="ISessionArchiveService" /> 的实现:从仓储取数据交给纯函数,再把算好的结果写回仓储。
/// </summary>
/// <param name="sessions">连接与分组仓储。</param>
/// <param name="credentials">共享凭据仓储;null(无头宿主、单测)时当作没有共享凭据。</param>
/// <param name="dataStore">通用文档存储(隧道在 <c>tunnels</c> 集合里);null 时不导出、不导入隧道。</param>
/// <param name="timeProvider">时钟(导出时间);null 用系统时钟。</param>
public sealed class SessionArchiveService(
    ISessionRepository sessions,
    ISharedCredentialRepository? credentials = null,
    IAppDataStore? dataStore = null,
    TimeProvider? timeProvider = null) : ISessionArchiveService
{
    /// <summary>隧道所在的集合(文档 Id = 连接 Id),与隧道面板、云同步同一处。</summary>
    private const string TunnelsCollection = "tunnels";

    private readonly ISessionRepository _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<SessionExportPreview> PreviewExportAsync(
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        (List<SessionProfile> all, List<ServerGroup> groups, List<SharedCredential> shared) = await LoadAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        SessionExportSelection selection = SessionArchiveBuilder.Select(profileIds, all, groups);
        SessionArchive archive = SessionArchiveBuilder.Build(selection, groups, shared,
            new Dictionary<Guid, List<TunnelConfig>>(), _time.GetUtcNow().UtcDateTime, null);
        return new SessionExportPreview(
            selection.Sessions.Count,
            selection.RequestedCount,
            selection.DependencyCount,
            archive.Groups.Count,
            SessionArchiveJson.CountSecrets(archive));
    }

    /// <inheritdoc />
    public async Task<SessionExportFile> ExportAsync(
        IReadOnlyCollection<Guid> profileIds,
        SessionFileFormat format,
        string? passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        (List<SessionProfile> all, List<ServerGroup> groups, List<SharedCredential> shared) = await LoadAsync().ConfigureAwait(false);
        SessionExportSelection selection = SessionArchiveBuilder.Select(profileIds, all, groups);
        Dictionary<Guid, List<TunnelConfig>> tunnels = format == SessionFileFormat.Json
            ? await LoadTunnelsAsync(selection.Sessions, cancellationToken).ConfigureAwait(false)
            : [];
        SessionArchive archive = SessionArchiveBuilder.Build(selection, groups, shared, tunnels,
            _time.GetUtcNow().UtcDateTime, ApplicationName());
        if (format == SessionFileFormat.Csv)
        {
            return new SessionExportFile(SessionFileText.EncodeCsv(SessionCsv.Write(archive)), selection.Sessions.Count, false);
        }
        bool withSecrets = !string.IsNullOrEmpty(passphrase) && SessionArchiveJson.CountSecrets(archive) > 0;
        string json = SessionArchiveJson.Serialize(archive, withSecrets ? passphrase : null);
        return new SessionExportFile(SessionFileText.EncodeJson(json), selection.Sessions.Count, withSecrets);
    }

    /// <inheritdoc />
    public byte[] CreateCsvTemplate() => SessionFileText.EncodeCsv(SessionCsv.Template());

    /// <inheritdoc />
    public SessionImportDocument Parse(string fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);
        string text = SessionFileText.Decode(content);
        string name = Path.GetFileName(fileName);
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        bool json = extension switch
        {
            ".json" => true,
            ".csv" or ".tsv" or ".txt" => false,
            _ => text.TrimStart().StartsWith('{')
        };
        return json ? SessionArchiveJson.Read(name, text) : SessionCsv.Read(name, text);
    }

    /// <inheritdoc />
    public async Task<SessionImportPlan> PlanAsync(SessionImportDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        (List<SessionProfile> all, List<ServerGroup> groups, List<SharedCredential> shared) = await LoadAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return SessionImportPlanner.Plan(document, all, groups, shared);
    }

    /// <inheritdoc />
    public async Task<SessionFileImportOutcome> ImportAsync(
        SessionImportPlan plan,
        SessionImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        SessionImportWriteSet writes = SessionImportPlanner.BuildWrites(plan, options);

        // 落盘顺序:凭据 → 分组 → 连接 → 隧道。连接引用前两者,反过来的话中途失败会留下一批
        // GroupId 指向不存在分组的连接 —— 资源管理器建树时找不到分组,它们就凭空消失了。
        if (credentials is not null)
        {
            foreach (SharedCredential credential in writes.SharedCredentials)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await credentials.SaveAsync(credential).ConfigureAwait(false);
            }
        }
        foreach (ServerGroup group in writes.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _sessions.SaveGroupAsync(group).ConfigureAwait(false);
        }
        foreach (SessionProfile profile in writes.Sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _sessions.SaveSessionAsync(profile).ConfigureAwait(false);
        }
        if (dataStore is not null)
        {
            foreach ((Guid profileId, List<TunnelConfig> tunnels) in writes.Tunnels)
            {
                await dataStore.UpsertAsync(TunnelsCollection, profileId.ToString("D"), tunnels, cancellationToken).ConfigureAwait(false);
            }
        }
        return new SessionFileImportOutcome(
            writes.Created,
            writes.Updated,
            writes.Skipped,
            writes.GroupsCreated,
            writes.SecretsImported,
            writes.Warnings);
    }

    private async Task<(List<SessionProfile> All, List<ServerGroup> Groups, List<SharedCredential> Shared)> LoadAsync()
    {
        List<SessionProfile> all = await _sessions.GetAllSessionsAsync().ConfigureAwait(false);
        List<ServerGroup> groups = await _sessions.GetAllGroupsAsync().ConfigureAwait(false);
        List<SharedCredential> shared = credentials is null ? [] : await credentials.GetAllAsync().ConfigureAwait(false);
        return (all, groups, shared);
    }

    private async Task<Dictionary<Guid, List<TunnelConfig>>> LoadTunnelsAsync(
        IReadOnlyList<SessionProfile> selection,
        CancellationToken cancellationToken)
    {
        var tunnels = new Dictionary<Guid, List<TunnelConfig>>();
        if (dataStore is null)
        {
            return tunnels;
        }
        foreach (SessionProfile profile in selection)
        {
            List<TunnelConfig>? list = await dataStore
                .GetAsync<List<TunnelConfig>>(TunnelsCollection, profile.Id.ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            if (list is { Count: > 0 })
            {
                tunnels[profile.Id] = list;
            }
        }
        return tunnels;
    }

    private static string ApplicationName()
    {
        string? version = typeof(SessionArchiveService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrEmpty(version) ? "VelaShell" : $"VelaShell {version}";
    }
}
