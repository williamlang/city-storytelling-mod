---
description: Open a session — state scan + checklist of opening tasks
---

Run the session-start checklist for this city.

**1. Quick state scan** (brief, internal — don't dump to the user yet):
- Current branch and last 5–8 commits.
- Most recent file in `sessions/`.
- Unpopulated scaffold features:
  - Is `canon/playthrough-goal.md` missing or empty?
  - Is `secrets/` missing or empty?
  - Are `arc:` blocks missing from major characters, companies, factions, or places?
  - Are CLAUDE.md frontmatter fields missing from existing entity files?

**2. Report** in 3–5 lines: where the city was left off, and any scaffold gaps you found.

**3. Ask** what the player wants to do this session, using `AskUserQuestion`. Offer these options:

- **Check for new scaffold features** — run the **Scaffold arrival** backfill defined in CLAUDE.md (prompt arcs, generate secrets silently). *Lead with this option and mark it Recommended if step 1 detected any gap.*
- **Pre-session planning** — propose 1–3 narratively-motivated gameplay objectives based on active agendas, recent events, and any live secrets.
- **Post-session recording** — the player is about to describe what happened in-game; record it into `sessions/` and propagate consequences.
- **Continuity question / lookup** — answer a question about who runs what, prior deals, etc.
- **Generate a new entity** — invent a character, company, faction, or place hooked into existing canon.

Only after the player picks do you start the corresponding workflow.
