# VelaShell.XServer

可嵌入的 X11 服务端(rootless、软件绘图、零原生依赖)。宿主把它的顶层窗口画成自己的原生窗口,
远端经 SSH X11 转发过来的图形程序就显示在本机 —— 不需要用户另装 VcXsrv / XQuartz。

**状态:功能完备,已接入宿主(M3),M4 完成。** 全部核心请求,加 21 个扩展:

| 类别 | 扩展 |
| --- | --- |
| 基础 | BIG-REQUESTS、XC-MISC、Generic Event |
| 窗口与绘图 | SHAPE、XFIXES、RENDER、DAMAGE、Composite、DOUBLE-BUFFER、Present、SYNC、MIT-SHM 1.1(仅 Linux,仅经 Unix 套接字连进来的客户端可见) |
| OpenGL | GLX 1.4:直接渲染(Mesa 在客户端软件渲染)只做登记;间接渲染由 `Gl/` 的软件 GL 执行(固定功能管线的子集,报 GL 1.1) |
| 输入 | XKEYBOARD(含 SetMap)、XInputExtension 2.2(含 XI 1.x 查询、XIChangeHierarchy)、XTEST;同步抓取 |
| 显示器与电源 | RANDR 1.5(布局由宿主给)、XINERAMA、MIT-SCREEN-SAVER、DPMS |
| 诊断 | X-Resource |

服务端同时兼任**窗口管理器**(EWMH / ICCCM 属性、客户端提示解析、移动 / 缩放 / 最大化 / 关闭请求转交宿主)与
**XSETTINGS 管理器**(DPI、缩放),CLIPBOARD(可选 PRIMARY)与宿主剪贴板互通。监听 TCP 与 Unix 套接字,
也可以 `ServeAsync` 直接喂一条双工流(SSH 的 x11 通道)。真实的 `xterm`(含 Xft)、`xeyes`、`xclock`、
GTK3 的 `zenity` / `gedit`、Qt5 的 `qt5ct`、`xdotool`、`xinput`、`xkbcomp`、`glxinfo` / `glxgears`(直接与间接两条路径)
画得对、输入走得通、零协议错误。

```csharp
using VelaShell.XServer;   // 公开类型全在这一个命名空间

await using X11Server server = new(new X11ServerOptions { DisplayNumber = 1 }, host);   // host: IX11ServerHost
await server.StartAsync();       // 监听 127.0.0.1:6001(Windows 以外还有 /tmp/.X11-unix/X1);server.Display 给出 DISPLAY
// 或者不监听,直接喂一条双工流:await server.ServeAsync(stream, isLocal: true);

// 宿主回调里拿到的 XTopLevelWindow 就是之后指名窗口的句柄;属性读快照(整份替换,先取到局部变量)。
XTopLevelSnapshot s = window.Snapshot;
server.InjectPointerButton(window, x, y, button: 1, pressed: true);   // Inject*:合成的用户输入
server.MoveTopLevel(window, s.X + 10, s.Y);                           // *TopLevel:宿主作为窗口管理器的动作
server.SetKeymap(new XKeymap("de", 6) { AltGr = true }.Map(XKeycodes.Q, 'q', 'Q', 'q', 'Q', '@', '@'));   // Set*:运行中换配置
server.SetClipboardText(text);   // 宿主剪贴板 → X;反方向是 IX11ServerHost.ClipboardChanged
```

线程模型:全部协议状态只在一条执行线程上改;宿主调用的方法当场校验参数、只排工作项、不阻塞;宿主回调在放掉像素锁之后按顺序调用。
窗口的属性是不可变快照(`XTopLevelSnapshot`),`TopLevelChanged` 说明变了哪几组(`XTopLevelChanges`)。
每个客户端有输出积压与未执行请求两道上限,慢客户端或恶意客户端拖不垮服务端。

| 目录 | 职责 |
| --- | --- |
| `Host/` | 全部公开类型(根命名空间 `VelaShell.XServer`):`IX11ServerHost`、`X11ServerOptions`、`XTopLevelWindow` 与 `XTopLevelSnapshot`、`XCursor`、`XKeymap`、`XMonitor`、窗口管理器请求与枚举、`XKeycodes`、`XRect` |
| `Server/` | `X11Server`:公开成员全在 `X11Server.cs`;执行循环、连接建立与授权、请求分派、扩展注册表(编号与清理钩子),请求处理按领域 / 扩展拆成 partial 文件;自成一体的 GLX 是单独的 `GlxExtension` 类 |
| `Protocol/` | 常量、字节序感知的请求读取与回复 / 事件 / 错误写出 |
| `Windowing/` | 窗口模型 |
| `Drawing/` | 像素缓冲、区域、软件光栅化、RENDER 的合成 / 取样 / 覆盖率 |
| `Gl/` | GLX 间接渲染的软件 GL:渲染命令解码、显示列表、变换 / 光照 / 裁剪、三角形 / 线 / 点光栅化、纹理、逐片元操作 |
| `Resources/` | GC、像素图、颜色表、光标、字体、颜色名,以及各扩展的资源(RENDER、SYNC、DAMAGE、XFIXES、Present、MIT-SHM、GLX) |
| `Fonts/` | BDF 解析、内置 misc-fixed 字体、XLFD 匹配 |
| `Input/` | 键码表、抓取的数据结构 |

开发约定(净室规程)见 [`AGENTS.md`](AGENTS.md);架构见
velashell-docs [`zh/xserver/design/architecture.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/xserver/design/architecture.md)。
本目录按 **MIT** 授权([`LICENSE`](LICENSE) / [`NOTICE.md`](NOTICE.md))。
