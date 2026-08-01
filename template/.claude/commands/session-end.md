---
description: Close a session — record what happened, propagate consequences, mark the session file closed
order: 40
---

Run the session-end checklist.

**0a. Resolve open events first.** Run the `/events-resolve` flow (see `.claude/commands/events-resolve.md`) inline as the first step. Any `events/*.md` whose acceptance criteria the snapshot now satisfies closes as `resolved-by-player`; anything past `in_world_deadline` closes as `resolved-by-timeout` with the consequence canon written. This shapes what's left for the player to describe in step 1 — they don't need to re-narrate things that already auto-resolved, only the parts that *weren't* covered by an open event. Summarize the resolutions in one line for the player after the pass.

**0b. Find the open session.**

`clock.json`'s `open_session` field names it directly — a city-dir-relative path to the open stub (typically `sessions/SXX-YYYY-MM-DD-open.md`), created either by `/session-start` or by the mod's auto-start-on-save-load setting. Don't list `sessions/` to find it; the read of `clock.json` belongs in step 0a's batch anyway, since `/events-resolve` needs its `in_world_date` and `latest_snapshot`.

- If `open_session` is a path, that's the file you'll be updating in steps 3–5 below.
- If `open_session` is `null`, there is no open session to close. Tell the player and stop — they likely meant to run `/session-start` first.
- If `clock.json` is missing or has no `open_session` field (older mod build), fall back to the manual check: most recent `SXX` in `sessions/`, open if its frontmatter has no `ended_real_date:`. Empty `sessions/` → nothing to close, tell the player and stop.

**1. Ask** the player what happened this session — *beyond* what step 0a already auto-resolved. Frame the question around the gap: "Anything else worth recording that wasn't part of [the resolved events from 0a]?" Specifically prompt for:
- Things they built / zoned / changed that didn't match an open event's acceptance criteria.
- Any milestones (population thresholds, new districts unlocked, services failing or expanding).
- Whether any `proposed` / `planned` canon outside the resolved events became real or got abandoned.
- Anything visually distinct worth a screenshot reference.

If the 0a pass already covered the whole session, this prompt can be a one-line check ("anything else?") rather than a full questionnaire.

**2. Propose the in-world time window** (default 2–6 months per real-world session) and confirm with the player.

**3. Record** into the open session file. Update its frontmatter:
- Keep `session:`, `real_date:` as-is.
- Set `in_world_window:` (e.g. `2026-03 → 2026-06`).
- Add `ended_real_date: <today>` — this is the marker that flips the file from open to closed. The presence of this field is what the next `/session-start` checks to know the prior session is wrapped.

Then fill the body: `## What I built in-game`, `## Story consequences`, `## Open threads`.

**4. Propagate consequences**:
- Advance `places/` status (planned → under-construction → existing). Update `built:` dates.
- Update affected `characters/` (status, agenda, allies/adversaries) and `companies/` (status, key_people).
- Add `events/` entries for things that happened in-game *without* a corresponding open event — openings, deals, scandals, groundbreakings, ribbon-cuttings the player just describes. These are `status: historical`: no `options`, no `in_world_deadline`, no resolution loop. (Events that *were* covered by an open proposal already got their canon written in step 0a — don't double-record them.) **If Custom Chirps is enabled** (`"customchirps"` in `settings.json.integrations`), surface the one or two of these that a real city feed would actually carry — a notable opening, a deal, a scandal breaking — as chirps in `chirp-requests.json` (see CLAUDE.md "Chirping the city"). Restraint, not volume: skip routine bookkeeping events, and don't chirp anything already chirped when it was proposed. Skip entirely if the integration is off.
- **Check `secrets/`.** Did anything this session put pressure on a hidden fact? Update `status` (hidden → suspected if rumors started; suspected → partially-revealed if a leak landed; → revealed if it broke fully). If a secret flipped to `revealed`, write the corresponding `events/` entry and update implicated entity files. Whether to quote unrevealed secret content to the player follows `secrets_visibility` in `settings.json` (see CLAUDE.md "Secrets").
- **Elections (if the integration is enabled).** If `"elections"` is in `settings.json.integrations` and `snapshot.politics` is present (if the integration is off, skip this bullet entirely — see CLAUDE.md "Peer-mod integration gate"), the election cycle — candidate/party seeding, a new mayor, a concluded race, scandal pressure — was already handled by the `/events-resolve` "Election cycle" step in 0a. Here, just fold the political shift into the session log and `## Story consequences` (who took power, what it means for active arcs) and make sure implicated `characters/` / `factions/` / `secrets/` are consistent. Don't re-record events 0a already wrote.
- **Check for a city level-up.** Diff `city.milestone_level` between the latest snapshot (`clock.json`'s `latest_snapshot`) and the most recent prior snapshot. This is the one place a `snapshots/` listing is genuinely needed — the *prior* file has no pointer — so glob it once here and read both in one batch. If it rose, the city hit a milestone this session (which in CS2 means a funds influx + new unlocks). Always record the advance as a one-line bullet in the session log. Then check `levelup_storylines` in `settings.json`:
  - If `true`: write a `status: historical` `events/` entry for the milestone advance (the council vote / budget allocation / unlock decision) and a short narrative piece in `stories/` (council transcript, news clipping, developer memo) about who pushed for what spending, who lost out, the political fallout. Ground the storyline in active characters/factions and the playthrough premise — don't generate generic boilerplate. Update implicated `characters/` / `companies/` (e.g., a developer who won the contract gets a status / agenda update).
  - If `false`: stop after the session-log bullet. No `events/` or `stories/` entry for this milestone.
- Optionally draft short narrative pieces (news clipping, council transcript, developer email) into `stories/` for the most consequential moments.

**4b. Name new civic buildings.** Read `snapshot.services.civic_buildings` and find entries with `has_custom_name: false` — civic buildings the player has placed but not named. Give the ones worth naming real names (see CLAUDE.md "Naming civic buildings" for conventions: grounded, scale-appropriate, story-significant where canon wants it, plain-geographic otherwise; parks when the story has a reason, skip pure-flavor courts). Write the chosen names to `naming-requests.json` at the city root as a JSON array of `{ "id": "<civic_buildings id>", "name": "<name>" }`, then confirm via `naming-results.json`. Where a name plants or pays off canon (a school named for a character, a park for an event), write the matching canon/event entry too, anchored to the building's coordinate once it's in the carto chunks. Narrate the results as story — the city now calls the place X — never the file mechanism. Skip buildings already named.

**5. Rename the session file** to reflect its title now that you have one. Pick a short kebab-case title from what happened (e.g. `riverfront-rezoning`, `stadium-vote`) and rename `sessions/SXX-YYYY-MM-DD-open.md` → `sessions/SXX-YYYY-MM-DD-<title>.md`. This is purely cosmetic — the `ended_real_date` field is the actual closed marker — but it keeps `sessions/` scannable.

**6. Summarize for the player**: files added/modified, events written, time advanced, and a count of any secrets whose status shifted. Quoting hidden content in the summary depends on `secrets_visibility` (see CLAUDE.md "Secrets").

**7. Ask** if the player wants to immediately set up the next session via `/story-driven`, or close out here.
