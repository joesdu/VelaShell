using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>会话树视图模型:管理分组/会话节点、选中项与右键菜单命令,并向宿主转发连接、编辑、SFTP 等操作请求。</summary>
public sealed class SessionTreeViewModel : ReactiveObject
{
    private readonly ISessionRepository _repository;
    private readonly ISettingsService? _settings;
    private readonly Dictionary<Guid, SessionProfile> _sessionCache = [];

    /// <summary>
    /// 各分组在本次运行里的展开态记忆(#474):键为分组 Id。
    /// </summary>
    /// <remarks>
    /// 没有这份记忆,「启动时折叠分组」会在每次 <see cref="LoadTreeAsync" /> 上重新生效 ——
    /// 而新建/编辑/删除一条连接、云同步回来都会重建整棵树,于是刚展开的分组又折回去了。
    /// 记忆只活在进程内:设置管的是「应用打开时」,重启后本就该回到设置说的那个状态。
    /// </remarks>
    private readonly Dictionary<Guid, bool> _groupExpansion = [];

    /// <summary>
    /// 各配置最近一次上报的连接状态;重建树(LoadTreeAsync)后重放,状态圆点
    /// 与「活跃/连接中」标签才不会因刷新而回到断开态。
    /// </summary>
    private readonly Dictionary<Guid, SessionStatus> _statusCache = [];
    private readonly Dictionary<Guid, string> _syncChannelCache = [];

    private bool _hasNoSessions;

    /// <summary>用指定的会话仓储构造视图模型,并初始化各右键菜单命令及其可用性约束。</summary>
    /// <param name="repository">提供会话与分组读写、持久化的仓储。</param>
    /// <param name="settings">
    /// 设置服务,用于读取「启动时折叠分组」(#474)。为 null(无头宿主/单测)时按展开处理。
    /// </param>
    public SessionTreeViewModel(ISessionRepository repository, ISettingsService? settings = null)
    {
        _repository = repository;
        _settings = settings;
        Nodes = [];
        Nodes.CollectionChanged += OnNodesChanged;
        _hasNoSessions = true;
        LoadCommand = ReactiveCommand.CreateFromTask(LoadTreeAsync);
        IObservable<bool> hasSelectedSession = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node is { IsGroup: false });
        ConnectCommand = ReactiveCommand.Create(
            () => RaiseForSelected(ConnectRequested),
            hasSelectedSession
        );
        EditSessionCommand = ReactiveCommand.Create(
            () => RaiseForSelected(EditRequested),
            hasSelectedSession
        );
        DeleteSessionCommand = ReactiveCommand.CreateFromTask(
            DeleteSelectedSessionAsync,
            hasSelectedSession
        );
        DuplicateSessionCommand = ReactiveCommand.CreateFromTask(
            DuplicateSelectedSessionAsync,
            hasSelectedSession
        );
        IObservable<bool> hasSelectedSftpProfile = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node is { IsGroup: false, IsSshProfile: true } or
            { IsGroup: false, IsSftpProfile: true });
        IObservable<bool> hasSelectedSshSession = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node is { IsGroup: false, IsSshProfile: true });
        OpenSftpCommand = ReactiveCommand.Create(
            () => RaiseForSelected(OpenSftpRequested),
            hasSelectedSftpProfile
        );
        PortForwardCommand = ReactiveCommand.Create(
            () => RaiseForSelectedSsh(PortForwardRequested),
            hasSelectedSshSession
        );
        DisconnectCommand = ReactiveCommand.Create(
            () => RaiseForSelected(DisconnectRequested),
            hasSelectedSession
        );
        DiagnoseCommand = ReactiveCommand.Create(
            () => RaiseForSelected(DiagnoseRequested),
            hasSelectedSession
        );
        MoveToGroupCommand = ReactiveCommand.CreateFromTask<SessionTreeNodeViewModel>(
            MoveSelectedToGroupAsync
        );
        DeleteGroupCommand = ReactiveCommand.CreateFromTask(
            DeleteSelectedGroupAsync,
            this.WhenAnyValue(x => x.SelectedNode).Select(node => node is { IsGroup: true })
        );
        TogglePinCommand = ReactiveCommand.CreateFromTask(
            TogglePinSelectedAsync,
            hasSelectedSession
        );
        OpenDualSftpCommand = ReactiveCommand.Create(
            RaiseOpenDualSftp,
            this.WhenAnyValue(x => x.CanOpenDualSelection)
        );

        // 多选(#571):Ctrl / Shift 选中多条后右键弹的那份菜单。
        IObservable<bool> hasMultiSelection = this.WhenAnyValue(x => x.HasMultiSelection);
        OpenSelectedCommand = ReactiveCommand.Create(RaiseOpenSelected, hasMultiSelection);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteMultiSelectionAsync, hasMultiSelection);
        MoveSelectionToGroupCommand = ReactiveCommand.CreateFromTask<SessionTreeNodeViewModel>(MoveMultiSelectionToGroupAsync);
        BatchEditCommand = ReactiveCommand.Create(RaiseBatchEdit, hasMultiSelection);
        ExportSelectedCommand = ReactiveCommand.Create(RaiseExportSelected, hasMultiSelection);

        // 分组行的菜单(#571):整组打开、上移下移、导出 / 导入到这一组。
        IObservable<bool> hasSelectedGroup = this.WhenAnyValue(x => x.SelectedNode).Select(node => node is { IsGroup: true });
        OpenGroupCommand = ReactiveCommand.Create(RaiseOpenGroup, hasSelectedGroup);
        MoveGroupUpCommand = ReactiveCommand.CreateFromTask(
            () => MoveSelectedGroupByAsync(-1),
            this.WhenAnyValue(x => x.CanMoveSelectedGroupUp)
        );
        MoveGroupDownCommand = ReactiveCommand.CreateFromTask(
            () => MoveSelectedGroupByAsync(1),
            this.WhenAnyValue(x => x.CanMoveSelectedGroupDown)
        );
        ExportGroupCommand = ReactiveCommand.Create(RaiseExportGroup, hasSelectedGroup);
        ImportIntoGroupCommand = ReactiveCommand.Create(
            () => ImportFileRequested?.Invoke(SelectedNode is { IsGroup: true } group ? group.Id : null),
            hasSelectedGroup
        );

        // 资源管理器「更多」菜单(#571)。
        ExportAllCommand = ReactiveCommand.Create(
            () => ExportRequested?.Invoke(new SessionExportRequest([.. OrderedSessionIds()], null)),
            this.WhenAnyValue(x => x.HasNoSessions).Select(static none => !none)
        );
        ImportFileCommand = ReactiveCommand.Create(() => ImportFileRequested?.Invoke(null));
        SaveCsvTemplateCommand = ReactiveCommand.Create(() => CsvTemplateRequested?.Invoke());
        SortGroupsByNameCommand = ReactiveCommand.CreateFromTask(SortGroupsByNameAsync);
        CollapseAllCommand = ReactiveCommand.Create(() => SetAllGroupsExpanded(false));
        ExpandAllCommand = ReactiveCommand.Create(() => SetAllGroupsExpanded(true));
    }

    /// <summary>树的根级节点集合,包含各分组节点及直接挂在根级的未分组会话。</summary>
    /// <remarks>
    /// 这是<b>数据形状</b>(两层:分组 → 会话)。界面绑的不是它而是摊平后的 <see cref="Rows" /> ——
    /// 两者由 <see cref="SyncRows" /> 保持同步,这里的任何增删改都会自动反映过去。
    /// </remarks>
    public ObservableCollection<SessionTreeNodeViewModel> Nodes { get; }

    /// <summary>
    /// 摊平后的行:每个根级节点一行,展开的分组后面紧跟它的会话行。<b>界面绑的是这个。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 之所以自己摊平、用平列表画,而不是交给 <c>TreeView</c>:那个控件为每一层预留了一块
    /// 缩进区与一枚内置箭头,而本设计的箭头是自绘的、缩进是行内 padding ——
    /// 于是只能靠一串按模板部件名去关灯的样式把内置的那套压掉,压不干净就在展开后的子行前面
    /// 留下一条<b>点不着、也不跟着高亮</b>的空白。摊平之后每一行都是同一层的普通行,
    /// 行背景从最左画到最右,那条空白从根上就不存在了。
    /// </para>
    /// <para>
    /// 就地对齐而不是清空重建:清空会让 <see cref="SelectedNode" /> 被列表控件顺手清成 null
    /// (选中项跟着 <c>SelectedItem</c> 双向绑),折一下分组就把用户的选择弄丢了。
    /// </para>
    /// </remarks>
    public ObservableCollection<SessionTreeNodeViewModel> Rows { get; } = [];

    /// <summary>已经挂上监听的节点(<see cref="Nodes" /> 的 Reset 不带旧项,得自己记着才摘得掉)。</summary>
    private readonly HashSet<SessionTreeNodeViewModel> _watched = [];

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Clear() 不给 OldItems,只能照着自己记的那份全摘掉
            foreach (SessionTreeNodeViewModel node in _watched.ToList())
            {
                Unwatch(node);
            }
        }
        foreach (SessionTreeNodeViewModel node in e.OldItems?.OfType<SessionTreeNodeViewModel>() ?? [])
        {
            Unwatch(node);
        }
        foreach (SessionTreeNodeViewModel node in Nodes)
        {
            Watch(node);
        }
        SyncRows();
        // 分组多了、少了、挪了位置,选中分组能不能再上移 / 下移都可能变。
        RaiseGroupMoveState();
    }

    /// <summary>盯住一个根级节点:它的展开状态、以及(分组的)子项增删都会改变行序。</summary>
    private void Watch(SessionTreeNodeViewModel node)
    {
        if (!_watched.Add(node))
        {
            return;
        }
        node.PropertyChanged += OnNodePropertyChanged;
        if (node.IsGroup)
        {
            node.Children.CollectionChanged += OnChildrenChanged;
        }
    }

    private void Unwatch(SessionTreeNodeViewModel node)
    {
        if (!_watched.Remove(node))
        {
            return;
        }
        node.PropertyChanged -= OnNodePropertyChanged;
        if (node.IsGroup)
        {
            node.Children.CollectionChanged -= OnChildrenChanged;
        }
    }

    private void OnNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionTreeNodeViewModel.IsExpanded))
        {
            return;
        }
        // 记下用户这次的展开/折叠选择,重建树时照这份记忆恢复(#474)。
        if (sender is SessionTreeNodeViewModel { IsGroup: true } group)
        {
            _groupExpansion[group.Id] = group.IsExpanded;
        }
        SyncRows();
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncRows();

    /// <summary>
    /// 把 <see cref="Rows" /> 对齐到 <see cref="Nodes" /> 此刻应该摊出来的样子。
    /// </summary>
    /// <remarks>
    /// <b>只动有差异的那几行</b>(移走多余的、把错位的挪到位、补上缺的),不清空重建 ——
    /// 见 <see cref="Rows" /> 的说明。折叠把选中的会话收进去时,选中<b>上移到它那一组</b>:
    /// 不然选中项从列表里消失,而右键菜单里的命令仍然对着一个看不见的会话执行。
    /// </remarks>
    private void SyncRows()
    {
        var desired = new List<SessionTreeNodeViewModel>(Rows.Count);

        // 置顶的会话先摊在最前(#474)。它们在 Nodes 里的位置**一点没动** —— 只是这一层
        // 换个顺序摊出来,于是分组归属、拖放落点、"分组空了就删掉"那些规则一条都不用改。
        desired.AddRange(
            EnumerateSessionNodes()
                .Where(node => node.IsPinned)
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
        );
        foreach (SessionTreeNodeViewModel node in Nodes)
        {
            if (node.IsPinned)
            {
                // 根级的置顶会话:已经摊在最前了,不能再摊一次 —— 同一个实例在
                // 列表里出现两次,选中与容器复用都会错乱。
                continue;
            }
            desired.Add(node);
            if (node.IsGroup && node.IsExpanded)
            {
                desired.AddRange(node.Children.Where(child => !child.IsPinned));
            }
        }
        var keep = new HashSet<SessionTreeNodeViewModel>(desired);
        // 多选里有几条被折叠收进去(或整棵树重建)了:看不见的不该还算"选中",从多选里摘掉;
        // 剩不到两条就退回普通单选。选中项本身被收进去时,下面会把选中挪到它那一组,多选随之结束。
        if (_multiSelection.Any(node => !keep.Contains(node)))
        {
            List<SessionTreeNodeViewModel> visible = [.. _multiSelection.Where(keep.Contains)];
            ApplyMultiSelection(visible.Count >= 2 ? visible : []);
        }
        SessionTreeNodeViewModel? selected = SelectedNode;

        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i]))
            {
                Rows.RemoveAt(i);
            }
        }
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], desired[i]))
            {
                continue;
            }
            int at = Rows.IndexOf(desired[i]);
            if (at >= 0)
            {
                Rows.Move(at, i);
            }
            else
            {
                Rows.Insert(i, desired[i]);
            }
        }

        if (selected is null)
        {
            return;
        }
        // 收进去了:落到它那一组的行上。整个节点已经不在树里了(删掉了)就落空,那是对的。
        SelectedNode = keep.Contains(selected)
            ? selected
            : Nodes.FirstOrDefault(node => node.IsGroup && node.Children.Contains(selected));
    }

    /// <summary>是否当前没有任何会话,用于驱动空状态提示的显示。</summary>
    public bool HasNoSessions
    {
        get => _hasNoSessions;
        private set => this.RaiseAndSetIfChanged(ref _hasNoSessions, value);
    }

    /// <summary>无会话时的空状态提示文案(本地化)。</summary>
    public static string EmptyStateMessage => Strings.Get("Svc_AddFirstConnection");

    /// <summary>当前选中的树节点;命令的可用性依据其是否为非分组会话节点判定。</summary>
    /// <remarks>
    /// 选中挪到多选之外的节点(普通单击、键盘上下、折叠分组把选中项收进去)就结束多选 ——
    /// 否则那几行还亮着,右键却对着另一行弹菜单。没有多选时,选中的会话同时是下一次 Shift 区间选择的起点。
    /// </remarks>
    public SessionTreeNodeViewModel? SelectedNode
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (_multiSelection.Count > 0 && (value is null || !_multiSelection.Contains(value)))
            {
                ClearMultiSelection();
            }
            if (_multiSelection.Count == 0 && value is { IsGroup: false })
            {
                _selectionAnchor = value;
            }
            RaiseGroupMoveState();
        }
    }

    /// <summary>
    /// 多选(#571,由 #524 的 Ctrl 双选扩展而来):按选中先后排列(先选的在前,双栏 SFTP 里它在左栏);没有多选时为空。
    /// </summary>
    /// <remarks>
    /// <b>不变式:要么 0 条,要么至少 2 条。</b>一条就是普通的单选,由 <see cref="SelectedNode" /> 表示,
    /// 不在这里重复记一份 —— 两处各记一条迟早对不上。
    /// </remarks>
    private readonly List<SessionTreeNodeViewModel> _multiSelection = [];

    /// <summary>Shift 区间选择的起点:最近一次普通单击 / Ctrl 加选的那一行。</summary>
    private SessionTreeNodeViewModel? _selectionAnchor;

    /// <summary>多选的会话(先选的在前);没有多选时为空。</summary>
    public IReadOnlyList<SessionTreeNodeViewModel> MultiSelection => _multiSelection;

    /// <summary>当前是否有多选(右键弹的是多选专用菜单)。</summary>
    public bool HasMultiSelection => _multiSelection.Count >= 2;

    /// <summary>多选是不是恰好两条(只有这时才能「在双栏 SFTP 中打开」)。</summary>
    public bool IsPairSelected => _multiSelection.Count == 2;

    /// <summary>多选的两条是否都能在双栏 SFTP 中打开(SSH / SFTP / FTP / 插件的文件协议)。</summary>
    public bool CanOpenDualSelection =>
        IsPairSelected
        && _multiSelection.All(node =>
            node.CanOpenInDualSftp
            && (DualSftpFilter is not { } filter
                || (_sessionCache.TryGetValue(node.Id, out SessionProfile? profile) && filter(profile))));

    /// <summary>选了两条却进不了双栏(混进了工作台类插件之类)时,菜单里那一行灰色说明是否显示。</summary>
    public bool ShowDualSftpUnsupportedHint => IsPairSelected && !CanOpenDualSelection;

    /// <summary>多选菜单「打开 N 个连接」的文案。</summary>
    public string OpenSelectedText => Strings.Format("Tree_OpenSelected", _multiSelection.Count);

    /// <summary>多选菜单「删除 N 个连接」的文案。</summary>
    public string DeleteSelectedText => Strings.Format("Tree_DeleteSelected", _multiSelection.Count);

    /// <summary>
    /// 宿主给的补充判断:这条配置能不能进双栏。树只认得连接类型,插件协议是文件协议还是工作台要问插件注册表
    /// (见 <see cref="SessionTreeNodeViewModel.CanOpenInDualSftp" />)。为 null 时只看连接类型。
    /// </summary>
    public Func<SessionProfile, bool>? DualSftpFilter { get; set; }

    /// <summary>
    /// Ctrl + 单击一条会话:把它加入或移出多选。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>已有一条普通选中的会话时,Ctrl 点另一条 → 两条组成多选,先选的那条在前。</item>
    ///   <item>再 Ctrl 点别的 → 接着往里加,不设上限(#524 时只能两条、第三条会把最早那条顶掉;多选之后不再顶)。</item>
    ///   <item>Ctrl 点已在选择里的那条 → 把它移出;只剩一条就退回普通单选。</item>
    ///   <item>分组行不参与。</item>
    /// </list>
    /// 同一条配置不会出现两次:选择按节点记,而一条配置在树上只有一个节点。
    /// </remarks>
    /// <param name="node">被 Ctrl 单击的会话行。</param>
    public void ToggleMultiSelection(SessionTreeNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsGroup)
        {
            return;
        }
        List<SessionTreeNodeViewModel> picked = CurrentPicks();
        bool added = !picked.Remove(node);
        if (added)
        {
            picked.Add(node);
        }
        ApplyMultiSelection(picked.Count >= 2 ? picked : []);
        if (added)
        {
            _selectionAnchor = node;
        }
        // 选中项落在最后点的那条上(移出时落在剩下最后那条上),几行一起亮;全移空了就什么都不选。
        SelectedNode = picked.Contains(node) ? node : picked.LastOrDefault();
    }

    /// <summary>
    /// Shift + 单击一条会话:从起点(上一次普通单击或 Ctrl 加选的那一行)到这一行,把中间看得见的会话全部选上。
    /// </summary>
    /// <remarks>
    /// 区间按<b>眼睛看到的行</b>算(<see cref="Rows" />):折叠着的分组里的会话不进来,分组行本身跳过。
    /// 起点已经看不见了(被折起来、被删掉)就从这一行自己开始。
    /// </remarks>
    /// <param name="node">被 Shift 单击的会话行。</param>
    /// <param name="additive">同时按着 Ctrl:把区间并进已有的多选,而不是替换它。</param>
    public void ExtendSelectionTo(SessionTreeNodeViewModel node, bool additive)
    {
        ArgumentNullException.ThrowIfNull(node);
        int to = Rows.IndexOf(node);
        if (node.IsGroup || to < 0)
        {
            return;
        }
        SessionTreeNodeViewModel anchor =
            _selectionAnchor is { } remembered && Rows.Contains(remembered) ? remembered
            : SelectedNode is { IsGroup: false } selected && Rows.Contains(selected) ? selected
            : node;
        int from = Rows.IndexOf(anchor);
        int step = to >= from ? 1 : -1;
        List<SessionTreeNodeViewModel> picked = additive ? CurrentPicks() : [];
        for (int i = from; ; i += step)
        {
            if (!Rows[i].IsGroup && !picked.Contains(Rows[i]))
            {
                picked.Add(Rows[i]);
            }
            if (i == to)
            {
                break;
            }
        }
        ApplyMultiSelection(picked.Count >= 2 ? picked : []);
        SelectedNode = node;
        _selectionAnchor = anchor;
    }

    /// <summary>
    /// 普通单击一条会话:多选结束,这一行成为下一次 Shift 区间选择的起点。
    /// </summary>
    /// <remarks>
    /// 起点不能只靠 <see cref="SelectedNode" /> 的 setter 去挪:点的正好是已经选中的那一行时,列表根本不会再赋一次值,
    /// 起点就停在更早的地方 —— 先 Shift 选了 1–5、再单击 5、再 Shift 点 8,选出来的是 1–8 而不是 5–8。
    /// </remarks>
    /// <param name="node">被单击的行。</param>
    public void StartSelectionAt(SessionTreeNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        ClearMultiSelection();
        if (!node.IsGroup)
        {
            _selectionAnchor = node;
        }
    }

    /// <summary>结束多选(普通单击、重建树时)。</summary>
    public void ClearMultiSelection()
    {
        if (_multiSelection.Count > 0)
        {
            ApplyMultiSelection([]);
        }
    }

    /// <summary>当前选中的会话:有多选取多选,否则取选中的那一条(分组行不算)。</summary>
    private List<SessionTreeNodeViewModel> CurrentPicks() =>
        _multiSelection.Count > 0 ? [.. _multiSelection]
        : SelectedNode is { IsGroup: false } current ? [current]
        : [];

    private void ApplyMultiSelection(IReadOnlyList<SessionTreeNodeViewModel> picked)
    {
        foreach (SessionTreeNodeViewModel old in _multiSelection)
        {
            old.IsMultiMarked = false;
        }
        _multiSelection.Clear();
        _multiSelection.AddRange(picked);
        foreach (SessionTreeNodeViewModel node in _multiSelection)
        {
            node.IsMultiMarked = true;
        }
        this.RaisePropertyChanged(nameof(MultiSelection));
        this.RaisePropertyChanged(nameof(HasMultiSelection));
        this.RaisePropertyChanged(nameof(IsPairSelected));
        this.RaisePropertyChanged(nameof(CanOpenDualSelection));
        this.RaisePropertyChanged(nameof(ShowDualSftpUnsupportedHint));
        this.RaisePropertyChanged(nameof(OpenSelectedText));
        this.RaisePropertyChanged(nameof(DeleteSelectedText));
    }

    /// <summary>多选里的连接,按树上从上到下的顺序(打开时标签就按这个顺序排出来)。</summary>
    private List<SessionProfile> MultiSelectionProfiles() =>
    [
        .. _multiSelection
            .OrderBy(node => Rows.IndexOf(node))
            .Select(node => _sessionCache.GetValueOrDefault(node.Id))
            .OfType<SessionProfile>()
    ];

    /// <summary>把多选的前两条交给宿主打开成一个双栏远程文档(先选的在左)。</summary>
    private void RaiseOpenDualSftp()
    {
        if (!CanOpenDualSelection
            || !_sessionCache.TryGetValue(_multiSelection[0].Id, out SessionProfile? left)
            || !_sessionCache.TryGetValue(_multiSelection[1].Id, out SessionProfile? right))
        {
            return;
        }
        OpenDualSftpRequested?.Invoke(left, right);
    }

    /// <summary>「在双栏 SFTP 中打开」:多选恰好两条时多选菜单里的一项。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenDualSftpCommand { get; }

    /// <summary>多选菜单「打开 N 个连接」,触发 <see cref="OpenManyRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSelectedCommand { get; }

    /// <summary>多选菜单「删除 N 个连接」(先经 <see cref="ConfirmDeleteSessions" /> 确认)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteSelectedCommand { get; }

    /// <summary>多选菜单「移动到分组」:参数为子菜单里的分组节点(<see cref="Guid.Empty" /> 为未分组)。</summary>
    public ReactiveCommand<SessionTreeNodeViewModel, RxVoid> MoveSelectionToGroupCommand { get; }

    /// <summary>多选菜单「批量修改」,触发 <see cref="BatchEditRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BatchEditCommand { get; }

    /// <summary>多选菜单「导出所选」,触发 <see cref="ExportRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportSelectedCommand { get; }

    /// <summary>分组菜单「打开全部连接」,触发 <see cref="OpenManyRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenGroupCommand { get; }

    /// <summary>分组菜单「上移」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveGroupUpCommand { get; }

    /// <summary>分组菜单「下移」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveGroupDownCommand { get; }

    /// <summary>分组菜单「导出此分组」,触发 <see cref="ExportRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportGroupCommand { get; }

    /// <summary>分组菜单「导入到此分组」,触发 <see cref="ImportFileRequested" />(参数为该分组)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ImportIntoGroupCommand { get; }

    /// <summary>「更多」菜单「导出全部连接」,触发 <see cref="ExportRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportAllCommand { get; }

    /// <summary>「更多」菜单「导入连接文件」,触发 <see cref="ImportFileRequested" />(不指定分组)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ImportFileCommand { get; }

    /// <summary>「更多」菜单「保存 CSV 模板」,触发 <see cref="CsvTemplateRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveCsvTemplateCommand { get; }

    /// <summary>「更多」菜单「分组按名称排序」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SortGroupsByNameCommand { get; }

    /// <summary>「更多」菜单「全部折叠」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CollapseAllCommand { get; }

    /// <summary>「更多」菜单「全部展开」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExpandAllCommand { get; }

    /// <summary>选中的分组能不能再往上移。</summary>
    public bool CanMoveSelectedGroupUp => SelectedNode is { IsGroup: true } group && GroupIndexOf(group) > 0;

    /// <summary>选中的分组能不能再往下移。</summary>
    public bool CanMoveSelectedGroupDown =>
        SelectedNode is { IsGroup: true } group && GroupIndexOf(group) is var index && index >= 0 && index < GroupCount() - 1;

    /// <summary>
    /// 一次打开多条连接(整组打开、多选打开)。由宿主决定要不要先确认、按什么并发度连。
    /// </summary>
    public event Action<IReadOnlyList<SessionProfile>>? OpenManyRequested;

    /// <summary>多选菜单「批量修改」:由宿主打开批量修改对话框。</summary>
    public event Action<IReadOnlyList<SessionProfile>>? BatchEditRequested;

    /// <summary>导出(全部 / 一个分组 / 多选):由宿主打开导出对话框。</summary>
    public event Action<SessionExportRequest>? ExportRequested;

    /// <summary>导入连接文件:参数为目标分组(分组菜单「导入到此分组」),null = 按文件里的分组。</summary>
    public event Action<Guid?>? ImportFileRequested;

    /// <summary>「保存 CSV 模板」:由宿主弹出保存对话框。</summary>
    public event Action? CsvTemplateRequested;

    /// <summary>
    /// 批量删除前的确认回调,由视图提供弹窗;参数是已本地化好的提示语,返回 true 才继续删。
    /// 与 <see cref="ConfirmDeleteGroup" /> 同形:未挂回调(无头宿主/单测)时直接删。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDeleteSessions { get; set; }

    /// <summary>右键「在双栏 SFTP 中打开」:由宿主连接两条会话并建出双栏远程文档(参数依次为左栏、右栏)。</summary>
    public event Action<SessionProfile, SessionProfile>? OpenDualSftpRequested;

    /// <summary>分组节点(供“移动到分组”子菜单绑定);随 LoadTreeAsync 同步。</summary>
    public ObservableCollection<SessionTreeNodeViewModel> GroupNodes { get; } = [];

    /// <summary>从仓储加载并重建整棵会话树。</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    /// <summary>连接选中的会话,触发 <see cref="ConnectRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ConnectCommand { get; }

    /// <summary>编辑选中的会话,触发 <see cref="EditRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> EditSessionCommand { get; }

    /// <summary>删除选中的会话(含落库与树节点移除)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteSessionCommand { get; }

    // 复制选中的连接为“<名称> (副本)”并落库
    /// <summary>复制选中的会话为“&lt;名称&gt; (副本)”并落库,随后重建树。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DuplicateSessionCommand { get; }

    /// <summary>为选中的会话打开 SFTP,触发 <see cref="OpenSftpRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSftpCommand { get; }

    /// <summary>为选中的会话打开端口转发,触发 <see cref="PortForwardRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> PortForwardCommand { get; }

    /// <summary>断开选中会话的连接,触发 <see cref="DisconnectRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DisconnectCommand { get; }

    /// <summary>对选中的会话发起连接诊断,触发 <see cref="DiagnoseRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DiagnoseCommand { get; }

    /// <summary>把选中的会话移动到指定分组节点(参数为“移动到分组”子菜单项)。</summary>
    public ReactiveCommand<SessionTreeNodeViewModel, RxVoid> MoveToGroupCommand { get; }

    /// <summary>删除选中的分组,连同组内全部连接一并删除(落库 + 移除树节点)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteGroupCommand { get; }

    /// <summary>置顶 / 取消置顶选中的会话(#474),落库后立刻重排行序。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TogglePinCommand { get; }

    /// <summary>
    /// 删除分组前的确认回调,由视图提供弹窗;参数是已本地化好的提示语,返回 true 才继续删。
    /// 与 <c>FileBrowserViewModel.ConfirmDelete</c> 同形:未挂回调(无头宿主/单测)时直接删,
    /// 界面上则永远挂着——不确认就删掉整组连接是不可接受的。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDeleteGroup { get; set; }

    /// <summary>右键“连接”或双击会话时触发,由宿主发起 SSH 连接。</summary>
    public event Action<SessionProfile>? ConnectRequested;

    /// <summary>右键“编辑”时触发,由宿主打开连接配置弹窗。</summary>
    public event Action<SessionProfile>? EditRequested;

    /// <summary>右键“打开 SFTP”:由宿主连接会话并展开文件浏览面板。</summary>
    public event Action<SessionProfile>? OpenSftpRequested;

    /// <summary>右键“端口转发”:由宿主打开隧道管理面板。</summary>
    public event Action<SessionProfile>? PortForwardRequested;

    /// <summary>右键“断开连接”:由宿主断开该会话已连接的终端标签。</summary>
    public event Action<SessionProfile>? DisconnectRequested;

    /// <summary>右键“连接诊断”:由宿主打开连接诊断中心(设计 RGXg1)。</summary>
    public event Action<SessionProfile>? DiagnoseRequested;

    private void RaiseForSelected(Action<SessionProfile>? handler)
    {
        if (
            SelectedNode is { IsGroup: false } node
            && _sessionCache.TryGetValue(node.Id, out SessionProfile? session)
        )
        {
            handler?.Invoke(session);
        }
    }

    private void RaiseForSelectedSsh(Action<SessionProfile>? handler)
    {
        if (SelectedNode is { IsSshProfile: true })
        {
            RaiseForSelected(handler);
        }
    }

    /// <summary>视图双击会话行时调用:选中并触发连接。</summary>
    public void RequestConnect(Guid sessionId)
    {
        if (_sessionCache.TryGetValue(sessionId, out SessionProfile? session))
        {
            ConnectRequested?.Invoke(session);
        }
    }

    /// <summary>将一个会话加入树:无分组的挂到树根,否则挂到对应分组节点下,并刷新空状态。</summary>
    /// <param name="session">要加入树的会话配置。</param>
    public void AddSession(SessionProfile session)
    {
        _sessionCache[session.Id] = session;
        var sessionNode = new SessionTreeNodeViewModel(session.Id, session.Name, false, session.ConnectionType)
        {
            IsPinned = session.IsPinned,
            Notes = session.Notes,
        };
        if (session.GroupId is null)
        {
            // 未分组会话直接挂树根(设计 FrJPu),不再有“未分组”目录。
            sessionNode.IsRootLevel = true;
            Nodes.Add(sessionNode);
        }
        else
        {
            SessionTreeNodeViewModel? groupNode = Nodes.FirstOrDefault(node =>
                node.IsGroup && node.Id == session.GroupId
            );
            if (groupNode is null)
            {
                return;
            }
            groupNode.Children.Add(sessionNode);
        }
        RefreshHasNoSessions();
    }

    /// <summary>
    /// 把指定会话移动到目标分组;<paramref name="targetGroupId" /> 为 <see cref="Guid.Empty" /> 表示移回树根(未分组)。
    /// 树的改动是同步的,落库异步跟进(调用方不关心持久化时机时用这个重载)。
    /// </summary>
    /// <param name="sessionId">要移动的会话标识。</param>
    /// <param name="targetGroupId">目标分组标识;<see cref="Guid.Empty" /> 表示未分组(树根)。</param>
    public void MoveSessionToGroup(Guid sessionId, Guid targetGroupId) =>
        _ = MoveSessionToGroupAsync(sessionId, targetGroupId);

    /// <summary>
    /// 移动会话到目标分组并落库。源分组因此空掉时连同分组一并删除 —— 分组在本应用里
    /// 只是会话的容器,空容器既没有可展示的内容,也无法再被拖入(拖放落点是分组行本身),
    /// 留着只会变成永远清不掉的僵尸目录。
    /// </summary>
    /// <remarks>
    /// 树节点的增删全部排在第一个 await 之前:同步入口 <see cref="MoveSessionToGroup" />
    /// 靠这一点让界面立即更新,只把持久化留给后台。
    /// 落库顺序是先存会话、再删分组 —— 反过来的话中途失败会留下一批 GroupId 指向已消失
    /// 分组的会话,下次加载时它们既不在分组下、也不在树根,等于凭空消失
    /// (与 <see cref="DeleteSelectedGroupAsync" /> 同一处置)。
    /// </remarks>
    public async Task MoveSessionToGroupAsync(Guid sessionId, Guid targetGroupId)
    {
        SessionTreeNodeViewModel? sourceNode = FindSessionNode(
            sessionId,
            out SessionTreeNodeViewModel? sourceGroup
        );
        if (sourceNode is null)
        {
            return;
        }
        // 原地不动直接返回。少了这道判断,“拖回自己所在的分组”会先把节点摘下来、
        // 把分组判为空而删掉,再往这个已删除的分组里挂回去 —— 会话凭空消失。
        if ((sourceGroup?.Id ?? Guid.Empty) == targetGroupId)
        {
            return;
        }
        SessionTreeNodeViewModel? targetGroup = null;
        if (targetGroupId != Guid.Empty)
        {
            targetGroup = Nodes.FirstOrDefault(node => node.IsGroup && node.Id == targetGroupId);
            if (targetGroup is null)
            {
                // 目标分组不存在:整个移动放弃,不能把节点摘下来又挂不回去。
                return;
            }
        }
        if (sourceGroup is not null)
        {
            sourceGroup.Children.Remove(sourceNode);
        }
        else
        {
            Nodes.Remove(sourceNode);
        }
        if (targetGroup is null)
        {
            // “未分组”落点 = 树根(设计 FrJPu)。
            sourceNode.IsRootLevel = true;
            InsertRootSessionSorted(sourceNode);
        }
        else
        {
            sourceNode.IsRootLevel = false;
            InsertSorted(targetGroup.Children, sourceNode);
            // 拖进折叠着的分组时展开一下,否则会话看起来像是"没了"。
            // 置顶的会话不用:它本来就摊在树顶,展开目标分组只是平白多铺开一片。
            if (!sourceNode.IsPinned)
            {
                targetGroup.IsExpanded = true;
            }
        }
        bool sourceGroupEmptied = sourceGroup is { Children.Count: 0 };
        if (sourceGroupEmptied)
        {
            Nodes.Remove(sourceGroup!);
            // GroupNodes 里是同一批分组节点实例(“移动到分组”子菜单绑定它),
            // 不同步移除的话,菜单里会留下一个指向已删分组的落点。
            GroupNodes.Remove(sourceGroup!);
            if (ReferenceEquals(SelectedNode, sourceGroup))
            {
                SelectedNode = null;
            }
        }
        if (_sessionCache.TryGetValue(sessionId, out SessionProfile? session))
        {
            // Guid.Empty 是“未分组”落点:落库必须存 null,否则下次加载时会话会
            // 因找不到分组而从树里消失。
            session.GroupId = targetGroupId == Guid.Empty ? null : targetGroupId;
            await _repository.SaveSessionAsync(session);
        }
        if (sourceGroupEmptied)
        {
            await _repository.DeleteGroupAsync(sourceGroup!.Id);
        }
    }

    /// <summary>
    /// 拖放落点解析:把"鼠标松开时所在的节点"翻译成目标分组 Id
    /// (<see cref="Guid.Empty" /> = 未分组/树根)。放在视图模型里而非视图里,
    /// 是为了让落点规则可单测 —— 视图只负责找出鼠标下的那个节点。
    /// </summary>
    /// <param name="node">鼠标下的节点;树的空白处传 null。</param>
    public Guid ResolveDropTargetGroupId(SessionTreeNodeViewModel? node) =>
        node switch
        {
            null => Guid.Empty,              // 空白处 = 未分组
            { IsGroup: true } => node.Id,    // 分组行 = 该分组
            // 会话行 = 它所在的分组(根级会话即未分组),这样"拖到某台机器上"
            // 与"拖到它所在的分组上"是一回事,不必精确瞄准分组标题行。
            _ => FindGroupIdOfSession(node.Id) ?? Guid.Empty
        };

    /// <summary>
    /// 落点的显示名,供拖拽时跟随光标的提示标签使用:
    /// <see cref="Guid.Empty" />(树根)= “未分组”,其余取分组名;
    /// 分组已不在树上(理论上不该发生)时同样回落到“未分组”,而不是显示一个 Guid。
    /// </summary>
    public string DescribeDropTarget(Guid targetGroupId) =>
        targetGroupId == Guid.Empty
            ? Strings.Get("Svc_Ungrouped")
            : Nodes.FirstOrDefault(node => node.IsGroup && node.Id == targetGroupId)?.Name
              ?? Strings.Get("Svc_Ungrouped");

    /// <summary>返回会话当前所属分组 Id;根级(未分组)为 <see cref="Guid.Empty" />,节点不存在为 null。</summary>
    public Guid? FindGroupIdOfSession(Guid sessionId)
    {
        if (FindSessionNode(sessionId, out SessionTreeNodeViewModel? parentGroup) is null)
        {
            return null;
        }
        return parentGroup?.Id ?? Guid.Empty;
    }

    /// <summary>按名称把会话节点插进分组子集合,保持与 <see cref="LoadTreeAsync" /> 一致的排序。</summary>
    private static void InsertSorted(
        ObservableCollection<SessionTreeNodeViewModel> children,
        SessionTreeNodeViewModel node
    )
    {
        int index = 0;
        while (
            index < children.Count
            && string.Compare(children[index].Name, node.Name, StringComparison.OrdinalIgnoreCase) < 0
        )
        {
            index++;
        }
        children.Insert(index, node);
    }

    /// <summary>
    /// 把会话节点插进树根的未分组区段。根级布局是“全部分组在前、未分组会话在后”
    /// (见 <see cref="LoadTreeAsync" />),所以先跳过分组节点再按名称排位 ——
    /// 直接 Add 会让刚移出来的会话固定落在最后,与重新加载后的顺序对不上。
    /// </summary>
    private void InsertRootSessionSorted(SessionTreeNodeViewModel node)
    {
        int index = 0;
        while (index < Nodes.Count && Nodes[index].IsGroup)
        {
            index++;
        }
        while (
            index < Nodes.Count
            && string.Compare(Nodes[index].Name, node.Name, StringComparison.OrdinalIgnoreCase) < 0
        )
        {
            index++;
        }
        Nodes.Insert(index, node);
    }

    /// <summary>宿主上报某配置的连接状态,驱动状态圆点与「活跃/连接中/离线」标签。</summary>
    public void SetSessionStatus(Guid sessionId, SessionStatus status)
    {
        _statusCache[sessionId] = status;
        SessionTreeNodeViewModel? node = FindSessionNode(sessionId, out _);
        node?.Status = status;
    }

    /// <summary>宿主上报某配置的同步输入频道字母(空串 = 已退出),驱动节点名前的频道标识。</summary>
    public void SetSessionSyncChannel(Guid sessionId, string letter)
    {
        _syncChannelCache[sessionId] = letter;
        SessionTreeNodeViewModel? node = FindSessionNode(sessionId, out _);
        node?.SyncChannelLetter = letter;
    }

    /// <summary>展开父分组并选中指定会话;找不到时保留当前选择。</summary>
    public bool SelectSession(Guid sessionId)
    {
        SessionTreeNodeViewModel? node = FindSessionNode(
            sessionId,
            out SessionTreeNodeViewModel? parentGroup
        );
        if (node is null)
        {
            return false;
        }
        // 置顶的会话恒在树顶可见,不必为了露出它把整个分组铺开。
        if (!node.IsPinned)
        {
            parentGroup?.IsExpanded = true;
        }
        foreach (SessionTreeNodeViewModel current in EnumerateSessionNodes())
        {
            current.IsSelected = ReferenceEquals(current, node);
        }
        SelectedNode = node;
        return true;
    }

    private IEnumerable<SessionTreeNodeViewModel> EnumerateSessionNodes() =>
        Nodes.SelectMany(node => node.IsGroup ? node.Children : [node]);

    /// <summary>
    /// 换语言后重取树上 C# 侧拼的文案:会话行的置顶菜单项与状态标签,
    /// 以及「移动到分组」里的「未分组」(它在建树时就取好了名字)。
    /// </summary>
    public void RefreshLocalizedText()
    {
        foreach (SessionTreeNodeViewModel node in EnumerateSessionNodes())
        {
            node.RefreshLocalizedText();
        }
        if (GroupNodes.FirstOrDefault(node => node.Id == Guid.Empty) is { } ungrouped)
        {
            ungrouped.Name = Strings.Get("Svc_Ungrouped");
        }
        this.RaisePropertyChanged(nameof(OpenSelectedText));
        this.RaisePropertyChanged(nameof(DeleteSelectedText));
    }

    /// <summary>在树根与各分组下查找会话节点;<paramref name="parentGroup" /> 为 null 表示根级。</summary>
    private SessionTreeNodeViewModel? FindSessionNode(
        Guid sessionId,
        out SessionTreeNodeViewModel? parentGroup
    )
    {
        foreach (SessionTreeNodeViewModel node in Nodes)
        {
            if (node.IsGroup)
            {
                SessionTreeNodeViewModel? child = node.Children.FirstOrDefault(item =>
                    item.Id == sessionId
                );
                if (child is null)
                {
                    continue;
                }
                parentGroup = node;
                return child;
            }
            if (node.Id != sessionId)
            {
                continue;
            }
            parentGroup = null;
            return node;
        }
        parentGroup = null;
        return null;
    }

    private void RefreshHasNoSessions() =>
        HasNoSessions = !Nodes.Any(node => !node.IsGroup || node.Children.Count > 0);

    private async Task MoveSelectedToGroupAsync(SessionTreeNodeViewModel? targetGroup)
    {
        if (targetGroup is not { IsGroup: true } || SelectedNode is not { IsGroup: false } node)
        {
            return;
        }
        await MoveSessionToGroupAsync(node.Id, targetGroup.Id);
    }

    /// <summary>
    /// 翻转选中会话的置顶态并落库。只改显示位置,分组归属(<see cref="SessionProfile.GroupId" />)
    /// 原样保留 —— 取消置顶后它回到自己那一组。
    /// </summary>
    /// <remarks>
    /// 末尾显式调一次 <see cref="SyncRows" />:置顶只改节点自身的一个属性,既没动
    /// <see cref="Nodes" /> 也没动任何分组的 <c>Children</c>,而分组子节点的属性变更
    /// 本就不在监听范围里(只有根级节点挂了 PropertyChanged),不叫它行序不会变。
    /// </remarks>
    private async Task TogglePinSelectedAsync()
    {
        if (
            SelectedNode is not { IsGroup: false } node
            || !_sessionCache.TryGetValue(node.Id, out SessionProfile? session)
        )
        {
            return;
        }
        bool pinned = !node.IsPinned;
        session.IsPinned = pinned;
        node.IsPinned = pinned;
        SyncRows();
        await _repository.SaveSessionAsync(session);
    }

    private async Task DuplicateSelectedSessionAsync()
    {
        if (
            SelectedNode is not { IsGroup: false } node
            || !_sessionCache.TryGetValue(node.Id, out SessionProfile? source)
        )
        {
            return;
        }
        SessionProfile copy = source.Clone();
        // 副本是一条新配置:换个 id、改个名字,其余原样。原先这里逐字段手写,
        // 每加一个字段就得记得回来补一行 —— 漏了的表现是"复制之后某个设置莫名丢了"。
        copy.Id = Guid.NewGuid();
        copy.Name = Strings.Format("Svc_CopySuffix", source.Name);
        // 复制出来的是一条从未连过的配置,"上次连接时间"不该跟着抄过来。
        copy.LastConnectedAt = null;
        await _repository.SaveSessionAsync(copy);
        await LoadTreeAsync();
    }

    private async Task LoadTreeAsync()
    {
        Nodes.Clear();
        GroupNodes.Clear();
        _sessionCache.Clear();

        // 分组的初始展开态:设置说了算(#474),但用户这次运行里手动改过的以记忆为准 ——
        // 新建一条连接就把刚展开的分组折回去,比不折叠更烦人。
        bool defaultExpanded = _settings is null
            || !(await _settings.GetSnapshotAsync().ConfigureAwait(true)).General.CollapseGroupsByDefault;

        // 以会话的 GroupId 为唯一事实来源分组;无分组的会话归入“未分组”节点。
        List<ServerGroup> groups = await _repository.GetAllGroupsAsync();
        List<SessionProfile> sessions = await _repository.GetAllSessionsAsync();
        var byGroup = sessions
            .Where(session => session.GroupId is not null)
            .GroupBy(session => session.GroupId!.Value)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToList());
        var ungrouped = sessions.Where(session => session.GroupId is null).ToList();
        int groupIndex = 0;
        foreach (ServerGroup group in groups.OrderBy(item => item.SortOrder))
        {
            var groupNode = new SessionTreeNodeViewModel(group.Id, group.Name, true)
            {
                // 文件夹图标按设计 FrJPu 以 warning/info/accent 轮换配色。
                GroupColorIndex = groupIndex++ % 3,
                IsExpanded = _groupExpansion.GetValueOrDefault(group.Id, defaultExpanded),
            };
            if (byGroup.TryGetValue(group.Id, out List<SessionProfile>? members))
            {
                foreach (
                    SessionProfile session in members.OrderBy(
                        s => s.Name,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    groupNode.Children.Add(CreateSessionNode(session, false));
                }
            }
            Nodes.Add(groupNode);
            GroupNodes.Add(groupNode);
        }

        // 未分组会话直接挂在树根(设计 FrJPu),不再收进“未分组”目录。
        foreach (
            SessionProfile session in ungrouped.OrderBy(
                s => s.Name,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            Nodes.Add(CreateSessionNode(session, true));
        }

        // “移动到分组”子菜单始终提供“未分组”落点(即移回树根)。
        GroupNodes.Add(new(Guid.Empty, Strings.Get("Svc_Ungrouped"), true));
        RefreshHasNoSessions();
    }

    private SessionTreeNodeViewModel CreateSessionNode(SessionProfile session, bool isRootLevel)
    {
        _sessionCache[session.Id] = session;
        var node = new SessionTreeNodeViewModel(session.Id, session.Name, false, session.ConnectionType)
        {
            IsRootLevel = isRootLevel,
            IsPinned = session.IsPinned,
            Notes = session.Notes,
        };
        if (_statusCache.TryGetValue(session.Id, out SessionStatus status))
        {
            node.Status = status;
        }
        if (_syncChannelCache.TryGetValue(session.Id, out string? letter))
        {
            node.SyncChannelLetter = letter;
        }
        return node;
    }

    /// <summary>
    /// 删除选中的分组:组内连接随分组一并删除(用户在确认框里被明确告知会删掉几条)。
    /// 落库顺序是先删会话再删分组 —— 反过来的话,中途失败会留下一批 GroupId 指向已消失分组的
    /// 会话,下次加载时它们既不在分组下、也不在树根,等于凭空消失。
    /// </summary>
    private async Task DeleteSelectedGroupAsync()
    {
        if (SelectedNode is not { IsGroup: true } group)
        {
            return;
        }
        List<Guid> memberIds = [.. group.Children.Select(child => child.Id)];
        if (ConfirmDeleteGroup is not null)
        {
            string message = memberIds.Count == 0
                ? Strings.Format("Tree_DeleteGroupConfirmEmpty", group.Name)
                : Strings.Format("Tree_DeleteGroupConfirm", group.Name, memberIds.Count);
            if (!await ConfirmDeleteGroup(message))
            {
                return;
            }
        }
        foreach (Guid sessionId in memberIds)
        {
            await _repository.DeleteSessionAsync(sessionId);
            _sessionCache.Remove(sessionId);
            _statusCache.Remove(sessionId);
            _syncChannelCache.Remove(sessionId);
        }
        await _repository.DeleteGroupAsync(group.Id);
        Nodes.Remove(group);
        // GroupNodes 里存的就是同一批分组节点实例(“移动到分组”子菜单绑定它),
        // 不同步移除的话,菜单里会留下一个指向已删分组的落点。
        GroupNodes.Remove(group);
        SelectedNode = null;
        RefreshHasNoSessions();
    }

    // ———— 多选的批量操作(#571) ————

    private void RaiseOpenSelected()
    {
        List<SessionProfile> profiles = MultiSelectionProfiles();
        if (profiles.Count > 0)
        {
            OpenManyRequested?.Invoke(profiles);
        }
    }

    private void RaiseBatchEdit()
    {
        List<SessionProfile> profiles = MultiSelectionProfiles();
        if (profiles.Count > 0)
        {
            BatchEditRequested?.Invoke(profiles);
        }
    }

    private void RaiseExportSelected()
    {
        List<SessionProfile> profiles = MultiSelectionProfiles();
        if (profiles.Count > 0)
        {
            ExportRequested?.Invoke(new SessionExportRequest([.. profiles.Select(static p => p.Id)], null));
        }
    }

    /// <summary>
    /// 删除多选的全部连接(确认过之后)。与单条删除同一口径:分组空了也留着,跳板引用不跟着改。
    /// </summary>
    private async Task DeleteMultiSelectionAsync()
    {
        List<SessionTreeNodeViewModel> nodes = [.. _multiSelection];
        if (nodes.Count == 0)
        {
            return;
        }
        if (ConfirmDeleteSessions is not null
            && !await ConfirmDeleteSessions(Strings.Format("Tree_DeleteSelectedConfirm", nodes.Count)))
        {
            return;
        }
        ClearMultiSelection();
        foreach (SessionTreeNodeViewModel node in nodes)
        {
            await _repository.DeleteSessionAsync(node.Id);
            _sessionCache.Remove(node.Id);
            _statusCache.Remove(node.Id);
            _syncChannelCache.Remove(node.Id);
            if (FindSessionNode(node.Id, out SessionTreeNodeViewModel? parentGroup) is { } found)
            {
                if (parentGroup is not null)
                {
                    parentGroup.Children.Remove(found);
                }
                else
                {
                    Nodes.Remove(found);
                }
            }
        }
        SelectedNode = null;
        RefreshHasNoSessions();
    }

    /// <summary>
    /// 把多选的连接一条条移进目标分组。逐条走 <see cref="MoveSessionToGroupAsync" />:
    /// 「源分组空了连同分组一起删」那条规矩与拖放、单条移动完全一致。
    /// </summary>
    private async Task MoveMultiSelectionToGroupAsync(SessionTreeNodeViewModel? targetGroup)
    {
        if (targetGroup is not { IsGroup: true })
        {
            return;
        }
        foreach (Guid id in _multiSelection.Select(static node => node.Id).ToList())
        {
            await MoveSessionToGroupAsync(id, targetGroup.Id);
        }
    }

    // ———— 分组的操作(#571) ————

    private void RaiseOpenGroup()
    {
        if (SelectedNode is not { IsGroup: true } group)
        {
            return;
        }
        List<SessionProfile> profiles =
        [
            .. group.Children.Select(child => _sessionCache.GetValueOrDefault(child.Id)).OfType<SessionProfile>()
        ];
        if (profiles.Count > 0)
        {
            OpenManyRequested?.Invoke(profiles);
        }
    }

    private void RaiseExportGroup()
    {
        if (SelectedNode is { IsGroup: true } group)
        {
            ExportRequested?.Invoke(new SessionExportRequest([.. group.Children.Select(static child => child.Id)], group.Name));
        }
    }

    /// <summary>树上全部连接的 Id,按树上从上到下的顺序。</summary>
    private IEnumerable<Guid> OrderedSessionIds() =>
        Nodes.SelectMany(node => node.IsGroup ? node.Children : [node]).Select(static node => node.Id);

    private int GroupIndexOf(SessionTreeNodeViewModel group)
    {
        int index = 0;
        foreach (SessionTreeNodeViewModel node in Nodes)
        {
            if (!node.IsGroup)
            {
                continue;
            }
            if (ReferenceEquals(node, group))
            {
                return index;
            }
            index++;
        }
        return -1;
    }

    private int GroupCount() => Nodes.Count(static node => node.IsGroup);

    private void RaiseGroupMoveState()
    {
        this.RaisePropertyChanged(nameof(CanMoveSelectedGroupUp));
        this.RaisePropertyChanged(nameof(CanMoveSelectedGroupDown));
    }

    private Task MoveSelectedGroupByAsync(int delta) =>
        SelectedNode is { IsGroup: true } group
            ? MoveGroupAsync(group.Id, GroupIndexOf(group) + delta)
            : Task.CompletedTask;

    /// <summary>
    /// 把一个分组挪到第 <paramref name="newIndex" /> 位(只在分组之间数,从 0 起;越界按两端算),并把新的顺序落库。
    /// </summary>
    /// <remarks>
    /// 树上的改动排在第一个 await 之前,界面立刻跟着变;落库把<b>全部</b>分组的排序序号重新编成 0、1、2…
    /// —— 原先的序号可能是断开甚至重复的(删过分组、新建分组按「当时的个数」取号),只挪一个不重编,
    /// 下次加载时的先后就说不准了。
    /// </remarks>
    /// <param name="groupId">要挪的分组。</param>
    /// <param name="newIndex">挪到第几位。</param>
    public async Task MoveGroupAsync(Guid groupId, int newIndex)
    {
        List<SessionTreeNodeViewModel> groups = [.. Nodes.Where(static node => node.IsGroup)];
        int from = groups.FindIndex(node => node.Id == groupId);
        if (from < 0)
        {
            return;
        }
        newIndex = Math.Clamp(newIndex, 0, groups.Count - 1);
        if (newIndex == from)
        {
            return;
        }
        SessionTreeNodeViewModel moving = groups[from];
        groups.RemoveAt(from);
        groups.Insert(newIndex, moving);
        await ApplyGroupOrderAsync(groups);
    }

    /// <summary>
    /// 拖动分组时的落点:松手后这个分组排在第几个「空位」上(0 = 最前,分组数 = 最后)。规则只看鼠标下面是哪一行:
    /// 分组行的上半截 = 插在它前面,下半截 = 插在它后面;组内的会话行 = 插在这一组后面;
    /// 根级会话、置顶的会话与空白处 = 排到最后。
    /// </summary>
    /// <remarks>
    /// 放在视图模型里而不是视图里,是为了让落点规则可单测 —— 视图只负责找出鼠标下的那一行、算出在不在上半截。
    /// </remarks>
    /// <param name="hovered">鼠标下的那一行;空白处传 null。</param>
    /// <param name="upperHalf">鼠标在那一行的上半截。</param>
    /// <returns>空位序号。</returns>
    public int ResolveGroupDropSlot(SessionTreeNodeViewModel? hovered, bool upperHalf)
    {
        List<SessionTreeNodeViewModel> groups = [.. Nodes.Where(static node => node.IsGroup)];
        if (hovered is { IsGroup: true })
        {
            int index = groups.IndexOf(hovered);
            return index < 0 ? groups.Count : upperHalf ? index : index + 1;
        }
        if (hovered is { IsPinned: false } session
            && groups.FindIndex(group => group.Children.Contains(session)) is >= 0 and var owner)
        {
            return owner + 1;
        }
        return groups.Count;
    }

    /// <summary>空位换算成 <see cref="MoveGroupAsync" /> 要的目标位置;落回原处时为 null(松手等于什么都没做)。</summary>
    /// <param name="groupId">被拖的分组。</param>
    /// <param name="slot">由 <see cref="ResolveGroupDropSlot" /> 得到的空位。</param>
    /// <returns>目标位置,或 null。</returns>
    public int? GroupDropIndex(Guid groupId, int slot)
    {
        int from = Nodes.Where(static node => node.IsGroup).ToList().FindIndex(node => node.Id == groupId);
        if (from < 0)
        {
            return null;
        }
        // 自己原来占的那个位置拿掉之后,后面的空位都往前挪一格。
        int index = slot > from ? slot - 1 : slot;
        return index == from ? null : index;
    }

    /// <summary>拖动分组时幽灵标签的落点说明:「移到「X」之前」/「移到最后」。</summary>
    /// <param name="slot">空位。</param>
    /// <returns>说明文案。</returns>
    public string DescribeGroupDropSlot(int slot)
    {
        List<SessionTreeNodeViewModel> groups = [.. Nodes.Where(static node => node.IsGroup)];
        return slot < groups.Count
            ? Strings.Format("Tree_DragGroupBefore", groups[slot].Name)
            : Strings.Get("Tree_DragGroupToEnd");
    }

    /// <summary>分组按名称排序(自然序:「机房 2」排在「机房 10」前面;按当前区域的排序规则,中文按拼音)。</summary>
    public Task SortGroupsByNameAsync()
    {
        IComparer<string> comparer = NaturalNameComparer();
        List<SessionTreeNodeViewModel> groups = [.. Nodes.Where(static node => node.IsGroup).OrderBy(static node => node.Name, comparer)];
        return ApplyGroupOrderAsync(groups);
    }

    /// <summary>
    /// 「按名称排序」用的比较器:当前区域的排序规则 + 数字按数值比。
    /// </summary>
    /// <remarks>
    /// 资源管理器里连接本身仍按序数排(老规矩,不在这次改动里动);分组排序是用户主动点的一下,
    /// 要的就是「看着顺」—— 序数比较会把「机房 10」排在「机房 2」前面,中文则按码位排,看着毫无章法。
    /// 个别平台(不变全球化模式)不支持数值排序,退回只按区域规则比。
    /// </remarks>
    private static StringComparer NaturalNameComparer()
    {
        try
        {
            StringComparer natural = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture,
                System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);
            // 有的平台创建时不报、比较时才报:先比一次,别让排序做到一半抛出来。
            _ = natural.Compare("2", "10");
            return natural;
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            return StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, ignoreCase: true);
        }
    }

    private async Task ApplyGroupOrderAsync(List<SessionTreeNodeViewModel> ordered)
    {
        // 分组恒排在 Nodes 的最前面(未分组的会话跟在后面),逐个挪到位即可。
        for (int i = 0; i < ordered.Count; i++)
        {
            int at = Nodes.IndexOf(ordered[i]);
            if (at >= 0 && at != i)
            {
                Nodes.Move(at, i);
            }
            int menuAt = GroupNodes.IndexOf(ordered[i]);
            if (menuAt >= 0 && menuAt != i)
            {
                GroupNodes.Move(menuAt, i);
            }
            // 文件夹图标的轮换配色跟着位置走,与重新加载后的样子一致。
            ordered[i].GroupColorIndex = i % 3;
        }
        RaiseGroupMoveState();

        List<ServerGroup> stored = await _repository.GetAllGroupsAsync();
        var byId = new Dictionary<Guid, ServerGroup>();
        foreach (ServerGroup group in stored)
        {
            _ = byId.TryAdd(group.Id, group);
        }
        int next = 0;
        foreach (SessionTreeNodeViewModel node in ordered)
        {
            if (byId.Remove(node.Id, out ServerGroup? group))
            {
                await SaveSortOrderAsync(group, next);
            }
            next++;
        }
        // 库里有、树上却没有的分组(理论上不该有)排到最后,免得序号和树上的撞车。
        foreach (ServerGroup group in byId.Values.OrderBy(static g => g.SortOrder))
        {
            await SaveSortOrderAsync(group, next++);
        }
    }

    private async Task SaveSortOrderAsync(ServerGroup group, int sortOrder)
    {
        if (group.SortOrder == sortOrder)
        {
            return;
        }
        group.SortOrder = sortOrder;
        await _repository.SaveGroupAsync(group);
    }

    private void SetAllGroupsExpanded(bool expanded)
    {
        foreach (SessionTreeNodeViewModel node in Nodes.Where(static node => node.IsGroup).ToList())
        {
            node.IsExpanded = expanded;
        }
    }

    private async Task DeleteSelectedSessionAsync()
    {
        if (SelectedNode is null || SelectedNode.IsGroup)
        {
            return;
        }
        Guid sessionId = SelectedNode.Id;
        await _repository.DeleteSessionAsync(sessionId);
        _sessionCache.Remove(sessionId);
        _statusCache.Remove(sessionId);
        SessionTreeNodeViewModel? node = FindSessionNode(
            sessionId,
            out SessionTreeNodeViewModel? parentGroup
        );
        if (node is not null)
        {
            if (parentGroup is not null)
            {
                parentGroup.Children.Remove(node);
            }
            else
            {
                Nodes.Remove(node);
            }
        }
        SelectedNode = null;
        RefreshHasNoSessions();
    }
}
