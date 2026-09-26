// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>
/// 宿主:把服务端的顶层窗口显示成原生窗口(rootless,架构 §6)。服务端经它通知宿主;
/// 宿主经 <see cref="X11Server" /> 的公开方法把用户输入与窗口管理器的决定交回去。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>全部回调都在服务端的执行线程上调用</b>,宿主自己切到 UI 线程;回调里不要阻塞,
/// 也不要同步等服务端的方法(那些方法只是把工作项排进同一个执行线程,同步等它会死锁)。
/// 回调在执行线程放掉像素锁之后按发生的顺序调用;回调抛的异常记进 <see cref="X11ServerOptions.Log" /> 后吞掉。
/// </para>
/// <para>
/// 像素经 <see cref="XTopLevelWindow.ReadPixels" /> / <see cref="XTopLevelWindow.CopyPixels" /> 读,可以在任意线程上调;
/// 窗口的属性经 <see cref="XTopLevelWindow.Snapshot" /> 读。
/// </para>
/// </remarks>
public interface IX11ServerHost
{
    /// <summary>一个顶层窗口映射了(该创建原生窗口并显示)。</summary>
    void TopLevelMapped(XTopLevelWindow window);

    /// <summary>一个顶层窗口取消映射或销毁了(该隐藏 / 关闭原生窗口)。</summary>
    void TopLevelUnmapped(XTopLevelWindow window);

    /// <summary>
    /// 映射中的顶层窗口的快照换了(<see cref="XTopLevelWindow.Snapshot" />):几何(客户端 ConfigureWindow)、标题、
    /// 窗口管理器提示、形状……<paramref name="changes" /> 说明这次变了哪几组,宿主只需重新应用这几组。
    /// </summary>
    void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes);

    /// <summary>有内容画进了顶层窗口;<paramref name="damage" /> 是顶层内区坐标里的矩形。</summary>
    void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage);

    /// <summary>
    /// 指针所在位置该显示的光标变了(换了窗口,或者同一窗口换了光标)。<paramref name="window" /> 是指针所在的顶层;
    /// 指针不在任何顶层里时为 null。
    /// </summary>
    void CursorChanged(XTopLevelWindow? window, XCursor cursor);

    /// <summary>客户端要求响铃;<paramref name="volume" /> 是按协议从基准音量算出的实际音量,0–100。</summary>
    void BellRequested(int volume);

    /// <summary>
    /// X 客户端复制了文本(占有了 CLIPBOARD;<see cref="X11ServerOptions.SyncPrimary" /> 时也包括 PRIMARY),
    /// 服务端已把内容取了过来。宿主把它写进系统剪贴板;反方向用 <see cref="X11Server.SetClipboardText" />。
    /// </summary>
    void ClipboardChanged(string text);

    /// <summary>
    /// 客户端向窗口管理器提出了请求(拖动 / 缩放、最大化、全屏、激活、关闭、最小化……,见 <see cref="XWindowManagerRequest" /> 的派生类)。
    /// 宿主就是窗口管理器:照办的,改完原生窗口后调服务端对应的方法(如 <see cref="X11Server.SetTopLevelStates" />)把结果告诉客户端。
    /// 默认实现什么也不做 —— 相当于一个拒绝所有请求的窗口管理器。
    /// </summary>
    void WindowManagerRequested(XWindowManagerRequest request)
    {
    }
}
