本次更新聚焦 AI 插件的本地 Agent 与子 Agent 协作、MCP 运行管理和文件传输免确认，同时升级到 .NET 11 正式版，为在本机或远端服务器上让 AI 分工处理文件的用户带来更完整、更可控的 Agent 工作流。

## ✨ 重点更新

- **没有 SSH 也能用本地 Agent**：Agent 模式可以直接读取和修改本机选定工作区里的文件，不再依赖远端会话。
  - 在聊天输入区的工作区下拉框中选择已保存的目录，或用「选择本地工作区…」添加目录；选中的工作区决定本轮本地工具、`#local://工作区ID/相对路径` 引用、附件补全和本地 MCP 工作目录的范围。
  - 读取类工具直接可用；写入、精确替换、移动和删除仍需审批，只读工作区拒绝写入。越出工作区的绝对路径、`..` 路径和指向外部的符号链接 / 目录联接一律拒绝；OneDrive 同步目录（文档、桌面）可正常使用。
  - 变更：`c5e1677a`、`6f381eda` · [PR #731](https://github.com/joesdu/VelaShell/pull/731)；`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)

- **子 Agent 可以分工执行，结果自动交回**：主 Agent 用 `delegate_subagent` 委派任务，子任务结束后结果自动送回，主 Agent 不必轮询，父轮在所有子任务交回结果前不会结束。
  - 默认只读，继承父轮已授权的本地工作区、SSH 会话和经筛选的只读 MCP 工具；不提供命令执行、新建会话和递归委派。
  - 每轮最多 8 个、并行最多 4 个；单个默认 120 秒，委派时可用 `timeoutSeconds`（30–600）调整，等待审批和排队等文件修改锁的时间不计入期限。
  - 委派时可用 `modelId` 指定另一个已配置的聊天模型，省略时沿用父模型；跨供应商会把子任务提示和读取结果发给该供应商。未知模型或图片模型会明确报错，不静默回退。
  - 变更：`15c9eba5` · [PR #732](https://github.com/joesdu/VelaShell/pull/732)；`81946fbe`、`c4514c79` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)；`fd84c8a6` · [PR #741](https://github.com/joesdu/VelaShell/pull/741)

- **子 Agent 可按需写入文件**：委派时显式开启 `allowWrites=true`，子 Agent 才能在父轮已授权的范围内修改文件；默认仍为只读。
  - 开放本地写入、补丁、建目录、移动、删除文件 / 空目录，以及现有远端写入类工具；不扩大目录或 SSH 范围，不重新启用已禁用工具，MCP 仍只读，不开放进程、终端和上传下载。
  - 修改沿用父轮的审批挡位与审批卡片，审批卡标明来源子 Agent；拒绝会停止父轮后续工具执行。父子之间的内置文件修改串行执行，等待中可取消。
  - 变更：`bfbdf266` · [PR #736](https://github.com/joesdu/VelaShell/pull/736)

- **MCP 服务器运行状态一目了然，可手动启停**：MCP 管理页显示每台服务器的已停止、启动中、就绪、异常、失败、停止中状态，以及实际发现的工具数量和最近错误。
  - 保存配置后可启动、停止、重启或刷新工具目录；默认需要工具时自动连接，也可改为手动启动。手动停止会取消启动和进行中的工具调用，并阻止后续对话自动重连，直到再次显式启动。
  - 服务器可设置工具白名单、并发上限、启动 / 调用超时；stdio 服务器可绑定一个已授权的本地工作区。
  - 变更：`69fbd703`、`4a57b638` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)

## 🤖 AI 助手

### 子 Agent 的呈现

- **运行卡片实时显示子任务在做什么**：卡片显示当前正在调用的工具及目标、「等待你审批」或等待文件修改锁、已用 Token，并提供单任务「取消」按钮，可以只停一个子任务而不停整轮；报告正文在生成过程中实时出现。
  - 超时、取消或失败时保留已写出的部分并标明提前停下；没有最终报告或报告被截断时会在错误中说明。交回父模型的正文上限为 24000 字符。
  - 卡片随会话历史保存，重开历史会话时恢复；`subagent_wait` / `subagent_status` 不再额外生成工具卡。
  - 变更：`81946fbe`、`bea1965c`、`b6eb0aab` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)；`fd84c8a6` · [PR #741](https://github.com/joesdu/VelaShell/pull/741)

- **IM 机器人与对外 MCP 也能委派子 Agent**：IM 机器人在 Agent 模式下可委派子 Agent，范围、挡位与审批沿用该聊天的授权，进度消息末尾显示「子 Agent 运行: n/m 已结束」。对外 MCP 在 Agent 模式且已配置聊天模型时，向外部 agent 提供 `delegate_subagent`。
  - 对外 MCP 的子任务结果不会主动推送，调用方需自行 `subagent_wait`；每条连接最多 32 个子任务。
  - 变更：`fd84c8a6` · [PR #741](https://github.com/joesdu/VelaShell/pull/741)

### 用量显示与输入区

- **实时显示 Token 收发量**：右下角上下文用量左侧显示 `xxx tok ↑  xxx tok ↓`（当前会话累计输入 / 输出），首次请求开始即显示，生成期间以 `~` 标记在途估算，结束后按供应商报告的 usage 校准；子任务用量并入会话计数。
  - 模型窗口已知时，上下文占用率和进度条也从请求开始动态估算；窗口未知时不显示百分比。该数值不写入费用账本。
  - 变更：`bea1965c`、`a483c366`、`595b92ea` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)；`fd84c8a6` · [PR #741](https://github.com/joesdu/VelaShell/pull/741)

- **输入区选择器更紧凑**：模式、审批、工作区三个选择器去掉下拉箭头和工作区旁的加号，宽度随选中内容自适应；添加目录入口保留在工作区下拉列表的「选择本地工作区…」项中。
  - 变更：`c5e1677a` · [PR #731](https://github.com/joesdu/VelaShell/pull/731)；`15c9eba5` · [PR #732](https://github.com/joesdu/VelaShell/pull/732)；`1e7f47fb` · [PR #734](https://github.com/joesdu/VelaShell/pull/734)

### MCP 连接

- **跨轮次连接保留与环境变量继承策略**：默认跨轮次保留连接；关闭后，在最后一个使用该连接的轮次（桌面或 IM）结束时断开，不影响仍在使用它的其他聊天。
  - Stdio 默认只继承必要的系统环境，再叠加显式填写的变量；需要代理、`JAVA_HOME`、`DOTNET_ROOT` 等变量时请显式填写，或主动选择「全部宿主变量」（可能把宿主环境中的秘密传给服务器）。
  - 变更：`4a57b638` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)

- **对外 MCP 上传 / 下载可分别免确认**：对外 MCP 设置页新增「自动批准 MCP 上传」「自动批准 MCP 下载」两个开关，默认均关闭、互不影响；开启后，Agent 挡位下该 MCP 服务端发起的对应方向传输同时跳过工具审批和宿主桌面确认。
  - 默认仍不覆盖同名文件；服务器范围、禁用工具、路径校验和安全提交照常执行。聊天、IM、远端链接下载不受影响；通用「绕过审批」仍不能跳过宿主传输确认；插件撤权会取消待批准的任务。
  - 变更：`061d8871` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)

## 🖥️ 终端与连接

- **断线遮罩可以只收起、不关标签**：会话断开后，覆盖层新增「关闭提示」，只收起遮罩并保留标签页和终端历史，「重新连接」与关闭标签仍可用；再次连接或再次断开时遮罩重新出现。
  - `Esc` 关闭断开标签只在遮罩显示时生效；收起遮罩后仍可通过标签页关闭操作关闭。
  - 变更：`7a597da1` · [PR #734](https://github.com/joesdu/VelaShell/pull/734)（关闭 [#733](https://github.com/joesdu/VelaShell/issues/733)）；`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)

- **登录框允许空密码**：密码可以留空直接保存或进入下一步，是否接受由服务器决定。
  - 空密码不会回写到共享凭据，勾选「更新共享凭据」时也不会把共享凭据清空。
  - 变更：`16571d6d` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)；`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)

- **启动动画换上新图片**：启动动画的吉祥物图片更新，外观设置页中的预览效果同步调整。
  - 变更：`2b647c92` · [PR #730](https://github.com/joesdu/VelaShell/pull/730)

## 🔐 安全与隐私

- **本地文件写入在审批前后都校验路径**：审批通过后、真正执行前重新校验路径，防止审批期间目录被替换为联接；修补期间文件被用户改动时放弃写入，不覆盖你的修改。拒绝删除或重命名工作区根目录。
  - 变更：`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)

- **IM 桥接只获得选中的工作区**：此前 IM 桥接会拿到全部已配置的本地工作区（即使面板选了「无」），现在与面板选择一致。
  - 变更：`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)

- **对外 MCP 的子 Agent 会使用你的模型额度**：外部 agent 调用 `delegate_subagent` 时使用 VelaShell 当前选中的模型（或指定的 `modelId`），**消耗你的模型额度，并把子任务读取的内容发送给该模型的供应商**。此能力默认开启，可在对外 MCP 的「配置工具」中停用。
  - 变更：`fd84c8a6` · [PR #741](https://github.com/joesdu/VelaShell/pull/741)

- **已知限制**：MCP 上传 / 下载自动审批的开关目前保存在插件自己的存储中；开启下载自动审批后不限制本地目标路径范围。仅在信任对应 MCP 客户端时开启。MCP 工具的 `readOnlyHint` 是服务端声明，筛选与本地审批不构成操作系统沙箱。

## 🐛 修复与稳定性

- **插件更新前先关闭其活动连接**：更新插件时如果有该插件的文件协议或工作台连接，会先提示；确认后等待工作台文档、插件连接和 SSH 隧道真正关闭再替换文件，避免 `velashell.rdp` 等文件被占用导致 `Access denied`。
  - 断开连接推迟到发布者变更确认、验签和清单校验全部通过之后，被拒或失败时不再白白断开；单个连接关闭失败不再跳过其余连接。
  - 变更：`1bc6138b` · [PR #728](https://github.com/joesdu/VelaShell/pull/728)；`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)

- **本地文件写入不再损坏编码**：写入新文件不再添加 UTF-8 BOM（此前会破坏 shebang 和 JSON），修补时保留原文件的 BOM；非 UTF-8 文件拒绝修补，不再把非 UTF-8 字节替换成乱码。
  - 变更：`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)

- **并发对话的子 Agent 与 MCP 连接互不干扰**：并发对话不会再把子任务派进对方的编排器；两个对话绑定不同工作区时不再互相关闭对方正在使用的 MCP 连接；启动途中手动停止一台服务器只影响它自己，不再让整轮 MCP 工具获取失败。
  - 变更：`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)

- **模型故障转移更准确**：多 Key 场景下 Key 故障尽快交给故障转移；已有可见正文或思考后断流时保留已有内容并报告失败，不切 Key / 模型重放已输出内容或工具副作用；只有供应商侧故障才会让模型进入 60 秒冷却，用户拒绝工具等情况不再误判。
  - 变更：`15c9eba5` · [PR #732](https://github.com/joesdu/VelaShell/pull/732)；`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)

- **Stdio MCP 支持带空格的程序路径**：可执行命令可以填写 `C:\Program Files\dotnet\dotnet.exe` 这类含空格的完整路径；参数中的路径含空格时用双引号包裹。
  - 变更：`29f4156d` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)

- **其他界面修复**：工作区下拉框取消「选择文件夹」后不再卡住无法再次打开；工作区选择器增加宽度上限，不再挤掉用量条；以缩进代码块开头的回复不再丢失首行缩进；流式输出时 Markdown 保持连续；`subagent_wait` / `subagent_status` 在状态仍变化时不再触发「无进展」暂停。
  - 变更：`b6eb0aab` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)；`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)；`fd84c8a6` · [PR #741](https://github.com/joesdu/VelaShell/pull/741)

## ⚠️ 升级说明 / 不兼容变更

- 宿主与 AI 插件升级到 **.NET 11 正式版**（依赖从 `11.0.0-rc.3` 换为 `11.0.0`），AI 插件同步升级 `Microsoft.Extensions.AI` 11.0.0 与 `Anthropic` 13.8.0。变更：`521dea07` · [PR #734](https://github.com/joesdu/VelaShell/pull/734)
- 宿主使用 **插件 SDK 2.1.9**，AI 插件的最低 SDK 版本由 2.1.8 升至 2.1.9。第三方插件无需改动。变更：`061d8871` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)
- 对外 MCP 新增的 `delegate_subagent` **默认开启**；不希望外部 agent 消耗你的模型额度时，请在对外 MCP 的「配置工具」中停用。
- Stdio MCP 服务器旧配置未指定环境变量策略时，默认只继承必要的系统环境；依赖宿主其他环境变量的服务器需要显式填写变量，或改选「全部宿主变量」。

## 📚 文档与生态

- 中英文用户手册同步说明本地 Agent、子 Agent、MCP 运行管理与连接策略、上传下载免确认、Token 用量显示、断线遮罩和空密码。
  - 变更：`15c9eba5` · [PR #732](https://github.com/joesdu/VelaShell/pull/732)；`061d8871` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)；`58978b2d` · [PR #740](https://github.com/joesdu/VelaShell/pull/740)；`fd84c8a6` · [PR #741](https://github.com/joesdu/VelaShell/pull/741)
- velashell-docs 同步插件自动化与 AI、安全威胁模型等文档：[velashell-docs PR #203](https://github.com/VelaShellLabs/velashell-docs/pull/203)、[velashell-docs PR #210](https://github.com/VelaShellLabs/velashell-docs/pull/210)。
- 插件 SDK 2.1.9 的契约变更：[velashell-plugin-sdk PR #55](https://github.com/VelaShellLabs/velashell-plugin-sdk/pull/55)。
- README 新增捐赠信息。变更：`ed390563` · [PR #743](https://github.com/joesdu/VelaShell/pull/743)；`134c0b7e`、`6e4d134c` · [PR #738](https://github.com/joesdu/VelaShell/pull/738)；`a7f44a21` · [PR #739](https://github.com/joesdu/VelaShell/pull/739)

## ✅ 验证

- [三平台 CI #43272437878](https://github.com/joesdu/VelaShell/actions/runs/43272437878)（对应发版提交 `fc808cfa`）：Windows、Linux、macOS 的构建与测试全部通过；[CodeQL #43272433962](https://github.com/joesdu/VelaShell/actions/runs/43272433962) 通过。
- CI 按既有规则排除 `DockerIntegration`、`CrossPlatform`、`Interop` 类别，部分依赖平台或外部环境的用例跳过；跳过不计为通过。
- 各 PR 合并前的本机验证（Windows / .NET 11，Debug）：#740 解决方案构建 0 警告、0 错误；#741 AI 插件完整测试 2173/2173 通过；#736 写入型子 Agent 定向测试 15/15 通过；#740 宿主 Infrastructure 插件相关测试 515 通过、24 跳过。
- 测试稳定性修复：SFTP 中转测试等待后台句柄关闭、远程编辑上传收尾后再清理副本、Linux CI 中 Xvfb 不再在最后一个客户端断开后重置，修复间歇性 `XOpenDisplay failed`。变更：`da1ef75f` · [PR #735](https://github.com/joesdu/VelaShell/pull/735)；`51e36053` · [PR #741](https://github.com/joesdu/VelaShell/pull/741)；`403843c8`、`70c21ed7` · [PR #742](https://github.com/joesdu/VelaShell/pull/742)
- 未完成的人工验收：真实模型供应商、真实 IM 渠道、真实第三方 MCP 服务 / 外部 MCP 客户端，以及桌面多 DPI 下的界面检查，均未在本版本中手工执行。

## 🔗 相关变更

- [PR #728：插件更新前关闭活动连接](https://github.com/joesdu/VelaShell/pull/728) — `1bc6138b`
- [PR #729：新增吉祥物图标概念稿（未替换应用图标）](https://github.com/joesdu/VelaShell/pull/729) — `02849554`
- [PR #730：调整启动动画图片与设置页预览](https://github.com/joesdu/VelaShell/pull/730) — `2b647c92`
- [PR #731：本地 Agent 与子 Agent 基础能力](https://github.com/joesdu/VelaShell/pull/731) — `c5e1677a`、`6f381eda`
- [PR #732：完善只读子任务、故障恢复与输入区选择器](https://github.com/joesdu/VelaShell/pull/732) — `15c9eba5`
- [PR #734：断线遮罩可收起而不关闭标签；升级 .NET 11 正式版](https://github.com/joesdu/VelaShell/pull/734) — `7a597da1`、`1e7f47fb`、`521dea07`
- [PR #735：完善 MCP 协作并支持文件传输自动审批](https://github.com/joesdu/VelaShell/pull/735) — `81946fbe`、`69fbd703`、`4a57b638`、`c4514c79`、`29f4156d`、`b6eb0aab`、`bea1965c`、`a483c366`、`595b92ea`、`16571d6d`、`061d8871`、`da1ef75f`
- [PR #736：写入型子 Agent（显式开启）](https://github.com/joesdu/VelaShell/pull/736) — `bfbdf266`
- [PR #737：升级 MSTest](https://github.com/joesdu/VelaShell/pull/737) — `ff1623e6`
- [PR #738](https://github.com/joesdu/VelaShell/pull/738) / [PR #739](https://github.com/joesdu/VelaShell/pull/739)：README 更新 — `134c0b7e`、`6e4d134c`、`a7f44a21`
- [PR #740：修复 #728–#737 审核发现的缺陷](https://github.com/joesdu/VelaShell/pull/740) — `58978b2d`
- [PR #741：子 Agent 执行、结果回传与呈现，接入 IM 桥接与对外 MCP](https://github.com/joesdu/VelaShell/pull/741) — `fd84c8a6`、`51e36053`
- [PR #742：稳定 SFTP 清理与 Linux 插件测试](https://github.com/joesdu/VelaShell/pull/742) — `403843c8`、`70c21ed7`
- [PR #743：新增捐赠信息](https://github.com/joesdu/VelaShell/pull/743) — `ed390563`
- [PR #744：按 IDE 建议格式化代码](https://github.com/joesdu/VelaShell/pull/744) — `81e6d5a5`
- 完整差异：[1.8.1...1.8.2](https://github.com/joesdu/VelaShell/compare/1.8.1...1.8.2)
