# City Storytelling

A living narrative companion for a [Cities: Skylines 2](https://www.paradoxinteractive.com/games/cities-skylines-ii/) playthrough. The story shapes the city; the city shapes the story.

Inspired by [City Planner Plays](https://www.youtube.com/@CityPlannerPlays) — named characters with agendas, plausible motives, and second-order consequences drive what gets built.

## The idea

Causality runs **story → gameplay**. A new mayor wins on a pro-sports platform, so the player builds a stadium. A developer pulls strings at city hall, so a suburb gets zoned. A buried safety report eventually surfaces, so an industrial district gets condemned.

The player is an invisible god-hand with no in-world avatar. Every major in-game decision — a new neighborhood, a transit line, a scandal-driven referendum — is motivated by someone or something inside the fiction tracked here.

Tone is grounded realism: present-day North America, mixed records, real friction. Closer to *The Wire* or long-form newspaper reporting than soap opera.

## How it works

This folder is the **per-city storytelling workspace**. It gets scaffolded automatically by the [CityStoryMod](../../) the first time the mod exports a snapshot for a new city — a copy of this template lands at `%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityStoryMod\<city-slug>\` and from then on every file generated for that city lives there.

The agent drives the loop through four slash commands (in `.claude/commands/`):

- **`/new-city`** — bootstrap a playthrough from a map screenshot; write founding canon and an inferred playthrough premise.
- **`/session-start`** — open a play session: state scan + opening checklist. Writes an open session stub.
- **`/story-driven`** — generate 3–5 grounded in-game choices, with characters for and against each.
- **`/session-end`** — record what happened in-game, propagate consequences, close the session.

Sessions are the playthrough's pid: while a session file in `sessions/` lacks an `ended_real_date:`, the player is mid-arc and the next conversation should pick up where they left off (or close the prior session before opening a new one). The mod has an optional `AutoSessionStartOnSaveLoad` setting that writes the open stub the moment a save is loaded, so the agent always sees a fresh session waiting.

## Read more

[`CLAUDE.md`](./CLAUDE.md) is the full spec — directory layout, file conventions, how arcs and secrets work, the session lifecycle, the style guide.
