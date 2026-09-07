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

</div>

---

VelaShell 是用 **.NET 11 + Avalonia** 写的桌面终端应用，Windows / Linux / macOS 三平台原生发布、
自包含（目标机器不装 .NET Runtime 也能跑）。

它把远程运维一天里要用到的东西装进了同一个窗口：**自研 VT 终端引擎**、SSH / SFTP / FTP 连接、
本机终端标签、跳板机与网络代理、端口转发隧道、SFTP 双栏传输与远程编辑、资源监视与路由追踪、
会话录制回放、命令面板与十二页设置中心。持久化统一走嵌入式 SonnetDB，凭据 AES-256-GCM 落盘。
再往外，是一套**双模插件系统**（进程内 / 独立进程）与第一方 **AI 助手插件**。

三条贯穿始终的取舍：**键盘优先**、**信息密度高**、**响应迅速**。

---

## ✨ 功能一览

| | |
| --- | --- |
| **终端** | 自研 VT 引擎（DEC ANSI / VT / Xterm 状态机）· 十种终端 profile（vt52 → xterm-256color）· 256 色 / 真彩色 / 线绘字符 / 主备屏 / 鼠标协议 / CJK 双宽 · 自绘渲染 · 行 / 块 / 多段不连续选区 · 行号与时间侧栏 |
| **连接** | SSH · SFTP · FTP / FTPS · 本地终端（Windows ConPTY）· 跳板机 ProxyJump（≤5 跳、环检测）· HTTP / SOCKS5 / 跟随系统代理 · 两步身份验证 · 主机指纹 TOFU · 每条连接各配「认证后执行命令」· 断线自动重连（含睡眠唤醒 / 网络恢复） |
| **文件** | SFTP 双栏浏览与拖拽互传 · 断点续传与传输队列 · 远程文件内置编辑器（AvaloniaEdit 语法高亮）或交给外部编辑器并监听落盘回传 · ZMODEM / XMODEM / YMODEM 收发（全部自研） |
| **隧道** | 本地 `-L` / 远程 `-R` / 动态 SOCKS5 `-D` · 自研计量数据面（实时连接数与字节数）· 断线自动恢复 · 端口冲突预检 |
| **运维** | 资源监视器（CPU / 内存 / 磁盘 / 网络 / 进程）· 进程管理器 · 路由追踪（带地理信息）· 连接诊断 · 会话录制与回放（可导出 asciicast v2） |
| **工作区** | 自研 VelaDock 拖拽分屏 · 分组会话管理 · 从 WinSCP / Xshell 导入 · 命令面板（`Ctrl+P` / `Ctrl+K`）· 快捷命令片段 · **多终端同步输入** · 命令智能补全（历史 + 片段，程序提问时自动闭嘴）· 消息中心与安全资讯源 |
| **数据** | 嵌入式 SonnetDB（文档 + 时序双模型）· AES-256-GCM 凭据加密 · GitHub Gist 云同步（可选口令端到端加密、版本可回溯）· 安全审计日志 · 会话日志 |
| **外观** | 12 套具名主题（7 暗 5 亮，含 Tokyo Night / Nord / Gruvbox / One Dark / Sakura…）· 16 套内置终端配色，与界面主题成对联动 · 全令牌化零硬编码颜色 · 内置 Cascadia Mono 四款字形 |
| **本地化** | 简体中文 / English / 繁體中文 / 日本語 / 한국어 —— 五份 resx 键集完全一致，漏译与孤儿键都会让测试变红 |
| **扩展** | 双模插件宿主（可收集 ALC / 独立进程 + 命名管道 RPC）· `.vpx` 打包与插件管理器 · 危险能力逐项授权 · 第一方 AI 助手插件 |
| **桌面集成** | 单实例 · 托盘 · 开机自启 · 硬件加速开关 · Xshell 兼容登录（可被堡垒机 / SSO 门户外部拉起）· URL 协议注册 |

<details>
<summary><b>展开：几处值得多说两句的实现</b></summary>

- **自研 VT 终端引擎** —— 终端是自绘的 Avalonia 控件，字形、选区与滚动全部自己渲染，
  不依赖任何已停维护的第三方终端控件。选区支持行 / 块两种模式、`Shift+左键` 扩展，
  以及 `Ctrl+Shift+拖拽` 追加的多段不连续选区（可一次复制第 1 行 + 第 3 行）。

- **SSH 栈** —— 基于 [Tmds.Ssh](https://github.com/tmds/Tmds.Ssh)（全托管、async-first）。
  跳板机由其原生 `SshProxy` 逐跳建链，指纹按各跳逻辑主机分别校验。
  首次连接默认 TOFU 记录指纹，可切换为人工确认；指纹变化立即拒绝连接。

- **隧道的数据面是自研的** —— Tmds.Ssh 把转发的搬运做在库内部、不暴露任何计数，
  于是本地转发改为自建 `TcpListener` + `direct-tcpip`，动态转发自研 SOCKS5 服务端握手
  （RFC 1928），远程转发经本机计量监听接力。搬运保留半关闭语义，否则
  「发完请求就 shutdown 再等响应」的协议全部读不到东西。

- **三种 X / Y / ZMODEM 都是自研引擎**，收发双向、传输无关（SSH 与本地 ConPTY 通用）。
  ZMODEM 从输出流识别引导序列后**自动接管**，结束自动复位回终端；
  XMODEM / YMODEM 在链路上没有可识别的引导序列，只能从命令面板手动发起
  （先在远端敲 `sb` / `rb`，再点对应命令）。
  排障置 `VELASHELL_TRANSFER_TRACE=1` 打开协议帧跟踪。

- **FTP / FTPS** —— 基于 [FluentFTP](https://github.com/robinrodricks/FluentFTP)，
  自带连接池以支持并发传输（FTP 一条控制连接同时只能跑一条命令），
  复用与 SFTP 完全相同的双栏面板与传输栈；服务器证书未通过校验时给出 SHA-256 指纹交由用户确认。

- **主题是种子色 + 派生** —— 一套主题只需人工填 25 个种子色（`UiThemePalette`），
  其余六十多个令牌由 `ThemeTokenApplier` 按固定规则派生。手抄会错，而且错了看不出来。

- **消息中心只收「要留存、可回看」的东西** —— 新版本、订阅源的公告与安全资讯。
  运行时告警（指纹变了、会话断了）当场弹窗或亮状态栏，不混进列表把真正要读的淹掉。
  资讯源默认订阅官方源，清空地址即彻底不发任何请求（如实说明见 [`PRIVACY.md`](PRIVACY.md)）。

- **快捷键只有一个事实来源** —— `src/VelaShell/ViewModels/ShortcutCatalog.cs`，
  11 个分组近百条，设置页与文档都从它取数；没登记的话 `ShortcutCatalogTests` 会失败，
  并打印出可直接粘贴的 Markdown 行。

</details>

### 🤖 AI 助手插件（第一方，随包发布）

<details open>
<summary>点开看它到底做了什么</summary>

- **多提供商流式对话** —— OpenAI Responses / OpenAI Chat Completions 兼容 / Anthropic Messages
  三种线协议，覆盖 OpenAI、Anthropic、Grok、Gemini、DeepSeek、Kimi、GLM 与各类中转站。
- **点一下就连上** —— 接入走内置供应商目录：支持登录的直接开浏览器，授权完自己跳回来，
  凭据加密存好、模型一并配好，全程零输入；其余的只问一把 API Key，
  名称 / 地址 / 模型 / 协议都有出厂值并收进「高级设置」。
  **授权码 + PKCE**（环回端口接回调）与**设备码**（浏览器不在本机时）两套标准流程都实现了，
  令牌临近过期自动续期。已经在付 **ChatGPT Plus / Claude Pro / GitHub Copilot** 的用户
  可以直接用手里的订阅额度 —— 这几条借的是各家官方 CLI 公开的客户端身份，界面上标了「实验性」。
- **模型清单** —— 先问端点自己的 `/models`（只有它知道这个地址实际供应什么），
  再按 id 从开源的 models.dev 补上下文窗口与单价。
- **Agent 模式** —— 基于 `Microsoft.Extensions.AI` 的 `FunctionInvokingChatClient` 工具循环，
  工具桥接到 sessions / terminal / remoteExec / remoteFs，危险操作面板内逐条审批；
  可挂接自定义 **MCP 服务器**（stdio / HTTP）扩展工具集；另带网页检索与抓取工具
  （默认走公共 SearXNG 实例，可换成自建）。
- **协作接入** —— 把 agent 接到 **飞书 / 钉钉 / Telegram / 企业微信**：
  配对码授权群聊（不用再回电脑）、一键放行敲过门的会话、`[测试]` 按钮填完当场验连通性；
  无头回合另起一条，不动桌面上的聊天面板。
- **对外 MCP 服务端** —— Streamable HTTP，只绑 `127.0.0.1` 且每个请求必须带令牌，
  把 VelaShell 的会话能力开放给外部 agent。这条路上没有审批界面，
  所以「询问」模式等于一律拒绝写操作 —— 要放行得显式选，而不是一个悄悄的默认。
- **交互细节** —— Agent 跑动过程中可**插话**（新消息进队列即时排上，不必等本回合结束）；
  输入框是带 Markdown 着色的编辑器，`@` 唤出所选会话的远端文件选择器并把内容随消息附上；
  对话落插件私有时序库，可翻回、可续聊、可删除。

</details>

---

## 🖥️ 平台与分发

| 平台 | 架构 | 分发形式 |
| --- | --- | --- |
| **Windows** 10 / 11 | x64 · arm64 | 便携 zip（应用内自动更新）· Microsoft Store MSIX |
| **Linux** | x64 · arm64 | 便携 tar.gz · `.AppImage` 单文件免安装 · `.deb` / `.rpm` 原生包 |
| **macOS** | x64 · arm64 | tar.gz · `.dmg` 拖装包（未签名 / 未公证） |

全部为 **Self-contained** 发布，解压到任意目录即可运行。
**自动更新只走 tar.gz / zip**；dmg、AppImage、deb、rpm 装完后应用目录不可写，
关于页会自动降级成「请手动下载」。Store 版（MSIX）的更新由商店接管，
且装在只读的 `WindowsApps` 下、数据目录被系统重定向到包私有位置，
**与便携版的配置、会话、密钥互不相通**。

---

## 🚀 快速开始

### 环境要求

- [.NET SDK **11.0**](https://dotnet.microsoft.com/download) 或更高 —— `global.json` 锁到
  `11.0.100-preview.7`，`rollForward: latestFeature`
- （可选）Docker，用于起本地 SSH 测试服务器

> ⚠️ 仓库开启了 `EnablePreviewFeatures` 与 `runtime-async=on`（见 `Directory.Build.props`），
> 因此**构建依赖 .NET 11 预览版 SDK**。需要 LTS 基线的话，把 `<TargetFramework>` 与
> `global.json` 一并回退到 net10 即可。

### 构建与运行

```bash
git clone https://github.com/joesdu/VelaShell
cd VelaShell

dotnet build VelaShell.slnx                 # 整个解决方案（含插件宿主与 AI 插件）
dotnet test  VelaShell.slnx                 # 全量测试

dotnet watch run --project src/VelaShell/VelaShell.csproj   # 开发模式（热重载）
```

> **应用正在运行时构建会因文件占用失败**，先关掉应用。

<details>
<summary>本机想连 Redis / S3 / Telnet 插件一起跑？</summary>

干净克隆构建出来只带本仓库自建的 **AI 插件**。把其余插件目录铺进暂存目录
`artifacts/plugins/`（或用 `-p:VelaPluginsStageDir=<目录>` 指别处），构建时会自动镜像到
应用输出目录的 `plugins/<插件目录名>/`，F5 即可加载。插件目录可以从
[velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins) 的 Release 资产
—— 各插件的 **`.vpx` 包** —— 解出来（`.vpx` 是本项目自有的插件包格式，容器内就是
安装后 `plugins/<插件>/` 那一层），也可以直接指向那边的构建产物。

自 2026-08-22 起**发布流水线不再预装** Redis / S3 / Telnet —— 不是每个人都要连 Redis、
开 S3、拨 Telnet，改由用户按需从[插件商店](https://market.easilynet.top)安装。
暂存目录纯属本机行为：铺过之后你本机 `dotnet publish` 出来的包会把它们一并打进去，
只影响你自己的产物。

> ⚠️ 别把 `velashell-ai` 也铺进来 —— 本仓库自己就产出它，同一个 id 出现两份会被
> `PluginManager` 判重，后来者标 Invalid，表象是「插件莫名其妙用不了」。

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
| 会话日志（开启后） | `~/.velashell/logs` |
| 用户手动安装的插件（`.vpx`） | `~/.velashell/plugins`（第一方插件仍在程序目录的 `plugins/`） |
| 宿主自登记（供 `vela-plugin` 定位安装与核对版本） | `~/.velashell/host.json` |
| 插件开发期挂载与影子副本 | `~/.velashell/plugins.dev.txt`、`~/.velashell/dev-shadow/` |
| SSH 密钥对（密钥管理页） | `~/.ssh` |

> 旧版本的 `sessions.json` / `settings.json` 等 JSON 配置会在首次运行时自动导入 SonnetDB
> 并改名为 `*.migrated.bak`。从更早的数据根升级时，`%LocalAppData%/VelaShell` 的全部内容
> 会被校验迁移到 `~/.velashell`，成功后删除旧目录；冲突的旧目标文件保存在
> `.migration-backup/localappdata/`。

---

## 📦 构建与发布

```bash
pwsh scripts/publish-all.ps1     # 一键产出全平台发布包 → publish/
```

产物覆盖三平台 x64 / arm64，全部是含运行时的自包含发布。包内除主程序外还带着隔离插件的
宿主进程 `VelaShell.PluginHost`，以及只放着自建 AI 插件的 `plugins/` 目录。
`.dmg` 只在 CI 的 macOS runner 上生成（`hdiutil` / `iconutil` / `codesign` 是 macOS 独有工具）；
Linux 的 `.AppImage`（见 [`build/appimage/`](build/appimage/README.md)）与
`.deb` / `.rpm`（装进 `/opt/velashell`，登记菜单项与 `/usr/bin/velashell`，
见 [`build/linux-packages/`](build/linux-packages/README.md)）两种架构各一份，
全部在同一台 x64 runner 上交叉打出。

> `dotnet publish` 仅在自建插件与暂存目录**两边都空**时才失败 ——
> 发行包不接受「插件系统看着在、实则没插件」。

**应用内自动更新**（设置 → 关于 → 检查更新）：从 GitHub Releases 读 `latest.json` 清单，
下载对应平台压缩包到应用目录下的暂存目录，SHA-256 校验后解包，
再由**应用退出后才动手的外置换版进程**完成换版并重启 —— 那时应用目录里没有任何文件被占用，
不会留下删不掉的残骸。那个「外置进程」就是暂存目录里解包出来的那份新版应用
（Release 为自包含**摊开**发布，解开即可运行），因此无需随包分发额外的更新器。
应用装在哪里就更新哪里，不限定安装位置；`~/.velashell` 与更新流程完全隔离，
升级 / 回滚均不触碰用户数据。换版中途失败会自动还原到旧版本，
若流程被意外中断卡住，关于页的「修复更新状态」可一键重置。更新通道（stable / preview）在设置页切换。

### CI / CD

| 流水线 | 触发 | 做什么 |
| --- | --- | --- |
| [`ci.yml`](.github/workflows/ci.yml) | push `main` / 全部 PR | windows + ubuntu + macos 三平台矩阵，Debug 构建 + 全量测试，`-warnaserror` |
| [`release.yml`](.github/workflows/release.yml) | 发布 Release | 三平台原生 runner 并行构建打包，汇总 `SHA256SUMS.txt` 与自动更新清单 `latest.json` 附加到该 Release；同一条流水线另打 **MSIX** 供 Microsoft Store 提交（刻意不自签，商店认证通过后由微软重签） |

CI 有几处刻意的选择：用 **Debug**（强名签名只在 Release 打开，fork 与 Dependabot 的 PR
拿不到仓库 secret）；**排除** `DockerIntegration` / `CrossPlatform` 分类；
**把本仓检出到 `VelaShell/` 子目录**并把 `velashell-docs` 并排检出 ——
快捷键总表与《快捷键参考》的比对用例才找得到对方。
版本号在发版时由 Release 标签经 `-p:Version` 覆盖，**发版无需改代码**。

> 早期的 WiX MSI 与 Velopack 安装包已于 `241c2a2` 移除：装进 Program Files 会让应用目录不可写，
> 应用内更新只能退化成「提示手动下载」，与便携发布的自更新模型冲突。

---

## 🏗️ 代码结构

```text
VelaShell/
├── src/
│   ├── VelaShell/                  # 桌面应用入口、DI 组合根、XAML 视图、VelaDock 停靠与全局样式
│   ├── VelaShell.Terminal/         # 自研 VT 终端引擎与 Avalonia 渲染控件
│   ├── VelaShell.Presentation/     # 跨层 ViewModel、工作流与 Presentation DI 模块
│   ├── VelaShell.Controls/         # 复用控件库、主题令牌与内置字体
│   ├── VelaShell.Core/             # 领域模型、服务契约、持久化抽象、协议引擎与本地化（无 UI 依赖）
│   ├── VelaShell.Infrastructure/   # SSH/SFTP/FTP/隧道实现、SonnetDB 持久化、AES-256 凭据加密、
│   │                               # Gist 同步、插件管理与能力实现
│   └── VelaShell.PluginHost/       # 隔离插件的宿主进程（命名管道 RPC，只依赖 SDK 契约）
├── plugins/VelaShell.Plugin.Ai/    # 第一方 AI 助手插件（与主程序同仓构建、同版发布）
├── tests/                          # 8 个 MSTest 项目 + 1 个 BenchmarkDotNet 项目
│   └── fixtures/                   # 插件运行时用例的夹具插件（非示例代码，见其 README）
├── build/                          # AppImage / deb / rpm / MSIX / 社交预览图的打包脚本
├── scripts/publish-all.ps1         # 跨平台一键发布脚本
├── docker-compose.test.yml         # 本地 SSH 测试服务器
├── global.json                     # SDK 版本锁定
├── Directory.Build.props           # 全仓版本与公共 MSBuild 属性
├── src/Directory.Packages.props    # 集中式 NuGet 版本管理
└── VelaShell.slnx                  # 解决方案
```

> 本仓库**不放** `docs/` —— 全部文档在 [velashell-docs](https://github.com/VelaShellLabs/velashell-docs)。
> 每个源项目与测试项目自带 `README.md`，说明该项目的架构、目录职责与依赖关系。
> 入口项目实际名为 `VelaShell`（历史文档中的 `VelaShell.App` 为旧别名）。

### 架构约定

- **严格分层** —— 依赖方向为 `App(VelaShell) → Presentation / Controls / Infrastructure → Core`，
  Core 层不依赖任何 UI 框架，可独立测试与复用；服务均通过接口注入，便于 Mock。
- **单一组合根** —— 所有 DI 注册集中在 [`src/VelaShell/App.axaml.cs`](src/VelaShell/App.axaml.cs)，
  各层通过 `*ServiceCollectionExtensions` 贡献注册。
- **自绘渲染** —— 终端通过自定义 Avalonia Control 直接渲染字形、选区与滚动。
- **自研停靠** —— VelaDock 的模型层（纯 INPC，可单测）与控件层分离，
  拖拽 / 分屏 / 标签重排全套自研，零第三方停靠依赖。
- **单引擎持久化** —— 一个嵌入式 SonnetDB 实例承载文档（配置 / 业务数据）与时序
  （连接历史 / 审计 / 录制 / 插件数据）两类模型，接口在 Core、实现在 Infrastructure，退出时统一刷盘。
- **设计令牌化** —— **XAML 与 C# 里不许出现颜色字面量**，一律 `DynamicResource`
  绑定令牌（规范见 [`DESIGN.md`](DESIGN.md)）。
- **安全默认值** —— 凭据静态加密（AES-256-GCM + 本地密钥文件）、主机指纹 TOFU 校验、
  「记住密码」可按连接关闭、插件危险能力逐项授权。

---

## 🧩 插件系统

**双模宿主** —— 插件既可**进程内**装载（可收集 `AssemblyLoadContext` 隔离，
UI 直接并入停靠工作区），也可跑在**独立进程** `VelaShell.PluginHost` 里
（自研命名管道 RPC，崩溃不波及主程序，带心跳、自愈重启与空闲回收）。
装载方式由插件清单声明，两种模式共用同一套 SDK 契约。依赖按插件自己的 `deps.json` 解析，
只有 SDK 契约与 `Avalonia*` 框架程序集回落到宿主，保证跨边界类型同一。

**能力面（Capability APIs）** —— 插件经 `IPluginContext` 访问宿主能力：

| 能力 | 做什么 |
| --- | --- |
| `Sessions` | 会话枚举 / 状态 / 按已保存配置开关会话 |
| `Terminal` | 读终端输出、写终端输入 |
| `RemoteExec` · `RemoteFs` | 远端命令执行、远端文件读写与目录列举 |
| `RemoteTunnel` | 复用宿主既有连接开 `direct-tcpip` / `direct-streamlocal` 通道，把裸流交给插件 |
| `Protocols` · `Workspaces` | **注册新的连接类型与工作台** —— Telnet / 串口 / Redis / S3 就是这么接进会话树的 |
| `Storage` · `TimeSeries` | 插件私有的文档与时序存储 |
| `Secrets` | 经宿主加密的机密 |
| `Commands` · `Events` · `Ui` · `Clipboard` · `Log` | 注册命令与快捷入口、订阅会话 / 语言 / 主题事件、开面板（停靠文档或独立窗口） |

危险能力经权限对话框逐项授权；协议与工作台的 id 强制插件前缀 —— 冒名等于劫持别家的连接配置。

**打包与管理** —— 插件以 `.vpx` 包分发，独立的插件管理窗口可安装 / 启停 / 卸载；
卸载时其私有数据（SonnetDB 命名空间与数据目录）一并清理。
SDK 另提供测试替身（`VelaShell.PluginSdk.Testing`），插件可在 headless 下自测。
第三方开发者一条命令即可断点调试（`vela-plugin dev init` → F5）。

📖 [开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)
· [命令行手册](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/cli/cli.md)
· [SDK 参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/sdk/sdk-reference.md)
· [打包发布](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/publishing.md)
· [设计蓝图 15 篇](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins)

### 六个仓库各管一摊

| 仓库 | 管什么 | 怎么交付到本仓库 |
| --- | --- | --- |
| **joesdu/VelaShell**（本仓库） | 主程序 + 宿主侧插件运行时 + 自建的 AI 插件 | — |
| [**velashell-plugin-sdk**](https://github.com/VelaShellLabs/velashell-plugin-sdk) | 插件契约 SDK | `VelaShell.PluginSdk` / `.Testing` **NuGet 包** |
| [**velashell-plugin-cli**](https://github.com/VelaShellLabs/velashell-plugin-cli) | `vela-plugin` 命令行、`VelaShell.PluginSdk.Build` | **NuGet 包**（插件作者用，本仓库不引用） |
| [**velashell-plugin-templates**](https://github.com/VelaShellLabs/velashell-plugin-templates) | `dotnet new velaplugin` 模板 | **NuGet 包**（插件作者用，本仓库不引用） |
| [**velashell-plugins**](https://github.com/VelaShellLabs/velashell-plugins) | Redis / S3 / Telnet / 串口插件 | 各插件的 **`.vpx` 包**（Release 资产 / 插件商店） |
| [**velashell-docs**](https://github.com/VelaShellLabs/velashell-docs) | 上面所有仓库的**全部文档** | — |

SDK 契约的版本 pin 在 `src/Directory.Packages.props` 与 `tests/Directory.Packages.props`
（都是具体版本号），**一律走 NuGet 包，不做工程引用**。
插件二进制本仓库不 pin —— 它们不进发行包，也就没有需要锁的版本。

**AI 插件是例外**，留在本仓库的 [`plugins/VelaShell.Plugin.Ai/`](plugins/VelaShell.Plugin.Ai)：
它与宿主耦合最紧，且那些耦合点都是**编译期**的（借宿主的 AvaloniaEdit 作输入框、
必须进程内装载、Avalonia 版本必须与宿主逐字一致），分在两个仓库时改一行 UI
都要先发一次 Release 才能联调。理由详见 [`plugins/README.md`](plugins/README.md)。

---

## 🧪 测试

```bash
dotnet test VelaShell.slnx                          # 全部
dotnet test tests/VelaShell.Terminal.Tests/         # 单个项目
dotnet test --logger "console;verbosity=detailed"   # 详细输出

dotnet run -c Release --project tests/VelaShell.Benchmarks -- --filter *VtParser*   # 基准
```

| 测试项目 | 覆盖 |
| --- | --- |
| `VelaShell.Core.Tests` | 领域模型、SFTP 与传输队列、隧道与计量转发、云同步加密、ZMODEM / XMODEM / YMODEM 协议（期望值按 lrzsz 与 ymodem.txt 手工构造的互操作回归） |
| `VelaShell.Terminal.Tests` | VT 解析、终端仿真、编码、字符宽度、侧栏折叠，以及 ZMODEM 自动接管与 X / YMODEM 手动接管的路由 |
| `VelaShell.Terminal.RenderTests` | 字形绘制的**像素级**回归（挂 Skia 软件后端做真实光栅化） |
| `VelaShell.Presentation.Tests` | ViewModel 工作流与命令 |
| `VelaShell.Infrastructure.Tests` | SonnetDB 持久化、凭据加密、ConPTY、SSH 密钥管理、插件管理与跨进程 RPC |
| `VelaShell.Controls.Tests` | 自定义控件行为、主题令牌与样式守门 |
| `VelaShell.Plugin.Ai.Tests` | AI 插件：工具箱审批闸门、能力桥接、设置与机密存取、会话历史、`@` 引用语法、协作接入与面板 headless 交互 |
| `VelaShell.Tests` | 窗口级视图模型、身份验证流程、插件面板与主题令牌、集成与冒烟测试 |
| `VelaShell.Benchmarks` | BenchmarkDotNet 吞吐与分配基准，**不进 CI 门禁**（BDN 结果受机器负载影响太大，当门禁只会天天误报），用于同一台机器上改动前后的对比 |

当前基线：排除 `DockerIntegration` / `CrossPlatform` 分类后 **3194 条通过**，构建零警告。

> ⚠️ **早退跳过在 MSTest 里记为「通过」**。`DockerIntegration` 分类需要 Docker 与
> `docker-compose.test.yml` 里的 SSH 服务器（ZMODEM 那几条还需容器内能装上 `lrzsz`），
> `CrossPlatformPublishTests` 需 `VELASHELL_PUBLISH_TESTS=1`。前提不满足时它们会安静地全绿
> 而一行都没跑 —— 看测试结果分辨不出来，要确认真跑过就看 `TestContext` 里有没有 `[SKIP]` 行。
> 判据本身也要够诚实：探 TCP 端口是不够的，Docker 的端口代理**永远**接受连接，
> 哪怕后端 sshd 根本握不上手，所以夹具改成缓存一次**真实 SSH 握手**再决定跳不跳。
>
> ⚠️ **headless UI 测试要用带返回值的重载**：`Dispatch(async () => { …; return true; })`。
> `HeadlessUnitTestSession` 没有 `Func<Task>` 重载，写成无返回值会拿到一个从未被等待的
> `Task<Task>`，测试体跑到第一个 `await` 就「通过」，断言失败全部丢失。

---

## 📚 文档

全部文档集中在 **[VelaShellLabs/velashell-docs](https://github.com/VelaShellLabs/velashell-docs)** ——
本仓库、SDK、CLI、模板四个仓库的文档都在那里，互相之间用相对链接。
中文在 [`zh/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh)，
English in [`en/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en)，两棵树互为镜像。

| 分区 | 内容 |
| --- | --- |
| [`zh/host/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host) | **本仓库的文档**：[分层架构](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/architecture.md)、[工程化重构蓝图](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/架构设计.md)、[交互与界面规格](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/交互与界面规格.md)、[快捷键参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/快捷键参考.md)、[设置项审计](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/settings-audit.md)，以及隧道、消息中心与资讯源、[Xshell 兼容登录](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/Xshell兼容登录.md)、[路由追踪](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/路由追踪设计.md)、[FTP](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/FTP客户端可行性调研.md) / Telnet / 串口 / Redis / S3 / 系统密钥链等设计与可行性调研 |
| [`zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins) | 插件系统设计蓝图 15 篇 + [进度总览 STATUS](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md) |
| [`zh/sdk/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/sdk) · [`zh/cli/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/cli) · [`zh/templates/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/templates) | SDK 参考、`vela-plugin` 手册、插件开发指南与打包发布 |

留在本仓库的两份，因为它们服务的是「在这个仓库里写代码」这件事：

- [`DESIGN.md`](DESIGN.md) —— 设计系统：色彩 / 字体 / 间距令牌与组件规范
  （XAML 注释与单元测试按章节号直接引用它）
- [`plan.md`](plan.md) —— 进展记录、已知问题与后续待办，开发跟进以它为准

另有 [`AGENTS.md`](AGENTS.md)（给 AI 代理与新加入者的操作约定）、
[`CONTRIBUTING.md`](CONTRIBUTING.md)、[`SECURITY.md`](SECURITY.md)、[`PRIVACY.md`](PRIVACY.md)。

---

## 🛠️ 技术栈

| | |
| --- | --- |
| **运行时 / UI** | .NET 11（`net11.0`，启用预览特性与 `runtime-async`）· Avalonia 12.1 · ReactiveUI |
| **自研** | VT 终端引擎 · VelaDock 停靠分屏 · ZMODEM / XMODEM / YMODEM · 计量端口转发与 SOCKS5 服务端 · 插件运行时（可收集 ALC + 独立宿主进程 + 命名管道 RPC + `.vpx` 打包）· 便携式自更新 |
| **网络** | Tmds.Ssh（SSH / SFTP / 端口转发 / ProxyJump，全托管 async-first）· FluentFTP（FTP / FTPS） |
| **存储** | SonnetDB —— 嵌入式多模型数据库（文档 + 时序），唯一持久化引擎 |
| **编辑 / 渲染** | AvaloniaEdit（远程文件编辑器与 AI 输入框）· LiveMarkdown.Avalonia（增量 Markdown，含 Mermaid / LaTeX / SVG 扩展） |
| **AI** | Microsoft.Extensions.AI（统一模型抽象与 Agent 工具循环）· ModelContextProtocol（MCP 客户端与服务端） |
| **工程** | MSTest · BenchmarkDotNet · 集中式包管理（`Directory.Packages.props`）· SourceLink |

---

## 🚧 开发状态

项目处于**活跃开发**阶段，已在持续发版（最新 tag `1.5.2`）。
仓库内 `Directory.Build.props` 的 `0.0.1-dev` 是开发期占位，发版时由 Release 标签经
`-p:Version` 覆盖 —— 别把它当成产品版本号。

**已可用** —— 终端引擎、SSH / SFTP、FTP / FTPS、X / Y / ZMODEM、本地终端、跳板机、
会话管理与导入、身份验证、隧道（含计量、流量统计与断线自愈）、持久化、设置中心、云同步、
会话录制、资源监视 / 进程管理 / 路由追踪、消息中心与资讯源、同步输入与命令补全，
以及**插件系统框架层**（双宿主模式、完整能力面、UI 与协议扩展、心跳自愈与空闲回收、
插件私有存储与卸载清理、`.vpx` 装卸、SDK 测试替身与开发文档）与第一方 **AI 助手插件**。

**由插件提供** —— Telnet、串口（COM / USB 转串口）、Redis、S3。
均不随安装包预装，按需从[插件商店](https://market.easilynet.top)安装
（源码在 [velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins)）。

**未开放** —— 证书认证；容器管理插件尚未开始；
系统密钥链与 sudo 凭据自动填充仍在调研。部分设置项目前仅持久化、待接线到运行时。

完整完成情况与待办清单见 [`plan.md`](plan.md) 与
[插件进度总览](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md)。

---

## 🤝 贡献

欢迎提交 Issue 与 Pull Request。**动手前请先读 [`CONTRIBUTING.md`](CONTRIBUTING.md)** ——
里面写清了环境准备（SDK 为 preview 版、本地只能构建 Debug）、分支与提交约定、
测试的两条硬约束，以及多语言与文档的同步要求。AI 代理另见 [`AGENTS.md`](AGENTS.md)。

几条会当场把你拦下来的硬约束：

- **界面文案五份 resx 必须齐**（`Strings` / `zh-Hans` / `zh-Hant` / `ja` / `ko`），不许硬编码字符串。
- **新增或改动快捷键先改 `ShortcutCatalog.cs`**，它是唯一事实来源，设置页与文档都从它取数。
- **XAML 与 C# 里不许出现颜色字面量**，一律 `DynamicResource` 绑定令牌。
- **插件 SDK 一律走 NuGet 包**，不做工程引用；不要自己决定 SDK 版本号，也不要造本地包。
- **改了行为就要同步改 velashell-docs**，两个 PR 互相引用、一起合 ——
  只改代码不改文档，等于让文档开始骗人。

发现安全漏洞请**不要**开公开 Issue，按 [`SECURITY.md`](SECURITY.md) 的流程私下报告。

---

## 📄 许可证

本项目采用**双许可（Dual License）**模式：

- **[AGPL-3.0](LICENSE)（默认）** —— 自由使用、修改与分发，但衍生作品（含通过网络提供服务）
  **必须以相同许可证开放全部源代码**，并保留版权与捐赠信息。
  移除本项目信息后闭源售卖属于侵权行为，版权方将依法追究（DMCA 下架 / 诉讼）。
- **[商业授权](LICENSE-COMMERCIAL.md)（付费，按需）** —— 需要闭源集成、闭源分发，
  或企业合规无法接受 AGPL 时，可联系作者购买商业许可
  （📧 <dygood@outlook.com>，标题注明「Commercial License」）。

**正版声明**：VelaShell 本体对所有个人与企业**永久免费**，唯一官方发布渠道为本仓库的
GitHub Releases（以及 Microsoft Store 上的同名商店版）；任何渠道的「收费版 VelaShell」均为盗版。
「VelaShell」名称与 Logo 不在开源许可授权范围内，衍生版本不得使用本项目名称与标识宣传或售卖。

向本项目提交贡献即表示同意贡献以 AGPL-3.0 授权，并授予版权方在商业许可下再许可该贡献的权利
（详见 [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) §3）。

---

## 🪶 关于「VelaShell」这个名字

**读音**：`/ˈveɪlə ʃɛl/` —— 读作 **「VAY-la shell」**，中文近似「薇拉·谢尔」，
第一个音节 *Vay* 重读。

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
