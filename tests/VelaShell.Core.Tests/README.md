# VelaShell.Core.Tests

> [`VelaShell.Core`](../../src/VelaShell.Core) 的单元测试。

验证领域层的纯逻辑，无需 UI、数据库或网络，全部以 Mock/内存实现驱动。

## 覆盖范围

| 目录 | 被测对象 |
|------|----------|
| `Models/` | 模型序列化、`TerminalColorScheme` 解析。 |
| `Data/` | `JsonDataStore`、`SessionRepository`、`SettingsService` 读写与迁移。 |
| `Ssh/` | SSH/SFTP 中立包装、`HostKeyService` 指纹校验、`Utf8StreamDecoder` 增量解码。 |
| `Sftp/` | `SftpService`、`TransferManager` 传输逻辑与限速。 |
| `Tunnels/` | `TunnelService` 端口转发。 |
| `Services/` | `SessionMetrics` 指标计算。 |
| `Sync/` | `SyncCrypto` 云同步加密（PBKDF2 + AES-256-GCM）。 |
| `Resources/` | 本地化回退链（`zh-Hans`/`zh-Hant`/`ja`/`ko`）。 |

## 运行

```bash
dotnet test tests/VelaShell.Core.Tests/
```
