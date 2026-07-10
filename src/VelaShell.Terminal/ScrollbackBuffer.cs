namespace VelaShell.Terminal;

public class ScrollbackBuffer
{
    private readonly TerminalLine[] _buffer;
    private int _head;

    public ScrollbackBuffer(int maxLines = 10000)
    {
        if (maxLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines), @"MaxLines must be positive.");
        }
        MaxLines = maxLines;
        _buffer = new TerminalLine[maxLines];
    }

    public int MaxLines { get; }

    public int ScrollbackLineCount { get; private set; }

    public int VisibleRows { get; set; }

    public int TotalLines => ScrollbackLineCount + VisibleRows;

    public int ViewportRow { get; private set; }

    public void AddLine(TerminalLine line)
    {
        _buffer[_head] = line;
        _head = (_head + 1) % MaxLines;
        if (ScrollbackLineCount < MaxLines)
        {
            ScrollbackLineCount++;
        }
    }

    public TerminalLine GetLine(int absoluteRow)
    {
        if (absoluteRow < 0 || absoluteRow >= ScrollbackLineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteRow));
        }
        int startIndex = (_head - ScrollbackLineCount + MaxLines) % MaxLines;
        int index = (startIndex + absoluteRow) % MaxLines;
        return _buffer[index];
    }

    public void ScrollTo(int absoluteRow)
    {
        ViewportRow = Math.Clamp(absoluteRow, 0, Math.Max(0, ScrollbackLineCount));
    }

    public void ScrollUp(int lines)
    {
        ScrollTo(ViewportRow - lines);
    }

    public void ScrollDown(int lines)
    {
        ScrollTo(ViewportRow + lines);
    }

    public List<SearchMatch> Search(string query)
    {
        var matches = new List<SearchMatch>();
        if (string.IsNullOrEmpty(query))
        {
            return matches;
        }
        for (int row = 0; row < ScrollbackLineCount; row++)
        {
            TerminalLine line = GetLine(row);
            int startIndex = 0;
            while (startIndex <= line.Content.Length - query.Length)
            {
                int found = line.Content.IndexOf(query, startIndex, StringComparison.Ordinal);
                if (found < 0)
                {
                    break;
                }
                matches.Add(new()
                {
                    Row = row,
                    Column = found,
                    Length = query.Length
                });
                startIndex = found + 1;
            }
        }
        return matches;
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _head = 0;
        ScrollbackLineCount = 0;
        ViewportRow = 0;
    }
}
