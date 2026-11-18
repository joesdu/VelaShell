using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 命令工具卡的执行状态:运行中实时计时,结束后标出退出码与耗时;
/// 远端命令转入后台(run_command 短等后仍在跑)时继续跟踪,实时显示耗时与最新输出,并可在卡上直接停止。
/// </summary>
/// <remarks>
/// <para>
/// 跟踪走 <see cref="CommandJobRunScope.PeekAsync" />,不走模型用的 task_status:
/// 后者读到终态会消费「任务结束」通知,界面一刷新,模型就收不到完成提醒了。
/// </para>
/// <para>
/// 卡上显示的永远只是界面状态,不改写 <c>_result</c>:历史与入库保存的仍是模型当时读到的那份结果。
/// </para>
/// </remarks>
public sealed partial class ChatPanelView
{
    internal sealed partial class AssistantBubble
    {
        /// <summary>这一轮的命令任务范围;命令卡转入后台后靠它跟踪。历史回放时为空,不跟踪。</summary>
        public CommandJobRunScope? CommandRun { get; set; }
    }

    internal sealed partial class ToolCard
    {
        /// <summary>后台任务卡下方保留几行最新输出。</summary>
        private const int CommandTailLines = 3;

        /// <summary>单行输出在卡上最多显示几个字。</summary>
        private const int CommandTailChars = 200;

        private static readonly TimeSpan CommandPollInterval = TimeSpan.FromSeconds(1);

        /// <summary>本地命令结果的首两行:退出码,以及(若有)进程耗时。</summary>
        [GeneratedRegex(@"^Local command exit code: (-?\d+)\r?\n(?:Elapsed: ([0-9.]+)s)?", RegexOptions.CultureInvariant)]
        private static partial Regex LocalExitPattern();

        /// <summary>ANSI 的 CSI 序列(颜色、光标)与 OSC 序列(标题等),显示输出尾巴前去掉。</summary>
        [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07", RegexOptions.CultureInvariant)]
        private static partial Regex AnsiPattern();

        private TextBlock? _commandMeta;
        private DispatcherTimer? _commandClock;
        private readonly long _commandCreatedAt = Environment.TickCount64;
        private bool _foregroundRunning;
        private DateTimeOffset _backgroundSince;
        private string _commandTaskId = "";
        private long _commandCursor;
        private bool _commandPolling;
        private TextBlock? _commandTail;
        private Button? _stopCommand;
        private readonly List<string> _tailLines = [];

        /// <summary>这一轮的命令任务范围(由所在气泡在完成时交给卡片)。</summary>
        internal CommandJobRunScope? CommandRun { get; set; }

        /// <summary>界面状态:后台任务仍在跟踪中。</summary>
        internal bool IsTrackingBackgroundCommand { get; private set; }

        /// <summary>卡头右侧的耗时 / 退出码文字(测试用)。</summary>
        internal string? CommandMetaText => _commandMeta?.IsVisible == true ? _commandMeta.Text : null;

        /// <summary>后台任务卡下方的最新输出(测试用)。</summary>
        internal string? CommandTailText => _commandTail?.IsVisible == true ? _commandTail.Text : null;

        private static bool IsCommandTool(string name) => name is "run_command" or "run_local_command";

        private static bool IsTaskTool(string name) => name is "task_status" or "task_wait";

        private void InitCommandStatus(Grid header, int column)
        {
            if (!IsCommandTool(ToolName) && !IsTaskTool(ToolName))
            {
                return;
            }
            _commandMeta = new TextBlock
            {
                Name = "ToolCommandMeta",
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IsVisible = false
            };
            _commandMeta[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
            _commandMeta[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            SetMetaTone("VelaTextTertiary");
            Grid.SetColumn(_commandMeta, column);
            header.Children.Add(_commandMeta);

            if (IsCommandTool(ToolName))
            {
                // 前台命令:从卡片出现开始计时,结果回来时换成确切的退出码与耗时
                _foregroundRunning = true;
                UpdateForegroundClock();
                StartClock();
            }
        }

        /// <summary>
        /// 命令 / 任务工具的结果:按退出码或任务状态设置状态图标与耗时。
        /// 返回 false 表示不是可识别的命令结果(拒绝、参数错误等),交回通用的成功 / 失败判断。
        /// </summary>
        private bool CompleteCommand(string result)
        {
            if (_commandMeta is null)
            {
                return false;
            }
            var wall = TimeSpan.FromMilliseconds(Environment.TickCount64 - _commandCreatedAt);
            _foregroundRunning = false;
            StopClock();

            // 本机命令现在与远端一样返回任务快照(走下面的通用分支);这里只兼容历史记录里的旧纯文本格式
            if (ToolName == "run_local_command" && LocalExitPattern().Match(result) is { Success: true } match)
            {
                int exit = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                TimeSpan elapsed = match.Groups[2].Success
                    && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                        ? TimeSpan.FromSeconds(seconds) : wall;
                ShowExit(exit, elapsed);
                return true;
            }

            if (!CommandJobView.TryParse(result, out CommandJobView job))
            {
                if (IsCommandTool(ToolName))
                {
                    ShowMeta(ChatFormatting.Duration(wall), "VelaTextTertiary");
                }
                else
                {
                    _commandMeta.IsVisible = false;
                }
                return false;
            }
            if (job.IsTerminal)
            {
                ShowTerminal(job.State, job.ExitCode, job.Duration ?? (IsCommandTool(ToolName) ? wall : null));
                return true;
            }
            if (!IsCommandTool(ToolName))
            {
                // task_status / task_wait 读到的还在跑:只标一下已跑多久,跟踪交给发起它的那张命令卡
                ShowMeta(_owner._loc.F("CommandRunning", ChatFormatting.Duration(job.RunningFor)), "VelaAccent");
                SetStatusIcon("Icon.ellipsis", "VelaAccent", _owner._loc["ToolRunning"]);
                return true;
            }
            BeginBackground(job);
            return true;
        }

        /// <summary>一轮结束时调:没等到结果的前台命令把计时定格,不再往上跳。</summary>
        internal void SettleForegroundClock()
        {
            if (!_foregroundRunning)
            {
                return;
            }
            _foregroundRunning = false;
            StopClock();
            UpdateForegroundClock(running: false);
        }

        // ---------- 后台跟踪 ----------

        private void BeginBackground(CommandJobView job)
        {
            _commandTaskId = job.TaskId;
            // 结果里已带的输出先显示,轮询从它之后接着读,同一行不出现两次
            _commandCursor = job.NextCursor;
            _backgroundSince = job.StartedAt ?? job.CreatedAt ?? DateTimeOffset.UtcNow;
            if (CommandRun is null || job.TaskId.Length == 0)
            {
                // 历史回放:当时转入了后台,之后怎样历史里没有,只如实标出来
                ShowMeta(_owner._loc["CommandHistorical"], "VelaTextTertiary");
                ToolTip.SetTip(_commandMeta!, _owner._loc["CommandHistoricalTip"]);
                SetStatusIcon("Icon.circle-alert", "VelaTextTertiary", _owner._loc["CommandHistoricalTip"]);
                return;
            }

            IsTrackingBackgroundCommand = true;
            SetStatusIcon("Icon.ellipsis", "VelaAccent", _owner._loc["CommandBackgroundTip"]);
            ToolTip.SetTip(_commandMeta!, _owner._loc["CommandBackgroundTip"]);
            EnsureBackgroundPanel();
            AppendTail(job.Output);
            UpdateBackgroundClock();
            StartClock();
            _ = PollBackgroundAsync();
        }

        private void EnsureBackgroundPanel()
        {
            if (_commandTail is not null)
            {
                return;
            }
            _commandTail = new TextBlock
            {
                Name = "ToolCommandTail",
                TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                IsVisible = false
            };
            _commandTail[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
            _commandTail[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            _commandTail[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");

            _stopCommand = new Button
            {
                Name = "ToolCommandStop",
                Content = _owner._loc["CommandStop"],
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
            };
            _stopCommand[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
            _stopCommand.Click += async (_, _) => await StopBackgroundAsync();

            var panel = new StackPanel { Spacing = 5, Margin = new Thickness(17, 6, 0, 0) };
            panel.Children.Add(_commandTail);
            panel.Children.Add(_stopCommand);
            ((StackPanel)Root.Child!).Children.Add(panel);
        }

        private async Task PollBackgroundAsync()
        {
            if (_commandPolling || !IsTrackingBackgroundCommand || CommandRun is not { } run)
            {
                return;
            }
            _commandPolling = true;
            try
            {
                CommandJobSnapshot snapshot = await run.PeekAsync(_commandTaskId, _commandCursor);
                if (!IsTrackingBackgroundCommand)
                {
                    return;
                }
                _commandCursor = snapshot.NextCursor;
                AppendTail(snapshot.Output.Select(item => item.Text));
                if (snapshot.IsTerminal)
                {
                    EndBackground();
                    TimeSpan? duration = snapshot.CompletedAt is { } done
                        ? done - (snapshot.StartedAt ?? snapshot.CreatedAt) : null;
                    ShowTerminal(snapshot.State.ToString(), snapshot.ExitCode, duration);
                    Group?.Refresh(); // 分组头部的失败计数要跟上后台任务的结局
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 轮次已收尾(范围释放、任务随之移除)或读不到了:如实标成「已停止跟踪」,不再猜它的结局
                if (IsTrackingBackgroundCommand)
                {
                    EndBackground();
                    ShowMeta(_owner._loc.F("CommandUntracked", ChatFormatting.Duration(DateTimeOffset.UtcNow - _backgroundSince)),
                        "VelaTextTertiary");
                    ToolTip.SetTip(_commandMeta!, _owner._loc["CommandUntrackedTip"]);
                    SetStatusIcon("Icon.circle-alert", "VelaTextTertiary", _owner._loc["CommandUntrackedTip"]);
                }
            }
            finally
            {
                _commandPolling = false;
            }
        }

        private async Task StopBackgroundAsync()
        {
            if (!IsTrackingBackgroundCommand || CommandRun is not { } run || _stopCommand is null)
            {
                return;
            }
            _stopCommand.IsEnabled = false;
            _stopCommand.Content = _owner._loc["CommandStopping"];
            try
            {
                await run.CancelJobAsync(_commandTaskId);
                await PollBackgroundAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (IsTrackingBackgroundCommand)
                {
                    _stopCommand.Content = _owner._loc["CommandStop"];
                    _stopCommand.IsEnabled = true;
                    ToolTip.SetTip(_stopCommand, _owner._loc["CommandStopFailed"]);
                }
            }
        }

        private void EndBackground()
        {
            IsTrackingBackgroundCommand = false;
            StopClock();
            _stopCommand?.IsVisible = false;
            ToolTip.SetTip(_commandMeta!, null);
        }

        private void AppendTail(IEnumerable<string> chunks)
        {
            if (_commandTail is null)
            {
                return;
            }
            foreach (string chunk in chunks)
            {
                string clean = AnsiPattern().Replace(chunk, "");
                foreach (string line in clean.Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r');
                    // 进度条一类用 \r 原地刷新的输出,只留最后一帧
                    int carriage = trimmed.LastIndexOf('\r');
                    if (carriage >= 0)
                    {
                        trimmed = trimmed[(carriage + 1)..];
                    }
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        continue;
                    }
                    _tailLines.Add(Truncate(new string([.. trimmed.Where(c => c == '\t' || !char.IsControl(c))]), CommandTailChars));
                }
            }
            if (_tailLines.Count > CommandTailLines)
            {
                _tailLines.RemoveRange(0, _tailLines.Count - CommandTailLines);
            }
            _commandTail.Text = string.Join("\n", _tailLines);
            _commandTail.IsVisible = _tailLines.Count > 0;
        }

        // ---------- 计时与展示 ----------

        private void StartClock()
        {
            if (_commandClock is not null)
            {
                _commandClock.Start();
                return;
            }
            _commandClock = new DispatcherTimer { Interval = CommandPollInterval };
            _commandClock.Tick += (_, _) =>
            {
                if (_foregroundRunning)
                {
                    UpdateForegroundClock();
                }
                else if (IsTrackingBackgroundCommand)
                {
                    UpdateBackgroundClock();
                    _ = PollBackgroundAsync();
                }
                else
                {
                    StopClock();
                }
            };
            _commandClock.Start();
        }

        private void StopClock() => _commandClock?.Stop();

        private void UpdateForegroundClock(bool running = true)
        {
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _commandCreatedAt);
            ShowMeta(running ? _owner._loc.F("CommandRunning", ChatFormatting.Duration(elapsed)) : ChatFormatting.Duration(elapsed),
                running ? "VelaAccent" : "VelaTextTertiary");
        }

        private void UpdateBackgroundClock() => ShowMeta(
            _owner._loc.F("CommandBackground", ChatFormatting.Duration(DateTimeOffset.UtcNow - _backgroundSince)), "VelaAccent");

        private void ShowExit(int exit, TimeSpan elapsed)
        {
            bool ok = exit == 0;
            ShowMeta(_owner._loc.F("CommandExit", exit, ChatFormatting.Duration(elapsed)), ok ? "VelaTextTertiary" : "VelaWarning");
            SetStatusIcon(ok ? "Icon.circle-check" : "Icon.circle-alert", ok ? "VelaStatusConnected" : "VelaWarning",
                ok ? _owner._loc["ToolDone"] : _owner._loc.F("CommandExit", exit, ChatFormatting.Duration(elapsed)));
            IsFailed = !ok;
        }

        private void ShowTerminal(string state, int? exitCode, TimeSpan? duration)
        {
            string time = duration is { } span && span >= TimeSpan.Zero ? ChatFormatting.Duration(span) : "";
            if (exitCode is { } exit && state is nameof(CommandJobState.Succeeded) or nameof(CommandJobState.Failed))
            {
                ShowExit(exit, duration ?? TimeSpan.Zero);
                if (time.Length == 0)
                {
                    ShowMeta(_owner._loc.F("CommandExit", exit, "-"), exit == 0 ? "VelaTextTertiary" : "VelaWarning");
                }
                return;
            }
            string label = _owner._loc[state switch
            {
                nameof(CommandJobState.Succeeded) => "ToolDone",
                nameof(CommandJobState.Cancelled) => "CommandCancelled",
                nameof(CommandJobState.TimedOut) => "CommandTimedOut",
                nameof(CommandJobState.Failed) => "CommandFailed",
                _ => "CommandOutcomeUnknown"
            }];
            bool ok = state == nameof(CommandJobState.Succeeded);
            ShowMeta(time.Length == 0 ? label : _owner._loc.F("CommandEnded", label, time), ok ? "VelaTextTertiary" : "VelaWarning");
            SetStatusIcon(ok ? "Icon.circle-check" : "Icon.circle-alert", ok ? "VelaStatusConnected" : "VelaWarning", label);
            IsFailed = !ok;
        }

        private void ShowMeta(string text, string toneKey)
        {
            if (_commandMeta is null)
            {
                return;
            }
            _commandMeta.Text = text;
            _commandMeta.IsVisible = text.Length > 0;
            SetMetaTone(toneKey);
        }

        private void SetMetaTone(string toneKey) =>
            _commandMeta![!TextBlock.ForegroundProperty] = new DynamicResourceExtension(toneKey);

        private void SetStatusIcon(string icon, string toneKey, string tip)
        {
            _statusIconHost.Child = MakeIcon(icon, toneKey, 11);
            ToolTip.SetTip(_statusIconHost, tip);
        }
    }

    /// <summary>run_command / task_status / task_wait 结果 JSON 里界面关心的那几项。</summary>
    private readonly record struct CommandJobView(
        string TaskId, string State, int? ExitCode,
        DateTimeOffset? CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
        IReadOnlyList<string> Output, long NextCursor)
    {
        public bool IsTerminal => State is nameof(CommandJobState.Succeeded) or nameof(CommandJobState.Failed)
            or nameof(CommandJobState.Cancelled) or nameof(CommandJobState.TimedOut) or nameof(CommandJobState.Unknown);

        public TimeSpan? Duration => CompletedAt is { } done && (StartedAt ?? CreatedAt) is { } start ? done - start : null;

        public TimeSpan RunningFor => (StartedAt ?? CreatedAt) is { } start
            ? DateTimeOffset.UtcNow - start
            : TimeSpan.Zero;

        public static bool TryParse(string json, out CommandJobView view)
        {
            view = default;
            if (json.Length == 0 || json[0] != '{')
            {
                return false;
            }
            try
            {
                using var document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (Get(root, "state") is not { ValueKind: JsonValueKind.String } state
                    || Get(root, "taskId") is not { ValueKind: JsonValueKind.String } taskId)
                {
                    return false;
                }
                var output = new List<string>();
                if (Get(root, "output") is { ValueKind: JsonValueKind.Array } items)
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        if (Get(item, "Text") is { ValueKind: JsonValueKind.String } text)
                        {
                            output.Add(text.GetString()!);
                        }
                    }
                }
                view = new CommandJobView(taskId.GetString()!, state.GetString()!,
                    Get(root, "exitCode") is { ValueKind: JsonValueKind.Number } exit && exit.TryGetInt32(out int code) ? code : null,
                    Time(root, "CreatedAt"), Time(root, "StartedAt"), Time(root, "CompletedAt"), output,
                    Get(root, "nextCursor") is { ValueKind: JsonValueKind.Number } next && next.TryGetInt64(out long cursor) ? cursor : 0);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static JsonElement? Get(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }
            return null;
        }

        private static DateTimeOffset? Time(JsonElement root, string name) =>
            Get(root, name) is { ValueKind: JsonValueKind.String } value && value.TryGetDateTimeOffset(out DateTimeOffset time)
                ? time : null;
    }
}
