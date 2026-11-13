using System.ComponentModel;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Interop;

/// <summary>
/// 一个外部 agent 连进来之后的会话状态:它选中了哪台服务器、能调哪些工具。
/// </summary>
/// <remarks>
/// <b>工具直接复用 <see cref="AgentToolbox" />。</b>VelaShell 自己的 agent 用的就是那一套,
/// 对外再抄一份只会让两边慢慢长歪。代价是工具签名里没有"服务器"这个参数
/// (工具箱靠 <see cref="AgentToolbox.SessionIdProvider" /> 拿会话),所以这里补一个
/// <c>use_session</c>:外部 agent 先 <c>list_sessions</c> 看有哪些,再 <c>use_session</c> 选一台。
///
/// <para><b>审批在这条路上没有界面。</b>外部 agent 跑在别的进程里,弹不出 VelaShell 的审批卡。
/// 所以 <see cref="ApprovalMode.Ask" /> 在这里等于"一律拒绝"(工具箱在
/// <c>ApprovalHandler</c> 为 null 时就是这个行为),用户要放开就得显式选
/// 只读放行或绕过审批 —— 这是一个明摆着的选择,而不是一个悄悄的默认。</para>
///
/// <para><b>提权命令是例外。</b><c>run_command</c> 带 <c>elevated=true</c> 时宿主自己会在桌面上弹 root 确认框
/// 与 sudo 密码框,那是这条路上现成的、插件伪造不了的人工确认,所以工具箱这一层不再替它拒绝
/// (<see cref="AgentToolbox.ElevationConfirmedByHost" />)。以前它在 Ask 与只读放行下一律被拒,
/// 外部 agent 遇到要 sudo 的命令只能失败,用户连密码框都见不到。</para>
///
/// <para><b><see cref="McpServerSettings.Scope" /> 挡在每一次工具调用上。</b>
/// 从前那份 <c>user@host:port</c> 清单只挡 <c>use_session</c>,而工具箱里九个工具都收可选的
/// <c>session_id</c> —— 外部 agent <c>list_sessions</c> 拿到 id 直接传就绕过去了。现在它是一个
/// <see cref="ISessionScope" />,挂在 <see cref="AgentToolbox.Scope" /> 上,每一次调用都要过。
/// <b>默认值仍是不限范围</b> —— 这条路的边界是回环地址、令牌与只读挡位,把用户自己机器上的
/// agent 一起收紧挡不住任何攻击者,只挡得住用户自己。</para>
/// </remarks>
internal sealed class McpToolHost : IAsyncDisposable
{
    private readonly CommandJobRunScope _runScope;
    private readonly SemaphoreSlim _calls = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IPluginContext _context;
    private readonly McpServerSettings _settings;
    private readonly AgentToolbox _toolbox;
    private readonly ISessionScope? _scope;
    private readonly SubagentOrchestrator? _subagents;
    private readonly AgentToolbox? _subagentTemplate;
    private string? _sessionId;
    private string _target = "";

    /// <summary>暴露给外部 agent 的工具(建好就不变)。</summary>
    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>最后一次活动时刻(空闲会话回收看它)。</summary>
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 仍在启动或运行的传输不因外部模型暂时没有调用而被闲置回收。已终结、停止未确认的
    /// Unknown/TimedOut 与撤权扣下的不再钉住会话 —— 否则攒满会话上限后 initialize 一律 503;回收照样 CancelAll。
    /// </summary>
    public bool HasActiveTransfers => _runScope.Transfers?.HasUnfinishedTransfers == true;

    /// <summary>还有子任务在跑:外部 agent 多半正等着取结果,这条连接不按闲置回收。</summary>
    public bool HasActiveSubagents => _subagents?.HasActive == true;

    /// <summary>一条连接最多委派多少个子任务。连接比桌面的一轮长得多,上限放宽;并发仍是 4。</summary>
    private const int MaxSubagentsPerConnection = 32;

    /// <summary>
    /// 建一个会话。给了 <paramref name="aiStore" /> 且是 Agent 模式时,外部 agent 也能用
    /// <c>delegate_subagent</c> 把任务交给 VelaShell 里配置的模型 —— 那会消耗用户的模型额度,
    /// 并把子任务读到的内容发给该供应商;不想给就在「配置工具」里停用 <c>delegate_subagent</c>。
    /// </summary>
    public static async Task<McpToolHost> CreateAsync(IPluginContext context, McpServerSettings settings,
        AiSettingsStore? aiStore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AiSettings? ai = aiStore is not null && settings.Mode == ChatMode.Agent
            ? await aiStore.LoadAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return new McpToolHost(context, settings, aiStore, ai);
    }

    public McpToolHost(IPluginContext context, McpServerSettings settings)
        : this(context, settings, null, null)
    {
    }

    private McpToolHost(IPluginContext context, McpServerSettings settings, AiSettingsStore? aiStore, AiSettings? ai)
    {
        _context = context;
        _settings = settings;
        _scope = settings.ResolveScope(context);
        _toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => _sessionId,
            Scope = _scope,
            Approval = settings.Approval,
            TransferApprovalContext = RemoteTransferApprovalContext.ExternalMcp,
            AutoApproveUploads = settings.AutoApproveUploads,
            AutoApproveDownloads = settings.AutoApproveDownloads,
            // 提权的人工确认交给宿主的 root 确认框与密码框(见类型注释)
            ElevationConfirmedByHost = true,
            DisabledTools = new HashSet<string>(
                settings.DisabledTools.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase)
            // ApprovalHandler 刻意不设:见类型注释
        };
        // 一条连接一个轮次,后台任务才查得到;但拒绝不能封住它 —— 只读放行下一条写命令就会被拒,
        // 封住之后 list_sessions 也调不动,外部 agent 无从恢复。拒绝只作为那一次调用的结果返回,
        // 停不停由 MCP 客户端决定(桌面与 IM 的轮次才在拒绝后强制暂停)。
        _runScope = _toolbox.BeginRun(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            denialEndsRun: false, cancellationToken: _lifetime.Token);
        // 一个聊天模型都没配时不摆委派工具:摆了也只会每次失败
        ResolvedModel? anchor = ai?.FindModel(ai.ActiveModelId)
            ?? ai?.ResolveModels().FirstOrDefault(model => !AiSettingsStore.IsImageModel(model));
        if (aiStore is not null && ai is not null && anchor is not null
            && Chat.SubagentRunner.ModelChoices(ai, anchor.Provider.Id) is { Count: > 0 } models)
        {
            // 外部 agent 自己就是模型:这里没有"父模型",子任务用 VelaShell 当前选中的那个,每次按当下设置现取凭据。
            // 子任务的工具与这条连接同一套范围与挡位;没有审批界面,所以 allowWrites 只在绕过审批时写得进去。
            _subagentTemplate = _toolbox.CreateSubagentToolbox();
            string defaultModelId = models.ContainsKey(anchor.Id) ? anchor.Id : models.Keys.First();
            string providerId = anchor.Provider.Id;
            var runner = new Chat.SubagentRunner(
                (request, token) => Chat.SubagentRunner.ResolveFromSettingsAsync(aiStore, request.ModelId, defaultModelId, token, providerId),
                (model, credential) => aiStore.CreateClient(model, credential, callerCountsRetries: true),
                () => _subagentTemplate, _runScope, context.Host.Locale);
            SubagentOrchestrator created = null!;
            created = new SubagentOrchestrator((request, token) => runner.RunAsync(created, request, token), _lifetime.Token)
            {
                ModelChoices = models,
                // 没指定模型时用 VelaShell 当前选中的;选中的是图片模型就退到第一个聊天模型。
                // 在这里定下来,结果里才说得清实际用的是哪个模型。
                DefaultModelId = defaultModelId,
                MaxTotal = MaxSubagentsPerConnection,
                // 这条路唤不醒外部模型:结果得它自己用 subagent_wait 来取
                DeliversResults = false
            };
            _subagents = created;
            _toolbox.Subagents = created;
        }
        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(UseSessionAsync, "use_session",
                "Select which of the user's VelaShell SSH sessions the other tools act on. "
                + "Call list_sessions first to see what is connected. "
                + "Accepts user@host:port, host:port or host.")
        };
        tools.AddRange(_toolbox.CreateTools(settings.Mode, runScope: _runScope).OfType<AIFunction>());
        // 「对话」档一个工具都不给,与界面上的说法(纯对话:完全不给工具)一致(维护者 2026-10-25 决定)。
        // 以前工具箱对 Chat 与 Agent 一视同仁,外部 agent 在这一档拿到的是全部工具、连 run_command 都在
        Tools = settings.Mode == ChatMode.Chat ? [] : tools;
    }

    /// <summary>initialize 回给外部 agent 的说明。</summary>
    public string Instructions => Tools.Count == 0
        ? $"VelaShell's external MCP server is in Chat mode, which exposes no tools. Mode: {_settings.Mode}. "
          + "Ask the user to switch it to Plan (read-only) or Agent in VelaShell's AI collaboration settings if tools are needed."
        : "Tools act on the SSH sessions the user has open in VelaShell. "
          + "Call list_sessions first, then use_session to pick one. "
          + Describe();

    /// <summary>按名字调一个工具,返回给模型看的文本。</summary>
    public async Task<string> CallAsync(string name, IDictionary<string, object?> arguments,
        CancellationToken cancellationToken, string? invocationId = null)
    {
        LastActivity = DateTimeOffset.UtcNow;
        if (Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal)) is not { } tool)
        {
            throw new KeyNotFoundException($"Unknown tool: {name}");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        // 查询与等待有上限且绑定所有者；不能占写操作锁挡住显式取消。
        bool serialize = name is not ("transfer_wait" or "transfer_status" or "task_wait" or "task_status"
            or "subagent_wait" or "subagent_status" or "subagent_cancel");
        if (serialize) await _calls.WaitAsync(linked.Token).ConfigureAwait(false);
        string? previous = AgentToolbox.InvocationIdentity.Value;
        AgentToolbox.InvocationIdentity.Value = invocationId ?? Guid.NewGuid().ToString("N");
        try
        {
            object? result = await tool.InvokeAsync([with(arguments)], linked.Token).ConfigureAwait(false);
            return result?.ToString() ?? "";
        }
        finally
        {
            AgentToolbox.InvocationIdentity.Value = previous;
            if (serialize) _calls.Release();
        }
    }

    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _calls.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_subagents is not null) await _subagents.DisposeAsync().ConfigureAwait(false);
            if (_subagentTemplate is not null) await _subagentTemplate.DisposeAsync().ConfigureAwait(false);
            if (_runScope.Transfers is { } transfers) await transfers.CancelAllAsync().ConfigureAwait(false);
            await _runScope.DisposeAsync().ConfigureAwait(false);
            await _toolbox.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }
        finally { _calls.Release(); }
    }

    /// <summary>
    /// 选一台服务器。绑定的是<b>当下那条连接</b> —— 与桥接不同,MCP 会话本来就是短命的,
    /// 没必要为它做"过夜后重新解析"。
    /// </summary>
    private async Task<string> UseSessionAsync(
        [Description("Which session to use: user@host:port, host:port or just host.")] string target,
        CancellationToken cancellationToken = default)
    {
        // 范围外的会话在 ResolveAsync 里就当作不存在,这里不再单独比一遍字符串:
        // 一句"它不在名单上,名单是 A、B、C"会把拒绝消息本身变成一个探测接口。
        if (await SessionTargets.ResolveAsync(_context, target, cancellationToken, _scope).ConfigureAwait(false) is not { } session)
        {
            string list = await SessionTargets.DescribeAsync(_context, cancellationToken, _scope).ConfigureAwait(false);
            return list.Length == 0
                ? "No sessions are connected in VelaShell right now."
                : $"No connected session matches '{target}'. Connected sessions:\n{list}";
        }
        _sessionId = session.SessionId;
        _target = SessionTargets.Format(session);
        return $"Now acting on {_target}.";
    }

    /// <summary>
    /// 只有一台连着时,免掉 <c>use_session</c> 这一步。
    /// </summary>
    /// <remarks>
    /// 纯粹是省事:外部 agent 一上来就 <c>run_command</c> 是很自然的写法,
    /// 而"只有一台"时并不存在选错的风险。多于一台就老老实实让它自己选。
    /// </remarks>
    public async Task AutoSelectAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is not null)
        {
            return;
        }
        IReadOnlyList<SessionInfo> sessions = await _context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        var connected = new List<SessionInfo>();
        foreach (SessionInfo session in sessions.Where(s => s.State == SessionState.Connected))
        {
            // 数的是"允许操作的里面有几台",不是"一共连着几台" —— 否则用户连着两台、
            // 清单里只写了一台时,本该没有歧义的那一台反而选不上。
            if (_scope is not null && !await _scope.AllowsLiveAsync(session, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            connected.Add(session);
        }
        if (connected.Count != 1)
        {
            return;
        }
        _sessionId = connected[0].SessionId;
        _target = SessionTargets.Format(connected[0]);
    }

    /// <summary>给 <c>initialize</c> 的服务端说明里带一句"现在对着哪台机器"。</summary>
    public string Describe()
        => DescribeTarget() + Elevation + Delegation + " Background tasks can be queried, waited on, or cancelled with task tools. "
            + "This server does not wake external models; continuation is the MCP client's responsibility.";

    /// <summary>
    /// 要 root 的命令怎么跑。外部 agent 的习惯是把 <c>sudo …</c> 直接写进命令,
    /// 而独立 exec 通道没有终端,sudo 一要密码就报错退出 —— 得一上来就告诉它走 <c>elevated</c>。
    /// </summary>
    private string Elevation
        => Tools.Any(t => string.Equals(t.Name, "run_command", StringComparison.Ordinal))
            ? " Commands that need root: call run_command with elevated=true and the command WITHOUT sudo"
              + " (a plain sudo has no terminal here and fails as soon as it wants a password)."
              + " VelaShell then shows the user its own root confirmation and sudo password dialog on their desktop;"
              + " until they answer the task reports AwaitingUser, so tell the user to switch to VelaShell and use task_wait."
            : "";

    /// <summary>委派工具在这条路上的用法:结果不会推过来,得自己取;用的是用户在 VelaShell 里配置的模型。</summary>
    private string Delegation
        => Tools.Any(t => string.Equals(t.Name, "delegate_subagent", StringComparison.Ordinal))
            ? " delegate_subagent hands a self-contained, read-focused task to a model configured in VelaShell (it uses the user's model quota);"
              + " collect each result with subagent_wait."
            : "";

    private string DescribeTarget()
        => _target.Length > 0
            ? $"VelaShell is acting on {_target}. Mode: {_settings.Mode}, approval: {_settings.Approval}.{CanOpen}"
            : $"No session selected yet — call list_sessions, then use_session. Mode: {_settings.Mode}, approval: {_settings.Approval}.{CanOpen}";

    /// <summary>
    /// 一台都没连着时还有没有别的路走。
    /// </summary>
    /// <remarks>
    /// 只在 <c>open_session</c> 真的走得通时才说,而这条路上"走得通"只有<b>绕过审批</b>一种:
    /// 没有审批界面,<see cref="ApprovalMode.Ask" /> 等于一律拒绝(见类型注释);
    /// <see cref="ApprovalMode.ReadOnlyAuto" /> 只放行确定无副作用的命令,开连接不在其列。
    /// 把"你可以自己连一台"写进说明却让它每次都撞墙,比不写更糟。
    /// </remarks>
    private string CanOpen
        => _settings.Approval == ApprovalMode.Bypass
           && Tools.Any(t => string.Equals(t.Name, "open_session", StringComparison.Ordinal))
            ? " A machine that is saved in VelaShell but not connected can be connected with"
              + " list_saved_sessions + open_session; the user is asked to confirm on their desktop."
            : "";
}
