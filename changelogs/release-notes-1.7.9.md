本次更新聚焦插件商店集成、插件管理体验与日志阅读，让插件的发现、安装、更新和故障排查可以在 VelaShell 内完成。

## ✨ 重点更新

- **在应用内浏览与安装插件**：插件管理新增「已安装」与「插件商店」两个页签，无需先去浏览器下载插件包。
  - 商店支持关键词搜索、按最近更新 / 下载量 / 评分排序及分页；选择条目后，右侧展示描述、作者、许可证、最新稳定版与兼容性。只有点击「安装」或「更新到 …」后才下载，并显示进度、允许取消下载。
  - 不兼容、已是最新版、随应用提供或开发挂载的插件不会通过商店被覆盖；商店只选择有效的稳定版本。加载失败时可以重试，仍保留本地 `.vpx` 安装入口及浏览器详情入口。
  - 变更：`b9425b56` · [PR #714](https://github.com/joesdu/VelaShell/pull/714)

- **插件日志按状态高亮**：错误、警告、成功、信息等中英文关键词使用不同颜色，排查异常更直观。
  - 在「已安装」页点击插件的「查看日志」，只读查看该插件日志的最后 256 KB；可刷新、打开日志文件夹，按 Esc 关闭窗口。高亮随当前主题变化，链接颜色也适配深色主题。
  - 高亮失败时退回纯文本，不影响查看日志；日志收集范围与轮转规则保持原有行为。
  - 变更：`484cf84b` · [PR #717](https://github.com/joesdu/VelaShell/pull/717)

## 🖥️ 插件管理

- **列表、详情与操作位置更清晰**：优化商店的左右分栏，将详情操作放在右侧，收紧页签尺寸，并调整已安装插件的操作行、日志入口与卸载按钮位置。
  - 修正尚未选中插件时的空详情绑定与按钮悬停状态，使空白页和条目切换时的展示更稳定。
  - 变更：`d0fe5f81` · [PR #716](https://github.com/joesdu/VelaShell/pull/716)

- **下载可取消，批量更新可停止后续下载**：商店下载使用独立的临时包，失败或取消后清理；已开始的安装事务不会被下载取消按钮强行中断。
  - 切换商店条目不会改变已经选定的下载与安装目标；「全部更新」取消后不再开始后续下载。
  - 变更：`b9425b56` · [PR #714](https://github.com/joesdu/VelaShell/pull/714)

## 🔐 安全与隐私

- **安装前核对实际插件身份与兼容性**：商店下载继续要求 HTTPS 和 SHA-256 摘要，安装前还会核对包内插件 ID、版本与宿主兼容性，避免商店选中的条目与实际包不一致时覆盖已有插件。
  - 签名校验、陌生或未签名发布者的显式确认、发布者变更确认继续生效。插件以当前用户权限运行，安装确认不代表插件受到完整的本机代码沙箱限制。
  - 变更：`b9425b56` · [PR #714](https://github.com/joesdu/VelaShell/pull/714)

- **商店联网行为明确可见**：切换到商店页才加载目录；搜索、排序和翻页会发送关键词、排序方式及页码，选择条目（含自动选中的首项）会请求对应插件详情。
  - 请求不带账号、令牌或安装标识符，服务端可看到 IP 地址；不会在启动时或后台定时浏览商店。描述不加载远程图片，HTTPS 链接仅在点击后由浏览器打开。
  - 变更：`b9425b56` · [PR #714](https://github.com/joesdu/VelaShell/pull/714)

## 🐛 修复与稳定性

- **适配新版 Anthropic SDK 的流式错误类型**：保留 AI 对过载、服务端错误、限流和超时的重试及故障转移判断，同时改用 SDK 当前支持的错误属性，修复依赖升级后严格构建失败的问题。
  - 使用真实 SSE 错误体覆盖已有分类，并补充未知及空错误类型的回归验证；不改变供应商、凭据与故障转移配置。
  - 变更：`2bb1a91f` · [PR #719](https://github.com/joesdu/VelaShell/pull/719)

## ⚠️ 升级说明 / 不兼容变更

- 无需要用户额外操作的不兼容变更。
- 商店安装与更新仍受宿主 / SDK / 插件 API 兼容性检查限制；开发挂载的插件继续通过开发入口管理。

## 📚 文档与生态

- 中英文用户手册补充应用内插件商店、下载取消、安装校验及日志高亮操作；隐私说明同步列出商店浏览和下载发送的数据。
  - 变更：`b9425b56` · [PR #714](https://github.com/joesdu/VelaShell/pull/714)；`484cf84b` · [PR #717](https://github.com/joesdu/VelaShell/pull/717)
- 更新运行依赖：SonnetDB.Core 5.0.0、BouncyCastle.Cryptography 2.8.0-beta.349、Anthropic 13.5.0、ModelContextProtocol.Core 2.3.1；这些是随应用或 AI 插件分发的依赖，不要求用户单独安装。
  - 变更：`e0f2a303` · [PR #718](https://github.com/joesdu/VelaShell/pull/718)

## ✅ 验证

- 本机 Windows 执行 `dotnet build VelaShell.slnx -c Debug --nologo -warnaserror`：通过，0 警告、0 错误。
- 执行 `dotnet test tests\VelaShell.Plugin.Ai.Tests\VelaShell.Plugin.Ai.Tests.csproj -c Debug --filter FullyQualifiedName~ChatFormattingTests --no-restore --verbosity minimal -warnaserror`：10 通过、0 失败、0 跳过。
- [发布修复 CI #41959552440](https://github.com/joesdu/VelaShell/actions/runs/41959552440)：Windows、Linux、macOS 的 Debug 构建与回归测试全部通过；Linux 额外的 `runtime-async=off` 编译检查通过。此运行对应 `2bb1a91f`，后续发布准备仅新增本发布说明。
- CI 按既有规则排除 `DockerIntegration`、`CrossPlatform`、`Interop` 类别，部分依赖平台或外部环境的用例跳过；跳过不计为通过。本次发布准备未手工执行真实插件商店安装、外部 AI 服务调用或 Docker / SSH 互操作验证。

## 🔗 相关变更

- [PR #714：在插件管理中集成插件商店](https://github.com/joesdu/VelaShell/pull/714) — `b9425b56`
- [PR #716：优化插件管理布局与操作](https://github.com/joesdu/VelaShell/pull/716) — `d0fe5f81`
- [PR #717：改进插件日志高亮](https://github.com/joesdu/VelaShell/pull/717) — `484cf84b`
- [PR #718：升级依赖包](https://github.com/joesdu/VelaShell/pull/718) — `e0f2a303`
- [PR #719：修复发布构建并准备 1.7.9](https://github.com/joesdu/VelaShell/pull/719) — `2bb1a91f`
- 完整差异：[1.7.8...1.7.9](https://github.com/joesdu/VelaShell/compare/1.7.8...1.7.9)
