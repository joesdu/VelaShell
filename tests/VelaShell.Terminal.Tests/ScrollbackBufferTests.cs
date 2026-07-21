namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Scrollback")]
public class ScrollbackBufferTests
{
    [TestMethod]
    public void AddLine_StoresLineCorrectly()
    {
        var buffer = new ScrollbackBuffer(100);
        var line = new TerminalLine { Content = "hello world" };
        buffer.AddLine(line);
        Assert.AreEqual(1, buffer.ScrollbackLineCount);
        Assert.AreEqual("hello world", buffer.GetLine(0).Content);
    }

    [TestMethod]
    public void AddLine_CircularWrap_OverwritesOldest()
    {
        var buffer = new ScrollbackBuffer(3);
        buffer.AddLine(new() { Content = "line0" });
        buffer.AddLine(new() { Content = "line1" });
        buffer.AddLine(new() { Content = "line2" });
        buffer.AddLine(new() { Content = "line3" });
        Assert.AreEqual(3, buffer.ScrollbackLineCount);
        Assert.AreEqual("line1", buffer.GetLine(0).Content);
        Assert.AreEqual("line2", buffer.GetLine(1).Content);
        Assert.AreEqual("line3", buffer.GetLine(2).Content);
    }

    [TestMethod]
    public void GetLine_ReturnsCorrectContent()
    {
        var buffer = new ScrollbackBuffer(100);
        for (int i = 0; i < 10; i++)
        {
            buffer.AddLine(new() { Content = $"line {i}" });
        }
        Assert.AreEqual("line 0", buffer.GetLine(0).Content);
        Assert.AreEqual("line 5", buffer.GetLine(5).Content);
        Assert.AreEqual("line 9", buffer.GetLine(9).Content);
    }

    [TestMethod]
    public void ScrollTo_MovesViewport()
    {
        var buffer = new ScrollbackBuffer(100)
        {
            VisibleRows = 24
        };
        for (int i = 0; i < 50; i++)
        {
            buffer.AddLine(new() { Content = $"line {i}" });
        }
        buffer.ScrollTo(10);
        Assert.AreEqual(10, buffer.ViewportRow);
    }

    [TestMethod]
    public void ScrollUp_MovesViewportUp()
    {
        var buffer = new ScrollbackBuffer(100)
        {
            VisibleRows = 24
        };
        for (int i = 0; i < 50; i++)
        {
            buffer.AddLine(new() { Content = $"line {i}" });
        }
        buffer.ScrollTo(30);
        buffer.ScrollUp(5);
        Assert.AreEqual(25, buffer.ViewportRow);
    }

    [TestMethod]
    public void ScrollDown_MovesViewportDown()
    {
        var buffer = new ScrollbackBuffer(100)
        {
            VisibleRows = 24
        };
        for (int i = 0; i < 50; i++)
        {
            buffer.AddLine(new() { Content = $"line {i}" });
        }
        buffer.ScrollTo(10);
        buffer.ScrollDown(5);
        Assert.AreEqual(15, buffer.ViewportRow);
    }

    [TestMethod]
    public void Search_FindsTextAcrossScrollback()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.AddLine(new() { Content = "foo bar baz" });
        buffer.AddLine(new() { Content = "hello world" });
        buffer.AddLine(new() { Content = "foo again" });
        List<SearchMatch> matches = buffer.Search("foo");
        Assert.HasCount(2, matches);
        Assert.AreEqual(0, matches[0].Row);
        Assert.AreEqual(0, matches[0].Column);
        Assert.AreEqual(3, matches[0].Length);
        Assert.AreEqual(2, matches[1].Row);
        Assert.AreEqual(0, matches[1].Column);
        Assert.AreEqual(3, matches[1].Length);
    }

    [TestMethod]
    public void TotalLines_CountsScrollbackAndVisible()
    {
        var buffer = new ScrollbackBuffer(100)
        {
            VisibleRows = 24
        };
        for (int i = 0; i < 10; i++)
        {
            buffer.AddLine(new() { Content = $"line {i}" });
        }
        Assert.AreEqual(10 + 24, buffer.TotalLines);
    }

    [TestMethod]
    public void ConfigurableMaxLines()
    {
        var buffer = new ScrollbackBuffer(500);
        Assert.AreEqual(500, buffer.MaxLines);
        var defaultBuffer = new ScrollbackBuffer();
        Assert.AreEqual(10000, defaultBuffer.MaxLines);
    }

    [TestMethod]
    public void Clear_RemovesAllLines()
    {
        var buffer = new ScrollbackBuffer(100);
        for (int i = 0; i < 20; i++)
        {
            buffer.AddLine(new() { Content = $"line {i}" });
        }
        buffer.Clear();
        Assert.AreEqual(0, buffer.ScrollbackLineCount);
        Assert.AreEqual(0, buffer.ViewportRow);
    }

    [TestMethod]
    public void ScrollUp_ClampsToZero()
    {
        var buffer = new ScrollbackBuffer(100)
        {
            VisibleRows = 24
        };
        for (int i = 0; i < 10; i++)
        {
            buffer.AddLine(new() { Content = $"line {i}" });
        }
        buffer.ScrollTo(3);
        buffer.ScrollUp(100);
        Assert.AreEqual(0, buffer.ViewportRow);
    }

    [TestMethod]
    public void ScrollDown_ClampsToMaxViewport()
    {
        var buffer = new ScrollbackBuffer(100)
        {
            VisibleRows = 24
        };
        for (int i = 0; i < 50; i++)
        {
            buffer.AddLine(new() { Content = $"line {i}" });
        }
        buffer.ScrollDown(1000);
        Assert.AreEqual(50, buffer.ViewportRow);
    }

    [TestMethod]
    public void Search_FindsMultipleMatchesInSameLine()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.AddLine(new() { Content = "ab ab ab" });
        List<SearchMatch> matches = buffer.Search("ab");
        Assert.HasCount(3, matches);
        Assert.AreEqual(0, matches[0].Column);
        Assert.AreEqual(3, matches[1].Column);
        Assert.AreEqual(6, matches[2].Column);
    }

    [TestMethod]
    public void GetLine_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.AddLine(new() { Content = "only line" });
        TerminalLine act() => buffer.GetLine(5);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>((Func<TerminalLine>)act);
    }

    [TestMethod]
    public void CircularWrap_ManyOverwrites_MaintainsCorrectOrder()
    {
        var buffer = new ScrollbackBuffer(5);
        for (int i = 0; i < 20; i++)
        {
            buffer.AddLine(new() { Content = $"line {i}" });
        }
        Assert.AreEqual(5, buffer.ScrollbackLineCount);
        Assert.AreEqual("line 15", buffer.GetLine(0).Content);
        Assert.AreEqual("line 16", buffer.GetLine(1).Content);
        Assert.AreEqual("line 17", buffer.GetLine(2).Content);
        Assert.AreEqual("line 18", buffer.GetLine(3).Content);
        Assert.AreEqual("line 19", buffer.GetLine(4).Content);
    }
}
