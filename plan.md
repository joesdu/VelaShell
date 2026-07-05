# PulseTerm 项目进展与参考文档

> 本文件记录已完成的工作、当前架构、关键文件索引与后续待办,供后续开发参考。
> 最近更新:2026-07-06。

## 1. 技术栈现状

| 项 | 版本/说明 |
|----|-----------|
| .NET | net10.0 |
| UI 框架 | **Avalonia 12.0.5**(已从 11.x 升级) |
| MVVM | ReactiveUI 23.2.28 / ReactiveUI.Avalonia 12.0.3 |
| 停靠框架 | Dock.Avalonia 12.0.0.2(+ Themes.Fluent、Model.ReactiveUI) |
| SSH/SFTP | SSH.NET 2025.1.0 |
| 持久化 | JSON(`~/.pulseterm/*.json`),LiteDB 预留未启用 |
| 打包 | Velopack 1.2.0 |
| 测试 | **MSTest 3.11.1**(已从 xUnit 全量迁移;FluentAssertions 已移除) |

## 2. 解决方案分层

```
src/
├── PulseTerm.App/            桌面入口、DI 组合根、视图(axaml)、App 层 ViewModel、停靠、行为
├── PulseTerm.Presentation/   跨层 ViewModel、连接/隧道工作流服务
├── PulseTerm.Controls/       自定义控件与设计 token(PulseTokens/PulseShellTokens/Generic)
├── PulseTerm.Terminal/       ★ 自研 VT 终端引擎 + 自绘渲染控件
├── PulseTerm.Core/           领域模型、抽象契约、数据存储、SSH/SFTP 封装接口、本地化
└── PulseTerm.Infrastructure/ SSH.NET/SFTP/隧道实现、存储路径、DI 扩展
tests/  6 个 MSTest 项目(见 §7)
解决方案文件:仓库根目录 PulseTerm.slnx(注意:曾在 src/ 下,VS 打开后移到了根目录)
```

## 3. 自研终端引擎(核心,替换了坏掉的 AvaloniaTerminal)

彻底移除第三方 `AvaloniaTerminal 1.0.0-alpha.7`,改为手写 VT 引擎。位于 `src/PulseTerm.Terminal/Emulation/` 与 `Rendering/`:

- `VtParser.cs` — Paul Williams DEC ANSI 状态机(Ground/Escape/CSI/OSC/DCS…)+ 独立 VT52 语法路径;消费 Unicode 标量,派发到 `IVtActions`。
- `TerminalScreen.cs` + `TerminalRow/TerminalCell/CellFlags/TerminalColor` — 网格、主/备屏、滚动区域(DECSTBM)、scrollback、光标、tab stops。
- `TerminalEmulator.cs` — 仿真器大脑(实现 `IVtActions`):SGR(16/256/truecolor)、光标/擦除/插删行列、模式(DECAWM/DECOM/应用键盘/插入/括号粘贴/鼠标跟踪…)、DEC 线绘字符集、DA/DSR 应答、备用屏。
- `TerminalType.cs` — **vt52/100/102/220/320/340/420/520/xterm/xterm-256color** 十种 profile,各自 TERM 名 + Device Attributes 应答;`FromTermName`/`ToTermName`;**xterm-256color 为默认**。
- `Utf8Sink.cs` — 增量解码,**可配置任意编码**(UTF-8 默认,GBK/Big5 等);`CharWidth.cs` — wcwidth(CJK 双宽);`TerminalPalette.cs` — 256 色 + 设计稿 term-* 配色;`Charsets.cs` — DEC 线绘映射;`InputEncoder.cs` — 按键→字节(应用光标键、xterm 修饰键、VT52)。
- `Rendering/PulseTerminalControl.cs` — 纯自绘 Avalonia `Control`:glyph 渲染、光标、选区、滚轮回溯、剪贴板(含括号粘贴);**同时实现旧 `ITerminalEmulator` 接口**以无缝接回 `SshTerminalBridge` 与视图。默认网格 120×32;`ApplyLayoutSize` 拒绝 <2 列/行的早期布局(修过"横幅每字一行"bug)。

## 4. SSH / PTY

- `SshTerminalBridge` 只读循环,**不再向 shell 预写 `\n`**(修过"末行提示符重复"bug)。
- **PTY 实时改窗**:`IShellStreamWrapper.Resize` → `ShellStream.ChangeWindowSize`;`ITerminalEmulator.PtySizeChanged(cols,rows)` 由控件布局时抛出,`TerminalTabViewModel` 后台线程转发给 PTY。
- **连接失败不崩溃**:`MainWindowViewModel.TryConnectProfileAsync` 捕获认证/网络/超时异常,映射中文提示写入状态栏 + `LastConnectionError`;交互式连接失败弹错误对话框。`Program.cs` 装了 `TaskScheduler.UnobservedTaskException`/`AppDomain.UnhandledException` 兜底。
- **连接持久化**:保存本就写盘(`ConnectionWorkflowService.SaveProfileAsync`→`SessionRepository`→`~/.pulseterm/sessions.json`);新增 `MainWindowViewModel.InitializeAsync` 启动时加载到侧栏"最近连接";侧栏最近项**双击重连**;命令面板也可连。
- **新建连接密码框仅限 ASCII**:`Behaviors/AsciiOnlyInput.cs` 拦截 IME/中文 TextInput + VM setter 剥离粘贴的非 ASCII。

## 5. 停靠 / 分屏(Dock.Avalonia)

- `Docking/TerminalDocument.cs`(包装 `TerminalTabViewModel`)、`Docking/TerminalDockFactory.cs`(RootDock→DocumentDock,`AddTerminal`/`OnDockableClosed`)。
- `MainWindow.axaml` 用 `<dock:DockControl Layout="{Binding Layout}">` 承载,`TerminalDocument`→`TerminalTabView` 模板。标签可拖动重排、**撕出成浮动窗口**、**边缘拖放二分/四分分屏**。
- `Controls/ReparentingHost.cs` — 解决分屏/撕下时"同一控件被两个 ContentPresenter 领养"崩溃:自绘终端控件是共享单实例,该 Decorator 在挂载时先从旧父级摘除再领养,保证任一时刻只有一个父级。
- `App.axaml` 挂 `DockFluentTheme` + `Themes/DockStyles.axaml`(token 覆盖)。
- `Logging/FilteringLogSink.cs` — 过滤 Dock 的无害 `DockCapability … Value is null` 绑定诊断噪音(仅丢弃 Binding 区域含该标记的消息,其余透传)。

## 6. UI / 视图与设置

- **状态栏跟随激活 Tab**:每个 `TerminalTabViewModel` 携带 `ConnectionSummary/TerminalTypeName/EncodingName`;`UpdateStatusBarForActiveTab` 投影连接串/状态/类型/编码/尺寸/延迟;订阅 `ActiveTerminalTab` 变化 + Dock `ActiveDockableChanged`/`FocusedDockableChanged` → 切换标签/窗格实时更新左下角。
- **自绘窗口壳**:Avalonia 12 用 `WindowDecorations="None"`(替代已移除的 `ExtendClientAreaChromeHints`)+ `ExtendClientAreaToDecorationsHint`;`MainWindow.axaml.cs` 自绘可拖动标题栏(`BeginMoveDrag`)+ 最小化/最大化/关闭;根容器 `Margin` 绑 `OffScreenMargin` 防最大化裁切。
- **命令面板(Ctrl+P / Ctrl+K)**:`ViewModels/CommandPaletteItem.cs`(+Group)、`CommandPaletteViewModel.cs`(模糊子序列搜索、分类分组、上下循环导航、执行/关闭)、`Views/CommandPaletteView.axaml(.cs)`;`MainWindow` 半透明遮罩浮层,条目=最近会话(Enter 连接)+ 全局命令。
- **终端类型/编码设置项**:`AppSettings.TerminalType`(默认 xterm-256color)/`TerminalEncoding`(默认 UTF-8);`SettingsViewModel`/`SettingsView` 两个下拉;`Program.cs` 注册 `CodePagesEncodingProvider`(GBK/Big5);连接时 `MainWindowViewModel.ConfigureTerminal` 应用到 PTY 的 TERM 与控件。`ISettingsService`/`JsonDataStore` 已入 DI。
- 快捷命令面板、隧道管理面板此前已有完整 View+VM。

## 7. 测试(已全量迁移到 MSTest)

- 6 个测试项目:`Controls.Tests`(1)、`Infrastructure.Tests`(1)、`Presentation.Tests`(13)、`Terminal.Tests`(58)、`Core.Tests`(116)、`App.Tests`(170)。**合计 359 通过 / 0 失败**。
- 已移除 `xunit`/`xunit.v3`/`FluentAssertions`/`Avalonia.Headless.XUnit`;改用 `MSTest.TestFramework`+`MSTest.TestAdapter` 3.11.1,全局 `using Microsoft.VisualStudio.TestTools.UnitTesting`。
- 转换约定(供新增测试参考):`[Fact]`→`[TestMethod]`;`[Theory]`+`[InlineData]`→`[DataTestMethod]`+`[DataRow]`;`[Trait("Category","X")]`→`[TestCategory("X")]`;每类 `[TestClass]`;`ITestOutputHelper`→`public TestContext TestContext {get;set;}`;`IAsyncLifetime`→`[TestInitialize]`/`[TestCleanup]`。
- 断言:MSTest `Assert.AreEqual(EXPECTED, ACTUAL)`(期望在前);异常用 `Assert.ThrowsExactly`/`Assert.ThrowsExactlyAsync`;字符串用 `StringAssert`;序列用 `CollectionAssert`。
- 注意点:`long`/`uint` 期望值要带后缀(`AreEqual(object,object)` 类型严格);`bool?` 用 `x == true`;非记录类型对象等价用 JSON 序列化比较。
- 测试**不渲染** Avalonia(控件只 `new`),故无需 headless 包;`App.Tests/ModuleInit.cs` 用 `[ModuleInitializer]` 初始化 ReactiveUI 调度器,保留。
- 集成测试(`SshIntegrationTests` 需 Docker+SSH 服务器、`CrossPlatformPublishTests` 需 `PULSETERM_PUBLISH_TESTS=1`)按环境早退跳过。

## 8. 关键约定 / 已知坑

- 构建/测试用根目录 `PulseTerm.slnx`。运行 App 后 DLL 被占用会导致构建报"文件被锁定"——先停掉运行实例。
- Bash 工具用 Git Bash;不要用 `Read`/`Grep` 直接读 `.pen`(加密,只能走 pencil MCP)。
- 记忆索引见 `C:\Users\Joe\.claude\projects\G--PulseTerm\memory\`(terminal-engine、docking)。

## 9. 后续待办(未完成)

**优先(设置子页补全)** —— 设计稿里这些设置分区尚未实现视图:外观 / 快捷键 / 文件传输 / 关于 / 安全审计 / 密钥管理 / 代码片段。

**设计稿其余面板组件化**:系统资源监控、连接诊断中心、运维编排中心(多主机批量执行)、主机信任中心、会话录制与回放、文件传输提示 toast、会话右键菜单。

**终端/体验增强**:
- 分屏 Dock 的设计 token 精细化样式(当前 `DockStyles.axaml` 只覆盖了主要面。)
- 布局持久化(Dock 布局保存/恢复);`Ctrl+T` 目前只加空 TabBar 标签、无 dock 文档(小瑕疵)。
- sixel / DECRQSS / OSC 52 剪贴板;运行时热切终端类型(当前仅连接时生效)。
- CJK 内嵌字体(设计用 JetBrains Mono,未指定中文回退字体)。

**安全**:已保存连接(含密码)当前**明文**存于 `sessions.json`,建议加本地加密(DPAPI/AES)。会话树(设计的分组保存归宿)尚未接线,当前落在"最近连接"(上限 10)。

**窗口**:`WindowDecorations="None"` 下边缘缩放需实机确认;若不可用改 `BorderOnly`。

## 10. 设计稿分析已记录的问题(供实现时对照)

- 设置-终端 缺终端类型/编码选择器(已在代码补上)。
- term-* 只定义 8 个 ANSI 色,无 bright/256(引擎侧已补全)。
- 未指定 CJK/双宽回退字体。
- 终端交互(光标样式、选区色、终端内搜索、分屏)设计未建模。
- 亮色主题 `bg-terminal=#1E1E2E` 仍为深色(疑似有意)。
- Logo 有一个 `enabled:false` 残留图标;文件列表"修改时间"列无固定宽度。
