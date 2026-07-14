# VelaShell.Core

> 领域核心层 —— 模型、服务契约、持久化抽象与本地化。**不依赖任何 UI 框架。**

`VelaShell.Core` 是整个解决方案的地基。它定义了「VelaShell 是什么」，而不关心「界面如何呈现」或「底层用哪个库实现」。所有跨层共享的领域概念、接口契约与不可变数据结构都集中在这里，因此它可以被独立编译、独立测试，并在未来整体替换上层框架而无需改动。

## 🎯 设计原则

- **无 UI 依赖**：只引用 `Microsoft.Extensions.Logging.Abstractions` 与 `ReactiveUI`（用于 `ReactiveObject` 数据模型），**不引用 Avalonia**。
- **接口优先**：对外只暴露接口（`I*Service`、`I*Store`、`I*Wrapper`），具体实现落在 `VelaShell.Infrastructure`。上层通过 DI 注入依赖，便于 Mock 与替换。
- **SSH 库中立**：Core **不引用 SSH.NET**。SSH/SFTP 能力全部经 `Ssh/` 下的中立抽象（`ISshClientWrapper`、`ISftpClientWrapper`、`SftpEntry`、`SshClientException` 等）访问，具体 SSH.NET 依赖只存在于 Infrastructure，便于日后整体替换传输实现。

## 🗂️ 目录结构

| 目录 | 职责 |
|------|------|
| `Models/` | 领域模型与枚举：`ConnectionInfo`、`ServerGroup`、`SshSession`、`AppSettings`、`KnownHost`、`QuickCommand`、`TunnelConfig`、`TransferTask`、`AuditEntry`、`TerminalColorScheme` 等，多为 `ReactiveObject` 或不可变记录。 |
| `Data/` | 持久化抽象与逻辑：`IAppDataStore`、`ISessionRepository`、`ISettingsService`、`IRecentConnectionService`、`IAuditLogService`、`ISecretProtector`（凭据加密契约），以及 `JsonDataStore` 等中立实现。 |
| `Ssh/` | SSH/SFTP 中立抽象层：客户端/Shell/SFTP 包装接口、`HostKeyVerification`（主机指纹 TOFU 校验）、`PortForwarding`、`Utf8StreamDecoder`、`SecurityAlertService`、异常类型。 |
| `Sftp/` | 文件传输领域逻辑：`ISftpService`、`ITransferManager` 及其实现、`ThrottledStream`（限速流）。 |
| `Tunnels/` | 端口转发隧道契约 `ITunnelService`。 |
| `Services/` | 跨层服务契约与逻辑：`IThemeService`、`ISessionMetricsService`（CPU/内存/网速采集）、`SettingsPreviewService`。 |
| `Recording/` | 会话录制存储契约 `ISessionRecordingStore`。 |
| `Localization/` | `ILocalizationService` + `LocalizationService`，运行时语言切换。 |
| `Resources/` | 强类型本地化资源。`Strings.resx`（英文，开发语言）+ `zh-Hans`/`zh-Hant`/`ja`/`ko` 卫星资源。`Strings.cs` 为**手写**的强类型访问层（`Get`/`Format` + 常用键属性），非设计器生成。 |

## 🔑 核心思路

1. **稳定的领域词汇表**：连接、分组、会话、隧道、传输、审计等概念在此一次性定义，所有上层共用同一套模型，杜绝 DTO 转换与概念漂移。
2. **契约与实现分离**：Core 声明「需要什么能力」（接口），Infrastructure 提供「如何做到」（SonnetDB / SSH.NET / AES）。这让核心逻辑可在无数据库、无网络的环境下单元测试。
3. **可替换的边界**：SSH 库、存储引擎、加密方案都被接口隔离在 Core 之外，替换成本被约束在单一实现项目内。

## 🌐 本地化回退链

`NeutralLanguage=en`，`CurrentUICulture=en` 时直取主程序集无需 en 卫星。中文卫星按脚本中性文化命名（`zh-Hans`/`zh-Hant`），使 `zh-CN`/`zh-SG` → `zh-Hans`、`zh-TW`/`zh-HK`/`zh-MO` → `zh-Hant` 沿 .NET 标准回退链自动命中。

## 🔗 依赖关系

```text
VelaShell.Core  ──(被依赖)──►  Terminal / Infrastructure / Presentation / Controls / App
        │
        └─ 仅依赖：Microsoft.Extensions.Logging.Abstractions、ReactiveUI
```

> Core 是依赖图的汇点，不引用任何其他 VelaShell 项目。测试见 [`tests/VelaShell.Core.Tests`](../../tests/VelaShell.Core.Tests)。
