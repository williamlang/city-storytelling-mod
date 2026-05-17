# City Mod (Cities: Skylines 2 — Data Export)

A CS2 mod that exports rich city state to JSON, feeding the sibling **city-storytelling project** (a separate repo of grounded fiction generated from the playthrough).

This mod is the **sensor**. It runs inside CS2, queries the game's ECS for citizens, companies, districts, buildings, and trade flows, and writes snapshots to disk. An external agent reads those snapshots and turns them into characters, events, and stories that drive subsequent gameplay.

## How this fits together

Two repos:

- **`city-storytelling`** (sibling) — the narrative. Markdown files: canon, characters, companies, places, factions, events, sessions, stories. The story drives in-game decisions. Remote: `github.com/williamlang/city-storytelling`.
- **`<this repo>`** — the mod. C# / Unity DOTS. Reads game state, writes JSON.

Data flow:

```
[CS2 playthrough]
      │  (mod queries ECS)
      ▼
[this mod] ──── snapshot-YYYY-MM-DD.json ────► [storytelling agent]
                                                       │ (ingests, updates canon)
                                                       ▼
                                                 [next session's story-driven choices]
```

The story flows player → city; the mod flows city → story. Together they close the loop.

## Stack

- **Language:** C#
- **Runtime (inside CS2):** Unity 2022.3.7f1 with Mono. Target framework is set by `Mod.props` from the Paradox toolchain — **do not override it** in csproj.
- **Build SDK:** .NET 8 SDK (per Paradox toolchain recommendation)
- **IDE:** Visual Studio 2022 Community (free) or newer. Rider also works.
- **ECS:** Unity DOTS — Entities, Burst, Collections, Mathematics packages.

## Toolchain setup (Windows-only)

The CS2 Modding Toolchain is the foundation. It's installed from **inside CS2**:

1. Launch CS2 → **Options → Mods → enable modding tools.** Big download (Unity Editor, SDKs, MSBuild integration, Mod.props/targets). Takes a while.
2. After install, in PowerShell: `echo $env:CSII_TOOLPATH` should print a path. That folder contains `Mod.props` and `Mod.targets`, which every CS2 mod imports.
3. Visual Studio gets a new project template: **File → New → CS2 Mod**.

Build output is auto-copied to:
```
%LOCALAPPDATA%\Colossal Order\Cities Skylines II\Mods\<ModName>\<ModName>.dll
```

Logs land at:
```
%LOCALAPPDATA%\Colossal Order\Cities Skylines II\Logs\<ModName>.log
```

## Reference mods to study before writing code

| Repo | Why |
|---|---|
| [taipei-native/Carto](https://github.com/taipei-native/Carto) | **Closest analog.** Exports spatial data (buildings, districts, zones, roads) to GeoJSON/Shapefile/GeoTIFF. Study its `Carto.csproj`, `Mod.cs`, and `Systems/`. |
| [Infixo/CS2-InfoLoom](https://github.com/Infixo/CS2-InfoLoom) | Demographics / population extraction — overlaps heavily with what we want. |
| [krzychu124/SceneExplorer](https://github.com/krzychu124/SceneExplorer) | Runtime ECS browser. Useful for *discovering* what components exist on a given entity before writing queries. |
| VehicleCounter ([Thunderstore source](https://thunderstore.io/c/cities-skylines-ii/p/CaptainOfCoit/VehicleCounter/source/)) | Minimal "hello world" — one EntityQuery, no UI. Read first if just starting. |
| [River-Mochi/CS2-Templates](https://github.com/River-Mochi/CS2-Templates) | Maintained starter template with QuickStart.md. Useful for project layout and the standard Options UI pattern. |

## What we want to export (data targets, rough priority)

1. **Demographics** — population, age distribution, education, wealth tiers, happiness, immigration/emigration
2. **Districts** — name, area, density, dominant zone type, growth trend
3. **Companies** — name, sector, headcount, profitability, imports/exports
4. **Citizens (sampled)** — named individuals with jobs, homes, families. CS2 already simulates a "Lifepath" for Chirper; tap that for character seed material.
5. **Trade flows** — per-resource imports/exports, partner ratios
6. **Service coverage** — gaps in police, fire, healthcare, education (where political pressure originates)
7. **Recent construction** — new buildings since last snapshot, especially services/landmarks

The storytelling project consumes these and synthesizes characters, companies, events, and stories.

## How to read game state (high-level)

CS2 uses Unity DOTS / ECS. Standard pattern:

1. Subclass `Game.GameSystemBase`.
2. In `OnCreate`, build an `EntityQuery` using `GetEntityQuery(ComponentType.ReadOnly<Whatever>(), ...)`.
3. In `OnUpdate`, call `query.ToComponentDataArray<T>(Allocator.Temp)` to get a NativeArray, iterate.
4. **Throttle.** Do not query every frame. Tie to a sim-tick cadence (e.g. once per in-game day) or a hotkey.

Key game namespaces:

- `Game.Citizens` — citizens, lifepath, jobs
- `Game.Companies` — businesses, employment, production
- `Game.Areas` — districts, zoning
- `Game.Buildings` — all structures
- `Game.Economy` — production, trade resources
- `Game.Common.TimeData` — sim time, useful for cadence

**The wiki is incomplete.** Plan to decompile `Game.dll` (in `<CS2 install>\Cities2_Data\Managed\`) with ILSpy to find exact component names and shapes. Every serious CS2 modder does this.

## Output format

JSON, one snapshot per export. Default path:

```
%LOCALAPPDATA%\Colossal Order\Cities Skylines II\ModsData\CityStoryExport\snapshot-<in-game-date>-<unix-ts>.json
```

Alternative: write directly into a clone of the storytelling repo's `imports/` folder if both repos live side by side.

**Schema is TBD** — design alongside the storytelling repo's ingestion side. Default: one file per snapshot, top-level keys for each entity type (citizens, companies, districts, etc.). The storytelling agent diffs snapshots to detect changes between sessions.

## Status

Brand new. No code yet. Project setup is the first task.

## First-session tasks (suggested order)

1. **Verify toolchain.** `echo $env:CSII_TOOLPATH` prints a path. `Mod.props` and `Mod.targets` exist there.
2. **Hello mod.** VS 2022 → New Project → CS2 Mod template. Build. Confirm DLL lands in the mods folder and shows up in CS2 → Options → Mods.
3. **Clone Carto.** `git clone https://github.com/taipei-native/Carto` somewhere local. Skim its csproj, `Mod.cs`, and `Systems/`.
4. **Sketch the snapshot schema.** Coordinate with the storytelling project (sibling repo). Define what fields, what cadence, what file naming.
5. **Smallest possible export.** One ECS query for `Game.Citizens.Citizen`, count them, write `{"population": N}` to disk. Confirm the file appears.
6. **Expand iteratively** — add named entities (citizen names, company names, district names), then per-entity detail.

## Gotchas

- **Unity version is locked at 2022.3.7f1.** Do not install newer. The toolchain matches this version exactly.
- **Don't query ECS on the main thread every frame.** Throttle to in-game-day cadence or behind a hotkey. `ToComponentDataArray` with `Allocator.Temp` is fine for occasional dumps, pathological per-frame.
- **Patches break mods.** Mods that hook internals (Harmony patches) break frequently. Mods that just query well-known components survive better. Favor the official "register your own system" pattern over patching.
- **Mod manifest matters.** The csproj plus `Properties\PublishConfiguration.xml` is what makes CS2 recognize the mod. The toolchain template handles this — don't fight it.
- **Author identity in commits.** If keeping the GitHub author out of history matters for sharing, set a per-repo override before the first commit: `git config user.name "..."` and `git config user.email "..."`.
- **Save-hook API is not cleanly documented.** Most mods just throttle on sim ticks rather than literally tying to the save event. Confirm by reading Carto's pattern.
- **Trade flows aren't a single component.** They're assembled from `Game.Economy` components + building connections. Will need digging.

## Useful links

- [Cities Skylines Modding Discord](https://discord.gg/HTav7ARPs2) — the real community hub. Plug in here for tribal knowledge the wiki lacks.
- [CS2 Modding Wiki](https://cs2.paradoxwikis.com/Modding) — official, incomplete
- [Modding Toolchain page](https://cs2.paradoxwikis.com/Modding_Toolchain) — install steps
- [Dev Diary #3 (Code Modding)](https://www.paradoxinteractive.com/games/cities-skylines-ii/modding/dev-diary-3-code-modding) — official philosophy
- [ECS pattern guide](https://cs2.paradoxwikis.com/ECS_-_Entity_Component_System)
- [Common ECS Components](https://cs2.paradoxwikis.com/Common_ECS_Components) — WIP wiki page
- [ps1ke API reference (generated)](https://ps1ke.github.io/Cities-Skylines-2-Modding-Guide/) — class index for `Game.dll`
- [CitiesSkylinesModding GitHub org](https://github.com/CitiesSkylinesModding)

## How to use this file

When opening a fresh Claude Code session in this repo, point it at this file. It contains the orientation needed to pick up cold: stack, conventions, reference mods, gotchas, first tasks. The sibling storytelling project has its own CLAUDE.md describing the canon side.

A good first prompt:
> Read CLAUDE.md, then help me set up the dev environment and build the smallest possible "hello mod" using the official Paradox toolchain template. Reference Carto for project structure.
