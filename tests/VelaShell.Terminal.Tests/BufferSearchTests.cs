using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Search")]
public class BufferSearchTests
{
    private static TerminalEmulator Feed(params string[] lines)
    {
        var e = new TerminalEmulator(80, 24);
        e.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));
        return e;
    }

    [TestMethod]
    public void FindAll_FindsHits_CaseInsensitive_WithPositions()
    {
        var e = Feed("hello world", "no match here", "WORLD of Hello");

        var hits = BufferSearch.FindAll(e.Screen, "world");

        Assert.AreEqual(2, hits.Count);
        Assert.AreEqual(0, hits[0].Row);
        Assert.AreEqual(6, hits[0].StartCol);
        Assert.AreEqual(2, hits[1].Row);
        Assert.AreEqual(0, hits[1].StartCol);
        Assert.AreEqual(5, hits[1].Length);
    }

    [TestMethod]
    public void FindAll_MultipleHitsPerLine()
    {
        var e = Feed("ab ab ab");

        var hits = BufferSearch.FindAll(e.Screen, "ab");

        Assert.AreEqual(3, hits.Count);
        Assert.AreEqual(0, hits[0].StartCol);
        Assert.AreEqual(3, hits[1].StartCol);
        Assert.AreEqual(6, hits[2].StartCol);
    }

    [TestMethod]
    public void FindAll_SearchesScrollback()
    {
        var e = new TerminalEmulator(80, 4); // tiny screen so early lines scroll out
        var sb = new StringBuilder();
        sb.Append("needle-in-history\r\n");
        for (int i = 0; i < 10; i++)
            sb.Append($"filler {i}\r\n");
        e.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        Assert.IsTrue(e.Screen.ScrollbackCount > 0, "line must have scrolled out");
        var hits = BufferSearch.FindAll(e.Screen, "needle-in-history");
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(0, hits[0].Row);
    }

    [TestMethod]
    public void FindAll_EmptyQuery_ReturnsNothing()
    {
        var e = Feed("anything");
        Assert.AreEqual(0, BufferSearch.FindAll(e.Screen, "").Count);
    }
}
