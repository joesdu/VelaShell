using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Agent;
using Path = Avalonia.Controls.Shapes.Path;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 团队拓扑与数据流:主控在上,成员按任务的先后分层排开;连线是关系(指派 / 交付 / 协作 / 审查),
/// 每来一条消息就有一串光点沿对应的连线飞过去。
/// </summary>
/// <remarks>
/// 节点外环 = 上下文占用,右上角圆点 = 此刻的状态(执行中带呼吸光晕,思考中带旋转虚线环)。
/// 点节点看成员,点连线看两人之间的对话;悬停时只留相关的节点与连线。
/// 整张图画在一块固定宽度的虚拟画布上,由 Viewbox 等比缩放到面板宽度。
/// </remarks>
internal sealed partial class TeamConsoleView
{
    private const double R = TeamConsoleModel.NodeRadius;

    private readonly Canvas _graphCanvas = new() { Width = TeamConsoleModel.GraphWidth };
    private readonly Canvas _edgeLayer = new(), _labelLayer = new(), _particleLayer = new() { IsHitTestVisible = false }, _nodeLayer = new();
    private readonly Dictionary<string, GraphNode> _nodes = [with(StringComparer.Ordinal)];
    private readonly List<GraphEdge> _graphEdges = [];
    private readonly List<Flight> _flights = [];
    private readonly DispatcherTimer _graphClock = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _graphKey = "";
    private TeamMessageInfo? _lastFlown;
    private bool _flightsPrimed, _attached;

    /// <summary>拓扑图上的节点(测试用):键是成员名,主控是 <see cref="TeamConsoleModel.Lead" />。</summary>
    internal IReadOnlyCollection<string> GraphNodes => _nodes.Keys;

    /// <summary>拓扑图上的连线(测试用)。</summary>
    internal IReadOnlyList<TeamEdge> GraphEdges => [.. _graphEdges.Select(e => e.Model)];

    /// <summary>此刻还在飞的光点串数(测试用)。</summary>
    internal int ActiveFlights => _flights.Count;

    private sealed class GraphNode
    {
        public required Canvas Root { get; init; }
        public required Ellipse Halo { get; init; }
        public required Ellipse Spin { get; init; }
        public required Ellipse Dot { get; init; }
        public required Path Arc { get; init; }
        public required TextBlock Sub { get; init; }
        public required ScaleTransform HaloScale { get; init; }
        public required RotateTransform SpinAngle { get; init; }
        public TeamMemberState State { get; set; } = TeamMemberState.Idle;
        public string StateColor { get; set; } = "";
        public double Share { get; set; } = -1;
    }

    private sealed class GraphEdge
    {
        public required TeamEdge Model { get; init; }
        public required TeamEdgeGeometry Geometry { get; init; }
        public required Canvas Root { get; init; }
        public required Path Line { get; init; }
        public required Panel Label { get; init; }

        /// <summary>有消息刚飞过:这一刻之前连线保持高亮的流动虚线。</summary>
        public long ActiveUntil { get; set; }
    }

    /// <summary>沿一条连线飞的一串光点。</summary>
    private sealed class Flight
    {
        public required GraphEdge Edge { get; init; }
        public required bool Reversed { get; init; }
        public required long Start { get; init; }
        public required (Ellipse Glow, Ellipse Core)[] Dots { get; init; }
    }

    private const long FlightMs = 1300, FlightStaggerMs = 140, EdgeGlowMs = 1400;

    private Border BuildGraphPanel()
    {
        var toggle = new CheckBox { Name = "TeamGraphLabels", Content = _loc["TeamGraphLabels"], IsChecked = true }
            .Bind(ThemeProperty, "AiCheckBoxTheme");
        toggle.IsCheckedChanged += (_, _) => _labelLayer.IsVisible = toggle.IsChecked == true;

        _graphCanvas.Children.Add(_edgeLayer);
        _graphCanvas.Children.Add(_labelLayer);
        _graphCanvas.Children.Add(_particleLayer);
        _graphCanvas.Children.Add(_nodeLayer);
        _labelLayer.IsHitTestVisible = false;
        var viewbox = new Viewbox { Name = "TeamGraph", Stretch = Stretch.Uniform, Child = _graphCanvas };

        var legend = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (string kind in (string[])["assign", "handoff", "collab", "review"])
        {
            (string key, string color) = EdgeLook(kind);
            var swatch = new Border { Width = 18, Height = 2, VerticalAlignment = VerticalAlignment.Center }.Bind(Border.BackgroundProperty, color);
            legend.Children.Add(LegendItem(swatch, _loc[key]));
        }
        legend.Children.Add(new Border { Width = 8 });
        foreach (TeamMemberState state in (TeamMemberState[])[TeamMemberState.Working, TeamMemberState.Thinking, TeamMemberState.Waiting, TeamMemberState.Blocked, TeamMemberState.Idle])
        {
            (string key, string color) = StateLook(state);
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center }.Bind(Shape.FillProperty, color);
            legend.Children.Add(LegendItem(dot, _loc[key]));
        }

        var stack = new StackPanel();
        stack.Children.Add(viewbox);
        stack.Children.Add(legend);
        _graphClock.Tick += (_, _) => TickGraph();
        return Panel(_loc["TeamGraph"], _loc["TeamGraphHint"], toggle, stack, "TeamGraphPanel");

        static StackPanel LegendItem(Control mark, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 14, 0) };
            row.Children.Add(mark);
            row.Children.Add(TeamUi.Text(text, "VelaTextSecondary", 11));
            return row;
        }
    }

    private void RefreshGraph()
    {
        IReadOnlyList<TeamEdge> edges = TeamConsoleModel.Edges(Team);
        // 结构(谁、怎么排、谁连谁)变了才重搭;平时只改节点上的状态
        string key = string.Join("|", Team.Members.Select(m => m.Name + ":" + m.Color))
                     + "#" + string.Join("|", Team.Tasks.Select(t => t.Assignee + ":" + string.Join(",", t.DependsOn)))
                     + "#" + string.Join("|", edges.Select(e => e.Key + e.Kind + e.Subject));
        if (key != _graphKey)
        {
            _graphKey = key;
            RebuildGraph(edges);
        }
        foreach ((string id, GraphNode node) in _nodes) UpdateNode(id, node);
        FlyNewMessages();
        UpdateGraphClock();
    }

    private void RebuildGraph(IReadOnlyList<TeamEdge> edges)
    {
        Dictionary<string, Point> positions = TeamConsoleModel.Layout(Team, out double height);
        _graphCanvas.Height = height;
        _edgeLayer.Children.Clear();
        _labelLayer.Children.Clear();
        _particleLayer.Children.Clear();
        _nodeLayer.Children.Clear();
        _nodes.Clear();
        _graphEdges.Clear();
        _flights.Clear();

        (TeamEdge Edge, TeamEdgeGeometry Geometry)[] drawn =
        [
            .. edges.Where(e => positions.ContainsKey(e.From) && positions.ContainsKey(e.To))
                .Select(e => (e, TeamConsoleModel.Geometry(e, positions)))
        ];
        Point?[] labels = TeamConsoleModel.PlaceLabels(
            [.. drawn.Select(d => (d.Geometry, LabelWidth(EdgeLabel(d.Edge)), d.Edge.Kind == "assign"))], positions.Values);
        for (int i = 0; i < drawn.Length; i++)
        {
            GraphEdge built = BuildEdge(drawn[i].Edge, drawn[i].Geometry, labels[i]);
            _graphEdges.Add(built);
            _edgeLayer.Children.Add(built.Root);
            _labelLayer.Children.Add(built.Label);
        }
        foreach ((string id, Point at) in positions)
        {
            GraphNode node = BuildNode(id, at);
            _nodes[id] = node;
            _nodeLayer.Children.Add(node.Root);
        }
    }

    /// <summary>标签大概多宽(虚拟坐标):全角字按一个字号算,半角按一半多一点,外加药丸的内边距。</summary>
    private static double LabelWidth(string text) => text.Sum(c => c > 0x2E7F ? 11.5 : 6.5) + 16;

    private GraphEdge BuildEdge(TeamEdge edge, TeamEdgeGeometry geometry, Point? labelAt)
    {
        string color = EdgeLook(edge.Kind).Color;
        var curve = new StreamGeometry();
        using (StreamGeometryContext g = curve.Open())
        {
            g.BeginFigure(geometry.Start, false);
            g.QuadraticBezierTo(geometry.Control, geometry.End);
            g.EndFigure(false);
        }
        var line = new Path { Data = curve, StrokeThickness = 1.6, Opacity = 0.5, StrokeLineCap = PenLineCap.Round, IsHitTestVisible = false }
            .Bind(Shape.StrokeProperty, color);
        // 透明的粗线只用来接鼠标:1.6px 的细线不好点
        var hit = new Path { Data = curve, StrokeThickness = 14, Stroke = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        var root = new Canvas();
        root.Children.Add(hit);
        root.Children.Add(line);
        root.Children.Add(Arrow(geometry.End, geometry.Control, color));
        if (edge.Kind == "collab") root.Children.Add(Arrow(geometry.Start, geometry.Control, color));

        string text = EdgeLabel(edge);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = TeamUi.Text(text, "VelaTextSecondary", 11)
        }.Bind(Border.BackgroundProperty, "VelaBgSidebar").Bind(Border.BorderBrushProperty, "VelaBorderPrimary");
        // 标签要以落点为中心:给一个固定大小的容器,让里头的药丸自己居中。哪儿都放不下(会压到节点或别的标签)就不画
        Point at = labelAt ?? geometry.Label;
        var label = new Panel { Width = 200, Height = 20, IsVisible = labelAt is not null };
        label.Children.Add(chip);
        Canvas.SetLeft(label, at.X - 100);
        Canvas.SetTop(label, at.Y - 10);

        var built = new GraphEdge { Model = edge, Geometry = geometry, Root = root, Line = line, Label = label };
        ToolTip.SetTip(hit, _loc.F("TeamEdgeTip", PartyOf(edge.From).Name, PartyOf(edge.To).Name,
            edge.Kind == "handoff" && edge.Subject is { Length: > 0 } subject ? subject : text));
        hit.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenView(new DrawerView("pair", edge.From, edge.To));
        };
        hit.PointerEntered += (_, _) => FocusGraph([edge.From, edge.To], [built]);
        hit.PointerExited += (_, _) => FocusGraph(null, null);
        return built;
    }

    /// <summary>箭头:尖端落在 <paramref name="tip" /> 稍外一点,方向是曲线在这一端的切线。</summary>
    private static Path Arrow(Point tip, Point control, string color)
    {
        double dx = tip.X - control.X, dy = tip.Y - control.Y;
        double length = Math.Max(0.001, Math.Sqrt((dx * dx) + (dy * dy)));
        double ux = dx / length, uy = dy / length;
        var point = new Point(tip.X + (ux * 7), tip.Y + (uy * 7));
        var left = new Point(tip.X - (ux * 2) - (uy * 4.5), tip.Y - (uy * 2) + (ux * 4.5));
        var right = new Point(tip.X - (ux * 2) + (uy * 4.5), tip.Y - (uy * 2) - (ux * 4.5));
        var head = new StreamGeometry();
        using (StreamGeometryContext g = head.Open())
        {
            g.BeginFigure(point, true);
            g.LineTo(left);
            g.LineTo(right);
            g.EndFigure(true);
        }
        return new Path { Data = head, IsHitTestVisible = false }.Bind(Shape.FillProperty, color);
    }

    private GraphNode BuildNode(string id, Point at)
    {
        Party party = PartyOf(id);
        var root = new Canvas();
        Canvas.SetLeft(root, at.X);
        Canvas.SetTop(root, at.Y);

        var haloScale = new ScaleTransform(1, 1);
        Ellipse halo = Centered(new Ellipse { Width = R * 2, Height = R * 2, Opacity = 0, RenderTransform = haloScale, IsHitTestVisible = false });
        var spinAngle = new RotateTransform(0);
        Ellipse spin = Centered(new Ellipse { Width = 100, Height = 100, StrokeThickness = 2, StrokeDashArray = [1.5, 3.5], Opacity = 0, RenderTransform = spinAngle, IsHitTestVisible = false });
        Ellipse track = Centered(new Ellipse { Width = (R * 2) + 4, Height = (R * 2) + 4, StrokeThickness = 4 }
            .Bind(Shape.FillProperty, "VelaBgSidebar").Bind(Shape.StrokeProperty, "VelaBorderSecondary"));
        // 外环上的那一段弧:画在以 (R+2, R+2) 为圆心的局部坐标里,再整体挪到节点中心
        var arc = new Path { StrokeThickness = 4, StrokeLineCap = PenLineCap.Round, IsHitTestVisible = false }.Bind(Shape.StrokeProperty, party.ColorKey);
        Canvas.SetLeft(arc, -(R + 2));
        Canvas.SetTop(arc, -(R + 2));
        Viewbox avatar = TeamAvatar.Create(party.Face, 66);
        Canvas.SetLeft(avatar, -33);
        Canvas.SetTop(avatar, -33);
        var dot = new Ellipse { Width = 17, Height = 17, StrokeThickness = 3 }.Bind(Shape.StrokeProperty, "VelaBgSidebar");
        Canvas.SetLeft(dot, 30 - 8.5);
        Canvas.SetTop(dot, -30 - 8.5);

        string title = party.Role.Length > 0 ? $"{party.Name} · {ChatFormatting.OneLine(party.Role, 18)}" : party.Name;
        TextBlock sub = TeamUi.Text("", "VelaTextSecondary", 11, mono: true);
        Panel nameBox = Caption(TeamUi.Text(title, "VelaTextPrimary", 12, FontWeight.SemiBold), 50);
        Panel subBox = Caption(sub, 67);

        var hit = Centered(new Ellipse { Width = 96, Height = 96, Fill = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) });
        ToolTip.SetTip(hit, _loc.F("TeamNodeTip", party.Name, party.Role));
        hit.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenView(new DrawerView("agent", id));
        };
        hit.PointerEntered += (_, _) =>
        {
            GraphEdge[] touching = [.. _graphEdges.Where(e => e.Model.From == id || e.Model.To == id)];
            FocusGraph([id, .. touching.Select(e => e.Model.From == id ? e.Model.To : e.Model.From)], touching);
        };
        hit.PointerExited += (_, _) => FocusGraph(null, null);

        foreach (Control child in (Control[])[halo, spin, track, arc, avatar, dot, nameBox, subBox, hit]) root.Children.Add(child);
        return new GraphNode { Root = root, Halo = halo, Spin = spin, Dot = dot, Arc = arc, Sub = sub, HaloScale = haloScale, SpinAngle = spinAngle };

        static Ellipse Centered(Ellipse shape)
        {
            Canvas.SetLeft(shape, -shape.Width / 2);
            Canvas.SetTop(shape, -shape.Height / 2);
            return shape;
        }

        static Panel Caption(TextBlock text, double top)
        {
            text.HorizontalAlignment = HorizontalAlignment.Center;
            var box = new Panel { Width = 240, Height = 17, IsHitTestVisible = false };
            box.Children.Add(text);
            Canvas.SetLeft(box, -120);
            Canvas.SetTop(box, top);
            return box;
        }
    }

    private void UpdateNode(string id, GraphNode node)
    {
        TeamMemberInfo? member = Team.Members.FirstOrDefault(m => m.Name == id);
        TeamMemberState state = StateOf(id);
        string color = StateLook(state).Color;
        if (color != node.StateColor)
        {
            node.StateColor = color;
            node.Halo.Bind(Shape.FillProperty, color);
            node.Spin.Bind(Shape.StrokeProperty, color);
            node.Dot.Bind(Shape.FillProperty, color);
        }
        if (state != node.State)
        {
            node.State = state;
            // 思考中:虚线环转起来;等待 / 阻塞:虚线环停着;其余不画
            node.Spin.Opacity = state switch
            {
                TeamMemberState.Thinking => 1,
                TeamMemberState.Waiting or TeamMemberState.Blocked => 0.8,
                _ => 0
            };
            if (state != TeamMemberState.Working) node.Halo.Opacity = 0;
        }
        if (member is null)
        {
            // 主控的用量算在对话里,不在团队这本账上:不画占用弧,只写它用的模型
            node.Arc.IsVisible = false;
            node.Sub.Text = Team.LeadModelId is { } lead ? ChatFormatting.OneLine(ModelName(lead), 26) : "";
            return;
        }
        TeamMemberUsage usage = UsageOf(member);
        double share = ContextShare(usage) ?? 0;
        if (Math.Abs(share - node.Share) > 0.002)
        {
            node.Share = share;
            node.Arc.Data = ArcGeometry(share);
        }
        string model = ChatFormatting.OneLine(ModelName(usage.ModelId), 20);
        node.Sub.Text = (model.Length > 0 ? model + " · " : "") + ChatFormatting.Compact(usage.Total);
    }

    /// <summary>外环上从 12 点方向顺时针画过 <paramref name="share" />(0–1)的一段弧。</summary>
    private static StreamGeometry? ArcGeometry(double share)
    {
        if (share <= 0.001) return null;
        double c = R + 2;
        var geometry = new StreamGeometry();
        using StreamGeometryContext g = geometry.Open();
        var top = new Point(c, c - R);
        g.BeginFigure(top, false);
        if (share >= 0.999)
        {
            // 整圈拆成两个半圆:起点等于终点的弧画不出来
            g.ArcTo(new Point(c, c + R), new Size(R, R), 0, false, SweepDirection.Clockwise);
            g.ArcTo(top, new Size(R, R), 0, false, SweepDirection.Clockwise);
        }
        else
        {
            double angle = share * 2 * Math.PI;
            g.ArcTo(new Point(c + (R * Math.Sin(angle)), c - (R * Math.Cos(angle))), new Size(R, R), 0, share > 0.5, SweepDirection.Clockwise);
        }
        g.EndFigure(false);
        return geometry;
    }

    /// <summary>悬停时只留相关的节点与连线;传 null 恢复全部。</summary>
    private void FocusGraph(IReadOnlyCollection<string>? nodes, IReadOnlyCollection<GraphEdge>? edges)
    {
        foreach ((string id, GraphNode node) in _nodes)
        {
            node.Root.Opacity = nodes is null || nodes.Contains(id) ? 1 : 0.3;
        }
        foreach (GraphEdge edge in _graphEdges)
        {
            bool on = edges is null || edges.Contains(edge);
            edge.Root.Opacity = on ? 1 : 0.12;
            edge.Label.Opacity = on ? 1 : 0.08;
            if (edges is not null && on)
            {
                edge.Line.Opacity = 1;
                edge.Line.StrokeThickness = 2.4;
            }
            else if (edge.ActiveUntil <= _clock.ElapsedMilliseconds)
            {
                edge.Line.Opacity = 0.5;
                edge.Line.StrokeThickness = 1.6;
            }
        }
    }

    // ---------- 数据流:光点 ----------

    /// <summary>上次刷新之后新到的消息,各沿对应的连线飞一串光点。第一次刷新只记位置,不把历史消息重放一遍。</summary>
    private void FlyNewMessages()
    {
        IReadOnlyList<TeamMessageInfo> messages = Team.Messages;
        TeamMessageInfo? last = messages.Count > 0 ? messages[^1] : null;
        if (!_flightsPrimed)
        {
            _flightsPrimed = true;
            _lastFlown = last;
            return;
        }
        int from = 0;
        if (_lastFlown is not null)
        {
            int seen = -1;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(messages[i], _lastFlown) || messages[i] == _lastFlown)
                {
                    seen = i;
                    break;
                }
            }
            // 找不到上次那条(消息表被截过头):这一拍全当旧的
            from = seen < 0 ? messages.Count : seen + 1;
        }
        _lastFlown = last;
        if (!_attached) return;
        for (int i = from; i < messages.Count; i++)
        {
            TeamMessageInfo message = messages[i];
            string color = KindLook(message.Kind).Color;
            if (message.To == "all")
            {
                foreach (TeamMemberInfo member in Team.Members.Where(m => m.Name != message.From)) Fly(message.From, member.Name, color);
            }
            else
            {
                Fly(message.From, message.To, color);
            }
        }
    }

    /// <summary>沿 <paramref name="from" /> 与 <paramref name="to" /> 之间的连线飞一串光点;两人之间没有连线就不飞。</summary>
    internal void Fly(string from, string to, string colorKey)
    {
        GraphEdge? edge = _graphEdges.FirstOrDefault(e => e.Model.From == from && e.Model.To == to);
        bool reversed = false;
        if (edge is null)
        {
            edge = _graphEdges.FirstOrDefault(e => e.Model.From == to && e.Model.To == from);
            reversed = true;
        }
        if (edge is null) return;
        long now = _clock.ElapsedMilliseconds;
        var dots = new (Ellipse Glow, Ellipse Core)[3];
        for (int k = 0; k < dots.Length; k++)
        {
            double glowRadius = 9 - (k * 2), coreRadius = 4.6 - k;
            var glow = new Ellipse { Width = glowRadius * 2, Height = glowRadius * 2, Opacity = 0 }.Bind(Shape.FillProperty, colorKey);
            var core = new Ellipse { Width = coreRadius * 2, Height = coreRadius * 2, Opacity = 0 }.Bind(Shape.FillProperty, colorKey);
            _particleLayer.Children.Add(glow);
            _particleLayer.Children.Add(core);
            dots[k] = (glow, core);
        }
        _flights.Add(new Flight { Edge = edge, Reversed = reversed, Start = now, Dots = dots });
        edge.ActiveUntil = now + FlightMs + EdgeGlowMs;
        edge.Line.Opacity = 1;
        edge.Line.StrokeThickness = 2.6;
        edge.Line.StrokeDashArray = [2.7, 2.3];
        UpdateGraphClock();
    }

    // ---------- 动画的钟 ----------

    private bool GraphNeedsClock()
        => _flights.Count > 0
           || _graphEdges.Any(e => e.ActiveUntil > 0)
           || _nodes.Values.Any(n => n.State is TeamMemberState.Working or TeamMemberState.Thinking);

    /// <summary>有东西要动(光点、流动的虚线、呼吸光晕、旋转环)且页面在可视树上时才让钟走。</summary>
    private void UpdateGraphClock()
    {
        _attached = TopLevel.GetTopLevel(this) is not null;
        if (_attached && GraphNeedsClock())
        {
            if (!_graphClock.IsEnabled) _graphClock.Start();
        }
        else
        {
            _graphClock.Stop();
        }
    }

    private void StopGraphClock()
    {
        _attached = false;
        _graphClock.Stop();
    }

    private void TickGraph()
    {
        long now = _clock.ElapsedMilliseconds;
        foreach (GraphNode node in _nodes.Values)
        {
            if (node.State == TeamMemberState.Working)
            {
                // 呼吸光晕:1.6 秒一圈,从外环大小放大到 1.5 倍、同时淡出
                double phase = now % 1600 / 1600.0;
                node.HaloScale.ScaleX = node.HaloScale.ScaleY = 1 + (0.5 * phase);
                node.Halo.Opacity = 0.5 * (1 - phase);
            }
            else if (node.State == TeamMemberState.Thinking)
            {
                node.SpinAngle.Angle = now % 3000 / 3000.0 * 360;
            }
        }
        for (int i = _flights.Count - 1; i >= 0; i--)
        {
            Flight flight = _flights[i];
            bool done = true;
            for (int k = 0; k < flight.Dots.Length; k++)
            {
                double progress = (now - flight.Start - (k * FlightStaggerMs)) / (double)FlightMs;
                (Ellipse glow, Ellipse core) = flight.Dots[k];
                if (progress is <= 0 or >= 1)
                {
                    glow.Opacity = core.Opacity = 0;
                    done &= progress >= 1;
                    continue;
                }
                done = false;
                double eased = progress < 0.5 ? 2 * progress * progress : 1 - (Math.Pow((-2 * progress) + 2, 2) / 2);
                Point at = flight.Edge.Geometry.At(flight.Reversed ? 1 - eased : eased);
                Place(glow, at);
                Place(core, at);
                glow.Opacity = 0.22;
                core.Opacity = 1;
            }
            if (done)
            {
                foreach ((Ellipse glow, Ellipse core) in flight.Dots)
                {
                    _particleLayer.Children.Remove(glow);
                    _particleLayer.Children.Remove(core);
                }
                _flights.RemoveAt(i);
            }
        }
        foreach (GraphEdge edge in _graphEdges)
        {
            if (edge.ActiveUntil <= 0) continue;
            if (now >= edge.ActiveUntil)
            {
                edge.ActiveUntil = 0;
                edge.Line.StrokeDashArray = null;
                edge.Line.StrokeDashOffset = 0;
                edge.Line.Opacity = 0.5;
                edge.Line.StrokeThickness = 1.6;
            }
            else
            {
                // 虚线顺着数据的方向流:半秒走完一个周期
                edge.Line.StrokeDashOffset = -(now % 500 / 500.0 * 5);
            }
        }
        if (!GraphNeedsClock()) _graphClock.Stop();

        static void Place(Ellipse dot, Point at)
        {
            Canvas.SetLeft(dot, at.X - (dot.Width / 2));
            Canvas.SetTop(dot, at.Y - (dot.Height / 2));
        }
    }
}
