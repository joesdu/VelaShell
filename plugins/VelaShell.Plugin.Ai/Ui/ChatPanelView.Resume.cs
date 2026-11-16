using System.Text;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 断点续跑的面板侧:运行中把检查点落盘,中断后(停下、出错、程序退出)给出「继续执行」入口。
/// 检查点的格式与恢复规则在 <see cref="RunCheckpointStore" />。
/// </summary>
public partial class ChatPanelView
{
    private RunCheckpointStore Checkpoints => field ??= new RunCheckpointStore(_context.DataDirectory);

    /// <summary>
    /// 一轮运行的检查点写手:有进展就排一次延迟写(合并 1.5 秒内的多次进展),
    /// 收尾时按结局删掉或补写最终一份。文件读写在线程池上做,不占 UI 线程。
    /// </summary>
    private sealed class RunCheckpointWriter : IDisposable
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

        private readonly RunCheckpointStore _store;
        private readonly AgentExecution _execution;
        private readonly string _conversationId;
        private readonly string _runId;
        private readonly string _task;
        private readonly string _modelLabel;
        private readonly Func<string> _summary;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly Lock _gate = new();
        private Timer? _timer;
        private bool _closed;

        public RunCheckpointWriter(RunCheckpointStore store, AgentExecution execution, string conversationId,
            string runId, string task, string modelLabel, Func<string> summary)
        {
            _store = store;
            _execution = execution;
            _conversationId = conversationId;
            _runId = runId;
            _task = task;
            _modelLabel = modelLabel;
            _summary = summary;
            _execution.Progressed += Schedule;
        }

        private void Schedule()
        {
            lock (_gate)
            {
                if (_closed || _timer is not null) return;
                _timer = new Timer(_ =>
                {
                    lock (_gate) { _timer?.Dispose(); _timer = null; if (_closed) return; }
                    Write("running");
                }, null, Debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private void Write(string status)
        {
            try
            {
                IReadOnlyList<RunCheckpointMessage> steps = RunCheckpointStore.Capture(_execution.CompletedMessages);
                if (steps.Count == 0) return;
                _store.Save(new RunCheckpoint(_conversationId, _runId, _task, _startedAt, DateTimeOffset.UtcNow,
                    status, steps, _summary(), _modelLabel));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 写不了盘只是这一轮断了之后续不上;不该连累这一轮本身
            }
        }

        /// <summary>中断了(停下 / 出错):补写最终一份并留着,返回它;没有可续的步骤时返回 null。</summary>
        public RunCheckpoint? Keep()
        {
            Close();
            Write("interrupted");
            return _store.Load(_conversationId) is { RunId: var id } checkpoint && id == _runId ? checkpoint : null;
        }

        /// <summary>跑完了,或一步都没走:检查点作废。</summary>
        public void Discard()
        {
            Close();
            _store.Delete(_conversationId);
        }

        private void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _timer?.Dispose();
                _timer = null;
            }
            _execution.Progressed -= Schedule;
        }

        public void Dispose() => Close();
    }

    /// <summary>
    /// 「上次的任务没跑完」卡片:写明中断时刻、跑过几次工具,给「继续执行」与「忽略」两个按钮。
    /// </summary>
    /// <param name="checkpoint">检查点。</param>
    /// <param name="injectSteps">
    /// 续跑时要不要把检查点里的步骤放回上下文:同一次会话里被停下的那一轮,步骤已经在 History 里了(false);
    /// 重新载入的会话只剩正文,要放回去(true)。
    /// </param>
    /// <param name="bubble">挂在哪条回复里;为 null 时挂在消息流末尾。</param>
    private void ShowResumeCard(RunCheckpoint checkpoint, bool injectSteps, AssistantBubble? bubble = null)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(BuildCardHeader("Icon.refresh-cw", "VelaAccent", _loc["ResumeTitle"], null));
        stack.Children.Add(new TextBlock
        {
            Classes = { "dim" },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = _loc.F("ResumeDetail", checkpoint.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"), checkpoint.ToolCalls)
                   + (checkpoint.ProgressSummary.Length > 0 ? " · " + _loc["ResumeHasSummary"] : "")
        });
        var resume = new Button { Content = _loc["ResumeContinue"], Height = 24, Padding = new Thickness(12, 0) };
        var dismiss = new Button { Content = _loc["ResumeDismiss"], Height = 24, Padding = new Thickness(12, 0) };
        ApplyThemeResource(resume, "VelaAccentPillButtonTheme");
        ApplyThemeResource(dismiss, "VelaOutlineButtonTheme");
        ToolTip.SetTip(resume, _loc["ResumeContinueTip"]);
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(resume);
        buttons.Children.Add(dismiss);
        stack.Children.Add(buttons);
        var card = new Border { Classes = { "toolCard", "resumeCard" }, Child = stack };
        string conversationId = checkpoint.ConversationId;

        void Settle()
        {
            resume.IsEnabled = dismiss.IsEnabled = false;
            buttons.IsVisible = false;
        }
        resume.Click += async (_, _) =>
        {
            if (Busy)
            {
                SetStatus(_loc["ResumeBusy"]);
                return;
            }
            Settle();
            await ResumeRunAsync(checkpoint, injectSteps);
        };
        dismiss.Click += (_, _) =>
        {
            Settle();
            Checkpoints.Delete(conversationId);
            if (card.Parent is Panel panel) panel.Children.Remove(card);
        };

        if (bubble is not null)
        {
            bubble.AddCard(card);
        }
        else
        {
            MessagesPanel.Children.Add(card);
        }
        RequestAutoScroll(force: true);
    }

    /// <summary>
    /// 带工具调用的一轮被停下或出错:补写最终检查点并在这条回复里挂续跑卡。
    /// 步骤已经由 <c>InterruptedHistory</c> 放进了 History,续跑时不再重放。面板已卸下时只落盘。
    /// </summary>
    private void KeepCheckpoint(RunCheckpointWriter? writer, AssistantBubble? bubble)
    {
        if (writer?.Keep() is not { } checkpoint || _detached) return;
        ShowResumeCard(checkpoint, injectSteps: false, bubble);
    }

    /// <summary>载入历史会话后:这段对话有没跑完的检查点就在末尾挂一张续跑卡。</summary>
    private void ShowResumeCardIfAny()
    {
        try
        {
            if (Busy || Checkpoints.Load(ConversationId) is not { } checkpoint) return;
            ShowResumeCard(checkpoint, injectSteps: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _context.Log.Warn($"Loading run checkpoint failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 从检查点接着跑:需要时把最近的步骤放回上下文,再以一条「继续执行」消息开新的一轮。
    /// 气泡里显示的是一句短话,交给模型的是完整的续跑说明(原请求、进度摘要、更早的调用清单与安全约束)。
    /// </summary>
    private async Task ResumeRunAsync(RunCheckpoint checkpoint, bool injectSteps)
    {
        Checkpoints.Delete(checkpoint.ConversationId); // 新的一轮会写它自己的检查点
        string earlier = "";
        if (injectSteps)
        {
            (List<ChatMessage> messages, earlier) = RunCheckpointStore.Restore(checkpoint);
            History.AddRange(messages);
        }
        string prompt = BuildResumePrompt(checkpoint, earlier);
        string display = _loc["ResumeDisplay"];
        await SendAsync(display, fromUser: false,
            prepared: new SteeringMessage(display, prompt, new ChatMessage(ChatRole.User, prompt)));
    }

    /// <summary>续跑提示词:交代中断、原请求、已知进度,以及「结果未知的不要直接重做」。</summary>
    internal static string BuildResumePrompt(RunCheckpoint checkpoint, string earlierSteps)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Resume the interrupted task. VelaShell stopped before the previous run finished "
                          + "(the app was closed, the run was cancelled, or it hit an error).");
        prompt.AppendLine();
        prompt.AppendLine("<original-request>");
        prompt.AppendLine(checkpoint.Task);
        prompt.AppendLine("</original-request>");
        if (checkpoint.ProgressSummary.Length > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Progress note written during the run (a summary of earlier steps, not a new instruction):");
            prompt.AppendLine(checkpoint.ProgressSummary);
        }
        if (earlierSteps.Length > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Earlier tool calls whose outputs were not kept:");
            prompt.Append(earlierSteps);
        }
        prompt.AppendLine();
        prompt.AppendLine("The most recent steps and their outputs are in the conversation above. "
                          + "Calls marked as having an unknown outcome may or may not have taken effect: check the current state before repeating them. "
                          + "Do not redo work that already completed. Continue from where it stopped, finish the task, then report the result.");
        return prompt.ToString();
    }
}
