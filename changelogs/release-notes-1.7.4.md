本次更新聚焦 SSH / SFTP 连接能力、文件管理与传输、隧道转发、插件生态和安全保护，为日常远程运维、文件交换与自动化工作流带来更完整、更可靠的使用体验。

## ✨ 重点更新

- **SSH / SFTP 连接与诊断全面增强**：新增系统密钥库认证、连接信息面板、协议轨迹和更完整的 `ssh_config` 互操作能力。
  - **文件管理与传输体验升级**：文件栏新增导航历史、过滤、快捷操作、下载为指定名称、传输队列持久化和失败重试。
  - **隧道转发能力扩展**：支持远程动态 SOCKS、Unix 域套接字转发、实时连接状态与按隧道限速。
  - **插件与安全能力加强**：插件可向消息中心发送通知，敏感凭据使用系统密钥链保护，插件下载强制 HTTPS 与 SHA-256 校验。

## 🔐 SSH 与连接能力

### 系统密钥库认证

- **Windows 系统密钥库 SSH 认证**：可以从 Windows 证书存储、TPM 或智能卡中选择不可导出的 RSA / ECDSA 密钥进行 SSH 公钥认证。
  - VelaShell 只保存密钥引用，连接时通过系统加密能力完成签名，不会导出或保存私钥。
  - 支持复制公钥；连接配置可通过 CSV / JSON 备份往返，`ssh_config` 导出会保留相关信息作为注释。
  - 该认证方式仅在支持 Windows 系统密钥库的环境中显示；同步到其他平台的配置会如实提示不可用。
  - 变更：`5d3acbd1` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)

- **系统密钥库用户证书**：系统密钥库认证现在可以选配 OpenSSH 用户证书。
  - 适用于由 CA 为系统密钥库中的密钥签发用户证书的登录场景，不需要把对应公钥单独写入服务器的 `authorized_keys`。
  - VelaShell 会在连接前核对证书与系统密钥是否匹配；留空证书时保持原有公钥认证行为。
  - 变更：`651daf51` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

### 连接信息与诊断

- **连接信息面板**：在 SSH 会话标签页的右键菜单中，可以查看对端、协商结果、认证方式、计时、流量、重协商、通道以及代理 / 跳板信息。
  - 信息来自已建立连接的只读状态，不会为了展示面板而额外探测服务器。
  - 可以刷新内容，也可以复制为文本；经过跳板连接时会按实际跳板链展示。
  - 变更：`ce674c81` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

- **协议轨迹**：连接诊断新增协议轨迹页面，记录报文方向、类型、长度、序号和通道号，并按跳板分组展示。
  - 轨迹只记录元数据，不记录报文内容；认证报文不会显示发送内容。
  - 仅在运行连接诊断时启用，不会影响普通连接和「测试连接」。
  - 支持复制和随诊断报告导出。
  - 变更：`bb446acf` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)

- **协商失败快速处理**：当 SSH 协商失败时，可以只放开诊断明确指出的算法并重新连接。
  - 只追加当前失败的算法，不会打开全部旧算法；跳板协商失败时修改对应跳板的配置。
  - 同一操作也推广到独立 SFTP 诊断和「测试连接」流程。
  - 变更：`cab1010a`、`9beb5cac` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)、[PR #672](https://github.com/joesdu/VelaShell/pull/672)

### SSH 配置互操作与错误提示

- **增强 `ssh_config` 导入 / 导出**：补充算法清单、压缩、Agent、Agent 转发、X11 转发、按键时序混淆、证书、Unix 套接字和远程动态转发等配置的互通。
  - 对未明确写出的 OpenSSH 选项，按 VelaShell 自身默认值处理并在导入时保持清晰提示。
  - 变更：`3f96d453`、`5bc890e7`、`100b79e1`、`e3ed8da9` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)、[PR #672](https://github.com/joesdu/VelaShell/pull/672)

- **按连接指定 Agent 端点**：SSH 连接可以指定命名管道或 Unix 套接字形式的 Agent 端点，Agent 认证、转发和自动加载密钥使用同一配置。
  - 留空时继续使用系统默认 Agent；连接失败时会明确指出无法连接的端点。
  - 变更：`43d96e31` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)

- **结构化 SSH / SFTP 错误提示**：拨号、主机密钥、认证、Agent、端口转发、SFTP 子系统和通道错误现在按类型提供更明确的多语言提示。
  - SFTP 文件流的断开、读写、定位、刷新和关闭错误会转换为宿主可识别的中立错误类型，便于诊断和重试策略正确判断。
  - 变更：`5a94d9a1`、`3bcd8321`、`d61f5033`、`dc06c898`、`7bbc13f6`、`205076f3` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)

## 📁 文件管理与传输

### 文件栏操作

- **文件栏支持在当前位置打开终端**：SSH 文件栏右键菜单新增「在此处打开终端」。
  - 可以从终端侧栏、单栏文件标签或双栏文件标签中的 SSH 远端栏打开。
  - 目录行会以该目录作为终端起始目录；空白处使用当前目录。重新连接时会继续回到该目录。
  - SFTP 专用配置、FTP 和插件协议不显示此入口。
  - 变更：`9c974187` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

- **文件栏导航与操作快捷键**：本地栏和远端栏新增或完善以下能力：
  - 后退 / 前进按钮及 `Alt+←` / `Alt+→` 导航历史。
  - 当前目录过滤框，仅过滤当前已列出的文件，不会把过滤条件发送到服务器。
  - `F5` 刷新、`F2` 重命名、`Delete` 删除并确认。
  - 远端栏「上一级」按钮。
  - 列宽、分栏比例和文件栏布局跨重启记忆。
  - 变更：`8cf6ff9f`、`30d2ac94`、`026e9a62`、`545b0e37`、`d9393cc8` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)、[PR #674](https://github.com/joesdu/VelaShell/pull/674)

- **下载为指定名称**：远端普通文件右键菜单新增「下载为…」。
  - 仅对单个普通文件显示；目录、`..` 和多选不会显示该入口。
  - 在另存为对话框中指定现有文件名时，会明确询问是否替换；用户确认的替换决定不会被后续冲突策略再次覆盖。
  - 任务仍会进入传输队列，可查看进度、取消和处理传输状态。
  - 变更：`32d81c80` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

- **远端搜索与文件名编码**：远端文件栏支持在 POSIX 服务器上使用 `find` / `grep` 搜索，并支持按连接指定的字符集解读 SFTP 文件名。
  - 搜索对用户输入进行参数转义，并限制结果数量和执行时间；用户可以停止搜索并在结果中定位文件。
  - UTF-8 连接保持原有行为，非 UTF-8 服务器的文件名可以更可靠地往返。
  - 变更：`9ca53ca2`、`b69909b3` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

### 文件传输可靠性

- **传输队列持久化**：应用退出或连接断开时，未完成的普通文件传输会保留为「已暂停」，下次可以手动继续并从中断位置恢复。
  - AI 插件发起的传输仍使用插件自身的授权与回执流程，不进入普通传输队列。
  - 变更：`448d27bb` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

- **传输失败自动重试**：断线和超时支持按 `TransferMaxRetries` 次数进行退避重试。
  - 权限不足、磁盘空间不足、路径不存在等确定性错误会直接失败，不会盲目重复操作。
  - 续传下载也纳入带宽限制，避免启用限速后续传分支绕过限制。
  - 变更：`3176d48f`、`47c6fed7` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)、[PR #672](https://github.com/joesdu/VelaShell/pull/672)

- **插件下载授权信息更完整**：插件文件下载确认框现在显示远端文件大小，帮助用户在批准前判断传输范围。
  - 上传和下载确认框使用一致的大小展示方式；大小取审批前读取到的远端文件信息。
  - 变更：`8f078d0a` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

## 🌐 隧道与终端

### 隧道转发

- **远程动态 SOCKS 转发**：新增远程动态转发类型，由服务器端建立 SOCKS5 连接。
  - 可配置目标放行名单；不在名单中的目标会被拒绝，并在隧道状态中显示原因。
  - 变更：`22a75233` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

- **Unix 域套接字转发**：本地转发和远程转发支持 Unix 域套接字端点。
  - 会校验本机套接字路径、完整路径和平台限制；旧隧道配置仍可正常读取。
  - 变更：`f6c28e41` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

- **隧道状态与限速**：隧道面板显示实时速率和当前连接列表，并可以按隧道设置限速。
  - 启动时自动建立失败的隧道可以直接重试；单条隧道失败不会阻止其他隧道建立。
  - 远程监听、本机监听和本机目标连接的错误提示会明确指出失败发生在哪一端。
  - 变更：`b29224d4`、`173db63c`、`e21b331e` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

### 终端体验

- **终端输出规则**：设置中新增「输出规则」页，自定义高亮、触发器和自动应答共用一套规则配置。
  - 变更：`b48cc64e` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)

- **OSC 133 命令块操作**：终端识别命令块后支持复制当前命令和重新执行。
  - 命令块会在调整终端列宽后保持正确布局；同时修正了 zsh 初始化片段。
  - 已安装旧版 zsh 片段的用户需要重新复制一次片段，才能使用新的命令块能力。
  - 变更：`a2069034` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

- **共享密码填入体验**：从共享密码列表选择密码后会自动回车提交。
  - 密码正文和回车会在同一次出站输入中发送，避免用户在选中密码后顺手输入的字符被拼接到密码末尾。
  - 会根据当前终端换行模式发送正确的回车序列。
  - 变更：`646595ed`、`ce16f897`、`8ca7eb9c` · [PR #669](https://github.com/joesdu/VelaShell/pull/669)、[PR #670](https://github.com/joesdu/VelaShell/pull/670)、[PR #671](https://github.com/joesdu/VelaShell/pull/671)

- **快捷命令与会话管理改进**：命令补全选中带 `{{变量}}` 的快捷命令时会先询问变量值；资源管理器会话树支持按名称、主机、标签、备注和分组过滤。
  - 支持为会话设置标签页图标，以及将多个会话打开并同步输入到终端频道。
  - 变更：`25ae4aaa`、`93f93b8a`、`7d539e96`、`aa2acd94` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

## 🔌 插件与云同步

### 插件通知与文件能力

- **插件消息通知**：插件现在可以向消息中心发送通知。
  - 通知会校验插件身份，支持按通知 ID 去重、按插件限频、按插件静音，并在插件卸载时清理对应通知。
  - 消息中心显示插件来源；通知动作只能调用当前插件允许的命令。
  - 插件管理页可以在已有通知或已静音时切换「静音通知 / 恢复通知」。
  - 变更：`05211378` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

- **插件协议文件能力扩展**：插件协议可以报告符号链接、链接目标、文件修改时间和 SHA-256 校验值。
  - 宿主会按插件实际声明的能力处理文件操作；未实现或当前会话不支持时，会如实报告不支持，不会伪造结果。
  - 文件面板、删除、复制、递归下载和目录同步会正确区分链接本身与链接目标。
  - 变更：`2f638121` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

- **插件传输拒绝结果更明确**：源文件不存在、源文件被占用、传输额度已满等明确拒绝会在登记任务前返回结构化原因。
  - AI 插件可以区分明确拒绝与未知状态，不会把已知不可重试的错误当成回执丢失，也不会立即重复提交同一个请求。
  - 变更：`ab59d691`、`48c4c8d0` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

### 插件更新与版本校验

- **插件版本校验更严格**：插件清单版本统一使用可排序的 Semantic Version 规则。
  - 带前导零的预发布数字标识、空标识符或超出范围的数字字段会被判定为无效；安装时直接拒绝并说明修正方式。
  - 任一侧版本无法解析时，插件管理页显示未知，不会错误提示「有新版」或「已是最新」。
  - 变更：`3be2d051` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

- **插件下载安全校验**：插件更新只允许使用 HTTPS 下载地址，并且必须提供有效的 SHA-256 摘要。
  - HTTP 地址、缺少摘要或摘要格式不正确的下载请求会在发起网络请求前被拒绝。
  - 变更：`0aad9336` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)

- **插件管理体验**：插件管理页新增查看插件日志，补充「已停止」状态的多语言显示。
  - 日志按插件 ID 单独保存，单个日志文件限制为 1 MB 并轮转；查看窗口支持刷新和打开所在目录。
  - 变更：`49d50300`、`2f982c66` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

### 布局模板与云同步

- **布局模板同步与导入导出**：命名布局模板可以随 Gist 同步，也可以从管理窗口导出和导入。
  - 模板保存在独立的 `velashell-layouts.json` 载荷中；支持按模板 ID 合并、删除墓碑和冲突时选择替换或另存。
  - 导出文件带格式头和版本信息，不包含机密；引用本机不存在的连接时会提示用户。
  - 布局模板同步默认开启，存量用户首次同步时会纳入已有模板。
  - 变更：`d6db1462` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)

- **云同步敏感信息保护**：未设置端到端同步口令时，不再上传设置中的代理密码和 Webhook 地址。
  - 拉取不包含这些字段的载荷时，会按照设置导入规则保留本机已有值；使用端到端加密载荷时保持原有加密同步行为。
  - 当同步拉到「Webhook 告警已开启但本机没有地址」的设置时，消息中心会提示一次，并提供前往安全审计设置的入口。
  - 变更：`f2f7b285`、`847e8a2b` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)、[PR #674](https://github.com/joesdu/VelaShell/pull/674)

## 🛡️ 安全与隐私

- **本机加密密钥使用系统密钥链**：macOS 使用登录钥匙串，Linux 使用 Secret Service；数据目录只保留 `secret.key.ref` 引用清单。
  - Windows 继续使用 DPAPI 保护本地密钥文件；系统密钥链不可用时会回落到受保护的本地密钥文件并在消息中心提示。
  - 删除 VelaShell 数据目录不会删除系统密钥链中的对应条目；迁移过程中无法立即解开的已存密码不会被丢弃。
  - 变更：`62a4e480`、`04b1d407` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

- **已存凭据解不开时不再盲目登录**：如果已保存的密码或私钥口令无法用当前本机密钥解开，VelaShell 不会把密文原文拿去尝试认证。
  - SSH、SFTP、FTP 和插件连接会直接打开登录提示，并说明是密钥链不可用、密钥不在当前设备还是共享凭据无法解开。
  - 隧道、测试连接、巡检和跳板等不能弹登录框的流程会返回结构化的凭据不可用原因。
  - 变更：`0b3c6e6b` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

- **敏感输出遮盖增强**：改进会话日志、终端录制和诊断包中的敏感信息识别与遮盖。
  - 覆盖更多 Bearer / Basic 认证信息、GitLab / Stripe / Google / npm / PyPI 令牌、私钥正文、颜色序列后的令牌和转义输出。
  - 同时减少对 `token_type`、`secretName` 等非机密字段的误遮盖。
  - 变更：`742e0772`、`44fdbe63` · [PR #668](https://github.com/joesdu/VelaShell/pull/668)、[PR #675](https://github.com/joesdu/VelaShell/pull/675)

- **设置、密钥与审计改进**：设置导出支持按分类选择，敏感分类默认不勾选；代理密码和 Webhook 地址不会写入设置导出文件。
  - 私钥导入会使用 SSH 解析并核对 `.pub` 是否属于同一把密钥；导出私钥前会明确警示，Unix 平台写出时使用受限权限。
  - 审计日志记录连接配置、设置和共享凭据的变更，并支持按时间筛选、CSV 导出和打开对应配置。
  - 变更：`70f69c9d`、`b2e32d45`、`7ccf5053` · [PR #670](https://github.com/joesdu/VelaShell/pull/670)

## 🐛 修复与稳定性

- **插件传输并发状态修复**：批准、排队、传输和取消状态现在在同一套锁保护下推进，避免已批准但尚未开始的任务被错误地当作待审批任务撤销。
  - 任务在真正获得传输槽后进入运行状态；取消请求在开始传输前会再次检查，避免取消后仍传输数据。
  - 变更：`48c4c8d0` · [PR #674](https://github.com/joesdu/VelaShell/pull/674)

- **跨平台测试稳定性修复**：修正 macOS 监听器收尾、文件监视器延迟事件、断开时未观察的任务异常以及部分测试中的时序假设。
  - 变更：`f2f7da59`、`27819947` · [PR #676](https://github.com/joesdu/VelaShell/pull/676)

- **测试数据安全修复**：替换会触发 GitHub Secret Scanning 的伪 Google API Key，同时保持测试对凭据模式匹配和遮盖逻辑的覆盖。
  - 变更：`44fdbe63` · [PR #675](https://github.com/joesdu/VelaShell/pull/675)

- **密码提示与输入框体验修复**：补充 sudo-rs、doas 以及多语言密码提示识别，并修正部分搜索 / 过滤输入框的实际高度。
  - 变更：`8ca7eb9c` · [PR #671](https://github.com/joesdu/VelaShell/pull/671)

## ⚠️ 升级说明 / 不兼容变更

- macOS / Linux 用户首次运行本版本时，本机加密密钥会优先迁移到系统钥匙串或 Secret Service；系统密钥链不可用时会回落到本地密钥文件。请不要在迁移完成前删除旧数据目录。
- 自动下载更新默认关闭；需要自动下载新版本的用户，请在设置中重新开启。
- 网络检索默认关闭；需要使用该能力的用户，请在设置中手动开启。
- 布局模板同步默认开启；首次同步时会把本机已有布局模板纳入同步范围。
- 插件更新现在要求 HTTPS 下载地址和有效 SHA-256 摘要，旧的 HTTP 地址或缺少摘要的插件包无法安装 / 更新。
- 使用 OSC 133 命令块能力的用户需要重新复制新版 zsh 初始化片段。
- 除以上事项外，无需用户额外操作的不兼容变更。

## 📚 文档与生态

- 同步更新 SSH、SFTP、文件传输、隧道、插件通知、插件权限、系统密钥链和安全审计相关的中英文使用手册。
  - 变更：`62a4e480`、`9c974187`、`2f638121`、`0aad9336` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)、[PR #674](https://github.com/joesdu/VelaShell/pull/674)
- 宿主插件 SDK 依赖升级至当前发布基线，并对齐插件清单和传输拒绝契约。
  - 变更：`be025d75`、`013907b4`、`cc60955c` · [PR #672](https://github.com/joesdu/VelaShell/pull/672)、[PR #674](https://github.com/joesdu/VelaShell/pull/674)、[PR #676](https://github.com/joesdu/VelaShell/pull/676)

## ✅ 验证

- 完整解决方案 Debug 构建通过，`-warnaserror` 下 0 个警告、0 个错误。
- 宿主、插件、协议互操作、本地化和 Headless UI 测试通过。
- CI 已覆盖 Windows、Linux 和 macOS 相关分支；跨平台监听器、文件监视器、SSH / SFTP、插件传输和发布检查均已补充对应测试。
- 需要 Docker、特定 SSH 靶机或额外环境变量的集成测试，按 CI 环境规则执行；未满足条件的测试会明确标记为跳过，不计入实际通过项。

## 🔗 相关变更

- [PR #674：合并 SDK 与 SFTP 功能的 CI 修复](https://github.com/joesdu/VelaShell/pull/674)
- [PR #672：feature-plan 一轮功能落地](https://github.com/joesdu/VelaShell/pull/672)
- [PR #671：共享密码提示与输入框体验改进](https://github.com/joesdu/VelaShell/pull/671)
- [PR #670：标签栏、布局、传输、SSH、隧道与文件管理增强](https://github.com/joesdu/VelaShell/pull/670)
- [PR #669：共享密码填入体验改进](https://github.com/joesdu/VelaShell/pull/669)
- [PR #668：AI 传输、遮盖、更新流程与 CI 稳定性修复](https://github.com/joesdu/VelaShell/pull/668)
- [PR #666：依赖升级](https://github.com/joesdu/VelaShell/pull/666)
- [PR #665：依赖更新](https://github.com/joesdu/VelaShell/pull/665)
- [PR #675：替换测试数据中的伪 Google API Key](https://github.com/joesdu/VelaShell/pull/675)
- [PR #676：合并 SDK 与 SFTP 功能的 CI 修复](https://github.com/joesdu/VelaShell/pull/676)

**Full Changelog**: https://github.com/joesdu/VelaShell/compare/1.7.3...1.7.4
