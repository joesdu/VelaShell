using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Layouts;
using VelaShell.Core.Resources;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Services.Layouts;

namespace VelaShell.ViewModels;

/// <summary>布局模板的增删改与打开要问用户的那几句话(由视图层按属主窗口实现)。</summary>
public interface ILayoutTemplateDialogs
{
    /// <summary>问一个名字;取消返回 null。</summary>
    /// <param name="title">弹窗标题。</param>
    /// <param name="initialValue">输入框里预填的值。</param>
    /// <param name="message">输入框上方的说明;可空。</param>
    /// <returns>用户输入的名字。</returns>
    Task<string?> PromptNameAsync(string title, string initialValue, string? message);

    /// <summary>确认一件事。</summary>
    /// <param name="title">弹窗标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="confirmText">确认按钮的文字。</param>
    /// <param name="danger">确认按钮是否按危险操作渲染(删除)。</param>
    /// <returns>用户点了确认返回 <see langword="true" />。</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger);

    /// <summary>多选一(导入撞上已有模板时:替换 / 另存 / 取消)。</summary>
    /// <param name="title">弹窗标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="choices">按钮文字,按顺序排;第一个是默认动作。</param>
    /// <param name="cancelResult">按 Esc 关掉时返回的下标。</param>
    /// <returns>被点的按钮下标。</returns>
    Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> choices, int cancelResult);

    /// <summary>选导出文件的保存位置;取消返回 null。</summary>
    /// <param name="suggestedFileName">建议的文件名。</param>
    /// <returns>本机路径。</returns>
    Task<string?> PickExportPathAsync(string suggestedFileName);

    /// <summary>选要导入的文件;取消返回 null。</summary>
    /// <returns>本机路径。</returns>
    Task<string?> PickImportPathAsync();
}

/// <summary>模板列表里的一行。</summary>
/// <param name="template">模板。</param>
public sealed class LayoutTemplateItemViewModel(LayoutTemplate template)
{
    /// <summary>背后的模板。</summary>
    public LayoutTemplate Template { get; } = template;

    /// <summary>模板名。</summary>
    public string Name => Template.Name;

    /// <summary>模板里的标签数。</summary>
    public int DocumentCount { get; } = LayoutTree.Documents(template.Root).Count();

    /// <summary>「N 个标签 · M 个窗格」。</summary>
    public string Summary => Strings.Format("LayoutTemplate_Summary", DocumentCount, LayoutTree.GroupCount(Template.Root));
}

/// <summary>
/// 命名布局模板:把当前的分屏存成模板、改名、覆盖、删除,以及打开一个模板;
/// 外加「恢复会话」顺带的那一份「退出时的布局」。
/// </summary>
/// <remarks>
/// <para>
/// 入口有三处,行为同一套:标题栏的「布局模板」下拉、命令面板(两条命令 + 每个模板一条)、
/// 「管理布局模板」窗口。
/// </para>
/// <para>
/// 存进模板的只有<b>能重新打开</b>的标签:已保存的连接(按配置 Id)与本地终端(按 shell 类型)。
/// 插件面板(AI 聊天等)、快速连接与外部拉起的临时连接(配置不在库里)跳过,存完之后如实说出少了哪几个。
/// </para>
/// </remarks>
public sealed class LayoutTemplatesViewModel : ReactiveObject
{
    /// <summary>打开的标签数超过这个值先确认(与资源管理器的整组打开同一个阈值)。</summary>
    public const int OpenManyConfirmThreshold = 10;

    /// <summary>提示里最多点名几个标签,其余用省略号。</summary>
    private const int MaxNamedInNotice = 5;

    /// <summary>导入文件的大小上限(与设置导入同一个数)。</summary>
    internal const long MaxImportBytes = 4L * 1024 * 1024;

    private readonly LayoutTemplateStore _store;
    private readonly DockWorkspace _layout;
    private readonly LayoutTemplateLauncher _launcher;
    private readonly Func<DockDocument, LayoutDocument?> _describe;
    private readonly Func<Guid, Task<bool>> _profileExists;
    private readonly ToastHostViewModel _toasts;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<LayoutTemplate> _templates = [];

    /// <summary>装配布局模板的视图模型。</summary>
    /// <param name="store">模板的持久化。</param>
    /// <param name="layout">停靠工作区。</param>
    /// <param name="opener">查配置、开标签的那一面。</param>
    /// <param name="describe">一个文档在模板里记成什么(见 <see cref="LayoutDocumentDescriber" />)。</param>
    /// <param name="profileExists">一条配置是不是还在库里(临时连接不在)。</param>
    /// <param name="toasts">浮层提示。</param>
    /// <param name="now">取当前时间;单测可注入。</param>
    public LayoutTemplatesViewModel(
        LayoutTemplateStore store,
        DockWorkspace layout,
        ILayoutDocumentOpener opener,
        Func<DockDocument, LayoutDocument?> describe,
        Func<Guid, Task<bool>> profileExists,
        ToastHostViewModel toasts,
        Func<DateTimeOffset>? now = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
        _profileExists = profileExists ?? throw new ArgumentNullException(nameof(profileExists));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _launcher = new(layout, opener ?? throw new ArgumentNullException(nameof(opener)));
        // 云同步把别的设备的模板写进来了(可能在后台线程):回到界面线程重读一遍列表。
        // 自己的增删改不走这条,它们当场就改了内存里的列表。
        _store.SyncApplied += (_, _) => Dispatcher.UIThread.Post(() => FireAndForget.Run(LoadAsync));
    }

    /// <summary>全部模板,按名字的自然顺序。</summary>
    public ObservableCollection<LayoutTemplateItemViewModel> Templates { get; } = [];

    /// <summary>有没有模板(空列表的提示用)。</summary>
    public bool HasTemplates => Templates.Count > 0;

    /// <summary>
    /// 标题栏下拉与命令面板用的对话框(由主窗口装上)。管理窗口开着时它把自己的那份传进各个方法,
    /// 弹框才会叠在它上面而不是主窗口上。
    /// </summary>
    public ILayoutTemplateDialogs? Dialogs { get; set; }

    /// <summary>请求打开「管理布局模板」窗口(由主窗口承接:视图层才建得了窗口)。</summary>
    public event Action? ManagerRequested;

    /// <summary>打开「管理布局模板」窗口。</summary>
    public void OpenManager() => ManagerRequested?.Invoke();

    /// <summary>从存储读入模板列表。</summary>
    /// <returns>表示异步操作的任务。</returns>
    public async Task LoadAsync()
    {
        IReadOnlyList<LayoutTemplate> loaded = await _store.LoadAllAsync().ConfigureAwait(true);
        _templates.Clear();
        _templates.AddRange(loaded);
        Publish();
    }

    // ---- 保存 / 覆盖 / 改名 / 删除 ----

    /// <summary>把当前布局存成一个新模板(名字撞上已有的,问一句要不要替换它)。</summary>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>存上了返回 <see langword="true" />。</returns>
    public async Task<bool> SaveCurrentAsync(ILayoutTemplateDialogs? dialogs = null)
    {
        if ((dialogs ?? Dialogs) is not { } ui)
        {
            return false;
        }
        (LayoutNode? root, List<string> skipped) = await CaptureCurrentAsync().ConfigureAwait(true);
        if (root is null)
        {
            _toasts.Warning(Strings.Get("LayoutTemplate_NothingToSave"));
            return false;
        }
        string? name = Normalize(await ui.PromptNameAsync(
                                     Strings.Get("LayoutTemplate_SaveTitle"),
                                     DefaultName(),
                                     Strings.Get("LayoutTemplate_NamePrompt")).ConfigureAwait(true));
        if (name is null)
        {
            return false;
        }
        LayoutTemplate? existing = FindByName(name, except: null);
        if (existing is not null
            && !await ui.ConfirmAsync(
                    Strings.Get("LayoutTemplate_ReplaceTitle"),
                    Strings.Format("LayoutTemplate_ReplaceMessage", existing.Name),
                    Strings.Get("LayoutTemplate_Replace"),
                    danger: false).ConfigureAwait(true))
        {
            return false;
        }
        DateTimeOffset now = _now();
        var template = new LayoutTemplate
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = name,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Root = root,
        };
        if (!await TrySaveAsync(template).ConfigureAwait(true))
        {
            return false;
        }
        ReportSaved(template, skipped);
        return true;
    }

    /// <summary>用当前布局覆盖一个已有的模板(名字与 Id 不变)。</summary>
    /// <param name="item">要覆盖的模板。</param>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>覆盖了返回 <see langword="true" />。</returns>
    public async Task<bool> OverwriteAsync(LayoutTemplateItemViewModel item, ILayoutTemplateDialogs? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((dialogs ?? Dialogs) is not { } ui)
        {
            return false;
        }
        (LayoutNode? root, List<string> skipped) = await CaptureCurrentAsync().ConfigureAwait(true);
        if (root is null)
        {
            _toasts.Warning(Strings.Get("LayoutTemplate_NothingToSave"));
            return false;
        }
        if (!await ui.ConfirmAsync(
                Strings.Get("LayoutTemplate_OverwriteTitle"),
                Strings.Format("LayoutTemplate_OverwriteMessage", item.Name),
                Strings.Get("LayoutTemplate_Overwrite"),
                danger: false).ConfigureAwait(true))
        {
            return false;
        }
        LayoutTemplate template = Copy(item.Template);
        template.Root = root;
        template.UpdatedAt = _now();
        template.SchemaVersion = LayoutTemplate.CurrentSchemaVersion;
        if (!await TrySaveAsync(template).ConfigureAwait(true))
        {
            return false;
        }
        ReportSaved(template, skipped);
        return true;
    }

    /// <summary>给一个模板改名;新名字与另一个模板重名时拒绝(改名不该悄悄吞掉别的模板)。</summary>
    /// <param name="item">要改名的模板。</param>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>改了返回 <see langword="true" />。</returns>
    public async Task<bool> RenameAsync(LayoutTemplateItemViewModel item, ILayoutTemplateDialogs? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((dialogs ?? Dialogs) is not { } ui)
        {
            return false;
        }
        string? name = Normalize(await ui.PromptNameAsync(
                                     Strings.Get("LayoutTemplate_RenameTitle"),
                                     item.Name,
                                     Strings.Get("LayoutTemplate_NamePrompt")).ConfigureAwait(true));
        if (name is null || string.Equals(name, item.Name, StringComparison.Ordinal))
        {
            return false;
        }
        if (FindByName(name, except: item.Template.Id) is { } other)
        {
            _toasts.Warning(Strings.Format("LayoutTemplate_NameTaken", other.Name));
            return false;
        }
        LayoutTemplate template = Copy(item.Template);
        template.Name = name;
        template.UpdatedAt = _now();
        return await TrySaveAsync(template).ConfigureAwait(true);
    }

    /// <summary>删除一个模板(先确认;只删模板,不碰它引用的连接)。</summary>
    /// <param name="item">要删的模板。</param>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>删了返回 <see langword="true" />。</returns>
    public async Task<bool> DeleteAsync(LayoutTemplateItemViewModel item, ILayoutTemplateDialogs? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((dialogs ?? Dialogs) is not { } ui
            || !await ui.ConfirmAsync(
                    Strings.Get("LayoutTemplate_DeleteTitle"),
                    Strings.Format("LayoutTemplate_DeleteMessage", item.Name),
                    Strings.Get("Delete"),
                    danger: true).ConfigureAwait(true))
        {
            return false;
        }
        try
        {
            await _store.DeleteAsync(item.Template.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _toasts.Error(Strings.Format("LayoutTemplate_DeleteFailed", ex.Message));
            return false;
        }
        _templates.RemoveAll(template => template.Id == item.Template.Id);
        Publish();
        return true;
    }

    // ---- 导出 / 导入 ----

    /// <summary>把全部模板导出成一个 JSON 文件(格式见 <see cref="LayoutTemplateFile" />;不含机密)。</summary>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>写出了文件返回 <see langword="true" />。</returns>
    public async Task<bool> ExportAsync(ILayoutTemplateDialogs? dialogs = null)
    {
        if ((dialogs ?? Dialogs) is not { } ui)
        {
            return false;
        }
        if (_templates.Count == 0)
        {
            _toasts.Warning(Strings.Get("LayoutTemplate_NothingToExport"));
            return false;
        }
        string? path = await ui.PickExportPathAsync("velashell-layout-templates.json").ConfigureAwait(true);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        LayoutTemplate[] templates = [.. _templates];
        try
        {
            await File.WriteAllTextAsync(path, LayoutTemplateFile.Export(templates, _now().UtcDateTime)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _toasts.Error(Strings.Format("LayoutTemplate_ExportFailed", ex.Message));
            return false;
        }
        _toasts.Info(Strings.Format("LayoutTemplate_Exported", templates.Length, path));
        return true;
    }

    /// <summary>
    /// 从导出文件导入模板。与本机已有的是同一个(同 Id)或重名(不分大小写)的,先问一次:全部替换、全部另存为新模板,或者取消;
    /// 其余直接加进来。导入的模板引用的连接在本机不存在时提示一句(打开时照常跳过那些标签)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「替换」沿用原来那一条的 Id 与创建时间(同「存为模板」撞名时的替换),换上文件里的名字与布局;
    /// 「另存」给新 Id,名字后面补序号(<c>名字 (2)</c>)。冲突只按导入之前本机已有的那些判断 ——
    /// 文件里自己重名的几条照样都收下,与同步「重名照存」同一口径。「替换」时本机的一条只替换一次:
    /// 文件里有两条都对上它(一条同 Id、一条同名),同 Id 的替换它,另一条照「另存」收下(<see cref="ClaimReplacements" />)。
    /// </para>
    /// <para>
    /// 写进来的一律把修改时间刷成现在:导入是用户刚做的决定,随后的同步里它要胜过云端那份(包括本机删过它留下的墓碑)。
    /// </para>
    /// </remarks>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>导入了几个;取消、文件不对时为 null。</returns>
    public async Task<int?> ImportAsync(ILayoutTemplateDialogs? dialogs = null)
    {
        if ((dialogs ?? Dialogs) is not { } ui
            || await ui.PickImportPathAsync().ConfigureAwait(true) is not { Length: > 0 } path)
        {
            return null;
        }
        string json;
        try
        {
            if (new FileInfo(path).Length > MaxImportBytes)
            {
                // 模板文件是几 KB 到几百 KB 的东西;超过上限多半是选错了文件,整份读进内存再解析纯属白费。
                _toasts.Warning(Strings.Get("LayoutTemplate_ImportInvalid"));
                return null;
            }
            json = await File.ReadAllTextAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _toasts.Error(Strings.Format("LayoutTemplate_ImportFailed", ex.Message));
            return null;
        }
        if (LayoutTemplateFile.Parse(json) is not { } parsed)
        {
            _toasts.Warning(Strings.Get("LayoutTemplate_ImportInvalid"));
            return null;
        }
        // 同一个文件里同一个 Id 出现两次(手拼的文件):留修改时间较晚的那份。
        List<LayoutTemplate> incoming = [.. parsed.GroupBy(t => t.Id).Select(g => g.OrderByDescending(t => t.UpdatedAt).First())];
        if (incoming.Count == 0)
        {
            _toasts.Warning(Strings.Get("LayoutTemplate_ImportEmpty"));
            return null;
        }

        LayoutTemplate[] before = [.. _templates];
        var conflicts = incoming.ToDictionary(t => t.Id, t => ConflictOf(t, before));
        bool keepBoth = false;
        if (conflicts.Values.Any(existing => existing is not null))
        {
            List<string> names = [.. incoming.Where(t => conflicts[t.Id] is not null).Select(t => t.Name)];
            int answer = await ui.ChooseAsync(
                Strings.Get("LayoutTemplate_ImportConflictTitle"),
                Strings.Format("LayoutTemplate_ImportConflictMessage", names.Count, JoinNames(names)),
                [Strings.Get("LayoutTemplate_ImportReplace"), Strings.Get("LayoutTemplate_ImportKeepBoth"), Strings.Cancel],
                cancelResult: 2).ConfigureAwait(true);
            if (answer is not (0 or 1))
            {
                return null;
            }
            keepBoth = answer == 1;
        }
        Dictionary<Guid, LayoutTemplate> replaces = keepBoth ? [] : ClaimReplacements(incoming, conflicts);

        DateTimeOffset now = _now();
        var imported = new List<LayoutTemplate>();
        // 替换的先存:被替换的那条换上文件里的名字之后,原来的名字多半就空出来了,没认领到的那条不必再补序号。
        foreach (LayoutTemplate template in incoming.OrderBy(t => replaces.ContainsKey(t.Id) ? 0 : 1))
        {
            LayoutTemplate copy = Copy(template);
            copy.UpdatedAt = now;
            if (copy.CreatedAt == default)
            {
                copy.CreatedAt = now;
            }
            if (conflicts[template.Id] is not null)
            {
                if (replaces.TryGetValue(template.Id, out LayoutTemplate? replaced))
                {
                    copy.Id = replaced.Id;
                    copy.CreatedAt = replaced.CreatedAt;
                }
                else if (keepBoth)
                {
                    copy.Id = Guid.NewGuid();
                    copy.Name = UniqueCopyName(template.Name);
                    copy.CreatedAt = now;
                }
                else if (FindByName(copy.Name, except: null) is not null)
                {
                    // 要替换的那条已经被别的认领了,这一条照「另存」收下:自己的 Id 本机没有(有的话冲突就按 Id 算了),留着 ——
                    // 别的设备同步过来的同一个模板还认得出它;名字仍被占着才补序号。
                    copy.Name = UniqueCopyName(template.Name);
                }
            }
            if (!await TrySaveAsync(copy).ConfigureAwait(true))
            {
                return imported.Count;
            }
            imported.Add(copy);
        }

        int missing = await CountWithMissingProfilesAsync(imported).ConfigureAwait(true);
        if (missing > 0)
        {
            _toasts.Warning(Strings.Format("LayoutTemplate_ImportedMissing", imported.Count, missing));
        }
        else
        {
            _toasts.Info(Strings.Format("LayoutTemplate_Imported", imported.Count));
        }
        return imported.Count;
    }

    // ---- 打开 ----

    /// <summary>
    /// 打开一个模板:按它的分屏新开连接、排好;配置已删除的标签跳过并提示,其余照开。
    /// </summary>
    /// <param name="item">要打开的模板。</param>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />(只在标签多时用来确认)。</param>
    /// <returns>全部标签的打开流程都结束时完成;用户在确认框上取消时返回 null。</returns>
    public async Task<LayoutOpenResult?> OpenAsync(LayoutTemplateItemViewModel item, ILayoutTemplateDialogs? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.DocumentCount > OpenManyConfirmThreshold
            && (dialogs ?? Dialogs) is { } ui
            && !await ui.ConfirmAsync(
                    Strings.Get("Tree_OpenManyTitle"),
                    Strings.Format("Tree_OpenManyConfirm", item.DocumentCount),
                    Strings.Get("Tree_OpenManyAction"),
                    danger: false).ConfigureAwait(true))
        {
            return null;
        }
        LayoutOpenResult result = await _launcher.OpenAsync(item.Template.Root).ConfigureAwait(true);
        if (result.Opened == 0)
        {
            _toasts.Warning(Strings.Format("LayoutTemplate_NothingToOpen", item.Name));
        }
        else if (result.Missing.Count > 0)
        {
            _toasts.Warning(Strings.Format(
                "LayoutTemplate_OpenSkipped",
                item.Name,
                result.Missing.Count,
                JoinNames(result.Missing.Select(DescribeMissing))));
        }
        return result;
    }

    // ---- 「恢复会话」的那一份 ----

    /// <summary>
    /// 拍下「退出时的布局」:只记此刻在线的标签(见 <see cref="LayoutDocumentDescriber.IsLive" />)。
    /// 同步、不查库 —— 退出路径上只做本地写;临时连接的配置不在库里,下次启动时自然对不上、被静默跳过。
    /// </summary>
    /// <returns>布局;一个在线的标签都没有时为 null。</returns>
    public LayoutTemplate? CaptureSessionLayout()
    {
        LayoutNode? root = DockLayoutSnapshot.Capture(
            _layout,
            document => LayoutDocumentDescriber.IsLive(document) ? _describe(document) : null);
        if (root is null)
        {
            return null;
        }
        DateTimeOffset now = _now();
        return new() { Id = Guid.Empty, Name = string.Empty, CreatedAt = now, UpdatedAt = now, Root = root };
    }

    /// <summary>把「退出时的布局」写进存储(null = 清掉旧的)。</summary>
    /// <param name="layout">要记下的布局。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task SaveSessionLayoutAsync(LayoutTemplate? layout) => _store.SaveLastSessionAsync(layout);

    /// <summary>
    /// 启动时按「退出时的布局」恢复会话。配置已删除的标签静默跳过(其中多半是没入库的临时连接,
    /// 启动时冒一句「少了一个」只会让人摸不着头脑)。
    /// </summary>
    /// <returns>确实还原出了标签返回 <see langword="true" />;没有可还原的布局时返回 <see langword="false" />,调用方退回逐条重连。</returns>
    public async Task<bool> RestoreSessionLayoutAsync()
    {
        if (await _store.LoadLastSessionAsync().ConfigureAwait(true) is not { Root: { } root })
        {
            return false;
        }
        LayoutOpenResult result = await _launcher.OpenAsync(root).ConfigureAwait(true);
        if (result.Missing.Count > 0)
        {
            Trace.WriteLine($"[LayoutTemplates] 恢复上次的布局时跳过了 {result.Missing.Count} 个标签。");
        }
        return result.Opened > 0;
    }

    // ---- 内部 ----

    /// <summary>
    /// 拍下当前布局,并去掉配置不在库里的标签(快速连接、外部拉起的临时配置)。
    /// </summary>
    /// <returns>布局树,以及没能存进去的标签标题。</returns>
    internal async Task<(LayoutNode? Root, List<string> Skipped)> CaptureCurrentAsync()
    {
        var skippedDocuments = new List<DockDocument>();
        LayoutNode? root = DockLayoutSnapshot.Capture(_layout, _describe, skippedDocuments);
        List<string> skipped = [.. skippedDocuments.Select(document => document.Title)];
        if (root is null)
        {
            return (null, skipped);
        }
        var exists = new Dictionary<Guid, bool>();
        foreach (Guid id in LayoutTree.Documents(root)
                                      .SelectMany(document => (Guid?[])[document.ProfileId, document.SecondProfileId])
                                      .OfType<Guid>()
                                      .Distinct())
        {
            try
            {
                exists[id] = await _profileExists(id).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.WriteLine($"[LayoutTemplates] 查询配置 {id} 失败:{ex.Message}");
                exists[id] = false;
            }
        }
        var dropped = new List<LayoutDocument>();
        root = LayoutTree.Prune(
            root,
            document => (document.ProfileId is not { } first || exists.GetValueOrDefault(first))
                        && (document.SecondProfileId is not { } second || exists.GetValueOrDefault(second)),
            dropped);
        skipped.AddRange(dropped.Select(document => document.Title));
        root?.Proportion = null;
        return (root, skipped);
    }

    private async Task<bool> TrySaveAsync(LayoutTemplate template)
    {
        try
        {
            await _store.SaveAsync(template).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _toasts.Error(Strings.Format("LayoutTemplate_SaveFailed", ex.Message));
            return false;
        }
        _templates.RemoveAll(existing => existing.Id == template.Id);
        _templates.Add(template);
        Publish();
        return true;
    }

    private void ReportSaved(LayoutTemplate template, List<string> skipped)
    {
        int documents = LayoutTree.Documents(template.Root).Count();
        if (skipped.Count == 0)
        {
            _toasts.Info(Strings.Format("LayoutTemplate_Saved", template.Name, documents, LayoutTree.GroupCount(template.Root)));
            return;
        }
        _toasts.Warning(Strings.Format("LayoutTemplate_SavedPartially", template.Name, skipped.Count, JoinNames(skipped)));
    }

    private void Publish()
    {
        IReadOnlyList<LayoutTemplate> sorted = LayoutTemplateStore.Sort(_templates);
        _templates.Clear();
        _templates.AddRange(sorted);
        Templates.Clear();
        foreach (LayoutTemplate template in sorted)
        {
            Templates.Add(new(template));
        }
        this.RaisePropertyChanged(nameof(HasTemplates));
    }

    private LayoutTemplate? FindByName(string name, Guid? except) =>
        _templates.FirstOrDefault(template =>
            template.Id != except && string.Equals(template.Name, name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>默认名「布局 N」:N 取第一个没被占用的序号。</summary>
    private string DefaultName()
    {
        for (int n = _templates.Count + 1; ; n++)
        {
            string candidate = Strings.Format("LayoutTemplate_DefaultName", n);
            if (FindByName(candidate, except: null) is null)
            {
                return candidate;
            }
        }
    }

    private static string? Normalize(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }
        return trimmed.Length > LayoutTemplate.MaxNameLength ? trimmed[..LayoutTemplate.MaxNameLength] : trimmed;
    }

    private static LayoutTemplate Copy(LayoutTemplate template) => new()
    {
        SchemaVersion = template.SchemaVersion,
        Id = template.Id,
        Name = template.Name,
        CreatedAt = template.CreatedAt,
        UpdatedAt = template.UpdatedAt,
        Root = template.Root,
        ExtensionData = template.ExtensionData,
    };

    /// <summary>导入的一项撞上了本机哪一条:同 Id 优先,其次同名(不分大小写);都没有为 null。</summary>
    private static LayoutTemplate? ConflictOf(LayoutTemplate incoming, IReadOnlyList<LayoutTemplate> local) =>
        local.FirstOrDefault(template => template.Id == incoming.Id)
        ?? local.FirstOrDefault(template =>
            string.Equals(template.Name, incoming.Name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// 「替换」时谁替换本机的哪一条(导入项 Id → 本机那一条):本机的一条只能被替换一次。
    /// </summary>
    /// <remarks>
    /// 冲突是逐条对着导入前的本机列表算的,文件里同 Id 的那条与同名的另一条可能都指向本机同一条
    /// (在别的设备上把它改了名、又新建了一个同名的,一起导出)。都换成它的 Id,后存的就把先存的盖掉:丢一条,
    /// 提示里却说导入了两条。同 Id 的先认领(它就是本机这一条,只是改过名);同名的按文件顺序认领剩下的。
    /// 没认领到的不在结果里,照「另存」收下。
    /// </remarks>
    private static Dictionary<Guid, LayoutTemplate> ClaimReplacements(
        List<LayoutTemplate> incoming,
        Dictionary<Guid, LayoutTemplate?> conflicts)
    {
        var replaces = new Dictionary<Guid, LayoutTemplate>();
        var claimed = new HashSet<Guid>();
        foreach (LayoutTemplate template in incoming)
        {
            if (conflicts[template.Id] is { } local && local.Id == template.Id && claimed.Add(local.Id))
            {
                replaces[template.Id] = local;
            }
        }
        foreach (LayoutTemplate template in incoming)
        {
            if (conflicts[template.Id] is { } local && local.Id != template.Id && claimed.Add(local.Id))
            {
                replaces[template.Id] = local;
            }
        }
        return replaces;
    }

    /// <summary>另存时的名字:「名字 (2)」「名字 (3)」…取第一个没被占用的;超长时截短原名、保住序号。</summary>
    private string UniqueCopyName(string name)
    {
        for (int n = 2; ; n++)
        {
            string candidate = Strings.Format("LayoutTemplate_CopyName", name, n);
            if (candidate.Length > LayoutTemplate.MaxNameLength)
            {
                int keep = Math.Max(1, name.Length - (candidate.Length - LayoutTemplate.MaxNameLength));
                candidate = Strings.Format("LayoutTemplate_CopyName", name[..keep].TrimEnd(), n);
            }
            if (FindByName(candidate, except: null) is null)
            {
                return candidate;
            }
        }
    }

    /// <summary>有几个模板引用了本机不存在的连接配置(打开时那些标签会被跳过)。</summary>
    private async Task<int> CountWithMissingProfilesAsync(IEnumerable<LayoutTemplate> templates)
    {
        var exists = new Dictionary<Guid, bool>();
        int missing = 0;
        foreach (LayoutTemplate template in templates)
        {
            foreach (Guid id in LayoutTree.Documents(template.Root)
                                          .SelectMany(document => (Guid?[])[document.ProfileId, document.SecondProfileId])
                                          .OfType<Guid>()
                                          .Distinct())
            {
                if (!exists.TryGetValue(id, out bool found))
                {
                    try
                    {
                        found = await _profileExists(id).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Trace.WriteLine($"[LayoutTemplates] 查询配置 {id} 失败:{ex.Message}");
                        found = false;
                    }
                    exists[id] = found;
                }
                if (!found)
                {
                    missing++;
                    break;
                }
            }
        }
        return missing;
    }

    private static string DescribeMissing(LayoutMissingDocument missing)
    {
        string title = string.IsNullOrWhiteSpace(missing.Title) ? Strings.Get("LayoutTemplate_Untitled") : missing.Title;
        return missing.Reason switch
        {
            LayoutMissingReason.ProfileDeleted => Strings.Format("LayoutTemplate_MissingProfile", title),
            LayoutMissingReason.ShellUnavailable => Strings.Format("LayoutTemplate_MissingShell", title),
            _ => Strings.Format("LayoutTemplate_MissingUnsupported", title),
        };
    }

    private static string JoinNames(IEnumerable<string> names)
    {
        List<string> all = [.. names.Select(name => string.IsNullOrWhiteSpace(name) ? Strings.Get("LayoutTemplate_Untitled") : name)];
        string joined = string.Join(Strings.Get("LayoutTemplate_ListSeparator"), all.Take(MaxNamedInNotice));
        return all.Count > MaxNamedInNotice ? joined + "…" : joined;
    }
}
