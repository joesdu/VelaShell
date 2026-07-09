namespace VelaShell.Terminal.Emulation;

/// <summary>
/// The terminal emulation profile advertised to the remote host. Determines the
/// TERM string, the Device Attributes (DA) response, and which feature set the
/// emulator enables. xterm-256color is the primary/default profile.
/// </summary>
public enum TerminalType
{
    Vt52,
    Vt100,
    Vt102,
    Vt220,
    Vt320,      // "vt340" family — DEC VT300 series
    Vt340,
    Vt420,
    Vt520,
    Xterm,
    XtermusColor256,
}

public static class TerminalTypeExtensions
{
    /// <summary>The value sent as the TERM environment variable / PTY terminal type.</summary>
    public static string ToTermName(this TerminalType type) => type switch
    {
        TerminalType.Vt52 => "vt52",
        TerminalType.Vt100 => "vt100",
        TerminalType.Vt102 => "vt102",
        TerminalType.Vt220 => "vt220",
        TerminalType.Vt320 => "vt320",
        TerminalType.Vt340 => "vt340",
        TerminalType.Vt420 => "vt420",
        TerminalType.Vt520 => "vt520",
        TerminalType.Xterm => "xterm",
        TerminalType.XtermusColor256 => "xterm-256color",
        _ => "xterm-256color",
    };

    /// <summary>Parses a TERM string (e.g. "xterm-256color") back to a <see cref="TerminalType"/>.</summary>
    public static TerminalType FromTermName(string? term) => (term ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "vt52" => TerminalType.Vt52,
        "vt100" => TerminalType.Vt100,
        "vt102" => TerminalType.Vt102,
        "vt220" => TerminalType.Vt220,
        "vt320" => TerminalType.Vt320,
        "vt340" => TerminalType.Vt340,
        "vt420" => TerminalType.Vt420,
        "vt520" => TerminalType.Vt520,
        "xterm" => TerminalType.Xterm,
        "xterm-256color" => TerminalType.XtermusColor256,
        _ => TerminalType.XtermusColor256,
    };

    /// <summary>True for VT300+ / xterm profiles that support ANSI colors and 8-bit charsets.</summary>
    public static bool SupportsColor(this TerminalType type) =>
        type is TerminalType.Xterm or TerminalType.XtermusColor256
             or TerminalType.Vt320 or TerminalType.Vt340 or TerminalType.Vt420 or TerminalType.Vt520;

    /// <summary>True only for the xterm-256color profile (full 256/truecolor SGR).</summary>
    public static bool Supports256Color(this TerminalType type) =>
        type is TerminalType.XtermusColor256;

    public static bool IsVt52(this TerminalType type) => type == TerminalType.Vt52;

    /// <summary>
    /// The primary Device Attributes (CSI c) reply. The first parameter identifies the
    /// terminal class; subsequent parameters advertise supported extensions.
    /// </summary>
    public static string PrimaryDeviceAttributes(this TerminalType type) => type switch
    {
        // VT52 has no DA; it replies to ESC Z with ESC / Z instead (handled in the emulator).
        TerminalType.Vt52 => "\x1b/Z",
        // VT100 with AVO: "I am a VT100 with Advanced Video Option".
        TerminalType.Vt100 => "\x1b[?1;2c",
        TerminalType.Vt102 => "\x1b[?6c",
        // VT220: service class 62, with 132-columns, printer, selective erase, DRCS, UDK, NRCS.
        TerminalType.Vt220 => "\x1b[?62;1;2;6;7;8;9c",
        TerminalType.Vt320 => "\x1b[?63;1;2;6;7;8;9c",
        TerminalType.Vt340 => "\x1b[?63;1;2;4;6;7;8;9;15c",
        TerminalType.Vt420 => "\x1b[?64;1;2;6;7;8;9;15;18;19;21c",
        TerminalType.Vt520 => "\x1b[?65;1;2;6;7;8;9;12;15;18;19;21c",
        // xterm advertises itself as a VT100-class terminal with many extensions.
        TerminalType.Xterm => "\x1b[?1;2c",
        TerminalType.XtermusColor256 => "\x1b[?64;1;2;6;9;15;18;21;22c",
        _ => "\x1b[?64;1;2;6;9;15;18;21;22c",
    };

    /// <summary>The secondary Device Attributes (CSI > c) reply, identifying firmware/version.</summary>
    public static string SecondaryDeviceAttributes(this TerminalType type) => type switch
    {
        TerminalType.Vt220 => "\x1b[>1;10;0c",
        TerminalType.Vt320 => "\x1b[>24;20;0c",
        TerminalType.Vt340 => "\x1b[>19;20;0c",
        TerminalType.Vt420 => "\x1b[>41;20;0c",
        TerminalType.Vt520 => "\x1b[>65;20;0c",
        // xterm reports terminal type 0/41 and a patch level; we advertise as VT420-compatible xterm.
        TerminalType.Xterm or TerminalType.XtermusColor256 => "\x1b[>41;360;0c",
        _ => "\x1b[>0;10;0c",
    };
}
