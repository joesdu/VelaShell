# VelaShell AI 插件开发指南

这是一份与源码同目录的快速开发指南。它强调修改位置、边界和验证方式；用户可见的完整功能说明请看仓库的 `manual.md`，生态级插件规范请看 `velashell-docs`。

## 1. 开发前先确认边界

1. 先读本目录的 `README.md` 和 `Architecture.md`。
2. 需要改变 Agent 生命周期、计划模型或恢复契约时，优先修改 `src/VelaShell.AgentCore`，再在插件中实现适配器。
3. 需要改变模型供应商、工具、审批、上下文或 UI 时，只修改插件层，不把这些依赖带进 AgentCore。
4. 改用户可见行为时同步仓库用户手册和 `velashell-docs` 中对应的中英文文档。

## 2. 常见修改位置

| 需求 | 首选位置 |
| --- | --- |
| 新增宿主无关的计划/状态/恢复契约 | `src/VelaShell.AgentCore/` |
| 连接 Core 与插件执行引擎 | `Core/Runtime/` |
| 新增工具或工具目录 | `Agent/AgentToolbox.cs` 及相邻工具实现 |
| 修改审批、拒绝或副作用判断 | `Agent/Policy/`；最终执行保护仍在 `Chat/AgentExecution.cs` |
| 修改 Prompt 预算、历史裁剪或工具消息配对 | `Chat/PromptContextCompiler.cs` |
| 修改事件统计或运行卡片 | `Chat/AgentRunEvents.cs`、`Chat/AgentRunSnapshot.cs`、对应 UI |
| 修改检查点格式或恢复 | `Chat/RunCheckpointStore.cs`、`RunCheckpointWriter.cs` |
| 修改桌面交互 | `Ui/`；先遵循根目录 `DESIGN.md` 的资源和布局规则 |
| 修改 IM/外部消息接入 | `Bridge/` |
| 修改供应商目录、协议或凭据 | `Configuration/`、`AiHttp.cs` 及对应测试 |

## 3. 添加工具的规则

- 工具必须有稳定名称、明确参数和可解释的失败结果。
- 按 `ChatMode` 选择暴露范围；不要只在 UI 隐藏写工具。
- 有副作用的工具必须经过 Policy Gate 和现有 `AgentExecution` 账本。
- 工具结果要限制大小；不要把凭据、认证头或不必要的完整环境变量返回模型。
- 取消时明确区分“已停止”和“结果未知”，未知副作用不能自动重试。
- 工具名、参数形状或权限改变时补充旧配置/旧历史兼容测试。

## 4. 修改 Runtime 的规则

- Planner 不执行工具；Executor 不改变计划边界；Verifier 不依赖 UI 状态。
- 所有重试必须有上限，使用计划步骤的 `MaxAttempts`，不能用无限循环掩盖失败。
- 每个状态转换都要有明确的终态、暂停原因或恢复条件。
- 事件是事实，Snapshot 是投影；不要在 UI 或 Bridge 再维护一套计数器。
- 检查点是恢复材料，不是重放授权。恢复时先验证外部状态，再决定是否重新调用。
- 新增 Core 公共类型必须写完整 XML 文档；构建时将 CS1591 当错误处理。

## 5. 测试与验证

从插件项目目录的上级仓库根目录执行：

```powershell
# 编译插件和 AgentCore
dotnet build .\VelaShell.slnx --no-restore

# Runtime、Policy、Snapshot、Prompt、History、Checkpoint 聚焦测试
dotnet test .\tests\VelaShell.Plugin.Ai.Tests\VelaShell.Plugin.Ai.Tests.csproj `
  --no-restore --filter "FullyQualifiedName~Runtime|FullyQualifiedName~AgentPolicy|FullyQualifiedName~AgentRun|FullyQualifiedName~PromptContextCompiler|FullyQualifiedName~ChatHistoryMeta|FullyQualifiedName~BridgeAgentRunner|FullyQualifiedName~RunCheckpointStore"

# 检查所有公开 API 都有真实 XML 文档
dotnet build .\VelaShell.slnx --no-restore --no-incremental -warnaserror:CS1591
```

完整插件测试还包含本地命令和 Headless UI。若环境无法启动 PowerShell 或缺少图形运行条件，应记录具体失败原因，不把环境失败改成代码成功。

## 6. 提交前检查

- `git diff --check`；
- 新增文件是否已加入项目/解决方案（SDK 默认包含源码，但项目引用仍需确认）；
- Core 是否仍然不依赖插件、Avalonia 或供应商 SDK；
- 旧历史、旧检查点和旧配置是否仍能读取；
- UI 文案是否同步五份资源；
- 架构变化是否同步 `Architecture.md`、README 和外部设计文档。
