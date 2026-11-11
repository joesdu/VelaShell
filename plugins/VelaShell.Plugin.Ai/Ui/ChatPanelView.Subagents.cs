using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

public partial class ChatPanelView
{
    private sealed class SubagentRunGroup
    {
        private readonly ChatPanelView _owner;
        private readonly TextBlock _progress = new() { Classes = { "dim" } };
        private readonly StackPanel _rows = new() { Spacing = 4 };
        private readonly Dictionary<string, SubagentRunCard> _cards = [];

        public Border Root { get; }

        public SubagentRunGroup(ChatPanelView owner)
        {
            _owner = owner;
            var stack = new StackPanel { Spacing = 4 };
            Grid heading = owner.BuildCardHeader("Icon.users", "VelaAccent", owner._loc["SubagentRuns"], null);
            Grid.SetColumn(_progress, 2);
            _progress.Margin = new Thickness(8, 0, 0, 0);
            heading.Children.Add(_progress);
            stack.Children.Add(heading);
            stack.Children.Add(_rows);
            Root = new Border { Classes = { "toolCard", "subagentGroup" }, Child = stack };
        }

        public void Update(IReadOnlyList<SubagentSnapshot> snapshots)
        {
            int finished = snapshots.Count(s => s.Status is not ("queued" or "running"));
            _progress.Text = _owner._loc.F("SubagentProgress", finished, snapshots.Count);
            foreach (SubagentSnapshot snapshot in snapshots)
            {
                if (!_cards.TryGetValue(snapshot.SubagentId, out SubagentRunCard? card))
                {
                    card = new SubagentRunCard(_owner);
                    _cards.Add(snapshot.SubagentId, card);
                    _rows.Children.Add(card.Root);
                }
                card.Update(snapshot);
            }
        }
    }

    private sealed class SubagentRunCard
    {
        private readonly ChatPanelView _owner;
        private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, FontWeight = FontWeight.Medium };
        private readonly TextBlock _state = new() { Classes = { "dim" } };
        private readonly TextBlock _metrics = new() { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _model = new() { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _tools = new() { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap };
        private readonly SelectableTextBlock _task = new() { TextWrapping = TextWrapping.Wrap };
        private readonly SelectableTextBlock _errors = new() { Classes = { "mono" }, TextWrapping = TextWrapping.Wrap };
        private readonly Border _output = new();
        private string _lastOutput = "";
        private string? _lastTone;

        public Expander Root { get; }

        public SubagentRunCard(ChatPanelView owner)
        {
            _owner = owner;
            var header = new StackPanel { Spacing = 3 };
            header.Children.Add(_title);
            var metadata = new WrapPanel();
            _state.Margin = new Thickness(0, 0, 8, 0);
            metadata.Children.Add(_state);
            metadata.Children.Add(_metrics);
            header.Children.Add(metadata);
            var details = new StackPanel { Spacing = 6 };
            details.Children.Add(_task);
            details.Children.Add(_model);
            details.Children.Add(_tools);
            details.Children.Add(_errors);
            details.Children.Add(_output);
            _errors[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaError");
            Root = new Expander
            {
                Classes = { "subagentRun" },
                Header = header,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer
                {
                    MaxHeight = 320,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = details
                }
            };
        }

        public void Update(SubagentSnapshot snapshot)
        {
            _title.Text = ChatFormatting.OneLine(
                snapshot.TaskPrompt.Length > 0 ? snapshot.TaskPrompt : snapshot.Role, 160);
            _task.Text = snapshot.TaskPrompt;
            _task.IsVisible = snapshot.TaskPrompt.Length > 0;
            _metrics.Text = $"{snapshot.Role} · {ChatFormatting.Duration(snapshot.Elapsed)} · "
                + _owner._loc.F("SubagentToolCalls", snapshot.ToolsUsed.Sum(t => t.Count));
            string stateKey = snapshot.Status switch
            {
                "queued" => "SubagentQueued",
                "running" => "SubagentRunning",
                "completed" => "SubagentCompleted",
                "failed" => "SubagentFailed",
                "cancelled" => "Cancelled",
                "timed_out" => "SwitchReasonTimeout",
                _ => ""
            };
            _state.Text = stateKey.Length > 0 ? _owner._loc[stateKey] : snapshot.Status;
            if (snapshot.Errors.Count > 0)
                _state.Text += $" · {_owner._loc.F("SubagentErrors", snapshot.Errors.Count)}";
            string tone = snapshot.Status switch
            {
                "failed" or "timed_out" => "VelaError",
                _ when snapshot.Errors.Count > 0 => "VelaWarning",
                "completed" => "VelaStatusConnected",
                "running" => "VelaAccent",
                _ => "VelaTextTertiary"
            };
            if (_lastTone != tone)
            {
                _state[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(tone);
                _lastTone = tone;
            }
            _model.Text = $"{snapshot.SubagentId}"
                + (snapshot.ModelId is { } modelId ? $" · {_owner._loc["Model"]}: {_owner._settings.FindModel(modelId)?.Model ?? modelId}" : "");
            _tools.Text = snapshot.ToolsUsed.Count == 0
                ? _owner._loc.F("SubagentToolCalls", 0)
                : string.Join(Environment.NewLine, snapshot.ToolsUsed.Select(t => $"{t.Name} ×{t.Count}"));
            _errors.Text = string.Join(Environment.NewLine, snapshot.Errors);
            _errors.IsVisible = snapshot.Errors.Count > 0;
            if (_lastOutput != snapshot.Output)
            {
                var segment = new MarkdownSegment(_owner);
                segment.Append(snapshot.Output);
                _output.Child = segment.Host;
                _lastOutput = snapshot.Output;
            }
        }
    }
}
