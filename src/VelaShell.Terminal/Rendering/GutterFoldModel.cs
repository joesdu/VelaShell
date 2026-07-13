using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 侧栏折叠模型(WindTerm 式历史折叠)。折叠区域按「<see cref="TerminalRow" /> 对象引用」锚定:
/// 内容滚入 scrollback 时行对象按引用迁移,折叠随之保留;列宽 reflow 会重建行对象,届时由
/// <see cref="Clear" /> 使折叠失效。本类 UI 无关(只依赖 <see cref="TerminalScreen" />),可独立单测。
/// </summary>
public sealed class GutterFoldModel
{
    private sealed class Region
    {
        public required TerminalRow Anchor; // 折叠后仍可见的折叠头(区域末行 = 用户点击的那一行)
        public required TerminalRow[] Rows; // 区域全部行(含 Anchor),按缓冲区顺序
    }

    private readonly List<Region> _regions = [];

    /// <summary>当前是否存在折叠。无折叠时调用方走连续快路径,零开销。</summary>
    public bool HasFolds => _regions.Count > 0;

    /// <summary>折叠区域数量。</summary>
    public int Count => _regions.Count;

    /// <summary>清除全部折叠区域(如列宽 reflow 重建行对象、折叠失效时调用)。</summary>
    public void Clear() => _regions.Clear();

    /// <summary>绝对行 <paramref name="abs" /> 是否某折叠区域的折叠头(可点击展开)。</summary>
    public bool IsAnchor(TerminalScreen screen, int abs)
    {
        if (_regions.Count == 0 || abs < 0 || abs >= screen.TotalRows)
        {
            return false;
        }
        TerminalRow row = screen.ViewLine(abs);
        foreach (Region r in _regions)
        {
            if (ReferenceEquals(r.Anchor, row))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 本帧可见的绝对行序列(隐藏被折叠的非折叠头行);无折叠返回 null 表示走连续快路径。
    /// </summary>
    public List<int>? VisibleRowsOrNull(TerminalScreen screen)
    {
        if (_regions.Count == 0)
        {
            return null;
        }
        PruneStale(screen);
        if (_regions.Count == 0)
        {
            return null;
        }
        var hidden = new HashSet<TerminalRow>();
        foreach (Region reg in _regions)
        {
            foreach (TerminalRow r in reg.Rows)
            {
                if (!ReferenceEquals(r, reg.Anchor))
                {
                    hidden.Add(r);
                }
            }
        }
        var vis = new List<int>(screen.TotalRows);
        for (int abs = 0; abs < screen.TotalRows; abs++)
        {
            if (!hidden.Contains(screen.ViewLine(abs)))
            {
                vis.Add(abs);
            }
        }
        return vis;
    }

    /// <summary>
    /// 点击绝对行 <paramref name="abs" />:若它是折叠头则展开;否则把「上一折叠边界 .. abs」折叠成
    /// 一个新区域(点击的这一行作折叠头保留可见)。返回折叠状态是否发生变化。
    /// </summary>
    public bool Toggle(TerminalScreen screen, int abs)
    {
        if (abs < 0 || abs >= screen.TotalRows)
        {
            return false;
        }
        TerminalRow row = screen.ViewLine(abs);
        for (int i = 0; i < _regions.Count; i++)
        {
            if (ReferenceEquals(_regions[i].Anchor, row))
            {
                _regions.RemoveAt(i); // 再点折叠头 → 展开
                return true;
            }
        }
        int startAbs = NearestBoundaryAbove(screen, abs);
        if (abs <= startAbs)
        {
            return false; // 该行之前没有可折叠内容
        }
        int len = abs - startAbs + 1;
        var rows = new TerminalRow[len];
        for (int i = 0; i < len; i++)
        {
            rows[i] = screen.ViewLine(startAbs + i);
        }
        _regions.Add(new Region { Anchor = rows[^1], Rows = rows });
        return true;
    }

    /// <summary>解析既有折叠,返回 <paramref name="abs" /> 之上最近折叠头的下一行(无则 0)。</summary>
    private int NearestBoundaryAbove(TerminalScreen screen, int abs)
    {
        if (_regions.Count == 0)
        {
            return 0;
        }
        var absOf = new Dictionary<TerminalRow, int>();
        for (int a = 0; a < screen.TotalRows; a++)
        {
            absOf[screen.ViewLine(a)] = a;
        }
        int boundary = 0;
        foreach (Region reg in _regions)
        {
            int last = -1;
            foreach (TerminalRow r in reg.Rows)
            {
                if (absOf.TryGetValue(r, out int a))
                {
                    last = Math.Max(last, a);
                }
            }
            if (last >= 0 && last < abs)
            {
                boundary = Math.Max(boundary, last + 1);
            }
        }
        return boundary;
    }

    /// <summary>
    /// 填充「屏幕行 → 绝对缓冲行」映射(<paramref name="dest" />,长度 = 屏幕行数,-1 = 空):
    /// <paramref name="visibleRows" /> 为 null 时走连续快路径(dest[sr]=top+sr),否则按可见序列取值。
    /// 同时把 <paramref name="scrollOffset" /> 夹到可见范围(折叠会缩短可滚动范围)。纯计算,可单测。
    /// </summary>
    public static void FillScreenRowMap(int[] dest, List<int>? visibleRows, int totalRows, int screenRows, ref int scrollOffset)
    {
        int total = visibleRows?.Count ?? totalRows;
        int maxOffset = Math.Max(0, total - screenRows);
        if (scrollOffset > maxOffset)
        {
            scrollOffset = maxOffset;
        }
        if (scrollOffset < 0)
        {
            scrollOffset = 0;
        }
        int top = Math.Max(0, total - screenRows - scrollOffset);
        for (int sr = 0; sr < screenRows; sr++)
        {
            int vi = top + sr;
            dest[sr] = vi < total ? visibleRows?[vi] ?? vi : -1;
        }
    }

    /// <summary>折叠头行已被 scrollback 淘汰(不在当前缓冲区)则折叠失效,清除之。</summary>
    public void PruneStale(TerminalScreen screen)
    {
        if (_regions.Count == 0)
        {
            return;
        }
        var present = new HashSet<TerminalRow>();
        for (int abs = 0; abs < screen.TotalRows; abs++)
        {
            present.Add(screen.ViewLine(abs));
        }
        _regions.RemoveAll(reg => !present.Contains(reg.Anchor));
    }
}
