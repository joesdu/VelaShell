// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  双引号与 # 注释的说明(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7.4
//                          (分词规则来自对 Win32-OpenSSH 9.5p2 `ssh -G` 的黑盒观察,见那一节)

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace VelaShell.Ssh.Config;

/// <summary><c>ssh_config</c> 的分词：一行拆成关键字与值、值切成词（去掉引号与转义）、一个参数写成读得回原值的样子。</summary>
/// <remarks>
/// <para>
/// 〔velashell-docs/zh/ssh/spec/09 §7.4〕照 OpenSSH 的读法（维护者 2026-10-31 定）：空格与制表符分词；<c>"…"</c> 与 <c>'…'</c> 两种引号
/// 规则相同，可以出现在词中间；转义只有 <c>\\</c>、<c>\"</c>、<c>\'</c> 三种（引号外再加 <c>\</c> + 空格），别的反斜杠原样留着 ——
/// 普通的 Windows 路径 <c>C:\Users\me\.ssh\id</c> 照原样读，而 <c>\\server\share</c> 读成 <c>\server\share</c>，与 <c>ssh</c> 一样；
/// 不在引号里、落在词开头的 <c>#</c> 开始注释。
/// </para>
/// <para>
/// 〔历史〕曾经只认双引号、只在整个值被引号括住时去掉引号，不认单引号与反斜杠：<c>IdentityAgent \\.\pipe\x</c> 在本库里读成
/// <c>\\.\pipe\x</c>，<c>ssh</c> 却读成 <c>\.\pipe\x</c>，宿主原样导出的命名管道 <c>ssh</c> 用不了，本库读回来又看不出错。
/// </para>
/// </remarks>
internal static class SshConfigSyntax
{
    /// <summary>值是关键字之后、到行尾的原文的那几个键：不分词，引号、反斜杠、<c>#</c> 都原样留着（交给 shell 去解释）。</summary>
    /// <remarks>ssh_config(5) 对前三个写着「The command string extends to the end of the line」；<c>KnownHostsCommand</c> 是观察到的。</remarks>
    internal static bool TakesRestOfLine(string key) =>
        key.Equals("ProxyCommand", StringComparison.OrdinalIgnoreCase)
        || key.Equals("RemoteCommand", StringComparison.OrdinalIgnoreCase)
        || key.Equals("LocalCommand", StringComparison.OrdinalIgnoreCase)
        || key.Equals("KnownHostsCommand", StringComparison.OrdinalIgnoreCase);

    /// <summary>一行拆成关键字与值。</summary>
    /// <param name="line">一行原文（行尾的 <c>\r</c> 可以还在）。</param>
    /// <remarks>
    /// 行尾空白先去掉（CRLF 与 LF 读法相同；行尾的 <c>\</c> 不续行）。关键字到第一个空白或 <c>=</c> 为止，之后跳过空白、
    /// <b>至多一个</b> <c>=</c>、再跳过空白，剩下的是值：<c>Key==a</c> 的值是 <c>=a</c>。
    /// </remarks>
    internal static SshConfigLine ReadLine(string line)
    {
        string text = line.TrimEnd(' ', '\t', '\r', '\n');
        int i = 0;
        SkipBlanks(text, ref i);
        if (i == text.Length || text[i] == '#')
        {
            return new SshConfigLine("", "", QuotesBalanced: true);
        }

        // 关键字本身也可以带引号（"HostKeyAlias" a）——关键字里没有反斜杠这回事，只去掉引号。
        StringBuilder key = new();
        char quote = '\0';
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    key.Append(c);
                }
                continue;
            }
            if (c is ' ' or '\t' or '=')
            {
                break;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }
            key.Append(c);
        }

        if (quote != '\0')
        {
            return new SshConfigLine(key.ToString(), "", QuotesBalanced: false);
        }

        SkipBlanks(text, ref i);
        if (i < text.Length && text[i] == '=')
        {
            i++;
            SkipBlanks(text, ref i);
        }

        string name = key.ToString();
        if (TakesRestOfLine(name))
        {
            return new SshConfigLine(name, text[i..], QuotesBalanced: true);
        }

        bool balanced = Scan(text, i, words: null, out int end);
        return new SshConfigLine(name, balanced ? text[i..end] : text[i..], balanced);
    }

    /// <summary>把一个值切成词：去掉引号与转义（spec/09 §7.4）。</summary>
    /// <param name="value">值的原文（<see cref="SshConfigLine.Value"/>，或者写法相同的一段文本）；碰到注释就停。</param>
    /// <param name="words">切出来的词；空的引号（<c>""</c>）是一个空词。</param>
    /// <returns>引号配上了为 <see langword="true"/>；没配上时 <paramref name="words"/> 为 <see langword="null"/>。</returns>
    internal static bool TrySplit(string value, [NotNullWhen(true)] out List<string>? words)
    {
        List<string> result = [];
        if (!Scan(value, 0, result, out _))
        {
            words = null;
            return false;
        }
        words = result;
        return true;
    }

    /// <summary>
    /// 一个参数写成本库与 <c>ssh</c> 读回都是原值的样子；值里有换行或 NUL 时写不出来。
    /// </summary>
    /// <param name="value">参数的值。</param>
    /// <param name="text">写进配置的样子。</param>
    /// <returns>写得出来为 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/09 §7.4〕能不改的地方不改：两个及以上连着的反斜杠每个写成两个（<c>\\.\pipe\x</c> → <c>\\\\.\pipe\x</c>）；
    /// 单个反斜杠后面紧跟引号、或者落在加了引号的值的末尾时同样写成两个；别的单个反斜杠原样 —— <c>C:\Users\me\.ssh\id</c> 写出来不变，
    /// 读法不同的老版本也读得对。<c>"</c> 与 <c>'</c> 前加 <c>\</c>。有空格、制表符或 <c>#</c>、以 <c>=</c> 开头、或者是空串时整个加双引号。
    /// </para>
    /// <para>
    /// ⚠️ 换行（<c>\r</c> / <c>\n</c>）与 NUL 写不进去：ssh_config 一行装不下，硬写的话下一行就成了另一条设置（一个带换行的用户名能写出一行 <c>ProxyCommand</c>）。
    /// </para>
    /// </remarks>
    internal static bool TryFormat(string value, [NotNullWhen(true)] out string? text)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            text = null;
            return false;
        }

        bool quoted = value.Length == 0 || value[0] == '=' || value.AsSpan().IndexOfAny(' ', '\t', '#') >= 0;
        StringBuilder result = new(value.Length + 8);
        if (quoted)
        {
            result.Append('"');
        }

        for (int i = 0; i < value.Length;)
        {
            char c = value[i];
            if (c == '\\')
            {
                int run = 1;
                while (i + run < value.Length && value[i + run] == '\\')
                {
                    run++;
                }

                // 一串反斜杠从左往右两两成对地读：两个以上的每个都写成两个，读回来正好是原来那么多；
                // 只有一个时，后面紧跟引号（否则引号被它转义掉）、或者是加了引号的值的末尾（否则把收尾的引号转义掉）才要写成两个。
                bool atEnd = i + run == value.Length;
                bool escape = run >= 2 || (atEnd ? quoted : value[i + run] is '"' or '\'');
                result.Append('\\', escape ? run * 2 : run);
                i += run;
                continue;
            }

            if (c is '"' or '\'')
            {
                result.Append('\\');
            }
            result.Append(c);
            i++;
        }

        if (quoted)
        {
            result.Append('"');
        }
        text = result.ToString();
        return true;
    }

    private static void SkipBlanks(string text, ref int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }
    }

    /// <summary>从 <paramref name="start"/> 起按 spec/09 §7.4 扫一段值，止于注释或末尾。</summary>
    /// <param name="text">原文。</param>
    /// <param name="start">值从哪里开始。</param>
    /// <param name="words">切出来的词往这里加；为 <see langword="null"/> 时只找边界。</param>
    /// <param name="end">最后一个词的末尾（其后只剩空白与注释）。</param>
    /// <returns>引号配上了为 <see langword="true"/>。</returns>
    private static bool Scan(string text, int start, List<string>? words, out int end)
    {
        StringBuilder current = new();
        bool inWord = false;
        char quote = '\0';
        end = start;

        int i = start;
        while (i < text.Length)
        {
            char c = text[i];

            if (quote != '\0')
            {
                // 引号里只有 \\、\"、\' 是转义（两种引号一样）；\ 后跟空格、别的字符都原样。
                if (c == '\\' && i + 1 < text.Length && text[i + 1] is '\\' or '"' or '\'')
                {
                    current.Append(text[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == quote)
                {
                    quote = '\0';
                    i++;
                    end = i;
                    continue;
                }
                current.Append(c);
                i++;
                continue;
            }

            if (c is ' ' or '\t')
            {
                if (inWord)
                {
                    words?.Add(current.ToString());
                    current.Clear();
                    inWord = false;
                }
                i++;
                continue;
            }

            // 不在引号里、落在词开头的 # 开始注释；词中间的、跟在 \ 或转义空格后面的都是值的一部分。
            if (!inWord && c == '#')
            {
                break;
            }

            inWord = true;
            if (c is '"' or '\'')
            {
                quote = c;
                i++;
                continue;
            }

            // 引号外多一种转义：\ + 空格是一个不分词的空格。\ + 制表符不算 —— 反斜杠留着，制表符照样分词。
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '\\' or '"' or '\'' or ' ')
            {
                current.Append(text[i + 1]);
                i += 2;
                end = i;
                continue;
            }

            current.Append(c);
            i++;
            end = i;
        }

        if (quote != '\0')
        {
            end = text.Length;
            return false;
        }
        if (inWord)
        {
            words?.Add(current.ToString());
        }
        return true;
    }
}
