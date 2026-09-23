#!/usr/bin/env dotnet
#:property LangVersion=preview
#:property Nullable=enable
// 这是一个开发期工具，不是产品代码：把仓库给库定的那几条严格规则关掉，
// 免得每次跑门禁都先刷一屏与门禁本身无关的风格警告。
#:property TreatWarningsAsErrors=false
#:property EnforceCodeStyleInBuild=false
#:property AnalysisLevel=none
#:property IsAotCompatible=false
// 宿主根 Directory.Build.props 全仓打开了 GenerateDocumentationFile;脚本里的 /// 注释不是 API 文档。
#:property GenerateDocumentationFile=false
// file-based app 默认关掉反射式 JSON（面向 AOT）。报告是本地文件，开回来即可。
#:property JsonSerializerIsReflectionEnabledByDefault=true

// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// ============================================================================
// 相似度门禁 —— src/VelaShell.Ssh/AGENTS.md §2 纪律 6
// ============================================================================
//
// 把参照实现的源码拉到临时目录，对我们的 src/VelaShell.Ssh/ 做 **token 级 n-gram 指纹比对**，
// 超阈值就让构建红灯。
//
// 它的价值不只是防呆 —— 它是一份**可以直接交给质疑者的证据**：
// 回答不是「我们没抄」，而是「每次提交都自动比对，阈值在这，历史记录在这」。
//
// 为什么是 token 级而不是文本级：改个变量名、换个大括号风格、把注释删掉，
// 文本 diff 就面目全非，而 token 序列纹丝不动。抄袭检测（MOSS / JPlag）
// 用的都是这一层。
//
// 为什么保留标识符：标识符的**组合**恰恰是 src/VelaShell.Ssh/AGENTS.md 纪律 3 要防的东西。
// 把它们归一化成 ID0/ID1 会让「整套类型体系照搬、只改名字」通过检测。
//
// 用法（通常由 CI 调用，本地也能跑）：
//   dotnet run scripts/ssh/similarity-gate/similarity-gate.cs -- --fetch
//   dotnet run scripts/ssh/similarity-gate/similarity-gate.cs
//
// ============================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;

const int    GramSize        = 25;   // 连续 25 个 token 完全一致才算一次命中
const int    DefaultMaxRun   = 40;   // 单文件对允许的最长公共 token 串
// 1.0% —— 这个数字是标定出来的，不是拍的（2026-09-21）：
//
//   SSH.NET  对 Tmds.Ssh（两个彼此独立的实现）   2.41%  ← 底噪
//   本仓 src 对两者合并语料                       0.17%  ← 我们
//
// 底噪 2.41% 说明「都在直译同一批 RFC」本身就能产生两个多百分点的重合，
// 所以把阈值设在底噪**之下**并不是在判定抄袭 —— 它是一条针对**本仓**的
// 早期警报线：我们现在有近 6 倍余量，真涨到 1% 就说明有什么东西变了，
// 值得在它变成 3% 之前去看一眼。
//
// 原来的 3.0 是标定前的猜测，正好压在底噪上方 —— 那个位置最没用：
// 既抓不到早期漂移，又只比「和一个无关实现一样像」好一点点。
const double DefaultMaxRatio = 1.0;  // 全仓被覆盖 token 的百分比上限

string repoRoot   = FindRepoRoot();
string oursDir    = Path.Combine(repoRoot, "src", "VelaShell.Ssh");
string corpusDir  = Path.Combine(repoRoot, "scripts", "ssh", "similarity-gate", ".corpus");
string reportDir  = Path.Combine(repoRoot, "scripts", "ssh", "similarity-gate", ".reports");
string allowPath  = Path.Combine(repoRoot, "scripts", "ssh", "similarity-gate", "allowlist.txt");
string sourcePath = Path.Combine(repoRoot, "scripts", "ssh", "similarity-gate", "sources.json");
string boilerPath = Path.Combine(repoRoot, "scripts", "ssh", "similarity-gate", "boilerplate.cs.txt");

int    maxRun   = DefaultMaxRun;
double maxRatio = DefaultMaxRatio;
bool   fetch    = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--fetch":     fetch = true; break;
        case "--max-run":   maxRun = int.Parse(args[++i]); break;
        case "--max-ratio": maxRatio = double.Parse(args[++i]); break;
        case "--ours":      oursDir = args[++i]; break;
        case "--corpus":    corpusDir = args[++i]; break;
        case "-h" or "--help":
            Console.WriteLine("usage: similarity-gate [--fetch] [--max-run N] [--max-ratio PCT]");
            return 0;
    }
}

if (fetch)
{
    return await FetchCorpusAsync(sourcePath, corpusDir) ? 0 : 1;
}

if (!Directory.Exists(corpusDir))
{
    Console.Error.WriteLine($"""
        语料目录不存在: {corpusDir}
        先跑一次: dotnet run scripts/ssh/similarity-gate/similarity-gate.cs -- --fetch
        """);
    return 1;
}

// ---------------------------------------------------------------- 读白名单

HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);
if (File.Exists(allowPath))
{
    foreach (string line in File.ReadAllLines(allowPath))
    {
        string t = line.Trim();
        if (t.Length == 0 || t.StartsWith('#')) continue;
        allowed.Add(t.Replace('\\', '/'));
    }
}

// ---------------------------------------------------------------- 建语料索引

Console.WriteLine($"语料: {corpusDir}");
var corpusIndex = new Dictionary<ulong, List<(string File, int Pos)>>();
int corpusFiles = 0, corpusTokens = 0;

foreach (string f in EnumerateCs(corpusDir))
{
    string[] toks = Tokenize(File.ReadAllText(f));
    if (toks.Length < GramSize) continue;
    corpusFiles++;
    corpusTokens += toks.Length;
    string rel = Path.GetRelativePath(corpusDir, f).Replace('\\', '/');

    for (int i = 0; i + GramSize <= toks.Length; i++)
    {
        ulong h = HashGram(toks, i, GramSize);
        if (!corpusIndex.TryGetValue(h, out var list))
        {
            corpusIndex[h] = list = [];
        }
        // 同一个 k-gram 在语料里重复出现很多次时只留前几个位置 —— 报告里够用，内存可控
        if (list.Count < 4) list.Add((rel, i));
    }
}
Console.WriteLine($"  {corpusFiles} 个文件, {corpusTokens:N0} 个 token, {corpusIndex.Count:N0} 个 k-gram");

// ⚠️ **空语料必须是硬错误，不能是 0.00% 的绿灯。**
//
// `.corpus/` 不进仓库（.gitignore），所以在一台没跑过 --fetch 的机器上
// 比对会一路顺畅地跑完，然后报出一个漂亮的「覆盖率 0.00%，门禁通过」——
// 而它的真实含义是「和 0 个文件比过了」。
//
// 这种失败模式最危险的地方在于**它看起来像成功**：越是想确认自己没抄，
// 越容易被这行绿字安抚下来。所以这里宁可红。
if (corpusIndex.Count == 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("❌ 语料是空的 —— 这次比对没有任何意义。");
    Console.Error.WriteLine($"   目录: {corpusDir}");
    Console.Error.WriteLine("   先拉语料：dotnet run scripts/ssh/similarity-gate/similarity-gate.cs -- --fetch");
    return 2;
}

// ------------------------------------------------- 剔除 BCL 逼出来的样板
//
// 「不可 Seek 的 Stream 必须 override Length/Position 并抛 NotSupportedException」
// 这类代码，任何实现都只能这么写 —— 相同不说明任何问题。
// 把它们的 k-gram 从索引里拿掉，比把整个文件加进 allowlist.txt 精确得多：
// 同一个文件里真正的问题仍然会被抓住。
int boilerplateGrams = 0;
if (File.Exists(boilerPath))
{
    string[] bToks = Tokenize(File.ReadAllText(boilerPath));
    for (int i = 0; i + GramSize <= bToks.Length; i++)
    {
        if (corpusIndex.Remove(HashGram(bToks, i, GramSize)))
        {
            boilerplateGrams++;
        }
    }
    Console.WriteLine($"  剔除 BCL 样板 k-gram: {boilerplateGrams:N0}（{Path.GetFileName(boilerPath)}）");
}

// ---------------------------------------------------------------- 比对

Console.WriteLine($"\n本仓: {oursDir}");
var findings = new List<Finding>();
int ourFiles = 0;
long ourTokensTotal = 0, ourTokensCovered = 0;

foreach (string f in EnumerateCs(oursDir))
{
    string rel = Path.GetRelativePath(repoRoot, f).Replace('\\', '/');
    if (allowed.Contains(rel))
    {
        Console.WriteLine($"  跳过(白名单) {rel}");
        continue;
    }

    string[] toks = Tokenize(File.ReadAllText(f));
    ourFiles++;
    ourTokensTotal += toks.Length;
    if (toks.Length < GramSize) continue;

    // 每个位置是否命中语料
    bool[] hit = new bool[toks.Length];
    var    hitWhere = new Dictionary<int, (string File, int Pos)>();

    for (int i = 0; i + GramSize <= toks.Length; i++)
    {
        if (corpusIndex.TryGetValue(HashGram(toks, i, GramSize), out var where))
        {
            for (int j = i; j < i + GramSize; j++) hit[j] = true;
            hitWhere[i] = where[0];
        }
    }

    // 最长连续命中串
    int covered = 0, run = 0, bestRun = 0, bestRunStart = -1;
    for (int i = 0; i < toks.Length; i++)
    {
        if (hit[i])
        {
            covered++;
            if (++run > bestRun) { bestRun = run; bestRunStart = i - run + 1; }
        }
        else { run = 0; }
    }
    ourTokensCovered += covered;

    if (bestRun > maxRun)
    {
        int anchor = hitWhere.Keys.Where(k => k >= bestRunStart).DefaultIfEmpty(-1).Min();
        (string File, int Pos) src = anchor >= 0 ? hitWhere[anchor] : ("?", -1);
        findings.Add(new Finding(rel, bestRun, toks.Length, covered,
                                 src.File, src.Pos,
                                 string.Join(' ', toks.Skip(bestRunStart).Take(Math.Min(bestRun, 60)))));
    }
}

double ratio = ourTokensTotal == 0 ? 0 : 100.0 * ourTokensCovered / ourTokensTotal;
Console.WriteLine($"  {ourFiles} 个文件, {ourTokensTotal:N0} 个 token");
Console.WriteLine($"\n覆盖率: {ratio:F2}%  (阈值 {maxRatio:F2}%)");
Console.WriteLine($"超限文件: {findings.Count}  (单文件最长公共串阈值 {maxRun} token)");

// ---------------------------------------------------------------- 报告

Directory.CreateDirectory(reportDir);
string reportPath = Path.Combine(reportDir, "similarity.json");
File.WriteAllText(reportPath, JsonSerializer.Serialize(new
{
    generatedAt = DateTimeOffset.UtcNow,
    gramSize    = GramSize,
    maxRun,
    maxRatio,
    ourFiles,
    ourTokensTotal,
    ourTokensCovered,
    ratio,
    corpusFiles,
    corpusTokens,
    findings,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"报告: {reportPath}");

if (findings.Count > 0)
{
    Console.Error.WriteLine("\n===== 超阈值的文件 =====");
    foreach (Finding x in findings.OrderByDescending(x => x.LongestRun))
    {
        Console.Error.WriteLine($"""

            {x.OurFile}
              最长公共 token 串: {x.LongestRun}  (阈值 {maxRun})
              命中来源: {x.CorpusFile} @token {x.CorpusPos}
              片段: {Truncate(x.Excerpt, 300)}
            """);
    }
}

bool failed = findings.Count > 0 || ratio > maxRatio;
Console.WriteLine(failed
    ? "\n❌ 门禁未通过。这不一定意味着抄袭 —— 也可能是两边都在直译同一段 RFC。\n" +
      "   逐条看上面的片段：确实是协议规定的唯一表达，就加进 allowlist.txt 并写明理由；\n" +
      "   否则请重写那一段。"
    : "\n✅ 门禁通过。");
return failed ? 1 : 0;

// ============================================================================
// 工具
// ============================================================================

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + " …";

static IEnumerable<string> EnumerateCs(string root) =>
    Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
             .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                      && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                      && !p.EndsWith(".g.cs", StringComparison.Ordinal)
                      && !p.EndsWith(".Designer.cs", StringComparison.Ordinal));

/// <summary>
/// 极简 C# 词法切分。目标不是正确编译，是得到一个**稳定、可比对**的 token 序列。
/// 刻意的取舍：
///   · 丢掉注释与空白 —— 它们是最容易被顺手改掉的东西，留着会让检测失灵。
///   · **丢掉 using 指令** —— 见 StripUsingDirectives。
///   · 字符串/字符字面量折叠成占位符 —— 协议里的算法名字符串必然相同（NOTICE.md 说明），
///     留着会制造大量无意义的命中。
///   · **保留标识符原文** —— 标识符的组合正是纪律 3 要防的东西。
/// </summary>
static string[] Tokenize(string src)
{
    src = StripUsingDirectives(src);
    var outp = new List<string>(src.Length / 4);
    int i = 0, n = src.Length;

    while (i < n)
    {
        char c = src[i];

        if (char.IsWhiteSpace(c)) { i++; continue; }

        // 注释
        if (c == '/' && i + 1 < n)
        {
            if (src[i + 1] == '/') { while (i < n && src[i] != '\n') i++; continue; }
            if (src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i = Math.Min(i + 2, n);
                continue;
            }
        }

        // 字符串 / 字符 / 逐字字符串 / 原始字符串
        if (c is '"' or '\'' || (c == '@' && i + 1 < n && src[i + 1] == '"')
                             || (c == '$' && i + 1 < n && (src[i + 1] == '"' || src[i + 1] == '@')))
        {
            i = SkipLiteral(src, i);
            outp.Add("\"L\"");
            continue;
        }

        // 标识符 / 关键字
        if (char.IsLetter(c) || c == '_')
        {
            int s = i;
            while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
            outp.Add(src[s..i]);
            continue;
        }

        // 数字（折叠：字面量的具体取值不构成表达）
        if (char.IsDigit(c))
        {
            while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '.' || src[i] == 'x' || src[i] == 'X')) i++;
            outp.Add("#N");
            continue;
        }

        // 其余符号
        outp.Add(c.ToString());
        i++;
    }

    return [.. outp];
}

/// <summary>
/// 去掉 <c>using</c> 指令行（含 <c>global using</c> 与别名）。
/// </summary>
/// <remarks>
/// <para>
/// import 块**不携带任何表达**：两个都用同一个库的文件，它们的 using 块必然逐字相同。
/// 留着它们只会制造误报 —— 而误报会让人开始习惯性忽略门禁，那比漏报更危险。
/// </para>
/// <para>
/// 只匹配**整行**的 <c>using X.Y.Z;</c> 与 <c>using Alias = X.Y.Z;</c>，
/// 因此 <c>using var x = ...</c>（using 语句）与 <c>using (...)</c> 不受影响 ——
/// 那些是真正的代码。
/// </para>
/// </remarks>
static string StripUsingDirectives(string src) =>
    System.Text.RegularExpressions.Regex.Replace(
        src,
        @"(?m)^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?[A-Za-z_][\w.]*(?:[ \t]*=[ \t]*[A-Za-z_][\w.<>,\[\] ]*)?[ \t]*;[ \t]*\r?$",
        string.Empty);

static int SkipLiteral(string s, int i)
{
    int n = s.Length;
    bool verbatim = false;

    if (s[i] == '$') { i++; if (i < n && s[i] == '@') { verbatim = true; i++; } }
    else if (s[i] == '@') { verbatim = true; i++; }

    if (i >= n) return i;

    // 原始字符串 """..."""
    if (s[i] == '"' && i + 2 < n && s[i + 1] == '"' && s[i + 2] == '"')
    {
        int q = 0;
        while (i < n && s[i] == '"') { q++; i++; }
        int run = 0;
        while (i < n)
        {
            if (s[i] == '"') { if (++run == q) { i++; return i; } }
            else run = 0;
            i++;
        }
        return i;
    }

    char quote = s[i];
    i++;
    while (i < n)
    {
        if (verbatim)
        {
            if (s[i] == quote)
            {
                if (i + 1 < n && s[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
        }
        else
        {
            if (s[i] == '\\') { i += 2; continue; }
            if (s[i] == quote) return i + 1;
            if (s[i] == '\n') return i;   // 未闭合，止损
        }
        i++;
    }
    return i;
}

/// <summary>FNV-1a 64 位。够快、碰撞率对本用途足够低。</summary>
static ulong HashGram(string[] toks, int start, int len)
{
    ulong h = 14695981039346656037UL;
    for (int i = start; i < start + len; i++)
    {
        foreach (char ch in toks[i]) { h ^= ch; h *= 1099511628211UL; }
        h ^= (byte)'|'; h *= 1099511628211UL;
    }
    return h;
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "VelaShell.slnx"))) d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}

static async Task<bool> FetchCorpusAsync(string sourcesPath, string corpusDir)
{
    if (!File.Exists(sourcesPath))
    {
        Console.Error.WriteLine($"缺少 {sourcesPath}");
        return false;
    }

    Directory.CreateDirectory(corpusDir);
    using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(sourcesPath));

    foreach (JsonElement src in doc.RootElement.GetProperty("sources").EnumerateArray())
    {
        string name = src.GetProperty("name").GetString()!;
        string url  = src.GetProperty("repo").GetString() ?? "";
        string? sub = src.TryGetProperty("subdir", out JsonElement sd) ? sd.GetString() : null;
        string dest = Path.Combine(corpusDir, name);

        if (url.Length == 0) { Console.WriteLine($"跳过(占位条目): {name}"); continue; }

        if (Directory.Exists(dest)) { Console.WriteLine($"已存在, 跳过: {name}"); continue; }

        Console.WriteLine($"拉取 {name} ← {url}");
        string tmp = Path.Combine(Path.GetTempPath(), "vsg-" + Guid.NewGuid().ToString("N")[..8]);
        if (!Run("git", $"clone --depth 1 --quiet {url} \"{tmp}\"")) return false;

        string from = sub is null ? tmp : Path.Combine(tmp, sub.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dest);
        foreach (string f in Directory.EnumerateFiles(from, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, f);
            string to  = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, overwrite: true);
        }
        try { Directory.Delete(tmp, recursive: true); } catch { /* 临时目录清不掉不影响结果 */ }
        Console.WriteLine($"  → {Directory.EnumerateFiles(dest, "*.cs", SearchOption.AllDirectories).Count()} 个 .cs");
    }
    return true;
}

static bool Run(string exe, string args)
{
    using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })!;
    p.WaitForExit();
    if (p.ExitCode != 0) Console.Error.WriteLine($"命令失败({p.ExitCode}): {exe} {args}");
    return p.ExitCode == 0;
}

record Finding(
    string OurFile,
    int    LongestRun,
    int    OurTokens,
    int    CoveredTokens,
    string CorpusFile,
    int    CorpusPos,
    string Excerpt);
