本次更新聚焦 AI 助手的技能（Skills）、扩展包与记忆，跨供应商的团队分工，以及本机命令与上传映射，为用 AI 助手处理运维与开发任务、频繁在本地与远端之间传文件的用户带来更可定制、更会记事、更顺手的工作流。

## ✨ 重点更新

- **技能（Skills）与斜杠调用**：把「上线前怎么检查 nginx 配置」「按团队规范写变更说明」这类做事说明写成技能，AI 会在需要时自己取用，也可以用 `/技能名` 点名调用。
  - 兼容 Anthropic Agent Skills 的 `SKILL.md` 格式；来源为 `~/.velashell/skills/<名>/`，以及当前选中且允许读取的工作区 `.velashell/skills/<名>/`，同名时工作区优先，每轮重新扫描。
  - 渐进披露：系统提示词只放技能名与一句说明，模型用 `load_skill` 取正文、用 `read_skill_file` 读附带文件；这两件工具只读、不执行脚本，拒绝越出技能目录的路径、符号链接、二进制文件与超过 256 KB 的文件。
  - `/技能名 请求` 在桌面与 IM 都可用，对话模式也行；界面与历史保留原话，内置斜杠命令优先于同名技能。「配置工具」的「技能」页可逐个启停、查看来源与解析失败原因、打开目录、重新扫描。
  - 变更：`bcfe7419`、`6f5d8a65`、`cfc0a356` · [PR #782](https://github.com/joesdu/VelaShell/pull/782)

- **扩展包**：把一组技能、斜杠命令和 MCP 服务器打包成一个文件夹或 `.zip`，整套安装与分享。
  - 清单为根目录的 `velashell-extension.json`，逐项校验；安装到插件数据目录时检查 zip-slip、项数与总大小，不跟随符号链接，先复制到临时目录再替换，失败不会留下半装好的扩展包。
  - 安装前原样列出每台 MCP 服务器的命令、参数、工作目录、环境变量、地址与请求头供确认；之后指纹对不上（升级未确认，或安装目录里的文件被改）时，该包的 MCP 暂停直到重新确认，技能与命令照常可用。
  - 扩展包的技能（同名时优先级最低）、命令与已确认的 MCP 服务器在桌面与 IM 都生效，MCP 复用现有的审批、超时与生命周期，停用后立即断开。「配置工具」的「扩展包」页可安装、启停、卸载、重新确认。
  - 变更：`4ee94d66` · [PR #782](https://github.com/joesdu/VelaShell/pull/782)

- **记忆**：AI 跨对话记住你的偏好、环境里的稳定事实和被你纠正过的做法，你可以随时查看与管理。
  - Agent 模式下模型自己调用 `save_memory` / `update_memory` / `delete_memory`，回复里能看到对应工具卡；三种模式都会把记忆交给模型参考（最近改过的在前，8000 字符上限），并说明它是背景而非指令，与你当下所说冲突时以你为准。
  - 「配置工具」新增「记忆」页：总开关（默认开）、搜索、新增、就地编辑、删除与全部清除（都要确认），每条写明是谁存的、什么时候改的；关掉总开关后已存的记忆保留但不再交给模型。
  - 限制：全局一份，最多 200 条；只存在本机插件数据目录的 `memory.json`，不随设置同步；只在桌面面板生效，IM 机器人与对外 MCP 既不读也不写；内容像私钥、令牌或 `password=` 一类的会被拒绝写入（粗筛）。
  - 变更：`fa6351d5` · [PR #784](https://github.com/joesdu/VelaShell/pull/784)

- **团队与委派可以用你添加的所有供应商的模型**：主模型给团队成员与子 Agent 分配模型时，不再局限于当前供应商，可以让不同厂商的模型各展所长。
  - 候选清单覆盖所有供应商实例的聊天模型（不含图片模型），主模型所在的那家排在最前，这个顺序同时是替补顺序；每个候选写明所属供应商与真实型号 ID。桌面、IM 桥接与对外 MCP 三个入口一起放开，别家模型按它自己的配置取凭据。
  - 替补放宽：除 `model_not_found` 外，401 / 403 / 429 与 OAuth 续期失败也会在子任务尚未输出、未调工具时换模型重跑，某一家的 Key 过期不再让分到它的成员整个失败。
  - 隐私与计费：子任务读取的内容会发给所选模型的供应商，并由那一家单独计费；不希望某家参与时，可以在提示里说明，或删除它的聊天模型。
  - 变更：`23f41b0e` · [PR #780](https://github.com/joesdu/VelaShell/pull/780)

- **本机命令（`run_local_command`）**：桌面面板 Agent 模式下，未绑定远端会话且选中的本地工作区可读可写时，Agent 可以在该工作区里运行本机命令（如 `dotnet test`、`docker`）。
  - Windows 优先 pwsh、其次 Windows PowerShell；Linux / macOS 用 bash 或 zsh。每条命令按当前审批挡位确认，审批卡显示解释器、工作目录和完整命令，不可见字符或改变文字方向的字符显示为 `⟦U+XXXX⟧` 并给出警告。
  - 与远端 `run_command` 共用任务机制：几秒内结束直接返回，仍在运行就转入后台，可查看、等待或取消，结束时自动通知；默认时限 15 分钟（最长 24 小时），到期或按停止时终止整个进程树并保留已收到的输出。
  - 只有你在面板里发起的轮次（直接输入、排队、编辑后重发、继续）才提供这个工具，「解释终端输出」等注入入口不提供；只读工作区不注册。输出按 UTF-8 解码，不理会控制台编码的老程序自动改用系统代码页（如 GBK）。Windows / Linux 上 VelaShell 退出时一并结束仍在运行的命令，macOS 没有对应机制。
  - 变更：`8d14da60`、`d92bcdbf` · [PR #777](https://github.com/joesdu/VelaShell/pull/777)、[PR #778](https://github.com/joesdu/VelaShell/pull/778)

## 🤖 AI 助手

### 配置工具

- **「配置工具」按 VS Code Copilot 的配置界面重做**：内置工具、技能、扩展包和各台 MCP 不再挤在一页。
  - 左栏「扩展包」「MCP 服务器」「技能」「工具」「记忆」分节导航并带计数，默认打开「工具」；右栏是标题与页内操作、说明、只筛本节的搜索框（匹配到的来源自动展开）与列表。
  - 「工具」页的内置工具按类别分组，类别开关一次启停整类；MCP 每台服务器一行，行尾开关启停服务器本身（停用立即断开），展开逐个工具，「更新工具库」在行内。「MCP 服务器」页列出已配置的服务器，以及已启用扩展包带来的服务器（只读）。
  - 变更：`40b19659`、`f1e9781a` · [PR #783](https://github.com/joesdu/VelaShell/pull/783)、[PR #785](https://github.com/joesdu/VelaShell/pull/785)

### 团队模式

- **团队规模按任务自定，团队控制台改为环形拓扑**：成员上限 6 → 16、任务上限 12 → 32（安全上限），团队指引改为按职责配人，小任务通常 2–4 名；同时在跑的仍不超过 4 个，每份计划仍需你确认。
  - 拓扑中主控居中，成员沿圆角矩形均匀排开，人多时整圈连同画布等比放大；右栏改为「分工 / 通信」标签页，拓扑下方始终显示最新一条通信。
  - 成员身份色扩到十二种，随明暗主题变化，十二名以内不撞色。
  - 变更：`b4cae4ee` · [PR #783](https://github.com/joesdu/VelaShell/pull/783)

### 对话与输入框

- **「你应该知道」提示条**：Agent 与计划模式下，主模型可以把回答里需要你留意的事（风险、要你亲自去做的事、影响后续决定的发现）钉到输入框上方，回复滚走后仍然可见。
  - 标注由主模型在正常回答中顺手完成（`note_for_user`），不额外发请求、不额外计费；同标题原位更新，每份对话最多 20 条；可折叠、可逐条或一次全部关掉，载入会话时从历史重建。
  - 对话模式、IM 桥接和对外 MCP 不提供；可在「配置工具」中停用。
  - 变更：`1935b3f1` · [PR #781](https://github.com/joesdu/VelaShell/pull/781)

- **排队消息放回输入框**：点击输入框上方的排队芯片，消息回到输入框继续编辑（接在已有草稿后），随它排队的图片 / 文件附件一并放回，不再直接丢弃；本轮被停止、排队内容退回输入框时也不再丢附件。
  - 变更：`d92bcdbf` · [PR #778](https://github.com/joesdu/VelaShell/pull/778)

- **命令卡显示耗时并跟踪后台命令**：`run_command` / `run_local_command` 卡头运行中显示「运行中 · 12s」，结束后显示「退出码 N · 耗时」；转入后台的命令在卡片下方实时显示最新 3 行输出并带「停止」按钮，结束后换成最终结局。界面跟踪不会吞掉留给模型的完成通知。
  - 变更：`d92bcdbf` · [PR #778](https://github.com/joesdu/VelaShell/pull/778)

### 提示词与会话目标

- **自定义提示词改为追加在内置说明之后**：全局提示词、模型系统提示词与 IM 机器人附加提示词不再整段替换内置说明，模式规则、「MCP 跑在本机」等提醒会一直保留；模型的提示词优先，填了就不再叠加全局的。IM 现在也会使用模型级提示词（以前被忽略）。
  - 变更：`d6cf6a51` · [PR #782](https://github.com/joesdu/VelaShell/pull/782)

- **Agent 自己打开的连接不再改变对话目标**：桌面面板里 Agent 用 `open_session` 连上的机器不会悄悄成为后续工具的默认目标，后续工具仍作用于你在面板里选中的会话，要操作新连接需显式带上它的 `session_id`。
  - 变更：`8d14da60` · [PR #777](https://github.com/joesdu/VelaShell/pull/777)

## 📁 文件传输

- **上传选择窗口：勾选上传与目录映射**：点击远端栏的「上传」，在右侧按复选框勾选文件与文件夹（列表与图标视图都支持，表头三态全选），上传与底部计数只取勾选项；可直接选择目录。
  - 映射栏空白处右键「添加映射」，在双栏绑定窗口中左选远端目录、右选本地目录；同一远端目录可绑定多个本地目录，重复组合复用原记录，记录右键可删除。
  - 映射按原顺序显示，不排序、不置顶；打开窗口时按本连接、本远端目录最近一次提交的上传恢复映射与勾选。左键点击映射会联动主远端列表跳到对应目录。
  - 映射与勾选只保存在本机，不随连接导出、目录收藏或同步。
  - 变更：`b4cb37c1`、`de733cfa`、`4fed0d2e` · [PR #776](https://github.com/joesdu/VelaShell/pull/776)（感谢 @foxiexianmo）；合并后修正 `9dcf2a2d` · [PR #778](https://github.com/joesdu/VelaShell/pull/778)

## 🖥️ 连接管理

- **资源管理器右键「复制 IP/域名」**：在「复制连接」下新增一项，把连接里填的主机地址（不含端口与用户名）复制到剪贴板；没填主机的插件连接（如 S3）不显示这一项。
  - 变更：`79471b49` · [PR #779](https://github.com/joesdu/VelaShell/pull/779)

- **主机栏自动拆出端口**：在连接表单的主机栏写 `192.168.1.1:22`、`example.com:2222` 或 `[2001:db8::1]:2222` 时，离开主机栏（或直接保存）会把端口填进端口栏，主机栏只留 IP 或域名。
  - 只认「一个冒号 + 1–65535」；裸 IPv6、串口设备名、SQLite 文件路径、插件 URL 与越界端口原样不动，端口栏收起的协议不拆；不会在打字过程中拆。
  - 变更：`79471b49` · [PR #779](https://github.com/joesdu/VelaShell/pull/779)

## 🪟 窗口

- **对话框与工具窗口的阴影画在裁剪之外**：Windows 上对话框与独立工具窗口统一显示圆角描边和阴影，最大化和全屏时收起；主窗口保持不透明、无阴影边距，贴边分屏不留缝。macOS、Wayland 与 X11 的外框行为不变。
  - 变更：`b4cb37c1` · [PR #776](https://github.com/joesdu/VelaShell/pull/776)；`9dcf2a2d` · [PR #778](https://github.com/joesdu/VelaShell/pull/778)

## 🐛 修复与稳定性

- **在上传选择器里浏览其他目录会改写映射**：进入子目录或其他目录再勾选保存，会静默把当前映射的本地目录改掉；现在映射的远端 / 本地目录只由添加映射决定，映射只记录自己目录下的勾选。
  - 变更：`9dcf2a2d` · [PR #778](https://github.com/joesdu/VelaShell/pull/778)

- **读取上传记忆失败后清空全部映射**：读取失败时缓存了空状态，下一次写入会把磁盘上的映射全部清掉；现在读成功前不写盘，下次重读。
  - 变更：`9dcf2a2d` · [PR #778](https://github.com/joesdu/VelaShell/pull/778)

- **删除当前映射后无法上传、最近上传跨目录接管目标**：删除映射后仍可勾选并上传到窗口显示的远端目录；自动恢复只在入口远端目录的绑定之间进行，不再把文件传到与主面板不同的目录。
  - 变更：`9dcf2a2d` · [PR #778](https://github.com/joesdu/VelaShell/pull/778)

- **主窗口出现透明外框**：#776 曾把 Windows 主窗口改为透明窗口加 16px 阴影边距，导致可见区域缩小、贴边分屏露缝、四周透明区域挡点击；主窗口已恢复默认外框。
  - 变更：`9dcf2a2d` · [PR #778](https://github.com/joesdu/VelaShell/pull/778)

- **本机命令的一组问题**：长命令阻塞整轮且无进度；超时 / 停止丢掉全部输出或只留开头；`start /b`、`&` 留下的后台进程占住管道导致挂起；超时 / 停止落成「结果未知」暂停整轮；超长命令行每个解释器各审批一次；PowerShell 5.1 下 `2>&1` 误报退出码 1、报错行号偏 1；bash / zsh 下以 `-` 开头的命令被当成选项；macOS 从访达启动时 PATH 不全；老程序输出乱码；VelaShell 崩溃后留下孤儿进程（Windows / Linux）。以上均已修正。
  - 变更：`d92bcdbf` · [PR #778](https://github.com/joesdu/VelaShell/pull/778)

## ⚠️ 升级说明 / 不兼容变更

- 自定义提示词的行为变化：全局提示词、模型系统提示词与 IM 机器人附加提示词改为**追加**在内置说明之后。以前靠自定义提示词整段替换内置说明的用户，请检查提示词里是否有与内置模式规则重复或冲突的内容。变更：`d6cf6a51` · [PR #782](https://github.com/joesdu/VelaShell/pull/782)
- 团队与委派的候选模型扩展到所有供应商后，子任务的内容可能发往另一家供应商并由其单独计费；不希望某家参与时，请在提示里说明或删除它的聊天模型。变更：`23f41b0e` · [PR #780](https://github.com/joesdu/VelaShell/pull/780)
- 内置 AI 插件版本由 1.8.5 升到 1.8.6。变更：`384c4bfa` · [PR #789](https://github.com/joesdu/VelaShell/pull/789)
- 依赖更新：MaxMind.Db 5.2.0 → 5.3.0，AI 插件的 Anthropic SDK 13.9.0 → 13.10.0；AI 插件不再单独覆盖 Markdig、MicroCom.Runtime、Onigwrap、TextMateSharp 的版本，统一使用宿主的共享版本。变更：`2db5c8c0` · [PR #784](https://github.com/joesdu/VelaShell/pull/784)；`1370127e` · [PR #774](https://github.com/joesdu/VelaShell/pull/774)

## 📚 文档与生态

- 中英文用户手册同步：上传选择窗口与上传映射、窗口边界、复制 IP/域名与主机栏拆端口、排队消息与命令卡、本机命令、「你应该知道」、技能、扩展包、记忆、跨供应商的委派与团队、团队控制台新版式、提示词追加规则。变更：`b4cb37c1`、`9dcf2a2d`、`79471b49`、`d92bcdbf`、`23f41b0e`、`1935b3f1`、`d6cf6a51`、`bcfe7419`、`4ee94d66`、`40b19659`、`b4cae4ee`、`fa6351d5` · [PR #776](https://github.com/joesdu/VelaShell/pull/776)、[PR #778](https://github.com/joesdu/VelaShell/pull/778)、[PR #779](https://github.com/joesdu/VelaShell/pull/779)、[PR #780](https://github.com/joesdu/VelaShell/pull/780)、[PR #781](https://github.com/joesdu/VelaShell/pull/781)、[PR #782](https://github.com/joesdu/VelaShell/pull/782)、[PR #783](https://github.com/joesdu/VelaShell/pull/783)、[PR #784](https://github.com/joesdu/VelaShell/pull/784)
- velashell-docs 同步（交互与界面规格、《自动化与 AI》、安全威胁模型，中英同步）：[PR #244](https://github.com/VelaShellLabs/velashell-docs/pull/244)、[PR #245](https://github.com/VelaShellLabs/velashell-docs/pull/245)、[PR #246](https://github.com/VelaShellLabs/velashell-docs/pull/246)、[PR #247](https://github.com/VelaShellLabs/velashell-docs/pull/247)、[PR #248](https://github.com/VelaShellLabs/velashell-docs/pull/248)、[PR #249](https://github.com/VelaShellLabs/velashell-docs/pull/249)、[PR #250](https://github.com/VelaShellLabs/velashell-docs/pull/250)、[PR #251](https://github.com/VelaShellLabs/velashell-docs/pull/251)。
- 1.8.5 发布日志归档到 `changelogs/release-notes-1.8.5.md`。变更：`f8cdb4cb` · [PR #773](https://github.com/joesdu/VelaShell/pull/773)

## ✅ 验证

- [三平台 CI #44462390554](https://github.com/joesdu/VelaShell/actions/runs/44462390554)（对应提交 `189a7db7`，即 #788 合并后的 main）：发版时 Ubuntu 构建与测试已通过，Windows 与 macOS 仍在运行、结果未计入本节；[CodeQL #44462390510](https://github.com/joesdu/VelaShell/actions/runs/44462390510) 通过。发版 PR [#789](https://github.com/joesdu/VelaShell/pull/789)（只改插件版本号与本日志）按维护者决定未等 CI 即合并。
- CI 按既有规则排除 `DockerIntegration`、`CrossPlatform`、`Interop` 类别，部分依赖平台或外部环境的用例跳过；跳过不计为通过。
- 测试提速（不影响产品行为）：AI 插件测试 9 分 22 秒 → 5 分 15 秒；SonnetDB 测试改用模板库并在测试中关闭逐次 fsync，Argon2id 在测试中用低成本参数，三处计时改为 internal 替身，2000 文件中转用例减量，CI 先启动最长的 `VelaShell.Tests`；生产默认值由新增用例钉住不变。另修了恢复码「抄错一位」用例约 1/256 的随机失败与几条 macOS / Windows CI 偶发失败。变更：`80b685fc`、`65a00350`、`b29c9936`、`cb5a75ec`、`10a165ec`、`c55dca8f` · [PR #784](https://github.com/joesdu/VelaShell/pull/784)、[PR #786](https://github.com/joesdu/VelaShell/pull/786)、[PR #787](https://github.com/joesdu/VelaShell/pull/787)、[PR #788](https://github.com/joesdu/VelaShell/pull/788)
- 各 PR 合并前的本机验证（Windows / .NET 11，Debug）：
  - #782：`dotnet build VelaShell.slnx -warnaserror` 通过；`dotnet test VelaShell.slnx` 11,949 通过、0 失败、246 跳过（Docker、真实 SSH、Shell 集成、Sudo 内核等按环境早退）。
  - AI 测试工程全量随版本推进由 2403 项增至 2468 项；#783、#784、#786 各次全量均全部通过。#780 全量时有 1 项旧断言失败，修正后相关 38 项通过，但修正后未再跑全量（其后 #781 全量 2415 项全部通过）。
  - #776：全量报告 11,830 项通过、274 项显式跳过、0 失败；上传、映射与窗口相关 303 项通过。#778 / #779 / #783：`VelaShell.Tests` 全量 0 失败。
  - #788：Core.Tests 1616 通过 / 7 跳过，Infrastructure.Tests 1420 通过 / 33 跳过，VelaShell.Tests 3538 通过 / 12 跳过，0 失败；[CI #44459250311](https://github.com/joesdu/VelaShell/actions/runs/44459250311) 三平台全绿。
- 已知问题：main 上 [CI #44453221304](https://github.com/joesdu/VelaShell/actions/runs/44453221304) 的 Windows 作业曾红过一条 XServer 用例（改尺寸后的重绘时序，疑与负载相关），另行排查；`FtpSessionStatusTests.OpeningRemoteFileAfterDisconnect` 在 Linux CI 上耗时约 67 秒，另行排查。
- 未完成的人工验收：技能、扩展包、记忆、「你应该知道」、跨供应商分工均未接真实模型验证；「配置工具」新版式、团队控制台、命令卡与主窗口外框未在真实窗口中人工验收；未做真实远端上传与 macOS / Linux 实机验收；本机命令未在 zsh、macOS 登录 shell 与真实 GBK 输出程序上实测。

## 🔗 相关变更

- [PR #773：新增 1.8.5 发布日志](https://github.com/joesdu/VelaShell/pull/773) — `f8cdb4cb`
- [PR #774：删除无效的版本升级](https://github.com/joesdu/VelaShell/pull/774) — `1370127e`
- [PR #776：上传选择、映射导航与窗口阴影](https://github.com/joesdu/VelaShell/pull/776) — `b4cb37c1`、`de733cfa`、`4fed0d2e`
- [PR #777：修复 AI 插件审批语义，本机命令与 .NET 11 Process 新用法](https://github.com/joesdu/VelaShell/pull/777) — `8d14da60`
- [PR #778：修正 #776 合并后的问题；排队消息放回输入框、命令卡耗时、本机命令接入任务机制](https://github.com/joesdu/VelaShell/pull/778) — `9dcf2a2d`、`d92bcdbf`、`c1ff9725`
- [PR #779：资源管理器右键「复制 IP/域名」，连接表单主机栏自动拆出端口](https://github.com/joesdu/VelaShell/pull/779) — `79471b49`
- [PR #780：团队模式与委派的候选模型扩展到所有已添加的供应商](https://github.com/joesdu/VelaShell/pull/780) — `23f41b0e`
- [PR #781：输入框上方的「你应该知道」提示条](https://github.com/joesdu/VelaShell/pull/781) — `1935b3f1`
- [PR #782：Agent 宿主重构，新增技能（Skills）与扩展包](https://github.com/joesdu/VelaShell/pull/782) — `2c9ce681`、`22e55f73`、`d6cf6a51`、`89bd2ac6`、`8903d423`、`71ddacc2`、`bcfe7419`、`6f5d8a65`、`cfc0a356`、`4ee94d66`、`d275f111`
- [PR #783：「配置工具」按 Copilot 配置界面重做；团队控制台环形拓扑与规模自定](https://github.com/joesdu/VelaShell/pull/783) — `40b19659`、`b4cae4ee`、`a85383c8`
- [PR #784：记忆 —— 跨对话记住偏好与环境事实，「配置工具」新增记忆页](https://github.com/joesdu/VelaShell/pull/784) — `fa6351d5`、`c55dca8f`、`2db5c8c0`
- [PR #785：优化 MCP 服务展示效果](https://github.com/joesdu/VelaShell/pull/785) — `f1e9781a`
- [PR #786：AI 插件测试从 9 分 22 秒提速到 5 分 15 秒](https://github.com/joesdu/VelaShell/pull/786) — `80b685fc`
- [PR #787：宿主与 Infrastructure 测试提速，CI 先跑最长的工程](https://github.com/joesdu/VelaShell/pull/787) — `65a00350`
- [PR #788：Argon2、计时替身、大批量用例减量，并修几条偶发失败](https://github.com/joesdu/VelaShell/pull/788) — `b29c9936`、`cb5a75ec`、`10a165ec`
- [PR #789：AI 插件版本升到 1.8.6，新增 1.8.6 发布日志](https://github.com/joesdu/VelaShell/pull/789) — `384c4bfa`
- 完整差异：[1.8.5...1.8.6](https://github.com/joesdu/VelaShell/compare/1.8.5...1.8.6)
