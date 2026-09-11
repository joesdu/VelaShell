namespace VelaShell.Infrastructure.Plugins.Market;

/// <summary>商店上某个插件的「最新已发布版本」,以及装上它需要什么。</summary>
/// <remarks>
/// 字段刻意比商店详情页窄:这是拿来做机器比对的(该不该升、升得上去吗、还是不是上次那个人),
/// 不是拿来渲染插件详情的。要展示描述、评分、截图,让用户去商店页面看。
/// </remarks>
public sealed record PluginMarketVersion
{
    /// <summary>插件 id。</summary>
    public required string Id { get; init; }

    /// <summary>版本号。</summary>
    public required string Version { get; init; }

    /// <summary>该版本声明的 apiLevel。</summary>
    public int ApiLevel { get; init; }

    /// <summary>要求的最低宿主版本;未声明时为 <see langword="null" />。</summary>
    public string? MinHostVersion { get; init; }

    /// <summary>要求的最低插件 SDK 版本;未声明时为 <see langword="null" />。</summary>
    public string? MinSdkVersion { get; init; }

    /// <summary>商店收包时验出的签名结论(<c>Trusted</c> / <c>Untrusted</c> / <c>Unsigned</c>)。</summary>
    public string? Signature { get; init; }

    /// <summary>
    /// 发布者公钥指纹(<c>SHA256:…</c>);包未签名时为 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// 与安装收据里钉住的那一个比对,就能在**下载之前**判断这一版是不是换了人 ——
    /// 换了人的那一版不该悄悄装上去,而"先下完再被拒"白费的是用户的流量。
    /// </remarks>
    public string? PublisherFingerprint { get; init; }

    /// <summary>整个 <c>.vpx</c> 文件的 SHA-256(下载后核对用)。</summary>
    public string? FileSha256 { get; init; }

    /// <summary>包字节数。</summary>
    public long PackageSize { get; init; }
}
