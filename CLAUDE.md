# Ghostwriter (Cities: Skylines 2 — narrative mod, codename CityStoryMod)

A CS2 mod that turns a playthrough into a living narrative. It exports rich ECS state from the game and ships a per-city Claude-driven storytelling system inside the same repo. Both halves run together: the mod is the sensor *and* the workspace.

**On naming:** the user-facing display name is **Ghostwriter**. The technical identity (namespace, DLL, `ModsData/CityStoryMod/`) stays `CityStoryMod` — renaming those would break existing player data. So in code you'll see `CityStoryMod.*` everywhere; in the in-game UI, manifest, and any string a player reads, it's "Ghostwriter."

## How this fits together

Two halves, one repo:

- **Mod code** (repo root) — C# / Unity DOTS. Reads game state, writes JSON snapshots, scaffolds the per-city storytelling workspace, and runs Anthropic-API tool-using agents against it.
- **Storytelling template** (`template/`) — CLAUDE.md, `.claude/commands/`, canon templates, conventions. The mod copies this tree into each new city's data folder.

Data flow:

```
[CS2 playthrough]
      │  (ExportSystem queries ECS;
      │   CartoBridge calls peer mod for spatial data)
      ▼
[ModsData/CityStoryMod/<city-slug>/]
   ├── snapshots/      ← snapshot-<ts>.json — city stats, demographics, diff,
   │                     and a `map.*` block (name, theme, latitude, climate)
   ├── carto/          ← spatial geography, refreshed on demand
   │   ├── GeoJSON/    ← raw Carto output (districts, buildings, roads, map tiles)
   │   ├── GeoTIFF/    ← raw rasters (Elevation.tif, Depth.tif)
   │   └── processed/  ← storyteller-facing markdown chunks (what the agent reads)
   │       ├── index.md
   │       ├── elevation.md  ← terrain reading + stdev/relief/quadrants
   │       ├── water.md      ← water reading + coastline length + complexity
   │       ├── roads.md
   │       └── districts/<slug>.md
   ├── canon/          ← founding facts, premise (agent infers from spatial data)
   ├── characters/     ← people the agent has invented or the player named
   ├── companies/      ← businesses, employers
   ├── places/         ← neighborhoods, landmarks
   ├── factions/       ← teams, parties, unions
   ├── events/         ← in-world timeline
   ├── sessions/       ← real-world playthrough log (the "pid")
   ├── stories/        ← longer narrative pieces
   ├── secrets/        ← hidden facts driving the story
   ├── CLAUDE.md       ← scaffolded from template/, drives the agent
   └── settings.json   ← per-city config
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

The ghostwriter agent (running inside each city folder via `StorytellerDispatcher`) consumes these and synthesizes characters, companies, events, and stories into the same folder.

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

Two surfaces, both file-based. The agent's working directory is the city folder; the mod and the agent communicate purely through files there.

### Surface 1 — JSON snapshots

`snapshots/snapshot-<unix-ts>.json`, one per export. Full state (not deltas — the agent diffs successive files). Schema contract lives in [`docs/snapshot-schema.md`](docs/snapshot-schema.md); currently **v0.3**. Carries:

- **`map.*`** — world identity: `name`, `theme`, `latitude`, `longitude`, `temperature_min_c`/`max_c`, `cloudiness`, `precipitation`, `ground_water_availability`, `surface_water_availability`. Pulled reflectively from `Game.UI.MapMetadataSystem`. Drives the storyteller's founding-prompt sense of climate and region.
- **`city.*`** — population, money, happiness, health, tourists, attractiveness, danger, milestone, XP, zone counts by type.
- **`outside_connections[]`, `water_sources[]`** — named entities Carto doesn't surface.
- **`district_zones`** — per-district building-type counts (subdivision-growth signal).
- **`demographics`** — citizen flag counts, average wellbeing/health, employed count.
- **`diff`** — change-since-last-snapshot block: zone deltas, district zone deltas, named-building churn (added/removed/renamed), outside-connection and water-source diffs, in-game days elapsed.

### Surface 2 — Carto spatial chunks

`carto/processed/` markdown produced by `CartoProcessor` from Carto's raw GeoJSON + GeoTIFF output. The agent reads these chunks, not the raw files:

- **`index.md`** — city spatial index. Map footprint (size in km), districts table, adjacency graph, road network summary, terrain + water teaser lines, named-buildings + decoration list (deduped with `× N` counts).
- **`elevation.md`** — terrain reading driven by stdev/relief: *Mostly flat / Gently rolling / Hilly / Rugged*, with a "localized high point" suffix when an outlier peak sits on an otherwise flat map. Includes range, mean, stdev, highest/lowest quadrant.
- **`water.md`** — water reading driven by coverage + complexity + distribution: archipelago vs. river system vs. major lake vs. open sea, with a named dominant quadrant when one stands out. Includes coastline cells, approximate shoreline length (km), shoreline complexity index (~1 = round basin, > 4 = fragmented), and per-quadrant coastline distribution.
- **`roads.md`** — named roads/bridges with combined length and per-segment breakdown.
- **`districts/<slug>.md`** — per-district detail: centroid, bounding box, area, neighbors with compass directions, Carto's resident/employee counts, named buildings inside.

The raw `GeoJSON/` and `GeoTIFF/` files stay on disk as the source of truth, but the agent ignores them — the processed chunks are the contract.

### First-Carto-on-new-city auto-trigger

When `ExportSystem` detects a new city (no `<cityDir>/carto/` directory yet) on a save-load edge, it auto-fires `RequestCartoExport` so the storyteller has spatial context the first time the player opens Claude Code against the dir. Without this the agent's first run would see only the snapshot's empty city stats. See `ExportSystem.Export` for the latch.

## Testing

CS2 mod code is glued to `Unity.Entities` and `Game.dll` types that aren't easily mockable, and there's no headless ECS world. So tests live in a sibling `CityStoryMod.Tests` project that targets net48 and **does not reference Game.dll** — it covers only the pure-C# subset.

Test what we can without mocking Unity:
- String/text helpers (`TextUtils.Slugify`, frontmatter parsing)
- Subprocess helpers (`ClaudeCliRunner.ResolveClaudeExe` with mockable PATH)
- JSON request-body shape for LLM providers (assert against fixture JSON)
- Pure value types (`RunResult`, etc.)

Don't try to test Unity-coupled code (`ExportSystem.Export`, anything `EntityManager`-bound, save-load detection, ECS query construction) — the mock infrastructure is more code than the SUT and breaks on every CS2 patch. In-game verification (edit → quit CS2 → build → relaunch → load save) carries that load.

If a useful helper sits inside a `GameSystemBase` subclass, extract it to its own file (e.g. `TextUtils.cs`) so the test project can `<Compile Link>` it without dragging Unity in. Run tests with `dotnet test` from the repo root.

## Status

Working end-to-end. Schema **v0.3**. Mod and agent together produce a city's founding context on the first export of a fresh save.

What's wired up:
- Project structure: `CityStoryMod.csproj`, `Mod.cs` (IMod), `Settings.cs`, `Properties/PublishConfiguration.xml`, `Systems/ExportSystem.cs`. Builds via the Paradox toolchain; auto-deploys to the local Mods folder.
- Mod registers a Settings sidebar entry in CS2 Options with localized labels (en-US). Settings: `ExportEnabled` toggle, `IntervalMinutes` slider, `AutoSessionStartOnSaveLoad`.
- `ExportSystem : GameSystemBase` registered into `SystemUpdatePhase.UIUpdate` (ticks under pause). Three export triggers: **Ctrl+Shift+E** hotkey, wall-clock interval, and save-load edge.
- **Snapshot v0.3** — emits `map.*`, `city.*`, `demographics`, `district_zones`, `outside_connections`, `water_sources`, and a full `diff` block. See [`docs/snapshot-schema.md`](docs/snapshot-schema.md).
- **Carto peer-mod integration** via `Storyteller/CartoBridge.cs` (reflective, no compile-time dependency on Carto.dll). Requests `Area + Building + Network + Raster` systems → `District + Building + MapTile + Road` features, plus `Elevation + Depth` GeoTIFFs.
- **`CartoProcessor`** turns the raw GeoJSON + GeoTIFF output into storyteller-facing markdown chunks (see "Output format" above). Includes a minimal Int16 TIFF reader (`Storyteller/GeoTiffReader.cs`), stdev-based terrain classifier, coastline extractor with complexity ratio, and per-quadrant water/coast distribution analysis.
- **First-Carto-on-new-city auto-trigger.** New city detected via `!Directory.Exists("<dir>/carto")` on the save-load edge → `RequestCartoExport()` queued before the agent ever opens the folder.
- **Founding flow on the agent side** — `template/.claude/commands/new-city.md` reads spatial data first (snapshot.map.* + carto/processed/*) as the primary anchor, and reads `carto/processed/map.png` (the mod-generated combined topographic image — a raster the agent can actually see, replacing the former multi-MB SVG that came back as XML text) as its secondary visual signal. No player-supplied screenshot needed. Premise inference in `template/CLAUDE.md` lists spatial signals as priority-2 inputs.
- **Storyteller window UI** with map/canon/refresh-map buttons (`Systems/PromptUISystem.cs` + `UI/src/mods/storyteller/`).
- **`clock.json` heartbeat + instance pointers.** `ExportSystem.WriteClockFile` rewrites `<cityDir>/clock.json` every ~10 s with the live in-world date *and* the two paths the agent would otherwise glob for — `latest_snapshot`, `open_session` (null when none is open) — plus `bootstrapped`. Resolvers live in `Storyteller/CityPointers.cs` (pure C#, unit-tested); the template docs point every opener at them so a cold `claude -p` response costs ~2 batched round-trips instead of ~6 sequential ones. Not part of the snapshot schema — no version bump.

Known caveats / open questions:
- **Raw `Citizen` count ≠ HUD population.** The `Citizen` ECS component is broader: includes tourists, commuters, and transient/spawning entities. For a sensor mod this is more useful than the HUD number, but it surprises people who compare. Will refine to break down resident vs. tourist vs. commuter when expanding the schema.
- **Localization is en-US only** and lives in code (`Locale.cs`) rather than embedded JSON. Fine for one language; if we add more, switch to embedded `Locale/*.json` like Carto.
- **Carto's elevation TIFF doesn't render visibly in basic image viewers** (data sits in a narrow band of Int16 with no NoData mask, so naive viewers see all-black). Use QGIS/GIMP/IrfanView for visual inspection. Data is correct; only display is affected.
- **Carto exports map-generated default roads** alongside player-laid ones in `Network_Centerline.json`. Even t=0 cities show ~270+ road segments. The storyteller shouldn't treat every named road as player intent — highways especially are almost always map-generated.

## Next-up tasks

1. **`companies[]` standalone array** — name, sector, headcount, district. Today only surfaced via renamed buildings; the storyteller can't see the full employer roster except through whichever citizens happen to roll into `citizens_sample`.
2. **Citizen wealth tier** — completes `citizens_sample`. Needs `CitizenHappinessParameterData` singleton + household `Resources` buffer join.
3. **Service coverage gaps** — depends on building service-area data; advanced. Drives the "where political pressure originates" storytelling.
4. **Classifier tuning as more maps surface** — the terrain and water classifiers are calibrated against Lakeland (boreal lake district), Archipelago (heavy archipelago), and Verdant Vale (mid-water valley). Edge cases (Sunbelt desert, alpine, dense coastal) will need threshold tweaks.

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

When opening a fresh Claude Code session in this repo, point it at this file. It contains the orientation needed to pick up cold: stack, conventions, reference mods, gotchas, first tasks. The agent-side playbook for the storytelling content lives at [`template/CLAUDE.md`](template/CLAUDE.md) — that's what gets scaffolded into each city folder and drives the in-game agent.
