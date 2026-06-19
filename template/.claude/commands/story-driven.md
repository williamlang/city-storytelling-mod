---
description: Propose one open story event the player can respond to in-game, with 2-4 grounded response options
order: 30
---

Write **one** open event — a moment the story is pushing on the city — with 2–4 in-world response options the player can act on in CS2. The event sits as `status: open` until the player's in-game actions match an option (closed by `/events-resolve`) or the deadline passes (closed by timeout). I do **not** ask the player to pick verbally — they pick by what they do in-game.

**Optional focus:** `$ARGUMENTS`

If a focus is supplied above (a topic, character, faction, place, or theme — e.g. `transit`, `Halverson Civil`, `riverfront`, `scandal`), the event must engage with it. Before writing, state in one short line how I interpreted the focus so the player can redirect. If the focus line is empty, generate from current canon and city state as usual.

**1. Check the open-event cap.** Count `events/*.md` files with `status: open`, **excluding `type: election` events** (those are mod-driven civic events on their own cycle — they ride the Elections schedule and don't compete for the storyteller's proposal budget). If the remaining count is already 3 or more, do *not* write a new event. Instead:
- Surface the open count and the titles in one short sentence to the player.
- Offer to either (a) resolve / supersede a stale one (run `/events-resolve` to clear timeouts, or pick one to retire early), or (b) describe what kind of event they'd want once one closes.
- Stop here. Don't generate a new proposal on top of a full queue.

**2. Survey state silently.** Pull these without narrating each file load:
- `canon/INDEX.md` first — that's the navigation surface. From it I know what entities exist without loading every file. Pull full entity files only for the ones the proposal will touch.
- `canon/city.md` `region:` — read it as a hard constraint on naming and cultural grounding (don't re-derive region from latitude); `canon/tone.md` for the active narrative focus lenses, which bias what *kind* of objective to propose (citizens → human-scale, neighborhood goals tied to named residents; civic → systems/economy/politics goals).
- `canon/playthrough-premise.md` — the event must serve, test, or complicate this premise.
- The latest snapshot in `snapshots/` — required for grounding:
  - `city.money`, `city.budget.income_daily`, `city.budget.tax_*` — what the city can afford and how it's funded.
  - `city.population_hud`, `city.milestone_level` — scale band (see CLAUDE.md "Grounded in city state"). **Read the population every run** — it sets not just what's buildable but the *texture* of the drama (a handful of people in a room vs. a factional board). There is no `city.population` field; use `population_hud`.
  - `city.churn.moved_away_by_reason`, `city.social`, `pollution.by_district`, `land_value.by_district`, `crime.by_district` — pressure signals worth dramatizing.
  - `services.education` — per-school enrollment vs. capacity. A school at/over capacity (`utilization` ≥ ~0.95) or a tier with no seats is a ready-made "build/expand a school" event. Use the real numbers; never invent enrollment. (null = no school yet.)
  - `district_zones` and `diff.*` — what's actually being built / changing right now.
- `politics` (Elections mod; null if absent) — **a live race is a major live thread that should bias what I propose.** A poll dropping in three weeks, a frontrunner with strong `poll_votes` vs. a trailing rival, a sitting `mayor` about to face re-election, an active scandal in `integrity.*` — these pull the proposal toward civic-spend moments (a project the mayor's faction wants to point to before the vote), candidate-positioning moments (a developer courting a frontrunner), or scandal-bait. The election itself already has its own `type: election` event (from `/events-resolve`); what I write here is a *story event that intersects the race*, not a duplicate of it. Tie `pushed_by`/`opposed_by` to the candidates and parties in `politics`.
- **Current in-world date** — read `clock.json` at the city root (`in_world_date`). This is the live "now," refreshed every few seconds; use it as the event's open date and the base for computing the deadline — not the snapshot's `captured_at_ingame`, which can be in-world weeks stale. Fall back to the snapshot's `captured_at_ingame` only if `clock.json` is missing.
- `carto/processed/index.md` plus any `districts/<slug>.md` the event will target — for spatial grounding (where the road would actually go, which district the rezoning hits).
- Open `events/*.md` (`status: open`) — don't propose something that overlaps with an open thread; either pick a different angle or reference / supersede the existing one.
- Active characters' agendas (load specific files via `Read` based on INDEX hits).
- Live `secrets/` — read them; let them shape the proposal. Whether to quote contents in chat follows `secrets_visibility` in `settings.json` (see CLAUDE.md "Secrets").
- Live arcs across major entities — bias toward proposals that *earn* the arc's outcome (no easy wins for `ascends`; tempting bad calls for `falls`).
- Recent events (resolved + historical) and the last session file (`sessions/`, not `sessions/archive/` unless something older is directly load-bearing).

**3. Pick the event.** One concrete in-world moment, motivated by existing canon (a character pressing, a company moving, a faction surfacing). It must:
- **Be plausible at the city's current scale.** Apply the bands in CLAUDE.md "Grounded in city state" — a stadium for a 5k town is fiction the player can't act on. Match every option's scope *and the texture of the conflict* to current `city.money`, `city.population_hud`, and `city.milestone_level`. CS2's population is heavily compacted; use the in-game number as the city's actual size — don't rescale to a real-world equivalent.
- **Be motivated by named people** — not "developers want…" but "Marcus Devereaux's firm has a 14-acre option on the old Conrail yard and a quiet promise from two councilors." Friction always has a face.
- **Have at least one meaningfully opposed party** — every win has a loser; note them. (The only exception is a genuinely uncontested moment — rare, and only when it serves the story.)
- **Vary across runs.** Across `/story-driven` invocations in this playthrough, cover different categories: a new company opening, a farm or extractive operation, a real-estate development, a civic project (school / clinic / library / transit), a service expansion, a faction power move, a scandal-driven decision, an ultimatum, a controversy. Skim recently-resolved + open events to avoid repeating the same shape twice in a row. *(If a focus argument was supplied, this rule relaxes — focus replaces variety as the unifying constraint.)*

**4. Write 2–4 options.** Each option is a distinct in-game move the player could make to engage with this moment. For each:
- **`id`** — short kebab-case slug, stable for the event's life (used by `/events-resolve` to record which option fired).
- **`label`** — one short line describing the move in fiction terms (e.g. "Extend Highway 17 to the North Yards", "Industrial tax break").
- **`in_game_action`** — what the player actually does in CS2 (zone X acres of medium density in district Y, build a road from A to B, lower industrial tax by ~2 points, demolish the old mill). Concrete enough that the player can act without asking for clarification.
- **`acceptance_criteria`** — what the next snapshot will show if this option fires. Reference a specific field path or a concrete observable change (e.g. "snapshot.city.budget.tax_industrial drops by 2+ from this event's open date", "a new ~1 km road segment appears in `diff.named_buildings.added` near SE-quadrant North Yards", "snapshot.pollution.by_district['Pine Quarter'].noise drops below 200"). Avoid fuzzy outcomes — "the road gets built" isn't a criterion; "a road segment ≥ 800 m long appears in `carto/processed/roads.md` connecting <A> to <B>" is.
- **`pushed_by`** / **`opposed_by`** — character / company / faction slugs. Empty list is fine if nobody on that side surfaced.

At least one option must have meaningful opposition (consistent with rule 3's "at least one opposed party"). Options should be genuinely distinct — different in-game moves, different in-fiction winners and losers, not three flavors of the same zoning change. Don't write a "do nothing" option; that's what timeout covers automatically.

**5. Pick a deadline.** Based on the fiction's urgency, pick an in-world date `in_world_deadline = current in-world date + N` (the date from `clock.json`):
- **Weeks (≤ 1 month):** a fire-department staffing crisis, a strike vote, a council session next Tuesday, a child-welfare scandal hitting the news.
- **Months (1–6 months):** a developer's purchase option expiring, a tax-policy window, a service-coverage emergency, a rezoning hearing.
- **Quarters / a year (6–18 months):** a contract negotiation, a faction power play, a re-election cycle, a campus-siting decision.
- **Multi-year (18+ months):** a ranch sale that's been talked about for a generation, a long-running franchise relocation, a generational shift.

Don't over-clock — short windows feel artificial unless the fiction genuinely supports them. Most events should land in the months-to-a-year range.

**6. Write the file.** Create `events/<YYYY-MM-DD>-<short-slug>.md` (date = current in-world date from `clock.json`). Frontmatter follows the events template in CLAUDE.md exactly: `status: open`, `in_world_deadline:` filled in, all options with their five fields, `resolved_on:` / `resolved_via:` / `consequences:` empty. The body **leads with the summary blockquote** (see the events template in CLAUDE.md): "The ask" in one line, then each option's `in_game_action` restated as one plain numbered line, then the deadline — so the player can see *what to actually do* before reading any prose. Then the motivating prose — who's pushing what, why now, what's at stake. Keep the prose tight (3–6 short paragraphs); the summary carries the ask, the options carry the structure, the prose carries the texture. The summary is scannable plain language (no field names, no mechanism); the prose can stay as rich as it wants — keep both. The body must also carry a clickable `(x, y)` pin for the event's site(s) — see "Anchoring an event in space" below; a coordinate sitting only in frontmatter (`anchor:` / `in_game_action`) does not render as a pin.

**Anchoring an event in space — the pin must be in the BODY.** A clickable map pin renders **only** from a `(x, y)` pair written in the **body prose** (and in chat). A coordinate that lives only in frontmatter — `anchor:`, `in_game_action`, `acceptance_criteria` — does **not** render as a pin; the player can't click it. So every place an event turns on (its primary site, and each option's site where they differ) **must appear as an `(x, y)` in the body** — in the summary blockquote line or in the motivating prose where that spot is described. Putting the coordinate in `in_game_action` too is fine for the resolver, but that's *in addition to* the body pin, never instead of it. **Default to pinning** — most events reference an existing district, road, junction, or building that already carries a coordinate in the chunks, so there is almost always a real pin to drop.

The coordinate must be a **verbatim chunk coordinate** — see CLAUDE.md "Locate canon in space." A development on empty land (a new shoreline parcel, an unbuilt corridor) has no coordinate of its own: anchor to the **nearest existing feature it extends from** (the grid edge, the highway, the road junction, a district centroid) and let the prose carry the direction — *never* estimate a point out on the empty parcel or "toward the shore" (the chunks have no land/water test, so an extrapolated point lands offshore as often as not). Describing a site in words with **no** pin is the rare last resort — only when nothing in the chunks is genuinely near — not the easy default.

**7. Don't write setup canon for any option.** None of the options is "the chosen path" — the player picks by acting. Don't create characters / places / companies *for* an option unless they're already established canon being referenced. If the event introduces a *new* major entity (a developer the city's never seen before, a faction just forming), write the new entity file with `status: active` and an `arc:` derived from the playthrough premise — but only for entities whose existence the event itself requires, not entities one specific option would create.

**8. Tell the player** in plain in-character prose:
- One sentence framing the moment ("Cascade Composite Products has put a deadline on the Highway 17 fight.").
- The options, numbered, each with its label and the concrete in-game move.
- The in-world deadline ("This collapses by August 2027 if nothing moves.").
- One short closing — *not* "which do you pick?" but something like "Take it however you take it; the next read will pick up what you actually did." Make it clear the player doesn't have to commit verbally.

Do **not** mention frontmatter, file paths, status fields, or the resolution machinery. The player sees fiction; the lifecycle is bookkeeping.
