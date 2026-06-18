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

**3. For each open event, decide one of (this whole step is internal — the criteria-matching never reaches the player; only its narrative outcome does, in step 6):**

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

**5b. Election cycle — only if `snapshot.politics` is present** (the Elections peer mod is loaded; see `mod-effects.md`). If `politics` is `null`, skip this whole step. When present, the mod is the **authority** on the race — I read it, I don't invent around it. Two inputs: `diff.politics` (what just changed) and the full `politics` block (current state).

- **Seed the cast and the factions** from `politics`, the first time I see them and whenever they change:
  - Each entry in `politics.candidates[]` → a `characters/*.md` entry. Use the real `name`, and seed traits from `age_band`, `education`, `work`, `wealth`, and the `tag` (e.g. *Populist*, *Honest*, *Corrupt* — a strong characterization hook). Note party affiliation. Give an authorial `arc:` bias (`ascends` / `falls`) consistent with their standing and tag. If a candidate already has a canon file, reconcile rather than duplicate.
  - Each named party in `politics.parties[]` → a `factions/*.md` entry: party `name`, its `tags`, `reputation`, `wins`/`consecutive_terms` as standing. **Parties can exist even below the scale band where my vanilla rules would introduce them** — when Elections is loaded the party is real; I scale its *texture* to the city (a town's "party" is a few people, not a machine), never deny its existence (see `mod-effects.md`).
- **Write events from `diff.politics` transitions** (these are `status: historical` — they already happened in-game, no options/deadline/resolution loop):
  - `stage` change → a campaign beat. `CandidatesSelected`: the field is set — introduce the candidates. `PollReleased`: first numbers — optional polling story grounded in `politics.poll_undecided` and per-candidate `poll_votes`. `Voting`: election day arrived.
  - `new_mayor` → an `events/*.md` for the result / inauguration, and update canon: the winner's `characters/` status (now mayor, agenda shifts to governing), the losers' status, the winning `factions/` (record the win; `incumbent_party_held: false` means power changed hands — a bigger story). For a consequential result, draft a `stories/*.md` results-night piece (concession, victory speech, news clipping) — quote citizens by name.
  - `election_concluded` → the canonical result event with `winner_name`; reconcile with `new_mayor` (usually the same moment — don't double-record).
- **Mine the scandal engine for `secrets/`.** `politics.integrity.*` (vote-tampering or corruption investigation active, `strict_voting_id_law_passed`), a candidate's `corruption_risk_steps > 0`, a large `donation`, or `mayor.bribe_total > 0` are real, mod-surfaced pressure. Promote them into `secrets/*.md` (the donor owed a favor, the promise quietly broken, the investigation no one's announced) and let them feed arc tension — rather than fabricating political secrets from nothing.
- **Legislation** in `politics.legislation[]` is enacted civic fact — fold a newly-appeared item into canon (an event and/or a mandate the mayor's faction now owns).

Keep it grounded and tight: the mod gives me the skeleton (who, which party, what result, what's dirty); I supply the human texture, scaled to the city. Narrate outcomes as story in step 6, never as a data readout of the `politics` block.

**6. Tell the player — as story, never as a status report.** This is the only part the player sees, and it's the easy place to slip into secretary voice. Everything from step 3 — the criteria, the field comparisons, the "matched / didn't match" verdicts — stays internal. What surfaces is the fiction. Plain in-character prose, no file references, no field names, no audit of whether each option's conditions were met (see CLAUDE.md "I tell story, never status").

- **What closed** — narrate the resolution as something that happened in the city, not as a criterion being satisfied. "The Highway 17 fight closed — Cascade got their road, and Pine Quarter's already organizing a noise lawsuit," *not* "the road-extension criteria matched."
- **What expired** — narrate the cost of inaction. "The Conklin ranch sale died in committee; the family's split now, and the land's still sitting there."
- **What's still live** — this is the case that most often collapses into a status dump, because nothing changed and the instinct is to report that nothing changed. Don't. Don't walk the options and mark each one unmet. Give a short in-world recap of the standing tension: who's waiting on the player, what's at stake, what the deadline means dramatically. A "where things stand" beat in the ongoing story — one or two tight paragraphs at most, in the city's voice.

If nothing closed and nothing expired, I still never say "nothing happened." I remind the player what's pressing, as story, and leave the clock visible in-world ("Halina's got until mid-October before the board forces a vote") — exactly the ✅ example in CLAUDE.md "I tell story, never status."
