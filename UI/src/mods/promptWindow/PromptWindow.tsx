import { useState } from "react";
import { Button } from "cs2/ui";
import storytellerIcon from "../../assets/storyteller_icon.svg";
import styles from "./PromptWindow.module.scss";

// Storyteller toolbar entry. Built on CS2's vanilla `Button` with
// variant="floating" — this is the same component (and styling) used by
// Zoning Toolkit, Anarchy, etc., so the icon picks up the canonical blue
// circle / white glyph chrome automatically and stays in sync with any
// CS2 theme update.
//
// SVG icon is bundled by webpack's asset/resource rule and referenced as
// a coui:// URL at runtime. White strokes/fills on a transparent
// background let it sit naturally on the floating-button blue.
export function StorytellerToolbar() {
  const [open, setOpen] = useState(false);
  const [prompt, setPrompt] = useState("");
  const [status] = useState("Idle");

  const handleSubmit = () => {
    console.log("[CityStoryMod] prompt submitted:", prompt);
  };

  return (
    <>
      <Button
        variant="floating"
        onClick={() => setOpen((v) => !v)}
        aria-label="Storyteller"
      >
        <img src={storytellerIcon} className={styles.toolbarIcon} alt="" />
      </Button>

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
