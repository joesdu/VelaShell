using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>快捷命令单文档 v2 仓储,负责 SonnetDB/旧 JSON/Gist 的版本迁移。</summary>
public sealed class SonnetDbQuickCommandRepository(
    IAppDataStore dataStore,
    string? legacyDataPath = null
) : IQuickCommandRepository
{
    private const string Collection = "quick_commands";
    private const string DocumentId = "commands";
    private const string BackupDocumentId = "commands.v1.backup";
    private static readonly Guid CommandNamespace = new("90ec0554-932a-5aa8-9f85-46c75d96921d");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _legacyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private QuickCommandLoadResult? _loaded;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task<QuickCommandLoadResult> LoadAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (_loaded is not null)
        {
            return _loaded;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded is not null)
            {
                return _loaded;
            }

            DocumentHeader? header = await dataStore
                .GetAsync<DocumentHeader>(Collection, DocumentId, cancellationToken)
                .ConfigureAwait(false);
            if (header is null)
            {
                LegacyQuickCommandData? legacy = await TryReadLegacyFileAsync(cancellationToken)
                    .ConfigureAwait(false);
                QuickCommandData fresh = legacy is null
                    ? CreateEmptyDocument()
                    : MigrateLegacy(legacy);
                await dataStore
                    .UpsertAsync(Collection, DocumentId, fresh, cancellationToken)
                    .ConfigureAwait(false);
                if (legacy is not null)
                {
                    TryRenameLegacyFile();
                }
                _loaded = new(fresh, legacy is not null);
                return _loaded;
            }

            if (header.SchemaVersion > QuickCommandData.CurrentSchemaVersion)
            {
                _loaded = new(
                    CreateEmptyDocument(),
                    Error: $"Quick command data version {header.SchemaVersion} is newer than supported version {QuickCommandData.CurrentSchemaVersion}."
                );
                return _loaded;
            }

            if (header.SchemaVersion < QuickCommandData.CurrentSchemaVersion)
            {
                JsonObject? original = await dataStore
                    .GetAsync<JsonObject>(Collection, DocumentId, cancellationToken)
                    .ConfigureAwait(false);
                LegacyQuickCommandData legacy =
                    await dataStore
                        .GetAsync<LegacyQuickCommandData>(Collection, DocumentId, cancellationToken)
                        .ConfigureAwait(false)
                    ?? new();
                if (
                    await dataStore
                        .GetAsync<LegacyQuickCommandData>(
                            Collection,
                            BackupDocumentId,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                    is null
                )
                {
                    await dataStore
                        .UpsertAsync(
                            Collection,
                            BackupDocumentId,
                            original ?? JsonSerializer.SerializeToNode(legacy)!.AsObject(),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                QuickCommandData migrated = MigrateLegacy(legacy);
                await dataStore
                    .UpsertAsync(Collection, DocumentId, migrated, cancellationToken)
                    .ConfigureAwait(false);
                _loaded = new(migrated, Migrated: true);
                return _loaded;
            }

            QuickCommandData data =
                await dataStore
                    .GetAsync<QuickCommandData>(Collection, DocumentId, cancellationToken)
                    .ConfigureAwait(false)
                ?? CreateEmptyDocument();
            bool repaired = Normalize(data);
            if (repaired)
            {
                await dataStore
                    .UpsertAsync(Collection, DocumentId, data, cancellationToken)
                    .ConfigureAwait(false);
            }
            _loaded = new(data, Migrated: repaired);
            return _loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            _loaded = new(CreateEmptyDocument(), Error: ex.Message);
            return _loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        QuickCommandData data,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(data);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            data.SchemaVersion = QuickCommandData.CurrentSchemaVersion;
            Normalize(data);
            await dataStore
                .UpsertAsync(Collection, DocumentId, data, cancellationToken)
                .ConfigureAwait(false);
            _loaded = new(data);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task<QuickCommandSyncData> ExportSyncAsync(
        CancellationToken cancellationToken = default
    )
    {
        QuickCommandData data = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Data;
        var groups = data.Groups.ToDictionary(group => group.Id);
        return new()
        {
            SchemaVersion = QuickCommandData.CurrentSchemaVersion,
            Groups = [.. data.Groups.Select(QuickCommandGroupCatalog.Clone)],
            Commands =
            [
                .. data.Commands.Select(command => new QuickCommandSyncItem
                {
                    Id = command.Id,
                    GroupId = command.GroupId,
                    Name = command.Name,
                    Category = groups.TryGetValue(command.GroupId, out QuickCommandGroup? group)
                        ? DisplayCompatibilityName(group)
                        : "Ungrouped",
                    CommandText = command.CommandText,
                    ExecuteAfterSend = command.ExecuteAfterSend,
                    Description = command.Description,
                    SortOrder = command.SortOrder,
                }),
            ],
            HiddenBuiltInIds = [.. data.HiddenBuiltInIds],
        };
    }

    /// <inheritdoc />
    public async Task ApplySyncAsync(
        QuickCommandSyncData data,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.SchemaVersion > QuickCommandData.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Remote quick command data version {data.SchemaVersion} is newer than supported version {QuickCommandData.CurrentSchemaVersion}."
            );
        }

        QuickCommandData normalized =
            data.SchemaVersion < QuickCommandData.CurrentSchemaVersion
                ? MigrateLegacy(
                    new()
                    {
                        Commands =
                        [
                            .. data.Commands.Select(command => new LegacyQuickCommand
                            {
                                Id = command.Id,
                                Name = command.Name,
                                Category = command.Category,
                                CommandText = command.CommandText,
                                Description = command.Description,
                                IsBuiltIn = command.IsBuiltIn,
                            }),
                        ],
                    }
                )
                : new()
                {
                    SchemaVersion = QuickCommandData.CurrentSchemaVersion,
                    Groups = [.. data.Groups.Select(QuickCommandGroupCatalog.Clone)],
                    Commands =
                    [
                        .. data
                            .Commands.Where(command => !command.IsBuiltIn)
                            .Select(command => new QuickCommand
                            {
                                Id = command.Id,
                                GroupId = command.GroupId,
                                Name = command.Name,
                                CommandText = command.CommandText,
                                ExecuteAfterSend = command.ExecuteAfterSend,
                                Description = command.Description,
                                SortOrder = command.SortOrder,
                            }),
                    ],
                    HiddenBuiltInIds = [.. data.HiddenBuiltInIds ?? []],
                };
        Normalize(normalized, data.Commands);
        await SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LegacyQuickCommandData?> TryReadLegacyFileAsync(
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(legacyDataPath) || !File.Exists(legacyDataPath))
        {
            return null;
        }
        string json = await File.ReadAllTextAsync(legacyDataPath, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize<LegacyQuickCommandData>(json, _legacyJsonOptions);
    }

    private void TryRenameLegacyFile()
    {
        if (string.IsNullOrEmpty(legacyDataPath) || !File.Exists(legacyDataPath))
        {
            return;
        }
        string backupPath = legacyDataPath + ".migrated.bak";
        if (!File.Exists(backupPath))
        {
            File.Move(legacyDataPath, backupPath);
        }
    }

    private static QuickCommandData MigrateLegacy(LegacyQuickCommandData legacy)
    {
        QuickCommandData data = CreateEmptyDocument();
        var groups = data
            .Groups.Where(group => group.Kind != QuickCommandGroupKind.Default)
            .ToDictionary(group => group.Name, StringComparer.OrdinalIgnoreCase);
        int nextUserGroupOrder = QuickCommandGroupCatalog.BuiltIns.Count;
        Dictionary<Guid, int> commandOrder = [];

        for (int index = 0; index < legacy.Commands.Count; index++)
        {
            LegacyQuickCommand old = legacy.Commands[index];
            if (
                old.IsBuiltIn
                || string.IsNullOrWhiteSpace(old.Name)
                || string.IsNullOrWhiteSpace(old.CommandText)
            )
            {
                continue;
            }

            string category = old.Category.Trim();
            Guid groupId;
            if (string.IsNullOrEmpty(category))
            {
                groupId = QuickCommandGroupCatalog.DefaultGroupId;
            }
            else if (groups.TryGetValue(category, out QuickCommandGroup? existing))
            {
                groupId = existing.Id;
            }
            else
            {
                var group = new QuickCommandGroup
                {
                    Id = QuickCommandGroupCatalog.IdForName(category),
                    Name = category,
                    SortOrder = nextUserGroupOrder++,
                    Kind = QuickCommandGroupKind.User,
                };
                groups.Add(group.Name, group);
                data.Groups.Add(group);
                groupId = group.Id;
            }

            int sortOrder = commandOrder.GetValueOrDefault(groupId);
            commandOrder[groupId] = sortOrder + 1;
            data.Commands.Add(
                new()
                {
                    Id = old.Id == Guid.Empty ? CommandId(old, index) : old.Id,
                    GroupId = groupId,
                    Name = old.Name.Trim(),
                    CommandText = old.CommandText.Trim(),
                    Description = old.Description.Trim(),
                    SortOrder = sortOrder,
                }
            );
        }
        Normalize(data);
        return data;
    }

    private static bool Normalize(
        QuickCommandData data,
        IReadOnlyList<QuickCommandSyncItem>? syncItems = null
    )
    {
        bool changed = data.SchemaVersion != QuickCommandData.CurrentSchemaVersion;
        data.SchemaVersion = QuickCommandData.CurrentSchemaVersion;
        changed |= data.Groups is null || data.Commands is null;
        data.Groups ??= [];
        data.Commands ??= [];

        // 系统分组(内置 + 未分组)的标识、名称与来源以目录为准;用户拖出来的顺序沿用存档 ——
        // 内置分组之间的先后(#555)与每个分组的组内顺序。未分组固定垫底,它的 SortOrder 不跟存档走。
        var stored = data
            .Groups.Where(group => group is not null)
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First());
        List<QuickCommandGroup> normalizedGroups = QuickCommandGroupCatalog.CreateSystemGroups();
        foreach (QuickCommandGroup system in normalizedGroups)
        {
            if (stored.TryGetValue(system.Id, out QuickCommandGroup? persisted))
            {
                if (system.Kind == QuickCommandGroupKind.BuiltIn)
                {
                    system.SortOrder = persisted.SortOrder;
                }
                system.CommandOrder = NormalizeOrder(persisted.CommandOrder);
            }
        }
        var groupIds = normalizedGroups.Select(group => group.Id).ToHashSet();
        var groupNames = normalizedGroups
            .Where(group => group.Kind != QuickCommandGroupKind.Default)
            .Select(group => group.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (
            QuickCommandGroup group in data
                .Groups.Where(group => group.Kind == QuickCommandGroupKind.User)
                .OrderBy(group => group.SortOrder)
        )
        {
            string name = group.Name.Trim();
            if (string.IsNullOrEmpty(name) || groupNames.Contains(name))
            {
                changed = true;
                continue;
            }
            Guid id = group.Id == Guid.Empty ? QuickCommandGroupCatalog.IdForName(name) : group.Id;
            normalizedGroups.Add(
                new()
                {
                    Id = id,
                    Name = name,
                    SortOrder = group.SortOrder,
                    Kind = QuickCommandGroupKind.User,
                    CommandOrder = NormalizeOrder(group.CommandOrder),
                }
            );
            groupIds.Add(id);
            groupNames.Add(name);
            changed |= id != group.Id || name != group.Name;
        }

        Dictionary<Guid, string> compatibilityCategories =
            syncItems
                ?.Where(item => item.Id != Guid.Empty)
                .GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.First().Category)
            ?? [];
        HashSet<Guid> commandIds = [];
        for (int index = 0; index < data.Commands.Count; index++)
        {
            QuickCommand command = data.Commands[index];
            if (command.Id == Guid.Empty || !commandIds.Add(command.Id))
            {
                command.Id = CommandId(command, index);
                commandIds.Add(command.Id);
                changed = true;
            }
            if (!groupIds.Contains(command.GroupId))
            {
                if (
                    compatibilityCategories.TryGetValue(command.Id, out string? category)
                    && !string.IsNullOrWhiteSpace(category)
                )
                {
                    QuickCommandGroup? matching = normalizedGroups.FirstOrDefault(group =>
                        string.Equals(
                            group.Name,
                            category.Trim(),
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
                    if (matching is null)
                    {
                        matching = new()
                        {
                            Id = QuickCommandGroupCatalog.IdForName(category),
                            Name = category.Trim(),
                            SortOrder = normalizedGroups.Count,
                            Kind = QuickCommandGroupKind.User,
                        };
                        normalizedGroups.Add(matching);
                        groupIds.Add(matching.Id);
                    }
                    command.GroupId = matching.Id;
                }
                else
                {
                    command.GroupId = QuickCommandGroupCatalog.DefaultGroupId;
                }
                changed = true;
            }
            command.IsBuiltIn = false;
        }

        changed |= !GroupsEquivalent(data.Groups, normalizedGroups);
        data.Groups = normalizedGroups;

        List<Guid> hidden = NormalizeOrder(data.HiddenBuiltInIds);
        changed |= data.HiddenBuiltInIds is null || !hidden.SequenceEqual(data.HiddenBuiltInIds);
        data.HiddenBuiltInIds = hidden;
        return changed;
    }

    /// <summary>去掉空标识与重复项,保留首次出现的位置。</summary>
    private static List<Guid> NormalizeOrder(List<Guid>? ids) =>
        [.. (ids ?? []).Where(id => id != Guid.Empty).Distinct()];

    /// <summary>
    /// 按标识比较两份分组表(列表里的先后不算:界面按 <see cref="QuickCommandGroup.SortOrder" /> 排)。
    /// 「有变化」会让装载结果标成已迁移、进而触发一次云同步,这里多报一次就是每次启动都同步一次。
    /// </summary>
    private static bool GroupsEquivalent(
        List<QuickCommandGroup> left,
        List<QuickCommandGroup> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        var byId = left.Where(group => group is not null)
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First());
        return right.All(group =>
            byId.TryGetValue(group.Id, out QuickCommandGroup? other)
            && other.Name == group.Name
            && other.SortOrder == group.SortOrder
            && other.Kind == group.Kind
            && (other.CommandOrder ?? []).SequenceEqual(group.CommandOrder)
        );
    }

    private static QuickCommandData CreateEmptyDocument() =>
        new()
        {
            SchemaVersion = QuickCommandData.CurrentSchemaVersion,
            Groups = QuickCommandGroupCatalog.CreateSystemGroups(),
        };

    private static Guid CommandId(LegacyQuickCommand command, int index) =>
        DeterministicGuid(
            $"{index}\n{command.Name}\n{command.Category}\n{command.CommandText}\n{command.Description}"
        );

    private static Guid CommandId(QuickCommand command, int index) =>
        DeterministicGuid(
            $"{index}\n{command.Name}\n{command.GroupId:D}\n{command.CommandText}\n{command.Description}"
        );

    private static Guid DeterministicGuid(string value)
    {
        byte[] namespaceBytes = CommandNamespace.ToByteArray();
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);
        byte[] input = new byte[namespaceBytes.Length + valueBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        valueBytes.CopyTo(input, namespaceBytes.Length);
        byte[] hash = SHA256.HashData(input);
        Span<byte> guidBytes = hash.AsSpan(0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new(guidBytes);
    }

    private static string DisplayCompatibilityName(QuickCommandGroup group) =>
        group.Kind == QuickCommandGroupKind.Default ? "Ungrouped" : group.Name;

    private sealed class DocumentHeader
    {
        public int SchemaVersion { get; set; }
    }

    private sealed class LegacyQuickCommandData
    {
        public List<LegacyQuickCommand> Commands { get; set; } = [];
    }

    private sealed class LegacyQuickCommand
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string CommandText { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public bool IsBuiltIn { get; set; }
    }
}
