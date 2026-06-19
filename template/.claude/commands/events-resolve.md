---
description: Scan open events against the latest snapshot — close matches, expire past-deadline events with consequence canon
order: 35
---

Walk every open event and decide which ones close, then propagate consequences. This runs manually any time the player wants to "check the inbox," and automatically as the first step of `/session-end`.

**1. Read the current date and snapshot context** (do this first — every step below needs it).
- **Current in-world date** — read `clock.json` at the city root. Its `in_world_date` is the live "now," refreshed every few seconds; **use this for all deadline comparisons**, not the snapshot's `captured_at_ingame` (which can be minutes stale — i.e. in-world weeks behind, since the sim clock runs fast). If `clock.json` is missing, fall back to the latest snapshot's `captured_at_ingame`.
- **Snapshot state** — pull the latest snapshot in `snapshots/`. Hold onto:
  - `city.*`, `pollution.*`, `crime.*`, `land_value.*`, `district_zones`, `diff.*` — the fields acceptance criteria refer to.
  - `politics` + `diff.politics` — the election cycle (step 2), present only with the Elections mod.
  - `carto/processed/index.md`, `roads.md`, `districts/<slug>.md` — for criteria that reference spatial state.

**2. Election cycle — runs every pass, whether or not there are open events** (Elections mod; skip entirely if `snapshot.politics` is `null`). The mod is the **authority** on the race — I read it, I don't invent around it. The point of this step is to turn the live election into a normal open event so the rest of this command's machinery carries it automatically. Two inputs: `politics` (current state) and `diff.politics` (what just changed).

- **Seed the cast and the factions** from `politics`, the first time I see them and whenever they change:
  - Each entry in `politics.candidates[]` → a `characters/*.md` entry. Keep the mod's real `name` (see CLAUDE.md "Grounding mod-given names"), and seed traits from `age_band`, `education`, `work`, `wealth`, and the `tag` (e.g. *Populist*, *Honest*, *Corrupt* — a strong characterization hook). Note party affiliation. Give an authorial `arc:` bias (`ascends` / `falls`) consistent with their standing and tag. If a candidate already has a canon file, reconcile rather than duplicate.
  - Each named party in `politics.parties[]` → a `factions/*.md` entry: party `name`, its `tags`, `reputation`, `wins`/`consecutive_terms` as standing. **Parties can exist even below the scale band where my vanilla rules would introduce them** — when Elections is loaded the party is real; I scale its *texture* to the city (a town's "party" is a few people, not a machine), never deny its existence (see `mod-effects.md`).
- **Generate the election event** (this is the integration — the election becomes a real `events/*.md` that flows through steps 3–6 like anything else). When a campaign is live (`politics.stage` is `CandidatesSelected`, `PollReleased`, or `Voting`) and there is **no open `type: election` event for this cycle** (`cycle:` = `politics.schedule.mayor_term_year`), write one now as `status: open`:
  - `in_world_deadline:` = `politics.schedule.election` (the race resolves on a known date — unlike a normal "did the player build X" event).
  - `participants:` = the candidate characters.
  - `options:` = one per candidate ("back <name> — donations, endorsement, turnout programs, via the Elections panel"), plus a `stay-neutral` option. Each option's `acceptance_criteria` references the outcome: e.g. *"after election day, `politics.result.winner_name` is `<candidate>` (or their party wins)"*; the neutral option's criterion is *"the race concludes with no player donations registered (`politics.candidates[].donation` all 0)."*
  - Body: the campaign as story — the stakes, the frontrunner (read `poll_votes` / party `reputation`), what each candidate would mean for the city. Ground it in the premise and active arcs.
  - One election event per `cycle`. If a `PollReleased` update materially shifts the race, update the existing event's body (and optionally a `stories/` polling piece) rather than opening a second event.
- **Mine the scandal engine for `secrets/`.** `politics.integrity.*` (vote-tampering or corruption investigation active, `strict_voting_id_law_passed`), a candidate's `corruption_risk_steps > 0`, a large `donation`, or `mayor.bribe_total > 0` are real, mod-surfaced pressure. Promote them into `secrets/*.md` (the donor owed a favor, the promise quietly broken, the investigation no one's announced) and tie them to the election event as arc tension — rather than fabricating political secrets from nothing.
- **Legislation** in `politics.legislation[]` is enacted civic fact — fold a newly-appeared item into canon (an event and/or a mandate the mayor's faction now owns).

The election event created here is then resolved by the very next steps — I don't resolve it separately.

**3. Find open events.** Read every `events/*.md` file with `status: open` in frontmatter — including any election event from step 2. If there are none *and* step 2 did nothing (no Elections mod, or no live race), say so in one short line and stop.

**4. For each open event, decide one of (this whole step is internal — the criteria-matching never reaches the player; only its narrative outcome does, in step 7):**

**a) Match.** One of the event's options' `acceptance_criteria` is satisfied by current snapshot state (or by the diff between this snapshot and the one when the event opened). Be honest about what counts:
- A criterion saying "tax_industrial drops by 2+" requires actually seeing the drop in `snapshot.city.budget.tax_industrial`. Compare against either the snapshot closest to the event's `date:` or, if that's not available, against a reasonable read of the value at open time stated in the body.
- A criterion saying "a new road segment ≥ X km appears in carto/processed/roads.md near <area>" requires the road to actually be there in `carto/processed/roads.md` now (or in `diff.named_buildings.added` if it's also a CustomName), in the area the criterion specifies. "A road got built somewhere" is not a match.
- A criterion saying "a new building tagged 'Convention Center' appears" requires that building to actually appear in `diff.named_buildings.added` or in the current `carto/processed/districts/<slug>.md`.
- **Election events resolve the moment the race concludes** — `politics.result.winner_name` is populated (or `diff.politics.election_concluded` fired, or the `in_world_deadline` election date has passed). Close it then regardless: match `resolved_via` to the option backing the **actual winner**; if the player backed a different candidate, it *still* closes — recorded as their bet failing, not left hanging. `politics.result` is machine-truth, so this is a clean match, not a judgment call.

If a (non-election) criterion is borderline — the player did something in the general direction but the snapshot doesn't fully show it — leave the event `open`. False positives are worse than false negatives; an event closing early on a misread denies the player the second swing.

When you do call a match: update the event's frontmatter:
- `status: resolved-by-player`
- `resolved_on:` = the current in-world date (from `clock.json`)
- `resolved_via:` = the matched option's `id`
- `consequences:` = 2–4 short bullets capturing what this means in fiction (who won, who lost, what changes downstream)

Then write the consequence canon (step 5 below).

**b) Timeout.** The event is `open`, no option matched, and `in_world_deadline` is past the current in-world date (from `clock.json`). The window closed. Update frontmatter:
- `status: resolved-by-timeout`
- `resolved_on:` = the current in-world date (from `clock.json`)
- `resolved_via: timeout`
- `consequences:` = 2–4 short bullets capturing what happens *because nothing happened* — the deal collapsed, the rival smelled blood, the offer expired, the political moment passed. The point of timeout is that ignoring an event has weight; don't let it be neutral. Lean the consequences toward whoever was on the losing side of inaction (usually the option-pushers in `pushed_by`; sometimes the opposed parties get a quiet win). (An election event won't normally time out — it matches on the result the moment the race concludes — but if the snapshot somehow lacks a result past election day, close it by timeout and read the winner on the next pass.)

Then write the consequence canon (step 5 below).

**c) Still open.** Neither matched nor expired. Leave the file as-is. Note it in the summary so the player knows it's still live.

**5. Propagate consequences.** For every event you just closed (resolved-by-player or resolved-by-timeout), update implicated canon:
- **`characters/*.md`** named in `participants`, `pushed_by`, or `opposed_by` — update `status`, `agenda`, `allies` / `adversaries` based on what just happened. A character who lost a contract isn't dormant; their agenda just changed. Keep `quick_read:` aligned with the new state and update the corresponding `canon/INDEX.md` entry.
- **`companies/*.md`** named in `participants`, etc. — same treatment (status, key_people, headcount-implied shifts).
- **`places/*.md`** — if a place was created or modified by the resolution, advance its `status` (planned → under-construction → existing, or planned → cancelled). Write a new `places/*.md` if the resolution made a brand-new place real (a new commercial center, a new park).
- **`factions/*.md`** — if a faction won / lost / fractured, update accordingly. **For a closed election:** the winning party records the win (`reputation`/`wins` from `politics.parties[]`); `politics.result` / `diff.politics.new_mayor` with `incumbent_party_held: false` means power changed hands — a bigger story than a re-election.
- **Mayor (closed election).** The winner's `characters/` entry becomes the sitting mayor — `status` and `agenda` shift to governing; the losers' agendas shift to opposition / next-cycle positioning. Confirm against `politics.mayor` on the next snapshot.
- **`secrets/*.md`** — did this event put pressure on a hidden fact? Advance `status` (hidden → suspected, etc.) if the resolution leaked something. If a secret flipped to `revealed`, write a separate `events/*.md` entry as `status: historical` for the leak itself.
- **New `events/*.md`** — sometimes a resolution spawns its own downstream event (e.g. a timeout where "the rival smells blood" implies a new move next session). If the downstream is concrete and immediate, write it now as `status: open` with its own options + deadline. Most resolutions don't spawn new events; only do this when the fiction genuinely demands it.

Keep propagation tight. Don't rewrite every character's full file because one event closed — touch the fields that actually changed.

**6. Optional narrative pieces.** For the most consequential resolutions (a major character's reversal, a faction's collapse, a secret breaking, an election night), draft a short `stories/*.md` entry — news clipping, council transcript, developer memo, concession/victory speech. Skip for routine closures. The session-end pass will pick up anything missed.

**7. Tell the player — as story, never as a status report.** This is the only part the player sees, and it's the easy place to slip into secretary voice. Everything from step 4 — the criteria, the field comparisons, the "matched / didn't match" verdicts — stays internal. What surfaces is the fiction. Plain in-character prose, no file references, no field names, no audit of whether each option's conditions were met (see CLAUDE.md "I tell story, never status").

- **What closed** — narrate the resolution as something that happened in the city, not as a criterion being satisfied. "The Highway 17 fight closed — Cascade got their road, and Pine Quarter's already organizing a noise lawsuit," *not* "the road-extension criteria matched." For an election: "Devereaux took it, 41 to 36 — the Reform slate's got the mayor's office for the first time, and the Civic Alliance is already knives-out over who lost it for them."
- **What expired** — narrate the cost of inaction. "The Conklin ranch sale died in committee; the family's split now, and the land's still sitting there."
- **What's still live** — this is the case that most often collapses into a status dump, because nothing changed and the instinct is to report that nothing changed. Don't. Don't walk the options and mark each one unmet. Give a short in-world recap of the standing tension: who's waiting on the player, what's at stake, what the deadline means dramatically — for a live race, who's ahead and how long until the vote. A "where things stand" beat in the ongoing story — one or two tight paragraphs at most, in the city's voice.

If nothing closed and nothing expired, I still never say "nothing happened." I remind the player what's pressing, as story, and leave the clock visible in-world ("Halina's got until mid-October before the board forces a vote") — exactly the ✅ example in CLAUDE.md "I tell story, never status."
