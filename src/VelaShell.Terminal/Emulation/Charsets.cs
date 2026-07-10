namespace VelaShell.Terminal.Emulation;

/// <summary>
/// Character-set translation for the DEC Special Graphics ("line drawing") set, which maps
/// ASCII 0x60-0x7E to box-drawing and block glyphs. Selected via <c>ESC ( 0</c> and used
/// heavily by ncurses/TUI programs.
/// </summary>
public static class Charsets
{
    // Index by (rune - 0x60). '\0' means "no translation".
    private static readonly char[] DecSpecial =
    [
        '◆', // ` diamond
        '▒', // a checkerboard
        '␉', // b HT
        '␌', // c FF
        '␍', // d CR
        '␊', // e LF
        '°', // f degree
        '±', // g plus/minus
        '␤', // h NL
        '␋', // i VT
        '┘', // j lower-right corner
        '┐', // k upper-right corner
        '┌', // l upper-left corner
        '└', // m lower-left corner
        '┼', // n crossing lines
        '⎺', // o scan line 1
        '⎻', // p scan line 3
        '─', // q horizontal line
        '⎼', // r scan line 7
        '⎽', // s scan line 9
        '├', // t left tee
        '┤', // u right tee
        '┴', // v bottom tee
        '┬', // w top tee
        '│', // x vertical line
        '≤', // y less-than-or-equal
        '≥', // z greater-than-or-equal
        'π', // { pi
        '≠', // | not equal
        '£', // } pound sterling
        '·'  // ~ centered dot
    ];

    public static int MapDecSpecial(int rune)
    {
        if (rune is < 0x60 or > 0x7E)
        {
            return rune;
        }
        char mapped = DecSpecial[rune - 0x60];
        return mapped;
    }
}
