import { useEffect, useRef } from "react";
import styles from "./storyteller.module.scss";
import type { SlashCommand } from "./bindings";

// Custom dropdown — toggle button + absolute-positioned popup list. Each
// item shows the command name and its frontmatter description. Picking
// closes the menu and submits the command immediately (no Run click).
//
// Click-outside-to-close: document-level mousedown listener attaches
// only while the menu is open. We check whether the click target is
// inside the dropdown root before closing — clicks on the toggle bubble
// up here too, so this guard prevents the toggle from closing then
// re-opening on the same click.
export function CommandMenu({
  commands,
  open,
  disabled,
  onToggle,
  onPick,
}: {
  commands: SlashCommand[];
  open: boolean;
  disabled: boolean;
  onToggle: () => void;
  onPick: (name: string) => void;
}) {
  const rootRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const onDocMouseDown = (e: MouseEvent) => {
      if (rootRef.current && rootRef.current.contains(e.target as Node)) return;
      onToggle();
    };
    document.addEventListener("mousedown", onDocMouseDown);
    return () => document.removeEventListener("mousedown", onDocMouseDown);
  }, [open, onToggle]);

  if (commands.length === 0) return null;

  return (
    <div className={styles.commandMenuRoot} ref={rootRef}>
      <button
        type="button"
        className={styles.commandToggle}
        disabled={disabled}
        onClick={onToggle}
      >
        <span>Commands</span>
        {/* Inline SVG chevron — CS2's Coherent UI font doesn't ship the
            ▾ glyph (U+25BE), which renders as tofu / a tiny square. SVG
            guarantees the chevron actually paints. Stroke color is set
            explicitly because Coherent UI doesn't propagate `color` to
            SVG `currentColor` references in some builds. */}
        <svg
          viewBox="0 0 10 10"
          aria-hidden="true"
          className={styles.commandToggleChevron}
        >
          <path
            d="M2 4 L5 7 L8 4"
            stroke="#cfe5f5"
            strokeWidth="1.5"
            fill="none"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </button>
      {open && (
        <div className={styles.commandMenu}>
          {commands.map((c) => (
            <button
              key={c.name}
              type="button"
              className={styles.commandMenuItem}
              onClick={() => onPick(c.name)}
            >
              <span className={styles.commandMenuItemName}>/{c.name}</span>
              {c.description && (
                <span className={styles.commandMenuItemDesc}>{c.description}</span>
              )}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
