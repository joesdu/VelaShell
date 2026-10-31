using System.Diagnostics;
using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Docking.Model;

namespace VelaShell.Services.Layouts;

/// <summary>模板里一个没能打开的标签为什么没打开。</summary>
public enum LayoutMissingReason
{
    /// <summary>它指向的连接配置已经删掉了。</summary>
    ProfileDeleted,

    /// <summary>本机没有这个 shell(卸载了,或者是别的平台上存的)。</summary>
    ShellUnavailable,

    /// <summary>这个版本不认识的标签类型,或者数据不完整。</summary>
    Unsupported,
}

/// <summary>一个没能打开的标签:保存模板时它叫什么、为什么没开。</summary>
/// <param name="Title">保存时标签上的标题。</param>
/// <param name="Reason">原因。</param>
public sealed record LayoutMissingDocument(string Title, LayoutMissingReason Reason);

/// <summary>打开一个布局模板的结果。</summary>
/// <param name="Opened">发起打开的标签数(各自连没连上看各自的标签)。</param>
/// <param name="Missing">没能打开、已跳过的标签。</param>
public sealed record LayoutOpenResult(int Opened, IReadOnlyList<LayoutMissingDocument> Missing);

/// <summary>
/// 按布局模板搭出分屏、逐个开标签,并让每个标签落回它在模板里的那个窗格、那个位置。
/// </summary>
/// <remarks>
/// <para>
/// <b>先搭骨架、再开标签</b>:模板里的分屏先以空组的形式放进工作区(<see cref="DockWorkspace.InsertLayout" />),
/// 再按模板顺序发起连接,最多 <see cref="Concurrency" /> 条同时握手(与资源管理器的整组打开同一口径)。
/// 标签是宿主既有的连接流程自己建的 —— 缺凭据先弹框、认证失败原地重试、证书确认 —— 这里一处也不重写。
/// </para>
/// <para>
/// <b>怎么认出「这是第几号槽位的标签」</b>:那些流程建标签时只会调 <see cref="DockWorkspace.AddDocument" />,
/// 不带任何「放到哪」的信息,而且建标签之前可能先等用户填完凭据,到达顺序与模板顺序不一致。
/// 每个槽位的打开流程都跑在一个 <see cref="AsyncLocal{T}" /> 里带着自己的票据:<c>AddDocument</c>
/// 经 <see cref="DockWorkspace.PlacementResolver" /> 回头问「这是谁」,问到的就是正在建这个标签的那条流程的票据 ——
/// 异步上下文顺着 <c>await</c> 一路传下去,凭据框弹多久都不会认错人。一张票只用一次,流程结束即作废:
/// 流程里另起的后台任务会继承上下文,不能让它们之后建的东西也被当成这个槽位。
/// </para>
/// <para>
/// <b>后到的标签插回它的顺序</b>:落点按「同组里已经到了的、模板顺序在它后面的第一个标签」算;
/// 「连接中」占位被原位换成真文档时(<see cref="DockWorkspace.DocumentReplaced" />)记账跟着转过去。
/// </para>
/// <para>
/// 收尾时把一个标签都没等到的组收掉(用户在凭据框上点了取消、连接流程在建标签之前就失败了)。
/// </para>
/// </remarks>
public sealed class LayoutTemplateLauncher
{
    /// <summary>同时在握手的最多几条,与 <c>MainWindowViewModel.OpenManyConcurrency</c> 一致。</summary>
    public const int Concurrency = 6;

    private readonly DockWorkspace _workspace;
    private readonly ILayoutDocumentOpener _opener;
    private readonly AsyncLocal<SlotTicket?> _ticket = new();
    private readonly List<RestoreSession> _sessions = [];

    /// <summary>为一个工作区建立还原器,并接管它的新文档落点裁决。</summary>
    /// <param name="workspace">工作区。</param>
    /// <param name="opener">查配置、开标签的那一面。</param>
    public LayoutTemplateLauncher(DockWorkspace workspace, ILayoutDocumentOpener opener)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _workspace.PlacementResolver = ResolvePlacement;
        _workspace.DocumentReplaced += OnDocumentReplaced;
    }

    /// <summary>
    /// 打开一棵布局树:配置已删除、shell 不在了的标签跳过并在结果里报出来,其余照开。
    /// </summary>
    /// <param name="root">模板树。</param>
    /// <returns>全部标签的打开流程都结束之后的结果。</returns>
    public async Task<LayoutOpenResult> OpenAsync(LayoutNode? root)
    {
        var missing = new List<LayoutMissingDocument>();
        var requests = new Dictionary<LayoutDocument, LayoutOpenRequest>(ReferenceEqualityComparer.Instance);
        var profiles = new Dictionary<Guid, SessionProfile?>();
        foreach (LayoutDocument document in LayoutTree.Documents(root).ToList())
        {
            (LayoutOpenRequest? request, LayoutMissingReason reason) = await ResolveAsync(document, profiles).ConfigureAwait(true);
            if (request is null)
            {
                missing.Add(new(document.Title, reason));
            }
            else
            {
                requests[document] = request;
            }
        }
        if (LayoutTree.Prune(root, requests.ContainsKey) is not { } pruned)
        {
            return new(0, missing);
        }

        var session = new RestoreSession();
        int ordinal = 0;
        DockNode skeleton = DockLayoutSnapshot.BuildSkeleton(pruned, (node, group) =>
        {
            session.Groups.Add(group);
            for (int i = 0; i < node.Documents.Count; i++)
            {
                LayoutDocument document = node.Documents[i];
                session.Slots.Add(new(
                    group,
                    ordinal++,
                    requests[document],
                    document.IsPinned,
                    i == node.ActiveIndex,
                    document.IsActive));
            }
        });
        _workspace.InsertLayout(skeleton);
        _sessions.Add(session);
        try
        {
            using var gate = new SemaphoreSlim(Concurrency);
            await Task.WhenAll(session.Slots.Select(slot => OpenSlotAsync(session, slot, gate)).ToList())
                      .ConfigureAwait(true);
        }
        finally
        {
            _sessions.Remove(session);
            foreach (DockGroup group in session.Groups.Where(group => group.Documents.Count == 0))
            {
                // 一个标签都没等来的组:模板里这一格已经没有意义,留着只是一块「拖放标签到这里」的空白。
                _workspace.ClosePane(group);
            }
        }
        return new(session.Slots.Count, missing);
    }

    private async Task<(LayoutOpenRequest? Request, LayoutMissingReason Reason)> ResolveAsync(
        LayoutDocument document,
        Dictionary<Guid, SessionProfile?> profiles)
    {
        switch (document.Kind)
        {
            case LayoutDocumentKind.LocalShell:
                return document.ShellId is { Length: > 0 } shellId && _opener.FindLocalShell(shellId) is { } shell
                           ? (new(LayoutDocumentKind.LocalShell, Shell: shell), default)
                           : (null, LayoutMissingReason.ShellUnavailable);
            case LayoutDocumentKind.Terminal or LayoutDocumentKind.Files or LayoutDocumentKind.Workspace:
                if (document.ProfileId is not { } profileId)
                {
                    return (null, LayoutMissingReason.Unsupported);
                }
                return await FindAsync(profileId, profiles).ConfigureAwait(true) is { } profile
                           ? (new(document.Kind, profile), default)
                           : (null, LayoutMissingReason.ProfileDeleted);
            case LayoutDocumentKind.DualFiles:
                if (document is not { ProfileId: { } leftId, SecondProfileId: { } rightId } || leftId == rightId)
                {
                    return (null, LayoutMissingReason.Unsupported);
                }
                SessionProfile? left = await FindAsync(leftId, profiles).ConfigureAwait(true);
                SessionProfile? right = await FindAsync(rightId, profiles).ConfigureAwait(true);
                return left is not null && right is not null
                           ? (new(LayoutDocumentKind.DualFiles, left, right), default)
                           : (null, LayoutMissingReason.ProfileDeleted);
            default:
                return (null, LayoutMissingReason.Unsupported);
        }
    }

    /// <summary>同一条配置在模板里出现几次只查一次库;查库出错按「不在了」算。</summary>
    private async Task<SessionProfile?> FindAsync(Guid profileId, Dictionary<Guid, SessionProfile?> profiles)
    {
        if (profiles.TryGetValue(profileId, out SessionProfile? cached))
        {
            return cached;
        }
        SessionProfile? profile;
        try
        {
            profile = await _opener.FindProfileAsync(profileId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.WriteLine($"[LayoutTemplates] 读取配置 {profileId} 失败:{ex.Message}");
            profile = null;
        }
        profiles[profileId] = profile;
        return profile;
    }

    private async Task OpenSlotAsync(RestoreSession session, Slot slot, SemaphoreSlim gate)
    {
        await gate.WaitAsync().ConfigureAwait(true);
        // 票据只在这一条异步流程(及其调用的连接流程)里可见:async 方法返回时上下文还原,
        // 发起下一个槽位的循环看不到它。
        var ticket = new SlotTicket(session, slot);
        _ticket.Value = ticket;
        try
        {
            await _opener.OpenAsync(slot.Request).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 连接失败已在各自的标签页里提示;这里只保证一个标签的失败不掀掉其余。
            Trace.WriteLine($"[LayoutTemplates] 打开 {slot.Request.Kind} 失败:{ex.Message}");
        }
        finally
        {
            ticket.Expire();
            _ticket.Value = null;
            gate.Release();
        }
    }

    private DockPlacement? ResolvePlacement(DockDocument document)
    {
        if (_ticket.Value is not { Used: false, Expired: false, Session: { } session, Slot: { } slot } ticket)
        {
            return null;
        }
        ticket.Used = true;
        session.Placed[document] = slot;
        DockGroup group = slot.Group;
        int index = group.Documents.Count;
        for (int i = 0; i < group.Documents.Count; i++)
        {
            if (session.Placed.TryGetValue(group.Documents[i], out Slot? other)
                && ReferenceEquals(other.Group, group)
                && other.Ordinal > slot.Ordinal)
            {
                index = i;
                break;
            }
        }
        return new DockPlacement(
            group,
            index,
            slot.Pinned,
            SelectInGroup: slot.IsGroupActive || group.ActiveDocument is null,
            Activate: slot.IsWorkspaceActive);
    }

    private void OnDocumentReplaced(DockDocument oldDocument, DockDocument newDocument)
    {
        foreach (RestoreSession session in _sessions)
        {
            if (session.Placed.Remove(oldDocument, out Slot? slot))
            {
                session.Placed[newDocument] = slot;
            }
        }
    }

    /// <summary>一次打开模板的记账:骨架里的组、每个槽位、已经到达的标签各属于哪个槽位。</summary>
    private sealed class RestoreSession
    {
        public List<DockGroup> Groups { get; } = [];

        public List<Slot> Slots { get; } = [];

        public Dictionary<DockDocument, Slot> Placed { get; } = [with(ReferenceEqualityComparer.Instance)];
    }

    /// <summary>模板里的一个标签位。</summary>
    /// <param name="Group">它落进的组。</param>
    /// <param name="Ordinal">它在整份模板里的顺序号(组内顺序靠它排)。</param>
    /// <param name="Request">怎么打开。</param>
    /// <param name="Pinned">是否固定。</param>
    /// <param name="IsGroupActive">是不是组里选中的那个。</param>
    /// <param name="IsWorkspaceActive">是不是整个工作区的活动标签。</param>
    private sealed record Slot(
        DockGroup Group,
        int Ordinal,
        LayoutOpenRequest Request,
        bool Pinned,
        bool IsGroupActive,
        bool IsWorkspaceActive);

    /// <summary>一条槽位打开流程的票据:只认第一个建出来的标签,流程结束即作废。</summary>
    private sealed class SlotTicket(RestoreSession session, Slot slot)
    {
        /// <summary>这一次还原的记账;作废之后为 null。</summary>
        public RestoreSession? Session { get; private set; } = session;

        /// <summary>这条流程的槽位;作废之后为 null。</summary>
        public Slot? Slot { get; private set; } = slot;

        public bool Used { get; set; }

        public bool Expired { get; private set; }

        /// <summary>作废,并放掉对这一次还原的引用。</summary>
        /// <remarks>
        /// 流程里另起的后台任务(SSH 连接的收包与保活循环、SFTP 的接收循环、通道泵……)捕获了执行上下文,
        /// 也就一直攥着这张票,长的一攥就是几天。票若还连着记账 —— <see cref="RestoreSession.Placed" /> 里是这次还原放下的
        /// 每一个标签,标签又连着终端视图模型与回滚缓冲,<see cref="RestoreSession.Slots" /> 里还有连接配置 ——
        /// 用户关掉的那些标签就一个也回收不了,每打开一次模板还多攒一份。光把标记设上不够,引用得一并放掉。
        /// </remarks>
        public void Expire()
        {
            Expired = true;
            Session = null;
            Slot = null;
        }
    }
}
