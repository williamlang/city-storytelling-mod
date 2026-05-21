---
description: Generate concrete story-driven gameplay choices with for/against framing
---

Produce 3–5 story-driven choices the player can act on in-game.

**Optional focus:** `$ARGUMENTS`

If a focus is supplied on the line above (a topic, character, faction, place, or theme — e.g. `transit`, `Halverson Civil`, `riverfront`, `scandal`), every proposal must engage with it. The "vary the mix" rule in step 2 relaxes: all options can orbit the focus, as long as they remain meaningfully distinct moves. Before presenting choices, state in one short line how you interpreted the focus so the player can redirect. If the focus line is empty, generate the broad mix as usual.

**1. Survey state silently:**
- Read `canon/INDEX.md` first — that's the navigation surface; from it you know what entities exist without loading every file. Pull the full files only for entities that look relevant to the moves you're considering.
- The playthrough premise in `canon/playthrough-premise.md` — every proposal should serve, test, or complicate this premise.
- The latest snapshot in `snapshots/` — what's actually happening in-game right now.
- Active characters and their agendas (load specific files via `read_file` based on INDEX hits).
- Live secrets in `secrets/` — read them; let them shape proposals. Whether to quote contents in chat follows `secrets_visibility` in `settings.json` (see CLAUDE.md "Secrets").
- Live arcs across characters/companies/factions/places — bias toward moves that *earn* the arc's outcome (no easy wins for `ascends`; tempting bad calls for `falls`).
- Recent events and the last session file (`sessions/`, not `sessions/archive/` unless something in an older month is directly load-bearing).
- Faction tensions, company expansions, planned places, open threads.

**2. Generate 3–5 choices.** Each one is a concrete in-world event that maps to a gameplay action. For each:

- **Title** — short label (e.g. "Halverson Civil pitches a riverfront tower", "Reuben Kowalski's dairy operation wants the south fields").
- **What happens** — 1–2 sentences of the in-world event.
- **Driven by** — character(s) or faction(s) pushing it; what they want; why now.
- **Against** — character(s) or faction(s) opposing it; what they stand to lose. Omit only if the move is genuinely uncontested.
- **In-game action** — concrete Cities: Skylines 2 move (zone X acres of medium density, run a road from A to B, build a fire station in district C, demolish Y, upgrade a service).

Vary the mix. Don't make all five real-estate plays. Cover at least three of: a new company opening, a farm or extractive operation, a real-estate development, a civic project (school, hospital, library, transit), a service expansion, a faction power move, a scandal-driven decision. At least one option must have meaningful opposition. *(If a focus argument was supplied, this variety rule relaxes — the focus replaces the mix as the unifying constraint, but the "meaningful opposition" requirement still holds.)*

**3. Present** via `AskUserQuestion` (single-select). For each option:
- `label`: the title (short).
- `description`: in-game action + a "Pushed by X · opposed by Y" line.

**4. After the player picks**, write the setup canon for the chosen option only:
- Create new entities (character, company, faction, place) using the templates in CLAUDE.md if any are required. Ask about an `arc:` for any new major entity.
- New `places/` entries land at `proposed` or `planned` status — they advance to `under-construction` / `existing` only via `/session-end` once the player actually builds them.
- Write an `events/` entry for the moment of decision (the deal, the announcement, the council vote, the groundbreaking-to-be).
- Update implicated characters' status / agenda / adversaries.
- Tell the player concretely what to do in-game ("zone ~12 acres of medium density along the river east of Halverson Park; build the access road from 7th Ave first").

Do **not** write canon for the unselected options. They're discarded unless the player later asks to revive one.
