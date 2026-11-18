using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Testing;
using VelaShell.Tests.TestSupport;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>命令工具卡:运行计时、退出码与耗时、转入后台后的实时跟踪与停止。</summary>
public sealed partial class ChatPanelViewUiTests
{
    private static readonly Loc En = new("en");

    [TestMethod]
    public Task LocalCommandCard_TicksWhileRunning_ThenShowsExitCodeAndProcessElapsed() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            var card = new ChatPanelView.ToolCard(panel, "run_local_command", "{\"command\":\"dotnet build\"}");
            window.Content = card.Root;
            StringAssert.StartsWith(card.CommandMetaText, En.F("CommandRunning", ""), "前台命令一出现就开始计时");

            card.Complete("Local command exit code: 0\nElapsed: 3.25s\nstdout:\nok\nstderr:\n");
            Assert.AreEqual(En.F("CommandExit", 0, ChatFormatting.Duration(TimeSpan.FromSeconds(3.25))), card.CommandMetaText,
                "耗时取进程本身的,不含审批等待");
            Assert.IsFalse(card.IsFailed);

            var failed = new ChatPanelView.ToolCard(panel, "run_local_command", "{}");
            failed.Complete("Local command exit code: 2\nElapsed: 0.5s\nstdout:\n\nstderr:\nboom");
            StringAssert.StartsWith(failed.CommandMetaText, En.F("CommandExit", 2, ""));
            Assert.IsTrue(failed.IsFailed, "非零退出码是失败");

            var denied = new ChatPanelView.ToolCard(panel, "run_local_command", "{}");
            denied.Complete("Local command denied. Nothing was started.");
            Assert.IsFalse(denied.CommandMetaText?.StartsWith("exit", StringComparison.Ordinal) ?? false,
                "没跑起来就不该编出退出码");
        }
        finally { panel.Detach(); window.Close(); }
    });

    [TestMethod]
    public Task RemoteCommandCard_FinishedInTheShortWait_ShowsExitAndDuration() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            DateTimeOffset start = DateTimeOffset.UtcNow.AddSeconds(-65);
            var snapshot = new CommandJobSnapshot("t1", "s", CommandJobState.Failed, 1, start, start, start.AddSeconds(65),
                false, true, false, [], 0, 0, false, false, null);
            var card = new ChatPanelView.ToolCard(panel, "run_command", "{\"command\":\"make test\"}");
            card.Complete(AgentToolbox.FormatJob(snapshot));
            Assert.AreEqual(En.F("CommandExit", 1, "1m 5s"), card.CommandMetaText);
            Assert.IsTrue(card.IsFailed);
            Assert.IsFalse(card.IsTrackingBackgroundCommand);

            var cancelled = new ChatPanelView.ToolCard(panel, "task_status", "{\"taskId\":\"t1\"}");
            cancelled.Complete(AgentToolbox.FormatJob(snapshot with { State = CommandJobState.Cancelled, ExitCode = null }));
            Assert.AreEqual(En.F("CommandEnded", En["CommandCancelled"], "1m 5s"), cancelled.CommandMetaText);
        }
        finally { panel.Detach(); window.Close(); }
    });

    [TestMethod]
    public Task BackgroundCommandCard_TracksLiveOutput_UntilItFinishes() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<ExecOutput>? live = null;
        var exec = new ScriptedExec(async (output, token) =>
        {
            live = output;
            output.Report(new(ExecStream.StandardOutput, "\u001b[32mstep 1/3\u001b[0m\n"));
            await finish.Task.WaitAsync(token);
            return new ExecStreamResult(0, 0);
        });
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        try
        {
            string id = registry.Start(scope, "s", "./build.sh");
            CommandJobSnapshot running = await registry.WaitAsync(scope, id, TimeSpan.Zero);
            registry.MarkBackground(scope, id);

            var card = new ChatPanelView.ToolCard(panel, "run_command", "{\"command\":\"./build.sh\"}") { CommandRun = scope };
            window.Content = card.Root;
            card.Complete(AgentToolbox.FormatJob(running));
            Assert.IsTrue(card.IsTrackingBackgroundCommand);
            StringAssert.StartsWith(card.CommandMetaText, En.F("CommandBackground", ""));
            // 短等返回时第一行输出可能还没到,由卡片的轮询补上
            Assert.IsTrue(await WaitForAsync(() => card.CommandTailText == "step 1/3"),
                $"ANSI 颜色码去掉,只显示文字;实际:{card.CommandTailText}");
            Button stop = card.Root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToolCommandStop");
            Assert.IsTrue(stop.IsVisible);

            live!.Report(new(ExecStream.StandardOutput, "step 2/3\rstep 3/3\n"));
            Assert.IsTrue(await WaitForAsync(() => card.CommandTailText == "step 1/3\nstep 3/3"),
                $"新输出要实时出现,\\r 刷新的只留最后一帧;实际:{card.CommandTailText}");

            finish.SetResult();
            Assert.IsTrue(await WaitForAsync(() => !card.IsTrackingBackgroundCommand), "任务结束后停止跟踪");
            StringAssert.StartsWith(card.CommandMetaText, En.F("CommandExit", 0, ""));
            Assert.IsFalse(stop.IsVisible, "结束了就收起停止按钮");
            Assert.IsTrue(scope.HasActiveJobs, "界面跟踪不消费模型的完成通知");
        }
        finally { panel.Detach(); window.Close(); }
    });

    [TestMethod]
    public Task BackgroundCommandCard_StopButtonCancelsTheTask() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        var exec = new ScriptedExec(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new ExecStreamResult(0, 0);
        });
        await using var registry = new CommandJobRegistry(exec);
        await using CommandJobRunScope scope = registry.BeginRun("owner", "run");
        try
        {
            string id = registry.Start(scope, "s", "tail -f app.log");
            CommandJobSnapshot running = await registry.WaitAsync(scope, id, TimeSpan.Zero);
            registry.MarkBackground(scope, id);
            var card = new ChatPanelView.ToolCard(panel, "run_command", "{}") { CommandRun = scope };
            window.Content = card.Root;
            card.Complete(AgentToolbox.FormatJob(running));

            Button stop = card.Root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToolCommandStop");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(await WaitForAsync(() => !card.IsTrackingBackgroundCommand), "停止后任务进入终态");
            CommandJobSnapshot final = await scope.PeekAsync(id);
            Assert.IsTrue(final.IsTerminal);
            Assert.IsTrue(final.CancellationRequested);
            Assert.IsTrue(card.IsFailed, "被停掉的不算成功");
        }
        finally { panel.Detach(); window.Close(); }
    });

    [TestMethod]
    public Task HistoryReplay_OfABackgroundCommand_IsNotTrackedOrTicking() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-3);
            var snapshot = new CommandJobSnapshot("t9", "s", CommandJobState.Running, null, start, start, null,
                false, false, false, [], 0, 0, false, false, null);
            var card = new ChatPanelView.ToolCard(panel, "run_command", "{}");
            card.Complete(AgentToolbox.FormatJob(snapshot));
            Assert.IsFalse(card.IsTrackingBackgroundCommand);
            Assert.AreEqual(En["CommandHistorical"], card.CommandMetaText);

            var unfinished = new ChatPanelView.ToolCard(panel, "run_command", "{}");
            unfinished.SettleForegroundClock();
            string? settled = unfinished.CommandMetaText;
            await Task.Delay(1200);
            await PumpAsync(5);
            Assert.AreEqual(settled, unfinished.CommandMetaText, "没等到结果的命令,一轮结束后计时定格");
        }
        finally { panel.Detach(); window.Close(); }
    });

    /// <summary>只实现普通流式 exec 的远端执行替身。</summary>
    private sealed class ScriptedExec(Func<IProgress<ExecOutput>, CancellationToken, Task<ExecStreamResult>> stream) : IRemoteExecApi
    {
        public Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options,
            IProgress<ExecOutput> output, CancellationToken cancellationToken = default) => stream(output, cancellationToken);

        public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SudoExecResult> StreamSudoAsync(string sessionId, string command, SudoExecOptions? options,
            IProgress<SudoExecEvent> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
