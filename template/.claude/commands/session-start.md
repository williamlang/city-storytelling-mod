---
description: Open a session — state scan + checklist of opening tasks
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
- Read `canon/city.md`, `canon/era.md`, `canon/tone.md` if present (small, always-load world canon).
- Most recent file in `sessions/` (recent ones only — older sessions live compressed in `sessions/archive/`; skim that index lazily, only pulling specific months if relevant).
- Latest snapshot in `snapshots/` for the current in-game state.
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

**4. Ask** what the player wants to do this session, using `AskUserQuestion`. Offer these options:

- **Check for new scaffold features** — run the **Scaffold arrival** backfill defined in CLAUDE.md (infer premise if missing, generate arcs and secrets silently). *Lead with this option and mark it Recommended if step 1 detected any gap.*
- **Pre-session planning** — propose 1–3 narratively-motivated gameplay objectives based on active agendas, recent events, and any live secrets.
- **Post-session recording** — the player is about to describe what happened in-game; record it into `sessions/` and propagate consequences.
- **Continuity question / lookup** — answer a question about who runs what, prior deals, etc.
- **Generate a new entity** — invent a character, company, faction, or place hooked into existing canon.

Only after the player picks do you start the corresponding workflow.
