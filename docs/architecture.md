# PulseTerm Architecture Blueprint

## Recommended Baseline

```text
PulseTerm.App            -> Desktop entry point and composition root
PulseTerm.Presentation   -> ViewModels, navigation, orchestration
PulseTerm.Controls       -> Custom controls, styles, tokens, behaviors
PulseTerm.Terminal       -> Terminal engine, parser, renderer, interaction model
PulseTerm.Core           -> Stable domain models and contracts
PulseTerm.Infrastructure -> SSH, storage, platform integration, background work
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

## Why Split Out `PulseTerm.Controls`

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

- JSON first for settings, layout, quick commands, UI preferences.
- LiteDB second for richer local indexing/query scenarios.
- Keep persistence interfaces in `Core`, implementations in `Infrastructure`.

## Migration Rule

Do not move everything at once.

Preferred order:

1. Add new assemblies.
2. Wire solution references.
3. Migrate ViewModels to `Presentation`.
4. Migrate themes and reusable controls to `Controls`.
5. Migrate storage and SSH implementations to `Infrastructure`.
6. Reduce `App` to startup and shell hosting only.
