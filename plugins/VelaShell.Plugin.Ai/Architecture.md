# VelaShell AI 插件架构

本文件描述当前代码已经实现的架构，不把规划中的能力写成现状。完整的生态级插件设计仍以 `velashell-docs` 为准；这里保留与本插件源码一起阅读所需的运行时说明。

## 1. 目标与边界

AI 插件把多供应商对话、工具调用、审批、上下文管理和可恢复 Agent 运行接到 VelaShell。它同时服务三个入口：

- **桌面聊天面板**：负责交互、附件、模型选择和运行状态展示。
- **Bridge**：负责 IM/外部消息的会话路由与流式回报。
- **未来宿主**：通过 `VelaShell.AgentCore` 复用计划、执行、验证和恢复契约。

插件不是 AgentCore 本身。AgentCore 只包含宿主无关的模型、状态和端口；模型 SDK、Avalonia、SSH/SFTP、MCP、凭据和本地持久化仍属于插件或宿主适配层。

## 2. 依赖方向

```text
桌面 UI / Bridge / 未来入口
              |
        AgentRuntimeFactory
              |
  RuntimeSession + AgentExecution + Policy
       |             |             |
   AgentCore      ToolBox       Snapshot
       |             |             |
   计划/状态      工具账本      事件投影
              |
       Checkpoint / History / Telemetry
```

核心原则是依赖单向流动：

```text
VelaShell.Plugin.Ai  ──────>  VelaShell.AgentCore
```

`VelaShell.AgentCore` 不引用插件项目、Avalonia、Microsoft.Extensions.AI 或供应商 SDK。插件用适配器把现有实现接到 Core 契约，避免未来 APP 复制状态机。

## 3. 分层与源码位置

| 层 | 主要职责 | 当前源码 |
| --- | --- | --- |
| Experience | UI、Bridge、流式展示、用户中断 | `Ui/`、`Bridge/` |
| Session | 会话、轮次、历史、恢复入口 | `Chat/ChatHistoryStore.cs`、`Chat/AgentRunCoordinator.cs` |
| Intent | 请求规范化、模式和系统提示组合 | `Chat/RequestContext.cs`、`Chat/SystemPromptBuilder.cs` |
| Context | 历史裁剪、预算估算、工具消息配对 | `Chat/PromptContextCompiler.cs` |
| Policy | 审批、拒绝、未知副作用、安全重试 | `Agent/Policy/`、`Agent/AgentToolbox.cs` |
| Orchestrator | Intake → Planning → Executing → Verifying | `Chat/Runtime/`、`src/VelaShell.AgentCore/` |
| Model Gateway | 多供应商协议、凭据、故障转移、流式响应 | `Configuration/`、`AiHttp.cs`、聊天客户端适配代码 |
| Tool Runtime | 工具注册、参数、CallId、取消和执行账本 | `Agent/AgentToolbox.cs`、`Chat/AgentExecution.cs` |
| State & Evidence | 事件、快照、检查点、历史元数据和脱敏指标 | `Chat/AgentRunEvents.cs`、`Chat/AgentRunSnapshot.cs`、`Chat/RunCheckpointStore.cs` |

## 4. 一轮 Agent 的生命周期

### 4.1 兼容入口

桌面和 Bridge 当前默认使用兼容路径：

1. `AgentRuntimeFactory.Create` 为本轮创建执行账本、事件日志、Snapshot、Policy 和编排会话。
2. Agent 模式进入一个隐式的 `main` 计划步骤。
3. 既有模型工具循环继续负责逐批模型响应和工具调用。
4. 正常结束时统一进入 Verifying 并记录 Telemetry；取消、失败或未知副作用进入相应暂停/终止状态。

这条路径保留旧行为，同时让 UI、Bridge、历史和检查点读取同一份运行状态。

### 4.2 显式 Planner–Executor–Verifier

`AgentCore` 提供可注入的：

- `IAgentPlanner`：只生成目标、验收条件、顺序步骤、风险和停止条件。
- `IAgentExecutor`：只执行一个已经确定的步骤，不修改计划边界。
- `IAgentVerifier`：读取执行快照和步骤结果，独立判断是否通过。

`AgentRuntimeSession.PlanAndExecuteAsync` 支持多步骤、按 `MaxAttempts` 有界重试、取消传播和验证失败暂停。默认实现用于兼容和测试；实际模型结构化计划生成仍是后续接入点，不应误认为当前所有聊天都已经自动拆成多步计划。

## 5. 事件与快照

`AgentRunEventLog` 是有界、单调序列化的运行事件流，事件只保存短摘要、工具名、CallId、结果状态和耗时，不保存完整 Prompt、参数、工具输出或凭据。

`AgentRunSnapshotProjector` 订阅事件并生成线程安全快照，负责统计：

- 当前状态、步骤和序列号；
- 工具调用、成功、失败、取消和后台等待；
- 暂停次数与耗时；
- 可供 UI、Bridge 和历史元数据使用的脱敏 `AgentRunTelemetry`。

Snapshot 是事件的投影，不是另一份可独立修改的事实源。UI 和 Bridge 不应维护第二套运行状态。

## 6. Policy 与工具安全

所有工具仍由 `AgentExecution` 维护最终账本：CallId 去重、参数冲突、未知副作用、取消、暂停和检查点语义不能被上层绕过。`AgentPolicyAdapter` 在工具执行前处理审批和拒绝，`AgentToolbox` 负责按模式和工具目录暴露能力。

关键安全约束：

- Plan 模式只暴露只读能力；Agent 模式才允许受策略控制的写操作。
- 审批失败不会进入工具执行。
- 取消本地操作与远程未知副作用分开记录。
- 未知副作用不能自动重放；恢复前必须先核对外部状态。
- 子工具箱继承父级 Policy，不得借由嵌套工具扩大权限。

## 7. 检查点、恢复和数据边界

有进展时 `RunCheckpointWriter` 延迟写入检查点；中断、失败或停止时保留最后一份，正常完成时删除。检查点保存：

- 运行和会话 ID、任务、模型标签；
- 截断后的消息、工具调用和结果；
- 脱敏运行事件；
- 最近的计划编排状态、当前步骤、尝试次数和验证结果。

恢复流程只重建上下文，不自动重放工具。没有结果的调用会标为未知，恢复计划会告知宿主需要先做状态核对。

历史元数据只保存脱敏 Telemetry。Prompt、完整参数、工具输出、凭据和认证头不应进入 Telemetry。

## 8. 当前状态与后续演进

已落地：独立 AgentCore、统一 Runtime Factory、Planner–Executor–Verifier 契约、Policy Gate、事件投影、Prompt/Context 编译、检查点和 Telemetry。

仍属于演进方向：

- 将结构化 LLM 输出接入 Planner；
- 让桌面主流程在合适场景使用显式多步骤入口；
- 将计划状态恢复后的继续执行做成完整用户交互；
- 把模型路由、工具注册和宿主能力进一步拆成可替换端口。

新增能力时先判断它属于 Core 契约、插件适配器还是某个入口，不要把 UI 状态、供应商 SDK 类型或宿主对象放进 AgentCore。
