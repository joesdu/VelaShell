# VelaShell 特性计划

> **这份文件记录「还没发生的事」** —— 待办、候选特性，以及已经决策不做的清单。
>
> **已经发生的事在 [`plan.md`](plan.md)** —— 已完成的工作、当前架构、每一次改动的来龙去脉。
> 两份文件的分工是硬的：一件事做完了，就从这里删掉、去 `plan.md` 补一节；
> 一件事还没做，就不要在 `plan.md` 里留 TODO。
>
> **2026-10-31 补记**:复审 #669 – #698(`plan.md` §298)查出、这次没改的六项各补一行(一、欠账:安全与凭据两行、文件传输两行、终端与 SSH 两行),「FIPS 预设的互操作核对」一行补一句,都待维护者定;其余各行这次没有复核。
>
> 最近复核:**2026-10-25**(第二次)。维护者 2026-10-25 对 31 项按推荐拍板之后,这一批做完的条目(`plan.md` §250 – §269)逐条对着 `src/` 与各仓库的本地 dev 核过后删掉:
> 安全与凭据表里的五行(手册三处说法、诊断包、会话日志遮盖、#655 两项、配置审计);文件传输表里的五行(远程编辑口径、续传断线、导入备份清收藏、远端搜索、外部编辑副本);
> SSH 库限速突发、中文诊断(续)、ssh_config 剩余映射;AI 插件的安全边界、换源清 Key 残留、订阅换源、Anthropic 环境变量;「+」的显式落点;路线图里的输出高亮、触发器、布局模板同步、会话健康巡检与一键放开算法推广(路线图先排的四项全部做完);「三」里 `manual.md` 的去留(维护者定留在本仓库,已写进 AGENTS.md「留在本仓库的文档」,21b49851)。
> 只做了一部分的只留剩下的部分、现状指向新节号:主密码保护只剩第二步(可选主密码),SFTP 双栏的接线债只剩要定口径的四项,AI 上传、AI 传输安全提交残留、#651、Anthropic、#582、#616、FIPS、runtime-async、SSH 库已有能力等行各留剩下的。依赖 SDK 新契约的几行标成「SDK 契约已写好(velashell-plugin-sdk 本地 dev),待发版后宿主接」。
> 维护者定了暂缓 / 维持现状的写进各自的行、没删:对外 MCP 的带外审批与「远程 + 远程」同步窗口暂缓,拆 SSH 库两个上帝类排在这一轮之后,AI 传输安全提交残留的 ②③④ 维持现状。
> 新增的条目来自这一批各组记下的遗留与取舍,其中还要维护者拍板的写成「待维护者定」。一、欠账里「🗂️ 会话与工作区」那张表这一轮清空了,整节拿掉。
>
> 之前:**2026-10-24**。这一批改动涉及的条目逐条对着 `src/` 核过:四个 P0 开关里自动下载更新、传输失败重试、标签栏位置已接线(`plan.md` §241 / §238 / §235),
> 只剩主密码保护;`SessionProfile.Tags` 已由资源管理器的过滤框与命令面板读取(§240);SSH 库 §161 那一批能力宿主接了一部分 ——
> 转发的实时吞吐 / 连接快照 / 限速、远程动态转发与放行名单、Unix 域套接字转发(§244)、按键时序混淆(§248),其余仍没接。
> 这次删掉的:`plan.md` §235 – §248 做完的条目(上一轮「建议的下一步」三条都在其中;模板那一条只剩发版);
> 「三、文档待同步」里已由 velashell-docs 文档组同步的 9 行。新增的条目来自这一批各组记下的遗留与取舍。
> **2026-10-25 第一次再核,删掉已经做完的**:协议 / 工作台插件模板(velashell-plugin-templates#11 已合入,模板包 2.0.2 已发到 NuGet);
> 「Code scanning AI findings 一直失败」(#670 / #671 的检查里已经没有这一项)。改写成只留剩下部分的:终端内搜索(全部高亮、循环与计数早已有,只剩正则与大小写)、
> 生态侧几处的 ③(docs 那半已改,只剩模板仓库)、「三」里的既有漂移(en dev-guide 的 §5.13 / §5.14 与 `cli.md` 断锚点已补)。其余各行仍未做:多数在等维护者拍板、发 SDK 或实机验收;
> 可能被后来的提交顺手做掉的几行(AI 插件的只读放行与 `BareModelId`、SDK 的通知 / 版本比较 / `localFs`、`release.yml` 的 `+meta`)抽查了代码,仍成立。
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

按「杠杆 ÷ 成本」排，下面三件最值得先动：

1. **发一版 SDK,宿主跟上**(一、插件生态 / 文件传输,二、G)—— `plan.md` §260 的五项契约与 §261 的测试替身修复都只在 velashell-plugin-sdk 的本地 `dev` 上,没推送、没发版;一次发版解开六行:插件协议的文件能力补口、插件往消息中心发通知、版本比较口径、AI 上传 ①、`RpcConnection` 半帧、SDK 测试替身的注册句柄。先推送开 PR、由维护者定版本号发版,再抬 `src/Directory.Packages.props` 与 `tests/Directory.Packages.props`,照各行写的宿主后续逐项接(AGENTS.md 的版本纪律:不造本地包、不猜版本号)。
2. **拆 SSH 库的两个上帝类**(🟡 P2,一、终端与 SSH)—— 维护者 2026-10-25 定等这一轮 SSH 改动合入之后单独一个 PR 做;§251 / §254 / §259 都已在 dev 上,前提满足。纯重构,`VelaShell.Ssh.Tests` 与互操作用例是现成的安全网,行里已列出拆的时候不能断的七处耦合。
3. **跨平台实机验收**(🟠 P1,一、窗口与外观)—— 这一轮又多了只经 CI 编译的平台代码:本机加密密钥在 macOS 钥匙串 / Linux Secret Service 上的保管与迁移(§253)动的是已存密码的解密路径,真机没跑过一次;趁着跑一遍窗口外框,把 `VELASHELL_KEYCHAIN_TESTS=1` 的往返用例与升级迁移一起做掉。

## 📊 待办分布

| 部分 | 🔴 P0 | 🟠 P1 | 🟡 P2 | 🟢 P3 | 合计 |
| --- | :---: | :---: | :---: | :---: | :---: |
| 一、欠账 | 1 | 1 | 12 | 28 | **42** |
| 二、路线图 | — | 1 | 8 | 25 | **34** |
| 三、文档待同步 | — | — | — | — | **3** |

---

## 一、欠账

### 🔴 P0 —— 存了但不生效的开关

> 共同点：**字段已持久化、设置页可能还展示着，但运行时零消费者**。用户拨了开关以为生效了，其实什么都没发生。
> 闭合有两条路，二选一：**接上运行时**，或**从界面撤下并在 `AppSettings` 注释里写明原因**。
> 统计口径见 velashell-docs 的 [`zh/host/settings-audit.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/settings-audit.md)。

| 状态 | 项 | 字段 | 现状 | 闭合要做什么 |
| :---: | --- | --- | --- | --- |
| ⏳ | **主密码保护** | `AppSettings.MasterPasswordProtection` | 零消费者;UI 已撤下并留注释 R-04。第一步(本机加密密钥交给系统密钥链:macOS 钥匙串 / Linux Secret Service,Windows 仍是 DPAPI)已做(`plan.md` §253) | 只剩第二步:可选主密码 —— Argon2id 派生密钥包裹本机密钥、解锁框与锁定态、开启 / 关闭 / 修改 / 忘记、自动重连与后台任务不弹框、插件 `ISecretsApi` 的「已锁定」语义(要 SDK 新契约)。设计草稿见 velashell-docs [`zh/host/主密码保护设计.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/主密码保护设计.md);⚠️ 草稿 §9 的六个决策点待维护者定(解锁时机、自动锁定默认值、恢复码、`ISecretsApi` 的「已锁定」语义、Windows Hello / Touch ID、低端机器上的 Argon2id 参数),定了再动手 |

### 🔒 安全与凭据

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟡 P2 | **连接时认不出解不开的已存密码** | `ISecretProtector.Unprotect` 解不开时原样返回密文(历史契约;`TryUnprotect` 已能如实说,`plan.md` §253)。本机加密密钥换过、条目找不到时,连接会先拿 `enc1:` 密文当口令试一次,认证失败才弹登录框 —— 白白浪费一次认证,可能撞上 fail2ban;插件机密解不开时插件拿到的同样是密文 | 在 `RequiresCredentials` 一类判定处用 `TryUnprotect` 的结果直接弹登录框;插件机密要不要改成「解不开」的明确结果(不能改成 null —— 插件会以为不存在、让用户重填覆盖旧值),随主密码第二步的 `ISecretsApi`「已锁定」语义一起定 |
| ⏳ | 🟢 P3 | **代理密码落盘明文** | 设置文档里的 `ProxyOptions.Password` 不经 `ISecretProtector`(`SettingsCategories.SecretFields` 的注释写着「代理密码是明文存的」);设置导出(§237)与无口令云同步(§252)已剔除它,但 `PRIVACY.md`「已保存的密码与口令使用 AES-256 加密」与手册 16.4 都没把它排除在外 | 待维护者定:并进 P0「主密码保护」一起处理,还是单独改经 `ISecretProtector` 加密存(读写在设置服务里,导出 / 同步的剔除不变),或在 PRIVACY 与手册写明例外 |
| ⏳ | 🟢 P3 | **macOS 钥匙串条目改回只认 VelaShell** | 本机加密密钥的钥匙串条目访问控制放开到了「任何应用」(`MacKeychainMasterKeyStore` 的 `CreateOpenAccess`,`plan.md` §253):发行包是 ad-hoc 签名、每版签名都变,默认访问控制会让每次更新后第一次启动都弹授权框要登录密码。放开后与原先 0600 的文件同级,挡住的是「拿走数据目录」,不是「本机上以你身份运行的程序」 | 待维护者确认这个取舍;有了稳定的 Developer ID 签名后去掉「任何应用」那一路(删掉 `CreateOpenAccess`) |
| ⏳ | 🟢 P3 | **临时连接登录框里的「记住密码」勾了也不落盘** | 外部拉起与「最近连接」重建的临时配置不再落库(`plan.md` §252),这类连接的登录框里照旧显示「记住密码」勾选框,勾了什么都不存 —— 需要长期用的得在连接配置里另存一条 | 待维护者定:临时连接上隐藏这个勾选框,还是改成「另存为连接」 |
| ⏳ | 🟢 P3 | **会话日志与录制遮盖的剩余误遮与漏遮** | 遮盖改成先剥控制序列再匹配、关键词要成词之后(`plan.md` §258),仍会误遮的:`token_type: Bearer`、`token_endpoint: https://…`(OIDC 发现文档)、`secretName: tls-cert`(Kubernetes)、`token_count: 12`;没覆盖的常见格式:GitLab `glpat-`、Stripe `sk_live_` / `rk_live_`、Google `AIza…`、npm `npm_…`、PyPI `pypi-…`,INI 风格带空格的 `password = my pass phrase`(仍按 `=` 规则到空白为止);口令本身含「词: 词」(`correct horse: battery`)时 `名字: 值` 会停在 `horse:` 前 | 按 §258 同样的口径(每条都有「该遮的」与「不该遮的」用例、逐字节切分对照)逐条评估误报率;误遮那几个要给 token / secret 加一张「非机密后缀」表 |
| ⏳ | 🟢 P3 | **Xshell / `ssh://` 外部启动不产出证书字段** | `~/.ssh/config` 导入的 `CertificateFile` 与外部启动的 PuTTY `-cert` 已认(`plan.md` §242)。剩下:Xshell 6 起支持 OpenSSH CA(实验性),但 `.xsh` 与命令行的证书字段没有公开文档;`ssh://` 没有标准的证书参数 | 等拿到真实的 `.xsh` / 堡垒机样本再做,不猜字段名;`ssh://` 不做 |
| ⏳ | 🟡 P2 | **「在此处打开终端」:C 区域设置下的非 ASCII 目录名** | 带控制字符的目录名已不再敲进 PTY(`plan.md` §298),但 C / POSIX 区域设置下 bash 的 readline 把 ≥ 0x80 的字节当 Meta 键(`£` = C2 A3,A3 即 M-#,会提交整行),精心构造的非 ASCII 目录名仍可能注入 | 稳妥做法是只发可打印 ASCII(`cd "$(printf '\ooo…')"`,各 shell 的写法要逐一核),或非 ASCII 目录名一律提示不 cd(会挡掉正常的中文目录);待维护者定 |
| ⏳ | 🟢 P3 | **钥匙环解锁框被取消时报成「本机没有这把钥」** | Linux Secret Service 上取消解锁框,libsecret 大概率返回 NULL 而不带错误,`SecretServiceMasterKeyStore` 映射成 NotFound:提示不准、多弹一次解锁框,不丢数据(`plan.md` §298) | libsecret 文档没写清锁着的集合与取消的提示各返回什么;在真机钥匙环上确认后再改(可能要 libsecret ≥ 0.19 与新的 GObject 互操作) |

### 📁 文件传输

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟡 P2 | **AI 上传的几项口径(#656 复审)** | 上传提交转发、失败与取消的判定已修(§231);快照放在源文件同卷并先查空间、已打开的源按文件身份核验、停止宽限期 30 秒、断线终态维持失败(`plan.md` §257);停止或拒绝之后撤掉本轮还在等确认的传输(§263);传输失败原因里的路径随诊断包的口径定了(随默认收的诊断日志进包,本机家目录换 `~`、别人的家目录换名字段、其余路径原样,§255)。剩下:①宿主在登记前就明确拒绝(源找不到、被占用、配额满)时,插件当成「结果未知」或「用户拒绝」—— **SDK 契约已写好(velashell-plugin-sdk 本地 dev),待发版后宿主接**:`PluginTransferRejectedException` + `RemoteTransferRejectReason`,错误码 `transfer-rejected:<原因>`(§260);②下载确认框不显示大小(上传的已显示,§257;远端大小在审批前已能 `GetFileInfoAsync` 拿到) | ①SDK 发版、抬包之后:`RemoteFsCapability.Transfers.StartUploadAsync` 在 `_receipts.Add` 之前的拒绝改抛新类型 —— 源找不到(`FileNotFoundException` / `DirectoryNotFoundException`)→ `SourceNotFound`,共享冲突 / 锁冲突(Windows HResult 0x80070020 / 0x80070021)→ `SourceInUse`,`ThrowIfUploadQuotaReached` → `QuotaExceeded`;`StartDownloadAsync` 的额度检查同样改;OperationId 冲突、链接、特殊文件照旧。⚠️ §257 的快照空间检查在登记之后(审批之后、拷贝之前)才做、以 `Failed` / `insufficient-space` 收尾,不是「启动前被拒」,`InsufficientSpace` 这个原因宿主眼下用不上(挪到登记之前会误拒能克隆的卷)。AI 插件 `FileTransferJobRegistry.IsDefiniteRejection` 认新类型(它不是 `IOException`,`ReceiveStartAsync` 不会再按「回执丢了」去重取),给模型的提示用 `Reason`,插件清单的 `minSdkVersion` 跟着抬;②`RequestFileDownloadAsync` 照上传的改法多一个大小参数 |
| 🚧 | 🟡 P2 | **SFTP 双栏与 WinSCP 的剩余差距** | 接线债(velashell-docs《SFTP双栏与WinSCP差距分析》A 类)对照代码核过一遍、补了五项(`plan.md` §250),只剩四项、都要先定口径:A3 传输浮窗单条取消(命令在,但 `CancelTransfer` 只改状态、通知的是没人用的 `TransferManager`,传输本身停不下)、A4 `TransferManager` 整套零调用点、A9「下载为…」(`DownloadItemCommand` / `PickSavePathForDownload` 没入口)、A12 从系统拖文件进本地栏只刷新(却显示可放下)。能力缺失与架构级的逐项清单(含成本)在 velashell-docs 的 [`SFTP双栏与WinSCP差距分析.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/SFTP双栏与WinSCP差距分析.md),第一梯队:文件栏键盘快捷键(F2 / Delete / F5 / Ctrl+A)、当前目录过滤框、前进 / 后退 | 待维护者定(组里的推荐):A3 点 × 只停这一项、这一批其余照传、该项出队;A4 删掉(职责已由 `FileTransferViewModel` 的全窗口名额、`RunTransferBatchAsync` 与 `TransferQueueJournal` 接管,与 A3 一起做);A9 只给单个普通文件,另存为对话框里的覆盖确认就算冲突决定;A12 先不显示可放下(系统文件给 None),有人要再做复制;快捷键按资源管理器的口径(F5 刷新、F2 重命名、Delete 删除并确认,与任务管理器一致),登记进 `ShortcutCatalog` 的 SFTP 文件管理器组;过滤框放头部、只过滤显示、换目录清空;前进 / 后退用 `Alt+←/→`、断线重连后历史不保留。⚠️ 目录收藏、失败重试与传输队列、FTP 服务器时区、远端搜索、§250 的五项都已完成,不要再排进来 |
| ⏳ | 🟢 P3 | **AI 传输安全提交的几处残留(#656 / #657 复审跟进)** | macOS / Linux 的下载安全提交与 Windows 目录真上锁已做(§233),覆盖上传的临时文件一建出来就是被替换文件的权限位(§256)。留下:①macOS 只经 CI(arm64、APFS)跑过,x64 的 `$INODE64` 符号、非本地卷的拒绝、exFAT / SMB 上的「不支持」回退、Linux arm64 都没实跑;AI 上传快照的 macOS `fstatat(AT_SYMLINK_NOFOLLOW_ANY)` / `fclonefileat` 与大小写 / 规范化用例同样只经 CI,Linux 的 `FICLONE` 没在真正支持 reflink 的文件系统(Btrfs / XFS)上跑过(§257);②NFS 上的家目录按「不支持」拒绝(与 9p / SMB / FUSE 同一口径,9p 上实测祖先改名后提交没落进钉住的目录);③POSIX 按名字改名,别人可写且没有粘滞位的目录里临时文件名可被调包(Windows 按句柄改名,不受影响);④Windows 下载钉住祖先目录时不共享删除,别的进程以 DELETE 权限或不共享读打开着某一级祖先时下载会失败 | ①在 macOS x64 实机与外接 exFAT / SMB 卷上各跑一遍下载,在 Btrfs / XFS 与 APFS 上各传一次大文件看快照是不是克隆;②③④**维持现状**(维护者 2026-10-25 定,留作观察项):②NFS 照旧按不支持拒绝,要放行先证明它按句柄寻址;③可接受 —— 攻击者本就能换掉最终文件;④有真实报障再定 |
| ⏳ | 🟢 P3 | **插件协议的文件能力补口** | 插件协议上:符号链接如实抛不支持;设不了远端修改时间,双向同步会把刚上传的文件判成远端较新;算不了服务器端摘要(`plan.md` §72、§74、§75)。**SDK 契约已写好(velashell-plugin-sdk 本地 dev),待发版后宿主接**:`RemoteFileEntry.IsSymbolicLink` / `LinkTarget`、可选的 `IProtocolLastWriteTime` 与 `IProtocolChecksum` + `RemoteChecksumAlgorithm`(§260)。插件协议上**创建**符号链接仍没有契约 | SDK 发版、抬包之后:`PluginProtocolFileService` 的 `ToRemoteFileInfo` 映射两个链接字段,`SetLastWriteTimeAsync` 在协议实现了 `IProtocolLastWriteTime` 时把 UTC 转成 `DateTimeOffset` 调它(没实现照旧报不支持),`ComputeSha256Async` 在实现了 `IProtocolChecksum` 时按 Sha256 请求(缺的键补 null,不是 64 位小写十六进制的值丢成 null),`UploadStreamAsync` 里「修改时间不设」的注释改掉;`RemoteFsCapability.Map` 补上两个链接字段(插件经 `IRemoteFsApi` 也看得到链接,进程内与隔离两种模式都生效);velashell-plugins 里的协议插件(S3 等)按能力选实现。创建符号链接要的话以后另加一个可选接口 |
| ⏳ | 🟢 P3 | **「远程 + 远程」双栏的同步窗口** | 双栏远程文档只有「比较目录」（按大小与修改时间，不做 SHA-256），没有「同步…」与「保持远端最新」（`plan.md` §128） | **暂缓,等有人要**(维护者 2026-10-25 定)。要做时:`DirectorySyncViewModel` / `DirectorySyncRunner` 全是「本地路径 + Upload/Download」的语义，要做成两端对称（两边都用 `DirectoryTreeScanner.ScanRemoteAsync`、动作换成两个方向的 Relay）；「保持远端最新」靠监视本地文件系统，双远程下没有意义，不做 |
| ⏳ | 🟢 P3 | **消息中心里的外部编辑草稿只能打开文件夹** | 崩溃 / 回传失败留下的外部编辑草稿启动时在消息中心逐份列出(`plan.md` §256),动作只有「打开所在文件夹」(`files.remoteEditDrafts.open`):消息中心的动作是一条不带参数的已注册命令(`NotificationLink.CommandId`),重传要先连上那台服务器(会话随上次运行一起没了)再按这一份传 | 给 `NotificationLink` 加参数(或按条注册临时命令),按标记 `.vela-edit.json` 里的连接配置 Id 找已连着的面板 / 打开文件标签后上传到记着的远端路径(冲突照常询问),成功后删草稿、撤消息 |
| ⏳ | 🟢 P3 | **非 UTF-8 字符集下 SFTP 名字不能无损往返非法字节** | 会话字符集是 GBK / Big5 / Shift_JIS 时,名字里在该字符集下不合法的字节解不回原样(spec 06-sftp §4.7 明确这样定过),混用编码的服务器上这类文件打开、改名、删除会报「不存在」(`plan.md` §298) | 改成无损(如 U+DCxx 逃逸,与 UTF-8 会话同一条规则)要先改规格;待维护者定 |
| ⏳ | 🟢 P3 | **FTP 上传传到一半被复位不自动重试** | FluentFTP 把数据连接中途复位包成 `FtpException`,`FluentFtpInterop.Translate` 归为 `VelaFtpOperationException`(永久失败),#687 之前就是如此(`plan.md` §298 只让内部重试不再误判它) | 要自动重试得改 `Translate` / `Fault` 的分类,而那样每次数据复位都会把会话标成 Faulted(灰点);待维护者定 |

### 🖥️ 终端与 SSH

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟡 P2 | **拆 SSH 库的两个上帝类** | `SshConnection` 约 4,900 行（`Session/SshConnection*.cs`，`SshConnection.cs` 自己约 2,010 行）、`SshChannel` 约 1,800 行（2026-10-25 量,10-13 时是 4,850 / 1,980 / 1,760），远过 `src/VelaShell.Ssh/AGENTS.md` 4.4 的 800 行（`plan.md` §117、§161）。**维护者 2026-10-25 定:等这一轮的 SSH 改动合入之后单独一个 PR 做,这一轮不做** —— 这一轮的 SSH 改动(§251、§254、§259)已都在 dev 上 | 拆成 internal 协作者而不是更多 partial：`SshChannel` 的收发窗口与 stdin 泵、`SshConnection` 的收包分发与全局请求账本。**纯重构，单独开 PR**，`VelaShell.Ssh.Tests` 与互操作用例是安全网。⚠️ 拆的时候这几处耦合不能断（2026-10-05 审查的备忘）：①入队锁里的 admit 回调跨两个类 —— `MayStillSend` / `TryCommitClose` / `OnClose` / 账本 `Register` 必须与 `TryWriteFrame` 在同一把 `_enqueueLock` 里，不能拆成「先查后发」，也不能变异步；②锁顺序 `_enqueueLock` → 连接 `_stateLock` → 通道 `_stateLock`，`ReleaseId` 先放通道锁再进连接锁，新协作者不许持通道锁入队；③`FinishClose` 的步骤顺序：锁内改状态 → 事件 → 关账 → 管道 → stdin → 唤醒闸门 → `CancelAsync`；④接收分发会被重入（重协商期间 `RekeyKexTransport.ReadPacketAsync` 回调 `DispatchAsync`），拆出去后仍要能在 KEX 中被调用，且只许投递、不许等发送；⑤预算是两本账：连接的 `_windowBudgetUsed` 归连接锁，通道的 `_budgetCharged` / `_idReleased` 归通道锁，「先申请 → 通道锁里确认 → 失败回滚」三步要保持；⑥`_priority` 只在 `_enqueueLock` 下写、泵每次先读它（回补不越过 CLOSE 与闸门的前提）；交互道 `_interactive` 同样在锁里写，一条通道的帧（含接收循环替它回的 CLOSE 与请求应答）全走开通道时定下的那条道，泵在回补之后让它与普通队列轮流（`plan.md` §162 的 Q7）；⑦`WouldSendNow` 与 `Admit` 必须在泵线程上连续调用 |
| ⏳ | 🟡 P2 | **SSH 库已有、宿主还没接的能力** | `plan.md` §161 在库里落地、宿主还没接的只剩两样:TLS 拨号器(维护者 2026-10-25 定暂缓)与 agent 加钥的目的地约束(`AllowedHops`,`plan.md` §163)。`AllowedHops` 已核实(§259):Win32-OpenSSH 的 agent 对 `restrict-destination-v00@openssh.com` 约束回 `SSH_AGENT_FAILURE`(PowerShell/openssh-portable#612),Pageant 没有实现(PuTTY wishlist `pageant-forwarding-path` 仍是 low / taxing),Windows 上接了只会加钥失败,照决定不接。(CNG 签名器、报文旁路、按连接的 agent 端点见 §259;转发吞吐 / 远程动态 / Unix 套接字见 §244;转发项导入导出见 §242;按键时序混淆见 §248) | TLS 进代理设置:先定 TLS 包在 HTTP / SOCKS 代理之外还是之内、证书校验与 SNI 的口径(全局代理设置的一整块),待维护者定。`AllowedHops` 要不要做成显式开启的选项、对端 agent 不支持时如实说明(受益者是 Linux / macOS / WSL 上 OpenSSH ≥ 8.9 的 agent;CVE-2023-51384 提示 9.6 之前对 PKCS#11 钥的约束应用不完整),待维护者定;不许去掉约束重试 |
| ⏳ | 🟢 P3 | **FIPS 预设的互操作核对** | `FipsApprovedOnly` 已补上群交换与 group18、注释改准(`plan.md` §251)。两种 nistp 混合 KEX 只对 AlmaLinux 10.2(RHEL 10.2 下游补丁的 OpenSSH 9.9p1)核对过,还没与 OpenSSH 10.6、Apache MINA SSHD 对照(§164);`scripts/ssh/interop/kex-gex.sh` 没追加 group18,2229 那台默认不开 DH 的 OpenSSH 10 靶机没覆盖到它 | 补两份实现的互操作核对;`kex-gex.sh` 追加 group18 后在 2229 上跑一次。另有两处 §251 自己定的口径待维护者确认:group18 也进了默认清单(排在 group16 之后、group14 之前;只进 FIPS 清单的话算法目录会说它没实现);加密清单留着 `aes192-ctr`(RHEL 没开,要完全一致就删那一行与用例里的 `.Append(Aes192Ctr)`)。复审 #669 – #698 时查出(`plan.md` §298):群交换接受服务端给的任意素数(只核大小与素性),严格说不在 FIPS 认可的命名群之内 —— 预设里拿掉 `diffie-hellman-group-exchange-sha256`,还是预设下只认 RFC 3526 / 7919 的命名群,待维护者定 |
| ⏳ | 🟢 P3 | **SSH 库剩余的中文诊断(配置不成立、TLS 拨号等)** | 九类透传已按结构化出处出五语文案(`plan.md` §246、§254),`src/VelaShell.Ssh/AGENTS.md` 4.8 列着还剩的:配置不成立(`InvalidConfiguration`:ssh_config 的 ProxyJump 成环 / 跳数超限、ProxyCommand 模板与代入)、`TlsFailed`、`HostKeyStoreFailed`(宿主不走库的 known_hosts)、私钥 / 证书以外的 `Unsupported`、库自带主机密钥策略(`KnownHostsPolicy` / `PinnedFingerprintHostKeyPolicy`)写的拒绝理由 —— 宿主现在都碰不到或很少碰到。另:`ProxyTransportDialer` 在 HTTP 代理 + 22 端口时追加的那句英文提示是写死的;SFTP「流水线已释放」的原因码仍是 `Unsupported`(§254 只加了种类 `SessionClosed`) | 碰到了再照 §246 的做法补(库里补种类与参数,宿主映射);那句英文提示改走 resx;「流水线已释放」的原因码是否改成 `Aborted`(公开构造固定的那个,改了是语义变化),待维护者定 |
| ⏳ | 🟢 P3 | **远程转发「一头 TCP、一头套接字」** | 隧道的 Unix 域套接字(`plan.md` §244)按库实际公开的组合做:远程转发只有「两头 TCP」「两头套接字」;OpenSSH 的 `-R port:/本机/套接字` 与 `-R /服务器/套接字:host:port` 两种库没公开入口(构造函数已能表达) | 先补 velashell-docs `ssh/spec/07` §4.4,再在库里加入口,宿主表单放开远程转发只填一头路径 |
| ⏳ | 🟢 P3 | **ssh_config 里 Windows 管道与 UNC 路径的反斜杠** | 导出把 `IdentityAgent` / `IdentityFile` 里的 `\\.\pipe\x`、`\\server\share\…` 原样写出;本库的解析器不处理反斜杠、读回不变,而 Win32-OpenSSH 9.5 的 `ssh -G` 实测读成 `\.\pipe\x`、`\server\share\…`(`plan.md` §298) | 先在 spec/09 §7 定下库的分词器认不认反斜杠转义,再让导出与解析跟上;待维护者定 |
| ⏳ | 🟢 P3 | **终端标签普通重连用的是标签里的配置副本** | 会话健康巡检已改为每轮从库里重读(`plan.md` §298);终端标签的普通重连仍用 `tab.Profile`,连上之后还把这份副本整份写回库里,别处(云同步、巡检窗口里的「放开算法」)刚改的会被盖掉 | 先定标签副本(可能带着现敲的口令)与库里那份的合并规则;待维护者定 |

### 🪟 窗口与外观

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| 🚧 | 🟠 P1 | **跨平台实机验收** | 全部弹窗与独立窗口已按平台走原生机制（`plan.md` §118，`Views/WindowChrome.cs`）。macOS 上主窗口、设置窗口与消息框已实机验收；**非模态窗口的红绿灯没在 Mac 上看过**；Linux 只在 WSLg 里跑过（Weston 缺 `xdg_wm_base` ≥ 3，Wayland 起不来，测到的只是 X11 分支）。内置 X 服务端的 macOS「自动」键盘布局也只经过 CI 编译与平台无关的单测（`plan.md` §109） | ①**macOS**：任务管理器 / 资源监视 / 插件管理等独立窗口的红绿灯可用、自绘窗口按钮已隐藏、左侧让位宽度合适（`Themes/WindowChrome.axaml` 的 `traffic-light-spacer`，现为 60）、红绿灯与标题同一条中线、最大化 / 全屏后卡片铺满；X 服务端「自动」布局跟随系统输入法切换。②**Linux 原生 Wayland**（GNOME / KDE / Hyprland 任一）：弹窗外圈消失、阴影圆角描边与 Windows 一致、拖动与边缘缩放、背景模糊只在卡片范围内（取决于合成器，记下即可）、`Width` / `Height` 确实不含装饰（不对就改 `WindowChrome.SizeReduction` / `OuterFrameSize`）。③**Linux X11**：不透明矩形、没有外圈、自绘抓取区能缩放。④隔离插件的窗口（`PluginHostShellWindow`）同上各看一眼。⑤散在各节、同样只差实机的：窗口位置记忆在 macOS / Linux（§130 主窗口与 §241 的工具窗口）、回放中心等标题栏动作组排在红绿灯旁的样子（§136）、Linux 检查更新后真实桌面浏览器打开发布页 / 对应安装包的下载（§172 与 §241），以及 deb / rpm / AppImage 的安装方式在真机上认得对,本机加密密钥交给系统密钥链(§253:macOS 钥匙串与 Linux Secret Service 上的升级迁移,往返用例 `SystemKeychain_RoundTrip` 要 `VELASHELL_KEYCHAIN_TESTS=1`;Linux 钥匙环锁着而取消解锁框时可能被报成「找不到」,自动登录而钥匙环不自动解锁的机器上每次启动都可能弹一次解锁框,核过再定要不要加「拒绝后 N 天内不再试」)；非窗口类的：动态码对真实的 Duo / RSA SecurID（§133）、老算法与自定义清单对真实的 Cisco / 华为设备（§134）、远程 ⇄ 远程中转在两台真服务器之间（§128） |
| 🚧 | 🟡 P2 | **启动画面偶发首帧不出** | 启动画面接入后头两次启动 9 s / 12 s 内没等到首帧（`plan.md` §191），根因没坐实、没拿到栈；只做了降风险（STA 改 MTA）并加了自诊断日志 | 再遇到时先看日志时间线里的 `SplashShown`，拿栈再定；在那之前别把它当已修 |
| ⏳ | 🟢 P3 | **设计稿的两处残留** | Logo 有一个 `enabled:false` 的残留图标；文件列表「修改时间」列无固定宽度 | 小到可以顺手做掉 |

### 🧩 插件生态

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟡 P2 | **AI 插件:#651 之后 MCP 2.3 的 HTTP 自动探测与 SSE 回退** | #651 的截断收尾、工具调用之后的空答与外层提示已改(`plan.md` §264)。剩下 ④:#654 升级到 MCP 2.3 后 HTTP 自动探测与 SSE 回退有改动,读代码判断不了 | 连一次老式 SSE MCP 服务器实测 |
| ⏳ | 🟡 P2 | **AI 插件:Anthropic SDK 13 之后的思考配置(#657 复审跟进)** | SDK 13.x(#657 / #665 / #666)把 `anthropic-version` 换成 `2026-10-21`、API Key 改发 Bearer、去掉了采样参数;§233 已把 Key 改回 `x-api-key`,Anthropic 协议上不再发温度 / Top-P;模型清单那条请求已与聊天请求对齐(§264)。没动、也没用真 Key 验过的:插件经 `RawRepresentationFactory` 一律手拼 `thinking: {type: "enabled", budget_tokens}`,13.x 的适配器自己也会按 `ChatOptions.Reasoning` 生成思考配置(调用方给了就不覆盖);较新的模型可能不再接受 `budget_tokens`(复审时的说法,没核实),插件 README 里「12.40.0 的适配器不读 Reasoning」也已过时 | 维护者 2026-10-25 定:思考档位交给后续用真 Key 实测 —— 用官方 Key 与一两家中转站各测一轮思考档位,再定是交给适配器的 `Reasoning` 还是保留手拼 |
| ⏳ | 🟢 P3 | **AI 插件:模型覆盖地址换到别的源时,模型自带的 Key 照旧跟去** | 跨源覆盖必须自带 Key 已落地(`plan.md` §266);剩下的是模型这一层的换源:模型行把覆盖地址改到别的源,还没保存就点「测试」,带的是编辑框里预填的已存自带 Key,保存也照样留着它、之后的请求发往新地址(供应商换源会先问再清,模型这一层没有;早于 #646,复审 #646 时查出) | 待维护者定:模型覆盖地址换源时也像供应商换源一样先问、再清模型自带的 Key(「测试」相应只带新填的),还是维持现状(同一张表单里地址与 Key 并排,视为用户自己的决定) |
| ⏳ | 🟢 P3 | **AI 插件:故障转移或换 Key 时在聊天记录里留一条可见提示** | #582 复审的六条都已做完(①②③ `plan.md` §266,④⑤⑥ §263)。当时顺带记下的一条还没做:故障转移换了供应商、或换了一把 Key 之后,切换会粘住并落盘,用户不知道后续请求换了供应商与计费归属 | 切换发生时在聊天记录里留一条不进模型上下文的可见提示(换到谁、因为什么) |
| ⏳ | 🟢 P3 | **协作接入页的模型下拉认不出已删的指定模型** | 桥接指定的模型被删后,机器人回一句请重选、不再落到全局第一个模型(`plan.md` §264);但协作接入页的模型下拉对这种已删的指定模型显示成「跟随聊天面板」,此时点保存会静默改成跟随 | 下拉里如实显示「已删除的模型」并要求重选,保存时不悄悄改写 |
| ⏳ | 🟢 P3 | **宿主的条件取消(只在还等确认时才撤)** | 停止或拒绝之后撤掉本轮还在等确认的传输时,AI 插件先 `GetTransferAsync` 取宿主最新的快照、已过确认的只发布不撤(`plan.md` §263「合入后发现」,1a61038c);取快照与发出取消之间仍有一个往返的空当 —— 宿主恰好在这一拍里批准了,插件照样会把它撤掉 | 要 SDK 新契约(在 velashell-plugin-sdk 写好、不动版本号,发版由维护者定):`CancelTransferAsync` 带一个期望阶段(只在仍处于 `Approval` 时才撤,否则原样返回),或单独的「撤回确认」;宿主在同一把锁里判阶段并撤,插件改用它 |
| ⏳ | 🟡 P2 | **AI 插件:#616–#636 复审 ⑤(`RpcConnection` 写出半帧)** | ①②③④ 已做(②`plan.md` §263,①③④ §264)。⑤SDK 的 `RpcConnection.WriteAsync` 把调用方的取消令牌传进流写入,取消落在写帧中途时理论上会写出半帧 —— **SDK 已修(velashell-plugin-sdk 本地 dev,`RpcConnection` 一帧要么整帧写完、要么不开始,§260),待发版后宿主抬包即生效** | SDK 发版、抬包之后回归一次宿主的 `ExecutionRpcTests`;宿主与 PluginHost 不用改代码 |
| ⏳ | 🟡 P2 | **五套版本比较口径** | `PluginManager.IsOlder`(丢掉预发布后缀)、`UpdateVersion`(完整 SemVer 子集)、CLI 的 `VersionOrder`、市场的 `SemVerComparer`(后两者按序数比预发布),外加核对时查出的第五套 `AnnouncementFeedDocument.TryParseVersion` / `MatchesVersionRange`(用 `System.Version`,丢掉预发布后缀)—— `1.0.0-beta.10` 与 `1.0.0-beta.2` 谁新,各边能给出不同答案。**SDK 契约已写好(velashell-plugin-sdk 本地 dev),待发版后宿主接**:`VelaShell.PluginSdk.Versioning.SemanticVersion`(`CompareTo` / `CompareCoreTo` / `TextComparer`,`plan.md` §260) | SDK 发版、抬包之后:删掉宿主 `Services/Update/UpdateVersion.cs`,`GitHubReleaseSource`、`UpdateManifest`、`UpdateService`、`PluginManagerViewModel`、`ReleaseNotesDialog` 改用 `SemanticVersion`(`UpdateVersionTests` 的数据已搬进 SDK 用例,宿主那份可删);`PluginManager.IsOlder` 改成「两边都能解析且 `CompareCoreTo < 0`」、解析不出照旧放行;`AnnouncementFeedDocument` 一起换;CLI 的 `VersionOrder` 改用 `TextComparer`、`IsOlder` 改用 `CompareCoreTo`,商店的 `SemVerComparer` 改用 `TextComparer`(商店引用的 SDK 还是 2.0.0,一起抬)。已知差异:第四段非 0 时参与比较、只有一段的 `"2"` 不再能解析。清单正则 `^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?$` 能过、`SemanticVersion` 判不合法的写法(`1.0.0-beta.01`、`1.0.0-a..b`)要不要收紧清单校验(收紧会让现有这类清单被拒),待维护者定 |
| ⏳ | 🟡 P2 | **对外 MCP 的带外审批** | 对外 MCP 走 `AgentToolbox` 产出工具，那条路上没有审批界面，「询问」模式等于一律拒绝写操作 —— 这是刻意的（`plan.md` §33-四） | **暂缓**(维护者 2026-10-25 定):「询问」一律拒绝写操作是刻意的安全默认,带外审批只是可选增强,等有真实需求再排。要做时:审批推到 IM 渠道或桌面通知,用户放行后再执行 |
| ⏳ | 🟢 P3 | **SDK 测试替身注册句柄口径(待发版)** | `RecordingProtocols` / `RecordingWorkspaces` 已改为按注册条目判定、与宿主 `PluginProtocolRegistry` 同口径(velashell-plugin-sdk 本地 dev 4836e10,含 7 条用例,`plan.md` §261);同批查出的另两处已在各自仓库的本地 dev 上改好:六个第一方插件(含 MongoDB)的 `minSdkVersion` 统一 2.0.4、模板仓库 README / props / slnx 的过时说法 —— 都没推送,随各自的 PR 合并即完成 | 发一版 SDK(版本号维护者定)后,velashell-plugins 抬 `VelaShell.PluginSdk.Testing`,给 Telnet / 串口 / S3 补「换语言重注册后旧句柄 Dispose 不撤新注册」的用例 |
| ⏳ | 🟢 P3 | **生态仓库里几处过时的说法(eco-cleanup 核对时查出)** | 都不在本仓库(`plan.md` §261 的「核对时查出、没改的」):①模板仓库 README「想验一版还没发布的 `.Build`,就到 sdk 仓库 `dotnet pack -o <这里>/artifacts/nuget`」与 SDK 仓库 AGENTS 的「不要 pack 本地包给别的仓库用」相冲突;②模板仓库 `.gitignore` 的 `local-packages/` 说「见 nuget.config」,仓库里没有 `nuget.config`;③velashell-plugins `Directory.Packages.props` 的 ItemGroup Label 还写着源码在 velashell-plugin-cli;④velashell-plugins AGENTS.md「`-p:VelaSdkVersion=<版本>-dev` 配 nuget.config 本地源」与 CPM 的字面量版本、SDK 的版本纪律对不上;⑤SDK 仓库的 `VelaSdkVersion` / `VelaPluginApi.SdkVersion` 停在 2.0.9 而 tag 已到 2.1.2,velashell-docs `sdk-reference` 的横幅还写着 2.0.2(2.1.0 – 2.1.2 发版前都没在本地跑 Set-Version) | ①的做法待维护者定(删掉,或改成「用 CI 发的预览包」一类不造本地包的写法);②③④随下一个 PR 改掉;⑤下次发 SDK 时跑一次 `Set-Version.ps1 <版本> -DocsRoot …`,横幅与版本号一起补上(横幅由脚本维护,不手改) |
| 💡 | 🟢 P3 | **插件更新检查的响应缓存** | 市场没装 OutputCache，`/api/plugins/latest` 每次都真查库；目前只在打开插件管理页时问一次，量很小 | 装 OutputCache + `ETag`，按 ids 组合缓存几分钟。**等真有量了再做**，别为不存在的负载先加一层缓存 |

### 🧪 测试与本机环境

> 这几条在 `plan.md` 里被一节节重复记成「与本次无关」，攒在这里一次清掉。它们不影响产品，影响的是「红了到底是不是我改坏的」这个判断。

| 状态 | 优先级 | 项 | 现状 | 要做什么 |
| :---: | :---: | --- | --- | --- |
| ⏳ | 🟢 P3 | **渲染停顿需在 Windows 上重测(#648 复审)** | #648 的测量脚本原先没加主题,窗口里什么都不画,§219「0 次停顿、保温无收益」不能作数;脚本已修(§231),要在报障用户的 Windows 机器上重测,再定渲染停顿的优先级(WSL 互操作端口那一半已改,§233) | 重测 |
| ⏳ | 🟢 P3 | **runtime-async 下编译器不报 CS4007** | `plan.md` §251 已坐实:`runtime-async=on` 时 Roslyn 5.12(.NET 11 RC2)不报 ref struct 跨 await,await 真挂起之后 Span 回来是空的;仓里已清掉(只有测试工具 `TestChannelServer` 一处),约定记在 AGENTS.md | 向 dotnet/roslyn 提 issue(复现与版本信息在 §251;相关的 dotnet/runtime#136000 / #136171 只看过检索摘要,提之前逐条打开核对);上游修好、SDK 跟上之后把 AGENTS.md 那条的「编译器不报」改掉。要不要在 CI 加一步 `-p:Features=runtime-async=off` 的编译、专扫 CS4007(多一次全量编译),待维护者定 |
| ⏳ | 🟢 P3 | **映射盘用例会真的 `net use`** | AI 上传的 `WindowsMappedDriveSourceIsTheSameFile`(`plan.md` §257)真的把一个空闲盘符 `net use` 到 `\\localhost\<盘>$`(`/persistent:no`,`finally` 里删掉;映射不上时 Inconclusive),单元测试碰了网络盘映射 | 待维护者定:留着(它钉住的是实测过的误拒),还是删掉、只留 subst 与硬链接那几条 |

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
剩下的空档集中在**凭据与团队**(凭据管理器的外部保险库、团队共享配置);主机证书(`plan.md` §242)与文件侧的远端搜索、传输队列持久化(§239、§238)已在 2026-10-24 补上,文件侧只剩「远程 + 远程」的同步窗口这类边角。

### A. 终端体验

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| 💡 | 🟢 P3 | **录制与日志遮盖接输出规则表** | — | 输出规则表(自定义高亮与自动应答,`plan.md` §265)已有;录制与日志的遮盖(§220 / §258 的 `SessionOutputMasker`)仍只用内置规则。让用户在同一张表里加「遮盖」动作:遮盖跑在读线程的字节流上(按 Latin-1 逐字节、整行匹配、扣下半行),与渲染侧按字符的规则口径不同 —— 要么另编一份字节级的、要么把遮盖挪到解码之后;用户规则的线性时间引擎、表里不许有口令两条同样适用 |
| 💡 | 🟢 P3 | **终端内搜索:正则与区分大小写** | 各家都有 | 搜索条(`TerminalTabView` + `BufferSearch`)已有全部高亮、上一个 / 下一个循环与 `n/N` 计数;匹配固定是忽略大小写的字面串(`OrdinalIgnoreCase`)。正则与大小写开关按反馈再定,用户写的正则要防回溯卡死(带匹配超时,或用 `RegexOptions.NonBacktracking`) |
| 💡 | 🟢 P3 | **命令块:认 OSC 633 的 `E`、核 fish 4.x 的 OSC 133** | VS Code | 「复制这条命令 / 重跑」(`plan.md` §245)是从屏幕上 B 到 C 之间切出来的;VS Code 的 OSC 633 `E;<命令行>` 由 shell 显式上报命令文本,认了就不必从屏幕切、多行与续行提示符的麻烦一并消失,但要处理 nonce 防伪(远端输出也能打出同样的序列)。fish 4.x 自带 OSC 133,宿主没验过 |
| 💡 | 🟢 P3 | **Sixel / Kitty 图形协议** | MobaXterm / WezTerm | VT 引擎有 DCS 通道，缺的是图像解码 + 位图合成进自绘渲染。多日工作量，至今没有用户提过，**等真实需求** |

### B. 会话与工作区

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| 💡 | 🟢 P3 | **巡检记入审计 / 结果留档** | — | 会话健康巡检(`plan.md` §268)每次登录不写 `audit_log` 与连接历史(与「测试」一致),结果关窗即丢,「验证登录」开关也只在这次启动里记住。要不要把巡检的登录记进审计、结果要不要能存档对比上一次、「验证登录」要不要做成设置项(那样要新增设置并当场接线),待维护者定 |
| 💡 | 🟢 P3 | **运维编排中心** | — | 设计帧 `bR5c4` 已出，代码零实现，是设计稿里最后一个未落地的面板。有了 AI Agent 与协作接入之后，「编排」的形态可能和当初画的不一样，**先重新界定范围** |

### C. 文件与传输

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| 💡 | 🟢 P3 | **文件栏的图标视图与缩略图** | Windows 资源管理器 / Nautilus | 上传选择框已有「列表 \| 图标」两种视图（`plan.md` §202，`Views/VirtualizingTilePanel` 两种视图共用一个虚拟化面板）。顺着推：①双栏文档的本地栏、远端栏也给图标视图（§200 已把选择框的路径输入与键入定位推广到两栏，同一条路）；②本地图片的缩略图（只为视口里的格子后台解码、按尺寸封顶、可取消）；③按扩展名的字形（压缩包、图片、代码、密钥……，现在只印扩展名）。远端缩略图要先下载，**别做** |

### D. 连接与协议

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| 💡 | 🟢 P3 | **SecureCRT 风格的斜杠命令行** | SecureCRT | 外部拉起目前只认 Xshell 的调用约定（`plan.md` §84–85），`/SSH2 /L root /PASSWORD pw host` 被整条忽略。**先确认有没有真实调用方** —— 这层兼容的存在理由是「堡垒机客户端已经在发」，不是补齐对标表；没人发的写法接进来只是多一条攻击面。⚠️ `/` 开头的 token 与 Unix 路径、Avalonia 自己的参数会撞 |
| 💡 | 🟢 P3 | **更多协议插件** | — | RDP / VNC / Kubernetes exec / 数据库客户端。这正是 `Protocols` + `Workspaces` 能力面存在的意义，宿主一行不用改。**优先级交给插件市场的真实下载量决定** |

### E. 安全与合规

| 状态 | 优先级 | 项 | 对标 | 架构落点 |
| :---: | :---: | --- | --- | --- |
| 🚧 | 🟠 P1 | **插件发布者的信任根** | VS Code / JetBrains | 已有：验签 + 发布者连续性（`plan.md` §57，钉住首装的公钥，换钥 / 去签名要用户看过两个指纹再点头）；市场 `GET /api/plugins/latest` 给出每版的发布者指纹、宿主有只读客户端 `IPluginMarketClient`，**更新路径**已拿它比对（`plan.md` §71）。⚠️ 但这仍是 TOFU：`velashell-identity` 是 OIDC 账号服务，不是公钥注册表。真要闭合，缺一份**由发布者身份背书**的 id ↔ 公钥映射（市场把上传者 `sub` 与公钥绑定并可对外验证），且**装包路径也去查它**。外呼仍只在打开插件管理页时发生，别改成开机外呼（`PRIVACY.md`） |
| 🚧 | 🟡 P2 | **凭据管理器集成** | Termius | **设计已定稿**（velashell-docs [`zh/host/凭据管理器集成设计.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/凭据管理器集成设计.md)，v3）。**已落地第一个凭据来源：本机共享凭据**（`plan.md` §157，#550）—— `CredentialReference` / `ICredentialProvider` / `ICredentialResolver` 契约、仓储不变量、五个构建点接线、解析失败退回登录框，以及设计 §3 的 D1 / D3。**还差外部保险库**：1Password / Bitwarden / KeePassXC 三家 CLI（`CliCredentialRunner`、解锁口令内存 TTL 缓存、`ICredentialUnlockPrompt`），届时给解析补上交互 / 非交互之分（自动重连不许弹解锁框，设计 §5.7）与单次尝试缓存，`CredentialReference` 再加 `Scope` 等字段（JSON 省略 null，零迁移）。⚠️ `ISecretProtector` **不是**那层抽象（它管加密不管来源，见设计 §1）。外部保险库的私钥走 SSH Agent。系统密钥链 provider 可复用 `IMasterKeyStore` 那一层 P/Invoke(`plan.md` §253 已落地 macOS / Linux;Windows 上 `IMasterKeyStore` 没有实现,provider 要另接 `CredReadW` / `CredEnumerateW`,见 [`系统密钥链与sudo凭据填充可行性调研.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/系统密钥链与sudo凭据填充可行性调研.md))。设计 §3 的 D2（私钥口令不受「记住密码」控制）、D4（`SshSession.ConnectionInfo` 持有每一跳的密码）仍在；§16 另有 11 项待确认决策 |
| 💡 | 🟢 P3 | **共享凭据改完后批量试连** | — | #550 的场景是一百台同账号：改了共享凭据（`plan.md` §157）之后对引用它的连接逐台试一次认证、列出被拒的那几台，不必等逐台连接时登录框才暴露哪一台密码不同。⚠️ 这是一次批量外呼，由用户显式点开 |
| 💡 | 🟡 P2 | **团队共享配置（只读策略分发）** | Termius / Xshell 企业版 | 「运维组长发一份机器清单，组员只读订阅」。Gist 云同步的载荷格式与版本回溯现成，差的是**方向**：现在是「我的多设备漫游」，团队要的是「一处发布、多处只读」。⚠️ 这条会把产品推向企业形态，**先想清楚商业授权边界再动手** |
| 💡 | 🟢 P3 | **FIDO 安全密钥直连硬件（第二阶段）** | OpenSSH `ssh-sk-helper` | 第一阶段（经 agent 用 `sk-ssh-ed25519` / `sk-ecdsa-sha2-nistp256`）已落地（`plan.md` §161）。第二阶段直接驱动硬件：Windows 走 `webauthn.dll`，其余平台要 libfido2 —— 新依赖要过 `src/VelaShell.Ssh/AGENTS.md` 3.3 的许可审查；还要有一把真钥做验证 |
| 💡 | 🟢 P3 | **PKCS#11 与 macOS Secure Enclave 签名器** | OpenSSH `PKCS11Provider` | Windows 的 CNG 签名器已落地并接进连接配置的「系统密钥库」认证（`plan.md` §161、§259），同一个 `ISshSigner` 形状;系统密钥库的钥目前不能配证书认证(库的 `SshCertificateSigner.Create` 收任意 `ISshSigner`,技术上可行,没做)。PKCS#11 要按平台加载厂商的 `.so` / `.dll`（P/Invoke，零反射照守）；Secure Enclave 要 macOS 的 Security 框架。都要真硬件验证 |
| ⏳ | 🟢 P3 | **gssapi-with-mic（Kerberos）认证** | OpenSSH / PuTTY / SecureCRT | SSH 库还没实现（`plan.md` §113 评估）。协议面不大：RFC 4462 §3 的几种报文（60 / 61 / 63 / 64 / 65 / 66），MIC 覆盖 `session_id` 与认证请求头。GSS-API 本身走 BCL 的 `NegotiateAuthentication`（Windows 上是 SSPI，Linux / macOS 上是系统 GSSAPI 库），动手前先确认：①选 Kerberos 包时产出的是不是裸 krb5 机制令牌（不是 SPNEGO 包装），`host@主机名` 在两个平台上怎么写；②有没有可用的 MIC 接口；③凭据委派能否经 `TokenImpersonationLevel.Delegation` 拿到。**卡点在验证**：要 KDC + 配了 keytab 的 sshd + 拿得到票据的客户端，建议用 Docker 起 MIT krb5 KDC + sshd，Windows 域环境另找一台。`gssapi-keyex` 不在范围内 |

### F. 性能与稳定

| 状态 | 优先级 | 项 | 架构落点 |
| :---: | :---: | --- | --- |
| ⏳ | 🟡 P2 | **启动时间与内存的回归门禁** | `VelaShell.Benchmarks` 刻意不进 CI（BDN 的抖动当门禁只会天天误报）。折中：不比毫秒数，钉**结构性指标** —— 启动路径上的分配次数、冷启动跨过的模块数；`StartupTrace` / `StartupWarmup` 已在采点。同一类还有两处：内置 X Server 的帧率（`scripts/xserver/perf` 测量台，§201 的自适应窗口问题让经 SSH 只有 9 帧却一直没人发现）可以定期跑出基线；CI 按测试工程记耗时、明显变慢时报出来（§179 的 AI 插件一个工程 22 分钟，等整体拖到 25 分钟以上才被发现） |
| ⏳ | 🟡 P2 | **长时会话的内存与句柄基线** | 隧道有计量、终端积压有高水位、scrollback 有上限 —— 三条边界各自都在，**但没有「挂 24 小时会怎样」的端到端证据**。做一个长跑冒烟（放 nightly，不进 PR 门禁） |
| 💡 | 🟢 P3 | **无障碍（屏幕阅读器）** | 全仓只有二十来处 `AutomationProperties`。对「键盘优先」的产品落差比看上去大，但补齐是**跨全部视图**的工作量，先评估真实诉求。已知一处只能用鼠标的：快捷命令的拖动排序没有键盘替代（`plan.md` §158） |

### G. 生态与分发

| 状态 | 优先级 | 项 | 架构落点 |
| :---: | :---: | --- | --- |
| ⏳ | 🟡 P2 | **能力域补口：`localFs` / `net`** | `STATUS.md` 列为未开口。有了它插件才能做「本地文件 ↔ 远端」（如 DockerPanel 把容器里的文件拖到桌面）。⚠️ 开口就是**权限面变大**，必须同步进 `PluginPermissionGate` 的逐项授权，apiLevel 只增不改 |
| ⏳ | 🟡 P2 | **插件往消息中心发通知** | **SDK 契约已写好(velashell-plugin-sdk 本地 dev),待发版后宿主接**:`IUiApi.SupportsNotifications` / `PostNotificationAsync`、`PluginNotification`(去重 id、纯文本标题正文、级别、只许本插件的动作命令,没有网址字段)、RPC `ui/notify` 与握手的 `SupportsNotifications`;每插件每分钟至多 6 条、先去重后限频、可按插件静音,不新增权限项(`plan.md` §260)。宿主侧的接口形状已按这个用途定好(§20)。⚠️ 消息中心只收「要留存、可回看」的东西,插件通知同样守这条边界。SDK 发版、抬包之后:进程内 `Services/Plugins/PluginUiApi` 实现(先 `Validate(pluginId)`,去重键 `plugin:<pluginId>:<Id>`,每插件一个滑动窗口限频,查静音开关,映射成 `NotificationItem` —— 级别对应、动作走 CommandId、文案截断 —— 再 `INotificationCenter.PublishAsync`);宿主缺的几样要先补:消息的来源字段(列表里显示插件名)与对应的 `NotificationKind`、卸载插件时按前缀删消息、`PublishAsync` 回报「这次是不是新的」、插件管理页的「静音此插件通知」开关(五份 resx);隔离模式由 `PluginCapabilityRouter` 处理 `ui/notify`、握手置 `SupportsNotifications = true`,`PluginHost/PluginHostUi` 按握手值决定是否支持、先本地校验再 `RequestAsync`;`NullUiApi` 保持不支持。用户看得见的行为变了,同一提交改 `manual.md` / `manual.en.md` |
| 💡 | 🟡 P2 | **插件互调 / 插件间事件** | AI 插件能调 DockerPanel 的能力，「让 agent 帮我看看那个容器为什么起不来」就通了。**先想清楚权限模型再设计接口** —— 插件 A 借 B 的手绕过自己没拿到的授权，是这类设计最容易出的洞 |
| 💡 | 🟢 P3 | **启动画面支持 macOS / Linux** | 启动画面只在 Windows 10 1607+ 上有（`Splash/SplashScreen.cs`，§191）；「Avalonia 窗口要等平台初始化与 XAML 加载完才出现」与平台无关。先等上面「启动画面偶发首帧不出」坐实，再往别的平台推 |

### H. 内置 X 服务端

> 第二次全库审查(`plan.md` §167,草案 `xs_plan.md`)修完 184 条问题、2026-10-09 补齐只做了一部分的几项(§178)、2026-10-10 对着清单逐条核对并补完最后几项(§185)、同日做完草案第十二节的大部分新功能(§187),以及本机输入法的 XIM 桥与 X 程序拖出(§189)之后剩下的。新功能的编号沿用草案第十二节(F1–F30);草案已经删掉(登记的问题都修完了),各项的使用场景、工作量与风险在 git 历史里:`git log -- xs_plan.md` 找到删除它的提交,`git show <提交>^:xs_plan.md`。

| 状态 | 优先级 | 项 | 架构落点 |
| :---: | :---: | --- | --- |
| 💡 | 🟢 P3 | **整数倍放大**(F19 的其余) | 各显示器的 DPI 已经按各自的缩放报给 RANDR。整数放大要全服务端统一的放大倍数 Z(屏幕像素 = Z × 根坐标 + 原点),宿主所有根坐标与屏幕坐标的换算都要改 —— 要在实机多显示器上验,不能只靠无头用例;也可以等「每个 SSH 会话一个显示」(F1)给每个显示各自的倍数。**维护者 2026-10-10 决定先等**,不做全服务端统一倍数的方案 |
| 💡 | 🟢 P3 | **压感、触摸与手势**(F7) | 压感要主设备的类随 SlaveSwitch 切换,镜像里没有能核对的真实工具包;触摸与手势风险大,XI 2.4 也不在规范清单里 |
| 💡 | 🟢 P3 | **间接 GL 剩下的部分**(F23 / F24 的其余) | 反馈模式、mipmap LOD、PixelMap、深度 / 模板格式的 DrawPixels / CopyPixels;双线性取样与 GL 光栅化的 SIMD。选择模式、点画、求值器与 RENDER 的 SIMD 快路径 2026-10-10 做了 |
| 💡 | 🟢 P3 | **WSL 与 MIT-SHM 1.2**(F29 / F30) | WSL 的程序自动用上内置 X Server(要一个装着 WSL 的环境来测);MIT-SHM 1.2 的 fd 传递只有 Linux 的 Unix 套接字上才有,现有的 TCP 容器环境测不了 |
| 💡 | 🟢 P3 | **新功能各平台补齐与小尾巴** | 指针 Warp / confine 在 macOS 上没做、Wayland 不许(F8);屏保协作只在 Windows 上(macOS 的 IOPMAssertion、Linux 的 org.freedesktop.ScreenSaver 要 D-Bus 客户端,F9);合成管理器默认关,带 alpha 的窗口在 Windows 上的合成开销要实机量过再说(F11;新设置的默认值 —— 输入法开、来源标识开、合成管理器关、Linux / macOS 的 TCP 端口关、每个 SSH 会话一个显示关 —— 维护者 2026-10-10 确认保持);截图只在 Windows 的系统菜单里有入口、录屏没做(F20);Present 只有一个全局帧时钟(F25);托盘的气泡消息收下不显示(F12);单窗口模式下直接画在根窗口上的内容、远端合成器画在叠加窗口上的效果不显示(F13);SECURITY 的 XC-QUERY-SECURITY-1 与 Application Group(只为早已没人用的 X 防火墙代理);「每个 SSH 会话一个显示」要下次启动才生效(F1);macOS 的 LOCAL_PEERPID 没有实机核对(F28) |
| 💡 | 🟢 P3 | **核对新设置时看到、这一轮没做的**(`plan.md` §188) | **透明窗口(F11,只在打开「X 窗口透明与圆角」时)**:深度 32 的顶层里嵌着深度 24 的子窗口(`--wid` 嵌的 mpv、GtkSocket)时,子窗口写进 32 位缓冲的像素 alpha 是 0,宿主按预乘 ARGB 显示成叠加 —— 要在每条写入路径(光栅化、PutImage / CopyArea、RENDER、背景与边框、GLX、Present、SHM)给深度 24 的窗口补上不透明的 alpha;Windows 上无装饰的透明区域照样接住鼠标(要按 alpha 或输入形状回 `WM_NCHITTEST`),宿主也还没用快照里的 `InputShape` 与 `ClientFrameExtents`。**防冒充**:无装饰与 override-redirect 窗口的不透明度下限只有 0.05;自己画标题栏的窗口没有系统标题栏,来源只在任务栏上看得到(可考虑宿主画的来源条或任务栏角标);新窗口映射时激活的规则要再核一遍(审查说映射即激活,与排障文档写的「不能自己跳到前台」对不上,没核实);不同主机上的同名程序在任务栏归成一组,映射之后改了类名不重算;`_NET_WM_VISIBLE_NAME` 没写。**本机输入法剩余核对(F5)**:按着 Ctrl / Alt 时上屏的字照样带着修饰位(程序可能当成快捷键);只认核心协议的老客户端在 CapsLock 下仍转大写;宿主「这个键自己打出的字不重复输入」的判断没在 macOS / Linux(IBus、fcitx)的真实输入法上核对(那两个后端组字时是不是一律报 ImeProcessed)。**TCP 端口(F4)**:桥接网络里的容器要的「监听地址」选项没做;macOS 上 `DISPLAY` 指向 XQuartz 的 launchd 套接字而 XQuartz 没在跑时,自动选号可能占了 :0(没法在净室规程下核对 XQuartz 的行为) |
| ⏳ | 🟢 P3 | **审查留下的部分完成项** | 点本机窗口或桌面就收起 X 的弹出菜单(WN-M7):要全局指针钩子,各平台各写一份。(§185 / §178 记的宽弧几何与低位深 RENDER 已做完或早已修过,见 `plan.md` §243) |
| 💡 | 🟢 P3 | **互操作靶场扩充**(审查的测试缺口 T16) | 真实客户端现在只覆盖 x11-apps、xterm、xdpyinfo、glxgears 与 Swing。现有镜像保持轻量、留在 CI,另建一个 heavy 镜像手动或每晚跑,断言沿用「零 `: Bad` 日志 + 画出了东西」再加专项:GTK3(`gtk-3-examples`、`zenity`、`meld`:拖放、剪贴板、弹出层、自绘标题栏的边缘缩放)、GTK4(`gtk-4-examples`、`gnome-calculator`:`GSK_RENDERER=gl` 与 `cairo` 都零错误)、GTK2(`gimp`)、Qt(`wireshark`)、浏览器(`firefox-esr`、`chromium --disable-gpu`:剪贴板提供哪些格式)、Motif / Xaw / Tk / Emacs(`ddd`、`xfig`、`gnuplot-x11`、`gitk`、`emacs-lucid`:核心字体告警、中文与西里尔文)、桌面会话(`xserver-xephyr`、`openbox`、`xfwm4`、`xfdesktop4`:窗口管理器要以「已有窗口管理器」退出,桌面类窗口不开原生窗口)、托盘(`stalonetray`、`yad --notification`)、中日韩输入(`fcitx5`)、办公(`libreoffice-calc`:富格式剪贴板、启动时恢复最大化) |
| 💡 | 🟡 P2 | **整个应用的渲染停顿**(`plan.md` §201 的四、§219) | X 窗口里量到过:Windows 上 Avalonia 的渲染循环连着出帧之后转空拍,等系统合成器确认上一次提交偶尔要 400–490 毫秒,X 窗口已经靠「保温」避开。**主窗口一侧没复现出来**(§219,2026-10-19 开发机):用 `scripts/perf/render-keepwarm.cs` 量「改字 → 这一批被渲染线程处理完」,30 Hz 刷新 8724 帧、打字节奏 600 键 × 3 帧,最长 14 毫秒、0 次停顿;候选修法(应用级保温,脚本里的 `KeepWarm`)开着也是 0 次,只让 p99 多了不到 1 毫秒,所以**没挂进主程序**。⚠️ 别用 `RequestAnimationFrame` 的回调延迟当指标:窗口闲着时 Avalonia 在 UI 线程上当场补发回调(恒为 0 毫秒),停住的是渲染线程。下一步:有用户报「打字卡一下」时,在那台机器上跑脚本对照保温开关,量出收益再决定挂不挂;另两个选项(换渲染组合 —— X 窗口 3 分钟实测 AngleEgl + WinUI 合成停 4 次、AngleEgl + RedirectionSurface 1 次、Wgl + RedirectionSurface 0 次;报给 Avalonia 上游)同样先等复现 |
| 💡 | 🟢 P3 | **RANDR 的刷新率在 macOS / Linux 上**(`plan.md` §201 的五) | Windows 已按显示器句柄读当前显示模式;macOS(`CGDisplayModeGetRefreshRate`)、Linux(桌面 X 显示的 RANDR / Wayland 输出的 mode)仍报 60,高刷屏上远端程序只画 60 帧 |
| 💡 | 🟢 P3 | **需要实机核对的** | 分数缩放下最后一列像素可能被裁(API-H13);macOS 上 Command 组合键收不到 KeyUp 时的处理(IN-E19);macOS / FreeBSD 经 `getpeereid` 取对端 uid(CN-S8) |

---

## 三、文档待同步（velashell-docs）

> 按 [`AGENTS.md`](AGENTS.md) 第二条：**改了行为就同步改文档，两个 PR 互相引用、一起合**；`zh/` 与 `en/` 两棵树一起改。
> 同步完成的行直接删掉，不留 ✅。

| 出处 | 要改什么 | 进度 |
| --- | --- | --- |
| `plan.md` §118 窗口外框 | 平台外框的文档已随 velashell-docs#70 合入；剩下的是**实机验收之后**把 `{zh,en}/host/architecture.md` 里「验收」那一段改成实测结论 | 等「跨平台实机验收」（一、窗口与外观） |
| STATUS 与发版流程的中英 / 跨仓漂移(2026-10-25 核对 velashell-docs 时查出) | `plan.md` §261 补了 en STATUS 缺的「2026-08:协议能力域落地」一节、改准了 `sdk/release-process` §3 的版本号落点(velashell-docs 本地 dev,未推送);核对时查出的其余漂移:①`en/plugins/STATUS.md` 分项表缺 zh 的 timeSeries / protocols / workspaces 三行,「激活事件」「发布形态」两行与质量基线、更新日期仍是旧说法;反过来 zh 缺 en 的「2026-08: SDK productization」一节(zh 页首却说「均见文末各节」);两边页首「代码仓库对照」仍说 `.Build` 在 velashell-plugin-cli;②`zh|en/sdk/release-process.md` 开头「一次发布产出两个包」(实际三个)、§4 的 Avalonia 链路与第 3–4 步仍按 `.Build` 在 cli 仓库写,`zh|en/cli/release-process.md` 仍把 `src/VelaShell.PluginSdk.Build` 列成 cli 仓库的落点;③`zh|en/sdk/sdk-reference.md` 的版本表把「打开已保存的会话」记在 2.0.2,而 2.0.1 的包里已经有 `ListSavedAsync` / `OpenAsync` / `CloseAsync`(横幅的版本号由 `Set-Version.ps1` 维护,见一、插件生态「生态仓库里几处过时的说法」⑤) | 未开始 |
| `plan.md` §260 SDK 新契约(velashell-plugin-sdk 本地 dev,未发版) | 这次没改 docs,随发版一起改:`sdk/sdk-reference`(§3.3 RemoteFs 的链接字段、「传输启动前被拒」、§3.4 Ui 的 `PostNotificationAsync` 与消息中心通知一小节 —— 边界、去重、限频、动作命令规则、权限;协议的两个可选接口与 `RemoteChecksumAlgorithm`;新增「版本比较 `SemanticVersion`」一节;§5 版本历史加一行;§6 替身清单补 `FakeUi` 的通知成员;横幅由脚本维护,不手改)、`plugins/05-ipc-protocol`(`ui/notify` 与握手的 `SupportsNotifications`、错误码 `transfer-rejected:<原因>`、§7「取消」改成帧原子的口径 —— 排队时取消就不发、开写后取消只是不再等待,§7.1 补「写坏半帧同样作废连接」)、`plugins/07-capability-apis`(§7 vela.ui 写清实际 API 与 ≤ 6 条 / 分钟;§2 remoteFs 的链接字段与启动前被拒;protocols 那张表补可选接口)、`plugins/06-permission-system`(应用内通知随 `ui.contributions`、可按插件静音)、`plugins/STATUS` 与 `templates/dev-guide` 的协议与 UI 章节(zh / en) | 等 SDK 发版 |

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
| **SSH 的 CBC 加密模式（`aes*-cbc`）** | 维护者决策（2026-10-06，`ssh_plan.md` Q2）：不实现，「允许老算法」里也不加。CBC 配 Encrypt-and-MAC 有长度预言 / 明文恢复攻击（Albrecht–Paterson–Watson 2009），要做得严谨，长度与 MAC 出错的路径得做到不可区分 —— 那是一条要单独评审、又几乎没人用的错误路径；OpenSSH 6.7（2014）起默认就不开 CBC。代价是只剩 CBC 的老设备（老交换机、嵌入式）连不上：经能谈 CTR / GCM 的跳板去连，或者升级设备固件（`plan.md` §162） |
| **SSH 裸 `zlib` 压缩** | 只保留 `zlib@openssh.com`（认证后才压缩）。裸 `zlib` 让口令与签名也进压缩流，未认证的连接方能做 CRIME 类旁路（`plan.md` §107） |
| **键盘复制模式（vi-like）** | 产品决策（2026-09-09）：没见过这种用法，代价却不小 —— 要新起一个**模式态**穿过 `TerminalKeyRouter` 与抢在它前面的 `TerminalTabView.OnPreviewKeyDown` 两层输入路径；进模式必须关 IME、`Ctrl+C` 的三重身份要重定义、还要模式指示器。而主场景已被吃掉：选整条命令输出走 OSC 133 命令块，找文本走 `Ctrl+F`，抢鼠标的程序里走 `Shift+拖拽`。（当初另一条理由「与不做自定义键位冲突，Dvorak / Colemak 上 `hjkl` 的位置是错的」已随 `plan.md` §155 失效；上面几条仍成立） |
| **SFTP 面板内拖拽移动文件** | 维护者决策（#474 回复）：**做过，因为太容易误触发而关掉了** —— 一次不经意的拖动就把文件挪走，用户事后不知道东西去了哪。现有 `DragDrop` 只认本地路径落入与跨面板传输，`DragEffects` 只给 `Copy`。再提之前先想清楚怎么防误触发 |
| **连字（Ligatures）** | 自绘渲染器按**单元格**排版，无法跨字符连字。这是自绘换来渲染控制权的固有代价 |
| **自适应标题栏颜色** | 主窗与全部对话框的标题栏都是自绘的（macOS 上只借用系统红绿灯，`plan.md` §118），这条已失去对象 |
| **系统通知 Toast** | 需要 AppUserModelID 与通知框架。替代：常规页「声音提示」、安全审计页告警通道的「提示音」（`Security.AlertSound`）与消息中心（`plan.md` §20） |
| **输入脱敏（会话录制）** | 只录**输出流**，密码本就无回显，没有要脱敏的对象（输出里的口令与令牌另有遮盖，见 `plan.md` §220） |
| **运行时热切终端类型** | TERM 在**连接时**向远端协商，活动会话热切只会让本地仿真与远端 TERM 能力档不一致。「设置修改对新连接生效」即为正确语义 |
| **按会话的独立代理** | 已落地为**应用级全局代理**（`plan.md` §12-10、§79）。别与动态 SOCKS 转发（`-D`）混淆 —— 那是**入站**隧道；四种**出站**模式（none / system / http / socks5）只决定第一跳 TCP 怎么走 |
| **终端渲染的脏行检测 / 子可视化分带** | 2026-09-19 实测否决：纯字母满屏一帧只有 **54 个绘制操作**，逐格工作已被五套跨帧缓存压掉，不存在可被脏行检测救回的「每帧全量重算」。真要省绘制得走子可视化分带，要穿过滚动、选区、搜索高亮、光标层、侧栏、折叠六个子系统，且滚动本身就让全部带失效。见 `plan.md` §81 |
| **终端解码 / 填充的手写 SIMD** | 2026-09-19 逐条实测否决：手写 `Vector128` 填充与手写循环**完全持平**（45.9 ns）；手写加宽 ASCII 只快 24% 且处理不了非 ASCII；泛型 `LastIndexOfAnyExcept` 在 16 字节结构体上反而**慢约 4 倍**。唯一成立的是「换成 BCL 已向量化的重载」（`EchoSuppressor.IndexOf` 16.9x、`Fill` −26%）。**先量再说**，见 `plan.md` §81 |
| **触发器按「任意一行输出命中」就自动发送** | 2026-10-25 的输出规则(`plan.md` §265)刻意只答主屏光标所在、正在等输入的那一行:`cat` / `grep` 出来的、恰好含 "Continue? [y/N]" 的行光标早已离开,按任意一行触发会往 shell 里敲一个 `y` 回车。代价是「某条日志一出现就自动发命令」这种用法做不到 —— 那是 expect 脚本的活,不在输出规则里做 |

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
