using System.Collections.ObjectModel;
using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// 快捷键页的一个分组。折叠态与快捷命令面板同语言:分组头是 ToggleButton,状态挂在这里。
/// </summary>
/// <param name="id">跨语言稳定的分组标识(取分组标题的资源键)—— 换语言会整表重建,靠它把折叠态搬过去。</param>
/// <param name="title">已本地化的分组标题。</param>
/// <param name="items">分组下的全部条目(不随搜索变化)。</param>
public sealed class ShortcutGroup(string id, string title, ShortcutItem[] items) : ReactiveObject
{
    /// <summary>跨语言稳定的分组标识。</summary>
    public string Id { get; } = id;

    /// <summary>已本地化的分组标题。</summary>
    public string Title { get; } = title;

    /// <summary>分组下的全部条目。</summary>
    public ShortcutItem[] Items { get; } = items;

    /// <summary>应用搜索后的可见条目;未搜索时即全量。</summary>
    public ObservableCollection<ShortcutItem> FilteredItems { get; } = [.. items];

    /// <summary>分组是否展开。默认展开 —— 参考页的首要用途是通读,折叠是用户主动收纳。</summary>
    public bool IsExpanded
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;
}

/// <summary>
/// 快捷键页的单条记录:一个功能名及其组合键序列。固定键位只读;
/// 带 <see cref="BindingId" /> 的是可自定义键位(<see cref="ShortcutBindings" />),改键、解绑后原地刷新。
/// </summary>
public sealed class ShortcutItem : ReactiveObject
{
    /// <summary>建一条记录。</summary>
    /// <param name="label">功能说明文本(本地化后的动作名)。</param>
    /// <param name="keys">
    /// 组成该快捷键的按键序列(如 ["Ctrl", "N"];鼠标手势里也可以是「双击」「滚轮」这类本地化手势名);解绑时为空。
    /// </param>
    /// <param name="note">生效条件备注(如「仅在会话已断开时」);无条件生效时为 null。</param>
    /// <param name="bindingId">可自定义键位的绑定 id;固定键位为 null。</param>
    public ShortcutItem(string label, string[] keys, string? note = null, string? bindingId = null)
    {
        Label = label;
        Note = note;
        BindingId = bindingId;
        Keys = keys;
        SearchText = BuildSearchText();
    }

    /// <summary>功能说明文本(本地化后的动作名)。</summary>
    public string Label { get; }

    /// <summary>生效条件备注;无条件生效时为 null。</summary>
    public string? Note { get; }

    /// <summary>是否有生效条件备注(模板据此决定要不要占一行备注位)。</summary>
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>可自定义键位的绑定 id;固定键位为 null。</summary>
    public string? BindingId { get; }

    /// <summary>能不能在快捷键页里改键、解绑。</summary>
    public bool IsEditable => BindingId is not null;

    /// <summary>当前的按键序列;解绑时为空数组。</summary>
    public string[] Keys
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsUnbound));
            this.RaisePropertyChanged(nameof(HasKeys));
            this.RaisePropertyChanged(nameof(ShowsUnbound));
        }
    } = [];

    /// <summary>有键帽可画(解绑、或正在录键时为 false)。</summary>
    public bool HasKeys => Keys.Length > 0 && !IsRecording;

    /// <summary>可自定义键位当前是解绑状态(这一按原样交给终端)。</summary>
    public bool IsUnbound => IsEditable && Keys.Length == 0;

    /// <summary>显示「未绑定」字样(解绑且不在录键)。</summary>
    public bool ShowsUnbound => IsUnbound && !IsRecording;

    /// <summary>与出厂键位不同(改过键或解了绑)—— 出现「恢复默认」按钮,键帽改用强调色。</summary>
    public bool IsCustomized
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 当前键位会从终端手里抢走一个按键时的提示(<see cref="ShortcutKeymap.TerminalWarning" />);不会时为 null。
    /// </summary>
    public string? Warning
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasWarning));
        }
    }

    /// <summary>是否有终端冲突提示。</summary>
    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    /// <summary>正在等用户按下新的组合键。</summary>
    public bool IsRecording
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasKeys));
            this.RaisePropertyChanged(nameof(ShowsUnbound));
        }
    }

    /// <summary>上一次录键的反馈(被拒的原因,或与谁冲突);没有为 null。</summary>
    public string? Message
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasMessage));
        }
    }

    /// <summary>是否有录键反馈要显示。</summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>
    /// 录到的手势与别的绑定冲突、等用户确认替换时暂存在这里(存储写法);没有待确认的替换为 null。
    /// </summary>
    public string? PendingGesture
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasPendingReplace));
        }
    }

    /// <summary>是否在等用户确认「替换」。</summary>
    public bool HasPendingReplace => PendingGesture is not null;

    /// <summary>
    /// 搜索匹配用的合并文本:动作名 + 键位 + 备注,一次过滤全覆盖。
    /// 键位同时收录空格与加号两种拼法 —— 用户照着键帽敲的是 "Ctrl Shift F",
    /// 照着文档敲的是 "Ctrl+Shift+F",两种都得能搜到。
    /// </summary>
    public string SearchText { get; private set; }

    /// <summary>按新的键位表刷新键帽、改动标记与终端冲突提示(改键、解绑、恢复默认之后)。</summary>
    public void Refresh(ShortcutKeymap keymap)
    {
        if (BindingId is not { } id)
        {
            return;
        }
        Keys = keymap.Keycaps(id);
        IsCustomized = keymap.IsCustomized(id);
        Warning = keymap.TerminalWarning(id);
        SearchText = BuildSearchText();
    }

    private string BuildSearchText() => $"{Label} {string.Join(' ', Keys)} {string.Join('+', Keys)} {Note}";
}

/// <summary>
/// 应用内全部快捷键的<b>唯一事实来源</b>:设置 → 快捷键页与 velashell-docs 的 <c>zh/host/快捷键参考.md</c> 都以本表为准。
/// </summary>
/// <remarks>
/// <para>
/// 每一条都逐一核对过真实绑定,不得列出未绑定的键位。可自定义的那些(<c>Bound</c>)出厂表在
/// <see cref="ShortcutBindings" />,键帽取自当前键位表;其余是固定键位,绑定分散在这几处:
/// </para>
/// <list type="bullet">
///   <item><c>Services/KeyboardShortcutService</c> —— 终端上下文 + 平台差异(macOS 用 Command);</item>
///   <item><c>VelaShell.Terminal/Input/TerminalKeyRouter</c> —— 终端控件内的剪贴板/翻页/编码分流;</item>
///   <item><c>VelaShell.Terminal/Rendering/VelaTerminalControl</c> —— 终端鼠标手势(选区、缩放、链接);</item>
///   <item><c>Views/TerminalTabView.axaml.cs</c> —— 补全弹层、断线态键位;</item>
///   <item>各视图/对话框自己的 <c>OnKeyDown</c>(命令面板、文件管理器、进程管理器、编辑器、AI 面板等)。</item>
/// </list>
/// <para>
/// <b>新增或修改快捷键时必须同步本表</b> —— <c>ShortcutCatalogTests</c> 要求出厂表里每一条都在这里恰好出现一次,
/// 并拿文档逐条比对。文案键统一用 <c>Sc_</c> 前缀;与命令面板同名的动作直接复用其 <c>Cmd_</c> 键,保证两处措辞一致。
/// </para>
/// </remarks>
public static class ShortcutCatalog
{
    private const string Ctrl = "Ctrl";
    private const string Shift = "Shift";
    private const string Alt = "Alt";

    /// <summary>按当前界面语言构建完整分组表(语言切换后需重新调用)。</summary>
    /// <param name="keymap">可自定义键位按哪份键位表显示;null = 出厂键位(文档比对与测试用)。</param>
    public static ShortcutGroup[] Build(ShortcutKeymap? keymap = null)
    {
        keymap ??= ShortcutKeymap.Default;
        return
        [
            Group("Sc_GroupGlobal",
                [
                    Bound(keymap, "session.new"),
                    Bound(keymap, "session.new.tab"),
                    Bound(keymap, "session.clone"),
                    Bound(keymap, "app.settings"),
                    Bound(keymap, "app.palette"),
                ]
            ),
            Group("Sc_GroupTabsAndPanels",
                [
                    Bound(keymap, "session.close"),
                    Bound(keymap, "session.close.all"),
                    Bound(keymap, "tab.next"),
                    Bound(keymap, "tab.prev"),
                    // 数的是**当前标签条**上的第几个,分屏后按组算。
                    .. Enumerable.Range(1, 8).Select(slot => Bound(keymap, $"tab.goto.{slot}")),
                    Bound(keymap, "tab.goto.last"),
                    Bound(keymap, "split.horizontal"),
                    Bound(keymap, "split.vertical"),
                    Bound(keymap, "pane.maximize"),
                    Item("Sc_FocusPane", [Alt, "←", "→", "↑", "↓"], "Sc_NoteSplitOnly"),
                    Bound(keymap, "view.sidebar"),
                    Bound(keymap, "tools.files"),
                    Bound(keymap, "tools.tunnel"),
                    Bound(keymap, "terminal.linegutter"),
                ]
            ),
            Group("SetVm_SectionTerminal",
                [
                    Item("Copy", [Ctrl, Shift, "C"]),
                    Item("Cmd_Paste", [Ctrl, Shift, "V"]),
                    Item("Sc_PasteShiftInsert", [Shift, "Insert"]),
                    Item("Sc_SendInterrupt", [Ctrl, "C"], "Sc_NoteCtrlCCopies"),
                    Bound(keymap, "search.terminal"),
                    Item("Sc_SearchNext", ["Enter"], "Sc_NoteSearchOpen"),
                    Item("Sc_SearchPrev", [Shift, "Enter"], "Sc_NoteSearchOpen"),
                    Item("Sc_SearchClose", ["Esc"], "Sc_NoteSearchOpen"),
                    Item("Sc_ScrollPageUp", ["PageUp"], "Sc_NoteMainScreen"),
                    Item("Sc_ScrollPageDown", ["PageDown"], "Sc_NoteMainScreen"),
                    Item("Sc_ScrollPageUp", [Shift, "PageUp"], "Sc_NoteAnyScreen"),
                    Item("Sc_ScrollPageDown", [Shift, "PageDown"], "Sc_NoteAnyScreen"),
                    // OSC 133 命令块导航。只在对端装了 shell 集成时拦截,否则原样编码下发 ——
                    // 没有标记可跳还吞掉一组按键,只会让远端程序的键位神秘失灵。
                    Item("Sc_JumpPrevPrompt", [Ctrl, Shift, "Up"], "Sc_NoteShellIntegration"),
                    Item("Sc_JumpNextPrompt", [Ctrl, Shift, "Down"], "Sc_NoteShellIntegration"),
                    Item("Sc_DeleteWord", [Ctrl, "Backspace"]),
                    Item("Sc_LineStart", [Shift, "Home"]),
                    Item("Sc_LineEnd", [Shift, "End"]),
                    Item("Sc_Reconnect", ["Enter"], "Sc_NoteDisconnected"),
                    Item("Sc_ReconnectAlt", [Ctrl, "R"], "Sc_NoteDisconnected"),
                    Item("Sc_CloseDisconnectedTab", ["Esc"], "Sc_NoteDisconnected"),
                    Bound(keymap, "edit.clear"),
                    Bound(keymap, "view.zoom.in"),
                    Bound(keymap, "view.zoom.out"),
                    Bound(keymap, "view.zoom.reset"),
                ]
            ),
            Group("Sc_GroupCompletion",
                [
                    Item("Sc_CompletionPopup", [Alt, "Enter"]),
                    Item("Sc_SuggestNext", ["Down"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestPrev", ["Up"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestAccept", ["Enter"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestDismiss", ["Esc"], "Sc_NoteSuggestOpen"),
                    // Ctrl+C(取消当前行)与点击终端正文同样收起弹层:按键/点击照常
                    // 下发给终端,只是顺手收口面板(#315)。
                    Item("Sc_SuggestDismiss", [Ctrl, "C"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestDismiss", [K("Sc_KeyLeftClick")], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestNative", ["Tab"]),
                    Item("Sc_GhostAccept", ["Right"]),
                    Item("Sc_GhostAccept", ["End"]),
                ]
            ),
            Group("Sc_GroupMouse",
                [
                    Item("Sc_OpenLink", [Ctrl, K("Sc_KeyLeftClick")]),
                    Item("Sc_SelectWord", [K("Sc_KeyDoubleClick")], "Sc_NoteRequiresSetting"),
                    Item("Sc_AppendWord", [Ctrl, Shift, K("Sc_KeyDoubleClick")]),
                    Item("Sc_ExtendSelection", [Shift, K("Sc_KeyLeftClick")]),
                    Item("Sc_BlockSelection", [Alt, K("Sc_KeyDrag")]),
                    Item("Sc_AppendSelection", [Ctrl, Shift, K("Sc_KeyDrag")]),
                    Item("Sc_AppendBlockSelection", [Ctrl, Shift, Alt, K("Sc_KeyDrag")]),
                    Item("Sc_BypassMouseReport", [Shift, K("Sc_KeyDrag")], "Sc_NoteMouseReporting"),
                    Item("Sc_RightClickPaste", [K("Sc_KeyRightClick")], "Sc_NoteOptional"),
                    Item("Sc_ZoomFont", [Ctrl, K("Sc_KeyWheel")]),
                    Item("Sc_FastScroll", [Alt, K("Sc_KeyWheel")]),
                    // 停靠区的三个鼠标手势:不写进来就只有读过源码的人知道它们存在。
                    Item("CloseTab", [K("Sc_KeyTabItem"), K("Sc_KeyMiddleClick")], "Sc_NoteNotPinned"),
                    Item("Sc_ScrollTabs", [K("Sc_KeyTabStrip"), K("Sc_KeyWheel")]),
                    Item("Dock_EqualizePanes", [K("Sc_KeySplitter"), K("Sc_KeyDoubleClick")], "Sc_NoteNeedsSplit"),
                    Item("Sc_GutterMenu", [K("Sc_KeyGutter"), K("Sc_KeyRightClick")]),
                    Item("Sc_ToggleFold", [K("Sc_KeyGutter"), K("Sc_KeyLeftClick")]),
                    Item("Sc_SelectCommandOutput", [K("Sc_KeyCommandMark"), K("Sc_KeyLeftClick")], "Sc_NoteShellIntegration"),
                ]
            ),
            // 资源管理器的多选与拖动(#571):列表本身是单选的,Ctrl / Shift 选多条、拖分组排序都是自己接的手势,
            // 不写进来就没人知道能这么用。
            Group("Sc_GroupExplorer",
                [
                    Item("Sc_ExplorerToggleSelect", [Ctrl, K("Sc_KeyLeftClick")]),
                    Item("Sc_ExplorerRangeSelect", [Shift, K("Sc_KeyLeftClick")]),
                    Item("Sc_ExplorerMoveToGroup", [K("Sc_KeyExplorerConnection"), K("Sc_KeyDrag")]),
                    Item("Sc_ExplorerReorderGroup", [K("Sc_KeyExplorerGroupRow"), K("Sc_KeyDrag")]),
                ]
            ),
            Group("Cmd_CommandPalette",
                [
                    Item("Sc_PaletteNext", ["Down"]),
                    Item("Sc_PalettePrev", ["Up"]),
                    Item("Sc_PaletteRun", ["Enter"]),
                    Item("Sc_PaletteClose", ["Esc"]),
                ]
            ),
            Group("Cmd_SftpFileManager",
                [
                    Item("Sc_EditPath", [Ctrl, "L"]),
                    Item("Sc_CommitPath", ["Enter"]),
                    Item("Sc_CancelPath", ["Esc"]),
                    Item("Sc_OpenEntry", [K("Sc_KeyDoubleClick")]),
                ]
            ),
            Group("Sc_GroupFileOperations",
                [
                    Item("Sc_SaveInEditor", [Ctrl, "S"]),
                    Item("Sc_CloseEditor", ["Esc"], "Sc_NoteEditorOnly"),
                ]
            ),
            Group("Cmd_ProcessManager",
                [
                    Item("Sc_RefreshProcesses", ["F5"]),
                    Item("Sc_EndTask", ["Delete"], "Sc_NoteListFocused"),
                    Item("Sc_CloseWindow", ["Esc"]),
                ]
            ),
            Group("Sc_GroupDialogs",
                [
                    Item("Sc_DialogCancel", ["Esc"], "Sc_NoteAllDialogs"),
                    Item("Sc_DialogConfirm", ["Enter"]),
                    Item("Sc_MaximizeRestore", [K("Sc_KeyTitleBar"), K("Sc_KeyDoubleClick")]),
                    Item("Sc_CancelDockDrag", ["Esc"], "Sc_NoteDragging"),
                    Item("Sc_PasswordPaste", [Ctrl, "V"]),
                    Item("Sc_PasswordCopyBlocked", [Ctrl, "C"], "Sc_NotePasswordBlocked"),
                ]
            ),
            Group("Sc_GroupAi",
                [
                    Item("Sc_AiSend", ["Enter"]),
                    Item("Sc_AiNewline", [Shift, "Enter"]),
                    Item("Sc_AiHistoryPrev", ["Up"], "Sc_NoteCaretFirstLine"),
                    Item("Sc_AiHistoryNext", ["Down"], "Sc_NoteCaretLastLine"),
                    Item("Sc_AiRefNext", ["Down"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefPrev", ["Up"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefAccept", ["Enter"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefAccept", ["Tab"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefClose", ["Esc"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefDelete", ["Backspace"]),
                    Item("Sc_AiRenameCommit", ["Enter"]),
                    Item("Sc_AiRenameCancel", ["Esc"]),
                    Item("Sc_AiClosePanel", ["Esc"]),
                ]
            ),
        ];
    }

    /// <summary>全部条目的扁平序列(计数与搜索用)。</summary>
    public static IEnumerable<ShortcutItem> Flatten(ShortcutGroup[] groups) => groups.SelectMany(group => group.Items);

    /// <summary>分组标题的资源键同时充当分组 id —— 换语言重建后靠它把折叠态搬过去。</summary>
    private static ShortcutGroup Group(string titleKey, ShortcutItem[] items) => new(titleKey, T(titleKey), items);

    private static ShortcutItem Item(string labelKey, string[] keys, string? noteKey = null) =>
        new(T(labelKey), keys, noteKey is null ? null : T(noteKey));

    /// <summary>一条可自定义键位:动作名与备注取自出厂表,键帽、改动标记与终端冲突提示取自 <paramref name="keymap" />。</summary>
    private static ShortcutItem Bound(ShortcutKeymap keymap, string bindingId)
    {
        ShortcutBinding binding = ShortcutBindings.Find(bindingId)
                                  ?? throw new ArgumentException($"ShortcutBindings has no binding '{bindingId}'.", nameof(bindingId));
        var item = new ShortcutItem(binding.Label, [], binding.NoteKey is null ? null : T(binding.NoteKey), binding.Id);
        item.Refresh(keymap);
        return item;
    }

    /// <summary>本地化的手势名(左键/双击/滚轮…),与 Ctrl、Shift 一样占一枚键帽。</summary>
    private static string K(string key) => T(key);

    private static string T(string key) => Strings.Get(key);
}
