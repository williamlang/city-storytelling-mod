---
description: Close a session — record what happened, propagate consequences, commit
---

Run the session-end checklist.

**1. Ask** the player what happened this session:
- What did you build / zone / change in-game?
- Any milestones (population thresholds, new districts unlocked, services failing or expanding)?
- Did any `proposed` / `planned` canon become real (a planned farm got built; a pitched tower broke ground)? Did anything proposed get abandoned?
- Anything visually distinct worth a screenshot reference?

**2. Propose the in-world time window** (default 2–6 months per real-world session) and confirm with the player.

**3. Record** to `sessions/SXX-YYYY-MM-DD-title.md` using the session frontmatter. Number SXX from the last session file. Fill `## What I built in-game`, `## Story consequences`, `## Open threads`.

**4. Propagate consequences**:
- Advance `places/` status (planned → under-construction → existing). Update `built:` dates.
- Update affected `characters/` (status, agenda, allies/adversaries) and `companies/` (status, key_people).
- Add `events/` entries for milestones (openings, deals, scandals, groundbreakings, ribbon-cuttings).
- **Check `secrets/`.** Did anything this session put pressure on a hidden fact? Update `status` (hidden → suspected if rumors started; suspected → partially-revealed if a leak landed; → revealed if it broke fully). If a secret flipped to `revealed`, write the corresponding `events/` entry and update implicated entity files. Do not quote unrevealed secret content to the player.
- Optionally draft short narrative pieces (news clipping, council transcript, developer email) into `stories/` for the most consequential moments.

**5. Summarize for the player**: files added/modified, events written, time advanced, and a count of any secrets whose status shifted (without quoting hidden content).

**6. Propose a commit message** in the existing style — "Advance <city> canon — session N <short title>" — and confirm before committing.

**7. Ask** if the player wants to immediately set up the next session via `/story-driven`, or close out here.
