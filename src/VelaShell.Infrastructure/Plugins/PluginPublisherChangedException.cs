namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 这个包的签名发布者,与该插件<b>安装时钉住</b>的那一个对不上。
/// </summary>
/// <remarks>
/// <para>
/// 换钥这件事机器判不了:它可能是作者按期轮换密钥,也可能是发布身份被劫持
/// (市场那边的静态检查同样只把 <c>SIGNATURE_KEY_ROTATED</c> 转人工,不判死)。
/// 所以宿主既不静默放行也不一口回绝 —— 抛出这一条,由界面把两个指纹摆到用户面前再问一次。
/// </para>
/// <para>
/// 它把对话框需要的东西(显示名、钉住的指纹、这个包的指纹)一并带上,是为了让
/// 「问什么」与「拦什么」出自同一处判断。界面若自己再解析一遍包去凑这段话,
/// 就多出一条"问的时候看到 A、装下去的是 B"的缝 —— 那正是重名条目那类把戏的形状。
/// </para>
/// </remarks>
/// <param name="pluginId">插件 id。</param>
/// <param name="displayName">插件显示名(取自包内清单)。</param>
/// <param name="pinnedFingerprint">安装时钉住的发布者指纹。</param>
/// <param name="packageFingerprint">这个包的发布者指纹;<see langword="null" /> = 这个包没有签名。</param>
/// <param name="message">面向用户的拒绝原因。</param>
public sealed class PluginPublisherChangedException(
    string pluginId,
    string displayName,
    string pinnedFingerprint,
    string? packageFingerprint,
    string message) : InvalidOperationException(message)
{
    /// <summary>插件 id。</summary>
    public string PluginId { get; } = pluginId;

    /// <summary>插件显示名(取自包内清单;清单没给就是 id)。</summary>
    public string DisplayName { get; } = displayName;

    /// <summary>安装时钉住的发布者公钥指纹(<c>SHA256:…</c>)。</summary>
    public string PinnedFingerprint { get; } = pinnedFingerprint;

    /// <summary>这个包的发布者公钥指纹;<see langword="null" /> = 这个包根本没签名。</summary>
    public string? PackageFingerprint { get; } = packageFingerprint;
}
