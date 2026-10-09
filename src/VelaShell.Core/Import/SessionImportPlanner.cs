using VelaShell.Core.Credentials;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Import;

/// <summary>
/// 连接文件导入的规则(#571):与本机数据比对出重复,再按用户的选择算出要落盘的全部东西。纯函数,不碰仓储。
/// </summary>
/// <remarks>
/// <para>
/// <b>重复的判据</b>:先看 Id(同一条连接:本机导出的备份、改过再导回来的 CSV),
/// 再看「协议 + 主机 + 端口 + 用户名」(同一台机器上的同一个账号)。后者撞上多条时优先挑同名的那条。
/// 文件内部的重复不管 —— 那是用户自己写进去的。
/// </para>
/// <para>
/// <b>Id 尽量沿用文件里的</b>:不与本机冲突就用文件的 Id,这样在两台开着云同步的电脑之间导来导去,
/// 同步看到的仍是同一条连接,而不是一份副本。冲突了(「两条都留」、文件里自己重复)才换新 Id,
/// 并把文件内部指向它的引用(跳板)一起改过去。
/// </para>
/// <para>
/// <b>引用跟着重复走</b>:文件里的堡垒机与本机已有的那台重复而被跳过时,文件里经它跳的机器改指本机那一台 ——
/// 只导出一组机器时导出会自动带上它们的跳板,到了另一台电脑上那台跳板多半已经在了,这正是最常见的情形。
/// </para>
/// </remarks>
public static class SessionImportPlanner
{
    /// <summary>「同一台机器上的同一个账号」的比对键:协议、插件协议 id、主机(不区分大小写)、端口、用户名。</summary>
    /// <param name="profile">连接。</param>
    /// <returns>比对键。</returns>
    public static string EndpointKey(SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.Join('|',
            (int)profile.ConnectionType,
            profile.PluginProtocolId ?? string.Empty,
            profile.Host.Trim().ToLowerInvariant(),
            profile.Port,
            profile.Username.Trim());
    }

    /// <summary>把文件与本机数据逐条比对,得出导入预览。</summary>
    /// <param name="document">解析好的文件。</param>
    /// <param name="existingSessions">本机全部连接。</param>
    /// <param name="existingGroups">本机全部分组。</param>
    /// <param name="existingCredentials">本机全部共享凭据。</param>
    /// <returns>导入计划。</returns>
    public static SessionImportPlan Plan(
        SessionImportDocument document,
        IReadOnlyList<SessionProfile> existingSessions,
        IReadOnlyList<ServerGroup> existingGroups,
        IReadOnlyList<SharedCredential> existingCredentials)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(existingSessions);
        ArgumentNullException.ThrowIfNull(existingGroups);
        ArgumentNullException.ThrowIfNull(existingCredentials);

        Dictionary<Guid, SessionProfile> byId = IndexById(existingSessions);
        var byEndpoint = existingSessions
            .GroupBy(EndpointKey)
            .ToDictionary(static g => g.Key, static g => g.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());
        var fileIds = document.Candidates.Where(static c => c.HasFileId).Select(static c => c.Profile.Id).ToHashSet();
        var fileNames = document.Candidates.Select(static c => c.Profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fileCredentialIds = document.SharedCredentials.Select(static c => c.Id).ToHashSet();
        var credentialIds = existingCredentials.Select(static c => c.Id).ToHashSet();

        List<SessionImportPlanItem> items = [];
        foreach (SessionImportCandidate candidate in document.Candidates)
        {
            SessionProfile? existing = null;
            SessionImportMatch match = SessionImportMatch.None;
            if (candidate.IsValid)
            {
                if (candidate.HasFileId && byId.TryGetValue(candidate.Profile.Id, out SessionProfile? sameId))
                {
                    existing = sameId;
                    match = SessionImportMatch.SameId;
                }
                else if (byEndpoint.TryGetValue(EndpointKey(candidate.Profile), out List<SessionProfile>? sameEndpoint))
                {
                    existing = sameEndpoint.Find(s => string.Equals(s.Name, candidate.Profile.Name, StringComparison.OrdinalIgnoreCase))
                               ?? sameEndpoint[0];
                    match = SessionImportMatch.SameEndpoint;
                }
            }
            var item = new SessionImportPlanItem { Candidate = candidate, Existing = existing, Match = match };
            if (candidate.IsValid)
            {
                AddReferenceWarnings(item, document.Format, fileIds, fileNames, fileCredentialIds, credentialIds,
                    existingSessions, existingCredentials, byId);
                AddHostChangeWarning(item);
            }
            items.Add(item);
        }
        return new SessionImportPlan
        {
            Document = document,
            Items = items,
            ExistingSessions = existingSessions,
            ExistingGroups = existingGroups,
            ExistingCredentials = existingCredentials
        };
    }

    /// <summary>按用户的选择算出要落盘的全部东西。</summary>
    /// <param name="plan">由 <see cref="Plan" /> 得出的计划。</param>
    /// <param name="options">用户的选择。</param>
    /// <returns>要写的分组、凭据、连接、隧道与统计。</returns>
    public static SessionImportWriteSet BuildWrites(SessionImportPlan plan, SessionImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        return new Writer(plan, options).Run();
    }

    private static void AddReferenceWarnings(
        SessionImportPlanItem item,
        SessionFileFormat format,
        HashSet<Guid> fileIds,
        HashSet<string> fileNames,
        HashSet<Guid> fileCredentialIds,
        HashSet<Guid> credentialIds,
        IReadOnlyList<SessionProfile> existingSessions,
        IReadOnlyList<SharedCredential> existingCredentials,
        Dictionary<Guid, SessionProfile> byId)
    {
        SessionImportCandidate candidate = item.Candidate;
        if (format == SessionFileFormat.Json)
        {
            if (candidate.Profile.CredentialSource is { } reference
                && reference.TryGetSharedId(out Guid credentialId)
                && !fileCredentialIds.Contains(credentialId)
                && !credentialIds.Contains(credentialId))
            {
                item.Warnings.Add(Strings.Get("SessImport_WarnCredentialMissing"));
            }
            if (candidate.Profile.JumpHostProfileId is { } jump && !fileIds.Contains(jump) && !byId.ContainsKey(jump))
            {
                item.Warnings.Add(Strings.Get("SessImport_WarnJumpNotInFile"));
            }
            return;
        }

        if (candidate.CredentialReference is { } credential
            && !(Guid.TryParse(credential, out Guid id) && credentialIds.Contains(id))
            && MatchCredentialByName(credential, item.Existing, existingCredentials, out bool credentialAmbiguous) is null)
        {
            item.Warnings.Add(Strings.Format(credentialAmbiguous ? "SessImport_WarnCredentialAmbiguous" : "SessImport_WarnCredentialNotFound", credential));
        }
        if (candidate.JumpHostReference is { } jumpReference)
        {
            bool found;
            bool jumpAmbiguous = false;
            if (Guid.TryParse(jumpReference, out Guid jumpId))
            {
                found = fileIds.Contains(jumpId) || byId.ContainsKey(jumpId);
            }
            else
            {
                found = fileNames.Contains(jumpReference)
                        || MatchSessionByName(jumpReference, item.Existing, existingSessions, out jumpAmbiguous) is not null;
            }
            if (!found)
            {
                item.Warnings.Add(Strings.Format(jumpAmbiguous ? "SessImport_WarnJumpAmbiguous" : "SessImport_WarnJumpNotFound", jumpReference));
            }
        }
    }

    /// <summary>
    /// 覆盖会把主机改掉、而本机这条存着密码 / 口令时提醒一句:文件没带机密时覆盖会留着本机的那一份,
    /// 下次连接就把它发给了新主机。服务器真的换了 IP 时这正是想要的;但一份被人改过的文件也能借此把密码引到别处去,
    /// 所以要让用户在预览里看见。
    /// </summary>
    private static void AddHostChangeWarning(SessionImportPlanItem item)
    {
        if (item.Existing is not { } existing
            || string.Equals(existing.Host.Trim(), item.Candidate.Profile.Host.Trim(), StringComparison.OrdinalIgnoreCase)
            || (item.Candidate.Columns & SessionCsvColumns.Host) == 0)
        {
            return;
        }
        bool hasSecret = !string.IsNullOrEmpty(existing.Password)
                         || !string.IsNullOrEmpty(existing.PrivateKeyPassphrase)
                         || existing.PluginSecrets is { Count: > 0 };
        if (hasSecret)
        {
            item.Warnings.Add(Strings.Format("SessImport_WarnHostChangesKeepSecret", existing.Host, item.Candidate.Profile.Host));
        }
    }

    /// <summary>
    /// 按名称在本机共享凭据里找一条:恰好一条就是它;重名时,若与这一行重复的本机连接(<paramref name="hint" />)
    /// 正引用其中一条,就是那一条 —— 「导出 CSV → Excel 改 → 导回」时凭据名本机重名也不至于把引用弄丢;否则宁可不接也不接错。
    /// </summary>
    private static Guid? MatchCredentialByName(string reference, SessionProfile? hint, IReadOnlyList<SharedCredential> existing, out bool ambiguous)
    {
        List<SharedCredential> matches = [.. existing.Where(c => NameEquals(c.Name, reference))];
        ambiguous = false;
        if (matches.Count <= 1)
        {
            return matches.Count == 1 ? matches[0].Id : null;
        }
        if (hint?.CredentialSource is { } current && current.TryGetSharedId(out Guid currentId) && matches.Exists(m => m.Id == currentId))
        {
            return currentId;
        }
        ambiguous = true;
        return null;
    }

    /// <summary>按名称在本机连接里找跳板,重名时的处置同 <see cref="MatchCredentialByName" />(看重复那条现在经谁跳)。</summary>
    private static Guid? MatchSessionByName(string reference, SessionProfile? hint, IReadOnlyList<SessionProfile> existing, out bool ambiguous)
    {
        List<SessionProfile> matches = [.. existing.Where(s => NameEquals(s.Name, reference))];
        ambiguous = false;
        if (matches.Count <= 1)
        {
            return matches.Count == 1 ? matches[0].Id : null;
        }
        if (hint?.JumpHostProfileId is { } currentId && matches.Exists(m => m.Id == currentId))
        {
            return currentId;
        }
        ambiguous = true;
        return null;
    }

    private static Dictionary<Guid, SessionProfile> IndexById(IEnumerable<SessionProfile> sessions)
    {
        var byId = new Dictionary<Guid, SessionProfile>();
        foreach (SessionProfile session in sessions)
        {
            _ = byId.TryAdd(session.Id, session);
        }
        return byId;
    }

    private static bool NameEquals(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// CSV 覆盖已有连接:只改文件里有的那几列。没有的列一概不动 —— 终端设置、插件设置这些 CSV 表达不了的,
    /// 导出再导回不能被冲掉。密码列空着表示「不改」,不是「清空」(导出时它恒为空)。
    /// </summary>
    internal static void ApplyCsvColumns(SessionProfile target, SessionProfile source, SessionCsvColumns columns)
    {
        if (columns.HasFlag(SessionCsvColumns.Name))
        {
            target.Name = source.Name;
        }
        if (columns.HasFlag(SessionCsvColumns.Host))
        {
            target.Host = source.Host;
        }
        if (columns.HasFlag(SessionCsvColumns.Port))
        {
            target.Port = source.Port;
        }
        if (columns.HasFlag(SessionCsvColumns.Username))
        {
            target.Username = source.Username;
        }
        if (columns.HasFlag(SessionCsvColumns.Protocol))
        {
            bool protocolChanged = target.ConnectionType != source.ConnectionType
                                   || !string.Equals(target.PluginProtocolId, source.PluginProtocolId, StringComparison.Ordinal);
            target.ConnectionType = source.ConnectionType;
            if (source.ConnectionType == ConnectionType.FTP)
            {
                target.Ftp ??= new FtpSettings();
                target.Ftp.EncryptionMode = source.Ftp?.EncryptionMode ?? FtpEncryptionMode.Auto;
            }
            else
            {
                target.Ftp = null;
            }
            target.PluginProtocolId = source.ConnectionType == ConnectionType.Plugin ? source.PluginProtocolId : null;
            if (protocolChanged)
            {
                // 换了协议,上一个协议的专属设置对新协议毫无意义,留着只会在切回来时诈尸。
                target.PluginSettings = null;
                target.PluginSecrets = null;
            }
        }
        if (columns.HasFlag(SessionCsvColumns.Auth))
        {
            target.AuthMethod = source.AuthMethod;
        }
        if (columns.HasFlag(SessionCsvColumns.Password) && !string.IsNullOrEmpty(source.Password))
        {
            target.Password = source.Password;
            target.RememberPassword = true;
        }
        if (columns.HasFlag(SessionCsvColumns.PrivateKey))
        {
            target.PrivateKeyPath = source.PrivateKeyPath;
        }
        if (columns.HasFlag(SessionCsvColumns.Certificate))
        {
            target.CertificatePath = source.CertificatePath;
        }
        if (columns.HasFlag(SessionCsvColumns.Tags))
        {
            target.Tags = [.. source.Tags];
        }
        if (columns.HasFlag(SessionCsvColumns.Notes))
        {
            target.Notes = source.Notes;
        }
    }

    /// <summary>一次 <see cref="BuildWrites" /> 的工作状态。拆成一个类只是为了不把十几个局部变量在方法之间传来传去。</summary>
    private sealed class Writer(SessionImportPlan plan, SessionImportOptions options)
    {
        private readonly SessionImportWriteSet _result = new();
        private readonly Dictionary<Guid, SessionProfile> _existingById = IndexById(plan.ExistingSessions);
        private readonly HashSet<Guid> _usedIds = [.. plan.ExistingSessions.Select(static s => s.Id)];

        /// <summary>文件里的 Id → 落盘后的 Id(包括被跳过、落到本机已有那条上的)。</summary>
        private readonly Dictionary<Guid, Guid> _idMap = [];

        /// <summary>文件里的名称 → 落盘后的 Id;CSV 的跳板列按名称引用。同名取第一条。</summary>
        private readonly Dictionary<string, Guid> _nameMap = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<Guid, ServerGroup> _groupsById = plan.ExistingGroups
            .GroupBy(static g => g.Id)
            .ToDictionary(static g => g.Key, static g => g.First());

        private readonly Dictionary<string, ServerGroup> _groupsByName = plan.ExistingGroups
            .OrderBy(static g => g.SortOrder)
            .GroupBy(static g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);

        /// <summary>要新建的分组(按名称),带一个排序依据:JSON 用文件里的排序序号,CSV 用首次出现的行。</summary>
        private readonly Dictionary<string, (ServerGroup Group, double Rank)> _newGroups = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>成员列表要改的已有分组(克隆,不动调用方传进来的对象)。</summary>
        private readonly Dictionary<Guid, ServerGroup> _touchedGroups = [];

        private readonly Dictionary<Guid, SharedCredential> _credentialsById = plan.ExistingCredentials
            .GroupBy(static c => c.Id)
            .ToDictionary(static g => g.Key, static g => g.First());

        private readonly Dictionary<Guid, Guid?> _credentialMap = [];

        private bool IsCsv => plan.Document.Format == SessionFileFormat.Csv;

        public SessionImportWriteSet Run()
        {
            List<(SessionImportPlanItem Item, Guid Id, SessionProfile? Target)> writes = Choose();
            List<(SessionImportPlanItem Item, SessionProfile Profile, SessionProfile? Target)> built = [];
            foreach ((SessionImportPlanItem item, Guid id, SessionProfile? target) in writes)
            {
                built.Add((item, Build(item, id, target), target));
            }
            BreakCycles(built);
            foreach ((SessionImportPlanItem item, SessionProfile profile, SessionProfile? target) in built)
            {
                Place(profile, target?.GroupId);
                _result.Sessions.Add(profile);
                if (target is null)
                {
                    _result.Created++;
                }
                else
                {
                    _result.Updated++;
                }
                if (item.Candidate.HasSecret)
                {
                    _result.SecretsImported++;
                }
                if (!IsCsv
                    && item.Candidate.HasFileId
                    && plan.Document.Tunnels.TryGetValue(item.Candidate.Profile.Id, out List<TunnelConfig>? tunnels)
                    && tunnels is { Count: > 0 })
                {
                    _result.Tunnels[profile.Id] = [.. tunnels];
                }
            }
            FinishGroups();
            return _result;
        }

        /// <summary>第一趟:定下每一行写不写、落到哪个 Id 上,并记下文件 Id / 名称到落盘 Id 的映射。</summary>
        private List<(SessionImportPlanItem Item, Guid Id, SessionProfile? Target)> Choose()
        {
            HashSet<int>? selected = options.SelectedIndices is null ? null : [.. options.SelectedIndices];
            bool Chosen(SessionImportCandidate candidate) => candidate.IsValid && (selected?.Contains(candidate.Index) ?? true);

            // 同 Id 的那一行才是本机那条连接本身,它对覆盖目标有优先权:按「主机 + 端口 + 用户名」撞上同一条的别的行
            // 哪怕排在前面也不能抢走它(否则真正的那一行被另存成副本,本机那条却换成了别人的名字和设置)。
            var overwritten = options.Conflict == SessionImportConflict.Overwrite
                ? plan.Items
                    .Where(i => i is { Match: SessionImportMatch.SameId, Existing: not null } && Chosen(i.Candidate))
                    .Select(static i => i.Existing!.Id)
                    .ToHashSet()
                : [];
            var claimedById = new HashSet<Guid>(overwritten);
            List<(SessionImportPlanItem, Guid, SessionProfile?)> writes = [];
            foreach (SessionImportPlanItem item in plan.Items)
            {
                SessionImportCandidate candidate = item.Candidate;
                bool chosen = Chosen(candidate);
                if (!chosen || (item.Existing is not null && options.Conflict == SessionImportConflict.Skip))
                {
                    if (item.Existing is { } skipped)
                    {
                        // 没导入的那条,文件里谁拿它当跳板,就落到本机已有的那一条上。
                        Remember(candidate, skipped.Id);
                    }
                    _result.Skipped++;
                    continue;
                }
                if (item.Existing is { } existing
                    && options.Conflict == SessionImportConflict.Overwrite
                    && (item.Match == SessionImportMatch.SameId
                        ? claimedById.Remove(existing.Id)
                        : overwritten.Add(existing.Id)))
                {
                    Remember(candidate, existing.Id);
                    writes.Add((item, existing.Id, existing));
                    continue;
                }
                // 新连接,或「两条都留」,或文件里第二条也撞上了已经被覆盖过的同一条 —— 都另存一条。
                Guid id = candidate.HasFileId && _usedIds.Add(candidate.Profile.Id) ? candidate.Profile.Id : NewId();
                Remember(candidate, id);
                writes.Add((item, id, null));
            }
            return writes;
        }

        private Guid NewId()
        {
            Guid id = Guid.NewGuid();
            _usedIds.Add(id);
            return id;
        }

        private void Remember(SessionImportCandidate candidate, Guid id)
        {
            if (candidate.HasFileId)
            {
                _ = _idMap.TryAdd(candidate.Profile.Id, id);
            }
            _ = _nameMap.TryAdd(candidate.Profile.Name.Trim(), id);
        }

        /// <summary>第二趟:拼出要落盘的那一条。</summary>
        private SessionProfile Build(SessionImportPlanItem item, Guid id, SessionProfile? target)
        {
            SessionImportCandidate candidate = item.Candidate;
            SessionProfile profile;
            if (target is not null && IsCsv)
            {
                profile = target.Clone();
                ApplyCsvColumns(profile, candidate.Profile, candidate.Columns);
            }
            else
            {
                profile = candidate.Profile.Clone();
            }
            profile.Id = id;
            profile.LastConnectedAt = target?.LastConnectedAt;

            bool keepGroup = target is not null && IsCsv && options.UseFileGroups && !candidate.Columns.HasFlag(SessionCsvColumns.Group);
            profile.GroupId = keepGroup ? target!.GroupId : ResolveGroup(candidate);

            if (target is not null && !IsCsv)
            {
                // 文件没带机密(导出时没勾,或导入时没解锁)就留着本机那一份:覆盖的是配置,不是把人登录不上。
                if (string.IsNullOrEmpty(profile.Password))
                {
                    profile.Password = target.Password;
                }
                if (string.IsNullOrEmpty(profile.PrivateKeyPassphrase))
                {
                    profile.PrivateKeyPassphrase = target.PrivateKeyPassphrase;
                }
                if (profile.PluginSecrets is not { Count: > 0 })
                {
                    profile.PluginSecrets = SessionProfile.CloneSettings(target.PluginSecrets);
                }
            }

            ResolveCredential(candidate, profile, target, item.Existing);
            ResolveJumpHost(candidate, profile, target, item.Existing);
            return profile;
        }

        private Guid? ResolveGroup(SessionImportCandidate candidate)
        {
            if (!options.UseFileGroups)
            {
                return options.TargetGroupId is { } target && _groupsById.ContainsKey(target) ? target : null;
            }
            string? name;
            double rank;
            Guid? preferredId = null;
            if (IsCsv)
            {
                name = candidate.GroupName;
                rank = candidate.Index;
            }
            else
            {
                if (candidate.FileGroupId is not { } fileGroupId)
                {
                    return null;
                }
                if (_groupsById.ContainsKey(fileGroupId))
                {
                    return fileGroupId;
                }
                SessionArchiveGroup? fileGroup = plan.Document.Groups.FirstOrDefault(g => g.Id == fileGroupId);
                name = fileGroup?.Name ?? candidate.GroupName;
                rank = fileGroup?.SortOrder ?? int.MaxValue;
                preferredId = fileGroupId;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }
            name = name.Trim();
            if (_groupsByName.TryGetValue(name, out ServerGroup? existing))
            {
                return existing.Id;
            }
            if (_newGroups.TryGetValue(name, out (ServerGroup Group, double Rank) pending))
            {
                return pending.Group.Id;
            }
            // 沿用文件里的分组 Id(不冲突时):下次再导同一份文件,按 Id 就能认出是同一个分组。
            Guid id = preferredId is { } preferred
                      && !_groupsById.ContainsKey(preferred)
                      && !_newGroups.Values.Any(v => v.Group.Id == preferred)
                ? preferred
                : Guid.NewGuid();
            var group = new ServerGroup { Id = id, Name = name };
            _newGroups[name] = (group, rank);
            return id;
        }

        private void ResolveCredential(SessionImportCandidate candidate, SessionProfile profile, SessionProfile? target, SessionProfile? hint)
        {
            if (IsCsv)
            {
                if (!candidate.Columns.HasFlag(SessionCsvColumns.Credential))
                {
                    if (target is null)
                    {
                        profile.CredentialSource = null;
                    }
                    return;
                }
                if (candidate.CredentialReference is not { } reference)
                {
                    profile.CredentialSource = null;
                    return;
                }
                bool ambiguous = false;
                Guid? local = Guid.TryParse(reference, out Guid id) && _credentialsById.ContainsKey(id)
                    ? id
                    : MatchCredentialByName(reference, hint, plan.ExistingCredentials, out ambiguous);
                profile.CredentialSource = local is { } found ? CredentialReference.ForShared(found) : null;
                if (local is null)
                {
                    Warn(profile, Strings.Format(ambiguous ? "SessImport_WarnCredentialAmbiguous" : "SessImport_WarnCredentialNotFound", reference));
                }
                return;
            }

            if (profile.CredentialSource is not { } fileReference || !fileReference.TryGetSharedId(out Guid fileCredentialId))
            {
                return; // 没有引用,或是别的来源的引用(外部密码管理器):原样保留。
            }
            Guid? resolved = ResolveFileCredential(fileCredentialId);
            profile.CredentialSource = resolved is { } localId ? CredentialReference.ForShared(localId) : null;
            if (resolved is null)
            {
                Warn(profile, Strings.Get("SessImport_WarnCredentialMissing"));
            }
        }

        /// <summary>
        /// 文件里的共享凭据落到本机哪一条:同 Id 的、同名的沿用本机那条(不覆盖),都没有才新建。
        /// </summary>
        private Guid? ResolveFileCredential(Guid fileId)
        {
            if (_credentialMap.TryGetValue(fileId, out Guid? cached))
            {
                return cached;
            }
            Guid? result = null;
            SharedCredential? fileCredential = plan.Document.SharedCredentials.FirstOrDefault(c => c.Id == fileId);
            SharedCredential? local = _credentialsById.GetValueOrDefault(fileId)
                ?? (fileCredential is null
                    ? null
                    : plan.ExistingCredentials
                        .OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault(c => NameEquals(c.Name, fileCredential.Name)));
            if (local is not null)
            {
                FillMissingSecrets(local, fileCredential);
                result = local.Id;
            }
            else if (fileCredential is not null)
            {
                SharedCredential created = fileCredential.Clone();
                if (_result.SharedCredentials.Exists(c => c.Id == created.Id))
                {
                    created.Id = Guid.NewGuid();
                }
                _result.SharedCredentials.Add(created);
                result = created.Id;
            }
            _credentialMap[fileId] = result;
            return result;
        }

        /// <summary>
        /// 沿用本机的共享凭据时,本机那条缺的机密(云同步没设端到端口令时拉下来的凭据就只有名称与用户名)用文件里的补上;
        /// 本机已有的一概不动 —— 「沿用」的意思就是不拿文件去改本机。
        /// </summary>
        private void FillMissingSecrets(SharedCredential local, SharedCredential? fileCredential)
        {
            if (fileCredential is null)
            {
                return;
            }
            SharedCredential copy = _result.SharedCredentials.Find(c => c.Id == local.Id) ?? local.Clone();
            bool filled = false;
            if (string.IsNullOrEmpty(copy.Password) && !string.IsNullOrEmpty(fileCredential.Password))
            {
                copy.Password = fileCredential.Password;
                filled = true;
            }
            if (string.IsNullOrEmpty(copy.PrivateKeyPassphrase) && !string.IsNullOrEmpty(fileCredential.PrivateKeyPassphrase))
            {
                copy.PrivateKeyPassphrase = fileCredential.PrivateKeyPassphrase;
                filled = true;
            }
            if (filled && !_result.SharedCredentials.Contains(copy))
            {
                _result.SharedCredentials.Add(copy);
            }
        }

        private void ResolveJumpHost(SessionImportCandidate candidate, SessionProfile profile, SessionProfile? target, SessionProfile? hint)
        {
            Guid? resolved;
            string? shown;
            if (IsCsv)
            {
                if (!candidate.Columns.HasFlag(SessionCsvColumns.JumpHost))
                {
                    if (target is null)
                    {
                        profile.JumpHostProfileId = null;
                    }
                    return;
                }
                if (candidate.JumpHostReference is not { } reference)
                {
                    profile.JumpHostProfileId = null;
                    return;
                }
                shown = reference;
                if (Guid.TryParse(reference, out Guid id))
                {
                    resolved = _idMap.TryGetValue(id, out Guid mapped) ? mapped : _existingById.ContainsKey(id) ? id : null;
                }
                else if (_nameMap.TryGetValue(reference.Trim(), out Guid named))
                {
                    resolved = named;
                }
                else
                {
                    resolved = MatchSessionByName(reference, hint, plan.ExistingSessions, out bool ambiguous);
                    if (ambiguous)
                    {
                        Warn(profile, Strings.Format("SessImport_WarnJumpAmbiguous", reference));
                        profile.JumpHostProfileId = null;
                        return;
                    }
                }
            }
            else
            {
                if (candidate.Profile.JumpHostProfileId is not { } fileJump)
                {
                    profile.JumpHostProfileId = null;
                    return;
                }
                shown = null;
                resolved = _idMap.TryGetValue(fileJump, out Guid mapped) ? mapped
                    : _existingById.ContainsKey(fileJump) ? fileJump
                    : null;
            }
            if (resolved == profile.Id)
            {
                Warn(profile, Strings.Get("SessImport_WarnJumpSelf"));
                resolved = null;
            }
            else if (resolved is null)
            {
                Warn(profile, shown is null
                    ? Strings.Get("SessImport_WarnJumpNotInFile")
                    : Strings.Format("SessImport_WarnJumpNotFound", shown));
            }
            profile.JumpHostProfileId = resolved;
        }

        /// <summary>
        /// 跳板成环(a→b→a)的那几条断开、改为直连:留着只会让连接时的环检测报错,断在这里两条至少还能各自直连。
        /// 只看经过刚写入的连接的环 —— 本机原有数据里的环不归导入管。
        /// </summary>
        private void BreakCycles(List<(SessionImportPlanItem Item, SessionProfile Profile, SessionProfile? Target)> built)
        {
            var jumps = new Dictionary<Guid, Guid?>();
            foreach (SessionProfile existing in plan.ExistingSessions)
            {
                jumps[existing.Id] = existing.JumpHostProfileId;
            }
            foreach ((_, SessionProfile profile, _) in built)
            {
                jumps[profile.Id] = profile.JumpHostProfileId;
            }
            foreach ((_, SessionProfile profile, _) in built)
            {
                if (profile.JumpHostProfileId is null || !OnCycle(profile.Id, jumps))
                {
                    continue;
                }
                profile.JumpHostProfileId = null;
                jumps[profile.Id] = null;
                Warn(profile, Strings.Get("SessImport_WarnJumpCycle"));
            }
        }

        private static bool OnCycle(Guid start, Dictionary<Guid, Guid?> jumps)
        {
            var seen = new HashSet<Guid> { start };
            Guid current = start;
            while (jumps.TryGetValue(current, out Guid? next) && next is { } step)
            {
                if (!seen.Add(step))
                {
                    return step == start;
                }
                current = step;
            }
            return false;
        }

        /// <summary>维护分组的成员列表:从原来那组里摘掉,加进新的那组。</summary>
        private void Place(SessionProfile profile, Guid? previousGroupId)
        {
            if (previousGroupId is { } previous && previous != profile.GroupId && Touch(previous) is { } oldGroup)
            {
                oldGroup.Sessions.Remove(profile.Id);
            }
            if (profile.GroupId is not { } groupId)
            {
                return;
            }
            ServerGroup? group = _newGroups.Values.Select(static v => v.Group).FirstOrDefault(g => g.Id == groupId) ?? Touch(groupId);
            if (group is not null && !group.Sessions.Contains(profile.Id))
            {
                group.Sessions.Add(profile.Id);
            }
        }

        private ServerGroup? Touch(Guid groupId)
        {
            if (_touchedGroups.TryGetValue(groupId, out ServerGroup? touched))
            {
                return touched;
            }
            if (!_groupsById.TryGetValue(groupId, out ServerGroup? existing))
            {
                return null;
            }
            var copy = new ServerGroup
            {
                Id = existing.Id,
                Name = existing.Name,
                Icon = existing.Icon,
                SortOrder = existing.SortOrder,
                Sessions = [.. existing.Sessions]
            };
            _touchedGroups[groupId] = copy;
            return copy;
        }

        /// <summary>新建的分组接在已有分组之后,彼此之间按文件里的先后排。</summary>
        private void FinishGroups()
        {
            int next = plan.ExistingGroups.Count == 0 ? 0 : plan.ExistingGroups.Max(static g => g.SortOrder) + 1;
            foreach ((ServerGroup group, _) in _newGroups.Values.OrderBy(static v => v.Rank))
            {
                group.SortOrder = next++;
                _result.Groups.Add(group);
            }
            _result.GroupsCreated = _newGroups.Count;
            _result.Groups.AddRange(_touchedGroups.Values);
        }

        private void Warn(SessionProfile profile, string message) =>
            _result.Warnings.Add(Strings.Format("SessImport_ItemNoteFmt", profile.Name, message));
    }
}
