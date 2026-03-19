# PulseTerm 工程化计划

## 1. 当前结论

- 现有仓库不是空白工程，已经有 `PulseTerm.App`、`PulseTerm.Core`、`PulseTerm.Terminal` 及对应测试项目。
- `PulseTerm-zh.pen` 是可读的 `.pen` JSON 文档；虽然本次 `pencil` 连接不上本地编辑器，但通过静态解析已确认设计稿包含：
  - 主界面（侧边栏 / 多标签终端 / 文件区 / 状态栏）
  - 设置中心（常规 / 外观 / 终端 / 密钥管理 / 快捷键 / 文件传输 / 关于 / 安全审计 / 代码片段）
  - 命令面板、快捷命令面板、隧道管理、连接诊断、文件传输提示等独立面板
  - 完整的 dark/light 主题变量
- 现有三层结构可以作为起点，但不足以长期支撑：
  - 大量自定义控件
  - 动态主题与主题色切换
  - 复杂终端渲染与输入输出
  - 平台能力、持久化、后台任务、国际化的边界清晰化

## 2. 目标架构原则

1. **UI 与业务彻底解耦**：Avalonia 视图、ViewModel、终端引擎、SSH/持久化分层明确。
2. **自定义控件独立成库**：避免所有视觉资产和控件挤在 `PulseTerm.App` 中。
3. **主题运行时热切换**：深浅色、系统跟随、主题色修改都要求实时生效。
4. **国际化优先设计**：资源、格式化、文化切换路径要预留完整。
5. **终端高性能优先**：解析、绘制、I/O、后台传输彼此隔离，避免 UI 线程成为瓶颈。
6. **解决方案级治理**：统一构建参数、统一命名规则、统一测试结构。

## 3. 推荐目标项目结构

```text
src/
├── PulseTerm.App/                # Avalonia 启动入口、桌面生命周期、DI 组合根
├── PulseTerm.Presentation/       # ViewModel、导航、应用状态编排、命令协调
├── PulseTerm.Controls/           # 自定义控件、样式、行为、设计 token 映射
├── PulseTerm.Terminal/           # 终端引擎、ANSI/OSC/CSI 解析、渲染模型、选择/链接识别
├── PulseTerm.Core/               # 领域模型、应用抽象、跨层契约
└── PulseTerm.Infrastructure/     # SSH.NET、JSON/LiteDB、文件系统、平台主题、后台服务

tests/
├── PulseTerm.App.Tests/
├── PulseTerm.Presentation.Tests/
├── PulseTerm.Controls.Tests/
├── PulseTerm.Terminal.Tests/
├── PulseTerm.Core.Tests/
└── PulseTerm.Infrastructure.Tests/
```

### 项目职责说明

#### `PulseTerm.App`
- 只保留桌面应用入口职责：`Program.cs`、`App.axaml`、主窗口宿主、平台生命周期。
- 不直接承载复杂业务逻辑。
- 依赖 `Presentation` 与 `Controls`，由它负责把服务组装起来。

#### `PulseTerm.Presentation`
- 承载 ViewModel、导航状态、工作区编排、窗口/对话框协调。
- 负责把 `Core` + `Infrastructure` + `Terminal` 的能力投影到 UI。
- 不直接依赖具体的 Avalonia Window 生命周期对象。

#### `PulseTerm.Controls`
- 承载所有可复用自定义控件与视觉基础设施。
- 重点包括：
  - 会话树节点控件
  - 终端标签项
  - 终端工具栏按钮组
  - 文件列表行 / 文件传输项 / 隧道项
  - 设置导航项 / 指标 Chip / 浮层面板外壳
  - 主题 token、颜色映射、资源字典、样式选择器、行为
- **结论：需要单独组件代码库。**
  - 否则 `App` 会迅速膨胀成“视图 + 控件 + 样式 + 资源”的耦合体。
  - 后续如果你要做控件预览、快照测试、主题回归测试，也更适合单独成库。

#### `PulseTerm.Terminal`
- 作为性能敏感模块单独演进。
- 目标职责：
  - ANSI 转义序列解析
  - 终端缓冲区模型（主缓冲/滚动回溯）
  - 文本属性层（颜色、粗体、错误高亮、URL/linkify）
  - 选择复制、矩形选择、多行粘贴输入
  - 逐字输出 / 整行输出 / 粘贴输入体验
  - 兼容 Ubuntu / CentOS 等进度条刷新模式
- 当前仓库使用 `AvaloniaTerminal`，建议把它视作**过渡实现**，通过抽象逐步替换为自研控件与渲染层。

#### `PulseTerm.Core`
- 只放稳定模型与抽象：
  - 会话 / 隧道 / 传输 / 设置 / 主题 / 本地化模型
  - 终端相关契约（buffer、parser、link detector、selection service 抽象）
  - 跨层接口（仓储、SSH 会话、主题服务、语言服务、后台任务调度器）
- 避免继续把具体 SSH.NET 或存储实现塞进去。

#### `PulseTerm.Infrastructure`
- 实现 `Core` 中定义的基础设施接口：
  - SSH.NET / SFTP / 隧道
  - JSON / LiteDB 持久化
  - 配置存储
  - 系统主题监听
  - 平台集成
  - 后台文件传输与任务调度

## 4. 关键技术设计

### 4.1 自定义控件策略

优先级从高到低：

1. **终端控件族**
   - `TerminalSurface`
   - `TerminalTextLayer`
   - `TerminalSelectionLayer`
   - `TerminalInputOverlay`
   - `TerminalScrollBar`
2. **Shell 控件族**
   - `SessionTreeView`
   - `SessionTreeItem`
   - `TerminalTabStrip`
   - `StatusMetricChip`
   - `DockPanelShell`
3. **业务面板控件族**
   - `FileTransferItemControl`
   - `TunnelCardControl`
   - `QuickCommandList`
   - `CommandPaletteResultItem`
   - `SettingsNavItem`

### 4.2 主题系统

- 采用三层主题模型：
  1. **ThemeVariant**：Dark / Light / System
  2. **AccentColor**：主题色动态覆盖
  3. **Semantic Tokens**：`bg-page`、`text-primary`、`status-connected` 等
- 设计稿中的 `.pen` variables 作为语义 token 来源。
- `Controls` 负责 token 到 Avalonia `DynamicResource` 的映射。
- `Infrastructure` 负责系统主题监听；`Presentation` 负责设置变更广播。

### 4.3 国际化

- 保留 `.resx` 路线；它对 .NET/Avalonia 最稳妥。
- 将语言切换能力设计成服务接口，但 UI 是否热切换可以分阶段：
  - 第一阶段：启动时应用 + 设置持久化
  - 第二阶段：运行时热切换 UI

### 4.4 终端渲染与高性能

- 必须把以下能力拆开，避免一个“超级 TerminalControl”：
  - SSH 输入输出桥接
  - ANSI parser
  - 屏幕/scrollback buffer
  - 高亮/链接识别
  - 绘制层
  - 用户输入编辑缓冲
- 原则：
  - I/O 不阻塞 UI 线程
  - parser 可后台运行
  - UI 线程只负责最终渲染快照提交
  - 文件传输、隧道监控、诊断面板通过独立后台服务驱动

### 4.5 持久化

- **配置 / 用户偏好 / 最近状态**：优先 JSON
- **需要更强查询与索引的数据**（会话片段、传输记录、密钥索引）可逐步引入 LiteDB
- 建议分层：
  - JSON：settings / theme / language / layout / quick commands
  - LiteDB：session metadata / transfer history / diagnostics snapshots（后续阶段）

## 5. 分阶段实施计划

### Phase 0 — 工程底座调整
- 补齐解决方案级构建治理（共享 props、文档、项目边界）
- 新建 `Presentation`、`Controls`、`Infrastructure` 项目
- 让解决方案在新结构下仍可编译

### Phase 1 — 重新划分边界
- 把 `App` 中的 ViewModel 移入 `Presentation`
- 把主题资源与可复用样式迁入 `Controls`
- 把 `Core` 里的具体基础设施实现迁往 `Infrastructure`

### Phase 2 — 主题与国际化升级
- 支持 Dark / Light / System
- 支持主题色热更新
- 统一资源和 token 管理
- 补齐语言设置及持久化路径

### Phase 3 — 终端能力重构
- 为 `AvaloniaTerminal` 增加适配层，避免直接绑定第三方实现
- 引入自研 scrollback、linkify、selection、paste pipeline
- 处理 ANSI 与常见 Linux 进度条刷新行为

### Phase 4 — Shell 与面板组件化
- 会话树、终端标签栏、文件浏览器、状态栏、浮层面板统一组件化
- 建立控件测试与样式回归测试

### Phase 5 — 基础设施强化
- 后台文件传输任务编排
- SSH / SFTP / Tunnel 服务边界清理
- 系统主题监听与平台适配

### Phase 6 — 稳定性与质量
- 单元测试、Avalonia Headless 测试、终端 parser 测试、快照测试
- 性能 profiling 与 UI 卡顿治理

## 6. 本轮立即执行内容

1. 写入本计划文件。
2. 创建新的目标项目骨架：`Presentation`、`Controls`、`Infrastructure`。
3. 更新解决方案文件，把新项目纳入统一管理。
4. 增加一份架构蓝图文档，明确每个项目的职责与迁移方向。
5. 验证新解可以成功构建。

## 7. 本轮之后的首批迁移建议

优先迁移顺序：

1. `ViewModels/*` → `PulseTerm.Presentation`
2. `Themes/*` 与可复用样式 → `PulseTerm.Controls`
3. `ThemeService` / 系统主题监听抽象重构
4. `Data/*`、SSH.NET 实现、配置存储 → `PulseTerm.Infrastructure`
5. `TerminalTabView` 对终端引擎的直接依赖改为接口依赖

## 8. 对“是否需要单独组件库”的明确回答

**需要，而且建议现在就建立。**

原因：

- 你的产品不是普通 CRUD 桌面应用，界面大量依赖自定义视觉与交互。
- 主题、终端外壳、会话树、文件项、隧道卡片、命令面板都具备高度复用性。
- 单独控件库能让：
  - UI 结构更清晰
  - 样式与业务解耦
  - 控件测试更容易
  - 后续设计调整成本更低
  - 未来做独立 Demo / Preview / Storybook 风格工具更方便
