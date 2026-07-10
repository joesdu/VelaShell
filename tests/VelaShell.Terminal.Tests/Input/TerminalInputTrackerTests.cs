using System.Text;
using VelaShell.Terminal.Input;

namespace VelaShell.Terminal.Tests.Input;

[TestClass]
[TestCategory("InputTracker")]
public class TerminalInputTrackerTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [TestMethod]
    public void PrintableInput_BuildsCurrentLine()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("docker ps"));
        Assert.AreEqual("docker ps", tracker.CurrentInput);
    }

    [TestMethod]
    public void Backspace_RemovesLastCharacter()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("lss"));
        tracker.Process([0x7F]);
        Assert.AreEqual("ls", tracker.CurrentInput);
    }

    [TestMethod]
    public void Backspace_RemovesWholeMultiByteCharacter()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("ls 中"));
        tracker.Process([0x7F]);
        Assert.AreEqual("ls ", tracker.CurrentInput);
    }

    [TestMethod]
    public void Enter_SubmitsAndResetsToEmpty()
    {
        var tracker = new TerminalInputTracker();
        string? submitted = null;
        tracker.CommandSubmitted += cmd => submitted = cmd;
        tracker.Process(Bytes("htop\r"));
        Assert.AreEqual("htop", submitted);
        Assert.AreEqual(string.Empty, tracker.CurrentInput);
    }

    [TestMethod]
    public void CtrlC_ClearsLineWithoutSubmit()
    {
        var tracker = new TerminalInputTracker();
        string? submitted = null;
        tracker.CommandSubmitted += cmd => submitted = cmd;
        tracker.Process(Bytes("rm -rf /"));
        tracker.Process([0x03]);
        Assert.IsNull(submitted);
        Assert.AreEqual(string.Empty, tracker.CurrentInput);
    }

    [TestMethod]
    public void ArrowKey_EscSequence_MarksUnknownUntilReset()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("ls"));
        tracker.Process([0x1B, (byte)'[', (byte)'A']); // ↑:shell 召回历史,本地不可知。
        Assert.IsNull(tracker.CurrentInput);

        // 未知态下继续键入也不能恢复跟踪(行内容仍不可知)。
        tracker.Process(Bytes("x"));
        Assert.IsNull(tracker.CurrentInput);

        // Enter 把行交给 shell,回到确定的空行,且不提交未知内容。
        string? submitted = null;
        tracker.CommandSubmitted += cmd => submitted = cmd;
        tracker.Process([0x0D]);
        Assert.IsNull(submitted);
        Assert.AreEqual(string.Empty, tracker.CurrentInput);
    }

    [TestMethod]
    public void TabCompletion_MarksUnknown()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("sys"));
        tracker.Process([0x09]);
        Assert.IsNull(tracker.CurrentInput);
    }

    [TestMethod]
    public void EnterOnUnknownState_DoesNotSubmit()
    {
        var tracker = new TerminalInputTracker();
        string? submitted = null;
        tracker.CommandSubmitted += cmd => submitted = cmd;
        tracker.Process([0x1B]);
        tracker.Process([0x0D]);
        Assert.IsNull(submitted);
    }

    [TestMethod]
    public void InputChanged_FiresOnEdits()
    {
        var tracker = new TerminalInputTracker();
        int fired = 0;
        tracker.InputChanged += () => fired++;
        tracker.Process(Bytes("ab"));
        tracker.Process([0x7F]);
        Assert.AreEqual(2, fired);
    }
}
