namespace VelaShell.Core.Models;

/// <summary>
/// 对端 shell 的 OSC 133(命令块 / 语义提示符)集成片段 —— 给用户<b>复制到自己的 rc 文件</b>里,
/// VelaShell 不注入。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不自动注入。</b>完整的 OSC 133 要标出「命令输出从这里开始」(<c>C</c>),
/// 而那个位置只能由 <c>PS0</c> / <c>preexec</c> 给出 —— 都得动用户的提示符变量。
/// 现有的 OSC 7 钩子只碰 <c>PROMPT_COMMAND</c> 就已经踩过两次真机事故
/// (pyenv 的 <c>;;</c> 语法错误、fish 解析阶段就炸),而 <c>PS1</c> 会被 starship /
/// oh-my-posh / powerlevel10k <b>每次画提示符都重写</b>,爆炸半径大得多。
/// 这些片段因此是「摆出来给你抄」,装不装、装在哪由用户决定。
/// </para>
/// <para>
/// <b>bash 这段刻意不用 <c>DEBUG</c> trap。</b>教科书写法是拿 DEBUG trap 发 <c>C</c>,
/// 但那个 trap 全局只有一个,和 bash-preexec / direnv 这类工具直接抢;而且它对
/// <c>PROMPT_COMMAND</c> 里的每条钩子也会触发 —— 用户只要另有一个 <c>PROMPT_COMMAND</c> 钩子,
/// <c>C</c> 就会被那个钩子抢走、落在提示符上而不是命令上,真命令的输出反而没了标记
/// (本机拿真 bash 复现过)。改用 <c>PS0</c>(bash 4.4+,读完命令、执行之前打印)之后,
/// 语义正好就是 <c>C</c>,一个 trap 都不用装。
/// </para>
/// <para>
/// <b>退出码由 <c>PROMPT_COMMAND</c> 抓、由 <c>PS1</c> 发。</b><c>PS1</c> 在全部
/// <c>PROMPT_COMMAND</c> 钩子跑完之后才展开,那时 <c>$?</c> 已经是最后一个钩子的状态、
/// 不再是用户命令的。所以把抓取函数插在 <c>PROMPT_COMMAND</c> <b>最前面</b>存下 <c>$?</c>,
/// <c>PS1</c> 里再把它发出去 —— 这样 <c>D</c> 也天然排在 <c>A</c> 之前。
/// </para>
/// <para>
/// 三段都写成<b>可重复执行</b>的:每段各自 <c>case</c> 判一下装没装过,重连 / 多开标签 /
/// 反复 source 都不会叠加。
/// </para>
/// </remarks>
public static class ShellIntegration
{
    /// <summary>bash <b>4.4 及以上</b>(需要 <c>PS0</c>)。</summary>
    /// <remarks>
    /// <para>
    /// 已在真 bash 上逐条验过:<c>PS0</c>/<c>PS1</c> 用 bash 自己的提示符展开(<c>${VAR@P}</c>)
    /// 取出实际会打印的字节,<c>PROMPT_COMMAND</c> 比对最终值。见 <c>ShellIntegrationShellTests</c>。
    /// </para>
    /// <para>
    /// <b>4.4 这条下限不是随口写的。</b><c>PS0</c> 正是 4.4 引入的,而 <b>macOS 自带的
    /// <c>/bin/bash</c> 至今是 3.2</b>(Apple 为躲 GPLv3 冻在那儿)—— 在那上面这段一声不响地
    /// 什么也不做。片段头两行因此把这件事直接写给用户看,而不是只留在这里。
    /// CI 的 macOS runner 也是这么翻车的:<c>ShellIntegrationShellTests</c> 现在改用
    /// <c>BashProbe.FindSupportingPs0</c>,找不到 4.4 就如实跳过而不是假装通过。
    /// </para>
    /// </remarks>
    public const string Bash =
        """
        # VelaShell shell integration (OSC 133) -- needs bash 4.4+ (PS0)
        # macOS ships bash 3.2; `brew install bash` or use the zsh snippet instead.
        case "${PS0-}" in *'133;C'*) ;; *) PS0='\033]133;C\033\\'"${PS0-}" ;; esac
        case "${PS1-}" in
          *'133;A'*) ;;
          *) PS1='\[\033]133;D;${__vela_osc133_status:-0}\033\\\033]133;A\033\\\]'"${PS1-}"'\[\033]133;B\033\\\]' ;;
        esac
        __vela_osc133_status_capture() { __vela_osc133_status=$?; }
        case "${PROMPT_COMMAND-}" in
          *__vela_osc133_status_capture*) ;;
          *) PROMPT_COMMAND="__vela_osc133_status_capture${PROMPT_COMMAND:+;$PROMPT_COMMAND}" ;;
        esac
        """;

    /// <summary>zsh(用其原生的 <c>precmd</c> / <c>preexec</c> 钩子,不必碰 <c>PS0</c>)。</summary>
    /// <remarks>
    /// zsh 自带 <c>add-zsh-hook</c>,多个钩子各自独立注册、互不覆盖 —— 没有 bash 那边
    /// 「一个 <c>PROMPT_COMMAND</c> 字符串你争我抢」的问题,所以这段简单得多。
    /// <c>B</c>(提示符结束)接在 <c>PS1</c> 末尾。
    /// </remarks>
    public const string Zsh =
        """
        # VelaShell shell integration (OSC 133)
        autoload -Uz add-zsh-hook
        __vela_osc133_precmd()  { printf '\033]133;D;%s\033\\\033]133;A\033\\' "$?" }
        __vela_osc133_preexec() { printf '\033]133;C\033\\' }
        add-zsh-hook precmd  __vela_osc133_precmd
        add-zsh-hook preexec __vela_osc133_preexec
        [[ "$PS1" == *'133;B'* ]] || PS1="$PS1"$'%{\033]133;B\033\\\\%}'
        """;
}
