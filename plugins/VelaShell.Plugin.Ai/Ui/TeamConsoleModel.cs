using Avalonia;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>成员此刻的状态(团队控制台上的状态点 / 状态药丸)。</summary>
internal enum TeamMemberState
{
    /// <summary>正在调工具。</summary>
    Working,

    /// <summary>在跑,但此刻没有工具在调 —— 模型在想。</summary>
    Thinking,

    /// <summary>等审批、等文件锁,或者手上的任务还在等依赖。</summary>
    Waiting,

    /// <summary>没有在跑也没有在等的任务。</summary>
    Idle,

    /// <summary>手上有失败 / 受阻的任务,而且没有别的在跑或在等。</summary>
    Blocked,

    /// <summary>主控:团队在跑期间一直在线。</summary>
    Online
}

/// <summary>拓扑图上的一条关系。<c>Kind</c> 取 assign / handoff / collab / review。</summary>
/// <param name="From">起点(成员名或 <see cref="TeamConsoleModel.Lead" />)。</param>
/// <param name="To">终点。</param>
/// <param name="Kind">关系种类。</param>
/// <param name="Subject">交付关系上写的是哪个任务的标题;其它关系为 null(标签用种类名)。</param>
internal sealed record TeamEdge(string From, string To, string Kind, string? Subject = null)
{
    /// <summary>稳定的键(与方向有关)。</summary>
    public string Key => From + "→" + To;
}

/// <summary>一条边在画布上的几何:二次贝塞尔的三个点,以及标签放哪儿。</summary>
internal readonly record struct TeamEdgeGeometry(Point Start, Point Control, Point End, Point Label)
{
    /// <summary>曲线上参数 <paramref name="t" />(0–1)处的点。</summary>
    public Point At(double t)
    {
        double u = 1 - t;
        return new Point(
            (u * u * Start.X) + (2 * u * t * Control.X) + (t * t * End.X),
            (u * u * Start.Y) + (2 * u * t * Control.Y) + (t * t * End.Y));
    }
}

/// <summary>一名成员的用量合计(它名下所有任务的每一次运行,返工的也算)。</summary>
/// <param name="Input">输入 token(含缓存命中)。</param>
/// <param name="Cached">其中命中缓存的部分。</param>
/// <param name="Output">输出 token。</param>
/// <param name="Context">最近一次运行此刻的上下文大小。</param>
/// <param name="ModelId">最近一次运行实际用的模型;没跑过就是计划里的那个。</param>
/// <param name="Tools">用过的工具与次数(各次运行合并,按次数从多到少)。</param>
internal sealed record TeamMemberUsage(long Input, long Cached, long Output, long Context, string? ModelId,
    IReadOnlyList<SubagentToolUsage> Tools)
{
    /// <summary>输入 + 输出。</summary>
    public long Total => Input + Output;
}

/// <summary>
/// 团队控制台的数据推导:从 <see cref="TeamBoardSnapshot" /> 与各次运行的 <see cref="SubagentSnapshot" /> 算出
/// 关系、布局、成员状态与用量。全是纯函数,不碰界面。
/// </summary>
internal static class TeamConsoleModel
{
    /// <summary>主控在消息与拓扑里的名字(成员不能叫这个,见 TeamEngine.Validate)。</summary>
    public const string Lead = "lead";

    /// <summary>拓扑画布的宽度(虚拟坐标,整张图等比缩放到面板宽度)。</summary>
    public const double GraphWidth = 820;

    /// <summary>节点半径(外环)。连线从它外面一点起笔。</summary>
    public const double NodeRadius = 42;

    private const double LeadY = 68, FirstRowY = 268, RowGap = 190, BottomPad = 92;

    /// <summary>任务在依赖图里的层数:没有依赖是 0,否则是最深的那个依赖 + 1。</summary>
    public static Dictionary<string, int> TaskDepths(TeamBoardSnapshot team)
    {
        var byId = team.Tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> depth = [with(StringComparer.OrdinalIgnoreCase)];
        int Visit(TeamTaskInfo task, int guard)
        {
            if (depth.TryGetValue(task.Id, out int known)) return known;
            // 计划是引擎校验过无环的;历史数据万一坏了也别在这里转圈
            if (guard > team.Tasks.Count) return 0;
            int deepest = -1;
            foreach (string id in task.DependsOn)
            {
                if (byId.TryGetValue(id, out TeamTaskInfo? dependency)) deepest = Math.Max(deepest, Visit(dependency, guard + 1));
            }
            return depth[task.Id] = deepest + 1;
        }
        foreach (TeamTaskInfo task in team.Tasks) Visit(task, 0);
        return depth;
    }

    /// <summary>
    /// 成员之间、主控与成员之间的关系。每一对只留一条:审查 &gt; 交付 &gt; 协作;两边互相交付算双向协作。
    /// </summary>
    public static IReadOnlyList<TeamEdge> Edges(TeamBoardSnapshot team)
    {
        var edges = new List<TeamEdge>();
        var byId = team.Tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> members = [.. team.Members.Select(m => m.Name)];

        int Find(string a, string b) => edges.FindIndex(e => (e.From == a && e.To == b) || (e.From == b && e.To == a));

        foreach (TeamMemberInfo member in team.Members)
        {
            edges.Add(new TeamEdge(Lead, member.Name, "assign"));
        }
        foreach (TeamTaskInfo task in team.Tasks)
        {
            if (task.Verifies is { } verifiedId && byId.TryGetValue(verifiedId, out TeamTaskInfo? verified)
                && verified.Assignee != task.Assignee && members.Contains(verified.Assignee) && members.Contains(task.Assignee))
            {
                int existing = Find(task.Assignee, verified.Assignee);
                var review = new TeamEdge(task.Assignee, verified.Assignee, "review");
                if (existing < 0) edges.Add(review);
                else edges[existing] = review;
            }
        }
        foreach (TeamTaskInfo task in team.Tasks)
        {
            foreach (string id in task.DependsOn)
            {
                if (string.Equals(id, task.Verifies, StringComparison.OrdinalIgnoreCase)
                    || !byId.TryGetValue(id, out TeamTaskInfo? upstream) || upstream.Assignee == task.Assignee
                    || !members.Contains(upstream.Assignee) || !members.Contains(task.Assignee))
                {
                    continue;
                }
                int existing = Find(upstream.Assignee, task.Assignee);
                if (existing < 0)
                {
                    edges.Add(new TeamEdge(upstream.Assignee, task.Assignee, "handoff", upstream.Title));
                }
                else if (edges[existing] is { Kind: "handoff" } other && other.From == task.Assignee)
                {
                    edges[existing] = new TeamEdge(other.From, other.To, "collab");
                }
            }
        }
        foreach (TeamMessageInfo message in team.Messages)
        {
            if (message.Kind == "peer" && members.Contains(message.From) && members.Contains(message.To)
                && message.From != message.To && Find(message.From, message.To) < 0)
            {
                edges.Add(new TeamEdge(message.From, message.To, "collab"));
            }
        }
        return edges;
    }

    /// <summary>一行最多摆几名成员。</summary>
    public const int MembersPerRow = 3;

    /// <summary>
    /// 节点位置:主控在顶上,成员按「它最早那个任务在依赖图里的层数」排好先后,从左到右、一行最多
    /// <see cref="MembersPerRow" /> 名地摆开 —— 上游在左、下游在右,摆不下再换行。
    /// </summary>
    /// <remarks>
    /// 主控连着每一名成员,它放哪儿决定了多少条指派线要绕路:在「正中」与「相邻两列之间」里挑直线被挡得最少的那个位置。
    /// </remarks>
    /// <param name="team">团队快照。</param>
    /// <param name="height">画布高度(随行数变)。</param>
    public static Dictionary<string, Point> Layout(TeamBoardSnapshot team, out double height)
    {
        Dictionary<string, int> taskDepth = TaskDepths(team);
        string[] ordered =
        [
            .. team.Members.Select((member, index) => (member.Name, index, Depth: team.Tasks.Where(t => t.Assignee == member.Name)
                    .Select(t => taskDepth.GetValueOrDefault(t.Id)).DefaultIfEmpty(0).Min()))
                .OrderBy(m => m.Depth).ThenBy(m => m.index).Select(m => m.Name)
        ];
        int rowCount = Math.Max(1, (ordered.Length + MembersPerRow - 1) / MembersPerRow);
        // 各行人数尽量平均(4 人 = 2 + 2,而不是 3 + 1)
        int perRow = (ordered.Length + rowCount - 1) / rowCount;
        Dictionary<string, Point> positions = [with(StringComparer.Ordinal)];
        List<double> firstRow = [];
        for (int row = 0, next = 0; row < rowCount; row++)
        {
            int count = Math.Min(perRow, ordered.Length - next);
            for (int i = 0; i < count; i++)
            {
                double x = GraphWidth * (i + 0.5) / count;
                positions[ordered[next++]] = new Point(x, FirstRowY + (row * RowGap));
                if (row == 0) firstRow.Add(x);
            }
        }

        List<double> candidates = [GraphWidth / 2];
        for (int i = 1; i < firstRow.Count; i++) candidates.Add((firstRow[i - 1] + firstRow[i]) / 2);
        double leadX = GraphWidth / 2;
        int fewest = int.MaxValue;
        foreach (double x in candidates)
        {
            var lead = new Point(x, LeadY);
            int blocked = positions.Count(target => positions.Any(other => other.Key != target.Key
                && DistanceToSegment(other.Value, lead, target.Value) < NodeRadius + 20));
            if (blocked < fewest)
            {
                fewest = blocked;
                leadX = x;
            }
        }
        positions[Lead] = new Point(leadX, LeadY);
        height = FirstRowY + ((rowCount - 1) * RowGap) + BottomPad;
        return positions;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared <= 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        double x = a.X + (t * dx) - p.X, y = a.Y + (t * dy) - p.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    /// <summary>
    /// 一条边的几何。直线会穿过别的节点时往一侧弯(挑第一个让得开的弯度),都让不开就取离得最远的那个。
    /// </summary>
    public static TeamEdgeGeometry Geometry(TeamEdge edge, IReadOnlyDictionary<string, Point> positions)
    {
        Point p = positions[edge.From], q = positions[edge.To];
        Point[] others = [.. positions.Where(kv => kv.Key != edge.From && kv.Key != edge.To).Select(kv => kv.Value)];
        TeamEdgeGeometry best = default;
        double bestClearance = double.NegativeInfinity;
        foreach (double curvature in (double[])[0, 0.1, -0.1, 0.16, -0.16, 0.24, -0.24, 0.34, -0.34])
        {
            TeamEdgeGeometry candidate = Curve(p, q, curvature);
            double clearance = double.PositiveInfinity;
            foreach (Point other in others)
            {
                for (int i = 1; i < 10; i++)
                {
                    Point at = candidate.At(i / 10.0);
                    clearance = Math.Min(clearance, Math.Sqrt(((at.X - other.X) * (at.X - other.X)) + ((at.Y - other.Y) * (at.Y - other.Y))));
                }
            }
            if (clearance >= NodeRadius + 20) return candidate;
            if (clearance > bestClearance)
            {
                bestClearance = clearance;
                best = candidate;
            }
        }
        return best;
    }

    private static TeamEdgeGeometry Curve(Point p, Point q, double curvature)
    {
        double dx = q.X - p.X, dy = q.Y - p.Y;
        double length = Math.Max(1, Math.Sqrt((dx * dx) + (dy * dy)));
        double ux = dx / length, uy = dy / length;
        // 起点贴着外环、终点留出箭头
        var start = new Point(p.X + (ux * (NodeRadius + 8)), p.Y + (uy * (NodeRadius + 8)));
        var end = new Point(q.X - (ux * (NodeRadius + 12)), q.Y - (uy * (NodeRadius + 12)));
        var control = new Point(((start.X + end.X) / 2) - (uy * curvature * length), ((start.Y + end.Y) / 2) + (ux * curvature * length));
        var curve = new TeamEdgeGeometry(start, control, end, default);
        return curve with { Label = curve.At(0.5) };
    }

    /// <summary>标签沿连线可以落在哪几处(曲线参数),按先后试。</summary>
    private static readonly double[] LabelStops = [0.55, 0.42, 0.68, 0.3, 0.8];

    /// <summary>
    /// 给每条连线的标签找个不压东西的位置:不压节点、不压节点下面的名字、不压已经放好的标签。
    /// 先放成员之间的关系(交付 / 审查 / 协作,各不相同、信息量大),再放主控的指派线;哪儿都放不下的不放(返回 null)。
    /// </summary>
    /// <param name="edges">各连线的几何、标签宽度,以及是不是主控的指派线。</param>
    /// <param name="nodes">各节点的中心。</param>
    public static Point?[] PlaceLabels(IReadOnlyList<(TeamEdgeGeometry Geometry, double Width, bool Assign)> edges,
        IReadOnlyCollection<Point> nodes)
    {
        var taken = new List<Rect>();
        foreach (Point node in nodes)
        {
            taken.Add(new Rect(node.X - NodeRadius - 8, node.Y - NodeRadius - 8, (NodeRadius + 8) * 2, (NodeRadius + 8) * 2));
            // 节点下面的两行字:名字 · 角色、模型 · 用量
            taken.Add(new Rect(node.X - 95, node.Y + NodeRadius + 4, 190, 42));
        }
        var placed = new Point?[edges.Count];
        foreach (int index in Enumerable.Range(0, edges.Count).OrderBy(i => edges[i].Assign))
        {
            (TeamEdgeGeometry geometry, double width, _) = edges[index];
            foreach (double stop in LabelStops)
            {
                Point center = geometry.At(stop);
                var box = new Rect(center.X - (width / 2) - 3, center.Y - 13, width + 6, 26);
                if (taken.Any(other => other.Intersects(box))) continue;
                taken.Add(box);
                placed[index] = center;
                break;
            }
        }
        return placed;
    }

    /// <summary>两个人之间的那条边;<c>Reversed</c> 表示消息方向与边的方向相反。</summary>
    public static (TeamEdge Edge, bool Reversed)? FindEdge(IReadOnlyList<TeamEdge> edges, string from, string to)
    {
        foreach (TeamEdge edge in edges)
        {
            if (edge.From == from && edge.To == to) return (edge, false);
            if (edge.From == to && edge.To == from) return (edge, true);
        }
        return null;
    }

    /// <summary>某个任务的那些运行(返工时不止一次),按快照里的先后。</summary>
    public static IEnumerable<SubagentSnapshot> RunsOf(TeamTaskInfo task, IReadOnlyList<SubagentSnapshot> runs)
        => runs.Where(r => string.Equals(r.TeamTaskId, task.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>任务当前(最后一次)的那次运行;没开跑过为 null。</summary>
    public static SubagentSnapshot? RunOf(TeamTaskInfo task, IReadOnlyList<SubagentSnapshot> runs)
        => task.SubagentId is { } id ? runs.FirstOrDefault(r => r.SubagentId == id) : null;

    /// <summary>成员名下的用量合计。</summary>
    public static TeamMemberUsage UsageOf(TeamMemberInfo member, TeamBoardSnapshot team, IReadOnlyList<SubagentSnapshot> runs)
    {
        long input = 0, cached = 0, output = 0;
        Dictionary<string, int> tools = [with(StringComparer.Ordinal)];
        TeamTaskInfo[] own = [.. team.Tasks.Where(t => t.Assignee == member.Name)];
        foreach (TeamTaskInfo task in own)
        {
            foreach (SubagentSnapshot run in RunsOf(task, runs))
            {
                input += run.InputTokens;
                cached += Math.Clamp(run.CachedInputTokens, 0, run.InputTokens);
                output += run.OutputTokens;
                foreach (SubagentToolUsage tool in run.ToolsUsed)
                {
                    tools[tool.Name] = tools.GetValueOrDefault(tool.Name) + tool.Count;
                }
            }
        }
        // 「最近一次运行」:在跑的优先,否则取最后一个开跑过的任务
        TeamTaskInfo? latestTask = own.FirstOrDefault(t => t.Status == "running") ?? own.LastOrDefault(t => t.SubagentId is not null);
        SubagentSnapshot? latest = latestTask is null ? null : RunOf(latestTask, runs);
        return new TeamMemberUsage(input, cached, output, latest?.ContextTokens ?? 0, latest?.ModelId ?? member.ModelId,
            [.. tools.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new SubagentToolUsage(kv.Key, kv.Value))]);
    }

    /// <summary>成员此刻的状态。</summary>
    public static TeamMemberState StateOf(TeamMemberInfo member, TeamBoardSnapshot team, IReadOnlyList<SubagentSnapshot> runs)
    {
        TeamTaskInfo[] own = [.. team.Tasks.Where(t => t.Assignee == member.Name)];
        if (own.FirstOrDefault(t => t.Status == "running") is { } running)
        {
            return RunOf(running, runs)?.Activity switch
            {
                SubagentOrchestrator.WaitingForApproval or SubagentOrchestrator.WaitingForFileLock => TeamMemberState.Waiting,
                { Length: > 0 } => TeamMemberState.Working,
                _ => TeamMemberState.Thinking
            };
        }
        if (own.Any(t => t.Status == "pending")) return team.Phase == "running" ? TeamMemberState.Waiting : TeamMemberState.Idle;
        return own.Any(t => t.Status is "failed" or "blocked") ? TeamMemberState.Blocked : TeamMemberState.Idle;
    }

    /// <summary>主控的状态:团队在跑时在线,落定后空闲。</summary>
    public static TeamMemberState LeadState(TeamBoardSnapshot team)
        => team.Phase == "running" ? TeamMemberState.Online : TeamMemberState.Idle;

    /// <summary>消息的两端有没有 <paramref name="party" />(「全体」算每一名成员)。</summary>
    public static bool Involves(TeamMessageInfo message, string party)
        => message.From == party || message.To == party || (message.To == "all" && party != Lead && message.From != party);

    /// <summary>两个人之间的消息(发给「全体」的不算在某一对里)。</summary>
    public static IEnumerable<TeamMessageInfo> Between(TeamBoardSnapshot team, string a, string b)
        => team.Messages.Where(m => (m.From == a && m.To == b) || (m.From == b && m.To == a));
}

/// <summary>
/// 用量的时间序列:团队在跑期间按固定间隔记下各成员这一段新增了多少 token(控制台的迷你走势与速率图)。
/// </summary>
/// <remarks>
/// 供应商是在一次请求结束时才报用量的,所以序列天然是一跳一跳的;这里记的是「每个采样间隔里新增的量」,不做平滑。
/// 只在团队还在跑时采样;从历史里翻出来的团队没有序列(当时没记,也不编造)。
/// </remarks>
internal sealed class TeamTelemetry
{
    /// <summary>采样间隔。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2.5);

    /// <summary>每名成员留多少个采样点。</summary>
    public const int MemberPoints = 24;

    /// <summary>全队序列留多少个采样点。</summary>
    public const int TeamPoints = 40;

    private readonly Dictionary<string, long> _lastTotals = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, List<double>> _members = [with(StringComparer.Ordinal)];
    private readonly List<double> _team = [];
    private DateTimeOffset? _lastSample;

    /// <summary>全队每个间隔新增的 token。</summary>
    public IReadOnlyList<double> Team => _team;

    /// <summary>某名成员每个间隔新增的 token;没有采样过返回空。</summary>
    public IReadOnlyList<double> Member(string name) => _members.TryGetValue(name, out List<double>? series) ? series : [];

    /// <summary>
    /// 喂一次当前合计。距上次采样不足一个间隔就不记;第一次只定基线。
    /// </summary>
    /// <param name="now">此刻。</param>
    /// <param name="totals">各成员到此刻的 token 合计(输入 + 输出)。</param>
    /// <returns>这次有没有记下新的采样点。</returns>
    public bool Sample(DateTimeOffset now, IReadOnlyDictionary<string, long> totals)
    {
        if (_lastSample is null)
        {
            _lastSample = now;
            foreach ((string name, long total) in totals) _lastTotals[name] = total;
            return false;
        }
        if (now - _lastSample.Value < Interval) return false;
        _lastSample = now;
        double sum = 0;
        foreach ((string name, long total) in totals)
        {
            double delta = Math.Max(0, total - _lastTotals.GetValueOrDefault(name));
            _lastTotals[name] = total;
            if (!_members.TryGetValue(name, out List<double>? series)) _members[name] = series = [];
            series.Add(delta);
            if (series.Count > MemberPoints) series.RemoveAt(0);
            sum += delta;
        }
        _team.Add(sum);
        if (_team.Count > TeamPoints) _team.RemoveAt(0);
        return true;
    }
}

/// <summary>
/// 一个团队在界面上的那份状态:最新快照、各次运行、用量序列。会话里的入口卡与控制台标签页看的是同一份。
/// </summary>
internal sealed class TeamConsoleSession(TeamBoardSnapshot team, IReadOnlyList<SubagentSnapshot> runs)
{
    /// <summary>最新的看板。</summary>
    public TeamBoardSnapshot Team { get; private set; } = team;

    /// <summary>各次运行(只留团队任务的)。</summary>
    public IReadOnlyList<SubagentSnapshot> Runs { get; private set; } = Filter(runs);

    /// <summary>用量序列。</summary>
    public TeamTelemetry Telemetry { get; } = new();

    /// <summary>取消某个成员的这次运行;这一轮收尾之后、以及从历史里翻出来的团队为 null。</summary>
    public Action<string>? Cancel { get; set; }

    /// <summary>
    /// 重跑一个没做成的任务(任务 id、改派给谁 —— 为空仍由原来的成员做),返回给人看的一句结果;
    /// 这一轮收尾之后、以及从历史里翻出来的团队为 null(引擎已经不在了,没法再跑)。
    /// </summary>
    public Func<string, string?, string>? Retry { get; set; }

    /// <summary>快照换了(UI 线程上触发)。</summary>
    public event Action? Changed;

    /// <summary>换上新的快照并通知订阅者。</summary>
    public void Update(TeamBoardSnapshot team, IReadOnlyList<SubagentSnapshot> runs)
    {
        Team = team;
        Runs = Filter(runs);
        if (team.Phase == "running")
        {
            Telemetry.Sample(DateTimeOffset.UtcNow, team.Members.ToDictionary(m => m.Name,
                m => TeamConsoleModel.UsageOf(m, team, Runs).Total, StringComparer.Ordinal));
        }
        Changed?.Invoke();
    }

    private static IReadOnlyList<SubagentSnapshot> Filter(IReadOnlyList<SubagentSnapshot> runs)
        => [.. runs.Where(r => r.TeamTaskId is not null)];
}
