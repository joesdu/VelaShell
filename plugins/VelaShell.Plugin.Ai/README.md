# VelaShell AI 插件

VelaShell AI 是一个面向终端工作流的多供应商 AI 插件。它把聊天、附件、模型路由、工具调用、审批和可恢复 Agent 运行统一到同一套 Runtime 中，并同时服务桌面聊天面板与 Bridge 接入。

> 本 README 是源码入口，不是完整用户手册。详细配置和用户操作请看仓库根目录的 `manual.md`；生态级插件规范与威胁模型请看 [velashell-docs](https://github.com/VelaShell/velashell-docs)。

## 能力概览

- 支持多个自定义 OpenAI / Anthropic 兼容端点和模型，认证只用 API Key。VelaShell 不再代办账号登录或订阅授权，以降低账号安全和封号风险，减少维护成本并简化界面。需要登录时，可使用 [Magpie](https://usemagpie.ai) 或 [Astra](https://astra-gate.si)提供兼容端点，或在官方 Agent 工具中登录并通过 MCP 调用 VelaShell。
- Chat、Plan、Agent 三种工作模式。
- 远端 SSH/SFTP 工具、附件和 MCP 工具的统一工具箱。
- 工具审批、只读筛选、CallId 去重、取消和未知副作用保护。
- Prompt/Context 编译：历史裁剪、预算估算、工具消息配对和诊断。
- Planner–Executor–Verifier 编排、运行事件、Snapshot、Telemetry 和检查点恢复。
- 桌面 UI 与 Bridge 共享运行时状态，而不是各自维护一套 Agent 生命周期。

## 快速开始

1. 在 VelaShell 的 AI 设置中配置供应商、模型和凭据。
2. 打开 AI 聊天面板，选择 Chat、Plan 或 Agent 模式。
3. 需要远端工具时先建立 SSH 会话；需要 MCP 时在工具配置窗口启用服务器。
4. 对有副作用的工具按提示审批。Agent 运行可暂停、取消，并在检查点存在时恢复。

插件配置由宿主设置系统持久化；不要把 API Key、认证头或完整工具输出写入日志、Telemetry、历史元数据或检查点摘要。

## 架构地图

```text
ChatPanel / Bridge
        |
AgentRuntimeFactory
        |
RuntimeSession ── Policy ── AgentExecution ── AgentToolbox
        |                         |
  AgentCore                 Events → Snapshot → Telemetry
        |
Planner → Executor → Verifier
        |
Checkpoint / History / Recovery
```

依赖方向保持单向：

```text
VelaShell.Plugin.Ai  ──────>  VelaShell.AgentCore
```

`VelaShell.AgentCore` 是宿主无关的契约、计划模型和状态机，不引用 Avalonia、插件 SDK、Microsoft.Extensions.AI 或具体供应商 SDK。模型调用、工具注册、审批、SSH/MCP、存储和 UI 由插件适配。

## 一轮 Agent 请求

1. `AgentRuntimeFactory` 装配本轮 Execution、Policy、EventLog、Snapshot、Planner、Executor 和 Verifier。
2. 请求进入 Intake/Planning；兼容入口会创建一个隐式单步计划，保持现有聊天行为。
3. 工具由 `AgentExecution` 执行并记账；Policy 在执行前处理审批、拒绝和安全重试。
4. 运行事件投影为 Snapshot，供 UI、Bridge、历史和 Telemetry 使用。
5. 完成后进入 Verifying；失败、取消、未知副作用或验证不通过会暂停或终止，并保留可恢复信息。

显式 P–E–V 入口支持多步骤和有界重试。Planner 只生成计划，Executor 只执行步骤，Verifier 独立判断结果；当前默认桌面/Bridge 流程仍主要走兼容的隐式单步入口。

## 关键安全约定

- Plan 模式不应暴露写工具；写入、删除、移动和其他副作用必须经 Policy 和审批。
- `AgentExecution` 是工具执行安全权威，不能从 UI、Bridge 或子工具箱绕过。
- CallId 重复调用必须幂等处理；同一 CallId 参数冲突必须拒绝。
- 工具取消后的外部结果可能未知，未知副作用不能自动重放。
- 检查点用于恢复上下文，不代表获得了新的执行授权。
- Telemetry、事件和历史元数据只保存脱敏摘要，不保存完整 Prompt、参数、输出或凭据。

## 代码地图

| 区域 | 职责 |
| --- | --- |
| `src/VelaShell.AgentCore/` | Core 契约、计划、编排状态和 Telemetry 模型 |
| `Chat/Runtime/` | Runtime Factory 与显式编排会话 |
| `Chat/AgentExecution.cs` | 工具调用账本、取消、暂停、检查点进度 |
| `Agent/AgentToolbox.cs` | 工具注册、模式筛选和 Policy 接入 |
| `Agent/Policy/` | Policy 请求、决策、审批与安全重试 |
| `Chat/PromptContextCompiler.cs` | 上下文预算、历史裁剪和消息规范化 |
| `Chat/AgentRunEvents.cs` / `AgentRunSnapshot.cs` | 运行事实事件和脱敏投影 |
| `Chat/RunCheckpointStore.cs` / `RunCheckpointWriter.cs` | 检查点格式、写入和恢复 |
| `Ui/` | 桌面聊天面板与运行状态展示 |
| `Bridge/` | 外部消息路由、Agent 运行和流式回报 |

## 开发与验证

源码级架构和修改规则见 [`Architecture.md`](Architecture.md)；日常开发、工具扩展和测试命令见 [`Development.md`](Development.md)。

从仓库根目录运行：

```powershell
dotnet build .\VelaShell.slnx --no-restore

dotnet build .\VelaShell.slnx --no-restore --no-incremental -warnaserror:CS1591

dotnet test .\tests\VelaShell.Plugin.Ai.Tests\VelaShell.Plugin.Ai.Tests.csproj --no-restore
```

完整测试可能包含依赖本机 PowerShell 或图形环境的用例；环境限制导致的跳过或失败应单独记录。

## 文档索引

- [`Architecture.md`](Architecture.md)：当前架构、生命周期、Core 边界、事件、Policy 和恢复语义。
- [`Development.md`](Development.md)：修改入口、工具开发规则、测试与提交前检查。
- [`src/VelaShell.AgentCore/README.md`](../../src/VelaShell.AgentCore/README.md)：AgentCore 的独立说明。
- [`manual.md`](../../manual.md)：VelaShell 用户手册。
- [velashell-docs](https://github.com/VelaShell/velashell-docs)：插件系统、权限、安全和开发规范。
