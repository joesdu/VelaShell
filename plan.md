# VelaShell 项目进展与参考文档

> **这份文件记录「已经发生的事」** —— 当前架构、关键约定，以及每一次「为什么这么改」的来龙去脉。
> 它是开发跟进的事实来源。
>
> **还没发生的事在 [`feature-plan.md`](feature-plan.md)** —— 待办、候选特性、已决策不做的清单。
> 两份文件的分工是硬的：一件事做完了，就从 `feature-plan.md` 划掉、在这里补一节；
> 一件事还没做，就不要在这里留 TODO。
>
> **2026-09-26 精简**：原文 7,200 行，每节压成「做了什么 + 关键决定 + 值得记住的坑」，
> 删掉了代码对照、测试清单与逐步排查过程。完整原文在 git 历史里（本次精简之前的版本即是）。
> **2026-10-13 第二次精简**：此后新写的 §118 – §202 按同一口径压缩（全文 3,000 行 / 440 KB → 1,850 行 / 230 KB），
> 正文里的「还没做」一律摘进 `feature-plan.md`。精简前的全文：`git show eba8452d:plan.md`。
> **节号一律保持不变** —— 代码注释、CI 配置、测试、`feature-plan.md` 与 velashell-docs 都按「plan.md §NN」引用它们，
> 节内的小标签（§71-六、§162 的 Q7、§201 的一至六这类）也照原样；两个 §82、两个 §92 是历史上撞号留下的，照原样保留。

## 🏷️ 状态标识

| 标识 | 含义 |
| :---: | --- |
| ✅ | **已完成** —— 已落地、有测试、可验收 |
| 🚧 | **部分完成** —— 主线可用，剩下的缺口在 `feature-plan.md` |
| 📖 | **参考资料** —— 描述当前状态或约定，不是「一件要做的事」 |
| ⚠️ | **坑** —— 会绊到人的地方 |

## 🗺️ 全文索引

**一、当前状态与约定**（长期有效，随代码更新）：
[§1 技术栈](#-1-技术栈现状) ·
[§2 解决方案分层](#-2-解决方案分层) ·
[§3 终端引擎](#-3-终端引擎) ·
[§4 SSH / PTY](#-4-ssh--pty) ·
[§5 VelaDock](#-5-停靠--分屏veladock) ·
[§6 UI 与设置](#-6-ui--视图与设置) ·
[§7 测试](#-7-测试) ·
[§8 关键约定与已知坑](#-8-关键约定--已知坑)

**二、进展记录**（按时间顺序，最新在最下）：

| 时段 | 节 | 主线 |
| --- | :---: | --- |
| 07-08 | §9 | SonnetDB 存储层、两步登录、设置窗口 |
| 07-09 ~ 07-10 | §10 – §12 | 设置接线复盘、设计稿与竞品缺口（未完成项已迁往 `feature-plan.md`）；OSC 8 / OSC 133（§10-D / §10-E，09-08 补记） |
| 07-11 ~ 07-14 | §13 – §16 | 设置审计整改、Gist 云同步、会话录制、五语言、发布流水线、VelaDock、自绘窗口壳 |
| 07 下旬 ~ 08-14 | §17 – §18 | SSH.NET → Tmds.Ssh、SFTP 双栏、插件系统 v1 + AI 插件、资源监视、路由追踪、FTP、全局代理 |
| 08-30 ~ 08-31 | §19 – §28 | 隧道计量与自愈、消息中心与资讯源、具名主题 12 套 |
| 09-01 ~ 09-07 | §29 – §49 | 插件收据、AI 模型清单、协作接入（IM 桥接 + 对外 MCP）、插件开会话、连接体验一批修复、CI 偶发失败 |
| 09-08 ~ 09-10 | §50 – §68 | 远程编辑回传（#396 四轮）、插件发布者连续性、SSH config 导入、选区对比度、SSH 证书认证、VelaDock 窗格、防空闲、协议图标 |
| 09-11 ~ 09-15 | §69 – §77 | 插件图标与更新、符号链接、目录比较与同步、中文预编辑、Linux 走 XWayland |
| 09-17 ~ 09-21 | §78 – §90 | 冷启动与 Defender、出站代理规约、SIMD 调研、资源管理器置顶、指纹变更弹窗、Xshell 外部拉起、Ed25519 / ECDSA、AI 面板卡顿 |
| 09-22 ~ 09-23 | §91 – §103 | 换成 VelaShell.Ssh 并并入本仓库、Agent / X11 / 压缩、自动加钥、agent 转发限定、VelaShell.XServer M1 – M2 |
| 09-23 ~ 09-24 | §104 – §113 | XServer 功能完备与 M3（接入宿主）、AltGr、M4（GLX 等）、键盘布局、SSH 库三批全库审查与主机证书 |
| 09-25 ~ 09-26 | §114 – §124 | XServer 审查与渲染路径、SSH 库 API 规范、窗口外框跨平台适配、状态栏对齐、CI 行尾、软换行长行复制、XServer 审查 31 项修完 |
| 09-27 ~ 09-29 | §125 – §143 | PTY 像素贯通、CI 偶发、SFTP 双栏远程、窗口位置、快捷键可改、共享凭据 |
| 09-30 ~ 10-03 | §144 – §159 | chown、连接对话框改版、审计与快捷命令、SSH agent 修补 |
| 10-05 ~ 10-07 | §160 – §166 | SSH 库审查落地、FIPS 混合 KEX、CI SSH 红用例 |
| 10-07 ~ 10-08 | §167 – §172 | XServer 二次全库审查修复、CI 红用例、Linux 检查更新打开发布页 |
| 09-22 ~ 10-09 | §173 – §174 | AI 插件:健康检查与故障转移、同供应商多 API Key(#582) |
| 10-09 ~ 10-10 | §175 – §193 | 悬停闪烁真因(#577)、侧栏两个开关(#586)、AI 插件复审与修复、XServer 二次审查收尾与第十二节新功能(程序清单、信任级别、拖放、托盘、XIM 桥)、连接导入导出与批量操作(#571)、隧道面板一批修复、启动画面、SSH 库四处修补 |
| 10-11 ~ 10-14 | §194 – §208 | 清零编译警告、CI 偶发的两处根因、X 程序一打字就退出、上传选择框(路径输入、键入定位、图标视图)、IPv6(#611)、文件栏路径输入与键入定位、X Server 帧率、Responses 工具循环、SFTP 确认进度与续传测速、目录收藏 |
| 10-14 ~ 10-24 | §209 – §234 | 更新下载速率、Tailscale SSH、选区同文高亮与共享密码填入(git 提示、自动回车)、apt 大包回滚、su / sudo 跟随目录、诊断包与日志录制遮盖、渲染停顿测量、测试环境欠账、复审 #616–#667、AI 插件(换源清 Key、空回复重试、上传 / 下载、长任务续接、Anthropic SDK 13)、更新日志读 latest.json |
| 10-24 | §235 – §248 | 一批欠账与路线图收口:标签条(底部、「+」、纵排溢出)、布局模板、设置导出脱敏与审计补全、传输重试与队列持久化、文件侧五项、会话树过滤与标签页图标、自动下载更新与插件日志、主机 CA 与 OpenSSH 互通、X 服务端收尾、隧道接上库的转发能力、命令块复制与重跑、SSH 诊断五语、协议 / 工作台插件模板、SSH 连接信息面板 |
| 10-25 | §249 – §269 | 共享密码列表认出 sudo-rs、输入框高度按设计生效;维护者 2026-10-25 一轮拍板之后的一批:SFTP 双栏接线债五项、SSH 库限速分段与 FIPS 补 group18、手册三处安全说法改代码对齐、本机加密密钥交给系统密钥链、SSH 库其余中文诊断结构化、诊断包按项勾选与通用遮盖、发布页只放行本仓库、文件侧六处收尾、AI 上传四项口径、配置审计来源与输出遮盖重写、系统密钥库认证 / 协议轨迹 / agent 端点、SDK 五项契约(velashell-plugin-sdk 本地 dev,未发版)、生态仓库欠账(docs / templates / plugins / sdk 本地 dev,未推送)、布局模板同步与导出、AI 插件安全边界与复审遗留、终端输出规则(高亮 + 自动应答)、AI 供应商换源与凭据、一键放开算法推广到其它入口、会话健康巡检、「+」精确落点 |

## 📈 阶段脉络

```mermaid
timeline
    title VelaShell 主线演进
    2026-07 上旬 : SonnetDB 存储层 : 两步登录 : 设置中心成型
    2026-07 中旬 : VelaDock 替换 Dock.Avalonia : 自绘窗口壳 + Snap Layouts : 五语言全量补齐 : 版本与发布流水线
    2026-07 下旬 : SSH.NET → Tmds.Ssh : SFTP 双栏与断点续传 : net10 → net11
    2026-08 上旬 : 资源监视 / 路由追踪 / 连接诊断 : 会话导入 : FTP / FTPS
    2026-08 中旬 : 插件系统 v1 双宿主 : AI 助手插件 : 全局网络代理 : MSIX 商店版
    2026-08 下旬 : 隧道计量与自愈 : 消息中心与资讯源 : 具名主题 12 套
    2026-09 上旬 : 协作接入 IM 桥接 + 对外 MCP : 远程编辑回传 : SSH 证书认证 : 目录比较与同步 : 三平台 CI 门禁
    2026-09 中旬 : 冷启动排查 : 出站代理规约 : Ed25519 / ECDSA 密钥
    2026-09 下旬 : Tmds.Ssh → VelaShell.Ssh 并入本仓库 : Agent / X11 转发 : 内置 X 服务端 VelaShell.XServer : 窗口外框跨平台适配
    2026-09 末 ~ 10 月初 : SFTP 双栏两侧远程 : 新建连接对话框改版 : 快捷键可改 : 共享凭据
    2026-10 上中旬 : SSH 库审查落地 + FIPS 混合 KEX : XServer 二次审查与新功能 : 连接导入导出与批量操作 : 启动画面 : IPv6 : X Server 帧率 : 上传选择框图标视图
    2026-10 下旬 : AI 传输与长任务 : 命名布局模板 : 传输队列持久化 : 主机 CA 与 OpenSSH 互通 : 隧道速率 / 限速 / 远程动态 / Unix 套接字 : 命令块复制与重跑 : SSH 连接信息面板 : 本机加密密钥交给系统密钥链 : 系统密钥库认证与协议轨迹 : 终端输出规则 : 布局模板同步 : 会话健康巡检
```

## 🧭 当前基线（2026-10-14）

| 项 | 值 |
| --- | --- |
| 发布 | 以 [GitHub Releases](https://github.com/joesdu/VelaShell/releases) 为准。仓库里 `Directory.Build.props` 的 `0.0.1-dev` 是开发期占位，发版由 Release 标签经 `-p:Version` 覆盖 |
| 测试 | 最近一次全量记录：4800 条，**4761 通过 / 1 失败 / 38 跳过**（2026-09-26，§124；失败的是 Core.Tests 的 X11 靶机用例，本机靶机镜像旧）。之后各节只按改动涉及的测试工程记数（如 2026-10-13 `VelaShell.Tests` 2078 通过 / 19 跳过）；`-warnaserror -p:EnforceCodeStyleInBuild=true` 下构建零警告（§194） |
| 测试项目 | 11 个 MSTest 项目 + 1 个 BenchmarkDotNet 项目（见 §7） |
| CI | `ci.yml` 三平台矩阵（windows / ubuntu / macos），push `main` 与全部 PR 触发 |
| 待办 | 见 [`feature-plan.md`](feature-plan.md) |

## 📖 1. 技术栈现状

> 版本以 `Directory.Build.props`、`src/Directory.Packages.props`、`tests/Directory.Packages.props` 为准；
> 下表是 2026-10-13 的快照。

| 项 | 版本 / 说明 |
| --- | --- |
| .NET | **net11.0**（2026-07 由 net10 切入）。不再钉 SDK（2026-10-14 删掉 `global.json`，#625）：本机用装着的最新一版 11.0 预览 / RC，CI 装 `11.0.x` 的 preview 通道，并在 setup-dotnet 之后打印 `dotnet --info` 留下实际版本（§222）；`Directory.Build.props` 开 `EnablePreviewFeatures`、`runtime-async=on`、`LangVersion=preview` |
| UI | **Avalonia 12.1.4** · ReactiveUI 26.0.1 / ReactiveUI.Avalonia 12.1.6 |
| 停靠 | **VelaDock**（`src/VelaShell/Docking/`，零第三方停靠依赖，已替换 Dock.Avalonia） |
| SSH / SFTP | **VelaShell.Ssh**（`src/VelaShell.Ssh`，MIT，全托管 async-first；2026-09-22 替换 Tmds.Ssh，§91；09-23 并入本仓库，§92）。只有 `Infrastructure/Ssh/` 直接用它，异常经 `SshInterop` 翻译。密码学原语走 BCL 与 BouncyCastle 2.7.0 |
| X11 | **VelaShell.XServer**（`src/VelaShell.XServer`，MIT，可嵌入的 X 服务端，§101 起）；Windows 上可选改拉起 VcXsrv |
| FTP | **FluentFTP 55.0.0**。⚠️ **不要**引 FluentFTP.GnuTLS —— LGPL-2.1-only，与商业授权冲突 |
| 持久化 | **SonnetDB.Core 4.0.0**，嵌入式多模型（文档 + 时序），`~/.velashell/sonnetdb`；LiteDB 已移除 |
| IP 归属地 | MaxMind.Db 5.2.0（只是 mmdb 读取库，数据是 DB-IP Lite City，CC BY 4.0） |
| 插件契约 | **VelaShell.PluginSdk 2.0.9**（nuget.org 包，**不做工程引用**，版本 pin 在 `src/` 与 `tests/` 两份 `Directory.Packages.props`） |
| 打包 | 便携 zip / tar.gz（6 RID）+ `.AppImage` / `.deb` / `.rpm` / `.dmg` / MSIX；应用内自更新走 GitHub Releases 的 `latest.json`（Velopack、WiX MSI 均已移除） |
| 依赖管理 | 集中式包管理（`ManagePackageVersionsCentrally`）+ SourceLink。⚠️ `plugins/` 下的插件**不走**中央包管理，版本写在各自 csproj |
| 测试 | **MSTest 4.5.1**（已从 xUnit 全量迁移，FluentAssertions 已移除）· Avalonia.Headless 12.1.4 · BenchmarkDotNet 0.16.0-preview.2 |
| AI 栈 | Microsoft.Extensions.AI 10.10.0 · ModelContextProtocol.Core 2.2.0 · LiveMarkdown.Avalonia 2.4.3（含 Mermaid / Math / Svg）—— 只在 AI 插件里 |

## 📖 2. 解决方案分层

```text
src/
├── VelaShell/                桌面入口、DI 组合根、视图、App 层 ViewModel、VelaDock 停靠、X 服务端的 Avalonia 宿主
├── VelaShell.Presentation/   跨层 ViewModel、连接 / 隧道工作流服务
├── VelaShell.Controls/       自定义控件（LucideIcon 等）、设计令牌、内置 Cascadia Mono 字体
├── VelaShell.Terminal/       VT 终端引擎 + 自绘渲染控件
├── VelaShell.Core/           领域模型、抽象契约、数据存储接口、协议引擎、本地化（无 UI 依赖）
├── VelaShell.Infrastructure/ SSH / SFTP / FTP / 隧道实现、SonnetDB 持久化、插件管理与能力实现
├── VelaShell.Ssh/            SSH 库（MIT，净室规程见其 AGENTS.md）
├── VelaShell.XServer/        X11 服务端库（MIT，净室规程见其 AGENTS.md）
└── VelaShell.PluginHost/     隔离插件的宿主进程（命名管道 RPC，只依赖 SDK 契约）
plugins/VelaShell.Plugin.Ai/  第一方 AI 助手插件（同仓构建、同版发布；例外理由见 plugins/README.md）
tests/                        11 个 MSTest 项目 + 1 个 BenchmarkDotNet 项目 + cert-lab 证书靶机（见 §7）
```

**依赖方向**（Core 不依赖任何 UI 框架；两个 MIT 库不依赖宿主任何程序集）：

```mermaid
graph RL
    App["VelaShell<br/>(桌面入口 · DI 组合根)"]
    Pres["VelaShell.Presentation"]
    Ctrls["VelaShell.Controls"]
    Term["VelaShell.Terminal"]
    Infra["VelaShell.Infrastructure"]
    Core["VelaShell.Core<br/>(无 UI 依赖)"]
    Ssh["VelaShell.Ssh<br/>(MIT)"]
    X["VelaShell.XServer<br/>(MIT)"]
    Host["VelaShell.PluginHost"]
    Sdk["VelaShell.PluginSdk<br/>(NuGet 契约)"]
    Ai["plugins/VelaShell.Plugin.Ai"]

    App --> Pres
    App --> Ctrls
    App --> Infra
    App --> Host
    Pres --> Term
    Term --> Core
    Infra --> Core
    Infra --> Ssh
    Infra --> X
    Infra --> Sdk
    Host --> Sdk
    Ai --> Sdk

    style Core fill:#2d6a4f,color:#fff
    style Sdk fill:#5a3e85,color:#fff
```

> 箭头指向被依赖方。`PluginHost` 与 `Plugin.Ai` **只**认 SDK 契约，不依赖宿主任何内部程序集 ——
> 这正是插件能跨进程、跨 ALC 而类型仍然同一的前提。

## ✅ 3. 终端引擎

替换掉已停维护的 `AvaloniaTerminal 1.0.0-alpha.7`，VT 引擎在 `src/VelaShell.Terminal/Emulation/` 与 `Rendering/`：

- `VtParser` —— Paul Williams 的 DEC ANSI 状态机 + 独立 VT52 语法路径，消费 Unicode 标量，派发到 `IVtActions`。
- `TerminalScreen` / `TerminalRow` / `TerminalCell` —— 网格、主 / 备屏、DECSTBM、scrollback、tab stops。
- `TerminalEmulator` —— SGR（16 / 256 / 真彩）、光标与擦除、各种模式（DECAWM、括号粘贴、鼠标跟踪…）、DEC 线绘、DA / DSR 应答。
- `TerminalType` —— vt52 → xterm-256color 十种 profile，各带 TERM 名与 DA 应答，默认 xterm-256color。
- `Utf8Sink`（可配任意编码，GBK / Big5 靠 `CodePagesEncodingProvider`）、`CharWidth`（CJK 双宽）、`InputEncoder`（按键 → 字节）。
- `Rendering/VelaTerminalControl` —— 纯自绘 Avalonia `Control`，同时实现 `ITerminalEmulator` 接回 `SshTerminalBridge`。
  `ApplyLayoutSize` 拒绝 < 2 列 / 行的早期布局（修过「横幅每字一行」）。

## ✅ 4. SSH / PTY

- `SshTerminalBridge` 只读循环，**不向 shell 预写 `\n`**（修过「末行提示符重复」）。它的 `DataReceived` 旁路是记录专用的。
- **PTY 实时改窗**：控件布局时抛 `ITerminalEmulator.PtySizeChanged`（行列 + 物理像素的 `PtySize`，§125）→ `TerminalTabViewModel` 后台转发 →
  `IShellStreamWrapper.Resize` → `Infrastructure/Ssh/ShellStreamWrapper` 发 `window-change`。
- **连接失败不崩**：`MainWindowViewModel.TryConnectProfileAsync` 捕获认证 / 网络 / 超时，映射成本地化提示；
  `Program.cs` 挂了 `UnobservedTaskException` 与 `UnhandledException` 兜底。
- **连接持久化**：`SonnetDbSessionRepository`（`session_profiles` 集合，密码 AES-256-GCM）；最近连接取 `conn_history` 时序。
- 新建连接的密码框仅限 ASCII（`Behaviors/AsciiOnlyInput`）。

## ✅ 5. 停靠 / 分屏（VelaDock）

- **模型层** `Docking/Model/`（纯 INPC，可单测）：`DockWorkspace` / `DockGroup` / `DockSplit` / `DockDocument`。
  空组自动折叠（**只有根留着**，主组先把兜底身份交给邻居再退场，§64）；单子分栏自动提升；`MaximizedGroup` 只影响渲染、不动树。
- **控件层** `Docking/Controls/`：`DockWorkspaceControl` 按树渲染 Grid + GridSplitter，**按文档缓存视图**（切标签复用同一个 `TerminalTabView`）；
  `DockGroupControl`（标签条 + 溢出三连钮）、`DockTabItem`、`DockDragController` + `DockDropOverlay`（重排、跨组并入、五区拖放分屏）。
  **浮动窗口按产品决策不做。**
- **标签条**:横排可停到顶部或底部(工作区级的 `DockWorkspace.TabsAtBottom`),左 / 右侧按窗格挑(`DockGroup.TabsPosition`);溢出判定按排列轴(`TabStripOverflowConverter`);「+」只经 `DockWorkspaceControl.NewTabRequested` 报是哪一格,停靠层不建会话(§235)。
- **布局序列化在模型层之外**:`Docking/DockLayoutSnapshot` 把布局拍成 Core 的 `LayoutTemplate` 树、按树搭骨架;模型层为此只多了 `PlacementResolver`(新文档落点的外部裁决)、`InsertLayout` 与 `DocumentReplaced`(§236)。
- `Controls/ReparentingHost` 保证共享的终端控件任一时刻只有一个父级。方案见 velashell-docs `zh/host/dock-replacement-plan.md`。

## ✅ 6. UI / 视图与设置

- **窗口壳**：主窗与全部对话框在 Windows 上是自绘无边框（`WindowDecorations="None"`）+ 原生行为补齐 ——
  空白区 `BeginMoveDrag`、双击最大化、**Snap Layouts 经 `MainWindow` 的 WndProc 钩子处理 `HTMAXBUTTON`**（`ce71b32`）、四周自绘缩放抓取区。
  ⚠️ **为什么 Windows 不走 `ExtendClientArea`**：Avalonia 12.0.5 的托管装饰会重复画标题与按钮、`WindowDecorationsElementRole` 的输入重定向没落地（按钮点不动），
  `BorderOnly` 还丢 `WS_CAPTION`（issue #21160 / #21212）。另一个坑：`VisualRoot as Window` 恒为 null，取窗口要走 `FindLogicalAncestorOfType<Window>()`。
  **以上只管 Win32**：2026-09-26 起 macOS 与 Linux 按平台走原生机制，统一入口 `Views/WindowChrome.cs`，见 §118。
- 文字菜单栏已整体移除（与命令面板重复，用户决策）；标题栏右侧是功能图标组，全部走命令注册表。
- **命令面板**（`Ctrl+P`；`Ctrl+K` 不绑，见 §154）：模糊子序列搜索、分类分组，条目 = 最近会话 + 全局命令。
- **状态栏跟随激活标签**：连接串 / 状态 / 终端类型 / 编码 / 尺寸 / 延迟。
- **设置窗口 14 页**：常规 / 外观 / 终端 / 密钥管理 / 共享凭据（§157）/ 快捷键（全局与标签类可改键、解绑，§155）/ 文件传输 / 安全审计 / 网络代理 / X Server / 代码片段 / 云同步 / 关于 / 支持与捐赠。
  逐项审计见 velashell-docs `zh/host/settings-audit.md`。
- **终端配色跟随主题**：未自定义时随具名主题配对的终端方案实时切换（§25、§26）。

## 📖 7. 测试

| 测试项目 | 覆盖 |
| --- | --- |
| `VelaShell.Core.Tests` | 领域模型、SFTP 与传输队列、隧道、目录同步、云同步加密 |
| `VelaShell.Terminal.Tests` | VT 解析、仿真、编码、字符宽度、侧栏折叠、OSC 8 / 133 |
| `VelaShell.Terminal.RenderTests` | 字形绘制的**像素级**回归（Skia 软件后端真实光栅化） |
| `VelaShell.Presentation.Tests` | ViewModel 工作流与命令 |
| `VelaShell.Infrastructure.Tests` | SonnetDB 持久化、凭据加密、ConPTY、SSH 接线、密钥管理、插件管理与跨进程 RPC |
| `VelaShell.Controls.Tests` | 自定义控件、主题令牌与样式守门 |
| `VelaShell.Plugin.Ai.Tests` | AI 插件：审批闸门、能力桥接、机密存取、会话历史、协作接入、面板 headless 交互 |
| `VelaShell.Tests` | 窗口级视图模型、身份验证流程、插件面板、窗口外框、headless 视图与像素回归 |
| `VelaShell.ShellIntegration.Tests` | 「文件浏览器跟随终端目录」的端到端验证：真 sshd、真登录 shell、真 PTY |
| `VelaShell.Ssh.Tests` | SSH 库单测 + 对真实 OpenSSH 的互操作（`Interop` 分类）。**一律用 Debug 跑**（Release 签名会省掉 `InternalsVisibleTo`） |
| `VelaShell.XServer.Tests` | X 服务端库单测 + 真实 X 客户端用例（需 `velashell-xclients` 镜像与 `VELASHELL_XSERVER_INTEROP=1`） |
| `VelaShell.Benchmarks` | BenchmarkDotNet 基准。**不进 CI 门禁** —— 结果受机器负载影响太大，用于同一台机器上比较改动前后 |

- **CI**（`.github/workflows/ci.yml`）：Debug 构建（强名签名只在 Release 打开，fork 与 Dependabot 的 PR 拿不到 secret）+ 全量测试 + `-warnaserror`；
  排除 `DockerIntegration` / `CrossPlatform`；本仓检出到 `VelaShell/` 子目录并把 `velashell-docs` 并排检出（快捷键比对用例要找得到对方）。
- ⚠️ **早退跳过在 MSTest 里记为「通过」**。Docker、发布、X 客户端这几类前提不满足时会安静地全绿，要看 `TestContext` 里有没有 `[SKIP]` 行。
- ⚠️ **headless UI 测试要用带返回值的重载** `Dispatch(async () => { …; return true; })`；无返回值的写法拿到一个没人 await 的 `Task<Task>`，断言失败全部丢失（§70）。
- ⚠️ **别拿固定 sleep / 固定泵赌调度**，一律改成等条件（§49、§50、§71-六）。
- 写法约定：`[TestMethod]` / `[DataTestMethod]` + `[DataRow]` / `[TestCategory]`；`Assert.AreEqual(期望, 实际)`；异常用 `Assert.ThrowsExactly(Async)`；
  `long` / `uint` 期望值带后缀（`AreEqual(object, object)` 类型严格）。
- `Terminal.Tests` 与 `VelaShell.Tests` 引了 `Avalonia.Headless`（宿主 `VelaHeadlessApp`）；`VelaShell.Tests/ModuleInit.cs` 初始化 ReactiveUI 调度器，保留。

## 📖 8. 关键约定 / 已知坑

- 构建与测试都用根目录 `VelaShell.slnx`。应用运行时构建会因 DLL 被占用失败 —— 先关应用。
- 工作区一律签出成 LF（`.gitattributes` 的 `* text=auto eol=lf`），否则 Windows CI 会在注释处报 IDE0055（§121）。
- 不要用 `Read` / `Grep` 直接读 `.pen` 设计文件（加密，只能走 pencil MCP）。
- **SonnetDB**：`Tsdb.Open(new TsdbOptions { RootDirectory })`；文档 `db.Documents.Open(name)` 的 Upsert / Get / Scan / Delete；
  时序 `db.Write(Point.Create(...))` + `SqlExecutor.Execute`。`FieldType` 是 `Int64` 不是 `Long`；**时序 tag 值不许空串**；
  **`ORDER BY time` 要求 SELECT 列表含 time**；`DELETE FROM measurement` 可能不支持（录制以 drop + 回写压缩兜底，§13-F）；
  仓储加密必须写副本，不能原地改传入的 profile（内存明文还在给活动连接用，§9）。
- **Avalonia 12**：`Run.Text` 绑定会在卸载时回写（转换器 `ConvertBack` 返回 `BindingOperations.DoNothing`，绑定标 `OneWay`）；
  ComboBox 的 `SelectedItem` 在 ItemsSource 为空时会把 null 写回（先填列表再回填选中）；XML 属性值里的换行被规范化成空格；
  **`ControlTemplate` 里直接写的属性值是 `LocalValue`，压过外部样式的 `Setter`**（§90）；元素上直接写 `Background` 同理盖掉 `:pointerover`（§20-H）；
  `Button` 默认 `HorizontalAlignment` 是 `Left` 不是 `Stretch`（§23）。
- **`SessionProfile` 是逐字段手写拷贝**，新增字段要把几处拷贝都补上（名单见 §37）；`AuthMethod` 等按序号落盘的枚举只能在末尾追加。
- **跨层识别异常一律 `is` 类型匹配，绝不按类型名或消息字符串**（§17-A、§82）。

## ✅ 9. 2026-07-08 完成情况(6 次提交,514 测试全绿)

- `2a270e5` 持久化全面切到嵌入式 SonnetDB(`SonnetDbEngine`,文档集合 + 时序 `conn_history` / `audit_log`);`AesSecretProtector` = AES-256-GCM + 本地 `secret.key`(密文前缀 `enc1:`);旧 JSON 导入后改名 `.migrated.bak`,LiteDB 移除。
- `10e9e70` 最近连接改取连接历史;`1e1fa6b` 新建连接弹窗。**坑**:仓储原地加密会把内存明文密码改成密文、重连认证失败 —— 改为写副本。
- `f5405f5` 两步登录验证 + 主机密钥 **TOFU**(指纹变化拒连);`3ef6bed` 文档;`2812048` 设置窗口九页。

## 🚧 10. 历史待办清单(已迁出)

> 2026-09-07 重整:待办整体迁往 [`feature-plan.md`](feature-plan.md),这里只留做完的与确认不做的结论。

### ✅ A. 设置项接线(2026-07-09)

终端 / 外观 / 常规 / 传输 / 安全 / 密钥六组设置从「存了但不生效」变成真生效(接线点 `ApplyLiveTerminalSettings`、`InfrastructureServiceCollectionExtensions`、`SftpService`);另有上传冲突策略(编辑器回传属有意覆盖)、断点续传(尾部 64KB 核实起点)。未闭合项见 feature-plan.md 的 P0 表。

### ❌ B. 确认不做(2026-07-10)

连字、自适应标题栏色、系统 Toast、输入脱敏、自定义键位、热切终端类型;理由见 feature-plan.md「确认不做」。其中自定义键位 2026-10-03 因 #551 推翻并实现,见 §155。

### ✅ C. 顺手清掉的技术债

SonnetDB **保留全局信号量**(文档与时序共用一个 Tsdb 实例,分锁有损坏风险;设置读热点靠缓存 JSON 文本解决);**OSC 52**(只写不读,1MB)与 DECRQSS 落地,顺带修掉 ST 结尾的 OSC/DCS 被整段丢弃的解析器 bug;CJK 回退由字体链 + 逐格回退覆盖。

### ✅ D. OSC 8 显式超链接(2026-09-08)

- `HyperlinkTable` 把 `(id, URI)` 驻留成 `ushort` 句柄,4096 条上限,仅 RIS 整表回收。
- ⚠️ 句柄放在 `TerminalRow` 的平行 `ushort[]?` 而非 `TerminalCell`(单元格恰好 16 B,加字段涨 25%);代价是每条行编辑路径都要搬,**`ReflowResize` 漏搬则一拖宽度链接全没**(`Osc8HyperlinkTests`)。
- 打印路径每格都盖句柄防幽灵链接;URI 要把 `p[2..]` 拼回(`;` 在查询串合法);`SGR 0` 不关链接。
- **安全**:scheme 白名单 `http/https/ftp/ftps/mailto`;**`file:` 刻意不放行**(远端路径在本机打开既无语义又最危险)。命中走 `LinkAtCell`,OSC 8 优先、正则兜底。

### ✅ E. OSC 133 命令块 / 语义提示符(2026-09-08)

- 做了引擎 + UI,**不做自动注入**。`TerminalRow.Mark` + `ExitCode` 挂在行上随 `Timestamp` 搬运,命令块由 `CommandBlocks` 按需推导不存状态。
- ⚠️ 标记只落重排后首行;⚠️ `Resize` / 备用屏切换必须丢掉 `_promptRow`(行对象会被复用,退出码会盖到无关行)。`B` 暂不落行。
- UI:侧栏命令标记列(非 0 标红)、点标记选输出、`Ctrl+Shift+↑/↓` 跳提示符(无标记时原样下发)、折叠按命令块对齐。
- 设置页摆可复制片段。⚠️ bash **不用 `DEBUG` trap**(与 bash-preexec 抢、`C` 会被别的 `PROMPT_COMMAND` 钩子抢走),改用 `PS0`;真 bash 验证,zsh / fish 未验。

## 🚧 11. 设计稿分析已记录的问题

终端类型 / 编码选择器、bright/256 色、CJK 回退字体、终端交互四项、亮色终端底色均已解决(后两者并入 §25 具名主题);剩余两个小项已迁往 feature-plan.md。

## 🚧 12. 与主流终端工具的功能缺口(已迁出)

> 2026-07-09 对照 Xshell / MobaXterm / Tabby / WindTerm 的 20 条;未完成项已迁往 feature-plan.md。

- ✅ **1** 本地终端(ConPTY,仅 Windows,不自动重连)· **2** ProxyJump(≤5 跳,**指纹按各跳逻辑主机校验**)· **3** 会话进命令面板 · **4** 导出缓冲区 · **5** 配色预设 · **6** 克隆会话 · **7** 同步输入(只挂用户输入,不回环)· **9** SSH config 导入(§58)· **12** known_hosts 管理 · **14** SFTP 双栏 · **16** 命令补全(程序提问时闭嘴)· **17** OSC 52。❌ **19** 多窗口、**20** Mosh(同一条里的 SSH 证书认证已于 §63 落地)。
- **8** ZMODEM 协议引擎在本仓库实现(**2026-09-14 已整体移除**,理由见 feature-plan.md「确认不做」)。⚠️ 教训仍然有效:互操作期望值必须按 lrzsz 源码手工构造 —— 用被测编码器自己生成期望值,编解码同错也全绿。
- **10** 连接代理 → 2026-08-14 落地为**应用级全局代理**,唯一出口 `IProxyResolver`;**配置不完整时拒连,绝不静默直连**;ICMP 与诊断裸 TCP 有意不走代理。
- **13** 标签自定义颜色(`b9ae31f`,`TerminalOverrides.TabColor`)。⚠️ **复核订正**:09-05 复核写「用户不可选」,次日即被推翻 —— 复核结论也会过期,写现状要给证据(文件行号)。
- 清单外:Telnet / 串口 / Redis / S3 以插件落地(宿主新增 `IProtocolTerminal`);FTP / FTPS 内置(§18-N)。

## ✅ 13. 2026-07-11 ~ 07-12 批次(设置审计整改 + 四个新特性)

**A. 设置审计整改**:BellMode 合并、`ObservableOptions` 统一。**B. 主机指纹三选项**:永久 / 仅本次 / 取消 = fail-closed,补上 SFTP 通道信任任意指纹的 MITM 缺口。
**C. GitHub Gist 云同步**:设置 + 连接配置(upsert 不删本地)+ 代码片段进单个 secret Gist,版本复用 revision;可选 PBKDF2 + AES-GCM 端到端加密,未启用时凭据绝不上传;PAT 经 `ISecretProtector` 加密、永不进载荷。
**D. 会话录制与回放**(只录输出流,导出 asciicast v2)。**E. 支持与捐赠页**。
**F. 后续增量**:录制保留天数随会话日志清理,**DELETE 不可用时 drop + 回写压缩兜底**(防孤儿数据块磁盘只增不减);MIT → AGPL-3.0 + 商业双许可。遗留项已登记在 feature-plan.md。

## ✅ 14. 多语言(2026-07-12 全量补齐,C-09 一并完成)

五语言(英文默认 + `zh-Hans/zh-Hant/ja/ko` 脚本中性文化),键集平价有测试守护。实时切换两处根因:`LocalizationService` 自持目标文化(线程文化随 ExecutionContext 回卷);Avalonia 12 不响应 `Item[]` 索引器通知,改绑普通属性。

## ✅ 15. 版本与发布(2026-07-12)

版本号单一来源 `Directory.Build.props`,发版由 Release 标签 `-p:Version` 覆盖;`publish-all.ps1` / `release.yml` 产 6 个含运行时的包 + `latest.json`;08-12 起**摊开发布**(`PluginHost` 需真实可执行体)。Velopack 已移除(卸载会清空数据根),分发 = 便携 zip + 应用内自更新。

## ✅ 16. 2026-07-13 ~ 07-14 批次(VelaDock、原生窗口壳、终端侧栏、工程化)

**A.** VelaDock 替换 `Dock.Avalonia`,零第三方停靠依赖(§5)。**B.** 无边框标题栏 + WndProc `HTMAXBUTTON` 实现 Snap Layouts;坑:Avalonia 12 的 `VisualRoot` 不是 `Window`,走 `FindLogicalAncestorOfType<Window>`。**C.** 行号 / 时间侧栏。**D.** Job Object 杀本地进程树。**E.** 密码提示行不弹补全。**F.** 集中式包管理、Avalonia 12.1。

## ✅ 17. 2026-07 批次(SSH 传输层迁移、ZMODEM、SFTP 双栏)

**A. SSH.NET → Tmds.Ssh**:改动全在 `Infrastructure/Ssh/`,Core 一行没改。07-22 修:异常按旧类型名字符串匹配,认证重试静默失效 —— 测试没抓到是因为测试里定义了迎合实现的假异常。约定:**跨层识别异常一律 `is` 类型匹配,绝不用 `GetType().Name`**。
**B.** ZMODEM 见 §12-8(已于 2026-09-14 移除)。**C.** SFTP 双栏见 §12-14。**D.** 远程编辑器语法高亮。**E.** net10 → net11(依赖预览版 SDK)。
**F. 拖入文件夹异常风暴**:`AutoConnect` 默认 true 导致每次 SFTP 操作静默重连,现显式 false;续传探测改走预列举名单。**G.** 两份调研文档。

## ✅ 18. 2026-07-24 ~ 08-14 批次盘点(2026-08-14 补记)

**A. 插件系统 v1 + AI 助手插件**:进程内 / 隔离进程(`PluginHost`,命名管道 RPC)双宿主,插件源码零改动;进度以 velashell-docs `STATUS.md` 为准。
**B. 资源监控窗口**:⚠️ 只对 POSIX 远端发探针,`RemoteShellProbe` 判否即「无数据」—— Windows 远端的 cmd.exe 会回显整行命令,被解析成全 0 假指标。
**C.** 连接诊断(直连,不走代理)。**D.** 路由追踪 + 离线 IP 归属地。
**E. 远端任务管理器**:同样先判 POSIX,否则报「不可用」—— 教训同 B,否则是一张 0 进程的假空表。
**F. 文件浏览器跟随终端目录**(09-15 重构):SSH 只能往已运行的 PTY 里打字,借 VS Code 的 OSC 633 协议、投递另解决:分 shell 注入且还原 `$?`、带 nonce 哨兵扣住回显(不按回显匹配,超宽折行各 shell 不同)、对端静默后注入且超时放行。真 sshd + 五种 shell 验证。
**G.** 传输面板 · **H.** Xshell / WinSCP 导入 · **I.** UI 字体令牌 · **J.** 终端交互 · **K.** 外观 · **L.** MSIX 打包 · **M.** 小项。
**N. FTP / FTPS**:连接池上限遇 `421` / `450` 自适应下调(`AdjustableConcurrencyGate`)。**O.** 全局代理见 §12-10。

## ✅ 19. 2026-08-30 隧道功能完善(计量转发 / 流量统计 / 断线自动恢复 / 端口冲突预检)

**A.** Tmds.Ssh 不暴露字节计数,转发数据面改由宿主接管(`MeteredPortForwardHandle`,含 SOCKS5 服务端);保留**半关闭语义**;用例拿客户端侧编码当地面真值。**B.** 停止时先取最后读数再释放句柄。**C.** 自动重连退避,**用户按停的不拉起**。**D.** 端口预检的探测委托可注入(否则测试看机器)。**E.** 9 个新键。**F.** 面板字号上调一档。

## ✅ 20. 2026-08-30 消息中心(侧边栏铃铛)

**A.** 只收可回看的消息,**不收运行时告警**。**B.** 资讯源契约字段只加不改,外链只放 https,坏条目单独跳过(08-31 改默认订阅,§22)。**C.** 跳转走 `ICommandRegistry`,分区用 `SettingsSectionKey` 枚举而非下标。**D.** 修好死开关 `CheckUpdatesOnStartup`。**E.** 同 id 重投保住已读状态。**F.** 浮层锚在铃铛旁。**G.** 24 个新键。
**H.** ⚠️ 元素上直接写 `Background` 是 local value,压过样式,`:pointerover` 盖不过去。遗留项已登记在 feature-plan.md。

## ✅ 21. 2026-08-31 补全弹层关不掉(#315)

**A. Ctrl+C**:空行清空空行不算「内容变化」,事件被吞;改为 `0x03` / `0x15` 一律记为变化。**B. 点击终端**:点已聚焦终端没有 `LostFocus`,补 `PointerPressed`。已登记 `ShortcutCatalog`。

## ✅ 22. 2026-08-31 资讯源默认订阅官方源

`FeedUrl` 默认改为官方源 —— 安全资讯要「没去找也能到」。默认行为变更同步了 `PRIVACY.md` 中英、velashell-docs 中英与 §20-B;存量空串配置原样保留。

## ✅ 23. 2026-08-31 消息中心:可拖动、加大字号、动作靠右

**A.** 拖拽抽成 `PanelDragHandler`。**B.** 外层 Panel 须铺满(拖拽参考系是父容器)。**C.** 字号加大一档。**D.** 去处行两端对齐。**D-1.** ⚠️ `Button` 默认 `HorizontalAlignment` 是 `Left` 而非 `Stretch`。**E.** 右留白 16 让滚动条。**F.** ⚠️ 圆角不裁内容,内层加 `CornerRadius=5` + `ClipToBounds`(不能裁卡片自身,会裁掉阴影)。**G.** 唯一投影令牌调软,同步 `DESIGN.md` §4.5。
测试教训:headless 帧是 **RGBA**,按 BGRA 读断言永远为真。

## ✅ 24. 2026-08-31 会话树状态卡在「连接中」(#321)

根因:一个节点多个标签、状态按最后一次写入,失败 / 取消路径还绕过 `OnDocumentClosed`。改为 `RefreshSessionStatus` 按配置取优先级最高者,订阅挂在 `OnTabsCollectionChanged` 让所有移除路径共用收口。SFTP 等文档型连接当时仍是后写的赢(与既有行为等价,不是新洞),后来在 §39 收掉。

## ✅ 25. 2026-08-31 具名主题:九套配色 + 终端配色配对

三值主题改为具名主题目录(六暗三亮),Id `dark` / `light` 不变。
**一、** 只写 25 个种子色(`UiThemePalette`),其余由 `ThemeTokenApplier` 派生 —— 手抄会错且看不出(#246)。
**二、** 终端配色三层叠加;硬约束:**方案背景必须等于主题 `VelaBgTerminal`**。
**三、** `UiThemeCatalogTests` 逐主题查对比度,真拦下了几处原版配色。**四、** `IsEmpty` 恒 false 的静默 bug。
**五、** 插件侧仍只见 `dark` / `light` / `system`,具名主题不外泄;启动先验 Id(降级不崩)。

## ✅ 26. 2026-08-31 主题命名收敛 + 「跟随主题」不再是隐式状态(用户反馈)

**一、** 只有 VelaDark / VelaLight 保留品牌名,其余用调色板原名。
**二、** 「跟随主题」曾隐式编码为「颜色 == 出厂 Dracula」,明选 Dracula 无从区分。改为显式 `TerminalColorsFollowTheme`(`bool?`,**不给初值**,null 按老口径推断);⚠️ 手改单色时直接置标志,不能反问 `FollowsTheme`。

## ✅ 27. 2026-08-31 再补三套主题:One Dark / One Light / Sakura(用户反馈)

主题 9 → 12、终端方案 14 → 16;One Light 绿黄与 Sakura 错误色按对比度尺子调过。新增主题 = 填种子色 → 跑 `UiThemeCatalogTests`。出厂强调色 `#E91E63` 会盖掉各主题强调色,遗留项已登记在 feature-plan.md。

## ✅ 28. 2026-08-31 切主题发卡(用户反馈)

**一、** 逐个写 64 个令牌 → 游离字典整格换入 `ThemeDictionaries`,40~57 ms → 1.65 ms;先贴令牌再换变体。**二、** 终端字体 setter 加相等判断(否则丢字形缓存)。**三、** 跨明暗不再刷两遍。**四、** 回归用例数**通知次数**而非耗时(耗时断言在 CI 必抖)。

## ✅ 29. 2026-09-01 命令行装的插件被判「收据缺失」(用户反馈)

- **一、错在宿主**:收据存在宿主信任库,CLI 够不着;宿主却把「没收据」当成拒装载,旁装(CLI、直接放目录)的插件全被标 Invalid。
- **二、收养随用随做**(`VerifyOrAdoptInstallReceiptAsync`):管理页装的收据内容变了一律拒;旁装的记 TOFU 基线,变了重记 + 日志;基线写不进不放行。
- **三、** 启动时 `PruneReceiptsWithoutDirectory` 清掉孤儿收据,免得同 id 重装被判「被改过」。
- **四、代价**:安全没让步;但同一插件别混用管理页与命令行。文档已同步 velashell-docs。

## ✅ 30. 2026-09-02 AI 插件:自定义供应商也能自动拉模型清单(用户反馈)

- **一、** 原来只认 models.dev,没收录的(Ollama、`custom-*`)不显示按钮;按钮还藏在只有订阅登录才可见的面板里。
- **二、端点优先**(`ModelPull.RunAsync`):先问端点 `/models`(`EndpointModelCatalog`)拿实际型号,再按 id 从 models.dev 补窗口/单价;端点失败退回 models.dev。
- **三、刻意降级**:非对话模型按名字滤,宁漏勿误;中转站单价留空 —— 照抄原厂价会让花费估算静默偏低。

## ✅ 31. 2026-09-02 AI 插件:左栏模型列表可折叠(用户反馈)

- **一、** `AiProvider.ModelsExpanded` 是 `bool?`，用户折/展过就尊重；拉取模型后复位。2026-10-21 调整为未表态时一律默认折叠，不再按模型数量判断。
- **二、** 打开设置时，活跃模型所属供应商若折叠则选中供应商行，不自动展开、不切换聊天模型；显式新增或导航到模型时仍展示对应模型，手动折起时选中上移到供应商行。
- **三、** 整行都是折叠热区;→ 展开、← 折起;左右键须挂隧道阶段(`ListBox` 冒泡前就吃掉了)。

## ✅ 32. 2026-09-02 资源管理器:会话树改成摊平的平列表(用户反馈)

- **一、** 子行前的空白是 `TreeView` 的层级缩进区,压 `PART_*` 模板部件压不干净,换版本还会复发。
- **二、** 改成绑 `SessionTreeViewModel.Rows`(由 `Nodes` 派生)的 `ListBox`;`Rows` 就地对齐而非清空重建,否则双向绑的 `SelectedNode` 被清空。
- **三、** 滚到选中项改用 `ListBox.ScrollIntoView`(虚拟化后视口外没有控件可找)。
- 教训:`ShortcutCatalogTests.Doc_ListsEveryCatalogEntry` 因文档搬家红了几个月没人管(09-03 已修)。常年红着的用例等于没有。

## ✅ 33. 2026-09-02 协作接入:IM 桥接(飞书/钉钉/Telegram/企微)+ 对外 MCP 服务端

往外:群里 @ 机器人,agent 在已连上的会话上干活;往内:外部 agent 把 VelaShell 当 MCP 服务端。代码在 AI 插件 `Bridge/`、`Interop/`,插件因此改为 `onStartup` 激活。
- **一、** 四渠道四种传输(`IMessageChannel` + `ChannelHub`):飞书长连接(pbbp2 帧手写编解码)、钉钉 Stream、Telegram 长轮询、企微回调(只绑 127.0.0.1)。
- **二、安全默认**:白名单空 = 谁都不理;默认只读;`/mode` 只能往低调;审批走文本 `y`/`n`/`a`;绑定存 `user@host:port` 而非会变的 SessionId。
- **三、** 无头回合另写 `BridgeAgentRunner`,不拆 2500 行的 `ChatPanelView.SendAsync`。
- **四、对外 MCP**(`Interop/McpEndpoint`):Streamable HTTP,只绑回环,每请求带令牌。这条路没有审批界面,「询问」模式等于一律拒绝写操作 —— 这是刻意的,要放开得由用户显式选只读放行或绕过审批。
- **五、** 当时开不了新会话,已由 SDK 2.0.2 解决(§35、§36)。
- **六、坑**:`Wrap` 的多行 TextBox + 竖滚动条 Auto = 布局死循环,窗口卡死且无异常。文档已同步(`协作接入.md` 中英)。

## ✅ 34. 2026-09-02 协作接入配置流程返工(用户反馈:"要填一堆文本框")

改后流程:填两个框 → 测试 → 扫码拉机器人进群 → 群里发 `/pair`,全程不抄 id。
- **一、** 飞书/钉钉/企微的 `app_secret` 只能从后台拿,扫码变不出来。
- **二、配对码**(`PairingService`):六位、一次性、十分钟、错五次作废;只能加白名单。
- **三、** 被挡的聊天记为 `PendingChat`,设置页一键放行。**四、** [测试] 按钮(`ChannelProbe`)只读探测,飞书多查接入点。
- **五、** 拉群链接渲染成二维码;1.4.8 起在插件内实现编码器、替换 QRCoder(`Ui/QrCode.cs`)—— 它传递依赖 `System.Drawing.Common`,还炸了 macOS `codesign`。教训:引包先看传递依赖。**六、** 待放行清单加载完即刷。
- **七、** `PostAsJsonAsync` 按 Web 默认把 `AppID` 转成 `appID`,飞书回 9499;在类型上钉 `[JsonPropertyName]`。
- **八~十一、** 这页没照 `DESIGN.md` 走(引用不存在的 `VelaBorderSubtle`、按钮不居中、代码 `new` 的按钮进树前取不到主题),改用 `Button.host` / `Button.primary` / `CheckBox.host` class;headless 证明不了主题挂上,最后用 Skia 渲染 PNG 确认。
- **十二、十三、** 渲染抓出 `Loc.Table` 重复键静默覆盖;加 `NoKeyIsDefinedTwice`、`ThemeTokenUsageTests` 等守门。
- **十四、** 整轮在落后 18 个提交的基线上做,本节与 §33 因此顺延编号。教训:先 `git fetch`。
- **十五、** 飞书 401 而面板正常:桥接用的是启动时的设置快照,改为每轮现读。

## ✅ 35. 2026-09-03 插件能按已保存配置自己连一台机器(SDK 2.0.2 宿主侧落地)

宿主实现 SDK 2.0.2 的 `ISessionsApi.ListSavedAsync` / `OpenAsync` / `CloseAsync`。
- **一、闸门在宿主侧**:只开已保存的 SSH 配置;凭据不经插件;拒绝与失败分型(`PluginPermissionDeniedException` / `PluginSessionOpenException`);`Reason` 原样进确认框;只关得掉自己开的。
- **二、** 走 `TryConnectProfileAsync`,会话有标签、凭据框与指纹确认都不漏;headless 宿主一律拒 —— 没人可问不等于可以放行。
- **三、** `PluginPermissionGate` 里开会话与终端回写分两本账。**四、** `RpcSessions.OpenAsync` 超时给 5 分钟,等人看完确认框。
- **五、** 拒绝须以原异常类型跨进程到达插件。

## ✅ 36. 2026-09-03 AgentToolbox 接上开会话

新增 `list_saved_sessions` / `open_session` / `close_session`,§33-五 真正闭合。
- **一、** `open_session` 过两道人:审批闸问这轮对话,宿主确认框问这个插件(「始终允许」让无人值守走得通)。
- **二、** `close_session` 免审批:只能关自己开的,再要点头 agent 就不收拾了。
- **三、** 默认目标:显式 id > 选中项 > 本轮自己开的,免得用户看着 A、调用打到 B。
- **四、** 三处「你先去连一台」提示改掉。**五、** `AgentToolboxTests` 新增 15 条。

## ✅ 37. 2026-09-04 每条连接各配一条「认证后执行命令」(用户反馈)

每条 SSH 配置可各配一条命令 + 延迟(0~60 秒),与全局那条并存。
- **一、字段落在 `SessionProfile`,手写拷贝一处都不能漏**:`SessionProfile` 逐字段手写拷贝,新增字段须同步 `ConnectionProfileViewModel.BuildProfile`、`SonnetDbSessionRepository.Protect`、`ConnectionWorkflowService`(不记密码时的副本)、`SessionTreeViewModel`(复制会话);漏抄 = 重开后字段悄悄没了,`SonnetDbPersistenceTests` 钉住。
- **二、** 只对 SSH 出现,切走协议时清成 null(否则切回会诈尸执行)。
- **三、** 延迟防 motd / 横幅吞命令;用 `DispatcherTimer.RunOnce`,回调复核会话身份。
- **四、** 先全局后本条,重连同样执行。**五、** 文档已同步 §13.1 中英。

## ✅ 38. 2026-09-04 FTP / FTPS 可配「默认打开路径」(用户反馈)

- **一、** 落在 `FtpSettings`(只改 `Clone()`),FTP 专属,不进 `SessionProfile`。
- **二、** 只是候选表首位,打不开就退回登录目录再退回根 —— 配错不该把人堵在报错页。
- **三、** `FtpSettings.NormalizeRemotePath` 在 setter 上归一。
- **四、** 文档已同步 §13.1 中英。

## ✅ 39. 2026-09-04 文档型连接的树状态:关掉一个,别把另一个也熄了(用户反馈)

- **一、** 与 #321 同形:节点状态应合并名下全部会话,#321 只修了终端,文档型连接关闭时仍无条件写「未连接」。
- **二、** 新账本 `_documentSessions`,`RefreshSessionStatus` 合并「终端标签 ∪ 在册文档会话」;删掉三本旧册子,迟到事件不复活圆点。
- **三、** 第一版回归用例是假绿(断言跑在异步更新之前);改为等关闭任务 + 调度器栅栏,反向验证过会红。
- **四、** 新增一条环回 FTP 端到端用例。

## ✅ 40. 2026-09-04 数字输入框删空后别再甩一句转换异常(用户反馈)

- **一、** `NumericUpDown` 删空后 null 绑不到数值属性,界面原样显示 `InvalidCastException` 并撑坏布局;全项目 20 处皆然。
- **二、** `NumericInputGuard`:错误换成「请输入 A 到 B 之间的数字」;失焦仍空则 `SetCurrentValue` 恢复(直接赋值会掐断绑定);错误改用图标 + 悬停提示。挂在全局样式上。
- **三、** 隧道面板两个 `TextBox` 端口改 `NumericUpDown`。
- **四、** 设置窗口整卷扫描,带数量下限防假绿。**五、** `NumericInputGuardUiTests` 10 条。

## ✅ 41. 2026-09-05 对外 MCP 的「允许操作的服务器」改成勾选(用户反馈)

- **一、** 名字会改会重名,复用 §34 的勾选,存 `SavedSessionId`;`AllowedTargets` → `Scope`,范围外会话当作不存在。
- **二、** 默认值刻意与 IM 相反:MCP 默认不限范围(回环 + 令牌 + 只读已是边界)。
- **三、** `NormalizeScope` 迁移:旧清单空 → 不限;对得上的勾出;一行都对不上 → 失败关闭。
- **四、** 9 条新用例;文档已同步(协作接入 2.4 节中英)。

## ✅ 42. 2026-09-06 开一下 SFTP 面板,别把设置里的开关也给拨了(#377)

- **一、** 记标签状态时顺手写回了全局 `AutoOpenFileBrowser`,把临时状态与全局默认混成一层。
- **二、** 删掉写回;设置 = 新标签初值,标签 = 此后开关,面板 = 当下这一屏。
- **三、** 用例改钉「不写回」(`SaveCount == 0`)。

## ✅ 43. 2026-09-06 滚动条:悬停别等半秒,新建连接别一进来就是粗条(#378)

- **一、** `ShowDelay` 默认 0.5s;`AllowAutoHide="False"` 在 Avalonia 里是常驻展开而非「可见」。
- **二、** 全局 `ShowDelay` 150ms(置 0 会被擦碰误触);去掉连接对话框的 `AllowAutoHide="False"`。
- **三、** 两条用例;`DESIGN.md` 5.8 已补。

## ✅ 44. 2026-09-06 exit 之后不该被自动连回来;SFTP 通道跟着 SSH 一起收(#383)

- **一、** 重连判定只认工具栏「断开」;远端 `exit` 的干净 EOF 与掉线被 `ShellStreamWrapper.ReadAsync` 一律归成「返回 0」。
- **二、** 新增 `ShellCloseReason` 传到 `TerminalTabViewModel.RemoteShellExited`;`ShouldReconnect` 加 `remoteShellExited` 且不给默认值。
- **三、** `SftpService` 订阅 `SessionDisconnected` 在服务层收口,不靠各断开入口记得调用。
- **四、** 用例反向验证过;文档已同步「断开连接状态」中英。

## ✅ 45. 2026-09-07 新开标签页时,上一个会话的 SFTP 面板要立刻收起(#385)

- **一、** `RebindFileBrowser` 遇到 `SessionId == Guid.Empty`(握手中的新标签)直接 return,继续显示旧面板。
- **二、** 三种无 SFTP 会话的标签一律换隐藏占位;旧面板留在缓存,切回照旧恢复。
- **三、** 回归用例:加回 return 即红。

## ✅ 46. 2026-09-07 连接慢的时候,屏幕上必须有东西在动(#385 反馈)

- **一、** 标签先建再握手:`ConnectingDocument` 占位,连上后 `ReplaceDocument` 原位替换;文档型四条路径共用 `DocumentConnectUi`,关标签即取消,失败落在标签内而非模态框。
- **二、** 终端补 `ShowConnectingOverlay`。
- **三、** 各连接路径登记后台活动圆环;等用户输入时不转圈。
- **四、** 含渲染用例(只在慢时才看得到的界面最容易带坏资源发版);`DESIGN.md` 补 §5.2b「等待态」。

## ✅ 47. 2026-09-07 后台任务浮层是块黑砖,跟哪套主题都不搭(用户反馈)

- **一、** 裸内容浮层吃 Fluent 写死的近黑底色,不在 `Vela*` 令牌里。
- **二、** `FlyoutPresenter` 基础样式改走令牌,`.bare` 必须排在它之后。
- **三、** 每条活动带进度环(`BackgroundActivityItem.Fraction`)。
- **四、** `FlyoutPresenterStyleTests` 两条。**五、** `VerticalOffset="-8"` 让浮层不压状态栏,纯观感数值不写用例。

## ✅ 48. 2026-09-07 关掉「连接中」的标签,连接就该停下(用户反馈)

- **一、** 终端握手只带调用方令牌:关标签后圆环照转、超时后还弹错,握手在关后成功会留下孤儿连接。
- **二、** `BeginTabConnect` / `CancelTabConnect`(只取消不释放,否则撞 `ObjectDisposedException`)/ `EndTabConnect`,在 `OnDocumentClosed` 触发;失败按令牌判而非按异常类型。
- **三、** `ConnectingTabCancellationTests` 两条,反向验证会红。

## ✅ 49. 2026-09-07 CI ubuntu 偶发失败:隔离插件"连不上"被报成"激活超时"

- **一、** 报错是错的:连管道阶段超时逸出裸 `OperationCanceledException`,被一律写成「激活超时 30s」。
- **二、** 根因:`PluginHost` 先建 Avalonia 再连管道,Linux CI 上并行抢核时撞破 10s。
- **三、** 管道先连;新增 `IsolatedStartupTimeout` 与激活时限拆开;各段超时自报家门;软件渲染补齐 X11 / macOS。
- **四、** 回归用例把预算压到 1ms,断言报 `did not connect`。

## ✅ 50. 2026-09-08 CI macOS 偶发失败:背压用例拿固定 sleep 赌线程池已经起来了

[run 34159705858](https://github.com/joesdu/VelaShell/actions/runs/34159705858) 上 `DisposeReleasesAReadLoopWaitingOnBackpressure` 报「没攒出积压」。
- **一、** 不是产品 bug,是用例前提没成立:`Start()` 后固定睡 150ms,读线程还没跑起来。
- **二、根因**:读循环 `Task.Run` 排在线程池,饱和后约 500ms 才注入一条线程;macOS 3 核却并行跑 8 个测试程序集。本机 `DOTNET_PROCESSOR_COUNT=2` 必挂、加 min worker thread 即绿。
- **三、** 断言只要求积压 > 0,读线程不在闸上也会白绿 —— 又脆又弱。
- **四、改法**:`WaitUntil` 等到越过高水位(`HighWaterBytesForTest`),超时 20 秒;`Flood()` 见到首字节才开始计空闲。
- **五、验收**:负控制要同时堵住 `ReleaseDrainGate()` 与令牌两条路才红(双保险)。
- **六、教训**:同 §49,`Thread.Sleep(常数)` 后紧跟断言时问「这个数凭什么够」,答不上就改成等条件。


## ✅ 51. 2026-09-08 双击打开的远端文件也要自动回传;编辑会话不再赌编辑器进程(#396)

用户报「第一次保存有效,后面再存就没效果」。
- **一、** 三个「打开」入口外观一样:双击那条从来没有 watcher,右键两条才回传;用户分不出来。
- **二、** 另一条路径:3 秒启发式把单实例编辑器慢退的引导进程当成「已关」,拆掉会话,此后保存无声丢失。
- **三、改法**:升为 `RemoteEditSessionManager`,入口只决定谁来打开,回传归会话;按 `(SessionId, RemotePath)` 去重;删 3 秒启发式,会话只随手动结束 / 远程会话关闭(赶在 SFTP 断开前)/ 应用退出结束。另加浮窗「正在编辑」分组(§55 撤掉)、诊断日志 `remote-edit.log` 与两个设置项(默认打开方式不变)。
- **四、两颗雷**:`CheckAccess()` 快路在测试宿主里对 watcher 线程点头 → 一律 Post;界面异常冒回 `OpenAsync` 跳过了打开 → 逐订阅者 try/catch。
- **五、回归用例**:补上连存多次、原子保存、复用不覆盖未回传草稿。
- **六、判据**:同一动词出现在多处时先问能力集合是否一致;「外观一致、行为分叉」时用户报的现象不指向真凶。


## ✅ 52. 2026-09-08 编辑器都关掉了,「正在编辑」那一行还挂着(#396 反馈)

§51 让进程退出不再拆会话,于是没了「编辑器关了」这个出口。
- **一、** 收早了 = 保存丢失,收晚了 = 多一行;旧实现错在一个阈值同时管两边。
- **二、改法**:活不到 8 秒视为引导进程,收养最早启动的同名实例,找不到也继续守;慢退出才收会话。
- **三、** 双击走的 `Launcher` 只回 `bool`;新增视图侧 `OpenLocalFileTrackedAsync` 取句柄。
- **四、回归用例**:快退仍回传、慢退收会话、收会话前先落防抖中的那次保存。
- **五、判据**:代价不对称时,阈值只管便宜的方向,昂贵的一边要硬证据。(进程跟踪已在 §54 删除。)


## ✅ 53. 2026-09-08 VS Code 关掉了那一行还挂着:别拿单个进程代表"应用还开着"(#396 反馈二)

- **一、** VS Code 是一堆同名进程,收养到谁都不代表应用还开着。
- **二、改法**:每 5 秒 `GetProcessesByName` 看名下还有没有活的,「见过又消失」才收会话。⚠️ 每轮句柄要逐个 Dispose。
- **三、** DDE / COM 复用时拿不到句柄,状态直说「用完请手动结束」。
- **四、** 用例不拿真实进程当判据(CI 上同名进程会随机干扰),改为可注入的存活探针。
- **五、判据**:桌面应用与进程早就不是一一对应,别拿某个进程当应用的代理。


## ✅ 54. 2026-09-08 「正在编辑」改成只在出问题时出现,整段进程跟踪删掉(#396 反馈三)

用户问「为什么不直接 watch 文件」—— 对,§52/§53 是弯路。
- **一、** 回传只靠 `FileSystemWatcher` + 600ms 防抖;进程跟踪只为回答「那一行何时消失」,单实例编辑器下答不出来。
- **二、改法**:改成只在失败或有未传改动时露面,标题改「待回传」,进程跟踪整段删。
- **三、回归用例**:健康会话不出现、失败必须露面。
- **四、判据**:加常驻状态前先问它靠什么信号消失;拿不到就做成「例外才出现」。


## ✅ 55. 2026-09-08 传输浮窗里那一组整块撤掉,远程编辑不再出现在界面上(#396 反馈四)

用户嫌「闪一闪」:§54 的条件 `HasPendingChange` 在每次保存的防抖窗口里都为真。
- **一、撤掉**:浮窗分组、相关 VM 与服务侧只为显示存在的读模型;失败仍靠传输行标红与草稿提示。
- **二、** 「自动上传」关掉后收尾还会偷偷补传 —— 现在关掉即不启用 watcher。
- **三、回归用例**:关掉后不上传,收尾也不补传。
- **四、判据**:显示条件要用稳态不用过程量;用户要的是自动上传,不是看见它在上传。


## ✅ 56. 2026-09-08 按下 Ctrl 那一刻手型就该出来,不该等鼠标抖一下(#397)

- **一、根因**:判定只挂在 `OnPointerMoved`;松开侧有 KeyUp 钩子,按下侧没有。
- **二、修法**:`UpdateLinkHover` 与指针事件解耦,记最后指针位置,`OnKeyDown` 认 Ctrl 就地重判;`_ctrlHeld` 在 KeyUp 与 LostFocus 都要清。
- **三、回归用例**:`LinkHoverTests` 新增三条。
- **四、边界(没修)**:焦点不在终端时按 Ctrl 仍无手型,留作已知限制。判据:成对的状态转换两侧都做。


## ✅ 57. 2026-09-08 插件的发布者,得一直是同一个人(用户反馈)

验签只证明包没被改过,不证明签的还是上次那个人 —— 复用 id 就能换掉已装插件。
- **一、** 收据里早有 `PublisherPublicKey` 却零消费者;现由 `CheckPublisherContinuityAsync` 比对。
- **二、** 闸卡在卸载旧版之前,否则拦下时旧插件已没了。
- **三、** 换钥不判死也不静默放行:抛 `PluginPublisherChangedException`,用户看过两个指纹再确认;去签名同样拦。不并进 `allowUntrustedPackage`。
- **四、** 旁装的插件钉不住发布者,这一闸闭嘴(同 §29),保证命令行装的仍能更新。
- **五、** 顺序改为先装、装成再入信任库,不在告知换人前就签下信任。
- **六、** 管理页显示完整发布者指纹,只在显示层截断。
- **七、** 仍无信任根:这是 TOFU + 连续性,不是市场认证。
- **八、验收**:新增 4 条用例;文档同步已登记在 feature-plan.md。


## ✅ 58. 2026-09-09 SSH config 导入:难的是把 OpenSSH 的取值规则照抄对

`ssh_config` 不是 INI,照 INI 写在带 `Host *` 的真实配置上全错。
- **一、** 首次取得的值获胜:`SshConfigParser` 保留块结构,按块序 `TryAdd`。
- **二、** `Include` 就地展开(位置决定优先级),相对 `~/.ssh`,防环 + 深度上限。
- **三、** 只导字面量别名;`Match` 整块跳过;无 `HostName` 时别名即主机名。
- **四、** `IdentityFile` → 私钥认证;`ProxyJump` 第二趟解析、取最后一跳、成环即断。
- **五、** 配置不存密码,预览多一态「使用密钥文件」。
- **六、验收**:17 条用例;文档 [velashell-docs#26](https://github.com/VelaShellLabs/velashell-docs/pull/26) 新开「会话导入」一篇。


## ✅ 59. 2026-09-09 选区对比度再抬一档:暗底 20 / 亮底 16,两个数不该相同(用户反馈)

顺带补记 #405(选区不透明绘制、与底差 L\* ≥ 14)。
- **一、** 14 让各配色都卡在及格线上,实测仍弱;对齐 VS Code:暗 20、亮 16。
- **二、** 亮底要压深、逼近深色文字,吃不起同一档;拆成两个阈值,由 `MinLightnessDeltaFor(background)` 按背景选档,与推的方向同一判据。
- **三、** 16 套配色仅 Tokyo Night 原生达标,其余都被推上去。
- **四、代价**:被守卫改色的 ANSI 前景 30 → 41,属必然;「保色相」守卫没做,已登记在 feature-plan.md。
- **五、验收**:像素测试地板须低于较低一档(18 → 14),否则会因 8bit 舍入变红。


## ✅ 60. 2026-09-09 下拉列表的字在亮色主题下发糊:两条假设被像素实测推翻(用户反馈)

- **一、被推翻**:弹层半透明(实测不透明)、弹层落在半像素(读错了边框)。
- **二、真因**:11px Inter Regular 笔画没有一个像素被完全覆盖,亮底上读作灰。
- **三、** 下拉是唯一没接令牌的弹层,在 `ThemeTokenApplier` 里把十个 Fluent 键别名到令牌 —— 资源键是公开契约,模板部件名会随 Avalonia 版本变。
- **四、** 落地的是字号 11 → 12;压字重的尝试已由用户撤下。
- **五、验收**:新增逐主题用例;两个 `Height=22` 的下拉未做像素复核。


## ✅ 61. 2026-09-09 回滚行数设置项早就存下来了,只是有两处不生效(用户反馈)

feature-plan.md 说「只差设置项」是错的,整条线早通了。
- **一、** 调小不当场裁,闲着的标签页内存不还;setter 里当场裁。
- **二、** 在 vim 里保存,值写到了备用屏;上提为 `TerminalEmulator.ScrollbackLines`,恒读写主屏。
- **三、** 真裁到时连带收滚动位置、折叠与选区(同 §12-13 的纪律)。
- **四、验收**:6 条用例;当时本机 SDK 与 CI 不同,结论打折。


## ✅ 62. 2026-09-09 #414 的 CI 两处红:用不到的 apt 源,与预热超时后撒了手

[run 34384262772](https://github.com/joesdu/VelaShell/actions/runs/34384262772) 两处红都与 §61 无关。
- **一、ubuntu**:Chrome 源 Hash 不符让 `apt-get update` 整体失败;改为包齐就不碰 apt,缺包时 update 失败只警告。
- **二、windows**:`StartupWarmup.Discard` 等开库超时就撒手,WAL 一直被占(排队根因同 §50);改为挂 `ContinueWith` 续延(不用 `ExecuteSynchronously`),开库完成即关;用例改为等条件。
- **三、验收**:用上了钉定的 SDK;ubuntu 那步留给 CI 验。


## ✅ 63. 2026-09-10 SSH 证书认证:上游早就备好了,缺的只是这一路接线

Tmds.Ssh 0.23 起就有 `CertificateCredential`,包版本没动;缺的只是 `AddCredential` 那一路。
- **一、** 签名始终由私钥出、证书只是 CA 背书,所以整路复用私钥那一路;选完证书按 `-cert.pub` 自动补私钥,免得错配后只得到笼统的 `publickey` 被拒。
- **二、** 证书认证下私钥与证书路径留空都是硬错。
- **三、** 修掉切到 FTP 后证书页切不回来。
- **四、** `AuthMethod` 按序号落盘,`Certificate` 只能加在末尾,有用例钉住。
- **五、** 真机验证必须有阴性对照:`tests/cert-lab/` 靶机 `authorizedkeysfile none`、关密码,否则「连上了」证明不了证书生效。
- **六、范围之外**:导入与主机证书没做,已登记在 feature-plan.md。
- **七、验收**:新增 16 条用例,靶机阳性 + 阴性两条全过。


## ✅ 64. 2026-09-10 VelaDock:关掉左半屏,右半屏该铺满(用户反馈)

- **一、** `CollapseIfEmpty` 里多余的 `IsPrimary ||` 把「永不折叠」钉到了主组实例上,该保护的是根;`TryHandOverPrimary` 把兜底身份交给接管这块地方的邻居后退场。
- **二、** `TargetGroupForNewDocument`:空窗格 > 活动窗格 > 主组。
- **三、** 空窗格加「关闭窗格」(`ClosePane`),关闭闸第七个入口,有用例守着。
- **四、** 窗格最大化 `Ctrl+Shift+X`:`MaximizedGroup` 只动渲染不动树,焦点切走即解除。
- **五、** 中键关标签、标签条滚轮、分割条双击平分;补上无样式认领的 `.dragging`。
- **六、** `:activepane` 弱化非活动窗格强调线,刻意不加过渡。
- **七、返工**:根窗格上的「关闭窗格」点了没反应;判据是有无父分栏,两种「空」分两套文案。
- **八、验收**:净增 17 条用例;快捷键文档与 `dock-replacement-plan.md` 的过期规范已同步。


## ✅ 65. 2026-09-10 防空闲断开:保活防的是链路,踢你的是 shell(用户需求)

- **一、** 保活只到 sshd;`TMOUT` 等按 tty 输入计时,两者互补。
- **二、** 发 `NUL`:被行规范丢弃却刷新 tty 活动;空格会留在命令行或被 vim 吃掉。
- **三、** 只按会话,不设全局:会踢人的只是特定几台;`AntiIdleSeconds` 的 `null` = 没开。
- **四、** `AntiIdleKeeper` 记最后一次出站写(挂在 `EnqueueOutbound`),打字时不插话;ZMODEM 期间让路。
- **五、** 接线在 `AttachTransport`,重连也覆盖。
- **六、验收**:15 条用例;文档同步已登记在 feature-plan.md。


## ✅ 66. 2026-09-10 静默注入不该留在命令历史里(用户反馈)

- **一、** 前导空格依赖默认为空的 `HISTCONTROL`,从没生效。
- **二、** 前置(保住 `$?`)`history -d`;删前先认 `__vela_hist_scrub` 记号 —— 配了 `ignorespace` 时这行没进历史,删错用户历史更糟。
- **三、** 挂在 `SendSilentCommand`,四条静默注入一视同仁;zsh 仍留痕,已登记在 feature-plan.md。
- **四、验收**:须用交互式 `bash -i` 跑(脚本模式 history 是关的),`HISTFILE` 指临时文件。


## ✅ 67. 2026-09-10 MaxMind.Db 5.2 的 4 条 MMDBSG001:private 嵌套类型,源生成器看不见

`d3422b79` 升级后多出 4 条警告,CI `-warnaserror` 下即 4 条错误。
- **一、** 5.2 新增源生成器(注册激活器以免反射),生成代码看不见 `private` 嵌套类型。
- **二、** 生成不出时静默退回反射,开裁剪 / AOT 那天才炸 —— 这才是真代价。
- **三、** 改 `internal` 仍嵌在原类里,留注释防人改回 `private`。
- **四、验收**:`EmitCompilerGeneratedFiles` 核过 `RegisterType<…>` 齐全,激活器确实接上。

## ✅ 68. 2026-09-10 标签页协议图标:颜色答「哪一台」,图标答「哪一种」(用户需求)

SSH 终端标签原本没有图标;SFTP / 工作台标签写死的 `folder-open` / `hard-drive` 也不对。
- **一、** `ConnectionAccent` 色带答「哪一台」,新 `Services/ConnectionIcon` 答「哪一种」,两者不混用。SSH `square-terminal`、SFTP/FTP `hard-drive`、插件协议 `plug`,本地终端连位置收掉。
- **二、** 插件图标不进宿主对照表(第三方进不去),契约放 SDK、传路径数据(隔离进程没有 `Icon.*` 字典)。
- **三、** `LucideIcon` 加 `Fill` / `ViewBoxSize`,只为插件的填充式品牌 logo。
- **四、** `Icon.*` 键写错不报错、静默不画,用例在真实资源字典里解析并做过反证。

## ✅ 69. 2026-09-11 插件自己的标签页图标:一个入口,以及一次当天推翻的设计

- **一、** 宿主不放「插件 id → 图标」表,口子开在 SDK。
- **二、** 第一版给三种描述符各挂三个平行属性(SDK 2.0.3),用户一句「为什么搞成 2 套了」点破,收成一个 `PluginIcon`(`Stroked` / `Filled`)发 2.0.4。敢破坏性变更是因为核过零消费者;2.0.3 如实标废弃。
- **三、** 宿主 `TabIcon` 几何、视框、填充三样一起到渲染层,少一样不是看不见就是糊。
- **四、** `FromPlugin` 刻意 `catch (Exception)`:`PathMarkupParser` 抛什么取决于错在哪,猜漏一个就连主窗口一起炸。
- **五、** AI 插件用 `PluginIcon.Filled(AiIcon.PathData, 1024)`。
- **六、七、** 遗留项已登记在 feature-plan.md;验收全绿。

## ✅ 70. 2026-09-11 插件窗口的标题栏图标,以及一组「怎么改都绿」的用例(用户反馈)

进程内 `PluginPanelWindow` 与隔离进程 `PluginHostShellWindow` 的标题栏都改读 `PanelOptions.Icon`,契约未动。
- **一、** `VelaShell.PluginHost` 刻意不依赖 `VelaShell.Controls`,自己搭 `Path`,笔画按 `2 * 视框 / 24`。
- **二、** 无返回值 async lambda 绑到不 await 的 `Dispatch` 重载,断言异常被吞;全仓 11 条假绿,机械补 `return true` 后 7 条变超时,本次未改,已登记 feature-plan.md。
- **三、** 验收全绿。

## ✅ 71. 2026-09-11 插件更新:先把「升级会毁数据」堵上,再谈检查更新

- **一、** `InstallFromVpxAsync` 覆盖安装走完整卸载,KV、机密、时序数据全删。改法照抄 CLI 的 `PluginInstaller.Swap`:`DetachForUpgradeAsync` + 旧目录原子挪进 `<插件根>/.upgrade/` + 失败回滚。必须原子换名,否则崩溃后数据会被 `PurgeUninstalledDataAsync` 当残留删掉。
- **二、** 商店新端点 `GET /api/plugins/latest?ids=…`,按 id 问、不做全量索引;`SelectLatest` 与 CLI 的 `SelectVersion` 逐条对齐;响应带 `publisherFingerprint`,下载前就知道换没换发布者。
- **三、** `HttpPluginMarketClient` 只在打开管理页时外呼一次(`PRIVACY.md` 已补)。版本比较用 `UpdateVersion` 而非丢预发布后缀的 `IsOlder`;宿主太旧给提示不给按钮;换发布者走与手动装包共用的 `InstallWithPromptsAsync`。
- **四、** velashell-docs 中英同步;遗留项已登记在 feature-plan.md。
- **五、** 验收全绿。
- **六、** CI 竞态:`ChatPanelViewUiTests` 芯片跨 await 才出现,用例却固定 `PumpAsync(10)`。**固定泵几十毫秒等于赌调度,本机赌得赢、CI 上一忙就赌输。** 同形三条改 `WaitForAsync`;压满 CPU 验证旧 3/3 挂、新 3/3 过。

## ✅ 72. 2026-09-12 远端符号链接:认得出、进得去、建得了,以及删链接会删光目标目录的洞(用户需求)

- **一、** `SftpService.DeleteAsync` 用跟随链接的 stat 判目录,删指向目录的链接会删光目标。改先 lstat,链接一律当叶子删。
- **二、** 加 `IsSymbolicLink` / `LinkTarget`;`IsDirectory` 描述指向的对象,浏览零改动。⚠️ OpenSSH 的 `SSH_FXP_SYMLINK` 参数顺序是反的,Tmds.Ssh 已处理,包装层不能再对调。
- **三、** 复制链接得到链接(`cp -P`);文件夹下载不跟进嵌套目录链接。
- **四、** 链接图标、「→ 目标」悬停、右键「新建符号链接」。
- **五、** 真实 OpenSSH 验证通过。⚠️ `localhost` 先解析到 `::1` 而 Docker Desktop 只听 IPv4,本机 Docker 用例一直在跳过。

## ✅ 73. 2026-09-12 内置编辑器:链接看得清、配色跟具名主题走、语言补一轮、窗口加大(用户反馈)

- **一、** 链接纯蓝来自 AvaloniaEdit 缺省值,改绑 `VelaInfo`,十二套主题对比度均 ≥ 3:1。
- **二、** `SyntaxPalette.From(UiTheme)` 从种子色派生、实时重着色;重着色须先置空再赋值 `SyntaxHighlighting`。
- **三、** 内置 xshd 命名颜色全部归类。
- **四、** 新增 nginx、TOML、Go、Rust 等十种 xshd,nginx 按远端目录认。
- **五、** 坑:XML 注释里不能有 `--`;AvaloniaEdit 12 内置 TeX 定义是坏的。
- **六、七、** 窗口 928×648 → 1160×820 并按工作区收缩;全绿,velashell-docs#34。

## ✅ 74. 2026-09-12 目录比较与同步:对标 WinSCP 的三件套,难的不是比较,是时间(用户需求)

比较目录、同步(预览后执行)、保持远端最新三件都做了,SFTP / FTP / FTPS 共用。
- **一、** 双栏工具条「比较目录」与 `DirectorySyncWindow`,比较 → 预览 → 同步 → 自动复查。
- **二、** 纯逻辑在 `Core/DirectorySync`;`DirectorySyncRunner` 复用传输管道,跳过冲突询问与续传探测。
- **三、** 时间是难点:传完一律回写(新增 `SetLastWriteTimeAsync`);FTP `MFMT` 按 UTC 自己拼,不用会挪时区的 FluentFTP `SetModifiedTime`;按两边较粗精度比、容差 1 秒。
- **四、** 删除最后做、取消不删;冲突不动;不跟随目录链接;远端名过 `LocalPathSafety`。
- **五、** 迟到的 `Progress<T>` 回调覆盖结论,加 `_statusEpoch` 代次作废。
- **六、** 全绿,velashell-docs#35。

## ✅ 75. 2026-09-13 同步比较:先比 SHA-256,不支持或出错再回退到大小与修改时间(用户需求)

- **一、** 只算两边都有且大小相同的文件;摘要相同不传;远端整体不支持则全部回退,单文件失败只回退它。
- **二、** SFTP 经 exec 通道 `sh -c` 跑 `sha256sum` / `shasum`;FTP 用 `HASH` / `XSHA256`;插件协议不支持。纯函数 `Core/Sftp/RemoteSha256`。
- **三、** `SyncChecksumCache` 按路径、大小、时间缓存,复查顺带成了传输后校验。
- **四、** 全绿。⚠️ NSubstitute 对接口返回值给替身而非 null,「没有客户端」须显式返回 null。

## ✅ 76. 2026-09-14 终端里打中文看不见拼音:合成串没人画(用户反馈)

- **一、** Avalonia 不画合成串,唯一出口 `SetPreeditText` 被 `SupportsPreedit` 挡住,而 `TerminalImeClient` 报 `false`。合成串属于叠画,不属于文档。
- **二、** 改报 `true`,合成串画在 `CursorOverlay`,不进缓冲不下发 PTY;列宽用 `CharWidth`,光标长相照用户配置,幽灵文本让位,报给平台的光标矩形横跨整串(否则候选窗压住拼音)。
- **三、** 失焦、关输入法、回滚态三处收尾。
- **四、** 像素级验证;macOS / Linux 未验证。

## ✅ 77. 2026-09-15 Linux 显示:改用 XWayland,修复 GNOME 下应用图标未知与鼠标指针模糊

Avalonia 12.1.2 原生 Wayland 后端不发 `set_app_id`、`SetIcon` 也没实现,图标显示未知。
经用户确认移除强制 `UseWayland()`,由 `UsePlatformDetect()` 走 X11(XWayland 承载),`WmClass` 设 `VelaShell.App`,与桌面入口的 `StartupWMClass` 一致。
本地打 deb 后用户实测图标正常、指针不再模糊;KDE、arm64 未验证。

## ✅ 78. 2026-09-17 冷启动 7 秒:瓶颈不在 JIT,在 Defender 扫未签名程序集(用户反馈)

冷 6–7.6 s、热约 1 s,各段同比例放大。实测未签名 113 MB 首次扫描 3,153 ms(27.9 ms/MB),微软签名 109 MB 仅 712 ms(6.5 ms/MB)。R2R 省的 JIT ≈ 它把未签名体积翻倍多出的扫描,净收益近零。根治是 Authenticode 签名,暂无证书。
- **一、** AI 插件 48 个未签名文件与首帧挤同一条 Defender 队列,插件激活挪到首帧之后(`FirstFrameSignal`,10 s 保险丝)。
- **二、** R2R 改 `VelaShellReadyToRun` 开关。Defender 在装载时扫,不列凭直觉的排除清单;判决实验是热态对比 `DOTNET_ReadyToRun=0`,省不到约 560 ms 即净亏。
- **三、** 补上 `AvaloniaUI.DiagnosticsSupport` 从未生效的 Release Condition(发行包里 DevTools 一直开着)。

## ✅ 79. 2026-09-18 四种出站代理按规约对齐:system 是解析器,none 必须真直连(用户反馈 #464)

实测 CONNECT 确实到了 Clash、之后无字节,而直连正常 —— 是节点出口问题,不是 VelaShell 的 bug。随后按用户给的规约对齐四种模式:
- none 真直连,不读 `HTTP_PROXY` 等环境变量;system 是解析器不是协议,作用于全部出站 TCP,按 scheme 落成 http 或 socks5,PAC 回 DIRECT 照直连;socks5 默认远程 DNS。
- 系统代理变更无需重启(每次重读 `HttpClient.DefaultProxy`)。自动重连失败标签的第一版是死代码,见 09-20 复查 §82。
- 每会话代理、「测试代理」按钮、WinHTTP 机器级代理明确没做。
- **追加(09-18 晚)**:该节点唯独 :22 被静默黑洞,在 VelaShell 之外;给报错补的说明被维护者要求回退。

## ✅ 80. 2026-09-19 网络代理页:说明文字被裁 + 补一块「四种模式的区别」(用户截图反馈)

根因是 `TextBlock.page-subtitle` 漏了 `TextWrapping`(`row-desc` 早就设了),按类修,所有设置页受益。
四种模式说明第一版做成只读文本框被嫌「太乱」,改成表格,模式名复用下拉框文案 `SetProxy_Type*`,不另造译名。
`ProxySettingsPageUiTests` 断言副标题真的折成多行,去掉 `TextWrapping` 即红。

## ✅ 81. 2026-09-19 终端核心回路:一次 SIMD 调研,五个候选三项被实测否决(性能调研)

直觉热点多已被 BCL 或缓存覆盖;收益来自换 BCL 向量化重载与少数结构体特化,两个方向都得先量。
- **A1 ✅** `TrimToContent` 按 `Vector<byte>` 判零,2.6x / 1.6x;`LastIndexOfAnyExcept<T>` 反而慢约 4 倍。
- **A2 ✅** `Fill` / `FillRange` → `Span<T>.Fill`,−26%;手写 `Vector128` 与循环持平。
- **A3 ❌** 真 Skia 满屏一帧只 54 个绘制操作,无全量重算可救,分带代价不成比例(feature-plan「确认不做」);渲染结论只在 `RenderTests` 取。
- **B5 ✅** `EchoSuppressor.IndexOf` → `Span.IndexOf`,25.6 µs → 1.5 µs,16.9x。
- **B6 ❌** `Decoder.GetChars` 纯 ASCII 已达约 26 GB/s,手写只快 24% 且被校验吃掉。

## ✅ 82. 2026-09-20 资源管理器的折叠与置顶、rm -rf 删目录、路径栏复制(用户需求 #474)

五条落地四条;3 SFTP 拖拽移动按既有决策不做(易误触,见 feature-plan.md「确认不做」)。
- **1 与 2 是一件事**:组内上浮的置顶在全折叠后看不见,所以提到整棵树最前,只改 `SyncRows` 摊平层、同一节点只出现一次;折叠设置配进程内 `_groupExpansion` 记忆,否则每次重建树都把刚展开的组折回去。
- **4** `rm -rf` 是快路径不是替换:四道闸,失败回退 SFTP 递归并带出真实原因。⚠️ NSubstitute 递归替身让 `GetClient` 非 null,「依赖不存在」须显式写 null。
- **5** 路径栏复制按钮复用既有 `CopyCurrentPathCommand`,零新增字符串。

## ✅ 82. 2026-09-20 复查 #464 的修复:自动重连那一条是死代码,另修三处(代码复查)

- **P0**:终端标签失败态是 `Disconnected` + `ConnectionError`,没有 `Error`,筛选恒跳过。判据抽进 `ReconnectPolicy.ShouldReconnectAfterProxyChange`,用真实标签测。
- **另外几处**:`IsLoopback` 注释改正;`FormatHost` 不再给 IPv6 套两层方括号;新增 `ProxyMisconfiguredException` 代替按本地化文本比对;FTP 的 `via` 记实际路由;`LoopbackProxyRelay.Error` 改 volatile。
- **一处只补文档**:探针 scheme `https` → `http` 是行为变更,注释写明。

## ✅ 83. 2026-09-20 换了台服务器、IP 没变:指纹变更从"报错"改成"弹窗裁决"(用户需求 #476)

§13 的三选项弹窗本来就有,被一个默认值挡住了。
- **一、** `BlockOnFingerprintChange` 默认改 `false`,加一次性迁移。
- **二、** 弹窗摆出旧指纹与两种可能的建议。
- **三、** 原因牌 `HostKeyPromptOutcome` 区分策略阻断与用户取消。
- **四、** 跳板一跳与终点共用 `AddHostAuthentication`。
- **五、** 弹窗期间 `ConnectTimeout` 仍在跑,认了指纹且库内超时时补连一次。

## ✅ 84. 2026-09-20 `-newtab` 的标签名被当成了 URL:先到先得改成按可信度取用(用户反馈 #475)

JumpServer Client 发 `-newtab <标签名> -url ssh://…`,`XshellLaunchParser` 的 `url ??=` 让先出现的标签名占坑,窗口开了却不连接也不报错。
「先到先得」在顺序不由我们决定的输入上必然栽。改为 `-url` > 裸参数 > `-newtab` 按可信度取,`-newtab` 须解析出主机才收。
标签名目前只被忽略,未用作标签页标题(已登记在 feature-plan.md)。velashell-docs 中英已同步。

## ✅ 85. 2026-09-20 复查 #475:同一个家族里另有三处静默失败(代码复查)

23 条探针命令行又翻出三处静默失败。
- **一、** `-url` 不挑长相。
- **二、** 三个来源逐条试,高优先级解析失败就退让。
- **三、** `-newtab` 必须带 scheme。
- **四、** `PeekValue` 按选项名判,不按破折号(口令可能以 `-` 开头)。
- **五、** IPv6 zone id 放宽,`%25` 还原。

## ✅ 86. 2026-09-20 密钥生成默认给 Ed25519(`feature-plan.md` 🔒 P2 项)

「生成密钥」原先只产 RSA 4096;改为默认 Ed25519(`ssh-keygen` 9.5 起的默认,且有些堡垒机已拒收 `ssh-rsa`),RSA 保留可选。
- **一、为什么引 BouncyCastle**:它本就是 Tmds.Ssh 的依赖,体积增量为零;BCL 没有独立 Ed25519,「种子导出公钥」的曲线运算不手写。当时中央包里它的版本必须跟着 Tmds.Ssh 走(§91 换库后不再有这层约束)。
- **二、接口**:`GenerateRsaKeyAsync` → `GenerateKeyAsync(name, algorithm, rsaBits)` + `SshKeyAlgorithm`;默认名 `velashell_ed25519`,老的 `velashell_rsa` 不动。
- **三、坑**:ed25519 私钥字段是 seed‖pub 共 64 字节;只写种子也能加载,连服务器才签名失败 —— 所以测试单独拆字节验,并用系统 `ssh-keygen -y` 逐字节对过。

## ✅ 87. 2026-09-20 密钥生成做完整:ECDSA 三条曲线 + 界面算法下拉(接 §86)

- **一、ECDSA:曲线表只留一份**:`DescribeCurve` 供生成与导入共用,拆出 `BuildEcdsaPublicBlob`;⚠️ P-521 坐标 66 字节不是 65。`bits` 为 0 按算法取默认,ECDSA 只收三条 NIST 曲线。
- **二、下拉的唯一来源在视图模型**:`SshKeyManagerViewModel.AlgorithmChoices`;axaml 靠 `SelectedIndex` 按顺序对齐 —— 错位不报错、选 Ed25519 拿到 RSA,所以 `SshKeyChoiceCatalogTests` 直接读 axaml 比对。RSA 只给 4096。
- 五种钥均经 `ssh-keygen -y` / `-lf` 交叉验证。

## ✅ 88. 2026-09-20 SFTP 路径栏:编辑 / 复制钮改用工具栏图标钮规格(用户反馈)

两颗钮原挂面包屑的 `crumb`(命中区不到 15×13、零间距),改为 `toolbar-btn` 24×24 / 13px、间距 2px,补读屏名。`crumb` 不动 —— 面包屑是文字链,不是图标钮。
- **补:跟随终端目录 map-pin → terminal**:`LucideIcon` 不做外接框归一化,map-pin 重心偏上对不齐;pin 的语义也恰是反义。
- **补:清孤儿图标键**:本仓库零引用 ≠ 可删 —— 进程内第一方插件按键名取字形,取不到静默不画。只删 `map-pin` / `folder-up`;`gauge` 与 `arrow-*-half` 留下,删除前提写进 `Icons.axaml` 头部。

## ✅ 89. 2026-09-21 打开 AI 面板把主窗口冻住:三处压在 UI 线程上(用户反馈)

- **一、异步签名、同步实现**:`SonnetDbEngine` 闸不争用时 `action` 原地跑在调用方(UI 线程)。加 `InLockAsync`,闸内活进 `Task.Run`。
- **二、初始化跑在构造函数里**:`ChatPanelView.InitAsync` 先 `Task.Yield()` 并记 `_initTask`;读整份设置的入口先等它,否则设置窗口回写默认值会冲掉全部接入与登录态。
- **三、首开在 UI 线程上读三十多兆程序集**:冷缓存约 17 秒,全是磁盘 IO。后台 `PrewarmPanelAssemblies` 预装 —— 只装程序集不碰类型、走本插件的 ALC、不在插件 ALC 里就跳过。
- **顺带说明**:`plugin_data` 不回收、输入历史全表扫,都不是本次原因。

## ✅ 90. 2026-09-21 隧道面板:下拉混进 FTP/SFTP,自动重连那行被模板撑远(用户反馈)

- **一、只列 SSH**:端口转发只有 SSH 给得出通道;过滤补在 `TunnelPanelViewModel` —— 前提由持有它的那层保证。
- **二、⚠️ Fluent CheckBox 模板 12px 死高**:模板 XAML 里的直接赋值是 `LocalValue`,优先级高于外部样式 Setter,`MinHeight` / `Padding` 压不动。用 `Margin="0,-6"` 吃掉,间距回到设计的 8px,UI 测试钉住。

## ✅ 91. 2026-09-22 Tmds.Ssh → VelaShell.Ssh:换底层 SSH 库,释放全面异步化(用户需求)

分支 `feat/velashell-ssh`,当时走跨仓库工程引用。删掉的近 1200 行都是为绕开旧库,换回一层薄适配。
- **一、先补库的缺口**:**A.** 加密私钥的 `bcrypt_pbkdf` —— 「不手写密码学原语」唯一破例,Blowfish 常数现算 π 不抄表。**B.** OpenSSH 用户证书,签名委托私钥,CA 签名不在客户端验。⚠️ AEAD tag 在私钥 string 外面,放错时 checkint 照对、测试全绿。
- **二、删掉的 ~1200 行**:计量转发句柄、算法探测、本机代理接力、按消息字符串分派异常、PEM 转换 —— 新库都原生给了。
- **三、⚠️ 两条纪律**(用户定):不许用 `Task.Run` 假异步,库不支持就改库;释放也走异步。四个契约改 `IAsyncDisposable`,`Disconnect()` / `Stop()` 删掉,等待预算用 `Task.WaitAsync`。
- **四、⚠️ 证书靶机撞端口**:占了 `ssh-shells` 的 2223,harness 连错靶机、32 条用例全红;挪到 2224,Dockerfile 加 `sshd -T` 硬自检。
- **五、验证**:3604 通过 / 0 失败,证书端到端真跑。
- **六、文档漂移**:「当前用 Tmds.Ssh」全部改掉,历史陈述保留。
- **七、合并前审查**:改回六处行为漂移,最要紧的是指纹带 `SHA256:` 前缀让已存主机全判「已变更」(`SameFingerprint`)、rename 回退到原子 `posix-rename` 会静默覆盖。

## ✅ 92. 2026-09-23 SSH Agent 认证与转发、X11 转发、压缩开关(用户需求)

§91 之后的宿主接线,存 `SessionProfile.Ssh`(全关存 `null`,老配置零迁移)。
- **一、四样东西**:`AuthMethod.Agent`(枚举按序号落盘,只能加在末尾)、压缩 `zlib@openssh.com`、agent 转发(-A)、X11 转发(-Y)。
- **二、取舍**:压缩在 KEXINIT 就定了,不做「按延迟自动开」;转发被拒不连累会话,写进 `IShellStreamWrapper.Notices`;X11 默认受信任(Windows 的 X 服务器没有 SECURITY 扩展);连 agent 3 秒上限。
- **三、验证**:端到端 6/6(压缩真协商、假 X 服务器、假 agent)。遗留项已登记在 feature-plan.md。

## ✅ 92. 2026-09-23 把 VelaShell.Ssh 并进本仓库,不单独发 NuGet(用户需求)

库只有宿主一个使用者,单独维护发版流水线得不偿失;直接拷进来,不保留 git 历史。
- **一、搬到哪**:`src/VelaShell.Ssh/`(仍按 MIT,`LICENSE` / `NOTICE.md` 随目录)、`tests/VelaShell.Ssh.Tests/`、`eng/` → `scripts/ssh/`、文档 → velashell-docs `zh|en/ssh/`。
- **二、构建对齐**:严格规则只收进本工程 csproj;随 Release 签名,`InternalsVisibleTo` 只给非签名构建(所以测试用 Debug);测试运行器改 VSTest;BouncyCastle 只剩一条版本。
- **三、CI**:加 `ssh-checks` / `ssh-interop`;主作业过滤 `TestCategory!=Interop`;相似度门禁按用户决定整体移除。
- **四、引用**:链接与注释改指本仓库与 `velashell-docs/zh/ssh/`;§91 记的库侧三条已随代码修好进来。

## ✅ 93. 2026-09-23 CI 去掉 `ssh-interop` 作业,解决方案补齐 `.github` 文件(用户需求)

`ssh-interop` 在 PR 上永远显示 Skipped、像没跑的门禁,删掉;互操作用例改为本地用 `scripts/ssh/interop/Start-TestServer.ps1` 起靶机按需跑。`VelaShell.slnx` 补上 `.github/ISSUE_TEMPLATE/` 三个文件。

## ✅ 94. 2026-09-23 关通道时 stdin 泵撞上「reader 完成后不许再读」(用户反馈)

`SshChannel.FinishClose` 从外面完成了归 `PumpStandardInputAsync` 所有的 reader。改为 reader 只由泵自己完成,泵没起来时才由 `FinishClose` 代劳。断开时的取消异常是正常的首次机会异常,不改。

## ✅ 95. 2026-09-23 宿主接上 SSH 库的 X11 尽力而为(`BestEffort`)(用户需求)

X11 选项一律 `BestEffort = true`,失败原因取自 `SshShell.X11SetupFailure`、写一行黄字;`OpenShellWithFallbackAsync` 的多轮重试删掉,只剩 agent 被拒时重开一次。靶机给 `vela-dash` 关掉 X11 做端到端用例。

## ✅ 96. 2026-09-23 标签页协议图标的绑定错误(用户反馈)

本地终端的 `TabIcon` 为 null,绑 `Terminal.TabIcon.Xxx` 每开一个刷三条绑定错误,会淹掉真错误。四个标签模板把图标 `DataContext` 收窄到 `TabIcon`,null 时静默;回归 `SessionTabIconUiTests`。

## ✅ 97. 2026-09-23 CI 在 Linux / macOS 上随机红:请求账本「入队后才登记」的竞态(#492)

`SshConnectionSend.EnqueueAsync` 先入队后登记:发送泵可能先把帧发出、应答先到,接收循环 FIFO 失步判死连接,挂着的请求全被结算成「拒绝」。改为**先登记、再入队**(仍在同一把锁里)。教训:`FifoRequestLedger` 的注释早写着「登记必须在发送之前」,实现没兑现;竞态靠在回调里睡 300ms 撑大窗口才稳定复现。

## ✅ 98. 2026-09-23 自动加载密钥到 Agent(设置审计 R-06,`feature-plan.md` 🔴 P0 项)

- **一、SSH 库**:先写规格 spec/07 §7.3 再实现 `SshAgentClient.AddIdentityAsync`(无约束 `17`、有约束 `25`),明文私钥缓冲用完清零。私钥字段错位 agent 照回 SUCCESS,只有验签抓得出。
- **二、宿主**:认证成功后才在后台 `SshAgentKeyLoader.AddAsync` —— 配错的钥不进 agent,3 秒上限不落在连接路径上;已有同钥不再加,失败只记 Trace。
- **三、改字段名、默认关**:旧 `AutoLoadToAgent` 默认 true 且被整份序列化,不是用户选的;Windows 的 agent 会把钥存进注册表跨重启保留。新字段 `AddKeysToAgent` 默认关,旧键被忽略。
- **四、界面**:设置 → 密钥管理新增「SSH Agent」一节。
- **五、验证**:单测 + 对真实 OpenSSH agent 的互操作通过(⚠️ `ssh-test` 2222 禁了 TCP 转发,要用 ssh-shells)。

## ✅ 99. 2026-09-23 CI 随机红三处:重协商测试桩、X11 单连接放行两条、AI 插话用例赌调度(CI 反馈)

- **一、重协商测试桩**:与 §97 同类,KEXINIT 发出之后才赋值;改为在发送回调里、上线前记下。库无改动。
- **二、X11 单连接放行两条(库缺陷)**:「先看计数再加」不原子;改 `Interlocked.Exchange` 认领名额,没转发出去就退回。
- **三、AI 插话用例**:回车赶在第一次请求前被并进去;先等 `stub.Requests.Count >= 1` 再按。产品代码无改动。

## ✅ 100. 2026-09-23 本机 X Server:设置页 + 标题栏开关 + 拉起 VcXsrv(用户需求)

仍不捆绑 X 服务端,而是拉起用户装好的 VcXsrv;只在 Windows 上启用。
- **一、分层**:Core 放 `XServerOptions` / `ILocalXServer` / `XServerCommandLine`,Infrastructure 放 `VcXsrvLocator` / `VcXsrvLocalXServer`,App 放设置页、帮助对话框、标题栏按钮。
- **二、取舍**:「运行中」以 `6000+N` 端口能连为准;显示解析「配置 → 本机 X Server → `DISPLAY` → `localhost:0.0`」;已有别的 X 服务端或没装 VcXsrv 时静默不插手;开关显式写两态;只管自己拉起的进程。
- **三、验证**:单测覆盖;没在装了 VcXsrv 的机器上真跑过。

## ✅ 101. 2026-09-23 X11 服务端库 VelaShell.XServer:M1 核心协议(用户需求)

.NET 生态没有可用的 X 服务端库,照 VelaShell.Ssh 的做法新建 MIT 库 `src/VelaShell.XServer/`;本节只有库,宿主未接。
- **一、定位与纪律**:可嵌入、rootless、零原生依赖,宿主即窗口管理器。净室规程同 VelaShell.Ssh,依据只能是 X.Org 规范 / ICCCM / EWMH / BDF。
- **二、M1 做了什么**:全部 119 个核心请求 + BIG-REQUESTS + XC-MISC,单线程串行执行,软件光栅化,内置 misc-misc 字体。
- **三、验证**:单测 40 条;xterm / xeyes / xclock 等 5 个真实客户端零协议错误。

## ✅ 102. 2026-09-23 agent 转发:只转发选中的密钥、每次签名前询问(`feature-plan.md` D 组)

- **一、配置与界面**:`AgentForwardKeys`(存公钥行)+ `AgentForwardConfirm`;存过的钥即使别处没了也保持勾选,否则保存一次就悄悄少一把。
- **二、fail-closed**:限定的钥全解析失败就不转发(库把空列表当「全可见」);确认 60 秒无人应答按拒绝。
- **三、确认框**:`AgentSignPromptView`,「拒绝」是默认键与取消键并获焦点 —— 弹窗时用户可能正在打字。
- **四、验证**:单测 + 真实 OpenSSH 端到端(确认无 `[SKIP]`)。

## ✅ 103. 2026-09-23 VelaShell.XServer M2:现代工具包要的扩展(用户需求)

- **一、做了什么**:SHAPE、XFIXES、RANDR(只读)、RENDER、剪贴板互通、XSETTINGS 管理器,协议错误附上最近 8 条请求。
- **二、取舍**:XKB / XInput2 不能只做一半(一出现在 `QueryExtension` 里客户端就改走它),推迟;RENDER 浮点逐像素,正确性优先。
- **三、验证**:单测 73 条;gedit / zenity / Qt5 零协议错误,xclip 双向互通。

## ✅ 104. 2026-09-23 VelaShell.XServer:功能完备一轮与全库性能复查(用户需求)

按「以后可能作为开源库给别人用」的标准,对照 X.Org 规范与 GTK3 / Qt5 / 常用工具实际会碰的扩展补缺口,再按热路径全库复查。宿主此时未接入(M3)。

**一、补了什么**:GE、XTEST、XINERAMA、SCREEN-SAVER / DPMS、X-Resource、SYNC、DAMAGE、Composite、DBE、Present、XI 2.2、XKB;窗口管理器角色(EWMH / ICCCM,根窗口请求翻成 `XWindowManagerRequest` 交宿主,`IXServerHost` 用默认实现方法扩展、不破坏已有宿主);Unix 套接字,运行中换布局 / DPI / 键盘。

**二、全库复查:性能与异步**:损伤合并封顶 8 块矩形、可见区域缓存、RENDER 整数内核;宿主回调曾在 `PixelLock` 内同步调、与 UI 线程死锁,改由 `DeferredHost` 放锁后按序调;输出积压 64 MB 断开、未执行请求每客户端 1024 条;执行线程按 4 ms 预算放锁;大请求先校验再分配。⚠️ 基准(`scripts/xserver/bench/bench.cs`)复查后是预热值、复查前是冷启动,冷热本身约差 1.4 倍。

**三、顺手修掉的协议细节**:SYNC、PointerMotionHint、Enter/Leave 等边界;初始焦点须为 PointerRoot,否则 xdotool 打不进字。

**四、验证**:单测 121 条,`VELASHELL_XSERVER_INTEROP=1` 零协议错误,gedit / qt5ct 手测。遗留项已登记在 feature-plan.md(M3 见 §105,设备拓扑与 XKB 改表见 §108)。

## ✅ 105. 2026-09-24 VelaShell.XServer M3:接入宿主(用户需求)

「X Server」默认启动内置的 `VelaShell.XServer`,每个 X 顶层窗口一个 Avalonia 原生窗口,SSH 的 X11 转发直接接进它;VcXsrv 退成 Windows 上的可选引擎(`XServerOptions.Engine`)。

**一、结构**:`BuiltInLocalXServer`(连接器 = 内存双工流)、`LocalXServerSelector`、`IEmbeddedXServerHost`、`AvaloniaXServerHost`(回调全 Post 到 UI 线程)、`XNativeWindow`;SSH 库加 `X11ForwardOptions.LocalConnector`(只支持受信模式,spec 07 §7.5.9)。

**二、几处取舍**:SSH 不绕本机 TCP,但环回 TCP 与 Unix 套接字照样开;本机已有 X 就不自动启动;输入法(XIM)不做,中日韩走远端输入法。

**三、真实窗口验证中修掉的**:FreePixmap 不再连带销毁 Damage;Unix 监听先试连,有人应答就不删套接字(否则会删掉桌面 Xorg 的);显示后改尺寸要设 `Width` / `Height` 而非 `ClientSize`。

**四、验证**:单测 +21,interop 8/8;`scripts/xserver/host-demo` 实测 xterm / gedit / qt5ct 几何一致。遗留项已登记在 feature-plan.md。

## ✅ 106. 2026-09-24 内置 X 服务端:AltGr 层(§105 的后续)

§105 后 Windows 上只推两层,德语 `@` / `€` 这类 AltGr 字符打不出来。

**一、做了什么**:XKB 加四级键类型(按 XKB 规范 §17 列序,三、四级由 Mod5 选)、`X11Server.SetModifierMapping`;有 AltGr 的布局把右 Alt 设成 `ISO_Level3_Shift`。坑:Windows 按 AltGr 会先补一个假左 Ctrl,`XNativeWindow` 在 50 ms 内认出并撤掉,否则 X 程序看到的是 Ctrl+AltGr 快捷键。

**二、顺手修掉的**:窄的 ChangeKeyboardMapping(xmodmap 每键码 1 列)曾把整张表收成 1 列;现在列数只放宽不收窄。

**三、验证**:单测 +2,容器里打出 `@`;Windows 德语布局没实测(会改用户的输入语言列表)。

## ✅ 107. 2026-09-23 VelaShell.Ssh:压缩只保留 zlib@openssh.com(用户需求)

去掉裸 `zlib`:它从首次 NEWKEYS 起就压,认证报文也进压缩流,未认证方可做 CRIME 类旁路。

**一、做了什么**:`SshCompressorFactory.EnsureSupported` 只认 `none` / `zlib@openssh.com`,协商完当场校验 —— 谈成未实现的算法就在协商处失败,不悄悄按不压缩处理。

**二、验证**:Ssh.Tests 574 通过;文档 spec 00 §6.5、01 §六。

## ✅ 108. 2026-09-24 VelaShell.XServer M4:同步抓取、设备拓扑、XKB 改表、MIT-SHM、GLX

架构文档 §8 里 M4 剩下的五项一轮做完。

**一、做了什么**:同步抓取(冻结设备、AllowEvents / XIAllowEvents 放行重放);XIChangeHierarchy 动态设备表;XKB SetMap(`setxkbmap | xkbcomp` 可用);MIT-SHM 1.1(只在 Linux、只对 Unix 套接字客户端可见,SO_PEERCRED 校验属主);GLX 1.4:直接上下文只登记,Mesa 在客户端用 llvmpipe 渲染后 PutImage,间接上下文由新目录 `Gl/` 的软件 GL 执行固定功能子集。

**二、几处取舍**:GL 如实报 1.1(没 3D 纹理);间接渲染不做求值器、累积缓冲、选择 / 反馈、mipmap LOD;GLX 表面封顶 4096²,显示列表展开上限 400 万条(嵌套可到 2^64);MIT-SHM 只给本机;多指针只到拓扑,不是完整 MPX。

**三、真实客户端验证中修掉的**:`xinput remove-master` 的返回设备 0 表示挂回核心设备;`xwd -root` 按堆叠次序拼顶层。

**四、验证**:单测 +17、真实客户端用例 +3,glxgears 两条路径一致、零协议错误。遗留项已登记在 feature-plan.md。

## ✅ 109. 2026-09-24 内置 X 服务端:键盘布局可选,「自动」三平台跟随系统(§105、§106 的后续)

此前只有 Windows 跟随系统布局,macOS / Linux 一律 US。

**一、做了什么**:「键盘布局」两种引擎共用;`BundledKeymaps.cs` 由 `scripts/xserver/keymaps/generate.cs` 生成 27 个布局;抽出共用 `HostKeymap`;`MacKeymap` 用 TIS + `UCKeyTranslate`;`LinuxKeymap` 经 libxkbcommon-x11 读桌面 `$DISPLAY`,只认右 Alt 为 Level3(表里总有虚拟 `<LVL3>`,扫全表会把美式误判成有 AltGr)。

**二、验证**:单测 +6,Linux 在容器实测。坑:TIS 只能在主线程调;**macOS「自动」没在真 Mac 上跑过**;CI 补装 libxkbcommon(PR #503)。遗留项已登记在 feature-plan.md。

## ✅ 110. 2026-09-24 内置 X 服务端:SSH 转发两处缺陷与 GTK4 经 SSH 慢 / 卡排查(用户反馈)

**一、宿主的两处缺陷(ccd6c90f)**:连接器捕获了旧 `X11Server`,重启 X Server 后接进已释放实例 —— 改为每条通道取当前在运行的;连接器路径没传远端 EOF,窗口不关 —— 改 `CompleteWrites()`。

**二、慢与卡:远端环境,不是 X 服务端**:慢是 portal 两次 25 s 超时(只经 SSH 登录时用户会话无 `DISPLAY`,`import-environment` 后约 3 s);卡是 GTK4 默认 GL 退到 llvmpipe、整窗 PutImage,`GSK_RENDERER=cairo` 可解。写进 velashell-docs `xserver/troubleshooting.md`。

**三、验证**:单测 +3;宿主替用户改远端环境属越界,没做。

## ✅ 111. 2026-09-24 VelaShell.Ssh：全库审查，修掉第一批（用户需求）

按八个子系统通读 `src/VelaShell.Ssh`(守净室规程)。规则:每项配用例,先撤修复确认失败再确认通过。

**一、直接伤到使用者的**:agent 身份解析接错异常;认证只把凭据回调与签名器的异常算「跳过」;释放时挂住的调用方、取消开通道泄漏的通道;CLOSE 时序;严格 KEX 改为首次交换时定下(测试桩错在同处,所以一直绿);SFTP 顺序读预读。

**二、DoS 上限与窗口预算**:256 MiB 会话窗口预算真正生效;`MaxQueuedReplyBytes` 等队列上限;重协商 2 分钟超时;交换进行中不再发第二个 KEXINIT。

**三、验证**:626 条,新增 30 条;没对真实 OpenSSH 跑(§113 补上)。

## ✅ 112. 2026-09-24 VelaShell.Ssh：全库审查，修掉第二批（用户需求）

修完审查报告第二节(安全)剩下的七项,做法同 §111。

**一、做了什么**:重协商钉住首次的主机密钥、不再问策略;`AgentForwarder` 释放即停签;`DISPLAY=localhost:N` 只走 TCP(抽象套接字可被同机用户抢绑);KDF 参数设上限;命名管道 agent 校验属主;对端文本经 `PeerText.Sanitize` 清洗;`KnownHostsPolicy` 加 `OtherKeyTypesKnown`,`ProxyCommand` 拒 shell 元字符。

**二、顺带发现并修掉的**:测试服务端收 CLOSE 不给处理器结尾,新用例「通过」接的其实是超时 —— 断言要收紧。

**三、验证**:655 条,满跑 25 轮全绿;别的用户抢注管道没法在本机测。

## ✅ 113. 2026-09-25 VelaShell.Ssh：全库审查，修掉第三批，并补上主机证书（用户需求）

修完审查报告第三到第五节。用户说「太旧的不安全的算法先不考虑」:旧式加密 PEM 改回明确报错,gssapi-with-mic 只做评估。

**一、正确性**:报文中途断开归为 `ClosedByPeer`;连接前 `SshAlgorithmSet.Validate()`;异常断开时通道流以失败结束而非 EOF,relay 另一侧中止;拨号改 Happy Eyeballs(RFC 8305)。

**二、性能**:AES-CTR + HMAC 零分配(~800 → ~1100+ MB/s);WINDOW_ADJUST 走优先通道;自适应窗口按「读者是否饿着」扩窗。

**三、设计**:开通道决定移出接收循环;`NormalizeFault` 统一异常体系;`ssh_config` 的 `Include` / `Match` 修正;`DelayedStream` 改流水线。两套 `ssh_config` 解析器并存没动。

**四、主机证书与算法清单**:实现 `*-cert-v01` 与 `@cert-authority`(spec 03 §5.5),CA 担保的不合格即拒、不退回 TOFU;宿主侧与 gssapi 记入 feature-plan.md E 组。清单外的算法用户确认保留,不必再提议删。

**五、同步文档时顺带核出来的**:规格一批早与代码不符,已改正;对端 CLOSE 前不发 EOF 仍按正常结束(互操作风险大于收益)。

**六、验证**:761 条全过,22 条互操作对真实 OpenSSH 跑过。遗留项已登记在 feature-plan.md。

## ✅ 114. 2026-09-25 VelaShell.XServer:全库审查,并优化渲染路径(用户需求)

按五个子系统通读全库(守 `src/VelaShell.XServer/AGENTS.md` 净室规程),这一轮**只改渲染路径**。

**一、渲染路径**:宿主取像素改走新增的 `XTopLevelWindow.ReadPixels`(锁内回调、宽高取自此刻的缓冲,顺带修掉客户端改大窗口时 UI 线程越界的崩溃),只取损伤矩形、按帧取(`RequestAnimationFrame`)、切 256×256 小块只重传被改的块;新增 `PixelGate`,防 `lock` 不公平时宿主读像素被饿住、整个界面跟着卡;PutImage 32 位直贴(`Rasterizer.Blit`),其余格式只解码画得到的部分(`X11Server.PutImageRegion`)。

**二、基准**:`scripts/xserver/bench/bench.cs` 新增三个场景;整窗 PutImage 的 CPU 时间降约一成,宿主那一半没有量化数字。

**三、其余发现**:去重后 32 项,本节未修,分 A–E 五组整体登记在 `feature-plan.md`「内置 X 服务端（VelaShell.XServer）全库审查的待修项」。其中 `XTopLevelWindow` 跨线程读到撕裂几何那一项随 §116 的 `XTopLevelSnapshot` 修掉(09-26 复核),其余 31 项在 §124 逐项修掉。

**四、验证**:XServer.Tests 在 Windows 与 Linux 容器全绿(新增 `ImageTests`,此前 PutImage 没有像素用例),真实客户端零协议错误;分数缩放下小块有无接缝没实机看过。文档:`xserver/design/architecture.md` §5、§6、§10。

## ✅ 115. 2026-09-25 VelaShell.Ssh：对照 Tmds.Ssh 维护者对 X11 转发的改动，加固四处（用户需求）

**一、怎么做的**：净室规程不许看别的 SSH 实现源码，于是两阶段隔离 —— 不带本库上下文的独立代理读对方 PR、只交回行为描述（零代码、零对方标识符），实现侧对照 RFC 4254 §6.3、SECURITY 扩展规格与 `xauth(1)` 先改规格（`spec/07`、`spec/09`）再实现；过程记在 `architecture.md` §11.2.23。

**二、对照结论**：多数行为本库已有或更稳妥，不跟（如 `localhost:N` 按 TCP 原样交给 `xauth`；对方改写成 `unix:N`，嵌套 `ssh -X` 会连不上）。

**三、做了的四处**：授权字段上限降到 256 字节、读到头就判（`X11SetupMessage.MaxFieldLength`）；`xauth` 的 timeout 比有效期多 60 秒、有效期 0 时传 0（`X11Forwarder.XAuthTimeoutSeconds`）；临时目录失败报 `SshForwardException`，不再让尽力而为的会话起不来；支持 `ForwardX11Timeout`。有意与 OpenSSH 不同：有效期对受信模式也生效。

**四、验证**：`VelaShell.Ssh.Tests` 全绿（含真实 OpenSSH 互操作），两项修复先撤掉确认用例会红。

## ✅ 116. 2026-09-25 VelaShell.XServer:宿主 API 与代码组织整理(用户需求)

评审出九条 API 问题全部修掉,协议行为不变;顺带修了两处真缺陷:键盘布局名从没传到服务端、位图 / ARGB 光标一律显示成箭头。

**一、公开 API**:公开类型并进根命名空间 `VelaShell.XServer`、公开成员集中到 `X11Server.cs`;命名分 `Inject*` / `*TopLevel` / `Set*`,回调「主语 + 过去分词」,窗口一律用 `XTopLevelWindow` 句柄;属性移进不可变的 `XTopLevelSnapshot` 整份替换(原先跨线程逐个读写会撕裂);新增 `CursorChanged(window, XCursor)`、`SetKeymap(XKeymap)`(一次提交、只发一轮通知);`XServerOptions` → `X11ServerOptions`(构造时校验,消掉与宿主同名)。

**二、内部组织**:扩展操作码收进 `X11Server.Extensions.cs` 一张表并挂清理钩子;GLX 拆成独立的 `GlxExtension`;文件名与内容对上;处理器与协议请求同名、内部操作换动词。

**三、小项**:指向同一宿主的两个字段合一;响铃按协议公式换算成 0–100。

**四、验证**:XServer.Tests 全绿、真实客户端零协议错误;Core.Tests 那条 X11 靶机用例失败是本机靶机镜像早于 `ccc72015`。文档 `xserver/design/architecture.md` 与本仓库 `src/VelaShell.XServer/README.md`、`AGENTS.md` 同步。

## ✅ 117. 2026-09-25 VelaShell.Ssh：API 设计审查，定下规范并整改（用户需求）

SSH 库此前没有成文的 API 规范，这次写进 `src/VelaShell.Ssh/AGENTS.md` 第四节（4.1–4.8），再按它整改。

**一、审查结论**：191 个公开类型约 125 个宿主从未引用、协议管道全 public；同一功能多个入口；5 个类型与宿主撞名；异常 `Reason` 写死，宿主只能透传中文 `Message`；安全默认值不安全（`SshHostKeyDecision.Accept` 是枚举零值等）。

**二、改了什么**：
- 公开面 191 → 130，协议管道与拨号器具体类型降 internal（入口是 `DialerChain`）；建连只剩 `SshConnection.ConnectAsync`，跑命令只剩 `RunAsync` → `SshCommandResult`。
- 命名统一；枚举零值一律取安全值（`Reject` / `Failure` / `Unknown`）；`SessionId` 改 `ReadOnlyMemory<byte>`。
- `SshFailureReason` 新增私钥、agent、通道请求、转发等原因码并按实情报 —— 宿主这才能按码本地化。
- 一个类型一个文件；直接完成 `StandardInput` 也发 `CHANNEL_EOF`（原先远端 `cat` 会一直等）。

**三、宿主这一侧**：删掉库里已有的重复实现（`SshChannelStream`、`SshJumpDialer`、`SshConfigParser`、代理握手），只留适配层；`SshInterop.Localize` 按原因码本地化。

**四、有意没做的**：两个上帝类（`SshConnection`、`SshChannel`）没拆等遗留项已登记在 `feature-plan.md`。

**五、验证**：`VelaShell.Ssh.Tests` 全绿、互操作无跳过。文档在 velashell-docs `fix/ssh-api-cleanup` 分支，与本仓库 PR 互引后一起合。

## 🚧 118. 2026-09-26 窗口外框跨平台适配：全部窗口按平台走原生机制（用户需求）

弹窗原是无装饰透明窗 + 卡片 16px 边距里画阴影：Linux 合成器沿整个窗口矩形描边、模糊，边距成了一圈磨砂空白；macOS 上成了没阴影的直角矩形（Windows 的 DWM 什么都不加，所以正常）。
- **做法**：统一入口 `Views/WindowChrome.cs`（构造时 `WindowChrome.Apply(window, Main / Dialog / Tool)`，按平台设装饰并挂样式类）+ `Themes/WindowChrome.axaml`。Windows 不变；macOS 用 `Full` / `BorderOnly` + 扩展客户区拿原生红绿灯、圆角与阴影；原生 Wayland 用 `BorderOnly` + `VelaWaylandWindowDecorations`，经 `set_window_geometry` 告诉合成器真实范围；X11 一律不透明直角。XAML 里写死的外框属性全删（本地值压过样式）；标题栏统一 28（设置窗口与消息框例外）。
- Windows 不用扩展客户区：`75800523` 试过失败（托管装饰重复画标题与按钮、按钮点不动、`BorderOnly` 丢 `WS_CAPTION`）。⚠️ 试点在 WSLg（实为 X11、无透明）看似无效而撤回，macOS 实机测可以才推广到全部窗口，定为新窗口的默认做法。剩余实机验收见 feature-plan.md。

## ✅ 119. 2026-09-26 状态栏：一排条目对齐到同一条中线（用户反馈）

**根因**：`TextBlock` 居中的是行框，行框留白取整行字体的最大值，混进中文（回退到 CJK 字体）整行基线就沉下去；内容区高 23 而控件是偶数高，居中落在半像素上。新增 `Controls/CapCenteredTextBlock`：按主字体「H」的高度把基线对到中线并对齐物理像素（走 `RenderTransform`，不影响布局），与字体回退无关；尺寸一律取奇数。

## ✅ 120. 2026-09-26 标题栏：X Server 按钮挪到功能按钮组最后（用户需求）

按用户要求把 X Server 按钮挪到标题栏功能按钮组最后（紧挨最小化）；只改 `TitleBarView.axaml` 的顺序。文档：velashell-docs `host/交互与界面规格.md` §4A.2。

## ✅ 121. 2026-09-26 CI：Windows 上反复出现的 IDE0055 —— 工作区一律签出成 LF（用户反馈）

PR #516 的 windows-latest 报 4 条 IDE0055，ubuntu 通过，本地不报。**根因**：`.gitattributes` 只有 `* text=auto`，Git for Windows 默认 `core.autocrlf=true` 签出 CRLF，而 `.editorconfig` 是 `end_of_line = lf`；Roslyn 在要重建空白的位置（`=>` 与表达式体之间夹注释）按 `end_of_line` 写换行，CRLF 就报。本地不报是 IDE 早把那几行「修」成了 LF。
**做法**：`.gitattributes` 改 `* text=auto eol=lf`；`*.ps1` 等写 `!eol`（带 shebang 的脚本在 Linux 上 CRLF 跑不了），`*.cmd` / `*.bat` 定 CRLF；`CONTRIBUTING*.md` 补一句旧工作区要重新签出。

## ✅ 122. 2026-09-26 CI：修掉 PR #516 上剩下的两条红用例（用户反馈）

都与行尾无关，只改测试：macOS 上 `TunnelPanelUiTests` 写死了 §118 之前的 `WindowDecorations.None`，改按 `WindowChrome.PlatformOf(dialog)` 取期望值；Windows 上 `StatusBarViewModelTests` 的运行时长用例靠真实时钟、runner 忙就红，改注入 `ISequencer`、用 `VirtualClock` 手动推进。

## ✅ 123. 2026-09-26 复制被自动换行折开的长行，粘出来成了好几行（#517）

**根因**：引擎自动换行时本就给行打了 `TerminalRow.Wrapped`（改列宽重排靠它），但 `VelaTerminalControl.AppendSpanText` 不看，每个物理行后都补 `\n`；双击选词、「保存输出到文件」（`GetBufferText`）同样只按物理行走。
**做法**：三处都按逻辑行走：线性选区遇 `Wrapped` 行接着拼；折行处显式写入的空格是行中间的内容，不受「去除尾部空格」影响；宽字符挪到下一行后留下的末列空位不当成空格；块选照旧逐行断开；双击选词沿软换行向上 / 向下找。

## ✅ 124. 2026-09-26 VelaShell.XServer:全库审查的 31 项逐项修掉(用户需求)

§114 登记的 31 项(A–E 五组)全部修掉,一项一提交,每项配用例、先撤修复确认用例会红;守净室规程。
- **A 卡死与打垮进程**:窗口嵌套限 256 层、每客户端 32768 个,销毁与重画改显式栈;映射只从那棵子树重画(原先 O(d²),256 层的链逐个映射要 8 秒);`DestroyPointerBarrier` 只认指针屏障(原先一个请求就能删掉根窗口);`Region` 按 y 分带线性归并并设块数上限;GLX 的 CallList 计入执行预算、几类绘制参数封顶或裁剪。
- **B 内存**:未执行请求按字节计(每客户端 32 MB),像素缓冲、属性值、原子、NotifyMSC、GLX 资源全部封顶;XTEST 的 FakeInput 延迟期间挂起该客户端(协议语义,原先每条一个 `Task.Delay`、按下与松开还会乱序)。
- **C 访问控制**:内置服务端每次启动生成 MIT-MAGIC-COOKIE-1 写进 `.Xauthority`(`XAuthorityFile`,按 xauth 的锁文件约定,停止时只撤自己那一条);Unix 套接字 listen 前设 0600、按 SO_PEERCRED 认本用户;MIT-SHM 重核权限、SendEvent 只放行已登记的事件、日志限速封顶。⚠️ 本机 X 程序经环回 TCP 连进来现在要带 cookie(Xlib 从 `.Xauthority` 自动带,读不到的连不进来)。
- **D 正确性**:抓取 / 焦点 / crossing 事件按协议补齐;宿主按按钮逐个记按下状态,失去捕获 / 失活时替 X 松开;`FocusTopLevel` 按 ICCCM 输入模型、实现 WM_TAKE_FOCUS;冻结期间设备事件进有上限的队列按序回放。⚠️ XFIXES 多了一个错误码,之后各扩展的错误码顺延一位(客户端经 QueryExtension 取号不受影响,测试里写死的号要跟着改)。
- **E 性能**:RENDER 在 8888 目标上整数合成(ARGB + a8 遮罩 3.8k → 19.3k 次 / 秒);GLX 单缓冲只拷画过的外接矩形(也不再盖掉窗口里别处 X 画的内容);请求缓冲池化、回复按线程复用写入器(整窗 PutImage CPU 少四成)。⚠️ 这台机器上进程内基准是双峰的(同一份代码能差 1.5 倍,像是线程落在大小核上),前后比较各跑两遍以上。
- 当时全量 4800 条:4761 通过 / 1 失败 / 38 跳过,失败的是 X11 靶机用例 `X11_RefusedByServer_KeepsAgentForwardingAndWarnsOnce`(本机靶机镜像旧,`main` 上同样失败,同 §116)。⚠️ 改测试前先按 CI 的 `dotnet build VelaShell.slnx -c Debug -warnaserror` 构建,否则测试工程的警告到 CI 上才报成错误。

文档:velashell-docs#71(`xserver/design/architecture.md` §4、§5、§7、§10,补上 §114 欠的「所有 SSH 会话共享一个受信的显示」提醒)。

## ✅ 125. 2026-09-27 SSH PTY 像素尺寸贯通到 `window-change`（`feature-plan.md` 🟢 P3 项）

`window-change` 的像素字段恒为 0：库早就照发 `SshTerminalSize` 四个字段，卡在宿主的 `PtySizeChanged` / `Resize` 只带行列；sixel / kitty 图形与按像素排版的 TUI 靠 `ws_xpixel / ws_ypixel` 换算单元格尺寸。
- **做法**：`VelaShell.Core.Ssh` 新增 `PtySize`（行列 + 物理像素，0 = 不知道），`ITerminalEmulator` 加 `CurrentPtySize`；SSH 实现带上像素（负数按 0 发：库在构造时会拒绝负数，而这条路即发即忘），ConPTY 与插件流只取行列。
- **口径**：像素 = 单元格尺寸（DIP）× 行列 × 显示缩放，只算网格本身，不含内边距、侧栏与滚动条（远端拿它除以行列，多算的边就成了误差）。只有像素变了也补报一次（改字号而行列不变、拖到缩放不同的显示器），没变不重复报；`pty-req` 时还没布局、像素为 0，挂流时补推一次覆盖。
- **不动插件 SDK**：插件视图的 `Resized` 仍是 `Action<int, int>`，经一个具名方法转一道。⚠️ 每次现包 lambda 的话 `-=` 永远摘不掉。

文档：velashell-docs#72（中英镜像的 `host/architecture.md` §9）。

## ✅ 126. 2026-09-27 CI：修掉 PR #522 上两条随调度红的用例（CI 反馈）

两条都与 §125 无关，只改测试：Linux 上 `FtpSessionStatusTests` 在圆点变绿后立刻拆服务器再刷新，首次列目录常常还没做完，刷新先取消上一次导航（被取消的那次不报错），红不红看调度，改为先等 `InitialLoadTask`；macOS 上 `MiscExtensionTests` 的 XTEST 延迟用例只留 100 毫秒余量，延迟改为 1 秒。

## ✅ 127. 2026-09-27 标签条：自动宽度、固定标签页、多行显示（#521）

标签一多只能靠 `◀ ▶` 与下拉翻找，宽度钉死在标题原宽上；用户要求参考 Visual Studio。
- **自动宽度**：排版面板换成 `DockTabPanel`：放不下先把**最宽的那几个**削到同一宽度（VS Code 的 `tabSizing: shrink`，短标签不动），下限 120px，到下限仍放不下才溢出滚动。⚠️ 单排时外层 ScrollViewer 给的是无穷宽，面板改盯视口宽度；封顶宽度取整到整像素，免得小数累加超出半像素把溢出按钮闪出来。
- **多行显示**：`DockWorkspace.MultiRowTabs`，整个工作区一个开关，只对顶置标签条生效；入口是标签右键菜单与 设置 → 外观 → 窗口（`AppearanceOptions.MultiRowTabs`，默认关）。⚠️ 菜单开在弹出层里、数据上下文是文档，绑不到工作区，改由 `DockTabItemBase` 打开菜单时按名字找到这一项填状态。
- **固定标签页**：`DockDocument.IsPinned`（改顺序归 `DockWorkspace.SetPinned`），固定的永远排在组最前，移动落点由 `ClampInsertIndex` 收进合法区间，「连接中」占位换真标签时交接固定状态。批量关闭绕开它、中键关不掉，冲着它本人的「关闭」与 `Ctrl+W` 照常；× 换成图钉。与原 Dock 的 Pin（钉到侧边自动隐藏，产品红线）不是一回事，那一条仍不做。固定状态与分屏布局一样不落盘，只活在本次运行里（剩余项见 feature-plan.md）。
- 五种标签的右键菜单补齐「关闭其他 / 所有 / 左侧 / 右侧」；`DESIGN.md` 早写着「`ScrollIntoView` on activation」却没实现，现在激活标签在下一次布局回调里 `BringIntoView`。

文档：velashell-docs#73（中英镜像的 交互与界面规格 §4B、dock-replacement-plan、settings-audit、快捷键参考）。

## ✅ 128. 2026-09-28 SFTP 双栏支持两侧都是远程(#524)

两台服务器之间搬文件原先要先落一次本机盘;同会话「复制到」也经临时文件。
- **入口**:资源管理器 Ctrl(macOS ⌘)单击组成「双选」,恰好两条、先选的在左,第三条顶掉最早那条;列表控件仍是单选,双选由 `SessionTreeViewModel.DualSelection` 自己记,Ctrl 单击在行的 `PointerPressed` 里置 `Handled`。右键双选里的行在**按下**时换成双选菜单,原菜单一项没改。
- **连接与文档**:`OpenDualSftpDocumentAsync` 依次连两条,**任一条没连上就整体回滚**;`DualSftpDocument` 两栏各一个 `FileBrowserViewModel`(基类 `SftpDocumentBase`),两条连接归文档所有、关闭一起断,状态灯取较差的一条;启动恢复会话不恢复双栏。
- **中转**:`ISftpService.UploadStreamAsync` + `Core/Sftp/RemoteRelay`(先 stat 源再开读流喂给目标),`TransferType.Relay` 走原有传输管线,冲突检查同上传。⚠️ **源在开始写之前就读不到时不清理目标** —— 否则「清理半截文件」会删掉用户原有的同名文件。
- **取消不再被报成「中断,可续传」**:`VelaSftpClientWrapper.UploadAsync` 用 `await using` 包远端流,取消时在途 WRITE 被记成写入失败、关流异常把真正的取消顶掉;改为照发 CLOSE、吞掉关流异常、如实抛 `OperationCanceledException`。⚠️ 这条路径没有自动化用例。
- **中转断点续传（本节当时的实现）**:口径同上传,源须可 Seek（当时仅 SFTP 源传续传点）,尾部对不上抛 `VelaSftpResumeMismatchException`;FTP 目标交给 FluentFTP 的 Resume。半截文件与上传一致(开着续传就保留)。2026-10-28 的 FTP/FTPS 完整缓存也可 seek,可参与目标尾校验续传,但不保证所有目标支持,见 §276。
- **比较目录**:两栏当前层按大小与修改时间比较(FTP 按推断出的时间精度);不做 SHA-256,也不提供同步窗口(那套按「本地 ↔ 远端」设计,剩余项见 feature-plan.md)。
- **否决在 A 上 `scp` / `rsync` 直连 B**:要在 A 上持有 B 的凭据或转发 agent、A 要能直连 B、B 的指纹要在 A 上被信任,失败原因多在第三台机器上,用户无从排查。
- 插件协议这次挡在外;SDK 新增可选接口 `IProtocolStreamUpload`(只增不改,`apiLevel` 不变,velashell-plugin-sdk#28),宿主接入见 §129。

文档:velashell-docs#74(中英镜像的 交互与界面规格 §3 / §6.2 / §12、SFTP 双栏与 WinSCP 差距分析第八节、sdk-reference 版本表)。

## ✅ 129. 2026-09-28 插件的文件协议进双栏远程、插件流式上传接线(#524 后续,用户需求)

SDK 2.0.6 发布了 `IProtocolStreamUpload`,S3 这类对象存储与 SFTP 服务器之间互相倒文件是正经场景。
- **插件流式上传**:`PluginProtocolFileService.UploadStreamAsync` 在协议兼实现了该接口时转调,否则照旧报不支持;流式下载本来就有(`OpenReadAsync`)。
- **进双栏**:树只认得连接类型,宿主经 `SessionTreeViewModel.DualSftpFilter` 补判(排掉工作台,不装载插件);终端协议(Telnet…)要激活后才认得出,打开时报「不是文件协议」并回滚另一条。没实现流式上传的插件栏只能作源:往它那边的复制按钮置灰、拖放不接,代码调用报「不能接收」而不是静默。
- **修 §128 留下的静默覆盖**:续传探测看到「目标比源短」就跳过同名冲突询问,源回不了头(FTP 的数据连接)时 `RemoteRelay` 又悄悄整份重传 —— 那个短文件若是用户自己的另一个同名文件就被覆盖了。改为源不可 Seek 时抛 `VelaSftpResumeMismatchException` 交回同名冲突策略;插件目标被要求续传时(SDK 的流式上传不续传)同样处理。

文档:velashell-docs#75(中英镜像的 交互与界面规格 §3 / §6.2、差距分析 8.2、sdk-reference 填上 2.0.6)。

## ✅ 130. 2026-09-29 启动时窗口状态「记住上次」也记住窗口位置(#529)

原先只记宽高与是否最大化,位置交给 `CenterScreen`。
- `AppearanceOptions.LastWindowX` / `LastWindowY`(`int?`,屏幕物理像素,`null` = 没记过);与宽高一样是设备本地字段(Gist 同步推送前清空、拉取时保留本机值)。
- `Services/MainWindowPlacement` 启动时先核实位置还在某块屏幕的工作区里(标题栏顶边在内、下面留得出 32、横向至少重叠 120 逻辑像素),核实不了照旧居中 —— 拔掉副屏后不会开在屏幕外拖不回来。位置先于最大化摆好,于是最大化到上次那块屏。
- ⚠️ 关闭那一刻常不在普通态(最大化时 `Position` 是最大化后的左上角,最小化时 Win32 报 (-32000, -32000))。Windows 上位置 / 尺寸 / 状态一变就**投递**一次快照、回调里仍是普通态才记 —— 最大化那次的移动事件到达时状态可能还没改,同步读会记错。X11 与 macOS 保证不了这个顺序,只在普通态下关闭时读当下的值。顺手修:从最小化关闭原先把「最大化」记成 false,现按最小化之前的状态记。

macOS / Linux 没实机跑过(剩余项见 feature-plan.md)。

## ✅ 131. 2026-09-29 出厂强调色改为跟随主题(`feature-plan.md` 🟡 P2 项)

`AppSettings.AccentColor` 出厂是 `#E91E63`,而强调色覆盖优先于主题令牌,全新安装下十二套主题各自的强调色都被同一个粉色盖住(§27 记过)。出厂值改为空串(空 = 不覆盖、跟随主题),运行时一行不用动;**存量配置不迁移**(已落盘的 `#E91E63` 分不清是用户选的还是旧出厂值)。设置 → 外观 → 主题色加「跟随主题」按钮,输入框占位也写「跟随主题」。

## ✅ 132. 2026-09-29 快捷命令支持变量占位(用户需求)

`kubectl logs -f <pod>` 这类每次只差一两个参数的命令,原先只能存半截或每个参数各存一条。
- **写法**:`{{名字}}` 或 `{{名字=默认值}}` 写在命令正文里,`QuickCommand` 结构不动(同步与导入导出照旧);解析在 `Core/Models/QuickCommandTemplate`。
- ⚠️ **别误伤现有的双花括号**(`docker inspect -f '{{.State.Status}}'`、go-template、Ansible 的 `{{ inventory_hostname }}`):名字只能是字母 / 下划线开头的字母数字下划线连字符,**花括号里不许有空白**,Go 模板的 `end` / `else` / `break` / `continue` 不算;不是占位的一律原样发出。
- 同名只问一次、取第一个非空默认值。**值里的换行换成空格** —— 快捷命令不带回车,值里夹换行就等于替用户按了回车。弹框取消则一个字节都不发;弹框期间目标可能断开,发送时按当下的标签再挑一遍。
- 命令补全插入的仍是含占位的原文 —— 补全按前缀续写,换成替换后的文本会与已键入的前缀对不上。

## ✅ 133. 2026-09-29 keyboard-interactive 动态码弹框(`feature-plan.md` 🟠 P1 项)

只放行 keyboard-interactive 的 2FA 服务器(PAM + Google Authenticator、Duo、堡垒机 MFA)原先完全登不上,宿主没有弹框流程。⚠️ 库自带的兼答只看形状(一条不回显提示)不看内容,PAM 的 `Verification code:` 也是这个形状 —— 密码会被填进验证码那一轮。
- **结构**:Core 新增 `IKeyboardInteractivePrompt`(与 `IHostKeyPrompt` 同一模式),界面层 `Views/KeyboardInteractivePromptDialog` 弹框(一次只弹一个,第一行是连接目标);应答在 `Infrastructure/Ssh/KeyboardInteractiveResponder`(每次连接尝试一个)。有界面时每种凭据后面都跟一条 keyboard-interactive、密码凭据的 `AlsoAnswerKeyboardInteractive` 关掉;没有界面时与原先一样。
- **应答规则**:单条不回显、看起来是口令(含「密码」字样且不带验证码 / OTP / token —— 那要的往往是「口令 + 动态码」拼起来的串)用已有密码**代答一次**,再问就交给用户;其余一律弹框。**私钥 / 证书 / agent 不回退到口令**:口令提示答空串让服务端拒掉,只有验证码才弹框(钥 + 动态码的第二步)。纯展示轮不弹框;对端文字先清洗、限长。
- **取消** = 「不连了」:库会把它记成「凭据取不到材料」或「认证超时」,所以不改库,应答器记下 `Cancelled`,由装配器改抛 `VelaSshAuthenticationCancelledException`(刻意不派生自 `OperationCanceledException`,否则会被改判成超时)。首连撤标签不报错,重连按用户主动断开(不然自动重连几秒后又弹同一个框)。
- ⚠️ `SshAgentKeyLoader.TryGetKeyToAdd` 原先按「凭据恰好一条」匹配,多一条后「自动加钥到 agent」会静默失效,改为只看第一条。
- 测试靶机 `ssh-2fa`(端口 2224,PAM 先口令后 TOTP)。⚠️ 宽松的服务器上兼答没关也看不出来(先失败一次再由应答器连上),要靠 `MaxAuthTries 1` 的 `vela-strict` 账户。

没在真实的 Duo / RSA SecurID 服务器上试过(剩余项见 feature-plan.md)。

## ✅ 134. 2026-09-29 算法协商可配:老算法开关 + 自定义算法清单(`feature-plan.md` 🟡 P2 项)

连老设备(只剩 `diffie-hellman-group14-sha1`、`ssh-rsa`、`hmac-sha1`)谈不成,库早有 `SshAlgorithmSet.WithLegacyInterop()`,宿主却无处放开;要精确控制的(关掉 chacha20-poly1305 / EtM 缓解 Terrapin)也没入口。
- `SshSessionOptions` 新增 `LegacyAlgorithms` 与 `KexAlgorithms` / `HostKeyAlgorithms` / `Ciphers` / `Macs`(空 = 默认),跳板链每一跳各带各的。连接对话框「SSH 连接选项」里一个开关 + 四个输入框,写错时字段下报错、保存 / 连接 / 测试灰掉。
- **写法沿用 OpenSSH `ssh_config`**:`+` 追加、`-` 删除(可带通配)、`^` 提前,不带前缀整个替换;「默认」指放开老算法之后的那一份。照 `~/.ssh/config` 抄过来就能用。
- **`Infrastructure/Ssh/SshAlgorithmPreferences`**:能写哪些名字以库实际实现的为准,不在宿主另抄一份;OpenSSH 认得、本版没实现的(CBC、3des、group1、ssh-dss…)报「本版没有实现」而不是「不认识」—— 抄来的配置里最常见的就是 CBC,用户要知道的是放开也没用。⚠️ 不带通配的删除项也要认得(拼错的删除项什么都删不掉,用户却以为关了)。
- 协商失败的诊断多一行下一步:对端有本版实现了、只是没放开的就点名并指到连接配置。测试靶机 `ssh-legacy`(端口 2225)。CBC 本库没实现,只剩 CBC 的设备仍连不上;`~/.ssh/config` 导入(§58)不映射算法清单(剩余项见 feature-plan.md)。

## ✅ 135. 2026-09-29 审计日志查看界面与保留策略(`feature-plan.md` 🟠 P1 两项)

`audit_log` 一直在写,`IAuditLogService.QueryAsync` 在界面层却零调用;它和 `conn_history` 两张时序表又只增不减。
- **查看**:设置 → 安全审计 →「查看」打开 `Views/AuditLogView`(非模态)。一次取最近 2000 条,类别、「只看异常」、关键字三者叠加在本地筛;动作翻成人话,连接失败、拒绝与接受了变更的指纹算「异常」标红;读库失败写进摘要不抛。
- **保留**:`SecurityOptions.AuditLogRetentionDays`(默认 180 天,1–3650),审计日志与连接历史共用 —— 连接历史是「最近连接」的底账,比审计留得久没有意义,留得短又会让审计里的会话在侧栏对不上。启动时 `AuditRetention.PruneAsync` 删掉更早的记录。
- **没照搬录制那套「暂存 → drop 重建 → 回灌」**:实测 SonnetDB 的 `DELETE … WHERE time < @cutoff` 按时间删成立;审计是逐条短文本,墓碑不值得搬一遍数据,而重建在进程死在中途时会丢掉保留期内的记录,审计最不该冒这个险。方言不支持时原样保留。
- 导出、按时间段筛与更多事件类型还没做(剩余项见 feature-plan.md)。

## ✅ 136. 2026-09-29 回放中心与资源监视的动作收进标题栏(用户需求)

回放中心与资源监视原先在标题栏下另起一行放文字按钮 —— 标题栏图标按钮的主题定义在 `TitleBarView` 内部,别的窗口取不到。
- 主题挪进 `Themes/ButtonThemes.axaml`,改名 `VelaTitleActionButtonTheme`。回放中心四个动作(导出 / 刷新 / 清理 / 自动录制)改纯图标进标题栏动作组,悬停提示两行(动作名 + 说明),自动录制开着时图标转强调色;资源监视的主机标识(限宽 260 截断)与暂停 / 继续收进标题栏。窗口键换 27×27 图标方块贴右上角。
- ⚠️ 标题栏的 `DoubleTapped` 不管按钮有没有处理按下都会冒泡上来,连点两下刷新就把窗口最大化;改为落在按钮上的双击不算(审计日志窗口一并改)。
- `DESIGN.md` §4.2 改为「窗口级动作放标题栏的动作图标组,状态标识可以排在它前面,只有副标题放下一行」,§5.1 补标题栏图标按钮这第三种角色。

## ✅ 137. 2026-09-29 远程编辑与连接诊断的动作也收进标题栏(用户需求,§136 后续)

远程编辑的保存、连接诊断的导出报告 / 重新检测收进标题栏动作组,窗口键换 27×27 方块(连接诊断是对话框,不挂 `window-caption`,macOS 上照常显示 ×)。保存原是常亮的强调色实心按钮,变成灰图标后没人提醒该存 —— **有未保存的改动时图标转强调色**(按 `IsDirty` 挂 / 摘 `dirty` 类);`DESIGN.md` §4.2 记为通则。⚠️ 图标上不能写本地 `Foreground`:本地值压过样式,`dirty` 那条就再也亮不起来。

## ✅ 138. 2026-09-29 导入会话、新建连接两个对话框的标题栏按 28 高重排;X11「受信任」对齐(用户需求)

- 标题栏统一 28 高(§118)后这两个对话框还是 48 高那套;改成连接诊断(§137)同一规格:15px Lucide 线条图标、13px 标题、27×27 关闭键贴右上角(`DialogTitleBarAssert` 守着)。
- X11「受信任(-Y)」原先靠「贴底 + 底边距 8」凑输入框中线,高了一截;改两行网格、同行各自垂直居中。
- 导入会话的空行:空文字的 `TextBlock` 照样占一行高;路径行改为只在「自定义模式 **且** 路径非空」时显示。
- ⚠️ 一条新用例写成 `Dispatch(async () => …)`,拿到没人等的 `Task<Task>`,变异下照样绿才发现是假绿;整套跑下来又卡死,由此引出 §139。

## ✅ 139. 2026-09-29 headless UI 测试:await 写法把整套卡死;13 处假绿转真(§138 收尾时发现)

**卡死**:**只要 `async Task` 用例里直接 `await _session.Dispatch(…)` 排在 `Dispatch(…).GetAwaiter().GetResult()` 同步写法前面就必卡** —— 会话在 UI 线程上 `TrySetResult`,await 的续体就地跑在 UI 线程上,MSTest 顺势在 UI 线程上开跑下一条;下一条同步写法在 UI 线程上等 UI 线程,整套卡死。此前跑得完只是执行顺序恰好没让两种写法相邻。
**假绿**:无返回值的 async lambda 绑到 `Dispatch<Task>`,拿到没人 await 的 `Task<Task>`,第一个 await 之后的断言全丢(`AGENTS.md` 写过但没拦住)。这次查出 7 个文件 13 处;§70 起登记的 11 处这样写的用例,直到这里才改完(复核见 §148)。
- 新增 `tests/VelaShell.Tests/TestSupport/HeadlessUi.cs` 的 `RunOnUiAsync`(`Func<Task>` / `Action` 两个重载):内部走带返回值的重载真正等完用例体,再经 `ContinueWith(…, TaskScheduler.Default)` 转到线程池,续体不落在 UI 线程。21 处 `await _session.Dispatch(…)` 改用它,6 处无返回值 lambda 补 `return true;`。⚠️ `ConfigureAwait(ConfigureAwaitOptions.ForceYielding)` 治不了:它只管「任务已经完成」的情形,第一版就这么写,照样卡死。
- ⚠️ 转真后暴露两条 7 月 20 日起没真跑过的用例:b9d73914 把 `CloseSftpDocumentAsync` 改名为 `CloseSftpDocumentCoreAsync`,`StandaloneSftpDocumentBehaviorTests` 还靠反射取旧名、拿到 null 后卡在 `closeStarted` 上,被假绿盖住。改为取不到当场抛 `MissingMethodException`、`closeStarted` 加 10 秒上限;「回到 UI 线程更新」那条按 §39 之后的会话账本设计先 `TrackDocumentSession` 登记,并临时把 `RxSchedulers.MainThreadScheduler` 换成 `AvaloniaScheduler` —— 本程序集 ModuleInit 设的是就地执行,验不出「回到 UI 线程」。
- ⚠️ 用户的 VelaShell 开着会锁住 `bin/Debug`,编译与测试改输出到 `artifacts/qa-testout`。

## ✅ 140. 2026-09-29 新建连接、导入会话标题栏与回放中心同一个样子;关闭键悬停的红底不再伸出圆角(用户反馈)

§137 / §138 换成 27×27 贴角关闭键后,方形悬停红底伸出卡片的圆角 8;原先的 × 碰不到圆角,这三张卡片一直没写 `ClipToBounds`。补 `ClipToBounds="True"`,`DESIGN.md` §4.2 写明「窗口键贴角的窗口,卡片要 `ClipToBounds`」,`WindowChromeCoverageTests` 扫 XAML 守着。两个对话框标题栏照回放中心(标题靠左,**保留标题前 15px 线条图标**);问过用户只要外观一致,仍是模态、固定尺寸,外框仍是 `Dialog`。⚠️ 第一版把图标也去掉了(没单独确认),用户指出后加回。

## ✅ 141. 2026-09-29 界面语言出厂跟随系统,对不上用英文(AppImage 目录收录要求)

出厂 `zh-CN` 让非中文环境全新安装也是中文,卡住 AppImage 目录收录 PR([AppImage/appimage.github.io#8573](https://github.com/AppImage/appimage.github.io/pull/8573))。
- `AppSettings.Language` 出厂改空串(= 跟随系统):`LocalizationService` 沿父文化链在五种界面语言里找,中文按书写体系归简 / 繁(zh-SG → 简体,zh-HK / zh-MO → 繁体),对不上(德语、法语、`LANG=C` 的不变文化……)用英文。系统语言由 .NET 取:Windows 是显示语言,Linux 依次看 `LC_ALL` / `LC_MESSAGES` / `LANG`。
- ⚠️ 系统界面文化在服务构造时快照 —— `SetLanguage` 会改写环境文化,现读 `CurrentUICulture` 的话从「日本語」切回「跟随系统」就停在日语。
- 用户选过的优先;**存量配置不迁移**(已落盘的 `zh-CN` 分不清是用户选的还是旧出厂值,同 §131 的强调色)。语言下拉首项加「跟随系统」。

## ✅ 142. 2026-09-29 切换界面语言后仍是中文的几处跟着换(用户反馈,§141 后续)

两类根因:**XAML 用 `{x:Static res:Strings.X}`**,加载时取一次就定住(主窗口只加载一次)—— 9 个视图 40 处改成 `{loc:Localize 键名}`,`LocalizedKeyUsageTests` 扫 XAML 拦 `x:Static`;**C# 侧拼好存着的文案**(命令标题 / 分类、X Server 提示、会话树计算属性、「未分组」、状态栏)—— `MainWindowViewModel` 换语言时 `RefreshLocalizedText()` 重新注册命令(按 id 替换、顺序不变)并刷新各处。按需打开的窗口每次现取文案,不在此列。

## ✅ 143. 2026-09-29 排查其余不随语言变的文案(用户要求,§142 后续)

全库排查又找出写死的中文(连接诊断步骤与整份报告、捐赠页、云同步、路由追踪等)与拼好不重算的文案(托盘、终端行号栏菜单、消息中心、隧道面板等),新增 19 个键补齐。云同步设备名改走 `DeviceNameConverter`(绑定属性里套不了 {loc:Localize});⚠️ 捐赠页「已复制」要用 `SetCurrentValue`,否则会把按钮上 {loc:Localize} 的绑定顶掉。
- ⚠️ 终端行号栏右键菜单永远慢一拍:它在设置保存时下发,而保存先于换语言 —— `SettingsViewModel.SaveAsync` 改为先换语言再落盘。
- ⚠️ 换语言重建下拉条目时 ComboBox 会把选中项清成 -1 推回来,主题被当成「回到默认」、X Server 显示号被改回「自动」;两处下拉索引忽略 -1。
- 不在此列:调试日志、匹配远端输出的中文关键词、调用方会捕获的内部异常;SSH / X 服务端库的中文诊断文本(剩余项见 feature-plan.md)。守门:`Xaml_HasNoHardcodedChineseText` / `Code_HasNoHardcodedChineseText`(匹配远端输出的文件列白名单附理由)。
- ⚠️ 往 resx 按字母序插键时,zh-Hans / zh-Hant 文件头样例注释里有 `<data name="Name1">`,16 个键被插进了注释 —— 编译不报错,靠 `AllCultures_HaveIdenticalKeySets` 才发现。

## ✅ 144. 2026-09-30 远端文件改属主 / 属组(chown)(`feature-plan.md` 🟢 P3 项)

属性弹窗的属主 / 属组可改,**只有 SFTP 支持**;不递归、不批量(与 chmod 一致),符号链接改的是指向的对象。
- `ISftpService.SetOwnerAsync`(名称或十进制 id,null = 不改)与 `GetOwnerChoicesAsync`(null = 改不了,弹窗照旧只读)。名称翻 id 复用 `RemoteIdentityResolver` 的 `getent` 查表(同 id 的别名如 BSD 的 `toor` 也认),顺序与 `chown` 命令一致:先当名称、再当数字,都不是就不发请求;纯 SFTP 账号查不到表时候选为空,仍可填数字。FTP 没有标准命令、插件 `IProtocolFileSystem` 没这一面,`SetOwnerAsync` 抛 `NotSupportedException`。
- ⚠️ SFTP v3 的 uid 与 gid 共用一个标志位:只改一项时先 stat 取回另一项一并 setstat;服务端没报 uid/gid 就不写 —— 拿不到的一项会被当成 0,等于把文件交给 root。只用库的公开 `SetAttributesAsync`,`src/VelaShell.Ssh` 没动。
- **先 chown 再 chmod**:属主最容易被拒(非 root 只能把属组改成自己所在的组),放前面,被拒时什么都还没改;chmod 被拒时照样刷新,错误在刷新之后再写。

## ✅ 145. 2026-09-30 新建连接对话框改为「左侧协议栏 + 右侧分页表单」(用户需求,设计稿 `VelaShell-zh.pen`「新建连接 v2」)

原先 508 宽一列到底,「高级选项」开关管着其余一切:SSH 展开三十来行,跳板主机这种决定连不连得上的项也藏在折叠区;横排协议页签装不下插件协议。
- 卡片 760×664 定高,左 188 宽协议栏(「内置」SSH / SFTP / FTP +「插件」一组,取插件清单声明的协议、不装载程序集;底部「获取更多协议…」打开插件管理器,启用的协议当场补进),右侧分页 `ConnectionProfileSection`(常规 / 终端 / SSH 选项 / 转发 / 高级),只有表单区滚动;矮屏照旧按 `min(768, 工作区 − 48)` 钳高。
- 终端与转发只对 SSH,SSH 选项对 SSH 与 SFTP,高级对 FTP 与声明了 `IsAdvanced` 字段的插件协议。换协议后当前页不存在就落回常规(`RefreshSectionAvailability`);⚠️ 切到插件协议那条路不清字段,只能靠 `ConnectionType` 变化那一处触发。
- 「高级选项」开关删除,「有非默认值就自动展开」改为页签旁的圆点。认证方式改分段按钮;页脚左边 `EndpointPreview`(`user@host:port`,IPv6 加方括号),连接改用半透明强调药丸(DESIGN.md §5.1)。说明文字换 `TextTertiary`(`TextMuted` 在 `VelaBgSurface` 上对比度不够读整段)。
- **与设计稿的出入**:FTP 加密方式沿用原位置(用户名旁);插件字段仍一行一个 —— 字段是插件声明的,宿主不知道哪两项适合并排,没压成双栏 + ⓘ。

## ✅ 146. 2026-10-01 取色器:设置页与连接对话框里挑颜色不再手敲色值(用户需求)

强调色、终端四色与连接标签颜色六处原是手敲 `#RRGGBB`,敲错时终端四色整套落回出厂色(`TerminalAppearanceMapper` 只认六位)。
- `VelaShell.Controls` 加 `ColorSpectrumPad` / `HueStrip` 与 `ColorHex`(认 `#RGB` / `#RRGGBB` / `#AARRGGBB`,不认颜色名,输出大写 `#RRGGBB`);宿主 `Controls/ColorPickerField` 是像输入框的字段 + 浮层(面板、色相条、十六进制框、色板)。色板:终端四色给当前配色方案的 ANSI 16 色,其余用主题色板;清除只给值允许为空的。
- **松手才写回**(拖动中只刷浮层预览):强调色一改要重派生全套主题令牌,终端颜色一改要重刷所有终端。十六进制框认不出的值不写进设置,框里退回当前颜色。
- ⚠️ 状态按 H / S / V 三个数保存而不是 `HsvColor`:后者把色相 360 折回 0,色相条拖到最右一松手就跳回最左。面板里的白与黑是色彩空间本身,不走令牌。

## ✅ 147. 2026-10-01 审计日志窗口的筛选条高度与别的工具窗口对齐(用户反馈)

下拉与关键字框没写尺寸,吃 Fluent 默认(最小高 32),比其它工具窗口的 28 高筛选框高。定为 28 高(下拉 12 号字 —— 11 号比例字体在亮色主题下发糊),刷新按钮换 `VelaOutlineButtonTheme`(DESIGN.md §5.1)。

## ✅ 148. 2026-10-01 「怎么改都绿」的 UI 用例:复核已修,加守门测试(`feature-plan.md` 🔴 P0 项)

`feature-plan.md` 还挂着 §70 登记的「11 条『怎么改都绿』的 UI 用例」,实际 §139 已全部改完,只是条目没删。更要紧的是 AGENTS.md 早写着这条约束却没拦住它长出 11 处 —— 没有东西在机器上守着。
- 复核:32 处 `Dispatch(async` 全部带返回值;点名的四个文件逐条在末尾插 `Assert.Fail("PROBE")`,12 条全红,用例体确实跑到底。
- **守门** `Design/HeadlessDispatchUsageTests` 扫 `tests/` 下全部 `.cs`:`Dispatch(async …)` 的用例体必须有顶层带值 `return`,不许出现 `await …Dispatch(…)`。扫描前把注释与字符串字面量抹成空白;扫描器自带 9 条正反样例,免得守门测试自己也「怎么改都绿」。`feature-plan.md` 删掉这一条。

## ✅ 149. 2026-10-01 zsh 上的注入行不再写进历史文件(`feature-plan.md` 🟡 P2 项)

条目原写「非 bash 的 shell 别注入目录上报钩子」,前提已被 `0c5ef1aa`(09-16,按 shell 分派「跟随终端目录」)推翻:zsh / dash 上钩子真在工作,多出的提示符也已由 #448 收回;**「认出 zsh 就跳过注入」等于关掉 zsh 的目录跟随,不做**。还成立的只剩历史:zsh 上注入行接的仍是 bash 的 `history -d` 前缀,整行写进 `~/.zsh_history`。
- zsh 5.9 实测:本会话历史表里那一条摘不掉(没有 `history -d`,`HIST_IGNORE_SPACE` / `zshaddhistory` 在执行前就判完)。但 `HISTORY_IGNORE` 是**写盘时**逐行比对:`ShellHistoryScrub.ZshCommand` 把 `*__vela_hist_scrub*` 追加进去(保留用户原有模式),每条注入行被自己写着的记号拦住。退出时写盘(macOS 出厂 `/etc/zshrc`)、`APPEND_HISTORY`、`INC_APPEND_HISTORY_TIME` 下注入行都不进文件;⚠️ `INC_APPEND_HISTORY` / `SHARE_HISTORY` 读完一行就写,只漏第一条注入行。配了 `HIST_IGNORE_SPACE`(oh-my-zsh 默认开)的,靠前导空格内存与文件都不留。
- `ShellHistoryScrub.For` 按种类挑前缀:zsh → zsh 那段;bash / dash / ash / 探不出 → 原 bash 那段(登录 shell 是 sh、`.profile` 里 `exec bash` 时探针只看得到 sh,只在正面认出非 bash 时才跳);fish / 非 POSIX 不接。velashell-docs 没写到注入行与命令历史,不涉及文档同步。

## ✅ 150. 2026-10-01 侧栏底部的身份名称不再压到右侧按钮(#545)

根因:那一行是横向 `StackPanel`,沿排列方向给子元素无限宽,`TextTrimming` 永远等不到触发。四种处理里用户定为尾部省略:改成 `Grid`(`Auto,*`),名称与按钮间留 8px,完整名称在悬停提示里。文档:velashell-docs 中英镜像的交互与界面规格 §4 底部用户栏。

## ✅ 151. 2026-10-03 连接对话框只让有焦点的文本框滚动表单(插件连接表单那次改动撤回后保留的两处修复)

编辑存了口令的连接时,口令框在首屏以下的话表单一打开就被拽下去。根因:文本框被程序赋值时 `TextPresenter` 发 BringIntoView,不管有没有焦点。`ConnectionProfileView` 吞掉没有键盘焦点的文本框的请求;⚠️ 要挂在表单内容上,挂在 `ScrollViewer` 本身就拦晚了(请求先到 `ScrollContentPresenter`)。另一处是 `CopyToClipboard` 的分析器抑制。两处原属 `7769181d`(PR #548,插件工作台的连接表单);MongoDB 插件改为自己管理连接后,那次改动连同 SDK 2.0.7 的连接对话框契约一并撤回(`f4e6b470`;SDK 侧 velashell-plugin-sdk `06f5800`),`VelaShell.PluginSdk*` 回到 2.0.6。

## ✅ 152. 2026-10-03 连接配置加备注,资源管理器悬停显示(#549)

- `SessionProfile.Notes`(null = 没有备注),**明文、不经 `ISecretProtector`**:不是凭据,且要在悬停提示里直接显示;也随云同步上传,框下提示口令放在身份验证里。导入器只新建配置、插件 `sessions` 能力只读,都不会冲掉已有备注。
- 连接对话框「常规」页加多行备注框(64–96 高,对话框无默认按钮,回车只换行;只剩空白的存 null);资源管理器会话行悬停显示,没备注不弹空浮层。超过 12 行或 500 字截断,不在代理对中间切 —— 提示框只限宽不限高,大段备注会弹出比侧栏还高的浮层。

## ✅ 153. 2026-10-03 编辑连接不再把置顶与最近连接时间清掉

`BuildProfile` 按界面字段**新建**一条配置再整条覆盖落盘,对话框里没有的 `IsPinned`(#474)、`LastConnectedAt`、`AutoStartTunnelIds` 没带回 —— 改个端口置顶就没了、最近连接里也掉到最后,不报任何错(做 §152 时发现)。编辑模式下这三项原样带回(隧道 id 列表存副本);至此 `SessionProfile` 每个属性在 `BuildProfile` 里都有着落。属缺陷修复,不涉及文档同步。

## ✅ 154. 2026-10-03 命令面板不再占 `Ctrl+K`(#551)

#551:nano 里按 `Ctrl+K` 剪切行,弹出的却是命令面板。⚠️ 根因:Avalonia 的 `KeyboardDevice` 在分发路由事件**之前**就匹配 KeyBindings,命中即 Handled,终端控件收不到,`^K` 永远到不了远端(nano 剪切行、shell 删到行尾一并失灵)。`Ctrl+K` 只是 `Ctrl+P` 的多余别名,所以删掉不绑,命令面板只留 `Ctrl+P`。同样被占的 `Ctrl+T` / `N` / `W` / `P` / `B` / `-` 与 `Ctrl+F` 是正经功能键位,改默认值要动用户的肌肉记忆,这次不动,交给 §155。
- 守门 `ShortcutCatalogTests.MainWindowKeyBindings_DoNotTakeNewControlCharacters`:直接问 `InputEncoder`(与真发往远端的字节同源),只按 Ctrl 就编码成单个 C0 控制字节、且不是键本身就会发的(`Ctrl+Tab` 的 `^I`)一律报红;带 Shift 的不算 —— 不带 Shift 的那一按还在。历史遗留名单**只减不增**。文档:velashell-docs#83。

## ✅ 155. 2026-10-03 快捷键可自定义:改键、解绑、恢复默认(#551,推翻「确认不做」)

§154 只删了 `Ctrl+K` 这个多余的别名,其余出厂键位照样各抢一个控制字符,而 07-10 定下的「不做自定义键位」(§10-B)让用户撞上了也无计可施;任何一套出厂键位都会撞上某个远端程序,说得通的只有让用户自己改、自己解绑。
- 目录 `ShortcutCatalog` 仍是唯一事实来源;出厂表 `Services/ShortcutKeymap.cs` 的 `ShortcutBindings`(30 条,稳定 id + 命令 id + 出厂手势 + 生效范围),每条在目录里恰好一次。跳标签拆成 8 条各自可解绑(欧洲键盘布局上 AltGr 就是 Ctrl+Alt)。
- `AppSettings.Shortcuts.Overrides` 只存改过的(**空串 = 解绑**),出厂键位日后调整时没改过的人自动跟上;同一手势只归一条:改过的压过出厂的,两条改动撞车按表序先到先得。
- `MainWindow.axaml` 写死的 29 条 `KeyBinding` 全删,由 `ApplyKeymap` 按键位表整表登记;**`MainWindow.axaml` 不许再写死 `KeyBinding`**,出厂键位也不许新占 `Ctrl+字母` 这类终端控制字符,两条都由 `ShortcutCatalogTests` 拦着。
- 改键规则(`ShortcutGestures.Check`):要带修饰键(否则只能 F1–F24),不能占复制粘贴、`^C` 等固定键位;撞车先问。会抢终端按键的行提示替代按法(如「要把 ^P 发给远端,请按 Ctrl+Shift+P」)。
- ⚠️ `KeyboardShortcutService` 不再映射 Ctrl 版全局键位,否则解绑 `Ctrl+W` 后它照样关标签。⚠️ 带类名的样式(`TextBlock.row-desc`)会压过数据模板里直接写的 `Foreground`,提示行颜色必须走样式类。
- 设置页「快捷键参考」改名「快捷键」,可改键 / 解绑 / 恢复默认;`feature-plan.md`「确认不做」删掉自定义键位那一条。文档:velashell-docs#83(与 §154 同一个 PR)。

## ✅ 156. 2026-10-03 CI:文档仓库检出 PR 正文里引用的那个文档 PR(#554 的 CI 红)

#554 只红在 `Doc_ListsEveryCatalogEntry`:CI 检出的是 velashell-docs 默认分支,而代码 PR 按 AGENTS.md 要与文档 PR(这次是 velashell-docs#83)一起合,合并之前比对用例必然红。`ci.yml` 在检出文档仓库前加「定位文档分支」:只在 `pull_request` 上跑,从 PR 正文找 `velashell-docs#NN` / `velashell-docs/pull/NN`,第一个**开着且来自文档仓库本身**(不检出 fork)的 PR 就检出它的 head 分支,否则用默认分支(这一步 `continue-on-error`,失败也退回默认分支)。⚠️ 正文是任意文本:经环境变量传入、只抽数字、不拼进脚本。推送到 `main` 没有 PR 正文、用默认分支,所以**先合文档、再合代码**(写进 `CONTRIBUTING.md` / `CONTRIBUTING.en.md`)。

## ✅ 157. 2026-10-03 共享凭据:多条连接共用一套账号与密码 / 密钥(#550)

#550:一百台设备同一套账号,改一次密码要改一百条连接配置(对标 WindTerm 的 OneKey)。按 velashell-docs《凭据管理器集成设计》「配置存引用、连接那一刻才解析」的契约,把**本机共享凭据**做成第一个凭据来源(外部密码管理器见 feature-plan.md)。

- **契约**:`Core/Credentials/` 的 `CredentialReference`(来源 id + 条目 id,本机为 `shared`)、`ICredentialProvider` / `ICredentialResolver` / `CredentialProviderException`(消息面向用户);`SessionProfile.CredentialSource` 为 null 即在本配置中填写(老数据零迁移);新集合 `shared_credentials`,密码与口令经 `ISecretProtector` 加密。用户名:连接填了以连接为准、留空跟着凭据走(设计里的 `OverrideUsername` 开关不要了)。
- **仓储不变量**:带引用的配置,`SonnetDbSessionRepository.Protect` 落盘前一律清掉认证材料,七八条保存路径一处管住。由此**内存里带引用的配置若有材料,只可能是本次在登录框里手输的**,解析时以手输为准(否则刚输的新密码会被凭据里的旧值盖掉)。
- **解析**:`CredentialResolver` 交回填好凭据的副本、不回写原配置 —— 明文不进会话树缓存与 `tab.Profile`。FTP / 插件协议只认密码类凭据。五个构建点:SSH 的 `BuildChainAsync` **每一跳**各自解析(跳板取不到凭据报普通连接错误「跳板 X:…」—— 退回登录框问的是目标机的凭据),FTP 与插件协议在 `MainWindowViewModel` 里解析。
- **连接流程**:带引用的配置不先弹登录框;**解析失败的 `CredentialProviderException` 与认证被拒同等对待 —— 退回登录框**。框里说明原因,「记住密码」换成「同时更新共享凭据「X」(N 条连接在用)」,被拒时默认不勾(可能只是这一台密码不同)、不完整时默认勾。
- **顺带修掉设计 §3 的 D1 / D3**:登录框原先就地改会话树缓存里那份实例,手输、没让记住的密码会跟着拖动分组之类的保存落盘 —— 改为返回副本;证书信任后的保存按「记住密码」剥密码。D2 / D4 未动(剩余项见 feature-plan.md)。
- **界面**:设置 → 共享凭据(**删除时把凭据拷回每条引用它的连接**)、编辑框「勾选凭据相同的连接」(迁移用)、连接配置页「凭据来源」。⚠️ `FileBrowserView` 里写死的设置页下标 5 改为 `SelectSection(Transfer)` —— 中间插一页,写死的下标就静默错位。
- **云同步**:载荷加 `sharedCredentials`(先于连接落库;没设端到端口令时剥掉密码与口令)。**用到共享凭据时载荷版本写 3,否则仍写 2** —— 旧客户端会把不认识的引用丢掉、推回来就从云端抹了,写 3 让它照章拒收,没用这个功能的人照旧与旧版本互通。文档:velashell-docs#85。

## ✅ 158. 2026-10-03 快捷命令:内置命令可改可删、多行命令、拖动排序(#555)

#555 四条:① 28 条内置命令写死在 `QuickCommandCatalog`、不落盘,改不了也删不了;② 输入框单行,且 ⚠️ `TrySendCommandText` 只去末尾换行 —— **中间每个换行都等于按一次回车**,多行命令的前几行当场执行(云同步来的数据本就可能带换行);③ **点击后自动执行:维护者决定不做**(feature-plan.md「确认不做」);④ 没法排序。

- **覆盖层,不改目录**:删掉的内置命令记进 `QuickCommandData.HiddenBuiltInIds`,改过的存成**同标识**的自定义命令、显示时顶替目录那条(`QuickCommandCatalog.VisibleBuiltIns`,与命令补全共用);没动过的仍随界面语言换描述,新版本新增的照常出现。⚠️ 隐藏清单里不认识的标识原样留着,否则同步回去会把新版本删掉的命令放出来。有逐行「恢复默认」与整体「恢复内置命令」。
- **排序**:内置命令没有可存的 `SortOrder`,组内顺序记在分组上(`QuickCommandGroup.CommandOrder`,空表 = 默认顺序,表外的按默认顺序接在后面);「未分组」固定垫底;拖完顺手重写自定义命令的 `SortOrder` 给旧版本看。⚠️ `Normalize` 的分组比较改为按标识比、算上顺序表 —— 多报一次「有变化」就是每次启动都触发一次云同步。
- **云同步只加字段、不抬版本**:旧客户端推一次只丢偏好(删掉的内置命令重现、顺序回到默认,用户写的命令一条不丢);§157 丢了引用就连不上所以抬了版本,这里抬版本反而会让没升级的设备整次同步失败。
- **多行**:输入框改多行,`QuickCommandText.Normalize` 把换行统一成 `\n`、去首尾空白;侧栏只显示首行加「+N 行」。含换行的走括号粘贴(`WritePasteInput`);目标里有没开 ?2004 的(`ITerminalEmulator.IsBracketedPasteEnabled`,那边仍会逐行执行)先弹确认,沿用「粘贴时确认多行内容」开关。只有末尾带换行的不算多行;多行命令不进补全(接受一条多行候选等于替用户敲回车);变量占位先问,替换后整段再粘贴。
- **拖动排序**(设置 → 代码片段):六点手柄,超过 5px 才算拖,拖出列表 40px 外松手即取消。用页内指针捕获、不走系统拖放 —— 不会把内部标识拖进终端或别的程序。侧栏不加手柄(点一下就是发送)。**没有键盘替代**(剩余项见 feature-plan.md)。
- ⚠️ 删除图标用的 `VelaStatusError` 是个**不存在的令牌**,删除按钮一直透明,改成 `VelaError`。文档:velashell-docs#86。

## ✅ 159. 2026-10-03 SSH 库:agent 转发的几处修补(会话声明、连不上不挂死、没开成就不开)

把 agent 转发、压缩、端口转发搬运、X11、终端尺寸按规范与实测重过一遍,只有 agent 转发要改。

- **连不上不挂死**:Windows 上本机 agent 没跑时,连命名管道只带取消令牌、会一直重试,远端 `ssh` / `git` 挂到 shell 关掉、还占着 8 个名额之一。时限进库:`SshAgentClient` 连管道带 3 秒(`PipeConnectTimeout`),到点报新的 `SshFailureReason.AgentNotRunning`(`SSH_AUTH_SOCK` 没设、套接字不在或没人监听同样归它);宿主删掉自己那层 3 秒。
- **会话声明(`session-bind@openssh.com`)**:不做则 `ssh-add -h` 的目的地约束不成立 —— 真 OpenSSH agent(9.9p2 实测)拒绝替没声明的连接用受约束的钥签名。`CreateSessionProof()` 取首次交换的证明;认证经 `ISessionAwareSigner` 在首次签名前声明一次,转发时每条 agent 通道先声明再转发、远端那一跳自己的声明照转。`is_forwarding` 用 `SshAgentConnectionPurpose` 表达,零值是认证(填错落在「约束钥用不了」一侧)。`publickey-hostbound` 不做:本库永远是第一跳。
- **没开成就不开**:以前 agent 被拒时宿主去掉它把整个 shell 重开(`x11-req` 与 `xauth` 也重跑)。现在先试连本机 agent,连不上不发 `auth-agent-req`;服务端开回通道时在确认之前连好,连不上回 `CHANNEL_OPEN_FAILURE`(不外送本机路径)。失败策略换成 X11 与 agent 共用的 `ForwardFailureMode { Fail, Continue }`,不加「关」这一档 —— `null` 已表示不请求,再加就是两种矛盾的写法(spec/07 §7.5.8)。宿主用 `Continue`,删掉 `OpenShellWithFallbackAsync`,shell 一次开成。
- 小的两处:`ForwardAgent` 照 `ssh_config(5)` 认套接字路径与 `$环境变量`;`.Xauthority` 跳过空 cookie。
- ⚠️ `Core.Tests` 的 X11 靶机用例失败与改动无关:跑着的 `velashell-test-shells` 容器早于 Dockerfile 给 `vela-dash` 加 `X11Forwarding no` 的那次提交(§161、§164 同)。
- 文档:velashell-docs 中英 spec/07 / 04 / 05 / 08 / 09、architecture.md §5.10 与 §11.2.25。

## ✅ 160. 2026-10-05 SSH 库:连接计时器停表时预算已经用完,当场判超时,不再去问

净室对照(做法同 architecture.md §11.2.23)建连等人时的计时与重协商的主机密钥验法,多数本库已有。**根因**只有一处:`SshConnectDeadline` 借 `CancelAfter` 计时,停表时只看令牌取消没有;到点回调还排在线程池里时停表,计时器按剩 0 冻住,指纹照样拿去问用户,点完「信任」一续表就报密钥交换超时。改用自己的 `ITimer`(经 `TimeProvider`):停表按实际用时结账,用完(含刚好用完)当场判超时,停表期间才跑到的回调不作数;`ApplyHostKeyPolicyAsync` 停表后先看令牌,到点就不再去问。考虑过不做:「能不能交互」标志、认证期间停表、重协商换另一把受信任的钥。文档:velashell-docs spec/03 §5.3、spec/09 §2.4、architecture.md §11.2.26。

## ✅ 161. 2026-10-05 ~ 10-06 SSH 库:按全库审查的结论逐条修复与补齐(`ssh_plan.md`)

2026-10-05 对 `src/VelaShell.Ssh` 做分领域的全库审查(六个只读会话分头读、主会话逐条复核,守净室规程),结论写成仓库根未入库的 `ssh_plan.md`:164 条发现、第七节宿主绕路 W1–W10、第八节质疑决策 Q1–Q12、第九节测试缺口 T1–T14、第十节功能 F1–F42。一条一个本地提交(`dev` 上 253 个),文档在 velashell-docs `fix/ssh-review-fixes`。

- **会打断宿主连接的**:迟到的请求应答不再判成协议错误(CH-E1);跳板上等人时外层停表(FW-E1);取消与应答同时到达不泄漏 SFTP 句柄(SF-E1);入站通道先设好对端号再发确认(CH-E2);信任库按密钥类型分开记(API-H4);「允许老算法」放开 SHA-1 RSA 用户签名并补降级重试(API-H5)。
- **安全加固**:报文序号回绕成硬线;退出状态 / 信号 / EOF 只收第一份;私钥、口令用完清零;配置里的 `ProxyCommand` 要调用方批准、`%h` 以 `-` 开头不代入;命令失败消息只带清洗过的 stderr 末尾;重协商失败不放出暂存数据;`Include` 总量与 PKCS#8 KDF 迭代设上限。
- **SFTP 健壮性**:握手与关闭有时限、按 `max-open-handles` 排队、文件名编码无损往返、sftp-server 没起来时报出退出码与 stderr(SF-M3)。
- **原因码说真话**:内部异常不漏出公开 API;失败归到对的那一跳;连不上代理有了自己的 `ProxyUnreachable`(W6);取消与超时分清;宿主按原因码本地化、不解析句子。
- **公开面**(API-P*,纯重构单独成提交):一个功能一个入口、矛盾参数构造时就抛、结构化的 `LastRekey`、没理由公开的降为 internal、命名规矩(API-P15)。宿主的绕路(W1–W10)换成库的能力。
- **可观测性**:报文旁路(`IPacketTap`)、Meter、往返时间(PING / 保活)、always_display 的 `SSH_MSG_DEBUG`、谈成的算法 / 对端版本 / 认证方法 / 各阶段耗时、重协商事件(`Rekeyed`)与耗时、开着的通道的快照。
- **第十节的功能**:GEX、agent 管理与 Pageant、FIDO 安全密钥经 agent(sk-* 钥,第一阶段)、Windows CNG 签名器、主机密钥轮换、PING 与按键时序混淆(`ObscureKeystrokeTiming`)、远程动态转发与放行名单、Unix 域套接字转发、转发的实时吞吐 / 连接快照 / 限速、TLS 拨号器、SFTP 扩展(statvfs / expand-path / fsync / copy-data 等)、改密码、私钥生成与写出、FIPS 清单,以及 ssh_config 的算法清单、转发项、`CertificateFile`、`HostKeyAlias`、`IdentityAgent` 等。
- **测试缺口**(T1–T14):KEX / MAC 互操作矩阵、AEAD 独立参照、真 puttygen 样本、畸形输入与恶意对端、收尾竞态等。大多数修复做过变异检验、对真工具黑盒核对过。

**没做的**:Q1、Q4、Q5、Q7、Q8、Q9、Q11、Q12 拍板后见 §162,Q2 维持不做,Q3、Q6、Q10 已随相关条目解决。有意不做:SF-P3 小文件并发 FSTAT(Seek(End) 与下载中变长的文件);SF-P4 省掉 DATA 应答那次复制(取消后迟到的应答会写进已还回去的内存);F16 宿主处理 xon-xoff(真 OpenSSH 不发);`RequestTTY`(shell 总带伪终端)与 `%i`(没有取 uid 的托管接口)。缺环境或硬件的 F7 gssapi-with-mic、F2 第二阶段(直驱 FIDO 硬件)、F3 的 PKCS#11 与 Secure Enclave 记入 feature-plan.md;F12、F4 后来在 §164、§163 落地。**库里落地、宿主还没接**:连接信息面板(O7)、CNG 签名器、按键时序混淆、报文旁路、转发吞吐与限速、TLS 拨号器、远程动态转发、Unix 套接字转发、ssh_config 转发项的导入、成功建连时经过的每一跳(剩余项见 feature-plan.md)。`SshConnection` / `SshChannel` 两个上帝类体量超标,拆分单独排期为纯重构(CH-D8 列了不能断的耦合点)。

## ✅ 162. 2026-10-06 SSH 库:第八节的质疑决策拍板落地(Q1 / Q4 / Q5 / Q7 / Q8 / Q9 / Q11 / Q12;Q2 维持不做)

§161 留下的第八节决策,维护者 2026-10-06 拍板「除了 Q2 不支持旧的 legacy 外,其他按照推荐内容调整」;规格先行,每条做过变异检验。

- **Q12** SYMLINK 建完对 linkPath 回读 READLINK;发现服务端按相反参数顺序建了,就删掉建错的那条、报错(不支持 READLINK 的服务端照旧信它)。
- **Q9** 服务端回的 SFTP 版本高于 3 时不连(按 v3 解析会把属性读错),报 `SftpUnavailableException`,不再「降到 3 继续」。
- **Q8** SFTP 时间按无符号 32 位(`uint`),2038 年之后照常(到 2106 年)。
- **Q1** `SshRekeyPolicy.Default` 只剩 1 GiB / 2³¹ 个报文、不按时长:黑盒核对 OpenSSH 两端默认都不按时长(`rekeylimit 0 0` / `RekeyLimit default none`);⚠️ 文档里「OpenSSH 默认 1 GiB 或 1 小时」的说法已更正。
- **Q5** 本地与动态转发默认同时听 `127.0.0.1` 与 `::1`(同一端口,`BoundEndPoints`);没有 IPv6 时只听 IPv4,`[::1]` 那个端口被占则整个转发不起;宿主写 `localhost` 同一口径,隧道端口预检两个环回都查。
- **Q4** known_hosts 平时只追加;只有「密钥变了」确认后删旧记录与主机密钥轮换删旧钥(`IHostKeyRotationPolicy.ForgetHostKeysAsync`,默认不删)两处改写,都落到 `KnownHostsFile.RemoveHostKeysAsync`:只动专属于这台主机的记录(`@revoked` / `@cert-authority` / 通配行不动),临时文件 + 原子替换 + 冲突重试;轮换只在宣告完整、证明全过时才删。
- **Q11** SFTP 顺序读遇到短读只补缺口(插到队首,已发的预读照用、窗口不动),并学到服务端实际给的读长度(不低于 4 KiB);曾经整队作废,对每块都短读的服务端吞吐塌到一块 / RTT。
- **Q7 终端走交互道**:先核对了每条通道同一时刻至多一帧在发送队列里 —— 积压来自很多条通道各排一帧,按通道轮询对单条 FIFO 没有改进。改为交互式通道(`SshChannelOptions.IsInteractive`,`OpenShellAsync` 开的自动是)的报文进交互道 `_interactive`,**发送泵在窗口回补之后让它与普通队列轮流出队**:按键至多等一帧,粘贴大段也不饿死别的通道。一条通道的报文(含接收循环替它回的 CLOSE 与请求应答)全走开通道时定下的那条道,自身顺序不变。
- **Q2 维持不做**:CBC 不实现(feature-plan.md「确认不做」)。

## ✅ 163. 2026-10-07 SSH 库:agent 加钥可以带目的地约束(restrict-destination,`ssh-add -h`)

`ssh_plan.md` 第十节 F4 的最后一块:会话声明早已照发(§159),但本库往 agent 里加钥时给不出「这把钥只许登这几台」。原来卡在 velashell-docs 没有规格(净室规程要求实现依据先成文);两阶段走:独立的分析会话读 `PROTOCOL.agent`、RFC 9987 并黑盒核对真 `ssh-agent`,写出 spec/07 §7.3.2,实现只照那一节。

- `SshAgentKeyConstraints.AllowedHops`(`null` 为不限,空表不当「不限」,1–64 跳)+ `SshAgentHop` / `SshAgentHopHost`;`SshAgentHopHost.FromKnownHosts` 照 known_hosts 拼主机,对得上的 `@revoked` 剔掉(故意与 `ssh-add` 不同)。
- ⚠️ **有约束一定发 25** —— 接在 17 后面会被 agent 静默丢掉,钥就成了哪儿都能登。被拒报 `AgentRefused`,**绝不去掉约束重试**。
- 一条 agent 连接只替一个会话做认证:为另一个会话声明之前重开连接(按 `ProxyJump` 连时跳板与目标用同一份 agent 凭据)。
- ⚠️ 反向互操作用例要配口令兜底:认证整个失败会触发 sshd 的 `PerSourcePenalties`,把来源地址拒掉一阵、连累后面的用例。宿主还没接(剩余项见 feature-plan.md)。

## ✅ 164. 2026-10-07 SSH 库:面向 FIPS 的后量子混合密钥交换 `mlkem768nistp256-sha256` / `mlkem1024nistp384-sha384`

`ssh_plan.md` 第十节 F12,原来卡在验证 —— 交换哈希错一个字节,表现也只是「签名验不过」。靶机:AlmaLinux 10.2 的 OpenSSH 9.9p1 带 RHEL 下游补丁、两种都有(上游到 10.6p1 才有 768 那种、默认不开);草案已定稿为 RFC 10042,先写出 velashell-docs spec/03 §3.7,实现只照那一节。

- `HybridKeyExchange` 拆成「KEM + 经典 ECDH」两半。⚠️ `K_CL` 是定长的 X 坐标、**前导零保留**(每种连续交换 1200 次,碰上前导零的概率约 99%);⚠️ 哈希三支各写各的(SHA-384 落进 SHA-512 不报任何错,只是每次都签名失败)。ML-KEM 768 / 1024 平台支持时走 BCL、否则 BouncyCastle。
- 默认清单排在 `mlkem768x25519-sha256` 之后、sntrup761 之前 —— 开了 FIPS 策略的服务端不给 X25519,以前只能谈成不带后量子的 `ecdh-sha2-nistp256`;`FipsApprovedOnly` 里排最前。对端公开值不合格时所有交换方法一律以 `KEY_EXCHANGE_FAILED`(3)断开(曾发 2)。
- 靶机 `docker-compose.test.yml` 的 `ssh-pq`:三个 sshd(FIPS 清单 2226、只给 1024 的 2227、RHEL DEFAULT 2228)。
- ⚠️ `Rekeyed` 事件改在「可以再发起」之后才报 —— 以前订阅者收到事件就再发起会被当成空操作吞掉。(剩余项见 feature-plan.md)

## ✅ 165. 2026-10-07 CI:去掉「VelaShell.Ssh · 压缩严格校验」作业

维护者决定这一项现在不需要在 CI 里跑,删掉 `ci.yml` 的 `ssh-checks` 作业:它只单开一个进程跑 `scripts/ssh/compression/verify-strict-validation.cs`,验 `System.IO.Compression.UseStrictValidation` 打开时 `zlib@openssh.com` 照常往返(那个 AppContext 开关只能在进程启动时设一次,进不了常规用例)。脚本留着,改动压缩代码时手动跑。文档:velashell-docs#92。

## ✅ 166. 2026-10-07 CI:一直红着的几条 SSH 用例

#563 合并前后 CI 在三个系统上都红、本机全绿,都是 CI 环境才摆得出的情形:

- **`ppk截断到任意位置只报私钥异常`**:每截断一次都先跑一遍 Argon2 才在 MAC 上发现不完整,CI 上超过 30 秒被掐。修在库里:派生口令之前先核结构(`Private-MAC` 在且长度对、各段行数够、加密私钥区是 16 的倍数),不满足当场报 `KeyFormatInvalid`。
- ⚠️ **macOS 的 lo0 默认只有 127.0.0.1**:「整个 127/8 都是环回」只在 Linux 与 Windows 上成立,绑 127.0.0.2 报 `AddressNotAvailable`;macOS 上改从 127.0.0.1 发起。
- **DISCONNECT 原因的两条(Ubuntu,偶发)**:客户端发完 DISCONNECT 就关连接,机器忙时早于测试服务端发 NEWKEYS,服务端写失败直接退出、没读到原因;改为测试服务端写失败时先尽力再读一个报文。
- **`服务端拒绝隧道时单条连接失败而转发器继续跑`(Ubuntu,偶发)**:转发器的 RST 可能在 `ConnectAsync` 交回之前就到,Linux 上 connect 本身报 `ConnectionReset`;改成「连上并读到被重置为止」两种都认。

「Code scanning AI findings」是 GitHub 托管的 Copilot 任务、不在仓库的 CI 里,没有动。文档:velashell-docs#92(规格 04 的 `.ppk` 决策)。

## ✅ 167. 2026-10-07 内置 X 服务端:按第二次全库审查的结论逐条修复(`xs_plan.md`)

第二次全库审查(第一次见 §124,守净室规程)的结论写成仓库根未入库的草案 `xs_plan.md`:184 条问题(🔴 9 / 🟠 43 / 🟡 132,分八组)、可支持的功能 F1–F30、待拍板的决策 Q1–Q11、测试缺口 T1–T16。184 条全部修完,一条一个本地提交;前 50 条在主会话修,其余分给六个并行会话,各在自己的 git worktree 里做完再 cherry-pick 回 `dev`。产品取舍按决策表里保守的建议走。
- **资源与拒绝服务**:每项工作 2²⁸ 单位的工作量预算,持锁超 250 ms 记日志;每客户端 1 GiB / 全局 2 GiB 的内存账;GetImage 回复超 256 MB 回 BadAlloc。
- **安全**:剪贴板跟着键盘焦点所在的会话,PRIMARY 同步默认关(Q3);`RestrictForwardedClients` 可限制经 SSH 转发来的程序用 XTEST、原始按键与设备层级(默认关,Q5);`_NET_ACTIVE_WINDOW` 防焦点窃取;Unix 套接字按对端 uid 授权,MIT-SHM 只给同一 IPC 命名空间的对端;`ListenTcp` 默认只在配了 cookie 时开(Q4)。
- **按协议改对的**:整数坐标采样,宽线与弧的接头、端帽;SYNC、Present、DAMAGE、DBE、Composite 的语义;XInput2 抓取;XKB 自动重复按 X 的语义(Q10);剪贴板双向 INCR;颜色名换成 X.Org 的 `rgb.txt`。
- **宿主当窗口管理器**:补齐 ICCCM / Motif / EWMH 提示;`_NET_WM_PING` 没有回应时可强制结束;停 X Server 前说清会断开几个程序;客户端清单与 `BreakGrabs` 等卡住时的恢复手段。
- **Java**:OpenJDK 17 在 `WM_S0` 被占、名字又不认得时当成会套外框,最大化与改尺寸后不重排 —— `WindowManagerName` 默认改为 `LG3D`(CP-9)。
- F1–F30 整体不在这一批;DR-M1、DR-M4、DR-P3、WN-M5、WN-S11 只做了一部分,另发现间接 GLX 选不到单缓冲视觉 —— 后来补齐,见 §178(剩余项见 feature-plan.md)。合并六个分支时 SYNC / Present 的清理钩子拆成「连接断开」与「资源销毁」。文档:velashell-docs#93(`zh/` 与 `en/` 一起改)。

## ✅ 168. 2026-10-07 CI:#565 上的两处红

- **macOS 上套接字目录不可信的用例**:§167 起配置了要监听、却一种传输都没开起来时 `StartAsync` 抛 `IOException`;macOS 没有抽象名,按约定就该抛 —— 改用例,库不用改;没写成按平台的 `if` 分支,免得打断分析器的平台守卫推断(CA1416)。
- **SSH 库真竞态**(`输出读够了告诉OpenSSH_之后的输出丢弃且不会停住` 偶发 30 秒被掐):`WindowedPipeReader` 收尾时先清点未读字节报成已消费、再完成读端,两步之间到的包随读端丢掉、没人回补,回补够不到阈值,对端停在零窗口。改为先在通道状态锁里标记 `IsAbandoned`,`TryDeliver` 在同一把锁里看它,之后的数据走「丢弃并立刻回补」。

「GitHub Advanced Security」失败是 Copilot 月度额度用完(HTTP 402),不在仓库的 CI 里。

## ✅ 169. 2026-10-07 CI:输出队列记帐的飞行窗口把连接判死;ppk 截断用例撞上自己的超时

- **输出队列记帐(真 bug,生产上同样受害)**:`PumpOutputAsync` 整批写完才 `NoteWritten`,71 MB 的 GetImage 回复在飞行期间把计数顶在 `XClient.MaxQueuedOutputBytes`(64 MB)之上,紧接着的请求被 `Send` 判成「客户端不读了」而 `Abort()`。改为写出端**一取走就减**,客户端真不读时照样累积到 64 MB 才断。回归用例让客户端故意不读完,把「还在写」撑成稳态,不赌调度。
- **ppk 截断用例撞上 30 秒全局超时**:不是挂死 —— 只有截断点 492(只差结尾换行,即完整文件)走到 Argon2id 口令派生,3 核 runner 上内存带宽被瓜分时超 30 秒。最后一点不扫(完整文件另有用例覆盖);`test.runsettings` 的「30 秒还没完就一定是挂了」补上「CPU 密集的除外」。
- macOS 上 `EADDRNOTAVAIL` 不是偶发:lo0 只配了 127.0.0.1,`b36eaa8d` 已改从 127.0.0.1 发起,不用改。

## ✅ 170. 2026-10-07 CI:主机密钥轮换的用例等宣告靠轮询,机器忙时就红

- **现象**:合并 #566 后 main 的 ubuntu 作业连着两次红,**两次不是同一条**;同一提交的 PR run 全绿,是单点抖动,不是合并引出的回归。
- **根因**:`HostKeyRotationTests` 用「100 × 10 毫秒」轮询 `LastHostKeyUpdate`,固定 1 秒预算;宣告(`hostkeys-00@openssh.com`)在认证之后才发出、客户端还要再走一轮证明,机器忙时(3 核 runner 上)就赌输(§71「固定泵几十毫秒等于赌调度」)。
- **改法**:`TestChannelScript` 加 `HostKeysAnnouncementSent`,用例等这个确定事件、再给客户端 20 秒走完;「不会有更新」那条没有信号可等,给 2 秒,赌输只是断言变弱(漏判)。让宣告迟到 3 秒:旧写法 5/5 红、新写法 5/5 绿。
- 共同的放大器(3 核 runner + 线程池下限 = 核数)在 §171 压下去:测试设置里并行度固定成 2、不再把 3 核挤满,线程池下限抬高。

## ✅ 171. 2026-10-07 CI:把「3 核 + 线程池下限 = 核数」这个放大器压下去

- **为什么不再逐个修**:§169 / §170 修掉四处时序依赖后,长尾里仍冒出别的(如 macOS 上 `sftp_server起不来时报出退出码与它的stderr`)—— 逐个修是打地鼠。**放大器**:macOS runner 3 核,线程池下限默认就是核数,多出的线程靠「爬坡」每秒注入一两条,而每条连接要占读端、写出端、执行循环好几个续延,谁先排上队全看运气,红法不固定。
- **改法**:`VelaShell.Ssh.Tests` 与 `VelaShell.XServer.Tests` 各加 `TestSession`(`[AssemblyInitialize]`),`ThreadPool.SetMinThreads(max(16, 核数 × 4))` —— 只管线程到得快不快,**不管并发度、不改断言**,该等信号的照等(§170);`VelaShell.Ssh.Tests` 的 `Workers` 从 0(= 核数)压到 **2**。
- ⚠️ **代价**:本机这套 10 秒变 60 秒(CI 预计 55 → 80 秒左右);`test.runsettings` 是 IDE 与 CI 共用的(剩余项见 feature-plan.md)。

## ✅ 172. 2026-10-08 Linux:检查更新后打开发布页面供用户下载

Linux 关于页原先沿用便携式原地更新,系统安装目录不可写时只显示「目录不可写」。现在 Linux 的 `CanSelfUpdate` 固定为 false,发现新版本后在目录可写判断之前转入手动下载:经 Avalonia 的系统浏览器入口打开 GitHub Releases 发布列表,由用户选 deb、rpm 或便携包;提示保留版本号与下载地址,浏览器打不开时仍能手动访问。Windows、macOS 与 Microsoft Store 的更新分支不变。中英文规格已同步到 velashell-docs。

## ✅ 173. 2026-09-22 AI 插件:provider 健康检查 + 请求故障转移(用户定稿)

回答不上来时按用户排好的有序链自动换一家继续说,UI 落在全局设置窗口新一节;口径全部由用户定。

**一、口径(用户确认的五条)**:
1. **候选来源**:`AiSettings.FailoverChain` 非空按链序(可跨供应商跨模型);空则自动发现(同 `CatalogId` + 同 `Model`,排除失败者)。
2. **健康 = 被动冷却 + 主动探测**:失败记内存冷却 60s;切换前逐个探(15s 超时,极小真请求),探不通的跳过。
3. **粘住**:成功切换改写 `ActiveModelId` 并落盘,直到手动切回或它也失败。
4. **触发**:内层 `StreamRetries=1` 耗尽或 401/403(`IsWorthSwitching`)才切,400 等永久错不切;已有可见正文、思考或工具副作用(`HasMeaningfulOutput`)就不切。
5. **不加总开关**(YAGNI):平时一次都触发不了,摆开关是让人多背一个不知道开不开的选项。

**二、落法**:`FailoverChain.cs`、`ProviderHealth.cs`(只在内存)、`HealthProbe.cs`,`ChatPanelView.SendAsync` 外层加 hop 循环。设置页「测试」只在表单值与落库一致(`sameAsSaved`)时才记健康 —— 草稿测出来的成败不能污染共享健康记录。

**三、两个坑**:⚠️ `ReloadProviderCombo` 换 `ItemsSource` 会回放旧下标的 `SelectionChanged`,把刚程序化切过去的那家悄悄顶回原状 —— 加 `_syncingProviders` 护罩,回声只刷界面、不回写设置。⚠️ 401 抛的是 `System.ClientModel.ClientResultException`,按类型匹配,不能按字符串猜。

## ✅ 174. 2026-10-07 AI 插件:同供应商多 API Key、逐 Key 检测与可选轮流首发

- **存储**:同实例「添加 API Key」(OAuth 第二账号仍是独立实例);主槽沿用 `apikey:<provider.Id>`,补充槽按 Guid 加密,JSON 只存 `AdditionalApiKeyIds`、`ActiveApiKeyId` 与默认关闭的 `BalanceApiKeys`;旧配置不迁移。
- **设置页**:逐 Key 行检测只测已保存的槽与端点,选模型前零 HTTP、选后真实 SSE 探活;未保存的地址 / 协议或该行 Key 不发探活。⚠️ 水平减号用固定 24×24 图标坐标,否则退化为点。
- **聊天**:只有未开口且未取消的 401/403/429 才先试同模型下一把 Key(429 保留一次原地重试);多槽请求关 SDK 隐式重试免得次数相乘(§177 收窄);Key 专属冷却用健康表的 `key:` 前缀,全池失败才冷却模型;非 Key 专属的探活失败直接走原模型链(§173);成功后粘住槽。轮流首发开启时每轮首发轮转非冷却槽、不改持久 active,不承诺 HTTP/token/费用均摊。
- **安全**:补充 Key 不送往模型跨 origin 覆盖的地址;模型自有 Key 与 OAuth 不入池;换值 / 删除 / 出境端点变化后不发送会话历史。

## ✅ 175. 2026-10-09 新建连接对话框的悬停闪烁(#577):真因是从 Transparent 起步的画刷过渡

#581 去掉协议栏与分区页签的画刷过渡时,把闪烁解释成「高 DPI 下按钮与图标同时动画,命中区域反复重采样」(#587 又改称「各种缩放比例」)—— 代码改对了,解释是错的。**真因**:常态 `Background` / `BorderBrush` 是 `Transparent` 即 `#00FFFFFF`(全透明的**白**),Avalonia 12.1.3 的 `ColorAnimator` 逐通道插值、不预乘,过渡到 `#363948` 途中是半透明的白,叠在卡片上峰值约 `#7E7E86`,进出各闪一次。跟缩放、命中测试无关 —— **100% 缩放下一样复现**(修复前实测峰值 `#77787E` / `#7D7E86`,issue 动图逐帧量色也落在插值曲线上)。

**改动**:两处继续不挂过渡、注释改成真因;DESIGN.md §6.1 新增「Brush transitions never start or end at `Transparent`」,由 `Design/BrushTransitionFromTransparentTests` 扫 axaml 守住。⚠️ 真要淡入,常态得用目标色的全透明版(同一 RGB、alpha 为 0)。停靠标签的文字前景淡变恢复(两头都是不透明令牌、插值单调,与 #577 无关)。

## ✅ 176. 2026-10-09 侧栏分隔线与折叠拆成两个开关,折叠方框加大(#586)

**原因**:开着折叠时从左往右拖选整行容易误折 —— 竖线由折叠列画,想要线就得开折叠;折叠点击区还借用了紧贴正文的空白间隔(`GutterLayout.IsFoldColumnHit` 一直算到 `TotalWidth`)。**改动**:新增 `TerminalBehavior.ShowFoldGuideLine`(设置「侧栏分隔线」/ 右键菜单「分隔线」),与 `ShowFoldMarker` 各自独立、共用折叠列与列宽,只开分隔线时点击不折叠;一次性迁移 `FoldGuideLineMigrated` 让老配置的线跟着折叠走一次;命中区只认折叠列,列宽 1.6 → 2 格,方框 7–11px → 9–13px;关掉折叠时展开已有折叠,否则那段输出再也找不回来。文档:velashell-docs `{zh,en}/host/architecture.md`。

## ✅ 177. 2026-10-09 AI 插件:#582 复审,修掉九处隐藏问题

#582(多 API Key、故障转移与健康检查)合入后分四块复审,修了下面这些,各有先红后绿的回归用例。
- **规格匹配退回默认窗口(影响最大)**:`ModelsDevCatalog.FindAnywhere` 要求同优先级候选全体一致,否则判歧义、退回 128000 —— 真实 models.dev 缓存里跨供应商重名的 967 个 id 有 670 个被判歧义(含 `gpt-5`、`gpt-4o`)。改为按多数,并列才算歧义。
- **设备码登录**:Google「还没批」回的是 **428 + `authorization_pending`**,#582 只放过 400/401/403 与 429 + `slow_down`,首次轮询就中断;428 同样按配对的协议态放行。
- **「检测全部」**:`HttpClient.Timeout` 抛的 `TaskCanceledException` 被当成批次取消;单行旧结果过期会掐掉整批 —— 改为只认本批次令牌的取消,过期只作废那一行。
- **设置页**:补充槽的草稿 Key 被发往首模型 `BaseUrlOverride` 指向的别家地址;逐 Key 检测丢了槽 id;测通不解 Key 槽冷却;⚠️ `PersistAsync` 丢了 try/catch,`_ = PersistAsync(…)` 的冲突异常无人观察,「添加模型」下次重载悄悄消失。
- **SDK 重试**:两把以上 Key 就关 SDK 隐式重试,而 IM 桥接等没有换槽重试 —— 加第二把 Key 反而一次 429/5xx 就失败;改为 `callerCountsRetries`,只有聊天回路与逐槽探活传 true。
- **Anthropic 流内 `overloaded_error`** 抛不带状态码的 `AnthropicSseException`,故障转移接不住;改按 `ErrorType` 判瞬时。

另:本文件索引表里 #582 写的 §167 – §179 七行与实际节号对不上,已按实际内容改正。复审提出、这次没改的七条留给维护者定(剩余项见 feature-plan.md)。

## ✅ 178. 2026-10-09 内置 X 服务端:第二次审查只做了一部分的几项补齐(`xs_plan.md`)

§167 只做了一部分的几项里,WN-S11、DR-M4 与间接 GLX 的单缓冲视觉已在 `dev` 上补齐;这一批补其余四项(分支 `fix/xserver-review-leftovers`),净室依据为 X.Org 协议与 RENDER 规范、BDF 规范、PDF Reference 的混合公式。
- **核心字体(CP-16 / F21,用户选方案 A:随库带位图)**(8167b38c):原先只有 5 个裁剪过的 misc-fixed,Motif / Xaw / 不带 Xft 的 Tk 请求 `-adobe-helvetica-*` 一律 BadName,xterm 显示不了中文。现在随库带 X.Org 的 misc-fixed 全套、Adobe 75 / 100 dpi 字族与 GNU Unifont(取 OFL)。方案 A 原文是栅格化 Liberation,改用 X.Org 自己的位图:字体名与度量与真实 X 服务端一致,也不用写栅格化器。由 `scripts/xserver/fonts/build-fonts.cs` 从固定的上游提交生成,233 份字体、压缩后约 5 MB。B&H 的 Lucida 因许可要求附特定声明而没带。
- **cursor 字体的字形光标有真实位图**(WN-M5):GetCursorImage 给出它(x11vnc、ffmpeg x11grab 原先录到空指针),没有对应系统光标的字形不再一律显示成箭头。
- **字体在线程池上建、请求暂存**:`xlsfonts -l "*"` 曾在执行线程上持像素锁解析 1.5 秒;现在这个客户端的请求按 SYNC Await 那一套暂存、建好后按序放回。
- **PolyArc 相接的弧按 join-style 接**(DR-M1):一串宽弧当一条路径,GXxor 下接缝不画两次;单独的弧与原来逐像素相同(20 万组随机对拍)。
- **RENDER 在 a1 / a8 / 8888 目标上全部 53 种运算走整数**(DR-P3):穷举核对与浮点版最多差 1,原先走浮点的组合快约 2–12 倍。

文档:velashell-docs 同名分支(`zh/` 与 `en/` 一起改)。没改的两处(宽或高为 0 的宽弧、RENDER 在低位深目标上的浮点路径)见 feature-plan.md H。

## ✅ 179. 2026-10-09 CI:AI 插件的 UI 用例把每次 CI 拖到 25 分钟以上;顺带逮到 SFTP 握手的一个竞态

- **原因**:`VelaShell.Plugin.Ai.Tests` 从 587 条加到 1415 条,多是只能串行的 headless UI 用例;`PumpAsync` 按拍数等(`Task.Delay(5)`),时长看计时器粒度 —— 同样 60 拍 Linux 0.36 秒、macOS 1.55 秒,macOS 上这一个工程要 22 分 36 秒。
- **改法**(f48954df):`HeadlessPump.RunAsync` 保证 `rounds × 5 ms` 的真实时间过去;`scripts/ci/run-tests.sh` 把三组 UI 用例各起一个 `dotnet test` 进程并行跑。macOS 降到 8 分 18 秒。试过加 `timeout-minutes` 兜底又撤了:真卡住时照样烧满时限。
- **顺带的真 bug**(080da3ba):`SftpRequestPipeline` 收包循环先看到通道关了而 `Fault`,`Fault` 只结算已经有人在等的 VERSION,握手晚一步调 `WaitForVersionAsync` 就等满 30 秒,退出码与 stderr 都丢了(§171 记过这条用例在 macOS 上也红过)。现在建任务时流水线已收工就当场以原因结算。

## ✅ 180. 2026-10-09 连接的导入导出、多选批量操作与分组排序(#571)

#571 的用户手上两百多台设备;原先连接存在 SonnetDB 里,没有导入导出,资源管理器只能单选(外加 #524 的 Ctrl 双选)。
- **两种格式**(规则在 `Core/Import/` 的纯函数里,`SessionArchiveService` 只管读写仓储):**JSON**(`SessionArchive`)完整保真,带分组、被引用的共享凭据与隧道,缩进、中文不转义,给人改;**CSV**(`SessionCsv`)固定 15 列、导出即模板,`id` 列导回时精确对上原来那条。导出自动带上选中连接的**跳板主机**(逐级往上),否则到另一台电脑上全是直连。
- **敏感信息**默认不导;勾上必须设至少 8 个字符的导出口令,机密用 `SyncCrypto`(PBKDF2-SHA256 20 万次 + AES-256-GCM)加密进 `secrets`;没有明文导出密码的选项,CSV 永远不带。⚠️ `SessionArchiveFieldTests` 用反射检查每个属性都已归类(配置 / 机密 / 本机状态),新字段没归类直接红。
- **CSV 读得宽容**:分隔符与表头别名(含中日韩写法)自动认;编码依次试 BOM → 严格 UTF-8 → 本地 ANSI 代码页(中文 Windows 上 Excel 默认存 GBK);`= + - @` 开头的单元格导出时补单引号防公式注入。
- **导入规则**(`SessionImportPlanner`):先按 Id、再按「协议 + 主机 + 端口 + 用户名」判重复,跳过(默认)/ 覆盖 / 两条都留;覆盖时 CSV 只改文件里有的列(`password` 空着 = 不改)——「导出 CSV → Excel 改用户名 → 导回去」靠这一条;文件 Id 不冲突就沿用(开着云同步的两台电脑之间仍是同一条);跳板找不到改直连、成环断一环,妥协逐条报出来。
- **多选**:Ctrl 单击加减、不设上限,Shift 选区间;多选菜单:打开 N 个连接 / 在双栏 SFTP 中打开(恰好两条时)/ 移动到分组 / 批量修改 / 导出所选 / 删除。**整组 / 多选打开**(分组菜单「打开全部连接」、多选菜单「打开 N 个连接」)走 `MainWindowViewModel.OpenProfilesAsync`,最多 6 条同时握手,超过 10 条先确认。
- **批量修改**:用户名 / 端口 / 认证 / 跳板,勾了才改;协议不支持的跳过并计数(规则抽成 `CredentialCompatibility`),会成环的跳板整批跳过。分组不放进来 —— 多选的「移动到分组」就是改分组。
- **分组排序**:拖动分组行、「上移 / 下移」、按名称排序(`CompareOptions.NumericOrdering`,「机房 2」在「机房 10」前面);落库时重编 `SortOrder`,顺手修了新建分组按「当前个数」取号会撞号(改为最大值 + 1)。快捷键总表加「资源管理器」一组 —— 列表本身是单选的,这些手势不写进来就没人知道。
- ⚠️ **评审后补**:导入 / 批量修改的写库异常一律留在对话框里(只接 `IOException` 时别的异常会逃出 `ReactiveCommand` 带走整个应用),失败后关掉时资源管理器重读 —— 否则树里缓存的旧对象会在下次置顶 / 移动分组时把旧值整条写回;覆盖时同 Id 的行优先于按主机撞上的行。

文档:velashell-docs#103(中英镜像的 `会话导入.md` 第七节、`交互与界面规格.md` §3 / §12、`快捷键参考.md`「资源管理器」一节)。

## ✅ 181. 2026-10-09 内置 X 服务端:合并本地遗留的草稿 —— alpha-map 长链会把进程带崩、字体就近匹配看平均宽度

§178 开工前两个并行会话留在本地、没提交的草稿(DR-M1 / DR-M4 / DR-P3、WN-M5 / CP-16)逐文件对过:§178 已在 `dev` 上做得更完整,大部分收进 stash 不再用,只补 `dev` 上没有的几处。
- **alpha-map 只作用一层**(e05c3553):ChangePicture 只核新挂上的那张自己有没有 alpha-map,能接成任意长的链,合成时顺着链每环递归一层,约 6400 环栈溢出。⚠️ .NET 接不住栈溢出、服务端又在宿主进程里 —— 任何能连上的 X 客户端都能把 VelaShell 整个带崩。现在合成只看一层(作 alpha-map 用的那张自己的 alpha-map 不算)。
- **字体就近匹配一样近时先比平均宽度**(52ce6b57):`FontIndex.NearestSize` 原先按名字先后取,要双宽字体的 13 / 18 像素拿到 7x13 / 9x18;没给 AVERAGE_WIDTH 时不变。
- 另补测试,`AGENTS.md` 的规范清单补上 XLFD;比对时看到、两边都没做的四处由 §185 处理。

文档:velashell-docs#104(中英镜像的 `xserver/design/architecture.md` §7)。

## ✅ 182. 2026-10-09 AI 插件:保存一次之后再也发不出消息;字号与设置窗口整体放大一档

**根因**:#582 起设置写入走「基线比对」,基线是 `JsonSerializer.Serialize(settings)` 的原文,校验时把读回的 `JsonElement` 再序列化一遍来比。⚠️ 两者对 `DateTimeOffset` 不一致(`"+08:00"` 对 `"\u002B08:00"`):MCP 刷过工具清单后设置里就有东八区时间,第一次保存之后每次保存、每次发送都报 `ApiKeySlotChangedException`。问题出在序列化本身,不在存储。
**改法**:基线改取 `SavedSettingsJson(snapshot)`,与读回比对同一种规范化文本;存量数据不用迁移(`LoadAsync` 本就按读回的文本记基线)。
**字号与窗口**:`VelaFontSize*` 令牌整体上调一档,字段说明与标签同号、层级靠 Tertiary 颜色拉开;七个窗口各放大一档。宿主不替插件窗口收边,`Ui/WindowFit.cs` 把设计尺寸夹到屏幕工作区以内(四周留 48)。
同段输出里成批的 `TaskCanceledException` 是已捕获的首次机会异常,无关。

## ✅ 183. 2026-10-09 AI 插件:启动预热每次都报 `Preloading 'CSharpMath.Editor' failed`

功能无影响。`AiPlugin.PrewarmPanelAssemblies` 沿**元数据引用图**逐个硬装,而 fork 包 `Sylinko.CSharpMath.Avalonia` 12.0.0 的 `CSharpMath.Rendering.dll` 引着没随包带的 `CSharpMath.Editor`(只有公式编辑键盘用到;插件只渲染,运行期从不碰)。新的 `AiPlugin.CanResolve` 按 `PluginAssemblyLoadContext.Load` 的同一套规则先判断(插件 deps.json 解析得到,或在默认 ALC 的 `TRUSTED_PLATFORM_ASSEMBLIES` 里),两边都没有的悬空引用记 Debug 跳过;真装不动的(损坏、无权限)照旧警告。

## ✅ 184. 2026-10-09 AI 插件:模型下拉一律写成"供应商·模型"

多家供应商挂同名模型(几家中转站都有 `gpt-5.5`)时下拉分不清,原先只在多于一家时才加前缀。现在聊天面板的模型下拉一律 `供应商·模型`(如 `Routin·gpt-5.5`,按用户给的格式,中点两侧不留空格),供应商名为空时退回模型名;`MaxWidth` 230→300,超长用 `CharacterEllipsis` 收成省略号。故障转移链与协作接入页的下拉保持原样。

## ✅ 185. 2026-10-10 内置 X 服务端:对着 `xs_plan.md` 逐条核对,补完最后几项(用户需求)

**一、核对**:`xs_plan.md` 的 222 个条目编号与带 `xs_plan:` 行的提交逐个对 —— 191 个有对应提交,其余 7 个是「同 XX」的重复编号、24 个是 CP 一节的指向(别处的条目、F1–F30 的新功能或非目标)。§178、§181 的剩余项回到代码复核,能修的是下面四项加测试缺口 T15(分支 `fix/xserver-plan-leftovers`);要全局指针钩子或实机的几项留下。
**二、做了什么**:
- **宽或高为 0 的宽弧画满线宽**(ed457425):原先内边界缩成中线、只画出一侧半个线宽;现在按线段画,折返处补一个直径为线宽的圆。
- **RENDER 的 alpha-map 照规范补全**(de984828):变换与过滤作用在 drawable 与 alpha-map 拼好的那张上;只换 alpha 通道、像素一律预乘;目标的 alpha-map 原先接受但不生效,现在经 `CompositeOnto` 拼成临时 a8r8g8b8、颜色与 alpha 分别写回;同缓冲判断改成 `RenderSource` 的虚方法。文档原说「必须是只有 alpha 的格式」与规范不符,改的是文档。
- **按磅数要核心字体时按屏幕分辨率挑 75 / 100 dpi**(3030db33):原先总取 75 dpi,96 dpi 宿主上 Motif / Xaw / Tk 字偏小;现在按 `X11ServerOptions.Dpi` 挑 RESOLUTION_Y 最近的。
- **字体脚本核对 X.Org 字体**(b8ca6d91):⚠️ GitLab 现做的归档不保证逐字节稳定,改为对挑出的文件按文件名排序算整体摘要;全部核对完才动 `Fonts/Data`。
- **`.Xauthority` 与真实 xauth 对过**(1ac42bd7,T15)。⚠️ 文件按 base64 经命令行进出容器 —— 这台机器上 Docker Desktop 挂本机目录会一直卡住。

核对完 `xs_plan.md` 删掉:登记的问题都修完了,剩下的抄进 `feature-plan.md` H;原文(含 F1–F30 的使用场景与评估)在 git 历史里。文档:velashell-docs#105(中英镜像)。

## ✅ 186. 2026-10-09 隧道面板:重开后列表是空的;数量徽标看不见字;放大提亮;启停转圈;可拖动;程序启动时自动建立

**一、重开后列表空**:关面板只是藏起来,下拉框绑定一直活着;`OpenAsync` 刷新时 `Servers.Clear()` 让下拉框把选中项回写成 null、条目换成空集合,之后选回同一台又因「同一个实例」被跳过。现在重建列表期间(`_isReloadingServers`)不理回写,重建完换上新实例、通知下拉框重选。
**二、数量徽标看不见字**:三处徽标是 `VelaAccent` 底配 `VelaAccentText` 字,而 `ThemeTokenApplier` 把后者赋成强调色本身。改成 `VelaAccentDim` 底 + `VelaAccent` 字;守门用例 `Design/AccentOnAccentTests` 禁止 `VelaAccent` 底上用这两个令牌作字色。
**三、放大提亮**:宽 340→380、字号整体大一号,说明文字从 `VelaTextMuted`(暗色主题上约 2.5:1)提到 `VelaTextTertiary`;首行改 DockPanel,放不下时截断名字而不是最右的徽标。
**四、启停转圈**:`IsStarting` / `IsStopping`(合成 `IsBusy`)期间那格换成 `CircularProgressRing`,编辑、删除禁用。⚠️ 命令本身不并发,但自动恢复(掉线重建)与手动启动是两条路 —— 原先重建途中再点「启动」会把同一本地端口绑两次;现在各入口都先看 `IsBusy`。「端口被占用」改取这条隧道自己的端口。
**五、可拖动**:接入 `PanelDragHandler` + `IDraggablePanel`,存相对默认锚点的偏移(`ui-layout/tunnel-panel`)。⚠️ 外层 Panel 要铺满整行 —— 父容器只贴着面板时能拖的范围是零。交互规格「位置记忆」早写了隧道面板,此前没实现。
**六、程序启动时自动建立**:启停状态仍不落盘。`TunnelConfig.AutoStart`(缺字段按 false),第三个勾选框默认不勾、行上多一枚「自启」徽标;`InitializeAsync` 末尾不等待地调 `AutoStartTunnelsAsync`,只给含勾选项的服务器后台拨号,与切换服务器的恢复共用「每台只恢复一次」;失败走右下角错误提示、点名服务器与隧道,互不耽误,一条都没建成时专为它们拨的连接随即断开。

⚠️ 开着 Docker 时 `X11_RefusedByServer_KeepsAgentForwardingAndWarnsOnce` 会红:SSH 靶机镜像建于 Dockerfile 那次改动之前(§159、§161、§167),重建即可。文档:velashell-docs(中英镜像的交互与界面规格 §10)。

## ✅ 187. 2026-10-10 内置 X 服务端:草案第十二节的新功能 —— X 程序清单、信任级别与按会话的显示、拖放、托盘、单窗口模式,以及 F4–F30 的其余各项(用户需求)

**一、分工**:`feature-plan.md` H 剩下的三行新功能:F3、F2 / F1(决策 Q5)、F4–F30。主会话做 F3 并接 F12、F13、F16;两个子会话各在自己的 git worktree 做 F2 / F1 与 F4–F30,一项一个提交,由主会话 cherry-pick 回 `dev`。新用到的规范(SECURITY 7.1、XDND 5、System Tray 0.3 与 XEmbed 0.5、XI 2.1 等)先登记进 `src/VelaShell.XServer/AGENTS.md` 的规范清单。

**二、X 程序清单与强制结束(F3)**:库加 `XClientInfo.HoldsServerGrab`,GrabServer 抓得太久(10 秒起)经回调 `ServerGrabStalled` 在放锁后交给宿主(原先只记日志)。宿主 `ILocalXServer` 加 `GetClientsAsync` / `DisconnectClient(key)` / `BreakGrabs`;键是「第几次启动:哪个实例:客户端编号」,停了再开旧键作废、按会话的显示上也不混。标题栏 X Server 按钮打开面板(与隧道面板同样式、互斥):程序、来源会话、窗口数与内存、GrabServer 标记,悬停「断开」(先确认),底部「解除卡住」「停止」;抓得太久弹警告带「断开它」。外部 VcXsrv 列不出程序。

**三、信任级别(F2)与每个 SSH 会话一个显示(F1)**:
- 库实现 SECURITY 扩展:MIT-MAGIC-COOKIE-1 授权的签发 / 撤销 / 到期,`XClientTrust.Untrusted` 让宿主直接指明非受信。非受信客户端按规范第三章受限(碰不到受信客户端的资源、XTEST 等扩展看不见、键盘不归它时读到全 0、受信程序的选区回 None 等)。⚠️ 规范第五章编码表与 xauth 实际发的线格式不一致(value-mask 的位置),照协议头。
- `ssh -X` 的非受信通道以非受信客户端接入、不跑 xauth。⚠️ 内置引擎没在运行时不再退到本机 TCP —— 那等于悄悄升成受信。行为规格 `zh/ssh/spec/07` §7.5.9 同步。
- 「每个 SSH 会话一个显示」(`XServerOptions.DisplayPerSession`,**默认关**,下次启动生效):会话的通道进这个会话自己的、不监听的 `X11Server`(独立的根窗口、选区与剪贴板),会话断开或停服时收掉;至多 32 个,超了连不上、不落回共用的显示。`AvaloniaXServerHost` 改为 Transient,一个服务端一个。

**四、拖放进 X 窗口(F16)**:库替宿主当 XDND 第 5 版的源(`InjectDrop` 等)。文本按 UTF8_STRING 等给;文件经该程序所属 SSH 会话的 SFTP 传到远端 `/tmp/velashell-drop-<guid>`(权限 700)再以 `text/uri-list` 给,不是经 SSH 会话来的程序提示用户。拖出见 §189。

**五、托盘图标(F12)**:库可选当托盘管理器(`X11ServerOptions.SystemTray`,按 XEmbed 嵌入),宿主 `XTrayIcons` 把每个图标画成本机托盘图标,单击送左键、菜单里送右键;`TrayIconService` 改成只增删自己的图标(原先整组替换,会抹掉 X 程序的图标)。气泡消息收下不显示。

**六、单窗口(rootful)模式(F13,决策 Q2)**:`X11ServerOptions.Rootful` 把整个根窗口作为一个顶层交给宿主,服务端不当窗口管理器 / XSETTINGS / 托盘 / 合成管理器。顶层照旧各有缓冲,每批放锁前把变了的地方按堆叠次序拼进根窗口的缓冲;指针、滚动与拖放按根坐标注入。宿主的「窗口模式」对内置引擎也生效:开一个「X 桌面 :N」窗口,关掉它当作停 X Server。互操作镜像加装 twm,实跑零协议错误。

**七、F4–F30 的其余各项**(子会话 B,`xs_plan: Fn` 行标着对应哪一项):
- **F5 第一步,本机输入法上屏**:`InjectText` 每个字借一个空键码改成 Unicode 键值再按下松开,改过的不改回;设置「X 窗口里用本机输入法」**默认开**。推翻了「确认不做」里的 XIM 一条,XIM 桥见 §189。
- **F6** XI 2.1 平滑滚动(攒够一格再模拟按钮 4–7);**F8** Warp / confine-to(宿主挪 / 关系统光标,Windows 与 Linux 的 X11 桌面);**F9** 屏保协作(只在 Windows 上抑制本机屏保);**F10** `_NET_WM_SYNC_REQUEST`(改尺寸时攒着损伤等重画完,至多 300 毫秒)。
- **F11** 合成管理器,设置「X 窗口透明与圆角」**默认关**;`ClientSideShadows` 仍关(Windows 上透明阴影区接住鼠标)。
- **F14 / F15** 剪贴板文本、HTML、PNG 双向,服务端当 CLIPBOARD_MANAGER。
- **F17 / F18** 标题前标来源(设置「标出 X 窗口的来源」,**默认开**);Windows 任务栏按 WM_CLASS 归组;按类名 / 实例名 / role 记住本次运行里关掉时的位置与尺寸。
- **F20** Windows 上 X 窗口系统菜单里的截图;**F22** GLX 4 倍多重采样配置与 `GLX_EXT_libglvnd`;**F23** 间接 GL 的选择模式、点画、求值器;**F24** RENDER 的 Over / Add / 纯色过遮罩用 `Vector128`(与标量版逐位一致,约 2 倍 / 3.7 倍);**F25** Present 的帧号跟着宿主的帧节拍;**F27** `XServerMetrics` 与执行线程看门狗(超过 5 秒记 `watchdog:`);**F28** X-Resource 的 LocalClientPid。
- **F4**:Linux / macOS 上默认只开 Unix 套接字,设置「也开 TCP 端口」**默认关**(行为变化:经网络连的容器程序要打开它);Windows 照旧开。
- **执行线程按客户端轮流取工作**:间接 glxgears 排满请求时 xdpyinfo 要等 25 秒,改成按客户端分队轮流(宿主与计时器的队优先)后在 1 秒内。
- F21、F26 此前就有;F19 只做了按显示器各自缩放报 DPI。F7、F29、F30 等其余项见 `feature-plan.md` H。

**八、合进来时的衔接**:交叉处另做衔接(F3 × F1、F13 × F1,F6 × F13 的滚动按根坐标注入,F11 × F13 选区让给远端,F8 × F13 的坐标换算)。截图撞上两条守门用例:WndProc 钩子只许 `Win32WindowChrome.cs` 注册(加 `AddSystemCommandHandler`)、不许 async void。⚠️ 「两边都留」的冲突合并会丢掉共用的一行(resx 的 `</data>`、`{ }`、`</Grid>`),每合一个就核 resx 的 XML 并整体构建。⚠️ 轮流取工作之后连接收尾排在那个客户端自己的队里,`ServeAsync` 返回时不一定已经做了,测试要等到收尾(同类竞态另见 §188 九)。

文档:velashell-docs(中英镜像的 X Server 架构、排障、SSH 行为规格 07 §7.5.9 与 SSH 架构、交互与界面规格、设置审计第十二批)。

## ✅ 188. 2026-10-10 内置 X 服务端:核对 §187 的几项新设置,修掉看到的问题(用户需求)

**一、来由**:§187 的新设置维护者确认默认值都保持 —— Linux / macOS 不开 TCP 端口、本机输入法开、来源标识开、合成管理器关(`ClientSideShadows` 也关)、每个 SSH 会话一个显示关;F19 整数倍放大先等。再核一遍实现:主会话读宿主一侧,三个只读审查子会话看库里的 F5、F11 与 F17 / F18、F4;审查报的每一条先对着代码核实,不成立的不改。

**二、F5 本机输入法上屏(`X11Server.TextInput.cs` 重写)**:
- **挪用键码要空闲 3 秒**(原 200 毫秒):⚠️ 客户端收到通知才重取键位表,取到的是请求到达那一刻的;慢链路上一段多于空键码数(约 116 个)的字,前面的字的键码已被改成后面的字。「客户端取过一次键位表就能挪」不成立(它可能为别的原因先取过),只能靠时间;键盘被同步抓取冻结、还有按键排着时也不挪。一个可借的都没有时丢掉剩下的字、记日志(原先每 200 毫秒重试、永不停)。
- **按顺序**:注入的字与按键排成一队,等键码时按的回车不再插到字中间。
- **一批一次通知**:先改好全部新借键码,发一次 MappingNotify 与一次只报这几个键的 XKB MapNotify;原先每字一轮、报全部 248 个键,GTK / Qt / xkbcommon 每个字重编译一遍键位表。
- **大小写**:借来的键是 ALPHABETIC 类型、两级同键值,CapsLock 下不再把「é」转成「É」;按着修饰键(NumLock 除外)一律借键码;CR LF 只按一次 Return。
- **非受信客户端**:⚠️ 原先它们收到通知、重取键位表就能逐字读出用户在受信程序里输入的字。现在键盘事件不送它们时通知也不发、这些键码对它们是 NoSymbol,只有输入给它们自己时才看得见。
- 宿主 `XNativeWindow`:按着 Shift 时上屏的字不再被吞;一个键报来两个字(Windows 死键)不重复输入;失活时清掉预编辑。

**三、F11、托盘与来源标识**:
- **合成管理器关着时 ARGB 窗口按不透明显示**:`HasAlpha` 只在深度 32 且服务端当合成管理器时为真(托盘图标例外)。⚠️ 原先只看深度:默认关时要了 alpha 的 GL 程序(GLFW 默认就要)清屏 alpha 为 0,整个窗口透出后面 —— 正好违背「默认关」的初衷。
- 远端合成器接走 `_NET_WM_CM_S0` 又退出、或属主被设成 None 时,服务端接回来并按 ICCCM §2.8 广播 MANAGER。
- 托盘图标名字照窗口标题过滤,托盘提示前写 `user@host:22`(原先远端程序挂盾牌图标、提示写「Windows 安全中心」)。
- **「标出 X 窗口的来源」立即生效**(原先要重启 X Server)。⚠️ 来源与标题各自包在双向隔离符(FSI … PDI)里 —— 来源取自连接配置、不经服务端过滤,其中的 U+202E 原先能把标题倒过来。
- **记住窗口位置**改成所有宿主共用一份、记系统桌面坐标:宿主自 §187 起是 Transient,原先记在实例上,停了再开就全忘;按根坐标记时插拔左边的显示器会整体错开。

**四、F4 只开 Unix 套接字**:
- 显示地址按实际开着的监听报(Linux / macOS `:N`,只有 TCP 时 `localhost:N.0`);原先一律 `localhost:N.0`,本机程序照提示设 `DISPLAY` 连不上。
- **Unix 套接字建不起来、TCP 又没开时只服务 SSH 转发**并弹警告(`XServerStartResult.Warning`),原先整个起不来。⚠️ macOS 快速切换用户时 `/tmp/.X11-unix` 归先登录的用户。
- 设置说明按平台写;「也开 TCP 端口」写明只听 127.0.0.1、桥接网络里的容器连不到(Linux 上改挂 `/tmp/.X11-unix`)。
- 自动选号把活着的进程持着 `/tmp/.X{N}-lock` 的号算占用。内置引擎停了之后老会话的通道只在 Windows 上退到本机 TCP:⚠️ 别的平台没有 VcXsrv,环回 `6000+N` 上只可能是别的用户或 sshd 自己的 X11 转发端口。

**五、F1 每个 SSH 会话一个显示(宿主)**:归宿主管的设置改成共用的与会话的显示走同一处(`ConfigureHost`)—— 原先会话的显示只拿到键盘布局与窗口模式,关掉的开关在那里照样开着。单窗口模式下关掉某个会话的「X 桌面」窗口只收掉这个会话的显示(有程序连着先确认);共用的显示照旧是停 X Server。

**六、设置页**:五个开关原先全在「键盘」下;「X 窗口透明与圆角」「标出 X 窗口的来源」挪到「显示」,「X 窗口里用本机输入法」留在「键盘」,「限制经 SSH 转发来的程序」「每个 SSH 会话一个显示」「也开 TCP 端口」进新的「安全」一节(只在内置引擎时出现)。

**七、F19 与没做的**:F19 按维护者的决定先等;核对时看到、这一轮没做的几处(透明窗口、防冒充、本机输入法、TCP 监听地址)记进 `feature-plan.md` H。

**八、验证**:xev 收到借来的键码按新键值解释、一批一次 MappingNotify,CapsLock 下仍是 eacute(改之前是 Eacute、每字一轮)。⚠️ 本机的 `velashell-xclients` 镜像早于加装 twm,twm 那条一直早退,按 Dockerfile 重建之后才实跑。

**九、CI 上的测试竞态**:⚠️ §187 轮流取工作之后,`ServeAsync` 返回时连接收尾不一定做完,慢机器上偶尔看到收尾前的状态(macOS 上 `GlxTests` 的 Pbuffer 没释放)。测试工具加 `XTestClient.DisconnectAsync`(等清单里这个编号不再连着),8 处换过去;必现竞态的用例上旧写法 5 次全红、新写法 5 次全绿。同类:持锁时记的日志放锁后才交出,往返后马上数会数漏 —— 加 `ServerLog.WaitForAsync`(5 秒为限),6 处换过去。宿主里没有依赖这个先后的地方。

文档:velashell-docs(中英镜像的 X Server 架构 §5 / §7、排障、设置审计第十二批、交互与界面规格)。

## ✅ 189. 2026-10-10 内置 X 服务端:本机输入法的 XIM 桥(F5 第二步,决策 Q1)与从 X 程序往本机拖出来(F16 的另一半)(用户需求)

**一、XIM 服务端(库)**:`X11Server` 在 `InputMethodName` 非空时占住 `@server=名字`、登记进根窗口的 `XIM_SERVERS`,按 Input Method Protocol 1.0 与 XIM Transport Specification 0.1 实现 X 传输;两份规范对长包的属性属主、通知格式有冲突,按 Input Method Protocol 附录 D 的表实现。编码协商优先 COMPOUND_TEXT、其次 UTF-8,不接 off-the-spot。⚠️ `LOCALES` / `TRANSPORT` 要在启动 XIM 时先建好:真实 Xlib 用 only-if-exists 的 `InternAtom` 查它们,不预先建就认为没有输入法服务端(xterm 互操作才逮到)。输入上下文建立后发 `XIM_SET_EVENT_MASK(0,0)`,按键不走 XIM,免得 SSH 上每个键多一个往返;`InjectText` 用 XIM_COMMIT 交可见字,`InjectPreedit` 给 on-the-spot 客户端发预编辑,其它风格由宿主叠画。Qt 5/6 与 GTK 4 没有 XIM,仍走借键码路径。宿主静默注入 `XMODIFIERS=@im=velashell`(保留用户已有值)。

**二、拖出(库与宿主)**:多窗口模式下根窗口的 `XdndProxy` 指到服务端的代理窗口,按 XDND v5 只接 `text/uri-list` 与文字、只声明 `XdndActionCopy`。⚠️ Java AWT 不查根窗口代理:X 程序拥有 `XdndSelection` 且抓着指针时,把代理窗口静默垫进根窗口子窗口最底层,抓取结束后清理,不在 X 窗口之间的拖放链路里留下代理。宿主把远端文件经同一 SSH 会话的 SFTP 取到 `%TEMP%/velashell-xdrag` 下的隔离目录(不跟随符号链接目录、处理 Windows 保留名,一天后清理),取回期间要用户保持按键、松开即不拖;只在按钮仍按住时调 Avalonia 原生拖放,结果经 `CompleteOutgoingDrag(drag, dropped)` 交回服务端。一律复制、远端原件不删;拖回 X 窗口不接受,单窗口模式不接拖出。

文档:velashell-docs#109(中英镜像的 X Server 架构、排障、宿主交互规格与设置审计);宿主 PR VelaShell#600。

## ✅ 190. 2026-10-10 AI 插件:字号恢复紧凑比例,弱化文字统一 11 号

AI 插件上一轮整体放大的字号恢复一档:聊天正文回到 13 号,标题、表单、工具栏与各窗口文本回到原有层级,窗口设计尺寸不变;说明、状态、模型信息等弱化文字不再用 10 号,统一至少 11 号。全部引用 `VelaFontSize*` 动态令牌,随宿主 UI 字号缩放;回归用例守住紧凑档位与弱化文字下限。

## ✅ 191. 2026-10-10 启动画面:五套样式,设置里可选,跟随主题(用户需求)

**起因**:启动慢时没有任何指示,用户以为软件打不开。实测冷启动首帧 8.1 s,其中 `Main` 之前 2.2 s、`Main → DI` 3.5 s(Avalonia 平台初始化 + XAML 加载)—— Avalonia 窗口最早也只能在这之后出现,恰好错过最需要反馈的那一段。
**设计**:先做可动的预览给用户挑,用户定五套都做、设置里可选、默认经典:经典(最接近 VS)、终端开机、船帆座、提示符、看板娘;规格在 DESIGN.md §7.4。
**实现**(`Splash/`):`Program.Main` 拿到单实例锁后起独立线程,`Win32SplashWindow`(分层窗口,Skia 画进 DIB,不抢焦点)跑自己的消息循环,`FirstFrameSignal` 到达时淡出,60 s 保险丝;启动中途失败时先收掉画面再弹错误框。进度由启动打点(`StartupTrace.MarkRecorded`)驱动。配色取 `UiThemeCatalog`,不出现颜色字面量;设置页预览 `Controls/SplashPreview` 在 Avalonia 的 Skia 画布上跑同一份绘制代码。设置项 `AppearanceOptions.SplashStyle`(出厂 `classic`,仅 Windows;`VELASHELL_NO_SPLASH=1` 时不显示)。⚠️ 数据库开之前就要用的主题 / 强调色 / 语言 / 样式镜像进 `~/.velashell/startup.appearance`;Logo 另编一份嵌入资源(那时读不到 avares://);看板娘插画近 800 KB,作为松散文件 `splash/mascot.png`,选了那一套才读。
实测热启动 `Main` 115 ms → `SplashShown` 165 ms,冷启动 `Main` 3.0 s → `SplashShown` 3.09 s。
⚠️ **没坐实的现象**:接入后头两次启动 9 s / 12 s 内没等到首帧,之后十几次都正常,没拿到栈(`dotnet-dump` 10.0 读不了 .NET 11 的转储)。为降低风险把画面线程从 STA 改回默认 MTA(主线程的 COM 调用不会被封送过来等它泵消息),并加 `[Splash] style=… open=… frames=… render avg/max` 自诊断日志 —— 再遇到时先看这一行和时间线里的 `SplashShown`。
文档:velashell-docs#110(中英镜像的交互与界面规格 §14.5、设置审计第十三批)。

## ✅ 192. 2026-10-10 内置 X 服务端:gnome-calculator 拖动后冻住、下拉菜单黑边、操作卡(用户需求)

**起因**:远端 `gnome-calculator`(GTK 4)操作卡、拖动窗口后冻住一阵、下拉菜单一圈宽黑边。在 X 连接外包一层统计、Docker 里直连与经 SSH 各量一遍:两个是我们的,一个主要在客户端。
**一、拖动后冻住(库的缺陷)**:宿主每个 WM_MOVE 都让服务端发一组 ConfigureNotify,GTK 每组做五次同步往返 —— 往返 20 毫秒时拖一秒,松手后还冻 8.9 秒。改为**按客户端处理的进度合并**(`X11Server.TopLevels.cs` 的 `StartMoveBarrier` / `ReleaseMoveBarrier`):交出一次移动后,声明了 `_NET_WM_PING` 的等 pong(最多 2 秒),没声明的至少隔 50 毫秒;期间只记最新位置,客户端自己挪窗口时没交出的位置作废。往返 20 毫秒时同样的拖动只交 4 次、松手后 0.6 秒处理完。
**二、下拉菜单黑边(库的取舍)**:GTK 4 弹出菜单总按 ARGB 画、四周留 alpha 为 0 的阴影区,再用 SHAPE 输入形状只圈出本体;合成管理器关着(默认)时宿主把 alpha 补成 0xFF,阴影区成了黑边。改成**输入形状只占窗口一部分的 ARGB 窗口照 alpha 显示**(`HasAlphaOnHost`),没设输入形状(清屏 alpha 为 0 的 GL 程序)或盖满整窗的仍按不透明。否决「默认打开合成管理器」:所有 ARGB 窗口都多一层系统合成,GL 程序又会透出后面。
**三、操作卡(主要在客户端)**:GTK 4 经 SSH 退到 llvmpipe、每帧整窗 PutImage,点一下按钮 29 MB,`GSK_RENDERER=cairo` 时 0.15 MB。宿主新增 `Core/XServer/RemoteGtkRenderer`:开着 X11 转发的会话与 `XMODIFIERS` 同一句静默注入 `GSK_RENDERER=cairo`(已设的不动)。GTK 4 每帧约 16 次同步往返重算顶层尺寸,任何 X 服务端都省不掉,几十毫秒的链路上动画会掉到每秒几帧。
**换用别的 C# X 服务端**:否决 —— 按净室规程不读别家源码;两个是本库的行为、改掉即可,第三个换哪个 X 服务端(VcXsrv、MobaXterm、Xorg)都一样。
文档:velashell-docs#111(中英镜像的 X Server 架构 §6、§7、§10,排障与宿主交互规格)。

## ✅ 193. 2026-10-10 SSH 库:zlib 流结束标记、关闭后写 stdin、HMAC 的 Verify、重协商互操作四处修补;宿主自动重连不再弹主机指纹确认框(用户需求)

前四处是复查 SSH 库边界行为与 .NET 11 新 API 用法时找出的;来龙去脉在 velashell-docs 的 SSH 架构 §11.2.28。
**一、zlib 流结束标记**:对端写了流结束标记时,原来错到下一帧才报「解压失败」。.NET 11 的 `ZLibDecoder` 在标记处返回 `Done`;`ZlibCompressor.Decompress` 见到 `Done` 当场以 `ProtocolError` 断开 —— SSH 的压缩流不会结束,OpenSSH 遇到流结束同样是致命错误。
**二、关闭后写 stdin**:管道的 writer 要等 reader(归 stdin 泵,通道收尾之后才退出)完成才报「完成」,于是调用方已看到关闭之后的写照常返回、数据悄悄丢掉(300 轮 275 轮;线上从没多发)。新增 internal 的 `Channels/StandardInputWriter`:先问 `SshChannel.AcceptsInput`,不收了当场返回 `IsCompleted`,通道流抛 `IOException`。
**三、重协商互操作**:服务端发起的重协商原只对真 sshd 跑过 ed25519,补上 ecdsa-sha2-nistp256 / rsa-sha2-512 / rsa-sha2-256。
**四、`HMAC*.Verify`**:hashed known_hosts 与 `.ppk` 的 MAC 比对改用 `HMACSHA1.Verify` / `HMACSHA256.Verify`(内部就是 `FixedTimeEquals`)。⚠️ 长度不对时它抛 `ArgumentException` 而不是返回 false:`KnownHostsFile.MatchesHashed` 先查长度(坏行当成对不上),`.ppk` 的 MAC 长度在 `EnsureComplete` 里早已核过。
**五、自动重连不弹主机指纹确认框(宿主)**:睡眠唤醒后所有标签一起重连、隧道按退避重拨时,服务器换了钥会叠出一摞裁决时限无限的模态框,重连一直挂着。用户定:**自动重连直接拒绝**。`ConnectionInfo.IsAutomaticReconnect` → `VelaHostKeyPolicy(automaticReconnect)`:本该弹框问的一律拒绝(照记安全告警 `hostkey-rejected`),不问的两条默认(TOFU、变更阻断)不变,跳板链每一跳同一策略;凭据框、动态码框不在此列。倒计时重连、`ReconnectAllAfterResume` 与隧道自动恢复走 `ReconnectProfileAutomaticallyAsync`;手动重连、改代理后的重试、启动时的自启隧道仍会弹框。指纹被拒(含用户在框里拒绝)不再自动重试(标签与隧道各记一笔,手动连上后解除) —— 原来用户拒绝后过几秒又弹同一个框。
⚠️ 本机环境:互操作靶机与 compose 的 `ssh-2fa` 撞 2224 端口(起着靶机时 `KeyboardInteractiveIntegrationTests` 失败);`velashell-test-shells` 容器早于 b7e55d43 时 `X11_RefusedByServer_KeepsAgentForwardingAndWarnsOnce` 失败,`docker compose -f docker-compose.test.yml up -d --build ssh-shells` 重建即可。
文档:velashell-docs(中英镜像的 SSH 规格 01 §6、05 §4.3、架构 §11.2.28,宿主交互规格「断开连接状态」「隧道」「主机指纹确认」)。

## ✅ 194. 2026-10-11 清掉全部编译警告与 IDE 提示(用户需求)

普通构建只有 4 条;打开 `EnforceCodeStyleInBuild` 全量重编、诊断写进 SARIF 后是 740 条消息级提示,另有 49 条命名问题只在 IDE 里报(`src/`、`plugins/` 没给 IDE1006 设构建级别)。复现:`dotnet build VelaShell.slnx --no-incremental -p:EnforceCodeStyleInBuild=true "-p:ErrorLog=obj/x.sarif%2Cversion=2.1"`,再汇总各工程 `obj/x.sarif` 里没被抑制的结果。
- 修复器(`dotnet format analyzers/style`)做 MSTest 4 断言迁移、集合表达式等。⚠️ 产物要逐处看:它会丢字段注释、造出怪异的半自动属性与 `s_myChars` 这类名字、把「方法可以为同步」改成返回 `Task.CompletedTask`(应为 `void`)。
- 手改:IDE1006 命名;测试方法名开头是小写英文的 47 个改大写(如 `Ppk截断到任意位置只报私钥异常`、`Sftp_server起不来时报出退出码与它的stderr`;`S_REPLY…` 前加「服务端回的」,免得被当成 `s_` 前缀)。CA1068 把 `CancellationToken` 挪到最后,调用点改具名实参(挪位后按位置传的会编译失败,不会悄悄绑错)。两处误报:XAML 引用的 `ShortcutsPage.Recorder_GestureCaptured` 加带理由的 `SuppressMessage`;测试经反射读的 `_suggestionTask` 改成 `internal` 属性 `SuggestionTask`。
- MSTest:各工程显式写 `[assembly: DoNotParallelize]`,Ssh.Tests 写与 `test.runsettings` 同值的 `Parallelize(Workers = 2, Scope = MethodLevel)`;⚠️ 写出来之后 MSTEST0074 指出两条用例在方法级并行下改进程环境变量 —— 一直潜伏的竞态,加 `[ResourceLock(WellKnownResources.EnvironmentVariables)]`。按平台 `Assert.Inconclusive` 早退的 20 处换成 `[OSCondition(…)]`。
- `.editorconfig` 关掉 RXUISG0016(ReactiveUI 对每个普通属性都建议换通知属性,被点名的 75 个几乎都不需要通知)。

## ✅ 195. 2026-10-11 SSH 库:释放传输时还有读写在途,缓冲等它们退出了才还(CI 偶发失败的根因,用户需求)

**起因**:#607 的 macOS CI 上一条 `HandshakeTests` 读到的服务端 KEXINIT 被截短,别的平台都过、本机复现不了。
**根因**:`SshPacketTransport.DisposeAsync` 当场完成 reader / writer,`StreamPipeReader` 把 64 KB 读缓冲还回 `ArrayPool<byte>.Shared`,而另一个任务还停在读里 —— 数据一到照写,写进别人刚租走的数组;写端同理。生产里收发循环到点没退出(Windows 上 ProxyCommand 的匿名管道)也走这条路;§171 记的长尾偶发可能有一部分就是它。
**改法**(规格先行):`Transport/TransportSideLifetime` 给读端、写端各记在途调用,由最后离开的一方收尾;`DisposeAsync` 对有在途调用的一端只取消挂起的读 / 刷出、不等。被取消的读以 `ObjectDisposedException` 退出(读循环原来不看 `IsCanceled`)。⚠️ 密码套件与压缩器跟着各自那一端释放 —— 原来当场清零接收套件的密钥,在途的解密会算出一个假的 MAC 错误。
文档:velashell-docs(中英镜像的 SSH 规格 01 §4「释放」、05 §4.4,架构 §11.2.29)。

## ✅ 196. 2026-10-11 AI 插件:HttpListener 的接收循环停机时丢唤醒(macOS CI 上 UI 测试整组挂住的根因,用户需求)

**起因**:#608 的 macOS CI 测试步骤 55 分钟没结束(平时整个作业 7–8 分钟),挂住的是 `ChatPanelViewUiTests` 那组。
**根因**:.NET 托管实现的 `HttpListener`(macOS / Linux;Windows 走 HTTP.sys,没有这段代码)有丢唤醒窗口:`BeginGetContext` 不拿锁读一眼状态就把等待项排进队列,`Close` 先清空已有的等待项、最后才把状态改成「已关」,夹在中间进来的那次 accept 永远不完成。测试桩的 `Dispose` 先取消、再关监听,然后**在 UI 线程上同步**等服务循环 —— 撞上就永远堵住。⚠️ 生产里同一机制:停 MCP 服务(`McpEndpoint.StopCoreAsync`,还占着 `_reloadGate`)与停企业微信渠道(`WeComChannel` + `ChannelHub.StopAsync`)都可能永远挂住。
**改法**:`Interop/HttpListenerExtensions.AcceptAsync(listener, token)` = `GetContextAsync().WaitAsync(token)`,停机或监听已关时返回 `null`,退出由令牌决定;⚠️ Windows 上关到一半时取请求报 `InvalidOperationException`,同样当作已关。`McpEndpoint`、`WeComChannel` 改用它;测试桩改成只实现 `IAsyncDisposable`(269 处改 `await using`)。WSL 的 Debian 上直接 `await GetContextAsync()` 1400 轮挂住 5 轮,`AcceptAsync` 3000 轮 0 轮。
文档:不涉及(对外行为不变)。

## ✅ 197. 2026-10-12 内置 X 服务端与 SSH 库:gnome-calculator 一打字就退出;开着压缩时起 GTK 程序整条 SSH 断开(用户反馈)

**起因**:远端 `gnome-calculator` 里用本机输入法打字即以 `BadMatch`(`request_code 93`)退出;之后再起计算器,SSH 每次连上 1.4 秒左右就断开。是两个互不相干的缺陷。
**一、打字就退出(X 服务端)**:GTK 打字时藏指针,GDK 的隐形指针是 1×1 像素图同时当 source 与 mask、热点 (1, 1)、不设错误陷阱;热点正落在右下边界上,`CreateCursor` 回 BadMatch,GTK 就退出(敲任何键都会,与中文无关)。改为热点落在右边 / 下边边界线上照收,越界的照旧 BadMatch;mask 全为 0 的光标按隐形指针(`Hidden`)交给宿主。RENDER 的 CreateCursor 仍按严格规则。
**二、开着压缩时 SSH 断开(SSH 库)**:连 OpenSSH 9.6 复现,以「读取 string 需要 32768 字节,只剩 32759 字节」断开。根因在 `ZlibCompressor.Decompress`(10-09 换成 `ZLibDecoder` 时引入)按「输入喂完」收工:OpenSSH 每个报文末尾是只有几个位的部分 flush,输入可能已全进解码器、而 16 KiB 输出缓冲恰在最后一个匹配中间用满,压着的 9 字节留给了下一个报文。改为输入喂完后只要还报 `DestinationTooSmall` 就接着取,上限照旧。⚠️ 本库自己的压缩器以同步 flush(`00 00 FF FF`)收尾,往返用例永远测不出来 —— 回归用例照 RFC 1951 手搭固定哈夫曼编码的流。
⚠️ 本机 `PrivateKeyWriteTests.真ssh_keygen读得出本库写的私钥` 失败与改动无关:`%TEMP%` 的 ACL 带着别的用户组,Windows 版 ssh-keygen 拒读私钥。
文档:velashell-docs#115(中英镜像的 X Server 架构 §7「字体」与排障「已修的缺陷」、SSH 规格 01 §6、SSH 架构 §11.2.30)。

## ✅ 198. 2026-10-12 上传选择框:路径栏能直接输入;敲文件名开头即定位(用户需求)

上传选择框(`LocalPathPickerDialog`)照资源管理器补上两样。
**一、路径栏直接输入**:手势与远端浏览器的路径栏(#226)一致 —— 铅笔按钮、点面包屑后面的空白或 `Ctrl+L` 切成输入框,回车跳转、Esc / 失焦取消。`LocalFilePaneViewModel.ResolvePathInput` 认两头的双引号、`%VAR%`、`~`、相对**当前目录**的路径与 `..`;只写 `D:` 当作 `D:\`;用双参的 `Path.GetFullPath`,相对路径绝不落到进程工作目录。输入的是文件时进它所在目录并选中它。⚠️ 回车与 Esc 必须在输入框上就地标成已处理:漏到窗口上,回车会触发默认的「上传」把选着的东西传走,Esc 会关掉整个选择框。
**二、键入定位**(`Views/TypeAheadSelection` 接线 + `Views/TypeAheadSearch` 纯逻辑):挂在整个窗口上收。规则照资源管理器:1 秒内连敲攒成一个前缀;单个字符从当前项的下一项找起、到底绕回(连按同一字母即在同字母开头的项间轮转),多个字符从当前项本身找起;不分大小写,`..` 不参与;定位到的项单独选中、滚进视口、焦点跟过去。否决 Avalonia 自带的 `IsTextSearchEnabled`:总从第一项找起、不轮转、不滚动、焦点不跟。
**三、列表里的回车**:⚠️ `ListBoxItem` 自己把回车标成已处理,默认按钮收不到;列表上以 `handledEventsToo` 收回车,规矩同双击:只选着一个文件夹(含 `..`)时进去,选着文件或多项时确认上传。
⚠️ CI 暴露的测试坑:假文件系统不分大小写而视图模型按平台比较文件名,输入 `FXYC.txt` 在 Linux / macOS 上选不中。顺带修 `GlxTests` 双缓冲交换用例的旧偶发:回复在一批请求中途就发出、损伤要放锁后才交,映射窗口的整窗损伤被算进了第二帧。
文档:velashell-docs#116(中英镜像的「交互与界面规格」§6 与「快捷键参考」SFTP 文件管理器一节)。

## ✅ 199. 2026-10-12 IPv6 主机地址:编辑器、命令行拉起、导入与各处显示统一认 IPv6(#611,用户反馈)

**根因**:用户报的 `Can not parse port number from 'tzraeq@xxxx:…'` 出自 1.6.0 之前的 Tmds.Ssh(宿主拼 `用户名@主机` 交给库拆,IPv6 的冒号被当成端口分隔符)。换成 VelaShell.Ssh 后主机与端口分开传,**裸 IPv6 直连已经能用**;但还有几处不认:命令行 / `ssh://` 拉起(`XshellLaunchParser`)按最后一个冒号拆,`2001:db8:0:1::10:20` 读成另一个地址的 20 端口,**不报错、连到了别处**;库的目标串 `SshConnectionOptions(string)` 同样;主机栏写 `[v6]:port` 时端口被默默丢掉、方括号经跳板原样发给服务端、主机密钥与裸地址记成两台;拼 `host:port` 的二十来处不加方括号。
**改法**:
- Core 新增 `Net/HostAddress`(`TrySplit` / `Normalize` / `FormatEndPoint` 等):不带方括号又有不止一个冒号的整段是主机、不拆端口,与 OpenSSH 同一个口径;库的 `SshConnectionOptions(string)` 照此改。**配置里一律存不带方括号的裸地址。**⚠️ 不用 `IPAddress.TryParse` 判断:Windows 上它认不出按网卡名写的 zone id(`fe80::1%en0`),还会丢掉 `[::1]:2222` 里的端口;改用与库的 `SshEndPoint` 同一个「含冒号就是 IPv6」。
- 编辑器按「去掉方括号、方括号后的端口取代端口栏」保存,写坏了在栏下提示、按钮灰着;拉起与各类导入都去方括号;早先存下的配置在连接时兜底(跳板每一跳、FTP、端口转发);显示一律经 `HostAddress`(`HostBracketConverter`)。⚠️ X11 拖放按连接名找会话的两处(`VelaSshClientWrapper` 的 `target` 与 `SftpXServerDropUploader.LabelOf`)要同一个写法。
- **刻意不动**持久化的键(主机密钥的文档 Id、最近连接的键、外部拉起的 `TrustKey`)与几处缓存键:端口在最后一个冒号之后本来没有歧义,改写法等于让已存的记录全部对不上。

真实对端 `::1` 跑整组 `Interop` 与 `127.0.0.1` 结果一致(`Start-TestServer.ps1` 的主机证书主体补上了 `::1`)。
⚠️ CI 暴露的旧坑:`AvaloniaXServerHostUiTests` 偶发跨线程访问异常。根因在宿主:Linux 上激活 X 窗口时后台读键位表,续体在线程池上读静态的 `Dispatcher.UIThread`;Avalonia 12.1.4 的 headless 隔离在两条用例之间把 `s_uiThread` 置空,此时**由哪条线程读就在哪条线程上新建一个、并认作 UI 调度器**。改为在 UI 线程上取下调度器、续体投回这一个。
文档:velashell-docs#117(中英镜像的「交互与界面规格」连接目标一节、「Xshell 兼容登录」、「会话导入」与 SSH 库「上手」)。

## ✅ 200. 2026-10-12 SFTP / FTP 文件栏:本地栏的路径栏能直接输入;本地栏与远端栏都能敲文件名开头定位、回车进文件夹(用户需求)

§198 的路径输入与键入定位推广到双栏文档的本地栏、远端栏(含终端侧边栏的文件浏览器)。
**一、本地栏的路径栏**:抽成共用控件 `Views/LocalPathBar`。`Ctrl+L` 由宿主在自己身上以隧道登记 —— 选择框管整个窗口,本地栏只管焦点在本栏时,焦点在远端栏时由它自己的路径栏接。
**二、两栏的键入定位**:`TypeAheadSelection` 接到 `LocalFilePaneView` 与 `FileBrowserView`,范围只在本栏。修了两个「进了目录就不灵」的问题:⚠️ 前缀还带着进目录前敲的字 —— 列表行整批变化时清掉前缀;⚠️ 行整批换掉时焦点所在行被拆、键盘焦点没了着落 —— `ListBoxFocus.KeepFocusAcrossReloads` 把焦点还给视口里的一整行,**不滚动**(最初给「实现出来的最上面一行」,刷新后被拽上去 27 像素)。
**三、回车进文件夹**:只选着一个文件夹(含 `..`)时走与双击同一个 `ActivateCommand`;文件上的回车不做事 —— 远端双击文件是下载并打开,不该被一个顺手的回车触发。
文档:velashell-docs#118(中英镜像的「交互与界面规格」§6 与「快捷键参考」SFTP 文件管理器一节)。

## ✅ 201. 2026-10-12 内置 X Server 的帧率与跟手程度:搭测量台、修 SSH 自适应窗口、宿主保温避开 Avalonia 的渲染停顿、RANDR 报真实刷新率(用户需求)

用户觉得内置 X Server 刷新率和跟手感不太好,要求用 Docker 测试、调试、验证能不能做到高帧率低延迟。
**一、测量台**(`scripts/xserver/perf/`):镜像 `velashell-xperf`(Xlib 探针 `xprobe.c`、glxgears、GTK 演示、sshd、netem);`perf.cs` 起内置服务端 + 真实的 `AvaloniaXServerHost`,直连或 `--ssh` 经 X11 转发接入,注入指针 / 按键、量到损伤报出为止。⚠️ **一定要用 Release 量**:Debug 下 glxgears 1280×720 只有 40 帧,Release 227 帧。
**二、Docker 直连(服务端 + 宿主本身)已经够快**,不是瓶颈:注入到损伤 p50 0.4 毫秒,宿主每秒取满 60 帧。
**三、经 SSH 的 X11 转发只有 9 帧:SSH 库的自适应窗口长不上去**(`src/VelaShell.Ssh`,velashell-docs `ssh/spec/05-connection.md` §3.3、架构 §11.2.31):通道窗口一直停在 256 KiB(裸吞吐 29–59 MB/s,固定 8 MiB 时 675 MB/s)。扩窗只认「见底」(到包时剩余 ≤ 1/8),而回补在半窗处就发出,数据陆续到达时永远到不了 1/8。改为再认「**空等**」:读空之后等下一包的最长一段占最近两轮的 1/4 以上(只等带宽时只占「报文 / 窗口」),4 个报文以上的窗口才用,扩过一次上一轮的空档作废(`CreditIdleDetector`)。⚠️ 别用链路模拟测「带宽是瓶颈时窗口不白长」:macOS CI 上模拟器的计时器醒晚了会成批补发,判据照规矩认成空等 —— 改成按到达序列逐拍核对的确定性用例。修复后经 SSH 整窗动画 64.5 帧、glxgears 60.8 帧;加 15 毫秒单向延迟时我们这一侧只多 1 毫秒,帧率再降是客户端每帧的同步往返,服务端省不掉。
**四、偶发的「卡一下」在 Avalonia,宿主给 X 窗口保温**(`XNativeWindow.cs` 的 `XSurface.KeepWarm` / `Warm`):Windows 上零星刷新时每隔十几到几十秒有一次 400–490 毫秒的停顿 —— Avalonia 的渲染计时器线程(`WinUiCompositorConnection.RunLoop`)停在 `GetMessage` 等系统合成器确认上一次提交。与 X 无关:纯 Avalonia 12.1.4 程序同样会;WinUI 合成、DirectComposition、低延迟 DXGI 交换链都会,AngleEgl + RedirectionSurface 3 分钟 1 次,Wgl + RedirectionSurface 0 次。**每帧都有东西画时 3 分钟 10776 帧一次也不会**:触发的是「连着出帧之后转成空拍」那一次的确认。宿主改为:窗口激活、指针移进来,或用户在里面输入(按键、指针、滚动、输入法)时保温 2 秒 —— 没有新损伤的帧也 `InvalidateVisual`(不碰像素锁、不传纹理)。激活与移进来当场转起来;按键只把保温往后延(抢先空画一帧会让闲了几秒后的第一个键 p50 从 0.4 毫秒变成 14 毫秒)。⚠️ 保温 300 毫秒反而更糟(转空拍的次数多了),1 秒 0 次;只按用户输入保温、不按损伤 —— 光标闪烁、时钟这类刷新没人在等,按它们保温只会一直 60 帧重画。代价是保温期间新损伤平均晚一帧左右。真实宿主打字节奏 `xprobe key` 与 xterm 回显各 600 键:不保温各停 5 次(最长 214 毫秒),保温 2 秒都是 0 次。整个应用同样会撞上这个停顿(剩余项见 feature-plan.md)。
**五、RANDR 报真实的刷新率**(`Services/XServer/DisplayRefreshRate.cs`):原先一律报 60,120 / 144 Hz 的屏上 GTK、Qt、浏览器也只画 60 帧。Windows 上经 HMONITOR 用 `EnumDisplaySettingsW` 读当前显示模式;目前只 Windows,macOS / Linux 仍报 60(剩余项见 feature-plan.md)。
**六、宿主一侧的计量**(`Services/XServer/XServerHostMetrics.cs`,仪表源 `VelaShell.XServer.Host`):`frames`、`frames.busy`、`damage.latency`(`TopLevelDamaged` 记下这批损伤最早报来的时刻,一路带到 `XSurface`)、`read.duration`。没人订阅时不记;窗口打开之前攒的损伤不算延迟。
文档:velashell-docs#119(中英镜像的 SSH 规格 05 §3.3、SSH 架构 §5.5 / §7 / 性能表 / §11.2.31、X Server 架构 §9 测量台 / §10 排查记录、排障「操作卡」)。

## ✅ 202. 2026-10-13 上传选择框:窗口放大、可拖边缩放;右上角切换列表 / 图标视图(用户需求)

**起因**:672×552 偏小,Windows 上拖不动边框(对话框没挂缩放抓取区);用户要资源管理器「中等图标」那样的显示,切换做成 Linux 文件管理器头栏常见的「列表 | 网格」分段按钮。
**一、窗口**:默认 920×640、最小 560×380,挂上与工具窗口同一套 `ResizeGrips`;尺寸与视图关窗时记进 `WindowLayoutStore`(键 `uploadPicker`,所有会话共用一条)。
**二、视图切换器**:标题栏右上角、关闭键左边的两段分段控件(选中段 `VelaAccentDim` 底 + `VelaAccent` 图标)。
**三、图标视图**:新增虚拟化的等尺寸格子面板 `Views/VirtualizingTilePanel` 与纯几何 `Views/TileGridMath`,两种视图**共用这一个面板**(列表 `ItemWidth=NaN`、行高 28,图标 108×96),切换只换项模板与 `tiles` 类。
- ⚠️ 不在两种视图间换 `ItemsPanel`:Avalonia 12.1.4 换面板时 `VirtualizingPanel.Detach` 只清可视子项,旧容器还挂在逻辑子项里,来回切越攒越多。不用 `WrapPanel`:它不虚拟化,几千项的目录会一次建出几千套控件。
- 方向键按格子走;列表视图的 PageUp/PageDown 因此从「只滚动」变成「移动选中项」(资源管理器同款)。
- 文件图标上印大写扩展名(不超过 4 个字符,更长时不印 —— 截断的比不写更误导);格宽定 108px,100px 时 `archive.tar.gz` 的最后一个字母挤到第二行。⚠️ 悬停样式要排在选中样式之前(同优先级后写的赢)。
- 框选:`MarqueeSelection` 遇到格子面板时由面板按二维几何给命中项;⚠️ 点要加上滚过的距离换算到内容坐标 —— 自动滚动时偏移刚改、布局还没跟上,问面板的实时位置拿到的是旧的。远端栏、本地栏仍按行判定。
文档:velashell-docs#120(中英镜像的「交互与界面规格」§6「上传选择框」)。

## ✅ 203. 2026-10-13 AI 插件:OpenAI Responses 的工具循环不再拿 previous_response_id 续接 —— 中转站上 Agent 一调工具就 400(用户反馈)

**现象**:自定义供应商 Routin(`https://api.routin.ai/v1`,Responses 协议)反复报 `HTTP 400 (invalid_request_error: invalid_request_error) OpenAI response error: {"error":{"message":"Upstream rejected the request",…}}`。
**根因**:逐项打真端点排查(单发 `max_output_tokens=256000`、内置 `web_search`、函数工具、system 角色、`store` 都是 200;流里的 `codex.rate_limits` 事件说明它背后是 ChatGPT 的 Codex 后端)。出错的是工具结果那一步:M.E.AI 的 Responses 适配器在请求没写 `store:false` 时把 response id 当成 `ConversationId` 交上来,`FunctionInvokingChatClient` 于是第二步只发 `previous_response_id` + `function_call_output`;上游没存上一步,回 `previous_response_id is not available for this user`。自定义供应商的 `StoreResponses` 默认 true(不发 `store`),所以**接中转站的 Agent 模式只要模型调一次工具,这一轮就挂**;目录里的 Codex 订阅关了 store,一直没撞上。
**改法**:
- 新增 `Chat/StatelessResponsesChatClient`,在 `AiSettingsStore.CreateClient` 的 Responses 分支包在适配器外面,抹掉响应上的 `ConversationId`:工具循环和其它协议一样自己攒历史、每步整段重发。聊天面板、IM 桥接、探活、压缩都走这一处。`store` 字段本身不动。真端点核对:不抹 400;抹掉 200,开思考(medium / detailed)同样 200。
- `SteeringChatClient` 不再按「消息里还有没有系统提示词」猜循环是不是只发了增量,已送达的插话每步都补齐。⚠️ 那个判据在 Codex 那条路上本来就是错的:系统提示词挪进了 `instructions`,消息里没有 system,插过的话从第二步起就丢了。
- 回归:`ResponsesWireTests.ToolLoop_ResendsTheWholeHistory_InsteadOfChainingOnAStoredResponse` 经真实的 `CreateClient` 打本地桩(去掉包装层时它红,请求体正是 `previous_response_id` + 工具输出);`SteeringTests` 的「服务端存着会话就不补」换成「没有系统提示词也照样补」。
文档:无需改动(velashell-docs 没有描述过这条续接行为);插件 README「端点怪癖」一节补了一段。

## 204. 2026-10-13 AI 插件:404 提示检查地址、模型和协议,不再只显示 page not found

`ApiErrorText.Describe` 为模型请求的结构化 HTTP 404 增加五语排查提示,放在原始错误详情前面:
检查 Base URL、模型 ID 与协议支持;Responses 须由供应商提供 `/responses`,只有
`/chat/completions` 时改选 Chat Completions。说明健康检查也是真请求,404 不等于 Key 无效。
聊天错误卡、设置页测试、逐 Key 检测和 IM 模型错误共用提示;全局「全部检测」的结果灯与状态文字
悬停也显示具体原因,沿用凭据遮罩。只改展示,不改协议、重试与健康冷却规则。

验证:错误分类与本地化等 33 项、相关设置 UI 与重试分类等 190 项通过,无跳过;
三种协议经真实 SDK 接收本地 404,并覆盖嵌套异常、
外层状态优先和正文假含 404 的边界。临时 Avalonia/Skia 冒烟经 9 次本地 HTTP 请求验证聊天错误卡、
模型测试、逐 Key 与全局检测,后三者核对五语,并检查聊天、模型测试与 Key 悬停的实际截图。
文档:补充插件 README;velashell-docs 未规定错误展示文案,原有协议与健康检查行为不变。

## 205. 2026-10-13 AI 插件:新增供应商选择已有类型时创建独立实例

根因:新增与管理登录共用目录页,选择类型默认编辑最近一份;克隆入口此前只用于 OAuth 第二账号。
目录页区分新建与编辑意图,新建复用已有克隆逻辑:配置作底稿、供应商及模型重新生成 ID,
不预填旧 API Key、不复制补充密钥槽或令牌。取消回类型列表,保存后编辑新实例;
复用已打开窗口时显式回到新建入口。「管理登录」与「添加 API Key」保持原来的逐实例/密钥槽语义。

验证:供应商目录 UI 与相关对话框回归 172 项通过,无跳过;独立原生 Avalonia 窗口走设置页新增、
选择已有类型、保存、复用编辑窗口再次新增和取消,核对持久化实例、模型 ID、旧配置与机密隔离,
并检查实际渲染的新建表单截图。临时验证程序使用隔离数据与本地 HTTP 端点。
文档:更新插件 README;velashell-docs 的 AI 蓝图与进度文档未规定此入口的实例选择行为,无需变更。

## 206. 2026-10-13 AI 插件:模型清单按提供商实例独立维护

根因:同类型新实例复制了源实例的完整 Models 与 AvailableModels,后续拉取只追加;
目录页还按地址借用源实例候选,同地址但不同凭据也混在一起。
新实例仅保留接入配置与首模型表单底稿,不复制完整清单或折叠状态,拉取与候选显示只认当前实例。
成功取得端点清单后清理该实例未定制的过期自动模型,保留手工配置、用户定制和网络期间的编辑;
列表、当前模型与故障转移链原子落盘,不触碰同类型的其它实例。端点失败或目录回落时不删除旧模型。

验证:新增隔离回归修复前失败、修复后通过;目录/设置 UI、端点解析、规格与存储相关 559 项通过,无跳过。
独立原生 Avalonia 窗口经同一个本地 /v1/models、两把不同 Key 创建与刷新实例,
核对各自列表、手动配置保留、持久化、当前选择与故障转移链,并检查加载宿主主题令牌后的实际截图。
文档:更新插件 README;velashell-docs 的 AI 蓝图与进度文档未规定模型清单的实例复制及同步行为,无需变更。
## ✅ 207. 2026-10-13 SFTP 传输进度与速度:按远端确认计量,排空时继续更新(用户反馈)

**起因**:慢链路上传时进度迅速升到 90% 多、速度虚高,之后在 90% / 99% 长时间不动。
**改法**:宿主上传包装层按 `DurableLength`(从开头连续收到 WRITE 成功确认的字节数)上报,不把本地攒块或流水线入队量当完成量;源读完后的 `Flush` 等待期间每 200ms 采样。SFTP 上传 / 下载续传先核实起点再开始计时,百分比保留已有前缀、平均速度只算本次新增字节。最后 ACK 到齐时绕过 100ms 节流,100% 只表达字节确认完成,任务成功仍等可选 fsync / CLOSE;回调抛错时停止等待并观察冲写任务,已上线 WRITE 仍按流的关闭期限排空。UI 修正低于 1 B/s 时单位索引为负导致整帧进度丢失。
**验证**:核心续传 / 进度 / 中断映射 143 项、UI 与 headless 消费 58 项、SFTP 协议 126 项通过;Debug 严格重建零警告。真实 OpenSSH 六项全部执行通过(无跳过):经本机限速代理,1 MiB 后缀全部提交后扣住 ACK,普通上传仍报 0、4 MiB 前缀续传仍只报前缀;放行后 Flush 多次递增上报。断链时远端长度大于确认前缀,按异常的精确确认点恢复后 SHA-256 一致;取消仍报取消,Flush 回调失败保留原异常且连接仍可用。该用例验证确认语义,不是数百 MiB 文件或 3 MB/s 环境的性能测量。
文档:velashell-docs 中英镜像的「交互与界面规格」§9。

## ✅ 208. 2026-10-14 SFTP 双栏:连接级本地 ↔ 远程目录收藏(#621,用户需求)

为双栏文件文档补上 WinSCP 风格的目录收藏,解决用户反复切换同一组本地目录与远程目录的问题。

- **数据边界**:`SessionProfile.DirectoryBookmarks` 保存收藏列表;每项是稳定 Id、名称、本地绝对路径与远程绝对 POSIX 路径。旧配置没有该字段时自动得到空集合,收藏随连接配置保存并随连接导出;`SessionProfile.Clone` 深拷贝列表与条目,不会共享可变对象。
- **路径与数据安全**:保存时展开本地相对路径 / 环境变量、规范化本地根与远程 `.` / `..` / 重复分隔符,限制名称与路径长度;同一连接按规范化的本地路径 + 远程路径去重。收藏保存不访问目录,不存在的目录会保留到应用时再报告错误。
- **交互**:`SftpDocumentView` 文档工具条提供「收藏当前目录」与「目录收藏」;列表可直接应用、编辑、删除,另有独立管理窗口。应用收藏并行导航两栏,任一侧失败不回滚另一侧,两侧各自保留错误状态。
- **持久化语义**:已保存连接通过 `PersistProfileIfSavedAsync` 复用现有安全保存流程;最近连接重建的临时配置只在当前文档有效,不会被收藏动作悄悄保存为永久连接。持久化失败时恢复修改前的列表并提示用户。
- **适用范围**:单栏本地 ↔ 远程文档共用 SFTP / FTP / FTPS / 插件文件协议;双远程配对、访问历史、分组与快捷键仍属于 `feature-plan.md` 的后续范围。
- **验证**:Core 模型、旧 JSON 兼容、配置深拷贝、路径规整与五份资源测试通过;`dotnet build VelaShell.slnx --no-restore` 通过。
文档:已同步更新 velashell-docs 中英镜像的「SFTP 双栏与 WinSCP 差距分析」与「交互与界面规格」（待两个仓库分别提交）。

## 209. 2026-10-14 更新下载:状态行显示速率,停滞另有提示

**问题**:关于页下载更新时状态行只显示 `正在下载更新… 42%`。网络慢的时候百分比能几十秒不动,
用户分不清「还在下,只是慢」和「已经卡死」。

- **进度链路换类型**:`IProgress<int>` → `IProgress<UpdateDownloadProgress>`(已下载字节、整包字节、
  当前速率;`Percent` 由字节数算出)。`IUpdateSource.DownloadAssetAsync`、`IUpdateService.DownloadUpdateAsync`
  一并换掉;`UpdateService` 的「复用已下载包」分支上报 `UpdateDownloadProgress.Completed`。
- **速率用滑动窗口,不用全程均值**:`DownloadRateMeter` 以 3 秒为窗,基线钉在「最近一次收到字节」的
  时刻,窗口走完就地滚动。前段快后段慢时全程均值既不解释「现在慢」也不解释「刚才快」。时钟由构造参数
  注入,测试瞬间跑完整个时间窗。整窗零增益、且距上次收到字节已过一窗时速率归零。
- **汇报按时间兜底**:`GitHubReleaseSource` 原来只在百分比变化时汇报,现在百分比不变时每 250ms 也补报一次
  —— 用户要看的是速率,而慢速下载的百分比能几十秒纹丝不动。单连接与分段并发两条路径共用同一套节流。
- **心跳让停滞显形**:下载线程只在收到新字节时才汇报,网络一卡就没有任何回调,界面只能停在最后一个速度值上。
  `SettingsViewModel` 在下载期间起一条 500ms 的 `PeriodicTimer` 心跳,驱动 `UpdateDownloadTracker.Tick`:
  速率保鲜期 1 秒,过了归零;连续 10 秒没有任何新字节则 `IsStalled`,状态行换成带提示的文案。下载结束
  (`downloadEnded`)后不再接受排队中的迟到汇报,「已就绪,可重启」不会被盖回「正在下载」。
- **文案与布局**:删掉只带百分比的 `SetAbout_Downloading`,换成 `SetAbout_DownloadingSpeed` 与
  `SetAbout_DownloadStalled`(五份 resx 齐)。状态行从按钮行里挪出来独占一行并开 `TextWrapping` ——
  带速率与停滞提示的文本很长,挤在按钮行里会被截断,也会把按钮挤出可视区。字节 / 速率 / 停滞时长的
  格式化直接复用 `TransferItemViewModel` 里已有的 `FormatBytes` / `FormatSpeed` / `FormatTimeRemaining`。
- **验证**:新增 `DownloadRateMeterTests`(假时钟:首调只立基线、窗口内折算、窗口滚动后能掉到 0、
  滚窗瞬间不闪 0、慢速报非零)与 `UpdateDownloadTrackerTests`(字节单调不减、未知总量百分比为 0、
  心跳把过期速率清零、新字节撤销停滞判定);`GitHubReleaseSourceTests` 加一条,断言每条汇报都带得上
  整包字节数且字节单调不减。`VelaShell.Tests` 全量 2069 通过 / 46 跳过;五份 resx 齐套由
  `LocalizedKeyUsageTests` / `UnusedLocalizedKeyTests` 守住。
- **文档**:同步到 velashell-docs `zh|en/host/消息中心与资讯源.md`(message-center-and-feed.md)

## 210. 2026-10-14 Tailscale SSH：none 认证、手动 check 与运行期间策略切换

- **认证与数据边界**：`AuthMethod.Tailscale=4` 末尾追加，旧枚举值不变。配置、补用户名登录与批量认证提供 Tailscale SSH；只使用远端系统用户名，不准备密码、私钥、证书、Agent 或键盘交互材料。统一 `ClearTailscaleCredentials` 覆盖 resolver、仓储、CSV/JSON 导入导出和批量覆盖，清掉旧秘密与共享引用；共享凭据不支持此方式。主机指纹、代理与跳板策略不变。
- **check 生命周期与安全**：每次实际认证跳创建独立 AttemptId、取消与窗口生命周期。只处理 USERAUTH_BANNER 的合法官方 HTTPS 链接，普通 banner、前导行与 DEBUG 在进入缓存前脱敏；专属异常不带原始服务端链。owned 非模态等待窗只在用户点击时打开浏览器，允许复制、取消/Esc/关闭；显示/更新立即返回，SUCCESS 继续原握手并结束窗口，无「我已验证」按钮。
- **动态策略与恢复**：accept/check 由服务器当前应答决定，不保存模式、批准结果、checkPeriod 或链接；同一配置/进程可在连续握手中经历 accept→check→accept。等待中更新 URI 使用同一窗口，旧动作不串到其它尝试；现有连接由服务器决定是否终止。终端/SFTP 的当前跳失败不进入目标密码重试，终端和隧道认证暂停阻止唤醒、旧倒计时、代理变更与刷新反复触发验证；手动尝试始终允许，成功才解除。隧道先完成认证再移除旧服务记录，取消保留定义。
- **已执行验证**：步骤1 Core 114、Infrastructure 57、宿主 123 项；步骤2真实环回协议/动态握手/URI 66、banner 8、headless 等待窗 10 项；步骤3消费者 211 项全部通过，无跳过。Debug 全解构建成功；全量回归 7848 通过、163 跳过、1 失败：`PrivateKeyWriteTests.真ssh_keygen读得出本库写的私钥` 被 Windows OpenSSH 因临时文件继承的 UNKNOWN SID ACL 过宽拒绝。未修改无关权限或测试；TestContext 的环境早退未计作互操作验收。
- **桌面与真实服务边界**：隔离数据启动实际应用，核对五段认证选择及材料显隐；原生等待窗检查英文暗色、中文亮色实际截图与读屏名称，显示不等待验证、程序性 End 收窗且不触发用户取消。窗口输入用明确的演示链接，不是实际 Tailscale 登录。实施初期 Linux peer 的 TCP22 与 tailnet ping 无响应，未进入该轮认证；后续用户已明确确认实际桌面运行连接 Tailscale 测试通过，据此记录实际连接通过，不再把旧节点不可达作为当前阻塞。该确认不扩大为未逐项记录的 check/MFA、管理面板热变、checkPeriod 或撤销权限测试均已验收，也不用协议模拟冒充真实策略验收。
- **文档**：中英文「交互与界面规格」及「会话导入」已同步，相对链接检查通过；[velashell-docs PR #125](https://github.com/VelaShellLabs/velashell-docs/pull/125)。`feature-plan.md` 无对应 Tailscale 待办条目，保持不变。

## 211. 2026-10-15 选区同文高亮(#628 第 2 项)

- **行为**:双击选词或完成单行线性拖选后,在当前实际可见行内高亮不区分大小写的相同子串。保留原真实选区与复制语义;矩形、多段、跨物理行及纯空白不触发,尾空格按原文匹配。
- **实现**:`BufferSearch.FindVisible` 复用原搜索的屏幕列映射,只遍历 `_screenToAbs`,不扫描整段历史或折叠隐藏行。选区匹配与 Ctrl+F 状态分离,复用主题匹配色,真实选区不被它覆盖;Ctrl+F 导航行为不变。
- **刷新**:选区提交后延迟到实际渲染帧更新;输出、滚动与折叠只标脏,一帧最多搜索一次,空闲重绘不重搜,滚轮撞边界不重搜。新选区、输入(含旧 WriteInput 和 IME 组字开始)、清除选区、resize 和主/备用屏切换清理匹配;模拟器用屏幕代次识别同一批内切入又退出的情况。按行的有序命中用游标推进,避免密集短词逐格从头遍历。
- **验证**:Debug 严格构建(`--no-incremental`)0 警告 / 0 错误;终端 548 项与 Skia 渲染 7 项全部通过、无跳过;宿主全量 2100 项通过 / 19 项因平台、发布或 SSH 环境跳过。文档测试设置 `VELASHELL_DOCS_DIR` 指向文档 worktree,确认快捷键文档用例实际执行。新增真鼠标交互、中文/组合字符列坐标、可见范围/折叠、刷新次数与亮暗主题像素测试;未进行真人 SSH GUI 验收。
- **文档**:同步 velashell-docs 中英文「交互与界面规格」§5.2。第 1 项共享凭据填充随后在 §212 实现。
- **复核(2026-10-16,用户要求)**:按「扫可见行、不扫折叠隐藏行、不扩大真实选区或改复制语义、保留 Ctrl+F」四条逐一对照实现 —— 均成立,见下文结论。定向回归 `SelectionMatchHighlightTests` + `BufferSearchTests` 34 项通过。

## 212. 2026-10-15 终端共享密码选择与轻提示(#628 第 1 项)

- **交互(2026-10-16 重做)**:原先的右上角常驻钥匙按钮 + 「选择共享密码…」轻提示 + 二次确认选择器全部删除 —— 常驻按钮与 WindTerm 不一致,用户明确不要。现在识别到提示行就直接在**光标下一行**弹出共享密码列表(`Popup` 锚 `PlacementRect` = 光标格下移一格高,复用补全弹层那套定位),点中一行即读取填入,没有确认步骤。「命令面板 → 填入共享密码」保留为同一入口的手动版。没有默认快捷键,Ctrl+Shift+K 仍清屏。
- **为什么是无焦点、无搜索框的纯鼠标弹层**:列表项不可聚焦,否则点密码会把键盘焦点从终端抢走,而「终端失焦即收口」的规则会在指针抬起前关掉弹层,点选永远落不上;搜索框同理还会把提示符前打的字吞掉 —— 那可能正是用户在手敲密码。故长列表只靠滚动。风险说明移到列表上方常驻(确认步骤没了,这是用户唯一看到的告警)。
- **自动入口**:UI 合批输出通知读取当前光标行,独立保守密码判定排除 OTP/passphrase;只在活动、聚焦、实时主屏弹出。输入/滚动/失焦/失活/断连/Dock 换绑/脱离可视树收口,同一提示开始手输或已收口后不反复重开,换一条提示才重新给入口。
- **读取**:`ListMetadataAsync` 不调用 Unprotect、不携带秘密;`GetPasswordAsync` 只解密选中的密码字段,非密码认证或缺失返回不可用,保护器解密失败原样返回的密文按错误拒绝,合法解密后以 enc1: 开头的密码不误拒。
- **发送**:请求绑定原 tab、bridge、连接代次、当前提示行/列/屏幕代次与编码。异步读取期间输入、提示变化、重连、断连、切活动标签、关闭弹层均拒绝迟到发送;严格编码并拒绝控制字符,仅正文经桥的 TrySendRaw 入队,不经剪贴板/TypedInput/广播/命令跟踪或输入诊断。桥的出站输入代次也覆盖同步输入/注入,最终检查与入队原子化。桥拥有已入队字节,不在调用方提前清零。
- **限制**:SSH 无法证明远端关闭回显,EchoSuppressor 也不是依据;界面明确说明密码仍可能被远端回显至屏幕、日志、录制。未变更静态加密方案或插件自有终端契约。
- **顺带**:`SharedPasswordPickerDialog` / `SharedPasswordPickerViewModel` 删除;`PasswordFill_Hint` / `_Search` / `_Confirm` 三个键从五份 resx 一并移除(`UnusedLocalizedKeyTests` 会拦孤儿键)。视图新增 `RepositoryFactory` 取数口(headless 测试宿主不是 `App`,没有 DI 容器),与 `EnglishInputLocale.InputLocaleSwitcherFactory` 同一套做法。
- **提交与文档**:同步中英交互规格 §5.2.1;提交与 PR 描述不带 AI 工具署名。
- **验证**:宿主构建 0 警告 / 0 错误;`TestCategory=SharedPasswordFill` 32 项全通过(提示识别、真实桥发送、八种上下文变化拒绝、取消/重连竞态、弹层锚点与光标对齐、弹层只在真提示上出现并在错误上下文收敛);Terminal 选区匹配相关 34 项通过;五语言与设计门禁(23 项)通过。未进行真实远端 SSH 人工 GUI 验收。

## ✅ 213. 2026-10-19 终端:apt 装大包的日志不进回滚区,装完往上翻什么都没有(#634,用户反馈)

**根因**:`TerminalScreen.ScrollUp` 只在滚动区域等于整屏时才把滚出去的行存进回滚区。`apt install` 默认开着 `Dpkg::Progress-Fancy`,为了在最后一行钉住进度条,把区域设成「首行到倒数第二行」(`ESC 7` `ESC[0;{行数-1}r` `ESC 8`),整场安装的日志都在这个区域里滚 —— 滚出去的每一行都被直接丢掉,回滚行数调到 3 万也一样。issue 截图里屏幕首行的行号就是 1,也就是回滚区一行都没有。
**改法**:判据从「区域等于整屏」放宽为「区域上边界在首行」,仍只作用于主屏(备用屏容量恒为 0)。区域下方的固定行原地不动、不进历史;上边界不在首行时,滚出去的行夹在上方固定行与区域之间,历史里没有它的位置,照旧丢弃。xterm.js、VTE、Alacritty 都是这个行为。`CSI S`(SU)走同一个函数,口径一致。
**验证**:新增三条用例 —— 照 apt 的原样序列滚 10 行,L0–L6 进历史、进度条行不动;上方有固定行时不进历史;备用屏上设区域滚动不碰主屏历史。修复前第一条失败(回滚区 0 行)。终端 551 项、Skia 渲染 7 项通过;宿主全量 2236 项通过 / 19 项因平台、发布或 SSH 环境跳过(连跑 5 轮,有 1 轮偶发 1 项失败,之后未再复现)。本机 Docker 没在运行,未拿真实 apt 人工验收。
**文档**:与现有文档不冲突,不涉及 velashell-docs。

## ✅ 214. 2026-10-19 CI:共享密码「迟到结果」用例在 macOS 上偶发失败

**现象**:`SharedPasswordFillTests.LatePassword_KeepsPopupOpenAndSaysNothingWasSent` 在 macOS CI 上偶发失败(合并 #636、#639 时各一次;本机全量大约五轮一次),状态行读到空串。
**根因**:用例自身的时序假设,产品行为没有问题。点选后的读取与填入由 `FireAndForget` 发起,迟到的密码到达后,续体要经 UI 调度器排队才执行到写状态行;用例只 `Task.Yield()` 一次就往下走,实际靠后面 `DrainWritesAsync` 恰好多跨一跳才赶上 —— 写入排空得快就先断言了。实验:结果到达后只让一次路就断言,三次全部失败。
**改法**:视图记下最近一次点选发起的任务(`TerminalTabView.PasswordFillForTest`),三条点选用例都等它跑完再断言,不再数 `Yield`。「读取中收口」那条顺带受益:原先「没发出去」的断言可能在迟到结果被处理之前就跑了,等于没验证。
**验证**:`SharedPasswordFill` 32 项连跑 15 轮全过;宿主全量两轮 2236 项通过 / 19 项因平台、发布或 SSH 环境跳过;构建 0 警告。

## ✅ 215. 2026-10-19 共享密码填入:git 的密码提示不弹列表;列表加键盘选择(#638,用户反馈)

**现象**:`git fetch` 走 HTTPS 时的 `Password for 'https://host:8000':` 提示下,共享密码列表不弹;用户同时建议列表支持 ↑↓ 选、Enter 选中、再按 Enter 提交。
**根因**:识别正则里 `password for` 后面写的是 `[^:\r\n]+` —— 对象里不许有冒号,而 git / svn 把对象(URL)整段引起来,URL 自带 `https:` 与端口的冒号,整行就匹配不上。issue 里猜的「只认行尾的 Password」不对:识别本来就要求整行是提示,卡住的是 URL 里的冒号。另有一个同症状的缺口:窄窗格里带 URL 的长提示会被自动换行折开,光标行只剩尾巴,同样认不出。
**改法**:
- 正则:对象整段包在单/双引号里时放行任意字符(含冒号),不带引号时照旧不许冒号。
- 折行:新增 `VelaTerminalControl.GetLogicalLineEndingAt`,沿 `TerminalRow.Wrapped` 往上把折出来的物理行接回整段逻辑行(只在活动屏幕内回溯)。光标行自己是完整提示时也照认 —— `\r ESC[2K` 原地重写续行不会清上一行的折行标志,接起来反而认不出。收口后的「同一提示不再重开」也按同一段逻辑行记。
- 键盘:列表打开时 ↑↓ 移高亮(借 `ListBox` 选中态,`_highlightingPassword` 守卫让它不触发填入),Enter 填入高亮项,不附加回车,填完收口、焦点回终端,再按 Enter 才是提交。打开时不预先高亮:没按方向键的 Enter 原样进终端(用户自己的提交),不会变成「把第一个密码发出去」。读取在途(列表禁用)时吞掉 Enter、↑↓ 不动 —— 连按的第二下若放过去,会抢在密码前面到达远端提交空口令,这次键入还会让在途的填入作废。方向键在密码提示前只会把 ESC 序列塞进口令,所以列表开着时 ↑↓ 一律归列表;打字仍原样进终端。
- 鼠标:按下时先撤掉键盘高亮(列表上的隧道 `PointerPressed`,不置 Handled)。否则先 ↓ 看了一眼、再点同一行时选中态不变、不触发 `SelectionChanged`,这一下就白点了(headless 真实鼠标输入实测复现)。
- 高亮样式与补全弹层同款(bg-active + 2px 强调色左边条);快捷键目录新增「填入共享密码」一组(Down / Up / Enter / Esc,五份 resx)。
**顺带**:`Picker_CancelWhileReading_DoesNotSendLateResult` 原先在窗口第一轮调度之前就喂提示,列表根本没弹,点选什么都没做,「迟到的密码没发出去」是空断言(§214 以为它跟着受益,其实没跑到那条路径)。补一轮调度,并断言弹层确实打开、读取确实发起。
**验证**:新增 7 行提示识别用例(git/svn 引号对象含冒号、`Username for` 不认、尾随输入不认等)与 5 条视图用例(折行提示与陈旧折行标志、↑↓ 只挪高亮不读不发、Enter 填入不带回车且下一个 Enter 才提交、无高亮 Enter 直通终端、读取在途吞掉 Enter、点中已高亮行仍能填入)。逐项回退修复验证用例会失败:回退正则、回退逻辑行、去掉光标行兜底、去掉键位处理、去掉按下撤高亮,对应用例均失败。`SharedPasswordFill` 44 项通过;Debug 全解构建 0 警告 / 0 错误;宿主全量 2256 项通过 / 11 项因平台、发布或 shell 环境跳过;终端 551 项通过;`Doc_ListsEveryCatalogEntry` 指向改后的文档实际执行通过。未在真实 Linux 桌面上对 git 远端做人工验收。
**文档**:同步 velashell-docs 中英文「交互与界面规格」§5.2.1 与「快捷键参考」,[velashell-docs PR #132](https://github.com/VelaShellLabs/velashell-docs/pull/132)。

## ✅ 216. 2026-10-19 跟随终端目录:su / sudo 切进新 shell 后就不跟了(#637,用户反馈)

**现象**:root 登录后「跟随终端目录」正常;`su - node` 之后在终端里怎么 `cd`,文件浏览器都不动;`exit` 回到 root 又恢复。`sudo -i`、`sudo su -`、直接敲 `zsh` 同样。
**根因**:目录上报钩子只在连接时注入登录 shell 一次。新 shell 是一个全新的进程,函数与 `PROMPT_COMMAND` 都不继承(`su -` 连环境变量都清空,只留 `TERM`),于是不再上报。环境变量这条路走不通,只能在新 shell 的提示符前再注一次;难点是"再注"必须确定无疑 —— 往 PTY 里打字,打进的是前台此刻在读的程序,打进 `su` 的密码提示就成了口令。
**改法**:
- `ForegroundShellProbe`(Core):一条 exec 通道上的探针,不碰终端。Linux 读 `/proc`(只用 `read` 内建,只给前台组长 `tr` 一次命令行),macOS / BSD 退回 `ps`;整段包在 `sh -c '…'` 里,fish 登录 shell 也照样跑。登录 shell 靠进程树认:探针沿祖先往上,第一个**恰好**有一个带终端子进程的祖先(sshd 会话进程)的那个子进程;不靠 shell 自报 PID —— 那条上报谁都能在终端里伪造。前台组长是交互式 shell(按 `argv[0]` 认种类;带 `-c` 或脚本操作数的不算)就是它;不是,就看它的后代有没有落在另一个终端上 —— sudo 1.9.14 起默认 `use_pty`,root 的 shell 在新 pty 上,原终端前台是 sudo 自己,要沿过去接着认。认不出来一律当作没有。
- `NestedShellFollower`(宿主):回车(桥上新的 `LineSubmitted`,键入、粘贴、同步输入、快捷命令、共享密码填充都算)之后等输出静默 0.4 秒;收到了上报就说明前台是装过钩子的 shell,不问。没有上报、不在备用屏幕、开关开着,才问一次前台;是没注过的交互式 shell 就按它的种类补注一次(摘历史前缀也按它的种类接)。写出去那一下走 `SshTerminalBridge.TrySendInjection`:从静默那一刻起有任何输入入过队就不发,开窗口与入队同一把锁。等待期间用户按了键、探针往返期间屏幕又动了或者又来了一行,都作废这一轮。每个 shell 进程只补一次。认证后命令(`sudo su -`)是我们自己发的、紧跟在钩子注入后面,它那一行强制问一次,不拿钩子自己的首报当"没换人"。
- 宿主在注入登录 shell 的钩子之前挂上补注(`MainWindowViewModel.ArmNestedShellFollow`),不注钩子的连接不挂;断开 / 重连作废,开关在会话中途关掉也停手。设置页「上报终端工作目录」的说明改为如实描述(五份 resx)。
- 测试靶子 `ssh-shells` 加装 `sudo`(vela-bash 免密)与 `busybox-suid`(Alpine 的 busybox 默认不带 setuid,普通用户敲 su 只会得到 "must be suid")。
**不补的情形**:密码提示、跑着的脚本或命令、REPL 与 vim / less / top、`ssh` 到别的机器与 `docker exec`(那些目录不在 SFTP 看得到的这台机器上)、tmux / screen 里的 shell。代价是在没有上报的回车之后多一次 exec 往返:装了钩子的 bash / zsh / sh 每个提示符都上报,平时走不到;走到的是 REPL、长命令与 fish(它的钩子只在目录变化时报)。
**验证**:探针先在 Debian 13(dash 作 `/bin/sh`、sudo `use_pty`)真机上跑过 `su - node`、`sudo -i`、`sudo su -` 后敲 `zsh`、`su node`、`ssh` 出去几种形态,判定用例直接取这些真实输出;`ForegroundShellProbeTests` 57 项、`NestedShellFollowerTests` 14 项、桥 3 项;Docker 端到端 `ShellIntegrationDockerTests` 新增 8 项(五种登录 shell 下认出自己、su 问密码时认不出、su 进去后认出并补注后跟随、sudo 经中转终端认出、卡在 `read` 的脚本不认),`NestedShellFollowDockerTests` 走宿主整条链路:只在终端里敲 `su - vela-zsh` 与密码,新 shell 的目录自动报上来,`cd`、`exit` 之后都照样跟 —— 去掉补注这条用例就停在"新 shell 的首报"上,即 #637 本身。全量:Core 1026 通过 / 16 跳过,Terminal 554,ShellIntegration 46(无跳过),宿主 2271 通过 / 11 跳过;构建 0 警告。
**文档**:velashell-docs 中英文「交互与界面规格」§6 补上「跟随终端目录」一条(此前文档里没有),[velashell-docs PR #133](https://github.com/VelaShellLabs/velashell-docs/pull/133)。

## ✅ 217. 2026-10-19 AI 插件:Base URL 换到另一台主机时,旧 Key 不再跟过去(#582 复审遗留)

**问题**:在设置页或目录页把供应商的 Base URL 改到另一个 origin(协议、主机或端口不同)时,主槽、补充槽、当前槽与继承地址的模型自带 Key 原样保留,下一次请求就把旧中转站的 Key 发给新地址;地址改了还没保存就点「测试」,也会把已存的主 Key 发过去(§177 复审提出、一直没改)。
**改法**:
- `AiSettingsStore.SaveProviderApiKeyFormAsync`:地址换到别的 origin 时,主槽、补充槽与没填覆盖地址的自带 Key 模型一并清掉,补充槽与 `ActiveApiKeyId` 复位;同一次保存里新填的 Key 落进主槽(不留「主槽空、补充槽有」的状态,与旧 Key 相同也不报重复)。会清掉已存 Key 时默认拒绝、抛 `ApiKeyResetRequiredException`(带要清的把数),调用方让用户确认后带 `resetKeysOnOriginChange` 重新提交;只改路径(同 origin)不动 Key。清理沿用原事务:先在 `Updating` 闸下改机密、再写 JSON、最后无 await 地发布,任一步失败逐个恢复原机密;发布按主槽 id 推进 epoch,别处设置里同一供应商的副本一并作废。
- 设置页与目录页:第一次保存只提示要清几把,同一供应商、同一地址再点一次才清;提示之后又改了地址就重新提示。保存后按有没有新填 Key 分两种提示;目录页清空后不再自动拉模型(新地址还没有 Key,只会换来一次鉴权失败),框里预填的旧 Key 一并清掉。
- 设置页「测试」:地址改到别的 origin 而没保存时,只带表单里新填的 Key;没有就提示先填,不发请求。
- 没动的既有口径:模型覆盖地址跨源时只用主槽。原先与供应商同源、填了覆盖地址的模型,在供应商换源后会拿到为新地址填的主 Key —— 记进 `feature-plan.md` 由维护者定。目录合并提交(`SaveProviderCatalogueAsync`)的两个调用方都不改地址,没加同样的闸。
**验证**:新增存储 4 项(5 例:未确认拒绝且不改任何东西、确认后清主槽 / 补充槽 / 继承地址的模型 Key 而保留覆盖地址的、新 Key 落主槽且可与旧 Key 相同、只换唯一一把不用确认、同源改路径与大小写 / 默认端口不清),设置页 2 项(两击确认与改地址后重新提示、未保存跨源测试只带新填的 Key),目录页 1 项(两击确认、清空预填框、不拉模型、之后填新 Key 照常保存);把条件改回去这 5 项失败。9 条旧用例按新语义调整:5 条原本断言旧 Key 发往新主机或跨源测试带主 Key,改为断言新行为;4 条只是借换主机造场景,改成同源路径或在另一个窗口带确认提交,原意不变。`VelaShell.Plugin.Ai.Tests` 全量 1544 通过、0 跳过;构建 0 警告。
**文档**:插件 README「Base URL 换到另一个 origin 时清掉旧 Key」一段;velashell-docs 没有规定 Key 槽与地址的关系,无需变更。

## ✅ 218. 2026-10-19 SSH 断开原因写进诊断日志;关于页一键导出诊断包

**起因**:用户报「用着用着断了」时,诊断日志里只有重连的痕迹、没有断开的原因(§197 只能另搭程序复现才看到);报障时还要被教着去翻 `~/.velashell/logs`,自己判断哪几份有用、里面有没有不想给人看的东西。
**改法**:
- **断开原因**(`Infrastructure/Ssh/SshDisconnectLog.cs`):`SshConnectionAssembler` 每建好一条连接(跳板链上的每一跳都走这里)就挂上库的 `SshConnection.Completion`,结束时写一行英文日志:目标、时长、结束原因。本端释放记 `closed locally`;其余是库给的 `SshFailureReason(SshPhase)`,收到 `DISCONNECT` 时带原因码与对端原话,再附库的消息与内层异常。对端原话与异常消息是不可信文本:控制字符换成空格、截到 256 字符,永远只占一行。日志行用英文,是因为 `LocalizedKeyUsageTests` 只放过与 `Trace.WriteLine` 同一行的中文,而这一行是拼出来的;它本来也是给看日志的人的。
- **诊断包**(`Infrastructure/Diagnostics/DiagnosticBundle.cs`、`DiagnosticRedactor.cs`):关于页「导出诊断包…」→ 另存为 → 日志目录顶层的 `*.log` 与 `crash-*.txt`(从新到旧,原文合计封顶 32 MB)、`summary.txt`(版本、系统、架构、.NET、界面语言、进程运行时长、已发现插件的 id / 版本 / 状态 / 运行方式 / 加载错误)、`README.txt`(界面语言写的说明与各类替换次数)。先写 `.partial` 再换名,失败或取消不留半个包;日志按共享读打开,正被追加写入的当天日志也收得到。设置、连接配置与凭据一律不收 —— 那里的代理口令、密钥路径脱敏规则兜不全。
- **脱敏**:主机与登录名取自已保存连接、最近连接(最多 500 条)与共享凭据的**元数据**(`ListMetadataAsync`,不解密),换成 `<host-N>` / `<user-N>`,同一个值在整个包里同一个占位符,映射不进包;按词边界匹配(`db1` 不拆 `db10`),`root` / `ubuntu` 这类常见登录名与两个字符以内的值不换(认不出是谁,换了日志没法读)。本机主目录换 `~`(先于登录名换,路径里带着它),本机登录名、机器名、其余 IPv4(回环除外,同一个地址同一个占位符)、邮箱,以及 `password=` / `token:` / `api_key=` / `Bearer …` 的值也都遮盖。
- `PRIVACY.md` 本机数据表补上诊断日志(每条 SSH 连接结束一行,含 `用户@主机:端口`;7 天清理),并说明诊断包只写到用户选的位置、不上传。
**验证**:`SshDisconnectLogTests` 3 项(本端关闭、对端 DISCONNECT 带码与清洗后的原话且只占一行、内层异常与截断);Docker 端到端 `SshDisconnectLogIntegrationTests` 经宿主装配路径真连 `ssh`(2222):在服务端杀掉这条会话的 sshd 进程,日志里出现 `ClosedByPeer(Open) - 对端关闭了连接。`,再连一条正常释放出现 `closed locally`。`DiagnosticBundleTests` 4 项(已知主机 / 用户 / 路径的一致替换与词边界、未知 IP / 邮箱 / 口令 / 令牌、收齐日志与崩溃记录且包里查不到任何原值、按时间封顶与失败不留残件);`SettingsDiagnosticsExportTests` 走视图模型,三类来源的主机与用户都被换掉且不读解密接口;`AboutPageDiagnosticsUiTests` 按钮与「打开日志目录」同排、结果独占一行、没结果不占位(另用无头截图看过布局)。全量:宿主 2273 通过 / 11 跳过,Infrastructure 738 / 5,Core 1027 / 16;构建 0 警告。
**文档**:velashell-docs 中英文「交互与界面规格」关于页一段(导出诊断包与断开日志行)。

## ✅ 219. 2026-10-19 主窗口渲染停顿:测量脚本与结论 —— 开发机上复现不出来,应用级保温先不挂

**起因**:`feature-plan.md` 把「整个应用的渲染停顿」排在前面:§201 在 X 窗口里量到的 400–490 毫秒停顿(渲染循环连着出帧之后转空拍,等系统合成器确认上一次提交),推断终端打字回显、对话框也会撞上。打算照 X 窗口的办法给每扇窗口挂「保温」。
**测量**(`scripts/perf/render-keepwarm.cs`,Release,AngleEgl + 默认合成候选,与主程序同一套设置):
- ⚠️ 先踩的坑:照 §201 的 `--render-probe` 量 `RequestAnimationFrame` 回调延迟,30 Hz 刷新 5366 帧、打字节奏 600 键,恒为 0 毫秒 —— 窗口闲着时 Avalonia 在 UI 线程上当场补发回调,停住的是渲染线程,这个指标看不到它。改为量「改字 → `Compositor.RequestCommitAsync()` 完成」(这一批被渲染线程处理完)。
- 结果(Windows 11 build 29683、Avalonia 12.1.4):30 Hz 刷新 120 秒 3358 帧,p50 3.6 / p99 4.7 / 最长 9 毫秒;打字节奏(80–600 毫秒随机间隔、回显隔 10 毫秒、每次连出 3 帧)600 键,p50 3.8 / p99 10.6 / 最长 14 毫秒;**0 次超过 50 毫秒**。窗口置顶激活与否一样。
- 候选修法「保温」(输入后两秒内每帧重画浮层里 1×1 的透明块):30 Hz 3352 帧最长 11 毫秒、打字 600 键 p99 11.3 / 最长 21 毫秒,同样 0 次 —— 没有可见收益,只多了每次输入后两秒的逐帧重画和不到 1 毫秒的 p99。
**结论**:主程序不改。候选修法留在脚本里(`KeepWarm`),`feature-plan.md` 的条目降为 P2 并写明复现与量法:有用户报「打字卡一下」时,在那台机器上跑脚本对照开关,量出收益再挂。

## ✅ 220. 2026-10-19 会话日志与录制:落盘前遮盖口令、令牌与私钥(默认开)

**问题**:只录输出、不录输入是对的(密码没有回显),但输出里照样有密钥 —— `cat .env`、`kubectl get secret -o yaml`、`env | grep TOKEN` 的值原样进了会话日志与录制,事后谁拿到文件谁就看得到。
**改法**:
- `Services/SessionOutputMasker.cs`:挂在桥的旁路(`SshTerminalBridge.DataReceived`)与 `SessionLogWriter` / `SessionRecorder` 之间(`MainWindowViewModel.CaptureSink`),显示路径不经过它。规则(大小写不敏感,宁可多遮):名字里带 password / passwd / passphrase / secret / token / api key / access key / private key / credential 的 `名字=值`、`名字: 值`(JSON、YAML 写法,名字与分隔符之间的 `grep --color` 颜色序列不碍事)只换值,引号留着;`Bearer` / `Basic` 凭据(先于名字=值换,否则「token: Bearer abc」只遮掉 Bearer);PEM 私钥块正文;自带前缀的令牌(`AKIA` / `ASIA`、`ghp_` 一类、`xox?-`、`sk-`)。`pwd` 没进名单:`env` 里的 `PWD=` 是当前目录。
- 按字节、按行:每个字节按 Latin-1 映射再匹配,规则与替换都是 ASCII,UTF-8 与 GBK 原样往返;空白只认 ASCII 的空格与制表符 —— `\s` 会把 UTF-8 续字节 0x85 / 0xA0 当成空白,值就在一个汉字中间断开。整行才匹配,最后不完整的一行只有正写到一个值的中间(含刚好停在 `=` 上)或在私钥块里时才扣下(上限 4 KB);`Password: ` 这类等输入的提示与全屏程序不换行的输出当场放出,录制的时间轴不受影响。停止记录时先 `Flush` 扣着的部分再收尾;停止记录不退订旧桥,`Write` 与 `Flush` 用一把锁串起来。
- 设置 `Security.MaskSecretsInCaptures`(默认开),在「安全审计 → 会话录制」里,对之后开始的录制与会话日志生效;五份 resx。`PRIVACY.md` 两段补上默认遮盖与「尽力而为、不改屏幕显示」。
**验证**:`SessionOutputMaskerTests` 8 项(`.env` / YAML / JSON 只遮值且 `PWD` 不动、值跨两段输出与停在 `=` 上、提示与全屏输出不扣、颜色序列夹在中间、私钥块跨段、Bearer 与带前缀令牌、GBK / UTF-8 往返、扣下上限与收尾 Flush);`SessionRecorderTests` 加一项:遮盖接真实的录制器,落库的块里没有原值、扣着的半行收尾时遮完进库。本地化守卫通过;全量:宿主 2280 通过 / 11 跳过,Core 1026 / 16;构建 0 警告。
**文档**:velashell-docs 中英文「设置项审计」第十四批、「交互与界面规格」安全审计页一行。不做的:用户自定义规则(与「终端输出的自定义高亮规则」共用规则表,见 `feature-plan.md`);既没有名字也没有前缀的裸值认不出来。

## ✅ 221. 2026-10-19 测试环境的四笔欠账:SSH 用例本机按核数并行、互操作靶机换到 2229 并接进 compose 网络、ssh-keygen 用例不吃 %TEMP% 的 ACL、本机 Release 构建不再报 NETSDK1150

这几条在 `plan.md` 里被一节节记成「与本次无关」(`feature-plan.md`「测试与本机环境」),一次清掉。
- **`VelaShell.Ssh.Tests` 的并行度**:`test.runsettings` 的 `Workers=2` 是给 3 核 runner 定的(§170),本机整套从 5 秒拖到 53 秒。拆成两份:`test.runsettings`(本机,`Workers=0` 按核数)与 `test.ci.runsettings`(`Workers=2`),工程文件按 `$(CI)` 选 —— GitHub Actions 每一步都设了 `CI=true`。没用命令行覆盖(`-- MSTest.Parallelize.Workers=2`):CI 是整个解决方案一起跑,那样会给 UI 用例也打开方法级并行,而它们共用一条 headless UI 线程。实测本机 6 秒、`CI=true` 55 秒。
- **互操作靶机**:`Start-TestServer.ps1` 默认映射 2222 —— 那是 compose 的 `ssh`,大家只好改 `-Port 2224`,又撞上 `ssh-2fa`,`KeyboardInteractiveIntegrationTests` 连到这台上失败而不是跳过(§193)。默认改为 2229(用例的默认值同改),并在 compose 的 `ssh-shells` 靶机在跑时加入它所在的网络(名字随检出目录变,按 `docker inspect velashell-test-shells` 取;`-Network ''` 不加入)。`目的地约束对真agent_经转发逐跳放行` 因此第一次真跑通:互操作 61 项全过、0 跳过(原先 60 + 1 环境跳过)。⚠️ `[string]` 参数收不到 `$null`(会变成空串),所以「自动」用 `'auto'` 作标记。
- **`真ssh_keygen读得出本库写的私钥`**:Windows 上私钥文件从 `%TEMP%` 继承 ACL,`%TEMP%` 挂着别的用户组时 OpenSSH 报 `UNPROTECTED PRIVATE KEY FILE`(§197、§199、§201、§210)。改为断掉继承、只给当前用户完全控制 —— 与 Unix 上的 0600 是同一件事。临时给目录加一条 `BUILTIN\Users` 可读的继承项复现出原报错,加上修复后通过。
- **NETSDK1150**:主程序 Release 段 `SelfContained=true`,而它引用的 `VelaShell.PluginHost` 不是,本机 `dotnet build -c Release` 直接报错(脚本只好带 `-p:ValidateExecutableReferencesMatchSelfContained=false`)。发布流水线命令行上传 `-p:SelfContained=true`(全局属性,两边都吃到)所以不受影响。PluginHost 的 Release 段同样设成自包含;没去关那道检查 —— 它拦的正是「主程序自包含、PluginHost 找系统运行时」那种隔离插件起不来的包。按 `release.yml` 的命令发布 win-x64:PluginHost 的 runtimeconfig 仍是 `includedFrameworks`,双击能起到「缺 VELA_PLUGIN_PIPE」那一步。
- 顺带核实:`X11_RefusedByServer_KeepsAgentForwardingAndWarnsOnce` 本机已通过(`velashell-test-shells` 2026-10-19 重建过);镜像跟不上 Dockerfile 这类问题本身还在,`feature-plan.md` 那条降为 P3 并改写。「Code scanning AI findings」是仓库设置里的 GitHub 托管任务,要仓库所有者处理,留着。
**文档**:velashell-docs 中英文 SSH 架构的互操作一节补上默认端口与网络。

## ✅ 222. 2026-10-19 复审 #616–#636:4 处高危与一批中低问题

**来由**:对 #616 到 #636 的 17 个 PR 做了一轮只读复审(按区域分 7 组并行审,再按 8 组分别修;#620 / #621 / #628 / #634 是 issue)。高危 4 处:编辑连接保存即清空目录收藏(#623)、「新增供应商」首份 OAuth 一点登录就清空授权参数(#619)、Intel 核显指标恒为空(#622)、对外 MCP 一次拒绝就封死整条连接(#630)。#636 已被 657b174d 回退,#624 的依赖升级是有意为之,#629 不处理。
**改法(按区域)**:
- **目录收藏(#623)**:编辑框打开时深拷贝收藏、`BuildProfileCore` 原样带回。根治在仓储:`SaveSessionAsync` 更新已有配置时保留库里现存的收藏(读收藏与写回在同一次集合锁内),连接写回、置顶、拖分组、证书信任、批量编辑等十几处整条写回一行不用改 —— 与「凭据不落盘」同一个思路,写回点太多,逐个防守总会漏。新增 `ReplaceSessionAsync`(Gist 拉取、导入备份,本就该采用文件里的收藏)与 `UpdateDirectoryBookmarksAsync`(在库里最新的列表上改一次,读改写在锁内);SFTP 文档改走后者,不再整条写回自己手里那份配置。管理窗口自己作弹框属主、随文档 `Detached` 关闭、窗口内显示错误;菜单头改 `TextBlock`(路径里的 `_` 不再被吃成访问键);`~` / `~/x` 用远程栏家目录展开,展不开就拒收;远程路径不再修剪首尾空格;先落盘、成功再改列表。
- **上传进度(#617)**:满格只报一次;插件进度桥扣住满格帧,传输任务成功后补发一次、失败不发。`TestSftpServer` 加 `HoldWriteReplies` / `ReleaseHeldWrites`,补一条不靠真 OpenSSH 的「扣住确认」内存用例(原 6 条真机用例在 CI 上恒为跳过)。
- **AI 供应商(#619 / #616)**:`_addingAnother` 收窄为「该类型真有实例可拷」,首份 OAuth 实例按首次添加处理;窗口级 `_newProviderEntry` 让「新增」窗口加完一家后点别的类型仍是新建;复用窗口时 `StartNewProvider` 不掐进行中的登录、不丢草稿;Key 草稿不再依赖来源存在(OAuth 克隆仍拒绝,来源的隐藏授权参数不能借克隆复活)。模型拉取:Anthropic 按 `has_more` / `last_id` 翻页(首个请求不带参数,不认参数的中转站不受影响),翻页不完整就不清理;当前选中、故障转移链与 IM 桥接指定的模型不清理。补 #616 非流式工具循环的回归用例。`store` 默认值实测后**没改**:重放不按 id 引用 `rs_` / `msg_` / `fc_`,但改默认值会让所有存量 Responses 接入多发一个 `store:false` 字段,中转站认不认无从验证。
- **AI 受控执行,插件侧(#630)**:对外 MCP 的 run 不再因拒绝封死(`BeginRun(denialEndsRun: false)`,拒绝只是那一次调用的结果);任务满 128 时回收已收尾且终态已交付的(Unknown 与未确认停止的 TimedOut 不回收)。撤权的任务给一条「结果不予提供」的 Unknown 通知并以 TaskUnknown 暂停,不再静默按成功收尾;醒来交不出通知但仍有活动任务就继续等。参数绑定错误(缺必填、JSON 类型不符,工具体还没跑)交还模型。预算改按输出 token 计;完成通知唤醒不计入批次。超长输出拆条不截断;`task_status` / `task_wait` 交付过的终态不再重复通知;取消分支按 `CommandStarted` 判,审批中被取消的 sudo 与 `task_cancel` 掉的监控任务记 Cancelled。完成通知仍走 User 角色(Tool 消息必须紧跟调用、中途 System 会被部分适配器挪进系统提示),改为包进 `<untrusted-command-output>` 并声明不是用户指令。⚠️ MCP 拒绝的语义与 velashell-docs #127 原写法相反,是有意修改。
- **AI 受控执行,宿主侧(#630)**:`ISshClientWrapper.StreamCommandAsync` / `StreamSudoCommandAsync` 的行回调改为 `Func<…, ValueTask>`,新增 `IFlowControlledProgress<T>`,`ExecutionNotificationSink` 队列满就等(可取消)—— 隔离插件遇到成批短行不再「queue overflowed」。sudo 输出一律按 U+FFFD 替换(三个标记都是 ASCII,替换造不出也吞不掉它们)。识别到启动标记先报 Running 再做任何 await。登录 shell 是 fish / tcsh 时包装脚本语法错:实测 fish 的单引号会把 `\` 收成 `\`,`exec /bin/sh -c '<脚本>'` 会悄悄改写已批准的 root 命令,所以外层改为 `exec /bin/sh -c 'eval "$(printf %b "<payload>")"'`,payload 只含字母数字、少数标点与 `\0ooo`(代价:符号多的命令最多膨胀约 5 倍)。root 审批框回车与初始焦点在取消上(`MessageDialog.ShowCustomAsync` 的 `cancelIsDefault`),控制 / 格式 / 双向字符显成 `⟨U+XXXX⟩` 并注明个数,多行或长命令注明行数与长度。插件侧 `RemoteCapabilities` 带操作 id 的请求以 `CancellationToken.None` 发出 —— 原先在写闸前排队时被取消,请求帧没发、取消通知照发,宿主攒满 128 条早到取消后整条插件连接停用执行。新增 `tests/fixtures/ssh-sudo`(Ubuntu 24.04、sudo 1.9.15p5、真实 PAM,带 fish / tcsh)与 compose 服务 `ssh-sudo`(端口 2230),`SudoInteropTests` 改为靶机端口可达就跑、归入 `DockerIntegration`。
- **Intel GPU 监视(#622)**:旧探针恒空有两个原因 —— awk 每个文件开头把累加量清零;`/proc/*/fdinfo/*` 必含 shell 展开通配时的目录 fd,awk 打不开就整个退出、不跑 END(mawk / gawk / busybox 都如此)。新探针先看 `/sys/bus/pci/drivers/{i915,xe}` 有没有绑定设备,有才扫;`ls -ln` 挑出指向 `/dev/dri/` 的 fd,与 `/proc/uptime` 一起 `cat … 2>/dev/null` 成一条流喂一个 awk,输出 `t|` / `c|` / `m|` 三种行。xe 按 Δcycles / Δtotal-cycles;利用率按(客户端, 引擎)配对差分、取按 capacity 折算后最忙的引擎类,i915 分母用远端 uptime;`local*` / `vram*` 记作独显显存(只给已用量),`system*` / `stolen*` / `gtt` 记作核显共享内存;GPU 进程表表头固定为显存;`__GV__` 改 `|` 分隔(非 PCI 的 DRM 设备不再冒成 Unknown GPU);`__GC__` 没绑驱动的卡不再把驱动名显示成 `driver`。用例把命令里的 `/proc/`、`/sys/` 换成夹具目录真跑,按 sh / mawk / gawk / original-awk / busybox 五种工具链各跑一遍(Windows 上 `[SKIP]`)。按页面裁掉 GPU 采集范围**没做**:侧栏每一页都显示 GPU 利用率,且样本按采集范围分格,切页会打断差分。顺带(首轮 CI 在 macOS 上暴露):基础段的 `. /etc/os-release` 是特殊内建,文件不在时 dash、busybox ash 整个退出,后面的 `__K__` `__S__` `__N__` 与 GPU 段全丢(CentOS 6 这类主机即如此),改为先判可读、在子 shell 里读;夹具连 `/etc/` 一起换掉,Linux 上同样覆盖到。
- **更新下载(#626)**:`DownloadUpdateAsync` 带上 `CancellationToken`;停滞看门狗 45 秒无进度就断开本次尝试,凭 `.partial` 带 Range 续传,最多重连 3 次后报失败、按钮恢复可点。速率改真滑动窗口(长停滞后恢复第一拍报 0);停滞判定改单调时钟并可注入;第一条汇报前显示「准备中」;总量未知也汇报字节。
- **更新日志(#635)与 SDK(#625)**:只取发布列表第一页(20 条),API 走独立的开了解压的 `HttpClient`,流式解析,进程内缓存 5 分钟后带 `If-None-Match`;403 / 429 单独提示;Markdig 开自动链接 / 管道表格 / 删除线 / 任务列表(GitHub 自动生成的说明全是裸 URL);「在 GitHub 查看」改用 API 的 `html_url`。global.json **不恢复**:CI 与发布的各个 job 在 setup-dotnet 之后打印 `dotnet --info`,README / CONTRIBUTING 改为如实描述。
- **Tailscale SSH(#631)**:库的认证计时对 Tailscale 跳设为无限,由 `TailscaleAuthDeadline` 管:主机密钥裁决结束后才起算 2 分钟守卫,收到第一条合法验证链接放宽到 30 分钟(只放宽一次),验证窗显示截止时间。`OnClosing` 只拦用户直接关这扇窗,`OwnerWindowClosing` 放行(与 `XNativeWindow` 同类教训),主窗口一次就能关掉。异常加 `RequiresUserAction`:只有见过验证链接或服务端明确拒绝才暂停后台重连,连接重置、无链接超时照常退避。认证中「说明横幅 + DISCONNECT」报为拒绝并附上清洗过的服务器说明(去链接、一行、≤300 字符)。非 Tailscale 认证遇到 check 链接立即失败并提示改用 Tailscale 认证;连接诊断对 Tailscale 只要求用户名;FTP 与插件协议的登录框只给密码。验证链接的 query 仍放行(拿不到真实样本确认),改为显示实际打开的 `AbsoluteUri` 并提示「这页应当是批准 SSH 访问,要求登录或添加设备就别批准」。
- **选区同文高亮与共享密码填入(#632 / #633 / #642)**:拖过行尾的选区按 `LastNonBlank` 收尾;查询不足 2 个字素不高亮,单字只在本身是整词时生效且只匹配整词;`BufferSearch.FindVisible` 把视口内相邻的软换行行接起来找,命中按行切段(Ctrl+F 不变)。弹层锚点改为 `GetCursorRect().Inflate(0, 2)` 并去掉 `VerticalOffset`(Avalonia 翻转时 offset 不反向,原写法翻到上方正好盖住提示行)。请求新增 `Expired`(被键入作废时收起列表、不记为拒绝)与 `PromptChanged`(输错重新提示时按新请求重开);命令面板入口先把焦点交给终端;重开前 Dispose 旧请求、订阅 `Closed` 对账;列表读取失败如实提示并写 Trace;每批输出先看光标行是否以冒号结尾,不是就不拼逻辑行、不跑正则。
**留待维护者定**(已记入 `feature-plan.md`):AI `store` 默认值、复用窗口时「管理登录」同样会掐登录、桥接模型被手动删除后静默换到别家、「8 批」上限实际到不了(100 ÷ 25)、审批框批准键是否加延迟启用、SDK `RpcConnection.WriteAsync` 取消落在写帧中途可能写出半帧;防空闲 NUL 与同步输入不走 TypedInput,密码列表不会主动收起;补全弹层同样的 `VerticalOffset` 翻转问题;「复制配置」用树缓存副本可能缺收藏。
**验证**:每组新增用例都在临时撤掉对应修复后确认会失败。集成分支按 CI 口径(`scripts/ci/run-tests.sh`,排除 DockerIntegration / CrossPlatform / Interop)全量 8249 通过 / 0 失败 / 28 跳过(平台、Docker、Interop 条件用例);另在 ssh-sudo 靶机上真跑 `VelaShell.Sudo.Kernel.Tests` 24 项全过。Debug 全解 `-warnaserror` 0 警告;`EnforceCodeStyleInBuild` 下本次新增代码零告警(基线另有 107 处 IDE0055 等,多来自 #631,未在本次处理)。GPU 探针在 WSL Debian 13 上以 dash / bash / busybox sh 与四种 awk 真跑;sudo 包装在 ssh-sudo 靶机上以 bash / fish / tcsh 登录 shell 真跑。macOS 由 CI 覆盖(首轮暴露的两处已修:上面的 os-release,以及确认进度用例原先按固定 500ms 数补报拍数,改为等到补报出现)。
**文档**:同步 velashell-docs 中英文「架构设计」「消息中心与资讯源」「交互与界面规格」(§5.2 / §5.2.1 / §6 / §9 / §11 / §13 / §14)、「快捷键参考」「凭据管理器集成设计」,以及插件文档 05 / 07 / 11 / 12 与「协作接入」,[velashell-docs PR #134](https://github.com/VelaShellLabs/velashell-docs/pull/134)。

## 223. 2026-10-20 AI 插件:空回复有限重试,恢复模型步骤而非工具副作用

**来由**:用户遇到「端点返回了空回复」。普通聊天已有一次未开口重试,但 Agent 正常结束的空模型步骤绕过已有异常恢复,设置探活也会一次空回复就判失败。
**改法**:
- 普通聊天空回复最多补试两次(400/800ms),网络等其它瞬时错误仍只补试一次;共用尝试计数,耗尽后才按既有条件故障转移。有效正文、思考、工具调用/结果不重放。
- Agent/Plan/Bridge 在完整步骤确认及工具帧释放前判空,接入现有两次恢复预算;工具完成后的空步骤携带既有工具结果重问模型,不重新运行工具、不额外占模型步骤,失败尝试用量照常累计。
- 探活正常空结束最多补试一次(400ms),两次请求及退避共用15秒截止时间,固定原目标和凭据;只返回思考或不可见字符仍不判活。已知终止信号优先于随后断流,用户取消优先于错误。
- 明确长度耗尽、过滤、拒绝及错误内容不变成可重试空回复;OpenAI Chat Completions/Responses和Anthropic按已映射终止原因或明确SDK事件判定。长度/拒绝新增五语提示,Bridge及各设置探测入口复用本地化错误。
**验证**:AI工程全量1593通过/0失败/0跳过;随后补充探活共享截止时间、仅思考探活、终止后断流及取消优先边界,最终相关定向103通过。最终全解Debug构建0警告/0错误;宿主HeadlessDispatch及本地化门禁16通过。真实本地HTTP/SSE覆盖三种协议的明确终止、用量及恢复,headless覆盖第三次成功、换站、插话保留和迟到健康记录;没有声称真实供应商或人工GUI整链实测。首次并行构建遇测试进程锁DLL,等测试退出后串行构建已通过。
**文档**:更新插件README,同步velashell-docs的`zh/plugins/11-automation-and-ai.md`与`en/plugins/11-automation-and-ai.md` §1.4,两份相对链接检查通过。未改SDK版本、全局SDK内部重试、摘要回落或用户设置;重试次数不包含SDK内部重试。

## 224. 2026-10-20 CI:修正登录进度与空回复探活测试的异步等待

**来由**:文档 PR #652 的构建及 macOS 测试通过,Windows 的 PKCE 登录用例偶发未观察到 `exchanging`,Linux 的设置页探活用例等待请求数等于 2 超时。后者在 PR 与最新 main 的合并基线上运行:§223 已让一次空回复探活发送两次请求,旧测试第二轮仍等待总数恰好等于 2,会因调度而提前通过或错过该值。

**改法**:PKCE 用线程安全队列接收 `Progress<T>` 异步回调,等待两条实际进度都到达后断言;设置页等待完整探活任务结束,明确断言草稿两次请求、保存后累计四次,并逐条核对 Low 思考档位,保留草稿失败不冷却已保存 High、保存 Low 后失败才冷却的断言。只调整测试同步与预期,未改运行时行为、超时预算或 CI 门禁,没有跳过失败用例。

**验证**:最新 main 合并基线的 Debug 全解决方案 `-warnaserror` 构建通过,0 警告/0 错误;登录、探活和问题用例定向 78 项通过;全部设置页 160 项通过/0 失败/0 跳过。未声称本机验证 Linux/macOS,远端恢复以修复提交后的 CI 结果为准。

## 225. 2026-10-21 更新日志:改读正式版 latest.json 的附加字段

**来由**:更新日志直接读取 GitHub Release REST API,匿名配额按出口 IP 共享,容易因频繁请求或代理共享出口而耗尽。按确认的范围,日志仅展示最新正式版,不再包含预发布版。

**改法**:日志读取固定 `releases/latest/download/latest.json` 附件,复用根级 `tag`,从可选 `releaseNotes` 对象读取 `body` / `publishedAt` / `htmlUrl`,不查询或回退匿名 API。保留 5 分钟进程内缓存、ETag 条件复验、30 秒整体超时、响应体取消与现有弹窗状态;404 返回无发布,旧清单缺少日志字段显示版本标签和空正文。原 `version` / `tag` / `assets` 不变,更新检查(含预发布 API 路径)及安装包下载逻辑保持不变。发布工作流认证读取 Release 元数据,在计算校验表前填入日志;`edited` 或手动 `notes_only` 仅同步既有清单与 `SHA256SUMS.txt`,不重建安装包,按标签串行且支持部分上传失败后重跑修复。标签经白名单校验与环境变量传递,正文按 JSON 数据处理。本地打包生成空日志占位,不新增联网要求。同步修正“匿名 304 不占配额”的过时说明。

**验证**:Debug 全解决方案 `-warnaserror` 构建通过,0 警告/0 错误;更新源、清单、更新日志 UI 与更新服务定向 111 项通过/0 失败/0 跳过;宿主完整测试(按 CI 排除 DockerIntegration / CrossPlatform / Interop)2347 通过/0 失败/8 跳过,跳过项为符号链接、Linux 更新及 zsh/fish 平台条件用例。覆盖附件路径、无 API 回退、旧清单、正文及可选元数据、404 缓存、ETag 复验与失效、失败不覆盖缓存、响应体超时/取消,以及预发布更新选择回归。另对其余非 AI 工程运行相同分类过滤的回归测试:4383 通过/0 失败/20 跳过(平台与环境条件),本轮合计非 AI 测试 6730 通过/28 跳过。发布配置的 actionlint、21 个内联脚本及本地 PowerShell 脚本语法检查通过,53 项离线 fixture 与 4 种校验行格式验证通过,覆盖正文安全往返、日志编辑与校验更新、事件门控及失败重跑。未声称运行 AI 测试、线上发布或人工 GUI 实测。

**文档**:同步 velashell-docs 的 `zh/host/交互与界面规格.md` 与 `en/host/interaction-and-ui-specs.md`。新客户端需要正式版附件包含日志字段;现有旧清单在补齐前显示空正文,不会为兼容旧清单重新调用匿名 API。

## 226. 2026-10-21 更新日志:按安装版本选择说明并固定更新目标

**来由**:§225 的日志始终指向最新正式版,不能区分本机版本或在日志窗口直接发起更新。按确认的交互,无更新展示当前版本,有更新展示最新可用正式版(不是紧邻下一版)。

**改法**:先读正式版清单并比较本机版本,相同则复用响应,更高则展示新版日志,较低或不可取得时读取本机 tag 的附件;仅404尝试有/无 v 与规范三段版本号的有限变体。URL缓存/ETag隔离,不回退匿名API;当前版本无法识别、附件缺失和检查失败分别如实提示。日志可附带同响应的不可变平台安装快照,坏资产不影响正文但不形成安装候选。Windows/macOS可自更新时提供“立即更新”,指定快照直接下载、SHA-256校验,完成后由用户点击“重启并更新”;Linux/不可写目录前往所展示版本的HTTPS发布页,Store仅提示商店管理。关闭日志只取消读取,已启动下载由关于页继续承接。下载失败可重试,重启、检查、修复与下载共享忙状态;服务通过互斥及检查代数防止迟到检查或重复操作覆盖目标,Ready同目标复用、不同目标须明确重置。原关于页preview通道不变,五语文案及版本徽标同步调整。

**验证**:Debug全解决方案 `-warnaserror` 构建0警告/0错误;源、清单、服务、设置VM、日志UI、本地化及设计门禁定向303通过/0失败/3平台跳过;最终宿主完整回归(排除DockerIntegration/CrossPlatform/Interop)2483通过/0失败/8平台跳过;新增失败重试与Ready后重启按钮用例后,日志UI及资源/Headless门禁61通过。所改服务、VM及UI测试增量格式检查通过。未执行真实安装/重启、浏览器手动下载、线上发布或跨平台桌面实测。

**文档**:同步velashell-docs中英交互规格及消息中心与资讯源。历史版本必须已有对应清单及日志字段,缺少时需经维护者授权补齐,客户端不伪造或借用其它版本日志。

## 227. 2026-10-21 AI 上传:移除固定大小限制,独立任务与用户/模型进度

**来由**:AI 的 `upload_local_file` 原先以 32 MiB 拒绝大文件,且整份上传绑定一次工具请求,没有中途进度。只删除判断仍会遇到 MCP/Bridge 请求截止时间,因此先完成插件 SDK 独立传输契约,由维护者发布正式 NuGet 2.1.1 后再接宿主。

**实现**:
- 宿主、测试替身、AI 显式包引用与 `minSdkVersion` 统一为 2.1.1,仅使用 nuget.org 正式包。上传改为幂等启动后快速返回任务快照。本节记录2026-10-21上传交付时的历史状态：当时模型下载仍有32 MiB判断，次日§229已移除此判断并接入后台下载源码，当前下载发布阻塞与平台限制以§229为准；聊天附件和 IM 平台发文件上限不变。
- 每插件独立任务登记,4 项实际传输并发、32 项活动/排队额度、1024 条生命周期回执;字节计数为 64 位。请求/短等待取消不杀传输,取消指定执行代次,远端写入收尾、摘要校验和原子提交成功后才是 `Succeeded`。旧同步上传/下载行为不改,隔离进程走短 start/get/wait/cancel/resume RPC,旧宿主能力缺席明确不支持。
- Windows 持有稳定源句柄,Linux/macOS 在后台生成私有磁盘快照,固定缓冲流式处理,需要约同等文件大小的额外临时空间。优先服务端 SHA-256,不支持时完整流式回读校验;准备/收尾持续心跳,满字节不提前成功。默认不覆盖目标,显式覆盖仅走严格 POSIX 原子替换,不先删目标。无法确认提交结果为 `Unknown`,不自动重传。
- AI 新增状态/短等/取消/恢复工具,owner/run 隔离,本地进度观察不反复调用模型;终态作为新的数据通知续接,不给同 CallId 第二个结果。观察故障与宿主快照序号分开,UI Peek 不消费模型终态;已接受但回执迟到的启动预先登记,入口取消后仍能跟踪并清理,不产生无主上传。
- 工具卡五语显示进度条、已传/总量、速率、ETA、准备/校验状态和取消入口,始终更新原对话气泡;历史运行回执不冒充成功。模型 Stop 仅停止推理等待,原卡继续管理上传;关闭面板/入口销毁或授权重载显式清理。Bridge 只暂停纯上传本地等待的剩余轮次预算,命令/监控超时不扩大;Stop 后仍可报告上传终态。MCP 活跃上传不因两小时无工具请求而空闲回收。

**授权复核**:审查发现新上传入口缺少宿主文件授权强制点后,维护者批准补齐上传专用可信确认。新增单任务租约与宿主确认UI,仅批准预览中的插件/真实会话/本地源/远端路径/覆盖策略;不继承终端或开会话授权,AI Bypass不能绕过。缺少授权闸/提示器/桌面窗口则拒绝;撤销针对待批准及活动任务,提交前复验并隐藏新的元数据。这不扩展旧同步文件API,不是通用文件权限Broker。

**边界**:当前连接抽象不能证明重连后的服务器密钥/账户/命名空间一致,因此恢复明确不支持、`CanResume=false`,不承诺断线或应用重启恢复。撤权不抹除此前已知的输入和历史,也不承诺回滚已经提交的文件。

**验证**:授权补齐后 Debug 全解决方案 `--no-restore -warnaserror` 构建 0 警告/0 错误;`scripts/ci/run-tests.sh` 按既有口径排除 DockerIntegration/CrossPlatform/Interop 后,8362 通过/0 失败/29 跳过,其中 AI 四组合计 1623 通过/0 跳过。宿主传输/授权/RPC四组专项41通过、AI传输/执行/SDK专项46通过、确认弹窗/本地化/调度门禁23通过,末次五语撤销按钮文案修正后重新严格构建0警告0错误且UI/管理页门禁24通过,均0失败0跳过。专项覆盖大于32MiB、空文件、超过int范围进度、请求取消与等待到期、启动去重/迟到回执、关闭失败/校验失败不提交、提交未知、代次取消、无授权拒绝、真实Manager接线、pending/active撤销隐藏、宿主拒绝后模型暂停、UI归属、历史状态及IM预算。Docker Engine本机未运行,真实SFTP容器/人工GUI整链未实测;Unix快照公共逻辑在Windows测试开关下验证,不宣称Linux/macOS原生权限与文件系统行为已经验收。

**文档**:插件 README 及 velashell-docs 中英文 SDK 参考、IPC、能力 API、自动化与 AI、协作接入同步说明上传工具、进度、取消/恢复与资源成本;未修改 SDK 版本号、未制作本地包。配套中英文文档与宿主改动分别提交,PR正文互相引用。上述测试基于上传工作分支,转入最新main基线的dev分支后仅进展记录追加处需人工保留双方内容;合并基线的验证以PR CI为准。

## 228. 2026-10-21 AI上传:修正Unix父目录别名与平台锁断言

**来由**:PR #656 的CI三个平台均编译通过,Ubuntu一项测试错误要求Unix拒绝第二写句柄,macOS十七项测试都被源路径祖先中的系统链接检查拦截。Windows本地全绿不能代表Unix验证。

**改法**:原请求绝对路径继续作为幂等回执参数,首次启动逐组件解析父目录链接,保留链接内容中先展开再处理`..`的顺序;末级文件链接、悬空与循环父链接仍拒绝。使用解析路径打开并核验句柄实际路径,Windows沿用最终路径查询,Linux读取`/proc/self/fd`,macOS用`F_GETPATH`且区分Apple arm64变参栈布局;不能核验即失败。审批/快照显示解析后的路径,后台始终读取原句柄,别名改向与源路径消失后的同id重取不重新打开。Windows保留分享锁断言,Unix专门验证批准前源变化不上传、不提交,另补多级/相对父别名、末级/悬空/循环链接拒绝及`..`顺序测试。后台`Task.Run`显式传`CancellationToken.None`,防止请求取消在worker调度前将已登记任务永久留在Queued。

**验证**:Debug全solution严格构建0警告/0错误;宿主上传/授权/RPC专项41通过、4项Unix专用测试在Windows明确跳过,AI专项46通过0跳过,资源/调度门禁23通过0跳过。最终最新dev基线CI过滤回归8521通过/0失败/32跳过（排除DockerIntegration/CrossPlatform/Interop,含4项新增Unix专用平台跳过）,AI四组1623通过0跳过;传输整类连续五轮各21通过/4平台跳过。首轮回归因请求token调度竞态失败,定位后修正并重新验证,未通过增加超时或跳过原测试掩盖。当前本机仅Docker专用WSL且未安装dotnet,Docker Engine未运行,不声称Linux/macOS原生回归已通过。

**文档**:同步velashell-docs中英SDK参考、能力API及AI上传说明共六份,明确父别名解析、实际审批路径、Unix分享锁不保证禁止其它写入者以及快照校验边界;SDK仍用2.1.1,没有新native产物或发版。

## 229. 2026-10-22 AI下载:双入口后台任务与安全提交源码接线

**来由**:模型下载原32 MiB、远端链接原64 MiB固定判断阻止大文件,同步下载又缺少中途进度并可能提前截断最终目标。本次移除的是这两个AI判断,不是授权、用户下载限速、上下文读取、聊天附件或IM平台上限。

**已发生的改动**:SDK源码增加默认false的独立下载能力、`StartDownloadAsync`/`RemoteDownloadOptions`、下载RPC与方向/阶段兼容字段;宿主复用上传任务账本、get/wait/cancel和4实际传输/32活动排队/1024回执的共享预算,加入独立下载worker、远端读/本地写单任务许可与逐次可信确认,不借用上传/终端许可,AI Bypass不能绕过。撤权取消待批准/活动租约并在提交前重验,后续结果脱敏,不回滚已提交目标。请求取消只结束回执等待,显式取消针对代次;后端不合作而停止未确认时保留槽/句柄直至实际退出,Unknown/TimedOut不自动重下,下载不可恢复。

**落盘边界**:新增流式下载到已打开流的内部SFTP入口,复用用户限速,最终路径不交旧同步下载。安全提交源码仅支持Windows固定本地NTFS,逐段持有父目录句柄、独占随机临时文件并按句柄原子提交;默认不覆盖,中途出现目标也不替换,明确覆盖不先删目标,时间戳写持有临时句柄。Linux/macOS、UNC、其它文件系统/不支持后端明确拒绝,无直接最终路径回退。实际长度、前后元数据与可用摘要检查不等于远端原子快照保证;纯SFTP不强制exec或默认二次整文件下载。满字节仍需校验、关闭和提交确认。

**AI接线**:模型入口返回带方向/阶段/64位字节/速率/ETA/停止确认的任务回执,工具卡本地观察刷新,终态只消费通知一次,不为原CallId伪造第二个结果。链接入口用点击时原对话/会话的独立scope与普通任务卡,不冒充模型工具/通知。Stop只停止模型等待,显式取消/关闭面板/入口销毁/插件停用/授权重载清理,迟到启动也跟踪;Bridge保留纯传输预算暂停与Stop后观察,MCP主动查询,不承诺唤醒外部模型。send_file在下载成功提交前拒绝目标及同名旧文件,原外发审批与平台边界不变。

**验证与发布事实**:SDK成员报告Debug契约测试68/68通过。宿主/AI正式包仍缺新下载类型,下游构建及新增下载测试运行受阻,未宣称全解决方案通过、Windows安全提交平台验收或真实SSH/GUI整链已验。下载SDK正式版本仍TBD（发版时替换）,未修改发布版本、中央/显式依赖或minSdk,未制作本地包。正式SDK发布是后续依赖升级的前提,不能把既有上传2.1.1的测试记录算成本轮下载证据。

**文档**:同步既有插件README与velashell-docs中英文SDK、IPC、权限、能力API、AI、安全及协作接入说明;没有新增文档、提交、推送或创建PR。

## 230. 2026-10-22 AI 插件:复审 #646(换源清 Key)—— 确认按槽位与值核对,跨源「测试」只看真会带出去的那把 Key

**来由**:复审 #646(§217),只修它自己引入的代码,它之前就有的代码不动(要改那部分代码的问题记入 `feature-plan.md`,见文末)。用临时用例逐条复现:①两击确认只认「供应商 + 地址」,提示「清 1 把」之后目录页给同一家加了 Key、给同一个槽换了值,或别处写盘引发冲突重载,第二击就把提示里没有的也一并清掉;②「测试」新加的拦截不看有没有已存 Key,Ollama 一类无鉴权接入从本机换到另一台机器后不保存就测不了(#646 之前能测,属回退);③换源时跳过 `ValidateUniqueKeyAsync` 连非空校验一起跳过,空白能不经确认覆盖唯一的那把(回退);④首模型自带 Key 时,「测试」的拦截提示照做也没用;⑤清掉模型自带的 Key 后界面不说是哪几个模型,目录页只清了模型 Key(这家本来不用 Key)也当成「Key 没了」,不拉模型还提示填新 Key;⑥只有空白的已存值也算成一把 Key 去问。修完又经一轮复审:放开无鉴权接入之后,Anthropic 协议下 SDK 会把本机的 `ANTHROPIC_API_KEY` / `ANTHROPIC_AUTH_TOKEN` 发往刚填、还没保存的地址(只敲空格的「新 Key」也一样);第二次提示与第一次一字不差,用户以为没点上、再点就清掉新存的 Key;读已存 Key 时按钮还没置灰,连点测两次,读完到发请求之间别处存的 Key 会被带去;等写盘期间改缓存的写法反而让提交中途读到旧 Key 的界面把它带去新地址;只有空白的主槽被删却不列出,目录页仍当基线、下一次保存误报冲突;所有模型都自带 Key 时「先填新 Key」走不通;探活只带主槽、检查却看所有槽;换模型来测时可能挑到空型号;点名后缀可能写到别的目录卡片;另一个 Key 行在编辑时清 Key 的结果不报;空名的模型被点成空串。另:#646–#650 合并时三个平台的「构建与测试」全部 cancelled,本次在本机补跑。
**改法**:
- 确认:`ApiKeyResetRequiredException` 带上要清的归属者(`KeyOwners`)与确认凭据(`Confirmation`,每一把的「归属者 + 值的 SHA-256 摘要」,不留明文);`SaveProviderApiKeyFormAsync` 的确认参数改为交回这份凭据(`confirmedKeyReset`),返回值改为清掉的归属者。要清的超出确认过的(别处新存了一把,或同一个槽换了值)就再拒绝一次,界面换一句「要清除的 Key 有了变动」(新增五语 `SetupBaseUrlKeyResetChanged`,免得以为上一击没点上);要清的变少(提示后在主槽填了新 Key)仍算确认过。只有空白的已存值照清不问。两个界面的待确认状态收成 `KeyResetPrompt`。
- 换源时仍免「不得与其余 Key 重复」,但空白照样按 `SetupUniqueApiKey` 拒绝。缓存保持 #646 的写法(删一把清一把):挪到发布段再改,提交中途读到旧 Key 的界面会把它带去新地址;删一把清一把在回滚窗口里最坏只是一次请求失败。
- 「测试」(只动 #646 加的拦截块):地址换到别的源而没保存、又没填新 Key(只敲空格不算)时,先看测试本会带上的那把 —— 首模型自带的 Key,或供应商的主槽(探活只带主槽);Anthropic 协议下本机设了 `ANTHROPIC_API_KEY` / `ANTHROPIC_AUTH_TOKEN` 也算,SDK 没有 Key 时会改用它。有就请用户先填新 Key;所有模型都自带 Key 时改为提示先保存、再给各模型填(新增五语 `SetupTestNeedsModelKeys`);一把都没有(无鉴权接入)就照常测,与 #646 之前一致。首模型自带 Key 而填了供应商的新 Key 时,换一个继承供应商 Key、填了型号的同源模型来测,没有就提示 `SetupKeyNoModel`。读已存 Key 之前记下配置版本,读完版本变了按「有」处理;每一击领一个序号,读的期间又点了一次(连点、改了地址或新填了 Key 再点、换到别家再点)就以最新一击为准,旧的那一击读完作废;读失败如实报错,等待期间换了选择就不测。
- 提示:保存后逐个点名清掉自带 Key 的模型(新增五语 `SetupModelKeysCleared`,空名显示「(未命名)」),另一个 Key 行在编辑时也照报;它们仍保留「自带 Key」,不悄悄改用供应商的 Key,计费归属不变。目录页按是否换源决定基线;只清了模型自带的 Key 时照常拉新地址的模型清单,点名提示只接在本卡片的拉取结果后面;清掉供应商的 Key 而没填新的才不拉。确认提示注明「协议、主机或端口变了」都算换源。
**验证**:新增存储 6 项(7 例)、设置页 17 项(24 例)、目录页 4 项(5 例)。每处修复都先写出在改动前失败的用例,#646 改过的 7 条用例只改了它加的那几行(新的确认参数与返回值),计数断言改为逐个核对归属者,没有放松;第一轮临时撤掉每处修复做了 10 组变异,对应用例都失败。「读已存 Key 失败如实报错」「等待期间换了选择」没有用例覆盖(Key 在缓存里时这一步不等待),只靠代码核对。全量结果见 §231。只在 macOS + .NET 11 RC2 上跑过,Windows / Linux 没验证,也没有真实供应商与人工 GUI 整链实测。
**留待维护者定**(已记入 `feature-plan.md`,要改的都是 #646 之前就有的代码或需要产品取舍):订阅供应商换源后,登录令牌与自带 Key 的模型 Key 照旧发往新地址,未保存时「测试」仍带令牌;Anthropic 协议没有 Key 时回落本机环境变量(未保存的跨源「测试」已拦,保存之后的聊天与测试仍会回落);确认靠同一个按钮两击,连点两下就算确认(与删除供应商同一模式)。
**文档**:更新插件 README「Base URL 换到另一个 origin 时清掉旧 Key」一段;velashell-docs 没有规定 Key 槽与地址的关系(只有 STATUS.md 一句「自填 Base URL + API Key」),无需变更。

## 231. 2026-10-22 复审 #647–#658:上传提交转发、默认下载目录、诊断包与录制遮盖、空回复重试等一批修正

**来由**:按 PR 逐个复审 #647–#658 合进 main 的代码(#645 不在这段编号内,未审),每个 PR 的 diff 取合并提交相对合并前 main 的改动。只修这些 PR 自己引入的代码,它们之前就有的代码不动;需要维护者拍板的一律只记录、不替人决定(见文末与 `feature-plan.md`)。问题先用会失败的用例复现再修;修完又分三路复审一轮,复审查出的跟进问题一并修掉,三路的结论都是可以发。

**#647 诊断包**(§218):
- 导出时收日志目录顶层的全部 `*.log`,会话日志(`session-*.log`,终端原始输出)也被打进「已脱敏」的包里,与 PRIVACY.md「诊断日志」的说法不符 → 会话日志不进包(传输记录等其余日志是否进包待定)。
- 口令规则用 `\b…\b`,`DB_PASSWORD=`、`GITHUB_TOKEN=`、`access_token=` 一类带下划线的键名都漏,带引号、JSON、`Basic` 与私钥块也不遮 → 键名含关键词即可,补上引号(认反斜杠转义)、JSON(含字符串里再套一层的 `\"key\":`)、Basic、PEM、常见令牌前缀;键名规则用 ASCII 边界加原子组,耗时随长度线性。
- 封顶遇到第一个超限文件就整体停下,可能导出空包 → 超限的跳过、接着收更旧的。
- IP:句号结尾的地址漏遮、IPv6 不遮、四段版本号被当成 IP → 修正前瞻、补 IPv6(候选 + `IPAddress.TryParse`)、`Version=` / `v` / 启动横幅里的版本号不遮;版本号豁免的回看把两段空白各限 3 个(原先一长串空白后跟数字就平方级回溯,2 万个空白 1.1 秒)。
- 断开日志行只换控制字符,双向控制符与 U+2028 / U+2029 照样进日志 → 换行先成空格,其余交给库的 `PeerText.Sanitize`(控制字符变 `?`)。
- 正则按 IgnoreCase 匹配、字典按 OrdinalIgnoreCase 查,`K`(U+212A)之类会抛 `KeyNotFoundException`;关于页只接三种异常,其余进 FireAndForget 日志、用户看不到,旧的「已保存到 …」也不清 → 查不到时退回通用占位符,导出前先清结果,除取消外的异常都显示出来。
- 临时文件 `.partial` 名字固定、`FileMode.Create` 会顺着预先放好的符号链接写 → 随机名 + `CreateNew`。「失败不留残件」的用例原先在目录不存在时根本没建出临时文件,测不到东西 → 改成临时文件已存在之后才失败。

**#648 渲染测量脚本**(§219):探针应用没加主题,窗口没有模板、文字不上屏、浮层取不到(`?.` 静默略过),两轮测的都是「什么都不画」—— §219「0 次停顿、保温无收益」的结论不能作数 → 加 Fluent 主题,探针没挂上就报错退出(退出码 2),`timeBeginPeriod` 只在 Windows 上调。只做了编译检查,要在 Windows 上重测。

**#649 会话日志与录制遮盖**(§220):
- 同一行里同时有 BEGIN 与 END 私钥标记(JSON 里的私钥)时整行原样落盘 → 遮掉标记之间的正文,其余规则照跑。
- 分段边界落在值开始之前(键名被切开、逐字回显、`Bea|rer`、BEGIN 标记本身被切开)就漏遮;超过扣留上限时值的尾巴漏出;无空格的 `Password:` 提示被扣到回车之后 → 把本行已写出的部分(约 512 字节)留作匹配上下文,键与分隔符立即写出、只扣值,超限的值一直遮到结束。逐个切分位置与逐字回显的漏遮从 8/20、9/20、13/34 降到 0;另用随机分段与一次写入逐字对照(20 个种子、各约 7 万次比对)。
- `KeyValue` 正则会灾难性回溯(4 KB 的 `token…` 约 30 秒),卡住读循环,关标签页时清空缓冲还在界面线程上卡十几秒 → 全部规则改成线性,16 KB 病态输入 0.2 毫秒、清空约 0 毫秒。
- 带颜色的值(jq、bat 的默认输出)与 GNU grep 把分隔符一起高亮的输出(`ESC[m ESC[K` 跟在分隔符后)都遮不住 → 分隔符前后都认 CSI 序列。
- 带转义的引号值与转义 JSON 键、值字符类吞掉 BEL 等控制字符(回放错位)、`\b` 在中文后紧跟令牌时失效 → 一并修正。
- 新增接线用例:开关开着才包遮盖、停止前先 Flush、关着时原样输出。

**#651 空回复重试**(§223):
- Agent / Plan 与桥接里,只含空白的步骤走了「中断续写」,往历史里写伪造的中断往来、界面出现中断标记,也因此不换站 → 空答(只有空白也算)原样重发;连接中断等其余重试照旧按收到的正文续写(那条规则早于 #651,加用例钉住不变)。
- 步骤内重试成功后「连接失败,正在重试」一直挂到整轮结束,空回复也说成连接失败 → 空回复单独提示(新增五语 `RetryingEmptyReply`),这一步答上就撤掉。
- 空回复后被探活自己的 15 秒截止切断时报「操作已取消」→ 报空回复。旧账号失败不冷却新账号的用例在 #651 之后已测不到东西 → 让旧账号的重试也失败,先断言确实失败。

**#652 用户手册**:对照代码更正 8 处说过头或过时的说明(中英文同步),逐条核实:检查更新会直接下载、SHA-256 只证完整性;JSON 导出只加密凭据段;代理不覆盖独立进程插件与连接诊断;插件更新只在市场给出摘要时核对、安装时核对签名;故障转移不用开启;上传已无 32 MB 上限;IM「允许的用户」留空等于白名单聊天里任何人。手册里另外几处安全说法与 #647 之前就有的代码不符,记入 `feature-plan.md` 待定。

**#655 更新日志**(§225、§226):下载期间日志窗口每次进度刷新都重算按钮、读 `CanSelfUpdate`,在界面线程上往应用目录建删探测文件 → 进度只刷新状态文字;「关闭后下载仍会继续」点过就一直挂着,失败或被当场拒绝后也不收 → 只陪着进行中的下载。

**#656 / #657 AI 上传与下载**(§227–§229):
- `ISftpService.CommitUploadAsync` 是默认实现抛 `NotSupportedException` 的接口成员,依赖注入里实际用的 `RoutingRemoteFileService` 没转发 —— 正式程序里上传永远走不到成功,还在服务器上留下整份临时文件 → 转发,用例改走真实的路由服务。
- 远端写入失败、服务器明确拒绝提交(目标已存在、不支持 posix-rename)都记成「结果未知」、不删临时文件、丢掉原因 → 只有收不到应答的提交才算未知,其余记失败、清临时文件、带上清洗过的原因;写进宿主日志的原因清洗成一行并限长(原先整段异常连换行一起进日志,能伪造日志行)。
- 用户取消、到期、断线在后端停下之前就被定成「未知」,留下半个临时文件 → 先等后端停下(最多 5 秒)再判定,提交改名的应答在这段宽限期内到了就按实际结果算;宽限期内没停下的仍记未知,但后端真停下后照样清掉没提交过的临时文件。停止原因单独登记(取消 / 到期 / 断线 / 插件停止,先到者为准),速度从开始传输算起。
- 持 `_transfersGate` 做阻塞的文件 I/O,打开 FIFO 会卡死整个插件的传输与断线处理 → I/O 移到锁外,Unix 上打开前用 `statx` / `lstat` 拒绝 FIFO、设备与套接字;登记前在锁内再核一次会话,打开源文件期间断线的不会再弹审批框。
- 删掉了默认下载目录的 `Directory.CreateDirectory`,新装机器上不给 `local_path` 的下载一律失败 → 补回;默认目标的文件名来自服务器,带盘符或冒号、是设备名、以点或空格结尾的直接拒绝。
- 文档声称 `send_file` 拒发尚未成功提交的下载目标与同名旧文件,代码里并没有 → 按文档实现(仍在进行的下载拒发,以最近一次真正开始的下载是否成功为准;已终结的下载不再把路径封死)。
- 先弹审批框或保存位置选择、之后才说宿主不支持下载 → 先查支持;`download_remote_file` 的异常处理对齐上传工具;链接下载卡片在未知状态下的取消按钮点了没反应、长寿命的授权回调捕获了整个面板 → 修正;失败的传输卡片附一句简短原因;MCP 会话只要有一个终态为「未知」(或被扣下)的上传就永不回收、32 个之后 503 → 只在有进行中的传输时保护。
- 链接下载「不支持独立下载」那句是写死的英文 → 新增五语 `TransferDownloadUnsupported`。
- 复审时撤回了给 `SerializedSftpService` 补的提交转发:正式流程不经过它,不改。

**#650 / #653 / #654 / #658**:#650 改了互操作默认端口而 WSL 备用脚本仍用 2222(修要动更早的脚本,待定);#653、#658 未发现问题;#654 升级 MCP 2.3 后 HTTP 自动探测与 SSE 回退的变化读代码判断不了,需连一次老式 SSE 服务器实测(待定)。

**验证**:每处修复都先写出在改动前失败的用例,关键几处另做变异检查(撤掉修复,用例必失败)。全解决方案 `-warnaserror` 0 警告;AI 插件 1687 通过 / 0 失败(按 CI 口径,三组 UI 分进程);宿主 2537 通过 / 0 失败 / 21 跳过;Core 1032 / 0 / 34;SSH 1378 / 0 / 70;Terminal 558;XServer 555 / 0 / 7;Presentation、Controls、Sudo.Kernel、Terminal 渲染、ShellIntegration 全部通过;Infrastructure 814 通过 / 19 跳过 / 4 失败 —— 失败的是 4 个独立进程插件用例,本机会话里插件宿主子进程起不了 Avalonia.Native 的渲染计时器(-6661),改动前同样失败。只在 macOS + .NET 11 RC2 上跑过:Windows 专用的下载用例改过但没能运行,Linux 的 `statx` 分支没有实测,也没有真实 SSH 服务器与人工 GUI 整链实测。

**留待维护者定**(已记入 `feature-plan.md`):AI 下载在 macOS / Linux 全部失败(#657 改成后台任务后只支持 Windows);手册里的安全说法与早于 #647 的代码不符(主机密钥首次直接信任、代理密码随无口令 Gist 同步上传、外部拉起的一次性连接被保存、只读放行过宽、对外 MCP 的 Chat 模式带全部工具、网络搜索默认走 VelaShell 的 SearXNG、IM 并发审批串台、插件更新摘要可缺);诊断包的收录范围与遮盖口径;录制遮盖的几项取舍(含 grep 只高亮关键词一部分时关键词被切开);上传的几项口径(含停止宽限期长短、断线记失败还是取消、进度监控失败的任务让 MCP 会话无法回收、日志里的路径);#651 两项重试口径;更新的四项;渲染停顿需在 Windows 上重测;根目录用户手册与 AGENTS.md 文档规则冲突。

**文档**:插件 README 的换源测试规则与下载说明随之更新;SDK 已是 2.1.2,README 里「版本待定」的说法改掉。velashell-docs 现有内容与代码不矛盾,无需同步修改(传输的新错误码与停止宽限期如要写进 SDK 参考,可另开文档 PR)。

## 232. 2026-10-23 AI 插件:长任务自动跨批推进,移除累计执行硬限额

**来由与决策**:用户反馈安装 1Panel 时,1M 上下文窗口只占约 4% 就收到执行预算暂停。上下文条是最近请求输入除以模型窗口,并非执行额度;原运行级 100 模型步骤、200000 输出 token、8 批次硬限额独立生效,同一提示还被 Retry-After 超过 120 秒复用。没有该用户运行日志,不能断定具体命中哪一项。经用户明确选择,取消三项固定累计硬停,不增加配置或逐段确认;持续调用与费用由用户知情接受,不是把 1M 上下文当作 1M 输出预算。

**改动**:删除运行级步数/输出 token 计数及八批终点,桌面 25 步、Bridge 20 步仅作显式边界,协调器沿用同一工具账本与 checkpoint 自动续接,保留 MEAI 内层迭代守卫以免隐式撤掉工具。五语言批次提示移除 `/8`,显示实际编号;超过 120 秒的 Retry-After 仍不提前重试,但以专用 `RetryAfterTooLong` 解释暂停。成功与失败尝试的 usage 继续完整交给展示层累计,没有更改上下文百分比、压缩、裁剪或单次最大输出。

**安全边界**:取消、审批拒绝、结果未知、同 CallId 同参复用/异参拒绝、连续相同签名的重复调用暂停和有限故障补试均保留,跨批不清安全账本。重复签名检测不是完备的循环检测,自动续接不保证任意计划完成;Bridge 原有轮次超时、供应商限制及命令/传输容量策略不变,用户可随时 Stop。

**验证**:执行恢复与五语言定向测试 48/48 通过;AI 插件全量测试 1695/1695 通过,无失败或跳过;宿主本地化、HeadlessDispatch 与对话框按钮设计门禁 20/20 通过,无跳过;完整解决方案 Debug 构建 0 警告/0 错误。回归涵盖 20/25 两种批大小分别连续完成 226 次工具调用、跨越旧 100 步与 8 批限制且始终保留工具、累计输出超过旧额度仍完成并完整报告用量、失败后恢复用量各计一次且未完成工具帧不释放、Retry-After 120/121 秒与未来 HTTP-date、跨批账本去重及重复调用保护。独立复核 PASS:重跑定向 48/48 与相关 Bridge/任务/工具/Steering/传输/Responses 回归 118/118;临时本地替身探针完成 1200 次工具副作用和 1202 次模型请求,跨 60 次续接且复用旧 CallId 不重跑,迟到 Stop/拒绝/Unknown 与重复调用分别保持停止边界。额外 whitespace 检查报错逐行对照 HEAD 均为本次未改的既有行,未扩大范围格式化。未对真实服务器安装 1Panel,未宣称供应商或人工 GUI 整链实测。

**文档**:更新插件 README 中旧「上限 25 轮」为每批步数与自动续接;同步 velashell-docs 的 `zh/plugins/11-automation-and-ai.md` 与英文镜像;`feature-plan.md` 仅移除「第 N/8 批」对应子项,其它待定事项保留。未改 SDK 或版本号,未提交、推送或创建 PR。

## 233. 2026-10-24 复审 #646–#667:换源清 Key 之后的跟进、AI 传输两端、Anthropic SDK 13、遮盖与 CI 偶发失败

**来由**:按 PR 复审 #646 之后合进 main 的全部 PR(#646–#667,#661 号空缺),每个 PR 的 diff 取合并提交相对合并前 main 的改动。#659 / #660 是对 #646–#658 的复审修复,这次连同它们自己引入的代码一起审;#662(去掉累计执行硬限额)此前没人审过;#663–#667 是依赖升级、贡献者名单与 AGENTS.md 文案。§231 已记为「留待维护者定」的条目不重复报,除非复审发现原记录不准;维护者这次拍板了两件:Anthropic SDK 保留 13.x 并补兼容,macOS / Linux 的 AI 下载实现 POSIX 安全提交。Windows 上全量构建 `-warnaserror` 0 警告,各测试工程按 CI 口径(AI 插件三组 UI 分进程)全部通过 —— #660 只在 macOS 上跑过、没能跑的 Windows 专用下载用例,这次在 Windows 上补跑通过。

**#662 去掉总上限之后的空转**:
- 参数每次都不一样的绑定失败(弱模型把 `timeoutSeconds` 传成 `"soon"`、`"later"`…)、供应商每步重发同一个 CallId 只换回旧结果,签名各异,「同签名连调四次」拦不住,批次续接下会一直重发越来越长的上下文 → 连着三步调了工具却一个都没真跑就以 `NoProgress` 暂停(改两次参数再调对不受影响)。
- `transfer_wait` / `transfer_status` 没有游标参数,轮询一个正在传的大文件第四次就被当成重复暂停 → 只读的轮询工具(`task_status` / `task_wait` / `transfer_status` / `transfer_wait`)结果在变就算有进展,原样不变才累计;有副作用的工具照旧按签名拦。`AIFunctionFactory` 把返回值包成 `JsonElement`,按原文比。
- Retry-After 超过 120 秒的暂停丢掉了原始 429:面板首步的换 Key / 换站按内层状态码判断,于是多 Key 供应商的 Agent / Plan 首步直接暂停,与 Chat 模式和 README 不一致;状态行还先闪一下「正在重试」并写进一道续不上的续接边界 → 暂停挂上原异常,先算等待再报重试。
- 断线前只收到空白也走了「续接」(送出只含空白的 assistant 消息,边界算成正文后面板不再换站)→ 与空答一样原样重发。
- 上下文条用的是一轮各步 usage 之和(早于 #662,但 §232 说的「最近请求输入」并不成立,长任务一直顶在 100%)→ 取最后一条带输入计数的用量(`ChatFormatting.LastRequestUsage`)。韩文「继续第 N 批」改成序数说法。

**AI 传输,插件侧(#656 / #657 / #660)**:
- Bridge 一轮只等传输时仍占着桥接总并发名额(默认 2):宿主确认框没有时限、批准后的大文件能传几个钟头,两个这样的聊天就把其余聊天静默堵在闸前 → 只等传输的那段让出名额(聊天闸照旧握着),下一次调模型前取回同一把闸,取回之后才恢复预算;`BridgeTurnSlot` 记账,取回途中被停掉不多还也不漏还。`localWaiting` 改成可 await 的回调。
- 观察连续失败三次就永久停盯、置运行级的不确定:卡片冻住、IM 不报终态、`send_file` 一直被挡 → 1 s 起翻倍、封顶 30 s 退避续盯,不确定只记在那一项、拿回新鲜快照即撤;登记之后发布出错也照样起观察者。
- 插件自己的拒绝(1024 回执额度、操作身份冲突)被当成「结果可能未知」停成 TaskUnknown → `FileTransferRejectedException`,回「启动前被拒」。`transfer_resume` 续不上时把终态消费掉、模型永远收不到 → 只看一眼。`send_file` 审批后再核一遍下载登记与文件。`download_remote_file` 的 `local_path` 与上传同一把尺(完整绝对路径、审批前规范化)。下载卡没快照不再打「完成」的勾;IM 进度订阅在看到的传输都已终结时释放;链接另存选到已有文件(系统框已确认替换)按 `overwrite=true` 请求宿主。工具说明不再只说「上传」。

**AI 传输,宿主侧(#656 / #657)**:
- 覆盖上传把被替换文件的权限重置成 0644(临时文件按 0644 新建再原子换入;旧的同步上传是原地写、保留模式),`~/.pgpass` 一类 0600 的文件被放宽、脚本丢了 +x → 提交前把原文件的属主与 07777 权限位带到临时文件上(setuid / setgid 仅在属主对上之后才带;带不过去又会放宽时拒绝提交),真实 OpenSSH 上验证 600 / 755 / 4755 保持。
- 所有重解析点一律当链接拒绝,OneDrive「已知文件夹移动」下的桌面与文档目录(云文件占位符)上传、下载都失败 → 只认名字代理类(符号链接、目录联接、挂载点)。本机默认的 `PHCM_DISGUISE_PLACEHOLDER` 模式下 .NET 本就看不到占位符的重解析属性,用例切到 EXPOSE 模式验。
- Windows 下载的目录「锁」只开了 `FILE_READ_ATTRIBUTES`,共享模式不生效、祖先照样能改名,提交又按完整路径重新解析 → 以 `FILE_TRAVERSE | FILE_READ_ATTRIBUTES` 打开、共享读写不共享删除,提交改用 `NtSetInformationFile` 相对父目录句柄改名(Win32 的 `SetFileInformationByHandle` 带 RootDirectory 报 87),临时文件建好后按句柄核对确在钉住的目录里;打开同一目录时清扫崩溃遗留的 `.vela-download-*`。
- 另:上传在写入前做提交预检(服务器没有 posix-rename、不覆盖而目标已存在,以前要传完整份才知道);上传临时文件改为同目录短名 `.vela-up-<16 位十六进制>`(原名多 61 字节,长文件名 ENAMETOOLONG);Unix 磁盘快照打开后立即删掉路径(崩溃不留明文副本);断线回调按 Guid 认任务(大写或 N 格式的会话 id 以前对不上)。

**macOS / Linux 的 AI 下载**(#657 之后一律失败):新增 `PosixDownloadTarget`,x64 / arm64 的 Linux 与 macOS 上 `SupportsDownloadOperations` 为真。审批前把父目录解析成真实路径并显示,从 `/` 起逐级 `openat(O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)`(Linux 加 `O_PATH`)钉住父目录 fd,链上有链接即拒;临时文件 `O_EXCL` 新建并持 `flock`,不覆盖用 `renameat2(RENAME_NOREPLACE)` / `renameatx_np(RENAME_EXCL)`、不支持时退到 `linkat` + `unlinkat`,覆盖用 `renameat` 并保留旧权限位(去掉 setuid / setgid / 粘滞位);崩溃遗留按 Windows 同一口径清扫(拿得到 `flock` 的才删)。与 Windows 只收本地 NTFS 同理,父目录须在本地按 inode 寻址的文件系统上(Linux 按 `f_type` 白名单、macOS 看 `MNT_LOCAL`),9p / SMB / FUSE / NFS 审批前按不支持拒绝 —— WSL 的 `/mnt/c`(9p)上实测祖先改名后提交没落进钉住的目录。原有的 Download 用例改为三个平台都对真实目标跑;Linux 在 WSL Debian 13 的 ext4 与 tmpfs 上全过,macOS 的常量与调用约定只经 CI 验证。

**Anthropic SDK 13**(#657 把 12.60.0 抬到 13.1.0,#665 / #666 到 13.2.1,plan 没记):13.x (a) `anthropic-version` 由 `2023-06-01` 换成 `2026-10-21`,(b) `ApiKey` 也发成 `Authorization: Bearer`、不再发 `x-api-key`,(c) `MessageCreateParams` 去掉了 temperature / top_p / top_k,`IChatClient` 适配器也不再映射(对照两版程序集的字符串核实)。只认 `x-api-key` 的中转站因此聊天 401,而自己拼请求的模型拉取照常;设置里的温度 / Top-P 在 Anthropic 协议上悄悄不发。→ 新增 `AnthropicApiKeyHandler`,只把 `Bearer <这把 Key>` 改回 `x-api-key`(登录令牌与环境变量回退不挂它,用户配置的附加头在其后施加、不被覆盖);`ApplyEndpointQuirks` 在 Anthropic 协议上显式抹掉三个采样参数,设置页禁用温度与 Top-P 并注明原因(存着的值保留)。`AnthropicWireTests` 抓真实请求头断言。思考配置与模型清单的版本没动,记入 `feature-plan.md`。

**#649 / #660 遮盖**:令牌与 `Bearer` / `Basic` 紧跟在颜色序列后面时(`ESC[0m`、`ESC[K` 的收尾字节是字母,过不了「前面不是字母数字」的回看)原样落盘 —— bat 看令牌文件、grep 整个高亮令牌都是这样 → 起头也认紧挨着的 CSI / 两字节 ESC 序列,序列原样保留。多行私钥块里 END 前面同一行的正文换成 `***`;没收尾的引号串(`password="abc def` 后直接换行)原先整段放出,现从引号之后遮到行尾(不跨行)。诊断包不再把栈帧里的 `PasswordAuthenticator.cs:line 87` 当成名字=值。复审者的模糊测试:切分泄漏 0,新旧对照没有新增泄漏。

**#655 更新**:关于页检查与无参数下载之间插进启动时的通知检查,下载找不到目标就空手返回,界面照报「已就绪」,而 #655 起「检查更新」见就绪就短路、重启失败又不收,只能「修复更新状态」→ 无参数下载没有目标时抛出,界面只跟服务的就绪快照走;换版前按快照复核已下载的包,被删或损坏就删包、丢掉就绪,下一次检查重新下载(§231 记的循环随之消失)。发布流水线:`gh release upload --clobber` 先删后传,仅同步日志的运行若断在两者之间,latest.json 就没了,下一次同步直接报错、只能完整重建 → 缺 latest.json 而校验表在时按 SHA256SUMS.txt 与 Release 现存附件的大小重建(生成逻辑抽到 `scripts/ci/release-manifest.sh`,与构建任务共用;用 1.7.3 的实际附件验证,重建结果与原件逐字节一致),两份都缺时打 notice 跳过。并发组与触发方式不动(维护者已定为等构建完再改说明,#667)。

**CI 偶发失败**(10-23 main 与 PR 上四条,重跑即过):SSH 库断开时 `TrySendDisconnectAsync` 最多等 2 秒,链路堵着时超时先到,被丢下的发送任务随后以「连接已经关闭」失败、没人观察,宿主崩溃日志里每次多一条 UnobservedTaskException(SftpTests 按进程收集这类异常,与大量上传用例并行时变红)→ 接住它的异常。另三条是用例的时序假设:读断线原因只读一次(`Fault` 先放出 `Disconnected` 再收尾通道,这个顺序是 sudo 与端口转发依赖的)、关闭状态断言抢不过测试服务端的 CLOSE、Markdown 投影只泵固定 300 ms。放大竞态窗口复现:改前 100/100、50/50、11/24、10/10 失败,改后全 0。#650 没有改 CI 上 Ssh.Tests 的并行度(仍是 2)。

**其余**:`scripts/ssh/interop/wsl-sshd.sh` 也改听 2229(#650 漏改)。渲染测量脚本零样本时报告抛异常;`Start-TestServer.ps1` 在 PowerShell 7.0 / 7.1 下 `docker inspect` 一失败就整个终止 —— 一并修掉。

**查过、没问题**:#646 / #659 的换源清 Key 确认与跨源测试;#653 默认折叠;#654 / #664 的依赖升级(LiveMarkdown 宿主与插件版本一致、MCP 2.3);#657 的取消令牌参数挪位(调用方都按名传参,挪了位置的会编译失败,不会悄悄换参);#658 的 `VelaTextTertiary` 在全部主题里都有、对比度高于原来的 Muted;#663 / #667 文案。

**留待定**(已记入 `feature-plan.md`):AI 传输安全提交的几处残留(macOS 只经 CI、NFS 按不支持拒绝、POSIX 按名字改名、Windows 祖先目录不共享删除、覆盖上传写入期间临时文件是 0644);Anthropic 的思考配置与模型清单版本;遮盖的⑥(转义 JSON 被外层引号截断、`Password:` 后跟 jq 彩色输出)。

**文档**:velashell-docs 同步 `plugins/11-automation-and-ai`、`07-capability-apis`、`12-security-threat-model`、`05-ipc-protocol`、`06-permission-system`、`协作接入`、`sdk/sdk-reference` §3.3b–§3.3c、`host/交互与界面规格`、`host/消息中心与资讯源`(zh / en 同步);插件 README 的 Agent 循环、下载平台、鉴权与采样几节随代码改。

## 234. 2026-10-24 共享密码填入:选中即自动回车提交

**来由**:用户反馈选中共享密码之后还要再按一次 Enter 才提交,中间顺手敲的字符会接在密码后面、成为口令的一部分。原先「只填正文、不附加回车」是 #628 第 1 项与 #638(§213)按当时的需求刻意定的,这次由维护者改为自动提交。

**改动**:`SharedPasswordFillRequest.FillAsync` 在正文后面紧跟一次回车,两者拼成一段、同一次经桥的 `TrySendRaw` 入队(出站输入代次照旧在同一把锁里核对),所以用户选完敲的任何键都落在提交之后,插不进密码与回车之间。回车用终端控件新加的 `EncodeEnterKey()` 取,与此刻真按 Enter 发出的字节相同(远端开了换行模式 LNM 时是 CR LF),不写死 `\r`。点选、键盘 Enter 两个入口一样;读取在途时连按的 Enter 照旧被吞掉,最后只有填入自带的那一个回车。弹层顶部的风险说明(`PasswordFill_Warning`,五语)从「不发送回车」改为「并自动回车提交」。

**验证**:`SharedPasswordFillTests` 51 条全过;原先断言「只有正文」的 7 条改为正文加回车,并断言一次写出;新增换行模式用例(CR LF),键盘用例改为填入之后再敲的字符落在回车之后(`secret\rx\r`)。临时去掉追加的回车,8 条失败。

**文档**:velashell-docs `host/交互与界面规格` §5.2.1 的点选、键盘与发送三处(zh / en);仓库内 `manual.md` / `manual.en.md` 的 5.2 与快捷键表。

## 235. 2026-10-24 VelaDock 标签条:横排可停到窗格底部、「+」新建钮、纵排溢出控件

**来由**:清 `feature-plan.md` 三项。P0「标签栏位置」:`AppearanceOptions.TabBarPosition` 与 `SettingsViewModel.TabBarPositionIndex` 的映射一直在,停靠层零消费,2026-07 设置审计 R-05 以「Dock 固定在顶部」从外观页撤下。B 段两项 P3:分屏后要在某一格新开会话只能先点那格再 `Ctrl+T`;标签条停到左 / 右侧时溢出三连钮由 `WidthOverflowConverter` 按宽度判定 —— 纵排的内容宽恒等于视口宽,三连钮永不出现,两枚滚动钮也只动 `Offset.X`。

**标签栏位置**:`DockWorkspace.TabsAtBottom` 是工作区级开关,与「多行显示标签页」同口径(所有窗格一起变)。`DockGroupControl` 把横排标签条连同 1px 分割线停到内容区下方;标签外观不变,激活强调线仍贴标签上缘(在底部正是朝内容区的那一边,与纵排时贴内侧边同理)。只管横排:右键菜单停到左 / 右是按窗格挑的(`DockGroup.TabsPosition`),不被改写,改回横排时落到设置定的那一边 —— 所以 `DockTabsPosition` 没加 Bottom,「标签位置」子菜单的横排项按设置写「顶部」或「底部」。宿主在首帧前(`ApplyShellPreferences`)、保存设置、外观预览三处应用,预览即见、取消随预览回滚;外观页在「侧边栏位置」与「多行显示标签页」之间放回下拉。

**「+」新建钮**:停靠层不认识会话,分两层:`DockWorkspaceControl.NewTabRequested(DockGroup)` 只报是哪一格,触发前先把那格设为活动窗格;宿主 `MainWindowViewModel.OpenNewTabInPane` 走 `Ctrl+T` 同一条命令 `session.new`,新文档按 §64 的落点规则落下;没人订阅时「+」不显示。24px 的 `.tab-nav` 紧跟最后一个标签(多排在最后一排、纵排在其下、空窗格在起点),溢出时钉在视口末端;标签视口在排列方向末端让出 28px,单排收窄与多排换行都按让过之后的宽度算,不会把标签条挤成溢出。可聚焦、回车可按,提示与读屏名 `Dock_NewTab`。⚠️ 布局里另有空窗格时,§64 的规则让新标签落进空窗格而不是按下「+」的那一格;要精确落点得给 `AddDocument` 加显式目标并穿过新建连接对话框,记入 `feature-plan.md`。

**纵排溢出**:转换器改为 `TabStripOverflowConverter`,按 `TabsPosition` 取轴(横排比宽、纵排比高,容差 0.5px);滚轮、滚动钮(改名 `ScrollBack` / `ScrollForward`)与启用状态共用按轴取值,纵排滚 `Offset.Y`。纵排时三连钮停在条底端排成一行,滚动箭头转成上 / 下;「所有标签页」的箭头与弹出方向朝内容区(左侧条朝右、右侧条朝左、底部横排朝上)。

**验证**:新增 17 条(headless 的标签条摆放 12 条、选项 3 条、视图模型 2 条),去掉实现后 10 条变红;headless 截图核对过三种停靠边,真主窗口没实机点过。

**文档**:仓库内 `DESIGN.md` §4.2 / §5.7、`manual.md` / `manual.en.md` §8.1 / §15.2;velashell-docs `host/settings-audit`(R-05 与新一批)、`host/交互与界面规格` §4B 与 §14、`host/dock-replacement-plan` §2.4(zh / en)。

## 236. 2026-10-24 停靠布局:命名布局模板与「按上次的分屏恢复」

**来由**:`feature-plan.md` 二、B 的 P1(上一轮「建议的下一步」第 1 条):「打开这一组机器并按这个分屏排好」。文档即活动会话,单独恢复上次的布局意义不大;同一套序列化做成命名模板,「恢复会话」顺带就有。标签的固定状态(§127)此前只活在本次运行里。核对时发现「恢复会话」(`General.RestoreSessionsOnStartup`)早就有、默认开:它把 `LastOpenProfileIds` 逐条重连进一个窗格,不还原分屏,双远端文件标签与本地终端也不在其中。

**模板结构**(`Core/Layouts/LayoutTemplate`、`LayoutTree`):一棵「分栏 / 标签组」树。分栏记方向与比例,组记标签顺序、选中下标与标签条位置,标签记「怎么重新打开它」:配置 Id 加打开方式(`terminal` / `files` / `dualFiles` / `workspace` / `localShell`),外加固定状态、是否全局活动与当时的标题 —— 同一条 SSH 配置开的终端与独立 SFTP 分得开。字段一律字符串或可空数值:文档库的 JSON 不带枚举转换器,NaN(均分)写不进 JSON;不认识的类型与标签条位置按跳过 / 置顶处理,旧版本读到新版本存的模板不会读坏。`LayoutTree.Prune` 在保存与打开两头共用:去空组、提升单子分栏(接手它的份额)、扶正「固定在前」、深度超过 32 截断(免得改坏的数据打穿栈)。

**捕获**:`Docking/DockLayoutSnapshot.Capture` 加 `Services/Layouts/LayoutDocumentDescriber`:终端按配置或 shell 类型记,独立文件标签、双远端、插件工作台按配置记;「连接中 / 失败卡片」占位按连上之后的样子记(`ConnectingDocument` 新增 `SecondaryProfile`,双远端的占位因此认得出右栏)。插件面板记不下来(没有重新打开的 SDK 契约);快速连接与外部拉起的临时配置不在库里,保存时查库剔掉;被跳过的在提示里点名。

**还原**(`Services/Layouts/LayoutTemplateLauncher`):先从库里取最新的配置,缺失的(配置已删、本机没有这个 shell、类型不认识)跳过并汇报、其余照开。按剪过枝的树搭空组骨架,`DockWorkspace.InsertLayout` 放进工作区 —— 空工作区时它就是整个布局(兜底身份交给模板里的第一个组),已有标签时整块排在右侧一栏、现有标签不动;再按模板顺序走宿主既有的打开路径(`LayoutDocumentOpener` 映射到 `TryConnectProfileAsync`、SFTP / FTP / 插件文件、工作台、双栏、本地终端),最多 6 条同时握手。标签落点由模型层新加的 `DockWorkspace.PlacementResolver` 裁决:每个槽位的打开流程在 `AsyncLocal` 里带一张只用一次、流程结束即作废的票据,所以连接流程一个参数都没加,凭据框弹多久也不会认错槽位。晚到的标签插在同组里模板顺序排在它后面的第一个已到标签之前;占位被原位换成真文档时,新增的 `DocumentReplaced` 事件把记账转过去。固定状态在插入前定下(`AddPlaced`);不是模板活动标签的不抢焦点(组里正选中的就是全局活动文档时,改选中连带改活动,免得活动文档成了看不见的标签);一个标签都没等来的组(在凭据框上取消)收尾时收掉。打开方式按「模板里的类型 + 配置此刻的连接类型」映射,配置类型后来改过时退回资源管理器双击的默认打开,而不是硬走旧路径开出一张失败卡片。

**入口与存储**:标题栏「布局模板」下拉(每个模板一项,末尾「将当前布局存为模板…」「管理布局模板…」),命令面板 `layout.save` / `layout.manage` 与每个模板一条(「布局模板」桶)。管理窗口是非模态工具窗,每行打开、用当前布局覆盖、重命名、删除;尺寸与位置记在 `layoutTemplates`(与 §241 的工具窗口同一套 `WindowLayoutStore.RememberAsync`,读布局那一下之间连点两次也只开一扇 —— 合入时补的)。保存时重名(不分大小写)问是否替换,改名撞名直接拒绝,删除走危险确认,超过 10 个标签打开前先确认(与整组打开同一阈值)。模板存在 SonnetDB `layout_templates`(一个模板一条),不进 `AppSettings`,所以不随设置导出与 Gist 同步走:同步载荷整份推拉,旧客户端会抹掉不认识的段,删除与重名冲突的合并规则也还没定,记入 `feature-plan.md`。

**按上次的分屏恢复**:新增 `General.RestoreLayoutOnStartup`(设置 → 常规 → 启动与窗口,恢复会话的从属项,恢复会话关着时置灰,默认关,当场接线)。开着时退出另记一份布局 `layout_session/last`,只记在线的标签(口径同 `LastOpenProfileIds`,不替用户去连他退出时就没连着的机器),外加本地终端与双远端文件标签;启动时按它重连并排好,没有记下的布局退回逐条重连,`LastOpenProfileIds` 照旧写。关着时退出清掉旧的那份,免得哪天再打开时还原出一份过期的。退出时的布局不查库,下次对不上的静默跳过(多半是临时连接)。开着时启动的握手从「一次全发」(#118)变成最多 6 条。

**验证**:`VelaShell.Tests` 新增 46 条、`Core.Tests` 9 条,覆盖序列化往返(含真 SonnetDB 关库重开)、还原器映射、缺失配置的剪枝与提升、固定状态、晚到标签与占位换手的顺序、票据不被后台任务复用、空组收尾、非空工作区排右侧、只在开关打开时写退出布局。临时让 `AddDocument` 不问裁决方 4 条红;去掉换手记账、票据作废、捕获固定状态、退出写布局 5 条红。实机没点过标题栏下拉的弹出位置,真实凭据框与还原交错的时序用闸门模拟。

**文档**:velashell-docs `host/交互与界面规格`(§4A.2、§4B.5、新增布局模板一节、§8、§14)、`host/settings-audit`、`host/dock-replacement-plan`、`host/SFTP双栏与WinSCP差距分析`、`host/凭据管理器集成设计`(zh / en);仓库内 `manual.md` / `manual.en.md`(3.1、8.1、新增 8.4、15.1、23.1)与 `PRIVACY.md` 本机存储表。

## 237. 2026-10-24 设置导出 / 导入按分类勾选与脱敏;密钥管理导入校验与导出;审计日志补全

**设置导出 / 导入**:
- 来由:`feature-plan.md` 🟠 P1「配置导出的选择性与脱敏」(上一轮建议第 2 条)。导出此前只抹了代理密码,仍是整份 `AppSettings`:代理的地址账号、告警 Webhook 地址(钉钉 / 飞书 / 企业微信 / Slack 的机器人地址本身就是令牌)、外部拉起的信任目标(`用户@主机`)都随文件走;导入整份覆盖,`{}` 或选错的 `package.json` 会被当成一份全默认设置收下。
- 分类:`Core/Models/SettingsCategories` 按 `AppSettings` 的实际分块切十类(常规、消息中心、外观、终端、密钥管理、快捷键、文件传输、安全审计、网络代理、X Server),根级散字段归到所在的页(语言、默认端口 → 常规;主题、强调色 → 外观;字体、回滚、TERM、编码 → 终端),反射用例钉住「每个根级属性恰好归一类」。同一份表登记机密字段(代理密码、Webhook 地址)、运行状态(窗口尺寸位置、上次打开的会话)与一次性迁移标记,导出、导入、审计三处共用。
- 导出 / 导入共用一个勾选框(`SettingsTransferView`):**安全审计与网络代理默认不勾**,勾上在下面用警示色写明带着什么(导出)/ 会改掉什么(导入:改全部出站连接的代理、关掉外部拉起确认)。〔决策〕选「默认不勾 + 警示」、机密一律不写,不做加密导出:设置里真正的机密只有两项,剔除后导入端「文件里没有就留本机的」即可;剩下的敏感内容不是密码,默认不勾就挡住了「顺手全导出」;带机密换机已有云同步的端到端口令(§13-C),再给设置文件配一套口令是同一件事两条路。连接文件导出(#571)要口令是因为那里可能有成百条密码,情形不同。
- 文件形状:仍是 `AppSettings` 的 JSON,只留选中的块,外加导出头 `velaShellExport`(格式、版本 2、时间、分类)。没包一层 —— 旧版本导入包一层的文件等于全部恢复默认,这个形状在旧版本上只是没带的分类回到默认。
- 导入:按 JSON 键认出文件带着哪些分类(旧的整份导出 = 全部),只覆盖带着且勾上的,块内没写的字段取类型默认值,运行状态留本机的。文件里没带代理密码时,代理的类型、主机、端口、用户名都与本机相同才留本机密码 —— 指向另一台代理还留着,等于把本机密码交给文件作者;Webhook 地址只要文件里没写就留本机的;认不出任何分类、类型对不上的 JSON 报「不是设置文件」。导出改取设置页此刻的值(以前分组选项带着未保存的改动、根级字段却是上次载入的值)。设置里的本机路径(下载目录、编辑器、背景图、GeoIP、X Server 程序)仍随所在分类导出(备份回同一台机器时有用)。

**密钥管理**:
- 来由:🟡 P2「密钥管理的三处小缺口」。导入只认 BEGIN 行,头尾对、正文坏了的私钥照收,连接时才报错;`.pub` 拼错了也照收,列表指纹与认证用的不是同一把;没有导出。「删除无二次确认」核对时已不成立(`KeyManagementPage.DeleteKey_Click` 早有确认框并列出将删的路径)。「已信任主机只能删不能导」那半句在 §242 做掉。
- 导入:不给口令调 SSH 库的 `SshPrivateKeyFile.Parse` 整把解析 —— 解得开取真实公钥;报要口令(库在派生密钥之前就报,结构已读通,不跑 KDF)照收,回显说明连接时要口令;不受支持(过时的传统加密 PEM、陌生算法)与损坏分别拒收,文案按失败分类出五语言,不转述库的原话;`.pub` 不是公钥、或与私钥不是同一把时拒收,一个文件都不留。`ISshKeyService.ImportKeyAsync` 改为交回 `SshKeyImportResult(Key, NeedsPassphrase)`。
- 导出:每行加「导出」菜单(`ExportPublicKeyAsync` / `ExportPrivateKeyAsync`):公钥另存 `名称.pub`;私钥先弹警示(没口令的钥谁拿到都能登录、别放网盘聊天仓库)再原样复制,Unix 上目标写入前就收窄到 0600(已存在的先改权限、新建的以 0600 建,不留宽权限窗口);Windows 不改 ACL(继承目录权限,确认框里写明)。

**审计日志**:
- 来由:🟡 P2「审计日志补全」。`audit_log` 只记连接成败、主机指纹裁决、外部拉起三类(§135);连接配置的增删改、设置变更、共享凭据一次换掉 N 条连接的登录材料(§157)都没记;查看器不能导出、不能按时间段查。
- 事件:会话仓储、设置服务、共享凭据仓储外各套一层审计装饰器(`Infrastructure/Audit/`),在 DI 里替换原注册 —— 保存点有编辑框、拖分组、证书信任、云同步、导入、登录框「同时更新共享凭据」……挂在落盘这一层一处管住,没改任何编辑框 / 设置页的保存逻辑。新类别 `profile` / `settings` / `credential`,动作 `profile-created|updated|deleted`、`settings-changed`、`credential-created|updated|deleted`,**只记哪一块变了、不记值**:配置分名称 / 地址 / 登录凭据 / 其他设置,记录带配置 Id 与 `用户@主机:端口`(改地址时写「旧 → 新」);设置按导出同一套分类;凭据记保存那一刻引用它的连接数。详情按写入时的界面语言写成一句话(与安全告警同一口径)。
- 比较的是**落盘前后库里的两份**(保存后再读一次),不是调用方传进来的对象:引用共享凭据的配置落盘会清掉手输的材料,拿传入对象比会把没落盘的密码记成「改了登录凭据」;设置则是「先比、再存」(有调用方存完还接着改手里的对象)。读不出改之前的那份就不记(不会把修改记成新建);审计写失败只 `Trace`,不让保存失败。上次连接时间、目录收藏、置顶、窗口尺寸 / 位置、上次打开的会话、迁移标记不算改动,所以连接成功的回写、退出写窗口尺寸、云同步拉下来一样的数据都不留记录。
- 查看器:`IAuditLogService.QueryAsync` 加 `from` / `to`,SonnetDB 查询改为全参数化(类别也不再拼字符串);时间段下拉(全部 / 24 小时 / 7 天 / 30 天 / 自定义起止日,按本地日期、结束日整天算、填反自动对调)下推到查询,换时段即重读、并发重读只认最后一次;标题栏「导出 CSV」导出眼前筛出来的行(UTF-8 带 BOM,含事件代码与配置 Id;以 `= + - @` 等开头的单元格补单引号防 CSV 注入,写法抽成 `Core/Import/CsvText.Cell` 与连接文件 CSV 共用);带配置 Id 的行尾有「打开配置」,借主窗口的编辑框(`OpenProfileDialogAsync` 加了可选 `owner`)以审计窗口为属主弹出,配置已删则灰掉。存储与保留期口径不变。
- 合入时补的一处:自动下载更新的迁移标记(`autoDownloadUpdatesOptInMigrated`,§241)没登记进 `SettingsCategories.MigrationFields`,第一次保存会凭空记一条「常规」设置变更 → 登记上,并加守卫 `EveryMigratedFlag_IsRegisteredAsAMigrationField`:分块里凡是叫 `…Migrated` 的布尔字段都必须登记。
- 取舍:配置事件不区分来源,云同步拉下来改了 50 条配置就是 50 条 `profile-updated`;删除分组时把配置移回未分组走的是仓储内部的批量改写、不记 —— 两处记入 `feature-plan.md`。日常小操作(终端里 Ctrl+滚轮改字号、文件面板切列、外部拉起勾「不再询问」)各记一条设置变更,没做合并。

**验证**:设置一侧 `SettingsTransferTests` 11 条、分类与 `Changed` 用例、导入导出视图模型与对话框 UI 用例,去掉字段剔除与「同一代理才留密码」4 条红;密钥一侧 `SshKeyServiceTests` 新增 9 条(0600 那条只在 macOS / Linux 的 CI 上跑),把解析换回只认格式并去掉配对核对 5 条红;审计一侧 `ConfigAuditTests` 11 条、时间段查询、`CsvTextTests` 13 条与查看器用例,7 处变异全部变红。headless 截图核对过对话框与审计窗口。

**文档**:velashell-docs `host/settings-audit`(第十五批、C-08)、`host/交互与界面规格`(设置窗口常规页、密钥管理行、§15 审计日志)(zh / en);仓库内 `PRIVACY.md`(审计日志一行)、设置页「安全审计 → 审计日志」的说明文案(五语)。使用手册的相应几段(5.3、6.1、15.1、15.4、15.8、16.4、17.1、17.4、21.2、23.1)在这批收尾时改准。

## 238. 2026-10-24 文件传输:失败自动重试接上 `TransferMaxRetries`,传输队列持久化

**来由**:P0「传输失败重试」:`TransferOptions.TransferMaxRetries` 自设置审计 R-09 起只持久化、零消费者,设置页也撤了;`feature-plan.md` 把它排在「传输队列持久化」之后,理由是「重试得知道重试什么」。核对后这个前提不成立:进程内重试要的就是 `RunTransferAsync` 手里那一项(方向、本地、远端、中转源),不需要落盘;持久化管的是断线 / 重启之后接着传。于是先独立接上重试,再做持久化。C 段「传输队列持久化」:此前断线时批次里剩下的文件一个接一个失败、刷出一排红行,失败行的「重试」又绑在已经死掉的会话上;退出时没传完的整批丢失。

**失败重试**:
- `Core/Sftp/TransferRetryPolicy` 按中立异常类型三分:**临时**(连接、超时、通道 / 协议层的 `VelaSshClientException` 基类、非服务端拒写的 `VelaSftpTransferInterruptedException`、FTP 与插件协议的连接错误、`TimeoutException` / `SocketException`、没有更具体类型的 `IOException`)、**永久**(其余 SFTP 状态码、认证与指纹、FTP 证书 / 认证 / 550、本地无权限 / 不存在 / 磁盘满 / 共享冲突、取消)、**会话已不在**(`RemoteSessionUnavailableException`,`SftpService` 找不到或没连上会话时改抛它,仍派生自 `InvalidOperationException`,按基类接的调用方不受影响)。按类型认、不按消息认,认不出的沿内层链找,到底按永久处理 —— 漏重试一个临时错误最坏就是像以前一样失败,把永久错误重试几轮却要让用户多等几十秒才看到原因。服务端拒写与断线分开:库的 `SftpTransferInterruptedException` 内层是 `SftpException` 时,`SshInterop` 置 `RefusedByServer`(磁盘满、配额再试也一样)。
- `FileBrowserViewModel.RunTransferAsync` 对上传、下载、中转的临时失败在原会话上重试,1 s 起翻倍、封顶 30 s;等待吃批次的取消令牌,期间行回到等待态、右侧写「重试 n/N」。续传开着、且这次尝试确实传出过字节,就重新探测目标长度从那里接着传(起点照常由 `SftpService` 核实);一个字节都没动就按原起点再试 —— 目标上的东西不是它写的,目录同步里目标比源短正是「内容不同」的常态;续传关着整份重来。「传出过字节」在进度接收方的 `Report` 里同步记账,不靠经同步上下文投递的 `Progress<T>` 回调。等待期间不放传输名额(链路坏了别的文件也传不动,放出去只是让它们也去撞一遍)。同会话复制不重试;面板拆掉之后不在原会话上重试(会话 id 不会复活)。
- 设置 → 文件传输 → 冲突处理放回「失败自动重试」(0–10,出厂 3,紧跟「恢复中断的传输」),`AppSettings.Normalize` 同一区间钳制。同组的 `AutoResume` 没动,续传开关仍只认 `ResumeEnabled`。

**传输队列**:
- 落点:`Core/Sftp/TransferQueueJournal` 加 SonnetDB 文档集合 `transfer_queue`(建库时创建,与 `recordings` 同形),经 `IAppDataStore` 读写。一份文档是一批里的一段(200 项):批次 Id、连接配置 Id、服务器名、开始时间、还没落定的项(序号、方向、本地与远端路径)。方向存 `upload` / `download` 文本而不是枚举数值,免得哪天调了顺序把上传读成下载、拿远端覆盖本地。改动攒 1 秒只重写变过的段,整段落定删文档,退出收尾补落一次、最多等 2 秒;坏文档读时跳过,一项不剩的批次顺手删。
- 记什么:文件面板(终端侧栏、单栏文件标签、双栏文件标签往本机的上传 / 下载)发起、且知道连接配置(`FileBrowserViewModel.QueueOrigin`)的批次整批登记;传完、跳过、永久失败的拿掉(换连接也传不过去,原因在面板与传输日志里),用户取消 / 放弃的整批删,排队、进行中、断线停下的留着。
- 断线:一项因会话没了(或临时失败重试用完)停下 → `TransferStatus.Paused`(加在枚举末尾),剩下的不再开始,整批在浮窗上挂成一行「已暂停的批次」(一批一行,带「继续」「放弃」),停下的那几个文件行撤掉;永久失败照旧标红、批次继续。面板拆掉之后冒出来的失败一律按会话没了算。重启只还原成「已暂停」(原因「上次退出时没传完」),**不外连**;有暂停行时浮窗不自动隐藏。
- 继续:`MainWindowViewModel.ResumeQueuedTransfersAsync` 按配置 Id 查库 —— 删了就如实说只能放弃,不按记下的服务器名去猜主机(主机名可能已指向别处);先找已连着这条配置的单栏文件标签,再找开过文件面板的终端标签,都没有就按连接类型开一个文件标签(认证、指纹核对、失败卡片照常),而不是替终端标签在后台建一个看不见的面板。`FileBrowserViewModel.ResumeQueuedTransfersAsync` 对每项先做续传探测,有半截从半截接着传、没有整份传;冲突策略不再问(规划时已经过了那一关,起点核实对不上时「询问」按覆盖 —— 这期间第三方在目标处放了同名文件会被覆盖,是取舍点);新的一批登记之后才撤掉旧的(中间崩掉最多两份都在,不会两份都没)。「继续」命令不用 `CreateFromTask`(那会在整批传完之前锁住其余各行的「继续」)。
- 不进队列:双栏中转(要两条连接同时在)、同会话复制(服务端一次操作)、目录同步(计划是预览里逐条确认的,重启后应重新比较)、远程编辑回传、AI 插件发起的传输(§227–§233 有自己的任务账本、逐次审批租约与回执,`CanResume=false`,宿主明确不承诺跨重启恢复;塞进这个队列等于不经审批再传一次)。

**验证**:Core 的分类表(11 个临时 + 21 个永久)与退避序列、队列的跨重启往返与分段重写;Infrastructure 走真 SonnetDB 关库重开往返;宿主 `TransferRetryTests` 13 条、`TransferQueueTests` 10 条,`TransferQueueResumeTests` 用环回 FTP 服务器走真实的「继续 → 打开文件标签 → 上传」整链。DockerIntegration 的慢中继掐断用例真跑:真实断链判为临时、`RefusedByServer=false`。逐处临时去掉实现(次数强制 0、去掉「没传出字节按原起点」、不设 `RefusedByServer`、不挂暂停、接着传照走冲突策略、构造时不还原)对应用例变红。headless 截图后把暂停行的「继续 / 放弃」从禁用改成隐藏(禁用的透明按钮会画出灰底块)。已知漏网:续传下载与双栏中转直接读库交出的 `SftpFileStream`,断线时冒的是库自己的异常(没经 `SshInterop` 翻译),认不出、按永久处理,记入 `feature-plan.md`。

**文档**:velashell-docs `host/settings-audit`(R-09 / N-07 / M-02)、`host/交互与界面规格` §9(zh / en);仓库内 `manual.md` / `manual.en.md` 9.3、15.7。

## 239. 2026-10-24 文件侧五项:SFTP 文件名按会话编码、内置编辑器临时副本清理、目录收藏两处边角、FTP 服务器时区、远端搜索

**SFTP 文件名按会话编码**:SSH 库默认按 UTF-8 解文件名、解不开的字节无损往返,GBK 服务器上的中文名打得开、删得掉,却显示成一排替换字符,而同一台机器的终端早就按会话编码显示得好好的。库早有 `SftpOptions.FileNameEncoding`,宿主一直没接:`SshSession` / `ConnectionInfo` 不带编码。`ConnectionInfo.FileNameEncoding` 由 `ConnectionWorkflowService` 建连时按 `SessionTerminalSettings.Encoding`(会话覆盖优先于全局)算出,只带在终点那一跳(跳板只开隧道);Infrastructure 新增 `SftpConnectOptions`,字符集是 UTF-8、空白或认不出时返回 null 让库走默认的无损往返(⚠️ UTF-8 时传了就丢掉无损往返),否则只改这一项、其余参数保持库默认;传输用的第二条 SFTP 通道走同一个工厂。取的是连接那一刻的会话编码,状态栏临时切换只作用于终端。`SftpFileNameEncodingTests` 用内存 SFTP 服务端放 GBK 原始字节的名字,GBK 会话下列目录、按中文名打开与删除都落在原来那串字节上,UTF-8 会话下照样无损往返。

**内置编辑器临时副本**:「打开」(内置编辑器)把文件下到 `%TEMP%/VelaShell/builtin-edit/<8 位十六进制>/`,关窗时 `RemoteFileEditorView.OnClosed` 删整个子目录;崩溃、被结束时这一步没机会跑,副本(可能是敏感配置)一直留着。新增 `BuiltInEditTempFiles`(根目录、分配子目录、`SweepStale`),挂在 App 启动时本来就有的后台清日志任务上:只看名字是 8 位小写十六进制的子目录、跳过重解析点,目录及其全部内容的最晚修改时间早于 7 天才删,删不掉留给下一次,不提示。〔决策〕刻意不碰同级的 `remote-edit`:外部编辑回传失败时 `RemoteEditSession.Dispose` 刻意保留子目录并告诉用户位置,那是改动唯一的存身之处,而它在磁盘上与「崩溃时正开着的会话」分不开(后者同样可能带着没传上去的改动),两种都不能按时间删,记入 `feature-plan.md`。内置编辑器没有这种草稿:回传失败时改动还在窗口里,关窗要先确认丢弃。

**目录收藏的两处边角**(§222 留下的):资源管理器「复制配置」拷的是树缓存里的那份,树加载之后才在 SFTP 文档里加的收藏只经 `UpdateDirectoryBookmarksAsync` 落库、不回写缓存 → `DuplicateSelectedSessionAsync` 先 `GetSessionAsync` 取库里最新的一份再 Clone,读不到才退回缓存。Gist 拉取对连接配置整条覆盖(`ReplaceSessionAsync`),旧版本推上去的载荷根本没有收藏字段,而 `SessionProfile.DirectoryBookmarks` 有 `= []` 初值,反序列化后与「对方清空了收藏」一模一样 → `GistSyncService` 的 JSON 选项挂一个 `TypeInfoResolver` 修饰器,反序列化 `SessionProfile` 前把收藏置空,字段存在(含 `[]`)才写回;拉取时仍为空就说明载荷没带这个字段,改走 `SaveSessionAsync` 在仓储锁内保留本机收藏。修饰器只挂在同步自己的选项上(加密与明文两条路径都经它),仓储与导入 JSON 备份不受影响 —— 导入备份的同一个问题记入 `feature-plan.md`。

**FTP 服务器时区**:LIST 只给服务器墙上时间、不带时区,比较器只能当本机时间读,服务器在 UTC、本机在 UTC+8 时目录比较与同步里所有远端时间整体错开 8 小时,一律成了「较新」(§74)。先实测 FluentFTP 55 在默认 `TimeConversion=ServerTime` 下 LIST 的时间是 `Unspecified`、MLSD / MLST 是 `Utc`(环回 FTP 服务器为此加了可选的 MLSD / MLST 与 LIST 时钟偏移),所以只需要挪 `Unspecified`。`FtpSettings.ServerUtcOffsetMinutes`(`int?`,null = 与本机相同即旧行为,UTC−12:00…UTC+14:00 以外归 null,`Clone` 照抄;`FtpSettings` 是 `SessionProfile.Ftp` 的嵌套对象,§37 名单只需补 `ConnectionProfileViewModel` 的载入与 `BuildProfile`);FTP「高级」页加「服务器时区」下拉(「与本机相同」+ 38 个现实偏移,手改出来、不在表里的偏移按大小插进去保留)。〔决策〕存服务器的 UTC 偏移而不是「比本机快几小时」:本机换夏令时、带电脑出差都不用改配置;服务器自己换夏令时那半年要手改一次,固定偏移只能如此(WinSCP 也一样)。`SyncTime.ToUtc(raw, offset)` 只挪 `Unspecified`,精度仍按换算前的原始值推断(「只有日期」的 00:00 挪过偏移就认不出来了);按日比较时两边放进服务器那本日历截断(`SyncItem.CalendarOffset`),否则晚上改的文件会落在相邻两天。同步窗口(含保持远端最新)、单栏与双远端「比较目录」都按配置换算;文件面板的时间列照旧显示服务器自己的钟,同步选项本身仍不持久化。集成用例把服务器钟设成本机 ±3 小时:LIST 不配偏移判成不同、配了判相同,MLSD 配着同样的偏移也判相同。

**远端搜索**(C 段,MobaXterm / WindTerm 都有,差距分析 3.5 / C2):
- 入口:SSH 会话文件面板的工具条(放大镜)、空白处右键「搜索远端文件…」、目录行右键「在此目录中搜索…」;终端侧栏、单栏 SFTP 文档、双远端里是 SSH 的那一栏都有。FTP 与插件协议没有 exec,`CanSearchRemote` 为假、入口不出现。一个面板一扇非模态窗口,面板拆掉(标签关闭、会话断开)窗口随之关。
- 窗口:搜索位置(默认面板当前目录)、文件名(不带通配符按「名字含有」)、包含文字(按字面、只查文件)、忽略大小写(默认开),回车即搜,结果逐条进列表;双击、回车或行尾按钮在面板里定位 —— 目录进去,文件进所在目录并选中、滚进视口;被隐藏的点文件只在这个面板打开「显示隐藏文件」、不写回设置。Esc 在搜着时先停,再按才关。
- 先判 POSIX:`Core/Sftp/RemoteSearch`(纯函数)+ `RemoteSearchService`,用 `RemoteShellProbe` 判远端 shell(按主机缓存)。NonPosix(Windows 的 cmd / PowerShell、csh 一类)说不支持,Unknown(exec 被禁、只开 SFTP 的账号、超时)说判定不了,两种都**一条命令都不发**(§18-B/E 的教训);bash、zsh、POSIX sh 与 fish 可以搜。
- 不留注入口:命令是 `sh -c '<脚本>' vela-search '<根>' '<模式>' '<上限+1>' ['<文字>']`,脚本本体是写死的常量片段,用户输入只作单引号位置参数、脚本里以 `"$1"` 引用不二次展开;引号按登录 shell 规则写(POSIX 用 `'\''`,fish 的单引号里反斜杠翻倍、引号写 `\'`)。只用 BusyBox 也有的 `find -mindepth/-name/-iname/-type/-path/-prune/-exec … {} +`、`grep -l -s -i -F -e`、`head -n`、`[ -d ]`、`printf`;从 `/` 起找才跳过 `/proc`、`/sys`、`/dev`,不跟随链接。
- 上限与取消:500 项(远端 `head -n 501` 收满即退、`find` 随即被 SIGPIPE 收掉,多出的那一条用来判「结果不全」)、2 分钟,都写死不做设置;取消时库先发 `TERM`,脚本 `trap` 再 `kill 0` 收掉整个进程组 —— 管道放后台再 `wait`,因为 sh 在前台命令跑完前不执行 trap。状态行各报各的(找到 N 项 / 到上限不全 / 超时不全 / 已停止 / 会话断开 / 不是 POSIX / 判定不了 / 缺 find·grep / 失败原文)。
- 验证:本机真 sh(Linux GNU、macOS BSD、Windows 上 Git for Windows 的 sh)与 ssh-shells 靶机上 bash / zsh / fish / dash / ash × BusyBox 1.37 实跑,名字里带 `'`、`$(touch …)`、反引号、`;&` 时确实找得到、确实没被执行;去掉 POSIX 判定或改用双引号传参,相应用例变红。
- 已知边角:结果路径按 UTF-8 解,没跟上面的会话编码(GBK 服务器上的中文名显示成替换字符、点了只进得到所在目录);名字里含换行会拆成两条;极老、裁掉了 `-exec … {} +` 的 BusyBox 上按内容搜索报 0 项而不是报错。

**验证**(合计):五项各自的用例逐项临时撤掉修复都变红;整体 `-warnaserror` 0 警告,各测试工程按 CI 口径全过,`RemoteSearchDockerTests` 6 条真跑、没有 `[SKIP]`。headless 截图查出搜索窗口状态行的警示色被本地 `Foreground` 盖住,改成两条样式类。

**文档**:velashell-docs `host/交互与界面规格` §6、§13.1、`host/SFTP双栏与WinSCP差距分析`(zh / en);仓库内 `manual.md` / `manual.en.md` 7.2、9.2、9.3、9.4、9.5、10.3。

## 240. 2026-10-24 会话与工作区:会话树过滤与命令面板检索词、补全里的快捷命令占位、`-newtab` 标签名、按会话标签页图标、整组「打开并同步输入」

**会话树过滤与命令面板检索词**:`SessionProfile.Tags` 能编辑、导入导出也带着它(§180),会话树与命令面板却都不读,存了等于没存;连接备注(§152)也只在悬停时看得到。资源管理器顶上加过滤框 `SessionTreeFilterBox`(展开的侧栏与折叠细条的浮层各放一个,绑同一个 `SessionTreeViewModel.FilterText`,钉在树的上方、不跟着树滚)。规则在 `Presentation/ViewModels/SessionTreeFilter`:按空白拆词,**每个词都要命中**,不同词可以落在不同字段(名称 / 主机 / 标签 / 备注 / 所在分组名,不分大小写的连续子串);用户名不参与(输个 `root` 几乎全留)。有命中的分组留下并临时展开,没命中的(连同置顶项)藏起来;过滤期间的展开 / 折叠不记进展开态记忆(#474),清空后每个分组回到过滤前的样子,整树重建(编辑、云同步)时重新撑开有命中的分组。被过滤掉的选中项清掉(右键菜单不能对着另一行),跟随活动标签不去选看不见的行,过滤时不能拖分组(插入线只能画在看得见的行之间,落点会骗人);框里 Esc 清空,只剩一条时 Enter 直接连。过滤只改显示,分组菜单(打开全部 / 导出 / 删除)仍作用于整组,要对过滤结果动手用多选。命令面板:`CommandPaletteItem` 加不显示的检索词(会话条目依次是标签、主机、备注),`PaletteScorer` 新增带检索词的重载 —— 标题前缀 / 词首 / 连续子串照旧最高,检索词前缀命中 1600、中间命中 1400(按检索词先后各减一点,标签压过备注),排在标题连续子串之后、标题子序列之前;检索词只比连续子串(备注是长文本,按子序列比几乎全中)。全局「聚焦过滤框」快捷键没加(`Ctrl+Shift+E` 空着,要加就走 `ShortcutCatalog`)。

**补全里的快捷命令占位**(§132 记过「补全插入的仍是含占位的原文」):`CommandSuggestion` 带上正文相同的那条 `QuickCommand`(去重时历史常常赢,同文本的历史条目也挂上),来自快捷命令且有 `{{变量}}` 时,补全面板里选中它、或用 Right/End 接受这样的幽灵文本,先经 `TerminalTabViewModel.QuickCommandTextResolver` 弹与快捷命令区**同一个**询问框(两个入口都走 `MainWindowViewModel.ResolveQuickCommandTextAsync`,`QuickCommandExecutionRequest` 加 `CommandId`),确认后按原来的续写规则写进命令行(与已键入的同前缀只写剩下的,否则先回删);取消、询问框出错、弹框期间标签断开 / 视图换了数据上下文 / 命令行变了,一律一个字节都不写。新 `Services/QuickCommandVariableMemory` 按「快捷命令 id(没有 id 时按正文)+ 占位名」记上一次填的值,下次预填(压过占位自己的默认值)。〔决策〕**只在进程内**:不落盘、不进同步载荷与导入导出 —— 占位里可能是 token 或口令,落盘就得定级、加密、写进导出口径,而这项便利要的只是「连着重复用」;名字像凭据的占位(含 pass / pwd / secret / token / otp / credential / apikey / privatekey,及中日韩的密码 / 口令 / 密钥 / 令牌 / 验证码等)连进程内也不记(询问框是明文输入框,预填等于把口令摆在屏幕上);留空的那格不记并忘掉旧值,最多记 200 条命令。记忆按「快捷命令 + 占位名」分开:`logs {{pod}}` 填过的不会预填到 `exec {{pod}}`。

**`-newtab` 标签名**(§84 之后只是被忽略):JumpServer 发的是 `-newtab root@Linux[时间戳] -url ssh://JMS-x:口令@堡垒机:2222`,某 SSO 客户端发 `-newtab CBH_10.1.2.3_22_root`,目标全是同一个堡垒机地址,开出来的一排标签名字都一样。`ExternalLaunchRequest.TabName`(JSON 里是 `tabName`,随单实例转发的那份 JSON 过去;旧版本收到会忽略、新版本收到旧 JSON 为 null);`XshellLaunchParser` 把不带 `://` 的 `-newtab` 值收作标签名(带 scheme 的仍是目标);`NormalizeTabName` 去首尾空白与引号、控制字符与空白折成一个空格、超过 128 个 UTF-16 码元截断(不在代理对中间切),转发两端各规整一次(管道那头可能是任何同用户进程)。确认框在「凭据」下面单列一行「标签名」;连接时调新重载 `TryConnectProfileAsync(profile, tabTitle)`,SSH 终端标签与 SFTP / FTP 的占位和成品标签都用它当标题。**配置本身不改名**(复用已保存配置时改名会随最近连接时间落盘),状态栏仍是配置名 / 真实目标,审计记录不变 —— 标签名是调用方给的、只是显示,不参与信任判据。

**按会话标签页图标**(§68 的协议图标答「哪一种连接」,颜色答「哪一台」,这项答「这台是干什么的」):`TerminalOverrides.TabIcon`(lucide 字形名,如 `database`;null = 跟协议,进 `Clone` 与 `IsEmpty`);清单在 Core 的 `SessionTabIcons`,23 枚,名字描述「画的是什么」而不规定含义,认不出的 id(更新的版本加的、手改的)当作没挑;`Icons.axaml` 新增七枚 lucide 字形。`ConnectionIcon.ForSession` 改为 用户挑的 > 插件自报的 > 协议默认,终端 / SFTP / 双栏 / 插件工作台四种标签模板都经它取图标,模板一行没动;资源管理器挑过的行在状态圆点与名字之间画 12px 的那一枚(颜色跟名字走),没挑的不画也不占位。挑选用新控件 `Controls/IconPickerField`(外观与 `ColorPickerField` 同款,底部「跟随协议」),放在连接对话框「常规 → 整理」—— 颜色所在的「终端」页只有 SSH 才有,而图标对 SFTP / FTP / 插件标签也画。字段放在 `TerminalOverrides` 而不是 `SessionProfile` 顶层(与标签颜色同是「这个标签长什么样」),导入导出 JSON、Gist 同步、SonnetDB 落盘都自动带上,CSV 不带(颜色也不带)。已开着的标签不会因改了配置而换图标(与颜色一样建标签时取一次),资源管理器保存后整树重建、立刻更新。⚠️ `SessionProfileCloneTests` 原先的反射比对有个漏洞:样本里留着默认值的字段,源与副本都是默认值,漏抄也照样全绿 —— `IsPinned` 留着 false、`Ssh` 整个没设,两处从没被真正比过。补了守卫 `FullyPopulatedSample_LeavesNoPropertyAtItsDefault`(样本里每个可写属性,含 Terminal / Ftp / Ssh / 目录收藏那一层,都不许是默认值),并让嵌套比较按元素比列表。

**整组「打开并同步输入」**(§180 已有整组 / 多选打开,同步输入的频道 `SyncInputCoordinator` 本就是对等广播):分组菜单加「打开全部并同步输入」、多选菜单加「打开 N 个连接并同步输入」(`link-2` 图标),经 `SessionTreeViewModel.OpenManyInSyncChannelRequested` → `SessionTreeActions`(超过 10 条先确认)→ `MainWindowViewModel.OpenProfilesInSyncChannelAsync`。只开终端:SSH 一律算,插件协议问注册表(工作台不算,其余 `ResolveAsync` 后看有没有终端实现,可能触发惰性激活,激活失败当不是终端),SFTP / FTP 跳过;一条终端都没有就提示并返回。频道挑第一个空着的(A → D:没有任何标签在里面、也没被另一批正在开的预订),四个都在用就一个都不开 —— 混进有成员的频道,这批命令会打到不相干的机器上。**总是新开标签,已经开着的同配置标签不动**(它可能正停在 vim 里或在别的频道),所以同一台机器可能有两个标签,但只有新开的那个在频道里,命令不会打两遍。并发与 §180 相同(最多 6 条同时握手,`OpenProfilesAsync` 抽出 `OpenProfilesCoreAsync` 共用);每条连完(连上,或失败留着覆盖层)就入频道,失败的之后重连上来照样在频道里,但握手期间敲的字它收不到,所以全部连完的浮层要报「几个没连上」与跳过了几个非终端。

**验证**:过滤与检索词 15 条(临时让过滤恒通过、检索词不参与打分,12 条红);补全占位与记忆 17 条(去掉「先问再写」与记忆,9 条红);`-newtab` 的解析、单实例转发与标签标题(去掉名字的解析与使用,3 条红);图标 11 条与 Clone 守卫(去掉自定义优先、去掉 `TabIcon` 的拷贝、样本里去掉它,三处各自变红);同步频道 5 条(去掉入频道与占用判断,2 条红)。headless 截图看过 23 枚图标、会话树三种状态与图标浮层,换掉了与 `cpu` 几乎一样的 `microchip`(改用 `map-pinned`),修掉浮层格子里的图标看不见(`LucideIcon.Foreground` 不从按钮继承)。

**文档**:velashell-docs `host/交互与界面规格` §3、§4B.2、§6.1、§8、§12、§13.1、§14.2,`host/Xshell兼容登录` §2,`host/会话导入` 7.2(zh / en);仓库内 `DESIGN.md` §5.2(IconPickerField)与 §5.4、`manual.md` / `manual.en.md` 4.1、4.2、7.5、8.2、8.3、20.3。

## 241. 2026-10-24 宿主杂项:自动下载更新、工具窗口记住位置、Linux 更新直接给安装包、靶机镜像版本核对、插件日志查看

**自动下载更新**(一、P0):`AppSettings.AutoDownloadUpdates` 默认 `true`,却零消费者、零界面(设置审计 R-03)。关于页手动「检查更新」本来就会接着下载(§209 / §222 / §226 的下载、SHA-256 校验、就绪快照与换版前复核都在),缺的是启动检查这条路。
- 默认改为关,存量配置按 `AutoDownloadUpdatesOptInMigrated` 关一次(落盘的 `true` 只能是旧默认值,没有地方能选;与录制、指纹阻断同一套路),之后听用户的。理由:它比单纯的检查多一次几十 MB 的外呼(维护约定第 6 条)。
- 设置 → 常规里紧挨「启动时检查更新」,检查关着时置灰;只在 `CanSelfUpdate` 为真的安装上出现 —— Linux、商店版、只读目录上它什么也不做,摆出来就是又一个「界面在骗人」。
- 启动这一轮拆开投:`NotificationSources.CollectUpdateAsync` 管更新,资讯源与崩溃提示先投,不再排在可能几分钟的下载后面。总闸仍是启动检查;开着时「提醒可用更新」与「自动下载更新」至少一个开着才查(以前通知关着就不查)。查到新版本且能原地换版时,经新的 `BackgroundUpdateDownload` 委托交给 `SettingsViewModel.DownloadUpdateInBackgroundAsync`:与关于页同一条下载链路、同一个忙状态,关于页开着时看得到进度、「检查更新」不会并发再起一份,关于页那一路正在下载时后台这一路让开;委托在 DI 里惰性取设置视图模型,不为默认关着的开关在启动时造出整个设置页。
- 结果:下好了投 `update-ready:<版本>`「VelaShell X 已下载好」(点它到关于页「重启并更新」),不自动重启;id 与 `update:<版本>` 分开,先前读过「有新版本」的这次照样亮起。下载失败不弹任何东西,消息中心退回普通的「有新版本」,下次启动再试。已有就绪包直接认,不再下载。
- 顺带:启动这一轮查更新前设置快照还没缓存时先真读一次 —— 原先用 `new()` 兜底,关掉「启动时检查更新」的人在那一刻也可能被当成默认值(开)发出请求。
- 真实下载与重启换版没做实机验证(只读 / 可写目录与 macOS 只经单测)。

**非模态工具窗口记住位置**(二、B):`WindowLayoutStore` 只记尺寸,§130 的位置记忆只管主窗口,多屏用户每次都要把审计日志、回放中心、任务管理器重新拖到副屏上。`WindowLayout` 加 `X` / `Y`(物理像素,与 `Window.Position` 同口径;可空而不是拿 0 当「没记」,多屏时 0 与负坐标都正当)。关窗时普通态读当下的尺寸与位置,最大化 / 最小化沿用上次记录 —— 工具窗口不像主窗口那样平时跟着记,省掉 X11 / macOS 上状态事件与坐标先后不定的那一摊。打开时复用 `MainWindowPlacement.TryRestorePosition` 的「标题栏还在某块屏工作区里抓得住」核实,屏幕没了照窗口自己的 `CenterOwner` 居中。新增 `WindowLayoutStore.RememberAsync(key, window)` 一处挂好读 / 套 / 写回:任务管理器、资源监视、链路追踪改用它,插件管理(`pluginManager`)、审计日志(`auditLog`)、回放中心(`recordingPlayer`)、插件日志(`pluginLog`)新接上,布局模板的管理窗口(`layoutTemplates`,§236)与 SSH 连接信息窗口(`connectionInfo`,§248)是合入时补接的;同类窗口共用一条记录。上传选择框是模态框,仍只记尺寸与视图;SFTP 文档里开的目录收藏 / 目录同步窗口按文档开关,原本也不记尺寸,没接。macOS / Linux 实机未看(同 §130)。

**Linux 检查更新直接给对应安装包**(二、G,§172 起只打开整页发布列表):`AppPackaging.LinuxInstall` 按打包脚本定下的事实判断安装方式、不猜发行版:`APPIMAGE` 有值且应用目录在 `APPDIR` 挂载点里 → AppImage;应用目录是 `/opt/velashell` 且有 `/var/lib/dpkg/info/velashell.list`(或带 `:amd64` / `:arm64`)→ deb,没有 dpkg 记录而有 rpm 数据库(`/var/lib/rpm` 或 `/usr/lib/sysimage/rpm`)→ rpm,都对不上算不知道;其余位置 → tar.gz 便携包。只看文件系统痕迹,不起 `dpkg -S` / `rpm -qf` 子进程。`LinuxReleaseAsset` 照发布命名拼出那一个产物在检查选定标签下的下载地址(便携包直接取清单登记的 tar.gz,AppImage 是 `VelaShell-<版本>-<RID>.AppImage`,deb / rpm 跟随发行版惯例),拼法与更新器认的附件名同一把尺。预发布版的 deb / rpm 名字带 `~`,GitHub 附件名对这类字符的处理没有可依赖的约定(仓库也还没发过预发布版),不猜,打开那个版本的发布页;已知安装方式但拼不出名字时同样回落 tag 页;认不出安装方式或没有清单照旧打开发布列表。`IUpdateService` 新增 `AvailableUpdate`(检查选定的清单),标签从它取,检查与打开浏览器之间发了新版也不会指错。三个打包脚本与两份 README 注明改命名要同步 `LinuxReleaseAsset.cs`。`LinuxReleaseAssetTests` 23 条按 1.7.3 Release 的实际附件名对照,拼出的 1.7.3 deb / rpm / AppImage 地址 `curl -I` 均 302;Linux 视图模型用例只在 CI 的 Linux 上跑,真实桌面浏览器下载未实测。

**靶机镜像版本核对**(一、测试与本机环境):`velashell-test-shells` 建于 Dockerfile 给 `vela-dash` 关 X11 之前时,`X11_RefusedByServer_KeepsAgentForwardingAndWarnsOnce` 一直红(§144、§159、§167、§186),红得像代码回归。ssh-shells 的 Dockerfile 末尾 `COPY Dockerfile /etc/velashell-fixture/Dockerfile`,镜像自带构建它的那份;`tests/VelaShell.Tests/TestSupport/FixtureImage.cs`(链接进 Core.Tests 与 ShellIntegration.Tests)在环境检查时经 SSH `cat` 出这份文件,与仓库里的比「有效内容」(去掉 CR、整行注释、空行与行尾空白,与 Docker 自己解析时丢掉的一致),不一致或镜像里没有这个文件就 `[SKIP]` Inconclusive 并给出 `docker compose -f docker-compose.test.yml build ssh-shells && … up -d ssh-shells`。〔决策〕不手写版本号 —— 改 Dockerfile 的人忘了抬号,正是这条要防的「改了没重建」。结论按靶机在进程内只算一次;仓库里找不到 Dockerfile 或 SSH 连不上就不拦(判断不了不等于旧了)。接进 `SshSessionFeaturesIntegrationTests`、`NestedShellFollowDockerTests` 与 `ShellIntegrationHarness.RequireContainer`;只用到基础镜像的 SHA-256 用例与 SSH 库的互操作用例没接,其余靶机(2fa / legacy / pq / sudo)也没接,`tests/fixtures/README.md` 写了怎么接。⚠️ 比的是整份 Dockerfile 的有效内容,改任何一条指令都会让这三组用例整体跳过到重建为止(只改注释不会);本机 2026-10-19 建的旧镜像(没有版本文件)上三组 56 条全部走了「旧了 → 跳过」,要重建一次。

**插件日志查看**(二、G,`plugins/STATUS.md` 列为未做):管理页能启停、能撤授权,唯独看不了某个插件的日志尾部。插件日志原先只进宿主 Trace(`~/.velashell/logs/velashell-yyyyMMdd.log`,带 `[Plugin:<id>]` / `[PluginHost:<id>]` 前缀),与宿主几十种输出混在一起,异常堆栈的续行没有前缀、按前缀筛会丢,一天的文件能到 64 MB;velashell-docs `plugins/02-architecture` 写的 `logs/<pluginId>/` 并没有实现。
- 按插件另落一份:`PluginLogFiles`(Infrastructure)写 `logs/plugins/<插件 id>.log`,满 1 MB 轮转成 `.1.log`(只留一份,每个插件最多约 2 MB);UTF-8 无 BOM、一行一个时间戳、文本自带的换行(异常堆栈)原样保留;任何情况下不抛。读尾部默认 256 KB,当前那份不够时从轮转那份补前文,截在中间的那一行丢掉。插件 id 再按 `[A-Za-z0-9._-]` 守一道,拼不出目录外的路径。
- 来源全在宿主侧,不动 SDK:`TracePluginLogger` 带上它 —— `IPluginLogger` 写的(进程内直调、隔离进程经 RPC 转回)、隔离进程的 stdout / stderr(`PluginProcessClient` 的输出泵改成交给它)、宿主替插件记的激活 / 激活失败(带完整异常,管理页那一行只放得下 Message)/ 崩溃,都进同一份;宿主日志里那份照旧。卸载时随插件数据一起删;`logs/plugins/` 是子目录,诊断包照旧只收 `velashell-*.log` 与 `crash-*.txt`(没有扩大 `PRIVACY.md` 里诊断包的口径)。插件进程里的 `Trace.WriteLine` 仍不收集(PluginHost 没挂监听器)。
- 界面:插件管理页每行最前面加「查看日志」(清单有效的插件都有 —— 禁用、激活失败时正是最要看的时候);`PluginLogWindow` 是 28px 标题栏加刷新 / 打开所在文件夹,副标题是文件路径(只取了尾部时注明),正文等宽、只读、不折行,打开即滚到最后一行;按插件 id 去重,以插件管理窗口为属主。不做跟随滚动的实时流,点刷新即可。
- 用真夹具插件的 inProcess 与 isolated 两种模式激活后,经 `PluginManager.ReadLogTail` 读得到插件自己写的那行与宿主记的「已激活」;临时去掉写文件那一行 3 条红。

**插件状态文案**(合入时补的,velashell-docs 文档组写插件管理窗口一节时查出):`PluginState.Deactivated` 没有对应的 `PluginState_Deactivated` 键,而状态列按 `$"PluginState_{State}"` 拼键,真显示出来就是原样的键名 → 五语补上「已停止」,并加守卫 `EveryPluginState_HasAStatusText`:每个 `PluginState` 都要有文案(拼出来的键,静态扫描与豁免表都看不出)。`PluginManagerWindow.axaml` 注释里的「40px 标题栏」改成实际的 28px。

**验证**(合计):自动下载 18 条(去掉实现 5 条红)、窗口位置 7 条(去掉摆位置 2 条红)、Linux 安装包 23 条(恒回落发布列表 13 条红)、靶机核对 8 条纯函数用例、插件日志 16 条;宿主全量按 CI 口径全过。

**文档**:仓库内 `PRIVACY.md` 第 4 条「检查更新」(中英,补上自动下载)、`manual.md` / `manual.en.md`(2.4、15.1、第 18 节、23.1)、`tests/fixtures/README.md`、`src/VelaShell.Infrastructure/README.md`;velashell-docs `host/settings-audit`(R-03)、`host/消息中心与资讯源`、`host/交互与界面规格`(独立窗口、关于页、§11A 插件管理窗口)、`host/架构设计`、`plugins/STATUS`、`plugins/02-architecture`、`sdk/sdk-reference`、`templates/dev-guide`(zh / en)。

## 242. 2026-10-24 主机证书接进宿主、known_hosts 导入导出;ssh_config 互通与导入 / 外部启动的证书字段

**主机证书与 known_hosts**:SSH 库 §113 已实现 `*-cert-v01` 与 `@cert-authority`(velashell-docs `ssh/spec/03-key-exchange` §5.5)。宿主 `VelaHostKeyPolicy` 走自己的信任库,没有受信 CA 的概念;证书被当成里面那把钥的普通主机密钥,默认算法清单里证书又排在后面,实际谈不成证书;已信任主机只能看和删。
- 信任库:新集合 `known_host_markers`(`KnownHostMarker`),受信 CA / 吊销两种,按主机名模式生效,存公钥原文;种类零值 = 吊销(读坏的记录只会多拒一次,不会变成受信 CA)。〔决策〕CA 记在宿主自己的信任库里,不在每次连接时去读 `~/.ssh/known_hosts`。
- 裁决:记录换成库的 `KnownHostEntry` 交 `KnownHostsFile.Lookup`(取反、散列、证书的类型 / 签名 / 有效期 / 主体 / 关键选项都按库的那一套,库一行没改),顺序同 §5.5:吊销压过一切 → 单独记着的钥优先 → 对上的 CA 签的合格证书放行、不记进信任库 → 不合格拒绝 → **CA 管的主机出示没担保的钥 `RejectChanged`、不问不记**(首次确认、变更阻断开关怎么设都一样,自动重连也一样)。CA / 吊销记录读不出 → fail-closed;排算法时读不出 → 忽略。有对上的 CA 时 `IHostKeyTypePreference` 交出证书类型、证书算法排前,这台主机记着的普通类型仍在前(那把钥已经被明确信任)。不合格的原因由库的结构化出处出文案,见 §246 末段。
- 弹窗:指纹确认框在出示了证书却没有受信 CA 担保时多一行「证书签发 CA」(指纹 + 添加入口)。〔决策〕刻意不给「信任这个 CA」按钮 —— 从服务器出示的证书去信任 CA,本身就是盲信。信任时记下公钥原文(`KnownHost.HostKey`),老记录下次信任连上时补。
- known_hosts 导入:多主机、`[host]:port`、同行取反;`|1|` 散列行拿已保存的 SSH / SFTP 连接与已信任主机逐个对,对不上的计数;带通配的普通行跳过计数;同类型已有另一把钥不覆盖(计冲突,导入只增不改);`@cert-authority` / `@revoked` 进受信 CA 表,被吊销的普通行不导,认不出的标记整行跳过。导出:OpenSSH 格式,可选散列主机名(只作用于主机密钥行),只有指纹、没有公钥原文的老记录单独计数。吊销只能经导入或粘贴整行 `@revoked` 来。
- 设置 → 安全审计:已信任主机下「OpenSSH known_hosts」导入 / 导出与「导出时散列主机名」(不持久化),「受信主机 CA」表(受信 CA / 已吊销标签,可删)与添加表单(贴 CA 的 `.pub` 行或整行 `@cert-authority` / `@revoked`),都当场生效、不随「保存」走。
- 接口:`IHostKeyService.TrustHostKeyAsync` 加 `publicKey`,新增 `GetMarkersAsync` / `AddMarkerAsync` / `RemoveMarkerAsync`;`IHostKeyPrompt.DecideAsync` 加可选 `certificateAuthority`;`VelaHostKeyPolicy` 加可选 `TimeProvider`。
- 验证:`HostCaPolicyTests` 20 条用真 ssh-keygen 签的主机证书(夹具从 Ssh.Tests 链接)、`KnownHostMarkersTests`、`KnownHostsInteropTests`;去掉「没担保即拒」、吊销、证书排前,6 条红。

**ssh_config 互通**:§58 的导入不映射算法清单、压缩、agent、转发与证书,也没有反向导出;§63 的证书认证已落地,导入与外部启动却只认密码 / 私钥。
- 导入:`KexAlgorithms` / `HostKeyAlgorithms` / `Ciphers` / `MACs` 原样进 §134 的自定义清单,本版没实现的名字去掉并提示,拼错或去掉后一个不剩的整条不导(`SshAlgorithmPreferences.SanitizeImported`);`Compression` 照开;`ForwardAgent yes` 照开,指向别的 agent 套接字的不导;写了 `CertificateFile`、或 IdentityFile 旁边本机存在 `-cert.pub` → 证书认证(私钥取同名那把,没写时按命名约定反推);只写 `IdentityAgent` 指向默认 agent 且没写 IdentityFile → Agent 认证,其它套接字不导。三种转发成为这条连接的隧道(停止状态 —— VelaShell 没有「随连接启动隧道」的选项),本地一头默认 127.0.0.1(`GatewayPorts yes` → 0.0.0.0)、`*` → 0.0.0.0,`ClearAllForwardings` 一条不导,端口 0 单独说明不导。映射不了的进 `ImportedSession.Notes`,预览行多「导入说明」,凭据状态多「使用证书」「使用 SSH Agent」两态;`SessionImportWriter` 认 AuthMethod / CertificatePath / Ssh / Tunnels,隧道经 `IAppDataStore` 写 `tunnels` 集合。
- 转发补上 Unix 套接字与远程动态(§244 的新类型与字段合入之后补的):含 `/` 的一头落到 `TunnelConfig.LocalSocketPath` / `RemoteSocketPath` —— 本地转发两头各自可以是套接字,远程转发两头都是才导,只有一头的照隧道服务的口径跳过;本机路径展开开头的 `~`,要完整路径且不超过 `sun_path` 上限(107 字节,macOS 103),服务器路径要以 `/` 开头,不合格的跳过并说明。`RemoteForward` 不给目标映射成 `RemoteDynamicForward`,放行名单取这台主机的 `PermitRemoteOpen`(用 `RemoteOpenRules` 校验,一台主机的远程动态转发共用一份)。〔决策〕没写 `PermitRemoteOpen` 的远程动态转发**不导入**:OpenSSH 与库这时按 `any` 处理,而隧道的放行名单没有默认值,导入时替用户填 `any` 正是那条规则要防的「替用户当成全放」;明写了的(含 `any`)照收,`none` 与写错的同样跳过,预览里说清出路(补上 `PermitRemoteOpen` 再导,或在隧道面板里建)。
- 导出:导出对话框第三种格式「OpenSSH config」(`SessionFileFormat.SshConfig`,只导出,`SshConfigExporter`),只导 SSH / SFTP,一条一个 Host 块:HostName / Port / User / IdentityFile / CertificateFile / ProxyJump(按别名)/ Compression / ForwardAgent / ForwardX11 / 四个算法清单 / 转发;别名去特殊字符、重名加序号,主目录路径写 `~/`,「放开老算法」换算成 `+` 或完整清单,引用共享凭据的取凭据材料;不写密码、无 BOM、`.conf`,FTP / 插件跳过。套接字那头写路径;远程动态写成不给目标的 `RemoteForward` 并**一定连同 `PermitRemoteOpen`**(漏写即全放);一台主机只能有一份名单,名单不同的远程动态、名单为空的远程动态、只有一头是套接字的远程转发写成 `# … (VelaShell: 原因)` 注释,而不是取并集。
- 外部启动:认 PuTTY 的 `-cert`(PuTTY 手册 3.11.3.19),与 `-i` 配对,没给 `-i` 时按 `<私钥>-cert.pub` 反推、文件在才认;`ExternalLaunchRequest.CertificatePath` 随单实例转发,临时配置按证书认证,确认框与「复用已保存配置」都算作带了凭据。Xshell 的 `.xsh` / 命令行与 `ssh://` 没有公开的证书字段,不猜;宿主也没有 PuTTY 会话导入器。
- 验证:`SshConfigInteropTests` 15 条(导出后用库的解析器读回核对;导出再经会话导入读回逐字段一致、注释行不被读回、不会写出 `PermitRemoteOpen any`;套接字的各种组合与远程动态的名单各态)、`XshellLaunchParserTests` +2、`SessionExportViewModelTests` +1;变异(没写名单当 any、导出不查名单一致、套接字写成端口、去掉「只有一头套接字」判断……)各有用例变红。

**文档**:仓库内 `manual.md` / `manual.en.md` 4.3、4.4、15.8、16.1 与外部启动一节;velashell-docs `host/会话导入`(§3.4 映射表、§四 凭据状态、§五、§七 导出)、`host/交互与界面规格`(§14 安全审计、指纹确认框、§15 主机信任、导出对话框)、`host/settings-audit`、`host/Xshell兼容登录`(zh / en)。

## 243. 2026-10-24 内置 X 服务端收尾:拖放的远端临时目录清理、X 程序标出信任级别、宽弧几何与低位深 RENDER 整数合成

**拖放的远端临时目录**:往经 SSH 转发来的 X 程序里拖本机文件,文件先经那个会话的 SFTP 传到远端 `/tmp/velashell-drop-<32 位十六进制>/`(0700,§187),没有任何地方删,长期使用会在远端 `/tmp` 堆积。改动在 `Infrastructure/XServer/XServerDropUploader.cs`:
- 〔决策〕删的时点:拖放一结束就删太早 —— 程序可能稍后才读(先弹框问怎么打开)、编辑器会往原处存、拖进终端的是一条路径,用户随后才拿它敲命令。经 SSH 转发来的 X 程序走那个会话的 X11 通道,会话一断它们也跟着断,所以**拖放那一刻连着这台主机(同一个 `user@host:port`)的会话全都断了**,那批文件就没有程序会用了。会话断了远端也够不着(`SessionDisconnected` 在客户端释放之后才触发),于是在下一次够得着时删:又连上同一个连接名时(订阅 `ISshConnectionService.SessionConnected`,只在内存里确有要删的才开 SFTP),与下一次往那台主机拖放时(后台做,不耽误这一次)。「那一刻连着」按 `SshSession.ConnectedAt ≤ 批次时刻` 判;两个标签连同一台主机时,只要拖放前就连着的那个还在就留着。没在断开前抢着删:要在断开路径上插钩子并等远端往返,会拖慢关标签 / 退出,网络断开时也做不到。代价是只拖过一次、之后再也不连那台主机时,目录要等系统清 `/tmp`。
- 当场删:传输失败、取消、一个文件都没传上 —— 程序没拿到路径,在后台删,原异常照报。
- 本进程不知道的遗留(崩溃、上一次运行、本功能之前就堆着的、别的电脑上的 VelaShell):往那台主机拖放时一并清,只删 `/tmp` 下名字正好是前缀加 32 位小写十六进制的**真目录**(不是符号链接)、属主与这一次新建的目录相同、按**远端自己的时钟**(取新目录的 mtime)一天以前就没再动过的;一天与拖出方向的本机临时目录同一口径(`SftpXServerDragDownloader.Retention`)。
- 删除一律经 `ISftpService.DeleteAsync`(先试单引号包好路径的 `rm -rf --`,不行退回 SFTP 逐个删),路径先过 `IsDropDirectory` 的严格比对(前缀 + 正好 32 位小写十六进制,没有结尾斜杠、下一级或 `..`),变量空了也变不成 `/tmp/`。删不掉只记 `Trace`,本进程记着的那批留到下一次机会,三次都不行就不再记着、交给一天的口径;单次删除至多等 30 秒。
- 用例:去掉后台清扫 5 条红,放开路径闸 2 条红(大写十六进制与 `velashell-drop-notours` 被删)。

**X 程序标出信任级别**:库里早有 `XClientInfo.Trust`(§187),浮层列了来源会话、窗口数、内存与「独占中」,唯独没列受信 / 非受信 —— 用户判断「该断开谁」时最关心的正是它看不看得到别的程序的窗口与键盘。宿主的 `XServerClient` 加必填的 `Trusted`(生产方必须表态),`BuiltInLocalXServer` 按 `XClientInfo.Trust` 填;程序行第二行开头加徽标(第一行已有名字、「独占中」与来源,340 宽放不下第四样):受信任是描边的中性徽标(同隧道行的「自动」),非受信任换 `VelaInfo` 描边与字色,悬停说明两级各意味着什么。zh-Hans 用「受信任 / 非受信任」(与连接编辑器的「受信任(-Y)」一致;单独两个字的「受信」在徽标上容易读成「收信」);没把受信标成警告色 —— 本机程序全是受信,满屏警告没有意义。

**宽弧几何**(§185 核对时看到的):半径小于半个线宽的圆上的一段宽弧,内边界停在圆心、没有穿到对侧。协议对圆要的是理想边界(沿法线与路径相距 lw/2 的点),r < lw/2 时内边界上的点在圆心另一边、离圆心 lw/2 − r;原先内边界的半轴取「减半个线宽、不小于 0」,对侧那一截没画(整圆不受影响)。`Drawing/Rasterizer.cs`:宽高相等、0 < r < lw/2 的弧(`ArcStroke.Crosses`)按法线段扫出来的形状画 —— 圆心两边两个扇形(外侧半径 r + lw/2、角度就是这一截;对侧半径 lw/2 − r、转过半圈),各自是简单多边形,放进同一张活动边表求并集。外边界与两个端面围成的轮廓是自交的(端面在圆心交叉),跨度超过半圈时两个扇形还会重叠,当一个多边形按奇偶或环绕数填会在重叠处挖出洞;协议没写填充规则,并集与「一支宽 lw 的笔沿路径走」的直观、与宽线的定义一致。外侧扇形与原先的环带边逐条相同,原来画的像素一个不少;Projecting 端帽与接头的内角跟着落到对侧。宽高不等的椭圆照旧(协议明说由实现决定,圆与几乎是圆的椭圆之间因此不连续,规范允许)。400 组随机的圆 / 线宽 / 角度与两个扇形的并集逐像素对过。§178 记的「外接框宽或高为 0 的宽弧只画出一半线宽」核对时已是 §185 修过的(ed457425,带像素用例),只是 `feature-plan.md` 那一行漏删。

**低位深 RENDER 整数合成**(§178 留下的性能项):原先只有 8888 / a8 / a1 目标走整数,r5g6b5、x1r5g5b5、a4 逐像素浮点 Decode、合成、Encode。现在这三种目标在合成时把这一行先展开成 x8r8g8b8 / a8(各通道 v × 255 / (2ⁿ − 1) 四舍五入,查表),照整数路径合成,再按 v × (2ⁿ − 1) / 255 四舍五入收回(`WidenRow` / `NarrowRow`)。比全程浮点多两次取整,结果差不过目标格式的一级:Porter-Duff、Disjoint、Conjoint 对目标颜色只乘一个不大于 1 的因子,展开时不到半级的误差不会变大;目标 alpha 要么恒为 1(两种 rgb 格式没有 alpha),要么 4 位展开正好是整数(× 17)。〔决策〕**PDF 混合模式在 r5g6b5 / x1r5g5b5 上仍走浮点**:颜色减淡 / 加深与 HSL 几种对目标颜色的斜率没有上界,那半级会被放大成目标格式的好几级;a4 只算 alpha(混合模式的 alpha 是 αs + αd − αs·αd),照样走整数。用例:三种目标 × 全部 53 种运算 × 4 种源 × 3 种遮罩与逐像素浮点逐通道比,最大差 1 级(浮点路径是 0 差,说明确实走了整数),rgb 两种上的混合模式要求逐位相同。基准 `scripts/xserver/bench/bench.cs` 加了三个场景(Release;机器同时在跑别的构建,抖动大,各跑两遍):ARGB → r5g6b5 Over 每秒 2370 / 3117 → 17199 / 26151,带 a8 遮罩 1102 / 2645 → 11292 / 19055,a8 → a4 Add 2283 / 2795 → 21285 / 30235,CPU 时间约 2300 ms → 400–600 ms;改之后最慢的一遍也比改之前最快的一遍快 4.3–7.6 倍。

**验证**:`VelaShell.XServer.Tests` 555 条、Infrastructure 850 条、宿主浮层与视图模型用例(`ssh -X` 来的那行标非受信任、信息色)全过;各项临时撤掉修正,对应用例变红。

**文档**:velashell-docs `host/交互与界面规格` §4A.2 / §4A.3、`xserver/design/architecture` §6 与历史记录(zh / en)。

## 244. 2026-10-24 隧道:自动建立失败带「重试」、速率 / 连接列表 / 限速、远程动态 SOCKS、Unix 域套接字

**自动建立失败带「重试」**(二、B):「程序启动时自动建立」(§186)失败只在右下角报一句,用户得打开面板、切到那台服务器、再点那条隧道的启动键 —— 而他看到提示时想做的只有「再来一次」。提示组件早就支持动作按钮(断线的「立即重连」),只是 `TunnelPanelViewModel` 的出口 `errorReporter` 只收一句话 → 改为 `Action<string, Action?>`(第二个参数非空时提示带「重试」),另加 `infoReporter`。连不上服务器的那条重试整台(重连,只建勾了自启、还没起来的),一条建不起来(端口被占)只重试那一条,再失败照样带重试。重试是人点的:指纹照常弹框(`automaticReconnect: false`),之前因「认证要人处理」而暂停自动恢复的服务器也照样再认证;那条在此期间已被用户启动、改过或删掉,重试什么也不做,不会建出第二条;建成了右下角报一声「隧道「x」(srv)已建立」(面板多半没开,不报用户不知道成没成)。自动重连败在主机指纹 / Tailscale 认证上的提示**不带**重试 —— 那里要的是用户去面板手动启动、在弹窗里核对。

**速率、连接列表、限速**(一、「SSH 库已有、宿主还没接的能力」里 §161 落地的转发吞吐 / 连接快照 / 限速;先读库的公开 API,宿主照着接,没改库):`IPortForwardHandle` 加两个方向的速率与连接快照(`Core/Models/TunnelConnectionInfo`:序号、来源、目标、开始时刻、两个方向的字节),`LibraryPortForwardHandle` 直接取库的 `Throughput`(最近三个整秒的平均)与 `Connections`,来源写成 `host:port`(IPv6 带方括号;Unix 套接字上连进来的没有名字,界面写「来源未知」)。`TunnelService.RefreshStatistics` 与计数一起同步,停止时清零 / 清空(累计字节照旧留着)。面板开着才起 1 秒的流量时钟(`TunnelPanelViewModel.IsOpen` 由宿主随面板显隐设置),存活检测与自动恢复仍是 5 秒一拍。行上加速率行 `↑ … ↓ …`(设了限速追加「· 限速 …」,停着时只剩限速半句)与默认收起的「当前连接 N 条」(来源 → 目标、已开多久、两个方向的字节,至多 20 条,收起时不每秒重算)。限速按隧道存 `TunnelConfig.MaxBytesPerSecond`(`long?`,null 不限),表单按 KB/s 填(每个方向,0 为不限),编辑时不整除的向上取整,不会被悄悄放宽成 0 = 不限;新建、启动、编辑重建、自动重连、启动时自动建立都经 `CreateTunnelAsync` 带上(当场接线)。库在建转发器时定额度,改了要重启隧道 —— 而运行中的隧道本来就不能编辑。同一条里的 TLS 拨号器(可选)这次没做:那是全局代理设置的一整块,还要先定「TLS 包在代理之外还是之内」与证书校验口径,留在 `feature-plan.md`。

**远程动态 SOCKS**:新类型 `TunnelType.RemoteDynamicForward`(**追加在枚举末尾**,本机库存数值,前三个值不动),服务器上的监听地址 / 端口沿用远程转发的 `RemoteHost` / `RemotePort`,`LocalPort` 存 0;`ITunnelService.CreateRemoteDynamicForwardAsync` → `PortForwardKind.RemoteDynamic` → 库的 `RemotePortForwarder.StartDynamicAsync`。放行名单 `TunnelConfig.PermitRemoteOpen` 的写法与校验在 `Core/Tunnels/RemoteOpenRules`,同 ssh_config 的 `PermitRemoteOpen` / 库的 `RemoteOpenPolicy`(`主机:端口`,主机可 `*` `?` 通配,端口可 `*`,IPv6 方括号,空白或逗号隔开,单独一条 `any` 全放、和别的写在一起算写错)。〔决策〕**没有默认值、不许留空**:这等于把本机变成远端的 SOCKS 代理,放哪些出去要用户明说;表单上空着「创建」点不了、写错一条红字点名,服务层对空名单 / 写错的规则同样拒绝(手改、同步来的配置),不替用户当成全放或全拒。名单外的请求库回 SOCKS `0x02`、报 `TargetNotPermitted`,宿主换成本地化提示记到隧道行。表单:类型下拉第 4 项,本地端口栏收起、主机栏叫「服务器监听地址」,「只在服务器本机监听(127.0.0.1)」默认勾;端点那一行从五列 Grid 改成三块各自显隐(藏掉一块不留空列)。英 / 日的类型名缩成「Remote SOCKS」「リモート SOCKS」(136px 的下拉放不下)。

**Unix 域套接字**:按库实际支持的组合 —— **本地转发**两头各自可以是套接字(本机 TCP / 套接字 → 服务器 TCP / 套接字,四种都通),**远程转发**只有两头都是套接字(`StartUnixSocketAsync`),动态 / 远程动态没有套接字写法;库没公开 OpenSSH 的「服务器 TCP → 本机套接字」「服务器套接字 → 本机 TCP」两种,记入 `feature-plan.md`。〔决策〕做成 `TunnelConfig.LocalSocketPath` / `RemoteSocketPath` 两个可选字段而不是新类型:库的组合是「每一头各自 TCP 或套接字」,做成类型要 4 + 2 个枚举值;字段又与 ssh_config 的 `LocalForward /path host:port` 一一对应(§242 的导入直接填字段)。远程转发只给一头的配置不建(`TunnelSvc_RemoteSocketNeedsBothEnds`),不悄悄退回端口。本机监听路径上已经有文件就不建、**不替用户删**(`TunnelSocketPathInUseException`,「多半是上次没收拾干净,也可能是别的程序的套接字」),与库默认的 `AllowSocketReplacement = false` 一致 —— 崩溃后残留的要用户自己删,换来不会误删别的程序的套接字;停掉隧道时库删掉它建的文件。表单「改用 Unix 域套接字」勾上出现两栏路径,填了路径的那头置灰端口与主机栏;本机路径要是完整路径、UTF-8 不超过 107 字节(macOS 103),服务器路径以 `/` 开头。Windows 10 1803 起支持 AF_UNIX(`sun_path` 108 字节,套接字文件沿用所在目录的 ACL;非 Windows 上库设 0600),本机这头的监听、被连、作为远程转发的回连目标都在 Windows 11 上实跑通过;macOS 没有机器,只按 104 字节上限做了校验。

**兼容**:`TunnelConfig` 是 init-only、整对象序列化的(本机库、云同步、会话导出都是),没有逐字段手抄的克隆;新字段默认 null、`WhenWritingNull` 不写,旧配置读出来与原来一样,不带新字段的配置写出去与旧版同一个形状(`TunnelConfigPersistenceTests` 钉住:本机库、导出文件各往返一遍,另读一份旧 JSON 原样写回逐字比对)。旧版本经云同步读到「远程动态」会显示成本地转发、启动报 Unsupported tunnel type;读到套接字配置会忽略两个路径、按留在配置里的端口起一条 TCP 转发。

**文案与本机侧的错误**(合入后补的):远程转发(与远程动态一样)的主机栏改说「服务器监听地址」、勾选框改说「只在服务器本机监听(127.0.0.1)」—— 原先的「目标主机(服务器视角)」「转发到服务器本机」说的其实是服务器上的监听端。`TunnelService.DescribeForwardError(ex, type)` 按类型分开说:远程 / 远程动态转发的本机目标被拒 / 不可达说「本机这头的目标…是从本机去连的」(新键 `TunnelSvc_LocalTargetRefused` / `TunnelSvc_LocalTargetUnreachable`;远程动态在本机解析域名,不可达另认 `NetworkUnreachable` / `HostNotFound`),本地 / 动态仍是「从服务器视角」。本地 / 动态转发起监听失败(库报 `ForwardBindFailed`)时 `TunnelService` 抛 `TunnelBindFailedException`,按手里的配置说清在哪儿(`地址:端口` 或套接字路径)与套接字错误码(`AccessDenied`、`AddressNotAvailable`…),不再显示 SSH 库写给开发者的中文原话 —— 只改宿主就做得到,不必从库的句子里抠路径;TCP 端口被占仍交给面板的「本地端口 N 已被占用」,面板先认这类异常(套接字路径上的 AddressAlreadyInUse 不是端口的事)。`SshInterop.Localize` 对 `ForwardBindFailed` 照旧不翻(别的调用方手里没有配置可依)。

**验证**:Core / Infrastructure / Presentation / 宿主 UI 各加用例(速率与连接快照的同步与清零、限速带进请求、远程动态的请求与空 / 错名单、名单策略映射、套接字两头的请求与只给一头的拒绝、已有套接字文件不删、表单的显隐 / 校验 / 往返、行上的速率 / 连接列表 / 放行 / 路径显示、错误按类型分头说、配置往返与旧配置兼容),headless 截图核对过布局。新增 DockerIntegration `TunnelForwardingIntegrationTests` 4 条在 ssh-shells 上实跑(不是早退):远程转发限速 64 KB/s 收 256 KB 三秒上下;远程动态名单内的目标经本机连上回 `05 00`、名单外回 `05 02` 并在隧道行记本地化提示;「远程转发(服务器套接字 → 本机套接字)+ 本地转发(本机端口 → 服务器上那个套接字)」首尾相接走通;本地转发在本机套接字文件上监听、读到 sshd 的版本行、停掉后文件被删。逐项临时去掉实现,对应用例都变红。⚠️ 合入后限速用例在 dev 上稳定变红:SSH 库的限速按**一次读到的整块**等,机器忙时数据攒成约 200 KB 一块到达,先等三秒多再整块写出、整块计数 —— 平均速率对、限速照样生效,但中途读数是跳的,单次突发会超过「一秒额度」。用例改为不在固定时刻断言字节数(等连接进快照、收完后核对快照与速率;临时去掉限速仍红),库这边的突发粒度记入 `feature-plan.md`。另:同一次刷新里 `ActiveConnections` 与 `Connections` 是先后两次读的,刚开始搬的那一刻可能对不上一拍(界面上最多错一秒)。

**文档**:velashell-docs `host/交互与界面规格` §10、`host/隧道功能规划`、`host/architecture`「端口转发隧道」、`host/架构设计` 的隧道面板一行(zh / en);仓库内 `manual.md` / `manual.en.md` 第 11 节与 23.1 在这批收尾时改准。

## 245. 2026-10-24 终端:OSC 133 命令块的「复制这条命令」与「重跑」;选区上的前景守卫改成保色相

**复制这条命令 / 重跑**(二、A):§10-E 落地 OSC 133 时 B(提示符结束)只消费不落行:它标的是列,列在改列宽重排后整体挪位,跳转 / 选中输出 / 折叠都只按行定界、用不到。「复制这条命令」「重跑」要把命令从 B 切到 C,这份代价这次一起付。
- 记列:`TerminalRow` 新增 `InputStart`(B 的列)/ `InputEnd`(C 的列),-1 为无;光标停在行尾等着折行时记成 `Columns`(下一个字符其实在下一行行首)。C 不能只靠行标记 `Output`:没有输出的命令(`cd`)跑完,下一条提示符画在 C 那一行,A 把行标记改写成 `Prompt`,「这条命令回车执行过」就丢了 —— 区分执行过的与敲一半按 Ctrl+C 取消的(bash 会回显 `^C`)正要靠它。两个字段随整行覆写(ED 2 / EL 2 / 滚进来的新行)清掉;**ESC[K / ESC[J 把一行擦空时 C 的列留着**(zsh 跑完 `cd` 会在 C 那一行补 PROMPT_SP 再 ESC[J 擦空、接着在同一行画提示符)。按字段布局估算每个行对象多 8 字节(约 64 → 72,未实测)。`ReflowResize` 把 B / C 像光标那样换算成逻辑行内偏移,重新换行后落回新行的新列(换行判定之后再落,被挤到下一行的字符把它一起带过去);B 落在内容之后的空白上时把收集延到那一列,否则重排后 B 与光标错开一列。
- 切命令:`TerminalEmulator.IsCommandRunning` 在 C 之后为真,D、下一个 A(有的集成不发 D)或 RIS 复位。`CommandBlocks.CommandInputOf` 取块内第一个 B 到其后第一个 C(最远看到下一条提示符那一行,那一行上自己 B 之后的 C 归下一条命令):被自动换行折开的接回一行、硬换行保留为 `\n`;多行命令的续行上若有自己的 B(PS2 末尾也发了 B)就从那里切、剪掉续行提示符,没有就整行照收并标 `HasUnmarkedContinuation`;每行去行尾空白、**保留行首空白**(bash `HISTCONTROL=ignorespace`);C0 / DEL / C1 一律剔除(C1 是零宽字符,会被当组合标记挂在前一格上,Latin-1 会话编出来就是 8 位 CSI)。没有 B(老集成脚本只发 A/C/D)、B 之后没有 C(还在敲、取消了)、B 与 C 之间是空的,都返回 null。`CommandBlocks.IsIdleAtPrompt`:不在备用屏、`IsCommandRunning` 为假、光标正停在当前块**第一个** B 上(续行上的 B 不算)、光标下那一格从没写过(敲了 `ls` 再按 Home 光标也在 B 上;只看这一格而不是整行剩余,zsh 的 RPROMPT 画在同一行右端);判断不了的(没有 B、提示符恰好占满一行)一律当不空闲。
- 入口与发送:终端侧栏右键落在某条命令块的任一行上时,部件开关上方多「复制这条命令」「重跑这条命令」与一条分隔线;块切不出命令时两项都在、灰着(看得出是这一块不行 —— 还在敲 / 取消了 / 老脚本,而不是功能不存在)。〔决策〕不放正文区右键(那是粘贴,改它影响面太大),也不做侧栏按钮(标记列只有几像素宽,左键已是「选中输出」)。重跑在弹菜单时判一次空闲、点下去时再判一次(菜单开着的几秒里远端可能变了状态)。多行命令照快捷命令的口径:整段走括号粘贴后回车,对端没开括号粘贴时先弹「重跑多行命令」确认框(前五行预览,开关沿用「粘贴时确认多行内容」);续行没带 B 的多行命令不给重跑(`cat <<EOF` / `> important.txt` / `> EOF` 原样送回去,第二行就把文件截空),复制照给。发送交给 `TerminalTabViewModel.TryRerunCommand`(控件发 `CommandRerunRequested`):与快捷命令同一条路径,发的那一刻关掉同步输入转发(只发给这一个标签)、标成程序注入(补全弹层不弹),末尾 `WriteKeyInput(Enter)`(LNM 下是 CR LF),补全的行跟踪照常记进历史。重跑在**当前**提示符上执行,中间 `cd` 过、`ssh` 进了别的机器,就在现在这个目录 / 这台机器上跑,没加额外确认。
- 片段:bash / zsh 都给 PS2 末尾接一个 B。zsh 片段的 A 从 `precmd` 挪进 `PS1` 开头 —— zsh 画提示符前先补 PROMPT_SP、再回行首 `ESC[J`,`precmd` 正好跑在两者之间,打出的 A 落在随即被擦空的那一行上、按「整行擦空即作废」被丢掉:**zsh 下命令块此前一个都立不起来**(§10-E 记的「zsh 未验」)。同时去掉 ST 后多出的那个反斜杠(`$'…\\\\…'` 在 zsh 提示符展开里不再被吃,每条提示符后都打出一个 `\`,zle 的光标列也差一格)。⚠️ 装过旧片段的用户要重新复制一次:旧 zsh 片段下命令块立不起来,旧 bash 片段下多行命令只能复制不能重跑。starship / p10k 这类每次重写 PS1 的框架会冲掉 A 与 B,但此前 `precmd` 打的 A 在默认 zsh 下本来就活不下来,B 也一直在 PS1 里,没有比原来更差。
- VT 引擎:`TerminalScreen` 的 ED 0 从第 0 列起、ED 2 时把上一行(首行则是回滚区末行)的软换行标志清掉。zsh 下上一条输出没以换行收尾时,PROMPT_SP 的一整行空格把光标「折」到下一行、再 ESC[J 擦掉、在那里画提示符 —— 标志留在输出那一行上,一改列宽提示符就被并进输出的逻辑行,提示符标记(逻辑行只留第一段的)随之丢失。xterm.js 在同样两处断开续行;EL 不动(readline 改命令行用的是它)。被这样擦过、再在那一行写的内容,复制与重排都不再当成上一行的续段。
- 验证:Terminal.Tests 新增 52 条、宿主 7 条(其中 2 条用本机真 bash ≥ 4.4 展开 PS2,没有 Inconclusive);ShellIntegration.Tests 在 ssh-shells 容器里对真 bash 5.2 与真 zsh 5.9 端到端:装片段,敲 `echo`、`cd`(无输出)、`printf no-newline`、三行 `for…done`,再像拖窗口那样改到 23 / 61 / 120 列(本地重排 + 远端 SIGWINCH)断言切出来的命令一字不差;另一条验敲了半截、`sleep 30` 在跑时不空闲,Ctrl+C 后回到空闲。**端到端先红了 3 条,正是上面 zsh 的三处**,修完全绿。逐处拿掉实现(不在 reflow 里搬 B/C 6 条红、去掉 ED 断开续行 2 条、擦空行时不留 C 的列 1 条……)各有用例变红;「C 之后没 D」与备用屏两道拦截起初没有用例单独变红(别的判定顺带拦住了),各补了一条只靠它拦的。没认 OSC 633 的 `E`(VS Code 显式上报命令行),没验 fish 4.x 自带的 OSC 133,记入 `feature-plan.md`。

**选区前景守卫保色相**(一、💡,§59 当时两害相权的那一半):§59 把选区底抬亮(暗底 L* 差 20 / 亮底 16)之后,与选区底亮度差不到 `MinForegroundDelta`(0.18)的前景一律换成同一个兜底色(默认前景,它也撞就纯白 / 纯黑),选中一段 `ls --color` 配色全被抹成一个颜色。对着 16 套内置方案量:默认前景 + ANSI 16 色(272 个前景)里撞上的 41 个(与 §59 记的一致),有 29 个带色相的被换成灰 / 米黄;Solarized Dark / Light 的兜底色对选区底本身只有 2.42:1 / 2.69:1。触发判据不变(渲染热路径上逐格比的仍是 0.18 的亮度差),撞上之后改由 `SelectionContrast.ReadableOnSelection` 往白或黑里混(三个通道同比例向同一端靠,色相角不变,与 `Fill` 推选区底同一手法),往前景本来所在的一侧推、推到头也够不着才换边,二分到与选区底的 WCAG 对比度够 `MinForegroundContrast`(3.0,与 `UiThemeCatalogTests` 对 ANSI 压在各自终端底上的要求同一档 —— 选中之后的字不该比没选中时更难认,也不必更显眼)就停。只推到 0.18 那道亮度差不够:量过,那样推完只有 1.55–2.3:1。这一步要走 `Math.Pow` 与二分,渲染层按前景色缓存(选区色一变整表作废,超过 256 种清空重来);`ReadableForeground` 与 `_selectionFallbackForeground` 删掉。结果:41 个全部 ≥ 3.00:1、色相保住,从「每套方案 1 种兜底色」变成 39 种(Solarized Dark 蓝 #268BD2 → 旧 #839496 灰 / 新 #5FAADE 蓝)。代价:多数方案上对比度从默认前景的 3.3–10.4:1 降到 3:1;近灰的深色与近白色推完接近中性灰(往白 / 黑里混的必然结果)。新增尺子用例(全部内置方案的对比度与色相)与 Skia 像素用例(Solarized Dark 下选中蓝色 █ 仍是蓝的),渲染层改回旧兜底色即红;原有 `UiThemeCatalogTests` / `ThemeTokenContrastTests` 照过。

**文档**:velashell-docs `host/交互与界面规格` 新增 §5.2.2「命令块:复制这条命令 / 重跑」(zh / en);选区前景守卫没有文档段落。仓库内 `manual.md` / `manual.en.md` 7.6、22.3 与 23.1 在这批收尾时改准。

## 246. 2026-10-24 SSH 库剩余中文诊断补上结构化出处,宿主按种类出五语文案;主机证书问题只留一套文案

**来由**:`feature-plan.md` 🟡 P2「SSH 库剩余中文诊断文本的界面本地化」。§117 审查之后宿主已按 `Reason` 翻了大部分库异常,但 `src/VelaShell.Ssh/AGENTS.md` 4.8 记着四类仍透传库的中文原文:认证的逐条尝试记录、通道开不成 / 请求被拒时的建议、带路径 / 指纹的私钥与证书消息、`KnownHostLookup.CertificateProblem`。英 / 日 / 韩界面上认证失败时,主句是界面语言,下面几行却是中文;私钥格式不对、证书与私钥不是一对时,整句都是中文。原因是这些信息只存在于句子里,宿主要翻只能解析句子,而 4.5 不许。

**库这一侧**(只**加**成员,现有成员的签名与语义不变,库自己的中文 `ToString` / `Message` / `Detail` 一字不变 —— 库是独立的 MIT 组件,不依赖宿主的 resx):
- 认证:`SshAuthAttempt.Details` 是 `SshAuthDetail` 的列表,每条 `SshAuthDetailKind`(17 种)加参数(方法名单、次数、服务端原话、凭据抛的异常、算法、Key ID、时刻、主体、登录用户);`BridgedCredentialLabel` 记口令凭据改走 keyboard-interactive 时原来那条凭据的标签;`Detail` 改由 `Details` 拼成,拼法与原来相同(第一段照写,后面的括起来,证书线索合成一段)。只剩被禁用的 SHA-1 `ssh-rsa` 时,原先拿 `InvalidOperationException` 的消息当说明,现在直接记 `NoUsableSignatureAlgorithm`,原文不变。
- 通道:`SshChannelException.FailureKind`(`SshChannelFailureKind`,14 种 —— 转发被禁、通道被禁、目标连不上、未知通道类型、资源不足、其它原因码、未确认就关、通道数上限、窗口总预算、exec / pty / shell / 子系统被拒、max packet 为 0)加 `ChannelType` / `SubsystemName` / `Limit`,10 处抛出点逐一归类。
- 私钥与证书:`SshPrivateKeyException` / `SshCertificateException` 共用 `SshKeyProblemKind`(20 种),加 `Origin`(文件路径、`Parse` 的 origin 或密钥库里的钥名 —— 解析的内层只拿得到拼好的「(来源)」,所以由入口在异常离开库之前补上,`internal set`)与 `AlgorithmName`;证书另有 `KeyId` 与配错时两边的公钥(`CertificateKey` / `SignerKey`,界面据此显示指纹)。82 处抛出点逐一归类,约 40 种「文件坏了」的细分合并成 `Malformed`,细节留在内层消息与日志里。
- 主机证书:`KnownHostLookup.CertificateProblemDetail` 是 `HostCertificateProblem`,`HostCertificateProblemKind` 11 种(顺序即验证顺序,报的是第一条不过的)加参数(Key ID、时刻、主体、主机、关键选项、算法、CA 位数);`CertificateProblem` 字符串照旧,内部的 `CheckHostCertificate` 留作薄包装。
- 四个枚举的零值都是 `Unknown` = 没归类,库自己不产生。

**宿主这一侧**:`SshInterop` 改成 partial,新文件 `SshInterop.Diagnostics.cs` 按种类拼界面语言 —— `Localize` 先走结构化那一层,没归类的(调用方自己构造的异常)再落到按原因码的 `LocalizeByReason`;认证失败的逐条记录改由 `SshInterop.DescribeAttempts` 拼(跳板那条路也一样);私钥 / 证书问题前面带上来源(`路径: 原因`);来自对端或文件的名字(Key ID、主体、算法名、通道类型、服务端原话)先过 `PeerText.Sanitize` —— 结构化参数存原值、显示前清洗,与 `SftpException.ServerMessage` 同一口径。界面文字的几处变化:私钥口令两类原文案不变、前面多了来源路径;通道请求被拒原来是笼统的一句 `SshErr_ChannelRequestRejected`(ForceCommand、PermitTTY、Subsystem 一起列),现在 pty / exec / shell / 子系统各说各的,笼统那句只留给没归类的异常;通道数上限、窗口预算原来是转发 / agent / X11 那句笼统的 `SshErr_LimitExceeded`,现在说具体上限并建议关掉几个会话或隧道(宿主没有暴露窗口策略,不像库原文那样叫用户「调小窗口策略」);证书时刻在界面上用 UTC 的 `u` 格式(与库原文一致,不随本机时区变)。79 个新键,五份 resx 齐全。`src/VelaShell.Ssh/AGENTS.md` 4.8 划掉这四类,补上盘点出的其余透传项。

**守卫**:宿主逐个枚举值核对映射、五份 resx 与占位符是否一致,英文界面下每一种文案都不许出现汉字;`StructuredFailureCoverageTests` 扫库源码,要求每一处 `new SshPrivateKeyException(` / `new SshCertificateException(` / `new SshChannelException(` 都带种类,新抛出点没归类就红并报出文件与行号;`MalformedKeyInputTests` 要求截断与随机的密钥输入一律不是 `Unknown`。

**验证**:先只往现有用例里加「原文逐字相等」的断言(只用旧 API),`git stash push -u -- src/VelaShell.Ssh` 撤掉库的改动、在旧实现上跑 `AuthenticationTests` + `HostCertificateTests`(86 条全过),证明默认文本不变,之后再加结构化断言。`VelaShell.Ssh.Tests`(Debug,不含 Interop)全过;删两条宿主映射 4 条红,删一处库的归类扫描用例红,删 ko 的一条五语守卫红。`SshAuthenticator.cs` 从 1179 行涨到 1208 行(过了 4.4 的 800 行,不在 4.8 列的上帝类里;增加的是结构化初始化,拆分属于纯重构)。

**主机证书问题只留一套文案**(§242 合入之后):§242 的弹窗与裁决原先按证书的公开字段自己推断「哪里不合格」,与这里的 `LocalizeHostCertificateProblem` 成了两套文案 → `VelaHostKeyPolicy` 遇到受信 CA 签的证书不合格时,拒绝理由改用 `SshInterop.LocalizeHostCertificateProblem(authority.CertificateProblemDetail)`(库的约定是这个结论一定带着出处,万一没带退回库的原文);删掉按证书字段推断原因的 `DescribeCertificateProblem` 与它的 7 个 `HostCert_Problem*` 键。原先签名验不过、CA 位数不足、SHA-1 签名这三种只能笼统说一句,现在各说各的并带上 Key ID。库的 `HostCertificateProblem.ToString`(即 `CheckHostCertificate` / `KnownHostLookup.CertificateProblem` 的中文原文)里,来自对端证书的 Key ID、主体、关键选项与算法名从来没清洗过,这次改为先过 `PeerText.Sanitize` 再拼进句子 —— 对正常证书一字不变(原有的原文用例不用改就全过),只有带控制字符 / 双向文本控制符时换成 `?`、超长时截断(Key ID 与名字 256 字符、算法名 64 字符)。临时把拒绝理由改回库的原文,`HostCaPolicyTests` 红 4 条(这几条固定用英文界面核对 —— 中文界面的几句与库原文措辞相同,在 zh-CN 机器上原本只红 2 条)。

**没做的**:盘点出的其余透传项 —— 最大的是建连时主机密钥被拒的几种(解析不了、类型与协商的不符、RSA 太短、签名验不过,都是 `HostKeyRejected`,宿主却当成 `VelaHostKeyPolicy` 写的理由照用),以及代理握手、跳板、没带异常的转发运行期错误、X11 转发、agent、SFTP 扩展缺失与版本太低、TCP 拨号的 `Unknown` —— 记入 `feature-plan.md`,做法同本节:先在库里补种类与参数,再由宿主映射。

**文档**:velashell-docs `ssh/spec/04-authentication`(§3.4、§4.4、§4.5、§5.1)、`08-failures`(§一、§二、§5.3)、`05-connection`(§2.3 与请求被拒)、`03-key-exchange`(§5.5,含不合格原因的原文里对端给的名字已清洗)(zh / en)。

## 247. 2026-10-24 插件模板:velaplugin-protocol 与 velaplugin-workspace(velashell-plugin-templates)

**来由**:`feature-plan.md` 二、G 的 🟠 P1(上一轮建议第 3 条):Telnet / 串口 / Redis / S3 / DockerPanel 五个插件已把 `Protocols` 与 `Workspaces` 走通,把公共骨架沉淀成模板,第三方接新协议的成本会掉一个数量级 —— 生态能不能长起来的关键杠杆。在 velashell-plugin-templates 仓库完成(五个提交,VelaShellLabs/velashell-plugin-templates#11),本仓库没有代码改动。

**骨架与业务怎么分**(逐个读五个插件之后定的):进模板的是清单字段(contributes + 惰性激活 + `hostMode: "inProcess"` + 显式的 `minSdkVersion: "2.0.4"`,用到了 `PluginIcon`)、入口生命周期(只注册、换语言重注册、停用撤注册)、插件自带的 Loc、表单声明与解析放同一个文件、Protocol* 异常族的翻译、终端会话的读泵 / 写闸 / EOF / 可唤醒的 Dispose、工作台的状态发布与按端点留存、测试工程的形态(MSTest 4.5.1 + `VelaShell.PluginSdk.Testing` + InternalsVisibleTo + 激活时校验描述符);不进的是 Telnet 协商、串口枚举与带外命令、Redis 的发现、S3 的文件协议、Docker 的隧道 API 这类业务。清单里没有权限字段(权限系统按用户决策不做),唯一的门槛是 inProcess 与 minSdkVersion,模板里写清了。两个模板都没有 `--hostMode`(协议与工作台能力只在 inProcess 下可用);插件工程在根目录,`<名称>.Tests/` 与 `tools/EchoServer.cs`(file-based app,`dotnet run --file` 的 `--file` 不能省)用 `DefaultItemExcludes` 挡在插件编译与 `.vpx` 之外;根目录不放 .slnx(同时有 .csproj 与 .slnx 时 `dotnet build` 报 MSB1011)。要改的地方在代码里标了 ✏️。
- `velaplugin-protocol`(终端协议):注册 `ProtocolDescriptor`;`ProtocolSession` 沿用 Telnet / 串口的「读泵 + 单出口通道」,写侧串行化、掉线归一成 EOF、`DisposeAsync` 能唤醒挂着的读并带 2 秒兜底、连接超时与连不上翻成带端点的异常,另有回车改写(CR LF 粘贴算一个回车)与本地回显。测试 13 条。
- `velaplugin-workspace`(工作台连接类型):注册 `WorkspaceDescriptor`,默认带 SSH 隧道(只多一个字段加一位能力,但这是工作台能力最有价值的一面);`LineConnection` 是最该换成你的客户端库的一个类(第三方异常不许越过插件边界);`WorkspaceDocument` 的 `DescribeConnection()` 不输出口令与 IsSecret / IsHidden 字段,刻意不碰 Avalonia 类型以便单测完整驱动;面板用 code-behind(与 velaplugin-ui 一致)。SDK 没有保存 / 还原文档状态的契约,宿主重启后也不重开标签页,所以照 Redis 的做法按**端点**(走隧道时取真实目标)把草稿与最近发送写进 `context.Storage`,存储不可用时静默降级;不用 `SessionId` 当键 —— 宿主每次打开都 `Guid.NewGuid()`(核对过 `PluginWorkspaceLauncher`);`OpenAsync` 不写状态(「测试」按钮就是 Open → Dispose)。测试 14 条。
- 〔决策〕只出终端协议模板、不出文件协议模板:五个插件里只有 S3 实现 `IProtocolFileSystem`,沉淀不出公共骨架。换语言重注册时沿用同一个实现实例、不 Dispose 旧句柄(照 Redis):Telnet / 串口 / S3 的「先注册、再 Dispose 旧句柄」在宿主里没问题,在 Testing 替身里会把新注册一起撤掉。回显用「TCP 会话 + 外置回显服务」而不是进程内假回显,读泵、写闸、EOF、关闭唤醒读这些真正难写对的部分都是真代码。测试不碰面板、不引 Avalonia —— 要引就多一个必须与 `.Build` 锁定版本一致、`Set-Version` 又管不到的版本旋钮;代价是面板构造这类 bug 冒烟抓不到。

**顺手修的两处既有问题**(各自独立提交,方便单独挑拣):velaplugin-ui 模板的面板根本开不出来 —— `DemoPanel` 手写了无参 `InitializeComponent()`,而 Avalonia 源生成器生成的是 `InitializeComponent(bool loadXaml = true)`,只有后者给 `x:Name` 字段赋值;重载决议挑中手写的那个,`CountSessionsButton` 是 null,构造函数第二行就 NRE。冒烟只编译不实例化,所以一直没红,**已发布的 2.0.1 模板包带着这个 bug**。冒烟的容器读回一直被静默跳过 —— 脚本只认 `tools/net11.0/VelaShell.Plugin.Cli.dll`,而 2026-09-11 起 `.Build` 包里是 `VelaShell.PluginSdk.Packer.dll`(同样有 `info`),每次只打一条 notice;两个名字都认之后四个模板都真读回了,同一提交还修了 `"$template:…"` 被当成驱动器限定变量、插成空串的错误信息。

**版本**:`VelaTemplatesVersion` 保持 2.0.1(每次发版由 `Set-Version.ps1 <版本>` 落盘,这次是功能 PR 不是发版,新增模板按语义更像次版本,号由维护者排期时定);`VelaBuildPackageVersion` 保持 2.0.9(新模板只需 ≥ 2.0.4,2.0.4–2.0.9 之间 Protocols / Workspaces / PluginIcon 的契约没变,对着 SDK tag 2.0.9 比过)。只把 `Set-Version.ps1` 的落点清单从五处扩成七处,`-Check` 通过;加 `-DocsRoot` 时报出 velashell-docs 两份 dev-guide 的 `PackageReference` 仍是 2.0.0 —— 原有的漂移,按 docs 的约定这处版本号由脚本同步、不手改,已用 `Set-Version.ps1 -DocsRoot` 落进 velashell-docs#147。没造本地包,没碰 `nuget.config`。

**验证**:`dotnet build` 0 警告(VELA1004 核对四个模板的 sdkVersion 都是 2.0.9);`dotnet pack` 出的包里有两个新模板的全部文件、没有 bin / obj / TestResults;`Invoke-Smoke.ps1` 四个模板都 Packed、0 警告、容器读回 `format v1 / flags Masked`,协议 13/13、工作台 14/14,插件输出目录只有插件自己那一个 dll。另在 scratch 里做了三项:真宿主 `--dev-root` 加载两个生成工程,清单被接受、惰性等待与预热都对;控制台程序直接驱动安装目录里的 `PluginManager` + 协议注册表 + 终端连接器 + 工作台启动器(真 ALC 装载、真能力桥接),回显、连接、断开都对,死端口的 `PluginProtocolConnectionException` 原样到了宿主;Avalonia.Headless 实例化工作台面板 —— 这一步查出了上面 velaplugin-ui 那个 bug。

**范围外查到、没改的**:SDK 的 `RecordingProtocols` / `RecordingWorkspaces` 注册句柄 Dispose 时按实现实例判断「是不是自己那一份」,宿主 `PluginProtocolRegistry` 按注册条目判断,两边语义不一致;五个第一方插件都用了 2.0.4 的 `PluginIcon` / `PanelOptions.Icon`,`minSdkVersion` 却声明不足;dev-guide §1 与模板仓库的 README / AGENTS / props 注释仍写着 `.Build` 在 velashell-plugin-cli(2026-09-11 已搬到 velashell-plugin-sdk);`VelaShell.Plugin.Templates.slnx` 引用了不存在的 `docs/*.md`。都记入 `feature-plan.md`。

**文档**:velashell-docs `templates/dev-guide`(§1、§2.2、新增 §2.4、§5.9、§5.13 / §5.14、§7)、`templates/README`、`templates/release-process`、`sdk/sdk-reference` §1、`plugins/09-sdk-and-tooling`、`plugins/STATUS`(zh / en);模板仓库自己的 README 与 AGENTS.md 随 PR 改。

## 248. 2026-10-24 SSH:连接信息面板、协商失败一键放开算法、按键时序混淆;共享密码列表与补全弹层收尾

**连接信息面板**(一、终端与 SSH):SSH 库早就交出这条连接谈成的算法、主机密钥、会话 ID、对端版本、认证方法与 server-sig-algs、各阶段耗时、往返时间、重协商次数 / 起因 / 耗时、收发字节与开着的通道快照(§161 的 O7),宿主一处都没显示 —— 排障时只能开「连接诊断」重新连一遍去探。成功建连时经过的每一跳也没记:库的拨号器只在失败时往异常的 `Hops` 里放逐跳结果。
- 数据:Core 新增中立快照 `SshConnectionDetails`(对端、主机密钥、算法、认证、计时、往返、重协商、流量、通道、代理、跳板),`ISshClientWrapper.GetConnectionDetails()` 默认实现返回 null(测试替身不用改);`VelaSshClientWrapper` 经 `SshConnectionDetailsBuilder` 只读 `SshConnection` 的公开只读属性,**不发任何报文** —— 往返时间取 `LastRoundTrip`(状态栏延迟测量与保活顺手量的那次),不现量。
- 跳板:〔决策〕由宿主记而不改库。跳板连接本来就是 `SshConnectionAssembler` 在回调里一跳一跳现建的,在那里把连上的那一跳(连同它的 `SshConnection`)记进 `SshRouteRecorder`,还能顺带拿到每跳谈成的算法与认证方法 —— 库的逐跳记录里本来没有这些;改库要让拨号器经返回的流把跳信息带回连接,动的是净室库的公开面,收益反而少。出站代理由 `ProxyTransportDialer` 记下走的是哪个。每次连接开头清空重记。
- 界面:`SshConnectionInfoWindow`(工具窗外框,可缩放,尺寸与位置记在 `connectionInfo`(§241 的 `RememberAsync`,合入时补的位置),每个标签至多一扇,再点一次前置并刷新):标题栏放会话标识(连着绿点 / 没连着红点)与「复制为文本」「刷新」两个图标按钮(DESIGN.md §4.2),副标题是目标与取快照的时刻;正文按块摆对端、协商结果(两个方向一样只写一个,不一样写「发 a · 收 b」;AEAD 时 MAC 写「AEAD 加密自带」)、认证、计时、流量与重协商(做过时列最近一次起因与观测值 / 阈值)、通道表(号、类型、状态、开了多久、收发字节)、经过的代理与跳板(已断开的标红);值一律等宽可划选。数据源是按标签此刻的会话去取的回调:重连之后点刷新就跟上新连接,标签断着或关了就如实说「没有连着」。手动刷新,不自动轮询、也不订阅 `Rekeyed` —— 自动刷新会让人在划选复制时内容跳动;通道类型显示协议原名(同是 `session` 通道,库的快照里分不出 shell 还是 SFTP)。
- 入口:标签右键「连接信息…」(只对 SSH 会话标签出现,没连着时灰掉)、命令面板「SSH 连接信息」。状态栏入口没做:状态栏左组是 `DESIGN.md` 规定的只读信息区,加点击会改它的交互口径。

**协商失败一键放开算法**(二、D):§134 起协商失败的诊断会点名「对端有、本版实现了只是没放开」的算法并指到连接配置,用户还得自己翻开配置页、找对类别、照着写一行 `+…`。
- 计算:`Infrastructure/Ssh/SshAlgorithmRemedy` 从连接失败的异常链里找出 `SshNegotiationException`,算出可放开的那几个(对端给的 ∩ 本版实现的 − 我们这次报出去的,按对端顺序去重);诊断那一行(`SshInterop.DescribeRemedy`)改为调同一份计算,点名的与按钮写进去的永远是同一组。
- 写入:`ApplyTo` 把它们写进那一类的**自定义清单**:没写过 → `+a`;`+…` 或整份替换 → 接在后面;`-…` / `^…` 一行里接不上第二种前缀,写成此刻生效的完整清单再接上 —— 代价是把「默认」冻结在此刻(以后库的默认清单变了这条配置不跟着变),确认框里点名;若用户为缓解 Terrapin 用 `-chacha20*` 删掉了对端唯一剩下的那个,补救会把它加回来,同样点名、由用户决定。〔决策〕不开「允许老算法」总开关:那会一次放开三类 SHA-1 算法外加 SHA-1 的 RSA 用户签名。
- 跳板:谈不成的若是跳板,`SshConnectionAssembler` 在每一跳的建连上挂一个只记不接的异常过滤器,把「离终点几跳、那一跳的地址」记到那个异常对象上(库会把它包进「连不上跳板」原样往外传);补救据此顺着配置的「跳板主机」往上数到那一跳,改跳板自己的配置。
- 界面:终端标签的失败覆盖层在「复制」与「重新连接」之间多一颗描边按钮「放开这几个算法并重连」(警示色图标,悬停说明往哪一类清单加哪几个),只在有补救时出现;对端的算法本版一个都没实现(只剩 CBC)时不出。点了先弹确认框(点名连接、类别与算法,说明只加这几个、它们比默认弱),确认后改**库里那条配置**并经工作流落盘 —— 不取标签手里那份,登录框交回来的副本可能带着没让记住的口令;终点一跳同时改标签手里那份,然后就地重连。「最近连接」重建的临时配置不在库里,只改这一次;跳板配置找不到就提示用户手改、不重连。只接了终端标签的覆盖层,独立 SFTP / 连接诊断 / 测试连接失败时没有这颗按钮。

**按键时序混淆**(一、「SSH 库已有、宿主还没接的能力」里的一项):§161 在库里落地了 `SshShellOptions.ObscureKeystrokeTiming`(输入按固定节拍发、打字时空着的节拍发等长的 PING 掩护),宿主没接 —— 交互 shell 每次按键一个报文,按键间隔是公认的侧信道。`SshSessionOptions.ObscureKeystrokeTiming` 默认关,只开它一项也不算「空」(否则配置页存回 null 就丢了),`Clone` 补上,并加反射用例 `Options_Clone_CopiesEveryWritableProperty` 钉住每个可写属性都拷了(§37 名单里的配置复制、不记密码副本、持久化前拷贝都经它)。连接配置「SSH 选项 → 传输」在压缩下面加勾选项,只对 SSH(有交互 shell)显示、SFTP 配置不存,灰字说清代价:打字时每个方向每秒约 50 个报文、每次按键最多晚 20 毫秒,闲着不多发、粘贴不受影响,掩护要服务端支持 `ping@openssh.com`(OpenSSH 9.5 起)。开 shell 时按 20 毫秒节拍(OpenSSH `ObscureKeystrokeTiming yes` 的值,不开放成可调)交给库;终端顶部写一行灰字说明已开启,服务端不认 PING 时改成黄字说明只攒批、按键间隔仍看得出来;连接信息窗口的「流量与重协商」块里也显示它的状态。〔决策〕默认关,与 OpenSSH(默认开)相反,理由是带宽代价;ssh_config 导入没映射它(照抄 OpenSSH 的默认会让导入的配置全部开着,口径记入 `feature-plan.md` 待维护者定)。

**共享密码列表与补全弹层**(§222 留下的两处):防空闲的 NUL 与同步输入转来的 `SendRaw` 不走 `TypedInput`,共享密码列表不会随之收起,点哪项都只回「已过期」→ `SshTerminalBridge` 加出站事件 `InputQueued`:击键、`SendRaw`(同步输入、程序注入)、防空闲、敏感输入、会话中途补注,凡是让 `InputGeneration` 加一的入队都在锁外报一次(防空闲在计时器线程上,订阅者自己封送)。`SharedPasswordFillRequest` 订阅它:不经键入的出站输入同样作废请求、收起列表(不记成拒绝,提示还会再画一次入口)。已经点选、正在读取口令时例外 —— 按入队那一刻判断,这时不收列表,由 `FillAsync` 的复核回「已过期」并在列表上说明什么都没发出去(保住 `LatePassword` 那条「失败要说明没发出去」的既有设计);填入自己那一次入队(§234 的正文 + 回车)同理不算。命令补全弹层去掉 `VerticalOffset="2"`(定位器把它翻到光标行上方时偏移量不跟着反向,压进光标行 2px),缝照密码弹层的做法做在锚点矩形上(`GetCursorRect(…).Inflate(0, 2)`),两个弹层共用一个 `CursorPopupGap`。

**验证**:`SshConnectionDetailsTests` 在环回 TCP 上的测试服务端(新测试辅助 `tests/VelaShell.Core.Tests/Ssh/LoopbackSshHost.cs`,可当跳板,别的宿主侧 SSH 用例可复用)走 `SshConnectionAssembler` → `VelaSshClientWrapper` 的真实装配路径,含经跳板与同一份装配重连时从头记;`SshAlgorithmRemedyTests` 在只认 `diffie-hellman-group14-sha1` 的环回服务端上(终点与跳板两种)验证点名、写入与写入后真能连上且谈成的就是它,`AllowAlgorithmsFlowTests` 走 `MainWindowViewModel` 的重连流程验证落盘的是库里那份且只多了 `KexAlgorithms = +diffie-hellman-group14-sha1`、`LegacyAlgorithms` 仍为 false;按键混淆在环回服务端上覆盖默认、认 PING、不认 PING 三种情形;桥的四条入队路径各报一次且与代次同步,同步输入与计时器线程上的 NUL 都让列表收起,补全弹层上下两种落点都留缝。逐项临时去掉实现(成功时记跳板、每次连接开头清空、跳板打标、失败时带补救、`Clone` 那一行、入队事件、改回 `VerticalOffset`),对应用例变红。合入时 dev 上新加的守卫 `FullyPopulatedSample_LeavesNoPropertyAtItsDefault`(§240)因新字段变红,样本补上。全量 `-warnaserror` 0 警告,各测试工程按 CI 口径全过;`ssh-legacy` 靶机(2225)没在跑,`LegacyAlgorithmsIntegrationTests` 没跑,端到端改由环回测试服务端覆盖。

**文档**:velashell-docs `host/交互与界面规格` §4B.2(标签右键)、新增连接信息窗口一节、§5.2.1、§7 断开连接状态、§8 命令面板、§13.1 SSH 选项(zh / en);仓库内 `manual.md` / `manual.en.md` §5.2、§13.4、§16.3。

## 249. 2026-10-25 共享密码列表认出 sudo-rs 的提示;侧栏搜索框与几处输入框的高度按设计生效

**来由**:用户反馈两件事。① Ubuntu 上 `sudo apt update` 问口令时共享密码列表没有自动弹出,只能从命令面板手动打开;那台机器的 sudo 是 sudo-rs(Ubuntu 25.10 起的默认 sudo),提示是 `[sudo: authenticate] Password:`。② 资源管理器的过滤框和快捷命令区的「搜索代码片段」框比软件里别处的搜索框高,要降到一致,并顺带查别处有没有同样的不一致。

**sudo-rs 的提示**:`InteractivePromptDetector.IsPasswordFillPrompt` 的正则要求行首直接是 password 一类的词,`[sudo] ` 前缀只在 `password for …` 那一支里允许 —— sudo-rs 在 PAM 的原话前面加 `[sudo: authenticate] `,两条路都对不上。现在行首的 sudo 一族标签(`[sudo]`、`[sudo: authenticate]`,以 `sudo-rs` / `su` / `doas` 的名字调用时标签跟着变)所有写法共用;只认这几个名字,`[joe@host ~]` 一类方括号开头的 shell 提示符照旧不算。顺带补上本来同样认不出的本地化 sudo(`[sudo] joe 的密码：` / `のパスワード:` / `의 암호:`,界面支持这三种语言)与 doas 的 `doas (joe@host) password:`。口令输错后 sudo 先单独回一个换行、等完 PAM 的失败延迟再报错重问,那个换行会清掉「同一提示不重开」的抑制,所以重问的同一句提示照样再给一次列表 —— 端到端用例照用户那段输出走了一遍。

**输入框高度**:Fluent 主题给 `TextBox` / `ComboBox` / `CheckBox` / `RadioButton` 这一类设了 `MinHeight="32"`,而 Avalonia 排版时 `MinHeight` 压过 `Height` —— 过滤框与快捷命令搜索框按 26 写、实际渲染成 32(headless 实测),比任务管理器 / 链路追踪 / 审计日志的搜索框(28)高出一截。两者改为 28 并写上 `MinHeight`。全仓扫出同类的还有文件面板与本机面板的路径编辑框:按 22 写、实际 32,一进编辑整排跳高 8px(同一排的 ComboBox 早就写了 `MinHeight="0"`,只有它俩漏了),补上后按 22 生效;另把远端搜索窗口(§239)没设高度、吃默认 32 的三个条件框改成 28,与同从文件面板打开的目录同步窗口的 `TextBox.field` 一致。DESIGN.md §4.2 的标准高度表加一行「搜索 / 过滤框 28px」(设置页与隧道面板 30、对话框表单 32),写明 `MinHeight` 这个坑;§5.4 的过滤框规格随之改为 28。

**验证**:`PromptDetection_IsConservative` 加 13 组写法(sudo-rs 的几种、本地化 sudo、doas,以及方括号开头的 shell 提示符、标签后面是验证码、已回显输入、认证失败那一行这几种不该认的);新增 `Popup_OpensForSudoRsPrompt_AndAgainAfterAFailedAttempt` 照用户那段输出走一遍「弹出 → 手输收口 → 失败重问再弹」。改回旧正则,10 条变红。新增 `Design/InputMinHeightTests` 扫全部 `.axaml`:比 32 矮的 `Height`(元素上或样式里)没有同时写 `MinHeight` 就红,改回原写法时正好列出那 4 处;`SidebarSearchBoxHeightUiTests` 实测过滤框按 28 渲染。headless 截图核对 28 高时放大镜、文字与清空钮都垂直居中。`VelaShell.Tests` 全量 2856 通过,`-warnaserror` 0 警告。

**文档**:velashell-docs `host/交互与界面规格` §5.2.1 识别的提示写法补 sudo-rs、本地化 sudo 与 doas(zh / en);仓库内 `manual.md` / `manual.en.md` 5.2、DESIGN.md §4.2 / §5.4。

## 250. 2026-10-25 SFTP 双栏与 WinSCP 的差距:接线债逐项核对,补上本地栏排序、远端栏「上一级」、文件标签跟随设置、隐藏文件与列显示、续传限速

**来由**:`feature-plan.md` 🚧「SFTP 双栏与 WinSCP 的剩余差距」。velashell-docs《SFTP双栏与WinSCP差距分析》(2026-07-22 写)「A 类接线债」的 12 项对照代码核了一遍,文档的状态落后于代码不少:A1(默认编辑器,07-22)、A3 的重试与「清理已完成」(07-23)、A6(文件标签里隐藏远端栏的关闭钮,07-29)、A11(日志分 COPY / RELAY,09-28)早已修好,「可编辑路径输入框」(#226、§200)与 SFTP 上传按「保留时间戳」回写修改时间也早就有了。剩下的里头成本低、不用定口径的五项这次补上;A3 的单条取消、A4 `TransferManager`、A9「下载为…」、A12 系统文件拖进本地栏要先定口径,能力缺失与架构级的没动,都留在 `feature-plan.md`。

**改动**:
- 续传下载限速(A10):`SftpService.DownloadFileAsync` 的续传分支为了从核实过的起点截断续写,自己从远端流搬字节、不经 `client.DownloadAsync`,也就没包 `ThrottledStream` —— 开着带宽限制,整份下载限速、一续传就按全速跑。改成同样包住本地写流。上传两条路与 `DownloadStreamAsync` 本来就包着。
- 本地栏表头排序(A2):`LocalFilePaneViewModel` 的 `SortCommand` 早就写好,表头却是三个 `TextBlock`。换成与远端栏同规格的列头按钮(`col-header` 样式),补 `NameSortGlyph` / `SizeSortGlyph` / `ModifiedSortGlyph`,当前列后面带 ▲ / ▼;规则不变(目录始终在前,同列翻转,换列从升序起)。
- 远端栏「上一级」(A5):`FileBrowserViewModel.GoUpCommand` 一直没人绑,回上级只能双击「..」行。工具条上挨着刷新加一颗 `corner-left-up`(终端侧栏与文件标签的远端栏都有),在 `/` 时不动。双栏里两颗上一级钮挨在一起,照刷新钮的先例各给一条区分栏位的提示语兼读屏名(新键 `Sftp_GoUpLocalPane` / `Sftp_GoUpRemotePane`,五语)。
- 文件标签跟随设置(A7):`OnSettingsSaved` 只广播到终端侧栏的面板,单栏、双栏文件标签各自新建的面板一直拿着打开那一刻的 `TransferOptions` —— 改了冲突策略、并发、重试次数、双击行为要重开标签才生效(手册 15.7 原来就是这么教的)。`ISftpDocumentContent` 加 `RemotePanes`(单栏一栏、双栏两栏),广播时连 `Layout.AllDocuments()` 里的文件标签一起铺。限速、保留时间戳、上传后落盘由 `SftpService` 每个文件现读设置,本来就跟得上。〔决策〕已经开始的一批不回头改:同名冲突在开始前已按当时的策略处理完,并发上限按开始时的值,同一批里只有重试次数(每个文件现读)会变。
- 隐藏文件与列显示(A8):终端侧栏的面板按设置铺「显示隐藏文件」与六个列开关、工具栏 / 表头右键切换经回调写回设置(设置审计 C-04),文件标签的面板两样都没接 —— 设置里开着隐藏文件,文件标签照样不显示,切了也不留。抽出 `FileBrowserViewModel.ApplyDisplayOptions`(终端侧栏的广播也改用它,删掉 `MainWindowViewModel.ApplyColumnVisibility`);两个文档视图模型建面板时按设置铺,宿主在四个打开路径(SSH / SFTP、FTP、插件协议、双栏远程)建好之后由 `WireDocumentPanesToSettings` 挂写回回调,设置保存时与 A7 一起广播。〔决策〕沿用 C-04 的口径:这是一份全局设置,在任一面板上切都同步到其余开着的面板(含终端侧栏);远端搜索「定位」时为显示点文件临时打开的仍只作用于那个面板、不写回。

**验证**:新增 8 条 —— Core `SftpServiceTests.DownloadFileAsync_ResumeHonoursTheDownloadBandwidthLimit`(1 MiB/s 下续传 1 MiB 多一点,只断言耗时下界);宿主 `LocalFilePaneViewUiTests` 1 条(真指针点列头排序与翻转)、`FilePaneToolbarUiTests` 2 条(点上一级、两颗按钮的提示语与读屏名)、`SftpDocumentSettingsTests` 4 条(广播换选项、打开时按设置铺、广播隐藏文件与列、走真实 `OpenSftpForProfileAsync` 打开路径的写回)。逐项临时撤掉实现,对应用例变红(续传改回直接写本地流时报「续传了 1114112 字节、限速 1 MiB/s,却只用了 13 ms」)。`-warnaserror` 0 警告;按 CI 口径 `VelaShell.Tests` 2863 通过 / 9 跳过、`VelaShell.Core.Tests` 1252 通过 / 6 跳过。headless 截图核过本地栏表头与远端栏工具条。

**文档**:velashell-docs `host/SFTP双栏与WinSCP差距分析`(A 类表加状态列,3.1 – 3.7 与第五节的优先级改准)、`host/交互与界面规格` §6、`host/settings-audit`(C-04 与失败重试一批)(zh / en);仓库内 `manual.md` / `manual.en.md` 9.1、15.7。

## 251. 2026-10-25 SSH 库:限速一段一段搬、FIPS 预设补上群交换与 group18;runtime-async 下 Span 跨 await 坐实是编译器不报 CS4007

**限速的突发粒度**(`feature-plan.md`「SSH 库:限速的突发粒度」):§244 接上隧道限速后,端到端用例合入 dev 就稳定变红 —— `DuplexRelay` 按一次读到的整段调 `ByteRateLimiter`,机器忙时通道那头一次交出约 200 KB,64 KB/s 的隧道先什么都不写、等三秒多,再整块写出、整块计数。平均速率是对的,单次突发却是 velashell-docs spec/07 §5.1「一秒额度」的好几倍,§244 只好把用例改成不在固定时刻断言字节数。
- 改动:限速时搬运循环一段一段搬,每段不超过一块缓冲(32 KiB,spec/07 §六),也不超过限速放行的量。`ByteRateLimiter.WaitAsync` 换成 `AcquireAsync(wanted)`:一次至多放行一秒的额度(`MaxBurst`),额度不够照旧欠着、按欠额等,返回这一次放行了多少;`DuplexRelay` 的限速回调相应改成 `Func<int, CancellationToken, ValueTask<int>>`,只写出放行的那一段、`AdvanceTo` 只推进到段尾,剩下的留在读缓冲里、下一轮 `ReadAsync` 当场交回;读到结尾且这一轮读到的都搬完才算结束。等的时候照样不读新数据(背压不变),不限速时一行没变,令牌桶本身的记账(一秒突发、欠额、歇够了补满但不超过一秒)没动。〔决策〕每段另封顶一块缓冲,好让计数一块一跳、两个方向口径一致(本机那头本来每次至多读 32 KiB),高限速下多出的锁内记账可以忽略;一条连接等额度时被取消,已记账却没写出去的那一段(≤ 32 KiB)不退回共用的桶,同一转发器的其它连接替它多等至多一段(以前整块记账时退得更多)。
- 验证:库新增 4 条手动时钟用例 —— 一次至多放行一秒的额度;一次到齐的十秒数据当场只写出一秒的额度、之后每秒一段、九秒搬完,总时长与整块等相同;额度比一块缓冲还宽时每段也不超过 32 KiB;等额度时取消当场停下、两端中止,只报已写出的第一段。临时恢复整块等,4 条全红。`TunnelForwardingIntegrationTests` 的限速用例收紧回搬运途中的断言:每 50 ms 读一次快照,任意两次读数之间多收的不超过「这段时间按速率补的 + 一秒突发 + 读数那一刻正在写的一段」(窗口多算 20 ms,容定时器早醒),途中要读到「已收大于 0、小于总量」且速率有读数,收完至少 2.9 秒(原来是 2 秒)。在 ssh-shells 上用新实现跑 9 次全过(离上限最近还差 67–70 KB),临时恢复整块等跑 3 次全红。

**FIPS 预设**(`feature-plan.md`「FIPS 预设的口径」):`FipsApprovedOnly` 的注释说它与 RHEL 的 FIPS 策略对 SSH 放行的一致,实际少了群交换与 `diffie-hellman-group18-sha512`(§164)。维护者 2026-10-25 定:清单补上 `diffie-hellman-group-exchange-sha256` 与 group18,与 RHEL 一致,注释改准。先核:群交换早已实现,group18 却还在 `SshAlgorithmCatalog.KnownUnimplemented` 里 —— 只加名字的话连接前的校验会拒绝,`FipsApprovedOnly` 一用就抛;「与 RHEL 一致」的依据是正在运行的 `velashell-test-pq` 容器(AlmaLinux 10.2,crypto-policies 20260216)里的 `/usr/share/crypto-policies/FIPS/openssh.txt` 与 `opensshserver.txt`(发行版的策略数据,不是 SSH 实现的源码,不碰净室规程)。
- 改动:新增 `SshAlgorithmNames.DiffieHellmanGroup18Sha512`(RFC 8268 §3;RFC 3526 §7 的 MODP 8192 位群,参数取 BouncyCastle 的 `DHStandardGroups.rfc3526_8192`),与 group14 / 16 同一个实现 `DiffieHellmanGroupKeyExchange`(私指数 2× 哈希长度,即 1024 位),注册进工厂并从目录的「认得却没实现」挪到「实现了的」;测试工具 `TestKexResponder` / `TestSshServer` 认它。`FipsApprovedOnly` 的密钥交换变为 `mlkem768nistp256-sha256`、`mlkem1024nistp384-sha384`、`ecdh-sha2-nistp256/384/521`、`diffie-hellman-group-exchange-sha256`、`group16-sha512`、`group18-sha512`、`group14-sha256`。注释按策略文件改准:密钥交换、主机密钥与 MAC 和 RHEL 10.2 的 FIPS 策略是同一组;加密多一个 `aes192-ctr`(AES-192 同样是 FIPS 197 认可的,RHEL 没开;去掉属于收紧,超出这次拍板的范围);顺序按本库的规则排(RHEL 是 gex、14、16、18),不照抄策略;另加一条〔历史〕。〔决策〕默认清单也收进 group18,排在 group16 之后、group14 之前 —— 维护者的决定只说了 FIPS 清单,这一步是另加的:算法目录「实现了的」口径就是默认清单加老算法,只进 FIPS 清单的话目录会说它没实现,与校验、工厂表对不上;道理与当初把群交换加进默认清单一样,只剩它可谈的服务端原来连不上;两种都给的服务端照旧谈成 group16(8192 位的模幂慢好几倍),对现有连接的影响只是 KEXINIT 里多一个名字。
- 验证:新增 `AlgorithmCatalogTests.FIPS清单与RHEL的FIPS策略是同一组算法`(四类按集合比较,加密允许多 `aes192-ctr`)与 `HandshakeTests.FIPS清单与只给群交换或group18的服务端谈得成`(内存服务端,交换哈希一致);有限域 DH 共享密钥用例加 group18,「每一种密钥交换算法都能完成握手」随默认清单覆盖到它。临时把两项从 FIPS 清单里拿掉,两条新用例红。互操作:ssh-shells(2223)的 OpenSSH 9.9 默认给 group18,「每一种密钥交换」里 group18 握手并执行命令通过,FIPS 清单、群交换两条也通过;ssh-pq(2226–2228)上 7 条面向 FIPS 的混合 KEX 用例照旧全过。`scripts/ssh/interop/kex-gex.sh` 没改(2229 那台默认不开 DH 的 OpenSSH 10 靶机没起来,验不了)。

**runtime-async 下 Span 跨 await**(`feature-plan.md`「runtime-async 下 Span 跨 await 的可疑失败」):§222 修更新下载时,`zip.AsSpan().SequenceEqual(await File.ReadAllBytesAsync(…))` 编译通过,却在全量运行里失败过一次;改成先 await 再比较后就稳定了,当时没坐实。
- 结论:坐实,是编译器的问题。`Directory.Build.props` 开着 `runtime-async=on`,.NET 11 RC2(SDK 11.0.100-rc.2.26504.105,Roslyn 5.12.0-1.26504.105)在这个模式下**不报 CS4007**,ref struct 跨 await 存活的代码照常编译。IL 里 Span 先 `stloc` 进局部变量,跨过 `AsyncHelpers.Await<T>` 再取出(方法带 `MethodImplOptions.Async`);而 runtime-async 的设计规定 byref-like 局部变量不随挂起保存。于是 await 真挂起之后 Span 回来长度为 0,`SequenceEqual` 恒为 false;await 同步完成时又是对的,表现成偶发失败。同一份代码关掉 runtime-async 就报 CS4007。最小复现是一个 file-based app(`#:property Features=runtime-async=on`):`data.AsSpan().SequenceEqual(await CopyAsync(data))`,`CopyAsync` 里 `await Task.Yield()`,输出 False(关掉 runtime-async 报 CS4007);同样受影响的还有显式的 Span 局部变量(读回长度 0)、数组当接收者(C# 14 起 `bytes.SequenceEqual(await …)` 绑到 Span 的重载)、只放对象引用的 ref struct(碰巧还在,同样非法)。Debug 与 Release 结果相同,2000 次循环错 2000 次,不触发 GC 也错 —— 不是 GC 搬动的问题。`ref` 局部变量跨 await(CS9217)两种模式都照报,缺口只在 CS4007。相关上游:dotnet/roslyn#82608(非 runtime-async 下 `SequenceEqual(await …)` 的 CS4007)、dotnet/runtime#136000 / #136171(byref-like 局部变量与 runtime-async 续体),这两个 runtime issue 只看过检索摘要。
- 改动:仓库根 `AGENTS.md`「几条会让你踩坑的硬约束」加一条:Span / ReadOnlySpan 与任何 ref struct 都不许跨 await 存活,哪怕编译通过;写明症状、最容易踩的两种一行写法、规避办法,以及拿不准时用 `-p:Features=runtime-async=off` 编一遍查 CS4007。照这个办法全仓编了一遍(25 个工程):`src/` 干净,只有测试工具 `TestChannelServer.OnGlobalRequestAsync` 的 `SshDataReader` 读完公共字段之后跨过 `BeforeGlobalReply` 钩子的 await 接着读(4 处,`Core.Tests` 链接了同一份文件,共报 8 条)。现有用例的钩子都同步完成,所以没出过事;钩子真挂起时服务端读到截断、回拒绝。改为钩子之前取出剩余字节、之后另起读取器。
- 验证:临时写一条钩子里 `await Task.Yield()` 的远程转发用例,改前红、改后绿,看完删掉;改后关掉 runtime-async 全仓编译 0 错误。

**验证(三项合起来)**:`-warnaserror` 0 警告;Debug、按 CI 口径:`VelaShell.Ssh.Tests` 1441 通过、`VelaShell.Core.Tests` 1251 通过 / 6 跳过、`VelaShell.Infrastructure.Tests` 977 通过 / 26 跳过、`VelaShell.Tests` 2856 通过 / 9 跳过;DockerIntegration `TunnelForwardingIntegrationTests` 4 条在 ssh-shells 上真跑(不是早退)。

**文档**:velashell-docs `ssh/spec/07-forwarding` §5.1(「大块切开搬」的决策与历史)、`host/隧道功能规划`「按隧道限速」的突发口径、`ssh/spec/00-overview` §6.1 / §6.6、`ssh/spec/03-key-exchange` §3.4(zh / en);Span 那一条只进了本仓库的 `AGENTS.md`。

## 252. 2026-10-25 手册里的三处安全说法改代码对齐:首次连接确认指纹默认开、无口令云同步不带设置里的机密、外部拉起的临时配置不落库

**来由**:`feature-plan.md` 🟠 P1「用户手册里的安全说法与既有代码不符(#652 复审)」(§231 记为留待定)。手册说首次连接会弹窗核对指纹、自动重连不会自动信任,代码却默认 `ConfirmFirstFingerprint = false`,首次连接直接永久信任;手册说没设端到端口令的 Gist 同步不带机密,`GistSyncService` 却把设置里的代理密码与告警 Webhook 地址(机器人地址本身就是令牌)原样上传 —— 设置导出那一路已在 §237 剔掉;手册说外部拉起的一次性连接不保存,它连上之后却落进会话树、跟着同步上了云。维护者 2026-10-25 定:三处都改代码对齐手册 —— 首次连接的指纹确认默认开(与 OpenSSH 一致);同步前剔除,复用 §237 的 `SettingsCategories.SecretFields`;一次性连接不落库、不进同步。

**改动**:
- 首次确认:`SecurityOptions.ConfirmFirstFingerprint` 默认 `true`。存量配置里的 `false` 分不清是旧默认还是用户主动关的,照 `RecordingOptInMigrated` / `FingerprintChangeDefaultMigrated` 的套路在 `AppSettings.Normalize` 统一打开一次、打上 `FirstFingerprintDefaultMigrated`,之后听用户的;标记登记进 `SettingsCategories.MigrationFields`(审计不算设置变更,导出与同步照带),由守卫 `EveryMigratedFlag_IsRegisteredAsAMigrationField` 盯着。`VelaHostKeyPolicy` 的判定没动。各路径遇到没见过的主机(所有 SSH 都经 `SshConnectionAssembler` → 同一个 `VelaHostKeyPolicy`):界面上的各种连接、测试连接与连接诊断、独立 SFTP、隧道的手动启动与「程序启动时自动建立」、插件 `open_session`、传输队列「继续」、外部拉起(先过外部拉起确认框)都弹「主机信任」框(`HostKeyPromptDialogService`),拿不到主窗口时按拒绝;自动重连(倒计时、睡眠唤醒后的批量重连、隧道自动重拨)照 §193 不弹、直接拒绝并记 `hostkey-rejected` 告警 —— 以前默认关时这里是按 TOFU 悄悄记下。「仅本次信任」的在本次运行内仍由 `HostTrustOnceCache` 放行,受信主机 CA 管的主机不受影响。没注入 `IHostKeyPrompt` 的组装(只有测试)仍走「不问」的两条默认(首次 TOFU、变更拒绝):改成「本该问却没人能问 → 拒绝」更一致,但那些用 `(store, null, null, null)` 写的策略用例会一片红,没动。
- 云同步:`Core/Import/SettingsTransfer` 新增 `WithoutSecrets`(与导出同一份名单、同一种剔法:在 JSON 里拿掉再读回,原对象不动)与 `KeepLocalSecrets`(与导入同一套规矩:代理的类型、主机、端口、用户名都相同才留本机密码,Webhook 地址一律留本机的,载荷自己带着值的照用),`Merge` 改调同一对私有规则,导入行为不变。`GistSyncService` 只动两处:`BuildGistContentAsync` 推送时没设端到端口令就剔(`WithoutSecretsUnlessEncrypted`);拉取时不论加没加密都按上面的规矩保留(`PreserveLocalSecrets`,在 `PreserveDeviceLocalFields` 开头调),与连接配置「载荷里密码为空就留本机的」同一口径。〔决策〕设了口令的同步照带:载荷整份在本机做 AES-GCM、GitHub 只拿到密文,连接配置的密码这时也随同步走,§237 与手册 17.4 把「带机密换机」指的本来就是这条路;加密了还剔,只会让人换台机器又补填一遍。
- 外部拉起:原来 `ExternalLaunchCoordinator` 现搭的临时配置(`RememberPassword = false`)连上之后,`ConnectionWorkflowService.ConnectProfileCoreAsync` 无条件 `SaveSessionAsync`,这条配置去掉密码落进会话树,`GistSyncService` 从仓储取配置,也就跟着上了云。改为回写前先 `GetSessionAsync(profile.Id)`,库里有才保存 —— 即 `PersistProfileIfSavedAsync` 已有的「只回写已保存配置」口径,布局模板与 FTP 证书信任那边的代码本来就假定临时配置不在库里。临时配置只活在本次运行:标签照常开、断线照常重连,不进会话树、不随云同步上传,下次启动不恢复(`LastOpenProfileIds` 里那个 Id 在库里查不到,跳过);最近连接(`conn_history`,本就不存密码)与 `audit_log` 照记。没带凭据、复用已保存同目标配置的路径不变,仍更新它的最近连接时间。`ExternalLaunchCoordinator.ResolveProfileAsync` 改为 `internal static`(多一个仓储参数)以便测试。

**取舍**:改的是工作流本身的写回规则,所以外部拉起之外还有三处跟着变 ——「最近连接」里配置已删时重建的临时配置不再以新 Id「复活」进会话树;连着的标签所属配置被删之后,重连不再把它写回来;这类临时连接的登录框里勾「记住密码」也不再落盘(勾选框照旧显示,记入 `feature-plan.md`)。默认开之后,批量打开 /「打开全部并同步输入」与启动时恢复多个会话会为每台没见过的主机各弹一个框(与 OpenSSH 每个 `ssh` 进程各问一次同理),叠框的样子没在界面上实测。还没升级的设备拉到新版本无口令推上去的设置会照单全收,把本机的代理密码与 Webhook 地址清空(旧版本没有保留规则),Webhook 告警随之悄无声息地停掉;〔决策〕没为此抬载荷版本 —— 抬了旧客户端整份拒收,连接与片段也同步不了,代价更大。拉到开着 Webhook 告警却没有地址的设置时静默留空,不像设置导入那样提示重填(云同步本来就是静默的)。

**验证**:`-warnaserror` 全量 0 警告;按 CI 口径 `VelaShell.Core.Tests` 1258 通过 / 6 跳过、`VelaShell.Infrastructure.Tests` 985 / 26 跳过、`VelaShell.Presentation.Tests` 94、`VelaShell.Tests` 2859 / 9 跳过,全过。新增:`AppSettingsNormalizeTests` 3 条(默认开;旧 JSON 的 `false` 规整后为 `true` 且标记落下;迁移之后用户再关不会被打开)与 `VelaHostKeyPolicyTests` 3 条(出厂默认下没见过的主机先弹框,带设置与不带设置各一;自动重连直接拒绝、不弹框、记告警);`SettingsTransferTests` 4 条(含守卫:`SecretFields` 每一项拉取时都要有「没带就留本机的」规则)与 `GistSyncServiceTests` 5 条(无口令推送不带、加密推送的明文里没有而解密后在、同一台代理留密码、换了代理不留、对方带着照用);`ConnectionWorkflowServiceTests` 1 条与新文件 `ExternalLaunchTemporaryProfileTests` 3 条(真 SonnetDB:带口令时现搭的那份连接与重连都不在库里、同目标的已存配置没被改;不带凭据且库里没有时同样不落库;库里有时复用并更新最近连接时间)。改了的老用例:`自动重连时不问的默认照旧` 显式关掉首次确认,`ConnectProfileAsync_SavesLastConnectedAt` 与 `ConnectionWorkflowCredentialTests` 的 2 条先把配置登记成已保存。三项各自临时去掉实现,分别 5 / 5 / 3 条变红。DockerIntegration 没跑:相关用例都用 `hostKey: null`,不经这次的改动。

**文档**:velashell-docs `host/settings-audit`(新增一批:首次确认默认开与迁移标记;第十七批补一句无口令同步同样不带机密)、`host/交互与界面规格`(主机指纹确认一条写明默认开;设置窗口「安全审计」「云同步」两行)、`host/Xshell兼容登录`(§5 凭据处理、§6 风险表)(zh / en);仓库内 `manual.md` / `manual.en.md` 的 6.1、15.8、16.1、17.2、17.4、20.3 与 `PRIVACY.md` 云同步一条。

**手册留在本仓库**(维护者 2026-10-25 定):`manual.md` / `manual.en.md` 是写给用户、跟着发布版本走的使用手册,列进 `AGENTS.md` 第三节「留在本仓库的文档」的例外表(21b49851),并写明「改了用户看得见的行为,就在同一个提交里改手册,中英两份一起改」;设计与开发文档仍在 velashell-docs。

## 253. 2026-10-25 主密码保护第一步:本机加密密钥交给系统密钥链(macOS 钥匙串 / Linux Secret Service,Windows 仍是 DPAPI)

**来由**:`feature-plan.md` 🔴 P0「主密码保护」。维护者 2026-10-25 定:先把本机加密密钥改存系统密钥链(Windows DPAPI / macOS Keychain / Linux Secret Service),用户无感;可选主密码作为第二步,先写设计文档(velashell-docs),这一轮不实现。核对现状:`AesSecretProtector` 用 `~/.velashell/secret.key` 的 32 字节密钥做 AES-256-GCM(`enc1:` 前缀);Windows 上 `secret.key` 早已是 `ProtectedData.Protect(…, CurrentUser)` 的 DPAPI 密文,换账户、换电脑、拷走数据目录都解不开(域漫游配置文件例外);缺口在 macOS / Linux —— 32 字节明文 + 0600,拿到数据目录(拷走、备份、网盘同步)就能解开全部已存密码、共享凭据、Gist 令牌与同步口令、插件机密与插件信任库。

**改动**:
- 分层照 velashell-docs《系统密钥链与 sudo 凭据填充可行性调研》§3:`Core/Data/IMasterKeyStore` 按条目编号存取 32 字节密钥,同步接口(解析只做一次,`App.Initialize` 建完容器就在后台线程上跑,D-Bus 往返与 keyring 的解锁框不压 UI 线程),任何失败以 `Unavailable(原因)` 交回、不抛。实现在 `Infrastructure/Persistence/`:`MacKeychainMasterKeyStore`(Security.framework `SecItem*`,`LibraryImport`,登录钥匙串里的通用密码,服务 `com.velashell.master-key`、账户是条目编号)、`SecretServiceMasterKeyStore`(libsecret 无变参的 `*v_sync` 版本 + GLib `GHashTable` 属性表,schema `com.velashell.MasterKey`、属性 `key-id`,值是 base64、直接在原生缓冲上解码,不经托管字符串;变参函数经 P/Invoke 在 x86-64 上要设 `%al`,不碰)。Windows 没有新实现:DPAPI 包裹的密钥文件就是那里的保管方式,改存凭据管理器只多一次迁移、不多一分安全。`ISecretProtector` 与全部调用方不改(《凭据管理器集成设计》§1:管加密不管来源),`AesSecretProtector` 只换密钥来源;不带路径根的构造(测试、工具)只用文件,不碰系统密钥链。
- `MasterKeyResolver` 是唯一的决策者(回落链、迁移、多密钥),平台实现互相不知道对方。数据目录里多一个 `secret.key.ref`,只列条目编号、不含密钥;编号 = 域分隔 SHA-256(密钥) 的前 128 位,迁移被打断几次都落在同一条目(幂等、不留孤儿),读回的密钥能对着编号自证。顺序:清单里的编号逐个去密钥链取(第一次「用不了」之后不再逐条去问,免得连弹解锁框)→ 本地密钥文件(存量、回落、旧版本生成的)能迁就迁:写入、读回核对、清单原子落盘(临时文件 + 落盘 + 改名)之后才删文件,删时先改名 `.deleting` 再清零删除,中途断掉不会留下一把全零的「密钥」→ 一把都没有才生成新的,密钥链能用就不落明文文件。任何一步失败都停在「照旧能用」:文件原样留着,下次启动再试。
- 多把密钥并存、从不自动丢弃任何一把:解密依次试每一把(GCM 认证标签说明是不是这把),加密用第一把,密文格式不变。密钥链某次用不了(没起 keyring、没有会话总线、用户点了拒绝),这一轮另起一把本地密钥给新密码用,清单里的旧编号原样留着,密钥链回来那次启动把本地那把并进去;数据目录拷到别的电脑 / 账户(条目不在)、条目内容对不上编号,都如实报「找不到」并另起新密钥,旧编号保留,条目恢复或拷回原机后照样能解;回滚到旧版本、旧版本另生成了一把 `secret.key`,升回来时同样并入。〔决策〕调研里「迁移后保留文件副本一个版本周期」不采用:留着明文副本等于没迁,回滚安全改由密钥并存保证。代价:密钥链暂时不可用的那一轮新存的密码用的是数据目录里的本地临时密钥(明文 0600,与引入前同级);永久缺失的编号一直留在清单里,每次启动多一次查询。
- macOS 钥匙串条目的访问控制放开到「任何应用」(等价 `security add-generic-password -A`,`CreateOpenAccess`):发行包是 ad-hoc 签名(`release.yml` 的 `codesign --sign -`),默认访问控制按代码签名认人、每发一版就变,每次更新后第一次启动都会弹授权框要登录密码,与「用户无感」直接冲突。放开后与原先 0600 的文件同级(本用户进程都读得到),但密钥不再躺在数据目录里 —— 这一步要挡的正是「拿走数据目录」。放开失败(弃用的 SecAccess 接口)时退回默认访问控制。〔决策〕先放开;有了稳定的 Developer ID 签名后改回只认 VelaShell,记在 `feature-plan.md`。
- 回落与排障:`MasterKeyStores.ForCurrentPlatform` 按平台挑后端;`VELASHELL_NO_KEYCHAIN=1` 关掉密钥链(排障退路,已迁的密钥不丢,去掉变量那次启动照常取回),测试工程经 `tests/velashell.runsettings` 默认设上,一律不碰真密钥链。每次启动日志一行 `[Secrets] Master key: …`(后端、几把、是否迁移、取不到的编号、密钥链错误),不含密钥材料。Linux 上 keyring 锁着而用户取消解锁框时,`secret_password_lookupv_sync` 可能回「没找到」而不是错误,会被报成「找不到」(旧编号保留、密钥不丢,解锁后的下一次启动照常取回);首次迁移在钥匙环锁着时会弹系统的解锁 / 新建钥匙环框,取消就留在文件、下次启动再试 —— 自动登录且钥匙环不自动解锁的机器上每次启动都可能弹一次。libsecret / `SecItem` 的同步调用没有超时(在后台线程上等,D-Bus 自身 25 秒兜底)。
- 解不开时的提示:`IMasterKeyStatusSource` 交出解析结果,主窗口在消息中心投「系统」类消息(`NotificationSources.BuildMasterKeyNotifications`,五语):密钥文件被换过(点名改名保留的文件,只提示一次)、密钥链里找不到某把(换机 / 条目被删,按编号只提示一次)、密钥链暂时用不了(说明密码没丢、恢复后照常读取,按天去重)、从没迁过且密钥链用不了(信息级,说明仍在文件里,只提示一次);顺利迁移、Windows 常态都不打扰。恢复路径仍是连接时在登录框重新输入。`ISecretProtector` 加默认实现的 `TryUnprotect`(解没解开如实说);`GistSyncService` 的令牌 / 同步口令解不开时停下并报「请重新填写」(新键 `SyncSvc_PassphraseDecryptFailed`;令牌那条 `SyncSvc_TokenDecryptFailed` 以前从来触发不了)—— 原先拿 `enc1:` 密文本身当端到端口令加密上传,远端那份会变成别的设备都解不开的东西,多把密钥并存后「令牌重填了、口令还是旧密钥的」这种组合真的会出现。插件机密与会话密码解不开仍按历史契约原样返回密文(改成 null 有被插件 / 编辑框用空值覆盖、真丢数据的风险)。插件信任库「无法读取」的警示(`PluginManager_TrustStoreBrokenWarning`,五语)不再点名 `~/.velashell/secret.key`,补上「或者系统密钥链这次没能打开」,劝先别重置。
- `AppSettings.MasterPasswordProtection` 不接线(那是第二步),注释改为第一步已做、主密码见设计文档,常规页的隐藏注释同步。第二步(Argon2id 派生密钥包裹本机密钥、解锁与锁定态、开启 / 关闭 / 修改 / 忘记、自动重连与后台任务不弹框、插件 `ISecretsApi` 的「已锁定」语义)的设计草稿交给 velashell-docs。

**验证**:`-warnaserror` 0 警告;按 CI 口径 `VelaShell.Infrastructure.Tests` 1005 通过 / 27 跳过、`VelaShell.Core.Tests` 1251 / 6 跳过、`VelaShell.Tests` 2861 / 9 跳过。新增 `MasterKeyResolverTests` 28 条(27 条本机真跑):Windows 上 DPAPI 用真的(新密钥以 DPAPI 密文落盘、32 字节明文旧文件改包、别的账户的 DPAPI 密文原样挪开并报告);迁移无损与幂等(连跑三次只写一次)、写进密钥链后 / 记清单后 / 删文件中途三处中断都在下次启动收敛(中转文件全零也不会被当成密钥)、密钥链拒写时文件逐字节不变、写了读不回不算迁移、清单写不进不删文件;迁移后密钥链不可用再恢复(新旧都能解、临时密钥并入、不可用后不再逐条去问)、换机条目不在、Windows 读到别的平台的清单、条目对不上编号不用也不动、清单损坏挪开保留 / 读不出不被覆盖;libsecret 不存在时回落、Secret Service 结构体布局自检(64 位 592 / 16 / 528 / 16 / 8,在 Windows 上也能算)、环境变量关闭时原因如实;`TryUnprotect` 区分解开 / 解不开。`NotificationSourcesTests` 新增 5 条、`GistSyncServiceTests` 新增 1 条(同步口令解不开时拒绝推送)。七处变异(只用第一把解密、`Put` 不读回核对、同步口令改回 `Unprotect`、清单只写最新编号、不收尾中转文件、清单没写上也删文件、不可用后接着问)全部变红。真密钥链往返 `SystemKeychain_RoundTrip` 要 `VELASHELL_KEYCHAIN_TESTS=1` 才跑,开发机是 Windows,没跑过;macOS / Linux 的 P/Invoke 只经 CI 编译与布局自检,没在真机上跑过。合入时 dev 上 §252 新加的 `Push_WithPassphrase_CarriesThemInsideTheCiphertext` 只替身了 `Unprotect`,同步口令改经 `TryUnprotect` 读取后拿到「解不开」—— 替身改配 `TryUnprotect`(f8acea9c)。

**文档**:velashell-docs 新增 `host/主密码保护设计`(第二步的设计草稿,§9 列着待定的决策点);`host/系统密钥链与sudo凭据填充可行性调研` 加 2026-10-25 实施勘误、`host/凭据管理器集成设计` §1 / §9、`host/settings-audit` R-04、`host/架构设计` 规划中一节(zh / en);仓库内 `PRIVACY.md`(本地存储表与安全一节)、`manual.md` / `manual.en.md` 的 16.4、17.1、17.3、17.4 与排障表。

## 254. 2026-10-25 SSH 库其余透传的中文诊断补上结构化出处:主机密钥被拒、代理握手、跳板、转发、agent、SFTP 与说不清的拨号 / 断线

**来由**:`feature-plan.md` 🟡 P2「SSH 库仍透传的中文诊断(续)」。§246 之后盘点出的 9 类仍是库的中文原文,最常撞上的是建连时主机密钥被拒 —— 解析不了、类型与协商的不符、RSA 太短、签名验不过都是 `HostKeyRejected`,而 `SshInterop.Localize` 认定这一类的消息都由 `VelaHostKeyPolicy` 用界面语言写好,照用了库的中文;另有代理握手的拒绝与超时(`Msg_ProxyConnectFailed` 的后半句是库原文,SOCKS 应答码、HTTP 状态行只在句子里)、跳板的「连不上跳板 X」、隧道 `LastError` 的「Reason:中文」、端口占用与 X11 / xauth、agent 不可用、SFTP 缺扩展的说明与「版本太低」「流水线已释放」(都显示成「SFTP 不可用」)、TCP 拨号与连接中断的 `Unknown`。做法同 §246:库里只加成员给出结构化的出处,宿主按种类说,不解析句子(`src/VelaShell.Ssh/AGENTS.md` 4.5)。

**库这一侧**(只加成员,现有成员的签名与语义不变;除下面 xauth 那一处清洗外,库的中文原文一字不变):
- 建连:`SshConnectException.FailureKind`(`SshConnectFailureKind`,26 种,放在 `Diagnostics/` —— 底座不依赖别的文件夹,所以参数一律是字符串与整数,`Via` / `Target` 是 `host:port` 文本)与 `Via` / `Target` / `HostKeyType` / `HostKeyAlgorithm` / `HostKeyBits` / `MinimumHostKeyBits` / `ProxyReplyCode` / `ProxyReply`。主机密钥 5 处:4 种库自己拒的(`HostKeyMalformed` / `HostKeyAlgorithmMismatch` / `HostKeyTooShort` / `HostKeySignatureInvalid`,发生在问策略之前)+ `HostKeyPolicyRejected`(消息就是策略随裁决给的原因)/ `HostKeyPolicyRejectedWithoutReason`(`default` 裁决)。代理:SOCKS5 的版本字节 / 方法字节 / 地址类型 / 应答码,HTTP 的状态码与状态行,握手超时与中途断开,连不上代理,主机名放不进请求,凭据超长,ProxyCommand 的退出码与 stderr;`ProxyDialing.Refused` / `Misconfigured` 的种类改成必填参数(编译期拦住漏归类),`DialHops.Rewrap` 只补跳信息时把原来的种类与参数原样带过去。跳板 5 种(连不上、建连超时、不肯转发、转发时出问题、转发超时)。TCP 拨号认不出的套接字错误原因码照旧 `Unknown`,另记 `TcpConnectFailed` 与目标。没有新造异常子类 —— 测试与调用方大量 `ThrowsExactly<SshConnectException>`,换类型等于改语义。
- 转发:`SshForwardException.FailureKind`(`SshForwardFailureKind`,22 种)与 `Endpoint` / `Port` / `Limit` / `Timeout` / `DisplayName` / `ExitCode` / `ErrorOutput` / `ConfigLine`,29 处逐一归类(含只回给服务端的几种拒绝与本端停止 —— 界面上见不到,也照样配了文案,好让「每个种类都有文案」的守卫不留例外)。`ForwardErrorEventArgs` 加 `Target` / `Timeout` / `Limit` / `RejectedCount`,没带异常的那几种有参数可说;本地转发到套接字时事件里的目标不再带中文前缀(消息不变)。〔决策〕xauth 的 stderr 进消息前改按 `PeerText.SanitizeTail` 清洗、换行收成一行(曾经只 `Trim`),普通的一行输出前后一字不变 —— §246 对主机证书的名字做过同样的处理。
- agent:`SshAgentException.FailureKind`(`SshAgentFailureKind`,13 种:没在跑、连不上、管道属主读不到 / 不可信、问答错位、应答前断开、通信失败、应答不合协议、签名算法不符、拒签、拒绝加钥、拒绝清空、请求超出上限……)与 `Endpoint` / `PipeOwner` / `RequestedAlgorithm` / `SignedAlgorithm` / `HasDestinationConstraints`,19 处 + 应答格式不对的帮手逐一归类。
- SFTP:`SftpUnavailableException.FailureKind`(`SftpUnavailableKind`,6 种)与 `ServerVersion`,7 处;「流水线已释放」原因码照旧 `Unsupported`(公开构造的语义不变),种类 `SessionClosed` 说清是本端收工。`SftpException.MissingExtensions`:服务端没宣告扩展时本库自己报的 7 处 `OperationUnsupported` 带上缺的扩展名。
- 四个新枚举的零值都是 `Unknown` = 没归类,库自己不产生;成员顺序在合入前调整过一次(`TcpConnectFailed` 放在最前),枚举没发布过,不涉及兼容。

**宿主这一侧**:`SshInterop.Localize` 在 §246 那几层之后依次按 `SshConnectException` / `SshForwardException` / `SshAgentException` 的种类说。策略拒的主机密钥照旧原样用 `VelaHostKeyPolicy` 写的那句(不加尾巴,与原来一样),库自己拒的按种类说并带 `[原因 @ 阶段]`,异常类型照旧 `VelaSshHostKeyRejectedException`(自动重连据此停下)。连不上跳板 / 代理、跳板不肯转发时转述那一跳自己的完整译法(认证失败带尝试记录、协商失败带双方名单、通道失败按种类),外层不再叠尾巴;重协商时验签失败按内层的种类说;会话因意外异常判死(`SshConnectionClosedException`,`Unknown`)说内层的类型与消息 —— 异常类型 + 原因码 + 内层本身就是结构化的出处,没加库成员。`ProxyTransportDialer` 的「代理连接失败」后半句改按种类说(SOCKS5 应答码 1–8 按 RFC 1928 §6 的含义各有一句,其余说「未知错误」并带数字);它在 HTTP + 22 端口时追加的那句写死的英文提示没动,库消息里 403 / 405 / 501 的「只放行 80/443」建议也没搬进种类文案,免得同一条提示出现两次;代理握手超时被它改写成 `ProxyRefused` 是既有行为,同样没动。`LibraryPortForwardHandle.OnError` 没带异常的转发错误改由 `SshInterop.LocalizeForwardError` 说;端口占用按内层套接字错误码说「被占」;SFTP 缺扩展时在「不支持这个操作」后面说出是哪一个,覆盖式改名(posix-rename)另带「先删再改名不是原子的」;版本太低 / 过高、本端已收工各说各的。对端或配置里来的名字(主机密钥类型、HTTP 状态行、地址、SOCKS 目标名、xauth 输出、agent 回的算法名)一律清洗后才进文案。91 个新键,五份 resx 齐全,都插在同前缀的相关键旁边。`src/VelaShell.Ssh/AGENTS.md` 4.8 划掉这一轮的几类,列出还剩的。

**守卫**:`StructuredFailureCoverageTests` 的扫描加上 `new SshForwardException(` / `new SshAgentException(` / `new SftpUnavailableException(`,以及参数里提到 `HostKeyRejected` / `HostKeyChanged` / `ProxyRefused` / `SshFailureReason.Unknown` 的 `new SshConnectException(`、带 `OperationUnsupported` 的 `new SftpException(`(要带 `MissingExtensions`);宿主 `SshInteropDiagnosticsTests` 逐个枚举值核对映射、五份 resx 与占位符,英文界面下每一种文案(含经 `Translate` 的建连失败)都不许出现汉字。

**验证**:原有用例里改成逐字核对库的原文(主机密钥签名验不过、策略没给原因、SOCKS5 拒绝 / 版本不符 / 方法谈不拢 / 地址类型 / 中途断开、HTTP 403 与非 HTTP 应答、代理握手超时、跳板建连超时 / 不肯转发 / 连不上、转发的隧道超时与并发上限、SOCKS 握手超时、agent 签名算法不符、SFTP 版本太低、TCP 冷门错误),再加种类与参数的断言;库的每一条删除行逐条核对,只有收尾的 `)` / `;` 变了(xauth 那一处除外)—— 没有像 §246 那样撤掉库再跑旧实现。临时去掉一处库的归类(`HostKeyTooShort`),扫描与 `RSA主机证书里的钥也受长度下限约束` 红 2 条;去掉 X11 的 `NoX11Display` 归类,扫描报出 `Forwarding\X11Forwarder.cs:117`;临时去掉宿主的建连 / 转发 / agent / SFTP 映射,各红 5 / 2 / 2 / 2 条。91 个新键逐个确认在源码里有字面量引用。`-warnaserror` 0 警告;Debug、按 CI 口径:`VelaShell.Ssh.Tests` 1443 通过、`VelaShell.Core.Tests` 1272 / 6 跳过(Linux 专用)、`VelaShell.Infrastructure.Tests` 977 / 26 跳过(POSIX 专用与真实 WinSCP / Xshell)、`VelaShell.Tests` 2856 / 9 跳过。

**没做的**:配置不成立(`InvalidConfiguration`:ssh_config 的 ProxyJump 成环 / 跳数超限、ProxyCommand 模板与代入)、`TlsFailed`、`HostKeyStoreFailed`(宿主不走库的 known_hosts)、私钥 / 证书以外的 `Unsupported`、库自带主机密钥策略(`KnownHostsPolicy` / `PinnedFingerprintHostKeyPolicy`,宿主不用)写的拒绝理由 —— 宿主现在都碰不到或很少碰到,记在库 AGENTS 4.8 与 `feature-plan.md`。

**文档**:velashell-docs `ssh/spec/08-failures`(§一、§二、§5.2)、`03-key-exchange`(§5.3,顺手改正一处既有漂移:流程图里「`K_S` 的类型串与算法不匹配」走 `ProtocolError` 出口,实现一直报 `HostKeyRejected`)、`09-dialing`(§2.2、§3.4、§4.2、§五、§六)、`07-forwarding`(§五、§7.5.8、§八)、`04-authentication`(§4.5、§4.6 的 agent 两处)、`06-sftp`(§一、§5.4、§7.1)(zh / en);宿主手册与 `host/` 文档不用改(没有新设置项与界面元素,只是同样的报错换成了界面语言)。

## 255. 2026-10-25 诊断包:可选日志改为导出前勾选、通用的主机名 / IP / 用户目录遮盖、断开日志记连接起始时刻;发布标签不收构建元数据,「前往发布页」只放行本仓库的 releases 页

**诊断包的收录范围与遮盖口径**(`feature-plan.md` 🟡 P2「诊断包的收录范围与遮盖口径(#647 复审)」):§218 的诊断包收日志目录顶层的全部 `*.log`(§231 只把会话日志拿了出去),默认开着的传输记录、`remote-edit.log`、`vcxsrv.log` 里本机与远端的路径、文件名原样随包,传输记录的目录改到别处时反而收不到;主机与用户名只遮已知来源(已存连接、最近 500 条、共享凭据),连接改过之前的旧主机、异常消息里的主机、别人的家目录都不遮,PRIVACY.md 却说「遮盖主机名与用户名」;断开日志行的时长用 `Stopwatch`,机器睡眠的那段 Windows 与 macOS / Linux 口径不同;按插件的日志(§241)在子目录里不进包,而用户报「插件不工作」时它最有用。维护者 2026-10-25 定:默认只收主日志(`velashell-*.log`)与崩溃报告;传输记录、`remote-edit.log`、`vcxsrv.log`、插件日志在导出框里按项勾选、默认不勾;加通用主机名与 IP 遮盖,PRIVACY.md 的说法收窄到实际做到的;断开时长改记连接起始时刻。
- 收录(`Infrastructure/Diagnostics/DiagnosticBundle`):默认只收 `velashell-*.log` 与 `crash-*.txt`。新增 `DiagnosticLogKind` / `DiagnosticLogOption` 与 `FindOptionalLogs(日志目录, 传输记录目录)`,只列确有文件的可选项 —— 传输记录(按「文件传输 → 日志目录」的设置去找)、`remote-edit.log`、`vcxsrv.log`、`logs/plugins/` 按插件一项(`<id>.log` 与轮转的 `<id>.1.log` 归同一项);`DiagnosticBundleRequest` 加可选参数 `Optional`,勾上的进包(插件日志进 `logs/plugins/`)、照样过遮盖,README.txt 多一行 `optional logs: …`。默认两类与勾选的几类各有 32 MB 上限,互不挤占(包里原文最多约 64 MB);会话日志与不认识的文件一律不收;文件名再按前缀 / 后缀精确核一遍(Windows 上 `*.log` 会把 8.3 短名对得上的 `a.logx` 带进来)。插件清单与勾选的插件 id 交给脱敏器 `KeepNames`,不当主机名换。
- 界面:关于页「导出诊断包…」先弹勾选框 `Views/DiagnosticExportView`(视图模型 `DiagnosticExportViewModel`),外壳照 `SettingsTransferView`:460 宽、28px 标题栏(`file-down` 图标)、52px 按钮栏(取消 = `VelaOutlineButtonTheme`,导出… = `VelaAccentPillButtonTheme`)。顶部写明总会收什么,每一项是复选框 +「N 份 · 大小」+ 一行「它带着什么」,默认一个都不勾,一项都没有时换成「没有其它可收的日志」,一项不勾也能导出;确定后才弹另存为。找可选项在线程池上做(传输记录目录可能在网络盘)。五语新增 13 个键(`DiagBundle_Pick*` / `DiagBundle_Option*`),改写关于页提示、完成提示(不再说「敏感信息已遮盖」,改「已按规则遮盖」并提醒发送前检查)与 README 的两句。
- 遮盖(`DiagnosticRedactor`):已知来源照旧,另加四条通用规则,与已知来源共用 `<host-N>` / `<user-N>` 编号 ——①VelaShell 自己的断开日志行 `[SSH] 用户@主机:端口 closed`:格式是自己定的,两段都换,单段主机名(`old-db-3`)、域账户式用户名(`alice@corp`,取到最后一个 `@`)也认,`root` 等常见名与回环不换,新旧两种写法都认(日志目录里留着升级前 7 天的日志);②域名:全小写、至少两段、末段是常见顶级域或内网后缀(`local lan internal intranet corp home localdomain arpa test example`),刻意不收与文件扩展名撞车的(`sh md py rs pl pm so cc ps mo tf am ac mk pro zip mov`),插件 id、`com.` / `org.` / `net.` / `io.` 开头三段以上的倒写标识(包标识与 D-Bus 名)、VelaShell 自己联系的公共服务(`github.com`、`githubusercontent.com`、`easilynet.top`、`db-ip.com` 及子域)不换;大小写混写的(`System.Net`、`README.md`)一概不碰 —— 代价是大写书写的主机名与白名单外后缀(`.community`)认不出,`.ai` 收了所以 `logo.ai` 这类文件名会被当成主机;③用户目录:`X:\Users\名`(名字可带空格,须紧跟下一级分隔符)、`/home/名`、`/var/home/名`、`/Users/名` 只换名字那一段,公共 / 默认配置目录、`%USERNAME%` / `$USER` 一类占位、常见登录名不换,网址与相对路径里的 `/home/` 不算;④IPv4 按上下文分辨版本号:前后紧挨字母、数字、下划线或句点(`v1.2.3.4`、`VelaShell_1.7.3.0_x64`、`1.2.3.4rc`)、多出一段(`1.2.3.4.5`)、跟在 `Version=` / `version:` / `VelaShell ` 之后的不算 IP;光秃秃的 `1.2.3.4` 分不出,宁可当 IP 遮(原先 `VelaShell_1.7.3.0_x64.msix` 会被当成 IP)。顺序:主目录 → 口令类 → 断开日志行 → 已知字面值 → 邮箱 → 用户目录 → 域名 → IPv4 → IPv6。单段主机名(不在已知来源也不在断开日志行里)、其它位置的用户名(如 IM 用户 id)、文件名与其余路径仍认不出,PRIVACY.md 照实写。
- 断开日志(`Infrastructure/Ssh/SshDisconnectLog`):`closed after 时长` 改为 `closed (connected since yyyy-MM-dd HH:mm:ss ±hh:mm)`,挂上连接那一刻取墙上时钟(`TimeProvider.GetLocalNow()`),与日志行自己的时间戳一减即墙上时长,各平台一致;为测试加了带 `TimeProvider` 的 `Watch` 重载。
- PRIVACY.md(中英):本机数据表的诊断日志一行改记「连上的时刻」,补一行传输记录 / 外部编辑日志 / VcXsrv 日志 / 插件日志及各自的清理口径(原先表里没有);诊断包一段改写为总会收什么、可选几类默认不勾、按规则遮哪些与哪些认不出。

**更新:#655 复审留待定的两项**(`feature-plan.md` 🟢 P3):①`release.yml` 的标签白名单放行 SemVer 的 `+meta`,而版本号原样进产物名,应用内更新器只收 `[A-Za-z0-9._-]` 组成的附件名,带 `+` 的那一版永远装不上;②更新日志窗口的「在 GitHub 上查看」与 Linux / 只读目录上的「前往发布页」(共用 `ReleaseNotesDialog._releaseUri`)打开清单里的 `releaseNotes.htmlUrl`,只看 `IsAllowedLink`(https 即放行)、不限本仓库。维护者 2026-10-25 定:流水线禁 `+meta`;「前往发布页」只放行本仓库的 releases 页。
- `release.yml`:`VERSION_TAG_PATTERN` 去掉 `(\+…)?` 段(旁边注释写明原因);`prepare-release` 先单独拦带 `+` 的标签,报清楚原因与改法(SemVer 里 `1.2.3+build.1` 是合法版本,只说「不是合法版本」会让人摸不着头脑),各平台任务仍用同一变量兜底。
- 新增 `Services/Update/ReleasePage`(`RepositoryUrl` / `ReleasesUrl` 常量,`Resolve(htmlUrl)` 与 `TryGetRepositoryReleasePage(url)`):判定按解析后的各部分做、不按字符串前缀 —— https、`IdnHost` 正好是 `github.com`、无用户信息、默认端口、无查询串与片段、规范化后的路径在 `/joesdu/VelaShell/releases` 之下(仓库名不分大小写)且每段只有字母数字与 `. _ -`(无点段、空段与百分号转义);通过的按核过的路径重拼成 `https://github.com/<路径>` 打开,缺失或不合格的退回本仓库的发布列表。`github.com.evil…`、`github.com@evil…`、`…/releases/../../别的仓库` 这类前缀对得上的写法都过不去。〔决策〕清单缺 htmlUrl 时按钮不再变灰,改为打开发布列表(缺失按不合格处理)。正文 Markdown 链接的 https 过滤不变。仓库地址只写一处:`App.axaml.cs` 构造 `GitHubReleaseSource` 与 `LinuxReleaseAsset.ReleasesUrl` 都取 `ReleasePage` 的常量。手册没写这两个按钮去哪儿,不用改。

**验证**:`DiagnosticBundleTests` 新增 8 条(默认只收两类;可选项列举 —— 传输记录按设置目录找、插件按 id 分组含轮转;勾上的进包且遮盖、没勾的不进;断开日志行两段;漏遮 —— 异常消息与代理地址里的域名、`.local` / `.internal` / `.cn`;误遮 —— 类型名、程序集名、撞扩展名的文件名、插件 id、倒写标识、D-Bus 名、公共服务、大写域名;用户目录;IPv4 与版本号的上下文;三条新规则在最坏输入上随长度线性),`SshDisconnectLogTests` 4 条改为起始时刻口径(含 −04:30 偏移)并新增「挂上后时钟拨快 9 小时再结束,仍记挂上那一刻、不读单调时间戳」;宿主 `SettingsDiagnosticsExportTests` 新增 1 条走视图模型,`DiagnosticExportViewUiTests` 5 条(默认不勾与选中顺序、标题栏规格、无可选项、确定 / 取消的结果、关闭键悬停),另用无头截图看过两种布局;`ReleasePageTests` 28 条(本仓库发布页 6 种写法照开,21 种不合格写法退回列表,退回地址自身能过判定),`ReleaseNotesDialogUiTests` 的发布页用例改为 6 行。临时去掉实现:域名规则 3 条红、用户目录 2 条、断开日志行 1 条、IPv4 退回旧上下文 2 条、默认收全部 `.log` 2 条、忽略勾选项 1 条、结束时才取时钟 1 条、`Resolve` 退回「https 就放行」16 条。拿本机真实日志目录(约 2 MB,含传输记录、外部编辑与插件日志)跑过候选规则,没有误遮的文件名与标识符。标签白名单在本机用 bash 与 pwsh 对照过放行、`+meta` 专门报错与其余非法标签三类;流水线本身要真发版才触发,没跑。全量:`VelaShell.Tests` 2892 通过 / 9 跳过、`VelaShell.Infrastructure.Tests` 986 / 26、`VelaShell.Core.Tests` 1251 / 6;DockerIntegration `SshDisconnectLogIntegrationTests` 真连 2222 通过(没有 `[SKIP]`),日志行为 `closed (connected since … +08:00)`;`-warnaserror` 0 警告。

**文档**:velashell-docs `host/交互与界面规格`(关于页「导出诊断包」一段、插件日志一段、更新日志窗口的两个按钮与发布契约)、`host/消息中心与资讯源`、`plugins/02-architecture`、`plugins/STATUS`(zh / en);仓库内 `PRIVACY.md`、`manual.md` / `manual.en.md` 的 13.4、15.13 与插件日志一段。

## 256. 2026-10-25 文件侧六处收尾:远程编辑的两处口径、远端文件流的异常翻译、导入备份不清空收藏、远端搜索的编码与换行名字、覆盖上传的临时文件权限、外部编辑副本的崩溃遗留

**远程编辑的两处口径**(文档核对时查出):①设置「编辑后自动上传」的说明写「关闭后完全不监视本地副本,改动一律不回传」,而右键「打开」(双击选了内置编辑器同理)走内置编辑器、不经 `RemoteEditSessionManager`,点「保存」照样上传;同组「双击文件时」的说明「三种方式都会把本地副本的每次保存自动传回服务器」在开关关着时同样不成立。②终端侧栏的文件浏览器开出来的外部编辑会话没人调 `CloseScopeAsync`(只有 SFTP / 双栏文件标签关闭时才收),watcher 挂到应用退出,之后每存一次都是一次注定失败的上传。
- 改动:两条说明按实际口径改写(五语),`AppSettings.AutoUploadOnEdit` 的注释同步。`MainWindowViewModel.CloseSftpForTab` 改为返回任务:面板驱逐照旧当场做,后半段 `CloseSidebarScopeAsync` 先 `RemoteEditSessionManager.CloseScopeAsync(会话)` 再拆 SFTP 通道(顺序与 SFTP 文档标签关闭相同,两步各自兜住)。标签关闭(`OnDocumentClosedAsync`)等它收完才断 SSH,改完立刻关标签的那一次保存赶得上传(〔决策〕SSH 断开因此可能被推迟,最多 2 分钟,与 SFTP 文档一致);断线驱逐(`OnTabDisconnected`)与静默移除不等 —— 连接已经没了,没传的改动照原口径留成草稿并提示位置。
- 验证:新增 `TerminalSidebarRemoteEditTests` 2 条(终端标签关掉时侧栏开出的外部编辑会话被收掉、改完立刻关标签的那一次保存在拆 SFTP 通道之前传上去、别的会话名下的不受影响;没有编辑会话时照旧拆 SFTP);临时把 `CloseScopeAsync` 换成空操作,第 1 条变红。

**续传下载与双栏中转的断线认不出**(§238 记的已知漏网):失败重试按 Core 的中立异常类型分临时 / 永久,包装器的方法都在 `GuardedAsync` 里经 `SshInterop.Translate` 翻译,唯独 `VelaSftpClientWrapper.OpenAsync` 交出去的是库的 `SftpFileStream` 原样 —— 续传下载、续传核实比对尾部、双栏中转里源端的流喂给 FTP / 插件协议目标、插件的流式读取都在调用方自己的循环里读它,断线时冒出库的 `SshConnectionClosedException` 一类,`TransferRetryPolicy` 按永久处理。(SFTP→SFTP 的中转已被目标端 `UploadAsync` 的 `GuardedAsync` 顺带翻译。)
- 改动:新增 `Infrastructure/Ssh/TranslatingSftpStream`,`OpenAsync`(含 Append)交出的流包一层:读写(含 `byte[]` 重载,否则基类会走同步 `Read`、被库报不支持)、冲刷、定位、长度 / 位置、关闭的失败经 `SshInterop.Translate` 翻成中立类型,`NotSupportedException`、`ObjectDisposedException`、调用方自己的取消原样抛;`CanSeek` 照转(续传核实依赖它)。`ISftpClientWrapper.OpenAsync` 的契约写明流上的失败同样是中立异常,`TransferRetryPolicy` 的「已知漏网」说明改掉。核对过 AI 下载与插件:`RemoteFsCapability` 只按 `NotSupportedException` / `PluginPermissionDeniedException` / `SourceChangedException` 分支,换类型只改了 `ErrorMessage` 里的原文;插件 RPC 的错误码唯一的差别是库内部超时(调用方没取消)从 `cancelled` 变成 `error`,与包装器其它方法早就是的口径一致。
- 验证:新增 `SftpStreamTranslationTests` 4 条(内存服务端读到一半从服务端一侧掐断,流上抛的不是 `VelaShell.Ssh.*` 类型、`TransferRetryPolicy.Classify` 不是 Permanent;库异常在两个 `ReadAsync` / 两个 `WriteAsync` / `FlushAsync` / `Seek` / `CopyToAsync` 上都翻成中立类型;关流报的中断带着 `DurableLength` 翻成 `VelaSftpTransferInterruptedException`、判为临时;调用方取消与 `NotSupportedException` 原样抛,库内部超时翻成 `VelaSshOperationTimeoutException`)。临时让 `OpenAsync` 交回原始流,第 1 条变红(漏出的是 `SshConnectionClosedException`,改动前它被判为永久)。

**导入 JSON 备份也会用旧备份清空本机收藏**:§239 已让 Gist 拉取区分「载荷没有收藏字段」与「收藏为空」;`SessionArchiveService` 的覆盖导入同样整条 `ReplaceSessionAsync`,而 `SessionProfile.DirectoryBookmarks` 有 `= []` 初值,目录收藏功能之前导出的备份读出来与「收藏为空」一模一样。
- 改动:`SessionArchiveJson` 读单条连接改用私有的 `SessionReadOptions`(复制 `Options`,外加 `TypeInfoResolver` 修饰器:反序列化 `SessionProfile` 前把收藏置空),修饰器只挂在读连接这一处(`Options` 还用来写文件、读别的段)。读出来仍为 null 就是文件没提这个字段(或手写成 null,〔决策〕同样按「没提」),候选上记 `SessionImportCandidate.HasDirectoryBookmarks = false` 并兜成空列表;CSV 没有这一列,恒为 false。`SessionImportPlanner` 把这些连接的落盘 Id 记进 `SessionImportWriteSet.KeepStoredBookmarks`,`SessionArchiveService.ImportAsync` 对它们走 `SaveSessionAsync`(仓储锁内保留库里现存的收藏),其余照旧 `ReplaceSessionAsync`。CSV 覆盖导入原先拿的是计划那一刻的快照,现在改为保留库里此刻的收藏。
- 验证:`SessionArchiveJsonTests.Read_TellsAMissingBookmarkFieldFromAnEmptyOne`(缺字段 / `[]` / 手写 `null` / 大小写不同的字段名)与 `SessionArchiveServiceTests.OverwriteImport_OfABackupWithoutBookmarks_KeepsTheLocalOnes_ButAnEmptyFieldClearsThem`(真 SonnetDB);临时让 `ImportAsync` 一律走 `ReplaceSessionAsync`,后者变红。

**远端搜索的两处边角**(§239 记下的):①结果路径按 UTF-8 解,没跟会话编码,GBK 服务器上的中文名显示成替换字符,点了只进得到所在目录;②名字里含换行会被拆成两条。先在 ssh-shells 靶机(Alpine BusyBox 1.37,`docker exec` 只读探测)核了:`find -print0`、`xargs -0`、`printf "…\0"`、`tr "\n\000" "\000\n"`、`find … -exec sh -c … {} +` 都可用,**`grep -Z` 不支持、`head` 没有 `-z`**;另查出 musl 的 `fnmatch` 在 UTF-8 locale 下,模式里有非 UTF-8 字节就一个都匹配不上,`LC_ALL=C` 下按字节匹配正常。
- 文件名编码:SFTP 文件名编码的解析规则从 Infrastructure 的 `SftpConnectOptions` 挪到 Core 的 `RemoteFileNameEncoding.Resolve`,远端搜索取 `ConnectionInfo.FileNameEncoding` 走同一条;UTF-8、空白、认不出时照旧按 UTF-8 解。exec 输出改走新增的 `ISshClientWrapper.StreamCommandRecordsAsync`(默认实现报不支持,`VelaSshClientWrapper` 实现):按分隔字节切原始字节交出,不解码、不认 `\r`(原来按行解码的 `StreamCommandAsync` 会把名字里的 `\r` 当行尾、超过 4096 字符切成续片),一条超过 64 KiB 还没等到分隔符的整条丢弃;两者共用开通道 / TERM / 异常翻译那一段。
- 〔决策〕非 UTF-8 会话的参数也按会话编码编:SSH 的 exec 请求只能是 UTF-8 字符串,目录、模式、文字原样传到 GBK 服务器上就是另一串字节,只解输出的话从中文目录起找、按中文名 / 内容找一律 0 项(静默错)。这三项编成会话编码的字节、写成全三位八进制转义(纯 ASCII),脚本开头 `printf` 还原 —— 转义串只由 `\ddd` 组成,当格式串用不会解释出任何转换;编不进会话编码的输入整次退回原样的 UTF-8 参数,而不是被换成 `?` 通配符。这种会话里脚本 `LC_ALL=C`、`find` / `grep` 按字节匹配,代价是 `?` 匹配一个字节而不是一个汉字、`-iname` / `grep -i` 只对 ASCII 不分大小写。
- 名字里的换行:不直接用 `find -print0`(后面的 `head` 只按换行数,sh 的 `read` 也读不了 `\0`)。改为 `find … -exec sh -c 批处理 {} +` 把路径当参数交给一个小 sh,它用 `[ -d ]` 判目录 / 文件、每条打成 `d\t路径\0` / `f\t路径\0`,再 `tr "\n\000" "\000\n"` 对调 —— 记录之间是换行、名字里的换行成了 `\0`,`head -n 上限+1` 数的就是记录条数,客户端按换行分记录、把 `\0` 换回换行。按内容找时名字里没有换行的照旧一批交给一次 `grep -l`,带换行的逐个 `grep -q`。批处理脚本是常量,作为第 4 个位置参数交给 `sh -c`,内容文字挪到第 5 个。回退:先在根目录自己身上试 `find "$1" -maxdepth 0 -exec sh -c : vela-probe {} +`,裁掉了 `-exec … +` 的极老 BusyBox 上走原来的按行输出(带换行的仍拆成两条,按内容找为 0 项);两路的记录格式相同。缺工具的判断补上 `head` 与 `tr`(仍打 `VELA-SEARCH-NO-FIND`),状态行文案改为「服务器上缺少 find、head 或 tr 命令(按内容搜索时还需要 grep)」(五语);`TryParseHit` 不再去掉行尾的 `\r`(`\r` 是名字的一部分)。
- 验证:Core `RemoteSearchEncodingTests` 7 条、`RemoteSearchServiceTests` 新增 GBK 会话解码 + 换行名字 1 条、`RemoteSearchTests` 按新的命令形状改、`RemoteSearchShellTests` 新增 2 条(名字里带换行与 `\r` —— 本机 sh,Windows 文件系统不许名字里有换行,记 `[SKIP]`;`find` 不认 `+` 时退回按行输出,在 Windows 的 Git sh 上真跑过);Infrastructure `CommandRecordPumpTests` 2 条;DockerIntegration `RemoteSearchDockerTests` 新增 3 条(ash / fish 登录 shell 下换行名字按名字 / 按内容 / 上限计数;GBK 会话下中文名显示正确、从 GBK 名字的目录起找得到)。另在 WSL Debian(dash + GNU find / grep / tr)上手跑过生成的命令。变异:探针强制走回退,两条换行 Docker 用例红;服务不传会话编码,GBK 的 Docker 用例与服务用例红。

**覆盖上传的临时文件权限**(「AI 传输安全提交的几处残留」的⑤):覆盖上传的临时文件按 0644(减 umask)新建,要到提交前(§233 的 `CarryOverReplacedModeAsync`)才收窄到原文件的权限位 —— 覆盖一个 0600 的文件时,写入期间同机的其他用户读得到新内容。
- 改动:`RemoteUploadOptions` 加 `CreateMode`(只取 0777),`VelaSftpClientWrapper.UploadAsync` 把它作为 OPEN 的 ATTRS 权限发出(服务端照常减 umask;文件已存在时不改模式;setuid / setgid / 粘滞位不在这里带 —— 属主还没对上)。`ISftpService.UploadStreamAsync` 加可选参数 `uint? createMode`(在 `resumeOffset` 与取消令牌之间;FTP 与插件协议没有权限位,忽略),路由 / 串行包装 / 实现照转。新增 `ISftpService.GetUploadCreateModeAsync`(默认 null,`SftpService` 实现、`RoutingRemoteFileService` 显式转发):与提交同一套 lstat 判断 —— 被替换的是普通文件就取它的 0777 那几位,是链接 / 目录或不存在为 null(按服务端默认),是个条目却没报模式按 0600。`RemoteFsCapability` 覆盖上传在开写之前问一次、交给 `UploadStreamAsync`;〔决策〕问不出来(没权限 stat、通道一时出错)只是没法提前收窄,按服务端默认继续,提交时照旧重新核对、补齐属主与全部 07777 位。这是宿主内部契约,不涉及 SDK。
- 验证:`SftpServiceTests` 新增 3 条(权限位 6 组:600 / 755 / 6755→755 / 1777→777 / 链接 → null / 不存在 → null;已存在却没报模式按 0600;`UploadStreamAsync` 把它交给客户端);`SftpCommitModeIntegrationTests` 新增真 OpenSSH(2222)用例:600 / 755 / 4755 三个文件,临时文件提交前 `stat` 分别是 600 / 755 / 755、提交后 600 / 755 / 4755;`RemoteTransferTests` 新增 2 条,原有 30 处替身调用补上 `Arg.Any<uint?>()`。变异:包装器忽略 `CreateMode`,OpenSSH 用例红(临时文件 644);AI 流程不传,`RemoteTransferTests` 新用例红。

**外部编辑副本的崩溃遗留**:`remote-edit/<8hex>/` 里「回传失败刻意保留的草稿」与「崩溃时还开着的会话」在磁盘上分不开(后者同样可能带着没传上去的改动),一个都不能按时间删。维护者 2026-10-25 定:编辑会话落状态标记(干净 / 有未传改动);干净的 7 天后清,有未传改动的在消息中心列出让用户处理(草稿永不自动删)。
- 标记:每个外部编辑会话在自己的子目录里写 `.vela-edit.json`(与被监视的副本不同名,写标记不触发回传):状态、文件名、远端路径、服务器名、连接配置 Id;干净时还记着「与远端一致的那一份」副本的长度与修改时间。打开(下载落地)与干净地重新下载时写干净;watcher 一看到保存先写脏(再排防抖,此刻崩溃这份就是草稿);一次上传成功且没有新的待传改动时写干净;收尾保留草稿时确认写脏。写不了标记只留痕,最坏是多列一份。`RemoteEditRequest` 加 `Server` / `ProfileId`,文件面板按 `QueueOrigin` / `ServerDisplayName` 填。
- 启动清理(`RemoteEditDrafts.SweepStaleClean`,挂在 App 启动时本来就有的后台清理任务上,与 `builtin-edit` 一处):只看 8 位小写十六进制的子目录,跳过重解析点与正开着的会话;标记说干净**且**副本的长度与修改时间对得上(或子目录里一个文件都没有)才算干净,目录及其内容 7 天没碰过就删。没有标记(旧版本留下的)、标记读不出、标记是脏的、副本对不上干净标记(watcher 漏了一次保存、自动上传关着时改过)一律是草稿,永不自动删 —— 〔决策〕宁可多列一份,不能删掉一份改动。
- 消息中心:启动载入消息之后(`MainWindowViewModel.PublishRemoteEditDraftsAsync`,扫目录在线程池上)每份草稿投一条(`NotificationKind.System`、警示;id `remote-edit-draft:<子目录名>`,每次启动重投按 id 去重、保住已读;发布时间取第一次发现的时刻),正文说清哪个文件、哪台机器的哪个路径、本机副本在哪,没有标记的换一句「可能带着没传回服务器的改动」;动作「打开所在文件夹」= 新命令 `files.remoteEditDrafts.open`(只有一份时直接开它的子目录,否则开 `remote-edit` 根目录)。消息中心里还挂着、草稿却已经删掉的,撤掉那一条。运行中留下的草稿照旧当场在文件面板报位置,下次启动再列。五份 resx 各加 5 个键(`Notify_RemoteEditDraft*` 四个、`Cmd_OpenRemoteEditDrafts`)。「重新上传」没做:消息中心的动作只有一条不带参数的已注册命令(`NotificationLink.CommandId`),重传要先连上那台服务器再按这一份传 —— 标记里已记着连接配置 Id 与远端路径,记入 `feature-plan.md`。
- 验证:`RemoteEditDraftsTests` 4 条(真 `RemoteEditSession` 的标记随处境走;扫描只删「干净且 7 天没碰过」与空壳,带改动的、没有标记的、标记说干净副本却对不上的、正开着的、不是本程序起名的一个都不删,前三种列成草稿;一份草稿一条消息、草稿没了那一条撤掉;启动时投递与撤销)。变异:分类不比对副本、存档时不先标脏,各有用例红。`RemoteEditSessionManagerTests` 17 条、`BuiltInEditTempFilesTests`、`NotificationSourcesTests` 照过。

**验证(六项合起来)**:`-warnaserror` 0 警告;按 CI 口径 `VelaShell.Core.Tests` 1275 通过 / 7 跳过(多出的一条是 Windows 上早退的换行名字用例)、`VelaShell.Infrastructure.Tests` 982 / 26、`VelaShell.Tests` 2862 / 9;DockerIntegration 真跑 `RemoteSearchDockerTests` 9 条(ssh-shells 2223,含新加的换行名字 ×2、GBK 会话 ×1)与 `SftpCommitModeIntegrationTests` 2 条(OpenSSH 2222),全过、无 `[SKIP]`。合入时 dev 上启动时消息中心两路投递并存:§253 的本机加密密钥提示与这里的外部编辑草稿。

**文档**:velashell-docs `host/交互与界面规格` §6(远程编辑的两个设置项与会话结束时机、崩溃与草稿、目录收藏一条、文件名编码与远端搜索一节)、`host/settings-audit`(远程编辑一行、第十八批删掉「已知漏网」)、`host/凭据管理器集成设计` 仓储一段、`host/SFTP双栏与WinSCP差距分析` C2、`host/消息中心与资讯源` §4(新增「外部编辑草稿」一节)、`plugins/07-capability-apis`、`sdk/sdk-reference` §3.3b(覆盖上传的临时文件权限)(zh / en);仓库内 `manual.md` / `manual.en.md` 9.3、9.4、9.5、15.7、20.1。

## 257. 2026-10-25 AI 上传的四项口径:快照放在源文件同卷并先查空间、已打开的源按文件身份核验、停止宽限期 30 秒、断线终态写明理由(#656 复审 ②④⑤⑥)

**来由**:§231 复审 #656 时记下的「AI 上传的几项口径」里有四项交维护者定。维护者 2026-10-25 定:快照放在源文件同卷并先查空间;已打开文件改按文件身份比对(Windows 卷序列号 + 文件 id,Unix dev + inode);停止宽限期放宽到 30 秒;断线仍记失败(`session-disconnected`)。①要在 SDK 里加区分类型(§260 写了契约,待发版);⑦随诊断包的口径(§255)一起定。动手前逐项核了现状。

**⑤ 停止宽限期 30 秒**:取消、到期、断线、撤权之后,宿主最多等 5 秒让后端真正停下(§231);可 SFTP 上传被取消时,文件流关闭前要先排空已发出、还没确认的写入,再等 `CLOSE` 应答,SSH 库给这两步各 30 秒(`SftpOptions.CloseTimeout`),慢链路上排空一窗在途写入常要十几秒,5 秒一到就只能记「结果未知」、不敢清临时文件。`StopGracePeriod` 改成 30 秒,上传、下载与提交应答共用;宽限期只在停止之后才开始算,正常传输不受影响。连 `CLOSE` 也等满的链路仍按未确认记 Unknown,后端真停下之后照样清掉没提交过的临时文件。代价:点取消到卡片落定最长可能从 5 秒变成 30 秒(期间显示「取消中」)。

**⑥ 断线终态**:代码不改,口径写进注释与插件 README:断线记 `Failed` / `session-disconnected`,不记 `Cancelled`(#656 之前记的是取消)。`Cancelled` 表示「有人不要这次传输了」,调用方不该再发起;能落到断线这一档,说明写入已确认停下、提交没发出或被明确拒绝,目标确认没写入(`DestinationCommitConfirmedAbsent`),重连之后重传不会叠加副作用。提交已经发出、没等到应答时断线仍是 `Unknown` / `commit-unknown`,不得自动重传,也不清临时文件。

**④ 按文件身份核验已打开的源**:原先打开源之后拿句柄的最终路径(Windows `GetFinalPathNameByHandle`、Linux `/proc/self/fd`、macOS `F_GETPATH`)与审批路径逐字比,映射盘(查出来是 `\\server\share\…`,经本机 `net use Q: \\localhost\C$` 实测被拒)、subst 盘、同一文件的另一个硬链接名、macOS 大小写 / 规范化不敏感卷上的另一种写法都在启动时被拒。条目里的「8.3 短名误拒」证伪了:.NET 的 `Path.GetFullPath` 碰到带 `~` 的路径就展开成长名,旧代码也放行(`WindowsShortNameSourceIsTheSameFile` 照样留着钉住)。
- 改动:改比文件身份 —— Windows 比 `FILE_ID_INFO`(64 位卷序列号 + 128 位文件 id,取不到时两边一起退到 `BY_HANDLE_FILE_INFORMATION`),Linux 比 statx 的 dev major / minor 与 inode,macOS 比 fstat 的 st_dev 与 st_ino;查不出身份(id 为 0 或无效)一律拒绝,不退回字符串比较。拿来比的那一端必须不经任何链接走到(否则打开时经链接、核验时又经同一条链接,两边身份照样一致):Windows 逐级以遍历 + 读属性、共享读写不共享删除的方式钉住各级目录(与下载钉目录链同一做法),每一级与末级都不许是名字代理类重解析点;Linux 从 `/` 起逐级 `openat(O_PATH | O_NOFOLLOW | O_DIRECTORY)`,末级 `statx(AT_SYMLINK_NOFOLLOW)`;macOS 用 `fstatat(AT_SYMLINK_NOFOLLOW_ANY)`,不要求对各级目录有读权限(逐级 openat 会误拒只有 x 权限的祖先)。原先的 `F_GETPATH` 与 `/proc/self/fd` 路径核验随之删掉。代价:某级祖先的 DACL 不给当前用户遍历权限(只靠「跳过遍历检查」特权打得开文件)时会以「Cannot verify the upload source directory」拒绝;核验那几毫秒里别的进程正以 DELETE 权限开着某级祖先时会因共享冲突失败,重试即可。

**② 快照放在源文件所在的卷上**:原先 Linux / macOS 先把整份源文件拷进系统临时目录做快照,/tmp 是 tmpfs 时(Fedora、Arch、Debian 13……)几个 G 的快照直接吃内存与交换区,去掉 32 MiB 上限之后也没有替代保护,确认框也不显示大小。
- 改动:快照建在源文件所在的目录里,名字是隐藏的 `.vela-snapshot-<48 位十六进制>`,以不共享方式打开(Unix 上即 flock 排他锁)、建好就删掉名字,宿主崩溃时由内核回收。卷支持写时复制时直接克隆:Linux `ioctl(FICLONE)`(Btrfs、XFS、bcachefs 等),macOS 按源句柄 `fclonefileat`(APFS,`CLONE_NOFOLLOW | CLONE_NOOWNERCOPY`)。否则先查这个卷的剩余空间,不够「文件大小 + 64 MiB」(`SnapshotHeadroom`)就在拷贝之前以 `insufficient-space` 失败,消息写明需要多少、还剩多少,没传任何字节、目标确认没写入。〔决策〕源目录不可写(只读目录、别人的目录、只读挂载)时退到系统临时目录、同样先查空间,而不是拒绝 —— 否则从 `/etc` 这类地方上传都不行了;空间检查放在审批之后、拷贝之前(不实际试一次不知道卷支不支持克隆,审批前按大小拦会误拒 APFS / Btrfs 上本来能传的)。建快照之前清扫这个目录里崩溃遗留的快照,口径同 §233 下载临时文件的清扫(名字逐字对得上、拿得到排他锁的才删;同形的符号链接只删链接本身;FIFO 不碰)。快照与源句柄逐字节比对摘要,照旧。
- 确认框:「源文件」一行下面加一行「大小」,写约数加精确字节数(如 `5.0 GB (5,368,709,127 B)`);为此 `IPluginPermissionPrompt.RequestFileUploadAsync` 与 `PluginPermissionGate.CheckUploadAsync` 多了 `long sizeBytes`,五语 `PluginPermUpload_Preview` 加 `{3}`。Windows 生产上不做快照(持有拒写共享的源句柄),确认框同样显示大小。新错误码 `insufficient-space` 不是 SDK 类型,插件按普通 Failed 处理并显示那句原因,插件代码没改。

**验证**:每处改动先写出改动前会失败的用例,再临时撤掉实现确认变红 —— ⑤ `SlowCooperativeDrainWithinTheDefaultGraceIsAConfirmedStop`(生产默认宽限期下排空 6 秒的合作停写,上传与下载两行);⑥ `DisconnectWhileCommitIsOutstandingIsUnknownNotARetryableFailure`;④ 硬链接、链接祖先、subst 三条,外加 8.3 短名、映射盘(`WindowsMappedDriveSourceIsTheSameFile` 真的 `net use` 一个空闲盘符到 `\\localhost\<盘>$`,`/persistent:no`、用完删掉,映射不上时不确定)、大小写与 NFC / NFD 变体;② 余量边界两行、克隆不占空间(macOS 必过,其它系统不支持克隆时不确定)、源目录不可写退到临时目录(root 下不确定)、清扫、快照位置与删名、确认框大小,4 处变异各有用例变红。`-warnaserror` 0 警告;Windows 按 CI 口径 `VelaShell.Tests` 2835 通过 / 38 跳过、`VelaShell.Infrastructure.Tests` 990 / 28、`VelaShell.Core.Tests` 1252 / 6;WSL Debian 13(x64、uid 1000)上 TMPDIR 分别换成 /tmp(tmpfs)、/dev/shm(tmpfs)与 `$HOME`(ext4)各跑一遍 `RemoteTransferTests`,每轮 104 通过 / 9 跳过(只读目录退回那条真跑了)。macOS 的 `fstatat(AT_SYMLINK_NOFOLLOW_ANY)`、`fclonefileat` 与大小写 / 规范化用例只经 CI;Linux 的 FICLONE 没在真正支持 reflink 的文件系统上跑过(ext4 / tmpfs 上走的是拒绝之后的整份拷贝);没有真实 SFTP 服务器与人工 GUI 整链实测,也没验过从 OneDrive 同步盘上传。合入时外部编辑那组(§256)给 `UploadStreamAsync` 加了 `createMode`,AI 上传新用例的替身补上这一参数(290747fd)。

**文档**:velashell-docs `plugins/11-automation-and-ai` §1.6 / §1.7、`plugins/07-capability-apis` §2.1、`sdk/sdk-reference` §3.3b(zh / en);仓库内 `manual.md` / `manual.en.md` 的 AI 工具边界一段;`plugins/VelaShell.Plugin.Ai/README.md` 的 `upload_local_file` 一条。

## 258. 2026-10-25 配置审计带上改动来源、删分组补汇总事件;会话日志与录制遮盖:先剥控制序列再匹配、私钥块只认正文行、收窄关键词、补规则与「名字: 值」遮到行尾

**配置审计的两处口径**(§237 的取舍):配置类事件不区分来源,云同步拉下来改了 50 条配置就是 50 条看不出来历的 `profile-updated`;删除分组时仓储把组里的配置移回未分组是内部批量改写,不经 `SaveSessionAsync`,一条都不记。
- 来源作用域 `Core/Models/AuditSourceScope`:`AsyncLocal<string>`,`using (AuditSourceScope.Enter(AuditSources.CloudSync))` 罩住一段异步流程,`ConfigAudit.WriteAsync` 写记录时取当前值,没设时为「本机编辑」。〔决策〕不用 `GistSyncService.IsApplyingRemote` 那种静态开关:同步在后台应用载荷的同时用户在编辑框里手改的那一条不在同一条异步流程里,仍记成本机编辑(有用例钉住)。四种来源:`local` / `cloud-sync`(`ApplyRemoteAsync` 应用载荷那一段,含恢复历史版本;之后的 `ProfilesApplied` 通知不在作用域里)/ `import`(`SessionArchiveService.ImportAsync` 与 `SessionImportWriter`,即连接文件 JSON / CSV 与 Xshell、WinSCP、ssh_config)/ `login-prompt`(登录框「同时更新共享凭据」存回凭据那一次)。作用域内同步引发的订阅方若又存了一次(或顺手起了后台任务、之后再存),也记成这个来源(`AsyncLocal` 语义)。外部拉起落库、证书信任写回、连接成功写回等仍记「本机编辑」;设置文件导入也是 —— 它只改设置页里的值,落盘是用户点「保存」那一次。
- 存储:`AuditEntry.Source`,`audit_log` 加 `source` 标签。新库建表时声明;老库的 schema 里没有,SonnetDB 在第一次写入带这个标签的点时自动补列并随库持久化(实测 4.5.0-preview),在那之前 SELECT 点名未知列会报错 —— `QueryAsync` 先看 schema 有没有这一列,没有就不选、来源记 null。连接、安全两类与旧记录没有来源。
- 删除分组:`AuditedSessionRepository.DeleteGroupAsync` 删除前数一下组里还剩几条,删完记一条 `profiles-ungrouped`(类别连接配置,详情「分组「X」已删除,N 条连接配置移回未分组」,不带配置 Id),组里已空不记(会话树删分组会先逐条删组内连接,走到仓储时通常已空);读不出就按 0 条算,不让删除失败。
- 查看器(`AuditLogView`):「事件」与「会话」之间加 84 宽的「来源」一栏(窗口默认宽 1040 → 1120、最小宽 880 → 960),没有来源显示 —;关键字也搜来源;CSV 在「事件代码」后加「来源」列。五语新增 `AuditLog_ColSource`、`AuditLog_Source{Local,CloudSync,Import,LoginPrompt}`、`AuditLog_ActionProfilesUngrouped`、`AuditLog_DetailProfilesUngrouped`。
- 验证:`ConfigAuditTests` 新增 6 条(默认本机、嵌套作用域退回外层、并发流程不串、设置与共享凭据带来源、连接文件导入与 ssh_config 导入记「导入」、删分组一条汇总 / 空组不记),`SonnetDbPersistenceTests` 新增 2 条(来源往返;用加列前的表结构模拟老库:写入前能查、写入后补列、重开库仍在),`GistSyncServiceTests` 1 条(设置、分组、凭据、连接四处落盘都在云同步作用域里、通知不在),`AuditLogViewModelTests` 新增 1 条并改 1 条。变异:去掉写来源 / 云同步作用域 7 条红,去掉导入作用域、汇总事件、schema 检查 4 条红。登录框那一处在 `MainWindow.axaml.cs` 里,要弹真对话框,没有用例,只靠代码核对。headless 截了 960 与 1120 两个宽度核对「来源」一栏。

**会话日志与录制遮盖**(`feature-plan.md` 🟡 P2「会话日志与录制遮盖的几项取舍(#649 复审)」的 ①–⑥;`Services/SessionOutputMasker`):维护者 2026-10-25 定 ①④⑤⑥ —— 私钥模式只认 PEM 正文行、保留控制序列;收窄关键词匹配减少误遮;先剥控制序列再匹配并把偏移映射回原文(⑥随⑤一起)。②③ 按条目原文的推荐做。按 ②③ → ⑤⑥ → ① → ④ 的顺序做:① 的「保留控制序列」与 ④ 的成词判断都要在剥掉控制序列之后的可见文本上做才干净。
- ② 补规则(按误报率逐条收窄,六条全收):名字以 `.key` 结尾(`tls.key`、`ca.key`;作值出现的 `ssl_certificate_key /etc/nginx/tls.key;` 不算)、含 `key-data` / `key_data`(kubeconfig `client-key-data`,`client-certificate-data` 不算)、`.dockerconfigjson` / `.dockercfg`;Docker `config.json` 的 `"auth"` 只认带双引号的名字 + 字符串值 —— 写在正则的条件分支里、配不上就不算匹配,不是配上再丢,否则 `"auth": {"password": …}` 里的口令会被整段跳过;私钥标记认 `PGP PRIVATE KEY BLOCK`;前缀令牌加 `github_pat_`(正文 ≥ 22 个 `[A-Za-z0-9_]`);`.netrc` 只认小写 —— 一行写完的必须 `machine 主机` / `default` 起头且中间至少一个 `login` / `account` / `port`,分行写的只认行首单独一行 `password 口令`(上下文从行首起时才用,新增 `_contextAtLineStart`;值后面只能是空白与行尾,行没完先扣着),`[sudo] password for ops:`、帮助文本 `password  Set the account password` 之类不算。
- ③ `名字: 值`(冒号后跟空白,YAML、HTTP 头、`kubectl describe`)不加引号的值遮到行尾,停在同一行下一个键(标识符可带引号、紧跟 `=`,或紧跟「: 」)或 YAML 注释的 `#` 之前,紧挨着的逗号分号留给下一个键;`10:30`、`C:\x`、`http://` 不算键。`名字=值` 与冒号后无空白的(`host:~$` 提示符)照旧到空白为止。新值种类 `LineValue`(扣满 4 KB 吞的时候同一判据,末尾半个键留着拼下一段)与 `Word`(netrc 口令)。名字紧跟在 `@` 后面的不算(`用户@主机` 里的主机名),否则 `ops@vault-secrets-01: ~` 这类提示符会被扣着等回车。已知的漏:口令本身含「词: 词」(`correct horse: battery`)会停在 `horse:` 前。`token: Bearer abc…` 现在整段并成一个 `***`(原来 `*** ***`),既有用例的期望随之改了。
- ⑤⑥ 先剥控制序列再匹配:每一行先投影成「可见文本」(`Projection`)—— CSI 与两三字节的 ESC 序列不占位置;OSC / DCS / SOS / PM / APC 的开头与 ST 各留一个 ESC 作分界(标题、OSC 8 超链接里的内容照样过规则,但不会跟前后的文本连成一个值,值也不会吞进标题);坏的、超过 256 字节的序列 ESC 原样可见;段末没写完的序列不占位置、记下长度留着拼。全部规则(含私钥标记)跑在可见文本上,位置表二分换算回原文,正则里原先逐处容忍颜色序列的写法随之删掉。遮掉的那一段里夹着的控制序列原样接在 `***` 后面(引号串在收尾引号之前),回放时颜色、光标状态不乱。上下文截断不落在控制序列中间,改为记住截断点前的最后一个可见字符(`_contextPrev`),只给 Bearer 与令牌的开头看。⑥:转义过的 JSON 串(`\"…\"`)遇到裸引号就停 —— 外层 JSON 串里裸引号只能是外层的收尾;空值 `\"\"` 不再遮出一个反斜杠、也不吞下一个键。
- ① 多行私钥块(某行有 BEGIN 而没有 END)里的每一行先归类:base64 正文行(至少 4 个 base64 字符可带 `=`,或 OpenPGP 校验行 `=XXXX`,前面可有 `cat -n` / `nl` / `grep -n` / `grep -A` 加的行号与文件名前缀)只把正文换成 `***`,前缀、颜色序列、行尾照放;空行与 PEM / OpenPGP 头部行(`Proc-Type` `DEK-Info` `Version` `Comment` `Hash` `Charset` `MessageID`)照平常规则过、私钥块继续;END 那一行照旧;别的行说明这不是私钥块 —— 私钥块到此为止,这一行照平常处理。没写完的行只在还可能是私钥块里的行时才扣,提示符里一出现 `@ ~ $ # > % \ [ ]` 之类就当场放出。`echo` 一行 BEGIN 不再让后面的审计录像失明。单行里 BEGIN 之后的正文没改,所以 `$ echo '-----BEGIN RSA PRIVATE KEY-----'` 收尾的引号仍会被遮成 `***`(原有行为)。
- ④ 收窄关键词:名字里的关键词要成词 —— 紧跟在后面的若是同样大小写的字母(`max_tokens`、`tokenizers`、`passwordless`、`SECRETARY`)就不是这个词,驼峰的下一个词(`secretKey`、`authToken`)、数字、分隔符、名字结尾都算边界;复数 `s` 除 token 以外照认(`credentials.json`、`client_secrets.json`)。判断写在名字的原子组里:第一个关键词不成词就接着找同一个名字里的下一个(`tokens_secret` 照遮),整个名字不成立时这个位置不算匹配 —— 不先配上再丢弃,值里的真口令不会被一起跳过(`max_tokens: {"password": …}` 有用例)。冒号后无空白、值以 `~` 或 `/` 起头的不算(`secret-box:~#` 这类提示符、`host:/path`)。`Basic` 只认长度是 4 的倍数、解得开、解出来带冒号的 base64;`Bearer` 不认纯字母又不足 20 个字符的英文单词(`Bearer authentication`);还没写完的先扣着,写完再判。没做「保长度的遮盖」(条目给的另一条路)。成词之后仍会误遮的:`token_type: Bearer`、`token_endpoint: https://…`、`secretName: tls-cert`、`token_count: 12`。
- 验证:原有 108 条遮盖用例原样通过之后再加 —— 切分对照(每行一次写入、每个字节处切成两段、逐字节回显,结果逐字相同且不漏)② ③ 11 行、⑤⑥ 11 行、① 3 行;「不该遮的」② ③ 14 条、④ 12 条;「照样要遮的」、到行尾 / 扣着等结尾 / netrc 行首、看穿控制序列、没写完 / 坏序列、`Password:` 后跟 jq、`echo` BEGIN 不让录像失明、头部行照过规则等单项用例;线性用例补 7 + 5 种病态输入。变异逐条做,每一处都有用例变红(序列不剥 21 条红、遮掉段里不留序列 5 条红、去掉到行尾那一支 6 条红、去掉成词判断 6 条红……)。另做了一次不入库的随机切分对照:全部切分样本拼成一段再重复一遍,300 个种子 × 2 份语料、块长 1–63 随机,与一次写入逐字相同、无泄漏(④ 之后又跑了一遍)。「`Password:` 后跟 jq」那半条没能照原记录复现出漏遮(旧代码对单个上了色的字符串值本来遮得住),用例按 `read -p` 后打印多行 jq 与紧凑 jq 两种形态写。

**验证(合起来)**:`-warnaserror` 0 警告;`VelaShell.Tests` 全量 2917 通过 / 9 跳过(最后一个提交上);`VelaShell.Infrastructure.Tests` 986 / 26、`VelaShell.Core.Tests` 1251 / 6(第一个提交上,之后四个提交只动 `src/VelaShell` 的遮盖、测试与文档)。

**文档**:velashell-docs `host/交互与界面规格` §15 审计日志、`host/settings-audit` 第十四批「遮什么」(zh / en);仓库内 `PRIVACY.md` 审计日志一行、`manual.md` / `manual.en.md` 14.2、15.8、16.4。

## 259. 2026-10-25 SSH:系统密钥库认证、连接诊断的协议轨迹、按连接指定 agent 端点;ssh_config 的 X11 与按键时序混淆只认明写的值

**来由**:`feature-plan.md`「SSH 库已有、宿主还没接的能力」与「ssh_config 互通的剩余映射」两条。§161 在库里落地的 Windows CNG 签名器、报文旁路(`IPacketTap`)、按端点连 agent,宿主一处都没用上;§242 的 ssh_config 导入把指向非默认 agent 的 `IdentityAgent` / `ForwardAgent` 与 `ForwardX11`、§248 的 `ObscureKeystrokeTiming` 都留在「不导」。维护者 2026-10-25 定:CNG 签名器进密钥选择(「系统密钥库」一类)、报文旁路接进连接诊断的协议轨迹、按连接指定 agent 端点(连带 ssh_config 的 `IdentityAgent` / `ForwardAgent` 非默认套接字映射);`ForwardX11` / `ForwardX11Trusted` / `ObscureKeystrokeTiming` 只认明写的值(没写就按 VelaShell 自己的默认);TLS 拨号器暂缓;`AllowedHops` 先核实 Windows OpenSSH agent / Pageant 收不收目的地约束,核实前不接。SSH 库没改,只用它已公开的 `CngSshSigner`、`IPacketTap`、`SshAgentClient.ConnectAsync(endpoint)`、`AgentForwardOptions.AgentEndpoint`。

**系统密钥库认证**:
- 模型:`AuthMethod.KeyStore = 5`,`SessionProfile.KeyStoreKey` / `ConnectionInfo.KeyStoreKey`(`SystemKeyReference`:密钥库名 + 钥名 + 是否机器级 + 选钥时记下的公钥行,不可变 record,`Clone` 共享同一实例)。引用不是机密,明文落盘、随同步漫游;公钥只给界面显示与复制用,连接时以密钥库里实际的为准。
- 连接:`SshConnectionAssembler` 在线程池上 `CngSshSigner.Open(钥名, 密钥库, 机器级?)`,作 `PublicKeyCredential` 交给库(库先问服务端认不认再签,TPM / 智能卡的 PIN 由 Windows 在签名那一刻问),签名器连同其它读出的签名器在建连结束时释放。非 Windows 报 `SshErr_KeyStoreNotOnThisPlatform`;钥打不开由库报 `KeyStoreUnavailable`,宿主按既有的 `SshErr_KeyStoreUnavailable` 本地化,不去连服务器。不进「自动加载密钥到 Agent」(私钥导不出来)。
- 选钥:Core `ISystemKeyStore` / Infrastructure `SystemKeyStore` —— .NET 没有枚举 CNG 钥的接口,经 `ncrypt.dll` 的 `NCryptEnumKeys` 列软件密钥库、TPM(Platform Crypto Provider)、智能卡三个 KSP 的用户级与机器级钥名,一律带 `NCRYPT_SILENT_FLAG`(没插卡也不弹框);每把静默打开读公钥,打不开(没权限、要弹框)、不是 RSA / ECDSA 的不列。证书主题经 `CertGetCertificateContextProperty(CERT_KEY_PROV_INFO_PROP_ID)` 读当前用户「个人」存储里证书记着的密钥位置配上 ——〔决策〕不去取证书私钥:智能卡证书取私钥在卡没插时会弹框;旧式 CSP 里的证书私钥列不出来,库也打不开。
- 界面:连接配置认证方式第六段「系统密钥库」(只在 Windows 显示;配置本来就是它时别的平台也显示并说明这台电脑用不了;UniformGrid 不给隐藏的那段留位)。选中后:下拉(证书主题或钥名;第二行位置 · 类型位数 · 指纹 · 钥名)、刷新、「复制公钥」(贴进 authorized_keys);存过却找不到的那把照样列着、选中、标「这台电脑上找不到」,打开再保存不丢;一把都没有时一行怎么建钥的说明。没选钥不许保存;换到别的认证方式时引用不存。〔决策〕与 Tailscale 一样不挂共享凭据:选中即清掉凭据来源,共享凭据编辑框不把它列为可复制的来源、`IsCompatible` 判不兼容,解析器遇到带引用的(手改的配置)忽略引用。
- 其余入口:工作流 `ValidateProfile` 要求有引用(`Svc_KeyStoreKeyRequired`);连接诊断把有引用算作已存凭据、认证失败的建议同私钥;CSV 认证方式写 `keystore`,借 `private_key` 列写 `cng:密钥库/钥名`(机器级 `cng-machine:`),导回来还原、写成别的样子报错,CSV 覆盖已有连接时随私钥列一起更新;JSON 备份照原样往返(`"authMethod": "keyStore"`);ssh_config 导出写成一行 `# cng:… (VelaShell: …)` 注释而不是 IdentityFile。系统密钥库的钥这一轮不能配证书认证(库的 `SshCertificateSigner.Create` 收任意 `ISshSigner`,技术上可行)。

**连接诊断的协议轨迹**:
- Core `SshPacketTrace`:每个报文记时刻、哪一跳、方向、消息编号、长度、序号、通道号,**没有能装内容的字段**;只记前 1000 条,之后只计数。〔决策〕我们发出的认证报文(50–79)连长度也不记:口令认证的 `USERAUTH_REQUEST`、keyboard-interactive 的 `INFO_RESPONSE` 的长度减去固定的几段就是口令 / 应答的长度,而轨迹是要被复制、导出、贴进工单的;收到的认证报文(失败、成功、横幅、问题)照记。载荷本来就拿不到:不开 `AllowPacketTapPayload`,适配器从不碰 `Payload`。
- 挂法:`ConnectionInfo.PacketTrace` 只挂在最外层;装配那一层给链上每一跳的 `SshConnectionOptions.PacketTap` 一个各标各跳的适配器(`PacketTapFor`,没给轨迹时为 null,平常的连接收发循环上只多一次判空)。`IConnectionWorkflowService` 新增带轨迹的 `TestConnectionAsync(profile, trace, ct)` 重载,只有诊断的认证那一步用它;配置页的「测试」与平常连接不挂。`DiagnosticReport.PacketTrace` / `PacketTraceDropped`。
- 界面:诊断窗口右侧面板加 34 高的头栏,两页「诊断结果 / 协议轨迹 · N」(RadioButton 分段);轨迹页一行说明(只记元信息,发出的认证报文不显示长度)、等宽小字逐行(+时刻、→ 发 / ← 收、`KEXINIT (20)`、长度、`#序号`、通道),跳板链上换跳时上方写出那一跳;30–49 / 60–79 / 192+ 的编号写本地化的类别名,192 / 193 写 `PING` / `PONG`;右上角复制;没走到 SSH 连接时一句说明;超过上限说一声。导出报告末尾附同一段纯文本。

**按连接指定 agent 端点**:
- `SshSessionOptions.AgentEndpoint`(null = 系统默认),只填它也不算「空」,`Clone` 补上(§248 的 `Options_Clone_CopiesEveryWritableProperty` 盯着)。Core `SshAgentEndpoints`:存用户写的原样(去空白与一层引号),`~` 到连接时才展开(配置随同步换机器),命名管道前缀统一成库认的大小写;检查写法(非 Windows 写管道、管道没名字、不是完整路径、超过 `sun_path` 107 / macOS 103 字节)。
- 「SSH Agent」认证、agent 转发(`AgentForwardOptions.AgentEndpoint`)、「自动加载密钥到 Agent」都用它 —— 与 OpenSSH 一致,`IdentityAgent` 换掉的就是被转发的那个;跳板链每一跳各用各的。连不上时提示点名端点(`SshErr_AgentEndpointUnavailable`),「没在运行」不再叫人起 Windows 的 agent 服务(`SshErr_AgentEndpointNotRunning`),一把钥都没有也点名(`SshErr_AgentEndpointNoKeys`);转发开成那行灰字带上「· agent:端点」;「只转发指定密钥」的候选从那个 agent 里列(`ISshKeyService.ListAgentKeysAsync(endpoint)`,只在对话框第一次打开时按当时的端点列)。
- 界面:「SSH 选项」页(SSH 与 SFTP 都有)底部新一节「SSH Agent」→「Agent 端点」输入框,占位按平台给例子;〔决策〕框下的错误提示只提示不拦保存(配置可能是别的平台写的,与算法清单写错拦保存不同);填了算这一页有非默认值。
- ssh_config 导入(`SshConfigImportService` 原先「不导并提示」的分支):`IdentityAgent` 指默认 agent / `SSH_AUTH_SOCK` → 端点空;指别的、这台电脑上用得了的 → 记成端点(没有私钥与证书时按 SSH Agent 认证);`ForwardAgent yes` 转发 `IdentityAgent` 指的那个;`ForwardAgent <路径 / $变量>` 与认证用的是同一个、或认证不走 agent 时照收、端点取它;〔决策〕认证走 agent 而转发指了另一个 agent 时不导转发、说明一句 —— 一条连接只用一个 agent,换成认证用的那个等于多交出几把钥;别的平台的路径、没设的环境变量不导并说明。端点只在用得上(agent 认证或开了转发)时存。导出:agent 认证写 `IdentityAgent <端点>`,转发写 `ForwardAgent <端点>`(不写 yes),主目录下写 `~/…`。

**ssh_config 的 X11 与按键时序混淆**:`ForwardX11 yes/no`、`ForwardX11Trusted yes/no` 照映射,没写的按 VelaShell 默认(不转发;转发时受信任);只写了 `ForwardX11 yes` 时预览里说明「按 VelaShell 的默认导入为受信任(-Y),OpenSSH 这时是非受信任(-X)」—— 不悄悄改信任级别。`ObscureKeystrokeTiming yes` / `interval:N`(1–1000)开、`no` 关、没写关(OpenSSH 默认开,不照抄);节拍固定 20 毫秒,N 不是 20 时说明一句;认不出的值不导、说明一句。导出:X11 开着时信任级别一定明写;按键时序混淆开着才写 `yes` ——〔决策〕关着不写 `no`:这个关键字 OpenSSH 9.5 才有,老版本读到不认识的关键字整份配置报错,为一项没开的东西让每个 Host 块在老 ssh 上都用不了不划算(代价是这份配置在新版 ssh 上按 ssh 自己的默认开)。导出再导入两项回到原样。

**`AllowedHops` 的核实**:Win32-OpenSSH 的 agent 对认不得的约束扩展回 `SSH_AGENT_FAILURE`,并点名 `restrict-destination-v00@openssh.com`(PowerShell/openssh-portable#612,2023-04 合入);PuTTY 的 wishlist 条目 `pageant-forwarding-path` 仍是 outstanding(low / taxing),没找到 Pageant 实现 `session-bind@openssh.com` / `restrict-destination-v00@openssh.com` 的公开说法。本库有约束时一定发 25 号、被拒时报 `AgentRefused`、绝不去掉约束重试(§163),宿主接了只会让 Windows 用户加钥失败 —— 照决定不接。受益者主要是 Linux / macOS / WSL 上 OpenSSH ≥ 8.9 的 agent(CVE-2023-51384 提示 9.6 之前对 PKCS#11 钥的约束应用不完整)。

**验证**:`SystemKeyStoreTests`(软件密钥库里现建不可导出的 ECDSA 钥:列得出、公钥一致;环回测试服务端上用它登得上;钥不在时如实报;自签证书放进「个人」存储后列表带证书主题;用完删钥删证书)与 DockerIntegration `KeyStoreAuthentication_LogsInWithANonExportableCngKey`(当前用户软件密钥库里现建一把不可导出的 ECDSA P-256 钥,配置里只有引用,真 OpenSSH 上 `whoami` 通过,用完删钥、删 authorized_keys 那一行),与既有的 `AgentAuthentication_LogsInWithAKeyThatOnlyTheAgentHolds` 都在 ssh-shells(2223)上真跑;`SshPacketTraceTests`(环回服务端直连与经跳板,两跳都记、发出的认证请求没有长度;守卫:记录里除了跳名没有字符串 / 数组 / Memory 字段);`SshAgentEndpointTests`(Unix 域套接字上的假 agent,`SSH_AUTH_SOCK` 指向不存在的套接字照样登得上;连不上与没有钥时提示点名端点;写法检查两个平台);`SshConfigInteropTests` +5;视图模型、诊断服务、工作流、CSV、JSON、凭据解析与共享凭据各加用例。逐项临时撤掉实现(装配去掉 KeyStore 分支、视图模型不论认证方式都存引用、不挂旁路、不藏发出的认证报文长度、工作流不把轨迹挂上、装配 / 转发不传端点、`Clone` 漏抄、导入判同一个 agent 恒为假、导出转发写 `yes`、X11 没写时改按 OpenSSH 默认、去掉混淆映射),对应用例变红。`-warnaserror` 0 警告;按 CI 口径 `VelaShell.Core.Tests` 1284 / 6 跳过、`VelaShell.Infrastructure.Tests` 983 / 26、`VelaShell.Presentation.Tests` 96、`VelaShell.Tests` 2860 / 1 失败 / 9 跳过 —— 失败的 `LocalPathPickerKeyboardUiTests.EnteringAFilePath_OpensItsFolder_AndSelectsAndRevealsTheFile` 与本节无关(机器满载时 16 秒超时,单独重跑 1 秒通过;前一轮全量 2861 / 0 / 9)。headless 截图核过认证方式六段 / 五段、「SSH 选项」页底部的 Agent 端点、诊断窗口的两页。

**文档**:velashell-docs `host/会话导入`(§3.4 映射表、其余关键字、§四 导入说明、CSV 与导出、§八)、`host/交互与界面规格`(§13.1 认证方式与 SSH 选项、连接诊断窗口)(zh / en);仓库内 `manual.md` / `manual.en.md` 4.3、4.4、5.1、13.4、16.2。

## 260. 2026-10-25 SDK 契约:插件协议的文件能力补口、插件往消息中心发通知、共享的版本比较、传输「启动前被拒」的区分类型、RpcConnection 不写半帧(velashell-plugin-sdk 本地 dev,未发版)

**来由**:维护者 2026-10-25 定(第 30 条):在 velashell-plugin-sdk 把契约写好、不动版本号 —— 插件协议的文件能力补口(链接字段、`SetLastWriteTime`、摘要)、插件往消息中心发通知、共享的版本比较(`UpdateVersion` 抬进 SDK)、AI 上传「启动前被拒」的区分类型、`RpcConnection.WriteAsync` 取消落在写帧中途的半帧问题;维护者定版本号发版后宿主再跟上。在 velashell-plugin-sdk 的本地 `dev` 上完成(五个提交 0ab7fd6、a3c0fb8、e3c7fda、bd177ac、5d0acd2,接在 §261 的 4836e10 之后),**未推送、未发版**;本仓库没有代码改动。版本号一处没动(`VelaSdkVersion`、`VelaPluginApi.SdkVersion`、`Level`),XML 注释里该写版本的地方写「TBD(发版时替换)」,`grep -rn TBD src --include=*.cs` 一次找全。包发出之前,宿主与插件仓库编译不过新面是预期内的;没造本地包、没加本地源。

**文件能力补口**(0ab7fd6):`RemoteFileEntry` 新增两个 init 属性 `IsSymbolicLink`(按 lstat 判断;`IsDirectory`、`Size`、`LastModified` 描述链接目标,断链保留链接自身的属性)与 `LinkTarget`(readlink 原文,可以是相对路径),八参构造与 `Deconstruct` 不变;隔离进程的 `fs/list`、`fs/stat` JSON 自动带上,旧数据缺这两个字段读成「不是链接」。两个可选的兼实现接口(与 `IProtocolStreamUpload` 一样由协议实现类兼实现):`Protocols/IProtocolLastWriteTime.SetLastWriteTimeAsync(sessionId, path, DateTimeOffset, ct)`、`Protocols/IProtocolChecksum.ComputeChecksumsAsync(sessionId, RemoteChecksumAlgorithm, paths, ct)`(`RemoteChecksumAlgorithm`:Sha256 / Sha512 / Sha1 / Md5)。协议不实现就如实报不支持,实现了但这条会话做不到(服务器没有 MFMT、HASH)抛 `ProtocolUnsupportedException`;摘要只能用服务器自己给的,不许为凑一个值把整个文件读回来算,小写十六进制、键就是请求里的路径、单个文件算不出时值为 null。这两个接口只给 `inProcess` 的协议用(协议本来就只支持 `inProcess`),没加 RPC。插件协议上**创建**符号链接仍没有契约。

**插件往消息中心发通知**(a3c0fb8):`Ui/PluginNotification`(`Id` 本插件内稳定的去重键 1–128 字符、`Title` / `Body` 纯文本(宿主超过 200 / 1000 字截断)、`Level` Info / Warning / Critical 与宿主 `NotificationSeverity` 一一对应、`ActionCommandId` 只能是本插件的命令(必须以 `<pluginId>.` 开头)与 `ActionLabel`;`Validate(pluginId)` 宿主、PluginHost 与测试替身共用;〔决策〕故意没有网址字段,也没有「发送方」字段)、`PluginNotificationOutcome`(Posted / AlreadyPosted / Muted / RateLimited)。`IUiApi` 加两个带默认实现的成员 `SupportsNotifications => false` 与 `PostNotificationAsync`(默认抛 `NotSupportedException`),现有实现照旧编译;RPC `ui/notify`(`UiNotifyRequest` / `UiNotifyResponse`)与握手的 `SupportsNotifications`(缺这个字段为 false)。注释写明的约定:消息中心只收「要留存、可回看」的消息(进度、运行时状态、操作回执、调试信息不该往这里发);先去重、后限频,同一个 id 重投回 AlreadyPosted、不算频率;每个插件每分钟最多 6 条新消息(蓝图 07 §7),超出回 RateLimited 不抛;用户可按插件静音(Muted);消息跨重启保留,受消息中心总量上限约束,插件卸载时宿主删掉它发的,插件不能改写或撤回;动作命令最好写进清单的 `contributes.commands`,插件没激活时点击也能惰性激活。权限上不新增项:06 §1 的普通权限 `ui.contributions` 本就包含「通知(有频控)」,`notifications.system` 管的是系统通知。测试替身 `FakeUi` 有 `SupportsNotifications`、`NotificationRequests`、`PostedNotifications`、`NotificationOutcome` 脚本与 `PluginId`,校验与去重同宿主,`TestPluginContext` 把自己的 PluginId 同步给它。

**共享的版本比较**(e3c7fda):`Versioning/SemanticVersion`(`Major` / `Minor` / `Patch` / `Revision`(第四段,没有时为 0)、`PreRelease` / `BuildMetadata` 永不为 null;`TryParse` / `Parse`;`CompareTo` 是完整的 SemVer 2.0 §11 优先级 —— `beta.2 < beta.10`,纯数字标识符按长度再按序数比、任意长度不溢出,纯数字低于含字母的,含字母的按 ASCII 序数区分大小写,前面都相同时标识符多的更高,构建元数据保留但不参与比较;`CompareCoreTo` 只比数字段,给 minSdk / minHost 装载闸用;`TextComparer` 比版本字符串,解析不出的当最旧、同优先级按原文序数)。解析时数字段沿用宿主与 `plugin.json` 的宽松写法(前缀 v、两段或四段、前导零),后缀严格按 SemVer 语法。用例覆盖 SemVer 规范的示例链(两两都比)、宿主 `UpdateVersionTests` 的全部原有数据、解析边界、相等与哈希一致、`TextComparer` 的反对称性。核对时另查出第五套口径:宿主 `AnnouncementFeedDocument.TryParseVersion` / `MatchesVersionRange` 用 `System.Version`,丢掉预发布后缀。已知差异:第四段非 0 时现在参与比较(`UpdateVersion` 原来忽略);只有一段的 `"2"` 不再能解析(清单规则本来就不许);清单正则 `^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?$` 能过、`SemanticVersion` 判不合法的写法有 `1.0.0-beta.01`、`1.0.0-a..b`,清单校验这次没动。

**传输「启动前被拒」**(bd177ac):`PluginTransferRejectedException`(sealed,派生自 `InvalidOperationException`,故意不是 `IOException`)带 `RemoteTransferRejectReason`(SourceNotFound / SourceInUse / QuotaExceeded / InsufficientSpace),线上错误码 `RpcErrorCodes.TransferRejected = "transfer-rejected"`(格式 `transfer-rejected:source-in-use`,不认识或缺失的后缀还原成 Unknown),`FromException` 里排在 `InvalidOperationException` 之前。宿主抛它即保证:任务没登记、没生成 transferId、没读写任何内容、OperationId 也没记进账本 —— 条件变了之后可以再试(同一个 OperationId 不会撞「参数不符」),但不要紧接着自动重试;与「结果未知」(只能用同一个 OperationId 去重取回执)、「用户拒绝」(`PluginPermissionDeniedException`,重试无意义)分开处理。参数非法、平台不支持、会话不在、OperationId 参数冲突照旧用原来的异常类型。`IRemoteFsApi.StartUploadAsync` / `StartDownloadAsync` 补了 `<exception>` 说明。

**`RpcConnection` 不写半帧**(5d0acd2,公开 API 不变,改的是行为):调用方的取消令牌只管排队等写闸,拿到写闸之前取消则这一帧一个字节都不写;拿到之后流写入一律 `CancellationToken.None`,这时再取消,调用方立即收到 `OperationCanceledException`,帧在后台照样写完整、对端仍会执行这条请求(与执行操作「请求帧不随调用方取消」一致),后台写入的异常被处理掉。写到一半失败,整条连接作废:放开写闸之前置上坏帧标记(`_writeBroken`),排在后面的写入一个字节都不再写,然后在写闸之外失败全部在途请求、触发 `Disconnected`、释放连接;`NotifyAsync` 对单条通知仍是尽力而为,但不再带着半帧继续用这条连接。排队等写闸的调用也会响应连接关闭(`SemaphoreSlim.Dispose` 不会唤醒已经在等的异步调用,原来连接关了它们一直挂着),现在以 `ObjectDisposedException` 退出,`RequestAsync` 里表现为 `RpcDisconnectedException`。

**版本**:判断不需要抬 apiLevel —— 五项全是只增不改(新类型、带默认实现的接口成员、可选的兼实现接口、record 的 init 属性、RPC 方法名、握手字段、错误码,旧端缺字段都按保守值读;`RpcConnection` 是行为修正),按惯例是次版本级的新增,发 2.x 的哪个号由维护者定;用新面的插件要声明对应的 `minSdkVersion`。值得写进发版说明的两处行为变化:写坏半帧会作废整条连接;`RequestAsync` 在帧已开写之后被取消会立即返回,但请求仍会送达对端。宿主、CLI、商店与 AI 插件发版之后要跟的,逐项记在 `feature-plan.md` 对应的几行。

**验证**:在分支 `feat/sdk-contracts`(基于 fb0ae77)上 `dotnet test VelaShell.PluginSdk.slnx -c Debug` 205 个全过(原有 68,新增 137,DataRow 一行算一个),Debug 0 警告、`-c Release -p:SignAssembly=false` 也 0 警告;RPC 帧那组(`RpcFrameIntegrityTests`)连跑 10 次全过;把 `RpcConnection.cs` 临时换回旧实现,新用例 5 条里 3 条失败(写到一半时取消、写坏半帧、排队的写入被卡死),另 2 条是回归守护。并进本地 dev 之前 `git merge-tree` 验过能与 4836e10 干净合并。`.Build` 的 targets 与打包器没改,按 SDK 仓库的 AGENTS.md 不跑 `Invoke-Smoke.ps1`。

**文档**:velashell-docs 这次没改(`sdk/sdk-reference`、`plugins/05-ipc-protocol`、`plugins/07-capability-apis`、`plugins/06-permission-system`、`plugins/STATUS` 与 dev-guide 要随发版一起改,记在 `feature-plan.md`「三、文档待同步」)。

## 261. 2026-10-25 生态仓库的几处欠账:STATUS 与发版流程的漂移、模板仓库的过时说法、第一方插件的 minSdkVersion、SDK 测试替身的注册句柄(velashell-docs / templates / plugins / sdk 本地 dev,未推送)

**来由**:`feature-plan.md`「模板组顺带查出的生态侧几处」(§247 的「范围外查到、没改的」)与「三、文档待同步」里「文档组核对时查出的既有漂移」的 ①②。四个仓库各在本地 `dev` 上提交,**都没推送、没开 PR**,也没动任何版本号、本地包或本地源;本仓库没有代码改动。

**velashell-docs**(4281bce、8392301):`en/plugins/STATUS.md` 照 zh 补上「2026-08:协议能力域落地」一节(放在 V 之后),zh 那一节里 S3 设计的出处还是搬家前的 `docs/S3协议插件化设计.md`,两边都改成相对链接 `../host/…`。`zh|en/sdk/release-process.md` §3 的版本号落点对照 SDK 仓库 `scripts/Set-Version.ps1` 改准:它写四处 —— `Directory.Build.props`、`VelaPluginApi.cs`,以及 velashell-docs 的 `zh|en/sdk/sdk-reference.md`(按 `-DocsRoot` → `$env:VELASHELL_DOCS` → 同级 `../velashell-docs` 查找,找不到就跳过并警告),仿照模板那份补一句「可选落点」。`MongoDB工作台插件设计` / `mongodb-workbench-plugin-design` 的 minSdkVersion 2.0.2 → 2.0.4(配合下面的插件改动)。验证:四个文件仍是 CRLF;pwsh 跑全树 142 个 md 的相对链接检查,没有死链(报出的 2 处是正文代码里的 `](TX)`,误报)。

**velashell-plugin-templates**(29f46fb、2cb0a06):`VelaShell.Plugin.Templates.slnx` 删掉 `/docs/` 下已不存在的四项;`README.md` 的仓库分布表(SDK 仓库产出 `.Testing`、`.Build`,cli 仓库只产出 `vela-plugin`)、依赖方向(`sdk → templates`,cli 与本仓库之间没有依赖)与「什么时候动 VelaBuildPackageVersion」改指 SDK 仓库;`Directory.Build.props` 两段注释改准仓库归属、dev-guide 落点写成 velashell-docs 的 `zh|en/templates/dev-guide.md`;同一条过时说法顺带改了 `scripts/Set-Version.ps1` 的 `.EXAMPLE`、`.github/workflows/release.yml` 头部注释与 `AGENTS.md` 的版本表。只动注释与文字。验证:`dotnet build VelaShell.Plugin.Templates.slnx` 0 警告;`Set-Version.ps1 -Check -DocsRoot …` 全部落点已同步(模板 2.0.1、引用的 Build 包 2.0.9);没碰模板内容,没跑 `Invoke-Smoke.ps1`。

**velashell-plugins**(f9030ed):六个插件的 `plugin.json` `minSdkVersion` 统一钉到 2.0.4 —— Telnet / Redis / S3 原先没声明,串口 1.5.0、Docker 面板 2.0.0、MongoDB 2.0.2。依据:一个 `dotnet run` 单文件脚本读出每个插件 DLL 里指向 `VelaShell.PluginSdk` 的全部 TypeRef / MemberRef(含完整签名),逐个到本机 NuGet 缓存里各个 2.x 版 SDK 程序集中解析(缓存里没有 2.0.3);六个插件都从 2.0.4 起全部解析得了,2.0.0–2.0.2 缺的都是 `PluginIcon` 加 `*Descriptor.Icon` 或 `PanelOptions.Icon` 的 setter;再比对 2.0.4 与 2.1.2 的公开面,插件没用到 2.0.6 以后的任何东西。条目说「五个第一方插件」,实际是六个 —— MongoDB 经 `PanelOptions.Icon` 同样用了 `PluginIcon`、声明同样偏低,一并改了。`plugins/README.md`(「六个插件都要 ≥ 2.0.4」加两段历史缘由,页首「SDK 在 velashell-plugin-cli」改准)、DockerPanel / Mongo 的 README 与 Mongo 的清单用例随之改。验证:`dotnet build VelaShell.Plugins.slnx` 0 警告;六个测试工程全过(Telnet 26、DockerPanel 146、Serial 77、S3 104、Mongo 310 / 111 跳过、Redis 155 / 84 跳过,跳过的是按环境早退的真机服务器用例);`Set-Version.ps1 2.0.6 -Check` 通过。

**velashell-plugin-sdk**(4836e10):测试替身 `RecordingProtocols` / `RecordingWorkspaces` 的注册句柄原先按**实现实例**判断「是不是自己那一份」(`RecordingWorkspaces` 的条目还是值元组),「同一个实例重注册(换语言)、再 Dispose 旧句柄」在替身里会把新注册撤掉,在宿主里却是空操作。改为私有的 `sealed class Entry` 按引用身份判定、Dispose 时 `ReferenceEquals(current, entry)`,与宿主 `PluginProtocolRegistry` 的 `Unregister` / `UnregisterWorkspace` 同口径;〔决策〕刻意不用 record 或元组 —— 它们是值相等,同一个实例带同一份描述重注册时两条注册会被当成同一条(写进了注释)。新增 `RecordingRegistrationTests` 7 例(协议的文件系统与终端、工作台两边的「重注册再 Dispose 旧句柄」;描述一字不差地重注册;「被另一个实现替换后,旧句柄是空操作」防回归;成员很多的接口用 `DispatchProxy` 做占位实例,不新增包依赖)。验证:`dotnet test VelaShell.PluginSdk.slnx -c Debug` 75 / 75;把 src 的改动 stash 掉在旧替身上跑,5 例失败、2 例「被另一个实现替换」通过。这个修复要随下一版 SDK(`VelaShell.PluginSdk.Testing`)发出;不需要文档改动(`plugins/07-capability-apis` 早已写明宿主语义)。

**核对时查出、没改的**:velashell-docs 的 STATUS 中英漂移远不止那一节(en 表缺 zh 的 timeSeries / protocols / workspaces 三行,「激活事件」「发布形态」两行与质量基线、更新日期仍是旧的;反过来 zh 缺 en 的「2026-08: SDK productization」一节,zh 页首却说「均见文末各节」;两边页首「代码仓库对照」仍说 `.Build` 在 velashell-plugin-cli),`zh|en/sdk/release-process.md` 开头的「两个包」、§4 的 Avalonia 链路与第 3–4 步、`zh|en/cli/release-process.md` 的落点仍按 `.Build` 在 cli 仓库写(§3 写的 `AssemblyVersion` = `<主版本>.0.0.0` 是对的,2.0.0 – 2.1.2 各版包都是 2.0.0.0);`zh|en/sdk/sdk-reference.md` 的横幅还写着 SDK 2.0.2,SDK 仓库的 `VelaSdkVersion` / `VelaPluginApi.SdkVersion` 停在 2.0.9 而 tag 已到 2.1.2(2.1.0 – 2.1.2 发版前都没在本地跑 Set-Version);sdk-reference 的版本表把「打开已保存的会话」记在 2.0.2,而 2.0.1 的包里已经有 `ListSavedAsync` / `OpenAsync` / `CloseAsync`。模板仓库 README「想验未发布的 `.Build` 就到 sdk 仓库 `dotnet pack`」与 SDK 仓库 AGENTS 的「不要 pack 本地包给别的仓库用」相冲突(只改了仓库名),`.gitignore` 的 `local-packages/` 指向一个不存在的 `nuget.config`;velashell-plugins 的 `Directory.Packages.props` ItemGroup Label 还写着「源码在 velashell-plugin-sdk 与 velashell-plugin-cli」,AGENTS.md 里「`-p:VelaSdkVersion=<版本>-dev` 配 nuget.config 本地源」与 CPM 的字面量版本、SDK 的版本纪律都对不上。都记入 `feature-plan.md`。

**文档**:velashell-docs 的改动见上;本仓库不涉及。

## 262. 2026-10-25 布局模板随 Gist 同步、管理窗口导出 / 导入

**来由**:维护者 2026-10-25 定(第 31 条):路线图先排四项,这是其中一项,对应 `feature-plan.md` 二、B「布局模板的同步与导出」。§236 的命名模板只存在本机 SonnetDB 的 `layout_templates`,不进 Gist 同步,也不进设置导出。条目原文有三点要求:同步要像快捷命令那样单独成段、自带版本;先定好删除和重名冲突怎么合并;防住「旧客户端拉到不认识的段会忽略,再推一次就把它抹掉」(§158 的 `HiddenBuiltInIds` 就是这么丢的)。

**同步放在哪**:模板不放进主载荷 `velashell-sync.json`,而是在同一个 Gist 里并排放一个 `velashell-layouts.json`,格式是 `Core/Sync/LayoutTemplatesSyncEnvelope`(格式标识 `velashell-layout-templates-sync`、自己的 `schemaVersion`(现为 1)、设备名、时间,再加明文的 `data` 或加密的 `cipherText`;加密跟主载荷同一套,设了端到端口令就用 AES-GCM)。〔决策〕不放进主载荷:主载荷是整份推的,新段放在里面,旧客户端反序列化时丢掉、推回去就把云端的抹了;「推送前先合并远端已有的新段」防不住旧客户端(它根本不做这一步),何况墓碑一旦被抹,手里还留着旧副本的设备会把删掉的模板再推回来。旧客户端只读写主文件,而 GitHub「更新 Gist」只改请求里列出的文件、其余原样保留,所以旁边的文件它碰不到。新客户端推送时两个文件在同一次 PATCH 里写(`GistApiClient` 新增多文件的 Create / Update / GetFiles),版本历史里一个版本仍对应一台设备的一次推送;读取时两个文件一次 GET 取回,保证来自同一个 revision。推送前再 GET 一次模板文件、合并别的设备刚存的模板,开着模板同步时每次推送多一个请求。
- 读不了的时候:模板文件是更新版本写的,或加了密而本机没口令、口令不同 —— 只跳过模板这一份、既不读也不写,同步结果后面附一句说明,设置和连接照常同步;文件坏了(不是 JSON、格式标识不对、密文残缺)按「没有」处理,下次推送用合并结果重写,旧内容还在 Gist 的历史版本里。

**合并规则**(`Core/Layouts/LayoutTemplateMerge`,纯函数):不跟主载荷整份「较新者胜」走,而是按模板 Id 逐条比 —— 活着的模板比 `UpdatedAt`,删除比墓碑的 `DeletedAt`,时间晚的胜;同一时刻墓碑胜;两边都活着而时间相同算同一份、不比内容(比内容的话每次同步都要推一遍;两台设备在同一个 100 ns 刻度上各改同一个模板时不会收敛,只当理论情形)。合并结果与先后、次数无关,所以在三个时机各合并一次不会互相打架:推送前(先读云端那份合进本机再写,两台设备各存的模板都留下;「仅推送」也因此会把云端的合进本机)、拉取后、核对出主载荷两边一致时(补上升级前就有的模板、刚打开模板同步的设备、主载荷较新者胜那一轮输掉的本机模板改动;发现云端缺本机的改动就接着推一次,「仅拉取」不推)。删除:`LayoutTemplateStore.DeleteAsync` 先在 `layout_template_tombstones` 立墓碑再删模板;墓碑保留 180 天(`TombstoneRetention`,写死不做设置),过期后连同它压着的模板一起清掉 —— 代价是离线超过 180 天的设备回来时,它手里被删过的模板会重新出现。重名(不同 Id 同名)照条目原文的口径:不合并、不改名,两条都留(按名字合并会悄悄扔掉一套分屏;同步替用户改名,又会在两台设备上各改出一个名字、来回打架)。恢复历史版本时,那个版本里的模板全部写回、时间刷成现在,之后删过、改过的也回来,本机多出来的不动(与连接配置「恢复不删本机独有的」同一口径),随后那次推送里它们胜过云端现在的那份。

**向前兼容**:模板、节点、标签三层都加了 `[JsonExtensionData]`,更新版本加的字段原样落盘、原样同步和导出,不会被这一版拉下来再推一次就抹掉 —— 把同一个教训落实到字段这一层;`LayoutTemplatesViewModel.Copy` 带上了它。

**存储与接线**:`LayoutTemplateStore` 从宿主挪到 `Core/Layouts`(Infrastructure 的同步服务要用它),注册成 DI 单例,界面与 `GistSyncService` 共用一个实例;读改写在一把锁里完成,同步合并与界面上的改名同时落下时不互相盖掉。本机的增删改报 `Changed`(App 的自动同步据此标记改动、防抖推送,与快捷命令同一条路),同步写进来的报 `SyncApplied`(`LayoutTemplatesViewModel` 回到界面线程重读,不会又触发一次推送);`SaveAsync` 顺带撤掉同 Id 的墓碑(导入与恢复会让删过的 Id 回来)。`GistSyncService` 改成 partial,模板那一块全在 `GistSyncService.LayoutTemplates.cs`,主文件只在读 Gist、推送、拉取之后、恢复历史几处各挂一行(`ApplyRemoteAsync` 与 `BuildGistContentAsync` 一行没动)。

**同步开关**:设置 → 云同步 → 同步范围加「布局模板」(`SyncSettings.SyncLayoutTemplates`,当场接线,消费者是 `GistSyncService.LayoutTemplatesInScope` —— 推送、拉取、核对、恢复四处)。〔决策〕默认开,与应用设置、连接、代码片段三项一致:这不是新的外呼,走的是用户已经开着的那个 Gist;模板不含机密(内容只有配置 Id、shell 类型、标签标题),敏感程度不高于已经在同步的连接配置(标签标题常是 `用户@主机`,连接配置本身就带着主机和用户名);存量同步配置没有这个字段、读出来也是开,升级后第一次同步就会把已有的模板推上去(PRIVACY.md 已写明);关着的设备既不读也不写那个文件 —— 不写不等于删,别的设备的模板照样留着。「退出时的布局」不同步。

**导出 / 导入**:管理窗口底部左侧加「导出…」「导入…」(`file-down` / `file-up`,三级文字色),右侧仍是「将当前布局存为模板…」。文件格式(`Core/Layouts/LayoutTemplateFile`):导出头 `velaShellExport`(与设置导出同一个键;`format = velashell-layout-templates`、`version = 1`、时间、个数)加一个 `templates` 数组,每项就是库里那份模板,Id 跟着走(导到另一台开着同步的设备上仍是同一个模板);中文不转义,读取时容忍注释与尾逗号,文件不含机密;导出全部模板,不做勾选。读文件:格式对得上就尽力读,版本更高也照读、不认识的字段原样留着;不是 JSON、格式不对、没有 `templates` 数组的报「不是布局模板文件」;空名字、一个标签都剪不出来的项丢掉,全零的 Id 换成新的,文件里同 Id 重复的留较晚那份;超过 4 MB 的文件不读。冲突:与本机已有的模板同 Id 或重名(不分大小写)时问一次「替换 / 另存为新模板 / 取消」(一次问全部,不逐条问)—— 替换沿用本机那一条的 Id 与创建时间(与「存为模板」撞名时的替换一致)、换上文件里的名字与布局;另存给新 Id、名字补「 (2)」(`LayoutTemplate_CopyName`);冲突只按导入之前本机已有的模板判断,文件里自己重名的照收。写进来的模板修改时间一律是现在,要胜过本机删它时留下的墓碑与云端那份。导入完查一遍引用的配置,本机没有的提示「其中 N 个引用的连接本机没有,打开时会跳过那些标签页」,打开时照 §236 的口径跳过 —— 跨设备打开模板的前提是两边的配置 Id 一致(连接随云同步过来,或经连接文件 JSON 导入),手工新建的同名连接对不上,手册 8.4 写明了。

**验证**:新增 Core 23 条(`LayoutTemplateMergeTests` 12 条:两边各存的都留、各自取改得晚的那份、晚于模板的删除生效并留墓碑、删除之后又改过的会回来、同一时刻墓碑胜、重名不合并不改名、时间相同算同一份、与先后无关且合并两次无变化、过期墓碑清掉、两边都空什么都不做、云端坏条目被忽略;`LayoutTemplateFileTests` 11 条)、Infrastructure 15 条(`GistSyncLayoutTemplatesTests`,含**「新客户端推 → 旧客户端拉再推 → 新客户端拉」后模板还在**、旧客户端推完模板文件逐字节没变,**删除经旧客户端照样传到另一台、那台再推也不会把它加回来**;主载荷里不带模板;两边一致时不再写、没用过模板时不多出空文件;设了口令时是密文;没口令或口令不同时不读不写;更新版本写的不读不写;坏文件按没有处理;关掉开关不碰文件;恢复历史版本找回删过的模板;存量同步配置默认开)、宿主 13 条(`LayoutTemplateStoreTests` 新增 4 条走真 SonnetDB、`LayoutTemplatesViewModelTests` 9 条)。临时去掉实现 12 处,全部有用例变红 —— 其中把模板与主载荷放回同一个文件时 6 条红,含旧客户端那两条,说明单独一个文件确实是防住旧客户端的那一环;「未知字段经 SonnetDB 往返」那条第一轮在去掉 `JsonExtensionData` 时不红(它直接给 ExtensionData 赋值),改写成往库里直接写一份带新字段的原始 JSON 再经这一版读、改、存之后才红。`-warnaserror` 0 警告;`VelaShell.Tests` 2879 通过 / 9 跳过、`VelaShell.Core.Tests` 1282 / 6、`VelaShell.Infrastructure.Tests` 1000 / 26。headless 截图核过管理窗口底部;Gist 的真实多文件 PATCH 没有实测(`GistApiClient` 是静态类、没有注入点,用例跑在「文件名 → 内容」的模拟 Gist 上),依据是 GitHub REST 文档 *Update a gist* 的约定(没在这次编辑里显式改动的文件原样保留)。合入时 `MainWindowViewModel` 构造函数末尾 §253 与本节各加了一个可选参数(`IMasterKeyStatusSource?` 与 `LayoutTemplateStore?`),两个都保留;布局模板同步用例里的设备替身后来按 `TryUnprotect` 配同步口令(§253 改经 `TryUnprotect` 读取,372f0df9)。

**文档**:velashell-docs `host/交互与界面规格` §4C(管理窗口底部、导出 / 导入、存储、云同步)与 §14、`host/settings-audit`(第十六批改一句、新增布局模板随云同步一批)、`host/architecture`、`host/架构设计` §4.11(zh / en);仓库内 `manual.md` / `manual.en.md` 的 8.4、17.2、23.1,`PRIVACY.md` 本机存储表与云同步一条(中英两段)。

## 263. 2026-10-25 AI 插件:只读放行按参数判断、外部 MCP 审批每轮一份、#582 与 #616 复审遗留、停止或拒绝后撤掉排队的传输确认

**来由**:`feature-plan.md` 里几条核过仍成立、不用拍板的缺陷:#652 复审的 AI 插件安全边界 ①④(「只读放行」按首个词判断,`env sh -c …`、`curl -o ~/.ssh/authorized_keys …`、`sudo ip link …` 不弹确认就能写;IM 并发轮次共用外部 MCP 的审批挡位与回调),#582 复审 ④⑤⑥(当时只按代码推断),#616–#636 复审 ②,以及 AI 上传 ③(停止或拒绝之后,排队的上传确认框还一个个弹)。每条先补用例复现,再改。

**改动**:
- 只读放行(插件 `Agent/ReadOnlyCommand.cs`):`env` 移出名单(`printenv` 留着)、`sudo …` 一律照问、切词认空格与制表符。`curl` 改白名单参数:只认把显式 `http://` / `https://` 地址 GET / HEAD 到标准输出的那几个选项(`-s -S -L -I -i -v -k -f -4 -6 -g -N -q`、`-m` / `--connect-timeout` / `-A` / `-e` / `-r` / `--retry*`、`-X GET|HEAD` 等),写文件、发数据、换方法、自定义请求头(不认 `-H`)、读配置、gopher / dict / file / 无协议的地址都问;`wget` 默认落盘,只认 `-O -` / `-qO-` 与 `--spider`。〔决策〕收紧范围超出条目点名的几个:名单里另有十几个带参数就改状态的命令一并按参数判(以 root 登录时 `ip link set` 不带 sudo 同样绕过)—— `ip` 只认 show / list / get(缩写 `ip a a`、`ip r f`、`netns exec`、`-batch` 都问)、`route` 不带 add / del / flush、`ifconfig` 至多一个网卡名且只认列表开关、`mount` 只认无参与 `-l`、`hostname` 无位置参数、`date` 无 `-s`、`dmesg -c`、`journalctl --vacuum*` / `--rotate`、`ss -K`、`sar -o`、`rg --pre`、`file -C`、`bat cache` / `--pager`、`less +命令` 等都问;这些命令的参数里有引号、反斜杠、`$`、通配或花括号就不再猜 shell 会展开成什么,一律照问。代价是 `wget --output-document=-` 长写法被通用的 ` --output` 规则挡下。插件 README「只读自动」与手册 19.4 的措辞随之收紧。
- 外部 MCP 审批:`McpManager` 去掉可写的 `Approval` / `ApprovalHandler`,新增 `McpApproval`(回调 + 挡位),`GetToolsAsync(servers, approval, ct)` 由调用方每轮传一份、每个工具包装器捕获自己那一份;桥接每轮 `new McpApproval(…)`,面板持一份(`ApplyApprovalMode` 照旧当场改挡位)。改前第二个聊天一开轮次就改写共享属性,第一个聊天的 MCP 审批卡发到了别的群,对方是「全部自动」时干脆不问。README 的 MCP 小节顺带改准(所有 MCP 工具都走审批,`readOnlyHint` 只影响重试;原文写的「只读工具直接执行」与代码早不一致)。`McpManager` 的公共 API 因此变了。
- #582④:复现发现条目说的 `error` 事件(`ErrorContent`)已被 §223 的 `TransientFailure.TerminalFailure` 拦住,用例改前就绿;真正的缺陷在 `response.failed` —— M.E.AI 适配器把它转成一帧没有任何内容的更新(不给 ErrorContent、也不抛),先吐了半截正文再失败的流记成成功、解冻冷却,一个字没吐的被当成空回复重试、报「空回复」、服务端原话丢了。`TerminalFailure` 认出 `StreamingResponseFailedUpdate`,按服务端原话失败。
- #582⑤:自定义接入出厂首模型型号为空,拉模型时 `Materialise` 把它对齐成清单头一个,型号框与输入基线却仍是空的,再点保存 `ApplyForm` 把空串写回。`PullModelsAsync` 更新提交基线时,用户没碰过的型号框跟上新型号并同步输入基线(`FollowPulledModel`),改过的照旧当草稿;「保存后自动拉」与「点拉取模型」两条入口都覆盖。
- #582⑥:`BareModelId` 冒号前只剥已知的路由前缀(`RoutePrefixes = ["global"]`,不分大小写,与 `DeploymentLabels` 同一口径按见到的写法扩充),其余一律当完整型号 ——〔决策〕Ollama 的「型号:标签」开放词表(latest / instruct / mini / q4_K_M…)列不全,两种错的代价不对称:认不出的前缀只是配不上目录、退回端点窗口(界面提示核实),剥错了是悄悄借用别的型号的窗口与输出上限(以前 `phi4:latest` 剥成 `latest` 去配目录里别的型号)。
- #616②:复用的供应商目录窗口里「管理登录」(`ProviderSetupView.FocusEntry`)照 `StartNewProvider` 的守卫 —— 登录在跑、克隆 / 加 Key / 新建草稿没提交时只把窗口提到前面(记下之后点行按编辑处理),不掐登录、不丢草稿;要管的那一份(同一目录项、同一实例)已经摆着时不重建表单。
- AI 上传③(动了宿主):宿主 `RemoteFsCapability.Transfers.cs` 的上传快照与下载同口径报阶段 —— 回执与等确认时 `Approval`,批准后 `Transfer`,校验 `Verification`,提交 `Commit`(以前上传一律 `Unknown`,插件分不出「还没批准」);SDK 契约没变,只是宿主开始填这个字段。插件 `FileTransferRunScope.CancelUnapprovedAsync` 只撤 `Approval` 阶段、未请求取消的任务(宿主那边排在确认闸后的随取消撤下,已弹出的那个随取消关掉,以 Cancelled 收尾、什么都没写),登记标志让停止之后才到的回执一到就撤。触发:`CommandJobRunScope` 挂在入口令牌上(面板「停止」/ 换会话 / 关面板、IM 的 `/stop`、轮次超时、停用)与 `MarkExecutionDenied`(插件审批卡、sudo 认证取消);宿主确认框里拒了其中一个时,注册表在发布 permission-denied 快照时撤同一轮其余的(只对 `DenialEndsRun` 的轮次)。已批准的照常传完;正常答完不撤(入口令牌在收尾前释放登记),对外 MCP 连接里的单次拒绝不撤;模型发起的下载同样适用。

**验证**:每条新用例都在改动前或临时撤掉实现时确认会红 —— 只读放行的新用例(条目里的五种写法全覆盖,另有 40 余种绕过写法、90 余条按参数判定的正反例与一条走到审批回调的接线用例)在旧 `ReadOnlyCommand` 上 `AgentToolboxTests` 192 条里 85 条红;MCP 并发用例(插件自己的对外 MCP 服务当外部服务器,真 HTTP、真 MCP 客户端,两个聊天的模型各调一次它的工具,第一个挂住、第二个整轮跑完再放行)旧代码上 2 行全红;#582④ 撤掉识别 5 行红(面板 3、探活 2),`error` 事件那几行改前改后都绿;⑤ 旧代码 2 行红;⑥ 旧判据 4 行红;#616② 4 条全红;上传③ 让「等确认」判定恒假插件侧 4 条红、撤掉宿主阶段上报宿主侧 2 条红。全解决方案 `-warnaserror` 0 警告;AI 插件按 `scripts/ci/run-tests.sh` 分进程 1920 通过 / 0 失败;`VelaShell.Tests` 2857 / 9 跳过;`VelaShell.Infrastructure.Tests` 983 / 26。DockerIntegration、Interop、真实供应商与中转站、macOS / Linux 没跑。合入时手册 19.4「工具边界」一段与 §257 的改动合了一次(§257 的「确认框写明文件大小」在前、这里的「停止或拒绝后一并撤掉」接在后面,中英各一处);撤掉排队上传那条用例的确认框替身补上 §257 的 `sizeBytes`、SFTP 替身补上 §256 的 `createMode`(372f0df9)。

**合入后发现**(上传③,1a61038c):全量 CI 里 `Stop_WithdrawsTheRunsUploadsStillAwaitingHostApproval` 偶发超时;负载下(8 个进程并跑)16 次红 3 次,探针显示被撤的是 t1、t2、t3 —— 连已经批准的 t1 也撤了。原因:`FileTransferJobRegistry.CancelUnapprovedAsync(Job)` 按插件手里的快照判「还在等确认」,而宿主批准之后,观察者可能还没把 `Transfer` 阶段发布过来,插件手里的快照落后一拍,按它撤就把用户刚批准的上传一起撤了。修法:撤之前先 `GetTransferAsync` 向宿主取最新的快照,已经过了确认的只发布、不撤。新增 `Stop_KeepsAnUploadTheHostApprovedBeforeThePluginSawIt`(假宿主静默批准、插件手里的快照必然落后),撤掉修复即红;修后负载下 16 / 16 过。取最新快照与发出取消之间仍有一个往返的空当,彻底关上要宿主提供「只在还等确认时才撤」的条件取消(SDK 新契约),记在 `feature-plan.md`。

**文档**:插件 README(只读自动、MCP 审批、匹配规则、传输停止)、`manual.md` / `manual.en.md` 19.4;velashell-docs `plugins/协作接入`(只读放行、IM 的外部 MCP 审批按轮次、上传一节的停止与拒绝)、`plugins/11-automation-and-ai`(审批档位的只读规则摘要、`response.failed`、上传 §1.6)、`sdk/sdk-reference` §3.3b(宿主的上传快照也报 `Phase`)(zh / en)。

## 264. 2026-10-25 AI 插件与插件更新:维护者拍板的六项 —— 对外 MCP 对话档不给工具、网络检索默认关、插件更新强制 https 与摘要、#651 截断与空答、#616 的 store / 桥接模型 / root 确认延迟、Anthropic 清单请求对齐

**来由**:#652、#651、#616–#636 与 #657 复审留待定的几项,维护者 2026-10-25 按推荐定:Chat 模式不给工具(与界面说法一致);网络搜索默认关,并在手册与 PRIVACY.md 披露 VelaShell 运营的 SearXNG 实例;插件更新强制 https 与摘要校验;#651 有正文时照常完成并附截断 / 过滤提示,工具调用之后空的最终步骤补一句「请继续」重试一次、不记失败、不冷却,普通聊天的外层重试统一提示;#616 ①③④ —— 自定义 Responses 接入给可编辑的 `store` 开关、默认关(官方 openai 目录条目照旧),桥接指定的模型被删后拒绝并提示、不跨供应商回退,root 审批框的「批准」延迟约 0.6 秒才可点;Anthropic 模型清单那条请求的 `anthropic-version` 与鉴权头先对齐聊天请求,思考档位交给后续用真 Key 实测。

**改动**:
- 对外 MCP 的「对话」挡位:`McpToolHost` 在 Mode == Chat 时 `Tools = []`(连 `use_session` 也不给),`initialize` 的说明改由 `host.Instructions` 给出,对话档提示切到计划或 Agent;以前这一档与 Agent 一样注册全部工具。手册 19.6 补一句。
- 网络检索:`WebSearchOptions.Enabled` 默认 false(总闸同时管 `web_search` / `web_fetch` / 供应商原生检索)。〔决策〕存量不迁移:设置整份落盘,升级前存下的 `true` 分不清是用户勾的还是出厂值,设置页说明自功能上线起就写着查询会经过那台实例,一律改关会让一直在用的人突然搜不了;只有没存过这一项的配置拿新默认。设置页两句说明(五语)、插件 README、手册 19.1、`PRIVACY.md`(中英:概述一段、网络连接新增第 8 条、第三方补模型供应商)写明 `searxng.easilynet.top` 能看到什么(检索词与 IP)、看不到什么(对话、终端输出与标识符)。附带影响:对外 MCP 服务不读这一项、用的就是默认值,那条路上也不再给 web 工具。
- 插件更新:`HttpPluginMarketClient.DownloadAsync` —— 商店地址不是 https 连票据都不取;票据里下载地址非 https、`fileSha256` 缺失或不是 64 位十六进制,都在发下载请求之前拒绝(磁盘不留东西);以前 http 照下、没摘要就不核。接口注释、手册 18.x、`PRIVACY.md` 中英同步。前提是商店对所有已发布版本的下载票据都带 `fileSha256`:velashell-markets 从首个提交(2026-08-18)起 `PluginVersion.FileSha256` 就是必填,两条上传路径(`UploadEndpoints`)都在入库前算好,下载接口原样返回,所以经商店发布的版本都带着。
- #651 ①:有正文(且该步没有工具调用)的 length / content_filter / refusal 收尾照常完成 —— `RecoveringChatClient` 记 `AgentExecution.TerminationNotice`,面板纯对话模式自行判一步;面板挂一张警示色提示卡(新样式 `noticeCard`,`NoticeOutputLimit` / `NoticeFiltered`),桥接在回复后附一句;不报错、不换站、不切回原站,记健康成功。一字未吐的照旧报错、不重试;探活口径不变(严格)。②:工具调用之后最终一步空答,补一句英文「请继续」(`RecoveringChatClient.ContinueAfterTools`,只进那次请求、不进历史与界面)重试一次;还是空就照常收尾(`EmptyAfterTools`,面板提示卡 / 桥接回 `NoticeEmptyAfterTools`),不记失败、不冷却,空步骤不进历史。③:面板普通聊天的外层补试遇空回复改说 `RetryingEmptyReply`。五语新增 `NoticeTitle`、`NoticeOutputLimit`、`NoticeFiltered`、`NoticeEmptyAfterTools`;既有用例 `ToolCompleted_ThenEmptyStep_RetriesModelWithoutRepeatingTool` 按新口径改写(4 次请求 → 3 次,且断言「请继续」)。
- #616 ①:新增可空字段 `AiProvider.CustomResponsesStore`(null / false = 发 `store:false`,true = 不发);`EndpointQuirks.Of` 对目录 `custom-*`(新增 `ProviderCatalogEntry.UserDefinedEndpoint`)与无目录 id 的老配置按开关,具名目录项(官方 openai、Codex、Azure 等)照旧以目录为准。〔决策〕单开字段而不复用 `StoreResponses`:后者是建供应商时抄来的出厂值,存量自定义接入身上都是 true、从来不是用户选的,用 null 表示「没动过」,新默认对存量同样生效、不必迁移。设置页供应商一栏新增「允许服务端保存每一轮」(只对自定义接入显示,勾选当场落库并推进请求配置代次;五语);请求指纹、克隆、探活快照都带上这个字段;两条既有 Responses 工具循环用例显式开 store 保持原意。③:桥接指定的模型被删,`BridgeAgentRunner` 回 `BridgeModelMissing`(五语,「去协作接入里重选」),一个请求都不发;跟随面板的照旧。协作接入页的模型下拉对已删的指定模型仍显示成「跟随聊天面板」,此时点保存会静默改成跟随,这一轮没改。④:宿主 `MessageDialog.ShowCustomAsync` 加 `confirmDelay`(从 Opened 起算),`SudoPromptDialog.ApproveDelay = 600 ms`,root 确认框的「批准」先灰着,取消随时可点。
- Anthropic:`EndpointModelCatalog.AnthropicVersion` 改为 `2026-10-21`(与 SDK 13.x 的聊天请求一致,以前留在 2023-06-01);鉴权头本来就一致(Key 走 `x-api-key`、令牌走 Bearer)。思考档位未动。

**验证**:各项用例改前或撤掉实现时都会红 —— 对话档 1 条(instructions 叫外部 agent 去调 `list_sessions`、`tools/list` 非空)、插件更新 5 行(没摘要 / 空串 / 非 SHA-256 / http 地址 / http 商店)、#651 10 条、#616 插件 3 行与宿主 1 条、Anthropic 2 行(新用例拿真 SDK 聊天请求的头逐个比对清单请求 —— 版本、`x-api-key`、`Authorization`、`anthropic-beta`,Key 与令牌两种,SDK 再换版本号会红);网络检索补了默认值用例,4 条依赖旧默认的用例改为显式开启。数字同 §263(同一分支一起跑)。`store:false`、截断收尾与新版本号的清单接口只在本地桩上验过,没在真实中转站上验。合入时手册插件管理「入口与步骤」一段与 §255 的改动合了一次,两边都留。

**文档**:插件 README、`manual.md` / `manual.en.md` 19.1 / 19.4 / 19.6 / 18.x、`PRIVACY.md` 中英;velashell-docs `plugins/协作接入`(对外 MCP 的对话档与 web 工具、桥接模型被删、IM 回复的截断与空答)、`plugins/11-automation-and-ai`(恢复与重试、网络检索一小节、Responses `store`、Anthropic 清单请求)、`plugins/07-capability-apis` §3 与 `host/交互与界面规格`(root 确认框的延迟)、`plugins/10-packaging-and-distribution` 与 `plugins/STATUS`(应用内更新只走 https、要 `fileSha256`)(zh / en)。

## 265. 2026-10-25 终端输出规则:自定义高亮与触发器 / 自动应答共用一张规则表

**来由**:`feature-plan.md` 二、A 两行 —— P2「终端输出的自定义高亮规则」(语义高亮是 `SemanticMatcher` 编译期的 7 条 `[GeneratedRegex]`)与 P3「触发器 / 自动应答」(全仓没有「输出命中 → 自动发送」)。维护者 2026-10-25 定(第 31 条):路线图先排的四项里有这一项,高亮规则与触发器共用一张规则表。核对时两条现状都成立。

**规则表**(`Core/Models/OutputRule`,存在 `TerminalBehavior.OutputRules`,出厂为空 —— 默认没有任何触发器):名称、正则、区分大小写、启用、作用范围(`global` / `group` / `profile` + `ScopeId`)、动作(`highlight` / `respond`)。高亮只收调色板下标 0–15(前景 / 背景,null = 不改)与粗体,不收颜色字面量;应答是单行固定文本(≤ 128 字)+ 是否回车、是否转发同步输入频道(默认否)、冷却秒数(默认 2,1–3600)、每分钟上限(默认 6,1–30)。字段一律字符串 / 可空数值(文档库 JSON 不带枚举转换器)。〔决策〕放在终端这一块里:导出 / 导入与审计按「终端」分类走,云同步随应用设置走 —— 与快捷命令一样跟着人走,不必在 `SettingsCategories` 另登记一类(和所有新设置字段一样,旧客户端推一次会抹掉它不认识的字段)。`AppSettings.Normalize` 钳数值、丢空条目、超过 64 条截断,越界的调色板下标当「不改」;校验不过的规则不删,由编译跳过、设置页标出原因。

**编译与口令守卫**(`Core/OutputRules`):`OutputRuleCompiler` 一律以 `RegexOptions.NonBacktracking`(另挂 100 ms 匹配超时兜底)编译,按(正则, 大小写)缓存、所有标签共用;〔决策〕反向引用 / 环视 / 原子组 / 条件分支直接报「不支持」,不退回回溯引擎赌超时 —— 超时在渲染路径上同样是卡顿。正则 ≤ 512 字;空命中不算(`y*` 不会对每一行起反应)。`Build(rules, profileId, groupId)` 挑全局 + 这条连接 + 它所在分组的启用且校验通过的规则,按表序分成高亮与应答两组,带内容指纹。`OutputRuleSecretGuard`:应答规则的正则字面上提到口令一类词(password / passphrase / PIN / OTP / token / secret / 验证码 / 密码 / 口令,中日韩),或会命中 15 条常见口令提示原文之一(sudo、sudo-rs、ssh、git、私钥口令、PIN、中日韩 sudo),就不许设成自动应答 —— 拦得住 `:\s*$`、`.*`、`for alice` 这类不带关键词的写法(启发式、宁可多拦,换个更具体的正则即可);要输口令走共享密码(§212 / §234)。用户仍可能把口令填进一条普通提示的应答正文里,这拦不住,界面上写明了不要这样做。高亮规则不受限(把日志里的 password 标红是正当用法)。〔决策〕规则随设置同步,前提正是「表里不许有口令」:编辑框里命中口令提示的应答规则「完成」不可点,直接点窗口「保存」时这种草稿丢掉不写。

**高亮**:`VelaTerminalControl.OutputRules` 由 `TerminalSettingsApplier.Apply` 按标签的配置下发(本地终端只吃全局规则),内容指纹没变就什么都不做(每次保存设置、换主题都会重设一遍)。与内置语义高亮共用那张按行文本的缓存(值从 span 列表改成 `LineHighlights`),规则只在新到 / 变了的行上跑,帧间不变的行一条都不跑。合成:用户规则固定压在内置之上、逐项合成 —— 设了前景就用它,〔决策〕连程序显式设的 SGR 颜色一起盖(那是用户点名要标的字;内置语义高亮只染默认前景的字);没设前景的格内置语义着色照旧,URL / IP 的下划线照画;背景、粗体只改设了的项;选区、搜索、选区同文匹配的底色仍压在最上面。用户规则之间逐列先到先得,表里排前面的赢;优先级不做成可调。与内置一样只在主屏。插件自己建的终端视图走同一个 Apply,只吃全局高亮、不跑触发器;插件协议标签(经桥的串口 / Telnet)两样都有。

**触发器**(`Terminal/Semantics/OutputTriggerScanner`):
- 只对远端输出:控件新增 `FeedRemote`,`SshTerminalBridge` 的合批泵改走它;宿主写进终端的提示语、本地回显(`Emulator.Feed`)、录制回放仍走 `Feed`,不判。
- 〔决策〕只看主屏光标所在的逻辑行(自动换行接回):程序停下来等输入时,提示就在光标那一行;滚过去的输出 —— `cat` 一份恰好含 "Continue? [y/N]" 的文件 —— 光标早已离开,不答,否则会往 shell 里敲一个 `y` 回车。开销因此钉死在每批一行,与输出量无关;代价是「某条日志一出现就自动发命令」这种用法做不到(那更像 expect 脚本)。
- 本地在敲字时不答:自上次回车以来本地有过输入(键入、粘贴、本地回显、快捷命令、同步输入频道转来的字 —— 后者走新加的 `NoteForwardedInput`)就不判,回车后重新判。在 shell 提示符后敲 `echo "(yes/no)"` 的回显不是提问;用户已经开始手答一个提示时(哪怕只按了方向键)也不替他再答一遍。
- 等对端安静 300 ms 再答(新输出一到计时重来,到点重判):一批输出恰好断在一行中间时,光标会短暂停在一行「像提示」的字上。
- 同一逻辑行只答一次:`TerminalRow.TriggerSettled` 记在首行上,与 OSC 133 标记同一套生命周期,整行被擦空才作废(clear 之后同一行对象上的新提问照答);改列宽重排换了行对象,屏上有内容的行一律记成已处理(空行不记 —— 启动时布局那一次重排的是一整屏空行,记上了之后写在这些行上的提示就永远答不上;代价是一条正好在改列宽前一刻出现、还没答的提示,改完列宽就不再答)。
- 冷却与每分钟上限(按规则 Id 记账,规则集内容不变时不清):冷却里出现的新提示推迟到冷却结束再判,不丢;到了上限这一行不答,报一次「暂停应答」,一分钟窗口过去后恢复 —— 对端刷屏时不会被刷发回去(`rm -i` 这类连问很多次的,要用户自己把上限调高,最高 30)。
- 发送(`TerminalTabViewModel` 接 `ITerminalEmulator.OutputTriggerFired`):与快捷命令、重跑同一条程序注入路径(抽成 `SendProgrammatic`:按会话字符集编码、回车按终端模式编、标为程序注入不弹补全);默认不进同步输入频道,规则选了「转发」才转;没连着不发。终端上沿居中一枚轻提示「已自动应答:规则名」/「「规则名」已到每分钟上限,暂停自动应答」,4 秒消失,不接收指针。一行同时命中几条应答规则时只答表里最前面那条。

**设置页**:设置窗口新增「输出规则」页(导航「终端」之后,共 15 页;`SettingsSectionKey.OutputRules`)。顶部常驻安全说明(只用于 yes / 回车 / 固定文本,口令走共享凭据);规则列表(启用勾选、名称 + 动作胶囊 + 范围、正则、校验不过的原因,上移 / 下移 / 编辑 / 删除,最多 64 条);编辑框改副本(名称、正则 + 区分大小写、动作、作用范围 + 分组 / 连接下拉;高亮的前景 / 背景 =「不改」+ 当前配色方案 16 色色块(取设置窗口当前方案的 `Appearance.AnsiNormal/AnsiBright`,规则里只存下标)、加粗;应答的发送文本 / 再按回车 / 冷却 / 每分钟上限 / 转发同步通道),校验不过「完成」不可点,窗口「保存」时校验得过的草稿一并写回、校验不过的丢掉;「测试」粘贴一段输出,编辑框开着只测草稿、否则测全部启用的规则,按行列出命中段(带规则的颜色)与「自动应答 X → 发送 …」,最多 500 行。「恢复默认」会把规则表一起清空。没有新快捷键。五份 resx 76 个键(`SetVm_SectionOutputRules` + 75 个 `OutRule_*`)。

**不做的**:录制与日志的遮盖(§220 `SessionOutputMasker`)接这张表、让用户加自己的遮盖规则 —— 条目说可以,这一轮不做,记在 `feature-plan.md`;多步 expect 对话、变量、按标签 / 标记划作用范围都没做。

**验证**:新增 77 条(全部 `TestCategory("OutputRules")`,不早退):Core `OutputRuleCompilerTests` 32 条(`(a+)+$` 对 2 万字符 ×20 次线性完成、不支持的构造、写错报位置、只编一次、空命中、大小写、规则序、口令守卫的正反例、按全局 + 连接 + 分组挑、本地终端只吃全局、`Normalize` 钳制、设置 JSON 往返与旧设置载入为空表);Terminal `OutputTriggerTests` 17 条(真桥 + 假流端到端;光标行只答一次、滚过去的不答、`Feed` 不答、本地回显 / 回显用户输入 / 同步频道转来的字 / 用户抢先手答都不答、备用屏不答、批次断在行中间、冷却推迟不丢、每分钟上限只报一次且窗口过后恢复、改列宽不重答、清屏后同一行再问算新问题、折行提示按一行)与 `OutputHighlightTests` 7 条(含**帧间不变的行一条正则都不跑、新来一行只跑一行**);`OutputHighlightRenderTests` 1 条像素用例(规则选的调色板 1 号底色与同一行 `SGR 41` 那格同色,选区底色压在其上);宿主 `TerminalTabOutputTriggerTests` 7 条、`OutputRulesViewModelTests` 10 条、`OutputRulesPageUiTests` 2 条。逐条去掉实现(不看本地键入 4 条红、不去重 2 条、去上限 1 条、桥改回 `Feed`、高亮绕过缓存、改列宽不记已处理、去掉 `NonBacktracking` 2 条、去口令守卫 9 条、应答一律进同步频道)全部变红。`-warnaserror` 0 警告;`VelaShell.Tests` 2882 / 9 跳过、`VelaShell.Core.Tests` 1284 / 6、`VelaShell.Terminal.Tests` 638、`VelaShell.Terminal.RenderTests` 9、`VelaShell.Infrastructure.Tests` 977 / 26、`VelaShell.Presentation.Tests` 93、`VelaShell.Controls.Tests` 31。基准(`tests/VelaShell.Benchmarks/OutputRuleBenchmarks`,`--smoke`,本机满载、数字看量级):一屏 50 行全新日志行(刷屏时每帧最坏情况,全部缓存 miss),只有内置 7 条 ≈ 82–110 µs,加 8 / 32 / 64 条用户规则 ≈ 142 / 451 / 692 µs,无额外分配(60 fps 一帧 16.7 ms,最坏占约 4%;帧间不变的行用户规则零开销);触发器每批只判光标那一行,8 / 32 / 64 条 ≈ 0.5 / 3.3 / 9.9 µs。headless 截图核过设置页(高亮编辑 + 测试区着色、应答编辑 + 校验错误);真 SSH 会话里的自动应答(ssh 主机指纹提问、apt `[Y/n]`)只在 headless 里用真桥 + 假流、真终端控件验过,终端上方那枚提示没截图核对。

**文档**:velashell-docs `host/交互与界面规格`(§5.2 一条 + 新增 §5.2.3、§6.1 一句、§14 页数与表格一行 + 新增 §14.6)、`host/settings-audit`(新增一批、第十四批「不做的」一句、源码索引)(zh / en);仓库内 `manual.md` / `manual.en.md`(新增 7.7、15 章页数、15.3、23.1)、`src/VelaShell.Core/README.md`、`src/VelaShell.Terminal/README.md`。

## 266. 2026-10-25 AI 插件:供应商换源先确认再清 Key / 退出登录、跨源覆盖要自带 Key、Anthropic 环境变量只给官方主机;冷却按模型、空链默认不跨账号、空主槽能填回

**来由**:`feature-plan.md`「一 / 🧩 插件生态」里供应商配置与凭据那一块的五条(§177 / §217 / §230 / §233 留下的,都要改 #646 之前就有的代码):订阅供应商换源后旧凭据照旧发往新地址;Anthropic 协议没有 Key 时回落本机环境变量;跨源模型覆盖拿主槽 Key 的口径;换源清 Key 的两处残留;#582 复审其余六条的 ①②③。维护者 2026-10-25 定:订阅(OAuth)供应商换源先提示、再退出登录清令牌,未保存时「测试」不带令牌,模型自带 Key 按 API Key 供应商的口径一并清;Anthropic 协议没有 Key 时只对官方 `api.anthropic.com` 回落环境变量(`ApiKey` 与 `AuthToken` 一起显式置空),只配环境变量直连官方的用法不能被误伤;跨源模型覆盖必须自带 Key、不再借主槽,现有这类模型给迁移提示;换源清 Key 的确认改成独立确认框;模型级 429 / 403 冷却只冷那一个模型,空链时跨账号的自动重放加开关、默认关,删掉 Key #1 后照样显示空主槽、能填回去。

**改动**:
- Anthropic 环境变量:先对 Anthropic SDK 13.2.1 实测(本机设两个环境变量、打本地假端点)—— `ApiKey` / `AuthToken` 置空各自只关掉自己那个变量,两个都置空才一个都不读,同时设置不抛异常;两个变量都设时 SDK 只认 `ANTHROPIC_API_KEY`。`AiSettingsStore.CreateClient` 的 Anthropic 分支:凭据为空时只有基地址是 `https://api.anthropic.com`(默认端口;大小写、尾斜杠、`/v1` 按 `ClientBaseUrl` 归一)才照旧交给 SDK 读环境变量,别的地址两者一起置空;`IsOfficialAnthropicEndpoint` 不认明文 http、别的端口、`api.anthropic.com.relay.example` 这类。设置页未保存跨源「测试」的拦截不再把环境变量算作会带上的 Key(它只会去官方主机,那正是它该去的地方)。
- 跨源模型覆盖:`ResolveCredentialCoreAsync` 在 API Key 供应商下,覆盖地址与供应商不同源、没勾自带 Key 的模型返回空凭据(主槽也不借),点名任何槽都抛 `ApiKeySlotChangedException`(判据 `AiSettingsStore.InheritsNoKeyAcrossOrigins`);以前借主槽,供应商换源之后为新地址填的主 Key 会被发往旧覆盖地址(§217 留下的口径)。供应商自己的地址解析不出 origin(空着或写错)时照旧只用主槽,免得把这种无效配置也打成「跨源」。删掉只为「跨源探活只取主槽」存在的 `ResolvePrimaryCredentialForProbeAsync`;逐 Key 检测的候选不再含跨源覆盖的模型,「测试」同一口径。迁移提示:供应商页点名这类模型(`CrossOriginModelsHintText`,警示色);模型页在表单(含未保存的覆盖地址)是「跨源且没勾自带 Key」时显示说明与「沿用供应商的第 1 把 Key」(`CrossOriginKeyFillButton`)——〔决策〕只勾上自带 Key、把主槽此刻的 Key 填进表单,点保存才落库,那一刻才算用户明确把这把 Key 交给覆盖地址;主槽空着就只勾上并请用户填。〔决策〕这条口径只对填 Key 的供应商生效,订阅登录的不改:令牌的适用范围由签发方定(同一个 Entra 令牌本来就能用于同租户的多个 Azure OpenAI 资源)。五语新增 `CrossOrigin*` 五个键,`OwnApiKeyHint` 改成说明跨源时不沿用。
- 独立确认框:新增 `Ui/ConfirmOverlay`(盖住整个视图的遮罩 `VelaScrim` + 居中卡片,版式照宿主 `MessageDialog`:48 高头部 + 警示图标与标题、说明、52 高按钮栏,危险按钮是描边 + `VelaError`,DESIGN.md §5.1;默认焦点在「取消」,Esc 取消,Tab 只在框内循环,点遮罩不算任何选择,视图被拆下按取消收场,同时只问一件事)。插件窗口走宿主 `PanelDisplayMode.Window`、SDK 没有模态框契约,所以盖在视图里而不是自己开窗(宿主画的标题栏仍可操作,关窗时按取消收场)。设置页(`KeyResetConfirm`)与目录页(`SetupConfirm`)换源清 Key 都先弹框:说明清几把、点名哪些模型自带的 Key 会一起清,点「清除并保存」才清,取消时状态行写「已取消,什么都没改」、草稿留着;以前同一个按钮点两下就算确认(§217 / §230)。框开着时「保存」等按钮锁着(`SetKeyMutation`);设置页点确认后带着这份确认从头再存一次;框开着期间要清的有变(同一份设置又加了一把、同一个槽换了值),存储层核对确认凭据再拒、换一句话(`SetupBaseUrlKeyResetChanged`)再问,别的窗口写过盘按冲突重载。删除供应商、移除单把 Key、清空历史的两击确认没动。§230 留下的竞态(别处的换源提交写盘失败、回滚恢复 Key 不推进配置版本号,恰好撞上设置页未保存的跨源「测试」在读已存 Key):〔决策〕地址换了源、首模型跟着供应商地址走时,定下「不带任何已存凭据」就不再读机密,最坏是一次不带 Key 的测试 —— 比「取到凭据后再核一次」更彻底(那仍有核完到发出之间的窗口)。
- 订阅供应商换源:核对时发现目录页对配齐了的订阅行**不摆地址框**(`BuildSubscriptionDetail` 在已连接 / 字段齐全时早退),重新登录改不了地址,实际能换源的只有设置页。存储层 `CommitProviderConfigurationAsync`(`SaveProviderFormAsync` / `SaveProviderOAuthFormAsync` 共用)在地址换到别的 origin 时:跟着供应商地址走的已存 Key(`KeysBoundToProviderOrigin`:主槽、补充槽、没填覆盖地址的模型自带 Key)要清,订阅登录又没带来新令牌的还要退出登录;先在写闸里读、核确认,一样都不动,没确认就抛 `ApiKeyResetRequiredException`(新增 `SignsOut`;确认凭据里多一项登录令牌的摘要 `LoginResetToken`,确认之后换过一次登录就对不上、再问)。确认后同一次提交里删令牌机密与缓存、清 Key、写 JSON、发布,成功才推进登录代次(在途的令牌刷新回来对不上号不写回)与配置代次(在途请求复验时拒绝,报「接入配置已变」或换备用站;已经在路上的那一条 HTTP 照旧打旧地址、带旧地址的令牌 —— 不泄露,只是这一轮作废);任一步失败逐个恢复令牌与 Key。设置页走同一个确认框(标题「换地址需要退出登录」,说明正在用这家的对话与 IM 机器人会中断,按钮「退出登录并保存」),保存后提示到「管理登录」为新地址重新登录;未保存时「测试」不带令牌,还登着(或首模型自带、跟着旧地址走的 Key 还在)就提示 `SetupTestNeedsSaveSignIn` 不发请求。目录页「登录前先问」没有可达的入口,改为存储层保底(`SaveProviderOAuthFormAsync` 多一个 `confirmedKeyReset`,重新登录的提交若要换源、模型自带的旧 Key 没确认就拒绝)加守卫用例(配齐了的订阅行哪天摆出地址框就会红)。
- 冷却按模型(#582 复审①):`ProviderHealth` 的记录分模型、整把 Key、「某把 Key 用在某个模型上」三种。`RecordKeyFailure(key, model, wholeKey)`:`TransientFailure.IsKeyRejected`(只认 401)为真冷整把,403 / 429 只冷这一对;`RecordKeyForModel` 成功解开这一对与整把;`IsKeyCooling(key, model)` 看整把或这一对。单 Key 供应商的兄弟模型故障转移时照常探活,不再被连坐跳过。聊天面板首发选槽、换槽、换站探活、正式失败 / 成功,设置页逐 Key 检测与「测试」,全局「检测全部」都改用这套;设置页 Key 行的「冷却中」只看整把。〔决策〕403 也只冷这一对(有的服务对没开通的型号回 403、Key 本身没问题),代价是真被吊销却回 403 的 Key 在兄弟模型上会多花一次探活才被认出来。
- 空链跨账号开关(#582 复审②):`AiSettings.AutoFailoverAcrossAccounts`,默认关(存量 JSON 没有这个字段,读上来就是关),全局设置「故障转移」一节的勾选框 `FailoverAcrossAccountsCheck`。关着时 `FailoverCandidates` 的自动发现只收同一个供应商实例、同一凭据归属的候选(换了协议或地址的同一型号);另一个实例或模型自带的 Key 都算另一个账号(企业与个人 Copilot)。显式排链不受影响。
- 空主槽(#582 复审③):主槽只清了机密而补充槽还在时,设置页 `RefreshProviderKeyRowsAsync` 照样在第一行摆空的主槽(「(空着,点铅笔填回)」,`SetupApiKeyEmptySlot`),显示 / 移除 / 检测禁用、只留铅笔;「检测全部」只测有 Key 的行,空主槽不让批量入口变灰。铅笔填好按「保存」落回主槽(存储层原本就支持,只是界面没入口)。

**验证**:各项都有先红后绿的用例并做过变异(撤掉实现:环境变量改成一律回落 12 例 / 只置空 `ApiKey` 7 例、跨源借主槽存储层 3 例 + 设置页 3 例、确认框换成「直接当确认」7 例、退出登录 2 例 + 测试拦截 1 例、按模型冷却改回一律冷整把 7 例、跨账号条件 3 例、空主槽 1 例变红)。存量配置迁移各有一条:只配环境变量的官方 Anthropic 照旧回落、换源清了 Key 的中转站不回落;跨源覆盖的旧模型读回不带 Key 并能照提示迁移;旧 JSON 没有跨账号字段读回为关;移除过第 1 把的供应商显示空主槽。顺带发现目录页 `ProviderApiKey_MainSaveFailure_RestoresConfigurationAndKeepsRetryDraft` 自 #646 起一直停在「第一次只提示」,写机密 / 写 JSON 失败与取消三种失败从没真跑到,改成在确认框里确认后真跑,恢复路径全过。`-warnaserror` 0 警告;`VelaShell.Plugin.Ai.Tests` 照 `scripts/ci/run-tests.sh` 分 4 个进程 1760 例全过(其余 1072 / 聊天面板 301 / 接入向导 192 / 设置页 195)、0 跳过;`VelaShell.Tests` 2856 通过 / 9 跳过。只在 Windows + .NET 11 上跑过,没有真实供应商、真实 OAuth 登录与人工 GUI 实测;确认框外观只按 DESIGN.md 与宿主 `MessageDialog` 核对,没截图(插件测试宿主不装 Vela 令牌)。

**文档**:插件 README(Base URL 换源、订阅换源退出登录、跨源覆盖必须自带 Key、Anthropic 环境变量、确认框、按模型冷却、空链跨账号开关、空主槽);仓库内 `manual.md` / `manual.en.md` §19.2 / §19.3;velashell-docs `plugins/11-automation-and-ai` 新增 §1.8「AI 插件的供应商凭据与故障转移」(zh / en)。

## 267. 2026-10-25 一键放开算法推广到独立 SFTP 文档、连接诊断与连接配置页的「测试」

**来由**:`feature-plan.md` 二、D「一键放开算法推广到其它失败入口」。维护者 2026-10-25 定(第 31 条):路线图先排的四项里有这一项 —— 一键放开算法推广到其它失败入口(独立 SFTP、连接诊断、测试连接)。§248 的「放开这几个算法并重连」只接了终端标签的失败覆盖层;独立 SFTP 文档的失败卡片、连接诊断窗口、连接配置页「测试」失败时,诊断那一行(`SshInterop.DescribeRemedy`)照样点名了算法,却没有按钮 —— 只有 SFTP 账号的老设备用户还得自己翻到 SSH 选项手写 `+…`。

**改动**:
- 共用一段:新增 `Services/SshAlgorithmRemedyFlow`,把 §248 写在 `MainWindowViewModel.AllowAlgorithmsAndReconnectAsync` 里的「找到谈不成的那一跳在库里的配置 → 点名确认 → 写回库里」抽出来,四个入口同一个确认框(同一个标题「放开这几个算法?」、同一种点名;正文末句与确认键按入口说「然后重连 / 重新诊断 / 重新测试」),终端那条路径改为调它。〔决策〕找跳板的口径改成与 `ConnectionWorkflowService` 拼链一致:第一跳取连接所用那份配置手里的「跳板主机」,再往上取库里的 —— 原先从库里那份起数,「最近连接」重建的临时配置经跳板时找不到跳板(终端那条路径随之改变,临时配置经跳板时现在找得到);配置页里刚换过、还没保存的跳板也按表单里的算。
- 原始异常带出来:`ConnectionTestResult` 加 `Error`(测试失败的原始异常),`DiagnosticReport` 加 `Failure`(认证那一步 —— 协商就发生在这一步 —— 的原始异常);宿主照旧用 `SshAlgorithmRemedy.Find` 从异常链里认,点名的与按钮写进去的仍是同一份计算。
- 独立 SFTP 文档:`ConnectingDocument` 加 `AlgorithmRemedy` / `AllowAlgorithmsRequested`,失败卡片在「重新连接」上方多一颗与终端覆盖层同样的描边按钮(警示色图标,悬停点名类别与算法),重试时收掉。〔决策〕只认 SSH / SFTP 配置 —— 插件工作台经 SSH 隧道连出去的那条不是按这份配置连的,改了也白改。确认后改库里那份,终点那一跳同时改卡片「重新连接」要用的那份,然后走卡片自己的重试;双栏远程里是 SSH / SFTP 的那一栏失败时同一颗按钮,改的是失败的那一栏。
- 连接诊断:问题卡片的描述下面「放开这几个算法并重新诊断」;确认后改库,终点那一跳换成一份改过的**副本**重新诊断一轮 ——〔决策〕不改调用方那一份(它可能是资源管理器缓存的对象,也可能是某个标签手里的那份);跳板配置找不到、写不进库时原因写在按钮下面。改过库就在窗口关掉之后刷新资源管理器;确认框以诊断窗口为属主(它是模态的)。
- 连接配置页「测试」:反馈条的错误下面「放开这几个算法并重新测试」。〔决策〕终点那一跳就是正在编辑的这份,只改**表单**:展开自定义算法清单、把那一类写成补救后的样子,按「保存」或「连接」才存下,确认框多一句说明 —— 不替用户把表单里别的未保存改动一起落盘,取消对话框就当没改过;谈不成的是跳板时改跳板自己的配置并当场存下(表单里改不到别的配置)。错误换了(新一轮测试、保存失败)按钮跟着收掉。

**验证**:`AllowAlgorithmsEntryPointsTests` 7 条 —— SFTP 文档(协商失败给按钮、取消不改不重试、确认后库里那份只多 `+diffie-hellman-group14-sha1` 且不带手输口令、不开「允许老算法」、重试拿的是改过的那份、别的失败不给);诊断(确认键是「放开并重新诊断」、重新诊断用改过的副本、调用方那份不动、跳板配置找不到时说明且不重诊断);配置页(只改表单不落盘、确认框带「保存才存下」、重新测试用改过的表单;跳板那一跳当场存跳板的配置、表单不动;换了错误按钮收掉)。`ConnectingDocumentViewUiTests` 加一条(卡片上三颗按钮、悬停点名算法、点了把意图发给宿主、重试时收掉);Presentation:`TestConnectionAsync` 失败带出原始异常、诊断报告的 `Failure` 是认证那一步的异常。临时把三处「从失败里算补救」改成恒为 null,`AllowAlgorithms` 分类 6 条变红(SFTP 1、诊断 2、配置页 3);§248 原有的 5 条 `AllowAlgorithmsFlowTests` 照过(终端那条路径换成共用的 `SshAlgorithmRemedyFlow` 之后行为不变)。全量数字见 §269。合入时与 §259 在连接诊断这条链上撞了:`DiagnosticReport` 的 `PacketTrace` 与 `Failure` 并存;诊断窗口的问题卡片按 §259 加的「诊断结果 / 协议轨迹」分页结构重新放进这颗按钮;本节的诊断用例改为替身带轨迹的那个 `TestConnectionAsync` 重载。

**文档**:velashell-docs `host/交互与界面规格` §7「协商失败时一键放开算法」与「文档型连接的失败提示」、§13.1 协商失败那一条与反馈条、§15 连接诊断中心(zh / en);仓库内 `manual.md` / `manual.en.md` §13.4、§16.3。

## 268. 2026-10-25 会话健康巡检:对一批已保存连接跑四步诊断,出一张「哪几台连不上」的表

**来由**:`feature-plan.md` 二、B「会话健康巡检面板」。维护者 2026-10-25 定(第 31 条):路线图先排的四项里有这一项 —— 会话健康巡检,用户显式开启,不做定时外呼。连接诊断一次只查一台;几十台设备里哪几台掉了、哪几台密码过期了,只能一台台点。

**入口与范围**:资源管理器分组右键「巡检这一组」、多选右键「巡检 N 个连接」、命令面板「会话健康巡检(全部连接)」(`tools.healthCheck`,按名称排)。只在点开时跑,没有任何定时器;一次一批 —— 上一轮没查完时再点只把窗口前置并提示一句,不打断。全程至多一扇非模态结果窗口(`SessionHealthCheckWindow`,尺寸与位置记在 `healthCheck`,§241 的 `RememberAsync`)。只查 SSH / SFTP 配置,FTP 与插件协议列在表里标「不巡检」。最多 6 台同时查(与整组打开同一口径),每台限 30 秒,到点记「超时」不拖住整批(两个数写死为常量,不开放成设置);标题栏可「停止」(还没查完与排队的记「已停止」)、「重新巡检」、「只看有问题的」、「复制为文本」(制表符分隔)。

**每台做什么**:复用 `ConnectionDiagnosticsService` 的四步(DNS → TCP → SSH 识别串 → 认证;跳板链时前三步对第一跳,认证走全链),新增 `DiagnoseUnattendedAsync`(`DiagnoseAsync` 改为转调私有的 `DiagnoseCoreAsync`);结论按报告归类(`SessionHealthClassifier`):四步都过「正常」;没验证登录「可达」;没存凭据「需要凭据」;要人回答的「需要确认」;其余失败「连不上」,第一个失败的步骤就是「卡在哪一步」,说明是问题标题加那一步的原因。行上可打开那台的连接诊断(以巡检窗口为属主)或编辑配置,编辑保存后自动重查那一台(先从库里取最新的那份)。

**认证那一步**:〔决策〕做,但**不弹任何框** —— 用户要的是「哪几台连不上」,端口开着但密码过期 / 钥被撤也是连不上,只查到握手看不出这类问题。`ConnectionInfo` 新增 `IsUnattended`,`ConnectionWorkflowService.TestConnectionUnattendedAsync` 按它连(跳板链每一跳都标):
- 没存凭据的照诊断原样跳过、记成警告 →「需要凭据」(连接时会弹登录框,巡检不弹)。
- 主机指纹要人裁决的(首次见到且开着首次确认、或变了且没开「变更即阻断」)按自动重连的口径直接拒 →「需要确认」;说法是「巡检没有替你接受…请手动连接一次」(`Msg_HostKeyUnattendedBlocked`)而不是「已停止自动重连」;〔决策〕第一次见到的不记安全告警(用户点名要查的机器,只是还没亲自连过),变了的照记。关着首次确认时照旧 TOFU 记下,与手动连接同一口径。
- keyboard-interactive:换成一个谁都不问、一律谢绝的应答界面 —— 应答器照旧用已存的密码代答认得出的口令提示,轮到动态码、改密码就谢绝,报成「服务器要求动态码一类的交互验证,巡检不替你回答」→「需要确认」。不能干脆不挂应答器:那样库自带的「密码兼答」会把密码填进验证码那一轮,白白记一次动态码失败(`MaxAuthTries 1` 的账号当场断开)。
- Tailscale check 模式要复核的照自动重连口径拒 →「需要确认」。插件配置不测(插件探针会开真会话、可能弹插件自己的框)。调用方的取消(停止、单台到点)原样抛出,不当成「连不上」。
- 标题栏「验证登录」开关(钥匙图标,默认开):关掉时只查到 SSH 握手、不往服务器上登录(有的环境每次登录都告警、记 lastlog),下一轮生效,这次启动里记住、不进设置。
- 没有特殊处理的几处:外部 agent 自己设了逐次确认(`ssh-add -c`、1Password / KeePassXC 的授权弹窗)时那是 agent 的界面,巡检会照它的规矩弹(手册 13.4 写明了);设置开着「自动加载密钥到 Agent」时,巡检登录成功同样会把私钥加进 agent(与「测试」一致);巡检的登录不写 `audit_log` 与连接历史(与「测试」一致),最近连接列表也不变。

**隐私**:`PRIVACY.md` 中英两段「你所诊断的主机」各加一句:只在点开时运行、从不定时;对选中的每条连接做的与连接诊断相同,保存了凭据且开着「验证登录」时登录一次随即断开;只连这些主机。

**验证**:`SessionHealthCheckViewModelTests` 16 条(归类六种 —— 正常 / 需要凭据 / 可达 / 卡在 TCP / 指纹·动态码·Tailscale 三种「需要确认」而密码不对是「连不上」/ 跳板链配错卡在「跳板配置」;一批十台最多六台同时查、按给的顺序列、查着时汇总带进度、上一轮没完不接新批;非 SSH 列出但不探;停止取消在查的与排队的;慢的一台超时不拖别的;「验证登录」关掉传给诊断且下次开窗沿用;只看有问题的 + 复制文本列全部行;重查一台先从库里取最新配置、只查它;行上两个动作;资源管理器分组 / 多选两个菜单;命令面板按名称排出全部连接);`SessionHealthCheckWindowUiTests` 1 条(五种结论各一行、查完「停止」灰掉,带 `VELASHELL_VISUAL_QA_DIR` 截图、本地看过 PNG);Core `UnattendedProbeTests` 4 条在环回测试服务端上走 `SshConnectionAssembler`(首次见到的指纹不问直接拒、不记告警、说法是巡检的;变了的照记告警;手动连接照常弹框;「口令 + 动态码」的 PAM 剧本只答了口令那一轮、界面一次没问、报成要人亲自连);Presentation 5 条(巡检测试每一跳都标 `IsUnattended`、普通测试不标也不是自动重连、调用方取消照抛、插件不探;诊断巡检那一路走巡检测试、关掉「验证登录」停在握手)。逐项临时去掉实现(装配器里的「巡检换成谢绝式应答界面」与「巡检也算没人在等」、主机指纹策略的「首次见到不记告警」Core 3 条红;分类器的「要人回答」判断、并发上限 VelaShell.Tests 3 条红)。中途一次全量里 `Stop_CancelsWhatIsStillRunningOrWaiting` 红过一次 —— 实现的真问题:停下的那一刻别的行放出名额、先给了排队的那一行,它还会开始查;改成拿到名额后先看是否已停,再连跑 15 轮该分类、再跑全量都过。全量数字见 §269。合入时与 §259 撞在连接这条链上:`BuildConnectionInfoAsync` / `BuildChainAsync` 同时带 `packetTrace` 与 `unattended` 两个参数;装配器两处 `ConnectAsync` 的「没人在等」与协议轨迹两个实参都传;巡检那一路(`TestConnectionUnattendedAsync`)不挂协议轨迹,只有诊断窗口挂。

**文档**:velashell-docs `host/交互与界面规格`(§12 多选与分组菜单、§8 命令面板、新增「会话健康巡检窗口」一节、§15、主机指纹确认一节与动态码一节)、`host/settings-audit`(注明「验证登录」是窗口里的开关、不是设置项)(zh / en);仓库内 `PRIVACY.md`、`manual.md` / `manual.en.md` §3.1、§4.2、§13.4。

## 269. 2026-10-25 标签条「+」的新标签精确落进按下的那一格

**来由**:`feature-plan.md` 一、会话与工作区「「+」的显式落点」:§235 的「+」只保证那一格成为活动窗格,再走 §64 的落点规则 —— 布局里另有空窗格时新标签落进空窗格。维护者 2026-10-25 定(第 25 条):按下的那一格优先于空窗格(新标签精确落进按下的那格)。

**改动**:
- 模型层:`DockWorkspace.AddDocument(document, target = null)` 加显式目标(不在树上时退回默认规则)。落点先后:显式目标 > 当前异步流程的票据 > `PlacementResolver` > 默认规则(空窗格 > 正在用的窗格 > 主组)。
- 票据:新增 `DockWorkspace.TargetNewDocumentsAt(group)`,在 `AsyncLocal` 里挂一张「落进这一格」的票据,释放即作废 —— 新建连接对话框与 `TryConnectProfileAsync` 一个参数都没加,流程里建的第一个标签(终端标签、文档型连接的「连接中」占位)认领它;一张票只用一次(双栏之类一次建多个的流程本来也只建一个占位);流程走完(连上、失败、取消、只按了「保存」)作废,流程里另起、之后才建东西的后台任务拿不到;别的流程(自动重连、布局模板还原)看不到。与 §236 的槽位票据同一个思路,〔决策〕但放在模型层:`PlacementResolver` 已被布局模板还原器独占,两者不会同时出现在一条流程里,票据先于 `PlacementResolver`。
- 宿主:新建连接改为可等待的 `MainWindowViewModel.NewConnectionDialog`(窗口注入「弹框 → 保存 → 连接」整条流程,原 `NewConnectionRequested` 事件去掉);`session.new`(Ctrl+N / Ctrl+T / 命令面板)走它;「+」走 `OpenNewTabInPaneAsync`:那一格先成为活动窗格,挂票据,等流程走完再作废。`Ctrl+T` 与别的入口照旧按默认规则(先填空窗格)。`MainWindow.OpenProfileDialogAsync` 改为返回是否落了库(§268 巡检窗口的「编辑后重查」也用它)。

**验证**:`DockWorkspaceTests` 5 条(显式目标压过空窗格、目标已不在树上退回默认;票据顺着 `await`(含换线程)传进去、只认第一个;没用上的票据随流程作废 —— 流程里另起的后台任务之后建的照默认;别的流程看不到);`MainWindowViewModelTests` 1 条(原 `OpenNewTabInPane_ActivatesThatPane_ThenRunsTheCtrlTCommand` 改写:「+」打开的与 `session.new` 是同一个对话框;另有空窗格时,对话框让出一次之后经真实的 `TryConnectProfileAsync` 建出来的终端标签落进按下的那一格、空窗格不抢,流程结束后再开的照默认落进空窗格)。临时让 `AddDocument` 不认显式目标与票据 4 条红,去掉票据的「作废」判断 `TargetTicket_LeftUnused_ExpiresWithTheFlow` 红。§267 – §269 三节在同一分支上一起跑:`-warnaserror` 0 警告;按 CI 口径 `VelaShell.Tests` 2894 通过 / 9 跳过、`VelaShell.Presentation.Tests` 99、`VelaShell.Core.Tests` 1256 / 6、`VelaShell.Infrastructure.Tests` 977 / 26;没动 SSH 库与 X 服务端库,没跑那两个工程与 Docker 用例。

**集成后的全量**(覆盖 §250 – §269 全部):本地 dev @ 1a61038c,Debug 全解决方案 `-warnaserror` 0 警告;按 CI 口径 `scripts/ci/run-tests.sh`(排除 DockerIntegration / CrossPlatform / Interop,AI 插件三组 headless UI 分进程),14 个测试进程合计 **10240 通过 / 0 失败 / 54 跳过**(跳过的都是平台条件早退)—— `VelaShell.Tests` 3044、Core 1397、Infrastructure 1076、Ssh 1449、Terminal 638、RenderTests 9、XServer 528、Presentation 103、Controls 31、Sudo.Kernel 11、Plugin.Ai 1251 + 309 + 198 + 196。Docker 端到端这一轮集成之后没跑(各组自己跑过的写在各节里);只在 Windows 上跑,macOS / Linux 等推上去之后的 CI。

**文档**:velashell-docs `host/交互与界面规格` §4B.1「+」、`host/dock-replacement-plan` §2.3 `AddDocument`(zh / en);仓库内 `manual.md` / `manual.en.md` §8.1。

## 270. 2026-10-27 AI 插件:对话内作图与编辑、图片存档与剪贴板输入

**来由**:手动添加 `gpt-image-2` 后图片不能显示并报空回复。原请求装配没有 Images 通道,回复渲染与非空判断也不处理图片;另补用户要求的剪贴板图片输入。

**改动**:底层 `OpenAiImages`、生成预览与存档保留;交互改成全局设置选择默认图片模型 `ImageModelId`,聊天模型使用 `generate_image` / `edit_image` 调用 `/images/generations` / `/images/edits`,不切顶栏聊天模型。聊天仅开放图片工具、计划不开放、Agent可用;图片失败不自动重发或换服务,工具账本复用已完成CallId。图片ID只在所属对话有效,输入附件与生成图存档后可多轮编辑和历史续图,模型不能借工具读任意本地路径。Ask/ReadOnlyAuto显示模型、目标、参考图数量与提示词审批,Bypass按既有语义跳过。默认模型失效明确报错、不偷偷fallback。原直接选图片模型文生图仍兼容;生成字节/类型/数量限制、探活不收费、保存按钮与剪贴板4件/5MiB不变,IM本轮不接图片工具。仅借鉴cc-haha能力分层,未复制其代码或提示词。

**验证**:对话作图集成全量回归 1999 条通过、0 失败、0 跳过;图片/剪贴板/设置页/五语定向回归 232 条通过;最后补工具回执 JSON 类型防护后核心定向回归 19 条通过。宿主颜色令牌与 HeadlessDispatch 门禁 13 条通过。插件 Debug 非增量 `-warnaserror` 构建 0 警告/0 错误,双语手册与开发文档相对文件链接检查通过。额外启用 `EnforceCodeStyleInBuild` 时仍受未改动传输字段的 IDE0044 与既有 Anthropic 初始化格式 IDE0055 阻塞,新增文件的格式问题已修正。真实计费端点、系统剪贴板及跨平台 GUI 未实测。

**文档**:仓库 `manual.md` / `manual.en.md` §19.3,velashell-docs `zh|en/plugins/11-automation-and-ai.md` §1.9。

## 271. 2026-10-28 AI 输入入口分离与图片悬浮操作

**改动**:图片右下角同格 Grid 悬浮两个独立34px圆形图标浮钮（Lucide `image-down` 保存、`fullscreen` 大图）,无文字与共用底板;仅圆形表面半透明、图标保持清晰,悬停加深与键盘焦点环跟主题。原保存字节和选择器逻辑不变。预览走宿主二级窗口,按源PixelSize/屏幕缩放计算物理像素1:1,初始窗口贴合图片并限屏幕工作区;大图等比缩小完整显示、小图不放大。底部显示百分比,提供＋/−、100%、适应窗口,Ctrl滚轮/双击切换,适应模式随窗口改变重新计算,手动放大后可滚动。Esc/关闭退出;面板 Detach/视觉树移除与迟到打开都关闭并释放资源,加载失败提示而不崩对话。输入前缀按维护者后续要求分离为 `@` 工具(`@imagegen` / `@imageedit`)、`#` 远端文件、`/` 本地命令;不兼容旧 `@/路径`。工具菜单无 SSH 也可用且不探测供应商,保留自然对话生图;井号支持路径补全/引号/芯片/整块删除。命令目前仅 `/clear` 新建但不删历史、`/resume` 历史选择或按ID恢复/标题筛选、`/help`,未知命令留本地、忙态不换会话;不宣称完整Claude Code CLI兼容。文件补全在工作目录await后补代次校验,防旧请求覆盖新工具/命令菜单。

**验证**:插件全量 2006 通过/0失败/0跳过;新语法、预览、自然作图与五语定向31通过,颜色令牌/HeadlessDispatch门禁13通过。圆形图标浮钮调整后定向16通过（断言无文字/无共用底板、34px圆形、底面透明度、两枚Lucide几何与弹窗行为）,Debug非增量 `-warnaserror` 0警告/0错误;预览尺寸/缩放改动后另16条定向通过（包含横竖大图、高DPI、初始尺寸、实际比例、100%/适应切换与视口变化）,主题/HeadlessDispatch13条通过;未重复全量。真实GUI/系统剪贴板和计费供应商未实测。

**文档**:手册中英§19.3、插件README;velashell-docs 中英 `plugins/11-automation-and-ai.md` §1.9 与 STATUS。

## 272. 2026-10-28 执行通知失败用例的跨平台时序竞争

**来由**:PR #681 的 CI run `40565636099` 在 Ubuntu 上只有 `DeliveryFailure_WakesTheWaitingProducerWithAnExplicitError` 失败:生产者已收到发送异常,失败回调计数却仍为 0。写泵先关队列并释放锁、再调失败回调,生产者恢复不代表回调已完成;Windows/macOS 也没有额外的先后保证,该轮通过不代表不受影响。

**改动**:原用例先等 `DrainAsync` 完成并验证原始 `IOException`,再检查回调次数;直接运行异步生产者到背压等待点,去掉用延时猜测启动时机。新增受控阻塞失败回调的用例,明确验证生产者可在回调完成前收到原始错误、Drain 必须等回调结束。生产实现不变:普通流与 sudo 流本已在 finally 中等 Drain,不把测试的过早断言改成新的生产时序约束。

**验证**:Windows 下队列用例 5/5、执行队列与 RPC 相关回归 21/21 通过,0 跳过;两个失败用例重复 30 轮共 60/60 通过。Infrastructure 测试集按 CI 类别过滤运行:1133 通过、0 失败、30 跳过,跳过涉及平台限定、系统密钥链及外部工具/文件系统环境。测试工程及依赖 Debug `-warnaserror` 构建 0 警告/0 错误。未在 Ubuntu/macOS 本机重跑,新增用例无平台过滤,沿用已有三平台 CI 矩阵。不改用户行为、接口或配置,无需修改双语手册与 velashell-docs。

## 273. 2026-10-28 FTP → SFTP 批量中转的完成回复与长度门禁

**来由**:Ctrl 多选 FTP / SSH 后打开双远端文件列表,批量传输 2000 多个文件时出现部分 0B。核实 FluentFTP 55.1.0 后发现其数据流 `DisposeAsync` 不消费最终 FTP 回复,原封装却直接归还连接;中转又把提前 EOF 当正常结束,没有核对源 stat 的正长度。已确认这些代码缺陷,不把它们未经用户服务器复现就说成全部故障的唯一原因。

**改动**:FTP 读流统一独占完成回复消费,关闭数据资源后核实 226/250 才归池;异常、取消、提前弃读、无回复或负回复剔除连接,租约与混合同步/异步释放均幂等,失败路径也归还名额。中转校验已知正长度、兼容 seek 尾部核实与安全回退,源收尾成功前只报已确认字节、不发最终 100%;保留重试记账、原始异常及迟到进度语义。0 仍兼容空/未知大小,不改 SDK 或通用流上传契约;本轮（§273）未增加落盘缓存、原子替换或全文件哈希保证。后续 §276 改为 FTP/FTPS 完整缓存,§277 又将全部文件统一到 `UserPathResolver.Home/.velashell/temp`,不能将本节的无落盘描述用于当前 FTP 中转。新增错误五语齐全,失败目标沿用现有续传/清理设置。

**验证**:修复前 6 项核心回归确定失败;修复后 RemoteRelay 31/31、双栏入口/失败状态/本地化 31/31。Windows 上完整解决方案 Debug `-warnaserror` 0 警告/0 错误;按非外部环境类别过滤的 Core 回归 1413 通过/7 跳过、宿主 3146 通过/9 跳过（随后补正重试记账与 seek-EOF 边界,定向 31 项再次通过）。FTP 定向 56/56,含真实 loopback FTP → RemoteRelay → SftpService（mock SFTP client 目标）,分别以 1/3 并发传 2000 个不同文件,逐文件长度与 SHA-256 全匹配,连接分别复用 1/3 条。Infrastructure 非外部环境全量 1151 通过/30 跳过;独立验证的 Core 对抗探针 71/71、FTP 非压力回归 57/57。跳过不计为已验证,未连接用户生产服务器,未验证真实 Linux SFTP 或 FTPS 端到端。

**文档**:手册中英 §9.6;velashell-docs 中英双栏差距分析 §8.1。既有 0B 文件不会自动恢复,须核对源后重新传输。

## 274. 2026-10-28 FTP 中转频繁重试续查:长度口径与真实写入回归

**来由**:用户实测 §273 后不少文件在刚开始/仍0B时进入自动重试、速度下降。该文案仅表示错误被分类为瞬态,不证明网络断线。上轮2000文件目标为 mock,没有覆盖真实 SFTP 首WRITE/Flush/CLOSE;本轮明确补足协议链覆盖,不把可控复现认作用户服务器现场证据。

**已证缺陷与改动**:7个修复前失败用例证明TYPE A SIZE可把实际14B报为17B,LIST/MLST可把实际6B报为23B,未知SIZE仍被列表显示值冒充精确长度。FTP同租约先确保Binary再SIZE,非目录stat统一该口径;按公开TYPE状态只在必要时切换,OpenRead传正长度或-1禁止库隐式重查。未知时stat沿既有0口径、流内-1,不提供未知长度下“0B+服务端谎报完成”的检测保证。可信长度的早EOF、负/缺完成回复继续失败,不改全局重试与并发。

真实SFTP协议红测另复现源错被目标CLOSE拒绝盖掉的3种场景;上传保留原源异常类型/实例,通过`VelaSftpUploadFailureDetails`附加收尾后的durable与中立cleanup异常,SftpService继续消费精确续传水位。长度错误带结构化预期/实际字节;开启传输日志后,每次失败尝试在退避前写ATTEMPT_FAILED类型链/HResult/数值/目标cleanup类型,不记录异常消息、原始服务端正文或额外路径,重试成功也保留诊断。聚合异常有界遍历。既有最终传输记录仍含用户选择记录的路径,不是声称整个日志无路径。

**验证**:FTP修复前7/7红测;修复后61/61通过（43非压力、4组2000文件×原始/混合元数据×并发1/3、14support）,逐文件RETR=1/SIZE=2、TYPE次数仅连接数、大小/SHA-256一致。真实FTP→RemoteRelay→SftpService→实际VelaSftpClientWrapper→TestSftpServer完整协议链每并发1/3各2000文件首试成功,逐文件RETR=1、目标OPEN各1、哈希正确/无句柄泄漏;它是SSH/SFTP协议测试端,不是OpenSSH。首WRITE延迟ACK时保留0B直至确认,没有额外WRITE重试;拒写、CLOSE错、双错与精确续传定向覆盖。扩展212通过/8个OpenSSH环境用例跳过。最终Core非外部全量1431通过/7跳过、Infrastructure全量1164通过/30跳过;宿主全量3150通过/9跳过（之后补聚合日志测试,最终定向45/45（含独立审查发现后补正的8/9/10层异常链截断标记））,solution Debug `-warnaserror` 0警告/0错误。Docker可用但无运行容器且未配置测试凭据,未擅自启动,未验证真实Linux/FTPS。用户现场实际根因仍需更新构建后的每次重试诊断确认,不宣称本机协议绿测代表现场已彻底解决。

**文档**:手册中英§9.6与velashell-docs中英双栏差距分析§8.1,说明binary SIZE、未知大小限制和每次重试的安全诊断。

## 275. 2026-10-28 双端顺序源的4MiB内存预读缓冲

**历史范围**:本节记录完整缓存方案之前的预读实现与当轮验证,不作为 §276 的测试结果。FTP/FTPS 源现由 §276 的高级下载与完整缓存路径替代;本节的有界预读仍适用于插件非 seek 源,「无磁盘中转」不再适用于 FTP/FTPS。

**来由**:用户再次实测仍有源端正长度但actual=0的中断,要求先增加缓冲再测试。原256KiB搬运块是同循环读写,不能独立吸收两端速度波动;缓冲不承诺修复真实空读取,也不取消完整性检查。

**改动**:不可Seek、非续传源使用内部`BufferedRelayReadStream`,256KiB固定块×16槽,4MiB预算涵盖producer/排队/consumer持块,独立生产任务预读、满时背压、空时等待。已知正长度≤4MiB先全部缓存并校验EOF,源Dispose（含FTP最终回复）成功后才writing/上传;更大或未知源首块成功后边读边写,未知真实空也先完成源收尾。目标实际消费量单独核对,不把预读完当上传完。可Seek源与续传走原路径。取消/源错/目标失败时cancel→join→清零归还全部块,取消回调抛错也不跳过清理,源只关闭一次,首错误和最终进度门禁保持。无磁盘中转/新配置/SDK改动,默认并发3预算约12MiB（不含SFTP原写窗口）。

**验证**:Core Relay定向49/49,覆盖4MiB−1/等于/+1、unknown、独立预读与持consumer半块时总预算4MiB、满队列取消/目标失败、源后续读错、取消callback抛错后全部归还清零、目标提前返回、同步Span读取与seek续传。实际FTP→RemoteRelay→实际SFTP wrapper→协议端25/25,含8MiB+97/未知长度、源最终回复失败不OPEN、小文件源租约在target前归还及2000文件并发1/3每文件RETR/OPEN一次与哈希一致。最终Core非外部全量1458通过/7跳过、宿主3158通过/9跳过、Infrastructure1170通过/30跳过。通过的是协议测试端,未运行真实Linux OpenSSH/FTPS,未访问生产或启动Docker。

**文档**:手册中英§9.6和velashell-docs中英双栏规格§8.1说明容量、预验后写、缓存释放和源空读限制；当前 FTP/FTPS 口径已由 §276 更新。

## 276. 2026-10-28 FTP/FTPS 双端中转改为完整缓存（本轮文档 completed）

**来由与证据边界**:按批准方案,FTP/FTPS 源不再经低级读流边读边上传,改用高级下载先完成源阶段。既有代码缺陷与受控复现已记录在 §273–§275,但没有用户真实现场的唯一根因确认,不把缓存调整写成已彻底修复现场故障,也不声称本轮测过真实 OpenSSH。

**本轮完成的文档变更**:手册中英 §3/§9.6、velashell-docs 中英双栏规格 §8.1–§8.2 已同步批准行为；§128、§273、§275 的旧续传/不落盘口径加上历史范围说明。此记录仅确认文档部分 completed,不替代实现验收或测试结论。

- **FTP/FTPS source**:双远端任一侧作为源均走高级 `DownloadStream` → 完整 cache → Upload。缓存位置以 §277 的统一口径为准：全部文件（含空文件）使用 `UserPathResolver.Home/.velashell/temp` 下随机命名、独占打开、`DeleteOnClose` 临时文件,不再按 ≤4 MiB 内存、large/unknown 系统临时目录分流。源完整 download、缓存 `Flush`、适用的 length 核验与 final reply 全部成功才 touch target,失败前不得打开/截断/写入或清理原目标文件。
- **生命周期**:同一 `Run` 内自动 target retry 复用成功 cache,只有 source prepare 失败的自动重试才重新 prepare。手动 retry 开新 Run；冲突 rename、最终失败、取消、成功均清理 cache,不跨运行保留。远端半截文件仍按原续传/清理设置处理,不是本地缓存的保留策略。
- **不变与续传边界**:SFTP → SFTP 原有 stream/seek resume 不变,插件非 seek 源保留 4 MiB 有界预读 buffer。FTP 完整 cache 可 seek,可以参与 target 尾校验续传,但仍需目标协议/实现及设置支持,不宣称所有目标支持。
- **进度、错误与完整性**:下载准备显示独立阶段,不计 target ACK；本地缓存磁盘失败归为永久错误。unknown SIZE 下没有可信长度,不能证明无短读或源本应非空,final reply 成功也不能排除服务端谎报完成。无全文件哈希或原子替换保证。
- **server direct 答复**:普通 SFTP 无通用跨服务器复制；SSH 一端执行 `scp` / `sftp` / `rsync` 需服务器互通、工具、凭据及 host key 校验,FXP 受两端服务支持、安全策略与网络条件限制。本次不实现、不转发凭据或 agent。

**最终验证记录（维护者提供的实际执行结果）**:本次文档收口未重新运行构建或测试,以下按执行范围与 snapshot 分开记录,不把旧结果或未完成的运行算作最新通过。

- **最新构建与宿主**:完整 solution `-warnaserror` 构建 0 警告/0 错误；宿主定向 63/63 通过,完整非 Docker 测试 3173 通过/12 跳过。
- **Core 定向**:staged cache 与旧 relay 回归 87/87 通过；`runtime-async=off` 下同组 87/87 通过。
- **FTP 最后修正后的回归**:`Noop=false`、`NavigateManual` 与 readonly guard 修正后,focused 33/33 通过、0 跳过；这是定向回归,不是全量 pressure 重跑。独立 readonly 回归 2/2 通过。
- **新 staged wire**:12/12 通过、0 跳过。链路为实际 loopback FTP → Staged → 实际 SFTP wrapper → `TestSftpServer` memory-wire,不是 mock 目标,也不是 OpenSSH。并发 1/3 各传 2000 个文件,每文件 RETR=1、每组目标 OPEN=2000,逐文件哈希全部一致。目标首次 WRITE 拒绝后,复用同一 cache 换用另一个健康 Harness 上传成功,源 RETR 仍为 1；这是**目标替换后的缓存复用**,不是同一目标会话的恢复验证。源最终回复为 426/451 时,小文件、大文件及未知长度场景的目标 OPEN 均为 0。TRX：`G:/velashell-labs/artifacts/staged-wire-final/results/staged-wire.trx`。
- **上一 snapshot,不冒充最新全量**:Core 全量 1496 通过/7 跳过、Infrastructure 全量 1197 通过/30 跳过属于 FTP 上述最后修正之前的 snapshot,不作为修正后全量结果。
- **未通过与环境边界**:`runtime-async=off` 的 FTP 运行遇到 Socket 10048,不计为通过,也不能由 Core 同模式 87/87 替代。跳过不计为已验证。未测试真实 OpenSSH 或生产服务器；本轮结果没有证明用户现场根因,不宣称现场故障已彻底解决。

## 277. 2026-10-28 FTP/FTPS 双端缓存统一用户目录

**本轮事实**:按用户要求,`StagedRelaySource.CreateStorage` 已删除生产内存分支,所有文件统一写入用户目录缓存。同步手册中英 §9.6、velashell-docs 中英双栏规格 §8.1–§8.2；保留现有完整性检查、上传重试复用与清理机制。

- 双远端任一侧作为 FTP/FTPS 源时,全部文件（含空文件、小文件、大文件、unknown）统一缓存到 `UserPathResolver.Home/.velashell/temp` 下随机命名、独占打开、带 `DeleteOnClose` 的文件；不再使用 ≤4 MiB 内存缓存或 large/unknown 系统临时目录。
- 完整下载、缓存 `Flush`、适用的长度校验及最终完成确认全部成功后才允许上传并触碰目标；同一 `Run` 的自动 upload retry 复用已准备好的缓存。成功、最终失败或取消均释放缓存、关闭删除；手动重试为新 Run,不保留旧缓存。SFTP 原有 stream/seek 与插件有界预读路径不变。
- 磁盘缓存变更不等于已根治用户报告的约 2% 失败；未知 SIZE 的完整性限制与真实服务器互操作的证据边界不变。

**本轮验证**:Windows Debug 暂存与原中转定向测试 94/94 通过（45 项暂存、49 项 RemoteRelay）,覆盖各大小真实文件存储、正确目录、独占及成功/失败/取消删除。完整 solution `--no-restore -warnaserror` 构建 0 警告、0 错误；`TestCategory=StagedWire` 12/12 通过（含并发 1/3 各 2000 文件的 RETR 次数、长度及 SHA-256）,宿主重试/双栏/本地化定向 63/63 通过,均无跳过。Unix 0700/0600 分支未实机运行,未验证真实 OpenSSH 或生产服务器；不将可控协议测试通过等同于现场约 2% 失败已消除。

## 278. 2026-10-29 FTP 跨平台 CI 失败修复

**定位**：[CI 40725204631](https://github.com/joesdu/VelaShell/actions/runs/40725204631) 三平台构建均成功；Ubuntu 五条 2000 文件串行回归超过默认 60 秒，macOS 一条 reset/426 回归将长度错误误报为通用 FTP 操作错误。

- **Linux 超时根因**：环回 FTP 夹具的控制连接未禁用 Nagle，150/226 两次小回复与延迟 ACK 叠加约 40 ms/文件。WSL Debian 原串行 2000 文件耗时 88 秒、并发 3 耗时 29 秒；仅设置夹具控制连接 `NoDelay=true` 后降到约 1 秒/0.5 秒。保留原 60 秒默认超时、2000 文件规模和每文件 RETR/OPEN/哈希断言，不以放宽超时掩盖根因。
- **426 完整性诊断**：`DownloadStreamAsync` 在异常分支识别嵌套 `FtpCommandException(426)`，仅在 binary SIZE 已知且实际字节数不符时报告 `RemoteTransferLengthException`。本地写入错误、取消优先级以及 busy retry 保持不变；失败租约仍丢弃。reset/EOF 回归补充预期/实际字节数和目标流保持打开的断言。
- **验证记录**：最终补丁在 Linux 的 `TestCategory=Ftp` 为 110/110 通过；Windows 该分类修复后曾 110/110 通过。两平台 `SftpUploadAckProgressTests` 各 37/37 通过，均 0 跳过。Windows 连续多轮批量运行出现 Socket 10048（地址占用），一次定向运行两项失败，后续全分类重跑也再现；保留失败结果，不将最新 Windows 回归称为全绿，也未声称已解决该地址占用问题。最终 `dotnet build VelaShell.slnx --no-restore -warnaserror --nologo -v:q` 为 0 警告/0 错误；新增断言曾触发 MSTEST0017，已修正后重建。未在 macOS 本机重跑，未验证真实 FTP/OpenSSH 服务器，也未推送或重跑远端 CI。
- **文档**：用户手册中英 §9.6 与 velashell-docs 中英双栏规格 §8.1 同步异常分类边界；这是既有完整性门禁的纠正，不宣称用户现场故障已全部解决。

## 281. 2026-10-29 提交路径审查与 FTP 并发回归

**审查修复**：
- 目标为符号链接时不走远端临时提交，回退完整缓存，避免改变旧的跟随链接语义及暴露临时文件权限。
- SFTP 明确拒绝提交时保留原始权限/操作错误并清理临时文件；只有提交结果不明才包装 `RemoteRelayCommitException`、停止自动重试并保留残留。
- 未知大小的源在提交成功后按实际消费字节生成最终进度；提交不再等待文档元数据串行闸，但仍计入关闭排空计数。
- FTP `OpenReadAsync` 在返回读流前遇到 425/450 并发拒绝时失效租约、收紧池并最多重试两次；删除废弃上传路径时清除 durable 续传提示。

**验证**：核心定向 316/316、FTP 集成 91/91、宿主定向 66/66 通过，均 0 跳过；完整解决方案构建通过，`runtime-async=off` 构建通过；中英文手册与 velashell-docs 规格同步，双仓库 `git diff --check` 通过。新增回归覆盖符号链接回退、明确提交拒绝清理、未知大小最终进度、元数据闸并发、FTP OpenRead 425/450 重试、durable 提示清理。

**边界**：未运行真实 FTP/FTPS/OpenSSH 服务器、生产环境或远端 CI；受控 loopback / memory-wire 测试不能证明现场约 2% 失败率已消除。

## 280. 2026-10-29 FTP→SFTP 有界流式提交

**实现**：在 PR #684 的完整缓存基础上，为 FTP/FTPS 源从头传输、不同会话且目标支持原子替换的情况增加无本地整文件缓存路径。复用每文件 4 MiB 有界预读，写入目标同目录随机 `.vela-relay-*` 文件；完整消费、长度检查、源最终回复及目标上传成功后才提交，100% 等提交确认后发布。`SerializedSftpService` 补齐提交预检、创建权限及提交的透传，临时文件创建权限沿用原目标，提交沿用属主与权限处理。

**失败边界**：提交前失败只清理此次临时文件，自动重试重新读取源、使用新临时名，不续传或清理正式目标。提交使用独立 30 秒取消预算，未确认结果转专用异常并禁止自动重试，保留可能的残留供人工核对；五语提示同步。目标不支持原子提交、同会话及已有目标续传继续用完整缓存；缓存成功后自动上传重试仍复用。没有修改 FTP 目标的原子提交能力，也没有新增 SDK 契约。

**验证**：Windows Debug 核心/协议链定向 197/197、宿主重试/双栏/本地化 66/66、FTP 集成 88/88 通过，均 0 跳过。协议链包含新提交路径并发 1/3 各 2000 文件、逐文件 RETR=1 与长度/SHA-256，以及大文件最终回复失败保留旧目标、清除临时文件。此前新增测试缺少必填字段导致编译失败、旧缓存测试替身误报支持提交导致失败，修正后上述回归均通过。完整解决方案 `--no-restore -warnaserror` 构建通过。手册与 velashell-docs 中英规格同步。

**限制**：未运行真实 FTP/FTPS/OpenSSH 互操作、生产环境复测或跨平台 CI，未测性能基准，不声称现场约 2% 失败已解决。流式失败重试需要重读源，没有完整本地缓存的快照复用优势；目标目录须允许创建及重命名。随机临时名并非排他创建，不能将不可信共享可写目录作为安全边界。未新增源快照、全文件远端哈希或断电持久性保证；不支持安全提交的目标保留落盘回退。

## 279. 2026-10-29 Windows FTP 集成夹具端口耗尽修复

**根因**：`LoopbackFtpServer` 在每条 `PASV` / `EPSV` 命令中都关闭旧监听器并绑定随机端口；2000 文件传输会创建数千个被动监听器，Windows 上前序数据套接字进入 `TIME_WAIT` 后可能耗尽动态端口，最终以 Socket 10048（地址占用）失败。

- 每条 FTP 控制连接现在只创建一个被动监听端口，后续文件传输复用该监听器；控制会话结束时才关闭它。
- 被拒绝或未使用的数据连接在下一次被动协商时清理，避免残留连接污染后续用例。
- 2000 文件回归增加被动监听器数量必须等于控制连接数量的断言，确保夹具不会随文件数线性分配端口；未降低文件数量、并发度或完整性断言。

**验证**：Windows Debug 下 `FtpFileServiceIntegrationTests` 定向全类 88/88 通过；此前重复运行失败用例的三轮各 2/2 通过；`dotnet build VelaShell.slnx -c Debug --no-restore --nologo -warnaserror` 通过，0 警告、0 错误；基础设施工程完整测试 110/110 通过，0 跳过。未执行远端 CI 重跑。

## 282. 2026-10-30 FTP 单连接串行与双向流式中转核查

- FTP/FTPS 连接池的实际租约上限固定为 1，而非只修改默认值；旧 `MaxConnections` 仍序列化与拷贝，但不能再让同一会话开启多个客户端。上传、下载和元数据操作共用该串行队列，SFTP 并发不变。此限制按会话生效，不是全应用按主机合并连接。
- FTP→SFTP 安全提交路径经 `SourcePolledStream` 的有界 `Pipe` 边读边写；源短读时复用当前段剩余空间，修复每次申请整块导致内存分配远超有效字节背压阈值的问题。SFTP→FTP 直接从源流上传，无本地整文件暂存；不支持安全提交、符号链接、同会话及 FTP 源续传等兼容性回退继续保留。
- 回归补充旧连接上限 1/3/16 均只创建一个客户端、读流占用期间上传与目录刷新排队、真实 SFTP 包装层→环回 FTP 的空文件/分块边界/超过 4 MiB 文件及并发请求 1/3 的长度与 SHA-256 核对。修正测试读流向副本写数据的错误与取消异常类型的过严断言；源最终回复失败时不要求目标消费管道内尚未交付的全部数据，但必须不提交正式文件。

**验证**：最终 Windows Debug 核心定向 197/197、FTP 定向 119/119、宿主双栏与重试定向 60/60 通过，均 0 跳过；完整解决方案 `--no-restore -warnaserror` 构建通过，0 警告/0 错误。此前运行出现过测试主机栈溢出、短读内存分配耗尽和两处断言失败，未计作通过；修正后上述套件串行重跑通过。手册与 velashell-docs 中英同步。未执行完整测试集、真实 FTP/FTPS/OpenSSH、生产复测或跨平台 CI；不能据此断言现场服务端问题已消除。

## 283. 2026-10-30 Windows CI 文件补全测试等待条件修复

- [CI 40919256763](https://github.com/joesdu/VelaShell/actions/runs/40919256763) 的 Windows 作业仅 `FilePicker_ListsEachDirectoryOnce_WhileTheFilterKeepsChanging` 失败，断言时文件补全弹层尚未打开；同次 Linux、macOS 作业成功。失败属于 AI 插件 UI 测试，与本次 FTP 改动无直接调用关系。
- 原用例在输入后固定泵约 300 ms；补全需要异步解析工作目录、180 ms 防抖与 UI 调度，经过固定时长不代表弹层已经打开。改为复用现有 `WaitForAsync` 等待弹层打开，保留约 15 秒超时，以及列目录恰好一次、连续过滤不重复列目录、最终只剩匹配文件的断言。
- 仅调整测试同步条件，不改变产品行为、接口、配置或 CI 流程，无需同步用户手册或 velashell-docs。修复前本地单用例通过，未在本地复现原 CI 的偶发失败。

**验证**：Windows Debug 下 `dotnet test tests\VelaShell.Plugin.Ai.Tests\VelaShell.Plugin.Ai.Tests.csproj -c Debug --no-restore --nologo -warnaserror --filter FullyQualifiedName~ChatPanelViewUiTests` 构建及整组 329/329 通过，0 跳过；整组运行期间另起进程重复运行原失败用例 5 轮，均通过。`git diff --check` 通过。未推送改动或重跑远端 CI，未在本机验证 Linux、macOS。

## 284. 2026-10-30 CI 耗时核查与测试夹具等待修复

**运行数据**：对比 [CI 40804220762](https://github.com/joesdu/VelaShell/actions/runs/40804220762) 与 [CI 40926974730](https://github.com/joesdu/VelaShell/actions/runs/40926974730)，Windows 的构建从 148 秒增至 252 秒，测试步骤从 246 秒增至 502 秒；新运行三平台均成功，Windows 约 14 分钟、macOS 约 11 分钟、Ubuntu 约 8 分钟。测试进程彼此重叠，不能把各工程或用例累计时长当成流水线总时长。

- Windows TRX 中有耗时记录的用例仅从 10723 条增至 10739 条。44 条数据库持久化用例累计从 23.6 秒增至 104.2 秒，8 条时间序列用例从 3.7 秒增至 47.2 秒，8 条设置快照用例从 2.4 秒增至 30.3 秒；这些测试、数据库依赖版本及构建配置在两个提交间没有变化。数据表明不能只把增时归因于新增 FTP 用例或上节的单处 UI 等待修复，运行环境对耗时有显著影响，但未隔离出 CPU、磁盘或并发争用各自的贡献。
- CI 未启用 NuGet 包缓存；新 Windows 构建日志中的还原阶段约 73 秒。CI 还包含 SDK 安装，Linux 额外执行 `runtime-async=off` 构建；SSH 测试已有 CI 两个 worker、本地按 CPU 核数运行的区别。因此，本地全量测试与远端整条流水线不属于同一计时场景。本次没有调整工作流、测试范围、分析器或依赖版本。

**已修复的确定性浪费**：

- `HeldModelsEndpoint` 原先只应答一次，`PulledModels_RefreshTheUntouchedModelBoxAndSaveKeepsIt` 的第二次保存会再次拉模型清单，实际等待 30 秒 HTTP 超时后才返回。夹具改为持续应答，沿用可取消的监听接收工具收尾；原用例增加第二次清单刷新完成的断言。只加断言时两个 DataRow 均失败（预期两次通知、实际一次），修复夹具后通过。
- 桥接的「每轮重新读设置」「报错点名模型」用例改用现有 `ErrorStub` 返回真实 HTTP 401，替代固定未监听端口，保留原有错误文本断言并核对确实发出了请求。避免为验证提示文案等待连接失败及重试；不改变产品的连接超时或重试行为。
- 数据库持久化和设置快照夹具按需创建每用例引擎，清理时仅释放已创建实例，消除 6 次无用建库。用例隔离、真实持久化、测试规模和断言保持不变；时间序列测试没有改动。

**验证**：Windows Debug 下，同一组 4 条 HTTP 用例修复前累计约 115.0 秒、修复后约 5.7 秒；随后 `dotnet test tests\VelaShell.Plugin.Ai.Tests\VelaShell.Plugin.Ai.Tests.csproj -c Debug --no-build --no-restore --nologo --filter 'FullyQualifiedName~ProviderSetupViewUiTests|FullyQualifiedName~BridgeAgentRunnerTests'` 整组 213/213 通过，0 跳过。`dotnet test tests\VelaShell.Infrastructure.Tests\VelaShell.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~SonnetDbPersistenceTests|FullyQualifiedName~PluginTimeSeriesTests|FullyQualifiedName~SettingsSnapshotTests'` 前后均 60/60 通过，0 跳过；6 条直接受影响用例累计从 1.487 秒降至 0.590 秒，单次测量含环境波动。`git diff --check` 通过。只改测试夹具，不涉及用户行为或开发流程契约，无需同步手册或 velashell-docs。

两份改动涉及的测试工程均执行 `dotnet build <工程路径> -c Debug --no-restore --nologo -warnaserror -v quiet` 并通过，0 警告、0 错误；AI 工程出现 MSBuild 服务器连接不可用的信息后，已自动回退到进程内构建成功。

**边界**：未执行本轮修改后的远端 CI、Linux/macOS 测试或完整解决方案测试。FTP 断线状态三条本地基线均通过、约 13 秒，未贸然缩短产品默认超时或更改该夹具。消除无效等待不等于远端总时长会减少同样的累计秒数，也不能据此保证流水线恢复到 7 分钟。

## 285. 2026-10-30 Windows Key 行刷新与 Ubuntu 连续保存测试竞态

[CI 40932299892](https://github.com/joesdu/VelaShell/actions/runs/40932299892) 的 macOS 作业成功，Windows 与 Ubuntu 各有一条测试失败，均不在上一节修改的用例内。

- Windows 的 `BaseUrlToAnotherOrigin_ClearedStatusReplacesThePromptWhileAnotherKeyIsBeingEdited` 暂停在设置写盘阶段，但此时机密已经删除；一秒一次的 Key 行刷新会移除第二行，随后点击编辑抛出 `Sequence contains no matching element`。在旧用例暂停后泵 1.2 秒，已在本机复现同一异常与调用点。改用已有 `DelayedSecrets` 在删除主槽机密之前暂停，跨过一次刷新周期再编辑第二行，之后释放保存；继续严格断言换源清除提示与两把 Key 均已删除。
- Ubuntu 的 `ConsecutiveSaves_AreEachUploaded` 在等待第二次上传时实际已记录三次上传。原夹具直接覆盖写文件，截断、写入可产生多个 Changed 事件；下一次写入又可能赶上上一轮上传的收尾循环，不能据一次逻辑保存假定恰好一个文件事件。改用已有的「临时文件写完后原子替换」夹具，让 watcher 只看到完整文件的替换。累计上传次数仍严格等于 1、2、3，最终内容仍严格按 first、second、third 排列；没有放宽数量断言。覆盖写、防抖合并和上传收尾的既有用例继续保留。
- 只修复测试夹具及同步阶段，不调整产品实现、超时、重试或 CI 工作流，不涉及手册与 velashell-docs 契约同步。

**验证**：Windows Debug 下，下列最终验证均通过，0 跳过：

- `dotnet test tests\VelaShell.Plugin.Ai.Tests\VelaShell.Plugin.Ai.Tests.csproj -c Debug --no-restore --nologo -warnaserror --filter FullyQualifiedName~BaseUrlToAnotherOrigin_ClearedStatusReplacesThePromptWhileAnotherKeyIsBeingEdited -v quiet`：构建及原失败用例通过；随后以 `--no-build` 独立进程重复 5 轮均通过。
- `dotnet test tests\VelaShell.Plugin.Ai.Tests\VelaShell.Plugin.Ai.Tests.csproj -c Debug --no-build --no-restore --nologo --filter FullyQualifiedName~SettingsViewUiTests`：198/198 通过。
- `dotnet test tests\VelaShell.Tests\VelaShell.Tests.csproj -c Debug --no-restore --nologo -warnaserror --filter FullyQualifiedName~RemoteEditSessionManagerTests -v quiet`：构建及整组 17/17 通过；连续保存用例以 `--no-build --filter FullyQualifiedName~ConsecutiveSaves_AreEachUploaded` 独立进程重复 10 轮均通过。

未在本机运行 Linux/macOS、完整解决方案或 Docker/Interop/发布验证；Ubuntu 的原始额外上传在本机基线单次运行未复现，跨平台结果需由新提交的 CI 确认。

## 286. 2026-10-30 主窗口 AI 助手专属区域

- 第一方 AI 聊天由普通文档标签迁至主窗口最右侧专属区域，不改变当前文档或资源管理器的位置。支持拖动宽度、固定占位、取消固定后悬浮自动隐藏，以及临时折叠和隐藏；通过标题栏 AI 助手按钮或命令面板重新激活时复用原内容。悬浮区移入展开、移出延迟收起，打开下拉菜单或拖宽时保持展开；窄窗口自动使用浮层，恢复窗口宽度后沿用固定偏好。
- 宿主保存 AI 宽度及固定偏好，窗口/会话记忆关闭时仍保存；这些值归为本机运行状态，设置导出不携带、导入保留本机值、设置修改审计忽略。AI 专属区与独立窗口分别复用句柄，并串行处理重复打开；折叠或隐藏不会关闭会话，插件停用仍正常释放面板。
- AI 不再读取旧百分比宽度或向 SDK 写入宽度；通用 SDK 分栏宽度契约保留给其他插件，旧 AI 配置成员仅作兼容保留。服务器选择框收窄并省略长名称，为右侧操作按钮留空间；五语言命令名称同步改为打开助手。
- 中英用户手册及 velashell-docs 的主窗口规格、插件 UI 扩展、SDK 参考同步。

**验证**：Windows Debug 下，最终 `--no-restore -warnaserror` 定向验证为宿主布局/生命周期/本地化 35/35、AI 面板与语言表 10/10、设置分类及导入导出 23/23，均 0 跳过；此前 AI 面板、设置 UI 与设置存储相关 366/366 通过。覆盖左右资源侧栏、实际指针拖宽、悬浮菜单保护、窄窗口边条展开、隐藏恢复与偏好保存。开发文档 84 个相对文件链接有效，两仓 `git diff --check` 通过。未执行跨平台实机 GUI、完整解决方案或发布验证。

### §286 补充：服务器框对齐与最小显示宽度

服务器选择框改为贴左对齐，避免 MaxWidth 配合 Stretch 时在宽单元内居中；专属区标题恢复星光图标。除拖宽偏好已有的 300px 下限外，实际显示宽度也保持至少 300px，避免可用空间约束将其压至零。扩展既有用例，覆盖宽窄面板贴左、真实指针拖过最小边界及零可用空间。Windows Debug 下宿主区域 5/5、AI 面板 4/4 定向测试均通过，0 跳过，构建启用 `-warnaserror`。中英手册与交互规格同步；未执行实机 GUI 或跨平台验证。

### §286 补充：仅 AI 图标触发悬浮展开

将自动隐藏模式的悬浮展开入口收窄至边条中的 AI 图标按钮，鼠标移入边条空白处不再打开助手；从图标进入聊天内容仍保持展开，移出区域延迟收起及下拉菜单保护保持。窄窗口临时浮层使用同一入口。扩展既有 Headless 用例覆盖空白处、图标和进入内容的连续鼠标操作，Windows Debug 定向 5/5 通过，0 跳过，构建启用 `-warnaserror`。中英手册及交互规格同步。

## 287. AI 生成图片直接复制到剪贴板

- 聊天图片右下角新增独立复制图标，与保存、查看大图并列；大图窗口底部新增「复制图片」按钮，窄窗口工具栏自动换行。
- 两个入口共用原图复制路径，从图片存储读取原始字节，以系统图片格式写入所在窗口的剪贴板；不使用聊天缩略图或文件路径，无需先保存文件。剪贴板按需独立解码，不引用预览持有的 Bitmap，关闭聊天或预览后仍可读取。状态栏与工具提示反馈结果，原图缺失时保留原剪贴板并显示错误。
- 复制图标随插件自身资源提供，隔离窗口不依赖宿主图标；新增文案五语言齐备，同步中英用户手册及 velashell-docs 的 AI 图片交互规格。

**验证**：Windows Debug 下，`--no-restore -warnaserror` 定向图片操作、预览缩放/生命周期、剪贴板粘贴与语言表测试 14/14 通过，0 跳过。新增用例覆盖两个复制入口、关闭视图后的重复读取及每次独立位图、原图丢失不覆盖已有剪贴板。Headless 使用模拟图形/剪贴板，未验证原生剪贴板跨应用粘贴或真实解码像素；实现读取原始图片字节。开发文档14个相对文件链接有效，两仓 `git diff --check` 通过。未运行完整解决方案、实机 GUI 或跨平台验证。

## 288. AI 工具配置入口下移

- 删除聊天顶栏的工具配置按钮，仅在输入框及发送按钮下方状态行最右侧保留入口，改用插件自带的 wrench 扳手图标，与审批选择器保持同一中心线；继续打开原独立工具配置窗口。
- 保留状态行固定高度，为工具按钮预留宽度，用量文字与过长审批名称省略，避免 300px 窄面板中英文审批选项挤走按钮。中英用户手册及 velashell-docs 交互规格同步。

**验证**：Windows Debug 下，`--no-restore -warnaserror` 定向 4/4 通过，0 跳过，覆盖无宿主图标资源、输入区域结构、审批框显隐不跳高、800px/300px 长用量下按钮靠右且位于发送按钮下方，以及工具配置窗口打开与随面板关闭。未进行实机 GUI 或跨平台验证。

## 289. 紧凑聊天输入区与模型弹层

- 多行输入区下方左侧保留附件「＋」，模型和当前思考档位合为紧凑入口；通过原生按钮、弹层与列表选择模型，显示当前项标记与供应商信息，思考选择移入模型弹层。300px 窄面板下过长模型名省略，保留思考摘要与发送入口。
- 发送改为向上箭头图标，保留本地化提示与辅助名称、回复期间排队补充及独立停止操作。聊天/计划/Agent 模式移入外部状态区上行，下行继续放审批、用量、上下文条及扳手；固定预留两行高度。思考覆盖、切换模型清除、设置更新和故障转移行为沿用原规则。未增加语音输入。
- 同步中英用户手册及 velashell-docs 交互规格。

**验证**：Windows Debug 下启用 `-warnaserror`，聊天面板回归 335/335 通过；焦点调整后键盘及跨视图定向 4/4、思考选择回归 5/5 通过，均 0 失败、0 跳过。覆盖模型列表键盘选择、嵌套思考下拉关闭与焦点恢复，以及临时思考档位行为。开发文档 14 个相对链接有效。Headless 验证不代表实机 GUI 或跨平台验收。

## 290. 恢复发送旁模式选择并优化模型菜单

- 聊天/计划/Agent 选择移回输入框内发送按钮旁，与发送、停止按钮共用同一水平线；容器外的审批、用量与扳手状态区恢复为固定单行。
- 模型入口增加星光图标与轻量分隔，模型弹层采用圆角菜单行、悬停反馈与选中勾，移除默认列表边框；思考强度改为底部同行选择，说明通过工具提示提供。保留供应商身份、临时思考覆盖、键盘选择及焦点恢复。中英手册和开发规格同步。

**验证**：Windows Debug 下 `--no-restore -warnaserror` 定向 11/11 通过，0 失败、0 跳过，覆盖 300px 宽度下三种模式及忙态操作可见性、模式与发送对齐、单行状态区、弹层鼠标和键盘选择、思考覆盖行为。未进行实机 GUI 或跨平台验收。

## 291. 补齐思考档位并将工具配置并入模型操作行

- 思考选择在保留默认、关闭及 low/medium/high 的基础上追加 xhigh/max，聊天面板与模型设置均提供五语言文案，原枚举值保持兼容。OpenAI Chat Completions/Responses 准确发送所选档位；max 通过原生选项发送，Responses 的关闭存储选项复用原生请求，避免覆盖思考配置。Anthropic 对应预算为 32768/65536 token，沿用输出上限裁剪与协议下限规则。默认仍不设置请求档位，由服务端决定；可用档位由服务端与模型决定。
- MCP 扳手移至模型入口紧邻右侧，保留独立工具设置窗口；审批与用量仍处于输入框外的固定单行。模型、工具、模式及发送/停止按钮统一与附件「＋」同高（22px），调整思考强度不会持续高亮模型入口。同步中英用户手册及开发规格。

**验证**：Windows Debug 下 `--no-restore -warnaserror` 分批定向回归 49/49、9/9、3/3 通过，均 0 失败、0 跳过，覆盖两套 OpenAI 协议的 xhigh/max 实际 HTTP 请求及 store:false、Anthropic 预算、配置恢复与设置保存/探活、临时覆盖、五语言、300px 下三种模式及忙态同高布局、800px/300px 扳手紧邻模型入口、工具窗口激活和审批显隐。开发文档 14 个相对链接有效。未进行实机 GUI 或跨平台验收。

## 292. 2026-10-30 连接对话框：工作台变体与「先选类型 → 下一步」步骤

**起因**：数据库插件（`velashell.sql`）把 MySQL / PostgreSQL / SQL Server / Oracle / SQLite 声明成同一个工作台的五个变体（`WorkspaceDescriptor.VariantKey` / `Variants`），但宿主一直没有读变体：切到 PostgreSQL 时端口仍是 3306，SQLite 照样要填端口和账号；方言下拉作为插件字段只能排在主机 / 端口 / 凭据之后，落在表单最底下。参照常见数据库客户端的连接类型网格，改成先选类型、再填表单。

**变体读取**：

- `PluginConnectionForm` 带上 `VariantKey` / `Variants`，`ForVariant` 按变体覆盖默认端口、主机 / 用户名 / 密码标签与能力位（`Features` 整体替换，照 SDK 语义）。端口只在当前值是某个变体或页签的默认值时跟随，按「算目标」处理，不先改写再还原；手填的端口不动；切到另一个插件页签时不沿用上一个页签的变体端口；编辑已存配置装载时不跟随。
- 新增 `WorkspaceVariants.EffectiveFeatures` / `AllowsAnonymous`：配置里缺变体键时回落到字段默认值，与 launcher 的取值链一致。`MainWindowViewModel` 的匿名判定与隧道判定两处改走它，`NoCredentials` 视作匿名。

**类型选择步骤**：

- 出卡片的条件由宿主判定：`VariantKey` 指向的字段是 Choice、不隐藏、不可手输、至少两个候选。只在新建且类型尚未确认时进入卡片阶段；Redis / S3 / Telnet 等没有变体的连接类型行为不变。
- 卡片阶段：分页条与表单收起，右侧按 Choices 顺序排磁贴（名字取 Choice 的 Label，图标暂用工作台图标），预选字段当前值。单击选中；双击或在卡片上按 Enter 选中并进入下一步；Space 只选中。页脚为 [取消][下一步]，取消即关闭对话框。
- 表单阶段：顶部一行「类型 X」，变体下拉那一行不再显示；新建时页脚多一个 [上一步]，编辑时表头带「更换」。上一步 / 更换不清除任何已填值；只有新建且类型尚未确认（例如从别的协议切回来）时，下一步才强制按变体刷新一次端口。
- 磁贴规格写进 `DESIGN.md` §5.2「Type tile」；5 个新文案键五份 resx 齐全；`manual.md` / `manual.en.md` §4.1 同步。

**插件字段分页按页键**：内部模型 `PluginFormPage` 与字段的 `PageKey` 取代「`IsAdvanced` 二分」，页签条可以在「常规」与「高级」之间插入插件自定义页：页键优先于 `IsAdvanced`；未知页键回落默认位置；一页字段全不可见时页签收起，停在该页会落回「常规」；自定义页不做徽标、改动圆点与自动展开；没有自定义页时整组隐藏。本节两个 `From()` 都不填页，现有表单行为不变。数据来源是 velashell-plugin-sdk 的新契约 `WorkspaceDescriptor.Pages` / `ProtocolSettingField.Page` / `WorkspaceVariant.Icon`（可用版本待发版），SDK 发版后另开 PR 接上。

**验证**：Windows Debug 下 `dotnet build VelaShell.slnx -c Debug --no-incremental --nologo -warnaserror` 0 警告、0 错误；`dotnet test tests/VelaShell.Tests -c Debug --no-build --nologo` 3229 通过 / 0 失败 / 19 跳过（改动前 3181 / 0 / 19）。新增 `ConnectionProfileTypePickerTests`、`ConnectionProfilePluginPagesTests`、`WorkspaceVariantCredentialTests`、`WorkspaceVariantsTests`，以及 `ConnectionProfileViewUiTests` 中的 headless 用例。端口跟随、装载不跟随、匿名判定、强制刷新条件、取消、卡片上 Enter 等关键判定都做过变异，均能使对应用例失败。

**边界**：只在 headless 下渲染与截图，未在真实应用里操作过；未运行 Linux / macOS 与完整解决方案测试。五张卡片暂时共用工作台图标；「数据库 / SSL」等插件自定义页要等 SDK 发版后接入。

## 293. 2026-10-30 连接对话框：SDK 抬到 2.1.5，接上插件自定义分页与变体图标

**起因**：§292 的分页机制与类型卡片都已就位，但两个 `From()` 不填分页、卡片共用工作台图标，因为 velashell-plugin-sdk 的对应契约当时还没发版。SDK 2.1.5 已发布到 nuget.org，带着这四个成员：`ProtocolSettingPage(Key, Label)`、`ProtocolSettingField.Page`、`WorkspaceDescriptor.Pages`、`WorkspaceVariant.Icon`。

**接上了什么**：

- `src/Directory.Packages.props` 与 `tests/Directory.Packages.props` 的 `VelaShell.PluginSdk`（测试侧连同 `VelaShell.PluginSdk.Testing`）从 2.1.4 抬到 2.1.5。`plugins/VelaShell.Plugin.Ai` 用不到这四个成员，`minSdkVersion` 与包版本不动。
- `PluginConnectionForm.From(WorkspaceDescriptor)` 把 `WorkspaceDescriptor.Pages` 映射成内部的 `PluginFormPage` 表（顺序不变），把各字段的 `Page` 收成「字段键 → 页键」字典；没声明分页时两者都记为 null，与没有自定义页同一个值。页键在不在分页表里、是不是宿主自己的 general / advanced，仍只在 §286 的 `PageKeyFor` / 建页签处判（未知页键按没填处理）；字段键重复只认第一次，不抛异常。`From(ProtocolDescriptor)` 不变：`ProtocolDescriptor` 没有分页列表，文件协议字段上的 `Page` 被忽略，照旧按 `IsAdvanced` 落位。
- 类型卡片的图标改为：同 `Value` 变体的 `WorkspaceVariant.Icon` → 工作台描述符的图标 → 界面画通用插件图标。
- 代码注释里「第一期 / 等 SDK」的说法按现状改写；`PluginFormOverride` 测试接缝保留，用于未知页键、宿主键、重复键这类边界情况。
- `manual.md` / `manual.en.md` §4.1 补一句：插件可以在「常规」与「高级」之间加自己的页签，类型卡片显示插件给各类型配的图标。

**验证**：Windows Debug 下 `dotnet build VelaShell.slnx -c Debug --no-incremental --nologo -warnaserror` 0 警告、0 错误；`dotnet test tests/VelaShell.Tests -c Debug --no-build --nologo` 按 `ConnectionProfilePluginPagesTests`、`ConnectionProfileTypePickerTests`、`ConnectionProfileViewUiTests`、`ConnectionProfileViewModelTests`、`WorkspaceVariantCredentialTests`、`WorkspaceVariantsTests` 与 `LocalizedKeyUsageTests`、`UnusedLocalizedKeyTests`、`HeadlessDispatchUsageTests` 过滤，145 通过 / 0 失败 / 0 跳过。新增两条：`From_WorkspaceDescriptor_MapsPagesAndFieldPages`（`From()` 的映射、文件协议忽略 `Page`、不经测试接缝的真实装载出两个自定义页签、页键优先于 `IsAdvanced`）与 `TypeCards_UseTheVariantIcon_FallingBackToTheDescriptorIcon`。两处变异 —— `From(WorkspaceDescriptor)` 不映射 `Pages`、卡片不取变体图标 —— 分别使这两条用例失败。

**边界**：只在 headless 下验证，未在真实应用里操作过；未运行 Linux / macOS 与完整解决方案测试。眼下还没有插件声明 `Pages` 或变体图标，现有插件的对话框外观不变；数据库插件声明「数据库」「SSL」两页与各方言图标后，用户才会看到。

## 294. 2026-10-30 macOS CI：修正远程编辑状态等待竞态与 AI 传输卡片固定等待

**证据**：运行 `41098306406` 的 macOS TRX 中，`Session_KeepsItsMarkerInStep_WithWhatTheRemoteHas` 报「等了 10 秒」，实际仅运行 28 毫秒。`WaitUntilAsync` 在循环条件中观察到干净状态退出，又在断言中读取一次；其间 FSEvents 投递初始文件事件，状态变脏，于是立即误报超时。较早运行 `41095953103` 的传输卡片用例则在恢复监控后仍读到「无法确认进度」：界面以 250ms 计时器合并更新，测试固定等约 300ms，调度余量不足。

**修正**：远程编辑等待函数观察到条件成立就返回，按单调时钟限定 10 秒，超时附带条件表达式；成功上传的断言使用等待时取得的标记快照，避免重新读取会变化的标记。AI 增加带 15 秒上限的条件等待，持续泵 UI 调度器；传输卡片分别等待失联、恢复、成功和撤回状态，并同时检查绑定到同一传输的两张卡片。

**扩大回归发现的产品竞态**：完整 ChatPanel 与传输注册表回归连续两轮都是 379 通过、1 失败，失败均在撤权卡片等待。后台完成通知可能在撤权之后、UI 刷新之前覆盖合并队列中的撤权状态，导致卡片根本没有机会隐藏信息。`TransferBinding` 现在在队列内也把撤权当作不可逆状态，丢弃后到快照，与 `ToolCard` 原有的隐藏逻辑保持一致。已有用例明确投递「撤权 → 迟到的完成快照 → 泵 UI」：产品修正前稳定失败，修正后作为回归验证。没有跳过 macOS 测试或更改 CI 流程。

**验证**：Windows Debug `-warnaserror` 下外部编辑分类 26/26，通过故障用例独立重复 10 轮；AI 撤权竞态回归修正前稳定失败、修正后通过，完整 ChatPanel 与传输注册表 380/380 通过，故障用例再次独立重复 10 轮通过，均无跳过。GitHub 在本次 PR 创建时报告 Actions 服务降级，初次三个平台作业均排队；远端三平台结果以最终提交的 PR Checks 为准，此记录没有把排队记作通过。

**文档**：`manual.md` / `manual.en.md` 同步补充撤权后原传输卡不再被迟到通知恢复信息的说明。修正恢复 velashell-docs `plugins/11-automation-and-ai.md` §1.6–1.7 既有的撤权后隐藏元数据约定，接口、配置和构建流程均未改变。

## 295. 2026-10-31 连接对话框：主机留空提示跟着变体标签走，没有端点的变体收起跳板主机

**起因**：§292 / §293 合入后，数据库插件验收时发现的两处：

- 主机留空的提示跟着变体的主机标签说：当前变体给了 `WorkspaceVariant.HostLabel` 时，`HostError` 取新键 `Profile_ErrHostRequiredFormat`（SQLite：「请填写数据库文件」，五份 resx 齐）；没有变体、或变体没给 `HostLabel` 时仍是 `Profile_ErrHostRequired`。描述符级的标签（S3 的「服务端点」）不在此列。换类型时补发 `HostError` 通知。
- 没有端点的表单收起「跳板主机」那一行：`ShowJumpHostField = ShowPortField`，变体或描述符声明 `NoEndpoint` 时为 false，串口同理；SSH / SFTP / FTP、Redis、S3、Telnet 照旧显示。只收显示，已存的跳板值不清，切回有端点的类型时原样回来；连接时建不建隧道仍按 `WorkspaceVariants.EffectiveFeatures` 判定，未改。
- 用例：`ConnectionProfileTypePickerTests` 三条（提示跟标签走 / 跳板行随端点收起且值保留 / 无变体工作台不受影响），`ConnectionProfileViewUiTests` 一条 headless（断言落在绑定跳板列表的下拉及其所在行上）。变异「提示不跟标签走」「跳板行不看端点」「删掉 AXAML 那一行的 IsVisible 绑定」均使对应用例失败。`manual.md` / `manual.en.md` §4.1 同步一句。

## 296. 2026-10-31 PR #698：修复基线测试代码导致的 CI 构建失败

**根因**：CI 运行 `41167937874` 的构建报 6 个错误，本地以同一条 Debug / `-warnaserror` 命令复现。错误由基线提交 `58d44e1c` 的断言改写引入：5 处 `Assert.AreEqual` 的实际值是异常属性 `ExpectedBytes`，MSTEST0017 按属性名前缀误判参数颠倒；另有一处 `Assert.AreSequenceEqual` 的集合表达式被目标类型化为不可构造的非泛型 `IEnumerable`，报 CS9174。均早于 #698 的连接表单改动。

**修正**：期望字节数使用明确命名的 `expectedBytes` 参数或局部变量，保持期望值在前、异常属性在后的断言语义；带前缀的预期内容先保存为 `byte[]` 再比较。不关闭分析器，不改包版本、CI 或产品行为。

**验证**：Windows / .NET SDK `11.0.100-rc.3.26522.148`，`dotnet build VelaShell.slnx -c Debug --nologo -warnaserror` 通过，0 警告、0 错误。`dotnet test VelaShell.slnx -c Debug --no-build --nologo` 按 `StagedRelaySourceTests`、`FtpFileServiceIntegrationTests`、`ConnectionProfileTypePickerTests`、`ConnectionProfileViewUiTests`、`LocalizedKeyUsageTests`、`UnusedLocalizedKeyTests`、`HeadlessDispatchUsageTests` 过滤：205 通过、0 失败、0 跳过（Core 45、Infrastructure 94、宿主 66）。未运行完整测试集或 Linux / macOS 构建与测试。

**文档**：此次只修复测试编译，现有行为、接口、配置和构建流程不变，无需修改用户手册或 velashell-docs。

## 297. 2026-10-31 PR #698：修复 sudo 状态测试取消探针的竞态

**根因**：§296 后三平台编译均通过，运行 `41195289699` 的 Linux 作业唯一失败为 `RunningIsReportedBeforeAnyWaitAfterTheStartMarker`，耗时恰为测试宿主的 25 秒死线。密码等待与探针分别在同一令牌上注册取消回调；`Task.Delay` 被取消后的续体可能先执行 `await using` 的清理，注销尚未执行的探针。这样即使执行器已发出 `Running`，探针仍未记录状态、也未取消执行死线，最终超时误报。用受控的后注册回调唤醒等待续体，已确定性复现取消探针被跳过的机制。

**修正**：密码请求改为等待探针自己的 `TaskCompletionSource`；探针先记录状态、触发执行取消，再完成等待，确保注册不会被提前释放。保留原来的 `Running` 先于撤销等待的断言，不改生产状态机、超时或 CI 配置。

**验证**：Debug / `-warnaserror` 构建及 sudo 状态测试 7 条通过；Windows 按 CI 调度配置运行 SSH 非 Interop 测试，1450 条通过、0 失败、0 跳过；修正后目标用例在临时诊断程序中连续执行 500 次通过。临时把生产代码的 `Running` 通知移到密码取消之后，修正后的用例立即失败（约 0.3 秒），还原顺序后通过，确认测试仍能拦住真实顺序回归。

**文档**：只修正测试同步，不改变用户行为、接口、配置或构建流程，无需修改用户手册或 velashell-docs。
