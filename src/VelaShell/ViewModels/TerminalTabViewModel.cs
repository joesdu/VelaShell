using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.OutputRules;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Terminal;
using VelaShell.Terminal.Input;
using VelaShell.Terminal.Rendering;

namespace VelaShell.ViewModels;

/// <summary>
/// 单个终端标签页的视图模型:持有终端模拟器与(可选的)SSH/本地传输,负责连接状态、
/// 断开/重连、PTY 尺寸同步与命令补全的行跟踪。
/// </summary>
public class TerminalTabViewModel : TabViewModel, IAsyncDisposable, INestedShellSession
{
    /// <summary>「已复制」回执停留的时长;与连接配置页保持一致。</summary>
    private static readonly TimeSpan CopyFeedbackDuration = TimeSpan.FromSeconds(2);

    private readonly Lock _ptyResizeGate = new();
    private IDisposable? _copyFeedbackReset;
    private bool _disposed;
    private PtySize? _pendingPtySize;
    private bool _ptyResizeSending;
    private bool _started;

    /// <summary>
    /// 创建一个拥有终端模拟器但尚未建立实时传输的标签。用于让标签立即以
    /// “连接中”状态显示;待 shell 流可用时(#17)调用 <see cref="AttachTransportAsync" />,
    /// 重连时同样调用它就地重连(#19)。
    /// </summary>
    public TerminalTabViewModel(ITerminalEmulator terminalEmulator)
    {
        TerminalEmulator =
            terminalEmulator ?? throw new ArgumentNullException(nameof(terminalEmulator));
        Title = Strings.NewTab;
        ConnectionStatus = SessionStatus.Disconnected;

        // 保持远程 PTY 尺寸与本地终端网格同步。这绑定在模拟器而非传输上,
        // 因此重连后依然有效。
        TerminalEmulator.PtySizeChanged += OnPtySizeChanged;

        // 命令补全(plan.md #16):旁路跟踪用户键入的命令行;Enter 提交时做回显校验
        // (密码输入无回显,不入历史)后向宿主上报。必须订阅 TypedInput 而非 UserInput:
        // 后者还承载终端的协议自动应答(ESC 开头),会把跟踪器永久打进未知态。
        TerminalEmulator.TypedInput += OnUserInputForTracker;
        InputTracker.CommandSubmitted += OnTrackedCommandSubmitted;
        InputTracker.UnknownLineSubmitted += OnUnknownLineSubmitted;

        // OSC 7:shell 上报当前工作目录 → 供「文件浏览器跟随终端目录」用。仅在 cwd 真正变化时向外播报。
        TerminalEmulator.WorkingDirectoryChanged += OnEmulatorWorkingDirectoryChanged;

        // 输出规则的自动应答:命中判定(只在主屏、只看远端输出、冷却与上限)都在终端控件里做完了,
        // 这里只管发 —— 与快捷命令同一条程序注入路径,同步输入频道转不转由规则决定。
        TerminalEmulator.OutputTriggerFired += OnOutputTriggerFired;

        // 侧栏菜单的「重跑」(OSC 133 命令块):检查都在控件里做完了,发送交给这里 ——
        // 同步输入的转发与「程序注入」两个开关都在本视图模型上。
        if (TerminalEmulator is VelaTerminalControl rerunSource)
        {
            rerunSource.CommandRerunRequested += command => TryRerunCommand(command);
        }

        // 工具栏快捷操作:拆除传输但保留标签,
        // 或请求宿主就地重连(#19 流程)。
        DisconnectCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                UserRequestedDisconnect = true;
                await DetachTransportAsync().ConfigureAwait(true);
                MarkDisconnected();
            },
            this.WhenAnyValue(x => x.IsConnected)
        );
        ReconnectCommand = ReactiveCommand.Create(
            RequestReconnect,
            this.WhenAnyValue(x => x.IsConnected, connected => !connected)
        );

        // 标签页内失败/断开覆盖层(设计 yxjmg)的“关闭标签页”按钮。
        CloseTabCommand = ReactiveCommand.Create(() =>
            CloseRequested?.Invoke(this, EventArgs.Empty)
        );

        // 同步输入横条(标签右键菜单 → 同步输入)的三个动作。
        ToggleSyncPauseCommand = ReactiveCommand.Create(() =>
        {
            IsSyncPaused = !IsSyncPaused;
        });
        LeaveSyncChannelCommand = ReactiveCommand.Create(LeaveSyncChannel);
        CloseSyncChannelCommand = ReactiveCommand.Create(() =>
        {
            if (SyncChannel is { } channel)
            {
                SyncChannelCloseRequested?.Invoke(channel);
            }
        });

        CopyErrorCommand = ReactiveCommand.CreateFromTask(
            CopyErrorAsync,
            this.WhenAnyValue(x => x.ConnectionError, error => !string.IsNullOrWhiteSpace(error))
        );

        AllowAlgorithmsCommand = ReactiveCommand.Create(
            () =>
            {
                if (AlgorithmRemedy is not null && ConnectionStatus == SessionStatus.Disconnected)
                {
                    AllowAlgorithmsRequested?.Invoke(this, EventArgs.Empty);
                }
            },
            this.WhenAnyValue(x => x.AlgorithmRemedy, x => x.IsConnected, (remedy, connected) => remedy is not null && !connected)
        );
    }

    /// <summary>创建一个标签并立即挂载实时传输(已建立连接的场景)。</summary>
    public TerminalTabViewModel(ITerminalEmulator terminalEmulator, IShellStreamWrapper shellStream)
        // 新标签上没有旧桥可拆,所以这里直接挂 —— 不需要（构造函数里也无法）await。
        : this(terminalEmulator) => AttachTransportCore(shellStream ?? throw new ArgumentNullException(nameof(shellStream)));

    /// <summary>该标签所属会话的唯一标识,用于与宿主的会话管理关联。</summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// 本标签的 SFTP 面板开关状态(标签生命周期内记忆,含断线重连):null 表示尚未
    /// 决定过(首次连接时取设置「连接后自动打开文件浏览器」)。用户在该标签上
    /// 打开/关闭面板时由宿主回写,切回本标签按此恢复,与其他标签互不影响。
    /// </summary>
    public bool? FileBrowserOpen { get; set; }

    /// <summary>
    /// 认证后先 <c>cd</c> 进去的远端目录,优先于配置里的初始目录;文件栏「在此处打开终端」开出来的标签才有,其余为 null。
    /// 只挂在标签上、不写进配置(那份配置是别的标签与文件标签共用的);重连照样 cd 回这里 —— 这个标签就是为这个目录开的。
    /// </summary>
    public string? StartupDirectoryOverride { get; set; }

    /// <summary>
    /// 本标签页的资源面板数据(悬停 > 400ms 时显示,§11)。
    /// 握手完成后才赋值,而标签 ToolTip.Tip 在标签创建时就已绑定到它,
    /// 必须发变更通知,否则 Tip 停留在 null,悬浮面板永远不弹。
    /// </summary>
    public ResourceMonitorViewModel? ResourceMonitor
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>本标签连接所用的配置,用于就地重连(#19)。</summary>
    public SessionProfile? Profile { get; set; }

    /// <summary>
    /// 该连接的稳定标识色(标签页色条与 SFTP 面板同色联动,防多标签误操作)。
    /// 本地终端/无配置标签返回透明。Profile 在标签创建时就已赋值,绑定一次性读取即可。
    /// </summary>
    public Avalonia.Media.IBrush ConnectionAccentBrush =>
        Profile is { } profile
            ? ConnectionAccent.BrushForProfile(profile)
            : Avalonia.Media.Brushes.Transparent;

    /// <summary>
    /// 插件协议终端(串口 / Telnet …)由插件自报的标签页图标;宿主内建协议为 null。
    /// 由宿主在建标签时从 <c>ProtocolDescriptor.Icon</c> 取来塞进这里。
    /// </summary>
    public PluginSdk.PluginIcon? PluginTabIcon { get; set; }

    /// <summary>
    /// 标签页上的协议图标(SSH / 插件协议终端)。本地终端无配置,返回 null —— 标签不画图标,
    /// 这本身就是「这不是一条远程会话」的信号。
    /// </summary>
    public TabIcon? TabIcon => ConnectionIcon.ForSession(Profile, PluginTabIcon);

    // ---- 同步输入频道(标签右键菜单 → 同步输入,对等转发见 SyncInputCoordinator) ----

    /// <summary>所属同步输入频道;null = 未加入。经 <see cref="JoinSyncChannel" /> / <see cref="LeaveSyncChannel" /> 变更。</summary>
    public SyncInputChannel? SyncChannel
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(SyncChannelLetter));
            this.RaisePropertyChanged(nameof(SyncChannelBrush));
            this.RaisePropertyChanged(nameof(IsInSyncChannel));
        }
    }

    /// <summary>是否已加入某个同步输入频道(标签头字母与终端上方横条的可见性)。</summary>
    public bool IsInSyncChannel => SyncChannel is not null;

    /// <summary>频道字母(A/B/C/D),未加入频道时为空串。</summary>
    public string SyncChannelLetter => SyncChannel?.ToString() ?? string.Empty;

    /// <summary>频道标识色,未加入频道时透明。</summary>
    public Avalonia.Media.IBrush SyncChannelBrush =>
        SyncChannel is { } channel
            ? SyncInputChannels.BrushFor(channel)
            : Avalonia.Media.Brushes.Transparent;

    /// <summary>true = 本标签的同步输入已暂停:既不向频道发送,也不接收频道输入。</summary>
    public bool IsSyncPaused
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>暂停/恢复本标签的同步输入(横条“暂停”按钮)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleSyncPauseCommand { get; }

    /// <summary>让本标签退出频道(横条“离开频道”按钮与关闭钮)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> LeaveSyncChannelCommand { get; }

    /// <summary>请求关闭整个频道:所有成员标签一并退出(横条“关闭频道”按钮)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseSyncChannelCommand { get; }

    /// <summary>“关闭频道”触发,由 SyncInputCoordinator 让频道内全部标签退出。</summary>
    public event Action<SyncInputChannel>? SyncChannelCloseRequested;

    /// <summary>加入频道(已在其他频道时直接改挂新频道),并清除暂停态。</summary>
    public void JoinSyncChannel(SyncInputChannel channel)
    {
        IsSyncPaused = false;
        SyncChannel = channel;
    }

    /// <summary>退出当前频道并清除暂停态(未加入时为空操作)。</summary>
    public void LeaveSyncChannel()
    {
        SyncChannel = null;
        IsSyncPaused = false;
    }

    /// <summary>
    /// 把同频道其他标签的用户输入直写本标签 PTY。走桥的 SendRaw 而非终端控件的
    /// WriteInput:不触发本标签的 TypedInput(防转发回环),也不进入命令补全的行
    /// 跟踪——智能建议弹层只属于用户正在键入的焦点标签。以 IsConnected 为闸门,
    /// 关闭中的标签(Dispose 先复位 IsConnected)不会被写入。
    /// </summary>
    public void WriteSyncInput(byte[] data)
    {
        if (!IsConnected)
        {
            return;
        }
        Interlocked.Increment(ref _userInputs);
        // 输出触发器要知道「这一行有人在敲」:转来的字被对端回显到光标行上,不是对端在提问。
        (TerminalEmulator as VelaTerminalControl)?.NoteForwardedInput(data);
        Bridge?.SendRaw(data);
    }

    /// <summary>true = 当前的程序化写入不转发到同步频道(快捷命令自带多目标分发)。</summary>
    public bool IsSyncForwardSuppressed { get; private set; }

    /// <summary>本标签正在键入的命令行跟踪器(命令补全弹层的数据入口,见视图侧)。</summary>
    public TerminalInputTracker InputTracker { get; } = new();

    /// <summary>补全建议提供器(宿主 MainWindowViewModel 注入;null = 补全不可用)。</summary>
    public CommandSuggestionProvider? SuggestionProvider { get; set; }

    /// <summary>
    /// 补全里选中一条带 <c>{{变量}}</c> 的快捷命令时,把它换成要写进命令行的正文:
    /// 弹与快捷命令区同一个询问框(预填上一次的值),取消返回 null(一个字节都不写)。
    /// 宿主注入;null 时候选按原文插入(与引入占位之前一致)。
    /// </summary>
    public Func<QuickCommand, Task<string?>>? QuickCommandTextResolver { get; set; }

    /// <summary>用户在本标签提交了一条通过回显校验的命令(宿主记入全局命令历史)。</summary>
    public event Action<string>? CommandLineSubmitted;

    private void OnUserInputForTracker(byte[] data)
    {
        Interlocked.Increment(ref _userInputs);
        // 跟在终端后面走:编码是状态栏上可以随时热切的,而跟踪器解的正是终端刚编出去的
        // 那串字节。在这里同步而不是在切换处推一把,是因为切换点有好几个
        // (状态栏热切、设置热更新、重连重建标签),漏掉任何一个都会静默解成乱码。
        InputTracker.UseEncoding(TerminalEmulator.SessionEncoding);
        InputTracker.Process(data);
        // 每键路径:先查开关再拼实参,否则关着诊断每键也分配 3 个字符串。
        if (SuggestDiag.IsEnabled)
        {
            SuggestDiag.Log(
                "typed",
                $"""
                bytes=[{Convert.ToHexString(data)}] input="{InputTracker.CurrentInput ?? "<unknown>"}"
                """
            );
        }
    }

    private void OnTrackedCommandSubmitted(string command)
    {
        // 回显校验:所键入的文本应已被 shell 回显到屏上;密码提示符不回显 → 不记录,
        // 防止口令进历史。注意桥接层的输出是按帧合并 Feed 的——Enter 瞬间最后几个
        // 字符的回显可能还在队列里,同步校验失败时延迟 200ms 后在整个可视区做二次
        // 校验(此时命令行可能已随输出上移,故扫全屏而非只看光标行)。
        if (TerminalEmulator is not VelaTerminalControl control)
        {
            return;
        }
        if (CursorLineContains(control, command))
        {
            CommandLineSubmitted?.Invoke(command);
            return;
        }
        DispatcherTimer.RunOnce(
            () =>
            {
                if (!_disposed && ScreenContains(control, command))
                {
                    CommandLineSubmitted?.Invoke(command);
                }
            },
            TimeSpan.FromMilliseconds(200)
        );
    }

    private void OnUnknownLineSubmitted()
    {
        // 行内容本地不可知(用户按过方向键/Tab 等):改从屏幕的光标行提取命令——
        // 提示符之后的文本就是 shell 将执行的整行。读取发生在 Enter 的瞬间,
        // 先于命令输出刷屏;密码提示行没有提示符结尾标记,天然不会被提取。
        if (TerminalEmulator is not VelaTerminalControl control)
        {
            return;
        }
        try
        {
            string? command = ExtractCommandAfterPrompt(control.GetBufferLine(control.CursorRow));
            if (!string.IsNullOrWhiteSpace(command))
            {
                CommandLineSubmitted?.Invoke(command);
            }
        }
        catch
        {
            // 读缓冲失败时宁可漏记不误记。
        }
    }

    /// <summary>
    /// 从提示符行提取命令:取最早出现的提示符结尾标记("$ "/"# "/"❯ "/"% ")之后的文本。
    /// 识别不了的提示符样式(无标记)返回 null——宁可漏记不误记。
    /// </summary>
    internal static string? ExtractCommandAfterPrompt(string line)
    {
        int best = -1;
        foreach (string marker in (ReadOnlySpan<string>)["$ ", "# ", "❯ ", "% "])
        {
            int index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }
        if (best < 0)
        {
            return null;
        }
        string command = line[(best + 2)..].Trim();
        return command.Length == 0 ? null : command;
    }

    private static bool CursorLineContains(VelaTerminalControl control, string command)
    {
        try
        {
            return control
                .GetBufferLine(control.CursorRow)
                .Contains(command, StringComparison.Ordinal);
        }
        catch
        {
            // 读缓冲失败(极端竞态)时宁可漏记不误记。
            return false;
        }
    }

    private static bool ScreenContains(VelaTerminalControl control, string command)
    {
        try
        {
            for (int row = 0; row < control.Rows; row++)
            {
                if (control.GetBufferLine(row).Contains(command, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 同上:宁可漏记不误记。
        }
        return false;
    }

    /// <summary>
    /// 本地终端标签(§12 P1-1)对应的 shell;null = SSH 会话。重开(Enter/Ctrl+R)
    /// 用它重新拉起本地进程。
    /// </summary>
    public LocalShellInfo? LocalShell { get; set; }

    /// <summary>
    /// true = 本次断开由用户主动触发(断开按钮),自动重连(设置 → 常规)不介入;
    /// 重新挂载传输时复位。
    /// </summary>
    public bool UserRequestedDisconnect { get; private set; }

    /// <summary>
    /// true = 本次断开是远端 shell 自己退出的(在远端敲了 <c>exit</c> / <c>logout</c>),
    /// 自动重连不介入;重新挂载传输时复位。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="UserRequestedDisconnect" /> 是同一件事的两个入口:一个是点了断开按钮,
    /// 一个是在远端敲了 exit —— 都是用户说「这条会话到此为止」。之前只认前者,
    /// 于是 exit 之后标签自己又连回来,用户按常规办法根本退不掉(#383)。
    /// 两个标志分开留是因为它们的复位时机与判定来源不同,合并会含糊掉「谁说的」。
    /// </remarks>
    public bool RemoteShellExited { get; private set; }

    /// <summary>
    /// true = 上一次重连败在主机指纹上(用户在弹窗里拒绝、变更被阻断,或自动重连时没法问人),
    /// 自动重连不介入 —— 再试多少次都一样,只有用户手动重连、在弹窗里核对过才可能过。
    /// 手动重连开始时、重新挂载传输时复位。
    /// </summary>
    public bool HostKeyRejected { get; private set; }

    /// <summary>认证需要人工处理时暂停后台重连;只在成功挂载新传输时清除,不持久化。</summary>
    public bool AuthenticationRequiresUserAction { get; private set; }

    /// <summary>本标签在状态栏显示的连接摘要,例如 "SSH • root@host:22"。</summary>
    public string ConnectionSummary { get; init; } = string.Empty;

    /// <summary>本会话声明的终端模拟类型。</summary>
    public string TerminalTypeName { get; init; } = "xterm-256color";

    /// <summary>
    /// 本会话使用的字符编码。可写:状态栏的编码菜单能当场把它切掉(只作用于本会话)。
    /// </summary>
    public string EncodingName { get; set; } = "UTF-8";

    /// <summary>本标签持有的终端模拟器,跨重连保持不变(拥有滚动缓冲区)。</summary>
    public ITerminalEmulator TerminalEmulator { get; }

    /// <summary>
    /// 本会话远端 shell 的种类(<see cref="RemoteShellProbe" /> 的结论,握手时写入)。
    /// 本地终端、以及探不出来的会话都是 <see cref="RemoteShellKind.Unknown" />。
    /// </summary>
    /// <remarks>
    /// <see cref="SendSilentCommand" /> 靠它决定给注入行接哪一段摘历史前缀,或者不接 ——
    /// zsh 有它自己的一段,bash 那段在 fish 里会让**整行**解析失败,理由见 <see cref="ShellHistoryScrub.For" />。
    /// </remarks>
    public RemoteShellKind RemoteShellKind { get; set; }

    /// <summary>该终端 shell 当前工作目录(经 OSC 7 / 633 / 1337 / 9;9 上报);未知/未上报时为 null。</summary>
    public string? TerminalWorkingDirectory { get; private set; }

    /// <summary>终端 shell 工作目录【变化】时触发(去重:同值不重复触发)。参数为绝对路径。</summary>
    public event Action<string>? WorkingDirectoryChanged;

    private void OnEmulatorWorkingDirectoryChanged(string path)
    {
        // 去重之前先记一笔:嵌套 shell 的补注(NestedShellFollower)要知道的是"回车之后有没有上报",
        // 而不是"目录变没变" —— 装了钩子的 shell 在原地回车也会报。
        Interlocked.Increment(ref _workingDirectoryReports);

        // bash/zsh/sh 那几段钩子挂在提示符上,每次回车都会发一遍(即使 cwd 没变;只有 fish 那段
        // 是挂在 PWD 变量上、真变了才响)。这里只在真正变化时对外播报,避免把用户在文件浏览器里
        // 的手动切换在每次回车时都拽回终端目录(仅当终端 cd 到新目录才同步)。
        if (string.IsNullOrEmpty(path) || string.Equals(path, TerminalWorkingDirectory, StringComparison.Ordinal))
        {
            return;
        }
        TerminalWorkingDirectory = path;
        WorkingDirectoryChanged?.Invoke(path);
    }

    // ---- 嵌套 shell 的目录跟随(#637,见 NestedShellFollower) ----

    private long _workingDirectoryReports;
    private long _userInputs;
    private NestedShellFollower? _nestedShellFollower;

    /// <summary>
    /// 本连接注入过目录上报钩子之后由宿主调用:此后每次回车,前台若换成了一个还没装钩子的
    /// 交互式 shell(<c>su - node</c>、<c>sudo -i</c>),就给它补注一次。断开 / 重连时自动作废,重连后由宿主重新挂上。
    /// </summary>
    /// <param name="probe">走本连接 exec 通道的前台探针(见 <see cref="ForegroundShellProbe.DetectAsync" />)。</param>
    /// <param name="enabled">「上报终端工作目录」此刻是否开着。</param>
    internal void EnableNestedShellFollow(Func<CancellationToken, Task<ForegroundShell?>> probe, Func<bool> enabled)
    {
        _nestedShellFollower?.Dispose();
        _nestedShellFollower = new(this, probe, enabled);
    }

    /// <summary>停掉嵌套 shell 的补注(断开、重连、关标签,或这次连接没有注入钩子)。</summary>
    internal void DisableNestedShellFollow()
    {
        _nestedShellFollower?.Dispose();
        _nestedShellFollower = null;
    }

    private void OnBridgeLineSubmitted() => _nestedShellFollower?.OnLineSubmitted();

    long INestedShellSession.WorkingDirectoryReports => Interlocked.Read(ref _workingDirectoryReports);

    long INestedShellSession.UserInputs => Interlocked.Read(ref _userInputs);

    // 没有桥 = 断开了:报"刚有输出",于是永远等不到静默,这一轮自然作废。
    DateTime INestedShellSession.LastOutputUtc => Bridge?.LastOutputUtc ?? DateTime.UtcNow;

    long INestedShellSession.InputGeneration => Bridge?.InputGeneration ?? -1;

    bool INestedShellSession.IsAlternateScreen => TerminalEmulator is VelaTerminalControl { IsAlternateScreenActive: true };

    /// <summary>
    /// 补注 <paramref name="kind" /> 那一段钩子:与连接时的注入同一行(安装藏掉、末尾那次上报留着),
    /// 摘历史前缀也按<b>这个</b> shell 的种类接,而不是登录 shell 的。
    /// </summary>
    bool INestedShellSession.TryInjectShellIntegration(RemoteShellKind kind, long expectedInputGeneration)
    {
        ShellIntegrationInjection injection = ShellIntegrationScript.Build(kind);
        if (!IsConnected || Bridge is not { } bridge || injection.IsEmpty || injection.Sentinel.Length == 0)
        {
            return false;
        }
        System.Text.Encoding encoding = TerminalEmulator.SessionEncoding;
        return bridge.TrySendInjection(
            encoding.GetBytes(injection.Sentinel),
            encoding.GetBytes(" " + injection.CommandLine + "\n"),
            expectedInputGeneration);
    }

    /// <summary>当前挂载的 shell 数据流;未连接或断开后为 null。</summary>
    public IShellStreamWrapper? ShellStream { get; private set; }

    /// <summary>连接 shell 流与终端模拟器的桥接器;未连接时为 null。</summary>
    public SshTerminalBridge? Bridge { get; private set; }

    internal long ConnectionGeneration { get; private set; }

    /// <summary>
    /// 本标签的连接状态。赋值时同步 <see cref="IsConnected" />、在连接成功时清除错误,
    /// 并刷新失败/断开覆盖层的可见性与文案。
    /// </summary>
    public new SessionStatus ConnectionStatus
    {
        get => base.ConnectionStatus;
        set
        {
            if (base.ConnectionStatus != value)
            {
                ConnectionGeneration++;
            }
            base.ConnectionStatus = value;
            IsConnected = value == SessionStatus.Connected;

            // 连接成功即清除上次失败信息(隐去覆盖层);其余状态变化都要刷新覆盖层可见性。
            if (value == SessionStatus.Connected)
            {
                ConnectionError = null;
                AlgorithmRemedy = null;
            }
            this.RaisePropertyChanged(nameof(ShowDisconnectedOverlay));
            this.RaisePropertyChanged(nameof(DisconnectOverlayTitle));
            this.RaisePropertyChanged(nameof(DisconnectOverlayDetail));
            this.RaisePropertyChanged(nameof(ShowConnectingOverlay));
            this.RaisePropertyChanged(nameof(ConnectingOverlayTitle));
        }
    }

    /// <summary>
    /// 最近一次连接失败的原因;非空时覆盖层显示为“连接失败”,否则为掉线的
    /// “连接已断开”(设计 yxjmg / dcOverlay)。
    /// </summary>
    public string? ConnectionError
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasConnectionError));
            this.RaisePropertyChanged(nameof(DisconnectOverlayTitle));
            this.RaisePropertyChanged(nameof(DisconnectOverlayDetail));
        }
    }

    /// <summary>是否存在连接错误(决定覆盖层显示“连接失败”还是“连接已断开”)。</summary>
    public bool HasConnectionError => !string.IsNullOrEmpty(ConnectionError);

    /// <summary>
    /// 复制覆盖层上的失败原因到剪贴板。失败原因往往是一长串原文 —— 服务端的认证链、
    /// 主机密钥指纹、算法协商差异的两份名单 —— 要拿去搜索、贴给同事或发给设备厂商,
    /// 照着屏幕抄不现实。与连接配置页的「复制」是同一套做法。
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyErrorCommand { get; }

    /// <summary>写系统剪贴板的回调;由视图注入(视图模型层拿不到 TopLevel)。</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>刚复制过:按钮上短暂显示「已复制」回执。</summary>
    public bool ErrorCopied
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 标签页内失败/断开覆盖层(设计 yxjmg)的可见性:未连接(断开/错误)且是一个
    /// 真实会话标签(SSH 或本地)时显示。连接中/已连接不显示。
    /// </summary>
    public bool ShowDisconnectedOverlay =>
        !_disposed
        && ConnectionStatus is SessionStatus.Disconnected or SessionStatus.Error
        && (Profile is not null || LocalShell is not null);

    /// <summary>
    /// 标签页内「连接中」覆盖层的可见性:正在握手且是一个真实会话标签时显示。
    /// </summary>
    /// <remarks>
    /// 标签在握手开始前就建好了(见 <c>CreateConnectingTab</c>),这中间正文是一片
    /// 空白终端 —— 链路一慢,用户看到的就是"点了之后什么都没有"(#385 反馈)。
    /// 断开/失败早有覆盖层,连接中这一态却一直空着;补上之后三种非正常态各有各的画面。
    /// </remarks>
    public bool ShowConnectingOverlay =>
        !_disposed
        && ConnectionStatus == SessionStatus.Connecting
        && (Profile is not null || LocalShell is not null);

    /// <summary>「连接中」覆盖层的标题:正在连接 &lt;会话名&gt;。</summary>
    public string ConnectingOverlayTitle => Strings.Format("Msg_ConnectingToTitle", OverlayHostLabel);

    /// <summary>失败/断开覆盖层的标题:有错误时为“连接失败”,否则为“连接已断开”。</summary>
    public string DisconnectOverlayTitle =>
        HasConnectionError
            ? Strings.Get("Msg_ConnectionFailedTitle")
            : Strings.Get("Msg_ConnectionClosedTitle");

    /// <summary>失败/断开覆盖层的详情:有错误时显示具体原因,否则显示掉线主机提示。</summary>
    public string DisconnectOverlayDetail =>
        HasConnectionError
            ? ConnectionError!
            : Profile is null && LocalShell is not null
                ? Strings.Format("Msg_LocalShellExitedDetail", OverlayHostLabel)
                : Strings.Format("Msg_SshConnectionLostDetail", OverlayHostLabel);

    /// <summary>换语言后让绑定重取覆盖层的标题与详情(断开状态会一直挂着,不重取就停在旧语言)。</summary>
    public void RefreshLocalizedText()
    {
        this.RaisePropertyChanged(nameof(ConnectingOverlayTitle));
        this.RaisePropertyChanged(nameof(DisconnectOverlayTitle));
        this.RaisePropertyChanged(nameof(DisconnectOverlayDetail));
        this.RaisePropertyChanged(nameof(AlgorithmRemedyTip));
    }

    /// <summary>
    /// 覆盖层中指代本会话的名称:优先用配置的显示名称(用户认得的那个),
    /// 没起名时才退回 host:port;本地终端用 shell 名称。
    /// </summary>
    private string OverlayHostLabel =>
        Profile is { } p
            ? string.IsNullOrWhiteSpace(p.Name) ? HostAddress.FormatEndPoint(p.Host, p.Port) : p.Name
            : LocalShell is { } shell && !string.IsNullOrWhiteSpace(shell.Name)
                ? shell.Name
                : Title;

    /// <summary>最近一次测得的连接延迟;未知时为 null。</summary>
    public TimeSpan? Latency
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否处于已连接状态(随 <see cref="ConnectionStatus" /> 联动)。</summary>
    public bool IsConnected
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>已尝试的自动重连次数,连接成功或重置时归零。</summary>
    public int ReconnectAttempts
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 最大自动重连次数,唯一来源是设置 → 常规 → 自动重连(General.MaxRetries),
    /// 由宿主在断开处理时同步下发;默认值与设置模型一致,仅作未注入时的兜底。
    /// </summary>
    public int MaxReconnectAttempts
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 3;

    /// <summary>是否仍允许自动重连(重连次数未达上限)。</summary>
    public bool CanReconnect => ReconnectAttempts < MaxReconnectAttempts;

    /// <summary>断开实时传输,但保留标签(及其缓冲区)以便重连。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DisconnectCommand { get; }

    /// <summary>请求对已断开标签就地重连(等同于 Enter / Ctrl+R)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ReconnectCommand { get; }

    /// <summary>从标签页内失败/断开覆盖层(设计 yxjmg)关闭标签页。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseTabCommand { get; }

    /// <summary>
    /// 释放标签资源:解绑事件、即时销毁终端模拟器(UI 安全),并把耗时的网络拆除
    /// (取消读循环、关闭 SSH 通道)放到后台线程,避免关闭标签时卡住 UI(#18)。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ConnectionGeneration++;
        // 「已复制」回执的定时器可能还挂着:不取消的话它会在标签消失之后才回调。
        _copyFeedbackReset?.Dispose();
        _copyFeedbackReset = null;
        _autoResponseNoticeReset?.Dispose();
        _autoResponseNoticeReset = null;
        TerminalEmulator.OutputTriggerFired -= OnOutputTriggerFired;
        // 立即标记为未连接:同步频道转发(WriteSyncInput)以 IsConnected 为闸门。
        // 频道内其他标签的输入可能与本标签的关闭并发,不复位 IsConnected 的话,
        // 转发会向已释放的桥写入而与释放竞争。
        IsConnected = false;
        TerminalEmulator.PtySizeChanged -= OnPtySizeChanged;
        TerminalEmulator.TypedInput -= OnUserInputForTracker;
        TerminalEmulator.WorkingDirectoryChanged -= OnEmulatorWorkingDirectoryChanged;
        InputTracker.CommandSubmitted -= OnTrackedCommandSubmitted;
        InputTracker.UnknownLineSubmitted -= OnUnknownLineSubmitted;

        // 即时、UI 安全的拆除,使标签立即关闭:这里只解绑模拟器的
        // Updated 事件处理,不涉及网络 I/O。
        TerminalEmulator.Dispose();

        // 网络拆除(取消读循环、关闭 SSH 通道)是异步的:await 它,不占 UI 线程,
        // 也不像从前那样 Task.Run 甩出去就不管了 —— 那时标签早就消失,而连接
        // 还在后台慢慢断,关不干净的会话只能等进程退出。
        // (#18「关闭标签卡住 UI」修的是同一件事,当时的手段是甩到线程池。)
        SshTerminalBridge? bridge = Bridge;
        DisableNestedShellFollow();
        if (bridge is not null)
        {
            bridge.Closed -= OnBridgeClosed;
            bridge.LineSubmitted -= OnBridgeLineSubmitted;
            Bridge = null;
            await bridge.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 会话断开时(远端关闭了通道)触发,使 UI 显示断开覆盖层并提供重连(#19)。
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>用户请求重连已断开标签时触发(Enter / Ctrl+R)。</summary>
    public event EventHandler? ReconnectRequested;

    /// <summary>标签页内失败/断开覆盖层(设计 yxjmg)的“关闭标签页”触发,由宿主移除该标签。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>标签右键「连接信息」:请求宿主打开这条 SSH 会话的连接信息窗口。</summary>
    public event EventHandler? ConnectionInfoRequested;

    /// <summary>
    /// 这是一个 SSH 会话标签(有配置、协议是 SSH)—— 只有它有连接信息可看;本地终端与插件终端协议(Telnet、串口)没有。
    /// </summary>
    /// <remarks>配置在标签建好时就定了,协议不会中途变,绑定一次性读取即可。</remarks>
    public bool IsSshTerminal => LocalShell is null && Profile is { ConnectionType: ConnectionType.SSH };

    /// <summary>请求打开连接信息窗口(只对 SSH 会话标签有效)。</summary>
    public void RequestConnectionInfo()
    {
        if (IsSshTerminal)
        {
            ConnectionInfoRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>请求重连,但仅在已断开状态下有效(否则为空操作)。</summary>
    public void RequestReconnect()
    {
        if (ConnectionStatus == SessionStatus.Disconnected)
        {
            ReconnectRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>请求宿主关闭本标签(覆盖层按钮与 Esc 快捷键共用)。</summary>
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 通过正常用户输入通道把快捷命令文本发送到终端。只发正文不附加回车——
    /// 快捷命令可能是不完整的模板(如缺少参数),由用户在终端里补全后自行按 Enter 执行。
    /// </summary>
    /// <remarks>
    /// 多行命令走括号粘贴(#555):原样写进去的话,中间每个换行都等于按一次回车,前几行当场就执行了。
    /// 远端开着括号粘贴时 shell 把整段放进命令行等用户回车;没开时照样会逐行执行 ——
    /// 发之前问不问由调用方决定(见 <see cref="MainWindowViewModel" /> 的多行确认)。
    /// </remarks>
    /// <returns>文本已发送时为 true;终端未连接或命令为空时为 false。</returns>
    public bool TrySendCommandText(string command) => SendCommand(command, submit: false);

    /// <summary>
    /// 「重跑」一条 OSC 133 命令块里的命令:与快捷命令同一条发送路径,末尾再按一次回车。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 能不能重跑(对端停在空提示符上、上一条命令已结束、不在全屏程序里)、多行命令要不要先问,
    /// 都由终端控件在发起前判定过(<c>VelaTerminalControl.RerunCommandAsync</c>);这里只管发。
    /// </para>
    /// <para>
    /// <b>只发给本标签</b>,同步输入不转发:重跑点的是这一个终端历史里的一条命令,频道里别的标签
    /// 此刻停在什么状态、该不该跑它,这里一概不知 —— 与快捷命令关掉转发的理由一样。
    /// </para>
    /// </remarks>
    /// <returns>已发送时为 true;终端未连接或命令为空时为 false。</returns>
    public bool TryRerunCommand(string command) => SendCommand(command, submit: true);

    private bool SendCommand(string command, bool submit)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        SendProgrammatic(command.TrimEnd('\r', '\n'), submit, forwardToSyncChannel: false, background: false);
        return true;
    }

    /// <summary>
    /// 程序注入的唯一出口(快捷命令、重跑、自动应答):正文按会话字符集编码,多行走括号粘贴,
    /// 末尾按需再按一次回车;整段期间标为程序注入(补全弹层不弹)。
    /// </summary>
    /// <param name="payload">正文(可以为空:自动应答「只按回车」)。</param>
    /// <param name="submit">正文之后按回车。</param>
    /// <param name="forwardToSyncChannel">
    /// 是否经同步输入频道转发。快捷命令与重跑一律不转(前者自带多目标分发,后者只属于这一个终端);
    /// 自动应答只有规则明确选了才转。
    /// </param>
    /// <param name="background">
    /// 不是用户此刻发起的(自动应答):走 <see cref="ITerminalEmulator.WriteBackgroundInput" />,不把回滚中的视图
    /// 拉回底部、不清选区。快捷命令与重跑是用户点出来的,照键入的口径收尾。应答文字不含控制字符
    /// (<c>OutputRuleCompiler</c> 拦下),不会是多行。
    /// </param>
    private void SendProgrammatic(string payload, bool submit, bool forwardToSyncChannel, bool background)
    {
        bool multiline = QuickCommandText.IsMultiline(payload);

        // 快捷命令可能同时下发给同频道的多个标签,若再经同步频道转发,频道内
        // 每个标签都会收到重复注入;WriteInput 同步触发 TypedInput,压制窗口有效。
        // IsProgrammaticInput 同理:跟踪器仍需感知注入文本以保持行状态一致,
        // 但补全弹层不该把程序注入当作用户键入而弹出。
        IsSyncForwardSuppressed = !forwardToSyncChannel;
        IsProgrammaticInput = true;
        try
        {
            if (background && !multiline)
            {
                // 与下面同样按会话字符集编正文、回车按终端模式编,同样经键入出口(补全跟踪、同步转发、
                // 桥的输入代次、共享密码列表的作废都照旧),只是不做键入后的视图收尾。
                TerminalEmulator.WriteBackgroundInput(
                    payload.Length > 0 ? TerminalEmulator.SessionEncoding.GetBytes(payload) : [], submit);
                return;
            }
            if (multiline)
            {
                // 括号粘贴由终端按会话字符集编正文、按 ASCII 编标记,换行也由它统一成回车。
                TerminalEmulator.WritePasteInput(payload);
            }
            else if (payload.Length > 0)
            {
                // 按会话字符集编码:快捷命令里可能带中文(路径、grep 的关键字),
                // 写死 UTF-8 的话在 GBK 主机上送过去就是乱码。
                TerminalEmulator.WriteInput(TerminalEmulator.SessionEncoding.GetBytes(payload));
            }
            if (submit)
            {
                // 回车按终端当前模式编(远端开了换行模式 LNM 时是 CR LF),不写死 \r。
                // 走同一条键入通道,命令补全的行跟踪照常看到「提交了一行」、记进历史。
                TerminalEmulator.WriteKeyInput(Avalonia.Input.Key.Enter, Avalonia.Input.KeyModifiers.None);
            }
        }
        finally
        {
            IsSyncForwardSuppressed = false;
            IsProgrammaticInput = false;
        }
    }

    // ---- 输出规则的自动应答 -----------------------------------------------------

    /// <summary>「已自动应答」提示停留的时长。</summary>
    private static readonly TimeSpan AutoResponseNoticeDuration = TimeSpan.FromSeconds(4);

    private IDisposable? _autoResponseNoticeReset;

    /// <summary>
    /// 终端上方的轻提示:「已自动应答:规则名」,或某条规则到了每分钟上限、暂停应答;null = 不显示。
    /// </summary>
    /// <remarks>自动发出去的字用户没敲过,不在界面上说一声,他只会看到 shell 里凭空多了一个 y。</remarks>
    public string? AutoResponseNotice
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 一条自动应答规则命中:照它发送(或只提示到了上限)。
    /// </summary>
    /// <remarks>
    /// 与快捷命令同一条程序注入路径(<see cref="SendProgrammatic" />):按会话字符集编码、回车按终端模式编、
    /// 补全弹层不弹;同步输入频道默认不转,规则选了「转发」才转。没连着(断线、重连中)时什么都不发、也不提示。
    /// 与快捷命令不同的只有一处:这不是用户的动作,不清选区、不把回滚中的视图拉回底部(<c>background</c>)。
    /// </remarks>
    private void OnOutputTriggerFired(OutputTriggerHit hit)
    {
        if (_disposed || !IsConnected)
        {
            return;
        }
        if (hit.RateLimited)
        {
            ShowAutoResponseNotice(Strings.Format("OutRule_NoticeRateLimited", hit.RuleName));
            return;
        }
        string payload = hit.Response.TrimEnd('\r', '\n');
        if (payload.Length == 0 && !hit.SendEnter)
        {
            return;
        }
        SendProgrammatic(payload, hit.SendEnter, hit.ForwardToSyncChannel, background: true);
        ShowAutoResponseNotice(Strings.Format("OutRule_NoticeResponded", hit.RuleName));
    }

    private void ShowAutoResponseNotice(string text)
    {
        AutoResponseNotice = text;
        _autoResponseNoticeReset?.Dispose();
        _autoResponseNoticeReset = DispatcherTimer.RunOnce(() => AutoResponseNotice = null, AutoResponseNoticeDuration);
    }

    /// <summary>
    /// 程序注入输入(快捷命令下发等)期间为 true。注入与用户键入共用 TypedInput
    /// 通道(行跟踪必需),视图据此区分两者,注入期间不弹命令补全。
    /// </summary>
    public bool IsProgrammaticInput { get; private set; }

    /// <summary>
    /// 把初始化命令注入远端 shell 并静默执行:发送前在桥上装回显抑制器,
    /// 把 PTY 回显的这一行从输出流剥掉,不在界面显示。前导空格让
    /// HISTCONTROL=ignoreboth 不记历史;抑制针 needle 不含该空格(空格太常见,
    /// 不适合做流匹配锚点)。注入本身要占掉 shell 的一个提示符周期,那个周期的提示符
    /// 已经画在屏幕上了 —— 收回它的清行动作由 <see cref="SilentCommand" /> 接在哨兵后面
    /// 一并发出(见那边的 <c>PromptReclaim</c>),调用方不必操心。
    /// <para>
    /// 「静默」还包括**不留在命令历史里**:整行前面接一段 <see cref="ShellHistoryScrub" />,
    /// 由 bash 自己把这一条从历史里摘掉(zsh 摘不掉,退一步让它不写进历史文件)。屏幕上隐形、方向键一按却整行冒出来 ——
    /// 那不是用户敲的,他也不知道那是什么,第一反应是「谁往我服务器上注了东西」(用户反馈)。
    /// 前导空格只对配了 <c>HISTCONTROL=ignorespace</c> 的人有效,而它默认是空的,拦不住。
    /// </para>
    /// </summary>
    /// <param name="command">要静默执行的整条命令;空白则什么都不发。</param>
    public void SendSilentCommand(string command) => SendInjection(null, command);

    /// <summary>
    /// 注入目录上报脚本:<paramref name="installScript" /> 连输出带报错一起藏掉,
    /// <paramref name="firstReport" /> 的输出留着(文件浏览器一连上就要靠它拿到 cwd)。
    /// </summary>
    /// <remarks>
    /// 和 <see cref="SendSilentCommand" /> 分成两个入口而不是加个 bool:这一条是<b>两段</b>,
    /// 一段藏一段不藏,而"藏不藏"正是靠哨兵插在中间实现的(见 <see cref="SilentCommand.Build" />)。
    /// 用一个布尔开关表达不了"一行里有两种待遇"。
    /// </remarks>
    public void SendShellIntegration(string installScript, string firstReport) =>
        SendInjection(installScript, firstReport);

    /// <summary>
    /// 静默注入的唯一出口:组装整行、按需开注入窗口、直写 PTY。
    /// </summary>
    /// <param name="hidden">排在哨兵<b>之前</b>的部分 —— 它的输出与报错都不上屏。</param>
    /// <param name="visible">排在哨兵<b>之后</b>的部分 —— 它的输出原样显示。</param>
    private void SendInjection(string? hidden, string? visible)
    {
        // 整行的组装(摘历史前缀在前、哨兵插在中间)一律交给 SilentCommand ——
        // 它同时是 Docker 端到端测试里那条链路的起点,两边不能各拼各的。
        ShellIntegrationInjection injection = SilentCommand.Build(RemoteShellKind, hidden, visible);
        if (Bridge is null || injection.IsEmpty)
        {
            return;
        }
        if (injection.Sentinel.Length > 0)
        {
            // 有哨兵:从现在起整段扣住,看见哨兵才恢复放行。回显长什么样都无所谓 ——
            // 而那正是关键:整行一旦超出终端宽度,各家 shell 的折行重绘就没法逐字节匹配了
            // (真机实测三种形态,见 EchoSuppressor.OpenWindow)。
            Bridge.OpenInjectionWindow(TerminalEmulator.SessionEncoding.GetBytes(injection.Sentinel));
        }
        else
        {
            // 探不出种类 / 对端是 cmd.exe:那上面 printf 未必存在,哨兵永远回不来,
            // 窗口只能白等到超时。退回按回显匹配的抑制针 —— 这些场景下我们本来也只发
            // 用户自己那条短命令,短命令不会折行,针咬得住。
            Bridge.SuppressEchoOnce(
                TerminalEmulator.SessionEncoding.GetBytes(injection.CommandLine + "\r\n"));
        }

        // 直写 PTY(SendRaw)而非 WriteInput:注入不是用户键入,不得进入命令补全的
        // 行跟踪——补行脚本里的 ESC 字节曾把跟踪器打进未知态,SSH 标签建议全灭。
        // 前导空格是给配了 HISTCONTROL=ignorespace 的人的;真正的防线是摘历史前缀。
        // 注入行同样按会话字符集编码:用户配的「认证后执行命令」与初始目录里可能有中文,
        // 而抑制针 / 哨兵必须与真正送出去的那串字节同一套编码,否则回显匹配不上。
        Bridge.SendRaw(TerminalEmulator.SessionEncoding.GetBytes(" " + injection.CommandLine + "\n"));

        // 用户配的命令(认证后命令 `sudo su -`、启动命令)可能把前台换成新 shell。它紧跟在钩子注入后面发出,
        // 钩子自己的那次首报会落在它"之后",所以这一行要强制问一次,不能拿"随后有上报"当作没换人。
        if (hidden is null)
        {
            _nestedShellFollower?.OnLineSubmitted(force: true);
        }
    }

    /// <summary>启动桥接的 I/O 泵送(幂等;无桥或已启动时为空操作)。</summary>
    public void Start()
    {
        if (_started || Bridge is null)
        {
            return;
        }
        Bridge.Start();
        _started = true;
    }

    /// <summary>
    /// 挂载实时 shell 流并准备 I/O 泵送(之后调用 <see cref="Start" />)。
    /// 会先在后台拆除任何先前的传输,因此它同时充当复用同一标签与回滚
    /// 缓冲区的重连入口(#19)。
    /// </summary>
    public async ValueTask AttachTransportAsync(IShellStreamWrapper shellStream)
    {
        ArgumentNullException.ThrowIfNull(shellStream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // **等旧桥真的拆完**再挂新的:不等的话两条读循环会同时对着同一个模拟器写,
        // 表现是重连之后屏幕上混进上一条会话的残余输出。
        await DetachTransportAsync().ConfigureAwait(true);
        AttachTransportCore(shellStream);
    }

    /// <summary>挂上一条新传输。调用前必须确保没有旧桥。</summary>
    private void AttachTransportCore(IShellStreamWrapper shellStream)
    {
        UserRequestedDisconnect = false;
        RemoteShellExited = false;
        HostKeyRejected = false;
        AuthenticationRequiresUserAction = false;
        ShellStream = shellStream;
        var bridge = new SshTerminalBridge(TerminalEmulator, shellStream);
        bridge.Closed += OnBridgeClosed;
        bridge.LineSubmitted += OnBridgeLineSubmitted;
        ApplyAntiIdle(bridge);
        Bridge = bridge;
        ConnectionGeneration++;
        _started = false;

        // 通道以固定的默认网格(120×32)打开。到此时控件通常已经布局到真实视口,
        // 但承载该尺寸的 PtySizeChanged 在 ShellStream 仍为 null 时触发并被丢弃了。
        // 把模拟器当前网格推送到新流,使远程 PTY 的 winsize 与实际可见区域一致 ——
        // 否则全屏程序(htop/nano)会读取过期的 32 行尺寸,把页脚画在屏幕中央,
        // 导致终端下半部分空白。
        SyncPtySize();
    }

    /// <summary>
    /// 按本会话的高级设置打开/关闭防空闲注入(没有配置的标签 —— 本地终端 —— 恒为关闭)。
    /// </summary>
    /// <remarks>
    /// 挂在挂载传输这一步,首连与重连就都覆盖到了:重连会新建一条桥,设置得跟着走过去,
    /// 否则"断线重连之后又开始被踢"这种事没人查得出来。改了配置要等下次连接才生效 ——
    /// 与同一栏里的保活间隔一个口径。
    /// </remarks>
    /// <param name="bridge">刚建好、尚未启动的桥。</param>
    private void ApplyAntiIdle(SshTerminalBridge bridge) =>
        bridge.AntiIdleInterval = TimeSpan.FromSeconds(SessionTerminalSettings.AntiIdleSeconds(Profile));

    /// <summary>
    /// 把模拟器当前的网格尺寸重新发送到实时 shell 流,使远程 PTY 的
    /// winsize 与真实视口一致,而非通道打开时的固定尺寸。
    /// 通道打开时的 <c>pty-req</c> 像素是 0(那时还不知道),也由这一次补上。
    /// </summary>
    private void SyncPtySize()
    {
        if (TerminalEmulator.CurrentPtySize is { Columns: > 0, Rows: > 0 } size)
        {
            OnPtySizeChanged(size);
        }
    }

    /// <summary>拆除当前传输,同时保留标签与缓冲区完好。</summary>
    /// <remarks>
    /// 原先是 <c>Task.Run(bridge.Dispose)</c> —— 因为那时拆桥是同步阻塞的,
    /// 在 UI 线程上做会卡住(#18)。现在拆桥本身是异步的:直接 await,
    /// **拆完才返回**。这一点对重连这条路很重要 —— <see cref="AttachTransportAsync" />
    /// 会先调它再挂新桥,不等旧桥收完就挂新的,两条读循环会同时对着同一个模拟器写。
    /// </remarks>
    public async ValueTask DetachTransportAsync()
    {
        SshTerminalBridge? bridge = Bridge;
        if (bridge is null)
        {
            return;
        }
        bridge.Closed -= OnBridgeClosed;
        bridge.LineSubmitted -= OnBridgeLineSubmitted;
        // 换了连接,登录 shell 与它的进程号也换了:旧的补注记录与探针一并作废,重连后宿主重新挂。
        DisableNestedShellFollow();
        Bridge = null;
        ConnectionGeneration++;
        ShellStream = null;
        _started = false;

        // 拆桥也会释放 shell 流。
        await bridge.DisposeAsync().ConfigureAwait(false);
    }

    private void OnBridgeClosed(ShellCloseReason reason)
    {
        // 远端 shell 自己退了 = 用户意图,与点断开按钮同等对待:自动重连不再介入(#383)。
        // 必须赶在 MarkDisconnected 之前置位 —— 它同步触发 Disconnected,
        // 宿主就在那个事件里当场决定要不要排一次重连。
        RemoteShellExited = reason == ShellCloseReason.RemoteExited;

        // 在读线程上触发;将响应式状态变更封送到 UI 线程。
        if (Dispatcher.UIThread.CheckAccess())
        {
            MarkDisconnected();
        }
        else
        {
            Dispatcher.UIThread.Post(() => MarkDisconnected());
        }
    }

    /// <summary>
    /// 初始连接失败:保留标签并在标签页内显示失败覆盖层(设计 yxjmg),不销毁标签、
    /// 不弹全局框。置为断开态以启用“重新连接”;不触发 <see cref="Disconnected" /> 事件——
    /// 初始连接失败不应自动重连(尤其认证失败),由用户手动决定重连或关闭。
    /// </summary>
    /// <summary>
    /// 把失败原因原文送进剪贴板,并在按钮上留一句「已复制」的回执 —— 没有回执就分不清
    /// 「复制成功了」和「按钮没反应」,用户只会再点两下。回执到点自动退回;连点时先取消
    /// 上一个定时器,否则第二次点完会立刻被第一次的定时器把提示抹掉。
    /// </summary>
    private async Task CopyErrorAsync()
    {
        if (CopyToClipboard is not { } copy || ConnectionError is not { Length: > 0 })
        {
            return;
        }
        // 复制覆盖层上那一整段(标题下面显示的就是它),而不是只复制 ConnectionError:
        // 用户要的是「屏幕上这段话」,两者不一致就会让人以为漏复制了。
        await copy(DisconnectOverlayDetail).ConfigureAwait(true);
        ErrorCopied = true;
        _copyFeedbackReset?.Dispose();
        _copyFeedbackReset = DispatcherTimer.RunOnce(() => ErrorCopied = false, CopyFeedbackDuration);
    }

    /// <summary>
    /// 把标签切换到断开状态并记录连接失败的原因(幂等)。覆盖层显示为“连接失败 + 具体原因”,
    /// </summary>
    /// <param name="message">覆盖层上显示的失败原因。</param>
    /// <param name="remedy">算法协商失败、且能一键放开对端那几个算法时给;覆盖层据此多一颗按钮。</param>
    public void MarkConnectionFailed(string message, SshAlgorithmRemedy? remedy = null)
    {
        if (_disposed)
        {
            return;
        }

        // 先记录原因(覆盖层显示“连接失败”),再切断开态刷新覆盖层可见性。
        ConnectionError = message;
        AlgorithmRemedy = remedy;
        ConnectionStatus = SessionStatus.Disconnected;
    }

    /// <summary>
    /// 上一次连接败在算法协商上、而对端有本版实现了只是没放开的算法:覆盖层上的「放开这几个算法并重连」按它来。
    /// 每次记失败原因时一并换掉(没有就是 <see langword="null" />),连上即清。
    /// </summary>
    public SshAlgorithmRemedy? AlgorithmRemedy
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasAlgorithmRemedy));
            this.RaisePropertyChanged(nameof(AlgorithmRemedyTip));
        }
    }

    /// <summary>覆盖层上要不要出「放开这几个算法并重连」。</summary>
    public bool HasAlgorithmRemedy => AlgorithmRemedy is not null;

    /// <summary>那颗按钮的悬停说明:往哪一类清单里加哪几个。</summary>
    public string AlgorithmRemedyTip => AlgorithmRemedy is { } remedy
        ? SshAlgorithmRemedyFlow.ButtonTip(remedy, SshAlgorithmRemedyFollowUp.Reconnect)
        : string.Empty;

    /// <summary>覆盖层上点了「放开这几个算法并重连」:由宿主确认、改配置、重连。</summary>
    public event EventHandler? AllowAlgorithmsRequested;

    /// <summary>「放开这几个算法并重连」(只在断开且有补救时可点)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AllowAlgorithmsCommand { get; }

    /// <summary>
    /// 用户在认证交互(动态码框)里点了取消:与点断开按钮是同一句话,自动重连不再介入。
    /// </summary>
    public void MarkDisconnectedByUser()
    {
        UserRequestedDisconnect = true;
        MarkDisconnected();
    }

    /// <summary>重连败在主机指纹上:标为连接失败,并停掉自动重连(见 <see cref="HostKeyRejected" />)。</summary>
    /// <param name="reason">覆盖层上显示的失败原因。</param>
    public void MarkHostKeyRejected(string reason)
    {
        HostKeyRejected = true;
        MarkDisconnected(reason);
    }

    /// <summary>保留当前认证跳的安全失败原因,在断开通知之前暂停后台重连。</summary>
    public void MarkAuthenticationRequiresUserAction(string reason)
    {
        AuthenticationRequiresUserAction = true;
        MarkDisconnected(reason);
    }

    /// <summary>用户手动重连:上一次败在主机指纹上的那条不再拦着自动重连(这一次会弹窗问他)。</summary>
    public void ClearHostKeyRejected() => HostKeyRejected = false;

    /// <summary>
    /// 将标签切换到断开状态并通知监听者(幂等)。
    /// <paramref name="reason" /> 非空时(如重连失败)覆盖层显示为“连接失败 + 具体原因”,
    /// 否则为普通掉线的“连接已断开”。
    /// </summary>
    public void MarkDisconnected(string? reason = null, SshAlgorithmRemedy? remedy = null)
    {
        if (_disposed)
        {
            return;
        }
        if (!string.IsNullOrEmpty(reason))
        {
            ConnectionError = reason;
            AlgorithmRemedy = remedy;
        }
        if (ConnectionStatus == SessionStatus.Disconnected)
        {
            return;
        }
        ConnectionStatus = SessionStatus.Disconnected;
        FeedDisconnectNotice();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 向终端打印一行红色“连接已关闭”横幅以及重连提示,让用户知道按 Enter /
    /// 点“重新连接”按钮即可恢复会话。手动断开与远端关闭时都会执行。
    /// </summary>
    private void FeedDisconnectNotice()
    {
        string notice =
            $"\r\n\u001b[0m\u001b[31m● {Strings.TerminalDisconnectedNotice}\u001b[0m\r\n\u001b[90m{Strings.TerminalReconnectHint}\u001b[0m\r\n";
        try
        {
            // 横幅是我们自己生成的文本,但喂进去的是**字节**,而解码那一侧用的是会话字符集 ——
            // 写死 UTF-8 的话,GBK 会话上这行中文提示自己就是乱码。
            TerminalEmulator.Feed(TerminalEmulator.SessionEncoding.GetBytes(notice));
        }
        catch
        {
            // 纯属装饰;绝不让提示破坏断开流程。
        }
    }

    /// <summary>
    /// 在 UI 线程外把网格变化按严格顺序转发到 SSH 通道,并合并突发为最新尺寸。
    /// 原先每次事件都 fire-and-forget 地 Task.Run,在拖拽风暴期间可能乱序送达尺寸,
    /// 导致远程 shell 拿到过期网格 —— 其随后的提示符重绘便会破坏缓冲区。
    /// </summary>
    private void OnPtySizeChanged(PtySize size)
    {
        if (_disposed || ShellStream is null || !ShellStream.CanWrite)
        {
            return;
        }
        lock (_ptyResizeGate)
        {
            _pendingPtySize = size;
            if (_ptyResizeSending)
            {
                return;
            }
            _ptyResizeSending = true;
        }
        _ = Task.Run(DrainPtyResizeQueue);
    }

    private void DrainPtyResizeQueue()
    {
        while (true)
        {
            PtySize size;
            lock (_ptyResizeGate)
            {
                if (_pendingPtySize is null)
                {
                    _ptyResizeSending = false;
                    return;
                }
                size = _pendingPtySize.Value;
                _pendingPtySize = null;
            }
            IShellStreamWrapper? stream = ShellStream;
            if (_disposed || stream is null || !stream.CanWrite)
            {
                lock (_ptyResizeGate)
                {
                    _pendingPtySize = null;
                    _ptyResizeSending = false;
                }
                return;
            }
            try
            {
                stream.Resize(size);
            }
            catch
            {
                // 在已拆除或不支持的通道上调整尺寸是非致命的。
            }
        }
    }

    /// <summary>自动重连尝试计数加一。</summary>
    public void IncrementReconnectAttempt() => ReconnectAttempts++;

    /// <summary>把自动重连尝试计数归零(连接成功后调用)。</summary>
    public void ResetReconnectAttempts() => ReconnectAttempts = 0;
}
