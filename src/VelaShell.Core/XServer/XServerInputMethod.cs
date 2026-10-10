using VelaShell.Core.Ssh;

namespace VelaShell.Core.XServer;

/// <summary>
/// 内置 X Server 当 XIM 输入法服务端时的约定(F5 第二步,见 <c>X11ServerOptions.InputMethodName</c>):服务端占的名字,
/// 以及连接后注入远端 shell 的那一句 <c>XMODIFIERS</c>。
/// </summary>
/// <remarks>
/// Xlib 只在 <c>XMODIFIERS</c> 里写了 <c>@im=名字</c> 时才去找输入法服务端,没写就用它自己的组合键处理 —— 远端程序看不见本机的输入法。
/// sshd 默认只放行 <c>LANG</c> / <c>LC_*</c>(<c>AcceptEnv</c>),SSH 的 env 请求带不过去,所以借连接后的静默注入在 shell 里设上。
/// 已经设了的(远端装了 fcitx / ibus,用户在 rc 里写了自己的)不动。
/// </remarks>
public static class XServerInputMethod
{
    /// <summary>输入法服务端的名字(选区 <c>@server=velashell</c>)。</summary>
    public const string Name = "velashell";

    /// <summary>远端 <c>XMODIFIERS</c> 的值。</summary>
    public const string Modifiers = "@im=" + Name;

    /// <summary>
    /// 在远端 shell 里设 <c>XMODIFIERS</c>(没设过才设)的一句,按 shell 种类写;探不出种类、对端不是 POSIX / fish 时为空串(不注入)。
    /// </summary>
    /// <param name="kind">远端 shell 的种类(<see cref="RemoteShellProbe" /> 的结论)。</param>
    public static string ShellExport(RemoteShellKind kind) => kind switch
    {
        RemoteShellKind.Bash or RemoteShellKind.Zsh or RemoteShellKind.PosixSh => $"[ -n \"${{XMODIFIERS-}}\" ] || export XMODIFIERS={Modifiers}",
        RemoteShellKind.Fish => $"set -q XMODIFIERS; or set -gx XMODIFIERS {Modifiers}",
        _ => string.Empty,
    };
}
