// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)
//   行为规格: velashell-docs/zh/ssh/design/architecture.md §8 第 12 项

using System.Globalization;

namespace VelaShell.Ssh.Config;

/// <summary><c>ssh_config</c> 里的一个 <c>Host</c> 块。</summary>
/// <param name="Patterns">这个块管哪些主机（支持 <c>*</c>、<c>?</c>、<c>!</c>）。</param>
/// <param name="Settings">块里的设置，键不区分大小写。</param>
/// <param name="Match">
/// <c>Match</c> 块的条件；<see langword="null"/> 表示这是普通的 <c>Host</c> 块。
/// </param>
public sealed record SshConfigBlock(
    IReadOnlyList<string> Patterns,
    IReadOnlyDictionary<string, List<string>> Settings,
    SshConfigMatchCriteria? Match = null);

/// <summary>把 <c>ssh_config</c> 解完之后，某一台主机最终生效的设置。</summary>
public sealed partial class SshHostConfig
{
    // ⚠️ IDE0028 会建议把它简化成 []。**不能听** —— 那会把
    //    OrdinalIgnoreCase 丢掉，而 ssh_config 的键是不区分大小写的
    //    （`HostName` 与 `hostname` 是同一个键）。
#pragma warning disable IDE0028
    private readonly Dictionary<string, List<string>> _settings = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0028

    internal SshHostConfig(string host) => QueriedHost = host;

    /// <summary>当初查的是哪个名字。</summary>
    public string QueriedHost { get; }

    /// <summary>真正要连的主机（<c>HostName</c>，没有就是 <see cref="QueriedHost"/>）。</summary>
    public string HostName => First("HostName") ?? QueriedHost;

    /// <summary>端口。</summary>
    public int Port =>
        int.TryParse(First("Port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            ? port
            : 22;

    /// <summary>用户名。</summary>
    public string? User => First("User");

    /// <summary>私钥文件（<c>IdentityFile</c> 可以出现多次，按顺序）。</summary>
    public IReadOnlyList<string> IdentityFiles => All("IdentityFile");

    /// <summary>跳板（<c>ProxyJump</c>）。</summary>
    public string? ProxyJump => First("ProxyJump");

    /// <summary><c>ProxyCommand</c>：经一个外部程序连接（<c>none</c> 表示不用）。</summary>
    public string? ProxyCommand => First("ProxyCommand");

    /// <summary><c>ConnectTimeout</c>（秒）；没配为 <see langword="null"/>。</summary>
    public int? ConnectTimeoutSeconds =>
        int.TryParse(First("ConnectTimeout"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value > 0
            ? value
            : null;

    /// <summary><c>ForwardX11</c>。</summary>
    public bool ForwardX11 => IsYes(First("ForwardX11"));

    /// <summary><c>ForwardX11Trusted</c>。</summary>
    public bool ForwardX11Trusted => IsYes(First("ForwardX11Trusted"));

    /// <summary><c>StrictHostKeyChecking</c> 的原文（<c>yes</c> / <c>no</c> / <c>ask</c> / <c>accept-new</c>）。</summary>
    public string? StrictHostKeyChecking => First("StrictHostKeyChecking");

    /// <summary><c>UserKnownHostsFile</c>。</summary>
    public string? UserKnownHostsFile => First("UserKnownHostsFile");

    /// <summary>是否只用显式给出的密钥（<c>IdentitiesOnly yes</c>）。</summary>
    public bool IdentitiesOnly => IsYes(First("IdentitiesOnly"));

    /// <summary><c>ForwardAgent</c>。</summary>
    public bool ForwardAgent => IsYes(First("ForwardAgent"));

    /// <summary><c>Compression</c>。</summary>
    public bool Compression => IsYes(First("Compression"));

    /// <summary><c>ServerAliveInterval</c>（秒，<c>0</c> = 关）。</summary>
    public int ServerAliveInterval =>
        int.TryParse(
            First("ServerAliveInterval"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    /// <summary><c>ServerAliveCountMax</c>。</summary>
    public int ServerAliveCountMax =>
        int.TryParse(
            First("ServerAliveCountMax"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 3;

    /// <summary>取某个键的第一个值。</summary>
    /// <remarks>
    /// <b>「第一个」是对的，不是「最后一个」。</b>
    /// <c>ssh_config</c> 的规则是<b>先出现的赢</b> —— 与大多数配置格式相反。
    /// 这也是为什么 OpenSSH 的样例里 <c>Host *</c> 总是放在文件末尾。
    /// </remarks>
    public string? First(string key) =>
        _settings.TryGetValue(key, out List<string>? values) && values.Count > 0 ? values[0] : null;

    /// <summary>取某个键的全部值，按出现顺序。</summary>
    public IReadOnlyList<string> All(string key) =>
        _settings.TryGetValue(key, out List<string>? values) ? values : [];

    internal void Add(string key, string value)
    {
        if (!_settings.TryGetValue(key, out List<string>? values))
        {
            values = [];
            _settings[key] = values;
        }
        values.Add(value);
    }

    private static bool IsYes(string? value) =>
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}

/// <summary>读 <c>ssh_config</c>。</summary>
/// <remarks>
/// <para>
/// <b>我们只解析，不替使用者决定。</b>解出来的结果交给调用方，
/// 由它决定要不要用、用哪几项 —— 库自作主张去读 <c>~/.ssh/config</c>
/// 会让「为什么连的不是我写的那台机器」变成一个很难查的问题。
/// </para>
/// <para>
/// <b><c>Include</c> 与 <c>Match</c> 都支持</b>，但各带一条安全约束：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <c>Include</c> 只在 <see cref="LoadAsync"/> 里展开（<see cref="Parse"/> 是
///   纯文本解析，没有基准目录也不该碰文件系统）。展开时有<b>深度上限与环检测</b> ——
///   两个文件互相 include 是很容易写出来的，而那会把解析变成死循环。
///   </description></item>
///   <item><description>
///   <c>Match exec</c> <b>默认不执行</b>。它意味着「解析一份配置文件就能在本机跑任意程序」，
///   而配置文件常常是从别处拷来的。要用就自己传一个
///   <see cref="SshConfigMatchContext.ExecEvaluator"/> 进来 ——
///   这个决定必须明确地落在调用方身上。
///   </description></item>
/// </list>
/// </remarks>
public static partial class SshConfigFile
{
    /// <summary>用户默认的 <c>~/.ssh/config</c> 路径。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>解析一份 <c>ssh_config</c> 文本。</summary>
    /// <remarks>
    /// <b><c>Include</c> 在这里不展开</b> —— 纯文本解析没有基准目录，
    /// 也不该去碰文件系统。要展开就走 <see cref="LoadAsync"/>。
    /// </remarks>
    public static IReadOnlyList<SshConfigBlock> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<SshConfigBlock> blocks = [];
        ParserState state = new();
        ParseInto(content, blocks, state, includes: null);
        state.Flush(blocks);
        return blocks;
    }

    /// <summary>解析器在块之间要带着走的东西。</summary>
    private sealed class ParserState
    {
        // ⚠️ IDE0028 会建议简化成 []。**不能听** —— 会把 OrdinalIgnoreCase 丢掉。
#pragma warning disable IDE0028
        public Dictionary<string, List<string>> Current { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0028

        /// <summary>文件开头到第一个 Host / Match 之前的设置，对所有主机生效。</summary>
        public List<string> Patterns { get; set; } = ["*"];

        public SshConfigMatchCriteria? Match { get; set; }

        /// <summary>把攒着的那个块收进去，并开一个新的。</summary>
        public void StartBlock(
            List<SshConfigBlock> blocks, List<string> patterns, SshConfigMatchCriteria? match)
        {
            Flush(blocks);
            Patterns = patterns;
            Match = match;
#pragma warning disable IDE0028
            Current = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0028
        }

        public void Flush(List<SshConfigBlock> blocks)
        {
            if (Current.Count > 0)
            {
                blocks.Add(new SshConfigBlock(Patterns, Current, Match));
            }
        }
    }

    /// <summary>把一份文本解进 <paramref name="blocks"/>。</summary>
    /// <param name="content">文本。</param>
    /// <param name="blocks">解出来的块往这里加。</param>
    /// <param name="state">跨调用带着走的解析状态（Include 会递归进来）。</param>
    /// <param name="includes">
    /// 展开 <c>Include</c> 的回调；<see langword="null"/> 表示不展开（纯解析）。
    /// </param>
    private static void ParseInto(
        string content,
        List<SshConfigBlock> blocks,
        ParserState state,
        Action<string, List<SshConfigBlock>, ParserState>? includes)
    {
        foreach (string raw in content.Split('\n'))
        {
            string line = StripComment(raw);
            if (line.Length == 0)
            {
                continue;
            }

            (string key, string value) = SplitKeyValue(line);
            if (key.Length == 0)
            {
                continue;
            }

            if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                state.StartBlock(
                    blocks,
                    [.. value.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
                    match: null);
                continue;
            }

            if (string.Equals(key, "Match", StringComparison.OrdinalIgnoreCase))
            {
                // Match 块的 Patterns 用 ["*"]：主机名那一维由条件里的
                // host / originalhost 管，Patterns 在这里不再承担筛选。
                state.StartBlock(blocks, ["*"], ParseMatch(value));
                continue;
            }

            if (string.Equals(key, "Include", StringComparison.OrdinalIgnoreCase))
            {
                // 纯解析时跳过而不是报错 —— 报错会让一份带 Include 的
                // 正常配置完全不可用。
                includes?.Invoke(value, blocks, state);
                continue;
            }

            if (!state.Current.TryGetValue(key, out List<string>? values))
            {
                values = [];
                state.Current[key] = values;
            }
            values.Add(value);
        }
    }

    /// <summary>解析 <c>Match</c> 后面那一串条件。</summary>
    /// <remarks>
    /// 形如 <c>Match host a,b user root</c>：条件名后面跟一组逗号分隔的模式，
    /// 而 <c>all</c> / <c>canonical</c> / <c>final</c> 后面不跟东西。
    /// </remarks>
    internal static SshConfigMatchCriteria ParseMatch(string value)
    {
        List<SshConfigMatchCondition> conditions = [];
        List<string> tokens = TokenizeRespectingQuotes(value);

        for (int i = 0; i < tokens.Count; i++)
        {
            string keyword = tokens[i];
            bool negated = keyword.StartsWith('!');
            if (negated)
            {
                keyword = keyword[1..];
            }

            keyword = keyword.ToLowerInvariant();

            // 这几个是「不带参数」的条件。
            if (keyword is "all" or "canonical" or "final")
            {
                conditions.Add(new SshConfigMatchCondition(keyword, [], negated));
                continue;
            }

            // 取不到参数就当成一个空条件 —— 空条件永远不匹配，比静默忽略安全。
            string argument = i + 1 < tokens.Count ? tokens[++i] : "";

            // ⚠️ **exec 的参数不能按逗号切。** 它是一条 shell 命令，
            //    里面完全可以有逗号（`test -f a,b`）。切开的后果是
            //    把一条命令拆成几段，然后谁都对不上。
            IReadOnlyList<string> patterns = keyword == "exec"
                ? (argument.Length == 0 ? [] : [argument])
                : [.. argument.Split(',', StringSplitOptions.RemoveEmptyEntries)];

            conditions.Add(new SshConfigMatchCondition(keyword, patterns, negated));
        }

        return new SshConfigMatchCriteria(conditions);
    }

    /// <summary>按空白切词，但引号里的空白不算分隔。</summary>
    /// <remarks>
    /// <c>Match exec "test -f /etc/special"</c> 里那条命令是<b>一个</b>参数。
    /// 不认引号的话它会被切成三段，然后后两段变成两个不认识的条件 ——
    /// 而不认识的条件不匹配，于是整个块静默失效。
    /// </remarks>
    private static List<string> TokenizeRespectingQuotes(string value)
    {
        List<string> tokens = [];
        System.Text.StringBuilder current = new();
        bool quoted = false;

        foreach (char c in value)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && (c == ' ' || c == '\t'))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary><c>Include</c> 的最大嵌套深度。</summary>
    /// <remarks>
    /// 与 OpenSSH 一致。环检测已经挡住了「互相 include」，
    /// 这个上限挡的是另一种：一长串合法但深不见底的链条。
    /// </remarks>
    public const int MaxIncludeDepth = 16;

    /// <summary>读一份 <c>ssh_config</c>，并展开其中的 <c>Include</c>。</summary>
    /// <param name="path">路径；<see langword="null"/> 取 <see cref="DefaultPath"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 文件不存在时返回空列表 —— 没有 <c>~/.ssh/config</c> 是完全正常的状态，
    /// 不该让调用方为此写一个 try。
    /// </remarks>
    public static async ValueTask<IReadOnlyList<SshConfigBlock>> LoadAsync(
        string? path = null, CancellationToken cancellationToken = default)
    {
        string actual = path ?? DefaultPath;

        if (!File.Exists(actual))
        {
            return [];
        }

        List<SshConfigBlock> blocks = [];
        ParserState state = new();

        // 环检测按**规范化后的全路径**比，不按写法比 ——
        // `~/.ssh/a` 和 `/home/joe/.ssh/a` 是同一个文件。
        // ⚠️ IDE0028 会建议简化成 []。**不能听** —— 那会把比较器丢掉。
#pragma warning disable IDE0028
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0028

        await LoadIntoAsync(actual, blocks, state, visited, depth: 0, cancellationToken)
            .ConfigureAwait(false);

        state.Flush(blocks);
        return blocks;
    }

    private static async ValueTask LoadIntoAsync(
        string path,
        List<SshConfigBlock> blocks,
        ParserState state,
        HashSet<string> visited,
        int depth,
        CancellationToken cancellationToken)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return;   // 路径本身就不合法 —— 当作没有这个文件
        }

        // ⚠️ **环检测**：两个文件互相 include 是很容易写出来的
        //    （`config` include `conf.d/*`，而某个 conf.d 里又 include 回 `config`）。
        //    没有这一步就是死循环 —— 而那表现为「读配置的时候整个进程不动了」。
        if (!visited.Add(full))
        {
            return;
        }

        if (!File.Exists(full))
        {
            return;
        }

        string content = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);

        // Include 是**就地展开**的：被包含文件里的设置排在 Include 那一行的位置上。
        // 这很要紧，因为 ssh_config 是「先出现的值赢」。
        List<(string Spec, int Index)> pending = [];
        ParseInto(content, blocks, state, (spec, _, _) => pending.Add((spec, pending.Count)));

        // ParseInto 是同步的，而读文件是异步的 —— 所以先把 Include 收集起来，
        // 再按顺序展开。代价是：同一个文件里 Include 之后的设置会排在
        // 被包含内容**之前**。
        //
        // 这一点与 OpenSSH 不同，但要做到完全一致就得让整个解析器变成异步的，
        // 而实际配置里 Include 几乎总在文件开头（`Include ~/.ssh/conf.d/*` 那种写法）。
        // 〔决策〕先记在这里，等有人真被它咬到再改。
        if (pending.Count == 0)
        {
            return;
        }

        if (depth >= MaxIncludeDepth)
        {
            return;   // 太深了，停下。不报错 —— 配置文件的其余部分仍然可用。
        }

        string baseDirectory = Path.GetDirectoryName(full) ?? ".";

        foreach ((string spec, _) in pending)
        {
            foreach (string included in ExpandIncludePaths(spec, baseDirectory))
            {
                await LoadIntoAsync(
                    included, blocks, state, visited, depth + 1, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>把一条 <c>Include</c> 的参数展开成实际的文件列表。</summary>
    /// <remarks>
    /// 支持三件事：一行里写多个路径（空格分隔）、<c>~</c> 展开、
    /// 以及最后一段里的 <c>*</c> / <c>?</c> 通配。
    /// 相对路径按<b>包含它的那个文件所在的目录</b>解析。
    /// </remarks>
    internal static IReadOnlyList<string> ExpandIncludePaths(string spec, string baseDirectory)
    {
        List<string> result = [];

        foreach (string one in spec.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = one.Trim('"');

            if (candidate.StartsWith('~'))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                candidate = Path.Combine(home, candidate.TrimStart('~').TrimStart('/', '\\'));
            }
            else if (!Path.IsPathRooted(candidate))
            {
                candidate = Path.Combine(baseDirectory, candidate);
            }

            string directory = Path.GetDirectoryName(candidate) ?? baseDirectory;
            string leaf = Path.GetFileName(candidate);

            if (!leaf.Contains('*', StringComparison.Ordinal)
                && !leaf.Contains('?', StringComparison.Ordinal))
            {
                result.Add(candidate);
                continue;
            }

            if (!Directory.Exists(directory))
            {
                continue;
            }

            // 通配的结果要**排序**：目录枚举的顺序在不同文件系统上不一样，
            // 而 ssh_config 是「先出现的值赢」—— 顺序不定就意味着结果不定。
            try
            {
                result.AddRange(Directory.EnumerateFiles(directory, leaf).Order(StringComparer.Ordinal));
            }
            catch (Exception)
            {
                // 目录读不了（权限）—— 跳过这一条，别让整份配置失效。
            }
        }

        return result;
    }

    /// <summary>算出某台主机最终生效的设置。</summary>
    /// <remarks>
    /// 按块的出现顺序合并，<b>先出现的值赢</b> —— 这是 <c>ssh_config</c> 的规则，
    /// 与大多数配置格式相反。所以 <c>Host *</c> 要放在文件末尾才起「兜底」的作用。
    /// </remarks>
    public static SshHostConfig Resolve(IReadOnlyList<SshConfigBlock> blocks, string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Resolve(blocks, SshConfigMatchContext.ForHost(host));
    }

    /// <summary>算出某台主机最终生效的设置，<c>Match</c> 块按上下文判断。</summary>
    /// <param name="blocks">解出来的块。</param>
    /// <param name="context">主机、用户、本机用户…… <c>Match</c> 要拿它判断。</param>
    /// <remarks>
    /// 按块的出现顺序合并，<b>先出现的值赢</b> —— 这是 <c>ssh_config</c> 的规则，
    /// 与大多数配置格式相反。所以 <c>Host *</c> 要放在文件末尾才起「兜底」的作用。
    /// </remarks>
    public static SshHostConfig Resolve(
        IReadOnlyList<SshConfigBlock> blocks, SshConfigMatchContext context)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(context);

        SshHostConfig config = new(context.Host);

        foreach (SshConfigBlock block in blocks)
        {
            bool applies = block.Match is { } criteria
                ? MatchesCriteria(criteria, context)
                : Matches(block.Patterns, context.Host);

            if (!applies)
            {
                continue;
            }

            foreach ((string key, List<string> values) in block.Settings)
            {
                foreach (string value in values)
                {
                    config.Add(key, value);
                }
            }
        }

        return config;
    }

    /// <summary><c>Match</c> 块的条件都满足吗（条件之间是与）。</summary>
    private static bool MatchesCriteria(
        SshConfigMatchCriteria criteria, SshConfigMatchContext context)
    {
        if (criteria.Conditions.Count == 0)
        {
            return false;   // `Match` 后面什么都没写 —— 不匹配比乱匹配安全
        }

        foreach (SshConfigMatchCondition condition in criteria.Conditions)
        {
            bool satisfied = EvaluateCondition(condition, context);
            if (condition.Negated)
            {
                satisfied = !satisfied;
            }

            if (!satisfied)
            {
                return false;
            }
        }

        return true;
    }

    private static bool EvaluateCondition(
        SshConfigMatchCondition condition, SshConfigMatchContext context)
    {
        switch (condition.Keyword)
        {
            case "all":
                return true;

            // 我们不做主机名规范化（CanonicalizeHostname），所以这两个
            // **永远不匹配**。静默当成 true 会让一份为规范化写的配置
            // 在我们这里产生完全不同的结果。
            case "canonical":
            case "final":
                return false;

            case "host":
                return MatchesAny(condition.Patterns, context.Host);

            case "originalhost":
                return MatchesAny(condition.Patterns, context.OriginalHost ?? context.Host);

            case "user":
                return context.User is { } user && MatchesAny(condition.Patterns, user);

            case "localuser":
                return context.LocalUser is { } local && MatchesAny(condition.Patterns, local);

            case "exec":
                // ⚠️ 默认不执行。没有求值器就**不匹配** —— 见
                //    SshConfigMatchContext.ExecEvaluator 上的说明。
                return context.ExecEvaluator is { } evaluator
                    && evaluator(string.Join(',', condition.Patterns));

            default:
                // 不认识的条件不匹配。认识错了比不认识更糟：
                // 那会让一个本不该生效的块生效。
                return false;
        }
    }

    private static bool MatchesAny(IReadOnlyList<string> patterns, string text)
    {
        bool matched = false;

        foreach (string pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (WildcardMatch(pattern[1..], text))
                {
                    return false;
                }
                continue;
            }

            if (WildcardMatch(pattern, text))
            {
                matched = true;
            }
        }

        return matched;
    }

    private static bool Matches(IReadOnlyList<string> patterns, string host)
    {
        bool matched = false;

        foreach (string pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                // 取反模式对上了 → **整个块都不适用**，哪怕别的模式也对上了。
                if (WildcardMatch(pattern[1..], host))
                {
                    return false;
                }
                continue;
            }

            if (WildcardMatch(pattern, host))
            {
                matched = true;
            }
        }

        return matched;
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#', StringComparison.Ordinal);
        return (hash >= 0 ? line[..hash] : line).Trim();
    }

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        // ssh_config 允许 `Key Value`、`Key=Value`、以及 `Key = Value`。
        int separator = line.IndexOfAny([' ', '\t', '=']);
        if (separator < 0)
        {
            return (line, "");
        }

        string key = line[..separator];
        string value = line[(separator + 1)..].TrimStart(' ', '\t', '=').Trim();

        // 带引号的值去掉引号（路径里有空格时会这么写）。
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return (key, value);
    }

    private static bool WildcardMatch(string pattern, string text)
    {
        int p = 0;
        int t = 0;
        int starPattern = -1;
        int starText = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length
                && (pattern[p] == '?'
                    || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starText = t;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                t = ++starText;
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
}
