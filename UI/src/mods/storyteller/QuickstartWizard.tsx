import { useEffect, useMemo, useRef, useState } from "react";
import { useValue } from "cs2/api";
import styles from "./storyteller.module.scss";
import { useDrag } from "./useDrag";
import {
  setupNeededBinding,
  cartoExportingBinding,
  isRunningBinding,
  wizardDoneBinding,
  quickstartAvailableBinding,
  lastErrorBinding,
  foundCity,
  dismissQuickstart,
  submitPrompt,
} from "./bindings";
import type { WizardDone } from "./bindings";

// Quickstart founding wizard — a native config form that collects every
// founding choice with ZERO LLM calls, then fires a single one-shot
// generation when the player hits "Found my city" (see
// docs/quickstart-wizard.md §3, §7). The model is invoked exactly once, at
// the end — never between questions.
//
// Phase is driven entirely by bindings so the harness can preview each state
// from dev/fixtures/sample-city.ts:
//   wizardDone non-empty → result card
//   isRunning            → "founding your city…" progress
//   setupNeeded          → provider prerequisite gate
//   cartoExporting       → spatial-data prerequisite gate
//   otherwise            → the config form

const REGIONS = [
  "North America", "Europe", "Asia", "Latin America",
  "Africa", "Oceania", "Middle East",
];
const TONES = ["grounded-realist", "dramatic", "noir", "hopeful", "satirical"];
const MATURITY = ["cozy", "pg-13", "gritty"];
const CAST = ["tight", "balanced", "sprawling"];

// Placeholder integrations — none are wired yet. Each is gated on its own
// issue (#31 / #19 / #43) and only appears once both supported and detected.
// Rendered disabled here so the form shows where they'll live.
const PLANNED_INTEGRATIONS = [
  { id: "infoloom", label: "InfoLoom", issue: "#31" },
  { id: "custom-chirps", label: "Custom Chirps", issue: "#19" },
  { id: "elections", label: "Elections", issue: "#43" },
];

// A row of mutually-exclusive pill buttons standing in for a radio group.
// Native <input type=radio> styling is unreliable in Coherent, so we paint
// our own selectable pills.
function PillRow(props: {
  value: string;
  options: { id: string; label: string }[];
  onPick: (id: string) => void;
}) {
  return (
    <div className={styles.wizPillRow}>
      {props.options.map((o) => (
        <button
          key={o.id}
          type="button"
          className={`${styles.wizPill} ${props.value === o.id ? styles.wizPillOn : ""}`}
          onClick={() => props.onPick(o.id)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

const pills = (ids: string[]) => ids.map((id) => ({ id, label: id }));

// A click-to-open dropdown built from div/button only. We can't use a native
// <select>: Cohtml doesn't implement HTMLSelectElement.options, so React's
// controlled-<select> mount path throws on `node.options.length` and takes the
// whole UI down with it. This renders the chosen value as a button and expands
// an inline list of options below it on click.
function Dropdown(props: {
  value: string;
  options: string[];
  onPick: (value: string) => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <div className={styles.wizDropdown}>
      <button
        type="button"
        className={styles.wizDropdownHead}
        onClick={() => setOpen((v) => !v)}
      >
        <span>{props.value}</span>
        <span className={styles.wizDropdownCaret}>{open ? "▾" : "▸"}</span>
      </button>
      {open && (
        <div className={styles.wizDropdownList}>
          {props.options.map((o) => (
            <button
              key={o}
              type="button"
              className={`${styles.wizDropdownItem} ${o === props.value ? styles.wizDropdownItemOn : ""}`}
              onClick={() => { props.onPick(o); setOpen(false); }}
            >
              {o}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export function QuickstartWizard({ onClose }: { onClose: () => void }) {
  const setupNeeded = useValue(setupNeededBinding);
  const cartoExporting = useValue(cartoExportingBinding);
  const isRunning = useValue(isRunningBinding);
  const quickstartAvailable = useValue(quickstartAvailableBinding);
  const lastError = useValue(lastErrorBinding);
  const doneJson = useValue(wizardDoneBinding);
  const done = useMemo<WizardDone | null>(() => {
    if (!doneJson) return null;
    try { return JSON.parse(doneJson) as WizardDone; } catch { return null; }
  }, [doneJson]);

  // Founding lifecycle. The wizard_done tool gives us a rich summary, but the
  // Claude Code CLI provider can't call it (and the model may skip it on the
  // API path too). The provider-agnostic completion signal is
  // quickstartAvailable flipping false — C# drives that off settings.json's
  // bootstrapped:true once /new-city finishes. We track that the player
  // submitted and that a run actually started, so we only treat "no longer
  // available" as success after our own founding run, not on first open.
  const [submitted, setSubmitted] = useState(false);
  const sawRunning = useRef(false);
  useEffect(() => {
    if (isRunning) sawRunning.current = true;
  }, [isRunning]);
  const runFinished = submitted && sawRunning.current && !isRunning;
  // Success: a wizard_done summary arrived, OR founding completed (the city is
  // no longer flagged fresh). Failure: the run ended, the city is still fresh,
  // and an error surfaced.
  const succeeded = !!done || (submitted && !quickstartAvailable);
  const failed = runFinished && quickstartAvailable && !done && !!lastError;

  // Draggable floating window (same pattern as FileModal / the panel). Header
  // is the drag handle; initial position is roughly centered on the 1920×1080
  // game canvas and sits above the panel (z-index in SCSS).
  const modalRef = useRef<HTMLDivElement | null>(null);
  const { pos, beginDrag } = useDrag();
  const onHeaderMouseDown = (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest(`.${styles.wizClose}`)) return;
    beginDrag(e, modalRef.current);
  };
  const modalStyle = pos
    ? { top: `${pos.y}px`, left: `${pos.x}px` }
    : { top: "120rem", left: "730rem" };

  // -- Core --
  const [region, setRegion] = useState("North America");
  const [name, setName] = useState("");
  const [tone, setTone] = useState("grounded-realist");
  const [focus, setFocus] = useState({ citizens: true, civic: true });

  // -- Advanced --
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [playerRole, setPlayerRole] = useState<"chronicler" | "character">("chronicler");
  const [playerName, setPlayerName] = useState("");
  const [realWorldRefs, setRealWorldRefs] = useState<"fictional" | "real">("fictional");
  const [castDensity, setCastDensity] = useState("balanced");
  const [maturity, setMaturity] = useState("pg-13");
  const [secrets, setSecrets] = useState<"hidden" | "shown">("hidden");
  const [levelup, setLevelup] = useState(true);
  const [proactivity, setProactivity] = useState<"on-request" | "proactive">("on-request");
  const [git, setGit] = useState(false);

  // At least one focus lens must stay checked — a story can't focus on
  // nothing (spec §7.1). Unchecking the last one is a no-op.
  const toggleFocus = (key: "citizens" | "civic") => {
    setFocus((prev) => {
      const next = { ...prev, [key]: !prev[key] };
      if (!next.citizens && !next.civic) return prev;
      return next;
    });
  };

  const submit = () => {
    const config = {
      region,
      name: name.trim(), // blank → C# treats as (suggest)
      tone,
      focus: [focus.citizens ? "citizens" : null, focus.civic ? "civic" : null].filter(Boolean),
      player_role: playerRole,
      player_character_name: playerRole === "character" ? playerName.trim() : "",
      real_world_refs: realWorldRefs,
      cast_density: castDensity,
      content_maturity: maturity,
      secrets_visibility: secrets,
      levelup_storylines: levelup,
      storyteller_proactivity: proactivity,
      git_versioning: git,
      integrations: [], // placeholder — none supported yet
    };
    foundCity(JSON.stringify(config));
    setSubmitted(true);
  };

  const later = () => {
    dismissQuickstart();
    onClose();
  };

  const startSession = () => {
    submitPrompt("/session-start");
    onClose();
  };

  const retry = () => {
    setSubmitted(false);
    sawRunning.current = false;
  };

  const phase: "result" | "error" | "generating" | "provider" | "spatial" | "form" =
    succeeded ? "result"
    : failed ? "error"
    : (submitted || isRunning) ? "generating"
    : setupNeeded ? "provider"
    : cartoExporting ? "spatial"
    : "form";

  return (
    <div className={styles.wizModal} style={modalStyle} ref={modalRef}>
        <div className={styles.wizHeader} onMouseDown={onHeaderMouseDown}>
          <span className={styles.wizTitle}>Found your city</span>
          <button type="button" className={styles.wizClose} onClick={onClose}>×</button>
        </div>

        {phase === "provider" && (
          <div className={styles.wizGate}>
            <div className={styles.wizGateTitle}>Set up a language model first</div>
            <div className={styles.wizGateBody}>
              Open Options → Ghostwriter, choose a provider, and paste an API key
              (or set up the Claude Code CLI). The founding step needs a model to
              write your city&rsquo;s story.
            </div>
          </div>
        )}

        {phase === "spatial" && (
          <div className={styles.wizGate}>
            <span className={styles.wizSpinner} />
            <div className={styles.wizGateTitle}>Mapping your terrain…</div>
            <div className={styles.wizGateBody}>
              Reading the map&rsquo;s elevation, water, and roads. This takes a
              moment and only happens once.
            </div>
          </div>
        )}

        {phase === "generating" && (
          <div className={styles.wizGate}>
            <span className={styles.wizSpinner} />
            <div className={styles.wizGateTitle}>
              Founding {name.trim() || "your city"}…
            </div>
            <div className={styles.wizGateBody}>
              Reading the spatial data, inferring the era, and writing the
              founding history. One moment.
            </div>
          </div>
        )}

        {phase === "result" && (
          <div className={styles.wizResult}>
            {done ? (
              <>
                <div className={styles.wizResultName}>{done.city_name}</div>
                <div className={styles.wizResultMeta}>
                  {done.region}{done.founded ? ` · founded ${done.founded}` : ""}
                </div>
                <div className={styles.wizResultPremise}>{done.premise}</div>
                <div className={styles.wizReminder}>
                  Rename your CS2 save to <strong>{done.city_name}</strong> so future
                  exports land in the right folder.
                </div>
              </>
            ) : (
              <>
                {/* No wizard_done summary (CLI provider, or the model skipped
                    the tool) — the founding still completed (bootstrapped). Show
                    a generic result; the full story is in the chat / canon. */}
                <div className={styles.wizResultName}>
                  {name.trim() || "Your city"} is founded
                </div>
                <div className={styles.wizResultPremise}>
                  The founding history and premise are written. See the chat for
                  the details.
                </div>
                <div className={styles.wizReminder}>
                  Rename your CS2 save to your city&rsquo;s name so future exports
                  land in the right folder.
                </div>
              </>
            )}
            <div className={styles.wizFooter}>
              <button type="button" className={styles.wizFound} onClick={startSession}>
                Start session 1
              </button>
            </div>
          </div>
        )}

        {phase === "error" && (
          <div className={styles.wizResult}>
            <div className={styles.wizResultName}>Founding didn&rsquo;t finish</div>
            <div className={styles.wizResultPremise}>
              Something went wrong while founding the city
              {lastError ? `: ${lastError}` : "."}
            </div>
            <div className={styles.wizFooter}>
              <button type="button" className={styles.wizLater} onClick={onClose}>
                Close
              </button>
              <button type="button" className={styles.wizFound} onClick={retry}>
                Back to form
              </button>
            </div>
          </div>
        )}

        {phase === "form" && (
          <>
            <div className={styles.wizBody}>
              {/* -- Core -- */}
              <div className={styles.wizField}>
                <label className={styles.wizLabel}>Region</label>
                <Dropdown value={region} options={REGIONS} onPick={setRegion} />
              </div>

              <div className={styles.wizField}>
                <label className={styles.wizLabel}>City name</label>
                <input
                  className={styles.wizInput}
                  value={name}
                  placeholder="Leave blank to let the storyteller suggest one"
                  onChange={(e) => setName(e.target.value)}
                />
              </div>

              <div className={styles.wizField}>
                <label className={styles.wizLabel}>Narrative tone</label>
                <PillRow value={tone} options={pills(TONES)} onPick={setTone} />
              </div>

              <div className={styles.wizField}>
                <label className={styles.wizLabel}>Narrative focus</label>
                <div className={styles.wizCheckRow}>
                  <button
                    type="button"
                    className={`${styles.wizCheck} ${focus.citizens ? styles.wizCheckOn : ""}`}
                    onClick={() => toggleFocus("citizens")}
                  >
                    <span className={styles.wizCheckBox}>{focus.citizens ? "✓" : ""}</span>
                    Citizens &amp; families
                  </button>
                  <button
                    type="button"
                    className={`${styles.wizCheck} ${focus.civic ? styles.wizCheckOn : ""}`}
                    onClick={() => toggleFocus("civic")}
                  >
                    <span className={styles.wizCheckBox}>{focus.civic ? "✓" : ""}</span>
                    Civic &amp; political
                  </button>
                </div>
                <div className={styles.wizHint}>
                  Both on tells the richest story; uncheck one to narrow it.
                </div>
              </div>

              {/* -- Advanced -- */}
              <button
                type="button"
                className={styles.wizAdvancedToggle}
                onClick={() => setAdvancedOpen((v) => !v)}
              >
                {advancedOpen ? "▾" : "▸"} Advanced / optional
              </button>

              {advancedOpen && (
                <div className={styles.wizAdvanced}>
                  <div className={styles.wizSectionLabel}>Story</div>

                  <div className={styles.wizField}>
                    <label className={styles.wizLabel}>Your place in the fiction</label>
                    <PillRow
                      value={playerRole}
                      options={[
                        { id: "chronicler", label: "Unseen chronicler" },
                        { id: "character", label: "Named character" },
                      ]}
                      onPick={(id) => setPlayerRole(id as "chronicler" | "character")}
                    />
                    {playerRole === "character" && (
                      <input
                        className={styles.wizInput}
                        value={playerName}
                        placeholder="Your character's name (blank = suggested)"
                        onChange={(e) => setPlayerName(e.target.value)}
                      />
                    )}
                  </div>

                  <div className={styles.wizField}>
                    <label className={styles.wizLabel}>Real-world references</label>
                    <PillRow
                      value={realWorldRefs}
                      options={[
                        { id: "fictional", label: "Fully fictional" },
                        { id: "real", label: "References real world" },
                      ]}
                      onPick={(id) => setRealWorldRefs(id as "fictional" | "real")}
                    />
                  </div>

                  <div className={styles.wizField}>
                    <label className={styles.wizLabel}>Cast density</label>
                    <PillRow value={castDensity} options={pills(CAST)} onPick={setCastDensity} />
                  </div>

                  <div className={styles.wizSectionLabel}>Settings &amp; behavior</div>

                  <div className={styles.wizField}>
                    <label className={styles.wizLabel}>Content maturity</label>
                    <PillRow value={maturity} options={pills(MATURITY)} onPick={setMaturity} />
                    <div className={styles.wizHint}>
                      Affects how explicitly detail is divulged to you — not what
                      story gets written.
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
                    <div className={styles.wizHint}>
                      None available yet — these light up once detected and
                      supported.
                    </div>
                    <div className={styles.wizCheckRow}>
                      {PLANNED_INTEGRATIONS.map((m) => (
                        <button
                          key={m.id}
                          type="button"
                          className={`${styles.wizCheck} ${styles.wizCheckDisabled}`}
                          disabled
                        >
                          <span className={styles.wizCheckBox} />
                          {m.label} <span className={styles.wizPlanned}>{m.issue}</span>
                        </button>
                      ))}
                    </div>
                  </div>
                </div>
              )}
            </div>

            <div className={styles.wizFooter}>
              <button type="button" className={styles.wizLater} onClick={later}>
                Later
              </button>
              <button type="button" className={styles.wizFound} onClick={submit}>
                Found my city
              </button>
            </div>
          </>
        )}
    </div>
  );
}
