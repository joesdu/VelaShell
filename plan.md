# PulseTerm 项目进展与参考文档

> 本文件记录已完成的工作、当前架构、关键文件索引与后续待办,供后续开发参考。
> 最近更新:2026-07-08(SonnetDB 持久化 / 连接与认证流程 / 设置窗口九页,见 §9-§10)。

## 1. 技术栈现状

| 项 | 版本/说明 |
|----|-----------|
| .NET | net10.0 |
| UI 框架 | **Avalonia 12.0.5**(已从 11.x 升级) |
| MVVM | ReactiveUI 23.2.28 / ReactiveUI.Avalonia 12.0.3 |
| 停靠框架 | Dock.Avalonia 12.0.0.2(+ Themes.Fluent、Model.ReactiveUI) |
| SSH/SFTP | SSH.NET 2025.1.0 |
| 持久化 | **SonnetDB.Core 3.0.0 嵌入式多模型数据库**(`%LocalAppData%/PulseTerm/sonnetdb`;文档集合 + 时序 measurement;旧 JSON 首次运行一次性导入;LiteDB 已移除) |
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
- **连接持久化**:`ConnectionWorkflowService.SaveProfileAsync`→`SonnetDbSessionRepository`(SonnetDB `session_profiles` 集合,密码 AES-256 加密);`MainWindowViewModel.InitializeAsync` 启动时加载侧栏"最近连接"(SonnetDB `conn_history` 时序)与会话树;侧栏最近项**双击重连**;命令面板也可连。
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

- 6 个测试项目:`Controls.Tests`(1)、`Infrastructure.Tests`(19)、`Presentation.Tests`(23)、`Terminal.Tests`(107)、`Core.Tests`(136)、`App.Tests`(228)。**合计 514 通过 / 0 失败**(2026-07-08)。
- 已移除 `xunit`/`xunit.v3`/`FluentAssertions`/`Avalonia.Headless.XUnit`;改用 `MSTest.TestFramework`+`MSTest.TestAdapter` 3.11.1,全局 `using Microsoft.VisualStudio.TestTools.UnitTesting`。
- 转换约定(供新增测试参考):`[Fact]`→`[TestMethod]`;`[Theory]`+`[InlineData]`→`[DataTestMethod]`+`[DataRow]`;`[Trait("Category","X")]`→`[TestCategory("X")]`;每类 `[TestClass]`;`ITestOutputHelper`→`public TestContext TestContext {get;set;}`;`IAsyncLifetime`→`[TestInitialize]`/`[TestCleanup]`。
- 断言:MSTest `Assert.AreEqual(EXPECTED, ACTUAL)`(期望在前);异常用 `Assert.ThrowsExactly`/`Assert.ThrowsExactlyAsync`;字符串用 `StringAssert`;序列用 `CollectionAssert`。
- 注意点:`long`/`uint` 期望值要带后缀(`AreEqual(object,object)` 类型严格);`bool?` 用 `x == true`;非记录类型对象等价用 JSON 序列化比较。
- 测试**不渲染** Avalonia(控件只 `new`),故无需 headless 包;`App.Tests/ModuleInit.cs` 用 `[ModuleInitializer]` 初始化 ReactiveUI 调度器,保留。
- 集成测试(`SshIntegrationTests` 需 Docker+SSH 服务器、`CrossPlatformPublishTests` 需 `PULSETERM_PUBLISH_TESTS=1`)按环境早退跳过。

## 8. 关键约定 / 已知坑

- 构建/测试用根目录 `PulseTerm.slnx`。运行 App 后 DLL 被占用会导致构建报"文件被锁定"——先停掉运行实例。
- Bash 工具用 Git Bash;不要用 `Read`/`Grep` 直接读 `.pen`(加密,只能走 pencil MCP)。
- 记忆索引见 `C:\Users\Joe\.claude\projects\G--PulseTerm\memory\`(terminal-engine、docking、sonnetdb-storage、connect-flow)。
- SonnetDB 要点:`Tsdb.Open(new TsdbOptions{RootDirectory})`;文档 `db.Documents.Open(name)` 的 Upsert/Get/Scan/Delete;时序 `db.Write(Point.Create(...))` + `SqlExecutor.Execute` SELECT;`FieldType` 在 `SonnetDB.Storage.Format`(是 `Int64` 不是 `Long`);**时序 tag 值不允许空串**(临时连接不写 profile_id);仓储加密必须写副本、不可原地改传入的 profile(内存明文用于活动连接)。

## 9. 2026-07-08 完成情况(6 次提交,514 测试全绿)

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

## 10. 后续待办 / 已知问题(2026-07-08 复盘)

**A. 设置项"仅持久化未消费"(需逐项接线到运行时)**
- 终端:行高 / 连字 / 光标样式与闪烁 / Bell 模式 / 滚动行为 / 选中即复制、右键粘贴等复制粘贴项 / 多行粘贴确认 / IME 开关 / Ctrl+C 行为。
- 外观:界面字体与字号 / 窗口透明度 / 标签栏与侧边栏位置 / 菜单栏显隐 / 启动窗口状态 / 终端四色与 ANSI 16 色(引擎有 `TerminalPalette`,尚未从设置读取)。
- 常规:开机自启 / 恢复会话 / 托盘 / 连接超时与心跳(SSH 侧当前硬编码 10s)/ 会话日志 / 更新频道与自动下载 / 关闭前确认 / 自动重连 / 系统通知与声音 / 主密码保护。
- 文件传输:全部选项(默认路径 / 并发 / 冲突策略 / 带宽 / 日志 / 断点续传)未接 SFTP 服务。
- 安全审计:录制与脱敏开关(录制功能本身未实现);`ConfirmFirstFingerprint` 已存但流程仍为 TOFU 自动信任(`HostKeyPromptView` 存在未使用,可接成人工确认弹窗);告警通道(应用内/系统/Webhook)未发送。

**B. 功能缺口**
- 非 SSH 协议:连接弹窗 SFTP/Telnet/串口 标签禁用;第 2 步"证书"认证禁用。
- 快捷键自定义未实现(只读展示);**展示表来自设计稿,与 `KeyboardShortcutService` 实际绑定未逐条核对**(如 Ctrl+L 清屏 / Ctrl+D 断开 / 分屏键等未必真实存在)——需对齐或标注。
- 密钥生成仅 RSA(PEM+OpenSSH 公钥);ed25519 生成缺失(.NET 无内置 OpenSSH ed25519 私钥导出,需评估 SSH.NET 或 BouncyCastle);导入不校验私钥有效性;删除无二次确认。
- 审计日志已在写(connect/connect-failed),但**无查看界面**;`audit_log`/`conn_history` 无保留策略(retention),长期运行会累积。
- 配置导出目前仅 AppSettings,不含连接/分组(界面文案写的是"所有连接和设置")——待扩展为全量导出。
- 命令面板"会话"类目仅最近连接,不含全部已保存会话。
- 关于页"检查更新"为占位(直接显示"已是最新版本"),Velopack `UpdateService` 未接;"更新日志"禁用。
- 会话录制与回放、系统资源监控面板、连接诊断中心、运维编排中心、主机信任中心、文件传输 toast 等设计稿面板仍未组件化(遗留自上轮)。

**C. 技术债 / 小瑕疵**
- `QuickConnectViewModel` / `QuickConnectView` 已随输入框移除而闲置,可删(注意 `SidebarViewModel.QuickConnect` 与 `QuickConnectCommand` 引用)。
- SonnetDB 引擎所有操作走单信号量串行,数据量大时的吞吐待观察;必要时按集合分锁。
- 新对话框(连接/验证/设置)在**亮色主题**下未专项走查(个别 `#0A0E14` 前景为硬编码,亮色下按钮文字对比度需确认)。
- Dock 布局持久化仍未做;`Ctrl+T` 只加空 TabBar 标签的旧瑕疵仍在。
- sixel / DECRQSS / OSC 52 / 运行时热切终端类型 / CJK 回退字体:遗留自上轮,未动。
- `WindowDecorations="None"` 下主窗边缘缩放需实机确认;新增的三个自绘对话框(SystemDecorations=None)同样需实机核对拖动与阴影。

## 11. 设计稿分析已记录的问题(供实现时对照)

- 设置-终端 缺终端类型/编码选择器(已在代码补上)。
- term-* 只定义 8 个 ANSI 色,无 bright/256(引擎侧已补全)。
- 未指定 CJK/双宽回退字体。
- 终端交互(光标样式、选区色、终端内搜索、分屏)设计未建模。
- 亮色主题 `bg-terminal=#1E1E2E` 仍为深色(疑似有意)。
- Logo 有一个 `enabled:false` 残留图标;文件列表"修改时间"列无固定宽度。
