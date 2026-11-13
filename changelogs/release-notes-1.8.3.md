本次更新聚焦插件免重启更新、插件管理窗口重排与 Linux 钥匙环可靠性，同时修复 `make menuconfig` 等 ncurses 界面的画面错位，为经常更新插件、在 Ubuntu 上保存连接密码和在终端里跑全屏 TUI 的用户带来更省心的日常体验。

## ✨ 重点更新

- **打开过的插件无需重启即可更新**：此前进程内插件（如远程桌面 `velashell.rdp`）用过一次后，更新会报 `Access to the path '…\plugins\velashell.rdp' is denied`，必须重启 VelaShell；现在可以直接更新或卸载。
  - 用户安装的进程内插件改为从数据目录下 `plugin-shadow` 中的副本装载，安装目录不再被占用；副本在插件卸载后自动删除，未能删除的会在下次启动时清理。
  - 更新时如果旧版目录仍被占用（例如杀毒软件短暂扫描），会先自动重试约两秒；仍失败时保留并继续使用旧版，并提示文件被占用，插件不再从管理页和连接页消失。
  - 隔离模式插件和应用自带插件的装载方式不变。插件自身泄漏的内存仍要到重启后才回收。
  - 变更：`e0ffea73`、`a2584dcf` · [PR #746](https://github.com/joesdu/VelaShell/pull/746)

- **插件管理窗口重排**：常用操作一眼可见，次要操作收进菜单，插件多时可以筛选。
  - 「检查更新」「安装 .vpx…」移到标题栏右侧的图标按钮；「已安装」页新增筛选框（按名称、id、作者匹配）、「共 N 个插件 · M 个运行中 · K 个可更新」概况和「全部更新」按钮。
  - 插件卡片显示首字图标、「进程内 / 隔离」模式标签（悬停查看区别）和「敏感权限」标签；发布者指纹中间省略，完整值在悬停提示中。
  - 行内只保留「更新到 x.y.z」、重新加载（开发期插件）和启用 / 禁用；查看日志、静音通知、撤销权限、卸载移入「…」菜单。状态栏只在有提示时出现，失败标红，可点 × 收起。
  - 变更：`b52e4d27` · [PR #747](https://github.com/joesdu/VelaShell/pull/747)

## 🖥️ 终端与连接

- **支持 REP 重复字符序列，`menuconfig` 不再画歪**：此前在 OpenWrt `make menuconfig` 等 ncurses 界面中，横线只剩一格、对话框边框缩短、右侧内容整体左移；现在显示与 Windows Terminal 一致。
  - 默认 `TERM=xterm-256color` 下 ncurses 会把连续相同字符压缩为 `CSI Ps b`，此前该序列被忽略；现在按当前颜色与样式正确重复，线框字符、宽字符和行尾换行的表现与正常输出一致。
  - 变更：`79fb2b1c` · [PR #752](https://github.com/joesdu/VelaShell/pull/752)（关闭 [#749](https://github.com/joesdu/VelaShell/issues/749)）

## 🔐 安全与隐私

- **Linux 钥匙环锁定或只在内存中时，不再每次重启都要重输密码**：此前在 Ubuntu 上重启后，已勾选「记住密码」的连接会提示「加密它的本机密钥不在这里」。
  - 钥匙环处于锁定状态（例如自动登录后登录钥匙环未解锁）时，VelaShell 会识别为「暂时用不了」，不再另生成一把新密钥顶替；解锁钥匙环后重启即可照常取回已保存的密码。
  - 默认钥匙环是 gnome-keyring 的 session 集合（只保存在内存中，重启即清空）时，本机密钥改存到 `secret.key`（权限 0600），重启后不再丢失。
  - 如果你此前正处于 session 集合的情况，旧密钥已无法找回，升级后需要再输入一次密码。
  - 变更：`d302fd1d` · [PR #751](https://github.com/joesdu/VelaShell/pull/751)（关闭 [#750](https://github.com/joesdu/VelaShell/issues/750)）

## 🐛 修复与稳定性

- **停用进程内插件后内存可以正常回收**：此前插件控件渲染过之后，停用插件时其程序集无法卸载，日志报 `load context is still alive`；现在卸载前会清理 Avalonia 以插件类型为键的属性缓存，退出程序时跳过此步骤。
  - 插件自己登记的 Avalonia 属性需由插件自行处理：远程桌面插件已在 [velashell-plugins PR #156](https://github.com/VelaShellLabs/velashell-plugins/pull/156) 修复，Docker 面板与 MongoDB 插件尚未处理。
  - 变更：`c40f0067` · [PR #748](https://github.com/joesdu/VelaShell/pull/748)

## ⚠️ 升级说明 / 不兼容变更

- Linux 用户若默认钥匙环为 gnome-keyring 的 session 集合，升级后需要为已保存密码的连接重新输入一次密码，之后重启不再丢失。
- CI 与发布流水线改用 .NET 11 正式版 SDK 构建（不再使用 preview 通道）。变更：`d8c883b1` · [PR #753](https://github.com/joesdu/VelaShell/pull/753)

## 📚 文档与生态

- 中英文用户手册同步插件管理窗口的新布局与筛选，以及 Linux 钥匙环的原理与排障说明。变更：`b52e4d27` · [PR #747](https://github.com/joesdu/VelaShell/pull/747)；`d302fd1d` · [PR #751](https://github.com/joesdu/VelaShell/pull/751)
- `DESIGN.md` 新增插件卡片（Plugin card）组件规范。变更：`b52e4d27` · [PR #747](https://github.com/joesdu/VelaShell/pull/747)
- velashell-docs 同步：影子副本装载（SDK 参考、开发指南、打包与分发）[velashell-docs PR #215](https://github.com/VelaShellLabs/velashell-docs/pull/215)；插件管理窗口交互规格 [velashell-docs PR #216](https://github.com/VelaShellLabs/velashell-docs/pull/216)；插件卸载与共享状态（开发指南）[velashell-docs PR #217](https://github.com/VelaShellLabs/velashell-docs/pull/217)；系统密钥链调研 [velashell-docs PR #218](https://github.com/VelaShellLabs/velashell-docs/pull/218)。
- 远程桌面插件修复自身登记的 Avalonia 属性，配合本版实现停用后回收：[velashell-plugins PR #156](https://github.com/VelaShellLabs/velashell-plugins/pull/156)。

## ✅ 验证

- [三平台 CI #43379360646](https://github.com/joesdu/VelaShell/actions/runs/43379360646)（对应提交 `dd6ec64d`）：Windows、macOS 首次运行通过；Ubuntu 首次运行中 `SudoPromptDialogUiTests.RootConfirmation_ApproveStaysDisabledBriefly_SoAClickAtPopUpNeverApproves` 一条计时类 Headless UI 用例失败，与本版改动无关，重跑失败作业后通过（`VelaShell.Tests` 3453 通过、39 跳过、0 失败）。[CodeQL #43379361273](https://github.com/joesdu/VelaShell/actions/runs/43379361273) 通过。
- CI 按既有规则排除 `DockerIntegration`、`CrossPlatform`、`Interop` 类别，部分依赖平台或外部环境的用例跳过；跳过不计为通过。
- 各 PR 合并前的本机验证（Windows / .NET 11，Debug）：
  - #746、#747、#748：`dotnet build VelaShell.slnx -c Debug -warnaserror` 通过。#746 插件相关 541 条中 517 通过、24 跳过；#748 `VelaShell.Infrastructure.Tests` 1407 通过、33 跳过，`VelaShell.Tests` 3477 通过、24 跳过；均为 0 失败。
  - #747：`VelaShell.Tests` 3481 通过、24 跳过、0 失败，新增 `PluginManagerWindowLayoutTests`，并在 980 与 740 两种窗口宽度下人工核对布局。
  - #751：`MasterKeyResolverTests` 与主密码相关用例 89 通过、1 跳过（`SystemKeychain_RoundTrip` 需 `VELASHELL_KEYCHAIN_TESTS=1`）。
  - #752：`VelaShell.Terminal.Tests` 658 条全部通过，含新增 `RepeatCharacterTests` 10 条。
- 未完成的人工验收：新增的 libsecret 调用未在 Linux 真机钥匙环上运行；`menuconfig` 修复未在真机上复现验证，依据 issue 截图与 ncurses 输出形状推断。

## 🔗 相关变更

- [PR #746：已安装的进程内插件从影子副本装载，打开过的插件无需重启即可更新](https://github.com/joesdu/VelaShell/pull/746) — `e0ffea73`、`a2584dcf`
- [PR #747：插件管理窗口重排，主操作突出、次要操作收进菜单、可筛选](https://github.com/joesdu/VelaShell/pull/747) — `b52e4d27`
- [PR #748：进程内插件卸载前清掉 Avalonia 按插件类型的缓存](https://github.com/joesdu/VelaShell/pull/748) — `c40f0067`
- [PR #751：Linux 钥匙环锁着与 session 集合不再报成「本机密钥不在」](https://github.com/joesdu/VelaShell/pull/751) — `d302fd1d`
- [PR #752：支持 REP（CSI Ps b），menuconfig 框线不再只剩一格、内容不再错位](https://github.com/joesdu/VelaShell/pull/752) — `79fb2b1c`
- [PR #753：CI 改用 .NET 11 正式版](https://github.com/joesdu/VelaShell/pull/753) — `d8c883b1`
- 完整差异：[1.8.2...1.8.3](https://github.com/joesdu/VelaShell/compare/1.8.2...1.8.3)
