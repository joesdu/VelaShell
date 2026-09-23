// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using VelaShell.XServer.Drawing;

namespace VelaShell.XServer.Host;

/// <summary>
/// 宿主:把服务端的顶层窗口显示成原生窗口(rootless,架构 §6)。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>全部回调都在服务端的执行线程上调用</b>,宿主自己切到 UI 线程;回调里不要阻塞,
/// 也不要同步调回服务端的注入方法(那些方法只是把工作项排进同一个执行线程,同步等它会死锁)。
/// </para>
/// <para>
/// 像素通过 <see cref="XTopLevelWindow.CopyPixels" /> 读,它与绘图共用一把锁,可以在任意线程上调。
/// </para>
/// </remarks>
public interface IXServerHost
{
    /// <summary>一个顶层窗口映射了(该创建原生窗口并显示)。</summary>
    void TopLevelMapped(XTopLevelWindow window);

    /// <summary>一个顶层窗口取消映射或销毁了(该隐藏 / 关闭原生窗口)。</summary>
    void TopLevelUnmapped(XTopLevelWindow window);

    /// <summary>几何(客户端 ConfigureWindow)或标题、类名等属性变了。</summary>
    void TopLevelChanged(XTopLevelWindow window);

    /// <summary>有内容画进了顶层窗口;<paramref name="damage" /> 是顶层内区坐标里的矩形。</summary>
    void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage);

    /// <summary>
    /// 指针所在位置该显示的光标变了。<paramref name="cursorGlyph" /> 是 cursor 字体的字形号
    /// (68 = left_ptr、152 = xterm、…);−1 表示默认箭头或位图光标,−2 表示隐藏光标(XFIXES HideCursor)。
    /// </summary>
    void CursorChanged(XTopLevelWindow? window, int cursorGlyph);

    /// <summary>响铃。</summary>
    void Bell(int percent);

    /// <summary>
    /// X 客户端复制了文本(占有了 CLIPBOARD;<see cref="XServerOptions.SyncPrimary" /> 时也包括 PRIMARY),
    /// 服务端已把内容取了过来。宿主把它写进系统剪贴板;反方向用 <see cref="Server.X11Server.SetClipboardText" />。
    /// </summary>
    void ClipboardChanged(string text);

    /// <summary>
    /// 客户端向窗口管理器提出了请求(拖动 / 缩放、最大化、全屏、激活、关闭、最小化……,见 <see cref="XWindowManagerRequest" /> 的派生类)。
    /// 宿主就是窗口管理器:照办的,改完原生窗口后调服务端对应的方法(如 <c>SetTopLevelStates</c>)把结果告诉客户端。
    /// 默认实现什么也不做 —— 相当于一个拒绝所有请求的窗口管理器。
    /// </summary>
    void WindowManagerRequest(XWindowManagerRequest request)
    {
    }
}
