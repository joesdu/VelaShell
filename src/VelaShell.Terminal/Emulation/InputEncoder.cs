using System.Text;
using Avalonia.Input;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// Translates Avalonia key events into the byte sequences a host expects, honoring
/// application-cursor-key mode, VT52 mode and xterm modifier encoding. Text (printable)
/// input is encoded separately as UTF-8.
/// </summary>
public static class InputEncoder
{
    private static readonly byte[] Empty = [];

    /// <summary>Encodes ordinary text (from IME / TextInput) as UTF-8.</summary>
    public static byte[] EncodeText(string text) => string.IsNullOrEmpty(text) ? Empty : Encoding.UTF8.GetBytes(text);

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
            {
                return WithAlt([b], false);
            }
        }
        bool app = modes.ApplicationCursorKeys && type != TerminalType.Vt52;
        bool vt52 = type == TerminalType.Vt52;
        int mod = ModifierCode(mods);
        return key switch
        {
            Key.Up => Cursor('A', app, vt52, mod, alt),
            Key.Down => Cursor('B', app, vt52, mod, alt),
            Key.Right => Cursor('C', app, vt52, mod, alt),
            Key.Left => Cursor('D', app, vt52, mod, alt),
            Key.Home => Cursor('H', app, vt52, mod, alt),
            Key.End => Cursor('F', app, vt52, mod, alt),
            Key.Insert => Tilde(2, mod, alt),
            Key.Delete => Tilde(3, mod, alt),
            Key.PageUp => Tilde(5, mod, alt),
            Key.PageDown => Tilde(6, mod, alt),
            Key.Enter => WithAlt(modes.NewLineMode ? "\r\n"u8.ToArray() : "\r"u8.ToArray(), alt),
            Key.Tab => shift ? Esc("[Z") : WithAlt([0x09], alt),
            Key.Back => WithAlt([ctrl ? (byte)0x08 : (byte)0x7F], alt),
            Key.Escape => WithAlt([0x1B], alt),
            Key.F1 => Function(1, 'P', mod, alt, vt52),
            Key.F2 => Function(2, 'Q', mod, alt, vt52),
            Key.F3 => Function(3, 'R', mod, alt, vt52),
            Key.F4 => Function(4, 'S', mod, alt, vt52),
            Key.F5 => Tilde(15, mod, alt),
            Key.F6 => Tilde(17, mod, alt),
            Key.F7 => Tilde(18, mod, alt),
            Key.F8 => Tilde(19, mod, alt),
            Key.F9 => Tilde(20, mod, alt),
            Key.F10 => Tilde(21, mod, alt),
            Key.F11 => Tilde(23, mod, alt),
            Key.F12 => Tilde(24, mod, alt),
            _ => null
        };
    }

    private static byte[] Cursor(char final, bool app, bool vt52, int mod, bool alt)
    {
        if (vt52)
        {
            return Encoding.ASCII.GetBytes($"\e{final}");
        }
        if (mod > 1)
        {
            return Encoding.ASCII.GetBytes($"\e[1;{mod}{final}");
        }
        string prefix = app ? "\eO" : "\e[";
        return WithAlt(Encoding.ASCII.GetBytes($"{prefix}{final}"), false);
    }

    private static byte[] Tilde(int code, int mod, bool alt)
    {
        string seq = mod > 1 ? $"\e[{code};{mod}~" : $"\e[{code}~";
        return WithAlt(Encoding.ASCII.GetBytes(seq), alt && mod == 1);
    }

    private static byte[] Function(int number, char final, int mod, bool alt, bool vt52)
    {
        if (vt52)
        {
            return Encoding.ASCII.GetBytes($"\e{final}");
        }
        if (mod > 1)
        {
            return Encoding.ASCII.GetBytes($"\e[1;{mod}{final}");
        }
        return WithAlt(Encoding.ASCII.GetBytes($"\eO{final}"), alt && mod == 1);
    }

    private static byte[] Esc(string tail) => Encoding.ASCII.GetBytes("\e" + tail);

    /// <summary>Prepends ESC when Alt is held (xterm meta-sends-escape convention).</summary>
    private static byte[] WithAlt(byte[] seq, bool alt)
    {
        if (!alt)
        {
            return seq;
        }
        byte[] result = new byte[seq.Length + 1];
        result[0] = 0x1B;
        Array.Copy(seq, 0, result, 1, seq.Length);
        return result;
    }

    /// <summary>xterm modifier parameter: 1 + shift(1) + alt(2) + ctrl(4) + meta(8).</summary>
    private static int ModifierCode(KeyModifiers mods)
    {
        int m = 0;
        if (mods.HasFlag(KeyModifiers.Shift))
        {
            m += 1;
        }
        if (mods.HasFlag(KeyModifiers.Alt))
        {
            m += 2;
        }
        if (mods.HasFlag(KeyModifiers.Control))
        {
            m += 4;
        }
        if (mods.HasFlag(KeyModifiers.Meta))
        {
            m += 8;
        }
        return m + 1;
    }

    private static byte? ControlByte(Key key, bool shift)
    {
        // Letters A-Z -> 0x01..0x1A
        if (key is >= Key.A and <= Key.Z)
        {
            return (byte)(key - Key.A + 1);
        }
        return key switch
        {
            Key.Space => 0x00,
            Key.OemOpenBrackets => 0x1B, // Ctrl+[
            Key.OemBackslash or Key.Oem5 => 0x1C, // Ctrl+backslash
            Key.OemCloseBrackets => 0x1D,
            Key.D2 when shift => 0x00, // Ctrl+@
            Key.D3 => 0x1B,
            Key.D4 => 0x1C,
            Key.D5 => 0x1D,
            Key.D6 => 0x1E, // Ctrl+^
            Key.D7 => 0x1F, // Ctrl+_
            Key.OemMinus => 0x1F,
            _ => null
        };
    }
}
