# Snapshot schema (v0.9)

The mod's `ExportSystem` emits JSON snapshots into each city's folder. The storytelling agent (running in the same folder, via `StorytellerDispatcher` or any Claude session opened against that folder) ingests them, diffs successive snapshots, and turns observed changes into characters, companies, places, and events.

This document defines the contract. The mod is the producer; the agent is the consumer. Both ship inside CityStoryMod, but the producer/consumer split is real and stays useful.

## File naming and location

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityStoryMod\<city-slug>\snapshots\snapshot-<unix-ts>.json
```

- One file per snapshot.
- `<unix-ts>` = UTC unix seconds at capture. Monotonic ordering on the filesystem.
- Snapshots are full state, not deltas. The agent diffs.
- The mod never deletes or rewrites past snapshots. Cleanup is the agent's concern (or a manual prune).

## Design principles

1. **Full state, not deltas.** Every snapshot describes the city's complete current state for the fields it knows about. The agent computes diffs between successive snapshots.
2. **Stable entity IDs.** Districts, companies, sampled citizens, and notable buildings each carry a stable `id` (CS2 ECS entity index + version) so the agent can track the same entity across snapshots.
3. **Null means "not implemented yet."** Empty arrays and `null` values are valid and explicit — they signal "this field is in the schema but the mod hasn't filled it in this version." The agent should treat `null` and missing keys the same.
4. **`schema_version` gates breaking changes.** Additive changes (new fields, new nested objects) don't bump the version. Renames, removals, or semantic shifts do.
5. **JSON keys are `snake_case`.** Matches typical Python/agent tooling. The mod is the C# side, so this is a translation at serialization time.

## v0.2 split — snapshot vs Carto chunks

Spatial geometry and per-district building lists are NOT in the snapshot. Those live in `carto/processed/index.md` and `carto/processed/districts/<slug>.md`, produced by `CartoProcessor` from Carto's raw GeoJSON exports. The snapshot owns city stats, demographics, trade, diffs, and the small entity classes Carto doesn't cover (outside connections, water sources). The agent reads both surfaces for different purposes.

## v0.3 — world identity, road network, terrain

Adds:
- **`map.*`** block in the snapshot. The map is the world the city sits inside; the city is the player's project on that world. At founding time the agent wants both.
- **`carto/processed/roads.md`** chunk + **Road network** + **Map footprint** sections in `carto/processed/index.md` — emitted by an expanded CartoProcessor reading Carto's Network and MapTile features.
- **First-Carto-on-new-city auto-trigger** — when a new city dir is scaffolded, ExportSystem fires Carto immediately so the storyteller has spatial context the first time it runs against the dir.
- (Future) `carto/processed/elevation.md` and `carto/processed/water.md` from Carto's Elevation and Depth GeoTIFFs.

## v0.4 — friction signals

The top-3 from the [snapshot fields wishlist](https://github.com/williamlang/city-storytelling-mod/issues/18). Each one hands the storyteller a different kind of friction that the agent otherwise had to invent.

- **`pollution`** (top-level) — air, ground, noise. Sampled at every building position via the corresponding `Game.Simulation.*PollutionSystem`, binned by `CurrentDistrict`. Provides per-district averages and a city-wide average. Drives class stories, NIMBY fights, lawsuits, health scandals: "Birchwood's complaints are 70% noise" becomes writeable.
- **`city.churn`** — births / deaths / move-ins / move-aways per day. Read via `ICityStatisticsSystem.GetStatisticValue` against the `BirthRate`, `DeathRate`, `CitizensMovedIn`, `CitizensMovedAway` statistic types. Each is a Daily collection — diff across snapshots for period totals.
- **`diff.building_churn`** — per-district demolition + construction counts (per zone-type), separate from the existing net `district_zone_deltas`. Stories about displacement and gentrification need to know that 6 residential lots were torn down AND 4 built, not just that the net was -2.

Per-district churn (immigration/deaths by district) is deferred — CS2's stats API exposes only city-wide rolls. Surfacing per-district would require sampling citizens by home district between snapshots.

## v0.5 — social conditions, budget, and "why people leave"

The "easy wins" tier from #18 — every field here is a single `ICityStatisticsSystem.GetStatisticValue` call, all city-wide.

- **`city.social`** — `homeless_count`, `unemployed_count`, `crime_count`, `crime_rate`. Classic neighborhood-pressure signals; pair with the existing per-district `pollution` and `district_zones` for spatial inference.
- **`city.budget`** — `income_daily` and `tax_residential`, both verified against multi-day live data. `city.money` already carries the running balance, so the agent can compute net flow per period from those two fields plus the previous snapshot's money.
  - **Known gap:** `expense_daily`, `tax_commercial`, `tax_industrial`, `tax_office` were dropped from the schema before shipping — they all returned `0` against `parameter=0` on cities with active commercial/industrial buildings and known-negative net cash flow. Those `StatisticType` slots almost certainly need a non-zero `parameter` to roll up correctly (sub-category sums, the same way `MovedAwayReason` is keyed by the reason enum). Tracked in [#28](https://github.com/williamlang/city-storytelling-mod/issues/28).
- **`city.churn.moved_away_by_reason`** — citizens leaving, broken down by the eight `Game.Agents.MoveAwayReason` enum values: `not_happy`, `no_money`, `no_suitable_property`, `no_adults`, plus the three tourist variants and `trip_need_not_moved_in`. The sleeper hit — the agent can write "people are fleeing the Yards over the noise" with data backing it.

Per-district crime and land value are deferred to v0.6 — both follow the pollution sampling pattern (sample at building positions, bin by district) and pair well as a single batch.

## v0.6 — per-district land value and crime

Two new top-level blocks, shipped together because they share the same per-building → bin-by-district sampling loop. They pair with per-district `pollution` to localize friction signals the storyteller otherwise has to invent.

- **`land_value`** — per-district + city-wide averages of `LandValueCell.m_LandValue`, sampled at every building position via the same `CellMapSystem<T>` pattern that backed pollution in v0.4. Drives "this is where the money lives" / "the bottom fell out of this district" stories.
- **`crime`** — *(v0.6 shape reworked in v0.8; see below.)* Originally per-district + city-wide averages of `Game.Buildings.CrimeProducer.m_Crime`. That sensor turned out to saturate everywhere on a real city (essentially every building carries `CrimeProducer`, and `m_Crime` is a monotonically-climbing accumulator), so it produced no spatial signal. v0.8 swapped it for an active-criminal count.

`land_value` returns `null` if its cell grid hasn't allocated yet (fresh save before sim has run).

## v0.7 — per-citizen sample

Replaces the empty `citizens_sample[]` placeholder with a real sample. Each export captures up to `CitizensSampleMaxSize` (currently 30) resident citizens. The selection strategy is:

1. **Every `Game.Citizens.Followed` citizen is always included.** These are the citizens the player has explicitly opted into tracking via the in-game UI — they're the ones the storyteller is most likely to anchor canon around, so dropping them would defeat the purpose of the sample.
2. **Remaining slots are filled with a uniform random sample** of other residents, seeded by the export's unix timestamp. Same snapshot file always reproduces the same sample (debuggable); successive snapshots rotate over time (the storyteller sees fresh faces).

Non-residents are filtered out at the source: tourists (`CitizenFlags.Tourist`), commuters (`CitizenFlags.Commuter`), citizens with `MovingAwayReachOC`, members of `TouristHousehold` / `CommuterHousehold`, and dead citizens (`HealthProblemFlags.Dead`).

Per-entry fields:

| Field | Source | Notes |
|---|---|---|
| `id` | Entity index/version | Stable across snapshots within a save. |
| `name` | `NameSystem.GetRenderedLabelName(citizen)` | CS2's rendered display name. |
| `gender` | `Citizen.m_State & CitizenFlags.Male` | `"male"` / `"female"`. |
| `age` | `Citizen.GetAge()` | `"child"` / `"teen"` / `"adult"` / `"elderly"`. |
| `education` | `Citizen.GetEducationLevel()` | `"uneducated"` → `"highly_educated"` (5-tier `CitizenEducationLevel`). |
| `happiness` | `Citizen.Happiness` | 0–100; average of `m_WellBeing` and `m_Health`. |
| `home_district` | `HouseholdMember.m_Household` → `PropertyRenter.m_Property` → `CurrentDistrict` | `null` if homeless w/ no temp home, or building outside any district. |
| `workplace` | `Worker.m_Workplace` (resolved via `PropertyRenter` when present) | Rendered name of the company / building. `null` for unemployed, students, retirees. |
| `school` | `Game.Citizens.Student.m_School` | Rendered name; `null` if not a student. |
| `followed` | `HasComponent<Followed>` | `true` if the player has clicked "follow" on this citizen. |
| `is_criminal` | `HasComponent<Criminal>` | `true` while actively flagged as a criminal. |

**Wealth / household money is deferred.** Computing it correctly needs a `CitizenHappinessParameterData` singleton + the household's `Resources` dynamic buffer, which is a bigger join than the others. The current schema's `city.social.unemployed_count` + `city.budget` already give a city-wide picture; per-citizen wealth is still queued.

## v0.8 — crime sensor swap

Replaces the v0.6 building-side `CrimeProducer.m_Crime` averaging with an active-criminal **count** binned by home district. First real-data snapshot from Halverson Crossing (4.6k pop, 4 reported crimes city-wide) showed the old reading was useless: ~all buildings carry `CrimeProducer`, the `m_Crime` accumulator climbs monotonically regardless of district, and Pine Quarter and The North Yards came back at 845 / 793 — visually identical despite very different in-game character.

The new `crime` block walks the citizen query, finds residents carrying `Game.Citizens.Criminal`, applies the same resident filter as `citizens_sample` (skips tourists, commuters, moving-away, dead), and bins by the home building's `CurrentDistrict`. The city-wide field stays an unfiltered count of active resident criminals — distinct from `city.social.crime_count` (CS2's `CrimeCount` statistic), which tracks reported crime over the stats window.

Shape changed from `{ city: { average }, samples, by_district: { name: { average, samples }}}` to `{ city: { active_criminals }, by_district: { name: { active_criminals }}}`. The agent reading old snapshots will see the field name unchanged but the inner key shift — the `schema_version` bump from 0.7 → 0.8 gates this.

## v0.9 — service capacity (education)

Populates the previously-empty `services` block with the first capacity/utilization sensor: **`services.education`**. Per-school enrollment vs. capacity is the single most legible "build/expand a service" signal — a school at or over capacity, or a tier with no seats at all, is an event the storyteller can act on without inventing numbers (which it had been doing, since nothing in the snapshot carried capacity).

Sources, verified against `Game.dll`:
- **`Game.Buildings.School`** marks a school *building instance* (the query).
- **`PrefabRef` → `Game.Prefabs.SchoolData`** carries `m_StudentCapacity` (max) and `m_EducationLevel` (the tier the school grants, 1–4 on the same scale as `CitizenEducationLevel`).
- **`Game.Buildings.Student`** is a per-building `DynamicBuffer` of enrolled citizens; its `Length` is current enrollment — no citizen scan needed.

Shape:
```jsonc
"education": {
  "city": {
    "schools": 3, "enrolled": 612, "capacity": 700, "utilization": 0.87,
    "by_tier": {
      "elementary": { "schools": 1, "enrolled": 139, "capacity": 650, "utilization": 0.21 },
      "secondary": { "schools": 1, "enrolled": 514, "capacity": 1750, "utilization": 0.29 },
      "higher_education": { "schools": 1, "enrolled": 94, "capacity": 10000, "utilization": 0.01 }
    }
  },
  "schools": [
    { "name": "Iries Skene Elementary School", "district": null, "tier": "elementary",
      "education_level": 1, "enrolled": 139, "capacity": 650, "utilization": 0.21 }
  ]
}
```
`tier` collapses CS2's school types into the three capacity pools the game itself uses — `elementary`, `secondary` (high school), and `higher_education` (college + university). The mapping is `education_level` 1 → elementary, 2 → secondary, ≥3 → higher_education. The raw `education_level` byte is also emitted per school and is authoritative. `utilization` is `enrolled / capacity` (null when capacity is 0). `services.education` itself is `null` when the city has no schools yet.

**Read `by_tier`, not the city-wide top line, for the "build a school" signal.** Higher-ed assets carry very large capacities (often 10,000), so a city-wide `utilization` mixing them with a near-full elementary reads artificially low. Per-tier utilization is the honest signal — a `secondary` pool at 0.95+ with no other high school is the event. The same `{ enrolled/patients, capacity, utilization }` shape is intended to extend to healthcare and other capacity-bound services beside `education`.

### `services.civic_buildings` + the naming return channel (#40)

`services.civic_buildings` is the roster of namable city-service buildings — each `{ id, name, category, prefab_name, district, has_custom_name }`. It's built from `CityServiceUpkeep` + `Building`, **excluding** `ServiceUpgrade` (upgrades/extensions), owned sub-buildings (`Owner`), and `OutsideConnection`. That last exclusion matters: the game tags commute-out higher-education access points (neighbouring towns, ~10k "capacity") with `Game.Buildings.School` + `SchoolData`, so without it both `education` and the roster would report phantom universities the player never placed. The same `OutsideConnection` guard is on the education school query.

`category` is component-derived for the common services (education/health/fire/police/garbage/park) and prefab-name-derived for the rest (power/water/deathcare/transit/…), with `prefab_name` always included so an `other` is still identifiable.

**Write path — first time the mod mutates game state.** The agent gives the `has_custom_name: false` buildings real names that appear *in-world*. It writes `naming-requests.json` at the city root — a JSON array of `{ "id", "name" }` keyed by `civic_buildings[].id` — and the mod, on its ~10s clock heartbeat, applies each via the game's own `Game.UI.NameSystem.SetCustomName` (adds the serializable `CustomName` component, so it persists across save/load, same as a manual player rename), writes `naming-results.json` (per-id `applied`/`skipped`/`error`), and deletes the request file. Ids are resolved against the *live* civic-building set, so only real civic buildings can be renamed and a stale id (entity index+version isn't stable across save/load) reports `skipped` rather than misfiring. Blank name clears a name.

## The shape

```json
{
  "schema_version": "0.8",
  "snapshot_id": "snapshot-1779083749",
  "session_id": "session-1779083100",
  "session_started_at_utc": "2026-05-17T22:45:00Z",
  "captured_at_utc": "2026-05-17T22:55:49Z",
  "captured_at_ingame": null,

  "map": {
    "name": "Lakeland",               // From Game.UI.MapMetadataSystem.mapName.
    "theme": "North American",        // Asset-pack style; drives building / vehicle look.
    "latitude": 62.04,                // Real-world latitude (degrees) — bigger = farther north.
    "longitude": 28.51,               // Real-world longitude — combines with latitude as a climate anchor.
    "temperature_min_c": -14.86,      // Coldest temperature seen across the year.
    "temperature_max_c": 25.79,       // Warmest.
    "cloudiness": 0.21,                // 0..1 fraction of the year cloudy.
    "precipitation": 0.07,             // 0..1 fraction of the year wet.
    "ground_water_availability": 0,    // CS2 metric; 0 on a fresh save before the city is built.
    "surface_water_availability": 0
  },

  "city": {
    "name": null,
    "population_hud": null,         // residents count, matches in-game HUD
    "population_with_move_in": null, // includes incoming move-ins
    "citizens_total": 2,             // raw ECS Citizen entity count; broader than HUD
    "money": null,                   // PlayerMoney
    "happiness": null,               // 0-100, lives inside Population
    "health": null,                  // 0-100, lives inside Population
    "tourists_current": null,
    "tourists_average": null,
    "attractiveness": null,
    "danger_level": null,            // float
    "milestone_level": null,
    "xp": null,
    "zones": {                       // counts of ALL buildings by classified type
      "residential": N, "commercial": N, "industrial": N, "office": N,
      "extractor": N, "service": N, "transformer": N, "water_pumping": N,
      "other": N                     // anything that didn't match a marker
    },
    "churn": {                       // v0.4 — most-recent daily values from CityStatisticsSystem
      "births_daily": 12,            // BirthRate
      "deaths_daily": 4,             // DeathRate
      "moved_in_daily": 18,          // CitizensMovedIn
      "moved_away_daily": 7,         // CitizensMovedAway
      "moved_away_by_reason": {      // v0.5 — Game.Agents.MoveAwayReason breakdown
        "no_suitable_property": 0,
        "not_happy": 5,
        "no_adults": 0,
        "no_money": 2,
        "tourist_no_target": 0,
        "tourist_no_hotel": 0,
        "tourist_no_money": 0,
        "trip_need_not_moved_in": 0
      }
    },
    "social": {                      // v0.5 — city-wide social conditions
      "homeless_count": 23,
      "unemployed_count": 145,
      "crime_count": 12,
      "crime_rate": 4
    },
    "budget": {                      // v0.5 — income + residential tax (verified working)
      "income_daily": 4500,
      "tax_residential": 2200
      // expense_daily + tax_commercial / industrial / office dropped before ship;
      // returned 0 on cities with real activity. Need non-zero parameter to roll up.
    }
  },

  "pollution": {                     // v0.4 — sampled at every building position, binned by district
    "city": { "air": 12.3, "ground": 4.1, "noise": 21.7 },
    "samples": 247,                  // total buildings sampled across the city
    "by_district": {
      "Pine Quarter": { "air": 8.2, "ground": 2.4, "noise": 18.1, "samples": 89 },
      "The North Yards": { "air": 31.4, "ground": 18.6, "noise": 42.0, "samples": 56 }
    }
  },

  "land_value": {                    // v0.6 — LandValueCell sampled at building positions, binned by district
    "city": { "average": 184.5 },
    "samples": 247,                  // same population as pollution.samples (every building w/ a Transform)
    "by_district": {
      "Pine Quarter":    { "average": 312.7, "samples": 89 },
      "The North Yards": { "average":  64.2, "samples": 56 }
    }
  },

  "crime": {                         // v0.8 — count of active criminal residents, binned by home district
    "city": { "active_criminals": 12 },
    "by_district": {
      "Pine Quarter":    { "active_criminals": 3 },
      "The North Yards": { "active_criminals": 9 }
    }
  },

  // districts[], buildings[], roads[], other_named[]: REMOVED in v0.2.
  // See carto/processed/index.md + carto/processed/districts/<slug>.md.

  "outside_connections": [
    // Edge-of-map destinations (cities the player connects to via highway / rail / air).
    // { "id": "...", "name": "Canmore" }
    // CS2 typically pairs these (one inbound, one outbound) so the same name appears
    // twice with different ids. Detected via Game.Objects.OutsideConnection.
    // Stays in the snapshot — Carto doesn't expose these.
  ],

  "water_sources": [
    // Lakes, river segments, springs. { "id": "...", "name": "Lake Minnewanka" }
    // Detected via Game.Simulation.WaterSourceData. Includes both narratively-named
    // landmarks (Lake Minnewanka) and auto-named flow segments (Bow12).
    // Stays in the snapshot — Carto's water output is raster-only by default.
  ],

  "citizens_sample": {                 // v0.7 — see "per-citizen sample" above for shape
    "sampled": 30,                     // number of entries in citizens[]
    "eligible_total": 1842,            // residents that passed the non-tourist/commuter/dead filter
    "followed_count": 2,               // how many of those were Followed (always included in sample)
    "citizens": [
      {
        "id": "12345-1",
        "name": "Inger Brevik",
        "gender": "female",
        "age": "adult",
        "education": "well_educated",
        "happiness": 73,
        "home_district": "Pine Quarter",
        "workplace": "Brevik Lumber Co.",
        "school": null,
        "followed": true,
        "is_criminal": false
      }
    ]
  },

  "district_zones": {
    // Per-district building-type counts. Same shape as city.zones but keyed by
    // district name (matching Carto chunks). Backs the diff.district_zone_deltas
    // signal so the agent can spot subdivision growth localized to a neighborhood.
    // "Pine Quarter":    { "residential": 245, "commercial": 12, "industrial": 0, ... },
    // "The North Yards": { "residential": 0,   "commercial": 4,  "industrial": 38, ... }
  },

  "demographics": {
    "by_age_band": null,
    "by_education": null,
    "by_wealth": null,
    "tourists_count": null,
    "commuters_count": null
  },

  "trade": {
    "imports": [
      // { "resource": "oil", "amount_per_day": 1234, "partner_count": 3 }
    ],
    "exports": []
  },

  "services": {
    // v0.9 — per-school enrollment vs. capacity + city rollup by tier.
    // null when the city has no schools yet. See "v0.9" above for the full shape.
    "education": {
      "city": { "schools": 3, "enrolled": 612, "capacity": 700, "utilization": 0.87, "by_tier": { } },
      "schools": [ /* { name, district, tier, education_level, enrolled, capacity, utilization } */ ]
    },
    // v0.9 — roster of namable city-service buildings (#40). null when the city
    // has none. Real in-city buildings only (CityServiceUpkeep + Building,
    // excluding service upgrades and outside connections). The storyteller names
    // the has_custom_name:false ones via the naming-requests.json return channel.
    "civic_buildings": [
      // { id, name, category, prefab_name, district, has_custom_name }
      { "id": "91602-1", "name": "Fire House", "category": "fire",
        "prefab_name": "FireHouse01", "district": "Sound Strand", "has_custom_name": false }
    ]
    // future: healthcare (patients vs. beds), coverage_gaps, ...
  },

  "diff": {
    // null on the first snapshot of a session (no prior to compare against);
    // populated thereafter. Carries temporal change relative to the previous
    // snapshot — this is the "what just happened" feed for the storyteller.
    "since_snapshot_id": "snapshot-1779235454",
    "since_captured_at_ingame": "2026-01-10",
    "ingame_days_elapsed": 0,

    // City-wide zone count changes. Bulk growth/decline signal.
    "zones_delta": {
      "residential": { "from": 415, "to": 423, "delta": 8 }
    },

    // Per-district zone count changes. Subdivision-opening signal — when
    // residential count jumps inside one district, a new neighborhood is
    // filling in. Only districts with at least one zone-type change appear.
    "district_zone_deltas": {
      "Pine Quarter": {
        "residential": { "from": 200, "to": 245, "delta": 45 }
      }
    },

    // v0.4 — per-district demolition + construction counts, separated by
    // zone-type. Stories about displacement and gentrification need both
    // sides of the churn, not just the net change in `district_zone_deltas`.
    // null on the first snapshot of a session; populated thereafter.
    "building_churn": {
      "total_demolished": 14,
      "total_constructed": 22,
      "demolitions_by_district": {
        "Pine Quarter":    { "residential": 6, "industrial": 2 },
        "The North Yards": { "industrial": 4 }
      },
      "constructions_by_district": {
        "Pine Quarter": { "residential": 12 }
      }
    },

    // Named-building churn. Catches both player-renamed buildings
    // (intentional canon link, e.g. "Conklin Ranch") and CS2's auto-named
    // civic / service buildings (e.g. "Inger Brevik Elementary"). The agent
    // treats these as candidate events/*.md entries.
    "named_buildings": {
      "added":   [ { "id": "...", "name": "Inger Brevik Elementary", "type": "service", "district": "Pine Quarter" } ],
      "removed": [],
      "renamed": [ { "id": "...", "from": "Old Name", "to": "New Name", "type": "...", "district": "..." } ]
    },

    "outside_connections": {
      "added":   [ { "id": "...", "name": "..." } ],
      "removed": [],
      "changed": []
    },
    "water_sources": {
      "added":   [],
      "removed": [],
      "changed": []
    }
  }
}
```

### Mapping to storytelling entities

| Source | Storytelling project entity | Notes |
|---|---|---|
| `carto/processed/districts/<slug>.md` | `places/*.md` (type: neighborhood) | One markdown file per district, with adjacency + assigned buildings. |
| Per-district named buildings in those chunks | `places/*.md` (type: landmark, civic, industrial) | Civic buildings come through individually; generic zoned buildings are deduped + aggregated. |
| `citizens_sample[]` (snapshot) | `characters/*.md` candidates | Most sampled citizens stay anonymous; some become named characters when the story needs them. |
| `services.coverage_gaps` (snapshot) | `events/*.md` candidates | Pressure points the story can hang scandals or political campaigns on. |
| `diff.zones_delta` (snapshot) | `events/*.md` | Bulk residential/commercial growth signals when an in-world period saw real expansion. |
| Trade partner changes (snapshot diff) | `events/*.md` | New trade route opening = potential event. |

## Identity conventions

- **`id`** = CS2 ECS entity, serialized as `"<index>-<version>"`. Stable across snapshots within a save; not stable across new game starts.
- **`snapshot_id`** = `"snapshot-<unix-ts>"`. Matches the filename stem.
- **`session_id`** = `"session-<unix-ts>"`. Set once when the mod loads (CS2 launch). Every snapshot in the same play session carries the same `session_id`. Changes only when the user fully restarts CS2.
- **Cross-references** (e.g. `building.district_id`) always use the referenced entity's `id`. Never embed copies.

## What's emitted today (v0.9)

- Metadata header: `schema_version`, `snapshot_id`, `session_id`, timestamps — populated.
- `city.*` — name, money, happiness, health, tourists, attractiveness, danger, milestone, xp, zones, `churn` (incl. `moved_away_by_reason`), `social`, `budget` (income + residential tax only — see [#28](https://github.com/williamlang/city-storytelling-mod/issues/28)).
- `pollution`, `land_value` — per-district + city-wide cell-grid averages sampled at building positions.
- `crime` — per-district active-criminal counts (v0.8; reworked from v0.6's per-building accumulator).
- `citizens_sample` — up to 30 residents, all `Followed` plus a timestamp-seeded random fill.
- `outside_connections[]`, `water_sources[]` — populated from `CustomName` entities.
- `district_zones` — per-district building-type counts.
- `diff.*` — `zones_delta`, `district_zone_deltas`, `building_churn`, `named_buildings`, `outside_connections`, `water_sources`, `ingame_days_elapsed` — populated from second snapshot onward.
- `services.education` — per-school enrollment vs. capacity + city rollup by tier (v0.9). `null` until the city has a school.
- `demographics`, `trade`, rest of `services` — still null / empty.

Spatial data (districts, buildings, adjacency) lives in `carto/processed/` chunks — see [the Carto integration issue (#17)](https://github.com/williamlang/city-storytelling-mod/issues/17).

Runtime verification of the most recent schema versions on Windows is tracked in [#30](https://github.com/williamlang/city-storytelling-mod/issues/30) (v0.6 land_value + crime) and [#33](https://github.com/williamlang/city-storytelling-mod/issues/33) (v0.7 citizens_sample).

## Implementation order (next fields to ship)

Roughly easiest → hardest:

1. **Citizen wealth tier** — extends `citizens_sample` with the deferred wealth field; needs `CitizenHappinessParameterData` singleton + household `Resources` buffer join.
2. **Demographics aggregations** — by-age / by-education / by-wealth rollups over the same filtered resident set the v0.7 sampler walks.
3. **Trade flows** — multi-component join via `Game.Economy`.
4. **Service coverage gaps** — depends on building service-area data; advanced.

Each lands as its own commit; bump `schema_version` only if the shape of an existing field changes meaningfully.

## Versioning

- `0.1` — initial skeleton. Embedded spatial data (`districts[]`, `buildings[]`, etc.) and per-building churn diff.
- `0.2` — Spatial data extracted to Carto chunks (`carto/processed/`). Snapshot retains city stats, demographics, trade, and the bulk-signal diff (`zones_delta`, `outside_connections`, `water_sources`). Per-building churn removed; rebuild against Carto chunks if it becomes story-relevant.
- `0.3` — Added `map.*` block (world identity). Carto pipeline expanded to include Network (roads) + MapTile (footprint); new-city Carto auto-trigger on save-load edge.
- `0.4` — Added top-level `pollution` block (per-district + city-wide air/ground/noise), `city.churn` (births/deaths/move-ins/move-aways daily rates), and `diff.building_churn` (per-district demolition + construction counts separate from the net `district_zone_deltas`). Implements the top-3 from the [snapshot fields wishlist](https://github.com/williamlang/city-storytelling-mod/issues/18).
- `0.5` — Added `city.social` (homeless / unemployed / crime), `city.budget` (income + residential tax — non-residential tax + expense dropped pending parameter-shape investigation), and `city.churn.moved_away_by_reason` (Game.Agents.MoveAwayReason breakdown). All city-wide rolls via CityStatisticsSystem.
- `0.6` — Added top-level `land_value` and `crime` blocks: per-district + city-wide averages, same per-building → bin-by-district pattern as v0.4 pollution. `land_value` reads `LandValueSystem`'s cell grid; `crime` reads the per-building `Game.Buildings.CrimeProducer` component (so its sample population is a subset of buildings, distinct from pollution's).
- `0.7` — `citizens_sample` is now a real per-citizen array (up to 30 entries) instead of an empty placeholder. Every `Followed` citizen is always included; remaining slots filled with a timestamp-seeded uniform random sample of residents. Per-entry fields: name, age band, education, gender, happiness, home district, workplace, school, followed/is_criminal flags. Wealth tier deferred.
- `0.8` — current. `crime` reworked from a building-side `CrimeProducer.m_Crime` average (saturated everywhere — first real-data check on Halverson Crossing showed every district reading ~the same) to a count of active resident criminals binned by home district. Shape changed from `{ average, samples }` per scope to `{ active_criminals }` per scope.
- `1.0` — full schema implemented, used in at least one playthrough end-to-end, agent has consumed and produced grounded fiction from it.

