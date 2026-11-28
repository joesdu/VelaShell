# VelaShell.AgentCore

AgentCore 是桌面 AI 插件、Bridge 与未来独立应用可以共同引用的 .NET 11 类库。它定义一次 Agent 运行中「要做什么、执行到了哪一步、用什么证据判定完成」，不负责连接模型或执行命令。

## 为什么需要单独的项目

桌面聊天和 Bridge 都需要表达计划、取消、暂停和恢复，但前者用 Avalonia 显示进度，后者向聊天渠道回报。如果将这些状态定义在窗口或模型客户端里，新宿主就必须引用整个插件，或复制一套可能逐渐分歧的规则。现在依赖方向是 **AI 插件 → AgentCore**：Core 不引用 Avalonia、插件 SDK 或模型供应商 SDK。新宿主可以复用同一套状态转换和端口，自行选择界面、工具和存储。

## 目前代码如何分工

| 位置 | 职责 |
| --- | --- |
| [AgentRuntimeContracts.cs](AgentRuntimeContracts.cs) | 定义消息、工具调用及结果、运行事件、执行快照、检查点、恢复计划，以及执行、持久化和宿主生命周期端口。 |
| [AgentOrchestrationState.cs](AgentOrchestrationState.cs) | 定义计划和步骤；记录当前步骤、尝试次数、已完成步骤和验证结果。验证不通过会暂停，而不是标记为完成。 |
| [AgentPlanContracts.cs](AgentPlanContracts.cs) | 定义 `IAgentPlanner`、`IAgentExecutor`、`IAgentVerifier`。默认规划器只生成一步；默认执行器不执行外部操作，默认验证器只检查各步是否报告成功。 |
| [AgentRunTelemetry.cs](AgentRunTelemetry.cs) | 供历史和界面展示的运行摘要。它不是恢复用的转录；检查点消息可能包含用户正文、工具参数或输出。 |
| [插件运行时](../../plugins/VelaShell.Plugin.Ai/Chat/Runtime) | `AgentRuntimeFactory` 为一轮运行装配组件，`AgentRuntimeSession` 驱动规划、执行和验证。桌面与 Bridge 目前默认走兼容的隐式单步路径；显式多步编排可注入不同实现，不等于默认调用模型生成计划。 |
| [插件适配器](../../plugins/VelaShell.Plugin.Ai/Core/Runtime) | 将 Core 契约映射到插件的 `AgentExecution` 和磁盘检查点。模型调用、工具授权、实际副作用、上下文编译、事件投影与 UI 仍在插件内。 |

## 独立宿主如何使用

引用 `VelaShell.AgentCore.csproj` 即可使用计划模型、状态机和端口。程序集名为 `VelaShell.AgentCore`；运行契约保留 `VelaShell.Plugin.Ai.Core.Runtime` 命名空间，并不表示它依赖插件。需要执行真实任务时，宿主必须提供实际执行器、授权及验证逻辑；需要跨进程恢复时，再接入检查点存储。对结果未知的外部操作，应先核对状态，不能把恢复或重试当作自动重放的许可。**仅引用 Core 不会得到可独立运行的 Agent 应用。**

从 VelaShell 仓库根目录构建：

```powershell
dotnet build .\src\VelaShell.AgentCore\VelaShell.AgentCore.csproj
```
