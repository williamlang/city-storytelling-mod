---
description: Bootstrap a new city — name, founding history, map, inferred premise
order: 10
---

Start a new playthrough. By the end, this city folder has `canon/city.md`, `canon/tone.md`, `canon/era.md`, and `canon/playthrough-premise.md` populated enough that `/session-start` can take over, plus a written `settings.json`.

The city folder itself already exists — the CityStoryMod scaffolded it (with this template) the first time it exported a snapshot for this city. `/new-city` runs inside that folder; it does not create one.

**0. Two entry styles — read the config block first.**

This command runs two ways:

- **Quickstart wizard (one-shot).** The native founding wizard appends a `<<QUICKSTART_CONFIG>>` block to the prompt. It carries the player's answers to the founding questions, gathered in a native form with no model involved. When this block is present, **do not ask anything** — read every spatial source, honour the config, derive the era, write all the canon + `settings.json` in one non-interactive pass, then call the `wizard_done` tool with a summary.
- **Chat (`/new-city` typed in the panel).** No config block, or a partial one. Ask for whatever the config didn't supply, in prose, waiting for the player's reply between questions (see "Asking the player questions" in CLAUDE.md).

**The config block is field-level, not all-or-nothing.** Scan the prompt for a block like:

```
<<QUICKSTART_CONFIG>>
region: Europe
name: Selkirk Falls          # or:  name: (suggest)
tone: grounded-realist
focus: citizens, civic
player_role: chronicler
player_character_name:
real_world_refs: fictional
cast_density: balanced
content_maturity: pg-13
secrets_visibility: hidden
levelup_storylines: true
storyteller_proactivity: on-request
git_versioning: false
integrations:
<<END_CONFIG>>
```

For **each field the block carries**, treat it as the player's answer and **do not re-ask it**:
- `region` — a hard constraint, written verbatim to `canon/city.md`.
- `name` — a literal value is used as-is; `(suggest)` (or a blank/absent field) means choose one grounded in the spatial data.
- `tone`, `focus`, `player_role`, `player_character_name`, `real_world_refs` — story-shaping; go to `canon/tone.md` (+ a seeded `characters/` entry when `player_role: character`).
- `cast_density`, `content_maturity`, `secrets_visibility`, `levelup_storylines`, `storyteller_proactivity`, `git_versioning`, `integrations` — behavior/disclosure; go to `settings.json`.

For **every field NOT supplied** (including when there is no config block at all), ask for it in prose as part of the flow below, waiting for each reply. The prose flow is the **superset** — it always covers region, name, founding history, and the founding questions — and the config block just suppresses the questions it already answered.

`era` is **never** a config field and **never** asked — always derive it from the in-game date (step 7).

**1. Verify the folder is fresh.**

Read `canon/city.md`. If it's still the unmodified template stub (no chosen name, no chosen founding history), proceed. If it already has a real name and history filled in, stop and ask the player whether they want to overwrite — `/new-city` is meant to run once per city, before any session. (In one-shot config mode, if the folder is already founded, still stop and report rather than silently clobbering populated canon.)

**2. Read the spatial data the mod already collected.**

The CityStoryMod has already exported a first snapshot and (if Carto is installed) the Carto chunks by the time the player invokes `/new-city`. Pull all of it before deciding anything:

- Latest `snapshots/snapshot-*.json` — extract the `map.*` block: `name`, `theme`, `latitude`, `longitude`, `temperature_min_c`, `temperature_max_c`, `cloudiness`, `precipitation`. These are *world-identity* signals — latitude+temperature alone tell you whether this is boreal, temperate, or Mediterranean. Also read `captured_at_ingame` — the in-world date, which step 7 uses to derive the era.
- `carto/processed/index.md` — footprint dimensions (km × km), road-network summary, and the named-decoration list (cairns, ruins, monuments). The decoration repeats often carry implicit history.
- `carto/processed/elevation.md` — the one-line terrain reading ("Mostly flat", "Hilly, with a localized high point", "Rugged / mountainous"), relief, and which quadrant holds the high ground.
- `carto/processed/water.md` — the one-line water reading ("Heavy water — complex lake district", "Essentially landlocked", etc.) and per-quadrant water share.
- `carto/processed/roads.md` — the named highways/bridges. These carry the regional flavor (a "Cypress Highway" + "Mulberry Lane" + four named bridges over water suggests an established settler-era road system).

Synthesize these into 4–6 lines internally — don't dump to the player yet. This is the **primary** geographic anchor. If any chunk is missing (older save, Carto not installed), proceed with whatever is available; the snapshot at minimum always exists.

**2b. Check for prior cities on the same map.**

The mod's data folder holds one folder per playthrough. Walk the sibling directories (the city folders next to this one) and read each one's latest `snapshots/snapshot-*.json` to compare `map.name` with the current city's. For every sibling that used the same map, read its `canon/city.md` and `canon/playthrough-premise.md` and note the chosen city name, founding era, dominant economic engine, region, and premise shape.

**This list drives diversification in the name, history, and premise steps.** If the player is running `/new-city` for the third time on Lakeland, the third run must propose choices that don't echo the first two — different surnames, different eras, different economic anchors, different premise shapes (boom/decline/reinvention/identity-loss). Repeating "Halverson" twice, or proposing two rust-belt postwar-decline angles for the same map, reads as lazy.

Hold the list silently. Don't surface it to the player — just use it to constrain your choices. If no sibling shares the map, this step is a no-op.

**3. Read the combined topographic map — visual anchor.**

`Read` the file `carto/processed/map.png`. This is a single PNG image generated by the mod that overlays everything from step 2 in one picture: terrain in hypsometric tints (green lowland → tan → brown → white peaks), water bodies in depth-shaded blue, district outlines, named and unnamed roads, zoning fills (building footprints colored by use — green residential, blue commercial, cyan office, amber industrial), and service buildings as bright color-coded markers (red fire, dark-blue police, pink health, orange education, yellow power, cyan water, purple transit, green parks). The zoning colors are *inferred* by the mod from building data and aren't authoritative — trust them as a strong hint, not gospel. Road and building *names* are not drawn on the image — those live in the text chunks (`roads.md`, `index.md`); the picture carries shape, adjacency, and color. The data is the same data that backed the text chunks; the image is what's useful when shape and adjacency matter more than the numbers.

Look at the image and pull what the text chunks didn't already make obvious:
- Landform shape — coastline curve, peninsula reach, river meander, valley orientation, the shape of high ground vs. low.
- Spatial relationships — how roads fit the terrain, where districts sit relative to water and elevation, which corners are crowded vs. empty.
- Anything that contradicts the text chunks' classifier reading. If the map disagrees with "Mostly flat" or "Heavy water," **trust your eyes over the classifier** and note the disagreement in your synthesis.

This is one image you already have; do not ask the player for a screenshot path. If `carto/processed/map.png` is missing (older save, Carto not installed, or carto hasn't run yet), tell the player so (chat mode) or note it silently (one-shot mode) and continue with the text chunks from step 2 — the map adds visual texture but isn't load-bearing for naming or premise inference. **No magic, no fantasy geography** — describe what's actually present.

**4. Region.**

Region is one of: **North America | Europe | Asia | Latin America | Africa | Oceania | Middle East**.

- **Config supplies `region`** → use it verbatim as a hard constraint. It anchors naming pools and cultural framing for everything downstream.
- **Not supplied (chat mode)** → ask it as the first numbered question, defaulting to the best fit for the map's latitude + theme (a 62°N boreal lake district reads North America or Europe; a 37°N Mediterranean archipelago reads Europe or North America). Present the seven options, mark the recommended default, wait for the reply.

> **Naming-pool scope (v1):** the agent's naming and founding-history prose is strongest for **North America**. A non-NA region still pins correct metadata and grounds cultural framing, but lean on North-American naming traditions as the model until per-region pools are authored. Don't fabricate shaky regional detail you're unsure of — keep names plausible and the framing honest to the region.

**5. Name.**

- **Config supplies a literal `name`** → use it as-is. Write one sentence on what it means / where it comes from when you write `canon/city.md`.
- **Config says `(suggest)`, or chat mode** → generate **4 plausible city names** grounded in the spatial data (step 2), the combined map (step 3), and the region (step 4), consistent with the style guide:
  - Real-sounding: founders' surnames, geographic features, place names used respectfully and grounded in the actual region the geography + climate suggests. No joke names.
  - Each name implies a slightly different founding story — give meaningful range (an old port name vs. a mill-town name vs. a railroad-junction name vs. an agricultural-county-seat name).
  - Lean on signals only the data carries — the CS2 map name itself (e.g. "Archipelago", "Lakeland") is *not* canon and the player won't see it in-world, but it's a hint for the kind of story the map was designed to support.
  - **Avoid the prior-city list from step 2b.** Different surnames, different naming traditions, different regional flavors.
  - **One-shot mode:** pick the single best-fit name yourself — don't present options or wait.
  - **Chat mode:** present in plain prose, one-sentence lead, four numbered options, each with a phrase of context (e.g. "Old Great Lakes shipping port — Scottish-immigrant founders"). End with "Reply with the number, or propose your own." Wait for the reply before continuing.

**6. Founding history.**

Founding history is always authored by you (it's never a config field) — but **one-shot mode picks one; chat mode offers a choice.**

Generate the founding history grounded in the chosen name, the spatial data, the combined map, and the region. It covers:
- Founding era and original economic engine (rail, mill, port, mine, military base, agricultural hub, etc.). The data narrows this: a 62°N boreal lake district + many named bridges suggests Great Lakes / Canadian Shield logging or iron range; a 37°N Mediterranean archipelago suggests coastal trade or shipping; a 191 m relief mostly-flat plain near a river suggests rail-junction agriculture.
- What the 20th century did to it (boom, decline, suburban sprawl, deindustrialization, reinvention).
- The sub-region the climate + terrain place it in, pinned to the latitude / temperature signals from step 2 and consistent with the chosen `region`.
- **Avoid the prior-city histories from step 2b.** Vary era / engine / trajectory from any sibling run of the same map.

- **One-shot mode:** choose the single most narratively-fertile history that fits, and proceed.
- **Chat mode:** present **3–4 founding-history options** in plain prose — one-sentence lead, numbered, each a short tag ("Old timber port, postwar decline") then a tight 2–3 sentence capsule. End with "Reply with the number, or describe your own founding story." Wait for the reply.

**7. Derive the era (always — never asked).**

Read `captured_at_ingame` from the latest snapshot (the in-world date; fall back to `clock.json`'s `in_world_date` if present). The era follows the in-game year: a 2020s+ year → contemporary; an earlier year → the matching mid-century / period framing. Write `canon/era.md` to match — set `in_world_start_date` to the in-game date, `real_world_anchor` to its year, and adjust the prose if the year is not contemporary. Keep `time_per_session` as the default unless the player has said otherwise. The player can edit `canon/era.md` afterward if the derived era isn't what they want.

**8. The remaining founding questions.**

These are the story-shaping and behavior choices. Each has a safe default. **Config supplies → use it and skip the question. Not supplied → ask in chat (prose, recommended default marked), wait for the reply.** In one-shot mode every answer comes from config (or its default) — ask nothing.

Story-shaping (→ `canon/tone.md`):
- **Narrative tone** — one of `grounded-realist` (default) / `dramatic` / `noir` / `hopeful` / `satirical`.
- **Narrative focus** — one or both of `citizens` (citizens & families) and `civic` (civic & political). Default: both. **At least one must be set** — if config somehow carries neither, treat as both. Both on tells the richest story; one narrows it. This biases the gameplay objectives `/session-start` proposes and how the diff attributes new construction (people vs. institutions).
- **Player's place in the fiction** — `chronicler` (unseen, default) or `character` (named). When `character`, use `player_character_name` (or suggest a founder/mayor name if blank) and seed a `characters/` entry for the player.
- **Real-world references** — `fictional` (fully fictional, default) or `real` (may reference the real world).

Behavior / disclosure (→ `settings.json`):
- **Cast density** — `tight` / `balanced` (default) / `sprawling`.
- **Content maturity** — `cozy` / `pg-13` (default) / `gritty`. **Disclosure preference only** — it gates how explicitly you narrate detail to the player, never what canon gets generated. Identical canon at every setting.
- **Secrets visibility** — `hidden` (default) or `shown`. See "Secrets" in CLAUDE.md.
- **Level-up storylines** — `true` (default) or `false`.
- **Storyteller proactivity** — `on-request` (default) or `proactive` (turns on the active-events loop from session 1).
- **Git versioning** — `false` (default) or `true`. Record the preference; the repo-init plumbing is gated on a separate issue, so until then this is just a recorded flag.
- **Integrations** — a list of enabled peer-mod integrations; empty for now (none supported yet).

In chat mode, you can batch the lighter toggles into one or two prose messages (secrets + level-up together, the rest together) rather than nine separate turns — but still ask region, name, and founding history as their own steps. Map each answer to the field names above.

**9. Write `canon/city.md` and infer the playthrough premise.**

Overwrite the scaffold `canon/city.md` with:
- Frontmatter: `name`, `region` (the enum value from step 4), `founded` (year), `geography` (one-line phrase from the map read). Leave `population_at_start` and `climate` as `TBD` — pinned at session 1.
- `## Name` — the chosen name, one sentence on what it means / where it comes from.
- `## Where it is` — region + the geographic features you observed, as prose.
- `## How it got here` — the founding-history paragraph, expanded to 4–8 sentences with specifics (a named founding family or company, a decade-defining event, the 20th-century trajectory).
- `## What it is now` — a short stub: *"To be pinned at session 1: starting population, dominant industries today, major employers, the city's regional reputation."*
- `## The defining tension at session 1` — a stub: *"To be set at session 1 — see `/session-start`."*

Then **infer the playthrough premise** silently from the chosen founding history, the map observations, the region, and the name. Write the one-sentence (or short paragraph) premise to `canon/playthrough-premise.md` as plain prose. See "Playthrough premise" in CLAUDE.md for inputs and heuristics. Do **not** ask the player to author it. Diversify from the prior-city premises noted in step 2b.

Do **not** invent characters, companies, places, factions, or events here (the one exception: seed a `characters/` entry for the player when `player_role: character`). Everything else flows from `/session-start` and `/story-driven`.

**10. Write `canon/tone.md`.**

Add the story-shaping fields from step 8 to `canon/tone.md` as frontmatter (narrative tone, the active focus lenses, player's-place-in-the-fiction, real-world-references), keeping the existing prose tone guide below. If `narrative_tone` is anything other than `grounded-realist`, note the shift in a line of prose so later commands read it. Content maturity does **not** live here — it's a `settings.json` disclosure field.

**11. Write `settings.json`.**

Write `settings.json` at the city-folder root using `settings.sample.json` as the shape, with:
- `secrets_visibility`, `levelup_storylines`, `cast_density`, `content_maturity`, `git_versioning`, `storyteller_proactivity`, and `integrations` set from step 8.
- `bootstrapped` set to `true`. This is the signal that `/new-city` has run; the CityStoryMod prompt panel reads it to hide the `/new-city` button and to clear the fresh-city flash/banner. Flipping it is the last write `/new-city` makes.

**12. Hand off.**

- **One-shot (config) mode:** if a **`wizard_done`** tool is available, call it once with `{ city_name, region, founded, premise }` (premise one sentence) — the native result card reads it for a richer summary. **If no such tool is exposed (e.g. the Claude Code CLI provider doesn't surface it), skip it — do not search for it or treat its absence as an error.** Either way, `settings.json` with `bootstrapped: true` (step 11) is the authoritative completion signal, and the native wizard detects founding completion from that and closes itself with a result card. Don't print a long prose hand-off in this mode; the card carries it.
- **Chat mode:** tell the player concretely, in prose:
  - **The inferred premise** — quote the one-sentence premise verbatim and note they can ask you to revise it before session 1.
  - **Rename their CS2 save to "`<City Name>`"** so the mod's future exports keep landing in this city folder. Until that rename, exports may land under the placeholder slug.
  - What's still TBD in `canon/city.md` (population, climate, defining tension).
  - That the next step is `/session-start`.

Either way, **do not run `/session-start` automatically.** The player decides when to start the first session.
