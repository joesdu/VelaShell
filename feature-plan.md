# VelaShell 特性计划

> **这份文件记录「还没发生的事」** —— 待办、候选特性，以及已经决策不做的清单。
>
> **已经发生的事在 [`plan.md`](plan.md)** —— 已完成的工作、当前架构、每一次改动的来龙去脉。
> 两份文件的分工是硬的：一件事做完了，就从这里删掉、去 `plan.md` 补一节；
> 一件事还没做，就不要在 `plan.md` 里留 TODO。
>
> 最近复核：**2026-09-26**。逐条对着 `src/` 核过：四个 P0 开关仍然零消费者、
> `SessionProfile.Tags` 仍无人读。2026-10-01 单独复核了「11 条『怎么改都绿』的 UI 用例」：
> 早在 `plan.md` §139 就已修完（这里漏删），探针确认后删除，并加了守门测试（§148）。
> 已完成的条目（✅）一律移出本文件，
> 它们的来龙去脉在 `plan.md` 对应小节；「SSH 压缩开关」核实已在 `plan.md` §92 接线，一并删除。
>
> **全文分三部分，读法不同**：
>
> | 部分 | 是什么 | 怎么用 |
> | --- | --- | --- |
> | [一、欠账](#一欠账) | **说了没做到** —— 开关存了不生效、数据有了没界面、用例假绿、已知缺陷 | 优先清，它们是「界面在骗人」 |
> | [二、路线图](#二路线图) | **还没说要做** —— 对着竞品找空档，按「架构落点」估成本 | 排下一步做什么 |
> | [三、文档待同步](#三文档待同步velashell-docs) · [四、确认不做](#四确认不做) | 欠 velashell-docs 的账；评估过、别再提的 | 改文档前查一眼；提新想法前查一眼 |

## 🏷️ 标识

| 状态 | 含义 | | 优先级 | 含义 |
| :---: | --- | --- | :---: | --- |
| ⏳ | 待办 —— 已确认要做，未开工 | | 🔴 P0 | 影响正确性、安全或诚信（含「存了但不生效」的开关） |
| 🚧 | 进行中 —— 有部分实现，未闭合 | | 🟠 P1 | 日常使用高频，缺了明显别扭 |
| 💡 | 候选 —— 想法已记录，要不要做待评估 | | 🟡 P2 | 进阶能力，有它更好 |
| 📄 | 文档待同步 —— 代码已改，velashell-docs 没跟上 | | 🟢 P3 | 锦上添花 / 大工程，等需求 |

## 🎯 建议的下一步

按「杠杆 ÷ 成本」排，下面这件最值得先动（内置 X 服务端全库审查的 31 项已在 `plan.md` §124 修完、第二次审查的 184 条已在 §167 修完；keyboard-interactive 动态码界面、算法协商可配已在 §133、§134 落地；「怎么改都绿」的 UI 用例已在 §139 修完、§148 加了守门）：

1. **录制与日志的输出脱敏**（🟠 P1）—— `cat .env`、`kubectl get secret` 的输出正原样落盘，是现实风险。

## 📊 待办分布

| 部分 | 🔴 P0 | 🟠 P1 | 🟡 P2 | 🟢 P3 | 合计 |
| --- | :---: | :---: | :---: | :---: | :---: |
| 一、欠账 | 4 | 2 | 9 | 9 | **24** |
| 二、路线图 | — | 6 | 18 | 16 | **40** |
| 三、文档待同步 | — | — | — | — | **29** |


---

## 一、欠账

### 🔴 P0 —— 存了但不生效的开关

> 共同点：**字段已持久化、设置页可能还展示着，但运行时零消费者**。用户拨了开关以为生效了，其实什么都没发生。
> 闭合有两条路，二选一：**接上运行时**，或**从界面撤下并在 `AppSettings` 注释里写明原因**。
> 统计口径见 velashell-docs 的 [`zh/host/settings-audit.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/settings-audit.md)。

| 状态 | 项 | 字段 | 现状 | 闭合要做什么 |
| :---: | --- | --- | --- | --- |
| ⏳ | **主密码保护** | `AppSettings.MasterPasswordProtection` | 零消费者；UI 已撤下并留注释 R-04 | 主密码派生密钥替换 `AesSecretProtector` 的本机密钥文件 + 启动解锁弹窗 + 存量密文迁移。**安全敏感，先单独设计再动手** |
| ⏳ | **自动下载更新** | `AppSettings.AutoDownloadUpdates` | 零消费者、零 UI | 下载调度 + SHA-256 校验 + 静默换版。别和已实现的「启动时检查更新」（`CheckUpdatesOnStartup`）混淆 |
| ⏳ | **传输失败重试** | `AppSettings.TransferMaxRetries` | 零消费者 | 前提是[传输队列持久化](#c-文件与传输)（重试得知道「重试什么」）。同组的 `AutoResume` 是遗留兼容字段，真开关是 `ResumeEnabled`，**别**给它接线 |
| ⏳ | **标签栏位置（顶部 / 底部）** | `AppearanceOptions.TabBarPosition` + `SettingsViewModel.TabBarPositionIndex` | 映射都在，停靠层零消费；UI 已从外观页撤下 | VelaDock 的 `DockGroupControl` 改标签条停靠边即可，**低成本**，按需排期 |

### 🔒 安全与凭据

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| 🚧 | 🟠 P1 | **配置导出的选择性与脱敏** | 导出 / 导入是全量 `AppSettings` 序列化 + 整体覆盖。⚠️ **含 Security / Proxy 等敏感块，代理密码是明文** | 分类勾选 + 敏感块默认排除（或强制加密）。跨设备迁移已有 Gist 云同步（§13-C），这条的价值是**别把明文代理密码写进一个随手分享的文件** |
| ⏳ | 🟡 P2 | **密钥管理的三处小缺口** | 导入不校验私钥有效性；删除无二次确认；导出未做（已信任主机同样只能删不能导） | 各自独立的小改动，一批做掉 |
| ⏳ | 🟢 P3 | **导入与外部启动不产出证书字段** | SSH 证书认证已落地（`plan.md` §63），但 PuTTY / Xshell / `ssh://` 导入与外部拉起都只认密码 / 私钥 | 先扩各自的解析器，再映射到 `AuthMethod.Certificate` |

### 🗂️ 会话与工作区

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟡 P2 | **会话标签（Tags）存了不生效** | `SessionProfile.Tags` 能编辑（`ConnectionProfileViewModel`），但全仓没有任何地方消费它 | 会话树加过滤框、命令面板按 tag 匹配（`PaletteScorer` 现成） |
| ⏳ | 🟢 P3 | **外部拉起的 `-newtab` 标签名没用上** | Xshell 兼容登录里 `-newtab` 带的标签名目前**只是被忽略**，没有拿去当标签页标题（`plan.md` §84） | 给 `ExternalLaunchRequest` 加字段，并一路穿过单实例转发的那份 JSON |

### 📁 文件传输

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| 🚧 | 🟡 P2 | **SFTP 双栏与 WinSCP 的剩余差距** | 逐项清单在 velashell-docs 的 [`SFTP双栏与WinSCP差距分析.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/SFTP双栏与WinSCP差距分析.md) | ⚠️ 那份文档把差距分成「接线债 / 能力缺失 / 架构级缺失」三类，**别混在一起排期** —— 接线债是几小时，架构级是几周 |
| ⏳ | 🟢 P3 | **插件协议的文件能力补口** | 插件协议上：符号链接如实抛不支持（SDK 的 `RemoteFileEntry` 没有链接字段）；设不了远端修改时间，双向同步会把刚上传的文件判成远端较新；算不了服务器端摘要（`plan.md` §72、§74、§75） | 一次性扩 SDK 契约（链接字段、`SetLastWriteTime`、摘要）。⚠️ 要发 SDK，按 `AGENTS.md` 的版本纪律走，别自己定版本号 |
| ⏳ | 🟢 P3 | **「远程 + 远程」双栏的同步窗口** | 双栏远程文档只有「比较目录」（按大小与修改时间，不做 SHA-256），没有「同步…」与「保持远端最新」（`plan.md` §128） | `DirectorySyncViewModel` / `DirectorySyncRunner` 全是「本地路径 + Upload/Download」的语义，要做成两端对称（两边都用 `DirectoryTreeScanner.ScanRemoteAsync`、动作换成两个方向的 Relay）；「保持远端最新」靠监视本地文件系统，双远程下没有意义，不做。等有人要再排 |
| ⏳ | 🟢 P3 | **目录同步：FTP 时区偏移** | FTP 服务器与本机不在同一时区时，按修改时间比较会整体错位（WinSCP 在会话设置里有这一项） | 连接配置加时区偏移，比较器按偏移换算。同步选项本身**刻意不持久化**（只在文档标签内记住），别顺手加设置项 |
| ⏳ | 🟢 P3 | **SFTP 文件名按会话编码显示** | SSH 库默认按 UTF-8、解不开的字节无损往返（GBK 名字的文件打得开、删得掉，但显示成替换字符）；库已经有 `SftpOptions.FileNameEncoding`，宿主没接 | 把会话的终端编码（`SessionTerminalSettings.Encoding`）带进 SFTP 的连接参数：`SshSession` / `ConnectionInfo` 现在都不带这个值，得从配置那一路传过来。⚠️ UTF-8 时不要传（传 null），否则丢掉无损往返 |
| ⏳ | 🟢 P3 | **内置编辑器的临时副本没有兜底清理** | `RemoteFileEditorView.OnClosed` 只删自己那个 `builtin-edit\<8hex>\`；进程崩溃留下的没人管，退出时的 `TryDeleteEmptyTree` 只删空目录 | 启动时按时间兜底（如清 7 天前的）。⚠️ 别把「上传失败刻意保留的草稿」一起删了 —— 那是用户改动唯一的存身之处 |

### 🖥️ 终端与 SSH

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟡 P2 | **拆 SSH 库的两个上帝类** | `SshConnection` 约 4,000 行（六个文件，`SshConnection.cs` 自己约 1,970 行）、`SshChannel` 约 1,720 行，远过 `src/VelaShell.Ssh/AGENTS.md` 4.4 的 800 行（`plan.md` §117、§161） | 拆成 internal 协作者而不是更多 partial：`SshChannel` 的收发窗口与 stdin 泵、`SshConnection` 的收包分发与全局请求账本。**纯重构，单独开 PR**，`VelaShell.Ssh.Tests` 与互操作用例是安全网。⚠️ 拆的时候这几处耦合不能断（2026-10-05 审查的备忘）：①入队锁里的 admit 回调跨两个类 —— `MayStillSend` / `TryCommitClose` / `OnClose` / 账本 `Register` 必须与 `TryWriteFrame` 在同一把 `_enqueueLock` 里，不能拆成「先查后发」，也不能变异步；②锁顺序 `_enqueueLock` → 连接 `_stateLock` → 通道 `_stateLock`，`ReleaseId` 先放通道锁再进连接锁，新协作者不许持通道锁入队；③`FinishClose` 的步骤顺序：锁内改状态 → 事件 → 关账 → 管道 → stdin → 唤醒闸门 → `CancelAsync`；④接收分发会被重入（重协商期间 `RekeyKexTransport.ReadPacketAsync` 回调 `DispatchAsync`），拆出去后仍要能在 KEX 中被调用，且只许投递、不许等发送；⑤预算是两本账：连接的 `_windowBudgetUsed` 归连接锁，通道的 `_budgetCharged` / `_idReleased` 归通道锁，「先申请 → 通道锁里确认 → 失败回滚」三步要保持；⑥`_priority` 只在 `_enqueueLock` 下写、泵每次先读它（回补不越过 CLOSE 与闸门的前提）；交互道 `_interactive` 同样在锁里写，一条通道的帧（含接收循环替它回的 CLOSE 与请求应答）全走开通道时定下的那条道，泵在回补之后让它与普通队列轮流（`plan.md` §162 的 Q7）；⑦`WouldSendNow` 与 `Admit` 必须在泵线程上连续调用 |
| ⏳ | 🟡 P2 | **SSH 连接信息面板** | 库已经交出这条连接谈成的算法、主机密钥、会话 ID、对端版本、认证方法与 server-sig-algs、各阶段耗时、往返时间、重协商次数 / 起因 / 耗时（`Rekeyed` 事件）、收发字节、开着的通道快照（`plan.md` §161），宿主一处都没显示 —— 排障时用户只能看连接诊断重新探一遍 | 会话标签的上下文菜单或状态栏点开一个只读面板，按块摆：对端、协商结果、认证、计时、通道列表（类型、开了多久、字节数）。令牌照 `DESIGN.md`，文案五份 resx；数据全在库的只读属性上，不新开探测 |
| ⏳ | 🟡 P2 | **SSH 库已有、宿主还没接的能力** | `plan.md` §161 那一批在库里落地、宿主还没用上：Windows CNG 签名器（证书存储 / TPM / 智能卡里不可导出的钥）、按键时序混淆（`ObscureKeystrokeTiming`）、报文旁路（`IPacketTap`）、转发的实时吞吐 / 连接快照 / 限速、TLS 拨号器、远程动态转发（`-R` 不给目标）与放行名单、Unix 域套接字转发、ssh_config 转发项与 `IdentityAgent` 的导入；agent 加钥的目的地约束（`AllowedHops`，`plan.md` §163） | 逐项接：CNG 进密钥选择（「系统密钥库」一类）；按键混淆作为连接的高级选项（默认关，说明带宽代价）；报文旁路接进连接诊断的协议轨迹；隧道面板显示速率与连接列表、可设限速；TLS 进代理设置；隧道类型加「远程动态」「Unix 套接字」；会话导入带上转发项。每项都要五份 resx 与交互规格 |
| ⏳ | 🟡 P2 | **SSH 库剩余中文诊断文本的界面本地化** | 英 / 日 / 韩界面仍会看到库的中文原文：认证逐条尝试记录（`SshAuthAttempt.ToString` / `Detail`）、`SshChannelException` 的建议、`KnownHostLookup.CertificateProblem`、带路径 / 指纹 / 端口的私钥与证书消息（`plan.md` §117） | **先在库里补结构化出处**（`Detail` 的种类、证书问题的枚举），宿主 `SshInterop` 再按枚举出五语言文案 —— 不在宿主里解析句子（`AGENTS.md` 4.5） |

### 🪟 窗口与外观

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| 🚧 | 🟠 P1 | **跨平台实机验收** | 全部弹窗与独立窗口已按平台走原生机制（`plan.md` §118，`Views/WindowChrome.cs`）。macOS 上主窗口、设置窗口与消息框已实机验收；**非模态窗口的红绿灯没在 Mac 上看过**；Linux 只在 WSLg 里跑过（Weston 缺 `xdg_wm_base` ≥ 3，Wayland 起不来，测到的只是 X11 分支）。内置 X 服务端的 macOS「自动」键盘布局也只经过 CI 编译与平台无关的单测（`plan.md` §109） | ①**macOS**：任务管理器 / 资源监视 / 插件管理等独立窗口的红绿灯可用、自绘窗口按钮已隐藏、左侧让位宽度合适（`Themes/WindowChrome.axaml` 的 `traffic-light-spacer`，现为 60）、红绿灯与标题同一条中线、最大化 / 全屏后卡片铺满；X 服务端「自动」布局跟随系统输入法切换。②**Linux 原生 Wayland**（GNOME / KDE / Hyprland 任一）：弹窗外圈消失、阴影圆角描边与 Windows 一致、拖动与边缘缩放、背景模糊只在卡片范围内（取决于合成器，记下即可）、`Width` / `Height` 确实不含装饰（不对就改 `WindowChrome.SizeReduction` / `OuterFrameSize`）。③**Linux X11**：不透明矩形、没有外圈、自绘抓取区能缩放。④隔离插件的窗口（`PluginHostShellWindow`）同上各看一眼 |
| ⏳ | 🟢 P3 | **设计稿的两处残留** | Logo 有一个 `enabled:false` 的残留图标；文件列表「修改时间」列无固定宽度 | 小到可以顺手做掉 |
| 💡 | 🟢 P3 | **选区上的前景守卫改成「保色相」** | 选区底抬亮之后，ANSI 前景与选区底亮度差低于 `MinForegroundDelta`（0.18）的格子会被 `ReadableForeground` 换成兜底的纯白 / 纯黑 —— 选中一段就把 `ls --color` 的配色抹掉（`plan.md` §59，当时两害相权取了「一眼看得出选中了」） | 守卫改成保色相、只调明度，再对着具名主题跑一遍对比度尺子 |

### 🧩 插件生态

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟡 P2 | **四套版本比较口径** | `PluginManager.IsOlder`（丢掉预发布后缀）、`UpdateVersion`（完整 SemVer 子集）、CLI 的 `VersionOrder`、市场的 `SemVerComparer`（后两者按序数比预发布）—— `1.0.0-beta.10` 与 `1.0.0-beta.2` 谁新，四边能给出不同答案。更新路径已显式选用 `UpdateVersion`，**但那是绕不是修** | 把 `UpdateVersion` 抬进 `VelaShell.PluginSdk`，宿主 / CLI / 市场三边共用。⚠️ 要发 SDK，按 `AGENTS.md` 的版本纪律走 |
| ⏳ | 🟡 P2 | **对外 MCP 的带外审批** | 对外 MCP 走 `AgentToolbox` 产出工具，那条路上没有审批界面，「询问」模式等于一律拒绝写操作 —— 这是刻意的（`plan.md` §33-四） | 可选增强：审批推到 IM 渠道或桌面通知，用户放行后再执行 |
| ⏳ | 🟢 P3 | **三个第一方插件填图标** | 宿主侧已完成（`plan.md` §69，SDK 2.0.4 起有 `PluginIcon`），**宿主一行都不用再动**；`velashell-plugins` 里三个插件画的仍是通用插头 | 抬包到 SDK 2.0.4+；串口用 lucide `usb-c-port`（`M6 12h12 M6 8h12a4 4 0 0 1 0 8H6a4 4 0 0 1 0-8Z`），Redis 用品牌 logo（`PluginIcon.Filled(path, 1030)`），S3 用云或桶的描边字形 |
| 💡 | 🟢 P3 | **插件更新检查的响应缓存** | 市场没装 OutputCache，`/api/plugins/latest` 每次都真查库；目前只在打开插件管理页时问一次，量很小 | 装 OutputCache + `ETag`，按 ids 组合缓存几分钟。**等真有量了再做**，别为不存在的负载先加一层缓存 |

---

## 二、路线图

> 这一部分是**往前看**：对着 Xshell / MobaXterm / Tabby / WindTerm / Termius / SecureCRT / WezTerm / Warp
> 看还差什么，再结合本仓库的架构判断**哪些对我们特别便宜**。
> **「架构落点」那一列是重点。** 同一个功能，在别家要新起一套子系统，在我们这儿可能只是复用一个已有的接缝 ——
> 排期按这一列来，而不是按功能听起来多大。
>
> ⚠️ 对标矩阵是**粗粒度**的：只记各家公认的能力有无，不逐版本核对。用它决定「值不值得做」，别拿它当竞品说明书。

### 对标矩阵

| 能力 | VelaShell | Xshell | MobaXterm | Tabby | WindTerm | Termius |
| --- | :---: | :---: | :---: | :---: | :---: | :---: |
| 不依赖第三方的 VT 引擎 / 自绘渲染 | ✅ | — | — | (xterm.js) | ✅ | (xterm.js) |
| SSH / SFTP / 跳板机 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| SSH 证书认证 / Agent 转发 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| FTP / FTPS | ✅ | ✅ | ✅ | — | ✅ | — |
| Telnet / 串口 | ✅ 插件 | ✅ | ✅ | ✅ | ✅ | ✅ |
| X/Y/ZMODEM | ❌ 不做 | ✅ | ✅ | — | ✅ | — |
| X11 转发 + X 服务端 | ✅ **内置 VelaShell.XServer** | ✅ 转发 | ✅ 自带 X 服务端 | — | ✅ 转发 | — |
| 拖拽分屏 | ✅ VelaDock | 有限 | ✅ | ✅ | ✅ | ✅ |
| 多会话同步输入 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 端口转发 + 流量计量 | ✅ **带计量** | ✅ 无计量 | ✅ | ✅ | ✅ | ✅ |
| 会话录制 / 回放 | ✅ + asciicast | — | — | — | ✅ | — |
| 资源监视 / 进程 / 路由追踪 | ✅ | — | ✅ | — | ✅ | — |
| 目录比较 / 同步 | ✅ 含保持最新 | 有限 | ✅ | — | ✅ | — |
| OSC 8 超链接 / OSC 133 命令块 | ✅ | — | — | OSC 8 | — | — |
| 插件系统 | ✅ **双模 + 商店** | — | — | ✅ | — | — |
| AI 助手 / Agent | ✅ **含 IM 桥接 + MCP** | — | — | 插件 | — | 有限 |
| 云同步 | ✅ Gist | — | ✅ | ✅ | ✅ | ✅ 自营 |
| 键盘复制模式 | ❌ 不做 | — | — | — | ✅ | — |
| 凭据管理器集成 | 🚧 共享凭据 ✅ · 外部保险库 ⏳ | — | — | — | — | ✅ 自营保险库 |
| 团队共享配置 | 💡 | 企业版 | — | — | — | ✅ |

**结论**：连接与运维这条主线已经追平甚至反超 —— 计量隧道、录制回放、双模插件、AI 协作接入、内置 X 服务端都是别家少有的。
剩下的空档集中在两处：**① 凭据与团队**（凭据管理器、主机证书、共享配置）、**② 文件侧的深水区**（远端搜索、传输队列持久化）。

### A. 终端体验

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟡 P2 | **命令块的「重跑」与「复制这条命令」** | Warp | OSC 133 的其余部分已落地（`plan.md` §10-E）。**只差 `B`（提示符结束的那一列）** —— 有了它才能把命令文本切出来。当时没做是因为列在改列宽重排后会挪位，要像 `ReflowResize` 搬光标那样换算成逻辑行内偏移再跟着重新换行。做这项时连这份代价一起付 |
| 💡 | 🟡 P2 | **终端输出的自定义高亮规则** | WindTerm | 语义高亮是编译期硬编码的 7 条（`SemanticMatcher` 的 `[GeneratedRegex]`：Url / Ip / Error / Warning / Success / Option / Number）。改成可注入规则表 + 设置 UI；注意别拖慢逐格渲染的热路径。**这张用户规则表可与「触发器」「输出脱敏」共用** |
| 💡 | 🟢 P3 | **触发器 / 自动应答** | SecureCRT / Xshell | 全仓没有「输出匹配 → 自动发送」机制。expect 式：输出命中正则时自动发送响应（自动 yes、密码带外输入）。与上一行共用规则表，适合一起做 |
| 💡 | 🟢 P3 | **终端内搜索的增强** | 各家都有 | 基础搜索已有（`MainWindowViewModel.TerminalSearchRequested`）。正则、大小写、全部高亮、上一个 / 下一个的循环计数 —— 按反馈再定 |
| 💡 | 🟢 P3 | **Sixel / Kitty 图形协议** | MobaXterm / WezTerm | VT 引擎有 DCS 通道，缺的是图像解码 + 位图合成进自绘渲染。多日工作量，至今没有用户提过，**等真实需求** |

### B. 会话与工作区

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟠 P1 | **布局持久化与命名布局模板** | Royal TS / mRemoteNG / Tabby | 「打开这一组机器并按这个分屏排好」。单独恢复上次布局意义不大（文档即活动会话），**同一套序列化做成命名模板价值大得多**：VelaDock 的模型层是纯 INPC、本来就可单测可序列化，缺的是 `布局节点 ↔ profileId` 映射、自定义 document 还原器与一个模板列表；「恢复会话」顺带就有了。标签的固定状态（`DockDocument.IsPinned`，`plan.md` §127）眼下只活在本次运行里，做这项时一起存 |
| 🚧 | 🟡 P2 | **按会话自定义标签页图标** | Xshell / Termius | 按协议的默认图标已完成（`plan.md` §68，`Services/ConnectionIcon`）。还差「像挑颜色一样挑图标」：`TerminalOverrides` 加一个图标 id。⚠️ `SessionProfile` 是**逐字段手写拷贝**，新增字段要把几处拷贝都补上（`plan.md` §37 列了名单，漏抄不报错、重开软件字段就没了） |
| ⏳ | 🟡 P2 | **整组批量操作** | MobaXterm | 对一个分组批量连接 / 批量下发命令。同步输入的频道模型（`SyncInputCoordinator`）已经是对等广播，按分组建频道是自然延伸 |
| ⏳ | 🟢 P3 | **标签条上的「+」新建按钮** | Windows Terminal / Chrome | 分屏后「在这一格里新开会话」目前只能靠 `Ctrl+T`（`plan.md` §64 后它已落到活动窗格）。⚠️ 卡点在**分层**：`Docking/` 不认识「会话」，要么 `DockWorkspaceControl` 抛 `NewTabRequested(group)` 由宿主接，要么注入回调 —— **别让停靠层直接 new 一个终端** |
| ⏳ | 🟢 P3 | **纵排标签条的溢出控件** | — | 标签条停到左 / 右侧时，溢出三连钮由 `WidthOverflowConverter` 按宽度判定，因而永不出现；`ScrollLeft/Right_Click` 也只动 `Offset.X`。做的时候连转换器一起改成按轴取值 |
| 💡 | 🟢 P3 | **会话健康巡检面板** | — | 对已保存配置定期探活（复用 `ConnectionDiagnosticsService` 的四步诊断），出一张「哪几台连不上」的表。**别做成定时外呼** —— 由用户显式开启，理由见 `PRIVACY.md` |
| 💡 | 🟢 P3 | **运维编排中心** | — | 设计帧 `bR5c4` 已出，代码零实现，是设计稿里最后一个未落地的面板。有了 AI Agent 与协作接入之后，「编排」的形态可能和当初画的不一样，**先重新界定范围** |

### C. 文件与传输

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟡 P2 | **传输队列持久化** | — | [失败重试](#-p0--存了但不生效的开关)的前提。落点：SonnetDB 文档集合加一份队列快照，和 `recordings` 同样的形状 |
| ⏳ | 🟡 P2 | **远端搜索（find / grep）** | MobaXterm / WindTerm | 复用 `RemoteExec` 通道跑 `find` / `grep`，结果进一个可点击的列表 —— 一条 exec 就够，不必自己遍历目录。注意 `RemoteShellProbe` 的 POSIX 判定：Windows 远端要老实说「不支持」而不是发命令（`plan.md` §18-B/E 的教训） |

### D. 连接与协议

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| 💡 | 🟢 P3 | **SecureCRT 风格的斜杠命令行** | SecureCRT | 外部拉起目前只认 Xshell 的调用约定（`plan.md` §84–85），`/SSH2 /L root /PASSWORD pw host` 被整条忽略。**先确认有没有真实调用方** —— 这层兼容的存在理由是「堡垒机客户端已经在发」，不是补齐对标表；没人发的写法接进来只是多一条攻击面。⚠️ `/` 开头的 token 与 Unix 路径、Avalonia 自己的参数会撞 |
| 💡 | 🟢 P3 | **更多协议插件** | — | RDP / VNC / Kubernetes exec / 数据库客户端。这正是 `Protocols` + `Workspaces` 能力面存在的意义，宿主一行不用改。**优先级交给插件市场的真实下载量决定** |

### E. 安全与合规

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟠 P1 | **录制与日志的输出脱敏** | — | ⚠️ **现实风险，不是洁癖。**「输入脱敏不做」是对的（只录输出、密码无回显），但输出里照样有密钥：`cat .env`、`kubectl get secret -o yaml`、`env \| grep TOKEN`，会话日志与录制存的是原始字节。落点：`SshTerminalBridge.DataReceived` 这条旁路本来就是记录专用，在那里过一遍可配的脱敏规则，**不影响显示路径**。规则表与 A 组的自定义高亮共用 |
| 🚧 | 🟠 P1 | **插件发布者的信任根** | VS Code / JetBrains | 已有：验签 + 发布者连续性（`plan.md` §57，钉住首装的公钥，换钥 / 去签名要用户看过两个指纹再点头）；市场 `GET /api/plugins/latest` 给出每版的发布者指纹、宿主有只读客户端 `IPluginMarketClient`，**更新路径**已拿它比对（`plan.md` §71）。⚠️ 但这仍是 TOFU：`velashell-identity` 是 OIDC 账号服务，不是公钥注册表。真要闭合，缺一份**由发布者身份背书**的 id ↔ 公钥映射（市场把上传者 `sub` 与公钥绑定并可对外验证），且**装包路径也去查它**。外呼仍只在打开插件管理页时发生，别改成开机外呼（`PRIVACY.md`） |
| 🚧 | 🟡 P2 | **凭据管理器集成** | Termius | **设计已定稿**（velashell-docs [`zh/host/凭据管理器集成设计.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/凭据管理器集成设计.md)，v3）。**已落地第一个凭据来源：本机共享凭据**（`plan.md` §157，#550）—— `CredentialReference` / `ICredentialProvider` / `ICredentialResolver` 契约、仓储不变量、五个构建点接线、解析失败退回登录框，以及设计 §3 的 D1 / D3。**还差外部保险库**：1Password / Bitwarden / KeePassXC 三家 CLI（`CliCredentialRunner`、解锁口令内存 TTL 缓存、`ICredentialUnlockPrompt`），届时给解析补上交互 / 非交互之分（自动重连不许弹解锁框，设计 §5.7）与单次尝试缓存，`CredentialReference` 再加 `Scope` 等字段（JSON 省略 null，零迁移）。⚠️ `ISecretProtector` **不是**那层抽象（它管加密不管来源，见设计 §1）。外部保险库的私钥走 SSH Agent。系统密钥链 provider 等 [`系统密钥链与sudo凭据填充可行性调研.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/系统密钥链与sudo凭据填充可行性调研.md) 里 `IMasterKeyStore` 的 P/Invoke 层。设计 §3 的 D2（私钥口令不受「记住密码」控制）、D4（`SshSession.ConnectionInfo` 持有每一跳的密码）仍在；§16 另有 11 项待确认决策 |
| ⏳ | 🟡 P2 | **主机证书（宿主侧）** | OpenSSH `@cert-authority` | **SSH 库已支持**（`plan.md` §113，velashell-docs `ssh/spec/03-key-exchange.md` §5.5）。宿主还用不上：`VelaHostKeyPolicy` 走自己的信任库（`IHostKeyService`），没有「受信 CA」的概念；证书目前按里面那把钥当普通主机密钥处理，默认算法清单里证书又排在后面，实际谈不成证书。要接：①信任库能存受信的主机 CA（手动添加，或从 `~/.ssh/known_hosts` 读 `@cert-authority`）；②`VelaHostKeyPolicy` 已实现 `IHostKeyTypePreference`（交出已记下的钥的类型，`ssh_plan.md` API-H4），还要在有对上的 CA 时把证书算法排前；③裁决时先用 `OpenSshCertificate.CheckHostCertificate` 验证书，CA 管的主机出示没有担保的钥**不退回 TOFU**（规格 §5.5 第 4 条）；④弹窗与设置页文案，五份 resx |
| ⏳ | 🟡 P2 | **与 OpenSSH 互通：known_hosts 与 `~/.ssh/config`** | 各家都有 | known_hosts 现在只能在设置里看和删，导入 / 导出之后与命令行 ssh 共用一份信任基线（顺带喂给上一行的 `@cert-authority`）。`~/.ssh/config` 的导入已有（`plan.md` §58），反向的**导出**是同一条线 |
| 💡 | 🟡 P2 | **团队共享配置（只读策略分发）** | Termius / Xshell 企业版 | 「运维组长发一份机器清单，组员只读订阅」。Gist 云同步的载荷格式与版本回溯现成，差的是**方向**：现在是「我的多设备漫游」，团队要的是「一处发布、多处只读」。⚠️ 这条会把产品推向企业形态，**先想清楚商业授权边界再动手** |
| 💡 | 🟢 P3 | **FIDO 安全密钥直连硬件（第二阶段）** | OpenSSH `ssh-sk-helper` | 第一阶段（经 agent 用 `sk-ssh-ed25519` / `sk-ecdsa-sha2-nistp256`）已落地（`plan.md` §161）。第二阶段直接驱动硬件：Windows 走 `webauthn.dll`，其余平台要 libfido2 —— 新依赖要过 `src/VelaShell.Ssh/AGENTS.md` 3.3 的许可审查；还要有一把真钥做验证 |
| 💡 | 🟢 P3 | **PKCS#11 与 macOS Secure Enclave 签名器** | OpenSSH `PKCS11Provider` | Windows 的 CNG 签名器已落地（`plan.md` §161），同一个 `ISshSigner` 形状。PKCS#11 要按平台加载厂商的 `.so` / `.dll`（P/Invoke，零反射照守）；Secure Enclave 要 macOS 的 Security 框架。都要真硬件验证 |
| ⏳ | 🟢 P3 | **gssapi-with-mic（Kerberos）认证** | OpenSSH / PuTTY / SecureCRT | SSH 库还没实现（`plan.md` §113 评估）。协议面不大：RFC 4462 §3 的几种报文（60 / 61 / 63 / 64 / 65 / 66），MIC 覆盖 `session_id` 与认证请求头。GSS-API 本身走 BCL 的 `NegotiateAuthentication`（Windows 上是 SSPI，Linux / macOS 上是系统 GSSAPI 库），动手前先确认：①选 Kerberos 包时产出的是不是裸 krb5 机制令牌（不是 SPNEGO 包装），`host@主机名` 在两个平台上怎么写；②有没有可用的 MIC 接口；③凭据委派能否经 `TokenImpersonationLevel.Delegation` 拿到。**卡点在验证**：要 KDC + 配了 keytab 的 sshd + 拿得到票据的客户端，建议用 Docker 起 MIT krb5 KDC + sshd，Windows 域环境另找一台。`gssapi-keyex` 不在范围内 |

### F. 性能与稳定

| 状态 | 优先级 | 项 | 架构落点 |
| :---: | :---: | --- | --- |
| ⏳ | 🟠 P1 | **一键导出诊断包** | 支持成本最低的一项投资。崩溃已落地在 `DiagnosticLog.WriteCrash`，但用户报障时仍要被教着去翻目录。做成「关于页一个按钮 → 收集日志 + 版本 + 平台 + 已装插件清单 → 打成一个包」。⚠️ **先脱敏再打包**（主机名、用户名、路径），**只在本地落盘、不自动上传** |
| ⏳ | 🟡 P2 | **启动时间与内存的回归门禁** | `VelaShell.Benchmarks` 刻意不进 CI（BDN 的抖动当门禁只会天天误报）。折中：不比毫秒数，钉**结构性指标** —— 启动路径上的分配次数、冷启动跨过的模块数；`StartupTrace` / `StartupWarmup` 已在采点 |
| ⏳ | 🟡 P2 | **长时会话的内存与句柄基线** | 隧道有计量、终端积压有高水位、scrollback 有上限 —— 三条边界各自都在，**但没有「挂 24 小时会怎样」的端到端证据**。做一个长跑冒烟（放 nightly，不进 PR 门禁） |
| 💡 | 🟢 P3 | **无障碍（屏幕阅读器）** | 全仓只有二十来处 `AutomationProperties`。对「键盘优先」的产品落差比看上去大，但补齐是**跨全部视图**的工作量，先评估真实诉求 |

### G. 生态与分发

| 状态 | 优先级 | 项 | 架构落点 |
| :---: | :---: | --- | --- |
| ⏳ | 🟠 P1 | **协议插件 / 工作台插件模板** | `dotnet new velaplugin` 现在给的是通用模板。Telnet / 串口 / Redis / S3 / DockerPanel 五个插件已经把 `Protocols` 与 `Workspaces` 走通，把公共骨架沉淀成模板，第三方接新协议的成本会掉一个数量级 —— 生态能不能长起来的关键杠杆 |
| ⏳ | 🟡 P2 | **插件日志查看** | `STATUS.md` 已列为未做。管理页能启停、能撤授权，唯独看不了某个插件的日志尾部 —— 用户报「插件不工作」时第一件要问的东西 |
| ⏳ | 🟡 P2 | **能力域补口：`localFs` / `net`** | `STATUS.md` 列为未开口。有了它插件才能做「本地文件 ↔ 远端」（如 DockerPanel 把容器里的文件拖到桌面）。⚠️ 开口就是**权限面变大**，必须同步进 `PluginPermissionGate` 的逐项授权，apiLevel 只增不改 |
| ⏳ | 🟡 P2 | **插件往消息中心发通知** | SDK 的 `IUiApi` 只有 `ShowPanelAsync`，插件发不了通知；宿主侧的接口形状已按这个用途定好（`plan.md` §20）。要开放得改 velashell-plugin-sdk + 扩展隔离进程 IPC + 走 SDK 发版流程，是独立一批。⚠️ 消息中心只收「要留存、可回看」的东西，插件通知同样守这条边界 |
| 💡 | 🟡 P2 | **插件互调 / 插件间事件** | AI 插件能调 DockerPanel 的能力，「让 agent 帮我看看那个容器为什么起不来」就通了。**先想清楚权限模型再设计接口** —— 插件 A 借 B 的手绕过自己没拿到的授权，是这类设计最容易出的洞 |

### H. 内置 X 服务端

> 第二次全库审查(`plan.md` §167,草案 `xs_plan.md`)修完 184 条问题、2026-10-09 补齐只做了一部分的几项(§175)之后剩下的。新功能的编号沿用草案第五节(F1–F30)。

| 状态 | 优先级 | 项 | 架构落点 |
| :---: | :---: | --- | --- |
| ⏳ | 🟠 P1 | **X 程序清单与强制结束的界面**(F3) | 库已经就绪(`plan.md` §167):`GetClientsAsync` / `XClientInfo`、`DisconnectClient`、`KillTopLevelClient`、`BreakGrabs`、连接标签(SSH 会话的 `user@host:port`)。差标题栏 X Server 按钮的浮层:列出「谁连着、来自哪个会话、占多少内存」,能断开单个程序、能解除卡住的抓取与冻结;GrabServer 被占太久时提示(库已记日志点名持有者)。五份 resx |
| 💡 | 🟡 P2 | **按连接给信任级别 / 每个会话一个显示**(F2 / F1,决策 Q5) | 现在所有转发来的会话共用一个受信的显示,`RestrictForwardedClients` 是全局开关。F2:按连接给信任级别,在分派层做访问检查(隐藏 XTEST、原始事件、跨客户端 GetImage);F1:每个 SSH 会话一个显示号,彼此看不见。默认值要拍板 |
| 💡 | 🟡 P2 | **草案第五节的其余功能**(F4–F30) | 本机输入法组好字上屏(F5,决策 Q1 的第一步)、宿主光标跟着 Warp(F8)、屏保 Suspend 转告宿主(F9)、更宽的边缘缩放区(F11)、可选的「一个大窗口」模式(F13,Q2)、GL 的选择 / 反馈 / 求值器(F23)等,逐项评估 |
| ⏳ | 🟢 P3 | **审查留下的部分完成项** | 点本机窗口或桌面就收起 X 的弹出菜单(WN-M7):要全局指针钩子,各平台各写一份。另有审查之前就有的一处:外接框宽或高为 0 的宽弧只画出一半线宽 |
| 💡 | 🟢 P3 | **需要实机核对的** | 分数缩放下最后一列像素可能被裁(API-H13);macOS 上 Command 组合键收不到 KeyUp 时的处理(IN-E19);macOS / FreeBSD 经 `getpeereid` 取对端 uid(CN-S8) |

---

## 三、文档待同步（velashell-docs）

> 按 [`AGENTS.md`](AGENTS.md) 第二条：**改了行为就同步改文档，两个 PR 互相引用、一起合**；`zh/` 与 `en/` 两棵树一起改。
> 同步完成的行直接删掉，不留 ✅。

| 出处 | 要改什么 | 进度 |
| --- | --- | --- |
| `plan.md` §175 X 服务端第二次审查遗留项的收尾 | `{zh,en}/xserver/design/architecture.md`:§3 净室第 4 条(随库带的字体数据与许可)、§4 `Fonts/`、§6 光标的图像、§7 字体(整套 X.Org 位图字体与 Unifont、字符集派生、后台建字体)、PolyArc 的接头、RENDER 的整数路径与 alpha-map、GLX 单缓冲配置、XFIXES 的光标图像,§10 决策记录;`{zh,en}/xserver/troubleshooting.md` 有意的行为一行、已修缺陷两行 | [velashell-docs#101](https://github.com/VelaShellLabs/velashell-docs/pull/101) 已开,与宿主 [#591](https://github.com/joesdu/VelaShell/pull/591) 一起合 |
| `plan.md` §118 窗口外框 | `{zh,en}/host/architecture.md` §5「窗口壳」的 ⚠️ 限定为 Win32、新增「各平台的外框」；`交互与界面规格.md` §2 补 macOS 红绿灯与各平台外框；`design-specs.md` 补 macOS 红绿灯；标题栏统一 28 的口径（设置窗口与消息框保持 48 的例外） | [velashell-docs#70](https://github.com/VelaShellLabs/velashell-docs/pull/70) 已开，与宿主 PR 一起合；实机验收后改掉 architecture 里「验收」那一段 |
| `plan.md` §74 / §75 目录比较与同步 | `SFTP双栏与WinSCP差距分析.md`（C1 改已实现、新增第七节）与 `交互与界面规格.md` §6（文档工具条、同步窗口、保持远端最新、SHA-256 优先比较） | [velashell-docs#35](https://github.com/VelaShellLabs/velashell-docs/pull/35) **待合入** |
| `plan.md` §82 #474 | `交互与界面规格.md` 资源管理器补**置顶**与 SFTP 路径栏的**复制当前路径**；`设置项审计.md` 补 `General.CollapseGroupsByDefault`、`Transfer.UseRecursiveDeleteCommand`（写明只对有 exec 通道的 SSH 会话生效、失败自动回退、没有逐条进度） | 已在 `docs/474-explorer-sftp` 分支改好（中英各 3 个文件），**待开 PR** |
| `plan.md` §164 面向 FIPS 的后量子混合密钥交换 | `ssh/spec/03-key-exchange.md` §3.7（新的一节，含对 AlmaLinux 10.2 OpenSSH 的核对结果）、§8.1、§九；`ssh/spec/00-overview.md` 的算法表、默认清单顺序与 FIPS 预设；`ssh/spec/08-failures.md` §6 的断开原因码（中英两边） | [velashell-docs#91](https://github.com/VelaShellLabs/velashell-docs/pull/91) 已开，与宿主 [#563](https://github.com/joesdu/VelaShell/pull/563) 一起合 |
| `plan.md` §163 agent 加钥的目的地约束 | `ssh/spec/07-forwarding.md` §7.3.2（新的一节，含对真 agent 的核对结果）、§7.3 的约束表与 §7.4 的指引；`ssh/getting-started.md` 的用法示例，以及「证书加钥暂不支持」这句过时的话（中英两边） | [velashell-docs#90](https://github.com/VelaShellLabs/velashell-docs/pull/90) 已开，与宿主 [#563](https://github.com/joesdu/VelaShell/pull/563) 一起合（§161 / §162 的那一批已随 [velashell-docs#89](https://github.com/VelaShellLabs/velashell-docs/pull/89) 合并） |
| `plan.md` §117 SSH 库 API 整改 | `ssh/getting-started.md` 示例改用新公开面；`ssh/design/architecture.md` §6、§8 对上代码；`ssh/spec/08-failures.md` 补新增的 `SshFailureReason` 值与 `SshHostKeyVerdict.Reason` | 已在 `fix/ssh-api-cleanup` 分支备好（本地工作树，未提交），**待开 PR** |
| `plan.md` §129 插件文件协议进双栏（#524 后续） | `交互与界面规格.md` §3（能进双选的类型）、§6.2（插件栏、续传核实不了按冲突处理）；`SFTP双栏与WinSCP差距分析.md` 8.2；`sdk/sdk-reference.md` 版本表把 `IProtocolStreamUpload` 那行的 TBD 换成 2.0.6。中英两棵树都改了 | [velashell-docs#75](https://github.com/VelaShellLabs/velashell-docs/pull/75) 已开，与宿主 PR 一起合 |
| `plan.md` §125 PTY 像素尺寸 | `{zh,en}/host/architecture.md` §9 连接时序图：`PtySizeChanged(cols,rows)` 那一行改成带物理像素、落到 `window-change` | [velashell-docs#72](https://github.com/VelaShellLabs/velashell-docs/pull/72) 已开，与宿主 PR 一起合 |
| `plan.md` §63 SSH 证书认证 | `zh/host/架构设计.md:37` 与 `交互与界面规格.md:451`（及英文镜像 `architecture-design.md:37`、`interaction-and-ui-specs.md:463`）的认证方式口径还停在「密码 / 私钥」。要写：证书 + 私钥是**两件套**、选完证书按 `-cert.pub` 自动补私钥、**证书路径留空是硬错** | 未开始 |
| `plan.md` §86 / §87 密钥生成 | `交互与界面规格.md` 密钥管理页：工具栏多了算法下拉（Ed25519 默认 / ECDSA 256·384·521 / RSA 4096，位数刻意不给选），自动命名随算法走（`velashell_ed25519` 等，重名加 `_2`）；`架构设计.md` 若有「只能生成 RSA」一并改 | 未开始 |
| `plan.md` §51 远程编辑 | `host/` 补：双击 / 「打开」/「使用默认编辑器打开」三个入口的语义差别、自动回传规则、`双击文件时` / `编辑后自动上传` 两个设置项、`~/.velashell/logs/remote-edit.log` 诊断日志 | 未开始 |
| `plan.md` §57 发布者连续性 | `cli/cli.md` 与 `templates/dev-guide.md` 补「旁装钉不住发布者，宿主无从拦截冒名的覆盖安装」；`交互与界面规格.md` 补管理页那行指纹与换发布者的确认框 | 未开始（`plugins/STATUS.md` 那一格已改准） |
| 插件管理页 | `交互与界面规格.md` **从头到尾没有插件管理窗口这一节** —— 按 §10 / §11 浮层规格的写法补：窗口尺寸、工具条、列表行结构（状态色点、发布者指纹）、行内操作的显隐条件、检查更新 / 全部更新 / 「宿主太旧」提示。⚠️ 是新写一节，别塞进别的 PR 顺手做 | 未开始 |
| `plan.md` §68 标签页协议图标 | `交互与界面规格.md` 补标签条图标口径（SSH / 文件协议 / 插件协议三种字形，本地终端不画）。SDK 的三个图标字段写进 `sdk/sdk-reference.md` | 未开始 |
| `plan.md` §65 防空闲 | 连接对话框「防空闲（秒）」：按间隔往 PTY 送一个 `NUL`、只在真空闲时发、**只按会话没有全局开关**（刻意的） | 未开始 |
| `plan.md` §61 回滚行数 | 调小**当场生效**，超出的历史立刻裁掉、不可恢复；只作用于主屏，全屏程序的备用屏恒无回滚 | 未开始 |
| `plan.md` §124 内置 X 服务端全库审查修复 | `{zh,en}/xserver/design/architecture.md`：§5 线程模型补未执行请求的字节预算、日志放锁之后交出并限流、请求缓冲池化；§7 授权改写（启动时生成 cookie 并写进 `.Xauthority`、Unix 套接字 0600 与对端 uid、SSH 连接器走 `ServeAuthenticatedAsync`）并补各项资源上限与连接建立时限；§10 决策记录。连同 §114 欠下的设计层面提醒：所有 SSH 会话共享一个受信的显示，一台被攻破的远端机能看到、也能操作别的会话里的 X 程序 | [velashell-docs#71](https://github.com/VelaShellLabs/velashell-docs/pull/71) 已开，与宿主 PR 一起合 |
| `plan.md` §131 出厂强调色 | `{zh,en}/host/交互与界面规格.md` §14 外观页那一行：强调色出厂跟随主题，色板前有「跟随主题」按钮 | [velashell-docs#76](https://github.com/VelaShellLabs/velashell-docs/pull/76) 已开（宿主 #531 已合，#532 待合） |
| `plan.md` §132 快捷命令变量占位 | `{zh,en}/host/交互与界面规格.md` 快捷命令（侧栏面板与设置页）补：`{{名字}}` / `{{名字=默认值}}` 写法、发送前的询问框与实时预览、哪些双花括号不算占位（花括号内有空白、`.` 开头、Go 模板的 `end` / `else` 等）、值里的换行压成空格 | [velashell-docs#76](https://github.com/VelaShellLabs/velashell-docs/pull/76) 已开（宿主 #531 已合，#532 待合） |
| `plan.md` §133 keyboard-interactive 动态码框 | `{zh,en}/host/交互与界面规格.md` 补动态码框：何时弹（只有服务端问验证码之类才弹，口令提示用已存的密码代答一次）、非密码认证不回退到口令、纯展示轮不弹、取消 = 不连了（首连撤标签 / 重连记为用户断开）、按 echo 遮罩；`{zh,en}/host/architecture.md` 的认证装配补 `IKeyboardInteractivePrompt` | [velashell-docs#76](https://github.com/VelaShellLabs/velashell-docs/pull/76) 已开（宿主 #531 已合，#532 待合） |
| `plan.md` §134 算法协商可配 | `{zh,en}/host/交互与界面规格.md` 连接对话框「SSH 连接选项」补：「允许老算法」开关（放开哪三个、追加在后）、自定义算法清单（四类、OpenSSH 写法、悬停看默认与另可加、写错时保存灰掉）；协商失败文案多出的那一行（可放开的算法 / 放开也没用）；`settings-audit` 若逐项列了 `SshSessionOptions` 的字段一并补上 | [velashell-docs#76](https://github.com/VelaShellLabs/velashell-docs/pull/76) 已开（宿主 #531 已合，#532 待合） |
| `plan.md` §135 审计日志查看与保留 | `{zh,en}/host/交互与界面规格.md` 安全审计页补「审计日志」一节（查看按钮、保留天数）与审计日志窗口（筛选条、列、异常标红、只载入最近 2000 条）；`settings-audit.md` 补 `Security.AuditLogRetentionDays`（默认 180、1–3650、同时管 `conn_history`、只在启动时清理） | [velashell-docs#76](https://github.com/VelaShellLabs/velashell-docs/pull/76) 已开（宿主 #531 已合，#532 待合） |
| `plan.md` §136 动作收进标题栏 | `{zh,en}/host/交互与界面规格.md` 第 89 行一带（全部窗口标题栏统一 28 那条）：资源监视的主机标识与暂停、回放中心的导出 / 刷新 / 清理 / 自动录制已收进标题栏的动作图标组，下面一行只剩副标题；远程编辑的保存（有未保存改动时转强调色）与连接诊断的导出报告 / 重新检测同样收进标题栏（§137），这两扇窗口的第二行只剩远端路径 / 诊断目标；`NceE6` 回放中心那条的「顶栏」措辞对上 | [velashell-docs#76](https://github.com/VelaShellLabs/velashell-docs/pull/76) 已开（宿主 #531 已合，#532 待合） |
| `plan.md` §138 / §140 对话框标题栏 | `{zh,en}/host/交互与界面规格.md` 导入会话与新建连接两个对话框：标题栏与回放中心等独立窗口同一个样子（标题靠左、13px，前面一个 15px 线条图标；27×27 关闭键贴住右上角，悬停的红底随卡片圆角裁掉；仍是固定尺寸的模态框，不放最大化）；新建连接高级选项的 X11 一行改为标签单占一行、显示输入框与「受信任（-Y）」同在第二行各自居中 | [velashell-docs#76](https://github.com/VelaShellLabs/velashell-docs/pull/76) 已开（宿主 #531 已合，#532 待合） |
| `plan.md` §144 远端文件 chown | `{zh,en}/host/SFTP双栏与WinSCP差距分析.md` 3.2 的 chown 一行改成已实现（仅 SFTP、不递归不批量），第五节路线图第 13 条划掉 chown；`{zh,en}/host/交互与界面规格.md` 文件浏览器右键菜单的「权限 chmod」改为「属性」，并补属性弹窗一条（属主 / 属组可编辑下拉、候选来源、数字 id、清空即不改、先 chown 再 chmod、FTP 与插件协议只读） | [velashell-docs#78](https://github.com/VelaShellLabs/velashell-docs/pull/78) 已开，与宿主 #539 一起合 |
| `plan.md` §141 界面语言跟随系统 | `{zh,en}/host/settings-audit.md` §9.2「常规 · 语言」一行：`Language` 出厂为空 = 跟随系统，系统语言对不上五种界面语言时用英文，设置里选过的语言优先；下拉首项为「跟随系统」 | [velashell-docs#77](https://github.com/VelaShellLabs/velashell-docs/pull/77) 已开，与宿主 #537 一起合 |
| `plan.md` §145 新建连接对话框改版 | `{zh,en}/host/交互与界面规格.md` §13.1 重写结构部分（协议栏、分页页签与出现条件表、页签圆点与字段数、常规页分节、认证分段按钮、反馈条、页脚目标预览；「高级选项」相关各条改指到对应页）；`{zh,en}/host/design-specs.md` 新建连接宽 500 → 760；`zh/host/架构设计.md` 与 `en/host/architecture-design.md` 设计稿对照表那一行；`zh/host/S3协议插件化设计.md`、`zh/host/Redis客户端插件化调研与设计.md`、`zh/templates/dev-guide.md` 三处「收进高级选项折叠」改为「放到高级页」。⚠️ `en/templates/dev-guide.md` 本来就没有 `IsAdvanced` 那一段（旧漂移），没补 | [velashell-docs#79](https://github.com/VelaShellLabs/velashell-docs/pull/79) 已开，与宿主 #540 一起合 |
| `plan.md` §146 取色器 | `{zh,en}/host/交互与界面规格.md` 新增 §14.3「取色器」（字段外观、浮层构成、键盘步长、十六进制框只认十六进制且认不出不写回、两种色板来源、清除按钮只在可为空处给、松手才写回、写出 `#RRGGBB`），§13.1 标签颜色与 §14 外观页那两处改指到它；`{zh,en}/host/settings-audit.md` 外观行的展示规则补一句 | [velashell-docs#80](https://github.com/VelaShellLabs/velashell-docs/pull/80) 已开，与宿主 #542 一起合 |
| `plan.md` §152 连接备注（#549） | `{zh,en}/host/交互与界面规格.md` §13.1「整理」补备注一行（多行、回车换行、最多约五行高再长框内滚、去首尾空白、明文且随云同步上传）；§4 资源管理器补悬停显示备注（整行、无备注不弹、12 行 / 500 字符截断） | [velashell-docs#82](https://github.com/VelaShellLabs/velashell-docs/pull/82) 已开，与宿主 #553 一起合 |
| `plan.md` §154 命令面板不再占 `Ctrl+K`（#551） | `{zh,en}/host/快捷键参考.md` 全局表只留命令面板 `Ctrl+P` 一行，表下补一句为什么不绑 `Ctrl+K`；`{zh,en}/host/交互与界面规格.md` §8 标题与搜索框键帽徽章、§4A 那条实现变更备注、键位总表；`{zh,en}/host/architecture.md` 与 `{zh,en}/templates/dev-guide.md` 里的「Ctrl+P / Ctrl+K」 | [velashell-docs#83](https://github.com/VelaShellLabs/velashell-docs/pull/83) 已开（与 §155 同一个 PR），与宿主 #554 一起合 |
| `plan.md` §155 快捷键可自定义（#551） | `{zh,en}/host/快捷键参考.md`（`keyboard-shortcuts.md`）：开头说明表里是出厂键位；维护约定换成新的守门用例；「绑定都写在哪」与「平台差异」改写；新增「自定义键位」一节（可改范围、怎么改、规则、终端冲突提示、`shortcuts.overrides` 存储与绑定 id 表）；跳标签拆成 8 行；删掉早已不存在的「过滤会话 `Ctrl+Shift+E`」一行；英文版补上一直漏掉的关闭全部标签、跳标签、清屏与三个字号缩放。`{zh,en}/host/settings-audit.md`：C-10 / A-08 改为已完成，§6 / §9.2 两行与阶段勾选项对上，补「第十批」整改记录。`{zh,en}/host/交互与界面规格.md`：设计稿对照表的快捷键行、§8 命令面板右侧键位跟随当前键位、§16 标题与跳标签那一行。`zh/host/架构设计.md` / `en/host/architecture-design.md`：「明确不做清单」删掉自定义快捷键并注明推翻 | [velashell-docs#83](https://github.com/VelaShellLabs/velashell-docs/pull/83) 已开，与宿主 #554 一起合 |
| `plan.md` §157 共享凭据（#550） | `zh/host/凭据管理器集成设计.md` 升 v3：内置共享凭据已落地、与 v2 决策的出入（用户名规则取代 `OverrideUsername`、`ResolvedCredential` 带认证方式与私钥、解析交回副本、跳板失败不退回登录框、同步载荷版本 3）、阶段 0a 的 D1 / D3 勾掉；`{zh,en}/host/交互与界面规格.md`：设置页加「共享凭据」、§13.1 身份验证补「凭据来源」、登录框补共享凭据说明条与「同时更新共享凭据」；`zh/host/架构设计.md` / `en/host/architecture-design.md` 与 `{zh,en}/host/architecture.md`：SonnetDB 集合补 `shared_credentials`、连接时序图补「带引用的配置连接时才解析」；`{zh,en}/host/README.md` 那篇设计文档的一行简介 | [velashell-docs#85](https://github.com/VelaShellLabs/velashell-docs/pull/85) 已开，与宿主 PR 一起合 |
| `plan.md` §158 快捷命令覆盖层、多行与拖动排序（#555） | `{zh,en}/host/交互与界面规格.md` §14 设置页表「代码片段」一行，新增 §14.4（内置命令可改可删、恢复默认与恢复内置命令；多行命令走括号粘贴、远端没开时先确认、沿用多行粘贴确认开关、不进补全；拖动排序的手柄、插入线、落点规则、「未分组」垫底、拖出列表取消、自动滚动、**没有键盘替代**；侧栏只显示首行加「+N 行」）；`{zh,en}/host/architecture.md` 快捷命令仓储那段补覆盖层与组内顺序表的存法、同步只加字段不抬版本 | [velashell-docs#86](https://github.com/VelaShellLabs/velashell-docs/pull/86) 已开，与宿主 PR 一起合 |
| `en/` 树 | `zh/` 有 **8 篇** `en/` 里没有：Redis 调研、S3 两篇、系统密钥链调研、凭据管理器集成设计、三份 `release-process.md`。缺口已在 `en/host/README.md` 与根 README 逐篇列出（不再是静默漂移），翻译本身仍欠着 | 未开始 |

---

## 四、确认不做

> 每一条都附了理由。**再提之前先读理由** —— 这些不是「还没排上」，是「评估过，与当前架构或产品决策冲突」。

| 项 | 不做的理由 |
| --- | --- |
| **快捷命令点击后自动执行** | 维护者决策（#555，2026-10-03）：很多命令要在终端里改一改参数才能正确执行；发送后由用户自己按回车，本身就是一道二次确认。快捷命令照旧只发正文、不带回车，多行命令也是整段放进命令行等回车（`plan.md` §158） |
| **X / Y / ZMODEM** | 2026-09-14 移除：终端链路上要写大量专门处理，维护麻烦、收益很低，如今几乎没人用。协议引擎、终端路由、设置项、连接级覆盖与命令面板的「发送 / 接收文件」一并删除，传文件统一走 SFTP 面板 |
| **多窗口（新开独立主窗口）** | 与现架构三处硬冲突：①应用是**单实例**（`Program.cs` 命名 Mutex，自更新重启依赖锁交接）；②主窗口是唯一组合根（`MainWindowViewModel` 持有会话 / 布局 / 状态栏全部状态，无状态分片）；③VelaDock **产品决策不做浮动窗口**。多屏需求由五区拖放分屏承担 |
| **VelaDock 浮动窗口** | 产品决策，见上一条 |
| **Mosh** | 远程通道抽象建立在 SSH 流式通道之上（`ISshClientWrapper` / `IShellStreamWrapper`）。Mosh 是独立的 UDP + SSP 协议栈，.NET 无可用实现，接入等于并行维护第二套传输与终端预测引擎。弱网由自动重连 + keepalive 缓解 |
| **捆绑第三方 X 服务端** | X11 转发已落地（`plan.md` §92），标题栏 X Server 按钮默认启动内置的 `VelaShell.XServer`（纯托管、随程序分发、各平台可用，`plan.md` §101、§105），「零安装」已经做到；Windows 上仍可改成拉起用户装好的 VcXsrv。**不做**的是把第三方 X 服务端的二进制打进安装包：安装包与维护面整个变一个量级，与「解压即跑」冲突。重度远程图形需求交给 RDP / VNC 插件 |
| **内置 X 服务端的输入法（XIM）** | 中日韩输入走远端自己的输入法框架（`plan.md` §105） |
| **SSH 的 CBC 加密模式（`aes*-cbc`）** | 维护者决策（2026-10-06，`ssh_plan.md` Q2）：不实现，「允许老算法」里也不加。CBC 配 Encrypt-and-MAC 有长度预言 / 明文恢复攻击（Albrecht–Paterson–Watson 2009），要做得严谨，长度与 MAC 出错的路径得做到不可区分 —— 那是一条要单独评审、又几乎没人用的错误路径；OpenSSH 6.7（2014）起默认就不开 CBC。代价是只剩 CBC 的老设备（老交换机、嵌入式）连不上：经能谈 CTR / GCM 的跳板去连，或者升级设备固件（`plan.md` §162） |
| **SSH 裸 `zlib` 压缩** | 只保留 `zlib@openssh.com`（认证后才压缩）。裸 `zlib` 让口令与签名也进压缩流，未认证的连接方能做 CRIME 类旁路（`plan.md` §107） |
| **键盘复制模式（vi-like）** | 产品决策（2026-09-09）：没见过这种用法，代价却不小 —— 要新起一个**模式态**穿过 `TerminalKeyRouter` 与抢在它前面的 `TerminalTabView.OnPreviewKeyDown` 两层输入路径；进模式必须关 IME、`Ctrl+C` 的三重身份要重定义、还要模式指示器。而主场景已被吃掉：选整条命令输出走 OSC 133 命令块，找文本走 `Ctrl+F`，抢鼠标的程序里走 `Shift+拖拽`。（当初另一条理由「与不做自定义键位冲突，Dvorak / Colemak 上 `hjkl` 的位置是错的」已随 `plan.md` §155 失效；上面几条仍成立） |
| **SFTP 面板内拖拽移动文件** | 维护者决策（#474 回复）：**做过，因为太容易误触发而关掉了** —— 一次不经意的拖动就把文件挪走，用户事后不知道东西去了哪。现有 `DragDrop` 只认本地路径落入与跨面板传输，`DragEffects` 只给 `Copy`。再提之前先想清楚怎么防误触发 |
| **连字（Ligatures）** | 自绘渲染器按**单元格**排版，无法跨字符连字。这是自绘换来渲染控制权的固有代价 |
| **自适应标题栏颜色** | 主窗与全部对话框的标题栏都是自绘的（macOS 上只借用系统红绿灯，`plan.md` §118），这条已失去对象 |
| **系统通知 Toast** | 需要 AppUserModelID 与通知框架。替代：常规页「声音提示」、安全审计页告警通道的「提示音」（`Security.AlertSound`）与消息中心（`plan.md` §20） |
| **输入脱敏（会话录制）** | 只录**输出流**，密码本就无回显，没有要脱敏的对象（输出脱敏是另一回事，见路线图 E 组） |
| **运行时热切终端类型** | TERM 在**连接时**向远端协商，活动会话热切只会让本地仿真与远端 TERM 能力档不一致。「设置修改对新连接生效」即为正确语义 |
| **按会话的独立代理** | 已落地为**应用级全局代理**（`plan.md` §12-10、§79）。别与动态 SOCKS 转发（`-D`）混淆 —— 那是**入站**隧道；四种**出站**模式（none / system / http / socks5）只决定第一跳 TCP 怎么走 |
| **终端渲染的脏行检测 / 子可视化分带** | 2026-09-19 实测否决：纯字母满屏一帧只有 **54 个绘制操作**，逐格工作已被五套跨帧缓存压掉，不存在可被脏行检测救回的「每帧全量重算」。真要省绘制得走子可视化分带，要穿过滚动、选区、搜索高亮、光标层、侧栏、折叠六个子系统，且滚动本身就让全部带失效。见 `plan.md` §81 |
| **终端解码 / 填充的手写 SIMD** | 2026-09-19 逐条实测否决：手写 `Vector128` 填充与手写循环**完全持平**（45.9 ns）；手写加宽 ASCII 只快 24% 且处理不了非 ASCII；泛型 `LastIndexOfAnyExcept` 在 16 字节结构体上反而**慢约 4 倍**。唯一成立的是「换成 BCL 已向量化的重载」（`EchoSuppressor.IndexOf` 16.9x、`Fill` −26%）。**先量再说**，见 `plan.md` §81 |

---

## 🔧 维护约定

1. **一件事只出现在一个文件里。** 做完了就从这里删掉、去 `plan.md` 补一节；`plan.md` 里不留 TODO。
   部分完成的条目，把做完的部分写进 `plan.md`，这里只留剩下的那部分。
2. **写现状要给证据，而且证据会过期。** 「零消费者」这种话要能落到具体文件或符号。
   复核结论本身也会过期：`会话标签自定义颜色` 在 `plan.md` §12-13 里被复核成「用户不可选」，次日（`b9ae31f`）就落地了。
   接手时请一样地核一遍，不要照抄结论。
3. **先证伪再排期。** 排进「建议的下一步」之前，先确认它确实还没做、前提确实还成立。
4. **新增设置项必须当场接线**，否则就进 P0 表，变成下一个「界面在骗人」。
5. **别在宿主里做插件能做的事。** `Protocols` / `Workspaces` 已经验证过五遍，「加一种连接类型」的默认答案是插件。
6. **凡是要外呼的功能**（健康巡检、诊断包、团队订阅）**默认关**，并同步改 `PRIVACY.md` —— 默认行为变更就是文档变更（`plan.md` §22）。
7. **欠账与路线图不要混。** 前者是「说了没做到」，后者是「还没说要做」，优先级不在一个量纲上。
   路线图里的一条决定开工后，把它移进「一、欠账」对应的主题表并给出架构落点。
8. **「确认不做」只增不减，除非架构真的变了。** 它的价值在于别人不用把同一个评估再做一遍。
9. **改了行为就同步 velashell-docs**（`AGENTS.md` 第二条），欠下的登记到「三、文档待同步」。
