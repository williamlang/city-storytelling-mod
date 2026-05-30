---
description: Propose one open story event the player can respond to in-game, with 2-4 grounded response options
order: 30
---

Write **one** open event — a moment the story is pushing on the city — with 2–4 in-world response options the player can act on in CS2. The event sits as `status: open` until the player's in-game actions match an option (closed by `/events-resolve`) or the deadline passes (closed by timeout). I do **not** ask the player to pick verbally — they pick by what they do in-game.

**Optional focus:** `$ARGUMENTS`

If a focus is supplied above (a topic, character, faction, place, or theme — e.g. `transit`, `Halverson Civil`, `riverfront`, `scandal`), the event must engage with it. Before writing, state in one short line how I interpreted the focus so the player can redirect. If the focus line is empty, generate from current canon and city state as usual.

**1. Check the open-event cap.** Count `events/*.md` files with `status: open`. If the count is already 3 or more, do *not* write a new event. Instead:
- Surface the open count and the titles in one short sentence to the player.
- Offer to either (a) resolve / supersede a stale one (run `/events-resolve` to clear timeouts, or pick one to retire early), or (b) describe what kind of event they'd want once one closes.
- Stop here. Don't generate a new proposal on top of a full queue.

**2. Survey state silently.** Pull these without narrating each file load:
- `canon/INDEX.md` first — that's the navigation surface. From it I know what entities exist without loading every file. Pull full entity files only for the ones the proposal will touch.
- `canon/playthrough-premise.md` — the event must serve, test, or complicate this premise.
- The latest snapshot in `snapshots/` — required for grounding:
  - `city.money`, `city.budget.income_daily`, `city.budget.tax_*` — what the city can afford and how it's funded.
  - `city.population`, `city.milestone` — scale band (see CLAUDE.md "Grounded in city state").
  - `city.churn.moved_away_by_reason`, `city.social`, `pollution.by_district`, `land_value.by_district`, `crime.by_district` — pressure signals worth dramatizing.
  - `district_zones` and `diff.*` — what's actually being built / changing right now.
  - `captured_at_ingame` — the current in-world date, used to compute the deadline.
- `carto/processed/index.md` plus any `districts/<slug>.md` the event will target — for spatial grounding (where the road would actually go, which district the rezoning hits).
- Open `events/*.md` (`status: open`) — don't propose something that overlaps with an open thread; either pick a different angle or reference / supersede the existing one.
- Active characters' agendas (load specific files via `Read` based on INDEX hits).
- Live `secrets/` — read them; let them shape the proposal. Whether to quote contents in chat follows `secrets_visibility` in `settings.json` (see CLAUDE.md "Secrets").
- Live arcs across major entities — bias toward proposals that *earn* the arc's outcome (no easy wins for `ascends`; tempting bad calls for `falls`).
- Recent events (resolved + historical) and the last session file (`sessions/`, not `sessions/archive/` unless something older is directly load-bearing).

**3. Pick the event.** One concrete in-world moment, motivated by existing canon (a character pressing, a company moving, a faction surfacing). It must:
- **Be plausible at the city's current scale.** Apply the bands in CLAUDE.md "Grounded in city state" — a stadium for a 5k town is fiction the player can't act on. Match every option's scope to current `city.money`, `city.population`, and `city.milestone`. CS2's population is heavily compacted; use the in-game number as the city's actual size — don't rescale to a real-world equivalent.
- **Be motivated by named people** — not "developers want…" but "Marcus Devereaux's firm has a 14-acre option on the old Conrail yard and a quiet promise from two councilors." Friction always has a face.
- **Have at least one meaningfully opposed party** — every win has a loser; note them. (The only exception is a genuinely uncontested moment — rare, and only when it serves the story.)
- **Vary across runs.** Across `/story-driven` invocations in this playthrough, cover different categories: a new company opening, a farm or extractive operation, a real-estate development, a civic project (school / clinic / library / transit), a service expansion, a faction power move, a scandal-driven decision, an ultimatum, a controversy. Skim recently-resolved + open events to avoid repeating the same shape twice in a row. *(If a focus argument was supplied, this rule relaxes — focus replaces variety as the unifying constraint.)*

**4. Write 2–4 options.** Each option is a distinct in-game move the player could make to engage with this moment. For each:
- **`id`** — short kebab-case slug, stable for the event's life (used by `/events-resolve` to record which option fired).
- **`label`** — one short line describing the move in fiction terms (e.g. "Extend Highway 17 to the North Yards", "Industrial tax break").
- **`in_game_action`** — what the player actually does in CS2 (zone X acres of medium density in district Y, build a road from A to B, lower industrial tax by ~2 points, demolish the old mill). Concrete enough that the player can act without asking for clarification. Must be in the realm of what CS2 actually supports at the current `city.milestone` — don't write options the game can't unlock yet.
- **`acceptance_criteria`** — what the next snapshot will show if this option fires. Reference a specific field path or a concrete observable change (e.g. "snapshot.city.budget.tax_industrial drops by 2+ from this event's open date", "a new ~1 km road segment appears in `diff.named_buildings.added` near SE-quadrant North Yards", "snapshot.pollution.by_district['Pine Quarter'].noise drops below 200"). Avoid fuzzy outcomes — "the road gets built" isn't a criterion; "a road segment ≥ 800 m long appears in `carto/processed/roads.md` connecting <A> to <B>" is.
- **`pushed_by`** / **`opposed_by`** — character / company / faction slugs. Empty list is fine if nobody on that side surfaced.

At least one option must have meaningful opposition (consistent with rule 3's "at least one opposed party"). Options should be genuinely distinct — different in-game moves, different in-fiction winners and losers, not three flavors of the same zoning change. Don't write a "do nothing" option; that's what timeout covers automatically.

**5. Pick a deadline.** Based on the fiction's urgency, pick an in-world date `in_world_deadline = captured_at_ingame + N`:
- **Weeks (≤ 1 month):** a fire-department staffing crisis, a strike vote, a council session next Tuesday, a child-welfare scandal hitting the news.
- **Months (1–6 months):** a developer's purchase option expiring, a tax-policy window, a service-coverage emergency, a rezoning hearing.
- **Quarters / a year (6–18 months):** a contract negotiation, a faction power play, a re-election cycle, a campus-siting decision.
- **Multi-year (18+ months):** a ranch sale that's been talked about for a generation, a long-running franchise relocation, a generational shift.

Don't over-clock — short windows feel artificial unless the fiction genuinely supports them. Most events should land in the months-to-a-year range.

**6. Write the file.** Create `events/<YYYY-MM-DD>-<short-slug>.md` (date = current `captured_at_ingame`). Frontmatter follows the events template in CLAUDE.md exactly: `status: open`, `in_world_deadline:` filled in, all options with their five fields, `resolved_on:` / `resolved_via:` / `consequences:` empty. Body: motivating prose — who's pushing what, why now, what's at stake. Keep it tight (3–6 short paragraphs); the options carry the structure, the body carries the texture.

**7. Don't write setup canon for any option.** None of the options is "the chosen path" — the player picks by acting. Don't create characters / places / companies *for* an option unless they're already established canon being referenced. If the event introduces a *new* major entity (a developer the city's never seen before, a faction just forming), write the new entity file with `status: active` and an `arc:` derived from the playthrough premise — but only for entities whose existence the event itself requires, not entities one specific option would create.

**8. Tell the player** in plain in-character prose:
- One sentence framing the moment ("Cascade Composite Products has put a deadline on the Highway 17 fight.").
- The options, numbered, each with its label and the concrete in-game move.
- The in-world deadline ("This collapses by August 2027 if nothing moves.").
- One short closing — *not* "which do you pick?" but something like "Take it however you take it; the next read will pick up what you actually did." Make it clear the player doesn't have to commit verbally.

Do **not** mention frontmatter, file paths, status fields, or the resolution machinery. The player sees fiction; the lifecycle is bookkeeping.
