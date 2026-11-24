本次更新让多终端操作更方便：同步输入可分配到六个频道，SSH 断线后可以回看重连前的输出，资源管理器里的会话分组也可以直接改名。

## ✨ 重点更新

- **集中管理同步输入频道**。从终端标签的「同步输入 → 频道管理」、同步横条或「资源管理器 → 更多 → 频道管理」打开窗口，即可查看 A–F 六个频道和未加入频道的终端。按 `Ctrl+F` 搜索终端名、主机或分组路径，可批量移动、暂停、恢复或移出成员。离线成员只能移出频道。`358b8662`、`f6ca788f`、`10b1ac36` · [PR #803](https://github.com/joesdu/VelaShell/pull/803)
- **同一 SSH 标签重连后可回看输出**。手动或自动重连成功时，带本地时间的分隔行标出新会话的起点，向上滚动即可查看回滚行数范围内的主屏输出。此选项默认开启，可在「设置 → 常规 → 行为」关闭。远端 shell 进程状态仍由服务器管理。`4c44f349` · [PR #805](https://github.com/joesdu/VelaShell/pull/805)

## 🖥️ 资源管理器与终端

- **直接重命名会话分组**。在资源管理器右键分组并选择「重命名」，子组路径会一并更新。新名称不能留空、包含 `>` 或与同级分组重名，重名检查忽略大小写。`cc15a34e` · [PR #804](https://github.com/joesdu/VelaShell/pull/804)
- **减少全屏终端程序的重绘**。备用屏幕复用未变化的行，只重绘发生变化的区域。连续滚屏和突发 SSH 输出也减少了逐行搬移与临时缓冲区分配。`f516301d` · [PR #809](https://github.com/joesdu/VelaShell/pull/809)

## ⚠️ 升级说明

- 本版随附的 AI 插件要求 VelaShell **1.8.9 或更高版本**。插件开发者适配时请注意，宿主已升级至 Avalonia 12.2.0 和 PluginSdk 2.2.1。`18872bed`、`acf20c08`、`37127633` · [PR #808](https://github.com/joesdu/VelaShell/pull/808) · [PR #810](https://github.com/joesdu/VelaShell/pull/810)
- 主程序和隔离插件宿主的受支持 64 位自包含发行包启用了实验性的 Satori GC。自行构建时可用 `-p:UseSatoriGC=false` 关闭。`1f86e5c3` · [PR #806](https://github.com/joesdu/VelaShell/pull/806)

## 📚 文档与生态

- 中英文用户手册第 4.2、6.4 和 8.2 节分别更新了分组重命名、SSH 重连和频道管理的操作说明。`cc15a34e`、`4c44f349`、`358b8662` · [PR #804](https://github.com/joesdu/VelaShell/pull/804) · [PR #805](https://github.com/joesdu/VelaShell/pull/805) · [PR #803](https://github.com/joesdu/VelaShell/pull/803)

## ✅ 验证

- 终端性能改动的 PR 验证记录包括 666 项终端测试和 10 项渲染测试，均通过。渲染测试在本地缺少签名密钥的环境中关闭程序集签名后运行。`f516301d` · [PR #809](https://github.com/joesdu/VelaShell/pull/809)
- 终端性能改动合并后的主分支提交 `991d281e` 已通过 [GitHub Actions CI](https://github.com/joesdu/VelaShell/actions/runs/45365247720)。

## 🔗 相关变更

[查看 1.8.8 到 1.8.9 的完整差异](https://github.com/joesdu/VelaShell/compare/1.8.8...1.8.9)
