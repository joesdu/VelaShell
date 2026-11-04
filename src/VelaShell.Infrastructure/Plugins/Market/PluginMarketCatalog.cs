namespace VelaShell.Infrastructure.Plugins.Market;

/// <summary>商店的分页检索结果。</summary>
public sealed record PluginMarketPage
{
    /// <summary>符合查询条件的总条数。</summary>
    public long Total { get; init; }
    /// <summary>从 1 开始的页码。</summary>
    public int Page { get; init; }
    /// <summary>服务端采用的每页条数。</summary>
    public int Size { get; init; }
    /// <summary>本页条目;成功但无结果时为空集合。</summary>
    public required IReadOnlyList<PluginMarketEntry> Items { get; init; }
}

/// <summary>商店列表条目;兼容性以详情中的具体版本为准。</summary>
public sealed record PluginMarketEntry
{
    /// <summary>插件清单标识。</summary>
    public required string Id { get; init; }
    /// <summary>服务端提供的显示名称。</summary>
    public required string DisplayName { get; init; }
    /// <summary>作者填写的简介。</summary>
    public string? Summary { get; init; }
    /// <summary>服务端生成的纯文本摘要。</summary>
    public string? Excerpt { get; init; }
    /// <summary>作者显示名称,不代表签名信任。</summary>
    public string? Author { get; init; }
    /// <summary>分类标签。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>列表展示的最新版本;安装选择以详情为准。</summary>
    public string? LatestVersion { get; init; }
    /// <summary>商店统计的下载次数。</summary>
    public long Downloads { get; init; }
}

/// <summary>商店详情。描述使用 Markdown,不执行服务端返回的 HTML。</summary>
public sealed record PluginMarketDetail
{
    /// <summary>插件清单标识,必须与请求一致。</summary>
    public required string Id { get; init; }
    /// <summary>服务端提供的显示名称。</summary>
    public required string DisplayName { get; init; }
    /// <summary>作者填写的简介。</summary>
    public string? Summary { get; init; }
    /// <summary>作者显示名称,不代表签名信任。</summary>
    public string? Author { get; init; }
    /// <summary>声明的许可证名称。</summary>
    public string? License { get; init; }
    /// <summary>待安全渲染的原始 Markdown。</summary>
    public string? DescriptionMarkdown { get; init; }
    /// <summary>描述下架原因;存在时替代原始描述。</summary>
    public string? DescriptionRemovedReason { get; init; }
    /// <summary>已发布版本,可能包含预发布。</summary>
    public required IReadOnlyList<PluginMarketRelease> Versions { get; init; }
}

/// <summary>详情里的已发布版本,包含安装前的完整兼容性要求。</summary>
public sealed record PluginMarketRelease
{
    /// <summary>SemVer 版本号。</summary>
    public required string Version { get; init; }
    /// <summary>所需插件 API 级别。</summary>
    public int ApiLevel { get; init; }
    /// <summary>最低宿主版本;为空表示未声明。</summary>
    public string? MinHostVersion { get; init; }
    /// <summary>最低 SDK 版本;为空表示未声明。</summary>
    public string? MinSdkVersion { get; init; }
    /// <summary>商店报告的签名状态;不替代本机验签。</summary>
    public string? Signature { get; init; }
    /// <summary>安装包大小,单位为字节。</summary>
    public long PackageSize { get; init; }
}
