using System.Text;

namespace PulseTerm.Terminal.Emulation;

/// <summary>
/// One row of the terminal grid: a fixed-length array of <see cref="TerminalCell"/> plus
/// a "wrapped" flag used to reflow soft-wrapped lines on resize and to join lines for copy.
/// </summary>
public sealed class TerminalRow
{
    private TerminalCell[] _cells;

    public TerminalRow(int columns)
    {
        _cells = new TerminalCell[columns];
    }

    /// <summary>True when the line was ended by autowrap rather than an explicit newline.</summary>
    public bool Wrapped { get; set; }

    public int Columns => _cells.Length;

    public ref TerminalCell CellRef(int col) => ref _cells[col];

    public TerminalCell this[int col]
    {
        get => _cells[col];
        set => _cells[col] = value;
    }

    public void Fill(in TerminalCell cell)
    {
        for (int i = 0; i < _cells.Length; i++)
            _cells[i] = cell;
        Wrapped = false;
    }

    public void FillRange(int start, int endExclusive, in TerminalCell cell)
    {
        for (int i = Math.Max(0, start); i < Math.Min(_cells.Length, endExclusive); i++)
            _cells[i] = cell;
    }

    /// <summary>Hard grow/shrink to an exact width. Only used where reflow doesn't apply
    /// (the alternate screen, whose programs fully redraw on resize) — the main screen is
    /// resized via <see cref="TerminalScreen"/> reflow, which preserves content.</summary>
    public void Resize(int columns, in TerminalCell blank)
    {
        if (columns == _cells.Length)
            return;
        var next = new TerminalCell[columns];
        int copy = Math.Min(columns, _cells.Length);
        Array.Copy(_cells, next, copy);
        for (int i = copy; i < columns; i++)
            next[i] = blank;
        _cells = next;
    }

    /// <summary>Deletes <paramref name="count"/> cells at <paramref name="col"/>, shifting the tail left.</summary>
    public void DeleteCells(int col, int count, in TerminalCell blank)
    {
        if (count <= 0 || col >= _cells.Length)
            return;
        count = Math.Min(count, _cells.Length - col);
        Array.Copy(_cells, col + count, _cells, col, _cells.Length - col - count);
        FillRange(_cells.Length - count, _cells.Length, blank);
    }

    /// <summary>Inserts <paramref name="count"/> blank cells at <paramref name="col"/>, shifting the tail right.</summary>
    public void InsertCells(int col, int count, in TerminalCell blank)
    {
        if (count <= 0 || col >= _cells.Length)
            return;
        count = Math.Min(count, _cells.Length - col);
        Array.Copy(_cells, col, _cells, col + count, _cells.Length - col - count);
        FillRange(col, col + count, blank);
    }

    /// <summary>Index of the last cell with content, or -1 for an all-blank row.</summary>
    public int LastNonBlank()
    {
        for (int i = _cells.Length - 1; i >= 0; i--)
            if (_cells[i].Rune != 0)
                return i;
        return -1;
    }

    /// <summary>Text of the row up to the last non-blank cell (trailing blanks trimmed).</summary>
    public string GetText()
    {
        var sb = new StringBuilder(_cells.Length);
        int lastNonBlank = LastNonBlank();
        for (int i = 0; i <= lastNonBlank; i++)
            _cells[i].AppendText(sb);
        return sb.ToString();
    }

    public TerminalRow Clone()
    {
        var clone = new TerminalRow(_cells.Length) { Wrapped = Wrapped };
        Array.Copy(_cells, clone._cells, _cells.Length);
        return clone;
    }
}
