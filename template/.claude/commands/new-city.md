---
description: Bootstrap a new city — name, founding history, map, inferred premise
order: 10
---

Start a new playthrough. By the end, this city folder has `canon/city.md` and `canon/playthrough-premise.md` populated enough that `/session-start` can take over.

The city folder itself already exists — the CityStoryMod scaffolded it (with this template) the first time it exported a snapshot for this city. `/new-city` runs inside that folder; it does not create one.

**1. Verify the folder is fresh.**

Read `canon/city.md`. If it's still the unmodified template stub (no chosen name, no chosen founding history), proceed. If it already has a real name and history filled in, stop and ask the player whether they want to overwrite — `/new-city` is meant to run once per city, before any session.

**2. Read the spatial data the mod already collected.**

The CityStoryMod has already exported a first snapshot and (if Carto is installed) the Carto chunks by the time the player invokes `/new-city`. Pull all of it before asking anything:

- Latest `snapshots/snapshot-*.json` — extract the `map.*` block: `name`, `theme`, `latitude`, `longitude`, `temperature_min_c`, `temperature_max_c`, `cloudiness`, `precipitation`. These are *world-identity* signals — latitude+temperature alone tell you whether this is boreal, temperate, or Mediterranean.
- `carto/processed/index.md` — footprint dimensions (km × km), road-network summary, and the named-decoration list (cairns, ruins, monuments). The decoration repeats often carry implicit history.
- `carto/processed/elevation.md` — the one-line terrain reading ("Mostly flat", "Hilly, with a localized high point", "Rugged / mountainous"), relief, and which quadrant holds the high ground.
- `carto/processed/water.md` — the one-line water reading ("Heavy water — complex lake district", "Essentially landlocked", etc.) and per-quadrant water share.
- `carto/processed/roads.md` — the named highways/bridges. These carry the regional flavor (a "Cypress Highway" + "Mulberry Lane" + four named bridges over water suggests an established settler-era road system).

Synthesize these into 4–6 lines internally — don't dump to the player yet. This is the **primary** geographic anchor. If any chunk is missing (older save, Carto not installed), proceed with whatever is available; the snapshot at minimum always exists.

**3. Ask for the map screenshot — secondary visual signal.**

First, check `maps/` in the city folder. If a file matching `*-overview.*` already exists (the mod may have captured it automatically), use it without prompting the player. Otherwise, ask the player for the absolute path to a Cities: Skylines 2 map screenshot of the starting tile (typically under `E:\Steam\userdata\...\screenshots\`). The save may still have a placeholder name in-game at this point — that's fine, the rename happens at the end. Confirm the file exists before continuing. If they don't have one, skip this step and proceed using only the spatial data from step 2.

Use the `Read` tool on the screenshot to add what the rasters and vectors don't capture:
- Biome look (forest density, vegetation type, urban vs. wild texture).
- Specific landform shapes (cliff faces, peninsula curves, river bends, bay outlines).
- Anything that contradicts the spatial data — if it does, **trust your eyes over the classifier** and note the disagreement.

These visual signals augment but don't replace step 2. **No magic, no fantasy geography** — describe what's actually present in the data and image.

**4. Suggest a name.**

Generate **4 plausible North American city names** grounded in the spatial data (step 2) and the screenshot (step 3), consistent with the style guide:
- Real-sounding: founders' surnames, geographic features, Indigenous place names (used respectfully and grounded in the actual region the geography + climate suggests — a 62°N boreal lake district reads Minnesota / Manitoba / interior BC; a 37°N Mediterranean archipelago reads coastal California / Pacific Mexico), industrial-heritage names. No joke names.
- Each name implies a slightly different founding story — give the player meaningful range (e.g. an old port name vs. a mill-town name vs. a railroad-junction name vs. an agricultural-county-seat name).
- Lean on signals only the data carries — the CS2 map name itself (e.g. "Archipelago", "Lakeland") is *not* canon and the player won't see it in-world, but it's a hint for the kind of story the map was designed to support.

Present in plain prose (see "Asking the player questions" in CLAUDE.md). One sentence lead, four numbered options. Each option: the proposed name, then a phrase of context (e.g. "Old Great Lakes shipping port — Scottish-immigrant founders"). End with "Reply with the number, or propose your own." Wait for the player's reply in their next message before continuing.

**5. Suggest a founding history.**

After the player picks a name, generate **3–4 founding-history options** grounded in the chosen name, the spatial data, and the screenshot. Each option is a one-paragraph capsule covering:
- Founding era and original economic engine (rail, mill, port, mine, military base, agricultural hub, etc.). The data narrows this: a 62°N boreal lake district + many named bridges suggests Great Lakes / Canadian Shield logging or iron range; a 37°N Mediterranean archipelago suggests California coastal trade or Pacific shipping; a 191 m relief mostly-flat plain near a river suggests Midwestern rail-junction agriculture.
- What the 20th century did to it (boom, decline, suburban sprawl, deindustrialization, reinvention).
- The region of North America the climate + terrain place it in (Great Lakes, Pacific Northwest, Sunbelt, Atlantic Coast, Prairies, Appalachia, California, etc.) — pin this to the latitude / temperature signals from step 2.

Vary the options meaningfully — don't propose four flavors of the same rust-belt story. At least one option should suggest a non-industrial origin if the geography allows it.

Present in plain prose. One sentence lead, 3–4 numbered options. Each option: a short tag (e.g. "Old timber port, postwar decline", "Railroad junction, agricultural seat"), then the founding-history paragraph itself on the next line (kept tight — 2–3 sentences). End with "Reply with the number, or describe your own founding story." Wait for the player's reply before continuing.

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

Ask the two toggle questions in one plain-prose message — short intros, numbered choices, with the recommended option clearly marked. Format roughly:

```
Two quick toggles before I close this out.

**Secrets.** Should hidden secrets be visible to you, or kept blind until they break in-story?
  1. **Hidden** (recommended) — secrets are generated and shape the story, but you never see their contents in chat until they're revealed in-world. Closer to playing blind.
  2. **Shown** — same secrets get generated, but I freely show their contents to you. Author / editor mode.

**Level-up stories.** When the city levels up in-game (milestone advance + funds influx), should I write a storyline about how the new money gets spent and fought over?
  1. **Enabled** (recommended) — on every milestone advance I write an event + short narrative piece (council transcript, news clipping, developer memo) about who pushed for what, who lost out, the political fallout.
  2. **Disabled** — milestone advances are still logged in the session, but no dedicated stories.

Reply with two numbers (e.g. "1, 1") or pick each by name.
```

Wait for the player's reply, then map the answers to settings:
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
