using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PulseTerm.Terminal.Semantics;

/// <summary>The kind of thing a <see cref="SemanticSpan"/> marks in a line of terminal output.</summary>
public enum SemanticKind
{
    Url,
    Error,
    Warning,
    IpAddress,
}

/// <summary>A matched region within a single line, in character offsets.</summary>
public readonly record struct SemanticSpan(int Start, int Length, SemanticKind Kind)
{
    public int End => Start + Length;
}

/// <summary>
/// Finds semantically interesting regions (URLs, error/warning words, IP addresses) in committed
/// line text so the renderer can color them and links can be made clickable (#9). Operates on
/// finished text, not the VT byte stream, so it never interferes with emulation. Rules are
/// intentionally simple and overridable.
/// </summary>
public sealed partial class SemanticMatcher
{
    [GeneratedRegex(@"\bhttps?://[^\s""'<>()\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IpRegex();

    [GeneratedRegex(@"\b(?:error|errors|failed|failure|fatal|panic|exception|denied)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex(@"\b(?:warn|warning|deprecated)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningRegex();

    /// <summary>
    /// Returns non-overlapping spans for the line, ordered by start offset. When regions overlap
    /// (e.g. an IP inside a URL), the higher-priority kind wins: Url &gt; Error &gt; Warning &gt; IpAddress.
    /// </summary>
    public IReadOnlyList<SemanticSpan> Match(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return System.Array.Empty<SemanticSpan>();

        var raw = new List<SemanticSpan>();
        Collect(raw, UrlRegex(), line, SemanticKind.Url);
        Collect(raw, ErrorRegex(), line, SemanticKind.Error);
        Collect(raw, WarningRegex(), line, SemanticKind.Warning);
        Collect(raw, IpRegex(), line, SemanticKind.IpAddress);

        // Higher priority first, then earliest, so the greedy pass below keeps the best match.
        raw.Sort((a, b) =>
        {
            int byKind = Priority(a.Kind).CompareTo(Priority(b.Kind));
            return byKind != 0 ? byKind : a.Start.CompareTo(b.Start);
        });

        var chosen = new List<SemanticSpan>();
        foreach (var span in raw)
        {
            bool overlaps = false;
            foreach (var kept in chosen)
            {
                if (span.Start < kept.End && kept.Start < span.End)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps)
                chosen.Add(span);
        }

        chosen.Sort((a, b) => a.Start.CompareTo(b.Start));
        return chosen;
    }

    /// <summary>Returns the URL at the given character offset, or null if none.</summary>
    public string? UrlAt(string? line, int offset)
    {
        if (string.IsNullOrEmpty(line) || offset < 0 || offset >= line.Length)
            return null;

        foreach (Match m in UrlRegex().Matches(line))
        {
            if (offset >= m.Index && offset < m.Index + m.Length)
                return m.Value;
        }
        return null;
    }

    private static void Collect(List<SemanticSpan> into, Regex regex, string line, SemanticKind kind)
    {
        foreach (Match m in regex.Matches(line))
        {
            if (m.Length > 0)
                into.Add(new SemanticSpan(m.Index, m.Length, kind));
        }
    }

    private static int Priority(SemanticKind kind) => kind switch
    {
        SemanticKind.Url => 0,
        SemanticKind.Error => 1,
        SemanticKind.Warning => 2,
        _ => 3,
    };
}
