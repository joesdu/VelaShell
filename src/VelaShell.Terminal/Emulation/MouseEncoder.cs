using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>A mouse button (or wheel notch) in xterm button-code terms.</summary>
public enum TerminalMouseButton
{
    Left = 0,
    Middle = 1,
    Right = 2,
    None = 3, // buttonless motion / legacy release
    WheelUp = 64,
    WheelDown = 65
}

public enum TerminalMouseEventType
{
    Press,
    Release,
    Move
}

/// <summary>
/// Encodes pointer events into the byte sequences an application expects when it has enabled mouse
/// tracking (DECSET ?9/?1000/?1002/?1003) so programs like htop, btop, vim, tmux receive clicks,
/// drags and wheel notches. Honors the active encoding: legacy X10 (<c>ESC [ M</c>), SGR
/// (<c>ESC [ &lt; … M/m</c>, ?1006) and urxvt (<c>ESC [ … M</c>, ?1015). Kept UI-agnostic — the
/// caller passes plain modifier flags — so it is directly unit-testable.
/// </summary>
public static class MouseEncoder
{
    /// <summary>
    /// Returns the bytes to send for a mouse event under the current <paramref name="modes" />, or
    /// null when the event should not be reported (e.g. motion in a press-only mode, or any event
    /// while tracking is off). Column/row are 0-based cell coordinates within the visible screen.
    /// </summary>
    public static byte[]? Encode(
        TerminalMouseEventType type,
        TerminalMouseButton button,
        int column,
        int row,
        bool shift,
        bool alt,
        bool control,
        TerminalModes modes)
    {
        MouseTracking tracking = modes.Mouse;
        if (tracking == MouseTracking.None)
        {
            return null;
        }
        bool isWheel = button is TerminalMouseButton.WheelUp or TerminalMouseButton.WheelDown;

        // Which event types each tracking mode reports.
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (tracking)
        {
            case MouseTracking.X10:
                if (type != TerminalMouseEventType.Press || isWheel)
                {
                    return null;
                }
                break;
            case MouseTracking.Normal:
                // ReSharper disable once ConvertIfStatementToSwitchStatement
                if (type == TerminalMouseEventType.Move)
                {
                    return null;
                }
                if (type == TerminalMouseEventType.Release && isWheel)
                {
                    return null;
                }
                break;
            case MouseTracking.ButtonEvent:
            case MouseTracking.AnyEvent:
                if (type == TerminalMouseEventType.Release && isWheel)
                {
                    return null;
                }
                break;
        }
        bool sgr = modes.MouseEncoding == MouseEncoding.Sgr;
        int cb;
        if (isWheel)
        {
            cb = (int)button; // 64 / 65
        }
        else if (type == TerminalMouseEventType.Release && !sgr)
        {
            cb = 3; // legacy release: button bits = 3
        }
        else
        {
            cb = (int)button; // 0/1/2, or 3 (None) for buttonless motion
        }
        if (type == TerminalMouseEventType.Move)
        {
            cb += 32; // motion bit
        }

        // X10 predates modifier reporting; every other mode carries Shift/Alt/Control.
        if (tracking != MouseTracking.X10)
        {
            if (shift)
            {
                cb += 4;
            }
            if (alt)
            {
                cb += 8;
            }
            if (control)
            {
                cb += 16;
            }
        }
        int cx = column + 1; // reported coordinates are 1-based
        int cy = row + 1;
        return modes.MouseEncoding switch
        {
            MouseEncoding.Sgr   => Ascii($"\e[<{cb};{cx};{cy}{(type == TerminalMouseEventType.Release ? 'm' : 'M')}"),
            MouseEncoding.Urxvt => Ascii($"\e[{cb + 32};{cx};{cy}M"),
            _                   => EncodeX10(cb, cx, cy)
        };
    }

    // Legacy encoding: ESC [ M, then three bytes each offset by 32. Coordinates are limited to
    // 223 (255-32); anything larger is clamped, matching xterm.
    private static byte[] EncodeX10(int cb, int cx, int cy)
    {
        byte b = (byte)(32 + (cb & 0xFF));
        byte x = (byte)(32 + Math.Clamp(cx, 1, 223));
        byte y = (byte)(32 + Math.Clamp(cy, 1, 223));
        return [0x1b, (byte)'[', (byte)'M', b, x, y];
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
}
