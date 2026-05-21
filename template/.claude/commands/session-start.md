---
description: Open a session — state scan + checklist of opening tasks
---

Run the session-start checklist for this city.

**1. Quick state scan** (brief, internal — don't dump to the user yet):
- Read `canon/INDEX.md` first — that's the navigation surface; from it you know what entities exist without loading every file.
- Read `canon/city.md`, `canon/era.md`, `canon/tone.md` if present (small, always-load world canon).
- Most recent file in `sessions/` (recent ones only — older sessions live compressed in `sessions/archive/`; skim that index lazily, only pulling specific months if relevant).
- Latest snapshot in `snapshots/` for the current in-game state.
- Unpopulated scaffold features:
  - Is `canon/playthrough-goal.md` missing or empty?
  - Is `secrets/` missing or empty?
  - Are `arc:` blocks missing from major characters, companies, factions, or places?
  - Are CLAUDE.md frontmatter fields (including `quick_read:`) missing from existing entity files?
- Pull individual `characters/*.md` etc. only when an entity is directly relevant — INDEX.md tells you what's there without the cost.

**2. Report** in 3–5 lines: where the city was left off, and any scaffold gaps you found.

**3. Ask** what the player wants to do this session, using `AskUserQuestion`. Offer these options:

- **Check for new scaffold features** — run the **Scaffold arrival** backfill defined in CLAUDE.md (prompt arcs, generate secrets silently). *Lead with this option and mark it Recommended if step 1 detected any gap.*
- **Pre-session planning** — propose 1–3 narratively-motivated gameplay objectives based on active agendas, recent events, and any live secrets.
- **Post-session recording** — the player is about to describe what happened in-game; record it into `sessions/` and propagate consequences.
- **Continuity question / lookup** — answer a question about who runs what, prior deals, etc.
- **Generate a new entity** — invent a character, company, faction, or place hooked into existing canon.

Only after the player picks do you start the corresponding workflow.
