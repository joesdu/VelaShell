<div align="center">

# VelaShell

**一款为运维与开发者打造的现代化跨平台 SSH 终端客户端**

`/ˈveɪlə ʃɛl/` · 以终端为帆，乘信号之风驶向远方主机

[![.NET](https://img.shields.io/badge/.NET-11.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.1-8B44AC)](https://avaloniaui.net/)
[![CI](https://github.com/joesdu/VelaShell/actions/workflows/ci.yml/badge.svg)](https://github.com/joesdu/VelaShell/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/joesdu/VelaShell?label=release)](https://github.com/joesdu/VelaShell/releases)
[![License](https://img.shields.io/badge/license-AGPL--3.0%20%7C%20Commercial-blue)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)](#-平台与分发)

**简体中文** · [English](README.en.md) · [插件商店](https://market.easilynet.top) · [文档](https://github.com/VelaShellLabs/velashell-docs)

<img src="mascot/chibi.png" alt="VelaShell 看板娘" width="720">

</div>

---

VelaShell 是用 **.NET 11 + Avalonia** 写的桌面终端应用，Windows / Linux / macOS 三平台原生发布、
自包含（目标机器不装 .NET Runtime 也能跑），对个人与企业**永久免费**。

它把远程运维一天里要用到的东西装进了同一个窗口：VT 终端引擎、SSH / SFTP / FTP 连接、本机终端、
跳板机与网络代理、端口转发隧道、SFTP 双栏与目录同步、远程编辑、**内置 X 服务端**、资源监视与路由追踪、
会话录制回放、命令面板与十二页设置中心。再往外，是一套**双模插件系统**（进程内 / 独立进程）
与第一方 **AI 助手插件**。

三条贯穿始终的取舍：**键盘优先**、**信息密度高**、**响应迅速**。

---

## ✨ 功能一览

| | |
| --- | --- |
| **终端** | VT 引擎（DEC ANSI / VT / Xterm 状态机）· 十种终端 profile（vt52 → xterm-256color）· 256 色 / 真彩 / 线绘字符 / 主备屏 / 鼠标协议 / CJK 双宽 · 自绘渲染 · 行 / 块 / 多段不连续选区 · 行号与时间侧栏 · 中文输入法预编辑 · OSC 8 超链接（Ctrl+点击）· OSC 133 命令块（失败标红、提示符间跳转、一键选中输出、按块折叠） |
| **连接** | SSH · SFTP · FTP / FTPS · 本地终端（Windows ConPTY）· 跳板机 ProxyJump（≤5 跳、环检测）· HTTP / SOCKS5 / 跟随系统代理 · 断线自动重连（含睡眠唤醒 / 网络恢复）· 保活与防空闲 · 每条连接各配「认证后执行命令」· SSH 压缩 · 算法清单可配（一键放开老算法连老交换机，或按 OpenSSH 写法自定义） |
| **认证与密钥** | 密码 / 私钥 / **SSH 证书** / **SSH Agent** · **两步验证**（keyboard-interactive：服务端要动态码时弹框，口令自动代答；也支持「钥 + 动态码」）· Agent 转发（可只转发选中的钥、每次签名前询问）· 主机指纹 TOFU，指纹变更弹窗裁决 · 密钥生成（Ed25519 默认 / ECDSA / RSA 4096）· 登录后自动把钥加进 agent（可选）· **共享凭据**（多条连接共用一套账号与密码 / 密钥，改一处，所有连接下次连接即生效） |
| **X11** | X11 转发 · **内置 X 服务端 VelaShell.XServer**：每个 X 窗口一个原生窗口、剪贴板双向、多显示器与 DPI、键盘布局跟随系统、GLX 1.4 · Windows 上也可改用已装的 VcXsrv |
| **文件** | SFTP 双栏与拖拽互传 · 断点续传与传输队列 · **目录比较与同步**（SHA-256 优先比较、保持远端最新）· 符号链接 · 远程文件内置编辑器（语法高亮）或外部编辑器保存即回传 |
| **隧道** | 本地 `-L` / 远程 `-R` / 动态 SOCKS5 `-D` · 实时连接数与字节数 · 断线自动恢复 · 端口冲突预检 |
| **运维** | 资源监视（CPU / 内存 / 磁盘 / 网络 / 进程）· 进程管理器 · 路由追踪（带地理信息）· 连接诊断 · 会话录制与回放（可导出 asciicast v2） |
| **工作区** | VelaDock 拖拽分屏 · 分组会话管理与置顶（分组可拖动排序）· 多选批量打开 / 修改 / 删除 · 连接导入导出（JSON 可加密带上密码、CSV 用 Excel 批量编辑）· 从 WinSCP / Xshell / `~/.ssh/config` 导入 · 命令面板（`Ctrl+P`）· 快捷键可改键、可解绑 · 快捷命令片段 · **多终端同步输入** · 命令智能补全 · 消息中心与安全资讯源 |
| **数据** | 嵌入式 SonnetDB（文档 + 时序）· AES-256-GCM 凭据加密 · GitHub Gist 云同步（可选口令端到端加密、版本可回溯）· 安全审计日志（可筛选查看，保留天数可配）· 会话日志 |
| **外观** | 12 套具名主题（7 暗 5 亮）· 16 套终端配色，与界面主题成对联动 · 全令牌化、零硬编码颜色 · 内置 Cascadia Mono |
| **本地化** | 简体中文 / English / 繁體中文 / 日本語 / 한국어 —— 默认跟随系统语言，对不上时用英文；五份 resx 键集完全一致，漏译与孤儿键都会让测试变红 |
| **扩展** | 双模插件宿主（可收集 ALC / 独立进程 + 命名管道 RPC）· `.vpx` 打包与插件管理器（检查更新、发布者指纹钉住）· 危险能力逐项授权 · 第一方 AI 助手插件 |
| **桌面集成** | 单实例 · 托盘 · 开机自启 · Xshell 兼容登录（可被堡垒机 / SSO 门户外部拉起）· URL 协议注册 |

<details>
<summary><b>展开：几处值得多说两句的实现</b></summary>

- **VT 终端引擎** —— 终端是自绘的 Avalonia 控件，字形、选区与滚动全部自己渲染，
  不依赖任何已停维护的第三方终端控件。选区支持行 / 块两种模式、`Shift+左键` 扩展，
  以及 `Ctrl+Shift+拖拽` 追加的多段不连续选区（可一次复制第 1 行 + 第 3 行）。

- **SSH 栈** —— [VelaShell.Ssh](src/VelaShell.Ssh/)（全托管、async-first、MIT，源码在本仓库），
  按 RFC 与 [velashell-docs 的行为规格](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/ssh)
  以净室规程实现，并对真实 OpenSSH 跑互操作用例。跳板机以嵌套连接 + `direct-tcpip` 逐跳建链，
  指纹按各跳逻辑主机分别校验；指纹变了弹窗摆出新旧指纹交由用户裁决，而不是一句报错了事。

- **隧道的数据面也在 SSH 库里** —— 本地 / 动态 / 远程三种转发连同 SOCKS5 服务端握手（RFC 1928）
  都由 `VelaShell.Ssh` 实现，逐条转发的字节数与并发连接数直接从库里读。
  搬运保留半关闭语义，否则「发完请求就 shutdown 再等响应」的协议全部读不到东西。

- **内置 X 服务端** —— [VelaShell.XServer](src/VelaShell.XServer/)（MIT，纯托管、零原生依赖）
  随程序分发，标题栏一个按钮就能起。它是 rootless 的：每个 X 顶层窗口是一个 Avalonia 原生窗口，
  宿主兼任窗口管理器；SSH 的 X11 通道在进程内直接接进服务端，不绕本机 TCP。
  GTK3 / Qt5 / Mesa（`glxgears`）实测零协议错误。

- **FTP / FTPS** —— 基于 [FluentFTP](https://github.com/robinrodricks/FluentFTP)，
  带连接池以支持并发传输（一条 FTP 控制连接同时只能跑一条命令），与 SFTP 共用同一套双栏与传输栈；
  服务器证书未通过校验时给出 SHA-256 指纹交由用户确认。

- **主题是种子色 + 派生** —— 一套主题只需人工填 25 个种子色（`UiThemePalette`），
  其余六十多个令牌由 `ThemeTokenApplier` 按固定规则派生，逐主题的对比度由测试把关。

- **快捷键只有一个事实来源** —— `src/VelaShell/ViewModels/ShortcutCatalog.cs`，
  设置页与文档都从它取数；没登记的话 `ShortcutCatalogTests` 会失败，并打印出可直接粘贴的 Markdown 行。

</details>

### 🤖 AI 助手插件（第一方，随包发布）

<details>
<summary>点开看它做了什么</summary>

- **多提供商流式对话** —— OpenAI Responses / Chat Completions 兼容 / Anthropic Messages 三种线协议，
  覆盖 OpenAI、Anthropic、Grok、Gemini、DeepSeek、Kimi、GLM 与各类中转站。
- **点一下就连上** —— 内置供应商目录：支持登录的直接开浏览器授权（授权码 + PKCE / 设备码），
  其余的只问一把 API Key。已经在付 **ChatGPT Plus / Claude Pro / GitHub Copilot** 的用户
  可以直接用手里的订阅额度（界面上标了「实验性」）。
- **模型清单** —— 先问端点自己的 `/models`，再按 id 从 models.dev 补上下文窗口与单价。
- **Agent 模式** —— 基于 `Microsoft.Extensions.AI` 的工具循环，工具桥接到会话 / 终端 / 远端命令 / 远端文件，
  危险操作逐条审批；可挂接自定义 **MCP 服务器**；另带网页检索与抓取。
- **协作接入** —— 把 agent 接到 **飞书 / 钉钉 / Telegram / 企业微信**：配对码授权群聊、一键放行、当场验连通性。
- **对外 MCP 服务端** —— Streamable HTTP，只绑 `127.0.0.1` 且每个请求必须带令牌，
  把 VelaShell 的会话能力开放给外部 agent。这条路上没有审批界面，所以「询问」模式等于一律拒绝写操作。
- **交互细节** —— 跑动中可随时插话；`@` 引用所选会话的远端文件；对话可翻回、续聊、删除。

</details>

---

## 🖥️ 平台与分发

| 平台 | 架构 | 分发形式 |
| --- | --- | --- |
| **Windows** 10 / 11 | x64 · arm64 | 便携 zip（应用内自动更新）· Microsoft Store MSIX |
| **Linux** | x64 · arm64 | 便携 tar.gz · `.AppImage` · `.deb` / `.rpm` |
| **macOS** | x64 · arm64 | tar.gz · `.dmg`（未签名 / 未公证） |

全部为 **Self-contained** 发布，解压到任意目录即可运行。

- **应用内自动更新**（设置 → 关于）只走 zip / tar.gz：下载、SHA-256 校验后，由应用退出后才动手的换版进程完成替换并重启；
  中途失败自动还原，`~/.velashell` 下的用户数据全程不碰。更新通道（stable / preview）可在设置页切换。
- dmg、AppImage、deb、rpm 装完后应用目录不可写，关于页会降级成「请手动下载」。
- Store 版（MSIX）由商店负责更新，数据目录被系统重定向，**与便携版的配置、会话、密钥互不相通**。

---

## 🚀 快速开始

### 环境要求

- [.NET SDK **11.0**](https://dotnet.microsoft.com/download) 预览版 —— `global.json` 钉在 `11.0.100-rc.1.26425.128`（`rollForward: latestFeature`）。
  仓库开启了 `EnablePreviewFeatures` 与 `runtime-async=on`，正式版 SDK 之前只能用预览版构建。
- （可选）Docker，用于起本地 SSH 测试服务器与互操作用例

### 构建与运行

```bash
git clone https://github.com/joesdu/VelaShell
cd VelaShell

dotnet build VelaShell.slnx                 # 整个解决方案（含插件宿主与 AI 插件）
dotnet test  VelaShell.slnx                 # 全量测试

dotnet run --project src/VelaShell/VelaShell.csproj -- --data-root ~/velashell-dev   # 用独立数据目录启动
```

> - **应用正在运行时构建会因文件占用失败**，先关掉应用。
> - 开发时建议带 `--data-root <目录>`：数据与单实例锁都跟着它走，不会碰到、也不会连回你日常用的配置。

<details>
<summary>本机想连 Redis / S3 / Telnet 插件一起跑？</summary>

干净克隆构建出来只带本仓库的 **AI 插件**。把其余插件目录铺进暂存目录 `artifacts/plugins/`
（或用 `-p:VelaPluginsStageDir=<目录>` 指别处），构建时会自动镜像到输出目录的 `plugins/<插件目录名>/`，
F5 即可加载。插件目录可以从 [velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins)
Release 里的 **`.vpx` 包**解出来（`.vpx` 是 VelaShell 的插件包格式，里面就是安装后 `plugins/<插件>/` 那一层），
也可以直接指向那边的构建产物。发布流水线**不预装**这些插件，用户按需从[插件商店](https://market.easilynet.top)安装。

> ⚠️ 别把 `velashell-ai` 也铺进来 —— 本仓库自己就产出它，同一个 id 出现两份会被判重，
> 后来者标 Invalid，表象是「插件莫名其妙用不了」。

</details>

### 本地 SSH 测试服务器

```bash
docker compose -f docker-compose.test.yml up -d
# testuser / testpass @ localhost:2222
```

### 数据与配置位置

| 内容 | 位置 |
| --- | --- |
| SonnetDB 数据目录（连接 / 分组 / 设置 / known_hosts / 历史 / 审计 / 录制 / 插件数据） | `~/.velashell/sonnetdb` |
| 凭据加密密钥（AES-256） | `~/.velashell/secret.key` |
| 会话日志（开启后）与诊断日志 | `~/.velashell/logs` |
| 用户手动安装的插件（`.vpx`） | `~/.velashell/plugins`（第一方插件在程序目录的 `plugins/`） |
| 宿主自登记（供 `vela-plugin` 定位） | `~/.velashell/host.json` |
| 插件开发期挂载与影子副本 | `~/.velashell/plugins.dev.txt`、`~/.velashell/dev-shadow/` |
| SSH 密钥对（密钥管理页） | `~/.ssh` |

> 旧版的 JSON 配置与 `%LocalAppData%/VelaShell` 数据根会在首次运行时自动迁移到 `~/.velashell`，
> 旧文件改名为 `*.migrated.bak` 或存进 `.migration-backup/`。

---

## 📦 构建与发布

```bash
pwsh scripts/publish-all.ps1     # 一键产出全平台发布包 → publish/
```

产物覆盖三平台 x64 / arm64，全部含运行时。包内除主程序外还带隔离插件的宿主进程 `VelaShell.PluginHost`
与只放着 AI 插件的 `plugins/`。`.dmg` 只在 CI 的 macOS runner 上生成；`.AppImage`
（[`build/appimage/`](build/appimage/README.md)）与 `.deb` / `.rpm`
（[`build/linux-packages/`](build/linux-packages/README.md)）在同一台 x64 runner 上交叉打出。

| 流水线 | 触发 | 做什么 |
| --- | --- | --- |
| [`ci.yml`](.github/workflows/ci.yml) | push `main` / 全部 PR | windows + ubuntu + macos 三平台矩阵，Debug 构建 + 全量测试，`-warnaserror` |
| [`release.yml`](.github/workflows/release.yml) | 发布 Release | 三平台并行打包，附上 `SHA256SUMS.txt` 与自动更新清单 `latest.json`；另打 **MSIX** 供 Microsoft Store 提交 |

CI 刻意用 **Debug**（强名签名只在 Release 打开，fork 与 Dependabot 的 PR 拿不到仓库 secret），
**排除** `DockerIntegration` / `CrossPlatform` 分类，并把 `velashell-docs` 并排检出（快捷键比对用例要找得到它）。
版本号在发版时由 Release 标签经 `-p:Version` 覆盖，**发版无需改代码** —— 仓库里的 `0.0.1-dev` 只是开发期占位。

---

## 🏗️ 代码结构

```text
VelaShell/
├── src/
│   ├── VelaShell/                  # 桌面应用入口、DI 组合根、XAML 视图、VelaDock 停靠、X 服务端的窗口宿主
│   ├── VelaShell.Terminal/         # VT 终端引擎与 Avalonia 渲染控件
│   ├── VelaShell.Presentation/     # 跨层 ViewModel 与工作流
│   ├── VelaShell.Controls/         # 复用控件、主题令牌与内置字体
│   ├── VelaShell.Core/             # 领域模型、服务契约、持久化抽象、本地化（无 UI 依赖）
│   ├── VelaShell.Infrastructure/   # SSH / SFTP / FTP / 隧道实现、SonnetDB 持久化、凭据加密、同步、插件管理
│   ├── VelaShell.Ssh/              # SSH 库（MIT，净室实现，见其 AGENTS.md）
│   ├── VelaShell.XServer/          # X11 服务端库（MIT，净室实现，见其 AGENTS.md）
│   └── VelaShell.PluginHost/       # 隔离插件的宿主进程（命名管道 RPC，只依赖 SDK 契约）
├── plugins/VelaShell.Plugin.Ai/    # 第一方 AI 助手插件（与主程序同仓构建、同版发布）
├── tests/                          # 11 个 MSTest 项目 + 1 个 BenchmarkDotNet 项目 + 证书靶机 cert-lab
├── build/                          # AppImage / deb / rpm / MSIX / 社交预览图的打包脚本
├── scripts/                        # 一键发布脚本，以及 SSH / X 服务端的互操作靶机、基准与代码生成
├── docker-compose.test.yml         # 本地 SSH 测试服务器
├── global.json                     # SDK 版本锁定
├── Directory.Build.props           # 全仓版本与公共 MSBuild 属性
└── VelaShell.slnx                  # 解决方案
```

每个源项目与测试项目自带 `README.md`，说明它的职责、目录结构与依赖关系。

### 架构约定

- **严格分层** —— `VelaShell → Presentation / Controls / Infrastructure → Core`，Core 不依赖任何 UI 框架；
  `VelaShell.Ssh` 与 `VelaShell.XServer` 不依赖宿主任何程序集，可以单独拿走用。
- **单一组合根** —— 全部 DI 注册集中在 [`src/VelaShell/App.axaml.cs`](src/VelaShell/App.axaml.cs)，各层通过 `*ServiceCollectionExtensions` 贡献注册。
- **单引擎持久化** —— 一个嵌入式 SonnetDB 实例承载文档与时序两类数据，接口在 Core、实现在 Infrastructure。
- **设计令牌化** —— **XAML 与 C# 里不许出现颜色字面量**，一律 `DynamicResource` 绑定令牌（规范见 [`DESIGN.md`](DESIGN.md)）。
- **安全默认值** —— 凭据静态加密、主机指纹 TOFU、「记住密码」可按连接关闭、插件危险能力逐项授权；应用会在什么时候对外发请求，逐条如实写在 [`PRIVACY.md`](PRIVACY.md)。

---

## 🧩 插件系统

**双模宿主** —— 插件既可**进程内**装载（可收集 `AssemblyLoadContext`，UI 直接并入停靠工作区），
也可跑在**独立进程** `VelaShell.PluginHost` 里（命名管道 RPC，崩溃不波及主程序，带心跳、自愈重启与空闲回收）。
两种模式共用同一套 SDK 契约，由插件清单声明。

**能力面** —— 插件经 `IPluginContext` 访问宿主能力：

| 能力 | 做什么 |
| --- | --- |
| `Sessions` | 会话枚举 / 状态 / 按已保存配置开关会话 |
| `Terminal` | 读终端输出、写终端输入 |
| `RemoteExec` · `RemoteFs` | 远端命令执行、远端文件读写与目录列举 |
| `RemoteTunnel` | 复用宿主既有连接开 `direct-tcpip` / `direct-streamlocal` 通道 |
| `Protocols` · `Workspaces` | **注册新的连接类型与工作台** —— Telnet / 串口 / Redis / S3 就是这么接进会话树的 |
| `Storage` · `TimeSeries` · `Secrets` | 插件私有的文档、时序存储与经宿主加密的机密 |
| `Commands` · `Events` · `Ui` · `Clipboard` · `Log` | 注册命令、订阅事件、开面板（停靠文档或独立窗口） |

危险能力经权限对话框逐项授权；协议与工作台的 id 强制插件前缀；同一个插件的后续版本必须由安装时钉住的那把钥签名。
插件以 `.vpx` 包分发，管理窗口可安装 / 启停 / 卸载 / 检查更新，卸载时私有数据一并清理。
第三方开发者一条命令即可断点调试（`vela-plugin dev init` → F5），SDK 另带测试替身（`VelaShell.PluginSdk.Testing`）。

📖 [开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)
· [命令行手册](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/cli/cli.md)
· [SDK 参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/sdk/sdk-reference.md)
· [打包发布](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/publishing.md)
· [设计蓝图](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins)

**由插件提供的能力**（不随安装包预装，从[插件商店](https://market.easilynet.top)按需安装，源码在
[velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins)）：

- **Telnet、串口（COM / USB 转串口）、Redis、S3**
- **Docker 面板** —— 在已经连上的 SSH 会话上开一个完整的 Docker 管理面板（容器 / 镜像 / 卷 / 网络 / Compose、
  实时统计、合并日志流、容器内文件编辑与 TTY 控制台）。经 SSH 直连远端 `/var/run/docker.sock`
  （`direct-streamlocal@openssh.com`），**服务器上什么都不用改**，也不必把 daemon 暴露在 2375 / 2376。

### 六个仓库各管一摊

| 仓库 | 管什么 | 怎么交付到本仓库 |
| --- | --- | --- |
| **joesdu/VelaShell**（本仓库） | 主程序、宿主侧插件运行时、SSH 库、X 服务端库、AI 插件 | — |
| [**velashell-plugin-sdk**](https://github.com/VelaShellLabs/velashell-plugin-sdk) | 插件契约 SDK | `VelaShell.PluginSdk` / `.Testing` **NuGet 包** |
| [**velashell-plugin-cli**](https://github.com/VelaShellLabs/velashell-plugin-cli) | `vela-plugin` 命令行、`VelaShell.PluginSdk.Build` | NuGet 包（插件作者用，本仓库不引用） |
| [**velashell-plugin-templates**](https://github.com/VelaShellLabs/velashell-plugin-templates) | `dotnet new velaplugin` 模板 | NuGet 包（插件作者用，本仓库不引用） |
| [**velashell-plugins**](https://github.com/VelaShellLabs/velashell-plugins) | Redis / S3 / Telnet / 串口 / Docker 面板插件 | 各插件的 **`.vpx` 包**（Release 资产 / 插件商店） |
| [**velashell-docs**](https://github.com/VelaShellLabs/velashell-docs) | 上面所有仓库的**全部文档** | — |

SDK 契约一律走 NuGet 包、不做工程引用，版本 pin 在 `src/Directory.Packages.props` 与 `tests/Directory.Packages.props`。
**AI 插件是例外**，留在本仓库：它与宿主的耦合点都是编译期的（借宿主的 AvaloniaEdit、必须进程内装载、
Avalonia 版本必须与宿主逐字一致），分仓的话改一行 UI 都要先发一次 Release 才能联调（详见 [`plugins/README.md`](plugins/README.md)）。

---

## 🧪 测试

```bash
dotnet test VelaShell.slnx                          # 全部
dotnet test tests/VelaShell.Terminal.Tests/         # 单个项目
dotnet run -c Release --project tests/VelaShell.Benchmarks -- --filter *VtParser*   # 基准
```

| 测试项目 | 覆盖 |
| --- | --- |
| `VelaShell.Core.Tests` | 领域模型、传输队列、隧道、目录同步、云同步加密 |
| `VelaShell.Terminal.Tests` · `.RenderTests` | VT 解析与仿真；字形绘制的**像素级**回归（Skia 软件后端真实光栅化） |
| `VelaShell.Presentation.Tests` | ViewModel 工作流与命令 |
| `VelaShell.Infrastructure.Tests` | 持久化、凭据加密、ConPTY、SSH 接线、密钥管理、插件管理与跨进程 RPC |
| `VelaShell.Controls.Tests` | 自定义控件、主题令牌与样式守门 |
| `VelaShell.Tests` | 窗口级视图模型、身份验证流程、插件面板、窗口外框、headless 视图 |
| `VelaShell.Plugin.Ai.Tests` | AI 插件：审批闸门、能力桥接、协作接入、面板 headless 交互 |
| `VelaShell.ShellIntegration.Tests` | 「文件浏览器跟随终端目录」的端到端验证：真 sshd、真登录 shell、真 PTY |
| `VelaShell.Ssh.Tests` · `VelaShell.XServer.Tests` | 两个 MIT 库的单测，以及对真实 OpenSSH / 真实 X 客户端的互操作 |
| `VelaShell.Benchmarks` | BenchmarkDotNet 基准，**不进 CI 门禁**（结果受机器负载影响太大），用于同一台机器上改动前后的对比 |

> ⚠️ **早退跳过在 MSTest 里记为「通过」**。`DockerIntegration` 需要 Docker 与 `docker-compose.test.yml`，
> `CrossPlatformPublishTests` 需 `VELASHELL_PUBLISH_TESTS=1`，X 服务端的真实客户端用例需 `VELASHELL_XSERVER_INTEROP=1`。
> 前提不满足时它们安静地全绿而一行都没跑 —— 要确认真跑过，看 `TestContext` 里有没有 `[SKIP]` 行。
>
> ⚠️ **headless UI 测试要用带返回值的重载**：`Dispatch(async () => { …; return true; })`。
> 写成无返回值会拿到一个从未被等待的 `Task<Task>`，测试跑到第一个 `await` 就「通过」，断言失败全部丢失。

---

## 📚 文档

全部文档集中在 **[VelaShellLabs/velashell-docs](https://github.com/VelaShellLabs/velashell-docs)**，
中文在 [`zh/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh)，English in [`en/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en)，两棵树互为镜像。

| 分区 | 内容 |
| --- | --- |
| [`zh/host/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host) | **本仓库**：[分层架构](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/architecture.md)、[交互与界面规格](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/交互与界面规格.md)、[快捷键参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/快捷键参考.md)、[设置项审计](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/settings-audit.md)、[Xshell 兼容登录](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/Xshell兼容登录.md)，以及各类设计与可行性调研 |
| [`zh/ssh/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/ssh) · `zh/xserver/` | SSH 库与 X 服务端库的架构、行为规格与上手 |
| [`zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins) | 插件系统设计蓝图 + [进度总览 STATUS](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md) |
| [`zh/sdk/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/sdk) · [`zh/cli/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/cli) · [`zh/templates/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/templates) | SDK 参考、`vela-plugin` 手册、插件开发指南与打包发布 |

留在本仓库的几份，服务的是「在这个仓库里写代码」这件事：

- [`AGENTS.md`](AGENTS.md) —— 给 AI 代理与新加入者的操作约定（**动手前先读**）
- [`DESIGN.md`](DESIGN.md) —— 设计系统：色彩 / 字体 / 间距令牌与组件规范
- [`plan.md`](plan.md) —— **已经发生的事**：当前架构、进展记录、每次改动的来龙去脉
- [`feature-plan.md`](feature-plan.md) —— **还没发生的事**：待办、路线图、确认不做的清单（附理由）
- [`CONTRIBUTING.md`](CONTRIBUTING.md) · [`SECURITY.md`](SECURITY.md) · [`PRIVACY.md`](PRIVACY.md)

---

## 🛠️ 技术栈

| | |
| --- | --- |
| **运行时 / UI** | .NET 11（启用预览特性与 `runtime-async`）· Avalonia 12.1 · ReactiveUI |
| **本仓库的核心组件** | VT 终端引擎 · VelaDock 停靠分屏 · VelaShell.Ssh（SSH / SFTP / 端口转发 / SOCKS5 服务端）· VelaShell.XServer（X11 服务端）· 插件运行时 · 便携式自更新 |
| **第三方** | FluentFTP（FTP / FTPS）· SonnetDB（嵌入式文档 + 时序数据库）· BouncyCastle（Ed25519 等 BCL 缺的曲线运算）· AvaloniaEdit（编辑器与 AI 输入框）· MaxMind.Db（离线 IP 归属地） |
| **AI 插件** | Microsoft.Extensions.AI · ModelContextProtocol · LiveMarkdown.Avalonia（含 Mermaid / LaTeX / SVG） |
| **工程** | MSTest · BenchmarkDotNet · 集中式包管理 · SourceLink |

---

## 🚧 开发状态

项目处于**活跃开发**阶段，持续发版，最新版本见 [Releases](https://github.com/joesdu/VelaShell/releases)。

上面「功能一览」里列出的都已可用。少数设置项目前仍只是持久化、没有接到运行时，已经从界面撤下或在
[`feature-plan.md`](feature-plan.md) 的 P0 表里逐条登记；系统密钥链与外部密码管理器（1Password / Bitwarden / KeePassXC）集成还在设计阶段。
完整的完成记录见 [`plan.md`](plan.md)，待办与路线图见 [`feature-plan.md`](feature-plan.md)，
插件侧见[进度总览](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md)。

---

## 🤝 贡献

欢迎提交 Issue 与 Pull Request。**动手前请先读 [`CONTRIBUTING.md`](CONTRIBUTING.md)**（AI 代理另见 [`AGENTS.md`](AGENTS.md)）。
几条会当场把你拦下来的硬约束：

- **界面文案五份 resx 必须齐**（`Strings` / `zh-Hans` / `zh-Hant` / `ja` / `ko`），不许硬编码字符串。
- **新增或改动快捷键先改 `ShortcutCatalog.cs`**，它是唯一事实来源。
- **XAML 与 C# 里不许出现颜色字面量**，一律 `DynamicResource` 绑定令牌。
- **插件 SDK 一律走 NuGet 包**；不要自己决定 SDK 版本号，也不要造本地包。
- **改 `src/VelaShell.Ssh/` 或 `src/VelaShell.XServer/` 之前先读各自的 `AGENTS.md`** —— 两个库都守净室规程。
- **改了行为就同步改 velashell-docs**，两个 PR 互相引用、一起合。

发现安全漏洞请**不要**开公开 Issue，按 [`SECURITY.md`](SECURITY.md) 的流程私下报告。

---

## 💖 赞助

VelaShell 对个人与企业**永久免费**，赞助纯属自愿 —— 不解锁任何功能，也不存在「赞助版」与「免费版」的区别。

| 渠道 | 适用 | 链接 |
| --- | --- | --- |
| 爱发电 | 国内，支持支付宝 / 微信 | <https://afdian.com/a/velashell> |
| Wise | 海外，支持信用卡 / 银行转账 | <https://wise.com/pay/me/yud162> |

也可以直接扫码：

<div align="center">
  <img src="src/VelaShell/Assets/donate-alipay.png" width="200" alt="支付宝" />
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="src/VelaShell/Assets/donate-wechat.png" width="200" alt="微信支付" />
</div>

应用内 **设置 → 支持与捐赠** 有同样的入口。感谢每一位赞助者。

---

## 📄 许可证

本项目采用**双许可（Dual License）**模式：

- **[AGPL-3.0](LICENSE)（默认）** —— 自由使用、修改与分发，但衍生作品（含通过网络提供服务）
  **必须以相同许可证开放全部源代码**，并保留版权与捐赠信息。
  移除本项目信息后闭源售卖属于侵权行为，版权方将依法追究（DMCA 下架 / 诉讼）。
- **[商业授权](LICENSE-COMMERCIAL.md)（付费，按需）** —— 需要闭源集成、闭源分发，
  或企业合规无法接受 AGPL 时，可联系作者购买商业许可
  （📧 <dygood@outlook.com>，标题注明「Commercial License」）。

`src/VelaShell.Ssh/` 与 `src/VelaShell.XServer/` 两个库例外，按各自目录下的 **MIT** 许可证授权。

**正版声明**：VelaShell 本体对所有个人与企业**永久免费**，唯一官方发布渠道为本仓库的
GitHub Releases（以及 Microsoft Store 上的同名商店版）；任何渠道的「收费版 VelaShell」均为盗版。
「VelaShell」名称与 Logo 不在开源许可授权范围内，衍生版本不得使用本项目名称与标识宣传或售卖。

向本项目提交贡献即表示同意贡献以 AGPL-3.0 授权，并授予版权方在商业许可下再许可该贡献的权利
（详见 [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) §3）。

---

## 🪶 关于「VelaShell」这个名字

**读音**：`/ˈveɪlə ʃɛl/` —— 读作 **「VAY-la shell」**，中文近似「薇拉·谢尔」，第一个音节 *Vay* 重读。

- **Vela（船帆座）** —— 拉丁语意为「帆」。船帆座是南天的一个星座，与龙骨座（Carina）、
  船尾座（Puppis）同源，共同拆分自古希腊神话中「阿尔戈号」（Argo Navis）——
  伊阿宋与阿尔戈英雄们远航寻找金羊毛所乘的巨船。取其**扬帆远航、驶向未知彼岸**之意。
- **Shell（终端外壳）** —— 命令行 shell，也是本软件的核心：一个连接远程主机的终端。

合起来，VelaShell 寓意 **「以终端为帆，乘信号之风驶向远方主机」**。
图标即这一理念的浓缩：青绿渐变的圆角方块上，一枚深色 `>_` 命令提示符。

---

<div align="center">

**VelaShell** —— 为命令行而生。

© 2026 VelaShell 作者及贡献者

</div>
