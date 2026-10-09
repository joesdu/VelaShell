using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VelaShell.Core.Credentials;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sync;

namespace VelaShell.Core.Import;

/// <summary>
/// VelaShell JSON 连接文件的读写(#571):写出时剥掉机密(或用导出口令整体加密),读入时逐条解析、不因一条坏数据整份作废。
/// </summary>
public static class SessionArchiveJson
{
    /// <summary>
    /// 文件的 JSON 约定:camelCase、缩进、枚举写成名字(读时数字也认)、中文原样不转义。
    /// </summary>
    /// <remarks>
    /// 与库里落盘的约定(<c>SonnetDbJson</c>)只差在「枚举写名字 + 缩进 + 不转义中文」三处 ——
    /// 这份文件是给人看、给人改的,<c>"authMethod": 1</c> 没人看得懂它是私钥。
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 返回一条连接可以写进文件的样子:去掉机密(密码、私钥口令、插件机密)与本机状态(上次连接时间)。
    /// </summary>
    /// <remarks>引用了共享凭据的连接,认证材料本来就不该在它身上(仓储层的规矩),这里再清一遍。</remarks>
    /// <param name="profile">本机的连接(不会被修改)。</param>
    /// <returns>剥离后的副本。</returns>
    public static SessionProfile StripForExport(SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        SessionProfile copy = profile.Clone();
        if (copy.CredentialSource is not null)
        {
            CredentialMaterial.ClearInline(copy);
        }
        copy.Password = null;
        copy.PrivateKeyPassphrase = null;
        copy.PluginSecrets = null;
        copy.LastConnectedAt = null;
        return copy;
    }

    /// <summary>这份导出里有几条连接 / 共享凭据带着机密(用来告诉用户「勾上敏感信息」会多导出什么)。</summary>
    /// <param name="archive">尚未剥离机密的导出内容。</param>
    /// <returns>带机密的条目数。</returns>
    public static int CountSecrets(SessionArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        int sessions = archive.Sessions.Count(static s => !SecretsOf(s).IsEmpty);
        int credentials = archive.SharedCredentials?.Count(static c => !SecretsOf(c).IsEmpty) ?? 0;
        return sessions + credentials;
    }

    /// <summary>
    /// 写出导出文件。<paramref name="passphrase" /> 为空时一项机密都不写;非空时把全部机密用它加密成 <c>secrets</c> 段。
    /// </summary>
    /// <param name="archive">导出内容(连接上可以带着明文机密,这里会剥掉,不修改传入的对象)。</param>
    /// <param name="passphrase">导出口令;null / 空串 = 不导出机密。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(SessionArchive archive, string? passphrase)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var payload = new SessionArchiveSecretPayload();
        var output = new SessionArchive
        {
            Format = SessionArchive.FormatId,
            Version = SessionArchive.CurrentVersion,
            ExportedAtUtc = archive.ExportedAtUtc,
            Application = archive.Application,
            Groups = [.. archive.Groups],
            Tunnels = archive.Tunnels is { Count: > 0 } tunnels ? new(tunnels) : null
        };
        foreach (SessionProfile session in archive.Sessions)
        {
            SessionSecretValues secrets = SecretsOf(session);
            if (!secrets.IsEmpty)
            {
                (payload.Sessions ??= [])[session.Id] = secrets;
            }
            output.Sessions.Add(StripForExport(session));
        }
        if (archive.SharedCredentials is { Count: > 0 } credentials)
        {
            output.SharedCredentials = [];
            foreach (SharedCredential credential in credentials)
            {
                SessionSecretValues secrets = SecretsOf(credential);
                if (!secrets.IsEmpty)
                {
                    (payload.SharedCredentials ??= [])[credential.Id] = secrets;
                }
                SharedCredential copy = credential.Clone();
                copy.Password = null;
                copy.PrivateKeyPassphrase = null;
                output.SharedCredentials.Add(copy);
            }
        }
        if (!string.IsNullOrEmpty(passphrase) && (payload.Sessions is not null || payload.SharedCredentials is not null))
        {
            output.Secrets = new SessionArchiveSecrets
            {
                Data = SyncCrypto.Encrypt(JsonSerializer.Serialize(payload, Options), passphrase)
            };
        }
        return JsonSerializer.Serialize(output, Options);
    }

    /// <summary>
    /// 读入一份 JSON 连接文件。整份文件不是 VelaShell 的格式、版本比本程序新或 JSON 本身坏掉时抛
    /// <see cref="SessionFileFormatException" />;单条连接有问题只记在那一条的 <see cref="SessionImportCandidate.Errors" /> 里。
    /// </summary>
    /// <param name="fileName">文件名(只用于显示)。</param>
    /// <param name="json">文件内容。</param>
    /// <returns>解析结果;加密的机密尚未解开(见 <see cref="TryUnlock" />)。</returns>
    public static SessionImportDocument Read(string fileName, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            throw new SessionFileFormatException(Strings.Format("SessFile_JsonInvalid", ex.Message), ex);
        }
        using (parsed)
        {
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SessionFileFormatException(Strings.Get("SessFile_NotVelaShell"));
            }
            bool hasFormat = TryGetProperty(root, "format", out JsonElement format);
            bool hasSessions = TryGetProperty(root, "sessions", out JsonElement sessions) && sessions.ValueKind == JsonValueKind.Array;
            // 没写 format 但有 sessions 数组的也收:手写的文件不必背下这个标识。写了别的值则一定不是我们的文件。
            if ((hasFormat && !string.Equals(format.ValueKind == JsonValueKind.String ? format.GetString() : null,
                    SessionArchive.FormatId, StringComparison.Ordinal))
                || !hasSessions)
            {
                throw new SessionFileFormatException(Strings.Get("SessFile_NotVelaShell"));
            }
            if (TryGetProperty(root, "version", out JsonElement versionElement)
                && versionElement.ValueKind == JsonValueKind.Number
                && versionElement.TryGetInt32(out int version)
                && version > SessionArchive.CurrentVersion)
            {
                throw new SessionFileFormatException(Strings.Format("SessFile_VersionTooNew", version));
            }

            // 数组里的 null 元素、名称写成 null 的(手写文件)一并兜住,理由同 ReadSession。
            List<SessionArchiveGroup> groups = [.. (ReadSection<List<SessionArchiveGroup?>>(root, "groups") ?? []).OfType<SessionArchiveGroup>()];
            foreach (SessionArchiveGroup group in groups)
            {
                group.Name ??= string.Empty;
            }
            List<SharedCredential> credentials = [.. (ReadSection<List<SharedCredential?>>(root, "sharedCredentials") ?? []).OfType<SharedCredential>()];
            foreach (SharedCredential credential in credentials)
            {
                credential.Name ??= string.Empty;
                credential.Username ??= string.Empty;
            }
            Dictionary<Guid, List<TunnelConfig>> tunnels = ReadSection<Dictionary<Guid, List<TunnelConfig>>>(root, "tunnels") ?? [];
            SessionArchiveSecrets? secrets = ReadSection<SessionArchiveSecrets>(root, "secrets");
            DateTime? exportedAt = TryGetProperty(root, "exportedAtUtc", out JsonElement exportedElement)
                                   && exportedElement.ValueKind == JsonValueKind.String
                                   && exportedElement.TryGetDateTime(out DateTime stamp)
                ? stamp
                : null;

            var groupNames = new Dictionary<Guid, string>();
            foreach (SessionArchiveGroup group in groups)
            {
                _ = groupNames.TryAdd(group.Id, group.Name);
            }

            List<SessionImportCandidate> candidates = [];
            foreach (JsonElement element in sessions.EnumerateArray())
            {
                candidates.Add(ReadSession(candidates.Count, element, groupNames));
            }

            var document = new SessionImportDocument
            {
                FileName = fileName,
                Format = SessionFileFormat.Json,
                Candidates = candidates,
                Groups = groups,
                SharedCredentials = credentials,
                Tunnels = tunnels,
                EncryptedSecrets = secrets is { Data.Length: > 0 } && secrets.Algorithm == SessionArchiveSecrets.CurrentAlgorithm
                    ? secrets
                    : null,
                ExportedAtUtc = exportedAt
            };
            if (secrets is { Data.Length: > 0 } && document.EncryptedSecrets is null)
            {
                // 将来换了算法、这一版却拿到了那样的文件:机密解不开,连接照样能导,只是要补密码。
                document.Warnings.Add(Strings.Get("SessFile_UnknownSecretAlgorithm"));
            }
            return document;
        }
    }

    /// <summary>
    /// 用口令解开文件里的机密,并填回各条连接与共享凭据。口令不对、数据损坏都返回 false,文件内容不变。
    /// </summary>
    /// <param name="document">由 <see cref="Read" /> 读出的文件。</param>
    /// <param name="passphrase">导出时设的口令。</param>
    /// <returns>解开了为 true。</returns>
    public static bool TryUnlock(SessionImportDocument document, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.EncryptedSecrets is not { } secrets || string.IsNullOrEmpty(passphrase))
        {
            return false;
        }
        SessionArchiveSecretPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SessionArchiveSecretPayload>(SyncCrypto.Decrypt(secrets.Data, passphrase), Options);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return false;
        }
        if (payload is null)
        {
            return false;
        }
        foreach (SessionImportCandidate candidate in document.Candidates)
        {
            if (candidate.HasFileId
                && payload.Sessions is { } sessionSecrets
                && sessionSecrets.TryGetValue(candidate.Profile.Id, out SessionSecretValues? values))
            {
                candidate.Profile.Password = NullIfEmpty(values.Password) ?? candidate.Profile.Password;
                candidate.Profile.PrivateKeyPassphrase = NullIfEmpty(values.PrivateKeyPassphrase) ?? candidate.Profile.PrivateKeyPassphrase;
                if (values.PluginSecrets is { Count: > 0 } pluginSecrets)
                {
                    candidate.Profile.PluginSecrets = SessionProfile.CloneSettings(pluginSecrets);
                }
            }
        }
        foreach (SharedCredential credential in document.SharedCredentials)
        {
            if (payload.SharedCredentials is { } credentialSecrets
                && credentialSecrets.TryGetValue(credential.Id, out SessionSecretValues? values))
            {
                credential.Password = NullIfEmpty(values.Password) ?? credential.Password;
                credential.PrivateKeyPassphrase = NullIfEmpty(values.PrivateKeyPassphrase) ?? credential.PrivateKeyPassphrase;
            }
        }
        document.SecretsUnlocked = true;
        return true;
    }

    private static SessionImportCandidate ReadSession(int index, JsonElement element, Dictionary<Guid, string> groupNames)
    {
        bool hasId = TryGetProperty(element, "id", out JsonElement idElement)
                     && idElement.ValueKind == JsonValueKind.String
                     && Guid.TryParse(idElement.GetString(), out _);
        SessionProfile? profile = null;
        string? error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = Strings.Get("SessImport_ErrNotAnObject");
        }
        else
        {
            try
            {
                profile = element.Deserialize<SessionProfile>(Options);
            }
            catch (JsonException ex)
            {
                error = Strings.Format("SessImport_ErrBadEntry", ex.Message);
            }
        }
        profile ??= new SessionProfile
        {
            Name = TryGetProperty(element, "name", out JsonElement name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? string.Empty
                : string.Empty
        };

        Guid? fileGroupId = profile.GroupId;
        profile.GroupId = null;
        // 上次连接时间是导出方那台机器上的事,搬过来没有意义;导出时本就不写,手写的文件里有也不认。
        profile.LastConnectedAt = null;
        // 手写的文件里可能有 "host": null 这种写法:反序列化会把不可空的字符串 / 列表设成 null,
        // 后面一碰就是空引用,整份文件跟着读不了。在这里一律兜成空值,让它落到「主机为空」这条行级错误上。
        profile.Host = (profile.Host ?? string.Empty).Trim();
        profile.Username = (profile.Username ?? string.Empty).Trim();
        profile.Name ??= string.Empty;
        profile.Tags ??= [];
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            profile.Name = profile.Host;
        }

        var candidate = new SessionImportCandidate
        {
            Index = index,
            Profile = profile,
            HasFileId = hasId,
            FileGroupId = fileGroupId,
            GroupName = fileGroupId is { } groupId && groupNames.TryGetValue(groupId, out string? groupName) ? groupName : null,
            Columns = SessionCsvColumns.All
        };
        if (error is not null)
        {
            candidate.Errors.Add(error);
            return candidate;
        }
        if (profile.Host.Length == 0)
        {
            candidate.Errors.Add(Strings.Get("SessImport_ErrNoHost"));
        }
        if (profile.Port is < 1 or > 65535)
        {
            candidate.Errors.Add(Strings.Format("SessImport_ErrBadPort", profile.Port));
        }
        return candidate;
    }

    private static T? ReadSection<T>(JsonElement root, string name) where T : class
    {
        if (!TryGetProperty(root, name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        try
        {
            return element.Deserialize<T>(Options);
        }
        catch (JsonException ex)
        {
            throw new SessionFileFormatException(Strings.Format("SessFile_SectionInvalid", name, ex.Message), ex);
        }
    }

    /// <summary>不区分大小写地取一个属性(与反序列化时的 <c>PropertyNameCaseInsensitive</c> 同一口径)。</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static SessionSecretValues SecretsOf(SessionProfile profile) =>
        new()
        {
            Password = NullIfEmpty(profile.Password),
            PrivateKeyPassphrase = NullIfEmpty(profile.PrivateKeyPassphrase),
            PluginSecrets = profile.PluginSecrets is { Count: > 0 } secrets ? SessionProfile.CloneSettings(secrets) : null
        };

    private static SessionSecretValues SecretsOf(SharedCredential credential) =>
        new()
        {
            Password = NullIfEmpty(credential.Password),
            PrivateKeyPassphrase = NullIfEmpty(credential.PrivateKeyPassphrase)
        };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
