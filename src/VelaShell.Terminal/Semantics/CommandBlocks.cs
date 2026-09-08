using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Semantics;

/// <summary>一条命令块在缓冲区里的位置(绝对行号,含首含尾)。</summary>
/// <param name="PromptRow">提示符行(<c>OSC 133 ; A</c>)。块由它起头,退出码也记在这一行。</param>
/// <param name="OutputStart">输出首行(<c>OSC 133 ; C</c>);对端没发 C 时为 -1。</param>
/// <param name="LastRow">块的末行 —— 下一条提示符的上一行,或缓冲区末尾。</param>
/// <param name="ExitCode">命令退出码;尚未结束或对端没报时为 null。</param>
public readonly record struct CommandBlock(int PromptRow, int OutputStart, int LastRow, int? ExitCode)
{
    /// <summary>该块是否已知失败(退出码非 0)。</summary>
    public bool Failed => ExitCode is not null and not 0;

    /// <summary>该块有没有可选中的输出区间。</summary>
    public bool HasOutput => OutputStart >= 0 && LastRow >= OutputStart;
}

/// <summary>
/// 从行上的 <see cref="PromptMark" /> 推导命令块。
/// </summary>
/// <remarks>
/// <para>
/// <b>刻意不建模型、不存状态 —— 每次按需扫。</b>块的全部事实已经挂在
/// <see cref="TerminalRow.Mark" /> / <see cref="TerminalRow.ExitCode" /> 上,而行对象在滚动时
/// 按引用迁进 scrollback、在改列宽时由 <c>ReflowResize</c> 连标记一起搬 —— 也就是说,
/// <b>事实本身已经跟着内容走了</b>。再另建一张按行对象引用锚定的表(如
/// <c>GutterFoldModel</c> 那样),就得额外承担一份"什么时候作废"的心智负担,而 reflow
/// 恰恰会把那种表整体作废。
/// </para>
/// <para>
/// 扫描代价是 O(可见范围),调用点都是用户动作(按跳转键、点侧栏标记),不在每帧渲染路径上。
/// 渲染只需逐行读一个枚举,压根不经过这里。
/// </para>
/// </remarks>
public static class CommandBlocks
{
    /// <summary>
    /// 从 <paramref name="fromRow" /> 往上找最近的提示符行(不含 <paramref name="fromRow" /> 自身);
    /// 没有则返回 -1。
    /// </summary>
    public static int PreviousPrompt(TerminalScreen screen, int fromRow)
    {
        for (int abs = Math.Min(fromRow, screen.TotalRows) - 1; abs >= 0; abs--)
        {
            if (screen.ViewLine(abs).Mark == PromptMark.Prompt)
            {
                return abs;
            }
        }
        return -1;
    }

    /// <summary>
    /// 从 <paramref name="fromRow" /> 往下找最近的提示符行(不含 <paramref name="fromRow" /> 自身);
    /// 没有则返回 -1。
    /// </summary>
    public static int NextPrompt(TerminalScreen screen, int fromRow)
    {
        for (int abs = Math.Max(fromRow, -1) + 1; abs < screen.TotalRows; abs++)
        {
            if (screen.ViewLine(abs).Mark == PromptMark.Prompt)
            {
                return abs;
            }
        }
        return -1;
    }

    /// <summary>
    /// 返回包含 <paramref name="row" /> 的命令块;该行之上没有任何提示符标记时返回 null。
    /// </summary>
    /// <remarks>
    /// 落在提示符行自身时算<b>这一条</b>块(而不是上一条):用户点的是这条命令的标记,
    /// 要的显然是这条命令的输出。
    /// </remarks>
    public static CommandBlock? BlockAt(TerminalScreen screen, int row)
    {
        if (row < 0 || row >= screen.TotalRows)
        {
            return null;
        }
        int prompt = screen.ViewLine(row).Mark == PromptMark.Prompt ? row : PreviousPrompt(screen, row);
        if (prompt < 0)
        {
            return null;
        }
        int next = NextPrompt(screen, prompt);
        int last = (next < 0 ? screen.TotalRows : next) - 1;

        // 输出首行:块内第一条 C 标记。没有 C(对端只发了 A/B,或命令还没回车)时留 -1,
        // 调用方据 HasOutput 判断有没有东西可选。
        int outputStart = -1;
        for (int abs = prompt; abs <= last; abs++)
        {
            if (screen.ViewLine(abs).Mark == PromptMark.Output)
            {
                outputStart = abs;
                break;
            }
        }
        return new CommandBlock(prompt, outputStart, last, screen.ViewLine(prompt).ExitCode);
    }
}
