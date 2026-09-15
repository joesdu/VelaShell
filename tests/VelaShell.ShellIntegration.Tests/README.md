# VelaShell.ShellIntegration.Tests

> 「文件浏览器跟随终端目录」的**端到端**验证:真 sshd、真登录 shell、真 PTY。

这一套横跨三个程序集,也正是它单独立项的理由 —— Core 出脚本与探针、Terminal 出抑制器与
仿真器、Infrastructure 出真 SSH 连接,塞进任何一个单元测试项目都会给那个项目平白拖进
两份不相干的依赖。

## 为什么必须有这一套

这个功能栽过的每一个跟头,**都只在真机上现形**:

| 真机发现的问题 | 替身测试为什么发现不了 |
| --- | --- |
| `PROMPT_COMMAND` 结尾带分号时拼出 `;;`,用户每敲一次回车看一行语法错误 | 那是 bash 的语法,C# 只断言得了字符串里有什么 |
| fish 对 `${var:-}` 在**解析期**就报错,整行连同后面的命令一起死掉 | 得有一个真的 fish 去解析那一行 |
| 注入行超出终端宽度后,**回显被折行重绘**——ash 插 `CR LF`、zsh 重复断点字符、fish 整行重排 | 替身流吐什么是我们自己写的,永远"刚好"匹配 |
| 摘历史前缀两百多字符,把用户自己那条短命令也顶过了宽度 | 同上 |

前三条直接推翻了一版实现(逐字节匹配回显),第四条是它连带暴露的既有 bug。

## 靶子

`docker-compose.test.yml` 里的 `ssh-shells` 服务(镜像见
[tests/fixtures/ssh-shells](../fixtures/ssh-shells/Dockerfile)):一台容器、七个账号。

```bash
docker compose -f docker-compose.test.yml up -d ssh-shells   # 端口 2223,口令一律 velapass
```

| 账号 | 登录 shell | 用来验什么 |
| --- | --- | --- |
| `vela-bash` / `vela-zsh` / `vela-fish` / `vela-dash` / `vela-ash` | 同名 | 四段脚本 × 五种 shell 的探测、注入、跟随、隔离 |
| `vela-pyenv` | bash | rc 把 `PROMPT_COMMAND` 设成 `_pyenv_virtualenv_hook;`(pyenv-virtualenv 的真实形状)——**用户的钩子不许被弄坏,也不许拼出 `;;`** |
| `vela-starship` | bash | rc 每次提示符都重写 `PS1`(starship / p10k 那一类)——**提示符被别人全权接管时也得工作** |

起不来就整组报 Inconclusive,而不是假装通过 —— 全绿的报告里不能混着一行断言都没跑的用例。

## 覆盖范围

- **探测** — 五种登录 shell 都得认对(fish 要两道探针才认得出来)。
- **注入** — cwd 立刻报得出来(不必等用户先敲回车),且屏幕上一个字节的痕迹都没有。
- **跟随** — 终端里 `cd` 之后再报一次。
- **隔离** — 用户紧跟着的命令输出<b>原样显示</b>;用户的 `PROMPT_COMMAND` / `PS1` 既不被我们弄坏,也弄不坏我们。
- **不留痕** — 注入行不进命令历史;钩子对 `$?` 透明(提示符看到的仍是上条命令的退出码)。
- **tmux 透传** — 在 tmux 里额外发一份包进 DCS 的拷贝,三种引号规则的转义各验一遍。

## harness

`ShellIntegrationHarness` 把「注入 → 抑制 → 解析」接成与宿主同构的一条链:
读循环把原始字节喂给 `EchoSuppressor`,再喂给 `TerminalEmulator`,分别留下
`Raw`(抑制前)与 `Visible`(抑制后,也就是用户真正看得见的)。断言失败时两份一起打出来。

它**刻意重走** `TerminalTabViewModel.SendSilentCommand` 的每一步而不是调用它(那个方法在 UI
程序集里,拖进来要连 Avalonia 一起)。步骤一旦分叉就验不到真东西,所以两边的注释都写着
对方的名字:改了一边,记得看另一边。
