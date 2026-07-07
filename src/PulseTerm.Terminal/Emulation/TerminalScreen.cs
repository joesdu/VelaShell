namespace PulseTerm.Terminal.Emulation;

/// <summary>
/// The terminal grid: an active screen of <see cref="TerminalRow"/> plus a scrollback
/// history for the main (non-alternate) buffer. Owns the cursor, the vertical scroll
/// region (DECSTBM) and all structural editing primitives the emulator drives.
///
/// This class is intentionally UI-free and single-threaded; the rendering control reads
/// it on the UI thread only.
/// </summary>
public sealed class TerminalScreen
{
    private TerminalRow[] _lines;
    private readonly List<TerminalRow> _scrollback = new();

    public TerminalScreen(int columns, int rows, int maxScrollback = 10_000)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        MaxScrollback = Math.Max(0, maxScrollback);
        _lines = NewLines(Rows, Columns);
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int MaxScrollback { get; set; }

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }

    /// <summary>Top margin of the scroll region (0-based, inclusive).</summary>
    public int ScrollTop { get; private set; }

    /// <summary>Bottom margin of the scroll region (0-based, inclusive).</summary>
    public int ScrollBottom { get; private set; }

    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Total rows available to the viewport (scrollback + active screen).</summary>
    public int TotalRows => _scrollback.Count + Rows;

    private static TerminalRow[] NewLines(int rows, int cols)
    {
        var lines = new TerminalRow[rows];
        for (int i = 0; i < rows; i++)
            lines[i] = new TerminalRow(cols);
        return lines;
    }

    public TerminalRow ActiveLine(int screenRow) => _lines[Math.Clamp(screenRow, 0, Rows - 1)];

    /// <summary>
    /// Returns a row addressed in "total" space: 0..ScrollbackCount-1 are history,
    /// ScrollbackCount..TotalRows-1 are the active screen. Used by the renderer.
    /// </summary>
    public TerminalRow ViewLine(int absoluteRow)
    {
        // Callers can hold stale row indexes (a selection made before a resize, a pointer
        // dragged past the top edge); clamp instead of throwing.
        absoluteRow = Math.Clamp(absoluteRow, 0, TotalRows - 1);
        if (absoluteRow < _scrollback.Count)
            return _scrollback[absoluteRow];
        return _lines[absoluteRow - _scrollback.Count];
    }

    public ref TerminalCell CellRef(int x, int y) => ref _lines[y].CellRef(x);

    public void SetCell(int x, int y, in TerminalCell cell)
    {
        if ((uint)x < (uint)Columns && (uint)y < (uint)Rows)
            _lines[y][x] = cell;
    }

    public TerminalCell GetCell(int x, int y)
    {
        if ((uint)x < (uint)Columns && (uint)y < (uint)Rows)
            return _lines[y][x];
        return TerminalCell.Empty;
    }

    // ---- Cursor -------------------------------------------------------------

    public void SetCursor(int x, int y)
    {
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = Math.Clamp(y, 0, Rows - 1);
    }

    public void SetCursorX(int x) => CursorX = Math.Clamp(x, 0, Columns - 1);

    public void SetCursorY(int y) => CursorY = Math.Clamp(y, 0, Rows - 1);

    /// <summary>Places the cursor exactly one past the last column, used to defer autowrap.</summary>
    public void SetCursorAtEndOfLine() => CursorX = Columns;

    public void SetMargins(int top, int bottom)
    {
        if (top < 0) top = 0;
        if (bottom > Rows - 1) bottom = Rows - 1;
        if (top >= bottom)
        {
            top = 0;
            bottom = Rows - 1;
        }
        ScrollTop = top;
        ScrollBottom = bottom;
    }

    public void ResetMargins()
    {
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }

    // ---- Vertical movement --------------------------------------------------

    /// <summary>Line feed / Index: move down one line, scrolling the region up at the bottom margin.</summary>
    public void Index(in TerminalCell blank)
    {
        if (CursorY == ScrollBottom)
            ScrollUp(1, blank);
        else if (CursorY < Rows - 1)
            CursorY++;
    }

    /// <summary>Reverse Index: move up one line, scrolling the region down at the top margin.</summary>
    public void ReverseIndex(in TerminalCell blank)
    {
        if (CursorY == ScrollTop)
            ScrollDown(1, blank);
        else if (CursorY > 0)
            CursorY--;
    }

    /// <summary>Scrolls the scroll region up by <paramref name="count"/> lines. When the region
    /// spans the whole screen, retired top lines are pushed into scrollback.</summary>
    public void ScrollUp(int count, in TerminalCell blank)
    {
        count = Math.Clamp(count, 0, ScrollBottom - ScrollTop + 1);
        bool fullScreen = ScrollTop == 0 && ScrollBottom == Rows - 1;

        for (int i = 0; i < count; i++)
        {
            TerminalRow retired = _lines[ScrollTop];
            if (fullScreen && MaxScrollback > 0)
            {
                _scrollback.Add(retired);
                if (_scrollback.Count > MaxScrollback)
                    _scrollback.RemoveAt(0);
            }
            for (int y = ScrollTop; y < ScrollBottom; y++)
                _lines[y] = _lines[y + 1];
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[ScrollBottom] = fresh;
        }
    }

    public void ScrollDown(int count, in TerminalCell blank)
    {
        count = Math.Clamp(count, 0, ScrollBottom - ScrollTop + 1);
        for (int i = 0; i < count; i++)
        {
            for (int y = ScrollBottom; y > ScrollTop; y--)
                _lines[y] = _lines[y - 1];
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[ScrollTop] = fresh;
        }
    }

    // ---- Line editing (within scroll region) --------------------------------

    public void InsertLines(int count, in TerminalCell blank)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
            return;
        count = Math.Clamp(count, 0, ScrollBottom - CursorY + 1);
        for (int i = 0; i < count; i++)
        {
            for (int y = ScrollBottom; y > CursorY; y--)
                _lines[y] = _lines[y - 1];
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[CursorY] = fresh;
        }
    }

    public void DeleteLines(int count, in TerminalCell blank)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
            return;
        count = Math.Clamp(count, 0, ScrollBottom - CursorY + 1);
        for (int i = 0; i < count; i++)
        {
            for (int y = CursorY; y < ScrollBottom; y++)
                _lines[y] = _lines[y + 1];
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[ScrollBottom] = fresh;
        }
    }

    public void InsertChars(int count, in TerminalCell blank) =>
        _lines[CursorY].InsertCells(CursorX, count, blank);

    public void DeleteChars(int count, in TerminalCell blank) =>
        _lines[CursorY].DeleteCells(CursorX, count, blank);

    public void EraseChars(int count, in TerminalCell blank) =>
        _lines[CursorY].FillRange(CursorX, CursorX + count, blank);

    // ---- Erase --------------------------------------------------------------

    /// <summary>ED — Erase in Display. mode 0: cursor→end, 1: start→cursor, 2: all, 3: scrollback.</summary>
    public void EraseInDisplay(int mode, in TerminalCell blank)
    {
        switch (mode)
        {
            case 0:
                _lines[CursorY].FillRange(CursorX, Columns, blank);
                // The line no longer continues onto the next row; a stale soft-wrap flag
                // would make the resize reflow merge unrelated rows (prompt redraw bug).
                _lines[CursorY].Wrapped = false;
                for (int y = CursorY + 1; y < Rows; y++)
                    _lines[y].Fill(blank);
                break;
            case 1:
                _lines[CursorY].FillRange(0, CursorX + 1, blank);
                for (int y = 0; y < CursorY; y++)
                    _lines[y].Fill(blank);
                break;
            case 2:
                for (int y = 0; y < Rows; y++)
                    _lines[y].Fill(blank);
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    /// <summary>EL — Erase in Line. mode 0: cursor→end, 1: start→cursor, 2: whole line.</summary>
    public void EraseInLine(int mode, in TerminalCell blank)
    {
        switch (mode)
        {
            case 0:
                _lines[CursorY].FillRange(CursorX, Columns, blank);
                // Erasing to the end of the line severs its soft-wrap continuation — this is
                // exactly what readline's "\r ESC[K + prompt" redraw emits on every WINCH, and
                // a stale Wrapped flag here made resize reflows merge the redrawn prompt with
                // whatever followed (progressively corrupting the buffer on repeated drags).
                _lines[CursorY].Wrapped = false;
                break;
            case 1: _lines[CursorY].FillRange(0, CursorX + 1, blank); break;
            case 2: _lines[CursorY].Fill(blank); break;
        }
    }

    // ---- Buffer switching & resize -----------------------------------------

    public void ResetToBlank(in TerminalCell blank)
    {
        foreach (var line in _lines)
            line.Fill(blank);
        CursorX = 0;
        CursorY = 0;
        ResetMargins();
    }

    public void ClearScrollback() => _scrollback.Clear();

    /// <summary>
    /// Replaces the active lines wholesale (used when switching between main and alternate
    /// buffers). The provided array becomes the live screen.
    /// </summary>
    public TerminalRow[] SwapLines(TerminalRow[] replacement)
    {
        var previous = _lines;
        _lines = replacement;
        return previous;
    }

    public TerminalRow[] SnapshotLines() => _lines;

    public TerminalRow[] CreateBlankLines(in TerminalCell blank)
    {
        var lines = NewLines(Rows, Columns);
        foreach (var l in lines)
            l.Fill(blank);
        return lines;
    }

    public void Resize(int columns, int rows, in TerminalCell blank)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
            return;

        // Column changes on the primary screen reflow the whole buffer (the mainstream
        // approach — Windows Terminal / iTerm2 / VTE / kitty): soft-wrapped rows are joined
        // back into logical lines and re-wrapped at the new width, so narrowing never
        // destroys content and widening re-joins it. The alternate screen (MaxScrollback
        // 0 — htop/vim/tmux) is NOT reflowed: those programs repaint themselves on
        // SIGWINCH, matching every mainstream terminal.
        if (columns != Columns && MaxScrollback > 0)
        {
            ReflowResize(columns, rows, blank);
            return;
        }

        // Alternate screen column resize: hard grow/shrink each row in place.
        if (columns != Columns)
        {
            foreach (var line in _lines)
                line.Resize(columns, blank);
            foreach (var line in _scrollback)
                line.Resize(columns, blank);
        }

        // Row resize: when shrinking, discard only genuinely blank bottom rows; everything
        // else retires from the top into scrollback. (This used to drop ANY row below the
        // cursor — during drag-resize storms, where the cursor can sit mid-buffer, that
        // silently ate real content a few rows per shrink.) When growing, pull lines back
        // from scrollback if available.
        if (rows < Rows)
        {
            int remove = Rows - rows;

            int blankBottom = 0;
            for (int y = Rows - 1; y > CursorY && blankBottom < remove; y--)
            {
                if (_lines[y].Wrapped || _lines[y].LastNonBlank() >= 0)
                    break;
                blankBottom++;
            }

            int fromBottom = blankBottom;
            int fromTop = remove - fromBottom;

            var next = new TerminalRow[rows];
            for (int i = 0; i < fromTop; i++)
            {
                if (MaxScrollback > 0)
                {
                    _scrollback.Add(_lines[i]);
                    if (_scrollback.Count > MaxScrollback)
                        _scrollback.RemoveAt(0);
                }
            }
            Array.Copy(_lines, fromTop, next, 0, rows);
            _lines = next;
            CursorY = Math.Max(0, CursorY - fromTop);
        }
        else if (rows > Rows)
        {
            int add = rows - Rows;
            var next = new TerminalRow[rows];
            int pulled = Math.Min(add, _scrollback.Count);
            for (int i = 0; i < pulled; i++)
            {
                next[i] = _scrollback[_scrollback.Count - pulled + i];
            }
            _scrollback.RemoveRange(_scrollback.Count - pulled, pulled);
            Array.Copy(_lines, 0, next, pulled, Rows);
            for (int i = pulled + Rows; i < rows; i++)
            {
                var fresh = new TerminalRow(columns);
                fresh.Fill(blank);
                next[i] = fresh;
            }
            _lines = next;
            CursorY += pulled;
        }

        Columns = columns;
        Rows = rows;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        CursorX = Math.Clamp(CursorX, 0, Columns - 1);
        CursorY = Math.Clamp(CursorY, 0, Rows - 1);
    }

    // ---- Reflow (primary-screen column resize) -------------------------------

    /// <summary>
    /// Rebuilds the entire buffer at a new width: physical rows are joined into logical
    /// lines along their <see cref="TerminalRow.Wrapped"/> flags, each logical line is
    /// re-wrapped to <paramref name="newCols"/> (wide characters kept atomic), and the
    /// result is split back into scrollback + a bottom-anchored screen. The cursor is
    /// carried through as (logical line, cell offset) so it lands on the same character.
    /// </summary>
    private void ReflowResize(int newCols, int newRows, in TerminalCell blank)
    {
        int cursorAbs = _scrollback.Count + CursorY;

        // 1. Flatten scrollback + screen into one physical row list.
        var physical = new List<TerminalRow>(_scrollback.Count + Rows);
        physical.AddRange(_scrollback);
        physical.AddRange(_lines);

        // Drop trailing blank, unwrapped rows below the cursor — they're just the unused
        // bottom of the screen and would otherwise pad the scrollback with empties.
        while (physical.Count > cursorAbs + 1)
        {
            var last = physical[^1];
            if (last.Wrapped || last.LastNonBlank() >= 0)
                break;
            physical.RemoveAt(physical.Count - 1);
        }

        // 2. Re-emit logical lines at the new width.
        var rebuilt = new List<TerminalRow>(physical.Count);
        int newCursorRow = -1, newCursorCol = 0;

        int i = 0;
        while (i < physical.Count)
        {
            // A logical line spans [i..j]: every row but the last carries the Wrapped flag.
            int j = i;
            while (j < physical.Count - 1 && physical[j].Wrapped)
                j++;

            // Collect its cells: wrapped segments contribute their full width, the final
            // segment is trimmed at the last non-blank cell (extended to cover the cursor).
            var cells = new List<TerminalCell>();
            int cursorOffset = -1;
            for (int r = i; r <= j; r++)
            {
                var row = physical[r];
                int len = r < j ? row.Columns : row.LastNonBlank() + 1;
                if (r == cursorAbs)
                {
                    int cursorCol = Math.Min(CursorX, row.Columns - 1);
                    len = Math.Max(len, cursorCol + 1);
                    cursorOffset = cells.Count + cursorCol;
                }
                for (int c = 0; c < len; c++)
                    cells.Add(row[c]);
            }

            EmitLogicalLine(cells, cursorOffset, newCols, blank, rebuilt, ref newCursorRow, ref newCursorCol);
            i = j + 1;
        }

        if (rebuilt.Count == 0)
            rebuilt.Add(NewBlankRow(newCols, blank));
        if (newCursorRow < 0)
        {
            newCursorRow = rebuilt.Count - 1;
            newCursorCol = 0;
        }

        // 3. Split back: the screen is the bottom-most newRows rows (content stays anchored
        //    at the bottom like every terminal). The split NEVER discards rows: everything
        //    above the screen goes to scrollback, and a cursor that mapped above the window
        //    is clamped into it rather than dragging the window up and silently dropping the
        //    tail rows (that drop is what ate the buffer on repeated drag-resizes).
        int screenStart = Math.Max(0, rebuilt.Count - newRows);

        _scrollback.Clear();
        for (int r = 0; r < screenStart; r++)
            _scrollback.Add(rebuilt[r]);
        while (_scrollback.Count > MaxScrollback)
        {
            _scrollback.RemoveAt(0);
        }

        var lines = new TerminalRow[newRows];
        int idx = 0;
        for (int r = screenStart; r < rebuilt.Count && idx < newRows; r++, idx++)
            lines[idx] = rebuilt[r];
        for (; idx < newRows; idx++)
            lines[idx] = NewBlankRow(newCols, blank);
        _lines = lines;

        Columns = newCols;
        Rows = newRows;
        ScrollTop = 0;
        ScrollBottom = newRows - 1;
        CursorY = Math.Clamp(newCursorRow - screenStart, 0, newRows - 1);
        CursorX = Math.Clamp(newCursorCol, 0, newCols - 1);
    }

    /// <summary>Wraps one logical line's cells into rows of <paramref name="cols"/>, keeping
    /// wide-character lead/trail pairs on the same row, marking every produced row but the
    /// last as soft-wrapped, and reporting where <paramref name="cursorOffset"/> landed.</summary>
    private static void EmitLogicalLine(List<TerminalCell> cells, int cursorOffset, int cols,
        in TerminalCell blank, List<TerminalRow> output, ref int cursorRow, ref int cursorCol)
    {
        var row = NewBlankRow(cols, blank);
        output.Add(row);
        int col = 0;

        for (int k = 0; k < cells.Count; k++)
        {
            var cell = cells[k];
            // A wide pair (lead + trailing marker) must stay together on one row.
            bool wide = cols >= 2 && !cell.IsWideTrailing &&
                        k + 1 < cells.Count && cells[k + 1].IsWideTrailing;
            int need = wide ? 2 : 1;

            if (col + need > cols)
            {
                row.Wrapped = true;
                row = NewBlankRow(cols, blank);
                output.Add(row);
                col = 0;
            }

            if (k == cursorOffset)
            {
                cursorRow = output.Count - 1;
                cursorCol = col;
            }

            row[col++] = cell;
            if (wide)
            {
                if (k + 1 == cursorOffset)
                {
                    cursorRow = output.Count - 1;
                    cursorCol = col - 1;
                }
                row[col++] = cells[k + 1];
                k++;
            }
        }

        // A cursor sitting on the (blank) cell right past the collected content.
        if (cursorOffset == cells.Count && cursorOffset >= 0)
        {
            cursorRow = output.Count - 1;
            cursorCol = Math.Min(col, cols - 1);
        }
    }

    private static TerminalRow NewBlankRow(int cols, in TerminalCell blank)
    {
        var row = new TerminalRow(cols);
        row.Fill(blank);
        return row;
    }
}
