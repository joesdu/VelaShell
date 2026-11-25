本次更新聚焦插件出站连接跟随网络代理、网络代理默认改为直连与发行包瘦身，同时让 AI 插件可以按任务挑选模型、修复聊天框中文拼音不显示，为需要经代理连接远程桌面 / Telnet 的用户、使用 AI 助手的用户和下载安装包的所有用户带来更可控、更轻量的体验。

## ✨ 重点更新

- **RDP 与 Telnet 插件的连接跟随网络代理**：在「设置 → 网络代理」中配置的代理，现在同样作用于远程桌面（含 RD Gateway）与 Telnet 插件的连接，不再只对 SSH 生效。
  - 插件 SDK 新增出站网络能力 `INetworkApi`（`IPluginContext.Network`），插件的出站 TCP 由宿主按当前代理设置拨号，支持直连、HTTP CONNECT 与 SOCKS5，与 SSH 使用同一套代理解析。
  - 经代理连接时远程桌面只走 TCP（不使用会绕过代理的 UDP 通道）；选择了代理但代理不可用时直接报错，提示文案与 SSH 代理报错一致，**绝不悄悄退回直连**。
  - 限制：Redis / MongoDB 插件暂不经此代理；隔离进程模式运行的插件暂不支持该能力。连接建立超时限定在 1 秒至 2 分钟之间。
  - 变更：`dd34b0b3`、`2f673ec5`、`39bebf5d` · [PR #757](https://github.com/joesdu/VelaShell/pull/757)

- **发行包瘦身约 11 MiB**：Windows x64 压缩包由 118.0 MiB 降到 106.9 MiB（解压后 292 MB → 257.5 MB），启动首帧耗时不变（中位数 975.7 ms 对比 971.4 ms）。
  - 发行包不再携带原生调试符号（`.pdb` / `.dbg` / `.dwarf`）；各插件只保留当前平台所需的原生库。
  - AI 插件不再重复携带宿主已有的 Markdig、TextMateSharp 等库，运行时直接复用宿主那份。
  - 启动时用不到的程序集（如 BouncyCastle、FluentFTP 及其他操作系统的界面后端）不再预编译：每次启动后第一次 SSH 握手约多 50 ms，启用主密码的用户解锁约多 116 ms，换来冷启动时杀毒软件扫描时间减少约 200 ms。
  - macOS / Linux 上上述排除项的实际效果以本版发布流水线构建结果为准。
  - 变更：`c396902d` · [PR #758](https://github.com/joesdu/VelaShell/pull/758)

## 🤖 AI 助手

- **按任务选择模型，生图可逐次指定模型**：主模型可以把独立的子任务委派给当前供应商下的其他聊天模型；生成与编辑图片时可以为每次请求指定模型，方便用同一提示词比较不同模型的效果。
  - 默认图片模型改为可选，可保留默认模型或交给 AI 自动选择；逐次选模不会改动聊天模型或全局默认设置。
  - 子任务只能委派给当前供应商实例下的聊天模型，跨供应商、图片模型或无效模型 ID 会被拒绝；审批与读写权限范围不变，图片工具仍保留每轮次数上限与失败即停。
  - IM 与对外 MCP 场景中的子代理同样只限当前供应商，且只处理聊天任务。
  - 变更：`9c29eee6` · [PR #755](https://github.com/joesdu/VelaShell/pull/755)

- **聊天输入框输入中文时能看到未确认的拼音**：此前用拼音输入法在 AI 聊天框打字时只出现候选窗、输入框里看不到正在输入的拼音。
  - 现在拼音以主题化样式显示在光标处（深浅主题均可见），确认后才写入正文；组字期间按回车不会误发送，也不会触发历史回溯或删除文件引用，取消或失焦后自动清除。
  - 变更：`9c29eee6`、`8b93d2cf` · [PR #755](https://github.com/joesdu/VelaShell/pull/755)、[PR #756](https://github.com/joesdu/VelaShell/pull/756)

## 🌐 网络与连接

- **网络代理默认改为「不使用代理」**：新安装的 VelaShell 默认直连，不再隐式跟随系统代理。
  - 配置中已保存的代理类型（无论是「不使用代理」还是「系统代理」）升级后原样保留；升级迁移也不再把已保存的「不使用代理」改成「系统代理」。
  - 需要走系统代理时，到「设置 → 网络代理」中选择「系统代理」即可。
  - 变更：`ac3b7473`、`b2dedb1b` · [PR #756](https://github.com/joesdu/VelaShell/pull/756)

## ⚠️ 升级说明 / 不兼容变更

- 网络代理的默认值由「系统代理」改为「不使用代理」。配置里没有保存代理类型、此前依赖默认跟随系统代理的用户，升级后请在「设置 → 网络代理」中确认是否需要改回「系统代理」。变更：`ac3b7473` · [PR #756](https://github.com/joesdu/VelaShell/pull/756)
- 宿主内置的插件 SDK 升级到 `VelaShell.PluginSdk` 2.2.0（新增 `INetworkApi`），AI 插件的 `minSdkVersion` 同步为 2.2.0。依赖出站网络能力的插件需要 1.8.4 及以上版本的宿主。变更：`8b93d2cf`、`39bebf5d` · [PR #756](https://github.com/joesdu/VelaShell/pull/756)、[PR #757](https://github.com/joesdu/VelaShell/pull/757)
- 依赖更新：ReactiveUI 26.0.1 → 27.0.0，LiveMarkdown.Avalonia 2.6.1 → 2.7.0。变更：`8b93d2cf` · [PR #756](https://github.com/joesdu/VelaShell/pull/756)

## 📚 文档与生态

- 中英文用户手册同步：网络代理默认值改为直连；RDP（含 RD Gateway）与 Telnet 插件连接跟随网络代理、经代理时 RDP 只走 TCP，Redis / MongoDB 暂不经此代理；AI 插件按任务选择模型与图片模型选择。变更：`ac3b7473`、`2f673ec5`、`9c29eee6` · [PR #755](https://github.com/joesdu/VelaShell/pull/755)、[PR #756](https://github.com/joesdu/VelaShell/pull/756)、[PR #757](https://github.com/joesdu/VelaShell/pull/757)
- 中英文 README 重新整理。变更：`8b93d2cf` · [PR #756](https://github.com/joesdu/VelaShell/pull/756)
- `plugins/README.md` 补充插件与宿主共享依赖（`VelaHostSharedPackage`）、按平台保留原生库的约定。变更：`c396902d` · [PR #758](https://github.com/joesdu/VelaShell/pull/758)
- velashell-docs 同步：AI 模型路由、图片模型选择与中文预编辑 [velashell-docs PR #225](https://github.com/VelaShell/velashell-docs/pull/225)；代理默认直连与 RD Gateway 代理行为 [velashell-docs PR #226](https://github.com/VelaShell/velashell-docs/pull/226)；`INetworkApi`（SDK 2.2.0）与 RDP 代理行为 [velashell-docs PR #228](https://github.com/VelaShell/velashell-docs/pull/228)。

## ✅ 验证

- [三平台 CI #43779046836](https://github.com/joesdu/VelaShell/actions/runs/43779046836)（对应提交 `bdf459e1`）：Windows、Ubuntu、macOS 构建与测试全部通过，0 失败；其中 `VelaShell.Tests` 在 Windows / Ubuntu / macOS 上分别 3484 / 3453 / 3474 通过，8 / 39 / 18 跳过。[CodeQL #43779047083](https://github.com/joesdu/VelaShell/actions/runs/43779047083) 通过。
- CI 按既有规则排除 `DockerIntegration`、`CrossPlatform`、`Interop` 类别，部分依赖平台或外部环境的用例跳过；跳过不计为通过。
- 各 PR 合并前的本机验证（Windows / .NET 11，Debug）：
  - #755：图片、子代理、配置、设置界面与本地化相关 353 项测试通过；输入框相关 24 项通过（含 6 项新增 IME 用例）；Skia headless 像素验证深浅主题下拼音可见。
  - #756：`VelaShell.Core.Tests`（ProxyDefaults / SettingsTransfer）21 通过；`VelaShell.Tests`（SettingsViewModel / ProxySettingsHotApply）75 通过、4 跳过。
  - #757：`dotnet build VelaShell.slnx -c Debug` 通过；`VelaShell.Infrastructure.Tests` 相关 47 项通过（含新增 `NetworkCapabilityTests` 6 项）。
  - #758：win-x64 实际发布并测量体积与启动首帧；osx-arm64 / linux-x64 交叉发布后确认只剩目标平台原生库且无调试符号文件；AI 插件测试 2199 项全部通过。
- 未完成的人工验收：AI 功能未调用真实付费模型，未在真实 Windows 输入法下人工验收候选窗；RDP / Telnet 经代理连接未在真实代理环境下人工验收；macOS / Linux 的预编译排除效果待本版发布流水线确认。

## 🔗 相关变更

- [PR #755：AI 插件支持按任务选择模型并修复中文拼音显示](https://github.com/joesdu/VelaShell/pull/755) — `9c29eee6`
- [PR #756：网络代理默认改为不使用代理](https://github.com/joesdu/VelaShell/pull/756) — `ac3b7473`、`b2dedb1b`、`8b93d2cf`
- [PR #757：实现插件出站网络能力（INetworkApi）](https://github.com/joesdu/VelaShell/pull/757) — `dd34b0b3`、`2f673ec5`、`39bebf5d`
- [PR #758：发行包瘦身（调试符号、插件原生库、共享依赖、按程序集排除 R2R）](https://github.com/joesdu/VelaShell/pull/758) — `c396902d`
- 完整差异：[1.8.3...1.8.4](https://github.com/joesdu/VelaShell/compare/1.8.3...1.8.4)
