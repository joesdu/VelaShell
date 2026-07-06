using System.Linq;
using PulseTerm.Terminal.Semantics;

namespace PulseTerm.Terminal.Tests;

[TestClass]
[TestCategory("Semantics")]
public class SemanticMatcherTests
{
    private readonly SemanticMatcher _matcher = new();

    [TestMethod]
    public void Match_FindsUrl_WithCorrectOffsets()
    {
        const string line = "see https://example.com/path for details";
        var spans = _matcher.Match(line);

        var url = spans.Single(s => s.Kind == SemanticKind.Url);
        Assert.AreEqual("https://example.com/path", line.Substring(url.Start, url.Length));
    }

    [TestMethod]
    public void Match_FindsErrorKeyword()
    {
        var spans = _matcher.Match("fatal: connection failed");
        Assert.IsTrue(spans.Any(s => s.Kind == SemanticKind.Error));
    }

    [TestMethod]
    public void Match_FindsWarning()
    {
        var spans = _matcher.Match("WARNING: disk almost full");
        Assert.IsTrue(spans.Any(s => s.Kind == SemanticKind.Warning));
    }

    [TestMethod]
    public void Match_FindsIpAddress()
    {
        const string line = "listening on 192.168.1.10:22";
        var spans = _matcher.Match(line);
        var ip = spans.Single(s => s.Kind == SemanticKind.IpAddress);
        Assert.AreEqual("192.168.1.10", line.Substring(ip.Start, ip.Length));
    }

    [TestMethod]
    public void Match_UrlContainingIp_KeepsOnlyUrl()
    {
        // The IP inside the URL must not produce a separate overlapping span.
        var spans = _matcher.Match("open http://192.168.0.1/admin now");
        Assert.AreEqual(1, spans.Count);
        Assert.AreEqual(SemanticKind.Url, spans[0].Kind);
    }

    [TestMethod]
    public void Match_ReturnsSpansOrderedByStart()
    {
        var spans = _matcher.Match("error at https://a.co after warning");
        var starts = spans.Select(s => s.Start).ToList();
        var sorted = starts.OrderBy(x => x).ToList();
        CollectionAssert.AreEqual(sorted, starts);
    }

    [TestMethod]
    public void Match_EmptyLine_ReturnsNothing()
    {
        Assert.AreEqual(0, _matcher.Match("").Count);
        Assert.AreEqual(0, _matcher.Match(null).Count);
    }

    [TestMethod]
    public void UrlAt_ReturnsUrlWhenOffsetInside_ElseNull()
    {
        const string line = "go to https://example.com ok";
        int inside = line.IndexOf("example", System.StringComparison.Ordinal);

        Assert.AreEqual("https://example.com", _matcher.UrlAt(line, inside));
        Assert.IsNull(_matcher.UrlAt(line, 0)); // "go" is not a URL
    }
}
