# VelaShell.Core.Tests

> [`VelaShell.Core`](../../src/VelaShell.Core) 的单元测试。

验证领域层的纯逻辑，无需 UI、数据库或网络，全部以 Mock/内存实现驱动。

## 覆盖范围

| 目录 | 被测对象 |
|------|----------|
| `Models/` | 模型序列化、`TerminalColorScheme` 解析。 |
| `Ssh/` | `SshConnectionService` 生命周期，以及 Tmds.Ssh 包装与异常翻译（`TmdsSshClientWrapperTests`、`TmdsSshInteropTests`——经 Infrastructure 的 `InternalsVisibleTo` 白盒访问）。 |
| `Sftp/` | `SftpService`（含并发特征化与独立 SFTP 契约）、`SerializedSftpService`、`TransferManager` 传输逻辑与限速。 |
| `ZModem/` | ZMODEM 协议引擎：CRC、ZDLE 转义、子包、帧编解码、收发端状态机，以及 **`LrzszInteropTests`**——其期望值按 lrzsz 的 `zm.c`/`zmodem.h` 定义**手工构造**，不经我们自己的编码器生成，避免「编码器与解码器一起错还全绿」的自证测试。 |
| `Tunnels/` | `TunnelService` 端口转发。 |
| `Services/` | `SessionMetrics` 指标计算、`SettingsPreviewService`。 |
| `Sync/` | `SyncCrypto` 云同步加密（PBKDF2 + AES-256-GCM）。 |
| `Resources/` | 本地化回退链（`zh-Hans`/`zh-Hant`/`ja`/`ko`）。 |

## 运行

```bash
dotnet test tests/VelaShell.Core.Tests/
```
