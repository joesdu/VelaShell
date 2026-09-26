# VelaShell.Infrastructure

> 基础设施层 —— Core 契约的具体实现：SSH/SFTP、隧道、SonnetDB 持久化、凭据加密、云同步。

`VelaShell.Infrastructure` 回答「如何做到」。`VelaShell.Core` 声明了一组接口（存储、SSH、密钥、录制……），本项目用真实的第三方库把它们逐一实现。因为所有具体依赖（VelaShell.Ssh、SonnetDB、DPAPI）都被收拢在这里，上层与领域层可以保持纯净、可测试。

## 🗂️ 目录结构

| 路径 | 职责 |
|------|------|
| `Persistence/SonnetDbEngine.cs` | **唯一持久化引擎**：封装嵌入式 [SonnetDB](https://github.com/IoTSharp/SonnetDB) 多模型数据库实例，业务数据走文档集合、时间序列数据走时序引擎，退出时统一刷盘。 |
| `Persistence/SonnetDb*Service.cs` | 各 Core 存储契约的 SonnetDB 实现：`AppDataStore`、`SessionRepository`、`SettingsService`、`RecentConnectionService`、`AuditLogService`、`HostKeyService`、`SessionRecordingStore`。 |
| `Persistence/AesSecretProtector.cs` | `ISecretProtector` 实现：连接密码与私钥口令以 **AES-256-GCM** 加密落盘；Windows 上密钥文件再经 DPAPI（CurrentUser）包裹。 |
| `Persistence/VelaShellStoragePaths.cs` | 数据目录、密钥文件等路径解析（`~/.velashell`）。 |
| `Persistence/SonnetDbJson.cs` | 文档序列化辅助。 |
| `Persistence/SonnetDbPluginDataStore.cs` `SonnetDbPluginTimeSeries.cs` | 插件数据的宿主侧后端：KV/机密走 `plugin_data` 文档集合（复合主键 `<插件id>\|<种类>\|<键>`），时序走 `pts_<插件命名空间>_<短名>` measurement；两者都按插件 id 命名空间隔离，卸载时整体清除。 |
| `Persistence/SonnetDbHostKeyService.cs` | `IHostKeyService` 实现：`known_hosts` 文档集合读写与指纹比对。 |
| `Ssh/SshConnectionService.cs` | `ISshConnectionService` 实现：连接、认证、Shell 会话生命周期管理。 |
| `Ssh/VelaSshClientWrapper.cs` `VelaSftpClientWrapper.cs` `ShellStreamWrapper.cs` | 把 VelaShell.Ssh 的 `SshConnection` / `SftpFileSystem` / `RemoteProcess` 包装为 Core 的中立抽象（`ISshClientWrapper` 等）。 |
| `Ssh/SshConnectionAssembler.cs` | 由 `ConnectionInfo` 装出 `SshConnectionOptions`：凭据（口令/私钥/证书/agent/键盘交互）、跳板链、心跳与各段超时。 |
| `Ssh/VelaHostKeyPolicy.cs` | `IHostKeyPolicy` 实现：`known_hosts` 比对 → 首见/变更时弹窗 → 用户裁决写回，附带安全告警。 |
| `Ssh/HostTrustOnceCache.cs` | 主机指纹「仅本次信任」的进程内缓存（不落盘）。 |
| `Ssh/SshKeyService.cs` | `~/.ssh` 密钥枚举（类型 + SHA256 指纹，`.pub` 的解析与指纹都用库的 `SshPublicKey`）、RSA 密钥对生成、公钥导入。 |
| `Ssh/SessionMetricsService.cs` | 采集会话 CPU / 内存 / 网速指标。 |
| `Ssh/SshInterop.cs` `LibraryPortForwardHandle.cs` | VelaShell.Ssh 异常 → Core 中立异常（`VelaSsh*Exception`）的翻译，以及端口转发句柄。翻译**按异常类型分派，没有一处字符串解析**。 |
| `Ssh/ProxyTransportDialer.cs` `SyncCompatibleStream.cs` | 传输层接入：按代理设置选 SSH 库的拨号器（`DialerChain.Tcp` / `HttpConnect` / `Socks5`，握手全在库里），跳板走 `DialerChain.Jump`；`SyncCompatibleStream` 给库的通道流（`SshChannel.AsStream()`）补上同步读写，交给只认 `Stream` 的下游。 |
| `Ssh/OpenSshPrivateKey.cs` | 私钥的 OpenSSH 格式写出（自生成密钥用）。读取侧不再需要转换 —— VelaShell.Ssh 原生认 OpenSSH / PKCS#1 / PKCS#8。 |
| `Ssh/SshBackend.cs` | 关于页要展示的后端标识：名称、版本（程序集元数据）、许可与项目地址。 |
| `Ssh/RemoteProcessService.cs` | `IRemoteProcessService` 实现：远端进程快照采集（相邻两次采样算瞬时 CPU）与信号发送。 |
| `Pty/ConPtyShellStream.cs` | Windows ConPTY 本地终端流（本地 Shell 会话）。 |
| `Ftp/` | FTP/FTPS 后端：`FtpFileService`（远程文件操作）、`FtpConnectionPool`（控制连接池）、`FluentFtpInterop`（FluentFTP 异常 → Core 中立异常的翻译）。 |
| `Sftp/RoutingRemoteFileService.cs` | 远程文件操作的**协议路由**：按会话归属把调用分派给 FTP 后端或 SSH 上的 SFTP 实现。之所以能这么干，是因为 `ISftpService` 全部以 `sessionId` 为键、返回协议无关的 `RemoteFileInfo` —— 文件浏览器、传输管理器、限速、拖放对新增协议零改动。 |
| `Import/` | 从其他工具导入会话：`WinScpImportService`（含 `WinScpCrypto`）、`XshellImportService`（`XshellCrypto` + `XshellIniParser` + `Rc4`）、`SshConfigImportService`（解析、`Include` 展开与 `Match` 判定都用 SSH 库的 `SshConfigFile`，路径展开用 `SshPathResolver`）、`SessionImportWriter`（去重后写入仓储，并把 `IdentityFile` / `ProxyJump` 落成私钥认证与跳板引用）。 |
| `Diagnostics/` | `PingTraceRouteService`（逐跳路由追踪）与 `MmdbIpGeolocationService`（MaxMind 库离线解析 IP 归属地）。 |
| `Tunnels/TunnelService.cs` | 本地(`-L`)/远程(`-R`)/动态 SOCKS5(`-D`)端口转发统一管理。 |
| `Sync/GistSyncService.cs` `GistApiClient.cs` | GitHub Gist 云同步：设置/连接/片段同步到私密 Gist，支持版本历史与可选 PBKDF2 + AES-256-GCM 端到端加密。 |
| `Plugins/` | **插件运行时**（宿主侧）：`PluginManager`（发现 → 装载 → 激活 → 停用/卸载，每插件一个可收集 ALC，单插件异常只把它自己标记为 Failed）、`PluginContext`、`PluginDescriptor`、`PluginManagerOptions`、`PluginPermissionGate`（敏感能力的用户裁决与记忆）、`IPluginDataStore`。 |
| `Plugins/Capabilities/` | SDK 能力接口的宿主实现：`SessionsCapability`、`RemoteExecCapability`、`RemoteFsCapability`、`ProtectedSecretsCapability`、`HostInfoCapability`、`PluginEventHub`、`TracePluginLogger`，以及无 UI 宿主下的空实现（`NullUiApi`/`NullCommandsApi`）。 |
| `Plugins/Isolated/` | 隔离模式的宿主侧：`PluginProcessClient`（建管道 + 一次性令牌、拉起 [`VelaShell.PluginHost`](../VelaShell.PluginHost)、握手、观察进程退出）、`PluginCapabilityRouter`（把 RPC 分发到**与进程内插件同一套**能力实现，权限/节流单点生效）、`IPluginEmbedHost`。 |
| `DependencyInjection/InfrastructureServiceCollectionExtensions.cs` | 本层所有实现的 DI 注册入口；「怎么连上去」已整体搬进 `Ssh/SshConnectionAssembler.cs`（超时/心跳、凭据、主机密钥策略、ProxyJump 链），这里只做注册与转调。 |
| `DependencyInjection/PluginServiceCollectionExtensions.cs` | 插件运行时的装配：插件发现根（安装目录 `plugins/` + 用户数据目录 `plugins/`）、能力注册与权限闸门接线。 |

## 🔑 核心思路

- **单引擎持久化**：一个 SonnetDB 实例同时承载文档模型（连接/分组/设置）与时序模型（连接历史/审计/录制），避免多存储引擎的复杂度。接口在 Core、实现在此。
- **安全默认值**：凭据静态加密（AES-256-GCM + 本地密钥文件，Windows 再叠加 DPAPI）；主机指纹 TOFU 校验防中间人。
- **传输可替换**：`VelaShell.Ssh` 的类型只出现在本项目 `Ssh/` 下的包装类中，异常也在 `SshInterop` 一处翻译为 Core 的 `VelaSsh*Exception`；一旦更换传输库，改动被约束在这一层。（本项目先后基于 SSH.NET、Tmds.Ssh，现已整体迁到 VelaShell.Ssh —— 每次迁移都只动了 `Ssh/` 与 DI 装配。）
- **协议可扩展**：远程文件能力对上只有 `ISftpService` 一个面孔，FTP 是经 `RoutingRemoteFileService` 按会话分派进来的第二个后端 —— 文件浏览器、传输、限速、拖放都不知道有第二种协议存在。
- **插件故障隔离**：插件运行时也落在这一层。进程内插件各用一个可收集 ALC、异常只影响自身；声明 `isolated` 的插件整体外移到独立进程，但两种模式共用同一套能力实现，权限与纪律只有一处。
- **平台守卫**：`System.Security.Cryptography.ProtectedData`（DPAPI）仅 Windows 可用，非 Windows 平台已在代码中守卫降级。

## 🔗 依赖关系

- **引用**：`VelaShell.Core`（实现其契约）、`VelaShell.PluginSdk`（插件能力接口）。
- **包**：`SonnetDB.Core`、`FluentFTP`、`MaxMind.Db`、`BouncyCastle.Cryptography`、`System.Security.Cryptography.ProtectedData`。SSH 后端 `VelaShell.Ssh` 是**工程引用**（本仓库 `src/VelaShell.Ssh`），不是 NuGet 包。
- **被引用**：`VelaShell`（App，仅在组合根装配，不被 Presentation/Controls 直接引用）。

> 需 `AllowUnsafeBlocks`（ConPTY / 加密互操作）。测试见 [`tests/VelaShell.Infrastructure.Tests`](../../tests/VelaShell.Infrastructure.Tests)；此外 `InternalsVisibleTo` 暴露给 [`tests/VelaShell.Core.Tests`](../../tests/VelaShell.Core.Tests)，SSH 包装类与异常翻译的白盒测试放在那里（`Ssh/VelaSshClientWrapperTests`、`SshInteropTests`）。
