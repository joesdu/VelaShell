# PulseTerm

> 一款为运维与开发者打造的现代化跨平台 SSH 终端客户端。

PulseTerm 是一个使用 .NET 与 Avalonia 构建的桌面终端应用，支持 Windows、Linux 与 macOS。它内置自研 VT 终端引擎、SSH/SFTP 连接、端口转发隧道、会话管理、可拖拽分屏与命令面板，旨在为高频远程操作提供**键盘优先、信息密度高、响应迅速**的使用体验。

---

## ✨ 主要特性

- **自研 VT 终端引擎**  
  完整实现 DEC ANSI / VT / Xterm 状态机，支持 256 色、真彩色、DEC 线绘字符、主/备屏、滚动区、应用光标键、鼠标协议、CJK 双宽字符与动态编码切换。

- **SSH 与 SFTP**  
  基于 SSH.NET 实现 Shell、SFTP 文件传输与端口转发。支持密码、私钥与自动探测多种认证方式，会话持久化与快速重连。

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
cd PulseTerm

# 构建整个解决方案
dotnet build

# 或直接构建桌面应用入口项目
dotnet build src/PulseTerm.App/PulseTerm.App.csproj
```

### 运行

```bash
# 开发模式（热重载）
dotnet watch run --project src/PulseTerm.App/PulseTerm.App.csproj

# 发布为 Windows 独立可执行文件
dotnet publish src/PulseTerm.App/PulseTerm.App.csproj -c Release -r win-x64 --self-contained true

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

---

## 🏗️ 项目结构

```text
PulseTerm/
├── src/
│   ├── PulseTerm.App/              # 桌面应用入口、DI 组合根、XAML 视图与全局样式
│   ├── PulseTerm.Terminal/         # 自研 VT 终端引擎与 Avalonia 渲染控件
│   ├── PulseTerm.Presentation/     # 跨层 ViewModel、工作流与 Presentation DI 模块
│   ├── PulseTerm.Controls/         # 复用控件库与主题 Token
│   ├── PulseTerm.Core/             # 领域模型、服务契约、持久化抽象与本地化（无 UI 依赖）
│   └── PulseTerm.Infrastructure/   # SSH/SFTP/隧道/JSON 存储的基础设施实现
├── tests/                          # 单元测试、集成测试与冒烟测试
├── docs/                           # 架构设计、UI 规格与交互说明
├── scripts/                        # 跨平台发布脚本
├── docker-compose.test.yml         # 本地 SSH 测试服务器
├── global.json                     # SDK 版本锁定
└── PulseTerm.slnx                  # Visual Studio 解决方案
```

---

## 🧩 架构亮点

- **严格分层**：依赖方向为 `App → Presentation / Controls / Infrastructure → Core`，Core 层不依赖任何 UI 框架，可独立测试与复用。
- **接口优先**：服务均通过接口注入，便于 Mock 与单元测试。
- **单一组合根**：所有依赖注入注册集中在 [`src/PulseTerm.App/App.axaml.cs`](src/PulseTerm.App/App.axaml.cs)。
- **自绘渲染**：终端通过自定义 Avalonia Control 直接渲染字形、选区与滚动，避免依赖已废弃的第三方终端控件。
- **设计 Token 化**：颜色、字体、间距全部通过资源字典管理，支持主题与品牌定制。

---

## 🧪 测试

项目包含覆盖核心模型、VT 引擎、ViewModel 与集成场景的完整测试套件。

```bash
# 运行全部测试
dotnet test

# 仅运行终端引擎测试
dotnet test tests/PulseTerm.Terminal.Tests/

# 详细输出
dotnet test --logger "console;verbosity=detailed"
```

| 测试项目 | 说明 |
|----------|------|
| `PulseTerm.Core.Tests` | 领域模型、数据存储、服务逻辑 |
| `PulseTerm.Terminal.Tests` | VT 解析、终端仿真、编码与字符宽度 |
| `PulseTerm.Presentation.Tests` | ViewModel 工作流与命令 |
| `PulseTerm.Infrastructure.Tests` | 存储路径与基础设施 |
| `PulseTerm.Controls.Tests` | 自定义控件行为 |
| `PulseTerm.App.Tests` | 集成测试与冒烟测试 |

---

## 📚 文档

- [`docs/architecture.md`](docs/architecture.md) — 分层架构与依赖方向
- [`docs/架构设计.md`](docs/架构设计.md) — 工程化重构蓝图
- [`docs/design-specs.md`](docs/design-specs.md) — UI 视觉规格
- [`docs/交互与界面规格.md`](docs/交互与界面规格.md) — 交互逻辑与设计 Token

---

## 🛠️ 技术栈

- **.NET 10** — 目标运行时
- **Avalonia 12** — 跨平台 XAML UI 框架
- **ReactiveUI** — 响应式 MVVM
- **Dock.Avalonia** — 可拖拽分屏与停靠布局
- **SSH.NET** — SSH / SFTP / 端口转发
- **Velopack** — 自动更新与发布管理
- **MSTest** — 单元测试框架

---

## 🤝 贡献

欢迎提交 Issue 与 Pull Request。在贡献前，建议先阅读 [`docs/architecture.md`](docs/architecture.md) 了解项目的分层约定与依赖方向。

---

## 📄 许可证

本项目采用 [MIT](LICENSE) 许可证。

---

> PulseTerm — 为命令行而生。
