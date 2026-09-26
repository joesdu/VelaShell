// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5) 的 PATTERNS 章节（* / ? 通配、! 取反）
//   OpenSSH sshd(8) 的 SSH_KNOWN_HOSTS FILE FORMAT 章节（主机名模式同一套规则）

namespace VelaShell.Ssh.Protocol;

/// <summary><c>ssh_config</c> 的 <c>Host</c> 模式与 <c>known_hosts</c> 的主机模式共用的通配规则。</summary>
internal static class HostPatterns
{
    /// <summary>一个模式对不对得上：<c>*</c> 匹配任意多个字符、<c>?</c> 匹配一个，不分大小写。</summary>
    public static bool Matches(string pattern, string text)
    {
        // 逐字符的双指针回溯：模式与主机名都很短，不值得为它编译正则。
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

    /// <summary>
    /// 一组模式（可含 <c>!</c> 取反）：至少对上一个正向模式，且没有对上任何取反模式。
    /// </summary>
    /// <remarks>取反模式对上了就一票否决 —— 哪怕别的正向模式也对上了。</remarks>
    public static bool MatchesList(IReadOnlyList<string> patterns, string text)
    {
        bool matched = false;

        foreach (string pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (Matches(pattern[1..], text))
                {
                    return false;
                }
                continue;
            }

            if (Matches(pattern, text))
            {
                matched = true;
            }
        }

        return matched;
    }
}
