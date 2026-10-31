namespace VelaShell.Docking.Model;

/// <summary>
/// 终端工作区布局:一棵 分栏/标签组 树 + 全局激活文档,并提供全部结构操作
/// (docs/dock-replacement-plan.md §2.3)。取代原 TerminalDockFactory + Dock.Model:
/// 新文档进主组;用户关闭走 <see cref="CloseDocument" />(触发 <see cref="DocumentClosed" />,
/// 下游据此断 SSH/SFTP/日志);程序撤除走 <see cref="RemoveDocument" />(静默)。
/// 空组自动折叠(主组先把兜底身份交给邻居再退场,只有根留着),单子分栏自动提升。
/// </summary>
public sealed class DockWorkspace : DockElement
{
    /// <summary>当前异步流程里「新文档落进哪一格」的票据(见 <see cref="TargetNewDocumentsAt" />)。</summary>
    private readonly AsyncLocal<TargetTicket?> _targetTicket = new();

    private DockNode _root;

    /// <summary>创建仅含主组的空工作区,主组即为初始布局树根。</summary>
    public DockWorkspace()
    {
        PrimaryGroup = new DockGroup { IsPrimary = true };
        _root = PrimaryGroup;
    }

    /// <summary>布局树根:单组时就是主组,拆分后为最外层分栏。</summary>
    public DockNode Root
    {
        get => _root;
        private set => SetField(ref _root, value);
    }

    /// <summary>
    /// 布局的兜底组:唯一一个不会被折叠掉的组,也是找不到更合适落点时新文档的归宿。
    /// </summary>
    /// <remarks>
    /// 主组清空而布局里还有别的组时,这个身份会交给幸存的邻居(见 <see cref="TryHandOverPrimary" />),
    /// 所以**不要把它缓存起来**。
    /// </remarks>
    public DockGroup PrimaryGroup { get; private set; }

    /// <summary>
    /// 当前被最大化(独占整片工作区)的窗格;<c>null</c> = 按布局树正常平铺。
    /// </summary>
    /// <remarks>
    /// 与 tmux 的 <c>resize-pane -Z</c> 是同一件事:分屏之后想把某一格看仔细,
    /// 不必先拆掉布局、看完再拼回去。**只影响渲染,不动布局树** —— 解除时原样恢复,
    /// 连比例都不用记;因此它也不需要参与任何持久化。
    /// </remarks>
    public DockGroup? MaximizedGroup
    {
        get;
        private set => SetField(ref field, value);
    }

    /// <summary>
    /// 标签放不下时换行成多排(<c>true</c>),还是留在一排里横向滚动(<c>false</c>,默认)。
    /// </summary>
    /// <remarks>
    /// 整个工作区一个开关、所有窗格一起变,与 Visual Studio 的「多行显示选项卡」同一口径;
    /// 宿主把它与设置里的 <c>AppearanceOptions.MultiRowTabs</c> 双向对齐。只对顶置的标签条生效 ——
    /// 停到左 / 右侧的标签条本来就是一个标签一行。
    /// </remarks>
    public bool MultiRowTabs
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>
    /// 横排的标签条停在窗格底部(<c>true</c>),还是顶部(<c>false</c>,默认)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="MultiRowTabs" /> 同一口径:整个工作区一个开关、所有窗格一起变。宿主把设置里的
    /// <c>AppearanceOptions.TabBarPosition</c> 灌进来,这里不往回写 —— 入口只有设置页一处。
    /// 只管<b>横排</b>的标签条:右键菜单里停到左 / 右侧的窗格(<see cref="DockGroup.TabsPosition" />)
    /// 是用户按窗格单独挑的,这个开关不去动它;它们回到横排时,就落在这里定的那一边。
    /// </remarks>
    public bool TabsAtBottom
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>全局激活文档(最后交互的组的选中标签),驱动 ActiveTerminalTab/状态栏联动。</summary>
    public DockDocument? ActiveDocument
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                ActiveDocumentChanged?.Invoke(value);
            }
        }
    }

    /// <summary>激活文档变化(合并了原 Dock 的 ActiveDockableChanged + FocusedDockableChanged)。</summary>
    public event Action<DockDocument?>? ActiveDocumentChanged;

    /// <summary>用户语义的“关闭标签”完成后触发;程序性 <see cref="RemoveDocument" /> 不触发。</summary>
    public event Action<DockDocument>? DocumentClosed;

    /// <summary>
    /// 文档离开工作区时触发(无论用户关闭还是程序撤除;组间移动不算),
    /// 供控件层清理视图缓存。
    /// </summary>
    public event Action<DockDocument>? DocumentRemoved;

    /// <summary>
    /// 文档进入工作区时触发(组间移动不算)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="DocumentRemoved" /> 成对,让"工作区是标签集合的唯一事实来源"这件事
    /// 在生命周期两端都成立。缺了这一半,宿主要挂每个标签的订阅(同步输入、会话状态、
    /// 快捷命令目标)就只能另存一份平行的标签列表去监听它的 <c>CollectionChanged</c> ——
    /// 那正是 Q-02 要拆掉的东西。
    /// </remarks>
    public event Action<DockDocument>? DocumentAdded;

    /// <summary>
    /// <see cref="ReplaceDocument" /> 换下旧文档、换上新文档之后触发(参数依次为旧、新)。
    /// </summary>
    /// <remarks>
    /// 「连接中」占位换成真文档在 <see cref="DocumentRemoved" /> / <see cref="DocumentAdded" /> 里看
    /// 是两件不相干的事;需要跟住「这是同一个标签」的一方(还原布局模板时按模板顺序给后到的标签找位置)
    /// 靠这一条把旧文档上的记账转给新文档。
    /// </remarks>
    public event Action<DockDocument, DockDocument>? DocumentReplaced;

    /// <summary>
    /// 新文档落点的外部裁决:返回 null 时按默认规则(见 <see cref="TargetGroupForNewDocument" />)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为还原布局模板而设:模板里每个标签要落回它自己的那个窗格、那个位置,而打开它的是宿主
    /// 既有的连接流程(先弹凭据框、握手、证书确认……),那些流程自己调 <see cref="AddDocument" />,
    /// 不该为了「放到哪」各加一个参数。裁决方在这里认出「这是我在等的那个标签」并给出落点。
    /// </para>
    /// <para>
    /// 只管<b>新进来</b>的文档;组间移动、原位替换都不经过它。裁决给出的组已经不在树上
    /// (用户在还原途中把那个窗格关了)时退回默认规则。
    /// </para>
    /// </remarks>
    public Func<DockDocument, DockPlacement?>? PlacementResolver { get; set; }

    // ---- 查询 ----

    /// <summary>深度遍历布局树,返回其中全部标签组。</summary>
    public IEnumerable<DockGroup> AllGroups() => EnumerateGroups(Root);

    /// <summary>返回工作区内所有组的全部文档(拉平的标签集合)。</summary>
    public IEnumerable<DockDocument> AllDocuments() => AllGroups().SelectMany(group => group.Documents);

    /// <summary>查找该文档当前所属的组,未在树上则返回 null。</summary>
    public DockGroup? FindGroup(DockDocument document) =>
        AllGroups().FirstOrDefault(group => group.Documents.Contains(document));

    /// <summary>布局里是否不止一个标签组(= 当前处于分屏状态)。</summary>
    /// <remarks>
    /// 窗格移焦手势要靠它做**有条件拦截**:只有一个窗格时 Alt+方向键必须原样送给远端
    /// (zsh / fish 里有人绑了它),不能被无条件吃掉。
    /// </remarks>
    public bool HasMultipleGroups => AllGroups().Skip(1).Any();

    /// <summary>
    /// 从给定组出发,沿父链找到指定方向上相邻的那个标签组;边缘处返回 null。
    /// </summary>
    /// <remarks>
    /// 做法是沿父链上溯,找第一个**方向匹配**的分栏(左右看水平分栏,上下看垂直分栏),
    /// 在它的子节点里取相邻的那个,再向下钻到一个具体的组。向左/上时从相邻子树的
    /// **末端**进入,向右/下时从**首端**进入 —— 这样跨越嵌套分屏时,落点总是视觉上最近的那个窗格。
    /// </remarks>
    /// <param name="from">出发的组。</param>
    /// <param name="direction">方向。</param>
    /// <returns>相邻的组;该方向上没有邻居时为 null。</returns>
    public static DockGroup? FindNeighborGroup(DockGroup from, DockDirection direction)
    {
        ArgumentNullException.ThrowIfNull(from);
        bool horizontal = direction is DockDirection.Left or DockDirection.Right;
        int step = direction is DockDirection.Left or DockDirection.Up ? -1 : 1;

        DockNode node = from;
        while (node.Parent is { } split)
        {
            bool matchesAxis = horizontal == (split.Orientation == DockOrientation.Horizontal);
            int index = split.Children.IndexOf(node);
            int target = index + step;
            if (matchesAxis && index >= 0 && target >= 0 && target < split.Children.Count)
            {
                return Descend(split.Children[target], enterFromEnd: step < 0);
            }
            node = split;
        }
        return null;
    }

    /// <summary>向下钻到一个具体的组:遇到分栏就按进入方向取首/末子节点。</summary>
    private static DockGroup? Descend(DockNode node, bool enterFromEnd)
    {
        while (true)
        {
            switch (node)
            {
                case DockGroup group:
                    return group;
                case DockSplit { Children.Count: > 0 } split:
                    node = enterFromEnd ? split.Children[^1] : split.Children[0];
                    continue;
                default:
                    return null;
            }
        }
    }

    private static IEnumerable<DockGroup> EnumerateGroups(DockNode node)
    {
        switch (node)
        {
            case DockGroup group:
                yield return group;
                break;
            case DockSplit split:
                foreach (DockNode child in split.Children.ToArray())
                {
                    foreach (DockGroup group in EnumerateGroups(child))
                    {
                        yield return group;
                    }
                }
                break;
        }
    }

    // ---- 增删与激活 ----

    /// <summary>
    /// 把文档放进窗格并激活:给了 <paramref name="target" />(且还在树上)就落进那一格,
    /// 否则按默认规则(见 <see cref="TargetGroupForNewDocument" />)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 显式目标压过「空窗格优先」:标签条上的「+」是在说「在<b>这一格</b>里开」,
    /// 布局里另有一块空窗格不改变这个意思(维护者 2026-10-25 定)。
    /// </para>
    /// <para>
    /// 落点的先后:显式目标 &gt; 当前异步流程带着的票据(<see cref="TargetNewDocumentsAt" />)&gt;
    /// <see cref="PlacementResolver" /> &gt; 默认规则。
    /// </para>
    /// </remarks>
    /// <param name="document">新文档。</param>
    /// <param name="target">落进哪一格;null = 不指定。</param>
    public void AddDocument(DockDocument document, DockGroup? target = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        DockGroup? group = target is not null && IsOnTree(target) ? target : ClaimTargetTicket();
        if (group is null && PlacementResolver?.Invoke(document) is { } placement && IsOnTree(placement.Group))
        {
            AddPlaced(document, placement);
            return;
        }
        (group ?? TargetGroupForNewDocument()).Documents.Add(document);
        // 先报"来了"再激活:订阅方(宿主的每标签接线)要在活动标签切过去之前就位,
        // 否则激活回调里读到的还是一个没接好线的标签。
        DocumentAdded?.Invoke(document);
        ActivateDocument(document);
    }

    /// <summary>
    /// 让当前异步流程里建的<b>第一个</b>新文档落进 <paramref name="group" />(释放返回值即作废)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 标签条上的「+」先弹新建连接对话框、再走连接流程(凭据框、握手、证书确认……),标签是那些流程里
    /// 很深的地方自己 <see cref="AddDocument" /> 出来的。与其给每一层都加一个「放到哪」的参数,
    /// 不如在 <see cref="AsyncLocal{T}" /> 里带一张票:异步上下文顺着 <c>await</c> 传下去,对话框开多久都认得出是谁;
    /// 别的流程(自动重连、还原布局模板)看不到这张票,不会被拐进这一格。
    /// </para>
    /// <para>
    /// 一张票只用一次:连接流程里后建的东西(同一流程顺手开的第二个文档)照默认规则落。释放之后作废 ——
    /// 流程里另起的后台任务会继承上下文,不能让它们在流程结束之后建的东西也落进这一格。
    /// 那一格在流程途中被关掉了,就按默认规则落。
    /// </para>
    /// <para>
    /// 必须在 <c>async</c> 方法里调用并在同一个方法里释放:<see cref="AsyncLocal{T}" /> 的改动跟着那个方法的执行上下文走,
    /// 方法返回时自动退回调用方原来的样子。
    /// </para>
    /// </remarks>
    /// <param name="group">落进哪一格。</param>
    /// <returns>释放即作废票据。</returns>
    public IDisposable TargetNewDocumentsAt(DockGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        TargetTicket? previous = _targetTicket.Value;
        var ticket = new TargetTicket(group);
        _targetTicket.Value = ticket;
        return new TargetScope(this, ticket, previous);
    }

    /// <summary>取当前异步流程里还没用过、没作废的票据所指的那一格(用掉它);没有或那一格已不在树上时为 null。</summary>
    private DockGroup? ClaimTargetTicket()
    {
        if (_targetTicket.Value is not { Used: false, Expired: false, Group: { } group } ticket)
        {
            return null;
        }
        ticket.Used = true;
        return IsOnTree(group) ? group : null;
    }

    /// <summary>一张「落进这一格」的票据。</summary>
    private sealed class TargetTicket(DockGroup group)
    {
        /// <summary>落进哪一格;作废之后为 null。</summary>
        public DockGroup? Group { get; private set; } = group;

        public bool Used { get; set; }

        public bool Expired { get; private set; }

        /// <summary>
        /// 作废,并放掉那一格:流程里另起的后台任务(SSH 的收包、保活循环……)捕获了执行上下文,会一直攥着这张票,
        /// 票再攥着组,用户关掉的那个窗格连同它最后的标签就回收不了。
        /// </summary>
        public void Expire()
        {
            Expired = true;
            Group = null;
        }
    }

    /// <summary>释放即作废票据,并把上下文退回发票之前的样子。</summary>
    private sealed class TargetScope(DockWorkspace owner, TargetTicket ticket, TargetTicket? previous) : IDisposable
    {
        public void Dispose()
        {
            ticket.Expire();
            if (ReferenceEquals(owner._targetTicket.Value, ticket))
            {
                owner._targetTicket.Value = previous;
            }
        }
    }

    /// <summary>按外部裁决放下新文档(见 <see cref="PlacementResolver" />)。</summary>
    /// <remarks>
    /// <para>
    /// 固定状态在插入<b>之前</b>定下:插入位要按它收进固定区或普通区(<see cref="ClampInsertIndex" />),
    /// 先插再 <see cref="SetPinned" /> 会把标签挪到交界处,模板里排好的顺序就乱了。
    /// </para>
    /// <para>
    /// 不要求激活的文档不抢焦点 —— 还原一套模板时标签陆续连上,每来一个就把焦点拽过去,
    /// 用户在等的这几秒里什么也干不了。但工作区还没有活动文档时总要有一个,
    /// 而组里正选中的就是全局活动文档时,改选中等于改活动(否则活动文档会是一个看不见的标签)。
    /// </para>
    /// </remarks>
    private void AddPlaced(DockDocument document, DockPlacement placement)
    {
        DockGroup group = placement.Group;
        document.IsPinned = placement.Pinned;
        int index = ClampInsertIndex(group, document, Math.Clamp(placement.Index, 0, group.Documents.Count));
        group.Documents.Insert(index, document);
        DocumentAdded?.Invoke(document);
        bool selectsActive = placement.SelectInGroup
                             && ActiveDocument is not null
                             && ReferenceEquals(group.ActiveDocument, ActiveDocument);
        if (placement.Activate || ActiveDocument is null || selectsActive)
        {
            ActivateDocument(document);
        }
        else if (placement.SelectInGroup || group.ActiveDocument is null)
        {
            group.ActiveDocument = document;
        }
    }

    /// <summary>新文档的落点(没有显式目标时):空窗格 &gt; 正在用的窗格 &gt; 主组。</summary>
    /// <remarks>
    /// <para>
    /// <b>空窗格优先</b>:拆分只有一个标签的组会在原地留下一块写着"拖放标签到这里"的空面板,
    /// 那块空白就是等着被填的 —— 新开的会话理应落进去,而不是挤进旁边那条已经有标签的条,
    /// 把空面板晾在一边。
    /// </para>
    /// <para>
    /// <b>其次是当前窗格</b>:分屏之后人眼盯着右半屏工作,新标签却开在左半屏的标签条上 ——
    /// 焦点跟着跑过去,视线还留在原处。"新标签开在我正在用的这半边"是 VS Code /
    /// Windows Terminal 一致的做法,也是分屏之后唯一不让人找标签的做法。
    /// </para>
    /// <para>
    /// 主组只作兜底:没有空窗格、也没有活动文档(刚启动的空布局)时用它。
    /// </para>
    /// </remarks>
    private DockGroup TargetGroupForNewDocument()
    {
        if (PrimaryGroup.Documents.Count == 0)
        {
            return PrimaryGroup;
        }
        if (AllGroups().FirstOrDefault(group => group.Documents.Count == 0) is { } empty)
        {
            return empty;
        }
        return ActiveDocument is { } active && FindGroup(active) is { } activeGroup ? activeGroup : PrimaryGroup;
    }

    /// <summary>
    /// 原位替换一个文档:新文档接手旧文档在组内的**位置**与选中/激活状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「连接中」占位标签换成连上之后的真文档走这一条。用"先 Remove 再 Add"是不行的:
    /// <see cref="AddDocument" /> 永远追加到主组末尾,于是用户眼看着标签从原地跳到最右边;
    /// 而且旧文档若不是当前激活的(用户在等连接时切去了别的标签),Add 还会把焦点抢回来。
    /// </para>
    /// <para>
    /// 事件按"旧的走了、新的来了"如实播报:视图层的内容控件缓存正是靠
    /// <see cref="DocumentRemoved" /> 丢弃旧视图的(见 <c>DockWorkspaceControl</c>)。
    /// </para>
    /// </remarks>
    /// <param name="oldDocument">要被替换掉的文档;不在工作区内时本方法为空操作。</param>
    /// <param name="newDocument">接手其位置的新文档。</param>
    public void ReplaceDocument(DockDocument oldDocument, DockDocument newDocument)
    {
        ArgumentNullException.ThrowIfNull(oldDocument);
        ArgumentNullException.ThrowIfNull(newDocument);
        if (FindGroup(oldDocument) is not { } group)
        {
            return;
        }
        int index = group.Documents.IndexOf(oldDocument);
        bool wasGroupActive = ReferenceEquals(group.ActiveDocument, oldDocument);
        bool wasWorkspaceActive = ReferenceEquals(ActiveDocument, oldDocument);
        // 固定也一并接手:用户在「连接中」时就把它固定了,连上之后不该又掉回普通标签堆里。
        // 位置原样不动,所以"固定的在前"这条顺序不会被打破。
        newDocument.IsPinned = oldDocument.IsPinned;
        group.Documents[index] = newDocument;
        if (wasGroupActive)
        {
            group.ActiveDocument = newDocument;
        }
        DocumentRemoved?.Invoke(oldDocument);
        DocumentAdded?.Invoke(newDocument);
        DocumentReplaced?.Invoke(oldDocument, newDocument);
        // 激活放在事件之后:订阅方(宿主的每标签接线)要在活动标签切过去之前就位。
        if (wasWorkspaceActive)
        {
            ActivateDocument(newDocument);
        }
    }

    /// <summary>激活文档:选中其标签并设为全局激活。</summary>
    public void ActivateDocument(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        // 切到别的窗格 = 此刻想同时看见它们:与 tmux 的 select-pane 一样顺手解除最大化。
        // 否则用户会对着一个"焦点已经在别处、屏幕上却还是那一格"的界面发愣。
        if (MaximizedGroup is { } maximized && !ReferenceEquals(maximized, group))
        {
            MaximizedGroup = null;
        }
        group.ActiveDocument = document;
        ActiveDocument = document;
    }

    /// <summary>切换某个窗格的最大化状态;布局里只有一个窗格时是空操作(没有可让位的邻居)。</summary>
    /// <param name="group">要最大化 / 还原的窗格。</param>
    public void ToggleMaximizeGroup(DockGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (ReferenceEquals(MaximizedGroup, group))
        {
            MaximizedGroup = null;
            return;
        }
        if (HasMultipleGroups && IsOnTree(group))
        {
            MaximizedGroup = group;
        }
    }

    /// <summary>把所有分栏恢复成均分 —— 分割条拖乱之后的一键复位。</summary>
    public void EqualizePanes()
    {
        foreach (DockNode node in EnumerateNodes(Root))
        {
            node.Proportion = double.NaN; // NaN = 与兄弟均分(渲染层按 1 星处理)
        }
    }

    /// <summary>
    /// 关掉整个窗格:还有标签就走确认闸逐一关闭(关空之后窗格自会折叠),
    /// 已经是空窗格则直接从布局里撤掉。
    /// </summary>
    /// <remarks>
    /// 空窗格(拆分留下的那块"拖放标签到这里")在此之前<b>没有任何撤销入口</b> ——
    /// 只能把邻居的标签拖进去、再拖回来,靠副作用把它挤掉。一个显式的"关闭窗格"
    /// 才是这块空白该有的出口。
    /// </remarks>
    /// <param name="group">要关闭的窗格。</param>
    public void ClosePane(DockGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Documents.Count > 0)
        {
            RequestCloseMany([.. group.Documents]);
            return;
        }
        CollapseIfEmpty(group);
    }

    /// <summary>
    /// 静默移除(撤掉连接失败的标签等程序行为),不触发 <see cref="DocumentClosed" />。
    /// </summary>
    public void RemoveDocument(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        int index = group.Documents.IndexOf(document);
        group.Documents.RemoveAt(index);
        if (ReferenceEquals(group.ActiveDocument, document))
        {
            group.ActiveDocument = group.Documents.Count > 0
                                       ? group.Documents[Math.Min(index, group.Documents.Count - 1)]
                                       : null;
        }
        CollapseIfEmpty(group);
        if (ReferenceEquals(ActiveDocument, document))
        {
            // 组仍在树上则接管其新选中标签;组已折叠则退回仍有文档的第一个组。
            ActiveDocument = group.ActiveDocument
                             ?? AllGroups().FirstOrDefault(g => g.ActiveDocument is not null)?.ActiveDocument;
        }
        DocumentRemoved?.Invoke(document);
    }

    /// <summary>
    /// 关闭前的拦截器:返回 false 即取消本次关闭。由宿主装上"关闭已连接标签前确认"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 装在这一层而不是各个入口:用户能触发关闭的地方有六个(标签 ×、Ctrl+W、命令面板、
    /// 右键的 关闭其他 / 关闭全部 / 关闭左侧 / 关闭右侧),逐个去接必然漏。
    /// </para>
    /// <para>
    /// <see cref="CloseDocument" /> 保持**无条件**:拦截器放行后由它执行,程序性关闭
    /// (连接失败撤标签、退出时清场)也照旧直接调它,不会被确认框挡住。
    /// </para>
    /// </remarks>
    public Func<IReadOnlyList<DockDocument>, Task<bool>>? CloseInterceptor { get; set; }

    /// <summary>用户语义的关闭请求:先过拦截器,通过了才真的关。</summary>
    /// <param name="document">要关闭的文档。</param>
    public void RequestClose(DockDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RequestCloseMany([document]);
    }

    /// <summary>
    /// 批量关闭请求:一次询问、一次放行,而不是逐个弹确认框把人问烦。
    /// </summary>
    /// <param name="documents">要关闭的文档集合(会先取快照,关闭过程改动集合不影响遍历)。</param>
    public void RequestCloseMany(IEnumerable<DockDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        DockDocument[] targets = [.. documents];
        if (targets.Length == 0)
        {
            return;
        }
        if (CloseInterceptor is not { } interceptor)
        {
            CloseEach(targets);
            return;
        }
        _ = RequestCloseAsync(interceptor, targets);
    }

    private async Task RequestCloseAsync(
        Func<IReadOnlyList<DockDocument>, Task<bool>> interceptor,
        DockDocument[] targets)
    {
        if (await interceptor(targets).ConfigureAwait(true))
        {
            CloseEach(targets);
        }
    }

    private void CloseEach(DockDocument[] targets)
    {
        foreach (DockDocument document in targets)
        {
            CloseDocument(document);
        }
    }

    /// <summary>用户语义的关闭:尊重 CanClose,移除后触发 <see cref="DocumentClosed" />。</summary>
    public void CloseDocument(DockDocument document)
    {
        if (!document.CanClose || FindGroup(document) is null)
        {
            return;
        }
        RemoveDocument(document);
        DocumentClosed?.Invoke(document);
    }

    // 下面四个批量关闭一律**绕开已固定的标签**(#521):固定就是为了让某几条会话
    // 挺过"一把关掉一片"。要关固定标签得冲着它本人去 —— 右键「关闭」、Ctrl+W,或先取消固定。

    /// <summary>关闭同组内除该文档与已固定标签以外的所有标签(组内语义与原 Dock 一致)。</summary>
    public void CloseOtherDocuments(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        // 经 RequestCloseMany:一次确认放行全部,而不是逐个弹框。
        RequestCloseMany([.. group.Documents.Where(d => !d.IsPinned && !ReferenceEquals(d, document))]);
    }

    /// <summary>关闭该文档所在组的所有未固定标签(含自身,除非它自己就是固定的)。</summary>
    /// <remarks>经 <see cref="RequestCloseMany" />:一次询问放行全部,而不是逐个弹框。</remarks>
    public void CloseAllDocuments(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        RequestCloseMany([.. group.Documents.Where(d => !d.IsPinned)]);
    }

    /// <summary>关闭同组内位于该文档左侧的所有未固定标签。</summary>
    public void CloseLeftDocuments(DockDocument document) => CloseToSide(document, left: true);

    /// <summary>关闭同组内位于该文档右侧的所有未固定标签。</summary>
    public void CloseRightDocuments(DockDocument document) => CloseToSide(document, left: false);

    private void CloseToSide(DockDocument document, bool left)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        int index = group.Documents.IndexOf(document);
        IEnumerable<DockDocument> side = left
                                             ? group.Documents.Take(index)
                                             : group.Documents.Skip(index + 1);
        // 「关闭左侧/右侧」同样要过确认闸 —— 一次静默关掉半屏已连接会话,
        // 恰恰是这道闸要防的事故。
        RequestCloseMany([.. side.Where(d => !d.IsPinned)]);
    }

    // ---- 固定标签 ----

    /// <summary>
    /// 固定或取消固定一个标签,并把它挪到固定区与普通区的交界处。
    /// </summary>
    /// <remarks>
    /// 固定 = 排到固定区的末尾,取消固定 = 排到普通区的开头 —— 两种情况下标签都只挪到
    /// "离原位最近的合法位置",用户的视线不用满条去找它(VS Code 同一做法)。
    /// </remarks>
    /// <param name="document">要改的标签;不在工作区内时为空操作。</param>
    /// <param name="pinned">固定(<c>true</c>)还是取消固定。</param>
    public void SetPinned(DockDocument document, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsPinned == pinned || FindGroup(document) is not { } group)
        {
            return;
        }
        int boundary = group.PinnedCount; // 改之前的固定区长度 = 交界处
        document.IsPinned = pinned;
        int from = group.Documents.IndexOf(document);
        int to = pinned ? boundary : boundary - 1;
        if (from != to)
        {
            group.Documents.Move(from, to);
        }
    }

    /// <summary>
    /// 把"插到第 <paramref name="index" /> 个标签前面"收进 <paramref name="document" /> 能去的区间:
    /// 固定标签只能落在固定区里,普通标签只能落在固定区之后。
    /// </summary>
    /// <remarks>
    /// <paramref name="index" /> 按<b>插入前</b>的集合计(同组拖动时文档本身还在里面),
    /// 与拖拽控制器算插入线的口径一致;同组、跨组两种情况用同一个公式都对,
    /// 因为固定区长度里是否算上文档本身,恰好就是它能落的边界。
    /// </remarks>
    /// <param name="target">要落进去的组。</param>
    /// <param name="document">被拖的文档。</param>
    /// <param name="index">原始插入位。</param>
    /// <returns>收进合法区间后的插入位。</returns>
    public static int ClampInsertIndex(DockGroup target, DockDocument document, int index)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(document);
        int pinned = target.PinnedCount;
        return document.IsPinned
                   ? Math.Clamp(index, 0, pinned)
                   : Math.Clamp(index, pinned, target.Documents.Count);
    }

    // ---- 拆分与停靠 ----

    /// <summary>
    /// 右键菜单“水平/垂直拆分”:把文档移入紧邻其所属组新建的组
    /// (水平 = 新组在右,垂直 = 新组在下,各占一半)。组内唯一文档时同样拆分,
    /// 原组留空作为放置目标(所有组行为一致);
    /// 空组会在其兄弟结构收敛时自动回收(见 <see cref="HoistIfSingle" />)。
    /// </summary>
    public void SplitDocument(DockDocument document, DockOrientation orientation)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        DockGroup newGroup = DetachToNewGroup(document, group, collapseSource: false);
        InsertNeighbor(group, newGroup, orientation, after: true);
        ActivateDocument(document);
    }

    /// <summary>
    /// 拖放停靠:Center = 并入目标组(index 为插入位,-1 为末尾);
    /// 四边 = 在目标组对应侧拆分出新组。
    /// </summary>
    public void DockTo(DockDocument document, DockGroup target, DockPosition position, int index = -1)
    {
        if (FindGroup(document) is not { } source)
        {
            return;
        }
        if (position == DockPosition.Center)
        {
            MoveToGroup(document, source, target, index);
            return;
        }
        DockOrientation orientation = position is DockPosition.Left or DockPosition.Right
                                          ? DockOrientation.Horizontal
                                          : DockOrientation.Vertical;
        bool after = position is DockPosition.Right or DockPosition.Bottom;

        // 拖到自身组的边缘且组里只有它自己 = 语义上的拆分(与右键拆分一致):原组留空。
        if (ReferenceEquals(source, target) && source.Documents.Count == 1)
        {
            DockGroup splitGroup = DetachToNewGroup(document, source, collapseSource: false);
            InsertNeighbor(source, splitGroup, orientation, after);
            ActivateDocument(document);
            return;
        }
        DockGroup newGroup = DetachToNewGroup(document, source);
        // source 若因清空被折叠,目标组仍在树上(source != target 已由上面分支保证),
        // 极端情况下兜底锚定主组。
        DockGroup anchor = IsOnTree(target) ? target : PrimaryGroup;
        InsertNeighbor(anchor, newGroup, orientation, after);
        ActivateDocument(document);
    }

    /// <summary>
    /// 把一棵新建的布局子树放进工作区(打开布局模板):工作区里还没有任何文档时它就是整个布局,
    /// 否则作为新的一栏排在现有布局的右侧。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 子树里的组通常是空的 —— 标签随后由各自的连接流程陆续放进来(见 <see cref="PlacementResolver" />)。
    /// 空组在这里不会被折叠:折叠只在「最后一个标签离开」时发生,一个还没等到标签的组不算。
    /// 等不来标签的组由调用方用 <see cref="ClosePane" /> 收掉。
    /// </para>
    /// <para>
    /// 有文档时不去动现有的布局:那些都是正连着的会话,模板没有资格替用户关掉它们;
    /// 也不往某个现有窗格里塞,否则模板的分屏会跟用户自己的分屏搅在一起,哪块是哪块分不清。
    /// 整块排在右侧,用户关掉旧标签之后模板那一栏自然铺满。
    /// </para>
    /// </remarks>
    /// <param name="tree">调用方新建的子树(分栏与标签组);不能已经挂在别处,至少含一个标签组。</param>
    /// <returns>子树里的全部标签组,按树上的顺序。</returns>
    /// <exception cref="ArgumentException">子树已经挂在别处,或者里面没有标签组。</exception>
    public IReadOnlyList<DockGroup> InsertLayout(DockNode tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (tree.Parent is not null || IsOnTree(tree))
        {
            throw new ArgumentException("The layout must be a detached tree.", nameof(tree));
        }
        DockGroup[] groups = [.. EnumerateGroups(tree)];
        if (groups.Length == 0)
        {
            throw new ArgumentException("The layout must contain at least one pane.", nameof(tree));
        }
        MaximizedGroup = null;
        tree.Proportion = double.NaN;
        if (!AllDocuments().Any())
        {
            // 空工作区:原来的(空)主组退场,兜底身份交给模板里的第一个组 —— 它就在用户眼前左上角。
            PrimaryGroup.IsPrimary = false;
            groups[0].IsPrimary = true;
            PrimaryGroup = groups[0];
            Root = tree;
            return groups;
        }
        DockNode existing = Root;
        var split = new DockSplit(DockOrientation.Horizontal);
        existing.Proportion = double.NaN;
        split.Children.Add(existing);
        split.Children.Add(tree);
        Root = split;
        return groups;
    }

    /// <summary>组内拖拽重排;固定标签只在固定区里挪,普通标签只在固定区之后挪。</summary>
    public void MoveDocument(DockDocument document, int newIndex)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        int oldIndex = group.Documents.IndexOf(document);
        int pinned = group.PinnedCount;
        newIndex = document.IsPinned
                       ? Math.Clamp(newIndex, 0, pinned - 1)
                       : Math.Clamp(newIndex, pinned, group.Documents.Count - 1);
        if (oldIndex != newIndex)
        {
            group.Documents.Move(oldIndex, newIndex);
        }
    }

    private void MoveToGroup(DockDocument document, DockGroup source, DockGroup target, int index)
    {
        if (ReferenceEquals(source, target))
        {
            if (index >= 0)
            {
                MoveDocument(document, Math.Min(index, source.Documents.Count - 1));
            }
            ActivateDocument(document);
            return;
        }
        source.Documents.Remove(document);
        if (ReferenceEquals(source.ActiveDocument, document))
        {
            source.ActiveDocument = source.Documents.FirstOrDefault();
        }
        // 固定身份跟着标签走,落点收进它在目标组里能去的那一段(-1 = 那一段的末尾)。
        int insertAt = ClampInsertIndex(target, document, index < 0 ? target.Documents.Count : index);
        target.Documents.Insert(insertAt, document);
        CollapseIfEmpty(source);
        ActivateDocument(document);
    }

    /// <summary>
    /// 把文档从原组摘出放入一个新组,返回新组。拆分路径传 collapseSource: false,
    /// 让原组即使清空也留在原位(作为空放置面板);移动路径保持自动折叠。
    /// </summary>
    private DockGroup DetachToNewGroup(DockDocument document, DockGroup source, bool collapseSource = true)
    {
        source.Documents.Remove(document);
        if (ReferenceEquals(source.ActiveDocument, document))
        {
            source.ActiveDocument = source.Documents.FirstOrDefault();
        }
        var newGroup = new DockGroup();
        newGroup.Documents.Add(document);
        newGroup.ActiveDocument = document;
        if (collapseSource)
        {
            CollapseIfEmpty(source);
        }
        return newGroup;
    }

    /// <summary>
    /// 把 newGroup 插到 anchor 的旁边:父分栏方向相同则同级插入(平分 anchor 的比例),
    /// 否则用新分栏替换 anchor 再装入两者(各占一半)。
    /// </summary>
    private void InsertNeighbor(DockGroup anchor, DockGroup newGroup, DockOrientation orientation, bool after)
    {
        if (anchor.Parent is { } parent && parent.Orientation == orientation)
        {
            int i = parent.Children.IndexOf(anchor);
            if (!double.IsNaN(anchor.Proportion))
            {
                double half = anchor.Proportion / 2;
                anchor.Proportion = half;
                newGroup.Proportion = half;
            }
            parent.Children.Insert(after ? i + 1 : i, newGroup);
            return;
        }
        var split = new DockSplit(orientation) { Proportion = anchor.Proportion };
        ReplaceNode(anchor, split);
        anchor.Proportion = double.NaN;
        split.Children.Add(after ? anchor : newGroup);
        split.Children.Add(after ? newGroup : anchor);
    }

    // ---- 树维护 ----

    /// <summary>节点是否还挂在当前布局树上(折叠掉的组仍被调用方持有引用,得能问出来)。</summary>
    private bool IsOnTree(DockNode node)
    {
        DockNode current = node;
        while (current.Parent is { } parent)
        {
            current = parent;
        }
        return ReferenceEquals(current, Root);
    }

    /// <summary>深度遍历布局树的全部节点(分栏与组都算)。</summary>
    private static IEnumerable<DockNode> EnumerateNodes(DockNode node)
    {
        yield return node;
        if (node is not DockSplit split)
        {
            yield break;
        }
        foreach (DockNode child in split.Children.ToArray())
        {
            foreach (DockNode descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private void ReplaceNode(DockNode oldNode, DockNode newNode)
    {
        if (ReferenceEquals(Root, oldNode))
        {
            Root = newNode;
            newNode.Parent = null;
            return;
        }
        DockSplit parent = oldNode.Parent!;
        int index = parent.Children.IndexOf(oldNode);
        parent.Children[index] = newNode;
    }

    /// <summary>
    /// 空组从布局里退场:主组同样退场,只是先把"兜底"的身份交出去。
    /// </summary>
    /// <remarks>
    /// 早先这里对主组一律早退(<c>IsPrimary || …</c>),结果是分屏之后关光左侧的标签,
    /// 右侧那半永远填不满整片区域 —— 左边留着的正是那个空的、"永不折叠"的主组。
    /// 需要不折叠的其实只有<b>根</b>(布局树总得有个底),而不是某个特定的组;
    /// 由 <c>group.Parent is not { } parent</c> 这一条兜住:根节点没有父分栏。
    /// </remarks>
    private void CollapseIfEmpty(DockGroup group)
    {
        if (group.Documents.Count > 0 || group.Parent is not { } parent)
        {
            return;
        }
        if (group.IsPrimary && !TryHandOverPrimary(group))
        {
            return; // 没人能接手兜底,主组只好原地留着。
        }
        parent.Children.Remove(group);
        HoistIfSingle(parent);
        NormalizeMaximized();
    }

    /// <summary>把"主组"的身份交给幸存的邻居;没人可交则返回 false(调用方据此放弃折叠)。</summary>
    /// <remarks>
    /// 优先交给同一分栏里的兄弟,而且是折叠之后<b>会接管这块地方</b>的那个 ——
    /// 于是新文档出现在用户眼睛刚才盯着的位置,而不是布局树另一头的某个窗格。
    /// </remarks>
    /// <param name="leaving">即将退场的空主组。</param>
    /// <returns>成功易主返回 <c>true</c>。</returns>
    private bool TryHandOverPrimary(DockGroup leaving)
    {
        DockGroup? successor = leaving.Parent?.Children
                                      .Where(child => !ReferenceEquals(child, leaving))
                                      .Select(child => Descend(child, enterFromEnd: false))
                                      .FirstOrDefault(group => group is not null);
        successor ??= AllGroups().FirstOrDefault(group => !ReferenceEquals(group, leaving));
        if (successor is null)
        {
            return false;
        }
        leaving.IsPrimary = false;
        successor.IsPrimary = true;
        PrimaryGroup = successor;
        return true;
    }

    /// <summary>结构变动后校正最大化状态:被最大化的窗格已不在树上、或布局只剩一格时解除。</summary>
    private void NormalizeMaximized()
    {
        if (MaximizedGroup is { } maximized && (!IsOnTree(maximized) || !HasMultipleGroups))
        {
            MaximizedGroup = null;
        }
    }

    private void HoistIfSingle(DockSplit split)
    {
        if (split.Children.Count != 1)
        {
            return;
        }
        DockNode child = split.Children[0];
        split.Children.RemoveAt(0);
        child.Proportion = split.Proportion;
        ReplaceNode(split, child);

        // 提升出来的若是一个空组(拆分留下的空面板,兄弟已全部关闭),顺带回收,
        // 不让空面板独自留在布局里;递归令上层分栏继续收敛。主组不必在这里特判 ——
        // CollapseIfEmpty 自己会先交出兜底身份,而升到根之后它没有父分栏、自然留下。
        if (child is DockGroup { Documents.Count: 0 } emptyGroup)
        {
            CollapseIfEmpty(emptyGroup);
        }
    }
}
