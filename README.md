<div align="center">

# VelaShell

**为运维与开发者打造的现代化跨平台 SSH 终端客户端**

以终端为帆，乘信号之风驶向远方主机。

[![Release](https://img.shields.io/github/v/release/joesdu/VelaShell?label=release&color=19a88b)](https://github.com/joesdu/VelaShell/releases)
[![CI](https://github.com/joesdu/VelaShell/actions/workflows/ci.yml/badge.svg)](https://github.com/joesdu/VelaShell/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-AGPL--3.0%20%7C%20Commercial-blue)](#license)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-64748b)](#download)

[![.NET](https://img.shields.io/badge/.NET-11.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.1-8B44AC)](https://avaloniaui.net/)

**简体中文** · [English](README.en.md)

**[下载 VelaShell](https://github.com/joesdu/VelaShell/releases)** · **[用户手册](manual.md)** · **[插件商店](https://market.easilynet.top)** · **[开发文档](https://github.com/VelaShellLabs/velashell-docs)**

<img src="mascot/chibi.png" alt="VelaShell 看板娘：抱猫、使用笔记本与挥手的三连插画" width="640">

**键盘优先 · 高效工作区 · 跨平台桌面体验**

</div>

---

VelaShell 把 **SSH 终端、文件传输、隧道、远程图形应用与运维工具**整合进同一个桌面工作区。
从连接一台服务器，到分屏管理多台主机，再到通过插件接入 AI、RDP 远程桌面、Redis、MongoDB、S3 或 Docker，常用操作都能在一个窗口内完成。

基于 **.NET 11 + Avalonia**，支持 Windows、Linux 与 macOS，发行包自带运行时，**无需安装 .NET**。
官方应用对个人与企业**永久免费**；衍生开发与分发的授权方式见[许可证](#license)。

<div align="center">

[下载与上手](#download) · [核心功能](#features) · [AI 助手](#ai) · [插件生态](#plugins) · [数据与隐私](#data) · [开发](#development) · [文档](#docs) · [贡献](#contributing) · [赞助](#sponsor)

</div>

<a id="download"></a>

## 🚀 下载与上手

在 **[GitHub Releases](https://github.com/joesdu/VelaShell/releases)** 选择与你的操作系统和 CPU 架构匹配的安装包。

| 平台 | 架构 | 分发形式 |
| --- | --- | --- |
| **Windows 10 / 11** | x64 · arm64 | 便携 `.zip` · Microsoft Store MSIX |
| **Linux** | x64 · arm64 | 便携 `.tar.gz` · `.AppImage` · `.deb` · `.rpm` |
| **macOS** | x64 · arm64 | `.tar.gz` · `.dmg`（未签名、未公证） |

### 建立第一个 SSH 连接

1. 解压便携包或安装对应的软件包，启动 VelaShell。
2. 点击资源管理器顶部 **「+」**，选择 **SSH**，填写主机、端口、用户名与认证信息。
3. 点击 **「连接」**，通过可信渠道核对服务器指纹后确认，即可进入远端终端。

用 **`Ctrl+P` 命令面板**查找功能；从 WinSCP、Xshell 或 `~/.ssh/config` 迁移时，可直接[导入已有连接](manual.md#connections)。
详细操作见[安装与首次连接](manual.md#start)。

> [!NOTE]
> **更新方式因发行形式而异。** Windows / macOS 的可写便携发行形式支持应用内更新；Linux 当前需手动下载安装。
> Microsoft Store 版由商店更新，且数据与便携版隔离。具体限制与操作见[用户手册](manual.md#start)。

<a id="features"></a>

## ✨ 核心功能

| 场景 | 你可以做什么 |
| --- | --- |
| **终端操作** | VT 终端引擎、真彩色与 CJK 双宽；行 / 块 / 多段选区；超链接、命令块、输出折叠与会话录制 |
| **连接与认证** | SSH、SFTP、FTP / FTPS；密码、私钥、SSH 证书、Agent 与两步验证；共享凭据、跳板机、网络代理与自动重连 |
| **文件管理** | 双栏浏览、拖拽互传、断点续传与队列；目录比较 / 同步；内置编辑器与外部编辑器保存回传 |
| **隧道转发** | 本地 `-L`、远程 `-R`、动态 SOCKS5 `-D`；流量与连接计数、端口冲突预检、断线恢复 |
| **远程图形应用** | X11 转发与内置 X 服务端；每个 X 顶层窗口独立显示，支持双向剪贴板、多显示器与 DPI |
| **运维诊断** | CPU / 内存 / 磁盘 / 网络监视、进程管理、带地理信息的路由追踪、连接诊断与录制回放 |
| **多会话工作区** | VelaDock 拖拽分屏、分组与置顶、批量管理、同步输入、命令片段、智能补全与可自定义快捷键 |
| **外观与语言** | 12 套界面主题、16 套终端配色、内置 Cascadia Mono；简体中文、English、繁體中文、日本語、한국어 |
| **桌面集成** | 系统托盘、开机自启、单实例、URL 协议、Xshell 兼容登录；Windows 本地终端（ConPTY） |

<details>
<summary><b>展开进阶功能：终端、连接与工作区</b></summary>

### 终端体验

- **VT 兼容**：DEC ANSI / VT / Xterm 状态机，十种终端 profile（`vt52` → `xterm-256color`），支持 256 色、真彩色、线绘字符、主备屏与鼠标协议。
- **精细选区**：行 / 块选区，`Shift+左键` 扩展，`Ctrl+Shift+拖拽` 追加多段不连续选区；支持中文输入法预编辑。
- **可读输出**：行号与时间侧栏、OSC 8 超链接（`Ctrl+点击`）、OSC 133 命令块，支持失败标记、提示符跳转、输出选中与按块折叠。
- **记录与回放**：会话日志、录制与回放，可导出 asciicast v2。

### 连接与认证

- **复杂网络**：ProxyJump 最多 5 跳并检测环路；HTTP / SOCKS5 / 系统代理；保活、防空闲及睡眠唤醒 / 网络恢复后的自动重连。
- **登录方式**：密码、私钥、SSH 证书、SSH Agent、keyboard-interactive 两步验证，支持「私钥 + 动态码」。
- **密钥管理**：生成 Ed25519 / ECDSA / RSA 4096 密钥；可在登录后加入 Agent；Agent 转发可限定密钥并在签名前询问。
- **连接配置**：共享凭据、认证后执行命令、SSH 压缩、OpenSSH 风格算法配置；主机指纹首次信任（TOFU）与变更确认。

### 文件与工作区

- **传输与同步**：SFTP 与 FTP / FTPS 共用双栏浏览与传输队列；支持符号链接、SHA-256 优先的目录比较与远端同步。
- **批量管理**：连接 JSON 导入导出（可加密携带密码）、CSV 批量编辑，以及 WinSCP / Xshell / OpenSSH 配置导入。
- **个性化**：界面与终端配色联动，7 套暗色与 5 套亮色主题；界面语言默认跟随系统，无匹配时使用英文。

完整操作与适用条件见[用户手册](manual.md)。

</details>

### 内置 X 服务端

**[VelaShell.XServer](src/VelaShell.XServer/)** 随应用发布，可从标题栏启动，无需额外安装 X 服务端。
它以 rootless 模式将远端 X 顶层窗口映射为桌面原生窗口，支持双向剪贴板、系统键盘布局与 GLX 1.4；
SSH X11 通道在进程内直接接入服务端。Windows 上也可选择已安装的 VcXsrv。

[使用 X11 转发](manual.md#x11) · [X Server 排障](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/xserver/troubleshooting.md)

<a id="ai"></a>

## 🤖 AI 助手

第一方 **AI 助手插件随应用发布**，在工作区内提供流式对话与 Agent 模式。

- **多提供商**：支持 OpenAI Responses、Chat Completions 兼容接口与 Anthropic Messages，覆盖 OpenAI、Anthropic、Grok、Gemini、DeepSeek、Kimi、GLM 等服务。
- **灵活接入**：提供商目录支持 API Key；部分提供商支持浏览器登录或设备码授权。模型列表优先读取端点，并从 models.dev 补充上下文窗口与价格信息。
- **结合会话工作**：Agent 可调用会话、终端、远端命令与文件工具；支持自定义 MCP 服务器、网页检索与抓取，危险操作逐项审批。
- **连续协作**：运行中可插话，用 `@` 引用所选会话的远端文件，浏览、续聊与管理历史对话。
- **团队接入**：支持飞书、钉钉、Telegram 与企业微信，通过配对码授权聊天并检查连通性。

<details>
<summary><b>订阅登录与对外 MCP 服务说明</b></summary>

ChatGPT Plus / Claude Pro / GitHub Copilot 等订阅接入在界面中标为**实验性**，具体可用性以提供商和当前接入方式为准。

插件还提供 **Streamable HTTP MCP 服务端**，让外部 Agent 调用 VelaShell 会话能力。
服务仅绑定 `127.0.0.1`，每个请求必须携带令牌；此路径没有审批界面，因此「询问」模式会拒绝写操作。

接入配置、数据边界与权限说明见[AI 使用手册](manual.md#ai)和[隐私说明](PRIVACY.md)。

</details>

<a id="plugins"></a>

## 🧩 插件生态

通过 **[插件商店](https://market.easilynet.top)** 安装 `.vpx` 插件，按需扩展连接类型与工作台。
除 AI 助手外，下列第一方插件**不随主程序预装**，可在 **插件管理 → 插件商店** 中一键安装，或从
[velashell-plugins Releases](https://github.com/VelaShellLabs/velashell-plugins/releases) 下载已签名的 `.vpx` 手动安装。
第一方插件统一发版、同号更新，与第三方插件使用同一套 SDK 与权限模型。

| 插件 | 入口 | 你可以做什么 |
| --- | --- | --- |
| **RDP 远程桌面** | 新建连接 → RDP | 连接 Windows 远程桌面。分辨率与 DPI 跟随标签页自动调整，支持 GFX / H.264 图形管线、双向文本 / 图像 / 文件剪贴板、文件夹 / 打印机 / 智能卡映射、远端音频、RD Gateway、RemoteApp 与断线自动重连；可经 SSH 跳板连接 |
| **MongoDB 工作台** | `Ctrl+P` →「MongoDB: 打开 MongoDB 工作台」 | 对象树与网格 / 树 / JSON 三视图，修改先进暂存区、预览后提交；查询编辑器与可视化执行计划、聚合管道构建器、Schema 分析与验证规则、索引建议、GridFS、服务器监控、慢查询、用户与角色、导入导出与跨连接数据传输 |
| **Redis** | 新建连接 → Redis | 支持 Redis / Valkey 的单机、哨兵与集群部署及 TLS。提供键空间浏览、类型化编辑与解码链、命令控制台，以及概览、慢日志、客户端、订阅、集群和内存分析；可从 SSH 会话自动探测 Redis 并预填连接 |
| **S3 对象存储** | 新建连接 → S3 | 支持 Amazon S3、MinIO、Ceph RGW、阿里云 OSS、腾讯云 COS、Cloudflare R2 等。复用宿主双栏浏览与传输队列，并提供桶管理器（22 项桶配置）与对象检视器（版本、标签、保留、加密、S3 Select、预签名 URL） |
| **Docker 面板** | `Ctrl+P` →「Docker: 打开 Docker 管理面板」 | 总览、容器、镜像、卷、网络、Compose、系统七个页面；事件驱动实时刷新，提供行内 CPU 曲线、日志、容器内文件浏览与编辑、面板控制台和分级清理；可管理远端或本机 Docker |
| **Telnet** | 新建连接 → Telnet | 连接交换机、路由器等网络设备；支持 RFC 854 选项协商（ECHO / SGA / TERMINAL-TYPE / NAWS / BINARY）与 8 位透明传输 |
| **串口** | 新建连接 → 串口 | 连接 RS-232 / USB 转串口设备；支持端口热插拔枚举、波特率 / 校验 / 流控、换行归一化、本地回显与发送节流，可从命令面板发送 BREAK 或脉冲 DTR 复位开发板 |

Telnet 与串口会话打开的是普通终端标签，宿主的 VT 引擎、回滚、搜索、会话日志、录制与 ZMODEM 全部可用。
RDP、Redis、S3、Telnet 与串口以连接类型接入，与 SSH 连接一样参与分组、会话树、最近连接与 Gist 云同步；MongoDB 在工作台内自行管理连接。所有插件的口令都保存在宿主的加密存储中。

<details>
<summary><b>安全边界与使用注意</b></summary>

- **Docker 面板**复用已有 SSH 会话，经 `direct-streamlocal@openssh.com` 直连远端 `/var/run/docker.sock`：
  无需将 Docker daemon 暴露到 2375 / 2376 端口，本机也不会留下监听端口。能访问 Docker socket 通常等同于主机高权限，清理卷前请确认内容。
- **Redis** 按服务器返回的命令标志将写操作分为「写 / 危 / 毁」三档：生产标记默认只读，危险命令逐次确认，`FLUSHALL` 等需手动输入确认串。
  客户端只读模式只能防止误操作，真正的权限隔离仍需依靠 ACL。
- **MongoDB 与 Redis 的 SSH 跳板**由宿主建立会话与本地转发，插件不接触跳板机凭据。
- **RDP** 由纯托管 C# 协议栈实现，不捆绑原生 RDP / FFmpeg 库，三平台使用同一实现；剪贴板、磁盘、打印机和智能卡映射默认关闭，需要逐项开启。
- **Telnet** 为明文协议，没有主机指纹校验与传输加密，请勿在不可信网络中使用。

各插件的详细用法见[用户手册](manual.md#plugins)，设计说明见
[RDP](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/RDP远程桌面插件设计与使用.md) ·
[MongoDB](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/MongoDB工作台插件设计.md) ·
[Redis](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/Redis客户端插件化调研与设计.md) ·
[S3](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/S3协议插件化设计.md) ·
[Telnet 与串口](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/Telnet与串口可行性调研.md)。
插件源码见 [velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins)。

</details>

### 开发插件

插件支持**进程内加载**与**独立进程运行**，共用 SDK 契约，由清单声明模式。
进程内插件可将 UI 嵌入工作区；独立进程通过命名管道 RPC 与宿主通信，提供心跳、重启和空闲回收。
危险能力逐项授权，发布者签名密钥在安装时固定；插件管理器支持安装、启停、卸载与更新检查。

[开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md) · [SDK 参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/sdk/sdk-reference.md) · [CLI 手册](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/cli/cli.md) · [打包发布](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/publishing.md)

<details>
<summary><b>宿主能力与相关仓库</b></summary>

插件经 `IPluginContext` 访问宿主：

| 能力 | 接口 |
| --- | --- |
| 会话状态与连接管理 | `Sessions` |
| 终端输入输出、远端命令与文件 | `Terminal` · `RemoteExec` · `RemoteFs` |
| 复用 SSH 通道 | `RemoteTunnel` |
| 注册连接类型与工作台 | `Protocols` · `Workspaces` |
| 插件私有数据与加密机密 | `Storage` · `TimeSeries` · `Secrets` |
| 命令、事件、面板与桌面交互 | `Commands` · `Events` · `Ui` · `Clipboard` · `Log` |

| 仓库 | 职责 |
| --- | --- |
| **[VelaShell](https://github.com/joesdu/VelaShell)** | 主程序、插件运行时、SSH 库、X 服务端与 AI 插件 |
| [velashell-plugin-sdk](https://github.com/VelaShellLabs/velashell-plugin-sdk) | SDK 契约、测试替身与构建工具包 |
| [velashell-plugin-cli](https://github.com/VelaShellLabs/velashell-plugin-cli) | `vela-plugin` 命令行工具 |
| [velashell-plugin-templates](https://github.com/VelaShellLabs/velashell-plugin-templates) | `dotnet new velaplugin` 模板 |
| [velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins) | 按需安装的第一方插件 |
| [velashell-docs](https://github.com/VelaShellLabs/velashell-docs) | 生态设计与开发文档，中英文镜像 |

SDK 通过 NuGet 引用，版本集中在 `src/Directory.Packages.props` 与 `tests/Directory.Packages.props`。
用 `vela-plugin dev init` 配置调试后可按 F5 启动；测试替身由 `VelaShell.PluginSdk.Testing` 提供。
AI 插件与宿主同仓构建、同版发布，原因见 [`plugins/README.md`](plugins/README.md)。

</details>

<a id="data"></a>

## 🔐 数据与隐私

连接、设置与历史记录保存在本机，凭据使用 **AES-256-GCM** 加密。
可选的 **GitHub Gist 云同步**支持口令端到端加密与历史版本；安全审计日志支持筛选与保留期配置。
联网行为、AI 数据流与第三方服务说明见 **[PRIVACY.md](PRIVACY.md)**。

<details>
<summary><b>数据位置、密钥与迁移</b></summary>

普通发行版默认使用 `~/.velashell`；开发时可通过 `--data-root <目录>` 指定独立数据根。

| 内容 | 默认位置 |
| --- | --- |
| SonnetDB 数据（连接、分组、设置、指纹、历史、审计、录制、插件数据） | `~/.velashell/sonnetdb` |
| 会话日志与诊断日志 | `~/.velashell/logs` |
| 用户安装的插件 | `~/.velashell/plugins` |
| 随包插件 | 程序目录的 `plugins/` |
| 宿主登记 / 插件开发挂载 / 影子副本 | `~/.velashell/host.json` · `plugins.dev.txt` · `dev-shadow/` |
| 密钥管理页生成的 SSH 密钥对 | `~/.ssh` |

凭据加密密钥优先保存在 **macOS 登录钥匙串 / Linux Secret Service**，数据目录中的 `secret.key.ref` 仅保存条目引用。
Windows 与系统密钥链不可用时使用 `secret.key`；Windows 上由 DPAPI 绑定当前用户。
**仅复制数据目录不能保证在其他设备或账户上解密凭据**，换机请按[备份与迁移手册](manual.md#backup)操作。

旧版 JSON 配置与 `%LocalAppData%/VelaShell` 数据根会在首次运行时迁移，旧文件保留为 `*.migrated.bak` 或存入 `.migration-backup/`。
Microsoft Store 版的数据由系统重定向，与便携版不自动互通。

</details>

<a id="development"></a>

## 🛠️ 从源码开发

需要 **[.NET 11 SDK](https://dotnet.microsoft.com/download)**。仓库启用预览语言特性与 `runtime-async`，
没有 `global.json`；CI 使用 `11.0.x`。Docker 仅在需要本地 SSH 测试服务器或相关互操作测试时使用。

```powershell
git clone https://github.com/joesdu/VelaShell
cd VelaShell

dotnet build VelaShell.slnx -c Debug
dotnet run --project src\VelaShell\VelaShell.csproj -- --data-root "$HOME/velashell-dev"
```

以上为 PowerShell 示例；Bash / Zsh 下将项目路径中的 `\` 改为 `/`。

> [!TIP]
> 开发时使用独立的 `--data-root`：数据与单实例锁都跟随该目录，避免连接到日常配置。
> 构建前关闭使用同一输出目录的应用，防止文件占用。

<details>
<summary><b>项目结构与技术栈</b></summary>

```text
VelaShell/
├── src/
│   ├── VelaShell/                 # 桌面入口、DI 组合根、视图与 VelaDock
│   ├── VelaShell.Terminal/        # VT 引擎与终端渲染
│   ├── VelaShell.Presentation/    # ViewModel 与工作流
│   ├── VelaShell.Controls/        # 复用控件、主题令牌与字体
│   ├── VelaShell.Core/            # 领域模型、契约与本地化，无 UI 依赖
│   ├── VelaShell.Infrastructure/  # 连接、持久化、加密、同步与插件管理
│   ├── VelaShell.Ssh/             # SSH / SFTP / 转发库，MIT
│   ├── VelaShell.XServer/         # X11 服务端库，MIT
│   └── VelaShell.PluginHost/      # 隔离插件宿主进程
├── plugins/VelaShell.Plugin.Ai/   # 随包发布的 AI 助手
├── tests/                         # 单元、UI、互操作测试与基准
├── build/                         # 平台打包脚本
├── scripts/                       # 发布、测试与代码生成工具
└── VelaShell.slnx                 # 解决方案
```

各项目的 `README.md` 说明内部职责与依赖。Core 不依赖 UI 框架，DI 在
[`App.axaml.cs`](src/VelaShell/App.axaml.cs) 统一组合；SSH 与 X Server 两个库不依赖宿主程序集。

| 领域 | 技术 |
| --- | --- |
| 界面 | Avalonia 12.1 · ReactiveUI · VelaDock · AvaloniaEdit |
| 连接与数据 | VelaShell.Ssh · FluentFTP · SonnetDB · BouncyCastle · MaxMind.Db |
| AI 插件 | Microsoft.Extensions.AI · ModelContextProtocol · LiveMarkdown.Avalonia |
| 工程 | MSTest · BenchmarkDotNet · 集中式包管理 · SourceLink |

架构详见[宿主分层架构](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/architecture.md)，
界面规范见 [`DESIGN.md`](DESIGN.md)。

</details>

<details>
<summary><b>测试与本地插件联调</b></summary>

```powershell
dotnet test VelaShell.slnx -c Debug
dotnet test tests\VelaShell.Terminal.Tests -c Debug
dotnet run -c Release --project tests\VelaShell.Benchmarks -- --filter '*VtParser*'

docker compose -f docker-compose.test.yml up -d
# 本地 SSH 测试服务器：testuser / testpass @ localhost:2222
```

测试覆盖领域模型、VT 解析与像素渲染、ViewModel、持久化与加密、插件 RPC、headless UI，
以及真实 SSH / X 客户端的互操作。BenchmarkDotNet 用于同机前后对比，不作为 CI 门禁。

集成测试需要相应环境：`DockerIntegration` 依赖 Docker，跨平台发布测试需 `VELASHELL_PUBLISH_TESTS=1`，
X Server 互操作需测试镜像与 `VELASHELL_XSERVER_INTEROP=1`。查看测试报告中的跳过项，不能将环境未满足视为已验证。
headless UI 测试约定见 [`AGENTS.md`](AGENTS.md)。

干净克隆仅构建随包的 AI 插件。其他插件可从 `.vpx` 解出后放入 `artifacts/plugins/`，
或用 `-p:VelaPluginsStageDir=<目录>` 指定暂存目录；构建时会镜像到输出目录的 `plugins/`。
**不要重复放入 AI 插件**，重复 ID 会导致插件无效。

</details>

<details>
<summary><b>构建发布包与 CI</b></summary>

```powershell
pwsh scripts\publish-all.ps1
```

发布包输出到 `publish/`，覆盖三平台 x64 / arm64，包含运行时、`VelaShell.PluginHost` 与 AI 插件。
`.dmg` 在 macOS runner 生成；Linux 打包说明见 [AppImage](build/appimage/README.md) 与 [deb / rpm](build/linux-packages/README.md)。

| 流水线 | 触发与职责 |
| --- | --- |
| [`ci.yml`](.github/workflows/ci.yml) | `main` 推送、PR 或手动触发；三平台 Debug 构建与测试，警告视为错误；Linux 另以 `runtime-async=off` 检查跨 `await` 的 Span |
| [`release.yml`](.github/workflows/release.yml) | Release 发布或编辑；三平台打包，生成 `SHA256SUMS.txt`、`latest.json` 与供商店提交的 MSIX；打包前抓一次 models.dev 规格快照打进 AI 插件（[`scripts/Update-ModelsDevSnapshot.ps1`](scripts/Update-ModelsDevSnapshot.ps1)，抓取失败只警告） |

CI 使用 Debug，避免依赖 Release 强名签名密钥；筛除需要额外环境的 `DockerIntegration` / `CrossPlatform` / `Interop` 分类。
快捷键文档比对需并排检出 `velashell-docs`。版本由 Release 标签经 `-p:Version` 注入，`0.0.1-dev` 仅是开发占位。

</details>

<a id="docs"></a>

## 📚 文档与路线图

| 我想…… | 入口 |
| --- | --- |
| 安装、连接、传文件、配置 AI 或排障 | [中文用户手册](manual.md) · [English manual](manual.en.md) |
| 查快捷键与交互设计 | [快捷键参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/快捷键参考.md) · [宿主文档](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host) |
| 了解第一方插件的用法与设计 | [插件使用手册](manual.md#plugins) · [第一方插件仓库](https://github.com/VelaShellLabs/velashell-plugins) · [插件设计文档](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host) |
| 开发插件或了解权限模型 | [插件开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md) · [插件设计](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins) |
| 了解 SSH / X11 实现 | [SSH 文档](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/ssh) · [X Server 文档](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/xserver) |
| 查看已完成的工作与后续计划 | [进展记录](plan.md) · [路线图](feature-plan.md) · [插件进度](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md) |
| 查看版本变更 | [Releases](https://github.com/joesdu/VelaShell/releases) · [更新日志归档](changelogs/) |

项目处于**活跃开发**阶段。当前行为与限制以用户手册和对应版本的发布说明为准。
设计与开发文档集中在 [velashell-docs](https://github.com/VelaShellLabs/velashell-docs)，`zh/` 与 `en/` 互为镜像；
用户手册、贡献约定和项目级说明随代码保留在本仓库。

<a id="contributing"></a>

## 🤝 参与贡献

欢迎通过 **[Issue](https://github.com/joesdu/VelaShell/issues)** 反馈问题或提出建议，也欢迎提交 Pull Request。
开始前请阅读 **[贡献指南](CONTRIBUTING.md)**；AI 代理与新加入者还需阅读 [`AGENTS.md`](AGENTS.md)。

界面文案需同步五种语言，颜色使用设计令牌，快捷键以 `ShortcutCatalog.cs` 为准，SDK 通过 NuGet 引用。
修改 SSH / X Server 库前请阅读各目录的净室开发约定；行为变更需同步用户手册与相关设计文档。

安全漏洞请按 **[SECURITY.md](SECURITY.md)** 私下报告，不要提交公开 Issue。

<a id="sponsor"></a>

## 💖 支持 VelaShell

VelaShell 对个人与企业**永久免费**。赞助完全自愿，不解锁额外功能，也不区分「赞助版」与「免费版」。

| 渠道 | 支持方式 |
| --- | --- |
| [爱发电](https://afdian.com/a/velashell) | 支付宝 / 微信 |
| [Wise](https://wise.com/pay/me/yud162) | 信用卡 / 银行转账 |

<details>
<summary><b>支付宝 / 微信支付二维码</b></summary>

<p align="center">
  <img src="src/VelaShell/Assets/donate-alipay.png" width="200" alt="支付宝捐赠二维码">
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="src/VelaShell/Assets/donate-wechat.png" width="200" alt="微信支付捐赠二维码">
</p>

</details>

应用内 **设置 → 支持与捐赠** 提供相同入口。感谢每一位支持者。

### 致谢捐赠者

按时间倒序排列，未留名的记为「匿名」。

<!-- 新记录加在表头下的第一行，金额保留币种；同步更新 README.en.md。 -->

| 捐赠人 ID | 金额 | 渠道 |
| :---: | :---: | :---: |
| *清 | ￥50 | 微信支付 |
| 天真i | ￥50 | QQ 支付 |
| s*y | ￥10 | 微信支付 |

可在留言或转账备注中填写希望展示的 ID；如需匿名、改名或移除，请联系 <dygood@outlook.com>。

<a id="license"></a>

## 📄 许可证

VelaShell 采用 **AGPL-3.0 / 商业授权双许可**：

| 授权 | 说明 |
| --- | --- |
| **[AGPL-3.0](LICENSE)** | 默认开源许可。使用、修改与分发须遵守许可证；衍生作品的源码提供与网络服务义务以许可证全文为准。 |
| **[商业授权](LICENSE-COMMERCIAL.md)** | 需要闭源集成、闭源分发，或企业合规无法接受 AGPL 时，可联系作者购买商业许可。 |
| **MIT 组件** | [VelaShell.Ssh](src/VelaShell.Ssh/LICENSE) 与 [VelaShell.XServer](src/VelaShell.XServer/LICENSE) 按各目录的 MIT 许可证授权。 |

请保留项目版权与许可证信息。移除项目信息后违规闭源分发或售卖，版权方保留依法追究的权利。
官方应用永久免费，官方发布渠道为本仓库 **GitHub Releases** 与 **Microsoft Store** 同名商店版。
**VelaShell 名称与 Logo 不在开源许可授权范围内**，未经许可，衍生版本不得使用它们宣传或售卖。

商业授权联系：<dygood@outlook.com>，邮件标题注明 **Commercial License**。
提交贡献即表示同意以 AGPL-3.0 授权，并授予版权方商业再许可权，详见 [商业授权说明 §3](LICENSE-COMMERCIAL.md#3-说明)。

---

<details>
<summary><b>🪶 关于 VelaShell 这个名字</b></summary>

**读音**：`/ˈveɪlə ʃɛl/`，读作 **VAY-la shell**，中文近似「薇拉·谢尔」，重音在第一个音节。

**Vela** 在拉丁语中意为「帆」，也是南天的船帆座，与龙骨座、船尾座同源于阿尔戈船座；
**Shell** 则是连接远程主机的命令行外壳。两者合起来，寓意「以终端为帆，乘信号之风驶向远方主机」。
青绿渐变图标上的 `>_`，正是这一理念的缩影。

</details>

<div align="center">

**VelaShell · 为命令行而生**

[下载](https://github.com/joesdu/VelaShell/releases) · [使用手册](manual.md) · [反馈问题](https://github.com/joesdu/VelaShell/issues)

© 2026 VelaShell 作者及贡献者

</div>
