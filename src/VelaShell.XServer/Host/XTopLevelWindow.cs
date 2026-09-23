// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using VelaShell.XServer.Drawing;

namespace VelaShell.XServer.Host;

/// <summary>一个顶层窗口在宿主眼里的样子。属性值由服务端线程更新,宿主只读。</summary>
public sealed class XTopLevelWindow
{
    private readonly object _pixelLock;
    private readonly Func<(uint[] Pixels, int Width, int Height)?> _pixels;

    internal XTopLevelWindow(uint id, object pixelLock, Func<(uint[] Pixels, int Width, int Height)?> pixels)
    {
        Id = id;
        _pixelLock = pixelLock;
        _pixels = pixels;
    }

    /// <summary>窗口 ID(XID)。宿主注入输入、移动 / 缩放 / 关闭时用它指名。</summary>
    public uint Id { get; }

    /// <summary>外框左上角在根窗口坐标里的位置。</summary>
    public int X { get; internal set; }

    /// <summary>外框左上角在根窗口坐标里的位置。</summary>
    public int Y { get; internal set; }

    /// <summary>内区宽。</summary>
    public int Width { get; internal set; }

    /// <summary>内区高。</summary>
    public int Height { get; internal set; }

    /// <summary>标题(<c>_NET_WM_NAME</c> 优先,否则 <c>WM_NAME</c>)。</summary>
    public string Title { get; internal set; } = "";

    /// <summary><c>WM_CLASS</c> 的 class 部分。</summary>
    public string ClassName { get; internal set; } = "";

    /// <summary>override-redirect(菜单、提示框):宿主应画成无装饰、不抢焦点的弹出窗口。</summary>
    public bool OverrideRedirect { get; internal set; }

    /// <summary><c>WM_TRANSIENT_FOR</c> 指向的顶层窗口 ID;没有为 0。</summary>
    public uint TransientFor { get; internal set; }

    /// <summary>客户端支持 <c>WM_DELETE_WINDOW</c>(点关闭时应当礼貌地请它退出,而不是直接断开)。</summary>
    public bool SupportsDeleteWindow { get; internal set; }

    /// <summary>是否映射中。</summary>
    public bool IsMapped { get; internal set; }

    /// <summary>
    /// 窗口形状(SHAPE 扩展的边界形状与内区的交集,内区坐标);null = 普通矩形窗口。
    /// 宿主应当让形状以外的部分透明、且不接收鼠标(xeyes 的两只眼睛、不规则弹层)。
    /// </summary>
    public IReadOnlyList<XRect>? Shape { get; internal set; }

    /// <summary>
    /// 拷贝当前像素(<c>0x00RRGGBB</c>,行优先,宽 × 高)。<paramref name="destination" /> 不够大时只拷能放下的部分。
    /// </summary>
    /// <returns>实际拷贝时的 (宽, 高);窗口已没有缓冲时为 (0, 0)。</returns>
    public (int Width, int Height) CopyPixels(Span<uint> destination)
    {
        lock (_pixelLock)
        {
            if (_pixels() is not { } buffer)
            {
                return (0, 0);
            }
            int count = Math.Min(destination.Length, buffer.Width * buffer.Height);
            buffer.Pixels.AsSpan(0, count).CopyTo(destination);
            return (buffer.Width, buffer.Height);
        }
    }
}
