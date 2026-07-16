# VelaShell-zh.pen 逐帧精确规格（Pencil 提取，UI 重建唯一依据）

> 由 Pencil MCP 从设计稿节点树逐层提取。所有颜色为 token 名（见 `docs/交互与界面规格.md` §1.1）。
> 字体：JetBrains Mono = mono，Inter = ui。图标库 lucide（描边 2、圆角端点，按 24×24 视箱缩放）。
>
> ⚠️ **实现差异说明**：本文档为设计稿原始提取。实现中窗口为**自绘无边框**（`WindowDecorations="None"`，非原生标题栏），
> 由 `TitleBarView` 自绘 36px 标题栏：左 logo+产品名，右 功能图标组 + 自绘 最小化/最大化/关闭 三枚窗口按钮；
> 且下方「菜单栏」中的 6 个**文字菜单项（会话/编辑/操作/搜索/工具/帮助）已整体移除**（与命令面板功能重复，产品决策），
> 右侧功能图标组中「广播 send」已实现为多终端输入栏开关；「组同步 link-2」仍为未实现禁用态（半透明）。其余布局与 token 仍为 UI 重建依据。

## 菜单栏 `TSiDh`（36px，全宽）
- 容器：`bg-sidebar` 底，底边 1px `border-primary`，padding [0,12]，space-between，垂直居中。
- 左 `DaZfB`（gap 2）：6 个菜单项（会话/编辑/操作/搜索/工具/帮助）。
  - 每项：frame padding [0,10]，cornerRadius 3，高度撑满；文字 Inter 12 **weight 500** `text-secondary`。hover `bg-hover`。
- 右 `r3RUzf`→`GQQwj`（gap 4）：7 个图标按钮 **24×22**，cornerRadius 3，图标 **12px**：
  | 按钮 | lucide | 默认色 | 备注 |
  |---|---|---|---|
  | taSearch | search | text-muted | 终端搜索 |
  | taCopy | copy | text-muted | 复制 |
  | taSplit | columns-2 | text-muted | 分屏 |
  | taTunnel | route | text-muted | 隧道 |
  | taQuickCmd | zap | text-muted | 命令面板 |
  | taSyncGroup | link-2 | **accent**，容器 fill `bg-active` | 组同步激活态示例 |
  | taBroadcast | send | text-tertiary | 广播输入 |

## 标签栏 `nunbT`（36px，`bg-page`，children 底对齐 alignItems:end）
- **激活标签 `h0Nv0`**：高 32，`bg-terminal` 底，**顶边 2px `accent`**（inner），padding [0,14]，gap 8：
  - 状态点 ellipse 7×7 `status-connected`；名 JB Mono 11 **500** `text-primary`；x 图标 12 `text-tertiary`。
- **非激活标签**：高 32，`tab-inactive-bg` 底，无顶线；名 JB Mono 11 normal `text-tertiary`；x 12 `text-muted`。
- **tabAdd `9BYBb`**：32×32 居中，plus 14 `text-tertiary`。
- spacer fill_container。
- **溢出组 `pZGS4`**：padding [0,6] gap 2，高 32；3 个按钮 24×24 cornerRadius 3：chevron-left / chevron-right / chevron-down，各 14 `text-tertiary`。

## 状态栏 `gzmsb`（24px，全宽，`bg-sidebar`，顶边 1px `border-primary`，padding [0,12]，space-between）
- 左 `1y2au`（gap 14）：wifi 12 `status-connected`；`SSH • web-prod-01:22` JB Mono 10 `text-secondary`；分隔 1×12 `border-secondary`；`Latency: 12ms` JB Mono 10 **accent**；分隔；`↑ 2h 34m` JB Mono 10 `text-tertiary`。
- 右 `4Cio1`（gap 12）：`xterm-256color` JB Mono 10 `text-muted`；分隔；`120×36` muted；分隔；cpu 图标 11 `text-tertiary`+`23%` JB Mono 10 `text-secondary`（gap 4）；memory-stick 11+`1.2G`；arrow-up-down 11+`4.2 MB/s`；分隔；`UTF-8` muted。

## 侧边栏 `aMaSq`（260px，`bg-sidebar`，右边 1px `border-primary`）
1. **工具栏 `cnUAB`** 36px，底边 1px，padding [0,12]，space-between：
   - 「资源管理器」JB Mono 11 500 letterSpacing 1 `text-secondary`；
   - 右 gap 4：两个 24×24 cornerRadius 3 按钮：plus 13、ellipsis 13，均 `text-tertiary`。
2. **会话树 `FrJPu`** fill，padding [8,0] gap 2：
   - 分组行 30px padding [0,12] gap 6：chevron-down 12 `text-tertiary` + folder 13 `warning` + 名 JB Mono 12 500 `text-primary` + 计数 JB Mono 10 `text-tertiary`。
   - 主机行 28px padding [0,12,0,36] gap 8：状态点 7×7 + 名 JB Mono 11（激活：**accent** 500 + 行底 `bg-active` + 标签「活跃」badge：`accent-dim` 底 cornerRadius 2 padding [1,6] 文字 JB Mono 9 500 accent；普通：`text-secondary` normal）。
3. **快速连接区 `XcIor`** 320px 顶边 1px：
   - 头 `DdINU` 36px padding[0,12] 底边 1px：「快速连接」JB Mono 11 500 ls1 `text-secondary` + 右 history 按钮 24×24。
   - 输入框 `wIHgo` 32px `bg-input` padding [0,12] gap 8：terminal 图标 13 `text-tertiary` + 占位「用户名@主机名:端口」JB Mono 11 `text-muted`。
   - 「// 最近连接」24px padding[0,12] JB Mono 10 `text-muted`。
   - 最近行 ×3 32px padding[0,12] gap 8：timer 12 `text-tertiary` + 竖排(gap1)：`root@192.168.1.100` JB Mono 11 `text-secondary` / `2 小时前` JB Mono 9 `text-muted`。
4. **底部用户栏 `t6hT9`** 40px 顶边 1px padding [0,12] space-between：
   - 左 gap 8：头像 22×22 圆(cornerRadius 11) `accent-dim` 内 user 12 accent(置于 5,5) + `root` JB Mono 11 `text-secondary`。
   - 右 gap 6：bell 13 / settings 13，24×24 cornerRadius 3，`text-tertiary`。

## 终端区 `QzoMC`（`bg-terminal`，padding [10,0,0,0]）
- **工具栏 `BdPtF`** 28px padding [0,14] space-between：左 `IGfp7` gap 12：
  - `root@web-prod-01:~` JB Mono 11 500 **accent**；`uptime: 42d 7h 23m` JB Mono 10 `text-muted`；`|` muted；`latency: 12ms` JB Mono 10 `status-connected`。

## 文件区 `dyuii`（220px，顶边 1px）
1. **头 `cKZr7`** 36px padding [0,14] 底边 1px space-between：
   - 左 gap 8：folder-open 14 **accent** + `/var/www/html` JB Mono 11 500 `text-primary`。
   - 右 gap 4：上传按钮（`accent-dim` 底 cornerRadius 3 高 24 padding [0,8] gap 4：upload 12 accent + 「上传」JB Mono 10 500 accent）+ 刷新 24×24（refresh-cw 图标）。
2. **列头 `3vU7e`** 26px `bg-surface` padding [0,14]：文件名(280)/大小(100)/权限(120)/修改时间，JB Mono 10 500 letterSpacing 1 `text-muted`。
3. **列表行** 28px padding [0,14] 底边 1px `border-primary`（`..` 行 `bg-hover` 无底边）：
   - 图标 13（folder=`warning`、corner-left-up=`text-tertiary`）+ 名（前置两空格）JB Mono 11（目录 `info`、`..` `text-secondary`）宽262 + 大小 11 `text-tertiary` 宽100 + 权限 宽120 + 时间。

## 窗口结构（§2 权威）
- **自绘无边框窗口**（`WindowDecorations="None"`）：自绘标题栏 36（左 logo+名称 · 右 功能图标组 GQQwj + 最小化/最大化/关闭）→ (侧栏 260 ‖ 右区) → 状态栏 24。**无独立菜单栏行**（原「菜单栏」的文字菜单已移除，功能图标组并入标题栏）。
- 右区：标签栏 36 → 终端(fill，含可选 行号/时间侧栏) → 文件区 220。

## 浮层（后续）
- 命令面板 `FN5dM` 560px、传输组件 `9Ralg` 280px、隧道 `fuXS7` 320px、资源监控 `EP3Gd` 280px(padding12 gap8)、右键菜单 `e6klM` 200px(padding[4,0])。
- 小浮层：cornerRadius 6 + `bg-surface` + 1px `border-secondary`(outer) + 阴影 blur16 #00000060 y+4。
- 大弹窗：cornerRadius 8 + 阴影 blur32 #00000080 y+8（设置 720、新建连接 500、密码验证 420）。
