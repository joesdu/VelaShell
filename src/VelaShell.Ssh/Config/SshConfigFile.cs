// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)
//   行为规格: velashell-docs/zh/ssh/design/architecture.md §8 第 12 项
//             velashell-docs/zh/ssh/spec/09-dialing.md §7.1(Include / Match)、§7.4(分词:引号、反斜杠与注释)

using System.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Config;

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
///   两个文件互相 include 是很容易写出来的，而那会把解析变成死循环；还有<b>总量上限</b>
///   （<see cref="MaxIncludedFiles"/>），并且只读不超过 <see cref="MaxConfigFileBytes"/> 的普通文件。
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
        ParseInto(content, blocks, state);
        state.Flush(blocks);
        return blocks;
    }

    /// <summary>
    /// 把一个参数写成 <c>ssh_config</c> 里读得回原值的样子 —— 本库（<see cref="Parse"/>）与 OpenSSH 的 <c>ssh</c> 读回来都是 <paramref name="value"/>。
    /// </summary>
    /// <param name="value">参数的值（一个路径、一个用户名……）。</param>
    /// <param name="text">写进配置的样子，接在关键字与一个空格之后。</param>
    /// <returns>值里有换行（<c>\r</c> / <c>\n</c>）或 NUL 时为 <see langword="false"/>：ssh_config 的一行装不下它。</returns>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/09 §7.4〕能不改的地方不改：普通的 Windows 路径（<c>C:\Users\me\.ssh\id_ed25519</c>）原样写；
    /// 两个以上连着的反斜杠每个写成两个（命名管道 <c>\\.\pipe\x</c> 写成 <c>\\\\.\pipe\x</c>，UNC 路径同理）；引号前加 <c>\</c>；
    /// 有空白或 <c>#</c>、以 <c>=</c> 开头、或者是空串时整个加双引号。
    /// </remarks>
    public static bool TryFormatArgument(string value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text) =>
        SshConfigSyntax.TryFormat(value, out text);

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

        /// <summary>当前这个块的外层条件（见 <see cref="SshConfigBlock.Enclosing"/>）。</summary>
        public SshConfigBlock? Enclosing { get; set; }

        /// <summary>正在展开的 Include 所在的块：这期间新开的块都带上它作外层条件。</summary>
        public SshConfigBlock? IncludeScope { get; set; }

        /// <summary>把攒着的那个块收进去，并开一个新的。</summary>
        public void StartBlock(
            List<SshConfigBlock> blocks, List<string> patterns, SshConfigMatchCriteria? match)
        {
            Flush(blocks);
            Patterns = patterns;
            Match = match;
            Enclosing = IncludeScope;
#pragma warning disable IDE0028
            Current = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0028
        }

        public void Flush(List<SshConfigBlock> blocks)
        {
            if (Current.Count > 0)
            {
                blocks.Add(new SshConfigBlock(Patterns, Current, Match, Enclosing));
            }
        }

        /// <summary>当前块的条件（设置留空）；无条件（文件开头那种 <c>*</c>）时为 <see langword="null"/>。</summary>
        public SshConfigBlock? CurrentCondition() =>
            Match is null && Enclosing is null && Patterns is ["*"]
                ? null
                : new SshConfigBlock(Patterns, EmptySettings, Match, Enclosing);

        private static readonly Dictionary<string, List<string>> EmptySettings = [];
    }

    /// <summary>把一份文本解进 <paramref name="blocks"/>。</summary>
    /// <param name="content">文本。</param>
    /// <param name="blocks">解出来的块往这里加。</param>
    /// <param name="state">跨调用带着走的解析状态（Include 会递归进来）。</param>
    /// <remarks><c>Include</c> 行在这里跳过：展开它要读文件，由 <see cref="LoadAsync"/> 在逐行扫描时就地做。</remarks>
    private static void ParseInto(string content, List<SshConfigBlock> blocks, ParserState state)
    {
        foreach (string raw in content.Split('\n'))
        {
            // 〔velashell-docs/zh/ssh/spec/09 §7.4〕分词照 OpenSSH 的读法：两种引号、\\ \" \' 三种转义、落在词开头的 # 才是注释。
            SshConfigLine line = SshConfigSyntax.ReadLine(raw);
            string key = line.Key;
            if (key.Length == 0)
            {
                continue;
            }

            if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                // 引号没配对的 Host 行开一个哪台主机都配不上的块：只跳过这一行的话，
                // 它下面的设置会落进上一个块（常常是 Host *），对所有主机生效。
                state.StartBlock(
                    blocks,
                    line.QuotesBalanced && SshConfigSyntax.TrySplit(line.Value, out List<string>? patterns) ? patterns : [],
                    match: null);
                continue;
            }

            if (string.Equals(key, "Match", StringComparison.OrdinalIgnoreCase))
            {
                // Match 块的 Patterns 用 ["*"]：主机名那一维由条件里的
                // host / originalhost 管，Patterns 在这里不再承担筛选。
                // 引号没配对时同上：一个条件都没有的 Match 不生效。
                state.StartBlock(blocks, ["*"], line.QuotesBalanced ? ParseMatch(line.Value) : new SshConfigMatchCriteria([]));
                continue;
            }

            if (string.Equals(key, "Include", StringComparison.OrdinalIgnoreCase))
            {
                // 纯解析时跳过而不是报错 —— 报错会让一份带 Include 的
                // 正常配置完全不可用。
                continue;
            }

            if (!line.QuotesBalanced)
            {
                // 引号没配对：OpenSSH 整份配置不读，我们只是这一行不算 —— 不因一行写错让整份配置不可用，也不猜半截的值。
                continue;
            }

            if (!state.Current.TryGetValue(key, out List<string>? values))
            {
                values = [];
                state.Current[key] = values;
            }
            values.Add(line.Value);
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

        // 引号与转义照 spec/09 §7.4：Match exec "test -f /etc/special" 里那条命令是一个参数。
        // 不认引号的话它会被切成三段，后两段变成两个不认识的条件 —— 而不认识的条件判不了，于是整个块静默失效。
        List<string> tokens = SshConfigSyntax.TrySplit(value, out List<string>? words) ? words : [];

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


    /// <summary><c>Include</c> 的最大嵌套深度。</summary>
    /// <remarks>
    /// 与 OpenSSH 一致。环检测已经挡住了「互相 include」，
    /// 这个上限挡的是另一种：一长串合法但深不见底的链条。
    /// </remarks>
    public const int MaxIncludeDepth = 16;

    /// <summary>一次 <see cref="LoadAsync"/> 最多读多少个文件（含最外层那一份）；用完之后的 <c>Include</c> 不再展开。</summary>
    /// <remarks>
    /// 环检测只看当前这条包含链（同一个文件在两个块里各被包含一次是正常写法），深度上限只管「深」不管「宽」：
    /// N 个文件互相 <c>Include dir/*</c> 时，每一条不成环的链都要走一遍，展开次数是 N!/(N−k)! 的量级 ——
    /// 10 个文件约一千万次，读配置就成了挂死。真实的配置远用不到这么多文件。
    /// </remarks>
    public const int MaxIncludedFiles = 256;

    /// <summary>单个配置文件的大小上限；超过的整个跳过。</summary>
    public const int MaxConfigFileBytes = 1024 * 1024;

    /// <summary>读一份 <c>ssh_config</c>，并展开其中的 <c>Include</c>。</summary>
    /// <param name="path">路径；<see langword="null"/> 取 <see cref="DefaultPath"/>。</param>
    /// <param name="includeDirectory">
    /// <c>Include</c> 里的相对路径以哪个目录为准；<see langword="null"/> 表示以写着那行 <c>Include</c> 的文件所在目录为准。
    /// OpenSSH 对用户配置的规则是 <c>~/.ssh</c> —— 读别处的一份用户配置、又要照 OpenSSH 的口径展开时传它。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 文件不存在时返回空列表 —— 没有 <c>~/.ssh/config</c> 是完全正常的状态，
    /// 不该让调用方为此写一个 try。
    /// </remarks>
    public static async ValueTask<IReadOnlyList<SshConfigBlock>> LoadAsync(
        string? path = null, string? includeDirectory = null, CancellationToken cancellationToken = default)
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

        IncludeBudget budget = new();
        await LoadIntoAsync(actual, includeDirectory, blocks, state, visited, budget, depth: 0, cancellationToken)
            .ConfigureAwait(false);

        state.Flush(blocks);
        return blocks;
    }

    private static async ValueTask LoadIntoAsync(
        string path,
        string? includeDirectory,
        List<SshConfigBlock> blocks,
        ParserState state,
        HashSet<string> visited,
        IncludeBudget budget,
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
        //    只看**当前这条 include 链**：同一个文件在两个 Host 块里各被 include 一次是正常写法，
        //    按「读过就不再读」算的话，第二次会被当成环悄悄跳过。
        if (!File.Exists(full) || budget.FilesLeft <= 0 || !visited.Add(full))
        {
            return;
        }
        budget.FilesLeft--;

        try
        {
            if (await ReadConfigFileAsync(full, cancellationToken).ConfigureAwait(false) is not { } content)
            {
                return;
            }

            string baseDirectory = includeDirectory ?? Path.GetDirectoryName(full) ?? ".";

            // Include 是**就地展开**的：被包含文件里的设置排在 Include 那一行的位置上，
            // 而且落在那一行所在的 Host / Match 块里（带条件的包含）。这很要紧 —— ssh_config 是「先出现的值赢」。
            // 曾经是先把整个文件解完、再把被包含的内容接在后面：Include 之后的设置就跑到了被包含内容的前面。
            System.Text.StringBuilder chunk = new();
            foreach (string raw in content.Split('\n'))
            {
                // Include 的参数照 spec/09 §7.4 分词（带空格的路径写成 "~/my dir/x"）。
                SshConfigLine line = SshConfigSyntax.ReadLine(raw);

                if (!string.Equals(line.Key, "Include", StringComparison.OrdinalIgnoreCase))
                {
                    chunk.Append(raw).Append('\n');
                    continue;
                }

                ParseInto(chunk.ToString(), blocks, state);
                chunk.Clear();

                if (depth >= MaxIncludeDepth || !line.QuotesBalanced)
                {
                    continue;   // 太深了（或者引号没配对），不再往下。不报错 —— 配置文件的其余部分仍然可用。
                }

                // 带条件的包含：被包含文件里新开的 Host / Match 块，也只在 Include 所在的块生效时才生效；
                // 展开完回到 Include 所在的块，这个文件里 Include 之后的设置仍然归它。
                // 曾经被包含文件一开新块，外层条件就丢了（那些块对所有主机无条件生效），
                // 而 Include 之后的设置又落进了被包含文件的最后一个块里。
                (List<string> patterns, SshConfigMatchCriteria? match, SshConfigBlock? enclosing, SshConfigBlock? scope) =
                    (state.Patterns, state.Match, state.Enclosing, state.IncludeScope);
                state.IncludeScope = state.CurrentCondition();

                foreach (string included in ExpandIncludePaths(line.Value, baseDirectory))
                {
                    await LoadIntoAsync(included, includeDirectory, blocks, state, visited, budget, depth + 1, cancellationToken)
                        .ConfigureAwait(false);
                }

                state.IncludeScope = scope;
                state.StartBlock(blocks, patterns, match);
                state.Enclosing = enclosing;
            }

            ParseInto(chunk.ToString(), blocks, state);
        }
        finally
        {
            visited.Remove(full);
        }
    }

    /// <summary>一次 <see cref="LoadAsync"/> 还能读几个文件（见 <see cref="MaxIncludedFiles"/>）。</summary>
    private sealed class IncludeBudget
    {
        public int FilesLeft { get; set; } = MaxIncludedFiles;
    }

    /// <summary>读一个配置文件：只读普通文件，超过 <see cref="MaxConfigFileBytes"/> 的不读。</summary>
    /// <returns>文本；空文件、不是普通文件、太大时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>先看大小、再打开。</b>设备文件与 FIFO 报的大小是 0：<c>Include /dev/zero</c> 会无上限地读下去，
    /// 打开一个 FIFO 则一直阻塞到有人往里写 —— 两种情形取消令牌都管不到。按大小为 0 一律不打开，
    /// 真正的空文件本来也没有设置，跳过没有损失。
    /// </para>
    /// <para>
    /// 读的时候仍然按上限截住：大小是打开之前看的，文件在这中间变大或被换掉也不会无上限地读。
    /// 编码与 <see cref="File.ReadAllTextAsync(string, CancellationToken)"/> 一致：认 BOM，默认 UTF-8。
    /// </para>
    /// </remarks>
    private static async ValueTask<string?> ReadConfigFileAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length is 0 or > MaxConfigFileBytes)
        {
            return null;
        }

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(MaxConfigFileBytes + 1);
        try
        {
            int total = 0;
            await using (FileStream stream = new(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous,
            }))
            {
                int read;
                while (total <= MaxConfigFileBytes
                       && (read = await stream.ReadAsync(buffer.AsMemory(total, MaxConfigFileBytes + 1 - total), cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    total += read;
                }
            }

            if (total > MaxConfigFileBytes)
            {
                return null;
            }

            using StreamReader reader = new(
                new MemoryStream(buffer, 0, total, writable: false),
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>把一条 <c>Include</c> 的参数展开成实际的文件列表。</summary>
    /// <remarks>
    /// 支持三件事：一行里写多个路径（照 spec/09 §7.4 分词，引号里的空格不算 —— <c>"~/my dir/x"</c> 是一个路径）、<c>~</c> 展开、
    /// 以及最后一段里的 <c>*</c> / <c>?</c> 通配。
    /// 相对路径按<b>包含它的那个文件所在的目录</b>解析。
    /// 〔FW-E15〕曾经先去掉整行的引号再按空格切：带空格的路径被切成了两个。
    /// </remarks>
    internal static IReadOnlyList<string> ExpandIncludePaths(string spec, string baseDirectory)
    {
        List<string> result = [];

        foreach (string one in SshConfigSyntax.TrySplit(spec, out List<string>? words) ? words : [])
        {
            string candidate = one;

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
    /// <exception cref="ArgumentException">上下文带着 <see cref="SshConfigMatchContext.ExecEvaluator"/>：它是异步的，用 <see cref="ResolveAsync"/>。</exception>
    public static SshHostConfig Resolve(
        IReadOnlyList<SshConfigBlock> blocks, SshConfigMatchContext context)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(context);
        if (context.ExecEvaluator is not null)
        {
            throw new ArgumentException("带 ExecEvaluator 的上下文要用 ResolveAsync —— 求值器是异步的。", nameof(context));
        }

        // 没有求值器时整个求值同步完成（不碰任何 await 点）。
        ValueTask<SshHostConfig> resolved = ResolveCoreAsync(blocks, context, CancellationToken.None);
        Debug.Assert(resolved.IsCompleted, "没有 exec 求值器时求值应当同步完成");
        return resolved.Result;
    }

    /// <summary>算出某台主机最终生效的设置；<c>Match exec</c> 交给上下文里的异步求值器。</summary>
    /// <param name="blocks">解出来的块。</param>
    /// <param name="context">主机、用户、本机用户、<c>exec</c> 求值器……</param>
    /// <param name="cancellationToken">取消令牌，交给 <see cref="SshConfigMatchContext.ExecEvaluator"/>。</param>
    public static ValueTask<SshHostConfig> ResolveAsync(
        IReadOnlyList<SshConfigBlock> blocks, SshConfigMatchContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(context);
        return ResolveCoreAsync(blocks, context, cancellationToken);
    }

    private static async ValueTask<SshHostConfig> ResolveCoreAsync(
        IReadOnlyList<SshConfigBlock> blocks, SshConfigMatchContext context, CancellationToken cancellationToken)
    {
        string originalHost = context.OriginalHost ?? context.Host;
        SshHostConfig config = new(context.Host, originalHost);

        foreach (SshConfigBlock block in blocks)
        {
            if (!await AppliesAsync(block, config, context, originalHost, cancellationToken).ConfigureAwait(false))
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

    /// <summary>一个块此刻生效吗：它自己的条件，加上外层（带条件的 Include）的条件，都要满足。</summary>
    private static async ValueTask<bool> AppliesAsync(
        SshConfigBlock block, SshHostConfig config, SshConfigMatchContext context, string originalHost,
        CancellationToken cancellationToken)
    {
        for (SshConfigBlock? current = block; current is not null; current = current.Enclosing)
        {
            // Match host 比的是 HostName 改写之后的主机名（前面的块里若已给出 HostName），
            // Match originalhost 与 Host 块比的才是使用者输入的那个名字。
            // 曾经 Match host 一律拿输入的别名去比：为真实主机名写的 Match 块永远对不上。
            bool applies = current.Match is { } criteria
                ? await MatchesCriteriaAsync(criteria, context with
                {
                    Host = config.HostName,   // 前面的块给了 HostName 就用它（其中的 %h 已换成输入的名字）
                    OriginalHost = originalHost,
                }, cancellationToken).ConfigureAwait(false)
                : HostPatterns.MatchesList(current.Patterns, context.Host);

            if (!applies)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>Match</c> 块的条件都满足吗（条件之间是与）。</summary>
    /// <remarks>
    /// ⚠️ <b>判不了的条件让整块不生效 —— 取反也一样。</b>曾经「判不了」算成「不满足」，
    /// 前面加个 <c>!</c> 就成了「满足」：<c>Match !exec "…"</c> 在我们不执行命令时对所有主机生效，
    /// 那正是写配置的人想排除的情形。判不了就是判不了，不因为一个 <c>!</c> 变成真的。
    /// </remarks>
    private static async ValueTask<bool> MatchesCriteriaAsync(
        SshConfigMatchCriteria criteria, SshConfigMatchContext context, CancellationToken cancellationToken)
    {
        if (criteria.Conditions.Count == 0)
        {
            return false;   // `Match` 后面什么都没写 —— 不匹配比乱匹配安全
        }

        foreach (SshConfigMatchCondition condition in criteria.Conditions)
        {
            if (await EvaluateConditionAsync(condition, context, cancellationToken).ConfigureAwait(false) is not { } result)
            {
                return false;
            }

            if (result == condition.Negated)
            {
                return false;
            }
        }

        return true;
    }

    /// <returns>满足 / 不满足；<see langword="null"/> 表示判不了（信息不足、不支持、不执行）。</returns>
    private static async ValueTask<bool?> EvaluateConditionAsync(
        SshConfigMatchCondition condition, SshConfigMatchContext context, CancellationToken cancellationToken)
    {
        if (condition.Keyword == "exec")
        {
            return await EvaluateExecAsync(condition, context, cancellationToken).ConfigureAwait(false);
        }

        return condition.Keyword switch
        {
            "all" => true,
            // 我们不做主机名规范化（CanonicalizeHostname），也没有「最后再解析一遍」这一轮，
            // 所以这两个判不了。静默当成真或假，都会让一份为规范化写的配置在我们这里产生不同的结果。
            "canonical" or "final" => null,
            "host" => HostPatterns.MatchesList(condition.Patterns, context.Host),
            "originalhost" => HostPatterns.MatchesList(condition.Patterns, context.OriginalHost ?? context.Host),
            "user" => context.User is { } user ? HostPatterns.MatchesList(condition.Patterns, user) : null,
            "localuser" => context.LocalUser is { } local ? HostPatterns.MatchesList(condition.Patterns, local) : null,
            // 不认识的条件判不了。认识错了比不认识更糟：那会让一个本不该生效的块生效。
            _ => null,
        };
    }

    /// <summary><c>Match exec</c>：交给调用方的求值器（见 <see cref="SshConfigMatchContext.ExecEvaluator"/>）。</summary>
    /// <returns>满足 / 不满足；没有求值器、或者命令里的记号不能安全地代入时 <see langword="null"/>（判不了）。</returns>
    /// <remarks>
    /// 〔FW-D5〕命令里的 <c>%h %n %r %u %%</c> 由库按 <c>ProxyCommand</c> 同一套白名单展开好再交出去（CVE-2023-51385 那一类：
    /// 主机名、用户名常常不是写配置的人给的）。值不安全、或者有不认识的记号，这一条就判不了、不去问求值器。
    /// 曾经只交出原样的命令（同步、没有令牌、没有主机与用户）：调用方自己去代入 <c>%h</c>，就回到了那一类问题上。
    /// </remarks>
    private static async ValueTask<bool?> EvaluateExecAsync(
        SshConfigMatchCondition condition, SshConfigMatchContext context, CancellationToken cancellationToken)
    {
        if (context.ExecEvaluator is not { } evaluator)
        {
            return null;   // ⚠️ 默认不执行。没有求值器就判不了 —— 见 SshConfigMatchContext.ExecEvaluator 上的说明。
        }

        string command = string.Join(',', condition.Patterns);
        if (ExpandExecCommand(command, context) is not { } expanded)
        {
            return null;
        }

        SshMatchExecRequest request = new(
            command, expanded, context.Host, context.OriginalHost ?? context.Host, context.User, context.LocalUser);
        return await evaluator(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>展开 <c>Match exec</c> 命令里的记号；有不认识的、或者值不能安全地交给 shell 时为 <see langword="null"/>。</summary>
    private static string? ExpandExecCommand(string command, SshConfigMatchContext context)
    {
        System.Text.StringBuilder result = new(command.Length + 32);
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c != '%' || i + 1 >= command.Length)
            {
                result.Append(c);
                continue;
            }

            (string? value, bool allowAt) = command[++i] switch
            {
                'h' => (context.Host, false),
                'n' => (context.OriginalHost ?? context.Host, false),
                'r' => (context.User, true),
                'u' => (context.LocalUser, true),
                '%' => ("%", false),
                _ => (null, false),
            };

            if (value is null || (value != "%" && !Transport.ProxyCommandDialer.IsShellSafe(value, allowAt)))
            {
                return null;
            }
            result.Append(value);
        }
        return result.ToString();
    }
}
