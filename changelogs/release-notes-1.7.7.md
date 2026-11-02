本次更新聚焦快捷命令的发送后执行、对外 MCP 的 sudo 提权交互与 Linux / macOS 的 RDP 认证兼容性，让常用命令的执行方式更可控，并修复外部 Agent 请求提权时无法到达宿主确认框的问题。

## ✨ 重点更新

- **每条代码片段可单独开启「发送后执行」**：常用命令发送到终端后，可以按需自动按一次 Enter，省去手动提交。
  - 在「设置 → 代码片段」的新建或编辑表单中开启；默认关闭，旧数据与内置片段也保持关闭。从侧栏发送时，先完成变量填写与多行确认，再发送正文，最后按目标终端的模式发送一次 Enter；取消确认则正文与 Enter 都不发送。
  - 每个目标标签只接收一次，不经同步输入再次转发。接受命令补全或幽灵文本仍只填入正文，不会因该开关执行；正文里的 `\n`、`\r` 等反斜杠写法不会转换成换行或按键。
  - 开关随片段本地保存和 v2 Gist 同步；内置片段的覆盖可以通过「恢复默认」还原为关闭。
  - 变更：`654abe1d` · [PR #708](https://github.com/joesdu/VelaShell/pull/708)

- **外部 Agent 的提权请求可以弹出宿主确认框**：对外 MCP 在 Agent 模式下收到 `run_command` 的 `elevated=true` 请求时，交由 VelaShell 显示 root 确认框，需要密码时再显示 sudo 密码框。
  - 即使 sudo 配置为 NOPASSWD，每条命令也必须先在宿主确认；弹窗期间任务状态为 `AwaitingUser`，用户切回 VelaShell 处理后，外部 Agent 可用 `task_wait` 获取结果。
  - 普通执行通道没有终端，直接发送需要密码的 `sudo …` 仍会失败；此时回执增加使用提权方式的提示，不会自动把整条命令改成 root 执行。
  - 变更：`2ca1d7a9` · [PR #705](https://github.com/joesdu/VelaShell/pull/705)

## 🖥️ RDP 与跨平台连接

- **提前启用 Unix 托管 NTLM**：主程序与隔离插件宿主在启动时启用 .NET 托管 NTLM，避免其他插件先使用认证 API 后，RDP 的 NLA 认证仍选中 Linux 原生提供程序并报 `Unsupported`。
  - Linux / macOS 需要同时使用含配套修复的 RDP 插件，并完整退出、重启 VelaShell；服务器仍须允许 NTLM，无需为绕过这个客户端错误而关闭 NLA，也无需另装 `gss-ntlmssp`。
  - Windows 继续使用原有 SSPI 路径；Unix 上该开关也影响同进程其他使用 `NegotiateAuthentication` 的客户端。macOS 真实连接故障仍待日志确认，本次不宣称已完成 macOS RDP 端到端验证。
  - 变更：`ab724220` · [PR #707](https://github.com/joesdu/VelaShell/pull/707)；配套插件变更：[velashell-plugins PR #109](https://github.com/VelaShellLabs/velashell-plugins/pull/109)

## 🔐 安全与隐私

- **MCP 提权由宿主逐条确认**：Agent 模式下，提权请求可越过插件层的「询问 / 只读放行」限制到达宿主，但必须由用户在桌面确认来源插件、真实目标和完整命令；密码只交给宿主，不经过外部 Agent 或模型。
  - 这意味着持有对外 MCP 令牌的本机进程现在可以发起提权确认请求。非提权的写命令、写文件和打开会话仍受原审批档位限制；计划与对话模式不提供命令执行工具。内置聊天与 IM 保留原有审批流程。
  - 变更：`2ca1d7a9` · [PR #705](https://github.com/joesdu/VelaShell/pull/705)

## 🐛 修复与稳定性

- **减少 macOS CI 的偶发失败**：本地文件栏与上传选择框的相关 UI 测试不再被真实临时目录的延迟监视事件打断；「在此处打开终端」的非 ASCII 目录测试按完整字节流识别命令行，兼容写入合并。
  - 这些调整只修正测试环境与断言，不改变产品的目录监视或终端写入行为。
  - 变更：`fdd2e55c`、`26546b56` · [PR #705](https://github.com/joesdu/VelaShell/pull/705)

## ⚠️ 升级说明 / 不兼容变更

- **片段开关默认关闭**：开启前请核对正文与目标；关闭开关也不保证多行正文不会执行，未启用括号粘贴的 shell 仍可能按正文中的换行执行命令。旧客户端可能忽略新字段，并在保存或再次同步时丢掉它，混用版本后请检查开关。
- **RDP 修复需要宿主与插件配套**：发布说明核对时，velashell-plugins PR #109 已合并，但最新正式插件 Release 仍是修复合并前的 `2.0.9`。仅更新宿主不能代替插件更新，请在包含该修复的插件版本发布后更新 RDP 插件，并完整退出重启。
- **对外 MCP 的提权确认行为发生变化**：Agent 模式下，提权请求现在可以触发宿主确认框；每次仍须用户批准，不会静默获得 root 权限。

## 📚 文档与生态

- 中英文用户手册补充片段开关、保存与同步兼容性、RDP 更新与重启要求，以及对外 MCP 的 root 命令操作说明。
  - 变更：`654abe1d` · [PR #708](https://github.com/joesdu/VelaShell/pull/708)；`ab724220` · [PR #707](https://github.com/joesdu/VelaShell/pull/707)；`2ca1d7a9` · [PR #705](https://github.com/joesdu/VelaShell/pull/705)
- velashell-docs 的配套中英文开发文档均已合并：[PR #180（MCP 提权）](https://github.com/VelaShellLabs/velashell-docs/pull/180)、[PR #182（RDP 认证）](https://github.com/VelaShellLabs/velashell-docs/pull/182)、[PR #184（代码片段）](https://github.com/VelaShellLabs/velashell-docs/pull/184)。
- 归档上一版 1.7.6 的完整发布日志，便于历史追溯。
  - 变更：`e35b72a0` · [PR #704](https://github.com/joesdu/VelaShell/pull/704)

## ✅ 验证

- 已核对源码提交 `58fdc0fd` 的 [CI #41400021494](https://github.com/joesdu/VelaShell/actions/runs/41400021494)：Windows、Linux、macOS 三个平台的 `dotnet build VelaShell.slnx -c Debug --nologo -warnaserror` 与 CI 测试任务均通过；Linux 的 `runtime-async=off` 全解决方案编译检查通过。
- CI 测试按仓库配置排除需要专门环境的 `DockerIntegration`、`CrossPlatform` 与 `Interop` 类别；平台或环境不满足而跳过的用例不计为已验证。
- 本次发布准备复用上述已完成的 CI，未在本机重复运行构建和测试；未进行真实 SSH 主机与外部 Agent 的 MCP 提权端到端手工验证，也未进行 macOS RDP 真实连接验证。

## 🔗 相关变更

- [PR #708：为每条片段新增默认关闭的发送后执行开关](https://github.com/joesdu/VelaShell/pull/708) — `654abe1d`
- [PR #707：在宿主启动时启用 Unix 托管 NTLM](https://github.com/joesdu/VelaShell/pull/707) — `ab724220`
- [PR #705：对外 MCP 提权命令走宿主的 root 确认框与 sudo 密码框](https://github.com/joesdu/VelaShell/pull/705) — `2ca1d7a9`、`fdd2e55c`、`26546b56`
- [PR #704：归档 1.7.6 发布日志](https://github.com/joesdu/VelaShell/pull/704) — `e35b72a0`
- 完整差异：[1.7.6...1.7.7](https://github.com/joesdu/VelaShell/compare/1.7.6...1.7.7)
