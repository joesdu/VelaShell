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

    /// <summary>单元格的前景(文字)颜色。</summary>
    public TerminalColor Foreground;

    /// <summary>单元格的背景颜色。</summary>
    public TerminalColor Background;

    /// <summary>单元格的渲染属性位(加粗、反显、宽字符尾格等)。</summary>
    public CellFlags Flags;

    /// <summary>一个使用默认颜色、无属性的空单元格。</summary>
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

    /// <summary>是否为宽字符所占据的第二个(尾随)单元格。</summary>
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

    /// <summary>将单元格文本(基础字符及组合标记)追加到指定的 <see cref="StringBuilder" />;宽字符尾格不追加内容。</summary>
    /// <param name="sb">接收单元格文本的目标缓冲区。</param>
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

    /// <summary>判断两个单元格的字符、组合标记、颜色与属性是否全部相等。</summary>
    /// <param name="other">要比较的另一个单元格。</param>
    /// <returns>各字段均相等时返回 true。</returns>
    public readonly bool Equals(TerminalCell other) =>
        Rune == other.Rune &&
        Combining == other.Combining &&
        Foreground == other.Foreground &&
        Background == other.Background &&
        Flags == other.Flags;

    /// <summary>判断指定对象是否为相等的单元格。</summary>
    /// <param name="obj">要比较的对象。</param>
    /// <returns>对象为等值的 <see cref="TerminalCell" /> 时返回 true。</returns>
    public override readonly bool Equals(object? obj) => obj is TerminalCell other && Equals(other);

    /// <summary>返回基于全部字段的哈希码。</summary>
    /// <returns>单元格的哈希码。</returns>
    public override readonly int GetHashCode() => HashCode.Combine(Rune, Combining, Foreground, Background, Flags);

    /// <summary>判断两个单元格是否相等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>相等时返回 true。</returns>
    public static bool operator ==(TerminalCell left, TerminalCell right)
    {
        return left.Equals(right);
    }

    /// <summary>判断两个单元格是否不相等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>不相等时返回 true。</returns>
    public static bool operator !=(TerminalCell left, TerminalCell right)
    {
        return !(left == right);
    }
}
