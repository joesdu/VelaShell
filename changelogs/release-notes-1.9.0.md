本次更新聚焦 SSH 安全与传输、AI 远程编辑审阅、路由可视化与资源管理器响应速度，为需要管理多种远程连接和审阅远程文件的用户带来更清晰的网络状态、更稳健的连接保护和更高效的操作反馈。

## ✨ 重点更新

- **交互式路由追踪地球仪**：把已定位的路由跃点放到可拖动的点阵地球仪上，连续采样时逐步绘制空中弧线。
  - 可在路由追踪窗口中固定视角查看路线，工具栏可切换自动旋转；地图会跟随当前窗口主题和强调色。
  - 相邻跃点坐标相同时不绘制零距离弧线；会话树缩进改为使用节点自身深度，根节点和分组加载不再出现错误缩进。
  - 变更：`cb9440d6` · [PR #812](https://github.com/joesdu/VelaShell/pull/812)

- **VelaShell Copilot 体验统一**：统一 AI 助手在五种界面语言中的产品名称，并修复 Debug 宿主无法注册助手入口的问题。
  - 标题栏、聊天窗口、命令面板、回复署名和协作欢迎语统一显示为 VelaShell Copilot；模型、OAuth、MCP 和协作渠道请求带上插件版本 User-Agent，同时保留用户显式配置的值。
  - Debug 宿主版本与最低宿主版本检查保持一致，内置助手可以正常注册聊天命令。
  - 变更：`d8a7826c` · [PR #813](https://github.com/joesdu/VelaShell/pull/813)

## 🖥️ 终端、SSH 与网络

### SSH 连接保护与传输

- **按方向选择终端压缩**：默认关闭 SSH 终端压缩，也可按连接选择认证后仅压缩输入，或在确认服务器已有等效保护后启用双向压缩。
  - 直连和跳板连接在拨号前检查主机密钥验证服务；缺少严格密钥交换时要求双向 AES-GCM，以避免在未满足保护条件时继续连接。
  - SFTP 使用独立连接时不受终端压缩设置影响；需要兼容旧服务时，请根据服务端能力选择连接级选项。
  - 变更：`095eb658` · [PR #815](https://github.com/joesdu/VelaShell/pull/815)

- **代理、密钥与 SFTP 稳定性增强**：HTTPS 代理在建立 CONNECT 隧道前验证证书和主机名，FTP/FTPS 经过 HTTPS 代理时会在拨号前明确报告不支持。
  - 密码套件异常路径会清零敏感缓冲，密钥和 Xauthority 文件读取上限为 1 MiB；SSH 配置 Include 的路径参数和目录项检查上限为 4,096。
  - 对 FSTAT 已确认长度未知的普通 SFTP 文件启用受 8 个请求和 1 MiB 限制的并行预读，特殊文件和类型未知的句柄继续串行读取。
  - 变更：`bee93546` · [PR #816](https://github.com/joesdu/VelaShell/pull/816)

- **状态栏显示丢包率**：在连接延迟旁显示最近最多 20 次探测中未收到应答的比例。
  - 每个终端标签独立统计，重连后重新计数；该指标复用现有探测，不会增加网络请求。SSH 基于 TCP，显示的丢包率不等同于底层链路的原始丢包率。
  - 变更：`ae290ed2` · [PR #822](https://github.com/joesdu/VelaShell/pull/822)

### XServer 与剪贴板隔离

- **隔离 X 会话剪贴板并强化 Unix 授权**：剪贴板应答和 INCR 数据块绑定到当前选区属主，焦点或属主改变时取消未完成传输。
  - 共享显示按 SSH 连接隔离剪贴板作用域；同一连接内的多个 X 程序仍可共享，连接到同一目标的独立会话各自隔离。
  - Unix 套接字优先校验显式凭据并保留已签发授权的信任级别和撤销行为；XRes `QueryClientIds` 在请求长度不匹配时返回 `BadLength`。
  - 变更：`7ef6a943` · [PR #817](https://github.com/joesdu/VelaShell/pull/817)

## 🗂️ 资源管理器与远程编辑

### 更快的会话树加载

- **资源管理器优先加载会话树**：启动时在同一把锁内读取分组和会话，命令面板复用这份数据，减少重复扫描和凭据解密。
  - 会话树加载期间显示异步动画；过滤框和最近连接区仍可使用，列表就绪后立即显示连接。
  - 变更：`108485ed` · [PR #819](https://github.com/joesdu/VelaShell/pull/819)

### 远程编辑器与 AI 差异审阅

- **更容易定位远程文件改动**：SFTP 内置编辑器高亮当前行、在状态栏显示光标行列，并在标题栏提供自动换行开关。
  - AI 并排和内联差异视图标出替换行中的字符级变化；点击 `␠` 可忽略行首行尾空白进行比较，但保存时仍写入完整草稿。
  - 工具卡片和内联标签支持按段展开上下文，展开后跳转位置会同步更新；并排 C# 编辑器支持代码块缩进，停止输入 300 毫秒后在后台比较文档快照。
  - 变更：`39b42c68` · [PR #820](https://github.com/joesdu/VelaShell/pull/820)

## 🔐 安全与隐私

- **强化 SSH 和代理边界**：连接建立前验证主机密钥服务和 HTTPS 代理证书，限制 Include、密钥及 Xauthority 输入规模，并在异常路径清理敏感缓冲。
  - 这些检查默认生效；超过限制或不满足验证条件的连接会失败，而不会静默降级到较弱路径。
  - 变更：`095eb658`、`bee93546` · [PR #815](https://github.com/joesdu/VelaShell/pull/815) · [PR #816](https://github.com/joesdu/VelaShell/pull/816)

- **收紧 XServer 会话隔离**：剪贴板传输绑定到选区属主和 SSH 连接作用域，Unix 授权继续遵守显式凭据、信任级别与撤销状态。
  - 变更：`7ef6a943` · [PR #817](https://github.com/joesdu/VelaShell/pull/817)

## 🐛 修复与稳定性

- **修复路由追踪和会话树显示问题**：路由追踪可保留用户视角并增量显示跃点，会话树根节点不再因空父分组产生错误缩进。
  - 变更：`cb9440d6` · [PR #812](https://github.com/joesdu/VelaShell/pull/812)

- **修复 AI 助手入口和远程编辑审阅问题**：Debug 宿主可以通过最低版本检查并注册聊天命令，差异视图在展开上下文、切换视图、保存期间继续编辑和主题切换时保持正确状态。
  - 变更：`d8a7826c`、`39b42c68` · [PR #813](https://github.com/joesdu/VelaShell/pull/813) · [PR #820](https://github.com/joesdu/VelaShell/pull/820)

- **收敛内部状态表示并降低热路径开销**：将远程编辑、布局模板同步、插件生命周期、会话健康、插件更新解析、主密码文件头和命令任务的内部结果统一为互斥状态模型，同时保留公开插件描述、会话健康记录、远程编辑状态和同步信封的兼容性。
  - 不改变协议和公开接口；终端桥接单项布局从 16 B 降至 8 B，突发场景下单项中位分配从 35.07 B 降至 19.08 B。
  - 变更：`c29538f5`、`f710d5fc`、`a74f7166` · [PR #821](https://github.com/joesdu/VelaShell/pull/821) · [PR #823](https://github.com/joesdu/VelaShell/pull/823) · [PR #824](https://github.com/joesdu/VelaShell/pull/824)

## ⚠️ 升级说明 / 不兼容变更

- SSH 终端压缩现在默认关闭；如需压缩，请在连接配置中按服务端能力选择仅输入压缩或双向压缩。缺少严格密钥交换的服务端还需要支持双向 AES-GCM。
- 没有需要用户迁移配置、重新导入会话或更新插件 SDK 的其他不兼容变更。

## 📚 文档与生态

- 中英文用户手册同步补充了 SSH 压缩与主机验证、代理限制、SFTP 读取、XServer 剪贴板隔离、路由追踪、资源管理器加载、状态栏丢包率和远程编辑审阅说明。
  - 变更：`095eb658`、`bee93546`、`7ef6a943`、`108485ed`、`39b42c68`、`ae290ed2` · [PR #815](https://github.com/joesdu/VelaShell/pull/815) · [PR #816](https://github.com/joesdu/VelaShell/pull/816) · [PR #817](https://github.com/joesdu/VelaShell/pull/817) · [PR #819](https://github.com/joesdu/VelaShell/pull/819) · [PR #820](https://github.com/joesdu/VelaShell/pull/820) · [PR #822](https://github.com/joesdu/VelaShell/pull/822)

- velashell-docs 已同步对应的快捷键、SSH 行为规格、宿主架构和 AI 指南：
  - [VelaShellLabs/velashell-docs#260](https://github.com/VelaShellLabs/velashell-docs/pull/260) · [#262](https://github.com/VelaShellLabs/velashell-docs/pull/262) · [#263](https://github.com/VelaShellLabs/velashell-docs/pull/263) · [#264](https://github.com/VelaShellLabs/velashell-docs/pull/264) · [#266](https://github.com/VelaShellLabs/velashell-docs/pull/266)

- 统一组织链接和仓库元数据，后续生态入口使用 VelaShell 组织名称。
  - 变更：`d9d85a86` · [PR #818](https://github.com/joesdu/VelaShell/pull/818)

## ✅ 验证

- `dotnet build VelaShell.slnx -c Debug --no-restore`：PR #821、#823、#824 已验证通过，均为 0 警告、0 错误；PR #822 的常规构建也为 0 警告、0 错误。
- SSH：PR #815 验证通过 1,553 项 SSH 协议测试和 519 项宿主 SSH 回归测试，并使用 OpenSSH 10.3p1 完成压缩模式与服务端主动换钥互操作；PR #816 通过 1,574 项 SSH 非互操作测试、100 项宿主测试和 29 项本地化测试。
- 宿主与插件：PR #812 路由追踪 UI、会话树菜单 UI 等定向测试 23 项通过；PR #813 请求头和传输测试 115 项、产品标识测试 90 项通过；PR #819 资源树、侧栏及主窗口测试 191 项、SonnetDB 持久化测试 45 项通过；PR #820 远程编辑器测试 6 项、AI 差异与 TextMate 测试 50 项通过；PR #822 状态栏和丢包率测试 16 项通过；PR #824 重点测试 214 项通过、1 项跳过。
- XServer：PR #817 Windows 回归 538 项通过、9 项跳过，宿主测试 33 项通过；Linux 专项测试 53 项通过。Docker 到 Windows 的显示连接测试未通过环境条件，随后 Linux 隔离回环网络上的真实 xclip/xauth 验证通过。
- PR #821、#823 的完整 Debug 测试分别记录 7 项 `VelaShell.Plugin.Ai.Tests` 本地 LocalCommand 进程测试失败，原因是本机 `pwsh.exe` 启动、JSON/环境和取消行为，不在对应代码路径内；SSH 真实服务器互操作测试在 PR #816 中未运行。以上状态按原 PR 验证记录保留，未将环境跳过或已知失败写成通过。
- 发布前本地仅修改发布文档，未重复执行完整解决方案测试；GitHub Release 流水线将在发布事件中执行三平台构建、打包和附件校验。

## 🔗 相关变更

- [PR #812：添加交互式路由追踪地球仪并修复会话树缩进绑定](https://github.com/joesdu/VelaShell/pull/812)
- [PR #813：统一 VelaShell Copilot 产品标识并修复调试版助手入口](https://github.com/joesdu/VelaShell/pull/813)
- [PR #815：加固 SSH 握手与主机验证，支持按方向压缩终端流量](https://github.com/joesdu/VelaShell/pull/815)
- [PR #816：修复 SSH 代理降级与输入处理问题，优化 SFTP 预读](https://github.com/joesdu/VelaShell/pull/816)
- [PR #817：修复 XServer 剪贴板隔离、Unix 授权和 XRes 请求长度校验](https://github.com/joesdu/VelaShell/pull/817)
- [PR #819：优化资源管理器会话加载并显示异步加载动画](https://github.com/joesdu/VelaShell/pull/819)
- [PR #820：增强远程编辑器与 AI 代码差异审阅](https://github.com/joesdu/VelaShell/pull/820)
- [PR #821：使用 C# 15 union 建模互斥结果](https://github.com/joesdu/VelaShell/pull/821)
- [PR #822：在状态栏延迟旁显示连接丢包率](https://github.com/joesdu/VelaShell/pull/822)
- [PR #823：收敛状态机结果](https://github.com/joesdu/VelaShell/pull/823)
- [PR #824：收敛主密码文件头与命令任务的状态表示](https://github.com/joesdu/VelaShell/pull/824)
- [PR #825：更新 NuGet 包](https://github.com/joesdu/VelaShell/pull/825)
