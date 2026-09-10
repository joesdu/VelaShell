namespace VelaShell.Core.Ssh;

/// <summary>
/// 静默注入的收尾:让被注入的那一行不留在远端 shell 的命令历史里。
/// </summary>
/// <remarks>
/// <para>
/// 注入的命令在屏幕上是隐形的(<c>SshTerminalBridge.SuppressEchoOnce</c> 把回显剥掉了),
/// 在历史里却不是:用户登进去按一下方向键,迎面就是一整行
/// <c>test -n "${BASH_VERSION:-}" &amp;&amp; eval '…'</c> —— 那不是他敲的,他也不知道那是什么,
/// 第一反应是「谁往我服务器上注了东西」。屏幕上隐形、历史里现形,这本身就是自相矛盾的。
/// </para>
/// <para>
/// <b>为什么前导空格不够。</b>注入一直带着一个前导空格,那是 <c>HISTCONTROL=ignorespace</c>
/// 的老办法 —— 可 <c>HISTCONTROL</c> 默认<b>是空的</b>,绝大多数机器上那个空格什么也没做。
/// 它仍然留着(配了的人本就该受益),但不能当成防线。
/// </para>
/// <para>
/// <b>怎么摘。</b>bash 在<b>读到</b>这一行时就把它记进了历史,所以命令跑起来的时候
/// 「最后一条」正是自己 —— <c>history -d</c> 把它删掉即可,而且要赶在
/// <c>PROMPT_COMMAND</c> 之前(有人在那儿挂 <c>history -a</c> 往文件里追加),
/// 所以这段是<b>前置</b>在注入行开头的,不是缀在末尾:
/// </para>
/// <list type="bullet">
/// <item>前置还顺带保住了退出码 —— 这一行最终的 <c>$?</c> 由用户自己那条命令决定,
/// 而不是被收尾动作抹成 0。提示符(starship / p10k)是会把它画出来的。</item>
/// <item>删之前先<b>认一认</b>:只有当最后一条历史里出现 <see cref="Marker" /> 才动手。
/// 少了这一步,遇上真配了 <c>ignorespace</c> 的用户 —— 那时我们这行<b>根本没进历史</b> ——
/// 删掉的就是人家上一条真命令。删错用户的历史比留下一行噪音严重得多。</item>
/// <item>标记就是那个临时变量名本身,因此它必然出现在被记下的那一行里,不必另外埋记号。</item>
/// </list>
/// <para>
/// <b>整段仍旧包在 <c>eval '…'</c> 里,由 <c>BASH_VERSION</c> 守卫。</b>理由与目录上报钩子
/// 逐字相同(见 <c>MainWindowViewModel.WorkingDirectoryReportHook</c>):shell 先把整行解析完
/// 再执行,裸写的 <c>case</c>/<c>${var//}</c> 会让 fish 在<b>解析阶段</b>就报错,那时守卫还没
/// 来得及短路。zsh 没有 <c>history -d</c>,守卫同样把它挡在外面 —— 代价是 zsh 上那一行仍会
/// 留在历史里,但那一行在 zsh 上本来就是个空操作(该做的是干脆别注入,另说)。
/// </para>
/// </remarks>
public static class ShellHistoryScrub
{
    /// <summary>
    /// 历史里认自己用的记号。它同时是那个临时变量的名字,因此一定出现在被记下的那一行中。
    /// </summary>
    public const string Marker = "__vela_hist_scrub";

    /// <summary>
    /// 摘掉「最后一条历史」的那段 bash 代码(非 bash 一律短路,一个字节都不执行)。
    /// </summary>
    /// <remarks>
    /// <c>HISTTIMEFORMAT=</c> 这个前缀不能省:用户设了它之后 <c>history 1</c> 会在序号后面
    /// 多打一列时间戳,而下面要按「开头的数字」取序号。前缀只对这一次调用生效,不动用户的设置。
    /// 取序号用参数展开而不是 <c>set -- $line</c>:后者会对历史内容做通配展开,
    /// 一条含 <c>*</c> 的命令能把它变成一串文件名。
    /// </remarks>
    public const string Command =
        """
        test -n "${BASH_VERSION:-}" && eval '__vela_hist_scrub=$(HISTTIMEFORMAT= builtin history 1); __vela_hist_scrub=${__vela_hist_scrub#"${__vela_hist_scrub%%[![:space:]]*}"}; case "$__vela_hist_scrub" in *__vela_hist_scrub*) builtin history -d "${__vela_hist_scrub%%[![:digit:]]*}";; esac; unset __vela_hist_scrub'
        """;

    /// <summary>
    /// 把摘历史那段接在注入命令<b>前面</b>,返回可直接发给 PTY 的一整行。
    /// </summary>
    /// <param name="command">要静默执行的命令;空白则原样返回(没有命令就没有历史要摘)。</param>
    /// <returns>带摘历史前缀的整行,或原串。</returns>
    public static string Prepend(string command) =>
        string.IsNullOrWhiteSpace(command) ? command : $"{Command}; {command}";
}
