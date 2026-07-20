using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal;

/// <summary>终端缓冲区内的一次搜索命中,采用绝对行/字符坐标。</summary>
public readonly record struct BufferSearchHit(int Row, int StartCol, int Length);

/// <summary>
/// 对整个终端缓冲区(回滚历史 + 当前屏幕)进行不区分大小写的纯文本搜索,
/// 供终端内搜索栏使用(规范 §5.3)。纯逻辑实现,不依赖任何 UI。
/// </summary>
public static class BufferSearch
{
    /// <summary>
    /// 在整个终端缓冲区(回滚历史 + 当前屏幕)中返回 <paramref name="query" /> 的全部不区分大小写匹配项;
    /// 空查询不会产生任何命中。
    /// </summary>
    /// <param name="screen">要搜索的终端缓冲区。</param>
    /// <param name="query">要搜索的纯文本。</param>
    /// <returns>全部命中,以绝对行/字符坐标表示,按缓冲区顺序排列。</returns>
    public static IReadOnlyList<BufferSearchHit> FindAll(TerminalScreen screen, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }
        var hits = new List<BufferSearchHit>();
        int totalRows = screen.TotalRows;
        for (int row = 0; row < totalRows; row++)
        {
            string text = screen.ViewLine(row).GetText();
            int index = 0;
            while (true)
            {
                int found = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    break;
                }
                hits.Add(new(row, found, query.Length));
                index = found + Math.Max(1, query.Length);
            }
        }
        return hits;
    }
}
