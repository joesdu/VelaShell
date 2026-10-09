# VelaShell.XServer 审查与改进计划

> **草案 · 2026-10-06 · 基于 `dev` @ `78298089`**（之后到 `6b10c5e9` 的提交只动了 SFTP，X 服务端相关目录没有变化）
>
> 这是一份**待拍板的计划**，不是已经发生的事。拍板之后：要做的条目挪进 `feature-plan.md` 或直接开 PR，
> 做完照例在 `plan.md` 记一节；改到协议行为、上限或宿主接口的，同时改 velashell-docs 的
> `{zh,en}/xserver/design/architecture.md`（根 AGENTS.md 第二节）。这份文件随之删掉，或者缩成一张索引。

---

## 〇、怎么来的、怎么读

**方法**：把库与宿主集成分成七块 —— 连接与授权（CN）、窗口 / 属性 / 选区 / 窗口管理器 / 字体（WN）、
绘图与 RENDER / 合成 / 同步（DR）、输入 / 键盘 / 显示器（IN）、GLX 与软件 GL（GL）、公开面与宿主（API），
外加一块「真实应用兼容性」（CP：按 Java、GTK、Qt、浏览器、EDA、3D、桌面会话、托盘、中日韩输入逐类推演会用到的协议面，再回代码核对）。
每块由一个只读的审查会话通读代码，对照 X11 核心协议、各扩展规范、ICCCM、EWMH、Khronos 的 GLX / GL 规范，
以及 velashell-docs 的 `xserver/design/architecture.md`。DR、GL、API 三块另在 scratchpad 里写了探针（反射加载编好的 DLL、
Avalonia Headless、对 Avalonia 12.1.3 的 IL 扫描）实测复现，探针不入库。主会话再把严重度高、中的条目回到代码逐条复核。
全程守 `src/VelaShell.XServer/AGENTS.md` 的净室规程：没有打开任何其它 X 服务端、窗口管理器或 OpenGL 实现的源码。

**规模**：库 98 个文件、约 2.76 万行；宿主侧（Avalonia 宿主、Infrastructure、Core）约 4.3 千行；
`VelaShell.XServer.Tests` 216 例（214 通过、2 例只在 Linux 上跑而跳过）。2026-09-25 的上一轮审查登记的 31 项已在
`plan.md` §124 修完 —— 这一轮**不重复**那些，只报它们修得不完整的地方。去重后 184 条需要动手的问题（🔴 9、🟠 43、🟡 132；CP 一节的 31 条多数指向别处的条目或功能，不重复计），外加 30 条将来可以支持的功能。

**标记**

| 标记 | 含义 |
| --- | --- |
| `CN` `WN` `DR` `IN` `GL` `API` `CP` | 领域：连接与授权 / 窗口与属性 / 绘图 / 输入 / GLX / 公开面与宿主 / 真实应用兼容性 |
| `X-` | 横切问题：几个领域的同一类根因，单独立项 |
| `-E` `-S` `-M` `-D` `-P` | 类别：错误 / 安全 / 遗漏与规格漂移 / 设计与 API / 性能；`API-H` 是宿主侧的问题 |
| 🔴 🟠 🟡 | 严重度：高 / 中 / 低 |
| ✅ | 主会话已回到代码复核，属实 |
| 🧪 | 审查会话写探针实测复现过（主会话没有重跑） |
| 🔎 | 审查会话读代码确认，主会话未逐行复核 |
| ❓ | 待确认：需要实测或对照规范原文，条目里写了怎么确认 |

几条在不同领域里被各自发现的同一问题已经合并，保留一个编号，别处写「同 XX」。

**总体判断**

- **底座是稳的。** 连接建立、回复 / 事件 / 错误、GenericEvent 的线格式逐字节对过；背压、BIG-REQUESTS、MIT-SHM 的权限核对、
  cookie 的常数时间比较、`.Xauthority` 的原子替换都对；核心窗口与属性请求的错误条件、XKB / XI2 / RANDR 的回复布局、
  RENDER 的 Porter-Duff 与 PDF 混合公式、GL 的光照与逐片元管线也逐项核对过。托管代码里越界只会变成托管异常、由分派层转成
  BadImplementation —— **没有找到内存越界读写**。§124 修过的 31 项复核都还有效。
- **问题集中在四类：**
  1. **嵌入式特有的「代价无界」**（第一批）。X 的信任模型里，受信客户端本来就能读写一切 X 内容；但这里服务端与终端、
     全部 SSH 会话在**同一个进程**里，执行线程执行一条请求期间一直持着像素锁，宿主 UI 线程在 `ReadPixels` 里**不限时**等这把锁。
     于是一个已授权的客户端 —— 任何一台开了 X11 转发的远端机，或者一个失控的程序 —— 用几十字节的请求就能让整个 VelaShell
     冻住几分钟到几小时（核心绘图不先裁剪、宽线圆帽、渐变色标、GLX 显示列表、DrawPixels），或者分配几 GB 直到 OOM
     （AddGlyphs 的 int 溢出、没有总量配额）。§124 只堵了多边形、区域、GLX 列表嵌套这几条路，同类的还有十几条。
  2. **rootless 接缝没对齐**（第二批）。X 端的堆叠次序、焦点、锁定键、根原点、图标化状态都不跟宿主同步。
     最直接的后果是**两个 X 窗口一重叠，点击就可能送到看不见的那个**（WN-E1），每个多窗口用户都会碰到。
  3. **一个受信显示的放大面**（第三批）。所有 SSH 会话共享一个显示（architecture §7 已提醒），而剪贴板默认双向自动同步（含 PRIMARY）、
     XTEST 与 XI2 原始按键没有开关、`_NET_ACTIVE_WINDOW` 不防焦点窃取、内置引擎下「非受信（-X）」根本开不起来 ——
     等于没有任何一档能把一台不太信任的远端机隔开。另有几处本机其他用户能发起的拒绝服务与 cookie 劫持（CN-S1～S4）。
  4. **兼容性缺口**（第四批）。Java 程序的窗口管理器判定、核心字体只有 5 个 misc-fixed、没有 `GLX_ARB_create_context`、
     远端没有输入法框架时完全不能输入中文。
- **宿主正在踩的**：WN-E1（点错窗口）、API-H1（关窗级联留下幽灵窗口，关弹层可能断开整个程序）、API-H3（Linux 上拖边框缩放不回报）、
  IN-E6（NumLock 不同步，Windows 上小键盘默认打出方向键）、IN-E7（插拔左侧显示器后坐标整体错位）、IN-M1（日文键盘打不出下划线）、
  WN-E3（复制源退出后剪贴板就空了）。
- **宿主有几处绕路**（第八节「宿主的绕路说明库缺什么」），每一处都对应库缺的一样东西。

---

## 一、建议的处理批次

每批内部按顺序做；**一条一个 PR**，行为改动不和重构混在一起。改了协议行为或上限的，同步改 architecture.md 并补用例（先撤修复确认用例会红）。

### 第一批：一个客户端就能冻住或打垮整个 VelaShell 的

先做两条横切的「兜底」，再逐个堵具体的路 —— 兜底做好之前，每堵一条都只是少一条。

| 条目 | 一句话 | 严重度 |
| --- | --- | :-: |
| [X-1](#x-1) | 执行一条请求期间一直持像素锁，宿主 UI 线程不限时等锁；没有单请求的工作量预算 | 🔴 ✅ |
| [X-2](#x-2) | 资源只有单件上限、没有每客户端 / 全局总量：像素图、属性、字形、GLX 表面与纹理都能叠加到 OOM | 🔴 ✅ |
| [DR-S3](#dr-s3) | AddGlyphs 的长度校验 int 溢出：36 字节的请求触发一次 4 GB 分配 | 🔴 ✅ 🧪 |
| [DR-S1](#dr-s1) | 核心绘图（矩形、细线、细弧）不先与裁剪求交，代价随坐标范围线性增长 | 🔴 🧪 |
| [DR-S2](#dr-s2) | 宽线圆帽 / 圆接头的顶点数随线宽线性增长，一个 4 KB 的 PolyLine 要约 12 GB | 🔴 🧪 |
| [DR-S4](#dr-s4) | 渐变按色标线性查找，一条 36 字节的 Composite 可以跑几分钟 | 🔴 ✅ 🧪 |
| [GL-S1](#gl-s1) | 显示列表的执行预算按「条数」不按工作量：一条 CallLists 可以把执行线程卡上几小时 | 🔴 ✅ 🧪 |
| [GL-S2](#gl-s2) | DrawPixels 在 ROW_LENGTH < width 加极小 PixelZoom 时逐个解码全部源像素 | 🔴 🧪 |
| [DR-S5](#dr-s5) | CompositeGlyphs 的遮罩按「字形外接 ∩ 可绘对象」分配，远距两个字形就是 1 GB | 🟠 🧪 |
| [DR-S7](#dr-s7) | SYNC 报警器求值 O(n²)，每建一个都整轮求值 | 🟠 🧪 |
| [IN-S1](#in-s1) | XIPassiveGrabDevice 无界增长、O(n²)、不查跨客户端冲突 | 🟠 ✅ |
| [WN-S5](#wn-s5) + [WN-P1](#wn-p1) | 快照每次几何变化都整份解码 32 MB 的标题；属性追加每次整份复制 | 🟠 ✅ / 🔎 |
| [IN-S4](#in-s4) + [WN-S8](#wn-s8) | Bell 与宿主回调不合并、不节流，任何客户端都能把宿主 UI 线程灌满 | 🟠 🔎 |
| [WN-S6](#wn-s6) / [IN-S2](#in-s2) | GetCursorName 绕过原子上限；XI 设备属性没有上限、断开不释放 | 🟠 ✅ / 🔎 |
| [CN-P1](#cn-p1) | 放行暂存请求（Ungrab、SYNC Await、XTEST）时一个工作项里同步执行全部 | 🟡 🔎 |

### 第二批：每个用户都会碰到的 rootless 接缝与宿主问题

| 条目 | 一句话 | 严重度 |
| --- | --- | :-: |
| [WN-E1](#wn-e1) | 指针按 X 的堆叠次序命中，不按宿主的 z 序：两个窗口重叠时点到看不见的那个 | 🔴 ✅ |
| [API-H1](#api-h1) + [API-H2](#api-h2) | Avalonia 的 owner 级联关闭：父窗口变幽灵；关弹层被当成关闭按钮，整个 X 程序被断开；拦住系统注销 | 🟠 ✅ 🧪 |
| [API-H3](#api-h3) | Linux 上用户拖边框缩放，X 客户端收不到新尺寸 | 🟠 ✅ 🧪 |
| [IN-E6](#in-e6) | CapsLock / NumLock 不同步：Windows 上开着 NumLock，小键盘在 X 里是方向键 | 🟠 ✅ |
| [IN-E7](#in-e7) | 插拔左侧 / 上方显示器后，已有窗口的 X 坐标整体错位 | 🟠 ✅ |
| [IN-M1](#in-m1) | JIS / ABNT2 的 Ro、Yen 键没有映射：日文键盘打不出 `_`、`\`、`\|` | 🟠 ✅ |
| [WN-E3](#wn-e3) | X 端复制源退出后，其它 X 程序再也粘贴不到这份内容 | 🟠 ✅ |
| [WN-E2](#wn-e2) | 最小化之后客户端用 MapWindow 还原无效（Tk、Emacs、xdotool） | 🟠 🔎 |
| [WN-M1](#wn-m1) | 颜色库缺 rgb.txt 的编号变体（red3、blue2…）：xterm 的 ANSI 调色板、Emacs 高亮回 BadName | 🟠 ✅ |
| [DR-E1](#dr-e1) | 宽线 / 多边形 / 弧在 (x+0.5, y+0.5) 采样，核心协议规定整数坐标就是像素中心 | 🟠 ✅ 🧪 |
| [DR-E2](#dr-e2) | 子窗口伸出顶层缓冲时 GetImage / CopyArea / RENDER 源回 BadImplementation 或读到折行像素 | 🟠 🧪 |
| [GL-E2](#gl-e2) | 绑定的纹理名已不存在时 TexImage 抛 NullReferenceException（合法的 GL 序列） | 🟠 🧪 |
| [GL-E1](#gl-e1) | GLX 像素图的表面在 FreePixmap 之后不释放 | 🟠 🔎 |
| [IN-E3](#in-e3) | SetInputFocus 不看时间戳，X 焦点与宿主的活动窗口会分叉（键入进了另一个窗口） | 🟠 🔎 |
| [IN-E2](#in-e2) / [IN-E4](#in-e4) / [IN-E5](#in-e5) | 隐式抓取不发 Ungrab crossing；XISelectEvents 不按设备存；Warp 也产生 RawMotion | 🟠 🔎 |
| [IN-D1](#in-d1) + [API-M2](#api-m2) | 抓取卡住、程序无响应、设备被 float 时宿主没有任何恢复手段 | 🟠 🔎 |

### 第三批：安全加固（多会话共享一个受信显示、本机其他用户）

| 条目 | 一句话 | 严重度 |
| --- | --- | :-: |
| [X-3](#x-3) | 没有任何隔离档：内置引擎下 -X 必然失败，所有会话互相能看、能注入、能读剪贴板 | 🟠 ✅ |
| [API-H5](#api-h5) | 剪贴板默认双向自动同步，PRIMARY 也默认开（与库的默认相反） | 🟠 ✅ |
| [WN-S4](#wn-s4) | `_NET_ACTIVE_WINDOW` 不防焦点窃取，X 窗口能抢走 VelaShell 自己的前台（含输入 sudo 口令时） | 🟠 🔎 |
| [IN-S3](#in-s3) | XTEST、XI2 原始按键、XIChangeHierarchy 对经 SSH 来的连接没有开关 | 🟠 ✅ |
| [WN-S2](#wn-s2) | SetSelectionOwner 的时间戳规则只在有属主时检查，可以把 CLIPBOARD 锁死 | 🟠 ✅ |
| [WN-S3](#wn-s3) | 没人占住根窗口的 SubstructureRedirect，远端误跑一个窗口管理器就截走所有会话的窗口；save-set 不生效 | 🟠 ✅ |
| [WN-S7](#wn-s7) | 服务端自有窗口（0x43）可以经 Reparent + Destroy 绕过保护，全显示的剪贴板与 XSETTINGS 失效 | 🟠 ✅ |
| [WN-E4](#wn-e4) | `_NET_MOVERESIZE_WINDOW` 不夹取 32 位几何，任何客户端都能弄坏任何顶层 | 🟠 ✅ |
| [CN-S1](#cn-s1) + [CN-E1](#cn-e1) | 连接建立阶段没有并发上限、先按客户端给的长度分配；接收循环遇到任何 SocketException 就永久退出 | 🟠 ✅ |
| [CN-S2](#cn-s2) | Unix 套接字的「有人在听吗」用阻塞 connect：本机任何用户能让内置服务端永远启动不了 | 🟠 ✅ |
| [CN-S3](#cn-s3) | 抽象命名空间的名字被抢占时只记日志：用 `:N` 连的本机客户端会把 cookie 交给攻击者 | 🟠 ✅ |
| [CN-S4](#cn-s4) | Retain 模式的客户端能占满 255 个编号，之后显示拒绝一切新连接 | 🟠 🔎 |
| CN-S5～S10 / WN-S9～S11 / DR-M2 | 低危的纵深防御，见各节 | 🟡 |

### 第四批：兼容性 —— 让更多真实程序能用

[CP-9](#cp-9)（Java 的窗口管理器判定，**先实测**，与 WN-S3 一起改）、[CP-16](#cp-16)（核心字体）、[GL-M1](#gl-m1)（`GLX_ARB_create_context`）、
[WN-M2](#wn-m2) + [API-M3](#api-m3)（ICCCM 提示解析全：initial_state、USPosition、win_gravity、base size）、[CP-24](#cp-24)（桌面类窗口盖住整屏）、
[WN-E14](#wn-e14)（换 DPI 冲掉 xrdb 资源）、[WN-M3](#wn-m3)（工作区）、[IN-E8](#in-e8)（自动重复与 DetectableAutoRepeat）、
[DR-M1](#dr-m1)（虚线、Miter / Bevel 接头）、[IN-E16](#in-e16)（左手鼠标映射等核心请求杂项）、[IN-E18](#in-e18)（RANDR 的 BadAccess 让程序退出）。

### 第五批：规格细节、文档漂移、诊断与测试补强

各节的 🟡，第十一节的测试缺口，以及 architecture.md 需要补写的几处（IN-M4、GL-M3、API-E3、X-2 的上限粒度）。

### 单独开 PR 的纯重构（不与上面混）

- CN-D4：两份 `.Xauthority` 解析器（Infrastructure 与 SSH 库）规则不同，合成一处。
- WN-D1 / API-D5：公开记录里可变的 `uint[]`（`XCursorImage.Pixels`、`XWindowIcon.Pixels`）改 `ReadOnlyMemory<uint>`。
- CN-D2（死字段 `GenericEventsEnabled`）、CN-D3（`SameHost` / `PeerUid` 在连接线程上写）、CN-D5（`Take` 的溢出比较）。
- 各领域的 `-P` 小项（GL-P1～P5、DR-P1～P7、IN-P1、WN-P2～P3），先补基准场景再动。

---

## 二、横切问题（X）

<a id="x-1"></a>

### X-1 执行线程单项无界 + 宿主不限时等像素锁 · 🔴 ✅

- **位置**：`Server/X11Server.WorkLoop.cs:152-165`（4 ms 预算只在两个工作项**之间**检查，一条请求从头到尾持锁）；
  `Server/PixelGate.cs:34-48`（`EnterHost` 是不限时的 `Monitor.Enter`）；宿主在 UI 线程的帧回调里调 `ReadPixels`（`Views/XServer/XNativeWindow.cs:688`）。
- **问题**：只要有一条请求执行得久，宿主 UI 线程下一帧就卡在这把锁上 —— 终端、所有 SSH 会话的界面一起冻住。
  具体的路见 DR-S1、DR-S2、DR-S4、DR-S5、DR-S7、GL-S1、GL-S2、IN-S1、WN-S5、WN-P1、CN-P1；它们是「这一条路很贵」，X-1 是「贵的时候整个宿主陪着」。
  合法但昂贵的请求（整窗「渐变 + 变换 + 双线性」合成、几十万个梯形）在大窗口上也是秒级。
- **修法**（两头都要）：
  1. **库**：单请求工作量预算 —— 绘图按「裁剪后的像素数」、多边形按边数、GL 按片元 / 顶点 / 像素扣，超了回 BadAlloc（GL 记 OUT_OF_MEMORY）；
     放回暂存请求走「就绪队列」按预算分批（CN-P1）。在 architecture §5 写明这条约束。
  2. **宿主**：`ReadPixels` 加一个带超时的重载（`TryReadPixels(reader, timeout)`），UI 线程等不到就跳过这一帧、下一帧再取；
     再加一个看门狗：执行线程单项超过 N 秒时记日志并点名客户端，配合 API-M2 让用户断开它。

<a id="x-2"></a>

### X-2 资源只有单件上限，没有每客户端 / 全局总量 · 🔴 ✅

- **位置**：`Server/X11Server.Graphics.cs:58`（CreatePixmap 只限单块 2²⁶ 像素 = 256 MB）、`Server/X11Server.Dbe.cs:45`（后缓冲无上限）、
  `Server/X11Server.Composite.cs:104`、`Server/X11Server.Render.cs:726-728`（字形集无上限）、`Server/X11Server.Properties.cs:31,129`（单个属性 32 MB，
  窗口上的条数与总字节不限，挂在根窗口上的断开后也留着）、GLX 的上限按共享组算（GL-S3）、Retain 客户端的资源（CN-S4）、连接建立阶段（CN-S1）。
- **问题**：architecture §7 写的是「资源都有上限，超了回 BadAlloc……而不是拖垮进程」，实际只限住了**单个对象**。
  一条 16 字节的 CreatePixmap 就是 256 MB，十几条就是几 GB；服务端嵌在 VelaShell 主进程里，OOM 时所有 SSH 会话一起崩。
- **修法**：每客户端按字节记账（像素图、窗口缓冲、DBE、字形、属性、GLX 表面 / 纹理 / 列表 / 图元缓冲合在一本账里，释放时归还；
  属性记在写入者名下，断开不退还但计入全局），再加一道全局上限（比如 2 GB，可经 `X11ServerOptions` 配置）。
  X-Resource 的 `QueryClientPixmapBytes` 顺带报出真实值。文档写明上限的粒度。

<a id="x-3"></a>

### X-3 没有任何隔离档 · 🟠 ✅

- **现状**：内置引擎只有一个显示（`BuiltInLocalXServer` 与宿主都只有一个 `_server`）；SSH 连接器只用于受信模式（`SshForwardingOptions.cs:79`）；
  非受信模式走 `xauth generate … untrusted`（`Ssh/Forwarding/X11Forwarder.cs:351-367`），而服务端不实现 SECURITY 扩展，于是**勾掉「受信任」的会话 X11 转发根本开不起来**，
  界面提示却写着「非受信模式需要本机装有 xauth」（`Strings.zh-Hans.resx:5872`）。
- **放大面**：剪贴板默认双向（API-H5）、XTEST 与原始按键无开关（IN-S3）、焦点窃取（WN-S4、IN-E3）、任何客户端能清掉全局按钮状态（WN-S10）、
  CLIPBOARD 能被锁死（WN-S2）、远端窗口管理器能截走所有窗口（WN-S3）。
- **修法**：短期改提示文字（按引擎区分）；中期给连接加信任级别，在分派层做访问检查（见 F2）；长期每个 SSH 会话一个显示（F1）。
  这是产品决策，见 Q5。

---
## 三、连接、授权与基础设施（CN）

整体质量高：线格式、背压（条数 + 字节两道，登记后再看一眼不丢唤醒）、cookie 常数时间比较、Unix 套接字 listen 前 0600、
抽象命名空间按 uid、MIT-SHM 三级权限与「别人引用时再核一次」都对。问题集中在**连接建立之前那一段**、**Unix 套接字的占用判定**、
**Retain 落地后的副作用**，以及几处可以绕过的保护。路径相对 `src/VelaShell.XServer/`，宿主侧另注。

### 安全

<a id="cn-s1"></a>
#### CN-S1 连接建立阶段没有并发上限，还先按客户端给的长度分配 · 🟠 ✅

- **位置**：`Server/X11Server.Connection.cs:63-80`（接收循环无上限）、`:152-166`（读到 12 字节头就按 `nameLength` / `dataLength` 各至多 65535 分配 `rest`，
  最多 128 KB、直接进大对象堆）、`:134`（时限 30 秒）；`Server/X11Server.UnixSocket.cs:124-158`（抽象套接字同样，uid 到 `Authorize` 才核）。
- **问题**：`MaxClients` 只数已建立的客户端。未授权的连接每条占一个 fd、一个任务、两个 CTS 和至多 128 KB。
- **触发**：本机任何用户（不需要 cookie）经 `127.0.0.1:6000+N` 或抽象套接字开几万条连接，每条只发 `'l' 0 11 0 FFFF FFFF 0 0` 然后挂着；每 30 秒重来一轮。
  后果是 OOM 或 fd 耗尽，fd 耗尽又顺带触发 CN-E1，本机监听永久停掉。Windows 终端服务器、Linux 多用户机器上都够得着。
- **修法**：未授权连接全局上限（如 32 条）；授权名与数据各限 256 字节（与 SSH 侧 `X11SetupMessage.MaxFieldLength` 一致），读到头就判；
  `rest` 校验后再分配或从池里租；时限缩到 10 秒；抽象套接字在 accept 时就知道 uid，没配 cookie 且 uid 不同当场关。

<a id="cn-s2"></a>
#### CN-S2 Unix 套接字的占用探测是阻塞 connect，本机任何用户能让内置服务端永远启动不了 · 🟠 ✅（代码）/ ❓（内核行为）

- **位置**：`src/VelaShell.Infrastructure/XServer/XDisplayProbe.cs:44-66`（`CanConnect` 同步 `Socket.Connect`、无超时）、
  `Server/X11Server.UnixSocket.cs:77-89`（`IsUnixSocketLive` 同样，在 `StartAsync` 里同步调用）。
- **问题**：Linux 上对 backlog 已满的 AF_UNIX 流套接字做阻塞 connect 会一直等。`/tmp/.X11-unix` 人人可写、抽象命名空间人人可绑。
- **触发**：攻击者在 `\0/tmp/.X11-unix/X1` 上 `listen(0)` 并自己先连一条不 accept。显示号探测从 0 往上试，试到 1 就挂住：
  `BuiltInLocalXServer.StartAsync` 一直持有 `_gate`（`BuiltInLocalXServer.cs:115`），X Server 按钮再也启动不了；
  开了「转发时自动启动」的 SSH 会话卡在 `ResolveForwardingDisplayAsync`；`DisposeAsync` 也在 `_gate` 上等。
- **确认**：Linux 容器里按上法布置，调 `XDisplayProbe.IsInUseAsync(1, …)` 看是否返回。
- **修法**：探测一律 `ConnectAsync` + 300 ms 取消（与 TCP 探测一致）。

<a id="cn-s3"></a>
#### CN-S3 抽象名被抢占、`/tmp/.X11-unix` 属主不核对：用 `:N` 连的本机客户端会交出 cookie · 🟠 ✅

- **位置**：`Server/X11Server.UnixSocket.cs:39-43`（抽象名 bind 失败只记一行日志、继续）、`:46-55`（目录已存在时不看属主与粘滞位）、
  `Server/X11Server.cs:432-438`（任一 Unix 监听成功 `Display` 就是 `:N`）。
- **场景 A（Linux）**：攻击者先 bind 抽象名不 listen → 我们的探测得到 ECONNREFUSED、判为空闲 → 我们的抽象名 bind 失败只记日志，文件套接字与 TCP 成功 →
  攻击者再 listen → Xlib / XCB 对 `:1` **先试抽象名**，连到攻击者那里，建立报文里带着我们写进 `.Xauthority` 的 cookie → 攻击者拿它经 TCP 连进真服务端，
  截屏、记键盘、经 XTEST 往别的会话的 xterm 注入命令。
- **场景 B（macOS，或目录不存在的 Linux）**：攻击者事先建好属于自己的 `/tmp/.X11-unix`，作为目录属主把我们的 0600 套接字改名移走、换上自己的。
- **影响面**：宿主自己导出的是 `localhost:N.0`、SSH 走进程内连接器，所以只影响手工设 `DISPLAY=:N` 的用户；但库的 `Display` 属性给的就是 `:N`（MIT-SHM 只在 Unix 套接字上可用），对库是实打实的问题。
- **修法**：任何一条 Unix 传输因名字被占而建不起来时，这个显示号视为不可用（换下一个或抛）；探测改用 bind 判断（原子）；
  目录已存在时核对属主是 root 或本用户、非本用户则必须带粘滞位，否则不开文件套接字。

<a id="cn-s4"></a>
#### CN-S4 Retain 模式的客户端能占满 255 个编号 · 🟠 🔎

- **位置**：`Server/X11Server.Dispatch.cs:183`（`SetCloseDownMode` 无条件接受，也不校验取值，CN-M1）、`Server/X11Server.Connection.cs:302-307`（登记跳过保留的编号）、
  `:499-503`、`Server/X11Server.XRes.cs:28`（QueryClients 只列在线的）。
- **触发**：任何已授权客户端（包括经 SSH 来的）循环 254 次「连上 → RetainPermanent → 断开」，不到一秒。之后所有新连接收到 `Maximum number of clients reached`，
  连不上就发不了 KillClient，X-Resource 也看不见它们 —— 只能重启服务端，所有会话的 X 程序全断。每个保留的客户端还能留下 256 MB 的像素图。
- **修法**：保留客户端数量上限（如 16，超了按 Destroy 处理）；宿主有清理入口；X-Resource 列出保留的客户端。

#### 其余安全（🟡）

- **CN-S5** 服务端自有窗口的保护可以绕过 —— 同 [WN-S7](#wn-s7)。Composite 的叠加窗口 0x46 连 `DestroyWindow` 都没挡。
- **CN-S6** 客户端可控字符串原样进日志（`Server/X11Server.Text.cs:35` OpenFont 的名字最长 64 KB、可带 CR / LF / ESC；`Dispatch.cs:62` 打完整栈）：伪造日志行、往终端注入控制序列；
  每秒 50 条 × 64 KB 几十秒就用完宿主日志每天 64 MB 的额度。转义、截到 200 字符、限流按字节计。
- **CN-S7** `AuthorizationCookie` 为空数组时 `FixedTimeEquals([], [])` 为真，任何带空数据的 MIT-MAGIC-COOKIE-1 都放行（比不配还宽，远端也能进）；选项只持有引用，构造后改数组授权跟着变。要求 ≥ 16 字节并拷贝一份。
- **CN-S8** `localUser = ownerOnly || uid == ProcessUid`（`UnixSocket.cs:147`）：取到对端 uid 时应以 uid 为准（自定义路径在 9p / drvfs 上 chmod 不生效）；macOS / BSD 不取对端 uid，可用 `getpeereid` / `LOCAL_PEERCRED` ❓。
- **CN-S9** MIT-SHM 按服务端自己的 IPC 命名空间解释 shmid：把 `/tmp/.X11-unix` 挂进容器、同 uid 的容器进程能借服务端读写宿主用户的 SysV 段（同 CP-14）。按 SO_PEERCRED 的 pid 比对 `/proc/<pid>/ns/ipc`，不同就隐藏 MIT-SHM。
- **CN-S10** Windows 上 TCP 监听没设 `ExclusiveAddressUse`，同用户的低完整性进程能用 SO_REUSEADDR 抢端口 ❓。

### 错误

<a id="cn-e1"></a>
#### CN-E1 接收循环遇到任何 SocketException 就永久退出 · 🟠 ✅（代码）/ ❓（各平台多常见）

- **位置**：`Server/X11Server.Connection.cs:72-75`、`Server/X11Server.UnixSocket.cs:133-136`（`catch (… or SocketException) { return; }`）；`Connection.cs:76` 的 `NoDelay` 在 try 外。
- **问题**：EMFILE / ENFILE、accept 之前对端复位（Windows 的 ConnectionReset、BSD 的 ECONNABORTED）都是暂时性的，却让循环退出。
  之后本机 X 程序连不进来、一行日志都没有；SSH 连接器那条路不受影响，所以很难察觉。
- **修法**：只在监听器已释放或已取消时退出；其余记一行（限流）继续接，TooManyOpenFiles 先退避 100 ms；`NoDelay` 放进 try。

#### 其余错误（🟡）

- **CN-E2** 宿主关窗对以 Retain 模式断开的客户端无效（`Server/X11Server.TopLevels.cs:248-262`：消息发给已关闭的连接被丢弃，`DisconnectClient` 提前返回），窗口成了关不掉的僵尸。先判 `IsRetained`、走 `DestroyRetainedClient`。（同 WN-E15）
- **CN-E3** `DisconnectClient` 按编号摘除（`Connection.cs:488`）：KillClient / ApplyClose 同步调一次、连接的 finally 再 Post 一次，中间编号若已分给新客户端 C，C 会被误摘，之后 D 与 C 同 resource-base。改为按对象比对再摘。
- **CN-E4** `ServeAsync` 的令牌在「等执行线程登记」时取消（`Connection.cs:186-193`），`RegisterClient` 照样执行、没人清理，永久占一个编号。
- **CN-E5** 写出任务以 IOException 结束不触发断开（`Connection.cs:199`、`:436-477`）：读端卡在背压上（SYNC Await、XTEST 延迟、别人的 GrabServer）时察觉不到对端已断，窗口成僵尸。写出失败时 `client.Abort()`。
- **CN-E6** 单条回复超过 64 MB 整条连接被断（`XClient.cs:177-181`）：三块 4K 横排时 `xwd -root` 的回复约 99.5 MB；断开前还白算一遍。GetImage 先估大小、超限回 BadAlloc，或队列空时放行单条超限的回复。（同 DR-E11）
- **CN-E7** 宿主的日志委托抛异常会杀死执行循环（`DeferredHost.cs:58-61` 放锁后直接调 `Log`，外面没有 try）：`RunLoopAsync` 只接 OCE，之后所有客户端卡住；`DisposeAsync` 在 `await task` 处重抛、不收尾。`Log` 内部吞异常，RunLoop 外层兜底。
- **CN-E8** `.Xauthority` 的锁只建 `-c` 不建 `-l`（`Infrastructure/XServer/XAuthorityFile.cs:181-214`），与 xauth 只是部分互斥 ❓；10 秒就强删陈旧锁（慢 NFS 家目录上可能误删）；macOS 换网络后主机名变了，按启动时主机名登记的 cookie 失效。
- **CN-E9** 自动选显示号时探测与绑定之间的竞态失败不重试（`BuiltInLocalXServer.cs:139-155`、`:174-186`）；不建也不查 `/tmp/.X{N}-lock`，与 Xvfb / `xvfb-run -a` 撞号。
- **CN-E10** `StartAsync` 失败后不可重试（`_started` 已置 1，报误导性的「已经在监听」）；自定义 `UnixSocketPath` 过长抛 ArgumentOutOfRange 时宿主只接三类异常，服务端不释放、状态卡在 Starting（同 API-H10）；TCP 与 Unix 都没起来也照常返回、`Display == null`。

### 遗漏与规格漂移（🟡）

- **CN-M1** `SetCloseDownMode` 取值超出 0–2 应回 BadValue。
- **CN-M2** ListHosts 报访问控制 Disabled（`xhost` 显示「clients can connect from any host」，实际有 cookie）；ChangeHosts / SetAccessControl 静默成功。配了 cookie 时回 Enabled，改动回 BadAccess。
- **CN-M3** MIT-SHM QueryVersion 的 uid / gid 回 0（`Server/X11Server.Shm.cs:44`），规范要求服务端的有效 uid / gid。
- **CN-M4** X-Resource 看不见保留的客户端；`ClientClosed` 钩子注释说「资源已释放」，Retain 时资源还在、KillClient 时又调一次（`CleanupDamage` 摘掉了还在资源表里的 Damage）。
- **CN-M5** GrabServer 没有任何逃生路径，也**零用例**（grep 确认）：持有者挂住时所有会话的 X 程序冻结，宿主只能停服。见 API-M2。

### 设计与 API（🟡）

- **CN-D1** 库的默认配置不安全：`ListenTcp` 默认开、不要 cookie，`new X11Server()` + `StartAsync()` 的结果是本机任意用户可以记键盘、注入输入。宿主每次都配了 cookie 所以本产品不受影响，但与 SSH 库「零值取安全值」的约定不一致。见 Q4。
- **CN-D2** `GenericEventsEnabled` 只赋值从不读（`XClient.cs:267-268`）：照注释执行（先确认 XCB / Mesa 的 Present 路径都发过 GEQueryVersion）或删掉。
- **CN-D3** `SameHost` / `PeerUid` 在连接线程上写（`Connection.cs:194-195`），违反 `XClient` 的「只在执行线程上读写」约定，眼下靠 Channel 的先后关系恰好没事。
- **CN-D4** 两份 `.Xauthority` 解析器（Infrastructure 严格、SSH 库宽容）规则不同，违反「库里有的宿主不许再写一份」。
- **CN-D5** `XRequestReader.Take` 的 `_pos + count` 可能溢出（`Protocol/XWire.cs:81`），结果是 BadImplementation 而不是 BadLength（`AsSpan` 自己会查边界，不是内存问题）。

### 性能（🟡）

<a id="cn-p1"></a>
- **CN-P1** 放行暂存请求时一个工作项里同步执行全部（`WorkLoop.cs:220-229` Ungrab、`Sync.cs:399-409` EndWait、`XTest.cs:115-127`）：最坏 255 × 1024 条全程持锁，绕过 4 ms 预算与让行。
  放进「就绪队列」由 RunLoop 按预算分批 —— 不能简单排回 Channel 末尾，否则同一客户端已在 Channel 里的后续请求会插到前面。
- **CN-P2** ShmAttach 每次整读一遍 `/proc/sysvipc/shm`（`Shm.cs:219-255`），客户端用一串错的 shmid 反复 Attach 就能持续占 CPU。先 `shmctl(IPC_STAT)` 或对失败限流。

---

## 四、窗口、属性、选区、窗口管理器与字体（WN）

核心窗口与属性请求的线格式与错误条件大多照规范；DestroyNotify 子先父后、Expose 的 count、GetProperty 的偏移、跨字节序都对。
主要问题：**rootless 下 X 端状态与宿主不同步**（堆叠、图标化、激活）、**「每件有上限、总量没有」**、**剪贴板防回声永久生效**。

### 错误

<a id="wn-e1"></a>
#### WN-E1 指针按 X 的堆叠次序命中，不按宿主的 z 序 · 🔴 ✅（同 IN-E1、CP-1）

- **位置**：`Server/X11Server.Input.cs:53-70`（宿主注入时带着顶层句柄 `top`，却只用它换算根坐标）、`:169-201`（`WindowAt` 从 `Root.Children` 栈顶往下找）；
  `Server/X11Server.TopLevels.cs:197-219`（`ApplyFocus` 不调整堆叠）；宿主 `AvaloniaXServerHost.cs:398-406`（激活只调 `FocusTopLevel`）；公开 API 没有任何「升起 / 重排」入口。
  `Server/X11Server.Windows.cs:96` 建窗口时放到最上，`:352-388` Map 不抬升；`Ewmh.cs:251` 最小化只写 `WM_STATE=Iconic`、窗口仍映射着。
- **问题**：X 端顶层的次序只随「创建先后」和客户端自己的 ConfigureWindow 变；用户在宿主里点谁到前面、把谁最小化，X 都不知道。反方向也不通：
  客户端 `XRaiseWindow`、Java `toFront()`、`xdotool windowraise` 都升不起原生窗口。
- **触发**：先开 xterm A、再开 B，两窗重叠。点 A 把它提到前面，在重叠区点击或拖选 —— ButtonPress、隐式抓取、光标形状全落到后面看不见的 B 上。
  最小化 B 后，鼠标经过 B 原来那块区域，事件被不可见的 B 吃掉。`_NET_CLIENT_LIST_STACKING`、根窗口 GetImage 的拼图、XDND 找目标也跟着错。
- **修法**：
  1. 宿主注入的指针事件（没有活动抓取时）以注入的 `top` 为命中起点，只在它的子树里找；落在外面（捕获拖动）时才全局找。
  2. 新增宿主动作 `RaiseTopLevel`（或让 `FocusTopLevel` 顺带抬到最上），发 ConfigureNotify、更新 `_NET_CLIENT_LIST_STACKING`；非 override-redirect 的顶层映射时也抬升。
  3. 客户端对顶层发 stack-mode = Above（含 MapRaised）时转成一个「升起」请求交给宿主（`XWindowManagerRequest` 加一种）。
  4. Hidden 状态的顶层不参与命中。

<a id="wn-e2"></a>
#### WN-E2 最小化之后，客户端用 MapWindow 还原无效 · 🟠 🔎（代码路径）/ ❓（各工具包表现）

- **位置**：`Server/X11Server.Windows.cs:354-357`（已映射就直接返回）；图标化只写属性，窗口仍映射（`Ewmh.cs:251`、`:320-322`）。
- **问题**：ICCCM §4.1.4：从 Iconic 回 Normal，客户端 map 窗口；客户端靠「是否映射」判断状态。这里最小化后 X 窗口一直映射，`XMapWindow` 是空操作、宿主收不到请求，客户端也收不到 UnmapNotify。
- **触发**：Tk `wm deiconify`、Emacs `make-frame-visible`、`xdotool windowmap`。GTK 另发 `_NET_WM_STATE` 去掉 Hidden，不受影响；Qt `showNormal()` 待确认。
- **修法**：`Map()` 碰到「已映射的顶层且状态含 Hidden」时发 `XStateChangeRequest(remove Hidden)` 或 `XActivateRequest`。

<a id="wn-e3"></a>
#### WN-E3 X 端复制源退出后，X 程序再也粘贴不到这份内容 · 🟠 ✅（同 CP-3）

- **位置**：`Server/X11Server.Clipboard.cs:79`（与上次交给宿主的文本相同就永久忽略）、宿主 `AvaloniaXServerHost.cs:296`（宿主也记一份 `_lastClipboard`）、`Connection.cs:555-567`（属主断开只删选区）。
- **问题**：防回声没有「当前属主仍是那个 X 客户端」这个前提。X 属主一走 CLIPBOARD 就没人占，而宿主手里的同一份文本被两道防回声拦着，永远不会再交回 X。architecture §7 的原意是「宿主写回刚收到的文本时不抢」，现在成了「永远不抢」。
- **触发**：在 xterm A 里复制、关掉 A、到 gedit 粘贴 —— 空的，虽然本机剪贴板里有。
- **修法**：同步的选区失去 X 属主时，由服务端以宿主身份接管（相当于剪贴板管理器），或者清空 `_lastDeliveredText` 并通知宿主作废 `_lastClipboard`；更完整的是 F14 的 CLIPBOARD_MANAGER。

<a id="wn-e4"></a>
#### WN-E4 `_NET_MOVERESIZE_WINDOW` 不夹取 32 位几何值 · 🟠 ✅

- **位置**：`Server/X11Server.Ewmh.cs:326-336`，值直接交给 `Configure`（`Windows.cs:498-513`，不夹取）。
- **问题**：x / y / 宽 / 高都是任意 32 位整数，宽高可以到 2³¹−1，远超 X 的 16 位；宿主自己的 `MoveTopLevel` 却只许 short 范围。GetGeometry 与 ConfigureNotify 按 `(ushort)` 截断、
  `ApplyPointerMotion` 算根坐标 int 溢出成负数（指针被当成离开）、宿主收到 2³¹ 大小的原生窗口；缓冲按 2²⁶ 像素截，每个窗口 256 MB。也忽略了 EWMH 的 gravity 与外框。
- **触发**：会话 B 的客户端对会话 A 的窗口发 flags = 0xF00、x = 0x7FFFFFF0、w = 0x7FFFFFFF。
- **修法**：x / y 夹到 short，宽高夹到 1..32767，不合法就忽略；按 gravity 与 `_NET_FRAME_EXTENTS` 换算；最好转成请求交给宿主决定。

#### 其余错误（🟡）

- **WN-E5** DestroySubwindows 的销毁顺序反了（`Windows.cs:237`）：协议要求从下到上。
- **WN-E6** CirculateWindow 不做 SubstructureRedirect 改道（从不发 CirculateRequest）、不按遮挡判断、LowerHighest 后让出来的兄弟留着旧像素（`Windows.cs:584-610`）。
- **WN-E7** ReparentWindow 自动重映射时请求方传了 null（`Windows.cs:658`），有改道者时发起 reparent 的窗口管理器自己也收到多余的 MapRequest。
- **WN-E8** ConfigureWindow：stack-mode > 4 不回 BadValue（`Windows.cs:469`）；ResizeRedirect / ResizeRequest 没实现；父窗口改尺寸时不按子窗口的 win-gravity 挪动、没有 GravityNotify / UnmapGravity；TopIf / BottomIf / Opposite 按 Above / Below 近似。
- **WN-E9** InputOnly 顶层被当成可见窗口交给宿主（`Windows.cs:373-383` 给它建 24 位缓冲），宿主开一个黑色原生窗口；快照里没有 InputOnly 标志（GTK 的 GtkInvisible 就会多出一个窗口）。
- **WN-E10** xterm 的隐形指针显示成箭头（`Cursors.cs:66`）：nil2 等非 cursor 字体的字形光标一律 −1、按默认箭头。把字形栅格化成图像，空白字形给 Hidden。
- **WN-E11** 字符串编码不看属性类型：WM_NAME 一律按 Latin-1 解（`TopLevels.cs:70-71`，类型为 UTF8_STRING / COMPOUND_TEXT 的标题乱码）；剪贴板 TEXT 目标按 Latin-1 有损转换（`Clipboard.cs:146-149`，中日韩变「?」，TEXT 可以直接回 UTF8_STRING 类型）。✅
- **WN-E12** EWMH 细节：`_NET_CLIENT_LIST` 按 XID 排序（`Ewmh.cs:200`，规范要求按首次映射顺序）；焦点进入 override-redirect 窗口时 `_NET_ACTIVE_WINDOW` 与 FOCUSED 被挪到弹层上、主窗口显示成非活动外观（`:204-221`）；withdraw 时不删 `_NET_WM_STATE` / `_NET_WM_DESKTOP`，重新映射带着过期的 Hidden / Focused。
- **WN-E13** ConvertSelection 不校验 property 原子（`Properties.cs:362`），宿主占有 CLIPBOARD 时 `ServeSelection` 直接用它当属性名写到任意窗口（含根窗口），之后 `xprop -root` 收到 BadAtom。
<a id="wn-e14"></a>
- **WN-E14** 换 DPI 时覆盖整个 RESOURCE_MANAGER（`XSettings.cs:53`；宿主屏幕变化时调用，`AvaloniaXServerHost.cs:131-136`）：用户 `xrdb -merge` 进去的 xterm / Emacs / Motif 资源全丢。只替换 `Xft.*` 那几行。（同 CP-18）
- **WN-E15** 关闭按钮对 Retain 模式断开的客户端无效 —— 同 CN-E2。
- **WN-E16** SHAPE Combine 在客户端给的偏移之外又加了两个窗口内区原点之差（`Shape.cs:74-76`），常见用法（子窗口位置当偏移传）会偏两倍 ❓。对照 SHAPE 规范「ShapeCombine」原文，并写一条子窗口在 (10,10) 的用例。
- **WN-E17** 单字节（线性）字体的 QueryTextExtents 与 PolyText16 丢掉 byte1（`Text.cs:105,220`）：byte1 非 0 应按不存在的字符走默认字符。
- **WN-E18** 宿主移动窗口后只发合成的 ConfigureNotify（`TopLevels.cs:222-237`）：根窗口上选了 SubstructureNotify 的客户端收不到真实事件，也没调 `UpdatePointerWindow` 与 Present 的 ConfigureNotify。
- **WN-E19** 宿主 → X 的大文本不走 INCR、绕过 32 MB 属性上限（`Clipboard.cs:142-157`）；请求方取超过 64 MB 时被输出积压上限断开；每次 ConvertSelection 都重编码一遍；`SetClipboardText` 本身也不设上限。

### 安全

<a id="wn-s2"></a>
#### WN-S2 SetSelectionOwner 的时间戳规则只在「当前有属主」时检查 · 🟠 ✅

- **位置**：`Server/X11Server.Properties.cs:321-341`；释放与属主断开时整条记录被删，最后一次变更时间随之丢失（`:336`、`Connection.cs:557-563`、`Windows.cs:298-305`）。
- **问题**：协议规定「时间晚于服务端当前时间则请求无效」，且最后一次变更时间不随属主消失而变；实现里两条都只在有属主时检查。
- **触发**：恶意客户端先 `SetSelectionOwner(None, CLIPBOARD, CurrentTime)`，再 `SetSelectionOwner(w, CLIPBOARD, now + 0x7FFFFFF0)` —— 未来时间被接受。之后所有会话里的程序带真实事件时间去占 CLIPBOARD 都被当成「早于当前属主」静默忽略，
  复制全部失效、粘贴拿到的都是攻击者的内容。只有宿主复制新文本时（`Clipboard.cs:91-101` 不查时间）才解锁一次，攻击者可以立刻重来。
- **修法**：`time - now > 0` 一律忽略；另存 `_selectionLastChange[atom]`，没属主时也拿它比；`TakeSelectionForHost` 用 `max(now, lastChange)`。

<a id="wn-s3"></a>
#### WN-S3 没人占住根窗口的 SubstructureRedirect；save-set 不生效 · 🟠 ✅（同 CP-23）

- **位置**：`Server/X11Server.Windows.cs:188-207`（根窗口与其它窗口一视同仁，只检查别的客户端是否已占）、`:357-362`（之后所有 MapWindow 都变 MapRequest）、`:485-492`；
  `:330-346`（ChangeSaveSet 只登记）、`XFixes.cs:58-59`（XFIXES 的 ChangeSaveSet 空操作）；`Properties.cs:427-430` 注释写明「没有别的客户端会重定向根窗口」。
- **问题**：服务端以窗口管理器自居（`_NET_SUPPORTING_WM_CHECK`），却不是一个客户端，没占 `WM_S0` 也没选根窗口的 SubstructureRedirect。
- **触发**：远端误跑 `openbox`、`twm`、`startxfce4`、`xfwm4` 就会占上。之后**所有会话**的新窗口变成发给它的 MapRequest，它把它们 reparent 进自己的外框：
  外框成了原生窗口，宿主边框与远端窗口管理器的边框叠成两层，两个窗口管理器抢 `_NET_SUPPORTED` / `_NET_ACTIVE_WINDOW`。它一退出，外框被销毁，而 save-set 不生效 —— **别人的窗口跟着被销毁**。XEmbed 的嵌入方崩溃同理。
- **修法**：服务端用一个内部占位占住根窗口的重定向与 `WM_S0`，让别的客户端选根窗口 SubstructureRedirect 时回 BadAccess（真实桌面上已有窗口管理器时就是这样，真实的窗口管理器会自己退出）；
  连接收尾时按协议「Connection Close」一节处理 save-set（reparent 到最近的不属于它的祖先、未映射的补映射），XFIXES 的 ChangeSaveSet 支持 target 与 map 标志。
  ⚠️ **与 CP-9（Java）耦合**：占住 `WM_S0` 之后 Java 会改判成「会套外框的窗口管理器」，必须同一批改、先实测。

<a id="wn-s4"></a>
#### WN-S4 `_NET_ACTIVE_WINDOW` 不防焦点窃取 · 🟠 🔎（同 API-H6）

- **位置**：`Server/X11Server.Ewmh.cs:314-316`（不读 source、时间戳、请求方当前活动窗口）；`Host/XWindowManager.cs:123`（`XActivateRequest` 没有这些字段）；
  宿主 `AvaloniaXServerHost.cs:326-328` 无条件 `native.Activate()`；`XNativeWindow.cs:237` 让 override-redirect 与 ABOVE 状态的窗口成为系统级 `Topmost`；`:252` 新映射的窗口默认激活。
- **问题**：原生窗口与 VelaShell 主窗口在同一个进程里，Windows 的前台锁拦不住它们之间的切换。任何 X 客户端都能在任意时刻把自己的窗口切到系统前台。
- **触发**：被攻破的远端机上的程序周期性发 `_NET_ACTIVE_WINDOW`；用户正在 VelaShell 终端里输 sudo 口令时，X 窗口跳到前台接走按键。全屏的 override-redirect 窗口能盖住所有本机程序，画一个像系统凭据框的界面。
- **修法**：`XActivateRequest` 带上 source（1 应用 / 2 分页器）、时间戳与该客户端最近一次用户交互时间（维护 `_NET_WM_USER_TIME`）；宿主只在 source = 2 或时间戳不早于用户对该程序的最后操作时 `Activate`，
  否则改设 DemandsAttention；本机窗口有焦点时一律降级为闪任务栏；override-redirect 只在所属程序活动时才置顶；标题里标出来源主机（F18）。这部分宿主策略写进 architecture §7。

<a id="wn-s5"></a>
#### WN-S5 快照里的字符串没有长度上限，每次几何刷新都整份解码 · 🟠 ✅

- **位置**：`Server/X11Server.TopLevels.cs:68-80`、`Ewmh.cs:416-417`；`RefreshTopLevel` 在每次移动 / 缩放（`Windows.cs:550-553`）、SetShape、相关属性变化时都重建整份快照。
- **问题**：`_NET_WM_NAME`、`WM_NAME`、`WM_CLASS`、`WM_CLIENT_MACHINE`、`WM_WINDOW_ROLE` 每次整份解码（各至多 32 MB），原样交给宿主设成系统标题（`XNativeWindow.cs:84`）；控制字符与双向覆盖符（U+202E）也照传，可以在任务栏伪造标题。
- **触发**：把 `_NET_WM_NAME` 设成 32 MB，然后不停对自己的顶层发 ConfigureWindow —— 每条 24 字节的请求换一次 32 MB 的 UTF-8 解码和一次 64 MB 的大对象分配。
- **修法**：标题截到 4 K 字符，类名 / 角色 / 机器名截到 256；按 XProperty 引用缓存解码结果（同 `ParsedIcons`）；只有几何变化时只换几何字段；过滤 C0 / C1 与双向控制符。

<a id="wn-s6"></a>
#### WN-S6 XFIXES GetCursorName 把任意名字登记成原子，绕过原子上限 · 🟠 ✅

- **位置**：`Server/X11Server.XFixes.cs:246-253`（第 249 行直接 `Intern(name)`），`Intern` 本身不查上限（`Properties.cs:47-58`）；名字来自 SetCursorName。
- **触发**：循环「SetCursorName(c, 64 KB 随机名) + GetCursorName(c)」，§124 加的原子上限（2¹⁸ 个、名字合计 16 MB）对这条路无效，原子永不释放。
- **修法**：只查不建（不存在回 None），或走 InternAtom 的同一套上限。

<a id="wn-s7"></a>
#### WN-S7 服务端自有窗口的保护可以绕过 · 🟠 ✅（同 CN-S5）

- **位置**：`Server/X11Server.Windows.cs:248`（`Destroy` 只在顶层调用处挡根窗口与 0x43）、`:612-640`（ReparentWindow 不挡服务端的窗口）、`:267-288`（`DestroyTree` 销毁后代时不查）。
- **触发**：任意客户端先 `ReparentWindow(0x43, 自己的窗口)`，再 `DestroyWindow(自己的窗口)`（或者直接断开）。0x43 被销毁：整个显示（所有会话）的剪贴板桥接、`_XSETTINGS_S0`（DPI / 缩放）、
  `_NET_SUPPORTING_WM_CHECK` 一起失效，直到重启。`MapWindow(0x43)` 还会让宿主多出一个原生窗口。
- **修法**：Owner 为 null 的服务端窗口与根窗口同等对待：Reparent / Map / Configure / ChangeWindowAttributes 回 BadMatch 或 BadAccess，`DestroyTree` 跳过（先挂回根窗口）。

#### 其余安全（🟡）

<a id="wn-s8"></a>
- **WN-S8** 宿主回调不合并、不限流（`DeferredHost.cs:20-41` 逐条转发）：循环改标题、反复映射 / 取消映射、狂发 `_NET_WM_STATE` 或 Bell，宿主 UI 线程就不停建关原生窗口，队列无界增长。同一批里按窗口把 `TopLevelChanged` 的标志或起来合成一条，成对的映射 / 取消映射抵消，WM 请求与响铃按客户端限流（与 IN-S4 一起做）。
- **WN-S9** XFIXES 的选区监听没有上限（`XFixes.cs:60-75`），每次属主变化扫全表（`:352`）。按选区建索引、每客户端设上限。
- **WN-S10** 任意客户端对任意顶层发 `_NET_WM_MOVERESIZE`、`data[3]` 给 0 或越界值，就清掉全局按钮状态并解除隐式抓取（`Ewmh.cs:344-364`），打断别的会话正在进行的拖动。至少核对发送者是当前抓取的属主。
- **WN-S11** 宿主剪贴板一旦交给 X 所有会话都能读（设计层面），别的会话还能经 XFIXES SelectionNotify 精确得知何时有新内容。见 API-H5、Q3。

### 遗漏与规格漂移

<a id="wn-m1"></a>
#### WN-M1 颜色库缺 rgb.txt 的编号变体 · 🟠 ✅（回 BadName）/ ❓（各程序的可见表现）

- **位置**：`Resources/ColorNames.cs:21-49,82-130`，只有 `grayN` / `greyN` 处理了编号；`red3`、`blue2`、`VioletRed4`、`Blue1`、`Firebrick` 等都不在。
- **触发**：xterm 内置的 ANSI 调色板用 red3 / green3 / yellow3 / blue2 / magenta3 / cyan3，LookupColor / AllocNamedColor 回 BadName，`ls --color` 可能全退回前景色；Emacs 默认的语法高亮同理。
- **修法**：随库带完整的 X11 颜色名表（约 750 项，含 1–4 编号与带空格的写法）—— 是数据，处理方式同内置字体（AGENTS 纪律 4）。
- **确认**：interop 里 `xterm -e 'ls --color=always /'` 截图，或看 xterm 启动时的「Cannot allocate color」告警。

#### 其余遗漏（🟡）

<a id="wn-m2"></a>
- **WN-M2** ICCCM / EWMH 提示没解析全（`Ewmh.cs:385-418`）：WM_HINTS 的 initial_state（`xterm -iconic` 不生效）、icon_pixmap / icon_mask（老程序任务栏没图标）、window_group；WM_NORMAL_HINTS 的 base size（xterm 按字符格缩放基准不对）、aspect、win_gravity、USPosition / PPosition（宿主只能猜「坐标是不是 0,0」）；MWM 的 functions；数值没消毒（超过 int.MaxValue 的 uint 强转成负的最小尺寸）。映射前写好的 `_NET_WM_STATE` 最大化 / 全屏也不生效（CP-8）。
<a id="wn-m3"></a>
- **WN-M3** `_NET_WORKAREA` 是整个根窗口（`Ewmh.cs:166`），`XMonitor` 没有工作区字段：菜单、最大化、对话框会落到任务栏 / Dock 后面。（同 CP-7、API-M5）
- **WN-M4** 剪贴板：TARGETS 里没有 MULTIPLE（ICCCM §2.6.2 要求属主支持）与 COMPOUND_TEXT；X 端取回没有超时；同一时刻只能取一个选区（`_fetch` 单槽），CLIPBOARD 与 PRIMARY 同时变化会互相覆盖。
- **WN-M5** XFIXES GetCursorImage / GetCursorImageAndName 只回 1×1 透明像素（`XFixes.cs:90-96,254-260`），库里其实已有位图与 ARGB 光标的图像；ChangeCursor / ChangeCursorByName 是空操作。x11vnc、ffmpeg x11grab 录不到光标。（同 CP-31）
- **WN-M6** 从不发 VisibilityNotify ✅（影响 ❓：看 xterm / mpv 选了 VisibilityChangeMask 时是否改刷新策略）。
- **WN-M7** 弹出菜单开着时点 X 窗口以外的地方，X 端收不到 ❓（同 CP-6）：宿主接口没有「抓取开始 / 结束」通知；Xaw / Motif / Tk 的菜单可能关不掉，弹层又是置顶的。xterm 里 Ctrl+左键开菜单再点桌面确认。
- **WN-M8** 快照里没有输入形状与 InputOnly 标志：宿主做不出「形状以外不接收鼠标」（快照文档自己这么要求）。
- **WN-M9** 字体别名只有 7 个（`Fonts/FontCatalog.cs:34-43`），没有 5x7 / 6x10 / 7x13 / 8x13；请求 14 像素之类的尺寸直接 BadName，不退到最接近的。与 CP-16 一起做。

### 设计与 API（🟡）

- **WN-D1** 公开记录 `XCursorImage.Pixels`、`XWindowIcon.Pixels` 是可变 `uint[]`，多份快照共用同一实例（`Host/XCursor.cs:30`、`Host/XWindowManager.cs:105`），宿主一改就污染服务端缓存。改 `ReadOnlyMemory<uint>`。

### 性能

<a id="wn-p1"></a>
#### WN-P1 ChangeProperty 的追加 / 前插每次复制整份属性 · 🟠 🔎

- **位置**：`Server/X11Server.Properties.cs:136`；顶层的 `_NET_WM_ICON` 每追加一次还整份重新解析图标并通知宿主（`Ewmh.cs:393-398`）。
- **触发**：往一个 30 MB 的属性上每次追加 4 字节，每条 28 字节的请求换约 30 MB 复制与一次大对象分配；排满 1024 条就是数秒。
- **修法**：属性值改可增长缓冲（容量翻倍）；图标只在批末合并刷新一次。

#### 其余性能（🟡）

- **WN-P2** RotateProperties 用 `Array.IndexOf` 查重复，O(n²)（`Properties.cs:271`）。
- **WN-P3** `ExposeSubtree` 对每个窗口重算 `ClipByChildren` / `VisibleOuter`，n 个兄弟时 O(n²) 次区域运算（`Exposure.cs:70-95,214-255`）；平铺背景逐像素取模；每次 SetShape 整窗重画 ❓（2000 个兄弟窗口 + ResizeTopLevel 计时）。

---
## 五、绘图、RENDER、合成与同步（DR）

§114 / §124 修过的几项（Region 按带归并、RENDER 整数核、同缓冲 Detach、CopyArea 先核深度、PutImage 只解码可见部分）复核有效。
问题集中在：**代价与几何范围成正比而不是与可见面积成正比**（§124 只修了多边形一路）、**读窗口源只按窗口尺寸裁**、**大分配没有上限**、
**宽线 / 多边形 / 弧的像素中心约定与核心协议不一致**、SYNC 的 O(n²) 与空转计时器。

### 安全（全部属于 X-1 / X-2 的具体路径）

<a id="dr-s3"></a>
#### DR-S3 AddGlyphs 长度校验 int 溢出，36 字节的请求触发 4 GB 分配 · 🔴 ✅ 🧪

- **位置**：`Server/X11Server.Render.cs:720`（`int size = BitmapStride(w * bpp) * h`）、`:766`（`new uint[w * h]`）；`:700`（`count` 按 `(int)r.U32()` 读，ReadStops `:319` 同样）。
- **触发**：a8r8g8b8 字形集里 w = h = 32768：stride 131072 × 32768 = 2³² 溢出成 0，长度检查通过；随后 `DecodeColorGlyph` 先分配 4 GB，再在解码里回 BadLength。
  实测连发 50 条（共约 1.8 KB）：执行线程卡 13.4 秒、累计分配 200 GB、进程私有内存峰值 16.5 GB。
- **修法**：size 用 long 算、与 `r.Remaining` 比，先核长度再分配；`count` 为负回 BadLength 而不是 BadImplementation。

<a id="dr-s1"></a>
#### DR-S1 核心绘图不先裁剪，代价随坐标范围线性增长 · 🔴 🧪

- **位置**：`Drawing/Rasterizer.cs:387-397`（FillRect 从 y 扫到 y+height，每行再遍历全部裁剪块）、`:433-462`（ThinLine 逐像素走完整条线）、`:730-756`（细弧每段之间调 ThinLine）、
  `:177-187`（InClip 线性扫全部裁剪块）；入口 `Server/X11Server.Graphics.cs:303-340,414-425`。
- **触发**：10×10 的目标上，`FillRect(0, -32768, 1, 65535)` 约 1.7 ms、一条 65535 长的细线约 1.5 ms（各 1000 次实测）。一个 256 KB 的 PolyFillRectangle 装 32 K 个矩形就是约 50 秒，
  BIG-REQUESTS 的 16 MB 请求是小时级。CoordModePrevious 的 PolyLine 累计坐标不设限；SetClipRectangles 设 16384 块之后 PolyPoint 每个点线性扫一遍。
- **修法**：FillRect 先与 `ClipBounds` 求交；细线先 Cohen–Sutherland 裁到 ClipBounds，再从入口点用同样的误差项继续 Bresenham（保证与不裁时像素一致）；
  细弧只取与 ClipBounds 相交的参数区间；InClip 利用分带有序早退或二分；GC 的裁剪 Region 缓存在 GC 上（`Rasterizer.cs:46-50` 现在每个请求 FromRects 一次）。

<a id="dr-s2"></a>
#### DR-S2 宽线的圆帽 / 圆接头顶点数随线宽线性增长 · 🔴 🧪

- **位置**：`Drawing/Rasterizer.cs:698-708`（`n = Math.Max(8, (int)(r * 4))`）、`:645-658`（每个接头一个圆，整条折线一次建边表）。
- **触发**：line-width 65535、CapRound 的 4 点折线实测 250 ms、分配 48 MB；外推一个约 4 KB 的 1000 点 PolyLine 约 12 GB，进程 OOM。PolySegment 每段两个圆同理。
- **修法**：顶点数封顶（如 1024），宽线段与圆先按 ClipBounds 剔除；更好是解析地算圆帽 / 圆接头的 span。

<a id="dr-s4"></a>
#### DR-S4 渐变按色标线性查找 · 🔴 ✅ 🧪

- **位置**：`Drawing/RenderSources.cs:392-396`（`while (stops[i] < t) i++`，每像素一次）、`Server/X11Server.Render.cs:317-335`（色标个数只受请求长度限制）。
- **触发**：先建一个 20000 色标的线性渐变（请求 234 KB），之后每条 500×500 的 Composite 实测 1.8 秒；BIG-REQUESTS 能放约 140 万个色标，单条 Composite 就是分钟级。
- **修法**：二分查找（或预算 256 / 1024 级颜色表）；色标个数设上限；色标非递增时回 BadValue。

<a id="dr-s5"></a>
#### DR-S5 CompositeGlyphs 的遮罩按「字形外接 ∩ 可绘对象」分配 · 🟠 🧪

- **位置**：`Server/X11Server.Render.cs:889`、`:897-898`（a8 遮罩 `ArrayPool.Rent(size)`）、`:918`（带颜色遮罩 `new Argb[w*h]`，每像素 16 字节）。
- **触发**：32000×32000 的窗口（缓冲被截到 2²⁶，窗口尺寸不受限）上放两个相距很远的字形，a8 遮罩实测分配 1023 MB；a8r8g8b8 遮罩格式时十几 GB。
- **修法**：与 `CompositeShapes` 一样用 `BoundsOf(target.Target.Clip)` 求交（同文件 `:488` 已有现成写法）。

<a id="dr-s7"></a>
#### DR-S7 SYNC 报警器求值 O(n²) · 🟠 🧪

- **位置**：`Server/X11Server.Sync.cs:514-516`：遍历 `_alarms.ToArray()` 时每项又 `_alarms.Contains`；CreateAlarm、ChangeAlarm、SetCounter、TriggerFence 都触发一轮。
- **触发**：建 1000 个从不满足的报警器 189 ms，4000 个 5.0 秒，增长接近立方。
- **修法**：`HashSet` 或「已销毁」标志代替 `Contains`；按计数器建索引，只求值变了的那个计数器上挂的报警器。

#### 其余安全（🟡）

- **DR-S6** 像素内存只限单块、不限累计 —— 归入 [X-2](#x-2)。
- **DR-S8** 系统计数器上「已越过」的 Transition 触发器让计时器 1 ms 空转（`Sync.cs:560-566`，`Math.Max(1, WaitValue − now)`）：IDLETIME 上的 PositiveTransition 报警器触发后（GNOME / KIdleTime 的用法）用户不动时每毫秒醒一次、持锁求值。`LastValue ≥ WaitValue` 时不排计时器。
- **DR-S9** 源与目标同缓冲、源带 repeat 或 transform 时，每条请求拷整张 picture（`RenderSources.cs:159`，窗口 picture 至多 256 MB）。用变换后的源外接矩形估算范围。

### 错误

<a id="dr-e1"></a>
#### DR-E1 宽线 / 多边形 / 弧在 (x+0.5, y+0.5) 采样，核心协议规定整数坐标就是像素中心 · 🟠 ✅ 🧪

- **位置**：`Drawing/Rasterizer.cs:547`（`sampleY = row + 0.5`）、`:576-577`（`Ceiling(X - 0.5)`）。
- **依据**：核心协议「coordinates are integral … and coincide with pixel centers」；多边形填充取「中心在多边形内」的像素，边界按左 / 上包含、右 / 下不包含；宽线取「中心落在线的外形内」的像素。
- **实测**：lw = 1 的水平线 y = 10 画到了第 9 行；lw = 3 画到 8..10（应为 9..11）；同一矩形边框 lw = 0 画在 5..10、lw = 1 画在 4..9，两者错开一像素；三角形 (0,0)(10,0)(0,10) 只有 45 个像素（应为 55）；FillArc 整体偏半像素。
  整数顶点的矩形碰巧一致，所以现有用例「多边形按像素中心填充」发现不了。
- **影响**：用 line-width ≥ 1 的程序（Tk canvas、gnuplot、xfig、Motif 的宽边框）与 xclock / xlogo 的多边形都错位一像素。
- **修法**：核心绘图在整数坐标采样（`sampleY = row`、`start = Ceiling(xa)`、`end = Ceiling(xb)`，左闭右开、上闭下开）。**RENDER 的 CoverageMask 按 RENDER 规范本来就用 +0.5，不要动。**

<a id="dr-e2"></a>
#### DR-E2 读窗口源只按窗口尺寸裁，越出顶层缓冲时回 BadImplementation 或读到折行像素 · 🟠 🧪

- **位置**：`Server/X11Server.Graphics.cs:497`、`:504-510`（ReadSource 的范围是 `(0,0,w,h)`，下标按偏移，不与 `buffer.Bounds` 求交）；`Drawing/RenderCompositor.cs:236-241`、`:319-323`（BlitImage 快路径只查 picture 尺寸）。
- **实测**：子窗口放在 (−20, 0)、尺寸 40×10：GetImage、CopyArea（窗口内滚动）、以另一顶层为目标的 RENDER Src 合成都回 BadImplementation —— Xlib 默认错误处理让程序直接退出；子窗口伸出右边时 GetImage 回的是折到下一行开头的像素。
  被 2²⁶ 截掉高度的顶层读截掉的部分同样出错。
- **修法**：可用范围再与「顶层缓冲 − 偏移」求交，拿不到的部分 CopyArea 发 GraphicsExposure、GetImage 补 0；BlitImage 的条件同时查缓冲范围。

#### 其余错误（🟡）

- **DR-E3** PolyLine 首尾重合时不当闭合路径（`Graphics.cs:322` 从不传 `closed`）：GXxor 细线起点被画两次抵消；宽线首尾是两个端帽而不是接头。
- **DR-E4** 宽弧用 `Math.Round`（银行家舍入）取外 / 内框（`Rasterizer.cs:760-763`）：lw = 1 时 x = 4 与 x = 5 的弧画在同一处。
- **DR-E5** CopyPlane 不发 GraphicsExposure、不校验 bit-plane（≥ 2^源深度应 BadValue）（`Graphics.cs:617-653`），且逐像素 PutPixel。
- **DR-E6** DBE 的 Background 交换动作只填 BackgroundPixel（`Dbe.cs:159-161`）：忽略背景像素图与 ParentRelative、不按深度掩码；SwapBuffers 里窗口重复出现应 BadMatch。
- **DR-E7** SYNC 报警器按 delta 推进最多 100 万次、超了就停用（`Sync.cs:535-547`）：规范只在溢出时停用。用闭式 `k = ceil((value − wait + 1) / delta)` 加溢出检查。
- **DR-E8** CreateAlarm 的 test-type 默认值是 PositiveTransition（`Resources/XSyncResources.cs:31`），按 SYNC 3.1 应为 PositiveComparison ❓（对照规范原文）。
- **DR-E9** Relative 的 value-type 换算后被改回 Absolute（`Sync.cs:295-299`）：QueryAlarm 报错、只带 value 位的 ChangeAlarm 被按绝对值解释 ❓。
- **DR-E10** Await 的 event-threshold 读入后从未使用（`Sync.cs:159-163`）；AlarmNotify 先发后改状态（`:527`），delta = 0 的比较型报警器报 Active 实际随即 Inactive ❓。
- **DR-E11** 回复超过 64 MB 时客户端被断 —— 同 CN-E6。
- **DR-E12** NameWindowPixmap 给顶层的是共享缓冲（`Composite.cs:98-107`），但 `PixelBuffer.Resize` 就地换数组（`Drawing/PixelBuffer.cs:81-102`），窗口改尺寸后这张像素图的宽高跟着变（规范要求旧像素图保持原样）；往它里画只记像素图的损伤，宿主收不到 TopLevelDamaged。
- **DR-E13** 参数校验不全：GC function 被 `& 0xF` 截断，line-style / cap / join / fill-style / fill-rule / arc-mode / subwindow-mode 越界不回 BadValue；ApplyGcValues、SetDashes、FreeGlyphs 出错时前面的值已生效（违反「出错的请求不产生效果」）；PutImage 的 left-pad ≥ 32 不拒绝（`Graphics.cs:154-226`、`Render.cs:786-793`）。

### 遗漏与规格漂移（🟡）

<a id="dr-m1"></a>
- **DR-M1** 宽线与所有弧忽略 line-style（OnOffDash / DoubleDash 画成实线）；JoinMiter / Bevel 一律近似成圆（lw ≥ 4 的矩形边框缺角）；零长度线段 + CapProjecting 什么都不画；宽弧没有端帽（`Rasterizer.cs:645-673,730-766`）。影响 gnuplot、xfig、Tk canvas 的虚线与宽线。
- **DR-M2** Present 忽略 target-msc / divisor / remainder 与 wait-fence（`Present.cs:161,171-174`），按 MSC 控帧的客户端会空转渲染；idle-fence 无效时静默忽略；QueryVersion 不按客户端版本协商（DAMAGE 同）。
  另：PresentNotify 列表能向任意窗口的选择者放大发事件（`:163-166,202-205`），一个 16 MB 请求可以给别的客户端塞约 80 MB 的 CompleteNotify、把对方顶过 64 MB 上限断开 —— 限条数或只许通知自己的窗口。
- **DR-M3** CopyArea 源被兄弟或下级遮住、或源窗口不可见时，规范要求发 GraphicsExposure；现在直接拷缓冲里的像素（可能是别的窗口的）（`Graphics.cs:551-591`）；GC 的 subwindow-mode 对源不起作用。
- **DR-M4** RENDER 源裁剪 / alpha-map / 源的 subwindow-mode 不生效（与文档一致）：cairo-xlib、Qt5 xcb、Java XRender 都只裁目标，影响面很小。
- **DR-M5** DamageSubtract 之后重报一律只发外接矩形；RENDER CreateCursor 的热点不校验是否在图内；Await / AwaitFence 列表为空时客户端永远挂起。

### 设计（🟡）

- **DR-D1** 没有单请求工作量预算 —— 归入 [X-1](#x-1)，并在 architecture §5 写明。

### 性能（🟡，先补基准场景再动）

- **DR-P1** Rasterizer 每个请求 `Clone()` 一次可见区域再拷成 List；GC 的 ClipRects 每次 FromRects；逐点 InClip 线性扫。
- **DR-P2** 平铺填充与 PaintBackground 逐像素两次取模（`Exposure.cs:267-280`）；CopyPlane 逐像素。GXcopy + 全平面时按行从 tile 拷。
- **DR-P3** a8 / a1 目标、Disjoint / Conjoint、混合模式逐像素浮点 Decode / Encode（`RenderCompositor.cs:92-134`）。
- **DR-P4** FillPolygons 每行 RemoveAll 闭包、MergeSpans 新建 List；PolySegment / PolyRectangle 每段一个 List；CoverageMask 每条子扫描线调一次委托。
- **DR-P5** 顶层改尺寸每次新建整窗数组（`PixelBuffer.cs:88`），拖动缩放时大对象堆反复分配。
- **DR-P6** 每次绘图遍历全部 Damage 对象并走祖先链（`Damage.cs:160-187`），Damage 个数没有上限。
- **DR-P7** GetImage XYPixmap 用 `List<byte>` 逐平面 AddRange（`Graphics.cs:943-968`）。

---

## 六、输入、抓取、键盘、XInput2 与显示器（IN）

XKB 的 GetMap / GetNames / StateNotify、XI2 的 DeviceEvent / RawEvent / HierarchyEvent、RANDR 与 XINERAMA 的回复逐字段核对过；§124 修过的冻结生命周期、焦点 detail、crossing 次序、motion hint 都对。
问题集中在：**rootless 接缝**（堆叠、焦点、Warp、confine-to、锁定键、根原点）、**协议细节**（隐式抓取的 crossing、时间戳、XISelectEvents、原始事件语义）、**滥用面**（XI2 被动抓取与设备属性无上限、Bell 不节流、XTEST 无开关、抓取卡住无恢复）。

### 错误

- **IN-E1** X 堆叠与原生 z 序脱节、指针按 X 堆叠命中 —— 同 [WN-E1](#wn-e1)。

<a id="in-e6"></a>
#### IN-E6 CapsLock / NumLock 不同步 · 🟠 ✅（同 API-M1）

- **位置**：锁定位初值为 0、只随注入的锁定键翻转（`Server/X11Server.Input.cs:38`、`:696-703`）；`Input/Keymap.cs:140-145` 小键盘第 1 级是方向键、第 2 级才是数字；
  公开面没有设锁定状态的方法；宿主把锁定键当普通键转发（`Services/XServer/XInputMap.cs:84`），激活窗口时不同步。
- **触发**：Windows 台式机通常默认开 NumLock，于是在 X 程序里按小键盘得到 KP_End / KP_Up（xterm 里是移动光标）而不是数字；在 X 窗口里按一下 NumLock「修好」它，宿主自己的 NumLock 又被关掉。
  在别的程序里切过 CapsLock，回到 X 窗口大小写与键盘灯相反、一直反着。Mac 键盘没有 NumLock，小键盘在 X 里永远是方向键。
- **修法**：库加 `SetLockState(caps, num, scroll)`，走 LatchLockState 的路径（发 StateNotify 与 IndicatorStateNotify），不合成按键；
  宿主每次激活 X 窗口时按系统真实状态推一次（Windows `GetKeyState`；macOS NumLock 设开；Linux 读桌面的 XKB state）。

<a id="in-e7"></a>
#### IN-E7 显示器布局变化让根原点偏移后，已有窗口的 X 坐标不更新 · 🟠 ✅（同 API-H4）

- **位置**：`Services/XServer/AvaloniaXServerHost.cs:107-137`（`ApplyLayout` 只改 `RootOrigin` 与根窗口）；`Views/XServer/XNativeWindow.cs:134-144`（ApplyGeometry 用「新原点 + 旧 X 坐标」设原生位置）、`:262-274`（只在用户拖动时回报位置）。
- **触发**：插拔位于左侧或上方的副屏、笔记本上下扩展坞、休眠唤醒后显示器重新枚举。之后每个 X 顶层的根坐标差一个原点位移（常见就是一整块屏宽）：客户端按根坐标摆的菜单、下拉框、对话框落到别处甚至屏幕外；
  下一次客户端改尺寸触发 ApplyGeometry 时原生窗口整个跳一段。`PlaceIfUnpositioned` 同样受影响。
- **修法**：宿主在 `ApplyLayout` 之后按每个原生窗口当前位置补一次 `MoveTopLevel`；或者让 `SetScreenLayout` 带上原点平移量，由服务端一次性平移全部顶层并发合成的 ConfigureNotify。

<a id="in-e3"></a>
#### IN-E3 SetInputFocus 不看时间戳；X 焦点能被客户端移走而宿主不知道 · 🟠 🔎

- **位置**：`Input.cs:1026-1041`（不读 time、不维护 last-focus-change-time、revert-to 不校验）；`XInput.cs:93-100`（SetDeviceFocus）、`:290-295`（XISetFocus）同样；`TopLevels.cs:197-219`；宿主不看快照的 Focused 位。
- **触发**：GTK3 这类 ICCCM「Locally Active」客户端收到 WM_TAKE_FOCUS 后带时间戳回 SetInputFocus，经 SSH 有百毫秒级延迟。用户点 A（服务端给 A 发 WM_TAKE_FOCUS）、紧接着点 B（宿主 `FocusTopLevel(B)`），
  A 迟到的 SetInputFocus 照样生效 —— 焦点被拉回 A，而宿主上亮着的是 B，用户敲的字（可能是口令）进了 A。任何客户端随时 SetInputFocus 抢焦点，宿主也不会激活对应的原生窗口，用户看不出键盘去了哪儿。
- **修法**：维护 last-focus-change-time，过期或超前的请求按规范丢弃；焦点移到另一个顶层又不是宿主发起时通知宿主（新回调，或宿主响应快照的 Focused 位），由宿主决定 Activate 还是拉回。

<a id="in-e2"></a>
#### IN-E2 隐式抓取的激活与解除不发 Grab / Ungrab 模式的 crossing · 🟠 🔎

- **位置**：`Server/X11Server.GrabFreeze.cs:71-96` 只在非 Automatic 时发（注释「按钮按下时的自动抓取不发」）；`Input/Grabs.cs:57`。
- **依据**：协议「EnterNotify / LeaveNotify」一节：指针抓取激活时（在发出激活它的 ButtonPress 之前）按「从 P 瞬移到 G」发 Grab 模式的 crossing，解除时（在 ButtonRelease 之后）发 Ungrab 模式的 ❓（隐式抓取是否同样适用，对照规范原文确认）。
- **触发**：在 A 里按下、拖到另一个客户端的窗口 B 松开。抓取期间 B 上的 Enter 只报给抓取方（对），但松开后没补 Ungrab 的 Enter：B 一直不知道指针在它里面，悬停高亮不生效，要移出再移入才恢复。
- **修法**：去掉 Automatic 的豁免（抓取窗口就是指针窗口时 `GenerateCrossing` 本来就什么也不做）。

<a id="in-e4"></a>
#### IN-E4 XISelectEvents 不按 deviceid 分开存 · 🟠 🔎（真实客户端是否命中 ❓）

- **位置**：`XInput.cs:566-603`：每窗口每客户端只存 (Master, Slave) 两个掩码；选 XIAllDevices 同时覆盖两者，选任何一个具体主设备都覆盖 Master。规范要求按 (客户端, 窗口, deviceid) 分别存，投递时取并集。
- **触发**：同一客户端先在根窗口选 `XIAllMasterDevices: RawMotion | RawButton*`，再单独选一次 `XIAllDevices: HierarchyChanged`，原始事件全丢（新版 SDL2 疑似这样分两次选，待实测）；一次请求里给设备 2 选指针事件、给设备 3 选按键，指针事件全丢。
- **修法**：按 deviceid 存字典；投递时按事件的主 / 从设备，再并上 0、1 两个通配。

<a id="in-e5"></a>
#### IN-E5 XI_RawMotion 语义不对 · 🟠 🔎（对客户端的影响 ❓）

- **位置**：原始移动在 `MovePointer` 里发（`Input.cs:203-209`），核心 WarpPointer（`:1267-1284`）、XIWarpPointer（`XInput.cs:206-231`）、XTEST 都走它；valuator 声明 Abs X / Abs Y、mode Absolute（`XInput.cs:448-449,463-468`），原始值写的却是增量（`:826-831`）。
- **问题**：Warp 是服务端合成的，不算设备运动。用户移动 +10、程序 Warp 回中心，随即收到一条 −10 的原始移动，两者抵消 —— 靠「原始移动 + Warp 回中心」实现相对鼠标的程序（3D 视图、游戏、虚拟机控制台）视角不动或抖。
- **修法**：Warp 不发原始事件；轴声明与原始值自洽（改 Rel X / Rel Y + Relative，或原始值发绝对位置）。

#### 其余错误（🟡）

<a id="in-e8"></a>
- **IN-E8** 自动重复由宿主做，服务端的重复语义全缺：宿主每个 KeyDown 注入一次按下（`XNativeWindow.cs:485-503`），服务端看到「按下、按下……松开」；DetectableAutoRepeat 只登记从不读（`Xkb.cs:73,205-224`），没选它的客户端收不到规范要求的成对松开 / 按下；
  XI2 KeyPress 的 flags 恒为 0、没有 XIKeyRepeat（`XInput.cs:755`）；修饰键也被重复；`xset r off` / XkbSetControls 不生效。❓ Avalonia 的 X11 后端若没开 detectable autorepeat，Linux 宿主会转发成对的松开 / 按下（Linux 桌面上按住一个键看 xev 确认）。
- **IN-E9** 冻结队列满时丢新来的按键 / 按钮，连松开也丢（`GrabFreeze.cs:150-158`）：解冻后键或按钮卡住、隐式抓取不解除。应保留松开、优先丢按下；architecture §5 也没写这一支。
- **IN-E10** 抓取类请求一律忽略时间戳（`Input.cs:1064,1170`，AllowEvents 同），从不回 InvalidTime / Frozen；设备被 A 冻着时 B 的 GrabKeyboard(Sync) 也能成功并覆盖冻结者。
- **IN-E11** 被动抓取冲突检测只比完全相同的组合（`Input.cs:1143-1153,1209-1218`），不查 AnyModifier / AnyKey 引起的冲突；Ungrab 不拆分 AnyModifier 抓取；修饰掩码与 event-mask 不校验；核心 UngrabButton 连该客户端的 XI2 被动抓取一起删（`:1161`）。
- **IN-E12** 重放事件的 state 已经带上本次按钮 / 修饰位（`Input.cs:313-316`、`GrabFreeze.cs:360-378`），规范是事件之前的状态。
- **IN-E13** 每设备只记一个冻结者：SyncBoth 重新冻结时把另一设备的冻结者换掉（`GrabFreeze.cs:262-287`），一个抓取解除就提前解冻另一个本该冻着的设备。
- **IN-E14** RevertToParent 一路退到根时给的是 None（`Input.cs:1012-1019`），规范是退到根窗口；键盘输入被丢弃直到宿主下一次 FocusTopLevel。
- **IN-E15** XKB 推导的字母类型只认拉丁字母（`Xkb.cs:278-311`）：西里尔、希腊、Latin-2 与 Unicode 键值的字母被推成 TWO_LEVEL，CapsLock 在 XKB 客户端里无效而核心客户端里有效；单列字母不按核心规则展开；FOUR_LEVEL_ALPHABETIC 缺 Shift+Lock+Mod5（`:48`）。
<a id="in-e16"></a>
- **IN-E16** 核心请求杂项：ChangeKeyboardControl 全吞、GetKeyboardControl 的 LED 恒为 0（`xset b off`、`xset led` 无效）；SetPointerMapping 回 Success 但不生效、不发 MappingNotify（左手用户 `xmodmap -e "pointer = 3 2 1"` 静默无效）；GetPointerMapping 报 5 个按钮而 XI 报 9 个；
  SetModifierMapping 不回 Busy、不校验键码、改完不重算当前修饰状态（`Input.cs:1334-1342`）；GetDeviceKeyMapping 键码按字节回绕（`XInput.cs:101-118`）。宿主的 `BellRequested` 不看音量，音量 0 也响（API-M6）。
- **IN-E17** XI2 杂项：选 XIAllDevices 只收到主设备一份（规范是主、从各一份，`Input.cs:408-416`）；XIUngrabDevice 不校验设备；XISetFocus 传 PointerRoot 回 BadWindow；Enter / FocusIn / Touch 类被动抓取不支持却回「0 个失败」（`XInput.cs:691`）；
  XIQueryPointer 不重置 motion hint；被动抓取激活时 crossing 的 mode 不是 PassiveGrab；没有 XI_DeviceChanged、XI_PropertyEvent；核心的 do-not-propagate 截断了 XI2 的传播（`Input.cs:468`）。
<a id="in-e18"></a>
- **IN-E18** RANDR / XINERAMA 杂项：SetScreenSize 一律 BadAccess（规范下同尺寸应成功、其它 BadValue，`RandR.cs:242-255`）；SetCrtcGamma / SetOutputPrimary 回 BadAccess —— 用 Xlib 默认错误处理的程序会因此退出，建议静默接受；
  点时钟 32 位（`:325`，8K@144 溢出）；DPI 变化不发 ScreenChangeNotify；CRTC / 输出 ID 按下标分配（拔一台后 ID 指向另一台）；XINERAMA 的 QueryScreens 把主屏排第一、GetScreenSize 却按原下标（`Xinerama.cs:39-64`）；宿主不截断超过 16 台的显示器，`NormalizeMonitors` 的异常落到 UI 线程。
- **IN-E19** macOS 上 Cmd 组合键可能收不到 KeyUp ❓：X 端认为那个键一直按着，由它激活的被动键抓取也不解除。macOS 上按 Cmd+C 后 QueryKeymap 确认。

### 安全

<a id="in-s1"></a>
#### IN-S1 XIPassiveGrabDevice：无界增长、O(n²)、不查跨客户端冲突 · 🟠 ✅

- **位置**：`Server/X11Server.XInput.cs:653-705`（重点 `:672-692`）：detail 任意 32 位不校验；每个修饰组合都对该窗口整张表 `RemoveAll`；不像核心 GrabKey 那样查别的客户端的同一组合，回复永远「0 个失败」。
- **触发**：恶意客户端在根窗口上换着 detail 反复发，每个请求带 257 个修饰组合（约 1 KB）。约 4000 个请求后表长 10⁶，单个请求就是数亿次比较；之后每次按键、按钮 `FindPassiveGrab`（`Input.cs:804-830`）都线性扫这张表。
- **修法**：校验 detail 范围（BadValue）；被动抓取按 (detail, mods) 建字典；每窗口、每客户端上限（BadAlloc）；补冲突检测并把冲突的组合放进回复。

<a id="in-s3"></a>
#### IN-S3 XTEST、XI2 原始按键、XIChangeHierarchy 对经 SSH 来的连接没有开关 · 🟠 ✅（设计）

- **位置**：`Server/X11Server.Extensions.cs:81,133` 无条件注册；`X11ServerOptions` 没有相关选项；`Input.cs:679` 原始按键不看焦点，直接发给在根窗口上选了它的客户端。先例：`Extensions.cs:158` 的 MIT-SHM 已用 `VisibleTo = client.SameHost` 控制可见性。
- **问题**：architecture §7 提醒「所有 SSH 会话共享一个受信的显示」，而 XTEST 是放大器 —— SendEvent 合成的按键带 send_event 标志、xterm 默认丢弃；XTEST 伪造的输入与真实键盘无从区分，一台被攻破的远端机可以往别的会话的 xterm 注入命令（横向移动到另一台主机）。
  XI_RawKeyPress 让任何客户端不抢焦点就能记下所有 X 窗口里敲的键。XIChangeHierarchy 能让物理输入设备失效（IN-D1）。
- **修法**：加选项（或给 `ServeAuthenticatedAsync` 加参数），决定这三项对经 SSH 来的连接是否可见，复用 `IsVisibleTo`。默认值是产品决定（远程用 xdotool 是已验证的场景），但至少要有开关 —— 见 Q5。

<a id="in-s4"></a>
#### IN-S4 Bell 不节流 · 🟠 🔎

- **位置**：三个响铃入口（`Input.cs:1347-1368`、`Xkb.cs:89-100`、`XInput.cs:159-162`）→ `DeferredHost.cs:37` 每次攒一个闭包 → `AvaloniaXServerHost.cs:292` 每次往 UI 线程 Post 一个 `SystemSound.Alert`。
- **触发**：循环发 4 字节的 Bell，每 4 ms 一批就是成千上万次 Post —— 整个 VelaShell 界面卡死、内存上涨。
- **修法**：DeferredHost 每批最多交一次 BellRequested 再按时间节流；宿主音量为 0 时不出声。（与 WN-S8 一起做）

<a id="in-s2"></a>
#### 其余安全（🟡）

- **IN-S2** XIChangeProperty：设备属性没有上限、Append / Prepend 每次整份拷贝、设备属性是全局的断开不还（`XInput.cs:362-379,510-525`）；format 16 / 32 不做字节序转换、不发 XI_PropertyEvent、类型或格式不匹配时直接替换（应 BadMatch）。套用窗口属性的上限与原子计数。

### 设计

<a id="in-d1"></a>
#### IN-D1 抓取、冻结、设备浮动卡住时宿主没有恢复手段 · 🟠 🔎

- **位置**：`TopLevels.cs:248-262`（支持 WM_DELETE_WINDOW 时关闭只发 ClientMessage，没有 `_NET_WM_PING`、没有强制断开）；公开 API 没有打断抓取或断开客户端的方法；
  `XiHierarchy.cs:191-203`（DetachSlave 是全局状态，不随发起它的客户端断开而恢复），浮动后不再产生核心事件（`Input.cs:347-350`）。
- **触发**：
  1. 远端程序的菜单开着（抓着指针与键盘）时 SSH 断网、笔记本睡眠或远端进程被 SIGSTOP —— 连接没断，抓取就一直在，**所有会话的所有 X 窗口**点不动、打不了字，直到 SSH 保活超时（没开保活就是永远）；点原生窗口的关闭按钮也没用。GrabServer 同理（CN-M5）。
  2. 任何客户端执行一次 `xinput float 4`，核心鼠标在所有 X 程序里永久失效，断开也不恢复。
- **修法**：宿主 API 加 `BreakGrabs()`（解除全部抓取、解冻、复位设备拓扑）与 `KillTopLevelClient(window)`；实现 `_NET_WM_PING`，让宿主能弹「无响应，强制关闭？」（F3）。

- **IN-D2** WarpPointer：src-window 与源矩形读了就丢、不校验 BadWindow；结果不夹到根窗口；负坐标撞上「指针离开」的 −1 哨兵（`Input.cs:206,297,1267-1284`）；XIWarpPointer 倒是判断了源矩形，两条路不一致；confine-to 不实现（`Grabs.cs:20`）；
  Warp 不经冻结队列，冻着时它的 MotionNotify 越过排队的事件先到；宿主不知道 Warp，服务端的指针与真实光标分叉（CP-5）。🟠 🔎 —— 修法见 F8。
- **IN-D3** 键盘布局只在激活 X 窗口时重推（在 X 窗口里切系统布局，继续敲出的仍是旧布局）；`SetKeymap` 整张覆盖修饰键表（`Input.cs:128-135`），用户用 xmodmap 交换 Caps / Ctrl 的设置会被还原。🟡

### 遗漏与规格漂移

<a id="in-m1"></a>
#### IN-M1 JIS / ABNT2 的 Ro、Yen 键没有映射 · 🟠 ✅

- **位置**：`Services/XServer/XInputMap.cs:14-128` 没有 IntlRo / IntlYen（KanaMode、Convert、NonConvert、Lang1 / Lang2、F13–F24、NumPadEqual、多媒体键也没有）；`HostKeymap.cs:44-51` 只按布局推导键码 10–61 与 94；
  `Input/Keymap.cs` 的 US 表与 `Host/XKeycodes.cs` 都没有键码 97 与 132（`XkbKeyNames.cs` 倒是给了 AB11 / AE13 的名字）。
- **触发**：JIS 键盘上的 `\`、`_`（Shift+Ro）、`|`（Shift+Yen）在 X 程序里按了没反应 —— shell 里下划线极常用；ABNT2 键盘右 Shift 旁的 `/ ?` 同样失效。
- **修法**：XInputMap 补 IntlRo → 97、IntlYen → 132；HostKeymap 把这两个键码纳入按布局推导；XKeycodes 补常量；顺带补 F13–F24、日韩专用键与多媒体键。

#### 其余遗漏（🟡）

- **IN-M2** 屏保与空闲时间和宿主脱节（`ScreenSaver.cs:28-38`）：用户整小时在宿主终端里打字，远端程序仍认为他离开了；视频播放器的 ForceScreenSaver(Reset) / Suspend 也不转告宿主（F9）。
- **IN-M3** 指针离开所有顶层后 QueryPointer 报 (−1, −1) / (0, 0)、按键事件的 root-x 为 −1（`Input.cs:86,501,1262`）。
- **IN-M4** architecture.md 没写：confine-to 不实现、Warp 不动宿主光标、自动重复由宿主做、锁定键不同步、X 堆叠不跟原生 z 序、冻结队列满时丢新来的按键。`Input.cs:786-791` 有两个重复的 `<summary>`。

### 性能（🟡）

- **IN-P1** `SendXi2Crossing` 每次 LINQ + ToArray（`XInput.cs:784-788`）、CommonAncestor 每次建 HashSet、UpdateModifierState 每次扫 248 个键、被动抓取线性扫 —— 修 IN-S1 时一起改成字典。

---
## 七、GLX 与间接渲染的软件 GL（GL）

GLX 1.4 协议层（4 个 FBConfig、上下文标签按客户端分表、Render / RenderLarge / Single 分派）与软件 GL（固定功能 1.1 子集）整体严谨；
`Gl/` 里没有 `unsafe`，`GlReader` 越界返回 0，所有越界最终都是托管异常 → BadImplementation，**经典意义上的越界读写不可能**。
真正的风险在**无界 CPU / 内存**与**未捕获异常**两类；§124 的 GLX 修复本身都在位，但有几处修得不完整（S1、S3、E1）。

### 安全

<a id="gl-s1"></a>
#### GL-S1 显示列表的执行预算只按「条数」计，不按工作量计 · 🔴 ✅ 🧪

- **位置**：`Gl/GlContext.cs:195`（`ListCommandBudget = 4_000_000`）、`:424-467`（`CallList` / `Spend` 每条命令扣 1）；执行请求时一直持像素锁（X-1）。
- **问题**：§124 修的是「空列表也计数」，没修「每条命令的代价无界」：Clear 是表面面积，大三角形 / Rect 是视口面积 × 每片元开销，一条 DrawArrays 可以 2¹⁹ 个顶点，DrawPixels / CopyPixels 是 4096²。
- **实测**：一个只含 `Clear(COLOR|DEPTH)` 的列表 A，再一条 `CallLists(n, UNSIGNED_BYTE, A…)`：1024² 表面上每次调用 0.234 ms，4 M 次约 **16 分钟**；4096² 的 Pbuffer 上每次 9.4 ms，约 **10.5 小时**。
  X 核心协议的代价大致是「请求字节数 × 绘图面积」，线性；显示列表把一个小请求放大了 4 M 倍。
- **修法**：预算改成工作量单位 —— Clear / Fill 按裁剪后像素数、三角形按包围盒与裁剪框相交的面积、顶点按个数、DrawPixels / CopyPixels / Bitmap / TexImage 按处理的像素数；
  每个请求一个总额（如 2²⁸），超了记 OUT_OF_MEMORY 并停止；ExecuteStream 也计入同一份预算。

<a id="gl-s2"></a>
#### GL-S2 DrawPixels：ROW_LENGTH < width 时数据装得下的检查约束不了 width × height · 🔴 🧪

- **位置**：`Gl/GlContext.Pixels.cs:245`（`groups = RowLength > 0 ? RowLength : width`）、`:223-224`（`Covers`）、`:640-653`（先解完一整行再 `DrawRow`）、`:672-681`（`VisibleRange` 在 zoom 很小时返回整个 `[0, count)`）。
- **问题**：ROW_LENGTH = 1、alignment = 1、LUMINANCE / UNSIGNED_BYTE 时各行在数据里重叠，`Covers` 只要求 w + h − 1 字节；PixelZoom 设 1e-6 后每个源像素都「可能可见」，外层循环对每一点都 `ReadGroup`，`DrawRow` 发现一像素都盖不住才返回 —— 解码已经做完。
- **实测**：8000×8000 的图只要 16 KB 数据，耗时 4.2 秒；一条 Render 命令最大 64 KB 可放 32000²（约 10⁹ 次解码）；一个 16 MB 请求塞 250 条就是几小时，RenderLarge（64 MB）到 10¹² 量级。
- **修法**：按目标像素反向映射，只解码「放大后的矩形含有像素中心」的源像素（每行先算 y0 / y1、空行不解码，每列预算 x 范围），代价不超过裁剪面积；DrawPixels 计入 GL-S1 的工作量预算。

<a id="gl-s3"></a>
#### GL-S3 文档写的 GLX 上限是「每个共享组」的，还有几处根本没记账 · 🟠 🔎

- **默认纹理（名字 0）不记账**：`Gl/GlContext.Pixels.cs:50-55`（`FitsTexture` 遇 `!IsNamed` 直接放行）、`:392-397`（不带数据的 TexImage2D 也分配 `new byte[w*h*4]`）。默认 2D 纹理 12 级、每级 2048²，合计 192 MB，约 12 条 60 字节的命令，绕过 256 MB 的账。
- **上限按共享组（≈ 按上下文）计**：每个不带 share list 的 CreateContext（24 字节）新建一个 `GlShared`（`GlxExtension.cs:403`），各带 64 MB 列表、256 MB 纹理额度；每个上下文还能在 Begin / End 之间攒 2¹⁹ 个顶点（约 56 MB）；Pbuffer 与 GLX 像素图表面每个至多 4096² × 13 B ≈ 218 MB、个数不限。
- **`Enable` 接受任意值**（`Gl/GlContext.Commands.cs:351,553-560`），`State.Enabled` 无界增长；`PushAttrib` 每次整份克隆 State（深度 16，`GlState.cs:145-158`），16 MB 的 Enable 流 + 16 条 PushAttrib ≈ 1 GB。按 GL 规范应报 INVALID_ENUM。
- **空显示列表不占字节账**、`GlShared.SetList`（`GlContext.cs:132-137`）不查个数：NewList(任意名) + EndList 可建无限多个；超过 65536 后 `GenLists` 永远返回 0。
- **修法**：归入 [X-2](#x-2) 的每客户端账（表面 + 纹理含默认纹理 + 列表 + 图元缓冲），Enable / Disable 只认识的 cap，每个列表计一份对象开销；architecture §7 写明上限的粒度。

#### 其余安全（🟡）

- **GL-S4** 两条只靠畸形请求触发的异常路径 🧪：CopyTexSubImage2D 不校验 target，`PROXY_TEXTURE_2D` 的代理级图像 `Texels` 是空数组 → IndexOutOfRange（`Pixels.cs:492-511`）；CopyPixels 的 x 或 y 为 `int.MinValue` 时 `(int)Math.Max(i0, -(long)x)` 回绕 → ArgumentOutOfRange（`:785-794`）。
- **GL-S5** TexImage / TexSubImage 不核对数据长度（`Pixels.cs:398-401,447`）：1 字节数据也按 2050² 解码、分配约 67 MB 的临时 `Vector4[]`。照 DrawPixels 先 `Covers`（客户端传 NULL 时仍允许）。
- **GL-S6** GetTexImage 的回复大小不设上限（`GlxExtension.cs:992-1001`）：2048² RGBA 按 FLOAT 取是 64 MB，正好顶到输出积压上限、客户端被断开。ReadPixels 已有 `PackedSize` 守卫，这里漏了。

### 错误

<a id="gl-e1"></a>
#### GL-E1 GLX 像素图的表面在 FreePixmap 之后不释放 · 🟠 🔎

- **位置**：`_glxSurfaces` 只在四处删（`GlxExtension.cs:323` DestroyPbuffer、`:526` ID 被重用、`:725` 客户端断开、`:739` 窗口销毁）；`FreePixmap`（`Server/X11Server.Graphics.cs:65-73`）没有 GLX 钩子；表面项还持有 `XPixmap` 本身（含像素缓冲）。
- **触发**：每帧（或每张缩略图）新建像素图 → 建 GLX 像素图 → MakeCurrent → 画 → 释放。Xlib 的 XID 递增、很久才重用，每轮泄漏一份表面加一份像素缓冲（512² 约 4.4 MB），直到客户端断开。现有用例只覆盖「ID 被重用」。architecture §7 写的是「表面随可绘对象释放」。
- **修法**：加一个「资源释放」钩子（类似 `WindowDestroyed`）或 FreePixmap 通知 GLX；或表面挂在 `XPixmap` 对象上随它的生命周期走。

<a id="gl-e2"></a>
#### GL-E2 绑定的纹理名已经不存在时 TexImage / CopyTexImage 抛 NullReferenceException · 🟠 🧪

- **位置**：`Gl/GlContext.Pixels.cs:375`、`:476`（`BoundTexture(target)!`）。名字悬空的三条来路：① `PushAttrib(TEXTURE_BIT)` → `DeleteTextures(当前绑定)` → `PopAttrib`，`State.Restore` 把已删的名字恢复回来（`GlState.cs:258`）；
  ② `CopyContext`（`GlxExtension.cs:208`）把源共享组的纹理名拷进目标共享组；③ 共享组里另一个上下文删了它（`DeleteTextures` 只重置本上下文的绑定，`GlContext.cs:510-517`）。
- **后果**：客户端收到 BadImplementation，Xlib 默认错误处理让程序直接退出 —— 而第 1 条是合法的 GL 序列。
- **修法**：`BoundTexture` 拿到 null 时按「绑定已退回 0」处理或照 BindTexture 的语义新建对象，不用 `!`。

#### 其余错误（🟡）

- **GL-E3** PolygonOffsetEXT 的 bias 按 `units / 2²⁴` 处理（`Commands.cs:476-479`、`Vertex.cs:747`），等于没有偏移；EXT_polygon_offset 的 bias 直接以深度范围为单位。走 EXT 入口的老程序出现 z-fighting。
- **GL-E4** 枚举与 Begin / End 内的合法性大多不校验：PolygonMode、DepthFunc、Stencil 函数与操作、BlendFunc、DrawBuffer、ReadBuffer、TexEnv / Fog mode、TexParameter 收到垃圾值直接存（DepthFunc 落到 default 等于 ALWAYS）；
  Begin / End 内的 Enable、矩阵操作、PushAttrib 照常执行而不是 INVALID_OPERATION；DrawArrays 在 Begin / End 内把顶点并进外层图元并替外层执行 End（`Vertex.cs:964-968`）；CallLists 的 FOUR_BYTES / FLOAT 恰好解出 0xFFFFFFFF 时被误判 INVALID_ENUM、n < 0 不报 INVALID_VALUE。
- **GL-E5** 边标记不起作用（`State.EdgeFlag` 有人写没人读，`Vertex.cs:568-611` 按图元类型写死）：GLU 镶嵌器的内部对角线在 `PolygonMode(LINE)` 线框下全画出来；GL_CLAMP 配 LINEAR 时不与边框色混合。
- **GL-E6** DrawArrays 按固定槽位次序读每个顶点的数据，不按 ARRAY_INFO 的出现顺序（`Vertex.cs:933-1012`）❓：客户端按别的顺序列数组时分量错位。`LIBGL_ALWAYS_INDIRECT=1` 跑颜色数组 + 顶点数组的小程序对照像素确认。
- **GL-E7** 当前可绘对象没了、或窗口太大时的错误语义：窗口在上下文仍为当前时被销毁，此后每个请求抛 GLXBadDrawable / GLXBadWindow（`:474,489`），规范给的是 GLXBadCurrentWindow / GLXBadCurrentDrawable；
  窗口放大到超过 4096²（8K 屏、跨屏最大化）后每个 GL 请求回 BadAlloc（`:496-499`），程序因 X 错误退出 —— 建议表面夹到上限、只渲染一块并记日志（Q9）；
  GLX 1.2 写法下同一窗口的表面配置由第一个绑上来的上下文决定（`:500-509`），之后双缓冲的上下文绑到单缓冲表面时 SwapBuffers 什么也不做、每个 Render 直接上屏（撕裂）。

### 遗漏与规格漂移

<a id="gl-m1"></a>
#### GL-M1 没有 GLX_ARB_create_context / _profile · 🟠 ✅（代码）/ ❓（各程序的反应）（同 CP-20）

- **位置**：`Server/GlxExtension.cs:375-376`（33 SetClientInfoARB、34 CreateContextAttribsARB、35 SetClientInfo2ARB 落到 default 回 BadRequest）；`:380` 的扩展串只有 `GLX_ARB_get_proc_address GLX_EXT_visual_info GLX_EXT_visual_rating`；错误编号已预留 GLXBadProfileARB 却没用上。
- **问题**：走 drisw 的直接渲染客户端本可以用 llvmpipe 跑 GL 4.5，但要求核心 profile（3.2+）或 3.x 兼容 profile 的程序（GLFW / SDL / Qt 的 CoreProfile、Blender、Godot、ParaView、VTK 新版）只能经 glXCreateContextAttribsARB 建上下文，拿不到。
- **确认**：`velashell-xclients` 里 `glxinfo -B` 的直接路径输出有没有「OpenGL core profile version string」；再跑一个只要 3.3 core 的 GLFW 小程序或 glmark2。
- **修法**：声明这两个扩展；34 号请求对直接上下文只登记，间接上下文只接受 1.x 兼容 profile、否则回 GLXBadProfileARB；33 / 35 号接受并忽略内容。工作量小（约 150 行加用例）。

#### 其余遗漏（🟡）

- **GL-M2** 扩展串声明了 GL_EXT_texture_object，但它的 GLX 编码走 VendorPrivate（厂商码 11–14），被一律回 GLXUnsupportedPrivateRequest（`Query.cs:24-27`、`GlxExtension.cs:253-255`）。映射到 Single 143–146，或从串里拿掉。
- **GL-M3** 选择 / 反馈没有实现：`RenderMode(SELECT)` 之后什么都不画、切回时命中数恒为 0，老 CAD 的 gluPickMatrix 拾取**静默落空**。求值器（Map / EvalMesh）静默吃掉、GetMap 回 INVALID_ENUM（GLUT 茶壶、SGI GLU 的 NURBS 一片空白）。
  architecture §7 列的「不实现」也不全：像素传输的缩放 / 偏置与 PixelMap（静默忽略）、深度 / 模板 / 颜色索引格式的 DrawPixels 与 CopyPixels、边标记、GL_CLAMP 边框色、点 / 线 / 多边形平滑、Hint 查询 —— 补进文档。

### 设计（🟡）

- **GL-D1** 跨客户端：任意客户端可以用别人的上下文做 share list（读到对方的纹理与显示列表）、MakeCurrent 别人的上下文、CopyContext / DestroyContext 别人的。上下文标签按客户端分表，伪造标签不可行。
  这与 X 核心「客户端之间不隔离」的信任模型一致，不是越权；做 F2 的非受信级别时这几处要一起收紧。

### 性能（🟡）

- **GL-P1** 每个三角形约 650 B 垃圾（裁剪用的 `List<(GlVertex, bool)>`、`ActivePlanes()` 迭代器、`RasterVertex[]`，`Vertex.cs:649,656,764`）；每顶点约 14 次 `HashSet.Contains`；COLOR_MATERIAL 时 `MaterialsFor` 每顶点新建数组。派生状态缓存 + struct 裁剪器 + `stackalloc`。
- **GL-P2** 光栅化按包围盒逐像素算浮点边函数，细长斜三角形白扫整个包围盒；每片元插值全部属性；每片元调一次 `MarkFrontDirty`（`Raster.cs:141-170,321`）。扫描线 + 增量边函数 + 无纹理 / 无混合快路径。
- **GL-P3** `CallList` 每次 `commands.ToArray()`（`GlContext.cs:438`，执行期间列表不可能被重定义，复制不必要）；`PushAttrib` 不看 mask 整份克隆 State。
- **GL-P4** GLX CreateWindow 每次扫 `server.AllResources` 全表（`GlxExtension.cs:355`，n 由攻击者控制）；RenderLarge 拼接复制四次（64 MB 的命令峰值约 256 MB）；GenLists 每次对全部名字排序、GenTextures 每次从 1 线性探测。
- **GL-P5** 双缓冲每次交换整窗拷贝、整窗记损伤（`GlSurface.cs:107-112`）；单缓冲那边已经只拷画过的外接矩形，照做。

---

## 八、公开面与宿主（API）

§116 整理之后，公开面的形状是干净的：回调命名、句柄、快照、DeferredHost、PixelGate 让行都到位，库没有可变静态状态、多实例安全。
缺口集中在两类：**宿主当窗口管理器所需的信息与动作不全**（锁定键、强制结束、客户端身份、堆叠、工作区、重力），以及**没有可用的隔离档**（X-3）。
路径相对 `src/VelaShell/`（宿主）或 `src/VelaShell.XServer/`（库），另注。

### 宿主侧的问题

<a id="api-h1"></a>
#### API-H1 原生 owner 级联关闭：父窗口变幽灵；关弹层被当成关闭按钮，整个 X 程序被断开 · 🟠 ✅ 🧪

- **位置**：`Services/XServer/AvaloniaXServerHost.cs:353-363`（瞬态窗口与 override-redirect 弹层都 `Show(owner)`，弹层的 owner 取「当前活动的 X 窗口」）、`:212-218`（先从 `_windows` 摘掉再 `CloseByHost`）、`:91-102`（`Detach`）；
  `Views/XServer/XNativeWindow.cs:372-382`（`OnClosing` 不看 `CloseReason`，一律 Cancel 再 `CloseTopLevel`）；库 `Server/X11Server.TopLevels.cs:248-262`（没声明 WM_DELETE_WINDOW 就断开整个客户端，不区分 override-redirect）。
- **Avalonia 12.1.3 的语义**（Headless 探针实测）：owner 关闭时先以 `OwnerWindowClosing` 询问每个子窗口；只要有子窗口 Cancel，owner 的关闭整体取消、owner 自己的 Closing 也不触发。而 XNativeWindow 对所有非宿主发起的关闭都 Cancel。
- **触发**：
  1. **停 X Server（必现）**：`Detach` 按映射顺序关窗口，有对话框或菜单开着的程序，父窗口关不掉；`_windows.Clear()` 之后再没人管它，屏幕上留下一个空壳窗口。
  2. **程序退出**：服务端按资源表顺序先销毁主窗口，`TopLevelUnmapped(主)` 先到；对话框 Cancel，主窗口成了幽灵。
  3. **弹层**：弹层的 `OnClosing` 调 `CloseTopLevel(弹层)`，弹层还活着（GTK 的 tooltip / 菜单取消映射后复用），库走 `ApplyClose` → override-redirect 窗口没有 WM_DELETE_WINDOW → **整个程序被断开**。
     弹层的 owner 是「当前活动的 X 窗口」，可能属于别的客户端甚至别的会话 —— 关 A 的窗口可能杀掉 B。弹层被单击激活后按 Alt+F4 也走这条路。
- **修法**：宿主只把 `WindowCloseReason.WindowClosing` 转成 `CloseTopLevel`，`OwnerWindowClosing` / `ApplicationShutdown` / `OSShutdown` 一律放行；关父窗口前先关掉或去 owner 重显子窗口；
  弹层的 owner 用 `TransientFor` 或不设 owner；库的 `CloseTopLevel` 对 override-redirect 窗口静默忽略（与 `FocusTopLevel` 一致）。补无头用例。

<a id="api-h2"></a>
#### API-H2 OnClosing 不看 CloseReason：拦住注销与关机；关掉主窗口后进程不退 · 🟡 ✅（代码）/ ❓（各平台表现）

- 系统注销或关机时 X 窗口 Cancel，表现为「VelaShell 阻止关机」（对照 `MainWindow.axaml.cs:883-887` 主窗口刻意不拦）。用户关掉主窗口（不是缩到托盘）后只要还有 X 窗口进程就不退（`Program.cs:96` 默认 OnLastWindowClose）：`desktop.Exit` 的停服与 `.Xauthority` 收尾都不执行。
- 修法同 H1；主窗口真正关闭时先停内置服务端。确认：开着 xterm 在 Windows 上注销。

<a id="api-h3"></a>
#### API-H3 Linux 上用户拖边框缩放，X 客户端收不到新尺寸 · 🟠 ✅（代码）🧪（IL 扫描）

- **位置**：`Views/XServer/XNativeWindow.cs:276-295`：第 284 行只认 `WindowResizeReason.User` 或窗口状态变化。
- **问题**：IL 扫描 Avalonia 12.1.3：`Avalonia.X11.X11Window` 在 ConfigureNotify 处理与 `OnPropertyChange` 里一律传 Unspecified（只有 XEmbed 路径传 User）；仓库没启用 Avalonia.Wayland（`src/Directory.Packages.props:14` 被注释），Linux 上走的就是 X11 后端。
  结果：用户拖大窗口，X 缓冲还是原尺寸、多出来的地方是空的；拖小则内容被裁。最大化 / 还原能回报。macOS 的 Reason 由 libAvaloniaNative 给 ❓（实机确认）。
- **修法**：不靠 Reason 区分。记下 `ApplyGeometry` 最后请求的像素尺寸，Resized 到来时 `ClientSize × Scale` 与它不同就当用户缩放回报。

- **API-H4** 显示器拓扑变化后已有窗口的 X 坐标不重新同步 —— 同 [IN-E7](#in-e7)。

<a id="api-h5"></a>
#### API-H5 剪贴板默认双向自动同步，PRIMARY 也默认开 · 🟠 ✅

- **位置**：`Services/XServer/AvaloniaXServerHost.cs:398-425`（激活任一 X 窗口就把系统剪贴板推给服务端）；库 `Server/X11Server.Clipboard.cs:77-89`（服务端占有 CLIPBOARD 后任何客户端都能读）、`:167-175`、`:266-275`（X 客户端一占有选区服务端就取回写进系统剪贴板）；
  `VelaShell.Core/Models/AppSettings.cs:1482-1493`（`Clipboard` 默认开、`CopyOnSelection` 默认也开），于是 `Infrastructure/XServer/BuiltInLocalXServer.cs:164` 的 `SyncPrimary = true` —— 与库自己的默认值（关）相反。
- **问题**：用户在本机密码管理器里复制了密码、随后点一下任意 X 窗口，**所有会话的所有 X 客户端**都能读到；PRIMARY 还会被鼠标中键随手粘出去。反过来，任意远端 X 客户端可以反复改写本机剪贴板（pastejacking），用户再往本机终端或别的会话里粘贴时中招。设置页文案没提这层风险。
- **修法**：`CopyOnSelection`（对 X 的 PRIMARY 同步）默认关；host → X 只对拥有键盘焦点的客户端可读（库按请求方客户端过滤）；X → host 只接受当前焦点客户端的变化；设置页写明风险、支持按会话关闭同步。见 Q3。

- **API-H6** 远端程序可以无提示地抢焦点、置顶、全屏，外观与本机窗口无法区分 —— 同 [WN-S4](#wn-s4)。

#### 其余宿主侧问题（🟡）

- **API-H7** 最大化 / 全屏时客户端自己改尺寸不被纠正（`XNativeWindow.cs:122-125` 直接返回），X 缓冲与原生窗口从此不一致。这时把原生尺寸用 `ResizeTopLevel` 推回去。
- **API-H8** 停服后立即重启的竞态 ❓：UI 队列里还排着旧服务端的 `TopLevelMapped`，会用旧句柄建原生窗口；之后 `Inject*` 在 UI 事件里抛 ArgumentException；新服务端的 XID 与旧的重合，旧回调可能误关新窗口（`AvaloniaXServerHost.cs:31` 按 uint Id 存窗口）。回调里校验句柄属于当前服务端（库缺 `XTopLevelWindow.Server` / `IsAlive`），字典按句柄引用做键。
- **API-H9** 切换引擎后老会话的 x11 通道被拒：`SshForwardingOptions.cs:79` 开 shell 时就绑了内置引擎的连接器，用户改用 VcXsrv 后老会话的新 X 程序都 `Failed to open display`（`BuiltInLocalXServer.cs:319-323`）。取不到内置服务端时按显示地址走 TCP。
- **API-H10** `StartCoreAsync` 的异常过滤太窄（`BuiltInLocalXServer.cs:174` 只接 SocketException / OCE / InvalidOperationException）：别的异常时服务端不释放、状态卡在 Starting。catch 全部、统一收尾。（同 CN-E10）
- **API-H11** Linux 上每次激活窗口都在 UI 线程上 `xcb_connect` 桌面并完整拉一遍 XKB 表（`LinuxKeymap.cs:35,67-69`），Windows 有布局句柄短路、Linux 没有；DISPLAY 指向慢的显示时切窗口会卡。
- **API-H12** 多用户 Windows 上转发可能落到别人的 X 服务端 ❓：`BuiltInLocalXServer.cs:348-351` 只要看到 `localhost:0` 有人监听就不自动启动、转发退回 `localhost:0.0` —— 终端服务器上那可能是别的用户开着 `-ac` 的 VcXsrv。
- **API-H13** 小项：Shape 以外透明了却仍接收鼠标（`XNativeWindow.cs:256-259`，与快照文档不符）；`_NET_WM_WINDOW_OPACITY` 用在不透明窗口上混出背景色 ❓；首帧按外框为 0 摆放、`Opened` 之后才修正，窗口跳一下；Urgent 不闪任务栏；
  光标放在 ConditionalWeakTable 里不 Dispose 原生句柄 ❓；分数缩放取整后最后一列可能被裁 ❓；停 X Server 不确认、不提示会断开几个程序（`XServerToggleViewModel.cs:110-123`）。

### 库的公开面问题

<a id="api-m2"></a>
#### API-M2 没有「强制结束」、客户端枚举、按客户端断开，也没法给连接打标签 · 🟠 🔎

- **位置**：`Server/X11Server.cs:307-317`（`CloseTopLevel` 是唯一的关闭手段）；`XClient.ToString` 只给 `client#N`；`Infrastructure/XServer/BuiltInLocalXServer.cs:311-327`（连接器不带会话信息）。
- **问题**：① 客户端声明了 WM_DELETE_WINDOW 却卡死，用户关不掉窗口，只能停掉整个 X Server，所有会话的程序一起断；没有 `_NET_WM_PING`，也无从判断「无响应」。② Retain 客户端的窗口永远关不掉（CN-E2）。
  ③ 宿主列不出「谁连着、来自哪个会话、占多少内存」，停服前没法提示「会断开 N 个程序」，日志也对不上是哪个会话。
- **修法**：`KillTopLevelClient(window)`（覆盖 Retain 客户端）；`_NET_WM_PING` + 回调 `TopLevelNotResponding`；`ServeAuthenticatedAsync` 接受一个标签；`Clients` 快照（X-Resource 已有这些统计）与 `DisconnectClient`；`XTopLevelSnapshot` 加 `Client` 字段；`BreakGrabs()`（IN-D1）。见 F3。

<a id="api-m3"></a>
- **API-M3** 快照缺宿主需要的信息 🟡：归属客户端、WM_CLASS 的 instance、window_group、USPosition / PPosition、win_gravity、base size、aspect、BorderWidth、输入形状、UserTime、STRUT、SYNC 计数器、堆叠次序。
  直接后果：`PlaceIfUnpositioned`（`AvaloniaXServerHost.cs:369-395`）只能用 `X==0 && Y==0` 猜「没给位置」，于是 `xterm -geometry +0+0` 被挪到屏幕中央；宿主一律让内容区对准请求坐标，而按 ICCCM §4.1.2.3 默认 NorthWest 重力是**外框**对准，请求 y = 0 的窗口标题栏被摆到屏幕外。
- **API-D1** `SetTopLevelStates` 整组覆盖（`TopLevels.cs:265-269`、`XNativeWindow.cs:352-369`）：第一次最大化 / 最小化时客户端映射前设的 SkipTaskbar、Modal、Sticky、Below、DemandsAttention 被清空，本不进任务栏的窗口出现在任务栏里。加 `ChangeTopLevelStates(add, remove)`。🟡
- **API-E1** `_NET_WM_MOVERESIZE` 之后投递一个孤立的 ButtonRelease：`Ewmh.cs:344` 已把按钮记成松开，而 `ButtonEvent` 松开时不查按钮是否按着（`Input.cs:308-334`），宿主拖动结束后又补一个松开。🟡
- **API-E2** 顶层 border_width > 0 时内容位置差 bw 像素、边框不画：快照的 X / Y 是外边框角（`XTopLevelSnapshot.cs:17`）却没有 BorderWidth，宿主把 X / Y 当内容区摆，而注入坐标时加了 bw（`Input.cs:55-58`）。影响 Xt / Motif 的菜单、`xterm -bw`。🟡
- **API-E3** `ReadPixels` / `CopyPixels` 的文档说取消映射后返回 false / (0,0)（`Host/XTopLevelWindow.cs:51,82`），实际缓冲只在销毁与 reparent 时置 null。改文档（`XNativeWindow.cs:696` 的注释一并改）。🟡
- **API-D2** 异常契约不一致：`ServeAsync` / `ServeAuthenticatedAsync` 遇 null 或已释放给 faulted Task 而不是当场抛；`Inject*` 的坐标不校验而 `MoveTopLevel` 校验，负坐标与「指针离开」的 −1 哨兵混用；`CloseTopLevel` 不忽略 override-redirect 而 `FocusTopLevel` 忽略；`StartAsync` 失败后不能重试、文档没写。🟡
- **API-D3** 生命周期：`DisposeAsync` 的第二个调用者立即返回、不等第一个收完；`StartAsync` 与 `DisposeAsync` 并发时可能泄漏监听器与套接字文件；停服时不对仍映射的顶层发 Unmapped，接口与 `DisposeAsync` 的注释都没写这条契约；没有 Completion / Faulted 通知。🟡
- **API-D4** 选项校验：空 cookie（CN-S7）；`Monitors` 不查是否落在根窗口内（`Monitors.cs:26-44`）；`XKeymap.AltGr = true` 但列数 < 5 时不报错。🟡
- **API-D5** 命名交叉：`SetTopLevelStates` / `SetTopLevelFrameExtents` 同时占了 `Set*` 与 `*TopLevel` 两类；`SetClipboardText` 其实不是「换配置」。可变数组见 WN-D1。🟡
- **API-M4** 堆叠次序双向不通 —— 同 WN-E1；`_NET_RESTACK_WINDOW` 不支持。
- **API-M6** `ChangeKeyboardControl` 整个忽略、`BellRequested` 不看音量、DetectableAutoRepeat 只登记 —— 见 IN-E8、IN-E16。
- **API-P1** 多个忙窗口时 UI 线程每帧逐个窗口拿像素锁（`XNativeWindow.cs:688`）❓。先把 bench 的「满载读像素」扩成 4 个窗口量一下，再决定要不要「一次锁内读多个窗口」的 API。

### 宿主的绕路说明库缺什么

| 宿主现在的做法 | 库缺的东西 |
| --- | --- |
| 按 XID 自管窗口字典，还要防旧句柄 | `XTopLevelWindow.Server` / `IsAlive`，以及给宿主挂数据的地方 |
| 用 `X==0 && Y==0` 猜「没给位置」，外框偏移自己算 | 快照里的 USPosition / PPosition / win_gravity / BorderWidth |
| 失活时自己逐个松开按着的键和按钮 | `ReleaseAllInput()` 与 `SetLockState(...)` |
| 窗口状态只能整组回写 | `ChangeTopLevelStates(add, remove)` |
| 进程内连接借用 VelaShell.Ssh 的 InMemoryTransport，bench 与测试也各造一份双工流 | `ConnectInProcess()`（返回一对流） |
| `run-server.cs` 自己做了一张字符 → 键码表 | `InjectText` / `InjectKeysym` |
| 关不掉卡死的窗口、列不出客户端、日志对不上会话 | 客户端句柄、连接标签、Kill、`_NET_WM_PING`、`BreakGrabs` |
| 没法前置窗口、也收不到 Raise | 双向的堆叠 API（WN-E1） |
| 工作区传不进去 | `XMonitor.WorkArea` |
| DPI 只能取主显示器 | 按显示器的 DPI（RANDR 的毫米数已经是按显示器算的） |
| 剪贴板开关不能即时生效 | `SetClipboardSync(clipboard, primary)` |

---
## 九、真实应用兼容性（CP）

按用户真正会经 SSH X11 转发跑的程序逐类推演「启动、画界面、输入、剪贴板 / 拖放、菜单与对话框、改尺寸、退出」用到的协议面，再回代码核对。
没有启动任何应用，结论里 ❓ 的部分都写了怎么实测；推断依据是各工具包的公开行为，没有看它们的源码。

### 总览

| 类别 | 结论 | 主要缺口 | 影响 | 补齐工作量 |
| --- | --- | --- | --- | --- |
| 所有程序 | — | CP-1 重叠窗口点错；CP-2 / CP-3 剪贴板只有文本、复制方退出就空；CP-5 程序挪不动本机光标；CP-6 菜单可能关不掉 | 高（人人遇到） | CP-1 约 1 天，其余各 0.5–4 天 |
| Java（AWT / Swing；JavaFX、SWT 按 GTK 看） | 能用但有缺陷 ❓ | CP-9 被判成「没有窗口管理器」，与 CP-23 耦合 | **高**：Vivado、IntelliJ 系、MATLAB、SQL Developer、JMeter、Burp | 先实测 1 天；规避 0.5 天 |
| GTK3 / GTK4 | 能用但有缺陷 | CP-10 无平滑滚动；CP-11 自绘标题栏边缘缩放 ❓；CP-12 无合成管理器；CP-13 不发同步请求 / ping；GTK4 默认 GL 很重（已写进排障文档） | 中 | 各 1–3 天 |
| Qt5 / Qt6 | 能用（小缺陷） | 只有通用缺口；CP-14 本机 Docker 的 MIT-SHM ❓；Qt6 要远端装 `libxcb-cursor0` | 中 | 0.5 天 |
| 浏览器 / Electron | 能用但有缺陷 | 整窗像素走 SSH（DRI3 是非目标）；剪贴板无 html / 图片；本机 ↔ 远端拖放；透明窗口；CP-15 XI 只到 2.2 | 中低（浏览内网更适合 `-D` 加本机浏览器） | 见各条 |
| 老程序 / EDA / Motif / Tk / Emacs | 能用但有缺陷 | **CP-16 核心字体极少**；CP-17 只有 TrueColor（非目标）；CP-18 换 DPI 冲掉 xrdb 资源 | **高**：IC 设计是 MobaXterm 的重度用户群 | 字体 3–5 天（数据随库）或 1–2 周（宿主提供） |
| 3D 与可视化 | 能用但有缺陷 | **CP-20 没有 `GLX_ARB_create_context`**；CP-21 没有多重采样配置；CP-22 间接 GL 只有 1.1 子集 | 中（科研、CAD） | CP-20 1–2 天 |
| 整个远端桌面会话（xfce4-session 等） | **实际不能用** | CP-23 远端窗口管理器抢走根窗口；CP-24 桌面类窗口盖住整屏 ❓；CP-25 没有单窗口模式 | 中低 | 防抢 1 天；单窗口模式 2–3 周 |
| 托盘与通知 | 不能用（图标不显示） | CP-26 没有托盘管理器 | 低 | 1–2 周 |
| 中日韩输入 | 远端没有输入法框架时**不能用** | CP-27 没有 XIM（「确认不做」清单里）；CP-28 远端 fcitx / ibus 没实测 ❓ | **对中文用户高** | 见 Q1 / F5 |
| 命令行 X 工具 | 大多能用 | CP-29 没有 SECURITY（非受信转发不可用）；CP-30 没有 RECORD；CP-31 光标图像；CP-32 `xset` / 左手鼠标 | 低 | 各 0.5 天–2 周 |

### 所有程序都受影响的

- **CP-1** 堆叠次序不同步、点错窗口 —— 同 [WN-E1](#wn-e1)。
- **CP-2** 剪贴板只有文本 🟠 ✅：服务端占有剪贴板时只给 `TARGETS / TIMESTAMP / UTF8_STRING / text/plain;charset=utf-8 / STRING / TEXT`（`Server/X11Server.Clipboard.cs:128`），从 X 取只要 UTF8_STRING、再退 STRING（`:166-175,272`）；
  没有 `image/png`、`text/html`、`text/uri-list`、COMPOUND_TEXT、MULTIPLE；宿主接口本身只有字符串（`IX11ServerHost.ClipboardChanged(string)`）。截图、GIMP、LibreOffice 的图片与富文本到不了本机。见 F15。
- **CP-3** 复制方退出后剪贴板就空 —— 同 [WN-E3](#wn-e3)。
- **CP-4** 本机 ↔ X 不能拖放 🟡 ✅：库与宿主里搜不到任何 Xdnd。X 程序之间的 XDND 只靠核心协议，应当可行但没测过 ❓。见 F16。
- **CP-5** 程序挪鼠标挪不动本机光标 🟡 ✅：`WarpPointer` 只改服务端记的位置（`Input.cs:1267-1284`），confine-to 与 XFIXES 指针屏障也不生效。Blender 连续拖拽、CAD 旋转、virt-manager 控制台的相对鼠标都会乱跳。见 IN-D2、F8。
- **CP-6** 点到本机其它窗口时菜单可能卡住 🟡 ❓ —— 同 WN-M7。
- **CP-7** `_NET_WORKAREA` 是整个根窗口 —— 同 WN-M3。
- **CP-8** 映射时不按初始状态摆：映射前写好的 `_NET_WM_STATE` 最大化 / 全屏、WM_HINTS 的 initial_state、WM_NORMAL_HINTS 的 USPosition / win_gravity 都不生效（`Ewmh.cs:369-420`，宿主只在收到 ClientMessage 时才改状态）。并入 WN-M2。

### Java（AWT / Swing）

<a id="cp-9"></a>
#### CP-9 Java 的窗口管理器判定 · 🟠 ❓

- **已经没问题的**：XRender 管线的请求全部实现、滤镜名做了映射；MIT-SHM 对远端客户端不可见，Java 直接退回 PutImage；XINERAMA、`_NET_FRAME_EXTENTS` 都有。
- **推断**（按 OpenJDK 的公开行为）：AWT 先看 `WM_S0` 有没有属主，再试着在根窗口上选 SubstructureRedirect，选得上就判定**没有窗口管理器**（NO_WM）。我们两样都空着（只占了 `_XSETTINGS_S0`，`XSettings.cs:31`），所以 Java 判成 NO_WM：
  IntelliJ / Vivado「启动时恢复最大化」无效（用户点原生窗口的最大化按钮照常有用）；`toFront()` 只发 XRaiseWindow、原生窗口不动（叠加 WN-E1）；屏幕可用区按整屏算。
- **耦合风险**：一旦为 WN-S3 占住根窗口的重定向或 `WM_S0`，Java 会改判成「未知窗口管理器」并假定它会给窗口套外框。我们从不套，Java 就一直等 ReparentNotify、忽略 ConfigureNotify：
  改尺寸 / 最大化后内容不重排、出现灰块、菜单弹在旧位置 —— 就是 `_JAVA_AWT_WM_NONREPARENTING` 那个老坑。
- **规避**：把 `X11ServerOptions.WindowManagerName`（`Host/X11ServerOptions.cs:82`，宿主没覆盖它）设成 Java 认得的「不套外框」的名字（LG3D 一类）；或者真正给窗口套外框（1–2 周、风险高，顶层窗口的含义全变）。
- **实测**：interop 镜像加 `default-jre` 与一个单文件 Swing 程序（JFrame + 菜单 + JPopupMenu + JComboBox + JTable），用 `run-server.cs` 的 `resize` / `click` 截图比较；分别在现状、模拟占住 `WM_S0` 后、设了 `_JAVA_AWT_WM_NONREPARENTING=1` 时各跑一遍。**先实测 CP-9，再动 WN-S3。**
- 其它：托盘 `SystemTray.isSupported()` 为 false（CP-26）；Swing 不读 `Xft.dpi`，HiDPI 要靠远端 `GDK_SCALE` 或 `-Dsun.java2d.uiScale`（F19）。

### GTK3 / GTK4

- **已经没问题的**：XI 2.2、`_NET_WM_MOVERESIZE`（宿主走系统拖动循环）、`_GTK_FRAME_EXTENTS` 会解析、XSETTINGS、自绘标题栏窗口不加原生边框；GtkFileChooser 只依赖 GIO / D-Bus（门户超时已写进排障文档）。
- **CP-10** 没有平滑滚动 🟡 ✅：指针设备只有按钮类与两个绝对坐标轴，没有滚动类（`XInput.cs:446-449`）；宿主把滚轮增量攒满一格折成按钮 4–7（`XNativeWindow.cs:447-473`）。GTK3 / 4、Qt、Firefox、Chromium、Emacs 29 都拿不到像素级滚动，触控板一格一跳。见 F6。
- **CP-11** 自绘标题栏窗口的边缘缩放 🟡 ❓：默认 `ClientSideShadows = false`（`X11ServerOptions.cs:79`，宿主没开），`_NET_SUPPORTED` 里不列 `_GTK_FRAME_EXTENTS`（`Ewmh.cs:108-111`）；这类窗口在宿主完全无边框（`XNativeWindow.cs:245-249`），GTK 又没有阴影区放缩放手柄。host-demo 里拖 gedit / gnome-calculator 四边确认。
- **CP-12** 没有合成管理器 🟡 ✅：没人占 `_NET_WM_CM_S0`。GTK 不用 ARGB 视觉、没有圆角与阴影；Qt 半透明窗口、Electron 的 `transparent` 窗口拿不到 alpha —— 而宿主其实已经能显示带 alpha 的窗口（`XNativeWindow.cs:257-259`）。见 F11。
- **CP-13** 不发同步请求与 ping 🟡 ✅：`_NET_SUPPORTED` 里没有 `_NET_WM_SYNC_REQUEST`、`_NET_WM_PING`（`Ewmh.cs:98-105`）。改尺寸时可能撕裂，也没法提示「程序无响应」。见 F3、F10。
- GTK2（GIMP 2.10、旧 Inkscape、gtkwave）只用核心事件加 XI 1.x 查询，应当可用；压感笔不可用（XI 1.x 设备事件不产生，F7）。

### Qt5 / Qt6

- **已经没问题的**：XCB 依赖的扩展全部注册（XKB 含 PerClientFlags、XI 2.2、RENDER 0.11、SHAPE 1.1、SYNC 3.1、RANDR 1.5、XFIXES 5）；MIT-SHM 对远端客户端隐藏，**不需要 `QT_X11_NO_MITSHM`**；`Xft.dpi` 写在 RESOURCE_MANAGER 里。
- **CP-14** 本机 Docker 容器的共享内存 —— 同 CN-S9 ❓（Linux 上 `docker run -v /tmp/.X11-unix:/tmp/.X11-unix` 跑一个 Qt 程序确认）。
- Qt6 在远端缺 `libxcb-cursor0` 时 xcb 插件加载不起来，是远端环境问题，值得补进 troubleshooting.md。

### 浏览器与 Electron

- **已经没问题的**：XI 正好报 2.2（Chromium 的最低要求，推断）；XKB、RANDR 1.5、XFIXES 隐藏 / 显示光标、SHAPE 输入区、SYNC 计数器都有；`_NET_WM_MOVERESIZE` 撑得住 Electron 的自绘标题栏。
- **性能**：DRI3 / Present 翻页是非目标、MIT-SHM 只给本机，每帧像素都走 SSH，建议远端加 `--disable-gpu`。
- **CP-15** XI 封顶 2.2 🟡 ✅（`XInput.cs:263-268`）：没有 2.4 的触控板手势（捏合缩放）与 2.3 的屏障事件。见 F7。

### 老程序与科学 / EDA 工具

<a id="cp-16"></a>
#### CP-16 核心字体只有 5 个裁剪过的 misc-fixed · 🟠 ✅

- **位置**：`Fonts/FontCatalog.cs:32`（6x13 / 6x13B / 9x15 / 9x15B / 10x20）；字形裁到 U+0000–024F、通用标点、箭头、制表符等少数区块（`Fonts/Data/README.md`）——**没有希腊文、西里尔文、中日韩**；没有 75dpi / 100dpi 的 Helvetica、Times、Courier、Lucida，也没有可缩放字体。
- **后果**：Motif / Xaw / 不带 Xft 的 Tk 请求 `-adobe-helvetica-*` 失败、退回 fixed，界面错位难看（Xt 默认的 `-*-*-*-R-*-*-*-120-*-*-*-*-ISO8859-*` 还能匹配到 6x13，不至于启动失败）；
  默认核心字体模式的 xterm 显示不了中文文件名；xfig 画布文字、ddd、nedit、旧 EDA 都受影响。EDA 安装指南里常写「X 服务端要有 75dpi / 100dpi 字体」，这是实打实的同类产品差距（VcXsrv、MobaXterm 据称都带整套 X.Org 字体，未核实）。
- **修法**：见 F21（方案 A：随库带数据；方案 B：architecture §7 预留的宿主字体提供者）。WN-M9（别名与就近回退）一起做。
- **CP-17** 只有 TrueColor 🟡 ✅：可写颜色单元一律 BadAlloc（`Dispatch.cs:157-158`），architecture §2 的非目标。现今 EDA 都跑 24 位，影响很小；替代是远端跑 `Xephyr -screen WxHx8`。
- **CP-18** 换 DPI 冲掉 xrdb 资源 —— 同 [WN-E14](#wn-e14)。
- **不是缺口的**：backing-store 报 Never 但每个顶层都有自己的缓冲，Expose 反而比 Xorg 少；Motif 的同步抓取已实现（M4）；`WM_COMMAND` / 会话管理走远端的 ICE；没有 WM_DELETE_WINDOW 的老程序被关闭时直接断开是设计；`xset +fp` 接受不生效（远端字体路径本来就不在本机）。
- 现代 EDA：Virtuoso、Verdi、DVE、Quartus 已是 Qt；Vivado 是 Swing；KiCad 是 wxGTK3。

### 3D 与可视化

- 直接渲染（远端 Mesa llvmpipe + PutImage）可用，glxgears / glxinfo 已实测；但每帧整窗像素，1080p 一帧约 8 MB，只适合局域网。
- **CP-20** 没有 `GLX_ARB_create_context` —— 同 [GL-M1](#gl-m1)。
- **CP-21** FBConfig 只有 4 个 🟡 ✅：没有多重采样、累积缓冲（`GlxExtension.cs:84,1157-1172`）。
- **CP-22** 间接渲染只有 GL 1.1 子集 🟡：没有 GL_SELECT 拾取、求值器（GL-M3）。只装了 NVIDIA 驱动、没有 Mesa 厂商库的 GPU 服务器上 GLVND 可能被迫走间接渲染，那时几乎不可用 ❓。

### 整个远端桌面会话

- **CP-23** 远端窗口管理器能抢走管理权 —— 同 [WN-S3](#wn-s3)。
<a id="cp-24"></a>
- **CP-24** 桌面类窗口盖住整个屏幕 🟠 ❓：xfdesktop、caja、pcmanfm 的桌面窗口（以及 CentOS 7 上旧版 nautilus 默认画的桌面）在宿主只是一个无边框、不进任务栏的普通窗口（`XNativeWindow.cs:246-251`），大小等于所有显示器的外接矩形，一激活就挡住本机所有程序。
  不显示 `_NET_WM_WINDOW_TYPE_DESKTOP` 的窗口，或沉底且不可激活（0.5–1 天）。
- **CP-25** 没有单窗口（rootful）模式 🟡 ✅：根窗口不画，`xsetroot`、壁纸无效；`_NET_WM_STRUT` 没处理，面板占不住位置。现在能用的替代：远端跑 `Xephyr :2 -resizeable`，把整个桌面嵌进一个窗口（带宽大）。见 Q2、F13。

### 托盘与通知

- **CP-26** 没有托盘管理器 🟡 ✅：没人占 `_NET_SYSTEM_TRAY_S0`。GtkStatusIcon、Electron 的 XEmbed 退路看不到图标；Java `SystemTray.isSupported() = false`、Qt `isSystemTrayAvailable() = false`。会「关闭到托盘」的程序关窗后找不回来。
  XEMBED 本身只靠 ReparentWindow 与 ClientMessage，X 程序之间可用。StatusNotifierItem、通知、AT-SPI 都走远端 D-Bus，与 X 无关。见 F12。

### 中日韩输入

- **CP-27** 没有 XIM 服务端 🔴（对中日韩用户）✅：宿主是 Avalonia 窗口，没有文本输入客户端，本机输入法不会进来，X 端只收到英文键码。现实里 SSH 登录的服务器几乎不装 fcitx / ibus，sshd 通常也不接收 `XMODIFIERS`。
  VelaShell 的界面有 zh-Hans、zh-Hant、ja、ko 四种语言，目标用户正好是这批人。VcXsrv、Cygwin/X 系（含 MobaXterm）据称也没有把 Windows 输入法桥接给 X 程序（未核实）—— 所以这**不是差距，而是能拉开差距的地方**。`feature-plan.md` 把它列在「确认不做」，见 Q1。
- **CP-28** 远端 fcitx5 / ibus 经本服务端工作 🟡 ❓：XIM 只是客户端之间用属性、ClientMessage、选区通信，服务端只需核心协议，理论可行。实测：镜像加 `fcitx5 fcitx5-chinese-addons dbus-x11 fonts-noto-cjk`，
  `dbus-launch sh -c 'fcitx5 -d; sleep 2; XMODIFIERS=@im=fcitx GTK_IM_MODULE=fcitx gedit'`，注入 Ctrl+Space 加拼音后截图。
- 相关：核心字体没有中日韩（CP-16）；TEXT 回 Latin-1、没有 COMPOUND_TEXT，中文粘进 Motif / Xaw 会丢字（WN-E11、WN-M4）。

### 命令行 X 工具与其它

| 工具 | 状态 |
| --- | --- |
| xdotool / xsel / xclip / xprop / xwininfo / xev / xinput / xkill / wmctrl | 能用。`xclip -t image/png` 不行（CP-2） |
| xrandr | 查询能用；改配置回 Failed，是设计（architecture §7）。SetCrtcGamma 回 BadAccess 会让部分程序退出（IN-E18） |
| xset | 部分生效（**CP-32** 🟡 ✅）：ChangeKeyboardControl / ChangePointerControl 被忽略，SetPointerMapping 不生效（`Dispatch.cs:173,176,187`）—— `xset r rate`、`xset m`、`xset b off`、左手鼠标都无效。见 IN-E16 |
| `import -window root`、scrot、xwd | 能用（根窗口按堆叠次序拼各顶层，其余是黑的）；多块 4K 屏时超过 64 MB 被断开（CN-E6） |
| xhost | 不报错、不生效（服务端只听本机）；ListHosts 却报「access control disabled」（CN-M2） |
| 录屏 / 截图带光标 | **CP-31** 🟡 ✅ XFIXES GetCursorImage 回 1×1 透明像素 —— 同 WN-M5 |
| autokey、xcape、screenkey、xmacro | **CP-30** 🟡 ✅ 没有 RECORD 扩展。价值低、又能被拿来记键盘，不建议（第十二节「不建议做的」） |
| `ssh -X`（非受信） | **CP-29** 🟡 ✅ 没有 SECURITY，`xauth generate … untrusted` 必失败 —— 见 X-3、F2 |
| mpv / VLC | 没有 Xv，退回 x11 / shm 输出，能用 |

---

## 十、质疑决策（需要你拍板）

这些是现有设计里明确写下的取舍，或「确认不做」清单里的条目，审查认为值得重新考虑。每条给出建议，**不拍板不动**。

| # | 现在的决策 | 质疑的理由 | 建议 |
| :-: | --- | --- | --- |
| Q1 | XIM「确认不做」：中日韩输入走远端自己的输入法框架（`feature-plan.md` 确认不做清单、`plan.md` §105） | SSH 登录的服务器几乎不装 fcitx / ibus，结果是中日韩用户在远端 IntelliJ、Emacs、gedit 里**完全打不了字**，只能从本机粘贴；而界面本身有四种 CJK 语言。同类产品据称也没做，做了就是差异点 | 两步走：① 先做便宜的「本机输入法上屏」（宿主组好字，经 `InjectText` 临时改一个空闲键码的键值注入，无预编辑，覆盖所有工具包，中等工作量）；② 再评估 XIM 桥接（over-the-spot，覆盖 xterm / Emacs / Java / Tk / Motif 与 `GTK_IM_MODULE=xim` 的 GTK2/3，**覆盖不到 Qt5/6 与 GTK4**，3–5 周；净室依据要在 AGENTS 的规范清单里加 X 联盟的 *The Input Method Protocol*）。见 F5 |
| Q2 | 只做 rootless：「每个顶层一个原生窗口、根窗口不画」（architecture §2 的设计前提，没写成非目标） | 完整远端桌面（xfce / MATE / 图形安装器）实际不能用（CP-23～25）；MobaXterm 的 Windowed 模式、VcXsrv 的「One large window」都是用户熟悉的形态；设置里的 WindowMode 现在只对 VcXsrv 生效 | 作为**可选模式**加入（F13），默认仍 rootless；短期先把 `Xephyr :2 -resizeable` 写进 troubleshooting.md |
| Q3 | 剪贴板默认双向自动同步，宿主把「选中即复制」映射成 PRIMARY 同步（默认开） | 本机复制的密码会在用户点一下 X 窗口后对所有会话可读；远端可以反复改写本机剪贴板（API-H5、WN-S11） | `SyncPrimary` 跟库默认一样关；CLIPBOARD 仍默认开，但 host → X 只给焦点客户端、X → host 只收焦点客户端；设置页写明风险；以后按会话开关 |
| Q4 | 库默认 `ListenTcp = true`、不要 cookie（CN-D1）；宿主在类 Unix 上也开 TCP | 库面向嵌入、MIT 授权，零值配置就是本机任意用户能记键盘；宿主在类 Unix 上其实可以只开 Unix 套接字（SSH 走进程内连接器，本机程序走 `:N`，MIT-SHM 还能直接用） | 库：没配 cookie 时默认不开 TCP，或自动生成 cookie 经属性交出。宿主：Linux / macOS 默认只开 Unix 套接字（前提是先修 CN-S3） |
| Q5 | 一个显示给所有会话；architecture §7 把「受信转发的含义就是这样」当成提醒而不是缺陷 | 提醒成立，但现在用户**没有任何一档可选**：-X 在内置引擎下开不起来（X-3），XTEST / 原始按键也没有开关（IN-S3） | 分三步：① 改提示文字；② 连接加信任级别，经 SSH 来的连接可选「隐藏 XTEST / 原始事件 / 跨客户端 GetImage」（F2）；③ 每会话一个显示（F1）。默认值由你定 |
| Q6 | architecture §7「资源都有上限，超了回 BadAlloc」 | 实际只有单件上限（X-2），文档与实现漂移 | 加每客户端 + 全局账；文档改成写明粒度 |
| Q7 | 间接 GLX「如实报 1.1、固定功能子集」 | X.Org 自 1.17 起默认关间接 GLX，日常需求很小；但慢链路上的 CAD / 可视化（显示列表只传一次几何）与只装 NVIDIA 驱动的 GPU 集群是两个不可替代的场景 | 投入限定在**1.1 子集的正确性与安全**（GL-S1～S3、E1～E5）与 CAD 常用的选择模式 / 点画 / 求值器（F23）；**不追** GL 1.3+；直接路径补 create_context（GL-M1） |
| Q8 | GLX 表面上限 4096²，超了每个请求回 BadAlloc（GL-E7） | 8K 屏或跨屏最大化时 GL 程序因 X 错误退出 | 表面夹到上限、只渲染一块并记一行日志 |
| Q9 | 「XKB 由核心表推出」，SetCompatMap / SetNames 等不支持 | 原则本身合理；但 DetectableAutoRepeat 只登记不生效（IN-E8）、非拉丁字母的 ALPHABETIC 推导不对（IN-E15）是这条原则下可以修的 | 保留原则，补这两处；多组布局（XKB group 切换）维持不做 |
| Q10 | 自动重复由宿主做（每个系统 KeyDown 注入一次按下） | 服务端看到「按下、按下……松开」，DetectableAutoRepeat、XIKeyRepeat、`xset r` 都无从实现 | 宿主告诉库「这是重复」（`InjectKey(…, repeat: true)`），由库按客户端是否选了 DetectableAutoRepeat 决定发不发成对的 KeyRelease |
| Q11 | 屏保 / 空闲只按 X 输入算 | 用户在本机终端里一直打字，远端仍认为他离开了（IN-M2） | 宿主把本机活动上报给库（`NoteUserActivity()`），视频播放器的 Suspend 转告宿主（F9） |

---

## 十一、测试缺口

按「补了能挡住多大的事」排序。

| # | 缺口 | 为什么要紧 |
| :-: | --- | --- |
| T1 | **限时用例（防 DoS 守门）**：屏外巨大矩形与细线（DR-S1）、大线宽圆帽（DR-S2）、AddGlyphs 32768²（DR-S3）、多色标渐变（DR-S4）、远距字形遮罩（DR-S5）、多报警器（DR-S7）、Clear 列表被调用很多次（GL-S1）、ROW_LENGTH 重叠 + 极小 zoom 的 DrawPixels（GL-S2）、XIPassiveGrab 洪泛（IN-S1）、Bell 洪泛（IN-S4）、超长标题 + ConfigureWindow 循环（WN-S5） | 现在只有「巨大的 PolyFillArc」一条。第一批每修一条就该配一条「必须在 N 毫秒内返回 / 分配不超过 M 字节」的用例 |
| T2 | **资源总量**：多个上下文 / 多个 Pbuffer / 默认纹理 / 空列表 / 属性挂根窗口 / Retain 客户端 / 未授权连接的累计上限 | 对应 X-2、GL-S3、CN-S1、CN-S4 |
| T3 | **rootless 接缝（库 + 宿主无头 UI）**：两个重叠顶层激活下面那个后点击重叠区（WN-E1）；最小化窗口不再拦截事件；客户端 XRaiseWindow 宿主是否得知；NumLock / CapsLock 同步（IN-E6）；布局原点变化（IN-E7）；Ro / Yen 键（IN-M1） | 每个多窗口用户都会碰到，目前零用例 |
| T4 | **宿主无头 UI**（现在只有 3 条）：owner 级联关闭含 Detach 时有对话框（API-H1）、CloseReason 分流（API-H2）、Linux 缩放回报（API-H3，靠记下的请求尺寸判定）、最大化往返与 SkipTaskbar 保留（API-D1）、重启竞态（API-H8）、剪贴板双向与防回声、Shape / alpha 像素、光标图像 | 宿主侧的问题都是这次才发现的 |
| T5 | **宽线与核心绘图像素**：line-width ≥ 1 的像素位置（DR-E1）、首尾闭合、各接头 / 端帽、虚线（含 DoubleDash 与 dash-offset）、宽弧与细弧、PieSlice / Chord；tile / stipple / OpaqueStippled 与 ts-origin；clip-mask 像素图；跨出顶层的子窗口做源（DR-E2） | 现有「多边形按像素中心填充」只用整数顶点的矩形，发现不了半像素偏移 |
| T6 | **选区与剪贴板**：时间早于最后一次变更 / 晚于当前时间 / 当前没有属主（WN-S2）；X 属主退出后能否粘贴（WN-E3）；宿主端超大文本；MULTIPLE / TEXT 的非 Latin-1；ConvertSelection 带非法 property 原子 | 对应 WN-S2、WN-E3、WN-E11、WN-E13、WN-E19 |
| T7 | **结构请求**：ReparentWindow 的事件次序、顶层与子窗口互转时宿主句柄的变化；DestroySubwindows 的次序；MapRequest / ConfigureRequest 改道；根窗口 SubstructureRedirect 被占（WN-S3）；对 0x43 / 0x46 做 Reparent / Destroy（WN-S7）；GetGeometry、QueryTree、TranslateCoordinates | 这些核心请求目前没有直接用例 |
| T8 | **GrabServer**（零用例）：持有期间别的客户端的请求暂存并按序放回、持有者断开自动解除、与 SYNC Await 的交互、分批放行（CN-P1） | grep 确认为零 |
| T9 | **输入协议细节**：隐式抓取结束的 Ungrab Enter（IN-E2）；SetInputFocus / Grab* / AllowEvents 的时间戳与 InvalidTime / Frozen；分两次或按多个设备选 XI2 事件（IN-E4）；Warp 不产生 RawMotion（IN-E5）；do-not-propagate 与 OwnerGrabButton 的投递（零用例）；冻结队列满时不丢松开（IN-E9）；RevertToParent 退到根；DetectableAutoRepeat | 对应 IN-E2～E14 |
| T10 | **GLX 异常路径**：逐条断言「没有 BadImplementation」—— PopAttrib 恢复已删除的纹理名、跨共享组 CopyContext、代理目标的 CopyTexSubImage、`int.MinValue` 坐标的 CopyPixels、数据过短的 TexImage、超大 GetTexImage；FreePixmap + DestroyGLXPixmap 之后 `SurfaceCount` 回落（GL-E1） | 对应 GL-E1、E2、S4～S6 |
| T11 | **GLX 覆盖面**：大端客户端的 Render / RenderLarge / 带 swap 的 ReadPixels（零覆盖）；CopyContext、WaitGL / WaitX、UseXFont、GLX 1.3 的 CreateWindow、Get / ChangeDrawableAttributes、带 share list 的 CreateContext、33 / 34 / 35 号请求、读写表面不同的 MakeContextCurrent；GL 语义的像素用例（光照、雾、混合、模板、alpha 测试、TexEnv、TexGen、用户裁剪面、多边形偏移、剔除） | 现在只靠 glxgears 的互操作用例兜着，而它不比较像素 |
| T12 | **连接与授权**：注入一个 accept 时抛 ConnectionReset / EMFILE 的监听，断言之后仍能连上（CN-E1）；Unix 探测在约 1 秒内返回（CN-S2，Linux）；空 cookie 被拒（CN-S7）；`SetCloseDownMode(5)` 回 BadValue；写出端失败后 Abort（CN-E5）；日志控制字符转义；DeferredHost 回调与日志委托都抛时循环仍活（CN-E7）；编号复用竞态（CN-E3） | 对应 CN 各条 |
| T13 | **SYNC / Present / DBE**：Relative value-type、默认 test-type、Await 的 threshold 与多条件、比较型报警器远距推进、Transition 不空转；Present 的 target-msc、wait-fence、PresentNotify；DBE 的 Untouched 与 Background | 对应 DR-E6～E10、DR-S8、DR-M2 |
| T14 | **RENDER 语义**：Over / Src / Add 以外的 op 的像素值、Reflect / Pad 用在图像上、radial / conical 渐变、a1 / a4 遮罩合成；深度 4 / 8 / 15 的 PutImage → GetImage 往返（现在只有 16 位）；XYPixmap 的 GetImage 带 plane-mask | 整数核之外的路径没有像素用例 |
| T15 | **XAuthorityFile 与真实 xauth 的互操作**（Interop 类别）：Add 之后 `xauth list` 能看到；xauth 正持锁时的行为 | 对应 CN-E8 |
| T16 | **interop 靶场扩充**（见附录 B）：Java Swing、GTK3 全面、GTK4、Firefox / Chromium、Motif / Tk / Emacs、桌面会话、托盘、fcitx5 | 现在的真实客户端只覆盖 x11-apps、xterm、gedit、qt5ct、glxgears 一类 |

---
## 十二、将来可以支持的功能

每条写清**使用场景**与**实际用途**，再给依据、大致工作量与风险，以及是否碰到 architecture.md 的非目标或「确认不做」清单。
上面已经作为缺陷登记的（比如堆叠同步 WN-E1、锁定键 IN-E6、颜色名 WN-M1）不再重复列为功能。

工作量：小（≤ 3 天）/ 中（1–2 周）/ 大（> 2 周）。

### A. 安全与隔离

#### F1 每个 SSH 会话（或每台远端主机）一个显示

- **场景**：同时连着生产机和一台不太信任的测试机，两边都开了 X11 转发。
- **用途**：会话之间互相看不见窗口、读不到键盘、注入不了输入、读不到剪贴板；断一个会话只影响它自己的 X 程序。直接消掉 architecture §7 那条 ⚠️。
- **依据**：库没有可变静态状态，多实例安全；`CheckHandle` 已能区分不同服务端的句柄；连接器路径不需要本机端口。
- **工作量**：中。宿主要按服务端分组窗口，键位表、DPI、显示器布局要广播给每个实例，剪贴板要按会话路由；每个实例一条执行线程加一份根窗口。
- **风险**：内存与线程数随会话数增长；本机 X 程序（`DISPLAY=:N`）连哪一个要有约定（保留一个「本机显示」）。不冲突。

#### F2 非受信级别（按 SECURITY 扩展规范的语义）

- **场景**：相当于 `ssh -X`。现在连接器强制受信（`Ssh/Forwarding/X11ForwardOptions.cs:135-141`），非受信又因为没有 SECURITY 而开不起来（X-3）。
- **用途**：经 SSH 来的不受信客户端拿不到 XTEST、跨客户端 GetImage、QueryKeymap、XI2 原始事件、往别人的窗口 SendEvent、读别人的属性；选区只经宿主中转。
- **依据**：SECURITY 扩展规范（要加进 AGENTS 的规范清单）；现成的 `Extension.VisibleTo` 能管扩展，核心请求要在分派层加一道按信任级别的判断。
- **工作量**：中到大。**风险**：部分程序在非受信下会出错，先对真实客户端做一轮兼容测试。不冲突。

#### F3 X 客户端清单、强制结束、`_NET_WM_PING`、BreakGrabs

- **场景**：远端程序卡死了（声明了 WM_DELETE_WINDOW 却不响应）；菜单开着时 SSH 断网，所有 X 窗口点不动；有客户端一直持有 GrabServer。
- **用途**：第一次点关闭发 WM_DELETE_WINDOW；对 ping 没有响应时提示「无响应，强制关闭？」（KillClient 语义）；X Server 按钮的浮层列出「谁连着、来自哪个会话、占多少内存」，可一键断开；停服前提示「会断开 N 个程序」。
- **依据**：EWMH `_NET_WM_PING`；X-Resource 已有每客户端统计。
- **工作量**：小到中（API-M2、IN-D1、CN-M5 一起解决）。不冲突。

#### F4 平台补齐：对端身份、显示号锁、只开 Unix 套接字

- **内容**：macOS / BSD 用 `getpeereid` / `LOCAL_PEERCRED` 取对端 uid（CN-S8）；建 `/tmp/.X{N}-lock`（Xserver(1) 手册的约定，与 Xvfb / `xvfb-run -a` 互不撞号，CN-E9）；类 Unix 上宿主可选「只开 Unix 套接字」（Q4，前提是先修 CN-S3）。
- **用途**：授权逻辑三个平台一致；本机其他用户连连接建立阶段都够不着；MIT-SHM 直接可用。
- **工作量**：小。不冲突。

### B. 输入与输入法

#### F5 本机输入法上屏

- **场景**：远端服务器没装 fcitx / ibus（绝大多数），用户要在远端 IntelliJ、Emacs、gedit、xterm 里输入中文、日文、韩文。
- **用途**：中日韩用户能直接用本机输入法在 X 程序里打字，而不是只能从本机粘贴。
- **两条路**：
  1. **上屏注入**（先做）：宿主拿到本机输入法提交的文本，库新增 `InjectText`：临时把一个空闲键码的键值改成目标字符、注入按下 / 松开、再改回。覆盖所有工具包；没有预编辑、候选框跟着本机窗口而不是光标；每个字一轮 MappingNotify。中等工作量。
  2. **XIM 桥接**：服务端扮演 XIM 服务器，over-the-spot 让本机输入法自己显示候选框，只回传 XIM_COMMIT。覆盖 xterm、Emacs、Java、Tk、Motif，以及设了 `GTK_IM_MODULE=xim` 的 GTK2/3（Firefox、Chromium 跟着 GTK）；**Qt5/6 与 GTK4 没有 XIM，覆盖不到**；远端还要设 `XMODIFIERS`（可以借 shell 注入）。大（3–5 周）。
- **依据**：X 联盟的 *The Input Method Protocol*（要加进 AGENTS 的规范清单）。
- **冲突**：与 `feature-plan.md`「确认不做：内置 X 服务端的输入法（XIM）」冲突，见 Q1。

#### F6 平滑滚动（XI 2.1 滚动类）

- **场景**：触控板在 Firefox、GTK3、Qt5、Emacs 29 里滚动；现在宿主攒满一格才发一次按钮 4 / 5（`XNativeWindow.cs:447-473`），一格一跳；横向滚动同理。
- **用途**：像素级、带惯性的滚动，与本机程序手感一致。
- **依据**：XI 2.1 起的 ScrollClass 与滚动 valuator。
- **工作量**：中。加滚动类与两条滚动轴，宿主传原始增量（新接口 `InjectScroll(window, dx, dy)`），继续为只认按钮 4 / 5 的核心客户端模拟按钮。不冲突。

#### F7 触摸、触控板手势、数位板压感

- **场景**：Windows 触屏本 / 平板上用远端程序（现在触摸被当成左键）；浏览器、文档查看器的捏合缩放；经 SSH 用 GIMP / Krita / Inkscape 画画。
- **用途**：多指滚动、长按菜单、捏合缩放、压感与倾斜。
- **依据**：XI 2.2 touch（已在规范清单内）；XI 2.4 手势（**超出现有规范清单**，要先加）；压感是一个单独的笔设备 + 压力 / 倾斜轴，数据来自宿主（Avalonia 给得出压力值），不是直接读硬件。
- **工作量**：触摸大（触摸序列归属、Accept / Reject、指针模拟，风险高）；手势中；压感中。不冲突。

#### F8 指针 Warp / confine-to / 指针锁定交给宿主

- **场景**：Blender 连续拖拽、CAD 旋转、远端游戏的鼠标视角、virt-manager 控制台抓鼠标；程序把指针挪到对话框的默认按钮。
- **用途**：服务端的指针与真实光标不再分叉（CP-5、IN-D2）。
- **做法**：宿主回调 `PointerWarpRequested`（接口默认空实现），能挪系统光标的平台照办；只在 X 窗口激活且有抓取时才挪（防远端乱挪用户的鼠标）；confine-to 映射为宿主的指针约束。
- **工作量**：中，三个平台各写一份；Wayland 宿主做不到。不冲突。

#### F9 宿主活动上报与屏保抑制

- **场景**：用户整小时在本机终端里打字，远端的「空闲自动锁定」「离开状态」却认为他走了；用户在远端 mpv 里看视频，本机屏保照样启动。
- **用途**：IN-M2。宿主把本机活动上报给库（`NoteUserActivity()`），库把 ForceScreenSaver(Reset) / Suspend 转成宿主回调，宿主据此抑制系统屏保。
- **工作量**：小。不冲突。

### C. 窗口管理与桌面集成

#### F10 `_NET_WM_SYNC_REQUEST`（与 GTK3 的 `_NET_WM_FRAME_DRAWN`）

- **场景**：拖动缩放 GTK / Qt 窗口；现在可能撕裂、出现未重画的空白。
- **用途**：宿主每次改尺寸后等客户端画完那一帧再上屏，缩放平滑不闪。
- **依据**：EWMH；SYNC 扩展已实现。**工作量**：中。不冲突。

#### F11 合成管理器（`_NET_WM_CM_S0`）、客户端阴影与透明窗口

- **场景**：GTK 程序的圆角与阴影、自绘标题栏窗口的边缘缩放手柄（CP-11）；Qt 的半透明窗口、Electron 的 `transparent` 窗口。
- **用途**：占住 `_NET_WM_CM_S0` 后工具包会用 ARGB 视觉；默认打开 `ClientSideShadows` 让 GTK 有地方放缩放手柄。宿主已能显示带 alpha 的窗口。
- **工作量**：小到中。**风险**：Windows 分层窗口有性能代价，要量。不冲突。

#### F12 XEMBED 系统托盘 → 宿主托盘图标

- **场景**：远端程序「关闭到托盘」后找不回来（CP-26）；GtkStatusIcon、Electron 的 XEmbed 退路、Java `SystemTray`。
- **用途**：服务端占 `_NET_SYSTEM_TRAY_S0`，把嵌进来的图标窗口渲染成宿主托盘里的图标，点击转回 X。
- **工作量**：中到大；macOS 菜单栏有差异；实际需求少，优先级低。不冲突。

#### F13 单窗口（rootful）桌面模式

- **场景**：远端完整桌面（xfce / MATE）、图形安装器、需要远端窗口管理器的工作流；MobaXterm 的 Windowed、VcXsrv 的「One large window」都是用户熟悉的形态。
- **用途**：一整块根窗口嵌进一个宿主窗口或标签页（正好避开 VelaDock「不做浮动窗口」的决定），远端窗口管理器照常工作。
- **做法**：库加根缓冲、让出窗口管理器角色（MapRequest 路径已有）；宿主一个控件、按根坐标注入。
- **工作量**：大（2–3 周）。与 architecture §2「rootless、根窗口不画」的设计前提冲突（没写成非目标），见 Q2，要在决策记录里记一笔。

### D. 剪贴板与拖放

#### F14 服务端充当 CLIPBOARD_MANAGER（SAVE_TARGETS）

- **场景**：GTK 程序退出时会把剪贴板交给剪贴板管理器保存；现在没有管理器，复制源一退出内容就没了（WN-E3）。
- **用途**：根治 WN-E3；复制方退出后照样能粘贴（含非文本格式，配合 F15）。
- **依据**：freedesktop 的 ClipboardManager 约定（ICCCM 之上的约定，要加进规范清单）。**工作量**：小到中，复用现有的取回代码。不冲突。

#### F15 剪贴板多格式

- **场景**：远端截图工具或 GIMP 复制图片贴到本机；LibreOffice 的表格、富文本；本机图片贴进远端程序；老 Motif / Xt 程序的中日韩（COMPOUND_TEXT）。
- **用途**：补齐「只同步文本」（CP-2）。
- **做法**：`image/png`、`text/html`、`text/uri-list`、`x-special/gnome-copied-files`、COMPOUND_TEXT（规范的 ISO 2022 子集）、MULTIPLE；按 INCR 双向分块、设大小上限；宿主接口从 `string` 扩成「目标 → 数据」。
  文件 URI 指向的是远端路径，只能当文本给（真正的文件见 F16）。
- **工作量**：中，要先配合 API-H5 收紧剪贴板。不冲突。

#### F16 拖放（XDND）—— 本机文件拖进远端程序

- **场景**：把本机文件拖进远端的 gedit、IDE、GIMP；从远端程序拖出文本或 URL。
- **用途**：先经 SFTP 把文件传到远端临时目录，再以 `text/uri-list` 交出**远端路径** —— 这是 VelaShell 同时有 SSH、SFTP 与 X 服务端才做得到的结合，同类产品没有。
- **依据**：freedesktop 的 XDND 协议（要加进规范清单）。
- **工作量**：文本 1–2 周；文件部分要碰 SSH / SFTP 层，大。依赖 WN-E1（堆叠）与会话标签（F1 / F3）。不冲突。

### E. 宿主体验

#### F17 任务栏分组、记住窗口位置、老程序的图标

- **场景**：同一远端程序开了多个窗口散在任务栏；每次打开 xterm 都跳到屏幕中央；xterm、xclock 等老程序在任务栏没有图标。
- **用途**：按 WM_CLASS / window_group 归组（Windows 上设 AppUserModelID，可固定到任务栏）；按 WM_CLASS + Role 记住位置与尺寸（快照里已有这两个字段，宿主没用）；WM_HINTS 的 icon_pixmap / icon_mask 转成快照图标。
- **工作量**：小到中，宿主侧为主。不冲突。

#### F18 来源标识

- **场景**：同时转发着几台主机的程序；或远端程序画一个像本机凭据框的窗口（WN-S4）。
- **用途**：标题或任务栏标出远端主机 / 会话标签（可选会话色），一眼看出窗口来自哪里，防仿冒。
- **工作量**：小（连接器带会话标签之后）。不冲突。

#### F19 按显示器的 DPI 与整数倍放大

- **场景**：主屏 100%、副屏 200% 的混合 DPI；不认 DPI 的 Swing / Motif 程序在 200% 屏幕上太小。
- **用途**：每台显示器各自的 DPI 报给 RANDR / XSETTINGS；对选中的窗口做整数倍像素放大（画面会糊，坐标要换算）。
- **工作量**：中。插值缩放与「按 DPI 缩放不插值」的取舍冲突，只做整数倍。

#### F20 截图 / 录制 X 窗口

- **场景**：给远端程序的界面截图发给同事、录一段操作。
- **用途**：宿主已有 `CopyPixels` / `ReadPixels`，加一个入口即可截图；录屏要编码器。
- **工作量**：截图小、录屏中。不冲突。

### F. 字体与文字

#### F21 核心字体扩充

- **场景**：EDA（Virtuoso 的老界面、Synopsys 工具）、Motif / Xaw / 不带 Xft 的 Tk、xfig、老 Emacs 的菜单字体；默认核心字体模式的 xterm 显示中文文件名。
- **用途**：CP-16 与 WN-M9。不再回 BadName、界面不错位、中日韩能用核心字体显示。
- **两个方案**：
  - **A. 随库带数据**（3–5 天）：GNU Unifont 做全 Unicode 兜底（OFL，约 2–3 MB）；把度量与 Helvetica / Times / Courier 兼容的开放字体（如 Liberation）栅格化成 75 / 100 dpi 的位图字号、XLFD 字段对齐，加完整的 fonts.alias。数据从上游重新生成，遵守 AGENTS 纪律 4。
  - **B. 宿主字体提供者**（1–2 周，architecture §4 / §7 已预留）：宿主按请求现场栅格化系统字体成位图字体。
- **风险**：A 让库体积多几 MB；B 要处理 XLFD 匹配的全部字段与缩放字体请求。不冲突。

### G. 图形

#### F22 `GLX_ARB_create_context(_profile)`、多重采样 FBConfig、`GLX_EXT_libglvnd`

- **场景**：经 SSH 转发的 Qt Quick / GLFW / SDL / Blender / ParaView / VTK 走 drisw 要 3.3+ core profile（GL-M1）；要 MSAA 的可视化程序；远端同时装了 NVIDIA 与 Mesa 时让 libglvnd 选 Mesa 的 drisw（报 `GLX_VENDOR_NAMES_EXT = mesa`，效果 ❓ 实测）。
- **工作量**：小（create_context 约 150 行加用例），低风险；属于「直接渲染只做登记」的范围，不冲突。

#### F23 间接 GL 补 CAD 常用

- **场景**：慢链路上的老 CAD / 科学可视化（VMD、Motif + OpenGL 程序）—— 显示列表让几何只传一次，比 drisw 每帧传整个帧缓冲（1080p 30 fps 约 250 MB/s）现实得多。
- **内容与工作量**：选择模式（SelectBuffer、名字栈 121–125、命中记录；按图元裁剪后取 z 范围即可，约 300 行，中）—— 修掉 gluPickMatrix 拾取静默落空；线 / 多边形点画（约 80 行，小）；
  求值器（Map1/2、MapGrid、EvalMesh，约 400 行，中，要按 MAX_EVAL_ORDER 与数据长度严格校验）；边标记、GL_CLAMP 边框色、PolygonOffsetEXT（小）；mipmap LOD（中，片元开销上升）；像素传输 / PixelMap、深度与模板格式的 DrawPixels / CopyPixels（小到中）。
- **冲突**：都要把对应项从 architecture §7 的「不实现」里移出；**不追 GL 1.3+**（Q7）。

#### F24 光栅化提速

- **场景**：cairo-xlib 的 GTK2 程序、Java XRender 管线、Xft 文字；大网格、全屏清除与贴图的间接 GL。
- **内容**：RENDER 整数核 SIMD 化（`Vector128/256` 做 Over / Src / Add、OverSolidMask、双线性；现在只有 `CopyMasked` 用了 `Vector<uint>`），预计合成吞吐 2–4 倍；
  GL 光栅化改扫描线 + 增量边函数、派生状态缓存、struct 裁剪器（GL-P1～P2）。
- **工作量**：中；纯托管，与零依赖不冲突。现有「与浮点只差取整」的用例与 headless 像素对比可兜底。

#### F25 Present 用宿主真实帧节拍

- **场景**：xeyes 的 Present 模式、按 MSC 控帧的客户端（DR-M2）。
- **用途**：宿主的 `RequestAnimationFrame` 喂 MSC，按 target-msc 排队、帧到时发 CompleteNotify；动画平滑，客户端不再空转。仍是拷贝模式，不违反「只做软件拷贝」。
- **工作量**：中。

#### F26 超过 64 MB 的回复

- **场景**：多块 4K / 8K 屏上 `xwd -root`、`import -window root`（CN-E6）。
- **做法**：单条回复流式写出，或队列为空时放行单条超限的回复。**工作量**：小。

### H. 可观测性与平台

#### F27 指标与诊断

- **内容**：`System.Diagnostics.Metrics` 计数（被拒的连接、协议错误、被断开的连接、执行线程单项耗时分布），做法与 SSH 库的 ForwardMetrics 一致；单项超过 N 秒的看门狗日志点名客户端（X-1）；每客户端的资源与输出积压快照（F3）。
- **用途**：用户反馈「卡了一下」时有数据可看；配合 F3 的界面。**工作量**：小。

#### F28 X-Resource 的 LocalClientPIDMask

- **做法**：Linux 从 SO_PEERCRED 的 pid、macOS 用 `LOCAL_PEERPID`。**用途**：xrestop、xprop 与 `_NET_WM_PID` 交叉核对能显示真实 pid。**工作量**：小。

#### F29 WSL2 / Windows AF_UNIX

- **场景**：WSL 里的 Linux 程序直接用 VelaShell 的内置服务端，不装 VcXsrv、不依赖 WSLg。
- **做法**：监听 vEthernet 接口或用 mirrored 网络模式，把 cookie 写进 WSL 一侧的 `~/.Xauthority`。
- **工作量**：中。**风险**：监听非环回地址会扩大暴露面，必须要求 cookie 并先做 CN-S1。Windows 上取 AF_UNIX 对端 pid 的方式需要先查证。

#### F30 MIT-SHM 1.2（AttachFd / CreateSegment）

- **场景**：Qt5 / Qt6 的 xcb 后端遇到 1.2 会优先用 memfd；容器与沙箱里 SysV 共享内存常被关掉。
- **用途**：本机程序的共享内存不依赖 SysV，也绕开 CN-S9 的 IPC 命名空间问题。
- **工作量**：中到大。BCL 没有 SCM_RIGHTS，要 P/Invoke `recvmsg` 并管理 fd 生命周期；映射大小用 `fstat` 校验。依据 MIT-SHM 1.2 规范。

### 不建议做的

- **8 位 PseudoColor / 可写颜色表**：architecture §2 的非目标；现今 EDA 都跑 24 位，确有需要时远端跑 `Xephyr -screen WxHx8`。
- **DRI3 / Present 翻页**：经 SSH 传不了文件描述符，做了也没用。
- **间接 GL 1.3+、反馈模式、累积缓冲、VBO、多线程光栅化**：见 Q7；多线程光栅化还与「状态只在执行线程上碰」的总原则冲突。
- **会话保持（xpra 一类）**：需要远端代理，超出「本机 X 服务端」的定位；重度远程图形交给 RDP / VNC 插件。
- **Composite 真实的 Manual 重定向**（隐藏窗口、交给合成器画叠加窗口）：与 rootless 冲突。
- **深度 30 / 16 视觉**：与「只提供 24 位 TrueColor」冲突，SSH 转发下几乎用不到。
- **RECORD 扩展**（CP-30）：价值低，又是现成的键盘记录通道；真要做必须与 F2 同时、只给受信连接。
- **RANDR 写操作映射为宿主动作**（客户端改分辨率）：改根窗口会影响其它会话的窗口；与 architecture §7「RANDR 只读」冲突。
- **XKB 多组**（组切换对应宿主布局切换）：与「XKB 由核心表推出」的原则相冲，收益小。

---

## 十三、核对过、不必动的（防止以后重复审查）

- **连接与线格式**：连接建立报文（字节序标记、授权名 / 数据各自补齐、只接受主版本 11、失败回复布局）；成功回复的定长部分、vendor 补齐、7 个 FORMAT、SCREEN / DEPTH / VISUALTYPE 布局；
  `resource-base = index << 21`、`mask = 0x1FFFFF`，服务端自己的 ID（0x20–0x46、0x100）落在所有客户端范围之外；回复至少 32 字节、事件恰好 32 字节、错误报文布局、GenericEvent；序列号 unchecked 回绕、暂存的请求在真正执行时才加序号；
  QueryExtension、ListExtensions、GE QueryVersion、BigReqEnable、XC-MISC、X-Resource 0–5 的回复；操作码、错误码、事件码、掩码位、68 个预定义原子与附录 B 一致。
- **BIG-REQUESTS 与背压**：0 长度读扩展长度（含 8 字节头、2 到 4 M 单位），未开启就收到 0 长度直接断开；每客户端未执行请求 1024 条 / 32 MB，单条比上限还大时前面执行完就放行（不死锁）；`ReserveRequestBytesAsync` 登记后再看一眼；所有发往客户端的字节都经 `Send` 计入 64 MB 上限。
- **授权**：判定顺序与 architecture §7 一致；`FixedTimeEquals`、名字精确匹配；默认只听 `127.0.0.1`；套接字文件在 bind 与 listen 之间 chmod 0600；SO_PEERCRED 结构解析正确，在编号不同的架构上取不到 uid 时偏保守；残留套接字文件先试连、有人应答就不删。
  `.Xauthority`：O_EXCL 临时文件 + 0600 + 原子 rename，整份解析不了就不改写，`Remove` 只撤 cookie 也对得上的那一条，只写 FamilyLocal + 主机名（不写 FamilyWild），cookie 16 字节 CSPRNG、每次启动重新生成。
- **SSH 接驳**：假 cookie 只认 MIT-MAGIC-COOKIE-1、常数时间比较、授权字段 256 字节上限、读到头就判；连接器路径上真 cookie 不出进程；远端 EOF 能传到服务端；服务端停掉后连接器按「此刻在运行的那个」取实例。
- **MIT-SHM**：只在 Linux 注册、只对 Unix 套接字连进来的客户端可见（分派时同样判断）；附加者以外的客户端用段时按附加时记下的属主与权限再核一次、读写分开；忽略组权限（比 ipcperms 更严）；`offset + length` 用 long；`/proc/sysvipc/shm` 按表头定位列；只在执行线程上 shmdt。
- **执行循环**：宿主回调与日志都在放锁之后交出；回调异常被吞掉记日志；GrabServer 暂存与放回保持原顺序、持有者断开自动解除；SYNC 与 XTEST 暂存的请求在断开时丢弃、计时器取消；`DisposeAsync` 在循环结束后才碰 `_clients` / `_resources`；`XClient.Abort` 同时取消读写。
- **窗口与属性**：CreateWindow 的 BadValue / BadMatch 条件、InputOnly 的属性限制；三种独占事件的 BadAccess；GetWindowAttributes 44 字节布局；Destroy 先 Unmap 再按后序发 DestroyNotify（显式栈）、随之清理选区 / 焦点 / 抓取 / 宿主句柄 / save-set 登记 / 扩展钩子；
  Map 先 MapNotify 后 Expose、MapSubwindows 从上到下、UnmapSubwindows 从下到上；ConfigureWindow 的 sibling BadMatch、ConfigureRequest 字段、above-sibling、重画用新旧并集；ReparentWindow 的三方事件；QueryTree 从下到上；Expose 的 count；
  ChangeProperty 的 format / mode 校验与先算总长、GetProperty 的偏移算法与只在剩余 0 时删除、RotateProperties 的方向；InternAtom 的 only-if-exists 与上限；SendEvent 的传播、sent 位、跨字节序按布局翻转、拒绝 GenericEvent。
- **剪贴板与 XSETTINGS**：INCR 作为请求方的流程（16 MB 上限）、UTF8_STRING → STRING 退路、服务端作为属主时的 TARGETS / TIMESTAMP 与旧式请求方；XSETTINGS 线格式（serial、last-change-serial、补齐、Xft/DPI 以 1024 为单位），真正的设置守护进程来抢时让出。
- **SHAPE / XFIXES / 字体 / 光标 / 颜色**：SHAPE 1.1 的五种运算、默认形状、事件布局、位图转区域超限 BadAlloc、结果都是副本；XFIXES 区域运算操作副本、HideCursor 计数与断开清理；QueryFont / ListFontsWithInfo 的布局与结束回复、PolyText 换字体、缺字退回默认字符、通配不区分大小写；
  CreateCursor 的深度 / mask / 热点检查；TrueColor 下 AllocColor / QueryColors 换算、`#RGB` 与 `rgb:` 解析、默认颜色表不可释放。
- **绘图**：Region 分带归并与预算；Porter-Duff 标准 / Disjoint / Conjoint 三张 Fa / Fb 表、Saturate、Add 饱和；PDF 可分离与 HSL 混合公式；整数路径 Div255 不进位到隔壁通道、x8r8g8b8 读时 alpha 当 1；同缓冲 Detach；
  PutImage 先核长度再解码、各格式与位序 / 补齐；GetImage 的格式、plane-mask、深度 1 走位图；深度 1 / 4 / 8 / 15 / 16 写入按深度掩码、往返一致；池化数组不泄漏旧数据；CopyArea 重叠先拷出源、源外部分发 GraphicsExposure；
  GC 默认值、dash 为 0 BadValue、虚线状态跨 PolyLine 各段延续、CapNotLast；FillPoly 按 fill-rule、弧的角度截到 ±360° 并按参数角解释；RENDER 的格式 ID、四种 repeat、radial 取最大 t、变换在 +0.5 处取样、梯形与三角形的覆盖率只算可写区域、a4 字形存低 4 位。
- **DAMAGE / DBE / Present / SYNC / Composite**：DAMAGE 四种报告级别与 more 位、FreePixmap 后 Damage 照常可用；DBE 的 Undefined / Copied、Untouched 保存前缓冲、改尺寸时后缓冲跟着改；Present 三种事件布局、valid / update 区域、idle fence、NotifyMSC 每客户端 256 条；
  SYNC 的 INT64 编码、溢出 BadValue、系统计数器只读、Await 暂存后续请求且字节预算有界、计数器销毁时 destroyed = True、求值有重入保护；Composite 的 Manual 互斥、根窗口不能重定向。
- **输入**：事件传播与 do-not-propagate、child 字段、state 取事件之前；Enter / Leave 五种 detail 及次序、抓取期间只报给抓取方；焦点 detail 与 FocusIn 后的 KeymapNotify；motion hint 按「窗口 + 轮次」；隐式抓取的 owner-events 与掩码；
  冻结按设备、两设备都放行时按原先后回放、Replay 跳过抓取窗口及祖先的被动抓取、抓取被替换 / 解除 / 断开 / 不可见时解冻；键码范围校验、ChangeKeyboardMapping 列数只放宽不收窄、锁定键只在首次按下翻转、锁存修饰键用一次即解除。
- **XKB / XI2 / XTEST / RANDR**：XKB GetMap 头部与各段计数自洽、GetNames 次序、GetControls / GetState / StateNotify / MapNotify / IndicatorStateNotify / GetCompatMap / LatchLockState / SelectEvents 字段宽度、SetMap 各段次序与修饰映射键码校验；
  XIQueryDevice 各类长度、DeviceEvent / RawEvent / HierarchyEvent 布局、XIChangeHierarchy 部分生效时带已生效条数、设备 ID 封顶 255；XTEST 延迟期间暂存的请求仍受背压、伪造输入走冻结队列；RANDR 1.5 各回复布局、宿主已把负坐标显示器平移成非负。
- **GLX 与软件 GL**：上下文标签按客户端分表、不接受 0、回绕跳过 0；Render 先整条校验再执行、GLXBadRenderRequest 带出错前的命令数；RenderLarge 的第一段校验、64 MB 上限、段号连续性、断开时丢弃半截；ReadPixels 先 long 算 `PackedSize`；Bitmap / DrawPixels 的 `needed` 用 long；
  TexSubImage / CopyTexSubImage 的 offset + size 用 long（§124 的修复有效）；纹理采样边界检查、NaN 坐标饱和到 0；三角形包围盒夹到裁剪框、NaN / 无穷面积丢弃；线宽与点大小夹到 64；GenLists / DeleteLists 用 long；Begin / End 之间至多 2¹⁹ 顶点；
  MakeCurrent 先备表面（§124 有效）；FBConfig / VisualConfig 属性表与回复长度；所有串带结尾 NUL；Get*v 的回复布局；矩阵、光照方程、Table 2.12 的提供顶点、正反面判定、雾、alpha → 模板 → 深度的顺序、混合与逻辑运算、BGRA / ABGR 与紧缩类型的打包解包。
- **公开面与宿主**：全部回调经 DeferredHost 在放锁后按顺序调用；PixelGate 让行正常；`ReadPixels` 宽高取自缓冲本身，宿主尺寸不符时重建位图；快照整份替换、Diff 分组正确、形状与图标沿用引用；`CheckHandle` 拒收别的服务端的句柄；
  `SetKeymap` 只发一轮通知；宿主损伤先合并再投递、按帧取、先锁位图再进像素锁；失活与失去捕获时松开按键与按钮、AltGr 假 Ctrl 被过滤；光标预乘 BGRA、图标非预乘 BGRA；日志不含剪贴板内容与按键、不打 cookie；设置里的 27 个布局随程序带的键位表全都有。

---

## 附录 A　能力清单（2026-10-06 的现状）

路径相对 `src/VelaShell.XServer/`。

### 协议与扩展

| 类别 | 有 | 版本 / 备注 |
| --- | --- | --- |
| 核心 | 全部 119 个核心请求 | ChangeKeyboardControl / ChangePointerControl / SetPointerMapping / ChangeHosts / SetAccessControl 接受但不生效（IN-E16、CN-M2）；可写颜色单元 BadAlloc |
| 基础 | BIG-REQUESTS、XC-MISC、Generic Event | GetXIDList 一次 ≤ 65536 |
| 窗口与绘图 | SHAPE 1.1、XFIXES 5、RENDER 0.11、DAMAGE 1.1、Composite 0.4、DOUBLE-BUFFER 1.0、Present 1.2、SYNC 3.1 | RENDER 源裁剪 / alpha-map 不生效；Present 不等 target-msc / wait-fence（DR-M2）；Composite 只读式（rootless） |
| 共享内存 | MIT-SHM 1.1 | 仅 Linux、仅经 Unix 套接字的客户端可见；不做共享像素图与 1.2 的 fd 传递（F30） |
| OpenGL | GLX 1.4 | 直接渲染（drisw）只登记；间接渲染为固定功能 1.1 子集（无选择 / 反馈、求值器、累积、mipmap LOD、点画）；FBConfig 4 个（无 MSAA）；没有 create_context（GL-M1） |
| 输入 | XKEYBOARD（含 SetMap）、XInputExtension 2.2（含 XI 1.x 查询、XIChangeHierarchy）、XTEST 2.2 | XKB 由核心表推出（6 个类型）；XI2 没有滚动类、触摸、手势；DetectableAutoRepeat 只登记 |
| 显示器与电源 | RANDR 1.5（对客户端只读）、XINERAMA 1.1、MIT-SCREEN-SAVER 1.1、DPMS 1.1 | 布局由宿主给 |
| 诊断 | X-Resource 1.2 | 不列保留的客户端（CN-M4） |
| 没有 | SECURITY、RECORD、XVideo、DRI2 / DRI3、XIM、XDND 中转、CLIPBOARD_MANAGER、`_NET_SYSTEM_TRAY_S0`、`_NET_WM_CM_S0` | 见 F2、F5、F11、F12、F14、F16 与「不建议做的」 |

### 窗口管理器与 XSETTINGS

- **EWMH**：`_NET_SUPPORTED`、`_NET_SUPPORTING_WM_CHECK`、`_NET_CLIENT_LIST(_STACKING)`、`_NET_ACTIVE_WINDOW`、`_NET_WM_STATE`、`_NET_FRAME_EXTENTS`、`_NET_REQUEST_FRAME_EXTENTS`、`_NET_MOVERESIZE_WINDOW`、`_NET_WM_MOVERESIZE`、`_NET_CLOSE_WINDOW`、`_NET_WORKAREA`（恒为整个根窗口）、`_GTK_FRAME_EXTENTS`（解析）。
  没有：`_NET_WM_PING`、`_NET_WM_SYNC_REQUEST`、`_NET_RESTACK_WINDOW`、`_NET_WM_FULLSCREEN_MONITORS`、`_NET_WM_ALLOWED_ACTIONS`、`_NET_WM_USER_TIME` 的使用。
- **ICCCM**：WM_STATE、WM_PROTOCOLS（WM_DELETE_WINDOW、WM_TAKE_FOCUS）、WM_TRANSIENT_FOR、WM_CHANGE_STATE、WM_HINTS 的 input 与 urgency、WM_NORMAL_HINTS 的最小 / 最大 / 步长、合成 ConfigureNotify（§4.1.5）。
  没有：initial_state、icon_pixmap / icon_mask、window_group、base size、aspect、win_gravity、USPosition / PPosition（WN-M2）、save-set（WN-S3）。
- **XSETTINGS 管理器**：`_XSETTINGS_S0`，发布 `Xft/DPI`、`Gdk/WindowScalingFactor` 等；根窗口 RESOURCE_MANAGER（`Xft.dpi`，换 DPI 时整份覆盖，WN-E14）。
- **剪贴板**：CLIPBOARD（默认开）、PRIMARY（库默认关，宿主默认开），只有文本（UTF8_STRING / STRING / TEXT），INCR 只在 X → 宿主方向。

### 字体、颜色与视觉

- 视觉：TrueColor 深度 24 与 32（ARGB）；像素图另可建 1 / 4 / 8 / 15 / 16。
- 核心字体：内置 misc-fixed 5 个（6x13、6x13B、9x15、9x15B、10x20，字形裁到拉丁区与少数符号区块）、7 个别名、合成的 `cursor` 与 `nil2`。
- 颜色名：基础名表 + grayN / greyN；没有 rgb.txt 的编号变体（WN-M1）。

### 资源上限（单件）

客户端 255；窗口嵌套 256 层、每客户端 32768 个；区域 16384 块；像素缓冲 2²⁶ 像素；属性值 32 MB；客户端建的原子 2¹⁸ 个 / 名字合计 16 MB；XI2 设备 ID ≤ 255；未执行请求 1024 条 / 32 MB；输出积压 64 MB；
连接建立 30 秒；GLX（每共享组）显示列表 65536 个 / 64 MB、纹理 65536 个 / 256 MB、一个图元 2¹⁹ 顶点、表面 4096²、一个请求的列表展开 400 万条。**没有每客户端或全局总量**（X-2）。

### 宿主接口

- 回调：`TopLevelMapped` / `TopLevelUnmapped` / `TopLevelChanged`（附 `XTopLevelChanges`）、`TopLevelDamaged`、`CursorChanged`、`BellRequested`、`ClipboardChanged`、`WindowManagerRequested`（移动 / 缩放拖拽、改状态、激活、关闭、最小化）。
- 方法：`Inject*`（指针移动 / 按钮 / 离开、按键）、`*TopLevel`（焦点、移动、缩放、关闭、状态、外框）、`Set*`（键位表、修饰映射、显示器布局、DPI 与缩放、剪贴板文本）、`ReadPixels` / `CopyPixels`、`ServeAsync` / `ServeAuthenticatedAsync`。
- 没有：堆叠 / 升起、锁定键状态、强制结束 / 客户端枚举 / 连接标签、BreakGrabs、滚动增量、Warp 回调、按显示器 DPI、工作区、带超时的读像素（见第八节「宿主的绕路说明库缺什么」与 X-1）。

---

## 附录 B　建议加进 interop 靶场的程序

建议分两个镜像：现有的 `velashell-xclients` 保持轻量、留在 CI；另建 `velashell-xclients-heavy` 放 JRE、浏览器、LibreOffice 这类大件，手动或每晚跑。Qt6 与较新的 GTK4 可以考虑把基础镜像换成 trixie。
断言沿用现有约定（零 `: Bad` 日志 + 非背景像素），再加下表每行的专项检查；CP-1、CP-9、CP-11 的专项检查靠 `run-server.cs` 的 `cmd.txt` 注入（`click`、`resize` 已有）。

| 代表 | 包（Debian） | 测什么 / 怎么断言 |
| --- | --- | --- |
| Java Swing | `default-jre` + 一个单文件 Swing 程序 | CP-9：`resize` 后内容像素要重排；拖动后弹出菜单位置正确；模拟占住 `WM_S0` 前后各跑一遍 |
| GTK3 全面 | `gtk-3-examples`（gtk3-demo）、`zenity`、`meld` | 拖放、剪贴板、弹出层、自绘标题栏边缘缩放（CP-11）、滚动 |
| GTK4 / libadwaita | `gtk-4-examples`、`gnome-calculator` | `GSK_RENDERER=gl` 与 `cairo` 都零错误，记下字节数 |
| GTK2 | `gimp` | XI 1.x 查询路径 |
| Qt5 / Qt6 | `wireshark`、`qt5ct`（已有）、trixie 的 `qt6ct` ❓ | XKB、XI2；本机 Docker 的 MIT-SHM（CP-14） |
| 浏览器 | `firefox-esr`；`chromium --no-sandbox --disable-gpu` | 打开 data: URL 零协议错误；`xclip -selection clipboard -t TARGETS -o` 看它提供哪些格式 |
| Motif / Xaw / Tk / Emacs | `ddd`、`xfig`、`gnuplot-x11`、`gitk`、`emacs-lucid`、`emacs-gtk`；已有的 `xedit`、`xcalc`、`xterm` | CP-16：统计「Cannot convert string … FontStruct」告警；不带 `-fa` 的 xterm 显示中文与西里尔文截图；WN-M1：`ls --color` 截图 |
| 3D | `mesa-utils`（已有）、`mesa-utils-extra`、glmark2 | GL-M1：`glxinfo -B` 必须有 core profile 一行（先记为已知失败） |
| 桌面会话 | `xserver-xephyr`、`openbox`、`xfwm4`、`xfdesktop4`、`xfce4-panel` | WN-S3：修好后 `openbox` 必须以「已有窗口管理器」退出；CP-24：桌面窗口不应映射成原生窗口；Xephyr 能嵌一整个桌面 |
| 托盘 | `yad --notification`、`stalonetray` | CP-26（用 stalonetray 当托盘管理器验证 X 程序之间的 XEMBED） |
| 中日韩输入 | `fcitx5 fcitx5-chinese-addons dbus-x11 fonts-noto-cjk` | CP-28 |
| 窗口管理与工具 | `wmctrl`、`xsel`、`scrot`、`imagemagick` | WN-E1：两个 xterm 重叠，激活后面那个、在重叠区点击，用 `xdotool getmouselocation` 与 xev 看事件落到哪；`wmctrl -m`、`wmctrl -l` |
| 办公（heavy） | `libreoffice-calc libreoffice-gtk3` | 剪贴板富格式；启动时恢复最大化（CP-8） |

---

## 2026-10-09 复核：上一轮之后的剩余项

这次复核以当前 `dev` 代码、XServer 单元测试和 `plan.md §167` 为准。原文中的若干“未实现”描述是审查时的快照，不能直接当成当前状态。

### 已完成或已由上一轮提交覆盖

- `DrawArrays` 已按 `ARRAY_INFO` 的出现顺序读取逐顶点数据，并有回归测试。
- WN-S11 的选区按会话隔离已完成：其它会话看不到属主变化，也收不到对应的 `SelectionClear` / XFIXES 通知。
- GL-M1 的 `GLX_ARB_create_context` / `_profile` 已完成；GLX 表面释放、纹理悬空、RenderLarge 等审查项已有代码和测试覆盖。
- 本次继续完成 DR-M4：源 picture 的 alpha-map、alpha 原点、alpha-map 约束与裁剪已实现，并有 Composite 回归测试。
- 本次继续完成间接 GLX 单缓冲视觉：`GetVisualConfigs` 为两个视觉同时发布单缓冲与双缓冲配置，并有配置 ID / DOUBLEBUFFER 回归测试。

### 仍未完成的审查项

- **DR-M1**：PolyArc 连续弧之间的 JoinMiter / JoinRound / JoinBevel 接头。
- **DR-P3**：a1 目标、Disjoint / Conjoint Porter-Duff 以及 PDF 混合模式的整数快速路径。
- **WN-M5**：cursor 字体的真实字形位图；当前仍主要按字形号映射宿主系统光标形状。
- **CP-16 / F21**：完整核心字体数据仍未随库提供，当前只有少数 misc-fixed 字号、别名和近似字号回退。
- 宿主侧新功能仍未完成：F1/F2 会话隔离档、F3 X 程序清单与强制结束界面、F8 WarpPointer、F9 屏保协作、F10 同步缩放、F11 合成管理器、F12 托盘、F13 rootful 单窗口模式、F14–F20 桌面集成、F22–F30 扩展功能。

### 仍缺环境验证的项目

- Docker 真实客户端互操作本轮未运行，因为当前环境没有 Docker；需要重建 `velashell-xclients` 后验证 GTK、Swing、GLX 与 gnome-calculator。
- API-H13（分数缩放最后一列像素）、IN-E19（macOS Command 组合键 KeyUp）、CN-S8（macOS / FreeBSD `getpeereid`）仍需对应平台实机。

### 当前验证结果

- `VelaShell.XServer.Tests`：430 通过、9 个平台条件跳过。
- `VelaShell.XServer` Release 构建：0 警告、0 错误。
