using VelaShell.Terminal.Semantics;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Semantics")]
public class SemanticMatcherTests
{
    private readonly SemanticMatcher _matcher = new();

    [TestMethod]
    public void Match_FindsUrl_WithCorrectOffsets()
    {
        const string line = "see https://example.com/path for details";
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(line);
        SemanticSpan url = spans.Single(s => s.Kind == SemanticKind.Url);
        Assert.AreEqual("https://example.com/path", line.Substring(url.Start, url.Length));
    }

    [TestMethod]
    public void Match_FindsErrorKeyword()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("fatal: connection failed");
        Assert.IsTrue(spans.Any(s => s.Kind == SemanticKind.Error));
    }

    [TestMethod]
    public void Match_FindsWarning()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("WARNING: disk almost full");
        Assert.IsTrue(spans.Any(s => s.Kind == SemanticKind.Warning));
    }

    [TestMethod]
    public void Match_FindsIpAddress()
    {
        const string line = "listening on 192.168.1.10:22";
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(line);
        SemanticSpan ip = spans.Single(s => s.Kind == SemanticKind.IpAddress);
        Assert.AreEqual("192.168.1.10", line.Substring(ip.Start, ip.Length));
    }

    [TestMethod]
    public void Match_UrlContainingIp_KeepsOnlyUrl()
    {
        // The IP inside the URL must not produce a separate overlapping span.
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("open http://192.168.0.1/admin now");
        Assert.AreEqual(1, spans.Count);
        Assert.AreEqual(SemanticKind.Url, spans[0].Kind);
    }

    [TestMethod]
    public void Match_ReturnsSpansOrderedByStart()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("error at https://a.co after warning");
        var starts = spans.Select(s => s.Start).ToList();
        var sorted = starts.OrderBy(x => x).ToList();
        CollectionAssert.AreEqual(sorted, starts);
    }

    [TestMethod]
    public void Match_FindsSuccessKeyword()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("cockpit.service is now active and running");
        Assert.IsTrue(spans.Any(s => s.Kind == SemanticKind.Success));
    }

    [TestMethod]
    public void Match_FindsOptionFlag()
    {
        const string line = "systemctl enable --now cockpit.socket";
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(line);
        SemanticSpan option = spans.Single(s => s.Kind == SemanticKind.Option);
        Assert.AreEqual("--now", line.Substring(option.Start, option.Length));
    }

    [TestMethod]
    public void Match_OptionDoesNotFireInsideHyphenatedWord()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("a well-known re-run host");
        Assert.IsFalse(spans.Any(s => s.Kind == SemanticKind.Option));
    }

    [TestMethod]
    public void Match_FindsStandaloneNumber()
    {
        const string line = "There were 38 failed login attempts";
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(line);
        SemanticSpan number = spans.Single(s => s.Kind == SemanticKind.Number);
        Assert.AreEqual("38", line.Substring(number.Start, number.Length));
    }

    [TestMethod]
    public void Match_IpAddress_IsNotFragmentedIntoNumbers()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("login from 10.10.10.1 on ssh");
        Assert.IsTrue(spans.Any(s => s.Kind == SemanticKind.IpAddress));
        // The dotted quad must stay a single IP span, not split into number spans.
        Assert.IsFalse(spans.Any(s => s.Kind == SemanticKind.Number));
    }

    [TestMethod]
    public void Match_EmptyLine_ReturnsNothing()
    {
        Assert.AreEqual(0, SemanticMatcher.Match("").Count);
        Assert.AreEqual(0, SemanticMatcher.Match(null).Count);
    }

    [TestMethod]
    public void UrlAt_ReturnsUrlWhenOffsetInside_ElseNull()
    {
        const string line = "go to https://example.com ok";
        int inside = line.IndexOf("example", StringComparison.Ordinal);
        Assert.AreEqual("https://example.com", SemanticMatcher.UrlAt(line, inside));
        Assert.IsNull(SemanticMatcher.UrlAt(line, 0)); // "go" is not a URL
    }
}
