# VelaShell.XServer

可嵌入的 X11 服务端(rootless、软件绘图、零原生依赖)。宿主把它的顶层窗口画成自己的原生窗口,
远端经 SSH X11 转发过来的图形程序就显示在本机 —— 不需要用户另装 VcXsrv / XQuartz。

**状态:M2(现代工具包)。** 全部核心请求,加 BIG-REQUESTS、XC-MISC、SHAPE、XFIXES、RANDR(只读)、RENDER;
服务端兼任 XSETTINGS 管理器,CLIPBOARD(可选 PRIMARY)与宿主剪贴板互通。真实的 `xterm`(含 Xft)、`xeyes`、
`xclock`、GTK3 的 `zenity` / `gedit`、Qt5 的 `qt5ct` 画得对、键盘输入走得通、零协议错误。宿主接入是 M3。

```csharp
await using X11Server server = new(new XServerOptions { DisplayNumber = 1 }, host);   // host: IXServerHost
await server.StartAsync();                     // 监听 127.0.0.1:6001;或者 ServeAsync(stream) 直接喂一条双工流
server.PointerButton(windowId, x, y, 1, true);  // 宿主注入输入
server.SetClipboardText(text);                  // 宿主剪贴板 → X;反方向是 IXServerHost.ClipboardChanged
```

| 目录 | 职责 |
| --- | --- |
| `Protocol/` | 常量、字节序感知的请求读取与回复 / 事件 / 错误写出 |
| `Server/` | `X11Server`:执行循环、连接建立与授权、请求分派(按领域拆成 partial 文件) |
| `Windowing/` | 窗口模型 |
| `Drawing/` | 像素缓冲、区域、软件光栅化、RENDER 的合成 / 取样 / 覆盖率 |
| `Resources/` | GC、像素图、颜色表、光标、颜色名、RENDER 的 picture 与字形集 |
| `Fonts/` | BDF 解析、内置 misc-fixed 字体、XLFD 匹配 |
| `Input/` | 键码表、抓取 |
| `Host/` | 面向宿主的接口(`IXServerHost`、`XTopLevelWindow`、`XServerOptions`、`XKeycodes`) |

开发约定(净室规程)见 [`AGENTS.md`](AGENTS.md);架构见
velashell-docs [`zh/xserver/design/architecture.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/xserver/design/architecture.md)。
本目录按 **MIT** 授权([`LICENSE`](LICENSE) / [`NOTICE.md`](NOTICE.md))。
