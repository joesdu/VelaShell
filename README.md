# VelaShell

> 一款为运维与开发者打造的现代化跨平台 SSH 终端客户端。

VelaShell 是一个使用 .NET 与 Avalonia 构建的桌面终端应用，支持 Windows、Linux 与 macOS。它内置自研 VT 终端引擎、SSH/SFTP 连接、两步身份验证与主机指纹校验、端口转发隧道、分组会话管理、可拖拽分屏、命令面板与九页设置中心；全部数据经嵌入式 SonnetDB 加密持久化。旨在为高频远程操作提供**键盘优先、信息密度高、响应迅速**的使用体验。

---

## 🪶 关于「VelaShell」

**读音**：`/ˈveɪlə ʃɛl/` — 读作 **「VAY-la shell」**，中文近似「薇拉·谢尔」。第一个音节 *Vay* 重读。

**含义**：由 **Vela** + **Shell** 两部分组成。

- **Vela（船帆座）** — 拉丁语意为「帆」。船帆座是南天的一个星座，与龙骨座（Carina）、船尾座（Puppis）同源，共同拆分自古希腊神话中「阿尔戈号」（Argo Navis）——伊阿宋与阿尔戈英雄们远航寻找金羊毛所乘的巨船。取其**扬帆远航、驶向未知彼岸**之意。
- **Shell（终端外壳）** — 命令行 shell，也是本软件的核心：一个连接远程主机的终端。

合起来，VelaShell 寓意 **「以终端为帆，乘信号之风驶向远方主机」** —— 一个为远程操作扬帆的 SSH shell。图标即这一理念的浓缩：青绿渐变的圆角方块上，一枚深色 `>_` 命令提示符。

### 速览

| 项目 | 说明 |
|------|------|
| **名称** | VelaShell |
| **读音** | `/ˈveɪlə ʃɛl/`（VAY-la shell·薇拉·谢尔） |
| **类别** | 跨平台 SSH / SFTP 终端客户端 |
| **当前版本** | `v0.0.5-beta`（活跃开发中） |
| **平台** | Windows 10 / 11 · Linux · macOS（x64 / arm64） |
| **运行时** | .NET 10 + Avalonia 12，Self-contained 发布（免装 Runtime） |
| **许可证** | 双许可：[AGPL-3.0](LICENSE) / [商业授权](LICENSE-COMMERCIAL.md) · © 2026 VelaShell 作者及贡献者 |

---

## ✨ 主要特性

- **自研 VT 终端引擎**  
  完整实现 DEC ANSI / VT / Xterm 状态机，支持 256 色、真彩色、DEC 线绘字符、主/备屏、滚动区、应用光标键、鼠标协议、CJK 双宽字符与动态编码切换。

- **SSH 与 SFTP**  
  基于 SSH.NET 实现 Shell、SFTP 文件传输与端口转发。支持密码与私钥认证，缺少凭据时自动进入两步身份验证流程（用户名 → 认证方式），认证失败可原地重试。

- **主机密钥信任**  
  首次连接默认 TOFU 自动记录指纹，可切换为人工确认（永久信任 / 仅本次信任 / 取消）；指纹变化立即拒绝连接，防御中间人攻击；设置中可查看与删除已信任主机（支持截图防泄露的地址脱敏）。

- **会话管理**  
  资源管理器按分组维护连接配置（新建/编辑/删除/双击直连）；侧边栏"最近连接"展示 名称-分组 与相对时间，重启不丢失，双击即可重连。

- **嵌入式 SonnetDB 存储**  
  所有持久化（连接配置、分组、设置、known_hosts、命令片段、连接历史、审计日志、会话录制）统一存入本地嵌入式 [SonnetDB](https://github.com/IoTSharp/SonnetDB) 多模型数据库：业务数据用文档集合，最近连接、审计与录制数据块等时间序列数据用时序引擎。连接密码与私钥口令以 **AES-256-GCM** 加密落盘。

- **会话录制与回放**  
  开启后自动记录终端输出（SonnetDB 时序存储，随日志保留天数自动清理）；回放中心支持按时间轴回放、拖动定位、1x/2x/4x 倍速、跳过空闲片段，并可导出为 asciinema 兼容的 asciicast v2（`.cast`）格式。

- **GitHub Gist 云同步**  
  应用设置、连接配置（含分组与端口转发隧道）与代码片段同步到你自己账号下的私密 Gist，多设备无缝漫游；每次同步即一个可回溯的历史版本，支持任意版本恢复；可选口令端到端加密（PBKDF2 + AES-256-GCM），未启用加密时凭据绝不上传。

- **设置中心**  
  十一个设置页面：常规、外观、终端、密钥管理、快捷键、文件传输、安全审计、代码片段、云同步、关于、支持与捐赠。密钥管理可直接枚举 `~/.ssh` 密钥（类型 + SHA256 指纹）、生成 RSA 密钥对、导入与复制公钥。

- **端口转发隧道**  
  本地转发（`-L`）、远程转发（`-R`）与动态 SOCKS5 转发（`-D`）统一管理。

- **可拖拽分屏与浮窗**  
  基于 Dock.Avalonia 的标签页、边缘分屏与窗口撕出，支持多终端并行操作。

- **命令面板**  
  `Ctrl+P` / `Ctrl+K` 呼出命令面板，支持模糊子序列搜索、最近会话与全局命令快速跳转。

- **键盘优先的交互**  
  快捷键驱动的连接、分屏、搜索与文件传输，减少鼠标往返。

- **深色 / 浅色 / 系统主题**  
  设计 Token 化，无硬编码颜色，支持运行时切换。

- **多语言支持**  
  英文与简体中文资源，运行时动态切换。

- **实时状态栏**  
  连接状态、延迟、运行时长、终端尺寸、编码、CPU / 内存 / 网速一目了然。

---

## 🖥️ 平台支持

| 平台 | 架构 | 构建脚本 | 状态 |
|------|------|----------|------|
| Windows 10 / 11 | x64 | [`scripts/build-win.sh`](scripts/build-win.sh) | ✅ 完整支持 |
| Linux | x64 | [`scripts/build-linux.sh`](scripts/build-linux.sh) | ✅ 完整支持 |
| macOS | x64 / arm64 | [`scripts/build-mac.sh`](scripts/build-mac.sh) | ✅ 完整支持 |

发布方式为 **Self-contained**，目标机器无需预装 .NET Runtime。

---

## 🚀 快速开始

### 环境要求

- [.NET SDK](https://dotnet.microsoft.com/download) 10.0.201 或更高版本（`global.json` 锁定）
-（可选）Docker，用于启动本地 SSH 测试服务器

### 克隆与构建

```bash
git clone <仓库地址>
cd VelaShell

# 构建整个解决方案
dotnet build

# 或直接构建桌面应用入口项目
dotnet build src/VelaShell.App/VelaShell.App.csproj
```

### 运行

```bash
# 开发模式（热重载）
dotnet watch run --project src/VelaShell.App/VelaShell.App.csproj

# 发布为 Windows 独立可执行文件
dotnet publish src/VelaShell.App/VelaShell.App.csproj -c Release -r win-x64 --self-contained true

# 使用跨平台脚本
./scripts/build-win.sh
./scripts/build-linux.sh
./scripts/build-mac.sh
```

### 启动测试 SSH 服务器

```bash
docker-compose -f docker-compose.test.yml up
# 用户名：testuser，密码：testpass
# 端口：2222
```

### 数据与配置位置

| 内容 | 位置 |
|------|------|
| SonnetDB 数据目录（连接/分组/设置/known_hosts/连接历史/审计) | `%LocalAppData%/VelaShell/sonnetdb` |
| 凭据加密密钥（AES-256) | `%LocalAppData%/VelaShell/secret.key` |
| SSH 密钥对（密钥管理页) | `~/.ssh` |

> 旧版本的 `sessions.json` / `settings.json` 等 JSON 配置会在首次运行时自动导入 SonnetDB 并改名为 `*.migrated.bak`。

---

## 🏗️ 项目结构

```text
VelaShell/
├── src/
│   ├── VelaShell.App/              # 桌面应用入口、DI 组合根、XAML 视图与全局样式
│   ├── VelaShell.Terminal/         # 自研 VT 终端引擎与 Avalonia 渲染控件
│   ├── VelaShell.Presentation/     # 跨层 ViewModel、工作流与 Presentation DI 模块
│   ├── VelaShell.Controls/         # 复用控件库与主题 Token
│   ├── VelaShell.Core/             # 领域模型、服务契约、持久化抽象与本地化（无 UI 依赖）
│   └── VelaShell.Infrastructure/   # SSH/SFTP/隧道实现、SonnetDB 持久化、AES-256 凭据加密
├── tests/                          # 单元测试、集成测试与冒烟测试
├── docs/                           # 架构设计、UI 规格与交互说明
├── scripts/                        # 跨平台发布脚本
├── docker-compose.test.yml         # 本地 SSH 测试服务器
├── global.json                     # SDK 版本锁定
└── VelaShell.slnx                  # Visual Studio 解决方案
```

---

## 🧩 架构亮点

- **严格分层**：依赖方向为 `App → Presentation / Controls / Infrastructure → Core`，Core 层不依赖任何 UI 框架，可独立测试与复用。
- **接口优先**：服务均通过接口注入，便于 Mock 与单元测试。
- **单一组合根**：所有依赖注入注册集中在 [`src/VelaShell.App/App.axaml.cs`](src/VelaShell.App/App.axaml.cs)。
- **自绘渲染**：终端通过自定义 Avalonia Control 直接渲染字形、选区与滚动，避免依赖已废弃的第三方终端控件。
- **设计 Token 化**：颜色、字体、间距全部通过资源字典管理，支持主题与品牌定制。
- **单引擎持久化**：一个嵌入式 SonnetDB 实例承载文档（配置/业务数据）与时序（连接历史/审计）两类模型，接口在 Core、实现在 Infrastructure，退出时统一刷盘；旧版 JSON 配置首次运行自动迁移。
- **安全默认值**：凭据静态加密（AES-256-GCM + 本地密钥文件）、主机指纹 TOFU 校验、"记住密码"可按连接关闭。

---

## 🧪 测试

项目包含覆盖核心模型、VT 引擎、ViewModel 与集成场景的完整测试套件。

```bash
# 运行全部测试
dotnet test

# 仅运行终端引擎测试
dotnet test tests/VelaShell.Terminal.Tests/

# 详细输出
dotnet test --logger "console;verbosity=detailed"
```

| 测试项目 | 说明 |
|----------|------|
| `VelaShell.Core.Tests` | 领域模型、数据存储、服务逻辑 |
| `VelaShell.Terminal.Tests` | VT 解析、终端仿真、编码与字符宽度 |
| `VelaShell.Presentation.Tests` | ViewModel 工作流与命令 |
| `VelaShell.Infrastructure.Tests` | SonnetDB 持久化、凭据加密、SSH 密钥管理 |
| `VelaShell.Controls.Tests` | 自定义控件行为 |
| `VelaShell.App.Tests` | 视图模型、身份验证流程、集成与冒烟测试 |

---

## 📚 文档

- [`docs/architecture.md`](docs/architecture.md) — 分层架构、依赖方向与 SonnetDB 持久化策略
- [`docs/架构设计.md`](docs/架构设计.md) — 工程化重构蓝图
- [`docs/design-specs.md`](docs/design-specs.md) — UI 视觉规格
- [`docs/交互与界面规格.md`](docs/交互与界面规格.md) — 交互逻辑与设计 Token
- [`plan.md`](plan.md) — 进展记录、已知问题与后续待办(开发跟进以此为准)

---

## 🛠️ 技术栈

- **.NET 10** — 目标运行时
- **Avalonia 12** — 跨平台 XAML UI 框架
- **ReactiveUI** — 响应式 MVVM
- **Dock.Avalonia** — 可拖拽分屏与停靠布局
- **SSH.NET** — SSH / SFTP / 端口转发
- **SonnetDB** — 嵌入式多模型数据库（文档 + 时序），唯一持久化引擎
- **Velopack** — 自动更新与发布管理
- **MSTest** — 单元测试框架

---

## 🚧 开发状态

项目处于活跃开发阶段。核心链路(终端引擎、SSH/SFTP、会话管理、身份验证、持久化、设置中心)已可用;部分设置项目前仅持久化、待接线到运行时,非 SSH 协议(SFTP 标签页/Telnet/串口)与证书认证暂未开放。完整的完成情况与待办清单见 [`plan.md`](plan.md) §9-§10。

---

## 🤝 贡献

欢迎提交 Issue 与 Pull Request。在贡献前，建议先阅读 [`docs/architecture.md`](docs/architecture.md) 了解项目的分层约定与依赖方向。

---

## 📄 许可证

本项目采用**双许可(Dual License)**模式：

- **[AGPL-3.0](LICENSE)(默认)**：自由使用、修改与分发，但衍生作品（含通过网络提供服务）**必须以相同许可证开放全部源代码**，并保留版权与捐赠信息。移除本项目信息后闭源售卖属于侵权行为，版权方将依法追究（DMCA 下架 / 诉讼）。
- **[商业授权](LICENSE-COMMERCIAL.md)（付费，按需）**：需要闭源集成、闭源分发或企业合规无法接受 AGPL 时，可联系作者购买商业许可（📧 <dygood@outlook.com>，标题注明「Commercial License」）。

**正版声明**：VelaShell 本体对所有个人与企业**永久免费**，唯一官方发布渠道为本仓库的 GitHub Releases；任何渠道的「收费版 VelaShell」均为盗版。「VelaShell」名称与 Logo 不在开源许可授权范围内，衍生版本不得使用本项目名称与标识宣传或售卖。

向本项目提交贡献即表示同意贡献以 AGPL-3.0 授权，并授予版权方在商业许可下再许可该贡献的权利（详见 [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) §3）。

---

> VelaShell — 为命令行而生。
