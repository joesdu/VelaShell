using Avalonia;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 团队控制台的数据推导(<see cref="TeamConsoleModel" />):关系、布局、成员状态、用量与采样,全是纯函数。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class TeamConsoleModelTests
{
    private static TeamMemberInfo Member(string name, int color) => new(name, "role", null, false, color);

    private static TeamTaskInfo Task(string id, string assignee, string status = "completed", string[]? dependsOn = null,
        string? verifies = null, string? run = null)
        => new(id, "title " + id, assignee, status, dependsOn ?? [], verifies, 1, null, run, "", "", TimeSpan.FromSeconds(10));

    private static TeamBoardSnapshot Board(TeamMemberInfo[] members, TeamTaskInfo[] tasks, TeamMessageInfo[]? messages = null,
        string phase = "running")
        => new("team-1", "goal", phase, members, tasks, messages ?? [], TimeSpan.FromSeconds(60));

    /// <summary>主控指派每名成员;依赖 = 交付(上游 → 下游),审查 = 审查者 → 被审查者;成员互发消息补一条协作。</summary>
    [TestMethod]
    public void Edges_ComeFromThePlanAndPeerMessages()
    {
        TeamBoardSnapshot team = Board(
            [Member("scout", 0), Member("fixer", 1), Member("checker", 2), Member("writer", 3)],
            [
                Task("t1", "scout"),
                Task("t2", "fixer", dependsOn: ["t1"]),
                // 审查任务天然依赖它审查的那个任务(引擎补上的),那条依赖算审查而不是交付
                Task("t3", "checker", dependsOn: ["t2"], verifies: "t2"),
                Task("t4", "writer")
            ],
            [new(DateTimeOffset.UtcNow, "writer", "scout", "peer", "which port?")]);

        IReadOnlyList<TeamEdge> edges = TeamConsoleModel.Edges(team);

        Assert.AreEqual(4, edges.Count(e => e.Kind == "assign" && e.From == TeamConsoleModel.Lead));
        TeamEdge handoff = edges.Single(e => e.Kind == "handoff");
        Assert.AreEqual(("scout", "fixer", "title t1"), (handoff.From, handoff.To, handoff.Subject));
        TeamEdge review = edges.Single(e => e.Kind == "review");
        Assert.AreEqual(("checker", "fixer"), (review.From, review.To));
        TeamEdge collab = edges.Single(e => e.Kind == "collab");
        Assert.AreEqual(("writer", "scout"), (collab.From, collab.To));
        Assert.AreEqual((handoff, true), TeamConsoleModel.FindEdge(edges, "fixer", "scout"), "反方向的消息沿同一条边倒着走");
        Assert.IsNull(TeamConsoleModel.FindEdge(edges, "checker", "writer"));
    }

    /// <summary>两名成员互相交付时只留一条边,算双向协作。</summary>
    [TestMethod]
    public void Edges_MutualHandoffsBecomeOneCollaboration()
    {
        TeamBoardSnapshot team = Board([Member("a", 0), Member("b", 1)],
            [Task("t1", "a"), Task("t2", "b", dependsOn: ["t1"]), Task("t3", "a", dependsOn: ["t2"])]);

        TeamEdge[] between = [.. TeamConsoleModel.Edges(team).Where(e => e.From != TeamConsoleModel.Lead)];

        Assert.HasCount(1, between);
        Assert.AreEqual("collab", between[0].Kind);
    }

    /// <summary>
    /// 主控在顶上;成员按最早那个任务的依赖层数排先后,从左到右摆成一行(上游在左、下游在右),
    /// 三人以内不换行。隔着一个人的连线会弯出去,不从中间那个节点上穿过。
    /// </summary>
    [TestMethod]
    public void Layout_PutsMembersLeftToRightInDependencyOrder()
    {
        // 声明的顺序故意打乱:排位置看的是依赖,不是声明顺序
        TeamBoardSnapshot team = Board(
            [Member("checker", 2), Member("scout", 0), Member("fixer", 1)],
            [Task("t1", "scout"), Task("t2", "fixer", dependsOn: ["t1"]), Task("t3", "checker", dependsOn: ["t2"], verifies: "t2")]);

        Dictionary<string, Point> at = TeamConsoleModel.Layout(team, out double height);

        Assert.AreEqual(TeamConsoleModel.GraphWidth / 2, at[TeamConsoleModel.Lead].X, 0.01, "一行三人:主控在正中,三条指派线都是直的");
        Assert.IsLessThan(at["scout"].Y, at[TeamConsoleModel.Lead].Y);
        Assert.AreEqual(at["scout"].Y, at["fixer"].Y, 0.01, "三人以内摆成一行");
        Assert.AreEqual(at["fixer"].Y, at["checker"].Y, 0.01);
        Assert.IsLessThan(at["fixer"].X, at["scout"].X, "上游在左");
        Assert.IsLessThan(at["checker"].X, at["fixer"].X, "下游在右");
        Assert.IsGreaterThan(at["checker"].Y + TeamConsoleModel.NodeRadius, height, "画布够高,名字不被裁掉");

        // 相邻两人之间没有东西挡着:走直线(控制点在两端的中点上)
        TeamEdgeGeometry straight = TeamConsoleModel.Geometry(new TeamEdge("scout", "fixer", "handoff"), at);
        Assert.AreEqual((straight.Start.Y + straight.End.Y) / 2, straight.Control.Y, 0.01);
        // 隔着 fixer 的那条(scout → checker)必须弯出去
        TeamEdgeGeometry around = TeamConsoleModel.Geometry(new TeamEdge("scout", "checker", "collab"), at);
        double nearest = Enumerable.Range(1, 9).Min(i =>
        {
            Point p = around.At(i / 10.0);
            return Math.Sqrt(Math.Pow(p.X - at["fixer"].X, 2) + Math.Pow(p.Y - at["fixer"].Y, 2));
        });
        Assert.IsGreaterThan(TeamConsoleModel.NodeRadius, nearest, "连线绕开了中间的节点");
    }

    /// <summary>
    /// 超过三人换成两行、人数尽量平均(4 = 2 + 2,5 = 3 + 2,6 = 3 + 3);主控放在直线被挡得最少的位置,
    /// 五人以内每条指派线都是直的,六人时只有正下方隔着一个人的那一条要绕。
    /// </summary>
    [TestMethod]
    [DataRow(4, 2, 0)]
    [DataRow(5, 3, 0)]
    [DataRow(6, 3, 1)]
    public void Layout_WrapsIntoTwoRowsAndKeepsTheLeadsLinesClear(int count, int firstRow, int curved)
    {
        TeamMemberInfo[] members = [.. Enumerable.Range(0, count).Select(i => Member("m" + i, i))];
        Dictionary<string, Point> at = TeamConsoleModel.Layout(Board(members, [.. members.Select((m, i) => Task("t" + i, m.Name))]), out double height);

        Assert.HasCount(2, at.Where(kv => kv.Key != TeamConsoleModel.Lead).Select(kv => kv.Value.Y).Distinct().ToArray());
        Assert.AreEqual(firstRow, at.Count(kv => kv.Key != TeamConsoleModel.Lead && Math.Abs(kv.Value.Y - at["m0"].Y) < 0.01));
        Assert.IsGreaterThan(at["m" + (count - 1)].Y + TeamConsoleModel.NodeRadius, height, "画布够高,第二行的名字不被裁掉");
        int bent = members.Count(member =>
        {
            TeamEdgeGeometry line = TeamConsoleModel.Geometry(new TeamEdge(TeamConsoleModel.Lead, member.Name, "assign"), at);
            // 直线的控制点正好在两端的中点上
            return Math.Abs(line.Control.X - ((line.Start.X + line.End.X) / 2)) > 0.01
                   || Math.Abs(line.Control.Y - ((line.Start.Y + line.End.Y) / 2)) > 0.01;
        });
        Assert.AreEqual(curved, bent, "主控的指派线里要绕路的条数");
    }

    /// <summary>
    /// 关系标签不压节点、不压节点下面的名字、互相也不压;成员之间的关系先占位置,主控的指派线后放,实在放不下的不画。
    /// </summary>
    [TestMethod]
    public void PlaceLabels_AvoidsNodesCaptionsAndEachOther()
    {
        TeamMemberInfo[] members = [.. Enumerable.Range(0, 6).Select(i => Member("m" + i, i))];
        TeamBoardSnapshot team = Board(members,
        [
            Task("t0", "m0"), Task("t1", "m1", dependsOn: ["t0"]), Task("t2", "m2", dependsOn: ["t1"]),
            Task("t3", "m3", dependsOn: ["t1"]), Task("t4", "m4", dependsOn: ["t2", "t3"]), Task("t5", "m5", dependsOn: ["t3"], verifies: "t3")
        ]);
        Dictionary<string, Point> at = TeamConsoleModel.Layout(team, out _);
        TeamEdge[] edges = [.. TeamConsoleModel.Edges(team)];
        const double width = 90;

        Point?[] placed = TeamConsoleModel.PlaceLabels(
            [.. edges.Select(e => (TeamConsoleModel.Geometry(e, at), width, e.Kind == "assign"))], at.Values);

        Rect[] boxes = [.. placed.OfType<Point>().Select(p => new Rect(p.X - (width / 2), p.Y - 10, width, 20))];
        for (int i = 0; i < edges.Length; i++)
        {
            if (edges[i].Kind != "assign") Assert.IsNotNull(placed[i], $"{edges[i].Key} 的标签放得下");
        }
        foreach (Rect box in boxes)
        {
            foreach (Point node in at.Values)
            {
                Assert.IsFalse(box.Intersects(new Rect(node.X - 42, node.Y - 42, 84, 84)), "标签不压节点");
                Assert.IsFalse(box.Intersects(new Rect(node.X - 90, node.Y + 50, 180, 34)), "标签不压节点下面的名字");
            }
            Assert.AreEqual(1, boxes.Count(other => other.Intersects(box)), "标签之间互不重叠(只和自己相交)");
        }
    }

    /// <summary>状态:在调工具 = 执行中,在跑但没调工具 = 思考中,等审批 / 等依赖 = 等待中,有失败的 = 阻塞,其余空闲。</summary>
    [TestMethod]
    public void StateOf_FollowsTheRunningTaskAndItsActivity()
    {
        TeamMemberInfo[] members = [Member("a", 0), Member("b", 1), Member("c", 2), Member("d", 3), Member("e", 4), Member("f", 5)];
        TeamBoardSnapshot team = Board(members,
        [
            Task("t1", "a", "running", run: "r1"),
            Task("t2", "b", "running", run: "r2"),
            Task("t3", "c", "running", run: "r3"),
            Task("t4", "d", "pending", dependsOn: ["t1"]),
            Task("t5", "e", "failed"),
            Task("t6", "f")
        ]);
        SubagentSnapshot[] runs =
        [
            new("r1", "a", "running", "", "", [], [], TimeSpan.Zero, Activity: "read_file /etc/nginx.conf", TeamTaskId: "t1"),
            new("r2", "b", "running", "", "", [], [], TimeSpan.Zero, TeamTaskId: "t2"),
            new("r3", "c", "running", "", "", [], [], TimeSpan.Zero, Activity: SubagentOrchestrator.WaitingForApproval, TeamTaskId: "t3")
        ];

        TeamMemberState[] states = [.. members.Select(m => TeamConsoleModel.StateOf(m, team, runs))];

        CollectionAssert.AreEqual((TeamMemberState[])
        [
            TeamMemberState.Working, TeamMemberState.Thinking, TeamMemberState.Waiting,
            TeamMemberState.Waiting, TeamMemberState.Blocked, TeamMemberState.Idle
        ], states);
        Assert.AreEqual(TeamMemberState.Online, TeamConsoleModel.LeadState(team));
        Assert.AreEqual(TeamMemberState.Idle, TeamConsoleModel.LeadState(team with { Phase = "completed" }));
        Assert.AreEqual(TeamMemberState.Idle, TeamConsoleModel.StateOf(members[3], team with { Phase = "cancelled" }, runs),
            "团队已经落定,没开跑的任务不再算「等待」");
    }

    /// <summary>用量是名下所有任务每一次运行的合计(返工的也算);上下文与模型取最近那次运行;工具按次数合并。</summary>
    [TestMethod]
    public void UsageOf_SumsEveryRunOfTheMembersTasks()
    {
        TeamMemberInfo member = new("fixer", "engineer", "planned", true, 0);
        TeamBoardSnapshot team = Board([member, Member("other", 1)],
            [Task("fix", "fixer", "running", run: "second"), Task("x", "other", run: "o1")]);
        SubagentSnapshot[] runs =
        [
            new("first", "fixer", "completed", "", "", [new("read_file", 2), new("patch_file", 1)], [], TimeSpan.Zero, ModelId: "old",
                InputTokens: 1000, OutputTokens: 100, TeamTaskId: "fix", CachedInputTokens: 400, ContextTokens: 900),
            new("second", "fixer", "running", "", "", [new("read_file", 3)], [], TimeSpan.Zero, ModelId: "actual",
                InputTokens: 2000, OutputTokens: 300, TeamTaskId: "fix", CachedInputTokens: 5000, ContextTokens: 1500),
            new("o1", "other", "completed", "", "", [], [], TimeSpan.Zero, InputTokens: 7, OutputTokens: 7, TeamTaskId: "x")
        ];

        TeamMemberUsage usage = TeamConsoleModel.UsageOf(member, team, runs);

        Assert.AreEqual(3000, usage.Input);
        Assert.AreEqual(400, usage.Output);
        Assert.AreEqual(2400, usage.Cached, "缓存命中不会超过那次运行的输入");
        Assert.AreEqual(3400, usage.Total);
        Assert.AreEqual(1500, usage.Context, "上下文取在跑的那次运行");
        Assert.AreEqual("actual", usage.ModelId, "模型写实际跑的那个,不是计划里的");
        Assert.AreEqual("read_file ×5, patch_file ×1", string.Join(", ", usage.Tools.Select(t => $"{t.Name} ×{t.Count}")));
    }

    /// <summary>采样:第一次只定基线,不足一个间隔不记,之后每个间隔记一次增量;序列封顶。</summary>
    [TestMethod]
    public void Telemetry_RecordsDeltasPerInterval()
    {
        var telemetry = new TeamTelemetry();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        static Dictionary<string, long> Totals(long a, long b) => new() { ["a"] = a, ["b"] = b };

        Assert.IsFalse(telemetry.Sample(start, Totals(100, 0)), "第一次只定基线");
        Assert.IsFalse(telemetry.Sample(start + TimeSpan.FromSeconds(1), Totals(500, 0)), "不足一个间隔");
        Assert.IsTrue(telemetry.Sample(start + TeamTelemetry.Interval, Totals(700, 50)));
        Assert.IsTrue(telemetry.Sample(start + (TeamTelemetry.Interval * 2), Totals(700, 80)));

        CollectionAssert.AreEqual((double[])[600, 0], telemetry.Member("a").ToArray());
        CollectionAssert.AreEqual((double[])[50, 30], telemetry.Member("b").ToArray());
        CollectionAssert.AreEqual((double[])[650, 30], telemetry.Team.ToArray());
        Assert.IsEmpty(telemetry.Member("nobody"));

        for (int i = 3; i < 80; i++) telemetry.Sample(start + (TeamTelemetry.Interval * i), Totals(700 + i, 80));
        Assert.HasCount(TeamTelemetry.TeamPoints, telemetry.Team);
        Assert.HasCount(TeamTelemetry.MemberPoints, telemetry.Member("a"));
    }

    /// <summary>发给「全体」的消息算在每名成员头上(发的人自己除外),不算在主控头上。</summary>
    [TestMethod]
    public void Involves_TreatsAllAsEveryMember()
    {
        var broadcast = new TeamMessageInfo(DateTimeOffset.UtcNow, "a", "all", "peer", "heads up");

        Assert.IsTrue(TeamConsoleModel.Involves(broadcast, "a"));
        Assert.IsTrue(TeamConsoleModel.Involves(broadcast, "b"));
        Assert.IsFalse(TeamConsoleModel.Involves(broadcast, TeamConsoleModel.Lead));
    }

    /// <summary>时间轴的总长:比已运行的时间多留一成,取够得着的最小一档(都能被 6 整除)。</summary>
    [TestMethod]
    [DataRow(0, 60)]
    [DataRow(50, 60)]
    [DataRow(60, 90)]
    [DataRow(400, 600)]
    [DataRow(3600, 5400)]
    [DataRow(30000, 36000)]
    public void SpanFor_PicksTheSmallestStepThatFits(int elapsedSeconds, int expected)
        => Assert.AreEqual(expected, TeamConsoleView.SpanFor(TimeSpan.FromSeconds(elapsedSeconds)));
}
