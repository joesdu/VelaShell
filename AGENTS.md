# AGENTS.md

> 给 AI 代理与新加入者的操作约定。**动手之前先读完本文件,以及它指向的文档。**

## 一、开工前必读:velashell-docs

VelaShell 生态的**全部文档**集中在一个仓库:
[**VelaShellLabs/velashell-docs**](https://github.com/VelaShellLabs/velashell-docs)。
本仓库**不放** `docs/`、`docs-en/` —— 设计手册、开发规范与开发文档都在那边。

**在动任何代码之前**,先把下表中与你要改的部分相关的几篇读掉。跳过这一步直接改,
结果通常是两种:与既有设计冲突,或者重复实现一个已经存在的能力。


| 位置                                                                                        | 内容                                                                                                                                            |
| ----------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| [`zh/host/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host)           | 宿主分层架构与依赖方向、工程化重构蓝图、交互与界面规格、快捷键参考、设置项审计,以及 SFTP / FTP / Telnet / 串口 / Redis / S3 / 系统密钥链等可行性调研                                                |
| [`zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins)     | 插件系统设计蓝图 01–15(进程模型、IPC 协议、权限系统、UI 扩展、威胁模型、路线图)与[进度总览 STATUS](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md) |
| [`zh/sdk/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/sdk)             | 插件契约 SDK 参考、SDK 仓库的发版流程                                                                                                                       |
| [`zh/cli/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/cli)             | `vela-plugin` 命令行手册、CLI 仓库的发版流程                                                                                                               |
| [`zh/templates/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/templates) | 插件开发指南、打包与发布、模板仓库的发版流程                                                                                                                        |
| [`zh/ssh/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/ssh)             | SSH 库 `src/VelaShell.Ssh` 的架构与原理(含净室论证)、行为规格 00–09、上手                                                                                      |


英文镜像在 [`en/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en),与 `zh/` 同构。
[仓库首页](https://github.com/VelaShellLabs/velashell-docs)有按「我想做什么」组织的快速入口表。

## 二、涉及文档的改动一律同步到 velashell-docs

**这是本文件最重要的一条。**

- 本仓库里**不新建** `docs/`、`docs-en/` 或任何成体系的文档目录。要写文档,去 velashell-docs 开 PR。
- 改了代码,而**行为、接口、配置项、命令行、构建流程或版本纪律**与现有文档对不上时,
必须**同时**在 velashell-docs 提一个 PR 把文档改过来。两个 PR 在正文里互相引用,一起合。
只改代码不改文档,等于让文档开始骗人 —— 而文档是别人照抄的。
- velashell-docs 的 `zh/` 与 `en/` 是**互为镜像**的两棵树,文件一一对应。改了中文就要改英文,
反之亦然。漏一边,两棵树就开始漂。
- velashell-docs 内部的互相引用**一律走相对路径**(如 `../templates/dev-guide.md`),
不要写回 GitHub 绝对 URL —— 文档集中到一个仓库,消掉的正是那种一改路径就断的跨仓库链接。
- **例外**:留在代码仓库里的少数几份文件不适用上述规则,因为它们服务的是「在这个仓库里写代码」
这件事,搬走只会离使用场景更远。各仓库的例外清单见下面第三节。

## 三、本仓库:VelaShell(宿主主程序)

.NET 11 + Avalonia 12.1 的跨平台终端客户端,以及宿主侧的插件运行时。

### 构建与测试

```bash
dotnet build VelaShell.slnx
dotnet test  VelaShell.slnx
```

### 留在本仓库的文档(不搬去 velashell-docs)


| 文件                                                                               | 为什么留在这                                                                                                             |
| -------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| [`DESIGN.md`](DESIGN.md)                                                         | 设计系统的色彩/字体/间距令牌与组件规范。`ButtonThemes.axaml` 的注释与 `DialogButtonStyleTests` **按章节号直接引用它**,搬走等于把代码的参照物挪到另一个仓库           |
| [`plan.md`](plan.md)                                                             | **已经发生的事**:进展记录、当前架构、每次改动的来龙去脉。开发跟进以它为准                                                                            |
| [`feature-plan.md`](feature-plan.md)                                             | **还没发生的事**:待办、候选特性、确认不做的清单(附理由)。⚠️ 两者分工是硬的 —— 做完了就从 `feature-plan.md` 划掉、在 `plan.md` 补一节;**不要在 `plan.md` 里留 TODO** |
| `README.md` / `README.en.md` / `CONTRIBUTING*.md` / `SECURITY.md` / `PRIVACY.md` | GitHub 仓库门面与流程约定                                                                                                   |
| `manual.md` / `manual.en.md`                                                     | 写给用户的使用手册,跟着发布版本走。**改了用户看得见的行为,就在同一个提交里改手册,中英两份一起改**;设计与开发文档仍在 velashell-docs(维护者 2026-10-25 定) |
| [`changelogs/`](changelogs/) 下的 `release-notes-<版本号>.md`                     | 每个版本的完整 Release 正文,与 GitHub Release 保持一致并纳入版本控制,留在宿主仓库供发版与历史追溯使用                                  |
| `src/**/README.md`、`tests/**/README.md`                                          | 各工程自己的目录职责说明,跟着代码走                                                                                                 |
| `src/VelaShell.Ssh/AGENTS.md` / `LICENSE` / `NOTICE.md`                           | SSH 库专属的开发约定(净室规程)、它自己的 MIT 授权与独立性声明 —— 服务的是「改这个库」,且许可证必须跟着代码走                                                              |
| `src/VelaShell.XServer/AGENTS.md` / `LICENSE` / `NOTICE.md`                       | X11 服务端库的开发约定(同样的净室规程)、MIT 授权、独立性声明与内置字体的来源 —— 理由同上                                                                          |


改 UI 相关代码前先读 `DESIGN.md`:**XAML 与 C# 里不许出现颜色字面量**,一律用
`DynamicResource` 绑定令牌。

### 几条会让你踩坑的硬约束

- **原生绑定优先使用 `LibraryImport`**(包括测试和脚本)。新增或迁移绑定前逐项核对实际导出入口点、字符集与 ABI;
`LibraryImport` 不做 `DllImport` 的 A/W 后缀探测,Windows 字符串 API 必须显式指定真实的 `EntryPoint`(如 `GetFinalPathNameByHandleW`),
不得直接照搬没有后缀的托管方法名。保留各平台原有 UTF-16 / UTF-8 / ANSI 字符串语义、`bool` 宽度、调用约定、
数组与输出缓冲区长度(字节还是字符)、`out` / 结构体布局、`SetLastError` 以及句柄生命周期与所有权。
`StringBuilder` 等不受支持的形状先考虑等价的字符缓冲区或简洁封送声明;只有源生成器确实不能处理且无法简洁等价表达时才保留 `DllImport`,
并在绑定旁注释具体限制与保留原因,不能只写「兼容性需要」。迁移须构建验证并运行相关原生路径测试;编译通过不代表入口点或跨平台 ABI 已验证。
- **界面文案五份 resx 必须齐**(`Strings` / `zh-Hans` / `zh-Hant` / `ja` / `ko`),不许硬编码字符串。
漏译与孤儿键由 `LocalizedKeyUsageTests` / `UnusedLocalizedKeyTests` 拦住。
- **新增或改动快捷键先改目录**:唯一事实来源是
`src/VelaShell/ViewModels/ShortcutCatalog.cs`,设置页与文档都从它取数。没登记的话
`ShortcutCatalogTests` 会失败并打印出可粘贴的 Markdown 行。全局键位用户可以改键、解绑,
出厂表在 `src/VelaShell/Services/ShortcutKeymap.cs` 的 `ShortcutBindings`,主窗口按它登记 KeyBindings ——
**`MainWindow.axaml` 里不许写死 `KeyBinding`**(写死的那条用户改不掉),新的出厂键位也不许再占 `Ctrl+字母`
这类终端控制字符(#551),两条都有测试拦着。快捷键文档在
[`zh/host/快捷键参考.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/快捷键参考.md),改了要同步。
- **集成测试按环境早退跳过,而 MSTest 把跳过记为「通过」**。`DockerIntegration` 分类需要
Docker 与 `docker-compose.test.yml`,`CrossPlatformPublishTests` 需 `VELASHELL_PUBLISH_TESTS=1`。
要确认它们真跑过,看 `TestContext` 里有没有 `[SKIP]` 行 —— 全绿不等于跑过。
- **headless UI 测试要用带返回值的重载**:`Dispatch(async () => { …; return true; })`。
`HeadlessUnitTestSession` 没有 `Func<Task>` 重载,写成无返回值会拿到一个从未被等待的
`Task<Task>`,测试跑到第一个 `await` 就"通过",断言失败全部丢失。
**`async Task` 的测试方法别直接 `await _session.Dispatch(…)`**,用 `TestSupport/HeadlessUi.cs` 的
`RunOnUiAsync`:会话在 UI 线程上完成任务,`await` 的续体就地跑在 UI 线程上,MSTest 接着在那条线程上
跑下一条用例 —— 下一条若是 `Dispatch(…).GetAwaiter().GetResult()` 写法,就在 UI 线程上等 UI 线程,
整套卡死(只在两种写法按这个顺序相邻时出现)。这两种写法都由 `tests/VelaShell.Tests/Design/HeadlessDispatchUsageTests`
扫全部测试工程拦住,红了照它给的文件与行号改。
- **`Span` / `ReadOnlySpan` 与任何 ref struct(`SshDataReader` 之类)都不许跨 `await` 存活**,哪怕编译通过。
`Directory.Build.props` 开着 `runtime-async=on`,而这个模式下编译器**不报 CS4007**(.NET 11 RC2 的 Roslyn 5.12 实测):
`await` 真挂起之后,那个 Span 回来是空的(长度 0)、读取器回来是默认值;同步完成时又是对的,于是表现成偶发失败。
最容易踩的是**接收者先求值、实参里才 `await`** 的一行写法 —— `x.AsSpan().SequenceEqual(await …)`,连 `bytes.SequenceEqual(await …)`
也算(C# 14 起它绑到 Span 的重载),`await` 真挂起时恒为 `false`。先 `await` 拿到结果再造 Span;ref struct 的读取器在第一个 `await`
之前读完,或者 `await` 之后另起一个。拿不准时用 `dotnet build VelaShell.slnx --artifacts-path <临时目录> -p:Features=runtime-async=off`
编一遍,报出来的 CS4007 就是这类问题;CI 的 Linux 作业每次都这样编一遍(`.github/workflows/ci.yml`「扫 Span 跨 await」那一步),
那一步红了就是这一类。`ref` 局部变量跨 `await`(CS9217)两种模式都照报,不受影响。
- **改 `src/VelaShell.Ssh/` 之前先读 [`src/VelaShell.Ssh/AGENTS.md`](src/VelaShell.Ssh/AGENTS.md)**。
那是宿主的 SSH 库(2026-09-23 从 velashell-ssh 仓库并入,不单独发 NuGet),有一条宿主其余部分没有的
**净室规程**:写实现时不许打开任何其它 SSH 实现的源码,实现依据只能是 RFC 与 velashell-docs 的
`zh/ssh/spec/`。该目录按 **MIT** 授权,
别把宿主其余部分的代码挪进去。它的测试一律用 Debug 跑(Release 签名会省掉 `InternalsVisibleTo`)。
- **改 `src/VelaShell.XServer/` 之前先读 [`src/VelaShell.XServer/AGENTS.md`](src/VelaShell.XServer/AGENTS.md)**。
那是可嵌入的 X11 服务端库(2026-09-23 立项,MIT;宿主的「X Server」默认用它,Avalonia 宿主在 `src/VelaShell/Services/XServer/`),与 SSH 库同一套净室规程:
实现依据只能是 X.Org 的协议规范、ICCCM、EWMH 与 Khronos 的 GLX / OpenGL 规范,不许打开任何其它 X 服务端或 OpenGL 实现(Mesa 等)的源码。
真实客户端用例需 Docker 镜像 `velashell-xclients` 与 `VELASHELL_XSERVER_INTEROP=1`,不满足时同样早退记为通过。
- **插件 SDK 一律走 NuGet 包**,不做工程引用。版本在 `src/Directory.Packages.props`。
- **不要自己决定 SDK 版本号,也不要造本地包**。宿主的改动需要 SDK 的新契约时,正确做法是:
在 velashell-plugin-sdk 里把契约写好(那边**同样**不动版本号),然后**明确交代**
「需要发一版 SDK,发完把 `src/Directory.Packages.props` 与 `tests/Directory.Packages.props`
抬上去」。在那之前宿主编译不过是**预期内的**,不是要绕过的障碍。
绝不要 `dotnet pack` 一个本地包、往 `nuget.config` 塞本地源、或者把包版本指到一个
还不存在的号 —— 下一版发什么号是排期决定的,你猜一个就把所有下游都钉死在那个号上了;
何况本地包未签名(`VelaShell.snk` 不在仓库里),一编译就是一片 `CS0012` 假错误。
- **不许用「自研」「自建」(英文不许用 in-house)给东西贴标签**。代码注释、README、`plan.md`、界面文案、
提交信息与 PR 描述一律适用,换成同义的招牌词(自主研发 / 自有 / 自家 / 原创 / home-grown / our own)也不行。
这两个词现在都不是褒义词,满篇「自研」「自建」只会显得在自夸。直接写名字和事实:
「VT 终端引擎」而不是「自研 VT 终端引擎」,「VelaShell.Ssh(本仓库 `src/VelaShell.Ssh`)」而不是「自研 SSH 库」。
确实需要强调「没用第三方库」时写**具体事实**:「零第三方停靠依赖」「不依赖 Dock.Avalonia」
「在插件内实现编码器、替换 QRCoder」。
「自建」表示自行部署时写「自行部署的 MinIO」,表示自己创建时写「插件自己创建的控件」;
唯一保留的是企业微信后台的官方菜单名「自建应用」(那是产品术语,改了用户就找不到入口)。
- 永远不要试图使用Python来执行脚本或者进行什么操作.可以使用PowerShell或C#的单文件脚本的方式来处理.可以参考文档: [https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)

### 相关仓库

插件契约 SDK、`vela-plugin` CLI、`dotnet new` 模板、第一方插件与插件商店都在
[VelaShellLabs](https://github.com/VelaShellLabs) 组织下,文档全部在
[velashell-docs](https://github.com/VelaShellLabs/velashell-docs)。

### 提交约束

- **所有提交信息与 PR 描述都不要附加 Claude Code 等 AI 工具的作者信息** —— 不加 `Co-Authored-By: Claude …` 行,也不加 `🤖 Generated with Claude Code` 之类的署名。VelaShellLabs 下所有仓库一致适用。
- 只有仓库所有者才需要从dev分支发 PR,其他人都从 feature 分支发 PR。feature 分支的命名规则是 `feat/xxx` 或 `fix/xxx`,其中 `xxx` 是简短描述,不许用中文。仓库所有者处理同一任务中的连续调整时,若不是多个独立任务并行产生的不同改动,统一直接提交到 `dev`,从 `dev` 创建 PR 合并到 `main`,无需为每次调整另建分支。
- 创建PR的时候直接选择合并到main分支,但是在PR描述中必须写明本次PR的主要改动内容,以及是否涉及到文档的修改,如果涉及到文档的修改,请在PR描述中附上对应的文档链接。
- **发布 Release 时避免重复触发发布流水线**：`.github/workflows/release.yml` 同时监听 `published` 与 `edited`。创建 Release 后，必须一次性写好标题与正文（包括对应 PR 链接），不要再通过编辑 Release 反复修正文案；每次编辑都会为同一标签新增一次 Actions 运行。若正文确需修改，先确认已有同标签运行已结束，再只编辑一次，并等待该次 `edited` 运行完成。发布前先检查同一标签是否已有 Release、标签和运行，避免重复创建、删除后重建或并发补发。
- **Release 标题只填写版本号**：标题必须与标签一致（例如标签为 `1.7.4` 时标题填写 `1.7.4`），不要添加产品名或其他前后缀。创建 Release 时直接使用版本号作为标题，避免后续手动修改并再次触发发布流水线。
- **Release 正文统一使用以下 Changelog 模板**：每次发版都按固定章节组织内容，使用 Emoji 区分功能领域；每项功能必须说明用户可感知的变化、适用范围或限制，并附上真实的 Git 提交短 ID 与对应 PR 链接。只引用本次版本范围内实际存在的提交 / PR，不要猜测、编造或只写笼统的「修复若干问题」。

```markdown
本次更新聚焦 <版本主题 1>、<版本主题 2> 与 <版本主题 3>，为 <目标用户 / 使用场景> 带来 <一句话总结>。

## ✨ 重点更新

- **<功能名称>**：<一句话说明对用户的直接价值>。
  - <详细行为：用户在哪里可以使用、如何使用、默认行为是什么>
  - <适用范围 / 限制 / 兼容性 / 安全边界>
  - 变更：`<commit-sha>` · [PR #<number>](https://github.com/joesdu/VelaShell/pull/<number>)

- **<功能名称>**：<一句话说明对用户的直接价值>。
  - <详细行为>
  - <适用范围或限制>
  - 变更：`<commit-sha>` · [PR #<number>](https://github.com/joesdu/VelaShell/pull/<number>)

## 🖥️ <功能领域一>

### <子功能名称>

- **<变化名称>**：<用户可感知的变化，以及与旧版本相比的差异>。
  - <操作路径、配置项、默认值或触发条件>
  - <边界行为、兼容性说明或不会改变的行为>
  - 变更：`<commit-sha>` · [PR #<number>](https://github.com/joesdu/VelaShell/pull/<number>)

### <子功能名称>

- **<变化名称>**：<详细说明>。
  - 变更：`<commit-sha>` · [PR #<number>](https://github.com/joesdu/VelaShell/pull/<number>)

## 🔐 安全与隐私

- **<安全改进>**：<说明保护了什么、何时生效，以及用户是否需要采取行动>。
  - <迁移、回退、兼容性或已知限制>
  - 变更：`<commit-sha>` · [PR #<number>](https://github.com/joesdu/VelaShell/pull/<number>)

## 🐛 修复与稳定性

- **<问题名称>**：<说明旧版本中的现象、触发条件和现在的修复结果>。
  - 变更：`<commit-sha>` · [PR #<number>](https://github.com/joesdu/VelaShell/pull/<number>)

## ⚠️ 升级说明 / 不兼容变更

- <配置迁移、默认值变化、数据格式变化、插件 / SDK 要求或需要用户重新操作的事项>。
- 没有需要用户注意的升级事项时，写：`无需要用户额外操作的不兼容变更。`

## 📚 文档与生态

- <用户手册、隐私说明、插件 SDK 或相关生态仓库的同步内容>。
- 变更：`<commit-sha>` · [PR #<number>](https://github.com/joesdu/VelaShell/pull/<number>)

## ✅ 验证

- <构建命令与结果，例如 `dotnet build VelaShell.slnx -c Debug -warnaserror`：通过。>
- <测试范围与结果：宿主 / 插件 / 协议互操作 / 本地化 / Headless UI / 跨平台 CI。>
- <未运行或被环境跳过的验证必须明确写出原因，不要把跳过写成通过。>

## 🔗 相关变更

- [PR #<number>：<标题>](https://github.com/joesdu/VelaShell/pull/<number>)
- `<commit-sha>`：<变更摘要>

```

Release Changelog 编写规则：

1. 标题只写版本号，正文首段先给出本版本的一句话主题总结。
2. 章节顺序固定为「重点更新 → 功能领域 → 安全与隐私 → 修复与稳定性 → 升级说明 → 文档与生态 → 验证 → 相关变更」；没有内容的章节可以省略，但不能改变剩余章节的顺序。
3. 功能说明优先写用户收益，再写操作方式、默认值、适用范围、限制和兼容性；不要把内部实现细节当作唯一说明。
4. 每个重要功能或修复至少附一个真实提交短 ID；涉及多个 PR 时，逐项列出对应 PR，不要只在文末堆一串无法对应的链接。
5. PR 链接统一使用 `https://github.com/joesdu/VelaShell/pull/<number>`；跨仓库变更使用对应仓库的真实链接，并在文字中注明仓库名。
6. 「验证」只填写实际执行过的构建、测试和 CI 结果；区分通过、失败和跳过，并保留失败或跳过原因。
7. Release 创建时一次性填入版本号标题和完整正文，发布后不要为了格式问题反复编辑 Release，以免触发额外的 `edited` 发布流水线运行。
8. **Release 更新日志必须归档到本仓库的 `changelogs/release-notes-<版本号>.md`**（例如 `changelogs/release-notes-1.7.5.md`），并提交到版本控制。每次发版先完成并核对该文件，再直接使用文件内容作为 GitHub Release 的完整正文，确保归档与线上内容一致；在会话或临时目录中起草的文件必须移动到此目录，不能只留在临时目录或 GitHub Release 页面。
