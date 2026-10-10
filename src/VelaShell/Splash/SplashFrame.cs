namespace VelaShell.Splash;

/// <summary>启动阶段所处的状态。</summary>
internal enum SplashStageState
{
    /// <summary>还没轮到。</summary>
    Waiting,

    /// <summary>正在进行。</summary>
    Running,

    /// <summary>已完成。</summary>
    Done
}

/// <summary>
/// 一个启动阶段在某一时刻的样子。
/// </summary>
/// <param name="State">状态。</param>
/// <param name="Seconds">已完成时是这一段的耗时,进行中是已经过去的时间,未开始为 0。</param>
/// <param name="Fraction">本段的完成度估计(0–1);已完成为 1,未开始为 0。</param>
internal readonly record struct SplashStage(SplashStageState State, double Seconds, double Fraction);

/// <summary>
/// 启动画面某一帧要画的全部状态。设计稿只从这里取数,于是同一个设计既能接真实的启动打点
/// (<see cref="SplashProgressTracker" />),也能接设置页预览用的模拟时间线
/// (<see cref="SplashSimulation" />),画出来逐像素一致。
/// </summary>
internal sealed class SplashFrame
{
    /// <summary>阶段数:运行时 → 数据库 → 界面框架 → 会话与设置 → 主窗口。</summary>
    public const int StageCount = 5;

    /// <summary>构造一帧。</summary>
    /// <param name="elapsedMs">启动画面出现以来的毫秒数(开场动画、闪烁光标等连续动画用它)。</param>
    /// <param name="stages">五个阶段的状态。</param>
    /// <param name="progress">总进度(0–1),已平滑。</param>
    public SplashFrame(double elapsedMs, SplashStage[] stages, double progress)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ElapsedMs = elapsedMs;
        Stages = stages;
        Progress = Math.Clamp(progress, 0, 1);
        Current = -1;
        for (int i = 0; i < stages.Length; i++)
        {
            if (stages[i].State == SplashStageState.Done)
            {
                DoneCount++;
            }
            else if (stages[i].State == SplashStageState.Running && Current < 0)
            {
                Current = i;
            }
        }
    }

    /// <summary>启动画面出现以来的毫秒数。</summary>
    public double ElapsedMs { get; }

    /// <summary>五个阶段的状态。</summary>
    public SplashStage[] Stages { get; }

    /// <summary>总进度(0–1)。</summary>
    public double Progress { get; }

    /// <summary>正在进行的阶段下标;没有时为 -1。</summary>
    public int Current { get; }

    /// <summary>已完成的阶段数。</summary>
    public int DoneCount { get; }

    /// <summary>是否全部完成(主窗口第一帧已画完)。</summary>
    public bool IsDone => DoneCount == Stages.Length;
}

/// <summary>
/// 把启动打点(<see cref="Infrastructure.Diagnostics.StartupTrace" />)折算成五个阶段的进度。
/// </summary>
/// <remarks>
/// <para>
/// 每个阶段由一个或几个打点宣告结束。数据库是在后台线程上与界面框架初始化并行打开的
/// (<c>StartupWarmup</c>),它的点有可能比后面阶段的点来得晚 —— 所以一个阶段的结束时刻取
/// 「它自己以及它之后任何一个阶段的打点」里最早的那个:后面的阶段都完成了,前面的不可能还没完成。
/// 这样算出来的结束时刻单调不减,每段耗时都不会是负数。
/// </para>
/// <para>
/// 进行中的那一段不知道还要多久,完成度按 <c>1 - e^(-t/τ)</c> 估计、封顶 0.95:
/// 越等越接近、永远不到头,不会出现进度条走满了却还停在那儿的情况。τ 取实测的典型耗时。
/// </para>
/// </remarks>
internal sealed class SplashProgressTracker
{
    /// <summary>宣告各阶段结束的打点名(见 <c>Program.Main</c>、<c>App</c>、<c>MainWindow</c>)。</summary>
    private static readonly string[][] StageMarks =
    [
        ["Main"],
        // 预热被环境变量关掉时没有 DbWarmup,数据库在读设置那一步才打开。
        ["DbWarmup", "Settings"],
        ["DI"],
        ["MainWindowViewModel"],
        ["FirstFrame"]
    ];

    /// <summary>各阶段的典型耗时(毫秒,取自热启动的启动打点),用作完成度估计的时间常数。</summary>
    private static readonly double[] TypicalMs = [400, 700, 1500, 500, 600];

    private readonly Lock _gate = new();
    private readonly double?[] _marks = new double?[SplashFrame.StageCount];
    private readonly ProgressSmoother _smoother = new();

    /// <summary>记一个打点(可在任意线程调用)。不认识的点忽略。</summary>
    /// <param name="name">打点名。</param>
    /// <param name="at">自进程创建起的时刻。</param>
    public void Record(string name, TimeSpan at)
    {
        for (int i = 0; i < StageMarks.Length; i++)
        {
            if (Array.IndexOf(StageMarks[i], name) < 0)
            {
                continue;
            }
            lock (_gate)
            {
                double ms = at.TotalMilliseconds;
                if (_marks[i] is not { } existing || ms < existing)
                {
                    _marks[i] = ms;
                }
            }
        }
    }

    /// <summary>取当前这一帧。</summary>
    /// <param name="nowMs">当前时刻(自进程创建起,毫秒)。</param>
    /// <param name="shownAtMs">启动画面出现的时刻(同一基准)。</param>
    /// <returns>这一帧的状态。</returns>
    public SplashFrame Snapshot(double nowMs, double shownAtMs)
    {
        double?[] marks;
        lock (_gate)
        {
            marks = (double?[])_marks.Clone();
        }
        SplashStage[] stages = Compute(marks, nowMs);
        return new SplashFrame(nowMs - shownAtMs, stages, _smoother.Next(RawProgress(stages), nowMs));
    }

    /// <summary>由各阶段自己的打点时刻算出五个阶段的状态(纯函数,便于测试)。</summary>
    /// <param name="marks">各阶段自己的打点时刻(毫秒,自进程创建起);没打过为 <see langword="null" />。</param>
    /// <param name="nowMs">当前时刻。</param>
    /// <returns>五个阶段的状态。</returns>
    internal static SplashStage[] Compute(IReadOnlyList<double?> marks, double nowMs)
    {
        int count = SplashFrame.StageCount;
        var ends = new double?[count];
        double? earliestLater = null;
        for (int i = count - 1; i >= 0; i--)
        {
            if (i < marks.Count && marks[i] is { } own && (earliestLater is null || own < earliestLater))
            {
                earliestLater = own;
            }
            ends[i] = earliestLater;
        }
        var stages = new SplashStage[count];
        double previous = 0;
        bool runningAssigned = false;
        for (int i = 0; i < count; i++)
        {
            if (ends[i] is { } end)
            {
                stages[i] = new(SplashStageState.Done, (end - previous) / 1000, 1);
                previous = end;
            }
            else if (!runningAssigned)
            {
                double elapsed = Math.Max(0, nowMs - previous);
                stages[i] = new(SplashStageState.Running, elapsed / 1000, Estimate(elapsed, TypicalMs[i]));
                runningAssigned = true;
            }
            else
            {
                stages[i] = new(SplashStageState.Waiting, 0, 0);
            }
        }
        return stages;
    }

    /// <summary>进行中阶段的完成度估计:<c>1 - e^(-t/τ)</c>,封顶 0.95。</summary>
    internal static double Estimate(double elapsedMs, double typicalMs) =>
        Math.Min(0.95, 1 - Math.Exp(-elapsedMs / typicalMs));

    /// <summary>未平滑的总进度。</summary>
    internal static double RawProgress(SplashStage[] stages)
    {
        double sum = 0;
        foreach (SplashStage stage in stages)
        {
            sum += stage.Fraction;
        }
        return sum / stages.Length;
    }
}

/// <summary>
/// 设置页预览用的模拟时间线:按一次「一般」速度的启动循环播放。
/// </summary>
/// <remarks>
/// 与真实启动保持同样的节奏感:启动画面出现时运行时那一段已经完成(画面是在 <c>Main</c> 里起来的),
/// 进行中阶段的完成度也走同一个估计式 —— 预览里看到的进度条怎么走,真实启动就怎么走。
/// </remarks>
internal sealed class SplashSimulation
{
    /// <summary>各阶段在模拟里的耗时(毫秒);第一段在画面出现前就已结束。</summary>
    private static readonly double[] DurationsMs = [420, 900, 1500, 800, 900];

    /// <summary>跑完后停留多久再重播。</summary>
    private const double HoldMs = 1600;

    private readonly ProgressSmoother _smoother = new();

    /// <summary>一个循环的总时长(毫秒)。</summary>
    public static double CycleMs { get; } = DurationsMs.Skip(1).Sum() + HoldMs;

    /// <summary>取循环内第 <paramref name="t" /> 毫秒的那一帧。</summary>
    /// <param name="t">循环内的时刻(毫秒,0 起)。</param>
    /// <returns>这一帧的状态。</returns>
    public SplashFrame At(double t)
    {
        var marks = new double?[SplashFrame.StageCount];
        double end = DurationsMs[0];
        marks[0] = end;
        for (int i = 1; i < DurationsMs.Length; i++)
        {
            end += DurationsMs[i];
            if (t + DurationsMs[0] >= end)
            {
                marks[i] = end;
            }
        }
        double now = t + DurationsMs[0];
        SplashStage[] stages = SplashProgressTracker.Compute(marks, now);
        return new SplashFrame(t, stages, _smoother.Next(SplashProgressTracker.RawProgress(stages), now));
    }
}

/// <summary>
/// 总进度的指数平滑:阶段结束时估计值会跳到 1,直接画会看到进度条与星线一顿一顿地跳。
/// 往回走(模拟循环重播)时直接落下去,不做倒退动画。
/// </summary>
internal sealed class ProgressSmoother
{
    /// <summary>平滑的时间常数(毫秒)。</summary>
    private const double TimeConstantMs = 140;

    private double _value = double.NaN;
    private double _lastMs;

    /// <summary>喂入新的目标值,返回平滑后的值。</summary>
    /// <param name="target">目标进度。</param>
    /// <param name="nowMs">当前时刻(毫秒)。</param>
    /// <returns>平滑后的进度。</returns>
    public double Next(double target, double nowMs)
    {
        if (double.IsNaN(_value) || target < _value - 0.2 || nowMs < _lastMs)
        {
            _value = target;
        }
        else
        {
            double dt = nowMs - _lastMs;
            _value += (target - _value) * (1 - Math.Exp(-dt / TimeConstantMs));
        }
        _lastMs = nowMs;
        return _value;
    }
}
