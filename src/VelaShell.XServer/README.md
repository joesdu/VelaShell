# VelaShell.XServer

可嵌入的 X11 服务端(rootless、软件绘图、零原生依赖)。宿主把它的顶层窗口画成自己的原生窗口,
远端经 SSH X11 转发过来的图形程序就显示在本机 —— 不需要用户另装 VcXsrv / XQuartz。

**状态:功能完备,宿主接入是 M3。** 全部核心请求,加 19 个扩展:

| 类别 | 扩展 |
| --- | --- |
| 基础 | BIG-REQUESTS、XC-MISC、Generic Event |
| 窗口与绘图 | SHAPE、XFIXES、RENDER、DAMAGE、Composite、DOUBLE-BUFFER、Present、SYNC |
| 输入 | XKEYBOARD、XInputExtension 2.2(含 XI 1.x 查询)、XTEST |
| 显示器与电源 | RANDR 1.5(布局由宿主给)、XINERAMA、MIT-SCREEN-SAVER、DPMS |
| 诊断 | X-Resource |

服务端同时兼任**窗口管理器**(EWMH / ICCCM 属性、客户端提示解析、移动 / 缩放 / 最大化 / 关闭请求转交宿主)与
**XSETTINGS 管理器**(DPI、缩放),CLIPBOARD(可选 PRIMARY)与宿主剪贴板互通。监听 TCP 与 Unix 套接字,
也可以 `ServeAsync` 直接喂一条双工流(SSH 的 x11 通道)。真实的 `xterm`(含 Xft)、`xeyes`、`xclock`、
GTK3 的 `zenity` / `gedit`、Qt5 的 `qt5ct`、`xdotool`、`xinput`、`xkbcomp` 画得对、输入走得通、零协议错误。

```csharp
await using X11Server server = new(new XServerOptions { DisplayNumber = 1 }, host);   // host: IXServerHost
await server.StartAsync();                     // 监听 127.0.0.1:6001(Windows 以外还有 /tmp/.X11-unix/X1);或者 ServeAsync(stream)
server.PointerButton(windowId, x, y, 1, true);  // 宿主注入输入
server.SetClipboardText(text);                  // 宿主剪贴板 → X;反方向是 IXServerHost.ClipboardChanged
server.SetDisplayScale(dpi: 192, scale: 2);     // 运行中换 DPI / 缩放;另有 SetScreenLayout、SetKeyboardMapping
```

线程模型:全部协议状态只在一条执行线程上改,宿主调用只排工作项、不阻塞;宿主回调在放掉像素锁之后调用。
每个客户端有输出积压与未执行请求两道上限,慢客户端或恶意客户端拖不垮服务端。

| 目录 | 职责 |
| --- | --- |
| `Protocol/` | 常量、字节序感知的请求读取与回复 / 事件 / 错误写出 |
| `Server/` | `X11Server`:执行循环、连接建立与授权、请求分派(按领域拆成 partial 文件) |
| `Windowing/` | 窗口模型 |
| `Drawing/` | 像素缓冲、区域、软件光栅化、RENDER 的合成 / 取样 / 覆盖率 |
| `Resources/` | GC、像素图、颜色表、光标、颜色名、RENDER 的 picture 与字形集 |
| `Fonts/` | BDF 解析、内置 misc-fixed 字体、XLFD 匹配 |
| `Input/` | 键码表、抓取 |
| `Host/` | 面向宿主的接口(`IXServerHost`、`XTopLevelWindow`、`XServerOptions`、`XMonitor`、窗口管理器请求与枚举、`XKeycodes`) |

开发约定(净室规程)见 [`AGENTS.md`](AGENTS.md);架构见
velashell-docs [`zh/xserver/design/architecture.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/xserver/design/architecture.md)。
本目录按 **MIT** 授权([`LICENSE`](LICENSE) / [`NOTICE.md`](NOTICE.md))。
