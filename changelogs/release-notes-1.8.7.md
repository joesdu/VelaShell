本次更新聚焦多级会话整理、跨设备会话同步与 AI 协作稳定性，为管理多台主机、组织复杂连接层级和使用团队 Agent 的用户带来更清晰、更可靠的工作流。

## ✨ 重点更新

- **多级会话分组**：连接可以按多层目录组织，适合按项目、环境和用途管理大量主机。
  - 在连接编辑器中输入 `项目 > 项目A > 生产`，保存时会自动创建缺少的分组；不同父组下允许使用同名子组。
  - 在资源管理器中拖动分组可以调整同级顺序、移入其他分组或移回根层；移走最后一个连接后空组仍会保留。整组打开、巡检和导出覆盖全部后代连接，删除前会显示受影响的子组与连接数量。
  - JSON v2 保存嵌套分组与空组并继续支持 v1 导入；CSV 增加 `group_path`，从 Xshell 与 WinSCP 导入时保留来源目录层级。
  - 变更：`2a7a9c98` · [PR #794](https://github.com/joesdu/VelaShell/pull/794)

## 🤖 AI 助手

- **团队等待与插话顺序更稳定**：团队启动或重试后，协调器先在本地等待成员报告汇总，再向主模型发起下一次请求，减少等待期间的重复请求。
  - 等待期间到达的用户消息会按原顺序保留，并在后续工具步骤中送达；取消与重试也沿用同一顺序。
  - 变更：`6c51d066` · [PR #792](https://github.com/joesdu/VelaShell/pull/792)

- **本机文件链接打开位置更准确**：在 AI 聊天或团队控制台中 Ctrl+单击本机文件链接会打开文件所在目录，目录链接会直接打开对应目录。
  - 文件名交由系统文件管理器显示，用户可以在目录中确认目标后再操作；远端路径和网页链接继续按原有方式处理。
  - 变更：`5d754637` · [PR #791](https://github.com/joesdu/VelaShell/pull/791)

## ⚠️ 升级说明 / 不兼容变更

- Gist 同步首次保存嵌套分组后使用同步格式 v4。即使之后把所有分组移回根层，仍会继续使用 v4；只支持 v2/v3 的设备需要升级后再拉取。使用多设备同步的用户应先完成备份并升级相关设备。
- 旧版扁平分组数据仍可使用。JSON v1 文件可以继续导入，CSV 中原有的 `group` 列仍按字面组名读取，完整分组路径使用 `group_path`。
- 内置 AI 插件 `velashell.ai` 升级到 1.8.7，插件 SDK 兼容门槛保持为 2.2.0。

## 📚 文档与生态

- 中文和英文用户手册已同步多级分组、导入导出、Gist 同步、团队等待顺序和本机文件链接行为。
- `velashell-docs` 已同步相关中英文规格与行为说明：[PR #252](https://github.com/VelaShellLabs/velashell-docs/pull/252) · [PR #253](https://github.com/VelaShellLabs/velashell-docs/pull/253)。

## ✅ 验证

- PR #794 的 Windows、Ubuntu、macOS 构建与测试，以及 C#、C/C++、CodeQL 分析均通过：[CI run #44800422798](https://github.com/joesdu/VelaShell/actions/runs/44800422798) · [CodeQL run #44800414231](https://github.com/joesdu/VelaShell/actions/runs/44800414231)。
- PR #791：Windows AI 插件测试 2,475 项通过、0 失败、0 跳过。
- PR #792：相关团队测试 123 项通过。
- 发版基线 `f1b25fd7` 的 Windows、Ubuntu 和 macOS 构建与测试均通过。常规 CI 排除了 DockerIntegration、CrossPlatform 和 Interop 类别。[三平台 CI #44814279734](https://github.com/joesdu/VelaShell/actions/runs/44814279734)
- 发布准备期间，Windows 本机 `dotnet build VelaShell.slnx -c Debug -warnaserror` 通过。全量测试在本机默认环境下返回失败，其中 7 项本机命令测试遇到 WindowsApps 的 `pwsh.exe` 启动被拒。改用 Windows PowerShell 重跑本机命令专项，18 项全部通过。
- 未做 macOS、Linux 实机交互验收，也未对真实供应商、真实远端上传和 Microsoft Store 提交做人工验收。

## 🔗 相关变更

- [PR #791：修复 AI 回复中的本机文件链接，点击后打开所在目录](https://github.com/joesdu/VelaShell/pull/791) — `5d754637`
- [PR #792：修复团队模式等待期间主模型重复请求与插话顺序](https://github.com/joesdu/VelaShell/pull/792) — `6c51d066`
- [PR #794：支持多级会话分组及其导入、导出与同步](https://github.com/joesdu/VelaShell/pull/794) — `2a7a9c98`
- 完整差异：[1.8.6...1.8.7](https://github.com/joesdu/VelaShell/compare/1.8.6...1.8.7)
