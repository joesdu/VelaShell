using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VelaShell.Core.Models;

/// <summary>快捷命令分组的来源与可编辑性。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuickCommandGroupKind>))]
public enum QuickCommandGroupKind
{
    /// <summary>系统保留的未分组。</summary>
    Default,

    /// <summary>由内置命令目录提供的系统分组。</summary>
    BuiltIn,

    /// <summary>用户创建的分组。</summary>
    User,
}

/// <summary>快捷命令分组。</summary>
public sealed class QuickCommandGroup
{
    /// <summary>分组稳定标识。</summary>
    public Guid Id { get; set; }

    /// <summary>分组名称;默认分组由界面显示本地化名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组显示顺序。</summary>
    public int SortOrder { get; set; }

    /// <summary>分组来源。</summary>
    public QuickCommandGroupKind Kind { get; set; }
}

/// <summary>内置及默认分组的稳定目录。</summary>
public static class QuickCommandGroupCatalog
{
    private static readonly Guid GroupNamespace = new("ea68a11a-a6c8-5f2c-b950-d4421096b8c8");

    /// <summary>未分组的固定标识。</summary>
    public static Guid DefaultGroupId { get; } = new("fc8524ec-e5f0-5c59-bbdd-7c66dd06b4f3");

    /// <summary>内置分组,标识跨版本和设备保持稳定。</summary>
    public static IReadOnlyList<QuickCommandGroup> BuiltIns { get; } =
    [CreateBuiltIn("Network", 0), CreateBuiltIn("System", 1), CreateBuiltIn("Docker", 2)];

    /// <summary>为分组名称生成确定性标识。</summary>
    public static Guid IdForName(string name)
    {
        string normalized = name.Trim().ToUpperInvariant();
        byte[] namespaceBytes = GroupNamespace.ToByteArray();
        byte[] nameBytes = Encoding.UTF8.GetBytes(normalized);
        byte[] input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);
        byte[] hash = SHA256.HashData(input);
        Span<byte> guidBytes = hash.AsSpan(0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    /// <summary>创建包含默认和内置分组的新快照。</summary>
    public static List<QuickCommandGroup> CreateSystemGroups() =>
        [
            .. BuiltIns.Select(Clone),
            new()
            {
                Id = DefaultGroupId,
                Name = string.Empty,
                SortOrder = int.MaxValue,
                Kind = QuickCommandGroupKind.Default,
            },
        ];

    /// <summary>复制分组,防止调用方修改静态目录实例。</summary>
    public static QuickCommandGroup Clone(QuickCommandGroup group) =>
        new()
        {
            Id = group.Id,
            Name = group.Name,
            SortOrder = group.SortOrder,
            Kind = group.Kind,
        };

    private static QuickCommandGroup CreateBuiltIn(string name, int sortOrder) =>
        new()
        {
            Id = IdForName(name),
            Name = name,
            SortOrder = sortOrder,
            Kind = QuickCommandGroupKind.BuiltIn,
        };
}
