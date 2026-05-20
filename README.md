# CityStoryMod

A Cities: Skylines 2 mod that exports rich city state to JSON snapshots, feeding the sibling [`city-storytelling`](https://github.com/williamlang/city-storytelling) project — a separate repo of grounded fiction generated from the playthrough.

This mod is the **sensor**. It runs inside CS2, queries the game's ECS for citizens, companies, districts, buildings, and trade flows, and writes snapshots to disk. An external agent reads those snapshots and turns them into characters, events, and stories that drive subsequent gameplay.

```
[CS2 playthrough]
      │  (mod queries ECS)
      ▼
[this mod] ──── snapshot-<unix-ts>.json ────► [storytelling agent]
                                                       │ (ingests, updates canon)
                                                       ▼
                                                 [next session's story-driven choices]
```

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
- **Auto-export** — fires every N minutes (configurable; default 5, set to 0 to disable)

Both triggers respect the **Export Enabled** toggle in Options → CityStoryMod.

Snapshots land at:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityStoryMod\snapshot-<unix-ts>.json
```

Logs at:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\Logs\CityStoryMod.log
```

## Snapshot schema

See [`docs/snapshot-schema.md`](docs/snapshot-schema.md) for the v0.1 contract. The shape is finalized; fields are being filled in iteratively — most are still `null` / `[]` placeholders today.

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
| `Settings.cs` | Options UI bindings (`ExportEnabled`, `IntervalMinutes`). |
| `Locale.cs` | en-US strings for the Options sidebar. |
| `Systems/ExportSystem.cs` | Tick loop, hotkey, ECS queries, JSON writer. |
| `Properties/PublishConfiguration.xml` | Manifest CS2 reads to recognize the mod. |
| `docs/snapshot-schema.md` | Snapshot JSON contract. |

## Status

Working scaffold with a growing schema. Continuing to fill in fields field-by-field; see CLAUDE.md's "Next-up tasks" for the queue.

## Reference

See [`CLAUDE.md`](CLAUDE.md) for the full orientation: toolchain setup, reference mods worth studying (Carto, InfoLoom, SceneExplorer), known gotchas, and useful links.

## License

[MIT](LICENSE) — covers both the C# mod code and the storytelling content under `template/`. Fork, modify, redistribute, sell — just keep the copyright notice.
