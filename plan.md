# VelaShell 项目进展与参考文档

> **这份文件记录「已经发生的事」** —— 已完成的工作、当前架构、关键文件索引，以及每一次
> 「为什么这么改」的来龙去脉。它是开发跟进的事实来源。
>
> **还没发生的事在 [`feature-plan.md`](feature-plan.md)** —— 待办、候选特性、已决策不做的清单。
> 两份文件的分工是硬的：一件事做完了，就从 `feature-plan.md` 划掉、在这里补一节；
> 一件事还没做，就不要在这里留 TODO。

## 🏷️ 状态标识

| 标识 | 含义 |
| :---: | --- |
| ✅ | **已完成** —— 已落地、有测试、可验收 |
| 🚧 | **部分完成** —— 主线可用，仍有明确缺口（缺口逐条在 `feature-plan.md`） |
| 📖 | **参考资料** —— 描述当前状态或约定，不是「一件要做的事」 |
| ⏳ | **待办** —— 已确认要做，未开工或进行中 |
| 💡 | **候选** —— 想法已记录，是否要做待评估 |
| ❌ | **确认不做** —— 与架构或产品决策冲突，附理由，别再提 |
| ⚠️ | **已知问题 / 坑** —— 会绊到人的地方 |
| 📄 | **文档待同步** —— 代码已改，velashell-docs 还没跟上 |

## 🗺️ 全文索引

### 一、当前状态与约定（长期有效，随代码更新）

| § | 状态 | 内容 |
| :---: | :---: | --- |
| [1](#-1-技术栈现状) | 📖 | 技术栈现状 —— 运行时、UI 框架、SSH 栈、持久化、打包、测试框架的当前版本 |
| [2](#-2-解决方案分层) | 📖 | 解决方案分层 —— 六个 src 项目 + 插件 + 测试的职责与依赖方向 |
| [3](#-3-终端引擎核心替换了坏掉的-avaloniaterminal) | ✅ | VT 终端引擎 —— 解析器、屏幕模型、仿真器、十种 profile、自绘渲染 |
| [4](#-4-ssh--pty) | ✅ | SSH / PTY —— 桥接循环、实时改窗、失败不崩、连接持久化 |
| [5](#-5-停靠--分屏veladock已替换-dockavalonia) | ✅ | VelaDock —— 模型层 / 控件层 / 拖拽分屏 |
| [6](#-6-ui--视图与设置) | ✅ | UI / 视图与设置 —— 自绘窗口壳、命令面板、十二页设置中心 |
| [7](#-7-测试已全量迁移到-mstest) | 📖 | 测试 —— 项目构成、规模、MSTest 迁移约定与断言风格 |
| [8](#-8-关键约定--已知坑) | 📖 | ⚠️ 关键约定 / 已知坑 —— SonnetDB、Avalonia 12、构建的几处必读 |

### 二、进展记录（按时间倒推读，最新在最下）

| § | 状态 | 日期 | 内容 |
| :---: | :---: | --- | --- |
| [9](#-9-2026-07-08-完成情况6-次提交514-测试全绿) | ✅ | 2026-07-08 | SonnetDB 存储层、侧边栏快速连接、新建连接弹窗、两步验证、设置窗口九页 |
| [10](#-10-历史待办清单已迁出) | 🚧 | 2026-07-09 | 设置项接线状态复盘（**待办部分已迁往 `feature-plan.md`**） |
| [11](#-11-设计稿分析已记录的问题供实现时对照) | 🚧 | 2026-07-09 | 设计稿与实现的出入清单 |
| [12](#-12-与主流终端工具的功能缺口已迁出) | 🚧 | 2026-07-09 | 对照 Xshell / MobaXterm / Tabby / WindTerm 的缺口（**未完成项已迁往 `feature-plan.md`**） |
| [13](#-13-2026-07-11--07-12-批次设置审计整改--四个新特性) | ✅ | 07-11~12 | 设置审计整改、主机指纹三选项、Gist 云同步、会话录制回放、双许可 |
| [14](#-14-多语言2026-07-12-全量补齐c-09-一并完成) | ✅ | 07-12 | 五语言全量补齐 + 实时切换的两处根因修复 |
| [15](#-15-版本与发布2026-07-12) | ✅ | 07-12 | 版本号单一来源、一键发布脚本、CI/CD |
| [16](#-16-2026-07-13--07-14-批次veladock-合并原生窗口壳终端侧栏安装包工程化) | ✅ | 07-13~14 | VelaDock 落地、自绘标题栏 + Snap Layouts、终端侧栏、集中式包管理 |
| [17](#-17-2026-07-批次ssh-传输层迁移zmodemsftp-双栏) | ✅ | 2026-07 | SSH.NET → Tmds.Ssh、ZMODEM、SFTP 双栏、net10 → net11 |
| [18](#-18-2026-07-24--08-14-批次盘点2026-08-14-补记此前均已落地但未入本文件) | ✅ | 07-24~08-14 | 插件系统 v1 + AI 插件、资源监视、路由追踪、连接诊断、会话导入、FTP、全局代理 |
| [19](#-19-2026-08-30-隧道功能完善计量转发--流量统计--断线自动恢复--端口冲突预检) | ✅ | 08-30 | 隧道：宿主接管计量数据面、流量统计、断线自愈、端口冲突预检 |
| [20](#-20-2026-08-30-消息中心侧边栏铃铛) | ✅ | 08-30 | 消息中心 —— 边界、资讯源契约、快捷跳转、一个死开关 |
| [21](#-21-2026-08-31-补全弹层关不掉315) | ✅ | 08-31 | 命令补全弹层关不掉（#315） |
| [22](#-22-2026-08-31-资讯源默认订阅官方源) | ✅ | 08-31 | 资讯源默认订阅官方源（含 PRIVACY 的如实修订） |
| [23](#-23-2026-08-31-消息中心可拖动加大字号动作靠右用户反馈) | ✅ | 08-31 | 消息中心的可拖动 / 字号 / 动作位置 |
| [24](#-24-2026-08-31-会话树状态卡在连接中321) | ✅ | 08-31 | 会话树状态卡在「连接中」（#321） |
| [25](#-25-2026-08-31-具名主题九套配色--终端配色配对) | ✅ | 08-31 | 具名主题：种子色 + 派生令牌，九套配色与终端配色配对 |
| [26](#-26-2026-08-31-主题命名收敛--跟随主题不再是隐式状态用户反馈) | ✅ | 08-31 | 主题命名收敛，「跟随主题」显式化 |
| [27](#-27-2026-08-31-再补三套主题one-dark--one-light--sakura用户反馈) | ✅ | 08-31 | 再补 One Dark / One Light / Sakura（主题 9 → 12） |
| [28](#-28-2026-08-31-切主题发卡用户反馈感觉有点卡是错觉吗) | ✅ | 08-31 | 切主题发卡：全树重解析 → 一次整格替换 |
| [29](#-29-2026-09-01-命令行装的插件被判收据缺失用户反馈) | ✅ | 09-01 | 命令行装的插件被判「收据缺失」 |
| [30](#-30-2026-09-02-ai-插件自定义供应商也能自动拉模型清单用户反馈) | ✅ | 09-02 | AI：自定义供应商也能自动拉模型清单 |
| [31](#-31-2026-09-02-ai-插件左栏模型列表可折叠用户反馈) | ✅ | 09-02 | AI：左栏模型列表可折叠 |
| [32](#-32-2026-09-02-资源管理器会话树改成摊平的平列表用户反馈) | ✅ | 09-02 | 会话树改成摊平的平列表 |
| [33](#-33-2026-09-02-协作接入im-桥接飞书钉钉telegram企微-对外-mcp-服务端) | ✅ | 09-02 | **协作接入**：IM 桥接（飞书/钉钉/Telegram/企微）+ 对外 MCP 服务端 |
| [34](#-34-2026-09-02-协作接入的配置流程返工用户反馈要填一堆文本框) | ✅ | 09-02 | 协作接入配置流程返工：配对码 / 一键放行 / 当场验 |
| [35](#-35-2026-09-03-插件能按已保存配置自己连一台机器sdk-202-的宿主侧落地) | ✅ | 09-03 | 插件可按已保存配置开会话（SDK 2.0.2 宿主侧） |
| [36](#-36-2026-09-03-agenttoolbox-接上开会话机器人不必再回你先去连一台) | ✅ | 09-03 | AgentToolbox 接上开会话 |
| [37](#-37-2026-09-04-每条连接各配一条认证后执行命令用户反馈) | ✅ | 09-04 | 每条连接各配一条「认证后执行命令」 |
| [38](#-38-2026-09-04-ftp--ftps-可配默认打开路径用户反馈) | ✅ | 09-04 | FTP / FTPS 可配「默认打开路径」 |
| [39](#-39-2026-09-04-文档型连接的树状态关掉一个别把还活着的另一个也熄了用户反馈) | ✅ | 09-04 | 文档型连接的树状态 |
| [40](#-40-2026-09-04-数字输入框删空后别再甩一句转换异常用户反馈) | ✅ | 09-04 | 数字输入框删空后的转换异常 |
| [41](#-41-2026-09-05-对外-mcp-的允许操作的服务器改成勾选与连接列表同一套用户反馈) | ✅ | 09-05 | 对外 MCP 的「允许操作的服务器」改成勾选 |
| [42](#-42-2026-09-06-开一下-sftp-面板别把设置里的开关也给拨了377) | ✅ | 09-06 | 开 SFTP 面板别拨设置开关（#377） |
| [43](#-43-2026-09-06-滚动条悬停别等半秒新建连接别一进来就是粗条378) | ✅ | 09-06 | 滚动条悬停延迟与初始粗条（#378） |
| [44](#-44-2026-09-06-exit-之后不该被自动连回来sftp-通道跟着-ssh-一起收383) | ✅ | 09-06 | `exit` 之后不该自动重连（#383） |
| [45](#-45-2026-09-07-新开标签页时上一个会话的-sftp-面板要立刻收起385) | ✅ | 09-07 | 新开标签立刻收起旧 SFTP 面板（#385） |
| [46](#-46-2026-09-07-连接慢的时候屏幕上必须有东西在动385-反馈) | ✅ | 09-07 | 连接慢时的加载回执（#385 反馈） |
| [47](#-47-2026-09-07-后台任务浮层是块黑砖跟哪套主题都不搭用户反馈) | ✅ | 09-07 | 后台任务浮层吃上主题 |
| [48](#-48-2026-09-07-关掉连接中的标签连接就该停下用户反馈) | ✅ | 09-07 | 关掉「连接中」的标签就该取消握手 |
| [49](#-49-2026-09-07-ci-的-ubuntu-作业偶发失败隔离插件连不上被报成激活超时) | ✅ | 09-07 | CI ubuntu 偶发失败：管道先连、Avalonia 后建 |
| [50](#-50-2026-09-08-ci-的-macos-作业偶发失败背压用例拿固定-sleep-赌线程池已经起来了) | ✅ | 09-08 | CI macOS 偶发失败：背压用例改等条件，不再赌固定 sleep |
| [51](#-51-2026-09-08-双击打开的远端文件也要自动回传编辑会话不再赌编辑器进程396) | ✅ | 09-08 | 双击打开的远端文件也自动回传；三个「打开」入口收敛到一套编辑会话（#396） |
| [52](#-52-2026-09-08-编辑器都关掉了正在编辑那一行还挂着396-反馈) | ✅ | 09-08 | 编辑器关掉后收掉「正在编辑」那一行（#396 反馈） |
| [53](#-53-2026-09-08-vs-code-关掉了那一行还挂着别拿单个进程句柄代表应用还开着396-反馈二) | ✅ | 09-08 | 别拿单个进程句柄代表「应用还开着」（#396 反馈二） |
| [54](#-54-2026-09-08-把正在编辑改成只在出问题时出现整段进程跟踪删掉396-反馈三) | ✅ | 09-08 | 「正在编辑」改成只在出问题时出现，整段进程跟踪删掉（#396 反馈三） |
| [55](#-55-2026-09-08-传输浮窗里那一组整块撤掉远程编辑从此不出现在界面上396-反馈四) | ✅ | 09-08 | 传输浮窗里那一组整块撤掉，远程编辑不再出现在界面上（#396 反馈四） |
| [56](#-56-2026-09-08-按下-ctrl-那一刻手型就该出来不该等鼠标抖一下397) | ✅ | 09-08 | 按下 Ctrl 立刻给手型，不必再抖一下鼠标（#397） |

## 📈 阶段脉络

```mermaid
timeline
    title VelaShell 主线演进
    2026-07 上旬 : SonnetDB 存储层 : 两步身份验证 : 设置中心成型
    2026-07 中旬 : VelaDock 替换 Dock.Avalonia : 自绘窗口壳 + Snap Layouts : 五语言全量补齐 : 版本与发布流水线
    2026-07 下旬 : SSH.NET → Tmds.Ssh : ZMODEM : SFTP 双栏与断点续传 : net10 → net11
    2026-08 上旬 : 资源监视 / 路由追踪 / 连接诊断 : 会话一键迁移 : FTP / FTPS
    2026-08 中旬 : 插件系统 v1 双宿主模式 : AI 助手插件 : 全局网络代理 : MSIX 商店版
    2026-08 下旬 : 隧道计量与自愈 : 消息中心与资讯源 : 具名主题 12 套
    2026-09 上旬 : 协作接入 IM 桥接 : 对外 MCP 服务端 : 插件可自行开会话 : 三平台 CI 门禁 : 偶发失败的固定 sleep 逐条换成等条件
```

## 🧭 当前基线（2026-09-07）

| 项 | 值 |
| --- | --- |
| 最新发布 | `1.5.2`（仓库内 `Directory.Build.props` 的 `0.0.1-dev` 是开发期占位，发版由 Release 标签经 `-p:Version` 覆盖） |
| 测试 | 排除 `DockerIntegration` / `CrossPlatform` 后 **3194 通过**，`dotnet build` 零警告 |
| 测试项目 | 8 个 MSTest 项目 + 1 个 BenchmarkDotNet 项目 |
| CI | `ci.yml` 三平台矩阵（windows / ubuntu / macos），push `main` 与全部 PR 触发 |
| 待办总数 | 见 [`feature-plan.md`](feature-plan.md) |

## 📖 1. 技术栈现状

> 版本号以 `src/Directory.Packages.props`、`tests/Directory.Packages.props`、
> `Directory.Build.props` 与 `global.json` 为准;下表是**当前值的快照**(2026-09-07 复核)。

| 项       | 版本/说明                                                                                                                                            |
| -------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET     | **net11.0**(2026-07 由 net10.0 切入;`global.json` 锁 `11.0.100-preview.7.26381.103` + `rollForward: latestFeature` + `allowPrerelease`。`Directory.Build.props` 对 net11 开启 `EnablePreviewFeatures` + `Features=runtime-async=on`,并 `NoWarn` 掉 CA2252/SYSLIB5007;`LangVersion=preview`) |
| UI 框架  | **Avalonia 12.1.2**(11.x → 12.0.5 → 12.1.0 → 12.1.2)                                                                                                 |
| MVVM     | ReactiveUI 24.2.0 / ReactiveUI.Avalonia 12.1.1                                                                                                       |
| 停靠框架 | **VelaDock**(`src/VelaShell/Docking/`,零第三方依赖;已替换 Dock.Avalonia,见 `velashell-docs zh/host/dock-replacement-plan.md`)                                     |
| SSH/SFTP | **Tmds.Ssh 0.24.0**(全托管 async-first;2026-07 由 SSH.NET 迁入,库类型只在 `Infrastructure/Ssh/` 出现,异常经 `TmdsSshInterop` 翻译为 `VelaSsh*Exception`) |
| FTP      | **FluentFTP 54.2.0**(MIT、零依赖;⚠️ **不要**引 FluentFTP.GnuTLS —— LGPL-2.1-only,与商业授权冲突) |
| 持久化   | **SonnetDB.Core 3.1.0 嵌入式多模型数据库**(`~/.velashell/sonnetdb`;文档集合 + 时序 measurement;旧 JSON 首次运行一次性导入;LiteDB 已移除) |
| IP 归属地 | MaxMind.Db 5.1.0(**只是 mmdb 格式读取库**,数据用的是 DB-IP Lite City / CC BY 4.0) |
| 插件契约 | **VelaShell.PluginSdk 2.0.2**(nuget.org 正式包,**不做工程引用**;版本 pin 在 `src/` 与 `tests/` 两份 `Directory.Packages.props`) |
| 打包     | 便携压缩包(zip / tar.gz,6 RID)+ `.AppImage` / `.deb` / `.rpm` / `.dmg` / MSIX;应用内自更新(GitHub Releases `latest.json`;Velopack 已移除 2026-07-17,WiX MSI 已于 `241c2a2` 移除)            |
| 依赖管理 | **集中式**:`src/Directory.Packages.props` 统一 NuGet 版本(`ManagePackageVersionsCentrally`);SourceLink.GitHub 构建期启用。⚠️ `plugins/` 下的插件**不走**中央包管理,版本写在各自 csproj |
| 测试     | **MSTest 4.4.0**(已从 xUnit 全量迁移;FluentAssertions 已移除)+ BenchmarkDotNet 0.16.0-preview.1                                                     |
| AI 栈    | Microsoft.Extensions.AI 10.9.0 · ModelContextProtocol.Core 2.2.0 · LiveMarkdown.Avalonia 2.4.0(含 Mermaid / Math / Svg 扩展)—— 均只在 AI 插件里 |

## 📖 2. 解决方案分层

```
src/
├── VelaShell/                桌面入口、DI 组合根、视图(axaml)、App 层 ViewModel、停靠、行为
├── VelaShell.Presentation/   跨层 ViewModel、连接/隧道工作流服务
├── VelaShell.Controls/       自定义控件(LucideIcon)、设计 token、内置 Cascadia Mono 字体
├── VelaShell.Terminal/       ★ VT 终端引擎 + 自绘渲染控件
├── VelaShell.Core/           领域模型、抽象契约、数据存储、SSH/SFTP/FTP 封装接口、协议引擎、本地化
├── VelaShell.Infrastructure/ Tmds.Ssh/SFTP/FTP/隧道实现、SonnetDB 持久化、插件管理与能力实现、DI 扩展
└── VelaShell.PluginHost/     隔离插件的宿主进程(命名管道 RPC,只依赖 SDK 契约)
plugins/VelaShell.Plugin.Ai/  第一方 AI 助手插件(同仓构建、同版发布;例外理由见 plugins/README.md)
tests/  8 个 MSTest 项目 + 1 个 BenchmarkDotNet 项目(见 §7)
解决方案文件:仓库根目录 VelaShell.slnx(注意:曾在 src/ 下,VS 打开后移到了根目录)
```

**依赖方向**(Core 不依赖任何 UI 框架,是这条链的底座):

```mermaid
graph RL
    App["VelaShell<br/>(桌面入口 · DI 组合根)"]
    Pres["VelaShell.Presentation"]
    Ctrls["VelaShell.Controls"]
    Term["VelaShell.Terminal"]
    Infra["VelaShell.Infrastructure"]
    Core["VelaShell.Core<br/>(无 UI 依赖)"]
    Host["VelaShell.PluginHost"]
    Sdk["VelaShell.PluginSdk<br/>(NuGet 契约)"]
    Ai["plugins/VelaShell.Plugin.Ai"]

    App --> Pres
    App --> Ctrls
    App --> Term
    App --> Infra
    Pres --> Core
    Ctrls --> Core
    Term --> Core
    Infra --> Core
    Infra --> Sdk
    Host --> Sdk
    Ai --> Sdk

    style Core fill:#2d6a4f,color:#fff
    style Sdk fill:#5a3e85,color:#fff
```

> 箭头指向被依赖方。`PluginHost` 与 `Plugin.Ai` **只**认 SDK 契约,不依赖宿主任何内部程序集 ——
> 这正是插件能跨进程、跨 ALC 而类型仍然同一的前提。

## ✅ 3. 终端引擎(核心,替换了坏掉的 AvaloniaTerminal)

彻底移除第三方 `AvaloniaTerminal 1.0.0-alpha.7`,改为手写 VT 引擎。位于 `src/VelaShell.Terminal/Emulation/` 与 `Rendering/`:

- `VtParser.cs` — Paul Williams DEC ANSI 状态机(Ground/Escape/CSI/OSC/DCS…)+ 独立 VT52 语法路径;消费 Unicode 标量,派发到 `IVtActions`。
- `TerminalScreen.cs` + `TerminalRow/TerminalCell/CellFlags/TerminalColor` — 网格、主/备屏、滚动区域(DECSTBM)、scrollback、光标、tab stops。
- `TerminalEmulator.cs` — 仿真器大脑(实现 `IVtActions`):SGR(16/256/truecolor)、光标/擦除/插删行列、模式(DECAWM/DECOM/应用键盘/插入/括号粘贴/鼠标跟踪…)、DEC 线绘字符集、DA/DSR 应答、备用屏。
- `TerminalType.cs` — **vt52/100/102/220/320/340/420/520/xterm/xterm-256color** 十种 profile,各自 TERM 名 + Device Attributes 应答;`FromTermName`/`ToTermName`;**xterm-256color 为默认**。
- `Utf8Sink.cs` — 增量解码,**可配置任意编码**(UTF-8 默认,GBK/Big5 等);`CharWidth.cs` — wcwidth(CJK 双宽);`TerminalPalette.cs` — 256 色 + 设计稿 term-\* 配色;`Charsets.cs` — DEC 线绘映射;`InputEncoder.cs` — 按键→字节(应用光标键、xterm 修饰键、VT52)。
- `Rendering/VelaTerminalControl.cs` — 纯自绘 Avalonia `Control`:glyph 渲染、光标、选区、滚轮回溯、剪贴板(含括号粘贴);**同时实现旧 `ITerminalEmulator` 接口**以无缝接回 `SshTerminalBridge` 与视图。默认网格 120×32;`ApplyLayoutSize` 拒绝 <2 列/行的早期布局(修过"横幅每字一行"bug)。

## ✅ 4. SSH / PTY

- `SshTerminalBridge` 只读循环,**不再向 shell 预写 `\n`**(修过"末行提示符重复"bug)。
- **PTY 实时改窗**:`IShellStreamWrapper.Resize` → Tmds.Ssh `RemoteProcess` 的终端窗口尺寸变更(实现见 `Infrastructure/Ssh/ShellStreamWrapper.cs`);`ITerminalEmulator.PtySizeChanged(cols,rows)` 由控件布局时抛出,`TerminalTabViewModel` 后台线程转发给 PTY。
- **连接失败不崩溃**:`MainWindowViewModel.TryConnectProfileAsync` 捕获认证/网络/超时异常,映射中文提示写入状态栏 + `LastConnectionError`;交互式连接失败弹错误对话框。`Program.cs` 装了 `TaskScheduler.UnobservedTaskException`/`AppDomain.UnhandledException` 兜底。
- **连接持久化**:`ConnectionWorkflowService.SaveProfileAsync`→`SonnetDbSessionRepository`(SonnetDB `session_profiles` 集合,密码 AES-256 加密);`MainWindowViewModel.InitializeAsync` 启动时加载侧栏"最近连接"(SonnetDB `conn_history` 时序)与会话树;侧栏最近项**双击重连**;命令面板也可连。
- **新建连接密码框仅限 ASCII**:`Behaviors/AsciiOnlyInput.cs` 拦截 IME/中文 TextInput + VM setter 剥离粘贴的非 ASCII。

## ✅ 5. 停靠 / 分屏(VelaDock,已替换 Dock.Avalonia)

- **模型层** `Docking/Model/`(纯 INPC,可单测):`DockWorkspace`(结构操作 + `DocumentClosed`/`ActiveDocumentChanged` 事件)、`DockGroup`(标签组)、`DockSplit`(分栏树)、`DockDocument`;空组自动折叠(主组先把兜底身份交给邻居再退场,**只有根留着**,见 §64)、单子分栏自动提升;`MaximizedGroup` 只影响渲染、不动树。方案与集成面分析见 `velashell-docs zh/host/dock-replacement-plan.md`。
- **控件层** `Docking/Controls/`:`DockWorkspaceControl`(按树渲染 Grid+GridSplitter,star ↔ Proportion 回写;**按文档缓存视图**,切标签复用同一 `TerminalTabView`,取代原 ControlRecycling)、`DockGroupControl`(标签条 + 溢出三连钮 + 标签列表下拉)、`DockTabItem`(标签视觉 + 右键菜单:关闭系列/水平垂直拆分/标签位置)、`DockDragController` + `DockDropOverlay`(拖拽重排插入线、跨组并入、五区拖放分屏,Esc 取消;浮动窗口按产品决策不存在)。
- `Docking/TerminalDocument.cs` 包装 `TerminalTabViewModel`,实现 `IDockViewProvider` 自己创建视图。
- `MainWindow.axaml` 用 `<dockc:DockWorkspaceControl Workspace="{Binding Layout}" />` 承载;`TabBar`(Ctrl+Tab/W 逻辑集合)与工作区激活态**双向同步**(原 Dock 集成缺 TabBar→文档区半边)。
- `Controls/ReparentingHost.cs` — 沿用:内容宿主挂缓存视图前先从旧父级摘除,保证共享终端控件任一时刻只有一个父级。
- `Themes/DockStyles.axaml` 保留全局通用样式(ToolTip/ContextMenu/MenuFlyout/tab-nav 等);标签视觉内联在 `DockTabItem.axaml`。

## ✅ 6. UI / 视图与设置

- **状态栏跟随激活 Tab**:每个 `TerminalTabViewModel` 携带 `ConnectionSummary/TerminalTypeName/EncodingName`;`UpdateStatusBarForActiveTab` 投影连接串/状态/类型/编码/尺寸/延迟;订阅 `ActiveTerminalTab` 变化 + Dock `ActiveDockableChanged`/`FocusedDockableChanged` → 切换标签/窗格实时更新左下角。
- **窗口壳:自绘无边框标题栏(2026-07-13 定稿)**:主窗 `WindowDecorations="None"`(与全部对话框同款全自绘模式);`Views/TitleBarView` 自绘 36px 标题栏 —— 左 logo+产品名,右 全局功能图标组(搜索/SFTP 文件管理/路由追踪/进程管理器/隧道/命令面板,经命令注册表,**已全部启用**;分屏走命令注册表 `split.horizontal`/`split.vertical`;多会话同步输入已以标签右键 A/B/C/D 频道菜单落地,见 §12-7 —— 2026-08-14 勘误,此前"组同步/广播未实现、禁用半透明"的描述已过时)+ 最小化/最大化/关闭三枚窗口控制按钮(46×35,关闭 hover #E81123)。**并非回退原生 chrome** —— Avalonia 12.x 的 `ExtendClientArea`/`WindowDecorationsElementRole` 托管装饰在 Win32 上会拦截标题栏输入(按钮点不动、窗口拖不动),整套机制不可用故弃用;改以**自绘 + 原生行为补齐**:空白区 `BeginMoveDrag`(原生移动循环,Win11 边缘贴靠有效)、双击切最大化;**Win11 Snap Layouts 经 `MainWindow` 的 WndProc 钩子处理 `HTMAXBUTTON`**(提交 `ce71b32`,`nc-hover` 类由 NC 消息挂/摘);窗口四周 5px + 四角 10px 自绘缩放抓取区(`BeginResizeDrag`,最大化时关闭)。**文字菜单(会话/编辑/…)已整体移除**——与命令面板功能重复(用户决策);随之移除设置里的"显示菜单栏"开关(`ShowMenuBar` 存储字段保留兼容)。
  - **踩坑备忘(自绘壳为何不走 extend/原生 chrome,Avalonia 12.0.5 观察)**:①`VisualRoot as Window` 恒为 null(视觉根是 TopLevelHost),取窗口必须走逻辑树 `FindLogicalAncestorOfType<Window>()`——曾令标题栏按钮/拖动看似"无输入"数小时;②`ExtendClientAreaToDecorationsHint`/`BorderOnly` 的托管装饰(`WindowDrawnDecorations`)会绘制重复标题与含"全屏"的按钮,且 `WindowDecorationsElementRole` 的输入重定向未落地(HT\*BUTTON 点击无动作、User 角色不可点),BorderOnly 还丢 WS_CAPTION(HTCAPTION 拖动与最小/最大化动画失效,issue #21160/#21212)——整套 extend 机制在 12.0.5 不可用,故弃用。
- **命令面板(Ctrl+P / Ctrl+K)**:`ViewModels/CommandPaletteItem.cs`(+Group)、`CommandPaletteViewModel.cs`(模糊子序列搜索、分类分组、上下循环导航、执行/关闭)、`Views/CommandPaletteView.axaml(.cs)`;`MainWindow` 半透明遮罩浮层,条目=最近会话(Enter 连接)+ 全局命令。
- **终端类型/编码设置项**:`AppSettings.TerminalType`(默认 xterm-256color)/`TerminalEncoding`(默认 UTF-8);`SettingsViewModel`/`SettingsView` 两个下拉;`Program.cs` 注册 `CodePagesEncodingProvider`(GBK/Big5);连接时 `MainWindowViewModel.ConfigureTerminal` 应用到 PTY 的 TERM 与控件。`ISettingsService`/`JsonDataStore` 已入 DI。
- 快捷命令面板、隧道管理面板此前已有完整 View+VM。
- **设置窗口现为 12 页**(2026-08-14,840×740):常规 / 外观 / 终端 / 密钥管理 / 快捷键参考(纯展示) / 文件传输 / 安全审计(含会话录制与已信任主机) / **网络代理(2026-08-14 新增,见 §12-10)** / 代码片段 / 云同步 / 关于(含贡献者) / 支持与捐赠;整改详情见 §13 与 `velashell-docs zh/host/settings-audit.md`。
- **终端配色跟随主题**:未自定义时 暗=Dracula / 亮=Solarized Light 实时切换;配色方案下拉的“(默认)”后缀与选中项随主题动态联动,选默认方案 = 恢复出厂跟随态。

## 📖 7. 测试(已全量迁移到 MSTest)

**当前基线(2026-09-07)**:排除 `DockerIntegration` / `CrossPlatform` 分类后 **3194 通过 / 0 失败**,`dotnet build` 零警告。历史刻度:2026-07-12 ≈606 → 2026-08-14 1657 → 2026-09-07 3194。

| 测试项目 | 覆盖 |
| --- | --- |
| `VelaShell.Core.Tests` | 领域模型、SFTP 与传输队列、隧道与计量转发、云同步加密 |
| `VelaShell.Terminal.Tests` | VT 解析、终端仿真、编码、字符宽度、侧栏折叠 |
| `VelaShell.Terminal.RenderTests` | 字形绘制的**像素级**回归(挂 Skia 软件后端做真实光栅化) |
| `VelaShell.Presentation.Tests` | ViewModel 工作流与命令 |
| `VelaShell.Infrastructure.Tests` | SonnetDB 持久化、凭据加密、ConPTY、SSH 密钥管理、插件管理与跨进程 RPC |
| `VelaShell.Controls.Tests` | 自定义控件行为、主题令牌与样式守门 |
| `VelaShell.Plugin.Ai.Tests` | AI 插件:审批闸门、能力桥接、设置与机密存取、会话历史、`@` 引用语法、协作接入与面板 headless 交互 |
| `VelaShell.Tests` | 窗口级视图模型、身份验证流程、插件面板与主题令牌、集成与冒烟测试 |
| `VelaShell.Benchmarks` | BenchmarkDotNet 吞吐与分配基准。**不进 CI 门禁** —— BDN 结果受机器负载影响太大,当门禁只会天天误报;用途是在**同一台机器上**比较改动前后 |

- 已移除 `xunit`/`xunit.v3`/`FluentAssertions`/`Avalonia.Headless.XUnit`;改用 `MSTest.TestFramework`+`MSTest.TestAdapter` 4.4.0,全局 `using Microsoft.VisualStudio.TestTools.UnitTesting`。
- **CI 门禁**(`.github/workflows/ci.yml`,2026-09 落地):push `main` 与全部 PR 触发,windows / ubuntu / macos 三平台矩阵,Debug 构建 + 全量测试 + `-warnaserror`。几处刻意的选择:用 Debug(强名签名只在 Release 打开,fork 与 Dependabot 的 PR 拿不到仓库 secret);排除 `DockerIntegration` / `CrossPlatform`;**本仓检出到 `VelaShell/` 子目录**并把 `velashell-docs` 并排检出 —— 快捷键总表与《快捷键参考》的比对用例才找得到对方。
- 转换约定(供新增测试参考):`[Fact]`→`[TestMethod]`;`[Theory]`+`[InlineData]`→`[DataTestMethod]`+`[DataRow]`;`[Trait("Category","X")]`→`[TestCategory("X")]`;每类 `[TestClass]`;`ITestOutputHelper`→`public TestContext TestContext {get;set;}`;`IAsyncLifetime`→`[TestInitialize]`/`[TestCleanup]`。
- 断言:MSTest `Assert.AreEqual(EXPECTED, ACTUAL)`(期望在前);异常用 `Assert.ThrowsExactly`/`Assert.ThrowsExactlyAsync`;字符串用 `StringAssert`;序列用 `CollectionAssert`。
- 注意点:`long`/`uint` 期望值要带后缀(`AreEqual(object,object)` 类型严格);`bool?` 用 `x == true`;非记录类型对象等价用 JSON 序列化比较。
- 早期约定「测试不渲染 Avalonia」**已放宽**:`Terminal.Tests` 与 `VelaShell.Tests` 现引 `Avalonia.Headless`,`VelaShell.Tests/Views/` 下有一批 headless 视图与像素回归用例(`VelaHeadlessApp` 为其宿主)。纯逻辑用例仍只 `new` 控件、不起 UI。`VelaShell.Tests/ModuleInit.cs` 用 `[ModuleInitializer]` 初始化 ReactiveUI 调度器,保留。
- 集成测试(`SshIntegrationTests` 需 Docker+SSH 服务器、`CrossPlatformPublishTests` 需 `VELASHELL_PUBLISH_TESTS=1`)按环境早退跳过。

## 📖 8. 关键约定 / 已知坑

- 构建/测试用根目录 `VelaShell.slnx`。运行 App 后 DLL 被占用会导致构建报"文件被锁定"——先停掉运行实例。
- Bash 工具用 Git Bash;不要用 `Read`/`Grep` 直接读 `.pen`(加密,只能走 pencil MCP)。
- 记忆索引见 `C:\Users\Joe\.claude\projects\G--VelaShell\memory\`(terminal-engine、docking、sonnetdb-storage、connect-flow)。
- SonnetDB 要点:`Tsdb.Open(new TsdbOptions{RootDirectory})`;文档 `db.Documents.Open(name)` 的 Upsert/Get/Scan/Delete;时序 `db.Write(Point.Create(...))` + `SqlExecutor.Execute` SELECT;`FieldType` 在 `SonnetDB.Storage.Format`(是 `Int64` 不是 `Long`,写值用 `FieldValue.FromLong`);**时序 tag 值不允许空串**(临时连接不写 profile_id);**SQL 方言:`ORDER BY time` 要求 SELECT 列表包含 time 列**;`DELETE FROM measurement` 可能不受支持(录制存储以 drop+回写压缩兜底回收);仓储加密必须写副本、不可原地改传入的 profile(内存明文用于活动连接)。
- Avalonia 12 坑:`Run.Text` 绑定会在卸载等时机回写(展示转换器 `ConvertBack` 返回 `BindingOperations.DoNothing`、绑定标 `Mode=OneWay`);ComboBox 的 `SelectedItem` 在 ItemsSource 为空/Clear 时会把 null 写回数据源(载入顺序先填列表再回填选中,见默认密钥修复);XML 属性值中的换行被规范化为空格(多行文案拆多个 TextBlock);**`ControlTemplate` 里直接写的属性值是 `LocalValue`,优先级高于外部样式的 `Setter`** —— `Style Selector="X /template/ Y"` 只改得动模板里没赋过值的属性,模板写死的(如 Fluent `CheckBox` 那层匿名 Grid 的 `Height="32"`)改不动,要换只能整份重写 `ControlTheme` 或在外层抵消(见 §90)。

## ✅ 9. 2026-07-08 完成情况(6 次提交,514 测试全绿)

按"每部分一次提交"推进,提交顺序即依赖顺序:

1. **`2a270e5` feat(storage) SonnetDB 存储层** —— 持久化全面切换嵌入式 SonnetDB。
   - `SonnetDbEngine`(单例,退出 Dispose 刷 WAL):文档集合 `session_groups` / `session_profiles`($.groupId 索引)/ `app_config`(settings/state 单文档)/ `known_hosts` / `ui_config` / `quick_commands`;时序 measurement `conn_history`(最近连接)/ `audit_log`(审计)。
   - 新接口:`IRecentConnectionService`、`IAuditLogService`、`IAppDataStore`(通用 JSON 文档存取)、`ISecretProtector`。
   - `AesSecretProtector`:AES-256-GCM + 本地密钥文件 `secret.key`,密文前缀 `enc1:`,历史明文读取兼容。
   - 旧 JSON(sessions/settings/state/known_hosts/quick-commands)首次运行导入后改名 `.migrated.bak`;LiteDB 包移除。
2. **`10e9e70` fix(ui) 侧边栏快速连接区** —— history 图标修正并接刷新;移除输入框;最近连接改"名称-分组 + 相对时间"两行(user@host:port 移入悬停提示);数据源 = SonnetDB 连接历史(去重、倒序、上限 10),重启不丢;双击按 ProfileId 解析档案重连。
3. **`1e1fa6b` feat(ui) 新建连接弹窗** —— 按设计 oAHna 重构(自绘标题栏/协议标签页/记住密码/会话分组/高级选项/测试/保存/连接)。保存只落库、连接落库+建会话;`SessionProfile.RememberPassword=false` 时凭据不持久化。**修复仓储加密副作用 bug**(原地加密会把内存明文密码改成密文导致重连认证失败,改为写副本)。会话树按 GroupId 接线(含"未分组"节点、双击/右键连接、右键编辑、保存后刷新);Ctrl+N 打开弹窗。
4. **`f5405f5` feat(auth) 两步登录验证** —— `AuthenticationDialogView` 按设计 oNZIM/twD13(第 1 步用户名+指纹,第 2 步密码/证书/密钥分段);凭据缺失时 `TryConnectProfileAsync` 经 `InteractiveAuthenticator` 弹窗,认证失败自动重试(≤3 次);SSH 握手接主机密钥 **TOFU**(首次记录指纹到 known_hosts,指纹变化拒绝连接);连接成败写 `audit_log`。
5. **`3ef6bed` docs** —— architecture.md / 架构设计.md / 隧道功能规划.md 持久化方案全部改为 SonnetDB 并补数据结构说明。
6. **`2812048` feat(settings) 设置窗口九页** —— 自绘对话框 + 图标导航(常规/外观/终端/密钥管理/快捷键/文件传输/安全审计/代码片段/关于),Ctrl+, / 侧边栏齿轮 / 命令面板均可打开;`AppSettings` 扩展分组选项(General/Appearance/TerminalBehavior/Transfer/Security/Keys)嵌套持久化;密钥管理页为真实功能(`SshKeyService`:枚举 ~/.ssh、类型+SHA256 指纹解析、生成 RSA、导入/删除/复制公钥);代码片段页复用 `quick_commands`;常规页清除历史/配置导入导出可用。

此前 §9 的"设置子页补全"与"安全(密码明文)"两大项**已完成**;会话树已接线。

## 🚧 10. 历史待办清单（已迁出）

> **2026-09-07 重整**：本节原本是 2026-07-09 复盘留下的待办台账，混着「已完成」「待办」
> 「确认不做」三类东西，越滚越长也越来越难读。现在**待办部分整体迁往
> [`feature-plan.md`](feature-plan.md)**；这里只留两样东西 —— 当年**做完了什么**，
> 以及**确认不做的那几条及其理由**。

### ✅ A. 设置项接线（2026-07-09 那一轮全量排查后完成）

一轮把下面这些从「存了但不生效」变成真的生效：

- **终端行为全套** —— 光标样式 / 闪烁、行高、选中即复制、右键粘贴、复制去尾空格、双击选词、
  多行粘贴确认、Ctrl+C 复制、滚动行为、Bell 三模式 + 标签闪烁、IME 开关
- **外观** —— 终端四色 + ANSI16 稀疏覆盖、窗口透明度、菜单栏显隐、侧边栏位置、启动窗口状态、UI 字体 / 字号
- **常规** —— 默认端口、连接超时 / 心跳、自动重连 + 间隔 + 重试、关闭前确认、断开提醒 + 声音、
  开机自启、托盘、恢复会话、会话日志 + 保留清理、全局记住密码
- **文件传输** —— 远程初始目录、下载目录、显示隐藏文件、最大并发、双向冲突策略、保留时间戳、
  完成通知、带宽限速、传输日志 + 保留清理
- **安全** —— 首次指纹人工确认、指纹变更阻断 / 人工裁决、告警通道（应用内 + Webhook + 审计）
- **密钥** —— 默认认证密钥

关键接线点：`MainWindowViewModel.ApplyLiveTerminalSettings` / `MainWindow.ApplyWindowAppearance+OnClosing` /
`InfrastructureServiceCollectionExtensions`（超时 / 心跳 / 指纹策略）/ `SftpService`（带宽 / 时间戳）/
`FileBrowserViewModel.TransferOptions`。

同批调整的默认值：LineHeight 1.2 → 1.0、ScrollOnOutput true → false、CopyOnSelect false → true、
RemoteInitialPath `"/home/user"` → `""`（空 = 家目录）。

✅ **上传方向冲突策略**（2026-07-10）：上传前 `ISftpService.ExistsAsync` stat 远端同名文件，
按策略询问（覆盖 / 跳过）/ 覆盖 / 跳过 / 重命名（`file (1).txt` 取首个可用名）；
「覆盖」策略下不额外 stat，沿用 SFTP 覆盖语义；编辑器保存回传属**有意覆盖**，不走冲突检查。

✅ **断点续传**（2026-07）：`SftpService` 双向按偏移续写 + 尾部 64KB 核实起点。
✅ **临时文件清理**：`FileBrowserViewModel.CleanupPartialTargetAsync`，仅 `ResumeEnabled` 关闭时生效。
✅ **会话录制**（2026-07-12，见 §13）。
✅ **启动时自动检查更新**：`CheckUpdatesOnStartup` 既有运行时消费者
（`MainWindowViewModel.RefreshNotificationSourcesAsync:1591`，经消息中心投递「有可用更新」），
也有设置页开关（`Settings/GeneralSettingsPage.axaml:42-45`）。

> ⏳ **仍未闭合的设置项**（主密码、Agent 自动加载、自动下载更新、传输失败重试、标签栏位置）
> 见 [`feature-plan.md` 的 P0 表](feature-plan.md#-p0--存了但不生效的开关)。

### ❌ B. 确认不做（2026-07-10 定，已从设置界面与 `AppSettings` 移除）

连字 Ligatures、自适应标题栏颜色、系统通知 Toast、输入脱敏、自定义键位、运行时热切终端类型。
**逐条理由见 [`feature-plan.md` 的「确认不做」表](feature-plan.md#-确认不做)**，
以及 `velashell-docs` 的 `zh/host/架构设计.md` §11。

### ✅ C. 那一轮顺手清掉的技术债

- ✅ QuickConnect 组件已删除（View / VM / SidebarViewModel 引用与测试同步清理）。
- ✅ **SonnetDB 锁粒度：决定保留全局信号量** —— 文档集合与时序共享同一 Tsdb 实例（同一 WAL /
  存储引擎），SonnetDB 未承诺内部线程安全，按集合分锁有并发损坏风险。真正的瓶颈是**设置读热点**
  （每次连接、每个传输文件都读一次），已在 `SonnetDbSettingsService` 加 settings JSON 缓存
  （缓存序列化文本、按次反序列化，调用方语义不变），读路径不再进锁 / 碰盘。
- ✅ 硬编码 `#0A0E14` 前景抽成 `PulseAccentForeground` 令牌；用户自定义强调色时
  `App.ApplyAccent` 按亮度自动配对前景。
- ✅ `Ctrl+T` 改为打开新建连接（与 Ctrl+N 一致；旧绑定往已不显示的 TabBar 塞空标签）。
- ✅ **OSC 52**（远端写剪贴板，tmux/vim yank；**只支持写方向**，查询 `?` 一律不应答防剪贴板泄露，
  1MB 上限）与 **DECRQSS**（应答 SGR `m` 与 DECSTBM `r`，其余回 `DCS 0 $ r`）已实现，含单测。
  **顺带修掉一个预存在的解析器 bug**：全局「ESC 重启序列」会把以 ST（`ESC \`）结尾的 OSC/DCS
  整段丢弃（只有 BEL 结尾才能用），现已在 ESC 分支先分发在途载荷，补了回归测试。
- ✅ **CJK 回退视为已解决**：字体链（Cascadia Mono → JetBrains Mono → Consolas → Microsoft YaHei →
  monospace）+ 渲染器逐格 FormattedText 回退路径已覆盖双宽字形。

### ✅ D. OSC 8 显式超链接（2026-09-08）

`OSC 8 ; params ; URI ST … OSC 8 ; ; ST`：远端**明说**「这段文本是指向该 URI 的链接」，
显示文本与目标可以完全不同（`ls --hyperlink`、`gh`、`delta`、cargo/gcc 的诊断都在发）。
在此之前只有 `SemanticMatcher` 的**正则猜测**，锚文本一旦不像 URL（`点此查看报告`）就永远认不出来。

**落点与取舍**

- **句柄而非引用**：`Emulation/HyperlinkTable.cs` 把 `(id, URI)` 驻留成 `ushort` 句柄（0 = 无链接），
  单元格只存句柄 —— 与 `CombiningPool` 同一路子。
- **⚠️ 句柄不在 `TerminalCell` 里，而是 `TerminalRow` 上一个平行的 `ushort[]?`。**
  `feature-plan.md` 原本写的是「`CellFlags` 旁边加一个 link-id」，**做的时候改了**：
  单元格现在恰好 16 字节（4+4+4+2+2），再加一个 `ushort` 会因对齐涨到 20 —— 那是回滚缓冲
  **25% 的无条件涨幅**，而带链接的行万里挑一。平行数组把这笔账精确记在真有链接的行上
  （多付 2 B/列），其余行只多一个 null 引用字段。`TerminalCellMemoryTests` 的两条断言因此原样通过。
  xterm.js 的 `_extendedAttrs` 是同一取舍。
- **代价是要逐条接线**：`Fill` / `FillRange` / `DeleteCells` / `InsertCells` / `Resize` /
  `TrimToContent` / `ResetFor` / `Clone` 每一条都要把链接数组一起搬，`TerminalScreen.ReflowResize`
  也要拿一个平行 `List<ushort>` 把句柄送过重排 —— **漏掉重排那一处，拖一下窗口宽度所有链接就没了**。
  `Osc8HyperlinkTests` 逐条守着这些路径。
- **打印路径每格都盖句柄，哪怕当前没有链接**（`SetLink`/`SetLinkRange` 在「写 0 且本行从无链接」时是
  纯空操作，不分配）。少了这一步，「在旧链接上覆写普通文本」会留下点得开的幽灵链接。
  `PrintRun` 快路径同样要盖 —— 否则长链接整段失效、短的（走逐字符路径）看着好好的。
- **URI 必须把 `p[2..]` 拼回来**：`;` 是 OSC 的字段分隔符，却在查询串里完全合法（`?a=1;b=2`）。
- **OSC 8 与 SGR 相互独立**：`SGR 0` 不关链接，只有 `OSC 8 ; ; ST` 关。

**安全**：URI 来自不可信的远端输出，而点开它走系统 shell 关联。因此 scheme 走**白名单**
（`http` / `https` / `ftp` / `ftps` / `mailto`），含控制字符或超长（>2083）的一律不驻留。
**`file:` 刻意不放行** —— 对 SSH 客户端来说 `ls --hyperlink` 报的是**远端**路径，在本机打开
既无正确语义，又正好是这条攻击面上最危险的一格。不驻留的链接**照常显示文本**，只是不画下划线、
点不开，与「看起来能点 = 真的能点」这条既有约定一致。

**表的生命周期**：没有引用计数（回滚区里的格随时可能引用任意一条旧句柄），故设 4096 条上限，
撞顶后新链接退化为普通文本；只有 RIS 硬复位（缓冲区连同回滚一并清空）才整表回收。

**UI**：渲染层给带句柄的格画下划线（不受「语义高亮」开关约束 —— 那个开关管的是本地猜测，
这条是协议事实），着色仍只染留在默认前景色下的文本。命中判定统一收进
`VelaTerminalControl.LinkAtCell`，**OSC 8 优先、正则兜底**，Ctrl+悬停与 Ctrl+点击共用它。

### ✅ E. OSC 133 命令块 / 语义提示符（2026-09-08）

`OSC 133` 把一屏输出切成结构化的**命令块** `[提示符][命令][输出][退出码]`：
`A`=提示符开始、`B`=输入开始、`C`=输出开始、`D;n`=命令结束附退出码。

**这一轮做了引擎 + UI，没做自动注入**（范围是用户定的）。

**引擎侧：标记挂在行上，搭 `Timestamp` 的顺风车**

- `TerminalRow.Mark`（`PromptMark` 枚举）+ `ExitCode`，生命周期与 `Timestamp` 逐条对齐：
  滚动按引用迁入 scrollback、整行擦空即作废、reflow 由 `ReflowResize` 搬运。
  **刻意不像 `GutterFoldModel` 那样另建一张按行对象引用锚定的表** —— 那种表 reflow 一来只能整体作废，
  而挂在行上的东西能跟着内容被搬过去。
- ⚠️ **标记只落重排后的首行，时间戳才铺满**。一条逻辑行是**一个**块边界；铺满会让一条被换行的
  提示符在侧栏冒出好几个标记，跳转还会在同一条提示符上原地跳。
- ⚠️ **`Resize` / 备用屏切换必须丢掉 `_promptRow`**。`ReflowResize` 会**回收复用**旧行对象
  （`_reflowPool` + `ResetFor`），攥着一个已被复用的行等 `D` 回来，就会把退出码盖到一条完全
  无关的行上 —— 屏幕上表现为某条历史输出凭空标红。有专门用例守着。
- `B` **有意不落行**：它标的是列，而列在重排后会挪位，正确搬运得像光标那样换算成逻辑行内偏移。
  本轮功能全部按**行**定界用不到它；等做「复制这条命令」「重跑」时再补。
- 命令块由 `CommandBlocks` **按需扫描推导**，不存状态 —— 事实已经跟着行走了，再建表只是多一份
  「什么时候作废」的心智负担。扫描只在用户动作时发生，不在每帧渲染路径上。
- `HasPromptMarks` 是**只进不退的锁存位**，不是「当前屏上有没有标记」：侧栏标记列据它显隐，
  按帧扫屏幕会让列宽在滚到无标记的历史区时收起来、正文跟着左右抖。

**UI 侧**

- 侧栏新增**命令标记列**（`GutterLayout.CommandMarkWidth`）：每条提示符画一个矢量小三角，
  退出码非 0 标红。没装集成的会话这一列宽度为 0，一个像素都不占。
- 点标记 → 选中**该命令的输出**（不含提示符与命令本身）。
- `Ctrl+Shift+↑/↓` 在提示符间跳转。**没有标记时原样编码下发** —— 没东西可跳还吞掉一组按键，
  只会让远端程序的键位神秘失灵。
- 折叠**按命令块对齐**（`GutterFoldModel.FoldRegion`，折叠头取提示符行而非区域末行，
  收起来的是输出、"哪条命令"必须还看得见）。没有标记时原样退回既有的 Notepad++ 式折叠。

**注入：不做，改为设置页摆出可复制片段**（设置 → 终端 → 会话）

- ⚠️ **bash 那段刻意不用 `DEBUG` trap。**教科书写法拿 DEBUG trap 发 `C`，但那个 trap 全局只有一个，
  和 bash-preexec / direnv 直接抢；而且它对 `PROMPT_COMMAND` 里的每条钩子也触发 ——
  用户只要另有一个 `PROMPT_COMMAND` 钩子，`C` 就会被那个钩子抢走、落在提示符上而不是命令上，
  **真命令的输出反而没了标记**（拿真 bash 复现过）。改用 `PS0`（bash 4.4+，读完命令、执行之前打印）
  之后语义正好是 `C`，一个 trap 都不用装。
- 退出码由 `PROMPT_COMMAND`（插在最前面）抓、由 `PS1` 发：`PS1` 在全部钩子跑完后才展开，
  那时 `$?` 已经是最后一个钩子的状态。这样 `D` 也天然排在 `A` 之前。
- `ShellIntegrationShellTests` 把片段交给**真 bash** 跑，用 bash 自己的提示符展开（`${VAR@P}`）
  取出实际会打印的字节再比对 —— 等价于在真 TTY 上画一次提示符，却不需要 PTY。
  与 `PromptHookShellTests` 共用抽出来的 `BashProbe`。
- **zsh 那段与「fish 自带」的说法未在本机验证**（开发机没有 zsh / fish），bash 那段验过。

## 🚧 11. 设计稿分析已记录的问题（供实现时对照）

| 状态 | 项 | 说明 |
| :---: | --- | --- |
| ✅ | 设置-终端 缺终端类型 / 编码选择器 | 已在代码补上 |
| ✅ | `term-*` 只定义 8 个 ANSI 色，无 bright/256 | 引擎侧已补全；2026-08-31 起并入具名主题的 16 套终端配色（§25 ~ §27） |
| ✅ | 未指定 CJK / 双宽回退字体 | 字体链 + 逐格回退路径已覆盖（§10-C） |
| ✅ | 终端交互（光标样式、选区色、终端内搜索、分屏）设计未建模 | 四项均已实现；搜索见 `MainWindowViewModel.TerminalSearchRequested:1616` |
| ✅ | 亮色主题 `bg-terminal=#1E1E2E` 仍为深色（疑似有意） | 具名主题落地后不再成立 —— 每套亮色主题各自带配套终端配色（§25） |
| ⏳ | Logo 有一个 `enabled:false` 残留图标 | 小项，见 [`feature-plan.md`](feature-plan.md#-终端与协议) |
| ⏳ | 文件列表「修改时间」列无固定宽度 | 同上 |

## 🚧 12. 与主流终端工具的功能缺口（已迁出）

> 2026-07-09 对照 Xshell / MobaXterm / Tabby / WindTerm 做的缺口分析，共 20 条。
> **2026-09-07 重整**：15 条已完成、3 条确认不做的**结论留在这里**（下表），
> **未完成的 5 条迁往 [`feature-plan.md`](feature-plan.md)**。

### P1 —— 日常使用高频

| # | 状态 | 项 | 落点 |
| :---: | :---: | --- | --- |
| 1 | ✅ | **本地终端标签** | `Infrastructure/Pty/ConPtyShellStream.cs`（CreatePseudoConsole + 双匿名管道）实现 `IShellStreamWrapper`，复用既有 桥 → VT 引擎 → 自绘控件 管线；`Services/LocalShellCatalog.cs` 探测 pwsh / Windows PowerShell / CMD / WSL / Git Bash。本地标签强制 UTF-8、**不自动重连**（exit 是用户意图）。⚠️ **仅 Windows**（`DetectShells` 在非 Windows 直接返回空） |
| 2 | ✅ | **SSH 跳板机（ProxyJump）** | `SessionProfile.JumpHostProfileId` 引用另一条已保存配置作跳板，链式即多段跳（≤5 跳、带环检测，`ConnectionWorkflowService.BuildChainAsync`）；**指纹按各跳逻辑主机校验**，绝不按 127.0.0.1 记录。Tmds.Ssh 迁移后改用库原生 `SshProxy` 链（`BuildProxyChain`），手工建链的 `JumpChainSshClientWrapper` 已删除 |
| 3 | ✅ | **保存的会话全部进命令面板** | 「最近连接」快速通道 + 「会话」全量类目（分组徽章、按名排序），两组按 ProfileId 去重 |
| 4 | ✅ | **导出终端缓冲区** | 命令面板 `terminal.export`：有选区导出选区、否则全量（scrollback + 屏幕，逐行去尾空格、截掉尾部空行） |
| 5 | ✅ | **配色方案预设** | 内置 16 套（`Core/Models/TerminalColorScheme.cs`），与 12 套具名界面主题成对联动（§25 ~ §27） |
| 6 | ✅ | **克隆会话** | `session.clone`（Ctrl+Shift+N / 命令面板） |

### P2 —— 进阶运维能力

| # | 状态 | 项 | 落点 |
| :---: | :---: | --- | --- |
| 7 | ✅ | **多会话同步输入** | `Services/SyncInputCoordinator.cs` 对等频道模型（标签右键 A/B/C/D 频道菜单）。挂钩 `TypedInput`（**仅用户产生的输入**，不含协议自动应答），直写同频道其他标签的 PTY —— 走桥的 `SendRaw`，不经接收端输入事件，因此既不回环也不驱动接收端的补全弹层 |
| 8 | ✅ | **ZMODEM（rz/sz）** | **协议引擎在本仓库实现**（未走 trzsz）：`Core/ZModem/` 传输无关引擎 + `Terminal/ZModem/` 自动接管路由。后续补齐 XMODEM / YMODEM（`Core/XYModem/`）。**2026-09-14 加上配置面**（`modem-plan.md`）：三种协议各自可启停（全局 `TransferOptions` + 每条连接 `TransferOverrides`，null = 跟随全局，合成入口唯一 —— `SessionTransferSettings.Resolve`），并新增「默认传输方式」`Sftp/ZModem/YModem/XModem`，由命令面板的「发送 / 接收文件」一对命令消费；SSH 默认走 SFTP，没有 SFTP 通道的连接按启用情况退回终端内协议。关掉 ZMODEM = 路由器**不再嗅探输出流**。⚠️ **测试教训**：互操作期望值必须按 lrzsz `zm.c`/`zmodem.h` **手工构造**（见 `LrzszInteropTests`）—— 用自家编码器生成期望值时，编解码同时错也照样全绿，CRC 双重增广的 bug 当初正是这么溜进来的 |
| 9 | ✅ | **SSH config 导入** | 2026-09-09 落地（§58）：`Infrastructure/Import/SshConfigParser.cs` 按 OpenSSH 语义解析 `~/.ssh/config`（块结构 + `Include` 就地展开 + 「先出现者胜」取值 + 通配/取反匹配），`SshConfigImportService` 作为第三个来源接进同一扇导入对话框。`IdentityFile` → 私钥认证，`ProxyJump` → 跳板引用（取离目标最近的最后一跳，批内解析、成环即断） |
| 10 | ✅ | **连接代理** | 2026-08-14 落地为**应用级全局代理**（非按会话）。统一抽象 `Core/Net/IProxyResolver`（唯一代理出口，新功能接网络一律消费它）+ `Infrastructure/Net/`（HTTP CONNECT / SOCKS5 握手、环回中继、进程级 `HttpClient.DefaultProxy`）。三条通道：SSH 走环回中继、FTP 走 FluentFTP 代理子类（代理下强制被动模式）、全部 HttpClient 由 `VelaWebProxy.Install` 接管。**代理配置不完整时抛错拒连，绝不静默直连**。ICMP 与连接诊断的裸 TCP **有意不走代理** |
| 11 | ⏳ | 防空闲断开（Anti-idle） | 见 [`feature-plan.md`](feature-plan.md#-终端与协议) |
| 12 | ✅ | **known_hosts 管理界面** | 设置 → 安全审计 → 已信任主机（列出 / 删除 / 截图防泄露地址脱敏）。⏳ 导出未做 |
| 13 | ✅ | **会话标签自定义颜色** | `b9ae31f`(2026-09-06)落地:`TerminalOverrides.TabColor` → `ConnectionAccent.cs:66` 优先读它,连接对话框可填;留空才回退到按 `profileId` 哈希取色。⚠️ 本节 2026-09-05 那次「复核订正」写的是「用户不可选」,**次日就被这次提交推翻了** —— 复核结论也会过期,写现状要给证据(文件行号),再新的证据出现就得改。⏳ 图标仍未做 |

### P3 —— 锦上添花

| # | 状态 | 项 | 落点 |
| :---: | :---: | --- | --- |
| 14 | ✅ | **SFTP 本地 / 远程双栏** | 独立 SFTP 标签（`ConnectionType.SFTP` + `Docking/SftpDocument`），左 `LocalFilePaneView` / 右 `FileBrowserView`，双栏互拖与 OS → 远端拖放。🚧 与 WinSCP 的剩余差距见 [`feature-plan.md`](feature-plan.md#-文件传输) |
| 15 | ⏳ | 用户自定义关键字高亮规则 | 见 [`feature-plan.md`](feature-plan.md#-数据与可观测)。当前 7 条为编译期硬编码 |
| 16 | ✅ | **命令自动补全 / 历史建议** | `CommandSuggestionProvider` 合并本地命令历史与快捷命令。⚠️ `InteractivePromptDetector` 在「程序在提问」的行上让它**闭嘴** —— sudo 密码提示、apt 的 `[Y/n]`、编号选单、REPL 提示符，把一整条 shell 历史塞进程序的输入里有害无益 |
| 17 | ✅ | **OSC 52 剪贴板** | 见 §10-C（只支持写方向，1MB 上限） |
| 18 | ⏳ | 触发器 / 自动应答 | 见 [`feature-plan.md`](feature-plan.md#-会话与工作区) |
| 19 | ❌ | **多窗口** | 与单实例 / 唯一组合根 / 无浮动窗口三处硬冲突，理由见 [`feature-plan.md`](feature-plan.md#-确认不做)。多屏需求由分屏承担 |
| 20 | ❌ / ✅ | **Mosh ❌ / SSH 证书认证 ✅** | Mosh 确认不做（需并行维护第二套 UDP 传输与终端预测引擎），见 [`feature-plan.md`](feature-plan.md#-确认不做)；**证书认证已于 2026-09-10 落地**（§63）—— 「待评估 Tmds.Ssh 支持度」那句已过期：`CertificateCredential` 在 0.23 就有，我们锁的 0.24 早就够用 |

### ✅ 同期落地、不在原清单里的

Telnet（2026-08-17，**以插件形式**：宿主为此新增「终端协议」能力 `IProtocolTerminal`，
插件会话经 `PluginTerminalShellStream` 适配成 `IShellStreamWrapper`，复用既有的桥 / VT 引擎 /
ZModem / 重连 —— 调研文档里那套「协议泛化」改造整套免掉）、**串口**（同一能力做成 `velashell.serial`
插件）、**Redis / S3**（同样走插件），四者源码均在
[velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins)。
**FTP / FTPS**（2026-08-13）走宿主内置，见 §18-N。

## ✅ 13. 2026-07-11 ~ 07-12 批次(设置审计整改 + 四个新特性)

**A. 设置审计整改**(台账与逐项状态见 `velashell-docs zh/host/settings-audit.md`,共三批):
BellMode/VisualBell 合并(旧配置经 `AppSettings.Normalize()` 迁移)、自动重连次数统一、默认值来源统一、显示隐藏文件写回持久化、恢复默认/清除历史加确认、误导性文案与九组相似命名修正、12+ 个未实现禁用控件隐藏或删除、选项类统一 `ObservableOptions`(INPC,从属设置条件显隐真正生效)、快捷键页与真实绑定核对重建(自定义键位确认不做)。

**B. 主机指纹三选项确认 + 已信任主机管理**:
`IHostKeyPrompt.DecideAsync` 三态(永久信任=写 known_hosts / 仅本次信任=进程内 `HostTrustOnceCache` 不落盘 / 取消=fail-closed);SFTP 独立通道补主机指纹校验(修复默认信任任意指纹的 MITM 缺口);安全审计页新增"已信任主机"列表(删除即可重触发首次确认;地址默认脱敏防截图泄露)。

**C. GitHub Gist 云同步**(`Core/Sync` + `Infrastructure/Sync` + 设置"云同步"页):
同步范围 = 应用设置(剔除设备本地字段)+ 连接配置(含分组与隧道,upsert 合并不删本地)+ 代码片段;单文件 secret Gist,版本管理复用 Gist 原生 revision(列表含来源设备,可恢复任意版本);可选 PBKDF2-SHA256(200k)+AES-256-GCM 端到端加密(未启用时凭据绝不上传);智能方向判定(本地改动标记 × 远端 revision,双端都改按较新者胜);自动同步 = 启动拉取 + 设置保存防抖推送;PAT/口令经 `ISecretProtector` 机器绑定加密,永不进载荷。

**D. 会话录制与回放**(设计 `NceE6`;`Core/Recording` + `SonnetDbSessionRecordingStore` + `RecordingPlayerView`):
录制 = 桥输出 600ms/64KB 缓冲成块写 SonnetDB 时序 measurement `session_recording_chunks`(元数据在文档集合 `recordings`);开关 `Security.RecordProductionSessions`(安全审计页,对新连接生效),保留天数随会话日志;回放中心 = 列表 + 只读终端按时间轴重放 + seek(重置瞬时重放)+ 1x/2x/4x + 跳过空闲 + 删除 + 导出 asciicast v2。输入脱敏确认不做(仅录输出流)。

**E. 支持与捐赠页**(设置导航末位):支付宝/微信/Wise(链接可点击+复制),收款码已裁剪入 `Assets/`;文案强调 PR/Issue 是最好的支持。

**F. 后续增量(同批小项)**:

- 回放中心:窗口 1200×820 + 无边框缩放(右下手柄/最大化/双击标题栏)、列表选中态改主题令牌、播完点播放自动从头、倍速扩至 1x~16x;录制保留随日志天数清理 + DELETE 不可用时 drop+回写压缩兜底(防孤儿数据块磁盘只增不减)。
- **双许可落地**:MIT → AGPL-3.0(`LICENSE` 官方全文)+ 商业授权(`LICENSE-COMMERCIAL.md`,联系 dygood@outlook.com,含轻量 CLA 条款);README/关于页同步正版声明(名称与 Logo 不在开源授权范围);商标注册与历史贡献者重许可确认为线下待办。
- 关于页**贡献者区**(设计 kGwqX,仅头像+名称):真实提交者(joesdu/tsaiggo),GitHub 头像异步加载(失败回退首字母),点击跳转主页。
- **终端配色随主题联动**:亮色默认调色板由 Alucard 换为 Solarized Light;配色方案下拉“(默认)”标注与跟随态选中项随主题动态切换(选默认方案 = 恢复出厂跟随态;显式选其它方案 = 钉住)。已知边界:覆盖模型以 Dracula 色值为出厂基准,亮色下无法“钉住 Dracula”(选它即回跟随态)。

**已知遗留**:QuickCommands 相关 12 个测试在用户某次提交后失败(测试期望 11 个内置命令含 htop,`QuickCommandCatalog` 只有 8 个,测试与目录不同步,与上述改动无关)。

## ✅ 14. 多语言(2026-07-12 全量补齐,C-09 一并完成)

- **五语言**:简体中文 / English / 繁體中文 / 日本語 / 한국어。资源按 .NET 标准命名:`Strings.resx` 为英文默认(`NeutralLanguage=en`),卫星 `zh-Hans/zh-Hant/ja/ko`(脚本中性文化,zh-CN/zh-SG→Hans、zh-TW/zh-HK→Hant 沿标准回退链自动命中);2026-07-12 首次补齐时为 867 键,随后续特性增长,**现为 938 键**五语齐平(键集平价有测试守护)。
- **全仓提取**:~900 处硬编码文案迁入 resx —— axaml 用 `{loc:Localize Key}`(实时切换),C# 动态文案用 `Strings.Get/Format`(占位符 {0}/{1})。不翻译:协议/提示符匹配串(密码提示关键词、"$ " 等)、TERM/编码名、shell 命令文本、日志。
- **实时切换的两处根因修复**:①`LocalizationService` 自持目标文化 —— 线程文化随 ExecutionContext 回卷、且 UI 线程显式设置过文化后 DefaultThreadCurrentUICulture 失效,均不可靠;②`LocalizeExtension` 改绑按键缓存的 `LocalizedText` 条目**普通属性**(Avalonia 12 绑定引擎不响应 `Item[]` 索引器变更通知),换语言逐条目发标准属性通知。语言选择:设置 → 常规 → 语言(5 项,存储值 zh-CN/en/zh-TW/ja/ko)。
- **测试守护**:键集平价(五文件同键、双向)+ 具体文化回退链(zh-SG/zh-HK/ja-JP)用例,见 `LocalizationTests`。已知边界:VM 构造时求值的标签(设置导航、快捷键参考页、状态栏初值、内置快捷命令描述)换语言后需重开窗口/重启刷新。

## ✅ 15. 版本与发布(2026-07-12)

- **版本号单一来源**:`Directory.Build.props` 的 `<Version>`(当前 `0.0.1-dev`;`AssemblyVersion`/`FileVersion` 另给不带后缀的 `0.0.1`,并关掉 `IncludeSourceRevisionInInformationalVersion` 以免 `+sha` 后缀);关于页版本运行时读程序集 InformationalVersion,不再硬编码;发版由 Release 标签经 `-p:Version` 覆盖。
- **本地发布**:`pwsh scripts/publish-all.ps1` → `publish/` 产出 6 个包(2026-07-17 起,`-noruntime` 变体已裁撤):Windows x64/arm64 便携 zip,macOS 与 Linux x64/arm64 tar.gz(全部含运行时;2026-08-12 起摊开发布,不再单文件 —— 隔离插件的 `VelaShell.PluginHost` 需要磁盘上的真实可执行体,换版随之从"移动"改为"复制"),外加自更新清单 `latest.json` 与 `SHA256SUMS.txt`。
- **CI/CD**:`.github/workflows/release.yml` —— GitHub 页面发布 Release(publish)即触发:windows/macos/ubuntu 三原生 runner 并行构建同一套 6 产物(版本号取 Release 标签,`-p:Version` 覆盖,发版无需改代码),汇总生成 `SHA256SUMS.txt` 与 `latest.json`(应用内自更新清单:版本/标签/各 RID 产物名+sha256+大小),经 `gh release upload` 全部附加到该 Release。macOS 产物未签名/未公证(需 Apple 证书后续补);Linux 为便携 tar.gz(.deb/AppImage 为后续扩展点)。
- **Windows 安装包(2026-07-13;2026-07-17 调整)**:Velopack `Setup.exe` 链路已整体移除——其默认安装目录曾与当时的 `%LocalAppData%\VelaShell` 应用数据根冲突,卸载会清空用户数据,且自打的便携 zip 无法经 Velopack 更新。现行数据根已改为 `~/.velashell`;分发方案为便携 zip + 应用内自更新(任意目录原地换版)。WiX v4 MSI 定义(`installer/VelaShell.wxs`,x64/arm64,`WixUI_InstallDir` 中文向导支持自定义安装目录,静默安装 `msiexec /i VelaShell.msi /qn INSTALLFOLDER="D:\Tools\VelaShell"`;`ProductVersion` 须为纯数字 x.y.z,`UpgradeCode` 固定走 MajorUpgrade)保留可手动构建,不再随 CI 发布;MSI 装进 Program Files 后应用内更新按"目录不可写"如实提示手动下载。

## ✅ 16. 2026-07-13 ~ 07-14 批次(VelaDock 合并、原生窗口壳、终端侧栏、安装包、工程化)

> 本批以数个独立 PR 合入 `dev`/`main`(#3 replacedock、#5、#6)。多为架构/工程化收尾与使用体验修正。

**A. VelaDock 正式落地(PR #3)**:详见 §5 与 `velashell-docs zh/host/dock-replacement-plan.md`(已补「已完成」横幅)。模型/控件/拖拽全套自己实现、替换 `Dock.Avalonia`,零第三方停靠依赖;拆分对所有标签组一致生效(单标签次级组也可水平/垂直拆分);点击窗格内容区即激活该组文档(SFTP 面板与状态栏随焦点窗格切换)。关于页开源许可列表已删 Dock.Avalonia 条目。

**B. 主窗自绘无边框标题栏 + 原生行为补齐(体验优化)**:详见 §6。主窗保持 `WindowDecorations="None"` 全自绘(`TitleBarView` 含 logo/名称 + 功能图标组 + 自绘 min/max/close),**未走原生 chrome**(extend/角色重定向在 Win32 不可用);改以 `BeginMoveDrag` 原生移动循环 + **WndProc 钩子处理 `HTMAXBUTTON` 实现 Win11 Snap Layouts**(`ce71b32`);并修 `c12a8ff`(Avalonia 12 `VisualRoot` 非 `Window`,取窗口须走逻辑树 `FindLogicalAncestorOfType<Window>`)。中途 `7580052` 试过原生 chrome 集成,因保真问题走回自绘+WndProc 方案。对话框同为自绘无边框。

**C. 终端行号 / 时间侧栏(gutter)**:`Terminal/Rendering/GutterLayout.cs` + `GutterFoldModel.cs` 为终端左侧新增**行号**与**时间戳**两列侧栏,各自独立开关、支持快捷键切换;含折叠标记与空白间隔,折叠模型重构以增强可测性(`GutterFoldTests`/`GutterLayoutTests`/`GutterFoldUiTests`/`LineTimestampTests`)。

**D. 本地终端进程树秒杀**:`ConPtyShellStream` 引入 **Windows Job Object**,关闭标签/退出时连带杀掉子进程树(如 WSL、pwsh 派生进程),优化关闭体验,避免孤儿进程。

**E. 交互式提示下的补全判定**:密码类提示行(sudo/密码提示关键词命中)**不再弹出命令补全**弹层(sudo 密码提示下按键误弹智能提示);新增交互式判定单元测试。

**F. 工程化收尾**:①**集中式包管理** `src/Directory.Packages.props`(`ManagePackageVersionsCentrally`,各 csproj 只写包名不写版本)+ 构建系统重构;②**Avalonia 12.0.5 → 12.1.0** 升级;③**全项目补充详细 XML 注释**(`GenerateDocumentationFile`);④为**每个 src / tests 项目新增独立 `README.md`**(架构、目录职责、依赖关系),根 `README.md` 与 `velashell-docs zh/host/architecture.md`/`架构设计.md` 同步刷新至当前状态(版本、VelaDock、原生标题栏、五语言、发布/安装包、命名 `Pulse*`→`Vela*`)。

**已知遗留(延续)**:§13 末 QuickCommands 相关 12 个测试仍待与 `QuickCommandCatalog` 对齐(测试期望 11 个内置命令含 htop,目录只有 8 个);ConPTY 无头握手用例环境相关失败,均与本批改动无关。

## ✅ 17. 2026-07 批次(SSH 传输层迁移、ZMODEM、SFTP 双栏)

**A. SSH.NET → Tmds.Ssh 迁移**(`tmds-ssh` 分支):换成全托管、async-first 的 [Tmds.Ssh](https://github.com/tmds/Tmds.Ssh) 0.23.0。
Core 的中立抽象证明有效——**迁移一行 Core 代码都没改**,改动全部落在 `Infrastructure/Ssh/`:

- `SshClientWrapper`/`SftpClientWrapper` → `TmdsSshClientWrapper`/`TmdsSftpClientWrapper`;`ShellStreamWrapper` 现包装 `RemoteProcess`。
- `SshNetInterop` → `TmdsSshInterop`:库异常翻译为 Core 的 `VelaSsh*Exception` 族。Tmds.Ssh 的 `ConnectFailedException` 是 `internal`,只能按消息前缀 `"The connection could not be established - {reason} - "` 提取原因再分派,这是当前实现的已知脆弱点。
- **ProxyJump 简化**:删除手工建链的 `JumpChainSshClientWrapper`,改用库原生 `SshProxy` 链(`BuildProxyChain`,见 §12-2)。
- **回归**:`连接代理(SOCKS5/HTTP 经由连接)` 随 SSH.NET 一起失去(§12-10 已改回 ❌)。
- ✅ **已修(2026-07-22)**:`MainWindowViewModel` 曾按旧类型名字符串匹配异常(`"SshAuthenticationException"` 等),而实际类型已是 `VelaSshAuthenticationException`,导致**认证失败重试与全部分类错误提示静默失效**(都落进兜底文案)。现改为直接匹配 `VelaSsh*Exception` 类型,并去掉已无对应实现的 `ProxyException` 分支(`Msg_ProxyError` 资源键保留)。
  **为什么没被测出来(重要)**:`InteractiveAuthFlowTests` 与 `MainWindowSshFeatureTests` 各自定义了一个**私有的假异常 `SshAuthenticationException`**,注释明写"Named to match SSH.NET's ... so the VM's type-name mapping applies" —— 测试专门迎合了实现的怪癖,于是生产路径从未被覆盖、测试长期全绿。根子在 `VelaSshClientException.cs` 的注释:它声称这些类型的简单名与 SSH.NET 一致(实际带 `Vela` 前缀,从来对不上),代码照着错注释写。两个假异常已删除、改抛真类型,该注释也已改写。
  **约定**:跨层识别异常一律 `ex is VelaSshXxxException` 类型匹配,**绝不用 `GetType().Name` 字符串** —— 换库或改名时字符串匹配不会产生任何编译错误。

**B. ZMODEM(rz/sz)**:见 §12-8。

**C. 独立 SFTP 标签 + 本地/远程双栏 + 断点续传**:见 §12-14 与 §10.A;差距清单见 `velashell-docs zh/host/SFTP双栏与WinSCP差距分析.md`。

**D. 远程文件编辑器语法高亮**:`App/Services/Syntax/`(`FileTypeDetector` + `SyntaxHighlightingService`)接 AvaloniaEdit,按扩展名对常见文件类型着色;保存即经 SFTP 回传(有意覆盖,不走冲突检查)。

**E. net10 → net11**:`Directory.Build.props` 统一切到 `net11.0`(全仓一处),`global.json` 锁 `11.0.0` + `rollForward: latestFeature`;对 net11 开启 `EnablePreviewFeatures` 与 `Features=runtime-async=on`。**代价**:构建依赖 .NET 11 预览版 SDK,全平台发布需实机冒烟;回退 net10(LTS)仍是一行改动。

**F. 拖入文件夹的异常风暴修复(2026-07-22)**:把一个文件夹拖进文件浏览器时,调试输出会被 `ConnectFailedException` / `VelaSshConnectionException` 刷屏。三处根因:

1. **`AutoConnect` 默认为 true**。`BuildSshClientSettings` 从未设置它,于是会话掉线后**每一次** SFTP 操作都会各自静默重连一次,失败抛一发 `ConnectFailedException`——这与同文件里"主连接不在时不得偷偷另建连接"的注释意图直接矛盾。现显式 `AutoConnect = false`,连接只由 `TmdsSshClientWrapper.ConnectAsync` 发起。
2. **续传探测逐文件打远端**。`TryResumeAsync` 走裸 `ExistsAsync`,绕开了本批已经预列举好的目录名单(`RemoteExistsAsync`,零往返)。拖入 N 个文件 = N 次额外往返,叠加第 1 点就是 N 次隐式重连。现已改走名单。
3. **“覆盖”策略下不列举目录**,导致开启断点续传时第 2 点退化回逐文件探测。现改为「覆盖 **且** 不续传」才跳过列举。

**G. 文档**:新增 `velashell-docs zh/host/SFTP双栏与WinSCP差距分析.md` 与 `velashell-docs zh/host/Telnet与串口可行性调研.md`(均为 2026-07-22 的决策清单,标注了每项的实现代价与"未核实"项)。

## ✅ 18. 2026-07-24 ~ 08-14 批次盘点(2026-08-14 补记;此前均已落地但未入本文件)

**A. 插件系统 v1 + AI 助手插件(08-10 ~ 08-13,本批最大特性)**
- **双宿主模式**:manifest `hostMode` 选进程内(可收集 ALC + dock 标签页)或**隔离进程**(`src/VelaShell.PluginHost/`,命名管道上的轻量 RPC、令牌握手、心跳自愈、空闲回收、独立卡片窗口);插件源码两模式零改动。关键路径 `Infrastructure/Plugins/`(`PluginManager`/`PluginContext`/`Capabilities/`/`Isolated/`/`PluginPermissionGate`)。
- **SDK**:`plugin-sdk/VelaShell.PluginSdk`(能力域 sessions/remoteFs/remoteExec/commands/events/storage/secrets/clipboard/terminal/timeSeries)+ `PluginSdk.Testing` 测试替身;管理页 `PluginManagerWindow` + 权限对话框。
- **分发**:目录即插件 + `.vpx` 包(zip,含 zip-slip 防护)一键装卸;**无商店/签名 —— 用户决策不做/推迟**(见 `velashell-docs zh/plugins/STATUS.md`,权威进度页);发布形态因此改**摊开发布**(隔离插件需磁盘上真实 PluginHost 可执行,§15 已记)。
- **AI 助手插件**(`plugins/VelaShell.Plugin.Ai`):多提供商流式对话(OpenAI Responses / Chat Completions 兼容 / Anthropic Messages,自填 Base URL+Key 走 Secrets 加密);**Agent 模式**(M.E.AI 工具循环,桥接 sessions/terminal/remoteExec/remoteFs,危险操作面板内逐条审批);**自定义 MCP 服务器**(`McpManager` 把用户自配 MCP 工具并入工具箱,非只读工具走同一审批闸);会话持久化到插件私有时序库(历史列表/切换/删除、↑↓ 调取、`@` 远端文件引用)。示例插件 HelloWorld。
- 文档:`velashell-docs zh/plugins/` 16 篇蓝图 + `STATUS.md` + `dev-guide.md`;英文镜像 `docs-en/`(08-14,31 个文件)。

**B. 系统资源监控窗口(08-01,08-29 修正)**:`ResourceMonitorWindow`(+状态栏内嵌弹层 `ResourceMonitorView`),六页 总览/CPU/GPU/内存/磁盘/网络;CPU 页热力图/迷你折线/列表三态,GPU 无卡自动隐藏;图表控件 `TimeSeriesChart`/`UsageHeatGrid`/`MeterBar`(`VelaShell.Controls`)。采集为**单条复合 shell 探针**分段解析(`Core/Services/SessionMetrics.cs`,`MetricsScope` 按页按需取 Basic/Detail/Gpu/Processes):CPU 含 user/sys/iowait/steal 与逐核、内存 htop 口径、磁盘逐分区 df + diskstats IO 速率、网络逐网卡 + `ss -ti` 逐连接速率、GPU nvidia-smi、进程 Top。入口:状态栏按钮。状态栏每秒轮询一次(`SessionMetricsService`),但只对 POSIX 远端发命令:`RemoteShellProbe` 判否即返回"无数据"。此前不拦,Windows 远端上每秒起一个 cmd.exe,而 cmd 把 `echo __P__; nproc; …` 整行原样回显,`Parse`(只在输出为空时返回 null)据此解出一份全 0 的假指标,状态栏一本正经地显示 CPU 0.00%。

**C. 连接诊断中心(07-25 前后)**:`Presentation/Services/ConnectionDiagnosticsService` 四步诊断 **DNS 解析 → TCP 建链 → SSH 握手(读 banner)→ 用户认证**,输出问题标题/描述/修复建议(`DiagnosticReport`);跳板会话前三步针对第一跳、认证走完整链。UI `ConnectionDiagnosticsView` 独立窗口,入口:会话树右键"诊断"。(诊断的裸 TCP/DNS **有意不走全局代理** —— 诊断语义即测直连链路,§12-10。)

**D. 路由/链路追踪 + 离线 IP 归属地(07-25)**:`PingTraceRouteService`(ICMP TTL 递增,免管理员;Linux TTL 不可用时抛可读异常而非假表)+ `MmdbIpGeolocationService`(本地 MMDB 离线库,默认 `~/.velashell/geoip/`,缺库静默降级、面板内引导下载);`TraceRouteWindow` 左侧 `TraceWorldMap` 世界地图落点 + 右侧 mtr 式跃点表(Loss/Sent/Last/Avg/Best/Worst、ECMP 额外地址)。入口:标题栏图标。设计文档 `velashell-docs zh/host/路由追踪设计.md`。

**E. 远端任务管理器(07-25,08-29 修正)**:SSH 进程管理(`IRemoteProcessService`/`RemoteProcessService`),入口:标题栏"进程管理器"图标。采集前按 `RemoteShellProbe` 判定对端是否 POSIX shell,不是就直接报"不可用"而不发命令 —— cmd.exe 会把整行探测命令原样 echo 回来,`Parse` 只在输出为空时返回 null,于是面板显示的是一张 CPU 0.0%、0 进程的**假空表**而非那句"需要一个已连接的 Linux 会话"。


**F. 文件浏览器跟随终端目录(07-24,08-20、08-29 修复;09-15 以 VS Code 的 shell 集成为基础整体重构)**:SFTP 上传按钮右侧 map-pin 开关(`FileBrowserViewModel.FollowTerminal`);终端 cwd 由对端 shell 的提示符上报,`TerminalEmulator` 解析→去重→浏览器同步。

**为什么不能照搬 VS Code。** 它的自动注入靠的是**自己启动 shell**(bash 给 `--init-file`、zsh 换 `ZDOTDIR`、fish 加 `XDG_DATA_DIRS`),官方文档也白纸黑字写着"普通 `ssh` 会话用不了这招"。SSH 客户端没有那个口子,只能往一个**已经跑起来的交互式 shell 的 PTY 里打字** —— 于是回显、命令历史、语法报错、探测、多条注入互相顶掉,这一整类问题得自己解决。能照搬的是协议(OSC 633)与片段写法,搬不了的是投递方式。

- **分 shell 注入(`ShellIntegrationScript`)**:bash 走 `PROMPT_COMMAND`、zsh 走 `precmd_functions`(刻意不用 `add-zsh-hook`——它是 autoload 函数,NixOS 这类 `fpath` 不全的机器上会静默失败)、fish 走 `--on-variable PWD`、dash/ash/ksh 走 `PS1='$(…)'` 单引号赋值。旧版只有 bash 一段,守卫 `test -n "$BASH_VERSION"` 把 **zsh 一并短路**掉了——zsh 用户不报错但功能就是不工作(macOS 默认 shell、oh-my-zsh 全体)。钩子一律**还原 `$?`**:bash 跑完全部 `PROMPT_COMMAND` 才展开 `PS1`,不还原就把用户提示符里的"上条退出码"永久钉死成 0。
- **两道探针(`RemoteShellProbe`→`RemoteShellKind`)**:第一道考 `printf` + `$((…))` + `${var:-}`(挡住 cmd.exe / PowerShell,#305),顺带用 `$BASH_VERSION`/`$ZSH_VERSION` 带回种类;**只有它失败时**才追问第二道 `echo vela-fish-$FISH_VERSION`。一条命令做不到两件事:挡 PowerShell 靠的正是 fish 不认的那种展开。「没人回答」(两道都拿不到结果)记 `Unknown` 且不缓存,只有真回了东西却对不上号才记 `NonPosix`。
- **注入窗口(`EchoSuppressor.OpenWindow` + `SilentCommand`)**:整行里埋一条**带随机 nonce 的哨兵**(`OSC 633;P;VelaShell=<nonce>`),终端从写下这一行起**整段扣住输出**,看见哨兵才恢复放行。**哨兵的位置决定藏什么**——排在命令后面就连输出带报错一起藏(我们自己的脚本),排在前面就只藏回显(用户配的命令)。两件事因此干净地分开:我们的注入藏得干干净净,用户的命令一个字节都不少。
- **为什么不再按回显匹配**:真机上注入行超出终端宽度后,各家 shell 的折行重绘五花八门——ash 插 `CR LF`、zsh 用 `CR`+`ESC[K` 重绘并**重复**断点字符、fish 整行按列重排,逐字节匹配全部失效(三种形态都由 `ShellIntegrationDockerTests` 抓到过)。而"超宽"并不罕见:光是 `ShellHistoryScrub` 前缀就两百多字符,连 `echo hi` 加上它也会折行——这条既有 bug 一并修掉了。
- **注入推迟到对端安静之后(`SshTerminalBridge.RunWhenOutputIdle`)**:窗口是"从武装那一刻起整段扣住",armed 得太早会把横幅、MOTD、第一个提示符一起吞掉。等"收到过输出且此后静默 300ms"(上限 3s)再动手。四条注入(钩子→全局启动命令→初始目录→认证后命令)一起推迟,顺序不变——用户命令里可能有 `exec zsh`/`tmux attach`,它必须排在钩子之后。
- **超时放行而不是丢弃**(与 electerm 的分歧):它超时即丢,我们是连上就注入,丢掉会把**提示符本身**一起吞掉,用户面对一块空屏。约定是"成功就藏干净,失败最多退回到和以前一样,绝不倒扣"。
- **发两种、认一族**:钩子一次发 OSC 7(通用,给 tmux / 别家终端)+ OSC 633(VS Code,排后面因此**后到者为准**,顺带绕开 OSC 7 对路径里 `%` 的歧义);解析端另认 1337(iTerm2)、9;9(ConEmu),白捡已经为别家配过 rc 的用户。在 tmux 里**额外**再发一份包进 DCS 的拷贝(裸的那份留给 tmux 自己记 pane cwd)。
- **摘历史前缀按 shell 放行(`ShellHistoryScrub.SupportedBy`)**:前缀里的 `${BASH_VERSION:-}` 在 fish 里是**解析期**语法错误,fish 先解析完整行再执行 ⇒ 整行连同后面真正要跑的命令一起死掉,而注入窗口还会把报错藏起来。fish / NonPosix 一律不接。
- **验证**:`PromptHookShellTests`(真 bash)、`ShellIntegrationScriptShellTests`(真 zsh/fish/sh)、以及 `VelaShell.ShellIntegration.Tests`——**真 sshd + 真 PTY**,五种登录 shell × 探测/注入/跟随/隔离/历史/退出码/tmux 各跑一遍,外加 pyenv 式 `PROMPT_COMMAND` 与 starship 式 `PS1` 重写两个"用户已动手脚"的账号。靶子见 `tests/fixtures/ssh-shells/`。

**G. SFTP 传输面板增强(07-27 ~ 07-29)**:框选、批量操作、文件+文件夹混选上传、冲突与历史交互优化;文件传输 toast 面板(`FileTransferView`,浮动活动/历史项,不跨重启持久化)。

**H. Xshell / WinSCP 会话一键迁移(07-30;08-13 改默认全自动)**:`ISessionImportService` 多来源自动扫描架构(`Infrastructure/Import/`:Xshell(Rc4/XshellCrypto)+ WinSCP(WinScpCrypto)),`SessionImportView` 对话框 —— 打开即遍历全部来源扫描、**默认全自动导入 + 高级手选**、按源目录建分组、跳过已存在、密码恢复三态提示 + 主密码告警(主密码库密码不可恢复,仅导会话)。新增来源只需 DI 追加一行(SSH config 导入的现成落点,§12-9)。

**I. UI 字体/字号令牌体系(07-30)**:设置 → 外观 → 界面字体/字号,经 `VelaUiFont`/`VelaUiMonoFont`/`VelaFontSize*` 令牌全局下发(派生字号按比例缩放、钳 9–24);曾清理 ~500 处 axaml 写死字体/字号(写死即令该处失效,见记忆索引)。同批:对话框按钮主题统一。

**J. 终端交互增强(08-05 ~ 08-07)**:Ctrl+Backspace 删词、Alt+左键矩形块选、右侧留白带。

**K. 外观增强**:应用背景图(`BackgroundImagePath` + 图层/内容双不透明度,gated on 有图,无图零回归)、Win11 圆角/投影/滚动条主题统一(08-14)、AvaloniaEdit 输入框(08-14)。

**L. 打包**:MSIX 商店版打包 + 自更新三段式重构 + 隐私政策(07-26)。

**M. 小项(08-14)**:会话树拖动分组(`VSESS|` 拖放载荷、空白处放下=移出分组、拖拽幽灵标签)、侧栏最近连接清除按钮(带确认)、关于页显示进程+系统架构(不一致时并列显示,自更新按进程架构选产物)。

**N. FTP / FTPS(08-13,第三方 PR;08-29 补并发自适应)**:见 §10.B —— `ConnectionType.FTP` + FluentFTP 后端 + 连接池 + `RoutingRemoteFileService` 按会话分派,上层文件浏览器/传输/限速零改动。连接池上限(`FtpSettings.MaxConnections`,默认 4)现在**会自己往下调**:①池里已有活连接却开不出新连接(`421 Too many users` 这类)→ 收到当前连接数并排队复用;②传输被 `450 Transfer busy` / "too many" 之类顶回来 → 收到 1 并重试该传输。两条合起来对付"服务端只支持单线程上传,批量上传只成功第一个"(闸门见 `AdjustableConcurrencyGate`:`SemaphoreSlim` 的许可只增不减,且超发期间只收不发)。回归测试用 `LoopbackFtpServer` 的 `MaxConcurrentSessions` / `MaxConcurrentTransfers` 复刻这两类服务器。

**O. 全局网络代理(08-14)**:见 §12-10。

## ✅ 19. 2026-08-30 隧道功能完善(计量转发 / 流量统计 / 断线自动恢复 / 端口冲突预检)

隧道规划文档 [`velashell-docs zh/host/隧道功能规划.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/隧道功能规划.md)
里挂了很久的三条迭代项一次做完。逐项的实现细节以那份文档为准,这里只记会绊到人的几点。

**A. 转发的数据面从库内搬到宿主自己接管(`Infrastructure/Ssh/MeteredPortForwardHandle`)**:
Tmds.Ssh 把 LocalForward / SocksForward 的搬运整个做在内部,**不暴露任何连接数或字节计数**
——`TunnelInfo.BytesTransferred` 一直恒为 0 就是这个原因。要出统计只能自己接管:本地转发 =
自己开 `TcpListener` + `SshClient.OpenTcpConnectionAsync`(direct-tcpip,与库内部同构,无额外跳数);
动态转发 = 自己监听 + 自己实现的 SOCKS5 服务端握手(`Socks5Negotiation`,RFC 1928,仅 CONNECT + 无认证);
远程转发的监听端只有库能开,于是让它转发到本机一个临时计量监听,再由宿主接力到真实目标
(多一次环回拷贝换来同样的统计)。搬运保留**半关闭语义**(SSH 侧 `SshDataStream.WriteEof`,
套接字侧 `Shutdown(Send)`)—— 做成整条拆链的话,"发完请求就 shutdown 再等响应"的协议全部读不到东西,
这条已有回归测试(`MeteredPortForwardTests.Relay_ForwardsHalfClose`)钉住。
**SOCKS5 服务端的用例喂的是客户端侧 `ProxyStreamConnector.BuildSocks5ConnectRequest` 生成的字节**
——那份实现另有对着 RFC 逐字节的断言(`ProxySupportTests`),拿它当地面真值,
免得服务端与客户端一起跑偏还全绿(ZMODEM 那次的教训,§12-8)。

**B. 流量统计**:行内显示 `3 连接 · 1.4 MB`(有在传的连接时并发数一并点出)。读数由
`ITunnelService.RefreshStatistics()` 从句柄同步到 `TunnelInfo`,面板 5 秒时钟调用;
**停止隧道时先取下最后一次读数再释放句柄**,否则这条隧道跑过多少字节就再也问不到了。

**C. 断线自动恢复**:`TunnelConfig.AutoReconnect`(默认 false,旧配置缺字段即 false),
表单勾选、行内带「自动」徽标。掉线检测覆盖面板持有的**每一台**服务器而非只有选中那台
(隧道跑在后台,用户不会为了让它被照看到而一直停在那个页面上);失败按
10s → 30s → 1min → 2min → 5min 退避。**用户按停过的隧道不自动拉起** ——
`TunnelItemViewModel.StoppedByUser` 记下这一点,"自动重连"扛的是网络抖动,
不是把用户刚按下的停止键撤销掉。

**D. 端口冲突预检**:创建本地/动态转发前比对系统 TCP 监听表,命中抛 `TunnelPortInUseException`。
判定按"端口相同 且(任一方绑 0.0.0.0 或 两者地址相同)"。**探测委托做成可注入**
(`TunnelService` 的 `isLocalPortInUse` 参数)—— 不然 `TunnelServiceTests` 里那些 5432 / 3306
就要看运行机器上恰好有没有数据库在监听,测试时灵时不灵。

**E. 界面文案**:新增 9 个键(`TunnelSvc_LocalPortInUse`、`Tunnel_Stats*`、`Tunnel_Auto*`、
`Msg_TunnelAutoReconnect*`),五份 resx 已齐。

**F. 面板字号上调一档(用户反馈"字太小")**:隧道面板里压在 8/9 两档的次要文字整体上移一档
——徽标 8 → 9(`DESIGN.md` 的阶梯里 9 才是"状态标签"档,8 本就在阶梯之外)、
端点摘要/状态行/统计行/错误行/表单标签/服务器状态行 9 → 10;隧道名(11 Medium)与路由行(10)不动,
层级关系原样保留。同时给路由行补 `TextWrapping="Wrap"` —— 中文文案在 340px 面板里已经铺满一行,
而界面字号是用户可调的,不给换行的话基准字号一调大这行就被硬裁。
`TunnelPanelUiTests` 的视觉 QA 样本补上了统计行与「自动」徽标(设 `VELASHELL_VISUAL_QA_DIR` 出图),
否则截图回归看不到这两处新元素。

## ✅ 20. 2026-08-30 消息中心(侧边栏铃铛)

侧边栏底部那枚铃铛此前是**空占位**(`NotificationsCommand = ReactiveCommand.Create(() => { })`,
点了什么都不发生)。现在把它做成消息中心。设计与资讯源契约见
[`velashell-docs zh/host/消息中心与资讯源.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/消息中心与资讯源.md),
这里只记会绊到人的几点。

**A. 边界:装什么、不装什么**。消息中心收的是**要留存、可回看**的东西 —— 有新版本了、
订阅源发来的公告与安全资讯、将来后台推的运营消息。**不收运行时告警**:主机指纹变了会当场弹窗、
会话断了会亮标签写状态栏,那些要的是立即打断,已各有归宿;混进列表只会把真正要读的东西淹掉。
这条边界写在 `Core/Notifications/INotificationCenter` 的类型注释里,加内容源前先读一遍。

**B. 资讯源契约(`Core/Notifications/AnnouncementFeedDocument`)**:这是**后台系统要照着发布的格式**,
字段发出去就不能改名,加能力只能加可选字段。支持按 语言 / 平台 RID / 版本区间 定向投放,
带 `expiresAt` 自动消失。几条硬约束:单次最多 100 条、响应体上限 512 KB、
**外链只放行 https**(内容来自远端,放行 http 等于让投递方把用户导去一条可被中间人改写的链路)、
**一条坏数据不让整个源哑掉**(缺字段的条目单独跳过)。
`AnnouncementFeedDocumentTests` 逐条钉住,那份用例同时也是契约的可执行说明。
**默认不订阅**:`Notifications.FeedUrl` 为空时一个网络请求都不发 ——
终端客户端默默定期外呼在企业环境里是要被问责的事,得由用户或部署方明确开启。
(**08-31 改为默认订阅官方源**,见 §22。)

**C. 快捷跳转走 `ICommandRegistry`**:通知带 `commandId` 就执行注册表里的命令,
带 `url` 就用系统浏览器开(仅 https);**站内优先**,命令没注册(返回 false)时退回外链。
「有可用更新」用的是 `app.settings.about` —— 新增命令,打开设置并**直接落到「关于」页**,
用户就地更新而不是被丢在设置首页自己找。分区定位用新增的 `SettingsSectionKey` 枚举而非下标:
往分区列表中间插一页而忘了同步枚举,跳转会静默跑到隔壁页 —— 不报错、不崩,只是用户点了
"去更新"却看到别的东西。`SettingsSectionKeyTests` 就是为了让那种改动**当场失败**。

**D. 顺手修好一个死开关**:`General.CheckUpdatesOnStartup` 默认 true,却**全仓没有任何消费者**
(设置审计 R-01 判为"更新服务尚未接入"而隐藏)。现在它真正决定启动时查不查新版本,
因而在常规页恢复展示。同批新增的 `Notifications.FeedIntervalHours` 一开始也差点成为死开关 ——
补了周期拉取:计时器按固定半小时跳,真正拉不拉由该设置决定,用户改完下一跳就生效。

**E. 持久化与上限**:SonnetDB 文档集合 `notifications` 单份文档,**跨重启留存**
(与文件传输面板相反 —— 一次传输的进度隔天没有意义,一条公告第二天仍然成立);200 条上限,
超出丢最旧的。**同 id 重投会被跳过且保住已读状态** —— 每次启动都重投同一条"有新版本",
覆盖会把读过的又变回未读,铃铛红点就永远消不掉。

**F. 界面**:360px 非模态浮层,**锚在左下贴着铃铛**(浮层从哪个按钮开出来就该长在哪个按钮旁边)。
未读左侧 2px 强调竖条 + 主文本色标题,已读退成次要色。整行可点 = 跳转并标记已读,
做成透明按钮而非给 Border 挂手势 —— 键盘 Tab 走得到,焦点框与 hover 跟着按钮语义走。
**外链条目把主机名摆在动作旁边**:地址是远端源给的,得让用户在点之前就看见自己会被带去哪。

**G. 界面文案**:新增 24 个键(`Notify_*`、`Cmd_OpenAbout`、`SetGeneral_*` 消息中心分节),五份 resx 已齐。

**未做**:插件发通知(SDK 的 `IUiApi` 只有 `ShowPanelAsync`;宿主侧接口形状已按这个用途定好,
但要开放给插件需改 velashell-plugin-sdk 仓库 + 扩展隔离进程 IPC + 走 SDK 发版流程,是独立一批)、
系统级通知。

**H. 悬停高亮切成两半(用户反馈,同日修复)**:消息行的悬停高亮原先挂在 `Button.row` 上,
而那个按钮只占三列布局里的内容列 —— 删除键那一列不跟着变色,整行被切成深浅两块,
中间那道边界看着就像凭空多了一条竖分割线。改为挂在整行的外层 `Border.msg-row` 上。
**两个 Background 都必须由样式设**:直接在元素上写 `Background="Transparent"` 是 local value,
优先级高于样式,`:pointerover` 再也盖不过去(Avalonia 的属性优先级,不是选择器不够具体)。
回归测试 `NotificationPanelUiTests.Row_HoverHighlight_CoversWholeRow` 把指针停在**删除键那一列**
再断言整行 `IsPointerOver` —— 问题正出在那半边,指在内容区是测不出来的。

## ✅ 21. 2026-08-31 补全弹层关不掉(#315)

空行按 `Alt+Enter` 召出「快捷指令 + 最近历史」全量面板后,**Ctrl+C 关不掉、点终端也关不掉**。
两个触发条件是两处独立的缺口,凑在一起正好把这个面板最常见的召出方式变成了单向门:

**A. Ctrl+C —— 事件在跟踪器里被吞掉**:`TerminalInputTracker` 只在「行的字面内容变了」时发
`InputChanged`,而 `ResetToKnownEmpty()` 对一个**本来就是确定空行**的行返回 `false`。
弹层的收口完全挂在 `InputChanged` 上(`TerminalTabView.OnTrackedInputChanged`),于是:
`Alt+Enter` 的主场景恰恰是空行 → Ctrl+C 发出的 `0x03` 走到跟踪器 → 空行清空空行 → 不算变化 →
一拍都不发 → 面板留在屏幕上。行里有字时 Ctrl+C 是好的(内容真的变了),所以这个洞只在空行上露出来。
修法:`0x03`/`0x15` 一律记为变化 —— **「取消当前行」本身就是消费方要感知的事件**,
不是内容差分的副产品。另加视图侧的 `Key.C when Ctrl` 分支直接收口(不置 `Handled`,按键照常下发):
开了「有选区时 Ctrl+C 复制」的用户根本没有字节发往 PTY,跟踪器那条路等不到。

**B. 点击终端 —— 没有任何路径能收口**:弹层刻意不开 light-dismiss(那会吞掉关闭它的那一次点击),
而唯一的指针侧收口是终端的 `LostFocus` —— 点一个**已经聚焦**的终端不产生 `LostFocus`。
补 `TerminalHost` 上的 `PointerPressed`(Tunnel、不置 `Handled`,选区/光标行为不变)。

登记进 `ShortcutCatalog` 的补全分组并同步 `velashell-docs` 的中英快捷键参考(收起建议弹层:
`Esc` / `Ctrl+C` / 左键)。回归测试 `TerminalInputTrackerTests.CtrlC_OnAlreadyEmptyLine_StillRaisesInputChanged`
钉住 A;B 是视图层指针接线,本仓暂无宿主视图的 headless 会话,未加用例。

## ✅ 22. 2026-08-31 资讯源默认订阅官方源

`velashell-feeds` 上线后,`Notifications.FeedUrl` 的默认值从空串改为
`NotificationOptions.OfficialFeedUrl`(`https://feeds.easilynet.top/feed.json`)。
理由是安全资讯的价值在于「用户没去找的时候它自己到」:默认关闭的源几乎没人会去打开,
CISA KEV 那几条「现在就有人在打这个洞」就等于谁也收不到。

**这是一次「默认行为」变更,不是一个配置项调整**,因此三处必须跟着改,否则文档开始骗人:

- `PRIVACY.md`(中英两份正文):概述里「开发者不运营任何服务器」不再成立 —— 改成如实说明
  开发者运营**一台**服务器、默认每几小时下载一次公开 JSON、请求里不带任何标识,
  服务端能拿到的只有 IP 与时间,以及「清空地址即彻底不发」。同时在「网络连接」清单里
  新增资讯源一条。**顺手修正了上一批留下的失真**:该清单第 3 条仍写着「检查更新 —— 仅手动
  触发……从不在后台自动检查」,而 08-30 起 `CheckUpdatesOnStartup`(默认开)会在启动时查一次。
- `velashell-docs` 的消息中心文档(中英):默认值表与「留空即不订阅」段落。
- 本文件 §20-B 的「默认不订阅」结论。

**存量用户不受影响**:设置整份 JSON 持久化在 `app_config/settings`,老文档里已经存着
`FeedUrl: ""`,反序列化会把空串照原样带回来 —— 新默认只对全新安装(以及消息中心发布前
从未存过该字段的配置)生效。消息中心尚未随版本发布,因此实际上没有需要迁移的存量。

## ✅ 23. 2026-08-31 消息中心:可拖动、加大字号、动作靠右(用户反馈)

**A. 拖拽逻辑抽成一份共用实现**:消息中心成为第二个可拖动浮层,而拖拽那套东西细到
「按在标题栏的按钮上不许起拖」「`Bounds` 不含渲染变换所以它就是锚定位置」这一层 ——
复制第二份必然漂。于是从 `FileTransferView` 原样抽出 `Behaviors/PanelDragHandler`
(捕获/位移/越界夹紧/松手落盘)与 `ViewModels/IDraggablePanel`(两个偏移量 + 落盘),
两个视图各一行 `PanelDragHandler.Attach(this, DragHandle)`。持久化载体
`TransferPanelPosition` 随之更名 `PanelPosition` 并独立成文件:同一个 `ui-layout` 集合里,
文件传输是 `transfer-panel`、消息中心是 `notification-panel`,类型名不该再绑着其中一个。

**B. 拖拽空间是父容器,所以外层 Panel 必须铺满**:`MainWindow.axaml` 里消息中心原先包在一个
`HorizontalAlignment=Left/VerticalAlignment=Bottom` 的 `Panel` 里 —— 那个 Panel 紧紧贴着面板本身,
而 `PanelDragHandler` 的参考坐标系正是父容器,于是可拖范围会是零。改为**外层 Panel 铺满整行、
对齐与边距落到里面的视图上**(与 `FileTransferView` 的摆法一致)。Panel 不设 `Background`
即不参与命中测试,铺满也不会挡住底下的操作 —— 这与标题栏手柄必须显式写
`Background="Transparent"` 是同一条规则的两面。

**C. 字号加大一档**:正文 10→12、标题 11→13、徽标/时间/主机名 9→10、标题栏 11→12,
全部走 `VelaFontSize*` 令牌(仍跟随「设置 → 外观 → 界面字号」缩放)。行高随之增加,
列表 `MaxHeight` 380→440,可见条数与改字号前基本持平;每行的删除键 20→22px、图标 10→12px。

**D. 「去处」一行改为两端对齐**(用户反馈两轮):动作原先贴左边缘,而指针本来就多停在右侧
(每行删除键、滚动条都在那边),一次跳转要横穿整张卡片。第一版直接把整条靠右,用户当场
反馈「参差不齐」—— 确实:主机名长短不一(站内跳转那行根本没有),整条靠右时动作的左沿
一行一个位置。最终做成**两端对齐**:主机名钉左、`动作 + ›` 钉右,两条竖直边都是齐的。

**D-1. 但真正让它参差的是另一件事**:`Button` 的默认 `HorizontalAlignment` 是
**`Left` 而非 `Stretch`**。整行那个透明按钮因此按自己的内容缩成一团(实测三行分别
295 / 284 / 309px,而它那一列是 318px),里面再怎么"靠右"也只是靠在那团东西的右边。
只设 `HorizontalContentAlignment="Stretch"` 不够 —— 那管的是内容在按钮里怎么摆,
管不到按钮自己有多宽。补上 `HorizontalAlignment="Stretch"` 后三行按钮齐齐 318px,
右沿才真的共线。顺带:这也让内容列的空白处变成可点区域,与「整行可点 = 跳转」的原意一致。
(同时给列表显式关掉横向滚动:开着的话每行按"不换行的理想宽度"各量各的,行宽本身就不一样。)

**E. 右留白 16,给悬浮滚动条让位**(用户反馈):列表的悬浮滚动条展开后约 12px,
压在贴着右边缘的每行删除键上。标题栏右内边距与每行删除键的右外边距统一改成 16
(左仍是 12),滚动条出现时既不遮挡,两处的 x 也仍然对齐。

回归测试:`NotificationPanelViewModelTests` 补三条(落盘到 `ui-layout/notification-panel`、
构造时恢复、无存储时不炸);`NotificationPanelUiTests.DestinationLine_ActionsShareOneRightEdge`
在真实 Avalonia 布局里钉住 D/D-1/E —— 用「无主机名 / 短主机名 / 超长主机名」三行,
断言各行等宽、整行按钮铺满内容列、动作右沿共线、删除键与标题栏关闭键共线。
这条用例在写下时**是红的**(实测 314 / 303 / 328),D-1 那行 `Stretch` 才让它变绿,
不是先有结论再补的用例。拖拽手柄本身是指针接线,未加用例 —— 但那份逻辑现在只有一处,
`FileTransferViewModelTests` 的位置用例仍覆盖着它的另一半。

**F. 左下圆角被未读竖条顶方**(用户反馈):列表滚到底时,最后一行的未读竖条(通高实心色块)
一路画到卡片下沿,把 6px 圆角盖成方角。**圆角不会自己裁剪内容** —— 加一层
`CornerRadius=5 + ClipToBounds=True` 的 Border 包住内容:半径 5 = 外层 6 − 1px 描边
(子元素被布局在描边内侧,照抄 6 会盖掉描边的圆弧段,见 `CardCornerRadiusTests` 的既有约定);
而 `ClipToBounds` **不能**写在卡片自己身上 —— 那会把它自己的 `BoxShadow` 一起裁掉。
顺带订正一处过时的注释:`CardCornerRadiusTests` 的说明写着「Avalonia 的 ClipToBounds
只裁矩形边界、不按圆角裁剪子元素」,在 Avalonia 12.1 上**已经不成立**了 ——
本批的像素用例实测它确实按圆角裁。那条注释描述的是当时的版本行为,不是永恒定律。

**G. 投影调软**(用户反馈「优化窗口的阴影效果」):`VelaShadowWindow` 远处那层原来是
**65% 纯黑 + blur 10** —— 又浓又紧,压在暗色终端底上不像投影,像卡片下面糊了一道黑边。
改为 **40% + blur 12 + y4**(亮色同比降档),延展仍是 16(4+12),外边距不用动。
这是全应用共用的唯一投影令牌,自绘窗体、命令面板、隧道面板、传输浮层一起变 ——
浮层的投影语言只有一套,单独给消息中心配一份等于让同屏两个浮层互相打架。
`DESIGN.md` §4.5 与 velashell-docs 的中英设计规格同步改写:此前它们写的是
「小浮层 blur16 #00000060 / 大弹窗 blur32 #00000080」两套规格,而代码里从来只有一个令牌,
值也对不上 —— 现在文档记的是实际令牌值 + 延展 ≤ 16 的约束 + 圆角不自裁的那条坑。

回归测试:`NotificationPanelUiTests.CardBottomCorner_IsNotSquaredByUnreadBar` 直接量渲染帧的
像素 —— 把列表填到卡片下沿,断言左下角圆弧之外那一小块三角区里没有强调色。
两个坑都踩过才写对:①视图若纵向铺满窗口,卡片下沿会落在列表下方的空白处,测了个寂寞,
必须按 MainWindow 的摆法让它按内容收缩;②headless 帧是 **RGBA** 排布,
按 BGRA 读会把强调色的 R/B 读反,断言永远为真(那正是它第一版"通过"的原因)。

## ✅ 24. 2026-08-31 会话树状态卡在「连接中」(#321)

在一条**已经连上**的 SSH 会话上再开一个标签(同一条配置),趁它还在握手时立刻关掉 ——
左侧资源管理器里那条会话就永远停在「连接中」,再也不会回到「活跃」。

根因是**树上一个节点、底下多个标签,而状态按「最后一次变更」写**:
`CreateConnectingTab` 给每个标签挂一条 `ConnectionStatus → SetSessionStatus(profile.Id, …)`
的订阅,后开的标签一进来就把节点写成 `Connecting`,盖掉前一个标签的 `Connected`。
关闭那半边同样是错的:`OnDocumentClosed` 判断「同配置还有别的已连接标签吗」,
有就**跳过、不回写** —— 而此刻节点上留着的正是被关掉的那个标签写下的 `Connecting`,
于是这个状态再也走不出去。同一个洞还有第二条更短的路径:连接失败或取消走
`RemoveTerminalTab` 静默摘标签,**根本不经过 `OnDocumentClosed`**,单个标签也能卡住。

改法是把「谁来报状态」从单个标签上收走,改成按配置**合并**:

- `RefreshSessionStatus(profileId)` 扫标签栏里属于该配置的全部终端标签,取优先级最高的一个
  (`Connected > Connecting > Error > Disconnected`,`SessionStatusRank`);一个标签都不剩时归零为
  `Disconnected`。一条连着的会话不该因为旁边多了个正在握手/握手失败的标签而变成「连接中」/「离线」。
- 订阅挪进 `OnTabsCollectionChanged`(`_sessionStatusSubscriptions`,与快捷命令目标订阅同生命周期):
  标签进标签栏时挂上、离开时退订并立刻重算。这样**所有**移除路径(关闭文档、连接失败/取消的静默摘除)
  自动共用一条收口,不必各自记得回写。本地终端标签没有 `Profile`,不参与。
- `OnDocumentClosed` 里那段「还有别的连着就跳过」换成同一个重算调用(幂等兜底:文档关闭时
  标签可能已不在标签栏里)。

回归测试 `SessionTreeStatusFromTabsTests` 四条:第二个标签握手期间节点保持「活跃」→ 关掉它仍是「活跃」
(#321 本体)、唯一标签在握手中被摘掉要回到未连接、一个标签失败不把还连着的配置写成离线、
别的配置的标签不串台。前三条在未修复的代码上是红的。

**留了一处没动**:SFTP / FTP / 插件协议 / 工作台文档也往同一个节点写状态(`SetTreeSessionStatus`),
它们与终端标签之间仍是「后写的赢」。本次只把终端标签这一侧收成合并语义 —— 与既有行为等价,不是新洞,
但要彻底,得把这些来源一起纳入同一个合并器。

## ✅ 25. 2026-08-31 具名主题:九套配色 + 终端配色配对

此前"主题"只有三个值:`dark` / `light` / `system`,颜色写死在两份 axaml 的 ThemeDictionaries 里;
终端那边同样只认明暗两套(暗 Dracula / 亮 Solarized Light,硬编码在 `VelaTerminalControl`)。
本批把它改成**具名主题目录**:六套暗色 + 三套亮色,各自带一套配套的终端配色。

**改名**:原亮色 → **VelaLight**,原暗色 → **VelaDark**。持久化的 Id 仍是 `light` / `dark`
—— 老配置一行不用迁,`ThemeServiceSwitchTests` / `HeadlessUiTests` 也不用改。

**新增七套**(名字取自"Vela=船帆座"的星空与自然一系,各有明确的使用场景,不是换个色相凑数):

| 主题 | 基底 | 血统 | 配套终端方案 |
| --- | --- | --- | --- |
| Tokyo Night | 暗 | Tokyo Night | Tokyo Night |
| Nord | 暗 | Nord | Nord |
| Everforest | 暗 | Everforest | Everforest Dark |
| Obsidian | 暗 | 中性近黑(OLED) | Obsidian |
| Gruvbox | 暗 | Gruvbox | Gruvbox Bright |
| Rosé Pine Dawn | 亮 | Rosé Pine Dawn | Rosé Pine Dawn |
| GitHub Light | 亮 | GitHub Light | GitHub Light |

### 一、六十多个令牌,只写二十几个种子色

`UiThemePalette`(Core)是一套主题需要人来定的全部内容 —— 底色阶梯、四档文字、两档描边、
强调色与语义色,二十五个。其余令牌(`*Dim`、`VelaHeat1-5`、`VelaGauge*`、`VelaTrace*`、
`VelaShell*` 等)由 `ThemeTokenApplier`(宿主侧)按固定规则派生。

理由是**手抄会错,而且错了看不出来**:`#644AC922` 这种把透明度写在末尾的错拼,编译期无感、
运行期是一片绿(issue #246 的原始症状)。派生出来的令牌天生自洽,加一套主题只需要填种子色。

**运行时怎么生效**:Avalonia 的资源查找先看字典自身的条目,再看 ThemeDictionaries 与合并字典 ——
所以写进 `Application.Resources` 顶层的键会**遮蔽** axaml 里同名的主题条目,全部
`DynamicResource` 立刻跟着变(强调色覆盖 #3 一直就是这么做的,这次把它推广到整套令牌)。
axaml 那两套保留为 VelaDark / VelaLight 的编译期缺省,`ThemeTokenApplierTests` 钉住两边逐值一致。
这条遮蔽假设本身也有用例:`ThemeTokenShadowingUiTests` 在真 Avalonia 资源栈上验证"贴上去盖得住、
摘下来落得回"。

**踩到的一个坑**:清空自定义强调色的老实现是把 `VelaAccent` 三件套从资源里**删掉**,让它落回
axaml 缺省。在只有明暗两套时这是对的;有了具名主题就成了错的 —— Tokyo Night 的蓝会变成VelaDark 的紫。改为 `ThemeTokenApplier.ResetAccent` 写回**当前主题自己**的强调色。

### 二、终端配色跟着主题走

`VelaTerminalControl` 原来只听 `ActualThemeVariant`。具名主题里有六套暗色,VelaDark 换到
Tokyo Night 时变体压根没变 —— 光听变体,终端画面会原地不动,和换过颜色的界面拼在一起。

改为三层叠加:**控件自带的明暗缺省 → 宿主下发的整套主题配色(新增的 `ThemePalette`)→
用户改过的单色(原有的稀疏 `PaletteOverrides`)**。宿主在 `ApplyLiveTerminalSettings` 里按当前
主题下发,并订阅 `IThemeService.ThemeChanged` 与 `Application.ActualThemeVariantChanged`
(后者是"跟随系统"下系统明暗翻转的那条路)。

配对的硬约束:**方案背景色必须等于该主题的 `VelaBgTerminal`**。亮色主题此前配 Solarized Light
(终端底 #FDF6E3)而界面底是 #FFFBEB,终端边缘一直挂着一道看得见的拼缝 —— 这次 VelaLight 改配
**Alucard**(Dracula 官方亮色),缝消失了。`UiThemeCatalogTests` 逐主题钉住这一条。

新增五套终端方案:Alucard、Everforest Dark、Obsidian、Rosé Pine Dawn、GitHub Light,外加 Gruvbox Bright
—— 最后这套是因为原版 Gruvbox 的 normal 红(#CC241D)压在 #282828 上只有 2.7:1,报错信息比正文
还难读;Gruvbox Bright 的常规八色取官方 bright 一档,原汁原味的 "Gruvbox Dark" 仍留在列表里。

### 三、配色不是"看着顺眼"就算数

`UiThemeCatalogTests` 对**每一套**主题跑:正文两档压在七种底色(含半透明选中底压在浮层上的
观感色)上 ≥ 4.5:1;按钮文字压在强调色上 ≥ 4.5:1;状态点与语义色 ≥ 3:1;强调色淡底的 RGB 必须
等于强调色本身;配套终端方案的前景 ≥ 4.5:1、ANSI 1–6 ≥ 3:1。

这把尺子真的拦下了东西:Nord 原色红压在面板上只有 2.5:1(提亮到 #D4757F)、Rosé Pine 的
iris 压白底只有 3.7:1(压深到 #7C5F9F)、Tokyo Night 的选中底让次要文字掉到 4.3:1(压暗一档)、
Everforest 的选中底同样偏亮。Rosé Pine 原版**没有绿**,补了一支同调森林绿 —— 没有绿,
`ls` 的目录与可执行位就分不开。

### 四、顺手修掉的一个静默 bug

`TerminalPaletteOverrides.IsEmpty` 写的是 `Ansi.Length == 0`,而数组恒为 16 个槽位 ——
于是它**恒为 false**,"一色未改"也被当成有覆盖。叠加一份全 null 的覆盖不改变画面,所以从没露头;
但它让"跟随主题"与"钉死配色"这两种状态在外部无从区分。改为逐槽位判空。

### 五、界面与插件侧

设置 → 外观 → 主题模式的下拉从写死的三个 `ComboBoxItem` 改为绑定 `AvailableThemeNames`
(主题目录 + 末项"跟随系统"),`SetAppear_ThemeDark` / `SetAppear_ThemeLight` 两个 resx 键随之
从五份资源里删除,说明文案改写。终端配色方案下拉的"(默认)"后缀现在跟着**当前主题的配套方案**走。

插件契约里的 `IHostInfo.Theme` 与 `HostEvents.ThemeChanged` 仍然只给 `dark` / `light` / `system`
—— 具名主题不外泄(`UiThemeCatalog.VariantName`)。插件拿到一个没见过的字符串,多半会落到自己的
兜底分支上;而插件真正需要的信息只有明暗。隔离插件的令牌快照走 `PluginThemeTokens`,读的就是
应用资源,自动拿到新主题的颜色,无需改动。

启动读配置时先验 Id 再 `SetTheme`:用新版选过 Tokyo Night 再退回旧版,老版本不认识这个 Id,
`SetTheme` 会抛 —— 验一下就退回默认主题,启动照常。

## ✅ 26. 2026-08-31 主题命名收敛 + 「跟随主题」不再是隐式状态(用户反馈)

两处,都是上一节留下的账。

### 一、主题名改回各自调色板本来的名字

上一批给每套主题都套了 `Vela` 前缀(VelaMidnight / VelaGlacier / …),用户反馈**没必要**:
一套源自 Tokyo Night 的配色就叫 Tokyo Night,认得出的名字比统一的品牌前缀有用。现在只有两套
自家配色保留品牌名 —— **VelaDark**(Dracula)与 **VelaLight**(Alucard),其余一律用血统名:

| 主题(Id) | 基底 | 配套终端方案 |
| --- | --- | --- |
| VelaDark(`dark`,出厂默认) | 暗 | Dracula |
| Tokyo Night(`tokyo-night`) | 暗 | Tokyo Night |
| Nord(`nord`) | 暗 | Nord |
| Everforest(`everforest`) | 暗 | Everforest Dark |
| Obsidian(`obsidian`) | 暗 | Obsidian |
| Gruvbox(`gruvbox`) | 暗 | Gruvbox Bright |
| VelaLight(`light`) | 亮 | Alucard |
| Rosé Pine Dawn(`rose-pine-dawn`) | 亮 | Rosé Pine Dawn |
| GitHub Light(`github-light`) | 亮 | GitHub Light |

Id 一并对齐到名字(`midnight` → `tokyo-night` 等);`dark` / `light` 仍是历史值不动。
调过的那套 Gruvbox 终端方案改名 **Gruvbox Bright** —— 它与原版的差别就是常规八色取了官方的
bright 一档,名字直接把这件事说出来,原版 "Gruvbox Dark" 仍在列表里。

### 二、选 Dracula 没反应:「跟随主题」不能再是隐式状态

用户报的:在配套方案不是 Dracula 的主题(Nord、VelaLight…)下,配色方案下拉里选 **Dracula
毫无反应**,终端仍是主题自带的那套,选中项还会自己跳回去。

根因是一处**语义重载**。老实现把「跟随主题」隐式编码成「设置里的颜色 == 出厂默认」,
而出厂默认的色值就是 Dracula:

- `ColorSchemeIndex` 的 setter 里,选中当前主题的配套方案 = 写回出厂值(= Dracula 色值);
- `BuildPaletteOverrides` 与出厂值逐色做差,全同 ⇒ 无覆盖 ⇒ 跟随主题;
- 于是**「用户明确选了 Dracula」与「跟随主题」写出来的设置一模一样**,分不开。

在 VelaDark 上这个洞看不见(它的配套方案本来就是 Dracula,两种解释同色);具名主题一上来,
除 VelaDark 之外的每一套都会踩到。

改法是把跟随与否变成**显式**的一项:

- `AppearanceOptions.TerminalColorsFollowTheme`(`bool?`)。**不给初值**:`null` 表示配置里
  没有这一项(1.4.x 及更早),由 `TerminalColorScheme.FollowsTheme` 按老口径推断
  (颜色 == 出厂 Dracula ⇒ 跟随)。给了初值 `true`,老用户自定义过的配色会被当成跟随态丢掉。
- `BuildPaletteOverrides`:跟随 → 返回 null(一个槽位都不覆盖);不跟随 → **整套**下发,
  不再与出厂值做差。用户在设置页上看到的那套颜色,就是终端要用的那套。
- 下拉首项独立为 **「跟随主题(配套方案名)」**(`SetVm_FollowThemeScheme`,五份 resx 齐),
  其后是全部内置方案,配套的那个仍带「(默认)」后缀标明出处。选首项 = 跟随,
  并把配套方案的色值写进去 —— 下面那几个色块显示的就是屏幕上真正在用的颜色。
- 跟随态下手改前景/背景/光标/选区任一色 = 用户要自己定配色,就此脱离跟随。
  **判定不能反过来问 `FollowsTheme`**:PropertyChanged 到达时新色值已经写进去了,
  而老配置的跟随与否恰恰是按色值推断的 —— 一改就自己翻成「不跟随」,永远看不到
  「改之前在跟随」这个事实。直接置标志,幂等。(这一版第一次写就踩了,用例是红的。)
- 换主题时若处于跟随态,顺带把色块重灌成新主题配套方案的颜色(载入阶段 `_suppressPreview`
  为真,不会改动刚读进来的设置)。

回归用例 `TerminalColorSchemeSelectionTests` 五条:非 Dracula 主题下选 Dracula 真的生效且不跳回、
配套方案本身也能被钉住、选回「跟随主题」清空覆盖并显示配套色、跟随态下改单色即脱离跟随、
老配置(无标志)按老口径判定 —— 最后一条同时钉住「没改过的老用户仍跟随」与
「改过配色的老用户覆盖不丢」。

## ✅ 27. 2026-08-31 再补三套主题:One Dark / One Light / Sakura(用户反馈)

用户要「一套主流暗色 + 一套主流亮色 + 一套类似 VS 粉色主题的亮色」。主题数 9 → 12(七暗五亮),
终端方案 14 → 16。

- **One Dark**(`one-dark`)—— 编辑器暗色主题里装机量最大的那一脉(Atom / One Dark Pro),
  蓝强调、灰蓝底,谁都认得出。底色阶梯照它自己那套(`#21252B` 侧栏 / `#282C34` 编辑区),
  配套终端方案 **One Dark** 本来就在内置表里,背景恰好对得上,一行色值都不用新编。
- **One Light**(`one-light`)—— One Dark 的亮色孪生:同一套语法色搬到白底上,
  两套换着用不换性格。新增配套终端方案 **One Light**。
- **Sakura**(`sakura`)—— 粉樱亮色,带粉调的近白底 + 深玫瑰强调(`#C2185B`)。
  新增同名终端方案。

### 两处调过原版的地方,都是被对比度尺子逼出来的

- **One Light 的绿与黄**:原版 `#50A14F` / `#C18401` 压在它自家侧栏底(`#F0F0F1`)上只有
  **2.81:1**,状态点(已连接 / 连接中)在侧栏上认不出来 —— 而侧栏正是状态点唯一出现的地方。
  压深一档到 `#3F8B3E` / `#A87300`,其余照抄。
- **Sakura 的错误色**:粉色主题最容易翻车的地方是把语义色也调得又粉又淡 ——
  一屏字糊在一起,而且「错误」与「强调」两种红分不开。这里错误取偏橙的深红 `#D84315`,
  与玫瑰强调拉开色相;ANSI 十六色也全部压到白底上读得出的深度(1–6 号最低 4.2:1)。

三套都是先过 `UiThemeCatalogTests` 那把尺子才落地的,新增主题的流程也就这一步:
填一组种子色 → 跑用例 → 红了就调。

### 顺带记一笔:出厂强调色会盖掉每套主题自己的强调色

`AppSettings.AccentColor` 的**出厂默认是 `#E91E63`(粉)**,而强调色覆盖的优先级高于主题令牌
(`App.ApplyAccent` 遮蔽 `VelaAccent` 三件套)。也就是说全新安装下,十二套主题的强调色都会被
这一个粉色盖住 —— One Dark 的蓝、Nord 的冰青、Gruvbox 的琥珀都看不见,除非用户手动清空
「设置 → 外观 → 主题色」。

这是本批之前就有的行为(那时只有两套主题,盖掉的只是 Dracula 紫),不在本次改动范围内,
**未动**。要让每套主题的强调色如实生效,把这个默认值改成空串即可(空 = 跟随主题);
存量用户的配置里已经写着 `#E91E63`,不受影响,只影响新装。

## ✅ 28. 2026-08-31 切主题发卡(用户反馈:「感觉有点卡,是错觉吗?」)

不是错觉。切一次主题原来要惊动可视树六十多遍,外加把每个终端标签的字体白重设一遍。
用一个临时的 headless 用例量了一下(合成树:400 个带 `DynamicResource` 绑定的控件 + 一个真终端):

| 环节 | 改前 | 改后 |
| --- | --- | --- |
| 贴一套主题令牌 | **40~57 ms** | **1.65 ms** |
| 同样的写入但不挂在可视树上 | 0.04 ms | — |
| 单个令牌写入 | 0.29 ms(×64 ≈ 全部代价) | — |
| 重设同名终端字体 | 12.7 ms(对照:仅重绘 10.4) | 与仅重绘持平 |

### 一、六十多次全树重解析 → 一次整格替换

`ThemeTokenApplier` 原来把六十多个令牌**逐个**写进 `Application.Resources`。资源字典每被写一次
就沿树发一遍变更通知,树上**每一处** `DynamicResource` 都要重新解析 —— 代价与写入次数成正比
(实测单次 0.29 ms × 64)。

改为:令牌先写进一个**游离**字典(0.04 ms,不通知任何人),再整格换进
`Application.Resources.ThemeDictionaries` 当前明暗那一格。查找优先级不变(主题字典高于合并字典,
应用级自有条目仍高于主题字典,所以强调色覆盖照旧生效),但通知从 64 次降到 3 次
(摘旧的、挂新的、认领 Owner)。

`Apply` 因此分成两个:`Fill(字典, 主题)` 只负责把令牌填进给定字典(用例与内部复用),
`Apply(Application, 主题)` 负责建字典 + 换格。

**顺序也调了**:具名主题现在**先贴令牌再换 `RequestedThemeVariant`**。反过来的话,切到那一格时
它还装着上一套主题的调色板,整棵树要先按旧色重解析一遍、下一句再重解析一遍 —— 白多一次全树解析,
还会闪一下旧色。「跟随系统」仍是先定变体(不然不知道该往哪一格贴)。

### 二、字体白重设

`VelaTerminalControl.FontFamily` / `FontSize` 的 setter 无条件 `RecomputeMetrics()` +
`RelayoutFromBounds()` + 重绘 —— 而 `RecomputeMetrics` 会把**字形缓存整个丢掉**,下一帧整屏重新塑形。
宿主的「把当前设置刷到所有终端」是一条通用路径(保存设置、换主题、插件面板都会走),字体多半根本没变。
两个 setter 各加一句相等判断即可,每个标签省下 2~7 ms。

### 三、跨明暗切主题时,终端设置刷了两遍

`MainWindowViewModel` 既订阅 `IThemeService.ThemeChanged`,又订阅
`Application.ActualThemeVariantChanged`(为了「跟随系统」下系统明暗翻转)。选具名主题跨明暗时
两条都会响,于是所有终端标签的设置被刷两遍。后者加一个判断:只在当前是「跟随系统」时才处理。

### 四、留了一条回归保险

`ThemeTokenShadowingUiTests.ApplyingATheme_NotifiesTheTreeAConstantNumberOfTimes` 数的是**通知次数**
(≤ 4)而不是耗时 —— 耗时断言在 CI 上必然抖,而次数一旦回到逐个写就会线性涨到六十多,一测就红。

## ✅ 29. 2026-09-01 命令行装的插件被判「收据缺失」(用户反馈)

`vela-plugin install velashell.redis` 一路正常(下载、摘要、验签、兼容性都过,末尾打印
`signature Valid`),重启后插件管理页却把它标成**无效**:

> Protected installation receipt is missing. Reinstall this plugin through the plugin manager.

### 一、错在宿主,不在命令行

安装收据落在宿主进程的信任库里(SonnetDB + `ISecretProtector` 认证加密),命令行够不着 ——
这一点 CLI 手册、dev-guide、publishing 三处都写明了,而且都写的是「代价仅仅是没有事后防篡改」。
但 `PluginManager` 把「没有收据」实现成了**拒绝装载**,收养只在升级后第一次启动做一轮
(`LegacyInstallMigrationCompleted`)。于是那一轮之后新出现的目录永远拿不到基线:
命令行装的、以及 dev-guide 里「方式三:直接放目录」,全部卡在这条错误上。

### 二、收养改成随用随做

`PluginManager.ValidateInstallReceipt` → `VerifyOrAdoptInstallReceiptAsync`,两种收据给出的保证
第一次被明确区分:

| 收据来源 | 内容变了怎么办 |
| --- | --- |
| 管理页安装(`LegacyAdopted == false`) | 宿主在解包后亲手落的,确实等于「目录出自那个包」;变了一律拒装载 |
| 旁装(命令行 / 直接放目录) | 第一次见到时记的 TOFU 基线;变了(多半是 `vela-plugin update`)重记一遍 + 一条日志 |

基线写不进信任库时**不放行** —— 否则每次启动都要重新收养一遍,这份保护等于不存在。

### 三、顺手清掉孤儿收据

`vela-plugin uninstall` 只删目录,够不着信任库。留着那份收据,同一个 id 下次装回来时内容必然
与旧收据对不上,插件会以「文件被改过」被拒,而用户什么都没改过。启动加载信任状态时,
把目录已经不存在的收据清掉(`PruneReceiptsWithoutDirectory`)。

### 四、代价说清楚

安全上没有让步:值钱的是第一种收据,一个字没动。旁装目录先于宿主存在,没有任何东西能证明它
出自哪个包,拒绝它挡不住「能往插件目录写文件的进程」(那种进程本就以用户身份在跑),
却会把文档写明支持的两条安装路径全堵死。**但同一个插件别把两条路混着用**:用命令行覆盖一个
经管理页装的插件,宿主只会看到内容变了,仍会标 Invalid —— 先在管理页卸载再装即可。

测试:`PluginInstallUninstallTests` 新增/改写三条(直接放进去的目录被收养并激活、旁装内容变了
重记基线不标红、目录被外部删掉后收据清掉且同 id 能重装),原有两条防篡改用例保持通过;
`TestCategory=Plugins` 127 条全绿。文档已在 velashell-docs 同步(`{zh,en}/cli/cli.md`、
`{zh,en}/templates/dev-guide.md`、`{zh,en}/plugins/STATUS.md`)。

## ✅ 30. 2026-09-02 AI 插件:自定义供应商也能自动拉模型清单(用户反馈)

> 「帮我给我的 AI 插件添加自动获取可用模型列表的功能,比如自定义的模型提供商……
> 是否可以使用 xxx/v1/models 的接口来获取。」

### 一、原来为什么拉不到

「拉取模型」这件事此前只有一个数据源 —— models.dev,而目录里没收录的那几家
(Ollama、`custom-openai`、`custom-anthropic`、`custom-oauth`)`ModelsDevId` 是空的,
于是按钮直接不显示:`SettingsView` 判 `ModelsDevId.Length > 0`,`ProviderSetupView`
在 `PullModelsAsync` 开头 `return`。手工新建的供应商 `CatalogId` 为 null,同样落在这一侧。

顺带查出第二层:`PullModelsButton` 摆在 `ProviderAuthPanel` 里面,而那一块只在
**订阅登录**时可见 —— 也就是说填 Key 的供应商即使被 models.dev 收录,那个按钮也从没露过面。
按钮已移到鉴权两块之外,两种鉴权方式都摆得出来。

### 二、端点优先,models.dev 补规格

两个数据源各知道一半,谁也替代不了谁:

| 数据源 | 知道 | 不知道 |
| --- | --- | --- |
| 端点自己的 `/models`(`EndpointModelCatalog`) | 这个地址**实际供应哪些型号** | 窗口、单价 |
| models.dev(`ModelsDevCatalog`) | 窗口、三档单价、能力位 | 这家中转站转发了哪几个 |

所以顺序是先问端点、再按 id 配规格(`ModelPull.RunAsync`,两处界面共用);端点没有这条接口
(订阅型私有后端就没有)或请求失败时,整条退回 models.dev 的清单 —— 也就是这次改动之前的行为。

协议差异照搬发对话请求时那一套:OpenAI 系 `{BaseUrl}/models` + `Authorization: Bearer`;
Anthropic `{BaseUrl}/v1/models` + `x-api-key`(登录换回来的令牌才是 Bearer)+ `anthropic-version`;
Ollama 走它的 OpenAI 兼容层。回应认三种形状:`{"data":[…]}`、裸数组、`{"models":[…]}`。

### 三、两处降级是刻意的

**向量/语音/画图/审核模型按名字滤掉,宁可漏筛也不误筛。** `/models` 把一家的全部模型都报上来,
它们不像 models.dev 那样带能力字段可判,只能按名字认 —— 漏一个只是列表里多一条用不上的,
误筛一个则是用户在下拉里**永远找不到**自己要的模型,且没有任何提示。长词按子串认,
短词(`tts`/`embed`)按整段认。

**中转站的单价一律留空。** 自定义端点没有 models.dev 的供应商 id,只能跨供应商按 id 找规格
(顺带剥掉 `anthropic/` 这类前缀)。窗口是模型自身的属性,谁家跑都一样,照补;
**价目却是各家自己定的**,中转站普遍加价,照抄原厂单价会让花费估算**静默地**偏低 ——
那正是最难被发现的一类错。空着至少还写在脸上。

测试:新增 `EndpointModelCatalogTests` 19 条(地址拼接、三种协议的鉴权头、三种回应形状、
去重排序、滤除与不误筛、坏报文降级、无地址不出网),`ModelsDevCatalogTests` 增 5 条
(`Describe` 的四种配法与顺序);插件测试 364 条全绿,`VelaShell.slnx` 零警告。

## ✅ 31. 2026-09-02 AI 插件:左栏模型列表可折叠(用户反馈)

> 「为我优化我的模型提供商,让其可以折叠其中的模型列表,现在一个提供商几百个模型要滚动半天。」

第 30 条的直接代价:端点报上来多少就落成多少个模型条目,设置页左栏是一棵摊平的两层树
(`SettingsView.ReloadList`),几百行一路滚到底。

### 一、折叠状态存在哪、默认是什么

`AiProvider.ModelsExpanded` 是 `bool?` 而不是 `bool` —— 要把「用户折过/展过」与「还没碰过」分开:
一律默认展开,几百个的那家一进去就是长龙;一律默认折叠,只配了两三个模型的人每次都得先点开
才看得见自己的东西。**只有「还没表过态」这一档才轮得到自动判断**(`AutoCollapseFrom = 12`,
一屏左栏大约摆得下二十来行);用户一旦手动折过或展过,那就是他的选择,不该被数量规则改回去。

拉取模型之后 `ModelsExpanded` 复位成 null:上次表态时面对的是另一份清单(往往是出厂那一条),
拿旧决定套新长度没有意义。

### 二、两条不能少的规矩

**选中项落在某一家的模型里时强制展开**(`IsVisiblyExpanded`)。否则右边的表单停在一个左栏
看不见的模型上,用户没有任何办法看出自己在编辑谁。

**折起来时若正选着这一家的模型,选中项上移到供应商行**(`Toggle`)。同理:那一行会随整批模型
从列表里消失。

### 三、交互

**供应商行整行都是折叠热区**(用户追问:「我希望他能点击名字也能正确的折叠,而不是只能点击
签名的三角」)—— 文件树里点文件夹名就该折叠,只让一枚 10px 的三角管这件事是把功能藏起来。
`PointerPressed` 里 `e.Handled = true`,选中项由折叠逻辑自己落到这一家的供应商行上,
不放给 `ListBox`:折叠会整个重建列表,被点中的那个容器当场失效,让它在一个已经拆掉的容器上
算选中项,选出来的是哪一行没人说得准。模型行与没挂模型的供应商行不在此列,照常只是被选中。

键盘按树形控件的约定:→ 展开、← 折起;模型行上的 ← 是「回到我这一家」,不顺手折起来
(一次按键做两件事,想再展开时会发现自己已经不在原来那一行了)。**必须走隧道阶段挂**:
`ListBox` 自己的类处理器把左右键当成条目导航吃掉了,冒泡阶段再挂时 `Handled` 已经是 true。

供应商行名字后面跟一个模型个数(`.count` 样式,排在 `:selected` 之后 —— 元数据不该跟名字
抢强调色):折起来之后仍看得出这一家有多少个,那正是决定「要不要展开」的依据。

测试:`SettingsViewUiTests` 新增 7 条(折起后模型行整批离开列表且状态落盘、点名字与点箭头
折叠效果一致、点模型名不触发折叠、点击时选中项上移到供应商行、长清单默认折叠而短清单不折、
选中项在里面时自动折叠让路、模型行上的 ← 只回父行);插件测试 371 条全绿,`VelaShell.slnx` 零警告。

## ✅ 32. 2026-09-02 资源管理器:会话树改成摊平的平列表(用户反馈)

> 「资源管理器中的列表折叠和显示的效果我觉得不太好。展开后他的子选项前面会有个空白区域。
> 按照 AI 插件这个模型列表的实现效果就挺好。」

### 一、那条空白是 TreeView 的层级缩进区

设计 FrJPu 的会话树:箭头自绘、紧挨文件夹图标,缩进由行 Border 的 padding 给。而 `TreeView`
给每一层预留一块缩进区与一枚内置箭头,于是原实现只能拿一串<b>按模板部件名去关灯</b>的样式把
内置那套压掉(`PART_ExpandCollapseChevronContainer` 归零、`PART_LayoutRoot` 背景压透明……)。
压不干净,展开后的子行整行被往右推 —— 前面那一块既<b>点不着</b>、也<b>不跟着行高亮</b>,
就是用户看到的空白。而且它是「换个 Avalonia 版本部件改名了就悄悄回来」的那一类问题。

### 二、摊平

`SessionTreeViewModel` 新增 `Rows`:每个根级节点一行,展开的分组后面紧跟它的会话行;
界面从 `TreeView` 换成绑 `Rows` 的 `ListBox`(与 AI 插件设置页左栏同一个做法)。
摊平之后每一行都是同一层的普通行,行背景从最左画到最右,那条空白从根上不成立 ——
上面那一整段压制内置模板的样式也随之删掉了。

`Rows` <b>就地对齐</b>而不是清空重建:清空会让 `SelectedNode` 被列表控件顺手清成 null
(它跟 `SelectedItem` 双向绑),折一下分组就把用户的选择弄丢了。对齐分三步 ——
移走不该在的、把错位的 `Move` 到位、补上缺的,顺带天然支持了「会话在分组之间搬家」的重排。

折叠把选中的会话收进去时,选中<b>上移到它那一组</b>(与 AI 插件那边同一条规矩):
不然选中项从列表里消失,而右键菜单里的命令仍然对着一个看不见的会话执行。

`Nodes` 保持不变,仍是数据形状(两层),`Rows` 由它派生 —— 拖放落点解析、状态回放、
分组增删那些逻辑一个字没动。

### 三、顺带修掉的一处

`BringSelectedSessionIntoView` 原来是遍历可视树找那一行再 `BringIntoView`。行是虚拟化出来的,
选中项在视口外时压根没有对应的控件可找 —— 而「选中了却没滚过去」恰恰只在列表长的时候发生。
改成 `ListBox.ScrollIntoView`。

测试:新增 `SessionTreeRowLayoutUiTests` 2 条(展开后的会话行左边缘与分组行同为 0、
两行都铺满整行宽;折起来后会话行整批离开列表),把那条空白钉死在测试里;
`SessionTreeViewModelTests` 新增 5 条覆盖 `Rows` 的摊平、折叠/展开、选中上移、
隔壁组折叠不动我的选择、跨分组搬家后的行序。`SidebarQuickCommandsUiTests` 里取控件的
`TreeView` 改成 `ListBox`。`TestCategory=SessionTree` 46 条全绿。

> **一处与本条无关的既有失败**:`ShortcutCatalogTests.Doc_ListsEveryCatalogEntry` 读
> `docs/快捷键参考.md`,而该文件在 `f0f492a`(文档搬去 velashell-docs)时已从本仓库删除,
> 测试没跟着改 —— 现在必然抛 `FileNotFoundException`。不在本次改动范围内,单独处理。
>
> **2026-09-03 已处理**:改为按 `VELASHELL_DOCS_DIR` 或"与本仓库并排检出的 velashell-docs"
> 去找 `zh/host/快捷键参考.md`,找不到报 Inconclusive 跳过。没有直接删掉这条用例 ——
> 那份文档的「维护约定」一节明写着由它把关,删了等于让文档那句话变成空头承诺。
> 顺带记一笔教训:**这条用例红了几个月没人管**。常年红着的用例和没有用例是一回事,
> 而它红的那几个月里,快捷键与文档是否漂过没人知道(这次接上之后逐条比对是过的)。
## ✅ 33. 2026-09-02 协作接入:IM 桥接(飞书/钉钉/Telegram/企微)+ 对外 MCP 服务端

两件事一起做,因为它们是同一个能力的两个方向,而且共用同一套安全观念:

- **往外**:团队在飞书/钉钉/Telegram/企微里 @ 机器人,VelaShell 的 agent 在**已连上的**
  SSH 会话上干活,结果回帖到群里 —— 对齐 Hermes Agent 的消息网关那一层;
- **往内**:Claude Code / Codex 这类外部 agent 把 VelaShell 当 MCP 服务端调,
  用的就是 `AgentToolbox` 里那套工具。

全部落在 `plugins/VelaShell.Plugin.Ai/`(`Bridge/` 与 `Interop/`)。**AI 插件因此改为
`onStartup` 激活** —— 桥接必须常驻,不能等用户点开面板才建连;换来的代价是启动多一次
程序集装载与一次配置读取(两条服务都关着时立刻返回)。

### 一、四个渠道,四种入站传输

抽象是 `IMessageChannel`:只管收发,"跑到断为止";重连退避统一在 `ChannelHub`。
四家刻意各选一种传输,把抽象压到位:

| 渠道 | 入站 | 能改已发消息 | 备注 |
| --- | --- | --- | --- |
| 飞书 / Lark | 官方长连接(WebSocket + pbbp2 帧) | 能 | 帧格式官方未公开,照 `larksuite/oapi-sdk-go` 的 `ws/pbbp2.pb.go` **手写**编解码,不引 protobuf 运行时 |
| 钉钉 | Stream 模式(WebSocket + JSON 帧) | 不能 | 协议官方有公开文档;发消息走带令牌的接口,**不用 sessionWebhook**(只能发 5 条、1.5 小时过期) |
| Telegram | Bot API 长轮询 | 能 | 走宿主全局代理 |
| 企业微信 | 公网回调(本机 HTTP 监听) | 不能 | 唯一需要公网入口的一家;只绑 127.0.0.1,前面自己接隧道 / 反代 |

飞书那条长连接有两个坑写在代码注释里:一个应用最多 50 条连接,而且多客户端时平台按
**集群**投递(随机挑一个)—— 同一套凭证别在两台机器上同时跑。

### 二、安全默认:能少给就少给

- **白名单为空 = 谁都不理**。群 id 在飞书/钉钉界面上看不到,所以第一次被 @ 时回一句
  (且只回一句)带 id 的提示,否则用户根本没法完成配置;
- 桥接默认**计划档(只读)**,比面板保守一档 —— 面板前面坐着人,IM 那头的人可能在地铁上;
- `/mode` **默认只能往低了调**。白名单里任何人都能 `/mode agent` 的话,设置页那个"只读"
  就形同虚设;要放开得去设置页勾 `AllowModeEscalation`;
- 审批走**文本回复**(`y`/`n`/`a`)而不是交互卡片:四家的卡片回传各有各的坑
  (飞书 `card.action.trigger` 在长连接上并不总能收到,上游项目挂着同样的 issue),
  文本是唯一四家都稳的通道。审批人可与"能说话的人"分开配;
- 绑定存 `user@host:port` 而**不是 SessionId** —— 后者断线重连就换一个,存它的话群里的绑定
  过夜就失效。

### 三、无头 agent 回合另起一条,不动聊天面板

`Bridge/BridgeAgentRunner`。`ChatPanelView.SendAsync` 把装配、流式渲染、审批卡片、插话与压缩
缝在一起、每一步都直接写 UI 控件;把它抽成界面无关要动那个 2500 行文件的骨架,风险远大于
并排写一条只做桥接需要的路。真正值钱的零件(`AgentToolbox` / `ContextBuilder` /
`AiSettingsStore` / `McpManager` / `ChatHistoryStore`)本来就界面无关,直接复用,重复的只有编排。

### 四、对外 MCP 服务端

`Interop/McpEndpoint`:Streamable HTTP(不是 stdio —— stdio 的前提是客户端能把服务端拉起来,
而 VelaShell 是一个已经开着的桌面程序)。只绑 127.0.0.1,**每个请求必须带令牌**:本机端口
同机任何进程(包括浏览器里的页面)都能敲,令牌是唯一的门。工具直接由 `AgentToolbox` 产出,
外加一个 `use_session` 让外部 agent 挑机器(工具箱靠 `SessionIdProvider` 拿会话,签名里没有这个参数)。

**审批在这条路上没有界面**,所以 `ApprovalMode.Ask` 等于一律拒绝写操作(工具箱在
`ApprovalHandler` 为 null 时就是这个行为)。要让外部 agent 能改东西,用户得显式选只读放行
或绕过审批 —— 这是一个明摆着的选择,而不是一个悄悄的默认。

### 五、已知限制(需要 SDK 新契约才能解)

`ISessionsApi` 只有 `ListAsync` / `GetAsync`,**开不了新会话**。所以两条路上的 agent 都只能
操作"用户已经连上的机器";没有连上的,只能回一句让人去 VelaShell 里连。要让它自己按保存的
配置连一台,得给 SDK 加契约(按 AGENTS.md 走 velashell-plugin-sdk 的发版流程)。

> **已解(宿主侧,2026-09-03)**:SDK 2.0.2 补上了 `ListSavedAsync` / `OpenAsync` / `CloseAsync`,
> 宿主实现见第 35 节。AI 插件的 `AgentToolbox` 尚未接上,所以本节描述的现象暂时还在。

### 六、踩到的坑

**`TextWrapping="Wrap"` 的多行 TextBox + 竖滚动条 Auto = 布局死循环。**外层 ScrollViewer 的
滚动条一旦出现就压窄可用宽度 → 文本重排变高 → 还是要滚动条 → 收回…… measure/arrange 无限
震荡,窗口整个卡死;headless 测试里表现为一分钟超时,**连异常都没有**。内容为空时不发作,
填上内容才挂 —— 所以它很容易溜过第一版用例。协作页那个"接入方式"框因此固定 NoWrap
(内容本来就是整段复制走的,横向滚动够用),注释已留在 `CollaborationView.axaml`。

测试:`Pbbp2Tests`(帧编解码,含未知字段跳过与截断报错)、`WeComCryptoTests`(验签与报文布局)、
`McpEndpointTests`(真起 HTTP:initialize / tools/list / tools/call / 鉴权拒绝)、
`BridgeRouterTests`(白名单、群里没 @ 不理、斜杠命令、`/mode` 不许提权)、`SessionTargetsTests`、
`CollaborationViewUiTests`(headless 真装载)、`LocTableTests`(插件那张多语言表必须齐五语 ——
少一项是运行时 `IndexOutOfRangeException`,而且只有切到日/韩才撞得到)。
`VelaShell.Plugin.Ai.Tests` 256 条全绿,全仓 `dotnet build` 无警告。

✅ **文档已同步**(2026-09-07 复核):`zh/plugins/协作接入.md` 与 `en/plugins/collaboration.md`
均已落地(渠道配置步骤、安全模型、MCP 接入方式),`{zh,en}/plugins/STATUS.md` 也已登记。

## ✅ 34. 2026-09-02 协作接入的配置流程返工(用户反馈:"要填一堆文本框")

第一版把开发者后台的东西原样誊了一遍。真正费事的其实不是那两个凭证(各复制一次而已),
而是后面那趟:**加机器人进群 → 发一句 → 看它回的群 id → 复制 → 回电脑粘进白名单 → 保存 → 重连**。
人在手机跟前,电脑在工位上,这一趟纯属受罪。

### 一、先说清楚 Hermes 的"扫码"是什么

用户提到 Hermes 可以扫码接入。它其实是两件不同的事:

- **WhatsApp / Signal**:设备链接 —— agent 挂成你账号的一台已链接设备,压根没有"应用"要注册,
  所以确实一扫就完;
- **飞书/钉钉/企微**:bot 的 `app_secret` 只能从开发者后台拿,**没有任何扫码能变出它来**。

(中文教程里流传的"扫码自动创建飞书应用"未经官方文档证实,没有照它实现。)

所以这一版不去做"扫码替代填凭证",而是把真正冗长的那一段砍掉。

### 二、配对码:授权一个群不用再回电脑

`Bridge/PairingService.cs`。设置页点一下生成六位码,在要授权的聊天里发 `/pair 428913` 即可。

- 一次性、十分钟过期、**猜错五次直接作废**,随机数走 `RandomNumberGenerator`(它在有效期内
  是一个能把陌生聊天放进白名单的凭据,用 `Random` 等于给知道规律的人留门);
- **只能加白名单**,动不了挡位与审批 —— 能不能在服务器上干活仍旧归那两项管;
- `/pair` 刻意**不要求群里先 @ 机器人**:此刻它还不在白名单里,再叠一条"先 @ 我"
  等于把配对本身也挡在门外;
- 放行同时写内存与库:只写库要等重载才认,只写内存重启就没。库里还没有这个渠道
  (设置页加了没保存)时给一条 warn,不让它静默地只活到下次重启。

### 三、一键放行:敲过门的聊天直接列出来

被白名单挡掉的聊天会被记下(`PendingChat`),设置页一行一个卡片:哪个渠道、群还是单聊、
谁在说话、聊天 id,右侧 [允许] / [忽略]。点允许即时生效并落盘,同时把 id 填进上面那个
白名单框 —— 否则用户接着点保存,反而把刚放行的又抹掉了。

清单挂在 `BridgeService` 而不是路由器上:设置页一保存就整体重建路由器,
"刚才有个群敲过门"这条线索不该被那一下抹掉。

### 四、[测试] 按钮:填完当场验,不用保存再翻日志

`Bridge/ChannelProbe.cs`,用界面上**当前**填的值去试(不保存)。飞书那条特意多走一步:
换到令牌之后再问一次长连接接入点 —— 接飞书最常见的两种翻车不是密钥填错,而是
**事件订阅没改成长连接**与**改完没发布版本**,这两种情况下密钥完全正确却一条消息都收不到。
探测全程只读:换令牌、查自身信息、问接入点,不发消息不改配置。

### 五、二维码用在真正省事的地方

测试通过后,如果拿得到"把机器人加进群"的链接就渲染成二维码(飞书 `applink.feishu.cn`、
Telegram `t.me/<bot>?startgroup=true`),手机扫一下直接跳过去,省掉在手机上按名字搜应用。
新引 `QRCoder`(MIT、netstandard2.0、无传递依赖),走 `PngByteQRCode` 出字节流交给
Avalonia 的 `Bitmap` —— 不碰 `System.Drawing`,Linux/macOS 上不需要 libgdiplus。

自己写 QR 编码器要 Reed-Solomon 与掩码评分,几百行且没有额外价值:与自己实现的 VT/ZMODEM 不同,
这里没有需要拿捏的协议细节。

> **1.4.8 后记:上面这段判断是错的,已改为在插件内实现二维码编码器、替换 QRCoder(`Ui/QrCode.cs`)。**
> "无传递依赖"没核实:QRCoder 1.8.0 的 netstandard2.0 目标依赖 `System.Drawing.Common 6.0.0`
> (再带 `Microsoft.Win32.SystemEvents`)—— 正是那个 6.0 之后只支持 Windows 的库,
> 绕开 `PngByteQRCode` 也没能真的绕开它。而且它带进来的 `runtimes/{win,unix}/lib/net6.0/`
> 目录名里有点号,被 macOS 的 `codesign --deep` 当成嵌套 bundle,把 1.4.8 的 dmg 打包整个炸掉。
> 代价也比预想的小:只做字节模式(编的全是带小写的 URL,数字/字母数字模式一次也用不上),
> 连注释三百多行,正确性由与独立实现逐格比对出来的黄金用例把关。
> **教训是"新引一个包之前先看清它的传递依赖",不是"能自己写就自己写"。**

### 六、顺手修的一处 UX

设置页原来要等三秒(定时器)才刷出待放行清单,现在加载完就先刷一次;定时器只负责
"页面开着时后来又有人敲门"。

于是整条链路变成:**填两个框 → 点测试 → 扫码把机器人拉进群 → 群里发一句 `/pair` → 完事**,
全程不用抄任何 id。

测试:`PairingServiceTests` 8 条(六位数字、一次性、猜错五次作废、重发作废旧码、待放行去重与排序)、
`BridgeRouterTests` 新增 6 条(有效码放行且落盘、错码不放行、没发过码不放行、缺参数给用法、
陌生聊天被记下、放行后从清单消失)、`CollaborationViewUiTests` 新增 4 条
(配对码显示、没开桥接时说明原因、待放行卡片带「允许」、**二维码真的画得出 Bitmap** ——
新依赖里这条最可能"编译得过、运行才炸")。
`VelaShell.Plugin.Ai.Tests` 275 条全绿,全仓 `dotnet build` 无警告。

### 七、真机联调抓到的第一个 bug:`PostAsJsonAsync` 把字段名 camelCase 掉了

用户拿真凭证一测,报 `credentials OK, but the long-connection endpoint was refused:
Feishu endpoint request failed: Bad Request (code 9499)`。

拿真凭证对同一个接口打两次,把两种写法并排比:

```
camelCase   → HTTP 400  {"code":9499,"msg":"Bad Request"}
PascalCase  → HTTP 200  {"code":0,...,"URL":"wss://msg-frontier.feishu.cn/..."}
```

`HttpClient.PostAsJsonAsync` 用的是 `JsonSerializerDefaults.Web`,它会把属性名转成
camelCase —— 匿名对象里写 `AppID` 发出去是 `appID`,而飞书这个接口要的是逐字的
`AppID` / `AppSecret`。

**阴险的是同一个类里换令牌那条用的是 `app_id`(本来就小写开头),camelCase 动不了它。**
于是症状是"凭证明明是对的,只有接入点这一步被拒" —— 最不容易往序列化上想的那种组合。

修法不是"调用时记得传对 options",而是把字段名用 `[JsonPropertyName]` 钉在类型上
(`FeishuApi.EndpointRequest`):选项跟着每一个调用点走,特性跟着类型走,下次谁再加一个
调用点也不会中。回归用例 `FeishuApiTests` 刻意**按 Web 默认值**序列化一次 ——
用 `JsonSerializerOptions.Default` 是测不出这个 bug 的,那正是当初漏掉它的原因。

其余三家不受影响:钉钉本来就是 camelCase(平台也要 camelCase),Telegram 与飞书发消息用
snake_case,企微用全小写,camelCase 策略对这些名字都是恒等变换。

### 八、协作接入页没吃上主题(用户反馈:"逐个检查处理")

不是"按钮没居中"一处,是我建这一页时**整体没照 `DESIGN.md` 走**。逐个控件对完之后:

| 问题 | 后果 |
| --- | --- |
| 六个按钮都没挂宿主的按钮主题 | `VelaOutlineButtonTheme` 里带着 `HorizontalContentAlignment="Center"` —— 不挂就是文字不居中(用户先看出来的那一条),配色也不是同一套 |
| 保存栏写了 `VelaBorderSubtle` | **这个令牌根本不存在**(只有 `VelaBorderPrimary` / `VelaBorderSecondary`)。那条分隔线从落地起就没画出来过 |
| 代码建的两个勾选框只挂了 `AiCheckBoxTheme` | 那个主题只管方块的画法,旁边那行字仍旧用控件自己的 `FontSize` / `Foreground` —— 于是比 XAML 里的大一号、颜色也不跟主题走 |
| 待放行卡片里的标题 `TextBlock` 没有 class | 拿到的是 Fluent 默认前景色,换主题不跟着变 |
| 卡片自己另写了 `Padding`、标签自己另写了 `Margin` | 把 `Border.card` / `TextBlock.label` 里定好的那一档盖掉了 |
| 二维码是张裸的黑白图 | 直接糊在深色卡片上;现在套一层 `Border.card`(码本身不跟着主题反色 —— 反色的 QR 很多扫码器读不出来) |

顺带:平台名补上拉丁写法(`钉钉 / DingTalk`)—— 界面能切日/韩,"钉钉"两个字对那边的用户不是可读的品牌名。
`DialogStyles.axaml` 新增一档 `TextBlock.body`(12 / Primary):在这之前"卡片里的正文一行"只能靠不写 class 蒙混过去。

### 九、加两道守门用例,免得再漂回去

**`{DynamicResource Xxx}` 拼错不会报错,只是什么都不做。** 而 headless 用例挡不住这一类:
测试进程里压根没装载宿主的令牌字典,所有键都解析不到,拼对拼错看起来一模一样。
所以 `ThemeTokenUsageTests` 按**文本**比对:

- `EveryResourceKeyUsedByThePluginIsDefinedSomewhere` —— 扫插件全部 `Ui/*.axaml` 引用的资源键,
  必须能在 `src` / `plugins` 的 axaml(`x:Key`)或 C#(`Resources["…"] =`,本地化提示就是这么给的)里
  找到定义。剥掉 XML 注释,否则注释里举例用的键会被算成引用。
- `EveryButtonOnTheCollaborationPageWearsAHostTheme` —— 这一页 XAML 里的每个 `<Button>` 都必须挂
  `Vela*ButtonTheme`。

两条都**先把 bug 放回去验过会红**:一条点名 `VelaBorderSubtle`,一条把没挂主题的那个按钮整行打印出来。
守门用例不验证它抓得住,和没有是一回事。

找不到仓库根时这两条**失败**而不是跳过 —— MSTest 把跳过记成通过,一条永远绿的守门用例比没有更糟。

**顺带发现但没动**:宿主自己的 axaml 里 `VelaBgPrimary` 与 `VelaStatusError` 两个键也找不到定义
(`StringScrollBar*` 是 Avalonia 内置的,不算)。不在本次范围内,单独记一笔。

`VelaShell.Plugin.Ai.Tests` 279 条全绿,全仓 `dotnet build` 无警告。

### 十、按钮居中:前两次都没修对(用户第三次反馈)

前两次都是"看着像对的"就交了,实际没解决。真原因有两条,单看代码都看不出来:

1. **`VelaAccentPillButtonTheme` 没有 `HorizontalContentAlignment` 这个 setter**
   (`VelaOutlineButtonTheme` 第 85 行有)。它只在模板里 `TemplateBinding`,而 `ContentControl`
   的默认值是 `Stretch` —— 纯文字内容被拉开,看起来就是没居中。保存按钮用的正是这个主题。
   宿主自己那几个 pill 按钮都塞了带 `VerticalAlignment` 的 `StackPanel` 当内容,恰好盖住了这一点。
2. **代码里 `new` 出来的按钮不能用 `TryFindResource` 取主题**:资源查找沿逻辑树往上走,
   而那时控件还没进树,一律落空,**而且是静默落空**。`测试` / `移除` / `允许` / `忽略`
   四个按钮从来没挂上过主题。

改法:不再在每个按钮上写 `Theme=`,改成样式表里的 `Button.host` / `Button.primary` / `CheckBox.host`
三档 class。样式是在控件**进树时**套上去的,`DynamicResource` 那时才解析 —— 时序问题自然消失;
两档里都显式补了 `HorizontalContentAlignment="Center"`,不指望宿主哪天会给 pill 补上。

### 十一、这次是渲染出来看过的

前两次的教训是:**headless 用例证明不了主题真的挂上了** —— 测试进程里宿主的资源字典根本没装载,
`VelaAccentPillButtonTheme` 解析不到,于是按钮退回 Fluent 默认主题(它自带居中),
"修了"和"没修"在 headless 里长得一模一样。文本级用例更弱,只能看出 XAML 里写没写。

所以在 scratchpad 起了一个 Skia 控制台工程,merge 宿主真实的令牌与 `ButtonThemes.axaml`,
把整页渲染成 PNG 看。做法记进了记忆(`previewing-plugin-ui-headlessly`),其中最坑的一步:
**必须挂到真正的 `Window` 上再 `CaptureRenderedFrame()`** —— 对脱离 TopLevel 的控件手工
`Measure/Arrange` 再 `RenderTargetBitmap.Render`,出来的是纯白图。

图上确认:添加 / 测试 / 移除 / 生成 / 允许 / 忽略 / 保存,七个按钮的文字全部居中,描边与配色一致。

### 十二、渲染顺手抓出另一个 bug:`Loc` 表里两个同名键

图上"单轮超时(秒)"旁边那个标签显示成了一整句 **"没人应答,按拒绝处理。"**。

`Loc.Table` 是用索引器 `["key"] = […]` 初始化的,**重复键是静默覆盖**(换成集合初始化器的 `Add`
才会抛)。设置页的「审批超时(秒)」标签与 IM 里那句超时提示都叫 `BridgeApprovalTimeout`,
后写的把前面盖掉。编译、测试、启动全都正常 —— 只有把界面画出来才看得见。
IM 那条改名 `BridgeApprovalTimedOut`。

### 十三、这一轮加的守门用例

- `EveryButtonCentresItsLabel`(headless)—— 遍历页面上每个 `Button`(排掉 `ToggleButton`,
  勾选框的文字本来就该左对齐),断言 `HorizontalContentAlignment == Center`。
  这条在 headless 里**成立**,因为样式表里那个 setter 的值是字面量,不依赖宿主资源。
  验证过:抽掉 `Button.primary` 的那一行,它精确报出 `SaveButton:Save`。
- `EveryButtonAndCheckBoxOnTheCollaborationPageCarriesItsClass`(文本级)—— 挡住"退回去每个按钮
  自己写 `Theme=`"那种改法。
- `NoKeyIsDefinedTwice`(源码级)—— 只能按源码查,运行时的字典里看不出曾经有过两条。

`VelaShell.Plugin.Ai.Tests` 281 条全绿。

**过程教训**:中间还有一次"注入 bug 验证守门用例"的 perl 替换没匹配上,于是"用例仍然通过"被我
当成了证据 —— 注入之后要先确认文件真的变了,再看用例的结果。

### 十四、过程问题:整轮功能是在落后 18 个提交的基线上做的(用户反馈)

这一整轮从头到尾没有 `git fetch` 过。做完才发现落后 `origin/main` **18 个提交**,
而且其中好几个正好动 AI 插件:订阅登录(`Auth/`)、思考档位下拉、供应商目录、
自定义供应商拉模型清单 —— 与本次改动的文件高度重叠。

补救:`git stash --include-untracked` → `git merge --ff-only origin/main` → `git stash pop`。
新增文件都是未跟踪的,不参与合并;四个被同时改过的文件里只有 `plan.md` 真冲突
(两边都往文件尾追加小节),上游占 30/31/32,本次的两节顺延为 33/34。
`DialogStyles.axaml` / `Loc.cs` / `plugin.json` 自动合上 —— 但"文本能合"不等于"语义没坏",
所以在新基线上重跑了一遍:全仓 `dotnet build` 无警告,`VelaShell.Plugin.Ai.Tests`
**466 条全绿**(本次 281 + 上游新增),协作页也重新渲染确认过外观没被上游的
`DialogStyles` 改动(新增了一条 `ListBox.nav ListBoxItem TextBlock.count`)影响。

**教训**:动手之前先 `git fetch` 看一眼落后多少。这次运气好只撞了一个文件;
如果上游重构了 `AiSettingsStore` 或 `AgentToolbox`(这两个都在本次依赖里),
返工量会大得多。

### 十五、飞书里 401,而面板同一刻好好的:桥接吃的是启动时那份 AI 设置快照

现象:群里报
`OpenAI Codex / gpt-5.6-sol: Service request failed. Status: 401 — {"detail":"Could not parse your authentication token."}`,
而 VelaShell 里的 AI 助手用同一个模型完全正常。

(顺带说明上一轮"把模型名带进错误里"那个改动是值得的 —— 没有那句 `OpenAI Codex / gpt-5.6-sol`,
这条线索根本立不起来:光看 401 会以为是飞书的凭证问题。)

面板与桥接走的是**同一个** `AiSettingsStore.CreateClientAsync`,里面刷新令牌、附加头、
账户级 BaseUrl 一应俱全。差别在传进去的那个 `AiProvider` 对象:

- 面板在设置窗口登录之后会刷新自己的 `AiSettings`;
- **桥接的那份是启动时读的,之后再没更新过**(只在设置页保存触发 `ReloadAsync` 时才换)。

于是用户登录订阅制供应商之后,桥接手里那份 provider 还是登录之前的形态(`Auth` 仍是 `ApiKey`、
没有 `OAuth` 配置),`ResolveCredentialAsync` 走了"取 API Key"那条岔路,把一个空 Key 发了出去 ——
服务端回的正是"解析不了你的认证令牌"。

改法:`BridgeAgentRunner` **每轮现读** `AiSettings`,`ConversationRouter` 不再缓存它
(`Apply` 只收桥接设置与语言)。代价是每轮多读一次 JSON,与一次模型调用比可以忽略;
换来的是"面板里改了什么,桥接下一句就跟上" —— 换模型、重新登录、改 MCP 配置都不必重启桥接。

回归用例 `BridgeAgentRunnerTests.RunAsync_RereadsTheAiSettingsEveryTurn`:**先**造 runner、
**之后**才往库里写供应商,第一轮必须说"没配模型",第二轮的报错里必须带上刚写进去的模型名。
快照式实现连编译都过不去(`ai` 是参数),所以这条用例是结构性的。
另有一条钉住"报错必须点名哪个模型"。

`VelaShell.Plugin.Ai.Tests` 469 条全绿。

## ✅ 35. 2026-09-03 插件能按已保存配置自己连一台机器(SDK 2.0.2 的宿主侧落地)

第 33 节 §5 记的那条"已知限制"到期了:SDK 2.0.2 给 `ISessionsApi` 补上了
`ListSavedAsync` / `OpenAsync` / `CloseAsync`,宿主这边三个 `ISessionsApi` 实现
(`SessionsCapability`、`PluginManager.EmptySessionsApi`、PluginHost 的 `RpcSessions`)
一起 `CS0535` —— 这不是要绕过的障碍,是该动手实现的信号。

### 一、闸门全部焊在宿主这一侧

"插件能自己连机器"是一次实打实的权限扩张,所以契约里的每一条约束都在
`SessionsCapability` 里有对应的代码,而不是留给实现自觉:

- **只能开已保存的配置**。参数是配置 id,不是主机名端口 —— 连哪些机器由用户先在
  会话树里定下来。列表还刻意只报 `ConnectionType.SSH` 的那些:SFTP / FTP / 插件协议
  开不出 `SessionInfo` 来,列出去只是发一个注定失败的 id。
- **凭据一个字节不经过插件**。`IPluginSessionOpener` 传的是宿主自己查出来的
  `SessionProfile`,插件那边自始至终只有一个不透明 id。
- **宿主可以拒绝**,且拒绝与失败是两种结局:`PluginPermissionDeniedException`
  (用户说了不,重试没有意义)vs `PluginSessionOpenException`(放行了但没连上,
  换个时间可能就好了)。合成一个异常,插件就只能靠读消息文本去猜。
- **`Reason` 原样进确认框**,空理由直接判成插件的编码错误 —— 一个没有理由的确认框
  只是一个让人盲点的按钮。
- **只关得掉自己开的**。归属账本按插件计;**复用**拿到的那条(用户自己开的标签页)
  不进账本,`CloseAsync` 对它一律拒绝。一个能挂断别人正在用的终端的接口,不该存在。

### 二、为什么走 `TryConnectProfileAsync` 而不是直接 `ConnectAsync`

直接调连接服务也能连上,但连出来的是一条**用户在界面上看不见**的会话:没有标签页,
关不掉,断了也没人知道。用户点"同意"时期待的是屏幕上多出一台机器,而不是后台多了一条
自己无从察觉的 SSH。顺带,凭据弹窗、跳板链、主机指纹确认、连接历史与审计全都在那条路上,
复用它等于这些一件都没漏。代价是这件事只能在 UI 层做,于是 Infrastructure 侧只留一个
`IPluginSessionOpener`(与 `IPluginPermissionPrompt` / `ITerminalResolver` 同一体例),
`HostSessionOpener` 在 `VelaShell` 工程里实现;headless 宿主不挂它,于是开会话一律拒绝 ——
**没人可问不等于可以自己放行**。

### 三、授权闸拆成两本账

`PluginPermissionGate` 原本只管终端回写。开会话另起一本(落库另一个文档,内存另一套):
合成一本就意味着"允许它替我敲一行命令"顺带把"允许它自己连生产机"也批了 ——
这两件事的分量差得远,用户在确认框上点的也不是同一个"是"。管理页的"撤销"仍是一刀切,
两本一起清。确认框复用 `PluginPermissionDialog`(换标题/图标,预览框里放理由),
四选一不变。

### 四、超时按"人要看一眼再点"给

`RpcSessions.OpenAsync` 用 5 分钟而不是普通能力调用的 30 秒。按 30 秒给的话,
用户还没抬头看见确认框,插件那边就已经把请求判死了 —— 而宿主这边的连接照开不误,
于是留下一条谁都不认领的会话。

### 五、验收

`SessionsCapabilityTests`(闸门语义:未知 id / 无 opener 即拒 / 拒绝与失败分型 /
空理由前置拒 / 理由原样透传 / 复用不再问 / 只关得掉自己开的 / 幂等 / 跨插件不许关)、
`SessionRoutingTests`(隔离模式真管道往返:listSaved → open → close,以及**拒绝要以
`PluginPermissionDeniedException` 的身份到达插件那一侧** —— 跨进程只剩一个错误码,
码丢了"用户说了不"就退化成一个笼统的调用失败,插件于是换个姿势再试一次)、
`PluginPermissionGateTests` 新增两本账互不串门与撤销一刀切。
`dotnet build VelaShell.slnx` 无警告;Infrastructure 147 条、Core 416 条、
Presentation 55 条、VelaShell.Tests 963 条通过。

**已知遗留**:`ShortcutCatalogTests.Doc_ListsEveryCatalogEntry` 读的是
`docs/快捷键参考.md`,而文档已在 `f0f492a` 搬去 velashell-docs —— 与本次改动无关的既存失败。

**插件侧**:见第 36 节 —— `AgentToolbox` 的三个工具已经接上,这条限制到此闭合。

## ✅ 36. 2026-09-03 AgentToolbox 接上开会话:机器人不必再回"你先去连一台"

第 35 节把宿主那一层备好了,但工具箱不给模型这几个工具,整条路就还是走不通。
这一节补上 `list_saved_sessions` / `open_session` / `close_session` 三个工具,
第 33 节 §5 那条限制到此真正闭合。

### 一、`open_session` 是工具箱里唯一要过两道人的工具

一道是工具箱自己的审批闸(面板的审批卡 / 群里的 `y`/`n`),一道是宿主的确认框。
看着重复,问的其实不是同一件事:

- 审批闸问的是"**这轮对话**里要不要让 agent 这么干";
- 宿主的确认框问的是"要不要让**这个插件**替我连机器",答案由用户一次性给定
  (可以选"始终允许")。

无人值守那条路正是靠后者才走得通:用户在桌面上批一次"始终允许",此后群里的机器人
自己就能连。少了任何一道,要么每次连机器都得有人在电脑前点一下(不可用),
要么 agent 可以悄悄替人连生产机(不可接受)。

`reason` 空着直接退回给模型重写,不拿占位符糊弄 —— 那句话是原样显示给用户的,
没有理由的确认框只是一个让人盲点的按钮。审批卡上同时显示机器与理由,
和宿主确认框上的那句对得上,不然用户会觉得可疑。

### 二、`close_session` 刻意不走审批

关的对象已经被宿主限死在"本插件开的那些"里,用户自己的标签页一根汗毛都动不了。
而收拾自己开的东西还要再点一次头,只会让 agent 干脆不收拾 —— 尤其在没有审批界面的
MCP 那条路上(`Ask` 等于一律拒绝),那样必然攒下一堆没人认领的标签页。

### 三、默认目标的优先级:选中项 > 自己开的那条

`ResolveAsync` 加了一档兜底:显式 `session_id` > 面板/`use_session` 选中的 > 本轮
`open_session` 开出来的。兜底排在选中项**之后**是关键 ——

- 排在后面,"聊天没绑机器"(桥接)与"还没 `use_session`"(MCP)这两条路自动走通:
  provider 返回 null,自己开的那条就是默认目标,不必逼模型在后续每次调用里都记得带 id;
- 若排到前面,用户在面板上选着 A、模型为了一个子任务开了 B,后面不带 id 的调用就会
  悄悄打到 B 上 —— 用户此刻正看着 A。

`open_session` 的回执因此分两种措辞:没有选中项时说"后续工具默认就用它",
有选中项时说"要打到这台就得显式带 session_id"。关掉之后兜底立即撤销,
每次取用前还核实一遍会话是否还在(用户可能手动关了)。

### 四、顺手改掉三处"你先去连一台"

- 工具箱的"没有会话可用"提示:能开会话时把 `list_saved_sessions` + `open_session`
  这条路指出来。**计划模式与用户取消勾选时不提** —— 提一个没注册的工具,
  模型会去调,然后把这次失败当成自己的问题;
- 桥接的系统提示词:Agent 模式下改成"这台机器保存过的话就自己连,但要说清是哪台、为什么";
  计划模式没有 `open_session`,照旧指路 `/use`;
- MCP 的 `initialize` 说明:只在**绕过审批**时才提这条路。那条路上没有审批界面,
  `Ask` 等于一律拒绝,而 `ReadOnlyAuto` 只放行确定无副作用的命令 —— 开连接不在其列。
  写了却让它每次都撞墙,比不写更糟。

### 五、验收

`AgentToolboxTests` 新增 15 条:已保存列表(含"这条已经连着了,直接用这个 id",
省掉一次没必要的确认框)、空理由前置退回、理由原样送到宿主、审批卡与确认框内容对得上、
拒绝与连不上读起来必须不一样、默认目标的两个方向、`close_session` 免审批但关不掉用户自己的、
关掉后兜底撤销,以及"没会话"提示在计划模式下不提 `open_session`。
`VelaShell.Plugin.Ai.Tests` 488 条全绿,`dotnet build VelaShell.slnx` 无警告。

## ✅ 37. 2026-09-04 每条连接各配一条「认证后执行命令」(用户反馈)

设置里那条「连接后执行命令」是**全局**的:配了就每个终端都跑。用户要的是另一件事 ——
不同机器登进去要做的事本来就不一样(堡垒机 `sudo su -`、开发机 `tmux attach`、
某台跳板要先 `source /etc/profile.d/xxx`),挤进同一个框里只能二选一。
所以这一条落在**连接配置**上,跟着配置走,而不是又加一个全局开关。

### 一、字段落在 `SessionProfile`,五处手写拷贝一处都不能漏

`SessionProfile` 全仓是逐字段手写拷贝(没有 `with`/克隆方法),新增字段必须同步:
`ConnectionProfileViewModel.BuildProfile`、`SonnetDbSessionRepository.Protect`、
`ConnectionWorkflowService`(不记密码时的副本)、`SessionTreeViewModel`(复制会话)。
漏抄的表现是"存的时候好好的,重开软件命令没了",而且不报任何错 ——
`SonnetDbPersistenceTests` 里那条 round-trip 用例就是钉这个的。

延迟(0~60 秒)的钳位放在**模型的 setter** 上而不是只靠界面:配置文件可以手改,
一个 `99999` 会让那条命令看起来永远不执行,而用户完全无从知道自己在等什么。

### 二、只对 SSH 出现,切走时连值一起清

命令是往 shell 通道里注入的;SFTP / FTP / 对象存储根本没有终端。换到这些协议时
该栏收起,`BuildProfile` 存下来的也一并归 null —— 留着它就是一条永远不执行的命令,
而且切回 SSH 时会诈尸执行一次。

### 三、为什么需要"延迟"这一档

PTY 输入由内核缓冲,shell 就绪后才读,本来不必等提示符 —— 全局那条正是这么发的。
但对端登录后还会自己往终端里写东西:motd 脚本、企业登录横幅、把 stdin 一起读掉的
banner。立刻注入会被这些输出盖住甚至吞掉,留一两秒才稳。`0` = 握手完立刻发。

延迟 > 0 时走 `DispatcherTimer.RunOnce` 而不是 `await Task.Delay`:握手方法不能因为
用户配了 5 秒就把整条连接流程(刷新最近连接、绑定 SFTP 面板、状态栏)一起挂起 5 秒。
定时器回调里以 **会话 id + 连接状态** 复核身份 —— 这几秒里标签可能已经断开、被关掉,
或者已经重连成了另一条会话,那时候再灌进去就是灌进了别人的 shell。

### 四、与全局那条的关系:两次注入,先全局后本条

不拼成一串发:那条是全局的、每个终端都跑,这条只跟着一条配置走,而且带自己的延迟。
拼在一起就没法各自延迟,也说不清谁先谁后。顺序固定「先全局、后本条」,
与用户在两个界面上看到的顺序一致。就地重连同样执行 —— 它描述的是
"每次登进这台机器要做什么",断线重连回来一样成立。

### 五、验收

`dotnet build VelaShell.slnx` 无警告;`dotnet test VelaShell.slnx` 全绿(2757 通过)。
新增 9 条:模型的延迟钳位(含反序列化,3 条 DataRow)、仓储 round-trip 且两条配置互不串味、
编辑对话框回显与保存、空白命令存成 null、延迟钳位、切到无终端协议时收起且不落盘,
以及一条走真实连接路径、断言那串字节确实落到 shell 流上的端到端用例。
文档已同步 velashell-docs `zh/host/交互与界面规格.md` §13.1 与 `en/` 镜像。

## ✅ 38. 2026-09-04 FTP / FTPS 可配「默认打开路径」(用户反馈)

与第 37 节同一类诉求:上传目标常年是同一个 `/var/www/html` 或 `/pub/incoming`,
而 FTP 服务器给的登录工作目录往往就是根,每连一次手点四五层是纯粹的重复劳动。
同样放进连接对话框的「高级选项」,只在 `FTP` 页签下出现。

### 一、落在 `FtpSettings` 而不是 `SessionProfile` 的平铺字段上

第 37 节那条命令是协议无关的,所以进了 `SessionProfile`,代价是四处手写拷贝同步。
这一条是 FTP 专属的,进 `FtpSettings` —— 那个类存在的理由正是这个,
配套只需改 `Clone()` 一处。SSH / SFTP 仍走登录家目录,插件协议(S3…)
自己在描述符里声明字段,谁都不必为别人的设置项让路。

### 二、它是候选路径的第一位,不是硬性要求

`FileBrowserViewModel.LoadInitialCoreAsync` 原本就是一张候选表
(登录工作目录 → `/`),依次尝试,谁先打开算谁的。这次只是把配置里的路径插到表头。
于是"路径配错了"(打错字、目录被删、账号被 chroot)自动退回登录目录再退回根目录 ——
与家目录进不去时回退根目录是同一条纪律。**配错一个路径不该把用户堵在报错的空白页上**,
这一点比"严格按配置走"重要。

### 三、归一化放在 setter 上

用户会照 Windows 的习惯敲 `\pub`,也会从别处粘一个带尾斜杠的路径进来,
而 FTP 的 `CWD` 对这些写法并不一律宽容。`FtpSettings.NormalizeRemotePath`
统一做:去首尾空白、反斜杠转正斜杠、补前导 `/`、去尾部 `/`,空串与纯 `/` 归 null
(根目录本来就是默认行为,当作没配)。放在 setter 上,界面、导入器
与手改的配置文件因此共用同一套规则。

### 四、验收

`dotnet build VelaShell.slnx` 无警告;`dotnet test VelaShell.slnx` 全绿(2770 通过)。
新增 13 条:归一化的 9 条 DataRow(连带验 `Clone()` 抄没抄)、FTP 配置的 JSON 往返、
对话框保存时归一化 + 重开回显 + 自动展开高级选项、换成非 FTP 协议时整块不落盘,
以及文件浏览器的两条 —— 配置路径优先于登录工作目录,以及路径打不开时回退且不留错误提示。
文档已同步 velashell-docs `zh/host/交互与界面规格.md` §13.1 与 `en/` 镜像。

## ✅ 39. 2026-09-04 文档型连接的树状态:关掉一个,别把还活着的另一个也熄了(用户反馈)

现象:点太快对同一条 FTP 配置开出两个标签,关掉其中一个,资源管理器里那条的状态圆点就灭了
—— 明明还有一个活着。

### 一、根因与 #321 同形,只是那次只修了终端那一半

树上一条配置只有**一个**节点,而名下可以同时开着好几条会话。#321 已经为终端标签定过纪律:
节点状态是名下**所有**标签的合并结果(`RefreshSessionStatus`,Connected > Connecting > Error >
Disconnected),不是最后一次变更的那个标签说了算。

文档型连接(独立 SFTP / FTP / S3 等插件文件系统 / Redis 等工作台)漏在了外面,一直是
"最后一次事件说了算":`CloseSftpDocumentCoreAsync` 按配置 Id **无条件写**「未连接」,
`CloseWorkspaceDocumentAsync` 同样,`OnFtpSessionStateChanged` / `OnPluginSessionStateChanged`
收到 `Closed` 也直接写「未连接」。四处都不看"这条配置名下还有没有别的活会话"。

### 二、修法:把文档也纳入同一次合并

新开一本 `_documentSessions`(会话 id → 配置 id + 当前状态),开文档时登记、关文档时摘掉;
`RefreshSessionStatus` 从"只枚举 TabBar 里的终端标签"改成"终端标签 ∪ 该配置名下在册的文档会话"。
于是四处关闭路径统一变成「摘掉这一条,然后重算」,谁也不再替别人做主。

**不能拿 `Layout.AllDocuments()` 当这本册子**:`DockWorkspace.CloseDocument` 是先
`RemoveDocument` 再触发 `DocumentClosed`,正在关的那个已经不在集合里了,而迟到的状态事件
仍会引用它 —— 于是"还没关完就已经不算数"和"关完了还在算"两头都不对。单开一本反而没有歧义。

顺带删掉 `_ftpSessionProfiles` / `_pluginSessionProfiles` / `_workspaceProfiles` 三本旧册子:
它们的唯一用途就是这件事,新册子接手后三者只剩写入、无人读取。留着两本能互相打架的账,
正是这类 bug 复发的温床。

`UpdateDocumentSessionStatus` 对**不在册**的会话直接忽略,不复活它:文档关掉之后仍可能收到
一条迟到的状态事件(FTP 的失效是在下一次操作时才暴露的),照单全收会把刚灭掉的圆点重新点亮。

### 三、回归用例差点写成一条假绿

第一版用例在"关掉一个之后断言节点仍为活跃",而状态更新是**异步**调度到主线程的 ——
断言跑在更新之前,于是把修复整个撤掉它照样通过。这种用例比没有更糟。

改成钉在两个确定的时点上:先 `await` 该文档的关闭任务(状态更新是在它的收尾里发起的,
为此加了一个 `GetStandaloneSftpCloseTask` 测试探针,手法同 `SshTerminalBridge.DrainWritesAsync`),
再往主线程调度器上压一道栅栏把排队的刷新冲掉。改完之后**验证过它会失败**:
把 `ForgetDocumentSession` 换回"直接写未连接"的老语义,用例立刻红。

### 四、验收

`dotnet build VelaShell.slnx` 无警告;`dotnet test VelaShell.slnx` 全绿(2771 通过)。
新增 1 条端到端用例(走环回 FTP 服务器,对同一条配置开两个文档、关一个、再关一个),
并已反向验证它对老语义确实报错。

## ✅ 40. 2026-09-04 数字输入框删空后别再甩一句转换异常(用户反馈)

现象:连接配置 → 高级选项 → 「认证后执行命令」右边那个「延迟(秒)」,把里面的数字删掉,
框旁边立刻冒出一整段红字 **`System.InvalidCastException: Could not convert '(null)' (null)
to System.Int32.`**,同时把同排的命令输入框挤成一条缝。

### 一、根因:空框直接撞在绑定的类型转换上

`NumericUpDown.Value` 是 `decimal?`,而目标属性(设置项、连接配置)是 `int` / `double`。
用户按退格删空的那一刻控件把 Value 置成 `null`,绑定引擎转换不了,把**异常对象本身**当作
校验错误交给 `DataValidationErrors` —— 界面于是原样显示异常的 `ToString()`。
全项目 20 处 `NumericUpDown` 无一例外(端口、超时、保活、日志留存、行高、内边距、回滚行数、
并发数、限速……),因为这是控件与绑定的默认组合行为,不是哪一处写错了。

布局被挤的那一半是同一件事的副作用:数字框统统长在 `"*,Auto"` 两栏的 Auto 一侧,
Fluent 默认把整段错误文字排进布局,一段几十字的异常把 Auto 列撑开,`*` 那一栏就被压没了。

### 二、修法:文案、状态、呈现三件事分开

**文案** —— `Behaviors/NumericInputGuard.cs` 用 `DataValidationErrors.ErrorConverter` 把任何
错误换成一句人话:「请输入 0 到 60 之间的数字」。区间直接读控件自己的 `Minimum` / `Maximum`
(延迟是 0–60、端口 1–65535、行高 0.8–2.0),按控件的 `FormatString` 渲染 —— 一位小数的
行高框才不会被写成「0.8 到 2」让人以为上界是整数。两端没有界时退回「请输入数字」。
五份 resx 各加 `Validation_NumberRange` / `Validation_NumberRequired` 两条。

**状态** —— 同一个行为在 `LostFocus` 上兜底:值还是空的就恢复成上一个有效值。清空只是编辑
过程中的中间态,人走开之后不该留一个红着的空框 —— 那时视图模型里其实一直是旧值,空框在说谎。
恢复走 `SetCurrentValue` 而不是直接赋值:直接赋值写的是本地值,优先级高过绑定,会把这个框与
视图模型的双向绑定就地掐断,此后改设置再也传不回去(已有回归用例钉住)。

**呈现** —— `Themes/DockStyles.axaml` 把 `DataValidationErrors.ErrorTemplate` 换成 14px 的红色
警告图标 + 悬停提示(文案经 `Converters/ValidationErrorTextConverter` 拼成一段)。占位恒定,
布局纹丝不动,文案一个字没少。

兜底挂在全局那条 `Style Selector="NumericUpDown"` 上,新加的数字框自动带上,不需要谁记得
在每个 axaml 里补一句 —— 有一条用例专门守这个。

### 三、顺手改掉两个用 TextBox 装端口的地方

隧道面板的「本地端口」「端口」绑的是 `int NewLocalPort` / `NewRemotePort`,却用的是 `TextBox`,
删空或输入字母同样会把 `Could not convert '' (System.String) to System.Int32` 摆进表单。
改成 `NumericUpDown`(1–65535,不显示微调钮),外观照抄本视图那条 TextBox 样式,与旁边的
主机框逐像素一致(已用 headless 截图核对)。全项目再无绑到数值属性的可编辑 `TextBox`。

顺带一提,输入字母这条路其实**到不了绑定**:控件解析不了的文本自己就拦下了,`Value` 不动,
失焦时文本回滚。真正会漏到绑定上的只有"清空"。有一条用例把这个前提钉住,免得日后有人以为
提示丢了。

### 四、设置窗口整卷扫一遍

设置窗口是数字框最密的地方,所以不靠"我改的是全局样式,应该都覆盖到了"这句话交差,
而是把它整个架起来逐页逐框走一遍(`Every_number_box_in_the_settings_window_is_covered`):
翻遍 12 个分页、把跟在开关后面的字段(自动重连的间隔与重试、限速上下行、日志留存与目录、
代理那一整组)全拨出来,**17 个数字框**逐个清空 → 核对提示是区间不是异常 → 挪走焦点 →
核对值回到原样、红标消失。17 = 常规 7 + 外观 1 + 终端 4 + 传输 4 + 代理 1,与静态清点一致。

同一扇窗里剩下的 **22 个纯文本框**另有一条用例:逐个清空,断言不许冒出异常原文 ——
真冒出来就说明又有人把数值属性绑到了 `TextBox` 上(隧道面板那两个端口原本就是这么写的)。
两条都带扫描数量下限,免得哪天页面结构一变,扫不到控件却一路绿。

### 五、验收

`dotnet build VelaShell.slnx` 无警告;`dotnet test VelaShell.slnx` 全绿(2781 通过)。
新增 10 条 headless UI 用例(`NumericInputGuardUiTests`):全局样式是否挂上、提示文案是否是
区间而不是异常、失焦是否恢复、恢复后绑定是否还活着、提示是不是定宽图标且文案在悬停里、
字母输入是否根本到不了绑定、无界与小数框的文案,以及上面那两条设置窗口整卷扫描。

## ✅ 41. 2026-09-05 对外 MCP 的「允许操作的服务器」改成勾选,与连接列表同一套(用户反馈)

用户反馈:连接列表早就改成显示名称了,协作接入页上那个 MCP 的「允许操作的服务器」还留着一个
要手打 `user@host:port` 的多行文本框,两处对不上。

### 一、为什么不是"按名称匹配",而是复用 IM 授权那套勾选

- **活会话身上没有名字**。SDK 的 `SessionInfo` 只有主机、端口、用户名;名字只挂在
  `SavedSessionInfo` 上。任何"按名称"判定最终都得先把活会话映射回一条已保存配置,再比主机、
  端口、用户名 —— 而这段逻辑已经存在,就是 `SavedSessionScope.Matches`,还有用例守着;
- **名字会改、会重名,已保存配置的 id 不会**。所以界面上勾的是名字,存下来的是
  `SavedSessionId` 与分组名 —— 与第 34 节那份「已授权的聊天」逐字同一套;
- 手打的老毛病:打错一个字的后果是外部 agent 一台机器也碰不到,而界面上看不出哪里错了。

`McpServerSettings.AllowedTargets`(多行字符串)→ `McpServerSettings.Scope`(`SessionScope`),
`McpToolHost` 改用 `settings.ResolveScope(context)`;`use_session` 里那段单独的字符串比对删掉 ——
范围外的会话在 `SessionTargets.ResolveAsync` 里就当作不存在,而"它不在名单上,名单是 A、B、C"
这种回答本身就是个探测接口。设置页那个多行框换成「不限范围 / 只有下面勾选的」下拉 + 与授权行
共用的 `BuildScopeChecklist`。

### 二、默认值的方向:两边刻意相反

MCP 默认 **`ScopeKind.All` = 允许全部**,与 IM 授权「空 = 一个都不放行」正好相反,理由没变:
这条路的边界是回环地址 + 令牌 + 只读挡位,顺手把用户自己机器上的 Claude Code / Codex 一起收紧,
挡不住任何攻击者,只挡得住用户自己。

### 三、迁移:唯一会改到别人行为的一处

`NormalizeScope` 在读写两头各跑一次(与 `NormalizeGrants` 同一套路数):

- 旧清单**空着** → 不限范围,与升级前逐字相同;
- 旧清单**非空** → 逐行去会话树里找对得上的配置,勾出它们;
- 旧清单非空却**一行都对不上** → 受限且一台都没勾,也就是一台都不给,**不是**回到"允许全部"。
  这种配置本来就只放行了几台不在会话树里的机器,把它读成"全都行"是凭空多给;界面上那句
  「一个都没勾:外部 agent 碰不到任何机器」会直接说出这件事。

`AllowedTargets` 留着,但降级成 `Scope` 的**派生镜像**,由 `NormalizeScope` 重算 ——
只为"用户万一换回旧版本,清单还在"这一件事。

**代价说清楚**:在终端里手敲 `ssh` 连出去、从没存进会话树的临时会话,受限模式下从此碰不到 ——
与 IM 授权那边是同一条失败关闭的规矩。不限范围时不受影响。

取会话树失败时 `NormalizeScope` 不跑,`ResolveScope` 退回旧的 `TargetListScope`,也就是升级前的
行为:既不多给也不少给。退回成"不限范围"是不行的 —— 一次读取失败不该把用户配好的名单悄悄拆掉。

### 四、验收

`VelaShell.Plugin.Ai.Tests` 全绿。新增 9 条:折算(勾出对得上的那几台)、空清单落在不限范围、
一行都对不上时失败关闭、迁移过就不再被旧清单改写、镜像重算(含不限范围写成空)、
迁移没跑过时退回旧清单、什么都没配过时压根没有闸,以及两条 headless UI —— 勾名字存 id、
默认那一项是不限范围且勾选框收着。

✅ **文档已同步**(2026-09-07):velashell-docs 的对外 MCP 一节新增
「2.4 『允许操作的服务器』怎么配」—— 下拉 + 勾选清单、三条不用手打的理由、
与 IM 授权刻意相反的默认值,以及 `NormalizeScope` 的三种迁移结果。
中英两棵树(`zh/plugins/协作接入.md` / `en/plugins/collaboration.md`)均已落地。

## ✅ 42. 2026-09-06 开一下 SFTP 面板,别把设置里的开关也给拨了(#377)

用户反馈:设置 → 终端里关掉了「连接后自动打开文件浏览器」,可只要点一下标题栏右上角的
「SFTP 文件浏览器」按钮,回头再看设置,那个开关自己变成开启了。

### 一、根因:把「这次想看一眼」当成了「以后都这样」

`MainWindowViewModel.RememberFileBrowserStateForTab` 在把面板的显示/隐藏记到标签上之后,
还顺手调了一次 `PersistAutoOpenFileBrowser`,把这一下开/关异步写回
`AppSettings.TerminalBehavior.AutoOpenFileBrowser`。于是开面板 = 打开设置项,关面板 = 关闭
设置项 —— 用户在设置页做的决定,被终端里一次随手操作覆盖掉了,而且没有任何提示。

这与这一项自己的说明是矛盾的。设置页上那行描述从来都写着「决定**新连接**的远程会话是否自动
打开;此后每个标签页各自记忆自己的显示/隐藏状态,互不影响」(五份 resx 一致),
`ToggleFileBrowser` 也一直是按标签记忆设计的。写回是后加的一条捷径,把「会话级的临时状态」
和「全局默认值」这两层混成了一层。

### 二、修法:删掉写回,两层各归各位

- `RememberFileBrowserStateForTab` 只写 `TerminalTabViewModel.FileBrowserOpen`(标签生命周期
  内的记忆,切回来照它恢复),不再碰设置。
- `PersistAutoOpenFileBrowser` / `PersistAutoOpenFileBrowserAsync` 连同那条序列化保存队列
  (`_fileBrowserPreferenceSaveSync` / `_fileBrowserPreferenceSaveTail`)一并移除 —— 没有了写回,
  也就没有了要串起来防乱序的东西。
- `AppSettings.AutoOpenFileBrowser` 的注释改回实情:本项只由用户在设置页改动。

行为分层现在是三段,与文案对得上:设置项 = 新标签首次连接时的初值;标签 = 该标签此后的开/关;
面板实例 = 当下这一屏。上面一层不被下面一层反向污染。

### 三、验收

`dotnet test VelaShell.slnx` 全绿。原来那三条钉「写回」的用例改成钉「不写回」:
关面板后设置不变且新标签仍自动打开、开面板后设置不变且新标签仍不自动打开(两条都额外断言
`SaveCount == 0`,顺手拦住别的路径偷偷存盘);连开带关的竞态用例随写回一起删掉,
测试替身 `MemorySettingsService` 的保存延时开关也没人用了,一并去掉。

## ✅ 43. 2026-09-06 滚动条:悬停别等半秒,新建连接别一进来就是粗条(#378)

两件事,同一根滚动条。

用户反馈其一(#378):鼠标压在滚动条上,它要等一会儿才变粗 —— Windows Terminal 是立刻的。
其二:新建连接(Redis 这类字段多的协议)一打开,右侧就顶着一条完全展开的粗滚动条,
不是别处那种收着的细线。

### 一、根因

**等一会儿**:Avalonia 的 `ScrollBar.OnPointerEntered` 并不直接展开,而是
`ExpandAfterDelay()` —— 挂一个 `DispatcherTimer`,延迟取 `ShowDelay`,默认 **0.5 秒**。
本主题的未激活态是一根贴边 2px 细线,于是用户的体感就是"指针都放上去了,滚动条不出来"。

**一进来就是粗的**:`ConnectionProfileView` 的表单区写了 `ScrollViewer.AllowAutoHide="False"`,
本意是"表单被窗口高度上限截断时,别让人以为字段没了"。但这个开关在 Avalonia 里的含义不是
"可见",而是**常驻展开**:`ScrollBar.UpdateIsExpandedState` 里 `if (!AllowAutoHide) IsExpanded = true`。
于是滑道、两端箭头、6px 居中滑块全程挂着,整个应用只有这一处长这样。

### 二、修法

- `Themes/ScrollBarThemes.axaml` 的 `{x:Type ScrollBar}` 加一条 `ShowDelay = 0:0:0.15`,
  全应用的横/纵滚动条(含终端回滚条那条独立 `ScrollBar`)一次生效。
  没有一路置 0:指针从正文横穿滑道那一下只有几十毫秒,零延迟会被这种擦碰一路误触,
  滚动条跟着鼠标一条条撑开。150ms 卡在两者之间 —— 一次有意的悬停等不出焦躁,擦碰则还没
  到点就已经离开。收起那侧的 `HideDelay` 保持默认 2s:移开就立刻缩回,同样会闪。
- `ConnectionProfileView` 去掉 `AllowAutoHide="False"`。原来的顾虑不成立:本主题的未激活态
  是一根**可见的** 2px 细条(不是 Fluent 那种整条隐形),截断照样看得见;悬停也不再等半秒,
  affordance 一点没少。

### 三、验收

`dotnet test VelaShell.slnx` 全绿(跳过的仍是那些按环境早退的集成用例)。
新增两条用例:`ScrollBarStyleTests.PointerEnteringTheBar_ExpandsItAfterAShortDelay_NotHalfASecond`
钉住 ShowDelay=150ms 且 HideDelay 仍是 2s;`ConnectionProfileViewUiTests.FormScrollBar_StartsCollapsed_EvenWhenTheFormIsTruncated`
在一块 320px 高的矮屏上确认表单确实需要滚动、而滚动条仍是收起的 —— 把
`AllowAutoHide="False"` 贴回去,这条会红。`DESIGN.md` 5.8 补了两条:展开时机,以及
"`AllowAutoHide=false` 等于常驻展开,不要拿它当'让人看见能滚'的手段"。

## ✅ 44. 2026-09-06 exit 之后不该被自动连回来;SFTP 通道跟着 SSH 一起收(#383)

用户反馈:建立连接后用 `exit` 正常退出,自动重连立刻把它连了回来 —— 换句话说,
**开着自动重连就退不掉**。

### 一、根因:「用户不想连了」只认了一个入口

`ReconnectPolicy.ShouldReconnect` 的四个条件里,代表用户意图的只有
`userRequestedDisconnect`,而它**只**由工具栏「断开」按钮置位。在远端敲 `exit`
说的是同一句话,却走的是另一条路:远端 shell 退出 → 通道关闭 → 桥 `Closed`
→ `MarkDisconnected`,全程没人把这层意思记下来,于是判定里它和掉线毫无区别。

更下面一层还丢了一次信息。SSH 协议本来把这两件事分得很清楚:

- 远端 shell 退出 → 对端发 `SSH_MSG_CHANNEL_CLOSE` → 读端拿到一次**干净的 EOF**;
- 连接中断(RST / 超时 / sshd 没了)→ 通道读**抛异常**(走 `ReadAsStream` 时是包着
  `Ssh*ClosedException` 的 `IOException`)。

但 `ShellStreamWrapper.ReadAsync` 把两者一律归一成"返回 0"(这个归一本身是对的 ——
掉线不是崩溃,抛出去只会让读循环带着异常收尾),原因就此蒸发。桥只看得到"读到 0",
宿主只看得到"断了"。

### 二、修法:结论仍是 EOF,原因单独留一份

- 新增 `ShellCloseReason`(`Unknown` / `RemoteExited` / `ConnectionLost` / `LocalTeardown`),
  `IShellStreamWrapper.CloseReason` 交代读端为什么走到头。`ShellStreamWrapper` 按上面那条
  协议事实填写:没抛而读到 0 = `RemoteExited`,抛了 = `ConnectionLost`,我们自己在拆 =
  `LocalTeardown`。`ConPtyShellStream` 只有一种收场(`RemoteExited`);
  `PluginTerminalShellStream` 报 `Unknown` —— 插件的传输层什么都可能抛,宿主分不出也不该猜,
  按连接中断处理即与改动前完全一致。
- `SshTerminalBridge.Closed` 由 `Action` 改成 `Action<ShellCloseReason>`,只负责转述,不做判断。
- `TerminalTabViewModel.RemoteShellExited` 记住这次是不是远端自己退的,在 `AttachTransport`
  里与 `UserRequestedDisconnect` 一起复位(否则一次 exit 会永久禁掉该标签的自动重连)。
- `ReconnectPolicy.ShouldReconnect` 加第五个条件 `remoteShellExited`,**不给默认值** ——
  这个类存在的理由就是"两处判定必须一致",能被忘掉的参数等于没加。三处消费点一并补齐:
  掉线后的排程、倒计时到点时的复查、睡眠唤醒后的批量重连。

### 三、顺带:SFTP 的生命周期挂到 SSH 上,而不是挂在调用方的记性上

SFTP **复用主 SSH 连接**(DI 注册处 `inner.OpenSftpClientAsync()`,刻意不在主连接之外另开
一条:那样的连接无人持有、无人释放,还绕过用户可见的连接生命周期)。既然是同一条连接上的
通道,SSH 一断,缓存里的 SFTP 客户端就只是个还占着句柄的死物。

原先靠 `MainWindowViewModel` 在断开/关标签时记得调一次 `CloseSftpForTab` —— 主路径是对的,
但断开的入口不止一个(标签断开、关标签、隧道面板直接 `DisconnectAsync`、退出应用),
靠约定维持不变量迟早漏一个。改为 `SftpService` 订阅
`ISshConnectionService.SessionDisconnected`,在服务层收口;`DisposeAsync` 里退订。
VM 里那次调用保留 —— 它还顺手驱逐文件面板(`EvictFileBrowser`),两者职责不同,
且 `CloseSessionAsync` 本就是幂等的。

### 四、验收

`dotnet test VelaShell.slnx` 全绿。新增用例:

- `ReconnectPolicyTests.ARemoteShellThatExitedIsNotDraggedBack` 钉住新条件;
- `TerminalTabViewModelTests` 三条走**真实链路**(替身流 → 桥 → 标签):exit 被记住、
  掉线不被误认成 exit、重连挂上新传输后标志复位。把 `OnBridgeClosed` 里那行改成
  `RemoteShellExited = false`,第一条与第三条会红;
- `TerminalBridgeTests` 两条钉住桥如实转述原因、抛异常那条路一律报 `ConnectionLost`;
- `SftpServiceTests.ASshDisconnect_TakesTheSftpChannelWithIt` 钉住服务层收口。

文档同步:velashell-docs `zh/host/交互与界面规格.md` 与 `en/host/interaction-and-ui-specs.md`
的「断开连接状态」补上自动重连的适用边界(只救没人要它结束的断开,四种例外)。

## ✅ 45. 2026-09-07 新开标签页时,上一个会话的 SFTP 面板要立刻收起(#385)

用户反馈:设置里的「连接后自动打开文件浏览器」是关闭的,手动在当前标签开了 SFTP 文件管理器,
再开一个新标签页时文件浏览器还挂在下面,要等新连接握手成功才消失。

### 一、根因:「还没有会话」这一态被当成了「什么都别动」

`MainWindowViewModel.RebindFileBrowser` 对没有 SFTP 会话的标签有两条分支,处理方式却不一样:

- 本地终端(ConPTY)与插件终端协议(Telnet…):**换成隐藏的空占位**,面板立刻收起;
- `tab.SessionId == Guid.Empty`:**直接 return**,什么都不换。

而新开的 SSH 标签正好落在第二条上 —— 会话 id 要到握手完成才由 `RunHandshakeAsync` 赋值
(`CreateConnectingTab` 建标签时它还是 `Guid.Empty`),`Layout.AddDocument` 的激活先到,
`SetActiveFromDocument` → `RebindFileBrowser` 撞上那句 return,于是 `FileBrowser` 仍然是上一个
会话那个可见的实例。等握手完成再走一次 `ShowFileBrowserForActiveSession`,这才换成新标签自己的
面板(设置关着 → 隐藏),表现就是「连上之后才消失」。

那句 return 原本大概是想说「会话还没就位,等等再绑」——「等等再绑」是对的,但期间不该继续
展示别人的面板。上一版加本地终端分支时留下的注释其实已经点出这一点(「不能靠下面那句
SessionId == Guid.Empty 兜底 —— 它是 return 而不是换占位」),只是没有把它一起改掉。

### 二、修法:三种「没有 SFTP 会话」的标签合成一条,一律换占位

三条并成一个判断,进去就把 `FileBrowser` 换成隐藏的空占位。上一个面板不 `Detach`
(仍按其会话留在 `_fileBrowserCache` 里),它的开关状态也仍记在缓存实例与所属标签上,
切回那个标签时照旧恢复展示 —— 收起的只是「当前这一屏」,不是那个标签的记忆。

### 三、验收

`dotnet test VelaShell.slnx` 全绿。新增
`MainWindowViewModelTests.FileBrowser_ConnectingTab_HidesPreviousSessionPanelImmediately`:
设置关着 → 手动开面板 → 开一个 `Connecting` 且 `SessionId` 为空的新标签 → 断言面板立刻隐藏
且不指向任何会话,再切回原标签断言面板恢复。把那条 return 加回去,这条用例会红。

## ✅ 46. 2026-09-07 连接慢的时候,屏幕上必须有东西在动(#385 反馈)

用户反馈:新建连接、打开插件的标签页,链路一慢就「点了没有任何反应」,右下角那个后台
状态圆环也不转。查下来现状比反馈还要糙一档:

| 路径 | 点击后立刻看到什么 |
| --- | --- |
| SSH 终端标签 | 标签**出现**了,页签上有黄色圆点;但**正文是一片空白终端** —— `ShowDisconnectedOverlay` 只覆盖 `Disconnected`/`Error`,`Connecting` 这一态从来没有覆盖层 |
| 独立 SFTP / FTP / 插件协议(S3…)/ 插件工作台(Redis…) | **什么都没有**。四处的 `Layout.AddDocument` 全写在 `OpenSessionAsync` / `OpenAsync` **返回之后**,连接期间没有标签、没有圆点 |
| 资源管理器树上的圆点 | 文档型连接的 `TrackDocumentSession(..., Connected)` 也只在成功后调用,连接期间不会变黄 |
| 右下角圆环 | 机制齐全(`IBackgroundActivityService` + `CircularProgressRing` + 悬停清单),但**只有插件装载与配置同步登记**,连接路径一条都没有 |

也就是说文档型连接是字面意义上的"点了没反应",SSH 只是"有个空壳"。

### 一、标签先建,再去握手

新增 `ConnectingDocument`(+ `ConnectingDocumentView` / `ConnectingDockTabItem`):点击那一刻
就进工作区,连上之后由新增的 `DockWorkspace.ReplaceDocument` **原位**换成真文档。

原位换而不是"先 Remove 再 Add":后者永远追加到主组末尾,用户眼看着标签从原地跳到最右边;
而且旧文档若不是当前激活的(等连接时切去了别的标签),Add 还会把焦点抢回来。事件按
"旧的走了、新的来了"如实播报 —— 视图层的内容控件缓存正是靠 `DocumentRemoved` 丢弃旧视图的。

四条路径的连接流程(缺凭据先弹框、认证失败原地重试三次、证书未信任提示后重连)本来一模一样,
于是把界面表示收进一个 `DocumentConnectUi`:占位标签 + 右下角那条活动 + 取消绳。同一套反馈
在四处各写一遍,漏一处就是一条新的"点了没反应"。

- **取消**:占位标签的「取消」、标签 ×、Ctrl+W、右键关闭全部收敛到 `Layout.DocumentClosed`
  一个点上 —— 关掉一个还在连的占位标签 = 不连了,取消一路传进插件的握手。
- **失败**:占位标签留在原地换成失败卡片(原因 + 重新连接 + 关闭标签页),**不再弹模态框**。
  终端标签早就把连接失败从全局对话框改成了标签页内的覆盖层(设计 yxjmg);文档型标签
  以前弹框是因为"连不上就没有标签,失败没有地方可画",而现在有了。
- 顺带修掉一处:收尾用的 `DisconnectAsync` 原先带着调用方的取消令牌,而那正是它已经取消的
  时刻 —— 清理动作自己取消掉,留下一条没人关的 SSH 连接。抽成 `DisconnectQuietlyAsync`。

### 二、终端标签补上「连接中」覆盖层

`ShowConnectingOverlay` + 与断开覆盖层同一张卡片(转圈 + 「正在连接 X」+ 关闭标签页)。
三种非正常态现在各有各的画面,且互不重叠。

### 三、右下角圆环接上连接

`MainWindowViewModel` 原先只把 `IBackgroundActivityService` 转接给状态栏,自己一条都不登记。
留一个字段,在 SSH 握手(`RunHandshakeAsync`)、SSH 重连、插件终端(`AttachPluginTerminalAsync`,
打开与重连共用)、以及四条文档型路径上各登记一条。

**等用户输入的时候不登记**(`EndAttempt`):凭据框与证书框是在等人,不是在等网络,圆环
继续转就是在撒谎。

插件面板(`PluginUiApi.ShowPanelAsync`)也登记一条,覆盖"排到 UI 线程 + 构造控件"这段。
边界说清楚:插件的惰性激活由 `PluginManager` 自己登记(`Msg_PluginLoading`),面板打开后
插件再去拉自己的数据,那段宿主看不见也管不着 —— 不给它编一个转圈。

### 四、验收

`dotnet test VelaShell.slnx` 全绿(3190 通过)。新增用例:

- `ConnectingDocumentTests` 五条:连接**期间**占位标签就在场且类型名已换成协议展示名、
  连上之后真文档接手原来那一格且激活状态一并交接、关掉占位标签会把取消传进插件的握手
  且不留空壳、这段时间后台活动账本里真有一条、`ReplaceDocument` 保位保焦点并如实播报两条事件;
- `ConnectingDocumentViewUiTests` 三条**渲染**用例:编译期的 AXAML 校验拦不住
  `{StaticResource Icon.*}` 写错或令牌改名,而这张界面恰好只在"连接慢"时才会被看到 ——
  最容易带着一个加载不出来的加载界面发版;
- `TerminalTabViewModelTests.ConnectingOverlay_CoversTheGapBetweenTabCreationAndHandshake`;
- `WorkspaceConnectionFailureTests` 两条改成钉新契约:失败落在它自己那个标签上,而不是一扇模态框。

五份 resx 补 `Msg_ConnectingToTitle` / `Msg_ConnectingDetail` / `Msg_PluginOpeningPanel`。
`DESIGN.md` 补 §5.2 的 ProgressRing 与 §5.2b「等待态」两节(唯一的加载指示、不编假进度、
标签先于会话、失败落在自己的标签里、等人的时候不转圈)。

## ✅ 47. 2026-09-07 后台任务浮层是块黑砖,跟哪套主题都不搭(用户反馈)

用户反馈:状态栏右下角那个后台任务浮层"纯黑色背景有点太丑,和整体主题不太搭",并且要能
跟着其他主题走。

### 一、根因:那层外壳从来就没进过令牌体系

浮层的外观有两种画法,本项目两种都在用:

- **内容自带 Border**(侧栏会话树、快捷命令目标选择器):浮层挂 `FlyoutPresenterClasses="bare"`
  把 presenter 剥成纯定位容器,底色/描边/圆角全由内容那层 Border 按令牌画;
- **内容是裸的**(状态栏后台任务浮层):外观就是 presenter 这一层。

第二种一直没人给它设过值,于是吃的是 Fluent 默认的 `FlyoutPresenterBackground` —— 一个**写死的
近黑色**,不在 `Vela*` 令牌里。`ThemeTokenApplier` 换主题时把令牌整片换掉,唯独换不到它:
九套主题换来换去这块砖岿然不动,亮色主题下尤其突兀。

`DockStyles.axaml` 里那段注释其实早就点破了这件事(「状态栏后台任务浮层的内容是裸 StackPanel,
靠的正是 presenter 这层底」),只是当时的结论是"所以别全局剥掉它",而不是"所以得给它上令牌"。

### 二、修法:给 `FlyoutPresenter` 一条走令牌的基础样式

与 `MenuFlyoutPresenter` 同一套值(`VelaBgSurface` / `VelaBorderSecondary` / 1px / 圆角 6),
浮层与右键菜单看起来才是同一个产品;顺带清掉 Fluent 的 `MinWidth` 下限(窄浮层会被平白撑宽)。
`.bare` 保持原样,但**必须排在它之后** —— 同优先级的样式按声明顺序后者胜出,写反了那两个
自带 Border 的浮层就会变回"两层边"。

这条是给**所有**裸内容浮层的,不只是眼下这一个:下次再加一个浮层,不写任何外观也已经是对的。

### 三、顺带把清单本身理一理

截图里标题与第一条活动糊成一块,而且一行字看不出它是在动还是卡住了:

- 标题降到 10px + 字距 + SemiBold,与侧栏那些分组标题同一档,底下加一条发丝线;
- **每条活动自己带一圈 12px 的进度环**:这张清单回答的正是"还有什么在跑"。有确定进度就画
  那个进度(`BackgroundActivityItem` 补一个 0~1 的 `Fraction`,原先只有百分比**文本**,
  环没法用),不可知就走不确定动画。

### 四、验收

`dotnet test VelaShell.slnx` 全绿(3192 通过)。新增 `FlyoutPresenterStyleTests` 两条:
普通浮层的底色/描边/圆角/MinWidth 取自令牌、`bare` 浮层仍被剥干净(后者钉的是两条样式的
先后)。把基础样式的选择器改瞎,第一条会红。

`DESIGN.md` §5.2 补 Flyout 外壳的两种画法与选择依据。

### 五、追加:浮层还压着状态栏(同一轮反馈)

`Placement="Top"` 是贴着锚点按钮的上沿摆的,而那颗按钮几乎占满 24px 的状态栏(20px 高、
上下各留 2px),于是浮层下沿正好压在状态栏上,两块面挨在一起分不出层次。加
`VerticalOffset="-8"` 抬起来,浮层与状态栏之间留出约 6px 的空。

方向不是猜的:写了个一次性探针在 headless 里真开一次浮层,量出 `0 → -8` 时弹出层在窗口内的
Y 从 158 变成 150(偏移走屏幕坐标,正 Y 朝下,故负值向上),确认之后把探针删掉。
这类纯观感的数值不写用例钉 —— 该由眼睛拍板(同 DialogButtonStyleTests 的立场)。

## ✅ 48. 2026-09-07 关掉「连接中」的标签,连接就该停下(用户反馈)

用户反馈:关闭标签页时后台仍在继续连接,右下角「后台任务」里那条「连接中 Debian13(测试服务器)」
赖着不走,过一会儿还要弹一句连不上的异常提示。

### 一、根因:终端标签这边一直没有那根"取消绳"

#385 之后,连接的四种文档型入口(独立 SFTP、FTP、插件协议、插件工作台)共用
`DocumentConnectUi`,它握着一个链接到调用方令牌的 `CancellationTokenSource`,
`ConnectingDocument` 一被关掉(六个关闭入口都汇到 `DockWorkspace.DocumentClosed`)就拉断它。

终端标签走的是另一条路:`CreateConnectingTab` 先建标签、`RunHandshakeAsync` 再握手,
中间只传了**调用方的**令牌 —— 而调用方(点一下会话树)早就返回了,那个令牌永远不会被取消。
于是关标签只做了三件事:移走文档、拆传输、Dispose 标签;握手照旧在后台跑到底:

- `RunHandshakeAsync` 里那条 `using IBackgroundActivityScope` 要等握手结束才释放,
  右下角的圆环就一直转着一条属于已关闭标签的「连接中」;
- 几十秒后 TCP 超时,`TryConnectProfileAsync` 的 `catch (Exception ex)` 照常
  `Toasts.Error(...)` —— 为一个用户十几秒前就亲手关掉的标签报一句"无法连接";
- 更隐蔽的一种:握手恰好在关标签之后成功。会话建起来了,标签却没了,那条连接
  (连同它的端口与隧道)再没有任何界面能关掉它。

### 二、修法:标签也挂一根绳,挂在同一个点上

`MainWindowViewModel` 加一张 `_tabConnectCancellations`(标签 → 取消源)与三个方法:

- `BeginTabConnect(tab, outer)` — 每次尝试(首连、认证重试、重连)登记一次,返回
  「调用方令牌 ∪ 关掉这个标签」的令牌;上一轮的源先收掉,免得关标签拉的是根断绳;
- `CancelTabConnect(tab)` — 只取消不释放。握手还在飞的时候释放取消源,底层库再往
  这个令牌上挂回调就会撞 `ObjectDisposedException` —— 那正是"取消"被翻译成一句
  莫名其妙的连接错误的来路;
- `EndTabConnect(tab)` — 流程结束(连上/失败/被取消)时在 `finally` 里注销并释放。

取消的触发点与文档型连接**同一个**:`OnDocumentClosed`(六个关闭入口都经过它),
`RemoveTerminalTab`(连接失败/取消时的静默移除)再幂等地补一次。

三条握手路径都改成用这根绳:`TryConnectProfileAsync` + `RunHandshakeAsync`(SSH 首连)、
`OpenPluginTerminalForProfileAsync`(Telnet 等插件终端)、`ReconnectTabAsync`(手动与自动重连)。

两处配套:

- **失败分支按令牌判定,而不是按异常类型**:`catch (Exception) when (token.IsCancellationRequested)`
  取代原先的 `catch (OperationCanceledException)`。取消在底层未必现成一个
  `OperationCanceledException`(连接被拆掉时也可能是一句 IO 错误),按异常类型判会漏到
  下面那条弹 Toast 的分支去 —— 用户看到的正是那句多余的"无法连接"。
- **握手赢了竞速也要收拾**:`RunHandshakeAsync` 里握手成功之后的部分拆成
  `CompleteHandshakeAsync`,外面包一层 `catch when (取消)` → `TeardownSshSession(session.SessionId)`。
  重连路径同理(`established` 变量记住已建起的会话)。
- 还有一段令牌管不到:弹凭据框的时候等的是用户不是网络,标签上还没有绳。那一段改成
  在框返回后按「标签还在不在」(`FindDocument`)判一次 —— 关掉标签和在框上点取消是同一句话。

### 三、验收

`dotnet test tests/VelaShell.Tests` 通过 1242 条;新增 `ConnectingTabCancellationTests` 两条:

- 握手途中关标签 → 令牌被拉断、标签撤走、`BackgroundActivityService.Activities` 清空、
  `LastConnectionError` 与 Toast 都是空的;
- 握手在标签关掉**之后**才回来 → 会话被 `DisconnectAsync` 拆掉,而不是留成一条孤儿连接。

把 `OnDocumentClosed` 里那行 `CancelTabConnect` 注释掉,两条都红 —— 第一条会一直挂到测试超时,
那正是这个 bug 在用户机器上的样子:握手没有人叫停。

(同一轮里 `ExternalEditSessionManagerTests` 有 6 条失败,与本次改动无关:它断言
`%TEMP%\VelaShell\remote-edit` 不存在,而本机上跑过的真实应用在那里留下了会话目录,
`CleanupAll` 只清自己登记过的那些。改前改后同样红。)

## ✅ 49. 2026-09-07 CI 的 ubuntu 作业偶发失败:隔离插件"连不上"被报成"激活超时"

用户反馈:Ubuntu 的构建验证偶尔失败,有时又能过。失败的永远是同一条 ——
`IsolatedPluginTests.IsolatedPlugin_ActivatesInChildProcess_AndDeactivatesCleanly`,
报 `Activation timed out after 30s`。

### 一、报出来的那句话是错的,它把人送错了方向

CI 日志里那条用例的耗时是 **10 秒**,而报错说的是 30 秒 —— 两个数对不上,说明超时的
根本不是激活。`PluginProcessClient.StartAsync` 分三段等:

| 阶段 | 原来的预算 |
| --- | --- |
| 拉起进程 → 管道连上 | 写死 10 秒 |
| 握手往返 | 写死 10 秒 |
| 插件 `ActivateAsync` | `options.ActivationTimeout`(用例里 30 秒) |

第一段用的是 `CancellationTokenSource.CancelAfter`,超时逸出的是一个裸
`OperationCanceledException`;而 `PluginManager.ActivateAsync` 的 catch 把**任何**
`OperationCanceledException` 都写成 `Activation timed out after {ActivationTimeout}s`。
于是一次 10 秒的连接超时,顶着"激活超时 30 秒"的名字出现在报告里:阶段是错的,秒数也是错的。

### 二、根因:子进程在连管道之前,先去建了一整套 Avalonia

`VelaShell.PluginHost/Program.cs` 的顺序是「`SetupWithoutStarting()` → 起后台线程 →
连管道」。也就是说,那 10 秒的连接窗口里,子进程有一大段时间在做与连接毫无关系的事:
初始化 Avalonia。Linux 上这一段尤其贵 —— 连 X11、初始化字体子系统(冷机器上要建
fontconfig 缓存),而 `UsePlatformDetect` 之后还要探测 GLX/EGL,在 CI 的虚拟屏
(xvfb + llvmpipe)上这一步能慢到秒级。

为什么只有 ubuntu、而且只是偶发:`dotnet test VelaShell.slnx` **并行**跑八个测试程序集,
这条用例恰好落在测试步骤刚开始、几个程序集同时冷启动的那半分钟里,子进程要和它们抢核。
机器闲的时候一两秒就连上了,忙的时候就撞破 10 秒 —— 这正是"有时又能过"的来源。
(Windows 与 macOS 同一次全绿:Windows 那段初始化便宜得多,macOS runner 是带图形会话的真机。)

### 三、三处改动

**1. 管道先连,Avalonia 后建**(`Program.cs`)。连接是纯 IO,不碰 Avalonia,也就不必等谁:
`Main` 里先发起 `ConnectAsync`(不等它完成),再 `SetupWithoutStarting()`,握手与其余 RPC
仍旧排在 Avalonia 就绪之后 —— 它们要在派发线程上开窗口,那确实得等。
本机实测:连接从 356~512ms 降到 142~163ms,差的那 200~350ms 就是 Avalonia 初始化,
在 Linux 冷机器上正是会膨胀成好几秒的那一段。

**2. 启动预算与激活时限拆成两个数**(`PluginManagerOptions.IsolatedStartupTimeout`,默认 30 秒)。
两者量级根本不同:一边是"插件的一个方法该多快返回"(长了就是挂死),另一边是
"一个 .NET 进程冷启动 + 建 Avalonia 该多慢"。合成一个数只能取大的那个,等于把插件挂死的
判定也一并放宽。连接与握手现在共花这一个预算(连接用掉多少,握手就少多少,握手保底 5 秒)。

**3. 每一段超时都自报家门**。连接超时抛
`Plugin host process {pid} did not connect to the host pipe within {n}s`,握手超时抛
`connected but did not complete the handshake within {n}s`;`ActivateAsync` 也把
"宿主正在关"与"真的超时"分开写。成功路径补一条 Trace:
`connected in {x}ms, handshake done at {y}ms` —— 下次再慢,日志直接说得出慢在哪一段。

顺带修掉一个潜伏的错误分支:原先按"`WhenAny` 里哪条支路先完成"判进程是否夭折,而预算到点时
两条支路是**一起**被取消的,被取消的 `exited` 与真的退出长得一模一样,此时去读 `ExitCode`
会撞上「进程尚未退出」的异常 —— 一次超时会被报成一句风马牛不相及的错。判据改成 `HasExited`。

顺带一件同源的事:`VELA_PLUGIN_GPU != 1` 时的软件渲染原先**只配了 Win32 那一份**,
Linux 与 macOS 上默认仍走 GPU 后端。这条策略("插件面板不值得为它映射一整套显卡驱动")
与操作系统无关,补齐 `X11PlatformOptions` 与 `AvaloniaNativePlatformOptions`。

### 四、验收

`dotnet test VelaShell.slnx`(排除 Docker/CrossPlatform)全绿:3194 通过。
新增 `IsolatedPlugin_StartupBudgetExhausted_BlamesTheConnectPhase` —— 把启动预算压到 1ms,
断言错误里写的是 `did not connect` 而**不是** `Activation timed out`。把连接阶段那个
`catch` 的条件改成永假(即恢复"逸出裸 OperationCanceledException"的旧行为),这条立刻红,
且红的方式与 CI 上一模一样。

隔离插件那几条用例把 `IsolatedStartupTimeout` 显式放到 60 秒:这个数只决定"等多久才判失败",
健康时一分钱不花,给足了才不会把"机器忙"判成"插件坏"。

## ✅ 50. 2026-09-08 CI 的 macOS 作业偶发失败:背压用例拿固定 sleep 赌线程池已经起来了

用户反馈:macOS 的构建验证又挂了一次([run 34159705858](https://github.com/joesdu/VelaShell/actions/runs/34159705858)),
Windows 与 ubuntu 同一次全绿。失败的是
`TerminalBridgeFloodTests.DisposeReleasesAReadLoopWaitingOnBackpressure`:

```
Assert.IsGreaterThan(0, bridge.PendingBytesForTest)
样本没能攒出积压,这条用例就没量到东西。
lower bound: 0    actual: 0
```

### 一、这条报错把人往产品 bug 上引,而它其实是用例自己的前提没成立

「样本没能攒出积压」听起来像背压坏了。实际挂掉的是**断言之前那一步** ——
用例给读线程留了固定的 150ms,而那 150ms 里读线程一个字节都没读出来。

原来的写法:

```csharp
bridge.Start();
Dispatcher.UIThread.RunJobs();
Thread.Sleep(150);                                   // ← 赌读线程已经跑起来了
Assert.IsGreaterThan(0, bridge.PendingBytesForTest); // ← 而且只赌到「> 0」
```

### 二、根因:读循环排在线程池上,而线程池注入新线程是有节流的

`SshTerminalBridge.Start` 里是 `_readTask = Task.Run(() => ReadLoopAsync(token))`。
线程池里没有空闲工作线程时,**注入新线程有节流** —— 饱和后大约每 500ms 才多一条。
于是「睡 150ms,读线程总该跑起来了吧」这个假设,在**核数少**的机器上直接不成立:

- `MinThreads` 默认等于 `ProcessorCount`;
- macos-latest 只有 **3 核**,而 `dotnet test VelaShell.slnx` 是**并行跑 8 个测试程序集**的。
  看这次的时间线:20:32:49 起 Core / Controls / Infrastructure 三个程序集同时启动,
  20:32:50 起 Plugin.Ai(一直跑到 20:35:17),Terminal.Tests 20:33:01 才开始、20:33:06 就挂了 ——
  那半分钟里机器上至少有三个 testhost 在抢 3 个核,其中 Infrastructure.Tests 还在拉起真正的
  `PluginHost` 子进程(§49 刚说过它在 CI 上有多贵)。

**证据(本机可复现,不用等 CI 抽风)**:

| 条件 | 结果 |
| --- | --- |
| 默认(32 核) | ✅ 通过 |
| `DOTNET_PROCESSOR_COUNT=2`,单跑这一条 | ❌ **必挂**,与 CI 上一模一样 |
| `DOTNET_PROCESSOR_COUNT=2` + `DOTNET_ThreadPool_ForceMinWorkerThreads=10` | ✅ 通过 |
| `DOTNET_PROCESSOR_COUNT=3 / 4` | ✅ 通过 |
| `DOTNET_PROCESSOR_COUNT=2`,整个类一起跑 | ✅ 通过 |

最后一行正是「偶发」的来源:同类里排在前面的几条用例会把线程池**预热**出工作线程,
轮到这一条时池子是热的。它挂不挂,取决于此前跑过什么、以及同机别的进程在不在抢 CPU ——
两者都不是用例能控制的。

**加一条 min worker thread 就转绿**,这一条足以把根因钉死在线程池上,而不是「机器慢」这种含糊的说法。

### 三、顺带发现:那个断言本身也太弱,弱到可能白绿

用例的名字与注释说的是「读线程**停在背压闸上**时 Dispose 要放行它」,
而断言只要求 `积压 > 0`。**只攒了一块 16 KB 时读线程根本不在闸上** ——
Dispose 当然快,断言照样绿,却什么都没验到。

也就是说:前提**又脆又弱**。脆是会误报,弱是会漏报。

### 四、改法:等真正的前提,而不是睡一个固定的数

```csharp
Dispatcher.UIThread.RunJobs();   // 只跑一轮;此后 UI 不再排空,积压只增不减

Assert.IsTrue(
    WaitUntil(() => bridge.PendingBytesForTest > SshTerminalBridge.HighWaterBytesForTest,
              TimeSpan.FromSeconds(20)),
    "…读线程没顶到闸上,这条用例就没量到东西。");
```

三处改动:

1. **`WaitUntil` 取代 `Thread.Sleep`** —— 轮询到条件成立为止,超时 20 秒。
   调用方本就跑在 headless UI 线程上,循环里的 `Sleep(2)` 顺带保证了 `FlushPending`
   不会被派发,积压因此只增不减,正是等待条件所需要的;`Sleep` 而非忙等,是因为 CI 上核数少,
   忙等会把读线程本身饿住。
2. **判据换成「越过高水位」** —— 那才是「读线程已经(或即将)停在闸上」的真正判据。
   为此在 `SshTerminalBridge` 上开一个 `internal static long HighWaterBytesForTest`,
   让用例从产品代码取这个数,而不是在测试里另抄一份 8 MB。
3. **`Flood()` 的空闲计数改成「见到第一个字节之后才开始计」**。它原先一上来就计,
   50 轮(约 50ms)能在读线程还没被调度时就走完 —— 同一个根因下的另一颗雷,
   只是还没炸过。现在挂上 `started` 标志。

### 五、验收:先证明它还抓得住东西,再证明它不再误报

**负控制(重要)**:第一次做负控制时把 Dispose 里的 `ReleaseDrainGate()` 注释掉,
用例**仍然是绿的** —— 因为 `_cts.Cancel()` 已经能把 `_drainGate.WaitAsync(cancellationToken)`
唤醒。也就是说 Dispose 里那句 `ReleaseDrainGate()` 是**双保险**,不是唯一出路
(Dispose 的注释把令牌写成「兜底」,实际两条路都走得通)。

把两条路一起堵上(注释掉 `ReleaseDrainGate()` **且**让闸的等待不认令牌)之后:

```
Dispose 花了 2002ms —— 读线程八成一直挂在背压闸上等到超时。
失败!  - 失败: 1,通过: 0
```

**红了,而且红在正确的理由上** —— 用例确实在量它声称要量的东西,不是白绿。

**正向验收**:`ProcessorCount` = 1 / 2 / 3 / 4 各跑两遍(单跑这一条 + 整个类),
八次全绿;`DOTNET_PROCESSOR_COUNT=2` 下整份 `VelaShell.Terminal.Tests` 396 条全绿;
`dotnet build VelaShell.slnx -warnaserror` 零警告,
`dotnet test VelaShell.slnx`(排除 Docker/CrossPlatform)**3194 通过**。

### 六、和 §49 是同一类病

§49 是 ubuntu 上「拉起进程 → 连管道」写死 10 秒,§50 是 macOS 上「读线程该起来了」写死 150ms;
再往前还有 `28b3c6f`「插话用例别再靠 700ms 延时赌'这一轮还在跑'」。三次都是
**拿一个固定时长去代替一个本该显式等待的条件**,而这个时长在开发机上够、在 CI 上不够。

**留给下一次的判据**:测试里出现 `Thread.Sleep(常数)` 且后面紧跟一条依赖它的断言时,
问一句「这个数凭什么够」。答不上来就把它换成「等条件 + 给足超时」——
超时给多大都不要紧,健康时一分钱不花;赌一个固定值才是每次 CI 都要掷一遍的骰子。

---

## ✅ 51. 2026-09-08 双击打开的远端文件也要自动回传;编辑会话不再赌编辑器进程(#396)

用户报的是「文件编辑之后自动上传」这个功能失效,原话:
**「第一次保存有效,后面再编辑保存就没有效果了」**(编辑器是 Notepad--,Windows 11)。

### 一、把「只有第一次生效」认出来:两个入口长得一样,行为相反

截图里 Ndd 标题栏的路径是决定性证据:

```
C:\Users\falcon\AppData\Local\Temp\VelaShell\93f408970803433ea9fbbc10fbd39f32\docker-compose.yml
```

那 32 位十六进制是 `_sessionId.ToString("N")`,全仓只有一处这么拼路径。当时的三个「打开」是:

| 入口 | 代码 | 临时目录 | 保存后回传 |
| --- | --- | --- | --- |
| **双击** | `ActivateAsync` → `DownloadAndOpenAsync` → `Launcher.LaunchFileInfoAsync` | `VelaShell\<sessionId:N>\` | **没有。全程没有 watcher** |
| 右键「打开」 | `OpenItemAsync` → 内置 AvaloniaEdit | `VelaShell\builtin-edit\<8hex>\` | ✅ 每次 Ctrl+S |
| 右键「使用默认编辑器打开」 | `OpenWithDefaultEditorAsync` → `ExternalEditSession` | `VelaShell\remote-edit\<8hex>\` | ✅ 600ms 防抖 |

报告人截图里的文件来自第一行。所以「后面再存就没反应」不是失效,
**是那条路径从来就没有过回传**;而「第一次保存有效」是他此前按维护者的说法用右键菜单开的那一次
(远端 mtime 12:07:14 对得上)。两个入口在界面上没有任何区别,用户当然分不出来。

逐行核过 `ExternalEditSession` 的防抖/上传闸/pending 记账之后确认:
**它对连续保存本来就是对的**,第 N 次不会丢。主因就是入口不一致。

### 二、顺带挖出来的第二条复现路径:3 秒启发式

`LaunchEditor` 用「编辑器进程 3 秒内退出就当作是单实例编辑器的引导进程」来判断编辑器是否还开着,
否则就认为"编辑器关了" —— 停 watcher、删临时目录。而 Notepad-- 恰恰是带标签页的单实例编辑器
(截图里 nginx.conf 与 docker-compose.yml 同窗口)。冷启动慢一点,引导进程 3.5 秒才退出,
会话就被当成"编辑器已关闭"拆掉,**此后每一次保存都无声丢失**——
一模一样的现象,另一条路。进程状态根本判断不出"用户还要不要编辑这个文件"。

### 三、改法

**入口收敛。** `ExternalEditSessionManager` 升成通用的 `RemoteEditSessionManager`
(`src/VelaShell/Services/RemoteEditSessionManager.cs`),入口只决定**谁来打开**
(`RemoteEditOpenWith`:系统默认程序 / 配置的编辑器 / 不打开),要不要监视回传是会话自己的事。
双击现在也下载到 `remote-edit\<8hex>\` 的独占子目录(顺带修掉"每会话共享一个临时目录、
不同远端目录的同名文件互相覆盖")。会话按 `(SessionId, RemotePath)` 去重:
同一个远程文件重复打开复用同一份副本与同一个 watcher,不再开第二个对着第二份副本各存各的。

**生命周期不再赌进程。** 删掉 3 秒启发式。进程退出只触发一次补传(`NotifyEditorExitedAsync`),
**不拆会话**。会话只由三件事结束:用户在浮窗里点「结束监视」、所属远程会话关闭
(`SftpDocumentViewModel.CloseCoreAsync` → `CloseScopeAsync`,**赶在 SFTP 连接关掉之前**,
否则那几次补传注定失败)、应用退出(`CleanupAll`,原有的"传不完就留草稿并提示路径"不变)。

**可见性。** 传输浮窗新增「正在编辑」分组(`FileTransferViewModel.Edits` +
`RemoteEditItemViewModel`):每行是一个被盯着的文件,显示上次回传时刻 / 待上传 / 正在上传 /
失败原因,行操作 = 立即上传、打开本地目录、结束监视。失败会把面板叫回来并标红,
「立即上传」同时充当重试入口。此前自动上传是完全隐形的:静默工作,静默失效,
用户能提供的只有一句"没上传"。

**诊断。** `RemoteEditLog` 常开写 `~/.velashell/logs/remote-edit.log`:
会话建立/复用、每一次文件事件、上传起止与失败原因、会话销毁。写量很小,
换来的是下次这类"保存了但什么都没发生"的 issue 有据可查。

**设置。** 设置 → 文件传输 → 远程编辑:「双击文件时」(系统默认程序 / 内置编辑器 /
配置的编辑器,默认仍是**系统默认程序** —— 改默认值等于动所有存量用户的肌肉记忆,
这个 issue 要修的是回传,不是打开方式)与「编辑后自动上传」(默认开;
关掉后改动仍然记账,在「正在编辑」里手动上传)。改这个开关对**已经开着的**会话即时生效。

### 四、途中踩到的两颗雷(都由全量测试抓出来)

1. **`Dispatcher.UIThread.CheckAccess()` 的快路把测试宿主打崩了。**
   `SyncEdits` 原本"已在 UI 线程就直接调,否则 Post"。在没有真正 UI 线程的宿主里
   `CheckAccess()` 会对着 `FileSystemWatcher` 的线程池线程点头,于是集合变更直接打到
   绑定的 `ItemsControl` 上:`The calling thread cannot access this object`。改成**一律 Post**。
2. **界面异常顺着事件冒回去,把"打开文件"整个跳过了。** 上面那条异常从
   `RaiseSessionsChanged()` 冒到 `OpenAsync`,让紧随其后的 `LaunchAsync()` 一步没走 ——
   文件下下来了却没人打开,而报错是 `ErrorMessage` 里一句看不懂的话。
   `RaiseSessionsChanged` 现在逐个订阅者 try/catch:**界面炸了不能连累编辑会话本身**。

### 五、回归用例

`tests/VelaShell.Tests/Services/RemoteEditSessionManagerTests.cs`(原 `ExternalEditSessionManagerTests`)。
此前这里覆盖了收尾、慢链路、失败保留草稿 —— **唯独没有一条按住 watcher 连存几次**,
正是这个 issue 的形状。新增:

- `ConsecutiveSaves_AreEachUploaded` —— 连存三次要传三次(#396 本体)。
- `AtomicSave_ViaReplace_IsUploaded` —— 写临时文件 + `File.Replace` 的"安全保存"也要认。
- `AfterTheEditorProcessExits_LaterSavesAreStillUploaded` —— 3 秒启发式的回归。
- `SavesInsideOneDebounceWindow_UploadTheLatestContentOnce` —— 防抖只合并,不吃掉最后那份内容。
- `WithAutoUploadOff_...` / `TurningAutoUploadBackOn_FlushesTheHeldChange` / `AfterAFailedUpload_UploadNowRetries`。
- `OpeningTheSameRemoteFileTwice_ReusesOneSession` / `ReopeningWithUnuploadedChanges_KeepsTheLocalDraft`
  —— 后者是条硬红线:复用时**绝不能**拿远端内容盖掉还没传上去的本地改动。
- `FileBrowserViewModelTests.DoubleClick_RegistersAnEditSessionThatUploadsOnSave` —— 入口这一侧的守门。

`dotnet build VelaShell.slnx` 零警告;`dotnet test VelaShell.slnx` **3205 通过 / 0 失败**。

### 六、留给下一次的判据

三个入口做同一件事的三种做法,界面上却完全看不出区别 —— 这类"外观一致、行为分叉"的功能,
**用户报上来的现象一定不指向真凶**(这次报的是"自动上传失效",真凶是另一个入口压根没这功能)。
判据:同一个动词在界面上出现多次时,先问"它们的能力集合是不是同一个";不是的话,
要么补齐,要么让界面说清楚差别 —— 不能靠用户去猜自己点的是哪一个。

---

## ✅ 52. 2026-09-08 编辑器都关掉了,「正在编辑」那一行还挂着(#396 反馈)

上一节把「编辑器进程退出」整个降级成"只补传、不拆会话",理由是旧的 3 秒启发式会误杀。
代价当场就来了 —— 用户反馈:**「我明明编辑器都关掉了,传输列表还显示正在编辑」**。
会话从此只有三个出口(手动结束 / 关会话 / 退应用),**没有一个是"编辑器关了"**,
而那恰恰是最自然的那个。

### 一、两条岔路的代价差着量级,不能用同一个判据

| 判错的方向 | 后果 |
| --- | --- |
| 收早了(编辑器还开着就拆会话) | 用户之后的保存**悄悄丢掉**,还不报错 —— 就是 #396 本身 |
| 收晚了(编辑器关了还留着) | 列表里多挂一行 |

旧实现的错不在"用了时长",而在**让时长决定生死**:一个 3 秒的阈值同时管着这两条路,
猜错任一边都要付上面那张表里的代价。

### 二、改法:时长只决定"要不要去找接手的实例"

```
进程退出
  ├─ 先把攒着的那次改动传掉(FlushAsync)
  ├─ 活得比 8 秒短 → 多半是单实例编辑器的引导进程,文件已转交
  │    ├─ 找到同名的存活实例 → 改盯它(Track),会话继续
  │    └─ 找不到 → 仍然继续守着。判断错了就是"保存无声丢失",绝不据此收摊
  └─ 活了一阵才退 → 这就是编辑器本身,用户把它关了 → 收会话
```

于是**快退出这条路永远不会杀会话**,#396 关掉的门没有再打开;
而"用户关掉编辑器"走的是慢退出那条,行会自己消失。

接手实例的找法(`TryAdoptSurvivingInstance`):按进程名找还活着的同名进程,取**最早启动**
的那个 —— 单实例编辑器里它就是主实例。⚠️ 进程名必须在**进程还活着的时候**记下来,
退出之后 `Process.ProcessName` 直接抛。收养次数封顶 4 次,防的是"一直有同名进程在退"这种打转。

顺带一个正确的副作用:非单实例编辑器(记事本)本来就是我们起的那个进程在编辑,
它退出必然是慢退出 —— 不会去找同名实例,也就不会因为用户另开着一个记事本而赖着不走。

### 三、双击那条路以前根本拿不到进程句柄

`TopLevel.Launcher.LaunchFileInfoAsync` 只回一个 `bool`。也就是说**默认的双击入口
从来就没有任何"编辑器关了"的信号** —— 上面那套判断对它一点用都没有。

新增 `FileBrowserView.OpenLocalFileTrackedAsync`:走 `Process.Start` + `UseShellExecute`
(与 Launcher 同一套文件关联,只是多给一个句柄),拿不到就返回 null 回落到原来的
`Launcher` 那条。关联走 DDE / COM 复用已有实例时它会返回 null,那种情况下句柄本来也拿不到。

放在**视图**里而不是 VM 里是有意的:回归用例覆盖的是 VM,它们只挂 `OpenLocalFile`,
于是测试进程里一个真实编辑器也不会被拉起来 —— 否则 CI 上跑一遍就是满屏记事本。

### 四、回归用例

- `WhenABootstrapProcessExitsQuickly_TheSessionKeepsWatching` —— 快退出后再存一次仍要回传
  (#396 那条门的守卫)。
- `WhenTheEditorItselfExits_TheSessionEnds` —— 慢退出后 `ActiveSessions` 必须空掉
  (这次反馈的守卫)。
- `WhenTheEditorExits_ThePendingSaveIsUploadedBeforeClosing` —— 收会话之前,
  防抖窗口里那次保存得先落到远端。**这是两条路交界处唯一真会丢东西的地方。**

`dotnet build VelaShell.slnx -warnaserror` 零警告;`dotnet test VelaShell.slnx` **3207 通过 / 0 失败**。

### 五、留给下一次的判据

§51 修的是"收早了",这一节修的是"因此收不掉了" —— 同一个开关来回拨了两次。
根子在于**拿一个标量(进程活了多久)去回答两个代价不对称的问题**。
判据:一个判断的两个方向后果差着量级时,别用同一个阈值决定两边;
让阈值只管那个**便宜的方向**,昂贵的那边要么找到硬证据(这里是"有没有同名实例还活着"),
要么一律往安全的方向倒。

---

## ✅ 53. 2026-09-08 VS Code 关掉了那一行还挂着:别拿单个进程句柄代表"应用还开着"(#396 反馈二)

用户反馈:双击一个 `.profile`,VS Code 打开;**关掉 VS Code 之后那一行还显示「正在编辑」**;
而前面用记事本开的 `test.txt` 一切正常。

### 一、差别在于 VS Code 是一堆同名进程

§52 的做法是:引导进程"启动即返回"时,去找一个**同名的存活进程改盯它**(收养)。
这个模型默认「一个应用 = 一个进程」——

| 编辑器 | 形态 | §52 的结果 |
| --- | --- | --- |
| 记事本 | 单进程,我们起的就是真身 | 慢退出 → 收会话 ✅ |
| VS Code | 一个实例底下一堆同名进程(主进程 + GPU + 渲染 + 扩展宿主) | 收养到谁都不代表"应用还开着" ❌ |

收养到辅助进程,它随时会自己退(而应用还开着);收养到主进程,也未必等得到那个事件。
两头都不对 —— 因为**这个问题根本不该问"某个进程死了没"**。

### 二、把问题换掉:不问某个进程,问这个名字还有没有活的

```
进程退出
  ├─ 活了一阵才退 → 编辑器本身,用户关了它 → 收会话(不变)
  └─ 启动即返回   → 引导进程转交完就走了
        └─ 轮询:Process.GetProcessesByName(编辑器名) 还有没有结果
              ├─ 有 → 记下"见过它起来",继续守
              └─ 没有,且**之前见过** → 编辑器关了 → 补传 → 收会话
```

多进程、单实例、启动器转交,三种形态一个答案。轮询 5 秒一次
(一次 `GetProcessesByName` 的开销可以忽略,而这是"编辑器关了"唯一靠得住的信号)。

**「之前见过」这个前置条件是必需的**:启动器把文件转交出去、真身还在加载的那一瞬,
名字底下可能一个进程都没有 —— 此时收摊就又回到 #396 那种"保存无声丢失"。
查不到进程列表(权限/平台)时也一律当作"还活着",继续往安全的方向倒。

⚠️ 每轮轮询 `GetProcessesByName` 都会新开一批进程句柄,**必须逐个 Dispose**,
否则是一个随时间稳定增长的句柄泄漏。

### 三、还有一种情况是真的没抓手,那就把话说出来

关联程序通过 DDE / COM 复用已有实例时,`Process.Start` 连句柄都不返回,进程名也就无从谈起。
这种会话确实只能手动结束 —— 但**不能让用户对着一个永远不消失的"正在编辑"发愣**。
`RemoteEditSnapshot` 加了 `EditorTracked`,为 false 时那一行的状态直接写
「已在监视 · 用完请手动结束」(`Transfer_EditUntracked`,五份 resx 齐)。

### 四、回归用例的取舍:别拿真实进程当判据

第一版用例真起了一个 `cmd /c exit` 当引导进程,再靠进程名去数存活。**这是错的** ——
进程名是全机器共享的,CI 上恰好另有一个 `cmd`(或 `sh`、`dotnet`)在跑,用例就随机变红,
而且红得毫无道理。改成把存活探针做成可注入的(`EditorLivenessProbeForTest`)+
可手动推进的一次轮询(`PollEditorLivenessForTestAsync`),三条路各自确定:

- `WhenNoInstanceOfTheEditorIsLeft_TheSessionEnds` —— 见过 → 消失 → 收会话(这次反馈)。
- `WhileAnInstanceOfTheEditorIsStillAlive_TheSessionIsKept` —— 一直在 → 一直守。
- `BeforeAnyInstanceIsEverSeen_AnEmptyProbeDoesNotEndTheSession` —— 没见过就查不到,不算它关了。

另外把 `WhenTheEditorExits_ThePendingSaveIsUploadedBeforeClosing` 的断言从"只传一次"
放宽到"最后传上去的是它":一次 `WriteAllText` 在 Windows 上常触发多个 watcher 事件
(大小 + 修改时间),补传与收尾各拿到一次是正常的 —— 内容相同,不是缺陷。
断言"恰好一次"测的是文件系统事件的合并时序,不是这条链路的正确性。

`dotnet build VelaShell.slnx -warnaserror` 零警告;`dotnet test VelaShell.slnx` **3210 通过 / 0 失败**。

### 五、留给下一次的判据

§51 → §52 → §53 是同一个开关拨了三次,每一次都因为**判据选得比问题窄**:
先是"进程退出 = 编辑器关了"(单实例编辑器不成立),再是"某个同名进程活着 = 应用开着"
(多进程应用不成立)。判据:要判断一个**应用**的生死,别拿**某一个进程**当代理;
桌面应用与进程早就不是一一对应了。

---

## ✅ 54. 2026-09-08 把「正在编辑」改成只在出问题时出现,整段进程跟踪删掉(#396 反馈三)

用户的质疑,原话:**「为什么不直接 watch 这个文件?编辑后上传即可,不需要在传输列表中
显示正在被编辑这类东西。」** 这个质疑是对的,而 §52、§53 那两版进程跟踪是我自己绕出来的弯路。

### 一、回传从来只靠 watcher,进程跟踪对功能零贡献

`FileSystemWatcher` 盯 `LastWrite | Size | FileName`,保存 → 600ms 防抖 → 上传。
这条链从头到尾没有进程什么事。指纹那一层也早就在了:只看 mtime 会漏掉
「写临时文件再改名顶上去」的安全保存(原文件 mtime 根本不变),所以订阅里带 `FileName`
并挂了 `Renamed` —— `AtomicSave_ViaReplace_IsUploaded` 守的就是这个。

**进程跟踪存在的唯一理由,是回答「浮窗里那一行什么时候该消失」。**
而那个问题等价于"编辑器关了没" —— 单实例编辑器(VS Code、Notepad--)把路径通过 IPC
交给已有实例后引导进程立刻退出,**我们启动的进程根本不是最后拿着文件的那个**,
真正打开文件的是一个我们从没启动过、也没有任何 OS 层面联系的进程。这个问题答不出来。
为它试了两版(§52 收养同名进程、§53 轮询同名实例存活),都是在给一个不该问的问题找近似解。

顺带回答"能不能靠文件占用":不能。文本编辑器打开文件是「读进内存 → 立刻关句柄 →
保存时再开一下写完再关」,用户盯着屏幕改的那半小时里文件完全空闲,占用判断会一直说
"没在编辑"。真会长期持有句柄的是 Word / Excel 那类,恰好不是这条链路的对象。

### 二、改法:问题本身删掉

「正在编辑」从**常驻列表**改成**只在出问题时出现**:回传失败,或有改动还没传上去
(`FileTransferViewModel.NeedsAttention`)。于是

- 日常编辑保存全程无感,浮窗里不冒任何东西 —— 用户要的就是这个;
- 出了事那一行**就该一直在**,直到用户处理掉 —— "什么时候消失"不再是个需要猜的问题;
- §52 / §53 整段进程跟踪(`Track`、`OnEditorProcessExitedAsync`、`StartLivenessPoll`、
  `AnyEditorInstanceAlive`、`OpenLocalTrackedAsync` 与视图那侧的 `ShellExecute` 取句柄)全部删除。

**没有整块删掉这一组**,是因为回传失败时本地副本是那份改动<b>唯一的存身之处</b>,
用户得能看见它、重试它、找到它在哪 —— #396 最初的形态正是"静默失败、无从分辨"。
`Uploading` 也算需要露面:失败后点「立即上传」的那几秒 pending 已被取走,
不带上它那一行会当场闪没,重试看起来像是把行删了。

界面上 `Edits` → `PendingEdits`,标题从「正在编辑」改成「待回传」(它现在描述的是
**这些改动的处境**,不是编辑器的状态)。随之退役的键:`Transfer_EditWatching`、
`Transfer_EditUploadedAt`、`Transfer_EditUntracked` —— 常驻列表没了,这三种状态再也到不了。

### 三、回归用例

- `AHealthyEditSession_NeverShowsUpInThePanel` —— 一切顺利就不该有行(这次反馈)。
- `AFailedUpload_ShowsUpInThePanel` —— 失败必须露面(守住不能一起删掉的那半)。
- 连续保存、原子保存、防抖合并、自动上传开关、失败重试、复用不覆盖草稿等原样保留。

`dotnet build VelaShell.slnx -warnaserror` 零警告;`dotnet test VelaShell.slnx` **3206 通过 / 0 失败**。

### 四、留给下一次的判据

§51 → §54 是同一处改了四次,而 §52、§53 两次<b>都在解一个不该存在的问题</b>:
它们服务的不是用户要的能力(保存即回传),而是我为了"可见性"顺手加的一块常驻 UI。
判据:**加一块常驻状态显示之前,先问它需要什么信号来消失,以及那个信号拿不拿得到。**
拿不到就别做成常驻 —— 改成"例外才出现",既不用回答那个问题,信息密度还更高。

---

## ✅ 55. 2026-09-08 传输浮窗里那一组整块撤掉,远程编辑从此不出现在界面上(#396 反馈四)

用户反馈:**「把显示正在编辑的那个完整的去掉吧。不需要这个。现在闪一闪的,很奇怪的操作。」**

「闪一闪」是 §54 的直接后果,而且是我的判据选错了:那一组的显示条件里带着
`HasPendingChange`,而**每一次保存都会让它在防抖那 600ms 里为真** ——
于是每存一次,行就冒出来一下、传完又消失。本来想做成"只在出问题时出现",
实际做成了"每次保存闪一次"。

### 一、撤掉了什么

- `FileTransferView.axaml` 里那一整组、`FileTransferViewModel` 的
  `PendingEdits` / `HasPendingEdits` / `NeedsAttention` / 三个行命令 / `RevealLocalPath` /
  `SyncEdits` / 静态事件订阅(连带 `IDisposable`),以及 `RemoteEditItemViewModel` 整个文件。
- 服务侧随之失去消费者的读模型:`RemoteEditSnapshot`、`Snapshot()`、`SessionsChanged`
  与 `RaiseSessionsChanged`、`Publish()`、`Find`、`CloseAsync`、`UploadNowAsync`、
  `RemoteEditRequest.ServerName`,以及只为显示而存在的 `_lastUploadedAt` / `_lastError`。
  会话对外只剩 `HasPendingChange` 与 `State`(回归用例与诊断日志读)。
- 面板的可见性规则回到原样:`Transfers.Count > 0`,自动隐藏不再被编辑会话拦住。

失败仍然说得出话,只是不再自成一组:回传本身走的就是传输行,失败标红;
`OnError` 把原因写进文件面板;会话收尾时 `Svc_RemoteEditDraftKept` 告诉草稿在哪儿。

### 二、顺带堵掉一个自己造出来的陷阱

`AutoUploadOnEdit` 关掉时,原本是"改动照样记账,等用户在那一组里点上传"。
那一组没了,**这笔账就没有出口了**;更糟的是收尾时 `ShutdownAsync` 还会把它补传上去 ——
开关写着"不自动上传",关掉标签页却传了,那是骗人。

现在关掉 = **watcher 根本不启用**(`_watcher.EnableRaisingEvents = request.AutoUpload`,
复用刷新后也按同一个值恢复)。本地副本随便改,一个字节也不回传。
设置项说明与五份 resx 一起改成这个口径。这个开关也不再对已开着的会话即时生效
—— 它现在决定的是"建会话时挂不挂 watcher",对下一次打开生效即可。

### 三、回归用例

- `WithAutoUploadOff_NothingIsWatchedAndNothingIsUploaded` —— 关掉后不记账、不上传,
  **且收尾也不偷偷补一发**(最后这条断言守的正是上面那个陷阱)。
- `ReopeningWithUnuploadedChanges_KeepsTheLocalDraft` 改用"回传一直失败"来制造未落地状态
  (原来靠 `AutoUpload = false`,那个手法随着语义变化失效了)。
- 连续保存、原子保存、防抖合并、会话复用、收尾补传/草稿保留等原样保留。

`dotnet build VelaShell.slnx -warnaserror` 零警告;`dotnet test VelaShell.slnx` **3202 通过 / 0 失败**。

### 四、留给下一次的判据

§51 加上这块 UI,§52/§53 为它做了两版进程跟踪,§54 想靠"只在异常时显示"救它,§55 整块删掉。
五节里有三节半在伺候一个**用户从来没要过**的东西 —— 它是我在阶段 2 以"可见性"为名自己加的。

判据两条:
1. **状态显示的触发条件必须是稳态,不能是过程量。**`HasPendingChange` 是过程量:
   它在每次保存的防抖窗口里都为真,拿它当显示条件必然闪。
2. 更根本的:**用户要的是"保存后自动上传",不是"看见它在自动上传"。**
   为一个能力配一块常驻状态显示之前,先问这块显示解决了谁的什么问题;
   答不上来就别加 —— 加了之后它自己会长出一串需要伺候的问题。

## ✅ 56. 2026-09-08 按下 Ctrl 那一刻手型就该出来,不该等鼠标抖一下(#397)

用户反馈:光标停在链接上按 Ctrl,光标样式不变;必须挪一下鼠标手型才出来。

### 一、根因:判定只挂在指针移动上,而按 Ctrl 时鼠标是静止的

链接悬停反馈(手型 + 地址气泡)整套逻辑只有一个入口 ——
`VelaTerminalControl.OnPointerMoved` 里那句 `UpdateLinkHover(e)`。而
`UpdateLinkHover` 的第一句就是从**事件**里读修饰键:

```csharp
if (_selecting || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
```

也就是说"Ctrl 有没有按下"这个状态**只在鼠标移动事件里被采样**。用户按 Ctrl 时鼠标一动不动,
不产生 `PointerMoved`,于是没有任何代码去重新判定 —— 抖一下鼠标才有事件把新的
`KeyModifiers` 带进来,这正是用户描述的"要移动才会发生变化"。

松开侧反而一直是即时的:`OnKeyUp` 专门认了 `Key.LeftCtrl or Key.RightCtrl` 去
`ClearLinkHover()`。**按下侧从一开始就没有对称的那一半**。

原注释里写着这套设计的动机 ——「只在 Ctrl 按下时才做匹配,否则每一次鼠标移动都要跑一遍正则」。
这个取舍本身没错,漏掉的是"按下 Ctrl"这个**边沿事件本身也是一次该重判的时机**。

### 二、修法:把判定与指针事件解耦,补上按下侧

- `UpdateLinkHover(PointerEventArgs)` → `UpdateLinkHover(Point position, bool ctrl)`。
  按 Ctrl 那一刻根本没有指针事件可传,原签名注定调不到。
- 新增 `_lastPointerPosition`(`Point?`):`OnPointerMoved` 里更新,`OnPointerExited` 里置 null。
  指针已经不在控件上,记下的位置就作废 —— 否则之后按 Ctrl 会照着一个旧位置亮手型。
- `OnKeyDown` 顶部认 `Key.LeftCtrl or Key.RightCtrl`,就地用最后的位置重判一次。
  裸修饰键在 `TerminalKeyRouter.Classify` 里落到 `TerminalKeyAction.None`(`InputEncoder.Encode`
  对纯修饰键返回空),插在这里不动下面任何一个分支。

两处状态复位,都是为了让 `_ctrlHeld` 这个新的边沿标记不会卡死:

- `OnKeyUp` 清掉它 —— 按住 Ctrl 会自动重复 KeyDown,只在 false→true 那一瞬做判定,
  每次重复都跑一遍正则没有意义(这正是原设计要省的那笔开销)。
- `OnLostFocus` 也清掉 —— **焦点若在 Ctrl 按住时被抢走,那次 KeyUp 会送给新的焦点控件**,
  这里不清就永远停在按下态,下次再按 Ctrl 会被 `if (_ctrlHeld) return;` 直接吃掉。

### 三、回归用例

`LinkHoverTests` 新增三条:

| 用例 | 守的是什么 |
| --- | --- |
| `PressingCtrlWhileAlreadyOverTheLink_ShowsTheFeedbackWithoutMovingTheMouse` | #397 本体:先无修饰键悬上去,鼠标一动不动只按 Ctrl,手型与地址就得出来 |
| `PressingCtrlOverPlainText_ShowsNothing` | 新入口不能把非链接的地方也点亮 |
| `PressingCtrlAfterThePointerLeft_ShowsNothing` | 指针移出控件后位置作废,不照着旧位置亮手型 |

把 `src` 侧的改动 stash 掉重跑,第一条如期变红(`expected: "https://example.com/docs",
actual: null`)—— 那正是这个 bug 在用户机器上的样子。

`dotnet build VelaShell.slnx -warnaserror` 零警告零错误;`dotnet test VelaShell.slnx` 全绿。

### 四、留下的边界(**没修**)

**焦点不在终端控件上**时(比如焦点在 SFTP 面板或搜索框),鼠标悬在终端上按 Ctrl,
KeyDown 送不到 `VelaTerminalControl`,手型仍然不会出现。要覆盖它得在窗口层挂隧道事件,
范围与风险都大一截,而 issue 描述的是终端有焦点的正常使用路径。留作已知限制。

判据:**成对的状态转换要么两侧都做,要么两侧都不做。**这个 bug 的形状就是
"松开侧有 KeyUp 钩子、按下侧没有 KeyDown 钩子",而不对称本身在代码里是看不见的 ——
`OnKeyUp` 那个方法孤零零地存在,读的时候不会有人问"那按下呢"。

## ✅ 57. 2026-09-08 插件的发布者,得一直是同一个人(用户反馈)

`.vpx` 的验签一直都在:容器摘要、签名块、未签名 / 陌生发布者的单次授权,`CheckSignature`
一条不少。但它只回答一件事 —— **这个包从签完之后没被人动过**。它不回答另一件事:
**签它的人还是不是上次那个人**。

少了后面这一问,任何一个能骗你点下"安装"的包,只要复用同一个 id,就能把已经装着的插件整个换掉,
而它自己的签名完全有效、对话框上甚至写着"签名有效"。`PluginPackageExtractor` 那三道闸挡的是
畸形包(路径逃逸、解压炸弹、条目轰炸),挡不住这个 —— 真正挡住**坏作者**的是本节这一条。

### 一、字段早就有了,只是全仓没人读

`InstalledPluginReceipt.PublisherPublicKey` 在安装收据里躺着,**零消费者**:写它的
`SaveInstallReceiptAsync` 会取出上一份收据,但那只是为了保存失败时回滚。也就是说宿主一直记着
"这个插件当初是谁签的",却从来没有拿它比对过任何东西 —— `feature-plan.md` 那张 P0 表里
"存了但不生效"的形状,这是最贵的一例:它存的不是一个偏好,是一份身份。

现在它被 `PluginManager.CheckPublisherContinuityAsync` 消费:同一个 id 的后续版本,
必须仍由钉住的那把私钥签名。公钥一致才放行,不一致就把问题交给用户。

### 二、闸卡在"卸载旧版"之前,不是之后

安装流程是「解包到临时目录 → 读清单 → 卸载同 id 旧版 → 搬进最终位置」。连续性检查要等
**清单读出来**才做得了(得先知道这个包声称自己是哪个 id,才知道该比对哪一条收据),
但必须赶在**卸载旧版之前**落位。差一步的后果是实打实的:被拒的那一刻,用户手上那个还装着的
插件已经被卸掉了 —— 拦截反倒帮攻击者完成了一半。用例里专门断言拦下之后旧插件仍是 `Active`。

### 三、换钥不由宿主判死,但也绝不静默放行

一次正常的密钥轮换,与发布身份被劫持,从宿主这个位置看长得**一模一样** ——
这件事机器判不了(市场那边的静态检查也只把 `SIGNATURE_KEY_ROTATED` 转人工,不判死)。
所以宿主抛 `PluginPublisherChangedException`,它带着对话框需要的全部字段:显示名、
钉住的指纹、这个包的指纹。界面把两个指纹摆出来,用户认了,调用方带
`allowPublisherChange: true` 再来一次,新公钥随即成为此后的基线。

**"把签名去掉"同样拦**,而且 `PackageFingerprint` 报 `null` 而不是编一个空指纹:
那不是轮换,是降级 —— 换上去之后,下一次覆盖安装就再也无从比对了。

授权开关也**没有**并进已有的 `allowUntrustedPackage`。两者问的是不同的问题:一个是
"这个包我认不认识",另一个是"它还是不是上次那个人"。合成一个开关,用户为前者点一次
"仍要安装",就顺带把后者也答了。

### 四、命令行装的插件不受影响 —— 这是硬要求,不是副作用

只有**管理页装的**插件钉得住发布者:它的收据是宿主在解包那一刻亲手落的,里面才有公钥。
旁装的(命令行 `vela-plugin install`、或者直接把目录放进插件根)拿到的是 TOFU 基线,
收据里 `PublisherPublicKey` 为 `null`,也没有任何东西能替它补一个 —— 目录先于宿主存在,
没有包可以证明它出自哪儿。

所以钉不住时这一闸**一律闭嘴**,与 `VerifyOrAdoptInstallReceiptAsync` 收养基线是同一条纪律
(§29)。否则的结果是:命令行装过的插件,从此在管理页一个都更新不上 —— 而那是 CLI 手册与
dev-guide 都写明支持的安装路径。用例 `SideLoadedPlugin_PinsNoPublisher_AndIsStillReplaceableFromThePluginManager`
钉的就是这一条。反过来,旁装的插件一旦经管理页装过一次签名包,从那次起它就钉得住发布者了。

### 五、顺手改掉一个顺序错误:先装,再信任发布者

原来的界面流程是「陌生发布者 → 弹框 → **先把公钥加进信任库** → 再安装」。而"这个包正在顶替
一个已装插件的发布者身份"这一问,要等解出包里的 id 才问得出来 —— 于是用户在被告知"换人了"
之前,就已经替这把钥匙签下了"以后它签的包一律直接信任"(那个对话框的措辞是对的,它确实说了
这句话,只是问早了)。

现在改成:先带 `allowUntrusted` 装,装成了再把发布者收进信任库。失败方向也更好 ——
信任库写不进去时,插件装上了但公钥没进全局信任库,朝着"信任更少"的一侧偏。

### 六、界面上看得见

管理页每一行加一条发布者指纹(有钉住的才显示;自带插件、旁装、未签名装的都不显示,
而不是显示一行"未知")。清单里的 `author` 是插件自己写的一行字,谁都能写;这一行是密码学身份。

**指纹给全,截断只发生在显示上**(`TextTrimming` + 悬停出全文):先截成八位再交给界面,
用户拿到的就是一串没法跟作者官方渠道对照的东西,而"看着像能核对"比"明说不能核对"更糟。

### 七、这一条**没有**顺带解决什么

宿主至今**没有信任根**:`velashell-identity` 是 OIDC 账号服务(它回答"你是 sub=xxx"),
不是发布者公钥注册表;市场那边知道每个插件已发布版本的公钥,但宿主没有市场 API 客户端,
装包这条路上问不到它。所以这里做的是**钉住 + 连续性**(TOFU),不是"这把钥匙属于市场认证过的
某某作者"。要做到后者,得先有一处"插件 id ↔ 公钥"的可验证映射,而那是市场侧的事。
`feature-plan.md` 里那一条据此重写,不算划掉。

### 八、验收

`dotnet build VelaShell.slnx` 零警告零错误。新增 4 条用例(`PluginInstallUninstallTests`):
同一发布者的升级不再多问一次、换发布者被拒且旧插件毫发无损、认了之后装上并重钉基线、
未签名包覆盖签名插件同样被拒(指纹报 null),以及旁装插件没有可钉的发布者、
仍能从管理页装上签名包。`TestCategory=Plugins` 155 条全绿;`dotnet test VelaShell.slnx`
3272 通过 / 19 跳过(跳过的是 Docker 与跨平台发布那两组环境门禁)/ 0 失败。

界面文案五份 resx 已补齐(`PluginManager_PublisherChangedTitle` / `_PublisherChangedWarning` /
`_PackageUnsigned` / `_PublisherPinned`)。

⚠️ **文档待同步 velashell-docs**:`{zh,en}/plugins/STATUS.md`(签名验证那一格的口径要改成
"验签 + 发布者连续性已做,信任根未做")、`{zh,en}/cli/cli.md` 与 `{zh,en}/templates/dev-guide.md`
(旁装的代价现在多一条:钉不住发布者)、`{zh,en}/host/交互与界面规格.md`(管理页多了一行指纹,
以及换发布者的确认框)。

---

## ✅ 58. 2026-09-09 SSH config 导入:框架早就备好了那一格,难的是把 OpenSSH 的取值规则照抄对

`feature-plan.md` 把这条标成「全清单里成本最低的一条 —— 在 DI 追加一行即可」。DI 那一行确实是
一行,但那一行背后要有一个**把 `ssh_config` 读对**的解析器,而 `ssh_config` 不是 INI:它没有
"节内取值"这回事,同一个关键字可以在多个块里出现,谁生效由**块的顺序**决定。照 INI 的思路写,
写出来的东西在简单配置上全对,在任何一份带 `Host *` 兜底的真实配置上全错。

### 一、取值规则:先出现者胜,而不是后写的覆盖先写的

OpenSSH 的规则是**首次取得的值获胜**(`ssh_config(5)` 原文:"the first obtained value for
each parameter is used"),所以约定俗成的写法是具名块在前、`Host *` 兜底在后 —— 兜底块只补
前面没写过的键。反过来实现成"后者覆盖前者",结果是每一条会话的 User / Port 都被文件末尾那个
兜底块拿走,而用户完全无从察觉:导进来的会话看着都对,只是全都连错了账号。

`SshConfigParser` 因此保留**完整的块结构**(`SshConfigBlock`:模式列表 + 有序选项),
`ResolveOptions` 按块顺序遍历所有匹配该别名的块,每个关键字 `TryAdd` —— 已经取到的不再被覆盖。
只挑关键字而丢掉块归属,这条规则就做反了。

### 二、`Include` 必须就地展开,不能读完再拼

`Include` 在文件里的位置**决定优先级**:写在开头的 include 里的 `User`,胜过主文件后面的兜底块;
挪到末尾则相反。所以展开发生在解析的那一刻(`Accumulate` 遇到 `Include` 先把当前块收口,
把被包含文件的块插进来,再另起一个同模式的块继续累积),而不是"先收集所有文件再按某个顺序拼"。

相对路径以 `~/.ssh` 为基准(OpenSSH 用户配置的规则),不是配置文件自身所在目录 —— 用户手动指定
别处的配置文件时,里面的 `Include conf.d/*` 仍该指向 `~/.ssh/conf.d`。互相 include 由
"已访问文件集合 + 深度上限 8"两道兜住;末段通配展开后**排序**,否则同一份配置在不同文件系统上
导出的顺序不一样。

### 三、哪些别名算「一台机器」

只导入**字面量别名**。`Host *`、`Host *.internal !secret.internal` 这类块是给别人兜底用的模板,
本身不是可连的机器;它们的选项会通过取值规则渗到具名别名上,这正是 OpenSSH 的语义,
不需要也不应该单独产出一条会话。

`Match` 块**整块跳过**:它的条件(`exec`、`originalhost`、`canonical`)要到真正连接时才有答案。
跳过而不是当成 `Host *`,是因为后者会把只在特定条件下才生效的选项无条件套到每一条会话上 ——
一个 `Match exec "on-vpn"` 里的内网跳板,会就这么长到所有会话头上。

不写 `HostName` 时,**别名本身就是主机名**(`Host build01` 不写 HostName 时 ssh 直接连 build01)。
这条不做,一份"全靠别名 + 全局 User"的配置会一条都导不出来。

### 四、导入框架跟着补了两个字段

`ImportedSession` 加 `PrivateKeyPath` 与 `JumpHostAlias`,`SessionImportWriter` 消费它们 ——
两者都是既有的三个来源共享的能力,而不是 SSH config 专属的旁路:

- **`IdentityFile` → 私钥认证**。写入器原先把 `AuthMethod` 写死成 `Password`(Xshell / WinSCP
  存的确实只有密码),现在有私钥路径就落 `AuthMethod.PrivateKey` + `PrivateKeyPath`。
  路径**不校验存在性**:密钥可能还没从另一台机器拷过来,把路径带进配置比悄悄丢掉有用得多。
- **`ProxyJump` → 跳板引用**。必须是**第二趟**:跳板那条会话可能排在被跳的会话后面导入,
  第一趟走到它时 id 还不存在。多跳的 `ProxyJump a,b` 取**最后一跳** —— 语义是「经 a 到 b、
  再由 b 抵达目标」,离目标最近的是 b,而 VelaShell 的跳板是逐条链式引用,取第一跳会把链接反。
  `user@host:port` 只认 host 段,带方括号的 IPv6 整段取出。

跳板**只在本批次内按别名解析**。拿 config 里的局部别名去撞用户既有会话的名字,会把毫不相干的
两台机器串成一条跳板链;找不到就留直连,用户在连接对话框里补一下即可。config 自己写出环
(a→b→a)时断掉后建立的那一条 —— 留着只会让连接工作流的环检测报错,断成直连是这两条会话里
唯一还能用的形态。

### 五、这里没有密码可还原

另外两个来源有一半的代码在解密码(RC4、0xA3 编码、主密码检测),这里一行都没有:
OpenSSH 的配置文件**从不存密码**。所以 `HasEncryptedPassword` 恒为 false、
`MasterPasswordEnabled` 恒为 false,预览行的三态提示随之多了第四态「使用密钥文件」
(`XImport_PwKey`,五份 resx 已齐)—— 原来那句"无密码"对一条配了 `IdentityFile` 的会话是**误导**,
它不是缺凭据,它的凭据是另一种。属性也从 `PasswordStatus` 改名成 `CredentialStatus`。

### 六、验收

`dotnet build VelaShell.slnx` 零警告零错误。新增 17 条用例(`SshConfigImportTests`):
基础字段、别名兜底为主机名、具名块胜过 `Host *`、通配与取反不产出会话、`Match` 块被忽略、
`Include` 就地展开与互相 include 不死循环、`关键字=值`/引号/整行注释、`IdentityFile` 落私钥认证、
`ProxyJump` 取最后一跳 / 反向声明也能接上 / 接不上留直连 / 成环即断、已存在目标标重复、
文件不存在给空结果,外加一条读**本机真实 `~/.ssh/config`** 的端到端冒烟(没有该文件时
`Assert.Inconclusive`,按 AGENTS.md 的纪律留 `[SKIP]` 痕迹)。

**文档已同步 velashell-docs**:[VelaShellLabs/velashell-docs#26](https://github.com/VelaShellLabs/velashell-docs/pull/26)
—— 新开 `{zh,en}/host/{会话导入,session-import}.md`。原以为该改 `交互与界面规格.md`,
翻过去才发现**会话导入从 2026-07 落地起就一篇文档都没有**,而那份规格是按屏幕区域组织的
(侧边栏、标签行、状态栏……),一扇按需弹出的对话框放不进去。所以补的是整块:三个来源、
对话框行为、`ssh_config` 的解析规则、以及明写**不认哪些关键字**。已挂进两份 host README
与仓库首页快速入口表。

---

## ✅ 59. 2026-09-09 选区对比度再抬一档:暗底 20 / 亮底 16,并且这两个数不该是同一个(用户反馈)

§前一节没有 —— **#405 那次落地压根没在这里留过记录**(合并于同日 07:03,只改了代码与
`src/VelaShell.Terminal/README.md` 的一行 `InternalsVisibleTo`)。所以本节连它一起收口:
`SelectionContrast` 现在的样子,以及这次为什么把阈值抬上去。

### 一、上一版取 14 是「稍低一档」的保守值,实测仍偏弱

#405 把选区从半透明叠加改成不透明绘制,并保证选区底与终端底至少差 **CIE L\* 14**。
那个 14 是照着 VS Code(自家明暗主题分别是 20.8 / 15.9)取的稍低一档。结果是:内置 16 套
配色里**只有 Tokyo Night(原生 20.6)本来就够**,其余全被推到刚好 14 出头就停手 ——
二分搜的是「刚好够」,所以每一套都精确地卡在及格线上。拖选时仍要盯一眼才确认选中了。

现在直接对齐 VS Code:**暗底 20、亮底 16**。

### 二、两个数不同,不是凑出来的

关键在于**推的方向**:暗底上把选区提亮,亮底上把它压深。

而亮色主题的文字是**深色**的。压得越狠,选区底就越贴近文字自身的明度,
`ReadableForeground` 那道守卫被触发得越多,代价是选中一段就把 `ls --color` 的配色抹成兜底色。
暗色主题上推的方向是提亮,离深色文字越推越远,没有这一层张力,因此吃得起更高的一档。
VS Code 自家那两个数(20.8 / 15.9)大概率也是同一个约束的产物。

阈值因此从一个常量拆成 `MinLightnessDeltaOnDark` / `MinLightnessDeltaOnLight`,
由 `MinLightnessDeltaFor(background)` 按**背景**明暗选档 —— 与「往哪个方向推」共用同一个判据。
两者要是各判各的,会出现「往亮里推、却按暗色档收手」。

### 三、16 套内置配色的落点

```
Tokyo Night     #33467C -> 不动     (20.6,唯一原生达标的)
Dracula         #46495B -> #545769  (14.1 → 20.0)
Nord            #4C5465 -> #5B6372  (14.0 → 20.2)
Monokai         #49483E -> #55554B  (14.6 → 20.0)   ← 上一版原样放行,这次要推
Gruvbox D/B     #504945 -> #5A5450  (15.5 → 20.0)   ← 同上
One Dark        #464C58 -> #545A65  (14.3 → 20.2)
Solarized Dark  #214B56 -> #335963  (14.0 → 20.0)
Obsidian        #303039 -> #3D3D46  (14.2 → 20.1)
Everforest Dark #4B565B -> #5A6469  (14.2 → 20.0)
Solarized Light #D4CEBE -> #CEC9B8  (14.1 → 16.0)
One Light       #D2D2D3 -> #CCCCCD  (14.0 → 16.2)
GitHub Light    #C5D9EF -> #C0D3E9  (14.1 → 16.2)
Alucard         #CFCFDE -> #CCCCDB  (15.0 → 16.1)
Sakura          #E9CCD8 -> #E3C7D2  (14.2 → 16.1)
Rosé Pine Dawn  #DDD0C7 -> #D7CBC2  (14.2 → 16.1)
```

### 四、代价:被守卫改色的格子从 30 涨到 41

抬档把选区底推向**中间调**,而 ANSI 颜色恰恰都住在那儿。逐套算过 16×16 = 256 个
ANSI 前景与各自选区底的亮度差,低于 `MinForegroundDelta`(0.18)即被守卫换成兜底前景:

| | 旧 14 | 新 20/16 |
| --- | --- | --- |
| 256 个 ANSI 前景中被改色的 | 30 | **41** |

最差的一套是 **Solarized Dark:4 → 9** —— 它整套 ANSI 本就是低对比中间调,选区底一提亮,
过半的颜色就贴上来了。也就是说在这套配色下选中一段彩色输出,有 9/16 的颜色会被抹成兜底色
(**只在选中期间**,松开即恢复)。

这是抬档的**必然代价**,不是 bug:选区底要离背景更远,就只能往颜色住的地方走。
两害相权取的是「一眼看得出选中了」——那才是用户报上来的故障。真要两头都保,得让守卫改成
「保色相、只调明度」而不是退到纯白/纯黑,那是另一件事,本次**没做**。

### 五、验收

`dotnet build VelaShell.slnx -warnaserror` 零警告零错误。`SelectionContrastTests` 由 21 条增至
22 条(新增 `Fill_UsesASeparateFloorPerBackgroundLightness`,钉住两档取值与「Monokai 不再被
原样放行」——换回单一 14 这条就红);`Fill_LeavesAlreadyVisibleSchemesAlone` 里的 Monokai
断言随之撤掉,只留 Tokyo Night。

⚠️ **像素测试的地板反而要往下调**(18 → 14):`SelectionVisibilityRenderTests` 是屏幕级的
**独立**度量仪,它的数必须低于两档中较低的那一档,否则就退化成把被测代码的算术抄一遍。
而且整定二分停在「刚好够」的那一步、填充色再量化到 8bit,屏幕上量到的总比算出来的低零点几
(这次 16.0 → 15.87,#405 当年 14.0 → 13.9 是同一回事)。地板贴着实现值走,它会因为
一个色阶的舍入而红 —— 第一次改成 18 时正是这么红的。

---

## ✅ 60. 2026-09-09 下拉列表的字为什么在亮色主题下发糊:两条假设被像素实测推翻(用户反馈)

用户报「切换主题后字体变得非常糊」,并指明是下拉列表(主题选择那个 ComboBox)的文字,
且「所有亮色都不行,所有深色看起来都没问题」。

### 一、先说两条被推翻的假设 —— 都是猜的,都被实测打掉

1. **「弹层表面半透明,文字画在非不透明表面上丢了次像素抗锯齿」**。听着合理,
   写了个 headless 探针把 Fluent 的资源读出来:`ComboBoxDropDownBackground` 是
   **完全不透明**的 `#2B2B2B`(亮色变体 `#F2F2F2`)。假设不成立。
2. **「弹层落在半个像素上,所以字形填不满」**。采截图像素时看到 y=130/131 两行都不是底色,
   判成「边框被劈成两行」。**读错了** —— 那是两条不同的线:y=130 是 ComboBox 自己的下边框
   (`#F3D9E3`),y=131 是弹层的上边框(`#E3BECC`),各自都是干净的单像素。弹层没有偏移。

留着这两条,是因为下次再遇到「界面发糊」大概率还会先想到它们。

### 二、真因:11px 的比例字体填不满像素

决定性的一步是**同款对同款**:闭合框里的字与弹层里的字**一样糊**,而它俩一个在弹层外、
一个在弹层内 —— 于是跟弹层无关。逐区采样同一张截图(设置页,Sakura):

| 文字 | 字号 / 字重 | 达到实墨的像素数 |
| --- | --- | --- |
| 页面标题「外观」 | 18 SemiBold | 21 |
| row-label「主题模式」 | 12 Medium | 17 |
| row-desc(等宽 Cascadia) | ~11 Regular | 50 |
| **闭合框「Sakura」** | **11 Regular 比例(Inter)** | **1** |
| **弹层每一条** | **11 Regular 比例(Inter)** | **1** |

11px 的 Inter Regular 笔画只有一个像素出头,**没有任何一个像素被完全覆盖**。
深色主题上半覆盖的亮笔画照样发亮,看不出来;亮色主题上半覆盖的深笔画读作灰 ——
「所有亮色不行、深色没问题」就是这么来的,不是配色对比度不够。

### 三、顺带查出一处真缺陷:下拉是唯一没纳入令牌体系的弹层

同一次探针把 Fluent 的整套下拉资源打了出来,全是**写死值**、不在令牌体系内:
底 `#2B2B2B` / 边框 `Black` / 条目字恒为 `White`·`Black` / 选中底 `#0078D7`
(Windows 经典蓝,不属于任何一套 VelaShell 主题)。

而 `DockStyles.axaml` 里那段注释早就写过同一个病因 —— 当时治了 `FlyoutPresenter`、
`ToolTip`、`ContextMenu`、`MenuFlyoutPresenter`,**唯独漏了 ComboBox 下拉**。
于是九套主题换来换去,那一块岿然不动。

改法是把这十个 Fluent 键别名到令牌上,写在 `ThemeTokenApplier` 的**主题字典那一格**,
而不是 `DockStyles` 的模板部件选择器上:部件名(`Border#PopupBorder` 之类)是 Fluent 的
内部实现、会随 Avalonia 版本改,资源键才是公开契约;写在同一格还保证弹层底与条目文字
出自同一次写入,不会一个新一个旧。

### 四、落地的是字号,不是字重

字号 11 → 12。全仓有 **5 处**把下拉钉死在 11(`SettingsView` / `ConnectionProfileView`
两个样式,`LocalFilePaneView` / `LocalPathPickerDialog` / `TunnelPanelView` 三个内联),
其余走默认 13 —— 这正是用户说「**部分**下拉列表还是不清晰」的那一部分。
**字号会继承进 `ComboBoxItem`**(实测:在 ComboBox 上设 12,容器读出来就是 12),
所以改 ComboBox 这一处即同时管住闭合态与下拉列表。

⚠️ **中途试过压字重(Regular → Medium → SemiBold),最终由用户撤下**,本次未合入。
过程中查清两件仍然有用的事,记在这里免得重查:Avalonia 的 Inter 包内置
Thin / Light / Regular / Medium / SemiBold / Bold **六个真实静态字面**(不是合成加粗);
而回退路径 Segoe UI 上 `Medium`(500) 会被吸附回 `Normal`(400),`SemiBold`(600) 才落到
真实的 "Segoe UI Semibold" —— 没装 Inter 的环境下 Medium 是个空操作。

### 五、验收

`dotnet build VelaShell.slnx -warnaserror` 零警告零错误;`dotnet test VelaShell.slnx`
3301 通过 / 19 跳过 / 0 失败。`ThemeTokenApplierTests` 新增
`EveryTheme_OverridesFluentComboBoxPopupBrushes`:逐主题核对那十个 Fluent 键都被顶掉,
并钉住「弹层底 = `VelaBgSurface`、条目字 = `VelaTextPrimary`」——
它们一个跟主题走、另一个不跟,正是这次要消掉的形态。

⚠️ 固定高度的两个下拉(`LocalFilePaneView` / `LocalPathPickerDialog`,`Height=22`)
字号上调后未做像素复核,是本次唯一没验到的地方。

## ✅ 61. 2026-09-09 回滚行数这个设置项存下来了,只是有两处根本不生效(用户反馈)

用户要求「把 scrollback 行数可配做全」,并指出「目前应该是已经有配置的地方了」—— 说对了。
`feature-plan.md` A 组那一条写的是「现在硬编码 10 000,只差一个设置项 + 接线」,**这句是错的**:
`AppSettings.ScrollbackLines`(默认 10 000、`ClampNumbers` 钳在 100..200 000)、
设置页的 `NumericUpDown`、五种语言的资源串、`SettingsViewModel` 的读写、
`TerminalSettingsApplier.Apply` 的下发,整条线一年前就通了。

**缺的不是开关,是开关拨下去之后的那一段。** 又一例「复核结论本身会过期」——
路线图那一条 09-08 才写,写的时候没核到设置页。

### 一、洞一:调小不当场裁,内存不还

`TerminalScreen.MaxScrollback` 是个自动属性,而裁剪只发生在 `TrimScrollbackToMax()` 里,
它的调用点只有两处:`ScrollUp`(滚动时)与 `Resize` 的 reflow。于是把 200 000 调到 1 000,
那 199 000 行**要等下一次滚动才退休**。

问题在于用户调小它几乎只有一个动机 —— 收回内存(200 列 × 20 万行 × 16 B ≈ 640 MB / 标签页,
见 `TerminalRow` 的类型注释)。而最该收的恰恰是**跑完就停在那儿的标签页**:
一个刷完日志、此后再无输出的会话,「下一次滚动」可能永远不来。

改法:`MaxScrollback` 改成带 setter 的属性,钳到非负后当场 `TrimScrollbackToMax()`。
构造函数原本自己做的 `Math.Max(0, ...)` 收进 setter,只留一处。

### 二、洞二:在 vim 里保存设置,值落到了备用屏上

`VelaTerminalControl.ScrollbackLines` 原先读写的是 `Emulator.Screen.MaxScrollback` ——
**`Screen` 是「当前」缓冲区**。用户正开着 vim / htop / less(备用屏活动)时按下保存,
`TerminalSettingsApplier` 对每个标签重设一遍,于是:

1. **主屏一个字没改** —— 用户以为生效了,退出 vim 才发现没有。这正是 P0 那张表里
   「界面在骗人」的形态,只不过它不是「零消费者」,而是「消费者接错了地方」。
2. **备用屏被弄脏** —— 它的容量本该恒为 0(`SetAlternateScreen` 里 `new(cols, rows, 0)`)。
   一旦有了容量,`ScrollUp` 的 `if (fullScreen && MaxScrollback > 0)` 会把 vim 的退休行压进历史
   (在 vim 里能往回滚出「历史」,是错的),而 `Resize` 的 `if (columns != Columns && MaxScrollback > 0)`
   还会给备用屏做一次**它本不该做的 reflow** —— 那行注释写得很清楚:
   备用屏靠 SIGWINCH 自行重绘,重排只会和应用打架。

改法:把这个属性上提到引擎,`TerminalEmulator.ScrollbackLines` **恒读写 `_mainScreen`**,
与此刻在哪个屏无关;控件转发过去。读回来也一并修好了 —— 原先在 vim 里打开设置页,
显示的是备用屏的 0。

### 三、裁剪要连带收的三样

裁剪把绝对行号整体前移,所以控件那一侧在值真的裁到了东西时还得收口:

- **滚动位置**:可能正停在一行已经不存在的历史上,`_scrollOffset` 与 `_lastScrollbackCount`
  一起收回新量程(后者不同步的话,下一次 `ApplyOutputUpdate` 的 `PinScrollOffset` 会按一个
  虚构的增量再挪一次)。
- **折叠**:折叠头可能刚被裁掉 —— 走既有的 `ClearFolds()`。
- **选区**:按绝对行寻址,留着只会复制到错的文本。与 resize 同一条纪律(见 §12-13 那句
  「与其让陈旧的范围标记错误的文本,不如直接丢弃它」)。

只在 `ScrollbackCount` 真的变了才做这一套:每次保存设置都会对所有标签重设一遍,
调大或没到上限时不该白惊动滚动条的订阅者。

### 四、验收

`dotnet build VelaShell.slnx -warnaserror` 零警告零错误;`dotnet test VelaShell.slnx`
**3310 通过 / 19 跳过 / 0 失败**。新增 `ScrollbackCapacityTests`(6 条):调小当场裁、
调大不丢已有历史、负值兜底钳零(插件能力面 `PluginTerminalViewApi` 也能写这个属性)、
备用屏上设置落到主屏且备用屏保持零回滚、备用屏上读回来的是主屏的值、裁剪后滚动位置回到量程内。

⚠️ **本机缺 `global.json` 钉的 SDK**(要求 `11.0.100-rc.1.26425.128`,机器上只有
`11.0.100-preview.7` 与 `10.0.400`),上面两条命令是在仓库外的目录里跑的 —— 那样
global.json 不参与解析,落到 preview.7。**与 CI 的 SDK 不是同一个**,结论按此打折看。

## ✅ 62. 2026-09-09 #414 的 CI 两处红:一处是用不到的 apt 源,一处是预热等超时后撒了手

[#414](https://github.com/joesdu/VelaShell/pull/414) 的
[run 34384262772](https://github.com/joesdu/VelaShell/actions/runs/34384262772) 三平台挂了两个,
而**两处都与那一版的改动(§61 回滚行数)无关** —— macOS 全绿,挂掉的两处一处在装依赖、
一处在数据库预热,都碰不到终端缓冲区。但红着的门禁就是拦着的门禁,得各自钉死。

### 一、ubuntu:一个我们不用的源没取下来,`apt-get update` 整体退 100

```
E: Failed to fetch https://dl.google.com/linux/chrome-stable/deb/dists/stable/main/binary-amd64/Packages.gz  Hash Sum mismatch
E: Some index files failed to download. They have been ignored, or old ones used instead.
##[error]Process completed with exit code 100
```

作业在**「安装虚拟显示」那一步就断了**,编译一行都没跑到(14 秒结束)。取不下来的是 runner
镜像自带的 Chrome 源,而这一步要的五个包(`xvfb libx11-6 libice6 libsm6 libfontconfig1`)
全在 Ubuntu 主仓 —— 一个用不到的第三方源抽风,把整条代码门禁拦下了。

`apt-get update` 的语义就是如此:任何一个源没取下来都算整体失败。而这一步的注释里本来就写着
「runner 镜像通常已带这些」—— 那就先看缺不缺:

- **一个都不缺时连 apt 都不碰**。这是绝大多数次的情况,索引根本不必更新,也就没有第三方源
  什么事;
- 真缺包时才 `apt-get update`,且它失败只记一行 `::warning::` ——
  **判据落到 install 上**:包真装不上,`apt-get install` 会如实失败。原注释里
  「列全是为了镜像换代时不至于悄悄少一个」那句话要的正是这个,而它从来不需要索引全绿。

### 二、windows:预热的库等不到,`Discard` 撒手,用例撞上还占着的 WAL

`StartupWarmupTests.AWarmupForAnotherRootIsClosedInsteadOfHandedOver` 跑了 13 秒后:

```
System.IO.IOException: The process cannot access the file
'…\vela-warmup-other-e7eee7bf…\sonnetdb\wal\0000000000000001.SDBWAL'
because it is being used by another process.
```

同一份报告里那行 Debug Trace 把根因说完了:

```
[VelaShell] Timed out waiting for the warmed database engine to open; leaving it to the process exit.
```

`StartupWarmup.Discard` 的 10 秒等到头了。链条是:

1. 用例先 `Begin(other)` 预热另一个根,再 `Claim(_root)`;
2. 根目录对不上,`Claim` 调 `Discard` 去关掉预热那一个 —— 但后台那次开库排在**线程池**上,
   10 秒没跑完;
3. `Discard` 打完那行日志就返回,**引擎留着不管**;
4. 用例紧接着去开 `other`,撞上还被占着的 WAL。

**为什么排这么久**:与 §50 同一个根因 —— `dotnet test VelaShell.slnx` **并行跑八个测试程序集**,
而 runner 只有三四个核;线程池饱和后注入新线程约每 500 ms 才多一条,一个 `Task.Run` 排多久
不是用例能控制的。本机(32 核)这一类六条 1 秒跑完,`DOTNET_PROCESSOR_COUNT=1` 把整个
Infrastructure 程序集跑完也全绿 —— **单机复现不出来,恰恰说明它要的是多进程抢核**。

#### 「超时就撒手」不只是用例脆,那是产品侧的缺陷

原注释写的是「宁可漏一个句柄也不能让应用停在这儿不动」。后半句对,前半句在 `Claim` 这条路上
把代价估低了:进程**才刚起来**,接下来要跑几个钟头 —— 这几个钟头里,另一个库的 WAL 一直被
一个没人要的引擎占着。用户把 `--data-root` 换回去、或另开一个实例指向那个目录,撞上的就是
「数据库被占用」。**那正是这个类存在的理由,只不过这回是它自己造出来的。**

而「不能让应用停在这儿不动」并不需要靠撒手来换:超时之后挂一个续延,开库真跑完的那一刻把它
关掉。等待照样有上限,句柄却不再漏。

- 超时分支改成 `pending.ContinueWith(CloseWhenOpened, TaskScheduler.Default)`;
- **排到线程池上,不用 `ExecuteSynchronously`**:后者在挂续延时任务恰好刚完成的话会就地跑在
  调用方那条线程上 —— 把上面刚刚拒绝掉的那次等待又变回一次等待;
- 预热本身就失败(多半是库被占用)时没有句柄要收,异常观察掉即可 —— 真正的报错仍由 `Claim`
  那次就地新建抛出,`Program.Main` 的 `IsDatabaseLockedFailure` 才接得住。

#### 用例改成等条件,不赌时长

`OpenOnceReleased` 轮询到能打开为止,上限 25 秒(在 `StartupWarmup` 那 10 秒之上留够余量,
又不越过 runsettings 里 60 秒的 `TestTimeout`)。要测的是「会不会放开」,不是「多快放开」;
真没放开时照样失败,抛出的仍是原来那句「文件正被另一个进程使用」。
`AnUnclaimedWarmupReleasesTheDatabaseAgain` 押的是一模一样的注,只是这次没轮到它,一并改掉。

### 三、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;
`dotnet test VelaShell.slnx`(按 CI 那条过滤,并指上 `VELASHELL_DOCS_DIR`)
**3309 通过 / 5 跳过 / 0 失败**;`StartupWarmupTests` 六条全过。
**这次是 `global.json` 钉的那个 SDK**(`11.0.100-rc.1.26425.128` 已装上),
不再有 §61 结尾那条打折说明。

ubuntu 那一步没法在本机验证(Git Bash 里没有 `dpkg-query`):`bash -n` 过了语法,
`js-yaml` 解析出来的 `run` 与写下的一字不差,逻辑的判据留在 CI 上。

## ✅ 63. 2026-09-10 SSH 证书认证:上游早就备好了,缺的只是这一路接线

连接对话框第 2 步那个「证书」按钮一直禁用着写「暂未支持」,而 `src/` 下搜 `Certificate`
命中的全是 FTPS/TLS 那一套 —— 看上去像在等上游。**先查上游,结论是不必等**:

```
T:Tmds.Ssh.CertificateCredential  →  "Credential for certificate-based authentication."
M:...CertificateCredential.#ctor(System.String, Tmds.Ssh.PrivateKeyCredential)
     path      →  "Path to the certificate file."
     privateKey→  "PrivateKeyCredential matching the certificate."
```

程序集里 8 个 OpenSSH 证书算法一个不缺(`ssh-rsa` / `rsa-sha2-256` / `rsa-sha2-512` /
`ecdsa-*-nistp256|384|521` / `ssh-ed25519`,各带 `-cert-v01@openssh.com`),
而且**这套 API 在 0.23 就有了,我们锁的 0.24 早就够用,包版本一行没动**。
「代码零踪迹」的原因不是上游没有,是这边压根没接:`AddCredential` 的 switch 只写了
密码与私钥两路,`default` 直接抛 `ArgumentOutOfRangeException`。那个禁用的按钮是在
如实反映现状,不是在等谁。

### 一、证书页不另起一套表单,它就是密钥页再加一个字段

`CertificateCredential` **只有 `(证书路径, PrivateKeyCredential)` 这一个构造函数** ——
这条约束直接决定了界面长什么样:证书认证不是"选一个文件",而是证书 + 私钥 + 口令三件套。
因为**签名始终由私钥出,证书只是 CA 的背书**。

于是这一路整个复用私钥那一路,连 `BuildPrivateKeyCredential` 里那套 PEM→OpenSSH 兼容转换
(§见 `OpenSshPrivateKey`)一起继承;界面上两块面板也合成一块,由 `ShowsPrivateKeyFields`
控制 —— 校验、浏览按钮、错误提示因此都只有一份,不会两边各改各的。

`BuildPrivateKeyCredential` 的返回类型顺势从 `Credential` 收窄到 `PrivateKeyCredential`:
不是为了好看,是那个构造函数只收这个类型。

**选完证书自动补私钥**:ssh-keygen 产出的两个文件永远同目录、名字只差一个 `-cert.pub` 后缀。
不自动补的话,用户要在两个文件选择器里把同一个目录翻两遍,还容易挑到隔壁那把不匹配的私钥 ——
**而那种错配到连接时只会得到一句笼统的 `publickey` 被拒**,从错误信息里根本看不出是选错了文件。
推断逻辑落在 `Core/Ssh/OpenSshCertificate` 而不是各写进两个对话框:重复一份的代价是将来改了
后缀只改好其中一处。

### 二、同一个空字符串,两种认证下的含义是相反的

`PrivateKeyPathError` 原先只有一条判断,现在必须分开:

- **私钥认证留空是合法的**,含义是「用默认密钥」—— 这是原注释里就写明的;
- **证书认证留空是硬错**。证书自己签不了名,少了私钥连不上。

证书路径则是空着也算错:私钥还能退回默认密钥,**证书没有任何默认位置可退**。

### 三、顺带修掉一处漏网:切到 FTP 之后,证书页再也切不回来

`NormalizeAuthMethodForProtocol` 只判 `IsKeyAuth`。它上面那段注释把病症写得很清楚 ——
切到 FTP/插件协议后认证方式下拉恰好是隐藏的,**界面上再没有任何途径把它切回来,
保存下去的却仍是那个用不上的认证方式**。证书认证掉进的是同一个坑,而且比私钥那页更难自救:
它还多一个同样切不回来的证书字段。

判据改成 `ShowsPrivateKeyFields`。`SwitchingToFtp_FallsBackFromCertificateAuth` 守着它 ——
**把修复临时退回验证过,用例确实变红**,不是空转。

### 四、枚举序号那道闸

`AuthMethod` 没挂 `JsonStringEnumConverter`(同仓的 `QuickCommandGroupKind` 挂了),
落盘的是**序号**而不是名字。往中间插一个值,已存档配置里的 `1` 就会从「私钥」变成别的东西 ——
用户的连接静默改用另一套凭据,而配置文件看上去毫无变化,几乎无从排查。所以 `Certificate`
只能加在末尾,并由 `AuthMethod_OrdinalValues_MustStayStable` 钉住。

那条用例**盯的是落盘产物**(`"authMethod": 0/1/2`)而不是 `(int)AuthMethod.X`:后者是编译期常量,
分析器(MSTEST0032)会判成恒真的废断言,而真正要守住的本来也就是存档里的那个数字。

### 五、真机验证:靶机不设阴性对照,"通过"就是假的

这一步最容易做错,也最值得写下来。**只要服务端还留着任何一条回退路径,"连上了"就不能证明
证书生效** —— 走的可能是普通公钥或密码,而你以为验的是证书。这类假阳性长得和成功一模一样。

`tests/cert-lab/` 那台靶机因此把每条路都堵死:

```
trustedusercakeys /etc/ssh/velashell_user_ca.pub
authorizedkeysfile none          ← 堵死普通公钥
passwordauthentication no        ← 堵死密码
gssapiauthentication no
kbdinteractiveauthentication no
```

其中 `authorizedkeysfile none` 尤其要紧:基础镜像(Tmds.Ssh 测试套件留下的 `test_sshserver`)
**给 testuser 预置了 authorized_keys**,不删掉的话阴性对照根本立不住。

服务端日志是最终的判据 —— 注意同一个指纹,带 `-CERT` 的被接受、不带的被拒:

```
Accepted certificate ID "velashell-lab" (serial 0) signed by ED25519 CA SHA256:uDQwg/PF…
Accepted publickey for testuser … ED25519-CERT SHA256:ieiyD4L3… ID velashell-lab CA …
Failed   publickey for testuser … ED25519      SHA256:ieiyD4L3…
```

靶机资产由 `setup.sh` 就地生成、`.gitignore` 挡住 —— **一把能登录的私钥不该躺在版本历史里,
哪怕只是测试用的**。基础镜像会自动探测:拉不到公共仓库时(国内网络的常态)优先用本机已有的
`test_sshserver`。集成测试标了 `DockerIntegration`,CI 那条过滤本就排除它;
没有靶机时按 §62 立下的口径 `Assert.Inconclusive`,如实记为「未执行」而不是安静地记成通过。

### 六、范围之外

`SessionImportWriter` 与 `ExternalLaunchCoordinator` **没有动**:`ImportedSession` 没有证书字段,
PuTTY/Xshell 那几个解析器不产出证书,`ssh://` URL 同理 —— 输入端本来就没有这个概念,
在那里加分支只是死代码。要支持得先扩解析器,那是另一件事。

主机证书(`HostKey.CertificateInfo` / `ServerHostKeyCertificateAlgorithms`,即用 CA 签的
host key 替代 known_hosts 逐台指纹)是**相反方向**的另一件事,接得上现有的 `AddHostAuthentication`,
但不在这一版里。

### 七、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;
`dotnet test VelaShell.slnx`(按 CI 那条过滤)**3325 通过 / 5 跳过 / 0 失败**
—— 比 §62 的 3309 多出的 16 条正是这一版加的。

端到端另算:靶机起着时 `VELASHELL_CERT_LAB` 一指,
`SshCertificateIntegrationTests` 两条(阳性 + 阴性对照)全过,
服务端日志里 `Accepted certificate` 与 `Failed publickey` 一并留痕。

---

## ✅ 64. 2026-09-10 VelaDock:关掉左半屏,右半屏该铺满(用户反馈)

> 「拖动分屏后,关闭掉所有左侧的窗口,右侧的窗口也还是保持在右侧,没有自动填充满整个区域。
> 并且检查当前实现和操作上是否存在其他优化的点。」

### 一、不该折叠的是「根」,不是「主组」

复现只要三步:开两个标签 → 把其中一个拖到右缘分屏 → 把左半屏的标签全关掉。左边留下一块
空白,右边的窗格纹丝不动 —— 而那块空白正是 `PrimaryGroup`。

根因在 `CollapseIfEmpty` 的第一行:

```csharp
if (group.IsPrimary || group.Documents.Count > 0 || group.Parent is not { } parent)
```

`IsPrimary ||` 这一条是**在保护一个不需要保护的东西**。真正的约束只有一个:布局树得有个底,
新文档才有地方落。而"底"是**根**,不是某个特定的组 —— 后半句的 `group.Parent is not { } parent`
本来就把根兜住了(根节点没有父分栏)。多出来的那一判,把"永不折叠"从根钉到了一个具体实例上,
于是它一旦被拖到侧边、又被关空,就成了一块谁也赶不走的空白。

改法不是"给主组开个例外",是**让这个身份可以易主**:`TryHandOverPrimary` 把兜底的差事交给
折叠之后**会接管这块地方**的那个邻居(沿父分栏找兄弟,再向下钻到具体的组),然后自己退场。
新文档于是出现在用户眼睛刚才盯着的位置,而不是布局树另一头。`DockGroup.IsPrimary` 因此从
`init` 放宽成 `internal set`,`DockWorkspace.PrimaryGroup` 从只读变成 `private set`。

同因异形的还有一处,一并好了:**把主组最后一个标签拖进邻居**(走 `MoveToGroup` → 同一个
`CollapseIfEmpty`),此前同样会留下一块空白。

`HoistIfSingle` 里那句 `IsPrimary: false` 的特判也跟着删掉:折叠的判据统一收在
`CollapseIfEmpty` 里一处,提升到根之后它自然没有父分栏、自然留下。

**测试口径要跟着改口**:`PrimaryGroup_NeverCollapses` 这条用例把这个 bug 逐字钉成了规范
(它断言的正是"主组即使为空也保留"),留着它就等于让测试替 bug 站岗。换成三条:
关光主组要铺满、拖空主组也要铺满、以及**唯一的那个组即使空着也留下**(根的约束还在)。

### 二、顺着这条线:新标签到底该开在哪一格

原来的规则是"永远进主组"。分屏之后它的实际表现是:人在右半屏工作,`Ctrl+T` 却在左半屏的
标签条上开出一个新标签 —— 焦点跟着跑过去,视线还留在原处。

新的落点规则(`TargetGroupForNewDocument`)是三级:**空窗格 > 当前正在用的窗格 > 主组**。

- 空窗格排第一,是因为拆分只有一个标签的组会**故意**在原地留下一块写着"拖放标签到这里"的
  空面板(这是既有设计)。那块空白就是等着被填的,新会话理应落进去。这一条也保证了老流程
  ——「拆分,然后开一个新会话把另一半填上」—— 一如既往。
- 没有空窗格时才轮到活动窗格,与 VS Code / Windows Terminal 一致。
- 主组降级为纯兜底:没有空窗格、也没有活动文档(刚启动的空布局)时用它。

### 三、空窗格此前没有出口

拆分留下的空面板,在这一版之前**没有任何撤销入口**:唯一的办法是把邻居的标签拖进来、再拖
回去,靠副作用把它挤掉。现在空面板上直接放一枚「关闭窗格」,走新的 `ClosePane(group)`;
非空的窗格它也接(逐一走确认闸),于是这是关闭闸的**第七个入口** ——
`DockCloseInterceptorTests` 里补了一条守着它,免得日后成为那道闸的后门。

顺带,「关闭所有标签页」现在会让该窗格一并收掉(第一节的直接结果),不再留下空壳。

### 四、窗格最大化(tmux 的 `resize-pane -Z`)

分屏之后想把某一格看仔细,原先只能"先拆掉布局,看完再照原样拼回去"——那两步手工活正是
分屏用起来累的地方。`MaximizedGroup` **只影响渲染,不动布局树**:解除时原样恢复,连比例都
不用记,因此也不需要参与将来的布局持久化。

三条自我解除的规矩,都是为了不留下"解不掉的状态":

- 焦点切到别的窗格就解除(与 tmux 的 `select-pane` 同一条规矩)—— 否则会出现"焦点已经在
  别处、屏幕上却还是那一格"的呆滞界面;
- 被最大化的窗格离开布局树(关空了)就解除;
- 布局收敛回一格就解除。

入口:`Ctrl+Shift+X`(Terminator 同款键位)、命令面板、四处标签右键菜单。

⚠️ 这一条真正的风险不在模型而在控件:内容视图是**按文档缓存、跨宿主收养**的,而最大化会
把整棵可视树换成一个窗格再换回来 —— 正是 `DockContentBlankHuntTests` 记的那次"终端内容
凭空消失"的同款场景。`DockPaneUiTests` 因此盯的是**还原之后 `ReparentingHost.Target` 是不是
同一个实例**,而不只是"控件数量对不对"。

### 五、三个一直缺席的鼠标手势

- **中键关标签**:浏览器与各家编辑器的通用手势,省掉"先瞄准那枚 11px 的 ×"。挂在
  `DockTabItemBase` 上,五种标签一次到位;同样走 `RequestClose`,确认闸对它照旧生效。
- **标签条滚轮**:`ScrollViewer` 默认把滚轮当纵向滚动,而标签条是横排的 —— 于是"在标签条上
  滚一下"此前**什么也不会发生**,标签一多就只能去够右端那两枚小箭头。隧道阶段接管,按标签
  排列方向翻;没有溢出时不吞事件。
- **分割条双击 = 平分**:拖歪一条缝之后原先只能靠手再瞄一次。另有命令面板的「平分窗格」
  一次性复位整棵树。

外加一处纯粹的遗漏:`DockDragController` 一直在给拖动中的标签挂 `.dragging` 类,而**全仓没有
任何一条样式认这个类** —— 拖拽过程中标签本身零反馈,"我手上拿着的到底是哪一个"全靠脑补。
补了半透明 + 移动光标。

### 六、分屏时看不出焦点在哪一格

每个标签组各自画着一条 accent 强调线,两格并排时两条线一样亮,**唯一的线索是"我刚才点了
哪儿"**,而那正是回头看屏幕时最先忘掉的事。加 `:activepane` 伪类,非活动格的强调线弱到 0.3。

两处细节:

- **刻意不给这条线的 `Opacity` 加过渡**。它是"我的键盘输入落在哪"的指示,该在点下去那一刻
  就成立;淡入淡出除了让指示晚 180ms,还会把它变成一个只能靠时序观测的状态 ——
  界面测试正是先在这里卡住的,那说明人眼也会卡在同一处。
- 把**当前激活的标签**整个拖进隔壁窗格时,`ActiveDocument` 的**值没有变**,
  `ActiveDocumentChanged` 因此不会响,可活动窗格已经易主。所以 `Documents.CollectionChanged`
  上也补了一次刷新,`DockPaneUiTests` 里单独有一步盯着这个场景。

### 七、返工:两种「空」是两件事(同批实测反馈)

上面第三节那枚「关闭窗格」按钮,第一版无差别地挂在**任何**空窗格上。实测截图打回来的是
一个会话都没开的主界面:正中间一句「将标签拖到此处」,底下一枚「关闭窗格」——

- 那枚按钮**点了不会有任何反应**:`ClosePane` 对根窗格是空操作(根没有父分栏,
  `CollapseIfEmpty` 直接早退)。一枚点下去毫无动静的按钮,比没有按钮更糟;
- 那句提示说的是一件**当下做不到的事**:一个标签都不存在,拖什么?用户还把它读成了
  "拖点什么进来就能打开连接",而那个功能并不存在 —— 文案自己招来了这个误会。

判据是**有没有父分栏**,不是"是不是主组"(与第一节同一个教训:别把结构性质钉到实例身份上)。
空状态因此分两套:能撤掉的空窗格给「把其他窗格的标签页拖到这里」+ 关闭按钮;
整个工作区空了则只说现状与下一步 —— 「还没有打开任何会话 / 从资源管理器里选一台机器,
或者新建一个连接」。

`Dock_DropTabHere` 的五份译文一并改写成「把**其他窗格的标签页**拖到这里」,把"拖的是已经
存在的标签页"说死。两句新文案不写具体键位 —— 快捷键的唯一事实来源是 `ShortcutCatalog`,
在 resx 里再抄一份,改键位那天它就开始骗人。

⚠️ 两套提示都**留在 XAML 里靠 `IsVisible` 切换**,不从代码写 `Text`:`{loc:Localize}` 给的是
活绑定,换语言即时刷新;代码赋值会把这两句钉死在赋值那一刻的语言上。

### 八、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;
`dotnet test VelaShell.slnx`(整解全量,不带 CI 那条过滤)**3343 通过 / 21 跳过 / 0 失败**,
其中 17 条是这一版的净增量(新增 18、删掉 1 条替 bug 站岗的)。
新增用例:模型层 10 条(`DockWorkspaceTests` 折叠/落点/关窗格/最大化/平分)+ 关闭闸 1 条 +
界面层 6 条(`DockPaneUiTests`:最大化往返不丢内容、活动窗格标记与弱化、空窗格的出口、
空工作区不给死按钮、标签条滚轮、中键关标签)。

`ShortcutCatalog` 与 `velashell-docs` 的 `快捷键参考.md` / `keyboard-shortcuts.md` 同步补齐
`Ctrl+Shift+X` 与三条鼠标手势(`Doc_ListsEveryCatalogEntry` 实跑通过,不是 Inconclusive);
`dock-replacement-plan.md` 中英两份把"主组永不消失""新终端总进第一组"这两句**过期的规范**
改掉并注明修正日期 —— 它们正是第一节那个 bug 的书面出处。界面文案五份 resx 各补 11 个键、
改写 1 个键。

---

## ✅ 65. 2026-09-10 防空闲断开:保活防的是链路,踢你的是 shell(用户需求)

> 「SSH keepalive 已接(`KeepAliveSeconds` → `SshClientSettings.KeepAliveInterval`),但那是
> **协议层**防 NAT 超时。anti-idle 防的是**服务端 shell 超时踢出**,需要按间隔向 PTY 输入流
> 发字节(如 `\0` 或空格)。两者互补,不能互相替代。」
> 追加:「是否可以新增个开关放在会话设置中?」「放在会话的高级设置中,让其能针对不同会话单独设置。」

### 一、两个"秒"防的不是同一件事

`feature-plan.md` 里这一条挂了很久,它真正难的地方不在实现而在**说清楚为什么保活不够**:

| | 保活(既有) | 防空闲(本节) |
| --- | --- | --- |
| 发什么 | SSH 协议的心跳包 | 一个 `NUL` 字节,走 PTY 的输入流 |
| 谁看得见 | 对端的 **sshd** | 对端的 **shell / tty** |
| 防谁 | NAT、防火墙回收闲置 TCP | `TMOUT`、堡垒机会话超时、登录会话空闲策略 |

踢人的那几个规则一律按"tty 上有没有输入"计时,而心跳包压根不经过 tty ——
所以"配了保活还是被踢"不是保活没生效,是它本来就管不着这件事。两条路必须各走各的。

### 二、为什么是 NUL,不是空格

空格会被 shell 当成用户输入留在命令行上,也会被全屏程序(vim / less / htop)当成按键吃掉:
人离开一小时回来,命令行前面多了三十个空格,或者 less 已经翻到文件末尾。
`NUL` 在行规范里被丢弃,却照样刷新 tty 的读活动 —— **有输入,但什么也没发生**,
正是这里要的语义。

### 三、只按会话,不设全局

按用户的要求放进连接对话框的高级选项(会话级终端项那一组,与保活并排),
**不加全局开关**。理由不是省事:会按空闲踢人的只是特定那几台机器(堡垒机、跳板机),
而注入的字节终究是打进对端 tty 的 —— 给每条会话都撒一份,等于让其余那些平白多担一份风险。
因此 `TerminalOverrides.AntiIdleSeconds` 的 `null` 含义与同组其它字段不同:
**不是"跟随全局",是"没开"**,`SessionTerminalSettings.AntiIdleSeconds(profile)` 也就只收
一个 `profile` 参数。界面上 `0` = 关闭,存回配置时归一成 `null` ——
免得给每条从没碰过这一项的配置留下一段"我设过、设的是关"的假痕迹。

### 四、只在真的空闲时才发

`AntiIdleKeeper`(`src/VelaShell.Terminal/`)记的是**最后一次出站写**的时刻,不是上一次注入的时刻:
定时器醒来发现距上次击键还不够久,就把闹钟改到刚好满一个间隔的那一刻再睡回去。
于是用户正在打字的那段时间里,一个多余的字节都不会有。

记账挂在桥的 `EnqueueOutbound` 上 —— 击键、`SendRaw` 注入、防空闲自己发的那一下,
三条出站路径合并成一个入口。漏掉击键那一路,注入就会在用户打字时插话;漏掉自己那一路,
它又会在刚发完之后立刻再发一发。

两处必须拦住的时刻:

- **ZMODEM 会话进行中**不发。那条流上跑的是协议帧,插一个字节轻则 CRC 错重传,重则整笔失败
  ——与击键在传输期间被拦下是同一个理由。传输本身就是流量,服务端此刻也不会认为会话空闲。
  拦下的那一发不作废:每秒回头看一眼,能发了立刻补(等满一整轮就可能迟到)。
- **桥释放时**第一个停表。它在线程池上跑,晚一步就可能往一条正在拆的流上再送一发。

### 五、接线落在挂载传输那一步

`TerminalTabViewModel.AttachTransport` 里按 `Profile` 设一次 —— 首连与**重连**因此都覆盖到。
重连会新建一条桥,漏掉这一步的表现是"断线重连之后又开始被踢",而没人会把这件事和重连联系起来。
改了配置要等下次连接才生效,与同一栏里的保活一个口径。
本地终端没有会话配置,`AntiIdleSeconds(null)` 返回 0,那条路上一个字节都不会发。

### 六、验收

`dotnet build VelaShell.slnx` 零警告零错误。新增用例 15 条:

- `AntiIdleTests`(9):默认关、满一个间隔才注入且注入的是 `NUL`、间隔内敲过键就顺延、
  自己发的也算活动、发不出去时顺延而不丢、置零即停、释放后不再发,
  外加两条走桥的接线用例(默认不发、配了间隔真的把字节写到流上)。
  时钟是手拨的,不靠睡真实的一分钟。
- `SessionTerminalSettingsTests`(2)、`ConnectionProfileViewModelTests`(2)、
  `TerminalTabViewModelTests`(2):没有全局值可跟随、上限与保活同为 3600、
  对话框往返回显、清零存回 `null`、重连也带过去、没有配置的标签恒为关。

界面文案五份 resx 各补 2 个键(`Profile_AntiIdle` / `Profile_AntiIdleHint`)。

📄 **velashell-docs 待同步**:`zh/host/` 与 `en/` 两棵树里,设置项审计与交互界面规格
需要补这一项 —— 尤其是它与保活的分工,那正是这条特性存在的全部理由。

---

## ✅ 66. 2026-09-10 静默注入不该留在命令历史里:屏幕上隐形,方向键一按却整行现形(用户反馈)

> 「开启了 OSC7 后,会在服务器的历史记录中留下这样的信息,是否能不留信息的执行该命令?
> 不然每次使用方向键选择历史命令的时候,都会对用户造成误解,以为注入了什么不得了的东西。」

### 一、前导空格从来就没生效过

注入一直带着一个前导空格,注释里写的是「让 <c>HISTCONTROL=ignoreboth</c> 不记历史」。
问题在于 **`HISTCONTROL` 默认是空的** —— 绝大多数机器上那个空格什么也没做。于是:

- 屏幕上这一行是隐形的(回显被 `SuppressEchoOnce` 剥掉了);
- 历史里它整行都在,按一下 ↑ 就是 `test -n "${BASH_VERSION:-}" && eval '…'`。

用户看到的不是"终端做了点内务",是"谁往我服务器上注了东西"。**屏幕上隐形、历史里现形,
这两件事本身就是自相矛盾的** —— 所谓"静默"当然该包括后者。

空格没删:配了 `ignorespace` 的人本就该受益。但它不能当防线。

### 二、摘法:bash 自己把这一条从历史里删掉

bash 在**读到**这一行时就已经把它记进历史,所以命令跑起来的时候"最后一条"正是自己,
`history -d` 删掉即可。三个决定:

**接在前面,不是缀在后面。** 缀在末尾会把这一行的 `$?` 抹成收尾动作的 0,而
starship / powerlevel10k 会把上一条命令的退出码画在提示符上 —— 一条失败的
「认证后执行命令」于是变得毫无痕迹。前置则退出码仍由用户那条命令决定。
它也必须赶在 `PROMPT_COMMAND` 之前(有人在那儿挂 `history -a` 往文件里追加)。

**删之前先认一认。** 只有当最后一条历史里出现 `__vela_hist_scrub` 才动手。少了这一步,
遇上真配了 `HISTCONTROL=ignorespace` 的用户 —— 那时我们这行**根本没进历史** ——
删掉的就是人家上一条真命令。**删错用户的历史比留下一行噪音严重得多**,这条用例单独钉着。
记号就是那个临时变量名本身,因此它必然出现在被记下的那一行里,不必另埋记号。

**取序号用参数展开,不用 `set -- $line`。** 后者会对历史内容做通配展开,一条含 `*` 的命令
能把它变成一串文件名。另外 `history 1` 前面要带 `HISTTIMEFORMAT=`:用户设过它的话,
输出会在序号后多一列时间戳,而序号正是按"开头的数字"取的。

整段仍旧包在 `eval '…'` 里、由 `BASH_VERSION` 守卫,理由与目录上报钩子逐字相同(§见
`ShellIntegrationScript.Bash` 的注释):shell 先把整行解析完再执行,裸写的 `case`/`${var//}`
会让 fish 在**解析阶段**就报错,那时守卫还没来得及短路。

### 三、作用范围是「所有静默注入」,不只是那个钩子

摘历史挂在 `TerminalTabViewModel.SendSilentCommand` 上,于是四条注入一视同仁:
目录上报钩子、设置里的全局「连接后执行命令」、配置里的「认证后执行命令」、
以及会话的「起始目录」那条 `cd`。这些都是**宿主替用户敲的**,一条都不该出现在他的历史里。

⚠️ **zsh / dash 上那一行仍会留在历史里**:zsh 没有 `history -d`,守卫把摘历史挡在外面。
而那一行在 zsh 上本来就是个空操作(钩子只对 bash 生效)—— 真正的解法是**干脆别往非 bash 的
shell 里注**,已记进 `feature-plan.md`。

### 四、验收

`dotnet build VelaShell.slnx -warnaserror` 零警告零错误;`dotnet test VelaShell.slnx` 全绿。

新增用例 7 条。其中 4 条(`ShellHistoryScrubShellTests`)把整行交给**真正的、交互式的**
bash 跑:注入行不留痕、用户自己的命令一条不少、退出码由后半段决定、`ignorespace` 下不误删
用户上一条、设了 `HISTTIMEFORMAT` 照样摘干净。

**必须是交互式 bash** —— 脚本模式下 history 根本是关的,`bash script.sh` 跑得再绿也什么都没验到
(既有的 `PromptHookShellTests` 走的正是脚本模式,它验的是 `PROMPT_COMMAND` 的拼装,不是历史)。
所以这一组用管道喂 `bash --norc -i`,`HISTFILE` 一律指到临时文件 —— 测试绝不能碰开发者自己的
`~/.bash_history`。另外做过一次反证:把 `Prepend` 改成原样返回,这 4 条里有 2 条立刻红,
不是"怎么改都绿"的假用例。

---

## ✅ 67. 2026-09-10 MaxMind.Db 5.2 的 4 条 MMDBSG001:private 嵌套类型,源生成器看不见

`d3422b79` 把 MaxMind.Db 从 5.1.0 升到 5.2.0 之后,`MmdbIpGeolocationService` 头上多出 4 条:

```
warning MMDBSG001: Type '...MmdbIpGeolocationService.MmdbRecord' is not accessible from source-generated code
                   (MmdbNamed / MmdbCountry / MmdbLocation 同)
```

**这不是可以按掉的洁癖警告**:CI 那步构建走 `-warnaserror`(`ci.yml:107`),4 条警告就是 4 条错误。

### 一、5.2 起多了一个源生成器

包里新增了 `analyzers/dotnet/cs/MaxMind.Db.SourceGenerator.dll`:它扫 `[Constructor]` 标注的模型,
产出一份挂着 `[ModuleInitializer]` 的 `Register()`,把每个类型的激活器提前注册进
`MaxMind.Db.SourceGeneratorSupport` —— 为的是不在运行期用反射构造。

而生成的那份代码落在**本程序集的另一处**,不是嵌在 `MmdbIpGeolocationService` 里面。
四个模型当时写成 `private` 嵌套类型,对它就是不可见的。

### 二、真正的代价不是那行黄字

生成不出激活器时,库并不会失败,而是**静默退回反射**那条老路。包里那条路径自己的注释写得很直白:
「unsupported in trimmed applications」。当前 `PublishTrimmed=false`(`VelaShell.csproj:40`),
所以运行期一切照旧、查询结果没有任何差别 —— 但这等于埋了一颗**开启裁剪/AOT 那天才炸**的雷,
而那种失败是运行期的、只在特定构建配置下复现的。**警告消失只是副产品,把 AOT 那条路接上才是目的。**

### 三、`private` → `internal`,但不必搬出去

四个模型仍旧嵌在 `MmdbIpGeolocationService` 里:外层类型是 public,内层改成 `internal` 就足够让
同程序集的生成代码看见它们。**没有必要为了迁就生成器把模型提到顶层** —— 那会让四个只服务于一处
实现细节的类型跑进 `VelaShell.Infrastructure.Diagnostics` 命名空间,是更大的代价。

改动处留了注释写明这四个为什么不是 `private`,免得下一个人按「能收窄就收窄」的直觉又改回去。
顺手删掉 `MmdbRecord` 头上重复了两遍的同一行注释。

### 四、验收:不能只看警告没了

`-p:EmitCompilerGeneratedFiles=true` 落一份生成物出来核过,
`MaxMind.Db.SourceGenerator.g.cs` 里四个 `RegisterType<…>` 加一条 `RegisterDictionary` 齐了,
`MmdbRecord` 的三个参数按 `city` / `country` / `location` 正确映射 —— 生成的激活器确实接上了,
而不只是诊断被绕过去。

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;
`dotnet test VelaShell.slnx`(按 CI 那条过滤)**3364 通过 / 5 跳过 / 0 失败**。

---

## ✅ 68. 2026-09-10 标签页的协议图标:颜色答「哪一台」,图标答「哪一种」(用户需求)

> 「为我的 SSH 链接的选项卡标签页添加一个图标,可以使用 square-terminal,同时 SFTP、FTP 等
> 可以使用 hard-drive,redis 可能要使用 REDIS.svg,lucide 目前没有 redis 的图标,
> 然后串口使用图标 usb-c-port。是否需要调整插件让其支持配置或者设置标签页图标的功能。」

摸现状先摸出一件与预期相反的事:**SFTP 与插件工作台的标签早就有图标了**,
`SftpDockTabItem` 写死 `Icon.folder-open`、`WorkspaceDockTabItem` 写死 `Icon.hard-drive`。
真正一个图标都没有的是 SSH 终端标签。而那两个写死的值本身也有问题:

- Redis 的工作台标签顶着 `hard-drive`,与 SFTP 标签**撞不撞**取决于运气 —— 这一版里恰好要撞,
  因为用户点名 SFTP / FTP 用的就是 hard-drive;
- `folder-open` 画的是「打开文件夹」这个**动作**,而标签要答的是「这是哪一种连接」。

### 一、颜色与图标答的不是同一个问题

`ConnectionAccent` 已经在标签上了,按 profileId 哈希(或用户指定)给一条色带,答的是
**「哪一台机器」**。新加的 `Services/ConnectionIcon` 答的是**「哪一种连接」**。
两者都挂在同一条标签上,但一旦混起来就都失效 —— 颜色被协议占用就分不出机器,
图标按 id 随机就分不出类型。所以是并排的两个服务,不是一个。

映射只有三条,而且**只认宿主内建的协议**:

| 连接类型 | 图标 |
| --- | --- |
| SSH | `Icon.square-terminal` |
| SFTP / FTP | `Icon.hard-drive` |
| 插件协议(Redis / 串口 / S3 …) | `Icon.plug`(通用插头) |
| 本地终端(无配置) | 不画,连位置一起收掉 |

本地终端那条不是省事:标签里留一块空,与旁边有图标的标签对不齐,看着像渲染坏了。
`IsVisible` 绑 `ObjectConverters.IsNotNull`,收掉整个位置。

### 二、插件的图标不进宿主的对照表

用户问的「是否需要调整插件」——**需要,而且只能那么做**。
宿主里放一张「`velashell.redis` → Redis 标」的表是最快的,但那张表**第三方插件永远进不去**,
与 `Protocols` / `Workspaces` 这套能力面存在的意义正相反(§见 feature-plan 的排期纪律)。

所以契约加在 SDK 一侧,`ProtocolDescriptor` 与 `WorkspaceDescriptor` 各三个字段:
`IconPathData` / `IconViewBoxSize` / `IconIsFilled`。传**路径数据**而不是资源键 ——
这不是新发明,`PanelTitleAction.IconPathData` 早就是这么定的,理由一字不改:
隔离进程里没有宿主的 `Icon.*` 资源字典。

⚠️ **这一段还不能用**:按 velashell-plugin-sdk 那边 `AGENTS.md` 的纪律,
「发版是人的决定」—— 契约写好了,版本号没动,也没造本地包。
在 SDK 真的发布之前,宿主与插件两边都不抬包版,Redis / 串口的标签**照旧画通用插头**。
三步的顺序与各自要填什么,记在 `feature-plan.md` 的插件生态那一节。

### 三、实心 logo 与描边图标是两种画法

用户给的 REDIS.svg 是**填充式**的、视框 `0 0 1030 1024`。而 `LucideIcon` 干的事是
「按 24×24 视框缩放,再用 2/24 的圆头画笔**描边**」。直接喂进去会连撞两堵墙:
按 24 缩放等于把它放大四十多倍,以及用描边去画实心图形得到的是它的**轮廓线**,一团糊。

于是控件开了两个口子:`Fill`(非空则填充、**且不再描边**)与 `ViewBoxSize`(默认 24)。
两个都只为插件的品牌图标存在,宿主自己的图标集一律不碰它们 —— lucide 的描边语言不该被破坏。
`ViewBoxSize` 取到非正数时按 24 处理:一个写错的值不该让整条标签条的图标消失。

### 四、验收:资源键写错是不会报错的

`{StaticResource Icon.*}` 的键写错**没有任何编译期报错**,取不到就是不画 ——
静默少一个图标。同一个教训 `ConnectingDocumentViewUiTests` 上已经吃过一次,
所以这次的 `SessionTabIconUiTests` 把三个键都放进真正的应用资源字典里解析一遍。

新增用例 11 条。**做过反证**:把 `ConnectionIcon.SshKey` 改成一个不存在的键,
`SessionTabIconUiTests` 立刻红 2 条 —— 不是"怎么改都绿"的假用例。

「三种字形互不相同」那一条比的是**解析出来的几何**而不是那三个常量键:
后者是编译期常量,分析器(MSTEST0032)会判成恒真的废断言(§见 63 里同样的坑)。

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;
`dotnet test VelaShell.slnx`(按 CI 那条过滤)**3375 通过 / 5 跳过 / 0 失败**。

---

## ✅ 69. 2026-09-11 插件自己的标签页图标:一个入口,以及一次当天推翻的设计

§68 把宿主内建的三种协议接上了图标,插件那一半当时留了个洞:Redis、串口、AI 面板
一律画同一个通用插头。这一节补完它 —— 顺带记下中途被用户一句话推翻的设计。

### 一、宿主里不放「插件 id → 图标」的表

最快的做法是在 `ConnectionIcon` 里写一张 `velashell.redis → Redis 标` 的映射。
没那么做,理由与 `Protocols` / `Workspaces` 存在的理由是同一条:**那张表第三方插件永远进不去**。
于是口子开在 SDK 一侧,插件把图标交出来。

### 二、被推翻的那版:同一个概念,三处各写三个平行属性

第一版给 `ProtocolDescriptor` 与 `WorkspaceDescriptor` 各加了一组
`IconPathData` / `IconViewBoxSize` / `IconIsFilled`,发了 2.0.3。
紧接着发现**面板那条路是漏的** —— AI 助手的聊天页清单里只有 `commands`,
它经 `Ui.ShowPanelAsync(PanelOptions)` 开标签,不走描述符。于是给 `PanelOptions` 抄了第三份。

用户在 review 里一句话点破:「这两种图标都是给插件的标签页添加图标,为什么搞成 2 套了?」

问得对。「一段路径 + 它的视框 + 描边还是填充」是**一件事**,拆成三个平行属性挂在三个类型上,
插件作者要记的是九个名字而不是一个。收成一个 `PluginIcon`,三处都填它,并带两个便捷构造
把两种真实用法直接说出来:`PluginIcon.Stroked(path)` 与 `PluginIcon.Filled(path, viewBoxSize)`。

**这是对 2.0.3 的破坏性变更,而敢改是因为核过一遍零消费者**:插件仓还锁在 2.0.2,
宿主与 AI 插件都没读过那几个字段(仓库里命中 `IconPathData` 的两处是早就存在的
`PanelTitleAction`,画的是窗口标题栏按钮,另一回事)。2.0.3 发出去还不到一天,
改的窗口就是那一刻 —— **等有插件用上了再改就晚了**。收敛后发 2.0.4。

版本历史里把 2.0.3 如实标成废弃而不是悄悄不提:它真的发出去过,装了那一版的人得知道往哪走。

### 三、三样必须一起走到渲染层

宿主侧新增 `TabIcon`(几何 + 视框 + 填充画刷)。**少一样都不行**:

- 少视框 —— 一个视框 1024 的品牌 logo 会被按 lucide 的 24 缩放,放大四十多倍,屏幕上什么也没有;
- 少填充 —— 实心图形被 2px 圆头画笔描边,得到的是它的轮廓线,一团糊。

所以四个标签控件的 XAML 都是三个绑定一起给。`SessionTabIconUiTests` 钉到**渲染层**而不是
视图模型层,正是为了守住这一条:反证时去掉 `ViewBoxSize` 那一行绑定,用例立刻红。

### 四、解析插件给的路径:这里刻意 catch 所有异常

`FromPlugin` 里那个 `catch (Exception)` 与仓库里逐类型列举的写法不同,是有意的。
解析的是**插件给的任意字符串**,而 Avalonia 的 `PathMarkupParser` 抛什么取决于错在哪一位 ——
光是写用例试出来的就有 `InvalidDataException`(命令字认不出)与 `FormatException`(数字坏了)。
枚举类型在这里是在猜,而**猜漏一个的代价是整条标签条连带主窗口一起炸**。

这一条不是推演出来的:`AMalformedPluginPathFallsBackInsteadOfThrowing` 写完第一次跑就红了,
红在 `InvalidDataException` 不在当时的捕获列表里。用例刚落地就抓到了真问题。

### 五、AI 插件用上了

`PanelOptions.Icon = PluginIcon.Filled(AiIcon.PathData, 1024)`。路径数据单独放
`AiIcon.cs` 而不是塞进 `AiPlugin`:一千多字符夹在打开面板的逻辑中间,读代码的人要翻很久。

### 六、还没做的

`velashell-plugins` 那三个插件(Redis / 串口 / S3)**尚未填图标**,今天画的仍是通用插头。
宿主一行都不用再动,记在 `feature-plan.md` 的插件生态那一节。

### 七、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;
`dotnet test VelaShell.slnx`(按 CI 那条过滤)**3379 通过 / 5 跳过 / 0 失败**。

---

## ✅ 70. 2026-09-11 插件窗口的标题栏图标,以及一组「怎么改都绿」的用例(用户反馈)

> 「插件中的窗口,比如 AI 插件中的模型设置、MCP 设置等窗口,打开后还是显示为通用的插件图标,
> 是否可以让他也跟随标签页图标的设置。这样的话,不同的插件打开不同的窗口也不容易混淆。」

§69 让插件的**标签页**能自报图标,窗口那一路漏了。摸过去发现两侧的毛病还不一样:

- **进程内**(`PluginPanelWindow.axaml`)标题栏写死 `Icon.plug`;
- **隔离进程**(`PluginHostShellWindow`)标题栏**一个图标都没有**,只有标题 + 插件 id。

两侧都改成读 `PanelOptions.Icon`。契约一个字没动 —— 2.0.4 的字段本来就在那儿,
只是宿主此前没用它;改的是行为与文档(SDK 那句「窗口模式忽略」现在是假话)。

AI 插件把聊天标签、协作窗口、模型配置/MCP 那一组对话框统一挂上同一个 `AiIcon.Panel`。
它们本来就该长一样:标题栏上认的是「这扇窗属于哪个插件」,不是「这是哪个功能」。

### 一、隔离进程那侧不能复用 LucideIcon

`VelaShell.PluginHost` 只引 SDK 与 Avalonia,**刻意不依赖 `VelaShell.Controls`**,
所以那边自己搭 `Path`。两处差别照抄主程序的口径:实心只填充;描边的笔画按
`2 * 视框 / 24` 算,于是视框放大多少笔就跟着粗多少,任何视框下都保持 lucide 的粗细比例。

### 二、意外收获:一组用例「怎么改都绿」

给这件事写用例时,**反证失败了** —— 把接线整行删掉,用例照样通过。

根因是 `HeadlessUnitTestSession` 的重载选择:
`Dispatch<T>(Func<Task<T>>)` 会 await,而 `Dispatch<TResult>(Func<TResult>)` 不会。
`_session.Dispatch(async () => { ... })` 这种**无返回值**的 async lambda 是 `Func<Task>`,
绑到后者 —— 那个 Task 没人 await,断言抛出的异常**被它整个吞掉**。

这个坑 `ConnectingDocumentViewUiTests` 的注释里早就记过,但 `PluginPanelUiTests` 等文件仍在用。
实测:往一条既有用例的第一行插 `Assert.Fail`,它照样通过。全仓扫下来,
**11 条**这样的用例(`PluginPanelUiTests` 5、`StandaloneSftpDocumentBehaviorTests` 3、
`LocalFilePaneViewUiTests` 2、`PluginThemeTokensTests` 1)。

**试过机械修法,不成立**:给每个 lambda 补一句 `return true;` 改绑到会 await 的重载之后,
7 条当场变成**超时**(各 1 分钟)—— 它们 await 的东西在无头环境里根本不会完成。
也就是说这些用例不是"少写了一个 return",而是异步流程本身需要重做,一条一条来。
本次**未改**它们:把假绿换成真卡,是更坏的状态。已记进 `feature-plan.md`。

本次新增的两条用例用的是同文件里正确的那种写法(同步 body + 返回值),并做过反证:
删掉 `SetIcon` 那一行,`WindowPanel_TitleIcon_FollowsThePluginsOwnIcon` 立刻红。

### 三、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;
`dotnet test VelaShell.slnx`(按 CI 那条过滤)**3381 通过 / 5 跳过 / 0 失败**。

## ✅ 71. 2026-09-11 插件更新:先把「升级会毁数据」堵上,再谈检查更新

起点是一个问题 ——「插件管理界面能不能检查版本、更新插件」。查下来的结论是:
市场、CLI、宿主自更新三边的基础设施已经够用了,但**这条路上有个坑必须先填**。

### 一、覆盖安装会把插件数据清光

`InstallFromVpxAsync` 撞上同 id 已装时,走的是完整卸载:
`UninstallAsync` → `PurgePluginDataAsync` → `IPluginDataStore.PurgeAsync`
(**KV + 机密 + 时序 measurement 整体删除**)外加删掉数据目录。

对 AI 插件意味着:供应商配置(Storage)、API Key 与 OAuth 令牌(Secrets)、
进时序库的聊天记录 —— **升一版全部归零**。

对照组很说明问题:**`vela-plugin update` 那条路一直是对的**。它的 `PluginInstaller.Swap`
只做「旧目录备份 → 换名 → 失败回滚」,从不碰插件数据 —— 它根本不知道数据存在。
同一个升级动作,走命令行数据还在,走管理页数据没了。

而既有用例 `InstallFromVpx_SameId_ReplacesOldVersion` 只断言「替换成功、状态是 Active」,
没有任何人守着数据。

改法照抄 CLI:

- `DetachForUpgradeAsync` —— 停用、释放命令能力、撤协议登记、移出集合。
  与 `UninstallAsync` 的区别全在**没做**的那三件事:不删目录、不清数据、不动安装收据。
  收据要留到新版那张落盘为止 —— 换名失败时它还得替搬回来的旧版作数。
- `MoveAsideForUpgrade` —— 旧目录挪进 `<插件根>/.upgrade/<id>-<guid>`。
  放在插件根**下面**是因为换名要求同卷;而发现期只认根下一层里带 `plugin.json` 的目录,
  备份被套了一层,扫不进来,不会变成一个与正版撞 id 的幽灵插件。
- 失败回滚 —— 删掉半成品新目录,把旧版搬回原位,`AttachAsync` 重新挂上。

**为什么必须是原子换名,而不是「先删再装」**:`PurgeUninstalledDataAsync` 会把数据根下
每个「没有对应插件目录」的子目录当作卸载残留整个删掉。升级中途崩溃留下一个没有目录的 id,
下次启动数据就被当残留清掉了。换名把这个窗口关死。

新用例 `InstallFromVpx_OverAnInstalledVersion_KeepsThePluginsData` 做过反证:
把这次的修复 stash 掉,它立刻红。

### 二、商店侧:按 id 问,不做全量索引

新端点 `GET /api/plugins/latest?ids=a.b,c.d[&pre=true]`(匿名,上限 200 个 id)。

**刻意不做 `latest.json` 那样的全量索引。** 插件多起来以后,让每个客户端为了看几个插件的
版本去下整张表,服务端和客户端两头都在做无用功;按 id 问,请求规模只跟本机装了几个插件有关,
与商店有多大无关。

选版规则抽成纯函数 `SelectLatest` 并单测,与 `vela-plugin` 的 `SelectVersion`
**逐条对齐**(含末尾那条「一个正式版都没有时放宽到最新预发布」—— 宿主自更新的稳定通道
也是这么兜的)。三处口径不同会得到「命令行说该升、管理页说没变」这种没法解释的结果。

**`Plugin.LatestVersion` 不能拿来顶替它**:那个字段按 semver 取最高、预发布也算在内,
作者一发 beta,所有人的「最新版」就成了那个 beta。

响应带 `publisherFingerprint`,宿主因此能在**下载之前**判断这一版换没换发布者 ——
换了人的那一版不该悄悄装上去,而「先下完再被拒」白费的是用户的流量。

### 三、宿主侧:打开管理页时问一次

`IPluginMarketClient` / `HttpPluginMarketClient` 落在 Infrastructure,规矩与
`HttpAnnouncementFeed` 一套:只走 https、响应体有上限、元数据短超时、失败不抛只留一行
`Trace`,出站自动继承 `VelaWebProxy` 装的进程级代理。不发账号、不发令牌、不发安装标识符。

几处值得记下来的判断:

- **版本比较用 `UpdateVersion`(应用自更新那一套),不是 `PluginManager.IsOlder`。**
  后者整段丢掉预发布后缀 —— 本地装着 `1.2.0-beta.1` 时,它会认为商店上的 `1.2.0`
  「不算更新」。顺带记一笔:全仓目前有**四套**版本比较口径(`PluginManager.IsOlder`、
  `UpdateVersion`、CLI 的 `VersionOrder`、市场的 `SemVerComparer`),彼此并不一致,
  已记进 `feature-plan.md`。
- **兼容性复用发现期那三闸**(`DescribeUpdateBlocker` 与 `Describe` 同一套判据)。
  宿主太旧时**给提示、不给按钮** —— 摆一个按下去必然失败的按钮比不摆更糟,
  用户会反复点它,而失败的原因跟插件一点关系都没有。
- **发布者没变就直接装**(指纹与安装时钉住的那个对得上);换了人、或者这一版干脆没签名,
  一律走完整确认流程。为此把 `Install_Click` 里那段「该问的都问过」抽成
  `InstallWithPromptsAsync`,手动装包与更新共用一条 —— 各写一份的话,
  迟早有一份会漏掉其中一问,而漏掉的那一问正是拦住冒名覆盖安装的闸。
- **只在打开插件管理页时外呼一次**,不在启动时、也不在后台定时。`PRIVACY.md`
  中英两份都补了对应的一条(它逐条交代每一个出站请求,新增一条就必须写进去)。

### 四、文档

按 `AGENTS.md` 第二节,velashell-docs 的 `zh/` 与 `en/` 两棵树同步改了三对文件:

- `plugins/STATUS.md` —— 「插件商店 / 插件源」整行**从「❌ 未开始」挪进「⏳ 部分完成」**
  (商店早已上线、宿主现在也有客户端了,留在「未开始」是这份文档在骗人),
  并把那格里「仍未做:发布者签名验证」改准 —— 验签与发布者连续性其实早就落地了,
  缺的是**信任根**。同页那句「下载不在应用内」也不再成立:浏览仍在浏览器,更新已经在应用内。
- `plugins/10-packaging-and-distribution.md` —— 蓝图头部补一条 2026-09-11 实现注记,
  逐条列出与 §2/§3 的出入:没有静态签名索引(改按 id 批量问)、不做后台定时检查、
  默认不吃预发布、自动更新与降级仍然不做、升级不再清数据。
- `cli/cli.md` —— `update` 一节点明宿主管理页用的是**同一套选版规则**;
  「自行部署的商店」那段补上第四个必须提供的只读接口。

市场仓库的 `docs/` 尚未并入 velashell-docs(那边 `AGENTS.md` 写明是唯一例外),
新端点记在它自己的 `docs/api.md` 公开接口表里。

**没做的一件事**:`host/交互与界面规格.md` 里**从来就没有插件管理页这一节** ——
不是缺这次新增的几个按钮,是整个窗口都没写过。补它是新写一节规格,不该塞进这个改动里,
已记进 `feature-plan.md`。

### 五、验收

`dotnet build VelaShell.slnx` 零警告零错误;`dotnet test VelaShell.slnx`
**3389 通过 / 21 跳过 / 0 失败**。市场侧 `dotnet test VelaShell.Market.slnx`
**42 通过 / 0 失败**(含 5 条新的选版规则用例)。

### 六、CI 上浮出来的一条竞态(与本次改动无关,但被它撞出来了)

本次 PR 的 windows-latest 跑挂了一条 **AI 插件**的用例
(`ChatPanelViewUiTests.ClickingAQueuedChip_TakesTheMessageBack`),三平台里只有 Windows 挂,
本机连跑八轮全过。它跟插件更新没有任何交集 —— 是一条早就潜伏在那儿的竞态,
被这次多出来的几条用例改变了 CI 的时序,正好撞了出来。

根因在 `ChatPanelView.QueueFollowUpAsync`:按下回车后它**先清空输入框**,再
`await ResolveAttachmentsAsync(...)`(展开引用可能走一趟 SFTP),回来才 `Enqueue` 并
`RenderQueuedChips()`。也就是说排队芯片是跨了一个 await 之后才出现的,而三条用例都写成
`PressEnter(input); await PumpAsync(10);` —— 固定泵十轮共约 50 毫秒,**没有重试**。
本机跑得赢,windows-latest 是双核共享 runner、八个测试程序集并行,就未必。

三条**全部**改成文件里既有的 `WaitForAsync` 轮询(它每轮 `RunJobs()` 再判条件)。
只改输的那一条等于把另外两条留在原地等下次 —— 它们是同一个形状。

**中途撞上的一个坑,值得记一笔**:把 `PumpAsync(10)` 整个删掉之后,
`EnterWhileBusy_...` 反而当场红了 —— `stub.Requests` 是 **0** 而不是 2。
原来那句固定泵**在兼两份差事**:既等芯片画出来,也顺带给 stub 那一头
把请求记上。只把“等芯片”换成条件等待,“等请求”那份就掉了。
所以 `Assert.HasCount(1, stub.Requests)` 之前另补了一句等 `>= 1` ——
先等它真的发生,再断言“只有这一条”,否则是拿一个还没发生的事实当证据。

**验证方式**:本机 24 核本来复现不出来,于是开 48 个忙循环把 CPU 压满再跑 ——
**旧代码 3/3 挂,新代码同样压力下 3/3 过**(单轮耗时从 4 秒拉到 23 秒,压力是真的)。
不做这一步的话,“改完本机还是绿的”证明不了任何事情 —— 它本来就是绿的。

## ✅ 72. 2026-09-12 远端符号链接:认得出、进得去、建得了,以及删链接会删光目标目录那个洞(用户需求)

> 「为我 SFTP,FTP 等文件操作添加符号链接支持。」

`feature-plan.md` 路线图 C 组原话是「`ISftpService` 现在**没有任何 symlink 面**,远端一个软链目录当场就走不进去」。
摸下去发现"走不进去"说轻了:SFTP 这边其实**一直在跟随链接**(`EnumerationOptions` 与 `GetEntryAsync` 都默认跟随),
所以链接目录能进,只是认不出来是链接;真正的问题是**跟随这件事被用在了删除上**。

### 一、先说那个洞

`SftpService.DeleteAsync` 用一次 stat 判「是不是目录」,而那次 stat 跟随链接。
对一个指向目录的链接(`current -> releases/42`),它被判成目录,于是列举"链接下的"子项逐个删掉,
最后 `rmdir` 链接本身失败或成功 —— **不管哪种,`releases/42` 里的东西已经没了**。
递归删除途中遇到子目录里的链接同理;链接要是指向 `/`,那就是一场事故。

修法:单条查询改为先 lstat,是链接再补跟随的 stat 与 readlink;删除与计数只进 `IsDirectory && !IsSymbolicLink` 的条目,
链接一律当叶子走 `SSH_FXP_REMOVE`(删的是链接本身)。FTP 侧原本就是 `Type != Directory` 走 `DELE`,链接天然是叶子,不用改。

### 二、模型:两个字段,一条口径

`RemoteFileInfo` / `SftpEntry` 各加 `IsSymbolicLink` 与 `LinkTarget`(readlink 原文,可能是相对路径)。
**`IsDirectory` 描述链接指向的对象** —— 这是刻意的:文件浏览器、排序、双击、AI 插件的文件选择器
全都只认 `IsDirectory`,指向目录的链接因此零改动就能进;真正需要区分"链接本身"的只有删除、复制、递归下载三处。
断链(目标不存在)保留链接自身属性、`IsDirectory = false`,而且 `GetEntryAsync` **返回条目而不是 null** ——
链接确实存在,删它不能先报「找不到」。

| 后端 | 列举 / stat | 建链接 |
| --- | --- | --- |
| SFTP(Tmds.Ssh) | 列举改为不跟随(否则 `SymbolicLink` 类型位被抹掉),链接再并发补 stat + readlink;非链接零额外往返 | `CreateSymbolicLinkAsync` |
| FTP(FluentFTP) | LIST 给得出 `Link` 与目标文本,给不出目标是不是目录(库已不提供解引用),对链接补一次 `DirectoryExists` | 非标 `SITE SYMLINK`(ProFTPD 类);回 500/501/502/504 或路径含空格 → `NotSupportedException` |
| 插件协议 | SDK 的 `RemoteFileEntry` 没有链接字段,不变 | 抛 `NotSupportedException`。要支持得先发 SDK 契约,**没在宿主这边编** |

**OpenSSH 的参数顺序坑**:服务端把 `SSH_FXP_SYMLINK` 的两个参数实现反了(bugzilla #861)。
动手前去 Tmds.Ssh 源码核过,它已按 OpenSSH 顺序发包,包装层照常传「链接、目标」,**不能再自己对调一次**。
这一条单测测不到(替身不在乎顺序),所以写了真实服务端用例钉住。

### 三、复制与下载:各选一个现成口径

- **复制链接得到链接**(`cp -P`):同一台服务器上目标文本依旧有效,也不会因为链接指回祖先而无限展开。
  读不到目标文本的链接才退回按内容复制,由原有的环检测与 64 层深度上限兜底。
- **文件夹下载不跟进嵌套的目录链接**(`rsync -r` 不带 `-L`):本地是 Windows,建不了等价的链接;
  跟进去又可能指向 `/` 把整台机器拖下来。用户**显式选中**的那一个链接照常跟随,指向文件的链接照常下载内容。
  代价是嵌套的目录链接在本地副本里缺席 —— 这是有意的取舍,已登记进文档待同步。

### 四、界面

行首图标换成 lucide 的 `folder-symlink` / `file-symlink`(颜色不变,仍答「能不能进去」),名称悬停显示「→ 目标」,
类型列「符号链接」,权限列首位 `l`(与 `ls -l` 一致),属性弹窗多一行「链接目标」。
右键(空白处与行上都有)「新建符号链接」:先问目标(在行上右键时预填该行路径),再问名称(预填目标最后一段)。
五份 resx 各加 5 个键。

### 五、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;`dotnet test VelaShell.slnx`
**3381 通过 / 47 跳过 / 0 失败**。

- **反证**:把 `DeleteAsync` 里的 `IsTraversableDirectory(entry)` 退回 `entry.IsDirectory`,
  `DeleteAsync_OnSymlinkToDirectory_RemovesOnlyTheLink` 当场红(没收到对链接的 `DeleteFileAsync`,走进了递归);改回即绿。
- **真实服务端**:新增 `Core.Tests/Ssh/SftpSymlinkIntegrationTests`(`DockerIntegration`),对容器里的 OpenSSH
  验证列举认得出链接与断链、断链 stat 不返回 null、`CreateSymbolicLinkAsync` 建出来 `readlink` 读回的是目标、
  删链接后目标文件仍在 —— **本机实跑通过**,不是跳过。
- **顺带记一个坑**:这台机器上 `TransferRealChannelIntegrationTests` 那组 Docker 用例**一直在跳过**,容器明明 healthy。
  原因是 `localhost` 先解析到 `::1`,而 Docker Desktop 的端口转发只在 IPv4 上应答,探测与登录都连空。
  新用例写死 `127.0.0.1`;旧用例本次**未改**(不在范围内),但「全绿」里那几条从来没跑过这件事值得知道。
- FTP 的 `SITE SYMLINK` 与链接目录探测**没有真实服务端验证**:测试容器里没有 ProFTPD。

## ✅ 73. 2026-09-12 内置编辑器:链接看得清、配色跟着具名主题走、语言补一轮、窗口加大(用户反馈)

> 「网址邮箱这类链接在深色模式下的蓝色就看不清,浅色模式下还未验证,是否可以换个颜色,
> 同时支持更多类型的语法高亮,同时可以适当的加大一些内置编辑器的默认大小。」

### 一、那个蓝不是语法定义给的

编辑器里的网址/邮箱是 AvaloniaEdit 的 `LinkElementGenerator` 画的,颜色取 `TextView.LinkTextForegroundBrush`,
缺省纯蓝 `#0000FF`。它**不经过**任何 xshd,所以 `SyntaxHighlightingService` 的重着色从来管不到它。
量了一下:纯蓝压在 Dracula 底 `#282A36` 上约 **1.7:1**,确实读不出来。

修法是在 `RemoteFileEditorView.axaml` 里给 `aer|TextView` 设样式,值取 `{DynamicResource VelaInfo}`:
Dracula 青 `#8BE9FD`、Alucard 深青蓝 `#036A96`,其余主题取各自的 Info。走 DynamicResource,主题切换实时生效。
「浅色下未验证」这件事改成了**逐主题量**:`LinkToken_IsReadableOnTheEditorBackground_InEveryTheme`
对十二套主题的 Info 与 BgTerminal 算 WCAG 对比度,全部 ≥ 3:1,并用纯蓝做对照组证明它本来过不了线。
**肉眼没在真窗口里看过**,这是数字上的验证。

### 二、顺着查出来的:配色只认明暗,不认具名主题

`SyntaxPalette.For(ThemeVariant)` 只有 Dracula / Alucard 两套写死的色值,而应用早已有十二套主题 ——
Nord、Gruvbox、GitHub Light 下编辑器底色跟着主题走,代码却还是 Dracula 那一套。
DESIGN.md 本来就规定颜色只活在主题目录与令牌派生里,这两套字面量属于漏网之鱼。

改为 `SyntaxPalette.From(UiTheme)` 从种子色派生:字符串 Yellow、关键字 Magenta、数字 Accent、函数 Success、
类型/链接 Info、注释 TextTertiary、错误 Error、变量 Warning。这套取法对 VelaDark **逐色还原** Dracula
(注释 `#6272A4` 恰好压线 3.02:1,用例钉住它没被提亮改掉);VelaLight 除变量色外逐色还原 Alucard ——
亮色主题的 Warning 与 Yellow 是同一个值,照搬会让字符串与变量撞色,于是变量取橙与红的中点。
任一角色对底色不足 3:1 时向正文色混合到够为止。

编辑器通过 `IThemeService` 解析当前主题,订阅 `EffectiveThemeChanged` 实时重着色
(原先的已知限制「打开后切主题不更新」随之消失)。重着色后必须**先置空再赋值**
`SyntaxHighlighting`:定义是全局单例,同一实例重复赋值是空操作,已缓存的着色行不会重画。

### 三、内置定义的颜色名:以前一半没归类

把 AvaloniaEdit 12 的 21 份内置 xshd 的命名颜色全部导出来对了一遍,`ForRole` 只收录了约一半。
没收录的走"太接近背景才改"的兜底,于是 CSS 选择器 `DarkBlue`、HTML 标签 `#8B008B`、Markdown 链接 `Blue`、
Patch 的增删行在暗色下顶着浅色配色 —— 用户说的"蓝色看不清"在 Markdown/HTML 文件里也有这一份。
现已全部归类(顺带让 Log 的 Warning 取橙、Info 取青,原先分别是红和绿)。
`EveryNamedColour_InEveryDefinition_HasAPaletteRole` 逐个核对,第一次跑就抓出三个漏掉的
(`Patch.UnchangedText`、`XmlDoc.KnownDocTags`、`XmlDoc.XmlPunctuation`)。

### 四、语言补一轮

新增自带 xshd:nginx、TOML、Makefile、Go、Rust、Lua、Ruby、Perl、通用 SQL、HCL/Terraform。
- `.sql` 原先走内置 TSQL(只认 SQL Server 方言),服务器上的 dump 与迁移脚本绝大多数是 MySQL/PostgreSQL。
- nginx.conf、Makefile、`.toml` 原先分别借 Ini / Shell / Ini 着色,块、目标、多行字符串都认不出来。
- **nginx 按目录认**:判定改用远端完整路径,`/etc/nginx/conf.d/*.conf` 与 `sites-available/` 下按 nginx,
  别处的 `.conf` 仍按 Ini。
- 另补一批扩展名(`.tsx`/`.jsx`、`.scss`/`.less`、`.axaml`/`.resx`、`.jsonl`、systemd `.timer`/`.socket`……)
  与 shebang(ruby / perl / lua5.4 / `make -f`)。

测试不止"能加载、产生了区段"——一条过宽的正则能把整行染成一个颜色也照样满足。
`BundledDefinitions_ColourTheRightTokens` 对每种新语言断言**具体的词染成具体的类别**(26 组)。

### 五、两个坑

- **XML 注释里不能有 `--`**:Lua.xshd 的注释里照写了 Lua 的注释符,整份定义解析失败;服务把异常吞了,
  编辑器静默退化成纯文本。是 `BundledDefinitions_Load` 那条"静默失败守门人"抓出来的,
  定位时临时加了一条不吞异常的诊断用例,查清后删掉。
- **AvaloniaEdit 12 内置的 TeX 定义自身是坏的**(延迟加载即抛 "Could not find main RuleSet")。
  `.tex` 原先映射过去,一直在静默退化。现在不再映射;新增 `EveryDetectedType_ResolvesToALoadableDefinition`,
  判定器能返回的每个定义名都必须真能加载。写这条用例时自己又踩一脚:`KnownDefinitionNames` 写成静态初始化器、
  排在两张表之前,按源码顺序初始化时表还是 null,整个类型初始化器抛出 —— 改成按需计算的 getter。

### 六、窗口

默认 928×648 → 1160×820。小屏(1366×768、150% 缩放)放不下,`OnOpened` 按屏幕工作区收缩并摆回中央,
写法与设置窗口的 `FitIntoWorkArea` 一致。

### 七、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;`dotnet test VelaShell.slnx`
**3449 通过 / 47 跳过 / 0 失败**(`SyntaxHighlightingTests` + `RemoteFileEditorDirtyStateTests` 共 112 条全过)。

- 链接色、语法色的「浅色下是否看得清」是**按对比度逐主题量**的,不是在真窗口里肉眼看过的。
- 窗口加大与工作区收缩没有自动化用例(依赖真实屏幕),未在小屏上实机验证。

文档:`velashell-docs` 的 `{zh,en}/host/SFTP双栏与WinSCP差距分析.md` 附录已补 2026-09-12 一节,
见 [velashell-docs#34](https://github.com/VelaShellLabs/velashell-docs/pull/34)。

## ✅ 74. 2026-09-12 目录比较与同步:对标 WinSCP 的三件套,难的不是比较,是时间(用户需求)

> 「为我实现(SFTP,FTP,FTPS)目录比较同步的功能」「让其能对标 WinSCP 等软件的功能。」

`SFTP双栏与WinSCP差距分析.md` 把它记作 C1「与 WinSCP 最本质的差距」,WinSCP 那边是三样东西:
**比较目录**、**同步**(单向 / 双向 / 镜像,预览后执行)、**保持远程目录最新**。三样都做了,SFTP / FTP / FTPS 共用一套
—— 双栏文档只认 `ISftpService` + 会话标识,插件协议的文件文档顺带也能用。

### 一、入口与交互

双栏文档顶部加一条 32px 工具条(`SftpDocumentView`),两个「图标 + 文字」按钮:

- **比较目录**:两栏当前目录这一层(不递归,与 WinSCP 同名命令同口径),在各自一栏里**选中**不同的条目 ——
  本地栏选本地独有 / 本地较新,远端栏选远端独有 / 远端较新,大小不同与冲突两边都选。工具条右侧一句结论,任一栏换目录即清。
  远端栏隐藏点文件时本地点文件也不参与,否则每个 `.git` 都是「只有本地有」。
- **同步…**:独立窗口 `DirectorySyncWindow`(窗体规格照链路追踪),同一文档只开一个。选项:方向(本地→远端 / 远端→本地 / 双向)、
  模式(同步 / 镜像 / 仅时间戳)、比较依据(时间、大小)、删除多余文件、仅已存在的文件、文件掩码(WinSCP 写法 `包含 | 排除`)。
  比较 → 预览(每步一行,可逐项取消)→ 同步 → **自动复查**。改任何选项都作废预览;有删除先二次确认。
- **保持远端最新**:同一窗口的按钮。开始时完整对齐一次,之后监视本地目录树,防抖 1 秒只重对变化的那一层;
  新目录整棵对,监视缓冲溢出整棵重对。列表区换成活动记录。关窗 / 关文档即停。

### 二、分层

| 层 | 内容 | 为什么在这 |
| --- | --- | --- |
| `Core/DirectorySync` | `SyncFileMask`、`DirectoryComparer`、`SyncPlanner`、`DirectoryTreeScanner`、模型与 `SyncTime` | 纯逻辑,同一份比较结果 + 选项永远得到同一份计划;单测不碰 UI |
| `ISftpService.SetLastWriteTimeAsync` | SFTP 走 setstat,FTP 走 `MFMT`,插件协议如实抛 `NotSupportedException` | 见第三节 |
| `ViewModels/DirectorySyncRunner` | 建目录 → 改时间 → 传文件 → 删除 | 传输复用 `FileBrowserViewModel` 的管道 |
| `ViewModels/DirectorySyncViewModel` | 窗口状态、预览、保持最新 | — |

传输管道只抽了一刀:`RunTransferBatchAsync` 拆出 `ExecuteResolvedBatchAsync`(登记批次、并发上限、收尾通知),
`RunTransferAsync` 改为返回最终状态;新增 `RunSyncTransfersAsync` 直接进执行段。**跳过冲突策略**(预览就是确认,
「文件已存在时:询问」会对每个要覆盖的文件再弹一次)与**续传探测**(目标比源小正是「内容不同」的常态,当成半截文件续传会拼出错文件)。
进度浮窗、全窗口并发名额、取消、传输日志与普通传输完全共用。

### 三、时间:这件事真正的难点

同步靠修改时间判新旧,于是三处必须同时对:

1. **传完回写时间,不看「保留时间戳」设置**。不回写,刚上传的文件在远端的时间是「现在」,下一次比较就是「远端较新」,
   双向同步还会把它下载回来。SFTP 上传原本就按设置 setstat,但 FTP 上传从来不写时间 —— 于是给 `ISftpService` 加了
   `SetLastWriteTimeAsync`,执行器每传完一个就调一次(SFTP 在开着「保留时间戳」时会多一次 setstat,可以接受)。
2. **FTP 的 `MFMT` 必须按 UTC 发**,而且**不能用 FluentFTP 的 `SetModifiedTime`**:它按 `TimeConversion` 配置换算时区,
   发出去的值会被一个配置项悄悄挪几个小时。自己拼 `MFMT yyyyMMddHHmmss path`,回 500/501/502/504 视为不支持。
3. **精度**:FTP 的 Unix LIST 只到分钟,半年前的文件只到日期。拿秒级本地时间去比,几乎每个文件都「不同」。
   `SyncItem` 带精度,比较时两边截到较粗的一级(在本地时区里截,LIST 的日期是按服务器日历给的),容差 1 秒。
   精度只对 FTP / 插件协议按原始值推断;SFTP 恒为秒,不能被一个恰好落在整分的时间降级。

`FtpFileServiceIntegrationTests` 对环回 FTP 服务器(新增 `MFMT` 支持,可关)实跑整条链:上传 → MFMT → LIST 读回 →
比较器判「相同」,再把本地改晚一分钟必须判「本地较新」;另一条验不支持 MFMT 的服务器报 `NotSupportedException`。

### 四、几条安全口径

- **删除放在最后,取消后不删**。计划里删目录时其下条目不再单列(目录整棵删)。
- **冲突不动**:一边文件一边目录,或远端有只差大小写的两个名字而本地(Windows / macOS)区分不了 —— 谁覆盖谁都是替用户做决定。
  冲突目录下的子项不再比较,否则勾上删除会去删一棵没打算动的树。
- **不跟随指向目录的链接**(两边都是,与文件夹下载同口径);本地只认真正的符号链接 / 目录联接 —— OneDrive 占位文件也带
  `ReparsePoint` 属性,按属性一刀切会让同步盘里的文件整片「消失」。
- **远端名字拼本地路径逐段过 `LocalPathSafety`**:`a:b`、`CON` 报错跳过,不写到别处。
- 被掩码排除的目录不扫描,也就不会被「删除多余文件」删到;保持最新时变化落在被排除目录里同样不同步。

### 五、一个自己埋的坑:进度回调覆盖结论

VM 用例单跑全绿、混跑偶发红:状态栏最后应是「同步完成…」,实际是「正在同步 2/2…」。`Progress<T>` 的回调是异步投递的,
执行结束后才到的那几条把结论覆盖了 —— 真窗口里 UI 线程排队顺序通常救得回来,但不是保证。
修法:状态文字带代次(`_statusEpoch`),写结论时进一代,旧代的进度回调一律作废。

### 六、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;`dotnet test VelaShell.slnx`
**3513 通过 / 21 跳过 / 0 失败**。第一次全量跑红了一条 `WindowMoveDragUsageTests`:新窗口标题栏直接调了 `BeginMoveDrag`,
改走 `BeginWindowMoveDrag`(#264 的幽灵弹起纠正)后转绿 —— 约定测试拦住的正是它该拦的东西。

- 新增用例:`Core.Tests/DirectorySync` 四个类(掩码、比较器、计划器、扫描器),`VelaShell.Tests/ViewModels/DirectorySyncViewModelTests`
  (比较 → 同步 → 复查无剩余、未勾选不执行、下载进新目录并对齐时间、Windows 非法名字拒写、改选项作废预览、双向收紧选项、
  保持最新的启动对齐与新目录上传、取消后不删),`FtpFileServiceIntegrationTests` 两条 MFMT 真协议用例。
- **没有自动化覆盖、也没有实机看过的**:窗口布局与主题下的观感;真实 FileSystemWatcher 的事件节奏(用例替换了监视工厂);
  FTPS 与真实 FTP 服务器(vsftpd / ProFTPD / IIS)对 `MFMT` 的实际支持;服务器与本机不同时区时的时间偏移(已知限制,见差距分析 7.5)。

文档:`velashell-docs` 的 `{zh,en}/host/SFTP双栏与WinSCP差距分析.md`(C1 改为已实现,新增第七节)与
`{zh,en}/host/交互与界面规格.md` §6 已同步(含 §75 的 SHA-256 口径),见
[velashell-docs#35](https://github.com/VelaShellLabs/velashell-docs/pull/35)(待合入)。

## ✅ 75. 2026-09-13 同步比较:先比 SHA-256,不支持或出错再回退到大小与修改时间(用户需求)

> 「是否可以先按照 SHA256 校验做比较,若是不支持或者出错再回退到按照文件大小和修改时间作比较。」

§74 的比较只看大小与时间,两类情况处理不好:`git checkout` / 解压 / `touch` 之后时间全变、内容没变,整片重传;
大小与时间都没变、内容却变了,永远看不出来。现在默认先比内容。

### 一、口径

- **只算两边都有、大小相同的文件**:大小不同已经证明内容不同,读两边整份文件只为再证明一次是纯浪费。
- 摘要相同 → `Same`,**时间不同也不传**;摘要不同 → 内容确实变了,由时间判断哪边较新,时间也相同时为 `Differs`
  (原 `SizeDiffers` 改名,含义扩成「内容不同而看不出哪边新」)。
- **回退分两级**:远端整体不支持或出错 → 余下全部回退、附注写明原因,不再一批批撞同一堵墙;单个文件读不了 → 只有它回退,
  附注写「N 个中有 M 个」。只勾了 SHA-256 而没勾时间与大小时,回退按两样都比 —— 回退不能退成「什么都不比」。
- **先远端、后本地**:服务器不支持时本地一个字节都不读。
- 时间戳模式也用上:算出摘要而不同的文件不改时间,免得把差异掩盖掉。
- 「比较目录」(一层)同样先比摘要;算摘要期间任一栏换了目录,结论作废,不把选中落到错的行上。

### 二、远端怎么算

| 后端 | 做法 | 判为「不支持」的情形 |
| --- | --- | --- |
| SFTP | SSH exec 通道跑 `sh -c '…' vela-sha256 路径…`:有 `sha256sum` 用它,否则 `shasum -a 256`,都没有打印标记;每批 ≤200 个路径、≤16 000 字符 | 会话没有 SSH 客户端、exec 被拒(`ForceCommand internal-sftp`)、打印了标记、一行摘要都认不出且标准错误不是工具自己的逐文件报错 |
| FTP / FTPS | FluentFTP `GetChecksum(path, SHA256)`:服务器通告 `HASH` 且含 SHA-256 用它,否则 `XSHA256`;指定了 SHA-256 就不会退去用 MD5 / CRC | `FtpHashUnsupportedException`、命令回 500/501/502/504;连接断开照常上抛,不当成「这个文件算不出来」 |
| 插件协议 | — | SDK 没有这一面,恒为不支持 |

命令与解析拆成纯函数 `Core/Sftp/RemoteSha256`:

- 用 `sh -c`,是因为登录 shell 可能是 fish / csh,直接写 `if … fi` 会语法错。
- 路径逐个单引号转义,再经 `"$@"` 原样传给工具。
- GNU `sha256sum` 遇到名字里有反斜杠或换行时,会在行首加 `\` 并转义名字,解析时反转义。
- `sha256sum` 有文件失败时退出码为 1,但其余文件照常输出。所以用 `RunCommandDetailedAsync` 同时拿标准错误与退出码,才分得清「有个文件读不了」和「这台主机跑不了」。

### 三、缓存

`SyncChecksumCache` 以「哪一边 + 完整路径 + 大小 + 修改时间」为键,挂在双栏文档上,与同步窗口共用,关文档即丢。
同步后的自动复查只重算刚传过去的文件(它们的远端时间被改过,键对不上)——**顺带就是一次传输后校验**。
代价是同一窗口内「内容变了而大小与时间都没变」(刻意 `touch -r`)会用到旧摘要,已写进差距分析 7.5。

### 四、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;`dotnet test VelaShell.slnx`
**3541 通过 / 21 跳过 / 0 失败**。途中红过一条自己写的 `NoSshClientForTheSession_IsNotSupported`:NSubstitute 对接口返回值
默认给自动替身而不是 null,用例根本没走到「没有 SSH 客户端」那条路,显式返回 null 后转绿。

- 新增用例:`RemoteSha256Tests`(引号、两种输出格式、转义名字、逐文件失败、整体不支持)、`SftpServiceSha256Tests`、
  `SyncChecksumsTests`(只算同大小、远端不支持只问一次、单文件回退、缓存命中、分批)、`ChecksumComparisonTests`、
  VM 用例三条(内容相同时间不同不传并以关掉校验作对照、大小时间相同内容不同照传、不支持时回退且附注写明)、
  FTP 环回服务器新增 `XSHA256`(可关)的两条真协议用例。
- `RemoteSha256IntegrationTests`(`DockerIntegration`)对真实 OpenSSH 验证空格、单引号、反斜杠、`$`、换行文件名的摘要与本地一致。
- **没有验证的**:真实 FTP 服务器(FileZilla Server、ProFTPD `mod_digest`、IIS)对 `HASH` / `XSHA256` 的实际应答格式;
  大目录首次比较的耗时(没有做基准)。


## ✅ 76. 2026-09-14 终端里打中文看不见拼音:合成串没人画(用户反馈)

> 「他没有显示输入的拼音,导致用户输入错误的时候不知道删除多少个字符。」

截图里候选窗好端端浮在提示符下方,提示符后面却什么都没有 —— 同一台机器上的 Windows 终端显示的是
`ni'hao`。选错字想退回去改,只能靠数自己敲了几下。

### 一、根因:两头都不画

反编译 `Avalonia.Win32` 12.1.2 的 `Imm32InputMethod` 后,这条链是闭合的:

```csharp
public bool ShowCompositionWindow => false;          // 从不让输入法自己画合成窗

public void CompositionChanged(string? composition, int? cursorPosition)
{
    if (IsActive && Client.SupportsPreedit)          // ← 卡在这里
        Client.SetPreeditText(composition, cursorPosition);
}
```

Avalonia 只用 `ImmSetCandidateWindow` 摆候选窗的**位置**,合成串本身一个像素都不画;它唯一的出口
`SetPreeditText` 又被 `Client.SupportsPreedit` 挡着。而 `TerminalImeClient` 当时报的是 `false`
(§#14b 只做了候选窗定位,注释里写着「终端并非可编辑文档、无缓冲区内的预编辑」)——
于是拼音被平台**静默丢弃**:输入法那边不画,终端这边收不到,屏幕上就只剩一个悬空的候选窗。

「终端不是可编辑文档」这句本身没错,错在由此推出「所以不需要预编辑」。合成串压根不属于文档:
它还没提交,一个字节都不该进屏幕缓冲、不该下发 PTY。它属于**叠画**,和幽灵文本是同一类东西。

### 二、修法:合成串住进 CursorOverlay

`SupportsPreedit` 改 `true`,`SetPreeditText` 的两个重载都接住(带串内插入点的给 Win32 IMM,
不带的给 X11 等后端),状态落在控件上,绘制交给已有的光标/幽灵叠加层 ——
每敲一个拼音字母只重记录这一层,正文的绘制记录原样复用。

呈现一律照搬各家编辑器与 Windows 终端的既有约定,不自创交互:

- **列宽走正文那套 `CharWidth`**,不走 `FormattedText` 的比例排版。合成串一提交就变成屏幕缓冲里的
  单元格,两套度量对不齐的话,候选一上屏文字会横向跳一下。日文输入法在选字前就把罗马字转成假名
  (`にほんご`),这时插入点在第 2 个字符之后、屏幕上却是第 4 列。
- **下划线标「尚未提交」**,取**前景色**而非光标色 —— 它属于文字,不属于光标。
- **光标移进合成串里**,停在输入法报的串内插入点上(IMM32 的 `GCS_CURSORPOS`),
  **长相仍是用户配的那一种**:配的是块就仍是块,配了不闪就仍不闪。为此把 `RenderCursor` 里的风格分支
  抽成 `DrawCursorShape`,正文与合成串共用;它返回「画的是不是实心块」,块状光标压住的那个字
  由调用方用背景色重绘(正文取自屏幕缓冲,合成期间取自合成串)。程序自己隐了光标(DECTCEM)就不画。
- **合成期间幽灵文本让位**:幽灵是按「光标左侧已回显文本」现算的,和未提交的合成串叠在一起就是重影。
  VS Code 在 IME 合成期间同样藏起内联建议。
- **报给平台的光标矩形横跨整条合成串**。Avalonia 把它直接当候选窗的 `CFS_EXCLUDE` 排除区
  (`CANDIDATEFORM.rcArea` 就取自它),只报光标那一格的话,拼音一长就被候选窗压在下面,等于白显示。
  命令补全弹层用的 `GetCursorRect()` 仍取单格,不受影响。

按键路径**没动**:`TerminalKeyRouter` 早就把 `Key.ImeProcessed` 归到 `ImePassthrough`(§#14a 的
htop 事故),合成期间的方向键/回车/ESC 本来就不会编码下发。这次只补上「看得见」。

### 三、三处会卡住的收尾

合成态是平台推过来的,平台不收尾就会永久挂在光标上:

- **失焦**:Win32 换客户端时会替我们清一次(`SetClient` 里那句 `Client.SetPreeditText(null, null)`),
  别的后端未必,`OnLostFocus` 自己再清一次。
- **关掉「启用输入法」设置**:此后平台不会再回调,`ImeEnabled` 的 setter 顺手清掉。
- **回滚态**:叠加层在 `_scrollOffset != 0` 时整层不画,不管的话用户翻着历史打中文就是对着空白敲。
  合成**开始**那一刻按 `ScrollOnKeystroke` 把视口拉回底部(与敲普通字符的既有语义一致)。

空串与 `null` 等价视作「合成结束」—— 部分后端用前者,留下 0 字符的合成态会让光标让位给一个画不出
东西的叠加层。

### 四、验收

`dotnet build VelaShell.slnx -c Debug -warnaserror` 零警告零错误;`dotnet test VelaShell.slnx`
**3399 通过 / 20 跳过 / 0 失败**。

- `ImePreeditTests`(`Ime` 分类,9 条):一律经 `ImeClientForTest` 走「平台索要客户端」的真路喂合成串,
  把 `SupportsPreedit` 一起锁在断言里 —— 这是原故障的开关,报 false 就整条链断掉。覆盖:
  插入点列(含宽字符按 2 列)、后端不给插入点时落在末尾、空串等价 null、合成串一个字节都不下发 PTY、
  候选窗排除矩形横跨整串而补全锚点仍是单格、关掉 IME 清掉卡住的合成态。
- `ImePreeditRenderTests`(`GlyphRendering` 分类):**像素级**——合成期间屏幕必须真的变,
  撤销后必须一像素不差地复原。逻辑层看着一切正常(合成串收到了、列也算对了)而屏幕上什么都没有,
  正是原故障的样子,只有真读像素才验得到。
- **没有验证的**:macOS / Linux 的实机输入法(本次只在 Windows + 微软拼音下看过实际效果);
  韩文那种「逐字节组字、每一击都在改同一个音节」的输入法;IMM32 的分段属性(已转换/未转换子句)
  —— Avalonia 不转发这份信息,因此整条合成串画成同一种样式。

## 77. 2026-09-15 Linux 显示:改用 XWayland,修复 GNOME 下应用图标未知与鼠标指针模糊

GNOME 50 Wayland 下,deb 安装的图标文件与桌面入口均存在,运行中的窗口却显示为「未知」;
200% 缩放下鼠标指针也模糊。
检查 Avalonia 12.1.2 源码确认:原生 Wayland 后端未发送 `xdg_toplevel.set_app_id`,
`WindowImpl.SetIcon` 也没有实现,主窗口已有的 `Icon` 因此不能建立桌面关联。

经用户确认,宿主启动移除强制 `UseWayland()`,由 `UsePlatformDetect()` 在 Linux 选择
X11(在 Wayland 会话中由 XWayland 承载)。显式配置 `X11PlatformOptions.WmClass`
为 `VelaShell.App`,与 deb/AppImage 共用的桌面入口及 URL handler 的 `StartupWMClass`
一致。Windows/macOS 的平台选择不受影响。

验证:

- `desktop-file-validate` 通过(仅原有多个主分类提示),`git diff --check` 通过。
- 使用安装版的 Avalonia 依赖、相同 X11 配置和仓库的 `.ico` 构建最小窗口,零警告零错误。
  在当前 GNOME Wayland / 200% 桌面实际启动,客户端窗口上报
  `WM_CLASS = (dotnet, VelaShell.App)` 与 128×128 的 `_NET_WM_ICON`。
  该标识与已安装桌面入口一致;检查覆盖了普通入口和 URL handler。
- 按 release.yml 的流程本地 publish linux-x64 self-contained 并用 `Build-Deb.sh` 打出 deb
  (本地无强名称密钥,以 `-p:SignAssembly=false` 构建)。用户安装后实测:
  应用图标正常显示,鼠标指针不再模糊。
- **没有验证的**:本地测试套件;linux-arm64 包;KDE 等其他桌面环境。

## ✅ 78. 2026-09-17 冷启动 7 秒:瓶颈不在 JIT,在 Defender 扫未签名程序集(用户反馈)

用户反馈首次冷启动很慢,且「开了 R2R 似乎也没什么帮助」。

`~/.velashell/logs` 里 `StartupTrace` 已攒下 37 次样本,是干净的两态分布:
全热 `FirstFrame` 0.96–1.28 s,全冷 6.07–7.63 s。关键线索是**每一段都在按同一比例放大** ——
连 `BuildServiceProvider` 这种纯内存操作都从 75 ms 涨到 1,875 ms。纯内存操作不会因为「冷」
而变慢,慢的只可能是它所在的程序集第一次装载这件事。

在本机(NVMe + i7-13700,Defender 实时防护开启,发行目录 `D:\VelaShell` 227 MB / 317 文件)
把三个候选原因拆开量了一遍:

| 测量 | 结果 | 结论 |
| --- | ---: | --- |
| robocopy 整目录复制 | 188 ms | 磁盘不是瓶颈 |
| 复制到新路径后首次全量读 | 3,934 ms | Defender 首次扫描 |
| 同一批文件再读一遍 | 87 ms | 扫描结果已缓存 |

再按 Authenticode 签名状态拆开,**体积几乎相同而代价差 4.4 倍**:

| 签名状态 | 文件数 | 体积 | 首次扫描 | ms/文件 | ms/MB |
| --- | ---: | ---: | ---: | ---: | ---: |
| NotSigned | 94 | 113 MB | 3,153 ms | 33.5 | 27.9 |
| Valid(微软签的运行时) | 207 | 109 MB | 712 ms | 3.4 | 6.5 |

`ms/文件` 差 10 倍说明大头是**每文件的信誉查询**而非按字节扫描 —— 最极端的是
`plugins/velashell-ai/ExCSS.dll`,只有 0.33 MB 却花了 312 ms。未签名的那 113 MB 正是
`VelaShell.*` / `Avalonia.*` / `SonnetDB` / `BouncyCastle` 与随包分发的 AI 插件。

**R2R 为什么白开**:它省的是 JIT,那是 CPU 时间、冷热两态一样多,碰不到上面这 6 秒;
而它把未签名程序集的体积几乎翻倍(`VelaShell.dll` 2.9 MB IL → 8.4 MB 代码段,
`Avalonia.Base` 2.3 → 7.4 MB,`SonnetDB.Core` 16 → 21.4 MB),等于直接加钱买扫描时间。
省下的 JIT ≈ 多出来的扫描,净收益接近零。

根治是 Authenticode 签名(同样的字节,签过名就从 27.9 ms/MB 掉到个位数,并顺带解决 #299
的报毒),但眼下没有证书。本轮先做三件不依赖证书的事:

**一、插件运行时的启动激活挪到首帧之后。** `App.OnFrameworkInitializationCompleted` 里那句
`Task.Run(() => pluginManager.StartAsync())` 的原注释写着「启动路径零阻塞」——
线程上确实零阻塞,但 Defender 的过滤驱动是**排队**的:随包分发的 AI 插件有 48 个未签名文件 /
33.7 MB,首次装载实测 ~1,973 ms 的扫描(比整个 .NET 运行时的 264 文件 / 193 MB 还贵,
因为运行时走微软签名的快速路径),而 `velashell.ai` 的清单声明了 `onStartup`,于是这 48 个
文件正好和首帧要的 Avalonia/Skia 程序集挤在同一条队里 —— 打点上 `WindowOpened→FirstFrame`
冷启动 1,165 ms、热启动只要 391 ms。

新增 `FirstFrameSignal`(`VelaShell.Infrastructure/Diagnostics/`):`MainWindow` 在首帧回调里
`Signal()`,`App` 那条后台链 `WaitAsync(10 s)` 之后再 `StartAsync()`。一个字节都没少读,
只是不再和首帧抢那条队。超时是保险丝 —— 窗口没开起来(headless 测试、设计器)时照常启动插件,
退回信号引入之前的行为。已有的 `PluginManagerOptions.PrewarmDelay` 表达的是同一诉求
(注释原话「让主窗口先把首帧画完」),但它用的是拍脑袋的 5 秒定时;这里给出的是真正的信号。

代价:插件贡献的命令、协议与工作区晚约一帧出现。它们本来就异步登记(`StartAsync` 一直是
fire-and-forget),真正受影响的边角是冷启动瞬间到达的插件协议链接(如 `redis://`),
此前也已经是竞态,现在窗口更宽。

**二、R2R 改成可开关,并把账写进 csproj。** 新增 `VelaShellReadyToRun` 属性(默认 `true`,
行为与此前一致),`-p:VelaShellReadyToRun=false` 即可发一版不带 R2R 的包做对比。

⚠️ 这里修正了一个一开始想当然的判断:Defender 是在**程序集被装载时**扫它,不是启动时把整个
目录扫一遍。所以启动路径上根本不装载的程序集(`BouncyCastle` 只在首次 SSH 握手时用、
`FluentFTP` 只在 FTP 会话里用),它们的 R2R 膨胀**不花冷启动的钱** —— 把它们列进
`PublishReadyToRunExclude` 只能省包体和首次连接的 JIT,换不来首帧。因此**没有**放一份凭直觉
猜出来的排除清单:那是拿一个未知换另一个未知。

真正要回答的是「启动路径上那 ~55 MB 的 R2R 到底值不值」,而它有一个**不需要重新构建**的
判决性实验 —— 用同一份二进制跑,扫描成本因此完全恒定,差值就是纯 JIT:

```powershell
$env:VELASHELL_STARTUP_TRACE='1'; D:\VelaShell\VelaShell.exe   # 基线
$env:DOTNET_ReadyToRun='0';       D:\VelaShell\VelaShell.exe   # 忽略 R2R 本机代码,全部 JIT
```

两次都在热态下跑(连开两次取第二次)。拿差值和「R2R 多出来的字节 × 27.9 ms/MB」
(当前约 20 MB → ~560 ms)比:省下的 JIT 少于 560 ms,R2R 在 Windows 上就是净亏。
Linux/macOS 不受这笔账影响(没有 Defender 那一层),真要关也应当只关 Windows 这一支。

**三、`AvaloniaUI.DiagnosticsSupport` 那个从没生效的 Condition。** `VelaShell.csproj` 里
注释写着「Condition below is needed to remove Avalonia.Diagnostics package from build output in
Release configuration」,但 Condition **从没落到元素上**(注释是 Avalonia 模板带来的)。
后果:发行包里一直躺着 3.1 MB 的 `AvaloniaUI.DiagnosticsSupport.Avalonia.dll`,
`VelaShell.runtimeconfig.json` 里 `Avalonia.Diagnostics.Diagnostic.IsEnabled` 也一直是 `true` ——
**DevTools 在用户手上的版本里是开着的**。补上 `Condition="'$(Configuration)' != 'Release'"`;
全仓没有任何代码引用 `Avalonia.Diagnostics` 的 API(纯 MSBuild/runtimeconfig 集成),不影响编译。

验证:

- `dotnet build src/VelaShell -c Debug` 零警告零错误。
- `dotnet msbuild -getProperty:PublishReadyToRun`:Release+RID → `true`;
  加 `-p:VelaShellReadyToRun=false` → 空;Release 无 RID → 空(NETSDK1095 的退路不变)。
- `dotnet msbuild -getItem:PackageReference`:Debug 含 `AvaloniaUI.DiagnosticsSupport`,
  Release 不含。
- `VelaShell.Tests` 1405 通过 / 0 失败。`VelaShell.Infrastructure.Tests` 471 通过 /
  1 失败 —— 失败的是 `AreSequenceEqualAwaitTests`(看门狗用例,断言上游 MSTest 的 bug
  仍在;它现在报「已修好」),与本次改动无关,按该文件自己的说明另行清理。
- **没有验证的**:真实冷启动的收益数字。本机无法可靠地把页缓存与 Defender 扫描缓存一起清空,
  必须重启后实测 —— 见下一条。
- **待用户实测**:重启后冷启一次,对比日志里的 `[Startup] timeline`,重点看
  `WindowOpened → FirstFrame` 这一段(基线:冷 1,165 ms / 热 391 ms)。
  作为对照,`Add-MpPreference -ExclusionPath 'D:\VelaShell'` 后再冷启一次,能量出
  Defender 在这台机器上占的全部份额(预期 ~4 s),从而给「要不要买证书」定价。

## ✅ 79. 2026-09-18 四种出站代理按规约对齐:system 是解析器,none 必须真直连(用户反馈 #464)

用户(#464)反馈:Clash Verge 开着系统代理时,VelaShell 选「系统代理」连不上海外服务器,
改成「无代理」就正常;并称切换代理模式后必须重启应用才生效。

**先把链路拆开量了**(本机就装着 Clash Verge,system proxy `127.0.0.1:7897`,rules 模式,TUN 关):

| 测量 | 结果 |
| --- | --- |
| `HttpClient.DefaultProxy` 解析 `hw.easilynet.top:22` | `http://127.0.0.1:7897/` —— 协议、端口都对 |
| Clash 日志 | `hw.easilynet.top:22 match Match using 节点选择[新加坡-优化3]` —— CONNECT **确实到了 Clash** |
| **直连** `hw.easilynet.top:22` | `SSH-2.0-OpenSSH_10.2p1 Ubuntu-2ubuntu3.6` |
| **经 Clash** CONNECT 同一目标 | `200 Connection established` 之后**一个字节都没有**(超时) |
| 经 Clash CONNECT 到 `ssh.github.com:443` | 横幅正常,能走到主机指纹校验 |

结论:HTTP CONNECT / 环回中继**没有 bug**,CONNECT 建得起来;是那个节点到不了目标 `:22`
—— 也就是**代理节点侧的出口问题**,不是 VelaShell 的解析或隧道问题。

用户随后给了一份**出站代理规约**,据此把四种模式逐个对齐(期间我先按"system 只作用于 HTTP 通道"
实现过一版,与规约冲突,已回退):

| 模式 | 位置 | 覆盖范围 | 本次落实 |
| --- | --- | --- | --- |
| **none** | 直连 | — | **真直连**:不回看系统代理,也不读 `HTTP_PROXY` / `ALL_PROXY` |
| **system** | OS 设置 | **全部出站** | **解析器不是协议**:折成 http 或 socks5;系统是 SOCKS 就按 SOCKS5,不硬套 CONNECT;解析不出(没配 / bypass / PAC 失败)则直连 |
| **http** | 第 7 层 | HTTP/HTTPS + `CONNECT` 裸 TCP 隧道 | 不支持 UDP;FTP 已强制被动模式、数据连接走同一条 CONNECT |
| **socks5** | 会话/传输层 | TCP | SSH / FTP 挂代理时的首选;`socks5h` 语义(远程解析) |

改动落点:

- `ProxyResolver.Resolve`:`system` 重新作用于**全部出站 TCP**(SSH / SFTP / FTP 控制与数据 / HTTP),
  只有 HTTP 通道传 `schemeHint` 用于让按 URL 分流的 PAC 问得准;裸 TCP 按 `http` 探针问,
  **PAC 回 DIRECT 是合法答案**,照它直连,不当失败。
- `system` 的解析结果按 scheme 落成 http 或 socks5(`MapProxyScheme`),不再在系统是 SOCKS 时硬套 CONNECT。
- `none` 在 `Resolve` 里直接短路成 `Direct`,注释写明"不再回看系统代理,也不读环境变量";
  进程级 `HttpClient.DefaultProxy` 已被 `VelaWebProxy` 接管,因此 .NET 那边也不会有环境变量兜底。
- **系统代理变更无需重启**:数据源是实时的 `HttpClient.DefaultProxy`(Windows 给 Internet Settings
  注册了变更通知),`ProxyResolver` 每次解析重读;另实测把 `ProxyEnable` 置 0/1,同一个代理实例立刻改口。
- **DNS 默认远程解析**(`ProxyDns` 默认开 = socks5h 语义),只有 `none` 本机解析。
- 代理只作用于**第一跳**:跳板链上真正出 TCP 的只有最内层那一跳(原本就是这样,已补文档)。
- **代理设置变更后把「连接失败」的 SSH 标签自动重连一次**(`ProxyChanged` 逐字段比对;
  断开标签可能是用户意图,本地终端与插件协议不看这份设置)。
  ⚠️ 这一条的**第一版是死代码**:按 `SessionStatus.Error` 筛标签,而终端标签根本没有那一态 ——
  一个都选不中,功能从未触发过。见 §82。

文案与文档同步:五份 resx 的 `SetProxy_Subtitle` / `SetProxy_TypeDesc`、AI 插件 `Loc.cs` 的
unreachable 提示、`velashell-docs` 的 `zh/en/host/architecture.md`(补了四种模式的对照表)。

**明确没做的**(规约里是"建议",且与既有产品决策冲突,留待确认):

- **每条会话覆盖全局代理**:规约的"建议产品模型"里有,但 `feature-plan.md` 已把「按会话的独立代理」
  记为**不做**(落地为应用级全局)。要做需先推翻那条决策。
- **「测试代理」按钮按协议分别探**(HTTP `CONNECT` 看 200 / SOCKS5 握手 / system 先打印解析结果再测):
  尚无此按钮;目前只有 SSH 侧的连接测试与会话内报错。
- Windows 的 **WinHTTP 机器级代理**:我们跟随 .NET 的 `HttpClient.DefaultProxy`(Windows 为
  Internet Settings + PAC,即桌面应用与浏览器看到的同一份)。WinHTTP 的机器级静态配置
  (`netsh winhttp`)对桌面应用基本是空的,且常被安装器写脏 —— 有意不合并,规约里把它列为易错点,
  这里按"跟随桌面应用一致的来源"取舍。

验证:

- `Infrastructure.Tests` 481(478 通过 / 3 跳过)、`Core.Tests` 441(437 / 4)、
  `VelaShell.Tests` 1426(1410 / 16),零失败;本地化键位对齐用例(8 条)通过。
- 新增/重写代理用例:`Resolver_SystemType_HttpSystemProxy_CoversBareTcp`(SSH/FTP 跟随系统代理)、
  `Resolver_SystemType_SocksSystemProxy_ResolvesToSocks5`(系统是 SOCKS 不硬套 CONNECT)、
  `Resolver_NoneType_IgnoresSystemProxyEntirely`(none 真直连,HttpClient 侧也返回 null)、
  `Resolver_SystemType_ProbeFailure_FallsBackToDirect`(PAC 失败明确回退直连)、
  `Resolver_SystemType_DefaultsToRemoteDns`(默认远程解析)、
  `Resolver_ProxyTypeChange_TakesEffectWithoutRestart`(同实例内换快照立即改口),
  以及 `ProxySettingsHotApplyTests`。
- **没有验证的**:真实服务器 + Clash 下"改完设置自动重连"的观感 —— 本机没有可连的凭据;
  经 Clash 到 `hw.easilynet.top:22` 拿不到数据是节点出口问题,客户端侧无法修复。

### §79 追加(2026-09-18 晚):把"经系统代理连不上"钉到端口级

用户追问"为什么开了系统代理还是连不上"。同一台机器、同一个 Clash,逐项再量一遍:

| 路径 | 结果 |
| --- | --- |
| HTTP CONNECT `hw.easilynet.top:22` | `200` 之后**静默黑洞**(6 s 无任何字节,连 RST 都没有) |
| **SOCKS5**(同一混合端口)`hw.easilynet.top:22` | 应答"granted",之后同样**静默黑洞** —— 不是 HTTP CONNECT 被特殊限制 |
| HTTP CONNECT **直接用解析出的 IP** `154.12.41.59:22` | 同样隧道建立后无数据 —— 与 DNS / 域名规则无关 |
| HTTP CONNECT `hw.easilynet.top:443` | 隧道建立后**立刻被关**(那里没有服务,RST 能穿透节点传回来) |
| HTTP CONNECT `hw.easilynet.top:80` + `HEAD /` | 收到 **`HTTP/1.1 200 OK`** —— 该节点**能**到达这台主机 |
| HTTP CONNECT `feeds.easilynet.top:443` | TLS 1.3 握手完成 |
| **直连** `hw.easilynet.top:22` | 收到 `SSH-2.0-OpenSSH_10.2p1` |

结论:节点到得了这台主机(:80 有正常 HTTP 响应、:443 的"无服务"也会传回 RST),
**唯独 :22 是被丢弃(blackhole)的**。所以要么机场/节点出口丢弃 22 端口(常见策略),
要么服务器防火墙对该来源 IP 只在 22 上丢包 —— 两者都在 VelaShell 之外。
客户端侧能做的只有:选「无代理」、在 Clash 里给这台主机加 `DIRECT` 规则,或换一个放行 22 的节点。

⚠️ 曾据此给报错补过一段说明(中继加 `TunnelEstablished` + 新键 `Msg_ProxyTunnelSilent`,
指出"隧道已建立但对端没回音、代理出口可能丢了该端口"),**维护者要求移除,已回退** ——
报错保持原样,只由平台侧的连接超时/关闭语义呈现。

## ✅ 80. 2026-09-19 网络代理页:说明文字被裁 + 补一块「四种模式的区别」(用户截图反馈)

用户截图:副标题与「代理类型」的说明都跑到卡片外面。

**根因是 `TextBlock.page-subtitle` 漏了 `TextWrapping`。** `TextBlock.row-desc` 早就设了
`Wrap`,其样式注释也写明「不换行的话长句子会顶出设置行右边的控件、跑到可视区外
(内容区只有纵向滚动条,横向没有可以救)」—— 但 `page-subtitle` 一直没跟上。
一句长副标题(网络代理页那句 46 字)按**单行自身宽度**量,超出的部分被直接裁掉,
同页的 `row-desc` 也因此紧贴右侧控件。这条按**类**修,所有设置页一并受益。

headless 里量到的差别(网络代理页,视口 714px):

| | 副标题 Desired | 结果 |
| --- | ---: | --- |
| 修复前 | 658 × 14 | 只有一行,文字被裁 |
| 修复后 | 645 × 28 | 两行,完整显示 |

同时:
- 「代理类型」那行的左列加了 `Margin="0,0,16,0"`,说明换行时不再贴着下拉框。
- 页面下方补了一块**四种模式的区别**。第一版做成只读文本框,用户反馈「太乱」——
  项目符号后的长句换行后从行首续写,眼睛找不到下一项从哪开始。改成**表格**
  (`Border Classes="table"`,与密钥 / 版本历史同款):模式名对齐成一列
  (直接复用下拉框那套词 `SetProxy_TypeNone/…`,不另造一份译名),
  说明在右列自动换行;表格下再加「覆盖范围」「怎么选」两条注。
  文案因此从单个长文本键换成 `SetProxy_Mode{None,System,Http,Socks5}Desc` +
  `ModesScope` / `ModesPick`,五语齐。

验证:

- 新增 `ProxySettingsPageUiTests`(headless):本页所有说明文字必须 `Wrap`;
  副标题必须真的折成多行(只设属性而布局没生效也会红);说明区是**四行表格**、
  模式名对齐成一列(四行名字列宽一致)、每行说明都换行;并断言正文覆盖四种模式的判别特征。
  **反向验证过**:把 `page-subtitle` 的 `TextWrapping` 去掉,前两条立刻变红并打印出被裁的句子。
- `Infrastructure.Tests` 485(482 / 3 跳过)、`Core.Tests` 441(437 / 4)、
  `VelaShell.Tests` 1429(1413 / 16),零失败。

## ✅ 81. 2026-09-19 终端核心回路:一次 SIMD 调研,五个候选三项被实测否决(性能调研)

给终端引擎找 SIMD 加速点,逐条实测之后:五分之三的「直觉热点」被证伪,真正落地的只有两条
「把手写算法换成 BCL 已向量化的 API」和一条「BCL 覆盖不到的结构体特化」。先摆结论:

| 项 | 结果 | 依据 |
| --- | --- | --- |
| A1 `TerminalRow.TrimToContent` 向量扫描 | ✅ 落地 | 2.6x / 1.6x;BCL 替代反而慢 4x |
| A2 `Fill` / `FillRange` → `Span<T>.Fill` | ✅ 落地 | −26%;手写 SIMD 实测持平 |
| A3 脏行检测 / 子可视化分带 | ❌ 不做 | 实测无「全量重算」可救 |
| B5 `EchoSuppressor.IndexOf` → `Span.IndexOf` | ✅ 落地 | 16.9x |
| B6 UTF-8 ASCII 手写快路径 | ❌ 不做 | BCL 已向量化,手写净值≈0 |

**A1 —— 唯一一处手写向量成立。** `TrimToContent` 是行退休进回滚区时从尾往前找最后一个
非 `default` 格的热路径。因为 `TerminalCell` 是 16 字节无填充、且 `TerminalColor.Default`
打包恒为 0,「格 == default」与「这 16 字节全是 0」等价,于是把 `_cells` 当字节流、用
`Vector<byte>` 成块判零,命中非零块后块内**按格**定位(而不是逐字节回扫 —— 逐字节回扫在
内容恰好顶到行尾的满宽换行行上会退化成几十次比较,反而比一次结构体比较还慢)。
实测(200 列,`Vector<byte>.Count`=32):典型日志行(20 格内容)2.6x,近满行(190 格)1.6x,无退化。
⚠️ **反向教训**:`Span<T>.LastIndexOfAnyExcept<T>` 这类泛型 `IEquatable` API 在 16 字节结构体上
比旧标量循环**慢约 4 倍** —— 「换成 BCL 更保险」在这里是错的。

**A2 —— 结论反直觉:手写 SIMD 广播是负收益。** `Fill` / `FillRange` 原先是手写 `for` 逐格赋值。
实测(200 格 × 16 B):手写 `Vector128` 展开写 45.9 ns,与手写循环 45.9 ns **完全持平**,
`Vector256` 也只 37.9 ns;而 `Span<T>.Fill`(BCL,已向量化)33.8 ns。正解不是加 SIMD,是把落单的
手写循环删掉、交给 BCL —— `ResetFor` 早就这么写了,`Fill` / `FillRange` 是仅剩的两处。

**A3 —— 不做,而且最初那个「几万格子每帧全量重算」的直觉是错的。** 用真 Skia 后端
(`VelaShell.Terminal.RenderTests`)量:纯字母满屏一帧只记录 **54 个绘制操作**(51 个字形 run,
约一行一个),逐格工作早被画刷 / 画笔 / 字形 / 语义 / 侧栏文本五套跨帧缓存与跨帧复用的
GlyphRun 缓冲压掉了。真正要省下绘制,得走 Avalonia 的**子可视化元素**分带(`CursorOverlay`
为光标闪烁就单拆一层,正是因为没有别的局部失效手段),那要同时穿过滚动、选区、搜索高亮、光标层、
侧栏、折叠六个子系统 —— 代价与实测收益不成比例,详见 feature-plan.md「确认不做」。
调查中顺带踩了个坑:无头测试项目 `VelaShell.Terminal.Tests` 没有真实字体,量出来满屏 2700+ 个
run、139 ms、46 MB,像合批彻底坏了;换到真 Skia 后一切正常。**渲染性能结论只能在 RenderTests 取。**

**B5 —— 16.9x,唯一的大收益。** `EchoSuppressor.IndexOf` 是手写朴素子串搜索(每碰到首字节相同
就逐字节往下比,针越长退化越狠)。换 `Span.IndexOf`(BCL 向量化),64 KiB 缓冲上 25.6 µs → 1.5 µs。
⚠️ 旁边的 `MatchFrom` **故意保留标量**:它每碰到首字节相同的位置就要调一次,绝大多数在第 2 字节
就失配,实测标量 1.9 ns 与 `SequenceEqual` 2.0 ns 持平,换不换无差,留早退语义更直白的这版。

**B6 —— 不做。** `Decoder.GetChars` 对纯 ASCII 已达 ~26 GB/s(比标量加宽快 10 倍,这就是它
已向量化的直接证据),手写 `Vector.Widen` 只快 24% 且处理不了非 ASCII,加一趟「是否全 ASCII」
校验就把这点吃干净。顺带发现 `GetCharCount` 是一趟冗余的完整扫描(约 25%),但按桥接 16 KiB
分块算绝对开销只有几百 ns / 块,为它引入「跟踪 Encoding 求安全上界」的复杂度不划算。

**全局经验:** 这个代码库里「直觉上的 SIMD 热点」基本已经被 BCL 或既有缓存覆盖掉了;真正的收益
来自**把手写算法换成 BCL 已向量化的重载**(B5、A2),以及**少数 BCL 覆盖不到的结构体特化
场景**(A1)。两种方向都得先量 —— 手写未必更快,换 BCL 也未必更保险。

验证:

- `VelaShell.Terminal.Tests` **505 通过**,本次新增 5 条:`TerminalCellMemoryTests` 4 条覆盖
  `TrimToContent` 的向量边界(内容格挪遍整行每处 / 全空行 / 空格上的 OSC 8 链接 / 窄行),
  `EchoSuppressorTests` 1 条钉住 `IndexOf` 的边界语义,含「空针 → -1」这个与 BCL 相反、必须守住的约定。
- `VelaShell.Terminal.RenderTests` 5 通过。
- 构建零警告。

## ✅ 82. 2026-09-20 资源管理器的折叠与置顶、rm -rf 删目录、路径栏复制(用户需求 #474)

Issue #474 一口气提了五条,标题只写了第一条。逐条落地四条,第三条按维护者既有决策不做:

| # | 诉求 | 处置 |
| --- | --- | --- |
| 1 | 启动时默认折叠分组 | ✅ `设置 → 常规` 新开关 |
| 2 | 置顶连接 | ✅ 右键置顶,提到整棵树最前 |
| 3 | SFTP 拖拽移动文件 | ❌ 维护者既有决策:太容易误触发,"文件乱飞",早先就是因此关掉的。见 feature-plan.md「确认不做」 |
| 4 | 删目录走 `rm -rf` | ✅ SSH 会话上的快路径,失败自动回退 |
| 5 | 路径栏加复制按钮 | ✅ 命令早就有,只差一个按钮 |

**1 与 2 是一件事,不是两件。** 起初想把置顶做成「在组内上浮」—— 改动最小。但把它和第 1 条摆在一起
就露馅了:分组全折上之后,组内上浮的置顶项照样看不见,等于没置顶。所以置顶最终做成
**提到整棵树的最前**,与折叠配套才有意义。`SessionTreePinAndCollapseTests` 里那条
`PinnedSessionInACollapsedGroup_IsStillVisible` 钉的就是这条性质。

**置顶只动"摊平"那一层,不动树本身。** 会话树是 `Nodes`(两层:分组 → 会话)经 `SyncRows`
摊成 `Rows` 给界面画。置顶的实现整个落在 `SyncRows` 里:先把置顶的会话摊在最前,再摊其余节点,
且跳过已经摊过的那些。节点在 `Nodes` 里的位置**一点没动** —— 于是分组归属、拖放落点解析、
「分组空了就连分组一起删」这些规则一条都不用改,`SessionProfile.GroupId` 也原样保留,
取消置顶它就回到自己那一组。
⚠️ 一个必须守住的点:同一个节点实例在 `Rows` 里只能出现一次。摊两遍(置顶区一次、组内再一次)
会让列表控件的选中与容器复用错乱,`PinnedSessionInAnExpandedGroup_AppearsExactlyOnce` 守这条。
缩进另开了 `ShowsAtRootIndent`(= `IsRootLevel || IsPinned`):`IsRootLevel` 仍是**数据事实**
(这条会话没有分组),而缩进要跟着**显示位置**走,两者在置顶的组内会话上第一次分岔。

**折叠设置必须配一份进程内的展开态记忆,否则它会变成骚扰。** 设置说的是「应用打开时」,
而 `LoadTreeAsync` 被调用的时机远不止启动:新建 / 编辑 / 删除连接、云同步回来都会重建整棵树。
只看设置的话,每加一条连接就把刚展开的分组又折回去。所以 `SessionTreeViewModel` 记一份
`_groupExpansion`:重建时**记忆优先、设置兜底**。记忆只活在进程内 —— 重启后本就该回到设置说的状态。

**`rm -rf` 是快路径,不是替换。** 删一棵上万文件的树,SFTP 这条路要先把整棵树列一遍算总数
(为了那根进度条),再对每个条目发一次 `SSH_FXP_REMOVE`/`RMDIR`;往返次数与文件数成正比。
`rm -rf` 是一次往返。但它只在**有 exec 通道的 SSH 会话**上成立,所以做成快路径,四道闸:
只对真目录(链接仍按叶子删,语义与原来一致)、`GetClient` 拿得到 SSH 客户端、设置允许、
路径是绝对路径且不是根。任何一条不满足,或者命令退非零码 / 抛异常,一律回退到原来的 SFTP 递归 ——
**回退路径会把真正的失败原因带出来**,而不是一句没有上下文的 "rm: exit 1"。
路径按 POSIX 单引号包起来(`'` → `'\''`),空格、`$`、`;`、通配符在 shell 那一层全部失去特殊含义。
代价如实写进了设置项说明:这条路上没有逐条进度(进度条转不确定态),取消只能关通道、远端的 rm 可能已经删完。
二次确认不受影响,照旧。

⚠️ **一个被 NSubstitute 掩盖的测试陷阱**:`ISshConnectionService.GetClient` 返回接口,
而 NSubstitute 的**递归替身**会给接口返回值凭空造一个替身 —— 不是 null。于是既有的
`SftpServiceTests`(本意是"纯 SFTP、没有 exec 通道")在加了快路径之后会悄悄拐进 `rm -rf` 分支。
表现是 `DeleteAsync_RecursivelyDeletesDirectoryContents_AndReportsProgress` 多出一条进度回报。
修法是把意图写明:`GetClient(...).Returns((ISshClientWrapper?)null)`。
**凡是"这个依赖不存在"的用例,null 都得显式写出来。**

**第 5 条只差一个按钮。** `CopyCurrentPathCommand` 早就有(空白区右键那一项),
只是没挂到路径栏上;用户原先要复制路径得先点铅笔进编辑态再手动选中。铅笔旁边加一枚
`Icon.copy` 绑上去即可,复用既有的 `CopyCurrentFolderPath` 文案,零新增字符串。

验证:

- 新增 `SftpRecursiveDeleteCommandTests` 9 条(快路径成立 / 只报不确定进度 / 退非零码回退 /
  抛异常回退 / 无 exec 通道 / 设置关闭 / 单文件不走命令 / 引号与空格的转义 / 根目录不走命令)。
- 新增 `SessionTreePinAndCollapseTests` 12 条(折叠开关两态 + 无设置服务 / 手动展开与手动折叠
  各自扛过一次重建 / 置顶上浮 / 折叠时仍可见 / 只出现一次 / 根级缩进 / 置顶与取消置顶落库 /
  分组行上命令不可用)。
- 新增 `PinAndCopyPathBindingUiTests` 2 条守 XAML 侧的反射绑定(右键置顶项、路径栏复制按钮)。
  ⚠️ 用带返回值的 `Dispatch(async () => { …; return true; })`,并**实测**过插一条
  `Assert.Fail` 确实会让用例红 —— 无返回值写法会拿到一个从未被等待的 `Task<Task>`。
- headless 截图人眼核对过三张:分组全折叠 + 两条置顶、全展开、SFTP 路径栏。
- 五份 resx 各补 6 个键;`dotnet build VelaShell.slnx` 零警告,`dotnet test VelaShell.slnx` 全绿。

## ✅ 82. 2026-09-20 复查 #464 的修复:自动重连那一条是死代码,另修三处(代码复查)

对 §79 / §80 那批改动做了一次复查。代理解析层(`ProxyResolver` / `VelaWebProxy` / SSH / FTP /
文案 / 文档)站得住,但**四项承诺里有一项从未执行过**,另有三处不准确或不可靠。

### P0:「改完代理自动重连失败的 SSH 标签」一次都没触发过

筛选条件写的是 `tab.ConnectionStatus != SessionStatus.Error → continue`,
而**终端标签没有 `Error` 这一态**:连接失败走 `MarkConnectionFailed`,它写的是
`Disconnected` + `ConnectionError`;失败覆盖层(`ShowDisconnectedOverlay` /
`DisconnectOverlayTitle`)认的也是后者 ——「连接失败」与「连接已断开」这两句话的区别
就在 `ConnectionError` 有没有值。`SessionStatus.Error` 全仓库只落在 `ConnectingDocument.Status`
与资源管理器树节点上,从没落到 `TerminalTabViewModel` 上。于是整个循环恒 `continue`,
issue 里对报告人承诺的那条能力实际不存在。

写注释时把「失败覆盖层还留在那儿」直接推成了「状态是 Error」,而覆盖层接受
`Disconnected or Error` 两种 —— 这一步推理没有任何测试拦得住,因为当时的用例
(`ProxySettingsHotApplyTests`)只测了纯函数 `ProxyChanged`,一行都没碰筛选逻辑。

修:

- 判据抽成 `ReconnectPolicy.ShouldReconnectAfterProxyChange(isDisconnected, hasConnectionError,
  userRequestedDisconnect, isLocalShell, remoteShellExited)` —— 和 `ShouldReconnect` 放同一处,
  正是这个类存在的理由(条件散落两处就会漂)。不看 AutoReconnect 总开关:这是用户刚做出的动作,
  不是后台自动行为。
- 选取抽成 `MainWindowViewModel.FailedSshTabsAfterProxyChange(tabs)`,可以拿**真实标签**测。
- 顺带补上 `ResetReconnectAttempts()`:用户显式重试不该继承上一轮烧掉的次数额度。
- 用例三层:`TerminalTabViewModelTests` 钉住「失败后是 `Disconnected` + `ConnectionError`,
  不是 `Error`」这个前提;`ReconnectPolicyTests` 钉判据;`ProxySettingsHotApplyTests` 拿真标签
  走一遍真实失败路径钉选取。**做过变异验证**:把判据改回 `SessionStatus.Error`,后两条立刻红。

### 另外几处

- **`IsLoopback` 的注释是错的,新增代码是冗余的。** 注释称在补 `IPAddress.IsLoopback`
  「漏掉的 127.0.0.2」。.NET 11 实测 `IsLoopback(127.0.0.2)` 与 `IsLoopback(::ffff:127.0.0.1)`
  **都是 true**(它比的是首字节),`TryParse` 也接受 `[::1]` 这种 `Uri.Host` 形式。
  真正新增的只有 `Trim()` 与尾点 FQDN。已按事实重写注释、删掉冗余分支,用例补上
  `127.0.0.1.` / 前后空格 / `[::1]`。
- **`FormatHost` 会给已带方括号的 IPv6 再套一层。** `VelaWebProxy` 传的就是 `Uri.Host`
  (IPv6 时自带方括号),`[[::1]]` 会让 `Uri` 构造直接抛 —— 探针那条被 catch 吞成直连,
  `GetProxy` 那条(系统代理本身是 IPv6 端点)会抛到调用方。已先认方括号再决定加不加。
- **代理配错的识别改用专用类型。** 原先 FTP 侧拿**本地化后的消息文本**比对
  (`ex.Message == Strings.Get("Msg_ProxyMisconfigured")`),换一种界面语言即失效,
  而且漏掉了同批新增的「SOCKS5 凭据超 255 字节」——那条走的是另一个键,会被
  `FluentFtpInterop.Translate` 翻成笼统的「连接丢失」,正是这段代码想避免的事。
  新增 `ProxyMisconfiguredException`(**刻意继承 `InvalidOperationException`**,
  SSH 侧 `PrepareProxyRelay` 的既有 catch 照常命中),两处抛点统一,FTP 侧按类型判。
- **FTP 报错里的 `via` 改成记录实际用过的路由。** 原先是失败后重新 `Resolve` 一次,
  而 `system` 档跟随的是 OS **当前**代理 —— 两次调用之间用户可能刚把 Clash 关掉,
  报出来的就不是真正失败的那一条,反而把人往错方向带。改为拨号前写进 `RouteProbe`,
  与 SSH 侧 `_lastRoute` 的做法对齐;`WithProxyContext` 随之变成静态纯函数。
- **`LoopbackProxyRelay.Error` 补 `volatile`**(既有缺陷,顺手一并修)。它写在中继所在的
  线程池线程、读在发起方连接失败的 catch 里,原本是个普通自动属性。因果顺序本来就是对的
  —— 失败时先写 `Error`,再由 `finally` 的 `CloseStreams()` 关掉环回连接,而正是那次关闭
  才让 Tmds.Ssh 的连接失败 —— 但这条链上缺一道内存屏障,读侧理论上可能看见过期的 null,
  于是 `DescribeProxyError` 退化成一句光秃秃的"连接被关闭",#464 刚补上的 `via` 路由丢失。
  `volatile` 只能加在字段上,因此改成 volatile 后备字段 + 只读属性,与同类里的
  `_inbound` / `_outbound` 写法一致。

### 一处只补文档、不改行为

裸 TCP 的系统代理探针 scheme 在 §79 里从 `https` 改成了 `http`,这是**行为变更**而非等价改写:
Windows 上分协议配置(`http=A;https=B`)的用户,SSH / FTP 会从走 B 变成走 A;单端点配置
(Clash 这类)无差别。两者都是启发式 —— 裸 TCP 本就没有"正确"的 scheme 可报。保持现状,
但在 `BuildSystemProbe` 的注释里写明这是变更,免得下次复查又要重新推一遍。

验证:

- `Infrastructure.Tests` 488(485 通过 / 3 跳过),`VelaShell.Tests` 1439(1423 / 16);
  本次净增 13 条用例。
- 构建零警告。

## ✅ 83. 2026-09-20 换了台服务器、IP 没变:指纹变更从"报错"改成"弹窗裁决"(用户需求 #476)

报告人描述的是一件很常见的事:服务器换了新机器,IP 沿用,双击资源管理器里的会话 ——
VelaShell 把它当成一条连接错误,让人去排查服务器;而其它 SSH 工具在这里弹的是一句
`WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!` 加一个「接受并覆盖 / 不接受」的框。
他绕过去的办法是去 `.velashell` 里删数据。

**那个框 VelaShell 本来就有**(§13 第二批做的三选项弹窗),被一个默认值挡住了而已。

### 一、默认值:`BlockOnFingerprintChange` 由 `true` 改为 `false`

`AddHostAuthentication` 里指纹变更那一支原本是「开关开着 → 直接 `Reject`,不问」,而开关
**默认开着**。于是这条路上永远走不到弹窗。改默认值,并按本仓库既有的两次先例
(`RecordingOptInMigrated` / `Proxy.DefaultsMigrated`)加一次性迁移
`Security.FingerprintChangeDefaultMigrated`:存量配置里那个 `true` 分不清是用户选的还是
旧默认值带的,统一关一次、打标记,此后主动打开的阻断永远算数 —— 要严格 fail-closed 的人
把开关打回去,行为与旧默认一字不差。

### 二、弹窗要摆出旧指纹,还要说清两种可能

只喊一句"主机密钥已更改"对用户没有帮助:他判断不了这是自己刚重装的那台,还是路上有人。
- `IHostKeyService` 补 `FindKnownHostAsync(host, port)`(取单条,而不是让调用方去
  `GetKnownHostsAsync` 里翻),`IHostKeyPrompt.DecideAsync` 尾部补可选的 `knownFingerprint`;
- 弹窗新增「已记录指纹」一行(仅变更时显示,弱化配色与本次指纹并排),以及告警下面一段
  `HostKeyChangedAdvice` / `HostKeyUnknownAdvice` —— 前者点名"重装/换机"与"劫持"两种可能。

### 三、被拒时说人话,而不是一句 `UntrustedPeer`

握手回调只能返回 true/false,真实原因递不出来 —— 这与 §79 里代理失败遇到的是同一个形状,
于是照 `LoopbackProxyRelay.Error` 的套路做了块原因牌 `HostKeyPromptOutcome`(链上各跳共用,
`Volatile` 读写):
- **策略阻断**(用户根本没被问过)→ `Msg_HostKeyChangedBlocked`:点名哪台、旧指纹、新指纹,
  以及"去 设置 → 安全与审计 → 已信任主机 删记录,或关掉阻断开关"两条出路;
- **用户自己点的取消** → `Msg_HostKeyRejected`。

`TmdsSshClientWrapper.ConnectAsync` 的 catch 里,这块牌排在代理错误与算法回探之前。

### 四、跳板链走同一套策略(顺带修掉的既有缺陷)

`BuildProxyChain` 里那一跳原本是另写的一段校验:`v == Trusted || IsTrusted(...)`,
**未知与变更一律静默返回 false**。于是「只当跳板、从没单独连过的那台机器」第一次用就连不上,
而且不弹窗、不说原因。现在与终点共用同一个 `AddHostAuthentication`(为此它的签名从
`VelaConnectionInfo` 收窄成 `host, port` —— 跳板那一跳要的就只是这两样)。

### 五、弹窗开着的时候,连接超时还在跑

Tmds.Ssh 0.24 的 `ConnectTimeout` 覆盖**整个**握手,`HostAuthentication` 回调也算在内
(`RunConnectionAsync` 里那个 `Timer` 一路传到 `AuthenticateAsync`)。默认 30 秒,用户盯着
一条红色警告多想十几秒再点"永久信任",裁决刚落盘连接已经被判超时 —— 明明认了却报连不上。

不能改已经点着的计时器,于是 `ConnectAsync` 拆成一层薄壳 + `TryConnectOnceAsync(canRetry)`:
只在「用户在弹窗里认了指纹(`ApprovedAfterPrompt`)」且「这次失败确实是库内部超时而非调用方
取消」两条同时成立时补连一次,**且只补一次**(第二轮 `canRetry: false`,照常抛它自己的错)。
调用方取消必须原样上抛 —— 那正是用户刚刚叫停的那件事,把它当超时去重连等于跟用户较劲。

### 验证

- 新增 6 条用例:迁移的三条(默认关 / 存量 `true` 抬一次 / 抬过之后听用户)、
  `FindKnownHostAsync` 的单条取值与端口不串、弹窗旧指纹与两套建议文案。
- `dotnet build` 零警告;全量 `dotnet test` 通过(Core 449、Infrastructure 486、
  VelaShell.Tests 1438,其余不变)。
- 📄 velashell-docs 已同步(中英各 4 个文件):架构图那一支的 `立即拒绝(fail-closed)` 改口径、
  `设置项审计.md` 补第五批、`交互与界面规格.md` 的指纹确认一条重写、`Xshell兼容登录.md` 措辞。

## ✅ 84. 2026-09-20 `-newtab` 的标签名被当成了 URL:先到先得改成按可信度取用(用户反馈 #475)

JumpServer Client 4.1.6 把 VelaShell 配成 SSH 工具,网页点一下"连接",VelaShell 开了、却没有任何
连接提示。报告人贴出的命令行是这样的:

```
-newtab root@Linux[2026_09_20_09_45_18] -url ssh://JMS-x:一次性口令@192.168.1.1:2222
```

`-newtab` 后面那个是**标签名**,真正的目标在 `-url` 里。而 `XshellLaunchParser` 原本把 `-url` 与
`-newtab` 并在同一个 `case` 里、用 `url ??=` 收第一个,于是先出现的标签名占了坑,后面的 `-url`
再也挤不进来 —— 拿 `root@Linux[2026_09_20_09_45_18]` 去解析主机,`[` 过不了 `LooksLikeHost` 的
字符集检查,`ParseUrl` 返回 null,`TryParse` 也就返回 null:一次正常启动,没有连接,也没有错误。

这是"先到先得"在一个**顺序不由我们决定**的输入上必然会栽的地方。改法是把三处 URL 来源分开收,
最后按可信度择优:

- `-url` —— 调用方明说的目标,最高;
- 裸位置参数 —— URL 协议关联那条路(`exe -url "%1"`)之外,有调用方直接把 URL 甩在第一个参数上;
- `-newtab` —— 最低,因为它多半只是个标签名。

光分优先级还不够:`-newtab` 的值仍会在没有 `-url` 时被当成目标,而 `LooksLikeUrl` 只看"有没有
`://`、有没有 `@` 且不含空格",标签名 `root@Linux[…]` 正好全中。所以 `-newtab` 的值现在还要
**真能解析出主机**(`ParseUrl(...) is not null`)才收 —— 解析不出就只当标签名,原样丢掉。
`-url` 与裸参数不加这层:前者是明说的,后者本来就只在"看着像 URL"时才收。

⚠️ 顺带明确:`-newtab` 的标签名目前**只是被忽略**,没有拿去当标签页标题。要用它得给
`ExternalLaunchRequest` 加字段,并一路过单实例转发的那份 JSON,不在这次修复范围内。

### 验证

- 新增 4 条用例:#475 原样的命令行、`-url` 与 `-newtab` 两种前后顺序都由 `-url` 说了算、
  `-newtab` 确实带 URL 时仍然认、只有标签名时不拿一个解析不出主机的串去"连接"。
  回退源码文件跑一遍确认前两条确实红。
- `dotnet test tests/VelaShell.Infrastructure.Tests` 全绿(490 通过,3 条按环境跳过)。
- 📄 velashell-docs 已同步(中英各 1 个文件):`Xshell兼容登录.md` / `xshell-compatible-login.md`
  的调用形态表补上 `-newtab` 也可能带标签名,并写明三个来源的取用顺序。

## ✅ 85. 2026-09-20 复查 #475:同一个家族里另有三处静默失败(代码复查)

§84 只堵住了报告人踩到的那一条。把解析器架到一个探针上灌 23 条真实/刁钻命令行跑一遍
(`dotnet run probe.cs`,文件式应用直接引工程),又翻出三处**同一个症状**的洞 ——
窗口开了、不连接、也不报错,或者连上了却少带凭据:

| 输入 | 修之前 | 病因 |
| --- | --- | --- |
| `-url 10.0.3.21:2222` | 不连接 | 调用方**明说**了这是 url,只是省了 scheme,却被 `LooksLikeUrl` 挡住 |
| `-l root -pw -Abc123 -url …` | 连上但**密码丢了** | `PeekValue` 拒收以 `-` 开头的值 |
| `-url ssh://root@[fe80::1%25eth0]:2222` | 不连接 | IPv6 zone id 里的字母过不了 `LooksLikeHost` 的十六进制收紧 |

### 一、`-url` 的值不再挑剔长相

`LooksLikeUrl`(有没有 `://`、有没有 `@` 且不含空格)本来是给**裸位置参数**设的:要在 Avalonia
自己的参数、拖拽进来的路径里挑出一条 URL。套到 `-url` 上属于用错了地方 —— 调用方都明说是 url 了,
我们还去猜它像不像,猜错的代价就是一次静默失败。现在 `-url` 的值原样交给 `ParseUrl`,
`-url 10.0.3.21:2222`(按 ssh 兜底)照连。

### 二、三个来源改成逐条试,而不是「选中一条再解析」

上一条放开之后冒出个新问题:一个写坏的 `-url` 会把 `url` 变成非 null,于是 `-newtab`、裸参数、
`-f` 全都没机会了。改成 `ParseUrl(-url) ?? ParseUrl(裸参数) ?? ParseUrl(-newtab) ?? ParseSessionFile(-f)`,
高优先级那条解析不出主机时接着试下一条。**优先级没变,只是多了退让。**

### 三、`-newtab` 收紧到「必须带 scheme」

`-newtab root@webserver01` 这种标签名恰好能解析出主机,§84 那道「真能解析出主机才认」拦不住它,
于是会去连一台叫 `webserver01` 的机器。Xshell 官方语义里 `-newtab` 的参数本就是**会话名**,
所以现在只认带 `://` 的;不带的一律当标签名丢掉。确有调用方往 `-newtab` 里塞完整 URL,那条不受影响。

### 四、「下一个 token 是不是选项」按名字判,不按破折号

`PeekValue` 原本是「开头有 `-` 就不是值」。一次性口令是现发的随机串,`-Abc123` 完全正常,
被吞掉的结果是连上了却没带密码 —— 用户看到一个莫名其妙的登录框,还以为是堡垒机发错了。
改成认名字(我们消费的那几个,外加 `-e` / `-s` / `-ssh` 这些**刻意不实现**但调用方照发的),
反向也堵住:`-pw -e uptime` 里的 `-e` 不会被当成口令收下。

### 五、IPv6 zone id

`LooksLikeHost` 对带 `:` 的一段收紧到十六进制数字,是为了不让 `user:pass` 这样的凭据片段冒充
IPv6 主机(那条注释还在)。但 zone id 是 `eth0` / `en0` 这种网卡名,一收就把整条链路本地地址判死。
现在按第一个 `%` 拆开:地址那半照旧严格,zone 那半放宽到字母数字与 `. - _`;
并在 `TrySplitHostPort` 里按 RFC 6874 把 `%25` 还原成 `%`(URL 里是转义的,套接字要的是还原后的)。

### 验证

- 新增 6 条用例(累计 28 条),覆盖上表三条 + `-newtab root@webserver01` 不当目标 +
  `-pw -e` 不吞选项 + 写坏的 `-url` 分别退到 `-newtab` 与 `-f`。
- 23 条探针重跑:四条修好,其余 19 条逐字未变(含 #475 原样、华为 CBH、PuTTY 风格 `-ssh -P -pw`、
  口令里带 `@ : /` 与空格、`?folder=prod` 查询串、`SSH://` 大写 scheme)。
- `dotnet build VelaShell.slnx` 零警告;`dotnet test tests/VelaShell.Infrastructure.Tests` 通过
  (495 通过、3 条按环境跳过)。
- ⚠️ SecureCRT 的斜杠风格(`/SSH2 /L root /PASSWORD pw host`)仍然完全不认 —— 本兼容层只认 Xshell
  的调用约定,要不要接是另一件事,已记在 `feature-plan.md`。
- 📄 velashell-docs 已同步(中英各 1 个文件):`-url` 不挑长相、`-newtab` 必须带 scheme、逐条试的退让、
  以 `-` 开头的值、IPv6 zone id。

## ✅ 86. 2026-09-20 密钥生成默认给 Ed25519(`feature-plan.md` 🔒 P2 项)

密钥管理页那个「生成密钥」按钮从第一天起就只会产出 RSA 4096。`ssh-keygen` 自 9.5(2023)
起默认给的已经是 Ed25519,我们还在给一把生成要等秒级、公钥贴进 `authorized_keys` 长达七行的
RSA —— 而且有些 CA / 堡垒机近年已经开始拒收 `ssh-rsa` 签名算法。现在默认换成 Ed25519,
RSA 保留为可选项。

### 一、为什么最终引了 BouncyCastle

`feature-plan.md` 那条把选择列为「自行实现 OpenSSH 私钥封装格式,或引入 BouncyCastle
(注意许可证与体积)」。两个顾虑现在都不成立:

- **体积**:BouncyCastle **本来就是 Tmds.Ssh 的依赖**,`BouncyCastle.Cryptography.dll` 早就躺在
  输出目录里。这次只是把传递依赖抬成 `src/Directory.Packages.props` 里的显式依赖,增量为零。
- **许可证**:MIT 改写版,与本仓库的双许可不冲突(对照当初拒掉 `FluentFTP.GnuTLS` 的理由)。

而「自行实现」指的其实是两件事,得拆开看:**OpenSSH 的私钥封装格式**本来就已经自己实现了
(`OpenSshPrivateKey`,为了把导入的 PEM 转成 Tmds.Ssh 唯一认的格式,见 §"导入私钥兼容转换"),
这次只是多加一个 `SerializeEd25519`;真正需要外援的是**由 32 字节种子导出公钥**那一步的曲线标量乘法
—— .NET 11 的 BCL 至今没有独立的 Ed25519(只有 `CompositeMLDsaAlgorithm.MLDsa44WithEd25519`
那个复合算法标识符),而手写曲线运算是那种**能把私钥悄悄写废、还一路绿灯**的地方。
种子仍旧取自 BCL 的 `RandomNumberGenerator`。

⚠️ 中央包里 `BouncyCastle.Cryptography` 的版本**必须跟着 Tmds.Ssh 走**,别让显式引用反向压低它。

### 二、接口从「生成 RSA」改成「生成密钥」

`GenerateRsaKeyAsync(name, bits)` → `GenerateKeyAsync(name, algorithm = Ed25519, rsaBits = 4096)`,
新增枚举 `SshKeyAlgorithm { Ed25519, Rsa }`。不留两个并列的生成方法,是因为它们的差别只是一个参数。
界面那个按钮不带选项、直接走默认值;RSA 这一路仍然完整可用(老堡垒机把 `ssh-rsa` 写死进白名单的
情况还在),只是要从 API 指定。自动命名也从 `velashell_rsa` 改成 `velashell_ed25519`
—— 老用户 `~/.ssh` 下那把 `velashell_rsa` 不会被动到,照常列出、照常可用。

### 三、一个躲在结构合法背后的坑

OpenSSH 的 ed25519 私钥字段存的是 **seed ‖ pub 共 64 字节**,不是那 32 字节种子本身
(`ssh-keygen` 沿用 NaCl 的 `crypto_sign` 习惯)。只写种子的话:文件结构完全合法、
Tmds.Ssh 也加载得动 —— 一路绿灯,直到真去连一台服务器才以签名验证失败告终。
所以 `SshKeyServiceFormatTests` 里**单独**有一条把封装拆开对字节的用例,不依赖「能加载」这个信号。
另:公钥在 blob 里是**定长字节串,不是 mpint**,不裁前导零也不补符号位。

### 验证

- 新增 3 条用例:默认算法的生成/列举/删除回合、Ed25519 的 OpenSSH 封装逐字节拆解;
  原先那条 Tmds.Ssh 真加载的格式用例改成 `DataRow` 覆盖 Ed25519 与 RSA 两路。
- **系统自带的 OpenSSH 交叉验证**(这条比任何单元测试都硬):`ssh-keygen -y -f <私钥>`
  从我们写出的私钥反推公钥,与我们写出的 `.pub` **逐字节相同**;`ssh-keygen -lf` 给的指纹
  也与 `SshKeyInfo.Fingerprint` 一致(`256 SHA256:… (ED25519)`)。RSA 那一路同样对得上,
  证明重构没有把 e / n 写反。
- `dotnet build VelaShell.slnx` 零警告零错误;`VelaShell.Core.Tests` 70 通过、
  `VelaShell.Infrastructure.Tests` 496 通过 / 3 条按环境跳过。
- ⚠️ **ECDSA 生成仍然没做**(`feature-plan.md` 原条目是「ed25519 / ecdsa」)。
  `OpenSshPrivateKey.SerializeEcdsa` 其实已经在了(导入转换那条路上用着),接上去是小事,
  只是 Ed25519 之后它的实际需求近乎为零,留在 `feature-plan.md` 里。
- 📄 velashell-docs **待同步**:密钥管理页的行为描述(生成的是 Ed25519 而不是 RSA)。

## ✅ 87. 2026-09-20 把密钥生成做完整:ECDSA 三条曲线 + 界面给算法下拉(接 §86)

§86 只换掉了默认算法,留了两处明说的缺口:ECDSA 生成没接,界面不给选。两条都补上了,
`feature-plan.md` 那条「ed25519 / ecdsa 密钥生成」现在整条闭合。

### 一、ECDSA:曲线表只留一份

`OpenSshPrivateKey.SerializeEcdsa` 本来就在(导入 PEM 转 OpenSSH 那条路上用着),只是私有。
改成 public,再把内部拼公钥 blob 那几行拆成 `BuildEcdsaPublicBlob` —— 生成路要把同一个 blob
写进 `.pub`,不拆就得抄第二遍。

更要紧的是**曲线表**:位数 →(算法名, 曲线名, 坐标定长)原本写死在 `TryLoadEcdsa` 里,
生成路再抄一份就是两份。提成 `DescribeCurve(int keySize)`,两边共用。
⚠️ P-521 的坐标是 **66** 字节(521 位向上取整),不是 65 —— 这类数字抄第二遍就是抄错的开始。

`bits` 的语义顺手统一了:`rsaBits` 改名 `bits`,**0 表示按算法取默认值**(RSA 4096、ECDSA 256),
Ed25519 定长、一律忽略。SSH 只定义了三条 NIST 曲线的算法名,给别的位数连个能写进公钥行的
名字都没有,所以 ECDSA 不像 RSA 那样收任意位数,直接抛。

### 二、界面:下拉的唯一事实来源在视图模型

工具栏加了个算法下拉(Ed25519 / ECDSA 256·384·521 / RSA 4096),默认第一项。
**位数不给选**:RSA 只给 4096 —— 4096 能用的地方 2048 一定能用,反过来不成立,列出来只是个坑。

档位表 `SshKeyManagerViewModel.AlgorithmChoices` 是唯一事实来源(算法、位数、建议文件名、文案键),
axaml 里那串 `ComboBoxItem` 只按同样顺序摆文案,靠 `SelectedIndex` 对齐。
**这条缝错了不会报错**:插一项、删一项、调个顺序,编译照过、界面照常渲染,
用户选「Ed25519」拿到的却是 RSA —— 列表里如实写着 RSA,可没人会怀疑自己刚选的那一项。
所以 `SshKeyChoiceCatalogTests` 直接读 axaml 文本逐项比对键名与顺序。

建议文件名也跟着算法走:`velashell_ed25519` / `velashell_ecdsa256|384|521` / `velashell_rsa`
(重名自动加 `_2`)。

### 验证

- **两条新测试都先证明过会红**,不是摆设:把 axaml 里两个 `ComboBoxItem` 对调 →
  `SshKeyChoiceCatalogTests` 失败;把 `SelectedIndex` 的绑定改成 `Mode=OneWay` →
  `KeyManagementPageUiTests` 失败(它补的正是文本比对够不着的那一半:控件真装配起来之后
  选择写不写得回视图模型。绑成单向的话编译照过、下拉照样能选,用户选什么都拿到第一档)。
- **系统自带 OpenSSH 交叉验证五把**:`ssh-keygen -y -f` 从私钥反推的公钥,与我们写出的 `.pub`
  逐字节相同 —— Ed25519、ECDSA 256/384/521、RSA 4096 全数通过,`ssh-keygen -lf` 报的位数与
  类型也对(`256 … (ED25519)`、`521 … (ECDSA)`、`4096 … (RSA)`)。
- 界面绑定不必靠跑起来验:`AvaloniaUseCompiledBindingsByDefault` 为 true,
  `{Binding SshKeys.SelectedAlgorithmIndex}` 在编译期就按 `x:DataType` 校验过路径。
- 五份 resx 各补 6 个键(`SetKeys_Algorithm` 与五个档位文案)。ECDSA / RSA 那几档各语言同形
  —— 算法名不是可翻译的散文,只有 Ed25519 那档带「推荐」标记需要翻。
- `dotnet build VelaShell.slnx` 零警告零错误;`dotnet test VelaShell.slnx` **3590 通过、0 失败**
  (23 条按环境跳过)。
- 📄 velashell-docs **待同步**:与 §86 合并成一条登记(见 `feature-plan.md` 的「文档待同步」)。

## ✅ 88. 2026-09-20 SFTP 路径栏:编辑 / 复制两颗钮改用工具栏图标钮规格(用户反馈)

路径栏尾部那两颗钮(铅笔=进路径编辑态、拷贝=复制当前目录路径)一直挂着 `Classes="crumb"` ——
它们跟着面包屑分段用了同一套内联尺寸:`Padding="2,1"` + 11px 图标,命中区不到 15×13,而且
两颗之间**一点间距都没有**(只有铅笔那颗带了 `Margin="2,0,0,0"` 把它跟最后一级面包屑分开)。
于是同一条表头上出现两种图标钮:左边这两颗挤在一起的小钮,右边一排 24×24 / 13px 的工具栏钮。
靠得太近容易点错,大小不一致则是同一行里两套尺度。

改动只在 `FileBrowserView.axaml` 的路径区:

- 两颗钮从 `crumb` 换成 `toolbar-btn`,并与右侧工具栏钮取**完全相同的规格** ——
  `Width/Height=24`、`Padding=0`、内容居中、图标 13×13、前景 `VelaTextTertiary`
  (原先是 `VelaTextMuted`,比右侧那排更暗,也是一处不一致)。
- 两颗钮包进一个 `Spacing="2"` 的横向 `StackPanel`,整体 `Margin="8,0,0,0"`:与最后一级
  面包屑拉开 8px,彼此留 2px。
- 顺手补上 `AutomationProperties.Name`(取与 `ToolTip.Tip` 同一个本地化键),此前这两颗
  纯图标钮对读屏是空的。

`crumb` 样式本身没动 —— 面包屑分段仍然要那套紧凑的内联尺寸,它们是文字链而不是图标钮;
这次要分开的正是「面包屑的一级」与「对当前路径动手的钮」这两类东西。

`PinAndCopyPathBindingUiTests` 按 `Command` 引用找按钮,不依赖 class 或尺寸,继续通过。

### 补:「跟随终端目录」换字形 map-pin → terminal

同一条表头上的另一处不齐。`map-pin` 在 lucide 的 24 网格里占 **y2–22**,比同排图标(history /
folder-plus / refresh-cw 都是 y3–21)上下各多出一点;更要命的是它的重心全压在上半部那个
r=8 的圆头上(质心约 y10),下半部只剩一根收尖的尾巴 —— 眼睛锚在圆头上,轮廓却垂到最低,
这一颗跟谁都对不齐。`LucideIcon` 是按 24 视图框等比缩放居中绘制的,不做外接框归一化,
所以字形自己的外接框偏到哪,画出来就偏到哪,调 `Margin` 只是把偏差换个地方。

换成 `Icon.terminal`(`>_`):外接框 **x4–20 / y5–19**,横竖都正对 12 居中,与同排图标齐平。
语义上也更直:这颗钮讲的就是「跟不跟终端走」,开/关本来就靠 checked 的 accent 色区分
(`follow-pin` 样式里早写明了「仅颜色区分开/关」),而 pin 表达的「钉住不动」恰好是反义。

同排还有三个字形**不能**用,它们在同一屏里已经各有所指:

- `link-2` —— 本视图右键菜单的「新建符号链接」;
- `folder-sync` / `git-compare` —— 正上方 `SftpDocumentView` 工具栏的「同步…」「比较目录」;
- `square-terminal` —— 按 `Icons.axaml` 里写明的分工,那个带框字形专指「一条 SSH 会话」
  (标签页协议图标,`ConnectionIcon.SshKey`),不用于工具栏动作。

顺带把样式类 `follow-pin` 改名 `follow-terminal`(只在本文件内出现,无外部引用),
并补 `AutomationProperties.Name`。`Icon.map-pin` 的几何留在 `Icons.axaml` 里不删 ——
那是共用图标库,`map-pinned` 还在标题栏与 traceroute 用着。

### 补:清掉三个真·孤儿图标键,并把「这张字典谁在用」写进注释

顺着上面那颗 map-pin 腾空,把 `Icons.axaml` 全表按精确边界扫了一遍,本仓库内零引用的有
五个:`map-pin`、`folder-up`、`arrow-up-half`、`arrow-down-half`、`gauge`。

但「本仓库零引用」**不等于可以删**。判断要跨到 velashell-plugins 去看:进程内插件拿得到
宿主的 `Application.Resources`,第一方的 Docker / Redis / AI 三个面板各按键名取了 45 / 34 / 39
个字形。上面五个里,`gauge` 被 Docker 与 Redis 两个面板用着,`arrow-down-half` 被 Redis 的
「自动滚动」用着 —— 照本仓库的 grep 结果删,会直接打坏两个插件,而且是**静默**的:
`Glyph.ResolveKey()` 里 `TryFindResource` 失败就 return,`Data` 保持 null,`Render` 早退,
图标凭空消失,不抛异常,本仓库测试照样全绿。

反过来,插件也**不是**这张字典的契约用户 —— `zh/templates/dev-guide.md` 写明插件图标走
`PluginIcon` 交 24×24 路径数据、不走资源键,隔离进程里根本看不到这张字典;品牌字形归插件
自己的字典(`DockerTheme.axaml` 里的 `Docker.*`)。所以第三方插件按文档写就不会踩到,
会踩的只有我们自己那三个进程内面板,而它们在我们自己的仓库里,可控。

据此:

- **删掉** `Icon.map-pin`、`Icon.folder-up` —— 宿主、测试、三个第一方插件全都零引用。
- **留下** `Icon.gauge` 与 `arrow-up-half` / `arrow-down-half` 这一对,但把注释改成实话:
  网速指示早就改用整只 `arrow-up-down` 了,这两半今天只剩 Redis 在用下半;两半是一对,
  要删一起删。删除的前置条件写在注释里 —— 先让插件把字形收进自己的字典,**并且**等在装的
  旧版插件更新过一轮(已发布的 `.vpx` 是运行期按键名取的,宿主先删就会打坏已安装的旧版)。
- 字典头部补一段约定,把上面这套判断固化下来,免得下次再从头排查一遍。

## ✅ 89. 2026-09-21 打开 AI 面板会把主窗口冻住:三处都压在 UI 线程上(用户反馈)

反馈原话是「AI 插件现在打开会比较卡,时间会很长,而且还可能会造成主程序未响应」。
"未响应"这个词把范围一下子收窄了:那是 UI 线程被占住超过五秒时 Windows 画出来的,
不是"慢",是**堵**。顺着"谁在 UI 线程上干了重活"查,查出三处,彼此独立。

### 一、宿主的库操作是「异步签名、同步实现」

`SonnetDbEngine` 上每个 `*Async` 都长这样:

```csharp
await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
try { ThrowIfDisposed(); return action(OpenStore(collection)); }
finally { _gate.Release(); }
```

闸不争用时 `SemaphoreSlim.WaitAsync` 返回的是一个**已完成**的任务,`await` 它一次线程都不换 ——
于是 `action(...)` 原地跑在**调用方线程**上。签名是异步的,行为不是,而调用方没有任何办法看出来。

插件那三样能力(`IPluginStorage` / `ISecretsApi` / `ITimeSeriesApi`)全都架在它上面,
而插件面板是宿主在 `Dispatcher.UIThread.InvokeAsync` 里构造的(`PluginUiApi.ShowPanelAsync`)。
两件事一碰头,`await context.Storage.GetAsync(...)` 就是一次不折不扣的界面冻结。
日志里那条 `[02] [Plugin:velashell.ai] Stored sign-in for provider … could not be read`
打在主线程上,走的正是这条路。

**改法**:加一个 `InLockAsync`,闸照旧在调用方线程上取(`SemaphoreSlim` 按到达顺序放行,
顺序语义不变),闸内的活整个扔进 `Task.Run`。全类 13 个 `*Async` 一律改走它。
`TryExecuteAsync` / `TryDeleteAsync` 把 `ThrowIfDisposed()` 留在自己的 `try` 里面 ——
它们把"引擎已释放"也算作"这条路走不通"吞掉,判定不能提到外面去。

### 二、面板的初始化整个跑在构造函数里

`ChatPanelView` 构造函数末尾是 `_ = InitAsync()`。配上第一条,那个 `_ =` 是假的:
里面"读设置 → 刷会话列表 → 开三张时序表 → 扫 ↑↓ 输入历史"一步都不让出线程,
整段跑完面板才被交给宿主。

**改法**:`InitAsync` 开头 `await Task.Yield()`,构造立刻返回、面板先成型。
代价是设置比面板晚到一点点,所以把那趟初始化记成 `_initTask`,凡是要读整份 `_settings`
的入口先等它:`SendAsync`(「解释终端输出」就是开完面板立刻发一条,踩的正是这几毫秒)、
`OpenSettingsDialog`、`OpenToolsDialog`、`RefreshHistoryListAsync`。

这一条**必须**和 `_initTask` 一起改。两个设置窗口拿的是整份设置、关窗整份回写 ——
抢在设置读回来之前开出来,交到它手上的是一份默认值,用户在里面拨一个开关,
存下去的就是"默认值 + 那个开关",全部接入连同登录态一起没了。

### 三、真正的大头:第一次开面板要在 UI 线程上读三十多兆程序集

前两条加起来解释得了"卡",解释不了"十几秒的未响应"。把插件目录单独拎出来量了一下:

| 动作 | 冷缓存 | 热缓存 |
| --- | ---: | ---: |
| `GetExportedTypes()`(激活期,5 个程序集 ≈7 MB) | 2925 ms | 28 ms |
| 装载插件目录全部 32 个程序集(≈35 MB) | 16770 ms | 33 ms |
| `new ChatPanelView(...)`(headless,程序集已热) | 275 ms | 3 ms |

差的全是磁盘 IO,不是代码。激活期只碰得到 5 个(SDK / Avalonia.Base / Avalonia.Controls /
TextMateSharp.Grammars —— 最后这个是被 `ChatPanelView` 那个 `private ThemeName` 字段
拖进来的:值类型字段要算布局,`GetExportedTypes()` 就得把它那个程序集装进来)。
剩下的 LiveMarkdown ×4 / CSharpMath ×3 / Svg ×4 / Markdig / Mermaider / Anthropic / OpenAI…
**全都等到第一次构造面板时才装,而那是在 UI 线程上**。冷缓存下就是十几秒的"未响应",
热了之后同一个动作几十毫秒 —— 和"重启后第一次开特别慢、之后就还好"完全对得上。
用户日志里两次启动的 `Activated 'velashell.ai' … in 6315ms` / `2941ms` 是同一回事的激活期版本。

**改法**:`AiPlugin.StartServices` 那条后台任务里,等 IM 桥接与 MCP 服务端起来之后,
把插件目录下的程序集预先装一遍(`PrewarmPanelAssemblies`)。本插件本来就是 `onStartup`
常驻的,这份 IO 早晚要付,挪到启动后的后台线程上付,用户点开时就只剩控件构造那点事。

三条约束写在代码注释里,这里只记要点:

- **只装载程序集,不碰里面的类型。**装载线程安全,类型的静态构造不是 —— Avalonia 控件的
  样式属性注册就跑在静态构造里,而这会儿 UI 线程正忙着建主窗口,在后台线程上引爆它
  等于两头同时往同一张注册表里写。
- **走本插件的 `PluginAssemblyLoadContext`,不用 `Assembly.Load`。**Avalonia* 与 SDK
  要回落到宿主那一份(规矩写在那个 ALC 里),绕过去就会装出第二套类型。
- **装载上下文不是 `PluginAssemblyLoadContext` 就整个跳过。**它同时是"我确实是作为插件
  被装载的"的判据 —— 换成 headless 测试,这里是默认 ALC、目录里是测试宿主那两百来个 dll,
  横扫过去既慢又没道理。

### 顺带说明:不是问题的那两处

- **`plugin_data` 文档集合 11 MB / 实际存活 6 份文档共 14 KB**(追加写不回收)。
  `Documents.Open` 热缓存 46 ms,而且发生在激活期、不在开面板这条路上,与本次现象无关。
- **`RecentUserInputsAsync` 走的是 `SELECT … LIMIT 20000` 全表扫 `chat_messages`**
  (`MaxScanRows = 4 × MaxQueryLimit`),连 32 KB 上限的 `text` 字段一起拉回来。
  现在只有 21 条、34 ms,所以这次不是它;但它是**随聊天记录线性增长**的那一项,
  已经靠第一、二条挪出 UI 线程和开面板的关键路径,真正收窄要等有人抱怨再说。

全量测试 3572 项通过。

## ✅ 90. 2026-09-21 隧道面板:服务器下拉混进了 FTP/SFTP,自动重连那行被模板撑远(用户反馈)

两处都在 `B3Rth` 那张 340px 浮动面板上,一处是逻辑、一处是布局,互不相干。

### 一、服务器下拉里不该有 SSH 以外的协议

`TunnelPanelViewModel.LoadServersAsync` 把 `GetAllSessionsAsync()` 的结果**原样**倒进
`Servers`,而那是**全部**已保存会话 —— SFTP、FTP/FTPS、以及插件协议(S3 / WebDAV…)都在里头。
面板的前提却是"选一台服务器 → 后台自动建立**专用 SSH 连接** → 在这条通道上开端口转发":
端口转发是 SSH 的能力,别的协议给不出这条通道。选中一条 FTP,只会撞上一次注定失败的后台连接。

`MainWindowViewModel.OpenTunnelPanel` 其实早就挡过一道 —— `preselect` 是 SFTP/FTP/Plugin 时
直接 return,不开面板。但那只管"从哪条会话点进来",管不到面板自己去列表里拉的那一把,
于是下拉框照样把它们摆出来。过滤补在 VM 里(而不是调用方那条 lambda 上):
"隧道只走 SSH"是这个面板的前提,前提该由持有它的那一层保证,换一个调用方也不会漏。

顺带,`OpenAsync(preferredProfileId)` 的预选走的是 `Servers.FirstOrDefault(...)`,
过滤之后非 SSH 的 id 自然落空、回退到第一台 SSH 服务器,不需要另写一段。

### 二、⚠️ Fluent 的 CheckBox 模板有 12px 死高,`MinHeight` / `Padding` 压不动

表单末尾两行复选框(`tunLoopback` / `tunAutoReconnect`)在设计里是 14px 行高 + 8px 行距,
实际跑出来两行**相距 40px** —— "自动重连"那行看着像掉了队。

量出来的原因:Fluent 的 `CheckBox` 模板里,盛放勾选框的那层匿名 `Grid` 直接写死了
`Height="32"`,而框本身(`NormalRectangle`)只有 20×20,居中放着,**上下各空出 6px**。
视图上已经写了 `MinHeight="0" Padding="0"`,一点用都没有:

> **模板 XAML 里的直接赋值是 `LocalValue`,优先级高于外部样式的 `Setter`。**
> 也就是说,`Style Selector="CheckBox /template/ ..."` 改得动模板里**没赋过值**的属性
> (实测 `Width` 能改,因为那层 Grid 的宽是列定义给的),改不动模板里写死的那几个。
> 要真正换尺寸,只能整份重写 `ControlTheme`。

为这两行复选框重写一整套 `ControlTheme`(checked / indeterminate / hover / pressed /
disabled / 焦点框全得自己来,还要新增一个 `Icon.check` 进共用图标字典)不划算 ——
真正碍眼的是那 12px 死高,不是勾选框大小。所以在 `TunnelPanelView` 的样式里给 `CheckBox`
加 `Margin="0,-6"`,把模板多出来的死高原样吃掉:两行的**可见间距**回到 `StackPanel` 的
`Spacing="8"`,也就是设计值。勾选框仍是 Fluent 的 20×20(设计给的是 14),与本仓库其余
复选框保持一致 —— 差的是框的尺寸,不是间距。

面板整体因此收窄约 24px。

`TunnelPanelUiTests.Panel_FormCheckBoxes_KeepTheDesignedEightPixelGap` 把这条钉住:
断言每个复选框的布局高度是 20(而不是模板撑出来的 32),两行之间正好 8px。
另加两条 `TunnelPanelViewModelTests` 覆盖协议过滤与预选回退。三条测试在改动前全部失败。

全量测试通过(ShellIntegration 32 项按环境早退跳过,属既有行为)。

## ✅ 91. 2026-09-22 Tmds.Ssh → VelaShell.Ssh:换掉底层 SSH 库,并把「释放」全面异步化(用户需求)

分支 `feat/velashell-ssh`。底层 SSH 从 [Tmds.Ssh](https://github.com/tmds/Tmds.Ssh) 0.24.0 换成
[VelaShell.Ssh](https://github.com/VelaShellLabs/velashell-ssh)(MIT,net11.0),
走**工程引用**(`..\..\..\velashell-ssh\src\VelaShell.Ssh\VelaShell.Ssh.csproj`)——
两个仓库还在并排开发,先用相对路径把两边钉在一起,CI 因此会红,这是明知的代价。

行数上几乎是平的:已跟踪文件 +1172 / −3821(其中 14 个文件整份删除,计 2832 行),
新增的未跟踪文件 2623 行 —— 加总下来净 −26 行。**但成分完全不同**:
删掉的近 1200 行是为绕开上一版底层库而存在的,换回来的是一层薄适配。

### 一、先把库的两个缺口补齐,再动迁移

换库前先确认新库能接住现有的**全部**用法。两条缺口是动手前就点清的:

**A. 加密的 OpenSSH 私钥**(`bcrypt_pbkdf`)。这是本库「不手写密码学原语,无例外」那条规矩的
**唯一一次破例**,并且写进了 `docs/design/architecture.md` §11.2.18 与 `AGENTS.md` §3.3:
BCL 没有 Blowfish,而 `bcrypt_pbkdf` 是 OpenSSH 私钥容器唯一用的 KDF,不实现就等于
「带口令的私钥一律打不开」。Blowfish 的 P 盒 / S 盒**不是抄表**,而是按 Machin 公式
现算 π 的十六进制展开(`BuildInitialState` / `ArcTangentReciprocal`),测试拿它与
已知向量对账 —— 抄一张 1042 个 32 位常数的表,错一个字节也不会有人看出来。

**B. OpenSSH 用户证书**(`*-cert-v01@openssh.com`)。`OpenSshCertificate` 解析容器,
`SshCertificateSigner` 校验「证书是用户证书」「证书与私钥配对」后把签名委托给私钥,
并在发签名算法名时剥掉 `-cert-v01@openssh.com` 后缀 —— 证书只换封装,签名始终由私钥出。
**CA 签名不在这里验**:那是服务端的活,客户端验了也说明不了什么。

⚠️ **AEAD 的认证标签被我放错了地方,而且症状极具欺骗性。**
一开始我以为 chacha20-poly1305 / aes-gcm 的 tag 在 openssh-key-v1 的私钥 string **里面**。
测试全绿 —— 因为流密码把前缀解出来之后,两个 checkint 照样对得上,而 tag 从头到尾**没被验过**。
靠实测才看出来:同一批 fixture 里,`section` 长度 176,容器末尾还**多出 16 个裸字节**。
tag 在那个 string 的**外面**,不带长度前缀。改成 `reader.ReadRemaining()` 单独读,
密文与 tag 分开传给 `OpenSshKeyCipher.Decrypt`。

两条都对着真实 OpenSSH 10.3 验过(库侧互操作 18/18,零跳过)。

### 二、删掉的 ~1200 行,每一行当初都是为了绕开上一版底层库

| 删掉的 | 行数 | 它当初在绕什么 |
| --- | ---: | --- |
| `MeteredPortForwardHandle` | 376 | 上一版库把转发的搬运整个做在内部,**不暴露任何计数**。隧道面板要显示字节数与连接数,只能自己开 `TcpListener` + `direct-tcpip` 把数据面整个接管过来。新库的 `PortForwarder` / `RemoteForwarder` 自带 `BytesTransferred` / `TotalConnections` / `ActiveConnections`,于是只剩一层薄适配(`LibraryPortForwardHandle`) |
| `SshAlgorithmProbe` + `SshAlgorithmDiagnostics` | 304 | 协商失败时上一版库只给一句话,两边到底各有什么算法拿不到。于是**再连一次**、逐类缩小候选集去倒推。新库直接抛 `SshNegotiationException`,两边清单都在异常上 |
| `LoopbackProxyRelay` | 107 | 上一版库的代理接入只认「地址」,不认「一条已经建好的流」。于是本机起一个监听端口、把代理连接接力过去。新库有 `ISshTransportDialer`,`ProxyTransportDialer` 把现成的 `ProxyStreamConnector` 直接交给它,**本机不再开监听端口** |
| `TmdsSshInterop` | 106 | 库的 `ConnectFailedException` 是 `internal`,只能**按消息前缀做字符串解析**再分派 —— §A 那节早就标过它是已知脆弱点。`SshInterop` 按异常类型分派,**没有一处字符串解析** |
| `HostKeyPromptOutcome` | 48 | 主机密钥回调的返回值塞不下「拒绝的理由」。新库的 `SshHostKeyVerdict.Reject(reason)` 本来就带 |
| `OpenSshPrivateKey.TryConvertToOpenSsh` | -78 | 上一版库只认 OpenSSH 格式,导入的 PKCS#1 / PKCS#8 会被判 "Unsupported format" 而**静默跳过** publickey。新库三种格式原生都认,转换整段删掉 —— `LegacyPrivateKeyFormatTests` 也从「转换器转对了没有」改成「库直接读得出来没有」,盯的是同一个用户可见行为 |

`TmdsSshClientWrapper`(541)/ `TmdsSftpClientWrapper`(502)则是被
`VelaSshClientWrapper` / `VelaSftpClientWrapper` 等量替换,不计入上面这笔账。

顺带补上的两件事:`pty-req` 的**像素尺寸不再被丢掉**(新库的 `TerminalSize` 带
`pixelWidth` / `pixelHeight`),以及 SFTP 的 `posix-rename@openssh.com`
在服务端支持时走真正的原子改名。

### 三、⚠️ 两条纪律,都是在踩坑之后由用户定下来的

**「永远不要搞这种假的异步 `Task.Run`。若是底层库不支持则去改 SSH 库。」**

起因是一个只在**连着跑**时才出现的死锁:Docker 集成用例里 `ssh.Dispose()` 挂满 60 秒超时。
用 `TempDisposeProbe` 探出来的结论很干净 —— 异步释放**永远**成功,
`DisposeAsync().AsTask().GetAwaiter().GetResult()` **永远**死锁。

我先交的方案是 `AsyncTeardown`:`Task.Run` + `Wait(超时)`。被否掉了,而且否得对:
那不是「异步」,只是把死锁换成「卡到超时为止」,还白占一条线程池线程。
第二版想在库里加一个原生同步的 `Dispose()`,也被下一条纪律直接盖过去了。

**「所有操作,包括释放,都尽可能用异步版本,而不是同步版本。」**

于是 VelaShell 这边四个契约整体改成 `IAsyncDisposable`:
`ISshClientWrapper` / `ISftpClientWrapper` / `IShellStreamWrapper` / `IPortForwardHandle`,
拆连接那条链一路 await 到底 —— `SshConnectionService` / `SftpService` / `TunnelService` /
`SshTerminalBridge` / `TerminalTabViewModel` / `MainWindowViewModel` 全部跟着改。
`ConPtyShellStream.DisposeAsync` 返回 `ValueTask.CompletedTask` 并注明理由:
它收尾的是**原生同步**的 ConPTY 句柄,不是「假装异步」。

这一改带来两个连锁:

1. **`Disconnect()` / `Stop()` 成了死代码,连同接口一起删掉。** 生产代码里已经没有调用方,
   实现也退化成 `_ = DisposeAsync()` 这种即发即忘 —— 正是纪律要去掉的东西。
   测试里那几处 `Received(1).Disconnect()` 相应改成 `await ….Received(1).DisposeAsync()`:
   拆连接现在**整个**在 `DisposeAsync` 里完成,不再有「先断后放」两步。

2. **`SshTerminalBridge.DisposeAsync` 的等待要有预算,但那预算不能靠 `Task.Wait`。**
   改成全 await 之后它变成**无界等待**,`ReconnectTabAsync` 那条用例当场挂满 60 秒 ——
   替身流的 `ReadAsync` 返回一个永不完成的 Task,而替身的 `DisposeAsync` 什么也不做。
   这不只是替身的问题:「释放流会唤醒挂起的读取」是**实现方的约定**,自带的
   `ShellStreamWrapper` / `ConPtyShellStream` 都守着,而插件提供的终端协议流是第三方代码。
   补回预算,但等法是 `Task.WaitAsync(budget)`(读 2s / 写 1s),
   **不占线程、不阻塞 UI**;超时之后**不释放** `_cts` 与 `_drainGate` ——
   循环还活着,这会儿释放只会让它炸一串 `ObjectDisposedException`。

退出路径上的 `Task.WhenAll(...).Wait(2s)` 保留:`desktop.Exit` 是同步事件,
进程正在退,那是最后一个真正的同步边界,不是「同步等异步」的懒办法。

### 四、⚠️ 证书靶机占了别人的端口 —— 撞端口的后果不是「起不来」

`tests/cert-lab/` 那台「只认证书」的 sshd 原先监听 **2223**,而
`docker-compose.test.yml` 的 `ssh-shells` 服务早就占着这个端口。
后果不是端口冲突报错,而是 **`ShellIntegrationHarness` 连上了证书靶机**,
然后以一句莫名其妙的「对端在发出版本标识串之前关闭了连接」告终 —— 32 条用例
从「按环境跳过」变成「全部失败」。挪到 2224,并在脚本里把 2222/2223 已被占用这件事写明。

同一节里另外三处也一并收紧:

- **基础镜像挑本机已有的**。Docker Hub 在这台机器上拉不动,原先硬写 `alpine:3.20` 会在
  第一步就失败。改成按「越现成越靠前」依次试 `test_sshserver` → `linuxserver/openssh-server`
  → `alpine`;linuxserver 那份 sshd 的二进制叫 `sshd.pam`,统一软链成 `/usr/local/sbin/sshd`。
- **`ENTRYPOINT []` 必须清掉**。基底自带 s6 init,不清的话我们的 CMD 只是被当成参数塞给它,
  **它那份 sshd 也会跟着起来** —— 一台「除了证书哪条路都不通」的靶机上凭空多一条没配过的认证路径。
- **账号不能是锁着的**。`adduser -D` 建出来的用户 shadow 里是 `!`,sshd 对锁着的账号
  **连公钥认证都直接拒**(`account is locked`)—— 证书对了也登不上。口令字段置成 `*`。

最要紧的是给 Dockerfile 补了一段**硬自检**:`sshd -T` 的实际生效配置里没出现我们的 CA、
或还留着别的认证路径、或账号锁着,就让构建当场失败。这一段是被一次真实的假阳性逼出来的 ——
发行版把 `Include /etc/ssh/sshd_config.d/*.conf` 那行**注释掉**了,我们的 `00-cert-only.conf`
于是一个字都没生效,而脚本只是把结果打出来给人看,没人会逐行去核。
(`AuthorizedKeysFile none` 的表现是这一项在 `-T` 里**根本不出现**,断言要按「不存在」写。)

### 五、验证

- `dotnet build VelaShell.slnx`:**0 警告 0 错误**。
- `dotnet test VelaShell.slnx`:**3604 通过 / 0 失败 / 43 跳过**。
  跳过的是 32 条 ShellIntegration(需要拉不到的 `ssh-shells` 镜像)加 11 条环境门禁用例。
- `VelaShell.Core.Tests` **零跳过**:证书靶机起来之后,那两条端到端用例真的在跑。
  服务端日志给出双向证据 —— `Accepted certificate ID "velashell-lab" … via /etc/ssh/velashell_user_ca.pub`
  (阳性),以及同一把私钥**不带证书**时的 `Failed publickey`(阴性对照)。
- 之前死锁的 `Symlinks_ListCreateResolveAndDelete_AgainstRealOpenSsh` 现在 269ms 通过。

### 六、顺带清掉的文档漂移

换库之后仓库里散着几十处「当前用的是 Tmds.Ssh」。留着比没有更糟:它们看起来像事实。
`README.md` / `README.en.md` / 两个 `README.md`(Core / Infrastructure)/ `Directory.Packages.props` /
`feature-plan.md` 全部对齐,`Directory.Packages.props` 里那条已经没人引用的
`PackageVersion Include="Tmds.Ssh"` 删掉。**历史陈述一律保留**
(「本项目先后基于 SSH.NET、Tmds.Ssh」这类),改掉的只有把过去说成现在的那些。

⚠️ `BouncyCastle.Cryptography` 的版本注释也跟着改了,而且多了一句:SSH 库如今在**隔壁仓库**、
有它自己的 `Directory.Packages.props`,两边不一致时 NuGet 取高的那个 —— 「能编过」依然不等于「钉住了」。

`feature-plan.md` 这边有四项因为换库**不再卡上游**,已就地改掉结论:
SSH PTY 像素尺寸贯通(原本等 `tmds/Tmds.Ssh#519`)、SSH Agent 转发、SSH 压缩开关
(原本等我们自己提的 `#513` 合并发版)、算法协商可配。四项的底层能力现在都在 VelaShell.Ssh 里,
剩下的全是宿主侧接线。

**未推送**:分支与改动都留在本地,等用户先跑一轮看效果。

### 七、合并前审查:改回来的六处行为漂移

合并前对着旧实现逐项对照了一遍(迁移完整性 + 「是不是真异步」两条线)。能力上**没有缺项**,
但有几处行为悄悄变了 —— 都是换库带出来的,单测替身测不出来:

| 漂移 | 后果 | 改法 |
| --- | --- | --- |
| **指纹格式变了**:旧库的 `SHA256FingerPrint` 是裸 base64,新库的 `Sha256Fingerprint` 带 `SHA256:` 前缀 | `SonnetDbHostKeyService` 逐字节比对 → 换库之后**每一台已保存的主机**都判成「指纹已变更」;开了「变更即阻断」的用户一台也连不上 | 比对前两边都去掉前缀与 base64 填充(`SameFingerprint`)。旧记录不迁移,下次信任时自然改写成新形式 |
| **重命名撞上同名目标会静默覆盖** | 普通 rename 失败后回退到 `posix-rename` —— 旧库那条是假的(转调普通 rename),新库是**真的原子覆盖**。「改名撞了同名文件」变成数据丢失 | 目标已存在(或问不出来)时不回退,抛原错误 |
| exec 不要 stderr 时库仍缓冲它 | 缓冲的 stderr 只在被读走时回补窗口;没人读 → 窗口满 → stdout 也停,`docker logs -f` 卡死 | 此时通道选 `SshStderrPolicy.Discard` |
| 「端口被占用」「转发被禁止」两条本地化提示不再出现 | 绑定失败被库包成 `SshForwardException`、宿主再包一层;「administratively prohibited」原来按英文文案认,库的消息是中文 | 沿 `InnerException` 链找 `SocketException`;禁止转发改认 `SshChannelOpenFailureReason.AdministrativelyProhibited` 原因码 |
| **非中文界面看到中文报错** | 库的异常消息是中文,`SshInterop` 原样透传 | 连接类失败按 `SshFailureReason` 给本地化标题 + `[原因 @ 阶段]` 尾注(`SshErr_*` 12 个键,五份 resx)。代理失败与主机密钥拒绝不翻 —— 那两条的消息是宿主自己拼的,信息更多 |
| 远程转发掉线后仍显示「运行中」 | 旧的计量句柄会上报一条通道错误;库的转发器只报单条连接的失败 | `LibraryPortForwardHandle` 订阅 `SshConnection.Disconnected` |

另外两处是异步那条线上的:

- **`SshTerminalBridge` 释放 shell 流没有上限**。释放要往对端发 `CHANNEL_CLOSE`,半死的链路上
  可能挂几分钟,关标签与重连就被拖住。改成 `WaitAsync(2s)`,超时让它在后台自己收尾。
- **插件停用时同步 `Dispose` 在途的远端读流**。那是一次 `SSH_FXP_CLOSE` 往返,同步释放
  只能阻塞着等网络。改成不等待的 `DisposeAsync`。

代理解析器自己抛的 `InvalidOperationException`(代理地址写错)以前会被包成连接失败,
换库后变成了一个裸异常 —— `ProxyTransportDialer` 里补回包装。

**库侧的三条**(已交给 velashell-ssh 那边,不在本仓库改):主机密钥弹窗的等待被计入了
连接超时(用户 10 秒内没点就连接失败,「永久信任」也可能没存上);`SftpFileStream.Dispose(bool)`
仍是同步等异步;`SshChannel.DisposeAsync` 的关闭报文没有时间上限。

## ✅ 92. 2026-09-23 SSH Agent 认证与转发、X11 转发、压缩开关(用户需求)

换库(§91)之后这三件在底层都已经有了,本节是宿主侧的接线。落在连接配置的「高级选项」里,
新增 `SessionProfile.Ssh`(`SshSessionOptions`,三项都关时整个存 `null`,老配置零迁移)。

### 一、四样东西

| 项 | 界面 | 接到哪 |
| --- | --- | --- |
| **SSH Agent 认证** | 认证方式下拉末项「SSH Agent」(`AuthMethod.Agent`,枚举按序号落盘,只能加在末尾) | `SshConnectionAssembler`:连本机 agent → 每把钥一个 `PublicKeyCredential`,agent 客户端活到认证结束再释放 |
| **压缩** | 「启用压缩(适合高延迟 / 低带宽链路)」+ 一行何时该开的说明;SSH 与 SFTP 都有 | `SshConnectionAssembler.Algorithms`:`SshAlgorithmSet.Default.WithCompression()`(`zlib@openssh.com` 在前、`none` 兜底) |
| **agent 转发** | 「转发 ssh-agent(-A)」+ 风险说明;只对 SSH | `SshShellOptions.AgentForwarding` |
| **X11 转发** | 「X11 转发」,打开后多出「本机 X 显示」与「受信任(-Y)」;只对 SSH | `SshShellOptions.X11`(`SshForwardingOptions` 负责翻译) |

### 二、几处取舍

- **压缩没有做成「按延迟自动开」**。压缩算法在握手最开头的 KEXINIT 里就定了,那时还没有任何
  延迟数据;要做自动只能拿上一次连接的测量值猜,猜错了用户也看不出为什么。所以是一个手动开关,
  说明文字里直接写清「往返 100 ms 以上、或带宽紧张时开;局域网与已压缩数据别开」。
- **转发被拒不连累会话**。库的 `SshForwardException` 不区分是哪一项被拒,两项都开时靠重试定位
  (先去掉 X11,再去掉 agent),只在失败路径上多几个往返。结果写进 `IShellStreamWrapper.Notices`
  (带默认实现的接口成员,ConPTY / 插件流零改动),主窗口在首连与重连时以灰字 / 黄字写进终端顶部。
- **X11 默认受信任、整条会话有效**。Windows 上的 X 服务器没有 SECURITY 扩展、本机也没有 xauth,
  非受信模式在那里根本跑不起来;库默认 20 分钟后拒绝新 X11 通道,交互式会话里只会被当成坏了。
  显示地址按「配置 → `DISPLAY` → `localhost:0.0`」依次取。**不带 X 服务端**(`feature-plan.md`「确认不做」)。
- **连 agent 带 3 秒上限**。Windows 上 agent 服务没起时命名管道不存在,不带超时的管道连接会一直
  重试到整条连接超时;现在 3 秒内报「连不上本机 ssh-agent」并附 `Start-Service ssh-agent`。
  `SSH_AUTH_SOCK` 在 Windows 上只认 `\.\pipe\…`(Git Bash / WSL 的 Unix 套接字连不上)。

### 三、验证

- 单测:`SshSessionFeaturesTests`(算法集、X11/agent 选项翻译)、`ConnectionProfileViewModelTests`
  三条(编辑往返、SFTP 丢掉转发、Agent 下拉位置)。
- **端到端**(`SshSessionFeaturesIntegrationTests`,ssh-shells 靶机,本次给它开了 `X11Forwarding`
  并装了 xauth):压缩真的协商成 `zlib@openssh.com`;远端用 bash 的 `/dev/tcp` 手搓一个 X 客户端,
  本机假 X 服务器收到了换过 cookie 的建立报文;远端 `ssh-add -l` 问到了本机的假 agent;
  私钥只在假 agent 里的情况下以「SSH Agent」认证登录成功。6/6 通过。
- 全量测试通过;ShellIntegration 32/32。

**没做的**:「自动加载密钥到 Agent」(R-06,要往 agent 里**加**钥,库的 agent 客户端还不会)、
Pageant、agent 转发的「只转发指定密钥 / 逐次确认」界面(库已支持)。均记在 `feature-plan.md`。
文档:velashell-docs `zh|en/host/交互与界面规格.md` 同步。

## ✅ 92. 2026-09-23 把 VelaShell.Ssh 并进本仓库,不再单独发 NuGet(用户需求)

§91 换库时 VelaShell.Ssh 还在独立仓库 `VelaShellLabs/velashell-ssh`,宿主走跨仓库的工程引用
(`..\..\..\velashell-ssh\…`)—— 明知 CI 会红、Release 会因引用未签名程序集报 CS8002,
原计划是等那边发一版 NuGet 再换回 `PackageReference`。用户拍板:**不单独发了,直接并进来**。
这个库只有宿主一个使用者,为它单独维护一条发版流水线、每次改库先发包再抬版本号,得不偿失。

### 一、搬到哪

| 原位置(velashell-ssh) | 现位置 | 说明 |
| --- | --- | --- |
| `src/VelaShell.Ssh/` | `src/VelaShell.Ssh/` | 库本体。**该目录仍按 MIT 授权**:`LICENSE` / `NOTICE.md` 随目录一起搬来,与本仓库其余部分的双授权不同 |
| `AGENTS.md` | `src/VelaShell.Ssh/AGENTS.md` | 改写成库专属约定:净室规程、依赖纪律、BcryptPbkdf 那一处例外;通用约定指回根 AGENTS.md |
| `tests/VelaShell.Ssh.Tests/` | `tests/VelaShell.Ssh.Tests/` | 568 条(549 通过 + 19 条 Interop 在无靶机时 Inconclusive) |
| `eng/` | `scripts/ssh/` | 互操作靶机脚本、压缩严格校验、基准、公开面清单生成(原来的相似度门禁没有搬,见下文「三、CI」) |
| `docs/` | velashell-docs 仓库 `zh/ssh/` + `en/ssh/` | 按本仓库「文档一律去 velashell-docs」的规矩;英文镜像是这次新译的 |

**没保留 git 历史**(用户选择直接拷贝):velashell-ssh 只有 5 个提交,追溯时去那个仓库看。

### 二、构建上要对齐的几处

- **严格规则收进 csproj**。库原来的 `Directory.Build.props` 给了 `TreatWarningsAsErrors`、
  `AnalysisLevel=latest-recommended`、`EnforceCodeStyleInBuild` 与一组 AOT/裁剪分析器;宿主的 props 没有这些,
  也不该为了一个库把全仓收紧 —— 所以挪进 `VelaShell.Ssh.csproj`,只作用于本工程。打包相关的属性删了。
  两边 `.editorconfig` 逐字相同,并进来 0 警告。
- **签名**。作为本仓工程,它随 Release 一起强名签名,CS8002 的问题自然消失;`InternalsVisibleTo` 照
  `VelaShell.Terminal` 的老规矩只在非签名构建里给。用临时密钥做过一次 Release 构建验证。
- **测试运行器**。库原来跑 Microsoft.Testing.Platform(`global.json` 的 `test.runner`、测试工程是 Exe),
  宿主是 VSTest —— `dotnet test` 一个解决方案里不允许混用,改成与宿主其余测试工程一样的
  `Microsoft.NET.Test.Sdk` + `MSTest.TestAdapter/Framework` + coverlet。
  它自带的 `test.runsettings`(30s 超时 + 方法级并行)更严,保留;`tests/Directory.Build.targets`
  改成「工程自己没给 `RunSettingsFilePath` 时才套全仓那份」。
- **BouncyCastle 只剩一条版本**。以前库与宿主各有一份 `Directory.Packages.props`,两边不一致时 NuGet 取高的,
  注释里专门提醒"升库时对一眼";现在都读 `src/Directory.Packages.props` 那一条。
- **C# 单文件脚本撞上根目录的 `CheckTargetFramework`**。那个 target 要 XmlPeek 工程文件,
  而 `dotnet run xxx.cs` 的工程是内存里虚拟的,磁盘上没有 → MSB3733。加了 `Exists(...)` 条件。
  根 props 全仓打开的 `GenerateDocumentationFile` 会让脚本里的 `///` 刷 CS1587/CS1591,脚本里各自关掉。
  基准脚本原来吃库仓库的中央包版本(BenchmarkDotNet 0.14.0),现在就地写死 `@0.14.0` ——
  tests/ 那边的 0.16 预览版改了 `InProcessNoEmitToolchain` 的 API。
  Release 意味着签名,所以基准的命令行改成 `dotnet run -c Release -p:SignAssembly=false …`。

### 三、CI

`ci.yml` 加两个作业、改一处过滤:

- `ssh-checks`:压缩严格校验(单独一个进程跑,理由见 `ci.yml` 注释)。
  库原仓库还有一道「相似度门禁」(拉 Tmds.Ssh / SSH.NET 源码做 token 级指纹比对,证明是独立实现),
  并入时先接进了 CI,随后按用户决定**整体移除**:脚本目录、CI 步骤、`.gitignore` 条目、
  AGENTS.md 净室规程里的那一条、NOTICE.md 的实测数据与代码注释里的白名单说明一并删掉。
- `ssh-interop`:两版 OpenSSH 容器的互操作矩阵,只在推 main 与手动触发时跑;为此 `on:` 加了
  `workflow_dispatch`。用 Debug —— Release 签名会省掉友元声明,测试工程编不过。
- 主测试作业的过滤加上 `TestCategory!=Interop`(没有靶机时它们本来也只是 Inconclusive,跑一遍白花时间)。

### 四、顺手改的引用

关于页的 SSH 库链接(`SshBackend.ProjectUrl` / `LicenseUrl`)与 README 里的仓库链接改指本仓库
`src/VelaShell.Ssh`;代码注释里的 `docs/…` 一律改成 `velashell-docs/zh/ssh/…`,`eng/…` 改成 `scripts/ssh/…`。
§91 末尾记的「库侧的三条」(主机密钥弹窗计入连接超时、`SftpFileStream.Dispose(bool)` 同步等异步、
`SshChannel.DisposeAsync` 无上限)在 velashell-ssh 最后一个提交里已经修掉,随代码一起进来了
(`SshConnectDeadline` 停表、`CloseInBackgroundAsync`、`DisposeTimeout`)。

velashell-docs 那边的配套改动:新增 `zh/ssh/` 与 `en/ssh/`(architecture、spec 00–09、getting-started 与索引页),
挂进仓库首页、`zh|en/README.md` 与 AGENTS.md 的目录表。原文 architecture.md 首行标题与一处被折断的
`chacha20-poly1305@openssh.com` 在搬运时顺手修了。

## ✅ 93. 2026-09-23 CI 去掉 `ssh-interop` 作业,解决方案补齐 `.github` 文件(用户需求)

- `ci.yml` 删掉 §92 加进来的 `ssh-interop` 作业。它只在推 main 与手动触发时跑，在 PR 的检查列表里
  永远显示为一项 Skipped,看着像没跑的门禁。互操作用例改为本地按需跑:`scripts/ssh/interop/Start-TestServer.ps1`
  起靶机，再 `dotnet test tests/VelaShell.Ssh.Tests -c Debug --filter "TestCategory=Interop"`(环境变量见脚本输出)。
  主测试作业里的 `TestCategory!=Interop` 过滤保留;`workflow_dispatch` 也保留，手动重跑整套门禁仍然有用。
- `VelaShell.slnx` 补上 `.github/ISSUE_TEMPLATE/` 下的三个文件 —— 此前 `.github` 里只有它们没进解决方案。

## ✅ 94. 2026-09-23 关通道时 stdin 泵撞上「reader 完成后不许再读」(用户反馈)

调试输出里关标签 / 断开时总有一条 `InvalidOperationException: Reading is not allowed after reader was completed`
(System.IO.Pipelines)。来源是 `SshChannel.FinishClose` 从外面把 stdin 管道的 **reader** 完成了，而那个 reader 归
`PumpStandardInputAsync` 所有：泵正读到一半或刚 `AdvanceTo` 完要回头再读，就撞上它。异常被泵的 catch 接住，
没有功能后果，但每次关通道都在调试器里冒一条。

改成 reader 只由泵自己完成(各条退出路径统一在泵尾部 `CompleteAsync`);`FinishClose` 只完成 writer、取消 `_lifetime`,
泵从没起来(通道没开成)时才由它代为完成 reader。

同一段输出里的 `OperationCanceledException` / `TaskCanceledException` 是断开时取消在途读写的正常首次机会异常，不需要改。

## ✅ 95. 2026-09-23 宿主接上 SSH 库的 X11 尽力而为(`BestEffort`)(用户需求)

SSH 库给 `X11ForwardOptions` 加了 `BestEffort`:设置失败时不抛、shell 照常开，原因放在 `SshShell.X11SetupFailure`。
宿主配置里的 X11 开关本来就是连接级的、失败要降级(§92),于是接上:

- `SshForwardingOptions.X11` 产出的选项一律 `BestEffort = true`。
- `VelaSshClientWrapper.OpenShellWithFallbackAsync` 删掉「挨个去掉转发来定位是哪项被拒」的多轮重试:
  X11 不会再抛，能接到的 `SshForwardException` 只会来自 agent —— 去掉 agent、保留 X11 重开一次即可。
  以前 X11 被拒时要多开一次 shell(两项都开时最多开四次),现在 X11 被拒不产生任何额外往返。
- `shell.X11SetupFailure` 非空时写一行黄字(`Ssh_X11ForwardFailed`),与以前的提示同一条文案。
- ssh-shells 靶机给 `vela-dash` 单独关了 `X11Forwarding`(`Match User`),新增端到端用例
  `X11_RefusedByServer_KeepsAgentForwardingAndWarnsOnce`:服务端拒绝 X11 时 shell 开成、agent 转发照常、只有一条黄字。

velashell-docs:`zh/host/交互与界面规格.md` 与 `en/host/interaction-and-ui-specs.md`「被拒不连累会话」一段同步改写。

## ✅ 96. 2026-09-23 标签页协议图标的绑定错误(用户反馈)

每开一个本地终端，调试输出里刷三条 `[Binding] ... binding 'Data' to 'Terminal.TabIcon.Geometry' at 'TabIcon': 'Value is null.'`
(外加 `ViewBoxSize` / `Fill`)。本地终端没有配置,`TabIcon` 是 null,而图标的三个属性直接绑 `Terminal.TabIcon.Xxx`,
路径走到 null 就断。图标本来就靠 `IsVisible` 收掉了，功能没问题，但刷多了会把真错误淹掉。

四个标签模板(`DockTabItem` / `SftpDockTabItem` / `WorkspaceDockTabItem` / `PluginDockTabItem`)统一改成
把图标的 `DataContext` 收窄到 `TabIcon`(`x:DataType="services:TabIcon"`),子属性绑 `Geometry` / `ViewBoxSize` / `Fill`;
DataContext 为 null 时绑定静默不取值。可见性改绑 `$self.DataContext`,前景色经 `$parent[...]` 回到标签的数据上下文。
回归用例 `SessionTabIconUiTests.ALocalTerminalTabBindsWithoutErrors`:本地终端标签零绑定错误，SSH 标签零错误且图标拿到连接标识色。

## ✅ 97. 2026-09-23 CI 在 Linux / macOS 上随机红:请求账本「入队后才登记」的竞态(#492)

SSH 库并入之后,CI 的 Linux / macOS 作业几乎每次都有几条 `VelaShell.Ssh.Tests` 随机失败,Windows 从来不挂;
每次挂的用例都不一样,症状却高度一致:「服务端拒绝执行这条命令 / 分配伪终端 / X11 转发」、`IsAlive` 为 false、等状态超时。
测试桩只在脚本要求时才拒绝，所以这些「拒绝」是客户端自己造出来的。

根因在 `SshConnectionSend.EnqueueAsync`:带登记回调的发送是「`TryWrite` 入队 → 回调登记账本」。
`_enqueueLock` 只让入队者之间互斥，挡不住另一个线程上的发送泵 —— 入队那一刻泵就可能把帧发出去，
内存传输上的测试服务端立刻应答，接收循环看到「没有对应请求的应答」,按 FIFO 失步把整条连接判死;
随后所有通道收尾，挂着的请求被 `Close(false)` 一律结算成「拒绝」。泵线程能立刻抢到核的 Linux / macOS 上才频繁撞上。
`FifoRequestLedger` 自己的注释早就写着「登记必须在发送之前」,是实现没兑现。

改成先登记、再入队(仍在同一把锁里，登记顺序与上线顺序照样一致)。全局请求与通道请求都走这一处，一并修好。
回归用例 `ChannelTests.请求账本的登记先于报文上线`:在登记回调里睡 300ms 把窗口撑大，断言回调结束前服务端没看到这一帧 ——
修复前在 Windows 上也稳定失败，修复后通过。

## ✅ 98. 2026-09-23 自动加载密钥到 Agent(设置审计 R-06,`feature-plan.md` 🔴 P0 项)

§92 把「SSH Agent」认证与 agent 转发接上之后,同一条线上还剩一件:用私钥文件登录的会话,
开了「转发 ssh-agent」也没用 —— 那把钥不在本机 agent 里,跳板机上 `ssh` 下一跳时 agent 拿不出东西。
`KeyOptions.AutoLoadToAgent` 这个开关早就存着,但库的 agent 客户端只会列身份与签名,不会**往里加**。

### 一、SSH 库:`SshAgentClient.AddIdentityAsync`

- 先写规格:velashell-docs `zh|en/ssh/spec/07-forwarding.md` 新增 §7.3(报文字段表、三种密钥的私钥内容布局、
  两种约束、五条决策),再照规格实现(`src/VelaShell.Ssh/AGENTS.md` 净室规程)。
- `AddIdentityAsync(InMemorySshSigner, comment, SshAgentKeyConstraints?, ct)`:无约束发 `17`
  `SSH_AGENTC_ADD_IDENTITY`,带有效期 / 逐次确认时发 `25` `SSH_AGENTC_ADD_ID_CONSTRAINED`
  (不发约束为空的 `25`,有的 agent 不认)。私钥内容由 `InMemorySshSigner.WriteAgentPrivateKey`(internal)
  按 draft-miller-ssh-agent 写出:ed25519 是「公钥 + 种子‖公钥」,RSA 是 **n 在前** 的 n/e/d/iqmp/p/q,
  ECDSA 是曲线名 + 公钥点 + d。
- 报文里是明文私钥:写进一块按报文上限一次性预留的缓冲(写满即抛,不扩容),发完整块清零;
  导出的 RSA / ECDSA 私钥参数、ed25519 种子用完也清零。
- 只接受进程内私钥;证书签名器要「证书 + 私钥」的组合格式,暂不做。库**从不自动**加钥。
- 测试:`AgentAddIdentityTests` —— 五种密钥(ed25519 / RSA 2048 / ECDSA 三条曲线)各走一遍
  「加钥 → 列出 → 让 agent 签 → 用原公钥验」。私钥字段写错位置时 agent 照样回 SUCCESS,
  只有验签能抓出来。另测 17/25 的选择、有效期向上取整、拒绝时抛 `SshAgentException`、重复加钥不查重。
  `TestAgent` 学会了解析加钥报文;`AgentForwardTests.改动agent状态的请求一律不转发` 多断言一条「加钥请求没到本机 agent」。

### 二、宿主:私钥认证成功后在后台加钥

- `SshConnectionAssembler.ConnectAsync`:连接建好(认证已成功)之后,开关开着、认证方式是「私钥」时,
  把 `SshAgentKeyLoader.AddAsync` 丢到后台。**认证成功之后才加**,配错的钥不会进 agent;
  **丢到后台**,agent 没在跑时那 3 秒连接上限不落在连接路径上。跳板链上每一跳各自判断。
- `SshAgentKeyLoader`:先 `REQUEST_IDENTITIES`,agent 里已有同一把钥就不再加(OpenSSH 对重复加钥只是更新注释,
  但每连一次就往 agent 里写一次私钥没有必要);agent 不在、被锁、拒绝、超时一律只记一行 `Trace`,不抛。
  注释写私钥文件路径,与 `ssh-add` 一致。连 agent 复用「SSH Agent」认证那一路的端点选择与 3 秒上限
  (抽出 `ConnectLocalAgentAsync`)。
- 证书认证与「SSH Agent」认证不触发(前者库暂不支持,后者钥本来就在 agent 里)。

### 三、为什么换了字段名、默认改成关

旧字段 `AutoLoadToAgent` 默认 `true`,而且设置是**整份**序列化的 —— 存量用户的配置里几乎都写着 `"autoLoadToAgent": true`。
那个 `true` 从来不是用户选的(开关一直藏着),把它当成「同意往系统 agent 里放私钥」说不过去:
Windows 的 OpenSSH agent 会把加进去的钥存进注册表,**重启后仍在**,这是一个会留下痕迹的动作。
所以新字段叫 `AddKeysToAgent`(对齐 OpenSSH 的同名选项)、默认关,旧键留在配置里被反序列化忽略。
回归用例 `SonnetDbPersistenceTests.Settings_LegacyAutoLoadToAgentDoesNotTurnOnAddKeysToAgent`。

### 四、界面

设置 → 密钥管理,「默认密钥」下面新增「SSH Agent」一节:「自动将密钥加入 Agent」开关 + 一行说明
(同 OpenSSH AddKeysToAgent、留到 `ssh-add -d` 删除为止、Windows 的 agent 跨重启保留)。五份 resx 齐。
云同步照常同步这个开关(它是偏好,不是本机路径)。

### 五、验证

- 单测:`AgentAddIdentityTests` 9 条(五种密钥各一条 + 四条行为)、`SshAgentKeyLoaderTests` 5 条、
  `SonnetDbPersistenceTests.Settings_LegacyAutoLoadToAgentDoesNotTurnOnAddKeysToAgent`。
- **互操作**(`OpenSshInteropTests.往真实的OpenSSH_agent里加钥`,`Interop` 分类,需 `VELASHELL_SSH_INTEROP=1`):
  在 ssh-shells 靶机(OpenSSH,端口 2223)上起一个一次性的 `ssh-agent -a`,经 `direct-streamlocal` 把本地客户端接过去,
  五种密钥逐把加进去(最后一把带有效期、走 `25`)→ agent 签名能用原公钥验过 → 远端 `ssh-add -l` 列出的指纹全对得上。通过。
  ⚠️ `docker-compose.test.yml` 的 `ssh-test`(2222)是 `AllowTcpForwarding no`,那上面开不了 streamlocal 通道,要用 ssh-shells
  或 `scripts/ssh/interop/Start-TestServer.ps1` 起的靶机。
- 全量 `dotnet test VelaShell.slnx` 0 失败。注意 DockerIntegration / Interop 用例是按环境早退的,全量跑时它们没有真跑 —— 上面那条互操作是单独带环境变量跑的。
- 未做真机验证:本机 Windows 的 ssh-agent 服务处于禁用状态,没有为此去改系统服务;命名管道那一路与「SSH Agent」认证共用同一段连接代码。

**没做的**:证书加钥、Pageant、agent 转发的「只转发指定密钥 / 逐次确认」界面 —— 后者登记在 `feature-plan.md` D 组。
文档:velashell-docs `zh|en/ssh/spec/07-forwarding.md` §7.3、`zh|en/ssh/getting-started.md`、
`zh|en/host/settings-audit.md` R-06、`zh|en/host/交互与界面规格.md` 密钥管理一节、`zh|en/host/架构设计.md` 未实现清单。

## ✅ 99. 2026-09-23 CI 随机红的三处:重协商测试桩、X11 单连接放行两条、AI 插话用例赌调度(CI 反馈)

### 一、重协商测试桩「发出之后才登记」

PR #493 的 ubuntu 作业挂了一条 `RekeyTests.服务端发起的重协商能接住并且连接继续可用`,25 秒超时;Windows / macOS 通过。
与本 PR 的改动无关,是 `TestChannelServer` 自己的竞态,和 §97 请求账本那次同一类。

`RequestRekeyAsync` 写的是 `_pendingRekeyKexInit = await _server.BeginRekeyAsync(...)`:KEXINIT 在 `BeginRekeyAsync` **里面**
就发出去了,赋值却要等发送返回。客户端回 KEXINIT 够快时,另一个线程上的收包循环进 `OnClientKexInitAsync` 读到 null,
把这次当成「客户端发起」、走 `RespondToRekeyAsync` 再发一份 KEXINIT —— 两边从此对不上,等重协商完成的任务永远不结束。

改成在发送回调里、字节上线**之前**把 KEXINIT 记下(`Volatile.Write`),收包侧用 `Interlocked.Exchange` 取走。
SSH 库本身没有改动。

回归用例 `RekeyTests.客户端的KEXINIT赶在服务端发送返回之前到达也能完成重协商`:测试桩新增 `DelayAfterRekeyKexInitSent`,
把「KEXINIT 已上线、发送未返回」的窗口撑到 300ms。把赋值挪回发送之后时它稳定挂在 25 秒超时上,修复后通过;
`VelaShell.Ssh.Tests` 全量连跑三遍均 0 失败。

### 二、X11 单连接模式会放行两条(库的缺陷,不只是用例不稳)

推上去之后 ubuntu 又挂了 `X11ForwardTests.单连接模式在本端强制`。根因在 `X11Forwarder.RelayAsync`:
单连接判断是「看 `_acceptedChannels` 是否 > 0」,而计数要等建立报文**发给本机 X server 之后**才加一。
用例在 X server 读到第一条的建立报文后立刻开第二条,第二条看到的仍是 0,于是也被放行。
脱开用例看,「先看再加」本身不是原子的:两条 x11 通道挨着到达时两条都会被转发,违背 spec/07 §7.5.3「第一条之后一律拒绝」。

- 名额改成 `Interlocked.Exchange` 原子认领;本机显示没连上(或连的时候出错)时在 `finally` 里退回 ——
  什么都没转发出去,不该占掉唯一的名额,与原先「只有接纳才计数」的口径一致。
- `_acceptedChannels` 挪到**建立报文上线之前**加一,X server 收到报文时计数已经可读
  (`同一连接上两个会话的cookie互不相通` 里 `ReadSetupAsync` 之后立刻断言计数,同样受益)。
- 回归用例 `单连接模式下同时到达的两条通道只放行一条`:两条通道同时写建立报文。旧代码在 Windows 上 6/6 稳定失败
  (两条都被放行),修复后 X11 这组连跑 6 遍全过。规格本来就是这么写的,velashell-docs 无需改。

### 三、AI 插件插话用例:回车按在第一次请求发出之前

macOS 挂了 `ChatPanelViewUiTests.ClickingAQueuedChip_TakesTheMessageBack`:等不到排队芯片。停止键亮起只说明这一轮开跑了,
不代表请求已发到 stub;而 `SteeringChatClient` 每次发请求前都会把队列 `DrainAll()` —— 回车若赶在那之前,
那句话就被并进第一次请求,芯片根本不出现。同文件 `StoppingTheTurn_…` 早就按「先等 `stub.Requests.Count >= 1` 再按回车」修过,
这次把 `ClickingAQueuedChip_…` 与写法相同的 `EnterWhileBusy_…` 一并改成同样的顺序。产品代码无改动。
## ✅ 100. 2026-09-23 本机 X Server:设置页 + 标题栏开关 + 拉起 VcXsrv(用户需求)

用户要在 VelaShell 里直接显示远端的图形程序,参照 WindTerm 的「X Server」设置。§92 做的是 X11 **转发**那一端,
本机的 X 服务端要用户自己开;这一节补上另一端 —— **但仍然不捆绑 X 服务端**(`feature-plan.md`「确认不做」的理由
没变),而是**拉起用户装好的 VcXsrv**(与 WindTerm 同一个 X 服务端)。自己写一个 X 服务端不现实:GTK / Qt 程序
要的不只是核心协议,还有 RENDER / XKB / GLX 一串扩展。

### 一、分层

| 层 | 东西 | 作用 |
| --- | --- | --- |
| Core | `Models/XServerOptions`(`AppSettings.XServer`) | 设置;`Normalize` 补缺失的节、回落认不出来的窗口模式、钳制显示号 |
| Core | `XServer/ILocalXServer` | 契约:状态、显示地址、找程序、启动 / 停止、为 SSH 转发解析显示 |
| Core | `XServer/XServerCommandLine` | 设置 → VcXsrv 命令行(纯函数,单测全覆盖) |
| Core | `XServer/XServerHelpCatalog` | 帮助对话框的内容:VcXsrv `-help` 的全部参数,分四组,说明文字走 resx |
| Infrastructure | `XServer/VcXsrvLocator` | 找 `vcxsrv.exe`:配置的路径(填目录也认)→ Program Files → Scoop → PATH |
| Infrastructure | `XServer/VcXsrvLocalXServer` | 进程管理:选显示号、拉起、等 `6000+N` 真能连上才算「运行中」、退出时随容器释放杀掉 |
| App | 设置 → X Server 页、`XServerHelpDialog`、标题栏按钮(`XServerToggleViewModel`)、命令 `tools.xserver` | |

### 二、几处取舍

- **「运行中」以端口为准,不以进程为准**。进程起来到开始监听之间有一段(首次建字体缓存能到好几秒),
  这段时间把显示交给 SSH 转发,第一个 X 客户端就连个空。启动等端口,上限 20 秒,超时杀掉并给出日志位置
  (VcXsrv 的日志被 `-logfile` 指到 VelaShell 的日志目录,「关于 → 打开日志目录」就能看到)。
- **SSH 转发的显示解析顺序**变成「配置里填的 → 本机 X Server → `DISPLAY` → `localhost:0.0`」。本机 X Server
  排在 `DISPLAY` 前:它在运行就说明用户此刻要的是它,而自动模式下它的显示号可能不是 0。
- **X11 转发时自动启动**(默认开)有两种情况**静默不插手**:本机 :0 上已有别的 X 服务端(X410、手开的 VcXsrv),
  以及根本没装 VcXsrv —— 否则没装的人每连一次都会收到一条黄字。其余启动失败写一行黄字,不拦 shell。
- **每个开关显式写两态**(`-clipboard` / `-noclipboard`…),不依赖 VcXsrv 的默认值;附加参数排在最后,
  同一开关后写的覆盖先写的,作为逃生口。页面下方实时预览下次启动的完整命令行。
- **显示号 :0–:15**。设置页下拉与 `Normalize` 的钳制、自动模式的扫描范围同一口径。
- **`-silent-dup-error` 总是带上**:显示号冲突由我们在启动前查端口报出来,不要 VcXsrv 再弹一个模态框。
- **只管自己拉起的那个进程**:外面另开的 X 服务端不碰;VelaShell 退出时把自己拉起的 VcXsrv 关掉。
- **只在 Windows 上启用**。Linux 桌面自带 X / XWayland、macOS 用 XQuartz,都经 `DISPLAY`,不需要我们管;
  这两个平台上标题栏按钮与命令面板条目都不出现,设置页只剩一段说明。
- 键盘布局 / 型号下拉的条目名沿用 XKB 的英文描述(要和远端 `setxkbmap` 对照着看),只有「自动」跟随界面语言。

### 三、验证

- 单测:`XServerCommandLineTests`(参数表、两态开关、附加参数切分与排序、帮助表五语齐全)、
  `XServerOptionsNormalizeTests`、`VcXsrvLocatorTests`、`VcXsrvLocalXServerTests`(端口探测注入:自动选号、
  三种静默不接管、固定号被占用启动前即失败)、`SshSessionFeaturesTests` 两条显示优先级、
  `SettingsXServerTests`(下拉索引映射、找程序状态行、命令行预览)、`XServerSettingsUiTests`
  (页面真渲染、下拉双向、帮助对话框过滤)。全量测试通过。
- **没有在装了 VcXsrv 的机器上跑过真实启动**:开发机上没装。进程拉起 / 等端口 / 退出清理这条路径只有
  单元层面的覆盖,首次在真机上用时留意 VcXsrv 日志。

文档:velashell-docs `zh|en/host/交互与界面规格.md`(标题栏按钮、X11 转发的显示解析、设置页一览)与
`zh|en/host/settings-audit.md`(第六批)同步。

## ✅ 101. 2026-09-23 X11 服务端库 VelaShell.XServer:M1 核心协议(用户需求)

§100 让宿主能拉起用户装好的 VcXsrv;用户希望更进一步 —— **不必装任何额外程序**。调研结论是 .NET 生态里没有可用的
X 服务端库(X11.Net 是客户端绑定;yserver 只跑 Linux DRM;node-x11 的服务端画在浏览器 canvas 上;WeirdX 是 GPL),
于是照 `VelaShell.Ssh` 的做法新建一个独立的 MIT 库 `src/VelaShell.XServer/`,**先做库、完成后再接进宿主**(本节只有库,宿主未改)。

### 一、定位与纪律

- **可嵌入、rootless、零原生依赖、跨平台**:每个顶层 X 窗口一块自己的 32 位像素缓冲,宿主(`IXServerHost`)把它画成原生窗口;
  宿主就是窗口管理器(移动 / 缩放 / 关闭经 `MoveTopLevel` / `ResizeTopLevel` / `CloseTopLevel` 回到服务端)。
- **净室规程同 VelaShell.Ssh**:实现依据只能是 X.Org 的协议规范、ICCCM、EWMH、BDF 规范;每个协议文件头写明依据;
  不看任何其它 X 服务端的源码。库自己的 `AGENTS.md` / `LICENSE` / `NOTICE.md`,根 AGENTS.md 已登记。
- 内置字体是 X.Org `font-misc-misc` 的 BDF(公有领域),按字符范围裁剪后作为嵌入资源,来源写在 `Fonts/Data/README.md`。

### 二、M1 做了什么

| 部分 | 内容 |
| --- | --- |
| 传输与握手 | TCP 6000+N(默认只听 127.0.0.1)或 `ServeAsync(stream)` 直接喂双工流(以后 SSH 的 x11 通道可以不经端口);两种字节序;`MIT-MAGIC-COOKIE-1`(常数时间比较)或「仅本机」 |
| 执行模型 | 单执行线程串行执行全部请求(X 的语义本就全局串行),读 / 写各一个任务,GrabServer 暂存他人请求;损伤按批合并后通知宿主 |
| 请求 | **全部 119 个核心请求** + BIG-REQUESTS + XC-MISC;抓取只实现异步模式 |
| 窗口 | 窗口树、映射 / 配置 / 堆叠 / 重设父窗口、可见区域(Region)、边框与背景(像素 / 平铺 / ParentRelative / None)、Expose、结构事件、SubstructureRedirect 改道 |
| 绘图 | 软件光栅化:16 种光栅操作、平面掩码、实色 / 平铺 / 点画 / 不透明点画、裁剪矩形与裁剪位图、细线(Bresenham)与宽线、虚线、弧、多边形(奇偶 / 非零)、CopyArea / CopyPlane(GraphicsExposure / NoExposure)、PutImage / GetImage(Bitmap / XYPixmap / ZPixmap;深度 1/4/8/15/16/24/32) |
| 文字 | 内置 fixed / 6x13 / 9x15 / 10x20(及 Bold、ISO8859-1 与 ISO10646-1 两种编码、XLFD 通配)、合成的 cursor 与 nil2;PolyText / ImageText 8 与 16 |
| 输入 | evdev 键码(+8)与 US 键值表、修饰键映射、指针与键盘事件的传播、自动 / 主动 / 被动抓取、Enter / Leave 的五种 detail、焦点;宿主注入 API |
| 属性与选区 | 属性按本机序存放、跨字节序读写一致;选区三件套;SendEvent 按事件布局换字节序;WM_NAME / _NET_WM_NAME / WM_CLASS / WM_TRANSIENT_FOR / WM_DELETE_WINDOW 同步给宿主 |

### 三、验证

- 单元测试 40 条(`tests/VelaShell.XServer.Tests`,内存双工流 + 逐字节的测试客户端,约 0.2 秒):握手(两种字节序、cookie、仅本机)、
  原子、属性分段读取与跨字节序、BIG-REQUESTS 大请求、错误报文、映射 + 背景 + Expose、子窗口裁剪与边框、填充与 CopyArea、
  GXxor 画两遍复原、ImageText 字形、QueryFont / ListFonts、宿主注入的点击 / 按键 / 缩放 / 关闭、区域运算与光栅化。
- **真实客户端**(`[TestCategory("Interop")]`,Docker 镜像 `velashell-xclients`,`VELASHELL_XSERVER_INTEROP=1`):
  `xdpyinfo`、`xterm`、`xeyes`、`xclock`、`xlogo` 全部映射窗口、画出内容、**零协议错误**,5/5 通过。
  手动经 `scripts/xserver/interop/run-server.cs` 注入按键:在 xterm 里敲 `echo Typed: $((6*7)) ABC` 回车,容器里的 sh 执行并回显 `Typed: 42 ABC`。
- 调试中修掉的:像素图深度只认 1/24/32(Xt 会建 4/8 深度的像素图)、xterm 要的 nil2 字体、测试客户端释放时挂住的管道读。

**没做的**(记在 `feature-plan.md`):M2 的 RENDER / XKB / SHAPE / XFIXES / RANDR / XInput2(GTK3 / Qt5 需要)与剪贴板互通,
M3 的宿主接入(Avalonia 原生窗口、HiDPI、替换 VcXsrv 路径)。
文档:velashell-docs 新增 `zh|en/xserver/`(README 与 `design/architecture.md`)。

## ✅ 102. 2026-09-23 VelaShell.XServer M2:现代工具包要的扩展(用户需求)

M1(§101)只有核心协议,GTK3 / Qt5 程序要么起不来、要么只能靠客户端自己把整窗画好再 PutImage。
M2 补上它们实际会用到的扩展,验收是 `zenity`、`gedit`、一个 Qt5 程序画得对且零协议错误。宿主仍未接入(M3)。

### 一、做了什么

| 扩展 / 能力 | 主操作码 · 事件 · 错误 | 内容 |
| --- | --- | --- |
| **SHAPE** 1.1 | 130 · 64 · — | 边界 / 裁剪 / 输入三种形状,Set / Union / Intersect / Subtract / Invert,矩形与位图两种来源、Combine、Offset;形状参与可见区域、绘图裁剪与命中测试;`XTopLevelWindow.Shape` 把顶层的非矩形轮廓交给宿主;ShapeNotify |
| **XFIXES** 5.0 | 131 · 65–66 · 128 | 区域对象全套运算(含 `SetGCClipRegion`、`SetWindowShapeRegion` 接到 SHAPE);选区属主追踪(SetSelectionOwner / 窗口销毁 / 客户端断开三种 SelectionNotify);CursorNotify;`HideCursor` / `ShowCursor`(宿主收到光标字形 −2);指针屏障只登记不生效 |
| **RANDR** 1.5(只读) | 132 · 67–68 · 129–132 | 一台覆盖整个根窗口的虚拟显示器(一个 CRTC、一个输出、一个模式),尺寸与毫米数取自 `XServerOptions`;伽马报 256 级线性斜坡;改配置的请求回 Failed 或 BadAccess |
| **RENDER** 0.11 | 133 · — · 133–137 | 七种 Direct 格式;全部 Porter-Duff / Disjoint / Conjoint 运算与 15 种 PDF 混合模式,预乘 alpha;分量 alpha 遮罩;repeat 四种、投影变换 + 最近邻 / 双线性;梯形 / 三角形 / 条带 / 扇形 / AddTraps 按 16 条子扫描线、水平解析的覆盖率光栅化;字形集与 CompositeGlyphs8/16/32;纯色与线性 / 径向 / 锥形渐变 |
| 剪贴板互通 | — | 宿主 → X:`X11Server.SetClipboardText`,服务端占有 CLIPBOARD,按 ICCCM 回应 TARGETS / TIMESTAMP / UTF8_STRING / STRING / TEXT。X → 宿主:X 客户端占有选区时服务端以隐藏的 InputOnly 窗口为请求方取回(UTF8_STRING → STRING 退路,支持 INCR),交给新增的 `IXServerHost.ClipboardChanged`;宿主把同一段文本写回来时不抢选区(防回声)。`XServerOptions.SyncClipboard`(默认开)/ `SyncPrimary`(默认关) |
| XSETTINGS 管理器 | — | 服务端占有 `_XSETTINGS_S0`,发布 `Xft/DPI`(取 `XServerOptions.Dpi`)与抗锯齿 / 微调几项。真实桌面总有一个设置守护进程,GTK / Qt 启动时会去找;没有时它们各踩一次 BadWindow / BadAtom |
| 诊断 | — | `XServerOptions.Log` 打印协议错误时附上该客户端最近 8 条请求的操作码 —— 上面那两个 XSETTINGS 错误就是靠它一眼看出来的 |

规范依据照 `src/VelaShell.XServer/AGENTS.md` 纪律 1,各文件头写明章节;允许的规范清单补上 XSETTINGS 与 RENDER 引用的 PDF 混合模式公式。

### 二、几处取舍

- **XKB 与 XInput2 没做**(原计划在 M2)。实测 Qt5 没有 XKB 时退回核心协议的键位表(只打一行警告),GTK3 没有 XInput2 时用核心指针事件,
  输入都正常。两者**不能只做一半**:扩展一出现在 `QueryExtension` 里客户端就改走它的路径,XKB 的任何一个回复不对,xkbcommon-x11 建键位表失败,
  键盘反而更糟。拆成 `feature-plan.md` 的一条 💡 P3。
- **RENDER 用浮点逐像素算**,加两条快路径(纯色 Src / 不透明 Over 整块填;Over / Add 下全透明的源像素直接跳过)。正确性优先,
  实测 gedit 的整窗重绘不卡;真成瓶颈时再按格式特化。
- 源 picture 的裁剪、alpha-map、poly-edge / poly-mode / dither 接受但不生效(文件头写明);渲染结果不受影响的客户端占绝大多数。
- 服务端占有的非剪贴板选区(`_XSETTINGS_S0`)不回应剪贴板内容,免得宿主剪贴板从别的选区名泄出去。

### 三、验证

- 单元测试 73 条(M1 的 40 条 + 新增 33 条):SHAPE 4、XFIXES 5、RANDR 4、剪贴板 5(含 INCR、回声、XSETTINGS)、RENDER 协议 7 + 像素 8。
- **真实客户端**(`VELASHELL_XSERVER_INTEROP=1`):interop 用例从 5 条加到 8 条 —— 新增 `xclock -render`、`xeyes -render`、
  `xterm -fa Monospace`(Xft 字形走 RENDER),全部零协议错误。
- 手动(`scripts/xserver/interop/run-server.cs`,靶场镜像加了 `xclip`,runner 加了 `clip` 命令):
  `zenity`(GTK3)、`qt5ct`(Qt5)、`gedit` 画得对且**零协议错误**,往 gedit 里注入按键打出 `Hello gedit on VelaShell.XServer`;
  `xclip` 双向互通(含 12 MB 文本);`xrandr --verbose` / `--listmonitors` 读得到配置,`xrandr --output … --mode …` 如期失败;真实 xeyes 的圆窗形状 200 个矩形。
- 调试中修掉的:GetCrtcGammaSize 起初报 0,xrandr 抱怨 `Failed to get size of gamma`,改成 256 级线性斜坡。

**没做的**(记在 `feature-plan.md`):XKB 与 XInput2(💡 P3),M3 的宿主接入。
文档:velashell-docs `zh|en/xserver/design/architecture.md` 同步扩展清单、宿主接口的新增项与决策记录。
