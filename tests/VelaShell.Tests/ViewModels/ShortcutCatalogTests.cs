using System.Globalization;
using VelaShell.Core.Resources;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 快捷键总表的守护:<see cref="ShortcutCatalog" /> 是设置页与
/// <c>velashell-docs</c> 的 <c>zh/host/快捷键参考.md</c> 的共同来源,这里保证三者不会各说各话。
/// </summary>
/// <remarks>
/// <para>
/// 快捷键最容易腐坏的地方不是代码,而是「加了绑定却没人记得改表」——
/// 界面照常工作,只有参考页和文档在悄悄说谎,而说谎的参考页比没有参考页更糟。
/// 全局键位的事实来源是出厂表 <see cref="ShortcutBindings" />(主窗口按它登记 KeyBindings),
/// 这里要求它的每一条都在总表里出现;文档同理:每一条目录条目都必须能在文档表格里找到同名同键的一行。
/// </para>
/// <para>
/// 文档在 2026-08-30 那次「文档搬到 velashell-docs」里迁走了,本仓库不再有 <c>docs/</c> ——
/// 当时漏改了这里,于是 <see cref="Doc_ListsEveryCatalogEntry" /> 从那天起在任何一次干净检出上
/// 都必然失败(找不到路径),这条守卫等于哑了几个月。现在改为按
/// <see cref="DocumentationPath" /> 去找并排检出的文档仓库;找不到就报 Inconclusive,
/// 而不是把失败当常态 —— 常年红着的用例和没有用例是一回事。
/// </para>
/// </remarks>
[TestClass]
public class ShortcutCatalogTests
{
    /// <summary>
    /// <c>MainWindow.axaml</c> 里不许再写死 <c>KeyBinding</c>:全局键位一律进出厂表,
    /// 由代码按当前键位表登记 —— 写死的那一条用户在设置里改不掉、解不了绑,#551 就又回来了。
    /// </summary>
    [TestMethod]
    public void MainWindowAxaml_HasNoHardcodedKeyBindings()
    {
        string axaml = File.ReadAllText(Path.Combine(SourceRoot(), "VelaShell", "Views", "MainWindow.axaml"));

        Assert.IsFalse(axaml.Contains("<KeyBinding", StringComparison.Ordinal),
                       "MainWindow.axaml 里出现了写死的 KeyBinding。全局键位请加进 Services/ShortcutKeymap.cs 的 "
                       + "ShortcutBindings,再在 ShortcutCatalog 里用 Bound 引用它。");
    }

    /// <summary>
    /// 出厂表里每一条可自定义键位都必须在总表里<b>恰好</b>出现一次:漏了,快捷键页上就改不到它;
    /// 重了,改一处另一处跟着变,看起来像两条不同的键位。
    /// </summary>
    [TestMethod]
    public void EveryBinding_AppearsInCatalogExactlyOnce()
    {
        List<string> listed = [.. ShortcutCatalog.Flatten(ShortcutCatalog.Build())
            .Where(item => item.IsEditable)
            .Select(item => item.BindingId!)];

        List<string> missing = [.. ShortcutBindings.All.Select(binding => binding.Id).Where(id => !listed.Contains(id))];
        List<string> repeated = [.. listed.GroupBy(id => id).Where(ids => ids.Count() > 1).Select(ids => ids.Key)];

        Assert.IsEmpty(missing, "以下可自定义键位没出现在 ShortcutCatalog 里:\n  " + string.Join("\n  ", missing));
        Assert.IsEmpty(repeated, "以下可自定义键位在 ShortcutCatalog 里出现了不止一次:\n  " + string.Join("\n  ", repeated));
    }

    /// <summary>
    /// 出厂键位不许再新占会从终端手里抢走按键的组合(#551)。
    /// </summary>
    /// <remarks>
    /// 窗口级键位由 KeyboardDevice 在分发路由事件之前就匹配掉,终端控件根本收不到。
    /// <c>Ctrl+K</c> 当年只是命令面板的一个别名,却让 nano 的剪切行、bash 的删到行尾整条失灵。
    /// 判定用 <see cref="ShortcutGestures.TerminalBytesLost" />,直接问终端的编码器,与真正发往远端的字节同源。
    /// 名单里是历史遗留的几条 —— 改出厂值要动用户的肌肉记忆,另行决定;现在至少可以在设置里解绑。
    /// <b>名单只减不增</b>,而且每一条都必须真的还在抢键,否则说明出厂值改过了,该从名单里删掉。
    /// </remarks>
    [TestMethod]
    public void DefaultBindings_DoNotTakeNewTerminalKeys()
    {
        HashSet<string> legacy =
        [
            "session.new", "session.new.tab", "session.close", "app.palette", "view.sidebar", "view.zoom.out",
            "search.terminal",
        ];
        List<string> taken = [.. ShortcutBindings.All
            .Where(binding => !legacy.Contains(binding.Id) && ShortcutGestures.TerminalBytesLost(binding.Default) is not null)
            .Select(binding => $"{binding.Id}({binding.DefaultGesture})")];
        List<string> stale = [.. ShortcutBindings.All
            .Where(binding => legacy.Contains(binding.Id) && ShortcutGestures.TerminalBytesLost(binding.Default) is null)
            .Select(binding => binding.Id)];

        Assert.IsEmpty(taken,
                       "以下出厂键位会把一个按键从终端手里抢走(远端程序的对应键位从此失灵),"
                       + "请改用 Ctrl+Shift / Ctrl+Alt 组合:\n  " + string.Join("\n  ", taken));
        Assert.IsEmpty(stale, "以下条目已经不再抢键,请从名单里删掉:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// 改键时拦下的固定键位,每一条都得真在快捷键页上(按动作名对),
    /// 被拒时告诉用户「被谁占着」才说得通;也防止固定键位改了名、这张表却还指着旧的。
    /// </summary>
    [TestMethod]
    public void ReservedGestures_AreFixedRowsInCatalog()
    {
        HashSet<string> fixedLabels = [.. ShortcutCatalog.Flatten(ShortcutCatalog.Build())
            .Where(item => !item.IsEditable)
            .Select(item => item.Label)];

        List<string> missing = [.. ShortcutGestures.ReservedGestures(isMacOS: true)
            .Where(reserved => !fixedLabels.Contains(Strings.Get(reserved.LabelKey)))
            .Select(reserved => $"{reserved.Gesture} → {reserved.LabelKey}")];

        Assert.IsEmpty(missing, "以下固定键位在快捷键页里找不到同名的固定行:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// 同一分组里不得出现「动作名 + 键位」完全相同的两行 —— 那只会是复制粘贴的残留。
    /// 跨分组重名是允许的(Esc 在多处都关东西),同名不同键也是允许的
    /// (翻页有 PageUp 与 Shift+PageUp 两行,条件不同)。
    /// </summary>
    [TestMethod]
    public void Catalog_HasNoDuplicateRowsWithinAGroup()
    {
        List<string> duplicates = [.. ShortcutCatalog.Build()
            .SelectMany(group => group.Items.Select(item => $"{group.Title} / {item.Label} / {Combo(item)}"))
            .GroupBy(row => row, StringComparer.Ordinal)
            .Where(rows => rows.Count() > 1)
            .Select(rows => rows.Key)
            .Order(StringComparer.Ordinal)];

        Assert.IsEmpty(duplicates, "总表里有重复行:\n" + string.Join("\n", duplicates.Select(row => $"  {row}")));
    }

    /// <summary>
    /// 每条文案都必须真的取到译文。<c>Strings.Get</c> 取不到时会原样回退成键名,
    /// 设置页于是显示 "Sc_OpenLink" 这种东西 —— 静默失败,只能靠这里拦。
    /// </summary>
    [TestMethod]
    public void Catalog_HasNoUnresolvedResourceKeys()
    {
        ShortcutGroup[] groups = ShortcutCatalog.Build();
        List<string> unresolved =
        [
            .. groups.Select(group => group.Title)
                .Concat(ShortcutCatalog.Flatten(groups).Select(item => item.Label))
                .Concat(ShortcutCatalog.Flatten(groups).Select(item => item.Note ?? string.Empty))
                .Where(LooksLikeResourceKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.IsEmpty(unresolved,
                       "以下文案没取到译文(资源里缺键,界面会直接显示键名):\n" +
                       string.Join("\n", unresolved.Select(key => $"  {key}")));
    }

    /// <summary>
    /// 文档必须与总表逐条对齐:每个目录条目都要在 <c>zh/host/快捷键参考.md</c> 的表格里
    /// 找到「动作名 + 键位」都相同的一行。失败信息直接给出可粘贴的 Markdown 行,
    /// 补文档不用再手抄一遍。
    /// </summary>
    /// <remarks>
    /// 文档在另一个仓库(<c>VelaShell/velashell-docs</c>),所以这条只在文档仓库就在手边时才跑。
    /// 那份文档的「维护约定」一节明写着由本用例把关,把它删掉等于让文档那句话变成空头承诺。
    /// </remarks>
    [TestMethod]
    public void Doc_ListsEveryCatalogEntry()
    {
        if (DocumentationPath() is not { } path)
        {
            Assert.Inconclusive(
                "没找到文档仓库,跳过文档比对。把 VelaShell/velashell-docs 检出到本仓库的同级目录"
                + $"(即 {Path.Combine(Path.GetDirectoryName(RepoRoot()) ?? "..", DocsRepositoryName)}),"
                + $"或用环境变量 {DocsDirectoryVariable} 指向它。");
            return;
        }

        // 文档以简体中文书写,取词文化必须钉死,否则本机语言一变整测失败。
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new("zh-Hans");
            string doc = File.ReadAllText(path);
            List<string> missing = [.. ShortcutCatalog.Build()
                .SelectMany(group => group.Items)
                .Where(item => !DocHasRow(doc, item))
                .Select(item => $"| {item.Label} | `{Combo(item)}` | {item.Note ?? "—"} |")
                .Distinct(StringComparer.Ordinal)];

            Assert.IsEmpty(missing,
                           $"{DocRelativePath} 缺少以下条目(新增快捷键必须同步文档,直接粘贴下面这几行):\n" +
                           string.Join("\n", missing));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>文档里是否有一行同时含该条目的动作名与键位(表格列序无关)。</summary>
    private static bool DocHasRow(string doc, ShortcutItem item)
    {
        string combo = $"`{Combo(item)}`";
        return doc.Split('\n')
                  .Any(line => line.Contains($"| {item.Label} |", StringComparison.Ordinal)
                            && line.Contains(combo, StringComparison.Ordinal));
    }

    private static string Combo(ShortcutItem item) => string.Join('+', item.Keys);

    /// <summary>形如 Sc_Xxx / Cmd_Xxx / SetVm_Xxx 的裸键名 —— 只可能是取词失败的回退值。</summary>
    private static bool LooksLikeResourceKey(string text) =>
        text.StartsWith("Sc_", StringComparison.Ordinal)
        || text.StartsWith("Cmd_", StringComparison.Ordinal)
        || text.StartsWith("SetVm_", StringComparison.Ordinal);

    /// <summary>文档仓库的目录名(与本仓库并排检出时)。</summary>
    private const string DocsRepositoryName = "velashell-docs";

    /// <summary>指向文档仓库的环境变量;并排检出之外的布局用它。</summary>
    private const string DocsDirectoryVariable = "VELASHELL_DOCS_DIR";

    /// <summary>快捷键参考在文档仓库里的相对路径。</summary>
    private static readonly string DocRelativePath = Path.Combine("zh", "host", "快捷键参考.md");

    /// <summary>
    /// 定位快捷键参考文档:先看环境变量,再看与本仓库并排的检出。都没有就返回 null(用例跳过)。
    /// </summary>
    private static string? DocumentationPath()
    {
        string? configured = Environment.GetEnvironmentVariable(DocsDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string fromVariable = Path.Combine(configured, DocRelativePath);
            return File.Exists(fromVariable) ? fromVariable : null;
        }
        string? parent = Path.GetDirectoryName(RepoRoot());
        if (parent is null)
        {
            return null;
        }
        string sibling = Path.Combine(parent, DocsRepositoryName, DocRelativePath);
        return File.Exists(sibling) ? sibling : null;
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根目录(找不到 VelaShell.slnx)。");
    }

    private static string SourceRoot() => Path.Combine(RepoRoot(), "src");
}
