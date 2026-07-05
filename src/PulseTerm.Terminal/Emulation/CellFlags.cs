namespace PulseTerm.Terminal.Emulation;

/// <summary>
/// Rendition attributes carried by a <see cref="TerminalCell"/>. Mirrors the SGR
/// (Select Graphic Rendition) attributes an xterm-class terminal supports.
/// </summary>
[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    /// <summary>Swap foreground and background at render time (SGR 7).</summary>
    Inverse = 1 << 5,
    /// <summary>Text is not drawn (SGR 8).</summary>
    Invisible = 1 << 6,
    Strikethrough = 1 << 7,
    DoubleUnderline = 1 << 8,
    /// <summary>Second cell of a double-width character; carries no glyph of its own.</summary>
    WideTrailing = 1 << 9,
    /// <summary>Cell was written via the DEC special graphics charset (line drawing).</summary>
    Protected = 1 << 10,
}
