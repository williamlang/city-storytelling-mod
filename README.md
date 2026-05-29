# Ghostwriter

A Cities: Skylines 2 mod that turns a playthrough into a living narrative. As you build, an LLM-driven agent quietly writes the story of your city in the background — named characters who plausibly drive its decisions, civic events as they happen, ongoing narrative threads. You stay the city's builder; the ghostwriter writes in your voice.

The mod has three halves running together: a C# **sensor** that exports rich ECS state to JSON snapshots, a per-city **storytelling workspace** the agent reasons against, and an **in-game React panel** for talking to the ghostwriter without alt-tabbing.

```
[CS2 playthrough]
      │  (ExportSystem queries ECS, snapshot per export)
      ▼
[ModsData/CityStoryMod/<city-slug>/]
   ├── snapshots/         ← snapshot-<ts>.json
   ├── canon/, characters/, companies/, places/, factions/,
   │   events/, sessions/, stories/, secrets/
   ├── CLAUDE.md          ← scaffolded from template/, kept in sync
   ├── .claude/commands/  ← slash commands the storyteller runs
   ├── .template-manifest.json  ← sync state (hashes of mod-written files)
   └── settings.json      ← per-city config
                          ↑
                  ┌───────┴────────────────────────────────┐
                  │   In-game prompt panel (React/CS2 UI)  │
                  │   Toolbar icon → chat, slash commands, │
                  │   canon browser, file modals           │
                  └────────────────────────────────────────┘
                  ↑
       [StorytellerDispatcher in C# — Anthropic API
        or `claude -p` CLI, configurable per provider]
```

Each new city's folder is scaffolded from the embedded `template/` tree on first export. **Subsequent exports re-sync**: files the mod wrote that the player hasn't touched migrate forward automatically when the template evolves; files the player has edited are left alone (see `TemplateScaffolder` for the per-file decision tree).

## Quick start (players)

> If you're here to use Ghostwriter, not hack on it — this section is the whole orientation. The rest of this document is dev-facing.

**What you'll need:** Cities: Skylines II, the modding tools enabled in CS2 (Options → Modding), and a way for the mod to talk to a language model. You have several options for that last part — pick whichever fits your wallet and setup:

| Provider | What it costs | How it works |
|---|---|---|
| **Anthropic API** (default) | Pay-per-use, billed to your Anthropic key (~$0.05 – $0.50 per typical session at Opus prices; less with Sonnet) | Paste an API key from [console.anthropic.com](https://console.anthropic.com) into Options |
| **Claude Code CLI** | Uses your existing Claude Code subscription — flat-rate, no per-session bill | Install [Claude Code](https://claude.com/claude-code), log in once with `claude /login`, select "AnthropicCLI" in Options |
| **OpenAI** | Pay-per-use, OpenAI pricing | Paste an OpenAI key |
| **Gemini** | Pay-per-use, Google pricing | Paste a Gemini key |
| **Ollama** (local) | Free, runs on your machine | Install [Ollama](https://ollama.com), pull a model (e.g. `ollama pull llama3.3`), point Options at `http://localhost:11434` |

Default in Options is **Anthropic API with Claude Opus** — the most expressive model, but also the priciest. If you want lower bills, switch the Model field to `claude-sonnet-4-6` (about 1/5 the cost, still excellent for narrative).

**Install:**
1. Subscribe to Ghostwriter in Paradox Mods. Carto (a peer dependency for spatial map data) auto-installs alongside.
2. Launch CS2 and enable Ghostwriter in the mods list. **Restart CS2** so both mods load fresh.
3. Open **Options → Ghostwriter**, pick your provider, paste your key or set up the CLI as above, and confirm a model id (the default `claude-opus-4-7` is fine for Anthropic; pick something installed locally for Ollama).

**First run:** start a new city and play for a minute. The mod auto-exports a snapshot of the city's state every 5 minutes (configurable in Options) — and on first save load, it also captures the map's spatial geometry via Carto. When you're ready to meet your ghostwriter:

1. Click the **Ghostwriter toolbar icon** (top-left of the in-game UI).
2. Pick the **/new-city** slash command from the dropdown. The agent reads what's been exported so far, asks you a short question about the kind of story you want to tell, and writes the city's founding canon — premise, era, named characters who could plausibly drive its decisions.
3. After that, just chat. Ask "who lives in The North Yards?" or "what just happened in Pine Quarter?" — the agent will read its own canon and answer. Use **/session-start** before a play session and **/session-end** after to keep a real-world playthrough log.

**Where the stories live:**

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityStoryMod\<your-city>\
```

(`LocalLow`, not `Local` — Windows is confusing here.)

There's an **Open story folder** button in Options if you'd rather not paste paths. Stories are plain Markdown — you can read, edit, or back them up freely.

**Cost-aware tips:**
- **Disable auto-export** (Options → Interval (minutes) → 0) if you only want the ghostwriter running when you click a slash command. Snapshots themselves don't cost anything; LLM calls do.
- Smaller / cheaper models work for everything except the most narrative-dense commands. Sonnet is a reasonable middle ground.
- The Anthropic API console shows a usage breakdown per key, so you can watch costs in real time.

**Troubleshooting:** if the chat panel shows a red error, read it — provider errors (bad key, network down, model missing, Claude CLI not on PATH) all surface there with actionable text. If the panel is blank when it shouldn't be, check the log at `%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\Logs\CityStoryMod.log` — every export, Carto trigger, and storyteller run leaves a line there.

> **A note on what the model sees.** The ghostwriter reads your city's snapshot JSON (population, zones, building names, pollution, etc.) and any canon files inside your city's folder. It does NOT read other saves, other mods' data, or anything outside that folder. If you're using a hosted provider (Anthropic / OpenAI / Gemini), that data is sent to their servers per their terms of service. Ollama runs entirely on your machine — pick it if you want full local-only.

## Stack

- **C#** for the mod itself — Unity 2022.3.7f1 with Mono, CS2 toolchain. ECS via Unity DOTS (Entities, Burst, Collections, Mathematics).
- **React 18 + TypeScript** for the in-game UI panel — Coherent UI runtime, webpack production builds, Vite dev server for out-of-CS2 iteration.
- **.NET 8 or 9 SDK.** Trust whatever the CS2 Modding Toolchain installer accepts.
- **LLM providers** (configurable in Options): direct Anthropic API, OpenAI, Gemini, Ollama, or shelling out to a local `claude -p` (Claude Code CLI, uses your Max subscription).

## Building

Requires the CS2 Modding Toolchain (Windows). Install from inside CS2: **Options → Modding → enable modding tools**. Open a fresh PowerShell after install — `$env:CSII_TOOLPATH` should resolve.

```sh
dotnet build
```

A successful build auto-deploys both halves to:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\Mods\CityStoryMod\
```

(`LocalLow`, not `Local` — the wiki and many tutorials get this wrong.)

The build also runs `npm run build` in `UI/` as a post-target to repopulate the React bundle (the toolchain's deploy step clears the folder, so the UI half needs to be restored each time — see `RestoreUIBundle` in the csproj).

No hot-reload while CS2 is running. The C# dev loop is **edit → quit CS2 → `dotnet build` → relaunch → load save**. For React-only iteration, use the out-of-CS2 Vite harness (see Contributing).

## Usage

In CS2, with the mod enabled:

- **Toolbar icon (top-left)** — opens the in-game ghostwriter panel: chat with the model, pick a slash command from a dropdown, browse the city's canon in a sidebar, click any canon entry to open it in a draggable modal with rendered markdown. This is the primary surface.
- **Ctrl+Shift+E** — trigger a snapshot export immediately.
- **Auto-export** — fires every N minutes (configurable; default 5, set to 0 to disable).
- **Auto session-start on save load** — opt-in; when enabled, writes an open `sessions/` stub the moment a save loads so the agent picks up where the player left off.

> **Heads-up on the prompt box.** There's a soft guardrail in `template/CLAUDE.md` that tells the ghostwriter to refuse off-scope prompts in-character (so a stray "write me a python script" doesn't burn your Max subscription or corrupt the canon). It's prompt-level only; a determined prompt-injector can talk the model around it, and someone who actually wanted to torch your canon could just delete files directly. Don't leave the game unlocked in a coffee shop.

Snapshots and per-city content land at:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityStoryMod\<city-slug>\
```

Logs at:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\Logs\CityStoryMod.log
```

## Snapshot schema

[`docs/snapshot-schema.md`](docs/snapshot-schema.md) is the current contract — **v0.7**. The narrative-leverage-ranked wishlist for what to add next lives on the [snapshot fields wishlist issue](https://github.com/williamlang/city-storytelling-mod/issues/18).

Currently populated:
- Metadata header — schema version, snapshot id, session id, wall-clock + in-game timestamps
- `map.*` — name, theme, latitude / longitude, temperature range, cloudiness, precipitation, water availability (from `Game.UI.MapMetadataSystem`)
- `city.*` — population, money, happiness, health, tourists, attractiveness, danger, milestone, XP, zone counts, **`city.churn`** (births / deaths / move-ins / move-aways per day, including a `moved_away_by_reason` breakdown), **`city.social`** (homeless / unemployed / crime counts), **`city.budget`** (income + residential tax)
- **`pollution`** — air / ground / noise sampled at every building position, binned by `CurrentDistrict`; emits city-wide + per-district averages with sample counts
- **`land_value`** — same per-building → bin-by-district sampling pattern, reading `LandValueSystem`'s cell grid
- **`crime`** — per-building `Game.Buildings.CrimeProducer.m_Crime` binned by district (sample population is a subset of buildings)
- **`citizens_sample`** — up to 30 sampled residents per export; always includes every `Followed` citizen, fills the rest with a timestamp-seeded random sample. Per-entry: name, age band, education, gender, happiness, home district, workplace, school, followed/is_criminal flags
- `outside_connections`, `water_sources` — `CustomName` entities Carto doesn't surface
- `district_zones` — per-district building-type counts (backs the subdivision-growth signal)
- `demographics` — citizen flag counts, average wellbeing / health, employed count
- `diff.*` — full change-since-last-snapshot block: zone deltas, district zone deltas, **`building_churn`** (per-district demolition + construction counts), named-building churn, outside-connection / water-source diffs, in-game days elapsed

Still null / empty (next-up targets): per-citizen wealth tier, `trade.imports/exports`, `services.coverage_gaps`. Spatial geometry (district polygons, building positions, roads, terrain, water bodies) lives in `carto/processed/*.md` chunks alongside the JSON.

## Project layout

| Path | Purpose |
|---|---|
| `Mod.cs` | `IMod` entry point. Registers ECS systems and settings. |
| `Settings.cs`, `Locale.cs` | Options UI bindings + en-US strings. |
| `Systems/ExportSystem.cs` | Tick loop, hotkey, ECS queries, JSON snapshot writer. |
| `Systems/PromptUISystem.cs` | C#↔React bridge — registers ValueBindings (messages, commands, canon tree) and TriggerBindings (submit prompt, cancel run). |
| `TemplateScaffolder.cs` | Per-file sync between embedded `template/` and each city dir, manifest-driven. |
| `TextUtils.cs`, `PathUtils.cs` | Pure C# helpers (frontmatter parser, PATH lookup) — linked into the test project. |
| `Storyteller/` | In-mod LLM agent — `Conversation` base, per-provider impls (Anthropic, OpenAI, Gemini, Ollama, Claude Code CLI), `AgentLoop`, `ToolExecutor`. |
| `template/` | Storytelling workspace template — CLAUDE.md, slash commands, canon stubs, settings.sample.json. Embedded into the DLL and synced into each city. |
| `UI/` | React/TS in-game panel. `src/mods/storyteller/` per-component. `dev/` is the Vite out-of-CS2 harness. |
| `tests/CityStoryMod.Tests/` | xUnit + FluentAssertions, pure-C# tests against linked source files. |
| `docs/snapshot-schema.md` | Snapshot JSON contract. |
| `Properties/PublishConfiguration.xml` | Manifest CS2 reads to recognize the mod. |

## Status

Working end-to-end: snapshots export, the agent runs from inside CS2 (no separate CLI required if using API providers), the in-game panel handles free-form prompts and slash commands, the canon browser shows the city's story files live. Template improvements migrate forward automatically without clobbering player edits.

Next chunks tracked on the [open issues](https://github.com/williamlang/city-storytelling-mod/issues).

## Reference

[`CLAUDE.md`](CLAUDE.md) is the full developer orientation: toolchain setup, reference mods worth studying (Carto, InfoLoom, SceneExplorer), known gotchas, useful links. [`template/CLAUDE.md`](template/CLAUDE.md) is the storytelling agent's own playbook — what gets scaffolded into each city and drives the in-game agent.

## Contributing

The mod has two halves — C# (running inside CS2) and React/TypeScript (the in-game UI panel) — each with its own dev loop.

### Setup

Prereqs: the CS2 Modding Toolchain (Windows-only, install from inside CS2 → Options → Modding) plus a recent .NET SDK and Node.js 18+.

```sh
# C# side: auto-deploys to the Mods folder on build
dotnet build

# React side: install once, then watch or one-shot build
cd UI
npm install
npm run build       # one-shot; UI bundle lands next to the .dll
npm run dev         # webpack watch — rebuilds on save
```

A successful `dotnet build` runs `npm run build` at the end (`RestoreUIBundle` target in the csproj). The toolchain's `Mod.targets` wipes the deploy folder between builds and would otherwise leave the UI half stranded; the auto-restore keeps them in sync.

### Iterating on the UI without CS2

For React-only changes (layout, component logic, state), `npm run dev:web` from `UI/` launches a Vite dev server at http://localhost:5173 with mocked `cs2/*` modules. Edit a file in `UI/src/`, see the change in the browser instantly.

- Mocks lie about Coherent UI quirks (rem scaling, missing font glyphs, SVG `currentColor` non-propagation). Final visual sign-off still requires CS2.
- Seed data for the harness lives in [`UI/dev/fixtures/sample-city.ts`](UI/dev/fixtures/sample-city.ts). Edit that file to change what the panel sees on first paint (different messages, different canon tree, error state, mid-run state, etc.).
- The mocked `cs2/api` lives in [`UI/dev/mocks/cs2-api.tsx`](UI/dev/mocks/cs2-api.tsx). Add a `trigger` handler there to fake a C#-side response when iterating on user actions.

### Tests

Both halves have test suites that **don't** require CS2 to run.

**C# tests** (xUnit + FluentAssertions, in `tests/CityStoryMod.Tests/`) cover the pure-C# subset — string helpers, frontmatter parsing, PATH lookup, template sync state machine. Unity-coupled code (`ExportSystem.Export`, ECS queries) is intentionally not tested; in-game verification carries that load. See [`CLAUDE.md`](CLAUDE.md) → Testing for the philosophy.

```sh
dotnet test tests/CityStoryMod.Tests/CityStoryMod.Tests.csproj
```

**JS tests** (Vitest + React Testing Library, alongside source files) cover hook logic and component interactions against the mocked `cs2/*` bindings. Same setup as the dev harness — same mocks, same fixtures.

```sh
cd UI
npm test           # one-shot
npm run test:watch # watch mode
```

New React components go next to a `Foo.test.tsx`. Vitest auto-discovers via the `src/**/*.{test,spec}.{ts,tsx}` glob.

### Dev loop summary

- **C# edit:** quit CS2 → `dotnet build` → relaunch → load save. UI bundle auto-restored as part of the build.
- **React edit, layout/logic only:** `npm run dev:web` running in the background; edit and the browser hot-reloads.
- **React edit, ready to verify in CS2:** quit CS2 → `dotnet build` (or `npm run build` in `UI/`) → relaunch.
- **Tests:** `dotnet test` and `npm test` whenever; neither needs CS2.

## Acknowledgements

Ghostwriter learned how to talk to CS2 by reading other people's mods and decompiles. Credit where it's due:

- **[taipei-native/Carto](https://github.com/taipei-native/Carto)** — peer dependency for spatial export. Our `CartoBridge` calls into it reflectively (no compile-time reference) and `CartoProcessor` consumes its GeoJSON + GeoTIFF output. Carto's own project layout (Mod.cs, Systems/, csproj) is also the model our project structure follows.
- **[bruceyboy24804/InfoLoom](https://github.com/bruceyboy24804/InfoLoom)** — citizen / household / workplace resolution patterns (`CitizenUIUtils` helpers, `m_NameSystem.GetRenderedLabelName` for rendered names, `PropertyRenter` chain for home buildings) were learned from `InfoLoom/Systems/Sections/ILCitizenSection.cs`. InfoLoom surfaces a lot of demographic detail in-game; Ghostwriter just needed to translate the same reads into JSON.
- **[Infixo/CS2-InfoLoom](https://github.com/Infixo/CS2-InfoLoom)** — the original demographics/population extraction work that influenced our `demographics` schema fields.
- **[krzychu124/SceneExplorer](https://github.com/krzychu124/SceneExplorer)** — runtime ECS browser. Indispensable for discovering what components exist on a given entity before writing a query.
- **VehicleCounter** by CaptainOfCoit ([Thunderstore source](https://thunderstore.io/c/cities-skylines-ii/p/CaptainOfCoit/VehicleCounter/source/)) — the minimal "hello world" ECS-query mod I read first to understand the basic `GameSystemBase` lifecycle.
- **[River-Mochi/CS2-Templates](https://github.com/River-Mochi/CS2-Templates)** — the maintained starter template + QuickStart.md that fills in toolchain gaps the official wiki leaves out.

Several non-public bits of CS2 itself were read via ILSpy-style decompile dumps shared by other modders. I credit them as decompile *sources* — the code inside is Colossal Order's:

- **[bworthy89/roadmod](https://github.com/bworthy89/roadmod)** — extensive decompile of `Game.dll`. I used it to verify shapes for `LandValueSystem` / `LandValueCell`, `Game.Buildings.CrimeProducer`, `CellMapSystem<T>`, `Citizen` / `HouseholdMember` / `Worker` / `PropertyRenter`, and `CitizenUIUtils` before writing the corresponding sensor code.
- **[Jimmyokok/LandValueOverhaul](https://github.com/Jimmyokok/LandValueOverhaul)** and **[JadHajjar/HardMode](https://github.com/JadHajjar/HardMode)** — confirmed the canonical `m_LandValueSystem.GetMap(true, out _)` + `LandValueSystem.GetCellIndex(pos)` access pattern.

If you see something in this codebase that came from your mod and I missed crediting you here, please open an issue — happy to fix it.

## License

[MIT](LICENSE) — covers the C# mod code, the React UI, and the storytelling content under `template/`. Fork, modify, redistribute, sell — just keep the copyright notice.
