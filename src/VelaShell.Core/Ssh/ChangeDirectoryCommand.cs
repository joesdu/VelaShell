using System.Text;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Ssh;

/// <summary>
/// 登录后切到某个目录的那一行 <c>cd</c>(配置里的初始目录、文件栏「在此处打开终端」),按会话字符集决定怎么写。
/// </summary>
/// <remarks>
/// <para>
/// <b>这一行是当击键敲进 PTY 的</b>(静默注入,见 <see cref="SilentCommand" />),不是交给 shell 解析的字符串。
/// 带控制字符的目录名在这之前就已经拦下、不 cd(<see cref="SilentCommand.CanTypeLiterally" />,plan.md §298);
/// 这里管的是剩下的那一种:<b>目录名按会话字符集编出来有 ≥ 0x80 的字节</b>。远端是 C / POSIX 区域设置时,
/// 行编辑器可能把这样的字节当成 Meta 键:readline 的 <c>convert-meta</c> 起作用时把它拆成 ESC + 低 7 位,<c>£</c> 编成 UTF-8 是
/// <c>C2 A3</c>,<c>A3</c> 就是 M-#(insert-comment,当场提交整行);别的字节对着别的 Meta 绑定(补全、展开、改写整行……)。
/// 会不会这样因版本与配置而异 —— WSL Debian 的 bash 5.2.37 / readline 8.2 在 <c>LC_ALL=C</c>、<c>bind -v</c> 显示
/// convert-meta on 时实测仍把 <c>A3</c> 当普通字符插入;更早的 readline、vi 模式、别的 shell 的行编辑器、各人的 inputrc
/// 没法逐个排除 —— 一个精心构造的非 ASCII 目录名就仍可能把这一行改成别的命令。单引号只挡得住 shell 的展开,挡不住行编辑器。
/// </para>
/// <para>
/// <b>所以这种目录名改发只含可打印 ASCII 的写法</b>(维护者 2026-10-31 定,按推荐):
/// <c>cd "$(printf '\ooo\ooo…')"</c> —— 目录名的字节(按会话字符集编码,GBK 会话就是 GBK 的字节,不一律当 UTF-8)
/// 除字母、数字与 <c>/</c> <c>.</c> <c>_</c> 之外一律写成三位八进制转义,由远端的 <c>printf</c> 还原成原来那串字节交给 <c>cd</c>。
/// 留原样的那几个在三层里都不带任何含义:双引号、命令替换、<c>printf</c> 的单引号格式串;其余的各有讲究 ——
/// <c>\</c> 与 <c>%</c> 是格式串的语法,<c>'</c> 会收掉单引号,<c>!</c> 在 bash / zsh 的双引号里会触发历史展开,
/// 打头的 <c>-</c> 会被 <c>printf</c> 当成选项,<c>$</c>、反引号、空格、引号写成八进制之后也就不必再逐个论证。
/// 敲进 PTY 的整行因此只有可打印 ASCII(加上末尾的换行),行编辑器没有可以误读的字节。
/// </para>
/// <para>
/// <b>各 shell 的取舍</b>:<c>printf</c> 的 <c>\ooo</c> 是 POSIX 的,bash / dash / zsh / ksh / BusyBox ash 的内建与
/// coreutils 的 <c>printf</c> 都认;<c>$(…)</c> 也是 POSIX 的。命令替换会去掉末尾的换行,而带换行的名字在前一步就拦下了。
/// 探出登录 shell 是 fish 时写成 fish 自己的 <c>cd (printf '…')</c>:fish 3.4 之前不认 <c>$(…)</c>,而 fish 的命令替换
/// 只按换行切分(名字里没有换行),结果正好是一个参数,所有版本都成立;fish 的单引号里只有 <c>\\</c> 与 <c>\'</c> 是转义,
/// <c>\3</c> 一类原样留给 <c>printf</c>。探不出种类(没开目录上报、探针失败)时照 POSIX 写:fish 3.4 起在双引号里同样认
/// <c>$(…)</c>,更早的 fish 只会报一句语法错。tcsh / csh 不认 <c>$(…)</c>,同样只报语法错、什么都不执行 —— 不会注入,
/// 只是这一次没切过去。Windows 服务器(cmd.exe / PowerShell)不是 readline,也没有 <c>printf</c>,照旧原样引起来发。
/// </para>
/// <para>
/// 纯 ASCII 的目录名(编出来全是 &lt; 0x80 的字节,控制字符已经拦下,剩下的都是可打印的)照旧按登录 shell 的单引号规则原样发
/// (<see cref="RemoteSearch.QuoteFor" />),回显里看得懂,也不改变已有的行为。
/// </para>
/// </remarks>
public static class ChangeDirectoryCommand
{
    /// <summary>组装那一行 <c>cd</c>。</summary>
    /// <param name="shell">登录 shell 的种类(探针结论;探不出为 <see cref="RemoteShellKind.Unknown" />)。</param>
    /// <param name="directory">要切过去的目录(调用方已确认没有控制字符,见 <see cref="SilentCommand.CanTypeLiterally" />)。</param>
    /// <param name="encoding">会话字符集:目录名在服务器上就是按它编码的字节,注入行也按它编码发出。</param>
    /// <returns>不含换行的整条命令。</returns>
    public static string Build(RemoteShellKind shell, string directory, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(encoding);
        byte[] bytes = encoding.GetBytes(directory);
        if (shell == RemoteShellKind.NonPosix || Array.TrueForAll(bytes, static b => b < 0x80))
        {
            return "cd " + RemoteSearch.QuoteFor(shell, directory);
        }
        string format = PrintfFormat(bytes);
        return shell == RemoteShellKind.Fish
            ? $"cd (printf '{format}')"
            : $"cd \"$(printf '{format}')\"";
    }

    /// <summary>
    /// 把一串字节写成 <c>printf</c> 的格式串:字母、数字与 <c>/</c> <c>.</c> <c>_</c> 原样,其余每个字节写成三位八进制(<c>\ooo</c>)。
    /// </summary>
    /// <remarks>
    /// 恒写满三位:后面紧跟一个数字字符时,少写一位就会被吃进转义里。多字节字符集的尾字节落在 ASCII 区间时
    /// (Shift_JIS 的 <c>表</c> 是 <c>95 5C</c>)按字节处理,与它在服务器上的样子一致。
    /// </remarks>
    /// <param name="bytes">目录名按会话字符集编码后的字节。</param>
    /// <returns>只含可打印 ASCII、不含 <c>'</c> 的格式串。</returns>
    internal static string PrintfFormat(ReadOnlySpan<byte> bytes)
    {
        var format = new StringBuilder(bytes.Length * 4);
        foreach (byte b in bytes)
        {
            if (b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'/' or (byte)'.' or (byte)'_')
            {
                format.Append((char)b);
            }
            else
            {
                format.Append('\\')
                      .Append((char)('0' + (b >> 6)))
                      .Append((char)('0' + ((b >> 3) & 7)))
                      .Append((char)('0' + (b & 7)));
            }
        }
        return format.ToString();
    }
}
