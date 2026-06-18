# Mod-effects registry

My grounding rules in `CLAUDE.md` ("Grounded in city state") are calibrated against Cities: Skylines 2's **vanilla** mechanics — the population scale bands, the four-band citizen aging, services, the economy. When the player has peer mods loaded that change those mechanics, vanilla-grounded narration drifts from what they actually see in-game: ages outside the vanilla range, population numbers that imply a different real-world scale, or whole systems (like elections) the vanilla rules don't know exist.

This file closes that gap. Each snapshot lists the enabled code mods under `snapshot.mods.loaded[]` (each with an `id`, `name`, `version`). This registry says what each known `id` *does to the story*. **For every entry whose `id` appears in `snapshot.mods.loaded`, its description is a hard grounding input — on equal footing with the snapshot fields themselves.** I read the matching entries before I write, the same way I read `city.population_hud`.

## How to use this file

1. Read `snapshot.mods.loaded[]` from the latest snapshot.
2. For each loaded `id`, find the entry below.
3. Apply every matching entry's "Effect on the story" as a grounding constraint.
4. **Unknown mod loaded?** If a loaded `id` has no entry here, I don't guess what it does. Once per playthrough I can offer, in passing: *"I see `<name>` is loaded but I don't have a note on how it changes the city — tell me what it does and I'll factor it in (and I'll add it to `mod-effects.md` so it sticks)."* Then I write the entry. Never noisy, never repeated.

## How entries are maintained

- This is the scaffolded **starting** registry. The mod re-syncs unmodified template files forward, so new versions of this file arrive on existing cities — but **if I (or the player) edit an entry, it's left alone** on re-sync. So per-city overrides are safe: if the player tells me something specific about how a loaded mod is configured for their run, I edit that mod's entry here and it persists.
- Entries are prose, not field detectors. A mod that only tunes vanilla simulation values (tax curves, growth rates) without adding new components still gets an entry — I describe the effect in words.

## Entry format

```
### <assembly id> — <human name>
**Effect on the story:** <how it bends storyteller-relevant gameplay — scale, aging, services, new systems>
**What changes in the snapshot:** <fields it adds or shifts, if any>
**Defer to it for:** <questions this mod is now the authority on, so I stop speculating>
```

---

## Known mods

### Elections — a real civic-political layer
**Effect on the story:** The city runs actual mayoral elections — a term clock, 2–4 candidates, optional parties, polls, an election day, and a winning mayor whose platform changes city policy. This is **built-in dramatic structure I should treat as canon**, not invent around. When `snapshot.politics` is present:
- Its **candidates are real residents** (with a real name, age band, education, household wealth, job, and a trait like *Honest* / *Populist* / *Corrupt*) — ideal seed material for `characters/`. Its **parties** are `factions/`. Its **mayor**, **results**, and **legislation** are civic facts.
- **Parties and contested elections can exist even at small population**, which my vanilla scale bands ("small-town: politics is personal, no parties yet") would otherwise rule out. When Elections is loaded, the mod's reality wins: the party *exists*. I still scale its **texture** to the city (a "party" in a town of 1,800 is a handful of people who know each other, not a machine with a press operation) — but I don't deny its existence.
- The **scandal engine is live** — donations, bribes, vote-tampering, corruption investigations (`politics.integrity.*`, candidate `corruption_risk_steps`, `mayor.bribe_total`). These are real pressure for `secrets/` and arc tension, not things I should fabricate independently.
**What changes in the snapshot:** adds the top-level `politics` block (stage, schedule, parties, candidates, poll/result tallies, legislation, integrity) and `diff.politics` (stage change, new mayor, concluded election). See `docs/snapshot-schema.md` and the `/events-resolve` "Election cycle" step.
**Defer to it for:** who's running, who's mayor, which party holds power, what got legislated, election dates and outcomes. I stop speculating about "soft politics" and read the race from `politics`.

<!--
Add an entry here whenever a new mod's effects matter to the story. Keep the
format above. Elections, InfoLoom, and other peer-mod integrations append their
own entries as they land.
-->
