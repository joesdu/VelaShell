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

    /// <summary>
    /// 一个顶层窗口取消映射或销毁了(该隐藏 / 关闭原生窗口)。服务端收工(<see cref="X11Server.DisposeAsync" />)时不再逐个发它:
    /// 宿主停服时自己收掉所有原生窗口。
    /// </summary>
    void TopLevelUnmapped(XTopLevelWindow window);

    /// <summary>
    /// 映射中的顶层窗口的快照换了(<see cref="XTopLevelWindow.Snapshot" />):几何(客户端 ConfigureWindow)、标题、
    /// 窗口管理器提示、形状……<paramref name="changes" /> 说明这次变了哪几组,宿主只需重新应用这几组。
    /// </summary>
    void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes);

    /// <summary>有内容画进了顶层窗口;<paramref name="damage" /> 是顶层内区坐标里的矩形。</summary>
    void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage);

    /// <summary>
    /// 宿主改尺寸之后客户端重画完了(<see cref="XTopLevelWindow.AwaitingRedraw" /> 刚变回假;客户端迟迟不画完时服务端到点也报):
    /// 宿主把这期间攒着没显示的像素一次显示出来。默认实现什么也不做。
    /// </summary>
    void TopLevelRedrawn(XTopLevelWindow window)
    {
    }

    /// <summary>
    /// 有没有客户端在等帧(Present 的 NotifyMSC、排队的 PresentPixmap):要的时候宿主每画一帧调一次 <see cref="X11Server.NotifyHostFrame" />,
    /// 帧号就跟着宿主真实的刷新走;不要了就停(不必一直逐帧回调)。默认实现什么也不做,服务端按 60 Hz 推算。
    /// </summary>
    void FrameClockWanted(bool wanted)
    {
    }

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
    /// X 客户端复制了(同 <see cref="ClipboardChanged" />),带上它给得出的全部格式:文本、HTML、PNG 图片(<see cref="XClipboardContent" />)。
    /// 宿主把它们一起写进系统剪贴板;反方向用 <see cref="X11Server.SetClipboard" />。服务端只调这一个;默认实现有文本时转给
    /// <see cref="ClipboardChanged" />(只认文本的宿主不必实现它)。
    /// </summary>
    void ClipboardContentChanged(XClipboardContent content)
    {
        if (content.Text is { } text)
        {
            ClipboardChanged(text);
        }
    }

    /// <summary>
    /// 客户端向窗口管理器提出了请求(拖动 / 缩放、最大化、全屏、激活、关闭、最小化……,见 <see cref="XWindowManagerRequest" /> 的派生类)。
    /// 宿主就是窗口管理器:照办的,改完原生窗口后调服务端对应的方法(如 <see cref="X11Server.SetTopLevelStates" />)把结果告诉客户端。
    /// 默认实现什么也不做 —— 相当于一个拒绝所有请求的窗口管理器。
    /// </summary>
    void WindowManagerRequested(XWindowManagerRequest request)
    {
    }

    /// <summary>
    /// 一个客户端 GrabServer 抓着太久(10 秒起,之后每隔一分钟再报一次),别的客户端的请求都在等它 —— 所有会话的 X 程序都冻着。
    /// 宿主据此提示用户:断开它(<see cref="X11Server.DisconnectClient(int)" />)或解除抓取(<see cref="X11Server.BreakGrabs" />)。
    /// 没人在等时不报(抓着不妨碍谁)。默认实现什么也不做。
    /// </summary>
    void ServerGrabStalled(XServerGrabStall stall)
    {
    }

    /// <summary>
    /// 抓着指针的 X 客户端挪了指针(WarpPointer / XIWarpPointer):连续拖拽的 3D 视图把指针拉回中间、CAD 旋转、远端游戏的视角、
    /// virt-manager 控制台的相对鼠标。(<paramref name="rootX" />, <paramref name="rootY" />) 是根坐标(与 <see cref="X11Server.SetScreenLayout" /> 的布局同一套)。
    /// 宿主在用户此刻正用 X 窗口时把系统光标挪到对应的位置,否则服务端认为的指针与真实光标就分了叉,下一次移动跳回去。
    /// 没抓着指针的客户端挪指针不报(远端程序不能随意挪用户的鼠标)。默认实现什么也不做。
    /// </summary>
    void PointerWarped(int rootX, int rootY)
    {
    }

    /// <summary>
    /// 一个 X 程序的托盘图标停靠进来了(开着 <see cref="X11ServerOptions.SystemTray" /> 时):<paramref name="icon" /> 是服务端的嵌入窗口,
    /// 不当普通顶层窗口显示 —— 宿主把它的像素(<see cref="XTopLevelWindow.ReadPixels" />,随 <see cref="TopLevelDamaged" /> 更新)
    /// 画成自己的托盘图标,点击时往它里面注入指针(<see cref="X11Server.InjectPointerButton" />,坐标是图标的内区)。
    /// <paramref name="title" /> 是图标的名字(<c>_NET_WM_NAME</c>,退到 WM_NAME、WM_CLASS),可以当悬停提示。默认实现什么也不做。
    /// </summary>
    void SystemTrayIconAdded(XTopLevelWindow icon, string title)
    {
    }

    /// <summary>托盘图标没了(程序退出、图标窗口销毁或被挪走):宿主收掉对应的托盘图标。默认实现什么也不做。</summary>
    void SystemTrayIconRemoved(XTopLevelWindow icon)
    {
    }

    /// <summary>
    /// 带 confine-to 的指针抓取开始或结束了:<paramref name="area" /> 是那个窗口的内区(根坐标,夹在根窗口里),null 表示解除。
    /// 能限制系统光标的宿主把光标关在这块里,直到解除或用户离开 X 窗口。默认实现什么也不做。
    /// </summary>
    void PointerConfinementChanged(XRect? area)
    {
    }

    /// <summary>
    /// X 程序挂起 / 恢复了屏保(MIT-SCREEN-SAVER 的 Suspend:视频播放器全屏放片子时)。<paramref name="suspended" /> 为真时宿主应当抑制
    /// 本机的屏保与关显示器,为假时恢复 —— 远端程序防的是远端的屏保,用户眼前的是本机的。默认实现什么也不做。
    /// </summary>
    void ScreenSaverSuspensionChanged(bool suspended)
    {
    }

    /// <summary>
    /// X 程序重置了屏保计时(ForceScreenSaver(Reset),播放器常每隔几秒发一次;服务端至多每 5 秒报一次)。宿主可以据此重置本机的空闲计时。
    /// 默认实现什么也不做。
    /// </summary>
    void ScreenSaverReset()
    {
    }
}
