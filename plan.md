# VelaShell 项目进展与参考文档

> 本文件记录已完成的工作、当前架构、关键文件索引与后续待办,供后续开发参考。
> 最近更新:2026-07-12(§13:设置审计整改三批完成;新特性 —— 主机指纹三选项确认与已信任主机管理、GitHub Gist 云同步、会话录制与回放、支持与捐赠页、双许可 AGPL-3.0 + 商业授权、终端配色随主题联动;测试计数见 §7,含两处已知非回归失败)。

## 1. 技术栈现状

| 项       | 版本/说明                                                                                                                                            |
| -------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET     | net10.0                                                                                                                                              |
| UI 框架  | **Avalonia 12.0.5**(已从 11.x 升级)                                                                                                                  |
| MVVM     | ReactiveUI 23.2.28 / ReactiveUI.Avalonia 12.0.3                                                                                                      |
| 停靠框架 | Dock.Avalonia 12.0.0.2(+ Themes.Fluent、Model.ReactiveUI)                                                                                            |
| SSH/SFTP | SSH.NET 2025.1.0                                                                                                                                     |
| 持久化   | **SonnetDB.Core 3.0.0 嵌入式多模型数据库**(`%LocalAppData%/VelaShell/sonnetdb`;文档集合 + 时序 measurement;旧 JSON 首次运行一次性导入;LiteDB 已移除) |
| 打包     | Velopack 1.2.0                                                                                                                                       |
| 测试     | **MSTest 3.11.1**(已从 xUnit 全量迁移;FluentAssertions 已移除)                                                                                       |

## 2. 解决方案分层

```
src/
├── VelaShell/                桌面入口、DI 组合根、视图(axaml)、App 层 ViewModel、停靠、行为
├── VelaShell.Presentation/   跨层 ViewModel、连接/隧道工作流服务
├── VelaShell.Controls/       自定义控件与设计 token(PulseTokens/PulseShellTokens/Generic)
├── VelaShell.Terminal/       ★ 自研 VT 终端引擎 + 自绘渲染控件
├── VelaShell.Core/           领域模型、抽象契约、数据存储、SSH/SFTP 封装接口、本地化
└── VelaShell.Infrastructure/ SSH.NET/SFTP/隧道实现、存储路径、DI 扩展
tests/  6 个 MSTest 项目(见 §7)
解决方案文件:仓库根目录 VelaShell.slnx(注意:曾在 src/ 下,VS 打开后移到了根目录)
```

## 3. 自研终端引擎(核心,替换了坏掉的 AvaloniaTerminal)

彻底移除第三方 `AvaloniaTerminal 1.0.0-alpha.7`,改为手写 VT 引擎。位于 `src/VelaShell.Terminal/Emulation/` 与 `Rendering/`:

- `VtParser.cs` — Paul Williams DEC ANSI 状态机(Ground/Escape/CSI/OSC/DCS…)+ 独立 VT52 语法路径;消费 Unicode 标量,派发到 `IVtActions`。
- `TerminalScreen.cs` + `TerminalRow/TerminalCell/CellFlags/TerminalColor` — 网格、主/备屏、滚动区域(DECSTBM)、scrollback、光标、tab stops。
- `TerminalEmulator.cs` — 仿真器大脑(实现 `IVtActions`):SGR(16/256/truecolor)、光标/擦除/插删行列、模式(DECAWM/DECOM/应用键盘/插入/括号粘贴/鼠标跟踪…)、DEC 线绘字符集、DA/DSR 应答、备用屏。
- `TerminalType.cs` — **vt52/100/102/220/320/340/420/520/xterm/xterm-256color** 十种 profile,各自 TERM 名 + Device Attributes 应答;`FromTermName`/`ToTermName`;**xterm-256color 为默认**。
- `Utf8Sink.cs` — 增量解码,**可配置任意编码**(UTF-8 默认,GBK/Big5 等);`CharWidth.cs` — wcwidth(CJK 双宽);`TerminalPalette.cs` — 256 色 + 设计稿 term-\* 配色;`Charsets.cs` — DEC 线绘映射;`InputEncoder.cs` — 按键→字节(应用光标键、xterm 修饰键、VT52)。
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
- **设置窗口现为 11 页**(2026-07-12,840×740):常规 / 外观 / 终端 / 密钥管理 / 快捷键参考(纯展示) / 文件传输 / 安全审计(含会话录制与已信任主机) / 代码片段 / 云同步 / 关于(含贡献者) / 支持与捐赠;新增页与整改详情见 §13 与 `docs/settings-audit.md`。
- **终端配色跟随主题**:未自定义时 暗=Dracula / 亮=Solarized Light 实时切换;配色方案下拉的“(默认)”后缀与选中项随主题动态联动,选默认方案 = 恢复出厂跟随态。

## 7. 测试(已全量迁移到 MSTest)

- 6 个测试项目(2026-07-12 计数):`Controls.Tests`(1)、`Infrastructure.Tests`(22)、`Presentation.Tests`(23)、`Terminal.Tests`(141)、`Core.Tests`(151,含 SyncCrypto)、`VelaShell.Tests`(268,含主机指纹三选项)。**合计 ≈606**。
- **已知失败(非回归)**:① `ConPty_SpawnsShell_HandshakesAndSignalsEof` 环境相关(本机无头 ConPTY 不出帧,干净工作树同样失败);② QuickCommands/命令建议相关 12 个 —— 测试期望 11 个内置命令(含 htop)而 `QuickCommandCatalog` 只有 8 个,**测试与目录不同步,待对齐**(见 §13 遗留)。
- 已移除 `xunit`/`xunit.v3`/`FluentAssertions`/`Avalonia.Headless.XUnit`;改用 `MSTest.TestFramework`+`MSTest.TestAdapter` 3.11.1,全局 `using Microsoft.VisualStudio.TestTools.UnitTesting`。
- 转换约定(供新增测试参考):`[Fact]`→`[TestMethod]`;`[Theory]`+`[InlineData]`→`[DataTestMethod]`+`[DataRow]`;`[Trait("Category","X")]`→`[TestCategory("X")]`;每类 `[TestClass]`;`ITestOutputHelper`→`public TestContext TestContext {get;set;}`;`IAsyncLifetime`→`[TestInitialize]`/`[TestCleanup]`。
- 断言:MSTest `Assert.AreEqual(EXPECTED, ACTUAL)`(期望在前);异常用 `Assert.ThrowsExactly`/`Assert.ThrowsExactlyAsync`;字符串用 `StringAssert`;序列用 `CollectionAssert`。
- 注意点:`long`/`uint` 期望值要带后缀(`AreEqual(object,object)` 类型严格);`bool?` 用 `x == true`;非记录类型对象等价用 JSON 序列化比较。
- 测试**不渲染** Avalonia(控件只 `new`),故无需 headless 包;`App.Tests/ModuleInit.cs` 用 `[ModuleInitializer]` 初始化 ReactiveUI 调度器,保留。
- 集成测试(`SshIntegrationTests` 需 Docker+SSH 服务器、`CrossPlatformPublishTests` 需 `VELASHELL_PUBLISH_TESTS=1`)按环境早退跳过。

## 8. 关键约定 / 已知坑

- 构建/测试用根目录 `VelaShell.slnx`。运行 App 后 DLL 被占用会导致构建报"文件被锁定"——先停掉运行实例。
- Bash 工具用 Git Bash;不要用 `Read`/`Grep` 直接读 `.pen`(加密,只能走 pencil MCP)。
- 记忆索引见 `C:\Users\Joe\.claude\projects\G--VelaShell\memory\`(terminal-engine、docking、sonnetdb-storage、connect-flow)。
- SonnetDB 要点:`Tsdb.Open(new TsdbOptions{RootDirectory})`;文档 `db.Documents.Open(name)` 的 Upsert/Get/Scan/Delete;时序 `db.Write(Point.Create(...))` + `SqlExecutor.Execute` SELECT;`FieldType` 在 `SonnetDB.Storage.Format`(是 `Int64` 不是 `Long`,写值用 `FieldValue.FromLong`);**时序 tag 值不允许空串**(临时连接不写 profile_id);**SQL 方言:`ORDER BY time` 要求 SELECT 列表包含 time 列**;`DELETE FROM measurement` 可能不受支持(录制存储以 drop+回写压缩兜底回收);仓储加密必须写副本、不可原地改传入的 profile(内存明文用于活动连接)。
- Avalonia 12 坑:`Run.Text` 绑定会在卸载等时机回写(展示转换器 `ConvertBack` 返回 `BindingOperations.DoNothing`、绑定标 `Mode=OneWay`);ComboBox 的 `SelectedItem` 在 ItemsSource 为空/Clear 时会把 null 写回数据源(载入顺序先填列表再回填选中,见默认密钥修复);XML 属性值中的换行被规范化为空格(多行文案拆多个 TextBlock)。

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

✅ **已完成接线**(本轮实现,详见各消费点):终端行为全套(光标样式/闪烁、行高、选中即复制、右键粘贴、复制去尾空格、双击选词、多行粘贴确认、Ctrl+C 复制、滚动行为、Bell 三模式+标签闪烁、IME 开关)、外观(终端四色+ANSI16 稀疏覆盖、窗口透明度、菜单栏显隐、侧边栏位置、启动窗口状态、UI 字体/字号)、常规(默认端口、连接超时/心跳、自动重连+间隔+重试、关闭前确认、断开提醒+声音、开机自启、托盘、恢复会话、会话日志+保留清理、全局记住密码)、文件传输(远程初始目录、下载目录、显示隐藏文件、最大并发、双向冲突策略(下载查本地/上传 stat 远端,询问弹窗+覆盖/跳过/重命名,2026-07-10)、保留时间戳、完成通知、带宽限速、传输日志+保留清理)、安全(首次指纹人工确认、指纹变更阻断/人工裁决、告警通道应用内+Webhook+审计)、密钥(默认认证密钥)。
关键接线点:`MainWindowViewModel.ApplyLiveTerminalSettings` / `MainWindow.ApplyWindowAppearance+OnClosing` / `InfrastructureServiceCollectionExtensions`(超时/心跳/指纹策略)/ `SftpService`(带宽/时间戳)/ `FileBrowserViewModel.TransferOptions`。
默认值调整:LineHeight 1.2→1.0、ScrollOnOutput true→false、CopyOnSelect false→true、RemoteInitialPath "/home/user"→""(空=家目录)。

⏳ **仍未实现(2026-07-11 起这些 UI 已按设置审计从界面隐藏——不再以禁用控件示人;字段仍持久化,实现后恢复展示)**:

| 项                                            | 设置位置 | 未实现原因 / 实现思路                                                                                             |
| --------------------------------------------- | -------- | ----------------------------------------------------------------------------------------------------------------- |
| 标签栏位置(顶部/底部)                         | 外观     | 标签条由 Dock.Avalonia 的 DocumentDock 模板提供,需覆写其主题模板才能移到底部                                      |
| 启动时检查更新 / 更新频道 / 自动下载          | 常规     | Velopack `UpdateService` 未接入,且无发布 feed URL;接入后三项一并实现(About 页"检查更新"目前也是占位)              |
| 主密码保护                                    | 常规     | 需主密码派生密钥替换 `AesSecretProtector` 的本机密钥文件 + 启动解锁弹窗 + 密文迁移,安全敏感需单独设计             |
| 断点续传 / 自动续传 / 传输重试 / 临时文件清理 | 文件传输 | 需 `.part` 临时文件 + SFTP offset 续写(SSH.NET 支持 DownloadFile offset 有限,需改用流式 seek)+ 传输队列持久化     |
| ~~会话录制 / 输入脱敏~~                       | 安全审计 | ✅ 2026-07-12 录制与回放已实现(SonnetDB 时序 + 回放中心,见 §13);输入脱敏确认不做(仅录输出流,密码无回显)         |
| 自动加载密钥到 Agent                          | 密钥管理 | 需集成 Windows OpenSSH ssh-agent(named pipe 协议)或 Pageant                                                       |

❌ **确认当前架构不实现,已从设置界面与 `AppSettings` 移除**(2026-07-10,见 docs/架构设计.md §11):连字 Ligatures(自绘渲染器按单元格排版,无法跨字符连字)、自适应标题栏颜色(系统原生标题栏由 OS 托管)、系统通知 Toast(需 AppUserModelID/通知框架;常规页用「声音提示」、安全审计页告警通道改为「提示音」`Security.AlertSound` 替代)。
✅ **上传方向冲突策略已实现**(2026-07-10):上传前 `ISftpService.ExistsAsync` stat 远端同名文件,按策略询问(弹窗:覆盖 or 跳过)/覆盖/跳过/重命名(`file (1).txt` 取首个可用名);「覆盖」策略下不额外 stat,沿用 SFTP 覆盖语义;编辑器保存回传属有意覆盖,不走冲突检查。

**B. 功能缺口**

- 非 SSH 协议:连接弹窗 SFTP/Telnet/串口 标签禁用;第 2 步"证书"认证禁用。
- ✅ 快捷键展示表已与真实绑定逐条核对重建(2026-07-11,删除虚构项、补 Ctrl+N 绑定);**自定义键位确认不做**(产品决定,页面定位为"快捷键参考")。
- 密钥生成仅 RSA(PEM+OpenSSH 公钥);ed25519 生成缺失(.NET 无内置 OpenSSH ed25519 私钥导出,需评估 SSH.NET 或 BouncyCastle);导入不校验私钥有效性;删除无二次确认。
- 审计日志已在写(connect/connect-failed),但**无查看界面**;`audit_log`/`conn_history` 无保留策略(retention),长期运行会累积。
- ✅ 配置导出文案已修正为"仅应用设置"(2026-07-11,settings-audit C-08);全量选择性导出待做——注:**Gist 云同步(§13)已覆盖设置/连接/隧道/片段的跨设备迁移场景**。
- 命令面板"会话"类目仅最近连接,不含全部已保存会话。
- 关于页"检查更新":Velopack `UpdateService` 仍未接,但占位文案已改为如实提示"更新服务尚未接入"(2026-07-11);"更新日志"禁用。
- ✅ 会话录制与回放已组件化(2026-07-12,§13);✅ 主机信任中心以"安全审计 → 已信任主机"落地(查看/删除/地址脱敏)。系统资源监控面板、连接诊断中心、运维编排中心、文件传输 toast 等设计稿面板状态不变。

**C. 技术债 / 小瑕疵(2026-07-09 处理完毕,余项见末尾)**

- ✅ QuickConnect 组件已删除(View/VM/SidebarViewModel 引用与测试同步清理;Strings 资源保留供本地化测试)。
- ✅ SonnetDB 锁粒度:**决定保留全局信号量**——文档集合与时序共享同一 Tsdb 实例(同一 WAL/存储引擎),SonnetDB 未承诺内部线程安全,按集合分锁有并发损坏风险。实际瓶颈是设置读热点(每次连接/每个传输文件都读一次),已在 `SonnetDbSettingsService` 加 **settings JSON 缓存**(缓存序列化文本、按次反序列化,调用方语义不变),读路径不再进锁/碰盘。
- ✅ 硬编码 `#0A0E14` 前景已抽成 `PulseAccentForeground` 令牌(暗=深字/亮=浅字;亮色 accent #644AC9 深底配深字对比不足的问题即此);用户自定义强调色时 `App.ApplyAccent` 按亮度自动配对前景。AboutPage 固定青色渐变上的图标有意保留硬编码。亮色主题整体仍建议实机走查一遍。
- ✅ `Ctrl+T` 改为打开新建连接(与 Ctrl+N 一致;旧绑定往已不显示的 TabBar 塞空标签)。Dock 布局持久化**缓做**:文档即活动 SSH 会话,单独恢复布局无意义;需与"恢复会话"联动(布局节点 ↔ profile 映射,Dock.Avalonia DockSerializer + 自定义 document 还原器),列为后续特性。
- ✅ OSC 52(远端写剪贴板,tmux/vim yank;只支持写方向,查询"?"一律不应答防剪贴板泄露;1MB 上限)与 DECRQSS(应答 SGR "m" 与 DECSTBM "r",其余回 `DCS 0 $ r`)已实现,含单测。**顺带修复解析器预存在 bug:全局"ESC 重启序列"会把 ST(ESC \)结尾的 OSC/DCS 整段丢弃**(BEL 结尾才能用),现已在 ESC 分支先分发在途载荷,补了回归测试。
- ✅ 运行时热切终端类型:**按设计不做**——TERM 在连接时向远端协商,活动会话热切只会造成本地仿真与远端 TERM 能力档不一致;设置修改对新连接生效即为正确语义。CJK 回退:字体链(Cascadia Mono→JetBrains Mono→Consolas→Microsoft YaHei→monospace)+ 渲染器逐格 FormattedText 回退路径已覆盖双宽字形,视为已解决。
- ⏳ sixel 图形仍挂起(多日工作量,依赖需求评估)。
- ✅ 需实机确认(代码层无法验证):三个自绘对话框(连接/验证/设置,SystemDecorations=None)的拖动与阴影;亮色主题下各对话框观感;命令面板圆角修复效果。注:plan 旧文提到的主窗 `WindowDecorations="None"` 已不存在——主窗现用原生标题栏(设计 §2),该条仅余对话框部分。

## 11. 设计稿分析已记录的问题(供实现时对照)

- 设置-终端 缺终端类型/编码选择器(已在代码补上)。
- term-\* 只定义 8 个 ANSI 色,无 bright/256(引擎侧已补全)。
- 未指定 CJK/双宽回退字体。
- 终端交互(光标样式、选区色、终端内搜索、分屏)设计未建模。
- 亮色主题 `bg-terminal=#1E1E2E` 仍为深色(疑似有意)。
- Logo 有一个 `enabled:false` 残留图标;文件列表"修改时间"列无固定宽度。

## 12. 与主流终端工具的功能缺口(2026-07-09 对照 Xshell / MobaXterm / Tabby / WindTerm 分析)

> §10.B 已列的缺口(Telnet/串口/证书认证、快捷键自定义、ed25519 生成、审计查看界面、全量配置导出、更新检查等)不在此重复。以下为本次新识别的缺口,按优先级排列。

**P1 —— 日常使用高频(2026-07-09 全部实现,除第 1 项外各自独立提交)**

1. ✅ **本地终端标签**(⚠️ 待实机验证,暂未提交):`Infrastructure/Pty/ConPtyShellStream.cs`(CreatePseudoConsole + 双匿名管道;进程退出 → 300ms 排空 → 关伪控制台 → 读端 EOF 归一化)实现 `IShellStreamWrapper`,复用既有 桥→VT 引擎→自绘控件 管线;`App/Services/LocalShellCatalog.cs` 探测 pwsh / Windows PowerShell / CMD / WSL / Git Bash 并动态注册命令面板入口(`local.*`);本地标签强制 UTF-8、不自动重连(exit 是用户意图)、Enter/Ctrl+R 重开进程。**已知注意点**:本机(Windows 预览版 conhost)对无头测试进程不渲染屏幕帧(新版 ConPTY 先发 `CSI 1t`/`CSI c`/`?1004h`/`?9001h` 协商,DA 无应答约 3 秒自杀),单测只断言 拉起+握手+输入通路+EOF 契约;GUI 内 VT 引擎会自动应答 DA,需实测确认出帧,若仍无帧则下一步补 win32-input-mode/更完整的终端应答。
2. ✅ **SSH 跳板机(ProxyJump)**:`SessionProfile.JumpHostProfileId` 引用另一条已保存配置作跳板(链式即多段跳,≤5 跳、带环检测,`ConnectionWorkflowService.BuildChainAsync`);`JumpChainSshClientWrapper` 逐跳建链,前一跳开 `ForwardedPortLocal(127.0.0.1:0)` 承载下一跳,任一跳失败整链回收;**指纹按各跳逻辑主机校验**(绝不按 127.0.0.1 记录);连接对话框-高级选项 选跳板;跳板配置需已保存凭据。
3. ✅ **保存的会话全部进命令面板**:"最近连接"(快速通道)+"会话"(session_profiles 全量、分组名徽章、按名排序),两组按 ProfileId 去重;缓存随会话树刷新(`RefreshPaletteSessionsAsync`)。
4. ✅ **导出终端缓冲区**:命令面板"导出终端输出到文件"(`terminal.export`):有选区导出选区、否则全量(scrollback+屏幕,逐行去尾空格、截掉尾部空行),保存对话框预填 标签名-时间戳.txt。
5. ✅ **配色方案预设**:`Core/Models/TerminalColorScheme.cs` 内置 Dracula / Solarized Dark / Solarized Light / Nord / Gruvbox Dark / One Dark / Monokai / Tokyo Night;外观页"配色方案"下拉一键写入整套颜色(保存生效);选 Dracula 即恢复默认、继续跟随主题。
6. ✅ **克隆会话**:`session.clone`(Ctrl+Shift+N / 命令面板)对当前标签的 Profile 再连一次。

**P2 —— 进阶运维能力**
7. **多会话同步输入**(send to all / 命令多发):对选中的多个标签广播键入,集群运维刚需;在 `UserInput` 分发处加广播开关即可。
8. **ZMODEM(rz/sz)**:终端内直接收发文件,Xshell/SecureCRT 标配;需在 `SshTerminalBridge` 流上识别 ZMODEM 起始序列并接管通道(可评估 trzsz 协议替代,实现更简单)。
9. **SSH config 导入**:解析 `~/.ssh/config`(Host/HostName/Port/User/IdentityFile/ProxyJump)批量导入会话,降低迁移成本。
✅10. **连接代理**:SOCKS5/HTTP 代理经由连接(公司内网出网场景);SSH.NET `ConnectionInfo` 原生支持 ProxyTypes。
11. **防空闲断开(Anti-idle)**:按间隔发送自定义串(如 `\0` 或空格),与已实现的 SSH keepalive 互补(keepalive 防 NAT 超时,anti-idle 防服务端 shell 超时踢出)。
✅12. **known_hosts 管理界面**:已落地为 设置 → 安全审计 → 已信任主机(2026-07-12,列出/删除/截图防泄露地址脱敏);导出未做。
✅13. **会话标签自定义颜色/图标**:多环境(生产红/测试绿)一眼区分;SessionProfile 加 color 字段 + 标签条着色。

**P3 —— 锦上添花**
14. **SFTP 本地/远程双栏**:目前只有远程单栏,MobaXterm/WinSCP 式左右双栏拖拽互传体验更好(升级现有 FileBrowser 布局)。
15. **用户自定义关键字高亮规则**:语义高亮已内置(URL/IP/错误词),开放用户正则+颜色规则表(WindTerm 卖点)。
✅16. **命令自动补全/历史建议**:输入时基于本地命令历史悬浮建议(WindTerm 式);已有 quick_commands 可作为数据源之一。
17. **OSC 52 剪贴板**:远端 vim/tmux 写系统剪贴板(§8 遗留,一并归档到此)。
18. **触发器/自动应答**:输出匹配正则时自动发送响应(expect 式),如自动 yes/密码带外输入。
19. **多窗口**:新开独立主窗口(目前只有单窗口+分屏/浮动 Dock)。
20. **Mosh / SSH 证书(certificate)认证**:弱网漫游与企业 CA 场景,按需求评估。

## 13. 2026-07-11 ~ 07-12 批次(设置审计整改 + 四个新特性)

**A. 设置审计整改**(台账与逐项状态见 `docs/settings-audit.md`,共三批):
BellMode/VisualBell 合并(旧配置经 `AppSettings.Normalize()` 迁移)、自动重连次数统一、默认值来源统一、显示隐藏文件写回持久化、恢复默认/清除历史加确认、误导性文案与九组相似命名修正、12+ 个未实现禁用控件隐藏或删除、选项类统一 `ObservableOptions`(INPC,从属设置条件显隐真正生效)、快捷键页与真实绑定核对重建(自定义键位确认不做)。

**B. 主机指纹三选项确认 + 已信任主机管理**:
`IHostKeyPrompt.DecideAsync` 三态(永久信任=写 known_hosts / 仅本次信任=进程内 `HostTrustOnceCache` 不落盘 / 取消=fail-closed);SFTP 独立通道补主机指纹校验(修复默认信任任意指纹的 MITM 缺口);安全审计页新增"已信任主机"列表(删除即可重触发首次确认;地址默认脱敏防截图泄露)。

**C. GitHub Gist 云同步**(`Core/Sync` + `Infrastructure/Sync` + 设置"云同步"页):
同步范围 = 应用设置(剔除设备本地字段)+ 连接配置(含分组与隧道,upsert 合并不删本地)+ 代码片段;单文件 secret Gist,版本管理复用 Gist 原生 revision(列表含来源设备,可恢复任意版本);可选 PBKDF2-SHA256(200k)+AES-256-GCM 端到端加密(未启用时凭据绝不上传);智能方向判定(本地改动标记 × 远端 revision,双端都改按较新者胜);自动同步 = 启动拉取 + 设置保存防抖推送;PAT/口令经 `ISecretProtector` 机器绑定加密,永不进载荷。

**D. 会话录制与回放**(设计 `NceE6`;`Core/Recording` + `SonnetDbSessionRecordingStore` + `RecordingPlayerView`):
录制 = 桥输出 600ms/64KB 缓冲成块写 SonnetDB 时序 measurement `session_recording_chunks`(元数据在文档集合 `recordings`);开关 `Security.RecordProductionSessions`(安全审计页,对新连接生效),保留天数随会话日志;回放中心 = 列表 + 只读终端按时间轴重放 + seek(重置瞬时重放)+ 1x/2x/4x + 跳过空闲 + 删除 + 导出 asciicast v2。输入脱敏确认不做(仅录输出流)。

**E. 支持与捐赠页**(设置导航末位):支付宝/微信/Wise(链接可点击+复制),收款码已裁剪入 `Assets/`;文案强调 PR/Issue 是最好的支持。

**F. 后续增量(同批小项)**:
- 回放中心:窗口 1200×820 + 无边框缩放(右下手柄/最大化/双击标题栏)、列表选中态改主题令牌、播完点播放自动从头、倍速扩至 1x~16x;录制保留随日志天数清理 + DELETE 不可用时 drop+回写压缩兜底(防孤儿数据块磁盘只增不减)。
- **双许可落地**:MIT → AGPL-3.0(`LICENSE` 官方全文)+ 商业授权(`LICENSE-COMMERCIAL.md`,联系 dygood@outlook.com,含轻量 CLA 条款);README/关于页同步正版声明(名称与 Logo 不在开源授权范围);商标注册与历史贡献者重许可确认为线下待办。
- 关于页**贡献者区**(设计 kGwqX,仅头像+名称):真实提交者(joesdu/tsaiggo),GitHub 头像异步加载(失败回退首字母),点击跳转主页。
- **终端配色随主题联动**:亮色默认调色板由 Alucard 换为 Solarized Light;配色方案下拉“(默认)”标注与跟随态选中项随主题动态切换(选默认方案 = 恢复出厂跟随态;显式选其它方案 = 钉住)。已知边界:覆盖模型以 Dracula 色值为出厂基准,亮色下无法“钉住 Dracula”(选它即回跟随态)。

**已知遗留**:QuickCommands 相关 12 个测试在用户某次提交后失败(测试期望 11 个内置命令含 htop,`QuickCommandCatalog` 只有 8 个,测试与目录不同步,与上述改动无关);C-09 设置页本地化(~150 条文案迁移 .resx)待专门批次。
