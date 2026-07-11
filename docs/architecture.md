# VelaShell Architecture Blueprint

## Recommended Baseline

```text
VelaShell.App            -> Desktop entry point and composition root
VelaShell.Presentation   -> ViewModels, navigation, orchestration
VelaShell.Controls       -> Custom controls, styles, tokens, behaviors
VelaShell.Terminal       -> Terminal engine, parser, renderer, interaction model
VelaShell.Core           -> Stable domain models and contracts
VelaShell.Infrastructure -> SSH, storage, platform integration, background work
```

## Dependency Direction

```text
App -> Presentation
App -> Controls
App -> Infrastructure
App -> Core

Presentation -> Core
Presentation -> Terminal

Controls -> Core (optional, only for shared UI contracts)

Infrastructure -> Core
Infrastructure -> Terminal (only if an adapter truly belongs here)

Terminal -> Core
```

## Why Split Out `VelaShell.Controls`

- The design file contains many reusable surfaces instead of one-off screens.
- Theme tokens, shared panel shells, session tree items, terminal tab strip, transfer rows, and tunnel cards should evolve independently from the application bootstrap.
- Runtime theme changes become simpler when token dictionaries and custom control styles live in a dedicated assembly.

## Terminal Design Notes

The terminal area should be treated as a subsystem, not a single control.

Suggested internal layers:

```text
SSH Stream Adapter
  -> ANSI Parser
    -> Buffer / Scrollback Model
      -> Semantic Highlight / Link Detection
        -> Render Snapshot
          -> Avalonia Drawing Surface
```

This split makes it easier to support:

- incremental streaming (character-by-character / line-by-line)
- ANSI escape sequences
- URL / error / warning highlighting
- selection without hijacking `Ctrl+C`
- multiline paste input
- common Linux progress bar redraw behavior

## Theme Design Notes

- Map `.pen` variables to semantic Avalonia resources.
- Support three user modes: `Dark`, `Light`, `System`.
- Keep accent color as a separate override layer.
- Use `DynamicResource` so changes are reflected without restart.

## Persistence Strategy

All persistence goes through **SonnetDB** (https://github.com/IoTSharp/SonnetDB), used as an
**embedded** multi-model database (`SonnetDB.Core`, opened via `Tsdb.Open` under
`%LocalAppData%/VelaShell/sonnetdb`). Legacy JSON files (`sessions.json`, `settings.json`,
`state.json`, `known_hosts.json`, `quick-commands.json`) are imported once on first run.

- **Document collections** (JSON documents) hold business/config data:
  `session_groups`, `session_profiles` (indexed by `$.groupId`), `app_config`
  (settings/state/sync singleton docs — `sync` holds the Gist cloud-sync config with
  the PAT encrypted via `ISecretProtector`), `known_hosts`, `ui_config`,
  `quick_commands`, `tunnels` (one doc per profile id holding its tunnel configs),
  `recordings` (session-recording metadata).
- **Time-series measurements** hold time-oriented data:
  `conn_history` (recent connections, powers the sidebar list),
  `audit_log` (security auditing) and `session_recording_chunks`
  (session recording output chunks: tag `recording_id`, fields `offset_ms` +
  Base64 `data`; point time = recording start + offset, replayed on a timeline
  by the recording player).
- SQL dialect note: `ORDER BY time` requires the `time` column to be present in
  the SELECT list; plain `DELETE FROM measurement` may be unsupported — the
  recording store falls back to a drop-and-rewrite compaction during retention
  cleanup to reclaim orphaned chunk bytes.
- Sensitive fields (passwords, key passphrases, sync tokens) are encrypted at
  rest with AES-256-GCM via `ISecretProtector` (local key file).
- Keep persistence interfaces in `Core` (`ISessionRepository`, `ISettingsService`,
  `IRecentConnectionService`, `IAuditLogService`, `IAppDataStore`,
  `ISessionRecordingStore`, `ISecretProtector`), implementations in
  `Infrastructure/Persistence` (`SonnetDb*`), all sharing one `SonnetDbEngine`
  singleton that is disposed on app exit (WAL flush).

## Migration Rule

Do not move everything at once.

Preferred order:

1. Add new assemblies.
2. Wire solution references.
3. Migrate ViewModels to `Presentation`.
4. Migrate themes and reusable controls to `Controls`.
5. Migrate storage and SSH implementations to `Infrastructure`.
6. Reduce `App` to startup and shell hosting only.
