# AGENTS.md —— VelaShell.Ssh 开发约定

> 给 AI 代理与新加入者的操作约定。**改 `src/VelaShell.Ssh/` 之前先读完本文件**，
> 以及仓库根目录的 [`AGENTS.md`](../../AGENTS.md)（全仓通用的约定在那边，这里只写本库特有的）。
> 本库有一条宿主其余部分没有的硬纪律（第二节的净室规程），违反它会让这个库的立身之本失效。

---

## 一、这是什么

`VelaShell.Ssh` —— 一套**独立实现**的 .NET SSH 客户端库，宿主用它替换了 `Tmds.Ssh`。
原本是独立仓库 `VelaShellLabs/velashell-ssh`，2026-09-23 并入宿主仓库，不再单独发 NuGet。

- **本目录按 MIT 授权**（[`LICENSE`](LICENSE) / [`NOTICE.md`](NOTICE.md)），与宿主其余部分的授权不同。
  往这里挪代码之前想清楚：宿主那边的代码搬进来就等于以 MIT 再授权一次。
- **架构与原理**：velashell-docs 仓库
  [`zh/ssh/design/architecture.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/ssh/design/architecture.md)
  —— 先读这个，读完再读别的。
- **行为规格**：velashell-docs 仓库
  [`zh/ssh/spec/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/ssh/spec) —— **实现的唯一依据**。
- **独立性声明**：[`NOTICE.md`](NOTICE.md)

代码注释里写的 `velashell-docs/zh/ssh/…` 都指 velashell-docs 仓库里的这两处。

| 位置 | 内容 |
| --- | --- |
| `src/VelaShell.Ssh/` | 库本体 |
| `tests/VelaShell.Ssh.Tests/` | 单元测试（全部走内存传输）与 `[TestCategory("Interop")]` 互操作用例 |
| `scripts/ssh/` | 互操作靶机脚本、压缩严格校验检查、性能基准、公开面清单生成 |

---

## 二、净室规程（硬纪律，不可商量）

本库的立身之本是「**可证明的独立实现**」。这不是法律问题（Tmds.Ssh 是 MIT，fork 也合法），
是**声誉、合规摩擦与升级绝缘**三件事。详细论证见 architecture.md §2。

| # | 纪律 | 具体要求 |
| :-: | --- | --- |
| 1 | **规范优先，源码不作依据** | 实现依据只能是 RFC / OpenSSH `PROTOCOL*` / IETF draft。**每个协议实现文件头必须写明它实现的是哪份文档的哪一节** —— 写不出来，说明当时抄的是别人的代码 |
| 2 | **两阶段隔离** | 写实现时**不要打开任何其它 SSH 实现的源码**。先有 `velashell-docs/zh/ssh/spec/` 下的行为规格（纯自然语言 + 报文字段表 + 时序图，零代码片段），再照规格写。用 AI 辅助时：分析会话与实现会话**不共享上下文** |
| 3 | **标识符体系整体另起** | 不复用其它库的类名 / 方法名 / 字段名 / 枚举成员名的**组合**。对照表见 architecture.md §6.1 |
| 4 | **架构真的不同** | Pipelines 替手写缓冲、状态机 + 发送闸门替信号量握手、统一 `RequestLedger` 替多套 pending、`IDuplexPipe` 替双 buffer 读。**这一条是前三条的地基** |
| 5 | **测试向量只取公开来源** | RFC 向量、NIST CAVP、OpenSSH regress 的**思路**。不复制任何他人的测试文件 |
| 6 | **NOTICE 如实写** | 致谢是诚实，不是风险。见 `NOTICE.md` |

> **例外（不必紧张的部分）**：协议常量必然相同，且不受版权保护 ——
> 消息号（`SSH_MSG_KEXINIT = 20`）、算法名字符串（`"curve25519-sha256"`）、
> wire 布局、交换哈希的输入顺序、SFTP 包类型与 `SSH_FX_*` 错误码、RFC 写死的上限。
> **不要为了「看起来不一样」去改这些** —— 改了就是协议不兼容。

---

## 三、技术栈与工程约定

### 3.1 编译设置

TFM、`LangVersion`、`Nullable` 由仓库根 `Directory.Build.props` 统一给。本库在
[`VelaShell.Ssh.csproj`](VelaShell.Ssh.csproj) 里额外收紧了几条，**只作用于本工程**：

| 项 | 值 | 说明 |
| --- | --- | --- |
| `TreatWarningsAsErrors` | `true` | 含 `CS1591`（公开成员必须有 XML 文档） |
| `AnalysisLevel` / `EnforceCodeStyleInBuild` | `latest-recommended` / `true` | 编译器能指出来的问题恰好是协议代码最容易错的地方 |
| `IsAotCompatible` | `true` | **零反射**。加任何反射之前先想清楚 |

强名签名随宿主走（Release 签名、Debug 不签），所以 `InternalsVisibleTo` 只在非签名构建里给
—— 测试与互操作用例一律用 Debug 跑。

### 3.2 脚本：**永远不用 Python**

与仓库根 AGENTS.md 同一条。`scripts/ssh/` 下的工具都是 PowerShell 或 C# 单文件脚本
（`dotnet run xxx.cs`）。

### 3.3 依赖纪律

- 新增依赖前先确认许可与 MIT 兼容。**不接受 GPL / LGPL / AGPL。**
- 新增依赖要同步更新 `NOTICE.md` 的第三方组件表与 `src/Directory.Packages.props`
  （中央包管理与宿主共用；`BouncyCastle.Cryptography` 那一条宿主的 `SshKeyService` 也在用）。
- **不自己写密码学原语**（architecture.md 原则 6）。**一个例外，写明在案。**
  BCL 有的走 BCL（AES / SHA-2 / ECDH / ECDSA / RSA / ML-KEM —— 还能吃到硬件加速）；
  BCL 缺的走 BouncyCastle（raw ChaCha20、独立 Poly1305、Ed25519、X25519、DH 标准群、
  ML-KEM-768、sntrup761、Argon2id）。
  理由：手写的原语对一个要被审计的库是负债，而且它的错误不会报错，
  只会在特定输入上静默产出错误结果。**除下面这一处以外，我们只做协议装配。**

  ⚠️ **唯一的例外：`src/VelaShell.Ssh/Keys/BcryptPbkdf.cs`**（2026-09-22，用户拍板）。
  加密的 OpenSSH 私钥的 KDF 是 `bcrypt_pbkdf`，它要 Blowfish 的**密钥编排内部**；
  `BCrypt.DeriveRawKey` 是 internal，公开的 `Generate` 做的是 2^cost 轮的标准 bcrypt，
  `BlowfishEngine` 只给 `Init` + `ProcessBlock` —— **没有任何现成原语能凑出来**。
  而不做它等于「`ssh-keygen` 带口令的默认产物读不了」，也就是绝大多数人手里那把钥。

  **这个例外的边界是硬的，改动前先读 `architecture.md` §11.2.18：**
  - 只有那一个文件，只做那一个 KDF，不导出任何分组加密能力；
  - 初始表（1042 个字）由 π 的十六进制位**现算**，不许改成手抄的常量表；
  - 验证只认真 `ssh-keygen` 的产物做端到端比对，不许自己写加密侧来自解自。

  **不要拿它当先例。** 再想加第二处，先回到「BCL 有吗 / BC 有吗 / 不做的代价是什么」。

### 3.4 代码约定

- 版本号不写在 `.csproj` 里，走 `src/Directory.Packages.props`。
- **公开面门禁是关闭的。** 它保护的是「已发布的稳定 API」；库并入宿主之后不再单独发包，
  唯一的调用方就在同一个仓库里，改公开面与改调用方是同一个 PR。
  `scripts/ssh/Update-PublicApi.ps1` 留着，哪天库又要对外发布时再打开。
- 热路径用 `ValueTask` + 池化的 `IValueTaskSource`，不用 `Task`。
- 注释写**为什么**，不写**是什么**。协议实现的文件头写清规范出处（纪律 1）。
- API 设计、命名与文件组织的规则见第四节。

---

## 四、API 设计与代码组织

> 2026-09-25 做过一次全库 API 审查。在那之前本库**没有成文的 API 规范**，同一件事于是长出了好几种写法：
> 191 个公开类型里约 125 个宿主从未引用；同一个功能有两三个公开入口；5 个类型名与宿主撞名；
> 异常把 `Reason` 写死成 `Unsupported`，宿主只好把中文的 `Message` 直接显示给用户。
> 审查查出来的问题同一轮就改掉了（来龙去脉见仓库根 `plan.md`），下面是从中归纳出来的规则。
>
> - **新代码必须遵守。** 改到存量代码时，被改的那个类型顺手按这里收拾；**成批的改名 / 降级 / 挪文件单独开 PR**，
>   不与行为改动混在一起 —— 混在一起就没法 review。
> - 条目里的「曾经」是审查时的真实反例，**已经改掉**，留着是为了说明规则为什么存在。**还没改完的列在 4.8。**
> - 与净室规程冲突时**净室规程优先**：这里的命名规则只追求本库内部一致，
>   不许为了「更顺手」去靠近任何其它 SSH 库的命名（纪律 3）。
> - 公开面以**代码和本节**为准。velashell-docs 的 `getting-started.md`、architecture.md §6（公共 API 形态）
>   与 §8（扩展点）跟着代码走：改了公开面就按 4.7 同步中英两边。

### 4.1 公开面：默认 `internal`

本库只有一个调用方（宿主，同一个仓库），测试有 `InternalsVisibleTo`。所以**公开需要理由，不公开不需要**。

**公开的标准只有三条**：`getting-started.md` 写到的功能入口、宿主**现在**在用的、只读的可观测信息
（architecture.md 原则 4：协商结果、主机密钥、会话 ID、度量）。三条都不沾的一律 `internal`。

| 规则 | 说明 |
| --- | --- |
| **新类型先写 `internal`** | 「测试要用」不是公开的理由（有友元）；「将来也许有人要用」也不是（不发包、没有兼容负担，真要用时再开） |
| **协议管道永不公开** | 帧层、密码套件、KEX、交换哈希、密钥派生、wire 编解码、`KEXINIT`、SFTP 请求管线、版本交换、认证器，以及各拨号器的具体类型（公开入口是 `DialerChain`）。它们的正确性依赖调用时序，公开出去等于把时序约束交给调用方。曾经：`SshPacketTransport`（连 `MaxPacketLength` 都能改）、`SshKeyExchangeRunner`、`SshAuthenticator`、`SftpWire` 全是 public |
| **工厂交出的对象，构造与启动是 `internal`** | `SshConnection`（`SshConnection.ConnectAsync`）、`SshChannel`、`SshCommand`、`SshShell`、`SftpFileSystem`、`SshChannelStream`（`SshChannel.AsStream`）都由工厂返回，返回时已经就绪。曾经：`SshConnection` 有公开构造和公开的 `Start()`，这样造出来的连接没有重协商上下文，`StartRekeyAsync` 一调就抛 |
| **公开 `init` 只给调用方本该配置的东西** | 装配用的属性写 `internal init`，或者干脆 `internal`。曾经：`SshConnection.HostKey` / `Description` / `RekeyPolicy` 是公开 `init` |
| **公开接口 = 扩展点** | 只有宿主或第三方要**实现**它时才公开（`ISshTransportDialer`、`IHostKeyPolicy`、`ISshSigner`）。库内的回调接口是 `internal`；公开类型实现内部接口时用**显式实现**（`AgentForwarder`、`RemotePortForwarder` 对 `IIncomingChannelHandler`）。扩展点要从外面够得着：`internal` 类型上的注册方法不是扩展点 —— 曾经 `SshKeyExchangeFactory.Register` 除了一条测试谁也调不到，却让一张无锁的字典在运行期可写 |
| **不交出可变状态** | 不公开 `byte[]`（用 `ReadOnlyMemory<byte>` 或返回副本）、`List<T>`、可变 class；度量只公开 `Meter` 名，不公开可写的 `Counter<T>`。曾经：`SshConnection.SessionId` 是 `byte[]`，重协商拿的就是这个数组去算哈希 —— 调用方改一个字节，下一次重协商就坏 |
| **内部类型不许从公开方法漏出去** | 包括 internal 异常。曾经：公开的 `SshKexInitMessage.Decode` 会抛 internal 的 `SshWireFormatException` |

基准或脚本要用内部类型时，给它加 `InternalsVisibleTo`（基准本来就以 `-p:SignAssembly=false` 跑），**不要为了它把类型公开**。

### 4.2 命名

**前缀**

- 通用概念、与 BCL 或宿主撞名风险高的，带 `Ssh`：`SshConnection`、`SshChannel`、`SshCommand`、`SshException`、`SshPublicKey`、`SshEndPoint`、`SshTerminalModes`。
- 子协议 / 文件格式用自己的名字当前缀，不再叠 `Ssh`：`Sftp*`、`X11*`、`KnownHosts*`、`OpenSsh*`、`Putty*`。
- 接口或抽象基类的实现 = 限定词 + 基类型的核心名词，不加 `Ssh`：
  `PasswordCredential : SshCredential`、`KnownHostsPolicy : IHostKeyPolicy`、`LocalPortForwarder : PortForwarder`、`AesGcmCipherSuite : ISshCipherSuite`。
- 同一组兄弟类型前缀一致（`SshKeepAlivePolicy` 与 `SshRekeyPolicy`；曾经前者叫 `KeepAlivePolicy`）。
- **新增公开类型前，在全仓 grep 一遍它的简单名。** 与宿主撞名会逼调用方写别名。
  曾经撞名的 `SshChannelStream`、`SshJumpDialer`、`SshConfigBlock`、`OpenSshCertificate`、`TerminalModes`
  靠「删掉宿主那份重复实现」或改名解决了，别再造新的。

**后缀：一个后缀一个意思**

| 后缀 | 含义 | 形态 |
| --- | --- | --- |
| `Options` | 调用方传入的一组配置 | `sealed record`，见 4.3 |
| `Policy` | 带开关 / 阈值的行为规则；或可插拔的裁决者 | 规则：`readonly record struct`；裁决者：`I*Policy` 接口 |
| `Limits` | 一组上限 | `sealed record` |
| `Result` | 一次操作的**完整**结果 | `record` / `record struct` |
| `Kind` / `Reason` / `State` / `Mode` | 枚举：种类 / 原因 / 状态 / 做法 | 单数 |
| `EventArgs` | 事件参数 | `sealed class : EventArgs` |

不再引入同义后缀：`Settings`、`Info`、`Config`（`ssh_config` 领域除外）、`Shape`，也不再给「裁决结果」各造一套 `Verdict` / `Outcome` / `Decision`。
曾经：`SshConfigConnectSettings`（→ `SshConfigConnectOptions`）；`AgentForwardPolicy` 其实是 options（→ `AgentForwardOptions`）；
枚举叫 `SshStderrPolicy`（→ `SshStderrMode`）；只装退出状态的叫 `SshCommandResult`、完整结果反而叫 `SshCommandOutput`（→ `SshExitStatus` / `SshCommandResult`）。

**分析器保留的后缀**：公开类型不许用 `Flags`、`Attribute`、`Collection`、`EventHandler` 这类后缀表达别的意思（CA1711，全仓警告即错误）。
`[Flags]` 枚举用复数名（`SftpOpenModes`），不叫 `XxxFlags`；不是特性的类型不叫 `XxxAttribute`（`SftpExtendedField`，不是 `SftpExtendedAttribute`）。

**动词：一个动词一个意思**

| 动词 | 用在 | 例 |
| --- | --- | --- |
| `ConnectAsync` | 建立一条要握手的会话，返回**已就绪**的对象；是目标类型上的静态方法 | `SshConnection.ConnectAsync`、`SftpFileSystem.ConnectAsync`、`SshAgentClient.ConnectAsync` |
| `Open*Async` | 在已有连接上开通道 / 子资源 | `OpenShellAsync`、`OpenSubsystemAsync`、`OpenTcpTunnelAsync` |
| `Execute*Async` / `Run*Async` | 前者启动并交出句柄；后者跑完并交回完整结果 | `ExecuteAsync` → `SshCommand`；`RunAsync` → `SshCommandResult` |
| `Request*Async` | 发一个要等回复的协议请求 | `X11Forwarder.RequestAsync` |
| `Start` / `Start*Async` | 启动一个长驻的后台服务（监听） | 本地转发只绑本机端口，同步的 `LocalPortForwarder.Start`；远程转发要等服务端应答，`RemotePortForwarder.StartAsync`。**有没有 `Async` 看有没有网络往返** |
| `LoadAsync` / `Parse` / `TryParse` | 读文件 / 解析**文本** | `KnownHostsFile`、`SshConfigFile`、`SshPublicKey.Parse` |
| `Decode` / `Encode` | 解析 / 生成 **wire 字节** | `SshPublicKey.Decode`、`SshTerminalModes.Encode` |

曾经：连接有 `SshConnectionFactory.ConnectAsync` 与 `options.ConnectAsync()` 两个入口；`Parse(blob)` 解析的是 wire 字节。

**其它**

- 同一个概念在所有类型上同名（`SshCommand` 与 `SshShell` 都叫 `StandardOutput` / `CompleteStandardInputAsync`；并发上限一律 `MaxConnections`）。
  曾经 `SshShell` 叫 `Output` / `CompleteInputAsync`，X11 / agent 转发里的上限叫 `MaxConcurrentChannels`。
- 布尔：`Is*` / `Has*` / `Can*` / `Allow*`。**不安全的开关用 `Dangerous` 前缀**（`DangerousAcceptAnyHostKeyPolicy`）。
- 枚举：非 `[Flags]` 用单数，`[Flags]` 用复数；wire 枚举写明底层类型，取值照规范。
- 异步方法一律带 `Async` 后缀。
- 协议字符串走 `SshAlgorithmNames` / `SshProtocolNames`（通道类型、请求名、子系统名）/ `SftpExtensionNames`，
  消息号走 `SshMessageNumber`，不写字面量；KEX / 认证专属号段（30–49、60–79）在各自的实现里起具名常量。

### 4.3 类型形态

- **Options**：`sealed record` + `init` + `public static X Default { get; } = new();`。
  入口参数写 `XxxOptions? options = null`，内部用 `options ?? XxxOptions.Default`。与它配置的类型放在同一文件。
- **record 的成员必须不可变。** 不放 `byte[]`、`List<T>`、可变 class：`with` 和静态的 `Default` 会把同一个可变对象分给所有人，
  数组成员还会让 record 的相等比较退化成比引用。要「改一项」就给不可变类型一个 `With(...)`（`SshTerminalModes.With`）。
  曾经：`SshShellOptions.Default.Modes` 是同一个可变的 `TerminalModes`，任何一处 `Default.Modes.Set(...)` 都会改掉全局默认。
- **开关型策略**：`readonly record struct`，带 `static Disabled` 与 `IsEnabled`。
- **非法值在构造时就抛**，不留一个要人记得去调的 `Validate()`。
  由几条 `init` 属性与 `With*` 链拼成、做不到构造时校验的（`SshAlgorithmSet`），校验方法写成 `internal`，
  由唯一消费它的入口代为调用（`SshConnection.ConnectAsync`）—— 调用方永远不需要记得调。
  曾经：`SshRekeyPolicy.Validate()` 是公开的、靠 `ConnectAsync` 代调，`KeepAlivePolicy` 干脆不校验。
- **静态默认实例**：class / record 用缓存的 `{ get; } = new()`，而且这个实例必须不可变；struct 用 `=>`。
  名字只用 `Default`（推荐默认）、`Disabled`（关）、`Empty`（空），不再造 `Shared` / `Ideal` 这类同义词（`internal` 的测试工具除外）。
- **枚举的零值必须是安全的那个**（未知 / 拒绝 / 失败）。
  曾经：`SshHostKeyDecision.Accept` 是 0，于是 `default(SshHostKeyVerdict)` 就等于「接受」；`SshAuthOutcome.Success`、`KnownHostStatus.Known` 也是 0。
- **接口的默认实现只许默认到安全的一边。** 曾经：`ISshSigner.IsLocalAndCheap => true` ——
  一个忘了覆写它的硬件签名器，会在服务端还没认这把公钥时就让用户去按硬件键。
- **公开 API 不返回元组**，用具名的 `record`（解构照样能用）。曾经：`ExecuteAndReadAsync`、`SshCommand.ReadToEndAsync`、`InMemoryTransport.CreatePair`。
- **同一个功能只有一个公开入口。** 曾经：「跑命令拿全部输出」有 `RunAsync` / `ExecuteAndReadAsync` / `ReadToEndAsync` 三个；
  全局请求有 `SendGlobalRequestAsync` / `SendGlobalRequestWithReplyAsync` 两个；`SshChannelStream` 既能 `new`、又能 `channel.AsStream()`。
- **参数之间不许自相矛盾；所有调用方都传同一个值的参数就删掉。**
  曾经：`SftpFileSystem.OpenAsync(…, mode, …, canRead, canWrite)` 的两个布尔是 `mode` 的重复；`ThrowIfError(treatEndOfFileAsError)` 的 19 处调用全传 `true`。
- **惯用法要做到惯用的事。** 调用方用平台的惯用法表达意图时，库要照那个意思办：完成 `StandardInput` 这个 `PipeWriter`
  就是 EOF，库照 `SendEofAsync` 的样子冲干净再发 `CHANNEL_EOF`。曾经只有 `CompleteStandardInputAsync` 才发，
  `StandardInput.Complete()` 之后远端的 `cat` 会一直等下去。
- **所有权**：接收可释放对象的参数叫 `ownsXxx`，默认 `true`（交出去就归它），要保留所有权的调用方显式传 `false`。
- **释放**：网络资源只实现 `IAsyncDisposable`，释放路径不抛。持有密钥材料的类型实现 `IDisposable` 并清零，
  而且**返回类型要让调用方看得出它可释放**（`SshPrivateKeyFile.LoadAsync` 返回 `InMemorySshSigner`）。
  曾经它返回不可释放的 `ISshSigner`，宿主从来不释放私钥，要用时还得向下转型。
- **公开的计数 / 统计属性要线程安全**（`Interlocked` / `Volatile`）。
- **异步**：公开方法返回 `ValueTask`（`Stream` 覆写除外）；`CancellationToken cancellationToken = default` 放最后；
  库内每个 `await` 都带 `ConfigureAwait(false)`；没有同步重载（architecture.md 原则 1）。回调的形状是 `Func<…, CancellationToken, ValueTask<T>>`。
- 参数校验用 `ArgumentNullException.ThrowIfNull`、`ArgumentException.ThrowIfNullOrEmpty`、`ArgumentOutOfRangeException.ThrowIf*`。

### 4.4 文件、命名空间与依赖

- **命名空间 = 文件夹。**
- **一个类型一个文件，文件名 = 类型名。** 公开类型只允许**专为它服务**的小伙伴同住：它的 `*Options`、它的结果 record、它专属的枚举。
  `internal` 类型同样要求文件名对得上里面的类型 —— 打开一个文件，应当能从名字猜到里面是什么。
  曾经：文件名对不上任何类型的 `SshConnectionOpen.cs`（里面是状态枚举、工厂、扩展方法、结果、异常五样东西）、
  `SshExceptions.cs`、`ForwardStatistics.cs`、`SftpConstants.cs`、`SshCompressor.cs`、`SshKexTransport.cs`、`SshConfigMatch.cs`；
  一个文件装一整族平级类型的 `SshCredential.cs`、`IHostKeyPolicy.cs`（各 7 个）。
- **类型放在它所属的领域。** 曾经：连接级的 `SshConnectionLimits` 在 `Channels/SshChannelOptions.cs` 里；`SshKexInitMessage` 在 `Protocol/`，却离不开 `Crypto/` 的算法清单。
- **partial 文件叫 `类型名.方面.cs`**（`SshConnection.Rekey.cs`、`SshConfigFile.Connect.cs`），`SshConnectionXxx.cs` 这种名字留给独立类型。
- **扩展方法类叫 `被扩展类型名Extensions`，一个类型至多一个。** 曾经：`SshConnection` 有 `SshConnectionSessions`、`SshConnectionTunnels`、`SshConnectionExtensions` 三个。
- **异常放哪**：`Diagnostics/` 只放基类 `SshException`、`SshFailureReason`、`SshPhase` 与连接级异常（Connect / Negotiation / Protocol / ConnectionClosed）；
  领域异常放在自己的领域文件夹，一个文件一个（`Sftp/SftpException.cs`、`Channels/SshChannelException.cs`、`Auth/SshAuthenticationException.cs`、`Crypto/Kex/SshKeyExchangeException.cs`）。
- **依赖方向**：`Protocol/` 与 `Diagnostics/` 是底座，**不 `using` 本库任何其它文件夹** —— 现在两边都干净，保持。
  其余文件夹之间现有的环（`Transport`↔`Session`、`HostKeys`↔`Keys`、`Auth`↔`Keys`）**不许再加新的**。
- **单个类型超过约 800 行就先想拆**，拆成 internal 协作者，而不是更多 partial —— 拆成十个 partial 也还是一个上帝类。现状见 4.8。

### 4.5 失败与异常

- **公开方法抛出的异常都派生自 `SshException`。** 例外只有 BCL 惯例：参数校验的 `Argument*Exception`、
  误用的 `InvalidOperationException` / `ObjectDisposedException`、`Stream` 契约里的 `IOException` / `NotSupportedException`。
  **库自己的编程错误**（算法清单与工厂表对不上之类）抛 `InvalidOperationException`，不借 `SshException` 的名义 —— 那不是对端或配置的问题。
- **`SshFailureReason` 必须说真话。** 没有合适的值就加一个，不许借一个相近的。
  只有异常类型本身就只对应一个原因时，才可以在类型里固定（`SshCommandFailedException` → `CommandFailed`、`SshNegotiationException` → `NegotiationFailed`）。
  曾经：`SshForwardException` 永远是 `Unsupported`（端口被占、被拒、xauth 失败都一样），私钥 / 证书 / agent 异常也一律 `Unsupported`；
  exec / pty-req / shell 请求被拒报成 `ChannelOpenFailed`（通道其实开成功了）；跳板成环报成 `ProxyRefused` —— 而它会被当成**可重试**。
- **新加的 `SshFailureReason` 值，同一个 PR 里在宿主的 `SshInterop.Localize` 决定翻不翻**：原因码足以说清的，加一条五语言的文案；
  消息里带着路径、指纹、端口这类具体信息的，照用原文，并在 `Localize` 的注释里写明为什么不翻。
- **异常消息是写给开发者的诊断文本，不是界面文案。** 宿主按 `Reason` 与结构化属性本地化，不在宿主里解析句子（五份 resx 的规矩，见仓库根 AGENTS.md）。
  所以界面要用的信息必须有结构化的出处（枚举、属性），不能只活在消息句子里；消息里也不写 Markdown 和表情符号。
- **对端给的文本**先过 `PeerText.Sanitize` 再进消息，原文另存一个属性。
- **公开 API 里不出现字符串形式的「原因码」**，用枚举。
  曾经：`ForwardErrorEventArgs.Reason` 是 `"accept"` / `"relay"` 这样的字符串（→ `ForwardErrorReason`）；`SftpException` 的操作名是二十来个自由书写的中文短语（→ `SftpOperation`）。
- 必需的数据走构造参数，可选的附加数据走 `init`。

### 4.6 简洁与可维护

- **没有调用方的公开成员直接删**，不「先留着」—— 不发包，没有兼容负担。
  曾经（审查时零引用）：`SshConnectionState`、`SshConnection.SetIncomingChannelHandler`、`SshChannel.TryReadEvent`、
  `SshDialTarget.HopIndex`、`ISshKeyExchange.RequiresGroupNegotiation`、没实现的 `DiffieHellmanGroupExchangeSha256` 与它在交换哈希里的输入。
- **库里有的，宿主不许再写一份；库里的不合用，就改库**，库和宿主在同一个 PR 里改。宿主那边只留**适配层**：
  `ProxyTransportDialer`（按宿主的代理设置挑 `DialerChain` 的拨号器）、`SyncCompatibleStream`（给库的通道流补上同步读写）。
  曾经：宿主各有一份自己的 `SshChannelStream`、`SshJumpDialer`、ssh_config 解析器、SOCKS5 / HTTP CONNECT 握手（`ProxyStreamConnector`）、
  `.pub` 行解析与指纹计算 —— 都删了，改用库。遇到这种情况先问清楚该留哪一份，别再加第三份。
- **同样的逻辑出现第二份，就提成 internal 帮手。** 曾经：通配符匹配（`KnownHostsFile` 与 `SshConfigFile` 各一份，→ `Protocol/HostPatterns`）、
  agent 消息号（`SshAgentWire` 与 `SshAgentMessage`）、`SafeShutdownSend`（三个转发器各一份）。
- **注释跟着代码改。** 结构变了，描述结构的注释一起改；一个成员只写一个 `<remarks>`。
  曾经：`VelaShell.Ssh.csproj` 头部的文件夹清单里有不存在的 `Threading/` 和 `Client/`；`InMemoryTransport` 还写着「链路特征模拟尚未做」，而 `DelayedStream` 早就在了。

### 4.7 改公开面时的自查

公开面门禁是关着的（3.4），把关只能靠人。改了公开面的 PR，提交前过一遍：

1. 新增的 `public` 符合 4.1 的三条标准之一吗？它不是协议管道吗？
2. 名字过了 4.2 的前缀、后缀、动词三张表吗？在全仓 grep 过同名吗？
3. record 成员都不可变？枚举零值是安全的？非法值在构造时就抛？（4.3）
4. 一个类型一个文件、放在对的文件夹、没有新增反向依赖？（4.4）
5. 抛出的异常带着真实的 `Reason`，宿主的 `SshInterop.Localize` 做了决定？（4.5）
6. 有没有让某个旧入口变成了重复？有就一起删掉。
7. PR 描述列出新增 / 删除 / 改名的公开成员；同一个 PR 改掉宿主的调用方；
   影响到 velashell-docs 的 `getting-started.md`、architecture.md §6 / §8 或 `spec/` 的，按第五节同步中英两边。

### 4.8 还没改完的

审查查出来的问题里，下面几项**没有**在那一轮改：它们要么是纯重构、该单独开 PR，要么要先补结构化的数据。动到相关代码时优先处理。

- **两个上帝类。** `SshConnection` 约 2,960 行（四个 partial，`SshConnection.cs` 自己 1,576 行），`SshChannel` 1,388 行，都远过 4.4 的 800 行。
  拆法：`SshChannel` 的收发窗口与 stdin 泵提成 internal 协作者；`SshConnection` 的收包分发、全局请求账本同理。
  纯重构，不混行为改动，单独开 PR（已记入仓库根 `feature-plan.md`）。
- **还透传库原文的界面文案。** 宿主已按 `Reason` 翻了连接、超时、保活、私钥口令、agent 拒绝、通道请求被拒、转发被拒几类，代理失败由
  `ProxyTransportDialer` 用界面语言拼。仍是库的中文原文的有：认证的逐条尝试记录（`SshAuthAttempt.ToString` 与 `Detail`）、
  通道开不成时的建议（`SshChannelException` 的消息）、带具体信息的私钥 / 证书 / 配置 / 端口占用消息、`KnownHostLookup.CertificateProblem`。
  要翻就先给它们加结构化的出处（枚举 / 属性），不要在宿主里解析句子。

---

## 五、文档在哪

**本库的设计文档与行为规格在 velashell-docs 仓库的 `zh/ssh/` 与 `en/ssh/`**，
与宿主其余文档同一条规矩（仓库根 AGENTS.md 第二节）：

```
velashell-docs/
  zh/ssh/design/architecture.md     架构与原理、净室论证、里程碑与逐条决策记录
  zh/ssh/spec/                      行为规格 —— 实现的唯一依据
  zh/ssh/getting-started.md         面向调用方的上手
  en/ssh/…                          英文镜像,文件一一对应
```

**改了行为就要同步改规格**：在 velashell-docs 提 PR，中英两边都改，两个 PR 在正文里互相引用、一起合。
规格先行（纪律 2）意味着更好的顺序是**先改规格、再改实现**。

留在本目录的只有 `README.md`、`LICENSE`、`NOTICE.md` 与本文件 —— 它们服务的是「在这里写代码」。

---

## 六、当前状态

M0–M5 与面向使用者的连接层已完成，宿主已从 Tmds.Ssh 切换到本库。
里程碑与逐条决策记录见 architecture.md §11.2。
2026-09-25 做过一次全库 API 审查并整改（规则见第四节，经过见仓库根 `plan.md` §117），没改完的两项列在 4.8。
