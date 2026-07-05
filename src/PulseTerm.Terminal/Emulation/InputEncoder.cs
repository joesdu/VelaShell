using System.Text;
using Avalonia.Input;

namespace PulseTerm.Terminal.Emulation;

/// <summary>
/// Translates Avalonia key events into the byte sequences a host expects, honoring
/// application-cursor-key mode, VT52 mode and xterm modifier encoding. Text (printable)
/// input is encoded separately as UTF-8.
/// </summary>
public static class InputEncoder
{
    private static readonly byte[] Empty = Array.Empty<byte>();

    /// <summary>Encodes ordinary text (from IME / TextInput) as UTF-8.</summary>
    public static byte[] EncodeText(string text) =>
        string.IsNullOrEmpty(text) ? Empty : Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// Encodes a non-text key press. Returns null when the key produces no direct sequence
    /// (the control should then rely on the TextInput event for the character).
    /// </summary>
    public static byte[]? Encode(Key key, KeyModifiers mods, TerminalModes modes, TerminalType type)
    {
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool alt = mods.HasFlag(KeyModifiers.Alt);
        bool shift = mods.HasFlag(KeyModifiers.Shift);

        // Control-letter combinations map to C0 controls.
        if (ctrl && !alt)
        {
            byte? c0 = ControlByte(key, shift);
            if (c0 is { } b)
                return WithAlt(new[] { b }, alt: false);
        }

        bool app = modes.ApplicationCursorKeys && type != TerminalType.Vt52;
        bool vt52 = type == TerminalType.Vt52;
        int mod = ModifierCode(mods);

        switch (key)
        {
            case Key.Up: return Cursor('A', app, vt52, mod, alt);
            case Key.Down: return Cursor('B', app, vt52, mod, alt);
            case Key.Right: return Cursor('C', app, vt52, mod, alt);
            case Key.Left: return Cursor('D', app, vt52, mod, alt);
            case Key.Home: return Cursor('H', app, vt52, mod, alt);
            case Key.End: return Cursor('F', app, vt52, mod, alt);

            case Key.Insert: return Tilde(2, mod, alt);
            case Key.Delete: return Tilde(3, mod, alt);
            case Key.PageUp: return Tilde(5, mod, alt);
            case Key.PageDown: return Tilde(6, mod, alt);

            case Key.Enter:
                return WithAlt(modes.NewLineMode ? "\r\n"u8.ToArray() : "\r"u8.ToArray(), alt);
            case Key.Tab:
                return shift ? Esc("[Z") : WithAlt(new byte[] { 0x09 }, alt);
            case Key.Back:
                return WithAlt(new byte[] { ctrl ? (byte)0x08 : (byte)0x7F }, alt);
            case Key.Escape:
                return WithAlt(new byte[] { 0x1B }, alt);

            case Key.F1: return Function(1, 'P', mod, alt, vt52);
            case Key.F2: return Function(2, 'Q', mod, alt, vt52);
            case Key.F3: return Function(3, 'R', mod, alt, vt52);
            case Key.F4: return Function(4, 'S', mod, alt, vt52);
            case Key.F5: return Tilde(15, mod, alt);
            case Key.F6: return Tilde(17, mod, alt);
            case Key.F7: return Tilde(18, mod, alt);
            case Key.F8: return Tilde(19, mod, alt);
            case Key.F9: return Tilde(20, mod, alt);
            case Key.F10: return Tilde(21, mod, alt);
            case Key.F11: return Tilde(23, mod, alt);
            case Key.F12: return Tilde(24, mod, alt);
        }

        return null;
    }

    private static byte[] Cursor(char final, bool app, bool vt52, int mod, bool alt)
    {
        if (vt52)
            return Encoding.ASCII.GetBytes($"\x1b{final}");
        if (mod > 1)
            return Encoding.ASCII.GetBytes($"\x1b[1;{mod}{final}");
        string prefix = app ? "\x1bO" : "\x1b[";
        return WithAlt(Encoding.ASCII.GetBytes($"{prefix}{final}"), alt && mod == 1 ? false : false);
    }

    private static byte[] Tilde(int code, int mod, bool alt)
    {
        string seq = mod > 1 ? $"\x1b[{code};{mod}~" : $"\x1b[{code}~";
        return WithAlt(Encoding.ASCII.GetBytes(seq), alt && mod == 1);
    }

    private static byte[] Function(int number, char final, int mod, bool alt, bool vt52)
    {
        if (vt52)
            return Encoding.ASCII.GetBytes($"\x1b{final}");
        if (mod > 1)
            return Encoding.ASCII.GetBytes($"\x1b[1;{mod}{final}");
        return WithAlt(Encoding.ASCII.GetBytes($"\x1bO{final}"), alt && mod == 1);
    }

    private static byte[] Esc(string tail) => Encoding.ASCII.GetBytes("\x1b" + tail);

    /// <summary>Prepends ESC when Alt is held (xterm meta-sends-escape convention).</summary>
    private static byte[] WithAlt(byte[] seq, bool alt)
    {
        if (!alt)
            return seq;
        var result = new byte[seq.Length + 1];
        result[0] = 0x1B;
        Array.Copy(seq, 0, result, 1, seq.Length);
        return result;
    }

    /// <summary>xterm modifier parameter: 1 + shift(1) + alt(2) + ctrl(4) + meta(8).</summary>
    private static int ModifierCode(KeyModifiers mods)
    {
        int m = 0;
        if (mods.HasFlag(KeyModifiers.Shift)) m += 1;
        if (mods.HasFlag(KeyModifiers.Alt)) m += 2;
        if (mods.HasFlag(KeyModifiers.Control)) m += 4;
        if (mods.HasFlag(KeyModifiers.Meta)) m += 8;
        return m + 1;
    }

    private static byte? ControlByte(Key key, bool shift)
    {
        // Letters A-Z -> 0x01..0x1A
        if (key is >= Key.A and <= Key.Z)
            return (byte)(key - Key.A + 1);

        return key switch
        {
            Key.Space => 0x00,
            Key.OemOpenBrackets => 0x1B, // Ctrl+[
            Key.OemBackslash or Key.Oem5 => 0x1C, // Ctrl+backslash
            Key.OemCloseBrackets or Key.Oem6 => 0x1D,
            Key.D2 when shift => 0x00, // Ctrl+@
            Key.D3 => 0x1B,
            Key.D4 => 0x1C,
            Key.D5 => 0x1D,
            Key.D6 => 0x1E, // Ctrl+^
            Key.D7 => 0x1F, // Ctrl+_
            Key.OemMinus => 0x1F,
            _ => null,
        };
    }
}
