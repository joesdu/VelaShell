# VelaShell.Core.Tests

> [`VelaShell.Core`](../../src/VelaShell.Core) 的单元测试。

验证领域层的纯逻辑，无需 UI、数据库或网络，全部以 Mock/内存实现驱动。

## 覆盖范围

| 目录 | 被测对象 |
|------|----------|
| `Models/` | 模型序列化、`TerminalColorScheme` 解析、`UserPathResolver`（相对路径以用户主目录为基准，不落到进程工作目录）。 |
| `Ssh/` | `SshConnectionService` 生命周期、凭据装配（`SshCredentialSetupTests`）、私钥格式（`SshKeyServiceFormatTests`、`LegacyPrivateKeyFormatTests`——PKCS#1 / PKCS#8 / OpenSSH 三种都必须直接可用）、OpenSSH 用户证书（`OpenSshCertificateTests`、`SshCertificateIntegrationTests`），以及 SSH 包装与异常翻译（`VelaSshClientWrapperTests`、`SftpEntryMappingTests`、`SshInteropTests`——经 Infrastructure 的 `InternalsVisibleTo` 白盒访问）。另有几组对着真实 OpenSSH 跑的集成用例（`SftpSymlinkIntegrationTests`、`RemoteSha256IntegrationTests`、`RemoteShellProbeTests`），靶机不在时记为未执行而非通过。 |
| `Diagnostics/` | 路由追踪的逐跳判定（`TraceAnalysisTests`）。 |
| `Processes/` | 远端进程探针的解析与瞬时 CPU 计算（`RemoteProcessProbeTests`）。 |
| `Sftp/` | `SftpService`（含并发特征化与独立 SFTP 契约）、`SerializedSftpService`、`TransferManager` 传输逻辑与限速。 |
| `Tunnels/` | `TunnelService` 端口转发。 |
| `Services/` | `SessionMetrics` 指标计算、`SettingsPreviewService`。 |
| `Sync/` | `SyncCrypto` 云同步加密（PBKDF2 + AES-256-GCM）。 |
| `Resources/` | 本地化回退链（`zh-Hans`/`zh-Hant`/`ja`/`ko`）。 |

## 运行

```bash
dotnet test tests/VelaShell.Core.Tests/
```
