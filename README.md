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
- **In-game prompt panel** — toolbar icon (top-left) opens the storyteller panel: chat with the model, run slash commands, browse canon files in a sidebar

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

## Contributing

The mod has two halves — C# (running inside CS2) and React/TypeScript (the in-game UI panel) — and each has its own dev loop.

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

A successful `dotnet build` automatically runs `npm run build` at the end (post-build target in `CityStoryMod.csproj` — see the `RestoreUIBundle` step). Mod.targets wipes the deploy folder between builds and would otherwise nuke the webpack output; the auto-restore keeps both halves in sync.

### Iterating on the UI without CS2

For React-only changes (layout, component logic, state), `npm run dev:web` from `UI/` launches a Vite dev server at http://localhost:5173 with mocked `cs2/*` modules. Edit a file in `UI/src/`, see the change in the browser instantly. Caveats:

- Mocks lie about Coherent UI quirks (rem scaling, missing font glyphs, SVG `currentColor`). Final visual sign-off still requires CS2.
- The seed data the harness renders against is in [`UI/dev/fixtures/sample-city.ts`](UI/dev/fixtures/sample-city.ts). Edit that file to change what the panel sees on first paint (different messages, different canon tree, error state, mid-run state, etc.).
- The mocked `cs2/api` lives in [`UI/dev/mocks/cs2-api.tsx`](UI/dev/mocks/cs2-api.tsx). Add a `trigger` handler there to fake a C#-side response when iterating on user actions.

### Tests

Both halves have test suites that **don't** require CS2 to run.

**C# tests** (xUnit + FluentAssertions, in `tests/CityStoryMod.Tests/`) cover the pure-C# subset — string helpers, frontmatter parsing, PATH lookup. Unity-coupled code (`ExportSystem.Export`, ECS queries) is intentionally not tested; in-game verification carries that load. See `CLAUDE.md` → Testing for the philosophy.

```sh
dotnet test tests/CityStoryMod.Tests/CityStoryMod.Tests.csproj
```

**JS tests** (Vitest + React Testing Library, alongside source files) cover hook logic and component interactions against the mocked `cs2/*` bindings. Same setup as the dev harness — same mocks, same fixtures.

```sh
cd UI
npm test           # one-shot
npm run test:watch # watch mode
```

When adding a new React component, drop a `Foo.test.tsx` next to it. Vitest auto-discovers via the `src/**/*.{test,spec}.{ts,tsx}` glob.

### Dev loop summary

- **C# edit:** quit CS2 → `dotnet build` → relaunch CS2 → load save. UI bundle auto-restored as part of build.
- **React edit, layout/logic only:** `npm run dev:web` running in the background; edit and the browser hot-reloads.
- **React edit, ready to verify in CS2:** quit CS2 → `dotnet build` (or `npm run build` in `UI/`) → relaunch.
- **Tests:** `dotnet test` and `npm test` whenever; neither needs CS2.

## License

[MIT](LICENSE) — covers both the C# mod code and the storytelling content under `template/`. Fork, modify, redistribute, sell — just keep the copyright notice.
