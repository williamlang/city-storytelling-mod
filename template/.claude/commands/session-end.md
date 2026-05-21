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
- **Check `secrets/`.** Did anything this session put pressure on a hidden fact? Update `status` (hidden → suspected if rumors started; suspected → partially-revealed if a leak landed; → revealed if it broke fully). If a secret flipped to `revealed`, write the corresponding `events/` entry and update implicated entity files. Whether to quote unrevealed secret content to the player follows `secrets_visibility` in `settings.json` (see CLAUDE.md "Secrets").
- **Check for a city level-up.** Diff `city.milestone_level` between the latest snapshot in `snapshots/` and the most recent prior snapshot. If it rose, the city hit a milestone this session (which in CS2 means a funds influx + new unlocks). Always record the advance as a one-line bullet in the session log. Then check `levelup_storylines` in `settings.json`:
  - If `true`: write an `events/` entry for the milestone advance (the council vote / budget allocation / unlock decision) and a short narrative piece in `stories/` (council transcript, news clipping, developer memo) about who pushed for what spending, who lost out, the political fallout. Ground the storyline in active characters/factions and the playthrough premise — don't generate generic boilerplate. Update implicated `characters/` / `companies/` (e.g., a developer who won the contract gets a status / agenda update).
  - If `false`: stop after the session-log bullet. No `events/` or `stories/` entry for this milestone.
- Optionally draft short narrative pieces (news clipping, council transcript, developer email) into `stories/` for the most consequential moments.

**5. Summarize for the player**: files added/modified, events written, time advanced, and a count of any secrets whose status shifted. Quoting hidden content in the summary depends on `secrets_visibility` (see CLAUDE.md "Secrets").

**6. Propose a commit message** in the existing style — "Advance <city> canon — session N <short title>" — and confirm before committing.

**7. Ask** if the player wants to immediately set up the next session via `/story-driven`, or close out here.
