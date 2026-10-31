using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sync;

namespace VelaShell.Infrastructure.Sync;

/// <summary>
/// Gist 云同步编排:采集(设置/连接/片段)→ 打包(可选端到端加密)→ 推送;
/// 拉取 → 解包 → 应用(保留设备本地字段、upsert 合并不删本地独有数据)。
/// 智能方向:本地改动标记 × 远端 revision 比对;双端都改按“较新者胜”,
/// 被覆盖的一方永远可从 Gist 修订历史恢复。
/// </summary>
public sealed partial class GistSyncService(
    ISettingsService settingsService,
    ISessionRepository sessionRepository,
    IAppDataStore appDataStore,
    IQuickCommandRepository quickCommandRepository,
    ISecretProtector secretProtector,
    Core.Services.IBackgroundActivityService? backgroundActivity = null,
    ISharedCredentialRepository? sharedCredentials = null,
    // 布局模板单独一个文件同步,整块逻辑在 GistSyncService.LayoutTemplates.cs。
    Core.Layouts.LayoutTemplateStore? layoutTemplates = null
) : IGistSyncService
{
    /// <summary>
    /// 本机认得的最高载荷版本。3 = 带共享凭据(#550)。
    /// </summary>
    /// <remarks>
    /// 推送时只在真用到共享凭据时才写 3,否则仍写 2(见 <see cref="PayloadSchemaVersion" />):
    /// 不认识引用的旧客户端拉到这份载荷,会把连接上的引用当未知字段丢掉,再推回来时就把
    /// 引用从云端抹了 —— 写 3 让旧客户端照章拒收,而没用这个功能的人照旧与旧版本互通。
    /// </remarks>
    private const int CurrentSchemaVersion = 3;
    private const string ConfigCollection = "app_config";
    private const string ConfigDocId = "sync";
    private const string GistFileName = "velashell-sync.json";
    private const string GistDescription = "VelaShell settings sync (managed by VelaShell)";
    private const string TunnelsCollection = "tunnels";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { MarkMissingDirectoryBookmarks } },
    };

    /// <summary>
    /// 反序列化连接配置之前先把目录收藏置空:载荷里有这个字段(哪怕是 <c>[]</c>)就会被写回,
    /// 没有就一直是 <see langword="null" /> —— 拉取时据此区分「旧客户端推上来的、根本不带收藏」与「这边确实清空了收藏」。
    /// </summary>
    /// <remarks>
    /// <see cref="SessionProfile.DirectoryBookmarks" /> 有 <c>= []</c> 的初值,按类型反序列化之后两种情形长得一模一样。
    /// 只挂在同步自己的选项上,仓储与导入备份的序列化不受影响;推送只写不读,也不受影响。
    /// </remarks>
    private static void MarkMissingDirectoryBookmarks(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type == typeof(SessionProfile))
        {
            typeInfo.OnDeserializing = static profile => ((SessionProfile)profile).DirectoryBookmarks = null!;
        }
    }

    /// <summary>同步操作串行化:防止自动推送与手动同步并发互踩。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 在状态栏右下角的圆环上开一条"同步中"的活动;副标题用设置页上同名按钮的文案
    /// (仅推送 / 仅拉取 / 立即同步),用户看到的措辞与他点下去的那个按钮一致。
    /// <para>
    /// 走不确定态:整段是网络往返,给不出有意义的百分比,而这条活动要回答的问题
    /// 本来也只有一个 —— "现在到底有没有在同步"。
    /// </para>
    /// <para>
    /// 自动同步一向静默(启动拉取、保存后防抖推送,失败都不打扰用户),但"不打扰"
    /// 不该等于"什么都看不见":用户的连接配置正在被上传或覆盖,这件事值得有个去处。
    /// </para>
    /// </summary>
    /// <param name="actionKey">设置页动作按钮的资源键。</param>
    /// <returns>活动句柄;未注入账本时为 <see langword="null" />。</returns>
    private Core.Services.IBackgroundActivityScope? BeginSyncActivity(string actionKey) =>
        backgroundActivity?.Begin(Strings.Get("Msg_Syncing"), Strings.Get(actionKey));

    /// <summary>是否正在应用远端数据;为 true 时忽略由此触发的本地保存事件,避免拉取后被误判为本地改动而立即回推。</summary>
    public bool IsApplyingRemote { get; private set; }

    /// <inheritdoc />
    public event EventHandler? ProfilesApplied;

    /// <inheritdoc />
    public event EventHandler<string>? WebhookUrlMissing;

    /// <summary>读取持久化的同步配置;不存在时返回默认 <see cref="SyncSettings"/>。</summary>
    public async Task<SyncSettings> GetSyncSettingsAsync(
        CancellationToken cancellationToken = default
    ) =>
        await appDataStore
            .GetAsync<SyncSettings>(ConfigCollection, ConfigDocId, cancellationToken)
            .ConfigureAwait(false)
        ?? new SyncSettings();

    /// <summary>将同步配置保存到应用数据存储。</summary>
    public Task SaveSyncSettingsAsync(
        SyncSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        return appDataStore.UpsertAsync(ConfigCollection, ConfigDocId, settings, cancellationToken);
    }

    /// <summary>将明文 GitHub 令牌加密后写入配置;空白令牌则保留原有值不变。</summary>
    public void ApplyToken(SyncSettings settings, string? plainToken)
    {
        if (!string.IsNullOrWhiteSpace(plainToken))
        {
            settings.ProtectedToken = secretProtector.Protect(plainToken.Trim());
        }
    }

    /// <summary>设置端到端加密口令:传 null 保留现有口令,传空字符串清除口令,其余值加密后保存。</summary>
    public void ApplyPassphrase(SyncSettings settings, string? plainPassphrase)
    {
        if (plainPassphrase is null)
        {
            return; // 保留现有口令
        }
        settings.ProtectedPassphrase =
            plainPassphrase.Length == 0 ? null : secretProtector.Protect(plainPassphrase);
    }

    /// <summary>配置中是否已保存 GitHub 令牌。</summary>
    public bool HasToken(SyncSettings settings) => !string.IsNullOrEmpty(settings.ProtectedToken);

    /// <summary>配置中是否已设置端到端加密口令。</summary>
    public bool HasPassphrase(SyncSettings settings) =>
        !string.IsNullOrEmpty(settings.ProtectedPassphrase);

    /// <summary>标记本地发生用户改动(更新改动时间戳),供后续同步判定推送方向;应用远端数据期间的保存不计入。</summary>
    public async Task MarkLocalChangedAsync(CancellationToken cancellationToken = default)
    {
        // 应用远端数据期间触发的保存事件不是用户改动,忽略,否则拉取后会立刻无谓回推。
        if (IsApplyingRemote)
        {
            return;
        }
        SyncSettings settings = await GetSyncSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings.LastLocalChangeAtUtc = DateTime.UtcNow;
        await SaveSyncSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>执行智能双向同步:比对本地改动标记与远端 revision,按“较新者胜”决定推送或拉取。</summary>
    public async Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using Core.Services.IBackgroundActivityScope? activity = BeginSyncActivity("SetSync_SyncNow");
        try
        {
            SyncSettings config = await GetSyncSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Validate(config) is { } error)
            {
                return SyncResult.Fail(error);
            }
            string token = Token(config);

            // 尚未绑定 Gist:首次推送即创建。
            if (string.IsNullOrEmpty(config.GistId))
            {
                return await PushCoreAsync(config, token, cancellationToken).ConfigureAwait(false);
            }
            (string? content, string remoteVersion, string? remoteLayouts) =
                await ReadGistAsync(token, config.GistId, revision: null, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                return await PushCoreAsync(config, token, cancellationToken).ConfigureAwait(false);
            }
            bool localChanged =
                config.LastLocalChangeAtUtc is not null
                && (
                    config.LastSyncAtUtc is null
                    || config.LastLocalChangeAtUtc > config.LastSyncAtUtc
                );
            bool remoteChanged = !string.Equals(
                remoteVersion,
                config.LastRemoteVersion,
                StringComparison.Ordinal
            );
            if (!localChanged && !remoteChanged)
            {
                return await ReconcileLayoutTemplatesAsync(config, token, remoteLayouts,
                    new(SyncAction.UpToDate, true, Strings.Get("SyncSvc_UpToDate")), allowUpload: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (localChanged && !remoteChanged)
            {
                return await PushCoreAsync(config, token, cancellationToken).ConfigureAwait(false);
            }
            SyncEnvelope envelope = ParseEnvelope(content);
            if (!localChanged)
            {
                return await ReconcileLayoutTemplatesAsync(config, token, remoteLayouts,
                    await ApplyRemoteAsync(config, envelope, remoteVersion, cancellationToken).ConfigureAwait(false),
                    allowUpload: true, cancellationToken).ConfigureAwait(false);
            }

            // 双端都有改动:较新者胜(LWW);被覆盖的内容在 Gist 修订历史中可随时找回。
            if (envelope.UpdatedAtUtc >= config.LastLocalChangeAtUtc)
            {
                SyncResult pulled = await ApplyRemoteAsync(
                        config,
                        envelope,
                        remoteVersion,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                // 主载荷云端赢了,模板照样逐条合并:本机这一轮的模板改动不跟着输掉。
                pulled = await ReconcileLayoutTemplatesAsync(config, token, remoteLayouts, pulled, allowUpload: true, cancellationToken)
                    .ConfigureAwait(false);
                return pulled with
                {
                    Message = pulled.Message + Strings.Get("SyncSvc_BothChangedRemoteWins"),
                };
            }
            SyncResult pushed = await PushCoreAsync(config, token, cancellationToken)
                .ConfigureAwait(false);
            return pushed with
            {
                Message = pushed.Message + Strings.Get("SyncSvc_BothChangedLocalWins"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SyncResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>强制将本地数据打包推送到 Gist(尚未绑定时会创建新的 Gist)。</summary>
    public async Task<SyncResult> PushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using Core.Services.IBackgroundActivityScope? activity = BeginSyncActivity("SetSync_PushOnly");
        try
        {
            SyncSettings config = await GetSyncSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Validate(config) is { } error)
            {
                return SyncResult.Fail(error);
            }
            return await PushCoreAsync(config, Token(config), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SyncResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>强制从 Gist 拉取远端数据并应用到本地(合并式,不删除本地独有数据)。</summary>
    public async Task<SyncResult> PullAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using Core.Services.IBackgroundActivityScope? activity = BeginSyncActivity("SetSync_PullOnly");
        try
        {
            SyncSettings config = await GetSyncSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Validate(config) is { } error)
            {
                return SyncResult.Fail(error);
            }
            if (string.IsNullOrEmpty(config.GistId))
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_NoGistBound"));
            }
            string token = Token(config);
            (string? content, string version, string? layouts) =
                await ReadGistAsync(token, config.GistId, revision: null, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_NoRemoteFile"));
            }
            SyncResult applied = await ApplyRemoteAsync(
                    config,
                    ParseEnvelope(content),
                    version,
                    cancellationToken
                )
                .ConfigureAwait(false);
            // 「仅拉取」不推:本机多出来的模板改动留给下一次同步。
            return await ReconcileLayoutTemplatesAsync(config, token, layouts, applied, allowUpload: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SyncResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>获取 Gist 的历史版本列表,并尽力补全前若干个版本的提交来源设备名。</summary>
    public async Task<List<GistRevision>> GetRevisionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        SyncSettings config = await GetSyncSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (Validate(config) is not null || string.IsNullOrEmpty(config.GistId))
        {
            return [];
        }
        string token = Token(config);
        List<GistRevision> revisions = await GistApiClient
            .GetCommitsAsync(token, config.GistId, cancellationToken)
            .ConfigureAwait(false);

        // 提交来源设备不在 commits API 中,而在各版本载荷信封的顶层 deviceName
        // (无论是否端到端加密都可读)。逐版本读取有额外请求开销:限 4 并发、
        // 只补前 20 个版本;单个失败不影响列表(设备名留空)。
        var enriched = new GistRevision[revisions.Count];
        using var throttle = new SemaphoreSlim(4);
        await Task.WhenAll(
                revisions.Select(
                    async (revision, index) =>
                    {
                        string? device = null;
                        if (index < 20)
                        {
                            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                            try
                            {
                                (string? content, _) = await GistApiClient
                                    .GetFileAtRevisionAsync(
                                        token,
                                        config.GistId,
                                        revision.Version,
                                        GistFileName,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                if (content is not null)
                                {
                                    using var doc = JsonDocument.Parse(content);
                                    if (
                                        doc.RootElement.TryGetProperty(
                                            "deviceName",
                                            out JsonElement name
                                        )
                                    )
                                    {
                                        device = name.GetString();
                                    }
                                }
                            }
                            catch
                            {
                                // 元数据补全失败不影响版本列表本身。
                            }
                            finally
                            {
                                throttle.Release();
                            }
                        }
                        enriched[index] = revision with { DeviceName = device };
                    }
                )
            )
            .ConfigureAwait(false);
        return [.. enriched];
    }

    /// <summary>将指定历史版本的内容恢复为当前状态并重新推送,使版本链保持线性、任何一步都可再恢复。</summary>
    public async Task<SyncResult> RestoreRevisionAsync(
        string version,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using Core.Services.IBackgroundActivityScope? activity = BeginSyncActivity("SetSync_RestoreRevision");
        try
        {
            SyncSettings config = await GetSyncSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Validate(config) is { } error)
            {
                return SyncResult.Fail(error);
            }
            if (string.IsNullOrEmpty(config.GistId))
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_NoGistNoRestore"));
            }
            string token = Token(config);
            (string? content, _, string? layouts) =
                await ReadGistAsync(token, config.GistId, version, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_RevisionNoFile"));
            }
            SyncResult applied = await ApplyRemoteAsync(
                    config,
                    ParseEnvelope(content),
                    remoteVersion: null,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!applied.Success)
            {
                return applied;
            }
            // 那个版本里的布局模板一并找回(删过、改过的也回来),随下面这次推送写回云端。
            string? layoutsNote = await RestoreLayoutTemplatesAsync(config, layouts).ConfigureAwait(false);

            // 恢复即把该历史内容作为最新状态重新推送:版本链保持线性,任何一步都可再恢复。
            config = await GetSyncSettingsAsync(cancellationToken).ConfigureAwait(false);
            SyncResult pushed = await PushCoreAsync(config, token, cancellationToken)
                .ConfigureAwait(false);
            return pushed.Success
                ? new(
                    SyncAction.Pulled,
                    true,
                    Strings.Format(
                        "SyncSvc_RestoredAndPushed",
                        version[..Math.Min(7, version.Length)]
                    ) + layoutsNote
                )
                : pushed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SyncResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ———— 推送 ————

    private async Task<SyncResult> PushCoreAsync(
        SyncSettings config,
        string token,
        CancellationToken cancellationToken
    )
    {
        string content = await BuildGistContentAsync(config, cancellationToken)
            .ConfigureAwait(false);
        // 主载荷加上(合并过云端那份的)布局模板文件,同一次提交写上去。
        Dictionary<string, string> files = await BuildUploadFilesAsync(config, token, content, cancellationToken)
            .ConfigureAwait(false);
        string version;
        if (string.IsNullOrEmpty(config.GistId))
        {
            (config.GistId, version) = await GistApiClient
                .CreateGistAsync(token, GistDescription, files, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            version = await GistApiClient
                .UpdateGistAsync(token, config.GistId, files, cancellationToken)
                .ConfigureAwait(false);
        }
        config.LastSyncAtUtc = DateTime.UtcNow;
        config.LastRemoteVersion = version;
        config.LastLocalChangeAtUtc = null;
        await SaveSyncSettingsAsync(config, cancellationToken).ConfigureAwait(false);
        return new(SyncAction.Pushed, true, Strings.Format("SyncSvc_Pushed", Short(version)));
    }

    internal async Task<string> BuildGistContentAsync(
        SyncSettings config,
        CancellationToken cancellationToken
    )
    {
        bool encrypted = HasPassphrase(config);
        var payload = new SyncPayload
        {
            UpdatedAtUtc = DateTime.UtcNow,
            DeviceName = config.DeviceName,
        };
        if (config.SyncAppSettings)
        {
            AppSettings settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
            payload.Settings = WithoutSecretsUnlessEncrypted(ScrubDeviceLocalFields(settings), encrypted);
        }
        if (config.SyncProfiles)
        {
            payload.Groups = await sessionRepository.GetAllGroupsAsync().ConfigureAwait(false);
            List<SessionProfile> profiles = await sessionRepository
                .GetAllSessionsAsync()
                .ConfigureAwait(false);
            if (!encrypted)
            {
                // 未启用端到端口令:凭据绝不明文上云(secret Gist 仅是“不被检索”,知道链接即可读)。
                foreach (SessionProfile profile in profiles)
                {
                    profile.Password = null;
                    profile.PrivateKeyPassphrase = null;
                    // 插件协议的机密字段(S3 的会话令牌之类)同一条规矩;拉取时同一协议下留本机的。
                    profile.PluginSecrets = null;
                }
            }
            else
            {
                foreach (SessionProfile profile in profiles)
                {
                    DropUnreadableSecrets(profile);
                }
            }
            payload.Profiles = profiles;

            // 共享凭据随连接同步(#550),剥离规则与连接配置一致。
            if (sharedCredentials is not null)
            {
                List<SharedCredential> credentials = await sharedCredentials.GetAllAsync().ConfigureAwait(false);
                if (!encrypted)
                {
                    foreach (SharedCredential credential in credentials)
                    {
                        credential.Password = null;
                        credential.PrivateKeyPassphrase = null;
                    }
                }
                else
                {
                    foreach (SharedCredential credential in credentials)
                    {
                        credential.Password = Readable(credential.Password);
                        credential.PrivateKeyPassphrase = Readable(credential.PrivateKeyPassphrase);
                    }
                }
                payload.SharedCredentials = credentials.Count > 0 ? credentials : null;
            }

            // 端口转发隧道配置随连接同步(tunnels 集合按 profileId 分文档)。
            var tunnels = new Dictionary<Guid, List<TunnelConfig>>();
            foreach (SessionProfile profile in profiles)
            {
                List<TunnelConfig>? configs = await appDataStore
                    .GetAsync<List<TunnelConfig>>(
                        TunnelsCollection,
                        profile.Id.ToString("D"),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (configs is { Count: > 0 })
                {
                    tunnels[profile.Id] = configs;
                }
            }
            payload.Tunnels = tunnels.Count > 0 ? tunnels : null;
        }
        if (config.SyncSnippets)
        {
            payload.Snippets = await quickCommandRepository
                .ExportSyncAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        payload.SchemaVersion = PayloadSchemaVersion(payload);
        var envelope = new SyncEnvelope
        {
            SchemaVersion = payload.SchemaVersion,
            UpdatedAtUtc = payload.UpdatedAtUtc,
            DeviceName = config.DeviceName,
            Encrypted = encrypted,
        };
        if (encrypted)
        {
            envelope.CipherText = SyncCrypto.Encrypt(
                JsonSerializer.Serialize(payload, JsonOptions),
                Passphrase(config)!
            );
        }
        else
        {
            envelope.Payload = payload;
        }
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    /// <summary>
    /// 这份载荷要标的版本:用到共享凭据(有凭据,或有连接引用凭据)时为 3,否则为 2。
    /// 理由见 <see cref="CurrentSchemaVersion" />。
    /// </summary>
    internal static int PayloadSchemaVersion(SyncPayload payload) =>
        payload.SharedCredentials is { Count: > 0 }
        || payload.Profiles?.Exists(static p => p.CredentialSource is not null) == true
            ? 3
            : 2;

    // ———— 拉取与应用 ————

    internal async Task<SyncResult> ApplyRemoteAsync(
        SyncSettings config,
        SyncEnvelope envelope,
        string? remoteVersion,
        CancellationToken cancellationToken
    )
    {
        if (envelope.SchemaVersion > CurrentSchemaVersion)
        {
            return SyncResult.Fail(
                $"Remote sync version {envelope.SchemaVersion} is newer than supported version {CurrentSchemaVersion}."
            );
        }
        SyncPayload payload;
        if (envelope.Encrypted)
        {
            string? passphrase = Passphrase(config);
            if (string.IsNullOrEmpty(passphrase))
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_NeedPassphrase"));
            }
            try
            {
                payload =
                    JsonSerializer.Deserialize<SyncPayload>(
                        SyncCrypto.Decrypt(envelope.CipherText ?? "", passphrase),
                        JsonOptions
                    ) ?? throw new InvalidOperationException(Strings.Get("SyncSvc_EmptyPayload"));
            }
            catch (CryptographicException)
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_WrongPassphrase"));
            }
        }
        else
        {
            payload =
                envelope.Payload
                ?? throw new InvalidOperationException(Strings.Get("SyncSvc_MissingPayload"));
        }
        if (payload.SchemaVersion > CurrentSchemaVersion)
        {
            return SyncResult.Fail(
                $"Remote sync payload version {payload.SchemaVersion} is newer than supported version {CurrentSchemaVersion}."
            );
        }

        // 这次拉到的设置要提示「Webhook 告警开着却没有地址」时,那份设置的指纹;不用提示时为 null。
        string? webhookNotice = null;
        IsApplyingRemote = true;
        try
        {
            // 这一段落的盘在审计里记成「云同步」(含恢复历史版本)。用来源作用域而不是 IsApplyingRemote:
            // 同步在后台跑的同时用户手改的那一条不在这条异步流程里,仍记成本机编辑。
            // 只罩住应用载荷这一段 —— 之后的 ProfilesApplied 通知里界面做的事不算同步带来的。
            using IDisposable source = AuditSourceScope.Enter(AuditSources.CloudSync);
            if (config.SyncAppSettings && payload.Settings is not null)
            {
                // 设备本地字段(窗口尺寸/开机自启/本机路径等)保留本机现值,其余采用云端。
                AppSettings local = await settingsService.GetSettingsAsync().ConfigureAwait(false);
                AppSettings incoming = payload.Settings;
                // 指纹取对方推上来的原样(保留本机字段之前):同一份设置不论拉几次都是同一个。
                string fingerprint = SettingsFingerprint(incoming);
                PreserveDeviceLocalFields(incoming, local);
                incoming.Normalize();
                await settingsService.SaveSettingsAsync(incoming).ConfigureAwait(false);
                if (WebhookUrlMissingAfterPull(incoming) && config.WebhookUrlMissingNotice != fingerprint)
                {
                    config.WebhookUrlMissingNotice = fingerprint;
                    webhookNotice = fingerprint;
                }
            }
            if (config.SyncProfiles)
            {
                // upsert 合并:云端条目按 Id 覆盖/新增,本地独有的连接不删除(防误删)。
                foreach (ServerGroup group in payload.Groups ?? [])
                {
                    await sessionRepository.SaveGroupAsync(group).ConfigureAwait(false);
                }
                // 共享凭据先于连接落库:连接里的引用指向它们。合并口径与连接相同 ——
                // upsert 不删本机独有的;非加密载荷不带机密,保留本机已存的那一份。
                if (sharedCredentials is not null && payload.SharedCredentials is { } incomingCredentials)
                {
                    var localCredentials = (
                        await sharedCredentials.GetAllAsync().ConfigureAwait(false)
                    ).ToDictionary(c => c.Id);
                    foreach (SharedCredential incoming in incomingCredentials)
                    {
                        // 没带的(含推送方解不开、原样推上来的密文)逐项留本机的,口径同下面的连接配置。
                        localCredentials.TryGetValue(incoming.Id, out SharedCredential? existing);
                        incoming.Password = Readable(incoming.Password) ?? existing?.Password;
                        incoming.PrivateKeyPassphrase = Readable(incoming.PrivateKeyPassphrase) ?? existing?.PrivateKeyPassphrase;
                        await sharedCredentials.SaveAsync(incoming).ConfigureAwait(false);
                    }
                }
                if (payload.Profiles is { } profiles)
                {
                    var localProfiles = (
                        await sessionRepository.GetAllSessionsAsync().ConfigureAwait(false)
                    ).ToDictionary(p => p.Id);
                    foreach (SessionProfile incoming in profiles)
                    {
                        // 非加密载荷不带凭据:保留本机已存的密码/私钥口令,避免拉取把凭据抹掉。
                        localProfiles.TryGetValue(incoming.Id, out SessionProfile? existing);
                        KeepLocalSecretsNotCarried(incoming, existing);
                        if (incoming.DirectoryBookmarks is null)
                        {
                            // 载荷里压根没有收藏字段:推它的是不认识收藏的旧版本,不是对方清空了收藏。
                            // 整条覆盖会把本机的收藏一并抹掉,所以走 SaveSessionAsync —— 它在仓储锁内保留库里现存的收藏
                            // (本机没有这条配置时就是空列表)。
                            incoming.DirectoryBookmarks = [];
                            await sessionRepository.SaveSessionAsync(incoming).ConfigureAwait(false);
                        }
                        else
                        {
                            // 字段在(哪怕是空数组):整条覆盖,远端那份的目录收藏也要带过来(SaveSessionAsync 会保留本机的收藏)。
                            await sessionRepository.ReplaceSessionAsync(incoming).ConfigureAwait(false);
                        }
                    }
                }

                // 隧道配置:按 profileId 整文档覆盖(文档本身即“该服务器的隧道列表”)。
                foreach ((Guid profileId, List<TunnelConfig> configs) in payload.Tunnels ?? [])
                {
                    await appDataStore
                        .UpsertAsync(
                            TunnelsCollection,
                            profileId.ToString("D"),
                            configs,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
            }
            if (config.SyncSnippets && payload.Snippets is not null)
            {
                await quickCommandRepository
                    .ApplySyncAsync(payload.Snippets, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            IsApplyingRemote = false;
        }
        config.LastSyncAtUtc = DateTime.UtcNow;
        if (remoteVersion is not null)
        {
            config.LastRemoteVersion = remoteVersion;
            config.LastLocalChangeAtUtc = null;
        }
        await SaveSyncSettingsAsync(config, cancellationToken).ConfigureAwait(false);
        if (webhookNotice is not null)
        {
            // 记下「提示过」之后再通知:同步配置没存进去,下次拉到同一份还会再提示,而不是这次提示了、记录却丢了。
            WebhookUrlMissing?.Invoke(this, webhookNotice);
        }
        if (config.SyncProfiles)
        {
            // 仓储已经完整应用远端分组/Profile/隧道后再通知界面。同步通常在后台线程执行,
            // 订阅方负责封送到自己的 UI 线程。历史版本恢复也复用本路径,因此无需另行接线。
            ProfilesApplied?.Invoke(this, EventArgs.Empty);
        }
        return new(
            SyncAction.Pulled,
            true,
            Strings.Format(
                "SyncSvc_Applied",
                payload.DeviceName,
                payload.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            )
        );
    }

    // ———— 设备本地字段(不参与同步) ————

    /// <summary>推送前清空设备本地字段:本机路径含用户名等隐私,窗口状态跨设备无意义。</summary>
    private static AppSettings ScrubDeviceLocalFields(AppSettings settings)
    {
        var defaults = new AppSettings();
        settings.General.LaunchAtStartup = defaults.General.LaunchAtStartup;
        settings.General.LastOpenProfileIds = [];
        settings.Appearance.LastWindowWidth = defaults.Appearance.LastWindowWidth;
        settings.Appearance.LastWindowHeight = defaults.Appearance.LastWindowHeight;
        settings.Appearance.LastWindowMaximized = defaults.Appearance.LastWindowMaximized;
        settings.Appearance.LastWindowX = defaults.Appearance.LastWindowX;
        settings.Appearance.LastWindowY = defaults.Appearance.LastWindowY;
        // 硬件加速取决于这台机器的显卡与内存,跨设备同步只会把弱机的将就设置推给强机(反之亦然)。
        settings.Appearance.HardwareAcceleration = defaults.Appearance.HardwareAcceleration;
        settings.Transfer.LocalDownloadDirectory = defaults.Transfer.LocalDownloadDirectory;
        settings.Transfer.DefaultEditorPath = defaults.Transfer.DefaultEditorPath;
        settings.Transfer.LogDirectory = defaults.Transfer.LogDirectory;
        settings.Keys.DefaultKeyName = defaults.Keys.DefaultKeyName;
        return settings;
    }

    /// <summary>拉取应用时同样保留本机的设备本地字段,云端值不覆盖;对方没带的机密也留本机的(<see cref="PreserveLocalSecrets" />)。</summary>
    private static void PreserveDeviceLocalFields(AppSettings incoming, AppSettings local)
    {
        PreserveLocalSecrets(incoming, local);
        incoming.General.LaunchAtStartup = local.General.LaunchAtStartup;
        incoming.General.LastOpenProfileIds = local.General.LastOpenProfileIds;
        incoming.Appearance.LastWindowWidth = local.Appearance.LastWindowWidth;
        incoming.Appearance.LastWindowHeight = local.Appearance.LastWindowHeight;
        incoming.Appearance.LastWindowMaximized = local.Appearance.LastWindowMaximized;
        incoming.Appearance.LastWindowX = local.Appearance.LastWindowX;
        incoming.Appearance.LastWindowY = local.Appearance.LastWindowY;
        incoming.Appearance.HardwareAcceleration = local.Appearance.HardwareAcceleration;
        incoming.Transfer.LocalDownloadDirectory = local.Transfer.LocalDownloadDirectory;
        incoming.Transfer.DefaultEditorPath = local.Transfer.DefaultEditorPath;
        incoming.Transfer.LogDirectory = local.Transfer.LogDirectory;
        incoming.Keys.DefaultKeyName = local.Keys.DefaultKeyName;
    }

    // ———— 设置里的机密(代理密码、告警 Webhook 地址) ————

    /// <summary>
    /// 没设端到端口令时,推上去的设置去掉机密(名单是 <see cref="SettingsCategories.SecretFields" />,与设置导出同一份):
    /// 与连接配置的密码同一条规矩 —— secret Gist 只是不被检索,知道链接就能读,而 Webhook 地址本身就是令牌。
    /// </summary>
    /// <remarks>
    /// 设了口令照带:载荷在本机整份 AES-GCM 加密,GitHub 只拿到密文,连接配置的密码这时也随同步走;
    /// 带机密换机本来就指着这条路(手册「推荐换机步骤」、<c>SettingsCategories.SecretFields</c> 的说明),
    /// 加密了还剔掉,只是让人换台机器又得补填一遍。
    /// </remarks>
    private static AppSettings WithoutSecretsUnlessEncrypted(AppSettings settings, bool encrypted) =>
        encrypted ? settings : SettingsTransfer.WithoutSecrets(settings);

    /// <summary>
    /// 应用之后(本机的地址已经按 <see cref="PreserveLocalSecrets" /> 保留过)Webhook 告警开着、地址却是空的:
    /// 告警发不出去,而云同步是静默的,不说一声用户不会知道。口径与设置导入的「需要重填 Webhook 地址」相同。
    /// </summary>
    private static bool WebhookUrlMissingAfterPull(AppSettings applied) =>
        applied.Security.AlertWebhook && string.IsNullOrWhiteSpace(applied.Security.WebhookUrl);

    /// <summary>拉到的那份设置的指纹(序列化后 SHA-256 的前 8 字节,十六进制):只用来认「是不是同一份设置」,记下的是它而不是设置本身。</summary>
    private static string SettingsFingerprint(AppSettings settings) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions)).AsSpan(0, 8));

    /// <summary>
    /// 拉到的设置没带机密(无口令的载荷,或对方本来就没填)时保留本机的,口径与设置导入相同:
    /// 代理的类型、主机、端口、用户名都与本机相同才留本机密码,Webhook 地址一律留本机的。
    /// 不论载荷加没加密都这样判 —— 与连接配置「载荷里密码为空就留本机的」一致。
    /// </summary>
    private static void PreserveLocalSecrets(AppSettings incoming, AppSettings local) =>
        SettingsTransfer.KeepLocalSecrets(incoming, local);

    // ———— 连接配置与共享凭据里的机密(这台设备解不开的那几项) ————

    /// <summary>
    /// 一项机密能不能随同步走:载入之后仍是密文的(<see cref="StoredSecret" />:本机加密密钥换过、系统密钥链这次没打开、
    /// 数据目录从别的设备拷来)当作「没带」,交回 null。
    /// </summary>
    /// <remarks>
    /// 推送时:仓储把解不开的密文原样交回,照带上去,别的设备就会把这串它也解不开的密文存成密码,顶掉它自己那份好的 ——
    /// 与同步口令、代理密码「解不开就不当值用」同一条规矩。拉取时同样认这个:载荷里还带着密文前缀的,
    /// 只可能是推送方解不开、原样推上来的(解得开的一律以明文进加密载荷),旧版本推过这种载荷。
    /// </remarks>
    private static string? Readable(string? secret) => StoredSecret.IsUnreadable(secret) ? null : secret;

    /// <summary>加密载荷推送前:这条配置里这台设备解不开的机密不带(置空 / 移出插件机密字典),对方留它自己的。</summary>
    private static void DropUnreadableSecrets(SessionProfile profile)
    {
        profile.Password = Readable(profile.Password);
        profile.PrivateKeyPassphrase = Readable(profile.PrivateKeyPassphrase);
        if (profile.PluginSecrets is { } secrets)
        {
            foreach (string key in UnreadableKeys(secrets))
            {
                secrets.Remove(key);
            }
        }
    }

    /// <summary>
    /// 拉取时:载荷没带的机密(未加密载荷剥掉的、推送方解不开而没带或原样推上来的密文)逐项留本机的,而不是把本机的抹掉。
    /// </summary>
    /// <remarks>
    /// 插件机密只在两边是同一个插件协议时才留:插件机密字典整本交给插件(<c>PluginWorkspaceLauncher.BuildSettings</c>),
    /// 换了协议还留着,等于把上一个插件的密钥交给下一个插件。同一协议下字典里缺的键就是没带 ——
    /// 编辑配置时表单以已存的那份为底(<c>CollectPluginValues</c>),清空的字段是空串,不会缺键。
    /// </remarks>
    private static void KeepLocalSecretsNotCarried(SessionProfile incoming, SessionProfile? local)
    {
        incoming.Password = Readable(incoming.Password) ?? local?.Password;
        incoming.PrivateKeyPassphrase = Readable(incoming.PrivateKeyPassphrase) ?? local?.PrivateKeyPassphrase;
        bool sameProtocol = local is not null
                            && string.Equals(local.PluginProtocolId, incoming.PluginProtocolId, StringComparison.Ordinal);
        if (incoming.PluginSecrets is not { } secrets)
        {
            // 整本没带(未加密载荷):同一协议下整本留本机的。
            if (sameProtocol)
            {
                incoming.PluginSecrets = local!.PluginSecrets;
            }
            return;
        }
        foreach (string key in UnreadableKeys(secrets))
        {
            secrets.Remove(key);
        }
        if (sameProtocol && local!.PluginSecrets is { } kept)
        {
            foreach ((string key, string value) in kept)
            {
                secrets.TryAdd(key, value);
            }
        }
    }

    private static string[] UnreadableKeys(Dictionary<string, string> secrets) =>
        [.. secrets.Where(static entry => StoredSecret.IsUnreadable(entry.Value)).Select(static entry => entry.Key)];

    // ———— 小工具 ————

    private string? Validate(SyncSettings config)
    {
        if (!config.Enabled)
        {
            return Strings.Get("SyncSvc_NotEnabled");
        }
        return HasToken(config) ? null : Strings.Get("SyncSvc_NoToken");
    }

    // 解不开(本机加密密钥这次没取到、数据目录从别的电脑拷来)时必须停下,不能把密文本身当令牌 / 口令用:
    // 拿密文当端到端口令去加密上传,远端那份就成了别的设备谁也解不开的东西。
    private string Token(SyncSettings config) =>
        secretProtector.TryUnprotect(config.ProtectedToken, out string? token) && token is not null
            ? token
            : throw new InvalidOperationException(Strings.Get("SyncSvc_TokenDecryptFailed"));

    private string? Passphrase(SyncSettings config) =>
        string.IsNullOrEmpty(config.ProtectedPassphrase)
            ? null
            : secretProtector.TryUnprotect(config.ProtectedPassphrase, out string? passphrase)
                ? passphrase
                : throw new InvalidOperationException(Strings.Get("SyncSvc_PassphraseDecryptFailed"));

    internal static SyncEnvelope ParseEnvelope(string content) =>
        JsonSerializer.Deserialize<SyncEnvelope>(content, JsonOptions)
        ?? throw new InvalidOperationException(Strings.Get("SyncSvc_BadFormat"));

    private static string Short(string version) => version.Length <= 7 ? version : version[..7];
}
