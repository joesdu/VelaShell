本次更新聚焦可选主密码、AI 助手专属区域与输入改版、连接对话框的「先选类型」步骤，并集中修复复审发现的安全与数据正确性问题，为管理多台服务器、多种连接类型的用户带来更安全的本机凭据保护和更顺手的日常操作。

## ✨ 重点更新

- **可选主密码**：给本机保存的密码、共享凭据、云同步令牌和插件机密再加一道锁，别人拿到数据目录或坐到你没锁屏的电脑前，也读不出这些机密。
  - 在「设置 → 常规 → 隐私与安全 → 主密码」点「开启…」，输入两次主密码并确认后果；默认同时生成一份只显示一次的恢复码。之后每次启动会弹一次解锁框，可以取消；状态栏左侧的锁形图标用来解锁或立即锁定。
  - 锁着时 VelaShell 照常可用，只是读不出已存的机密：主动连接、编辑存着密码的连接、导入连接、手动同步会先弹解锁框；自动重连、隧道自动重建和 Gist 自动同步不弹框，等解锁后各重试一次。可选「空闲后自动锁定（分钟）」和「系统锁屏时锁定」（目前只在 Windows 上显示），两项默认关闭。
  - 主密码经 Argon2id（64 MiB、3 次、并行 1）派生密钥，包裹本机加密密钥；已存的密文一字不改，开启、修改、关闭只重写这层包裹，任一步失败就整体撤回。输错后要等待递增的间隔（最长 30 秒）再试，不会锁死。主密码只管这台电脑，不随 Gist 同步。
  - 变更：`0e73e5de`、`e51c3a74`、`cf5b34f4`、`cc6b9525`、`0d015859`、`e98105a3`、`eb5fdfe2`、`154602a5`、`b248a467`、`8b265f49` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)

- **AI 助手有了自己的区域和标题栏入口**：不用再从命令面板打开，也不会占用文档标签或切走当前终端。
  - 主窗口右上角新增 Sparkles 图标按钮，点击后 AI 助手在主窗口最右侧的专属区域打开；已经打开时切回原会话，不重复创建。
  - 专属区域可以拖宽（最小 300px），可以固定占位，也可以悬浮自动隐藏；隐藏后只有悬停边条上的 AI 图标才会展开。窗口太窄时临时改为浮层，放大后恢复固定偏好。宽度和固定偏好只保存在本机，设置导入时保留本机值。
  - AI 插件的聊天标签、协作窗口和模型配置窗口统一使用 Sparkles 图标。
  - 变更：`8b95d14a`、`e9800959` · [PR #690](https://github.com/joesdu/VelaShell/pull/690)、[PR #691](https://github.com/joesdu/VelaShell/pull/691)

- **新建连接先选类型**：声明了多个变体的插件工作台（例如数据库插件的 MySQL / PostgreSQL / SQL Server / Oracle / SQLite）改成「先选类型卡片 → 下一步 → 填表单」，不用填完连接信息才在最后选数据库种类。
  - 端口、主机标签、凭据栏和跳板主机都跟着所选类型变化：切到 PostgreSQL 时默认端口随之改变，手填的端口不动；SQLite 这类没有网络端点的类型会收起端口、账号和跳板主机，主机栏留空时提示「请填写数据库文件」。
  - 卡片上单击只选中，双击或按 Enter 选中并进入下一步。表单阶段新建时有「上一步」，编辑已有连接时表头带「更换」，两者都不清除已填的值。编辑已存连接时不会被悄悄改端口。
  - 插件可以在「常规」和「高级」之间加入自己的表单分页，类型卡片显示插件给的变体图标（需要插件使用 SDK 2.1.5 或更高版本）。没有变体的连接类型（Redis、S3、Telnet 等）界面不变。
  - 变更：`f75bb2ca`、`05e726dd`、`6a117007`、`0090be0e`、`224c713a`、`56d6fffb` · [PR #693](https://github.com/joesdu/VelaShell/pull/693)、[PR #698](https://github.com/joesdu/VelaShell/pull/698)

- **重连用库里最新的配置，「记住密码」真正保存**：在别处改过的连接设置不会再被已打开标签里的旧副本覆盖。
  - 终端标签的普通重连（含 Telnet、串口等插件终端协议）先从连接库重读配置；连上之后只更新最近连接时间，不再把整份旧配置写回。云同步拉下来的、编辑框里刚存的、健康巡检「放开这几个算法」写进去的设置，重连时都会生效。
  - FTP / FTPS、插件协议文件标签和插件工作台弹过登录框时，勾选的「记住密码」和用户名现在会保存下来；以前这三处下次打开仍会再问。证书信任只记录指纹，不连带写回其他字段。同一标签的重连不再并行发起。
  - 变更：`2d2813bd`、`a1c0895b`、`448d400a`、`b248a467` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)

## 🤖 AI 助手

### 输入框与思考档位

- **紧凑的输入操作行**：附件、模型 / 思考摘要、MCP 配置、聊天 / 计划 / Agent 模式选择和发送按钮集中在输入框内。
  - 模型菜单是带选中标记的圆角列表，可以直接选思考档位，保留供应商身份、临时覆盖、键盘操作和焦点恢复；MCP 配置入口紧邻模型选择。审批与用量信息固定在输入框外的一行。
  - 发送与停止合并为同一个按钮：空闲时发送，回复期间停止，结束后恢复；回复期间按 Enter 仍可把补充内容排队。输入框默认高度增加 16px。
  - 变更：`b92fa648`、`3b143a83`、`51d8c6d7` · [PR #694](https://github.com/joesdu/VelaShell/pull/694)、[PR #695](https://github.com/joesdu/VelaShell/pull/695)

- **补齐 xhigh / max 思考档位**：在默认、关闭和 low / medium / high 之外新增 xhigh 与 max。
  - OpenAI 的两套接口按所选档位原样发送，Responses 接口的 `store: false` 不覆盖思考配置；Anthropic 映射为相应的思考预算，并受输出上限约束。
  - 默认不发送思考档位，由服务端决定；实际可用的档位取决于服务端与模型。
  - 变更：`3b143a83` · [PR #694](https://github.com/joesdu/VelaShell/pull/694)

- **作图工具更容易发现**：还没配置默认图片模型时，`@` 菜单仍会列出生图和改图工具，选中后提示去哪里配置；发送前照常校验模型配置。
  - 新增「检测服务器上是否存在可疑程序」起手建议。
  - 变更：`51d8c6d7` · [PR #695](https://github.com/joesdu/VelaShell/pull/695)

### 图片与对话记录

- **生成的图片可以直接复制**：聊天图片右下角新增复制图标，大图预览窗口底部新增「复制图片」按钮，不用先保存文件就能粘贴分享。
  - 复制的是原始图片字节，关闭预览或聊天面板后剪贴板仍可读取；原图缺失时提示失败，不覆盖已有剪贴板内容。窄窗口中预览工具栏自动换行。
  - 变更：`141e4f1c` · [PR #692](https://github.com/joesdu/VelaShell/pull/692)

- **故障转移和换 Key 留下记录**：自动切换到另一个服务站点或另一把 API Key 时，聊天记录里留一张切换提示卡，写明改用了哪一站、哪一把 Key（序号加末四位）以及原站点被换掉的原因。
  - 每轮最多一张卡，不进入模型上下文；随回复一起存档，导出时写成引用。
  - 变更：`0c4ff3d2` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)

- **图片文件随历史回收**：删除或清空对话历史时一并删除对应的图片文件，启动时清理不再被引用的孤儿图片；1.7.5 中删除历史不会删除已存图片的限制已解除。
  - GIF 附件不再登记为可改的参考图，避免 `edit_image` 必然失败。
  - 变更：`82194613`、`dc2a008f` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

## 🖥️ 终端、SSH 与隧道

### 隧道与 ssh_config

- **远程转发两头可以各自用 Unix 套接字**：新增「服务器端口 → 本机套接字」和「服务器套接字 → 本机 TCP」两种组合，只填一头路径也能建远程转发。
  - 隧道表单与本地转发同一口径：填了路径的那一头才用套接字，并置灰对应的主机 / 端口栏；两栏都空时照旧用端口。ssh_config 中这两种 `RemoteForward` 写法也能导入和导出，不再写成注释。在套接字上监听的远程动态转发仍不支持。
  - 变更：`31dbb6df`、`c850c53b` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)

- **ssh_config 按 OpenSSH 的读法解析引号与反斜杠**：导入时双引号、单引号和反斜杠转义的含义与 OpenSSH 一致；导出的命名管道、UNC 路径和 Windows 本机套接字路径（写成 `[C:/…]`）都能被 ssh 读回原值。
  - 引号没配对的行会在导入预览里逐条说明（ssh 读到这样的行会拒绝整份配置）。
  - 导出时算法清单规范为逗号连接的一条；使用系统默认 agent 的连接写成 `IdentityAgent SSH_AUTH_SOCK`，导出再导入不再变成密码认证；「允许老算法」写成 `PubkeyAcceptedKeyTypes +ssh-rsa`，Windows 10 自带的 OpenSSH 8.1 也能读。
  - 变更：`1ada877b`、`1956d9c5`、`c1f97f4b`、`35a96a89`、`8e5a1dba`、`8e04a462`、`c0e83c7e` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)、[PR #700](https://github.com/joesdu/VelaShell/pull/700)

- **FIPS 算法预设去掉群交换**：服务端在群交换中现给的素数不属于 FIPS 认可的命名群，FIPS 预设的密钥交换现在只保留命名的曲线和群。
  - 默认算法清单不变，仍包含群交换。
  - 变更：`12af0bd5` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)

### 健康巡检与主机信任

- **会话健康巡检更准**：每一轮先从库里读取各连接的最新配置；私钥带口令而口令没保存，或系统密钥库的钥需要 PIN / 确认时，记为「需要凭据」，不再标红「连不上」，也不会在并发探测时一台弹一个 Windows PIN 框。
  - 「放开这几个算法」在确认后重读库里那一份再补算法，不再冲掉确认框打开期间别处的改动。
  - known_hosts 导入时，类型只记了占位词的旧信任记录也算冲突，不会把另一把钥并排记为受信。
  - 变更：`4f05abe7`、`a31289c5`、`eb868309`、`7990f193`、`d3549e39` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

### 终端

- **非 ASCII 目录名安全进入**：初始目录与「在此处打开终端」遇到非 ASCII 目录名时，改发只含可打印 ASCII 的 `cd "$(printf '…')"`，避免远端 C 区域设置下行编辑器把高位字节当作 Meta 键改动命令行。
  - 目录名按会话字符集编码（GBK 会话就是 GBK 字节）。
  - 变更：`c1c9abd4` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)

- **代理失败提示本地化**：HTTP 代理连接 SSH 22 端口或 FTP 失败时追加的「改用 SOCKS5」提示改为界面语言显示，中文、日文、韩文界面不再出现写死的英文。
  - 变更：`4469f61f` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)

## 📁 文件与传输

### FTP 续传与重试

- **传到一半中断可以自动重试**：FTP 数据连接中途中断而控制连接仍正常应答时，归为可重试的传输中断，由传输失败重试再试一次，会话不再变灰。
  - 服务器不支持 `REST` / `APPE` 时，如果要续的是本次自己写下的半截，就从头整份重传；规划时探到的已有半截照旧交给冲突策略。
  - Linux / macOS 上上传返回失败而服务器没给最终回复时，先询问控制连接，不再把控制连接掉线报成永久的上传失败。
  - 变更：`2c1247c4`、`11d6c53e`、`5c7097db` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)

- **续传不再重复追加字节**：续传下载传到一半被复位时，以前可能从同一偏移再追加一遍，文件变长却显示完成；现在内部重试按「这次是否已动过字节」判断，每次截回起点。
  - FTP 续传与 SFTP 一样，先核实远端 / 本地那半截确实是这份文件的开头再追加，对不上时交给冲突策略，不再按长度盲接。
  - 变更：`db3f9f2c`、`d8556e80` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

### 传输队列与插件传输

- **暂停批次还原更可靠**：一份读不出来的队列文档不再挡住其余批次的还原；分段写入不再留下缺段的批次，缺段的整批读回时不还原。
  - 变更：`901e3ea5`、`c284f988` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

- **停止 AI 回复不再撤掉刚批准的传输**：宿主实现插件 SDK 的条件撤回，判定与撤下在同一把锁内完成；AI 插件在停止或拒绝时改用它撤回待确认的传输。
  - 修复 AI 传输卡片的撤权状态被后到的完成快照覆盖的问题。
  - 变更：`dadc3201`、`39e74c56`、`347b6cc0` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)、[PR #696](https://github.com/joesdu/VelaShell/pull/696)

### 外部编辑与远端浏览

- **外部编辑草稿可以在消息中心直接重新上传**：草稿消息的主动作是「重新上传」，会找已连接的面板或打开文件标签；第二个动作「打开所在文件夹」。
  - 远端文件在这之后被改过、不在了或无法确认时先询问；没有可询问的人时按不覆盖处理。成功后删除草稿并撤下消息。
  - 断线结束侧栏的编辑会话时不再删除本地副本，每个会话弹一条提示；没有改动过的副本不会因为 macOS FSEvents 补投的事件而回传。
  - 变更：`d2862d82`、`ff713689`、`d78d6aff` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)、[PR #699](https://github.com/joesdu/VelaShell/pull/699)

- **远端搜索与非 UTF-8 会话**：远端搜索收满结果上限后立即取消远端的 `find` / `grep`，不再等整棵树走完或两分钟超时；文件名编解码与 SFTP 一致，Big5 / Shift_JIS 模式下按字节转义。
  - 非 UTF-8 会话删除中文路径不再交给 UTF-8 命令行的 `rm -rf`，删除后再 `stat` 确认。
  - FTP 服务器时区在东边时，「比较目录」遇到没有时间的条目不再抛异常。
  - 变更：`6a97a982`、`986664f3`、`ee432a8d`、`951dcc10` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

## 🔐 安全与隐私

- **远程动态转发放行名单按写法比较**：以前名单是纯文字通配，`10.0.0.*:*` 能匹配上 `10.0.0.1.attacker.example`；现在 IP 规则只匹配 IP，远端给的主机名先过写法检查，带控制字符、空白等的畸形名字一律拒绝。
  - 变更：`e1007bd7` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

- **「在此处打开终端」不再执行恶意目录名**：带控制字符的远端目录名以前会被当作击键送进终端，可能借行编辑器注入命令；现在这种目录不执行 `cd`，并给出提示。登录 shell 是 fish 时按 fish 的引号规则转义。
  - 变更：`2706df3f` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

- **云同步不再泄露或冲掉机密**：没设端到端口令时，插件声明的机密字段（如 S3 会话令牌）不再以明文上传；加密载荷不再推送本机解不开的密文，拉到的密文也不会顶掉本机可用的密码。
  - 变更：`de8e41f8`、`a6eaabb1` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

- **插件凭据交付更收敛**：没有凭据的连接类型和声明了 `NoCredentials` 的协议（如 Telnet）不保存、不交出用户名和口令；存着的插件机密解不开时报告是哪一项，绝不把密文原样交给插件。
  - 插件更新包改在数据目录的私有子目录里下载、核对和安装，不再使用 Linux 上多用户共用的 `/tmp`。
  - 变更：`785a320a`、`2cd9d78c`、`09528f1d`、`9b07bf7d` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)、[PR #700](https://github.com/joesdu/VelaShell/pull/700)

- **减少口令落盘与伪装**：快捷命令中凭据类占位填入的值不再进入命令历史与补全；命令块在提示符与命令之间夹着屏幕上看不见的字（如 SGR 8 隐藏文本）时，不提供复制和重跑。
  - 会话日志与录制遮盖支持 INI 写法 `password = my pass phrase`（名字在行首、等号两边有空白）遮到行尾；空着的 `password =` 不再被误遮。
  - 变更：`af59e73e`、`1d1d8aae`、`39c45c42` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)、[PR #700](https://github.com/joesdu/VelaShell/pull/700)

## 🐛 修复与稳定性

- **共享密码填入只在认得出的密码提示上按回车**：从命令面板手动填入时，如果光标所在行不是密码提示，只填不按回车，避免密码被当成命令执行并进入 shell 历史；本地化的「X 的密码」只认 sudo 一族的提示，不再把 `passwd` 的「新的密码：」当作可填入的提示。
  - 变更：`033eccb2`、`3df45b51` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

- **输出触发器自动应答更稳**：带 shell 提示符的行不再自动应答；改列宽后按逻辑行保留「已应答」记号，冷却或等待安静中的提示照常应答；自动应答改走后台输入口，不再清掉选区或把往回翻的视图拉回底部。
  - 变更：`1db9c8c1`、`8010ac54`、`7d047920` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

- **云同步与布局模板**：布局模板改动不再给主载荷打「本机改过」标记，避免用旧设置覆盖其他设备的新改动；一次性迁移标记按设备计算；布局模板导入「替换」时不再丢一条。
  - 变更：`511b8110`、`414940e8`、`d173d8ef` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

- **启动、消息中心与插件日志**：存着代理密码时，启动不再在 UI 线程上等待系统密钥链，钥匙环锁着时主窗口也能正常出现；刷屏的插件消息挤不掉宿主消息（每个插件最多保留 20 条）；插件日志轮转件改为在插件 ID 后接 `.log.1`，启动时清理已卸载插件的日志；停靠布局还原后不再长期持有标签引用。
  - 变更：`baae53ef`、`3efe862e`、`b502611e`、`870fd7eb` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)

- **连接对话框**：编辑态从内建协议点回同一个插件页签时，端口不再被改成页签默认值；修复「系统密钥库」详情行在还没选钥时刷出的两条绑定错误，界面表现不变。
  - 变更：`7060c054`、`9b82bbe2` · [PR #699](https://github.com/joesdu/VelaShell/pull/699)、[PR #703](https://github.com/joesdu/VelaShell/pull/703)

- **测试与构建稳定性**：修复 macOS CI 上远程编辑等待和 AI 传输卡片的时序竞态、XServer 回退测试的端口竞争、sudo 状态测试的探针竞态，以及格式化提交后测试工程的编译错误；若干依赖 UI 线程的用例改在 headless UI 线程上运行。SDK 2.1.6 带来的同名异常改用别名区分。这些是测试与构建修正，没有放宽产品超时或断言。
  - 变更：`28080715`、`ce0096df`、`6e30f81c`、`c164cc8d`、`6fbd17c2`、`355fea71`、`1f41d0b8`、`b12a8539`、`a54367f3`、`ebc9e664` · [PR #696](https://github.com/joesdu/VelaShell/pull/696)、[PR #693](https://github.com/joesdu/VelaShell/pull/693)、[PR #698](https://github.com/joesdu/VelaShell/pull/698)、[PR #699](https://github.com/joesdu/VelaShell/pull/699)、[PR #700](https://github.com/joesdu/VelaShell/pull/700)

## ⚠️ 升级说明 / 不兼容变更

- **AI 助手不再以文档标签打开**：通过标题栏按钮或命令面板打开时进入主窗口右侧的专属区域；AI 插件不再使用 SDK 的面板宽度设置。
- **主密码默认关闭**：开启后忘了主密码又没有恢复码，已存的机密在这台电脑上无法找回，需要重新输入（旧密钥会改名保留为 `*.forgotten-时间`）。降级到不支持主密码的旧版本：macOS / Linux 上旧版本会另起一把新密钥；Windows 上旧版本会把包裹过的 `secret.key` 改名保留再另起新密钥，升回来时主密码按关闭处理，降级之前存的密码需要重新输入。
- **重连使用库里的最新配置**：标签打开期间在别处改过的主机、算法、认证方式等，重连时会生效；登录身份改变后，标签里手输的口令不会带过去。
- **FIPS 预设不再包含 `diffie-hellman-group-exchange-sha256`**：只提供群交换的服务器使用 FIPS 预设时会协商失败，需要自行组合算法清单。
- **ssh_config 读写口径变化**：导入时引号与反斜杠按 OpenSSH 规则解析，含反斜杠的旧配置可能读出与以前不同的值；导出的算法清单、agent 认证和老算法写法有变化（见上文）。
- **云同步与插件机密**：没设端到端口令时，插件机密字段不再上传，其他设备上需要手动填写。
- **目录名含控制字符时不进入**：「在此处打开终端」与初始目录遇到这类目录名时不执行 `cd`，终端停在默认目录并提示。
- **嵌入 SSH 库的开发者**：`VelaShell.Ssh` 的远程转发公开面变化，新增 `RemotePortForwardOptions.ListenSocketPath` 与 `RemotePortForwarder.StartToUnixSocketAsync`，删除与之重复的 `StartUnixSocketAsync`（两头套接字改用 `StartToUnixSocketAsync` 加 `ListenSocketPath`）。
- **插件 SDK**：宿主引用的插件 SDK 升至 2.1.6，AI 插件的 `minSdkVersion` 同步为 2.1.6。2.1.5 带来变体图标与自定义表单分页；2.1.6 新增的机密「已锁定」契约宿主尚未接入，主密码锁着时插件读写机密仍按「等待交互式解锁，取消则报权限被拒」处理。
- macOS 安装包未签名、未公证；Linux AppImage、deb、rpm 未签名，沿用现有安装方式。

## 📚 文档与生态

- 中英文用户手册同步主密码、AI 助手专属区域与输入框、连接类型选择、重连与登录框保存、远程转发套接字、ssh_config 读写、FTP 重试与外部编辑草稿等变化；`PRIVACY.md` 的「不上传」清单补上插件机密字段。
  - 变更：`0d015859`、`86719cdf`、`65659d86`、`de8e41f8`、`6a117007` · [PR #700](https://github.com/joesdu/VelaShell/pull/700)、[PR #699](https://github.com/joesdu/VelaShell/pull/699)、[PR #693](https://github.com/joesdu/VelaShell/pull/693)
- README 新增「致谢捐赠者」名单（中英两份）。
  - 变更：`80a28307`、`96d0a1cb` · [PR #702](https://github.com/joesdu/VelaShell/pull/702)
- 更新日志归档到 `changelogs/`：1.7.5 正文入库并明确保存规则；1.7.4 改正 8 处提交与 PR 的对应和 3 条死链。
  - 变更：`2b9fb38c`、`468a3ac8`、`0919249d` · [PR #689](https://github.com/joesdu/VelaShell/pull/689)、[PR #699](https://github.com/joesdu/VelaShell/pull/699)
- `VelaShellLabs/velashell-docs` 中英文开发文档均已合并：[PR #162](https://github.com/VelaShellLabs/velashell-docs/pull/162)（标题栏入口）、[PR #163](https://github.com/VelaShellLabs/velashell-docs/pull/163)（AI 专属区）、[PR #164](https://github.com/VelaShellLabs/velashell-docs/pull/164)（复制图片）、[PR #165](https://github.com/VelaShellLabs/velashell-docs/pull/165) 与 [PR #167](https://github.com/VelaShellLabs/velashell-docs/pull/167)（工作台变体与分页）、[PR #166](https://github.com/VelaShellLabs/velashell-docs/pull/166)（输入框与思考档位）、[PR #169](https://github.com/VelaShellLabs/velashell-docs/pull/169)（复审修复）、[PR #175](https://github.com/VelaShellLabs/velashell-docs/pull/175)（主密码与清欠账）。
- 插件 SDK 仓库 `VelaShellLabs/velashell-plugin-sdk`：[PR #46](https://github.com/VelaShellLabs/velashell-plugin-sdk/pull/46)、[PR #47](https://github.com/VelaShellLabs/velashell-plugin-sdk/pull/47)（变体图标与自定义分页，随 2.1.5 发布）、[PR #48](https://github.com/VelaShellLabs/velashell-plugin-sdk/pull/48)（机密「已锁定」契约，随 2.1.6 发布）。
- 生态仓库同步：`VelaShellLabs/velashell-plugins` [PR #99](https://github.com/VelaShellLabs/velashell-plugins/pull/99)（S3 服务器端摘要与过时说法）、`VelaShellLabs/velashell-plugin-templates` [PR #14](https://github.com/VelaShellLabs/velashell-plugin-templates/pull/14)、`VelaShellLabs/velashell-plugin-cli` [PR #38](https://github.com/VelaShellLabs/velashell-plugin-cli/pull/38)。

## ✅ 验证

- 发布提交：`e6484531`（`main`），变更范围为 `1.7.5...1.7.6`。
- 发布提交的 [三平台 CI](https://github.com/joesdu/VelaShell/actions/runs/41294545866) 首轮全部通过：Windows、macOS、Linux 均执行 `dotnet build VelaShell.slnx -c Debug --nologo -warnaserror` 与 `scripts/ci/run-tests.sh`；Linux 额外的 `runtime-async=off` 编译检查（CS4007）也通过。
- 各平台 14 个测试程序集合计：Windows 11346 通过 / 0 失败 / 56 跳过；macOS 11334 通过 / 0 失败 / 68 跳过；Linux 11324 通过 / 0 失败 / 78 跳过。跳过的是平台或环境不满足而早退的用例，不计为实际验证通过。
- CI 排除需要专用环境的 `DockerIntegration`、`CrossPlatform` 与 `Interop` 类别，因此 SSH 远程转发混合端与 FIPS 预设没有对真实 OpenSSH 服务器核对；需要真实系统密钥链的用例（`VELASHELL_KEYCHAIN_TESTS=1`）未运行。
- 本次发版未在本机重复完整构建或测试，以上结果以链接中的 CI 为准；未进行原生桌面 GUI、跨应用剪贴板粘贴、真实计费模型供应商和现场 FTP / SSH 服务器的端到端验收。

## 🔗 相关变更

- [PR #689：归档 1.7.5 更新日志并规范 Release 日志保存](https://github.com/joesdu/VelaShell/pull/689) · `2b9fb38c`、`468a3ac8`
- [PR #690：AI 助手标题栏入口与统一 Sparkles 图标](https://github.com/joesdu/VelaShell/pull/690) · `8b95d14a`
- [PR #691：主窗口 AI 助手专属区域与固定 / 悬浮交互](https://github.com/joesdu/VelaShell/pull/691) · `e9800959`
- [PR #692：聊天生成图与大图预览直接复制到剪贴板](https://github.com/joesdu/VelaShell/pull/692) · `141e4f1c`
- [PR #693：连接对话框读工作台变体，新增「先选类型 → 下一步」步骤](https://github.com/joesdu/VelaShell/pull/693)（感谢 @Cyaim）· `f75bb2ca`、`05e726dd`、`6a117007`、`0090be0e`、`224c713a`、`2452ba6d`
- [PR #694：优化聊天输入框并补齐 xhigh / max 思考档位](https://github.com/joesdu/VelaShell/pull/694) · `b92fa648`、`3b143a83`
- [PR #695：优化 AI 插件输入交互与作图工具发现](https://github.com/joesdu/VelaShell/pull/695) · `51d8c6d7`
- [PR #696：修复 macOS CI 的远程编辑等待与 AI 传输卡片竞态](https://github.com/joesdu/VelaShell/pull/696) · `28080715`、`347b6cc0`
- [PR #697：格式化代码，修正部分函数签名](https://github.com/joesdu/VelaShell/pull/697) · `58d44e1c`
- [PR #698：主机留空提示跟随变体标签，没有端点时收起跳板主机](https://github.com/joesdu/VelaShell/pull/698)（感谢 @Cyaim）· `56d6fffb`、`177c1a6c`、`6fbd17c2`、`c164cc8d`
- [PR #699：复审 #669 – #698 的安全与数据正确性修复](https://github.com/joesdu/VelaShell/pull/699) · `e1007bd7`、`2706df3f`、`a6eaabb1`、`db3f9f2c`、`d8556e80` 等，共 48 个提交
- [PR #700：清欠账一轮，含主密码第二步、远程转发混合端、ssh_config 分词与重连重读](https://github.com/joesdu/VelaShell/pull/700) · `0e73e5de`、`31dbb6df`、`1ada877b`、`2d2813bd`、`2c1247c4` 等，共 44 个提交
- [PR #701：记录 #700 合入前的 CI 修复](https://github.com/joesdu/VelaShell/pull/701) · `26b6c0c5`、`966c1bd0`
- [PR #702：新增捐赠者致谢名单](https://github.com/joesdu/VelaShell/pull/702) · `80a28307`、`96d0a1cb`
- [PR #703：修复连接对话框「系统密钥库」详情行的绑定错误](https://github.com/joesdu/VelaShell/pull/703) · `9b82bbe2`

**完整变更**：[1.7.5...1.7.6](https://github.com/joesdu/VelaShell/compare/1.7.5...1.7.6)
