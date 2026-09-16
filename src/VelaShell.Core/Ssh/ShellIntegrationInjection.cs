namespace VelaShell.Core.Ssh;

/// <summary>
/// 一次目录上报脚本的注入:要发的整行,以及「这一行跑完了」的哨兵。
/// </summary>
/// <param name="CommandLine">
/// 发给远端交互式 shell 的整行(不含前导空格与换行,那两样由发送方加)。
/// </param>
/// <param name="Sentinel">
/// 注入窗口的闭合信号,<b>整条转义序列</b>(<c>ESC ] 633 ; P ; VelaShell=1a2b3c4d BEL</c>)。
/// 它由被注入的那一行自己打印出来,因此<b>窗口的边界完全由我们这一行决定</b> ——
/// 用户的 rc、用户配的启动命令、对端恰好也在发的别家集成序列,都不可能提前关掉它,
/// 也不会被它吞掉。
/// <para>
/// <b>存整条序列而不是只存 <c>VelaShell=…</c> 那一截</b>:窗口是"从哨兵结束处恢复放行"的,
/// 只认中间那一截就会把开头的 <c>ESC ] 633 ; P ;</c> 吞掉、却把结尾的 BEL 放出去 ——
/// 解析器没在 OSC 状态里,那个 BEL 就是一声真响的铃。
/// </para>
/// </param>
/// <remarks>
/// 把这两样绑在一起传,而不是让调用方各自拼:它们必须严格对应,拆开传迟早会出现
/// "发的是这一行、等的是上一行的哨兵" —— 那种错误的表现是窗口永远等到超时,
/// 屏幕黑两秒之后把注入的全部副作用一股脑吐出来,正是这个机制要避免的。
/// </remarks>
public readonly record struct ShellIntegrationInjection(string CommandLine, string Sentinel)
{
    /// <summary>什么都不注入(探不出 shell 种类、或用户关掉了「上报终端工作目录」)。</summary>
    public static ShellIntegrationInjection None { get; } = new(string.Empty, string.Empty);

    /// <summary>是不是"什么都不发"。</summary>
    public bool IsEmpty => CommandLine.Length == 0;
}
