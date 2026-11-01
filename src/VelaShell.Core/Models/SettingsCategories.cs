using System.Text.Json;
using System.Text.Json.Nodes;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Models;

/// <summary>
/// 设置的一个分类:设置导出 / 导入的勾选单位,也是审计日志里「设置变更」记录的粒度。
/// </summary>
/// <param name="Id">稳定标识(写进导出文件的头与审计记录,改名就是破坏兼容)。</param>
/// <param name="LabelKey">界面上的名称在 Strings.resx 里的键(与设置页左侧导航同一份文案)。</param>
/// <param name="IsSensitive">
/// 敏感分类:导出与导入时默认不勾,勾上要看到警示。代理带着内网的地址与账号,
/// 安全审计带着主机指纹策略、外部拉起的信任目标(<c>用户@主机</c>)—— 前者不该进一个随手分享的文件,
/// 后者从别人给的文件里导进来,等于让对方替你关掉外部拉起的确认。
/// </param>
/// <param name="Block"><see cref="AppSettings" /> 上那一块分组选项在 JSON 里的名字(camelCase)。</param>
/// <param name="TopLevel">归到这一类的根级字段(同为 JSON 名)—— 它们历史上直接挂在根上,界面却在这一页。</param>
public sealed record SettingsCategory(
    string Id,
    string LabelKey,
    bool IsSensitive,
    string Block,
    IReadOnlyList<string> TopLevel)
{
    /// <summary>界面上的名称(按当前界面语言)。</summary>
    public string Label => Strings.Get(LabelKey);
}

/// <summary>
/// <see cref="AppSettings" /> 按分类的切法。<b>分类就是 <see cref="AppSettings" /> 的实际分块</b>,
/// 根级的散字段归到它们在设置页上所在的那一页。
/// </summary>
/// <remarks>
/// <para>
/// 每个根级属性都必须恰好归到一类(<c>SettingsCategoriesTests</c> 用反射核对):
/// 漏掉一个,它就既不随导出走、也不在审计里出现,而且没有任何地方会报错。
/// </para>
/// <para>
/// 字段的三种特殊身份也登记在这里,导出、导入与审计共用这一份:
/// <see cref="SecretFields" />(机密,导出一律不写)、<see cref="StateFields" />(运行状态,不是设置)、
/// <see cref="MigrationFields" />(一次性迁移标记,审计不算)。
/// </para>
/// </remarks>
public static class SettingsCategories
{
    /// <summary>常规。</summary>
    public const string General = "general";

    /// <summary>消息中心(界面在常规页,数据是单独一块)。</summary>
    public const string Notifications = "notifications";

    /// <summary>外观。</summary>
    public const string Appearance = "appearance";

    /// <summary>终端。</summary>
    public const string Terminal = "terminal";

    /// <summary>密钥管理。</summary>
    public const string Keys = "keys";

    /// <summary>快捷键。</summary>
    public const string Shortcuts = "shortcuts";

    /// <summary>文件传输。</summary>
    public const string Transfer = "transfer";

    /// <summary>安全审计(敏感)。</summary>
    public const string Security = "security";

    /// <summary>网络代理(敏感)。</summary>
    public const string Proxy = "proxy";

    /// <summary>X Server。</summary>
    public const string XServer = "xserver";

    /// <summary>全部分类,<b>顺序即界面上的顺序</b>(与设置页左侧导航一致,消息中心跟在常规后面)。</summary>
    public static IReadOnlyList<SettingsCategory> All { get; } =
    [
        new(General, "SetVm_SectionGeneral", false, "general", ["language", "defaultPort"]),
        new(Notifications, "SetGeneral_MessagesSection", false, "notifications", []),
        new(Appearance, "SetVm_SectionAppearance", false, "appearance", ["theme", "accentColor"]),
        new(Terminal, "SetVm_SectionTerminal", false, "terminalBehavior",
            ["terminalFont", "terminalFontSize", "scrollbackLines", "terminalType", "terminalEncoding"]),
        new(Keys, "SetVm_SectionKeys", false, "keys", []),
        new(Shortcuts, "SetVm_SectionShortcuts", false, "shortcuts", []),
        new(Transfer, "SetVm_SectionTransfer", false, "transfer", []),
        new(Security, "SetVm_SectionSecurity", true, "security", []),
        new(Proxy, "SetVm_SectionProxy", true, "proxy", []),
        new(XServer, "SetVm_SectionXServer", false, "xServer", []),
    ];

    /// <summary>
    /// 机密字段(分组 JSON 名, 字段 JSON 名):导出<b>一律不写</b>,勾了所在分类也不写。
    /// </summary>
    /// <remarks>
    /// 代理密码在本机落盘时经 <c>ISecretProtector</c> 加密(<c>SonnetDbSettingsService</c>),可写进导出文件就是明文了;
    /// 告警 Webhook 地址本身就是令牌(钉钉、飞书、企业微信、Slack 的机器人地址里带着 key,
    /// 拿到地址就能往群里发消息)。导出文件的用途是备份、换机、贴给别人排查,三种都会让它离开本机;
    /// 带机密的跨设备迁移走云同步的端到端口令(§13-C)。导入时文件里没有这两项就保留本机已填的(见 <c>SettingsTransfer.Merge</c>)。
    /// 没设端到端口令的云同步同样不带(推送前 <c>SettingsTransfer.WithoutSecrets</c>),拉取时按导入同一口径保留本机的
    /// (<c>SettingsTransfer.KeepLocalSecrets</c>)。
    /// </remarks>
    public static IReadOnlyList<(string Block, string Field)> SecretFields { get; } =
    [
        ("proxy", "password"),
        ("security", "webhookUrl"),
    ];

    /// <summary>
    /// 运行状态(分组 JSON 名, 字段 JSON 名):不是用户的设置,只是借设置文档存一下。
    /// 导出不写、导入保留本机的、审计不算 —— 每次退出都会改窗口尺寸,记成「外观改了」只会淹没真正的改动。
    /// </summary>
    public static IReadOnlyList<(string Block, string Field)> StateFields { get; } =
    [
        ("general", "lastOpenProfileIds"),
        // 主密码开没开的镜像:事实来源是包裹格式本身,这个字段只给界面看 —— 不随导出走、导入与同步不改本机的。
        ("general", "masterPasswordProtection"),
        ("appearance", "lastWindowWidth"),
        ("appearance", "lastWindowHeight"),
        ("appearance", "lastWindowMaximized"),
        ("appearance", "lastWindowX"),
        ("appearance", "lastWindowY"),
        ("appearance", "aiPanelWidth"),
        ("appearance", "aiPanelPinned"),
    ];

    /// <summary>
    /// 一次性迁移标记(分组 JSON 名, 字段 JSON 名):<see cref="AppSettings.Normalize" /> 打上之后就不再变,
    /// 不是用户的选择,审计不算。导出照常带着 —— 少了它,导入时会把用户已经选过的值再迁移一遍。
    /// </summary>
    public static IReadOnlyList<(string Block, string Field)> MigrationFields { get; } =
    [
        ("general", "autoDownloadUpdatesOptInMigrated"),
        ("security", "recordingOptInMigrated"),
        ("security", "fingerprintChangeDefaultMigrated"),
        ("security", "firstFingerprintDefaultMigrated"),
        ("proxy", "defaultsMigrated"),
        ("terminalBehavior", "foldGuideLineMigrated"),
    ];

    /// <summary>
    /// 两份设置之间哪几类变了(按界面顺序)。运行状态与迁移标记不算 —— 审计「设置变更」用它,
    /// 只记哪一块变了、不记值。
    /// </summary>
    /// <param name="before">改之前。</param>
    /// <param name="after">改之后。</param>
    /// <returns>变了的分类;一类都没变时为空。</returns>
    public static IReadOnlyList<SettingsCategory> Changed(AppSettings before, AppSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        JsonObject a = Comparable(before);
        JsonObject b = Comparable(after);
        return
        [
            .. All.Where(c => c.TopLevel.Prepend(c.Block).Any(key => !JsonNode.DeepEquals(a[key], b[key])))
        ];
    }

    private static readonly JsonSerializerOptions DiffOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>设置写成 JSON,去掉运行状态与迁移标记,留下拿来比的部分。</summary>
    private static JsonObject Comparable(AppSettings settings)
    {
        JsonObject root = JsonSerializer.SerializeToNode(settings, DiffOptions)!.AsObject();
        foreach ((string block, string field) in StateFields.Concat(MigrationFields))
        {
            (root[block] as JsonObject)?.Remove(field);
        }
        return root;
    }

    /// <summary>按标识找分类;认不出时为 null(更新版本写的文件里可能有本版本不认识的分类)。</summary>
    /// <param name="id">分类标识。</param>
    /// <returns>分类,或 null。</returns>
    public static SettingsCategory? Find(string id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>把几个分类的名称连成一句(按界面顺序,用界面语言的列举分隔符)。</summary>
    /// <param name="categories">分类。</param>
    /// <returns>如「外观、终端」。</returns>
    public static string JoinLabels(IEnumerable<SettingsCategory> categories) =>
        string.Join(Strings.Get("SetTransfer_ListSeparator"),
            All.Where(c => categories.Any(x => x.Id == c.Id)).Select(c => c.Label));
}
