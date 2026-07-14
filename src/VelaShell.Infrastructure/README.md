# VelaShell.Infrastructure

> 基础设施层 —— Core 契约的具体实现：SSH/SFTP、隧道、SonnetDB 持久化、凭据加密、云同步。

`VelaShell.Infrastructure` 回答「如何做到」。`VelaShell.Core` 声明了一组接口（存储、SSH、密钥、录制……），本项目用真实的第三方库把它们逐一实现。因为所有具体依赖（SSH.NET、SonnetDB、DPAPI）都被收拢在这里，上层与领域层可以保持纯净、可测试。

## 🗂️ 目录结构

| 路径 | 职责 |
|------|------|
| `Persistence/SonnetDbEngine.cs` | **唯一持久化引擎**：封装嵌入式 [SonnetDB](https://github.com/IoTSharp/SonnetDB) 多模型数据库实例，业务数据走文档集合、时间序列数据走时序引擎，退出时统一刷盘。 |
| `Persistence/SonnetDb*Service.cs` | 各 Core 存储契约的 SonnetDB 实现：`AppDataStore`、`SessionRepository`、`SettingsService`、`RecentConnectionService`、`AuditLogService`、`HostKeyService`、`SessionRecordingStore`。 |
| `Persistence/AesSecretProtector.cs` | `ISecretProtector` 实现：连接密码与私钥口令以 **AES-256-GCM** 加密落盘；Windows 上密钥文件再经 DPAPI（CurrentUser）包裹。 |
| `Persistence/VelaShellStoragePaths.cs` | 数据目录、密钥文件等路径解析（`%LocalAppData%/VelaShell`）。 |
| `Persistence/SonnetDbJson.cs` | 文档序列化辅助。 |
| `Ssh/SshConnectionService.cs` | `ISshConnectionService` 实现：连接、认证、Shell 会话生命周期管理。 |
| `Ssh/SshClientWrapper.cs` `SftpClientWrapper.cs` `ShellStreamWrapper.cs` | 把 SSH.NET 具体类型包装为 Core 的中立抽象（`ISshClientWrapper` 等）。 |
| `Ssh/JumpChainSshClientWrapper.cs` | 跳板机 / 多级跳转链连接。 |
| `Ssh/HostKeyService.cs` `HostTrustOnceCache.cs` | 主机指纹校验（TOFU / 人工确认）与「仅本次信任」内存缓存。 |
| `Ssh/SshKeyService.cs` | `~/.ssh` 密钥枚举、RSA 密钥对生成、公钥导入。 |
| `Ssh/SessionMetricsService.cs` | 采集会话 CPU / 内存 / 网速指标。 |
| `Ssh/SshNetInterop.cs` `SshNetPortForwardHandle.cs` | SSH.NET 互操作与端口转发句柄。 |
| `Pty/ConPtyShellStream.cs` | Windows ConPTY 本地终端流（本地 Shell 会话）。 |
| `Tunnels/TunnelService.cs` | 本地(`-L`)/远程(`-R`)/动态 SOCKS5(`-D`)端口转发统一管理。 |
| `Sync/GistSyncService.cs` `GistApiClient.cs` | GitHub Gist 云同步：设置/连接/片段同步到私密 Gist，支持版本历史与可选 PBKDF2 + AES-256-GCM 端到端加密。 |
| `DependencyInjection/InfrastructureServiceCollectionExtensions.cs` | 本层所有实现的 DI 注册入口。 |

## 🔑 核心思路

- **单引擎持久化**：一个 SonnetDB 实例同时承载文档模型（连接/分组/设置）与时序模型（连接历史/审计/录制），避免多存储引擎的复杂度。接口在 Core、实现在此。
- **安全默认值**：凭据静态加密（AES-256-GCM + 本地密钥文件，Windows 再叠加 DPAPI）；主机指纹 TOFU 校验防中间人。
- **传输可替换**：SSH.NET 只出现在本项目的 `Ssh/` 包装类中，一旦更换传输库，改动被约束在这一层。
- **平台守卫**：`System.Security.Cryptography.ProtectedData`（DPAPI）仅 Windows 可用，非 Windows 平台已在代码中守卫降级。

## 🔗 依赖关系

- **引用**：`VelaShell.Core`（实现其契约）。
- **包**：`SonnetDB.Core`、`SSH.NET`、`System.Security.Cryptography.ProtectedData`、`Microsoft.Extensions.DependencyInjection.Abstractions`、`Logging.Abstractions`。
- **被引用**：`VelaShell`（App，仅在组合根装配，不被 Presentation/Controls 直接引用）。

> 需 `AllowUnsafeBlocks`（ConPTY / 加密互操作）。测试见 [`tests/VelaShell.Infrastructure.Tests`](../../tests/VelaShell.Infrastructure.Tests)。
