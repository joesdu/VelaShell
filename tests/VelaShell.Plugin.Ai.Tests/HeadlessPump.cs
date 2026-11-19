using System.Diagnostics;
using Avalonia.Threading;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// UI 用例「跑几拍调度器,让异步链落定」的共用实现:按真实时间等,不按拍数等。
/// </summary>
/// <remarks>
/// 原先各处都是 <c>rounds</c> 次 <c>await Task.Delay(5)</c> + <c>RunJobs</c>,实际等多久取决于平台计时器的粒度。
/// 同样 60 拍,CI 上 Linux 约 0.36 秒、Windows 约 0.77 秒、macOS 约 1.55 秒(每拍约 26 ms)。这个工程的用例串行跑,
/// 1400 多条在 macOS 上因此要 22 分钟以上,看上去像是 CI 永远跑不完。
/// 用例要的其实是一段真实时间(盖过 @ 补全 180 ms 的防抖、让后台的解析与读写跑完),所以这里保证过去
/// <c>rounds × 5 ms</c> 的真实时间,中间照常跑调度器,拍数随计时器的粒度自适应 —— Linux 上与原来基本一样。
/// 小的拍数(1–3)有的用例拿来「只推几拍」(比如防抖到点之前断言还没弹出),所以至少推 min(rounds, 3) 拍。
/// <para>
/// 每拍保证的时间从 5 ms 降到 2 ms(2026-11-19):实测 5 ms 时整套 UI 用例有六成时间花在这里空等,
/// 降到 1.5 ms 也只有两条要盖过 @ 补全防抖的用例失败(它们已改为 <see cref="UntilAsync" /> 等条件成立)。
/// 2 ms 比实测底线多留了余量;要等防抖、计时器这类「必须过去一段时间」的事,用 <see cref="UntilAsync" /> 等结果,别靠拍数凑。
/// 慢机器上若出现偶发失败,设环境变量 <c>VELASHELL_TEST_PUMP_SCALE</c>(例如 <c>2.5</c> 即回到原来的 5 ms)整体放大,不必改代码。
/// </para>
/// </remarks>
internal static class HeadlessPump
{
    /// <summary>每拍保证过去的真实时间(毫秒),已乘上 <c>VELASHELL_TEST_PUMP_SCALE</c>。</summary>
    private static readonly double MillisecondsPerRound = 2 * PumpScale();

    /// <summary>每推一拍之间让出的时间;只决定推得多勤,总时长由 <see cref="MillisecondsPerRound" /> 定。</summary>
    private const int PassDelayMilliseconds = 5;

    private const int MinimumPasses = 3;

    private static double PumpScale()
        => double.TryParse(Environment.GetEnvironmentVariable("VELASHELL_TEST_PUMP_SCALE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale) && scale > 0
            ? scale
            : 1;

    public static async Task UntilAsync(Func<bool> condition,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.IsLessThan(TimeSpan.FromSeconds(15), elapsed.Elapsed, $"UI condition did not become true: {expression}");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static async Task RunAsync(int rounds)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * rounds * MillisecondsPerRound / 1000);
        int passes = Math.Min(rounds, MinimumPasses);
        for (int i = 0; i < passes || (rounds > 0 && Stopwatch.GetTimestamp() < deadline); i++)
        {
            await Task.Delay(PassDelayMilliseconds);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
