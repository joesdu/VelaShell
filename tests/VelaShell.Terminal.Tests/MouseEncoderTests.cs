using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Mouse")]
public class MouseEncoderTests
{
    private static TerminalModes Modes(MouseTracking tracking, MouseEncoding encoding = MouseEncoding.Default) =>
        new() { Mouse = tracking, MouseEncoding = encoding };

    private static string Sgr(byte[]? bytes) => Encoding.ASCII.GetString(bytes!);

    [TestMethod]
    public void Encode_WhenTrackingOff_ReturnsNull()
    {
        byte[]? bytes = MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.Left,
            0, 0, false, false, false, Modes(MouseTracking.None));
        Assert.IsNull(bytes);
    }

    [TestMethod]
    public void Encode_X10Press_UsesLegacyOffsetEncoding()
    {
        byte[]? bytes = MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.Left,
            0, 0, false, false, false, Modes(MouseTracking.X10));

        // ESC [ M, then (32+button), (32+col+1), (32+row+1).
        Assert.AreSequenceEqual(new byte[] { 0x1b, (byte)'[', (byte)'M', 32, 33, 33 }, bytes);
    }

    [TestMethod]
    public void Encode_X10_DoesNotReportReleaseMoveOrWheel()
    {
        TerminalModes m = Modes(MouseTracking.X10);
        Assert.IsNull(MouseEncoder.Encode(TerminalMouseEventType.Release, TerminalMouseButton.Left, 0, 0, false, false, false, m));
        Assert.IsNull(MouseEncoder.Encode(TerminalMouseEventType.Move, TerminalMouseButton.None, 0, 0, false, false, false, m));
        Assert.IsNull(MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.WheelUp, 0, 0, false, false, false, m));
    }

    [TestMethod]
    public void Encode_SgrPressAndRelease_UseAngleFormatWithMandmTerminators()
    {
        TerminalModes m = Modes(MouseTracking.Normal, MouseEncoding.Sgr);

        byte[]? press = MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.Left, 4, 2, false, false, false, m);
        byte[]? release = MouseEncoder.Encode(TerminalMouseEventType.Release, TerminalMouseButton.Left, 4, 2, false, false, false, m);

        Assert.AreEqual("\x1b[<0;5;3M", Sgr(press));
        Assert.AreEqual("\x1b[<0;5;3m", Sgr(release));
    }

    [TestMethod]
    public void Encode_LegacyRelease_UsesButtonCodeThree()
    {
        byte[]? bytes = MouseEncoder.Encode(TerminalMouseEventType.Release, TerminalMouseButton.Left,
            0, 0, false, false, false, Modes(MouseTracking.Normal));

        // Legacy release reports button bits = 3 → 32+3 = 35.
        Assert.AreSequenceEqual(new byte[] { 0x1b, (byte)'[', (byte)'M', 35, 33, 33 }, bytes);
    }

    [TestMethod]
    public void Encode_NormalMode_DoesNotReportMotion()
    {
        byte[]? bytes = MouseEncoder.Encode(TerminalMouseEventType.Move, TerminalMouseButton.Left,
            1, 1, false, false, false, Modes(MouseTracking.Normal, MouseEncoding.Sgr));
        Assert.IsNull(bytes);
    }

    [TestMethod]
    public void Encode_ButtonEventDrag_SetsMotionBit()
    {
        byte[]? bytes = MouseEncoder.Encode(TerminalMouseEventType.Move, TerminalMouseButton.Left,
            1, 1, false, false, false, Modes(MouseTracking.ButtonEvent, MouseEncoding.Sgr));

        // Left (0) + motion (32) = 32.
        Assert.AreEqual("\x1b[<32;2;2M", Sgr(bytes));
    }

    [TestMethod]
    public void Encode_AnyEventButtonlessMotion_UsesButtonThreePlusMotion()
    {
        byte[]? bytes = MouseEncoder.Encode(TerminalMouseEventType.Move, TerminalMouseButton.None,
            0, 0, false, false, false, Modes(MouseTracking.AnyEvent, MouseEncoding.Sgr));

        // None (3) + motion (32) = 35.
        Assert.AreEqual("\x1b[<35;1;1M", Sgr(bytes));
    }

    [TestMethod]
    public void Encode_Modifiers_AddToButtonCode()
    {
        TerminalModes m = Modes(MouseTracking.Normal, MouseEncoding.Sgr);

        Assert.AreEqual("\x1b[<4;1;1M", Sgr(MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.Left, 0, 0, true, false, false, m)));   // shift +4
        Assert.AreEqual("\x1b[<8;1;1M", Sgr(MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.Left, 0, 0, false, true, false, m)));   // alt +8
        Assert.AreEqual("\x1b[<16;1;1M", Sgr(MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.Left, 0, 0, false, false, true, m))); // control +16
    }

    [TestMethod]
    public void Encode_Wheel_ReportsButtons64And65()
    {
        TerminalModes m = Modes(MouseTracking.Normal, MouseEncoding.Sgr);

        Assert.AreEqual("\x1b[<64;1;1M", Sgr(MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.WheelUp, 0, 0, false, false, false, m)));
        Assert.AreEqual("\x1b[<65;1;1M", Sgr(MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.WheelDown, 0, 0, false, false, false, m)));
    }

    [TestMethod]
    public void Encode_Urxvt_UsesDecimalWithOffsetButtonCode()
    {
        byte[]? bytes = MouseEncoder.Encode(TerminalMouseEventType.Press, TerminalMouseButton.Left,
            0, 0, false, false, false, Modes(MouseTracking.Normal, MouseEncoding.Urxvt));

        // urxvt: ESC [ (cb+32) ; cx ; cy M  → 32;1;1
        Assert.AreEqual("\x1b[32;1;1M", Sgr(bytes));
    }
}
