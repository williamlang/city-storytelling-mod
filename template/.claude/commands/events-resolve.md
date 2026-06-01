---
description: Scan open events against the latest snapshot — close matches, expire past-deadline events with consequence canon
order: 35
---

Walk every open event and decide which ones close, then propagate consequences. This runs manually any time the player wants to "check the inbox," and automatically as the first step of `/session-end`.

**1. Find open events.** Read every `events/*.md` file with `status: open` in frontmatter. If there are none, say so in one short line and stop — nothing to do.

**2. Read the current date and snapshot context.**
- **Current in-world date** — read `clock.json` at the city root. Its `in_world_date` is the live "now," refreshed every few seconds; **use this for all deadline comparisons**, not the snapshot's `captured_at_ingame` (which can be minutes stale — i.e. in-world weeks behind, since the sim clock runs fast). If `clock.json` is missing, fall back to the latest snapshot's `captured_at_ingame`.
- **Snapshot state** — pull the latest snapshot in `snapshots/`. Hold onto:
  - `city.*`, `pollution.*`, `crime.*`, `land_value.*`, `district_zones`, `diff.*` — the fields acceptance criteria refer to.
  - `carto/processed/index.md`, `roads.md`, `districts/<slug>.md` — for criteria that reference spatial state.

**3. For each open event, decide one of:**

**a) Match.** One of the event's options' `acceptance_criteria` is satisfied by current snapshot state (or by the diff between this snapshot and the one when the event opened). Be honest about what counts:
- A criterion saying "tax_industrial drops by 2+" requires actually seeing the drop in `snapshot.city.budget.tax_industrial`. Compare against either the snapshot closest to the event's `date:` or, if that's not available, against a reasonable read of the value at open time stated in the body.
- A criterion saying "a new road segment ≥ X km appears in carto/processed/roads.md near <area>" requires the road to actually be there in `carto/processed/roads.md` now (or in `diff.named_buildings.added` if it's also a CustomName), in the area the criterion specifies. "A road got built somewhere" is not a match.
- A criterion saying "a new building tagged 'Convention Center' appears" requires that building to actually appear in `diff.named_buildings.added` or in the current `carto/processed/districts/<slug>.md`.

If a criterion is borderline — the player did something in the general direction but the snapshot doesn't fully show it — leave the event `open`. False positives are worse than false negatives; an event closing early on a misread denies the player the second swing.

When you do call a match: update the event's frontmatter:
- `status: resolved-by-player`
- `resolved_on:` = the current in-world date (from `clock.json`)
- `resolved_via:` = the matched option's `id`
- `consequences:` = 2–4 short bullets capturing what this means in fiction (who won, who lost, what changes downstream)

Then write the consequence canon (step 4 below).

**b) Timeout.** The event is `open`, no option matched, and `in_world_deadline` is past the current in-world date (from `clock.json`). The window closed. Update frontmatter:
- `status: resolved-by-timeout`
- `resolved_on:` = the current in-world date (from `clock.json`)
- `resolved_via: timeout`
- `consequences:` = 2–4 short bullets capturing what happens *because nothing happened* — the deal collapsed, the rival smelled blood, the offer expired, the political moment passed. The point of timeout is that ignoring an event has weight; don't let it be neutral. Lean the consequences toward whoever was on the losing side of inaction (usually the option-pushers in `pushed_by`; sometimes the opposed parties get a quiet win).

Then write the consequence canon (step 4 below).

**c) Still open.** Neither matched nor expired. Leave the file as-is. Note it in the summary so the player knows it's still live.

**4. Propagate consequences.** For every event you just closed (resolved-by-player or resolved-by-timeout), update implicated canon:
- **`characters/*.md`** named in `participants`, `pushed_by`, or `opposed_by` — update `status`, `agenda`, `allies` / `adversaries` based on what just happened. A character who lost a contract isn't dormant; their agenda just changed. Keep `quick_read:` aligned with the new state and update the corresponding `canon/INDEX.md` entry.
- **`companies/*.md`** named in `participants`, etc. — same treatment (status, key_people, headcount-implied shifts).
- **`places/*.md`** — if a place was created or modified by the resolution, advance its `status` (planned → under-construction → existing, or planned → cancelled). Write a new `places/*.md` if the resolution made a brand-new place real (a new commercial center, a new park).
- **`factions/*.md`** — if a faction won / lost / fractured, update accordingly.
- **`secrets/*.md`** — did this event put pressure on a hidden fact? Advance `status` (hidden → suspected, etc.) if the resolution leaked something. If a secret flipped to `revealed`, write a separate `events/*.md` entry as `status: historical` for the leak itself.
- **New `events/*.md`** — sometimes a resolution spawns its own downstream event (e.g. a timeout where "the rival smells blood" implies a new move next session). If the downstream is concrete and immediate, write it now as `status: open` with its own options + deadline. Most resolutions don't spawn new events; only do this when the fiction genuinely demands it.

Keep propagation tight. Don't rewrite every character's full file because one event closed — touch the fields that actually changed.

**5. Optional narrative pieces.** For the most consequential resolutions (a major character's reversal, a faction's collapse, a secret breaking), draft a short `stories/*.md` entry — news clipping, council transcript, developer memo. Skip for routine closures. The session-end pass will pick up anything missed.

**6. Tell the player.** One short in-character summary, in plain prose, no file references:
- What closed and how ("The Highway 17 fight closed — Cascade got their road, and Pine Quarter is now organizing a noise lawsuit.").
- What expired ("The Conklin ranch sale died in committee; the family's split now.").
- What's still open, with the in-world deadline ("The riverfront rezoning is still live through October.").

If nothing changed (everything is still open, no deadlines hit), say that in one line.
