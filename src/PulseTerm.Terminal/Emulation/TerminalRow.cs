using System.Text;

namespace PulseTerm.Terminal.Emulation;

/// <summary>
/// One row of the terminal grid: a logical width over a persistent cell buffer, plus a
/// "wrapped" flag used to reflow soft-wrapped lines on resize and to join lines for copy.
/// Narrowing only shrinks the logical width — the hidden tail cells are kept — so a
/// transient shrink (e.g. the tab-drag preview squeezing the shared control) restores the
/// text when the row grows back instead of erasing it (用户反馈：拖动标签后文字消失).
/// </summary>
public sealed class TerminalRow
{
    private TerminalCell[] _cells;
    private int _width;

    public TerminalRow(int columns)
    {
        _cells = new TerminalCell[columns];
        _width = columns;
    }

    /// <summary>True when the line was ended by autowrap rather than an explicit newline.</summary>
    public bool Wrapped { get; set; }

    public int Columns => _width;

    public ref TerminalCell CellRef(int col) => ref _cells[col];

    public TerminalCell this[int col]
    {
        get => _cells[col];
        set => _cells[col] = value;
    }

    public void Fill(in TerminalCell cell)
    {
        // Fill the whole backing buffer, not just the visible width: an explicit clear must
        // also kill any preserved (hidden) tail so it can't resurface on a later grow.
        for (int i = 0; i < _cells.Length; i++)
            _cells[i] = cell;
        Wrapped = false;
    }

    public void FillRange(int start, int endExclusive, in TerminalCell cell)
    {
        for (int i = Math.Max(0, start); i < Math.Min(_cells.Length, endExclusive); i++)
            _cells[i] = cell;
    }

    public void Resize(int columns, in TerminalCell blank)
    {
        if (columns == _width)
            return;

        if (columns > _cells.Length)
        {
            // Genuine growth beyond anything this row has held: extend and blank the new area.
            var next = new TerminalCell[columns];
            Array.Copy(_cells, next, _cells.Length);
            for (int i = _cells.Length; i < columns; i++)
                next[i] = blank;
            _cells = next;
        }

        // Shrinking (or re-growing within capacity) only moves the logical width; cells beyond
        // it keep their content and reappear when the row widens again.
        _width = columns;
    }

    /// <summary>Deletes <paramref name="count"/> cells at <paramref name="col"/>, shifting the tail left.</summary>
    public void DeleteCells(int col, int count, in TerminalCell blank)
    {
        if (count <= 0 || col >= _width)
            return;
        count = Math.Min(count, _width - col);
        Array.Copy(_cells, col + count, _cells, col, _width - col - count);
        FillRange(_width - count, _width, blank);
    }

    /// <summary>Inserts <paramref name="count"/> blank cells at <paramref name="col"/>, shifting the tail right.</summary>
    public void InsertCells(int col, int count, in TerminalCell blank)
    {
        if (count <= 0 || col >= _width)
            return;
        count = Math.Min(count, _width - col);
        Array.Copy(_cells, col, _cells, col + count, _width - col - count);
        FillRange(col, col + count, blank);
    }

    /// <summary>Text of the visible row up to the last non-blank cell (trailing blanks trimmed).</summary>
    public string GetText()
    {
        var sb = new StringBuilder(_width);
        int lastNonBlank = -1;
        for (int i = 0; i < _width; i++)
            if (_cells[i].Rune != 0)
                lastNonBlank = i;

        for (int i = 0; i <= lastNonBlank; i++)
            _cells[i].AppendText(sb);
        return sb.ToString();
    }

    public TerminalRow Clone()
    {
        var clone = new TerminalRow(_cells.Length) { Wrapped = Wrapped, _width = _width };
        Array.Copy(_cells, clone._cells, _cells.Length);
        return clone;
    }
}
