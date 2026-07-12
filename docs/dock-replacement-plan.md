# Dock.Avalonia 自研替换方案(VelaDock)

> 分支:`replacedock`。目标:用完全自研的轻量 Dock 实现替换 `Dock.Avalonia`
> (`Dock.Avalonia.Themes.Fluent` / `Dock.Model.ReactiveUI` 12.0.0.2),
> **不改变任何既有功能与 UI 交互逻辑**,并为后续 Avalonia 大版本升级消除第三方阻塞。

## 1. 现状分析:Dock.Avalonia 的实际使用面

经过全仓库排查,Dock 的集成面**高度集中**,且只用到其能力的一小部分
(单文档区、无浮动窗口、无工具面板、无 Pin、无布局序列化):

### 1.1 代码集成点

| 文件 | 用途 |
|---|---|
| `src/VelaShell/Docking/TerminalDockFactory.cs` | `Factory` 子类:单一 `DocumentDock` 根布局;`AddTerminal` / `RemoveTerminal`;`DocumentClosed` 事件 |
| `src/VelaShell/Docking/TerminalDocument.cs` | `Document` + `IDataTemplate` 包装 `TerminalTabViewModel`(`CanFloat=false`、`CanPin=false`) |
| `src/VelaShell/ViewModels/MainWindowViewModel.cs` | 持有 `Layout(IRootDock)`;订阅 `ActiveDockableChanged` / `FocusedDockableChanged` / `DocumentClosed`;创建/撤除标签时调 `AddTerminal` / `RemoveTerminal` |
| `src/VelaShell/Views/MainWindow.axaml` | `<dock:DockControl Layout="{Binding Layout}">` + `ControlRecycling`(切标签复用已建视图,流畅度关键)+ `TerminalDocument→TerminalTabView` 模板 |
| `src/VelaShell/Themes/DockStyles.axaml` | 重皮肤:标签条 36px 规格(nunbT)、Icon/Header/Close 模板(状态点、连接标识色条、资源监控悬浮、关闭钮);**同文件还有大量与 Dock 无关的全局样式(TextBox/ToolTip/ContextMenu/MenuFlyout/tab-nav)需保留** |
| `src/VelaShell/Themes/DockTabStrip.axaml(.cs)` | 覆写 Dock 标签条 ScrollViewer 主题:溢出时右端固定 24px 三连按钮(左滚/右滚/标签列表下拉) |
| `src/VelaShell/Themes/DockContextMenu.axaml` | 覆写标签右键菜单:关闭/其他/所有/左侧/右侧、水平/垂直拆分、标签位置(顶/左/右);移除浮动与 MDI |
| `src/VelaShell/App.axaml` | `DockFluentTheme`、Dock 度量资源(`DockFontSizeNormal` 等 8 项)、上述三个主题文件的挂载 |
| `src/VelaShell/Program.cs` + `Logging/FilteringLogSink.cs` | 专为过滤 Dock 的 `DockCapability` 绑定噪音而存在 |
| `src/VelaShell/ViewModels/SettingsViewModel.cs` | 关于页开源许可列表含 Dock.Avalonia 条目 |
| `src/VelaShell/Controls/ReparentingHost.cs` | 解决 Dock 分屏/拖拽时同一终端控件被二次实例化的双父级问题(自研后仍复用其思路) |

### 1.2 实际用到的运行时行为(= 必须复刻的交互契约)

1. **标签条**:顶置 35px+1px 分割线;激活标签 = tab-active 底 + 2px accent 顶线;
   状态点 7px、连接标识色条 3×12、标题悬停 400ms 浮出资源面板、11px 关闭钮。
2. **标签拖拽重排**(组内)与**跨组拖动**。
3. **拖到窗格边缘分屏**(水平/垂直,比例分割条可拖动)。
4. **右键菜单**:关闭 / 关闭其他 / 关闭所有 / 关闭左侧 / 关闭右侧、水平拆分、垂直拆分、
   标签位置(顶部/左侧/右侧)。
5. **溢出控件**:标签超宽时右端出现 左滚/右滚/全部标签下拉(激活项 accent 高亮)。
6. **激活/焦点跟踪**:点击标签或窗格 → `ActiveTerminalTab` / 状态栏 / SFTP 面板联动。
7. **关闭语义**:用户关标签 → `DocumentClosed` → 断 SSH/SFTP/日志;
   程序撤标签(连接失败)→ 静默移除,不触发关闭链。
8. **视图保活**:每个文档的 `TerminalTabView` 只构建一次,切换标签不重建
   (原由 `ControlRecycling` 提供;多标签切换流畅度的关键)。
9. **产品红线**:禁止浮动窗口、禁止 Pin、禁止 MDI、无“+”新建按钮(新建走 Ctrl+T/会话树)。
10. **空组折叠**:拆分出的组关掉最后一个标签后自动移除并提升兄弟节点;主组永不消失。

### 1.3 已知瑕疵(替换时一并修正,不属于行为变更)

- `Ctrl+Tab` / `Ctrl+W` 走 `TabBarViewModel`(逻辑标签集合),只改 `ActiveTerminalTab`,
  **没有反向同步到 Dock 的可见文档** —— 快捷键切标签时文档区不切换。
  自研后 `TabBar.ActiveTab → Workspace.ActivateDocument` 双向打通。

## 2. 方案:自研 VelaDock

### 2.1 设计原则

- **只做用到的**:文档式标签组 + 二叉/多叉分栏树 + 拖拽。不做浮动窗口、Pin、
  工具面板、布局序列化(未用到,留扩展点即可)。
- **模型与视觉分离**:纯 `INotifyPropertyChanged` 模型(可单测,无 Avalonia 依赖);
  控件层按模型渲染。不引入新框架、不依赖 ReactiveUI(App 层已有的继续用)。
- **视图保活内建**:工作区控件内部维护 `文档 → 视图` 缓存,天然替代
  `Dock.Controls.Recycling`,并沿用 `ReparentingHost` 的"抢占式收养"避免双父级。
- **API 兼容优先**:`MainWindowViewModel` 的改动控制在改名与类型替换级别。

### 2.2 代码布局(全部在 App 工程 `src/VelaShell/Docking/`)

```text
Docking/
  Model/
    DockElement.cs        INPC 基类
    DockDocument.cs       文档基类:Id / Title / CanClose;IDockViewProvider(CreateView)
    DockNode.cs           节点基类:Proportion / Parent
    DockGroup.cs          标签组:Documents / ActiveDocument / TabsPosition / IsPrimary
    DockSplit.cs          分栏:Orientation / Children
    DockWorkspace.cs      工作区:Root、全部结构操作、事件
    DockPosition.cs       枚举:Center / Left / Top / Right / Bottom
    DockTabsPosition.cs   枚举:Top / Left / Right
  TerminalDocument.cs     现有类改基:DockDocument + 视图工厂(不再依赖 Dock)
  TerminalWorkspace.cs    取代 TerminalDockFactory:AddTerminal/RemoveTerminal/DocumentClosed
  Controls/
    DockWorkspaceControl.cs  渲染分栏树(Grid+GridSplitter)、视图缓存、拖放覆盖层宿主
    DockGroupControl.cs      标签组:标签条(ItemsControl+ScrollViewer)+ 内容宿主
    DockTabItem.cs           单个标签(视觉 + 指针交互入口)
    DockDropOverlay.cs       拖放指示覆盖层(组中心/四边高亮)
    DockDragController.cs    拖拽状态机(重排/跨组/分屏/Esc 取消)
  Themes/(并入现有 Themes/DockStyles.axaml)
```

### 2.3 模型层语义

- `DockWorkspace.Root : DockNode` —— 初始为单个 `DockGroup`(`IsPrimary=true`)。
- **结构操作**(全部在模型层,可单测):
  - `AddDocument(doc)`:加入主组并激活(与 Dock 原行为一致:新终端总进第一组)。
  - `RemoveDocument(doc)`:静默移除(连接失败撤标签路径),空组折叠。
  - `CloseDocument(doc)`:尊重 `CanClose` → 移除 → 触发 `DocumentClosed`。
  - `CloseOthers/All/Left/Right(doc)`:逐个走 `CloseDocument`(保证 SSH 清理链完整)。
  - `SplitDocument(doc, orientation)`:在文档所属组旁新建组,文档移入(右/下侧,各 50%)。
    组内唯一文档时同样拆分(所有组行为一致,用户反馈),原组留空作为拖放目标。
  - `DockTo(doc, targetGroup, position)`:Center=并入(可指定序号),边=在目标组旁拆分;
    拖到自身组边缘且组内仅此一档 = 拆分语义(原组留空)。
  - 空组折叠:非主组因“文档移出/关闭”清空 → 从父分栏移除;分栏仅剩 1 子 → 子节点提升
    (比例继承);提升出的若是拆分留下的空次级组(兄弟已全部关闭)一并回收;
    根分栏收敛回单组。拆分留下的空组保留在原位,空面板显示“将标签拖到此处”提示。
- **激活语义**:每组有 `ActiveDocument`;工作区有全局 `ActiveDocument`
  (最后交互的组的激活文档),变更触发 `ActiveDocumentChanged`
  —— 对应原 `ActiveDockableChanged`+`FocusedDockableChanged` 双事件合一。
- `TabsPosition` 逐组生效(右键菜单"标签位置"作用于所属组,与 Dock 行为一致)。

### 2.4 控件层要点

- `DockWorkspaceControl`:监听模型树变化重建可视树 —— 分栏 = `Grid`
  (star 尺寸 ↔ `Proportion`,`GridSplitter` 拖完回写比例);组 = `DockGroupControl`。
  持有全局 `Dictionary<DockDocument, Control>` 视图缓存;文档关闭时移缓存。
- `DockGroupControl`:`DockPanel`,标签条按 `TabsPosition` 停靠(左右时纵排),
  内容区用 `ReparentingHost` 挂缓存视图。标签条 = `ScrollViewer(隐藏滚条)` +
  溢出三连钮(复用 `WidthOverflowConverter` 与 `tab-nav` 样式,滚动改为代码后置,
  去掉 Dock 的 `ScrollViewerLineCommand`/转换器)。
- 标签视觉直接内联现有规格(不再需要 Icon/Header/CloseTemplate 三段模板间接层):
  状态点、accent 色条、标题 + 资源悬浮、关闭钮,样式经 class 由
  `DockStyles.axaml` 重写后的选择器控制,视觉零变化。
- 右键菜单在 `DockTabItem` 上重建,命令直连 `DockWorkspace` 操作,菜单项不变。

### 2.5 拖拽交互(复刻 Dock 手感)

1. 标签上按下 → 记录;位移超过 4px → 进入拖拽(捕获指针)。
2. 指针仍在**本组标签条内**:按各标签中线计算插入位,实时重排(浏览器式)。
3. 指针离开标签条:显示覆盖层 —— 命中某组时高亮其 中心/上/下/左/右 五区
   (中心=并入该组,四边=在该组该侧拆分 50%);未命中任何有效区则松手无操作。
4. `Esc` 取消并还原;禁止拖出窗口(无浮动)。

### 2.6 移除清单(替换完成后)

- `VelaShell.csproj`:删 `Dock.Avalonia.Themes.Fluent`、`Dock.Model.ReactiveUI`。
- `App.axaml`:删 `DockFluentTheme`、8 项 Dock 度量资源、`DockContextMenu.axaml`
  与 `DockTabStripResources` 挂载;度量以常量/样式内化。
- 删除文件:`Themes/DockTabStrip.axaml(.cs)`、`Themes/DockContextMenu.axaml`、
  `Logging/FilteringLogSink.cs`(及 `Program.cs` 挂钩)、`Docking/TerminalDockFactory.cs`。
- `Themes/DockStyles.axaml`:`dc|*` 选择器改为自研控件选择器;无关全局样式原样保留。
- `SettingsViewModel`:删 Dock.Avalonia 许可条目。
- `docs/架构设计.md` / `docs/architecture.md`:更新 Dock 描述。

## 3. 实施步骤

| 步骤 | 内容 | 验证 |
|---|---|---|
| 1 | 模型层 + 单元测试(结构操作、激活、关闭语义、折叠提升) | `dotnet test` |
| 2 | 视觉控件(标签条/内容/分栏/溢出/右键菜单),静态接入可跑 | 构建 + 启动目测 |
| 3 | 拖拽控制器(重排/跨组/分屏/取消) | 手工交互验证 |
| 4 | 接入 `MainWindowViewModel`/`MainWindow.axaml`,补 TabBar 双向同步 | 全功能回归 |
| 5 | 依赖与残留移除(§2.6),文档更新 | 全仓 `Dock.` 零引用、构建绿 |

## 4. 风险与对策

- **终端控件双父级崩溃**(历史踩坑):所有内容宿主统一走 `ReparentingHost`;
  视图缓存保证单实例。
- **切换流畅度回退**:视图保活由工作区直接持有,路径比 ControlRecycling 更短;
  验证时专门多标签快速切换测试。
- **拖拽手感差异**:按 §2.5 状态机复刻;边缘分屏固定 50% 与 Dock 默认一致。
- **右键菜单命令语义**:关闭系列必须逐文档触发 `DocumentClosed`
  (SSH/SFTP/日志清理链依赖它),单测覆盖。
- **回退方案**:整个替换在 `replacedock` 分支;任何阶段可整体回退 `main`。
