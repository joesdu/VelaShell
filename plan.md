# PulseTerm 项目进展与参考文档

> 本文件记录已完成的工作、当前架构、关键文件索引与后续待办,供后续开发参考。
> 最近更新:2026-07-09(设置项全量接线完成,见 §10;未实现项与终端工具功能缺口分析,见 §10.A/§12)。

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

## 10. 后续待办 / 已知问题(2026-07-09 复盘)

**A. 设置项接线状态(2026-07-09 全量排查后)**

✅ **已完成接线**(本轮实现,详见各消费点):终端行为全套(光标样式/闪烁、行高、选中即复制、右键粘贴、复制去尾空格、双击选词、多行粘贴确认、Ctrl+C 复制、滚动行为、Bell 三模式+标签闪烁、IME 开关)、外观(终端四色+ANSI16 稀疏覆盖、窗口透明度、菜单栏显隐、侧边栏位置、启动窗口状态、UI 字体/字号)、常规(默认端口、连接超时/心跳、自动重连+间隔+重试、关闭前确认、断开提醒+声音、开机自启、托盘、恢复会话、会话日志+保留清理、全局记住密码)、文件传输(远程初始目录、下载目录、显示隐藏文件、最大并发、下载冲突策略、保留时间戳、完成通知、带宽限速、传输日志+保留清理)、安全(首次指纹人工确认、指纹变更阻断/人工裁决、告警通道应用内+Webhook+审计)、密钥(默认认证密钥)。
关键接线点:`MainWindowViewModel.ApplyLiveTerminalSettings` / `MainWindow.ApplyWindowAppearance+OnClosing` / `InfrastructureServiceCollectionExtensions`(超时/心跳/指纹策略)/ `SftpService`(带宽/时间戳)/ `FileBrowserViewModel.TransferOptions`。
默认值调整:LineHeight 1.2→1.0、ScrollOnOutput true→false、CopyOnSelect false→true、RemoteInitialPath "/home/user"→""(空=家目录)。

⏳ **仍未实现(UI 已禁用并标注"规划中/暂不支持",后期按情况实现)**:

| 项 | 设置位置 | 未实现原因 / 实现思路 |
|----|---------|---------------------|
| 连字 Ligatures | 终端 | 自绘渲染器按单元格逐字排版,跨字符连字需引入整行 shaping(与网格对齐冲突,成本高) |
| 标签栏位置(顶部/底部) | 外观 | 标签条由 Dock.Avalonia 的 DocumentDock 模板提供,需覆写其主题模板才能移到底部 |
| 自适应标题栏颜色 | 外观 | 使用系统原生标题栏,颜色由 OS 随主题托管,应用层无稳定控制手段 |
| 启动时检查更新 / 更新频道 / 自动下载 | 常规 | Velopack `UpdateService` 未接入,且无发布 feed URL;接入后三项一并实现(About 页"检查更新"目前也是占位) |
| 系统通知(Toast) | 常规+安全审计 | Windows 原生 Toast 需 AppUserModelID/通知框架;当前用状态栏+提示音替代 |
| 主密码保护 | 常规 | 需主密码派生密钥替换 `AesSecretProtector` 的本机密钥文件 + 启动解锁弹窗 + 密文迁移,安全敏感需单独设计 |
| 断点续传 / 自动续传 / 传输重试 / 临时文件清理 | 文件传输 | 需 `.part` 临时文件 + SFTP offset 续写(SSH.NET 支持 DownloadFile offset 有限,需改用流式 seek)+ 传输队列持久化 |
| 会话录制 / 输入脱敏 | 安全审计 | 录制功能本身未实现;可基于已完成的会话日志(SshTerminalBridge.DataReceived)扩展为带时间戳的 asciinema 格式 + 回放器 |
| 自动加载密钥到 Agent | 密钥管理 | 需集成 Windows OpenSSH ssh-agent(named pipe 协议)或 Pageant |
| 上传方向冲突策略 | 文件传输 | 目前只对下载(本地已存在)生效;上传需先 SFTP stat 远端同名文件再走询问/跳过/重命名 |

**B. 功能缺口**
- 非 SSH 协议:连接弹窗 SFTP/Telnet/串口 标签禁用;第 2 步"证书"认证禁用。
- 快捷键自定义未实现(只读展示);**展示表来自设计稿,与 `KeyboardShortcutService` 实际绑定未逐条核对**(如 Ctrl+L 清屏 / Ctrl+D 断开 / 分屏键等未必真实存在)——需对齐或标注。
- 密钥生成仅 RSA(PEM+OpenSSH 公钥);ed25519 生成缺失(.NET 无内置 OpenSSH ed25519 私钥导出,需评估 SSH.NET 或 BouncyCastle);导入不校验私钥有效性;删除无二次确认。
- 审计日志已在写(connect/connect-failed),但**无查看界面**;`audit_log`/`conn_history` 无保留策略(retention),长期运行会累积。
- 配置导出目前仅 AppSettings,不含连接/分组(界面文案写的是"所有连接和设置")——待扩展为全量导出。
- 命令面板"会话"类目仅最近连接,不含全部已保存会话。
- 关于页"检查更新"为占位(直接显示"已是最新版本"),Velopack `UpdateService` 未接;"更新日志"禁用。
- 会话录制与回放、系统资源监控面板、连接诊断中心、运维编排中心、主机信任中心、文件传输 toast 等设计稿面板仍未组件化(遗留自上轮)。

**C. 技术债 / 小瑕疵(2026-07-09 处理完毕,余项见末尾)**
- ✅ QuickConnect 组件已删除(View/VM/SidebarViewModel 引用与测试同步清理;Strings 资源保留供本地化测试)。
- ✅ SonnetDB 锁粒度:**决定保留全局信号量**——文档集合与时序共享同一 Tsdb 实例(同一 WAL/存储引擎),SonnetDB 未承诺内部线程安全,按集合分锁有并发损坏风险。实际瓶颈是设置读热点(每次连接/每个传输文件都读一次),已在 `SonnetDbSettingsService` 加 **settings JSON 缓存**(缓存序列化文本、按次反序列化,调用方语义不变),读路径不再进锁/碰盘。
- ✅ 硬编码 `#0A0E14` 前景已抽成 `PulseAccentForeground` 令牌(暗=深字/亮=浅字;亮色 accent #644AC9 深底配深字对比不足的问题即此);用户自定义强调色时 `App.ApplyAccent` 按亮度自动配对前景。AboutPage 固定青色渐变上的图标有意保留硬编码。亮色主题整体仍建议实机走查一遍。
- ✅ `Ctrl+T` 改为打开新建连接(与 Ctrl+N 一致;旧绑定往已不显示的 TabBar 塞空标签)。Dock 布局持久化**缓做**:文档即活动 SSH 会话,单独恢复布局无意义;需与"恢复会话"联动(布局节点 ↔ profile 映射,Dock.Avalonia DockSerializer + 自定义 document 还原器),列为后续特性。
- ✅ OSC 52(远端写剪贴板,tmux/vim yank;只支持写方向,查询"?"一律不应答防剪贴板泄露;1MB 上限)与 DECRQSS(应答 SGR "m" 与 DECSTBM "r",其余回 `DCS 0 $ r`)已实现,含单测。**顺带修复解析器预存在 bug:全局"ESC 重启序列"会把 ST(ESC \)结尾的 OSC/DCS 整段丢弃**(BEL 结尾才能用),现已在 ESC 分支先分发在途载荷,补了回归测试。
- ✅ 运行时热切终端类型:**按设计不做**——TERM 在连接时向远端协商,活动会话热切只会造成本地仿真与远端 TERM 能力档不一致;设置修改对新连接生效即为正确语义。CJK 回退:字体链(Cascadia Mono→JetBrains Mono→Consolas→Microsoft YaHei→monospace)+ 渲染器逐格 FormattedText 回退路径已覆盖双宽字形,视为已解决。
- ⏳ sixel 图形仍挂起(多日工作量,依赖需求评估)。
- ⏳ 需实机确认(代码层无法验证):三个自绘对话框(连接/验证/设置,SystemDecorations=None)的拖动与阴影;亮色主题下各对话框观感;命令面板圆角修复效果。注:plan 旧文提到的主窗 `WindowDecorations="None"` 已不存在——主窗现用原生标题栏(设计 §2),该条仅余对话框部分。

## 11. 设计稿分析已记录的问题(供实现时对照)

- 设置-终端 缺终端类型/编码选择器(已在代码补上)。
- term-* 只定义 8 个 ANSI 色,无 bright/256(引擎侧已补全)。
- 未指定 CJK/双宽回退字体。
- 终端交互(光标样式、选区色、终端内搜索、分屏)设计未建模。
- 亮色主题 `bg-terminal=#1E1E2E` 仍为深色(疑似有意)。
- Logo 有一个 `enabled:false` 残留图标;文件列表"修改时间"列无固定宽度。

## 12. 与主流终端工具的功能缺口(2026-07-09 对照 Xshell / MobaXterm / Tabby / WindTerm 分析)

> §10.B 已列的缺口(Telnet/串口/证书认证、快捷键自定义、ed25519 生成、审计查看界面、全量配置导出、更新检查等)不在此重复。以下为本次新识别的缺口,按优先级排列。

**P1 —— 日常使用高频,建议优先**
1. **本地终端标签**:PowerShell / CMD / WSL / Git Bash 作为标签页打开(主流工具标配)。VT 引擎与渲染控件是现成的,缺一个 ConPTY 传输层(`IShellStreamWrapper` 的本地实现,Windows 侧走 `CreatePseudoConsole`)。
2. **SSH 跳板机(ProxyJump)**:经堡垒机二段/多段连接。连接配置加"跳板主机"字段,SSH.NET 用第一跳的转发端口做第二跳 socket。
3. **保存的会话全部进命令面板**:目前"会话"类目只有最近连接,应并入 `session_profiles` 全量(带分组标签),这是命令面板作为中枢的关键。
4. **导出终端缓冲区**:右键/命令"保存输出到文件"(scrollback 全量或选区),运维取证常用;`TerminalScreen` 已能逐行读文本,只差落盘入口。
5. **配色方案预设**:内置 Dracula/Alucard 之外的常见方案(Solarized/Nord/Gruvbox/One Dark…)一键切换,当前只能逐色手改;`TerminalPaletteOverrides` 机制已具备,补预设清单+下拉即可。
6. **克隆会话/复制标签**:同一配置一键再开一个标签(Xshell 的"复制会话"),等价于对当前 Profile 再走一次 ConnectProfileAsync,成本极低。

**P2 —— 进阶运维能力**
7. **多会话同步输入**(send to all / 命令多发):对选中的多个标签广播键入,集群运维刚需;在 `UserInput` 分发处加广播开关即可。
8. **ZMODEM(rz/sz)**:终端内直接收发文件,Xshell/SecureCRT 标配;需在 `SshTerminalBridge` 流上识别 ZMODEM 起始序列并接管通道(可评估 trzsz 协议替代,实现更简单)。
9. **SSH config 导入**:解析 `~/.ssh/config`(Host/HostName/Port/User/IdentityFile/ProxyJump)批量导入会话,降低迁移成本。
10. **连接代理**:SOCKS5/HTTP 代理经由连接(公司内网出网场景);SSH.NET `ConnectionInfo` 原生支持 ProxyTypes。
11. **防空闲断开(Anti-idle)**:按间隔发送自定义串(如 `\0` 或空格),与已实现的 SSH keepalive 互补(keepalive 防 NAT 超时,anti-idle 防服务端 shell 超时踢出)。
12. **known_hosts 管理界面**(设计稿"主机信任中心"):列出/删除/导出已信任指纹;`IHostKeyService` CRUD 已齐,只缺 UI 页(可挂设置-安全审计)。
13. **会话标签自定义颜色/图标**:多环境(生产红/测试绿)一眼区分;SessionProfile 加 color 字段 + 标签条着色。

**P3 —— 锦上添花**
14. **SFTP 本地/远程双栏**:目前只有远程单栏,MobaXterm/WinSCP 式左右双栏拖拽互传体验更好(升级现有 FileBrowser 布局)。
15. **用户自定义关键字高亮规则**:语义高亮已内置(URL/IP/错误词),开放用户正则+颜色规则表(WindTerm 卖点)。
16. **命令自动补全/历史建议**:输入时基于本地命令历史悬浮建议(WindTerm 式);已有 quick_commands 可作为数据源之一。
17. **OSC 52 剪贴板**:远端 vim/tmux 写系统剪贴板(§8 遗留,一并归档到此)。
18. **触发器/自动应答**:输出匹配正则时自动发送响应(expect 式),如自动 yes/密码带外输入。
19. **多窗口**:新开独立主窗口(目前只有单窗口+分屏/浮动 Dock)。
20. **Mosh / SSH 证书(certificate)认证**:弱网漫游与企业 CA 场景,按需求评估。
