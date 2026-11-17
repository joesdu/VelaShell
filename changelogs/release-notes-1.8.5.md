本次更新聚焦 AI 团队模式、长任务的上下文管理与断点续跑，以及文件改动的审阅与上下文用量的可视化，为用 AI 助手处理多步骤、长时间运维与开发任务的用户带来更可协作、更可恢复、更看得清的 Agent 体验。

## ✨ 重点更新

- **AI 团队模式与团队控制台**：主模型可以把一项任务拆给最多 6 名成员、最多 12 个任务协同完成，进度和产出在主窗口标签页里的团队控制台中一目了然。
  - 新增 `team_run` / `team_status` / `team_message` / `team_cancel` / `team_retry` 工具：成员可分角色、按任务指定模型、显式开放写权限；任务支持依赖（`dependsOn`）与审查（`verifies`），依赖到齐自动开跑、上游报告自动交给下游，审查不通过返工一次，依赖失败的下游标为受阻，全部落定后汇总报告交回主模型。
  - 由主模型按任务形状自行判断是否组建团队，不需要用户点名；开跑前弹出「组建团队」审批卡（目标、成员、任务依赖与审查关系），可选「本次会话总是允许」，「全部自动」挡位不再询问；拒绝只退回给主模型，不中止整轮。
  - 会话里只显示一张两行的入口卡（目标、阶段、成员头像、完成数、耗时、全队 token 与预估费用），点开后在主窗口标签组中打开团队控制台：统计、拓扑与数据流、成员状态、任务时间轴、Token 消耗与通信记录，右侧抽屉可查看成员 / 任务 / 两人对话详情；从历史会话打开显示当时的终态。
  - 主控会按候选模型的上下文窗口、单价与是否支持思考为成员分配模型：例行收集与汇总用便宜的模型，读大量内容的用窗口大的，设计与疑难排查用会思考或更贵的，审查者尽量与被审查者用不同模型；用户明确指定的模型始终优先。
  - 成员因接口断流等瞬时故障失败时自动重跑同一任务（每个任务最多 2 次，下游不受阻）；失败 / 超时 / 取消的任务可在控制台抽屉中「重试」或「改派给」其他成员，主模型也可通过 `team_retry` 恢复。命令结果未知（可能重复执行）、拒答、超时不自动重跑。
  - 限制：成员默认只读、不能执行命令，需显式开启 `allowCommands`，且每条命令仍按审批挡位确认；候选模型只限当前供应商实例的聊天模型；对外 MCP 不提供团队工具。
  - 变更：`bbd70a14`、`42430f19`、`9dd3d5f4`、`23b5b698`、`5cc50a33`、`33b0537c`、`7d465324`、`1e9a83c5` · [PR #760](https://github.com/joesdu/VelaShell/pull/760)、[PR #761](https://github.com/joesdu/VelaShell/pull/761)、[PR #765](https://github.com/joesdu/VelaShell/pull/765)、[PR #767](https://github.com/joesdu/VelaShell/pull/767)、[PR #768](https://github.com/joesdu/VelaShell/pull/768)、[PR #769](https://github.com/joesdu/VelaShell/pull/769)

- **长任务的运行内上下文管理与断点续跑**：Agent 跑长任务时不再轻易撑爆上下文窗口，中途停下、出错或程序退出后也能接着做。
  - 每次请求前按上下文占用分三档整理：≥50% 清理较早的大块工具输出、≥70% 只保留最近 4 份、≥85% 把较早步骤写成进度摘要；不改动原始消息，回复里留整理分隔条。受「上下文压缩」开关控制，子 Agent 与团队成员的任务同样适用。
  - 停下 / 出错 / 退出后出现「继续执行」卡片：最近步骤原样放回、更早的以摘要交代；没拿到结果的调用一律标为「结果未知」、不重放副作用；中断时仍在运行的后台命令会列出来，要求先核实再决定是否重跑。
  - IM 桥接同样支持：`/stop`、超时或出错后提示 `/continue`，重启后仍可续跑；`/new` 丢弃旧检查点。
  - 删除会话、清空历史或从某条消息起删除时，同步清理对应的检查点（其中含截短的远端文件内容与命令输出）。
  - 变更：`bbd70a14`、`23b5b698` · [PR #760](https://github.com/joesdu/VelaShell/pull/760)、[PR #761](https://github.com/joesdu/VelaShell/pull/761)

- **文件改动一目了然：git 风格对比与「已更改文件」条**：Agent 改过哪些文件、改了什么，可以直接在 VelaShell 里审阅和修订。
  - 工具卡对写入 / 修补的文件显示行级差异（新旧行号、增删着色、3 行上下文、`+N −M`），查看历史记录时同样可见。
  - 输入框上方出现「N 个文件已更改」条（A / M / D / R 标记，同一文件合并，可清空，按对话隔离）；点文件在主标签区打开对比文档：TextMate 语法高亮、左右并排且右侧可编辑、分割条可拖动；保存（按钮或 Ctrl+S）时做冲突检测（覆盖 / 重新载入 / 取消），保留 BOM 与换行风格。
  - 并排两侧滚动同步（含横向），未改动的行严格对齐，插入 / 删除段按比例平滑跟随；「上一处 / 下一处改动」会真正滚到位。
  - 限制：关闭有未保存修改的对比标签时暂不提示；已更改文件列表不保存到历史；审批卡仍是 `- 旧 / + 新` 文本。
  - 变更：`19effd75`、`0a0e044d` · [PR #766](https://github.com/joesdu/VelaShell/pull/766)、[PR #770](https://github.com/joesdu/VelaShell/pull/770)

## 🤖 AI 助手

### 工具调用与文件工具

- **工具调用收纳成组**：相邻的工具调用合成一组，组头写「已执行 N 个操作 · 常用工具 ×次数 · N 个需留意」，运行中露出最近两张，模型开始输出正文后收成一行，点组头展开；一轮结束后思考、工具组与过渡正文收进默认收起的「工作过程」（被停下、出错或无结论时不收）。
  - 工具卡详情按「键: 值」逐行显示，多行脚本原样分行，不再出现转义码；失败 / 被拒的调用显示警告图标；工具卡按类别显示图标（MCP 等外部工具为插头图标）。
  - 交给模型的工具结果中文不再以 `\uXXXX` 发送，节省 Token；历史每轮保存的工具调用由 12 次提高到 200 次（按 16 万字符总预算截短）。
  - 变更：`bbd70a14`、`19effd75` · [PR #760](https://github.com/joesdu/VelaShell/pull/760)、[PR #766](https://github.com/joesdu/VelaShell/pull/766)

- **文件工具更省 Token**：`read_local_file` / `read_remote_file` 支持 `offset` / `limit` 分段读取并带行号（默认且最多 2000 行），大文件不再整份拒绝；新增只读、免审批的 `search_local_files`、`find_local_files`、`search_remote_files`；`patch_local_file` / `patch_remote_file` 支持 `replaceAll` 与多处编辑（一次审批，任一处不匹配就整体不写），旧参数保持兼容。
  - `search_remote_files` 由工具拼装 `grep` 命令、每个参数整体转义，无法拼出第二条命令。
  - 变更：`19effd75` · [PR #766](https://github.com/joesdu/VelaShell/pull/766)

### 模型与供应商

- **模型在当前账号不可用时自动换用其它模型**：供应商明确答复「该账号没有这个模型」（`model_not_found` 等）时，主模型自动切到同一供应商、同一凭据下的其它聊天模型继续，切换卡写明原因，可在模型菜单换回；子 Agent / 团队成员在尚未输出、未调工具时依次换用候选模型重跑。
  - 裸 404 与普通参数错误不触发切换；已经调用过工具后才失败的子任务不重跑；计费与数据去向不变（同一账号）。
  - 变更：`04831998` · [PR #760](https://github.com/joesdu/VelaShell/pull/760)

- **models.dev 连不上时不再阻断拉取模型，发行包内置规格快照**：此前 models.dev（模型上下文窗口与单价目录）超时会让整次「拉取模型」失败；现在两者并行，规格库单独 12 秒期限，失败只退回缓存，并在「模型设置」「连接供应商」提示「models.dev 暂时连不上」。
  - 发行包随附发版时抓取的 models.dev 快照（只含近 12 个月的型号，约 134 KB），无外网或被墙的用户首次拉取模型也有窗口与单价可用；快照与本地缓存按型号合并，较新的覆盖，联网用户不会因此丢失老型号的规格。
  - 变更：`22c981e9`、`c2ddb430`、`b58620bb` · [PR #762](https://github.com/joesdu/VelaShell/pull/762)、[PR #763](https://github.com/joesdu/VelaShell/pull/763)

### 用量与上下文

- **右下角用量改为分段面板，并新增「上下文明细」**：状态行显示 `↑累计输入 ↓累计输出 | 占用条 上下文 % · 缓存 %`；悬停面板按来源分段（系统提示词、内置工具定义、按服务器的 MCP 工具定义、压缩摘要、消息），在条上标出自动压缩线与剩余空间，接近时变警告色，并显示提示缓存读取 / 写入 / 未命中与本会话累计。
  - 点击用量打开「上下文明细」浮层：方格图与合计表、结论提示（未被调用的 MCP 服务器、最大的工具结果、固定开销、压缩已关闭等），可展开到单段内容、单个工具与单台服务器；Esc 关闭。
  - 团队卡 / 控制台的每个任务显示实际模型、思考强度、上下文占用、token 与预估费用，团队头部显示合计费用（单价由用户填写，只显示数字不带币种）。
  - 分类数值为本地估算，按供应商报告的输入等比缩放，与状态行合计一致。
  - 变更：`5cc50a33`、`5a4d36a8` · [PR #765](https://github.com/joesdu/VelaShell/pull/765)、[PR #771](https://github.com/joesdu/VelaShell/pull/771)

### 输入框

- **↑↓ 按屏幕上显示的行移动**：长内容自动折行后，光标不在显示的第一行时按 ↑ 只上移一行，到第一行才切换到上一条消息；↓ 同理。手动换行（Shift+Enter）同样按单独一行处理，与其他 Agent 软件一致。
  - 变更：`928a5c96` · [PR #764](https://github.com/joesdu/VelaShell/pull/764)

## 🐛 修复与稳定性

- **OpenAI 兼容协议的缓存写入一直显示为 0**：OpenAI Chat Completions / Responses 协议（含中转站、OpenRouter、DeepSeek 旧口径）返回的缓存写入用量此前被丢弃，现在会补回并参与费用估算；官方 OpenAI 不报告写入时面板注明「未报告」。Anthropic 协议原本就正常。
  - 变更：`5cc50a33` · [PR #765](https://github.com/joesdu/VelaShell/pull/765)

- **正常完成的团队被显示为「已取消」**：一轮结束释放编排器时会无条件标记取消，现在只有尚未落定的团队才算取消。
  - 变更：`9dd3d5f4` · [PR #760](https://github.com/joesdu/VelaShell/pull/760)

- **团队任务同步完成时团队永不收尾**：任务同步落定时完成回调早于记下子任务 id，导致团队一直不结束；改为预先生成 id。
  - 变更：`bbd70a14` · [PR #760](https://github.com/joesdu/VelaShell/pull/760)

- **断点续跑检查点的写入竞态**：一轮正常结束删掉检查点的同时，定时写入可能把它写回来，下次打开出现多余的续跑卡；写与删现在串行。
  - 变更：`23b5b698` · [PR #761](https://github.com/joesdu/VelaShell/pull/761)

- **AI 插件的对比标签退回成独立小窗**：宿主此前会把 AI 插件的所有文档面板请求都放进 AI 专属区，专属区已被聊天面板占用时就退回独立窗口；现在只有 `Placement = Right` 且专属区空着时才放进专属区，其余进入主标签区。
  - 变更：`19effd75` · [PR #766](https://github.com/joesdu/VelaShell/pull/766)

- **团队控制台抽屉打开时闪烁**：抽屉与遮罩改为常驻、只切换目标位置与透明度，开合都有 300 ms 平滑滑入 / 滑出，不再先在终点闪现。
  - 变更：`7d465324` · [PR #768](https://github.com/joesdu/VelaShell/pull/768)

## ⚠️ 升级说明 / 不兼容变更

- 无需要用户额外操作的不兼容变更。
- 内置 AI 插件版本由 1.7.8 升到 1.8.5。会话历史中团队与子任务新增的字段都有默认值，旧会话照常读取；旧会话里没有任务起点与交接 / 审查消息，团队控制台的任务时间轴从 0 起画。变更：`33b0537c` · [PR #767](https://github.com/joesdu/VelaShell/pull/767)
- 依赖更新：ReactiveUI 27.0.0 → 27.0.1，ReactiveUI.Avalonia 12.1.6 → 12.1.7，SonnetDB.Core 5.0.0 → 5.1.1，AI 插件的 Anthropic SDK 13.8.0 → 13.9.0。变更：`495fdef6` · [PR #772](https://github.com/joesdu/VelaShell/pull/772)

## 📚 文档与生态

- 中英文用户手册同步：工具调用的显示、团队模式（入口卡、团队控制台、出错与重试、按任务分配模型）、长任务与断点续跑、文件工具与对比标签、供应商一节的 models.dev 快照、底栏用量与「上下文明细」。变更：`bbd70a14`、`23b5b698`、`c2ddb430`、`b58620bb`、`5cc50a33`、`19effd75`、`33b0537c`、`7d465324`、`1e9a83c5`、`5a4d36a8` · [PR #760](https://github.com/joesdu/VelaShell/pull/760)、[PR #761](https://github.com/joesdu/VelaShell/pull/761)、[PR #762](https://github.com/joesdu/VelaShell/pull/762)、[PR #763](https://github.com/joesdu/VelaShell/pull/763)、[PR #765](https://github.com/joesdu/VelaShell/pull/765)、[PR #766](https://github.com/joesdu/VelaShell/pull/766)、[PR #767](https://github.com/joesdu/VelaShell/pull/767)、[PR #768](https://github.com/joesdu/VelaShell/pull/768)、[PR #769](https://github.com/joesdu/VelaShell/pull/769)、[PR #771](https://github.com/joesdu/VelaShell/pull/771)
- 发布流水线新增 models.dev 快照抓取（`scripts/Update-ModelsDevSnapshot.ps1`），四个平台的打包任务共用同一份快照；中英 README 的流水线说明同步。变更：`c2ddb430` · [PR #762](https://github.com/joesdu/VelaShell/pull/762)
- velashell-docs 同步（`plugins/11-automation-and-ai.md`、`plugins/12-security-threat-model.md`、`plugins/08-ui-extensions.md`，中英同步）：[PR #233](https://github.com/VelaShellLabs/velashell-docs/pull/233)、[PR #235](https://github.com/VelaShellLabs/velashell-docs/pull/235)、[PR #236](https://github.com/VelaShellLabs/velashell-docs/pull/236)、[PR #237](https://github.com/VelaShellLabs/velashell-docs/pull/237)、[PR #238](https://github.com/VelaShellLabs/velashell-docs/pull/238)、[PR #239](https://github.com/VelaShellLabs/velashell-docs/pull/239)、[PR #240](https://github.com/VelaShellLabs/velashell-docs/pull/240)、[PR #241](https://github.com/VelaShellLabs/velashell-docs/pull/241)。

## ✅ 验证

- [三平台 CI #44084976443](https://github.com/joesdu/VelaShell/actions/runs/44084976443)（对应提交 `01e4e7b8`）：Windows、Ubuntu、macOS 构建与测试全部通过，0 失败；其中 `VelaShell.Tests` 在 Windows / Ubuntu / macOS 上分别 3485 / 3454 / 3475 通过，8 / 39 / 18 跳过；`VelaShell.Plugin.Ai.Tests`（分 4 片运行）在三个平台上均为 2374 项全部通过、0 跳过。[CodeQL #44084974883](https://github.com/joesdu/VelaShell/actions/runs/44084974883) 通过。
- CI 按既有规则排除 `DockerIntegration`、`CrossPlatform`、`Interop` 类别，部分依赖平台或外部环境的用例跳过；跳过不计为通过。
- 各 PR 合并前的本机验证（Windows / .NET 11，Debug）：
  - AI 测试工程全量随版本推进由 2228 项增至 2374 项；最后一次全量（#771）2374 项全部通过、0 跳过。
  - #760 / #761 / #769 各有 1 项失败，均已说明原因：#760 的计划模式工具清单用例修正后通过；#761 的「拉取模型」UI 用例全量并发下超时、单独重跑 3 次通过；#769 的失败用例是工作区内另一项未提交的复现用例，不在该 PR 中（对应的对比标签滚动同步问题由 #770 修复）。
  - #770：全量 26 项失败，原因是构建产物放在仓库外临时目录、测试找不到源码；换到仓库内产物目录后全部通过。
  - #766：宿主 `AiAssistantRegionUiTests` / `PluginPanelUiTests` 与扫描类测试 44 项通过；#765：宿主本地化与字体令牌用例 8 项通过。
  - models.dev 快照裁剪经插件解析器比对 323 家供应商、8753 个模型规格零差异。
- 发版前核对：AI 插件对宿主共享依赖（Markdig、TextMateSharp 等）的生效版本经 MSBuild 求值与宿主完全一致。
- 未完成的人工验收：团队调度、运行内压缩、模型自动分配与断流重试未接真实模型验证；对比标签的滚动同步、团队控制台与上下文明细未在真实窗口中人工验收；IM 渠道的续跑未人工验收；models.dev 快照抓取的流水线步骤以本版发布流水线为首次实跑。

## 🔗 相关变更

- [PR #760：工具调用收纳、运行中上下文管理、断点续跑与团队模式](https://github.com/joesdu/VelaShell/pull/760) — `bbd70a14`、`2390acd5`、`9dd3d5f4`、`e94ed3fd`、`df86897a`、`42430f19`、`04831998`
- [PR #761：检查点随会话清理；成员可执行命令、团队开跑前确认、子任务运行内压缩、IM 续跑](https://github.com/joesdu/VelaShell/pull/761) — `23b5b698`
- [PR #762：models.dev 连不上时不再阻断拉取模型；发行包内置 models.dev 规格快照](https://github.com/joesdu/VelaShell/pull/762) — `22c981e9`、`c2ddb430`、`be11fb6c`
- [PR #763：内置 models.dev 快照只留近一年的型号，与缓存合并](https://github.com/joesdu/VelaShell/pull/763) — `b58620bb`
- [PR #764：AI 插件输入框的 ↑↓ 和其他 Agent 软件的行为一致](https://github.com/joesdu/VelaShell/pull/764) — `928a5c96`
- [PR #765：团队卡显示每个任务的模型与预估费用；右下角用量改为分段面板并补回缓存写入](https://github.com/joesdu/VelaShell/pull/765) — `5cc50a33`
- [PR #766：文件工具省 Token、工具图标、git 风格改动对比与已更改文件条](https://github.com/joesdu/VelaShell/pull/766) — `19effd75`
- [PR #767：团队模式改为会话入口卡 + 标签页里的团队控制台](https://github.com/joesdu/VelaShell/pull/767) — `33b0537c`
- [PR #768：团队成员出错后自动重试、可手动重试与改派；修控制台抽屉打开时的闪烁](https://github.com/joesdu/VelaShell/pull/768) — `7d465324`、`a22b5156`、`2faf13a3`
- [PR #769：团队主控按任务给成员挑模型](https://github.com/joesdu/VelaShell/pull/769) — `1e9a83c5`
- [PR #770：对比标签并排两侧的滚动同步真正生效](https://github.com/joesdu/VelaShell/pull/770) — `0a0e044d`
- [PR #771：用量悬停按来源分段，点击打开上下文明细](https://github.com/joesdu/VelaShell/pull/771) — `5a4d36a8`
- [PR #772：格式化代码与依赖更新](https://github.com/joesdu/VelaShell/pull/772) — `4b2bcf23`、`495fdef6`
- 完整差异：[1.8.4...1.8.5](https://github.com/joesdu/VelaShell/compare/1.8.4...1.8.5)
