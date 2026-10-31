using System.Text.RegularExpressions;

namespace VelaShell.Services;

/// <summary>
/// 判断光标所在这一行是不是「程序在提问」,而不是「用户在写 shell 命令」。
/// </summary>
/// <remarks>
/// <para>
/// 命令补全在这些行上必须闭嘴:sudo 的密码提示、apt 的 <c>[Y/n]</c> 确认、
/// update-alternatives 的编号选单、mysql/gdb 一类 REPL 的提示符 ——
/// 把一整条 shell 历史命令塞进程序的输入里有害无益。
/// </para>
/// <para>
/// <b>这是纯字符串逻辑,刻意不住在 <c>TerminalTabView</c> 的代码隐藏里。</b>
/// 它原先在那儿,于是覆盖它的四十来条用例全都得引用一个 Avalonia <c>UserControl</c> 类型 ——
/// 一段连终端都不认识的正则判定,却被拴在了 UI 的构造与生命周期上。
/// </para>
/// </remarks>
public static partial class InteractivePromptDetector
{
    /// <summary>
    /// 密码 / 是否 / 编号 / REPL 四类交互提示行的合并判定。
    /// </summary>
    /// <param name="line">光标所在行的文本(含已回显的输入)。</param>
    /// <param name="typed">用户此刻已键入的内容,用于从行尾剥掉回显。</param>
    /// <returns>是交互提示行时为 true。</returns>
    public static bool IsInteractivePrompt(string line, string typed)
    {
        string prompt = StripEcho(line, typed);
        return prompt.Length != 0
               && (IsSecretPromptCore(prompt)
                   || IsChoicePromptCore(prompt)
                   || IsSelectionPromptCore(prompt)
                   || IsReplPromptCore(prompt));
    }

    /// <summary>
    /// 只判密码/口令/验证码这一类。
    /// </summary>
    /// <remarks>
    /// 单独保留:密码类比"是否/编号"更严格,某些位置(如口令输入)只想拦密码而不误伤确认行。
    /// </remarks>
    /// <param name="line">光标所在行的文本。</param>
    /// <param name="typed">用户此刻已键入的内容。</param>
    /// <returns>是密码类提示行时为 true。</returns>
    public static bool IsSecretPrompt(string line, string typed) =>
        IsSecretPromptCore(StripEcho(line, typed));

    /// <summary>仅供共享密码入口使用的保守识别;不把私钥口令或验证码当成登录密码。</summary>
    /// <remarks>
    /// <para>
    /// <c>password for</c> 后面的对象要么不带冒号(sudo、psql 的用户名),要么整段包在引号里 ——
    /// git / svn 的 <c>Password for 'https://host:8000':</c> 把 URL 引起来,URL 里的冒号只能这样放行(#638)。
    /// </para>
    /// <para>
    /// 行首可以带 sudo 一族的方括号标签,所有写法共用:经典 sudo 的 <c>[sudo] password for joe:</c>,
    /// sudo-rs(Ubuntu 25.10 起的默认 sudo)的 <c>[sudo: authenticate] Password:</c> —— 标签之后是 PAM 给的原话,
    /// 以 <c>sudo-rs</c> 的名字调用时标签也跟着叫 <c>sudo-rs</c>。本地化的 sudo 把用户名放在前面:
    /// <c>[sudo] joe 的密码：</c>、<c>[sudo] joe のパスワード:</c>、<c>[sudo] joe의 암호:</c>。
    /// 标签只认 sudo / su / doas 这几个名字,别的方括号开头(<c>[joe@host ~]</c> 一类的 shell 提示符)照旧不算。
    /// </para>
    /// <para>
    /// <b>「X 的密码」这种本地化写法必须带着 sudo 一族的标签</b> —— 它唯一的出处就是本地化的 sudo。
    /// 不带标签时 X 可以是任何字:Linux-PAM 的中文目录里 <c>passwd</c> 问的「新的密码：」「重新输入新的密码：」
    /// 正好是这个形状(日文的「現在のパスワード:」同理),列表在设新口令时弹出来,再加上自动回车,
    /// 一点就把共享密码设成了新口令。英文那边一样只认 <c>password</c> 打头的几种固定说法,
    /// <c>New password:</c>、<c>Retype new password:</c> 本来就不认。
    /// </para>
    /// </remarks>
    public static bool IsPasswordFillPrompt(string line) => PasswordFillPromptRegex().IsMatch(line.Trim());

    [GeneratedRegex(@"^(?:(?:\[(?:sudo|su|doas)(?:-rs)?(?::[^\]\r\n]{1,40})?\]\s+)?(?:password|passwd|enter password|请输入密码|密码|パスワード|암호|password for (?:'[^'\r\n]+'|""[^""\r\n]+""|[^:\r\n]+)|[^\s:'\""$>#]+(?:@[^\s:'\""$>#]+)?['’]s password|doas \([^)\r\n]+\) password)|\[(?:sudo|su|doas)(?:-rs)?(?::[^\]\r\n]{1,40})?\]\s+[^\s:：'\""$>#\[\]]+ ?(?:的密码|のパスワード|의 암호))[:：]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordFillPromptRegex();

    /// <summary>剥掉光标行末尾已回显的输入,只留下程序打印的提示部分。</summary>
    private static string StripEcho(string line, string typed)
    {
        string prompt = line.TrimEnd();
        if (typed.Length > 0 && prompt.EndsWith(typed, StringComparison.Ordinal))
        {
            prompt = prompt[..^typed.Length].TrimEnd();
        }
        return prompt;
    }

    private static bool IsSecretPromptCore(string prompt)
    {
        if (prompt.Length == 0 || (prompt[^1] != ':' && prompt[^1] != '：'))
        {
            return false;
        }
        foreach (string keyword in (ReadOnlySpan<string>)
                 [
                     "password",
                     "passphrase",
                     "passwd",
                     "密码",
                     "口令",
                     "verification code",
                     "验证码",
                     "认证码",
                 ])
        {
            if (prompt.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 是否类确认行:结尾带斜杠分隔的括号选项([Y/n]/(yes/no)/[是/否] 等),或整行以问号
    /// 结尾(未显式列出选项的 "overwrite 'x'?"/"是否覆盖?")。问号分支要求提示含空格或
    /// 非 ASCII 字(即成句),排除主题里以单个 "?" 作装饰的提示符。
    /// </summary>
    private static bool IsChoicePromptCore(string prompt)
    {
        char tail = prompt[^1];
        if ((tail == '?' || tail == '？') && (prompt.Contains(' ') || HasNonAscii(prompt)))
        {
            return true;
        }
        return ChoiceTokenRegex().IsMatch(prompt);
    }

    private static bool HasNonAscii(string s)
    {
        foreach (char c in s)
        {
            if (c > 0x7F)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>编号选择菜单:冒号结尾且含"选择/编号/选项/number/selection/choice"等词。</summary>
    private static bool IsSelectionPromptCore(string prompt)
    {
        if (prompt[^1] is not ':' and not '：')
        {
            return false;
        }
        foreach (string keyword in (ReadOnlySpan<string>)
                 [
                     "selection",
                     "select",
                     "choose",
                     "choice",
                     "number",
                     "请选择",
                     "请输入",
                     "选择",
                     "编号",
                     "序号",
                     "选项",
                 ])
        {
            if (prompt.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// REPL/交互客户端提示符:Python(">>>" 主提示、"..." 续行,均按整段精确匹配)、
    /// mysql/mariadb/sqlite/psql、IPython、gdb/lldb/pdb、irb 等。裸 ">"(node/R/mongosh)
    /// 与用户自定义 shell 提示符无法区分,刻意不纳入,以免误伤正常补全。
    /// </summary>
    private static bool IsReplPromptCore(string prompt) =>
        prompt is ">>>" or "..." || ReplPromptRegex().IsMatch(prompt);

    // 结尾括号选项:[Y/n]、(yes/no)、[Y/I/N/O/D/Z]、[是/否],允许尾随 ?/:/。及空白。
    // 每段选项限 1~3 个字母,避免误伤 prompt 主题里的 "(feature/xxx)" 分支括号。
    [GeneratedRegex(
        @"[\[(]\s*\p{L}{1,3}(?:\s*/\s*\p{L}{1,3})+\s*[\])]\s*[?？:：.。]?\s*$",
        RegexOptions.Compiled
    )]
    private static partial Regex ChoiceTokenRegex();

    /*
        @"(?:(?:mysql|mariadb|sqlite|clickhouse|ftp|sftp|telnet)>" +   // 具名 SQL/网络客户端
        @"|MariaDB \[[^\]]*\]>" +                                       // MariaDB [db]>
        @"|\w+=[#>]" +                                                  // postgres 就绪提示:db=# / db=>
        @"|In \[\d+\]:" +                                               // IPython In [n]:
        @"|\((?:gdb|lldb|Pdb)\)" +                                      // (gdb) (lldb) (Pdb)
        @"|i?pdb>" +                                                    // pdb> / ipdb>
        @"|irb\([^)]*\)[^>\n]*>" +                                      // irb(main):001:0>
        @")\s*$"
    */
    // REPL 交互提示符:此时补全给的是 shell 快捷命令,语义上是错的,故一并不弹。
    // 只匹配"带库名/工具名"等高辨识度形态;裸 ">"(node/R/mongosh)与自定义 shell 提示符
    // 无法区分,一律不拦,避免误伤把 PS1 设成 "> " 的用户。
    [GeneratedRegex(
        @"(?:(?:mysql|mariadb|sqlite|clickhouse|ftp|sftp|telnet)>|MariaDB \[[^\]]*\]>|\w+=[#>]|In \[\d+\]:|\((?:gdb|lldb|Pdb)\)|i?pdb>|irb\([^)]*\)[^>\n]*>)\s*$",
        RegexOptions.Compiled
    )]
    private static partial Regex ReplPromptRegex();
}
