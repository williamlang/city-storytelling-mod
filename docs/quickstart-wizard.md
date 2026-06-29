# Quickstart Wizard — spec & implementation plan

**Issue:** [#42](https://github.com/) — Quickstart Wizard: guided canon bootstrap on first load of a fresh city
**Folds in:** [#35](https://github.com/) — Pin the map's real-world region in canon, with player override
**Status:** Design — not yet implemented
**Schema impact:** none (canon frontmatter + per-city `settings.json` only; no snapshot-schema bump)

---

## 1. Goal

When a player loads a save with no Ghostwriter canon yet, the mod should *lead* them through founding the city's story instead of relying on them to discover the `/new-city` command in the chat menu — and it should feel **fast**. The pieces already exist (fresh-city detection, the auto-Carto trigger, the `bootstrapped` flag, the provider nudge) but the initiative is entirely on the player today.

The wizard:

1. **Signals** a fresh city is ready to found (a second toolbar-icon flash colour + a dismissible banner).
2. **Sequences prerequisites** deterministically (provider configured → spatial data exported) before any LLM call.
3. **Collects founding config instantly** in one native form — region, name, feature toggles — with **zero LLM calls** in this part.
4. **Generates the narrative in a single LLM round-trip** — one "Found my city" click → the agent reads spatial data and writes the whole founding (history, premise, canon) in one run.
5. **Commits** canon, flips `bootstrapped: true`, shows a result summary, and hands off to `/session-start`.

---

## 2. Decisions (locked)

| Question | Decision |
|---|---|
| UI shape | **Native config form**, not LLM-driven stepped cards. One instant form for all fixed-choice config. |
| LLM strategy | **One-shot generate.** Config is collected natively (no LLM); a single generation call writes the founding. Drops ~4 round-trips to 1. |
| Trigger | **Dismissible banner**, no auto-run against the LLM. Plus a **second toolbar-icon flash colour** (warm gold/amber) distinct from the unseen-reply pulse. |
| Region (#35) | **Native dropdown**, C#-computed default, player override. Adds a validated `region:` enum to `canon/city.md`. **Metadata first, broaden naming pools later.** |
| Spec location | This file (`docs/quickstart-wizard.md`). |

---

## 3. Architecture: native config, one generation call

The driving constraint: **routing each decision through the LLM is the slow path.** An agent run is single-shot (`Storyteller/AgentLoop.cs:44-114`) — it runs the tool loop to completion and ends. A per-step interactive wizard would mean re-loading the system prompt + snapshot + carto files on every step (~4 sequential waits for region, name, history, toggles), most of which are fixed-choice config that needs no model at all.

So we split by what actually needs the LLM:

| Decision | LLM? | How |
|---|---|---|
| **Region** | No | 7 fixed enum values; default is a trivial C# heuristic from `map.latitude` + `map.theme`. |
| **Toggles** (secrets, level-up) | No | Fixed binary choices. |
| **Name** | Only if player wants suggestions | Player types their own (instant), or checks "suggest one for me" → the one generation call names it. |
| **Founding history + premise** | Yes | The genuinely creative work — produced in the single generation call. |

The config (region, name-or-suggest, toggles) is gathered in **one instant native form**. The player clicks **Found my city**, and exactly **one** agent run does everything: read spatial data → honour the config → write `canon/city.md`, `canon/playthrough-premise.md`, `settings.json` (with `bootstrapped: true`) → report a summary.

```
┌─ fresh city detected (C#) ───────────────────────────────────────────────┐
│  ExportSystem save-load edge → isNewCity → flash binding + banner          │
└───────────────────────────────────────────────────────────────────────────┘
                              │ player clicks "Found this city"
                              ▼
┌─ native prerequisite gate (C#/React, NO LLM) ─────────────────────────────┐
│  provider configured?  ──no──▶ card: "set up a model in Options"           │
│  carto/processed/ ready? ─no─▶ card: spinner, "mapping your terrain…"      │
└───────────────────────────────────────────────────────────────────────────┘
                              │ both green
                              ▼
┌─ native config form (INSTANT, no LLM) ────────────────────────────────────┐
│  Region:  [ North America ▾ ]   (C#-defaulted from latitude/theme)         │
│  Name:    [ ________ ]  ☐ suggest one for me                               │
│  Secrets: (●) Hidden  ( ) Shown                                            │
│  Level-up:(●) On      ( ) Off                                              │
│                         [ Found my city ▶ ]                                │
└───────────────────────────────────────────────────────────────────────────┘
                              │ ONE submit, config injected
                              ▼
┌─ single generation run (agent) ───────────────────────────────────────────┐
│  /new-city with CONFIG block → read spatial data → honour region/name/     │
│  toggles → write canon/city.md + playthrough-premise.md + settings.json    │
│  (bootstrapped=true) → call wizard_done with a summary                     │
└───────────────────────────────────────────────────────────────────────────┘
                              │ run finishes
                              ▼
┌─ result card (C#/React) ──────────────────────────────────────────────────┐
│  "Founded <Name>, <region> — <premise>."  + rename-save reminder           │
│  [ Start session 1 ]                                                       │
└───────────────────────────────────────────────────────────────────────────┘
```

What this buys us, versus an LLM-driven stepped wizard:
- **One wait, not four.** The only LLM latency the player feels is the single founding generation, behind a "founding your city…" progress state.
- **No `present_choice` tool, no per-step binding dance, no cross-run continuity dependency.** The wizard is a form plus one fire-and-await call.
- The founding *logic* (spatial reading, premise inference, canon writing) stays exactly where it is — `template/.claude/commands/new-city.md` + the inference rules in `template/CLAUDE.md:626-666`. We feed it config instead of asking questions.

Trade-off accepted: no curated "pick from 4 names / 3 histories" selection. The player types a name (or lets the agent pick) and gets one founding history. If they want a different take, they ask the storyteller in chat afterward ("rename it" / "give me a different founding story") — cheap and natural.

---

## 4. The agent side

### 4.1 Config block, not an interactive protocol

When the player clicks **Found my city**, `PromptUISystem` submits the `/new-city` command with a structured **CONFIG block** appended to the prompt, e.g.:

```
<<QUICKSTART_CONFIG>>
region: Europe
name: Selkirk Falls          # or:  name: (suggest)
tone: grounded-realist
focus: citizens, civic       # any of: citizens, civic (>=1 required; both = full default)
player_role: chronicler      # chronicler | character
player_character_name:       # only when player_role: character
real_world_refs: fictional   # fictional | real
cast_density: balanced       # tight | balanced | sprawling
content_maturity: pg-13
secrets_visibility: hidden
levelup_storylines: true
storyteller_proactivity: on-request   # on-request | proactive
git_versioning: false
integrations: infoloom, custom-chirps, elections   # placeholder; only detected+supported mods
<<END_CONFIG>>
```

(`era` is **not** in the config block — the agent derives it from the in-game date in the snapshot and writes `canon/era.md`; see §7.1.)

`template/.claude/commands/new-city.md` gains one branch near the top. The rule is **field-level**, not all-or-nothing: the config block simply *pre-supplies answers*; any field it doesn't carry is still asked in prose. So the prose flow is the **superset** — it always covers region, name, and toggles — and the config block just lets the wizard skip the questions it already answered.

> **Read the `<<QUICKSTART_CONFIG>>` block if present.** For each field it carries (`region`, `name`, `tone`, `focus`, `player_role`, `real_world_refs`, `cast_density`, `content_maturity`, `secrets_visibility`, `levelup_storylines`, `storyteller_proactivity`, `git_versioning`, `integrations`), treat that as the player's answer and do **not** re-ask it. `region` is a hard constraint; a literal `name` is used as-is; `name: (suggest)` means choose one grounded in the spatial data.
>
> **For every config field NOT supplied** (including when there is no config block at all — i.e. the player ran `/new-city` from chat), ask for it interactively in prose as today: the region question (§5.3), the name suggestions (4 grounded options), the founding-history pick (3–4 options), then the founding questions from §7.1 (tone, focus, player's place, real-world refs, cast density, maturity, secrets, level-up, proactivity). Wait for each reply before continuing.
>
> **Always derive era** from the in-game date in the snapshot (regardless of config) and write `canon/era.md` — it is never asked (§7.1).
>
> When everything is settled (from config, from prose, or a mix), write the founding history + premise, write `canon/city.md` (with `region:`), `canon/tone.md` (tone + focus + player's-place + real-world-refs — all story-shaping), `canon/era.md` (derived), `canon/playthrough-premise.md`, seed a `characters/` entry if `player_role: character`, and write `settings.json` (`cast_density`, `content_maturity`, `secrets_visibility`, `levelup_storylines`, proactivity/active-events, `git_versioning`, `bootstrapped: true`). Note `content_maturity` is a disclosure preference (§7.1) — it gates narration explicitness, never what canon is generated. If a config block was present, also call the `wizard_done` tool with a summary.

One command file, one founding engine, two entry styles:
- **Quickstart wizard** → full config block → zero questions → one-shot generate + `wizard_done`.
- **Chat `/new-city`** (dismissed the wizard, or re-founding) → no/partial config → prose asks for whatever's missing, keeping the curated name/history picks.

This guarantees the chat path is never left without region/name/toggles just because the wizard wasn't used — the questions live in the command, and the config block only suppresses the ones already answered.

### 4.2 New tool: `wizard_done`

The only new tool. Declared in `Storyteller/ToolSchemas.cs` (append to `_defaults`), handled in `Storyteller/ToolExecutor.cs` (`Execute` switch):

```jsonc
{
  "name": "wizard_done",
  "description": "Call once at the end of a quickstart founding to report the result to the native UI.",
  "input_schema": {
    "type": "object",
    "properties": {
      "city_name": { "type": "string" },
      "region":    { "type": "string" },
      "founded":   { "type": "string" },
      "premise":   { "type": "string", "description": "One-sentence playthrough premise." }
    },
    "required": ["city_name","region","premise"]
  }
}
```

Executor stashes the payload (thread-safe field, mirroring the `_pendingMessages` pattern); `PromptUISystem` drains it on the next `OnUpdate` into a `wizardDone` value binding that the result card reads. Belt-and-braces: the result is also derivable from the freshly written `canon/city.md`, and `bootstrapped: true` is the authoritative completion signal.

(No `present_choice` tool — the one-shot model doesn't need an outbound option channel.)

---

## 5. Region (#35) integration

### 5.1 Frontmatter enum

`template/canon/city.md` today has a free-text `region: TBD`. Constrain and document it:

```yaml
---
name: TBD
region: TBD          # enum: North America | Europe | Asia | Latin America | Africa | Oceania | Middle East
founded: TBD
population_at_start: TBD
geography: TBD
climate: TBD
---
```

### 5.2 Native dropdown with a C# default

Region is a dropdown in the config form, no LLM. The default is a **coarse C# heuristic** computed from the latest snapshot's `map.latitude` (+ `map.theme` as a tiebreaker) — e.g. latitude bands map to a best-guess region, defaulting to North America (the template's home turf) when ambiguous. The player can override to any of the seven values. Whatever they pick is passed in the CONFIG block as a hard constraint and written verbatim to `canon/city.md`.

Because all config precedes the single generation call, **region is automatically "known first"** — the agent has it in hand before it proposes a name or writes history. (This is the ordering #35 wanted, achieved for free by collecting config up front.)

### 5.3 Scope for this pass (locked): metadata first, broaden later

Ship `region:` as a pinned, validated enum and the dropdown now, but keep the agent's naming/history prose **North-America-strong** for v1. Per-region naming pools and founding-history shapes for the other six regions get broadened **incrementally as non-NA maps are playtested** — matching the "classifier tuning as more maps surface" approach already in the repo. In v1, a non-NA region pick still pins correct metadata and grounds cultural framing; the richest naming guidance stays North American until those pools are authored.

### 5.4 Downstream reads

`template/CLAUDE.md` premise-inference inputs and the `session-start` / `story-driven` commands read `region:` as an authoritative constraint (naming pools, cultural grounding) rather than re-deriving it from latitude each prompt. Prose-mode `/new-city` adds region as one numbered question before the name step, so both modes stay in parity.

---

## 6. The fresh-city signal (flash + banner)

### 6.1 Today's flash (reference)

Entirely client-side React (`UI/src/mods/storyteller/StorytellerToolbar.tsx:180,310-323`): `hasUnseen` flips true when `messages` grows while the panel is closed and the last message is from the assistant; cleared on open. The CSS is an **opacity** pulse with no colour change (`storyteller.module.scss:11-18`, `@keyframes ghostUnseenPulse`).

### 6.2 New "fresh city" flash — warm gold/amber, needs a C# binding

React can't see bootstrapped/fresh-city state on its own, so add a C# value binding:

- **`PromptUISystem`**: new `ValueBinding<bool> _quickstartAvailableBinding` (`Group, "quickstartAvailable", false`), registered with the others (~`PromptUISystem.cs:146`).
- **Set true** on a save-load edge when the city is fresh/un-bootstrapped: `settings.json` missing or `bootstrapped != true` (reuse the read from `IsCommandApplicable("new-city", …)`, `PromptUISystem.cs:744-752`). **Set false** when the wizard completes (`bootstrapped` flips / `wizard_done`) or the player dismisses for the session.
- **`bindings.ts`**: `export const quickstartAvailableBinding = bindValue<boolean>(GROUP, "quickstartAvailable", false)`.
- **Toolbar** (`StorytellerToolbar.tsx:442-452`): `const quickstartAvailable = useValue(quickstartAvailableBinding)`; second conditional class on the icon `img`:
  ```tsx
  className={`${styles.toolbarIcon}
    ${hasUnseen ? styles.toolbarIconFlash : ""}
    ${quickstartAvailable && !hasUnseen ? styles.toolbarIconQuickstart : ""}`}
  ```
- **SCSS** — warm gold/amber glow (locked), distinct from the opacity-only unseen pulse:
  ```scss
  .toolbarIconQuickstart { animation: ghostQuickstartPulse 1.1s ease-in-out infinite; }
  @keyframes ghostQuickstartPulse {
    0%, 100% { filter: none; }
    50%      { filter: drop-shadow(0 0 6px var(--quickstart-glow)) saturate(1.5); }
  }
  ```
  `--quickstart-glow` is a warm amber — reads as "new / inviting", clearly different from the neutral unseen pulse.

### 6.3 Banner

Reuse the existing setup-banner slot styling (`StorytellerToolbar.tsx:472-483`). When `quickstartAvailable && !setupNeeded`, show a "✨ New city — found its story" banner with a primary **Start** (opens the config form) and secondary **Later** (dismiss for session). If `setupNeeded`, the existing provider banner shows first (provider is a hard prerequisite).

---

## 7. Native UI

New component `UI/src/mods/storyteller/QuickstartWizard.tsx`, mounted from `StorytellerToolbar` when `wizardOpen` is true. It is mostly a single form, with a couple of native gate/result phases around it:

| Phase | Kind | Source |
|---|---|---|
| Prereq: provider | native gate | `setupNeededBinding` — link to Options, no LLM |
| Prereq: spatial data | native gate | `cartoExportingBinding` / poll for `carto/processed/` — spinner |
| **Config form** | native, instant | Region dropdown (C# default), name text, plus the founding-question set (§7.1) |
| Generating | progress | `isRunningBinding` — "founding your city…" |
| Result | native | `wizardDone` payload → summary + rename-save reminder + "Start session 1" |

Form details:
- **Region** dropdown pre-set to the C#-computed default; 7 enum values.
- **Name** text field. **Blank = suggest** — an empty field sends `name: (suggest)` and the agent picks one grounded in the spatial data. No separate checkbox.
- **Founding questions** (§7.1) — each renders as a radio group / dropdown with a recommended default pre-selected, so the player can one-click through. Layout follows §7.1's grouping: **Core** (region, name, tone, focus) always visible; **Story** and **Settings/behavior** tucked behind an **Advanced / optional** expander, collapsed by default. Narrative focus is two checkboxes (Citizens & families · Civic & political), both pre-checked, with a guard that keeps at least one checked. "Named character" reveals a conditional name field.
- One primary **Found my city** button submits everything (Core + whatever's in Advanced) in a single call.

No stepped Back/Next, no per-step thinking states — it's one form and one wait. The default path is: accept the Core defaults → *Found my city*; power users open Advanced to tune the rest. The same form is reused post-founding as a **Story Settings editor** to change any of these choices later (§11).

### 7.1 Founding question set (being decided)

Beyond region and name, the wizard asks a small set of founding questions. Each one:
- renders as a native control with a recommended default (the player can accept all defaults and just hit **Found my city**),
- becomes a field in the `<<QUICKSTART_CONFIG>>` block,
- becomes a prose question in `new-city.md`'s superset flow (so chat `/new-city` asks it too),
- writes to the appropriate canon/settings target.

**Final question set (locked).** Grouped so the form can show a short **Core** block and tuck the rest behind an **Advanced / optional** expander (§7) — every field has a safe default, so the player can accept all and hit *Found my city*.

**Core (always visible):**

| Field | Control / options | Default | Writes to |
|---|---|---|---|
| Region | dropdown, 7 enum values | C#-computed from latitude/theme | `canon/city.md: region` |
| Name | text (blank = suggest) | blank → agent suggests | `canon/city.md: name` |
| Narrative tone | radio: grounded-realist / dramatic / noir / hopeful / satirical | grounded-realist | `canon/tone.md` |
| Narrative focus | **two checkboxes** (independent): Citizens & families · Civic & political | both checked | `canon/tone.md` |

**Story (Advanced):**

| Field | Control / options | Default | Writes to |
|---|---|---|---|
| Player's place in the fiction | radio: Unseen chronicler / Named character (+ name) | Unseen chronicler | `canon/tone.md` (+ seeds a `characters/` file if named) |
| Real-world references | radio: Fully fictional / References real world | Fully fictional | `canon/tone.md` |
| Cast density | radio: tight core / balanced / sprawling ensemble | balanced | `settings.json: cast_density` |

**Settings / behavior (Advanced):**

| Field | Control / options | Default | Writes to |
|---|---|---|---|
| Content maturity | radio: cozy / PG-13 / gritty | PG-13 | `settings.json: content_maturity` |
| Secrets visibility | radio: Hidden / Shown | Hidden | `settings.json: secrets_visibility` |
| Level-up storylines | radio: On / Off | On | `settings.json: levelup_storylines` |
| Storyteller proactivity | radio: On-request only / Proactive (active events) | On-request only | `settings.json` → active-events |
| Git versioning (#26) | radio: Off / On | Off | `settings.json: git_versioning` (+ git plumbing) |

**Integrations (Advanced) — placeholder:**

| Field | Control / options | Default | Writes to |
|---|---|---|---|
| Mod integrations | **multi-checkbox** of supported + detected peer mods | all detected on | `settings.json: integrations[]` |

**Per-field notes:**

- **Narrative focus** — **two independent lenses, not an either/or dial**, because a full city story runs both at once. *Citizens & families* leans on `citizens_sample`/demographics/household texture and invents `characters/`; *Civic & political* leans on companies/budget/service-coverage/factions and invents `companies/` + `factions/`. Both default on (the rich default); the player can uncheck one to narrow the story, but **at least one must stay checked** (a story can't focus on nothing — if neither, treat as both). This is the one focus that *affects play, not just prose*: it biases the narratively-motivated objectives `/session-start` proposes (citizens → human-scale, neighborhood building goals tied to named residents; civic → systems/economy/politics goals) and how the diff attributes new construction (to people vs. institutions). With both on, the storyteller proposes and attributes across both registers. Story-shaping → lives in `canon/tone.md` (as a set of active lenses).

- **Content maturity** is a **disclosure setting, not a story bound.** It does not change what canon gets generated or how dark secrets/events are — identical at every setting. It only governs how explicitly the storyteller **divulges detail to the player when narrating** (cozy glosses over graphic/adult detail; gritty narrates in full). Player-facing presentation preference; changeable any time without altering canon.

- **Player's place in the fiction** — "Named character" reveals a conditional name field (default: a suggested founder/mayor name). When set, the agent seeds a `characters/` entry for the player and the storyteller may address/reference them; "Unseen chronicler" keeps the player outside the fiction. Hard to retcon later, which is why it's a founding question.

- **Storyteller proactivity** — "Proactive" turns on the periodic active-events loop (and a default cadence) from session 1; "On-request only" keeps the storyteller quiet until asked. **Wiring note:** active-events is currently a *global* mod setting (`Settings.ActiveEventsEnabled`, `Settings.cs`), not per-city. To make this a true per-city founding choice, either (a) have the wizard set the global toggle, or (b) migrate active-events to a per-city `settings.json` field. Recommend (b) — flag as a dependency for the phase that ships this field.

- **Git versioning (#26)** — the wizard only records the *preference* here (`git_versioning: true|false`). The actual repo-init + auto-commit-at-boundaries plumbing is issue **#26**'s scope; this field is the natural opt-in surface for it and should land with (or after) #26, not before. Until #26 ships, treat the field as inert/hidden.

- **Mod integrations** — a multi-checkbox list letting the player pick which peer-mod integrations to enable for this city, written to `settings.json: integrations[]`. **Wired:** **Elections** (#43), **InfoLoom** (#31), and **Custom Chirps** (#19) — each rendered as a real default-on checkbox when detected as loaded, gated on its availability binding (`electionsAvailable` / `infoloomAvailable` / `customChirpsAvailable`). Design rules:
  - **Only show a checkbox when the integration is both (a) supported in Ghostwriter and (b) detected as loaded** in the current game (reflective capability-probe, same pattern as `CartoBridge`). An integration that isn't installed simply doesn't appear — no dead checkboxes.
  - Each integration is **gated on its own implementation issue** (like git/#26): the checkbox is inert/hidden until that integration ships.
  - **Carto is not in this list** — it's the spatial backbone, always on, not optional.
  - Ties into **#39** (`mods.loaded[]` in snapshots + `mod-effects.md` registry): the detection list that drives these checkboxes is the same `mods.loaded[]` probe, and enabling one should make the storyteller aware of that mod's gameplay effects via the registry.
  - Default: **all detected, supported integrations on** (the player opts *out*, not in — if they installed the peer mod, they probably want it reflected in the story).

**Not a question — derived:**
- **Story era** is **inferred from the in-game date**, not asked. During founding the agent reads the in-world date from the latest snapshot and writes `canon/era.md` to match (e.g. the sim's current year → contemporary / mid-century / etc.). Keeps the era grounded in what the playthrough actually is, with no extra click.

> **Dependency to verify:** confirm the snapshot's `map.*` / time block already carries the in-game date/year (`Game.Common.TimeData`). If it isn't exported yet, exporting it is a prerequisite for era-derivation — flag as a small `ExportSystem` add in phase 1. The player can still edit `canon/era.md` afterward if the derived era isn't what they want.

Dropped from consideration: naming style (region already implies cultural naming flavor — revisit later as a chat canon edit, not a founding question).

---

## 8. State machine, triggers & guards

States: `Idle → Signaled → (PrereqProvider | PrereqSpatial) → ConfigForm → Generating → Result`, with `Dismissed` reachable from any signaled/active pre-generation state.

| Concern | Handling |
|---|---|
| **Auto-run** | Never. The single generation call fires only when the player clicks **Found my city** with prerequisites green. |
| **Dismiss** | "Later" sets a session-scoped `_quickstartDismissed` flag → clears flash/banner for this load. Reappears on the next save-load edge of a still-un-bootstrapped city. Not naggy within a session. |
| **Mid-generation reload** | If the player reloads while the single run is in flight, nothing is committed until the canon write completes, so on reload the city is still un-bootstrapped → flash/banner reappear → they re-open the form. Re-running is idempotent (spatial reads + fresh write). No partial-resume needed. |
| **Re-found an existing city** | `quickstartAvailable` is false once `bootstrapped: true`. Player can still run `/new-city` from chat (prose mode), which warns before overwriting populated canon (existing `new-city.md` step 1). |
| **No Carto peer mod** | Spatial-prereq gate explains reduced grounding; proceed using snapshot `map.*` only (mirrors `new-city.md` step 3 fallback). |
| **Provider changes mid-flow** | `setupNeeded` re-evaluated each `OnUpdate`; if it flips true before generation, surface the provider gate. |
| **City-name / save-name coupling** | Unchanged: the player must rename the CS2 save to the chosen city name so exports land in the right slug folder (`new-city.md` step 9). The result card surfaces this reminder prominently — the one manual step the wizard can't do for them. |

---

## 9. File-by-file change list

### Mod (C#)

| File | Change |
|---|---|
| `Storyteller/ToolSchemas.cs` | Add `wizard_done` schema to `_defaults`. |
| `Storyteller/ToolExecutor.cs` | Handle `wizard_done` in `Execute`; stash payload on a thread-safe field. |
| `Systems/PromptUISystem.cs` | New bindings: `quickstartAvailable`, `wizardDone` (value); `startQuickstart`, `dismissQuickstart`, `foundCity` (triggers). Fresh-city detection on save-load edge → set `quickstartAvailable`. `OnFoundCity(configJson)` builds the `<<QUICKSTART_CONFIG>>` block and submits `/new-city` through the existing submit path. Drain `_pendingWizardDone` in `OnUpdate`. Compute the C# region default. |
| `Systems/ExportSystem.cs` | Flash-binding hook point already exists (save-load edge calls into `_promptUI`; fresh-city + auto-Carto latch at `ExportSystem.cs:950,985-989`). **Verify the snapshot already exports the in-game date/year (`Game.Common.TimeData`); if not, add it** — era-derivation (§7.1) reads it. |
| `Settings.cs` | No new mod-wide setting (wizard state is per-city in `settings.json`). |

### UI (React / SCSS)

| File | Change |
|---|---|
| `UI/src/mods/storyteller/bindings.ts` | Add `quickstartAvailableBinding`, `wizardDoneBinding` (value) + `startQuickstart`, `dismissQuickstart`, `foundCity` (trigger) fns. |
| `UI/src/mods/storyteller/QuickstartWizard.tsx` | **New.** Prereq gates → config form (region dropdown, name + suggest checkbox, toggle radios) → generating state → result card. |
| `UI/src/mods/storyteller/StorytellerToolbar.tsx` | Read new bindings; warm gold/amber flash class on the icon; quickstart banner in the banner slot; mount `<QuickstartWizard>` when open. |
| `UI/src/mods/storyteller/storyteller.module.scss` | `.toolbarIconQuickstart` + `@keyframes ghostQuickstartPulse`; config-form / modal styles. |

### Template (agent-side)

| File | Change |
|---|---|
| `template/.claude/commands/new-city.md` | Add the `<<QUICKSTART_CONFIG>>` branch: honour the config (region, name, tone, content_maturity, toggles), derive era from the in-game date, generate the full founding in one non-interactive pass, write `canon/{city,tone,era,playthrough-premise}.md` + `settings.json` (`content_maturity` here), call `wizard_done`. Prose path adds region, tone, and maturity questions. |
| `template/canon/city.md` | Constrain `region:` to the documented enum (comment + validation note). |
| `template/canon/tone.md`, `template/canon/era.md` | tone.md carries the story-shaping fields (narrative tone, focus, player's-place, real-world-refs); era.md carries the derived era. Content-maturity does **not** go here — it's a `settings.json` disclosure field. |
| `template/settings.sample.json` | Add `cast_density` (`balanced`), `content_maturity` (`pg-13`), `git_versioning` (`false`), and proactivity/active-events fields alongside `secrets_visibility` / `levelup_storylines`. |
| `template/CLAUDE.md` | Document the quickstart config protocol + the `wizard_done` tool; note `region:` is an authoritative enum constraint, era is derived from the in-game date, **`content_maturity` gates only narration explicitness — never canon generation**, **narrative focus biases proposed gameplay objectives**, and how `player_role: character` seeds a `characters/` entry. |
| `template/.claude/commands/session-start.md`, `story-driven.md` | Read `region:` as a hard constraint (naming pools, cultural grounding). |

---

## 10. Phasing

1. **Region enum + era-derivation + prose ordering (#35 core).** Add the `region:` enum, the region question to `new-city.md` prose mode, downstream reads, and the C# region-default helper. Verify/add the in-game date to the snapshot and wire era-derivation into `canon/era.md`. Shippable alone; no wizard UI. Validates the inference default against real maps.
2. **Config protocol.** `wizard_done` schema/executor + `wizardDone` binding; `new-city.md` `<<QUICKSTART_CONFIG>>` branch. Testable by submitting `/new-city` with a hand-written config block and watching it generate non-interactively + the binding payload.
3. **Native form + result.** `QuickstartWizard.tsx` config form, `foundCity` trigger, result card. Full one-click founding.
4. **Signal layer.** Fresh-city C# binding, warm gold/amber flash, banner, dismiss guard.
5. **Story Settings editor (in v1, §11).** Reuse the form in edit mode — per-city `settings.json` writer, gear button / `/settings` entry, read-current-state, Save (no generation), and the storyteller-reconcile turn for canon-field changes.

Each phase is independently testable in-game.

---

## 11. Changing founding choices after the fact

Founding choices are not one-way. Every field set in the wizard can be changed later, and where it's changed depends on which of the two homes the value lives in:

- **`settings.json` (behavior / disclosure):** content maturity, secrets visibility, level-up storylines, cast density, storyteller proactivity, git versioning, integrations. These are preferences — freely changeable any time, take effect on the next snapshot/scan, **no LLM call**.
- **`canon/*.md` (story-shaping):** region (`city.md`), narrative tone / focus / player's-place / real-world-refs (`tone.md`), era (`era.md`). Changing these has narrative consequences, so they're changeable but **storyteller-aware** — the city is not re-founded (`bootstrapped` stays true) and existing canon is **not** retconned; the storyteller reconciles *forward*.

### Three surfaces

1. **Story Settings editor — recommended primary surface.** The wizard's config form (§7), reused in a post-bootstrap **edit mode**: opened from a gear affordance in the Ghostwriter panel (or a `/settings` command), pre-populated from the current `settings.json` + canon frontmatter, with **Save** as the primary action instead of "Found my city" and **no generation call**.
   - `settings.json` fields are written directly on Save — instant.
   - canon story-shaping fields: on Save the frontmatter is written directly, and a short storyteller turn is queued to *acknowledge and adjust going forward* (it does not rewrite history). The editor's copy flags region/tone changes as significant.
   - This reuses the exact component already built — the difference is the data source (read current state vs. defaults), the button label, and skipping the one-shot generation.

2. **Chat — always available, no new code.** The natural-language path: "switch the tone to noir," "make secrets visible," "stop proposing events on your own," "lean more civic but keep the family thread." The storyteller edits the right file. Best for nuanced changes the form can't express.

3. **Direct file edit — always available.** It's the player's folder; editing `settings.json` or a canon file's frontmatter by hand always works. The mod re-reads on the next snapshot/scan.

### Distinctions worth preserving

- **Content maturity** is purely a disclosure preference — flip it any time, canon is untouched, the storyteller just narrates with more/less explicit detail (§7.1).
- **Region and tone** are foundational — the editor treats changing them as a deliberate act (confirm copy), and the storyteller adapts forward rather than re-founding.
- **Era** is derived from the in-game date **once at founding**; after that it's an ordinary canon field the player/storyteller owns (a manual edit sticks). It is not silently re-derived on later snapshots.

### Impl deltas (beyond the wizard itself)

- A per-city `settings.json` writer (the wizard already needs this for founding) — reused for Save.
- A way to open the form in edit mode: a gear button in the panel header and/or a `/settings` command.
- The storyteller-reconcile turn for canon-field changes. No new generation pipeline.

**Decision (locked): the Story Settings editor ships in v1.** The form-as-editor is part of the initial scope, not a fast follow — opened from a gear button in the panel header (and/or `/settings`), reading current state, Save-not-Found. Chat + direct-edit remain available as the other two surfaces, but the native editor is the primary, supported path from day one.

> **Shipped (0.5.1):** the gear-button **Story Settings editor** (`UI/src/mods/storyteller/StorySettingsModal.tsx`) covers the **`settings.json` preference fields** — content maturity, secrets visibility, level-up, cast density, proactivity, git, and **`integrations`** (the Elections toggle). It reads current state from the `storySettings` C# binding and Saves via the `saveSettings` trigger, which writes `settings.json` **directly, no LLM call** (`PromptUISystem.OnSaveSettings`). The **story-shaping `canon/*.md` fields (region, tone, focus, era) are intentionally not in this first editor** — changing them adapts the story forward, which stays a chat request (the agent edits the canon frontmatter and reconciles). Folding those into the same form with the queued storyteller-reconcile turn is the natural follow-up.

---

## 12. Open questions for William

**Locked:** UI = native config form; LLM strategy = one-shot generate; flash = warm gold/amber; region scope = metadata first.

**Also locked:**
- Reuse `new-city.md` (no separate `/quickstart`). The config block is field-level — prose asks for any field it doesn't supply, so chat-only `/new-city` always gets a complete founding (§4.1).
- **Blank name → suggest.** If the name field is empty, the config sends `name: (suggest)` and the agent picks one grounded in the spatial data. No separate "suggest" checkbox needed — blank *is* the suggest path.
- **Question set finalized (§7.1):** Core — region, name (blank=suggest), narrative tone, narrative focus. Advanced — player's place in the fiction, real-world references, cast density, content maturity, secrets visibility, level-up storylines, storyteller proactivity, git versioning. **Era is derived from the in-game date**, not asked. Naming style dropped.

**Still open / dependencies:**
1. **In-game date in the snapshot** — verify it's already exported (needed for era-derivation); if not, a small `ExportSystem` add lands in phase 1.
2. **Active-events is currently a global mod setting** (`Settings.ActiveEventsEnabled`), but "storyteller proactivity" wants to be a per-city founding choice. Recommend migrating active-events to a per-city `settings.json` field; flag for the phase that ships proactivity.
3. **Git versioning depends on issue #26.** The wizard records only the preference; repo-init + auto-commit plumbing is #26's scope. Keep the field hidden/inert until #26 lands.
4. **Mod integrations are wired.** Elections (#43), InfoLoom (#31), and Custom Chirps (#19) each render a real default-on checkbox, gated on a reflective availability probe (`electionsAvailable` / `infoloomAvailable` / `customChirpsAvailable`, mirroring the bridges' `IsAvailable`). A checkbox appears only when its mod is detected as loaded; the step renders empty when none are. The same toggles are editable post-founding in the Story Settings editor. Carto is always-on and not listed.

---

## 13. Testing

Per the repo's posture (`CLAUDE.md` — Unity-coupled code verified in-game, pure helpers in `CityStoryMod.Tests`):

- **Unit (net48):** `<<QUICKSTART_CONFIG>>` block builder (config → prompt string); `wizard_done` payload (de)serialization; region-enum validation; the C# region-default heuristic (latitude/theme → region) as a pure function. Extract these as helpers (per the `TextUtils` pattern).
- **In-game:** fresh save → warm flash + banner → provider gate → spatial gate → config form (verify region default) → "Found my city" → **one** generation run → canon written with correct `region:` + chosen/suggested name + toggles → `bootstrapped: true` → result card → flash clears → `/session-start` available. Plus: name-suggest vs. typed name, dismiss-and-reload, mid-generation reload, no-Carto fallback, CLI vs API provider.
