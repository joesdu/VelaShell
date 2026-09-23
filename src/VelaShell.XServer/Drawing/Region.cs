// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer.Drawing;

/// <summary>整数矩形(左闭右开:覆盖 [X, X+Width) × [Y, Y+Height))。</summary>
/// <param name="X">左边。</param>
/// <param name="Y">上边。</param>
/// <param name="Width">宽;≤ 0 视为空。</param>
/// <param name="Height">高;≤ 0 视为空。</param>
public readonly record struct XRect(int X, int Y, int Width, int Height)
{
    /// <summary>右边(不含)。</summary>
    public int Right => X + Width;

    /// <summary>下边(不含)。</summary>
    public int Bottom => Y + Height;

    /// <summary>是否为空。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>交集;不相交时返回空矩形。</summary>
    public XRect Intersect(XRect other)
    {
        int x1 = Math.Max(X, other.X);
        int y1 = Math.Max(Y, other.Y);
        int x2 = Math.Min(Right, other.Right);
        int y2 = Math.Min(Bottom, other.Bottom);
        return x2 > x1 && y2 > y1 ? new(x1, y1, x2 - x1, y2 - y1) : default;
    }

    /// <summary>平移。</summary>
    public XRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>点是否在内。</summary>
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// 由互不重叠的矩形组成的区域:窗口可见区域、裁剪区域、Expose 区域都用它。
/// </summary>
/// <remarks>
/// 实现刻意朴素(矩形列表,O(n·m) 的集合运算):一个顶层窗口里同时存在的子窗口数量是几十量级,
/// 不值得为它上带状(banded)表示。以后真成为热点再换,接口不变。
/// </remarks>
internal sealed class Region
{
    private readonly List<XRect> _rects = [];

    public Region()
    {
    }

    public Region(XRect rect)
    {
        if (!rect.IsEmpty)
        {
            _rects.Add(rect);
        }
    }

    /// <summary>组成区域的矩形(互不重叠)。</summary>
    public IReadOnlyList<XRect> Rects => _rects;

    public bool IsEmpty => _rects.Count == 0;

    public Region Clone()
    {
        Region copy = new();
        copy._rects.AddRange(_rects);
        return copy;
    }

    /// <summary>外接矩形。</summary>
    public XRect Bounds
    {
        get
        {
            if (_rects.Count == 0)
            {
                return default;
            }
            int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
            foreach (XRect r in _rects)
            {
                x1 = Math.Min(x1, r.X);
                y1 = Math.Min(y1, r.Y);
                x2 = Math.Max(x2, r.Right);
                y2 = Math.Max(y2, r.Bottom);
            }
            return new(x1, y1, x2 - x1, y2 - y1);
        }
    }

    public bool Contains(int x, int y)
    {
        foreach (XRect r in _rects)
        {
            if (r.Contains(x, y))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>就地减去一个矩形。</summary>
    public Region Subtract(XRect cut)
    {
        if (cut.IsEmpty || _rects.Count == 0)
        {
            return this;
        }
        List<XRect> result = new(_rects.Count + 4);
        foreach (XRect r in _rects)
        {
            XRect i = r.Intersect(cut);
            if (i.IsEmpty)
            {
                result.Add(r);
                continue;
            }
            // 上、下两条整宽,中间左右两块。
            if (i.Y > r.Y)
            {
                result.Add(new(r.X, r.Y, r.Width, i.Y - r.Y));
            }
            if (i.Bottom < r.Bottom)
            {
                result.Add(new(r.X, i.Bottom, r.Width, r.Bottom - i.Bottom));
            }
            if (i.X > r.X)
            {
                result.Add(new(r.X, i.Y, i.X - r.X, i.Height));
            }
            if (i.Right < r.Right)
            {
                result.Add(new(i.Right, i.Y, r.Right - i.Right, i.Height));
            }
        }
        _rects.Clear();
        _rects.AddRange(result);
        return this;
    }

    /// <summary>就地减去另一个区域。</summary>
    public Region Subtract(Region other)
    {
        foreach (XRect r in other._rects)
        {
            Subtract(r);
        }
        return this;
    }

    /// <summary>就地与矩形求交。</summary>
    public Region Intersect(XRect clip)
    {
        for (int i = _rects.Count - 1; i >= 0; i--)
        {
            XRect r = _rects[i].Intersect(clip);
            if (r.IsEmpty)
            {
                _rects.RemoveAt(i);
            }
            else
            {
                _rects[i] = r;
            }
        }
        return this;
    }

    /// <summary>就地与另一个区域求交。</summary>
    public Region Intersect(Region other)
    {
        List<XRect> result = [];
        foreach (XRect a in _rects)
        {
            foreach (XRect b in other._rects)
            {
                XRect i = a.Intersect(b);
                if (!i.IsEmpty)
                {
                    result.Add(i);
                }
            }
        }
        _rects.Clear();
        _rects.AddRange(result);
        return this;
    }

    /// <summary>就地并上一个矩形(先把已有部分挖掉,保持互不重叠)。</summary>
    public Region Union(XRect add)
    {
        if (add.IsEmpty)
        {
            return this;
        }
        Region piece = new(add);
        foreach (XRect r in _rects)
        {
            piece.Subtract(r);
        }
        _rects.AddRange(piece._rects);
        return this;
    }

    public Region Union(Region other)
    {
        foreach (XRect r in other._rects)
        {
            Union(r);
        }
        return this;
    }

    public Region Translate(int dx, int dy)
    {
        for (int i = 0; i < _rects.Count; i++)
        {
            _rects[i] = _rects[i].Offset(dx, dy);
        }
        return this;
    }
}
