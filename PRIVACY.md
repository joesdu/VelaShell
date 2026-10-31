# VelaShell Privacy Policy / 隐私政策

**Last updated / 最后更新:2026-10-25**

[English](#english) · [简体中文](#简体中文)

---

## English

### Summary

VelaShell is an open-source SSH and SFTP client that runs entirely on your own computer.

There is no account to create, no telemetry, no analytics, and no crash reporting. Nothing you
type, no host you connect to, and no file you transfer is ever sent to us.

The developer does operate **one** server that VelaShell talks to on its own: the news feed
behind the message centre (`feeds.easilynet.top`), which publishes security advisories and
product announcements. It is **on by default**, and VelaShell downloads a public JSON file from
it at startup and every few hours after that. The request carries nothing about you or your
installation — no account, no identifier, no version, no list of your hosts — so that server
learns only what any web server learns from a plain download: your IP address and the time.
Clearing the address in Settings → General → Message centre stops it completely: not "less
often", but never again.

If you turn on web search in the AI assistant (it is **off by default**), the search words the
model writes go through a second server the developer runs, a public SearXNG instance — see
item 8 below.

Every network connection VelaShell makes is listed in full below.

### Information we collect

**None.** We do not collect, store, transmit, sell, or share any personal information. The news
feed is a one-way download of a file that is identical for everyone: nothing is uploaded to it,
it has no user accounts, and it cannot tell one VelaShell installation from another.

### Information stored on your device

VelaShell stores the following locally so the application can work. This data never leaves your
computer unless you explicitly enable the optional sync feature described below.

| Data | Location |
| --- | --- |
| Connection profiles (host, port, username), groups, and settings | `~/.velashell` |
| Passwords and private-key passphrases you choose to save, including those of shared credentials (one username and password / key reused by many connections) and the network proxy password in your settings | Same folder, encrypted with AES-256 |
| The encryption key protecting the above | Generated on your device. On macOS it is kept in the login keychain and on Linux in the Secret Service (gnome-keyring, KWallet and the like); the data folder only holds entry ids. If the system keychain is unavailable it is kept in the data folder (plain, readable only by you). On Windows it is kept in the data folder, bound to your Windows account with DPAPI |
| Known SSH host keys | Same folder |
| Saved commands and snippets | Same folder |
| Window sizes and positions, plus the file panes' column widths and split ratio (one copy for all connections, not part of the settings) | Same folder |
| Layout templates, and — only when **Restore split layout** is on — the layout at exit: which saved connections and local shells were open and how the panes were arranged (no passwords; templates can travel with the optional cloud sync below, the layout at exit never does) | Same folder |
| Session logs — raw terminal output, **off by default** | `logs` subfolder, auto-deleted after a retention period you set |
| Diagnostic logs — application events, crash reports, and one line per ended SSH connection (`user@host:port`, when it connected, reason) | `logs` subfolder, auto-deleted after 7 days |
| Transfer records — one line per upload or download (local path, remote path, result; on by default, Settings → File transfer) — plus the external-editing log (`remote-edit.log`), the VcXsrv log (`vcxsrv.log`, Windows only) and each plugin's own log | `logs` subfolder (the transfer-record folder can be changed); transfer records are deleted after the retention period you set, `remote-edit.log` starts over at 1 MB, plugin logs rotate once at 1 MB |
| Audit log — connection results (`user@host:port`), host-key decisions, external logins, and changes to connection profiles (name and `user@host:port`), settings and shared credentials: which part changed and where the change came from (local edit, cloud sync, import, login prompt), never passwords or setting values | `~/.velashell`; records older than the retention period you set (default 180 days) are deleted at startup |
| Offline IP geolocation database, if you add one | `geoip` subfolder |
| Plugins you manually install | `~/.velashell/plugins` |

The application data folder is `~/.velashell` on Windows, macOS, and Linux. On first launch
after this location changed, VelaShell migrates the former platform-specific data directory
into this folder and removes the former directory after verification.

VelaShell also **reads** your existing OpenSSH configuration in `~/.ssh` (keys, `known_hosts`,
and — only when you run the session import — `config`) so that keys you already use with
OpenSSH, Git, and other tools work without being duplicated. These files are read from and
written to on your device only.

You can delete all of this at any time by removing the corresponding folder, or by using the
in-app controls under Settings or Plugin Manager. The encryption key kept in the macOS keychain or
the Linux Secret Service is not inside that folder: VelaShell only reads and writes the entries it
created there, and you can delete them in Keychain Access or Seahorse (search for VelaShell).

### Network connections

VelaShell connects to the network only in these situations:

1. **Servers you connect to.** SSH and SFTP sessions go directly from your computer to the
   host you specify. Credentials are sent only to that host, over the encrypted SSH channel.
   No traffic is proxied through us.

2. **Hosts you diagnose.** The ping and route-tracing tools send ICMP/UDP probes to the
   address you enter. IP geolocation is resolved **entirely offline** against a database file
   stored on your computer; no address is ever sent to a lookup service. The world map is drawn
   from vector data bundled inside the application — no online map tiles are requested.
   The **session health check** runs only when you start it (from a group or a multi-selection
   in the explorer, or from the Command Palette) — never on a timer. For each connection you
   picked it does what Connection Diagnostics does: a name lookup, a TCP connection, reading the
   SSH version string and — when the connection has saved credentials and *Verify login* is on —
   one login with those credentials that is closed right away. It goes only to those hosts.

3. **The news feed — on by default.** VelaShell downloads
   <https://feeds.easilynet.top/feed.json> (a public file, https only) when it starts and then
   once every few hours, and shows what it finds — security advisories and announcements — in
   the message centre. The request is a plain GET: it sends no account, no identifier, and no
   information about your machine or your connections, so the server sees only your IP address,
   the same as any website you visit. Which entries apply to your platform, language, and
   version is decided **on your computer**, after the whole file is downloaded. You can point
   the address at your own feed, or clear it in Settings → General → Message centre, in which
   case no request is ever sent.

4. **Update checks.** VelaShell asks GitHub for a release manifest when you click "Check for
   updates" in Settings, and also once at startup while "Check for updates at startup" is on
   (Settings → General; on by default) so that a new version can appear in the message centre.
   GitHub receives your IP address and the request, as with any website visit. If you also turn
   on "Download updates automatically" (Settings → General, right below it; **off by default**),
   a new version found by that startup check is downloaded from the same GitHub release in the
   background and verified against its SHA-256 checksum — one more download of the installer
   package, carrying nothing about you either. It is only installed when you click restart.
   Clicking "Check for Updates" on the About page downloads the update as before. **In the
   Microsoft Store version this feature is disabled entirely**, because the Store handles updates.

5. **Plugin update checks.** When you open the Plugin Manager window, VelaShell asks the plugin
   marketplace (<https://market.easilynet.top>, https only) whether the plugins you installed have
   newer versions. The request carries **the ids of the plugins you installed yourself** — bundled
   plugins and development-mounted ones are not included — because there is no way to ask whether
   they have updates without naming them. No account, no token, no install identifier: that server
   sees only your IP address. **This happens only when you open that window** — not at startup, not
   on a background timer. If you never open it, no request is ever made. A plugin package is
   downloaded only after you click Update, only over https, and only when the marketplace
   declares its SHA-256 digest, which is verified before it is installed.

6. **Contributor avatars.** Opening the About page loads contributor profile pictures from
   GitHub, which discloses your IP address to GitHub in the same way. If the request fails,
   placeholder initials are shown and nothing else is affected.

7. **Cloud sync — off by default, entirely optional.** If you enable it, VelaShell stores your
   settings, connection profiles, snippets, and layout templates in a **secret GitHub Gist in your own GitHub
   account**, using a personal access token that you supply. The data goes to your GitHub
   account, never to us. You may additionally set an end-to-end encryption passphrase, in which
   case the payload is encrypted with AES-GCM on your device before upload and GitHub stores
   only ciphertext. Without that passphrase, saved passwords and private-key passphrases are
   never uploaded — neither those on connection profiles nor those of shared credentials, nor
   the secret fields of plugin connections (such as an S3 session token); a
   shared credential travels only as its name, username, notes and key / certificate file
   paths, and the proxy password and alert webhook URL in your settings are left out as
   well. Layout templates never contain passwords either: only the profile Id each tab points
   to, how it opens, and the tab title at save time (often `user@host`). You choose which
   categories to sync, and you can turn it off or delete the Gist at any time.

8. **AI assistant web search — off by default.** The AI assistant plugin sends your questions,
   and the context you let it read, to the model provider **you** configure; nothing goes there
   until you add one. Separately, it can give the model two web tools, `web_search` and
   `web_fetch`, but only after you tick "Let the assistant search and read the web" in its global
   settings. With that on, the search words the model writes — in troubleshooting these are often
   error messages, host names, or internal service names — are sent to the SearXNG instance set
   right below that switch. **By default that is a public instance run by the developer,
   <https://searxng.easilynet.top>**, which therefore sees those search words and your IP address;
   it does not receive your conversation, your terminal output, or any identifier. You can point
   the setting at your own SearXNG instance, or clear it to turn searching off. `web_fetch`
   downloads the page the model names directly from your computer (private-network addresses
   are blocked unless you allow them). When the model provider offers its own server-side
   search and "prefer the provider's search" is on, the search happens on the provider's side
   instead and follows that provider's policy.

VelaShell does not download the IP geolocation database itself. If you want that optional
feature, the application opens the DB-IP download page in your browser and you choose the
downloaded file manually.

### Third parties

VelaShell has no third-party SDKs, trackers, or advertising libraries.

The only third parties that can receive anything are those you direct it to:

- **The SSH/SFTP servers you connect to**, which are yours or your organization's.
- **GitHub**, for update checks, About-page avatars, and optional cloud sync into your
  own account. See the [GitHub Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-privacy-statement).
- **DB-IP**, only if you choose to download their free offline database, and only through your
  own browser. See the [DB-IP privacy policy](https://db-ip.com/legal/privacy-policy).
- **The AI model provider you configure** in the AI assistant plugin (and, if you enable web
  search with provider-side search preferred, that provider's search), under that provider's
  own policy.

### Security

Saved passwords and passphrases are encrypted with AES-256 using a key generated on and stored
on your device. On macOS and Linux the key is held by the system keychain, so copying or backing up
the data folder does not carry it along (unless the keychain was unavailable and the key stayed in
the folder); on Windows it is bound to your account with DPAPI. Optional sync payloads can
additionally be end-to-end encrypted with AES-GCM using a passphrase only you know.

Please note that session logging, when you turn it on, records **raw terminal output**,
which may include anything displayed on screen during a session. These logs are plain files on
your disk. Keep this in mind before enabling the feature on a shared machine. By default, values that look
like passwords, tokens or private keys (for example `PASSWORD=…`, `Bearer …`, a PEM private key block) are
replaced with `***` before session logs and recordings are written (Settings → Security Audit); this is
best-effort pattern matching, not a guarantee, and it does not change what you see on screen.

“Export diagnostics…” on the About page first lets you tick what to include, then writes a zip
**only to the location you choose**; nothing is uploaded. The diagnostic logs, crash reports, a
version and platform summary and the plugin list are always included. Transfer records, the
external-editing log, the VcXsrv log and plugin logs contain local and remote paths and file names;
they are unticked by default and only included when you tick them. Settings, connection profiles,
session logs and credentials are never included. Before writing, VelaShell masks by rule: host and
user names that appear in your saved connections, recent connections and shared credentials; the
user and host in its own SSH disconnect log lines; IPv4 and IPv6 addresses other than loopback;
lowercase domain names ending in a common top-level domain or internal suffix (such as `.com`,
`.cn`, `.local`, `.internal`); the user name in `C:\Users\<name>`, `/home/<name>` and
`/Users/<name>` paths; your local account name, machine name and home folder; email addresses; and
password- or token-like values. It cannot recognise single-label host names that are not in those
sources (such as `db-prod-3`), user names elsewhere (such as chat-app user ids), file names, or other
path parts. Sending the file to anyone is up to you — open it and check it first.

“Export” under Settings → General → Import/export settings writes only the categories you tick, again only
to the file you choose. Network proxy and Security audit are unticked by default, and the proxy password
and the alert webhook URL are never written, whichever categories you tick.

No method of storage is perfectly secure, and VelaShell cannot protect data on a computer that
has already been compromised.

### Children

VelaShell is a developer tool and is not directed at children. We do not knowingly collect
information from anyone, including children.

### Changes

If this policy changes, the revised version will be published at this address with an updated
date above.

### Contact

VelaShell is open source. You can read every line of the code, including everything described
above, at <https://github.com/joesdu/VelaShell>.

Questions or concerns: <https://github.com/joesdu/VelaShell/issues>

---

## 简体中文

### 概述

VelaShell 是一款开源的 SSH / SFTP 客户端,完全运行在你自己的计算机上。

无需注册账号,没有遥测、没有数据分析、没有崩溃上报。你输入的内容、连接的主机、传输的文件,
都不会被发送给我们。

开发者确实运营着**一台** VelaShell 会主动访问的服务器:消息中心背后的资讯源
(`feeds.easilynet.top`),发布安全资讯与产品公告。它**默认开启** —— 启动时拉一次,
之后每隔几小时再拉一次,下载的是一个所有人都一样的公开 JSON 文件。请求里不带任何与你、
与你这台机器有关的东西(没有账号、没有标识符、没有版本号、没有你的主机列表),因此那台
服务器能知道的,和任何网站从一次普通下载里能知道的一样多:你的 IP 地址与访问时间。
在「设置 → 常规 → 消息中心」把地址清空即可彻底停止 —— 不是"少发一点",而是一个请求都不再发。

若你在 AI 助手里打开了网络检索(**默认关闭**),模型写的检索词会经过开发者运营的第二台服务器 ——
一个公共 SearXNG 实例,见下文第 8 条。

VelaShell 发起的每一个网络连接,都完整列在下方。

### 我们收集的信息

**没有。** 我们不收集、不存储、不传输、不出售、不共享任何个人信息。资讯源是**单向下载**
一份对所有人完全相同的文件:没有任何东西被上传给它,它没有用户账号,也分不出两个 VelaShell
安装之间的区别。

### 存储在你设备上的信息

以下数据保存在本地以支撑应用运行。除非你主动启用下文所述的可选同步功能,否则它们不会离开
你的计算机。

| 数据 | 位置 |
| --- | --- |
| 连接配置(主机、端口、用户名)、分组与应用设置 | `~/.velashell` |
| 你选择保存的密码与私钥口令,包括共享凭据(多条连接共用的一套用户名与密码 / 密钥)里的,以及设置里的网络代理密码 | 同上,以 AES-256 加密 |
| 保护上述内容的加密密钥 | 在你的设备上生成。macOS 上存进登录钥匙串,Linux 上存进 Secret Service(gnome-keyring、KWallet 等),数据目录里只留条目编号;系统密钥链用不了时存在数据目录里(明文,仅本人可读)。Windows 上存在数据目录里,以 DPAPI 绑定当前 Windows 用户 |
| 已知的 SSH 主机密钥 | 同上 |
| 快捷命令与代码片段 | 同上 |
| 窗口的尺寸与位置,以及文件栏的列宽与分栏比例(所有连接共用一份,不在设置里) | 同上 |
| 布局模板,以及仅在开启「按上次的分屏恢复」时记下的退出时布局:开着哪些已保存的连接与本地 shell、窗格怎么排(不含密码;模板可随下文的可选云同步上传,退出时布局从不上传) | 同上 |
| 会话日志 —— 终端原始输出,**默认关闭** | `logs` 子目录,按你设定的保留天数自动清理 |
| 诊断日志 —— 应用事件、崩溃记录,以及每条 SSH 连接结束时的一行(`用户@主机:端口`、连上的时刻、原因) | `logs` 子目录,7 天后自动清理 |
| 传输记录 —— 每次上传、下载一行(本机路径、远端路径、结果;默认开,设置 → 文件传输),以及外部编辑日志(`remote-edit.log`)、VcXsrv 日志(`vcxsrv.log`,仅 Windows)与各插件自己的日志 | `logs` 子目录(传输记录的目录可改);传输记录按你设定的保留天数清理,`remote-edit.log` 满 1 MB 重写,插件日志满 1 MB 轮转一份 |
| 审计日志 —— 连接成败(`用户@主机:端口`)、主机指纹的裁决、外部拉起的登录,以及连接配置(名称与 `用户@主机:端口`)、设置、共享凭据的改动:只记改了哪一块与改动的来源(本机编辑、云同步、导入、登录框),不记密码与设置的值 | `~/.velashell`;启动时删掉超过保留期(默认 180 天,可设)的记录 |
| 离线 IP 归属地数据库(如果你添加了) | `geoip` 子目录 |
| 你手动安装的插件 | `~/.velashell/plugins` |

应用数据目录在 Windows、macOS 与 Linux 上统一为 `~/.velashell`。位置变更后的首次启动会把
原平台数据目录迁入此目录，校验成功后删除原目录。

VelaShell 还会**读取**你既有的 OpenSSH 配置(`~/.ssh` 下的密钥、`known_hosts`,以及仅在你执行
会话导入时读取的 `config`),使你已经在 OpenSSH、Git 等工具中配置好的密钥无需重复配置即可使用。
这些文件的读写全部发生在你的设备上。

你可以随时删除对应目录,或通过设置、插件管理器中的相应功能清除这些数据。macOS 钥匙串与 Linux
Secret Service 里的那把加密密钥不在这个目录里:VelaShell 在那里只读写它自己创建的条目,
可在「钥匙串访问」或 Seahorse 里搜 VelaShell 删除。

### 网络连接

VelaShell 仅在以下情形联网:

1. **你所连接的服务器。** SSH 与 SFTP 会话由你的计算机直连你指定的主机。凭据只通过加密的
   SSH 通道发送给该主机。没有任何流量经我们中转。

2. **你所诊断的主机。** Ping 与路由追踪向你输入的地址发送 ICMP/UDP 探测包。IP 归属地查询
   **完全离线**,基于保存在你计算机上的数据库文件完成,任何地址都不会被发往查询服务。
   世界地图由应用内置的矢量数据绘制,不请求任何在线地图瓦片。
   **会话健康巡检**只在你点开时运行(资源管理器的分组或多选菜单、命令面板),从不定时运行。
   它对你选中的每条连接做的事与「连接诊断」相同:解析主机名、建立 TCP 连接、读取 SSH 版本串;
   连接保存了凭据且开着「验证登录」时,再用这份凭据登录一次并随即断开。它只连这些主机。

3. **资讯源 —— 默认开启。** VelaShell 在启动时以及此后每隔几小时,下载一次
   <https://feeds.easilynet.top/feed.json>(公开文件,仅 https),把其中的安全资讯与公告
   显示在消息中心。请求是一次普通的 GET:不带账号、不带标识符,也不带任何关于你这台机器
   或你的连接的信息,那台服务器看到的只有你的 IP 地址 —— 与你访问任何网站时一样。
   哪些条目适用于你的平台、语言与版本,是在**你的计算机上**、把整份文件下载完之后才判定的。
   你可以把地址换成自行部署的资讯源,也可以在「设置 → 常规 → 消息中心」把它清空,清空后一个请求
   都不会发出。

4. **检查更新。** 你在设置中点击"检查更新"时,以及"启动时检查更新"处于开启状态时
   (「设置 → 常规」,默认开启)启动时的那一次,VelaShell 会向 GitHub 请求发布清单,
   以便有新版本时投一条消息到消息中心。与访问任何网站一样,GitHub 会收到你的 IP 地址与
   该请求。如果你另外打开了"自动下载更新"(「设置 → 常规」,就在它下面;**默认关闭**),
   启动检查发现的新版本会在后台从同一个 GitHub Release 下载、并按 SHA-256 校验 —— 多出的
   只是一次安装包下载,同样不带任何关于你的信息;只有你点了重启才会安装。在"关于"页点
   "检查更新"照旧会接着下载。**Microsoft Store 版本完全禁用了该功能**,更新由商店接管。

5. **插件更新检查。** 打开「插件管理」窗口时,VelaShell 会向插件商店
   (<https://market.easilynet.top>,仅 https)问一次:你装的这几个插件有没有新版本。
   请求里带的是**你自己安装的那些插件的 id** —— 应用自带的与开发期挂载的不在其中 ——
   因为"它们有没有新版"这个问题,没办法在不说出是哪几个插件的前提下问出口。
   不带账号、不带令牌、不带任何安装标识符,那台服务器看到的只有你的 IP 地址。
   **只在你打开那个窗口时发生** —— 不在启动时,也不在后台定时发生;你不打开它,
   一个请求都不会发出。插件包只有在你点了「更新」之后才会下载,只走 https,而且商店必须声明它的 SHA-256 摘要(没给就不下),装之前核对。

6. **贡献者头像。** 打开"关于"页面会从 GitHub 加载贡献者头像,同样会向 GitHub 暴露你的
   IP 地址。请求失败时显示首字母占位图,不影响其他功能。

7. **云同步 —— 默认关闭,完全可选。** 若你启用,VelaShell 会使用你自己提供的个人访问令牌,
   把设置、连接配置、代码片段与布局模板保存到**你自己 GitHub 账户下的一个 secret Gist** 中。数据进入
   的是你的 GitHub 账户,而非我们。你还可以额外设置端到端加密口令,此时载荷会在你的设备上
   用 AES-GCM 加密后再上传,GitHub 只能拿到密文。没设这个口令时,已保存的密码与私钥口令一律
   不上传 —— 连接配置上的与共享凭据里的都一样,插件连接的机密字段(如 S3 的会话令牌)也不带;共享凭据只带名称、用户名、备注与私钥 / 证书
   文件路径;设置里的代理密码与告警 Webhook 地址同样不上传。布局模板本来就不含密码,只有每个
   标签指向的连接配置 Id、打开方式与保存时的标签标题(常常就是 `用户@主机`)。同步范围由你勾选,
   可随时关闭或删除该 Gist。

8. **AI 助手的网络检索 —— 默认关闭。** AI 助手插件把你的提问、以及你允许它读取的上下文,
   发给**你自己**配置的模型供应商;一家都没配之前什么都不发。另外,它能给模型两个网络工具
   `web_search` 与 `web_fetch`,但只有在你于它的全局设置里勾上「允许助手检索并阅读网页」之后才有。
   打开后,模型写的检索词 —— 排障时常常是报错原文、主机名或内网服务名 —— 会发到那个开关下方
   设置的 SearXNG 实例。**默认是开发者运营的公共实例 <https://searxng.easilynet.top>**,
   它因此能看到这些检索词与你的 IP 地址;它收不到你的对话、终端输出或任何标识符。你可以把
   这一项换成自行部署的 SearXNG 实例,或清空以关闭检索。`web_fetch` 从你的计算机直接去取
   模型给出的网页(私网地址默认挡住,除非你放行)。模型供应商自带服务端检索、且勾了「优先用
   供应商自带的检索」时,检索改在供应商那边进行,遵循该供应商的政策。

VelaShell 不会自行下载 IP 归属地数据库。若你需要该可选功能,应用会在你的浏览器中打开 DB-IP
的下载页面,由你手动选择下载好的文件。

### 第三方

VelaShell 不含任何第三方 SDK、追踪器或广告库。

唯一可能接收到信息的第三方,都是由你指定的:

- **你连接的 SSH/SFTP 服务器**,它们属于你或你的组织。
- **GitHub**,用于检查更新、"关于"页头像,以及同步到你自己账户的可选云同步。参见
  [GitHub 隐私声明](https://docs.github.com/site-policy/privacy-policies/github-privacy-statement)。
- **DB-IP**,仅当你选择下载其免费离线数据库时,且全程通过你自己的浏览器。参见
  [DB-IP 隐私政策](https://db-ip.com/legal/privacy-policy)。
- **你在 AI 助手插件里配置的模型供应商**(以及打开网络检索且优先用供应商检索时,该供应商的检索),
  遵循该供应商自己的政策。

### 安全

已保存的密码与口令使用 AES-256 加密,密钥在你的设备上生成并保存在本地。macOS / Linux 上这把密钥
由系统密钥链保管,复制或备份数据目录不会带走它(密钥链用不了、密钥留在数据目录里时除外);
Windows 上它以 DPAPI 绑定当前用户。可选的同步载荷还可使用只有你知道的口令做 AES-GCM 端到端加密。

请注意:会话日志功能一旦开启,会记录**终端原始输出**,其中可能包含会话期间显示在屏幕上的
任何内容。这些日志是磁盘上的普通文件。在共用计算机上启用该功能前请考虑这一点。默认情况下,会话日志与录制
落盘前会把形如口令、令牌或私钥的值(比如 `PASSWORD=…`、`Bearer …`、PEM 私钥块)换成 `***`(设置 → 安全审计);
这是按规则匹配、尽力而为,不是保证,也不改变屏幕上显示的内容。

关于页的「导出诊断包…」先让你勾选要收哪些,再打成 zip,**只写到你选的位置**,不会上传。诊断日志、崩溃记录、
版本与平台概况和插件清单总会收进包;传输记录、外部编辑日志、VcXsrv 日志与插件日志里有本机与远端的路径和文件名,
默认不勾,勾上了才收。设置、连接配置、会话日志与凭据都不收。写入前按规则遮盖:你的已保存连接、最近连接与共享凭据里
出现过的主机名与用户名;VelaShell 自己的 SSH 断开日志行里的用户与主机;回环以外的 IPv4 与 IPv6 地址;小写书写、
以常见顶级域或内网后缀(如 `.com`、`.cn`、`.local`、`.internal`)结尾的域名;`C:\Users\<名>`、`/home/<名>`、
`/Users/<名>` 路径里的用户名;本机账户名、机器名与主目录;邮箱地址;形如口令或令牌的值。不在上述来源里的单段主机名
(如 `db-prod-3`)、其它位置的用户名(如聊天软件的用户 id)、文件名与其余路径认不出来。要不要发给别人由你决定,
发之前请先打开检查。

设置 → 常规 →「设置导入/导出」的「导出」只把你勾选的分类写进你选的文件;网络代理与安全审计默认不勾,
代理密码与告警 Webhook 地址无论勾选什么都不写进去。

没有任何存储方式是绝对安全的;对于一台已被入侵的计算机,VelaShell 无法保护其上的数据。

### 儿童

VelaShell 是开发者工具,并非面向儿童。我们不会在知情的情况下收集任何人(包括儿童)的信息。

### 变更

本政策如有修订,将在此地址发布新版本,并更新页首的日期。

### 联系方式

VelaShell 是开源软件。上述所有行为对应的每一行代码都可以在
<https://github.com/joesdu/VelaShell> 查阅。

疑问或建议请提交至 <https://github.com/joesdu/VelaShell/issues>
