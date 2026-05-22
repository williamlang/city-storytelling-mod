# CityStoryMod

A Cities: Skylines 2 mod that turns a playthrough into a living narrative. The mod exports rich ECS state to JSON snapshots **and** ships a Claude-driven storytelling system inside the same repo. Both halves run together: the mod is the sensor *and* the workspace.

```
[CS2 playthrough]
      │  (ExportSystem queries ECS)
      ▼
[ModsData/CityStoryMod/<city-slug>/]
   ├── snapshots/  ← snapshot-<ts>.json
   ├── canon/, characters/, companies/, places/, factions/, events/, sessions/, stories/, secrets/
   ├── CLAUDE.md           (scaffolded from template/)
   └── .claude/commands/   (scaffolded from template/.claude/commands/)
```

The mod scaffolds each new city's folder by copying the `template/` tree into it on first export. The agent then runs against that folder via the in-mod `StorytellerDispatcher` (raw Anthropic API in C# — no separate Claude Code CLI required).

## Stack

- **Language:** C#
- **Runtime:** Unity 2022.3.7f1 with Mono (locked by the CS2 toolchain)
- **ECS:** Unity DOTS — Entities, Burst, Collections, Mathematics
- **Build SDK:** .NET 8 or 9. Trust whatever the CS2 Modding Toolchain installer accepts.

## Building

Requires the CS2 Modding Toolchain (Windows). Install it from inside CS2: **Options → Modding → enable modding tools**. After install, open a fresh PowerShell — `$env:CSII_TOOLPATH` should resolve.

Then:

```sh
dotnet build
```

Build output is auto-deployed to:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\Mods\CityStoryMod\
```

Note: that's `LocalLow`, not `Local` — the wiki and many tutorials get this wrong.

No hot-reload. The dev loop is **edit → quit CS2 → `dotnet build` → relaunch → load save**. Quicksave (F5) before quitting and quickload (F9) after launching makes it bearable.

## Usage

In CS2, with the mod enabled:

- **Ctrl+Shift+E** — trigger an export immediately
- **Ctrl+Shift+S** — run the storyteller agent against the city folder
- **Auto-export** — fires every N minutes (configurable; default 5, set to 0 to disable)
- **Auto session-start on save load** — opt-in; when enabled, the mod writes an open `sessions/` stub the moment a save loads so the agent picks up where the player left off

All export triggers respect the **Export Enabled** toggle in Options → CityStoryMod.

Snapshots and the rest of the city's content land at:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityStoryMod\<city-slug>\
```

Logs at:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\Logs\CityStoryMod.log
```

## Snapshot schema

See [`docs/snapshot-schema.md`](docs/snapshot-schema.md) for the v0.1 contract. The shape is finalized; fields are being filled in iteratively.

Currently populated:
- Metadata header (timestamp, schema version)
- `city.citizens_total` (raw `Citizen` ECS count — includes tourists/commuters, so will differ from the HUD population)
- Per-district population, jobs, zones, named buildings
- Citizen demographics aggregated from `Citizen` + `Worker`
- Roads, outside connections, water sources captured from `CustomName` entities
- Buildings churn + zone deltas + in-game days elapsed (diff section)

## Project layout

| Path | Purpose |
|---|---|
| `Mod.cs` | `IMod` entry point. Registers `ExportSystem` and settings. |
| `Settings.cs` | Options UI bindings. |
| `Locale.cs` | en-US strings for the Options sidebar. |
| `Systems/ExportSystem.cs` | Tick loop, hotkey, ECS queries, JSON writer, city-folder scaffolder. |
| `Storyteller/` | In-mod LLM agent (raw Anthropic API + tool use). |
| `template/` | Storytelling workspace template — copied into each city's folder on first export. |
| `Properties/PublishConfiguration.xml` | Manifest CS2 reads to recognize the mod. |
| `docs/snapshot-schema.md` | Snapshot JSON contract. |

## Status

Working scaffold with a growing schema. Continuing to fill in fields field-by-field; see CLAUDE.md's "Next-up tasks" for the queue.

## Reference

See [`CLAUDE.md`](CLAUDE.md) for the full orientation: toolchain setup, reference mods worth studying (Carto, InfoLoom, SceneExplorer), known gotchas, and useful links. See [`template/CLAUDE.md`](template/CLAUDE.md) for the storytelling agent's playbook.

## License

[MIT](LICENSE) — covers both the C# mod code and the storytelling content under `template/`. Fork, modify, redistribute, sell — just keep the copyright notice.
