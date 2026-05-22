# Snapshot wishlist

What the storytelling agent wants from the [snapshot schema](../../docs/snapshot-schema.md), ranked by narrative leverage — not by ease of implementation.

The mod's `ExportSystem` produces the snapshot; the agent running in the city folder consumes it. This doc is the consumer's voice — even though both halves now ship together in CityStoryMod, the producer/consumer split inside the codebase is still worth keeping clear.

## Highest narrative leverage

Each of these directly hands the agent **friction**, which is what every grounded story needs.

- **Pollution by district** — air, ground, noise. The single biggest driver of class stories, NIMBY fights, lawsuits, and health scandals. "Birchwood's complaints are 70% noise" is an airport story I can write tomorrow.
- **Land value by district**, snapshotted so the trend is visible. Gentrification, displacement, "the developers smelled it first" arcs all need this signal — a single value isn't enough; the slope is the story.
- **Crime rate by district.** Classic neighborhood-decline / neighborhood-rebirth arc. Without it the agent is guessing.
- **Unhappiness reasons aggregated by district.** CS2 already tracks *why* citizens are unhappy. The breakdown ("42% traffic, 18% no schools, 12% noise") is far more usable than a single happiness score, because each reason maps to a different kind of plot.
- **Population churn** between snapshots — births, deaths, move-ins, move-outs, ideally by district. A wave of move-outs from one district is a story before any building changes.
- **Building demolitions and rezonings in `diff`.** The current `zones_delta` catches the count. Surfacing *which* buildings disappeared and what replaced them tells the agent who got displaced.
- **`captured_at_ingame`** (already on the mod's roadmap). Without an in-world date the agent can't anchor `events/*.md` correctly.

## Mid tier — explicit story beats

- **Budget breakdown** — revenue by tax type, spending by service category. "The police budget tripled this year" is a direct political plot the agent can hang a campaign on.
- **Company financial signal** — even a coarse status (`thriving | stable | struggling | failing`) per company. Headcount alone doesn't reveal who's winning. Companies have arcs; the agent needs to know which way they're bending.
- **Notable game events the sim already generates** — fires, accidents, deaths, evictions, business closures. The game knows these; the snapshot doesn't surface them. Even a thin `events[]` array per snapshot would be high-leverage.
- **Disasters** — floods, forest fires, tornadoes. Obvious story catalysts; CS2 already models them.
- **Milestone unlocks / city achievements.** Already canonical in-world beats. They should propagate straight into `events/`.

## Lower tier — texture, not catalysts

- **Citizen lifepath changes between snapshots** — wealth-tier jumps, education completed, job changes for citizens in `citizens_sample[]`. Lets characters *evolve* across sessions without the agent inventing the arc.
- **Education / job mismatch rate.** Brain-drain or oversupply stories.
- **Traffic bottleneck hotspots.** Infrastructure-fight plots ("the Conrail bridge is at 110%").
- **Building age / years-since-built**, surfaced in `buildings[]`. Lets the agent write "the oldest mill in the city" without guessing.

## Cuts the agent doesn't miss

If the mod has a build budget, the following are deprioritized from the agent's side:

- `tourists_current` / `tourists_average` / `attractiveness` — aggregate flavor; rarely the spine of a story.
- `xp` / `milestone_level` — derivable from milestone *events* if those land.
- `other_named[]` — useful for debugging the mod, not for the agent.

## Top three if forced to pick

1. Pollution per district.
2. Population churn (births / deaths / move-ins / move-outs per district).
3. Demolition + rezoning detail in `diff`.

Between them they generate the bulk of the friction needed to make sessions feel earned rather than invented.

---

## Annotated changes (and the rarer free-floating annotation)

Not snapshot data in the conventional sense — but probably the single highest-leverage feature the mod could add, and worth its own section.

### The problem it solves

The snapshot can tell the agent *what* changed in the city. It cannot tell the agent *why the player did it*. Intent is the hardest signal to infer:

- A new factory got built. Was it because the player loves industrial sprawl, or because they were role-playing a corrupt deal with a developer character?
- A district was bulldozed. Was that climate retreat, gentrification, or a sandbox whim?
- A citizen got renamed "Marcus Devereaux." That citizen now matters — but the snapshot just shows a name change.

Without an intent channel, the agent reverse-engineers narrative from state and gets it wrong sometimes. With one, the player whispers the why directly and the agent never has to guess.

### Primary mode: annotated changes

Every detected change in the diff — a build, a rezone, a rename, a demolition — is a candidate for a one-line *why* from the player. The annotation rides on the change rather than floating free in time. Mechanically this is the cleanest pairing: the diff already detects what happened, and the annotation enriches it with intent. The agent never has to guess which building or citizen the note refers to, because it arrives bolted onto the change.

Three surfaces, ordered by friction (lowest first):

**1. Rename dialog grows an optional "why" field.** This is the highest-hit-rate surface. The player is already opening the rename dialog *because* this entity matters narratively — that's the entire signal renaming sends today. Adding one optional text field to that dialog captures the moment of canonization at near-zero added cost.

- "Conklin Ranch" → *"Named for the patriarch — keep him in mind."*
- "Marcus Devereaux" → *"The developer who's been buying up the riverfront."*
- "Halverson Tower" → *"Named after a councillor's family — they pushed it through."*

**2. "Annotate my last change" hotkey.** For changes that aren't renames — a new building placed, a rezone, a demolition. The player makes the move, hits the hotkey, types one line, hits enter. The mod pairs the annotation with the most recent change it detected (it keeps a small ring buffer of recent changes for exactly this binding).

**3. Free-floating moment annotation (secondary).** A separate hotkey for moments that *aren't* changes — a protest, a speech, a tense lunch — where nothing in the diff surfaces but something happened in the player's head. Same overlay, no anchored change. Less common but covers the gap.

Across all three modes, **friction must stay near-zero**. If the player has to think about whether to annotate, they won't. The rename hook is safest for that reason: it piggybacks on a UI the player is already using.

### Schema addition

Annotations primarily attach **inside the diff**, so the agent reads them in the same step as the change they enrich:

```json
"diff": {
  "buildings": {
    "added": [
      {
        "id": "...",
        "name": "Halverson Tower",
        "type": "service",
        "annotation": {
          "text": "Named for the councillor's family — they pushed it through.",
          "category": "place",
          "captured_at_ingame": "2026-03-14"
        }
      }
    ],
    "changed": [
      {
        "id": "...",
        "name": "Conklin Ranch",
        "changes": { "name": { "from": "Farm 14", "to": "Conklin Ranch" } },
        "annotation": {
          "text": "Conklin's the patriarch — keep him in mind.",
          "category": "character",
          "captured_at_ingame": "2026-03-14"
        }
      }
    ]
  }
}
```

Free-floating annotations (mode 3) land in a separate top-level array, since they have no diff entry to ride on:

```json
"annotations": [
  {
    "id": "ann-1779083749-7",
    "captured_at_ingame": "2026-03-14",
    "text": "Protest outside city hall — student-led, climate group.",
    "district_id": "98765-2",
    "category": "event"
  }
]
```

`category` is optional in either mode — assigned by a single keystroke when the overlay opens (`c` character, `p` place, `e` event, `s` secret, `?` uncategorized). The agent classifies if it's missing.

Annotations appear only in the snapshot whose interval contains them — they do not repeat across snapshots. The mod buffers them to a sidecar JSON queue between captures, flushes them into the next snapshot's diff entries (or the top-level `annotations[]` for free-floating ones), and clears the queue on successful write.

### What the agent does with them

- An **annotation on an `added` building** turns that building into an immediate `places/*.md` candidate, with the annotation text seeding the file's first paragraph.
- An **annotation on a renamed citizen** (mode 1 hit) triggers promotion to a `characters/*.md` — the rename was already the canon-linking signal; the why-line gives the agent the agenda.
- An **annotation on a demolition** seeds an `events/*.md` entry — who lost what.
- A **free-floating annotation with `category: event`** becomes a `stories/*.md` vignette candidate (news clipping, transcript, eyewitness scene).
- A **`category: secret` annotation in any mode** routes into `secrets/*.md` and respects the "don't quote in chat" rule the same way authorial secrets do.

### Why this beats more schema expansion

- **One free-text field captures arbitrarily nuanced intent** that no number of structured fields could match.
- **It's a forcing function for play-with-the-story-in-mind**, which is the whole project premise. The player participates in authorship in-game, not only at session boundaries.
- **It's a low-ceremony pipe.** The player doesn't context-switch out of CS2.
- **It pairs intent with state mechanically.** Anchoring to changes means the agent never has to guess which building or citizen the note refers to.

### Risks and caveats

- **Friction must be near-zero.** The rename-dialog hook is the safest surface for that reason; standalone hotkeys are useful but rarely used if even slightly clunky.
- **Annotations are optional.** The system must work end-to-end without them — they are a booster on top of inferred state, never a dependency.
- **A rename without a why is still a rename.** The why field stays optional inside the dialog; renaming alone remains a valid canon-linking signal as it is today.
- **The text is unstructured.** Parsing intent from one line of prose is the agent's job, not the mod's. The mod should resist the urge to over-schematize.
- **Secrets typed in-game are still secrets.** If the player tags an annotation `s`, the agent must honor the "don't quote in chat" rule.

### Implementation hints for the mod side

- **Mode 1 (rename hook) is the priority build.** It piggybacks on a UI the player already uses, requires no new hotkey, and probably captures 70%+ of the value of the whole feature.
- Mode 2 needs a small ring buffer of recently-detected changes so "annotate the last one" has something concrete to bind to. A few seconds of memory is enough.
- A CS2 mod hotkey plus an ImGui-style text overlay is the well-trodden path for modes 2 and 3.
- The hovered entity under the cursor at hotkey time is the natural anchor for mode 3.
- Pause the sim while any overlay is open so the player isn't penalized for stopping to write.
