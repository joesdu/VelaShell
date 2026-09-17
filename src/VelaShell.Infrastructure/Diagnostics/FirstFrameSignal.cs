namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>
/// 「主窗口已经画出第一帧」这一信号。启动路径上想让路的后台活儿等它。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>:冷启动的瓶颈不是 CPU 也不是磁盘,而是 Defender 对未签名程序集的
/// 首次扫描(实测 ~27.9 ms/MB,外加每文件 ~33.5 ms 的信誉查询;详见
/// <c>src/VelaShell/VelaShell.csproj</c> 里 PublishReadyToRun 处的注释)。这类扫描发生在
/// <b>过滤驱动里</b>,几个线程同时装载程序集只会在那儿排队 —— 所以"丢进 <c>Task.Run</c>
/// 就不占启动路径"这个直觉在这里是不成立的:线程上确实不占,磁盘与 Defender 上照占不误,
/// 而首帧要的 Avalonia/Skia 程序集正排在同一条队里。
/// </para>
/// <para>
/// 已有的 <c>PluginManagerOptions.PrewarmDelay</c> 表达的是同一个诉求(注释原话:「让主窗口
/// 先把首帧画完,预读绝不与启动争磁盘」),但它用的是一个拍脑袋的 5 秒定时。这里给出真正的
/// 信号:<c>MainWindow</c> 在首帧回调里 <see cref="Signal" />,要让路的一方
/// <see cref="WaitAsync" />。热启动下首帧在 1 秒出头就到,等待几乎是零成本。
/// </para>
/// <para>
/// <b>永不吊死</b>:<see cref="WaitAsync" /> 带超时且超时不抛异常。窗口压根没开起来
/// (headless 测试、设计器、将来可能的"启动即最小化到托盘")时,等待方按超时照常往下走,
/// 行为退回成信号引入之前的样子。
/// </para>
/// </remarks>
public static class FirstFrameSignal
{
    private static readonly TaskCompletionSource Completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>首帧是否已经到达。</summary>
    public static bool HasReached => Completion.Task.IsCompleted;

    /// <summary>
    /// 标记首帧已到达。重复调用无副作用(窗口从托盘重新显示会再次触发 Opened)。
    /// </summary>
    public static void Signal() => Completion.TrySetResult();

    /// <summary>
    /// 等首帧到达,最多等 <paramref name="timeout" />。
    /// </summary>
    /// <param name="timeout">等待上限;超时不抛异常,直接返回。</param>
    /// <remarks>
    /// 超时是纯粹的保险丝,不是正常路径的一部分 —— 正常情况下首帧总会到。因此它只要
    /// "足够长到不会误伤真实的冷启动",不需要精调。
    /// </remarks>
    public static async Task WaitAsync(TimeSpan timeout)
    {
        if (Completion.Task.IsCompleted)
        {
            return;
        }
        try
        {
            await Completion.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 窗口没开起来(或开得异常慢)。等待方照常往下走,见类型注释。
        }
    }
}
