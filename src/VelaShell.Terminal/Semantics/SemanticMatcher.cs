using System.Text.RegularExpressions;

namespace VelaShell.Terminal.Semantics;

/// <summary>The kind of thing a <see cref="SemanticSpan" /> marks in a line of terminal output.</summary>
public enum SemanticKind
{
    Url,
    Error,
    Warning,
    Success,
    IpAddress,
    Option,
    Number
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

    [GeneratedRegex(@"\b(?:warn|warning|deprecated|caution|notice)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningRegex();

    [GeneratedRegex(@"\b(?:success|successful|successfully|succeeded|ok|done|enabled|active|running|started|listening|pass|passed|ready|healthy|online|connected)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SuccessRegex();

    // Command-line option flags such as -x, --now, --color=auto. Anchored so it never fires
    // inside a word or a hyphenated term (well-known, re-run).
    [GeneratedRegex(@"(?<![\w-])--?[A-Za-z][\w-]*")]
    private static partial Regex OptionRegex();

    // Standalone numbers, including dotted/colon groups (ports, counts, timestamps like 22:37:41).
    // IP addresses win via priority, so they are not fragmented into numbers.
    [GeneratedRegex(@"\b\d+(?:[.:]\d+)*\b")]
    private static partial Regex NumberRegex();

    /// <summary>
    /// Returns non-overlapping spans for the line, ordered by start offset. When regions overlap
    /// (e.g. digits inside an IP, or an IP inside a URL) the higher-priority kind wins:
    /// Url &gt; IpAddress &gt; Error &gt; Warning &gt; Success &gt; Option &gt; Number.
    /// </summary>
    public static IReadOnlyList<SemanticSpan> Match(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return [];
        }
        var raw = new List<SemanticSpan>();
        Collect(raw, UrlRegex(), line, SemanticKind.Url);
        Collect(raw, IpRegex(), line, SemanticKind.IpAddress);
        Collect(raw, ErrorRegex(), line, SemanticKind.Error);
        Collect(raw, WarningRegex(), line, SemanticKind.Warning);
        Collect(raw, SuccessRegex(), line, SemanticKind.Success);
        Collect(raw, OptionRegex(), line, SemanticKind.Option);
        Collect(raw, NumberRegex(), line, SemanticKind.Number);

        // Higher priority first, then earliest, so the greedy pass below keeps the best match.
        raw.Sort((a, b) =>
        {
            int byKind = Priority(a.Kind).CompareTo(Priority(b.Kind));
            return byKind != 0 ? byKind : a.Start.CompareTo(b.Start);
        });
        var chosen = new List<SemanticSpan>();
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (SemanticSpan span in raw)
        {
            bool overlaps = chosen.Any(kept => span.Start < kept.End && kept.Start < span.End);
            if (!overlaps)
            {
                chosen.Add(span);
            }
        }
        chosen.Sort((a, b) => a.Start.CompareTo(b.Start));
        return chosen;
    }

    /// <summary>Returns the URL at the given character offset, or null if none.</summary>
    public static string? UrlAt(string? line, int offset)
    {
        if (string.IsNullOrEmpty(line) || offset < 0 || offset >= line.Length)
        {
            return null;
        }
        foreach (Match m in UrlRegex().Matches(line))
        {
            if (offset >= m.Index && offset < m.Index + m.Length)
            {
                return m.Value;
            }
        }
        return null;
    }

    private static void Collect(List<SemanticSpan> into, Regex regex, string line, SemanticKind kind)
    {
        foreach (Match m in regex.Matches(line))
        {
            if (m.Length > 0)
            {
                into.Add(new(m.Index, m.Length, kind));
            }
        }
    }

    private static int Priority(SemanticKind kind) =>
        kind switch
        {
            SemanticKind.Url => 0,
            SemanticKind.IpAddress => 1,
            SemanticKind.Error => 2,
            SemanticKind.Warning => 3,
            SemanticKind.Success => 4,
            SemanticKind.Option => 5,
            _ => 6 // Number
        };
}
