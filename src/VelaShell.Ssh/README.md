# VelaShell.Ssh

**宿主的 SSH 客户端库 —— 全异步、低分配、AOT 友好。** 宿主用它替换了 Tmds.Ssh。

一套**独立实现**的 SSH 客户端库：远程命令、交互式 shell（含 PTY）、SFTP、
端口转发与隧道。它不是任何现有库的 fork —— 见 [`NOTICE.md`](NOTICE.md)。

> 原本是独立仓库 `VelaShellLabs/velashell-ssh`，2026-09-23 并入本仓库，不再单独发 NuGet。
> **本目录按 MIT 授权**（[`LICENSE`](LICENSE)），与仓库其余部分的授权不同。
> 改这里的代码之前先读 [`AGENTS.md`](AGENTS.md) —— 本库有一条净室规程，宿主其余部分没有。

```csharp
// 路径原样交给文件系统，不展开 ~
string keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
using InMemorySshSigner key = await SshPrivateKeyFile.LoadAsync(keyPath);  // 持有私钥，释放时清零

var options = new SshConnectionOptions("root@example.com")
{
    Credentials   = [ new PublicKeyCredential(key),
                      new KeyboardInteractiveCredential(AskUserAsync) ],  // 2FA / OTP
    HostKeyPolicy = new KnownHostsPolicy(),
};

await using SshConnection conn = await SshConnection.ConnectAsync(options, ct);

SshCommandResult result = await conn.RunAsync("uname -a", cancellationToken: ct);
Console.WriteLine(result.StandardOutput);
```

宿主不直接用这些类型：`VelaShell.Core` 只认 `Ssh/` 下的中立抽象（`ISshClientWrapper` 等），
本库只在 `VelaShell.Infrastructure/Ssh/` 的实现里出现。

## 目录

| 目录 | 内容 |
| --- | --- |
| `Protocol/` | 报文编号、wire 编解码、算法名与协议字符串常量、主机名模式匹配 |
| `Transport/` | L1 拨号（TCP / SOCKS5 / HTTP CONNECT / 跳板 / 代理命令）、L2 帧层 |
| `Crypto/` | L3 KEXINIT 与算法协商、密码套件、KEX（含后量子混合）、压缩 |
| `Session/` | L4 会话状态机、发送闸门、收发泵、重协商、`SshConnection` |
| `Channels/` | L5 通道、流控窗口、`SshCommand` / `SshShell` |
| `Auth/` | 认证方法链、签名器 |
| `HostKeys/` | 主机密钥策略、known_hosts、`SshPublicKey` |
| `Keys/` | 私钥格式（OpenSSH 含加密、PuTTY `.ppk`）、证书、ssh-agent 客户端 |
| `Sftp/` | SFTP wire / 请求管线 / 文件系统 |
| `Forwarding/` | 本地 / 远程 / 动态转发、agent 转发、X11 |
| `Config/` | `ssh_config`（含 `Include` / `Match`） |
| `Diagnostics/` | 失败分类、结构化异常 |

## 依赖

**一个运行时依赖：`BouncyCastle.Cryptography`**（MIT），只用来做 BCL 缺失的密码学原语 ——
raw ChaCha20 块函数、独立 Poly1305、Ed25519、X25519、DH 标准群、ML-KEM-768、sntrup761、
Argon2id（`.ppk` v3）。**没有第二种用途。** BCL 有的一律走 BCL。版本登记在
`src/Directory.Packages.props`，与宿主的 `SshKeyService` 共用一条。

**我们不自己写密码学原语**，唯一的例外是 `Keys/BcryptPbkdf.cs`（加密的 OpenSSH 私钥的 KDF），
范围钉死，理由见 [`AGENTS.md`](AGENTS.md) §3.3。

## 文档

设计文档与行为规格在 velashell-docs 仓库（中英双语）：

| 文档 | 内容 |
| --- | --- |
| [`zh/ssh/getting-started.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/ssh/getting-started.md) | **上手** —— 连接、认证、命令、shell、SFTP、转发、读错误 |
| [`zh/ssh/design/architecture.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/ssh/design/architecture.md) | 架构与原理、独立性论证、性能设计、扩展点、里程碑与逐条决策 |
| [`zh/ssh/spec/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/ssh/spec) | **行为规格** —— 实现的唯一依据（报文字段表 + 时序图） |
| [`AGENTS.md`](AGENTS.md) | 开发约定，含净室规程 |
| [`NOTICE.md`](NOTICE.md) | 独立性声明与第三方组件 |

## 构建与测试

随解决方案一起构建；单独跑库的测试（在仓库根目录）：

```bash
dotnet test tests/VelaShell.Ssh.Tests/VelaShell.Ssh.Tests.csproj
# 763 绿；22 条互操作用例没有服务端时跳过（MSTest 记为「已跳过」）
```

**互操作用例**（对着一台真的 OpenSSH 跑，需要 docker）：

```powershell
pwsh scripts/ssh/interop/Start-TestServer.ps1 -X11   # -X11 会装 xauth 并打开 X11Forwarding
. artifacts/interop/env.ps1
dotnet test tests/VelaShell.Ssh.Tests/VelaShell.Ssh.Tests.csproj --filter "TestCategory=Interop"
pwsh scripts/ssh/interop/Stop-TestServer.ps1
```

**性能基准**（BenchmarkDotNet，单文件应用，不进 CI 门禁）。Release 在本仓库意味着强名签名，
本机没有 `VelaShell.snk`，所以关掉签名：

```bash
dotnet run -c Release -p:SignAssembly=false scripts/ssh/benchmarks/benchmarks.cs
dotnet run -c Release -p:SignAssembly=false scripts/ssh/benchmarks/benchmarks.cs -- --filter '*Wire*'
```

> 别在命令行传 `--job` —— 它会覆盖脚本里指定的进程内工具链，
> 而单文件应用没有 `.csproj` 给 BDN 默认的工具链找。原因写在脚本头部。
