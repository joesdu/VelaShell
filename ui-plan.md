# UI 窗口外框跨平台适配计划

> 2026-09-25 立项（用户需求）。本文件是交给**新会话**执行的工作计划：先做试点（§7），用户在 macOS / Linux 上确认后再推广（§10）。
> 做完后按 `AGENTS.md` 的分工，把经过写进 `plan.md` 新的一节（当前最后一节是 §115）；本文件是否保留由用户决定。

## 0. 开工前

- 先读完 `AGENTS.md`。与本任务相关的硬规则：
  - 界面行为或规格变了，要同步 velashell-docs，`zh/` 与 `en/` 一起改（见 §11）。
  - 提交信息与 PR 描述不加任何 AI 工具署名；只有用户要求时才提交、建 PR。
  - 不用 Python；脚本用 PowerShell 或 C# 单文件程序。
  - 文件用 CRLF、UTF-8 无 BOM；构建开着 `TreatWarningsAsErrors`（含 XML 文档注释与 IDE 分析器）。
  - 不许用「自研」「自建」给东西贴标签。
- 开发机是 Windows，**跑不了 macOS 和 Linux 的界面**。这两个平台只能保证编译与测试通过，外观与交互由用户实机确认（§8 清单）。
- Avalonia 版本 12.1.3。§4 的每条结论都来自该版本源码（<https://github.com/AvaloniaUI/Avalonia/tree/12.1.3>），动手前可以再对一遍。

## 1. 问题

- 用户在 Linux 上的截图：设置窗口的卡片外面多出一圈磨砂状的「空白」；Windows 上正常。那台 Linux 的合成器会给每个窗口加圆角、描边和背景模糊。
- 原因：对话框的写法是 `WindowDecorations="None"` + 透明窗口 + 卡片 `Margin="16"` + 在这 16px 里画 `BoxShadow`（`VelaShadowWindow`）。合成器只知道整个窗口矩形（设置窗口 948×768），于是沿整个矩形描边、切圆角、做模糊，透明边距就成了那一圈。与走 X11 还是 Wayland 无关。
- Windows 正常，是因为 DWM 对无边框透明窗口什么都不加。
- macOS 现状：Avalonia 在 `None` 模式下关掉系统阴影，也没有圆角；我们又为了滚动卡顿把这些窗口改成了不透明，结果是没有阴影的直角矩形（`SettingsView.ApplyMacOsOpaqueWindow` 以及另外 6 个窗口里的 macOS 分支）。
- `Views/` 下用这种写法的窗口共 20 个：
  - 模态（`ShowDialog`，12 个）：`AgentSignPromptView`、`AuthenticationDialogView`、`ConnectionDiagnosticsView`、`ConnectionProfileView`、`HostKeyPromptView`、`LocalPathPickerDialog`、`MessageDialog`、`PluginPermissionDialog`、`SessionImportView`、`SettingsView`、`TunnelHelpDialog`、`XServerHelpDialog`
  - 非模态（`Show`，8 个）：`DirectorySyncWindow`、`PluginManagerWindow`、`PluginPanelWindow`、`ProcessManagerView`、`RecordingPlayerView`、`RemoteFileEditorView`、`ResourceMonitorWindow`、`TraceRouteWindow`

## 2. 目标

1. 三个平台上的弹窗都是「圆角卡片 + 柔和阴影 + 1px 描边」，与 Windows 现状一致；Linux 上不再有外圈。
2. 每个平台用自己的原生机制实现，不再依赖「系统对窗口什么都不加」。
3. macOS 主窗口使用原生红绿灯（关闭 / 最小化 / 全屏）。
4. **Windows 零改动**：外观、标题栏、贴靠布局、拖动全部保持原样。
5. 标题栏右上角的功能按钮组（搜索、SFTP 文件管理、链路追踪、任务管理器、X Server、隧道、命令面板）在各平台照常可用。

## 3. 方案

|            | Windows | macOS | Linux 原生 Wayland | Linux X11（含从 Wayland 回退） |
|------------|---------|-------|--------------------|-------------------------------|
| 主窗口     | 不变 | `Full` + `ExtendClientAreaToDecorationsHint`：原生红绿灯、系统圆角与阴影；隐藏自绘的三个窗口按钮，左侧给红绿灯让位 | 不变 | 不变 |
| 模态对话框 | 不变 | `BorderOnly` + `ExtendClientAreaToDecorationsHint`：系统圆角与阴影、不显示红绿灯、窗口不透明 | `BorderOnly` + 自定义 `WindowDecorationsTheme`：Avalonia 画阴影与描边，并经 `xdg_surface.set_window_geometry` 告诉合成器真正的窗口范围 | 不透明矩形（去掉边距、圆角、阴影） |
| 非模态窗口 | 不变 | `Full` + `ExtendClientAreaToDecorationsHint`：原生红绿灯 | 同模态对话框 | 同模态对话框 |

透明窗口 + 16px 边距 + 自绘阴影的写法只在 Windows 上保留。

## 4. 已核实的 Avalonia 12.1.3 行为（附源码位置）

### macOS（`native/Avalonia.Native/src/OSX/`、`src/Avalonia.Native/`）

- `WindowImpl.mm` 约 240–275 行：
  - `None` → `setHasShadow:NO`，没有标题栏样式，因此没有系统圆角；
  - `BorderOnly` → `Titled | FullSizeContentView`、`setHasShadow:YES`、标题隐藏、标题栏透明；
  - `Full` → 常规标题栏。
- `WindowImpl.mm` 约 602–612 行：红绿灯只在 `Full` 时显示（`hasTrafficLights = _decorations == SystemDecorationsFull`）。
- `WindowImpl.mm` `SetExtendClientArea`（约 380 行）：标题隐藏、标题栏透明、内容铺满整个窗口。
- `AutoFitContentView.mm` `SetTitleBarHeightHint`：`ExtendClientAreaTitleBarHeightHint` 只改标题栏背景材质的高度，**不移动红绿灯**。Avalonia 没有调整红绿灯位置的接口。
- `src/Avalonia.Native/WindowImpl.cs`：
  - `NeedsManagedDecorations => false`：macOS 上没有 Avalonia 自己画的标题栏层，不会有东西盖在按钮上；
  - `ChromeHitTest`（约 141–183 行）：扩展模式下按下左键时，先对自己的界面做命中测试；只有没命中任何元素、或命中 `ElementRole="TitleBar"` 的元素，才交给系统拖动 / 双击切换最大化；命中按钮就是普通点击；
  - `InvalidateExtendedMargins`：只在 `Full` + 扩展时把 `ExtendedMargins.Top` 设为标题栏高度。
- `AvnWindow.mm` `sendEvent` / `isPointInTitlebar`（约 487–570 行）：原生层只截留红绿灯（`_NSThemeWidget`）的点击；起于标题栏区域、拖出去的鼠标事件会转回 Avalonia 视图（仅扩展模式）。

### Linux Wayland（`src/Avalonia.Wayland/`）

- `WindowImpl.cs` `SetWindowDecorations`（约 245 行）：非 `Full` 时永久切到客户端装饰（`_csdSticky`）。`RequestedDrawnDecorations`（约 299 行）：客户端装饰时请求标题栏、描边、缩放抓取区和阴影。
- `src/Avalonia.Controls/Window.cs`：
  - `ComputeDecorationParts`（约 761 行）：`None` 不画任何装饰；`BorderOnly` 画阴影、描边、缩放抓取区，不画标题栏；
  - `UpdateDrawnDecorationMargins`（约 801 行）：把阴影宽度交给 `SetShadowExtents`；「强制模式」（未扩展）下由 `TopLevelHost.DecorationInset` 把窗口内容往里缩。
- `Server/Persistent/WSurface.cs` `MaybeEmitWindowGeometry`（约 571 行）：根据阴影宽度调用 `xdg_surface.set_window_geometry`，合成器以内圈为窗口边界。GTK4 程序就是这样做的。
- `src/Avalonia.Controls/TopLevelHost.Decorations.cs` `UpdateResizeGripThickness`：缩放抓取区只覆盖描边与阴影区，不覆盖窗口内容。
- 装饰外观由 `Window.WindowDecorationsTheme`（目标类型为 `WindowDrawnDecorations` 的 `ControlTheme`）决定。Fluent 默认模板在 `src/Avalonia.Themes.Fluent/Controls/WindowDrawnDecorations.xaml`：
  - `DefaultShadowThickness=8`、`DefaultFrameThickness=1`；
  - `PART_WindowBorder` 没有圆角，`:has-shadow` 时 `BoxShadow="0 2 10 2 #80000000"`；
  - `PART_UnderlayWrapper` / `PART_OverlayWrapper` 按 `ShadowThickness` 内缩。
- `WindowImplBase.cs` 约 53 行：`Handle => null`。

### Linux X11（`src/Avalonia.X11/`）

- `X11Window.cs` 约 1621 行：`BorderOnly` 也会让 Avalonia 画装饰（`NeedsDrawnDecorations`），但 X11 后端没有实现 `SetShadowExtents`，也不写 `_GTK_FRAME_EXTENTS`。合成器拿到的仍是整个矩形，外圈照旧。所以 X11 下改走不透明矩形。
- 窗口句柄的描述符是 `"XID"`（约 206 行）。可以用 `TryGetPlatformHandle()?.HandleDescriptor == "XID"` 区分 X11 与 Wayland（Wayland 返回 null）。试点里要先验证这个判断在窗口构造时就成立。

### 通用

- `src/Avalonia.Base/Input/WindowDecorationsElementRole.cs`：`TitleBar`（拖动区）、`User`（与外框区域重叠时仍接收输入）、`CloseButton` / `MinimizeButton` / `MaximizeButton` 等。12.0.5 就已经有这个枚举。

## 5. 历史教训

- 2026-07-12 的提交 `75800523` 在 Windows 上试过 `ExtendClientArea` + `ElementRole`（当时是 Avalonia 12.0.5），结果失败并撤回：Avalonia 的托管装饰重复画了标题和按钮，`User` 角色的按钮点不动，`BorderOnly` 还丢了 `WS_CAPTION`（Avalonia issue #21160 / #21212）。详见 `plan.md` 第 216–217 行，以及 velashell-docs `zh/host/architecture.md` 的「窗口壳」一节。
  - **本计划不在 Windows 上启用这套机制。** 文档里那条「⚠️ 不要试图改回去」本来就是 Win32 上得出的结论，同步文档时把它限定到 Windows（§11）。
- `VisualRoot as Window` 恒为 null（视觉根是 TopLevelHost），取窗口必须走逻辑树 `FindLogicalAncestorOfType<Window>()`（提交 `c12a8ff4`）。
- 自绘标题栏的拖动统一走 `WindowMoveDrag.BeginWindowMoveDrag`，`WindowMoveDragUsageTests` 钉住了这条约定。Windows 上 `WM_NCHITTEST` 只能有一个钩子，已被 `Win32WindowChrome` 占用，不要再挂 WndProc 钩子。

## 6. 设计

### 6.1 统一的外框辅助类

新增 `src/VelaShell/Views/WindowChrome.cs`（名字可调，注意与现有的 `Win32WindowChrome` 区分）：

- `enum WindowChromeKind { Main, Dialog, Tool }`。
- `enum ChromePlatform { Windows, MacOS, LinuxWayland, LinuxX11 }`：`Detect(Window)` 按 `OperatingSystem` 和 §4 的句柄描述符判断。
- `static void Apply(Window window, WindowChromeKind kind)`：在窗口构造函数的 `InitializeComponent()` 之后调用。另留一个显式传入平台的 `internal` 重载，供无头测试覆盖四种平台分支。
- 它负责设置 `WindowDecorations`、`ExtendClientAreaToDecorationsHint`、`TransparencyLevelHint`、`Background`、`WindowDecorationsTheme`（仅 Linux Wayland），并给窗口加一个平台样式类（如 `chrome-windows` / `chrome-macos` / `chrome-wayland` / `chrome-x11`）。

### 6.2 样式按类切换

- 各窗口的根卡片加统一类名（如 `Classes="window-card"`）。现在的命名不统一：`RootBorder`、`RootCard`，还有匿名的 `Border`。
- 共享样式（如 `Themes/WindowChrome.axaml`，在 `App.axaml` 引入）：
  - 默认（Windows）：保持现状，`Margin=16`、`CornerRadius=8`、`BorderThickness=1`、`BoxShadow=VelaShadowWindow`。
  - `chrome-macos`：`Margin=0`、`CornerRadius=0`、`BorderThickness=0`、不设 `BoxShadow`。圆角、描边、阴影由系统画。
  - `chrome-wayland`：`Margin=0`、`BorderThickness=0`、`CornerRadius=7`（描边内侧的半径）、不设 `BoxShadow`、`ClipToBounds`。描边与阴影由装饰层画。
  - `chrome-x11`：同 macOS，即不透明直角矩形。
- XAML 里写死的 `Margin="16"`、`BoxShadow`、`TransparencyLevelHint="Transparent"`、`Background="Transparent"` 要删掉：本地值优先级高于样式，留着的话样式不生效。改由样式和辅助类设置。

### 6.3 Linux Wayland 的装饰主题

- 新建一个 `ControlTheme`（`TargetType="WindowDrawnDecorations"`）：
  - `DefaultShadowThickness=16`，与 Windows 的边距一致，容得下 `VelaShadowWindow` 的 `0 4 12 0`；
  - `DefaultFrameThickness=1`；`BorderOnly` 不画标题栏，所以标题栏高度无关；
  - `PART_WindowBorder`：`CornerRadius=8`、`BorderBrush=VelaBorderSecondary`，`:has-shadow` 时 `BoxShadow=VelaShadowWindow`。背景是用 `VelaBgSurface` 还是保持透明、由卡片自己填，以圆角处不漏底为准；
  - 模板结构照 Fluent 的（`WindowDrawnDecorationsTemplate` → `WindowDrawnDecorationsContent`，含 Underlay / Overlay / FullscreenPopover）。Overlay 不放标题栏按钮。
- 只在 Wayland 分支里，经 `Window.WindowDecorationsTheme` 设到窗口上。

### 6.4 窗口尺寸

- 现在 Windows 上的窗口尺寸包含 16px 边距：设置窗口 948×768，可见卡片 916×736。要保证各平台的可见卡片尺寸与 Windows 一致：
  - macOS / X11：没有边距，窗口宽高各减 32。
  - Wayland：阴影在装饰层里，要先实测 `Width` / `Height` 是否包含装饰（阴影 + 描边），再决定怎么调。
- `SettingsView.FitIntoWorkArea` 的边界计算要跟着改。
- 用 `SizeToContent="Height"` 的窗口（如 `MessageDialog`）要确认高度仍然正确。

### 6.5 macOS 主窗口

- 只在 macOS 上、在代码里设置 `WindowDecorations=Full`、`ExtendClientAreaToDecorationsHint=True`。不改 XAML 默认值，免得影响 Windows。
- `TitleBarView`：
  - macOS 上隐藏第 3 列自绘的 最小化 / 最大化 / 关闭。
  - 左侧 logo 那一列加约 78px 的左边距给红绿灯，实测后微调。
  - 标题栏空白处的拖动和双击：先保留现有的 `Bar_PointerPressed`（非 Windows 走 `Window.BeginMoveDrag`，即原生拖动）。标题栏的 `Border` 有背景，`ChromeHitTest` 不会同时触发，不会重复处理，试点里确认一下。
  - 高度：现在是 36px。红绿灯按约 28pt 高的标准标题栏垂直居中，会偏上约 4px。先保持 36 看效果，由用户决定 macOS 上要不要改成 28–32。
- `MainWindow`：macOS 上关闭自绘缩放抓取区 `ResizeGrips`，系统已经提供窗口边缘缩放。
- 绿色按钮默认进入原生全屏，要确认全屏下标题栏和功能按钮正常。
- 右上功能按钮组（`TitleBarView.axaml` 第 2 列）不需要改。它们是普通 `Button`，按 §4 的 `ChromeHitTest` 走普通点击。

## 7. 试点

范围：

1. `WindowChrome` 辅助类 + 共享样式 + Wayland 装饰主题 + 单元测试。
2. `SettingsView`（模态、大窗口、可缩放）：删掉 `ApplyMacOsOpaqueWindow`，改用辅助类。
3. `MessageDialog`（模态、`SizeToContent`、右上角有 ×）。
4. macOS 主窗口的红绿灯（`MainWindow` + `TitleBarView`）。

步骤：

1. 改动前在 Windows 上截图：主窗口、设置窗口、消息框，暗色与亮色主题各一张，留作「Windows 零改动」的对照。
2. 按 §6 实现。
3. 测试（`tests/VelaShell.Tests`，Avalonia 无头模式）：
   - 四种平台分支下，`Apply` 设置的属性和样式类都对；
   - Windows 分支下三个窗口的属性与改动前一致（`WindowDecorations=None`、透明、卡片 `Margin=16` 等）；
   - 现有的 `SettingsView`、`MessageDialog`、`WindowMoveDragUsageTests` 等用例照常通过。
4. 整个解决方案（`VelaShell.slnx`）构建 0 警告 0 错误，相关测试项目全绿。
5. 在 Windows 上运行程序，对照第 1 步的截图确认没有变化。
6. 交给用户按 §8 在 macOS 和 Linux 上验证。

## 8. 验收清单（用户实机）

Windows

- [ ] 主窗口、设置窗口、消息框的外观与改动前一致
- [ ] 标题栏：功能按钮、最小化 / 最大化 / 关闭、拖动、双击最大化、Win11 贴靠布局、窗口边缘缩放

macOS

- [ ] 主窗口左上角显示红绿灯，关闭 / 最小化 / 全屏可用；自绘的三个按钮不再显示
- [ ] 右上功能按钮的点击和悬停提示正常，特别是窗口最上面约 28pt 那一条
- [ ] 标题栏空白处的拖动、双击行为
- [ ] 红绿灯与标题栏的垂直对齐能否接受（保持 36px，还是改成 28–32）
- [ ] 全屏下的标题栏和功能按钮
- [ ] 设置窗口、消息框：系统圆角 + 系统阴影，没有红绿灯
- [ ] 消息框右上角的 × 能点，标题区能拖动
- [ ] 设置窗口滚动流畅（当初改成不透明就是为了这个）
- [ ] 卡片的可见尺寸与 Windows 一致

Linux 原生 Wayland（用户的桌面；条件允许的话再测 GNOME 和 KDE）

- [ ] 设置窗口、消息框外面不再有一圈
- [ ] 阴影、圆角、描边的观感与 Windows 一致；合成器自己加的描边和圆角贴着卡片
- [ ] 背景模糊是否只在卡片范围内（取决于合成器，记录下来即可）
- [ ] 拖动、消息框的 ×、弹窗居中、设置窗口缩放

Linux X11（在 X11 会话里测；或临时把 `WAYLAND_DISPLAY` 设成一个不存在的名字，让程序回退到 X11，这个做法需先确认可行）

- [ ] 弹窗是不透明矩形，没有外圈

## 9. 风险与未决

1. **（最大风险）** macOS 窗口最上面约 28pt 的点击和悬停能否到达我们的按钮。源码的设计上可以，但必须实机确认。
2. 红绿灯的位置不能调（§4）。想让它居中只能改标题栏高度；不要用 Objective-C 运行时去硬挪，系统在窗口缩放、全屏时会重新排版。
3. Linux 上背景模糊的范围取决于合成器。
4. Wayland 下 `Width` / `Height` 与装饰的关系、`SizeToContent` 的表现（§6.4）。
5. 区分 X11 / Wayland 的方法（§4）能否在窗口构造时使用。
6. 用户那台 Linux 用的是哪个桌面或合成器还不知道（截图看像 Hyprland，或开了模糊的 KDE），开工时先问。
7. `BorderOnly` 在 Wayland 下会永久切到客户端装饰（`_csdSticky`）。对话框不受影响，但不要在主窗口上来回切换外框模式。

## 10. 推广（试点通过后）

- 其余 10 个模态对话框：做法同 §7。
- 8 个非模态窗口：
  - macOS 用 `Full` + 扩展，显示红绿灯，隐藏它们各自的关闭 / 最大化按钮，左侧让位；
  - Linux 做法同对话框；
  - 它们现有的「最大化时去掉圆角和边距」逻辑（各自的 `ApplyCardShape`）并进辅助类。
- 删掉各窗口里的 macOS 分支：`DirectorySyncWindow`、`PluginManagerWindow`、`PluginPanelWindow`、`ProcessManagerView`、`ResourceMonitorWindow`、`TraceRouteWindow`（`SettingsView` 的在试点里已经删了）。
- 加一条测试扫描 `Views/*.axaml`，禁止再出现写死的「`TransparencyLevelHint="Transparent"` + `Margin="16"` 卡片」写法，防止改回去。
- 顺带：`Program.cs` 里写着「Linux 暂用 X11」的注释已经与 `UseWaylandWithFallback()` 不符，一并改掉。

## 11. 文档与记录

- velashell-docs（`zh/` 与 `en/` 一起改，分支 `feat/xxx` → `main`）：
  - `host/architecture.md` 的「窗口壳」（zh 约 220–236 行，en 约 253 行起）：把 ⚠️ 那条限定为 Windows；补上各平台的外框做法，以及 §4 的依据。
  - `zh/host/交互与界面规格.md` / `en/host/interaction-and-ui-specs.md` 的 §2（约 70 行）和「窗口标题栏说明」（约 97 行）：补上 macOS 红绿灯和各平台的弹窗外框。
- `plan.md`：试点和推广各记一节，接在 §115 之后，编号以届时的最后一节为准。
- `feature-plan.md`：如果试点完成了而推广还没做，把推广写进去。
- PR：VelaShell 从 `dev` 合到 `main`；docs 的 PR 与它互相引用、一起合；描述写「主要改动 / 验证 / 文档」，不加 AI 署名。只有用户要求时才提交和建 PR。
