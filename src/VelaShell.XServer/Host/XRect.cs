// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

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
