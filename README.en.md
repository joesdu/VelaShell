<div align="center">

# VelaShell

**A modern, cross-platform SSH terminal client for sysadmins and developers**

A terminal as your sail, riding the signal winds to remote hosts.

[![Release](https://img.shields.io/github/v/release/joesdu/VelaShell?label=release&color=19a88b)](https://github.com/joesdu/VelaShell/releases)
[![CI](https://github.com/joesdu/VelaShell/actions/workflows/ci.yml/badge.svg)](https://github.com/joesdu/VelaShell/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-AGPL--3.0%20%7C%20Commercial-blue)](#license)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-64748b)](#download)

[![.NET](https://img.shields.io/badge/.NET-11.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.1-8B44AC)](https://avaloniaui.net/)

[简体中文](README.md) · **English**

**[Download VelaShell](https://github.com/joesdu/VelaShell/releases)** · **[User manual](manual.en.md)** · **[Plugin marketplace](https://market.easilynet.top)** · **[Developer docs](https://github.com/VelaShell/velashell-docs)**

<img src="mascot/chibi.png" alt="VelaShell mascot trio: holding a cat, using a laptop and waving" width="640">

**Keyboard-first · Productive workspaces · Cross-platform desktop experience**

</div>

---

VelaShell brings **SSH terminals, file transfers, tunnels, remote graphical apps and operations tools** into one desktop workspace.
Connect to a single server, manage several hosts in split panes, or add AI, RDP remote desktop, Redis, MongoDB, S3 and Docker through plugins, all from the same window.

Built with **.NET 11 + Avalonia** for Windows, Linux and macOS, each release includes its runtime: **no .NET installation required**.
The official app is **free forever** for individuals and companies. See [licensing](#license) for derivative development and distribution.

<div align="center">

[Get started](#download) · [Features](#features) · [AI assistant](#ai) · [Plugins](#plugins) · [Data & privacy](#data) · [Development](#development) · [Docs](#docs) · [Contributing](#contributing) · [Sponsor](#sponsor)

</div>

<a id="download"></a>

## 🚀 Download & get started

Choose a package for your operating system and CPU architecture from **[GitHub Releases](https://github.com/joesdu/VelaShell/releases)**.

| Platform | Architecture | Packages |
| --- | --- | --- |
| **Windows 10 / 11** | x64 · arm64 | Portable `.zip` · Microsoft Store MSIX |
| **Linux** | x64 · arm64 | Portable `.tar.gz` · `.AppImage` · `.deb` · `.rpm` |
| **macOS** | x64 · arm64 | `.tar.gz` · `.dmg` (unsigned, not notarised) |

### Make your first SSH connection

1. Extract the portable archive or install the appropriate package, then launch VelaShell.
2. Click **“+”** at the top of the resource explorer, choose **SSH**, and enter the host, port, username and authentication details.
3. Click **Connect**, verify the server fingerprint through a trusted channel, and confirm to open the remote terminal.

Use the **`Ctrl+P` command palette** to find features. Moving from WinSCP, Xshell or `~/.ssh/config`? [Import your existing connections](manual.en.md#connections).
See [installation and first connection](manual.en.md#start) for the full walkthrough.

> [!NOTE]
> **Updates depend on the package type.** Writable portable installations on Windows / macOS support in-app updates; Linux currently requires manual download and installation.
> Microsoft Store builds update through the Store and keep their data separate from portable builds. See the [user manual](manual.en.md#start) for details.

<a id="features"></a>

## ✨ Core features

| Area | What you can do |
| --- | --- |
| **Terminal** | VT engine, true colour and CJK double-width text; linear, block and disjoint selections; hyperlinks, command blocks, output folding and session recording |
| **Connections & auth** | SSH, SFTP, FTP / FTPS; passwords, private keys, SSH certificates, agents and two-step verification; shared credentials, jump hosts, proxies and automatic reconnection |
| **Files** | Dual-pane browsing, drag-and-drop, resumable transfers and queues; directory comparison and sync; built-in editing or upload-on-save from an external editor |
| **Tunnels** | Local `-L`, remote `-R` and dynamic SOCKS5 `-D`; traffic and connection counters, port-conflict checks and recovery after disconnects |
| **Remote graphical apps** | X11 forwarding and a built-in X server; separate native windows for X top-level windows, with two-way clipboard, multiple monitors and DPI support |
| **Operations** | CPU, memory, disk and network monitoring; process management, traceroute with geolocation, connection diagnostics and recording playback |
| **Multi-session workspace** | VelaDock drag-to-split layout, groups and pinning, bulk management, synchronised input, command snippets, completion and customisable shortcuts |
| **Appearance & languages** | 12 UI themes, 16 terminal palettes and bundled Cascadia Mono; 简体中文, English, 繁體中文, 日本語 and 한국어 |
| **Desktop integration** | System tray, launch at login, single instance, URL protocols and Xshell-compatible launch; Windows local terminals through ConPTY |

<details>
<summary><b>More features: terminal, connections and workspace</b></summary>

### Terminal experience

- **VT compatibility**: DEC ANSI / VT / Xterm state machine with ten profiles (`vt52` → `xterm-256color`), 256 colours, true colour, line drawing, primary / alternate screens and mouse protocols.
- **Precise selection**: linear and block modes, `Shift+click` to extend, and `Ctrl+Shift+drag` for disjoint selections; CJK IME pre-edit support.
- **Readable output**: line-number and timestamp gutters, OSC 8 hyperlinks (`Ctrl+click`), and OSC 133 command blocks with failure markers, prompt navigation, output selection and folding.
- **Recording and replay**: session logs, recording and playback, with asciicast v2 export.

### Connections and authentication

- **Complex networks**: ProxyJump with up to 5 hops and cycle detection; HTTP / SOCKS5 / system proxies; keepalive, anti-idle and automatic reconnection after sleep or network recovery.
- **Authentication choices**: passwords, private keys, SSH certificates, SSH agents and keyboard-interactive two-step verification, including private key + one-time code.
- **Key management**: generate Ed25519 / ECDSA / RSA 4096 keys; optionally add them to the agent after login; restrict forwarded keys and request confirmation before signing.
- **Connection settings**: shared credentials, post-login commands, per-direction terminal compression and OpenSSH-style algorithm configuration; trust-on-first-use (TOFU) host fingerprints and change confirmation.

### Files and workspace

- **Transfers and sync**: SFTP and FTP / FTPS share the dual-pane browser and transfer queue; symbolic links, SHA-256-first directory comparison and remote synchronisation.
- **Bulk management**: JSON connection import / export with optional encrypted passwords, CSV editing, and imports from WinSCP / Xshell / OpenSSH configuration.
- **Personalisation**: linked UI and terminal palettes, with 7 dark and 5 light UI themes; the interface follows the system language and falls back to English.

See the [user manual](manual.en.md) for instructions and requirements.

</details>

### Built-in X server

**[VelaShell.XServer](src/VelaShell.XServer/)** ships with the app and starts from the title bar, with no separate X server installation required.
Its rootless mode maps remote X top-level windows to native desktop windows, with two-way clipboard, system keyboard layouts and GLX 1.4.
SSH X11 channels connect to the server in-process. On Windows, you can also select an installed VcXsrv.

[Use X11 forwarding](manual.en.md#x11) · [X Server troubleshooting](https://github.com/VelaShell/velashell-docs/blob/main/en/xserver/troubleshooting.md)

<a id="ai"></a>

## 🤖 AI assistant

The first-party **AI assistant plugin ships with the app**, bringing streaming chat and Agent mode into your workspace.

- **Multiple providers**: OpenAI Responses, Chat Completions-compatible APIs and Anthropic Messages, covering services such as OpenAI, Anthropic, Grok, Gemini, DeepSeek, Kimi and GLM.
- **Custom providers**: enter an OpenAI- or Anthropic-compatible endpoint and an API key. No provider account sign-in is needed in VelaShell. Model lists come from the endpoint, with context limits and pricing supplemented by models.dev.
- **Work with your sessions**: the agent can use session, terminal, remote-command and file tools, plus custom MCP servers, web search and fetching. Dangerous operations require individual approval.
- **Continuous collaboration**: interject during a run, reference remote files from the selected session with `@`, and browse, resume or manage past conversations.
- **Team chat**: connect Feishu, DingTalk, Telegram or WeCom, with pairing codes to authorise chats and connectivity checks.

<details>
<summary><b>Why API keys only? What if my provider requires sign-in?</b></summary>

VelaShell no longer handles provider accounts or subscription credentials. This reduces account-security and suspension risks and keeps setup and maintenance simpler. If you need sign-in, use [Magpie](https://usemagpie.ai) or [Astra](https://astra-gate.si) and configure its compatible endpoint as a custom provider. Alternatively, sign in with an official agent tool and connect it to VelaShell over MCP.

The plugin also offers a **Streamable HTTP MCP server** so external agents can use VelaShell's session capabilities.
It binds only to `127.0.0.1` and requires a token on every request. This path has no approval UI, so “ask” mode refuses write operations.

See the [AI manual](manual.en.md#ai) and [privacy notice](PRIVACY.md) for setup, data boundaries and permissions.

</details>

<a id="plugins"></a>

## 🧩 Plugin ecosystem

Install `.vpx` packages from the **[plugin marketplace](https://market.easilynet.top)** to add connection types and workbenches.
Apart from the AI assistant, the first-party plugins below are **not preinstalled**. Install them in one click from **Plugin manager → Plugin store**,
or download signed `.vpx` packages from [velashell-plugins Releases](https://github.com/VelaShell/velashell-plugins/releases) and install them manually.
First-party plugins are released together under one version number and use the same SDK and permission model as third-party plugins.

| Plugin | Entry point | What you can do |
| --- | --- | --- |
| **RDP remote desktop** | New connection → RDP | Connect to Windows remote desktops. Resolution and DPI follow the tab automatically, with the GFX / H.264 graphics pipeline, two-way text / image / file clipboard, folder / printer / smart-card redirection, remote audio, RD Gateway, RemoteApp and automatic reconnection; connect through an SSH jump host if needed |
| **MongoDB workbench** | Command palette (`Ctrl+P`) → search “MongoDB” | Object tree with grid / tree / JSON views, where edits go to a staging area and are previewed before commit; query editor with visual explain plans, aggregation pipeline builder, schema analysis and validation rules, index suggestions, GridFS, server monitoring, slow queries, users and roles, import / export and cross-connection data transfer |
| **Redis** | New connection → Redis | Redis / Valkey in standalone, Sentinel and Cluster deployments, with TLS. Keyspace browser, typed editors with a decoding chain, command console, plus overview, slow log, clients, Pub/Sub, cluster and memory analysis; detect Redis from an SSH session and prefill the connection |
| **S3 object storage** | New connection → S3 | Amazon S3, MinIO, Ceph RGW, Alibaba Cloud OSS, Tencent Cloud COS, Cloudflare R2 and more. Reuses the host's dual-pane browser and transfer queue, and adds a bucket manager (22 bucket settings) and object inspector (versions, tags, retention, encryption, S3 Select, presigned URLs) |
| **Docker panel** | Command palette (`Ctrl+P`) → search “Docker” | Seven pages: overview, containers, images, volumes, networks, Compose and system. Event-driven live updates, inline CPU sparklines, logs, in-container file browsing and editing, a panel console and tiered cleanup; manage remote or local Docker |
| **Telnet** | New connection → Telnet | Reach switches, routers and other network devices, with RFC 854 option negotiation (ECHO / SGA / TERMINAL-TYPE / NAWS / BINARY) and 8-bit clean transfer |
| **Serial** | New connection → Serial | Connect to RS-232 / USB-to-serial devices, with hot-plug port discovery, baud rate / parity / flow control, newline normalisation, local echo and send pacing; send BREAK or pulse DTR to reset a board from the command palette |

Telnet and serial sessions open in regular terminal tabs, so the host's VT engine, scrollback, search, session logs, recording and ZMODEM all work.
RDP, Redis, S3, Telnet and serial plug in as connection types and join groups, the session tree, recent connections and Gist sync just like SSH connections; MongoDB manages its connections inside the workbench. Passwords for every plugin are kept in the host's encrypted store.

<details>
<summary><b>Security boundaries and usage notes</b></summary>

- **Docker panel** reuses an existing SSH session and connects straight to the remote `/var/run/docker.sock` through `direct-streamlocal@openssh.com`:
  the Docker daemon never needs to be exposed on ports 2375 / 2376, and no listening port is left open locally. Access to the Docker socket usually amounts to root on the host, so check what a volume holds before pruning it.
- **Redis** sorts commands into write / dangerous / destructive tiers using the flags reported by the server: production connections are read-only by default, dangerous commands need confirmation each time, and `FLUSHALL` and similar commands require typing a confirmation string.
  Client-side read-only mode only guards against slips; real permission boundaries still come from ACLs.
- **SSH jump hosts for MongoDB and Redis** are set up by the host, which opens the session and local forward, so plugins never see the jump host's credentials.
- **RDP** is a pure managed C# protocol stack with no bundled native RDP / FFmpeg libraries, and the same implementation runs on all three platforms. Clipboard, drive, printer and smart-card redirection are off by default and must be enabled individually.
- **Telnet** is plaintext, with no host fingerprint verification or transport encryption. Do not use it on untrusted networks.

See the [user manual](manual.en.md#plugins) for detailed usage, and the design notes for
[RDP](https://github.com/VelaShell/velashell-docs/blob/main/en/host/rdp-plugin-design-and-use.md) ·
[MongoDB](https://github.com/VelaShell/velashell-docs/blob/main/en/host/mongodb-workbench-plugin-design.md) ·
[Redis](https://github.com/VelaShell/velashell-docs/blob/main/en/host/redis-client-plugin-research-and-design.md) ·
[S3](https://github.com/VelaShell/velashell-docs/blob/main/en/host/s3-protocol-plugin-design.md) ·
[Telnet and serial](https://github.com/VelaShell/velashell-docs/blob/main/en/host/telnet-and-serial-feasibility-research.md).
Plugin sources are in [velashell-plugins](https://github.com/VelaShell/velashell-plugins).

</details>

### Build a plugin

Plugins can load **in-process** or run in a **separate process**, sharing one SDK contract and declaring their mode in the manifest.
In-process plugins can embed their UI in the workspace; isolated plugins communicate over named-pipe RPC with heartbeats, restarts and idle recycling.
Dangerous capabilities require individual consent, and publisher signing keys are pinned at installation. The plugin manager handles installation, enable / disable, removal and update checks.

[Development guide](https://github.com/VelaShell/velashell-docs/blob/main/en/templates/dev-guide.md) · [SDK reference](https://github.com/VelaShell/velashell-docs/blob/main/en/sdk/sdk-reference.md) · [CLI manual](https://github.com/VelaShell/velashell-docs/blob/main/en/cli/cli.md) · [Packaging & publishing](https://github.com/VelaShell/velashell-docs/blob/main/en/templates/publishing.md)

<details>
<summary><b>Host capabilities and related repositories</b></summary>

Plugins access the host through `IPluginContext`:

| Capability | API |
| --- | --- |
| Session state and connection management | `Sessions` |
| Terminal input / output, remote commands and files | `Terminal` · `RemoteExec` · `RemoteFs` |
| Reuse SSH channels | `RemoteTunnel` |
| Register connection types and workbenches | `Protocols` · `Workspaces` |
| Private plugin data and encrypted secrets | `Storage` · `TimeSeries` · `Secrets` |
| Commands, events, panels and desktop interaction | `Commands` · `Events` · `Ui` · `Clipboard` · `Log` |

| Repository | Responsibility |
| --- | --- |
| **[VelaShell](https://github.com/joesdu/VelaShell)** | Desktop app, plugin runtime, SSH library, X server and AI plugin |
| [velashell-plugin-sdk](https://github.com/VelaShell/velashell-plugin-sdk) | SDK contracts, test doubles and build tooling |
| [velashell-plugin-cli](https://github.com/VelaShell/velashell-plugin-cli) | The `vela-plugin` command-line tool |
| [velashell-plugin-templates](https://github.com/VelaShell/velashell-plugin-templates) | `dotnet new velaplugin` templates |
| [velashell-plugins](https://github.com/VelaShell/velashell-plugins) | First-party plugins installed on demand |
| [velashell-docs](https://github.com/VelaShell/velashell-docs) | Ecosystem design and developer documentation, mirrored in Chinese and English |

The SDK comes from NuGet, with versions centralised in `src/Directory.Packages.props` and `tests/Directory.Packages.props`.
Configure debugging with `vela-plugin dev init`, then press F5. Test doubles are provided by `VelaShell.PluginSdk.Testing`.
The AI plugin is built and released with the host; see [`plugins/README.md`](plugins/README.md) for the rationale.

</details>

<a id="data"></a>

## 🔐 Data & privacy

Connections, settings and history are stored locally; credentials are encrypted with **AES-256-GCM**.
Optional **GitHub Gist sync** supports passphrase-based end-to-end encryption and revision history. Security audit logs offer filtering and configurable retention.
See **[PRIVACY.md](PRIVACY.md)** for outbound requests, AI data flows and third-party services.

<details>
<summary><b>Data locations, keys and migration</b></summary>

Standard distributions default to `~/.velashell`. During development, use `--data-root <directory>` to select a separate data root.

| Content | Default location |
| --- | --- |
| SonnetDB data: connections, groups, settings, host fingerprints, history, audit, recordings and plugin data | `~/.velashell/sonnetdb` |
| Session and diagnostic logs | `~/.velashell/logs` |
| User-installed plugins | `~/.velashell/plugins` |
| Bundled plugins | `plugins/` in the application directory |
| Host registration / development mounts / shadow copies | `~/.velashell/host.json` · `plugins.dev.txt` · `dev-shadow/` |
| SSH key pairs created by key management | `~/.ssh` |

Credential encryption keys are stored preferentially in the **macOS login keychain / Linux Secret Service**; `secret.key.ref` in the data root holds references only.
Windows and systems without an available keychain use `secret.key`; on Windows it is bound to the current user with DPAPI.
**Copying the data directory alone does not guarantee credential decryption on another device or account.** Follow the [backup and migration guide](manual.en.md#backup).

Legacy JSON settings and the former `%LocalAppData%/VelaShell` data root migrate on first run, with old files preserved as `*.migrated.bak` or under `.migration-backup/`.
Microsoft Store data is redirected by the system and does not automatically transfer to or from portable builds.

</details>

<a id="development"></a>

## 🛠️ Develop from source

Install the **[.NET 11 SDK](https://dotnet.microsoft.com/download)**. The repository enables preview language features and `runtime-async`,
has no `global.json`, and uses `11.0.x` in CI. Docker is needed only for the local SSH test server and applicable interoperability tests.

```powershell
git clone https://github.com/joesdu/VelaShell
cd VelaShell

dotnet build VelaShell.slnx -c Debug
dotnet run --project src\VelaShell\VelaShell.csproj -- --data-root "$HOME/velashell-dev"
```

These examples use PowerShell. In Bash / Zsh, replace `\` with `/` in project paths.

> [!TIP]
> Use a separate `--data-root` during development: both data and the single-instance lock follow it, keeping your everyday profile separate.
> Close any app using the same output directory before building to avoid locked files.

<details>
<summary><b>Project layout and technology</b></summary>

```text
VelaShell/
├── src/
│   ├── VelaShell/                 # Desktop entry point, DI, views and VelaDock
│   ├── VelaShell.Terminal/        # VT engine and terminal rendering
│   ├── VelaShell.Presentation/    # View models and workflows
│   ├── VelaShell.Controls/        # Reusable controls, theme tokens and fonts
│   ├── VelaShell.Core/            # Domain models, contracts and localisation; UI-free
│   ├── VelaShell.Infrastructure/  # Connections, persistence, encryption, sync and plugins
│   ├── VelaShell.Ssh/             # SSH / SFTP / forwarding library, MIT
│   ├── VelaShell.XServer/         # X11 server library, MIT
│   └── VelaShell.PluginHost/      # Isolated plugin host process
├── plugins/VelaShell.Plugin.Ai/   # Bundled AI assistant
├── tests/                         # Unit, UI, interop tests and benchmarks
├── build/                         # Platform packaging scripts
├── scripts/                       # Release, test and code-generation tools
└── VelaShell.slnx                 # Solution
```

Each project's `README.md` describes its responsibilities and dependencies. Core has no UI framework dependency, DI is composed in
[`App.axaml.cs`](src/VelaShell/App.axaml.cs), and the SSH and X Server libraries do not depend on host assemblies.

| Area | Technologies |
| --- | --- |
| UI | Avalonia 12.1 · ReactiveUI · VelaDock · AvaloniaEdit |
| Connections & data | VelaShell.Ssh · FluentFTP · SonnetDB · BouncyCastle · MaxMind.Db |
| AI plugin | Microsoft.Extensions.AI · ModelContextProtocol · LiveMarkdown.Avalonia |
| Engineering | MSTest · BenchmarkDotNet · Central package management · SourceLink |

See the [host architecture](https://github.com/VelaShell/velashell-docs/blob/main/en/host/architecture.md)
and [`DESIGN.md`](DESIGN.md) for architectural and UI conventions.

</details>

<details>
<summary><b>Tests and local plugin development</b></summary>

```powershell
dotnet test VelaShell.slnx -c Debug
dotnet test tests\VelaShell.Terminal.Tests -c Debug
dotnet run -c Release --project tests\VelaShell.Benchmarks -- --filter '*VtParser*'

docker compose -f docker-compose.test.yml up -d
# Local SSH test server: testuser / testpass @ localhost:2222
```

Tests cover domain models, VT parsing and pixel rendering, view models, persistence and encryption, plugin RPC, headless UI,
and interoperability with real SSH / X clients. BenchmarkDotNet is for before / after comparisons on the same machine, not a CI gate.

Integration tests require the appropriate environment: `DockerIntegration` needs Docker; cross-platform publishing tests need `VELASHELL_PUBLISH_TESTS=1`;
X Server interop needs test images and `VELASHELL_XSERVER_INTEROP=1`. Check skipped tests in the report rather than treating missing prerequisites as verified behaviour.
See [`AGENTS.md`](AGENTS.md) for headless UI test conventions.

A clean clone builds only the bundled AI plugin. Unpack other `.vpx` plugins into `artifacts/plugins/`,
or set `-p:VelaPluginsStageDir=<directory>`; builds mirror them into the output's `plugins/` directory.
**Do not stage a second copy of the AI plugin**: duplicate IDs invalidate the extra plugin.

</details>

<details>
<summary><b>Release packaging and CI</b></summary>

```powershell
pwsh scripts\publish-all.ps1
```

Packages go to `publish/` for all three platforms on x64 / arm64, including the runtime, `VelaShell.PluginHost` and the AI plugin.
`.dmg` files are built on a macOS runner. See the Linux packaging guides for [AppImage](build/appimage/README.md) and [deb / rpm](build/linux-packages/README.md).

| Pipeline | Triggers and responsibilities |
| --- | --- |
| [`ci.yml`](.github/workflows/ci.yml) | Pushes to `main`, PRs or manual runs; three-platform Debug builds and tests with warnings as errors; an additional Linux build with `runtime-async=off` checks spans across `await` |
| [`release.yml`](.github/workflows/release.yml) | Release publication or editing; packages all three platforms and produces `SHA256SUMS.txt`, `latest.json` and an MSIX for Store submission; before packaging it takes one models.dev spec snapshot and bundles it into the AI plugin ([`scripts/Update-ModelsDevSnapshot.ps1`](scripts/Update-ModelsDevSnapshot.ps1); a failed fetch only warns) |

CI uses Debug to avoid requiring the Release strong-name key, and filters out the `DockerIntegration` / `CrossPlatform` / `Interop` categories that need extra infrastructure.
Shortcut documentation checks need `velashell-docs` checked out alongside this repository. Versions come from Release tags through `-p:Version`; `0.0.1-dev` is only a development placeholder.

</details>

<a id="docs"></a>

## 📚 Documentation & roadmap

| I want to… | Start here |
| --- | --- |
| Install, connect, transfer files, configure AI or troubleshoot | [English user manual](manual.en.md) · [中文手册](manual.md) |
| Look up shortcuts and interaction design | [Keyboard shortcuts](https://github.com/VelaShell/velashell-docs/blob/main/en/host/keyboard-shortcuts.md) · [Host documentation](https://github.com/VelaShell/velashell-docs/tree/main/en/host) |
| Learn how first-party plugins work | [Plugin manual](manual.en.md#plugins) · [First-party plugins repository](https://github.com/VelaShell/velashell-plugins) · [Plugin design docs](https://github.com/VelaShell/velashell-docs/tree/main/en/host) |
| Build a plugin or understand permissions | [Plugin development guide](https://github.com/VelaShell/velashell-docs/blob/main/en/templates/dev-guide.md) · [Plugin design](https://github.com/VelaShell/velashell-docs/tree/main/en/plugins) |
| Understand the SSH / X11 implementation | [SSH docs](https://github.com/VelaShell/velashell-docs/tree/main/en/ssh) · [X Server docs](https://github.com/VelaShell/velashell-docs/tree/main/en/xserver) |
| See completed work and future plans | [Progress log](plan.md) · [Roadmap](feature-plan.md) · [Plugin status](https://github.com/VelaShell/velashell-docs/blob/main/en/plugins/STATUS.md) |
| Read release changes | [Releases](https://github.com/joesdu/VelaShell/releases) · [Changelog archive](changelogs/) |

VelaShell is under **active development**. Refer to the user manual and the relevant release notes for current behaviour and limitations.
Design and developer documentation lives in [velashell-docs](https://github.com/VelaShell/velashell-docs), with mirrored `zh/` and `en/` trees.
User manuals, contribution guidelines and project-level references stay with the code in this repository.

`plan.md`, `feature-plan.md` and `AGENTS.md` are written in Chinese.

<a id="contributing"></a>

## 🤝 Contributing

Report problems or suggest improvements through **[Issues](https://github.com/joesdu/VelaShell/issues)**, or submit a pull request.
Start with the **[contribution guide](CONTRIBUTING.en.md)**; AI agents and newcomers should also read [`AGENTS.md`](AGENTS.md).

UI text needs all five languages, colours use design tokens, shortcuts are catalogued in `ShortcutCatalog.cs`, and the SDK comes from NuGet.
Read each library's clean-room rules before changing SSH / X Server code. Behaviour changes require matching updates to user manuals and relevant design documents.

Report vulnerabilities privately through **[SECURITY.md](SECURITY.md)**, not a public issue.

<a id="sponsor"></a>

## 💖 Support VelaShell

VelaShell is **free forever** for individuals and companies. Sponsorship is entirely voluntary, unlocks no extra features, and does not create a separate “sponsor edition”.

| Channel | Payment methods |
| --- | --- |
| [Afdian (爱发电)](https://afdian.com/a/velashell) | Alipay / WeChat Pay |
| [Wise](https://wise.com/pay/me/yud162) | Card / bank transfer |

<details>
<summary><b>Alipay / WeChat Pay QR codes</b></summary>

<p align="center">
  <img src="src/VelaShell/Assets/donate-alipay.png" width="200" alt="Alipay donation QR code">
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="src/VelaShell/Assets/donate-wechat.png" width="200" alt="WeChat Pay donation QR code">
</p>

</details>

The same options are available under **Settings → Support & Donate**. Thank you to everyone who supports the project.

### Thank you, donors

Listed newest first; donors who left no name are shown as “Anonymous”.

<!-- Add new entries as the first row below the header, keeping the currency symbol. Update README.md too. -->

| Donor ID | Amount | Channel |
| :---: | :---: | :---: |
| *清 | ￥50 | WeChat Pay |
| 天真i | ￥50 | QQ Pay |
| s*y | ￥10 | WeChat Pay |

Include the ID you would like displayed in your message or transfer note. To remain anonymous, change your name or be removed, contact <dygood@outlook.com>.

<a id="license"></a>

## 📄 License

VelaShell is **dual-licensed under AGPL-3.0 and a commercial license**:

| License | Scope |
| --- | --- |
| **[AGPL-3.0](LICENSE)** | The default open-source license. Use, modification and distribution must comply with its terms; see the full license for source-code and network-service obligations. |
| **[Commercial license](LICENSE-COMMERCIAL.md)** | Available for closed-source integration or distribution, or when corporate policy does not permit AGPL. Contact the author to purchase a license. |
| **MIT components** | [VelaShell.Ssh](src/VelaShell.Ssh/LICENSE) and [VelaShell.XServer](src/VelaShell.XServer/LICENSE) use the MIT licenses in their respective directories. |

Retain the project's copyright and license notices. The copyright holder reserves the right to pursue unlawful closed-source distribution or sale that strips project notices.
The official app is free, distributed through this repository's **GitHub Releases** and the **Microsoft Store** listing of the same name.
**The VelaShell name and logo are not covered by the open-source license**; derivative versions may not use them for promotion or sales without permission.

For commercial licensing, email <dygood@outlook.com> with the subject **Commercial License**.
Contributions are licensed under AGPL-3.0 and grant the copyright holder commercial sublicensing rights; see [commercial licensing §3](LICENSE-COMMERCIAL.md#3-说明).

---

<details>
<summary><b>🪶 About the name</b></summary>

**Pronunciation**: `/ˈveɪlə ʃɛl/`, **VAY-la shell**, with the stress on the first syllable.

**Vela** is Latin for “sails” and the name of a southern constellation, split from Argo Navis alongside Carina and Puppis.
**Shell** is the command-line interface connecting you to remote hosts. Together, they suggest “a terminal as your sail, riding the signal winds to remote hosts”.
The `>_` prompt on the teal gradient icon captures that idea.

</details>

<div align="center">

**VelaShell · Born for the command line**

[Download](https://github.com/joesdu/VelaShell/releases) · [User manual](manual.en.md) · [Report an issue](https://github.com/joesdu/VelaShell/issues)

© 2026 VelaShell authors and contributors

</div>
