# City Story Project

A living narrative for a Cities: Skylines 2 playthrough. The story shapes the city; the city shapes the story.

## The premise

The player is running Cities: Skylines 2. This project generates and tracks the realistic, grounded fiction behind the gameplay — characters, companies, factions, civic events — so that every major in-game decision (new suburb, stadium, transit line, industrial zone, scandal-driven referendum) is motivated by someone or something inside the story.

Inspired by **City Planner Plays** (YouTube): named characters with agendas drive the city's evolution.

## The narrative frame

- **Setting:** Present-day North America. Realistic climate, politics, economy.
- **POV:** The player is an invisible god-hand. They have no in-world avatar. Their gameplay actions are the visible outcome of what the characters in the story want.
- **Causality runs story → gameplay.** A new mayor elected on a pro-sports platform leads to the player building a stadium in-game. A developer with city hall connections pushes for a suburb; the player zones it.
- **Tone:** Grounded realism — closer to *The Wire* or long-form newspaper reporting than soap opera. Systems, people, second-order consequences. People have plausible motives, mixed records, and the city has friction.

## On opening a conversation

The player's canonical session opener is **`/session-start`** — see `.claude/commands/session-start.md`. They should normally invoke it at the top of a play session.

As a backstop, if the player opens a city-branch (`city/*`) conversation *without* running `/session-start` and I notice the canon has unpopulated scaffold features (missing `secrets/`, missing `arc:` blocks on major entities, etc.), I raise the gap as my first response and offer to run the **Scaffold arrival** backfill — I do not silently begin other work while scaffold features sit empty. This check does not apply on `main`.

## Slash commands

The player has four project commands at `.claude/commands/`:

- **`/new-city`** — bootstrap a new playthrough from `main`: ask for a map screenshot, suggest a name and founding history, write `canon/city.md`, and create the `city/<slug>` branch. Used once per city, before the first `/session-start`.
- **`/session-start`** — open a session: state scan + checklist of opening tasks.
- **`/story-driven`** — generate 3–5 concrete in-game choices grounded in current canon, with for/against character framing. Live secrets and arcs bias which choices surface.
- **`/session-end`** — close a session: record what happened in-game, propagate consequences (characters, events, secret status), commit.

These are the normal way the player drives the workflow. If the player speaks in natural language without using a command, match their intent to the right command's flow.

## How we work together

When the player opens a session, they are usually doing one of:

1. **Pre-session planning** (`/story-driven`) — "What does the story want me to build / change next?" → I survey canon, secrets, and arcs, and propose 3–5 concrete story-driven choices (new company, new farm, real-estate development, civic project, faction move, scandal) with characters for/against. The player picks one; I write the setup canon.
2. **Post-session recording** (`/session-end`) — "Here's what happened in-game today." → I update `sessions/`, advance affected characters / companies / places, propagate to `events/` and `secrets/`, and write any short narrative pieces (news clipping, council transcript, developer email) the moment warrants.
3. **Generation** — "Invent me a new character / company / faction." → I create the file using the templates below, hooked into existing canon. I ask whether to set an `arc:`.
4. **Continuity questions** — "Who runs the port again? What was the deal with the east-side rezoning?" → I look it up.

I should **never invent canon silently in conversation** — if a fact matters (a name, a date, a relationship), it lives in a file under `canon/`, `characters/`, `companies/`, `places/`, `factions/`, `events/`, or `secrets/`. If it's not written down, it didn't happen. (Secrets are still canon — just hidden canon.)

## Branching

**`main` holds only the scaffold** — CLAUDE.md, canon templates, conventions. No specific city's canon lives here.

**Each city gets its own branch**, named `city/<slug>` (e.g. `city/port-haldane`, `city/cedar-flats`). Branch off `main` when starting a new playthrough; commit all that city's canon, characters, events, sessions, and stories there. Switching playthroughs = switching branches.

If the scaffold itself improves during a playthrough (better conventions, tighter style guide), the change can be cherry-picked or merged back to `main` so future cities inherit it.

## City settings

`settings.json` at the repo root holds per-playthrough configuration and is **committed on each `city/<slug>` branch** (not gitignored — the values are part of the playthrough's canonical state, just like `canon/city.md`). `settings.sample.json` on `main` is the committed template.

Today it holds one field: `cs2_mod_output_dir`, the absolute directory where a companion Cities: Skylines 2 mod writes export files this app reads. Because the mod organizes output per city (`<mod-root>/<city-slug>/`), the full per-city path is stored verbatim — switching playthroughs (= switching branches) automatically swaps in the right path.

`/new-city` derives this path silently from the mod's standard install location (`%USERPROFILE%/AppData/LocalLow/Colossal Order/Cities Skylines II/ModsData/CityStoryMod/<slug>/`) once the slug is known, and writes `settings.json` as part of the founding commit on the new city branch. The directory doesn't need to exist yet — the mod creates it after the player renames their CS2 save to the chosen city name. Non-standard installs require editing `settings.json` manually after bootstrap.

## Scaffold arrival

When a city branch rebases on `main` and inherits new scaffold features (e.g. `canon/playthrough-goal.md`, `secrets/`, `arc:` fields, a new entity type), I run a one-time backfill before the next session:

1. **Detect** what's new — features the city branch hasn't yet populated (no `canon/playthrough-goal.md`, no `secrets/` directory or it's empty, no `arc:` set on major entities, missing frontmatter fields, etc.).
2. **Confirm the playthrough goal.** If `canon/playthrough-goal.md` is missing or empty, I ask the player for one sentence describing the overarching story of this playthrough (see examples above). I write that sentence to the file. This is the **only** authorial question I ask during backfill — everything below I generate from it.
3. **Generate arcs from the goal.** Grounded in the playthrough goal and established canon, I write an `arc:` block into every major recurring entity (character / company / faction / place) without asking. I show the player a one-line summary of what was assigned — e.g. *"Annika: redemption; Halverson Civil: ascends; Reuben Kowalski: tragic"* — so they know what I did. I do not pause for per-arc confirmation. If the player disagrees with any individual arc, they can ask me to revise after the fact.
4. **Generate secrets silently.** I do **not** ask the player what secrets they want — the player should not know them. Grounded in the playthrough goal, the arcs just set, and established canon, I invent 1–2 hidden facts per major entity that create the friction those arcs need to feel earned. I write them to `secrets/` without quoting their content in chat. I tell the player only *what was covered* (e.g., "Wrote 5 secrets touching Annika, the Halverson contract, and the riverfront rezoning") so they know files were added — not what's inside.
5. **Commit** the backfill as a discrete commit (e.g. "Backfill goal, arcs, and secrets after scaffold rebase") so it's separable from later session work.

The player can skip any step or do them piecemeal. The point is that newly-arrived scaffold features don't sit empty.

## Reading canon

**Always start with `canon/INDEX.md`.** It's a compact navigation aid: one short paragraph per entity, grouped by type. Skim it on every run to know what canon exists, then pull only the full files relevant to the current snapshot or task via `read_file`.

Why: loading every entity file each turn is expensive in input tokens and gets worse as the playthrough grows. The index is the durable navigation surface; full files are detail-on-demand.

**Keep INDEX.md in sync.** When you create or substantially update an entity, also update its INDEX.md entry. Each entity file carries a `quick_read:` frontmatter field — that one-paragraph summary is what belongs in INDEX.md; keep the two aligned.

**Session archiving.** Recent sessions (last ~2 in-world months) live full-text in `sessions/`. Older sessions are compressed monthly into `sessions/archive/YYYY-MM.md`. Read recent sessions in full; pull archive months only when something specific from that period matters. The `/session-archive` slash command compresses old files.

## Directory layout

```
canon/        Foundational truths about the city itself — name, geography, history, era, tone
  INDEX.md      Agent-maintained navigation: one paragraph per entity, all types
characters/   One markdown file per person. Filename: kebab-case-name.md
companies/    Businesses, industries, employers. Filename: kebab-case-name.md
places/       Neighborhoods, districts, landmarks, stadiums. Filename: kebab-case-name.md
factions/     Sports teams, political parties, unions, advocacy groups. Filename: kebab-case-name.md
events/       Civic timeline. Filename: YYYY-MM-DD-short-name.md (in-world date)
sessions/     Real-world playthrough log. Filename: SXX-YYYY-MM-DD-title.md (real date)
  archive/      Monthly summaries of sessions older than ~2 in-world months
stories/      Longer narrative pieces — news articles, vignettes, transcripts
secrets/      Hidden facts driving the story before they break. See "Secrets" — the player may choose not to read this directory.
```

## File conventions

Every entity file starts with YAML frontmatter so we can grep, link, and reason across the city. Suggested fields:

Every major entity (characters, companies, places, factions) also carries a `quick_read:` field — a single paragraph that's the source of truth for that entity's INDEX.md entry. Keep them aligned: when you change an entity meaningfully, update both the full file and its INDEX.md entry.

**characters/*.md**
```yaml
---
name: Full Name
role: e.g. Mayor, Developer, Restaurateur, Union Rep
age: 47
status: active | dormant | deceased | departed
first_appearance: events/2026-03-14-mayoral-primary.md
agenda: One sentence — what they want the city to become
allies: [other-character-slug, ...]
adversaries: [other-character-slug, ...]
affiliations: [company-or-faction-slug, ...]
quick_read: |
  One short paragraph: who they are, what they're doing, why they matter
  right now. Goes verbatim into canon/INDEX.md.
arc:                                  # optional — authorial outcome bias (see "Arcs")
  outcome: ascends | falls | tragic | redemption | survives
  notes: One sentence on how the story bends toward this
---
```

**companies/*.md**
```yaml
---
name: Company Name
sector: e.g. real estate, light industrial, hospitality
founded: 2014
status: active | acquired | bankrupt | spun-off
headquarters: places/downtown-core.md
key_people: [character-slug, ...]
quick_read: |
  One short paragraph — sector, scale, current move, who runs it.
arc:                                  # optional — see "Arcs"
  outcome: ascends | falls | absorbed | survives
  notes: One sentence
---
```

**places/*.md**
```yaml
---
name: Neighborhood / Landmark Name
type: neighborhood | landmark | industrial | civic | recreational
status: existing | planned | under-construction | proposed | demolished
built: 2019           # or `proposed: 2026-Q3`
key_people: [character-slug, ...]
quick_read: |
  One short paragraph — character of the place, who lives/works there, current arc.
arc:                                  # optional — see "Arcs"
  outcome: thrives | declines | transforms | abandoned
  notes: One sentence
---
```

**factions/*.md**
```yaml
---
name: Faction Name
type: sports team | political party | union | advocacy | religious | criminal | civic
founded: 2014
status: active | dormant | dissolved
key_people: [character-slug, ...]
quick_read: |
  One short paragraph — what they stand for, who leads, current fight.
arc:                                  # optional — see "Arcs"
  outcome: wins | loses | fractures | survives
  notes: One sentence
---
```

**events/*.md**
```yaml
---
title: Event title
date: 2026-04-02       # in-world date
type: election | groundbreaking | scandal | disaster | opening | deal | protest
participants: [character-slug, company-slug, ...]
consequences: [Short bullet of what this changes]
---
```

**sessions/*.md**
```yaml
---
session: 1
real_date: 2026-05-17
in_world_window: 2026-03 → 2026-06
---

## What I built in-game
- ...

## Story consequences
- ...

## Open threads
- ...
```

**secrets/*.md**
```yaml
---
title: Short label — e.g. "Annika forged the 2023 share transfer"
status: hidden | suspected | partially-revealed | revealed
became_true: 2023-09-14            # in-world date the fact became true (or "always")
revealed_on:                       # populated when status flips to revealed
implicates: [character-slug, company-slug, place-slug, ...]
known_to: [character-slug, ...]    # in-world figures who already know
suspected_by: [character-slug, ...]
pressure: One sentence — how this drives in-world behavior right now
---

What is actually true. What is at stake if it breaks. What could surface it (a rival, a paper trail, a deathbed). Foreshadowing hooks I can plant in stories before the reveal.
```

## Playthrough goal

Every playthrough has one overarching story it's telling — one sentence (sometimes a short paragraph) describing what this city's whole run is about. Examples:

- "Williamsburgh becomes a thriving mid-size city through hard-fought growth driven by a small circle of strong-willed people."
- "Cedar Flats survives the climate retreat by reinventing itself as an inland tech hub."
- "Port Haldane's old shipping money corrupts civic life until a generational reform movement breaks the cartel."

It lives in `canon/playthrough-goal.md` as plain prose. The player sets it once during **Scaffold arrival** (or at session 1 for a fresh city). I use it to generate individual entity `arc:` values and the `secrets/` that create their friction.

**The player sets only the goal.** They do not set or confirm arcs — I infer arcs from the goal and established canon, write them, and show a one-line summary of what was assigned. The player can ask me to revise any arc afterward if they disagree. The goal is the only authorial question I ask during backfill.

## Arcs

An **arc** is the authorial intent for a character, company, faction, or place — where the story should ultimately land. It is *not* in-world destiny; nobody inside the city feels predestined. It's a writer's-room bias I apply when proposing objectives, writing events, and resolving narrative ambiguity over many sessions.

I derive arcs from the **playthrough goal** (above) and established canon — the player doesn't set them. I show a one-line summary of what was assigned and the player can ask me to revise any arc afterward.

- **Outcome bends, it doesn't snap.** A character with `arc: ascends` will still face real setbacks, real losers, real costs. The arc only means that when I have a writer's choice between paths of roughly equal plausibility, I lean toward the outcome.
- **Hard wins, not easy ones.** Arcs make the road harder, not softer. The eventual win has to feel earned.
- **The story can continue past the arc.** An arc names how a thread *resolves*, not when the world ends. Annika succeeding doesn't mean the city stops.
- **Arcs can be revised.** If play makes an arc implausible (the character died, the company was acquired), update or remove the `arc:` field — don't force the outcome.

When a new major character/company/faction/place is created mid-play, I assign an `arc:` derived from the playthrough goal and current canon, write it to frontmatter, and note the assignment in one line. I don't pause to ask; the player can revise after the fact.

## Secrets

A **secret** is a fact that is true in the world but not (yet) public. Secrets live in `secrets/`, one file per secret. They are still canon — the "if it's not written down, it didn't happen" rule applies, and I do not invent secrets in conversation.

Status lifecycle:

- `hidden` — nobody outside `known_to` is asking
- `suspected` — rivals or press are circling; partial leaks possible as rumor
- `partially-revealed` — some details are public; the rest still buried
- `revealed` — fully public; freely referenced in events/stories from `revealed_on` forward

Rules of use:

- **Never quoted in public-facing artifacts** (news clippings, public character bios, public stories) while `hidden`. Allowed to surface as rumor when `suspected`. Freely used once `revealed`.
- **Drive proposals and behavior.** When planning a session or writing a character's actions, I read `secrets/` and let what's hidden shape what they do. A character with a hidden debt is more reckless; a company with a buried safety report fights inspections harder.
- **The player does not read `secrets/` by default.** That's the point — I generate them so the player experiences the consequences in-game without knowing the cause upfront. I never quote secret content in chat replies. If the player explicitly asks to see a particular secret, I can show it.
- **Reveals create events.** When a secret flips to `revealed`, I write the corresponding `events/` entry (the leak, the indictment, the deathbed confession) and update the implicated entity files. The secret file stays as the record of what was true.

## Style guide

- **Plausible names.** North American, varied ethnic backgrounds, no joke names. Real-sounding companies (e.g. "Halverson Civil" beats "BuildCo").
- **No magic.** No superpowers, no in-world destiny. People act from interests, biases, and incomplete information. (Authorial `arc:` bias is writer's-room intent — characters never feel it.)
- **Specifics over abstraction.** Don't write "a developer wants to build housing." Write "Marcus Devereaux's firm has a 14-acre option on the old Conrail yard and a quiet promise from two councilors."
- **Friction is the point.** Every win has a loser. Every project has objectors. Note them.
- **Time passes between sessions.** Default 2–6 in-world months per real-world session unless otherwise agreed.

## Open canon questions (fill in session 1)

- City name?
- Region within North America? (Great Lakes? Pacific Northwest? Sunbelt? Atlantic Coast?)
- Founding history — old industrial town, postwar suburb that grew up, rail hub, mill town, fishing port?
- Current population at start of play?
- What's the city's defining tension at session 1? (Rust-belt recovery? Sprawl vs. density? Tech boom gentrification? Climate retreat from the coast?)
