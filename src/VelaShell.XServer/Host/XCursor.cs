// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>
/// 指针该显示的光标(<see cref="IX11ServerHost.CursorChanged" />)。宿主能显示自定义光标时优先用 <see cref="Image" />,
/// 否则(或 <see cref="Image" /> 为 null 时)按 <see cref="Shape" /> 选一个系统光标。
/// </summary>
/// <param name="Shape">
/// 语义上的形状:cursor 字体的光标按字形推出;位图 / ARGB 光标按客户端经 XFIXES SetCursorName 起的名字推出,
/// 推不出来是 <see cref="XCursorShape.Arrow" />。
/// </param>
/// <param name="Image">光标图像;cursor 字体的光标、隐藏与默认光标为 null。</param>
public sealed record XCursor(XCursorShape Shape, XCursorImage? Image = null)
{
    /// <summary>默认箭头(窗口没设光标)。</summary>
    public static XCursor Default { get; } = new(XCursorShape.Arrow);

    /// <summary>隐藏光标(XFIXES HideCursor)。</summary>
    public static XCursor Hidden { get; } = new(XCursorShape.Hidden);
}

/// <summary>光标图像:预乘 alpha 的 <c>0xAARRGGBB</c>,行优先,<paramref name="Width" /> × <paramref name="Height" /> 个。</summary>
/// <param name="Width">宽,像素。</param>
/// <param name="Height">高,像素。</param>
/// <param name="HotspotX">热点(指针实际指着的那个像素)相对左上角的位置。</param>
/// <param name="HotspotY">热点相对左上角的位置。</param>
/// <param name="Pixels">像素。</param>
public sealed record XCursorImage(int Width, int Height, int HotspotX, int HotspotY, uint[] Pixels);

/// <summary>光标的语义形状(与 CSS 的 cursor 关键字大致一一对应)。</summary>
public enum XCursorShape
{
    /// <summary>默认箭头。</summary>
    Arrow,
    /// <summary>不显示光标。</summary>
    Hidden,
    /// <summary>文本插入(I 形)。</summary>
    Text,
    /// <summary>忙,不能操作(沙漏 / 手表)。</summary>
    Wait,
    /// <summary>忙,但仍可操作(箭头带沙漏)。</summary>
    Progress,
    /// <summary>帮助(箭头带问号)。</summary>
    Help,
    /// <summary>链接(手指)。</summary>
    Hand,
    /// <summary>十字准星。</summary>
    Crosshair,
    /// <summary>移动(四向箭头)。</summary>
    Move,
    /// <summary>不允许(圆圈加斜杠)。</summary>
    NotAllowed,
    /// <summary>从上边缩放。</summary>
    ResizeNorth,
    /// <summary>从下边缩放。</summary>
    ResizeSouth,
    /// <summary>从右边缩放。</summary>
    ResizeEast,
    /// <summary>从左边缩放。</summary>
    ResizeWest,
    /// <summary>从左上角缩放。</summary>
    ResizeNorthWest,
    /// <summary>从右上角缩放。</summary>
    ResizeNorthEast,
    /// <summary>从左下角缩放。</summary>
    ResizeSouthWest,
    /// <summary>从右下角缩放。</summary>
    ResizeSouthEast,
    /// <summary>上下缩放(双向箭头)。</summary>
    ResizeNorthSouth,
    /// <summary>左右缩放(双向箭头)。</summary>
    ResizeEastWest,
    /// <summary>拖放:复制。</summary>
    DragCopy,
    /// <summary>拖放:链接。</summary>
    DragLink,
}
