import { useState } from "react";
import styles from "./PromptWindow.module.scss";

// Top-left toolbar entry. Sits in the same row as Zoning Toolkit / other
// tool-icon mods (CS2's GameTopLeft append target). Click toggles the prompt
// panel as a sibling below the icon.
//
// No C# bindings yet — the Run button only console.logs. Wiring up the
// engine.trigger call to a Systems/PromptUISystem trigger binding lands in
// the next commit.
export function StorytellerToolbar() {
  const [open, setOpen] = useState(false);
  const [prompt, setPrompt] = useState("");
  const [status] = useState("Idle");

  const handleSubmit = () => {
    console.log("[CityStoryMod] prompt submitted:", prompt);
  };

  return (
    <>
      <button
        type="button"
        className={styles.toolbarIcon}
        title="Storyteller"
        aria-label="Storyteller"
        onClick={() => setOpen((v) => !v)}
      >
        {/* Inline SVG so we don't need an asset pipeline. A scroll/quill icon
            sized to match the other top-left toolbar entries. */}
        <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
          <path
            fill="currentColor"
            d="M5 3h11l3 3v15a0 0 0 0 1 0 0H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zm0 2v14h12V7h-3V5H5zm2 4h8v2H7V9zm0 4h8v2H7v-2zm0 4h5v2H7v-2z"
          />
        </svg>
      </button>

      {open && (
        <div className={styles.panel}>
          <div className={styles.header}>
            <span className={styles.title}>Storyteller</span>
            <button
              type="button"
              className={styles.close}
              onClick={() => setOpen(false)}
            >
              ×
            </button>
          </div>
          <textarea
            className={styles.prompt}
            placeholder="Type a prompt or /command…"
            value={prompt}
            onChange={(e) => setPrompt(e.target.value)}
            rows={4}
          />
          <div className={styles.footer}>
            <span className={styles.status}>{status}</span>
            <button
              type="button"
              className={styles.run}
              disabled={prompt.trim().length === 0}
              onClick={handleSubmit}
            >
              Run
            </button>
          </div>
        </div>
      )}
    </>
  );
}
