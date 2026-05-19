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
    "population_hud": null,
    "citizens_total": 2,
    "money": null,
    "happiness": null
  },

  "districts": [
    // { "id": "...", "name": "...", "population": N, "area_hectares": N, "dominant_zone": "residential|commercial|industrial|office|mixed" }
  ],

  "buildings": [
    // { "id": "...", "name": "...", "type": "...", "district_id": "...", "built_at_ingame": "..." }
    // Initially only notable/named buildings (landmarks, services). Not every house.
  ],

  "companies": [
    // { "id": "...", "name": "...", "sector": "...", "headcount": N, "building_id": "...", "district_id": "..." }
  ],

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
