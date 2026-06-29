import { useMemo, useRef, useState } from "react";
import { useValue } from "cs2/api";
import styles from "./storyteller.module.scss";
import { useDrag } from "./useDrag";
import {
  storySettingsBinding,
  electionsAvailableBinding,
  infoloomAvailableBinding,
  customChirpsAvailableBinding,
  saveSettings,
} from "./bindings";
import type { StorySettings } from "./bindings";
import { PillRow, pills, MATURITY, CAST } from "./QuickstartWizard";

// Story Settings editor — the post-founding surface for changing the per-city
// settings.json preference fields (docs/quickstart-wizard.md §11). Opened from
// the gear affordance in the panel header. Reuses the Quickstart wizard's
// controls and modal chrome, but: it reads CURRENT state from settings.json
// (the storySettings binding), its primary action is "Save", and Save writes
// settings.json DIRECTLY with no LLM call (these are pure preferences).
//
// Scope: the settings.json behavior/disclosure fields only — including the
// peer-mod integration toggles (Elections, InfoLoom, Custom Chirps). Story-
// shaping canon fields (tone, region, era) are NOT here: changing those adapts
// the story forward and is a chat request (see CLAUDE.md "Changing founding
// choices later"). The footer points the player there.

const DEFAULTS: StorySettings = {
  secrets_visibility: "hidden",
  levelup_storylines: true,
  cast_density: "balanced",
  content_maturity: "pg-13",
  storyteller_proactivity: "on-request",
  git_versioning: false,
  integrations: [],
};

export function StorySettingsModal({ onClose }: { onClose: () => void }) {
  const settingsJson = useValue(storySettingsBinding);
  const electionsAvailable = useValue(electionsAvailableBinding);
  const infoloomAvailable = useValue(infoloomAvailableBinding);
  const customChirpsAvailable = useValue(customChirpsAvailableBinding);

  // Parse the current settings once at mount. The modal is conditionally
  // mounted (StorytellerToolbar unmounts it on close), so this initializer
  // re-reads fresh state every time the player opens the editor.
  const initial = useMemo<StorySettings>(() => {
    try {
      return { ...DEFAULTS, ...(JSON.parse(settingsJson) as Partial<StorySettings>) };
    } catch {
      return DEFAULTS;
    }
    // Intentionally mount-only: re-syncing mid-edit would clobber the player's
    // in-progress choices. Save closes the modal, so there's no live re-read.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const [maturity, setMaturity] = useState(initial.content_maturity);
  const [secrets, setSecrets] = useState<"hidden" | "shown">(initial.secrets_visibility);
  const [levelup, setLevelup] = useState(initial.levelup_storylines);
  const [castDensity, setCastDensity] = useState(initial.cast_density);
  const [proactivity, setProactivity] =
    useState<"on-request" | "proactive">(initial.storyteller_proactivity);
  const [git, setGit] = useState(initial.git_versioning);
  // One state flag per wired integration, initialized from the current
  // settings.json. A flag keeps its initial value when its toggle isn't
  // rendered (mod not detected), so Save preserves an opt-in for a peer mod
  // that simply isn't loaded right now — same behavior the single Elections
  // toggle had before this was generalized.
  const [elections, setElections] = useState(initial.integrations.includes("elections"));
  const [infoloom, setInfoloom] = useState(initial.integrations.includes("infoloom"));
  const [customchirps, setCustomchirps] = useState(initial.integrations.includes("customchirps"));

  // The wired peer-mod integrations, rendered uniformly (mirrors the wizard's
  // WIRED_INTEGRATIONS). Each renders a real toggle only when its mod is
  // detected; an undetected one is simply absent but its flag still feeds Save,
  // so a stored opt-in survives a session where the mod isn't loaded. `hint`
  // explains what on/off does for that integration.
  const WIRED_INTEGRATIONS = [
    {
      id: "elections", label: "Elections", available: electionsAvailable,
      checked: elections, set: setElections,
      hint: "On weaves your mayoral races into the story; off keeps politics soft and inferred, as if Elections weren’t running.",
    },
    {
      id: "infoloom", label: "InfoLoom", available: infoloomAvailable,
      checked: infoloom, set: setInfoloom,
      hint: "On grounds trade and labor stories on InfoLoom’s real numbers; off falls back to inferring them from zone counts and demographics.",
    },
    {
      id: "customchirps", label: "Custom Chirps", available: customChirpsAvailable,
      checked: customchirps, set: setCustomchirps,
      hint: "On posts a short in-world chirp about each new event to the in-game Chirper feed; off keeps the story to this panel.",
    },
  ];
  const anyWiredAvailable = WIRED_INTEGRATIONS.some((m) => m.available);

  // Draggable floating window (same pattern as the wizard).
  const modalRef = useRef<HTMLDivElement | null>(null);
  const { pos, beginDrag } = useDrag();
  const onHeaderMouseDown = (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest(`.${styles.wizClose}`)) return;
    beginDrag(e, modalRef.current);
  };
  const modalStyle = pos
    ? { top: `${pos.y}px`, left: `${pos.x}px` }
    : { top: "120rem", left: "730rem" };

  const save = () => {
    // Carry forward any integrations the editor doesn't manage (future ids),
    // then add back each wired id whose flag is on. A wired id whose toggle
    // wasn't rendered (mod undetected) keeps its initial flag, so a stored
    // opt-in is preserved rather than silently dropped.
    const managed = new Set(WIRED_INTEGRATIONS.map((m) => m.id));
    const others = initial.integrations.filter((id) => !managed.has(id));
    const enabled = WIRED_INTEGRATIONS.filter((m) => m.checked).map((m) => m.id);
    const integrations = [...others, ...enabled];
    const payload: StorySettings = {
      secrets_visibility: secrets,
      levelup_storylines: levelup,
      cast_density: castDensity,
      content_maturity: maturity,
      storyteller_proactivity: proactivity,
      git_versioning: git,
      integrations,
    };
    saveSettings(JSON.stringify(payload));
    onClose();
  };

  return (
    <div className={styles.wizModal} style={modalStyle} ref={modalRef}>
      <div className={styles.wizHeader} onMouseDown={onHeaderMouseDown}>
        <span className={styles.wizTitle}>Story settings</span>
        <button type="button" className={styles.wizClose} onClick={onClose}>×</button>
      </div>

      <div className={styles.wizBody}>
        <div className={styles.wizField}>
          <label className={styles.wizLabel}>Content maturity</label>
          <PillRow
            value={maturity}
            options={pills(MATURITY)}
            onPick={(id) => setMaturity(id as "cozy" | "pg-13" | "gritty")}
          />
          <div className={styles.wizHint}>
            Affects how explicitly detail is divulged to you — not what story
            gets written.
          </div>
        </div>

        <div className={styles.wizField}>
          <label className={styles.wizLabel}>Secrets visibility</label>
          <PillRow
            value={secrets}
            options={[
              { id: "hidden", label: "Hidden" },
              { id: "shown", label: "Shown" },
            ]}
            onPick={(id) => setSecrets(id as "hidden" | "shown")}
          />
        </div>

        <div className={styles.wizField}>
          <label className={styles.wizLabel}>Level-up storylines</label>
          <PillRow
            value={levelup ? "on" : "off"}
            options={[
              { id: "on", label: "On" },
              { id: "off", label: "Off" },
            ]}
            onPick={(id) => setLevelup(id === "on")}
          />
        </div>

        <div className={styles.wizField}>
          <label className={styles.wizLabel}>Cast density</label>
          <PillRow
            value={castDensity}
            options={pills(CAST)}
            onPick={(id) => setCastDensity(id as "tight" | "balanced" | "sprawling")}
          />
        </div>

        <div className={styles.wizField}>
          <label className={styles.wizLabel}>Storyteller proactivity</label>
          <PillRow
            value={proactivity}
            options={[
              { id: "on-request", label: "On-request only" },
              { id: "proactive", label: "Proactive" },
            ]}
            onPick={(id) => setProactivity(id as "on-request" | "proactive")}
          />
        </div>

        <div className={styles.wizField}>
          <label className={styles.wizLabel}>Git versioning</label>
          <PillRow
            value={git ? "on" : "off"}
            options={[
              { id: "off", label: "Off" },
              { id: "on", label: "On" },
            ]}
            onPick={(id) => setGit(id === "on")}
          />
        </div>

        <div className={styles.wizSectionLabel}>Mod integrations</div>
        <div className={styles.wizField}>
          {anyWiredAvailable ? (
            WIRED_INTEGRATIONS.filter((m) => m.available).map((m) => (
              <div key={m.id}>
                <div className={styles.wizCheckRow}>
                  <button
                    type="button"
                    className={`${styles.wizCheck} ${m.checked ? styles.wizCheckOn : ""}`}
                    onClick={() => m.set((v) => !v)}
                  >
                    <span className={styles.wizCheckBox}>{m.checked ? "✓" : ""}</span>
                    {m.label}
                  </button>
                </div>
                <div className={styles.wizHint}>{m.hint}</div>
              </div>
            ))
          ) : (
            <div className={styles.wizHint}>
              No supported peer-mod integrations are detected right now.
            </div>
          )}
        </div>

        <div className={styles.wizHint}>
          Tone, region, and era shape the story itself — ask the ghostwriter in
          chat to change those (e.g. &ldquo;switch the tone to noir&rdquo;).
        </div>
      </div>

      <div className={styles.wizFooter}>
        <button type="button" className={styles.wizLater} onClick={onClose}>
          Cancel
        </button>
        <button type="button" className={styles.wizFound} onClick={save}>
          Save
        </button>
      </div>
    </div>
  );
}
