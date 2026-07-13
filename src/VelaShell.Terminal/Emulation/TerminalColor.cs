namespace VelaShell.Terminal.Emulation;

/// <summary>
/// How a <see cref="TerminalColor" /> resolves to an on-screen color.
/// </summary>
public enum TerminalColorKind : byte
{
    /// <summary>Use the terminal's configured default foreground/background.</summary>
    Default = 0,

    /// <summary>Index into the 256-color palette (0-15 = ANSI, 16-231 = cube, 232-255 = grayscale).</summary>
    Indexed = 1,

    /// <summary>Direct 24-bit truecolor.</summary>
    Rgb = 2
}

/// <summary>
/// A cell color that is independent from any particular palette. The rendering layer
/// resolves <see cref="TerminalColorKind.Default" /> and <see cref="TerminalColorKind.Indexed" />
/// against the active <see cref="TerminalPalette" />.
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    /// <summary>How this color resolves to an on-screen color.</summary>
    public TerminalColorKind Kind { get; }

    /// <summary>Palette index when <see cref="Kind" /> is Indexed.</summary>
    public byte Index { get; }

    /// <summary>Red channel when <see cref="Kind" /> is Rgb.</summary>
    public byte R { get; }

    /// <summary>Green channel when <see cref="Kind" /> is Rgb.</summary>
    public byte G { get; }

    /// <summary>Blue channel when <see cref="Kind" /> is Rgb.</summary>
    public byte B { get; }

    private TerminalColor(TerminalColorKind kind, byte index, byte r, byte g, byte b)
    {
        Kind = kind;
        Index = index;
        R = r;
        G = g;
        B = b;
    }

    /// <summary>The terminal default foreground/background sentinel.</summary>
    public static TerminalColor Default => new(TerminalColorKind.Default, 0, 0, 0, 0);

    /// <summary>Creates an indexed color from a 256-color palette index (clamped to 0-255).</summary>
    public static TerminalColor FromIndex(int index) => new(TerminalColorKind.Indexed, (byte)Math.Clamp(index, 0, 255), 0, 0, 0);

    /// <summary>Creates a 24-bit truecolor from the given red, green and blue channels.</summary>
    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(TerminalColorKind.Rgb, 0, r, g, b);

    /// <summary>Whether this color is the terminal default sentinel.</summary>
    public bool IsDefault => Kind == TerminalColorKind.Default;

    /// <summary>Determines whether this color equals another color.</summary>
    public bool Equals(TerminalColor other) => Kind == other.Kind && Index == other.Index && R == other.R && G == other.G && B == other.B;

    /// <summary>Determines whether this color equals the given object.</summary>
    public override bool Equals(object? obj) => obj is TerminalColor other && Equals(other);

    /// <summary>Returns a hash code combining all color components.</summary>
    public override int GetHashCode() => HashCode.Combine((byte)Kind, Index, R, G, B);

    /// <summary>Determines whether two colors are equal.</summary>
    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);

    /// <summary>Determines whether two colors are not equal.</summary>
    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);
}
