# VelaShell.Infrastructure.Tests

> [`VelaShell.Infrastructure`](../../src/VelaShell.Infrastructure) 具体实现的单元测试。

验证真实基础设施实现的行为：持久化、加密密钥、本地终端流与路径解析。

## 覆盖范围

| 文件 | 被测对象 |
|------|----------|
| `SonnetDbPersistenceTests` | SonnetDB 各存储服务的读写、时序/文档双模型持久化。 |
| `SshKeyServiceTests` | `~/.ssh` 密钥枚举、RSA 密钥对生成、公钥导入。 |
| `ConPtyShellStreamTests` | Windows ConPTY 本地终端流。 |
| `SessionMetricsServiceTests` | 会话 CPU/内存/网速采集。 |
| `VelaShellStoragePathsTests` | 数据目录与密钥文件路径解析。 |

## 运行

```bash
dotnet test tests/VelaShell.Infrastructure.Tests/
```
