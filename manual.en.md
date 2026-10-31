# VelaShell User Manual

[简体中文](manual.md) · **English**

<a id="scope"></a>
## 1. Scope and How to Use This Manual

This manual is for users managing servers, working in terminals, and transferring files with VelaShell. It does not cover building the application, developing plugins, or deploying servers. The content was checked against the current workspace source code and locally available official documentation on **2026-10-20**. It is not an all-platform acceptance-test report. Your operating system, distribution package, server permissions, installed plugins, and customized shortcuts affect which features are available.

- Menu paths use English interface terminology. Some title-bar actions are icon-only: hover to read their tooltips. If you cannot find an action, search for it in the **Command Palette, `Ctrl+P`**.
- A **connection profile** is saved host information in Explorer; a **session** is an actual connection; a **tab** hosts a terminal or file document. Editing a profile generally does not change an established session. Reconnect to use new connection parameters.
- **SSH, SFTP, and FTP/FTPS are different protocols.** An SSH terminal runs an interactive shell on the server. SFTP operates on files through the SSH file subsystem. FTP/FTPS uses a separate file protocol. Opening a file pane does not mean every command runs in that directory or on that server.
- **Read the warning before acting.** Risks are called out before synchronization with deletion, Agent forwarding, public listeners, secret exports, and AI write operations. `example.invalid`, `192.0.2.10`, `2001:db8::10`, and `demo` are placeholders; replace them with your own targets. Never put production passwords in demonstrations, screenshots, or chats.
- This file is delivered in the location requested by the user; it does not change the project's policy of maintaining formal documentation in `velashell-docs`. Historical designs, plans, and README descriptions do not automatically mean that a feature is connected to the current interface. Known limitations are listed at the end.

### Contents

1. [Scope and How to Use This Manual](#scope)
2. [Installation, First Launch, Updates, and Your First SSH Connection](#start)
3. [Main Window and Entry-Point Reference](#navigation)
4. [Connection Management, Import, and Export](#connections)
5. [Authentication, Keys, and Shared Credentials](#authentication)
6. [Proxies, Jump Hosts, Keepalive, and Reconnection](#network)
7. [Working with the Terminal](#terminal)
8. [Docking, Synchronized Input, and Quick Commands](#workspace)
9. [SFTP/FTP Transfers, Editing, and Directory Bookmarks](#files)
10. [Directory Comparison and Synchronization](#sync)
11. [SSH Tunnels](#tunnels)
12. [X11 Forwarding and X Server](#x11)
13. [Resource Monitoring, Processes, Route Tracing, and Diagnostics](#operations)
14. [Logs, Recording, and Playback](#recordings)
15. [Settings: All Available Pages](#settings)
16. [Security, Host Fingerprints, and Auditing](#security)
17. [Gist Sync, Backup, and Migration](#backup)
18. [Plugin Management, Optional Protocols, and Docker](#plugins)
19. [AI Providers, Chat, Agent, MCP, and IM](#ai)
20. [Message Center, Desktop Integration, and External Launch](#desktop)
21. [Common Tasks and Troubleshooting](#troubleshooting)
22. [Keyboard Shortcuts and Mouse Gestures](#shortcuts)
23. [Explicit Limitations and Reference Index](#limits)

<a id="start"></a>
## 2. Installation, First Launch, Updates, and Your First SSH Connection

### 2.1 Choose a Distribution Package

**Entry point and prerequisites:** Obtain a package matching your operating system and CPU architecture from [GitHub Releases](https://github.com/joesdu/VelaShell/releases) or the official Microsoft Store entry. Windows offers x64/arm64 portable zip packages and a Microsoft Store edition; Linux offers x64/arm64 tar.gz, AppImage, deb/rpm packages; macOS offers x64/arm64 tar.gz and dmg packages. Packages include the runtime. End users do not need the .NET SDK.

**Steps:** Extract a portable package or install the selected package format, then start VelaShell. Put a portable copy somewhere writable by your user and not subject to temporary-directory cleanup. Unsigned or unnotarized macOS packages may be blocked by the system: verify the source and downloaded file first, rather than disabling system protection globally for an unknown application. Install and update the Store edition through the Store.

**Result and how it works:** Profiles, settings, and encrypted credentials are stored locally. Portable does not mean that all personal data is beside the executable. Regular distributions use `~/.velashell` in your home directory by default. SSH keys may also be in `~/.ssh` or another path specified in a profile. Store data is subject to system redirection and is not automatically shared with the portable edition.

**Caution:** Finish important transfers, save remotely edited files, and finish sensitive command activity before updating. Copying the application directory alone is not a backup of your personal data.

### 2.2 Recommended First-Launch Setup

**Entry point:** **Settings (`Ctrl+,`)**.

1. In **General → Language & Region**, select a language or follow the system language.
2. Choose a theme and UI font in **Appearance**; check the monospaced font, size, and encoding in **Terminal**.
3. Decide whether to remember passwords under **General → Privacy & Security**. Review fingerprint policy and on-disk secret masking under **Security Audit**.
4. Review the feed under **General → Message Center**. Clear its URL if you do not want requests to that source. Startup update checks can be disabled separately.
5. Configure **Network Proxy** first if your organization requires a proxy.
6. Save settings. Recreate the affected connection, restart X Server, or restart the application where the page says changes apply on the next connection or startup.

**Result and how it works:** Settings establish host and new-session defaults. Supported connection-specific overrides take precedence. Feeds, update checks, cloud sync, plugin-marketplace checks, and AI providers are separate network paths; turning off one does not disable all of them.

### 2.3 Make Your First SSH Connection

**Prerequisites:** A reachable SSH server, host name or IP, port, login user, and password or private key. Obtain the server's SSH host fingerprint from an administrator or another trusted channel. Installing the client does not create a server you are authorized to access.

**Safety first:** A fingerprint is not trustworthy merely because this is your first connection. Verify its SHA-256 value through an independent channel. Investigate changes before accepting them.

1. Click **+** at the top of Explorer or press `Ctrl+N`.
2. Select **SSH**. Enter a name, host, port (commonly 22), and username. For example: name `Test server`, host `192.0.2.10`, user `demo`.
3. Choose an authentication method. For password authentication, enter the password. For private-key authentication, select a local private-key file and supply its passphrase if required. A `.pub` public key is not a private key.
4. Select a group if needed; keep other defaults initially. **Test** checks the connection, but a successful test is not an established, long-lived interactive terminal.
5. Choose **Save** to save the profile or **Connect** to begin a session. Handle first-use fingerprint confirmation and any multifactor authentication challenge.
6. Once the remote shell prompt appears, use read-only commands such as `pwd` and `hostname` to confirm the target before working.
7. When finished, run `exit` remotely or close the tab. Check the target before approving any close confirmation.

**Result:** An SSH terminal tab opens, and the saved host remains in Explorer for subsequent double-click access.

**How it works and cautions:** SSH verifies server identity, authenticates the user, and allocates a remote pseudo-terminal. The profile name, client-side username field, and remote prompt are not equivalent evidence of identity. Rely on the verified fingerprint and actual remote identity. If connection fails, use [connection diagnostics](#operations) to identify the stage instead of repeatedly changing trust policy.

### 2.4 Check for and Install Updates

**Entry point:** **Settings → About**. Startup checking is controlled in **General**. Depending on update state, About also offers actions to restart and finish an update, repair an update, open logs, and export diagnostics.

**Steps and result:** Run **Check for updates**. When a newer version exists, supported writable Windows/macOS distributions start downloading right away, check the SHA-256 recorded in the same release's `latest.json` (an integrity check, not a signature check), then exit and replace the application. If manual installation is requested, obtain the package from the release entry point. **Linux currently checks versions only; download and install updates manually:** when VelaShell can tell how it was installed (deb, rpm, AppImage, or the portable tar.gz), the browser opens the download of that one package; otherwise (or for pre-release deb/rpm) it opens the release page. **The Microsoft Store edition is updated by the Store.**

**Background download:** **Settings → General → Download updates automatically** (off by default; it needs **Check for updates on startup** on and only appears on the writable Windows/macOS installs described above). When on, a newer version found by the startup check is downloaded in the background and verified against the same SHA-256; the message center then says it is ready, and **Restart & update** on About finishes the job. It never restarts by itself. A failed background download shows no error: the message center falls back to the plain "update available" entry, and the next startup check tries again.

**How it works and cautions:** Finishing a download does not switch the running process to the new version; complete the exit/restart. Application files and user data are separate, but back up before updating. Do not rely on older descriptions of user-selectable update channels that the current interface does not provide.

<a id="navigation"></a>
## 3. Main Window and Entry-Point Reference

The main window contains a title bar, Explorer, a docking document area, and a bottom status bar. Explorer is normally on the left and can be moved to the right in Appearance. The central area can host terminals and independent dual-pane file documents. Each split pane has its own tab strip.

### 3.1 Find a Feature by Task

| I want to… | Preferred entry point | Prerequisite or result |
| --- | --- | --- |
| Add or edit a host | Explorer **+**; connection context menu **Edit** | Opens profile configuration; does not immediately alter a live session |
| Connect quickly or reopen a recent target | Quick Connect / recent connections | Accepts forms such as `demo@host:22`; authentication and fingerprint checks still apply |
| Reopen a set of machines in a saved split layout | Title-bar **Layout templates** icon; search the template name in the Command Palette | Save the arranged panes as a template first; templates refer to saved connections only, see 8.4 |
| Find an application action | Command Palette, `Ctrl+P` | Search, select with arrow keys, execute with Enter |
| Open a local terminal | Command Palette: open local terminal for a detected shell | Windows only; installed shells are listed; commands run locally, not on the current SSH server |
| Show the SSH side file pane | `Ctrl+Shift+F` | The active session must support file services |
| Open local/remote dual panes | A connection's file-document action or a new SFTP/FTP connection | Independent file tab with comparison, sync, and bookmark controls |
| Copy between two servers | Ctrl-select two file connections → **Open in dual-pane SFTP** | Relayed through this computer; FTP/FTPS sources use streaming commit or full caching according to target capabilities, not server-direct transfer |
| Manage SSH forwarding | Title-bar tunnel icon; `Ctrl+Shift+T` | Uses saved SSH profiles independently of terminal tabs |
| Manage processes or monitor resources | Corresponding title-bar actions; resource metrics also from the status bar | Connected SSH session; supported remote sampling commands |
| Trace a route | Title-bar route action / Command Palette | Probes originate locally, not from remote traceroute |
| See which of a batch of hosts are unreachable | Group menu **Health-Check This Group**; multi-selection menu **Health-Check N Connections**; Command Palette **Session Health Check (All Connections)** | Runs only when you start it; opens no terminal and no dialog, see 13.4 |
| Start or stop X Server | Title-bar X Server icon | Used together with a connection's X11-forwarding setting |
| View recordings | Settings → Security Audit → playback center | Only previously recorded sessions are available |
| Manage quick commands | Settings → Snippets; sidebar Quick Commands panel | Organize, edit, and send snippets |
| View notifications | Sidebar bell → Message Center | Updates, news, and security/runtime warnings |
| Install extensions | Plugin Manager | Install a trusted `.vpx` or obtain it through the marketplace entry |
| Use AI | Sparkles button at the top right of the main window; or search AI in the Command Palette | AI is bundled; model access requires configuration |
| Connect IM or an external Agent | Command Palette → AI collaboration integration | The opposite direction from adding MCP tool servers to the AI |

**Usage:** If an icon is unavailable, first check focus and session type. An FTP tab does not provide SSH process management; a disconnected tab does not supply live resource metrics. The Command Palette executes application actions, not arbitrary remote shell input.

<a id="connections"></a>
## 4. Connection Management, Import, and Export

### 4.1 Create, Save, and Edit Profiles

**Entry point:** Explorer **+**, a connection's **Edit** context-menu item, or the new-connection shortcut.

**Prerequisites and steps:** Choose SSH, SFTP, FTP, or a protocol supplied by an installed plugin. Enter the host, port, username, and protocol-specific fields; optionally set the name, group, tags, notes, tab icon, jump host, and initial paths. The tab icon is picked under **General → Organize**; leave it unset to follow the protocol. A connection with a picked icon shows it on its tabs and on its Explorer row. Recheck port and authentication when changing protocol: SSH private-key authentication cannot simply be applied to FTP. **Test**, **Save**, and **Connect** check, persist, and start a session respectively. Correct invalid host syntax or missing authentication material when an action is unavailable.

**Protocols with several connection types (for example databases: MySQL / PostgreSQL / SQLite):** a new connection first shows a screen of type cards, with the default type already selected. Click a card to select it, then **Next** to open the form; double-clicking a card selects it and moves on in one step. With the keyboard, Tab moves between cards, Space only selects, and Enter selects and moves on to the form. **Back** at the bottom of the form returns to the cards without clearing anything you entered. Editing a saved connection opens the form directly, with the type shown at the top; click **Change** next to it to pick another type. The port, the label of the host field (and the hint shown when it is left empty), and whether a username and password are needed all follow the selected type — a file-based database, for instance, only asks for the file path, with no port, account or jump host (a jump host you picked earlier is not cleared, and is back when you switch to a type with a port; a username and password entered earlier are not saved with such a connection, and opening it never asks you to sign in). A port still at the previous type's default moves to the new type's default; a port you changed yourself stays as it is. A plugin can also add tabs of its own between **General** and **Advanced** for its own settings, and the type cards show the icon the plugin gives each type (a type without one shows the plugin's icon).

**Result and how it works:** The profile is stored locally; connecting creates the appropriate protocol session. SSH/SFTP targets may be host names, IPv4, or IPv6. Where one field contains both IPv6 and a port, use `[2001:db8::10]:2222`, rather than treating the last numeric segment as the port.

**Caution:** Cloning creates another profile/session; later edits do not automatically propagate. Authentication material is shared only through an explicit shared-credential reference. Changing a host while keeping an old password is particularly sensitive: verify that the new target should receive that password.

### 4.2 Groups, Multiple Selection, and Bulk Actions

**Entry point:** Connection and group context menus.

- Double-click a connection to open it. Use its context menu to edit, copy, pin, move, or delete it. Drag a connection into a group to move it; drag group rows to reorder them.
- The filter box above the Explorer filters by name, host, tag, and note (and group name): every space-separated word must match, e.g. `prod web`. Groups with matches open automatically; clearing the filter (`Esc` or the × in the box) restores their previous state. When exactly one connection is left, Enter connects it. Filtering only changes what is shown: group menus still act on the whole group, so multi-select to act on the filtered rows.
- `Ctrl+click` adds or removes individual selections; `Shift+click` selects a range; `Ctrl+Shift` can add a further range.
- Multiple selection offers actions to open N connections, open N connections with synced input, health-check N connections, bulk-edit, or export the selection. Group menus can open all, open all with synced input, health-check the group, import into the group, or export the group (health check: see 13.4). Opening more than ten connections requires confirmation. Up to six connect concurrently; individual failures do not cancel every target.
- Bulk edits apply only fields explicitly selected, such as username, port, authentication, or jump host. Review the preview and skipped count. FTP/plugin targets do not accept unsupported private-key authentication types.

**Result, mechanism, and caution:** Bulk edits change saved profiles, not the identity of already logged-in terminals. Check the selection before bulk deletion. Plain bulk opening does not broadcast commands; to type into a group of machines at once, use **Open All with Synced Input** or [synchronized input](#workspace).

### 4.3 Import from Xshell, WinSCP, and OpenSSH

**Entry point:** Explorer's more menu → import from other tools.

**Steps:** Wait for the parallel scan of Xshell, WinSCP, and `~/.ssh/config`. Select a source manually if its default location is not found. Review importable and duplicate items, adjust selections, and import. Unsupported protocols cannot be selected. Passwords protected by the original tool's master password are not recovered; be ready to re-enter them.

**Result and how it works:** This imports recognized parameters, not the complete behavior of another application. OpenSSH literal `Host` entries become profiles; `Host *` supplies defaults. Without `HostName`, the alias is used. An imported `IdentityFile` path does not prove that the file exists locally. A host with `CertificateFile` (or a `-cert.pub` next to its private key) is imported with certificate authentication; one with only `IdentityAgent` (the default agent, or another endpoint usable on this computer) uses SSH agent authentication, and that endpoint goes into the profile's SSH options → Agent endpoint. `ForwardAgent yes` forwards the agent `IdentityAgent` names, and `ForwardAgent <path>` also becomes the endpoint — except when authentication uses one agent and forwarding names another (a connection uses a single agent), in which case forwarding is not imported; paths from another platform are not imported either, and the preview says why. `KexAlgorithms` / `HostKeyAlgorithms` / `Ciphers` / `MACs` go into the profile's custom algorithm lists as written (names this version does not implement are dropped), and `Compression` and `ForwardAgent yes` are carried over. `ForwardX11` / `ForwardX11Trusted` / `ObscureKeystrokeTiming` are taken only when written; otherwise VelaShell's own defaults apply (no X11 forwarding, trusted when forwarding, no keystroke timing obfuscation) — so a host with only `ForwardX11 yes` is trusted (`-Y`) in VelaShell, unlike OpenSSH, and the preview says so. `LocalForward` / `RemoteForward` / `DynamicForward` become tunnels of the connection (stopped; start them in the tunnel panel). An end containing `/` is a Unix socket (a local forward may use one on either end; a remote forward needs both ends), and a `RemoteForward` without a target is a remote dynamic forward whose allow list comes from the host's `PermitRemoteOpen` — **a remote dynamic forward without `PermitRemoteOpen` is not imported** (OpenSSH would then allow everything, while a tunnel's allow list must be stated; write `any` to allow all). Anything that could not be imported as written is listed on the preview row in manual-selection mode. `ProxyJump` requires the corresponding jump host in the import batch; otherwise the result remains direct and needs manual correction. Other keywords such as `ServerAliveInterval` are not imported.

**Caution:** Keep fingerprint checking enabled before and after import. Importing Xshell sessions is different from [Xshell-compatible external launch](#desktop).

### 4.4 Import and Export Connection JSON/CSV

**Sensitive-data warning:** Protect JSON exports containing secrets with a strong export passphrase and keep that passphrase separately. The application cannot recover a forgotten passphrase. Even exports without passwords may reveal hosts, usernames, notes, groups, and network structure. CSV imports can contain a `password` column; do not share such spreadsheets with untrusted parties.

**Entry point:** Explorer's more menu offers connection-file import, export-all, and a CSV import template. Selection and group menus export the corresponding scope.

**Steps:**

1. Choose JSON for a complete connection export; choose CSV for spreadsheet editing. Save the template first and follow its headers. `host` is required. To share connections with the command-line ssh, choose OpenSSH config: it writes `Host` blocks for SSH / SFTP connections only (host, user, keys and certificates, jump hosts, compression, the agent endpoint (`IdentityAgent`) and agent forwarding, X11 forwarding with its trust level, keystroke timing obfuscation (only when on), algorithms and port forwards, writing a remote dynamic forward together with its `PermitRemoteOpen`; never passwords); merge it into `~/.ssh/config` or reference it with `Include`.
2. JSON exports exclude passwords by default. Explicitly include credentials when needed and set an export passphrase of at least 8 characters; the credentials section is encrypted with a passphrase-derived key and AES-GCM, while hosts, user names, notes, groups and the rest stay in plain text. **CSV exports never contain secrets.**
3. Select the import file (maximum 16 MB), enter its decryption passphrase if required, and review targets and groups in the preview.
4. Duplicates are skipped by default. Choose overwrite only when intended, or keep both. Import, then spot-check hosts, jump hosts, authentication, and paths.

**Result and how it works:** JSON can carry profiles, groups, jump-host dependencies, shared credentials, and tunnels; required jump hosts are included in the export. This is not the application-settings JSON under General → Data & Storage.

**Caution:** Overwrite may retain a local password while updating host parameters; ensure an old password will not be sent to a new host. Exporting a private-key path does not back up the file itself. Copy actual key files securely during migration.

<a id="authentication"></a>
## 5. Authentication, Keys, and Shared Credentials

### 5.1 Choose an Authentication Method

**Entry point:** Connection configuration → General → authentication method / credential source.

| Method | Prerequisites and operation | Result, mechanism, and caution |
| --- | --- | --- |
| Password | Enter username/password; optionally remember it | The remote SSH server authenticates you; MFA may add a challenge. Basic FTP/plugin authentication is password-based |
| Private key | Select a local private key and supply its passphrase if needed | Supported OpenSSH, PKCS#8, PPK, and other key formats are parsed by the SSH layer. The server must already authorize the public key; selecting a file does not install it |
| Certificate | Select an SSH certificate and matching private key | The certificate is not the private key. Selecting `-cert.pub` can infer a private-key path; verify existence and matching material |
| Agent | Start/configure the local SSH Agent and add the required key | The Agent signs authentication requests. Entering a host does not automatically select Agent authentication or search your key directories |
| Tailscale | Both ends must meet Tailscale SSH environment and authorization requirements | Access depends on identity and policy; this is not a passwordless bypass of server authorization |
| Key store (Windows only) | Pick an RSA / ECDSA key from the certificate store, the TPM or a smart card, and add its public key (**Copy public key**) to the server's `authorized_keys`; or, under **User certificate (optional)**, pick an OpenSSH user certificate a CA issued for this key (`*-cert.pub`; have the CA sign the line **Copy public key** gives you), so the server only needs to trust the CA (`TrustedUserCAKeys`) and `authorized_keys` stays unchanged | The private key cannot be exported and never leaves the key store: Windows signs, and the profile stores only a reference to the key (plus the certificate path when one is set). The TPM or a smart card may ask for a PIN when signing. A certificate that does not belong to the key is reported before connecting; with no certificate the key signs in as a plain public key. A computer without that key (a synced profile, a card not inserted) reports that it cannot open it; other platforms do not show this option, and it cannot use a shared credential |

**Steps:** Establish what the server accepts, select the corresponding method and material, test, and connect. Enter interactive verification codes in the challenge dialog when requested. Cancelling stops authentication. Private-key, certificate, and Agent methods do not silently fall back to password authentication.

**Caution:** Do not interpret troubleshooting as an invitation to try every method through automatic key discovery. Fix a missing Agent or invalid material locally. A saved password does not mean a one-time verification code should also be saved.

Tailscale mode requires Tailscale installed and signed in locally, a MagicDNS name or tailnet IP, the remote operating-system username, and an enabled Tailscale SSH policy authorizing that user. A check policy may require browser approval; verify what you are approving. Fingerprint checks, jump hosts, and proxies still apply. Interaction-free access is not guaranteed.

### 5.2 Fill a Shared Password in the Terminal

**Disclosure warning:** The client cannot prove that the remote terminal has disabled echo. A malicious or misconfigured host can display or log the password. Use this only on a trusted terminal showing the password prompt you expect.

**Entry point and steps:** Focus a connected terminal on the primary screen at the bottom of the buffer, not scrolled into history. Recognized `Password:`, sudo (including sudo-rs, the default sudo since Ubuntu 25.10, which asks `[sudo: authenticate] Password:`), doas, Git, or Chinese / Japanese / Korean password prompts can show a shared-password list beside the cursor line. If detection does not trigger, use the Command Palette action to fill a shared password. Only password-authentication shared credentials with a stored password are listed. Nothing is initially selected, so Enter still goes to the remote terminal. Click an item, or select with ↑↓ and press Enter, to retrieve, fill and **submit it with an automatic Enter** (the password and the Enter are sent together, so keys typed right after picking cannot end up in the password); Esc closes the list.

**Result and how it works:** The selected secret goes directly to the bound terminal connection, not through the clipboard, synchronized-input channel, or completion. UI, connection, or input-state changes expire the request and close the list, including input that does not come from the keyboard (bytes forwarded by a synchronized-input channel, the anti-idle NUL). Private-key passphrases and verification codes are outside automatic detection. Queuing input does not prove delivery; recheck after state changes instead of blindly sending it again.

### 5.3 Key Management

**Entry point:** **Settings → Key Management**.

**Operation:** Search existing keys, generate Ed25519, ECDSA 256/384/521, or RSA 4096 keys, or import an existing key. Import parses the whole private key: damaged keys and unsupported formats (such as legacy encrypted PEM) are rejected; passphrase-protected keys are accepted and the passphrase is asked for when connecting; a `.pub` chosen along with it that does not belong to the same key is rejected too. Copy the public key to the administrator for authorization according to server policy; each row's **Export** menu saves the public key (`name.pub`) or the private key. A default key can be selected. Automatically adding keys to the SSH Agent after login is optional and off by default.

**Result and how it works:** These entries manage real files. Public keys can be distributed; private keys belong on controlled devices. A key passphrase protects use after a file is copied; it does not replace local access control.

**Before exporting a private key:** The private key is copied as it is to the chosen location; if it has no passphrase, whoever gets that file can sign in to every server that trusts the key. Keep it somewhere only you control and delete it when you are done; do not put it in cloud drives, chats, or repositories. On macOS/Linux the exported file is readable and writable by you only (0600); on Windows it inherits the target folder's permissions.

**Before deleting:** Deletion affects the actual private-key file under `~/.ssh`, not just a UI record. The confirmation shows the name and private-key path. Verify a protected offline backup and check whether Git, OpenSSH, or automation also uses the key. Generating another key with the same name will not restore access authorized for the old key.

### 5.4 Shared Credentials

**Entry point:** **Settings → Shared Credentials** to create, search, edit, or delete identities. Select a credential source in a connection's General page; authentication material can also be copied from an existing connection.

**Prerequisites and steps:** When multiple hosts genuinely use the same identity, create a named credential with username and authentication material, then reference it from those profiles. A blank profile username inherits the shared value; a filled username overrides it. Save and reconnect to verify.

**Result and how it works:** Profiles retain references rather than independent secret copies. An edit affects every referencing profile's **next connection**. Missing/rejected credentials may lead to a login dialog where you can choose whether to update the shared credential; updating is not preselected for a rejection case.

**Caution:** Check the usage scope before editing. Deletion requires confirmation and copies referenced authentication material back into affected profiles to avoid breaking their login configurations. Therefore deleting a shared credential does not securely erase every local copy of its secrets. Synced file paths refer to each device's own files; Gist sync without end-to-end encryption does not carry secrets.

<a id="network"></a>
## 6. Proxies, Jump Hosts, Keepalive, and Reconnection

### 6.1 Global Proxy

**Entry point:** **Settings → Network Proxy**.

**Steps:** Select no proxy, system proxy, HTTP, or SOCKS5 as required. For a custom proxy, enter address, port, and credentials; enable remote DNS if appropriate. Save and test a new connection.

**Result and how it works:** System proxy is the default. The setting affects SSH/SFTP/FTP and outgoing HTTP paths such as updates, Gist, feeds, and plugins running inside the main process (plugins running in their own process use the system default proxy; connection diagnostics resolve names locally and connect directly). In a jump-host chain, it affects the first outbound hop from this computer, not a separate proxy configuration on every host. DNS resolution location affects internal names and where name information is exposed.

**Caution:** A proxy is not an SSH jump host. Reaching the target through HTTP/SOCKS5 does not grant server access. Per-connection proxy configuration is not currently available. In settings export the **Network Proxy** category is unchecked by default, and even when checked the proxy password is never written; re-enter it after importing on another device (on import, the locally saved password is kept only when the proxy type, host, port, and user name all match). Gist sync without an end-to-end passphrase leaves the proxy password out too and keeps the local one on pull by the same rule; with a passphrase the whole payload is encrypted and the proxy password travels with it. The proxy password is stored encrypted on the device (the same way as connection passwords); one saved in plain form by an older version is encrypted the next time the settings are saved. If the local encryption key has changed (the data folder was copied from elsewhere, or the system keychain could not be opened this time) it cannot be decrypted: the password box here shows empty and the proxy is contacted without a password. Re-enter it once; if you do not, it is not lost either, and is decrypted as usual once the key is back.

### 6.2 Multiple Jump Hosts

**Entry point:** Jump-host selection on the connection's General page.

**Prerequisites and steps:** Save and verify the reachable SSH/SFTP jump-host profile first. Select it for the target. For multiple hops, make the profiles reference the preceding hops without cycles. Handle authentication and host fingerprints separately at each hop.

**Result and how it works:** The application opens a forwarding channel inside the preceding SSH connection and establishes the next SSH connection over it. A jump host carries channels; it does not automatically create an interactive shell for each hop. The final host still needs independent authentication and identity verification.

**Caution:** Distinguish first-hop network failure, jump-host authentication, reachability from jump host to target, and final-target authentication. An unresolved imported jump-host name may leave a direct connection; verify it after import.

### 6.3 Timeouts, Keepalive, and Anti-Idle

**Entry point:** **Settings → General → Connection Defaults**; per-connection keepalive overrides are on the Terminal page.

**Steps:** Start with the defaults: 30-second connection timeout and 60-second keepalive. If network devices frequently drop idle connections, adjust with your administrator. Per-connection keepalive `-1` inherits the global value, `0` disables it, and the maximum is 3600 seconds. Anti-idle is a separate session option, disabled by default.

**Result and how it works:** SSH keepalive checks whether a connection remains usable. Anti-idle sends NUL to the PTY in an attempt to prevent terminal-activity-based idle logout. They are not the same feature. Neither bypasses an organization's mandatory session expiry nor guarantees uninterrupted networking.

### 6.4 Automatic and Manual Reconnection

**Fingerprint boundary:** Automatic reconnection stops for unknown/changed fingerprints or authentication requiring human intervention. It does not keep a fingerprint dialog waiting or silently trust a key. Recover through manual reconnection, verify the fingerprint independently, and accept only when justified or contact your administrator. **Do not disable fingerprint checking to restore connectivity.**

**Entry point and steps:** General behavior settings control automatic reconnection, maximum attempts, and interval cap. Use the disconnected-session overlay, or press `Enter`/`Ctrl+R` in a disconnected tab. `Esc` closes a disconnected tab.

**Result and how it works:** Automatic reconnection defaults to on, with at most 3 attempts and a 5-second interval cap; unexpected disconnections use backoff such as 1/2/4 seconds. Initial handshake failure, intentional disconnect, remote `exit`, local shell exit, fingerprint rejection, and authentication requiring intervention are not retried indefinitely. Reconnection creates a new session; it does not restore unsaved state in the previous shell process. Use appropriate server-side session-persistence tools for important long-running work.

<a id="terminal"></a>
## 7. Working with the Terminal

### 7.1 Remote and Local Terminals

**Entry point and prerequisites:** Connecting an SSH profile opens a remote terminal. **Local terminals are currently Windows-only.** Search for the local-terminal action in `Ctrl+P`. Detected installations can include PowerShell 7, Windows PowerShell, CMD, WSL, and Git Bash; absent shells are not listed. Linux/macOS currently have no local-terminal entry. Check the tab and prompt before running commands.

**Steps and result:** Type and press Enter to send a command to the active terminal. Remote commands use server permissions/resources; local commands affect your computer. Opening the SSH side file pane does not turn a local terminal into a remote one. Changing tabs does not move a running command to another machine.

**How it works and cautions:** The VT terminal interprets text, control sequences, cursor movement, and colors; the shell interprets commands. The client is not a replacement for bash or PowerShell. Diagnose terminal parameters and remote-shell behavior separately for full-screen applications, encoding, and key problems.

### 7.2 Fonts, Colors, Encoding, and Display

**Entry point:** **Settings → Appearance / Terminal**; supported per-host overrides are on the connection's Terminal page.

**Steps:** For garbled output, compare the actual remote encoding and selected decoding. For alignment, choose a monospaced font with sufficient character coverage, then adjust size, line height, and padding. Match TERM to the remote application's expectations. Use `Ctrl+=`, `Ctrl+-`, and `Ctrl+0` for temporary zoom in/out/reset.

**Result and how it works:** Encoding turns bytes into characters; fonts draw them. A font change cannot repair incorrect decoding. SFTP file names of an SSH session are decoded with the session encoding in effect when it connected (for example Chinese names on a GBK server); with a UTF-8 session, undecodable names show replacement characters but can still be opened and deleted. An encoding switched temporarily from the status bar only affects the terminal, not an already open file pane. Themes and ANSI palettes change presentation, not remote output content. Terminal-background transparency and window opacity are separate settings.

**Caution:** Per-line times are client-side display aids, not authoritative remote business-event timestamps. Remote output can control screen colors, titles, and links.

### 7.3 Select, Copy, Paste, and Open Links

**Execution warning:** Multiline paste can execute multiple commands. Without bracketed-paste support on the target, embedded line breaks can execute lines even if the client appends no Enter. Keep multiline-paste confirmation enabled; verify host and content. In particular, do not blindly paste website instructions into a production terminal.

**Steps:** Drag to select; double-click selects a word when enabled; `Shift+click` extends the selection. `Alt+drag` selects a rectangle, `Ctrl+Shift+drag` adds a discontiguous selection, and `Ctrl+Shift+Alt+drag` adds a rectangle. When a full-screen application reports mouse input, use `Shift+drag` to bypass reporting and select text. Copy with `Ctrl+Shift+C`; paste with `Ctrl+Shift+V` or `Shift+Insert`.

**Result and how it works:** Copying can trim trailing spaces according to settings. Copy-on-select, right-click paste, and Ctrl+C-to-copy-with-selection modify normal gestures. Treat `Ctrl+C` as remote interrupt by default. With the selection-copy exception enabled, it copies rather than interrupts while a selection exists; clear the selection first.

**Links:** `Ctrl+click` opens recognized links; inspect the actual target. Remote OSC 52 writes to the local clipboard are controlled separately in Terminal settings. Do not unconditionally let untrusted hosts overwrite the clipboard.

### 7.4 Scrollback, Search, Clear, and Buffer Export

**Entry point and operation:** `Ctrl+F` opens search; Enter finds next, Shift+Enter previous, and Esc closes it. PageUp/PageDown scroll primary-screen history; Shift+PageUp/PageDown also scroll the alternate screen. Alt+wheel accelerates scrolling. Settings control scrolling on output/keypress, alternate-screen wheel behavior, and scrollback size (100–200000 lines). `Ctrl+Shift+K` clears the terminal display.

**Result and how it works:** Search covers content retained in the current terminal, not remote disk logs. Output discarded beyond the scrollback limit cannot be recovered by searching. The Command Palette can export the selection or current buffer as plain text: this is a snapshot, not continuous logging or a timed recording. Clearing the screen neither deletes remote files nor erases local logs or audits.

### 7.5 Command Suggestions and Native Shell Completion

**Entry point and operation:** `Alt+Enter` opens suggestions; use ↑↓ to select, Enter to adopt, and Esc to close. Empty input can show quick commands/history. Right/End accepts available ghost text; Tab remains native shell completion.

**Result and how it works:** Local suggestions rank recent history, common subcommands, and quick commands. They do not guarantee awareness of the remote program, permissions, paths, or command safety. Adopting usually inserts text; review before executing. Adopting a quick command with `{{placeholders}}` (including accepting it as ghost text with Right/End) first opens the same value prompt as the quick-command panel and writes the expanded command only after you confirm; cancelling writes nothing. Multiline quick commands are excluded from completion.

### 7.6 Line Numbers, Times, Command Blocks, and Shell Integration

**Entry point:** `Ctrl+Shift+L` toggles the line-number/time gutter; right-click it for options. **Settings → Terminal → Session** provides read-only bash/zsh shell-integration snippets.

**Steps:** To use command blocks, read the instructions and add the appropriate OSC 133 snippet to your shell configuration. Check interactions with prompt tools such as starship or oh-my-posh. Reopen the shell and run a command to verify markers. Click the gutter to fold/unfold output, click a command marker to select its output, and use `Ctrl+Shift+↑/↓` to jump between prompts.

**Result and how it works:** The remote shell reports command boundaries with OSC 133; the client cannot reliably infer them from arbitrary text. Without integration, equivalent command-block navigation is unavailable, and so are the copy and rerun actions below. Working-directory reporting also needs remote cooperation; not every shell automatically follows a file-pane path.

**Copy and rerun:** Right-click any line of a command block in the terminal's left gutter; the menu starts with **Copy this command** and **Rerun this command**. "This command" is the on-screen text between the end of the prompt and the moment Enter ran it (soft-wrapped lines are rejoined; a multi-line command keeps its lines with the continuation prompts cut off). Both items are greyed out for a command that has not been run yet, one abandoned with Ctrl+C, and blocks from older integration scripts that do not mark the end of the prompt. Rerun sends the command plus Enter to **the same terminal** and is available only while the remote side sits at an empty new prompt (not while the previous command still runs, inside a full-screen program, or with something already typed); it goes to this tab only, not through sync-input channels, and runs in the **current** directory on the current host rather than going back to the original context. Multi-line commands are sent as a bracketed paste; when the remote side has not enabled bracketed paste you are asked first, and multi-line commands whose continuation lines are unmarked can only be copied. **The integration snippets changed on 2026-10-24** (bash/zsh continuation prompts are marked too; the zsh prompt marker moved into PS1 and the extra backslash after every prompt is gone), **so copy the snippet from the settings page again if you installed an older one**: with the old zsh snippet command blocks never form at all, and with the old bash snippet multi-line commands cannot be rerun.
### 7.7 Output Rules: Custom Highlighting and Auto-Answer

**Auto-answer risk:** Auto-answer types into the terminal for you. Use it only for yes / Enter / fixed-text answers, and **never put a password into a rule**: rules are saved with your settings and, with cloud sync on, end up in the Gist. For passwords use the shared password list from 5.2. Rules that would match a password, passphrase, or verification-code prompt cannot be set to auto-answer; the settings page rejects them.

**Entry point and steps:** **Settings → Output Rules** (the page right below Terminal). **New rule** → enter a name and a pattern (.NET regular expression, matched line by line) and choose case sensitivity → pick the action (Highlight / Auto-answer) and where it applies (all terminals / one group / one connection) → **Done** → **Save settings** at the bottom of the window; open tabs pick it up immediately. In the list you can enable/disable, move up/down (higher wins), edit, and delete. **Test** at the bottom of the page takes pasted output and shows what matches: while the editor is open only the rule being edited is tested, otherwise all enabled rules. There are no rules out of the box.

**Highlight:** Text and background colors can only be chosen from the ANSI 16 colors of the terminal color scheme, plus bold, so they follow theme and scheme changes. Your rules sit above the built-in semantic highlighting (URLs, IPs, error/warning/success words, options, numbers): a text color you set overrides the program's own color, everything you leave unset stays as it was, and selection and search highlights still show on top. Main screen only; full-screen programs such as vim, htop, and less are left alone.

**Auto-answer:** After remote output, when the line the cursor is waiting on matches a rule, VelaShell waits for the remote to go quiet for a moment (about 0.3 s), sends the rule's text, presses Enter if the rule says so, and shows **Auto-answered: rule name** above the terminal. Each line is answered once; two answers from the same rule are at least **Cooldown** seconds apart (default 2; a prompt that shows up sooner is answered when the cooldown ends); after **At most per minute** answers (default 6) the rule pauses and says so, leaving the rest to you. Nothing is sent while you are typing on that line (including local echo and pastes); judging restarts after Enter. Only remote output counts: local echo, text the client writes into the terminal, and recording playback never trigger, and neither does output that has already scrolled past (for example a file you `cat` that happens to contain the same words). Answers go out the same way as Quick Commands and are **not** forwarded to sync-input channels unless the rule turns on **Also send to the sync input channel**.

**How it works and caveats:** Every pattern is compiled for a linear-time engine, so even catastrophic patterns such as `(a+)+$` cannot slow the terminal down; the price is no backreferences or lookarounds (the settings page tells you). At most 64 rules. Highlights are cached per line of text, so lines that did not change are not matched again. Rules live in the terminal settings, travel with the Terminal category of settings export/import and with the app settings in Gist sync. Terminal views owned by plugins get highlighting but no auto-answer. Masking for recordings and logs (chapter 14) still uses only the built-in rules; this table does not feed it yet.

<a id="workspace"></a>
## 8. Docking, Synchronized Input, and Quick Commands

### 8.1 Tabs and Splits

**Entry point and steps:** Open multiple tabs and drag one to a docking edge to split at the indicated target. Alternatively, `Ctrl+Shift+D` splits horizontally and `Ctrl+Shift+S` vertically. `Alt+arrow` moves focus between panes (with focus in a file pane, `Alt+←` / `Alt+→` go back / forward there instead); `Ctrl+Shift+X` maximizes/restores the current pane. Drag dividers to resize or double-click to equalize. Tab context menus offer tab-strip display/order options and pinning; middle-click closes unpinned tabs. The `+` at the end of each tab strip opens a new connection in that pane (the same new-connection dialog as `Ctrl+T`; the resulting tab lands in the pane whose `+` you clicked, even when another pane is empty). Tabs opened with `Ctrl+T` or from other entry points still fill an empty pane first, and otherwise land in the pane you are using.

**Result and how it works:** Each pane has its own tab strip; numeric tab navigation counts within that strip. When the tabs do not fit, scroll buttons and an "All Tabs" list appear at the end of the strip; a strip moved to the left / right side gets them too, at its foot. Pinning protects against some quick-close actions; the pinned state is saved with layout templates (8.4) and with **Restore split layout**, and lasts for this run only when neither is used.

**Caution:** Arbitrary floating windows created by dragging docking tabs out are not supported. To keep splits and pinned tabs for next time, use the layout templates in 8.4, or turn on **Restore split layout** under **Restore sessions** in **Settings → General** (off by default). With only **Restore sessions** on, the connections that were online at exit are reconnected one by one into a single pane: splits are not rebuilt and dual-remote file tabs are not included.

### 8.2 Synchronized Input Across Terminals

**High risk:** Every machine in a channel can receive the same destructive command. Verify host, permissions, and shell state before joining. Pause or leave before entering passwords, switching to production, or running interactive programs, to avoid broadcasting secrets or control keys.

**Entry point and steps:** Use tab context menus to join synchronized-input channel A/B/C/D. Put cooperating tabs in the same channel and check their top banner. Keyboard input, IME text, and pasted text from an active member are broadcast. Pause, leave, or close the channel from the banner; leave promptly when finished. From the Explorer, a group's **Open All with Synced Input** (or **Open N Connections with Synced Input** on a multiple selection) opens only the connections that produce a terminal (SFTP/FTP/workspaces are skipped), always in new tabs, and puts them in the first free channel; if all four channels are in use nothing is opened. Tabs already open for the same machines are left alone. When all have finished connecting a notice says how many joined and how many did not connect — those miss anything typed meanwhile.

**Result and how it works:** Members are peers, not a permanent master and followers. Pausing stops both sending and receiving for that tab; disconnected tabs do not receive input. Different encodings are transcoded. Writing to the destination PTY does not rebroadcast and form a loop.

**Caution:** Input is synchronized, not results or server state. Different directories, prompts, programs, or execution speeds can make identical input behave very differently.

### 8.3 Quick Commands and Parameters

**Multiline warning:** Quick commands append no Enter, but multiline bodies are not inherently safe. Shells without bracketed paste may execute embedded newlines immediately. Check content and target before sending, and keep multiline confirmation enabled.

**Entry point:** Manage groups/snippets under **Settings → Snippets** and use them from the sidebar Quick Commands panel.

**Steps:** Create a name and command body, optionally grouped. Use `{{name}}` or `{{name=default}}`, for example `cd {{directory=/tmp}}`. Supply prompted values, check the expanded result, and send. For single-line commands, decide separately whether to press Enter. The prompt pre-fills the values last entered for the same quick command (kept for this run only, never written to disk or synced; parameters whose names look like passwords, tokens, or verification codes are not remembered, and a field left blank falls back to its default).

**Result and how it works:** The host substitutes strings and sends the body to the current terminal. It is not a remote script-management service and does not validate shell escaping, permissions, or paths. Multiline sends prefer bracketed paste; otherwise follow confirmation behavior. There is no automatic-execution switch for quick commands.

### 8.4 Layout Templates

**Entry point:** The title-bar **Layout templates** icon (its menu lists every template, followed by **Save current layout as template…** and **Manage layout templates…**); the two commands of the same names in the Command Palette, plus one palette entry per template (Enter opens it).

**Steps:** Open the machines you work with together, drag them into the split you want, pin tabs as needed → **Save current layout as template…** → name it (a name that already exists asks whether to replace that template). Later, pick it from the menu or the palette to reopen everything as saved: pane orientation and proportions, tab order and the selected tab in each pane, pinned state, and tab-strip position. The **Manage layout templates…** window opens, overwrites with the current layout, renames, and deletes templates, and has **Export…** and **Import…** at the bottom.

**Result and how it works:** A template records which **saved connection** each tab points to (by profile Id) and how it was opened (terminal, standalone SFTP/FTP/plugin file tab, dual-remote file tab, plugin workspace); local terminals are recorded by shell type. It contains no passwords, and opening runs the normal connection flow (missing credentials still prompt). If the workspace already has tabs, the template opens as a column to the right of the existing layout and your tabs stay put; more than 10 tabs asks first. Templates live in the local database and are not part of settings export; with cloud sync on they travel with the Gist (the **Layout templates** sync scope, on by default, see 17.2).

**Export and import:** **Export…** writes all templates to one JSON file (with a format header and version). It holds only the profile Id each tab points to, how it opens, the local shell type, and the tab title at save time — no passwords and no host addresses, although a tab title is often `user@host`, so do not publish the file casually. **Import…** reads such a file: when a template in it is the same one you already have (same Id) or has the same name (case-insensitive), you are asked once — **Replace** keeps your existing entry and takes the file's name and layout, **Save as new** saves it as "name (2)" and keeps both, and cancelling writes nothing; the rest are added directly. If imported templates refer to connections that do not exist on this computer, a notice after the import says how many templates are affected, and opening them skips those tabs as usual. Profile Ids only match when the connections came over through cloud sync or a connection-file JSON import (4.4, which keeps the file's Ids where it can); a same-named connection created by hand on the other machine does not count.

**Caution:** Ordinary plugin tab panels and one-off connections from Quick Connect or external launch cannot be reopened, so saving skips them and says so. The dedicated AI region is separate from the document layout and is not part of layout templates. When opening, tabs whose connection profile has been deleted, or whose shell is not on this computer, are skipped and listed while the rest open; a pane whose credential prompt you cancel closes itself.

<a id="files"></a>
## 9. SFTP/FTP Transfers, Editing, and Directory Bookmarks

### 9.1 Side Pane or Independent Dual Panes?

**Entry point and prerequisites:** Toggle the SSH side file pane with `Ctrl+Shift+F`; create SFTP or FTP profiles for independent file tabs. SSH/SFTP requires the server's file subsystem; FTP/FTPS requires its corresponding service. Plugin file protocols require the plugin first.

**Steps and result:** Independent dual panes show local files on the left and remote files on the right. Resize with the divider; the ratio and the remote pane's column widths you dragged are remembered across restarts (one copy shared by all connections and both kinds of file tab, not on the settings page). Click a column header in either pane to sort by it (folders always stay first; click the same header again to reverse). The toolbar offers directory comparison, synchronization, **Save current directories**, and **Bookmarks**. The remote-only side pane does not have the complete local/remote comparison/sync toolbar. Navigate remotely with breadcrumbs; `Ctrl+L` edits a path, Enter navigates, and Esc cancels. The toolbar can go up one level, refresh, show hidden files, create files/directories, and view transfer history. Both panes have **Filter this folder** in the header (list-filter icon): type part of a name (case-insensitive) and the list keeps only the rows of the current folder whose names contain it; nothing is searched on the server, and select-all, upload, download and delete act only on the rows left. Changing folder clears the text (the box stays open); closing the box or pressing Esc in it clears the filter, and Enter returns focus to the list. **Compare Directories** clears both panes' filters before comparing. Next to Go up, both panes have Back / Forward (`Alt+←` / `Alt+→` while focus is in that pane, outside text boxes; in a split layout these two directions go to the file pane, and `Alt+↑` / `Alt+↓` still move between panes). Each pane keeps its own history: a step is recorded only when you enter a different folder (refresh is not), up to 50 steps, and a folder that can no longer be opened reports an error without changing the history. History lives only as long as the pane: closing the tab or reconnecting starts afresh. "Show hidden files" and the column choices on the header right-click menu are one shared setting: toggling them in any file pane (side pane or file tab) saves the setting and updates the other open file panes.

**How it works and cautions:** A session's shell, files, and monitoring commands can use separate SSH channels. Sharing an SSH connection does not share the shell's working directory or execution context. An SFTP-only account may prohibit shell/exec, so transfers can work while resource monitoring or remote SHA-256 does not.

### 9.2 FTP/FTPS Connections

**Plaintext warning:** FTP encryption **None** sends usernames, passwords, and contents in plaintext. **The default Auto setting does not guarantee encryption either: it tries explicit FTPS and falls back to plaintext FTP if unsupported.** On untrusted networks, explicitly choose the server's supported explicit/implicit FTPS mode and verify certificates, or use SFTP. Do not treat Auto as mandatory TLS. Anonymous login does not itself provide encryption; a reachable port is no reason to remove security checks.

**Entry point and steps:** Create an FTP connection with address, port, and account. Choose Auto, explicit, implicit, or no encryption according to server documentation; configure anonymous/passive behavior as needed. Set the initial directory and the server time zone in advanced options (when the server clock runs in another zone, directory compare and sync convert LIST modification times with it). Check fingerprint and certificate issuance details before trusting a TLS warning.

**Result and how it works:** FTP has control and data connections; FTPS adds TLS. It is not SSH. Passive mode, data ports, and firewalls affect cases where login works but transfers fail. FTP certificate trust is separate from SSH host-key trust.

**Single-connection serialization:** Each FTP/FTPS session uses exactly one control connection and transfers one file at a time; uploads, downloads, and directory refreshes queue on that connection. Neither the global maximum concurrent transfers setting nor a larger connection count in an older profile opens multiple clients for that session, avoiding rejected or reset data connections on servers that do not support concurrency. SFTP concurrency is unaffected.

### 9.3 Upload, Download, and Manage Files

**Overwrite/deletion warning:** Check both paths, selected files, and conflict policy first. Remote deletion does not use the local recycle bin. Recursive deletion can immediately affect an entire directory tree; back it up beforehand.

**Steps:** Select local items to upload, or remote items to download into the local pane's current directory. Directories transfer recursively. In the local pane, double-clicking a file opens it with the system's default program (mirroring the remote pane; upload by dragging, the button, or the context menu), and the context menu offers refresh, open, upload, rename, new folder / file, and delete; a new file whose name already exists is reported instead of overwriting it. You can also drag items between the two panes. Files dragged in from the system file manager upload when dropped on the remote pane; the local pane does not accept them (the cursor shows no drop, and nothing is copied — use the system file manager to copy files between local folders). Remote context menus offer open, open with default editor, download, download as…, rename, move to, copy to, properties, and delete. "Download as…" appears only when you right-click a single file: pick the location and name in the system save dialog; if the dialog asked whether to replace an existing file and you agreed, it is overwritten — the "When a file already exists" policy does not ask, skip, or rename again, and the existing file is not treated as a partial download to resume. Permissions/ownership can change only where supported by protocol and account; the client cannot bypass server authorization.

**Keyboard:** Both panes follow File Explorer: `F5` refreshes the current directory (focus anywhere in that pane except a text box); `F2` renames the selected item and `Delete` deletes the selected items. These two need focus in the file list, and deletion still asks first (the remote confirmation names the server). `F2` does nothing while several items are selected, and while the path box has focus these keys belong to it.

**Search:** File panes of SSH sessions (the side pane, file tabs, and the SSH side of a dual-remote tab) have "Search remote files" on the toolbar and in the background and folder context menus. It searches on the server with `find` / `grep` by file name (without wildcards it matches names containing the text) or by file content (literal match); double-click a result to show it in the pane. At most 500 results and 2 minutes; hitting either limit is reported as incomplete. Result names follow the session encoding (the same source as the file pane, so Chinese names on a GBK server show correctly and can be selected), and a name containing a line break is still one result. Windows servers (cmd.exe / PowerShell) and SFTP-only accounts are not supported and no command is sent. FTP and plugin protocols do not have this entry.

**Open terminal here:** File panes opened from a profile whose connection type is SSH (the side pane, file tabs, and the SSH-profile side of a dual-remote tab) have **Open terminal here** in the context menu: on the background it uses the current folder, on a folder it uses that folder, and it opens a new terminal tab on the same profile. Authentication works as usual (a missing credential brings up the login dialog); afterwards the tab silently runs `cd` into the folder (the path is single-quoted and escaped, and it takes precedence over the profile's startup directory; the tab returns there after a reconnect too). SFTP-only profiles, FTP, and plugin protocols do not have this entry. Windows servers (cmd.exe / PowerShell) may not understand the path form SFTP reports, so the `cd` can fail; the error shows in the terminal as usual.

**Result and how it works:** Transfers appear in the progress popup/history. Settings control concurrency (1–16), bandwidth, timestamps, completion notifications, and logs. Ordinary name conflicts can ask, overwrite, skip, or rename. Directory synchronization uses its preview rather than that ordinary conflict policy.

**Resume and cancellation:** With resume enabled, a nonempty destination smaller than the source is verified before resuming. Verification failure follows conflict policy; a matching name is not enough to append blindly. Keeping partial files enables later resume. "Cancel" in the popup header stops every running batch; the × at the end of a row stops only that file — the rest of its batch keeps going, and the file is dropped from the transfer queue (if the batch is later paused by a disconnect, "Resume" will not transfer it). Its partial file is handled like a cancelled batch, per the "Resume interrupted transfers" / "Clean up partial files" settings. The popup can also clear completed items, and failures can be retried manually as new transfer attempts.

**Automatic retry:** Temporary failures such as a dropped connection or a timeout are retried on the same connection up to the "Retry failed transfers" count (default 3, 0 = never), waiting 1, 2, 4 seconds and so on between attempts (at most 30 seconds); the row shows "Retry 1/3" meanwhile. With resume enabled a retry continues from what that file itself already transferred; with resume disabled it starts over. Permission errors, a full disk, missing paths, writes the server refuses, and user cancellation are not retried. **A length check that fails after the transfer** (the source file changed mid-transfer, or the server reported an unreliable size) is not retried either: retrying would replay the same bytes, so it fails once and leaves the decision to you.

**When the server has no SIZE:** If an FTP server refuses the `SIZE` command (older servers, some directory configurations), the length is treated as unknown. The transfer still completes byte for byte, the progress bar only shows bytes sent rather than a percentage, and the file is never mistaken for an empty one or failed because of a guessed length.

**Continuing after a disconnect or a restart:** Upload and download batches started from a file panel are recorded in a local transfer queue. When the connection drops (or retries run out), the remaining files of that batch are not tried one by one; the whole batch becomes one "paused" row in the popup. Batches not finished when VelaShell closed are restored as "paused" on the next launch — **nothing connects at startup**. Only "Resume" connects: a tab already connected to that profile is reused, otherwise a file tab is opened (with the usual authentication and host-key checks); partial files continue from where they stopped, files not yet started are transferred whole, and the conflict policy is not asked again (it was settled when the batch was planned). "Discard" drops the remaining files; files already transferred are not affected. A batch whose connection profile was deleted can only be discarded. Relays between two remote panes, directory sync, automatic upload of remote edits, and transfers started by AI plugins are not queued.

### 9.4 Built-In and External Remote Editing

**Write-back warning:** Saving a local editing copy can overwrite the original remote path. Back up production configuration first and verify account/file. Avoid two people or editing sessions overwriting one another's work.

**Entry point and steps:** A remote file's **Open** action follows the built-in opening flow; **Open with default editor** uses the editor in File Transfer settings. Double-click behavior is configurable under remote editing, so it is not always the built-in editor.

- Built-in opening accepts regular files up to 5 MB. It downloads into a dedicated temporary directory. `Ctrl+S` saves and uploads to the original path. Saving always uploads; **Upload automatically after editing** does not apply here.
- For external editing, configure the editor path first. VelaShell downloads a temporary copy, launches the editor, and watches saves according to **Upload automatically after editing**, with approximately 600 ms debounce. That setting only covers the external editor and the system default app: when it is off, the local copy is not watched and nothing is sent back.
- Check transfer results before closing the tab. Closing a file tab or a terminal tab first uploads any save still pending in the editing sessions opened from that panel, then disconnects. When a terminal disconnects, its sidebar file panel is torn down and those editing sessions end with it; the connection is already gone, so changes not yet uploaded are kept as a draft with its location shown.

**Result and how it works:** This is a local temporary copy plus remote write-back, not the local editor running on remote storage. An external editor's Save As is not guaranteed to update the original. Disconnection, crashes, permissions, or size limits can prevent upload. Temporary copies can contain sensitive content: built-in editor copies are deleted when the editor closes, and those left by a crash are removed in the background on the next start once untouched for 7 days. External-editor copies are sorted by state: those matching the server (clean) are likewise removed in the background on the next start once untouched for 7 days; those whose upload failed or never happened (including ones open with changes when the app crashed) are **deliberately kept** as drafts, with the location shown in the file pane at the time and each draft listed in the message center on the next start (**Open folder**). Drafts are never deleted automatically, so delete them yourself once handled.

### 9.5 Local/Remote Directory Bookmarks

**Entry point and steps:** Navigate both panes to frequently used directories, then choose **Save current directories**. Use **Bookmarks** to select a saved pair and navigate both sides. Bookmarks can be edited or deleted.

**Result and how it works:** Each connection stores local↔remote path pairs. Deleting a bookmark does not delete directories. Saved profiles retain bookmarks; temporary connections retain them for this run only. Dual-remote tabs do not offer these paired bookmarks. Adjust local paths after migration. Duplicating a profile copies the latest stored bookmarks; a Gist pull of a payload pushed by an older version without bookmarks keeps the local ones, and only an explicitly empty bookmark list clears them. Overwriting from a JSON backup follows the same rule: a backup exported before directory bookmarks existed has no bookmark field and keeps the local ones; only an empty list in the file clears them.

### 9.6 Copy Between Two Remote Hosts

**Entry point and prerequisites:** Ctrl-select two different file-capable connections in Explorer → **Open in dual-pane SFTP**. Either side can be SFTP, FTP/FTPS, or a plugin file protocol. Redis workspaces and terminal protocols such as Telnet cannot use this flow.

**Steps:** Check left/right hosts and paths, select source items, and choose the copy direction. **Compare directories** checks the current level only; there is no sync or keep-up-to-date action. Closing this tab disconnects its two independent connections.

**Result and how it works:** Data passes through this computer without placing destination credentials on the source server. A fresh FTP/FTPS-source transfer to an SFTP target supporting atomic replacement first uses up to 4 MiB of relay memory per file to stream into a randomly named `.vela-relay-*` file in the destination directory, without a complete local file cache. Only after source consumption, length validation, the final FTP reply and target upload all succeed does it atomically replace the final file. Creation permissions follow the existing destination; commit retains the existing ownership and permission handling. Completion is shown only after commit succeeds. The destination directory must allow file creation and rename; permission to write the existing file alone may not suffice.

**Bidirectional streaming:** The safe-commit FTP→SFTP path above reads and writes concurrently through a bounded stream pipe, applying backpressure at about 4 MiB of unconsumed data. In reverse, SFTP→FTP passes the SFTP read stream directly to FTP upload, also without a complete local file cache. Each FTP session still transfers one file at a time while other transfers queue. Streaming does not mean every case is disk-free: FTP sources retain full caching under the compatibility conditions below.

For FTP sources, targets without atomic commit support (including current FTP/FTPS and plugin targets, or SFTP servers without POSIX replacement), symbolic-link destinations, and requests to resume an existing partial file keep the full-cache path. All files use randomly named, exclusively opened, delete-on-close files under `UserPathResolver.Home/.velashell/temp`. Download, cache flush, applicable length checks and final completion must succeed before uploading. Preparation is shown separately and cache bytes are not target-acknowledged bytes. This fallback requires sufficient local disk space and may contain sensitive content; local disk or permission errors are not automatically retried. Existing SFTP → SFTP streaming, bounded read-ahead for sequential plugin sources and same-session “Copy to” remain unchanged.

**Cache and retries:** Before commit, failure or cancellation on the streaming path only attempts to remove this transfer's remote temporary file, leaving the final file untouched. Automatic retries reopen the source from the beginning with a new temporary name and never use the final file as a resume candidate. Once commit starts, it waits up to 30 seconds for confirmation; cancellation cannot undo a submitted commit. An explicit server refusal preserves the actionable error and attempts to delete the remote temporary file. Only ambiguous outcomes such as disconnection or timeout disable automatic retries and retain any possible temporary file for inspection. Unknown-size sources receive an accurate final byte total after successful commit. The cache fallback still reuses the validated cache for automatic upload retries within one transfer. Manual retries and conflict renames prepare a new cache; final failure, cancellation and success close and delete it.

**Integrity checks:** When the source reports a positive length, the relay checks the actual read length; early EOF or a mismatch fails the transfer. FTP uses `SIZE` queried in binary mode rather than the directory listing's display size. If a known-length download receives `426` (transfer aborted) with a mismatched byte count, it still reports an incomplete transfer rather than hiding it behind a generic FTP operation error. If the server does not support `SIZE`, the unknown length cannot prove that the read is complete or that the source should be nonempty. Even a successful final reply cannot rule out a 0-byte or short read when the server falsely reports completion. Full caching does not remove this limitation. Neither path provides full-file remote hash verification or a source snapshot during transfer; only the streaming-commit path above provides atomic target replacement. Existing damaged files must be checked and transferred again.

**Retry diagnostics:** With **Settings → File transfer → Transfer logging** enabled, each failed attempt writes an `ATTEMPT_FAILED` record, even if a later retry succeeds. It includes attempt counts, error types, length-mismatch numbers and acknowledged destination bytes where available, but not exception messages, server reply text or additional file paths. A 0-byte progress display can mean the target has not acknowledged its first write; it does not prove that no source data has been read.

**Resume and limitations:** The complete FTP/FTPS cache allows reading from a specified position, so it can be used to verify the end of the portion already transferred to the target before resuming. This still depends on the target protocol, implementation and resume settings; it does not mean every target supports resume. SFTP → SFTP keeps its existing resume verification. Plugin sources that only support sequential reading do not gain resume support from this change; plugin targets require streamed-upload support, which does not guarantee resume support. Recursion does not descend into internal directory symlinks. These tabs are excluded from startup restoration.

**Can the servers transfer directly (server direct)?** Ordinary SFTP has no universal cross-server copy operation. Running `scp` / `sftp` / `rsync` on one endpoint through SSH is a separate option, requiring connectivity between the servers, the necessary tools, destination credentials and host-key verification. FTP FXP depends on both servers' support, security policies and network conditions. This change implements neither direct-transfer approach and does not forward credentials or an SSH agent to a remote server.

<a id="sync"></a>
## 10. Directory Comparison and Synchronization

### 10.1 Compare the Current Level

**Entry point and steps:** Navigate the independent local/remote panes to the directories and choose directory comparison. The application highlights items present on only one side, newer items, and conflicts, and reports counts. Navigating clears the result.

**Result and how it works:** This is nonrecursive difference highlighting, not an automatic transfer. Local/remote comparison uses comparison settings and can use SHA-256 by default. Dual-remote comparison uses size and modification time only. When remote dotfiles are hidden, matching local dotfiles are excluded too, avoiding misleading differences for `.git`, `.env`, and similar files.

### 10.2 Preview, Then Synchronize

**Data-loss warning:** Mirror mode can overwrite newer destination versions. Deleting extra files removes destination-only items, potentially entire directory trees. Wrong direction, paths, or masks can cause irreversible loss. Back up first and test on a small directory without deletion. **Do not test mirroring against a production root.**

**Entry point and prerequisites:** Choose synchronization on a single-remote file document's toolbar. Enter local/remote roots and confirm scan/write permissions.

1. Select local→remote, remote→local, or bidirectional.
2. Choose synchronize files, mirror files, or timestamps only.
3. Select SHA-256, modification time, and/or file size. Configure existing-only, masks, and deletion as needed.
4. Compare and wait for scanning. Review each proposed action, relative path, size, and time; deselect unwanted items.
5. Changing options invalidates the preview and requires comparison again. Synchronize only after review; deletion has an additional confirmation.
6. Read results and the automatic recheck. Address remaining differences rather than treating a completed progress bar as proof of equality.

| Mode/option | Effect and boundary |
| --- | --- |
| Synchronize files | Transfers missing, newer, or differently sized files without overwriting a newer destination |
| Mirror files | Source wins for differences, even if destination is newer; deleting extras is a separate option |
| Timestamps only | Transfers no content; changes destination time only when both files exist, sizes match, and times differ; no deletion |
| Bidirectional | Uses synchronize-files behavior, without mirroring or deletion; equal-time/content-different conflicts need manual decisions |
| No comparison criteria selected | Adds missing items only; does not inspect existing contents for differences |
| File mask | `include \| exclude`; separate masks with `;`, use `*`/`?`, and end directory masks in `/`; excluded directories are neither scanned nor deleted |

For example, `*.conf;*.txt | cache/;*.tmp` considers the listed types and excludes cache directories/temporary files. Still inspect the preview rather than guessing the deletion scope from the mask.

**Execution mechanism:** Create directories, adjust times, transfer files, then delete. Cancellation prevents proceeding with further deletion. Sync uses the transfer pipeline but does not repeat ordinary name-conflict prompts or resume probing: **the preview is the confirmation for this run**. It attempts to align modification times after transfer regardless of the ordinary preserve-timestamps switch.

### 10.3 SHA-256, Times, and Conflicts

**Prerequisites and result:** Hash comparison is useful when sizes match. Equal hashes avoid retransmission even if times differ. SFTP tries remote `sha256sum`/`shasum -a 256`; FTP/FTPS uses server checksum commands; plugin protocols (S3 and others) use the server-side checksum their plugin provides, if any (a plugin can also provide setting modification times, which two-way sync needs to keep times aligned; without it the usual fallback applies). Missing exec access/tools or unsupported servers cause fallback to time+size, with the reason shown. Dual-remote comparison does not use SHA-256.

**How it works and cautions:** Hashing reads both sides' storage and can slow the first large comparison. Caches live within the document/window. Content deliberately changed while size and modification time remain unchanged may evade cache invalidation; reopen comparison. FTP LIST times reflect server-local time and are offset across time zones: set the server time zone on the FTP connection's Advanced page to convert them (MLSD times are UTC and unaffected). Without it, size-only comparison may be a temporary choice, but equal size does not prove equal content. Unsupported timestamp updates, such as FTP without MFMT, may make bidirectional sync download a just-uploaded file again; address the warning first.

Type conflicts, case-only name differences, and illegal local filenames are not solved safely by blind overwrite. Directory symlinks are not traversed; remote file links are treated as files. Synchronization is file-level, not a transaction: a later error does not roll back earlier successful writes.

### 10.4 Keep the Remote Directory Up to Date

**Deletion risk remains:** Local changes automatically trigger remote updates; with deletion selected, local deletions may propagate remotely. Verify direction, masks, and backup first. Do not let build outputs or temporary files unexpectedly overwrite production content.

**Entry point and steps:** Choose keep-remote-up-to-date in the sync window, perform an initial full alignment, and keep both window and document open. Watch activity and stop when needed. Closing the window or file tab stops it.

**Result and how it works:** Local→remote only. A local filesystem watcher tracks subdirectories and debounces for about 1 second before resynchronizing affected directories. New directories are scanned recursively; watcher overflow triggers a full rescan. This is not remote bidirectional watching or a service that survives application exit. Sync options remain in the current document only and reset after restart.

<a id="tunnels"></a>
## 11. SSH Tunnels

### 11.1 Choose the Direction and Listening Side

**Exposure warning:** Use loopback `127.0.0.1` by default. Binding `0.0.0.0`, a LAN address, or a publicly reachable server interface can expose databases, internal services, or an unauthenticated SOCKS proxy. Check firewall, permitted sources, and authorization before public listening. Connectivity alone does not justify exposure. Remote SOCKS turns this computer into a proxy for the server: the allow list has no default and cannot be empty, so list only the `host:port` entries you actually need; a lone `any` lets the remote side reach everything this computer can.

**Entry point and prerequisites:** Title-bar tunnel icon, `Ctrl+Shift+T`, or Command Palette. A saved **SSH** profile is required. The panel does not use SFTP, FTP, or plugin profiles, and no terminal must be open first.

| Type | Listener | Destination is reached from | Typical use |
| --- | --- | --- | --- |
| Local `-L` | Local address/port | SSH server side | A local tool accesses an internal database reachable by the server |
| Remote `-R` | SSH server address/port | Local computer side | A server-side client reaches a service on this computer |
| Dynamic `-D` | Local SOCKS5 port | SSH server side | SOCKS5-capable applications access a network through the server |
| Remote SOCKS (`-R` without a target) | SSH server port (acts as a SOCKS5 proxy) | Local computer side, only destinations on the allow list | Server-side programs reach a few destinations through this computer |

### 11.2 Create, Start, Stop, and Recover

**Steps:** Select a server and create a forward. Set alias, type, and listening host/port; local/remote forwards also need destination host/port. Dynamic forwarding has no fixed destination; remote SOCKS takes a server listen address/port and an allow list (for example `localhost:8080 *.corp.example:443`). A local forward's destination defaults to the server itself (`127.0.0.1`); clear **Forward to the server itself** to use another internal host. Remote forwards and remote SOCKS default to **Listen only on the server itself**. Local/remote forwards can switch to **Use Unix domain sockets** and take socket paths instead: either end of a local forward may be a socket, while a remote forward needs both ends to be sockets; local paths must be full paths. Each tunnel can have a rate limit (KB/s, each direction separately, 0 = none), which takes effect the next time the tunnel starts. Save, start, and check status, cumulative traffic, and connection count. Configure the consuming application to use the forwarded port or SOCKS5 address. Stop when finished; deleting a forward asks for confirmation and stops it.

**Result and how it works:** The tunnel maintains SSH transport and opens channels for client connections. It does not automatically configure your browser/system proxy. The statistics line shows cumulative bytes; a running tunnel also has a live-rate line (↑/↓, averaged over the last three seconds) refreshed every second while the panel is open, and **Open connections: N** expands to list each connection's source → destination, age, and bytes in each direction. Local listener ports are prechecked for conflicts; if a file already exists at a local socket path the tunnel does not start and the file is not deleted for you (it is usually left over from last time; delete it once you are sure it is unused). Remote listening depends on the server's response, not whether the same port is free locally.

**Automatic behavior:** Saving does not mean listening at next startup; forwards default to stopped after restart. Explicitly enable startup establishment when needed. Reconnection after loss is separate and off by default. Tunnel retries back off through 10 seconds, 30 seconds, 1 minute, 2 minutes, and 5 minutes. Manual stop does not restart automatically. Fingerprint/authentication requiring intervention stops retrying. When setting up tunnels at startup fails, the notification carries **Retry**: a server that could not be reached is reconnected as a whole, a single tunnel that could not be set up is retried alone.

**Caution:** `-R` listens on the server; a loopback address does not mean the listener is on your computer. Remote SOCKS refuses destinations outside its allow list and notes them on the tunnel row. Remote forwards cannot mix a TCP end with a socket end. Editing configuration files does not make unexposed capabilities supported user operations.

<a id="x11"></a>
## 12. X11 Forwarding and X Server

**Trust warning:** X11 clients can potentially inspect windows, simulate input, and access clipboard data. Trusted programs sharing one display are not isolated security containers. Enable trusted forwarding only for trusted hosts. For less trusted targets, consider untrusted forwarding, per-session displays, and forwarded-program restrictions.

### 12.1 Run a Remote Graphical Application

**Entry point and prerequisites:** SSH configuration → Forwarding → X11; **Settings → X Server**; title-bar X Server icon. SFTP profiles do not show the Forwarding page. The server must permit X11 forwarding and have the application installed.

1. Select the built-in engine in X Server settings. Windows can instead use a separately installed VcXsrv executable.
2. Review display number, window mode, clipboard, and security options; save. Changes take effect at the next X Server start.
3. Enable X11 forwarding in the SSH profile. Leave the display address blank to use the local built-in server. Trusted forwarding (`-Y`) is currently checked by default: evaluate target trust, and uncheck it when those privileges are unnecessary.
4. Start X Server or enable automatic startup with X11 connections. Reconnect SSH, then start the installed graphical application remotely.
5. Close applications when finished. With the built-in engine running, click the title-bar icon again for the program-management popup to disconnect a program, release a grab, or stop the server; each program is marked **Trusted** (local programs and `ssh -Y`, with access to the whole X Server) or **Untrusted** (`ssh -X`). VcXsrv controls only start/stop the external process.

**Result and how it works:** Applications still run remotely. X11 protocol travels through SSH channels for local display. This is neither a remote desktop nor local installation of the remote application. The built-in engine is bundled across platforms; VcXsrv binaries are not bundled.

### 12.2 Display, Input, and Compatibility

**Operation and caution:** Multiwindow, windowed, undecorated, fullscreen, and rootless options control presentation. The built-in engine offers compositing, source indicators, and local IME options. Clipboard exchange follows the focused session; PRIMARY selection copying defaults to off to avoid unintended disclosure. Local files dragged into an X program are first uploaded to `/tmp/velashell-drop-…/` on that host; once every session to the host that was connected at the time of the drop has ended, they are removed in the background the next time it connects or something is dropped on it again, and a failed upload is removed right away.

**Mechanism and limitations:** Separate displays per SSH session reduce interference between remote hosts. Restrictions can disable input injection such as xdotool/XTEST; that is not automatically a fault requiring weaker security. Additional TCP listening on Linux/macOS is off by default and loopback-only if enabled. Built-in SSH channels do not require publicly exposing X ports. GTK4 forwarding terminals set `GSK_RENDERER=cairo` for compatibility, but Wayland, portals, OpenGL/GLX, and latency can still affect applications. See the troubleshooting references. Protocol support is not equivalent to a complete desktop environment.

<a id="operations"></a>
## 13. Resource Monitoring, Processes, Route Tracing, and Diagnostics

### 13.1 Resource Monitoring

**Entry point and prerequisites:** From a connected SSH session, use the status-bar resource entry or corresponding Command Palette action. The server must support the sampling commands and data sources.

**Steps:** Inspect CPU, memory, network, and available disk/GPU metrics; choose a 1/2/5/10-second refresh. Correlate sampling times with the event under investigation. Close the window when done.

**Result and how it works:** The host retrieves remote state over SSH and graphs it. These are not the local computer's Task Manager figures. Empty/unavailable metrics require checking remote platform, tools, and permissions; they do not mean zero usage. Sampling itself adds a small load.

### 13.2 Process Management

**Destructive-action warning:** Terminating processes/trees, force-killing, or changing priorities can affect other users, services, and unsaved data. Verify host, PID, owner, and full command. Prefer graceful termination; do not bulk-force-kill based only on a name.

**Entry point and prerequisites:** Title-bar process action / Command Palette with connected SSH. Primarily for Linux with tools such as `ps`; unsupported platforms show an unavailable notice.

**Steps:** Refresh defaults to every 2 seconds; choose 1 or 4 seconds or pause, and use F5 manually. Search by PID, name, command, or user; sort or view the process tree. Select and send SIGTERM or terminate a tree. Use SIGKILL/nice only when required, confirm, and inspect subsequent results.

**Result and how it works:** Remote commands perform the action under the login user's permissions. A PID's associated process can change between refreshes; recheck before acting. Pausing sampling does not pause processes.

### 13.3 Route Tracing and Offline Maps

**Entry point:** Title-bar route icon / Command Palette; target defaults to the current session host.

**Steps:** Enter a target and start. Run continuously or perform a three-round test. Inspect per-hop loss, RTT, and destination; stop at any time. For geography, select a downloaded offline `.mmdb`/`.gz` database. Tracing still works without it. The download entry opens a third-party page in your browser only when clicked.

**Result and how it works:** The implementation sends ICMP TTL probes from this computer. It is not remote `mtr`/`traceroute` or TCP SYN tracing. IP lookup happens offline; the map is not proof of exact physical paths.

**Caution:** Loss at an intermediate hop followed by normal later hops may be ICMP rate limiting, not equivalent business-traffic loss. Firewalls, load balancing, and asymmetric return paths also cause missing/changing hops. Trace text/JSON export and a fifth diagnostics stage are not currently integrated.

### 13.4 Connection Diagnostics and Diagnostic Packages

**Entry point and steps:** Open diagnostics for the current profile from tools / Command Palette. Run DNS → TCP → SSH → authentication, review timings/advice, and optionally export plain text. Address resolution, port reachability, SSH negotiation, and credentials separately.

**Result and how it works:** Stage-based diagnosis avoids blaming every error on passwords. It does not change firewalls or server settings, and it has no integrated route-tracing fifth step. When the authentication step fails on algorithm negotiation and the server offers algorithms this version implements but does not enable, the issue card shows **Allow these algorithms and diagnose again** (same rules as 16.3).

**Protocol trace:** The authentication step makes one full connection (through the jump chain), and every packet it sends or receives gets one line — time, direction, message type (such as `KEXINIT (20)`), length, sequence number and channel — grouped by hop on a jump chain. Switch the right-hand panel to **Protocol trace** to read it, copy it with the button at its top right, and an exported report includes it. **Only metadata is recorded, never contents**; authentication packets we send (such as a password request) do not even show a length, because a password's length could be worked out from it. Only diagnostics record a trace — ordinary connections and **Test** do not — and a run that stops before an SSH connection (an earlier step failed, or no usable credentials) has none.

**Session health check (a batch of hosts):** **Health-Check This Group** on a group in the explorer, **Health-Check N Connections** on a multiple selection, or **Session Health Check (All Connections)** in the Command Palette opens a results window that runs the same four steps on every host and lists the result (OK / Reachable / Needs credentials / Needs you / Unreachable / Timed out), the step it stopped at, and the details. It **runs only when you start it**, never on a timer: one batch at a time, up to six hosts at once, at most 30 seconds each; the title bar has Stop, Run again, Show problems only, and Copy as text. The health check **never pops up a dialog**: a connection without saved credentials, or whose saved password cannot be decrypted on this device (it is not tried for signing in), gets no login prompt and is marked *Needs credentials*; a host key that needs confirming (first seen or changed), a one-time code, a forced password change, or a Tailscale re-check is not answered for you and is marked *Needs you* — connect to those once yourself (a changed host key still raises a security alert). Agent authentication still goes through your local agent: if the agent itself asks before each use (`ssh-add -c`, a password manager's approval prompt), that is the agent's own prompt and it will still appear. The key icon in the title bar is *Verify login*: on (the default) it logs in once with the saved credentials and disconnects right away; off, it checks only up to the SSH handshake and never logs in. The change applies to the next run and is remembered until VelaShell exits. Only SSH / SFTP connections are checked; FTP and plugin protocols are listed as *Not checked*. Each row has buttons to open that host's connection diagnostics or edit its profile; after you save an edit, that host is checked again.

**A session that is already connected:** Right-click its tab → **Connection info…** (or **SSH connection info** in the Command Palette) for a read-only window listing the peer's version and host-key fingerprint, the negotiated algorithms, the authentication method that succeeded and server-sig-algs, how long each connection stage took and the last measured round trip, traffic and rekeying, the open channels, and the proxy and every jump host passed through (each with its own version and algorithms). The data comes from the connection itself: opening and refreshing the window sends nothing extra to the server. The title-bar buttons refresh it and copy the whole thing as text to share. When the session is not connected the window says so.

**Application problems:** Use **Settings → About → open logs / export diagnostics**. Exporting first shows a small checklist: the diagnostic logs (`velashell-*.log`), crash reports, version/platform information and the plugin list are always included; transfer records, the external-editing log (`remote-edit.log`), `vcxsrv.log` and each plugin's own log (`logs/plugins/<plugin id>.log`, one item per plugin) contain local and remote paths and file names and are **unticked by default** — tick the relevant one when reporting "upload fails" or "the plugin does not work". After confirming, choose where to save; the local zip is not uploaded, and settings, profiles, session logs and credentials are never included. Before writing, these are masked by rule: host and user names that appear in your saved connections, recent connections and shared credentials (in any form), the user and host in disconnect log lines, IPv4/IPv6 addresses (except loopback), lowercase domain names ending in a common top-level domain or internal suffix (such as `.com`, `.cn`, `.local`, `.internal`), the user name in `C:\Users\<name>`, `/home/<name>` and `/Users/<name>`, the local account name, machine name and home folder, email addresses, and password- or token-like values. Not recognised: single-label host names outside those sources (such as `db-prod-3`), user names elsewhere (such as IM user ids), file names and other path parts. So inspect the archive yourself before sharing and add reproduction steps without secrets.

<a id="recordings"></a>
## 14. Logs, Recording, and Playback

### 14.1 Three Different Ways to Save Output

| Feature | Entry point and result | Mechanism and boundary |
| --- | --- | --- |
| Buffer/selection export | Command Palette terminal-text export | One-time plain-text snapshot of retained content |
| Session logging | Settings → General → Data & Storage → session logs | New connections create `~/.velashell/logs/session-*.log`, preserving output including ANSI; retention cleanup applies |
| Session recording | Settings → Security Audit → automatic recording; playback center | Timed output events for variable-speed playback and `.cast` export |

**Privacy warning:** Recording is not just command history. Echoed commands, inspected configuration, tokens, business data, and control sequences can appear in output. On-disk masking is best-effort, not a guarantee that every secret is removed. Inspect recordings/logs before sharing; default masking does not make them public material.

### 14.2 Enable, Play Back, and Clean Up

**Prerequisites and steps:** Enable the needed logging/automatic recording and save for subsequent new connections; do not assume existing sessions are retroactively recorded. Open the playback center, select a recording, play/pause, choose 1/2/4/8/16×, and seek on the timeline. Disable default long-idle skipping if needed. Delete, clean up, or export asciicast v2 `.cast` files.

**Result and how it works:** Only remote output is recorded, not input as a separate stream. Input echoed by the remote still becomes output. Before storage, masking replaces recognized password/token/API-key values, Bearer/Basic credentials, PEM and OpenPGP private-key bodies, Kubernetes `tls.key` and kubeconfig `client-key-data`, Docker registry credentials (`.dockerconfigjson`, the `"auth"` field of `config.json`), `.netrc` `password` entries, GitHub `github_pat_`, GitLab `glpat-`, Stripe `sk_live_` / `rk_live_`, Google `AIza…`, npm `npm_…` and PyPI `pypi-…` tokens (the length and characters after the prefix must fit the format, so `pk_live_` publishable keys, `npm_config_…` environment variables and package names such as `pypi-attestations` are left alone), and similar patterns with `***`. An unquoted value after `name: ` (colon followed by a space) is masked to the end of the line, stopping at the next key on the same line or a `#` comment; `name=value` is still masked up to the first space. A keyword must be a whole word in the name: `max_tokens`, `tokenizers` and `passwordless` do not count, nor does a host name in a `user@host:path` prompt; nor do token and secret when directly followed by type, endpoint, name, count, limit, url / uri, expires / expiry / expiration or ttl, since such a name describes the token rather than holding it (`token_type`, `token_endpoint`, `secretName`, `token_count` and `token_uri` are kept, while `access_token`, `secret_id` and `secret_key` are still masked); `Basic` only counts when followed by base64 that decodes to `user:password`, and `Bearer` followed by an English word such as `authentication` is left alone. Colour and other terminal control sequences are stripped before matching, so a keyword or token split by `grep --color` highlighting, or a value coloured by jq / bat, is still recognised; control sequences inside a masked span are kept, so replay colours stay intact. In a multi-line private-key block only base64 body lines are masked (line numbers and file names added by `cat -n` or `grep` are kept), header and blank lines are left as they are, and a line that cannot belong to a key block (an `echo`ed BEGIN line, truncated output) ends the block so later output is recorded normally. Screen display and remote content are unchanged.

**Storage boundary:** Recording chunks are stored in the local database. Storage failures or a full pending-write queue stop recording and notify Message Center; saved portions remain playable. Playback does not rerun commands or alter the server. Local logs do not replace server-side auditing/compliance retention and are not guaranteed tamper-proof. Retention and manual cleanup affect traceability; archive according to organizational requirements first.

<a id="settings"></a>
## 15. Settings: All Available Pages

**Common entry point and operation:** Press `Ctrl+,`, choose a page on the left, make changes, and save settings. Except for explicitly immediate management actions, editing a field is not proof of persistence. Follow instructions for restart, reconnection, or next X Server start. There are **15 actual navigation pages**, not the nine/twelve in historical material.

### 15.1 General

| Section | Controls | Mechanism, result, and caution |
| --- | --- | --- |
| Startup & Window | Start at login, restore sessions, restore split layout, minimize to tray on close, startup update check, automatic update download | Autostart currently supports the current Windows user only; restore sessions alone reconnects one by one into a single pane, and only **Restore split layout** (off by default, available while restore sessions is on) also rebuilds splits, tab order, and pinned tabs, covering the connections online at exit plus local terminals; tray presence is not exit; automatic download is off by default, rides on the startup check, and only downloads and verifies without restarting (see 2.4) |
| Message Center | Update notices, HTTPS feed URL, interval 1–168 hours, announcements | Startup update messages require both checking and notifications; clear the feed URL to stop that source |
| Language & Region | System language or Simplified Chinese/English/Traditional Chinese/Japanese/Korean | Does not change remote system language or terminal byte encoding |
| Connection Defaults | Port, timeout, keepalive | Defaults for new connections; supported per-profile overrides apply; proxy has a separate page |
| Data & Storage | Session logging/retention, application-settings JSON import/export (by category) | Logging defaults off; settings JSON is not a connection backup; Security Audit and Network Proxy are unchecked by default, the proxy password and alert Webhook URL are never written, and import overwrites only the categories that are in the file and checked |
| Behavior | Exit/tab-close confirmation, status refresh, follow active session, initially collapsed groups, disconnect sound, automatic reconnect attempts/interval | Attempts 1–20, maximum interval 1–300 seconds; dependent controls appear where applicable |
| Privacy & Security | Remember passwords, clear recent connections | Clearing recent history asks confirmation; does not delete saved profiles or all logs |

**Recommendation:** Understand tray behavior before relying on background IM/tunnels. Explicitly quit to stop all background capabilities. Disabling confirmations increases accidental production-session closure risk.

### 15.2 Appearance

**Entry point and steps:** Adjust theme, rendering, fonts, window, terminal colors, and ANSI palette; save and inspect previews/new windows.

- **Theme:** Named theme, whether accent follows theme, background image and image/content opacity.
- **Rendering/fonts:** Hardware acceleration and UI font/size; terminal fonts are configured separately.
- **Window:** Opacity, Explorer side, tab bar position (top / bottom, all panes at once), multiline tabs, initial window state, Windows splash screen. Restart when indicated.
- **Terminal colors/ANSI palette:** Select the terminal scheme and base/ANSI colors used for remote color sequences.

**Mechanism and caution:** Appearance does not change remote file contents. Background images may disclose local pictures during screen sharing. Low contrast/excessive transparency impairs command readability. Tab bar position applies to horizontal tab strips only: a pane whose tabs were moved to the left / right from the tab context menu stays there and lands on the chosen edge once moved back to horizontal.

### 15.3 Terminal

| Section | Available controls and ranges | Boundary |
| --- | --- | --- |
| Font & Display | Font, size 8–72, line height 0.8–2, padding 0–40; auto-open file browser; line times/milliseconds, numbers, separators, fold markers, gutter spacing | Monospaced fonts improve alignment; displayed times are client aids; folding needs recognized boundaries |
| Terminal Type & Encoding | TERM, character encoding | Match remote capabilities; fonts cannot repair decoding errors |
| Cursor | Bar/block/underline, blinking | Presentation only, not remote editor mode |
| Bell & Tab Notifications | System sound/none/visual flash, background-tab notification, remote clipboard writes (OSC 52) | Use conservative settings for untrusted hosts |
| Session | Post-connect command, working-directory reporting, read-only bash/zsh integration snippets | Commands run on new connections and reconnects; manually install the appropriate snippet in shell configuration |
| Scrolling | Scroll on output/keypress, alternate-screen wheel-to-arrow, scrollback 100–200000 | Large history uses memory; output beyond the limit is discarded |
| Selection & Copy | Copy on select, right-click paste, trim trailing spaces, double-click word selection | Changes gestures; trimming can alter significant whitespace |
| Input | Multiline confirmation, IME, local echo, Ctrl+C copies selection | Local echo can reveal passwords; ordinary SSH shells generally need no extra echo |

**Per-connection overrides:** The SSH Terminal page offers post-authentication command with 0–60-second delay, encoding, TERM, tab color, initial directory, keepalive, and anti-idle. The global post-connect command runs first, then the profile's command; both also run after reconnect. Avoid unintended side effects. There is no per-profile terminal color-scheme override control.

**Output Rules:** A separate page right below Terminal manages custom highlighting and auto-answer (rule list, editor, test); rules that apply to one connection or group pick their scope there. See 7.7 for usage and limits.

### 15.4 Key Management

**Entry point, steps, and result:** Search, generate/import (import validates the private key), copy or export public/private keys, set a default, or add keys to the Agent after login; see [authentication](#authentication). Deletion immediately affects real private-key files. Verify path and backup before confirming; it is not a draft waiting for Save Settings.

### 15.5 Shared Credentials

**Operation and mechanism:** Create, search, edit, or delete shared identities. Management changes take effect immediately; referenced profiles read updated material on the next connection. Check affected profiles before editing. Passwords/passphrases absent from sync without end-to-end encryption reflect its security design, not accidental credential loss.

### 15.6 Keyboard Shortcuts

**Steps:** Search an action, click the pencil, and press a new combination. Esc or clicking elsewhere cancels capture. Unbind stops the application action from consuming a key; restore resets that action, and restore-all resets all customizations. Save to apply.

**Rules and mechanism:** Window-level and some terminal actions are editable; fixed gestures are not. Combinations require a modifier such as Ctrl/Alt/Win/Cmd, except unmodified F1–F24 (`F2` and `F5` stay with the file panes' rename and refresh). Fixed terminal keys are reserved; replacing conflicts needs confirmation. Window shortcuts run before terminal input; `Ctrl+letter` is often a terminal control character. If a remote program does not receive a key, unbind/remap locally before changing the server. See the [shortcut table](#shortcuts).

### 15.7 File Transfer

| Section | Available controls and ranges | Caution |
| --- | --- | --- |
| Default Paths | Local download directory, default editor path/browse | Independent dual panes download to the current local directory; defaults are not forced destinations |
| Remote Editing | Double-click system/built-in/default editor, auto-upload after editing | Saving a local temporary copy can write to production remotely; auto-upload only covers the external editor and system default app, while saving in the built-in editor always uploads |
| Transfer Behavior | Concurrency 1–16, preserve timestamps, fsync after upload, completion notices | fsync requires server support; higher concurrency is not always faster |
| Conflict Handling | Ask/overwrite/skip/rename, hidden files, fast recursive deletion, resume, retry failed transfers 0–10 times | Fast deletion requires SSH exec and falls back to SFTP on failure; resume retains partial files; failed-temp cleanup is shown only when resume is disabled; only temporary failures such as disconnects and timeouts are retried |
| Bandwidth Limits | Enable, upload/download MB/s, 0 unlimited | Limits application transfers, not other server applications |
| Logging | Transfer logs, retention 1–365 days, directory | Paths/filenames can be sensitive; check access and disk space |

**Mechanism:** After you save, open file panes (the terminal side pane and single- or dual-pane file tabs) switch to the new options immediately; there is no need to reopen them. A batch that has already started is not reworked: its name conflicts were settled under the policy in effect when it started, and its concurrency limit stays at that value; the retry count applies from the next file on; bandwidth limits, timestamp preservation, and fsync after upload are read when each file starts.

### 15.8 Security Audit

**Sections:** Session recording, audit logs, host trust policy, trusted hosts (with OpenSSH known_hosts import/export), trusted host CAs, external login (Xshell compatibility), and security alerts.

**Operation:** Set automatic recording and on-disk secret masking; open playback/audit viewers (the audit viewer filters by category and time range, exports CSV, and opens the profile behind a profile record); set audit retention to 1–3650 days; control first-use confirmation and changed-key blocking; inspect/delete trusted records (addresses hidden by default); import `known_hosts` (plain lines become trusted hosts, `@cert-authority` / `@revoked` lines go to trusted host CAs) or export in OpenSSH format (optionally hashing host names); add/remove trusted host CAs; accept/confirm external logins, register protocols, or clear external-target trust; configure in-app/sound/Webhook alerts.

**Caution and mechanism:** First-use fingerprint confirmation is on by default (matching OpenSSH's `StrictHostKeyChecking=ask`; it used to default to off and is switched on once when you upgrade, after which your choice stands). Disabling it changes to trust on first use (TOFU), not stronger verification. Disabling changed-key blocking leads to manual adjudication, not a reason to accept a change. Webhooks can disclose event information to the configured service; verify trust and organizational authorization. Auditing records creating, changing, and deleting connection profiles and shared credentials and settings changes, but only which part changed, not the values. Each configuration record carries its source (local edit, cloud sync, import, or the login prompt's "Also update the shared credential"), shown in the viewer and exported as a CSV column. It is not a complete tamper-proof ledger.

### 15.9 Network Proxy

**Operation:** Select none/system/HTTP/SOCKS5, set credentials and remote DNS as needed, save, and retry. See the [proxy chapter](#network) for scope and the first-hop boundary. A masked password field protects display only, not the network or backups.

### 15.10 X Server

**Available controls:** Engine selection on Windows; display number, window mode, clipboard, keyboard layout, and startup. Built-in-engine options also cover compositing/source indicators, local IME, security restrictions, and per-SSH-session displays. Linux/macOS can optionally enable additional loopback TCP. VcXsrv options include executable path, keyboard model/special-key capture, native OpenGL, access control, extra arguments, command preview, and tray icon.

**Steps and result:** Configure, save, and restart X Server; enable X11 in the relevant SSH profile too. Extra arguments may override security behavior; do not casually disable access control. Keep defaults if unsure. This page does not install VcXsrv.

### 15.11 Snippets

**Operation:** Create/edit/delete groups and quick commands, fill bodies and placeholders, then use the sidebar panel. Snippets are terminal text, not scheduled jobs. See [Quick Commands](#workspace) for multiline warnings.

### 15.12 Cloud Sync

**Sections:** Account/binding, end-to-end encryption, sync scope, operations, and version history.

**Steps:** Enter a GitHub PAT with gist permission, Gist ID/device name, and preferably an end-to-end passphrase. Select scope and save, then run bidirectional/push-only/pull-only sync. Enable automatic sync or refresh/restore history as needed. See [backup](#backup) for conflicts and migration boundaries.

### 15.13 About

**Operation and result:** View version, contributors, and project entry points; check updates, restart to finish, or repair according to state. Open logs or export a local diagnostic zip (transfer, plugin and a few other logs can be ticked as well; unticked by default). Linux installs updates manually; Store updates are managed by the Store. Contributor avatars are requested from GitHub. Diagnostics are not uploaded automatically; inspect before sending.

### 15.14 Support & Donate

**Entry point and steps:** Read contribution information. If you choose to donate, use the shown Alipay/WeChat Pay QR codes or international Wise QR/link/copy-link controls. Check recipient details and amount on the payment page before paying voluntarily.

**Result and mechanism:** Donation is not a switch that purchases server authorization, protocol access, plugin permissions, or AI quotas. It does not connect you to servers or fund a third-party model account.

<a id="security"></a>
## 16. Security, Host Fingerprints, and Auditing

### 16.1 First-Use Fingerprints and Changed Keys

**Change warning:** A new fingerprint may indicate a reinstall/key rotation, or a DNS/proxy/routing problem or man-in-the-middle attack. Matching connection names/usernames or a fingerprint supplied by the same unverified endpoint is not enough.

**Entry point and steps:** Check target, algorithm, and SHA-256 fingerprint in first-use/change dialogs. Verify independently before permanent or one-time trust; reject mismatches. First-use confirmation is on by default: an unseen host gets the dialog first and is recorded only after you trust it (hosts vouched for by a trusted host CA excepted, see below). Security Audit settings display trusted hosts and changed-key blocking. Deleting a record changes future trust decisions; it does not fix the server. If automatic reconnect stops on trust, manually reconnect to review it.

**Result and how it works:** Trust associates a target with a server key to protect passwords/signatures against an impostor. The host's interactive flow is not the SSH library's default rejection policy; Security Audit settings control application behavior. Verify each jump independently; external launch does not bypass checking.

**Trusted host CAs:** When servers use OpenSSH host certificates, add the CA public key and host-name patterns under Settings → Security Audit → Trusted host CAs (same syntax as `@cert-authority` in `known_hosts`; you can also import them from `known_hosts`). Matching hosts that present a certificate signed by that CA, listing the host name as a principal and within its validity period, are then trusted without a dialog. An invalid certificate, or a covered host presenting a plain key the CA does not vouch for, is **rejected instead of falling back to first-use confirmation**; for a host that really has no certificate, verify its fingerprint and import its key from `known_hosts`. `@revoked` entries override any trust.

### 16.2 Agent Authentication Is Not Agent Forwarding

**Signing-permission warning:** Forwarding does not copy the private-key file to the server, but remote root or a compromised program may request signatures through the channel and access other systems as you. Do not enable it for untrusted hosts or equate a locally retained private key with zero risk.

**Entry point and prerequisites:** SSH connection → Forwarding → Agent forwarding. It is independent of Agent authentication, disabled by default, and not shown for SFTP profiles.

**Steps:** Enable only when necessary. Limit forwarded keys and request confirmation before each signature; limited-key mode requires at least one key. Verify source/purpose when prompted. No approval within 60 seconds means rejection. Disable forwarding and reconnect after use.

**Result and how it works:** Remote programs call the local Agent through SSH forwarding channels. Merely using a jump host does not require automatically enabling Agent forwarding.

**Choosing an agent per connection:** SSH/SFTP profile → SSH options → Agent endpoint. Leave it empty for the system default (`SSH_AUTH_SOCK`; on Windows the OpenSSH Authentication Agent service, or Pageant when that service is not running). With a Windows named pipe `\\.\pipe\name` or the full path of a Unix socket (it may start with `~/`), this connection's Agent authentication, Agent forwarding and "Add keys to agent" all use only that agent, like `IdentityAgent` in ssh_config; each hop of a jump chain uses its own. A malformed value is flagged under the box but does not block saving, and a connection failure names the endpoint.

### 16.3 Algorithm Compatibility and Compression

**Entry point:** SSH/SFTP profile → SSH options.

**Steps and result:** Compression defaults off; enable post-authentication compression if needed. For an old device's negotiation failure, verify server requirements first. Only then consider legacy algorithms or text overrides for KEX/HostKey/Ciphers/MACs, including OpenSSH-style `+`/`-`/`^` adjustments.

**Mechanism and caution:** Legacy compatibility adds a limited set such as group14-sha1, ssh-rsa, and hmac-sha1; it does not offer CBC. Weaker algorithm compatibility cannot repair fingerprints/passwords. Apply it only to affected profiles, not indiscriminately to all hosts.

**One-click fix after a negotiation failure:** When the failure card of a terminal tab or a standalone SFTP document shows **Allow these algorithms and reconnect**, the server offers algorithms this version implements but does not enable by default. Clicking it asks once more, then appends **only the algorithms the diagnosis named** (with `+`) to the custom algorithm list of the hop that failed (the jump host's own profile when it was a jump host) and reconnects. The legacy-algorithms switch is left alone. The issue card in the Connection Diagnostics window has the same button as **Allow these algorithms and diagnose again** (it changes the profile and runs the diagnosis once more); when **Test** fails on the connection profile page, the feedback bar offers **Allow these algorithms and test again** — that one changes **the form you are editing** (the custom algorithm lists open up) and is stored only when you click Save or Connect, unless the failing hop is a jump host, whose own profile is changed right away.

**Obscure keystroke timing (SSH only):** Input goes out on a fixed 20 ms beat and cover packets fill the empty beats while you type, closing the side channel where key gaps reveal what is typed. The cost is about 50 packets a second each way while typing and up to 20 ms extra delay per key; off by default. Cover packets need the PING extension (OpenSSH 9.5+); without it the terminal shows a yellow note that input is only batched.

### 16.4 Local Encryption and Audit Boundaries

**Operation and result:** Passwords/key passphrases (including those of shared credentials and the proxy password in the Network Proxy settings) are encrypted with AES-256-GCM under the data root. The alert Webhook URL is not treated as a password and is stored in the settings in plain form (it never leaves the device through settings export or sync without a passphrase). Filter the audit viewer by category, time range, and keyword, export the records shown as CSV, and configure retention. Remove unnecessary host/account details before sharing.

**How it works:** The local encryption key is essential decryption material. On macOS it is kept in the login keychain and on Linux in the Secret Service (gnome-keyring, KWallet, and the like); the data root only holds `secret.key.ref`, which lists entry ids and contains no key. The first start after upgrading from an older version moves the existing `secret.key` there and scrubs and deletes it from the data root only after reading it back and checking it. If the system keychain is unavailable (libsecret not installed, no desktop session or session bus, the unlock prompt for a locked keyring cancelled, access denied in a macOS keychain prompt), the key stays in the data root as `secret.key` (plain, 0600), the message center says so once, and the next start after the keychain becomes available moves it there. If the keychain cannot be opened at some start after the key has moved in, saved passwords cannot be decrypted during that run but are not lost: the message center explains, and the next start after it is back reads them as usual; passwords saved in the meantime are encrypted with a local key in the data root and merged into the keychain automatically later. On Windows `secret.key` is wrapped with current-user DPAPI; copying across Windows users or reinstalls usually cannot unwrap it. The macOS keychain entry is open to any program running as your user (on par with the former 0600 key file), and an unlocked Secret Service is likewise open to programs in your session: this protects against taking, backing up, or syncing the data root, not against programs running locally as you. A local process with access to data and usable key material may recover secrets. This is not a vault that defeats a compromised computer.

**Caution:** Profile and settings audit records name only the part that changed, not the values. When a group is deleted, the profiles still in it are moved back to ungrouped and recorded as one summary entry (group name and count), not one entry per profile. Logs/recordings are not guaranteed complete, undeletable, or tamper-proof. Master-password protection is not integrated; do not look for a master-password control to protect the whole database.

<a id="backup"></a>
## 17. Gist Sync, Backup, and Migration

### 17.1 Distinguish Four Data Operations

| Purpose | Entry point | Included/excluded and protection |
| --- | --- | --- |
| Move application preferences | General → Data & Storage → settings import/export | **Unencrypted JSON**; settings only, chosen by category, no profiles/groups/keys/snippets; Security Audit and Network Proxy are unchecked by default, and the proxy password and alert Webhook URL are never written, so re-enter them; still may contain personal/network configuration (proxy address, external-login trusted targets, local paths) |
| Move connections | Explorer connection-file export/import | JSON can include passphrase-encrypted credentials; CSV exports never include secrets; key files are separate |
| Sync devices | Settings → Cloud Sync | A secret Gist in your GitHub account, optionally end-to-end encrypted; not a full disk mirror/backup |
| Disaster recovery | Fully quit, then securely back up data root and actual keys | Preserve decryption conditions: on macOS/Linux the local encryption key is in the system keychain, not the data root, and travels with the login keychain/keyring (whole-system backup or migration); on Windows DPAPI/user constraints apply. A raw copy of the data root alone does not decrypt saved passwords on another computer or account |

**Sensitive-backup warning:** Leaking the database with usable `secret.key` (on macOS/Linux it is in the data root when the key fell back to a file) can expose credentials; copied private keys are also authentication material. Encrypt backups, restrict access, and keep export passphrases separately. Never commit `~/.ssh`, secret-bearing JSON, or Gist passphrases to Git or paste them into support chats.

### 17.2 Configure and Run Gist Sync

**Entry point and prerequisites:** Settings → Cloud Sync, a GitHub account, a PAT with the necessary gist permission, and GitHub connectivity.

1. Enable sync and set token/device name. Leave Gist ID blank for initial creation, save, and push once. To bind another device, use the created Gist ID.
2. **Set an end-to-end passphrase where possible.** Use the same passphrase on devices and store it securely offline. A secret Gist without it is unlisted, not an encrypted vault.
3. Choose scope: settings, connections/groups/tunnels/shared credentials, snippets, layout templates, and other available categories; save.
4. Establish which device is authoritative before the first push-only, pull-only, or bidirectional run. Check results before enabling automatic sync.
5. Bind the other device with Gist ID, token/passphrase, then pull and inspect hosts, proxy, local paths, and credentials.
6. Refresh version history to restore a revision when needed. Independently back up before important overwrites.

**Result and how it works:** With a passphrase, AES-GCM encryption occurs locally before upload; GitHub stores ciphertext. Without one, connection/shared-credential passwords and private-key passphrases are removed, as are the proxy password and alert Webhook URL in application settings (the same list settings export leaves out), but hosts, usernames, notes, key paths, and the proxy address still upload. Pull upserts by ID; conflicts generally prefer newer data. Local-only connections are not deleted, some device settings remain local, and secret-free payloads do not erase existing local passwords. When pulled settings lack the proxy password, the local one is kept only if the proxy type, host, port, and user name all match; the local Webhook URL is always kept. If pulled settings have Webhook alerts on but this device has no Webhook URL (sync without a passphrase does not carry it and none was filled in here), alerts cannot actually be sent: the message center says so once, with **Go to Security & Audit** to fill it in; pulling the same settings again does not repeat it.

**Layout templates (8.4):** A separate file in the same Gist, `velashell-layouts.json`, encrypted or not along with the end-to-end passphrase. It does not follow the whole-payload "newer wins" above; every sync merges it template by template — when both sides changed the same template the later edit wins, templates saved on two devices both survive, and deletions reach the other devices (a deleted template leaves a record with its deletion time, kept for 180 days; a device that stays offline longer than that brings its copy of a deleted template back). Two templates created on different devices under the same name are neither merged nor renamed; both are kept, so rename or delete one yourself. Older versions without this feature only read and write the main file and never touch this one, so they cannot erase templates. If the cloud copy was written by a newer version, or is encrypted while this device has no passphrase or a different one, only the templates are skipped that time, and the result says so. Restoring a revision brings back every template in that revision, including ones deleted or changed since.

**Caution:** Sync is not a multiuser editing transaction, complete file backup, or recoverable server-side passphrase escrow. Syncing key paths does not copy keys. Check details such as bookmarks rather than assuming old payloads contain all current fields. Verify device-local items after historical restoration, not just the success notice.

### 17.3 Locate Your Data

**Entry point and location:** Regular distributions default to `~/.velashell`, where `~` means your home directory. On Windows, enter `%USERPROFILE%\.velashell` in File Explorer; on Linux/macOS use `~/.velashell`. Store data may be redirected and is not necessarily the portable edition's data. Logs can also be located from Settings → About → open logs.

| Data | Default location | Backup purpose and boundary |
| --- | --- | --- |
| Profiles, groups, settings, trust, history, audits, recordings, plugin storage | `~/.velashell/sonnetdb` | Embedded database directory; fully quit before backup, not a live copy of one chosen file |
| Credential-decryption key | macOS: login keychain, service `com.velashell.master-key` (search for VelaShell in Keychain Access); Linux: Secret Service, schema `com.velashell.MasterKey` (search for VelaShell in Seahorse ("Passwords and Keys"), or run `secret-tool search --all xdg:schema com.velashell.MasterKey`; the output includes the key itself, so do not share it); `~/.velashell/secret.key.ref` in the data root is only the list of entries. On Windows and in the fallback case: `~/.velashell/secret.key` | Needed for encrypted credentials; deleting the keychain entry or `secret.key.ref` makes saved passwords undecryptable. Uninstalling the app does not delete the keychain entry; to remove it completely, search for VelaShell in the places above and delete it. Protect any backup of `secret.key` (plain in the fallback case); Windows also requires the original DPAPI conditions |
| Session, transfer, and diagnostic logs | `~/.velashell/logs` | May contain output/paths; a different transfer-log directory can be configured |
| User-installed plugins | `~/.velashell/plugins` | Separate from bundled `plugins/` under the application; verify compatibility/permissions after migration |
| Offline geographic database | `~/.velashell/geoip` | Optional data, not connection credentials |
| SSH private/public keys and certificates | Commonly `~/.ssh`; follow actual profile paths | Not embedded in profiles; securely back up actual files separately |

**Mechanism and caution:** SonnetDB stores documents and time-series data. Historical `settings.json`/`sessions.json` migration files are not the sole current configuration. Starting with `--data-root <directory>` selects another data root and single-instance scope. Check startup arguments before treating a different database as lost data. This option does not migrate existing data or move keys from `~/.ssh` into that root.

### 17.4 Recommended Migration Procedure

**Prerequisite:** The old machine can still decrypt its data, and the new machine has a trusted package.

1. Export connection JSON (with a passphrase encrypting its credentials) plus application settings separately, or use Gist sync with an end-to-end passphrase.
2. Encrypt and back up actual private keys/certificates and necessary public keys separately. Record original paths and purposes, not just path strings from profiles.
3. Import/pull on the new machine, re-enter proxy passwords and alert Webhook URLs (Gist sync with an end-to-end passphrase brings them along, so no re-entry is needed), restore keys, and correct paths/permissions. Reconfigure local Agent, external editor, X Server, plugins, AI access, and other device-specific items.
4. Verify a test connection's fingerprint/login, then important profiles. Do not delete old data before recovery is confirmed and retention obligations are met.

**Mechanism and caution:** Current-user Windows DPAPI prevents assuming raw database/key copies work after account changes/reinstall; on macOS/Linux the key is in the system keychain rather than the data root, so a raw copy of the data root on a new computer or another account cannot decrypt saved passwords either. This is intentional; migrate with connection JSON or Gist sync with an end-to-end passphrase as in step 1. A data root that was already copied over does not break anything: the message center says why (on Windows the `secret.key` that cannot be unwrapped is kept as `secret.key.unreadable-<timestamp>`), the login dialog opens when you connect, and you enter and remember the password again; the entries on the old computer are untouched, and its data root still decrypts there. Explicit import/sync is also needed across Store/portable isolation. Missing proxy passwords and Webhook URLs, and the Security Audit and Network Proxy categories being unchecked by default, are intentional redaction in settings exports, not corruption.

<a id="plugins"></a>
## 18. Plugin Management, Optional Protocols, and Docker

### 18.1 Install, Update, Disable, and Revoke Permissions

**Permission warning:** Plugins run with user privileges and can access local files, networking, and sessions. Host capability grants are not an operating-system sandbox for all plugin code. Install trusted packages only; investigate publisher changes and trust warnings rather than approving merely to try a feature.

**Entry point and steps:** Open Plugin Manager. Install a `.vpx` using its top action, or obtain one through the marketplace browser entry. Review package/publisher information and explicit prompts. Enable after installation and reopen relevant UI when needed. Opening the manager checks user-installed plugin updates; downloading happens only after Update is selected and only over https, the marketplace must declare the package's SHA-256 digest (no digest or an http address means no download), the digest is checked after downloading, and installation checks the package signature (bad signatures are always rejected; unsigned packages or unknown publishers need your explicit approval). Disable/uninstall user plugins as needed. Developer reload is not required for normal use. When a plugin misbehaves, use **View log** on its row: a read-only window shows the last 256 KB of that plugin's own log (what it writes through its logger, the standard output and error of its isolated process, and the activations, failures and crashes the host records), with refresh and open-containing-folder actions (`~/.velashell/logs/plugins/<plugin id>.log`, rotated once at 1 MB; deleted on uninstall, or on the next start if the plugin's folder was deleted while VelaShell was not running; left out of diagnostic bundles unless ticked for that plugin in the export checklist).

**Result and how it works:** Plugins contribute protocols, workspaces, commands, and UI. Availability depends on installation, enablement, and compatibility. Terminal writes and opening saved-profile sessions have separate host authorization gates. Revoke Authorization clears both relevant categories, not just the current dialog decision. Versions are compared by SemVer 2.0 (the same rules as `vela-plugin` and the plugin store): with a pre-release such as `1.2.0-beta.1` installed, the store's `1.2.0` counts as an update, `beta.10` is newer than `beta.2`, and no update is offered when the versions cannot be compared. Plugin manifests are checked by the same rules: numeric pre-release parts must not have leading zeros (write `1.0.0-beta.1`, not `1.0.0-beta.01`) and must not be empty (`1.0.0-a..b`). Such packages are refused at install time, and an installed one shows as invalid with the fix spelled out; uninstall it and install a package with a corrected version.

**Caution:** Disabling cannot undo written files or remote actions. Update checks send required information such as user-installed plugin IDs when the manager opens, not a startup timer automatically downloading every plugin.

### 18.2 Use Optional Protocols

**Prerequisite:** AI is bundled. Redis, S3, Telnet, serial, and Docker plugins are installed on demand. Missing entries without their plugins are not host faults.

| Plugin | Entry point, steps, and result | Mechanism and particular cautions |
| --- | --- | --- |
| Redis | Install/enable; create a Redis profile with host, usual port 6379, account/password, standalone/Sentinel/cluster, database, TLS, and environment. Optionally tunnel through a saved SSH profile. Use SCAN-based key tree, value view/edit, console, slow log, and overview | A Redis workspace, not a VT shell; cluster uses db0 only. Production defaults read-only and prohibits flush; dangerous commands require confirmation. Server TLS/ACL still apply |
| S3 | Create S3 with endpoint, Access Key ID/Secret, region, addressing style, TLS, default bucket, and STS token for temporary credentials. Use file panes plus bucket management, object details, and presigned URLs | Object storage, not SSH; TLS default. No POSIX permissions or upload resume; downloads can resume. Presigned URLs grant access during their validity: check scope/expiry before sharing |
| Telnet | Create Telnet with host/usual port 23, TERM, CRLF/CRNUL/CR, 8-bit mode, and local echo. Log in inside the terminal | Plaintext, without SSH fingerprints/encryption; avoid untrusted networks. Login is not the host's SSH authentication form |
| Serial | Create serial connection, refresh COM or `/dev/tty…` devices, choose port, baud (default 115200), data/parity/stop bits (8N1), flow control, CR, DTR/RTS, echo/newline/send delay. Open the device terminal | No host:port, SSH jump host, or network encryption; requires driver, device permission, and correct cable. BREAK/DTR actions target the most recently input serial session, not necessarily the currently visible tab |

**Execution warning:** Redis writes/flushes, S3 deletions, and serial control signals can change production data or hardware state. Verify environment, backup, and authorization before writes. A protocol entry does not create server permissions.

### 18.3 Docker Management Panel

**High-privilege warning:** Docker socket access often grants extensive host privileges. Verify the host before stopping/deleting containers, images, volumes, or networks, or executing commands. Removing volumes may permanently destroy data.

**Entry point and prerequisites:** Install the Docker plugin, then use the Command Palette's Docker management action. Remote use requires existing SSH and permission to access the Docker Unix socket, commonly `/var/run/docker.sock`. Plugin-provided local Docker endpoints are also available.

**Steps:** Select the correct endpoint and inspect overview, containers, images, volumes, networks, and Compose pages. Review details/logs before start/stop/cleanup. Confirm the host in destructive-action prompts. Remote Compose requires `docker compose` installed remotely; local endpoints do not offer the same Compose functionality.

**Result and how it works:** Remote access uses SSH `direct-streamlocal` to the socket, without local public listeners or exposing Docker on 2375/2376. The panel console makes individual command requests, not a full interactive PTY. File editing has a 2 MB limit. Endpoint selection determines the affected host; it is not necessarily the active terminal tab.

<a id="ai"></a>
## 19. AI Providers, Chat, Agent, MCP, and IM

### 19.1 Understand Data and Execution Boundaries First

**Disclosure/write warning:** Questions, terminal context, referenced remote files, and tool results can leave your computer for a provider or relay; IM also traverses the chat platform. Web search (`web_search` / `web_fetch`) is **off by default**. Once you enable it in the AI global settings, the search words the model writes (often error messages, host names, internal service names) are sent to the SearXNG instance in that setting — by default a public instance run by VelaShell, `searxng.easilynet.top`; you can point it at your own instance or clear the address. `web_fetch` downloads the pages the model names directly from your computer. Remove keys, passwords, personal data, and business secrets, and choose providers according to organizational policy. Model responses/arguments may be wrong. Review approvals seriously; start production investigation in read-only/Plan mode.

**Entry point and direction:** Click the Sparkles **AI assistant** button at the top right, or run **AI: Open Assistant** in the Command Palette, to expand the dedicated region at the far right of the main window. It creates no document tab and keeps the current terminal active. The palette also offers a separate chat window, terminal explanation, and collaboration integration. Model/tool configuration is in chat; collaboration integration opens independently.

**Width, pinning, and hiding:** A Sparkles icon appears beside the title. Drag the region's left edge to resize it, down to a minimum of 300px. The host remembers its width and pin preference after a normal exit. It is pinned by default and reserves workspace width. Unpinning makes it an overlay: hover over the AI icon button in the narrow right rail to expand it (the blank rail area does not trigger it), then move out to collapse it after a short delay. Open dropdowns keep it expanded. Collapse temporarily reduces it to the rail; × hides the entire region. Use the AI assistant button or palette command to restore the same panel. Collapsing and hiding preserve the conversation, draft, and running tasks. Narrow windows temporarily use an overlay and return to the pin preference when enlarged. The server selector is aligned to the left and has a limited width and truncates long names so the action buttons remain visible.

- **AI provider:** VelaShell sends messages to a model provider.
- **MCP client tools:** The AI plugin calls external MCP servers you configure.
- **Exposed MCP server:** External Agents call VelaShell's session tools.
- **IM:** Authorized chats use the local VelaShell Agent to operate permitted sessions.

These are separate paths, not one switch. Closing chat does not necessarily disable an enabled collaboration integration.

### 19.2 Add Providers and Models

**Prerequisites:** A working account/API key or supported browser authorization and permission to send the relevant data. Subscription integrations are experimental and may stop working after upstream changes; check service terms. A ChatGPT/Claude/Copilot subscription is not guaranteed to work in this client.

**Steps:**

1. Open AI chat → model settings → add provider. Select a catalog entry; configured OAuth entries open browser authorization, while API-key entries need a key and required base address.
2. Check name, Base URL, default protocol, and multiple-key slots. Test/fetch models. Multiple independent instances of one provider are supported.
3. Add a model under that provider, using its real model ID, not only a display name. Set protocol/address/key overrides, input/output token limits, reasoning, caching, temperature/top-p/stop sequences, and model system prompt as needed.
4. Save/test and send a nonsensitive question with the chosen model. Distinguish authentication, model permission, address/protocol, and quota errors.

**Result and how it works:** Multiple wire protocols and compatible endpoints are supported. Lists come from endpoints and may be supplemented by specification catalogs; catalog examples do not grant account access. Keys/login credentials use encrypted plugin Secrets; ordinary configuration is plugin-private storage. Do not place keys in prompts, screenshots, or public Base URL query parameters.

**Credentials do not follow a new address:** When a provider's Base URL moves to another server (scheme, host, or port changes), saving first opens a confirmation dialog. Confirming removes the previous server's keys (primary and additional slots, and separate keys of models without a Base URL override); a sign-in provider is signed out and must be signed in again for the new server under Manage sign-in, and chats and IM bots using it stop until then. While the address change is unsaved, Test sends no saved key or sign-in. The same holds one level down: when a model's Base URL override sends it to another server and its separate key in the form is unchanged, saving opens the same dialog and confirming removes that model's saved key (a new key entered in the same save is kept without asking); while unsaved, Test does not send the saved key. A model whose Base URL override points to another origin than its provider gets none of the provider's keys; give it its own key ("Use a separate API Key for this model") if that server needs one. Such models used key 1 before this version; the settings page lists them and offers to reuse key 1 in one click (it fills the form; Save applies it). With the Anthropic protocol and no key, only the official `https://api.anthropic.com` uses the machine's `ANTHROPIC_API_KEY` / `ANTHROPIC_AUTH_TOKEN`; other addresses never receive them. After removing key 1 while other keys remain, the list still shows the empty key 1, and the pencil fills it back in.

### 19.3 Chat, File References, and Global Settings

**Starter prompts:** An empty conversation offers four examples: explain terminal output, check disk usage, list listening services, and “Check this server for suspicious programs”, shown with the `virus` icon. Clicking sends the prompt when a model is configured; otherwise it fills the input. Actual checks follow the selected mode, target session, and approval settings.

**Entry point and steps:** Select model, target session, mode, and approval level in chat. Enter sends; Shift+Enter inserts a newline. View/continue/rename/delete history; stop or add input during a run. `#` selects files from the chosen remote session: up to five files, each at most 128 KB, included when sending. Use `#/etc/hosts` or `#"/opt/my app/config"` for spaces. The old `@/path` syntax is no longer parsed as a file reference.

**Composer layout:** Below the multiline editor, the left-side + button adds attachments. Click the adjacent model entry to open the model menu and adjust thinking effort within it. The upward arrow on the right sends the message. The model entry shows the current selection and thinking level; long names truncate in a narrow panel. Thinking changes apply temporarily to this conversation without changing the model's saved configuration; switching models clears the override. During a response, the send action still accepts follow-up input, and a separate action stops the current response. Chat/Plan/Agent selection sits beside the send button inside the input. The model entry, mode selector, and send/stop buttons match the height of the + button. Changing thinking effort does not leave the model entry highlighted. The model menu uses compact rounded rows with a checkmark for the selection, and a single footer row for thinking effort; hover for explanatory hints. The MCP tool configuration wrench sits immediately to the right of the model entry and matches the + button's height. Approval and usage occupy a single status row outside the input.

**Thinking levels:** Choose Default, Off (`off`), Low (`low`), Medium (`medium`), High (`high`), Extra high (`xhigh`), or Maximum (`max`). Default sends no thinking-effort setting and lets the server decide; it does not mean Medium or Off. OpenAI protocols send the selected level as `reasoning_effort` (Chat Completions) or `reasoning.effort` (Responses); supported levels depend on the server and model. For Anthropic, xhigh/max map to thinking budgets of 32768/65536 tokens, constrained by the output limit.

**Composer:** The default input area is 16px taller. Send and Stop share the bottom-right button: click to send while idle, or to stop during a response. Enter still queues additional input during a response.

**Composer entry points:** `@` lists tools only: currently `@imagegen` and `@imageedit`. Filter by localized label or English name, select with ↑↓ and insert with Enter/Tab, then add your request and send. No SSH session is needed. The menu remains available without a default image model; selecting a tool prompts you to choose one in Global settings → Chat image generation before sending an image request. Natural-language generation/editing remains available. `#` lists remote files only. `/` at the beginning opens local commands: `/clear` starts a new conversation without deleting saved history; `/resume` opens the history picker; `/resume <session-id>` restores that conversation, or filters history by title if no ID matches; `/help` lists supported commands. Commands never go to the model. Stop an active response before switching conversations. These names and basic semantics are familiar to Claude Code users, not a claim of full CLI compatibility.

**Image actions:** Three independent 34px circular translucent icon buttons float at the image's bottom-right: `copy` copies the image, `image-down` saves and `fullscreen` opens the full image. There are no visible text labels or shared solid toolbar; tooltips and keyboard focus rings identify each action. Full view sizes the window to the image: if it fits the current screen, 100% means one source pixel per screen pixel, accounting for system scaling. Larger images shrink proportionally to show the whole image initially; small images are not enlarged. The footer shows the actual percentage, with +/−, 100% and Fit window controls. Ctrl+wheel zooms; double-click toggles 100%/fit. Fit mode recalculates when resizing; manually enlarged images can be scrolled. Esc or the window close button exits. Closing chat also closes previews and releases their images. The floating copy action and the preview footer's Copy image button both put the original-resolution image on the system clipboard for direct pasting and sharing without saving first; it remains available after closing the preview. The status bar reports the result. The preview toolbar wraps in narrow windows. Saving keeps the existing system picker and original image bytes.

**Local images and clipboard:** Use the attachment button next to the input, drop local files, or paste a clipboard image in the input (usually Ctrl+V on Windows/Linux, Cmd+V on macOS). Images appear as removable attachment chips and are not sent until you send the message. The existing limits are four attachments per message and 5 MiB each; clipboard images are encoded as PNG and images above 25 million pixels are rejected. With no image, normal editor text paste remains available. Vision input requires support from the selected chat model and provider. PNG/JPEG/WebP input images are stored privately with conversation-scoped image references for later edits. GIF is sent to the chat model as visual input only; it gets no image reference and cannot be edited. To edit a GIF, convert it to PNG first and attach that.

**In-conversation image generation and editing:** Add an image model first (`OpenAI Images` protocol; API base address such as `https://example.invalid/v1`, not the full generation path; the provider's real model ID such as `image-2`). Then select it under Global settings → Image generation and save. Keep your normal chat model selected in the model entry below the input: ask “Draw this using the plan we discussed” and the chat model composes a prompt and calls `generate_image`. The result appears in the same reply and can be saved or reloaded from history. Ask to change a previous image, or paste a reference and request edits, and `edit_image` uses conversation-owned image IDs with `/images/edits` (up to three PNG/JPEG/WebP inputs, at most 20 MiB each; attached inputs still have the 5 MiB attachment limit). The chat model must support tool calling, and vision input still requires model support. Chat exposes only image tools, not server-operation tools; Plan does not generate images; Agent can use image tools. Image-provider billing is separate, and prompts/reference images are sent to that provider. Ask/read-only-auto modes show the target and prompt for approval; bypass skips plugin approval. Failure or denial stops automatic regeneration or image-provider failover for the turn; completed tool-call receipts are reused, with up to four images per turn. A removed default image model causes an explicit error, never a silent provider switch. The older direct text-to-image model selection remains compatible (`gpt-image-*` / `dall-e-*` are recognized under existing OpenAI protocols). Image models are not health-probed to avoid charges. Only directly returned base64 image bytes are accepted, not provider URLs; outputs are capped at 20 MiB each and stored privately. Deleting a history conversation also deletes the image files only it references; files another conversation still references are kept. Clearing history deletes images no conversation references any more, except those still shown in another conversation in the panel. Unreferenced files, such as images left by a cancelled or failed turn, are removed the next time the panel opens once they are at least a day old. Image tools are not exposed through the IM bridge in this change; keep using a text model there.

**Result and how it works:** Models have supplied context and permitted tool results, not inherently accurate live server state. Referencing reads and transmits content, not merely a local path. Writing back requires Agent mode and appropriate approval.

**Global settings:** Configure global prompts, context compression, follow-up questions, SearXNG/native search, private-network access, and failover. Compression can lose detail. Failover needs no switch: without a failover chain, a failed request moves only within the same provider and credential (for example the same model over another protocol). To move automatically to another account of the same catalog entry and model (another instance or a model's own key, such as a work and a personal Copilot; custom endpoints and Azure excluded), turn on "When the list is empty, also switch to another account of the same catalog" in the Failover section (off by default; the whole conversation is then sent to and billed by that account). With a chain, it follows the chain and may reach another provider, affecting confidentiality and billing. A 403 / 429 from one model pauses only that key for that model, so other models on the same key stay usable; only a 401 pauses the whole key. Private-network permission expands tool reach; enable deliberately.

### 19.4 Chat, Plan, Agent, and Approval

| Mode | Use and result | Boundary |
| --- | --- | --- |
| Chat | Explain, discuss, suggest | Natural-language advice is not proof of execution or validation |
| Plan | Gather information through permitted read-only tools and propose steps | No write tools; no opening/closing sessions or writing files |
| Agent | Read sessions/terminals, execute commands, and read/write remote files under tool/approval policy | Writes/commands have real side effects; verify arguments, host, path, and purpose |

**Steps and mechanism:** Approval levels include ask each time, read-only auto-approval, and bypass. Prefer ask or read-only. Review actual commands/content and targets; reject unclear actions. Read-only auto-approval skips confirmation only for `run_command` commands whose shape is known not to change anything: the command name must be on the allowlist, with no redirection, pipes, command separators or command substitution. `sudo` always asks, `env` is not on the list, `curl` / `wget` are accepted only for fetching an http(s) URL to standard output (writing files, uploads, POST and other non-GET requests, and custom headers all ask), and `ip` / `date` / `hostname` / `dmesg` and similar ask when given state-changing arguments. Read-only recognition is an allowlist guessed from the command's shape, not a full audit of server behavior (whether a GET has side effects is up to the service), and external tools' self-declared read-only status is not a sandbox.

**Tool limits:** Built-in reads, listings, and output have limits, for example 256 KB file reads, 200 directory entries, 32 KB command output, and up to 16 parallel hosts. Truncated results do not prove that an entire directory/log was inspected. Uploads and downloads have no fixed size limit; each one must be allowed separately in the host's confirmation dialog, which shows the file size (rounded plus exact bytes; for a download, the remote file's current size). On Linux / macOS the host first snapshots the file on the volume it lives on (an instant, space-free clone where the volume supports copy-on-write); if that volume lacks free space, the upload is refused before anything is sent, saying how much space is needed. After you press Stop or deny anything in the turn, the turn's uploads/downloads still waiting for your confirmation are withdrawn instead of popping up one after another (already approved ones keep going). When the source file is gone, held open exclusively by another program, or the plugin already has too many transfers queued, the host refuses the start before registering anything; AI tells you which of these it was, does not count it as an unknown outcome, and does not immediately retry on its own. Deal with the cause (close the program holding the file, let earlier transfers finish), then ask again. Opening a session accepts only an existing saved-profile ID, not arbitrary model-provided hosts/ports. The host separately confirms and can remember permission; revoke in Plugin Manager. This does not expose profile credentials to the plugin. It can close sessions it opened, not user-opened tabs.

**Revoking transfer permission:** After you revoke permission in Plugin Manager, existing transfer cards hide paths and results. Late progress or completion notifications do not reveal that information again. Revocation does not guarantee rollback of files already committed.

### 19.5 Add External MCP Tool Servers to AI

**Execution warning:** stdio MCP starts local programs; HTTP MCP contacts services. They may access files/network. Verify executable, arguments, working directory, environment, URL, headers, and server trust before using unfamiliar configuration.

**Entry point and steps:** Click the wrench button to the right of the model entry inside the input box, then open AI tool settings → MCP management. Add stdio with executable/arguments/working directory/environment, or HTTP transport with URL/headers. Remote endpoints must use HTTPS; plain HTTP is allowed only for local loopback, not ordinary LAN addresses. Test, refresh the tool list, inspect descriptions, and enable. Disable/delete when no longer needed.

**Result and how it works:** External MCP tools join the loop only in **Agent mode**. Chat has no external MCP tools (configured image tools are separate), and Plan does not attach external MCP tools, avoiding reliance on third-party read-only declarations. **All external MCP tools use local approval policy; even `readOnlyHint=true` does not exempt approval.** Any non-bypass level, including read-only auto-approval, requires an available approval channel and approval; without one, execution is rejected. The hint is advisory metadata affecting behavior such as transient-failure retries, not OS-level isolation. A successful connection proves protocol availability, not safety.

### 19.6 Let External Agents Call VelaShell: Exposed MCP Server

**Token/permission warning:** Other same-user processes can reach loopback, so protect the token; do not post it in issues/chats. Restrict allowed servers before enabling Agent/bypass, especially for production hosts and token-bearing local tools.

**Entry point and steps:** Command Palette → AI collaboration integration → exposed MCP. It is currently enabled by default at `127.0.0.1:8391`, requires a random token, and defaults to read-only Plan mode. Copy the page-generated HTTP configuration to the external client using its current address and Bearer token. Update clients after token regeneration.

**Scope:** Allowed servers default to unrestricted; choose selected-only and check saved profiles to restrict. A restricted empty selection permits no servers, not all servers. Select mode/approval before asking an external Agent to enumerate sessions or read terminals.

**Key mechanism:** This direction has no interactive approval UI. **Ask each time means reject write operations**, not that a dialog awaits your click. Chat mode exposes no tools at all (an external agent can connect but cannot call anything); Plan never registers write tools; Agent still follows approval policy. Read-only permits only recognized allowed reads. Side effects such as `open_session` require conditions such as **Agent + bypass**; the separate host plugin-session confirmation still applies. Connectivity is not write authorization.

**Result and caution:** External Agents can call permitted session, terminal, command, and file tools. Listening is restricted to `127.0.0.1`, with no public-interface option. Disabling stops this entry; quitting VelaShell takes all collaboration integrations offline.

### 19.7 Use the Agent from IM

**Group-chat warning:** Authorizing a group can let members within the permitted-user scope issue instructions. Configure questioners and approvers separately where appropriate. Messages, output, and approvals traverse the platform. Default Plan is read-only; do not switch to writes merely to test connectivity.

**Entry point and prerequisites:** Command Palette → AI collaboration integration. Enable the IM master switch (off by default), and prepare model access and channel credentials.

| Channel | Required information | Network/platform prerequisites |
| --- | --- | --- |
| Feishu/Lark | Domestic/international, App ID, App Secret | Outbound persistent connection; no public callback. Configure long-connection events, subscribe to `im.message.receive_v1`, and publish the app version |
| DingTalk | Client ID (AppKey), Client Secret | Stream connection; no public inbound endpoint |
| Telegram | Bot Token | Long polling; no public inbound endpoint; after testing, the page QR can help add the bot to a group |
| WeCom | CorpID, AgentID, Secret, callback Token, EncodingAESKey, local callback port/path | Public HTTPS callback required by platform; local listener is loopback-only, so use a trusted reverse proxy/tunnel, not an exposed management port |

**Steps:**

1. Add a named channel, credentials, and enable it; test. Testing validates integration, not authorization for every chat.
2. Add the bot to the intended group through platform controls. Feishu uses group settings, not a mini-app link. Do not run the same Feishu credentials on two VelaShell instances: platform cluster delivery can randomly route messages between them.
3. Configure permitted users/approvers, default target/mode, and save. Generate a personal/group pairing code and send `/pair <code>` in the target chat, or allow an entry in the pending-chat list. Codes are six digits, single-use, expire in ten minutes, and become invalid after five wrong attempts.
4. **An empty chat allowlist permits nobody; direct messages also need authorization. A blank "allowed users" list lets anyone in an allowed chat give commands, and blank approvers fall back to the allowed users.** Mention the bot in groups; speak directly in private chats. Use `/sessions` for online targets and `/use demo@host:22` to bind.
5. Commands include `/help`, `/status`, `/new`, `/stop`, and `/mode chat|plan|agent`. By default `/mode` can only downgrade; upgrades need explicit settings permission.
6. Authorized approvers reply `y` to allow, `n` to deny, or `a` to allow persistently for this conversation. Timeout denies. Keep VelaShell running; use the tray instead of quitting for background access.

**Result and how it works:** Authorized chats operate permitted sessions through the local Agent; bindings use user@host:port. The Agent can enumerate saved profiles and request an offline target when allowed. Opening is a write operation with chat approval and host confirmation gates, not plugin-supplied passwords. Plan only reads; pairing authorizes a chat without raising mode/approval privileges. The page's Model setting can pin the bridge to one model (by default it follows the chat panel). If the pinned model is deleted, the list shows "Removed model" with an explanation; the bot answers each message by asking for a new pick and never switches to the chat panel's model or any other, and saving keeps the pin until you choose another model here.

**Limitations:** The bridge handles text, not images/files/rich text. Feishu card approvals are not connected; use textual responses. WeCom's public callback requirement does not justify exposing local management services. Quitting makes the bot offline; this is not a hosted always-on server.

<a id="desktop"></a>
## 20. Message Center, Desktop Integration, and External Launch

### 20.1 Message Center and Feeds

**Entry point and steps:** The sidebar bell opens Message Center. Filter unread, mark all read, or clear; its draggable popup position is remembered, and at most 200 messages are retained. Check links before opening; only HTTPS links are allowed. General settings control update messages, feed, interval, and announcements.

**Result and how it works:** Updates, news, previous crashes, external-edit drafts left by the previous run (changes not uploaded; see 9.4), settings pulled by cloud sync with Webhook alerts on but no Webhook URL on this device (see 17.2), the state of the local encryption key (still in the data root, the system keychain could not be opened this time, a previous key was not found, the key file was unusable and replaced; see 16.4 and 17.4), and recording/security problems appear here. A draft message is withdrawn on the next start after its draft has been deleted. This is not remote mail or server logging. The default official feed is requested at startup and periodically; clearing its URL stops that source. Platform/language/version filtering occurs locally. Clearing notifications does not delete server data or resolve the underlying security incident.

### 20.2 Tray, Autostart, and Single Instance

**Entry point and steps:** General settings control minimize-to-tray on close and Windows autostart. Know whether closing hides or quits. Explicitly quit to stop tunnels/IM/MCP. A second launch brings the existing instance forward; external requests are forwarded to it.

**Result and how it works:** Single-instance scope prevents duplicate windows against the same data root. Current-user communication forwards requests. Autostart registers for the current Windows user without admin rights; the same toggle does not imply a Linux/macOS implementation.

### 20.3 Xshell-Compatible Login from Bastion/SSO Portals

**Command-line disclosure warning:** Passwords in URLs or `-pw` can be visible to same-user processes and EDR/Sysmon process logs. Prefer short-lived one-time credentials from a trusted portal, not long-lived passwords. Not persisting temporary credentials does not remove their command-line exposure.

**Entry point and prerequisites:** Settings → Security Audit → external login (Xshell compatibility). Accepting requests and confirmation before connect default on; `ssh://`/`sftp://` association defaults off. Portals can point their terminal executable to VelaShell. Register protocols only when browser-link launch is needed.

**Supported forms:**

```text
VelaShell.exe -url ssh://demo@192.0.2.10:2222
VelaShell.exe -newtab ssh://demo@192.0.2.10:2222
VelaShell.exe ssh://demo@192.0.2.10:2222
VelaShell.exe -f "C:\Temp\session.xsh"
```

Override username/port/password/private key with `-l/-user`, `-p/-port`, `-pw/-password`, and `-i/-identity`; PuTTY's `-cert` names the OpenSSH certificate paired with the private key (certificate authentication; without `-i`, the key is found by the `<key>-cert.pub` naming convention); secrets are deliberately absent above. `-newtab` identifies a target only with a scheme; a scheme-less value is a tab name and becomes the tab title (shown on its own row in the confirmation dialog; the status bar still names the real target). Candidates are tried as `-url`, bare URL, `-newtab`, then session file. ssh/sftp/ftp/ftps are supported; telnet/rlogin report unsupported. **Post-connect commands/scripts via `-e/-s` are not implemented.** `.xsh` reads recognized target/private-key fields but does not decrypt Xshell passwords.

**Steps and result:** Check target, source, and credential presence in the portal-triggered confirmation before connecting. Fingerprint/authentication still apply. Without supplied credentials, an existing matching profile may be reused (its last-connected time is updated as usual). Otherwise (one-time credentials supplied, or no profile to reuse) the temporary profile lives only for this run: the tab opens and reconnects as usual, but it is not added to the saved tree, not synced, and not restored at the next start. Recent history still records SSH/SFTP targets (without passwords); reopening one from there asks for credentials again. When such a temporary connection asks for credentials, the login dialog offers **Save as a connection** instead of **Remember password** (which could not be stored): tick it to save a regular connection (ungrouped in the Explorer), with the password remembered as long as **Remember passwords** is on in General settings, and from then on it syncs and is restored like any connection you created; leave it unticked and what you enter is used for this sign-in only. Choose not-to-ask-again only for genuinely trusted targets; clear external-target trust in Settings when needed.

**Protocol boundary:** Windows uses current-user associations; Linux uses desktop/xdg-mime integration. A runtime macOS toggle cannot change bundle declarations. External-target approval and SSH fingerprint trust are separate records; clearing one does not clear the other. Keep confirmation rather than letting arbitrary webpages silently select targets.

<a id="troubleshooting"></a>
## 21. Common Tasks and Troubleshooting

### 21.1 Task-Oriented Recipes

| Goal | Suggested sequence | Completion check |
| --- | --- | --- |
| First server login | Create SSH → verify fingerprint → authenticate → `hostname`/`pwd` | Correct identity, not just prompt text |
| Internal host through bastion | Verify saved jump host → select it for target → verify each hop | Final authentication works; not accidentally direct |
| Local tool to remote database | Local tunnel on loopback → tool uses local port | Not public; destination reachable from server side |
| Edit remote configuration | Back up → open file → save → inspect upload | Remote file updated; decide separately whether to reload service |
| Deploy a local directory | Local→remote sync → compare → review → run | Recheck has no unintended differences; no deletion on first test |
| Move files between hosts | Select two profiles → dual remote → check sides → copy | Destination content/size correct; local link stable |
| Same read-only check on several hosts | Split → same input channel → verify shells → type → leave | No production writes/password broadcast |
| Record a reproduction | Enable before new session → reproduce → play → inspect masking → export | Recording did not stop on storage failure; no unwanted secrets |
| Migrate login capability | Encrypted export/Gist E2E → separate key backup → import/check | Proxy password, paths, Agent, and local items restored |
| AI read-only investigation | Nonsensitive target → Plan → restrict scope → inspect results | No write grants; truncated results not treated as complete |

### 21.2 Symptom → Cause → Action

| Symptom | Common cause | Recommended action |
| --- | --- | --- |
| Cannot create/connect profile | Invalid host, port, or authentication material | Read field validation; bracket IPv6 with ports; match certificate and key |
| DNS failure/TCP timeout | Name, VPN, proxy, port, firewall | Run four-stage diagnostics; check first-hop networking before changing passwords |
| Authentication failure | Wrong identity/material, unauthorized key, MFA, permissions | Verify method/system user and challenge; do not assume password fallback |
| Automatic reconnect stops | Attempts exhausted, intentional exit, authentication/trust intervention | Reconnect manually and investigate; independently verify fingerprints |
| Fingerprint changed | Reinstall/rotation, impostor, wrong route | Stop and verify old/new keys and change with administrator; do not simply delete trust |
| No local-terminal entry | Non-Windows or missing shell | Windows detects installed shells; Linux/macOS lack this entry |
| Shortcut missing in nano/bash | Window shortcut intercepts it | Remap/unbind in Settings; Ctrl+K is not the default palette shortcut |
| Ctrl+C does not interrupt | Selection-copy exception and active selection | Clear selection; check input settings |
| Garbled/misaligned output | Encoding mismatch, missing glyphs, proportional font | Check decoding and monospaced font separately |
| No command blocks/prompt jumps | No OSC 133 from shell | Install appropriate integration; inspect prompt-tool overrides |
| No shared-password list | Wrong prompt/state, history/alternate screen, no suitable credential | Focus and scroll down, inspect credential type; use manual action only for trusted expected prompts |
| SFTP works but monitoring/hashes fail | File-only account, prohibited exec, missing tools | Keep security restrictions; use file operations/comparison fallback |
| FTP login works, transfer fails | Data channel/passive mode/firewall | Check data ports and server requirements; do not remove TLS to hide the issue |
| Double-click opens differently | Remote-editor setting | Use explicit context action or change File Transfer settings |
| Saved edit absent remotely | Temporary-only save, auto-upload off, disconnection/permission/size | Inspect transfer/original path; preserve temporary copy before recovery |
| Sync always finds differences | FTP timezone/precision/MFMT or content conflict | Read warnings and choose suitable criteria; do not repeatedly mirror blindly |
| Sync requires comparison again | Options invalidated preview | Recompare; prevents applying new rules to an old preview |
| Tunnel port conflict | Local listener, an existing local socket file, or remote policy/occupation | Choose a free loopback port locally and delete a leftover socket file once you are sure it is unused; investigate server for `-R` |
| X11 no window/stuck | Forwarding denied, local server off, graphics incompatibility | Verify both configurations, reconnect, consult X11 references; do not first expose TCP/disable access control |
| Heavy intermediate-hop loss | ICMP rate limiting/nonresponse | Inspect later hops, destination, and application metrics |
| Recording tail missing | Not enabled for new session, storage/queue failure, retention | Check Message Center/storage; saved portion may remain; absence is not proof an event never occurred |
| Settings export lacks profiles/proxy password | Intentional scope/redaction: no profiles, never the proxy password or Webhook URL, Security Audit and Network Proxy unchecked by default | Use connection JSON or Gist; check those two categories when exporting/importing if needed; re-enter the proxy password and Webhook URL |
| Copied database cannot decrypt | Windows DPAPI user/system changed; or on macOS/Linux the key is not in the system keychain (data root copied from another computer/account, entry deleted), or the keychain could not be opened this time (keyring locked, no desktop session or session bus) | Check the system message in the message center first. When connecting, the login dialog opens straight away and says why (a password it cannot decrypt is no longer tried for signing in first): if the keychain was only unavailable, unlock it or sign in with a desktop session and restart to read the passwords as usual, with no need to re-enter them (on Linux, cancelling the keyring unlock prompt may also show up as the key not being found; unlock and restart first there too); if the key is gone, enter the password again and remember it (for a shared credential, tick **Also update the shared credential**). Connections that never prompt (tunnels, Test Connection, the health check) report that the saved password cannot be decrypted. Export/E2E-sync while old environment can decrypt; generating a new key will not recover old secrets |
| Gist sync succeeds, private key unusable | Only path synced | Securely restore actual key/certificate, fix path/permissions |
| Optional protocol missing | Plugin absent/disabled/incompatible | Inspect Plugin Manager; install trusted compatible package |
| AI 401/model not found | Authentication/quota/model ID/protocol | Test provider/model separately from IM errors |
| MCP connects but rejects writes | Plan or ask/read-only policy | Expected gate; verify need instead of blindly bypassing; session opening has extra conditions |
| IM silent | Master switch off, bot absent, chat unauthorized | Test then pair; direct chats need authorization; empty list denies |
| Feishu tests but no events | Long connection/subscription/version not configured | Configure/publish; avoid competing instances with same credentials |
| Bot remains online after closing window | Minimize-to-tray on close | Explicitly quit or disable the collaboration switch |

**Still unresolved:** Record version, OS, package, protocol, failure stage, and nonsensitive reproduction. Export a local diagnostic package from About, inspect it, then send it to maintainers. Do not provide private keys, `secret.key`, PAT/API keys, MCP tokens, or secret-bearing connection exports.

<a id="shortcuts"></a>
## 22. Keyboard Shortcuts and Mouse Gestures

These are factory defaults; customized bindings in Settings take precedence. Global defaults use Ctrl on all three platforms. macOS additionally has fixed `Cmd+C/V/T/W/,` aliases. Arrow-key alternatives mean one direction at a time. Observe focus/state restrictions.

### 22.1 Global, Tabs, and Workspace

| Action | Default | Condition/notes |
| --- | --- | --- |
| New SSH connection / new tab | Ctrl+N / Ctrl+T | New tab opens new-connection flow |
| Clone current session | Ctrl+Shift+N | Cloneable session |
| Settings / Command Palette | Ctrl+, / Ctrl+P | Ctrl+K is not the palette binding |
| Close current / all tabs | Ctrl+W / Ctrl+Shift+W | Confirmations/pinning apply |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab | Current tab group |
| Tab 1–8 / last tab | Ctrl+Alt+1…8 / Ctrl+Alt+9 | Current strip, not global split numbering |
| Horizontal / vertical split | Ctrl+Shift+D / Ctrl+Shift+S | Docking workspace |
| Maximize / restore pane | Ctrl+Shift+X | Split layout |
| Focus adjacent pane | Alt+←/→/↑/↓ | Split only; otherwise sent remotely; with focus in a file pane, ←/→ go back/forward there |
| Toggle Explorer | Ctrl+B | Window action |
| File pane / tunnels | Ctrl+Shift+F / Ctrl+Shift+T | File-capable session for file pane |
| Line-number/time gutter | Ctrl+Shift+L | Terminal display |

### 22.2 Terminal, Search, Suggestions, and Password List

| Action | Keys | Condition/notes |
| --- | --- | --- |
| Copy / paste | Ctrl+Shift+C / Ctrl+Shift+V | Fixed; multiline paste can execute |
| Classic paste | Shift+Insert | Verify content too |
| Interrupt ^C | Ctrl+C | Copies instead with selection-copy option and selection |
| Search | Ctrl+F | Terminal-tab focus |
| Next / previous / close search | Enter / Shift+Enter / Esc | Search open |
| Scrollback pages | PageUp / PageDown | Primary screen with history |
| Scrollback including alternate screen | Shift+PageUp / Shift+PageDown | Not remote application page navigation |
| Previous / next prompt | Ctrl+Shift+↑ / Ctrl+Shift+↓ | OSC 133 integration |
| Delete previous word | Ctrl+Backspace | Terminal input |
| Line start / end | Shift+Home / Shift+End | Terminal input |
| Reconnect | Enter / Ctrl+R | Disconnected only |
| Close disconnected tab | Esc | Disconnected only |
| Clear terminal | Ctrl+Shift+K | Not shared-password filling |
| Zoom in / out / reset | Ctrl+= / Ctrl+- / Ctrl+0 | Ctrl+- occupies ^_; Ctrl+Shift+- sends that control character |
| Open suggestions | Alt+Enter | Command suggestions |
| Select / adopt suggestion | ↑↓ / Enter | Popup open |
| Dismiss suggestions | Esc, Ctrl+C, click terminal | Last two also continue normal input/click |
| Native shell completion | Tab | Not suggestion confirmation |
| Accept ghost text | Right / End | Available suggestion |
| Select shared password | ↑↓ | Password list open |
| Fill / close password list | Enter / Esc | Must select first; submits with an automatic Enter; with no selection Enter goes remotely |

### 22.3 Mouse, Files, Processes, and Dialogs

| Action | Gesture/keys | Condition/notes |
| --- | --- | --- |
| Open link | Ctrl+click | Verify destination |
| Select / add word | Double-click / Ctrl+Shift+double-click | Ordinary word selection must be enabled |
| Extend / rectangular selection | Shift+click / Alt+drag | Terminal |
| Add selection / rectangle | Ctrl+Shift+drag / Ctrl+Shift+Alt+drag | Discontiguous selection |
| Bypass mouse reporting | Shift+drag | Remote full-screen mouse reporting |
| Right-click paste | Right-click | Configurable |
| Font size / fast scroll | Ctrl+wheel / Alt+wheel | Fast scroll is fivefold |
| Close tab / scroll strip | Middle-click tab / wheel over strip | Pinned tabs resist middle-click |
| Equalize panes | Double-click divider | Split layout |
| Gutter menu / folding | Right-/left-click terminal gutter | Not Explorer sidebar; right-clicking a command block also offers copy/rerun this command |
| Select command output | Click command marker | OSC 133 |
| Tree selection / range | Ctrl+click / Shift+click | Explorer |
| Move group / reorder | Drag connection to group / drag group row | Changes saved organization |
| Palette select / execute / close | ↑↓ / Enter / Esc | Palette focus |
| Edit / navigate / cancel path | Ctrl+L / Enter / Esc | File browser |
| Open file / enter directory | Double-click / Enter | Selection/open behavior applies |
| Find by name | Type name prefix | File list |
| Refresh / rename / delete in a file pane | F5 / F2 / Delete | Local and remote panes; F2 and Delete need list focus, deletion asks first |
| Clear and close filter / back to list | Esc / Enter | Filter box focused |
| Back / forward in a file pane | Alt+← / Alt+→ | Focus in that pane (not a text box); wins over pane focus when split |
| Save / close remote editor | Ctrl+S / Esc | Built-in editor |
| Refresh / terminate / close processes | F5 / Delete / Esc | Delete requires list focus and confirmation |
| Cancel / default dialog action | Esc / Enter | Default can execute an action |
| Maximize / restore window | Double-click title bar | Window |
| Cancel docking drag | Esc | Drag active |
| Paste in password field | Ctrl+V | Copy/cut disabled; not proof the computer is secure |

### 22.4 AI Panel

| Action | Keys | Condition |
| --- | --- | --- |
| Send / newline | Enter / Shift+Enter | Input |
| Previous / next input | ↑ / ↓ | Cursor at first/last line |
| Select file candidate | ↑↓ | @ popup |
| Insert file reference | Enter / Tab | Candidate popup |
| Close file popup | Esc | Popup open |
| Delete whole reference | Backspace | At reference |
| Submit / cancel rename | Enter / Esc | Renaming |
| Close secondary AI window | Esc | Applicable secondary window |

<a id="limits"></a>
## 23. Explicit Limitations and Reference Index

### 23.1 Plans and Library Capabilities Are Not Interface Features

This manual intentionally does not promise:

- Local shells across all platforms: only Windows ConPTY is currently integrated; Linux/macOS lack local-terminal actions.
- Arbitrary floating docking windows; layout persistence beyond layout templates and **Restore split layout** (without a template or that option, splits and pinned tabs last for this run only; the layout saved at exit does not sync).
- Master-password protection of the whole database; encrypted application-settings export; profiles embedded in that export; proxy passwords or alert Webhook URLs in it. Current settings export is unencrypted JSON chosen by category, and those two secrets are never written.
- Linux in-app update installation or selectable update channels described by older material. Background automatic download exists only on writable Windows/macOS installs; Linux installs manually; Store is Store-managed.
- Per-profile proxies; dual-remote synchronization/keep-up-to-date; automatic remote→local watching; resuming transfers automatically at startup (the persisted transfer queue is only restored as "paused" and connects when you click "Resume").
- Sixel/Kitty terminal images or ZMODEM. X11 graphical applications are not terminal image protocols.
- Redis/S3/Telnet/serial/Docker without plugins. S3 is not POSIX storage and does not supply SSH chmod/upload-resume semantics.
- Automatic quick-command execution, or a command-block rerun that goes back to the original directory or host. Completion is not a safety review.
- Output rules as expect scripts: auto-answer only answers the line the cursor is waiting on, and a rule sends one piece of fixed text; there are no multi-step dialogues, variables, or passwords. Recording and log masking cannot take your own rules from this table yet.
- Remote mtr/TCP SYN tracing, trace text/JSON export, or a fifth diagnostics stage. Current tracing uses local ICMP.
- Remote forwards mixing a TCP end with a Unix-socket end, and SSH library capabilities the host has not wired up, such as the TLS dialer and the packet tap. Library support does not imply panel configuration.
- An OS-level plugin sandbox, configuration-change auditing that records the values (it records only which part changed), infallible masking, or secret-Gist links as encryption.
- Interactive write approvals for the exposed MCP endpoint: ask means reject there. An empty IM allowlist does not allow everyone; pairing grants chat access, not elevated mode.

These boundaries prevent mistakes. Editing files or skipping validation does not turn a missing feature into supported behavior. Distinguish no interface, unsupported protocol/server, insufficient permissions, and planned-only work.

### 23.2 Official References

This manual uses current source and existing references below. Design/audit documents may retain older conclusions; current runtime integration and interface take precedence when they conflict.

- [VelaShell documentation repository](https://github.com/VelaShellLabs/velashell-docs)
- [Host documentation directory](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host)
- [Shortcut reference](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/快捷键参考.md)
- Local documentation repository `../velashell-docs/zh/host/`: `交互与界面规格.md` (interaction/interface specification), `会话导入.md` (session import), `SFTP双栏与WinSCP差距分析.md` (dual-pane comparison), `隧道功能规划.md` (tunnel design), `Xshell兼容登录.md` (compatible login), and `消息中心与资讯源.md` (Message Center/feeds).
- `../velashell-docs/zh/plugins/协作接入.md`: IM/MCP direction, allowlists, approvals, and saved-profile sessions.
- `../velashell-docs/zh/xserver/troubleshooting.md`: X11/graphics compatibility and troubleshooting.
- This repository's `PRIVACY.md`: storage, automatic/user-triggered networking, and diagnostic exports; `SECURITY.md`: security-reporting procedure.

Chinese filenames above identify the actual referenced documents; they are intentionally not translated into nonexistent paths. The documentation repository also maintains an `en/` tree.

### 23.3 Source-of-Truth Index

This index is for maintenance; end users need not read source code.

| Manual area | Main current evidence |
| --- | --- |
| Main window, profiles, import/export | `src/VelaShell/Views/SidebarView.axaml`, `SessionTreeView.axaml`, `ConnectionProfileView.axaml`; `ViewModels/ConnectionProfileViewModel.cs`, `SessionBatchEditViewModel.cs`, `SessionExportViewModel.cs`, `SessionFileImportViewModel.cs` |
| Authentication, shared passwords, jumps, reconnect | `Services/InteractivePromptDetector.cs`, `SharedPasswordFillRequest.cs`, `ReconnectPolicy.cs`; `ViewModels/HostKeyPromptViewModel.cs`, `SharedCredentialsViewModel.cs`; `Views/TerminalTabView.axaml.cs` |
| Terminal, docking, suggestions, synchronized input | `ViewModels/ShortcutCatalog.cs`, `Services/ShortcutKeymap.cs`, `KeyboardShortcutService.cs`, `CommandSuggestionProvider.cs`, `SyncInputCoordinator.cs`, `LocalShellCatalog.cs`; `src/VelaShell.Terminal/`; `src/VelaShell/Docking/` |
| Files, editing, synchronization | `ViewModels/SftpDocumentViewModel.cs`, `DualSftpDocumentViewModel.cs`, `FileBrowserViewModel.cs`, `DirectorySyncViewModel.cs`, `FileTransferViewModel.cs`; `Services/RemoteEditSessionManager.cs` |
| Tunnels, X11, operations | `ViewModels/TunnelPanelViewModel.cs`, `XServerToggleViewModel.cs`, `ProcessManagerViewModel.cs`, `ResourceMonitorWindowViewModel.cs`, `TraceRouteViewModel.cs`, `ConnectionDiagnosticsViewModel.cs`; `src/VelaShell.Infrastructure/Diagnostics/PingTraceRouteService.cs` |
| Logs, recordings, data protection | `Services/SessionLogService.cs`, `SessionOutputMasker.cs`; `SessionRecorder.cs`, `RecordingPlayerViewModel.cs`; `src/VelaShell.Infrastructure/Persistence/VelaShellStoragePaths.cs`, `AesSecretProtector.cs`, `MasterKeyResolver.cs`, `MasterKeyStores.cs` |
| Settings, sync, plugins, launch, updates | `Views/Settings/*.axaml`, `ViewModels/SettingsViewModel.cs`, `PluginManagerViewModel.cs`; `Services/GistSyncService.cs`, `UpdateService.cs`, `ExternalLaunchCoordinator.cs`, `PluginPermissionGate.cs`; `src/VelaShell.Infrastructure/Startup/` |
| AI/MCP/IM | `plugins/VelaShell.Plugin.Ai/AiPlugin.cs`, its `Ui/`, Agent tools, MCP client/server, Bridge settings/implementations |
| Optional protocols/Docker | Current plugin source/manifests and checked user documentation in the adjacent `velashell-plugins` repository; older README contract/version claims are not evidence of host behavior |

**Maintenance rule:** When a release changes entry points, defaults, or security boundaries, review text, anchors, and shortcuts together. A written plan or lower-level capability is not a substitute for an operation users can perform.
