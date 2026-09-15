namespace VelaShell.Core.Ssh;

/// <summary>
/// 把一条命令组装成「静默注入」:摘历史前缀 + 窗口哨兵 + 命令本体。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么注入需要一个自带的哨兵,而不是去匹配回显。</b>注入是作为击键写进 PTY 的,
/// 对端会把它原样回显 —— 早先的做法是逐字节匹配"回显应该长什么样"再剥掉,而那个前提
/// 只在<b>短命令</b>上成立。一旦整行超出终端宽度,各家 shell 的折行重绘就各显神通:
/// ash / dash 插一对 <c>CR LF</c>,zsh 用 <c>CR</c> + <c>ESC[K</c> 重绘并<b>重复</b>断点字符,
/// fish 干脆整行按列重排、插进一堆光标移动 —— 回显里连原文都拼不回来。
/// 真机上三种全撞到了(<c>ShellIntegrationDockerTests</c>)。
/// </para>
/// <para>
/// 而"超出宽度"并不是罕见情况:目录上报脚本本身就有八九百字符,
/// 光是 <see cref="ShellHistoryScrub" /> 那段前缀就两百多 —— 连
/// <c>echo hello</c> 这种命令加上前缀之后也会折行。
/// </para>
/// <para>
/// <b>于是改成:让注入自己报到。</b>整行里埋一条带随机 nonce 的哨兵序列,终端从写下这一行起
/// 整段扣住输出,看见哨兵才恢复放行(见 <c>EchoSuppressor.OpenWindow</c>)。回显长什么样
/// 不再重要 —— 反正整段都不上屏。
/// </para>
/// <para>
/// <b>哨兵放在命令前面还是后面,决定了"藏什么"。</b>这是本类唯一的旋钮:
/// </para>
/// <list type="bullet">
/// <item><b>放后面</b>(<c>hidden</c>)—— 命令跑完才报到,于是它的输出与报错<b>一起被藏掉</b>。
/// 只有我们自己注的目录上报脚本该这样:用户没敲过它,凭什么为它的报错买单。</item>
/// <item><b>放前面</b>(<c>visible</c>)—— 先报到再执行,于是只有回显被藏,
/// 命令的输出原样显示。用户配的「连接后执行命令」「认证后执行命令」「初始目录」都走这条。</item>
/// </list>
/// <para>
/// 两件事因此被干净地分开了:<b>我们的注入藏得干干净净,用户的命令一个字节都不少。</b>
/// </para>
/// </remarks>
public static class SilentCommand
{
    /// <summary>
    /// 这种 shell 能不能用哨兵(要求它有 <c>printf</c>,也就是得是 POSIX 家族或 fish)。
    /// </summary>
    /// <remarks>
    /// 探不出种类、或对端是 cmd.exe / PowerShell 时返回 false:那上面 <c>printf</c> 未必存在,
    /// 哨兵永远不会回来,窗口只能白等到超时。调用方这时退回老办法(按回显匹配的抑制针)——
    /// 那条路对<b>短</b>命令仍然成立,而这些场景下我们本来也只发用户自己那条短命令。
    /// </remarks>
    public static bool SupportsSentinel(RemoteShellKind kind) =>
        kind is RemoteShellKind.Bash or RemoteShellKind.Zsh or RemoteShellKind.Fish or RemoteShellKind.PosixSh;

    /// <summary>
    /// 组装一条静默注入。
    /// </summary>
    /// <param name="kind">对端 shell 种类;决定要不要接摘历史前缀、以及能不能用哨兵。</param>
    /// <param name="hidden">要<b>连输出一起藏掉</b>的部分(排在哨兵之前);内置脚本走这里。</param>
    /// <param name="visible">输出<b>必须显示</b>的部分(排在哨兵之后);用户的命令走这里。</param>
    /// <returns>
    /// 可直接发给 PTY 的整行(不含前导空格与换行),以及窗口的闭合哨兵。
    /// 两段都空则返回 <see cref="ShellIntegrationInjection.None" />。
    /// </returns>
    /// <remarks>
    /// <b>摘历史前缀永远排在最前面。</b>它得赶在 <c>PROMPT_COMMAND</c>(有人在那儿挂
    /// <c>history -a</c>)之前把这一行从历史里摘掉;排在前面还顺带保住了退出码 ——
    /// 整行最终的 <c>$?</c> 由后面真正的命令决定,而不是被收尾动作抹成 0。
    /// </remarks>
    public static ShellIntegrationInjection Build(RemoteShellKind kind, string? hidden, string? visible)
    {
        string before = hidden?.Trim() ?? string.Empty;
        string after = visible?.Trim() ?? string.Empty;
        if (before.Length == 0 && after.Length == 0)
        {
            return ShellIntegrationInjection.None;
        }
        if (!SupportsSentinel(kind))
        {
            // 没法用哨兵:拼成一行发,由调用方退回抑制针。顺序不变,只是少了那道窗口。
            string plain = Join(before, after);
            return new(WithScrub(kind, plain), string.Empty);
        }
        string token = ShellIntegrationScript.NewNonce();
        string marker = $"printf '\\033]633;P;{ShellIntegrationScript.SentinelKey}={token}\\007'";
        string line = WithScrub(kind, Join(Join(before, marker), after));
        return new(line, $"\e]633;P;{ShellIntegrationScript.SentinelKey}={token}\a");
    }

    /// <summary>按 shell 决定要不要接摘历史前缀(fish / 非 POSIX 一律不接,理由见那边)。</summary>
    private static string WithScrub(RemoteShellKind kind, string command) =>
        ShellHistoryScrub.SupportedBy(kind) ? ShellHistoryScrub.Prepend(command) : command;

    /// <summary>用 <c>; </c> 接起来,空段不留下多余的分号。</summary>
    private static string Join(string left, string right) =>
        left.Length == 0 ? right : right.Length == 0 ? left : left + "; " + right;
}
