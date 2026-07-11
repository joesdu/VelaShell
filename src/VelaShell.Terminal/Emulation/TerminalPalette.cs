namespace VelaShell.Terminal.Emulation;

/// <summary>
/// A 32-bit packed color (0xAARRGGBB) produced when resolving a <see cref="TerminalColor" />.
/// Kept independent from Avalonia so the engine stays UI-agnostic and testable.
/// </summary>
public readonly record struct Rgba(byte A, byte R, byte G, byte B)
{
    public uint Packed => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    public static Rgba FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);
}

/// <summary>
/// The active color palette: 256 indexed colors plus the semantic default foreground,
/// background and cursor colors. Index 0-15 can be themed; 16-255 follow the standard
/// xterm 6x6x6 color cube and grayscale ramp.
/// </summary>
public sealed class TerminalPalette
{
    private readonly Rgba[] _colors = new Rgba[256];

    public TerminalPalette()
    {
        InitializeAnsi16();
        InitializeCube();
        InitializeGrayscale();
    }

    public Rgba DefaultForeground { get; set; } = Rgba.FromRgb(0xE0, 0xE6, 0xED);

    public Rgba DefaultBackground { get; set; } = Rgba.FromRgb(0x08, 0x0C, 0x12);

    public Rgba CursorColor { get; set; } = Rgba.FromRgb(0x00, 0xD4, 0xAA);

    public Rgba SelectionBackground { get; set; } = new(0x60, 0x1C, 0x2A, 0x3F);

    public Rgba this[int index] => _colors[index & 0xFF];

    /// <summary>Overrides one of the 16 ANSI colors (used to apply the design's term-* tokens).</summary>
    public void SetAnsi(int index, Rgba color)
    {
        if (index is >= 0 and < 16)
        {
            _colors[index] = color;
        }
    }

    /// <summary>Resolves a cell color to concrete RGBA, honoring inverse video and bold-brightening.</summary>
    public Rgba Resolve(TerminalColor color, bool isBackground, bool bold)
    {
        switch (color.Kind)
        {
            case TerminalColorKind.Rgb:
                return Rgba.FromRgb(color.R, color.G, color.B);
            case TerminalColorKind.Indexed:
                int idx = color.Index;
                // Bold text brightens the low 8 ANSI colors, matching xterm behavior.
                if (bold && !isBackground && idx < 8)
                {
                    idx += 8;
                }
                return _colors[idx];
            case TerminalColorKind.Default:
            default:
                return isBackground ? DefaultBackground : DefaultForeground;
        }
    }

    private void InitializeAnsi16()
    {
        // Standard xterm defaults; the app overrides 0-15 from the .pen term-* tokens.
        var ansi = new (byte, byte, byte)[]
        {
            (0x00, 0x00, 0x00), (0xCD, 0x00, 0x00), (0x00, 0xCD, 0x00), (0xCD, 0xCD, 0x00),
            (0x00, 0x00, 0xEE), (0xCD, 0x00, 0xCD), (0x00, 0xCD, 0xCD), (0xE5, 0xE5, 0xE5),
            (0x7F, 0x7F, 0x7F), (0xFF, 0x00, 0x00), (0x00, 0xFF, 0x00), (0xFF, 0xFF, 0x00),
            (0x5C, 0x5C, 0xFF), (0xFF, 0x00, 0xFF), (0x00, 0xFF, 0xFF), (0xFF, 0xFF, 0xFF)
        };
        for (int i = 0; i < 16; i++)
        {
            _colors[i] = Rgba.FromRgb(ansi[i].Item1, ansi[i].Item2, ansi[i].Item3);
        }
    }

    private void InitializeCube()
    {
        // 216-color 6x6x6 cube at indices 16-231.
        ReadOnlySpan<byte> steps = [0x00, 0x5F, 0x87, 0xAF, 0xD7, 0xFF];
        int i = 16;
        foreach (byte r in steps)
            foreach (byte g in steps)
                foreach (byte b in steps)
                {
                    _colors[i++] = Rgba.FromRgb(r, g, b);
                }
    }

    private void InitializeGrayscale()
    {
        // 24-step grayscale ramp at indices 232-255.
        for (int i = 0; i < 24; i++)
        {
            byte v = (byte)(8 + i * 10);
            _colors[232 + i] = Rgba.FromRgb(v, v, v);
        }
    }
}
