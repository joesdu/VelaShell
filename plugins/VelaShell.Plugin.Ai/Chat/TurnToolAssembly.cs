using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>一轮工具的一个来源:内置工具、作图、「你应该知道」、MCP,以后还有技能与扩展包。</summary>
internal interface ITurnToolSource
{
    /// <summary>这个模式下提供不提供。</summary>
    bool AppliesTo(ChatMode mode);

    /// <summary>把这个来源的工具加进本轮的清单。</summary>
    ValueTask ContributeAsync(IList<AITool> tools, CancellationToken cancellationToken);
}

/// <summary>
/// 一轮要交给模型的工具清单:按登记顺序向各来源收集,「停用的内置工具」这条规则只在这里执行一次
/// (ai-plan §12.2 的 1c)。桌面与 IM 共用;入口只登记自己有哪些来源。
/// </summary>
/// <remarks>
/// <para>
/// 以前每个入口自己拼:先 <c>CreateTools</c>,再各自补上作图、笔记、MCP,模式判断与停用检查散在各处。
/// 新增一类工具(技能、扩展包)就得在每个入口各补一遍。现在新增来源只需实现 <see cref="ITurnToolSource" />。
/// </para>
/// <para>
/// 顺序就是登记顺序(模型看到的工具顺序与以前一致:内置 → 入口专属 → MCP)。
/// 停用清单按工具名过滤全部来源;MCP 工具名带服务器前缀,不会与内置工具重名。
/// </para>
/// </remarks>
internal sealed class TurnToolAssembly(ChatMode mode, IReadOnlySet<string> disabledTools)
{
    private readonly List<ITurnToolSource> _sources = [];

    /// <summary>登记一个来源(按登记顺序收集)。</summary>
    public TurnToolAssembly Add(ITurnToolSource source)
    {
        _sources.Add(source);
        return this;
    }

    /// <summary>登记一个简单来源:哪些模式下提供、怎么造。</summary>
    public TurnToolAssembly Add(Func<ChatMode, bool> appliesTo, Func<IEnumerable<AITool>> create)
        => Add(new DelegateToolSource(appliesTo, create));

    /// <summary>收集本轮的工具。</summary>
    /// <remarks>
    /// <b>刻意不用 <c>ConfigureAwait(false)</c></b>:来源的回调(桌面的状态行)要回到调用方的上下文上跑;
    /// 桌面在 UI 线程上调,IM 在线程池上调(没有同步上下文,效果与 false 相同)。
    /// </remarks>
    public async Task<IList<AITool>> BuildAsync(CancellationToken cancellationToken)
    {
        var tools = new List<AITool>();
        foreach (ITurnToolSource source in _sources)
        {
            if (source.AppliesTo(mode)) await source.ContributeAsync(tools, cancellationToken);
        }
        if (disabledTools.Count > 0) tools.RemoveAll(tool => disabledTools.Contains(tool.Name));
        return tools;
    }

    private sealed class DelegateToolSource(Func<ChatMode, bool> appliesTo, Func<IEnumerable<AITool>> create) : ITurnToolSource
    {
        public bool AppliesTo(ChatMode mode) => appliesTo(mode);

        public ValueTask ContributeAsync(IList<AITool> tools, CancellationToken cancellationToken)
        {
            foreach (AITool tool in create()) tools.Add(tool);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// MCP 服务器带来的工具:只在 Agent 模式、至少启用了一台服务器时提供。连接按轮次租用,
/// 租约(<see cref="Lease" />)由入口在轮次结束时释放 —— 桌面换站会多次装配,各次的租约都要收。
/// </summary>
/// <remarks>
/// 计划模式下不接 MCP:那些工具的副作用由第三方服务器说了算,插件无从判断,而「计划」的承诺是这一步不动任何东西。
/// </remarks>
internal sealed class McpToolSource(
    McpManager mcp,
    IReadOnlyList<McpServerConfig> servers,
    McpApproval approval,
    IReadOnlyList<LocalWorkspace>? workspaces) : ITurnToolSource
{
    /// <summary>本次装配拿到的租约;没接 MCP 时为 null。</summary>
    public McpManager.ToolLease? Lease { get; private set; }

    /// <summary>开始连接前(桌面在状态行说一句「正在连接 MCP」)。</summary>
    public Action? Connecting { get; init; }

    /// <summary>连接结束:各服务器报来的错误(可能为空)。</summary>
    public Action<IReadOnlyList<string>>? Connected { get; init; }

    /// <summary>筛出的只读 MCP 工具,交给子 Agent 用。</summary>
    public Action<IReadOnlyList<AITool>>? ReadOnlyTools { get; init; }

    public bool AppliesTo(ChatMode mode) => mode == ChatMode.Agent && servers.Any(s => s.Enabled);

    public async ValueTask ContributeAsync(IList<AITool> tools, CancellationToken cancellationToken)
    {
        Connecting?.Invoke();
        // 不用 ConfigureAwait(false):下面的回调要回到调用方的上下文(见 TurnToolAssembly.BuildAsync)
        McpManager.ToolLease lease = await mcp.AcquireToolsAsync(servers, approval, cancellationToken, workspaces);
        Lease = lease;
        ReadOnlyTools?.Invoke(McpManager.FilterReadOnlyTools(lease.Tools));
        foreach (AITool tool in lease.Tools) tools.Add(tool);
        Connected?.Invoke(lease.Errors);
    }
}
