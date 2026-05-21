---
description: Compress old session files into monthly summaries to bound canon growth
---

Compress sessions older than ~2 in-world months into monthly archive summaries so the agent doesn't re-read the full playthrough log on every run.

**1. Survey state silently:**
- Current in-world date — pull it from the latest snapshot or the most recent session file.
- All files in `sessions/` (excluding `sessions/archive/`).
- Existing archive files in `sessions/archive/` so you know what's already compressed.

**2. Identify what to compress.** A session file qualifies if its in-world date is more than **2 in-world months** before the current in-world date. Group qualifying files by their in-world month (the month *the session covered*, not the real-world session date).

If nothing qualifies, report "Nothing to archive — recent sessions still load-bearing" and stop.

**3. For each in-world month with qualifying files**, write `sessions/archive/YYYY-MM.md`:

```markdown
---
in_world_month: 2026-04
session_count: 3
sessions_archived: [S04-2026-05-17-..., S05-..., S06-...]
---

# YYYY-MM Summary

One-paragraph overview of what this month was *about* in-world.

## Session-by-session (one short paragraph each)

- **S04 (in-world: 2026-04-02 → 2026-04-30)** — 2–3 sentences: what the player built, the major in-world events that resulted, anything that changed an entity's status / arc.
- **S05 …**
- **S06 …**

## Entities touched this month
- characters/maria-chen.md — first appearance, started the Friendship Park push
- companies/halverson-civil.md — won the riverfront contract
- places/westside.md — flipped from `planned` to `under-construction`

## Threads opened (still active)
- [Reference threads still relevant — these stay queryable for future sessions.]

## Threads closed
- [Reference threads that resolved this month — referenced for posterity, no longer load-bearing.]
```

**4. Delete the original full-fidelity session files** for the month. The archive file is the canonical record from this point. If the player has the city dir under their own git, history is preserved there — don't worry about it being lost.

**5. Report** which months were compressed and which sessions made it into each archive file. Keep recent sessions (last 2 in-world months) full-fidelity — they're still load-bearing context for the next play session.

**Do not compress** anything else. Don't touch `characters/`, `companies/`, `places/`, `factions/`, `events/`, `stories/`, or `secrets/` — only `sessions/*.md`. Those other directories are the load-bearing canon; sessions are the playthrough log, which is the only thing that grows unboundedly.
