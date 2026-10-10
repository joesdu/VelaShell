namespace VelaShell.XServer.Tests.TestKit;

/// <summary>看服务端诊断日志(<see cref="X11ServerOptions.Log" />)的测试辅助。</summary>
internal static class ServerLog
{
    /// <summary>
    /// 等日志里满足 <paramref name="match" /> 的行至少有 <paramref name="atLeast" /> 条(5 秒为限),返回此刻满足条件的全部行(到点了还不够就返回已有的,
    /// 由调用方断言)。
    /// </summary>
    /// <remarks>
    /// 执行线程持锁时记的日志放锁之后才交出去(<c>X11Server.Log</c>),而回复在批次中间就写给了客户端 —— 往返一次之后马上数日志,
    /// 慢的机器上会数漏(CI 的 Ubuntu 上 GlxTests「第一次用到反馈模式时记一行日志」红过:数到 0 行)。
    /// </remarks>
    /// <param name="snapshot">取此刻全部日志行的一份拷贝(日志列表要加锁的,在这里面加)。</param>
    /// <param name="match">要等的行。</param>
    /// <param name="atLeast">至少几行。</param>
    public static async Task<string[]> WaitForAsync(Func<IEnumerable<string>> snapshot, Func<string, bool> match, int atLeast = 1)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            string[] found = [.. snapshot().Where(match)];
            if (found.Length >= atLeast || DateTime.UtcNow >= deadline)
            {
                return found;
            }
            await Task.Delay(10);
        }
    }

    /// <summary>同 <see cref="WaitForAsync(Func{IEnumerable{string}}, Func{string, bool}, int)" />,日志存在加锁的 <see cref="List{T}" /> 里。</summary>
    public static Task<string[]> WaitForAsync(List<string> log, Func<string, bool> match, int atLeast = 1) =>
        WaitForAsync(() =>
        {
            lock (log)
            {
                return [.. log];
            }
        }, match, atLeast);
}
