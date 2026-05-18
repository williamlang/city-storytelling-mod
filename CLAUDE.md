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
- **Build SDK:** .NET 8 or 9 SDK works. The CS2 toolchain's own setup screen explicitly accepted .NET 9.0.311 on this machine, despite older Paradox docs saying .NET 8. Trust whatever the toolchain installer says when it checks prerequisites.
- **IDE:** Visual Studio 2022 Community (free) is the documented path (and gets a "CS2 Mod" project template). Rider also works. **VS Code + `dotnet build` from the CLI is also fine** — there's no IDE-only step, you just have to hand-author the csproj from a reference like Carto.
- **ECS:** Unity DOTS — Entities, Burst, Collections, Mathematics packages.

## Toolchain setup (Windows-only)

The CS2 Modding Toolchain is the foundation. It's installed from **inside CS2**:

1. Launch CS2 → **Options → Modding → enable modding tools.** Big download (Unity Editor 2022.3.x, SDKs, MSBuild integration, Mod.props/targets). Takes a while.
2. **The installer asks you to accept a Unity Personal license** mid-flow — it's bundling the Unity Editor. Click through that and it continues.
3. After install, **open a fresh PowerShell window** (existing shells won't see the new env var). `$env:CSII_TOOLPATH` should print a path like `C:\Users\<you>\AppData\LocalLow\Colossal Order\Cities Skylines II\.cache\Modding`. That folder contains `Mod.props` and `Mod.targets`, which every CS2 mod imports.
4. Visual Studio gets a new project template: **File → New → CS2 Mod**. (Skip this if using VS Code/CLI.)

Build output is auto-copied to (note: **`LocalLow`**, not `Local` — the wiki and many tutorials get this wrong):
```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\Mods\<ModName>\<ModName>.dll
```

Logs land at:
```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\Logs\<ModName>.log
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
3. In `OnUpdate`, either call `query.CalculateEntityCount()` for a count, or `query.ToComponentDataArray<T>(Allocator.Temp)` to get a NativeArray and iterate.
4. **Register the system in `IMod.OnLoad`** via `updateSystem.UpdateBefore<YourSystem>(SystemUpdatePhase.UIUpdate)` (or another phase). Use `UpdateBefore` rather than `UpdateAt` — `UpdateAt` exists but did not reliably cause OnUpdate to tick in testing.
5. **Throttle.** Don't query every frame. Three options: gate on wall-clock elapsed (works under pause), on an in-game-day boundary (won't tick while paused), or behind a hotkey. We use wall-clock + hotkey.

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
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityStoryMod\snapshot-<unix-ts>.json
```

(In-game date in the filename is TODO — currently just unix timestamp.)

Alternative: write directly into a clone of the storytelling repo's `imports/` folder if both repos live side by side.

**Schema is TBD** — design alongside the storytelling repo's ingestion side. Default: one file per snapshot, top-level keys for each entity type (citizens, companies, districts, etc.). The storytelling agent diffs snapshots to detect changes between sessions.

## Status

Working scaffold with a minimum-viable population export.

What's wired up:
- Project structure: `CityStoryMod.csproj`, `Mod.cs` (IMod), `Settings.cs`, `Properties/PublishConfiguration.xml`, `Systems/ExportSystem.cs`. Builds via the Paradox toolchain; auto-deploys to the local Mods folder.
- Mod registers a Settings sidebar entry in CS2 Options with localized labels (en-US). Settings: `ExportEnabled` toggle, `IntervalMinutes` slider (0–60, where 0 disables the interval trigger).
- `ExportSystem : GameSystemBase` is registered into `SystemUpdatePhase.UIUpdate` (ticks regardless of game pause, so wall-clock + hotkey both work even when the player has paused the sim).
- Two triggers fire `Export()`: **Ctrl+Shift+E** hotkey, plus a wall-clock interval (default 5 min, configurable).
- Export writes a **v0.1 schema** snapshot to `ModsData\CityStoryMod\snapshot-<unix-ts>.json`. Schema contract lives in [`docs/snapshot-schema.md`](docs/snapshot-schema.md). The shape is finalized; most fields are intentionally `null` / `[]` placeholders and get filled in iteratively. Today `city.citizens_total` is the only populated field beyond the metadata header.

Known caveats / open questions:
- **Raw `Citizen` count ≠ HUD population.** The `Citizen` ECS component is broader: includes tourists, commuters, and transient/spawning entities. For a sensor mod this is more useful than the HUD number, but it surprises people who compare. Will refine to break down resident vs. tourist vs. commuter when expanding the schema.
- **Localization is en-US only** and lives in code (`Locale.cs`) rather than embedded JSON. Fine for one language; if we add more, switch to embedded `Locale/*.json` like Carto.

## Next-up tasks

The schema is sketched. Filling it in field-by-field, easiest first (full order in [`docs/snapshot-schema.md`](docs/snapshot-schema.md)):

1. **`city.name` / `city.money` / `city.happiness`** — cheap city-stat queries.
2. **`captured_at_ingame`** — hook `TimeSystem` for the in-game date.
3. **`districts[]`** (id, name, population) — biggest jump toward per-place storytelling.
4. **Richer demographics + `citizens_sample[]`** — needs additional citizen-state components (`HouseholdMember`, `Resident`, etc.); find via SceneExplorer or ILSpy on `Game.dll`.
5. **`companies[]`** — name, sector, headcount.
6. **Side-by-side output mode.** Settings toggle to write snapshots directly into `<storytelling-repo>/imports/` when both repos live next to each other.

## Gotchas

- **Unity version is locked at 2022.3.7f1.** Do not install newer. The toolchain matches this version exactly.
- **No hot-reload for mods.** Once CS2 (Mono) loads your DLL, Windows memory-maps it and holds an exclusive lock until the process exits. `dotnet build`'s post-deploy step will fail with `MSB3231: Access to the path '...' is denied` until you fully quit CS2. The dev loop is **edit → quit CS2 → `dotnet build` → relaunch → load save**. Quicksave (F5) before quitting and quickload (F9) after launching makes the cycle bearable.
- **`Settings.RegisterInOptionsUI()` is required for the mod to appear in the Options sidebar.** Without it, the mod loads and runs fine but is invisible in the UI. Easy to mistake for "the mod didn't load" — check `Logs/<ModName>.log` first.
- **`SystemUpdatePhase.GameSimulation` doesn't tick while the game is paused.** For input handling or wall-clock cadence, register your system at `SystemUpdatePhase.UIUpdate` instead — it keeps ticking under pause and is also the right phase for input-related work.
- **Use `updateSystem.UpdateBefore<T>(phase)` to schedule OnUpdate.** `updateSystem.UpdateAt<T>(phase)` exists but in testing did not cause `OnUpdate` to fire (system was created but never ticked). All known-working mods use `UpdateBefore`.
- **`UnityEngine.Input` (legacy) needs `UnityEngine.InputLegacyModule` referenced, not just `UnityEngine.CoreModule`.** Unity 2022+ split legacy input out of CoreModule.
- **F12 is reserved for CS2's screenshot.** Don't bind it. We use Ctrl+Shift+E for the export hotkey.
- **Don't query ECS on the main thread every frame.** Throttle to a wall-clock interval, in-game-day cadence, or behind a hotkey. `CalculateEntityCount` and `ToComponentDataArray` with `Allocator.Temp` are fine for occasional dumps, pathological per-frame.
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
