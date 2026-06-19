---
description: Open a session — state scan + checklist of opening tasks
order: 20
---

Run the session-start checklist for this city.

**0. Open-session check (do this first).**

Scan `sessions/` for the most recent session file (highest `SXX`). Read its frontmatter:

- If it has no `ended_real_date` field (or `ended_real_date:` is blank), a prior session is still open — the player closed CS2 (or this conversation) before running `/session-end`. Stop here and tell the player:
  > "Session N from `<real_date>` is still open. Run `/session-end` on it first to record what happened, then re-invoke `/session-start`."
  Do not proceed to step 1 until the prior session is closed.
- If it has `ended_real_date:` set, the prior session is closed — continue.
- If `sessions/` is empty (first session of this city), continue.

The mod's auto-start-on-save-load setting may have already written the stub for this session before you got here — that's expected. Treat it the same as if you'd written it yourself: an open stub at the front of `sessions/` means there's an active session; a closed file means there isn't.

**1. Quick state scan** (brief, internal — don't dump to the player yet):
- Read `canon/INDEX.md` first — that's the navigation surface; from it you know what entities exist without loading every file.
- Read `canon/city.md`, `canon/era.md`, `canon/tone.md` if present (small, always-load world canon). Treat `city.md`'s `region:` as a hard constraint on naming and cultural grounding, and `tone.md`'s narrative focus lenses as the bias for what *kind* of opening objectives to propose (citizens → human-scale/neighborhood; civic → systems/economy/politics).
- Most recent file in `sessions/` (recent ones only — older sessions live compressed in `sessions/archive/`; skim that index lazily, only pulling specific months if relevant).
- Latest snapshot in `snapshots/` for the current in-game state.
- **Live election check (Elections mod).** If the latest snapshot's `politics` block is non-null, a mayoral race is running. Verify it's reflected in canon: are the candidates in `characters/`, the parties in `factions/`, and is there an open `type: election` event for this cycle (`politics.schedule.mayor_term_year`)? If any of that is missing — e.g. Elections came online mid-playthrough and the ballot never made it into the story — flag it as a gap and run the `/events-resolve` "Election cycle" step to seed the cast and open the event. (Don't let a race run for sessions without the cast existing in canon.)
- Unpopulated scaffold features:
  - Is `canon/playthrough-premise.md` missing or empty?
  - Is `secrets/` missing or empty?
  - Are `arc:` blocks missing from major characters, companies, factions, or places?
  - Are CLAUDE.md frontmatter fields (including `quick_read:`) missing from existing entity files?
- Pull individual `characters/*.md` etc. only when an entity is directly relevant — INDEX.md tells you what's there without the cost.

**2. Create (or claim) the session stub.**

Determine the next session number `N` by scanning existing `sessions/*.md` filenames and adding 1 to the highest. If the mod already wrote `sessions/SXX-YYYY-MM-DD-open.md` for this session (auto-start enabled), use that file — don't create a duplicate. Otherwise create it now with this frontmatter:

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
