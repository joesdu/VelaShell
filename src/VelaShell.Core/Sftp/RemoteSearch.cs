using System.Text;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>一次远端搜索要找什么。</summary>
/// <param name="Root">从哪个目录找起(远端绝对路径)。</param>
/// <param name="NamePattern">文件名模式(<c>find -name</c> 的通配符);空 = 任意名字,不含通配符时按「名字里含有」找。</param>
/// <param name="ContentText">文件内容里要有的文字(按字面、不按正则);空 = 只按名字找。</param>
/// <param name="IgnoreCase">名字与内容都不区分大小写。</param>
/// <param name="MaxResults">最多要多少条;到数就停,远端的 <c>find</c> 由服务发 <c>TERM</c> 一并收掉(见 <see cref="RemoteSearchService" />)。</param>
public sealed record RemoteSearchQuery(
    string Root,
    string? NamePattern,
    string? ContentText = null,
    bool IgnoreCase = true,
    int MaxResults = RemoteSearch.DefaultMaxResults)
{
    /// <summary>是不是按内容找(要跑 <c>grep</c>)。</summary>
    public bool IsContentSearch => !string.IsNullOrEmpty(ContentText);
}

/// <summary>搜到的一条。</summary>
/// <param name="Path">远端绝对路径。</param>
/// <param name="IsDirectory">是不是目录(指向目录的链接也算 —— 点了能进得去)。</param>
public sealed record RemoteSearchHit(string Path, bool IsDirectory);

/// <summary>
/// 远端搜索:把查询拼成<b>一条</b> exec 命令(<c>find</c>,按内容时再加 <c>grep</c>),解析它的输出。
/// 与连接无关的纯函数,单独拎出来才测得到引号与转义。
/// </summary>
/// <remarks>
/// <para>
/// <b>不留注入口。</b>脚本本体是写死的常量(只按「按名字 / 按内容」「忽略大小写」「从 / 起找」挑几段常量拼),
/// 用户填的目录、模式、文字一个字节都不进脚本,全部作为 <c>sh -c '脚本' 名字 参数…</c> 的位置参数传进去,
/// 脚本里只以 <c>"$1"</c> 这样的形式引用 —— 位置参数在 sh 里不会被二次展开。命令行上的每个参数都按登录 shell
/// 的单引号规则转义(见 <see cref="QuoteFor" />):POSIX shell 里 <c>'</c> 写成 <c>'\''</c>;fish 的单引号里
/// <c>\</c> 与 <c>'</c> 是转义符,要写成 <c>\\</c> 与 <c>\'</c>。
/// </para>
/// <para>
/// <b>BusyBox 兼容。</b>只用 BusyBox 也有的那一组:<c>find -mindepth/-maxdepth/-name/-iname/-type/-path/-prune/-print</c>、
/// <c>-exec … {} +</c>(POSIX.1-2008)、<c>grep -l -q -s -i -F -e</c>、<c>head -n</c>、<c>tr</c>、<c>[ -d ]</c>、<c>printf</c>。
/// 不用 GNU 才有的 <c>-printf</c>、<c>--include</c>、<c>grep -Z</c>(BusyBox 的 grep 没有)。
/// </para>
/// <para>
/// <b>名字里可以有换行。</b>按行输出的话,一个名字里带换行的文件会被拆成两条结果。所以路径不经 <c>find -print</c> 的换行分隔,
/// 而是由 <c>find … -exec sh -c 批处理 {} +</c> 当参数交给一个小 sh(参数里换行只是普通字符),它用 <c>[ -d ]</c> 判目录还是文件,
/// 每条打成 <c>d\t路径\0</c> 或 <c>f\t路径\0</c>;再经 <c>tr</c> 把 <c>\0</c> 与 <c>\n</c> 对调 —— 记录之间是换行、名字里的换行成了 <c>\0</c>
/// (文件名里不可能有 <c>\0</c>),<c>head -n</c> 数的就是记录条数,解析时再把 <c>\0</c> 换回换行(<see cref="DecodeRecord" />)。
/// 不直接用 <c>find -print0</c>:之后的 <c>head</c> 只会按换行数、BusyBox 的 <c>head</c> 没有 <c>-z</c>,sh 的 <c>read</c> 也读不了 <c>\0</c> 分隔。
/// 按内容找时,名字里没有换行的照旧一批交给一次 <c>grep -l</c>(它的输出按行,名字里没换行就不会错位),带换行的逐个 <c>grep -q</c>。
/// </para>
/// <para>
/// <b>回退。</b>先在根目录上试一下 <c>find -maxdepth 0 -exec … {} +</c>:裁掉了 <c>-exec … +</c> 的极老 BusyBox 上走原来的按行输出
/// (<c>find -print | head | while read</c>),名字里带换行的照旧拆成两条;按内容找本来就要 <c>-exec … +</c>,在那种 BusyBox 上仍是 0 项而不是报错。
/// 两种输出的每条记录是同一个格式,解析不必知道走的是哪一条。
/// </para>
/// <para>
/// <b>按会话编码。</b>GBK 一类服务器上文件名是 GBK 字节:输出按会话的文件名编码解(与 SFTP 同一个来源,见 <see cref="RemoteFileNameEncoding" />;
/// UTF-8 会话照旧按 UTF-8 解),命令里的目录、模式、文字也编成会话编码的字节 —— 但 SSH 的 exec 请求只能是 UTF-8 字符串,
/// 所以这三项写成全八进制转义(<c>\344\270\255</c>,纯 ASCII),脚本开头用 <c>printf</c> 还原成那串字节。转义串只由 <c>\ddd</c> 组成,
/// 拿它当 <c>printf</c> 的格式串不会被解释出任何转换。编不进会话编码的输入(GBK 里没有的字符)整次退回原样的 UTF-8 参数。
/// 这种会话里脚本在 C locale 下跑,<c>find</c> / <c>grep</c> 按字节匹配(理由见 <see cref="DecodeNameArguments" />)。
/// </para>
/// <para>
/// <b>有上限、能停。</b>条数:<c>head -n 上限+1</c> 收满就退出;多出来的那一条只用来判断「结果不完整」。
/// 光靠 <c>head</c> 退出停不下来 —— 往外写的是 <c>-exec</c> 的批处理 sh,它们死于 SIGPIPE,<c>find</c> 本身只看到子进程失败,
/// 照样把整棵树走完(按内容找时 <c>grep</c> 照样读完剩下的文件),主 sh 又在 <c>wait</c> 里等它们。所以收到第「上限 + 1」条时
/// 由 <see cref="RemoteSearchService" /> 从这一侧取消,与用户停止走同一条路。时间与取消由调用方的令牌管:
/// 取消时 SSH 库先给远端发 <c>TERM</c>,脚本的 <c>trap</c> 再把整个进程组(<c>find</c>、<c>grep</c>、<c>head</c>)一起收掉 ——
/// 只收 sh 的话,一个还没匹配到东西的 <c>find</c> 会在远端一直走下去。管道放在后台再 <c>wait</c>,
/// 是因为 sh 在前台命令跑完之前不执行 trap。
/// </para>
/// </remarks>
public static class RemoteSearch
{
    /// <summary>默认最多多少条。</summary>
    public const int DefaultMaxResults = 500;

    /// <summary>远端没有 <c>find</c>(或同样必需的 <c>head</c>、<c>tr</c>)时脚本打印的标记。</summary>
    public const string NoFindMarker = "VELA-SEARCH-NO-FIND";

    /// <summary>按内容找、远端没有 <c>grep</c> 时脚本打印的标记。</summary>
    public const string NoGrepMarker = "VELA-SEARCH-NO-GREP";

    /// <summary><c>sh -c</c> 的 <c>$0</c>(出现在远端的报错与进程列表里)。</summary>
    private const string ScriptName = "vela-search";

    // 脚本片段里不能出现单引号(整段要被单引号包着交给 sh -c)。
    // $1 = 根目录、$2 = 名字模式、$3 = 条数上限、$4 = 交给 find -exec 的批处理脚本(常量)、$5 = 内容文字。
    private const string Prologue =
        "trap \"trap - TERM HUP INT; kill 0\" TERM HUP INT; " +
        "command -v find >/dev/null 2>&1 && command -v head >/dev/null 2>&1 && command -v tr >/dev/null 2>&1 " +
        "|| { echo " + NoFindMarker + "; exit 0; }; ";

    private const string GrepCheck =
        "command -v grep >/dev/null 2>&1 || { echo " + NoGrepMarker + "; exit 0; }; ";

    /// <summary>
    /// 参数是八进制转义(会话不是 UTF-8)时,先把目录与模式还原成会话编码的字节;<c>_</c> 挡住命令替换吞掉末尾的换行。
    /// </summary>
    /// <remarks>
    /// 同时切到 C locale,让 <c>find -name</c> 与 <c>grep</c> 按字节比:musl(Alpine 的 BusyBox)在 UTF-8 locale 下,
    /// 模式里有不是 UTF-8 的字节就一个都匹配不上,连一字不差的名字都找不到(靶机上实测)。代价是 <c>?</c> 匹配一个字节
    /// 而不是一个汉字;<c>-iname</c> / <c>grep -i</c> 只对 ASCII 字母不分大小写 —— 中日韩文字本来就没有大小写。
    /// </remarks>
    private const string DecodeNameArguments =
        "LC_ALL=C; export LC_ALL; r=$(printf \"$1_\"); p=$(printf \"$2_\"); set -- \"${r%_}\" \"${p%_}\" \"$3\" \"$4\"; ";

    /// <summary>同上,再加上内容文字。</summary>
    private const string DecodeContentArguments =
        "LC_ALL=C; export LC_ALL; r=$(printf \"$1_\"); p=$(printf \"$2_\"); t=$(printf \"$5_\"); " +
        "set -- \"${r%_}\" \"${p%_}\" \"$3\" \"$4\" \"${t%_}\"; ";

    /// <summary>这台的 find 认不认 <c>-exec … {} +</c>:只在根目录自己身上试,不往下走。</summary>
    private const string ProbeExecPlus = "if find \"$1\" -maxdepth 0 -exec sh -c : vela-probe {} + >/dev/null 2>&1; then ";

    /// <summary>从 <c>/</c> 起找时跳过的伪文件系统:<c>/proc</c> 里的东西走不完,<c>/sys</c> 的链接成环。</summary>
    private const string PruneVirtual = "\\( -path /proc -o -path /sys -o -path /dev \\) -prune -o ";

    /// <summary>新输出的收尾:<c>\0</c> 与换行对调(名字里的换行成了 <c>\0</c>),<c>head</c> 才数得对条数。</summary>
    private const string SwapAndLimit = "; } | tr \"\\n\\000\" \"\\000\\n\" | head -n \"$3\" & wait; else ";

    /// <summary>回退那一路的收尾:原来的按行输出。</summary>
    private const string LegacyClassify =
        "; } | head -n \"$3\" | while IFS= read -r p; do " +
        "if [ -d \"$p\" ]; then printf \"d\\t%s\\n\" \"$p\"; else printf \"f\\t%s\\n\" \"$p\"; fi; done & wait; fi";

    /// <summary>按名字找的批处理($4):每个路径判一次目录还是文件,记录以 <c>\0</c> 结尾。</summary>
    private const string ClassifyBatch =
        "for p in \"$@\"; do if [ -d \"$p\" ]; then printf \"d\\t%s\\0\" \"$p\"; else printf \"f\\t%s\\0\" \"$p\"; fi; done";

    /// <summary>
    /// 按内容找的批处理($4,$1 = 文字,其后是这一批文件):名字里带换行的逐个 <c>grep -q</c>,其余攒成一批交给一次 <c>grep -l</c>。
    /// <c>{0}</c> 是 <c>-i </c> 或空。
    /// </summary>
    private const string ContentBatch =
        "t=$1; shift; n=$(printf \"\\n_\"); n=${n%_}; " +
        "for p in \"$@\"; do shift; case $p in " +
        "*\"$n\"*) grep -q -s {0}-F -e \"$t\" \"$p\" && printf \"f\\t%s\\0\" \"$p\";; " +
        "*) set -- \"$@\" \"$p\";; esac; done; " +
        "[ $# -eq 0 ] || grep -l -s {0}-F -e \"$t\" \"$@\" | while IFS= read -r p; do printf \"f\\t%s\\0\" \"$p\"; done";

    /// <summary>实际交给 <c>find -name</c> 的模式:空为 <c>*</c>;不含 <c>*</c> <c>?</c> <c>[</c> 时两头补 <c>*</c>(按「名字里含有」找)。</summary>
    /// <param name="pattern">用户填的模式。</param>
    public static string EffectiveNamePattern(string? pattern)
    {
        string trimmed = pattern?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "*";
        }
        return trimmed.IndexOfAny(['*', '?', '[']) >= 0 ? trimmed : "*" + trimmed + "*";
    }

    /// <summary>根目录归一:去掉尾部的 <c>/</c>(根目录本身除外),连续的 <c>/</c> 并成一个。</summary>
    /// <param name="root">远端目录。</param>
    public static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string collapsed = CollapseSlashes(root.Trim());
        string trimmed = collapsed.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>
    /// 拼出这次搜索的命令行。
    /// </summary>
    /// <param name="query">查询;<see cref="RemoteSearchQuery.Root" /> 必须是绝对路径。</param>
    /// <param name="shell">远端登录 shell 的种类(决定命令行上参数怎么加引号)。</param>
    /// <param name="fileNameEncoding">
    /// 会话的文件名编码(<see cref="RemoteFileNameEncoding.Resolve" />);null = UTF-8,参数原样传。
    /// 给了就把目录、模式、文字编成这种编码的字节、写成八进制转义(见类型说明);有编不进去的字符时整次退回原样传。
    /// </param>
    /// <exception cref="ArgumentException">根目录不是绝对路径,或条数上限不是正数。</exception>
    /// <exception cref="NotSupportedException">登录 shell 不是 POSIX shell 也不是 fish。</exception>
    public static string BuildCommand(RemoteSearchQuery query, RemoteShellKind shell, Encoding? fileNameEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (shell is not (RemoteShellKind.Bash or RemoteShellKind.Zsh or RemoteShellKind.PosixSh or RemoteShellKind.Fish))
        {
            throw new NotSupportedException($"Remote search needs a POSIX shell or fish, not {shell}.");
        }
        string root = NormalizeRoot(query.Root);
        if (root[0] != '/')
        {
            throw new ArgumentException("The search root must be an absolute remote path.", nameof(query));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.MaxResults);

        string pattern = EffectiveNamePattern(query.NamePattern);
        string? text = query.IsContentSearch ? query.ContentText : null;
        string[]? escaped = fileNameEncoding is null ? null : EscapeAll(fileNameEncoding, root, pattern, text);

        var script = new StringBuilder(Prologue);
        if (query.IsContentSearch)
        {
            script.Append(GrepCheck);
        }
        if (escaped is not null)
        {
            script.Append(query.IsContentSearch ? DecodeContentArguments : DecodeNameArguments);
        }
        string prune = root == "/" ? PruneVirtual : string.Empty;
        string name = query.IgnoreCase ? "-iname" : "-name";
        string ignoreCase = query.IgnoreCase ? "-i " : string.Empty;

        // -mindepth 1:根目录自己不算结果(名字恰好匹配时 find 会列出它),而且得在远端就排除 —— 否则它占掉一个名额,
        // 「上限 + 1」那一条就判断不出截断了。GNU、BSD、BusyBox 的 find 都有它(POSIX 没写)。
        script.Append(ProbeExecPlus).Append("{ find \"$1\" -mindepth 1 ").Append(prune);
        if (query.IsContentSearch)
        {
            // 内容文字($5)排在 {} 前面交给批处理,作它的 $1。
            script.Append("-type f ").Append(name).Append(" \"$2\" -exec sh -c \"$4\" ").Append(ScriptName)
                  .Append(" \"$5\" {} + 2>/dev/null");
        }
        else
        {
            script.Append(name).Append(" \"$2\" -exec sh -c \"$4\" ").Append(ScriptName).Append(" {} + 2>/dev/null");
        }
        script.Append(SwapAndLimit);

        // 回退:原来的按行输出。-l 只报文件名、-s 不报读不了的文件、-F 按字面、-e 让以 - 开头的文字也是文字。
        script.Append("{ find \"$1\" -mindepth 1 ").Append(prune);
        if (query.IsContentSearch)
        {
            script.Append("-type f ").Append(name).Append(" \"$2\" -exec grep -l -s ").Append(ignoreCase)
                  .Append("-F -e \"$5\" {} + 2>/dev/null");
        }
        else
        {
            script.Append(name).Append(" \"$2\" -print 2>/dev/null");
        }
        script.Append(LegacyClassify);

        string batch = query.IsContentSearch
            ? ContentBatch.Replace("{0}", ignoreCase, StringComparison.Ordinal)
            : ClassifyBatch;
        var command = new StringBuilder("sh -c ");
        command.Append(QuoteFor(shell, script.ToString())).Append(' ').Append(ScriptName)
               .Append(' ').Append(QuoteFor(shell, escaped?[0] ?? root))
               .Append(' ').Append(QuoteFor(shell, escaped?[1] ?? pattern))
               .Append(' ').Append(QuoteFor(shell, (query.MaxResults + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))
               .Append(' ').Append(QuoteFor(shell, batch));
        if (text is not null)
        {
            command.Append(' ').Append(QuoteFor(shell, escaped?[2] ?? text));
        }
        return command.ToString();
    }

    /// <summary>
    /// 把一条记录的原始字节解成文本:按会话编码(null = UTF-8)解,再把名字里被换成 <c>\0</c> 的换行换回来。
    /// </summary>
    /// <param name="record">一条记录(不含末尾的换行)。</param>
    /// <param name="fileNameEncoding">会话的文件名编码;null = UTF-8。</param>
    public static string DecodeRecord(ReadOnlySpan<byte> record, Encoding? fileNameEncoding)
    {
        string text = (fileNameEncoding ?? Encoding.UTF8).GetString(record);
        return text.Contains('\0', StringComparison.Ordinal) ? text.Replace('\0', '\n') : text;
    }

    /// <summary>
    /// 把几项参数都编成 <paramref name="encoding" /> 的字节、写成 <c>\ddd</c> 八进制转义;有一项编不进去就整体放弃(返回 null)。
    /// </summary>
    private static string[]? EscapeAll(Encoding encoding, params string?[] values)
    {
        var strict = (Encoding)encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        string[] escaped = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                continue;
            }
            byte[] bytes;
            try
            {
                bytes = strict.GetBytes(value);
            }
            catch (EncoderFallbackException)
            {
                return null;
            }
            var builder = new StringBuilder(bytes.Length * 4);
            foreach (byte b in bytes)
            {
                // 一律三位:「\0」后面紧跟数字时,短写会把下一个字节的数字并进来。
                builder.Append('\\').Append((char)('0' + (b >> 6))).Append((char)('0' + ((b >> 3) & 7))).Append((char)('0' + (b & 7)));
            }
            escaped[i] = builder.ToString();
        }
        return escaped;
    }

    /// <summary>按登录 shell 的单引号规则把一个参数原样引起来。</summary>
    /// <param name="shell">登录 shell 的种类。</param>
    /// <param name="value">参数原文。</param>
    public static string QuoteFor(RemoteShellKind shell, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // fish 的单引号里只有 \\ 与 \' 两个转义:先翻倍反斜杠,再转义引号。
        return shell == RemoteShellKind.Fish
            ? "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'"
            : RemoteSha256.Quote(value);
    }

    /// <summary>这一行是不是「远端缺工具」的标记。</summary>
    /// <param name="line">一行输出。</param>
    public static bool IsToolMissingMarker(string line) =>
        line.Trim() is NoFindMarker or NoGrepMarker;

    /// <summary>
    /// 解析一条记录。认得出的是 <c>d\t路径</c> / <c>f\t路径</c>,路径必须是绝对路径;根目录本身不算结果
    /// (它的名字恰好也匹配时 <c>find</c> 会把它列出来)。
    /// </summary>
    /// <param name="record">
    /// 一条记录的文本(<see cref="DecodeRecord" /> 解出来的,名字里的换行已换回)。<b>不去掉行尾的 <c>\r</c></b>:
    /// 记录只按换行分,<c>\r</c> 是名字的一部分(以前按行读时会被当成行尾吃掉)。
    /// </param>
    /// <param name="root">这次搜索的根目录。</param>
    /// <param name="hit">认出来的那一条。</param>
    public static bool TryParseHit(string record, string root, out RemoteSearchHit hit)
    {
        hit = null!;
        if (record.Length < 3 || record[1] != '\t' || record[0] is not ('d' or 'f') || record[2] != '/')
        {
            return false;
        }
        string path = CollapseSlashes(record[2..]);
        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }
        if (string.Equals(path, NormalizeRoot(root), StringComparison.Ordinal))
        {
            return false;
        }
        hit = new(path, record[0] == 'd');
        return true;
    }

    private static string CollapseSlashes(string path)
    {
        if (!path.Contains("//", StringComparison.Ordinal))
        {
            return path;
        }
        var builder = new StringBuilder(path.Length);
        foreach (char c in path)
        {
            if (c != '/' || builder.Length == 0 || builder[^1] != '/')
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }
}
