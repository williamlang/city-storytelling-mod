# Snapshot schema (v0.3)

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

## The shape

```json
{
  "schema_version": "0.3",
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

  "citizens_sample": [
    // Up to N sampled citizens, not every citizen.
    // { "id": "...", "name": "...", "age": N, "education": "...", "wealth_tier": "...",
    //   "home_district_id": "...", "workplace_company_id": "..." }
  ],

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
    // "coverage_gaps": [ { "service": "fire", "district_id": "..." } ]
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

## What's emitted today (v0.2)

- Metadata header: `schema_version`, `snapshot_id`, `session_id`, timestamps — populated.
- `city.*` — most fields populated (name, money, happiness, health, tourists, attractiveness, danger, milestone, xp, zones).
- `outside_connections[]`, `water_sources[]` — populated from `CustomName` entities.
- `diff.zones_delta`, `diff.outside_connections`, `diff.water_sources`, `diff.ingame_days_elapsed` — populated from second snapshot onward.
- `citizens_sample[]`, `demographics`, `trade`, `services` — still null / empty.

Spatial data (districts, buildings, adjacency) lives in `carto/processed/` chunks — see [the Carto integration issue (#17)](https://github.com/williamlang/city-storytelling-mod/issues/17).

## Implementation order (next fields to ship)

Roughly easiest → hardest:

1. **`citizens_sample[]`** — sample N citizens, pull name (via `Lifepath`), age, education, wealth, home, work.
2. **Demographics aggregations** — once we have citizen filters, cheap to compute alongside.
3. **Trade flows** — multi-component join via `Game.Economy`.
4. **Service coverage gaps** — depends on building service-area data; advanced.

Each lands as its own commit; bump `schema_version` only if the shape of an existing field changes meaningfully.

## Versioning

- `0.1` — initial skeleton. Embedded spatial data (`districts[]`, `buildings[]`, etc.) and per-building churn diff.
- `0.2` — Spatial data extracted to Carto chunks (`carto/processed/`). Snapshot retains city stats, demographics, trade, and the bulk-signal diff (`zones_delta`, `outside_connections`, `water_sources`). Per-building churn removed; rebuild against Carto chunks if it becomes story-relevant.
- `0.3` — current. Added `map.*` block (world identity). Carto pipeline expanded to include Network (roads) + MapTile (footprint); new-city Carto auto-trigger on save-load edge.
- `1.0` — full schema implemented, used in at least one playthrough end-to-end, agent has consumed and produced grounded fiction from it.

