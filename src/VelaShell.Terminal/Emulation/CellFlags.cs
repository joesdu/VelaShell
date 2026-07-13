namespace VelaShell.Terminal.Emulation;

/// <summary>
/// Rendition attributes carried by a <see cref="TerminalCell" />. Mirrors the SGR
/// (Select Graphic Rendition) attributes an xterm-class terminal supports.
/// </summary>
[Flags]
public enum CellFlags : ushort
{
    /// <summary>No rendition attributes.</summary>
    None = 0,

    /// <summary>Bold or increased-intensity text (SGR 1).</summary>
    Bold = 1 << 0,

    /// <summary>Dim or decreased-intensity text (SGR 2).</summary>
    Dim = 1 << 1,

    /// <summary>Italic text (SGR 3).</summary>
    Italic = 1 << 2,

    /// <summary>Underlined text (SGR 4).</summary>
    Underline = 1 << 3,

    /// <summary>Blinking text (SGR 5).</summary>
    Blink = 1 << 4,

    /// <summary>Swap foreground and background at render time (SGR 7).</summary>
    Inverse = 1 << 5,

    /// <summary>Text is not drawn (SGR 8).</summary>
    Invisible = 1 << 6,
    /// <summary>Struck-through text (SGR 9).</summary>
    Strikethrough = 1 << 7,

    /// <summary>Double-underlined text (SGR 21).</summary>
    DoubleUnderline = 1 << 8,

    /// <summary>Second cell of a double-width character; carries no glyph of its own.</summary>
    WideTrailing = 1 << 9,

    /// <summary>Cell was written via the DEC special graphics charset (line drawing).</summary>
    Protected = 1 << 10
}
