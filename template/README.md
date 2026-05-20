# City Storytelling

A living narrative companion for a [Cities: Skylines 2](https://www.paradoxinteractive.com/games/cities-skylines-ii/) playthrough. The story shapes the city; the city shapes the story.

Inspired by [City Planner Plays](https://www.youtube.com/@CityPlannerPlays) — named characters with agendas, plausible motives, and second-order consequences drive what gets built.

## The idea

Causality runs **story → gameplay**. A new mayor wins on a pro-sports platform, so the player builds a stadium. A developer pulls strings at city hall, so a suburb gets zoned. A buried safety report eventually surfaces, so an industrial district gets condemned.

The player is an invisible god-hand with no in-world avatar. Every major in-game decision — a new neighborhood, a transit line, a scandal-driven referendum — is motivated by someone or something inside the fiction tracked here.

Tone is grounded realism: present-day North America, mixed records, real friction. Closer to *The Wire* or long-form newspaper reporting than soap opera.

## How it works

The repo is driven by [Claude Code](https://claude.com/claude-code). Four project slash commands handle the loop:

- **`/new-city`** — bootstrap a playthrough from a map screenshot; create the `city/<slug>` branch.
- **`/session-start`** — open a play session with a state scan.
- **`/story-driven`** — generate 3–5 grounded in-game choices, with characters for and against each.
- **`/session-end`** — record what happened in-game, propagate consequences, commit.

`main` holds only the scaffold (conventions, templates, commands). Each playthrough lives on its own `city/<slug>` branch with all of its canon, characters, companies, places, factions, events, sessions, stories, and secrets.

## Read more

[`CLAUDE.md`](./CLAUDE.md) is the full spec — directory layout, file conventions, how arcs and secrets work, the style guide.
