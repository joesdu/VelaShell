namespace VelaShell.Terminal.Emulation;

/// <summary>
/// The terminal grid: an active screen of <see cref="TerminalRow" /> plus a scrollback
/// history for the main (non-alternate) buffer. Owns the cursor, the vertical scroll
/// region (DECSTBM) and all structural editing primitives the emulator drives.
/// This class is intentionally UI-free and single-threaded; the rendering control reads
/// it on the UI thread only.
/// </summary>
public sealed class TerminalScreen
{
    /// <summary>头部搬移的攒批粒度:每退休这么多行才做一次 RemoveRange 物理搬移。</summary>
    private const int ScrollbackTrimChunk = 1024;

    private readonly List<TerminalRow> _scrollback = [];

    /// <summary>
    /// 头部已裁剪但尚未物理搬移的行数。scrollback 满容量后每滚动一行都退休一行,
    /// 逐行 RemoveAt(0) 是 O(n) 整表搬移(1 万行缓冲下 cat 大文件 = O(n²) 卡死 UI 线程);
    /// 改为头指针前移 O(1),攒满 <see cref="ScrollbackTrimChunk" /> 行才搬移一次,
    /// 对外的 <see cref="ScrollbackCount" /> 语义与逐行裁剪完全一致。
    /// </summary>
    private int _scrollbackStart;

    private TerminalRow[] _lines;

    /// <summary>Creates a screen of the given size with the given scrollback capacity.</summary>
    public TerminalScreen(int columns, int rows, int maxScrollback = 10_000)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        MaxScrollback = Math.Max(0, maxScrollback);
        _lines = NewLines(Rows, Columns);
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }

    /// <summary>Number of columns in the active screen.</summary>
    public int Columns { get; private set; }

    /// <summary>Number of rows in the active screen.</summary>
    public int Rows { get; private set; }

    /// <summary>Maximum number of scrollback lines retained for the main buffer.</summary>
    public int MaxScrollback { get; set; }

    /// <summary>Current cursor column (0-based).</summary>
    public int CursorX { get; private set; }

    /// <summary>Current cursor row within the active screen (0-based).</summary>
    public int CursorY { get; private set; }

    /// <summary>Top margin of the scroll region (0-based, inclusive).</summary>
    public int ScrollTop { get; private set; }

    /// <summary>Bottom margin of the scroll region (0-based, inclusive).</summary>
    public int ScrollBottom { get; private set; }

    /// <summary>Current number of lines held in scrollback.</summary>
    public int ScrollbackCount => _scrollback.Count - _scrollbackStart;

    /// <summary>Total rows available to the viewport (scrollback + active screen).</summary>
    public int TotalRows => ScrollbackCount + Rows;

    private static TerminalRow[] NewLines(int rows, int cols)
    {
        var lines = new TerminalRow[rows];
        for (int i = 0; i < rows; i++)
        {
            lines[i] = new(cols);
        }
        return lines;
    }

    /// <summary>Returns the active-screen row at the given index (clamped into range).</summary>
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
        if (absoluteRow < ScrollbackCount)
        {
            return _scrollback[_scrollbackStart + absoluteRow];
        }
        return _lines[absoluteRow - ScrollbackCount];
    }

    /// <summary>
    /// 退休语义裁剪:超出容量时头指针前移(O(1)),被裁行置空让 GC 尽早回收;
    /// 攒满一块才做一次 RemoveRange 物理搬移,把逐行 O(n) 摊薄为 O(1)。
    /// </summary>
    private void TrimScrollbackToMax()
    {
        while (ScrollbackCount > MaxScrollback)
        {
            _scrollback[_scrollbackStart] = null!;
            _scrollbackStart++;
        }
        if (_scrollbackStart >= ScrollbackTrimChunk)
        {
            _scrollback.RemoveRange(0, _scrollbackStart);
            _scrollbackStart = 0;
        }
    }

    /// <summary>
    /// 把头部已裁剪区立即物理搬移掉,使 <c>_scrollback[0]</c> 重新对齐逻辑首行。
    /// 需要整表遍历/展平 scrollback 的冷路径(resize/reflow)先调用它,
    /// 之后即可按无偏移方式直接使用列表。
    /// </summary>
    private void CompactScrollback()
    {
        if (_scrollbackStart > 0)
        {
            _scrollback.RemoveRange(0, _scrollbackStart);
            _scrollbackStart = 0;
        }
    }

    /// <summary>Returns a mutable reference to the cell at (<paramref name="x" />, <paramref name="y" />).</summary>
    public ref TerminalCell CellRef(int x, int y) => ref _lines[y].CellRef(x);

    /// <summary>Writes a cell at (<paramref name="x" />, <paramref name="y" />); out-of-range coordinates are ignored.</summary>
    public void SetCell(int x, int y, in TerminalCell cell)
    {
        if ((uint)x < (uint)Columns && (uint)y < (uint)Rows)
        {
            _lines[y][x] = cell;
        }
    }

    /// <summary>Reads the cell at (<paramref name="x" />, <paramref name="y" />); returns <see cref="TerminalCell.Empty" /> when out of range.</summary>
    public TerminalCell GetCell(int x, int y)
    {
        if ((uint)x < (uint)Columns && (uint)y < (uint)Rows)
        {
            return _lines[y][x];
        }
        return TerminalCell.Empty;
    }

    // ---- Cursor -------------------------------------------------------------

    /// <summary>Moves the cursor to (<paramref name="x" />, <paramref name="y" />), clamped into the screen bounds.</summary>
    public void SetCursor(int x, int y)
    {
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = Math.Clamp(y, 0, Rows - 1);
    }

    /// <summary>Sets the cursor column, clamped into range.</summary>
    public void SetCursorX(int x) => CursorX = Math.Clamp(x, 0, Columns - 1);

    /// <summary>Sets the cursor row, clamped into range.</summary>
    public void SetCursorY(int y) => CursorY = Math.Clamp(y, 0, Rows - 1);

    /// <summary>Places the cursor exactly one past the last column, used to defer autowrap.</summary>
    public void SetCursorAtEndOfLine() => CursorX = Columns;

    /// <summary>Sets the vertical scroll region (DECSTBM); falls back to the full screen on an invalid range.</summary>
    public void SetMargins(int top, int bottom)
    {
        if (top < 0)
        {
            top = 0;
        }
        if (bottom > Rows - 1)
        {
            bottom = Rows - 1;
        }
        if (top >= bottom)
        {
            top = 0;
            bottom = Rows - 1;
        }
        ScrollTop = top;
        ScrollBottom = bottom;
    }

    /// <summary>Resets the scroll region to span the whole screen.</summary>
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
        {
            ScrollUp(1, blank);
        }
        else if (CursorY < Rows - 1)
        {
            CursorY++;
        }
    }

    /// <summary>Reverse Index: move up one line, scrolling the region down at the top margin.</summary>
    public void ReverseIndex(in TerminalCell blank)
    {
        if (CursorY == ScrollTop)
        {
            ScrollDown(1, blank);
        }
        else if (CursorY > 0)
        {
            CursorY--;
        }
    }

    /// <summary>
    /// Scrolls the scroll region up by <paramref name="count" /> lines. When the region
    /// spans the whole screen, retired top lines are pushed into scrollback.
    /// </summary>
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
                TrimScrollbackToMax();
            }
            for (int y = ScrollTop; y < ScrollBottom; y++)
            {
                _lines[y] = _lines[y + 1];
            }
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[ScrollBottom] = fresh;
        }
    }

    /// <summary>Scrolls the scroll region down by <paramref name="count" /> lines, inserting blank rows at the top margin.</summary>
    public void ScrollDown(int count, in TerminalCell blank)
    {
        count = Math.Clamp(count, 0, ScrollBottom - ScrollTop + 1);
        for (int i = 0; i < count; i++)
        {
            for (int y = ScrollBottom; y > ScrollTop; y--)
            {
                _lines[y] = _lines[y - 1];
            }
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[ScrollTop] = fresh;
        }
    }

    // ---- Line editing (within scroll region) --------------------------------

    /// <summary>IL — inserts <paramref name="count" /> blank lines at the cursor row, pushing rows down within the scroll region.</summary>
    public void InsertLines(int count, in TerminalCell blank)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }
        count = Math.Clamp(count, 0, ScrollBottom - CursorY + 1);
        for (int i = 0; i < count; i++)
        {
            for (int y = ScrollBottom; y > CursorY; y--)
            {
                _lines[y] = _lines[y - 1];
            }
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[CursorY] = fresh;
        }
    }

    /// <summary>DL — deletes <paramref name="count" /> lines at the cursor row, pulling rows up within the scroll region.</summary>
    public void DeleteLines(int count, in TerminalCell blank)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }
        count = Math.Clamp(count, 0, ScrollBottom - CursorY + 1);
        for (int i = 0; i < count; i++)
        {
            for (int y = CursorY; y < ScrollBottom; y++)
            {
                _lines[y] = _lines[y + 1];
            }
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[ScrollBottom] = fresh;
        }
    }

    /// <summary>ICH — inserts <paramref name="count" /> blank cells at the cursor, shifting the rest of the line right.</summary>
    public void InsertChars(int count, in TerminalCell blank) => _lines[CursorY].InsertCells(CursorX, count, blank);

    /// <summary>DCH — deletes <paramref name="count" /> cells at the cursor, shifting the rest of the line left.</summary>
    public void DeleteChars(int count, in TerminalCell blank) => _lines[CursorY].DeleteCells(CursorX, count, blank);

    /// <summary>ECH — erases <paramref name="count" /> cells starting at the cursor without shifting the line.</summary>
    public void EraseChars(int count, in TerminalCell blank) => _lines[CursorY].FillRange(CursorX, CursorX + count, blank);

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
                {
                    _lines[y].Fill(blank);
                }
                break;
            case 1:
                _lines[CursorY].FillRange(0, CursorX + 1, blank);
                for (int y = 0; y < CursorY; y++)
                {
                    _lines[y].Fill(blank);
                }
                break;
            case 2:
                for (int y = 0; y < Rows; y++)
                {
                    _lines[y].Fill(blank);
                }
                break;
            case 3:
                _scrollback.Clear();
                _scrollbackStart = 0;
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
            case 1:
                _lines[CursorY].FillRange(0, CursorX + 1, blank);
                break;
            case 2:
                _lines[CursorY].Fill(blank);
                break;
        }
    }

    // ---- Buffer switching & resize -----------------------------------------

    /// <summary>Clears every active line to blank, homes the cursor, and resets the scroll margins.</summary>
    public void ResetToBlank(in TerminalCell blank)
    {
        foreach (TerminalRow line in _lines)
        {
            line.Fill(blank);
        }
        CursorX = 0;
        CursorY = 0;
        ResetMargins();
    }

    /// <summary>Discards all scrollback history.</summary>
    public void ClearScrollback()
    {
        _scrollback.Clear();
        _scrollbackStart = 0;
    }

    /// <summary>
    /// Replaces the active lines wholesale (used when switching between main and alternate
    /// buffers). The provided array becomes the live screen.
    /// </summary>
    public TerminalRow[] SwapLines(TerminalRow[] replacement)
    {
        TerminalRow[] previous = _lines;
        _lines = replacement;
        return previous;
    }

    /// <summary>Returns the live active-line array (not a copy).</summary>
    public TerminalRow[] SnapshotLines() => _lines;

    /// <summary>Allocates a fresh screen-sized array of blank rows without installing it.</summary>
    public TerminalRow[] CreateBlankLines(in TerminalCell blank)
    {
        TerminalRow[] lines = NewLines(Rows, Columns);
        foreach (TerminalRow l in lines)
        {
            l.Fill(blank);
        }
        return lines;
    }

    /// <summary>
    /// Resizes the screen to <paramref name="columns" />×<paramref name="rows" />, reflowing the primary
    /// buffer on column changes and retiring/pulling rows through scrollback on row changes.
    /// </summary>
    public void Resize(int columns, int rows, in TerminalCell blank)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        // resize/reflow 要整表遍历或展平 scrollback,先把头部裁剪区搬移掉,
        // 后续逻辑保持无偏移访问(冷路径,一次 O(n) 可接受)。
        CompactScrollback();

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
            foreach (TerminalRow line in _lines)
            {
                line.Resize(columns, blank);
            }
            foreach (TerminalRow line in _scrollback)
            {
                line.Resize(columns, blank);
            }
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
                {
                    break;
                }
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
                    TrimScrollbackToMax();
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
    /// lines along their <see cref="TerminalRow.Wrapped" /> flags, each logical line is
    /// re-wrapped to <paramref name="newCols" /> (wide characters kept atomic), and the
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
            TerminalRow last = physical[^1];
            if (last.Wrapped || last.LastNonBlank() >= 0)
            {
                break;
            }
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
            {
                j++;
            }

            // Collect its cells: wrapped segments contribute their full width, the final
            // segment is trimmed at the last non-blank cell (extended to cover the cursor).
            var cells = new List<TerminalCell>();
            int cursorOffset = -1;
            DateTime? lineTimestamp = null;
            for (int r = i; r <= j; r++)
            {
                TerminalRow row = physical[r];
                // 行时间戳(时间/行号侧栏)必须穿过 reflow —— 否则改列宽(含开关侧栏导致的列变化)
                // 会把历史行的时间戳清空。取该逻辑行各物理段中最后一个非空时间戳(= 最后收到输出的时间)。
                if (row.Timestamp is { } t)
                {
                    lineTimestamp = t;
                }
                int len = r < j ? row.Columns : row.LastNonBlank() + 1;
                if (r == cursorAbs)
                {
                    int cursorCol = Math.Min(CursorX, row.Columns - 1);
                    len = Math.Max(len, cursorCol + 1);
                    cursorOffset = cells.Count + cursorCol;
                }
                for (int c = 0; c < len; c++)
                {
                    cells.Add(row[c]);
                }
            }
            int emittedStart = rebuilt.Count;
            EmitLogicalLine(cells, cursorOffset, newCols, blank, rebuilt, ref newCursorRow, ref newCursorCol);
            for (int r = emittedStart; r < rebuilt.Count; r++)
            {
                rebuilt[r].Timestamp = lineTimestamp;
            }
            i = j + 1;
        }
        if (rebuilt.Count == 0)
        {
            rebuilt.Add(NewBlankRow(newCols, blank));
        }
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
        _scrollbackStart = 0;
        for (int r = 0; r < screenStart; r++)
        {
            _scrollback.Add(rebuilt[r]);
        }
        if (_scrollback.Count > MaxScrollback)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
        }
        var lines = new TerminalRow[newRows];
        int idx = 0;
        for (int r = screenStart; r < rebuilt.Count && idx < newRows; r++, idx++)
        {
            lines[idx] = rebuilt[r];
        }
        for (; idx < newRows; idx++)
        {
            lines[idx] = NewBlankRow(newCols, blank);
        }
        _lines = lines;
        Columns = newCols;
        Rows = newRows;
        ScrollTop = 0;
        ScrollBottom = newRows - 1;
        CursorY = Math.Clamp(newCursorRow - screenStart, 0, newRows - 1);
        CursorX = Math.Clamp(newCursorCol, 0, newCols - 1);
    }

    /// <summary>
    /// Wraps one logical line's cells into rows of <paramref name="cols" />, keeping
    /// wide-character lead/trail pairs on the same row, marking every produced row but the
    /// last as soft-wrapped, and reporting where <paramref name="cursorOffset" /> landed.
    /// </summary>
    private static void EmitLogicalLine(List<TerminalCell> cells,
        int cursorOffset,
        int cols,
        in TerminalCell blank,
        List<TerminalRow> output,
        ref int cursorRow,
        ref int cursorCol)
    {
        TerminalRow row = NewBlankRow(cols, blank);
        output.Add(row);
        int col = 0;
        for (int k = 0; k < cells.Count; k++)
        {
            TerminalCell cell = cells[k];
            // A wide pair (lead + trailing marker) must stay together on one row.
            bool wide = cols >= 2 &&
                        !cell.IsWideTrailing &&
                        k + 1 < cells.Count &&
                        cells[k + 1].IsWideTrailing;
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
