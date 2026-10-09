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
/// </remarks>
internal static class HeadlessPump
{
    private const int MillisecondsPerRound = 5;
    private const int MinimumPasses = 3;

    public static async Task RunAsync(int rounds)
    {
        long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * rounds * MillisecondsPerRound / 1000);
        int passes = Math.Min(rounds, MinimumPasses);
        for (int i = 0; i < passes || (rounds > 0 && Stopwatch.GetTimestamp() < deadline); i++)
        {
            await Task.Delay(MillisecondsPerRound);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
