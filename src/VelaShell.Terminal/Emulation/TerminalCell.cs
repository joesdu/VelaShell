using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// A single character cell in the terminal grid. This is a value type so rows can be
/// stored as contiguous arrays and cleared cheaply.
/// </summary>
public struct TerminalCell : IEquatable<TerminalCell>
{
    /// <summary>
    /// The Unicode scalar value shown in this cell. 0 means an empty cell (rendered as a space).
    /// Combining characters are folded into the base cell via <see cref="Combining" />.
    /// </summary>
    public int Rune;

    /// <summary>Optional combining marks appended after <see cref="Rune" />. Null for the common case.</summary>
    public string? Combining;

    public TerminalColor Foreground;
    public TerminalColor Background;
    public CellFlags Flags;

    public static TerminalCell Empty => new()
    {
        Rune = 0,
        Foreground = TerminalColor.Default,
        Background = TerminalColor.Default,
        Flags = CellFlags.None
    };

    /// <summary>A blank cell that still carries the given background/attributes (used when erasing).</summary>
    public static TerminalCell Blank(TerminalColor background, CellFlags flags) =>
        new()
        {
            Rune = 0,
            Foreground = TerminalColor.Default,
            Background = background,
            Flags = flags & CellFlags.Inverse // erased cells keep background-affecting bits only
        };

    public readonly bool IsWideTrailing => (Flags & CellFlags.WideTrailing) != 0;

    /// <summary>Materializes the cell's text (base rune plus any combining marks).</summary>
    public readonly string GetText()
    {
        if (Rune == 0)
        {
            return " ";
        }
        if (Combining is null)
        {
            return char.ConvertFromUtf32(Rune);
        }
        return char.ConvertFromUtf32(Rune) + Combining;
    }

    public readonly void AppendText(StringBuilder sb)
    {
        if (IsWideTrailing)
        {
            return;
        }
        if (Rune == 0)
        {
            sb.Append(' ');
            return;
        }
        sb.Append(char.ConvertFromUtf32(Rune));
        if (Combining is not null)
        {
            sb.Append(Combining);
        }
    }

    public bool Equals(TerminalCell other) =>
        Rune == other.Rune &&
        Combining == other.Combining &&
        Foreground == other.Foreground &&
        Background == other.Background &&
        Flags == other.Flags;

    public override bool Equals(object? obj) => obj is TerminalCell other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Rune, Combining, Foreground, Background, Flags);
}
