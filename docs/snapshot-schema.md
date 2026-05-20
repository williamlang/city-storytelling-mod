# Snapshot schema (v0.1)

The mod emits JSON snapshots; the [sibling storytelling project](../../city-storytelling/CLAUDE.md) ingests them, diffs successive snapshots, and turns observed changes into characters, companies, places, and events.

This document defines the contract. The mod is the producer; the agent is the consumer.

## File naming and location

```
%LOCALAPPDATA%\..\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityStoryMod\snapshot-<unix-ts>.json
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

## The shape

```json
{
  "schema_version": "0.1",
  "snapshot_id": "snapshot-1779083749",
  "session_id": "session-1779083100",
  "session_started_at_utc": "2026-05-17T22:45:00Z",
  "captured_at_utc": "2026-05-17T22:55:49Z",
  "captured_at_ingame": null,

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

  "districts": [
    // {
    //   "id": "...", "name": "...",
    //   "population": N,                 // residents whose home is in this district
    //   "jobs": N,                       // workers whose workplace is in this district
    //   "zones": { residential, commercial, industrial, office, extractor,
    //              service, transformer, water_pumping, other },
    //   "named_buildings": [ "<id>", ... ]   // ids of buildings[] entries in this district
    // }
  ],

  "buildings": [
    // {
    //   "id": "...",
    //   "name": "...",                  // CS2-rendered name (custom or auto-named)
    //   "custom_named": true,
    //   "prefab_name": "WaterTower01",  // asset name from PrefabSystem
    //   "type": "transformer|water_pumping|extractor|industrial|commercial|office|residential|service",
    //   "efficiency": null,             // not yet wired (buffer-typed component)
    //   "condition": N,                 // raw m_Condition (level/XP scale, not 0-100)
    //   "citizens_present": N,          // current occupants
    //   "renter_count": N,
    //   "company": {                    // null for civic services
    //     "id": "...", "name": "...", "custom_named": false,
    //     "sector": "commercial|industrial|office",
    //     "subtype": "FurnitureStore|SawMill|...",
    //     "headcount": N
    //   },
    //   "district_id": "..."
    // }
    // Filtered to entities with the CustomName component. This covers both:
    //   - Player-renamed buildings (intentional canon link, e.g. "Conklin Ranch")
    //   - CS2's auto-named service / civic buildings (e.g. "Halverson Tower" for a
    //     water tower, "Selkirk Power Transformer" for a transformer station)
    // Both are useful candidates for canon `places/*.md`. The storytelling agent
    // can heuristically distinguish them if it cares, but for most purposes
    // "this building has a unique name" is the signal.
  ],
  // Note: there is no top-level companies[] in v0.1. Company info is folded into
  // each building.company. CS2 has many anonymous template companies in stock
  // commercial zones; surfacing only the company occupying a named building keeps
  // the snapshot focused on narratively-relevant businesses.

  "citizens_sample": [
    // Up to N sampled citizens, not every citizen.
    // { "id": "...", "name": "...", "age": N, "education": "...", "wealth_tier": "...",
    //   "home_district_id": "...", "workplace_company_id": "..." }
  ],

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
    // populated thereafter. Carries change relative to the previous snapshot.
    "since_snapshot_id": "snapshot-1779235454",
    "since_captured_at_ingame": "2026-01-10",
    "ingame_days_elapsed": 0,
    "buildings": {
      // named-building churn (only entities currently in buildings[])
      "added":   [ { "id": "...", "name": "...", "type": "..." } ],
      "removed": [ { "id": "...", "name": "...", "type": "..." } ],
      "changed": [
        { "id": "...", "name": "...", "changes": {
          "name":            { "from": "Old Name", "to": "New Name" },
          "type":            { "from": "industrial", "to": "extractor" },
          "district_id":     { "from": null, "to": "..." },
          "company_subtype": { "from": "Bar", "to": "Restaurant" },
          "has_company":     { "from": true, "to": false }
        } }
      ]
    },
    "zones_delta": {
      // bulk zone-count changes (catches residential/commercial growth and
      // demolitions that don't surface in buildings[] because most lots are
      // not custom-named). Only keys where the count actually changed appear.
      "residential": { "from": 415, "to": 423, "delta": 8 }
    }
  }
}
```

### Mapping to storytelling entities

| Snapshot field | Storytelling project entity | Notes |
|---|---|---|
| `districts[]` | `places/*.md` (type: neighborhood) | One markdown file per district. |
| `buildings[]` | `places/*.md` (type: landmark, civic, industrial) | Only notable ones — not residential houses. |
| `companies[]` | `companies/*.md` | One per company. |
| `citizens_sample[]` | `characters/*.md` candidates | Most sampled citizens stay anonymous; some become named characters when the story needs them. |
| `services.coverage_gaps` | `events/*.md` candidates | Pressure points the story can hang scandals or political campaigns on. |
| Trade partner changes (diff) | `events/*.md` | New trade route opening = potential event. |

## Identity conventions

- **`id`** = CS2 ECS entity, serialized as `"<index>-<version>"`. Stable across snapshots within a save; not stable across new game starts.
- **`snapshot_id`** = `"snapshot-<unix-ts>"`. Matches the filename stem.
- **`session_id`** = `"session-<unix-ts>"`. Set once when the mod loads (CS2 launch). Every snapshot in the same play session carries the same `session_id`. Changes only when the user fully restarts CS2.
- **Cross-references** (e.g. `building.district_id`) always use the referenced entity's `id`. Never embed copies.

## What's emitted today

As of v0.1.0 of the mod:

- `schema_version`, `snapshot_id`, `captured_at_utc` — populated.
- `city.citizens_total` — populated from a raw `Game.Citizens.Citizen` entity count. **Broader than the HUD population** (includes tourists, commuters, transient entities); see CLAUDE.md status section.
- Everything else — `null` or empty array.

## Implementation order (next fields to ship)

Roughly easiest → hardest:

1. **`city.name`, `city.money`, `city.happiness`** — single calls into `CityConfigurationSystem` / city stats.
2. **`captured_at_ingame`** — hook `TimeSystem` for the in-game date.
3. **`districts[]` (id + name + population)** — query `Game.Areas.District`, count citizens whose `HouseholdMember → Household → PropertyRenter → Building → CurrentDistrict` chain lands in each.
4. **`buildings[]` notable list** — query buildings with `BuildingData` filtered by landmark / service prefab flags. Skip residential.
5. **`companies[]`** — query `Game.Companies.*` for active commercial / industrial companies, pull name + headcount.
6. **`citizens_sample[]`** — sample N citizens (e.g. 50), pull name (via `Lifepath`), age, education, wealth, home, work.
7. **Demographics aggregations** — once we have filter components, these are cheap to compute from the same query results.
8. **Trade flows** — multi-component join; defer until building / company exports are solid.
9. **Service coverage gaps** — depends on building service area data; advanced.

Each lands as its own commit; bump `schema_version` only if the shape of an existing field changes meaningfully.

## Versioning

- `0.1.x` — current. Skeleton schema; most fields null. Additive changes allowed without version bump.
- `0.2` — once `districts`, `companies`, and a basic `citizens_sample` are populated. The agent should be useful at this point.
- `1.0` — full schema implemented, used in at least one playthrough end-to-end, agent has consumed and produced grounded fiction from it.

## Side-by-side output mode (planned)

Future setting: write snapshots directly into a sibling repo's `imports/` folder so the agent can consume without a copy step. Off by default.
