# CityStoryMod

A Cities: Skylines 2 mod that turns a playthrough into a living narrative. The mod has three halves running together: a C# **sensor** that exports rich ECS state to JSON snapshots, a per-city **storytelling workspace** the agent reasons against, and an **in-game React panel** for talking to the storyteller without alt-tabbing.

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

- **Toolbar icon (top-left)** — opens the in-game storyteller panel: chat with the model, pick a slash command from a dropdown, browse the city's canon in a sidebar, click any canon entry to open it in a draggable modal with rendered markdown. This is the primary surface.
- **Ctrl+Shift+E** — trigger a snapshot export immediately.
- **Ctrl+Shift+S** — run the default storyteller command (legacy hotkey; the panel is more flexible).
- **Auto-export** — fires every N minutes (configurable; default 5, set to 0 to disable).
- **Auto session-start on save load** — opt-in; when enabled, writes an open `sessions/` stub the moment a save loads so the agent picks up where the player left off.

Snapshots and per-city content land at:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityStoryMod\<city-slug>\
```

Logs at:

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\Logs\CityStoryMod.log
```

## Snapshot schema

[`docs/snapshot-schema.md`](docs/snapshot-schema.md) is the v0.1 contract; shape finalized, fields filled in iteratively. [`docs/snapshot-wishlist.md`](docs/snapshot-wishlist.md) is the storyteller's voice — what to add next, ranked by narrative leverage.

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
| `docs/snapshot-wishlist.md` | Developer planning — fields ranked by narrative leverage. |
| `Properties/PublishConfiguration.xml` | Manifest CS2 reads to recognize the mod. |

## Status

Working end-to-end: snapshots export, the agent runs from inside CS2 (no separate CLI required if using API providers), the in-game panel handles free-form prompts and slash commands, the canon browser shows the city's story files live. Template improvements migrate forward automatically without clobbering player edits.

Open issues track the next chunks: per-save branching canon (#12), out-of-canon refusal hardening (#15), optional Carto spatial-data integration (#17). The snapshot schema continues to fill in field-by-field — see [`docs/snapshot-wishlist.md`](docs/snapshot-wishlist.md) for priority order.

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

## License

[MIT](LICENSE) — covers the C# mod code, the React UI, and the storytelling content under `template/`. Fork, modify, redistribute, sell — just keep the copyright notice.
