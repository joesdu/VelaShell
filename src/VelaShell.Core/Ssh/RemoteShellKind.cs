namespace VelaShell.Core.Ssh;

/// <summary>
/// 远端默认 shell 的种类 —— <see cref="RemoteShellProbe" /> 的结论,决定注入<b>哪一段</b>
/// 目录上报脚本(见 <c>ShellIntegrationScript</c>),或者干脆不注入。
/// </summary>
/// <remarks>
/// <para>
/// 这个枚举取代了原先那个 <c>bool isPosixShell</c>。只有"是不是 POSIX"这一位信息时,
/// 目录跟随实际上只对 bash 有效:钩子里那句 <c>test -n "$BASH_VERSION"</c> 把 zsh 与 fish
/// 一并短路掉了 —— 它们不报错,但功能<b>就是不工作</b>,而 zsh 如今是 macOS 的默认 shell、
/// 也是 oh-my-zsh 用户的全部。分清种类之后,每种 shell 各走自己的提示符钩子。
/// </para>
/// <para>
/// <b><see cref="Unknown" /> 与 <see cref="NonPosix" /> 都表示"不注入",但成因不同,
/// 因此不能合并成一个值。</b><see cref="NonPosix" /> 是**结论**(对端就是 cmd.exe /
/// PowerShell,再连一百次也一样),可以按主机缓存;<see cref="Unknown" /> 是**噪声**
/// (exec 被禁、通道开不出来、超时),下次连接值得重新问一次,绝不能进缓存。
/// </para>
/// </remarks>
public enum RemoteShellKind
{
    /// <summary>探不出来(exec 被禁、超时、连接已断)。不注入,且<b>不缓存</b>。</summary>
    Unknown = 0,

    /// <summary>对端不认 sh 语法 —— Windows OpenSSH 的 cmd.exe / PowerShell(#305)。永不注入。</summary>
    NonPosix,

    /// <summary>bash:走 <c>PROMPT_COMMAND</c>。</summary>
    Bash,

    /// <summary>zsh:走 <c>precmd_functions</c>。</summary>
    Zsh,

    /// <summary>fish:走 <c>--on-variable PWD</c>(连提示符都不必碰)。</summary>
    Fish,

    /// <summary>
    /// 其余 POSIX shell —— dash / ash(Alpine、BusyBox、OpenWrt)、ksh、mksh。
    /// 它们既没有 <c>PROMPT_COMMAND</c> 也没有 <c>precmd</c>,只能在 <c>PS1</c> 上做文章。
    /// </summary>
    PosixSh
}
