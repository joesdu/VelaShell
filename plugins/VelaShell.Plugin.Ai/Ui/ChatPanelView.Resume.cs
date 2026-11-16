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

    /// <summary>删掉某个会话的检查点(会话被删除或截断时);写不了盘只记日志。</summary>
    private void DeleteCheckpoint(string conversationId, bool forget = false)
    {
        try
        {
            if (forget) Checkpoints.Forget(conversationId);
            else Checkpoints.Delete(conversationId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _context.Log.Warn($"Deleting run checkpoint failed: {ex.Message}");
        }
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

    /// <summary>续跑提示词(见 <see cref="RunCheckpointStore.BuildResumePrompt" />,面板与 IM 共用)。</summary>
    internal static string BuildResumePrompt(RunCheckpoint checkpoint, string earlierSteps)
        => RunCheckpointStore.BuildResumePrompt(checkpoint, earlierSteps);
}
