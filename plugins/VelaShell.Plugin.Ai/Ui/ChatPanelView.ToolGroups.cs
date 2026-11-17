using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using LiveMarkdown.Avalonia;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 工具调用的两层收纳:相邻的调用并成一组(<see cref="ToolGroup" />),
/// 一轮结束后把思考、工具组与中途的过渡话整段收进「工作过程」(<see cref="ProcessFold" />)。
/// </summary>
/// <remarks>
/// 一轮 Agent 动辄几十次调用,一次一张卡,回复就被拉成一长条,真正的结论埋在最底下。
/// 收纳只改可视树的摆放,不碰卡片本身:卡片照旧逐张创建、入库、回放,点开仍是完整参数与结果。
/// </remarks>
public partial class ChatPanelView
{
    /// <summary>不进组的工具:传输卡带进度条与取消按钮,折起来就看不到进度了。</summary>
    private static bool IsStandaloneTool(string name)
        => name is "upload_local_file" or "download_remote_file" or "transfer_status" or "transfer_wait"
            or "transfer_resume" or "transfer_cancel";

    /// <summary>工具结果是不是失败 / 被拒 / 参数错 / 被取消的那几句固定话术。</summary>
    internal static bool IsFailedToolResult(string result)
        => result.StartsWith("Tool failed", StringComparison.Ordinal)
           || result.StartsWith("Invalid arguments for ", StringComparison.Ordinal)
           || result.StartsWith("Execution was cancelled", StringComparison.Ordinal)
           || SubagentOrchestrator.IsDenialResult(result);

    internal sealed partial class ToolCard
    {
        /// <summary>所在的组;独立摆放的卡为 null。</summary>
        internal ToolGroup? Group { get; set; }

        /// <summary>结果还没回来。</summary>
        internal bool IsRunning { get => !field; private set; }

        /// <summary>结果是失败 / 被拒一类。</summary>
        internal bool IsFailed { get; private set; }
    }

    /// <summary>
    /// 一串相邻的工具调用:头部一行「已执行 N 个操作 · run_command ×9 · read_file ×5」,
    /// 运行中露出最近两张卡,这一串结束(模型开始写字或整轮收尾)后整组收成一行;点头部展开全部。
    /// 只有一张卡时不出头部,与单独一张卡看上去没有区别。
    /// </summary>
    internal sealed class ToolGroup
    {
        /// <summary>运行中、未展开时露出最近几张。</summary>
        private const int Peek = 2;

        private readonly ChatPanelView _owner;
        private readonly List<ToolCard> _cards = [];
        private readonly StackPanel _body = new() { Spacing = 0 };
        private readonly Grid _header;
        private readonly Decorator _statusHost = new() { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        private readonly TextBlock _title = new() { Classes = { "toolName" } };
        private readonly TextBlock _detail = new() { Classes = { "toolArgs" }, Margin = new Thickness(8, 0, 8, 0) };
        private readonly Avalonia.Controls.Shapes.Path _chevron;
        private string _statusKey = "";
        private bool _expanded;
        private bool _sealed;

        public Border Root { get; }

        public IReadOnlyList<ToolCard> Cards => _cards;

        public ToolGroup(ChatPanelView owner)
        {
            _owner = owner;
            _header = new Grid
            {
                // 叠层图标 | 标题 | 明细 | 状态 | 箭头 —— 与组内各张卡同一套摆法(类别图标在左、状态在右)
                ColumnDefinitions = [with("Auto,Auto,*,Auto,Auto")],
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                IsVisible = false
            };
            _header.Children.Add(new Decorator
            {
                Child = MakeIcon("AiIcon.layers", "VelaTextTertiary", 12),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            Grid.SetColumn(_statusHost, 3);
            _header.Children.Add(_statusHost);
            Grid.SetColumn(_title, 1);
            _header.Children.Add(_title);
            Grid.SetColumn(_detail, 2);
            _header.Children.Add(_detail);
            _chevron = new Avalonia.Controls.Shapes.Path
            {
                Width = 24,
                Height = 24,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            };
            _chevron[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension("Icon.chevron-right");
            _chevron[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension("VelaTextMuted");
            var chevronBox = new Viewbox { Width = 10, Height = 10, Child = _chevron, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(chevronBox, 4);
            _header.Children.Add(chevronBox);
            _header.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _expanded = !_expanded;
                Refresh();
            };
            var stack = new StackPanel();
            stack.Children.Add(_header);
            stack.Children.Add(_body);
            Root = new Border { Classes = { "toolCard", "toolGroup" }, Child = stack };
        }

        public void Add(ToolCard card)
        {
            card.Group = this;
            _cards.Add(card);
            _body.Children.Add(card.Root);
            Refresh();
        }

        /// <summary>这一串到头了:模型开始写字、插了别的卡,或整轮收尾。之后未展开的组只剩头部一行。</summary>
        public void Seal()
        {
            if (_sealed) return;
            _sealed = true;
            Refresh();
        }

        public void Refresh()
        {
            int count = _cards.Count;
            bool grouped = count >= 2;
            _header.IsVisible = grouped;
            Root.Classes.Set("grouped", grouped);
            _body.Margin = grouped ? new Thickness(17, 2, 0, 0) : new Thickness(0);
            for (int i = 0; i < count; i++)
            {
                _cards[i].Root.IsVisible = !grouped || _expanded || (!_sealed && i >= count - Peek);
            }
            if (!grouped) return;

            bool running = _cards.Any(c => c.IsRunning);
            int failed = _cards.Count(c => c.IsFailed);
            _title.Text = _owner._loc.F(running && !_sealed ? "ToolGroupRunning" : "ToolGroupDone", count);
            var detail = new StringBuilder();
            foreach (IGrouping<string, ToolCard>? group in _cards.GroupBy(c => c.ToolName).OrderByDescending(g => g.Count()).Take(3))
            {
                if (detail.Length > 0) detail.Append(" · ");
                detail.Append(group.Key).Append(" ×").Append(group.Count());
            }
            if (failed > 0) detail.Append(" · ").Append(_owner._loc.F("ToolGroupFailed", failed));
            _detail.Text = detail.ToString();
            ToolTip.SetTip(_detail, string.Join(Environment.NewLine,
                _cards.GroupBy(c => c.ToolName).Select(g => $"{g.Key} ×{g.Count()}")));

            string statusKey = running && !_sealed ? "run" : failed > 0 ? "warn" : "ok";
            if (statusKey != _statusKey)
            {
                _statusKey = statusKey;
                _statusHost.Child = statusKey switch
                {
                    "run" => MakeIcon("Icon.ellipsis", "VelaAccent", 11),
                    "warn" => MakeIcon("Icon.circle-alert", "VelaWarning", 11),
                    _ => MakeIcon("Icon.circle-check", "VelaStatusConnected", 11)
                };
            }
            _chevron.RenderTransform = _expanded ? new RotateTransform(90) : null;
        }
    }

    /// <summary>
    /// 一轮结束后的「工作过程」:思考、工具组与工具之间的过渡话收进一行,默认收起,
    /// 回复里直接露出的只剩最终结论。子任务卡、审批卡、图片不收 —— 它们是结果的一部分。
    /// </summary>
    private sealed class ProcessFold
    {
        private readonly StackPanel _body = new() { Spacing = 4, IsVisible = false };
        private readonly Avalonia.Controls.Shapes.Path _chevron;

        public Border Root { get; }

        public ProcessFold(ChatPanelView owner, int actions, TimeSpan? elapsed)
        {
            var header = new Grid
            {
                ColumnDefinitions = [with("Auto,Auto,*")],
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            _chevron = new Avalonia.Controls.Shapes.Path
            {
                Width = 24,
                Height = 24,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            };
            _chevron[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension("Icon.chevron-right");
            _chevron[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension("VelaTextMuted");
            header.Children.Add(new Viewbox
            {
                Width = 10,
                Height = 10,
                Child = _chevron,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            var icon = new Decorator
            {
                Child = MakeIcon("Icon.list", "VelaTextMuted", 11),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 1);
            header.Children.Add(icon);
            string title = owner._loc.F("ProcessFold", actions)
                + (elapsed is { } span ? " · " + ChatFormatting.Duration(span) : "");
            var titleText = new TextBlock { Classes = { "dim" }, Text = title, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(titleText, 2);
            header.Children.Add(titleText);
            header.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _body.IsVisible = !_body.IsVisible;
                _chevron.RenderTransform = _body.IsVisible ? new RotateTransform(90) : null;
            };
            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(new Border { Classes = { "processBody" }, Child = _body });
            Root = new Border { Classes = { "processFold" }, Child = stack };
        }

        public void Add(Control child) => _body.Children.Add(child);
    }

    internal sealed partial class AssistantBubble
    {
        private ToolGroup? _toolGroup;

        /// <summary>把卡放进当前这一串;中间隔了正文或别的卡就另起一组。</summary>
        private void PlaceToolCard(ToolCard card, string name)
        {
            if (IsStandaloneTool(name))
            {
                SealToolGroup();
                // 传输卡带进度条与取消按钮:整轮收尾时也不收进「工作过程」(见 IsToolElement)
                card.Root.Classes.Add("toolStandalone");
                _stack.Children.Add(card.Root);
                return;
            }
            if (_toolGroup is null || _stack.Children.Count == 0 || !ReferenceEquals(_stack.Children[^1], _toolGroup.Root))
            {
                SealToolGroup();
                _toolGroup = new ToolGroup(_owner);
                _stack.Children.Add(_toolGroup.Root);
            }
            _toolGroup.Add(card);
        }

        private void SealToolGroup()
        {
            _toolGroup?.Seal();
            _toolGroup = null;
        }

        /// <summary>
        /// 整轮收尾时把「过程」收进一行:从第一块过程内容到最后一个工具组为止,
        /// 其间的思考、工具组/卡与过渡正文挪进 <see cref="ProcessFold" />。
        /// 被停下、没有结论正文、或工具调用不到两次时不收 —— 停在哪一步、为什么停,得让人一眼看到。
        /// </summary>
        private void FoldProcess(bool cancelled, TimeSpan? elapsed)
        {
            if (cancelled || _toolCards.Count < 2) return;
            int lastTool = -1;
            for (int i = _stack.Children.Count - 1; i >= 0; i--)
            {
                if (IsToolElement(_stack.Children[i]))
                {
                    lastTool = i;
                    break;
                }
            }
            if (lastTool < 0) return;
            // 最后一个工具之后得有结论正文,否则收起来就什么都看不见了
            bool hasConclusion = false;
            for (int i = lastTool + 1; i < _stack.Children.Count; i++)
            {
                if (_stack.Children[i] is MarkdownRenderer)
                {
                    hasConclusion = true;
                    break;
                }
            }
            if (!hasConclusion) return;

            List<Control> moved = [];
            int insertAt = -1;
            for (int i = 1; i <= lastTool; i++)
            {
                Control child = _stack.Children[i];
                if (IsToolElement(child) || child is MarkdownRenderer || ReferenceEquals(child, _thinking?.Root))
                {
                    if (insertAt < 0) insertAt = i;
                    moved.Add(child);
                }
            }
            if (moved.Count == 0) return;
            var fold = new ProcessFold(_owner, _toolCards.Count, elapsed);
            foreach (Control child in moved)
            {
                _stack.Children.Remove(child);
                fold.Add(child);
            }
            _stack.Children.Insert(insertAt, fold.Root);
        }

        /// <summary>
        /// 运行中整理了一次上下文:在回复里留一道分隔条,写明清了多少、占用从几成降到几成;
        /// 折过摘要的点开能读到摘要原文 —— 模型从此刻起看到的是摘要而不是原文,用户有权知道。
        /// </summary>
        public void AddRunCompaction(RunCompaction compaction)
        {
            int before = compaction.Budget > 0 ? (int)Math.Round(100.0 * compaction.UsedBefore / compaction.Budget) : 0;
            int after = compaction.Budget > 0 ? (int)Math.Round(100.0 * compaction.UsedAfter / compaction.Budget) : 0;
            string title = compaction.FoldedMessages > 0
                ? _owner._loc.F("RunCompactFolded", compaction.FoldedMessages, before, after)
                : _owner._loc.F("RunCompactCleared", compaction.ClearedResults, before, after);
            var collapsible = new Collapsible(title, iconKey: "AiIcon.scissors", iconBrushKey: "VelaAccent");
            if (compaction.Summary is { Length: > 0 } summary) collapsible.SetBody(summary);
            AddCard(new Border { Classes = { "compactionMarker" }, Child = collapsible.Root });
        }

        private static bool IsToolElement(Control child)
            => child is Border border && border.Classes.Contains("toolCard") && !border.Classes.Contains("toolStandalone")
               && (border.Classes.Contains("toolGroup") || border.Classes.Contains("toolItem"));
    }
}
