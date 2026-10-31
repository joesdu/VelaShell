using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.ViewModels;

/// <summary>一台机器的巡检结论。</summary>
public enum SessionHealthStatus
{
    /// <summary>排着队,还没轮到。</summary>
    Pending,

    /// <summary>正在查。</summary>
    Checking,

    /// <summary>四步都过了(连得上、登得进)。</summary>
    Healthy,

    /// <summary>连得上、SSH 正常,这一轮没验证登录(巡检里关了「验证登录」)。</summary>
    Reachable,

    /// <summary>连得上、SSH 正常,但配置里没存凭据:连接时会弹登录框,巡检不弹,所以没验证登录。</summary>
    NeedsCredentials,

    /// <summary>卡在要人回答的地方(主机指纹待确认、Tailscale 复核):巡检不替人回答,要用户亲自连一次。</summary>
    NeedsAttention,

    /// <summary>连不上:DNS、端口、SSH 握手或登录有一步没过。</summary>
    Failed,

    /// <summary>一台查太久,到点放弃。</summary>
    TimedOut,

    /// <summary>不是 SSH / SFTP 连接,巡检不查(四步诊断是按 SSH 走的)。</summary>
    Unsupported,

    /// <summary>用户停下了巡检,这一台没查完。</summary>
    Cancelled,
}

/// <summary>一台机器的巡检结论:状态、卡在哪一步、怎么说。</summary>
/// <param name="Status">结论。</param>
/// <param name="Step">卡在哪一步(那一步的名称);没卡住时为 null。</param>
/// <param name="Detail">一句话说明。</param>
/// <param name="ElapsedMs">这一台查了多久;没查时为 null。</param>
public sealed record SessionHealthOutcome(SessionHealthStatus Status, string? Step, string? Detail, long? ElapsedMs);

/// <summary>把一份四步诊断报告归成巡检表里的一行结论。</summary>
public static class SessionHealthClassifier
{
    /// <summary>按报告归类:第一个失败的步骤就是「卡在哪一步」。</summary>
    /// <param name="report">诊断报告。</param>
    /// <param name="elapsedMs">这一台查了多久。</param>
    public static SessionHealthOutcome Classify(DiagnosticReport report, long elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(report);
        DiagnosticStepUpdate? failed = report.Steps.FirstOrDefault(step => step.Status == DiagnosticStepStatus.Failed);
        if (report.IssueTitle is { } title)
        {
            // 主机指纹待确认、要动态码、要先改密码、Tailscale 要复核:不是机器坏了,是巡检不替人回答。
            if (RequiresUser(report.Failure))
            {
                return new(SessionHealthStatus.NeedsAttention, failed?.Name, Join(title, failed?.Detail), elapsedMs);
            }
            // 私钥带口令、配置里没存口令:手动连接时会问口令,巡检不问 —— 与「没存凭据」同一类,不是机器连不上。
            // 系统密钥库的钥要 PIN / 确认才能用(巡检以「不许弹框」打开它)同理。
            if (MissingPassphrase(report.Failure) || KeyStoreNeedsUser(report.Failure))
            {
                return new(SessionHealthStatus.NeedsCredentials, failed?.Name, failed?.Detail ?? title, elapsedMs);
            }
            // 跳板链本身配错了(被删、成环):四步都无从谈起,卡在配置上。
            return new(SessionHealthStatus.Failed, failed?.Name ?? Strings.Get("Health_StepConfiguration"),
                Join(title, failed?.Detail ?? report.IssueDescription), elapsedMs);
        }
        DiagnosticStepUpdate auth = report.Steps[^1];
        return auth.Status switch
        {
            DiagnosticStepStatus.Warning => new(SessionHealthStatus.NeedsCredentials, auth.Name, auth.Detail, elapsedMs),
            DiagnosticStepStatus.Skipped => new(SessionHealthStatus.Reachable, null, auth.Detail, elapsedMs),
            _ => new(SessionHealthStatus.Healthy, null, auth.Detail, elapsedMs),
        };
    }

    private static bool RequiresUser(Exception? failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            // 巡检里的「已取消认证」只可能是那个不问人的界面谢绝了动态码 / 改密码(见 SshConnectionAssembler)。
            if (current is VelaSshHostKeyRejectedException or VelaSshAuthenticationCancelledException
                or VelaSshTailscaleAuthenticationException { RequiresUserAction: true }
                or SshException { Reason: SshFailureReason.TwoFactorRequired or SshFailureReason.PasswordExpired })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 失败链上有「私钥要口令而没给」(<see cref="SshFailureReason.KeyPassphraseRequired" />,库在派生密钥之前就报,不白跑 KDF)。
    /// </summary>
    /// <remarks>
    /// 配置里存着「私钥路径」就算有凭据(诊断那一步因此去登录),可口令没存时读私钥这一步就失败,翻译过的认证异常里面包着它。
    /// 存着的口令不对(<see cref="SshFailureReason.KeyPassphraseIncorrect" />)不在此列:那是存错了,与存错的密码一样记「连不上」。
    /// </remarks>
    private static bool MissingPassphrase(Exception? failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is SshException { Reason: SshFailureReason.KeyPassphraseRequired })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>CNG 的 <c>NTE_SILENT_CONTEXT</c>:以「不许弹框」打开的钥,密钥库非得问人(PIN、插卡、确认)才能用。</summary>
    private const int SilentContextRequired = unchecked((int)0x80090022);

    /// <summary>
    /// 系统密钥库的钥要人来(<c>NTE_SILENT_CONTEXT</c>):打不开时在失败链上,签名时才要的在认证尝试记录里
    /// (库把签名器抛的异常记进那条凭据的说明、换下一条,不在 <see cref="Exception.InnerException" /> 链上)。
    /// </summary>
    private static bool KeyStoreNeedsUser(Exception? failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current.HResult == SilentContextRequired)
            {
                return true;
            }
            if (current is SshAuthenticationException auth
                && auth.Attempts.SelectMany(static attempt => attempt.Details).Any(static detail => IsSilentContext(detail.Error)))
            {
                return true;
            }
        }
        return false;

        static bool IsSilentContext(Exception? error)
        {
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                if (current.HResult == SilentContextRequired)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static string? Join(string head, string? tail) =>
        string.IsNullOrWhiteSpace(tail) || tail == head ? head : $"{head} · {tail}";
}

/// <summary>巡检表里的一行。</summary>
public sealed class SessionHealthRowViewModel : ReactiveObject
{
    /// <summary>建一行(排队中)。</summary>
    /// <param name="profile">要查的配置。</param>
    public SessionHealthRowViewModel(SessionProfile profile) => Profile = profile ?? throw new ArgumentNullException(nameof(profile));

    /// <summary>要查的那份配置(重查之前从库里换成最新的)。</summary>
    public SessionProfile Profile
    {
        get;
        internal set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Name));
            this.RaisePropertyChanged(nameof(Target));
            this.RaisePropertyChanged(nameof(CanDiagnose));
        }
    }

    /// <summary>行上的「连接诊断」只对 SSH / SFTP 配置出现(四步诊断是按 SSH 走的)。</summary>
    public bool CanDiagnose => Profile.ConnectionType is ConnectionType.SSH or ConnectionType.SFTP;

    /// <summary>名称(没起名退回主机)。</summary>
    public string Name => string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Host : Profile.Name;

    /// <summary><c>用户@主机:端口</c>。</summary>
    public string Target => HostAddress.FormatTarget(Profile.Username, Profile.Host, Profile.Port);

    /// <summary>结论。</summary>
    public SessionHealthStatus Status
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(IsHealthy));
            this.RaisePropertyChanged(nameof(IsWarning));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(IsMuted));
            this.RaisePropertyChanged(nameof(IsChecking));
            this.RaisePropertyChanged(nameof(IsProblem));
            this.RaisePropertyChanged(nameof(IsDone));
        }
    } = SessionHealthStatus.Pending;

    /// <summary>结论那一列的文字。</summary>
    public string StatusText => Strings.Get(Status switch
    {
        SessionHealthStatus.Checking => "Health_StatusChecking",
        SessionHealthStatus.Healthy => "Health_StatusHealthy",
        SessionHealthStatus.Reachable => "Health_StatusReachable",
        SessionHealthStatus.NeedsCredentials => "Health_StatusNeedsCredentials",
        SessionHealthStatus.NeedsAttention => "Health_StatusNeedsAttention",
        SessionHealthStatus.Failed => "Health_StatusFailed",
        SessionHealthStatus.TimedOut => "Health_StatusTimedOut",
        SessionHealthStatus.Unsupported => "Health_StatusUnsupported",
        SessionHealthStatus.Cancelled => "Health_StatusCancelled",
        _ => "Health_StatusPending",
    });

    /// <summary>卡在哪一步;查着时是正在查的那一步。</summary>
    public string? Step
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>一句话说明。</summary>
    public string? Detail
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>这一台查了多久;没查完时为 null。</summary>
    public long? ElapsedMs
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ElapsedText));
        }
    }

    /// <summary>耗时那一列。</summary>
    public string ElapsedText => ElapsedMs is { } ms ? Strings.Format("Health_ElapsedMs", ms) : string.Empty;

    /// <summary>通过(正常、可达)。</summary>
    public bool IsHealthy => Status is SessionHealthStatus.Healthy or SessionHealthStatus.Reachable;

    /// <summary>要用户处理、但机器本身不一定坏了(缺凭据、待确认)。</summary>
    public bool IsWarning => Status is SessionHealthStatus.NeedsCredentials or SessionHealthStatus.NeedsAttention;

    /// <summary>连不上、超时。</summary>
    public bool IsFailed => Status is SessionHealthStatus.Failed or SessionHealthStatus.TimedOut;

    /// <summary>排队、不查、停下:弱化显示。</summary>
    public bool IsMuted => Status is SessionHealthStatus.Pending or SessionHealthStatus.Unsupported or SessionHealthStatus.Cancelled;

    /// <summary>正在查。</summary>
    public bool IsChecking => Status == SessionHealthStatus.Checking;

    /// <summary>「只看有问题的」留下的那些:要处理的与连不上的。</summary>
    public bool IsProblem => IsWarning || IsFailed;

    /// <summary>已经有结论(不管好坏)。</summary>
    public bool IsDone => Status is not (SessionHealthStatus.Pending or SessionHealthStatus.Checking);

    /// <summary>第几次查这一行:晚到的进度回调对不上号就丢掉。</summary>
    internal int Attempt { get; private set; }

    internal int BeginCheck()
    {
        Attempt++;
        Status = SessionHealthStatus.Checking;
        Step = null;
        Detail = null;
        ElapsedMs = null;
        return Attempt;
    }

    internal void ReportStep(int attempt, string step)
    {
        if (attempt == Attempt && Status == SessionHealthStatus.Checking)
        {
            Step = step;
        }
    }

    internal void Apply(SessionHealthOutcome outcome)
    {
        Status = outcome.Status;
        Step = outcome.Step;
        Detail = outcome.Detail;
        ElapsedMs = outcome.ElapsedMs;
    }

    internal void Reset()
    {
        Attempt++;
        Status = SessionHealthStatus.Pending;
        Step = null;
        Detail = null;
        ElapsedMs = null;
    }
}

/// <summary>
/// 会话健康巡检:对一批已保存的配置逐台跑四步诊断(DNS → TCP → SSH 握手 → 认证),出一张「哪几台连不上」的表。
/// </summary>
/// <remarks>
/// <para>
/// <b>只在用户点开时跑</b>(资源管理器的分组 / 多选菜单、命令面板):一次一批,没有定时器、不在后台自己跑 ——
/// 巡检就是对一批机器外连,定时外呼得用户自己开、还要在隐私说明里讲清楚,这一版不做。
/// </para>
/// <para>
/// <b>不弹任何框</b>:认证那一步走 <see cref="IConnectionDiagnosticsService.DiagnoseUnattendedAsync" />,
/// 主机指纹待确认、动态码、Tailscale 复核照拒绝报回,记成「需要确认」;没存凭据的不弹登录框,记成「需要凭据」。
/// 一次几十台,每台弹一个框就是一摞没人点的模态框,而巡检的意义正是「先看一眼哪几台有问题」。
/// 「验证登录」可以关:只查到 SSH 握手,不往服务器上登录(有的环境每次登录都会告警)。
/// </para>
/// <para>
/// 最多 <see cref="Concurrency" /> 台同时查(与整组打开同一口径),每台最多 <see cref="PerHostTimeout" />;
/// 「停止」取消还没查完的。非 SSH / SFTP 的配置列在表里、标为不查。
/// </para>
/// </remarks>
public sealed class SessionHealthCheckViewModel : ReactiveObject
{
    /// <summary>同时在查的最多几台,与整组打开(<see cref="MainWindowViewModel.OpenManyConcurrency" />)一致。</summary>
    public const int Concurrency = MainWindowViewModel.OpenManyConcurrency;

    /// <summary>一台最多查多久;到点记「超时」,不拖住整批。</summary>
    public static readonly TimeSpan PerHostTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CopyFeedbackDuration = TimeSpan.FromSeconds(1.5);

    private readonly IConnectionDiagnosticsService _diagnostics;
    private readonly ISessionRepository? _repository;
    private readonly TimeSpan _perHostTimeout;
    private CancellationTokenSource? _run;
    private IDisposable? _copyFeedbackReset;

    /// <summary>装配巡检视图模型。</summary>
    /// <param name="diagnostics">四步诊断。</param>
    /// <param name="repository">会话库:重查一台之前取最新的配置;为 null 时沿用手里那份。</param>
    /// <param name="perHostTimeout">一台最多查多久;不给用 <see cref="PerHostTimeout" />(测试用)。</param>
    public SessionHealthCheckViewModel(
        IConnectionDiagnosticsService diagnostics,
        ISessionRepository? repository = null,
        TimeSpan? perHostTimeout = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _repository = repository;
        _perHostTimeout = perHostTimeout ?? PerHostTimeout;
        IObservable<bool> idle = this.WhenAnyValue(x => x.IsRunning, running => !running);
        StopCommand = ReactiveCommand.Create(Stop, this.WhenAnyValue(x => x.IsRunning));
        RerunCommand = ReactiveCommand.CreateFromTask(RunAsync, idle);
        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync, this.WhenAnyValue(x => x.HasRows));
        ToggleProblemsOnlyCommand = ReactiveCommand.Create(() => { ShowProblemsOnly = !ShowProblemsOnly; });
        ToggleVerifyLoginCommand = ReactiveCommand.Create(() => { VerifyLogin = !VerifyLogin; });
        DiagnoseCommand = ReactiveCommand.Create<SessionHealthRowViewModel>(row => DiagnoseRequested?.Invoke(row.Profile));
        EditCommand = ReactiveCommand.Create<SessionHealthRowViewModel>(row => EditRequested?.Invoke(row.Profile));
    }

    /// <summary>全部行(查的顺序)。</summary>
    public ObservableCollection<SessionHealthRowViewModel> Rows { get; } = [];

    /// <summary>表上显示的行:「只看有问题的」打开时只留要处理的与连不上的。</summary>
    public ObservableCollection<SessionHealthRowViewModel> VisibleRows { get; } = [];

    /// <summary>有没有行。</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>底部那句说明:巡检做了什么、没做什么。</summary>
    public string Hint => Strings.Format("Health_Hint", Concurrency, (int)_perHostTimeout.TotalSeconds);

    /// <summary>只看有问题的。</summary>
    public bool ShowProblemsOnly
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ProblemsOnlyText));
            RefreshVisibleRows();
        }
    }

    /// <summary>「只看有问题的」开关的状态说明(悬停提示第一行)。</summary>
    public string ProblemsOnlyText => Strings.Get(ShowProblemsOnly ? "Health_ProblemsOnlyOn" : "Health_ProblemsOnlyOff");

    /// <summary>切换「只看有问题的」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleProblemsOnlyCommand { get; }

    /// <summary>「验证登录」开关的状态说明(悬停提示第一行)。</summary>
    public string VerifyLoginText => Strings.Get(VerifyLogin ? "Health_VerifyLoginOn" : "Health_VerifyLoginOff");

    /// <summary>切换「验证登录」(下一轮生效)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleVerifyLoginCommand { get; }

    /// <summary>
    /// 是否走到认证那一步(用已保存的凭据登录一次随即断开)。关着时只查到 SSH 握手,不往服务器上登录。
    /// 改了下一轮生效;这次启动里记住(关掉窗口再开沿用),不进设置。
    /// </summary>
    public bool VerifyLogin
    {
        get => _rememberedVerifyLogin;
        set
        {
            if (_rememberedVerifyLogin != value)
            {
                _rememberedVerifyLogin = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(VerifyLoginText));
            }
        }
    }

    private static bool _rememberedVerifyLogin = true;

    /// <summary>正在巡检。</summary>
    public bool IsRunning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>「N 个连接 · 正常 a · 需要处理 b · 连不上 c」,查着时前面加进度。</summary>
    public string Summary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>「只看有问题的」打开、而一个问题都没有时的那句话。</summary>
    public bool ShowNoProblems => ShowProblemsOnly && VisibleRows.Count == 0 && HasRows && !IsRunning;

    /// <summary>停下巡检(还没查完的记「已停止」)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> StopCommand { get; }

    /// <summary>重新巡检这一批。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RerunCommand { get; }

    /// <summary>把整张表复制为文本(制表符分隔,贴进表格或工单)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyCommand { get; }

    /// <summary>行上的「连接诊断」。</summary>
    public ReactiveCommand<SessionHealthRowViewModel, RxVoid> DiagnoseCommand { get; }

    /// <summary>行上的「编辑配置」。</summary>
    public ReactiveCommand<SessionHealthRowViewModel, RxVoid> EditCommand { get; }

    /// <summary>打开某一台的连接诊断(由窗口接上)。</summary>
    public event Action<SessionProfile>? DiagnoseRequested;

    /// <summary>编辑某一台的配置(由窗口接上;编辑完重查那一台)。</summary>
    public event Action<SessionProfile>? EditRequested;

    /// <summary>写剪贴板(由窗口注入)。</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>刚复制过;按钮短暂显示一个勾。</summary>
    public bool Copied
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>换一批机器并开始巡检(正在巡检时不接,返回 false)。</summary>
    /// <param name="profiles">要查的配置(按这个顺序列表)。</param>
    public bool TryStart(IReadOnlyList<SessionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (IsRunning)
        {
            return false;
        }
        Rows.Clear();
        foreach (SessionProfile profile in profiles.DistinctBy(p => p.Id))
        {
            Rows.Add(new(profile));
        }
        this.RaisePropertyChanged(nameof(HasRows));
        RefreshVisibleRows();
        _ = RunAsync(CancellationToken.None);
        return true;
    }

    /// <summary>对当前这一批跑一轮(<see cref="RerunCommand" /> 与 <see cref="TryStart" /> 共用)。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (IsRunning || Rows.Count == 0)
        {
            return;
        }
        IsRunning = true;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _run = run;
        bool verifyLogin = VerifyLogin;
        try
        {
            foreach (SessionHealthRowViewModel row in Rows)
            {
                row.Reset();
            }
            RefreshSummary();
            await ReloadProfilesAsync().ConfigureAwait(true);
            using var gate = new SemaphoreSlim(Concurrency);
            await Task.WhenAll(Rows.ToList().Select(row => CheckRowAsync(row, gate, verifyLogin, run.Token))).ConfigureAwait(true);
        }
        finally
        {
            _run = null;
            IsRunning = false;
            RefreshVisibleRows();
            RefreshSummary();
        }
    }

    /// <summary>重查一台(编辑过它的配置之后):先从库里取最新的那份;正在整批巡检时不插队。</summary>
    /// <param name="profileId">那一台的配置 Id。</param>
    public async Task RecheckAsync(Guid profileId)
    {
        if (IsRunning || Rows.FirstOrDefault(r => r.Profile.Id == profileId) is not { } row)
        {
            return;
        }
        if (_repository is not null)
        {
            try
            {
                if (await _repository.GetSessionAsync(profileId).ConfigureAwait(true) is { } latest)
                {
                    row.Profile = latest;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Trace.WriteLine($"[HealthCheck] 读取配置 {profileId} 失败:{ex.Message}");
            }
        }
        IsRunning = true;
        using var run = new CancellationTokenSource();
        _run = run;
        try
        {
            using var gate = new SemaphoreSlim(1);
            await CheckRowAsync(row, gate, VerifyLogin, run.Token).ConfigureAwait(true);
        }
        finally
        {
            _run = null;
            IsRunning = false;
            RefreshVisibleRows();
            RefreshSummary();
        }
    }

    /// <summary>
    /// 每一轮开查之前把每行的配置换成库里最新的那份(行还是那一行);库里已经没有的(删掉了)从表上拿掉。
    /// </summary>
    /// <remarks>
    /// 行手里的是开始巡检时交进来的那份对象(资源管理器的树节点、命令面板读出来的一批),库每读一次都给新对象,
    /// 别处改了配置不会落到它身上:在这一行的「连接诊断」里一键放开了算法、在主窗口里改了配置,
    /// 「重新巡检」曾经照旧拿老配置去查,同一处又失败一次,要关掉窗口重开才查得到改过的。
    /// 读不出来时沿用手里那份(与 <see cref="RecheckAsync" /> 同一个口径),不让一次读库失败掀掉整批。
    /// </remarks>
    private async Task ReloadProfilesAsync()
    {
        if (_repository is null)
        {
            return;
        }
        bool removed = false;
        foreach (SessionHealthRowViewModel row in Rows.ToList())
        {
            try
            {
                if (await _repository.GetSessionAsync(row.Profile.Id).ConfigureAwait(true) is { } latest)
                {
                    row.Profile = latest;
                }
                else
                {
                    Rows.Remove(row);
                    removed = true;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Trace.WriteLine($"[HealthCheck] 读取配置 {row.Profile.Id} 失败:{ex.Message}");
            }
        }
        if (removed)
        {
            this.RaisePropertyChanged(nameof(HasRows));
            RefreshVisibleRows();
            RefreshSummary();
        }
    }

    /// <summary>停下巡检。</summary>
    public void Stop() => _run?.Cancel();

    /// <summary>窗口关掉时调:还在查的一并取消。</summary>
    public void Close() => Stop();

    private async Task CheckRowAsync(SessionHealthRowViewModel row, SemaphoreSlim gate, bool verifyLogin, CancellationToken runToken)
    {
        if (row.Profile.ConnectionType is not (ConnectionType.SSH or ConnectionType.SFTP))
        {
            row.Apply(new(SessionHealthStatus.Unsupported, null, Strings.Get("Health_UnsupportedDetail"), null));
            RefreshSummary();
            return;
        }
        try
        {
            await gate.WaitAsync(runToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            row.Apply(new(SessionHealthStatus.Cancelled, null, Strings.Get("Health_NotChecked"), null));
            RefreshSummary();
            return;
        }
        if (runToken.IsCancellationRequested)
        {
            // 停下的那一刻,别的行正好放出一个名额、先给了这一行:不再开始新的一台。
            gate.Release();
            row.Apply(new(SessionHealthStatus.Cancelled, null, Strings.Get("Health_NotChecked"), null));
            RefreshSummary();
            return;
        }
        try
        {
            int attempt = row.BeginCheck();
            RefreshSummary();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            deadline.CancelAfter(_perHostTimeout);
            var stopwatch = Stopwatch.StartNew();
            // Progress<T> 在 UI 线程上建,回调封送回 UI;晚到的(这一台已经有结论之后)按 attempt 丢掉。
            var progress = new Progress<DiagnosticStepUpdate>(update =>
            {
                if (update.Status == DiagnosticStepStatus.Running)
                {
                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        row.ReportStep(attempt, update.Name);
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() => row.ReportStep(attempt, update.Name));
                    }
                }
            });
            try
            {
                DiagnosticReport report = await _diagnostics
                                                .DiagnoseUnattendedAsync(row.Profile, verifyLogin, progress, deadline.Token)
                                                .ConfigureAwait(true);
                row.Apply(SessionHealthClassifier.Classify(report, stopwatch.ElapsedMilliseconds));
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                row.Apply(new(SessionHealthStatus.Cancelled, row.Step, Strings.Get("Health_Stopped"), stopwatch.ElapsedMilliseconds));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                row.Apply(new(SessionHealthStatus.TimedOut, row.Step,
                    Strings.Format("Health_TimedOutDetail", (int)_perHostTimeout.TotalSeconds), stopwatch.ElapsedMilliseconds));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 诊断本身出了岔子(读库失败之类):照「连不上」记,原因如实写出来,别让一台掀掉整批。
                row.Apply(new(SessionHealthStatus.Failed, row.Step, ex.Message, stopwatch.ElapsedMilliseconds));
            }
        }
        finally
        {
            gate.Release();
            RefreshSummary();
            if (ShowProblemsOnly)
            {
                RefreshVisibleRows();
            }
        }
    }

    private void RefreshVisibleRows()
    {
        VisibleRows.Clear();
        foreach (SessionHealthRowViewModel row in Rows)
        {
            if (!ShowProblemsOnly || row.IsProblem)
            {
                VisibleRows.Add(row);
            }
        }
        this.RaisePropertyChanged(nameof(ShowNoProblems));
    }

    private void RefreshSummary()
    {
        int total = Rows.Count;
        int healthy = Rows.Count(r => r.IsHealthy);
        int warning = Rows.Count(r => r.IsWarning);
        int failed = Rows.Count(r => r.IsFailed);
        string counts = Strings.Format("Health_Summary", total, healthy, warning, failed);
        int done = Rows.Count(r => r.IsDone);
        Summary = IsRunning ? Strings.Format("Health_SummaryRunning", done, total, counts) : counts;
        this.RaisePropertyChanged(nameof(ShowNoProblems));
    }

    /// <summary>整张表的文本:表头 + 每台一行,制表符分隔。</summary>
    public string BuildReportText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join('\t',
            Strings.Get("Health_ColName"), Strings.Get("Health_ColTarget"), Strings.Get("Health_ColStatus"),
            Strings.Get("Health_ColStep"), Strings.Get("Health_ColDetail"), Strings.Get("Health_ColElapsed")));
        foreach (SessionHealthRowViewModel row in Rows)
        {
            builder.AppendLine(string.Join('\t', row.Name, row.Target, row.StatusText, row.Step ?? string.Empty,
                (row.Detail ?? string.Empty).ReplaceLineEndings(" "), row.ElapsedText));
        }
        return builder.ToString();
    }

    private async Task CopyAsync()
    {
        if (CopyToClipboard is not { } copy || !HasRows)
        {
            return;
        }
        await copy(BuildReportText()).ConfigureAwait(true);
        Copied = true;
        _copyFeedbackReset?.Dispose();
        _copyFeedbackReset = DispatcherTimer.RunOnce(() => Copied = false, CopyFeedbackDuration);
    }
}
