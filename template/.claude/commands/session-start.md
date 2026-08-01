---
description: Open a session — state scan + checklist of opening tasks
order: 20
---

Run the session-start checklist for this city.

**0. Batch the opening reads (do this first — one turn, in parallel).**

Every path here is fixed, so issue all of them together rather than one at a time (see CLAUDE.md "Opening reads"): `clock.json`, `canon/INDEX.md`, `canon/city.md`, `canon/era.md`, `canon/tone.md`, `canon/playthrough-premise.md`, `settings.json`, and a listing of `sessions/*.md` (needed for the next session number in step 2). Then step 1 reads what `clock.json` pointed at.

**Open-session check.** `clock.json`'s `open_session` field is the answer — the mod resolves it on every heartbeat from the most recent session file's frontmatter:

- **A path** (e.g. `sessions/S07-2026-07-12-open.md`) → a session is still open. Read it to get its `real_date` and `session:` number, then stop here and tell the player:
  > "Session N from `<real_date>` is still open. Run `/session-end` on it first to record what happened, then re-invoke `/session-start`."
  Do not proceed to step 1 until the prior session is closed.
- **`null`** → the most recent session is closed, or this is the city's first session. Continue.
- **`clock.json` missing or without the field** (older mod build, or the game hasn't run since this folder was scaffolded) → fall back to the manual check: highest `SXX` in `sessions/`, open if its frontmatter has no `ended_real_date` (or it's blank).

The mod's auto-start-on-save-load setting may have already written the stub for this session before you got here — that's expected, and it's why `open_session` can be non-null on a perfectly normal opener. Treat it the same as if you'd written it yourself: a path means there's an active session; `null` means there isn't.

**1. Quick state scan** (brief, internal — don't dump to the player yet). The canon files from step 0's batch cover most of this; the reads that *do* belong here are the ones whose paths came out of `clock.json` — issue those as a single second batch:
- `canon/INDEX.md` (from step 0) is the navigation surface; from it you know what entities exist without loading every file.
- `canon/city.md`, `canon/era.md`, `canon/tone.md` (from step 0, if present — small, always-load world canon). Treat `city.md`'s `region:` as a hard constraint on naming and cultural grounding, and `tone.md`'s narrative focus lenses as the bias for what *kind* of opening objectives to propose (citizens → human-scale/neighborhood; civic → systems/economy/politics).
- The most recent session file — the one `clock.json`'s `open_session` names, or the highest `SXX` from step 0's listing when it's `null` (recent ones only — older sessions live compressed in `sessions/archive/`; skim that index lazily, only pulling specific months if relevant).
- The latest snapshot — `clock.json`'s `latest_snapshot` path — for the current in-game state. Don't list `snapshots/` to find it.
- **Live election check (Elections mod).** If the latest snapshot's `politics` block is non-null, a mayoral race is running. Verify it's reflected in canon: are the candidates in `characters/`, the parties in `factions/`, and is there an open `type: election` event for this cycle (`politics.schedule.mayor_term_year`)? If any of that is missing — e.g. Elections came online mid-playthrough and the ballot never made it into the story — flag it as a gap and run the `/events-resolve` "Election cycle" step to seed the cast and open the event. (Don't let a race run for sessions without the cast existing in canon.)
- Unpopulated scaffold features:
  - Is `canon/playthrough-premise.md` missing or empty?
  - Is `secrets/` missing or empty?
  - Are `arc:` blocks missing from major characters, companies, factions, or places?
  - Are CLAUDE.md frontmatter fields (including `quick_read:`) missing from existing entity files?
- Pull individual `characters/*.md` etc. only when an entity is directly relevant — INDEX.md tells you what's there without the cost.

**2. Create (or claim) the session stub.**

Determine the next session number `N` from the `sessions/*.md` listing step 0 already fetched — add 1 to the highest `SXX`. (This is the one listing a normal opener genuinely needs, which is why it rides along in step 0's batch instead of costing its own round-trip here.) If the mod already wrote `sessions/SXX-YYYY-MM-DD-open.md` for this session (auto-start enabled), use that file — don't create a duplicate. Otherwise create it now with this frontmatter:

```yaml
---
session: N
real_date: <today's real-world date>
in_world_window: TBD
---

(Session in progress — filled in by /session-end.)
```

Filename: `sessions/SXX-YYYY-MM-DD-open.md` (where `SXX` is zero-padded if you've been padding them; otherwise match the style of existing files).

The lack of an `ended_real_date:` field is what marks this session as open. `/session-end` will add it and rename the file.

**3. Report** in 3–5 lines: where the city was left off, and any scaffold gaps you found.

**4. Ask** what the player wants to do this session, in plain prose. One sentence lead, then numbered options. If step 1 detected a scaffold gap, mark the backfill option recommended and put it first:

1. **Check for new scaffold features** — run the Scaffold arrival backfill (infer premise if missing, generate arcs and secrets silently).
2. **Pre-session planning** — propose 1–3 narratively-motivated gameplay objectives based on active agendas, recent events, and live secrets.
3. **Post-session recording** — the player is about to describe what happened in-game; record it into the session log and propagate consequences.
4. **Continuity question / lookup** — answer a question about who runs what, prior deals, etc.
5. **Generate a new entity** — invent a character, company, faction, or place hooked into existing canon.

End with "Reply with the number, or describe what you want." Wait for the player's reply, then start the corresponding workflow.
