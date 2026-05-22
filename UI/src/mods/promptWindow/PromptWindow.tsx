import { useState } from "react";
import styles from "./PromptWindow.module.scss";

// MVP storyteller prompt window. Floats top-right when open; collapsed to a
// pill-shaped toggle when closed. No bindings to the C# side yet — the submit
// button just logs to the console so we can confirm the UI is wired up before
// adding the C# trigger binding in a follow-up commit.
export function PromptWindow() {
  const [open, setOpen] = useState(false);
  const [prompt, setPrompt] = useState("");
  const [status] = useState("Idle");

  if (!open) {
    return (
      <button
        type="button"
        className={styles.toggle}
        onClick={() => setOpen(true)}
      >
        Storyteller
      </button>
    );
  }

  const handleSubmit = () => {
    // Will be replaced with a C# trigger binding (engine.trigger from cs2/api).
    // For now log so we can confirm the UI is alive.
    console.log("[CityStoryMod] prompt submitted:", prompt);
  };

  return (
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
  );
}
