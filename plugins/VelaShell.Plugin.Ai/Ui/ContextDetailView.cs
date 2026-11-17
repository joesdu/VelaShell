using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 上下文明细(点击输入框右下角的用量打开):先给结论,再给方格图与各类合计,
/// 最后每一类都能展开到单个工具、单台 MCP 服务器、单段消息。
/// </summary>
/// <remarks>
/// <para>
/// 悬停面板(<see cref="UsageDetailView" />)回答「占了多少、离压缩多远」;这里回答「为什么这么快就满了」——
/// 哪台 MCP 服务器没被调用却一直占着工具定义、哪一段工具输出最大。提示只写看得到的事实和能做的事,不猜。
/// </para>
/// <para>
/// 方格图每格是窗口的 1/200:各类按顺序填格,空格画空心框,压缩线之后那段(到了就会先压缩,用不上)
/// 与给回复留的余量各用淡色,一眼数得出还剩几格。窗口未知时不画方格。
/// </para>
/// </remarks>
internal sealed class ContextDetailView : StackPanel
{
    private const int Columns = 20;
    private const int Rows = 10;
    private const int Cells = Columns * Rows;
    private const int MaxInsights = 4;
    private const int ToolRows = 8;
    private const int McpToolRows = 3;

    private readonly Loc _loc;
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal)
    {
        ["messages"] = true,
        ["mcp"] = true
    };

    private string _plain = "";

    public ContextDetailView(Loc loc)
    {
        _loc = loc;
        Spacing = 10;
    }

    /// <summary>纯文本明细(读屏与测试用)。</summary>
    public override string ToString() => _plain;

    public void Update(UsageFigures f)
    {
        var u = new ContextUsage(f);
        var plain = new StringBuilder();
        Children.Clear();

        // ── 标题:名字 + 模型 | 已用 / 窗口 ──
        var header = new Grid { ColumnDefinitions = [with("*,Auto")], ColumnSpacing = 10 };
        var title = new StackPanel { Spacing = 0 };
        title.Children.Add(UsageVisuals.Text(_loc["ContextDetailTitle"], size: "VelaFontSize13", bold: true));
        if (f.Model.Length > 0) title.Children.Add(UsageVisuals.Text(f.Model, "VelaTextTertiary", "VelaFontSize11"));
        header.Children.Add(title);
        string total = u.Window > 0 ? $"{u.Estimate}{u.Used:N0} / {u.Window:N0}" : $"{u.Estimate}{u.Used:N0}";
        TextBlock totalText = UsageVisuals.Number(total, size: "VelaFontSize12", bold: true);
        totalText.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(totalText, 1);
        header.Children.Add(totalText);
        Children.Add(header);
        plain.AppendLine($"{_loc["ContextDetailTitle"]}: {total}");

        if (u.Window > 0 || u.Slices.Count > 0)
            Children.Add(UsageVisuals.Bar(u.BarSegments(), 10, u.CompactAt > 0 ? u.CompactAt / (double)u.Window : null));
        string caption = UsageDetailView.ContextCaption(u, _loc);
        Children.Add(UsageVisuals.Text(caption, u.NearCompaction ? "VelaWarning" : "VelaTextTertiary", "VelaFontSize11", wrap: true));
        plain.AppendLine(caption);
        if (f.WindowWarning.Length > 0)
        {
            Children.Add(UsageVisuals.Text(f.WindowWarning, "VelaWarning", "VelaFontSize11", wrap: true));
            plain.AppendLine(f.WindowWarning);
        }

        // ── 结论 ──
        List<(string Text, bool Warn)> insights = Insights(u, f);
        if (insights.Count > 0)
        {
            var list = new StackPanel { Spacing = 5 };
            foreach ((string text, bool warn) in insights)
            {
                var row = new Grid { ColumnDefinitions = [with("Auto,*")], ColumnSpacing = 8 };
                var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
                dot[!Shape.FillProperty] = new DynamicResourceExtension(warn ? "VelaWarning" : "VelaAccent");
                row.Children.Add(dot);
                TextBlock body = UsageVisuals.Text(text, "VelaTextSecondary", wrap: true);
                Grid.SetColumn(body, 1);
                row.Children.Add(body);
                list.Children.Add(row);
                plain.AppendLine("• " + text);
            }
            Children.Add(list);
        }

        if (u.Breakdown is not { } b)
        {
            string empty = _loc["ContextDetailEmpty"];
            Children.Add(UsageVisuals.Text(empty, "VelaTextTertiary", wrap: true));
            plain.AppendLine(empty);
        }
        else
        {
            // ── 方格 + 合计表 ──
            var overview = new WrapPanel { ItemSpacing = 16, LineSpacing = 8 };
            if (u.Window > 0) overview.Children.Add(CellGrid(u));
            overview.Children.Add(Legend(u, plain));
            Children.Add(overview);

            // ── 各类展开 ──
            var groups = new StackPanel { Spacing = 2 };
            AddMessages(groups, u, b, plain);
            AddTools(groups, u, b, plain);
            AddMcp(groups, u, b, plain);
            if (b.Summary > 0) groups.Children.Add(Leaf(u, ContextSlice.Summary, _loc["ContextSummarySub"], plain));
            if (b.System > 0) groups.Children.Add(Leaf(u, ContextSlice.System, "", plain));
            Children.Add(groups);
        }

        // ── 缓存与本会话 ──
        Children.Add(UsageVisuals.Rule());
        if (!f.Streaming && f.LastInput > 0)
        {
            int hit = (int)Math.Round(Math.Clamp(f.LastCacheRead, 0, f.LastInput) * 100.0 / f.LastInput);
            var cache = new Grid { ColumnDefinitions = [with("*,Auto")] };
            cache.Children.Add(UsageVisuals.Text(_loc["UsageSectionCache"], "VelaTextSecondary"));
            TextBlock hitText = UsageVisuals.Text(_loc.F("UsageCacheHitValue", hit), bold: true);
            Grid.SetColumn(hitText, 1);
            cache.Children.Add(hitText);
            Children.Add(cache);
        }
        var session = new Grid { ColumnDefinitions = [with("*,*,*,*")], ColumnSpacing = 8 };
        UsageDetailView.AddStat(session, 0, _loc["UsageInput"], u.Estimate + ChatFormatting.Compact(f.TotalInput), $"{f.TotalInput:N0}");
        UsageDetailView.AddStat(session, 1, _loc["UsageOutput"], u.Estimate + ChatFormatting.Compact(f.TotalOutput), $"{f.TotalOutput:N0}");
        if (f.TotalReasoning > 0)
            UsageDetailView.AddStat(session, 2, _loc["UsageReasoning"], ChatFormatting.Compact(f.TotalReasoning), $"{f.TotalReasoning:N0}");
        if (f.Cost is { } cost)
            UsageDetailView.AddStat(session, 3, _loc["UsageCostLabel"], ChatPanelView.FormatCost(cost), _loc["UsageCostNote"]);
        Children.Add(session);
        Children.Add(UsageVisuals.Text(_loc["ContextEstimateNote"], "VelaTextMuted", "VelaFontSize11", wrap: true));

        _plain = plain.ToString().TrimEnd();
    }

    // ───────────────────────── 结论 ─────────────────────────

    private List<(string, bool)> Insights(ContextUsage u, UsageFigures f)
    {
        var list = new List<(string, bool)>();
        if (!f.CompactionEnabled && u.Window > 0
            && u.Used > ContextCompactor.TriggerTokens((int)u.Window, (int)u.Reserve))
            list.Add((_loc["ContextInsightCompactOff"], true));
        if (u.Breakdown is { } b)
        {
            foreach (McpServerShare server in b.McpServers.Where(s => s.Calls == 0 && s.Tools.Count > 0).Take(2))
                list.Add((_loc.F("ContextInsightIdleMcp", server.Name, server.Tools.Count,
                    ChatFormatting.Compact(u.Scale(server.Tokens))), false));

            int messages = b.Messages, results = b.Parts.GetValueOrDefault(ContextPart.ToolResult);
            if (messages > 0 && u.Scale(messages) >= u.Used * 0.2 && results >= messages * 0.4
                && b.Largest.FirstOrDefault(i => i.Part == ContextPart.ToolResult) is { } biggest)
                list.Add((_loc.F("ContextInsightToolResults", (int)Math.Round(results * 100.0 / messages),
                    biggest.Label, ChatFormatting.Compact(u.Scale(biggest.Tokens))), false));

            long fixedCost = u.Scale(b.System + b.Tools + b.Mcp);
            if (u.Used > 0 && fixedCost >= u.Used * 0.5)
                list.Add((_loc.F("ContextInsightFixed", ChatFormatting.Compact(fixedCost),
                    (int)Math.Round(fixedCost * 100.0 / u.Used)), false));
        }
        return [.. list.Take(MaxInsights)];
    }

    // ───────────────────────── 方格与合计表 ─────────────────────────

    /// <summary>
    /// 200 格,每格窗口的 1/200。一格取覆盖它最多的那一类;已用部分不到半格的画成半透明(像 /context 里的半格)。
    /// </summary>
    private static UniformGrid CellGrid(ContextUsage u)
    {
        var parts = new List<(long Tokens, string? Tone, double Opacity, bool Used)>();
        foreach ((ContextSlice slice, long tokens) in u.Slices) parts.Add((tokens, UsageVisuals.ToneOf(slice), 1, true));
        parts.Add((u.Free, null, 1, false));
        parts.Add((u.Buffer, UsageVisuals.BufferTone, UsageVisuals.BufferOpacity, false));
        parts.Add((u.Reserve, UsageVisuals.ReserveTone, UsageVisuals.ReserveOpacity, false));

        var grid = new UniformGrid { Columns = Columns, Rows = Rows, VerticalAlignment = VerticalAlignment.Top };
        double cell = u.Window / (double)Cells;
        string? lastUsedTone = parts.LastOrDefault(p => p.Used).Tone;
        for (int i = 0; i < Cells; i++)
        {
            double from = i * cell, to = from + cell, start = 0, usedCover = 0, bestCover = -1;
            (long Tokens, string? Tone, double Opacity, bool Used) best = parts[^1];
            foreach ((long Tokens, string? Tone, double Opacity, bool Used) part in parts)
            {
                double cover = Math.Max(0, Math.Min(to, start + part.Tokens) - Math.Max(from, start));
                if (part.Used) usedCover += cover;
                if (cover > bestCover)
                {
                    bestCover = cover;
                    best = part;
                }
                start += part.Tokens;
            }
            var square = new Border { Width = 9, Height = 9, Margin = new Thickness(1), CornerRadius = new CornerRadius(2) };
            if (best.Used || usedCover > cell * 0.1)
            {
                string tone = best.Used ? best.Tone! : lastUsedTone!;
                square[!Border.BackgroundProperty] = new DynamicResourceExtension(tone);
                square.Opacity = usedCover >= cell * 0.5 ? 1 : 0.45;
            }
            else if (best.Tone is { } tone)
            {
                square[!Border.BackgroundProperty] = new DynamicResourceExtension(tone);
                square.Opacity = best.Opacity;
            }
            else
            {
                square.BorderThickness = new Thickness(1);
                square[!Border.BorderBrushProperty] = new DynamicResourceExtension(UsageVisuals.FreeTone);
            }
            grid.Children.Add(square);
        }
        return grid;
    }

    private Grid Legend(ContextUsage u, StringBuilder plain)
    {
        var table = new Grid { ColumnDefinitions = [with("Auto,Auto,Auto,Auto")], ColumnSpacing = 8, RowSpacing = 3 };
        void Row(Control swatch, string name, long tokens, bool estimate = true)
        {
            int row = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Add(table, swatch, row, 0);
            Add(table, UsageVisuals.Text(name, "VelaTextSecondary", "VelaFontSize11"), row, 1);
            TextBlock value = UsageVisuals.Number((estimate ? u.Estimate : "") + ChatFormatting.Compact(tokens));
            ToolTip.SetTip(value, $"{tokens:N0}");
            Add(table, value, row, 2);
            Add(table, UsageVisuals.Number(u.PercentOf(tokens), "VelaTextTertiary"), row, 3);
            plain.AppendLine($"{name}: {tokens:N0}{(u.Window > 0 ? " (" + u.PercentOf(tokens) + ")" : "")}");
        }
        foreach ((ContextSlice slice, long tokens) in u.Slices)
            Row(UsageVisuals.Swatch(UsageVisuals.ToneOf(slice)), _loc[UsageVisuals.NameKey(slice)], tokens);
        if (u.Window > 0)
        {
            Row(UsageVisuals.Swatch("", hollow: true), _loc["ContextFree"], u.Free);
            if (u.Buffer > 0)
                Row(UsageVisuals.Swatch(UsageVisuals.BufferTone, UsageVisuals.BufferOpacity), _loc["ContextBuffer"], u.Buffer, false);
            if (u.Reserve > 0)
                Row(UsageVisuals.Swatch(UsageVisuals.ReserveTone, UsageVisuals.ReserveOpacity), _loc["ContextReserve"], u.Reserve, false);
            int row = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock scale = UsageVisuals.Text(_loc.F("ContextCellSize", ChatFormatting.Compact(u.Window / Cells)), "VelaTextMuted", "VelaFontSize11");
            scale.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetColumnSpan(scale, 4);
            Add(table, scale, row, 0);
        }
        return table;
    }

    // ───────────────────────── 各类展开 ─────────────────────────

    private void AddMessages(StackPanel groups, ContextUsage u, ContextBreakdown b, StringBuilder plain)
    {
        if (b.Messages <= 0) return;
        var rows = new List<(string Label, long Tokens, bool Heading)>();
        foreach (ContextPart part in Enum.GetValues<ContextPart>())
        {
            int raw = b.Parts.GetValueOrDefault(part);
            if (raw > 0) rows.Add((_loc["ContextPart" + part], u.Scale(raw), false));
        }
        if (b.Largest.Count > 0)
        {
            rows.Add((_loc["ContextLargest"], 0, true));
            foreach (ContextLargeItem item in b.Largest)
                rows.Add(($"{_loc["ContextPart" + item.Part]} · {item.Label}", u.Scale(item.Tokens), false));
        }
        groups.Children.Add(Group("messages", u, ContextSlice.Messages, _loc.F("ContextMessagesSub", b.MessageCount), rows, plain));
    }

    private void AddTools(StackPanel groups, ContextUsage u, ContextBreakdown b, StringBuilder plain)
    {
        if (b.BuiltinTools.Count == 0) return;
        var rows = b.BuiltinTools.Take(ToolRows).Select(t => (t.Name, u.Scale(t.Tokens), false)).ToList();
        if (b.BuiltinTools.Count > ToolRows)
            rows.Add((_loc.F("ContextMoreItems", b.BuiltinTools.Count - ToolRows),
                u.Scale(b.BuiltinTools.Skip(ToolRows).Sum(t => t.Tokens)), false));
        groups.Children.Add(Group("tools", u, ContextSlice.Tools, _loc.F("ContextToolsSub", b.BuiltinTools.Count), rows, plain));
    }

    private void AddMcp(StackPanel groups, ContextUsage u, ContextBreakdown b, StringBuilder plain)
    {
        if (b.McpServers.Count == 0) return;
        var rows = new List<(string, long, bool)>();
        foreach (McpServerShare server in b.McpServers)
        {
            string calls = server.Calls == 0 ? _loc["ContextMcpUnused"] : _loc.F("ContextMcpCalls", server.Calls);
            rows.Add(($"{server.Name} · {_loc.F("ContextToolsSub", server.Tools.Count)} · {calls}", u.Scale(server.Tokens), false));
            foreach (ContextEntry tool in server.Tools.Take(McpToolRows))
                rows.Add(("    " + tool.Name, u.Scale(tool.Tokens), false));
            if (server.Tools.Count > McpToolRows)
                rows.Add(("    " + _loc.F("ContextMoreItems", server.Tools.Count - McpToolRows),
                    u.Scale(server.Tools.Skip(McpToolRows).Sum(t => t.Tokens)), false));
        }
        string sub = _loc.F("ContextMcpSub", b.McpServers.Count, b.McpServers.Sum(s => s.Tools.Count));
        groups.Children.Add(Group("mcp", u, ContextSlice.Mcp, sub, rows, plain));
    }

    /// <summary>一类的标题行:色块、名字、副标题、合计、占窗口百分比。</summary>
    private Grid Heading(ContextUsage u, ContextSlice slice, string sub, StringBuilder plain)
    {
        long tokens = u.Slices.FirstOrDefault(s => s.Slice == slice).Tokens;
        var grid = new Grid { ColumnDefinitions = [with("Auto,Auto,*,Auto,Auto")], ColumnSpacing = 8 };
        Add(grid, UsageVisuals.Swatch(UsageVisuals.ToneOf(slice)), 0, 0);
        Add(grid, UsageVisuals.Text(_loc[UsageVisuals.NameKey(slice)], bold: true), 0, 1);
        Add(grid, UsageVisuals.Text(sub, "VelaTextTertiary", "VelaFontSize11"), 0, 2);
        Add(grid, UsageVisuals.Number(u.Estimate + ChatFormatting.Compact(tokens), size: "VelaFontSize12", bold: true), 0, 3);
        TextBlock percent = UsageVisuals.Number(u.PercentOf(tokens), "VelaTextTertiary");
        percent.MinWidth = 38;
        Add(grid, percent, 0, 4);
        plain.AppendLine($"{_loc[UsageVisuals.NameKey(slice)]} {sub}: {tokens:N0}".Replace("  ", " "));
        return grid;
    }

    /// <summary>可展开的一类;展开状态跨刷新保留。</summary>
    private Expander Group(string key, ContextUsage u, ContextSlice slice, string sub,
        IReadOnlyList<(string Label, long Tokens, bool Heading)> rows, StringBuilder plain)
    {
        Grid heading = Heading(u, slice, sub, plain);
        var body = new Grid { ColumnDefinitions = [with("*,72,Auto")], ColumnSpacing = 8, RowSpacing = 3 };
        long max = Math.Max(1, rows.Where(r => !r.Heading).Select(r => r.Tokens).DefaultIfEmpty(1).Max());
        string tone = UsageVisuals.ToneOf(slice);
        foreach ((string label, long tokens, bool isHeading) in rows)
        {
            int row = body.RowDefinitions.Count;
            body.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (isHeading)
            {
                TextBlock head = UsageVisuals.Text(label, "VelaTextMuted", "VelaFontSize11");
                head.Margin = new Thickness(0, 4, 0, 0);
                Grid.SetColumnSpan(head, 3);
                Add(body, head, row, 0);
                continue;
            }
            TextBlock name = UsageVisuals.Text(label, "VelaTextSecondary", "VelaFontSize11");
            ToolTip.SetTip(name, label.Trim());
            Add(body, name, row, 0);
            Control bar = UsageVisuals.Bar([new(tokens / (double)max, tone)], 3);
            bar.VerticalAlignment = VerticalAlignment.Center;
            Add(body, bar, row, 1);
            TextBlock value = UsageVisuals.Number(u.Estimate + ChatFormatting.Compact(tokens));
            ToolTip.SetTip(value, $"{tokens:N0}");
            Add(body, value, row, 2);
            plain.AppendLine($"  {label.Trim()}: {tokens:N0}");
        }

        var expander = new Expander
        {
            Classes = { "subagentRun" },
            Header = heading,
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _expanded.GetValueOrDefault(key)
        };
        Avalonia.Automation.AutomationProperties.SetName(expander, _loc[UsageVisuals.NameKey(slice)]);
        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty) _expanded[key] = expander.IsExpanded;
        };
        return expander;
    }

    /// <summary>没有子项的一类(系统提示词、压缩摘要):只有标题行,左边空出展开箭头的位置与上面对齐。</summary>
    private Grid Leaf(ContextUsage u, ContextSlice slice, string sub, StringBuilder plain)
    {
        Grid heading = Heading(u, slice, sub, plain);
        heading.Margin = new Thickness(23, 5, 6, 5);
        return heading;
    }

    private static void Add(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
