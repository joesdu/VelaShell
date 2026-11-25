本次更新聚焦 AI 对话作图与改图、图片输入与预览、FTP/FTPS 双向中转可靠性，让图片创作可以接续聊天上下文，也让跨服务器文件传输的完成状态、失败原因和重试边界更清楚。

## ✨ 重点更新

- **在当前对话中生成和修改图片**：日常聊天模型可以结合前文调用独立配置的图片模型，无需切换聊天顶栏模型。
  - 在「AI 全局设置 → 对话作图」选择默认图片模型后，可以直接说「根据刚才的方案画一张图」或「把上一张背景改白」；也可使用 `@imagegen`、`@imageedit` 明确发起。
  - 支持引用本对话上传或生成的图片，恢复历史后可以继续改图。聊天模型须支持工具调用；图片服务单独计费，计划模式和 IM 桥接本次不开放图片工具。
  - 变更：`90ecc2e3` · [PR #681](https://github.com/joesdu/VelaShell/pull/681)

- **FTP → SFTP 优先流式传输，确认完整后再替换目标文件**：满足条件时无需在本机缓存整个文件，减少大文件中转对本地磁盘的占用。
  - 从头传输到支持原子替换的 SFTP 目标时，经每文件约 4 MiB 的有界缓冲写入远端同目录临时文件；源读取、长度检查、FTP 完成回复及目标上传全部成功后，才提交正式文件并显示完成。
  - 反向 SFTP → FTP 直接使用源读取流。目标不支持安全提交、目标为符号链接、FTP 源续传等场景仍保留完整缓存回退，不能将所有传输理解为零落盘。
  - 变更：`3a9d75ba`、`1f689ff8` · [PR #686](https://github.com/joesdu/VelaShell/pull/686)、[PR #687](https://github.com/joesdu/VelaShell/pull/687)

## 🎨 AI 对话、图片与输入

### 对话作图与图片引用

- **独立图片模型配置**：模型设置新增明确的 `OpenAI Images` 协议入口，Base URL 填 API 基地址，模型 ID 使用供应商实际支持的名称。
  - 默认图片模型与聊天模型分别选择；原先手动选择图片模型直接文生图的入口继续可用。
  - 改图最多使用 3 张 PNG/JPEG/WebP 参考图，每张不超过 20 MiB；输入附件仍受单个 5 MiB 的限制。每轮图片结果最多 4 张。
  - 变更：`90ecc2e3` · [PR #681](https://github.com/joesdu/VelaShell/pull/681)

- **粘贴本地图片并继续编辑**：除附件按钮和拖入文件外，现在可以在输入框粘贴剪贴板图片；发送前可从附件栏移除。
  - 沿用每条消息最多 4 个附件、单个最多 5 MiB；剪贴板图片编码为 PNG，超过 2500 万像素会拒绝。没有图片时保留原有文字粘贴行为。
  - 视觉理解需要聊天模型与供应商支持；支持的输入图片与生成结果保存在插件私有目录，供当前会话及历史续聊引用。
  - 变更：`90ecc2e3` · [PR #681](https://github.com/joesdu/VelaShell/pull/681)

### 图片预览与聊天输入

- **独立大图预览窗口**：图片右下角新增保存和查看大图两个悬浮图标，查看时优先按原始像素展示，过大的图片默认适应屏幕。
  - 支持百分比显示、加减缩放、100%、适应窗口、`Ctrl+滚轮`和双击切换；放大后可滚动查看，`Esc` 关闭。
  - 保存仍使用系统保存框和原始图片字节；关闭聊天面板时一并关闭预览并释放资源。
  - 变更：`90ecc2e3` · [PR #681](https://github.com/joesdu/VelaShell/pull/681)

- **工具、文件引用和本地命令分开输入**：`@` 显示图片工具，`#` 显示远端文件，输入框开头的 `/` 显示本地命令。
  - `@imagegen` / `@imageedit` 无需绑定 SSH 会话，但仍须配置默认图片模型。远端文件改用 `#/etc/hosts` 或 `#"/opt/my app/config"`，保留补全与引用芯片。
  - `/clear` 新建会话且保留历史；`/resume` 打开历史，支持会话 ID 或标题搜索；`/help` 显示已支持命令。这些命令在本地处理，不发送给模型。
  - 变更：`90ecc2e3` · [PR #681](https://github.com/joesdu/VelaShell/pull/681)

## 📁 FTP/FTPS 与跨服务器传输

### 单连接与完整性检查

- **每个 FTP/FTPS 会话固定使用一条控制连接**：上传、下载与目录查询排队共享连接，避免同一会话并发建立多个客户端。
  - 旧配置中较大的 `MaxConnections` 值不再扩大 FTP 会话的连接数；不同独立会话不合并，SFTP 并发设置不受此限制。
  - 变更：`1f689ff8` · [PR #687](https://github.com/joesdu/VelaShell/pull/687)

- **短读与完成回复检查更明确**：FTP 源以二进制 `SIZE` 为长度依据，已知长度与实际读取不一致时报告传输不完整，避免被普通 FTP 错误掩盖。
  - 即使异常链含 `426` 或连接重置，仍保留可判断的长度不匹配原因；源最终回复失败时不发布流式路径的正式目标。
  - 服务端不支持 `SIZE` 时，无法单凭长度证明完整性；本次未增加源快照或全文件远端哈希保证，已有损坏文件仍需核对后重传。
  - 变更：`509f3dd2`、`ddf5ba4b`、`1f689ff8` · [PR #684](https://github.com/joesdu/VelaShell/pull/684)、[PR #685](https://github.com/joesdu/VelaShell/pull/685)、[PR #687](https://github.com/joesdu/VelaShell/pull/687)

### 回退、重试与进度

- **完整缓存回退与上传重试复用**：FTP 源不适用流式安全提交时，先把文件完整下载到用户目录 `.velashell/temp`，确认缓存和完成回复后再上传。
  - 同一次传输的自动上传重试复用已验证缓存，成功、取消或最终失败后删除；手动重试或冲突改名会重新准备。
  - 缓存占用本机磁盘且包含文件内容，本地磁盘或权限错误不会按网络故障自动重试。SFTP → SFTP 原有流式路径保持兼容。
  - 变更：`509f3dd2`、`3a9d75ba` · [PR #684](https://github.com/joesdu/VelaShell/pull/684)、[PR #686](https://github.com/joesdu/VelaShell/pull/686)

- **提交结果与进度一致**：准备缓存时显示下载准备阶段，目标提交确认后才报告完成；未知源大小在成功提交后按实际字节补齐进度。
  - 提交前失败只清理此次远端临时文件；服务端明确拒绝时保留实际错误。若断线或超时导致提交结果未知，则停止自动重试，需先人工核对目标，避免重复覆盖。
  - 开启「设置 → 文件传输 → 日志记录」后，失败尝试会留下 `ATTEMPT_FAILED` 诊断，即使后续重试成功也可追溯。
  - 变更：`509f3dd2`、`3a9d75ba`、`1f689ff8` · [PR #684](https://github.com/joesdu/VelaShell/pull/684)、[PR #686](https://github.com/joesdu/VelaShell/pull/686)、[PR #687](https://github.com/joesdu/VelaShell/pull/687)

## 🔐 安全与隐私

- **图片调用保持明确的授权与计费边界**：每次询问和只读自动档会展示图片服务目标、提示词与参考图片信息；「全部自动」沿用跳过插件审批的既有语义。
  - 提示词与参考图会发送给所选图片供应商；失败或拒绝后本轮不自动重试或切换服务，已完成工具结果可复用。图片模型不通过实际作图进行探活，避免检测时计费。
  - 图片引用只在所属会话有效，不能用任意本机路径或其他会话的图片 ID 读取文件；只接收直接返回的 base64 图片，不额外下载供应商图片 URL。删除历史不会立即删除已存图片文件。
  - 变更：`90ecc2e3` · [PR #681](https://github.com/joesdu/VelaShell/pull/681)

- **中转失败诊断减少敏感信息写入**：传输日志记录错误类型、重试次数、长度差异和已确认字节，不写异常消息、服务器回复正文或额外文件路径。
  - 流式提交要求目标目录具备新建与重命名权限；随机临时文件名不构成对不可信共享可写目录的安全保证。
  - 变更：`509f3dd2`、`3a9d75ba` · [PR #684](https://github.com/joesdu/VelaShell/pull/684)、[PR #686](https://github.com/joesdu/VelaShell/pull/686)

## 🐛 修复与稳定性

- **中转缓冲和 FTP 连接处理**：修复短读时缓冲分配膨胀、提交被元数据队列阻塞、FTP 完成回复与失效连接释放，并完善 `425` / `450` 重试分类。
  - 变更：`509f3dd2`、`3a9d75ba`、`1f689ff8` · [PR #684](https://github.com/joesdu/VelaShell/pull/684)、[PR #686](https://github.com/joesdu/VelaShell/pull/686)、[PR #687](https://github.com/joesdu/VelaShell/pull/687)

- **跨平台回归测试更稳定**：修复 FTP 环回夹具的延迟 ACK 等待与 Windows 被动端口耗尽、文件补全弹层等待、Key 编辑行定时刷新和远程编辑连续保存测试中的时序竞争。
  - 同时消除模型端点重复保存后的无效 HTTP 等待与未使用的测试建库；这些是测试夹具修正，没有缩短产品超时或减少批量传输断言。
  - 变更：`ddf5ba4b`、`50384447`、`d24d3452`、`f8eea7ee`、`55769df8` · [PR #685](https://github.com/joesdu/VelaShell/pull/685)、[PR #687](https://github.com/joesdu/VelaShell/pull/687)、[PR #688](https://github.com/joesdu/VelaShell/pull/688)

- **.NET 11 RC3 配套依赖**：更新依赖注入、受保护数据和 SourceLink 等相关依赖，为本版本构建提供对应的 RC3 组件。
  - 发行包包含运行时，普通用户无需自行安装 SDK。
  - 变更：`b767906b` · [PR #680](https://github.com/joesdu/VelaShell/pull/680)

## ⚠️ 升级说明 / 不兼容变更

- **AI 远端文件引用语法变化**：旧 `@/路径` 不再解析为文件引用，请改为 `#/路径`；`@` 现在用于工具菜单。
- **作图需要单独配置**：在模型设置添加受支持的图片模型，再在全局设置中选为默认图片模型。计划模式和 IM 桥接不能调用图片工具，聊天模型的视觉与工具能力仍取决于供应商。
- **FTP 会话改为单连接排队**：旧的较大 FTP `MaxConnections` 值不再生效；大量传输时，目录刷新可能等待当前传输释放连接。SFTP 并发不变。
- **跨服务器流式提交需要目录权限**：仅有正式文件的写权限可能不足，还需目标目录的新建和重命名权限；缓存回退则需足够本机磁盘空间。提交结果未知时先核对远端目标再手动重试。
- macOS 安装包未签名、未公证；Linux AppImage、deb、rpm 未签名，沿用现有安装方式。

## 📚 文档与生态

- 中文与英文用户手册同步说明图片模型配置、剪贴板与预览、`@` / `#` / `/` 输入语义，以及 FTP 单连接、流式提交、完整缓存、重试和完整性限制。
  - 变更：`90ecc2e3`、`509f3dd2`、`ddf5ba4b`、`3a9d75ba`、`1f689ff8` · [PR #681](https://github.com/joesdu/VelaShell/pull/681)、[PR #684](https://github.com/joesdu/VelaShell/pull/684)、[PR #685](https://github.com/joesdu/VelaShell/pull/685)、[PR #686](https://github.com/joesdu/VelaShell/pull/686)、[PR #687](https://github.com/joesdu/VelaShell/pull/687)
- `VelaShell/velashell-docs` 对应中英文开发文档均已合并：[PR #153](https://github.com/VelaShell/velashell-docs/pull/153)（AI）、[PR #155](https://github.com/VelaShell/velashell-docs/pull/155)（FTP 缓存）、[PR #156](https://github.com/VelaShell/velashell-docs/pull/156)（短读诊断）、[PR #157](https://github.com/VelaShell/velashell-docs/pull/157)（流式提交）、[PR #161](https://github.com/VelaShell/velashell-docs/pull/161)（单连接与双向中转）。

## ✅ 验证

- 发布提交：`23e1d604`（`main`），变更范围为 `1.7.4...1.7.5`。
- 发布提交的 [三平台 CI](https://github.com/joesdu/VelaShell/actions/runs/40939843346)：Windows、macOS、Linux 构建与测试均通过；构建使用 `dotnet build VelaShell.slnx -c Debug --nologo -warnaserror`，Linux 额外的 `runtime-async=off` 编译检查也通过。
- 首轮 Linux 有一条 X Server 窗口事件合并测试未达断言阈值；未修改发布源码，重跑 Linux 作业后构建、测试及额外编译检查全部通过（[第 2 次运行](https://github.com/joesdu/VelaShell/actions/runs/40939843346/attempts/2)）。Windows、macOS 使用首轮已通过的结果。
- CI 排除需要专用环境的 `DockerIntegration`、`CrossPlatform` 与 `Interop` 类别；平台或环境不满足的用例按测试报告记为跳过，不计为实际验证通过。
- 本次发版未在本机重复完整构建或测试，以上结果以链接中的 CI 为准；未进行真实计费图片供应商、系统剪贴板/桌面 GUI、现场 FTP/FTPS/OpenSSH 服务器的端到端验收，不据受控协议测试宣称现场失败率已归零。

## 🔗 相关变更

- [PR #680：升级 .NET 11 RC3](https://github.com/joesdu/VelaShell/pull/680) · `b767906b`
- [PR #681：上下文生图改图、图片预览与输入命令](https://github.com/joesdu/VelaShell/pull/681) · `90ecc2e3`
- [PR #682：代码格式整理](https://github.com/joesdu/VelaShell/pull/682) · `3031bc2d`
- [PR #683：CI 异常修复](https://github.com/joesdu/VelaShell/pull/683) · `504df7ad`
- [PR #684：FTP 中转完整缓存与上传重试复用](https://github.com/joesdu/VelaShell/pull/684) · `509f3dd2`
- [PR #685：FTP 跨平台超时、短读分类与夹具修复](https://github.com/joesdu/VelaShell/pull/685) · `ddf5ba4b`、`50384447`
- [PR #686：FTP 中转优先有界流式传输并安全提交](https://github.com/joesdu/VelaShell/pull/686) · `3a9d75ba`
- [PR #687：FTP 单连接与双向流式中转](https://github.com/joesdu/VelaShell/pull/687) · `1f689ff8`、`d24d3452`
- [PR #688：消除 CI 无效等待并修复测试竞态](https://github.com/joesdu/VelaShell/pull/688) · `f8eea7ee`、`55769df8`

**完整变更**：[1.7.4...1.7.5](https://github.com/joesdu/VelaShell/compare/1.7.4...1.7.5)
