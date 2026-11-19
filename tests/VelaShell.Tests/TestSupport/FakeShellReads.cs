using NSubstitute.Core;

namespace VelaShell.Tests.TestSupport;

/// <summary>假 shell 流的 <c>ReadAsync</c>:一直没有输出,直到读取被取消 —— 和真流一样守「取消 / 释放会唤醒挂起的读取」这条约定。</summary>
/// <remarks>
/// 以前这些假流返回一个永远不完成、也不理会取消的 <c>new TaskCompletionSource&lt;int&gt;().Task</c>。拆桥时
/// <c>SshTerminalBridge</c> 先取消、再最多等读循环 2 秒(那 2 秒是给不守约定的第三方插件流兜底的),
/// 于是每次重连 / 关标签都白等满 2 秒:AllowAlgorithmsFlowTests、TerminalTabReconnectProfileTests、
/// MainWindowSshFeatureTests 一条重连用例就多 2 秒。守约定的假流在取消时立刻醒来,读循环随即退出。
/// </remarks>
internal static class FakeShellReads
{
    /// <summary>
    /// 先吐一次提示符(像真 shell 登录后那样),之后挂起到取消。每条假 shell 流各要一份(内部记着「吐过没有」)。
    /// </summary>
    /// <remarks>
    /// 启动目录 / 登录后命令的注入要等对端安静下来(<c>SshTerminalBridge.RunWhenOutputIdle</c>):对端<b>从没吐过</b>数据
    /// 就永远不算「安静」,只能等到 3 秒上限(<c>MainWindowViewModel.InjectionMaxWait</c>)才注入 —— 一条用例白等 3 秒。
    /// 真 shell 登录后总会吐提示符;吐过一次、静下来之后注入照常发生,走的是生产上的常规路径。
    /// 「对端一直不吐数据也要注入」那条兜底路径仍由 OpenTerminalHereTests 里一条保持静默的用例守着。
    /// </remarks>
    /// <param name="prompt">吐出的提示符。</param>
    public static Func<CallInfo, Task<int>> PromptThenUntilCancelled(string prompt = "$ ")
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(prompt);
        int served = 0;
        return call =>
        {
            if (Interlocked.Exchange(ref served, 1) == 0)
            {
                bytes.CopyTo(call.ArgAt<byte[]>(0), call.ArgAt<int>(1));
                return Task.FromResult(bytes.Length);
            }
            return UntilCancelled(call);
        };
    }

    /// <summary>挂起到 <c>ReadAsync</c> 的第 4 个参数(取消令牌)被取消为止。</summary>
    /// <param name="call">NSubstitute 的调用信息(<c>ReadAsync(byte[], int, int, CancellationToken)</c>)。</param>
    public static async Task<int> UntilCancelled(CallInfo call)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(3)).ConfigureAwait(false);
        return 0;
    }
}
