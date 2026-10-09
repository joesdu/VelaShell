<div align="center">

# VelaShell

**A modern, cross-platform SSH terminal client built for sysadmins and developers**

`/ˈveɪlə ʃɛl/` · A terminal as your sail, riding the signal winds to remote hosts

[![.NET](https://img.shields.io/badge/.NET-11.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.1-8B44AC)](https://avaloniaui.net/)
[![CI](https://github.com/joesdu/VelaShell/actions/workflows/ci.yml/badge.svg)](https://github.com/joesdu/VelaShell/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/joesdu/VelaShell?label=release)](https://github.com/joesdu/VelaShell/releases)
[![License](https://img.shields.io/badge/license-AGPL--3.0%20%7C%20Commercial-blue)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)](#-platforms--distribution)

[简体中文](README.md) · **English** · [Plugin marketplace](https://market.easilynet.top) · [Docs](https://github.com/VelaShellLabs/velashell-docs)

<img src="mascot/chibi.png" alt="VelaShell mascot" width="720">

</div>

---

VelaShell is a desktop terminal application built with **.NET 11 + Avalonia**, released natively for
Windows, Linux and macOS as self-contained builds (no .NET runtime needed on the target machine),
and **free forever** for individuals and companies alike.

It puts everything a day of remote work needs into one window: a VT terminal engine, SSH / SFTP / FTP
connections, local shells, jump hosts and network proxies, port-forwarding tunnels, dual-pane SFTP with
directory sync, remote editing, a **built-in X server**, resource monitoring and traceroute,
session recording and replay, a command palette and a twelve-page settings centre. On top of that sits a
**dual-mode plugin system** (in-process / isolated process) and a first-party **AI assistant plugin**.

Three trade-offs run through all of it: **keyboard-first**, **information-dense**, **snappy**.

---

## ✨ Features

| | |
| --- | --- |
| **Terminal** | VT engine (DEC ANSI / VT / Xterm state machine) · ten terminal profiles (vt52 → xterm-256color) · 256 colours / true colour / line drawing / primary and alternate screens / mouse protocols / CJK double width · custom-drawn rendering · linear / block / disjoint multi-span selection · line-number and timestamp gutters · IME pre-edit for CJK input · OSC 8 hyperlinks (Ctrl+click) · OSC 133 command blocks (failures in red, jump between prompts, select a command's output in one click, fold by block) |
| **Connectivity** | SSH · SFTP · FTP / FTPS · local shells (Windows ConPTY) · ProxyJump (≤ 5 hops, cycle detection) · HTTP / SOCKS5 / follow-system proxy · auto-reconnect (including wake from sleep / network recovery) · keep-alive and anti-idle · a per-connection post-login command · SSH compression · configurable algorithm lists (allow legacy algorithms in one click for old switches, or customise in OpenSSH syntax) |
| **Auth and keys** | Password / private key / **SSH certificate** / **SSH agent** · **two-step verification** (keyboard-interactive: a prompt pops up when the server asks for a one-time code, the password prompt is answered for you; key + code works too) · agent forwarding (optionally only selected keys, with a prompt before every signature) · TOFU host fingerprints, with a prompt to decide when one changes · key generation (Ed25519 by default / ECDSA / RSA 4096) · optionally add keys to the agent after login · **shared credentials** (one username and password / key reused by many connections — change it once and every connection picks it up on its next connect) |
| **X11** | X11 forwarding · **built-in X server, VelaShell.XServer**: one native window per X window, two-way clipboard, multiple monitors and DPI, keyboard layout follows the system, GLX 1.4 · on Windows you can switch to an installed VcXsrv instead |
| **Files** | Dual-pane SFTP with drag-and-drop transfers · resumable transfers and a transfer queue · **directory compare and sync** (SHA-256 first, keep remote up to date) · symbolic links · remote files in the built-in editor (syntax highlighting), or in an external editor with upload on save |
| **Tunnels** | Local `-L` / remote `-R` / dynamic SOCKS5 `-D` · live connection and byte counters · automatic recovery after a disconnect · port-conflict pre-check |
| **Operations** | Resource monitor (CPU / memory / disk / network / processes) · process manager · traceroute with geo data · connection diagnostics · session recording and replay (exports asciicast v2) |
| **Workspace** | VelaDock drag-to-split · grouped sessions with pinning (groups reorder by dragging) · multi-select to open / edit / delete in bulk · connection import and export (JSON with optional encrypted passwords, CSV for bulk editing in Excel) · import from WinSCP / Xshell / `~/.ssh/config` · command palette (`Ctrl+P`) · rebindable shortcuts · quick-command snippets · **synchronised input across terminals** · smart command completion · message centre and security news feed |
| **Data** | Embedded SonnetDB (document + time series) · AES-256-GCM credential encryption · GitHub Gist cloud sync (optional passphrase end-to-end encryption, revisitable revisions) · security audit log (filterable viewer, configurable retention) · session logs |
| **Appearance** | 12 named themes (7 dark, 5 light) · 16 terminal palettes paired with the UI themes · fully tokenised, zero hard-coded colours · Cascadia Mono bundled |
| **Localisation** | 简体中文 / English / 繁體中文 / 日本語 / 한국어 — follows the system language by default, English when there is no match; the five resx files share an identical key set; missing translations and orphaned keys both turn the tests red |
| **Extensibility** | Dual-mode plugin host (collectible ALC / separate process + named-pipe RPC) · `.vpx` packages and a plugin manager (update checks, pinned publisher fingerprints) · per-capability consent for dangerous APIs · first-party AI assistant plugin |
| **Desktop integration** | Single instance · tray · launch at login · Xshell-compatible launch (bastion hosts and SSO portals can start it) · URL protocol registration |

<details>
<summary><b>Expand: a few implementation details worth a closer look</b></summary>

- **VT terminal engine** — the terminal is a custom-drawn Avalonia control that renders glyphs, selection
  and scrolling itself, with no dependency on abandoned third-party terminal controls. Selection supports
  linear and block modes, `Shift+click` to extend, and disjoint multi-span selection via `Ctrl+Shift+drag`
  (copy line 1 and line 3 in one go).

- **SSH stack** — [VelaShell.Ssh](src/VelaShell.Ssh/) (fully managed, async-first, MIT, source in this repository),
  implemented under a clean-room rule from the RFCs and the
  [behaviour specs in velashell-docs](https://github.com/VelaShellLabs/velashell-docs/tree/main/en/ssh),
  with interop tests against real OpenSSH. Jump-host chains are built hop by hop as nested connections carrying
  a `direct-tcpip` tunnel, and fingerprints are verified per logical host at every hop. When a fingerprint changes,
  a dialog shows the old and new fingerprints and lets you decide, instead of just failing with an error.

- **The tunnel data plane lives in the SSH library too** — local, dynamic and remote forwarding, along with the
  SOCKS5 server handshake (RFC 1928), are all implemented in `VelaShell.Ssh`, and per-forward byte and connection
  counts are read straight from the library. Relaying preserves half-close semantics; without it, any protocol
  that "sends the request, shuts down, then waits for the response" would never read a byte.

- **Built-in X server** — [VelaShell.XServer](src/VelaShell.XServer/) (MIT, fully managed, no native dependencies)
  ships with the app and starts from a title-bar button. It is rootless: every X top-level window becomes a native
  Avalonia window, with the host acting as the window manager; SSH's X11 channels are wired into the server
  in-process rather than through a local TCP port. GTK3, Qt5 and Mesa (`glxgears`) run with zero protocol errors.

- **FTP / FTPS** — built on [FluentFTP](https://github.com/robinrodricks/FluentFTP), with a connection pool for
  concurrent transfers (a single FTP control connection can only run one command at a time), sharing the same
  dual-pane browser and transfer stack as SFTP. When a server certificate fails validation, its SHA-256 fingerprint
  is shown for the user to confirm.

- **Themes are seed colours plus derivation** — a theme is 25 hand-picked seed colours (`UiThemePalette`); the other
  sixty-odd tokens are derived by `ThemeTokenApplier` under fixed rules, and per-theme contrast is checked by tests.

- **Shortcuts have a single source of truth** — `src/VelaShell/ViewModels/ShortcutCatalog.cs`, which both the
  settings page and the docs read from. Anything missing makes `ShortcutCatalogTests` fail and print a
  ready-to-paste Markdown row.

</details>

### 🤖 AI assistant plugin (first-party, ships with the app)

<details>
<summary>Click to see what it does</summary>

- **Multi-provider streaming chat** — three wire protocols (OpenAI Responses / Chat Completions-compatible /
  Anthropic Messages) covering OpenAI, Anthropic, Grok, Gemini, DeepSeek, Kimi, GLM and relay endpoints.
- **One click connects you** — a built-in provider catalogue: sign-in providers open your browser
  (authorization code + PKCE / device code), the rest only ask for an API key. If you already pay for
  **ChatGPT Plus, Claude Pro or GitHub Copilot**, you can spend that subscription here (flagged "experimental" in the UI).
- **Model list** — asks the endpoint's own `/models` first, then fills in context windows and pricing by id from models.dev.
- **Agent mode** — a `Microsoft.Extensions.AI` tool loop bridged to sessions / terminal / remote commands / remote files,
  with per-command approval for dangerous operations; attach custom **MCP servers**; web search and fetch included.
- **Team chat integration** — connect the agent to **Feishu / DingTalk / Telegram / WeCom**: pairing codes to authorise
  group chats, one-click approval, and an on-the-spot connectivity check.
- **Outbound MCP server** — Streamable HTTP, bound to `127.0.0.1` only, with a token required on every request,
  exposing VelaShell's session capabilities to external agents. This path has no approval UI, so "ask" mode
  means every write is refused.
- **Interaction details** — interject while the agent is running; `@` references remote files from the selected
  session; conversations can be browsed, resumed and deleted.

</details>

---

## 🖥️ Platforms & distribution

| Platform | Architecture | Packages |
| --- | --- | --- |
| **Windows** 10 / 11 | x64 · arm64 | Portable zip (in-app updates) · Microsoft Store MSIX |
| **Linux** | x64 · arm64 | Portable tar.gz · `.AppImage` · `.deb` / `.rpm` |
| **macOS** | x64 · arm64 | tar.gz · `.dmg` (unsigned / not notarised) |

Every build is **self-contained** — unpack anywhere and run.

- **In-app updates** (Settings → About) only consume the zip / tar.gz: download, verify SHA-256, then an external
  swap process that starts only after the app exits replaces the files and relaunches. A failed swap rolls back
  automatically, and user data under `~/.velashell` is never touched. The update channel (stable / preview) is
  switchable in Settings.
- Once installed, the dmg, AppImage, deb and rpm leave the app directory read-only, so the About page degrades to
  "download it manually".
- The Store build (MSIX) is updated by the Store and its data folder is redirected by the system, so **its settings,
  sessions and keys are separate from the portable build's**.

---

## 🚀 Getting started

### Prerequisites

- A [.NET SDK **11.0**](https://dotnet.microsoft.com/download) preview — `global.json` pins `11.0.100-rc.1.26425.128`
  (`rollForward: latestFeature`). The repo enables `EnablePreviewFeatures` and `runtime-async=on`, so until the
  final SDK ships it only builds with a preview SDK.
- (Optional) Docker, for the local SSH test server and interop tests

### Build and run

```bash
git clone https://github.com/joesdu/VelaShell
cd VelaShell

dotnet build VelaShell.slnx                 # the whole solution (including the plugin host and the AI plugin)
dotnet test  VelaShell.slnx                 # the full test suite

dotnet run --project src/VelaShell/VelaShell.csproj -- --data-root ~/velashell-dev   # start with a separate data directory
```

> - **Building while the app is running fails on locked files** — close the app first.
> - During development, pass `--data-root <dir>`: both the data and the single-instance lock follow it, so a dev
>   build never touches — or reconnects — the profile you use day to day.

<details>
<summary>Want Redis / S3 / Telnet plugins running locally too?</summary>

A clean clone builds with only the **AI plugin** this repo produces. Drop the other plugin directories into the
staging directory `artifacts/plugins/` (or point elsewhere with `-p:VelaPluginsStageDir=<dir>`); every build mirrors
them into `plugins/<plugin-dir>/` under the output directory, so F5 loads them. You can unpack them from the
**`.vpx` packages** released by [velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins) (`.vpx` is
VelaShell's plugin package format; inside is exactly the installed `plugins/<plugin>/` level), or point straight at
that repo's build output. The release pipeline **does not preinstall** these plugins — users install them on demand
from the [plugin marketplace](https://market.easilynet.top).

> ⚠️ Do not stage `velashell-ai` — this repo already produces it, and two plugins with the same id make the later
> one Invalid, which shows up as "the plugin mysteriously doesn't work".

</details>

### Local SSH test server

```bash
docker compose -f docker-compose.test.yml up -d
# testuser / testpass @ localhost:2222
```

### Where data lives

| Content | Location |
| --- | --- |
| SonnetDB data directory (profiles / groups / settings / known_hosts / history / audit / recordings / plugin data) | `~/.velashell/sonnetdb` |
| Credential encryption key (AES-256) | `~/.velashell/secret.key` |
| Session logs (when enabled) and diagnostic logs | `~/.velashell/logs` |
| Manually installed plugins (`.vpx`) | `~/.velashell/plugins` (first-party plugins live in the app's `plugins/`) |
| Host self-registration (lets `vela-plugin` find the install) | `~/.velashell/host.json` |
| Plugin development mounts and shadow copies | `~/.velashell/plugins.dev.txt`, `~/.velashell/dev-shadow/` |
| SSH key pairs (Key management page) | `~/.ssh` |

> Legacy JSON configuration and the former `%LocalAppData%/VelaShell` data root are migrated into `~/.velashell`
> on first run; old files are renamed to `*.migrated.bak` or kept under `.migration-backup/`.

---

## 📦 Build & release

```bash
pwsh scripts/publish-all.ps1     # every platform package in one run → publish/
```

Artifacts cover all three platforms on x64 / arm64, all self-contained. Besides the app, each package carries the
isolated-plugin host process `VelaShell.PluginHost` and a `plugins/` directory holding only the AI plugin. The `.dmg`
is produced only on CI's macOS runner; the `.AppImage` ([`build/appimage/`](build/appimage/README.md)) and
`.deb` / `.rpm` ([`build/linux-packages/`](build/linux-packages/README.md)) are cross-built on the same x64 runner.

| Pipeline | Trigger | What it does |
| --- | --- | --- |
| [`ci.yml`](.github/workflows/ci.yml) | push to `main` / every PR | windows + ubuntu + macos matrix, Debug build + full test suite, `-warnaserror` |
| [`release.yml`](.github/workflows/release.yml) | a Release is published | Packages all three platforms in parallel and attaches `SHA256SUMS.txt` and the `latest.json` update manifest; also produces an **MSIX** for Microsoft Store submission |

CI deliberately builds **Debug** (strong-name signing is Release-only, and PRs from forks and Dependabot cannot read
repository secrets), **excludes** the `DockerIntegration` / `CrossPlatform` categories, and checks out
`velashell-docs` alongside (the shortcut cross-check tests need to find it). The version comes from the Release tag
via `-p:Version`, so **releasing needs no code changes** — the `0.0.1-dev` in the repo is only a development placeholder.

---

## 🏗️ Project layout

```text
VelaShell/
├── src/
│   ├── VelaShell/                  # Desktop entry point, DI composition root, XAML views, VelaDock, window host for the X server
│   ├── VelaShell.Terminal/         # VT engine and the Avalonia rendering control
│   ├── VelaShell.Presentation/     # Cross-cutting view models and workflows
│   ├── VelaShell.Controls/         # Reusable controls, theme tokens and bundled fonts
│   ├── VelaShell.Core/             # Domain models, service contracts, persistence abstractions, localisation (UI-free)
│   ├── VelaShell.Infrastructure/   # SSH / SFTP / FTP / tunnels, SonnetDB persistence, credential encryption, sync, plugin management
│   ├── VelaShell.Ssh/              # SSH library (MIT, clean-room, see its AGENTS.md)
│   ├── VelaShell.XServer/          # X11 server library (MIT, clean-room, see its AGENTS.md)
│   └── VelaShell.PluginHost/       # Host process for isolated plugins (named-pipe RPC, SDK contract only)
├── plugins/VelaShell.Plugin.Ai/    # First-party AI assistant plugin (built and released with the app)
├── tests/                          # 11 MSTest projects + 1 BenchmarkDotNet project + the cert-lab certificate test server
├── build/                          # Packaging scripts for AppImage / deb / rpm / MSIX / social preview images
├── scripts/                        # One-shot publish script, plus interop test servers, benchmarks and code generators for SSH / X server
├── docker-compose.test.yml         # Local SSH test server
├── global.json                     # SDK version pin
├── Directory.Build.props           # Repo-wide version and shared MSBuild properties
└── VelaShell.slnx                  # Solution
```

Every source and test project has its own `README.md` describing its responsibilities, layout and dependencies.

### Architecture conventions

- **Strict layering** — `VelaShell → Presentation / Controls / Infrastructure → Core`, and Core depends on no UI
  framework; `VelaShell.Ssh` and `VelaShell.XServer` depend on no host assembly at all and can be taken out and used on their own.
- **Single composition root** — all DI registration lives in [`src/VelaShell/App.axaml.cs`](src/VelaShell/App.axaml.cs),
  with each layer contributing through `*ServiceCollectionExtensions`.
- **One persistence engine** — a single embedded SonnetDB instance holds both document and time-series data;
  interfaces in Core, implementation in Infrastructure.
- **Tokenised design** — **no colour literals in XAML or C#**; everything binds tokens via `DynamicResource`
  (rules in [`DESIGN.md`](DESIGN.md)).
- **Secure defaults** — credentials encrypted at rest, TOFU host fingerprints, "remember password" can be turned off
  per connection, per-capability consent for plugins; every case in which the app makes an outbound request is
  listed truthfully in [`PRIVACY.md`](PRIVACY.md).

---

## 🧩 Plugin system

**Dual-mode hosting** — a plugin can load **in-process** (a collectible `AssemblyLoadContext`, its UI docked straight
into the workspace) or run in a **separate process**, `VelaShell.PluginHost` (named-pipe RPC, so a crash never takes
down the app, with heartbeats, self-healing restarts and idle recycling). Both modes share one SDK contract, and the
plugin manifest declares which one it uses.

**Capability APIs** — plugins reach the host through `IPluginContext`:

| Capability | What it does |
| --- | --- |
| `Sessions` | Enumerate sessions, observe their state, open and close sessions from saved profiles |
| `Terminal` | Read terminal output, write terminal input |
| `RemoteExec` · `RemoteFs` | Run remote commands; read, write and list remote files |
| `RemoteTunnel` | Open `direct-tcpip` / `direct-streamlocal` channels over the host's existing connections |
| `Protocols` · `Workspaces` | **Register new connection types and workbenches** — this is how Telnet / serial / Redis / S3 join the session tree |
| `Storage` · `TimeSeries` · `Secrets` | Per-plugin private document and time-series storage, plus host-encrypted secrets |
| `Commands` · `Events` · `Ui` · `Clipboard` · `Log` | Register commands, subscribe to events, open panels (docked documents or standalone windows) |

Dangerous capabilities are granted one by one through a permission dialog; protocol and workbench ids must carry the
plugin's prefix; later versions of an installed plugin must be signed with the key pinned at install time. Plugins
ship as `.vpx` packages, and the manager window installs, enables, disables, uninstalls and checks for updates;
uninstalling also purges the plugin's private data. Third-party developers get a debugger in one command
(`vela-plugin dev init` → F5), and the SDK ships test doubles (`VelaShell.PluginSdk.Testing`).

📖 [Dev guide](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/templates/dev-guide.md)
· [CLI manual](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/cli/cli.md)
· [SDK reference](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/sdk/sdk-reference.md)
· [Packaging and publishing](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/templates/publishing.md)
· [Design blueprint](https://github.com/VelaShellLabs/velashell-docs/tree/main/en/plugins)

**Provided by plugins** (not preinstalled; install on demand from the [plugin marketplace](https://market.easilynet.top),
sources in [velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins)):

- **Telnet, serial (COM / USB-to-serial), Redis, S3**
- **Docker panel** — a full Docker management surface on top of an already-connected SSH session (containers / images /
  volumes / networks / Compose, live stats, a merged log stream, in-container file editing and a TTY console).
  It talks to the remote `/var/run/docker.sock` over the SSH session (`direct-streamlocal@openssh.com`), so
  **nothing changes on the server**, and the daemon never needs to be exposed on 2375 / 2376.

### Six repositories, one job each

| Repository | Owns | How it reaches this repo |
| --- | --- | --- |
| **joesdu/VelaShell** (this one) | The app, the host-side plugin runtime, the SSH library, the X server library, the AI plugin | — |
| [**velashell-plugin-sdk**](https://github.com/VelaShellLabs/velashell-plugin-sdk) | The plugin contract SDK | `VelaShell.PluginSdk` / `.Testing` **NuGet packages** |
| [**velashell-plugin-cli**](https://github.com/VelaShellLabs/velashell-plugin-cli) | The `vela-plugin` CLI and `VelaShell.PluginSdk.Build` | NuGet packages (for plugin authors; not referenced here) |
| [**velashell-plugin-templates**](https://github.com/VelaShellLabs/velashell-plugin-templates) | The `dotnet new velaplugin` templates | NuGet package (for plugin authors; not referenced here) |
| [**velashell-plugins**](https://github.com/VelaShellLabs/velashell-plugins) | The Redis / S3 / Telnet / serial / Docker-panel plugins | One **`.vpx` package** per plugin (release assets / marketplace) |
| [**velashell-docs**](https://github.com/VelaShellLabs/velashell-docs) | **All documentation** for every repository above | — |

The SDK contract always arrives as a NuGet package, never a project reference, pinned in `src/Directory.Packages.props`
and `tests/Directory.Packages.props`. **The AI plugin is the exception** and stays here: its couplings to the host are
all compile-time (it borrows the host's AvaloniaEdit, must load in-process, and must match the host's Avalonia version
exactly), so in a separate repo even a one-line UI change would need a release before it could be tested together
(details in [`plugins/README.md`](plugins/README.md)).

---

## 🧪 Tests

```bash
dotnet test VelaShell.slnx                          # everything
dotnet test tests/VelaShell.Terminal.Tests/         # a single project
dotnet run -c Release --project tests/VelaShell.Benchmarks -- --filter *VtParser*   # benchmarks
```

| Test project | Scope |
| --- | --- |
| `VelaShell.Core.Tests` | Domain models, the transfer queue, tunnels, directory sync, sync encryption |
| `VelaShell.Terminal.Tests` · `.RenderTests` | VT parsing and emulation; **pixel-level** glyph rendering regressions (real rasterisation on Skia's software backend) |
| `VelaShell.Presentation.Tests` | View-model workflows and commands |
| `VelaShell.Infrastructure.Tests` | Persistence, credential encryption, ConPTY, SSH wiring, key management, plugin management and cross-process RPC |
| `VelaShell.Controls.Tests` | Custom controls, theme tokens and style guards |
| `VelaShell.Tests` | Window-level view models, the authentication flow, plugin panels, window chrome, headless views |
| `VelaShell.Plugin.Ai.Tests` | AI plugin: approval gate, capability bridging, team chat integration, headless panel interaction |
| `VelaShell.ShellIntegration.Tests` | End-to-end checks for "file browser follows the terminal's directory": real sshd, real login shells, real PTYs |
| `VelaShell.Ssh.Tests` · `VelaShell.XServer.Tests` | Unit tests for the two MIT libraries, plus interop against real OpenSSH / real X clients |
| `VelaShell.Benchmarks` | BenchmarkDotNet benchmarks, **not a CI gate** (results swing too much with machine load); for before/after comparisons on the same machine |

> ⚠️ **An early bail-out counts as "passed" in MSTest.** `DockerIntegration` needs Docker and `docker-compose.test.yml`,
> `CrossPlatformPublishTests` needs `VELASHELL_PUBLISH_TESTS=1`, and the X server's real-client tests need
> `VELASHELL_XSERVER_INTEROP=1`. Without their prerequisites they go quietly green without running a line — to confirm
> they actually ran, look for `[SKIP]` lines in `TestContext`.
>
> ⚠️ **In headless UI tests, use the value-returning overload**: `Dispatch(async () => { …; return true; })`.
> A void-returning lambda yields a `Task<Task>` that is never awaited: the test stops at the first `await`, "passes",
> and every assertion failure is lost.

---

## 📚 Documentation

All documentation lives in **[VelaShellLabs/velashell-docs](https://github.com/VelaShellLabs/velashell-docs)** —
English in [`en/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en), 中文在 [`zh/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh); the two trees mirror each other.

| Area | Contents |
| --- | --- |
| [`en/host/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en/host) | **This repository**: [layering and dependencies](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/host/architecture.md), [interaction and UI specs](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/host/interaction-and-ui-specs.md), [keyboard shortcuts](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/host/keyboard-shortcuts.md), [settings audit](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/host/settings-audit.md), [Xshell-compatible launch](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/host/xshell-compatible-login.md), plus design notes and feasibility research |
| [`en/ssh/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en/ssh) · `en/xserver/` | Architecture, behaviour specs and getting started for the SSH and X server libraries |
| [`en/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en/plugins) | The plugin system design blueprint + [status overview](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/plugins/STATUS.md) |
| [`en/sdk/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en/sdk) · [`en/cli/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en/cli) · [`en/templates/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en/templates) | SDK reference, the `vela-plugin` manual, the plugin dev guide and packaging / publishing |

A few documents stay in this repository, because what they serve is writing code *here*:

- [`AGENTS.md`](AGENTS.md) — working conventions for AI agents and newcomers (**read it before you start**)
- [`DESIGN.md`](DESIGN.md) — the design system: colour / type / spacing tokens and component rules
- [`plan.md`](plan.md) — **what already happened**: current architecture, the progress log, the reasoning behind every change
- [`feature-plan.md`](feature-plan.md) — **what has not happened yet**: backlog, roadmap, and the won't-do list with its reasons
- [`CONTRIBUTING.en.md`](CONTRIBUTING.en.md) · [`SECURITY.md`](SECURITY.md) · [`PRIVACY.md`](PRIVACY.md)

> `plan.md`, `feature-plan.md` and `AGENTS.md` are written in Chinese.

---

## 🛠️ Tech stack

| | |
| --- | --- |
| **Runtime / UI** | .NET 11 (preview features and `runtime-async` enabled) · Avalonia 12.1 · ReactiveUI |
| **Core components in this repo** | VT terminal engine · VelaDock split/dock layout · VelaShell.Ssh (SSH / SFTP / port forwarding / SOCKS5 server) · VelaShell.XServer (X11 server) · plugin runtime · portable self-update |
| **Third party** | FluentFTP (FTP / FTPS) · SonnetDB (embedded document + time-series database) · BouncyCastle (the curve arithmetic the BCL lacks, such as Ed25519) · AvaloniaEdit (editor and AI composer) · MaxMind.Db (offline IP geolocation) |
| **AI plugin** | Microsoft.Extensions.AI · ModelContextProtocol · LiveMarkdown.Avalonia (with Mermaid / LaTeX / SVG) |
| **Engineering** | MSTest · BenchmarkDotNet · central package management · SourceLink |

---

## 🚧 Project status

The project is under **active development** and ships regularly; see [Releases](https://github.com/joesdu/VelaShell/releases) for the latest version.

Everything in the feature list above works today. A handful of settings are still only persisted and not yet wired
to runtime behaviour; they have either been removed from the UI or are itemised in the P0 table of
[`feature-plan.md`](feature-plan.md). System keychain and external password-manager integration (1Password / Bitwarden / KeePassXC) are still at the design stage.
The full completion record is in [`plan.md`](plan.md), the backlog and roadmap in [`feature-plan.md`](feature-plan.md),
and the plugin side in the [status overview](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/plugins/STATUS.md).

---

## 🤝 Contributing

Issues and pull requests are welcome. **Read [`CONTRIBUTING.en.md`](CONTRIBUTING.en.md) before you start**
(AI agents: see also [`AGENTS.md`](AGENTS.md)). A few hard rules that will stop a change on the spot:

- **UI strings need all five resx files** (`Strings` / `zh-Hans` / `zh-Hant` / `ja` / `ko`); no hard-coded strings.
- **To add or change a shortcut, edit `ShortcutCatalog.cs` first** — it is the single source of truth.
- **No colour literals in XAML or C#**; bind tokens via `DynamicResource`.
- **The plugin SDK always comes from NuGet**; never pick an SDK version yourself, and never build a local package.
- **Before touching `src/VelaShell.Ssh/` or `src/VelaShell.XServer/`, read that directory's `AGENTS.md`** — both
  libraries follow a clean-room rule.
- **Behaviour changes must be synced to velashell-docs** — two PRs that reference each other and merge together.

Found a security vulnerability? **Do not open a public issue** — follow the private process in [`SECURITY.md`](SECURITY.md).

---

## 💖 Sponsor

VelaShell is **free forever** for individuals and companies alike. Sponsoring is entirely voluntary — it unlocks
nothing, and there is no "sponsor build" separate from the free one.

| Channel | Best for | Link |
| --- | --- | --- |
| Wise | International, card or bank transfer | <https://wise.com/pay/me/yud162> |
| Afdian (爱发电) | Mainland China, Alipay / WeChat Pay | <https://afdian.com/a/velashell> |

Or scan directly:

<div align="center">
  <img src="src/VelaShell/Assets/donate-alipay.png" width="200" alt="Alipay" />
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="src/VelaShell/Assets/donate-wechat.png" width="200" alt="WeChat Pay" />
</div>

The same links live in the app under **Settings → Support & Donate**. Thank you to everyone who chips in.

---

## 📄 License

VelaShell is **dual-licensed**:

- **[AGPL-3.0](LICENSE) (default)** — free to use, modify and distribute, but derivative works (including anything
  offered as a network service) **must release their complete source under the same license**, keeping copyright and
  donation notices intact. Stripping the project's identity and selling it closed-source is infringement, and will be
  pursued (DMCA takedowns / litigation).
- **[Commercial license](LICENSE-COMMERCIAL.md) (paid, on request)** — if you need closed-source integration or
  distribution, or corporate policy rules out AGPL, contact the author to purchase one
  (📧 <dygood@outlook.com>, subject line "Commercial License").

The two libraries `src/VelaShell.Ssh/` and `src/VelaShell.XServer/` are the exception: each is licensed under the
**MIT** license in its own directory.

**Authenticity notice**: VelaShell itself is **free forever** for individuals and companies alike. The only official
distribution channels are this repository's GitHub Releases (and the Microsoft Store build of the same name); any
"paid VelaShell" from any other channel is pirated. The "VelaShell" name and logo are not covered by the open-source
license — derivative versions must not use them to promote or sell.

By contributing you agree that your contribution is licensed under AGPL-3.0 and that the copyright holder may
sublicense it under the commercial license (see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) §3).

---

## 🪶 About the name

**Pronunciation**: `/ˈveɪlə ʃɛl/` — say it as **"VAY-la shell"**, stress on the first syllable.

- **Vela** — Latin for "sails". Vela is a southern constellation which, together with Carina (the keel) and Puppis
  (the stern), was split out of Argo Navis — the ship Jason and the Argonauts sailed in search of the Golden Fleece.
  It carries the sense of **setting sail for distant shores**.
- **Shell** — the command-line shell, and the heart of this app: a terminal attached to a remote host.

Together, VelaShell means **"a terminal as your sail, riding the signal winds to remote hosts"**. The icon distils
that idea: a dark `>_` prompt on a teal gradient rounded square.

---

<div align="center">

**VelaShell** — born for the command line.

© 2026 VelaShell authors and contributors

</div>
