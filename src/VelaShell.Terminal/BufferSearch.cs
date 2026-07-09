using System;
using System.Collections.Generic;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal;

/// <summary>One search hit inside the terminal buffer, in absolute-row/character space.</summary>
public readonly record struct BufferSearchHit(int Row, int StartCol, int Length);

/// <summary>
/// Case-insensitive plain-text search over the whole terminal buffer (scrollback + screen),
/// used by the in-terminal search bar (spec §5.3). Pure logic — no UI dependencies.
/// </summary>
public static class BufferSearch
{
    public static IReadOnlyList<BufferSearchHit> FindAll(TerminalScreen screen, string query)
    {
        if (string.IsNullOrEmpty(query))
            return Array.Empty<BufferSearchHit>();

        var hits = new List<BufferSearchHit>();
        int totalRows = screen.TotalRows;

        for (int row = 0; row < totalRows; row++)
        {
            var text = screen.ViewLine(row).GetText();
            int index = 0;
            while (true)
            {
                int found = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;
                hits.Add(new BufferSearchHit(row, found, query.Length));
                index = found + Math.Max(1, query.Length);
            }
        }

        return hits;
    }
}
