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

---

## 四、文档在哪

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

## 五、当前状态

M0–M5 与面向使用者的连接层已完成，宿主已从 Tmds.Ssh 切换到本库。
里程碑与逐条决策记录见 architecture.md §11.2。
