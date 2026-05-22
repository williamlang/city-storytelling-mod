---
description: Bootstrap a new city — name, founding history, map, inferred premise
order: 10
---

Start a new playthrough. By the end, this city folder has `canon/city.md` and `canon/playthrough-premise.md` populated enough that `/session-start` can take over.

The city folder itself already exists — the CityStoryMod scaffolded it (with this template) the first time it exported a snapshot for this city. `/new-city` runs inside that folder; it does not create one.

**1. Verify the folder is fresh.**

Read `canon/city.md`. If it's still the unmodified template stub (no chosen name, no chosen founding history), proceed. If it already has a real name and history filled in, stop and ask the player whether they want to overwrite — `/new-city` is meant to run once per city, before any session.

**2. Ask for the map screenshot.**

Ask the player for the absolute path to a Cities: Skylines 2 map screenshot of the starting tile (typically under `E:\Steam\userdata\...\screenshots\`). The save may still have a placeholder name in-game at this point — that's fine, the rename happens at the end. Confirm the file exists before continuing. If they don't have one yet, stop and tell them to capture one and re-run — do not proceed without the map.

**3. Read the map.**

Use the `Read` tool on the screenshot path to view the image. Note the dominant geographic features in 2–4 lines (internal — don't dump to the player yet):
- Coastline, lake, river, mountain range, plains, peninsula, island, valley.
- Approximate orientation of major water and terrain.
- Anything distinctive (a sharp bend in a river, a sheltered bay, a ridge cutting the map in two).

These features anchor every subsequent suggestion. **No magic, no fantasy geography** — describe what's actually visible.

**4. Suggest a name.**

Generate **4 plausible North American city names** grounded in the visible geography and consistent with the style guide:
- Real-sounding: founders' surnames, geographic features, Indigenous place names (used respectfully and grounded in the actual region the geography suggests), industrial-heritage names. No joke names.
- Each name implies a slightly different founding story — give the player meaningful range (e.g. an old port name vs. a mill-town name vs. a railroad-junction name vs. an agricultural-county-seat name).

Present via `AskUserQuestion` (single-select). For each option:
- `label`: the proposed name.
- `description`: one short phrase on what kind of place this name suggests (e.g. "Old Great Lakes shipping port — Scottish-immigrant founders").

**5. Suggest a founding history.**

After the player picks a name, generate **3–4 founding-history options** grounded in *both* the chosen name and the map. Each option is a one-paragraph capsule covering:
- Founding era and original economic engine (rail, mill, port, mine, military base, agricultural hub, etc.).
- What the 20th century did to it (boom, decline, suburban sprawl, deindustrialization, reinvention).
- The region of North America the geography places it in (Great Lakes, Pacific Northwest, Sunbelt, Atlantic Coast, Prairies, Appalachia, etc.).

Vary the options meaningfully — don't propose four flavors of the same rust-belt story. At least one option should suggest a non-industrial origin if the geography allows it.

Present via `AskUserQuestion` (single-select). For each option:
- `label`: short tag (e.g. "Old timber port, postwar decline", "Railroad junction, agricultural seat").
- `description`: the founding-history paragraph itself (kept tight — 2–3 sentences).

**6. Save the map.**

Copy the screenshot to `maps/<slug>-overview.<ext>` where `<slug>` is the kebab-case city name (e.g. `port-haldane-overview.png`). Create `maps/` if it doesn't exist. Keep the original in the Steam screenshots folder — don't move it.

**7. Write `canon/city.md` and infer the playthrough premise.**

Overwrite the scaffold `canon/city.md` with:

- Frontmatter: `name`, `region`, `founded` (year), `geography` (one-line phrase derived from the map read). Leave `population_at_start` and `climate` as `TBD` — those get pinned during session 1.
- `## Name` — the chosen name, one sentence on what it means / where it comes from.
- `## Where it is` — region + the geographic features you observed, written as prose. Reference the map by filename.
- `## How it got here` — the chosen founding-history paragraph, expanded to 4–8 sentences with specifics (a named founding family or company, a decade-defining event, the trajectory through the 20th century).
- `## What it is now` — leave a short stub: *"To be pinned at session 1: starting population, dominant industries today, major employers, the city's regional reputation."*
- `## The defining tension at session 1` — leave a stub: *"To be set at session 1 — see `/session-start`."*

Then **infer the playthrough premise** silently from the chosen founding history, the map observations from step 3, and the chosen name. Write the resulting one-sentence (or short paragraph) premise to `canon/playthrough-premise.md` as plain prose. See the "Playthrough premise" section in CLAUDE.md for the inference inputs and heuristics. Do **not** ask the player to author it — surface the result in the hand-off (step 9), not before.

Do **not** invent characters, companies, places, factions, or events here. This command only writes the foundational geography, history, and inferred premise. Everything else flows from `/session-start` and `/story-driven`.

**8. Ask about feature toggles.**

Batch both toggles into a single `AskUserQuestion` call (one tool call, two questions):

**Question 1** (header: "Secrets", single-select):
- Question: "Should hidden secrets be visible to you, or kept blind until they break in-story?"
- Option 1 (Recommended) — label: `Hidden (Recommended)`, description: "Secrets are generated and shape the story, but you never see their contents in chat until they're revealed in-world. Closer to playing blind."
- Option 2 — label: `Shown`, description: "Same secrets get generated, but I freely show their contents to you. Author / editor mode — you see the engine driving the city."

**Question 2** (header: "Level-up stories", single-select):
- Question: "When the city levels up in-game (milestone advance + funds influx), should I generate a storyline about how the new money gets spent and fought over?"
- Option 1 (Recommended) — label: `Enabled (Recommended)`, description: "On every milestone advance detected in the snapshot diff, I write an event + short narrative piece (council transcript, news clipping, developer memo) about who pushed for what spending, who lost out, the political fallout. Surfaces the story behind a moment the game otherwise treats silently."
- Option 2 — label: `Disabled`, description: "Milestone advances are still recorded in the session log, but I don't generate dedicated stories. Use this if the storyline noise feels like too much."

Map the answers to settings:
- `secrets_visibility`: `"hidden"` or `"shown"`
- `levelup_storylines`: `true` (Enabled) or `false` (Disabled)

These get written to `settings.json` in the next step and are read on every session.

**9. Write `settings.json`.**

Write `settings.json` at the city-folder root using `settings.sample.json` as the shape, with:
- `secrets_visibility` set to the secrets answer from step 8.
- `levelup_storylines` set to the level-up-stories answer from step 8 (`true` for Enabled, `false` for Disabled).
- `bootstrapped` set to `true`. This is the signal that `/new-city` has run for this city; the CityStoryMod prompt panel reads it to know when to hide the `/new-city` button. A new scaffold ships with `bootstrapped: false`; flipping it at the end of this step is the last thing `/new-city` does.

**10. Hand off.**

Tell the player concretely:
- **The inferred playthrough premise** — quote the one-sentence premise verbatim and note that it lives in `canon/playthrough-premise.md`. Mention they can ask you to revise it (or edit the file directly) before session 1; everything else (arcs, secrets) flows from it.
- **Rename their CS2 save to "`<City Name>`"** so the mod's future exports keep flowing into this same city folder. Until this rename happens in-game, exports may land under the placeholder slug.
- What's still TBD in `canon/city.md` (population, climate, defining tension).
- That the next step is `/session-start` — session 1 will pin the remaining `canon/city.md` stubs, and Scaffold arrival (arcs → secrets) runs as needed.

Do **not** run `/session-start` automatically. The player decides when to start the first session.
