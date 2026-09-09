using System.Text;

namespace VelaShell.Infrastructure.Import;

/// <summary>
/// <c>~/.ssh/config</c> 里的一个配置块:一组主机模式,以及块内按出现顺序排列的选项。
/// </summary>
/// <param name="Patterns">
/// <c>Host</c> 行上的模式列表(可含 <c>*</c> / <c>?</c> 通配与 <c>!</c> 取反);
/// <c>Match</c> 块解析为空列表 —— 它的条件依赖运行时上下文,无法静态判定,因此永不匹配。
/// </param>
/// <param name="Options">块内的 <c>关键字 值</c> 对,保留原始顺序。</param>
internal sealed record SshConfigBlock(IReadOnlyList<string> Patterns, IReadOnlyList<KeyValuePair<string, string>> Options);

/// <summary>
/// OpenSSH <c>ssh_config</c> 的解析器:把配置文件读成有序的 <see cref="SshConfigBlock" /> 列表,
/// 并按 OpenSSH 的取值规则(**先出现者胜**,跨块累计)求某个主机别名的有效选项。
/// </summary>
/// <remarks>
/// <para>
/// 只解析导入需要的那几个关键字,但**块结构是完整的** —— 因为取值规则依赖块的顺序与匹配关系:
/// 一个 <c>Host *</c> 兜底块里的 <c>User</c> 必须在所有具名块都没写 <c>User</c> 时才生效,
/// 只挑关键字而丢掉块归属会把这条规则做反。
/// </para>
/// <para>
/// <c>Match</c> 块整体跳过:它的条件(<c>exec</c>、<c>originalhost</c>、<c>canonical</c> 等)
/// 要到真正连接时才有答案,静态导入无从判定。跳过而不是当成 <c>Host *</c>,
/// 是因为后者会把只在特定条件下生效的选项无条件套到每一条会话上。
/// </para>
/// </remarks>
internal static class SshConfigParser
{
    /// <summary><c>Include</c> 的展开深度上限,防止互相包含转成死循环。</summary>
    private const int MaxIncludeDepth = 8;

    /// <summary>
    /// 读取一个 <c>ssh_config</c> 文件并展开其中的 <c>Include</c>,返回按文件顺序排列的配置块。
    /// </summary>
    /// <param name="path">配置文件路径。</param>
    /// <param name="baseDirectory">
    /// 解析 <c>Include</c> 相对路径的基准目录(用户配置为 <c>~/.ssh</c>);为空时取配置文件所在目录。
    /// </param>
    /// <returns>配置块列表;文件读不出来时为空列表。</returns>
    public static IReadOnlyList<SshConfigBlock> ParseFile(string path, string? baseDirectory = null)
    {
        string root = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty
            : baseDirectory;
        var blocks = new List<SshConfigBlock>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadInto(blocks, path, root, visited, 0);
        return blocks;
    }

    /// <summary>解析已在内存中的配置行(不展开 <c>Include</c>),供测试与嵌入式场景使用。</summary>
    /// <param name="lines">配置文件的全部行。</param>
    /// <returns>配置块列表。</returns>
    public static IReadOnlyList<SshConfigBlock> Parse(IEnumerable<string> lines)
    {
        var blocks = new List<SshConfigBlock>();
        Accumulate(blocks, lines, includeResolver: null);
        return blocks;
    }

    /// <summary>
    /// 收集所有可作为会话导入的主机别名:出现在 <c>Host</c> 行上、且不含通配与取反的模式。
    /// </summary>
    /// <param name="blocks">配置块。</param>
    /// <returns>按首次出现顺序去重后的别名列表。</returns>
    public static IReadOnlyList<string> CollectHostAliases(IReadOnlyList<SshConfigBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();
        foreach (SshConfigBlock block in blocks)
        {
            foreach (string pattern in block.Patterns)
            {
                if (IsLiteralAlias(pattern) && seen.Add(pattern))
                {
                    aliases.Add(pattern);
                }
            }
        }
        return aliases;
    }

    /// <summary>
    /// 求某个主机别名的有效选项:按块顺序遍历所有匹配该别名的块,**每个关键字取最先出现的值**
    /// (OpenSSH 的取值规则),因此写在文件末尾的 <c>Host *</c> 只起兜底作用。
    /// </summary>
    /// <param name="blocks">配置块。</param>
    /// <param name="alias">主机别名。</param>
    /// <returns>关键字(小写)到值的映射;<c>IdentityFile</c> 等可重复的关键字只保留第一条。</returns>
    public static IReadOnlyDictionary<string, string> ResolveOptions(IReadOnlyList<SshConfigBlock> blocks, string alias)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (SshConfigBlock block in blocks)
        {
            if (!Matches(block.Patterns, alias))
            {
                continue;
            }
            foreach ((string key, string value) in block.Options)
            {
                // 先出现者胜:已经取到的关键字不再被后面的块覆盖。
                _ = result.TryAdd(key, value);
            }
        }
        return result;
    }

    /// <summary>
    /// 判断主机别名是否匹配一组 <c>Host</c> 模式:至少命中一个正向模式,且不命中任何取反模式。
    /// </summary>
    /// <param name="patterns">模式列表。</param>
    /// <param name="alias">主机别名。</param>
    /// <returns>匹配则为 <c>true</c>。</returns>
    public static bool Matches(IReadOnlyList<string> patterns, string alias)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        bool positive = false;
        foreach (string pattern in patterns)
        {
            bool negated = pattern.StartsWith('!');
            string body = negated ? pattern[1..] : pattern;
            if (body.Length == 0 || !MatchesPattern(body, alias))
            {
                continue;
            }
            if (negated)
            {
                return false; // 取反命中一票否决。
            }
            positive = true;
        }
        return positive;
    }

    /// <summary>别名是否是「字面量」:不含 <c>*</c> / <c>?</c> 通配,也不是取反模式。</summary>
    private static bool IsLiteralAlias(string pattern) =>
        pattern.Length > 0 && !pattern.StartsWith('!') && pattern.AsSpan().IndexOfAny('*', '?') < 0;

    /// <summary>OpenSSH 的通配匹配:<c>*</c> 匹配任意多个字符,<c>?</c> 匹配单个字符。</summary>
    private static bool MatchesPattern(string pattern, string value)
    {
        // 逐字符的经典双指针回溯:模式与值都很短,不值得为它引入正则的编译开销。
        int p = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }

    /// <summary>读入一个配置文件(含 <c>Include</c> 展开)并把块追加到 <paramref name="blocks" />。</summary>
    private static void ReadInto(List<SshConfigBlock> blocks, string path, string baseDirectory, HashSet<string> visited, int depth)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        if (!visited.Add(full) || !File.Exists(full))
        {
            return; // 已经读过(互相 Include)或根本不存在,直接跳过。
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(full, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // 单个文件读失败不阻断整体解析。
        }

        Accumulate(blocks, lines, included =>
        {
            if (depth >= MaxIncludeDepth)
            {
                return;
            }
            foreach (string file in ExpandInclude(included, baseDirectory))
            {
                ReadInto(blocks, file, baseDirectory, visited, depth + 1);
            }
        });
    }

    /// <summary>把配置行折成块;遇到 <c>Include</c> 时回调 <paramref name="includeResolver" /> 就地展开。</summary>
    private static void Accumulate(List<SshConfigBlock> blocks, IEnumerable<string> lines, Action<string>? includeResolver)
    {
        // 首个 Host 行之前的全局选项对所有主机生效,等价于一个 `Host *` 块。
        List<string> patterns = ["*"];
        List<KeyValuePair<string, string>> options = [];

        void Flush()
        {
            if (options.Count > 0 || patterns.Count > 0)
            {
                blocks.Add(new SshConfigBlock(patterns, options));
            }
        }

        foreach (string raw in lines)
        {
            if (!TrySplitDirective(raw, out string keyword, out string value))
            {
                continue;
            }
            if (keyword.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                patterns = [.. SplitTokens(value)];
                options = [];
                continue;
            }
            if (keyword.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                // 条件块:保留成一个空模式块,里面的选项因此永不参与取值。
                Flush();
                patterns = [];
                options = [];
                continue;
            }
            if (keyword.Equals("Include", StringComparison.OrdinalIgnoreCase))
            {
                // Include 在文件里的位置决定取值优先级,因此必须就地展开:
                // 先把当前累积的块收口,展开被包含的块,再另起一个同模式的块继续累积。
                Flush();
                includeResolver?.Invoke(value);
                options = [];
                continue;
            }
            options.Add(new KeyValuePair<string, string>(keyword, Unquote(value)));
        }
        Flush();
    }

    /// <summary>
    /// 把一行拆成关键字与值。OpenSSH 允许 <c>关键字 值</c> 与 <c>关键字=值</c> 两种写法,
    /// 整行注释(<c>#</c> 开头)与空行跳过。
    /// </summary>
    private static bool TrySplitDirective(string raw, out string keyword, out string value)
    {
        keyword = string.Empty;
        value = string.Empty;
        string line = raw.Trim();
        // 只认整行注释:OpenSSH 本身不支持行尾注释,按行尾注释处理反而会吃掉合法的 `#`(如密码占位路径)。
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return false;
        }
        int split = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (char.IsWhiteSpace(line[i]) || line[i] == '=')
            {
                split = i;
                break;
            }
        }
        if (split < 0)
        {
            return false; // 光有关键字没有值,无从取值。
        }
        keyword = line[..split];
        value = line[split..].TrimStart(' ', '\t', '=').Trim();
        return keyword.Length > 0 && value.Length > 0;
    }

    /// <summary>按空白切分记号,并去掉每个记号两端的引号。</summary>
    private static IEnumerable<string> SplitTokens(string value)
    {
        foreach (string token in value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string unquoted = Unquote(token);
            if (unquoted.Length > 0)
            {
                yield return unquoted;
            }
        }
    }

    /// <summary>去掉值两端配对的双引号。</summary>
    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>
    /// 展开 <c>Include</c> 的实参:支持多个以空白分隔的路径、<c>~</c> 前缀、相对基准目录的相对路径,
    /// 以及末段的 <c>*</c> / <c>?</c> 通配(OpenSSH 只在文件名上做通配)。
    /// </summary>
    private static IEnumerable<string> ExpandInclude(string value, string baseDirectory)
    {
        foreach (string token in SplitTokens(value))
        {
            string path = SshPathResolver.Expand(token, baseDirectory);
            string directory = Path.GetDirectoryName(path) ?? baseDirectory;
            string name = Path.GetFileName(path);
            if (name.AsSpan().IndexOfAny('*', '?') < 0)
            {
                yield return path;
                continue;
            }
            string[] matched;
            try
            {
                matched = Directory.Exists(directory)
                    ? Directory.GetFiles(directory, name, SearchOption.TopDirectoryOnly)
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            // 通配展开的顺序在不同文件系统上不一致,排序让导入结果可复现。
            Array.Sort(matched, StringComparer.OrdinalIgnoreCase);
            foreach (string file in matched)
            {
                yield return file;
            }
        }
    }
}
