# AGENTS.md —— VelaShell.XServer 开发约定

> 给 AI 代理与新加入者的操作约定。**改 `src/VelaShell.XServer/` 之前先读完本文件**,
> 以及仓库根目录的 [`AGENTS.md`](../../AGENTS.md)(全仓通用的约定在那边,这里只写本库特有的)。
> 本库与 `VelaShell.Ssh` 同一套净室规程(第二节)。

---

## 一、这是什么

`VelaShell.XServer` —— 一个**可嵌入、无原生依赖、跨平台**的 X11 服务端库(rootless,软件绘图)。
宿主要在本机显示经 SSH X11 转发过来的远端图形程序,又不想让用户另装 VcXsrv / XQuartz。
2026-09-23 立项,M1(核心协议)、M2(SHAPE / XFIXES / RANDR / RENDER / 剪贴板 / XSETTINGS)与功能完备一轮
(XKB、XInput2、XTEST、SYNC、DAMAGE、Composite、DBE、Present 等十余个扩展与窗口管理器角色)已完成;宿主尚未接入(接入是 M3)。

- **本目录按 MIT 授权**([`LICENSE`](LICENSE) / [`NOTICE.md`](NOTICE.md)),与宿主其余部分的授权不同。
- **架构与原理、里程碑、决策记录**:velashell-docs
  [`zh/xserver/design/architecture.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/xserver/design/architecture.md)
  —— 先读这个。

| 位置 | 内容 |
| --- | --- |
| `src/VelaShell.XServer/` | 库本体(分层见 `VelaShell.XServer.csproj` 的注释) |
| `tests/VelaShell.XServer.Tests/` | 单元测试(内存双工流 + 逐字节的测试客户端)与 `[TestCategory("Interop")]` 真实客户端用例 |
| `scripts/xserver/interop/` | 互操作靶场:`Dockerfile`(x11-apps / xterm / xdpyinfo / xclip / xdotool / x11-xkb-utils)、`run-server.cs`(起服务端、存 PNG、注入输入)、`Run-Client.ps1` |
| `scripts/xserver/bench/` | 吞吐基准 `bench.cs`(进程内经内存管道;改热路径前后各跑一次) |

---

## 二、净室规程(硬纪律)

| # | 纪律 | 具体要求 |
| :-: | --- | --- |
| 1 | **规范优先** | 实现依据只能是 X.Org 发布的 *X Window System Protocol, X Version 11*(含附录 B 编码)、各扩展的协议规范(BIG-REQUESTS、XC-MISC、SHAPE、XFIXES、RANDR、RENDER、Generic Event、XTEST、XINERAMA —— 线格式依据 panoramiXproto 的协议定义、MIT-SCREEN-SAVER、DPMS、X-Resource、SYNC、DAMAGE、Composite、DOUBLE-BUFFER、Present、XKB、XInput 1.5 / 2.2)、ICCCM、EWMH、XSETTINGS、BDF 规范,以及 RENDER 规范引用的 PDF Reference 混合模式公式。**每个协议实现文件头写明它实现的是哪份规范的哪一节** |
| 2 | **不看别人的服务端** | 写实现时不打开任何其它 X 服务端的源码(X.Org / XLibre / yserver / node-x11 / WeirdX / VcXsrv / XQuartz) |
| 3 | **常量照抄规范** | 操作码、事件码、错误码、掩码位、预定义原子、线上布局都是协议事实,不许为了「看起来不一样」去改 |
| 4 | **数据不是代码** | 内置字体是 X.Org `font-misc-misc` 的 BDF(公有领域),以数据文件随库分发,来源写在 `Fonts/Data/README.md` 与 `NOTICE.md`。要更多字形时从上游重新裁剪,不手改 |

---

## 三、工程约定

- 编译设置与 `VelaShell.Ssh` 一致:`TreatWarningsAsErrors`、`latest-recommended` 分析、`EnforceCodeStyleInBuild`、AOT / 裁剪友好(**零反射**)。
- **零运行时依赖。** 绘图是自己的软件光栅化(X 的核心绘图是逐像素精确的语义,抗锯齿的 2D 库给不出同样的像素)。
- **全部可变状态只在执行线程上碰**(`X11Server.Post`)。宿主注入的方法只排工作项;宿主回调经 `DeferredHost` 攒起来,
  在执行线程**放掉 `PixelLock` 之后**按原顺序调用 —— 新增回调一律走它,不许在持锁时直接调宿主(宿主的 UI 线程会在 `CopyPixels` 里等这把锁)。
  唯一跨线程的是顶层像素,由 `PixelLock` 保护。
- **异步优先,不阻塞执行线程**:需要等的东西(XTEST / Present 的延迟、SYNC 的计时器)用 `Task.Delay` 到点后 `Post` 回来;
  连接层有背压(输出积压上限、每客户端未执行请求上限),大尺寸请求先校验再分配、只处理与目标相交的部分。
- 请求处理按领域拆成 `Server/X11Server.*.cs` 的 partial 文件;新请求 / 扩展照这个分法放。
- 协议错误一律 `throw new XProtocolError(...)`,由分派层统一转成错误报文(并经 `XServerOptions.Log` 记下来)。

## 四、测试

```bash
dotnet test tests/VelaShell.XServer.Tests                      # 单元测试,零网络,几百毫秒
docker build -t velashell-xclients scripts/xserver/interop     # 一次性
VELASHELL_XSERVER_INTEROP=1 dotnet test tests/VelaShell.XServer.Tests --filter TestCategory=Interop
```

Interop 用例在条件不满足时早退并写 `[SKIP]`,MSTest 记为通过 —— 看 `[SKIP]` 行才知道跑没跑。
手动排查时用 `scripts/xserver/interop/run-server.cs`:每批损伤后把顶层窗口存成 PNG,协议错误逐条打印,
往输出目录写 `cmd.txt` 可以注入按键 / 点击 / 缩放。

## 五、文档在哪

设计文档在 velashell-docs 的 `zh/xserver/` 与 `en/xserver/`(互为镜像)。**改了行为就同步改那边**,
两个 PR 互相引用、一起合(仓库根 AGENTS.md 第二节)。
